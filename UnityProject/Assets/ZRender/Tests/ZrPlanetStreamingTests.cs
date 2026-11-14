// ZrPlanetStreamingTests.cs —— 行星 chunk 流式管理器的 EditMode 检验（TASK-B-PLANET-07）。
//
// 钉住四类账目（简报 §1-4）：
//   1. 创建/销毁/SetLod 与 ZR_Planet_GetStats 严格对账（createTotal−destroyTotal==liveChunks
//      恒等式 + 托管计数与原生计数增量逐项相等）；
//   2. 分帧预算生效（预算内每 tick 操作数有界、待办跨 tick 推进、近档硬帽）；
//   3. SetLod 迟滞决策与原生 PlanetLodLevel 边界逐式一致（恰在阈值不切换）；
//   4. 可见性口径（沿锚点方向的块最先服务）+ 通道门控纪律（关闭=整体关闭）。
//
// 设备依赖：ChunkCreate/Destroy/SetLod/GetStats/SetWorld/SampleHeight/SetCameraOrigin
// 均为无 GPU 触点的主线程调用（设计稿 §8），EditMode 裸跑；真渲染路径由
// ZrPlanetWiringTests 的 D3D12 门控用例承载。
//
// 几何口径（断言可复现的前提）：cellsPerEdge=2 → 每面 2×2=4 块、每块跨面坐标 1.0
// （±半块角 22.5°）。锚点中心索引 = stride/2 = 7（远档 stride=15、total=30），
// 面坐标 u = 2·7/30 − 1 ≈ −0.5333（与 ZrPlanetChunkStreamer.BuildChunkTable 同式）。

using System;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Render.Bindings;
using ZFrame.Render.Planet;

namespace ZFrame.Render.Tests
{
    [TestFixture]
    public class ZrPlanetStreamingTests
    {
        [SetUp]
        public void SetUp()
        {
            ZrPlanetChannel.ResetForTests();
            ZrPlanetNative.ClearAll();
        }

        [TearDown]
        public void TearDown()
        {
            ZrPlanetChannel.ResetForTests();
            ZrPlanetNative.ClearAll();
        }

        /// <summary>小表配置：cellsPerEdge=2 → 全星 24 块，锚点表/排名毫秒级完成。</summary>
        private static ZrPlanetStreamConfig SmallConfig()
        {
            ZrPlanetStreamConfig config = ZrPlanetStreamConfig.CreateDefault(0.0, 0.0, 0.0, 1337u);
            config.CellsPerEdge = 2u;
            return config;
        }

        /// <summary>带预算覆写的小表配置（config 是可变值类型，返回副本避免共享）。</summary>
        private static ZrPlanetStreamConfig SmallConfig(int maxWorkMicros, int maxNearOps)
        {
            ZrPlanetStreamConfig config = SmallConfig();
            config.MaxWorkMicrosPerTick = maxWorkMicros;
            config.MaxNearTierOpsPerTick = maxNearOps;
            return config;
        }

        /// <summary>chunk(face,cx,cy) 锚点方向的面坐标（远档 stride/2 中心，与表构建同式）。</summary>
        private static (double u, double v) AnchorUv(uint cx, uint cy)
        {
            const int stride = (int)ZrPlanetChunkStreamer.TierResFar - 1;
            const int total = 2 * stride; // cellsPerEdge = 2
            double u = 2.0 * (double)(cx * stride + stride / 2) / total - 1.0;
            double v = 2.0 * (double)(cy * stride + stride / 2) / total - 1.0;
            return (u, v);
        }

        /// <summary>相机绝对坐标：沿 chunk(face,cx,cy) 锚点方向、对地高度 altitude。</summary>
        private static (double x, double y, double z) CameraOverAnchor(
            int face, uint cx, uint cy, double altitude)
        {
            (double u, double v) = AnchorUv(cx, cy);
            ZrPlanetChunkStreamer.CubeSphereDir(face, u, v, out double dx, out double dy, out double dz);
            double r = ZrPlanetChunkStreamer.NativePlanetRadiusMeters + altitude;
            return (dx * r, dy * r, dz * r);
        }

