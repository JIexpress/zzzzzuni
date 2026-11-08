// AssemblySystemPlayModeTests.cs —— 装配系统第二阶段 PlayMode 证据
// (TASK-A-TECH-05)。物理用确定性手动仿真（Physics.simulationMode = Script +
// Physics.Simulate，沿用 A-TECH-02 台架），与帧率无关、可复现。
//
// 可证伪用例（对应任务简报四条）：
//   ① 组装 10 零件 → 保存(persistentDataPath/assemblies) → 清空 → 重建：
//      关节结构逐项一致（零件 id/种类/位姿/连接点 + 关节端点逐项对上）+
//      物理行为等价（同一初始条件自由下落，逐采样点位移在容差内）。
//   ② 燃料消耗 → 刚体质量下降 → 同推力末动量相同但复合体速度变大
//      （ΔP = F·t 恒等，v = P/m：m 变小 → v 变大，物理一致）。
//   ③ 分离器 Detach：关节断裂 + 双向冲量（牛顿第三定律：两侧等量反向），
//      三体分解为三个独立体且按 J/m 获得分离速度。
//   ④ 连接点类型不匹配拒绝吸附：FuelLine 对 ThrusterMount 既不出吸附候选
//      也不允许 Connect；对照组 FuelLine-FuelPort 正常对接。
//   ⑤ 稳定性扩展：梁×4 + 燃料罐×3 + 方块×3 = 10 零件链在地面 10 秒
//      （500 步）不爆不散（锚点误差/速度有界/无 NaN/复合体完整）。
//   ⑥ 图-物理双向同步：Capture（物理→图）与 LiveLink（连接/断开实时镜像）。
//
// 对接方向约定沿用 A-TECH-02：Connect(moving, movingPoint, targetPoint) 把
// moving 点对到 target 点上、外法线相对——链式组装一律用移动件的 +Z 面点
// （Points[0]）对上一件的 -Z 面点（Points[1]）。

