// SurvivalLoopStateTests.cs —— TASK-A-LOOP-01：最小可玩循环的纯逻辑断言。
// 覆盖：计时胜利 / 耐久失败 / 结束后幂等 / 波次曲线单调 / 波次消费只发一次。

using Game.Domain.Survival;
using NUnit.Framework;

namespace Game.Domain.Tests
{
    /// <summary>守住火堆的状态机：输赢、幂等、压力曲线。</summary>
    public sealed class SurvivalLoopStateTests
    {
        [Test]
        public void TickingToTheDuration_Wins_AndFreezes()
        {
            var state = new SurvivalLoopState(90f, 100);
            state.Start();
            Assert.AreEqual(SurvivalPhase.Running, state.Phase);

            state.Tick(89f);
            Assert.AreEqual(SurvivalPhase.Running, state.Phase, "89s 还没到点");
            Assert.AreEqual(1f, state.RemainingSeconds, 1e-3f);

            state.Tick(2f);
            Assert.AreEqual(SurvivalPhase.Won, state.Phase, "越过 90s 必须判胜");
            Assert.AreEqual(0f, state.RemainingSeconds, 1e-4f);
            Assert.AreEqual(90f, state.ElapsedSeconds, 1e-4f, "时间必须夹在时长上，不能超");

            float frozen = state.ElapsedSeconds;
            state.Tick(5f);
            Assert.AreEqual(frozen, state.ElapsedSeconds, 1e-6f, "胜利后计时必须冻结");
            Assert.AreEqual(SurvivalPhase.Won, state.Phase);
        }

        [Test]
        public void FireDamageToZero_Loses_AndLaterDamageIsIgnored()
        {
            var state = new SurvivalLoopState(90f, 30);
            state.Start();

            Assert.IsTrue(state.ApplyFireDamage(20));
            Assert.AreEqual(10, state.FireHp);
            Assert.AreEqual(SurvivalPhase.Running, state.Phase);

            Assert.IsTrue(state.ApplyFireDamage(20), "溢出伤害仍然生效");
            Assert.AreEqual(0, state.FireHp, "耐久必须夹在 0");
            Assert.AreEqual(SurvivalPhase.Lost, state.Phase);

            Assert.IsFalse(state.ApplyFireDamage(5), "失败后不再接受伤害");
            Assert.AreEqual(0, state.FireHp);
            Assert.AreEqual(SurvivalPhase.Lost, state.Phase);
        }

        [Test]
        public void FinishedLoop_IgnoresTickDamageAndWaves()
        {
            var state = new SurvivalLoopState(10f, 10);
            state.Start();
            state.Tick(10f);
            Assert.AreEqual(SurvivalPhase.Won, state.Phase);

            Assert.IsFalse(state.ApplyFireDamage(3), "已结束不吃伤害");
            Assert.AreEqual(10, state.FireHp);
            Assert.IsFalse(state.TryConsumeWave(), "已结束不再刷波");

            state.Reset();
            Assert.AreEqual(SurvivalPhase.Idle, state.Phase);
            Assert.AreEqual(10, state.FireHp);
            Assert.AreEqual(0f, state.ElapsedSeconds);
            Assert.AreEqual(0, state.WaveIndex);
        }

        [Test]
        public void WaveCurve_GrowsInSize_AndShrinksInInterval()
        {
            Assert.AreEqual(1, SurvivalLoopState.WaveSize(1));
            Assert.AreEqual(1, SurvivalLoopState.WaveSize(3));
            Assert.AreEqual(2, SurvivalLoopState.WaveSize(4), "每 3 波 +1 个");
            Assert.AreEqual(3, SurvivalLoopState.WaveSize(7));

            for (int i = 1; i < 30; i++)
            {
                Assert.GreaterOrEqual(SurvivalLoopState.WaveSize(i + 1),
                    SurvivalLoopState.WaveSize(i), "波次人数必须单调不减");
                Assert.LessOrEqual(SurvivalLoopState.WaveInterval(i + 1),
                    SurvivalLoopState.WaveInterval(i), "波次间隔必须单调不增");
                Assert.GreaterOrEqual(SurvivalLoopState.WaveInterval(i),
                    SurvivalLoopState.MinWaveIntervalSeconds, "间隔有下限");
            }

            Assert.AreEqual(SurvivalLoopState.FirstWaveDelaySeconds, SurvivalLoopState.WaveInterval(1), 1e-4f);
            Assert.Less(SurvivalLoopState.WaveInterval(5), SurvivalLoopState.WaveInterval(1),
                "第 5 波必须比第 1 波更密");
        }

        [Test]
        public void WaveIsConsumedExactlyOnce()
        {
            var state = new SurvivalLoopState(60f, 100);
            state.Start();
            state.Tick(SurvivalLoopState.FirstWaveDelaySeconds + 0.1f);

            Assert.AreEqual(1, state.WaveIndex);
            Assert.IsTrue(state.TryConsumeWave(), "到点必须能取走一波");
            Assert.IsFalse(state.TryConsumeWave(), "同一波只能消费一次");
        }
    }
}
