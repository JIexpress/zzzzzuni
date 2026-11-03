using System;

namespace ZFrame.EditorTools.DevServer
{
    /// <summary>
    /// 请求来源闸门（TASK-A-AUDIT-02 A-1）。三条规则，全部在后台线程执行、**没有一行 Unity API**：
    ///
    /// 1) <c>Host</c> 只认回环（<c>127.0.0.1[:port]</c> / <c>localhost[:port]</c>）——挡 DNS rebinding：
    ///    那种情况下连 Host 都指向攻击者域名，浏览器仍然把它发到 127.0.0.1。
    /// 2) <c>Origin</c> 缺失 → 放行（curl / Tools/dev.ps1 不带 Origin）；回环 Origin → 放行；
    ///    其余（含字面量 <c>null</c>、<c>file://</c>、任何第三方站点）→ 403。这条挡的是浏览器 CSRF：
    ///    <c>form</c> POST 属 CORS 简单请求、不触发预检，只有 Origin 能把它认出来。
    /// 3) 放行时把**具体** origin 交给响应头（<c>Access-Control-Allow-Origin: &lt;origin&gt;</c>），
    ///    不再有通配 <c>*</c>——于是即便某个第三方页面绕过上一步，它也读不到响应正文。
    ///
    /// 为什么不用 token：Host/Origin 两道已经封死了"外部页面驱动 Editor"这条路（浏览器无法伪造 Host，
    /// 也无法省略跨站请求的 Origin），token 只会在"本机脚本"这一侧增加密钥分发/轮换/domain reload 的负担。
    /// </summary>
    internal static class DevGuard
    {
        /// <summary>
        /// 校验一个请求是否来自本机。返回 null 表示放行；否则返回可直接回给客户端的原因（调用方配 403）。
        /// <paramref name="corsOrigin"/> 只在放行且请求带了回环 Origin 时非空。
        /// </summary>
        public static string Authorize(DevHttpRequest request, out string corsOrigin)
        {
            corsOrigin = null;

            string host = request.Headers.TryGetValue("Host", out string hostValue) ? hostValue : null;
            if (!IsLoopbackHost(host))
            {
                return "host not allowed: '" + (host ?? "<missing>") + "' (only 127.0.0.1 / localhost)";
            }

            request.Headers.TryGetValue("Origin", out string origin);
            if (!TryAcceptOrigin(origin, out string accepted))
            {
                return "origin not allowed: '" + origin + "' (only same-machine origins; omit Origin for scripts)";
            }

            corsOrigin = accepted;
            return null;
        }

        /// <summary><c>Host</c> 必须恰好是回环主机名，可带端口；其余一律拒（空/缺失也拒）。</summary>
        public static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;

            string name = host.Trim();
            int colon = name.LastIndexOf(':');

            // 端口部分只能是纯数字；主机部分不含冒号（IPv6 字面量本服务不监听，直接拒）。
            if (colon >= 0)
            {
                string port = name.Substring(colon + 1);
                if (port.Length == 0) return false;
                for (int i = 0; i < port.Length; i++)
                {
                    if (port[i] < '0' || port[i] > '9') return false;
                }

                name = name.Substring(0, colon);
            }

            if (name.IndexOf(':') >= 0) return false;

            return name == "127.0.0.1" || string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// <c>Origin</c> 缺失 → 接受（脚本客户端）。带 Origin 时必须是 http(s) + 回环主机 + 无 userinfo。
        /// 字面量 <c>null</c>（沙箱 iframe / data: 页面）按第三方处理。
        /// </summary>
        public static bool TryAcceptOrigin(string origin, out string accepted)
        {
            accepted = null;

            if (string.IsNullOrEmpty(origin)) return true; // 无 Origin 头：非浏览器客户端，放行

            if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out Uri uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
            if (!IsLoopbackHost(uri.Host)) return false;

            accepted = origin.Trim();
            return true;
        }
    }
}
