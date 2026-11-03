using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;

namespace ZFrame.EditorTools.DevServer
{
    /// <summary>
    /// 路由。除 /health 的免主线程快路径外，全部在主线程执行（由 DevServerBootstrap.Pump 泵出）。
    /// 约定：<see cref="Handle"/> 的返回值只表示"路由是否认领了这个请求"，认领后必须 Complete 或置 Deferred。
    /// </summary>
    internal static class DevRouter
    {
        static readonly string[] EndpointList =
        {
            "GET  /health",
            "POST /compile",
            "GET  /console?since=<seq>&limit=<n>",
            "POST /test?mode=editmode|playmode  -> {runId}",
            "GET  /test?id=<runId>",
            "POST /screenshot[?view=auto|camera|gameview|scene&name=]  (gameview 仅 Play Mode 可用，否则 503)",
            "POST /menu?path=Tools/...  (只允许白名单前缀，见 ZBF_DEV_MENU_PREFIXES)",
        };

        /// <summary>
        /// <c>/menu</c> 的默认白名单前缀（TASK-A-AUDIT-02 A-1）。<c>ExecuteMenuItem</c> 覆盖整个菜单树，
        /// 任意直通等于把"删资源 / 跑构建 / 切 Play"变成 HTTP API。默认只放项目自己的 <c>Tools/</c> 工具项；
        /// 需要别的（例如 <c>Edit/Play</c>）用环境变量 <see cref="EnvMenuPrefixes"/> 显式追加，分号分隔。
        ///
        /// 为什么选白名单而不是随机会话 token：白名单是**静态、fail-closed、无需分发密钥**的——
        /// token 要跨 domain reload 轮换、要落一个 dev.ps1 能读的文件、还要在每条 /menu 上带密钥，
        /// 三种额外状态都是新的失败面；而白名单漏配的后果是"某个菜单项调不动"，可见且立刻能修。
        /// </summary>
        static readonly string[] DefaultMenuPrefixes = { "Tools/" };

        const string EnvMenuPrefixes = "ZBF_DEV_MENU_PREFIXES";

        static string[] _menuPrefixes;

        /// <summary>
        /// 免主线程快路径：只有 /health 与 / 走这里——它们只读主线程发好的快照，不碰任何 Unity API，
        /// 因此即便主线程正卡在导入/编译里也能立刻答复（这也是"服务不拖累 Editor"的底气）。
        /// </summary>
        public static bool TryFastPath(DevHttpRequest request, out DevResponse response)
        {
            if (string.Equals(request.Method, "GET", StringComparison.Ordinal))
            {
                if (request.Path == "/health")
                {
                    response = Health();
                    return true;
                }

                if (request.Path == "/")
                {
                    response = Index();
                    return true;
                }
            }

            response = null;
            return false;
        }

        public static bool Handle(DevHttpRequest request)
        {
            switch (request.Path)
            {
                case "/health":
                    if (!RequireMethod(request, "GET")) return true;
                    request.Complete(Health());
                    return true;

                case "/":
                    if (!RequireMethod(request, "GET")) return true;
                    request.Complete(Index());
                    return true;

                case "/compile":
                    if (!RequireMethod(request, "POST")) return true;
                    return DevCompile.Handle(request);

                case "/console":
                    if (!RequireMethod(request, "GET")) return true;
                    request.Complete(ConsoleTail(request));
                    return true;

                case "/test":
                    return DevTestRuns.Handle(request);

                case "/screenshot":
                    return DevScreenshot.Handle(request);

                case "/menu":
                    if (!RequireMethod(request, "POST")) return true;
                    request.Complete(ExecuteMenu(request));
                    return true;

                default:
                    return false;
            }
        }

        static bool RequireMethod(DevHttpRequest request, string method)
        {
            if (string.Equals(request.Method, method, StringComparison.Ordinal)) return true;
            request.Complete(DevResponse.Error("method not allowed: " + request.Method + " " + request.Path + "（期望 " + method + "）", 405));
            return false;
        }

