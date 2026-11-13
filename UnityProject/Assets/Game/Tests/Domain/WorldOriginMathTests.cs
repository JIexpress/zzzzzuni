// WorldOriginMathTests.cs —— TASK-A-WORLD-01：纯数学层的 EditMode 证据。
// 覆盖 V1（互转守恒）、V2（阈值语义）、V3（重定位守恒的数学部分）、V5（未达阈值不动）、
// V6（100km 精度量化：float 直接表示 vs double+偏移）与 V7 的变异靶点
// （ToUnity 若改成「先转 float 再减偏移」，ToUnity_PreservesSubMillimetre_* 必红）。

using Game.Domain.World;
using NUnit.Framework;
using UnityEngine;

namespace Game.Domain.Tests
{
    /// <summary>浮点原点纯函数：阈值、互转、守恒、精度。</summary>
    public sealed class WorldOriginMathTests
    {
        private const double Threshold = 4000.0;

        // ---- V2：阈值语义 ---------------------------------------------------

        [Test]
        public void ShouldRebase_BelowThreshold_False()
        {
            Assert.IsFalse(WorldOriginMath.ShouldRebase(0.0, Threshold));
            Assert.IsFalse(WorldOriginMath.ShouldRebase(3999.999, Threshold));
            Assert.IsFalse(WorldOriginMath.ShouldRebase(-10.0, Threshold));
        }

        [Test]
        public void ShouldRebase_AtOrAboveThreshold_True()
        {
            Assert.IsTrue(WorldOriginMath.ShouldRebase(Threshold, Threshold), "恰好等于阈值必须触发");
            Assert.IsTrue(WorldOriginMath.ShouldRebase(4000.0001, Threshold));
            Assert.IsTrue(WorldOriginMath.ShouldRebase(5000.0, Threshold));
        }

        [Test]
        public void ShouldRebase_NonPositiveThreshold_IsIllegal_AndNeverFires()
        {
            Assert.IsFalse(WorldOriginMath.ShouldRebase(100.0, 0.0));
            Assert.IsFalse(WorldOriginMath.ShouldRebase(100.0, -1.0));
            Assert.IsFalse(WorldOriginMath.ShouldRebase(double.MaxValue, 0.0));
        }

        [Test]
        public void ShouldRebase_UsesThreeDimensionalDistance()
        {
            var camera = new WorldPosition(3000.0, 0.0, 3000.0);
            var origin = WorldPosition.Zero;

            Assert.AreEqual(4242.640687119285, WorldOriginMath.OffsetMeters(camera, origin), 1e-9);
            Assert.IsTrue(WorldOriginMath.ShouldRebase(camera, origin, Threshold), "斜向 4.24km 必须触发");
            Assert.IsFalse(WorldOriginMath.ShouldRebase(new WorldPosition(2000.0, 2000.0, 0.0), origin, Threshold));
        }

        [Test]
        public void RebaseDelta_BelowThreshold_IsZero()
        {
            var camera = new WorldPosition(3999.0, 0.0, 0.0);

            Assert.AreEqual(Vector3.zero, WorldOriginMath.RebaseDelta(camera, WorldPosition.Zero, Threshold));
        }

        [Test]
        public void RebaseDelta_AtThreshold_IsCameraOffset()
        {
            var camera = new WorldPosition(4000.0, 3.0, -2.0);

            Vector3 delta = WorldOriginMath.RebaseDelta(camera, WorldPosition.Zero, Threshold);

            Assert.AreEqual(4000f, delta.x);
            Assert.AreEqual(3f, delta.y);
            Assert.AreEqual(-2f, delta.z);
        }

        // ---- V1：互转守恒（关键是偏移后相对坐标很小，float 才有足够分辨率）----

