// ZrPluginInfo.cs —— 原生插件加载后的版本诊断信息快照。

namespace ZFrame.Render.Bindings
{
    /// <summary>zrender.dll 加载成功后的一次性诊断信息。</summary>
    public sealed class ZrPluginInfo
    {
        /// <summary>C ABI 版本号（当前约定为 3——B-PLANET-02 行星通道 ABI；与 ZrPlugin.ExpectedApiVersion 一致）。</summary>
        public uint ApiVersion { get; }

        /// <summary>ZRender 版本字符串，如 "0.1.0"。</summary>
        public string Version { get; }

        /// <summary>构建诊断信息（编译器 / 后端 / 模块版本 / 构建日期）。</summary>
        public string BuildInfo { get; }

        public ZrPluginInfo(uint apiVersion, string version, string buildInfo)
        {
            ApiVersion = apiVersion;
            Version = version;
            BuildInfo = buildInfo;
        }
    }
}
