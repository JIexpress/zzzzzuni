// ZrFrameGenerationCameraSwitchTests.cs —— TASK-R10-06（审计 A-9）的守卫用例：
// 插帧开着时**活动相机变化**（过场→玩家、相机销毁重建、多相机叠加）不得把旧相机的
// requiresDepthOption 永久留在 On。
//
// 为什么要这一组：B-R9-08 的用例只注入**一个**桩相机（"开→On / 关→原值"），换相机这一步
// 从来没被走过。而 ZrFrameGenerationRuntime.OnCameraRendered 的换相机分支过去是
// "直接覆盖 s_quality 的相机目标"，旧相机既没被还原，也没人记得它 —— URP 于是每帧为它多渲
// 一遍深度，直到进程重启。这里全部用例都走**生产入口**（OnCameraRendered / SetSubmissionEnabled），
// 用真的 Camera + UniversalAdditionalCameraData，断言字段值而不是标志位。
//
// 四条：
//   1) 两相机切换 → 旧相机立刻回到自己的枚举原值（修前红：停在 On）；
//   2) 三相机依次绑定 → 每台的枚举原值互不覆盖，关插帧后三台都在原值（修前红：只还原最后一台）；
//   3) 相机被销毁（Unity fake-null）→ 还原路径不抛，且不挡住后续相机的还原（修前红：后一台换出时抛/漏还原）；
//   4) 同一目标重复绑定 → 不能把手写上去的 On 记成"原值"（修前红：关插帧后仍是 On）。
//
// 第 4 条直接对 ZrFrameGenerationQualityOverrides 做单元断言：换相机时生产路径每次都会 new 一个
// UrpFrameGenerationCameraDepthTarget 包装（引用不同），"同一台相机被再次绑定"这一支只有直接
// 调 BindCamera 才能钉住。

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using ZFrame.Render;

namespace ZFrame.Render.Tests
{
    public sealed class ZrFrameGenerationCameraSwitchTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();

        /// <summary>相机深度开关的桩（第 4 条用；真实现见 UrpFrameGenerationCameraDepthTarget）。</summary>
        private sealed class StubCameraTarget : IZrFrameGenerationCameraDepthTarget
        {
            public CameraOverrideOption RequiresDepthOption { get; set; } =
                CameraOverrideOption.UsePipelineSettings;
        }

        [TearDown]
        public void TearDown()
        {
            // 静态运行期状态在 EditMode 里跨用例存活：先"关"（走一遍还原），再清目标，最后销毁相机。
            ZrFrameGenerationRuntime.SetSubmissionEnabled(false);
            ZrFrameGenerationRuntime.BindQualityTargetsForTests(null, null);
            foreach (GameObject go in _created)
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }

