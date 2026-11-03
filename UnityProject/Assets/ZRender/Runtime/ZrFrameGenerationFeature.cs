// ZrFrameGenerationFeature.cs —— 让 URP 产出 depth/MV 并把它们每帧交给 FFX
// （TASK-B-R9-06 阶段 1 的产品侧落点）。
//
// 两件事，缺一不可：
//   1) 空 pass 只做 `ConfigureInput(Motion | Depth)`：URP 14 没有资产级 motion
//      vector 开关，requiresMotionVectors 只由 pass 的输入声明驱动（B-R9-02 实证）；
//   2) pass 的 Execute（AfterRenderingTransparents）里把
//      `_CameraDepthTexture` / `_MotionVectorTexture` 的 ID3D12Resource* + 尺寸 + 相机
//      参数交给 dxgi 代理的 ZFG_FgSubmitInputs。
//
// 为什么不改 URP_*.asset：资产污染是明令禁止的。这里走 B-R9-02 证明过的运行期路径
// —— Instantiate 一份管线资产副本 + 副本渲染器数据上 Add 本特性，原资产一个字节不动。
//
// 不锁 GPU：本文件只读纹理句柄与相机参数，不做 WaitForSingleObject / Flush；
// 唯一的等待在原生侧 PrepareV2 的命令分配器环（3 环，正常帧不等，B-R9-04 已验 2.0×）。
//
// TASK-B-R9-07 在这里收掉三个"玩家能感觉到"的尾巴：
//   M2 关即还原：装通道时把 MSAA/supportsCameraDepthTexture/相机深度开关
//      的**原值**记下来，关开关时配对还原（URP 每帧读 asset 字段建相机描述符 → 下一帧生效，
//      不需要重启；还原的是运行期副本，磁盘资产从始至终没动过）。
//   M3 关即停采：提交关闭时 pass **不入队**，URP 因此不再为本通道建 MV / 拷深度，
//      Execute 不被调用（PassEnqueues 不涨、SubmitCalls 不涨）。
// TASK-B-R9-08 N1：相机深度开关的原值改存 CameraOverrideOption **枚举**（存 bool 投影
//   会把默认的 UsePipelineSettings 永久钉成 On，关态 URP 仍每帧渲一遍 depth）；
//   配对簿记搬进 ZrFrameGenerationQualityOverrides（可注入桩，供还原行为的守卫用例）。

using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ZFrame.Core;
using ZFrame.Render.Bindings;

namespace ZFrame.Render
{
    /// <summary>
    /// 运行期状态 + 每帧提交（进程内单例语义；设置服务只改"要不要开"）。
    /// </summary>
    public static class ZrFrameGenerationRuntime
    {
        private static readonly IZrFrameGenApi Api = ZrFrameGenRealApi.Instance;

        private static UniversalRenderPipelineAsset s_installedAsset;
        private static bool s_installFailed;
        private static string s_installError = string.Empty;
        private static bool s_submissionEnabled;
        private static bool s_pendingReset = true;
        private static int s_lastSubmitFrame = -1;
        private static bool s_warnedGeometry;
        private static bool s_warnedRejected;
        private static bool s_warnedMsaa;
        private static Camera s_preparedCamera;

        // M2（TASK-B-R9-07）：开 FG 时被覆盖的画质字段原值。只记录一次（首次装配），
        // 之后每次"关"按这对值还原、每次"开"再覆盖下去 —— 配对，不靠猜。
        // TASK-B-R9-08 N1：簿记搬进 ZrFrameGenerationQualityOverrides，深度开关改存
        // CameraOverrideOption **枚举原值**（旧实现存 requiresDepthTexture 的 bool 计算值，
        // 把 UsePipelineSettings 永久压成 On，关态白渲一遍 depth）。
        private static readonly ZrFrameGenerationQualityOverrides s_quality =
            new ZrFrameGenerationQualityOverrides();

        /// <summary>是否在向 FFX 提交输入（= 用户把开关拨到"开"且通道装好了）。</summary>
        public static bool SubmissionEnabled => s_submissionEnabled;

        /// <summary>提交通道所在 pass 被 URP 入队的次数（M3：关闭时必须停止增长）。</summary>
        public static int PassEnqueues { get; private set; }

        /// <summary>因为"提交已关"而没入队的次数（M3：关闭时逐帧 +1，帧率下即每帧一次）。</summary>
        public static int PassSkips { get; private set; }

