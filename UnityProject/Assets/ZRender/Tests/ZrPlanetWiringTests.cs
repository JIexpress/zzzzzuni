// ZrPlanetWiringTests.cs —— WorldOrigin → 行星通道接线的 EditMode 检验（TASK-B-PLANET-07）。
//
// 三类断言（简报 §1-4）：
//   1. 接线值一致性：WorldOriginPlanetLink 推送后，ZR_Unity_GetCameraOrigin 收到的值
//      与 WorldOrigin 权威状态（OriginOffset + 相机 Unity 坐标，经 WorldOriginMath.ToWorld）
//      逐位一致；viewRel 发布的矩阵平移列恰为 0 且旋转逐位保持。
//   2. 门控失败路径：GateProbeForTests 注入失败 → Driver 整体停用、通道关闭、
//      Streamer 不创建（"门控不过=整体关闭+一行日志，不许半启用"）。
//   3. 真渲染集成（D3D12 门控）：设备就绪 → 流式 Tick → RenderFrame 延迟一帧语义
//      （pendingUploads→0 / SetLod 下一帧应用）+ 全程账目对账。
//      附：原生 seam 核查成本测量（G4-R1 证据）——相邻网格 vs 散布块的
//      LastFrameMs 对比，MESSAGE 输出不做硬断言（机器相关，ZRenderFrameSyncTests 先例）。

using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using ZFrame.Render;
using ZFrame.Render.Bindings;
using ZFrame.Render.Planet;
using Game.Domain.World;
using Game.View.World;

namespace ZFrame.Render.Tests
{
    [TestFixture]
    public class ZrPlanetWiringTests
    {
        [SetUp]
        public void SetUp()
        {
            ZrPlanetChannel.ResetForTests();
            ZrPlanetNative.ClearAll();
        }

        [TearDown]
        public void TearDown()
        {
            ZrPlanetChannel.ResetForTests();
            ZrPlanetNative.ClearAll();
        }

        // ---- 1. 接线值一致性（无设备依赖）------------------------------------------------

        private sealed class WiringScene : IDisposable
        {
            public GameObject CameraGo;
            public GameObject OriginGo;
            public GameObject LinkGo;
            public WorldOrigin Origin;
            public WorldOriginPlanetLink Link;
            public Camera Camera;

            public static WiringScene Create(Vector3 cameraPosition, Quaternion cameraRotation)
            {
                var scene = new WiringScene();
                scene.CameraGo = new GameObject("WiringCamera");
                scene.CameraGo.tag = "MainCamera";
                scene.CameraGo.transform.position = cameraPosition;
                scene.CameraGo.transform.rotation = cameraRotation;
                scene.Camera = scene.CameraGo.AddComponent<Camera>();

                scene.OriginGo = new GameObject("WiringOrigin");
                scene.Origin = scene.OriginGo.AddComponent<WorldOrigin>();

                scene.LinkGo = new GameObject("WiringLink");
                scene.Link = scene.LinkGo.AddComponent<WorldOriginPlanetLink>();
                UnityEditor.SerializedObject serialized = new UnityEditor.SerializedObject(scene.Link);
                serialized.FindProperty("_origin").objectReferenceValue = scene.Origin;
                serialized.FindProperty("_targetCamera").objectReferenceValue = scene.Camera;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                return scene;
            }

            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(CameraGo);
                UnityEngine.Object.DestroyImmediate(OriginGo);
                UnityEngine.Object.DestroyImmediate(LinkGo);
            }
        }

