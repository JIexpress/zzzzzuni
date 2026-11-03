// FrameGenerationSettingOptions.cs —— 视频页「插帧」行（video.frameGeneration）的
// 单一数据源（TASK-B-R9-06）：持久键、能力门控标签、apply 入口。
//
// 设计约束（照 A-R9-01 的两行 / B-R6-03 的超分行的既有作风）：
//   - View 层不得引用 ZFrame.Render.Bindings（架构矩阵），一切经 ZFrame.Core 的
//     IFrameGenerationSettings 服务（Services 定位器）；
//   - **不做能点但无效的开关**：四类不可用环境一律在标签上如实标注并禁用该行，
//     不允许玩家拨一个必然不生效的开关还被告知"已开启"；
//   - 标签长度受 SettingsRowFactory.LabelWidth（340px）约束，超长会被截断，
//     诚实的后缀反而看不见 —— 由 FrameGenerationSettingsTests 的守门用例覆盖。

using UnityEngine;
using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>
    /// "插帧"行的数据与行为：Toggle，默认关；开 = 请求插帧（原生每帧按
    /// Configure(frameGenerationEnabled=true) + PrepareV2 合成中间帧），关 = 纯透传。
    /// </summary>
    public static class FrameGenerationSettingOptions
    {
        /// <summary>持久键（Toggle，默认 false = 纯透传，零破坏）。</summary>
        public const string SettingId = "video.frameGeneration";

        /// <summary>显示名（不可用环境会追加上下文后缀）。</summary>
        public const string DisplayName = "插帧";

        /// <summary>Editor 内的如实标注：dxgi 代理不生效，点了也不会插帧。</summary>
        public const string EditorSuffix = "（仅打包后生效）";

        /// <summary>非 D3D12 的如实标注（原生侧已门控为纯透传）。</summary>
        public const string NotD3D12Suffix = "（需 D3D12）";

        /// <summary>代理缺失 / hook 未装的如实标注。</summary>
        public const string ProxySuffix = "（未装代理）";

        /// <summary>非 URP 管线的如实标注（MV 的唯一来源是 URP）。</summary>
        public const string PipelineSuffix = "（需 URP）";

        /// <summary>
        /// **可用时的代价标注（TASK-B-R9-07 M1）**：FFX 的 depth 必须单采样，装通道时会把
        /// 管线副本的 MSAA 压到 1×（可见的画质变化）。这句写在行标签上，玩家拨之前就知道代价；
        /// 关闭插帧会自动还原（见 M2），所以这里只讲"开启期间的状态"。
        /// 措辞是"降至 1×"而不是"降 2→1"：不同质量档的原值可能不同，讲结果不讲差值，
        /// 原值已经是 1× 时这句话也依然成立（没有额外损失）。
        /// </summary>
        public const string MsaaCostSuffix = "（抗锯齿降至 1×）";

        /// <summary>按当前能力生成行标签（注册表构建时调用一次）。</summary>
        public static string BuildLabel()
        {
            return BuildLabel(QueryCapability());
        }

        /// <summary>按给定能力档位生成行标签（纯函数，供单测逐档断言）。</summary>
        public static string BuildLabel(FrameGenerationCapability capability)
        {
            return DisplayName + SuffixFor(capability);
        }

        /// <summary>
        /// 能力档位 → 标签后缀。可用时**不是空串**：必须挂上"抗锯齿降至 1×"的代价标注（M1），
        /// 否则玩家只会看到"插帧"两个字，开了发现画面变糙却不知道为什么。不可用的四档各自点名
        /// 原因（此时开关被禁用、不会改画质，所以不再叠代价标注，免得把"不能开"和"开了会怎样"混成一句）。
        /// </summary>
        public static string SuffixFor(FrameGenerationCapability capability)
        {
            switch (capability)
            {
                case FrameGenerationCapability.Available:
                    return MsaaCostSuffix;
                case FrameGenerationCapability.EditorOnly:
                    return EditorSuffix;
                case FrameGenerationCapability.NotD3D12:
                    return NotD3D12Suffix;
                case FrameGenerationCapability.PipelineUnsupported:
                    return PipelineSuffix;
                case FrameGenerationCapability.ProxyUnavailable:
                    return ProxySuffix;
            }

            // TASK-B-R9-08 N3：这里**故意不写 default 兜底**。旧实现把任何未来新增的能力档
            // 都映射成"（未装代理）"，等于替新档撒了个谎（玩家看到的原因与真实原因无关）。
            // 新增档必须在上面显式加 case；漏了就在 Editor / 开发构建里当场断言，
            // Player 里退回代理文案（界面不崩，但用例 CapabilitySuffixes_AreDistinct_AndHonest
            // 会因为"两档文案相同"变红 —— 有牙，不是只靠这句断言）。
            Debug.Assert(false, "SuffixFor 缺少能力档的显式分支：" + capability);
            return ProxySuffix;
        }

        /// <summary>
        /// 该行是否可交互：只在"真的能插帧"的环境下可点。其余环境禁用（配合标签后缀），
        /// 免得玩家点一个必然失败的开关（UpscalerSettingOptions 的同一原则）。
        /// </summary>
        public static bool IsRowInteractable()
        {
            return QueryCapability() == FrameGenerationCapability.Available;
        }

        /// <summary>当前能力档位（服务缺席时按可判定的环境降级，绝不抛）。</summary>
        public static FrameGenerationCapability QueryCapability()
        {
            if (Services.TryGet(out IFrameGenerationSettings frameGeneration))
            {
                return frameGeneration.QueryCapability();
            }

            // 服务未注册（EditMode / 自研管线壳）：只能按"编辑器内"如实标注。
#if UNITY_EDITOR
            return FrameGenerationCapability.EditorOnly;
#else
            return FrameGenerationCapability.ProxyUnavailable;
#endif
        }

        /// <summary>
        /// video.frameGeneration 行的 apply：直连服务的 TrySetEnabled（内部落到
        /// ZFG_FgSetEnabled(0/1)）。服务缺席/拒绝只告警一次，值仍由 SettingsModel
        /// 持久化，下次开机由 SettingsBootApplier 重放。
        /// </summary>
        public static void ApplyEnabled(bool enabled)
        {
            if (!Services.TryGet(out IFrameGenerationSettings frameGeneration))
            {
                ZLog.Warn("Settings",
                    $"[插帧] IFrameGenerationSettings 未注册，{(enabled ? "开启" : "关闭")}仅存储不生效");
                return;
            }

            // 服务内部对不可用/被拒只告警一次，这里不重复刷屏。
            frameGeneration.TrySetEnabled(enabled);
        }
    }
}