        /// <summary>提交调用次数（每帧一次；诊断用）。</summary>
        public static int SubmitCalls { get; private set; }

        /// <summary>被代理拒绝的次数（0 = 一次都没被拒）。</summary>
        public static int SubmitRejected { get; private set; }

        /// <summary>最近一次没提交的原因（诊断用；正常为空串）。</summary>
        public static string LastNote { get; private set; } = string.Empty;

        /// <summary>最近一次读到的统计快照（每 60 次提交刷新一次）。</summary>
        public static FrameGenerationStatsInfo LastStats { get; private set; }

        /// <summary>当前活动管线是否 URP（MV/深度的唯一来源）。</summary>
        public static bool PipelineIsUrp => GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;

        /// <summary>通道是否已装上（只读，诊断用）。</summary>
        public static bool PipelineInstalled => s_installedAsset != null;

        /// <summary>
        /// 打开/关闭"每帧提交"（关 = 纯透传，不碰 FFX 上下文）。
        /// **关的副作用有两件，都在这里收干净**（TASK-B-R9-07）：
        ///   1) 画质还原：MSAA 与"强制深度纹理"回到开之前的原值（M2，无需重启）；
        ///   2) 采集停止：pass 不再入队（见 <see cref="ZrFrameGenerationFeature.AddRenderPasses"/>），
        ///      URP 因此不再建 MV、也不再拷深度（M3）。
        /// </summary>
        public static void SetSubmissionEnabled(bool enabled)
        {
            s_submissionEnabled = enabled;
            if (enabled)
            {
                s_pendingReset = true; // 关→开：FFX 必须丢历史（recon R5）
                ApplyQualityOverrides(); // 再开：MSAA 重新降到 1（与关闭时的还原配对）
            }
            else
            {
                RestoreQualityOverrides();
            }
        }

        /// <summary>
        /// 装 URP 渲染通道（幂等）。失败返回 false + 原因；**不动磁盘资产**。
        /// 只在 Player 里装：Editor 的 dxgi 代理不生效，装了也拿不到 FFX。
        /// </summary>
        public static bool TryInstallPipeline(out string error)
        {
            if (s_installedAsset != null)
            {
                error = string.Empty;
                return true;
            }

            if (s_installFailed)
            {
                error = s_installError;
                return false;
            }

            if (Application.isEditor)
            {
                s_installFailed = true;
                s_installError = "Editor 内不装配（代理不生效）";
                error = s_installError;
                return false;
            }

            if (GraphicsSettings.currentRenderPipeline is not UniversalRenderPipelineAsset source)
            {
                s_installFailed = true;
                s_installError = "当前管线不是 URP（拿不到 depth/MV）";
                error = s_installError;
                return false;
            }

            try
            {
                s_installedAsset = Install(source);
                error = string.Empty;
                return true;
            }
            catch (Exception e)
            {
                s_installFailed = true;
                s_installError = e.GetType().Name + ": " + e.Message;
                error = s_installError;
                return false;
            }
        }