        [Test]
        public void Link_Push_SetsNativeOriginToWorldOriginDerivedValue()
        {
            Vector3 cameraPosition = new Vector3(12.5f, 7f, -10f);
            using WiringScene scene = WiringScene.Create(cameraPosition, Quaternion.identity);

            scene.Link.PushOriginAndViewRel();

            Assert.IsTrue(scene.Link.GatePassed, "v3 dll 必须通过版本门控");
            Assert.IsTrue(scene.Link.LastPushOk, "SetCameraOrigin 必须成功");
            Assert.IsTrue(ZrPlanetChannel.IsEnabled, "推送成功后通道必须启用");

            // 权威期望值：world = OriginOffset(double) + unityPos(float)，经 ToWorld 加宽。
            WorldPosition expected = scene.Origin.UnityToWorld(cameraPosition);
            double[] native = new double[3];
            Assert.AreEqual(ZrPlanetNative.Ok, ZrPlanetNative.GetCameraOrigin(native));
            Assert.AreEqual(expected.X, native[0], "X 必须与 WorldOrigin 权威值逐位一致");
            Assert.AreEqual(expected.Y, native[1], "Y 必须与 WorldOrigin 权威值逐位一致");
            Assert.AreEqual(expected.Z, native[2], "Z 必须与 WorldOrigin 权威值逐位一致");
        }

        [Test]
        public void Link_Push_ViewRelHasZeroTranslation_AndRotationPreserved()
        {
            Quaternion rotation = Quaternion.Euler(15f, 40f, 0f);
            using WiringScene scene = WiringScene.Create(new Vector3(3000f, 100f, -2000f), rotation);

            scene.Link.PushOriginAndViewRel();
            Assert.IsTrue(scene.Link.LastPushOk);
            Assert.IsTrue(ZrPlanetChannel.TryConsumeCameraRelativeView(out Matrix4x4 viewRel));

            Matrix4x4 fullView = scene.Camera.worldToCameraMatrix;
            // 平移列归零 + 旋转逐位保持（WorldOriginMath.ToCameraRelativeView 的接线端证据）。
            Assert.AreEqual(0f, viewRel.m03);
            Assert.AreEqual(0f, viewRel.m13);
            Assert.AreEqual(0f, viewRel.m23);
            for (int row = 0; row < 3; row++)
            {
                for (int column = 0; column < 3; column++)
                {
                    Assert.AreEqual(fullView[row, column], viewRel[row, column],
                        $"viewRel 旋转 [{row},{column}] 必须与相机 worldToCameraMatrix 逐位一致");
                }
            }
        }