        // ---- SetLod 迟滞决策（与原生 PlanetLodLevel 逐式镜像）--------------------------

        [Test]
        public void DecideLodLevel_FarSide_BoundaryIsStrict()
        {
            // prevLod=0（远档）：dist < nearDist − hyst 才进近档，恰等于阈值不切换。
            Assert.AreEqual(1, ZrPlanetChunkStreamer.DecideLodLevel(0, 810_000.0 - 1.0, 900_000.0, 90_000.0),
                "810km−ε 应进近档");
            Assert.AreEqual(0, ZrPlanetChunkStreamer.DecideLodLevel(0, 810_000.0, 900_000.0, 90_000.0),
                "恰在 810km 不切换（严格不等式，与原生 doctest 同边界）");
            Assert.AreEqual(0, ZrPlanetChunkStreamer.DecideLodLevel(0, 900_000.0, 900_000.0, 90_000.0),
                "900km 在迟滞带内保持远档");
        }

        [Test]
        public void DecideLodLevel_NearSide_BoundaryIsStrict()
        {
            // prevLod=1（近档）：dist > nearDist + hyst 才退远档，恰等于阈值不切换。
            Assert.AreEqual(1, ZrPlanetChunkStreamer.DecideLodLevel(1, 990_000.0, 900_000.0, 90_000.0),
                "恰在 990km 不退档");
            Assert.AreEqual(0, ZrPlanetChunkStreamer.DecideLodLevel(1, 990_000.0 + 1.0, 900_000.0, 90_000.0),
                "990km+ε 应退远档");
            Assert.AreEqual(1, ZrPlanetChunkStreamer.DecideLodLevel(1, 0.0, 900_000.0, 90_000.0),
                "贴地保持近档");
        }

        [Test]
        public void DecideLodLevel_HysteresisBandKeepsPreviousTier()
        {
            Assert.AreEqual(0, ZrPlanetChunkStreamer.DecideLodLevel(0, 900_000.0, 900_000.0, 90_000.0));
            Assert.AreEqual(1, ZrPlanetChunkStreamer.DecideLodLevel(1, 900_000.0, 900_000.0, 90_000.0));
            Assert.AreEqual(0, ZrPlanetChunkStreamer.DecideLodLevel(0, 950_000.0, 900_000.0, 90_000.0));
            Assert.AreEqual(1, ZrPlanetChunkStreamer.DecideLodLevel(1, 850_000.0, 900_000.0, 90_000.0));
        }

        // ---- CubeSphereDir 镜像（锚点表的地基）------------------------------------------

        [Test]
        public void CubeSphereDir_FaceCenters_MatchNativeBasis()
        {
            ZrPlanetChunkStreamer.CubeSphereDir(0, 0.0, 0.0, out double x0, out double y0, out double z0);
            Assert.AreEqual(0.0, x0, 1e-15);
            Assert.AreEqual(0.0, y0, 1e-15);
            Assert.AreEqual(1.0, z0, 1e-15);

            ZrPlanetChunkStreamer.CubeSphereDir(4, 0.0, 0.0, out double x4, out double y4, out double z4);
            Assert.AreEqual(0.0, x4, 1e-15);
            Assert.AreEqual(1.0, y4, 1e-15);
            Assert.AreEqual(0.0, z4, 1e-15);

            ZrPlanetChunkStreamer.CubeSphereDir(5, 0.0, 0.0, out double x5, out double y5, out double z5);
            Assert.AreEqual(-1.0, y5, 1e-15);
        }

        [Test]
        public void CubeSphereDir_SharedCubeEdge_IsBitwiseIdenticalAcrossFaces()
        {
            // face0 (u=+1, v) 与 face1 (u=−1, v) 是同一条立方体棱：p 分量同为小整数加乘
            // → 归一化后逐位一致（跨面棱无缝的构造性基础，与原生 CubeSphereDir 同性质）。
            for (int v = 0; v <= 4; v++)
            {
                double t = v * 0.5 - 1.0;
                ZrPlanetChunkStreamer.CubeSphereDir(0, 1.0, t, out double ax, out double ay, out double az);
                ZrPlanetChunkStreamer.CubeSphereDir(1, -1.0, t, out double bx, out double by, out double bz);
                Assert.AreEqual(ax, bx, $"face0/face1 棱方向 x 必须逐位一致 (v={t})");
                Assert.AreEqual(ay, by, $"face0/face1 棱方向 y 必须逐位一致 (v={t})");
                Assert.AreEqual(az, bz, $"face0/face1 棱方向 z 必须逐位一致 (v={t})");
            }
        }

