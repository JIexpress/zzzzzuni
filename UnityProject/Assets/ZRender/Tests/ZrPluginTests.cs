// ZrPluginTests.cs —— Unity↔原生桥接实证：从 Unity 内加载 zrender.dll 并调通 C ABI 三函数。

using System.Runtime.InteropServices;
using NUnit.Framework;
using ZFrame.Render;
using ZFrame.Render.Bindings;

namespace ZFrame.Render.Tests
{
    [TestFixture]
    public class ZrPluginTests
    {
        [Test]
        public void EnsureLoaded_ReturnsApiVersion3()
        {
            // v3（TASK-B-PLANET-02）：追加行星通道 ABI（zplugin.h 末尾声明块，接口冻结）。
            ZrPluginInfo info = ZrPlugin.EnsureLoaded();
            Assert.AreEqual(3u, info.ApiVersion, "C ABI 版本应为 3");
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

        [Test]
        public void PlanetNative_StructMirrors_MatchNativeAbiSizes()
        {
            // zplugin.h 三个 static_assert（ZrPlanetWorld 32B / ZrPlanetChunkDesc 56B /
            // ZrPlanetStats 80B）在托管侧的镜像断言（TASK-B-PLANET-02 接口冻结自检）。
            Assert.AreEqual(32, Marshal.SizeOf<ZrPlanetWorldNative>(),
                "ZrPlanetWorldNative 托管布局应为 32B（与 zplugin.h static_assert 一致）");
            Assert.AreEqual(56, Marshal.SizeOf<ZrPlanetChunkDescNative>(),
                "ZrPlanetChunkDescNative 托管布局应为 56B（与 zplugin.h static_assert 一致）");
            Assert.AreEqual(80, Marshal.SizeOf<ZrPlanetStatsNative>(),
                "ZrPlanetStatsNative 托管布局应为 80B（与 zplugin.h static_assert 一致）");
        }

        [Test]
        public void PlanetNative_ChannelGate_PassesWithApiVersion3()
        {
            // 设计稿 §3.5/D6：ApiVersion >= 3 才启用行星通道——真 dll（v3）应通过门控。
            Assert.DoesNotThrow(() => ZrPlanetNative.EnsurePlanetChannelReady(),
                "真 dll ApiVersion=3 应通过行星通道版本门控");
        }

        [Test]
        public void PlanetNative_AbiConstants_MatchDesignDoc()
        {
            // 错误码七值 + 常量/掩码与 zplugin.h §14 一致（与 test_smoke 的运行时锁定互为镜像）。
            Assert.AreEqual(0, ZrPlanetNative.Ok);
            Assert.AreEqual(-1, ZrPlanetNative.ErrNull);
            Assert.AreEqual(-2, ZrPlanetNative.ErrAbi);
            Assert.AreEqual(-3, ZrPlanetNative.ErrRange);
            Assert.AreEqual(-4, ZrPlanetNative.ErrHandle);
            Assert.AreEqual(-5, ZrPlanetNative.ErrState);
            Assert.AreEqual(-6, ZrPlanetNative.ErrFull);
            Assert.AreEqual(-7, ZrPlanetNative.ErrNonfinite);
            Assert.AreEqual(8192u, ZrPlanetNative.MaxChunks);
            Assert.AreEqual(8u, ZrPlanetNative.VertexStrideFloats);
            Assert.AreEqual(1u, ZrPlanetNative.EdgeNegU);
            Assert.AreEqual(2u, ZrPlanetNative.EdgePosU);
            Assert.AreEqual(4u, ZrPlanetNative.EdgeNegV);
            Assert.AreEqual(8u, ZrPlanetNative.EdgePosV);
            Assert.AreEqual(3u, ZrPlanetNative.MinPlanetApiVersion);
        }
    }
}