        [Test]
        public void ToUnity_ThenToWorld_RoundTripsAt100km()
        {
            // 偏移刻意取 float 表示不了的 100000.0001：只有 double 域相减才留得住。
            var offset = new WorldPosition(100000.0001, -2.0, 7.0);
            var world = offset + new WorldPosition(12.5, 7.0, -10.0);

            Vector3 unity = WorldOriginMath.ToUnity(world, offset);
            Assert.AreEqual(12.5f, unity.x, 1e-4f);
            Assert.AreEqual(7f, unity.y, 1e-4f);
            Assert.AreEqual(-10f, unity.z, 1e-4f);

            WorldPosition back = WorldOriginMath.ToWorld(unity, offset);
            double roundTripError = WorldPosition.Distance(world, back);
            TestContext.WriteLine($"V1 @100km roundtrip error = {roundTripError:E3} m");
            Assert.Less(roundTripError, 1e-3, "100km 处互转误差必须 < 1e-3 m");
        }

        [Test]
        public void ToUnity_AtSmallCoordinates_IsExact()
        {
            var world = new WorldPosition(3.25, 0.0, -7.5);

            Vector3 unity = WorldOriginMath.ToUnity(world, WorldPosition.Zero);
            Assert.AreEqual(3.25f, unity.x, 0f);
            Assert.AreEqual(0f, unity.y, 0f);
            Assert.AreEqual(-7.5f, unity.z, 0f);
            Assert.AreEqual(world, WorldPosition.FromUnity(unity, WorldPosition.Zero));
        }

        /// <summary>
        /// V7 的靶点：100km 处 1mm 的细节，只有「先 double 相减」才留得住。
        /// 变体实现（先 (float)world 再减 (float)offset）在这里会得到 0，误差 1mm → 必红。
        /// </summary>
        [Test]
        public void ToUnity_PreservesSubMillimetreDetailAt100km()
        {
            var world = new WorldPosition(100000.001, 0.0, 0.0);
            var offset = new WorldPosition(100000.0, 0.0, 0.0);

            Vector3 unity = WorldOriginMath.ToUnity(world, offset);

            Assert.AreEqual(0.001f, unity.x, 1e-6f, "double 相减必须保住 1mm 细节");
            Assert.Less(WorldPosition.Distance(world, WorldPosition.FromUnity(unity, offset)), 1e-6,
                "double+偏移方案的守恒误差必须在微米以下");
        }

        // ---- V6：为什么必须 double（两个数字要进施工报告）-------------------

        [Test]
        public void Precision_At100km_FloatDirectVersusDoubleWithOffset()
        {
            const double worldX = 100000.001; // 100km + 1mm
            const double offsetX = 100000.0;

            double ulp = WorldOriginMath.FloatUlpAt(100000.0);
            double floatDirectError = WorldOriginMath.FloatQuantizationError(worldX);

            var world = new WorldPosition(worldX, 0.0, 0.0);
            var offset = new WorldPosition(offsetX, 0.0, 0.0);
            Vector3 unity = WorldOriginMath.ToUnity(world, offset);
            double doubleError = System.Math.Abs(WorldPosition.FromUnity(unity, offset).X - worldX);

            TestContext.WriteLine(
                $"V6 @100km: float ULP = {ulp * 1000.0:F4} mm; float direct error = {floatDirectError * 1000.0:F4} mm; " +
                $"double+offset conservation error = {doubleError:E3} m ({doubleError * 1000.0:E3} mm)");

            Assert.AreEqual(0.0078125, ulp, 1e-12, "100km 处 float 的最小刻度是 2^-7 = 7.8125mm");
            Assert.AreEqual(0.001, floatDirectError, 1e-6, "1mm 的细节在 float 直接表示下被吃成 0（误差 1mm）");
            Assert.Less(doubleError, 1e-6, "double+偏移方案的误差应远小于 1 微米");
            Assert.Greater(floatDirectError, doubleError * 1000.0, "量化差距必须显著——这就是 double 的理由");
        }

        // ---- V3：重定位守恒（数学侧），与 PlayMode 的 Transform 侧呼应 ------

