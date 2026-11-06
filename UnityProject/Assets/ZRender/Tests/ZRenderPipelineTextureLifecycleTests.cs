// ZRenderPipelineTextureLifecycleTests.cs —— 共享目标重建的纹理生命周期（TASK-B-UNITY-04）。
//
// 钉住的是 B-UNITY-03 验收点出的第二个遗留：**改分辨率重建共享目标时旧 Texture2D 没
// 销毁**（每次改尺寸漏一张）。旧实现只把字段置 null 并调 ReleaseSharedTarget，Unity
// 侧那张 Texture2D 就再也没人管了。
//
// 做法：走"按尺寸重建"的接缝（EnsureNativeTargetForSize）连续改 3 次分辨率，直接用
// Resources.FindObjectsOfTypeAll 数引擎里真实存在的同名纹理对象——不是数我自己的计数器
// （计数器只作交叉验证，两者必须一致）。
//
// 前提：真实设备（D3D12）。编辑器不是 D3D12 时 Skip，改由 Player 现场验证。

using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using ZFrame.Render;

namespace ZFrame.Render.Tests
{
    [TestFixture]
    public class ZRenderPipelineTextureLifecycleTests
    {
        private const string TargetTextureName = "ZRenderSharedTarget";
        private const int SlotCount = 2; // 双缓冲：两个槽位各一张外部纹理

        /// <summary>
        /// 连续改 3 次分辨率：引擎里同名纹理对象数必须恒定（= 槽位数），且创建数比销毁数
        /// 多出的正好是槽位数。旧实现（不 Destroy）这里会看到 2 → 4 → 6 → 8。
        /// </summary>
        [Test]
        public void RebuildThreeTimes_TextureObjectCountDoesNotGrow()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Assert.Ignore("共享目标只在 D3D12 下可用（当前 " +
                    SystemInfo.graphicsDeviceType + "）；该路径由 Player 现场验证");
            }

            var pipeline = new ZRenderPipeline();
            int baseline;
            try
            {
                Assert.IsTrue(pipeline.EnsureNativeTargetForSize(320, 180),
                    "首个共享目标必须建成功（D3D12 下失败即真问题，不是环境问题）");

                baseline = CountLiveTargetTextures();
                Assert.AreEqual(SlotCount, baseline, "双缓冲应有 " + SlotCount + " 张外部纹理");
                Assert.AreEqual(baseline, pipeline.LiveExternalTextureCount,
                    "自述存活数必须与引擎里真实存在的对象数一致");

                int[][] sizes =
                {
                    new[] { 400, 225 },
                    new[] { 512, 288 },
                    new[] { 640, 360 },
                };
                foreach (int[] size in sizes)
                {
                    Assert.IsTrue(pipeline.EnsureNativeTargetForSize(size[0], size[1]),
                        $"重建到 {size[0]}x{size[1]} 必须成功");
                    Assert.AreEqual(baseline, CountLiveTargetTextures(),
                        $"改到 {size[0]}x{size[1]} 后纹理对象数不得增长（旧实现每次都漏一张）");
                    Assert.AreEqual(baseline, pipeline.LiveExternalTextureCount);
                }

                Assert.AreEqual(sizes.Length * SlotCount, pipeline.DestroyedExternalTextureCount,
                    "三次重建应销毁三批旧纹理（每批 " + SlotCount + " 张）");
            }
            finally
            {
                pipeline.DisposeForTests();
            }

            Assert.AreEqual(0, CountLiveTargetTextures(), "Dispose 后不得残留外部纹理");
        }

        private static int CountLiveTargetTextures()
        {
            Texture2D[] textures = Resources.FindObjectsOfTypeAll<Texture2D>();
            int count = 0;
            for (int i = 0; i < textures.Length; i++)
            {
                Texture2D texture = textures[i];
                if (texture != null && string.Equals(texture.name, TargetTextureName))
                {
                    count++;
                }
            }

            return count;
        }
    }
}
