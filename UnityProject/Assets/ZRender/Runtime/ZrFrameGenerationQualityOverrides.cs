// ZrFrameGenerationQualityOverrides.cs —— "开时覆盖 / 关时还原"的配对簿记（TASK-B-R9-08）。
//
// 为什么要单独拎出来：B-R9-07 验收遗留 3 —— 那两条"关"的用例只断言标志位，
// 把 RestoreQualityOverrides() 整段删掉照样全绿，还原行为等于没有守卫。
// 这里把覆盖/还原的**目标**抽成两个可注入的接口（管线副本 / 相机的深度开关），
// 让 EditMode 用例能拿桩走**生产入口**（ZrFrameGenerationRuntime.SetSubmissionEnabled(false)），
// 断言字段真的回到开之前的原值 —— 而不是只断言一个 bool 标志位。
//
// 深度这一项（N1）必须是**枚举原值**：URP 的真相是
// UniversalAdditionalCameraData.requiresDepthOption（Off / On / UsePipelineSettings），
// 而 requiresDepthTexture 只是它的 bool 投影，setter 会把 UsePipelineSettings 压成 On/Off
// （com.unity.render-pipelines.universal@14.0.12/Runtime/UniversalAdditionalCameraData.cs:478-491）。
// 存 bool 的后果：默认档 UsePipelineSettings 被永久钉成 On，关掉插帧后 URP 仍每帧渲一遍
// _CameraDepthTexture —— B-R9-07 关态慢 26-29% 的一半原因（另一半是代理链固有成本）。

using System.Collections.Generic;
using UnityEngine.Rendering.Universal;

namespace ZFrame.Render
{
    /// <summary>
    /// 管线副本上被插帧覆盖的两个字段（真实现 = <see cref="UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset"/>）。
    /// 抽成接口只为可测：EditMode 拿不到"真的装过通道"的资产（Editor 内不装配）。
    /// </summary>
    public interface IZrFrameGenerationPipelineTarget
    {
        /// <summary>MSAA 采样数（FFX 的 depth 必须单采样，开插帧时压到 1）。</summary>
        int MsaaSampleCount { get; set; }

        /// <summary>是否产出 _CameraDepthTexture（URP 只在 true 时建它）。</summary>
        bool SupportsCameraDepthTexture { get; set; }
    }

    /// <summary>
    /// 相机上的深度开关（真实现 = UniversalAdditionalCameraData）。
    /// **枚举而不是 bool**：Off / On / UsePipelineSettings 三态，bool 会把第三态压成前两态。
    /// </summary>
    public interface IZrFrameGenerationCameraDepthTarget
    {
        /// <summary>URP 每帧据此（叠加资产字段）决定是否渲深度。</summary>
        CameraOverrideOption RequiresDepthOption { get; set; }
    }

    /// <summary>真实现：URP 运行期管线副本（<c>QualitySettings.renderPipeline</c>）。</summary>
    public sealed class UrpFrameGenerationPipelineTarget : IZrFrameGenerationPipelineTarget
    {
        private readonly UniversalRenderPipelineAsset _asset;

        /// <summary>包装一个 URP 管线资产（null 时所有读写都是空操作）。</summary>
        public UrpFrameGenerationPipelineTarget(UniversalRenderPipelineAsset asset)
        {
            _asset = asset;
        }

        /// <inheritdoc />
        public int MsaaSampleCount
        {
            get => _asset != null ? _asset.msaaSampleCount : 0;
            set
            {
                if (_asset != null)
                {
                    _asset.msaaSampleCount = value;
                }
            }
        }

        /// <inheritdoc />
        public bool SupportsCameraDepthTexture
        {
            get => _asset != null && _asset.supportsCameraDepthTexture;
            set
            {
                if (_asset != null)
                {
                    _asset.supportsCameraDepthTexture = value;
                }
            }
        }
    }

    /// <summary>真实现：相机上的 UniversalAdditionalCameraData。</summary>
    public sealed class UrpFrameGenerationCameraDepthTarget : IZrFrameGenerationCameraDepthTarget
    {
        private readonly UniversalAdditionalCameraData _additional;

        /// <summary>包装一个相机的附加数据（null 时读写退化为 UsePipelineSettings）。</summary>
        public UrpFrameGenerationCameraDepthTarget(UniversalAdditionalCameraData additional)
        {
            _additional = additional;
        }

