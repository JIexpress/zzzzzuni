// BuildModePlayModeTests.cs —— TASK-A-ASM-03：建造模式 + 蓝图存档的场景级证据。
// 口径：默认关闭、待机零可见物；B 开关幂等（不重复建幽灵）；Q/E 在 5 种零件间循环，
// 且幽灵只在切换时重建；V3 放置/吸附/连接；V4 删除（先 Disconnect 再销毁）；
// V5 蓝图 Capture → Save → Load → Rebuild 往返。
// 蓝图写进临时目录（AssemblySnapshotCodec.RootDirectoryOverride），不污染本机
// persistentDataPath；用例结束销毁 ~BuildMode 宿主与所有零件。

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Game.View.Assembly;
using Game.View.Build;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.View.PlayMode.Tests
{
    public sealed class BuildModePlayModeTests
    {
        private string _blueprintRoot;

        [SetUp]
        public void SetUp()
        {
            _blueprintRoot = Path.Combine(
                Application.temporaryCachePath, "buildmode-tests-" + Guid.NewGuid().ToString("N"));
            AssemblySnapshotCodec.RootDirectoryOverride = _blueprintRoot;
        }

        [TearDown]
        public void TearDown()
        {
            AssemblySnapshotCodec.RootDirectoryOverride = null;

            BuildMode build = BuildMode.Instance;
            if (build != null)
            {
                UnityEngine.Object.DestroyImmediate(build.gameObject);
            }

            DestroyAllParts();

            if (Directory.Exists(_blueprintRoot))
            {
                try
                {
                    Directory.Delete(_blueprintRoot, true);
                }
                catch (IOException)
                {
                    // 临时目录清理失败不影响用例结论。
                }
            }
        }

        // ---- V1：开关幂等 + 待机零可见物 --------------------------------

        [UnityTest]
        public IEnumerator V1_ToggleIsIdempotent_AndIdleBuildsNothing()
        {
            yield return LoadGameScene();

            BuildMode build = BuildMode.Instance;
            Assert.IsNotNull(build, "SCN_Game 必须自动挂载 ~BuildMode 宿主");
            Assert.IsFalse(build.IsBuilding, "默认必须关闭建造模式");
            Assert.IsTrue(build.Ghost == null, "待机不得建幽灵（像素用例零影响）");
            Assert.AreEqual(0, build.GhostBuildCount);

            int partsBefore = AssemblyPart.AllParts.Count;

            build.SetBuilding(true);
            Assert.IsTrue(build.IsBuilding, "B 后必须进入建造模式");
            AssemblyPart ghost = build.Ghost;
            Assert.IsNotNull(ghost, "进入建造模式必须有幽灵预览");
            Assert.AreEqual(1, build.GhostBuildCount);
            Assert.AreEqual(partsBefore, AssemblyPart.AllParts.Count, "幽灵不得注册成场上零件");
            Assert.AreEqual(BuildMode.PartKinds[0], ghost.Kind);

            build.SetBuilding(true); // 重复进入：状态不变 → 不重建
            Assert.AreSame(ghost, build.Ghost, "重复进入不得重建幽灵");
            Assert.AreEqual(1, build.GhostBuildCount);

            build.SetBuilding(false);
            Assert.IsFalse(build.IsBuilding, "再按 B 必须退出");
            Assert.IsTrue(build.Ghost == null, "退出必须销毁幽灵");
            Assert.AreEqual(partsBefore, AssemblyPart.AllParts.Count, "退出建造模式不得残留零件");

            yield return null;
        }

        // ---- V2：Q/E 五件循环，幽灵只重建一次 ---------------------------

        [UnityTest]
        public IEnumerator V2_CyclePartKinds_RebuildsGhostOncePerSwitch()
        {
            yield return LoadGameScene();

            BuildMode build = BuildMode.Instance;
            build.SetBuilding(true);

            AssemblyPartKind[] order = BuildMode.PartKinds;
            Assert.AreEqual(5, order.Length, "本刀要求 5 种零件");
            Assert.AreEqual(order[0], build.CurrentKind);
            Assert.AreEqual(order[0], build.Ghost.Kind);

            for (int i = 1; i <= order.Length; i++)
            {
                int expected = i % order.Length;
                build.CycleKind(1);
                Assert.AreEqual(order[expected], build.CurrentKind, $"第 {i} 次 E 的当前零件不对");
                Assert.AreEqual(order[expected], build.Ghost.Kind, "幽灵种类必须跟随切换");
            }

            Assert.AreEqual(1 + order.Length, build.GhostBuildCount, "每次切换只重建一次幽灵");

            int buildsBefore = build.GhostBuildCount;
            yield return null;
            yield return null;
            Assert.AreEqual(buildsBefore, build.GhostBuildCount, "空闲帧不得重建幽灵（每帧 new 是禁止的）");

            build.CycleKind(-1); // 0 → 4
            Assert.AreEqual(order[order.Length - 1], build.CurrentKind, "Q 必须反向循环");

            build.SetBuilding(false);
            yield return null;
        }

        // ---- V3：放置 / 吸附 / 连接 -------------------------------------

        [UnityTest]
        public IEnumerator V3_PlaceTwoCubes_ThenSnapConnectsIntoComposite()
        {
            yield return LoadGameScene();

            BuildMode build = BuildMode.Instance;
            build.SetBuilding(true);
            build.ClearAllParts();

            // 在 (1,0,0) 与 (3,0,0) 各放一个 Cube：间距 2.0m > 1.2m 吸附半径 →
            // 两件都必须自由落位（不误吸附），id 递增且稳定。
            AssemblyPart a = build.PlaceAt(new Vector3(1f, 0f, 0f));
            AssemblyPart b = build.PlaceAt(new Vector3(3f, 0f, 0f));
            Assert.IsNotNull(a);
            Assert.IsNotNull(b);
            Assert.AreEqual("build-0001", a.PartId);
            Assert.AreEqual("build-0002", b.PartId);
            Assert.AreEqual(2, AssemblyPart.AllParts.Count);
            Assert.AreEqual(0, CountOwnedJoints(), "超出 1.2m 不得吸附");
            Assert.AreEqual(1, AssemblyBuilder.GetComposite(a).Count);
            Assert.AreEqual(1, AssemblyBuilder.GetComposite(b).Count);

            // 第三件落在 B 的 +Z 面外侧 0.55m：落点距挂点 ≤1.2m → 吸附并 Connect，
            // GetComposite 必须返回 2 件。
            AssemblyPart c = build.PlaceAt(new Vector3(3f, 0f, 1.05f));
            Assert.IsNotNull(c);
            Assert.AreEqual(3, AssemblyPart.AllParts.Count);
            Assert.AreEqual(2, AssemblyBuilder.GetComposite(b).Count, "吸附后复合体必须是 2 件");
            Assert.IsTrue(AssemblyBuilder.GetComposite(b).Contains(c));
            Assert.AreEqual(1, CountOwnedJoints(), "吸附必须产出 1 条关节");

            float seamGap = Vector3.Distance(b.Points[0].WorldPosition, c.Points[1].WorldPosition);
            Assert.Less(seamGap, AssemblyBuilder.DockGapMeters + 0.005f,
                $"吸附对齐后挂点间隙必须 = 对接缝 {AssemblyBuilder.DockGapMeters} m（实测 {seamGap:F4}）");
            Assert.Less(Vector3.Dot(b.Points[0].WorldForward, c.Points[1].WorldForward), -0.999f,
                "吸附后两外法线必须相对（面贴合）");

            build.SetBuilding(false);
            yield return null;
        }

        // ---- V4：删除（先 Disconnect 再销毁） ---------------------------

        [UnityTest]
        public IEnumerator V4_DeleteDisconnectsFirst_ThenRemovesFromScene()
        {
            yield return LoadGameScene();

            BuildMode build = BuildMode.Instance;
            build.SetBuilding(true);
            build.ClearAllParts();

            AssemblyPart a = build.PlaceAt(new Vector3(1f, 0f, 0f));
            AssemblyPart b = build.PlaceAt(new Vector3(1f, 0f, 1.05f));
            Assert.AreEqual(2, AssemblyPart.AllParts.Count);
            Assert.AreEqual(1, b.Joints.Count, "后放置的 B 必须是关节 owner 侧");

            // 删 owner 侧 B：必须先把关节拆掉，另一侧 A 当帧就松手（不依赖帧末 OnDestroy）。
            Assert.IsTrue(build.DeletePart(b), "删除必须成功");
            Assert.AreEqual(1, AssemblyPart.AllParts.Count, "场上必须只剩 1 件");
            Assert.IsFalse(ContainsPart(b), "被删零件必须立刻离开零件注册表");
            Assert.AreEqual(0, a.Joints.Count, "先 Disconnect：另一侧当帧必须松手");
            Assert.AreEqual(1, AssemblyBuilder.GetComposite(a).Count, "删除后复合体只剩自身");
            Assert.IsTrue(a.Points[0].Occupant == null, "原对接点必须回到空闲");
            Assert.IsTrue(a.Points[1].Occupant == null);

            // 幽灵不可被删（它不是场上零件）。
            Assert.IsFalse(build.DeletePart(build.Ghost), "幽灵不得被当成零件删除");

            yield return null;
            Assert.IsTrue(b == null, "被删零件必须在帧末销毁");

            build.SetBuilding(false);
            yield return null;
        }

        // ---- V5：蓝图保存 → 清空 → 载入往返 -----------------------------

        [UnityTest]
        public IEnumerator V5_BlueprintRoundTrip_KeepsTwoPartsAndOneJoint()
        {
            yield return LoadGameScene();

            BuildMode build = BuildMode.Instance;
            build.SetBuilding(true);
            build.ClearAllParts();

            AssemblyPart a = build.PlaceAt(new Vector3(1f, 0f, 0f));
            AssemblyPart b = build.PlaceAt(new Vector3(1f, 0f, 1.05f));
            Assert.AreEqual(2, AssemblyPart.AllParts.Count);
            Assert.AreEqual(1, CountOwnedJoints());
            Assert.AreEqual(2, AssemblyBuilder.GetComposite(a).Count);

            // K：Capture → ToSnapshot → Save（全走既有装配存档链路）。
            Assert.IsTrue(build.SaveBlueprint(), build.Message);
            string path = AssemblySnapshotCodec.PathFor(BuildMode.DefaultBlueprintName);
            Assert.IsTrue(File.Exists(path), "K 必须落盘到 slot1.json，实测路径：" + path);

            AssemblySnapshot saved = AssemblySnapshotCodec.Load(BuildMode.DefaultBlueprintName);
            Assert.AreEqual(2, saved.Parts.Count, "存档必须含 2 件");
            Assert.AreEqual(1, saved.Joints.Count, "存档必须含 1 条关节");

            // 清空 → L：Rebuild 回放。
            build.ClearAllParts();
            Assert.AreEqual(0, AssemblyPart.AllParts.Count, "载入前必须清空现有零件");

            Assert.IsTrue(build.LoadBlueprint(), build.Message);
            Assert.AreEqual(2, AssemblyPart.AllParts.Count, "载入后必须恢复 2 件");
            Assert.AreEqual(1, CountOwnedJoints(), "载入后必须恢复 1 条关节");
            Assert.AreEqual(2, AssemblyBuilder.GetComposite(AssemblyPart.AllParts[0]).Count,
                "恢复的复合体必须连通");
            CollectionAssert.AreEquivalent(new[] { "build-0001", "build-0002" }, PartIds(),
                "零件 id 必须原样回来（保存时稳定）");

            // 读档后继续放件：序号必须接在已载入 id 之后，不撞车。
            AssemblyPart next = build.PlaceAt(new Vector3(10f, 0f, 0f));
            Assert.IsNotNull(next);
            Assert.AreEqual("build-0003", next.PartId, "读档后递增序号必须续上");

            build.SetBuilding(false);
            yield return null;
        }

        // ---------------------------------------------------------------- 工具 ----

        private static IEnumerator LoadGameScene()
        {
            yield return SceneManager.LoadSceneAsync("SCN_Game");
            yield return null;
            yield return null;
        }

        private static void DestroyAllParts()
        {
            IReadOnlyList<AssemblyPart> parts = AssemblyPart.AllParts;
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                AssemblyPart part = parts[i];
                if (part != null)
                {
                    UnityEngine.Object.DestroyImmediate(part.gameObject);
                }
            }
        }

        private static bool ContainsPart(AssemblyPart part)
        {
            IReadOnlyList<AssemblyPart> parts = AssemblyPart.AllParts;
            for (int i = 0; i < parts.Count; i++)
            {
                if (ReferenceEquals(parts[i], part))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>关节的 owner 侧计数（同一条关节在两端各挂一次，只数 owner 侧）。</summary>
        private static int CountOwnedJoints()
        {
            int count = 0;
            IReadOnlyList<AssemblyPart> parts = AssemblyPart.AllParts;
            for (int i = 0; i < parts.Count; i++)
            {
                AssemblyPart part = parts[i];
                if (part == null)
                {
                    continue;
                }

                IReadOnlyList<AssemblyJoint> joints = part.Joints;
                for (int j = 0; j < joints.Count; j++)
                {
                    AssemblyJoint joint = joints[j];
                    if (joint != null && !joint.IsBroken && joint.OwnerPart == part)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static List<string> PartIds()
        {
            var ids = new List<string>();
            IReadOnlyList<AssemblyPart> parts = AssemblyPart.AllParts;
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i] != null)
                {
                    ids.Add(parts[i].PartId);
                }
            }

            return ids;
        }
    }
}
