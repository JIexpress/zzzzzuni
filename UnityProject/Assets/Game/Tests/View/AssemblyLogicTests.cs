// AssemblyLogicTests.cs —— 装配切片纯逻辑 EditMode 用例 (TASK-A-TECH-02)。
// 覆盖：连接点类型兼容矩阵 / 外法线的规范 Up 推导 / 连接点世界位姿与归属 /
// 零件连接点的懒收集。物理行为（关节、推力、稳定性）在 PlayMode 用例取证。

using System.Collections.Generic;
using Game.View.Assembly;
using NUnit.Framework;
using UnityEngine;

namespace Game.View.Tests
{
    public sealed class AssemblyLogicTests
    {
        private readonly List<Object> _owned = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object owned in _owned)
            {
                if (owned != null)
                {
                    Object.DestroyImmediate(owned);
                }
            }

            _owned.Clear();
        }

        private T Track<T>(T owned) where T : Object
        {
            _owned.Add(owned);
            return owned;
        }

        [Test]
        public void Compatibility_StructuralIsUniversal_MatchedTypesDock()
        {
            // 结构面是万能面。
            Assert.IsTrue(AttachmentPoint.AreCompatible(AttachmentPointType.Structural, AttachmentPointType.Structural));
            Assert.IsTrue(AttachmentPoint.AreCompatible(AttachmentPointType.Structural, AttachmentPointType.ThrusterMount));
            Assert.IsTrue(AttachmentPoint.AreCompatible(AttachmentPointType.Structural, AttachmentPointType.WheelHub));
            Assert.IsTrue(AttachmentPoint.AreCompatible(AttachmentPointType.ThrusterMount, AttachmentPointType.Structural));
            Assert.IsTrue(AttachmentPoint.AreCompatible(AttachmentPointType.WheelHub, AttachmentPointType.Structural));
            // 同类可对接。
            Assert.IsTrue(AttachmentPoint.AreCompatible(AttachmentPointType.ThrusterMount, AttachmentPointType.ThrusterMount));
            Assert.IsTrue(AttachmentPoint.AreCompatible(AttachmentPointType.WheelHub, AttachmentPointType.WheelHub));
            // 异类专用接口互斥。
            Assert.IsFalse(AttachmentPoint.AreCompatible(AttachmentPointType.ThrusterMount, AttachmentPointType.WheelHub));
            Assert.IsFalse(AttachmentPoint.AreCompatible(AttachmentPointType.WheelHub, AttachmentPointType.ThrusterMount));
        }

        [Test]
        public void PickPerpendicularUp_IsDeterministic_AndPerpendicularToForward()
        {
            Assert.AreEqual(new Vector3(0f, 1f, 0f), AssemblyBuilder.PickPerpendicularUp(Vector3.forward));
            Assert.AreEqual(new Vector3(0f, 1f, 0f), AssemblyBuilder.PickPerpendicularUp(Vector3.right));
            Assert.AreEqual(new Vector3(0f, 0f, 1f), AssemblyBuilder.PickPerpendicularUp(Vector3.up));

            // 垂直性契约只覆盖两类输入：|y| < 0.9（Up=世界 up）与水平分量为 0 的
            // 近垂直法线（Up=世界 forward，此时法线 z=0 才垂直）。带 z 分量的
            // 近垂直法线（如 (0.3,0.9,0.3)）只保证"不平行、可作 LookRotation 参考"。
            foreach (Vector3 forward in new[]
            {
                Vector3.forward, Vector3.back, Vector3.right, Vector3.left, Vector3.up, Vector3.down,
                new Vector3(0.2f, 0.98f, 0f).normalized,
            })
            {
                Vector3 up = AssemblyBuilder.PickPerpendicularUp(forward);
                Assert.Less(Mathf.Abs(Vector3.Dot(up, forward.normalized)), 1e-4f,
                    $"forward={forward} 的 Up 必须与法线垂直");
            }
        }

        [Test]
        public void AddAttachmentPoint_PlacesChildWithWorldPoseAndOwnership()
        {
            var go = Track(new GameObject("t-part", typeof(Rigidbody)));
            go.transform.position = new Vector3(1f, 2f, 3f);
            go.transform.rotation = Quaternion.Euler(0f, 90f, 0f);

            AssemblyPart part = go.AddComponent<AssemblyPart>();
            AttachmentPoint point = part.AddAttachmentPoint(
                AttachmentPointType.ThrusterMount, new Vector3(0f, 0f, 0.5f), Vector3.forward);

            Assert.AreEqual(AttachmentPointType.ThrusterMount, point.Type);
            Assert.AreEqual(part, point.Owner, "连接点归属必须解析到宿主零件");
            Assert.IsFalse(point.IsOccupied);
            // 零件朝向 +90° yaw：局部 +Z 偏移 → 世界 +X。
            Assert.Less((point.WorldPosition - new Vector3(1.5f, 2f, 3f)).magnitude, 1e-4f, "连接点世界位置");
            Assert.Less((point.WorldForward - Vector3.right).magnitude, 1e-4f, "连接点世界法线应随宿主旋转");
        }

        [Test]
        public void Points_LazilyCollects_Children()
        {
            var go = Track(new GameObject("t-part", typeof(Rigidbody)));
            AssemblyPart part = go.AddComponent<AssemblyPart>();

            Assert.AreEqual(0, part.Points.Count, "初始无连接点");
            part.AddAttachmentPoint(AttachmentPointType.Structural, Vector3.zero, Vector3.forward);
            part.AddAttachmentPoint(AttachmentPointType.WheelHub, Vector3.zero, Vector3.right);
            Assert.AreEqual(2, part.Points.Count, "AddAttachmentPoint 后应立即可见");

            part.RefreshPoints();
            Assert.AreEqual(2, part.Points.Count, "RefreshPoints 幂等");
        }
    }
}
