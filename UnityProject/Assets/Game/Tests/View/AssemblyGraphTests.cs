// AssemblyGraphTests.cs —— AssemblyGraph 纯 C# 拓扑 EditMode 用例 (TASK-A-TECH-05)。
// 覆盖：零件/关节增删（重复 id 拒绝、未知端点拒绝、自环拒绝、点占用拒绝、
// 同点对重复连接拒绝）/ RemovePart 级联 / Validate 三查（连通性、孤立件、
// 重复连接）/ 图 <-> 快照往返一致性。物理行为在 PlayMode 用例取证。

using System.Collections.Generic;
using System.IO;
using Game.View.Assembly;
using NUnit.Framework;
using UnityEngine;

namespace Game.View.Tests
{
    public sealed class AssemblyGraphTests
    {
        private static AssemblyGraphNode Node(string id, AssemblyPartKind kind = AssemblyPartKind.Cube,
            int pointCount = 2)
        {
            var node = new AssemblyGraphNode
            {
                PartId = id,
                Kind = kind,
                Position = new Vector3(1f, 2f, 3f),
                Rotation = Quaternion.Euler(10f, 20f, 30f),
            };
            for (int i = 0; i < pointCount; i++)
            {
                node.Points.Add(new AssemblyGraphPointSpec
                {
                    Type = AttachmentPointType.Structural,
                    LocalPosition = new Vector3(0f, 0f, i),
                    LocalForward = Vector3.forward,
                });
            }

            return node;
        }

        private static AssemblyGraphJoint Joint(string id, string from, int fromPoint, string to, int toPoint)
        {
            return new AssemblyGraphJoint
            {
                JointId = id,
                FromPartId = from,
                FromPointIndex = fromPoint,
                ToPartId = to,
                ToPointIndex = toPoint,
            };
        }

        [Test]
        public void AddPart_DuplicateOrEmptyId_IsRejected()
        {
            var graph = new AssemblyGraph();
            Assert.IsTrue(graph.AddPart(Node("a")));
            Assert.IsFalse(graph.AddPart(Node("a")), "重复零件 id 必须拒绝");
            Assert.IsFalse(graph.AddPart(Node("")), "空 id 必须拒绝");
            Assert.IsFalse(graph.AddPart(null), "null 节点必须拒绝");
            Assert.AreEqual(1, graph.PartCount);
        }

        [Test]
        public void AddJoint_RejectsUnknownEndpoints_SelfLoops_OccupiedPoints_AndDuplicatePairs()
        {
            var graph = new AssemblyGraph();
            graph.AddPart(Node("a"));
            graph.AddPart(Node("b", pointCount: 1));
            graph.AddPart(Node("c", pointCount: 1));

            Assert.IsFalse(graph.AddJoint(Joint("j0", "a", 0, "ghost", 0)), "未知端点零件必须拒绝");
            Assert.IsFalse(graph.AddJoint(Joint("j1", "a", 0, "a", 1)), "自环必须拒绝");
            Assert.IsFalse(graph.AddJoint(Joint("j2", "a", 5, "b", 0)), "越界点索引必须拒绝");
            Assert.IsTrue(graph.AddJoint(Joint("j3", "a", 0, "b", 0)), "合法关节应加入");

            Assert.IsFalse(graph.AddJoint(Joint("j4", "a", 0, "c", 0)), "a 的 0 号点已被占用必须拒绝");
            Assert.IsFalse(graph.AddJoint(Joint("j5", "c", 0, "b", 0)), "b 的 0 号点已被占用必须拒绝");
            Assert.IsFalse(graph.AddJoint(Joint("j6", "b", 0, "a", 0)), "同一点对反向重复必须拒绝");
            Assert.AreEqual(1, graph.JointCount, "被拒绝的关节不得入图");
        }

        [Test]
        public void AddJoint_AutoAssignsUniqueJointId_AndDuplicateIdIsRejected()
        {
            var graph = new AssemblyGraph();
            graph.AddPart(Node("a"));
            graph.AddPart(Node("b"));

            var first = Joint("", "a", 0, "b", 0);
            Assert.IsTrue(graph.AddJoint(first));
            Assert.IsFalse(string.IsNullOrEmpty(first.JointId), "空 id 必须被自动赋值");

            var second = Joint("", "a", 1, "b", 1);
            Assert.IsTrue(graph.AddJoint(second));
            Assert.AreNotEqual(first.JointId, second.JointId, "自动 id 不得撞车");

            Assert.IsFalse(graph.AddJoint(Joint(first.JointId, "a", 1, "b", 1)), "显式重复 id 必须拒绝");
            Assert.AreEqual(2, graph.JointCount);
        }

