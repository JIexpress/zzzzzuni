// KernelCrowdDriverTests.cs —— 千人演练场 EditMode 真 dll roundtrip（TASK-A-R9-13）。
// 与 B-R9-17 的 ZKernelWorldTests 一样真实加载 Assets/Plugins/ZRender/zrender.dll，
// 但组件/compId/驱动全部是本任务玩法侧的定义：1000 实体三列求和、迁移读回、
// 销毁句柄失效、坏 compId 拒绝、ITickQueue 注册/注销、分段耗时。

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Game.View.Effects;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Core;
using ZFrame.Render.Bindings;

namespace Game.View.Tests
{
    /// <summary>玩法侧 DataKernel roundtrip + 驱动自测（真 dll，不 mock）。</summary>
    [TestFixture]
    public sealed class KernelCrowdDriverTests
    {
        private const uint CompPos = KernelCrowdIds.Position;
        private const uint CompVel = KernelCrowdIds.Velocity;
        private const uint CompTag = KernelCrowdIds.Tag;

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

        /// <summary>
        /// 性能守卫（分段，同一次运行内测量，不做跨跑比较）：
        /// ① 纯只读游标拉取（等价 B-R9-17 的 10.0 µs 基线口径）；
        /// ② 每帧内核迭代（读 + 积分 + 1000 次 SetComponent 回写 + 矩阵构造）。
        /// 渲染提交段见 PlayMode 用例。宽松上界只防病态回归。
        /// </summary>
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

                UnityEngine.Debug.Log($"[A-R9-13] 分段耗时（EditMode，{iterations} 次均值，同一次运行内）："
                    + $"纯只读游标拉取 {pullUs:F1} us/次（B-R9-17 基线 10.0 us）；"
                    + $"内核迭代(游标读+积分+1000次SetComponent回写+矩阵) {iterationUs:F1} us/次；"
                    + "渲染提交段见 PlayMode 用例。");
            }

            // 同一次运行内 A/B：100 vs 1000 实体的纯拉取耗时（说明内核迭代本身随实体数线性，
            // 与 PlayMode 里「DrawCall 恒定」形成对照）。
            double smallUs = MeasurePull(queue, 100, iterations);
            double largeUs = MeasurePull(queue, 1000, iterations);
            UnityEngine.Debug.Log($"[A-R9-13] 同一次运行内 A/B（纯只读游标拉取）："
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

        private static double Us(long startTimestamp, int iterations)
        {
            return (Stopwatch.GetTimestamp() - startTimestamp) * 1000000.0
                / Stopwatch.Frequency / iterations;
        }
    }
}
