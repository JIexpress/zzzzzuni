// SurvivalLoopPlayModeTests.cs —— TASK-A-LOOP-01：最小可玩循环的场景级证据。
// 口径：SCN_Game 加载后循环宿主必须处于 **Idle**（不建火堆、不刷怪，避免污染
// 既有像素用例）；StartLoop 才进入 Running；Simulate 到点判胜；重开复位；耐久归零判负。
// TASK-A-LOOP-02 追加：两击致死、击杀记账、死的敌人不扣耐久、活敌人照扣。
// 用后必须销毁宿主（DontDestroyOnLoad），否则会漏到后续用例里。

using System.Collections;
using Game.Domain.Survival;
using Game.View.Survival;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.View.PlayMode.Tests
{
    /// <summary>守住火堆：待机 → 开始 → 胜利 → 重开 → 失败。</summary>
    public sealed class SurvivalLoopPlayModeTests
    {
        [UnityTest]
        public IEnumerator Loop_StaysIdleUntilStarted_ThenRunsWinsAndRestarts()
        {
            yield return SceneManager.LoadSceneAsync("SCN_Game");
            yield return null;
            yield return null;

            SurvivalLoop loop = SurvivalLoop.Instance;
            Assert.IsNotNull(loop, "SCN_Game 必须自动挂载循环宿主");
            try
            {
                Assert.AreEqual(SurvivalPhase.Idle, loop.State.Phase, "默认必须待机");
                Assert.IsNull(loop.Fire, "待机时不该建火堆");
                Assert.AreEqual(0, loop.AttackerCount, "待机时不该刷怪");

                loop.StartLoop();
                Assert.AreEqual(SurvivalPhase.Running, loop.State.Phase, "F/StartLoop 后必须开始");
                Assert.IsNotNull(loop.Fire, "开始后必须出现火堆");

                loop.SimulateForTests(95f);
                Assert.AreEqual(SurvivalPhase.Won, loop.State.Phase, "撑满时长必须判胜");
                Assert.AreEqual(0f, loop.State.RemainingSeconds, 1e-3f);

                loop.Restart();
                Assert.AreEqual(SurvivalPhase.Running, loop.State.Phase, "重开必须回到进行中");
                Assert.AreEqual(loop.State.MaxFireHp, loop.State.FireHp, "重开必须满耐久");
                Assert.AreEqual(0f, loop.State.ElapsedSeconds, 1e-4f, "重开必须清零计时");

                Assert.IsTrue(loop.DamageFire(loop.State.MaxFireHp), "满耐久伤害必须被接受");
                Assert.AreEqual(SurvivalPhase.Lost, loop.State.Phase, "耐久归零必须判负");
            }
            finally
            {
                if (loop != null)
                {
                    Object.Destroy(loop.gameObject);
                }
            }

            yield return null;
            Assert.IsNull(SurvivalLoop.Instance, "销毁后必须解除静态引用");
        }

        // ---- TASK-A-LOOP-02：战斗闭环 ----------------------------------------

        [Test]
        public void Attacker_TakesTwoHitsToDie_AndNeverDestroysItself()
        {
            var go = new GameObject("~SurvivalAttackerUnderTest");
            try
            {
                var attacker = go.AddComponent<SurvivalAttacker>();
                Assert.AreEqual(SurvivalAttacker.DefaultMaxHp, attacker.MaxHp);
                Assert.AreEqual(SurvivalAttacker.DefaultMaxHp, attacker.Hp);
                Assert.IsFalse(attacker.IsDead);

                Assert.IsFalse(attacker.TakeDamage(SurvivalLoop.AttackDamage), "第一击不致死");
                Assert.AreEqual(SurvivalAttacker.DefaultMaxHp - 1, attacker.Hp);
                Assert.IsFalse(attacker.IsDead, "还有血就必须还能被打");
                Assert.IsFalse(go == null, "受击不得销毁宿主");

                Assert.IsTrue(attacker.TakeDamage(SurvivalLoop.AttackDamage), "第二击必须致死");
                Assert.IsTrue(attacker.IsDead);
                Assert.AreEqual(0, attacker.Hp, "血量夹在 0");
                Assert.IsFalse(attacker.TakeDamage(SurvivalLoop.AttackDamage), "尸体不再吃伤害");
                Assert.IsFalse(go == null, "组件不得自己 Destroy——销毁权归 SurvivalLoop");
            }
            finally
            {
                Object.Destroy(go);
            }
        }

        [UnityTest]
        public IEnumerator Attack_KillsEnemy_CountsKill_AndFireSurvivesWhileLivingEnemyStillBites()
        {
            yield return SceneManager.LoadSceneAsync("SCN_Game");
            yield return null;
            yield return null;

            SurvivalLoop loop = SurvivalLoop.Instance;
            Assert.IsNotNull(loop, "SCN_Game 必须自动挂载循环宿主");
            try
            {
                loop.StartLoop();
                Assert.AreEqual(SurvivalPhase.Running, loop.State.Phase);

                int fireHp = loop.State.FireHp;
                int killsBefore = loop.State.Kills;

                // 身前 2.4m：在攻击扇区内（2.5m），同时还在火堆触及距离外（2.2m）。
                GameObject enemy = loop.SpawnAttackerForTests(new Vector3(0f, 0f, 2.4f));
                Assert.IsNotNull(enemy);
                Assert.AreEqual(1, loop.AttackerCount);
                var health = enemy.GetComponent<SurvivalAttacker>();
                Assert.IsNotNull(health, "刷怪必须挂 SurvivalAttacker");

                Assert.AreEqual(1, loop.Attack(), "第一击必须命中");
                Assert.AreEqual(1, health.Hp, "第一击只掉 1 点");
                Assert.AreEqual(killsBefore, loop.State.Kills, "没死不算击杀");

                Assert.AreEqual(1, loop.Attack(), "第二击必须命中");
                Assert.AreEqual(0, loop.AttackerCount, "致死当帧必须从列表移除");
                Assert.AreEqual(killsBefore + 1, loop.State.Kills, "击杀必须记账");
                Assert.AreEqual(fireHp, loop.State.FireHp, "被打死的敌人不许扣火堆耐久");
                Assert.AreEqual(SurvivalPhase.Running, loop.State.Phase, "击杀不改变阶段");

                yield return null;
                Assert.IsTrue(enemy == null, "致死对象必须由循环销毁");

                // V5：活着的敌人抵达火堆仍然扣耐久。
                loop.SpawnAttackerForTests(new Vector3(0f, 0f, 2.1f));
                int beforeBite = loop.State.FireHp;
                yield return null;
                yield return null;
                Assert.Less(loop.State.FireHp, beforeBite, "活敌人抵达必须扣耐久");
            }
            finally
            {
                if (loop != null)
                {
                    Object.Destroy(loop.gameObject);
                }
            }

            yield return null;
            Assert.IsNull(SurvivalLoop.Instance, "销毁后必须解除静态引用");
        }
    }
}
