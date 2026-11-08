// AssemblySnapshotCodecTests.cs —— 装配序列化 EditMode 用例 (TASK-A-TECH-05)。
// 覆盖：JSON 文本往返（Newtonsoft）/ 文件保存-加载-删除（测试根目录注入，
// 不碰真实 persistentDataPath）/ 原子写（tmp 先行）/ 名称消毒 /
// 版本门禁 / 非法结构门禁（FromSnapshot 拒绝坏载具）。

using System;
using System.Collections.Generic;
using System.IO;
using Game.View.Assembly;
using NUnit.Framework;
using UnityEngine;

namespace Game.View.Tests
{
    public sealed class AssemblySnapshotCodecTests
    {
        private string _testRoot;
        private string _previousOverride;

        [SetUp]
        public void SetUp()
        {
            _previousOverride = AssemblySnapshotCodec.RootDirectoryOverride;
            _testRoot = Path.Combine(Path.GetTempPath(), "a-tech-05-codec-tests", Guid.NewGuid().ToString("N"));
            AssemblySnapshotCodec.RootDirectoryOverride = _testRoot;
        }

        [TearDown]
        public void TearDown()
        {
            AssemblySnapshotCodec.RootDirectoryOverride = _previousOverride;
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }

        private static AssemblySnapshot MakeSnapshot(string name)
        {
            var snapshot = new AssemblySnapshot { Name = name };
            snapshot.Parts.Add(new AssemblySnapshotPart
            {
                PartId = "core",
                Kind = (int)AssemblyPartKind.Cube,
                PositionX = 1.5,
                PositionY = 0.5,
                PositionZ = -3.25,
                RotationW = 1.0,
            });
            snapshot.Parts[0].Points.Add(new AssemblySnapshotPoint
            {
                Type = (int)AttachmentPointType.Structural,
                LocalPositionZ = 0.5,
                LocalForwardZ = 1.0,
            });
            snapshot.Joints.Add(new AssemblySnapshotJoint
            {
                JointId = "j0",
                FromPartId = "core",
                FromPointIndex = 0,
                ToPartId = "core",
                ToPointIndex = 0,
            });
            return snapshot;
        }

        [Test]
        public void Save_Load_Delete_RoundTripsThroughFile()
        {
            AssemblySnapshot original = MakeSnapshot("probe craft");

            string path = AssemblySnapshotCodec.Save(original);
            Assert.IsTrue(File.Exists(path), "保存后文件必须存在");
            Assert.AreEqual(Path.Combine(_testRoot, "probe_craft.json"), path,
                "名称消毒后落在注入的测试根目录");
            Assert.IsFalse(File.Exists(path + ".tmp"), "原子写完成后 tmp 必须消失");

            string json = File.ReadAllText(path);
            StringAssert.Contains("\"probe_craft\"", json, "文件必须记录消毒后的名称");
            StringAssert.Contains("core", json, "文件必须含零件 id");

            AssemblySnapshot loaded = AssemblySnapshotCodec.Load("probe craft");
            Assert.AreEqual(original.Name, loaded.Name);
            Assert.AreEqual(1, loaded.Parts.Count);
            Assert.AreEqual("core", loaded.Parts[0].PartId);
            Assert.AreEqual(1.5, loaded.Parts[0].PositionX);
            Assert.AreEqual(-3.25, loaded.Parts[0].PositionZ);
            Assert.AreEqual(1, loaded.Parts[0].Points.Count);
            Assert.AreEqual((int)AttachmentPointType.Structural, loaded.Parts[0].Points[0].Type);
            Assert.AreEqual(1, loaded.Joints.Count);
            Assert.AreEqual("j0", loaded.Joints[0].JointId);

            Assert.IsTrue(AssemblySnapshotCodec.Delete("probe craft"), "删除应报告成功");
            Assert.IsFalse(File.Exists(path));
            Assert.IsFalse(AssemblySnapshotCodec.Delete("probe craft"), "重复删除应报告 false");
        }

