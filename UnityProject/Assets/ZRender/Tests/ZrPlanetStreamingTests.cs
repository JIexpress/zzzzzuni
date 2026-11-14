// ZrPlanetStreamingTests.cs —— 行星 chunk 流式管理器的 EditMode 检验（TASK-B-PLANET-07）。
//
// 钉住四类账目（简报 §1-4）：
//   1. 创建/销毁/SetLod 与 ZR_Planet_GetStats 严格对账（createTotal−destroyTotal==liveChunks
//      恒等式 + 托管计数与原生计数增量逐项相等）；
//   2. 分帧预算生效（预算内每 tick 操作数有界、待办跨 tick 推进、近档硬帽）；
//   3. SetLod 迟滞决策与原生 PlanetLodLevel 边界逐式一致（恰在阈值不切换）；
//   4. 可见性口径（沿锚点方向的块最先服务）+ 通道门控纪律（关闭=整体关闭）。
// 第五类（TASK-B-PLANET-14）：排名重建阈值判据——纯海拔爬升 600m→258km 必须触发重建
//   （B-PLANET-11 缺陷锚点）、贴地微动 <500m 不触发（防过敏感）、阈值边界严格不等式、
//   旧判据静默而新判据触发的切向判别、稳态零重建、阈值常数选取的陈旧量界。
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

        /// <summary>带格数覆写的配置（B-PLANET-14 排名判据用例：cells=8/32 下阈值几何不同，
        /// 见 <see cref="ZrPlanetChunkStreamer.RebuildMoveThresholdMeters"/>）。</summary>
        private static ZrPlanetStreamConfig SmallConfig(uint cellsPerEdge, int maxWorkMicros, int maxNearOps)
        {
            ZrPlanetStreamConfig config = SmallConfig(maxWorkMicros, maxNearOps);
            config.CellsPerEdge = cellsPerEdge;
            return config;
        }

        /// <summary>chunk(face,cx,cy) 锚点方向的面坐标（远档 stride/2 中心，与表构建同式）。</summary>
        private static (double u, double v) AnchorUv(uint cx, uint cy)
        {
            return AnchorUv(2u, cx, cy);
        }

        /// <summary>可配格数版本（cellsPerEdge=2 时与上式逐位同一表达式）。</summary>
        private static (double u, double v) AnchorUv(uint cellsPerEdge, uint cx, uint cy)
        {
            const int stride = (int)ZrPlanetChunkStreamer.TierResFar - 1;
            int total = (int)cellsPerEdge * stride;
            double u = 2.0 * (double)(cx * stride + stride / 2) / total - 1.0;
            double v = 2.0 * (double)(cy * stride + stride / 2) / total - 1.0;
            return (u, v);
        }

        /// <summary>相机绝对坐标：沿 chunk(face,cx,cy) 锚点方向、对地高度 altitude。</summary>
        private static (double x, double y, double z) CameraOverAnchor(
            int face, uint cx, uint cy, double altitude)
        {
            return CameraOverAnchor(2u, face, cx, cy, altitude);
        }

        /// <summary>可配格数版本。</summary>
        private static (double x, double y, double z) CameraOverAnchor(
            uint cellsPerEdge, int face, uint cx, uint cy, double altitude)
        {
            (double u, double v) = AnchorUv(cellsPerEdge, cx, cy);
            ZrPlanetChunkStreamer.CubeSphereDir(face, u, v, out double dx, out double dy, out double dz);
            double r = ZrPlanetChunkStreamer.NativePlanetRadiusMeters + altitude;
            return (dx * r, dy * r, dz * r);
        }

        /// <summary>
        /// 反复 Tick 直到 LastOps 清空（收敛）。maxTicks 耗尽仍有待办视为失败（预算语义
        /// 或用例参数变化——不许静默吞掉未收敛态）。
        /// </summary>
        private static void Converge(ZrPlanetChunkStreamer streamer,
            double camX, double camY, double camZ, int maxTicks)
        {
            for (int i = 0; i < maxTicks && streamer.LastOps.Count > 0; i++)
            {
                streamer.Tick(camX, camY, camZ);
            }

            Assert.AreEqual(0, streamer.LastOps.Count,
                $"收敛循环（{maxTicks} tick）后仍有待办——预算语义或用例参数变化");
        }

        /// <summary>
        /// 相机位置处朝 +x 的单位切向量（e1 去掉沿相机方向 d 的分量后归一化；与相机位置
        /// 正交——沿它移动不改变离行星中心的距离到二阶小量）。B-PLANET-14 切向判据用例用。
        /// </summary>
        private static (double x, double y, double z) TangentTowardX(
            double posX, double posY, double posZ)
        {
            double len = Math.Sqrt(posX * posX + posY * posY + posZ * posZ);
            double dx = posX / len;
            double dy = posY / len;
            double dz = posZ / len;
            double dot = dx; // e1·d
            double tx = 1.0 - dot * dx;
            double ty = -dot * dy;
            double tz = -dot * dz;
            double invLen = 1.0 / Math.Sqrt(tx * tx + ty * ty + tz * tz);
            return (tx * invLen, ty * invLen, tz * invLen);
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

        // ---- 排名重建位移幅值判据（TASK-B-PLANET-14）-------------------------------------
        //
        // 缺陷：B-PLANET-07 重建判据 = turn>四分之一块角 ‖ lenDrift>5%，纯海拔爬升
        // 600m→258km 时 turn=0、lenDrift=4.04%<5%——期望集永远停留贴地集
        // （B-PLANET-11 实测 live=4，中档视野缺 chunk）。修复 = 加第三条判据
        // |ΔcamRel| > max(500m, 2%×chunk 弧长)（选型与常数依据见 streamer 类头注释块）。
        // 本组用例钉住：阈值口径（3 条）、缺陷复现锚点（爬升触发 ×2）、防过敏感锚点
        // （贴地微动 ×2）、边界严格性、跨 tick 累计、切向判据（旧判据静默而新判据触发）、
        // 稳态零重建、常数选取的陈旧量界。

        /// <summary>cells=32 阈值参考值：0.02 × 6371km × (π/2)/32 = 6254.71m（chunk 弧长
        /// 312.7km 的 2%——与 B-PLANET-11 施工报告引用的 chunk 弧长同式同值）。</summary>
        private const double DefaultGridThresholdMeters = 6254.7146;

        [Test]
        public void RebuildMoveThreshold_DefaultGrid_IsTwoPercentChunkArc()
        {
            double chunkArc = ZrPlanetChunkStreamer.NativePlanetRadiusMeters
                * (Math.PI / 2.0) / 32u;
            Assert.AreEqual(312_735.7, chunkArc, 0.1,
                "chunk 赤道弧长必须为 312.7km（B-PLANET-11 引用口径）");
            Assert.AreEqual(chunkArc * 0.02, ZrPlanetChunkStreamer.RebuildMoveThresholdMeters(32u), 1e-6,
                "生产格数阈值 = 2% × chunk 弧长");
            Assert.AreEqual(DefaultGridThresholdMeters,
                ZrPlanetChunkStreamer.RebuildMoveThresholdMeters(32u), 1e-3);
            Assert.Greater(ZrPlanetChunkStreamer.RebuildMoveThresholdMeters(32u), 500.0,
                "生产格数下 500m 地板不生效（2% 弧长项主导）");
        }

        [Test]
        public void RebuildMoveThreshold_FloorActive_OnDenseGrids()
        {
            // 地板边界：cells≤400 时 2% 弧长项 ≥ 500.4m（地板不生效），cells≥401 时弧长项
            // < 500m（地板接管，恰为 500.0）——贴地微动 <500m 永不触发的锚点在稠密格下成立。
            Assert.AreEqual(500.3772, ZrPlanetChunkStreamer.RebuildMoveThresholdMeters(400u), 1e-3,
                "cells=400：2% 弧长项仍略高于地板");
            Assert.AreEqual(500.0, ZrPlanetChunkStreamer.RebuildMoveThresholdMeters(401u), 1e-9,
                "cells=401：地板恰接管（弧长项 499.13m < 500m）");
            Assert.AreEqual(500.0, ZrPlanetChunkStreamer.RebuildMoveThresholdMeters(512u), 1e-9);
            Assert.AreEqual(500.0, ZrPlanetChunkStreamer.RebuildMoveThresholdMeters(4096u), 1e-9,
                "最稠密格（4096）下阈值 = 地板 500m");
        }

        [Test]
        public void RebuildMoveThreshold_MonotonicAcrossGrids_AndClimbAnchor()
        {
            // 单调不增（弧长项随 cells 收窄）+ 全域 ≥ 500m 地板 + 缺陷锚点：阈值上界
            // （cells=1 的 200.15km）必须低于 600m→258km 纯爬升位移 257.4km——任意合法
            // 格数下缺陷场景都触发。
            double previous = double.MaxValue;
            foreach (uint cells in new uint[] { 1u, 2u, 4u, 8u, 16u, 32u, 64u, 128u, 256u, 400u,
                401u, 512u, 1024u, 2048u, 4096u })
            {
                double threshold = ZrPlanetChunkStreamer.RebuildMoveThresholdMeters(cells);
                Assert.LessOrEqual(threshold, previous, $"cells={cells}：阈值必须随格数单调不增");
                Assert.GreaterOrEqual(threshold, 500.0, $"cells={cells}：阈值不得低于 500m 地板");
                previous = threshold;
            }

            Assert.AreEqual(200_150.8680, ZrPlanetChunkStreamer.RebuildMoveThresholdMeters(1u), 1e-3,
                "cells=1 极端粗格阈值 = 0.02·R·π/2");
            Assert.Less(ZrPlanetChunkStreamer.RebuildMoveThresholdMeters(1u), 257_400.0,
                "阈值全域上界必须低于 258km 纯爬升位移——缺陷锚点与格数无关");
        }

        [Test]
        public void Tick_PureAltitudeClimb600mTo258km_TriggersRebuild()
        {
            // 缺陷复现（cells=8 锥角口径）：纯径向爬升 turn=0、lenDrift=257.4km/6371.6km
            // =4.04%<5%——旧判据全静默（旧代码本用例必红）；位移 257.4km > 阈值 25018.86m
            // → 新判据触发。期望集随爬升扩张（几何复算：5 → 25 块）。
            var streamer = new ZrPlanetChunkStreamer();
            streamer.Initialize(SmallConfig(8u, 1_000_000, 8));

            try
            {
                (double gx, double gy, double gz) = CameraOverAnchor(8u, 0, 0, 0, 600.0);
                streamer.Tick(gx, gy, gz);
                Assert.AreEqual(1, streamer.RankingRebuildCount, "首 tick 建排名");
                Converge(streamer, gx, gy, gz, 64);
                long groundCreated = streamer.CreatedTotal;
                int groundDesired = streamer.DesiredCount;
                Assert.GreaterOrEqual(groundDesired, 1, "前置：贴地期望集非空");
                Assert.Greater(groundCreated, 0, "前置：贴地已建块");

                (double hx, double hy, double hz) = CameraOverAnchor(8u, 0, 0, 0, 258_000.0);
                streamer.Tick(hx, hy, hz);
                Assert.AreEqual(2, streamer.RankingRebuildCount,
                    "纯海拔爬升 600m→258km 必须触发排名重建（B-PLANET-11 缺陷锚点；lenDrift=4.04%<5%）");

                Converge(streamer, hx, hy, hz, 256);
                Assert.Greater(streamer.DesiredCount, groundDesired,
                    "爬升后期望集必须扩张（中档视野不再缺 chunk）");
                Assert.Greater(streamer.CreatedTotal, groundCreated, "扩张必须落成新块");
                Assert.IsTrue(streamer.LastAccountOk, "全程账目恒等");

                Assert.AreEqual(ZrPlanetNative.Ok, ZrPlanetNative.GetStats(out ZrPlanetStatsNative stats));
                Assert.AreEqual(stats.CreateTotal - stats.DestroyTotal, (ulong)stats.LiveChunks,
                    "原生账目恒等式");
                Assert.AreEqual((ulong)streamer.LiveCount, (ulong)stats.LiveChunks,
                    "托管/原生活跃数一致");
            }
            finally
            {
                streamer.Dispose();
            }
        }

        [Test]
        public void Tick_PureAltitudeClimb_ProductionGrid_GroundSetExpands()
        {
            // 生产格数（cells=32）+ 生产默认配置的缺陷复现：face0 中心方向贴地 600m 的期望集
            // 恰 4 块（几何复算 = B-PLANET-11 实测 live=4 同口径）；纯爬升到 258km 必须触发
            // 重建并把期望集扩张到中档覆盖（复算 112 块，断言 ≥100 留 libm 边界余量）。
            var streamer = new ZrPlanetChunkStreamer();
            streamer.Initialize(ZrPlanetStreamConfig.CreateDefault(0.0, 0.0, 0.0, 1337u));

            try
            {
                double r = ZrPlanetChunkStreamer.NativePlanetRadiusMeters;
                streamer.Tick(0.0, 0.0, r + 600.0);
                Converge(streamer, 0.0, 0.0, r + 600.0, 128);
                Assert.AreEqual(4, streamer.DesiredCount,
                    "贴地 600m（face0 中心方向）期望集恰 4 块——B-PLANET-11 live=4 缺陷态复现");
                Assert.AreEqual(4, streamer.LiveCount, "贴地活跃块与期望集一致");
                long groundCreated = streamer.CreatedTotal;

                streamer.Tick(0.0, 0.0, r + 258_000.0);
                Assert.AreEqual(2, streamer.RankingRebuildCount,
                    "生产格数下纯海拔爬升 258km 必须触发重建（本缺陷的主锚点）");

                Converge(streamer, 0.0, 0.0, r + 258_000.0, 512);
                Assert.GreaterOrEqual(streamer.DesiredCount, 100,
                    "258km 期望集必须扩张到中档覆盖（复算 112 块）");
                Assert.Greater(streamer.CreatedTotal, groundCreated, "扩张落成新块");
                Assert.IsTrue(streamer.LastAccountOk);

                Assert.AreEqual(ZrPlanetNative.Ok, ZrPlanetNative.GetStats(out ZrPlanetStatsNative stats));
                Assert.AreEqual(stats.CreateTotal - stats.DestroyTotal, (ulong)stats.LiveChunks);
                Assert.AreEqual((ulong)streamer.LiveCount, (ulong)stats.LiveChunks);
            }
            finally
            {
                streamer.Dispose();
            }
        }

        [Test]
        public void Tick_GroundTangentialMicroMove499m_DoesNotRebuild()
        {
            // 防过敏感锚点（切向臂）：贴地漫游 499m——turn=499/6371.6km=7.8e-5 rad ≪
            // 0.049（cells=8 四分之一块角）、lenDrift≈0、位移 499 < 阈值 25018.86m →
            // 三判据全静默；且无重建则排名冻结，不得产生任何操作。
            var streamer = new ZrPlanetChunkStreamer();
            streamer.Initialize(SmallConfig(8u, 1_000_000, 8));

            try
            {
                (double gx, double gy, double gz) = CameraOverAnchor(8u, 0, 0, 0, 600.0);
                streamer.Tick(gx, gy, gz);
                Converge(streamer, gx, gy, gz, 64);
                long created = streamer.CreatedTotal;
                int live = streamer.LiveCount;
                Assert.Greater(live, 0, "前置：贴地已建块");

                streamer.Tick(gx + 499.0, gy, gz);
                Assert.AreEqual(1, streamer.RankingRebuildCount,
                    "贴地微动 499m 不得触发重建（防过敏感锚点）");
                Assert.AreEqual(0, streamer.LastOps.Count, "排名冻结态不得产生任何操作");
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
        public void Tick_GroundRadialMicroMove499m_DoesNotRebuild()
        {
            // 防过敏感锚点（径向臂）：贴地爬升 499m——lenDrift=499/6371.6km=7.8e-5 ≪ 5%、
            // turn=0、位移 499 < 阈值 → 三判据全静默（径向微动是最贴近原缺陷方向的
            // 反向用例：阈值必须容忍它，否则流式在贴地抖动下空转）。
            var streamer = new ZrPlanetChunkStreamer();
            streamer.Initialize(SmallConfig(8u, 1_000_000, 8));

            try
            {
                (double gx, double gy, double gz) = CameraOverAnchor(8u, 0, 0, 0, 600.0);
                streamer.Tick(gx, gy, gz);
                Converge(streamer, gx, gy, gz, 64);
                long created = streamer.CreatedTotal;
                int live = streamer.LiveCount;

                (double ax, double ay, double az) = CameraOverAnchor(8u, 0, 0, 0, 1_099.0);
                streamer.Tick(ax, ay, az);
                Assert.AreEqual(1, streamer.RankingRebuildCount,
                    "贴地径向微动 499m 不得触发重建（防过敏感锚点）");
                Assert.AreEqual(0, streamer.LastOps.Count);
                Assert.AreEqual(live, streamer.LiveCount);
                Assert.AreEqual(created, streamer.CreatedTotal);
                Assert.IsTrue(streamer.LastAccountOk);
            }
            finally
            {
                streamer.Dispose();
            }
        }

        [Test]
        public void Tick_DisplacementThresholdBoundary_IsStrict()
        {
            // 阈值边界严格不等式（与 DecideLodLevel 的"恰在阈值不切换"同语义）：径向位移
            // 恰低于阈值（×(1−1e-6)）不触发、恰高于（×(1+1e-6)）触发——旧判据在两个位移
            // 下都静默（turn=0、lenDrift≈0.39%<5%），触发与否完全由新判据决定。
            var streamer = new ZrPlanetChunkStreamer();
            streamer.Initialize(SmallConfig(8u, 1_000_000, 8));

            try
            {
                (double gx, double gy, double gz) = CameraOverAnchor(8u, 0, 0, 0, 600.0);
                streamer.Tick(gx, gy, gz);
                Converge(streamer, gx, gy, gz, 64);

                double threshold = ZrPlanetChunkStreamer.RebuildMoveThresholdMeters(8u);
                (double bx, double by, double bz) =
                    CameraOverAnchor(8u, 0, 0, 0, 600.0 + threshold * (1.0 - 1e-6));
                streamer.Tick(bx, by, bz);
                Assert.AreEqual(1, streamer.RankingRebuildCount,
                    "恰低于位移阈值不得触发（严格不等式）");

                (double ax, double ay, double az) =
                    CameraOverAnchor(8u, 0, 0, 0, 600.0 + threshold * (1.0 + 1e-6));
                streamer.Tick(ax, ay, az);
                Assert.AreEqual(2, streamer.RankingRebuildCount,
                    "越过位移阈值必须触发（基线冻结在贴地 tick——跨阈以累计位移计）");
            }
            finally
            {
                streamer.Dispose();
            }
        }

        [Test]
        public void Tick_ContinuousClimb_AccumulatesToThreshold()
        {
            // 连续爬升的累计语义（缺陷第二形态——B-PLANET-11 "连续爬升跨不过首档"）：
            // 单帧 +20km 不过阈（<25018.86m，基线冻结不更新），继续爬到累计 +26km 跨阈
            // 触发——重建响应累计位移，不要求单帧跳变。
            var streamer = new ZrPlanetChunkStreamer();
            streamer.Initialize(SmallConfig(8u, 1_000_000, 8));

            try
            {
                (double gx, double gy, double gz) = CameraOverAnchor(8u, 0, 0, 0, 600.0);
                streamer.Tick(gx, gy, gz);
                Converge(streamer, gx, gy, gz, 64);

                (double mx, double my, double mz) = CameraOverAnchor(8u, 0, 0, 0, 20_600.0);
                streamer.Tick(mx, my, mz);
                Assert.AreEqual(1, streamer.RankingRebuildCount,
                    "累计 20km < 阈值：不得触发");

                (double hx, double hy, double hz) = CameraOverAnchor(8u, 0, 0, 0, 26_600.0);
                streamer.Tick(hx, hy, hz);
                Assert.AreEqual(2, streamer.RankingRebuildCount,
                    "累计 26km > 阈值：连续爬升必须跨阈触发");
            }
            finally
            {
                streamer.Dispose();
            }
        }

        [Test]
        public void Tick_TangentialDisplacement_RebuildFiresWithoutDesiredSetChange()
        {
            // 切向判据（旧判据静默而新判据触发的判别用例——旧代码本用例必红）：沿与相机
            // 方向正交的切向移动 30km——turn=0.0047 < 0.049（cells=8 四分之一块角）、
            // lenDrift=1.1e-5（纯切向，离星距离不变到二阶小量）、位移 30000 > 25018.86m
            // → 新判据触发。几何复算（TempLogs 探针）：期望集与迟滞带在移动前后均不变、
            // 活跃块距离变化 ≤30km 不跨 810/990km 迟滞带 → 重建 tick 零操作——重建事件
            // 只能经 RankingRebuildCount 观测（诊断计数存在的直接依据）。
            var streamer = new ZrPlanetChunkStreamer();
            streamer.Initialize(SmallConfig(8u, 1_000_000, 8));

            try
            {
                (double gx, double gy, double gz) = CameraOverAnchor(8u, 0, 0, 0, 600.0);
                streamer.Tick(gx, gy, gz);
                Converge(streamer, gx, gy, gz, 64);
                long created = streamer.CreatedTotal;
                long destroyed = streamer.DestroyedTotal;
                int live = streamer.LiveCount;
                int desired = streamer.DesiredCount;
                Assert.Greater(live, 0, "前置：贴地已建块");

                (double tx, double ty, double tz) = TangentTowardX(gx, gy, gz);
                streamer.Tick(gx + 30_000.0 * tx, gy + 30_000.0 * ty, gz + 30_000.0 * tz);

                Assert.AreEqual(2, streamer.RankingRebuildCount,
                    "切向 30km（旧判据全静默）必须经位移判据触发重建");
                Assert.AreEqual(0, streamer.LastOps.Count,
                    "期望集不变的重建不得产生操作——触发只能经计数观测");
                Assert.AreEqual(desired, streamer.DesiredCount, "期望集必须不变");
                Assert.AreEqual(live, streamer.LiveCount);

                Converge(streamer, gx + 30_000.0 * tx, gy + 30_000.0 * ty, gz + 30_000.0 * tz, 8);
                Assert.AreEqual(created, streamer.CreatedTotal, "收敛后不得有新创建");
                Assert.AreEqual(destroyed, streamer.DestroyedTotal, "收敛后不得有销毁");
                Assert.IsTrue(streamer.LastAccountOk);
            }
            finally
            {
                streamer.Dispose();
            }
        }

        [Test]
        public void Tick_StationaryCamera_RebuildsExactlyOnce()
        {
            // 稳态零重建（噪声免疫）：相机完全不动时只有首 tick 建排名，其后任意多 tick
            // 不得重建（若有逐 tick 重建，6144 块排序会常驻帧预算——回归即在此暴露）。
            var streamer = new ZrPlanetChunkStreamer();
            streamer.Initialize(SmallConfig(8u, 1_000_000, 8));

            try
            {
                (double gx, double gy, double gz) = CameraOverAnchor(8u, 0, 0, 0, 600.0);
                for (int i = 0; i < 32; i++)
                {
                    streamer.Tick(gx, gy, gz);
                }

                Assert.AreEqual(1, streamer.RankingRebuildCount,
                    "相机不动只允许首次建排名（稳态零重建）");
                Assert.AreEqual(0, streamer.LastOps.Count, "稳态无操作");
                Assert.IsTrue(streamer.LastAccountOk);
            }
            finally
            {
                streamer.Dispose();
            }
        }

        [Test]
        public void RebuildThreshold_StalenessBound_HoldsAcrossGrids()
        {
            // 常数选取依据的回归锁定：阈值 T 下"未重建径向位移 T"造成的地平线角增长
            // acos(R/(R+T))（低空最坏形态）必须 < 半块角 + 0.05 rad（unity_target.cpp (d)
            // 段剔除裕度）——陈旧排名的期望集偏差始终落在既有剔除包络内。改 0.02/500 任一
            // 常数若破坏本界，B-PLANET-11 的缺 chunk 覆盖缺口会重现（旧判据 5% lenDrift
            // 允许陈旧量至 0.3155 rad ≫ cells=32 的包络 0.0745 rad——正是缺陷机理）。
            double radius = ZrPlanetChunkStreamer.NativePlanetRadiusMeters;
            for (uint cells = 1u; cells <= 4096u; cells++)
            {
                double threshold = ZrPlanetChunkStreamer.RebuildMoveThresholdMeters(cells);
                double horizonAng = Math.Acos(Math.Min(1.0, radius / (radius + threshold)));
                double halfChunkAng = (Math.PI / 2.0) / cells * 0.5;
                Assert.Less(horizonAng, halfChunkAng + 0.05,
                    $"cells={cells}：阈值 T={threshold:F1}m 的排名陈旧量 {horizonAng:F6} rad " +
                    $"超出剔除包络（半块角 {halfChunkAng:F6} + 裕度 0.05）——常数选取依据被破坏");
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