        /// <summary>每帧（URP pass 的 AfterRenderingTransparents）取 depth/MV 并提交。</summary>
        internal static void OnCameraRendered(Camera camera)
        {
            if (!s_submissionEnabled || camera == null || camera.cameraType != CameraType.Game)
            {
                return;
            }

            // URP 的 MotionVectorRenderPass 只在相机带 UniversalAdditionalCameraData 时
            // 才真的画 MV（Passes/MotionVectorRenderPass.cs: `TryGetComponent` 失败即 return，
            // 纹理停留在清零状态）。这一步同时是"深度可用"的前提（requiresDepthOption）。
            // 只在相机变化时做一次（GetUniversalAdditionalCameraData 会按需添加组件）。
            // TASK-R10-06（A-9）：换相机的语义交给 ZrFrameGenerationQualityOverrides.BindCamera ——
            // 它会**先还原上一台**再记新相机的原值并置 On。关插帧时 Restore 还原**全表**，
            // 所以"过场相机→玩家相机 / 相机销毁重建 / 叠加相机"都不会留下永久停在 On 的相机。
            if (camera != s_preparedCamera)
            {
                UniversalAdditionalCameraData additional = camera.GetUniversalAdditionalCameraData();
                if (additional != null)
                {
                    // 记下开之前的枚举原值并置 On（M2）：关的时候配对还原，别把"强制深度纹理"
                    // 留成常驻开销（N1：存枚举，不存 bool 投影）。
                    s_quality.BindCamera(new UrpFrameGenerationCameraDepthTarget(additional));
                }

                s_preparedCamera = camera;
            }

            Texture depth = Shader.GetGlobalTexture("_CameraDepthTexture");
            Texture motion = Shader.GetGlobalTexture("_MotionVectorTexture");
            if (depth == null || motion == null)
            {
                Note("全局纹理缺失 depth=" + (depth == null ? "null" : "ok")
                    + " mv=" + (motion == null ? "null" : "ok"));
                return;
            }

            int width = motion.width;
            int height = motion.height;
            if (width <= 0 || height <= 0 || depth.width != width || depth.height != height)
            {
                NoteOnce(ref s_warnedGeometry,
                    "depth/MV 尺寸不一致或为零（depth=" + depth.width + "x" + depth.height
                    + " mv=" + width + "x" + height + "）—— 本帧不提交");
                return;
            }

            IntPtr depthPtr = depth.GetNativeTexturePtr();
            IntPtr motionPtr = motion.GetNativeTexturePtr();
            if (depthPtr == IntPtr.Zero || motionPtr == IntPtr.Zero)
            {
                NoteOnce(ref s_warnedGeometry, "depth/MV 原生句柄为空 —— 本帧不提交（非 D3D12 时属预期）");
                return;
            }

            int frame = Time.frameCount;
            bool continuous = frame == s_lastSubmitFrame || frame == s_lastSubmitFrame + 1;
            int reset = (s_pendingReset || !continuous) ? 1 : 0;

            Transform tr = camera.transform;
            Vector3 pos = tr.position;
            Vector3 up = tr.up;
            Vector3 right = tr.right;
            Vector3 forward = tr.forward;
            float frameTimeMs = Mathf.Max(0.001f, Time.unscaledDeltaTime * 1000f);

            var inputs = new ZfgFgFrameInputsNative
            {
                StructSize = (uint)ZrFgProxyNative.InputsSize,
                Version = ZrFgProxyNative.InputsVersion,
                Depth = depthPtr,
                MotionVectors = motionPtr,
                Width = (uint)width,
                Height = (uint)height,
                DepthState = 0u, // 0 = 原生取默认（PIXEL|COMPUTE 可读，Unity 采样后的状态）
                MvState = 0u,
                JitterX = 0f, // URP 的 MV 用非 jitter 矩阵算，jitter 恒 0（B-R9-02）
                JitterY = 0f,
                // B-R9-02 方案 B：Unity MV 原样当"半个 NDC"，scale 取 (W, -H)
                MvScaleX = width,
                MvScaleY = -height,
                FrameTimeMs = frameTimeMs,
                FovY = camera.fieldOfView * Mathf.Deg2Rad,
                NearZ = camera.nearClipPlane,
                FarZ = camera.farClipPlane,
                CamPosX = pos.x,
                CamPosY = pos.y,
                CamPosZ = pos.z,
                CamUpX = up.x,
                CamUpY = up.y,
                CamUpZ = up.z,
                CamRightX = right.x,
                CamRightY = right.y,
                CamRightZ = right.z,
                CamForwardX = forward.x,
                CamForwardY = forward.y,
                CamForwardZ = forward.z,
                Reset = reset,
                Reserved = 0,
            };

            s_lastSubmitFrame = frame;
            if (reset != 0)
            {
                s_pendingReset = false;
            }

            SubmitCalls++;
            if (!Api.Submit(ref inputs))
            {
                SubmitRejected++;
                NoteOnce(ref s_warnedRejected, "ZFG_FgSubmitInputs 被拒（代理不在 / 参数非法）");
                return;
            }

            LastNote = string.Empty;
            if ((SubmitCalls % 60) == 0 && Api.TryGetStats(out ZfgFgStatsNative native))
            {
                LastStats = new FrameGenerationStatsInfo
                {
                    LinkReady = true,
                    HaveInputs = native.HaveInputs != 0u,
                    Enabled = native.Enabled != 0u,
                    RealPresents = native.RealPresents,
                    GeneratedPresents = native.GeneratedPresents,
                    PrepareCount = native.PrepareCount,
                    PrepareFailed = native.PrepareFailed,
                    LastReturnCode = native.LastReturnCode,
                };
            }
        }

