// AssemblyConnectPlayModeTests.cs —— 装配切片连接/拆卸/吸附/稳定性证据
// (TASK-A-TECH-02)。物理用确定性手动仿真驱动（Physics.simulationMode = Script +
// Physics.Simulate），与帧率无关、可复现。
//
// 判据与 TECH-ROADMAP 方向二一致：
//   ① 连接后单一物理体（对接点相对速度/相对角速度≈0 + 同向位移 + 复合体 BFS）
//   ③ 拆卸（Break joint）后分离（水平分离增长 + 自由下落）
//   ④ 吸附候选 + 预览高亮（MeshRenderer 颜色断言，控制组复原）
//   ④b 端到端：抓取 → 吸附高亮 → 释放连接
//   ⑤ 连接体 10 秒物理仿真不抖动不爆炸（关节锚点误差 / 速度上限 / NaN）
//
// 对接方向约定：Connect(movingPart, movingPoint, targetPoint) 把 moving 点对到
// target 点上、外法线相对——所以调用方必须选择**朝向对方**的那个点（例如 A 的
// +Z 面点对 B 的 -Z 面点），否则两零件会同侧重叠。测试全部按此约定构造。

using System.Collections;
using System.Collections.Generic;
using Game.View.Assembly;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace ZFrame.PlayMode.Tests
{
    public sealed class AssemblyConnectPlayModeTests : AssemblyPlayModeTestBase
    {
        /// <summary>① 连接后两零件成为单一物理体：拖一个另一个跟随。</summary>
        [UnityTest]
        public IEnumerator ConnectedPair_TransmitsVelocity_AsOneRigidBody()
        {
            AssemblyPart a = Track(AssemblyPartFactory.CreateCube("t-cube-a", new Vector3(0f, 5f, 0f)));
            AssemblyPart b = Track(AssemblyPartFactory.CreateCube("t-cube-b", new Vector3(0f, 5f, 3f)));
            a.Body.useGravity = false;
            b.Body.useGravity = false;

            // A 的 +Z 面点对到 B 的 -Z 面点（面贴合，不重叠）。A 是移动侧。
            AssemblyJoint joint = AssemblyBuilder.Connect(a, a.Points[0], b.Points[1]);
            Assert.IsNotNull(joint, "Connect 必须产出 AssemblyJoint");
            Assert.IsFalse(joint.IsBroken);

            // 对齐吸附：两连接点相距一个对接缝间隙（面不接触）、外法线相对。
            float pointGap = Vector3.Distance(a.Points[0].WorldPosition, b.Points[1].WorldPosition);
            Assert.Less(pointGap, AssemblyBuilder.DockGapMeters + 0.005f,
                $"连接点间隙应 = 对接缝间隙 {AssemblyBuilder.DockGapMeters} m（实测 {pointGap:F4}）");
            Assert.Less(Vector3.Dot(a.Points[0].WorldForward, b.Points[1].WorldForward), -0.999f,
                "两外法线必须相对（面贴合）");
            Assert.Less(b.Body.position.z - a.Body.position.z, 1.1f, "A 应贴合在 B 的 -Z 面外侧");
            Assert.Greater(b.Body.position.z - a.Body.position.z, 0.9f, "两零件中心距应恰为一个边长（不重叠）");

            // 复合体 = 两零件的连通图。
            List<AssemblyPart> composite = AssemblyBuilder.GetComposite(a);
            Assert.AreEqual(2, composite.Count, "连接后复合体应包含两零件");
            Assert.IsTrue(composite.Contains(b));

            // 单一物理体：给 A 速度，30 步后刚体不变量成立且 B 跟随。
            // 注意正确的"单一物理体"判据不是 |vA - vB|≈0：偏置焊接对受单侧冲量
            // 会绕公共质心自旋（角动量 r×p 守恒，实测 ω=(0,-1.2,0) 与刚体解析解
            // 一致），两体质心速度自然不同。刚性不变量是——
            //   a. 相对角速度 ≈ 0；
            //   b. 对接点处相对速度（v + ω×r）≈ 0；
            //   c. 对接点间距保持 DockGap 不变。
            a.Body.velocity = new Vector3(2f, 0f, 0f);
            // 1.5 s（75 步）：焊接对在自旋（解析解 x_B(t)=t-0.5·sin(1.2t)），
            // B 的切向速度初期为负，0.6 s 时才 +0.27 m——给足时间看"跟随"。
            StepPhysics(75);

            Vector3 wRelative = a.Body.angularVelocity - b.Body.angularVelocity;
            Assert.Less(wRelative.magnitude, 0.05f,
                $"相对角速度应≈0（实测 {wRelative.magnitude:F4}）→ 单一物理体");

            Vector3 offsetA = a.Points[0].WorldPosition - a.Body.worldCenterOfMass;
            Vector3 offsetB = b.Points[1].WorldPosition - b.Body.worldCenterOfMass;
            Vector3 dockVelocityA = a.Body.velocity + Vector3.Cross(a.Body.angularVelocity, offsetA);
            Vector3 dockVelocityB = b.Body.velocity + Vector3.Cross(b.Body.angularVelocity, offsetB);
            float dockRelativeSpeed = (dockVelocityA - dockVelocityB).magnitude;
            Assert.Less(dockRelativeSpeed, 0.15f,
                $"对接点相对速度应≈0（实测 {dockRelativeSpeed:F4}，vA={a.Body.velocity} vB={b.Body.velocity}）→ 单一物理体");

            float dockGapNow = Vector3.Distance(a.Points[0].WorldPosition, b.Points[1].WorldPosition);
            Assert.Less(Mathf.Abs(dockGapNow - AssemblyBuilder.DockGapMeters), 0.02f,
                "对接点间距必须保持刚体不变（锁死）");

            Assert.Greater(b.Body.position.x, 0.5f, "B 必须随 A 沿 +X 位移");
            Vector3 bDisplacement = b.Body.position - new Vector3(0f, 5f, 3f);
            Assert.Greater(Vector3.Dot(bDisplacement, Vector3.right), 0.5f, "B 的位移必须与施加速度同向");

            yield return null;
        }

        /// <summary>③ 拆卸 joint 后两零件分离自由下落。</summary>
        [UnityTest]
        public IEnumerator Disconnect_SeparatesBodies_IntoFreeFall()
        {
            AssemblyPart a = Track(AssemblyPartFactory.CreateCube("t-cube-a", new Vector3(0f, 8f, 0f)));
            AssemblyPart b = Track(AssemblyPartFactory.CreateCube("t-cube-b", new Vector3(0f, 8f, 3f)));

            AssemblyJoint joint = AssemblyBuilder.Connect(a, a.Points[0], b.Points[1]);
            Assert.IsNotNull(joint);
            Assert.AreEqual(2, AssemblyBuilder.GetComposite(a).Count, "连接态复合体 = 2");

            StepPhysics(5); // 让关节约束稳定一拍

            float yBefore = 0.5f * (a.Body.position.y + b.Body.position.y);
            float xSeparationBefore = Mathf.Abs(a.Body.position.x - b.Body.position.x);

            AssemblyBuilder.Disconnect(joint);
            Assert.IsTrue(joint.IsBroken, "Disconnect 后 joint 必须标记为 Broken");
            Assert.IsNull(a.Points[0].Occupant, "连接点必须回到 free");
            Assert.AreEqual(1, AssemblyBuilder.GetComposite(a).Count, "拆卸后复合体应只剩自身");
            Assert.AreEqual(1, AssemblyBuilder.GetComposite(b).Count);

            a.Body.velocity += new Vector3(1.5f, 0f, 0f);
            // 关节销毁走 Object.Destroy（帧末生效，与真实玩法一致）。手动仿真在
            // 同一帧内连步 50 步会把"这一帧的残留约束"放大成 1 秒物理——先跨一帧
            // 让销毁落地，再进入纯自由落体。
            yield return null;
            StepPhysics(50); // 1 s 自由下落

            float xSeparationAfter = Mathf.Abs(a.Body.position.x - b.Body.position.x);
            Assert.Greater(xSeparationAfter - xSeparationBefore, 0.4f,
                $"拆卸后水平分离必须增长（实测 before={xSeparationBefore:F3} after={xSeparationAfter:F3} " +
                $"vA={a.Body.velocity} vB={b.Body.velocity} aPos={a.Body.position} bPos={b.Body.position}）");
            float yAfter = 0.5f * (a.Body.position.y + b.Body.position.y);
            Assert.Less(yAfter, yBefore - 3f, "两零件都必须自由下落（重力生效）");

            yield return null;
        }

        /// <summary>RGBA 平方距离（Color 没有 sqrMagnitude，走 Vector4）。</summary>
        private static float SqrDistance(Color x, Color y)
        {
            Vector4 delta = (Vector4)x - (Vector4)y;
            return delta.sqrMagnitude;
        }

        /// <summary>④ 吸附候选搜索 + 预览高亮（颜色断言 + 复原控制组）。</summary>
        [UnityTest]
        public IEnumerator SnapSearch_HighlightsCandidate_AndRestoresColor()
        {
            AssemblyPart a = Track(AssemblyPartFactory.CreateCube("t-cube-a", new Vector3(0f, 5f, 0.7f)));
            AssemblyPart b = Track(AssemblyPartFactory.CreateCube("t-cube-b", new Vector3(0f, 5f, 0f)));

            Color baseline = a.CurrentColor;

            // 正例：A 的自由连接点（-Z 面，z=0.2）进入 B 中心的吸附半径 0.75。
            bool found = AssemblyBuilder.TryFindSnap(
                b.Body.position, new AssemblyBuilder.AssemblyPartTypeFilter(b, null), 0.75f,
                out AssemblyBuilder.SnapCandidate candidate);
            Assert.IsTrue(found, "0.7 m 内必须找到吸附候选");
            Assert.AreEqual(a, candidate.Part);
            Assert.AreEqual(AttachmentPointType.Structural, candidate.Point.Type);

            // 预览高亮打在"提供连接点的候选零件"上（拖拽控制器的
            // SetSnapCandidate 语义：point.Owner 高亮），即 A。
            a.SetHighlighted(true);
            Color highlighted = a.CurrentColor;
            Assert.Greater(SqrDistance(highlighted, baseline), 0.01f, "高亮必须改变 MeshRenderer 颜色");

            a.SetHighlighted(false);
            Color restored = a.CurrentColor;
            Assert.Less(SqrDistance(restored, baseline), 1e-6f, "取消高亮必须复原基线色");

            // 反例：拉开距离后没有候选。
            b.transform.position = new Vector3(0f, 5f, 3f);
            Physics.SyncTransforms();
            bool foundFar = AssemblyBuilder.TryFindSnap(
                b.Body.position, new AssemblyBuilder.AssemblyPartTypeFilter(b, null), 0.75f, out _);
            Assert.IsFalse(foundFar, "3 m 外不应有吸附候选");

            yield return null;
        }

        /// <summary>④b 端到端：抓取 → 吸附高亮 → 释放连接（拖拽交互闭环）。</summary>
        [UnityTest]
        public IEnumerator GrabDragRelease_EndToEnd_ConnectsOnSnap()
        {
            AssemblyPart a = Track(AssemblyPartFactory.CreateCube("t-cube-a", new Vector3(0f, 5f, 0.7f)));
            AssemblyPart b = Track(AssemblyPartFactory.CreateCube("t-cube-b", new Vector3(0f, 5f, 0f)));

            GameObject cameraGo = Track(new GameObject("t-camera", typeof(Camera), typeof(AudioListener)));
            cameraGo.tag = "MainCamera";
            Camera camera = cameraGo.GetComponent<Camera>();
            camera.transform.position = new Vector3(0f, 5.5f, -3.5f);
            camera.transform.rotation = Quaternion.LookRotation(
                (new Vector3(0f, 5f, 0.35f) - camera.transform.position).normalized);

            GameObject rigGo = Track(new GameObject("t-rig", typeof(AssemblyDragController)));
            AssemblyDragController rig = rigGo.GetComponent<AssemblyDragController>();
            var serialized = new UnityEditor.SerializedObject(rig);
            serialized.FindProperty("_camera").objectReferenceValue = camera;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Vector3 screen = camera.WorldToScreenPoint(b.Body.position);
            Assert.Greater(screen.z, 0f, "前置：B 必须在相机前方");
            Assert.IsTrue(rig.TryGrab(screen), "必须抓到 B");
            Assert.AreEqual(b, rig.HeldPart);
            Assert.IsFalse(b.Body.useGravity, "被抓零件应暂时关闭重力（拖拽稳定）");

            Assert.IsTrue(rig.RefreshSnapCandidate(), "释放前必须给出吸附候选");
            Assert.AreEqual(a.Points[1], rig.SnapCandidatePoint, "候选应为 A 的 -Z 面连接点");
            Assert.IsTrue(a.IsHighlighted, "候选零件必须被高亮");

            AssemblyJoint joint = rig.TryRelease();
            Assert.IsNotNull(joint, "释放到吸附候选上必须建立连接");
            Assert.IsFalse(a.IsHighlighted, "释放后高亮必须清除");
            Assert.AreEqual(2, AssemblyBuilder.GetComposite(b).Count, "释放后两零件成一体");

            StepPhysics(20);
            float pointGap = Vector3.Distance(a.Points[1].WorldPosition, b.Points[0].WorldPosition);
            Assert.Less(pointGap, 0.05f, "物理稳定后连接点仍须重合");

            yield return null;
        }

        /// <summary>
        /// ⑤ 稳定性硬指标：A-B-推进器三体链在地面静置 10 秒物理仿真，
        /// 关节锚点误差 < 5 cm、无 NaN、无爆炸（速度/位移有界）。
        /// </summary>
        [UnityTest]
        public IEnumerator ConnectedChain_GroundSettled_10Seconds_NoJitterNoExplosion()
        {
            GameObject ground = Track(GameObject.CreatePrimitive(PrimitiveType.Plane));
            ground.name = "t-ground";
            ground.transform.position = Vector3.zero;

            AssemblyPart a = Track(AssemblyPartFactory.CreateCube("t-cube-a", new Vector3(0f, 0.5f, 0f)));
            AssemblyPart b = Track(AssemblyPartFactory.CreateCube("t-cube-b", new Vector3(0f, 0.5f, 3f)));
            AssemblyPart thruster = Track(AssemblyPartFactory.CreateThruster("t-thruster", new Vector3(3f, 0.45f, 0f)));

            // B 对到 A 的 +Z 面（B 移动），推进器对到 A 的 -Z 面（推进器移动）。
            Assert.IsNotNull(AssemblyBuilder.Connect(b, b.Points[1], a.Points[0]), "A+B 连接");
            Assert.IsNotNull(AssemblyBuilder.Connect(thruster, thruster.Points[0], a.Points[1]), "A+推进器连接");

            StepPhysics(100); // 2 s 静置（落地 + 关节稳定）

            AttachmentPoint anchorA = a.Points[0];
            AttachmentPoint anchorB = b.Points[1];
            AttachmentPoint anchorT = a.Points[1];
            AttachmentPoint anchorThruster = thruster.Points[0];

            Vector3 aStart = a.Body.position;
            float maxAnchorError = 0f;
            float maxSpeed = 0f;

            for (int step = 0; step < 500; step++) // 500 x 0.02 s = 10 s
            {
                Physics.Simulate(0.02f);

                float error = Mathf.Max(
                    Vector3.Distance(anchorA.WorldPosition, anchorB.WorldPosition),
                    Vector3.Distance(anchorT.WorldPosition, anchorThruster.WorldPosition));
                maxAnchorError = Mathf.Max(maxAnchorError, error);
                maxSpeed = Mathf.Max(maxSpeed, a.Body.velocity.magnitude + b.Body.velocity.magnitude);

                Assert.IsFalse(float.IsNaN(a.Body.position.x) || float.IsNaN(b.Body.position.x),
                    $"第 {step} 步出现 NaN（爆炸）");
            }

            Assert.Less(maxAnchorError, 0.05f, $"10 秒内关节锚点最大误差 {maxAnchorError:F4} m，须 < 0.05（不抖动）");
            Assert.Less(maxSpeed, 3f, "速度必须有界（不爆炸）");
            Assert.Less((a.Body.position - aStart).magnitude, 1.5f, "A 不得漂离初始位置超过 1.5 m");
            Assert.AreEqual(3, AssemblyBuilder.GetComposite(a).Count, "10 秒后三体仍是一个复合体（joint 未断）");

            yield return null;
        }
    }
}
