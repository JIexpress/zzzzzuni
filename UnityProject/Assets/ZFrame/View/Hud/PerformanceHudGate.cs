using System;

namespace ZFrame.View
{
    /// <summary>
    /// 性能 HUD 的门控策略。与 <c>RuntimeDevConsoleGate</c> 同一套思路：
    /// **开发包是唯一的可能区间，发布包不存在任何开启路径**。
    ///
    /// 规则：
    /// 1. <see cref="IsAvailable"/> 只认开发包（<c>Debug.isDebugBuild</c>，由调用方注入）；
    /// 2. 默认关闭：只有命令行 <c>-zfhud</c> 或环境变量 <c>ZBF_HUD=1/true/on/yes</c>
    ///    才让 HUD 以可见状态启动；
    /// 3. 开发包里 F3 手动切换，但切换入口同样先过 <see cref="IsAvailable"/>；
    /// 4. 判据是纯函数（委托 + 字符串数组），EditMode 可以断言"发布包 + 参数"也被拒。
    ///
    /// 另有一道编译期墙：<see cref="PerformanceHud"/> 本体只在 Editor / 开发构建里编译，
    /// 发布包的托管程序集里根本没有这个类、没有 F3 轮询、没有 ProfilerRecorder。
    /// </summary>
    public static class PerformanceHudGate
    {
        /// <summary>环境变量：值为 1/true/on/yes 时请求启动。</summary>
        public const string EnvEnable = "ZBF_HUD";

        /// <summary>命令行：出现该参数时请求启动。</summary>
        public const string CliEnable = "-zfhud";

        /// <summary>是否允许 HUD 存在/被切换。生产判据是 <c>Debug.isDebugBuild</c>；null 视为拒绝（fail closed）。</summary>
        public static bool IsAvailable(Func<bool> isDevelopmentBuild)
        {
            return isDevelopmentBuild != null && isDevelopmentBuild();
        }

        /// <summary>命令行或环境变量是否显式请求启动 HUD（**不含**开发包判定）。</summary>
        public static bool IsRequested(string[] commandLine, Func<string, string> getEnvironmentVariable)
        {
            if (commandLine != null)
            {
                for (int i = 0; i < commandLine.Length; i++)
                {
                    if (string.Equals(commandLine[i], CliEnable, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }

            string value = getEnvironmentVariable != null ? getEnvironmentVariable(EnvEnable) : null;
            return IsTruthy(value);
        }

        /// <summary>
        /// 启动时是否直接可见 = 开发包 &amp;&amp; 显式请求。
        /// **发布包里即使带了 <c>-zfhud</c> / <c>ZBF_HUD=1</c> 也返回 false**——这是门控的牙。
        /// </summary>
        public static bool ShouldStartVisible(
            Func<bool> isDevelopmentBuild,
            string[] commandLine,
            Func<string, string> getEnvironmentVariable)
        {
            if (!IsAvailable(isDevelopmentBuild)) return false;
            return IsRequested(commandLine, getEnvironmentVariable);
        }

        /// <summary>环境变量的"开"值：1/true/on/yes（大小写不敏感，允许首尾空白）。</summary>
        public static bool IsTruthy(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            value = value.Trim();
            return value == "1"
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