        private static UniversalRenderPipelineAsset Install(UniversalRenderPipelineAsset source)
        {
            var type = typeof(UniversalRenderPipelineAsset);
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
            FieldInfo listField = type.GetField("m_RendererDataList", Flags);
            FieldInfo indexField = type.GetField("m_DefaultRendererIndex", Flags);
            if (listField == null || indexField == null)
            {
                throw new InvalidOperationException("URP 资产字段改名（m_RendererDataList/m_DefaultRendererIndex）");
            }

            var sourceList = (ScriptableRendererData[])listField.GetValue(source);
            int index = (int)indexField.GetValue(source);
            if (sourceList == null || index < 0 || index >= sourceList.Length || sourceList[index] == null)
            {
                throw new InvalidOperationException("URP 渲染器数据缺失（m_DefaultRendererIndex 越界）");
            }

            // 内存副本：磁盘资产不参与、不 SaveAssets（B-R9-02 的做法）。
            var clone = UnityEngine.Object.Instantiate(source);
            clone.name = source.name + "__ZFrameFG";
            var rendererClone = UnityEngine.Object.Instantiate(sourceList[index]);
            rendererClone.name = sourceList[index].name + "__ZFrameFG";
            rendererClone.rendererFeatures.Add(ScriptableObject.CreateInstance<ZrFrameGenerationFeature>());
            rendererClone.SetDirty();

            var newList = (ScriptableRendererData[])sourceList.Clone();
            newList[index] = rendererClone;
            listField.SetValue(clone, newList);

            // 两条硬前提（都是运行期字段，不是资产改动）：
            //  - 深度：URP 的 _CameraDepthTexture 只有在 supportsCameraDepthTexture 时存在；
            //  - MSAA=1：FFX PrepareV2 的 depth 必须是单采样资源，且 MSAA=1 才会走
            //    "R32F 彩色深度拷贝"这条已被证明可用的路径（B-R9-02 Q2）。MSAA>1 时
            //    URP 把 MSAA 的 depth-stencil 附件当深度纹理，FFX 不可用。
            // 两条都会被覆盖 —— 覆盖前先把原值记下来（M2：关 FG 时按这对值还原）。
            s_quality.BindPipeline(new UrpFrameGenerationPipelineTarget(clone));

            QualitySettings.renderPipeline = clone;
            Debug.Log("[插帧] URP 通道已装配：clone=" + clone.name + " renderer=" + rendererClone.name
                + " features=" + rendererClone.rendererFeatures.Count + " msaa=" + clone.msaaSampleCount);
            return clone;
        }

        /// <summary>
        /// 把 FG 需要的画质字段覆盖到管线副本上（MSAA=1 + 要深度纹理）。
        /// 幂等：已覆盖则重复写同样的值（不会把"原值"覆盖掉，原值在绑定时已记下）。
        /// </summary>
        private static void ApplyQualityOverrides()
        {
            // 没绑过管线目标（= 通道没装 / 开机 PrimeDisabled）时无事可做。
            if (!s_quality.PipelineRecorded)
            {
                return;
            }

            WarnMsaaCostOnce(s_quality.OriginalMsaaSampleCount);
            s_quality.Apply();
        }

        /// <summary>MSAA 代价只提示一次（开的时候说清楚关掉就会还原）。</summary>
        private static void WarnMsaaCostOnce(int originalMsaa)
        {
            if (s_warnedMsaa || originalMsaa <= ZrFrameGenerationQualityOverrides.MsaaSampleCountForFrameGeneration)
            {
                return;
            }

            s_warnedMsaa = true;
            Debug.LogWarning("[插帧] 管线 MSAA " + originalMsaa
                + " → 1（FFX 的 depth 必须单采样；关闭插帧即还原 " + originalMsaa + "，无需重启）");
        }

