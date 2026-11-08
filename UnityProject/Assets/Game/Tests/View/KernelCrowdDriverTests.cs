// KernelCrowdDriverTests.cs —— 千人演练场 EditMode 真 dll roundtrip（TASK-A-R9-13 建，
// TASK-A-AND-08 扩充：Health/Alive 迁移读回、kernel 真实性断言、每帧 P/Invoke 预算、
// kernel vs C# 数组 A/B 双口径）。
// 与 B-R9-17 的 ZKernelWorldTests 一样真实加载 Assets/Plugins/ZRender/zrender.dll，
// 但组件/compId/驱动全部是玩法侧的定义：1000 实体五列求和、迁移读回、
// 销毁句柄失效、坏 compId 拒绝、ITickQueue 注册/注销、分段耗时。

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Game.View.Effects;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Core;
using ZFrame.Render.Bindings;
using ZFrame.View;

namespace Game.View.Tests
{
    /// <summary>玩法侧 DataKernel roundtrip + 驱动自测（真 dll，不 mock）。</summary>
    [TestFixture]
    public sealed class KernelCrowdDriverTests
    {
        private const uint CompPos = KernelCrowdIds.Position;
        private const uint CompVel = KernelCrowdIds.Velocity;
        private const uint CompTag = KernelCrowdIds.Tag;
        private const uint CompHealth = KernelCrowdIds.Health;
        private const uint CompAlive = KernelCrowdIds.Alive;

        private static ZkMask FullMask()
        {
            return ZkMask.Of(CompPos).With(CompVel).With(CompTag);
        }

        private static ZKernelWorld NewWorld()
        {
            var world = new ZKernelWorld();
            world.RegisterComponent<CrowdPosition>(CompPos);
            world.RegisterComponent<CrowdVelocity>(CompVel);
            world.RegisterComponent<CrowdTag>(CompTag);
            return world;
        }

        [Test]
        public void Roundtrip_1000Entities_ThreeColumnSumMatches()
        {
            using (ZKernelWorld world = NewWorld())
            {
                ZkMask mask = FullMask();
                const int count = 1000; // 28B/行 → chunk 容量 585 → 2 个 chunk
                for (int i = 0; i < count; ++i)
                {
                    ZkEntity e = world.Spawn(mask);
                    world.SetComponent(e, CompPos, new CrowdPosition(i, 0.25f, 0f));
                    world.SetComponent(e, CompVel, new CrowdVelocity(1f, 0f, 0f));
                    world.SetComponent(e, CompTag, new CrowdTag((uint)i));
                }

                double sumPos = 0.0;
                ulong sumTag = 0UL;
                uint batches = 0;
                var pos = new float[count * 3];
                var tag = new int[count];
                uint total = world.EachBatches(mask, (columns, rows) =>
                {
                    Marshal.Copy(columns[0], pos, 0, (int)rows * 3);
                    Marshal.Copy(columns[2], tag, 0, (int)rows);
                    for (uint r = 0; r < rows; ++r)
                    {
                        sumPos += pos[r * 3];
                        sumTag += (uint)tag[r];
                    }

                    ++batches;
                });

                Assert.AreEqual((uint)count, total, "游标累计交付行数必须 = 1000");
                Assert.GreaterOrEqual(batches, 2u, "1000 > chunk 容量 → 必须跨 chunk 推进");
                Assert.AreEqual(499500.0, sumPos, "pos.X = 0..999 精确求和");
                Assert.AreEqual(499500UL, sumTag, "tag = 0..999 精确求和");
                Assert.AreEqual((ulong)count, world.GetStats().EntityCount);
            }
        }