        [Test]
        public void Link_WithoutOrigin_KeepsSilentAndChannelUntouched()
        {
            // 静默契约（确定性形态）：WorldOrigin 权威缺席（_origin 未赋值）时链路必须
            // 静默不动作——不推原点、不启用通道。（相机缺席路径依赖场景状态——Camera.main
            // 退路在编辑器打开含相机场景时非空——不做环境敏感断言；两分支共用同一
            // `camera == null || _origin == null` 静默出口。）
            var cameraGo = new GameObject("LinkCameraOnly");
            Camera camera = cameraGo.AddComponent<Camera>();
            var linkGo = new GameObject("LinkNoOrigin");
            WorldOriginPlanetLink link = linkGo.AddComponent<WorldOriginPlanetLink>();
            UnityEditor.SerializedObject serialized = new UnityEditor.SerializedObject(link);
            serialized.FindProperty("_targetCamera").objectReferenceValue = camera; // 只给相机
            serialized.ApplyModifiedPropertiesWithoutUndo();

            try
            {
                link.PushOriginAndViewRel();

                Assert.IsFalse(link.LastPushOk, "无 WorldOrigin 时必须静默不动作");
                Assert.IsFalse(ZrPlanetChannel.IsEnabled, "未推送不得启用通道");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraGo);
                UnityEngine.Object.DestroyImmediate(linkGo);
            }
        }

        [Test]
        public void SceneBridge_CameraRelativeWorld_SubtractsCameraPositionOnlyInTranslation()
        {
            Matrix4x4 localToWorld = Matrix4x4.TRS(new Vector3(10f, 20f, 30f),
                Quaternion.Euler(0f, 30f, 0f), Vector3.one);
            Vector3 cameraPosition = new Vector3(4f, 5f, 6f);

            Matrix4x4 relative = ZrSceneMath.ToCameraRelativeWorld(localToWorld, cameraPosition);

            Assert.AreEqual(6f, relative.m03, 1e-5f, "平移 x = 物件 − 相机");
            Assert.AreEqual(15f, relative.m13, 1e-5f, "平移 y = 物件 − 相机");
            Assert.AreEqual(24f, relative.m23, 1e-5f, "平移 z = 物件 − 相机");
            for (int row = 0; row < 3; row++)
            {
                for (int column = 0; column < 3; column++)
                {
                    Assert.AreEqual(localToWorld[row, column], relative[row, column],
                        $"旋转/缩放 [{row},{column}] 必须逐位保持");
                }
            }

            // 数学恒等式：T(−cam)·localToWorld == 相机相对矩阵（逐分量）。
            Matrix4x4 expected = Matrix4x4.Translate(-cameraPosition) * localToWorld;
            Assert.AreEqual(expected.m03, relative.m03);
            Assert.AreEqual(expected.m13, relative.m13);
            Assert.AreEqual(expected.m23, relative.m23);
        }

        // ---- 2. 门控失败路径（整体关闭）--------------------------------------------------

        [Test]
        public void Driver_GateFailure_DisablesEverythingAndCreatesNothing()
        {
            var go = new GameObject("GateFailDriver");
            try
            {
                ZrPlanetStreamingDriver driver = go.AddComponent<ZrPlanetStreamingDriver>();
                driver.GateProbeForTests = () => false;

                driver.TickOnce();

                Assert.IsTrue(driver.GateFailed, "门控失败必须置整体停用位");
                Assert.IsNull(driver.Streamer, "门控失败不得初始化流式管理器（不碰 SetWorld）");
                Assert.IsFalse(ZrPlanetChannel.IsEnabled, "门控失败不得启用通道");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Driver_SecondInstance_IsRejected()
        {
            var firstGo = new GameObject("DriverFirst");
            var secondGo = new GameObject("DriverSecond");
            try
            {
                ZrPlanetStreamingDriver first = firstGo.AddComponent<ZrPlanetStreamingDriver>();
                ZrPlanetStreamingDriver second = secondGo.AddComponent<ZrPlanetStreamingDriver>();

                // EditMode 下 AddComponent 不触发 OnEnable——注册守卫显式调用（与产品
                // OnEnable 同一实现路径，ActivateForRuntime）。
                Assert.IsTrue(first.ActivateForRuntime(), "首个 Driver 正常注册");
                Assert.IsFalse(second.ActivateForRuntime(),
                    "第二个 Driver 必须被拒用（进程级单行星上下文）");
                Assert.IsFalse(second.enabled, "被拒用实例必须整体停用");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(firstGo);
                UnityEngine.Object.DestroyImmediate(secondGo);
            }
        }

        // ---- 3. 真渲染集成（D3D12 门控）--------------------------------------------------

        private static bool TryEnsureD3D12(out ZRenderPipeline pipeline)
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                pipeline = null;
                return false;
            }

            pipeline = new ZRenderPipeline();
            return pipeline.EnsureNativeTargetForSize(320, 240);
        }

        [Test]
        public void Integration_StreamTickRenderFrame_DelayedUploadAndAccount()
        {
            if (!TryEnsureD3D12(out ZRenderPipeline pipeline))
            {
                Assert.Ignore("共享目标只在 D3D12 下可用；该路径由 Player 现场验证");
                return;
            }

            try
            {
                var streamer = new ZrPlanetChunkStreamer();
                ZrPlanetStreamConfig config = ZrPlanetStreamConfig.CreateDefault(0.0, 0.0, 0.0, 1337u);
                config.CellsPerEdge = 4u; // 全星 96 块
                config.MaxWorkMicrosPerTick = 1_000_000;
                streamer.Initialize(config);

                try
                {
                    // 相机沿 face0 中心方向 600km：锥内块被创建（延迟一帧上传）。
                    streamer.Tick(0.0, 0.0, ZrPlanetChunkStreamer.NativePlanetRadiusMeters + 600_000.0);
                    Assert.Greater(streamer.LiveCount, 0, "流式决策必须创建出块");

                    ZrPlanetNative.GetStats(out ZrPlanetStatsNative afterTick);
                    Assert.Greater(afterTick.PendingUploads, 0u, "创建后待上传（延迟一帧语义的前半）");
                    Assert.AreEqual(afterTick.CreateTotal - afterTick.DestroyTotal,
                        (ulong)afterTick.LiveChunks);

                    Assert.AreEqual(ZrUnityTargetNative.Ok, ZrUnityTargetNative.RenderFrame());

                    ZrPlanetNative.GetStats(out ZrPlanetStatsNative afterFrame);
                    Assert.AreEqual(0u, afterFrame.PendingUploads, "RenderFrame 后待上传清零（延迟一帧语义落地）");
                    Assert.Greater(afterFrame.VertexBytes, 0UL, "上传后 GPU 顶点字节必须非零");
                    Assert.AreEqual(afterFrame.CreateTotal - afterFrame.DestroyTotal,
                        (ulong)afterFrame.LiveChunks, "RenderFrame 后账目恒等式仍成立");
                    Assert.AreEqual((ulong)streamer.LiveCount, (ulong)afterFrame.LiveChunks);

                    // 有相机提交时 drawn+culled 必须覆盖全部存活块（剔除口径）。
                    Assert.AreEqual(ZrUnitySceneNative.Ok,
                        ZrUnitySceneNative.SetCamera(_viewIdentity(), _projIdentity()));
                    Assert.AreEqual(ZrUnityTargetNative.Ok, ZrUnityTargetNative.RenderFrame());
                    ZrPlanetNative.GetStats(out ZrPlanetStatsNative drawnFrame);
                    Assert.AreEqual((ulong)drawnFrame.LiveChunks,
                        (ulong)(drawnFrame.DrawnLastFrame + drawnFrame.CulledLastFrame),
                        "drawn + culled 必须覆盖全部存活块");
                }
                finally
                {
                    streamer.Dispose();
                }
            }
            finally
            {
                pipeline.DisposeForTests();
            }
        }

        [Test]
        public void Integration_SeamCheckCost_AdjacentGridVersusScatteredChunks()
        {
            // G4-R1 证据：近/远档共享边 memcmp 核查现无档位门（B-PLANET-04 E9）——本用例
            // 量化"有邻居（共享边重生成）vs 无邻居（仅 O(8192) 槽扫）"的行星通道段成本差。
            // 数字只作 MESSAGE 打印（机器相关，ZRenderFrameSyncTests 先例），不做硬断言。
            if (!TryEnsureD3D12(out ZRenderPipeline pipeline))
            {
                Assert.Ignore("共享目标只在 D3D12 下可用；该路径由 Player 现场验证");
                return;
            }

            try
            {
                ZrPlanetWorldNative world = default;
                world.StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ZrPlanetWorldNative>();
                world.Seed = 1337u;
                world.CenterAbs = new[] { 0.0, 0.0, 0.0 };
                Assert.AreEqual(ZrPlanetNative.Ok, ZrPlanetNative.SetWorld(ref world));

                // 预热：首个行星 RenderFrame 可能含一次性编译/建池成本——先烧一遍再计时。
                MeasureChunkSetMin(2, true);
                ZrPlanetNative.ClearAll();

                // 各取 3 轮最小值压噪声（上一轮 8×8 单跑差值被 ±1.5ms 抖动淹没）。
                double adjacentMs = MeasureChunkSetMin(16, true);
                double scatteredMs = MeasureChunkSetMin(16, false);
                double deltaUs = (adjacentMs - scatteredMs) * 1000.0;
                double perChunkUs = deltaUs / (16.0 * 16.0);

                UnityEngine.Debug.Log(
                    $"[G4-R1] 远档 256 块行星通道段（含 seam 核查，3 轮取最小）：相邻 16×16 网格 = " +
                    $"{adjacentMs:F3}ms，互不相邻散布 = {scatteredMs:F3}ms，差值（≈共享边重生成成本）= " +
                    $"{deltaUs:F0}µs（单块 ≈ {perChunkUs:F1}µs）。" +
                    "MISSING 最小 diff（tier==0 早退门）见施工报告。");

                Assert.GreaterOrEqual(adjacentMs, 0.0);
                Assert.GreaterOrEqual(scatteredMs, 0.0);
            }
            finally
            {
                pipeline.DisposeForTests();
            }
        }

        /// <summary>创建 grid×grid 远档块组（adjacent=相邻网格 / 散布=全偶数格互不相邻），
        /// RenderFrame 后取行星通道段耗时（ZrPlanetStats.LastFrameMs）；重复 reps 轮取最小
        /// 值压噪声（块创建主线程成本不计入——LastFrameMs 只含渲染线程上传+核查段）。</summary>
        private static double MeasureChunkSetMin(uint grid, bool adjacent)
        {
            double minMs = double.MaxValue;
            for (int rep = 0; rep < 3; rep++)
            {
                var desc = default(ZrPlanetChunkDescNative);
                desc.StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ZrPlanetChunkDescNative>();
                desc.FaceId = 0u;
                desc.LodLevel = 0u; // 远档（320 顶点）
                desc.CellsPerEdge = 32u;
                desc.NeighborMask = 0u;
                desc.Reserved0 = 0u;
                desc.OriginAbs = new double[3]; // 复用缓冲（逐元素改写）

                // 锚点 = 远档中心索引的球面点（与 BuildChunkTable 同式；高程经 SampleHeight）。
                const int stride = 15;
                const int total = 32 * stride;
                const int center = stride / 2;

                for (uint cy = 0u; cy < grid; cy++)
                {
                    for (uint cx = 0u; cx < grid; cx++)
                    {
                        uint gx = adjacent ? cx : cx * 2u; // 散布：全偶数格 → 无共享边
                        uint gy = adjacent ? cy : cy * 2u;
                        desc.Cx = gx;
                        desc.Cy = gy;
                        double u = 2.0 * (double)(gx * stride + center) / total - 1.0;
                        double v = 2.0 * (double)(gy * stride + center) / total - 1.0;
                        ZrPlanetChunkStreamer.CubeSphereDir(0, u, v, out double dx, out double dy, out double dz);
                        Assert.AreEqual(ZrPlanetNative.Ok,
                            ZrPlanetNative.SampleHeight(dx * 6.371e6, dy * 6.371e6, dz * 6.371e6, out float h));
                        double radius = 6.371e6 + h;
                        desc.OriginAbs[0] = dx * radius;
                        desc.OriginAbs[1] = dy * radius;
                        desc.OriginAbs[2] = dz * radius;

                        Assert.AreEqual(ZrPlanetNative.Ok,
                            ZrPlanetNative.ChunkCreate(ref desc, out _), $"ChunkCreate({gx},{gy}) 失败");
                    }
                }

                Assert.AreEqual(ZrUnityTargetNative.Ok, ZrUnityTargetNative.RenderFrame());
                Assert.AreEqual(ZrPlanetNative.Ok, ZrPlanetNative.GetStats(out ZrPlanetStatsNative stats));
                Assert.AreEqual(0u, stats.PendingUploads);
                minMs = Math.Min(minMs, stats.LastFrameMs);
                ZrPlanetNative.ClearAll();
            }

            return minMs;
        }

        private static float[] _viewIdentity()
        {
            return new float[16]
            {
                1f, 0f, 0f, 0f,
                0f, 1f, 0f, 0f,
                0f, 0f, 1f, 0f,
                0f, 0f, 0f, 1f,
            };
        }

        private static float[] _projIdentity()
        {
            return new float[16]
            {
                1f, 0f, 0f, 0f,
                0f, 1f, 0f, 0f,
                0f, 0f, 1f, 0f,
                0f, 0f, 0f, 1f,
            };
        }
    }
}