        /// <summary>
        /// 关闭 FG 时把画质字段还原成开之前的原值（M2）。还原的是**运行期管线副本**上的
        /// 字段：URP 每帧按 <c>asset.msaaSampleCount</c> 建相机描述符（UniversalRenderPipeline.cs
        /// 的 InitializeStackedCameraData），所以下一帧就生效 —— 不需要重启、不需要重建管线。
        /// 同时把相机的深度开关还原成**枚举原值**（N1：旧实现写回 bool，丢了 UsePipelineSettings）。
        /// <para>
        /// TASK-R10-06（A-9）：这里复用同一个 <see cref="ZrFrameGenerationQualityOverrides.Restore"/>，
        /// 而它现在还原**全表**（所有被改写过的相机），不再只还原最后绑定的那一台 ——
        /// "关掉插帧也救不回来、必须重启"的那半条缺陷随之消失。
        /// </para>
        /// </summary>
        private static void RestoreQualityOverrides()
        {
            s_preparedCamera = null;
            if (!s_quality.Restore())
            {
                return;
            }

            Debug.Log("[插帧] 关闭插帧：MSAA 还原为 " + s_quality.OriginalMsaaSampleCount
                + "、相机深度开关还原为 " + s_quality.OriginalRequiresDepthOption + "（无需重启）");
        }

        /// <summary>
        /// **仅测试用**（TASK-B-R9-08 N2）：把覆盖/还原的目标换成桩，让 EditMode 能通过
        /// 生产入口 <see cref="SetSubmissionEnabled"/> 断言"开→覆盖、关→还原"真的发生。
        /// 生产路径从不调用（真实目标由 <see cref="Install"/> 与 <see cref="OnCameraRendered"/> 绑定）。
        /// 传 null 清空。
        /// <para>
        /// TASK-B-R10-01：从 <c>public</c> 收为 <c>internal</c> —— 产品程序集的公开面不该被测试撑大。
        /// 测试程序集通过 <c>Runtime/AssemblyInfo.cs</c> 的 <c>InternalsVisibleTo("ZFrame.Render.Tests")</c> 访问。
        /// </para>
        /// </summary>
        internal static void BindQualityTargetsForTests(
            IZrFrameGenerationPipelineTarget pipeline,
            IZrFrameGenerationCameraDepthTarget camera)
        {
            s_quality.Reset();
            if (pipeline != null)
            {
                s_quality.BindPipeline(pipeline);
            }

            if (camera != null)
            {
                s_quality.BindCamera(camera);
            }
        }

        /// <summary>pass 入队计数（M3 的"关就停"证据）。</summary>
        internal static void NotePassEnqueued()
        {
            PassEnqueues++;
        }

        /// <summary>pass 因"提交已关"而不入队的计数（M3 的"关就停"证据）。</summary>
        internal static void NotePassSkipped()
        {
            PassSkips++;
        }

        private static void Note(string note)
        {
            LastNote = note;
        }

        private static void NoteOnce(ref bool flag, string note)
        {
            LastNote = note;
            if (flag)
            {
                return;
            }

            flag = true;
            Debug.LogWarning("[插帧] " + note);
        }
    }

    /// <summary>
    /// 只做"声明输入 + 转交提交"的 URP 特性。由
    /// <see cref="ZrFrameGenerationRuntime.TryInstallPipeline"/> 在运行期挂到资产副本上。
    /// </summary>
    public sealed class ZrFrameGenerationFeature : ScriptableRendererFeature
    {
        private sealed class SubmitPass : ScriptableRenderPass
        {
            public SubmitPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
                // 唯一作用：让 URP 建 MV（R16G16_SFloat）与深度纹理。
                // 只在"提交已开"时才会被入队（见 AddRenderPasses）——关的时候 URP 看不到这条
                // 输入声明，也就不会建这两张纹理（M3）。
                ConfigureInput(ScriptableRenderPassInput.Motion | ScriptableRenderPassInput.Depth);
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                ZrFrameGenerationRuntime.OnCameraRendered(renderingData.cameraData.camera);
            }
        }

        private SubmitPass _pass;

        /// <inheritdoc />
        public override void Create()
        {
            _pass = new SubmitPass();
        }

        /// <inheritdoc />
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_pass == null)
            {
                return;
            }

            // M3（TASK-B-R9-07）：关 = **真不采**。URP 的输入需求是逐帧从"已入队的 pass"
            // 里收集的（UniversalRenderer.GetRenderPassInputs 遍历 activeRenderPassQueue），
            // 所以不入队就等于：不建 _MotionVectorTexture、不为本 pass 拷深度、Execute 不被调、
            // ZFG_FgSubmitInputs 一次都不发 —— 比"入队后再 early-return"少付全部采集成本。
            if (!ZrFrameGenerationRuntime.SubmissionEnabled)
            {
                ZrFrameGenerationRuntime.NotePassSkipped();
                return;
            }

            ZrFrameGenerationRuntime.NotePassEnqueued();
            renderer.EnqueuePass(_pass);
        }
    }
}