        [Test]
        public void Migration_AddVelocity_ZeroInitThenReadBack()
        {
            using (ZKernelWorld world = NewWorld())
            {
                ZkMask mask = ZkMask.Of(CompPos).With(CompTag); // 故意不含 vel
                ZkEntity e = world.Spawn(mask);
                world.SetComponent(e, CompPos, new CrowdPosition(7f, 0.25f, 0f));
                world.SetComponent(e, CompTag, new CrowdTag(3u));

                world.AddComponent(e, CompVel); // initData 空 = 零初始化（合法）
                CrowdVelocity zero = world.GetComponent<CrowdVelocity>(e, CompVel);
                Assert.AreEqual(0f, zero.Dx);
                Assert.AreEqual(0f, zero.Dy);
                Assert.AreEqual(0f, zero.Dz);

                world.SetComponent(e, CompVel, new CrowdVelocity(2f, 3f, 4f));
                CrowdVelocity written = world.GetComponent<CrowdVelocity>(e, CompVel);
                Assert.AreEqual(2f, written.Dx);
                Assert.AreEqual(4f, written.Dz);

                CrowdPosition kept = world.GetComponent<CrowdPosition>(e, CompPos);
                Assert.AreEqual(7f, kept.X, "迁移后原句柄仍有效、共享列保留");
                Assert.AreEqual(1UL, world.GetStats().Migrations);
            }
        }

        [Test]
        public void Destroy_StaleHandleRejected()
        {
            using (ZKernelWorld world = NewWorld())
            {
                ZkEntity e = world.Spawn(FullMask());
                world.SetComponent(e, CompPos, new CrowdPosition(1f, 2f, 3f));

                Assert.IsTrue(world.Destroy(e));
                Assert.IsFalse(world.Destroy(e), "重复销毁返回 false（不抛）");
                Assert.IsFalse(world.TryGetComponent(e, CompPos, out CrowdPosition _));
                Assert.Catch<ZKernelException>(() => world.GetComponent<CrowdPosition>(e, CompPos));
                Assert.Catch<ZKernelException>(() =>
                    world.SetComponent(e, CompPos, new CrowdPosition(9f, 9f, 9f)));
                Assert.AreEqual(0UL, world.GetStats().EntityCount);
            }
        }

        [Test]
        public void BadCompId_Rejected()
        {
            using (ZKernelWorld world = NewWorld())
            {
                ZKernelException ex = Assert.Catch<ZKernelException>(() =>
                    world.RegisterComponent<CrowdPosition>(128u));
                Assert.AreEqual(ZKernelErrorCodes.ErrRange, ex.Code, "compId 越 0..127 由原生 ErrRange 拒绝");

                Assert.Catch<ArgumentOutOfRangeException>(() => ZkMask.Of(200u));

                ZkEntity e = world.Spawn(FullMask());
                Assert.Catch<ArgumentException>(() =>
                    world.SetComponent(e, 999u, new CrowdPosition(1f, 1f, 1f)),
                    "未注册 compId 在托管侧即被拒绝");
            }
        }

        [Test]
        public void Driver_Tick_Integrates_And_Dispose_Unregisters()
        {
            var queue = new TickQueue();
            Assert.AreEqual(0, queue.Count(TickStage.Logic));

            var driver = new KernelCrowdDriver();
            driver.Initialize(queue);
            Assert.AreEqual(1, queue.Count(TickStage.Logic), "驱动注册到 Logic 档（暂停冻结）");
            Assert.AreEqual(1000UL, driver.World.GetStats().EntityCount);
            Assert.AreEqual(499500.0, driver.PullSum(), "初始 X = 序号，求和精确");

            driver.Tick(0.016f);
            Assert.AreEqual(1000u, driver.LastTotalRows, "每帧游标读回全部 1000 行");
            Assert.Less(driver.LastKernelIterationUs, 100000.0, "内核迭代 > 100ms = 病态回归");
            Assert.Greater(driver.PullSum(), 499500.0, "积分后 X 增加（速度为正、未回绕）");

            driver.Dispose();
            Assert.IsTrue(driver.IsDisposed);
            Assert.AreEqual(0, queue.Count(TickStage.Logic), "Dispose 注销 tick");
        }

        [Test]
        public void Dispose_ReleasesWorldHandle_NoLeak()
        {
            var queue = new TickQueue();
            // 原生句柄表上限 8 世界：连续 10 次创建/销毁不抛 = 槽位确实被释放。
            for (int i = 0; i < 10; ++i)
            {
                var driver = new KernelCrowdDriver(50);
                driver.Initialize(queue);
                Assert.AreEqual(50UL, driver.World.GetStats().EntityCount);
                driver.Dispose();
                Assert.IsTrue(driver.IsDisposed);
            }

            Assert.AreEqual(0, queue.Count(TickStage.Logic));
        }

