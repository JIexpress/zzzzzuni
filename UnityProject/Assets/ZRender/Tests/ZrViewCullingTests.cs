// ZrViewCullingTests.cs —— 真视锥剔除纯函数 + 帧内账本的 EditMode 单测（TASK-B-VIS-02）。
//
// 钉住三件事：
//   1) CullToViewMask 的几何语义（可见 / 不可见 / 恰好相切 / 退化半径 / NaN 输入）
//   2) 128 位掩码的位序（视图 i → 第 i 位，i ≥ 64 落 Hi，与 Mask128 的 w 索引一致）
//   3) 帧内"录入 N / 消费 M_v"计数：M_v ≤ N、(物件,视图) 不重复、错用会计数
//
// 全部是纯 CPU 计算（只依赖 UnityEngine.Matrix4x4/Plane/Vector3），EditMode 可裸跑。
// 变异证据见 Docs/Logs/TASK-B-VIS-02.md：把平面方向或位序改坏 → 本文件必红。

using NUnit.Framework;
using UnityEngine;
using ZFrame.Render.Bindings;

namespace ZFrame.Render.Tests
{
    [TestFixture]
    public class ZrViewCullingTests
    {
        // 单位 viewProjection：裁剪体 = 世界空间 [-1,1]³ 立方体（六个平面都是轴对齐的）。
        private static ZrSceneMath.ZrViewFrustum IdentityView()
        {
            return ZrSceneMath.ZrViewFrustum.FromViewProjection(Matrix4x4.identity);
        }

        private static ZkMask Cull(ZrSceneMath.ZrViewFrustum[] views, int count, Vector3 center,
            float radius)
        {
            return ZrSceneMath.CullToViewMask(views, count, center, radius);
        }

        // ---- 1) 几何三态 -----------------------------------------------------------

        [Test]
        public void CullToViewMask_SphereInside_SetsViewBit()
        {
            var views = new[] { IdentityView() };
            var mask = Cull(views, 1, Vector3.zero, 0.5f);

            Assert.IsTrue(mask.Has(0u), "完全在裁剪体内的球必须可见");
            Assert.AreEqual(1, mask.CountBits());
        }

        [Test]
        public void CullToViewMask_SphereFullyOutside_ReturnsZeroMask()
        {
            var views = new[] { IdentityView() };
            var mask = Cull(views, 1, new Vector3(2f, 0f, 0f), 0.5f);

            Assert.IsTrue(mask.Lo == 0ul && mask.Hi == 0ul, "完全在右侧平面外的球必须不可见");
        }

        [Test]
        public void CullToViewMask_SphereOutsideEachOfSixPlanes_IsCulled()
        {
            // 六面逐一越界（对称覆盖）：任何单面方向写反都会让其中一个球"显形"。
            var views = new[] { IdentityView() };
            var centers = new[]
            {
                new Vector3(-2f, 0f, 0f), new Vector3(2f, 0f, 0f), // 左 / 右
                new Vector3(0f, -2f, 0f), new Vector3(0f, 2f, 0f), // 下 / 上
                new Vector3(0f, 0f, -2f), new Vector3(0f, 0f, 2f), // 近 / 远
            };

            for (int i = 0; i < centers.Length; i++)
            {
                Assert.IsTrue(Cull(views, 1, centers[i], 0.5f).Lo == 0ul,
                    $"第 {i} 面（{centers[i]}）之外的球必须被剔除——单面方向写反会在这里变红");
            }

            Assert.IsTrue(Cull(views, 1, Vector3.zero, 0.5f).Has(0u), "原点在体内必须可见");
        }

        [Test]
        public void CullToViewMask_SphereBehindCamera_IsCulled()
        {
            // 透视相机在原点朝 -Z（Unity 约定），球在相机背后。
            var views = new[]
            {
                ZrSceneMath.ZrViewFrustum.FromViewProjection(
                    Matrix4x4.Perspective(60f, 1f, 0.3f, 100f)),
            };
            var front = Cull(views, 1, new Vector3(0f, 0f, -10f), 0.5f);
            var behind = Cull(views, 1, new Vector3(0f, 0f, 10f), 0.5f);

            Assert.IsTrue(front.Has(0u), "相机前方 10m 的球必须可见");
            Assert.IsTrue(behind.Lo == 0ul, "相机背后的球必须被剔除");
        }

        [Test]
        public void CullToViewMask_PerspectiveNearFar_AreRespected()
        {
            var views = new[]
            {
                ZrSceneMath.ZrViewFrustum.FromViewProjection(
                    Matrix4x4.Perspective(60f, 1f, 0.3f, 100f)),
            };

            Assert.IsTrue(Cull(views, 1, new Vector3(0f, 0f, -0.1f), 0.01f).Lo == 0ul,
                "比近平面更近的球必须被剔除");
            Assert.IsTrue(Cull(views, 1, new Vector3(0f, 0f, -200f), 0.5f).Lo == 0ul,
                "超出远平面的球必须被剔除");
            Assert.IsTrue(Cull(views, 1, new Vector3(0f, 0f, -99.8f), 0.5f).Lo == 1ul,
                "远平面内侧且相切的球必须保留");
        }

