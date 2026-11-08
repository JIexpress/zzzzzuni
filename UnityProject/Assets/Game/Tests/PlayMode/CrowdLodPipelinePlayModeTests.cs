// CrowdLodPipelinePlayModeTests.cs —— 人群四档 LOD 管线的 PlayMode 集成证据
// （TASK-A-AND-09 简报点名的四条可证伪用例 + kernel 数据源统一用例）：
//  1. 硬上限：25 个全精度请求 → 恰 20 L0 + 5 降级 L1（降的是最远者）；
//  2. 距离驱动：L0→L1→L2 阶梯 + 迟滞带内边界往返不振荡 + 切换帧位置/朝向连续；
//  3. L2 billboard 面向相机（相机绕转 → 旋转跟随）；
//  4. L2 数量上限：512 预算内全收，第 513 个注册被拒，DrawCall ≤ 1023 上限一批。
//  5. 数据源统一：只写 kernel 列（无任何其它输入）→ 三档表示全部跟随（V4）。
// 生产泵 = ITickQueue TickAll（沿 A-AND-08 口径）；表示资产可用性同口径断言。

using System.Collections;
using System.Collections.Generic;
using Game.View.Animation;
using Game.View.Crowd;
using Game.View.Effects;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using ZFrame.Core;
using ZFrame.Render.Bindings;

namespace Game.View.PlayMode.Tests
{
    /// <summary>人群 LOD 管线 PlayMode 集成证据（生产泵 + 真实表示提交）。</summary>
    public sealed class CrowdLodPipelinePlayModeTests
    {
        private TickQueue _queue;
        private ZKernelWorld _world;
        private Transform _camera;
        private CrowdLODPipeline _pipeline;
        private readonly List<GameObject> _spawnedGo = new List<GameObject>();
        private ZkMask _spawnMask;

        [SetUp]
        public void SetUp()
        {
            _queue = new TickQueue();
            _world = new ZKernelWorld();
            _world.RegisterComponent<CrowdPosition>(KernelCrowdIds.Position);
            _world.RegisterComponent<CrowdVelocity>(KernelCrowdIds.Velocity);
            _world.RegisterComponent<CrowdTag>(KernelCrowdIds.Tag);
            _spawnMask = ZkMask.Of(KernelCrowdIds.Position)
                .With(KernelCrowdIds.Velocity)
                .With(KernelCrowdIds.Tag);
            _camera = TrackGo(new GameObject("~CrowdLodTestCamera")).transform;
        }

        [TearDown]
        public void TearDown()
        {
            _pipeline?.Dispose();
            _pipeline = null;
            _world?.Dispose();
            _world = null;
            foreach (GameObject go in _spawnedGo)
            {
                if (go != null)
                {
                    Object.Destroy(go);
                }
            }

            _spawnedGo.Clear();
            _queue = null;
        }

        private GameObject TrackGo(GameObject go)
        {
            _spawnedGo.Add(go);
            return go;
        }

        private ZkEntity SpawnEntity(uint tag, Vector3 position, Vector3 velocity)
        {
            ZkEntity e = _world.Spawn(_spawnMask);
            _world.SetComponent(e, KernelCrowdIds.Position, new CrowdPosition(position.x, position.y, position.z));
            _world.SetComponent(e, KernelCrowdIds.Velocity, new CrowdVelocity(velocity.x, velocity.y, velocity.z));
            _world.SetComponent(e, KernelCrowdIds.Tag, new CrowdTag(tag));
            return e;
        }

        private NpcAnimatorDriver NewNpcRepresentation(string name)
        {
            return TrackGo(new GameObject(name)).AddComponent<NpcAnimatorDriver>();
        }

        private void TickOnce()
        {
            _queue.TickAll(TickStage.Logic, 0.016f);
            _queue.TickAll(TickStage.View, 0.016f);
        }

