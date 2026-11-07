// AssemblySceneSmokePlayModeTests.cs —— SCN_Assembly 演示场景冒烟 (TASK-A-TECH-02)。
// 真实 LoadSceneAsync 加载 Setup 脚本产物，断言演示场景的可玩要素齐备：
// 相机 / 地面 / 4 零件（方块x2 + 推进器 + 轮子）/ 连接点数 / 推进器 / 拖拽 Rig。
// 场景加载是单模式替换，沿用 UIRootCrossSceneEventSystemPlayModeTests 的先例
//（加载后不复原场景，由后续用例自建环境）。

using System.Collections;
using Game.View.Assembly;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ZFrame.PlayMode.Tests
{
    public sealed class AssemblySceneSmokePlayModeTests
    {
        private const string SceneName = "SCN_Assembly";

        [UnityTest]
        public IEnumerator DemoScene_Loads_AndContainsPlayablePieces()
        {
            AsyncOperation load = SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            Assert.IsNotNull(load, $"{SceneName} 必须在 Build Settings 中（AssemblySceneSetup 已注册）");
            yield return load;

            Assert.AreEqual(SceneName, SceneManager.GetActiveScene().name);

            Assert.IsNotNull(Camera.main, "演示场景必须有主相机");
            Assert.IsNotNull(GameObject.Find("Ground"), "必须有地面");
            Assert.IsNotNull(GameObject.Find("AssemblyRig"), "必须有 AssemblyRig（拖拽交互宿主）");
            Assert.IsNotNull(Object.FindObjectOfType(typeof(AssemblyDragController)), "必须挂 AssemblyDragController");

            AssemblyPart[] parts = Object.FindObjectsOfType<AssemblyPart>();
            Assert.AreEqual(4, parts.Length, "演示场景应有 4 个零件（方块x2/推进器/轮子）");

            int pointCount = 0;
            int thrusterCount = 0;
            int wheelCount = 0;
            foreach (AssemblyPart part in parts)
            {
                pointCount += part.Points.Count;
                if (part.GetComponent<AssemblyThruster>() != null)
                {
                    thrusterCount++;
                }

                foreach (AttachmentPoint point in part.Points)
                {
                    if (point.Type == AttachmentPointType.WheelHub)
                    {
                        wheelCount++;
                    }
                }
            }

            Assert.AreEqual(6, pointCount, "连接点总数应为 6（每方块 2 + 推进器 1 + 轮毂 1）");
            Assert.AreEqual(1, thrusterCount, "应恰有 1 个推进器");
            Assert.AreEqual(1, wheelCount, "应恰有 1 个轮毂点");

            // 让场景里的零件在真实固定步进下静置一拍，不应报错或穿透。
            yield return new WaitForSeconds(0.25f);
            foreach (AssemblyPart part in parts)
            {
                Assert.IsFalse(float.IsNaN(part.Body.position.y), "零件位置不得 NaN");
                Assert.LessOrEqual(part.Body.position.y, 1.5f, "零件应留在地面附近");
            }
        }
    }
}