        // ---- 账目对账（简报 §1-4 第 1 类）------------------------------------------------

        [Test]
        public void Tick_CreateAndDestroy_ReconcileWithNativeStats()
        {
            var streamer = new ZrPlanetChunkStreamer();
            streamer.Initialize(SmallConfig());

            try
            {
                ZrPlanetNative.GetStats(out ZrPlanetStatsNative before);

                // 相机沿 face0 中心块锚点方向、600km 高：朝向锥内应有块被创建。
                (double cx0, double cy0, double cz0) = CameraOverAnchor(0, 0, 0, 600_000.0);
                streamer.Tick(cx0, cy0, cz0);
                Assert.Greater(streamer.CreatedTotal, 0, "面向相机的锥内必须创建出块");
                Assert.IsTrue(streamer.LastAccountOk, "首次 Tick 后账目恒等式必须成立");

                ZrPlanetNative.GetStats(out ZrPlanetStatsNative afterCreate);
                Assert.AreEqual((ulong)streamer.CreatedTotal,
                    afterCreate.CreateTotal - before.CreateTotal,
                    "托管 Create 计数必须与原生 createTotal 增量一致");
                Assert.AreEqual((ulong)streamer.LiveCount, (ulong)afterCreate.LiveChunks,
                    "托管活跃数必须与原生 liveChunks 一致");
                Assert.AreEqual(afterCreate.CreateTotal - afterCreate.DestroyTotal,
                    (ulong)afterCreate.LiveChunks, "createTotal − destroyTotal == liveChunks");

                // 相机沿对面（face2 = −Z）的对应锚点：旧块出局、新锥创建。
                (double cx2, double cy2, double cz2) = CameraOverAnchor(2, 0, 0, 600_000.0);
                streamer.Tick(cx2, cy2, cz2);
                Assert.Greater(streamer.DestroyedTotal, 0, "背向相机方向必须销毁旧块");
                Assert.IsTrue(streamer.LastAccountOk);

                ZrPlanetNative.GetStats(out ZrPlanetStatsNative afterMove);
                Assert.AreEqual((ulong)streamer.CreatedTotal,
                    afterMove.CreateTotal - before.CreateTotal);
                Assert.AreEqual((ulong)streamer.DestroyedTotal,
                    afterMove.DestroyTotal - before.DestroyTotal,
                    "托管 Destroy 计数必须与原生 destroyTotal 增量一致");
                Assert.AreEqual(afterMove.CreateTotal - afterMove.DestroyTotal,
                    (ulong)afterMove.LiveChunks, "移动后恒等式仍成立");
                Assert.AreEqual((ulong)streamer.LiveCount, (ulong)afterMove.LiveChunks);
            }
            finally
            {
                streamer.Dispose();
            }
        }

        [Test]
        public void Tick_IdempotentCamera_NoDuplicateCreates()
        {
            var streamer = new ZrPlanetChunkStreamer();
            streamer.Initialize(SmallConfig());

            try
            {
                (double cx, double cy, double cz) = CameraOverAnchor(0, 0, 0, 600_000.0);
                streamer.Tick(cx, cy, cz);
                Assert.Greater(streamer.CreatedTotal, 0);

                // 先推进到收敛（相机不动，预算/硬帽允许的待办全部落地）。
                for (int i = 0; i < 64 && streamer.LastOps.Count > 0; i++)
                {
                    streamer.Tick(cx, cy, cz);
                }

                long created = streamer.CreatedTotal;
                int live = streamer.LiveCount;
                Assert.Greater(created, 0);

                // 收敛后相机不动：再 Tick 不应产生任何 Create（(face,cx,cy) 去重 + 迟滞稳定）。
                streamer.Tick(cx, cy, cz);
                foreach (ZrPlanetStreamOp op in streamer.LastOps)
                {
                    Assert.AreNotEqual(ZrPlanetStreamOpKind.Create, op.Kind,
                        "相机不动时不应重复创建同一 (face,cx,cy)");
                }

                Assert.AreEqual(live, streamer.LiveCount, "活跃数必须稳定");
                Assert.AreEqual(created, streamer.CreatedTotal, "Create 计数必须稳定");
                Assert.IsTrue(streamer.LastAccountOk);
            }
            finally
            {
                streamer.Dispose();
            }
        }

