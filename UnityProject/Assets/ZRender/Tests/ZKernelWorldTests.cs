// ZKernelWorldTests.cs —— DataKernel C# 绑定 EditMode 单测（TASK-B-R9-17）。
// 真实加载 zrender.dll（Assets/Plugins/ZRender/，构建产物入库 ADR-004），
// 全链路 roundtrip：Register→Spawn→Set→游标读回→迁移→Destroy。
// 列数据经 Marshal.Copy / Marshal.ReadInt32 读取（测试程序集不开 unsafe）。

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Render.Bindings;

namespace ZFrame.Render.Tests
{
    /// <summary>测试组件：位置（12B，组件 ID 0）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct KcPos
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;

        public KcPos(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    /// <summary>测试组件：速度（12B，组件 ID 1）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct KcVel
    {
        public readonly float Dx;
        public readonly float Dy;
        public readonly float Dz;

        public KcVel(float dx, float dy, float dz)
        {
            Dx = dx;
            Dy = dy;
            Dz = dz;
        }
    }

    /// <summary>测试组件：标签（4B，组件 ID 2）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct KcTag
    {
        public readonly uint V;

        public KcTag(uint v)
        {
            V = v;
        }
    }

    /// <summary>
    /// ZKernelWorld 安全包装的真 dll roundtrip 用例（组件 ID 0/1/2 = pos/vel/tag）。
    /// </summary>
    [TestFixture]
    public class ZKernelWorldTests
    {
        private const uint KCompPos = 0u;
        private const uint KCompVel = 1u;
        private const uint KCompTag = 2u;

        [Test]
        public void Roundtrip_SpawnSetGetComponent_ValuesMatch()
        {
            using (var world = new ZKernelWorld())
            {
                world.RegisterComponent<KcPos>(KCompPos);
                world.RegisterComponent<KcVel>(KCompVel);
                world.RegisterComponent<KcTag>(KCompTag);

                var mask = ZkMask.Of(KCompPos).With(KCompTag);
                var e = world.Spawn(mask);
                world.SetComponent(e, KCompPos, new KcPos(1.5f, 2.5f, 3.5f));
                world.SetComponent(e, KCompTag, new KcTag(77u));

                KcPos pos = world.GetComponent<KcPos>(e, KCompPos);
                Assert.AreEqual(1.5f, pos.X);
                Assert.AreEqual(2.5f, pos.Y);
                Assert.AreEqual(3.5f, pos.Z);
                KcTag tag = world.GetComponent<KcTag>(e, KCompTag);
                Assert.AreEqual(77u, tag.V);

                // 未持有组件：TryGet false（GetComponent 抛）
                Assert.IsFalse(world.TryGetComponent(e, KCompVel, out KcVel _));
                Assert.Catch<ZKernelException>(() => world.GetComponent<KcVel>(e, KCompVel));

                // Marshal.SizeOf 校验：尺寸不匹配的类型拒绝
                Assert.Catch<ArgumentException>(() => world.SetComponent(e, KCompPos, new KcTag(1u)));
            }
        }

        [Test]
        public void Cursor_1000Entities_ThreeColumnSumCorrect()
        {
            using (var world = new ZKernelWorld())
            {
                world.RegisterComponent<KcPos>(KCompPos);
                world.RegisterComponent<KcVel>(KCompVel);
                world.RegisterComponent<KcTag>(KCompTag);
                var mask = ZkMask.Of(KCompPos).With(KCompVel).With(KCompTag);

                const int count = 1000; // 28B/行 → chunk 容量 585 → 2 个 chunk
                for (uint i = 0; i < count; ++i)
                {
                    var e = world.Spawn(mask);
                    world.SetComponent(e, KCompPos, new KcPos(i, 0f, 0f));
                    world.SetComponent(e, KCompVel, new KcVel(i, 1f, 0f));
                    world.SetComponent(e, KCompTag, new KcTag(i));
                }

                double sumPos = 0.0, sumVel = 0.0;
                ulong sumTag = 0;
                uint batches = 0;
                uint total = world.EachBatches(mask, (columns, rows) =>
                {
                    var pos = new float[rows * 3];
                    var vel = new float[rows * 3];
                    Marshal.Copy(columns[0], pos, 0, pos.Length);
                    Marshal.Copy(columns[1], vel, 0, vel.Length);
                    for (uint r = 0; r < rows; ++r)
                    {
                        sumPos += pos[r * 3];
                        sumVel += vel[r * 3];
                        sumTag += (uint)Marshal.ReadInt32(columns[2], (int)r * 4);
                    }

                    ++batches;
                });

                Assert.AreEqual((uint)count, total);
                Assert.GreaterOrEqual(batches, 2u); // 1000 > 容量 585 → 跨 chunk 推进
                const double expected = 999.0 * 1000.0 / 2.0;
                Assert.AreEqual(expected, sumPos);
                Assert.AreEqual(expected, sumVel);
                Assert.AreEqual((ulong)count * (count - 1) / 2, sumTag);
            }
        }

        [Test]
        public void Cursor_MultiArchetypeFilter_SecondArchetypeIncluded()
        {
            using (var world = new ZKernelWorld())
            {
                world.RegisterComponent<KcPos>(KCompPos);
                world.RegisterComponent<KcVel>(KCompVel);
                world.RegisterComponent<KcTag>(KCompTag);

                var posVelTag = ZkMask.Of(KCompPos).With(KCompVel).With(KCompTag);
                var posVel = ZkMask.Of(KCompPos).With(KCompVel);
                for (uint i = 0; i < 1000; ++i)
                {
                    var e = world.Spawn(posVelTag);
                    world.SetComponent(e, KCompPos, new KcPos(i, 0f, 0f));
                    world.SetComponent(e, KCompVel, new KcVel(i, 2f, 0f));
                }

                for (uint i = 0; i < 10; ++i)
                {
                    var e = world.Spawn(posVel); // 第二 archetype（无 tag）
                    world.SetComponent(e, KCompPos, new KcPos(i, 0f, 0f));
                    world.SetComponent(e, KCompVel, new KcVel(i, 3f, 0f));
                }

                double sumVel = 0.0;
                uint rows = 0;
                world.EachBatches(posVel, (columns, rowCount) =>
                {
                    var vel = new float[rowCount * 3];
                    Marshal.Copy(columns[1], vel, 0, vel.Length);
                    for (uint r = 0; r < rowCount; ++r)
                    {
                        sumVel += vel[r * 3];
                    }

                    rows += rowCount;
                });

                Assert.AreEqual(1010u, rows);
                Assert.AreEqual(999.0 * 1000.0 / 2.0 + 45.0, sumVel); // 0..999 + 0..9
            }
        }

        [Test]
        public void Cursor_ColumnOrder_AscendingCompId()
        {
            using (var world = new ZKernelWorld())
            {
                world.RegisterComponent<KcPos>(KCompPos);
                world.RegisterComponent<KcTag>(KCompTag);
                var mask = ZkMask.Of(KCompTag).With(KCompPos); // 注意 With 顺序：tag(2) 先

                var e = world.Spawn(mask);
                world.SetComponent(e, KCompPos, new KcPos(7f, 0f, 0f));
                world.SetComponent(e, KCompTag, new KcTag(42u));

                // 列序 = 查询掩码升序 compId（pos=0 → 第 0 列，tag=2 → 第 1 列），与 With 顺序无关
                uint seenRows = 0;
                world.EachBatches(mask, (columns, rows) =>
                {
                    Assert.AreEqual(1u, rows);
                    float col0 = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(columns[0]));
                    Assert.AreEqual(7f, col0); // 若列序颠倒这里会读到 tag 位型（非 7f）
                    Assert.AreEqual(42u, (uint)Marshal.ReadInt32(columns[1]));
                    ++seenRows;
                });
                Assert.AreEqual(1u, seenRows);
            }
        }

        [Test]
        public void Migration_AddComponent_ZeroInitThenReadBack()
        {
            using (var world = new ZKernelWorld())
            {
                world.RegisterComponent<KcPos>(KCompPos);
                world.RegisterComponent<KcVel>(KCompVel);
                world.RegisterComponent<KcTag>(KCompTag);
                var mask = ZkMask.Of(KCompPos).With(KCompTag);

                const int count = 300;
                var entities = new ZkEntity[count];
                for (uint i = 0; i < count; ++i)
                {
                    entities[i] = world.Spawn(mask);
                    world.SetComponent(entities[i], KCompPos, new KcPos(i, 0f, 0f));
                }

                // 迁移加 vel（零初始化）→ 读回 0 → Set 覆写 → 读回
                for (uint i = 0; i < count; ++i)
                {
                    world.AddComponent(entities[i], KCompVel); // initData 空 = 零初始化
                }

                ZkWorldStats stats = world.GetStats();
                Assert.AreEqual((ulong)count, stats.Migrations);
                Assert.AreEqual((ulong)count, stats.EntityCount);
                Assert.GreaterOrEqual(stats.ChunkCount, 1ul);

                for (uint i = 0; i < count; ++i)
                {
                    KcVel vel = world.GetComponent<KcVel>(entities[i], KCompVel);
                    Assert.AreEqual(0f, vel.Dx);
                    Assert.AreEqual(0f, vel.Dy);
                    Assert.AreEqual(0f, vel.Dz);
                    world.SetComponent(entities[i], KCompVel, new KcVel(i, 1f, 2f));
                }

                for (uint i = 0; i < count; ++i)
                {
                    KcPos pos = world.GetComponent<KcPos>(entities[i], KCompPos); // 迁移后原句柄仍有效
                    Assert.AreEqual((float)i, pos.X);
                    KcVel vel = world.GetComponent<KcVel>(entities[i], KCompVel);
                    Assert.AreEqual((float)i, vel.Dx);
                    Assert.AreEqual(2f, vel.Dz);
                }

                // 游标全量回读（迁移后 SoA 布局一致性）
                double sumPos = 0.0;
                uint total = world.EachBatches(ZkMask.Of(KCompPos), (columns, rows) =>
                {
                    var pos = new float[rows * 3];
                    Marshal.Copy(columns[0], pos, 0, pos.Length);
                    for (uint r = 0; r < rows; ++r)
                    {
                        sumPos += pos[r * 3];
                    }
                });
                Assert.AreEqual((uint)count, total);
                Assert.AreEqual(299.0 * 300.0 / 2.0, sumPos); // 300 实体 X=0..299
            }
        }

        [Test]
        public void Migration_RemoveComponent_DataDropped()
        {
            using (var world = new ZKernelWorld())
            {
                world.RegisterComponent<KcPos>(KCompPos);
                world.RegisterComponent<KcVel>(KCompVel);
                world.RegisterComponent<KcTag>(KCompTag);
                var mask = ZkMask.Of(KCompPos).With(KCompVel);

                var e = world.Spawn(mask);
                world.SetComponent(e, KCompPos, new KcPos(5f, 6f, 7f));
                world.SetComponent(e, KCompVel, new KcVel(1f, 2f, 3f));

                world.RemoveComponent<KcVel>(e, KCompVel);
                Assert.IsFalse(world.TryGetComponent(e, KCompVel, out KcVel _));
                KcPos pos = world.GetComponent<KcPos>(e, KCompPos); // 共享列保留
                Assert.AreEqual(5f, pos.X);
                Assert.AreEqual(7f, pos.Z);
                ZkWorldStats stats = world.GetStats();
                Assert.AreEqual(1ul, stats.Migrations);
                Assert.AreEqual(1ul, stats.EntityCount);

                Assert.Catch<ZKernelException>(() => world.RemoveComponent<KcVel>(e, KCompVel)); // 再删拒绝
            }
        }

        [Test]
        public void StaleEntityHandle_RejectedAfterDestroy()
        {
            using (var world = new ZKernelWorld())
            {
                world.RegisterComponent<KcPos>(KCompPos);
                world.RegisterComponent<KcTag>(KCompTag);
                var mask = ZkMask.Of(KCompPos);

                var e = world.Spawn(mask);
                world.SetComponent(e, KCompPos, new KcPos(1f, 2f, 3f));
                Assert.IsTrue(world.Destroy(e));
                Assert.IsFalse(world.Destroy(e)); // 重复销毁
                Assert.IsFalse(world.TryGetComponent(e, KCompPos, out KcPos _));

                // 复用槽位：代际 +1 的新实体，旧句柄仍被拒。
                // 注意原生契约：Spawn 的组件内存未初始化（可能残留旧行字节），不断言其值。
                var e2 = world.Spawn(mask);
                Assert.AreEqual(e.Index, e2.Index);
                Assert.AreEqual(e.Generation + 1u, e2.Generation);
                Assert.IsFalse(world.TryGetComponent(e, KCompPos, out KcPos _));
                Assert.IsTrue(world.TryGetComponent(e2, KCompPos, out KcPos pos));
                world.SetComponent(e2, KCompPos, new KcPos(9f, 9f, 9f));
                pos = world.GetComponent<KcPos>(e2, KCompPos);
                Assert.AreEqual(9f, pos.X);
            }
        }

        [Test]
        public void CursorLayoutMutation_NextAdvanceThrowsWithErrState()
        {
            // 诊断性用例：验证"迭代期间布局变更 → 原生布局版本号作废游标"防护。
            // 生产代码禁止这样做（ZkEachBatchHandler doc 契约）。
            using (var world = new ZKernelWorld())
            {
                world.RegisterComponent<KcPos>(KCompPos);
                var mask = ZkMask.Of(KCompPos);
                for (uint i = 0; i < 10; ++i)
                {
                    world.Spawn(mask);
                }

                var victim = world.Spawn(mask);
                ZKernelException caught = null;
                try
                {
                    world.EachBatches(mask, (columns, rows) => { world.Destroy(victim); });
                }
                catch (ZKernelException ex)
                {
                    caught = ex;
                }

                Assert.IsNotNull(caught, "布局变更后的下一次推进应抛 ZKernelException");
                Assert.AreEqual(ZKernelErrorCodes.ErrState, caught.Code);
            }
        }

        [Test]
        public void WorldTable_Limit8_AndReleaseFreesSlot()
        {
            var worlds = new ZKernelWorld[8];
            try
            {
                for (int i = 0; i < 8; ++i)
                {
                    worlds[i] = new ZKernelWorld();
                }

                Assert.Catch<ZKernelException>(() => new ZKernelWorld()); // 第 9 个：表满
            }
            finally
            {
                worlds[3]?.Dispose();
                for (int i = 0; i < 8; ++i)
                {
                    if (i != 3)
                    {
                        worlds[i]?.Dispose();
                    }
                }
            }

            using (var released = new ZKernelWorld()) // 销毁后槽位可复用
            {
                Assert.IsFalse(released.IsDisposed);
            }
        }

        [Test]
        public void GetComponentSize_ReflectsRegistration()
        {
            using (var world = new ZKernelWorld())
            {
                Assert.AreEqual(0u, world.GetComponentSize(KCompPos)); // 未注册 = 0
                world.RegisterComponent<KcPos>(KCompPos);
                Assert.AreEqual(12u, world.GetComponentSize(KCompPos));
                world.RegisterComponent<KcTag>(KCompTag);
                Assert.AreEqual(4u, world.GetComponentSize(KCompTag));
            }
        }

        /// <summary>
        /// 性能守卫（A-R9-13 基线）：游标拉取全 1000 实体的 C# 循环耗时报告。
        /// 计时含 Marshal.Copy 列读 + 求和（无 unsafe）。宽松上界 100ms/次只为
        /// 捕获病态回归；正常值见 Debug.Log 输出（写入 EditMode 测试日志）。
        /// </summary>
        [Test]
        public void Perf_1000EntityCursorPull_TimingReported()
        {
            using (var world = new ZKernelWorld())
            {
                world.RegisterComponent<KcPos>(KCompPos);
                world.RegisterComponent<KcVel>(KCompVel);
                world.RegisterComponent<KcTag>(KCompTag);
                var mask = ZkMask.Of(KCompPos).With(KCompVel).With(KCompTag);

                const int count = 1000;
                for (uint i = 0; i < count; ++i)
                {
                    var e = world.Spawn(mask);
                    world.SetComponent(e, KCompPos, new KcPos(i, 0f, 0f));
                    world.SetComponent(e, KCompVel, new KcVel(i, 1f, 0f));
                    world.SetComponent(e, KCompTag, new KcTag(i));
                }

                // 预热 1 次（JIT/列缓冲分配）
                WarmupPull(world, mask);

                const int iterations = 200;
                var sw = Stopwatch.StartNew();
                double lastSum = 0.0;
                for (int it = 0; it < iterations; ++it)
                {
                    lastSum = PullSum(world, mask, count);
                }

                sw.Stop();
                double totalMs = sw.Elapsed.TotalMilliseconds;
                double avgPerPullUs = totalMs * 1000.0 / iterations;
                Assert.AreEqual(999.0 * 1000.0 / 2.0, lastSum);
                Assert.Less(totalMs / iterations, 100.0, "单次 1000 实体游标拉取 > 100ms，病态回归");

                // 耗时基线报告（A-R9-13 消费；数值写进测试日志与施工报告）
                UnityEngine.Debug.Log($"[B-R9-17] 1000 实体游标拉取（C# 循环 + Marshal.Copy 列读）："
                    + $"平均 {avgPerPullUs:F1} us/次（{iterations} 次均值，总计 {totalMs:F2} ms），"
                    + $"sumPos={lastSum:F0}（期望 499500）。环境：Editor EditMode，单线程。");
            }
        }

        private static void WarmupPull(ZKernelWorld world, ZkMask mask)
        {
            _ = PullSum(world, mask, 1000);
        }

        /// <summary>一次全量游标拉取：{pos,vel,tag} 三列求和（校验与计时共用）。</summary>
        private static double PullSum(ZKernelWorld world, ZkMask mask, int expectedCount)
        {
            double sumPos = 0.0;
            float[] posBuffer = null;
            uint total = world.EachBatches(mask, (columns, rows) =>
            {
                if (posBuffer == null || posBuffer.Length < rows * 3)
                {
                    posBuffer = new float[rows * 3];
                }

                Marshal.Copy(columns[0], posBuffer, 0, (int)rows * 3);
                for (uint r = 0; r < rows; ++r)
                {
                    sumPos += posBuffer[r * 3];
                }
            });
            Assert.AreEqual((uint)expectedCount, total);
            return sumPos;
        }
    }
}
