// ZRenderPipelineAssetTests.cs —— SRP 壳类可构造性验证。

using NUnit.Framework;
using ZFrame.Render;

namespace ZFrame.Render.Tests
{
    [TestFixture]
    public class ZRenderPipelineAssetTests
    {
        [Test]
        public void Constructor_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => _ = new ZRenderPipelineAsset());
        }
    }
}