        // ---- A-AND-08：Health/Alive 迁移、kernel 真实性、P/Invoke 预算、A/B 双口径 ----

        /// <summary>五列组件注册 + Health/Alive 初始数据在 kernel 里的确定性读回。</summary>
        [Test]
        public void Migration_HealthAlive_RegisteredAndInitialized()
        {
            var queue = new TickQueue();
            using (var driver = new KernelCrowdDriver())
            {
                driver.Initialize(queue);
                ZKernelWorld world = driver.World;
                Assert.AreEqual(1000UL, world.GetStats().EntityCount);

                // 初始数据（初始化期 SetComponent 写入）此刻必须真的住在 kernel：
                foreach (int i in new[] { 0, 1, 499, 500, 999 })
                {
                    CrowdHealth h = world.GetComponent<CrowdHealth>(driver.GetEntity(i), CompHealth);
                    Assert.AreEqual(KernelCrowdIds.InitialHealth((uint)i), h.Value,
                        $"实体 {i} 初始生命读回不一致");

                    CrowdAlive a = world.GetComponent<CrowdAlive>(driver.GetEntity(i), CompAlive);
                    Assert.AreEqual(1, a.Value, $"实体 {i} 初始应为存活");
                }

                // 组件布局抽查：五列注册尺寸与组件结构一致。
                Assert.AreEqual(12u, world.GetComponentSize(CompPos));
                Assert.AreEqual(12u, world.GetComponentSize(CompVel));
                Assert.AreEqual(4u, world.GetComponentSize(CompTag));
                Assert.AreEqual(4u, world.GetComponentSize(CompHealth));
                Assert.AreEqual(1u, world.GetComponentSize(CompAlive));
            }
        }

        /// <summary>
        /// 行为等价 + 数据宿主证明：Tick 后（A）逐实体组件读回 = 预期积分值
        /// （游标原地写确实落进 kernel，不是托管影子数组）；（B）实例矩阵翻译列
        /// = 由 kernel 读回位置独立重算的圆盘公式（矩阵确实从 kernel 列构建）。
        /// </summary>
        [Test]
        public void Tick_InPlaceCursorWrites_KernelReadBackAndMatrixConsistent()
        {
            var queue = new TickQueue();
            using (var driver = new KernelCrowdDriver())
            {
                driver.Initialize(queue);
                const float dt = 0.016f;
                driver.Tick(dt);

                Assert.AreEqual(1000u, driver.LastTotalRows);
                Assert.AreEqual(1000, driver.LastAliveCount, "首帧无死亡");

                ZKernelWorld world = driver.World;
                Matrix4x4[] matrices = driver.Matrices;
                for (int i = 0; i < 1000; ++i)
                {
                    float vx = 0.5f + (i % 3) * 0.1f;
                    float expectedX = i + vx * dt; // i ≤ 999：首帧必不回绕
                    CrowdPosition p = world.GetComponent<CrowdPosition>(driver.GetEntity(i), CompPos);
                    Assert.AreEqual(expectedX, p.X, 1e-3f, $"实体 {i} 位置读回 ≠ 积分预期（数据未落 kernel？）");
                    Assert.AreEqual(0.25f, p.Y, 1e-5f);

                    // 独立重算 BuildMatrix 公式（测试不复用产品代码），证明矩阵由 kernel 列推导。
                    float angle = p.X * KernelCrowdIds.GoldenAngle;
                    float radius = KernelCrowdIds.FieldRadius * Mathf.Sqrt((p.X + 0.5f) / 1000f);
                    Vector3 expectedWorld = new Vector3(
                        Mathf.Cos(angle) * radius,
                        p.Y + 0.3f + (i % 5) * 0.15f,
                        Mathf.Sin(angle) * radius);
                    Vector3 actualWorld = matrices[i].GetColumn(3);
                    Assert.AreEqual(expectedWorld.x, actualWorld.x, 1e-3f,
                        $"实体 {i} 矩阵 X ≠ 由 kernel 位置重算值");
                    Assert.AreEqual(expectedWorld.y, actualWorld.y, 1e-3f);
                    Assert.AreEqual(expectedWorld.z, actualWorld.z, 1e-3f);
                }
            }
        }