        [Test]
        public void RemovePart_CascadesItsJoints()
        {
            var graph = new AssemblyGraph();
            graph.AddPart(Node("a"));
            graph.AddPart(Node("b"));
            graph.AddPart(Node("c"));
            graph.AddJoint(Joint("j0", "b", 0, "a", 0));
            graph.AddJoint(Joint("j1", "c", 0, "b", 0));

            Assert.IsTrue(graph.RemovePart("b"), "中间件必须可删");
            Assert.AreEqual(2, graph.PartCount, "级联只删自身");
            Assert.AreEqual(0, graph.JointCount, "触碰被删零件的两条关节必须级联删除");
            Assert.IsNull(graph.GetPart("b"));
            Assert.IsFalse(graph.RemovePart("b"), "再删应返回 false");

            // 被级联释放的点可以复用（a:0 现在空闲）。
            Assert.IsTrue(graph.AddJoint(Joint("j2", "c", 0, "a", 0)), "级联后原点必须已释放");
        }

        [Test]
        public void Validate_EmptyAndSinglePart_AreTriviallyValid()
        {
            var empty = new AssemblyGraph();
            AssemblyGraphValidation emptyResult = empty.Validate();
            Assert.IsTrue(emptyResult.IsValid);
            Assert.AreEqual(0, emptyResult.ComponentCount);

            var lone = new AssemblyGraph();
            lone.AddPart(Node("a"));
            AssemblyGraphValidation loneResult = lone.Validate();
            Assert.IsTrue(loneResult.IsValid, "单零件载具是合法起点");
            Assert.AreEqual(1, loneResult.ComponentCount);
            Assert.AreEqual(0, loneResult.OrphanPartIds.Count, "唯一零件不算孤立件");
        }

        [Test]
        public void Validate_Chain_IsConnectedWithoutOrphans()
        {
            var graph = new AssemblyGraph();
            graph.AddPart(Node("a"));
            graph.AddPart(Node("b"));
            graph.AddPart(Node("c"));
            graph.AddJoint(Joint("j0", "b", 0, "a", 0));
            graph.AddJoint(Joint("j1", "c", 0, "b", 1));

            AssemblyGraphValidation result = graph.Validate();
            Assert.IsTrue(result.IsValid, result.ToString());
            Assert.AreEqual(1, result.ComponentCount);
            Assert.AreEqual(0, result.OrphanPartIds.Count);
        }

        [Test]
        public void Validate_TwoIslands_AreReportedDisconnected()
        {
            var graph = new AssemblyGraph();
            graph.AddPart(Node("a"));
            graph.AddPart(Node("b"));
            graph.AddPart(Node("c"));
            graph.AddPart(Node("d"));
            graph.AddJoint(Joint("j0", "b", 0, "a", 0));
            graph.AddJoint(Joint("j1", "d", 0, "c", 0));

            AssemblyGraphValidation result = graph.Validate();
            Assert.IsFalse(result.IsValid, "两个孤岛必须判不连通");
            Assert.AreEqual(2, result.ComponentCount);
            Assert.AreEqual(1, result.Errors.Count);
        }

        [Test]
        public void Validate_OrphanPart_IsListed()
        {
            var graph = new AssemblyGraph();
            graph.AddPart(Node("a"));
            graph.AddPart(Node("b"));
            graph.AddPart(Node("c", pointCount: 2));
            graph.AddJoint(Joint("j0", "b", 0, "a", 0));

            AssemblyGraphValidation result = graph.Validate();
            Assert.IsFalse(result.IsValid, "多零件图中零关节零件是孤立件");
            CollectionAssert.Contains(result.OrphanPartIds, "c");
        }

        [Test]
        public void Validate_SamePartPair_DistinctPoints_IsLegalCycle()
        {
            // 重复连接（同一对点）在 AddJoint 门口就被拒绝（见
            // AddJoint_RejectsUnknownEndpoints...）；经过两对"不同"点把
            // 同一对零件连成环是合法拓扑（每个物理点至多一条键）。
            var graph = new AssemblyGraph();
            graph.AddPart(Node("a"));
            graph.AddPart(Node("b"));
            graph.AddJoint(Joint("j0", "a", 0, "b", 0));
            graph.AddJoint(Joint("j1", "a", 1, "b", 1));

            AssemblyGraphValidation result = graph.Validate();
            Assert.IsTrue(result.IsValid, result.ToString());
            Assert.AreEqual(1, result.ComponentCount);
            Assert.AreEqual(0, result.OrphanPartIds.Count);
        }