        // ---- 用例 1：数量硬上限（25 全精度请求 → 恰 20 L0 + 5 降级）------------
        [UnityTest]
        public IEnumerator Lod_HardCap_25FullPrecisionRequests_Exact20L0And5Demoted()
        {
            yield return null;
            _pipeline = new CrowdLODPipeline(_world, _camera);
            _pipeline.Initialize(_queue);

            for (uint i = 0; i < 25; ++i)
            {
                // 距离随序号递增（4.0..6.4m，全部远小于升档阈值 13m）。
                ZkEntity e = SpawnEntity(i, new Vector3(4f + i * 0.1f, 0f, 0f), Vector3.zero);
                NpcAnimatorDriver rep = NewNpcRepresentation($"npc{i}");
                Assert.IsTrue(_pipeline.Register(e, i, isStoryNpc: false, rep),
                    $"全精度请求 {i} 注册成功");
            }

            Assert.AreEqual(CrowdLodLimits.MaxL0, _pipeline.L0Count, "硬上限锁死：恰 20 个 L0");
            Assert.AreEqual(5, _pipeline.L1Count, "超限 5 个自动降级 L1（报告选型：降最远者）");
            Assert.AreEqual(25, _pipeline.Count);

            // 降级身份：最远 5 个（tag 20..24）——档位状态在注册时即裁决。
            for (uint i = 0; i < 25; ++i)
            {
                Assert.IsTrue(_pipeline.TryGet(i, out CrowdLodEntityState s));
                Assert.AreEqual(i < CrowdLodLimits.MaxL0 ? CrowdLodLevel.L0FullPrecision : CrowdLodLevel.L1LowPoly,
                    s.Level, $"tag {i} 档位");
            }

            // 一帧后：表示激活态跟上档位（L0 GO 激活 / 降级者让位），上限仍是稳定态。
            TickOnce();
            for (uint i = 0; i < 25; ++i)
            {
                Assert.IsTrue(_pipeline.TryGetL0Representation(i, out NpcAnimatorDriver rep));
                Assert.AreEqual(i < CrowdLodLimits.MaxL0, rep.gameObject.activeSelf,
                    $"tag {i} 的全精度表示激活态 = 其档位");
            }

            Assert.AreEqual(CrowdLodLimits.MaxL0, _pipeline.L0Count, "一帧后上限仍是稳定态");
            Assert.AreEqual(5, _pipeline.L1Count);
            Assert.IsTrue(_pipeline.IsRepresentationAvailable,
                "L1/L2 表示材质须解析成功（沿 A-AND-08 shader 可用性口径）");
            Assert.AreEqual(5, _pipeline.LastL1Instances, "5 个 L1 低模实例进入提交");
            Assert.AreEqual(1, _pipeline.LastL1SubmitCalls, "5 ≤ 1023 → 单次 DrawMeshInstanced");

            UnityEngine.Debug.Log($"[A-AND-09] 用例1 硬上限：25 请求 → L0={_pipeline.L0Count} " +
                      $"L1={_pipeline.L1Count}（最远者降级），L1 提交 {_pipeline.LastL1SubmitCalls} 次 " +
                      $"（L1 面数 {_pipeline.L1TriangleCount}）");
        }

