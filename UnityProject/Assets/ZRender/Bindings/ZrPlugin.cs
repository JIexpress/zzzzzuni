// ZrPlugin.cs —— 原生插件加载门卫：验证 zrender.dll 可加载且 C ABI 版本匹配。

using System;
using System.Runtime.InteropServices;

namespace ZFrame.Render.Bindings
{
    /// <summary>
    /// 确保加载 zrender.dll 并校验 C ABI 版本；成功后缓存 <see cref="ZrPluginInfo"/>，
    /// 重复调用幂等（返回同一实例）。
    /// </summary>
    public static class ZrPlugin
    {
        /// <summary>当前托管侧支持的 C ABI 版本（与 ZR_ApiVersion 约定一致；v3 = B-PLANET-02 追加行星通道 ABI——zplugin.h 末尾声明块。历史：v2 = B-FSR-02 追加 fsr4ProbeMode）。</summary>
        public const uint ExpectedApiVersion = 3;

        private static readonly object Gate = new object();
        private static ZrPluginInfo _info;

        /// <summary>加载（或复用已加载的）zrender.dll 并返回其版本信息。</summary>
        /// <exception cref="DllNotFoundException">zrender.dll 不在插件搜索路径上。</exception>
        /// <exception cref="InvalidOperationException">C ABI 版本与托管侧不符。</exception>
        public static ZrPluginInfo EnsureLoaded()
        {
            lock (Gate)
            {
                if (_info != null)
                {
                    return _info;
                }

                uint apiVersion;
                try
                {
                    apiVersion = ZrNative.ZR_ApiVersion();
                }
                catch (DllNotFoundException e)
                {
                    throw new DllNotFoundException(
                        "zrender.dll 未找到：请先运行 Tools/build_native.ps1 生成并拷贝到 Assets/Plugins/ZRender/。", e);
                }

                if (apiVersion != ExpectedApiVersion)
                {
                    throw new InvalidOperationException(
                        $"zrender.dll C ABI 版本不符：期望 {ExpectedApiVersion}，实际 {apiVersion}。" +
                        "请重新构建原生库或更新托管绑定。");
                }

                string version = ReadAnsiString(ZrNative.ZR_GetVersion());
                string buildInfo = ReadAnsiString(ZrNative.ZR_GetBuildInfo());
                _info = new ZrPluginInfo(apiVersion, version, buildInfo);
                return _info;
            }
        }

        // 原生侧返回静态缓冲区指针；Marshal.PtrToStringAnsi 拷贝到托管字符串后原指针无需释放。
        private static string ReadAnsiString(IntPtr p)
        {
            return p == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(p);
        }
    }
}