        [Test]
        public void Dispose_ClearsAllNativeChunks()
        {
            var streamer = new ZrPlanetChunkStreamer();
            streamer.Initialize(SmallConfig());
            (double cx, double cy, double cz) = CameraOverAnchor(0, 0, 0, 600_000.0);
            streamer.Tick(cx, cy, cz);
            Assert.Greater(streamer.LiveCount, 0, "前置：确实建出了块");
            ZrPlanetNative.GetStats(out ZrPlanetStatsNative before);
            Assert.Greater(before.LiveChunks, 0u);

            streamer.Dispose();
            ZrPlanetNative.GetStats(out ZrPlanetStatsNative after);
            Assert.AreEqual(0u, after.LiveChunks, "Dispose 后原生 liveChunks 必须清零");
        }

        // ---- 分帧预算（简报 §1-1 / B-PLANET-04 E8 成本口径）------------------------------

        [Test]
        public void Tick_BudgetCapsOpsPerTick_AndDeferredWorkProgresses()
        {
            var streamer = new ZrPlanetChunkStreamer();
            ZrPlanetStreamConfig config = SmallConfig(800, 1); // 远档 700×2 > 800 → 每 tick ≤1 块
            streamer.Initialize(config);

            try
            {
                (double cx, double cy, double cz) = CameraOverAnchor(0, 0, 0, 600_000.0);
                streamer.Tick(cx, cy, cz);
                Assert.LessOrEqual(streamer.LastOps.Count, 1,
                    "预算 800µs（远档 700µs）下每 tick 至多 1 个操作");
                Assert.Greater(streamer.LiveCount, 0, "预算不阻止首个远档块推进");

                // 反复 Tick：操作数持续有界，活跃数单调不减（相机不动、无销毁）。
                // 600km 高下最近块 dist≈600km < 810km 属近档（8500µs > 800µs 预算）→ 恒被
                // 预算延后；其余期望块为远档（700µs ≤ 预算）逐 tick 推进——这正钉住
                // "预算生效且待办不丢"两个性质。
                int live = streamer.LiveCount;
                long created = streamer.CreatedTotal;
                for (int i = 0; i < 64; i++)
                {
                    streamer.Tick(cx, cy, cz);
                    Assert.LessOrEqual(streamer.LastOps.Count, 1, $"第 {i + 2} tick 操作数仍有界");
                    Assert.GreaterOrEqual(streamer.LiveCount, live, "活跃数不许回退（相机未动）");
                    live = streamer.LiveCount;
                    created = streamer.CreatedTotal;
                }

                Assert.AreEqual(live, (int)created, "收敛后托管活跃数与 Create 计数一致");
                Assert.IsTrue(streamer.LastAccountOk, "预算流式全程账目恒等");

                ZrPlanetNative.GetStats(out ZrPlanetStatsNative stats);
                Assert.AreEqual((ulong)streamer.CreatedTotal, stats.CreateTotal - stats.DestroyTotal,
                    "收敛后与原生账目一致");
            }
            finally
            {
                streamer.Dispose();
            }
        }

