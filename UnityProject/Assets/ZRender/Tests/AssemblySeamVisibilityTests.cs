// AssemblySeamVisibilityTests.cs —— TASK-B-R10-01：FG 的测试注入点必须**不是 public**。
//
// B-R9-08 为了给"开→覆盖、关→还原"做有牙测试，在 ZrFrameGenerationRuntime 上开了
// BindQualityTargetsForTests。它是测试需要，不是产品 API。本用例把这条约束钉住：
// 一旦有人把它改回 public（"测试调不到就放开"），这条会红。
//
// 本用例能调用它，靠 Runtime/AssemblyInfo.cs 的 InternalsVisibleTo("ZFrame.Render.Tests")；
// IVT 写错是编译期错误，比运行时断言更早暴露。

using System.Reflection;
using NUnit.Framework;
using ZFrame.Render;

namespace ZFrame.Render.Tests
{
    public sealed class AssemblySeamVisibilityTests
    {
        private const BindingFlags AnyMethod = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        [Test]
        public void FrameGenerationQualityBindSeam_IsInternal()
        {
            MethodInfo m = typeof(ZrFrameGenerationRuntime).GetMethod("BindQualityTargetsForTests", AnyMethod);
            Assert.IsNotNull(m, "BindQualityTargetsForTests 必须仍然存在（接缝被删也要显式改本用例）");
            Assert.IsFalse(m.IsPublic, "BindQualityTargetsForTests 不得是 public（产品程序集的公开面不该被测试撑大）");
            Assert.IsTrue(m.IsAssembly, "BindQualityTargetsForTests 应为 internal（测试靠 InternalsVisibleTo 访问）");
            Assert.IsTrue(m.IsStatic, "BindQualityTargetsForTests 是静态注入点");
        }

        [Test]
        public void FrameGenerationQualityBindSeam_StillReachesTheProductionEntry()
        {
            // 接缝改 internal 后必须仍然"够用"：注入桩 → 生产入口开 → 桩上真的被覆盖。
            // 这条同时证明 IVT 不是摆设（访问不到就编不过）。
            var pipeline = new StubPipeline();
            var camera = new StubCamera();
            ZrFrameGenerationRuntime.BindQualityTargetsForTests(pipeline, camera);
            try
            {
                ZrFrameGenerationRuntime.SetSubmissionEnabled(true);
                Assert.AreEqual(ZrFrameGenerationQualityOverrides.MsaaSampleCountForFrameGeneration, pipeline.Msaa,
                    "开插帧后桩管线的 MSAA 必须被覆盖成 1");
                Assert.IsTrue(pipeline.SupportsDepth, "开插帧后桩管线必须要求深度纹理");
            }
            finally
            {
                ZrFrameGenerationRuntime.SetSubmissionEnabled(false);
                ZrFrameGenerationRuntime.BindQualityTargetsForTests(null, null);
            }
        }

        private sealed class StubPipeline : IZrFrameGenerationPipelineTarget
        {
            public int Msaa { get; set; } = 4;

            public bool SupportsDepth { get; set; }

            public int MsaaSampleCount
            {
                get => Msaa;
                set => Msaa = value;
            }

            public bool SupportsCameraDepthTexture
            {
                get => SupportsDepth;
                set => SupportsDepth = value;
            }
        }

        private sealed class StubCamera : IZrFrameGenerationCameraDepthTarget
        {
            public UnityEngine.Rendering.Universal.CameraOverrideOption RequiresDepthOption { get; set; } =
                UnityEngine.Rendering.Universal.CameraOverrideOption.UsePipelineSettings;
        }
    }
}