        static DevResponse Index()
        {
            var endpoints = new List<object>();
            for (int i = 0; i < EndpointList.Length; i++) endpoints.Add(EndpointList[i]);

            return DevResponse.Ok(new Dictionary<string, object>
            {
                ["ok"] = true,
                ["service"] = "ZFrame Dev Server",
                ["port"] = DevServerBootstrap.Port,
                ["endpoints"] = endpoints,
            });
        }

        static DevResponse Health()
        {
            long age = DevState.SnapshotAgeMs();
            var payload = new Dictionary<string, object>(DevState.Snapshot)
            {
                ["ok"] = true,
                ["snapshotAgeMs"] = age,
                ["snapshotIsStale"] = age > 2000,
            };
            return DevResponse.Ok(payload);
        }

        static DevResponse ConsoleTail(DevHttpRequest request)
        {
            long since = 0;
            long.TryParse(request.GetQuery("since", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out since);

            int limit = 500;
            if (!int.TryParse(request.GetQuery("limit", "500"), NumberStyles.Integer, CultureInfo.InvariantCulture, out limit))
            {
                limit = 500;
            }

            limit = Math.Max(1, Math.Min(2000, limit));

            long next;
            List<DevConsoleRing.Entry> entries = DevConsoleRing.Read(since, limit, out next);

            var items = new List<object>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                DevConsoleRing.Entry entry = entries[i];
                items.Add(new Dictionary<string, object>
                {
                    ["seq"] = entry.Seq,
                    ["type"] = entry.Type,
                    ["message"] = entry.Message,
                    ["stack"] = entry.Stack,
                });
            }

            return DevResponse.Ok(new Dictionary<string, object>
            {
                ["ok"] = true,
                ["entries"] = items,
                ["next"] = next,
                ["count"] = items.Count,
            });
        }

        static DevResponse ExecuteMenu(DevHttpRequest request)
        {
            string path = request.GetQuery("path");
            if (string.IsNullOrEmpty(path))
            {
                return DevResponse.Error("缺少 path 参数，例：/menu?path=Tools/ZFrame/Dev Server/Log status");
            }

            if (!IsMenuPathAllowed(path))
            {
                // 白名单外一律 403，且**不进 ExecuteMenuItem**（A-1：不再任意 path 直通）。
                return DevResponse.Error(
                    "menu path not allowed: '" + path + "'（允许前缀：" + string.Join(", ", MenuPrefixes()) +
                    "；用环境变量 " + EnvMenuPrefixes + " 追加，分号分隔）", 403);
            }

            try
            {
                bool executed = EditorApplication.ExecuteMenuItem(path);
                if (!executed)
                {
                    return DevResponse.Error("菜单项未找到或执行失败: " + path, 404);
                }

                return DevResponse.Ok(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["path"] = path,
                });
            }
            catch (Exception e)
            {
                return DevResponse.Error("执行菜单项异常: " + e.GetType().Name + ": " + e.Message, 500);
            }
        }

        static bool IsMenuPathAllowed(string path)
        {
            string[] prefixes = MenuPrefixes();
            for (int i = 0; i < prefixes.Length; i++)
            {
                if (path.StartsWith(prefixes[i], StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>默认前缀 + 环境变量追加（缓存；环境变量只在第一个请求时读一次）。</summary>
        static string[] MenuPrefixes()
        {
            if (_menuPrefixes != null) return _menuPrefixes;

            var prefixes = new List<string>(DefaultMenuPrefixes);
            string extra = Environment.GetEnvironmentVariable(EnvMenuPrefixes);
            if (!string.IsNullOrEmpty(extra))
            {
                string[] parts = extra.Split(';');
                for (int i = 0; i < parts.Length; i++)
                {
                    string prefix = parts[i].Trim();
                    if (prefix.Length > 0) prefixes.Add(prefix);
                }
            }

            _menuPrefixes = prefixes.ToArray();
            return _menuPrefixes;
        }
    }
}