        [Test]
        public void Tick_NearCapAllowsAtMostOneNearOpPerTick()
        {
            var streamer = new ZrPlanetChunkStreamer();
            streamer.Initialize(SmallConfig(1_000_000, 1)); // 预算放行一切，只留近档硬帽

            try
            {
                // 相机贴在 chunk(0,0) 锚点正上方 2m：该块 dist ≈ |2m−h| < 810km → 近档；
                // 其余块与相机方向夹角 ≥ 33°（cells=2 的半块角 22.5° + 锚点偏心），
                // 在贴地视锥（horizon≈5km → 期望锥 ≈25°）之外 → 期望集恰为 1 块。
                (double cx, double cy, double cz) = CameraOverAnchor(0, 0, 0, 2.0);
                streamer.Tick(cx, cy, cz);
                int nearOps = 0;
                foreach (ZrPlanetStreamOp op in streamer.LastOps)
                {
                    bool isNearCreate = op.Kind == ZrPlanetStreamOpKind.Create
                        && op.LodLevel == ZrPlanetChunkStreamer.LodNear;
                    bool isNearUpgrade = op.Kind == ZrPlanetStreamOpKind.SetLodUpgrade;
                    if (isNearCreate || isNearUpgrade)
                    {
                        nearOps++;
                    }
                }

                Assert.LessOrEqual(nearOps, 1, "近档硬帽：每 tick 至多 1 个近档操作");
                Assert.AreEqual(1, streamer.LastOps.Count,
                    "贴地期望集恰 1 块：近档创建被硬帽放行（成本 8500µs ≤ 预算 1e6µs）");
                Assert.AreEqual(1, streamer.LiveCount);
            }
            finally
            {
                streamer.Dispose();
            }
        }

        [Test]
        public void Tick_ZeroBudget_StopsAllCreates()
        {
            var streamer = new ZrPlanetChunkStreamer();
            streamer.Initialize(SmallConfig(0, 1)); // 极端预算：什么都不放行

            try
            {
                (double cx, double cy, double cz) = CameraOverAnchor(0, 0, 0, 600_000.0);
                streamer.Tick(cx, cy, cz);
                Assert.AreEqual(0, streamer.LiveCount, "零预算下不得创建（尖峰钳制的极端形态）");
                Assert.AreEqual(0, streamer.CreatedTotal);
                Assert.IsTrue(streamer.LastAccountOk);
            }
            finally
            {
                streamer.Dispose();
            }
        }

        // ---- 可见性口径 ------------------------------------------------------------------

        [Test]
        public void Tick_CameraOnAnchorDirection_AnchorChunkServedFirst()
        {
            var streamer = new ZrPlanetChunkStreamer();
            streamer.Initialize(SmallConfig(700, 1)); // 每 tick 恰好 1 个远档块（确定性单步）

            try
            {
                // 相机严格沿 chunk(0,0) 锚点方向、1200km 高：该块 cos≈1 全场最高（排名 =
                // cos 降序），且 dist≈1200km ∈ 远档（>990km 迟滞上限）——预算单步（700µs
                // < 近档 8500µs）下第一个被服务的必须是它。
                (double cx, double cy, double cz) = CameraOverAnchor(0, 0, 0, 1_200_000.0);
                streamer.Tick(cx, cy, cz);
                Assert.AreEqual(1, streamer.LastOps.Count, "预算 700µs = 每 tick 恰一个远档块");
                ZrPlanetStreamOp first = streamer.LastOps[0];
                Assert.AreEqual(ZrPlanetStreamOpKind.Create, first.Kind);
                Assert.AreEqual(0u, first.FaceId, "最近块必属 face0");
                Assert.AreEqual(0u, first.Cx, "锚点方向上的块最先服务");
                Assert.AreEqual(0u, first.Cy, "锚点方向上的块最先服务");

                // 收敛后把相机移到正对面：原朝向块全部出局（销毁迟滞带外）。
                for (int i = 0; i < 64 && streamer.LastOps.Count > 0; i++)
                {
                    streamer.Tick(cx, cy, cz);
                }

                long destroyedBefore = streamer.DestroyedTotal;
                (double ox, double oy, double oz) = CameraOverAnchor(2, 0, 0, 600_000.0);
                streamer.Tick(ox, oy, oz);
                Assert.Greater(streamer.DestroyedTotal, destroyedBefore,
                    "背向移动必须销毁原朝向块（半块角迟滞带之外）");
                Assert.IsTrue(streamer.LastAccountOk);
            }
            finally
            {
                streamer.Dispose();
            }
        }