        /// <summary>
        /// Health/Alive 是活状态不是摆设：把全员生命设到濒死 → 一帧后全员死亡
        /// （Alive=0、矩阵置零不可见、死亡行仍被游标交付）→ 回复 2s 后全员重生
        /// （Alive=1、生命重置为初始值、矩阵重建）。全部状态变化都在 kernel 列上发生。
        /// </summary>
        [Test]
        public void HealthDecay_DeathAndRespawn_AliveBitAndMatrixFollow()
        {
            var queue = new TickQueue();
            using (var driver = new KernelCrowdDriver())
            {
                driver.Initialize(queue);
                ZKernelWorld world = driver.World;

                // 测试注入：直接把生命列压到濒死（初始化期一次性 SetComponent，非每帧路径）。
                for (int i = 0; i < 1000; ++i)
                {
                    world.SetComponent(driver.GetEntity(i), CompHealth, new CrowdHealth(0.001f));
                }

                driver.Tick(0.1f); // 0.001 - 0.1*0.1 < 0 → 全员死亡
                Assert.AreEqual(1000u, driver.LastTotalRows, "死亡行仍被行为游标交付（掩码含全实体）");
                Assert.AreEqual(0, driver.LastAliveCount, "全员死亡");

                foreach (int i in new[] { 0, 500, 999 })
                {
                    Assert.AreEqual(0, world.GetComponent<CrowdAlive>(driver.GetEntity(i), CompAlive).Value,
                        $"实体 {i} 死亡后 Alive 列应为 0");
                    Assert.AreEqual(-KernelCrowdIds.RespawnDelaySeconds * KernelCrowdIds.RespawnHealPerSecond,
                        world.GetComponent<CrowdHealth>(driver.GetEntity(i), CompHealth).Value, 1e-4f,
                        "死亡瞬间 Health 应置重生窗起点");
                    Assert.AreEqual(Matrix4x4.zero, driver.Matrices[i],
                        $"实体 {i} 死亡后矩阵应置零（不可见）");
                }

                // -10 起步、5/s 回复：0.5s×3 帧后 -2.5（仍死亡），第 4 帧到 0 → 全员重生。
                driver.Tick(0.5f);
                driver.Tick(0.5f);
                driver.Tick(0.5f);
                Assert.AreEqual(0, driver.LastAliveCount, "2.5s 内（< 2s 窗口 + 缓冲）不应有重生");

                driver.Tick(0.5f);
                Assert.AreEqual(1000, driver.LastAliveCount, "累计 2.0s 回复后全员重生");
                foreach (int i in new[] { 0, 500, 999 })
                {
                    Assert.AreEqual(1, world.GetComponent<CrowdAlive>(driver.GetEntity(i), CompAlive).Value,
                        $"实体 {i} 重生后 Alive 列应为 1");
                    Assert.AreEqual(KernelCrowdIds.InitialHealth((uint)i),
                        world.GetComponent<CrowdHealth>(driver.GetEntity(i), CompHealth).Value, 1e-4f,
                        "重生后 Health 重置为该实体初始值");
                    Assert.AreNotEqual(Matrix4x4.zero, driver.Matrices[i], "重生后矩阵重建（可见）");
                }
            }
        }