        [Test]
        public void Graph_ToSnapshotAndBack_RoundTripsExactly()
        {
            var graph = new AssemblyGraph();
            var beam = new AssemblyGraphNode
            {
                PartId = "beam-1",
                Kind = AssemblyPartKind.StructuralBeam,
                Position = new Vector3(0.1f, -2.5f, 3.75f),
                Rotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f),
            };
            beam.Points.Add(new AssemblyGraphPointSpec
            {
                Type = AttachmentPointType.Structural,
                LocalPosition = new Vector3(0f, 0f, 1f),
                LocalForward = Vector3.forward,
            });
            var tank = new AssemblyGraphNode
            {
                PartId = "tank-1",
                Kind = AssemblyPartKind.FuelTank,
                Position = new Vector3(-7.25f, 0.5f, 0.125f),
                Rotation = Quaternion.identity,
            };
            tank.Points.Add(new AssemblyGraphPointSpec
            {
                Type = AttachmentPointType.FuelPort,
                LocalPosition = new Vector3(0.4f, 0f, 0f),
                LocalForward = Vector3.right,
            });

            graph.AddPart(beam);
            graph.AddPart(tank);
            graph.AddJoint(new AssemblyGraphJoint
            {
                JointId = "j-x",
                FromPartId = "beam-1",
                FromPointIndex = 0,
                ToPartId = "tank-1",
                ToPointIndex = 0,
                Kind = AssemblyJointKind.Rigid,
            });

            AssemblySnapshot snapshot = AssemblySnapshotCodec.ToSnapshot(graph, "round-trip");
            Assert.AreEqual(AssemblySnapshotCodec.CurrentVersion, snapshot.Version);
            Assert.AreEqual("round-trip", snapshot.Name);
            AssemblyGraph restored = AssemblySnapshotCodec.FromSnapshot(snapshot);

            Assert.AreEqual(graph.PartCount, restored.PartCount);
            Assert.AreEqual(graph.JointCount, restored.JointCount);

            foreach (AssemblyGraphNode original in graph.Parts)
            {
                AssemblyGraphNode node = restored.GetPart(original.PartId);
                Assert.IsNotNull(node, $"{original.PartId} 必须在重建图中");
                Assert.AreEqual(original.Kind, node.Kind);
                // float -> double -> float 无损：位级一致。
                Assert.AreEqual(original.Position, node.Position, $"{original.PartId} 位置");
                Assert.AreEqual(original.Rotation, node.Rotation, $"{original.PartId} 朝向");
                Assert.AreEqual(original.Points.Count, node.Points.Count);
                for (int i = 0; i < original.Points.Count; i++)
                {
                    Assert.AreEqual(original.Points[i].Type, node.Points[i].Type, $"{original.PartId} 点 {i} 类型");
                    Assert.AreEqual(original.Points[i].LocalPosition, node.Points[i].LocalPosition,
                        $"{original.PartId} 点 {i} 局部位置");
                    Assert.AreEqual(original.Points[i].LocalForward, node.Points[i].LocalForward,
                        $"{original.PartId} 点 {i} 局部法线");
                }
            }

            foreach (AssemblyGraphJoint original in graph.Joints)
            {
                AssemblyGraphJoint joint = restored.GetJoint(original.JointId);
                Assert.IsNotNull(joint, $"{original.JointId} 必须在重建图中");
                Assert.AreEqual(original.FromPartId, joint.FromPartId);
                Assert.AreEqual(original.FromPointIndex, joint.FromPointIndex);
                Assert.AreEqual(original.ToPartId, joint.ToPartId);
                Assert.AreEqual(original.ToPointIndex, joint.ToPointIndex);
                Assert.AreEqual(original.Kind, joint.Kind);
            }

            AssemblyGraphValidation validation = restored.Validate();
            Assert.IsTrue(validation.IsValid, validation.ToString());
        }

        [Test]
        public void SnapshotJoint_RejectsUnknownPart_WhenReplayedIntoGraph()
        {
            var snapshot = new AssemblySnapshot();
            snapshot.Parts.Add(new AssemblySnapshotPart { PartId = "a" });
            snapshot.Joints.Add(new AssemblySnapshotJoint { JointId = "j0", FromPartId = "a", FromPointIndex = 0, ToPartId = "ghost", ToPointIndex = 0 });

            Assert.Throws<InvalidDataException>(() => AssemblySnapshotCodec.FromSnapshot(snapshot),
                "引用不存在零件的关节必须在 FromSnapshot 处失败");
        }
    }
}