using System.Collections;
using System.Collections.Generic;
using Game.View.Assembly;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ZFrame.PlayMode.Tests
{
    public sealed class AssemblySystemPlayModeTests : AssemblyPlayModeTestBase
    {
        private const float Dt = 0.02f;

        /// <summary>
        /// 场景隔离：本用例类的四个用例要独占物理世界（计数的捕获、自由下落
        /// 轨迹、地面静置都不容别的场景残留物掺和）。SCN_Assembly 冒烟用例
        /// 是单模式加载且不复原场景（其先例注释），所以这里同样单模式加载
        /// 空场景把演示场景的地面/零件清出去。
        /// </summary>
        private static IEnumerator LoadEmptyScene()
        {
            AsyncOperation load = SceneManager.LoadSceneAsync("SCN_Empty", LoadSceneMode.Single);
            Assert.IsNotNull(load, "SCN_Empty 必须在 Build Settings 中（AssemblySceneSetup 已固定）");
            yield return load;
            Assert.AreEqual(0, AssemblyPart.AllParts.Count, "空场景不得残留任何装配零件");
        }

        // ---- 链式组装台架 -------------------------------------------------

        /// <summary>零件沿 Z 的半长（对接面到质心的距离；推进器=接口面 0.5×0.9）。</summary>
        private static float HalfLengthZ(AssemblyPartKind kind)
        {
            switch (kind)
            {
                case AssemblyPartKind.Cube: return 0.5f;
                case AssemblyPartKind.StructuralBeam: return AssemblyPartFactory.BeamHalfLength;
                case AssemblyPartKind.FuelTank: return AssemblyPartFactory.TankHalfLength;
                case AssemblyPartKind.Separator: return AssemblyPartFactory.SeparatorHalfThickness;
                case AssemblyPartKind.FuelLine: return AssemblyPartFactory.FuelLineHalfLength;
                default: return 0.45f; // Thruster
            }
        }

        private static float RestHeight(AssemblyPartKind kind)
        {
            switch (kind)
            {
                case AssemblyPartKind.Cube: return 0.5f;
                case AssemblyPartKind.StructuralBeam: return 0.125f;
                case AssemblyPartKind.FuelTank: return 0.6f;
                default: return 0.45f;
            }
        }

        private static AssemblyPart CreateByKind(AssemblyPartKind kind, string id, Vector3 position)
        {
            switch (kind)
            {
                case AssemblyPartKind.Cube: return AssemblyPartFactory.CreateCube(id, position);
                case AssemblyPartKind.StructuralBeam: return AssemblyPartFactory.CreateStructuralBeam(id, position);
                case AssemblyPartKind.FuelTank: return AssemblyPartFactory.CreateFuelTank(id, position);
                case AssemblyPartKind.Separator: return AssemblyPartFactory.CreateSeparator(id, position);
                case AssemblyPartKind.FuelLine: return AssemblyPartFactory.CreateFuelLine(id, position);
                default: return AssemblyPartFactory.CreateThruster(id, position);
            }
        }

        /// <summary>
        /// 沿 -Z 链式组装：零件 i 的 +Z 面点（Points[0]）对接到零件 i-1 的
        /// -Z 面点（Points[1]）。出生位姿即按 DockGap 算好的终态位姿，
        /// Connect 的 Align 是精确空操作，保证确定性。
        /// </summary>
        private static List<AssemblyPart> BuildChain(IReadOnlyList<AssemblyPartKind> kinds, Vector3 headPosition)
        {
            var parts = new List<AssemblyPart>(kinds.Count);
            float z = headPosition.z;
            for (int i = 0; i < kinds.Count; i++)
            {
                if (i > 0)
                {
                    z -= HalfLengthZ(kinds[i - 1]) + AssemblyBuilder.DockGapMeters + HalfLengthZ(kinds[i]);
                }

                Vector3 position = new Vector3(headPosition.x, RestHeight(kinds[i]), z);
                parts.Add(CreateByKind(kinds[i], $"chain-{i}-{kinds[i]}", position));
            }

            for (int i = 1; i < kinds.Count; i++)
            {
                AssemblyJoint joint = AssemblyBuilder.Connect(parts[i], parts[i].Points[0], parts[i - 1].Points[1]);
                Assert.IsNotNull(joint, $"链节 {i}-{i - 1} 必须连接成功");
            }

            return parts;
        }

        private static readonly AssemblyPartKind[] SaveLoadChain =
        {
            AssemblyPartKind.Cube, AssemblyPartKind.StructuralBeam, AssemblyPartKind.FuelTank,
            AssemblyPartKind.StructuralBeam, AssemblyPartKind.Cube, AssemblyPartKind.FuelTank,
            AssemblyPartKind.StructuralBeam, AssemblyPartKind.FuelTank, AssemblyPartKind.StructuralBeam,
            AssemblyPartKind.Thruster,
        };

        private static readonly AssemblyPartKind[] StabilityChain =
        {
            AssemblyPartKind.Cube, AssemblyPartKind.StructuralBeam, AssemblyPartKind.FuelTank,
            AssemblyPartKind.StructuralBeam, AssemblyPartKind.Cube, AssemblyPartKind.FuelTank,
            AssemblyPartKind.StructuralBeam, AssemblyPartKind.FuelTank, AssemblyPartKind.StructuralBeam,
            AssemblyPartKind.Cube,
        };

        private static int IndexOfPoint(AssemblyPart part, AttachmentPoint point)
        {
            IReadOnlyList<AttachmentPoint> points = part.Points;
            for (int i = 0; i < points.Count; i++)
            {
                if (ReferenceEquals(points[i], point))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// 复合体质心速度：Σm·v_CoM / Σm。v_CoM 取
        /// GetPointVelocity(worldCenterOfMass)——刚体原点速度在自旋下会掺入
        /// ω×r 分量，质心点的速度才是动量一致的定义。
        /// </summary>
        private static Vector3 CompositeVelocity(List<AssemblyPart> parts)
        {
            Vector3 momentum = Vector3.zero;
            float mass = 0f;
            foreach (AssemblyPart part in parts)
            {
                momentum += part.Body.mass * part.Body.GetPointVelocity(part.Body.worldCenterOfMass);
                mass += part.Body.mass;
            }

            return momentum / mass;
        }

        private static float CompositeMass(List<AssemblyPart> parts)
        {
            float mass = 0f;
            foreach (AssemblyPart part in parts)
            {
                mass += part.Body.mass;
            }

            return mass;
        }

        // ---- ① 保存 → 清空 → 重建：结构逐项一致 + 物理等价 ----------------

        [UnityTest]
        public IEnumerator SaveClearRebuild_StructureIdentical_AndPhysicsEquivalent()
        {
            yield return LoadEmptyScene();

            const string craftName = "a-tech-05-roundtrip";
            // 从 30 m 高空起步：1.5 s 自由下落（约 11 m）不会触到任何东西，
            // 与场景内容完全无关。
            List<AssemblyPart> original = TrackEach(BuildChain(SaveLoadChain, new Vector3(0f, 30f, 0f)));
            Assert.AreEqual(10, original.Count, "前置：10 零件链");
            Assert.AreEqual(9, CountOwnerJoints(original), "前置：9 条关节");

            // 保存（走真实文件：persistentDataPath/assemblies 注入到临时根）。
            string previousRoot = AssemblySnapshotCodec.RootDirectoryOverride;
            AssemblySnapshotCodec.RootDirectoryOverride =
                Application.temporaryCachePath + "/a-tech-05-playmode";
            try
            {
                AssemblySnapshot snapshot = AssemblySnapshotCodec.ToSnapshot(
                    CaptureGraph(), craftName);
                Assert.AreEqual(10, snapshot.Parts.Count, "前置：捕获应恰好覆盖本链 10 零件");
                Assert.AreEqual(9, snapshot.Joints.Count, "前置：捕获应恰好覆盖 9 条关节");
                string savedPath = AssemblySnapshotCodec.Save(snapshot);
                Assert.IsTrue(System.IO.File.Exists(savedPath), $"装配件必须落盘：{savedPath}");

                // 物理等价的"同一初始条件"：重建后从同一初始位姿自由下落。
                // 原体先跑 1.5 s（75 步），每 25 步采样质心位置。
                var originalTrajectory = new List<Vector3> { CompositeCenter(original) };
                for (int step = 1; step <= 75; step++)
                {
                    Physics.Simulate(Dt);
                    if (step % 25 == 0)
                    {
                        originalTrajectory.Add(CompositeCenter(original));
                    }
                }

                Assert.Less(originalTrajectory[3].y, originalTrajectory[0].y - 10f,
                    "前置：1.5 s 自由下落应显著位移（>10 m）");

                // 清空场景。
                foreach (AssemblyPart part in original)
                {
                    Object.DestroyImmediate(part.gameObject);
                }

                Assert.AreEqual(0, AssemblyPart.AllParts.Count, "清空后场景不得残留零件");

                // 重建（从盘上文件完整读回）。
                AssemblySnapshot loaded = AssemblySnapshotCodec.Load(craftName);
                List<AssemblyPart> rebuilt = TrackEach(AssemblyRebuilder.Rebuild(loaded));
                Assert.AreEqual(10, rebuilt.Count, "重建必须还原 10 个零件");
                Assert.AreEqual(9, CountOwnerJoints(rebuilt), "重建必须还原 9 条关节");

                AssertStructureMatchesSnapshot(rebuilt, loaded);

                // 物理等价：同样的 1.5 s 自由下落，逐采样点对比质心轨迹。
                var rebuiltTrajectory = new List<Vector3> { CompositeCenter(rebuilt) };
                for (int step = 1; step <= 75; step++)
                {
                    Physics.Simulate(Dt);
                    if (step % 25 == 0)
                    {
                        rebuiltTrajectory.Add(CompositeCenter(rebuilt));
                    }
                }

                for (int i = 0; i < originalTrajectory.Count; i++)
                {
                    float drift = Vector3.Distance(originalTrajectory[i], rebuiltTrajectory[i]);
                    Assert.Less(drift, 0.05f,
                        $"采样点 {i} 轨迹偏差 {drift:F4} m 须 < 0.05（同初始条件物理等价）");
                }
            }
            finally
            {
                AssemblySnapshotCodec.Delete(craftName);
                AssemblySnapshotCodec.RootDirectoryOverride = previousRoot;
            }

            yield return null;
        }

        private static Vector3 CompositeCenter(List<AssemblyPart> parts)
        {
            Vector3 moment = Vector3.zero;
            float mass = 0f;
            foreach (AssemblyPart part in parts)
            {
                moment += part.Body.mass * part.Body.worldCenterOfMass;
                mass += part.Body.mass;
            }

            return moment / mass;
        }

        private static int CountOwnerJoints(List<AssemblyPart> parts)
        {
            int count = 0;
            foreach (AssemblyPart part in parts)
            {
                foreach (AssemblyJoint joint in part.Joints)
                {
                    if (joint != null && !joint.IsBroken && joint.OwnerPart == part)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        /// <summary>重建结构与快照逐项对账：零件 id/种类/位姿/连接点/关节端点/参数。</summary>
        private static void AssertStructureMatchesSnapshot(List<AssemblyPart> rebuilt, AssemblySnapshot snapshot)
        {
            Assert.AreEqual(snapshot.Parts.Count, rebuilt.Count, "零件数一致");
            foreach (AssemblySnapshotPart expected in snapshot.Parts)
            {
                AssemblyPart found = rebuilt.Find(p => p.PartId == expected.PartId);
                Assert.IsNotNull(found, $"零件 '{expected.PartId}' 必须被重建");
                Assert.AreEqual((AssemblyPartKind)expected.Kind, found.Kind, $"{expected.PartId} 种类");

                Vector3 expectedPosition = new Vector3(
                    (float)expected.PositionX, (float)expected.PositionY, (float)expected.PositionZ);
                Assert.Less(Vector3.Distance(found.transform.position, expectedPosition), 1e-3f,
                    $"{expected.PartId} 重建位姿须与快照一致");

                Assert.AreEqual(expected.Points.Count, found.Points.Count, $"{expected.PartId} 连接点数");
                for (int i = 0; i < expected.Points.Count; i++)
                {
                    Assert.AreEqual((AttachmentPointType)expected.Points[i].Type, found.Points[i].Type,
                        $"{expected.PartId} 连接点 {i} 类型");
                }

                if (expected.FuelMassKg.HasValue)
                {
                    AssemblyFuelTank tank = found.GetComponent<AssemblyFuelTank>();
                    Assert.IsNotNull(tank, $"{expected.PartId} 必须带燃料罐组件");
                    Assert.AreEqual((float)expected.FuelMassKg.Value, tank.FuelKg, 1e-3f,
                        $"{expected.PartId} 燃料量必须还原");
                }

                if (expected.ThrustNewtons.HasValue)
                {
                    AssemblyThruster thruster = found.GetComponent<AssemblyThruster>();
                    Assert.IsNotNull(thruster, $"{expected.PartId} 必须带推进器组件");
                    Assert.AreEqual((float)expected.ThrustNewtons.Value, thruster.ThrustNewtons, 1e-3f,
                        $"{expected.PartId} 推力必须还原");
                }
            }

            // 关节端点集合逐项对上（无序集合语义：按 (from,fi,to,ti) 键比对）。
            var expectedJoints = new HashSet<string>();
            foreach (AssemblySnapshotJoint joint in snapshot.Joints)
            {
                expectedJoints.Add($"{joint.FromPartId}#{joint.FromPointIndex}->{joint.ToPartId}#{joint.ToPointIndex}");
            }

            var rebuiltJoints = new HashSet<string>();
            foreach (AssemblyPart part in rebuilt)
            {
                foreach (AssemblyJoint joint in part.Joints)
                {
                    if (joint == null || joint.IsBroken || joint.OwnerPart != part)
                    {
                        continue;
                    }

                    rebuiltJoints.Add(
                        $"{part.PartId}#{IndexOfPoint(part, joint.OwnerPoint)}" +
                        $"->{joint.OtherPart.PartId}#{IndexOfPoint(joint.OtherPart, joint.OtherPoint)}");
                }
            }

            CollectionAssert.AreEquivalent(expectedJoints, rebuiltJoints, "关节端点集合必须逐项一致");
        }

        private AssemblyGraph CaptureGraph()
        {
            var graph = new AssemblyGraph();
            AssemblyGraphSync.Capture(graph);
            return graph;
        }

        private List<AssemblyPart> TrackEach(List<AssemblyPart> parts)
        {
            foreach (AssemblyPart part in parts)
            {
                Track(part.gameObject);
            }

            return parts;
        }

        // ---- ② 燃料消耗 → 质量变化 → 同推力加速度变大 ---------------------

        [UnityTest]
        public IEnumerator FuelBurn_ReducesMass_IncreasesCompositeAcceleration()
        {
            const float thrust = 6f;
            const float burnSeconds = 1.5f;

            float velocityFull = BurnRun(new Vector3(0f, 5f, 0f), fuelKg: 3f, thrust, burnSeconds, preDrained: false);
            float velocityLight = BurnRun(new Vector3(0f, 5f, 0f), fuelKg: 3f, thrust, burnSeconds, preDrained: true);

            // 物理一致的判据（可证伪）：链体沿推力轴、力矩为零，dv/dt = F/m(t)
            // 且 m(t) 单调递减 → 末速必然落在 [F·t/m_start, F·t/m_end] 的物理
            // 包络内。满载 m: 4.5→3.75 → v ∈ (2.0, 2.4)；预放空 m: 3.75→3.0
            // → v ∈ (2.4, 3.0)。同推力、更轻的复合体必须获得显著更大的速度
            // 增量（区间不相交，重叠即物理不一致）。
            Assert.Greater(velocityFull, 2.0f, $"满载末速实测 {velocityFull:F3}，须 > F·t/m_start = 2.0");
            Assert.Less(velocityFull, 2.4f, $"满载末速实测 {velocityFull:F3}，须 < F·t/m_end = 2.4");
            Assert.Greater(velocityLight, 2.4f, $"放空后末速实测 {velocityLight:F3}，须 > F·t/m_start = 2.4");
            Assert.Less(velocityLight, 3.0f, $"放空后末速实测 {velocityLight:F3}，须 < F·t/m_end = 3.0");
            Assert.Greater(velocityLight, velocityFull + 0.3f,
                $"同推力下更轻者必须显著更快（实测 满载 {velocityFull:F3} vs 放空 {velocityLight:F3}）");

            yield return null;
        }

        /// <summary>
        /// 一轮燃烧实验：罐+推进器二体复合体，重力关闭，燃烧
        /// <paramref name="burnSeconds"/>。返回复合体末速（沿推力轴）。链体
        /// 共线且推力过质心轴 → 无力矩，复合体平动。preDrained=true 时先把
        /// 燃料预放到"烧完 1.5 s 后的余量"再点火（全程更轻）。
        /// </summary>
        private float BurnRun(Vector3 origin, float fuelKg, float thrust, float burnSeconds, bool preDrained)
        {
            AssemblyPart tankPart = Track(AssemblyPartFactory.CreateFuelTank(
                "t-tank", origin, fuelMassKg: fuelKg, dryMassKg: 1f));
            AssemblyPart thrusterPart = Track(AssemblyPartFactory.CreateThruster("t-thruster", origin + Vector3.forward * 3f));
            tankPart.Body.useGravity = false;
            thrusterPart.Body.useGravity = false;

            AssemblyJoint joint = AssemblyBuilder.Connect(
                thrusterPart, thrusterPart.Points[0], tankPart.Points[1]);
            Assert.IsNotNull(joint, "推进器必须对接到燃料罐");

            AssemblyFuelTank tank = tankPart.GetComponent<AssemblyFuelTank>();
            AssemblyThruster thruster = thrusterPart.GetComponent<AssemblyThruster>();
            thruster.SetThrust(thrust);
            if (preDrained)
            {
                tank.SetFuel(tank.FuelKg - tank.BurnRateKgPerSecond * burnSeconds);
            }

            var parts = new List<AssemblyPart> { tankPart, thrusterPart };
            float massStart = CompositeMass(parts);

            int steps = Mathf.RoundToInt(burnSeconds / Dt);
            tank.StartBurn();
            thruster.Fire(burnSeconds);
            for (int step = 0; step < steps; step++)
            {
                tank.Tick(Dt);
                thruster.Tick(Dt);
                Physics.Simulate(Dt);
            }

            float massEnd = CompositeMass(parts);
            Assert.Less(massEnd, massStart - 0.5f, $"燃烧必须削减刚体质量（实测 {massStart:F3} → {massEnd:F3}）");

            Vector3 velocity = CompositeVelocity(parts);

            // 收尾：立即销毁本轮台架。两轮在同一原位生成，上一轮漂移停泊的
            // 复合体会与本轮燃烧中的复合体相撞并偷走动量（实测第二轮末速被
            // 压到 0.99）。
            Object.DestroyImmediate(tankPart.gameObject);
            Object.DestroyImmediate(thrusterPart.gameObject);

            // 推力轴 = 世界 +Z（对接约定：推进器装在罐体 -Z 侧、+Z 指向罐体）。
            return velocity.z;
        }

        // ---- ③ 分离器：关节断裂 + 等量反向冲量 → 两部分独立运动 -----------

        [UnityTest]
        public IEnumerator Separator_Detach_SeversJoints_AndAppliesEqualOppositeImpulse()
        {
            yield return LoadEmptyScene();

            // 链放远（x=100）：即使有残留场景物也不可能与本链交互。
            var kinds = new List<AssemblyPartKind>
            {
                AssemblyPartKind.Cube, AssemblyPartKind.Separator, AssemblyPartKind.Cube,
            };
            List<AssemblyPart> chain = TrackEach(BuildChain(kinds, new Vector3(100f, 0.5f, 0f)));
            AssemblyPart core = chain[0];
            AssemblyPart wafer = chain[1];
            AssemblyPart payload = chain[2];
            core.Body.useGravity = false;
            wafer.Body.useGravity = false;
            payload.Body.useGravity = false;

            Assert.AreEqual(3, AssemblyBuilder.GetComposite(wafer).Count, "前置：三体一体");

            const float impulse = 1.5f;
            AssemblySeparator separator = wafer.GetComponent<AssemblySeparator>();

            // 分离方向：A 在 +Z 侧、C 在 -Z 侧 → A 获 +Z、C 获 -Z、晶片净受力为 0
            //（两对等量反向冲量在中段对消——牛顿第三定律的可证伪形式）。
            List<AssemblyJoint> severed = separator.Detach(impulse);
            Assert.AreEqual(2, severed.Count, "分离器必须切断宿主的全部关节");
            Assert.IsTrue(severed[0].IsBroken && severed[1].IsBroken);
            Assert.AreEqual(1, AssemblyBuilder.GetComposite(wafer).Count, "分离后晶片独立");
            Assert.AreEqual(1, AssemblyBuilder.GetComposite(core).Count, "分离后核心独立");
            Assert.AreEqual(1, AssemblyBuilder.GetComposite(payload).Count, "分离后载荷独立");

            // 关节销毁跨帧落地（Object.Destroy 帧末生效），随后 AddForce 的
            // 冲量在首个仿真步兑现：Δv = J/m（两侧）与对消（中段）。实测存在
            // ~5% 的系统性求解器残差（见施工报告），故容差取 0.15 并以
            // "两侧等量反向 + 总动量≈0"的结构判据锁死牛顿第三定律。
            yield return null;
            StepPhysics(50);

            Vector3 deltaCore = core.Body.velocity;
            Vector3 deltaWafer = wafer.Body.velocity;
            Vector3 deltaPayload = payload.Body.velocity;
            Assert.AreEqual(impulse / core.Body.mass, deltaCore.z, 0.15f,
                $"核心侧 Δv.z 实测 {deltaCore.z:F3}（解析 +{impulse / core.Body.mass:F3}）");
            Assert.AreEqual(-impulse / payload.Body.mass, deltaPayload.z, 0.15f,
                $"载荷侧 Δv.z 实测 {deltaPayload.z:F3}（解析 -{impulse / payload.Body.mass:F3}）");
            Assert.Less(Mathf.Abs(deltaCore.z + deltaPayload.z), 0.2f,
                "两侧冲量必须等量反向（Δv_core + Δv_payload ≈ 0）");
            Assert.Less(deltaWafer.magnitude, 0.2f, "中段晶片的双向冲量必须对消");
            float totalMomentum = (core.Body.mass * deltaCore + wafer.Body.mass * deltaWafer
                + payload.Body.mass * deltaPayload).magnitude;
            Assert.Less(totalMomentum, 0.25f,
                $"分离是纯内力：总动量必须≈0（实测 {totalMomentum:F3} N·s）");

            float separationNow = core.Body.position.z - payload.Body.position.z;
            Assert.Greater(separationNow, 2.5f,
                $"1 s 后两侧分离距离实测 {separationNow:F3} m，必须显著拉开");
        }

        // ---- ④ 类型不匹配拒绝吸附 -----------------------------------------

        [UnityTest]
        public IEnumerator IncompatibleTypes_RejectedAtSnap_AndAtConnect()
        {
            // 几何前置：推进器接口点进入吸附半径——这样唯一的拒绝理由只能是类型。
            AssemblyPart pipe = Track(AssemblyPartFactory.CreateFuelLine("t-line", new Vector3(0f, 5f, 0f)));
            AssemblyPart thruster = Track(AssemblyPartFactory.CreateThruster("t-thruster", new Vector3(0f, 5f, 0.4f)));
            pipe.Body.useGravity = false;
            thruster.Body.useGravity = false;

            // 反例 A（TryRelease 语义）：候选点是推进器的 ThrusterMount，
            // 而燃油管（拖拽件）没有任何与 ThrusterMount 兼容的自由点 → 拒绝吸附。
            bool candidateFound = AssemblyBuilder.TryFindSnap(
                pipe.Body.position, new AssemblyBuilder.AssemblyPartTypeFilter(pipe, null),
                1f, out AssemblyBuilder.SnapCandidate candidate);
            Assert.IsTrue(candidateFound, "前置：推进器接口点在吸附半径内（几何上可吸附）");
            Assert.AreEqual(AttachmentPointType.ThrusterMount, candidate.Point.Type, "前置：候选是 ThrusterMount");
            Assert.IsNull(pipe.FindNearestFreePoint(candidate.Point.WorldPosition, 1f, candidate.Point.Type),
                "FuelLine 与 ThrusterMount 类型不匹配 → 拖拽件侧无可用连接点，拒绝吸附");

            // 反例 B：直接连接同样被拒绝（不产生关节、不做对齐）。
            AssemblyJoint rejected = AssemblyBuilder.Connect(
                pipe, pipe.Points[0], thruster.Points[0]);
            Assert.IsNull(rejected, "FuelLine 对 ThrusterMount 的 Connect 必须被拒绝");
            Assert.AreEqual(0, pipe.Joints.Count, "被拒绝的连接不得残留关节");

            // 对照组：同类专用对（FuelLine-FuelPort）正常吸附 + 对接。
            // 罐体燃油口在其 +X 面（局部 0.4,0,0）：摆到 (0,5,-0.8) 时离管 0.89 m < 1 m。
            AssemblyPart tank = Track(AssemblyPartFactory.CreateFuelTank("t-tank", new Vector3(0f, 5f, -0.8f)));
            tank.Body.useGravity = false;
            AttachmentPoint fuelPort = tank.Points[2];
            Assert.AreEqual(AttachmentPointType.FuelPort, fuelPort.Type);

            bool snapToTank = AssemblyBuilder.TryFindSnap(
                pipe.Body.position, new AssemblyBuilder.AssemblyPartTypeFilter(pipe, AttachmentPointType.FuelPort),
                1f, out AssemblyBuilder.SnapCandidate portCandidate);
            Assert.IsTrue(snapToTank, "燃油口必须在吸附半径内被找到（ThrusterMount 被 wanted 过滤掉）");
            Assert.AreEqual(tank, portCandidate.Part, "候选必须来自燃料罐");

            // TryRelease 语义正例：拖拽件（燃油管）能找到与 FuelPort 兼容的端点。
            AttachmentPoint mine = pipe.FindNearestFreePoint(fuelPort.WorldPosition, 1f, AttachmentPointType.FuelPort);
            Assert.IsNotNull(mine, "FuelLine 与 FuelPort 兼容 → 拖拽件侧有可用连接点");

            AssemblyJoint connected = AssemblyBuilder.Connect(pipe, mine, fuelPort);
            Assert.IsNotNull(connected, "FuelLine 对 FuelPort 必须允许对接");
            Assert.AreEqual(2, AssemblyBuilder.GetComposite(pipe).Count, "对接后成一体");

            yield return null;
        }

        // ---- ⑤ 稳定性扩展：10 零件复合体地面 10 秒 ------------------------

        [UnityTest]
        public IEnumerator TenPartComposite_GroundSettled_10Seconds_NoExplosion()
        {
            yield return LoadEmptyScene();

            GameObject ground = Track(GameObject.CreatePrimitive(PrimitiveType.Plane));
            ground.name = "t-ground";
            ground.transform.position = Vector3.zero;
            ground.transform.localScale = new Vector3(4f, 1f, 4f); // 40×40 m，覆盖 14.7 m 链

            List<AssemblyPart> chain = TrackEach(BuildChain(StabilityChain, new Vector3(0f, 0.5f, 0f)));
            Assert.AreEqual(10, chain.Count, "前置：10 零件（梁×4+罐×3+块×3）");

            StepPhysics(100); // 2 s 静置（落地 + 关节稳定）

            var anchors = new List<(AttachmentPoint mine, AttachmentPoint theirs)>();
            foreach (AssemblyPart part in chain)
            {
                foreach (AssemblyJoint joint in part.Joints)
                {
                    if (joint != null && !joint.IsBroken && joint.OwnerPart == part)
                    {
                        anchors.Add((joint.OwnerPoint, joint.OtherPoint));
                    }
                }
            }

            Assert.AreEqual(9, anchors.Count, "前置：9 条关节锚点");

            Vector3 headStart = chain[0].Body.position;
            float maxAnchorError = 0f;
            float maxSpeed = 0f;

            for (int step = 0; step < 500; step++) // 500 × 0.02 s = 10 s
            {
                Physics.Simulate(Dt);

                foreach (var anchor in anchors)
                {
                    float error = Vector3.Distance(anchor.mine.WorldPosition, anchor.theirs.WorldPosition);
                    maxAnchorError = Mathf.Max(maxAnchorError, error);
                }

                float speed = 0f;
                foreach (AssemblyPart part in chain)
                {
                    speed += part.Body.velocity.magnitude;
                    Assert.IsFalse(float.IsNaN(part.Body.position.x), $"第 {step} 步出现 NaN（爆炸）");
                }

                maxSpeed = Mathf.Max(maxSpeed, speed);
            }

            Assert.Less(maxAnchorError, 0.05f,
                $"10 秒内关节锚点最大误差 {maxAnchorError:F4} m，须 < 0.05（不抖动）");
            Assert.Less(maxSpeed, 3f, "速度必须有界（不爆炸）");
            Assert.Less((chain[0].Body.position - headStart).magnitude, 1.5f, "链首不得漂离初始位置超过 1.5 m");
            Assert.AreEqual(10, AssemblyBuilder.GetComposite(chain[0]).Count, "10 秒后 10 零件仍是一个复合体");
            foreach (AssemblyPart part in chain)
            {
                Assert.Greater(part.Body.position.y, -1f, "任何零件都不得穿地");
            }

            yield return null;
        }

        // ---- ⑥ 图-物理双向同步 ---------------------------------------------

        [UnityTest]
        public IEnumerator GraphSync_CaptureAndLiveLink_MirrorPhysicalBonds()
        {
            yield return LoadEmptyScene();

            AssemblyPart a = Track(AssemblyPartFactory.CreateCube("t-sync-a", new Vector3(0f, 5f, 0f)));
            AssemblyPart b = Track(AssemblyPartFactory.CreateCube("t-sync-b", new Vector3(0f, 5f, 3f)));
            a.Body.useGravity = false;
            b.Body.useGravity = false;

            // 物理 → 图：全量捕获（此时无关节 → 2 零件 0 边，孤立件不阻拦捕获）。
            var graph = new AssemblyGraph();
            AssemblyGraphSync.Capture(graph);
            Assert.AreEqual(2, graph.PartCount, "捕获必须覆盖全部场景零件");
            Assert.AreEqual(0, graph.JointCount);
            Assert.IsNotNull(graph.GetPart("t-sync-a"), "零件 id 必须保留");
            Assert.Less(Vector3.Distance(graph.GetPart("t-sync-a").Position, a.transform.position), 1e-5f,
                "捕获位姿必须与物理一致");

            // 图 ← 物理活动镜像：连接事件实时入图。
            using (AssemblyGraphSync.Bind(graph))
            {
                AssemblyJoint joint = AssemblyBuilder.Connect(a, a.Points[0], b.Points[1]);
                Assert.IsNotNull(joint);
                Assert.AreEqual(1, graph.JointCount, "Connect 必须实时镜像为图边");

                AssemblyGraphJoint edge = null;
                foreach (AssemblyGraphJoint candidate in graph.Joints)
                {
                    edge = candidate;
                }

                Assert.IsNotNull(edge);
                Assert.AreEqual("t-sync-a", edge.FromPartId, "移动侧（owner）= from");
                Assert.AreEqual(0, edge.FromPointIndex, "连接点索引必须对上");
                Assert.AreEqual("t-sync-b", edge.ToPartId);
                Assert.AreEqual(1, edge.ToPointIndex);

                AssemblyBuilder.Disconnect(joint);
                Assert.AreEqual(0, graph.JointCount, "断开必须实时删除图边");
                Assert.AreEqual(2, graph.PartCount, "断开不删零件节点（图是权威，Capture 全量刷新）");
            }

            yield return null;
        }
    }
}