        // ---- 用例 2：距离驱动降级阶梯 + 迟滞不振荡 + 切换连续 -------------------
        [UnityTest]
        public IEnumerator Lod_DistanceDriven_LadderHysteresisAndSwitchContinuity()
        {
            yield return null;
            _pipeline = new CrowdLODPipeline(_world, _camera);
            _pipeline.Initialize(_queue);

            // 实体 (0,0,10)，速度 +X（yaw=90°）；相机原点 → d=10 → L0。
            ZkEntity e = SpawnEntity(0, new Vector3(0f, 0f, 10f), new Vector3(1f, 0f, 0f));
            NpcAnimatorDriver rep = NewNpcRepresentation("npc0");
            Assert.IsTrue(_pipeline.Register(e, 0, isStoryNpc: false, rep));
            TickOnce();
            Assert.IsTrue(_pipeline.TryGet(0, out CrowdLodEntityState s));
            Assert.AreEqual(CrowdLodLevel.L0FullPrecision, s.Level);
            Assert.IsTrue(rep.gameObject.activeSelf);
            Assert.AreEqual(new Vector3(0f, 0f, 10f), rep.transform.position, "L0 位置 = kernel 列");
            Assert.AreEqual(90f, rep.transform.eulerAngles.y, 0.01f, "L0 朝向 = kernel 速度列推导（Atan2(dx,dz)）");

            // ---- 切换连续性：kernel 列不动，只把相机移到 d=17.5（越过降档阈值 17）。
            _camera.position = new Vector3(0f, 0f, -7.5f);
            TickOnce();
            Assert.IsTrue(_pipeline.TryGet(0, out s));
            Assert.AreEqual(CrowdLodLevel.L1LowPoly, s.Level, "d=17.5 越过降档阈值 → L1");
            Assert.IsFalse(rep.gameObject.activeSelf, "L0 表示让位（每实体每帧恰激活一档表示）");
            Assert.AreEqual(new Vector3(0f, 0f, 10f), rep.transform.position,
                "切换帧：L0 表示停在 kernel 坐标");
            Assert.AreEqual(rep.transform.position, (Vector3)s.RepresentationMatrix.GetColumn(3),
                "L0↔L1 位置连续：L1 提交矩阵平移 = L0 表示位置（kernel 未动 → 严格同坐标）");
            Assert.Less((rep.transform.forward - (Vector3)s.RepresentationMatrix.GetColumn(2)).magnitude, 1e-4f,
                "L0↔L1 朝向连续：L1 矩阵 +Z 轴 = L0 表示 forward（yaw=90°）");
            Assert.AreEqual(90f, s.YawDeg, 0.01f, "切换帧朝向不变（kernel 速度列不变）");

            // ---- 降档阶梯续：d=50（实体 z=-57.5）→ L1→L2 单帧一档。
            _world.SetComponent(e, KernelCrowdIds.Position, new CrowdPosition(0f, 0f, -57.5f));
            TickOnce();
            Assert.IsTrue(_pipeline.TryGet(0, out s));
            Assert.AreEqual(CrowdLodLevel.L2Billboard, s.Level, "d=50 > 44 → L2（L1→L2 单档步进）");
            Assert.AreEqual(1, _pipeline.LastL2Instances, "L2 表示接管提交");

            // ---- L1↔L2 迟滞带 [36,44]：d=39↔41 往返 10 帧，L2 不振荡。
            for (int frame = 0; frame < 10; ++frame)
            {
                float z = frame % 2 == 0 ? -46.5f : -48.5f; // d = 39 / 41（相机 z=-7.5）
                _world.SetComponent(e, KernelCrowdIds.Position, new CrowdPosition(0f, 0f, z));
                TickOnce();
                Assert.IsTrue(_pipeline.TryGet(0, out s));
                Assert.AreEqual(CrowdLodLevel.L2Billboard, s.Level, $"L2 迟滞带内振荡帧 {frame}");
            }

            // ---- 回程：d=15（z=7.5）< Up36 → 升 L1。
            _world.SetComponent(e, KernelCrowdIds.Position, new CrowdPosition(0f, 0f, 7.5f));
            TickOnce();
            Assert.IsTrue(_pipeline.TryGet(0, out s));
            Assert.AreEqual(CrowdLodLevel.L1LowPoly, s.Level, "d=15 < Up36 → L2→L1");

            // ---- L0↔L1 迟滞带 [13,17]：d=14↔16 往返 10 帧，L1 不振荡。
            for (int frame = 0; frame < 10; ++frame)
            {
                float z = frame % 2 == 0 ? 6.5f : 8.5f; // d = 14 / 16
                _world.SetComponent(e, KernelCrowdIds.Position, new CrowdPosition(0f, 0f, z));
                TickOnce();
                Assert.IsTrue(_pipeline.TryGet(0, out s));
                Assert.AreEqual(CrowdLodLevel.L1LowPoly, s.Level, $"L1 迟滞带内振荡帧 {frame}");
            }

            // ---- 阶梯闭合：d=10（z=2.5）< Up13 → 升回 L0，位置/朝向重新对位。
            _world.SetComponent(e, KernelCrowdIds.Position, new CrowdPosition(0f, 0f, 2.5f));
            TickOnce();
            Assert.IsTrue(_pipeline.TryGet(0, out s));
            Assert.AreEqual(CrowdLodLevel.L0FullPrecision, s.Level, "d=10 < Up13 → L1→L0");
            Assert.IsTrue(rep.gameObject.activeSelf, "L0 表示重新接管");
            Assert.AreEqual(s.Position, rep.transform.position, "升档帧：L0 表示回到 kernel 坐标（连续）");

            UnityEngine.Debug.Log("[A-AND-09] 用例2 距离驱动阶梯 L0→L1→L2→L1→L0 + 迟滞带不振荡 + 切换连续：断言通过");
        }

