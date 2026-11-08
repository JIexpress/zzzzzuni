// CrowdLodPipelineTests.cs —— 人群四档 LOD 管线的 EditMode 证据（TASK-A-AND-09）。
// 真 dll + 真 ZKernelWorld（沿 KernelCrowdDriverTests 口径，不 mock）：硬上限常量、
// 程序化资产确定性、剧情 NPC 永久 L0、最远者自动降级、迟滞带不振荡、总注册
// 预算拒绝、kernel 销毁冻结。PlayMode（Game/Tests/PlayMode）另补场景内四条
// 可证伪集成用例（真 GameObject / TickQueue 泵 / DrawMeshInstanced 计数）。

using System;
using System.Collections.Generic;
using Game.View.Animation;
using Game.View.Crowd;
using Game.View.Effects;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Core;
using ZFrame.Render.Bindings;

namespace Game.View.Tests
{
    /// <summary>人群 LOD 管线：硬上限 / 迟滞 / 数据源 / 资产的 EditMode 断言。</summary>
    [TestFixture]
    public sealed class CrowdLodPipelineTests
    {
        private ZKernelWorld _world;
        private Transform _camera;
        private ZkMask _spawnMask;
        private readonly List<GameObject> _spawnedGo = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
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
            if (_world != null && !_world.IsDisposed)
            {
                _world.Dispose(); // 原生句柄表仅 8 槽，测试失败路径也必须释放
            }

            _world = null;
            foreach (GameObject go in _spawnedGo)
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }

            _spawnedGo.Clear();
        }

        private GameObject TrackGo(GameObject go)
        {
            _spawnedGo.Add(go);
            return go;
        }

        /// <summary>在 kernel 生成一个群体实体并写初始列（返回句柄供注册）。</summary>
        private ZkEntity SpawnEntity(uint tag, Vector3 position, Vector3 velocity)
        {
            ZkEntity e = _world.Spawn(_spawnMask);
            _world.SetComponent(e, KernelCrowdIds.Position, new CrowdPosition(position.x, position.y, position.z));
            _world.SetComponent(e, KernelCrowdIds.Velocity, new CrowdVelocity(velocity.x, velocity.y, velocity.z));
            _world.SetComponent(e, KernelCrowdIds.Tag, new CrowdTag(tag));
            return e;
        }

        private CrowdLODPipeline NewPipeline(CrowdLodConfig config = null)
        {
            var pipeline = new CrowdLODPipeline(_world, _camera, config);
            pipeline.Initialize(new TickQueue());
            return pipeline;
        }

        /// <summary>全精度表示（A-ANIM-01 驱动组件挂在裸 GO 上，管线只消费 transform/激活）。</summary>
        private NpcAnimatorDriver NewNpcRepresentation(string name)
        {
            return TrackGo(new GameObject(name)).AddComponent<NpcAnimatorDriver>();
        }

        // ---- 硬上限常量（用户绑定约束锁死） -------------------------------------

        [Test]
        public void Limits_HardCaps_MatchUserBoundTable()
        {
            Assert.AreEqual(20, CrowdLodLimits.MaxL0, "用户约束：L0 全精度 ≤ 20");
            Assert.AreEqual(50, CrowdLodLimits.MaxL1, "用户约束：L1 低模 ≤ 50");
            Assert.AreEqual(512, CrowdLodLimits.MaxL2, "L2 billboard 预算：数百量级上限");
        }

        // ---- 程序化资产（L1 几百面 / L2 billboard） -----------------------------

        [Test]
        public void LowPolyHumanoid_IsHundredsOfFaces_AndStandsUpright()
        {
            Assert.AreEqual(300, CrowdLodLowPolyHumanoid.TriangleCount,
                "低模人形确定性面数：3 个 48 面部件 + 13 个 12 面部件 = 300（数百面量级）");

            Mesh mesh = CrowdLodLowPolyHumanoid.BuildMesh();
            try
            {
                Assert.AreEqual(900, mesh.vertexCount, "300 三角 × 3 顶点（非索引铺顶）");
                Assert.AreEqual(300, mesh.triangles.Length / 3);
                Bounds b = mesh.bounds;
                Assert.AreEqual(CrowdLodLowPolyHumanoid.Height, b.size.y, 1e-3f, "人形站立总高");
                Assert.LessOrEqual(b.size.x, 1f, "人体宽度合理（远小于高度）");
                for (int i = 0; i < mesh.normals.Length; ++i)
                {
                    Assert.AreEqual(1f, mesh.normals[i].magnitude, 1e-4f, "法线须为单位向量");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void BillboardAssets_ProceduralQuadAndSilhouetteTexture()
        {
            Mesh quad = CrowdLodBillboardSprite.BuildQuad();
            try
            {
                Assert.AreEqual(2, quad.triangles.Length / 3, "billboard = 单四边形（2 面）");
                Assert.AreEqual(4, quad.vertexCount);
                Assert.AreEqual(CrowdLodBillboardSprite.WorldSize, quad.bounds.size.y, 1e-3f);
                Assert.AreEqual(0f, quad.bounds.center.z, 1e-5f, "四边形在 XY 平面（+Z 为可见面法线）");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(quad);
            }

            Texture2D tex = CrowdLodBillboardSprite.BuildSilhouetteTexture();
            try
            {
                Assert.AreEqual(CrowdLodBillboardSprite.TextureSize, tex.width);
                Assert.AreEqual(CrowdLodBillboardSprite.TextureSize, tex.height);
                Color32[] pixels = tex.GetPixels32();
                int opaque = 0;
                int transparent = 0;
                foreach (Color32 p in pixels)
                {
                    if (p.a == 255)
                    {
                        ++opaque;
                    }
                    else
                    {
                        ++transparent;
                    }
                }

                Assert.Greater(opaque, 0, "剪影主体存在（程序化人群纹理，非纯空贴图）");
                Assert.Greater(transparent, 0, "轮廓外透明（billboard 剪影而非色块）");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        // ---- 注册：剧情 NPC 永远 L0 + 硬上限 ------------------------------------

        [Test]
        public void Register_StoryNpcAlwaysL0_BeyondCapRefused()
        {
            using (CrowdLODPipeline pipeline = NewPipeline())
            {
                // 20 个远距离剧情 NPC：距离 100m 仍必须 L0（重要性标记压过距离）。
                for (uint i = 0; i < CrowdLodLimits.MaxL0; ++i)
                {
                    ZkEntity e = SpawnEntity(i, new Vector3(i * 0.1f, 0f, 100f), Vector3.zero);
                    Assert.IsTrue(pipeline.Register(e, i, isStoryNpc: true, NewNpcRepresentation($"story{i}")),
                        $"剧情 NPC {i} 注册成功");
                    Assert.IsTrue(pipeline.TryGet(i, out CrowdLodEntityState s));
                    Assert.AreEqual(CrowdLodLevel.L0FullPrecision, s.Level, "剧情 NPC 永远 L0");
                }

                Assert.AreEqual(CrowdLodLimits.MaxL0, pipeline.L0Count);

                // 第 21 个剧情 NPC：剧情 L0 槽已满且剧情永不降级 → 拒绝注册。
                ZkEntity extra = SpawnEntity(999, new Vector3(0f, 0f, 5f), Vector3.zero);
                Assert.IsFalse(pipeline.Register(extra, 999, isStoryNpc: true, NewNpcRepresentation("storyExtra")),
                    "剧情 L0 槽满（20）→ 拒绝注册而非降级剧情 NPC");

                // 非剧情 NPC 仍可注册（占 L1/L2，不侵占剧情 L0 槽）。
                ZkEntity normal = SpawnEntity(998, new Vector3(0f, 0f, 5f), Vector3.zero);
                Assert.IsTrue(pipeline.Register(normal, 998), "非剧情注册不受剧情 L0 槽影响");
                Assert.IsTrue(pipeline.TryGet(998, out CrowdLodEntityState normalState));
                Assert.AreEqual(CrowdLodLevel.L1LowPoly, normalState.Level, "非剧情近距但无 L0 表示 → L1");
            }
        }

        [Test]
        public void Register_25FullPrecisionRequests_Exactly20L0And5DemotedToL1()
        {
            using (CrowdLODPipeline pipeline = NewPipeline())
            {
                // 25 个近距全精度请求（距离随序号递增 4.0..6.4m，最远者先被降级）。
                for (uint i = 0; i < 25; ++i)
                {
                    ZkEntity e = SpawnEntity(i, new Vector3(4f + i * 0.1f, 0f, 0f), Vector3.zero);
                    Assert.IsTrue(pipeline.Register(e, i, isStoryNpc: false, NewNpcRepresentation($"npc{i}")));
                }

                Assert.AreEqual(25, pipeline.Count);
                Assert.AreEqual(CrowdLodLimits.MaxL0, pipeline.L0Count, "硬上限：恰 20 个 L0");
                Assert.AreEqual(5, pipeline.L1Count, "超限 5 个自动降级 L1");
                Assert.AreEqual(0, pipeline.L2Count);

                // 降级的是最远者：tag 0..19（近）留 L0，tag 20..24（远）降 L1。
                for (uint i = 0; i < 25; ++i)
                {
                    Assert.IsTrue(pipeline.TryGet(i, out CrowdLodEntityState s));
                    CrowdLodLevel expected = i < CrowdLodLimits.MaxL0
                        ? CrowdLodLevel.L0FullPrecision
                        : CrowdLodLevel.L1LowPoly;
                    Assert.AreEqual(expected, s.Level, $"tag {i} 档位");
                }

                // 一帧后不变（上限裁决是稳定态，不抖动）。
                pipeline.Tick(0.016f);
                Assert.AreEqual(CrowdLodLimits.MaxL0, pipeline.L0Count);
                Assert.AreEqual(5, pipeline.L1Count);
            }
        }

        [Test]
        public void EnforceCaps_DemotesFarthestNonStory_StoryImmune()
        {
            using (CrowdLODPipeline pipeline = NewPipeline())
            {
                // 远距剧情 NPC + 19 个近距非剧情（都有 L0 表示）→ 剧情 L0 挤占一档。
                ZkEntity story = SpawnEntity(100, new Vector3(0f, 0f, 100f), Vector3.zero);
                Assert.IsTrue(pipeline.Register(story, 100, isStoryNpc: true, NewNpcRepresentation("story")));
                for (uint i = 0; i < CrowdLodLimits.MaxL0 - 1; ++i)
                {
                    ZkEntity e = SpawnEntity(i, new Vector3(i * 0.1f, 0f, 4f), Vector3.zero);
                    Assert.IsTrue(pipeline.Register(e, i, isStoryNpc: false, NewNpcRepresentation($"npc{i}")));
                }

                Assert.AreEqual(CrowdLodLimits.MaxL0, pipeline.L0Count);
                Assert.IsTrue(pipeline.TryGet(100, out CrowdLodEntityState storyState));
                Assert.AreEqual(CrowdLodLevel.L0FullPrecision, storyState.Level,
                    "剧情 NPC 距离 100m 仍 L0（重要性标记）");

                // 第 21 个非剧情注册：L0 满但存在非剧情 L0 → 降级最远非剧情者，剧情免疫。
                // （新注册者 z=4.05 近于 tag18 的 ≈4.39m → 被降的是 tag18 而非新注册者。）
                ZkEntity newcomer = SpawnEntity(200, new Vector3(0f, 0f, 4.05f), Vector3.zero);
                Assert.IsTrue(pipeline.Register(newcomer, 200, isStoryNpc: false, NewNpcRepresentation("newcomer")));
                Assert.AreEqual(CrowdLodLimits.MaxL0, pipeline.L0Count, "L0 数量守恒（降 1 升 1）");
                Assert.IsTrue(pipeline.TryGet(100, out storyState));
                Assert.AreEqual(CrowdLodLevel.L0FullPrecision, storyState.Level, "剧情 NPC 未被降级");

                // 非剧情中最远者（tag 18，z=4m 群里 x 最大 → 距离最远）被降。
                Assert.IsTrue(pipeline.TryGet(CrowdLodLimits.MaxL0 - 2, out CrowdLodEntityState demoted));
                Assert.AreEqual(CrowdLodLevel.L1LowPoly, demoted.Level, "最远非剧情者自动降级");
                Assert.IsTrue(pipeline.TryGet(200, out CrowdLodEntityState keptNear));
                Assert.AreEqual(CrowdLodLevel.L0FullPrecision, keptNear.Level, "近距新注册者留在 L0");
            }
        }

        [Test]
        public void Register_BeyondTotalCap_IsRefused()
        {
            using (CrowdLODPipeline pipeline = NewPipeline())
            {
                // 远距（100m）→ 全部落 L2；L2 预算 = 总注册预算 = 512。
                for (uint i = 0; i < CrowdLodLimits.MaxL2; ++i)
                {
                    ZkEntity e = SpawnEntity(i, new Vector3(i * 0.05f, 0f, 100f), Vector3.zero);
                    Assert.IsTrue(pipeline.Register(e, i), $"第 {i + 1} 个注册应成功");
                }

                Assert.AreEqual(CrowdLodLimits.MaxL2, pipeline.Count);
                Assert.AreEqual(CrowdLodLimits.MaxL2, pipeline.L2Count);
                Assert.AreEqual(0, pipeline.L0Count);
                Assert.AreEqual(0, pipeline.L1Count);

                ZkEntity over = SpawnEntity(CrowdLodLimits.MaxL2, new Vector3(0f, 0f, 100f), Vector3.zero);
                Assert.IsFalse(pipeline.Register(over, CrowdLodLimits.MaxL2),
                    "L2 预算耗尽（L3 未实现无处可降）→ 拒绝注册");
                Assert.AreEqual(CrowdLodLimits.MaxL2, pipeline.Count, "被拒者不占位");
            }
        }

        [Test]
        public void Register_ContractViolations_Throw()
        {
            using (CrowdLODPipeline pipeline = NewPipeline())
            {
                ZkEntity a = SpawnEntity(0, Vector3.zero, Vector3.zero);
                Assert.IsTrue(pipeline.Register(a, 0));

                ZkEntity b = SpawnEntity(1, Vector3.zero, Vector3.zero);
                Assert.Throws<ArgumentException>(() => pipeline.Register(b, 0), "tag 重复须抛出");
                Assert.Throws<ArgumentException>(
                    () => pipeline.Register(b, 1, isStoryNpc: true, null), "剧情 NPC 缺 L0 表示须抛出");
            }
        }

        // ---- 距离驱动 + 迟滞 ----------------------------------------------------

        [Test]
        public void Hysteresis_DistanceLadderAndBands_NoOscillation()
        {
            using (CrowdLODPipeline pipeline = NewPipeline())
            {
                // 近距（5m）+ 有 L0 表示 → L0。
                ZkEntity e = SpawnEntity(0, new Vector3(0f, 0f, 5f), Vector3.zero);
                Assert.IsTrue(pipeline.Register(e, 0, isStoryNpc: false, NewNpcRepresentation("npc0")));
                Assert.IsTrue(pipeline.TryGet(0, out CrowdLodEntityState s));
                Assert.AreEqual(CrowdLodLevel.L0FullPrecision, s.Level);

                // 降档阶梯：L0 →(20m)→ L1 →(50m)→ L2。
                _world.SetComponent(e, KernelCrowdIds.Position, new CrowdPosition(0f, 0f, 20f));
                pipeline.Tick(0.016f);
                pipeline.TryGet(0, out s);
                Assert.AreEqual(CrowdLodLevel.L1LowPoly, s.Level, "20m > Down17 → 降 L1");

                _world.SetComponent(e, KernelCrowdIds.Position, new CrowdPosition(0f, 0f, 50f));
                pipeline.Tick(0.016f);
                pipeline.TryGet(0, out s);
                Assert.AreEqual(CrowdLodLevel.L2Billboard, s.Level, "50m > Down44 → 降 L2");

                // 迟滞带 [36,44]：39↔41 往返 20 帧，L2 不升不降。
                for (int frame = 0; frame < 20; ++frame)
                {
                    float z = frame % 2 == 0 ? 39f : 41f;
                    _world.SetComponent(e, KernelCrowdIds.Position, new CrowdPosition(0f, 0f, z));
                    pipeline.Tick(0.016f);
                    pipeline.TryGet(0, out s);
                    Assert.AreEqual(CrowdLodLevel.L2Billboard, s.Level, $"迟滞带内 L2 振荡帧 {frame}");
                }

                // 回程：15m < Up36 → 升 L1；带 [13,17] 内 14↔16 往返 20 帧，L1 不振荡。
                _world.SetComponent(e, KernelCrowdIds.Position, new CrowdPosition(0f, 0f, 15f));
                pipeline.Tick(0.016f);
                pipeline.TryGet(0, out s);
                Assert.AreEqual(CrowdLodLevel.L1LowPoly, s.Level, "15m < Up36 → 升 L1");

                for (int frame = 0; frame < 20; ++frame)
                {
                    float z = frame % 2 == 0 ? 14f : 16f;
                    _world.SetComponent(e, KernelCrowdIds.Position, new CrowdPosition(0f, 0f, z));
                    pipeline.Tick(0.016f);
                    pipeline.TryGet(0, out s);
                    Assert.AreEqual(CrowdLodLevel.L1LowPoly, s.Level, $"迟滞带内 L1 振荡帧 {frame}");
                }

                // 10m < Up13 → 升回 L0（完整阶梯闭合）。
                _world.SetComponent(e, KernelCrowdIds.Position, new CrowdPosition(0f, 0f, 10f));
                pipeline.Tick(0.016f);
                pipeline.TryGet(0, out s);
                Assert.AreEqual(CrowdLodLevel.L0FullPrecision, s.Level, "10m < Up13 → 升回 L0");
            }
        }

        // ---- kernel 数据源真实性与布局校验 ---------------------------------------

        [Test]
        public void Tick_KernelWorldDisposed_FreezesWithoutThrow()
        {
            CrowdLODPipeline pipeline = NewPipeline();
            try
            {
                ZkEntity e = SpawnEntity(0, new Vector3(0f, 0f, 5f), Vector3.zero);
                Assert.IsTrue(pipeline.Register(e, 0, isStoryNpc: false, NewNpcRepresentation("npc0")));
                pipeline.Tick(0.016f);
                CrowdLodEntityState before = default;
                pipeline.TryGet(0, out before);

                _world.Dispose(); // Play 期杀 kernel：管线必须冻结而非抛异常
                Assert.DoesNotThrow(() => pipeline.Tick(0.016f), "kernel 销毁后 Tick 静默冻结");
                pipeline.TryGet(0, out CrowdLodEntityState after);
                Assert.AreEqual(before.Level, after.Level, "档位冻结");
                Assert.AreEqual(before.Position, after.Position, "位置冻结");
            }
            finally
            {
                pipeline.Dispose();
                if (_world != null && !_world.IsDisposed)
                {
                    _world.Dispose(); // 断言失败路径也释放原生句柄
                }

                _world = null;
            }
        }

        [Test]
        public void Initialize_WrongKernelLayout_Throws()
        {
            // 未注册群体组件的裸 World → 布局校验拒绝（A-AND-08 口径守护）。
            using (ZKernelWorld bare = new ZKernelWorld())
            {
                var pipeline = new CrowdLODPipeline(bare, null, null);
                Assert.Throws<InvalidOperationException>(() => pipeline.Initialize(new TickQueue()),
                    "缺群体组件列的 World 必须被布局校验拦下");
                pipeline.Dispose();
            }
        }

        [Test]
        public void Config_InvalidHysteresisBands_Throw()
        {
            Assert.DoesNotThrow(() => new CrowdLodConfig().Validate(), "默认配置合法");

            var invertedL0 = new CrowdLodConfig { L0UpDistance = 17f, L0DownDistance = 13f };
            Assert.Throws<ArgumentException>(() => invertedL0.Validate(), "Up ≥ Down = 无迟滞带 → 拒绝");

            var invertedL1 = new CrowdLodConfig { L1UpDistance = 44f, L1DownDistance = 36f };
            Assert.Throws<ArgumentException>(() => invertedL1.Validate());

            var zeroUp = new CrowdLodConfig { L0UpDistance = 0f };
            Assert.Throws<ArgumentException>(() => zeroUp.Validate());
        }
    }
}
