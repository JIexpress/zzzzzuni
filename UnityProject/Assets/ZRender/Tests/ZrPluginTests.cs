// ZrPluginTests.cs —— Unity↔原生桥接实证：从 Unity 内加载 zrender.dll 并调通 C ABI 三函数。

using NUnit.Framework;
using ZFrame.Render;
using ZFrame.Render.Bindings;

namespace ZFrame.Render.Tests
{
    [TestFixture]
    public class ZrPluginTests
    {
        [Test]
        public void EnsureLoaded_ReturnsApiVersion2()
        {
            // v2（TASK-B-FSR-02）：ZrUpscalerCaps 末尾追加 fsr4ProbeMode（36B）。
            ZrPluginInfo info = ZrPlugin.EnsureLoaded();
            Assert.AreEqual(2u, info.ApiVersion, "C ABI 版本应为 2");
        }

        [Test]
        public void EnsureLoaded_VersionIs_0_1_0()
        {
            ZrPluginInfo info = ZrPlugin.EnsureLoaded();
            Assert.AreEqual("0.1.0", info.Version, "原生版本字符串应为 0.1.0");
        }

        [Test]
        public void EnsureLoaded_BuildInfoIsNotEmpty()
        {
            ZrPluginInfo info = ZrPlugin.EnsureLoaded();
            Assert.IsFalse(string.IsNullOrEmpty(info.BuildInfo), "BuildInfo 应非空");
        }

        [Test]
        public void EnsureLoaded_IsIdempotent_SameInstance()
        {
            ZrPluginInfo first = ZrPlugin.EnsureLoaded();
            ZrPluginInfo second = ZrPlugin.EnsureLoaded();
            Assert.AreSame(first, second, "重复调用应返回同一缓存实例");
        }

        [Test]
        public void ZrNativeBridge_PluginInfo_MatchesFirstLoad()
        {
            ZrPluginInfo expected = ZrPlugin.EnsureLoaded();
            ZrPluginInfo bridged = ZrNativeBridge.PluginInfo;
            Assert.AreSame(expected, bridged);
            Assert.AreEqual(expected.ApiVersion, bridged.ApiVersion);
            Assert.AreEqual(expected.Version, bridged.Version);
            Assert.AreEqual(expected.BuildInfo, bridged.BuildInfo);
        }
    }
}