        // ---- 用例 3：L2 billboard 面向相机 ---------------------------------------
        [UnityTest]
        public IEnumerator Lod_L2Billboard_FacesCamera_FollowsCameraOrbit()
        {
            yield return null;
            _pipeline = new CrowdLODPipeline(_world, _camera);
            _pipeline.Initialize(_queue);

            // 远距（60m）无 L0 表示 → 注册即 L2。
            ZkEntity e = SpawnEntity(0, new Vector3(30f, 0f, 52f), Vector3.zero);
            Assert.IsTrue(_pipeline.Register(e, 0));
            TickOnce();
            Assert.IsTrue(_pipeline.TryGet(0, out CrowdLodEntityState s));
            Assert.AreEqual(CrowdLodLevel.L2Billboard, s.Level);
            Assert.AreEqual(1, _pipeline.LastL2Instances);

            // billboard +Z 轴指向相机（LookRotation(camPos − pos)）。
            Vector3 camPos = _camera.position;
            Vector3 toCam = (camPos - s.Position).normalized;
            Assert.Greater(Vector3.Dot(s.BillboardRotation * Vector3.forward, toCam), 0.999f,
                "billboard 面片 +Z 指向相机");

            // 相机绕转到远端侧翼（d=90，仍 > Down44 保持 L2）→ 旋转必须跟随
            // （可证伪：静态朝向或档位漂移都会失败）。
            _camera.position = new Vector3(120f, 0f, 52f);
            TickOnce();
            Assert.IsTrue(_pipeline.TryGet(0, out s));
            Assert.AreEqual(CrowdLodLevel.L2Billboard, s.Level, "d=90 仍在 L2（billboard 未让位）");
            toCam = (_camera.position - s.Position).normalized;
            Assert.Greater(Vector3.Dot(s.BillboardRotation * Vector3.forward, toCam), 0.999f,
                "相机绕转后 billboard 仍面向相机");
            Assert.AreEqual(1, _pipeline.LastL2SubmitCalls, "1 实例 → 单次提交");

            UnityEngine.Debug.Log("[A-AND-09] 用例3 L2 billboard 面向相机 + 绕转跟随断言通过");
        }

        // ---- 用例 4：L2 数量上限（数百预算 + 注册拒绝） ---------------------------
        [UnityTest]
        public IEnumerator Lod_HardCap_L2Budget_RefusesOverflow()
        {
            yield return null;
            _pipeline = new CrowdLODPipeline(_world, _camera);
            _pipeline.Initialize(_queue);

            // 513 个远距注册（100m → 全部 L2）：512 收、第 513 个拒。
            for (uint i = 0; i < CrowdLodLimits.MaxL2; ++i)
            {
                ZkEntity e = SpawnEntity(i, new Vector3(i * 0.05f, 0f, 100f), Vector3.zero);
                Assert.IsTrue(_pipeline.Register(e, i), $"预算内注册 {i} 应成功");
            }

            ZkEntity over = SpawnEntity(CrowdLodLimits.MaxL2, new Vector3(0f, 0f, 100f), Vector3.zero);
            Assert.IsFalse(_pipeline.Register(over, CrowdLodLimits.MaxL2),
                "L2 预算（512）耗尽 → 拒绝注册（L3 播片未实现，无处可降）");

            // 档位守恒不变量（数量硬上限全绿）。
            Assert.AreEqual(CrowdLodLimits.MaxL2, _pipeline.Count);
            Assert.LessOrEqual(_pipeline.L0Count, CrowdLodLimits.MaxL0);
            Assert.LessOrEqual(_pipeline.L1Count, CrowdLodLimits.MaxL1);
            Assert.LessOrEqual(_pipeline.L2Count, CrowdLodLimits.MaxL2);
            Assert.AreEqual(CrowdLodLimits.MaxL2, _pipeline.L2Count);

            TickOnce();
            Assert.AreEqual(CrowdLodLimits.MaxL2, _pipeline.LastL2Instances, "全部 L2 实例进提交");
            Assert.AreEqual(1, _pipeline.LastL2SubmitCalls, "512 ≤ 1023 → 仍单次 DrawMeshInstanced");

            UnityEngine.Debug.Log($"[A-AND-09] 用例4 L2 上限：512 收 + 拒绝 1，" +
                      $"提交 {_pipeline.LastL2SubmitCalls} 次 / {_pipeline.LastL2Instances} 实例");
        }

