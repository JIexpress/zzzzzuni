// KernelCrowdMigrationPlayModeTests.cs —— TASK-A-AND-08：千人群体 kernel 化的
// PlayMode 集成证据。EditMode（Game/Tests/View/KernelCrowdDriverTests）已覆盖
// 读回/真实性/A/B 细节；本文件在生产泵（ITickQueue TickAll）+ 真实渲染提交的
// 场景里补三条集成证据：
//  1. 行为→渲染全链：Logic 档游标原地读写 → View 档单次 DrawMeshInstanced；
//  2. kernel 真实性（Play 期口径）：World 销毁 → 行为停止、渲染矩阵冻结、无异常；
//  3. A/B 帧耗（场景内、同实体数）：kernel vs C# 数组，FrameGovernor/Stopwatch 双口径。
// 英雄侧零改动零断言（既有 KernelCrowdPlayModeTests 已锁英雄 FSM 不回归）。

using System;
using System.Collections;
using System.Diagnostics;
using Game.View.Effects;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using ZFrame.Core;
using ZFrame.Render.Bindings;
using ZFrame.View;

namespace Game.View.PlayMode.Tests
{
    /// <summary>群体 kernel 化的 PlayMode 集成证据（生产泵路径）。</summary>
    public sealed class KernelCrowdMigrationPlayModeTests
    {
        private TickQueue _queue;
        private KernelCrowdDriver _driver;
        private KernelCrowdRenderer _renderer;

        [SetUp]
        public void SetUp()
        {
            _queue = new TickQueue();
        }

        [TearDown]
        public void TearDown()
        {
            _renderer?.Dispose();
            _renderer = null;
            _driver?.Dispose();
            _driver = null;
            _queue = null;
        }

        private void EnsureCrowd()
        {
            _driver = new KernelCrowdDriver();
            _driver.Initialize(_queue);
            _renderer = new KernelCrowdRenderer();
            _renderer.Initialize(_queue, _driver.Matrices, _driver.EntityCount);
        }

        // ---- 用例 1：生产泵全链（Logic 游标更新 → View 单次批量提交）----
        [UnityTest]
        public IEnumerator KernelCrowd_Migration_InScene_BehaviorRenderAndPInvokeBudget()
        {
            yield return null; // 让 runner 场景稳定
            EnsureCrowd();

            for (int frame = 0; frame < 3; ++frame)
            {
                _queue.TickAll(TickStage.Logic, 0.016f);
                _queue.TickAll(TickStage.View, 0.016f);
            }

            Assert.AreEqual(1000u, _driver.LastTotalRows, "每帧行为游标读回全部 1000 行");
            Assert.AreEqual(1000, _driver.LastAliveCount, "正常游玩窗口内无死亡");
            Assert.AreEqual(1000UL, _driver.World.GetStats().EntityCount, "实体确实住在 kernel");
            Assert.LessOrEqual(_driver.LastTickPInvokeCalls, 32,
                $"每帧 P/Invoke 预算 32（实测 {_driver.LastTickPInvokeCalls}）");
            Assert.GreaterOrEqual(_driver.LastBehaviorBatches, 2u, "1000 实体跨 chunk 推进");
            Assert.IsTrue(_renderer.IsShaderAvailable,
                "URP 兼容材质须解析成功（shader=" + _renderer.ResolvedShaderName + "）");
            Assert.AreEqual(1, _renderer.LastSubmitCalls,
                "1000 实例仍须单次 DrawMeshInstanced（矩阵源换了，提交判据不变）");
            Assert.AreEqual(1000, _renderer.LastSubmittedInstances);

            // 场景内组件读回一致性：矩阵翻译列 = kernel 读回位置独立重算（抽样）。
            ZKernelWorld world = _driver.World;
            foreach (int i in new[] { 0, 250, 500, 999 })
            {
                CrowdPosition p = world.GetComponent<CrowdPosition>(_driver.GetEntity(i), KernelCrowdIds.Position);
                float angle = p.X * KernelCrowdIds.GoldenAngle;
                float radius = KernelCrowdIds.FieldRadius * Mathf.Sqrt((p.X + 0.5f) / 1000f);
                Vector3 expected = new Vector3(
                    Mathf.Cos(angle) * radius,
                    p.Y + 0.3f + (i % 5) * 0.15f,
                    Mathf.Sin(angle) * radius);
                Vector3 actual = _driver.Matrices[i].GetColumn(3);
                Assert.AreEqual(expected.x, actual.x, 1e-3f, $"实体 {i} 矩阵 ≠ kernel 列推导");
                Assert.AreEqual(expected.y, actual.y, 1e-3f);
                Assert.AreEqual(expected.z, actual.z, 1e-3f);
            }

            UnityEngine.Debug.Log($"[A-AND-08] PlayMode 全链：rows={_driver.LastTotalRows} " +
                      $"alive={_driver.LastAliveCount} pInvoke/frame={_driver.LastTickPInvokeCalls} " +
                      $"submitCalls={_renderer.LastSubmitCalls} " +
                      $"kernelIterationUs={_driver.LastKernelIterationUs:F1} " +
                      $"renderSubmitUs={_renderer.LastRenderSubmitUs:F1}。");
        }