        [Test]
        public void Rebase_PreservesWorldPosition_AndRelativeDistance()
        {
            var cameraWorld = new WorldPosition(5000.0, 100.0, 0.0);
            var objectWorld = new WorldPosition(5003.0, 100.0, 2.0);
            WorldPosition offset = WorldPosition.Zero;

            Vector3 objectUnity = WorldOriginMath.ToUnity(objectWorld, offset);
            Vector3 cameraUnity = WorldOriginMath.ToUnity(cameraWorld, offset);
            float distanceBefore = Vector3.Distance(cameraUnity, objectUnity);

            Vector3 delta = WorldOriginMath.RebaseDelta(cameraWorld, offset, Threshold);
            Assert.AreNotEqual(Vector3.zero, delta);

            Vector3 objectAfter = objectUnity - delta;
            Vector3 cameraAfter = cameraUnity - delta;
            WorldPosition newOffset = offset + new WorldPosition(delta.x, delta.y, delta.z);

            WorldPosition objectWorldAfter = WorldOriginMath.ToWorld(objectAfter, newOffset);
            Assert.Less(WorldPosition.Distance(objectWorld, objectWorldAfter), 1e-3,
                "重定位前后注册物体的世界坐标必须守恒");
            Assert.AreEqual(distanceBefore, Vector3.Distance(cameraAfter, objectAfter), 1e-3f,
                "相机到物体的相对距离必须守恒");
            Assert.Less(cameraAfter.magnitude, 1e-2f, "相机必须回到原点附近");
            Assert.Less(WorldPosition.Distance(newOffset, cameraWorld), 1e-6, "原点必须落到相机位置");
        }

        [Test]
        public void Rebase_WithIllegalThreshold_IsNoOp()
        {
            var cameraWorld = new WorldPosition(50000.0, 0.0, 0.0);

            Assert.AreEqual(Vector3.zero, WorldOriginMath.RebaseDelta(cameraWorld, WorldPosition.Zero, 0.0));
            Assert.AreEqual(Vector3.zero, WorldOriginMath.RebaseDelta(cameraWorld, WorldPosition.Zero, -4000.0));
        }

        // ---- WorldPosition 基本运算 ----------------------------------------

        [Test]
        public void WorldPosition_Operators_And_Distance()
        {
            var a = new WorldPosition(100000.5, -2.25, 3.0);
            var b = new WorldPosition(1.5, 0.75, -3.0);

            Assert.AreEqual(new WorldPosition(100002.0, -1.5, 0.0), a + b);
            Assert.AreEqual(new WorldPosition(99999.0, -3.0, 6.0), a - b);
            Assert.AreEqual(new WorldPosition(-1.0, 2.0, -3.0), -new WorldPosition(1.0, -2.0, 3.0));
            Assert.AreEqual(new WorldPosition(2.0, -4.0, 6.0), new WorldPosition(1.0, -2.0, 3.0) * 2.0);
            Assert.AreEqual(new WorldPosition(2.0, -4.0, 6.0), 2.0 * new WorldPosition(1.0, -2.0, 3.0));

            Assert.AreEqual(5.0, WorldPosition.Distance(WorldPosition.Zero, new WorldPosition(3.0, 4.0, 0.0)), 1e-12);
            Assert.AreEqual(25.0, new WorldPosition(3.0, 4.0, 0.0).SqrMagnitude, 1e-12);
            Assert.AreEqual(5.0, new WorldPosition(3.0, 4.0, 0.0).Magnitude, 1e-12);
            Assert.IsTrue(new WorldPosition(1.0, 2.0, 3.0) == new WorldPosition(1.0, 2.0, 3.0));
            Assert.IsTrue(new WorldPosition(1.0, 2.0, 3.0) != new WorldPosition(1.0, 2.0, 3.1));
        }

        [Test]
        public void FloatUlpAt_GrowsWithMagnitude()
        {
            Assert.AreEqual(0.0, WorldOriginMath.FloatUlpAt(0.0), 0.0);
            Assert.Less(WorldOriginMath.FloatUlpAt(10.0), WorldOriginMath.FloatUlpAt(100000.0));
            Assert.AreEqual(0.0078125, WorldOriginMath.FloatUlpAt(100000.0), 1e-12);
            Assert.AreEqual(0.0625, WorldOriginMath.FloatUlpAt(1000000.0), 1e-12);
        }

        // ---- TASK-B-PLANET-07：viewRel 纯旋转构造（行星通道唯一边界延伸）----

        /// <summary>构造 T(t)·R 的合成视图矩阵：旋转取一个确定性的正交基（绕 Y 转 30°）。</summary>
        private static Matrix4x4 MakeViewWithTranslation(float tx, float ty, float tz)
        {
            Quaternion rotation = Quaternion.Euler(0f, 30f, 0f);
            Matrix4x4 view = Matrix4x4.TRS(new Vector3(tx, ty, tz), rotation, Vector3.one).inverse;
            return view;
        }