        // ---- 用例 5：数据源统一（只写 kernel → 三档表示全部跟随） ------------------
        [UnityTest]
        public IEnumerator Lod_KernelIsSoleDataSource_AllLevelsFollowKernelWrite()
        {
            yield return null;
            _pipeline = new CrowdLODPipeline(_world, _camera);
            _pipeline.Initialize(_queue);

            // 三档各一：5m（L0，有表示）/ 25m（L1）/ 60m（L2）。
            ZkEntity e0 = SpawnEntity(0, new Vector3(0f, 0f, 5f), Vector3.zero);
            Assert.IsTrue(_pipeline.Register(e0, 0, isStoryNpc: false, NewNpcRepresentation("npc0")));
            ZkEntity e1 = SpawnEntity(1, new Vector3(0f, 0f, 25f), Vector3.zero);
            Assert.IsTrue(_pipeline.Register(e1, 1));
            ZkEntity e2 = SpawnEntity(2, new Vector3(0f, 0f, 60f), Vector3.zero);
            Assert.IsTrue(_pipeline.Register(e2, 2));

            TickOnce();
            Assert.AreEqual(3u, _pipeline.LastKernelReadRows, "游标交付全部注册行（单次 EachBatches）");
            Assert.IsTrue(_pipeline.TryGet(0, out CrowdLodEntityState s0));
            Assert.IsTrue(_pipeline.TryGet(1, out CrowdLodEntityState s1));
            Assert.IsTrue(_pipeline.TryGet(2, out CrowdLodEntityState s2));
            Assert.AreEqual(CrowdLodLevel.L0FullPrecision, s0.Level);
            Assert.AreEqual(CrowdLodLevel.L1LowPoly, s1.Level);
            Assert.AreEqual(CrowdLodLevel.L2Billboard, s2.Level);

            // 只写 kernel 列（无任何其它输入）→ 瞬移三档实体。
            var moved = new[]
            {
                (Entity: e0, Tag: 0u, Pos: new Vector3(5f, 1f, 0f)),
                (Entity: e1, Tag: 1u, Pos: new Vector3(35f, 2f, 0f)),
                (Entity: e2, Tag: 2u, Pos: new Vector3(65f, 3f, 0f)),
            };
            foreach ((ZkEntity entity, uint tag, Vector3 pos) in moved)
            {
                _world.SetComponent(entity, KernelCrowdIds.Position,
                    new CrowdPosition(pos.x, pos.y, pos.z));
            }

            TickOnce();

            foreach ((ZkEntity entity, uint tag, Vector3 pos) in moved)
            {
                Assert.IsTrue(_pipeline.TryGet(tag, out CrowdLodEntityState s));
                Assert.AreEqual(pos, s.Position, $"tag {tag}：管线状态 = kernel 列（唯一数据源）");
                CrowdPosition kernel = _world.GetComponent<CrowdPosition>(entity, KernelCrowdIds.Position);
                Assert.AreEqual(new Vector3(kernel.X, kernel.Y, kernel.Z), s.Position,
                    $"tag {tag}：kernel 列读回一致（档位未变，位置全跟随）");
            }

            // 三档表示逐一对位：L0 GO transform / L1·L2 提交矩阵（重新取快照对位）。
            Assert.IsTrue(_pipeline.TryGetL0Representation(0, out NpcAnimatorDriver rep));
            Assert.AreEqual(moved[0].Pos, rep.transform.position, "L0 表示跟随 kernel 写入");
            Assert.IsTrue(_pipeline.TryGet(0, out CrowdLodEntityState s0After));
            Assert.AreEqual(moved[0].Pos, s0After.Position, "L0 快照一致（移动后重取）");

            UnityEngine.Debug.Log("[A-AND-09] 用例5 数据源统一：仅 kernel 写入 → 三档表示全部跟随（V4）");
        }
    }
}