        // ---- 门控与契约 ------------------------------------------------------------------

        [Test]
        public void Initialize_Twice_ThrowsOneShotContract()
        {
            var streamer = new ZrPlanetChunkStreamer();
            streamer.Initialize(SmallConfig());
            try
            {
                Assert.Throws<InvalidOperationException>(() => streamer.Initialize(SmallConfig()),
                    "G6：SetWorld 为初始化期唯一托管调用点——重复 Initialize 必须显式失败");
            }
            finally
            {
                streamer.Dispose();
            }
        }

        [Test]
        public void Initialize_InvalidConfig_Throws()
        {
            var streamer = new ZrPlanetChunkStreamer();
            ZrPlanetStreamConfig badCells = SmallConfig();
            badCells.CellsPerEdge = 0u;
            Assert.Throws<ArgumentException>(() => streamer.Initialize(badCells),
                "cellsPerEdge=0 必须拒绝");

            ZrPlanetStreamConfig badHyst = SmallConfig();
            badHyst.HystMeters = -1.0;
            Assert.Throws<ArgumentException>(() => streamer.Initialize(badHyst),
                "负迟滞必须拒绝（与 ShouldRebase 非法阈值同哲学）");

            ZrPlanetStreamConfig badCenter = SmallConfig();
            badCenter.CenterAbsY = double.NaN;
            Assert.Throws<ArgumentException>(() => streamer.Initialize(badCenter),
                "NaN 中心必须拒绝（ERR_NONFINITE 同域）");
        }

        [Test]
        public void Tick_BeforeInitialize_Throws()
        {
            var streamer = new ZrPlanetChunkStreamer();
            Assert.Throws<InvalidOperationException>(() => streamer.Tick(0.0, 0.0, 0.0));
        }

        // ---- 通道缝（门控纪律的托管状态机）-----------------------------------------------

        [Test]
        public void Channel_ConsumeOnce_AndDisableStopsConsumption()
        {
            Matrix4x4 view = Matrix4x4.TRS(new Vector3(1000f, 0f, 0f), Quaternion.identity, Vector3.one)
                .inverse;
            ZrPlanetChannel.Enable();
            ZrPlanetChannel.PublishCameraRelativeView(view);
            Assert.IsTrue(ZrPlanetChannel.IsEnabled);
            Assert.IsTrue(ZrPlanetChannel.TryConsumeCameraRelativeView(out Matrix4x4 taken));
            Assert.AreEqual(view.m00, taken.m00, "取出的必须是发布的那份 viewRel");
            Assert.AreEqual(view.m03, taken.m03, "取出的必须是发布的那份 viewRel（逐元素）");
            Assert.IsFalse(ZrPlanetChannel.TryConsumeCameraRelativeView(out _),
                "consume-once：取走即清，防止陈旧矩阵被复用");

            // 整体关闭后：即使再发布也取不到（门控纪律——不许半启用）。
            ZrPlanetChannel.Disable("测试：整体关闭");
            ZrPlanetChannel.PublishCameraRelativeView(view);
            Assert.IsFalse(ZrPlanetChannel.IsEnabled);
            Assert.IsFalse(ZrPlanetChannel.TryConsumeCameraRelativeView(out _),
                "关闭状态下消费必须失败");
        }

        [Test]
        public void Channel_DisableClearsPublishedView_ReEnableDoesNotResurrect()
        {
            ZrPlanetChannel.Enable();
            ZrPlanetChannel.PublishCameraRelativeView(Matrix4x4.identity);
            ZrPlanetChannel.Disable("测试：清空");
            ZrPlanetChannel.PublishCameraRelativeView(Matrix4x4.identity);
            ZrPlanetChannel.Enable();
            Assert.IsFalse(ZrPlanetChannel.TryConsumeCameraRelativeView(out _),
                "关闭期间发布的 viewRel 必须被丢弃（不许半启用状态残留）");
        }
    }
}