        // ---- 2) 边界语义：相切 / 退化半径 / NaN ------------------------------------

        [Test]
        public void CullToViewMask_TangentExactly_IsVisible()
        {
            var views = new[] { IdentityView() };
            // 右平面 x=1：中心 1.5、半径 0.5 → 有符号距离 -0.5，恰好相切。
            Assert.IsTrue(Cull(views, 1, new Vector3(1.5f, 0f, 0f), 0.5f).Has(0u),
                "恰好相切必须判可见（闭集）");

            // 再远 0.001 → 完全在外。
            Assert.IsTrue(Cull(views, 1, new Vector3(1.501f, 0f, 0f), 0.5f).Lo == 0ul,
                "越过相切 1mm 的球必须剔除");
        }

        [Test]
        public void CullToViewMask_DegenerateRadius_BehavesAsPointTest()
        {
            var views = new[] { IdentityView() };

            Assert.IsTrue(Cull(views, 1, new Vector3(1f, 0f, 0f), 0f).Has(0u),
                "半径 0 落在边界上仍可见（点测试闭集）");
            Assert.IsTrue(Cull(views, 1, new Vector3(1.0001f, 0f, 0f), 0f).Lo == 0ul,
                "半径 0 越界 0.1mm 即剔除");
        }

        [Test]
        public void CullToViewMask_NegativeRadius_TreatedAsZero()
        {
            var views = new[] { IdentityView() };

            // 若把 -5 当成 5 处理，这个球会"因为半径大"被判可见；按 0 处理则应剔除。
            Assert.IsTrue(Cull(views, 1, new Vector3(1.1f, 0f, 0f), -5f).Lo == 0ul,
                "负半径必须按 0 处理，不能放大可见范围");
        }

        [Test]
        public void CullToViewMask_NaNInputs_FailClosed()
        {
            var views = new[] { IdentityView() };

            Assert.IsTrue(Cull(views, 1, Vector3.zero, float.NaN).Lo == 0ul,
                "NaN 半径必须 fail-closed（不可见）");
            Assert.IsTrue(Cull(views, 1, new Vector3(float.NaN, 0f, 0f), 1f).Lo == 0ul,
                "NaN 中心必须 fail-closed");
            Assert.IsTrue(Cull(views, 1, new Vector3(float.PositiveInfinity, 0f, 0f), 1f).Lo == 0ul,
                "无穷中心必须 fail-closed");
        }

        [Test]
        public void CullToViewMask_DegenerateFrustum_FailsClosed()
        {
            // 全零矩阵 → 六个平面全退化，视图非法。
            var views = new[] { ZrSceneMath.ZrViewFrustum.FromViewProjection(Matrix4x4.zero) };

            Assert.IsFalse(views[0].IsValid, "全零矩阵提取出的视图必须判非法");
            Assert.IsTrue(Cull(views, 1, Vector3.zero, 1f).Lo == 0ul,
                "非法视图必须 fail-closed（宁可不画）");
        }

        [Test]
        public void CullToViewMask_NullOrEmptyInputs_ReturnZeroMask()
        {
            Assert.IsTrue(ZrSceneMath.CullToViewMask(null, 4, Vector3.zero, 1f).Lo == 0ul);
            Assert.IsTrue(Cull(new[] { IdentityView() }, 0, Vector3.zero, 1f).Lo == 0ul);
            Assert.IsTrue(Cull(new[] { IdentityView() }, -3, Vector3.zero, 1f).Lo == 0ul);
        }

        // ---- 3) 128 位掩码位序（= Mask128 / ZkMask 语义）----------------------------

        [Test]
        public void CullToViewMask_MultipleViews_SetsOnlyVisibleBits()
        {
            // views[0]/[1] = 单位裁剪体；views[2] 把可见体平移到 x∈[-3,-1]（原点不可见）。
            var views = new[]
            {
                IdentityView(),
                IdentityView(),
                ZrSceneMath.ZrViewFrustum.FromViewProjection(
                    Matrix4x4.Translate(new Vector3(2f, 0f, 0f))),
            };

            var mask = Cull(views, 3, Vector3.zero, 0.1f);

            Assert.AreEqual(0b011ul, mask.Lo, "只有视图 0/1 看见原点");
            Assert.AreEqual(0ul, mask.Hi);
            Assert.AreEqual(2, mask.CountBits());
        }

        [Test]
        public void CullToViewMask_ViewIndex64_LandsOnHighWord()
        {
            // 65 个视图：只有 64 号（== Hi 的第 0 位）是单位裁剪体，其余默认退化 → 不可见。
            var views = new ZrSceneMath.ZrViewFrustum[65];
            views[64] = IdentityView();

            var mask = Cull(views, 65, Vector3.zero, 0.1f);

            Assert.IsTrue(mask.Has(64u), "视图 64 必须落在高 64 位");
            Assert.AreEqual(0ul, mask.Lo, "低位字必须为 0");
            Assert.AreEqual(1ul, mask.Hi, "Hi 的第 0 位 = 视图 64");
            Assert.AreEqual(1, mask.CountBits());
        }

