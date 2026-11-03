using System;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ZFrame.EditorTools.DevServer
{
    /// <summary>
    /// 生命周期入口。三条纪律：
    /// 1) domain reload 会重建整个 AppDomain（后台线程会被杀），所以静态构造 + afterAssemblyReload 负责重拉；
    /// 2) 任何启动失败只打日志，绝不抛出去让 Editor 崩；
    /// 3) 关闭（菜单/环境变量）后不留任何 hook、不留线程，Editor 行为与没有本服务时一致。
    /// </summary>
    [InitializeOnLoad]
    internal static class DevServerBootstrap
    {
        public const int DefaultPort = 17321;

        const string MenuRoot = "Tools/ZFrame/Dev Server";
        const string PrefEnabled = "ZFrame.DevServer.Enabled";
        const string SessionKeyDomainReloads = "ZFrame.DevServer.DomainReloads";
        const string EnvDisabled = "ZBF_DEV_SERVER";
        const string EnvPort = "ZBF_DEV_PORT";

        const int MaxRequestsPerTick = 16;

        static DevServerCore _server;
        static bool _updateHooked;

        public static int Port { get; private set; } = DefaultPort;
        public static double StartedAt { get; private set; }
        public static int PendingCount => _server != null ? _server.Pending : 0;
        public static bool IsRunning => _server != null;

        static DevServerBootstrap()
        {
            int reloads = SessionState.GetInt(SessionKeyDomainReloads, 0) + 1;
            SessionState.SetInt(SessionKeyDomainReloads, reloads);
            DevState.DomainReloadCount = reloads;

            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.quitting += OnEditorQuitting;

            TryStart("domain load #" + reloads);
        }

        // ---------------------------------------------------------------- 生命周期

        static void OnBeforeAssemblyReload()
        {
            // 必须在 reload 前主动关掉监听：否则旧的 socket/线程会被连带干掉，端口可能留在 TIME_WAIT 里。
            Stop("assembly reload");
        }

        static void OnAfterAssemblyReload()
        {
            // 静态构造通常已经拉起来了，这里兜底（例如进入/退出 Play Mode 的 reload 顺序差异）。
            if (_server == null) TryStart("after assembly reload");
        }

        static void OnEditorQuitting()
        {
            Stop("editor quitting");
        }

        static void TryStart(string reason)
        {
            if (Application.isBatchMode)
            {
                // 批处理/CI 一律不监听端口：run_tests.ps1 与 CI 行为零变化。
                return;
            }

            if (IsDisabledByEnvironment())
            {
                Log("disabled by env " + EnvDisabled);
                return;
            }

            // TASK-A-AUDIT-02 A-1：默认仍然是"开"，但**默认的姿态不再是裸奔**——Host/Origin 闸门
            // （DevGuard）+ /menu 白名单（DevRouter）在同一个进程里无条件生效，浏览器页面既驱动不了
            // /menu 也读不到 /console 的正文。保留默认开启的理由：dev.ps1、六段脚本和各人的调试习惯
            // 都默认有这个端口，改成默认关闭等于让"装了工程就有服务"变成"先找菜单打开"，而且
            // 关闭后**没有**任何提示会让故障看起来像坏了（简报 A-1 第 5 条允许这一取舍）。
            // 想彻底关掉：EditorPrefs / 菜单 Tools/ZFrame/Dev Server/Enabled 或环境变量 ZBF_DEV_SERVER=0。
            if (!EditorPrefs.GetBool(PrefEnabled, true))
            {
                Log("disabled by preference " + PrefEnabled);
                return;
            }
            Stop("restart before start");

            Port = ResolvePort();
            var server = new DevServerCore(Port);
            string error;
            if (!server.TryStart(out error))
            {
                // 端口被占（例如另一个 Editor 实例）也必须让 Editor 照常工作。
                Debug.LogWarning("[DevServer] 启动失败 127.0.0.1:" + Port + " -> " + error + "；服务未启用，Editor 不受影响。");
                return;
            }

            _server = server;
            StartedAt = EditorApplication.timeSinceStartup;
            // 立刻抓一份快照：否则 reload 后到第一个 update tick 之间，/health 只能报 {ok,snapshotAgeMs:-1}。
            DevState.Refresh(EditorApplication.timeSinceStartup);
            DevConsoleRing.Install();
            // 进/出 Play Mode 会重建 AppDomain：TestRunnerApi 的 ICallbacks 只在当前域里有效，
            // 不在这里重挂，playmode 的 RunFinished 就永远打不到我们身上（结果永远拿不到）。
            DevTestRuns.OnDomainLoad(DevState.DomainReloadCount);

            if (!_updateHooked)
            {
                EditorApplication.update += Pump;
                _updateHooked = true;
            }

            Log("listening on http://127.0.0.1:" + Port + " (" + reason +
                "; guard: loopback Host/Origin only, /menu allowlist）");
        }

        static void Stop(string reason)
        {
            var server = _server;
            _server = null;
            if (server != null)
            {
                server.Stop();
                Log("stopped (" + reason + ")");
            }

            // 关闭时把 console hook 也摘掉：禁用后编辑器里不留任何本服务的痕迹。
            DevConsoleRing.Uninstall();

            if (_updateHooked)
            {
                EditorApplication.update -= Pump;
                _updateHooked = false;
            }
        }

        // ---------------------------------------------------------------- 主线程泵

        static void Pump()
        {
            DevState.Refresh(EditorApplication.timeSinceStartup);

            DevServerCore server = _server;
            if (server == null) return;

            DrainServerLogs(server);

            long started = Stopwatch.GetTimestamp();
            bool handledAny = false;

            for (int i = 0; i < MaxRequestsPerTick; i++)
            {
                DevHttpRequest request;
                if (!server.TryDequeue(out request)) break;
                handledAny = true;
                Dispatch(request);
            }

            if (!handledAny)
            {
                double microseconds = (Stopwatch.GetTimestamp() - started) * 1000000.0 / Stopwatch.Frequency;
                DevState.RecordIdlePump(microseconds);
            }
        }

        /// <summary>
        /// 把后台线程攒下的拒绝日志泵出来（A-1 要求"其他 Origin → 403 并记日志"，A-2 要求超限可见）。
        /// 后台线程自己一行 Unity API 都不碰，这里是唯一的落地点；每 tick 上限 8 条防刷屏。
        /// </summary>
        static void DrainServerLogs(DevServerCore server)
        {
            for (int i = 0; i < 8; i++)
            {
                string message;
                if (!server.TryDequeueLog(out message)) return;
                Debug.LogWarning("[DevServer] " + message);
            }
        }

        static void Dispatch(DevHttpRequest request)
        {
            bool claimed;
            try
            {
                claimed = DevRouter.Handle(request);
            }
            catch (Exception e)
            {
                // 任何 handler 异常都转成结构化错误，绝不逃逸到 Editor 的 update 循环。
                request.Complete(DevResponse.Error(e.GetType().Name + ": " + e.Message, 500));
                return;
            }

            if (!claimed)
            {
                request.Complete(DevResponse.Error("no route for " + request.Method + " " + request.Path, 404));
                return;
            }

            if (!request.Completed && !request.Deferred)
            {
                request.Complete(DevResponse.Error("handler neither completed nor deferred " + request.Path, 500));
            }
        }

        // ---------------------------------------------------------------- 配置

        static bool IsDisabledByEnvironment()
        {
            string value = Environment.GetEnvironmentVariable(EnvDisabled);
            if (string.IsNullOrEmpty(value)) return false;
            value = value.Trim();
            return value == "0"
                || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                || value.Equals("off", StringComparison.OrdinalIgnoreCase)
                || value.Equals("no", StringComparison.OrdinalIgnoreCase);
        }

        static int ResolvePort()
        {
            string value = Environment.GetEnvironmentVariable(EnvPort);
            int port;
            if (!string.IsNullOrEmpty(value) && int.TryParse(value.Trim(), out port) && port > 0 && port < 65536)
            {
                return port;
            }

            return DefaultPort;
        }

        // ---------------------------------------------------------------- 菜单

        [MenuItem(MenuRoot + "/Enabled", false, 900)]
        static void ToggleEnabled()
        {
            bool enabled = EditorPrefs.GetBool(PrefEnabled, true);
            EditorPrefs.SetBool(PrefEnabled, !enabled);

            if (EditorPrefs.GetBool(PrefEnabled, true))
            {
                TryStart("menu enable");
            }
            else
            {
                Stop("menu disable");
            }
        }

        [MenuItem(MenuRoot + "/Enabled", true)]
        static bool ToggleEnabledValidate()
        {
            Menu.SetChecked(MenuRoot + "/Enabled", EditorPrefs.GetBool(PrefEnabled, true));
            return true;
        }

        [MenuItem(MenuRoot + "/Restart", false, 901)]
        static void RestartFromMenu()
        {
            if (IsDisabledByEnvironment())
            {
                Log("重启被忽略：环境变量 " + EnvDisabled + " 已禁用服务");
                return;
            }

            EditorPrefs.SetBool(PrefEnabled, true);
            TryStart("menu restart");
        }

        [MenuItem(MenuRoot + "/Copy health URL", false, 902)]
        static void CopyHealthUrl()
        {
            EditorGUIUtility.systemCopyBuffer = "http://127.0.0.1:" + Port + "/health";
            Log("health URL 已复制（端口 " + Port + (IsRunning ? "，服务运行中" : "，服务未运行") + "）");
        }

        [MenuItem(MenuRoot + "/Log status", false, 903)]
        static void LogStatus()
        {
            Log("running=" + IsRunning + " port=" + Port + " pending=" + PendingCount + " domainReloads=" + DevState.DomainReloadCount);
        }

        static void Log(string message)
        {
            Debug.Log("[DevServer] " + message);
        }
    }
}