            _created.Clear();
        }

        // ---- 1. 两相机切换：旧相机必须当场还原 ------------------------------

        [Test]
        public void SwitchingToAnotherCamera_RestoresThePreviousCamerasDepthOption()
        {
            ZrFrameGenerationRuntime.SetSubmissionEnabled(true);
            Camera first = CreateCamera("FG_Cam_A", CameraOverrideOption.UsePipelineSettings);
            Camera second = CreateCamera("FG_Cam_B", CameraOverrideOption.UsePipelineSettings);

            ZrFrameGenerationRuntime.OnCameraRendered(first);
            Assert.AreEqual(CameraOverrideOption.On, DepthOptionOf(first), "开插帧后活动相机必须强制要深度");

            ZrFrameGenerationRuntime.OnCameraRendered(second);

            Assert.AreEqual(CameraOverrideOption.On, DepthOptionOf(second), "新相机必须被置 On");
            Assert.AreEqual(CameraOverrideOption.UsePipelineSettings, DepthOptionOf(first),
                "换相机后旧相机必须回到枚举原值（A-9：修前它永久停在 On，URP 每帧多渲一遍深度）");
        }

        // ---- 2. 三相机叠加：各自记原值、互不覆盖，关插帧全量还原 --------------

        [Test]
        public void ThreeCamerasInARow_EachKeepsItsOwnOriginalValue_AndCloseRestoresThemAll()
        {
            ZrFrameGenerationRuntime.SetSubmissionEnabled(true);
            Camera a = CreateCamera("FG_Cam_A", CameraOverrideOption.Off);
            Camera b = CreateCamera("FG_Cam_B", CameraOverrideOption.On);
            Camera c = CreateCamera("FG_Cam_C", CameraOverrideOption.UsePipelineSettings);

            ZrFrameGenerationRuntime.OnCameraRendered(a);
            Assert.AreEqual(CameraOverrideOption.On, DepthOptionOf(a));

            ZrFrameGenerationRuntime.OnCameraRendered(b);
            Assert.AreEqual(CameraOverrideOption.Off, DepthOptionOf(a), "A 的原值是 Off：换到 B 之后必须回到 Off");
            Assert.AreEqual(CameraOverrideOption.On, DepthOptionOf(b));

            ZrFrameGenerationRuntime.OnCameraRendered(c);
            Assert.AreEqual(CameraOverrideOption.Off, DepthOptionOf(a), "A 的原值不能被 B/C 的绑定覆盖");
            Assert.AreEqual(CameraOverrideOption.On, DepthOptionOf(b), "B 的原值是 On：换到 C 之后仍是 On（不是被 C 写坏）");
            Assert.AreEqual(CameraOverrideOption.On, DepthOptionOf(c));

            // 关插帧：最后一台（当前活动相机）也必须还原 —— 修前只还原这一台，前两台永远留在 On。
            ZrFrameGenerationRuntime.SetSubmissionEnabled(false);

            Assert.AreEqual(CameraOverrideOption.Off, DepthOptionOf(a), "关插帧后 A 必须在原值");
            Assert.AreEqual(CameraOverrideOption.On, DepthOptionOf(b), "关插帧后 B 必须在原值");
            Assert.AreEqual(CameraOverrideOption.UsePipelineSettings, DepthOptionOf(c),
                "关插帧后 C（最后绑定的那台）必须在原值");
        }

        // ---- 3. 相机被销毁（fake-null）：不抛、清引用、不挡住后续还原 ---------

        [Test]
        public void DestroyedCamera_IsSkippedWithoutThrowing_AndDoesNotStallTheNextSwitch()
        {
            ZrFrameGenerationRuntime.SetSubmissionEnabled(true);
            Camera doomed = CreateCamera("FG_Cam_Doomed", CameraOverrideOption.UsePipelineSettings);
            Camera second = CreateCamera("FG_Cam_B", CameraOverrideOption.Off);
            Camera third = CreateCamera("FG_Cam_C", CameraOverrideOption.UsePipelineSettings);

            ZrFrameGenerationRuntime.OnCameraRendered(doomed);
            Assert.AreEqual(CameraOverrideOption.On, DepthOptionOf(doomed));

            GameObject doomedGo = doomed.gameObject; // 先取 GameObject 句柄：销毁后再碰 doomed 就是 fake-null
            Object.DestroyImmediate(doomedGo);
            _created.Remove(doomedGo);

            // 旧目标已是 fake-null：还原必须安全跳过（写入退化为空操作），绝不能抛 MissingReferenceException。
            Assert.DoesNotThrow(() => ZrFrameGenerationRuntime.OnCameraRendered(second),
                "相机销毁后换绑不得抛（Unity fake-null 必须安全跳过）");
            Assert.AreEqual(CameraOverrideOption.On, DepthOptionOf(second));

            Assert.DoesNotThrow(() => ZrFrameGenerationRuntime.OnCameraRendered(third),
                "死相机的条目不得在后续换绑时再被碰一次");
            Assert.AreEqual(CameraOverrideOption.Off, DepthOptionOf(second),
                "死相机不挡住还原链：换到 C 后 B 必须回到自己的原值（修前红：停在 On）");

            Assert.DoesNotThrow(() => ZrFrameGenerationRuntime.SetSubmissionEnabled(false),
                "关插帧时还原全表不得抛");
            Assert.AreEqual(CameraOverrideOption.UsePipelineSettings, DepthOptionOf(third));
        }

        // ---- 4. 同一目标重复绑定：不能把覆盖值记成原值 -----------------------

        [Test]
        public void RebindingTheSameTarget_DoesNotRecordTheOverwrittenValueAsTheOriginal()
        {
            var camera = new StubCameraTarget { RequiresDepthOption = CameraOverrideOption.UsePipelineSettings };
            var quality = new ZrFrameGenerationQualityOverrides();

            quality.BindCamera(camera);
            Assert.AreEqual(CameraOverrideOption.On, camera.RequiresDepthOption);

            quality.BindCamera(camera);
            Assert.AreEqual(CameraOverrideOption.On, camera.RequiresDepthOption);

            Assert.IsTrue(quality.Restore(), "绑过就必须有东西可还原");
            Assert.AreEqual(CameraOverrideOption.UsePipelineSettings, camera.RequiresDepthOption,
                "重复绑定同一目标时原值只能记一次（第二次看到的是自己写上去的 On，不能当原值）");
        }

        // ---- 工具 -------------------------------------------------------------

        private Camera CreateCamera(string name, CameraOverrideOption option)
        {
            var go = new GameObject(name);
            _created.Add(go);
            Camera camera = go.AddComponent<Camera>();
            camera.cameraType = CameraType.Game;
            camera.GetUniversalAdditionalCameraData().requiresDepthOption = option;
            return camera;
        }

        private static CameraOverrideOption DepthOptionOf(Camera camera)
        {
            return camera.GetUniversalAdditionalCameraData().requiresDepthOption;
        }
    }
}
