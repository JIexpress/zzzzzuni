using System;
using UnityEngine;

namespace ZFrame.View.DevConsole
{
    /// <summary>
    /// 运行期 DevConsole 的门控策略。**这是本任务最重要的一道墙**：A 层（Editor 服务）
    /// 泄露只影响开发者本机，B 层跟着发布包出去就是给外部开了一个能改画质/截图/触发
    /// 物件生成的洞。
    ///
    /// 规则（与日志 TASK-A-DEV-03 一致）：
    /// 1. 只有在**开发包**（<see cref="Debug.isDebugBuild"/>）里才可能启用；
    /// 2. 显式关闭优先，且关闭方式有两种：环境变量 <c>ZBF_RT_CONSOLE=0/false/off/no</c>，
    ///    或命令行 <c>-zbf-noconsole</c>；任何一条命中都彻底不监听、不建线程、不开 socket；
    /// 3. 判据本身是**可注入**的纯函数（<see cref="IsEnabled(Func{bool}, string[], Func{string, string})"/>），
    ///    测试可以同时断言"开发包分支监听"和"非开发包分支连接被拒"，不依赖真实 Release 构建。
    ///
    /// 刻意**没有**"强制打开"的开关：非开发包没有任何命令行/环境变量能把它打开。
    /// </summary>
    public static class RuntimeDevConsoleGate
    {
        public const int DefaultPort = 17322;

        public const string EnvDisable = "ZBF_RT_CONSOLE";
        public const string CliDisable = "-zbf-noconsole";
        public const string EnvPort = "ZBF_RT_PORT";
        public const string CliPort = "-zbf-port";

        /// <summary>
        /// 进程当前是否应启用运行期 DevConsole。
        ///
        /// TASK-A-DEV-04（H1）：这里**不再**有可写的 public `Func&lt;bool&gt; DevelopmentBuildProbe`，
        /// 也没有 `SetOverrideForTests`。判据直接读 <see cref="Debug.isDebugBuild"/>，
        /// 进程内没有任何"把判据换成 true"的公开入口；测试需要注入判据时走
        /// <c>RuntimeDevConsole.TryEnableForTests</c>（internal + 仅 Editor/测试/开发构建可见）。
        /// </summary>
        public static bool IsEnabled()
        {
            return IsEnabled(IsDevelopmentBuild, Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable);
        }

        /// <summary>生产判据：Development Build 才为 true（Unity 保证）。</summary>
        private static bool IsDevelopmentBuild()
        {
            return Debug.isDebugBuild;
        }

        /// <summary>
        /// 可注入的门控判据（无 Unity API 依赖，纯字符串/委托）：
        /// <c>开发包 &amp;&amp; !显式关闭</c>。关闭优先于一切。
        /// </summary>
        public static bool IsEnabled(
            Func<bool> isDevelopmentBuild,
            string[] commandLine,
            Func<string, string> getEnvironmentVariable)
        {
            if (isDevelopmentBuild == null) return false;
            if (IsExplicitlyDisabled(commandLine, getEnvironmentVariable)) return false;
            return isDevelopmentBuild();
        }

        /// <summary>环境变量或命令行任一命中关闭开关即为 true。</summary>
        public static bool IsExplicitlyDisabled(string[] commandLine, Func<string, string> getEnvironmentVariable)
        {
            if (commandLine != null)
            {
                for (int i = 0; i < commandLine.Length; i++)
                {
                    if (string.Equals(commandLine[i], CliDisable, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }

            string value = getEnvironmentVariable != null ? getEnvironmentVariable(EnvDisable) : null;
            if (string.IsNullOrEmpty(value)) return false;
            value = value.Trim();
            return value == "0"
                || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                || value.Equals("off", StringComparison.OrdinalIgnoreCase)
                || value.Equals("no", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>端口解析：命令行 <c>-zbf-port N</c> &gt; 环境变量 <c>ZBF_RT_PORT</c> &gt; 17322。</summary>
        public static int ResolvePort()
        {
            return ResolvePort(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable);
        }

        public static int ResolvePort(string[] commandLine, Func<string, string> getEnvironmentVariable)
        {
            if (commandLine != null)
            {
                for (int i = 0; i + 1 < commandLine.Length; i++)
                {
                    if (!string.Equals(commandLine[i], CliPort, StringComparison.OrdinalIgnoreCase)) continue;
                    if (TryParsePort(commandLine[i + 1], out int fromCli)) return fromCli;
                }
            }

            string raw = getEnvironmentVariable != null ? getEnvironmentVariable(EnvPort) : null;
            if (TryParsePort(raw, out int fromEnv)) return fromEnv;

            return DefaultPort;
        }

        private static bool TryParsePort(string text, out int port)
        {
            port = 0;
            if (string.IsNullOrEmpty(text)) return false;
            if (!int.TryParse(text.Trim(), out int parsed)) return false;
            if (parsed <= 0 || parsed > 65535) return false;
            port = parsed;
            return true;
        }
    }
}