        /// <summary>
        /// kernel 真实性断言（防「名义迁移实际还是数组」的核心证据）：
        /// 直接销毁原生 World（绕过 driver.Dispose）→ 行为停止——Tick 不抛异常但
        /// 不再产生任何更新（矩阵逐元素冻结、PullSum 归零、行计数不再推进），
        /// 且 driver 仍可安全 Dispose。若状态真住在托管数组，World 销毁不可能让行为停下。
        /// </summary>
        [Test]
        public void WorldDestroyed_BehaviorStops_MatrixFrozen_NoThrow()
        {
            var queue = new TickQueue();
            var driver = new KernelCrowdDriver();
            driver.Initialize(queue);
            driver.Tick(0.016f);
            Assert.IsTrue(driver.KernelAlive);
            Assert.Greater(driver.LastTotalRows, 0u);

            var frozen = (Matrix4x4[])driver.Matrices.Clone();
            uint rowsBefore = driver.LastTotalRows;

            driver.World.Dispose(); // 直接杀 kernel（模拟「内核没了」）

            Assert.IsTrue(driver.World.IsDisposed);
            Assert.IsFalse(driver.KernelAlive, "World 销毁后 KernelAlive 必须翻假");

            Assert.DoesNotThrow(() => driver.Tick(0.016f), "World 销毁后的 Tick 必须静默停止而非崩溃");
            Assert.AreEqual(rowsBefore, driver.LastTotalRows, "行为停止：游标不再推进（行计数冻结）");
            Assert.AreEqual(0.0, driver.PullSum(), "行为停止：只读拉取归零");
            Assert.Catch<ObjectDisposedException>(() =>
                driver.World.GetComponent<CrowdPosition>(driver.GetEntity(0), CompPos),
                "World 销毁后组件读回必须被托管包装拒绝（句柄表已释放）");

            for (int i = 0; i < 1000; ++i)
            {
                Assert.AreEqual(frozen[i], driver.Matrices[i], $"实体 {i} 矩阵未冻结");
            }

            Assert.DoesNotThrow(() => driver.Dispose(), "World 已被外部销毁时 driver.Dispose 仍须幂等安全");
            Assert.AreEqual(0, queue.Count(TickStage.Logic), "Dispose 注销 tick");
        }

        /// <summary>每帧 P/Invoke 预算：两段游标 = 2×Begin + (批数+1)×Next，无任何逐实体调用。</summary>
        [Test]
        public void PerTickPInvokeBudget_TwoPassCursor_Bounded()
        {
            var queue = new TickQueue();
            using (var driver = new KernelCrowdDriver())
            {
                driver.Initialize(queue);
                driver.Tick(0.016f);

                Assert.AreEqual(1000u, driver.LastTotalRows);
                Assert.AreEqual(1000, driver.LastAliveCount);
                Assert.GreaterOrEqual(driver.LastBehaviorBatches, 2u,
                    "1000 实体（33B/行 → chunk 容量 496）必须跨 ≥2 个 chunk 推进");

                int calls = driver.LastTickPInvokeCalls;
                Assert.LessOrEqual(calls, 32,
                    $"每帧 ZK_* P/Invoke 预算 32（实测 {calls}）；A-R9-13 旧路径每帧 ~2003 次（1000 实体 × 2 次 SetComponent）");

                UnityEngine.Debug.Log($"[A-AND-08] 每帧 ZK_* P/Invoke = {calls} " +
                    $"（行为段 {driver.LastBehaviorBatches} 批 + 矩阵段 {driver.LastMatrixBatches} 批；" +
                    "2×Begin + (批数+1)×Next/段；行为/矩阵段内零 SetComponent/GetComponent）");
            }
        }