        /// <inheritdoc />
        public CameraOverrideOption RequiresDepthOption
        {
            get => _additional != null
                ? _additional.requiresDepthOption
                : CameraOverrideOption.UsePipelineSettings;
            set
            {
                if (_additional != null)
                {
                    _additional.requiresDepthOption = value;
                }
            }
        }
    }

    /// <summary>
    /// 覆盖/还原的配对簿记。语义与 B-R9-07 的实现逐条一致（原值只记一次、关时配对还原），
    /// 唯一的行为改动是深度这一项改存枚举原值（TASK-B-R9-08 N1）。
    /// <para>
    /// TASK-R10-06（审计 A-9）：相机这一项从"只记最后一台"改成**表式**——每个被改写过的目标
    /// 各自占一条 <c>目标 → 枚举原值</c>。两条不变量：
    ///   ① 换目标前先还原旧目标（见 <see cref="BindCamera"/>），所以活动相机的深度开销不会因为
    ///      "过场相机 → 玩家相机 / 相机销毁重建"而永久留在 On；被还原的目标随即出表。
    ///   ② <see cref="Restore"/> 还原**全表**（不只是最后绑定的那台），关插帧时不会有遗留条目。
    /// 生产路径任一时刻通常只有一台处于被改写状态；表的意义在于"还原动作失败/跳过的条目不会丢"
    /// （例如目标已销毁），以及"同一目标重复绑定不会把覆盖值记成原值"。
    /// </para>
    /// </summary>
    public sealed class ZrFrameGenerationQualityOverrides
    {
        /// <summary>插帧期间要求的 MSAA 采样数（FFX 的 depth 必须单采样）。</summary>
        public const int MsaaSampleCountForFrameGeneration = 1;

        private IZrFrameGenerationPipelineTarget _pipeline;
        private bool _pipelineRecorded;
        private int _originalMsaa = -1;
        private bool _originalDepthSupport;
        private CameraOverrideOption _originalDepthOption = CameraOverrideOption.UsePipelineSettings;

        // A-9：活动相机 + "已改写相机 → 开之前的枚举原值"的表。用引用相等（目标对象身份）作键：
        // 生产路径每次换相机都会 new 一个 UrpFrameGenerationCameraDepthTarget 包装，
        // 换目标时旧条目已被还原并移除，所以不会因为包装对象不同而漏还原。
        private IZrFrameGenerationCameraDepthTarget _camera;
        private readonly Dictionary<IZrFrameGenerationCameraDepthTarget, CameraOverrideOption> _cameraOriginals =
            new Dictionary<IZrFrameGenerationCameraDepthTarget, CameraOverrideOption>();

        /// <summary>是否已记录过管线原值（= 装配过通道）。</summary>
        public bool PipelineRecorded => _pipelineRecorded;

        /// <summary>开之前记录的 MSAA 原值（未记录时为 -1）。</summary>
        public int OriginalMsaaSampleCount => _originalMsaa;

        /// <summary>开之前记录的 supportsCameraDepthTexture 原值。</summary>
        public bool OriginalSupportsCameraDepthTexture => _originalDepthSupport;

        /// <summary>开之前记录的相机深度开关原值（枚举，不是 bool 投影）。</summary>
        public CameraOverrideOption OriginalRequiresDepthOption => _originalDepthOption;

        /// <summary>
        /// 绑定管线副本：记录 MSAA / 深度纹理支持的**原值**并立即覆盖。
        /// 重复绑定同一目标不会把"原值"记成已覆盖后的值（先解绑再记）。
        /// </summary>
        public void BindPipeline(IZrFrameGenerationPipelineTarget pipeline)
        {
            _pipeline = pipeline;
            _pipelineRecorded = false;
            if (pipeline == null)
            {
                return;
            }

            _originalMsaa = pipeline.MsaaSampleCount;
            _originalDepthSupport = pipeline.SupportsCameraDepthTexture;
            _pipelineRecorded = true;
            ApplyPipeline();
        }

