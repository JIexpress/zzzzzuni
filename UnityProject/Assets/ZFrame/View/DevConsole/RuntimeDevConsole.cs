using System;
using System.Diagnostics;
using System.Net;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ZFrame.View.DevConsole
{
    /// <summary>
    /// 运行期 DevConsole 的生命周期与主线程泵。
    ///
    /// 三层开口第 B 层：真机/Player 里没有 Inspector、Console、Play 按钮，
    /// 这里给开发包一个只绑 127.0.0.1 的白名单命令台（一行请求 / 一行 JSON 响应）。
    ///
    /// 纪律（与 A 层 `Editor/DevServer` 同一套血泪）：
    /// 1) 后台线程只收发 socket；命令入队，<see cref="Pump"/> 在主线程执行；
    /// 2) Unity API 零后台调用（连日志也在主线程统一打）；
    /// 3) 监听线程 IsBackground=true，OnApplicationQuit 优雅关闭；
    /// 4) 端口占用/socket/命令异常一律吞掉，只记一次日志，绝不让游戏崩。
    ///
    /// 门控：默认只在 <see cref="Debug.isDebugBuild"/> 启用，见 <see cref="RuntimeDevConsoleGate"/>。
    /// </summary>
    public static class RuntimeDevConsole
    {
        private const int MaxRequestsPerTick = 8;

        private static RuntimeDevConsoleServer _server;
        private static GameObject _hostObject;
        private static bool _stopping;
        private static bool _portFailureLogged;
        private static readonly System.Collections.Generic.HashSet<string> LoggedMessages =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        private static double _idleLastMicros;
        private static double _idleAverageMicros;
        private static long _commandsServed;
        private static float _startedAt;

        public static bool IsRunning => _server != null;

        public static int Port { get; private set; }

        /// <summary>监听端点（用于"只绑回环"的机检断言）。</summary>
        public static IPEndPoint BoundEndPoint => _server != null ? _server.LocalEndPoint : null;

        /// <summary>存活的监听线程数（正常为 0 或 1；门控关闭时必须是 0）。</summary>
        public static int ListenThreads => RuntimeDevConsoleServer.LiveAcceptThreads;

#if UNITY_EDITOR || UNITY_INCLUDE_TESTS || DEVELOPMENT_BUILD
        /// <summary>H2 并发上限的机检判据：当前被服务端接住的客户端数（发布包里不存在）。</summary>
        internal static int ActiveClientsForTests => _server != null ? _server.ActiveClients : 0;
#endif

        public static double LastIdlePumpMicros => _idleLastMicros;

        public static double AverageIdlePumpMicros => _idleAverageMicros;

        public static long CommandsServed => System.Threading.Interlocked.Read(ref _commandsServed);

        public static float UptimeSeconds => _startedAt > 0f ? Time.realtimeSinceStartup - _startedAt : 0f;

        // ---------------------------------------------------------------- 生命周期

        /// <summary>
        /// 游戏启动时自动拉起。**非开发包在这里就被门控挡住**：不建线程、不开 socket、不建隐藏对象。
        /// 批处理（CI / PlayMode 测试）不自动监听：端口留给测试显式驱动，CI 行为零变化。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (Application.isBatchMode) return;
            if (!RuntimeDevConsoleGate.IsEnabled()) return;

            int port = RuntimeDevConsoleGate.ResolvePort();
            string error;
            if (!StartListener(port, out error))
            {
                LogOnce("start failed 127.0.0.1:" + port + " -> " + error + "；游戏不受影响");
            }
        }

        /// <summary>
        /// 生产启动入口：**门控在内部，调用方拿不到绕过**。判据恒为 <see cref="Debug.isDebugBuild"/>
        /// 加上命令行/环境变量的显式关闭（见 <see cref="RuntimeDevConsoleGate.IsEnabled()"/>）。
        /// </summary>
        public static bool TryEnable(int port, out string error)
        {
            if (!RuntimeDevConsoleGate.IsEnabled())
            {
                error = "disabled by gate (development build required, or explicitly disabled)";
                return false;
            }

            return StartListener(port, out error);
        }

#if UNITY_EDITOR || UNITY_INCLUDE_TESTS || DEVELOPMENT_BUILD
        /// <summary>
        /// 测试/编辑器专用：注入"是否开发包"判据。**编译期就只存在于 Editor / 测试程序集 /
        /// 开发构建**——非开发包（发布包）的 <c>ZFrame.View.dll</c> 里不存在这个符号
        /// （TASK-A-DEV-04 H1，证据见 Docs/Logs/TASK-A-DEV-04.md）。
        /// 开发构建里本来就默认开启控制台，这个 internal 入口不增加任何攻击面。
        /// </summary>
        internal static bool TryEnableForTests(Func<bool> isDevelopmentBuild, int port, out string error)
        {
            if (!RuntimeDevConsoleGate.IsEnabled(isDevelopmentBuild, Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable))
            {
                error = "disabled by gate (development build required, or explicitly disabled)";
                return false;
            }

            return StartListener(port, out error);
        }
#endif

        /// <summary>
        /// 真正开监听（**私有**：TASK-A-DEV-04 H1 把原来的 public <c>TryStart</c> 收掉，
        /// 避免发布包里存在"无门控启动"的入口）。端口/异常错误照旧吞掉并只记一次日志。
        /// </summary>
        private static bool StartListener(int port, out string error)
        {
            error = null;
            if (_server != null)
            {
                error = "already running on 127.0.0.1:" + Port;
                return false;
            }

            if (port <= 0 || port > 65535)
            {
                error = "invalid port " + port;
                return false;
            }

            var server = new RuntimeDevConsoleServer(port);
            if (!server.TryBind(out error))
            {
                if (!_portFailureLogged)
                {
                    _portFailureLogged = true;
                    Debug.LogWarning("[ZBF-RT] DevConsole 启动失败 127.0.0.1:" + port + " -> " + error
                        + "；服务未启用，游戏照常运行。");
                }

                return false;
            }

            _server = server;
            Port = port;
            _startedAt = Time.realtimeSinceStartup;
            _idleLastMicros = 0.0;
            _idleAverageMicros = 0.0;

            CreateHost();
            Debug.Log("[ZBF-RT] DevConsole listening on 127.0.0.1:" + port
                + " (development build, whitelist: " + string.Join(",", RuntimeDevConsoleCommands.CommandNames) + ")");
            return true;
        }

        public static void Stop()
        {
            if (_stopping) return;
            _stopping = true;

            try
            {
                RuntimeDevConsoleServer server = _server;
                _server = null;
                Port = 0;

                if (server != null)
                {
                    server.Stop();
                    Debug.Log("[ZBF-RT] DevConsole stopped");
                }

                if (_hostObject != null)
                {
                    UnityEngine.Object.Destroy(_hostObject);
                }
            }
            catch (Exception e)
            {
                LogOnce("stop failed: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                _hostObject = null;
                _stopping = false;
            }
        }

        private static void CreateHost()
        {
            if (_hostObject != null) return;

            _hostObject = new GameObject("~ZFrame.RuntimeDevConsole");
            _hostObject.hideFlags = HideFlags.HideInHierarchy;
            UnityEngine.Object.DontDestroyOnLoad(_hostObject);
            _hostObject.AddComponent<RuntimeDevConsoleHost>();
        }

        internal static bool OwnsHost(RuntimeDevConsoleHost host)
        {
            return host != null && _hostObject != null && host.gameObject == _hostObject;
        }

        // ---------------------------------------------------------------- 主线程泵

        /// <summary>每帧由隐藏对象的 Update 调用：先吐后台日志，再最多执行 8 条命令。</summary>
        internal static void Pump()
        {
            Pump(_server);
        }

        /// <summary>
        /// 泵的核心：对给定 server 吐日志 + 最多执行 8 条命令。生产路径只泵 <see cref="_server"/>；
        /// 参数化是为了让 PlayMode 用例驱动一个独立的、注入了短超时的实例（TASK-A-AUDIT-03 B-5）。
        /// </summary>
        internal static void Pump(RuntimeDevConsoleServer server)
        {
            if (server == null) return;

            string message;
            for (int i = 0; i < 16 && server.TryDequeueLog(out message); i++)
            {
                LogOnce(message);
            }

            long started = Stopwatch.GetTimestamp();
            int handled = 0;

            for (int i = 0; i < MaxRequestsPerTick; i++)
            {
                RuntimeDevCommand command;
                if (!server.TryDequeue(out command)) break;

                // TASK-A-AUDIT-03 (B-5): never run a command the caller has already been told
                // failed — neither one whose timeout expired nor one the client thread marked
                // abandoned. Executing it now would make "failed but effective" real, and a
                // retry would run the side effect twice.
                if (command.IsExpired || !command.TryBeginExecute())
                {
                    command.Complete(RuntimeDevConsoleCommands.Error(
                        "command not executed: its client already received a timeout response"));
                    LogOnce("command '" + Shorten(command.Line) + "' dropped: expired/abandoned before execution");
                    continue;
                }

                handled++;

                string response;
                try
                {
                    response = RuntimeDevConsoleCommands.Execute(command.Line);
                }
                catch (Exception e)
                {
                    // 命令异常绝不逃逸到游戏循环。
                    response = RuntimeDevConsoleCommands.Error(e.GetType().Name + ": " + e.Message);
                    LogOnce("command '" + Shorten(command.Line) + "' threw " + e.GetType().Name + ": " + e.Message);
                }

                command.Complete(response);
                System.Threading.Interlocked.Increment(ref _commandsServed);
            }

            if (handled == 0)
            {
                double microseconds = (Stopwatch.GetTimestamp() - started) * 1000000.0 / Stopwatch.Frequency;
                _idleLastMicros = microseconds;
                _idleAverageMicros = _idleAverageMicros <= 0.0
                    ? microseconds
                    : _idleAverageMicros * 0.95 + microseconds * 0.05;
            }
        }

        private static string Shorten(string line)
        {
            if (string.IsNullOrEmpty(line)) return string.Empty;
            return line.Length <= 40 ? line : line.Substring(0, 40) + "...";
        }

        private static void LogOnce(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            if (!LoggedMessages.Add(message)) return;
            Debug.LogWarning("[ZBF-RT] " + message);
        }
    }

    /// <summary>隐藏的泵宿主：只做一件事——把主线程 Update 转成一次队列消费。</summary>
    internal sealed class RuntimeDevConsoleHost : MonoBehaviour
    {
        private void Update()
        {
            RuntimeDevConsole.Pump();
        }

        private void OnApplicationQuit()
        {
            RuntimeDevConsole.Stop();
        }

        private void OnDestroy()
        {
            if (RuntimeDevConsole.OwnsHost(this)) RuntimeDevConsole.Stop();
        }
    }
}