        // ---- 用例 2：kernel 真实性（Play 期）：World 销毁 → 行为停止、渲染冻结 ----
        [UnityTest]
        public IEnumerator KernelCrowd_WorldDestroyedInPlay_BehaviorStopsRenderFrozen()
        {
            yield return null;
            EnsureCrowd();

            _queue.TickAll(TickStage.Logic, 0.016f);
            _queue.TickAll(TickStage.View, 0.016f);
            var frozen = (Matrix4x4[])_driver.Matrices.Clone();

            _driver.World.Dispose(); // Play 期直接杀 kernel

            Assert.IsFalse(_driver.KernelAlive, "World 销毁后 KernelAlive 须翻假");
            for (int frame = 0; frame < 3; ++frame)
            {
                Assert.DoesNotThrow(() => _queue.TickAll(TickStage.Logic, 0.016f),
                    "kernel 没了之后的生产泵必须继续跑且不抛（驱动静默停止）");
                _queue.TickAll(TickStage.View, 0.016f);
            }

            for (int i = 0; i < 1000; ++i)
            {
                Assert.AreEqual(frozen[i], _driver.Matrices[i], $"实体 {i} 矩阵未冻结");
            }

            Assert.AreEqual(1, _renderer.LastSubmitCalls, "行为停止 ≠ 渲染停止：冻结画面仍正常提交");
            UnityEngine.Debug.Log("[A-AND-08] PlayMode kernel 真实性：World 销毁 → 行为停止/矩阵冻结/渲染仍提交（无异常）。");
        }

        // ---- 用例 3：A/B 帧耗（场景内、同实体数 1000，双口径）----
        [UnityTest]
        public IEnumerator AB_FrameCost_InScene_KernelVsCSharpArray_DualGauge()
        {
            yield return null;
            EnsureCrowd();
            var baseline = new CrowdArrayBaseline();

            const int frames = 200;
            const float dt = 0.016f;
            _driver.Tick(dt);     // 预热
            baseline.Tick(dt);

            var kernelFrames = new double[frames];
            var arrayFrames = new double[frames];
            for (int i = 0; i < frames; ++i)
            {
                yield return null; // 每隔若干帧让出主线程，保持 PlayMode 心跳

                long t0 = Stopwatch.GetTimestamp();
                _queue.TickAll(TickStage.Logic, dt); // 生产泵路径（本队列只有群体驱动）
                kernelFrames[i] = ElapsedUs(t0);

                long t1 = Stopwatch.GetTimestamp();
                baseline.Tick(dt);
                arrayFrames[i] = ElapsedUs(t1);
            }

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

            Assert.Less(kernelFrameMs, 16.6, "kernel 均帧耗超出 60fps 帧预算");
            Assert.Less(arrayFrameMs, 16.6, "数组均帧耗超出 60fps 帧预算");
            Assert.Less(kernelTickUs, Math.Max(arrayTickUs * 3.0, arrayTickUs + 250.0),
                "kernel 数据侧单帧 > 3×数组路径（或 +250µs）= 病态回归");

            UnityEngine.Debug.Log(
                "[A-AND-08] PlayMode A/B（场景内，1000 实体，" + frames + " 帧均值）\n" +
                $"  FrameGovernor 口径（数据侧均帧耗 ms）：kernel={kernelFrameMs:F4} array={arrayFrameMs:F4} " +
                $"ratio={kernelFrameMs / Math.Max(arrayFrameMs, 1e-9):F2}（governor level: " +
                $"kernel={governorKernel.Level} array={governorArray.Level}，TargetMs=16.6）\n" +
                $"  Stopwatch 口径（每帧 µs）：kernelTick={kernelTickUs:F1} arrayTick={arrayTickUs:F1} ratio={ratio:F2}\n" +
                $"  渲染提交段（两路共享同成本，单测口径）：submitUs={_renderer.LastRenderSubmitUs:F1}\n" +
                $"  验收线判定：{(ratio <= 1.2 ? $"PASS（+{(ratio - 1.0) * 100.0:F1}% ≤ +20%）" : $"超出（×{ratio:F2}）→ 如实报告")}");

            yield return null;
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

        /// <summary>测试注入的帧耗样本（batchmode 无 GPU 计时 → CpuFrameMs 降级口径）。</summary>
        private sealed class MeasuredTelemetry : IFrameTelemetry
        {
            private float _cpuMs;

            public float GpuFrameMs => 0f;

            public float CpuFrameMs => _cpuMs;

            public MeasuredTelemetry WithCpu(float cpuMs)
            {
                _cpuMs = cpuMs;
                return this;
            }
        }
    }
}