        [Test]
        public void ToCameraRelativeView_ZeroesTranslation_KeepsRotationBitwise()
        {
            Matrix4x4 view = MakeViewWithTranslation(9010000f, -2500.5f, 3.25f);
            Matrix4x4 viewRel = WorldOriginMath.ToCameraRelativeView(view);

            // 平移列恰好为 0（不残留 9010km 爬升段幅值——ULP(9.01e6)=1m 的抖动源）。
            Assert.AreEqual(0f, viewRel.m03, "平移列 m03 必须归零");
            Assert.AreEqual(0f, viewRel.m13, "平移列 m13 必须归零");
            Assert.AreEqual(0f, viewRel.m23, "平移列 m23 必须归零");
            Assert.AreEqual(1f, viewRel.m33, "m33（w 行）必须保持 1");

            // 旋转部分逐位不变（上 3×4 的其余元素）。
            for (int row = 0; row < 3; row++)
            {
                for (int column = 0; column < 3; column++)
                {
                    Assert.AreEqual(view[row, column], viewRel[row, column],
                        $"旋转元素 [{row},{column}] 必须逐位保持");
                }
            }
        }

        [Test]
        public void ToCameraRelativeView_TransformsDirectionsIdentically()
        {
            Matrix4x4 view = MakeViewWithTranslation(-1234.5f, 0.25f, 98765f);
            Matrix4x4 viewRel = WorldOriginMath.ToCameraRelativeView(view);

            // 方向向量（w=0）在两个矩阵下逐位一致——视图只差平移，旋转不动。
            Vector3[] directions =
            {
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f),
                new Vector3(0f, 0f, 1f),
                new Vector3(0.3f, -0.8f, 0.5f),
            };
            foreach (Vector3 direction in directions)
            {
                Vector4 extended = new Vector4(direction.x, direction.y, direction.z, 0f);
                Vector4 expected = view * extended;
                Vector4 actual = viewRel * extended;
                Assert.AreEqual(expected.x, actual.x, "方向 x 分量必须逐位一致");
                Assert.AreEqual(expected.y, actual.y, "方向 y 分量必须逐位一致");
                Assert.AreEqual(expected.z, actual.z, "方向 z 分量必须逐位一致");
                Assert.AreEqual(expected.w, actual.w, "方向 w 分量必须逐位一致");
            }
        }

        [Test]
        public void ToCameraRelativeView_PointTransform_DiffersExactlyByRemovedTranslation()
        {
            Matrix4x4 view = MakeViewWithTranslation(4321.5f, -8.25f, 15000f);
            Matrix4x4 viewRel = WorldOriginMath.ToCameraRelativeView(view);

            Vector4 point = new Vector4(12.5f, 7f, -10f, 1f);
            Vector4 full = view * point;
            Vector4 relative = viewRel * point;

            // 恒等式：全量视图的点变换 = 纯旋转视图的点变换 + 被移除的平移列
            // （TRS().inverse 的平移列是 Rᵀ·(−pos)，不是 −pos——所以从矩阵本身读取）。
            Vector3 removed = view.GetColumn(3);
            Assert.AreEqual(full.x, relative.x + removed.x, 1e-3f, "x 恒等式");
            Assert.AreEqual(full.y, relative.y + removed.y, 1e-3f, "y 恒等式");
            Assert.AreEqual(full.z, relative.z + removed.z, 1e-3f, "z 恒等式");
        }

        [Test]
        public void ToCameraRelativeView_Idempotent_AlreadyRelativeStaysPut()
        {
            Matrix4x4 view = MakeViewWithTranslation(5000f, 0f, 0f);
            Matrix4x4 once = WorldOriginMath.ToCameraRelativeView(view);
            Matrix4x4 twice = WorldOriginMath.ToCameraRelativeView(once);

            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    Assert.AreEqual(once[row, column], twice[row, column],
                        $"重复归零必须幂等 [{row},{column}]");
                }
            }
        }
    }
}