        /// <summary>
        /// A/B 性能对比（同一次运行内，同实体数 1000，FrameGovernor/Stopwatch 双口径）：
        /// kernel 路径（五列游标原地读写 + 矩阵段）vs C# 自管数组路径
        /// （<see cref="CrowdArrayBaseline"/>，同行为语义零 ABI）。
        /// 宽松上界只防病态回归；±20% 验收线的精确数字以日志输出、由施工报告如实判定。
        /// </summary>
        [Test]
        public void Perf_AB_KernelVsCSharpArray_DualGauge()
        {
            var queue = new TickQueue();
            const int frames = 300;
            const float dt = 0.016f;

            using (var driver = new KernelCrowdDriver())
            {
                driver.Initialize(queue);
                var baseline = new CrowdArrayBaseline();

                driver.Tick(dt);        // 预热（JIT / 列缓冲 / delegate 缓存）
                baseline.Tick(dt);
                driver.PullSum();
                baseline.SumX();

                // -- 口径一：Stopwatch——数据侧每帧成本（µs/帧）与 Each 遍历成本（µs/次）
                var kernelFrames = new double[frames];
                var arrayFrames = new double[frames];
                for (int i = 0; i < frames; ++i)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    driver.Tick(dt);
                    kernelFrames[i] = ElapsedUs(t0);

                    long t1 = Stopwatch.GetTimestamp();
                    baseline.Tick(dt);
                    arrayFrames[i] = ElapsedUs(t1);
                }

                const int pullIterations = 300;
                long tp0 = Stopwatch.GetTimestamp();
                double kernelSum = 0.0;
                for (int i = 0; i < pullIterations; ++i)
                {
                    kernelSum = driver.PullSum();
                }

                double kernelPullUs = ElapsedUs(tp0) / pullIterations;

                long tp1 = Stopwatch.GetTimestamp();
                double arraySum = 0.0;
                for (int i = 0; i < pullIterations; ++i)
                {
                    arraySum = baseline.SumX();
                }

                double arraySumUs = ElapsedUs(tp1) / pullIterations;

                // -- 口径二：FrameGovernor——同批帧样本喂进真实 FrameGovernor（TargetMs=16.6 @60fps）
                var telemetryKernel = new MeasuredTelemetry();
                var telemetryArray = new MeasuredTelemetry();
                FrameGovernor governorKernel = new FrameGovernor(GovernorLadder.Create(QualityTier.High))
                {
                    TargetMs = 16.6f,
                    Enabled = true,
                };
                FrameGovernor governorArray = new FrameGovernor(GovernorLadder.Create(QualityTier.High))
                {
                    TargetMs = 16.6f,
                    Enabled = true,
                };

                double kernelMsSum = 0.0;
                double arrayMsSum = 0.0;
                for (int i = 0; i < frames; ++i)
                {
                    float kernelMs = (float)(kernelFrames[i] / 1000.0);
                    float arrayMs = (float)(arrayFrames[i] / 1000.0);
                    kernelMsSum += kernelMs;
                    arrayMsSum += arrayMs;
                    governorKernel.Tick(kernelMs / 1000f, telemetryKernel.WithCpu(kernelMs));
                    governorArray.Tick(arrayMs / 1000f, telemetryArray.WithCpu(arrayMs));
                }

                double kernelFrameMs = kernelMsSum / frames;
                double arrayFrameMs = arrayMsSum / frames;
                double kernelTickUs = Mean(kernelFrames);
                double arrayTickUs = Mean(arrayFrames);
                double ratio = kernelTickUs / Math.Max(arrayTickUs, 0.001);

                // 宽松守卫（只防病态回归，不硬凑验收线）：
                Assert.Less(kernelTickUs, 100000.0, "kernel 单帧 > 100ms = 病态回归");
                Assert.Less(arrayTickUs, 100000.0, "数组单帧 > 100ms = 病态回归");
                Assert.Less(kernelFrameMs, 16.6, "kernel 均帧耗超出 60fps 帧预算");
                Assert.Less(arrayFrameMs, 16.6, "数组均帧耗超出 60fps 帧预算");
                Assert.Less(kernelTickUs, Math.Max(arrayTickUs * 3.0, arrayTickUs + 250.0),
                    "kernel 数据侧单帧 > 3×数组路径（或 +250µs）= 病态回归");

                string verdict = ratio <= 1.2
                    ? $"PASS（kernel ≤ 数组 +20%：实际 +{(ratio - 1.0) * 100.0:F1}%）"
                    : $"超出验收线（kernel = 数组 ×{ratio:F2}）→ 按简报如实报告瓶颈，不硬凑";
                UnityEngine.Debug.Log(
                    "[A-AND-08] A/B（同一次运行内，1000 实体，" + frames + " 帧均值）\n" +
                    $"  FrameGovernor 口径（数据侧均帧耗 ms）：kernel={kernelFrameMs:F4} array={arrayFrameMs:F4} " +
                    $"ratio={kernelFrameMs / Math.Max(arrayFrameMs, 1e-9):F2}（governor level: kernel={governorKernel.Level} array={governorArray.Level}，TargetMs=16.6）\n" +
                    $"  Stopwatch 口径（每帧 µs）：kernelTick={kernelTickUs:F1} arrayTick={arrayTickUs:F1} ratio={ratio:F2}\n" +
                    $"  Stopwatch 口径（Each 遍历 µs/次）：kernelPull={kernelPullUs:F1} arraySum={arraySumUs:F1}\n" +
                    $"  每帧 P/Invoke：kernel={driver.LastTickPInvokeCalls} array=0\n" +
                    $"  验收线判定：{verdict}");

                Assert.Greater(kernelSum, 0.0);
                Assert.Greater(arraySum, 0.0);
            }
        }

