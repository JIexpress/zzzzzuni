// ZrNativeBridge.cs —— ZFrame.Render 与原生库之间的懒加载桥：首次访问加载并缓存信息。

using ZFrame.Render.Bindings;

namespace ZFrame.Render
{
    /// <summary>
    /// 静态桥：首次访问 <see cref="PluginInfo"/> 触发 zrender.dll 加载与版本校验，
    /// 结果缓存供调试面板等将来显示。
    /// </summary>
    public static class ZrNativeBridge
    {
        private static ZrPluginInfo _pluginInfo;

        /// <summary>原生插件版本信息；首次访问触发加载（失败抛异常）。</summary>
        public static ZrPluginInfo PluginInfo => _pluginInfo ??= ZrPlugin.EnsureLoaded();
    }
}
