// KernelCrowdPlayModeTests.cs —— 千人演练场 PlayMode 证据（TASK-A-R9-13）。
// 群体可见（提交层：1000 实例 / 1 次 DrawMeshInstanced）+ 同一次运行内
// 100 vs 1000 实体 A/B（DrawCall 不随实体数线性增长）+ 英雄侧 FSM 不回归
// + 场景卸载 Dispose() 无句柄泄漏。
// 真实 GPU DrawCall / 像素在 -batchmode 无渲染负载下不可信，见施工报告受限项。

using System.Collections;
using Game.Domain.Enemy;
using Game.View.Effects;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using ZFrame.Core;
using ZFrame.Gameplay;
using ZFrame.Render.Bindings;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>群体接入后：提交证据、A/B 判据、英雄不回归、句柄不泄漏。</summary>
    public sealed class KernelCrowdPlayModeTests
    {
        private KernelCrowdHost _host;

        [SetUp]
        public void SetUp()
        {
            GameRoot.Bootstrap(); // 幂等：确保 ITickQueue / TickQueue 已注册
            _host = KernelCrowdHost.EnsureInstance();
            _host.DisposeCrowd();  // 从干净状态起（auto-boot 默认不启用群体）
        }

        [TearDown]
        public void TearDown()
        {
            _host?.DisposeCrowd();
            _host = null;
        }

        [UnityTest]
        public IEnumerator Crowd_1000Entities_SingleSubmit_And_HeroFsmNotRegressed()
        {
            yield return null; // 让 runner 场景稳定

            TickQueue queue = Services.Get<TickQueue>();
            int logicBaseline = queue.Count(TickStage.Logic);
            int viewBaseline = queue.Count(TickStage.View);

            _host.EnsureCrowd();
            Assert.IsTrue(_host.CrowdActive);
            KernelCrowdDriver driver = _host.Driver;
            KernelCrowdRenderer renderer = _host.Renderer;

            Assert.AreEqual(1000, driver.EntityCount);
            Assert.AreEqual(1000UL, driver.World.GetStats().EntityCount);
            Assert.AreEqual(499500.0, driver.PullSum(), "真 dll：初始 X=序号，求和精确");
            Assert.AreEqual(logicBaseline + 1, queue.Count(TickStage.Logic), "群体积分走 Logic 档");
            Assert.AreEqual(viewBaseline + 1, queue.Count(TickStage.View), "批量绘制走 View 档");

            // 经 ITickQueue 驱动三帧（Logic 积分 → View 提交），非 MonoBehaviour.Update。
            for (int i = 0; i < 3; ++i)
            {
                queue.TickAll(TickStage.Logic, 0.016f);
                queue.TickAll(TickStage.View, 0.016f);
            }

            yield return null;

            Assert.AreEqual(1000u, driver.LastTotalRows, "每帧游标读回全部 1000 行");
            Assert.IsTrue(renderer.IsShaderAvailable,
                "URP 兼容材质须解析成功（shader=" + renderer.ResolvedShaderName + "）");
            Assert.AreEqual(1, renderer.LastSubmitCalls,
                "1000 <= 单次上限 1023 → 一次 DrawMeshInstanced（DrawCall 不随实体数线性增长）");
            Assert.AreEqual(1000, renderer.LastSubmittedInstances);
            Assert.GreaterOrEqual(renderer.LastRenderSubmitUs, 0.0);

            // 同一次运行内 A/B：100 与 1000 实体，提交次数恒为 1。
            Assert.AreEqual(1, renderer.Submit(100));
            Assert.AreEqual(100, renderer.LastSubmittedInstances);
            Assert.AreEqual(1, renderer.Submit(1000));
            Assert.AreEqual(1000, renderer.LastSubmittedInstances);

            // 英雄侧不回归：Domain 层敌人 FSM 初始 Idle → 计时后 Patrol
            // （照 EnemyFsmTests 的既有断言；群体接入不得改变英雄侧行为）。
            var fsm = new EnemyFsm("hero-probe", Vec2.Zero, new[] { new Vec2(5f, 0f) },
                new EnemyTuning(4f, 6f, 1.5f, 3f, 1f, 0.1f));
            fsm.SetPlayerPosition(new Vec2(100f, 100f));
            Assert.AreEqual(EnemyStateId.Idle, fsm.StateId);
            fsm.Tick(0.5f);
            Assert.AreEqual(EnemyStateId.Idle, fsm.StateId);
            fsm.Tick(0.6f);
            Assert.AreEqual(EnemyStateId.Patrol, fsm.StateId, "英雄侧 FSM 状态转换不受群体影响");

            Debug.Log($"[A-R9-13] PlayMode：entities=1000 totalRows={driver.LastTotalRows} "
                + $"submitCalls={renderer.LastSubmitCalls} submittedInstances={renderer.LastSubmittedInstances} "
                + $"shader={renderer.ResolvedShaderName} "
                + $"kernelIterationUs={driver.LastKernelIterationUs:F1} "
                + $"renderSubmitUs={renderer.LastRenderSubmitUs:F1}。");
        }

        [UnityTest]
        public IEnumerator Dispose_ReleasesWorld_NoHandleLeak()
        {
            yield return null;

            ITickQueue ticks = Services.Get<ITickQueue>();
            int logicBaseline = ticks.Count(TickStage.Logic);
            int viewBaseline = ticks.Count(TickStage.View);

            // 原生句柄表上限 8 世界：连续 10 次「启用→场景卸载 Dispose」不抛，
            // 且每次取到的 World 在 Dispose 后 IsDisposed=true（句柄确实失效）。
            for (int i = 0; i < 10; ++i)
            {
                _host.EnsureCrowd();
                ZKernelWorld world = _host.Driver.World;
                Assert.IsFalse(world.IsDisposed);
                Assert.AreEqual(1000UL, world.GetStats().EntityCount);

                _host.DisposeCrowd();
                Assert.IsTrue(world.IsDisposed, "场景卸载 Dispose() 后句柄必须失效");
                Assert.IsFalse(_host.CrowdActive);
            }

            Assert.AreEqual(logicBaseline, ticks.Count(TickStage.Logic), "Dispose 后 Logic 档回到基线");
            Assert.AreEqual(viewBaseline, ticks.Count(TickStage.View), "Dispose 后 View 档回到基线");
        }
    }
}