        /// <summary>
        /// 绑定相机：记录 <see cref="CameraOverrideOption"/> 原值并置 On。
        /// 只记枚举原值 —— 这是 N1 的修法（旧实现记的是 bool 计算值，丢了 UsePipelineSettings）。
        /// <para>
        /// A-9：**换目标前先还原旧目标**。旧实现直接覆盖 <see cref="_camera"/>，旧相机的
        /// requiresDepthOption 就永久停在 On（URP 每帧多渲一遍深度），关插帧也只救得回最后一台。
        /// 同一目标重复绑定沿用第一次记下的原值（第二次看到的是自己写上去的 On，不能当原值）。
        /// 目标已销毁时还原写入退化为空操作（<see cref="UrpFrameGenerationCameraDepthTarget"/> 的
        /// null 守卫），条目照样出表，不抛异常。
        /// </para>
        /// </summary>
        public void BindCamera(IZrFrameGenerationCameraDepthTarget camera)
        {
            if (!ReferenceEquals(camera, _camera))
            {
                RestoreCamera(_camera);
                _camera = camera;
            }

            if (camera == null)
            {
                return;
            }

            if (!_cameraOriginals.TryGetValue(camera, out CameraOverrideOption recorded))
            {
                recorded = camera.RequiresDepthOption;
                _cameraOriginals.Add(camera, recorded);
            }

            _originalDepthOption = recorded;
            camera.RequiresDepthOption = CameraOverrideOption.On;
        }

        /// <summary>再开时重新覆盖（幂等；未绑定的目标跳过）。</summary>
        public void Apply()
        {
            if (_pipelineRecorded)
            {
                ApplyPipeline();
            }

            foreach (IZrFrameGenerationCameraDepthTarget camera in _cameraOriginals.Keys)
            {
                camera.RequiresDepthOption = CameraOverrideOption.On;
            }
        }

        /// <summary>
        /// 关时配对还原，**全表**处理（A-9：不再只还原最后绑定的那一台）。返回 true = 确实还原了东西
        /// （调用方据此决定要不要打日志，避免开机 PrimeDisabled 每次都刷一条"还原为 -1"）。
        /// 还原后相机表清空：下次开重新记一次（相机可能已经换了），管线原值保留以便反复开关。
        /// </summary>
        public bool Restore()
        {
            bool restored = false;

            if (_pipelineRecorded && _pipeline != null)
            {
                _pipeline.MsaaSampleCount = _originalMsaa;
                _pipeline.SupportsCameraDepthTexture = _originalDepthSupport;
                restored = true;
            }

            if (_cameraOriginals.Count > 0)
            {
                // 先取键快照：RestoreCamera 会边还原边出表，不能就地枚举。
                var cameras = new IZrFrameGenerationCameraDepthTarget[_cameraOriginals.Count];
                _cameraOriginals.Keys.CopyTo(cameras, 0);
                foreach (IZrFrameGenerationCameraDepthTarget camera in cameras)
                {
                    if (RestoreCamera(camera))
                    {
                        restored = true;
                    }
                }
            }

            _camera = null;
            return restored;
        }

        /// <summary>清空全部绑定与原值（测试用；生产路径不调用）。</summary>
        public void Reset()
        {
            _pipeline = null;
            _camera = null;
            _cameraOriginals.Clear();
            _pipelineRecorded = false;
            _originalMsaa = -1;
            _originalDepthSupport = false;
            _originalDepthOption = CameraOverrideOption.UsePipelineSettings;
        }

        /// <summary>
        /// 还原单个相机目标并出表。只有"确实记过原值"的目标才写回（没记过就不动别人）；
        /// 目标已销毁（Unity fake-null）时写入退化为空操作，条目照样移除 —— 不抛、不残留。
        /// </summary>
        private bool RestoreCamera(IZrFrameGenerationCameraDepthTarget camera)
        {
            if (camera == null)
            {
                return false;
            }

            if (!_cameraOriginals.TryGetValue(camera, out CameraOverrideOption original))
            {
                return false;
            }

            _cameraOriginals.Remove(camera);
            camera.RequiresDepthOption = original;
            return true;
        }

        private void ApplyPipeline()
        {
            _pipeline.SupportsCameraDepthTexture = true;
            if (_pipeline.MsaaSampleCount > MsaaSampleCountForFrameGeneration)
            {
                _pipeline.MsaaSampleCount = MsaaSampleCountForFrameGeneration;
            }
        }
    }
}