        /// <summary>既有分段守卫：只读游标拉取 vs 含原地写回的每帧更新（A-AND-08 口径更新）。</summary>
        [Test]
        public void Perf_KernelIteration_Vs_ReadOnlyPull_Baseline()
        {
            var queue = new TickQueue();
            const int iterations = 200;
            using (var driver = new KernelCrowdDriver())
            {
                driver.Initialize(queue);
                driver.PullSum();   // 预热（JIT / 列缓冲）
                driver.Tick(0f);

                long t0 = Stopwatch.GetTimestamp();
                double lastSum = 0.0;
                for (int i = 0; i < iterations; ++i)
                {
                    lastSum = driver.PullSum();
                }

                double pullUs = Us(t0, iterations);

                long t1 = Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; ++i)
                {
                    driver.Tick(0.016f);
                }

                double iterationUs = Us(t1, iterations);

                Assert.AreEqual(499500.0, lastSum, "只读拉取求和仍精确");
                Assert.Less(pullUs, 100000.0, "纯游标拉取 > 100ms = 病态回归");
                Assert.Less(iterationUs, 100000.0, "内核迭代 > 100ms = 病态回归");

                UnityEngine.Debug.Log($"[A-AND-08] 分段耗时（EditMode，{iterations} 次均值，同一次运行内）："
                    + $"纯只读游标拉取 {pullUs:F1} us/次（B-R9-17 基线 10.0 us）；"
                    + $"内核迭代(两段游标原地读写+积分+矩阵) {iterationUs:F1} us/次；"
                    + "渲染提交段见 PlayMode 用例。");
            }

            // 同一次运行内 A/B：100 vs 1000 实体的纯拉取耗时（说明内核迭代本身随实体数线性，
            // 与 PlayMode 里「DrawCall 恒定」形成对照）。
            double smallUs = MeasurePull(queue, 100, iterations);
            double largeUs = MeasurePull(queue, 1000, iterations);
            UnityEngine.Debug.Log($"[A-AND-08] 同一次运行内 A/B（纯只读游标拉取）："
                + $"100 实体 {smallUs:F1} us/次；1000 实体 {largeUs:F1} us/次；"
                + $"比值 {largeUs / Math.Max(smallUs, 0.001):F2}x。");
            Assert.Greater(largeUs, 0.0);
        }

        private static double MeasurePull(TickQueue queue, int count, int iterations)
        {
            using (var driver = new KernelCrowdDriver(count))
            {
                driver.Initialize(queue);
                driver.PullSum();
                long t = Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; ++i)
                {
                    driver.PullSum();
                }

                return Us(t, iterations);
            }
        }

        private static double Mean(double[] samples)
        {
            double sum = 0.0;
            for (int i = 0; i < samples.Length; ++i)
            {
                sum += samples[i];
            }

            return sum / samples.Length;
        }

        private static double ElapsedUs(long startTimestamp)
        {
            return (Stopwatch.GetTimestamp() - startTimestamp) * 1000000.0 / Stopwatch.Frequency;
        }

        private static double Us(long startTimestamp, int iterations)
        {
            return (Stopwatch.GetTimestamp() - startTimestamp) * 1000000.0
                / Stopwatch.Frequency / iterations;
        }

        /// <summary>测试注入的帧耗样本（batchmode 无 GPU 计时 → CpuFrameMs 走既有降级口径）。</summary>
        private sealed class MeasuredTelemetry : IFrameTelemetry
        {
            private float _cpuMs;

            public float GpuFrameMs => 0f; // GPU 计时不可用（0 = 降级到 CPU 的既有约定）

            public float CpuFrameMs => _cpuMs;

            public MeasuredTelemetry WithCpu(float cpuMs)
            {
                _cpuMs = cpuMs;
                return this;
            }
        }
    }
}