        [Test]
        public void CullToViewMask_ViewCountAbove128_IsTruncated()
        {
            var views = new ZrSceneMath.ZrViewFrustum[130];
            for (int i = 0; i < views.Length; i++)
            {
                views[i] = IdentityView();
            }

            var mask = Cull(views, 130, Vector3.zero, 0.1f);

            Assert.AreEqual(128, mask.CountBits(), "128 位掩码只能表达前 128 个视图");
        }

        // ---- 4) 帧内录入 / 消费计数 -------------------------------------------------

        [Test]
        public void VisibilityFrame_SixViews_RecordsOnceAndConsumesPerView()
        {
            var views = new ZrSceneMath.ZrViewFrustum[6];
            for (int i = 0; i < views.Length; i++)
            {
                views[i] = IdentityView();
            }

            const int itemCount = 32;
            var frame = new ZrVisibilityFrame();
            frame.BeginFrame();

            var masks = new ZkMask[itemCount];
            for (int item = 0; item < itemCount; item++)
            {
                frame.Record(item);
                // 一半落在体内（x∈[-0.9,0.9]），一半落在体外（x=2）。
                float x = item % 2 == 0 ? -0.9f + (0.05f * item) : 2f;
                masks[item] = Cull(views, views.Length, new Vector3(x, 0f, 0f), 0.05f);
            }

            for (int item = 0; item < itemCount; item++)
            {
                for (int view = 0; view < views.Length; view++)
                {
                    if (masks[item].Has((uint)view))
                    {
                        Assert.IsTrue(frame.TryConsume(item, view), "首次消费必须成功");
                    }
                }
            }

            Assert.AreEqual(itemCount, frame.Recorded, "录入 N = 物件数（不是 N×视图数）");
            for (int view = 0; view < views.Length; view++)
            {
                Assert.LessOrEqual(frame.ConsumedForView(view), frame.Recorded,
                    "每视图消费 M_v 必须 ≤ 录入 N");
            }

            Assert.Greater(frame.ConsumedTotal, frame.Recorded, "多视图合计可以大于 N（不是重复消费）");
            Assert.IsTrue(frame.Validate(out var error), error);
        }

        [Test]
        public void VisibilityFrame_DuplicateConsumption_IsRejectedAndCounted()
        {
            var frame = new ZrVisibilityFrame();
            frame.BeginFrame();
            frame.Record(7);

            Assert.IsTrue(frame.TryConsume(7, 0));
            Assert.IsFalse(frame.TryConsume(7, 0), "同一 (物件,视图) 二次消费必须被拒");
            Assert.AreEqual(1, frame.DuplicateConsumptions);
            Assert.IsFalse(frame.Validate(out var error));
            StringAssert.Contains("重复消费", error);
        }

        [Test]
        public void VisibilityFrame_SameItemAcrossViews_IsNotDuplicate()
        {
            var frame = new ZrVisibilityFrame();
            frame.BeginFrame();
            frame.Record(7);

            Assert.IsTrue(frame.TryConsume(7, 0));
            Assert.IsTrue(frame.TryConsume(7, 5), "同一物件在不同视图消费是正确行为");
            Assert.AreEqual(0, frame.DuplicateConsumptions);
            Assert.IsTrue(frame.Validate(out _));
        }

        [Test]
        public void VisibilityFrame_DuplicateRecord_IsCounted()
        {
            var frame = new ZrVisibilityFrame();
            frame.BeginFrame();
            frame.Record(7);
            frame.Record(7);

            Assert.AreEqual(1, frame.Recorded, "重复录入不增加 N");
            Assert.AreEqual(1, frame.DuplicateRecords);
            Assert.IsFalse(frame.Validate(out var error));
            StringAssert.Contains("重复录入", error);
        }

        [Test]
        public void VisibilityFrame_ConsumeWithoutRecord_IsCounted()
        {
            var frame = new ZrVisibilityFrame();
            frame.BeginFrame();

            Assert.IsFalse(frame.TryConsume(99, 0), "未录入就消费必须被拒");
            Assert.AreEqual(1, frame.OrphanConsumptions);
            Assert.IsFalse(frame.Validate(out var error));
            StringAssert.Contains("越界消费", error);
        }

        [Test]
        public void VisibilityFrame_BeginFrame_ResetsAllCounters()
        {
            var frame = new ZrVisibilityFrame();
            frame.BeginFrame();
            frame.Record(1);
            frame.Record(1);
            frame.TryConsume(2, 3);

            frame.BeginFrame();

            Assert.AreEqual(0, frame.Recorded);
            Assert.AreEqual(0, frame.ConsumedTotal);
            Assert.AreEqual(0, frame.DuplicateRecords);
            Assert.AreEqual(0, frame.DuplicateConsumptions);
            Assert.AreEqual(0, frame.OrphanConsumptions);
            Assert.IsTrue(frame.Validate(out _));
        }
    }
}