        [Test]
        public void Save_OverwritesExistingCraft_AndKeepsSingleFile()
        {
            AssemblySnapshot first = MakeSnapshot("v-craft");
            first.Parts[0].PositionX = 1.0;
            AssemblySnapshotCodec.Save(first);

            AssemblySnapshot second = MakeSnapshot("v-craft");
            second.Parts[0].PositionX = 9.0;
            AssemblySnapshotCodec.Save(second);

            string path = AssemblySnapshotCodec.PathFor("v-craft");
            Assert.AreEqual(1, Directory.GetFiles(_testRoot, "*.json").Length, "覆盖不得残留多份文件");
            AssemblySnapshot loaded = AssemblySnapshotCodec.Load("v-craft");
            Assert.AreEqual(9.0, loaded.Parts[0].PositionX, "重存必须覆盖旧内容");
        }

        [Test]
        public void SanitizeName_CollapsesUnsafeCharacters()
        {
            Assert.AreEqual("my_rocket_1", AssemblySnapshotCodec.SanitizeName("my rocket/1"));
            Assert.AreEqual("assembly", AssemblySnapshotCodec.SanitizeName(""));
            Assert.AreEqual("assembly", AssemblySnapshotCodec.SanitizeName("///"));
            Assert.AreEqual("kebab-name", AssemblySnapshotCodec.SanitizeName("kebab-name"));
            Assert.AreEqual("assembly", AssemblySnapshotCodec.SanitizeName(null));

            string longName = new string('a', 100);
            Assert.LessOrEqual(AssemblySnapshotCodec.SanitizeName(longName).Length, 64, "名称必须截断到 64");
        }

        [Test]
        public void FromSnapshot_UnknownFutureVersion_IsRejected()
        {
            string json = "{\"Version\": 999, \"Name\": \"x\", \"Parts\": [], \"Joints\": []}";
            AssemblySnapshot parsed = AssemblySnapshotCodec.FromJson(json);
            Assert.Throws<InvalidDataException>(() => AssemblySnapshotCodec.FromSnapshot(parsed),
                "未来版本文件必须显式失败，不静默重建");
        }

        [Test]
        public void FromSnapshot_DisconnectedVehicle_IsRejected()
        {
            var snapshot = new AssemblySnapshot();
            snapshot.Parts.Add(new AssemblySnapshotPart { PartId = "a", PositionY = 0, RotationW = 1 });
            snapshot.Parts.Add(new AssemblySnapshotPart { PartId = "b", PositionY = 0, RotationW = 1 });
            snapshot.Parts[0].Points.Add(new AssemblySnapshotPoint());
            snapshot.Parts[1].Points.Add(new AssemblySnapshotPoint());

            // 两个零件、零关节：互为孤岛 -> 重建必须拒绝。
            Assert.Throws<InvalidDataException>(() => AssemblySnapshotCodec.FromSnapshot(snapshot),
                "不连通的载具不得进入重建");
        }

        [Test]
        public void ToSnapshot_ThenFromSnapshot_GateMatchesGraphValidate()
        {
            var graph = new AssemblyGraph();
            graph.AddPart(new AssemblyGraphNode { PartId = "a", Position = Vector3.zero, Rotation = Quaternion.identity });
            graph.AddPart(new AssemblyGraphNode { PartId = "b", Position = Vector3.zero, Rotation = Quaternion.identity });
            graph.AddPart(new AssemblyGraphNode { PartId = "c", Position = Vector3.zero, Rotation = Quaternion.identity });
            graph.AddJoint(new AssemblyGraphJoint { FromPartId = "b", FromPointIndex = 0, ToPartId = "a", ToPointIndex = 0 });
            // c 是孤立件。

            AssemblySnapshot snapshot = AssemblySnapshotCodec.ToSnapshot(graph, "broken");
            Assert.Throws<InvalidDataException>(() => AssemblySnapshotCodec.FromSnapshot(snapshot),
                "孤立件载具必须在 FromSnapshot 处被同一套 Validate 门禁拦下");
        }
    }
}
