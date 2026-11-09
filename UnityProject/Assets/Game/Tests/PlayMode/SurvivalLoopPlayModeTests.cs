// SurvivalLoopPlayModeTests.cs —— TASK-A-LOOP-01：最小可玩循环的场景级证据。
// 口径：SCN_Game 加载后循环宿主必须处于 **Idle**（不建火堆、不刷怪，避免污染
// 既有像素用例）；StartLoop 才进入 Running；Simulate 到点判胜；重开复位；耐久归零判负。
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
    }
}
