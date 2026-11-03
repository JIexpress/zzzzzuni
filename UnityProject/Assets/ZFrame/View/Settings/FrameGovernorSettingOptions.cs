// FrameGovernorSettingOptions.cs —— 视频页「动态分辨率」「帧率目标」两行的单一数据源
// （TASK-A-R9-01）：选项顺序、Enum 索引↔目标帧率的唯一映射、管线能力标注与 apply 入口。
// 设计约束：本层只碰 FrameGovernorHost 的公开 API（SetEnabled / SetTargetFps），
// 不碰 FrameGovernor 的核心算法（A-R8-01 已验收）；宿主未自举（boot 前 / EditMode /
// 已 Shutdown）时只告警不抛——值仍由 SettingsModel 持久化，开机应用点会再次送达。

using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>
    /// "动态分辨率"（<see cref="DrsSettingId"/>）与"帧率目标"
    /// （<see cref="TargetFpsSettingId"/>）两行的数据与行为：
    /// - 动态分辨率 = <see cref="FrameGovernorHost.SetEnabled"/>（DRS 使能）；
    ///   降分辨率措施只对 URP 的 <c>renderScale</c> 生效，非 URP 管线时行标签
    ///   追加 <see cref="DrsNonUrpSuffix"/>，如实说明只剩降档/降后处理；
    /// - 帧率目标 = <see cref="FrameGovernorHost.SetTargetFps"/>（TargetMs = 1000/fps），
    ///   选项 30 / 60 / 120，索引即 settings.json 持久值；
    /// - 不提供"不限"选项：治理预算没有"无限"语义（TargetMs ≤ 0 即整条旁路），
    ///   而"不治理 = 不限"正是 动态分辨率=关 的默认态，另立一项只会造出
    ///   "动态分辨率=开 + 帧率目标=不限 = 永远不动作"的死组合。实际帧率上限仍由
    ///   既有的 video.frameRateCap 行负责（Application.targetFrameRate）。
    /// </summary>
    public static class FrameGovernorSettingOptions
    {
        /// <summary>动态分辨率开关的持久键（Toggle，默认 false = 与 governor 默认关一致）。</summary>
        public const string DrsSettingId = "video.dynamicResolution";

        /// <summary>帧率目标的持久键（Enum，索引进 <see cref="BuildTargetFpsLabels"/>）。</summary>
        public const string TargetFpsSettingId = "video.frameRateTarget";

        /// <summary>选项索引：30fps。</summary>
        public const int TargetFpsOption30 = 0;

        /// <summary>选项索引：60fps（默认，等于 FrameGovernorHost.DefaultTargetFps）。</summary>
        public const int TargetFpsOption60 = 1;

        /// <summary>选项索引：120fps。</summary>
        public const int TargetFpsOption120 = 2;

        /// <summary>选项总数。</summary>
        public const int TargetFpsOptionCount = 3;

        /// <summary>出厂默认：60fps（<see cref="FrameGovernorHost.DefaultTargetFps"/>，零破坏）。</summary>
        public const int DefaultTargetFpsOptionIndex = TargetFpsOption60;

        /// <summary>动态分辨率的显示名（非 URP 管线时追加后缀）。</summary>
        public const string DrsDisplayName = "动态分辨率";

        /// <summary>帧率目标的显示名。</summary>
        public const string TargetFpsDisplayName = "帧率目标";

        /// <summary>
        /// 帧率目标的标签后缀（主控裁决 2 的硬要求，照超分行的"标签后缀"风格，不新造灰显机制）：
        /// 同时说明 ①它只在开启动态分辨率（DRS）后驱动任何措施 ②它不是锁帧，
        /// 与既有的"帧率上限"（<c>video.frameRateCap</c> → Application.targetFrameRate）划清界限。
        /// 长度受 <see cref="SettingsRowFactory.LabelWidth"/> 约束（超长会被截断，诚实的后缀反而看不见），由
        /// <c>FrameGovernorSettingsTests.GovernorRowLabels_FitTheLabelColumn_WithoutTruncation</c> 守住。
        /// </summary>
        public const string TargetFpsEffectSuffix = "（DRS不锁帧）";

        /// <summary>
        /// 非 URP 管线下的如实标注：<c>renderScale</c> 措施没有落点，治理只剩"降档/降后处理"。
        /// 受 <see cref="SettingsRowFactory.LabelWidth"/> 约束，文案压到最短（"仅降档"即"不降分辨率"的正向说法），
        /// 宽度由 <c>FrameGovernorSettingsTests.GovernorRowLabels_FitTheLabelColumn_WithoutTruncation</c> 一起守住。
        /// </summary>
        public const string DrsNonUrpSuffix = "（仅降档）";

        private static readonly string[] TargetFpsLabels = { "30", "60", "120" };

        private static readonly float[] TargetFpsByOption = { 30f, 60f, 120f };

        /// <summary>帧率目标行的完整标签（显示名 + 生效条件后缀）；漂移检查见类文档。</summary>
        public static string BuildTargetFpsLabel()
        {
            return TargetFpsDisplayName + TargetFpsEffectSuffix;
        }

        /// <summary>下拉框选项文案（索引 == 持久值）。每次返回副本，调用方改不脏共享数组。</summary>
        public static string[] BuildTargetFpsLabels()
        {
            return (string[])TargetFpsLabels.Clone();
        }

        /// <summary>Enum 索引 → 目标帧率。越界/未知回退 60fps（默认值），永不返回非正值。</summary>
        public static float TargetFpsFromOption(int optionIndex)
        {
            return optionIndex >= 0 && optionIndex < TargetFpsByOption.Length
                ? TargetFpsByOption[optionIndex]
                : FrameGovernorHost.DefaultTargetFps;
        }

        /// <summary>
        /// 动态分辨率行的显示名：降分辨率措施只在 URP 上真正生效
        /// （<see cref="FrameGovernorHost"/> 写的是
        /// <c>UniversalRenderPipelineAsset.renderScale</c>），其余管线如实标注。
        /// </summary>
        public static string BuildDrsLabel(bool renderScaleSupported)
        {
            return renderScaleSupported ? DrsDisplayName : DrsDisplayName + DrsNonUrpSuffix;
        }

        /// <summary>按当前活动管线决定动态分辨率行的显示名（注册表构建时调用）。</summary>
        public static string BuildDrsLabel()
        {
            return BuildDrsLabel(RenderScaleSupported());
        }

        /// <summary>当前活动管线是否是 URP —— 即 governor 的 RenderScale 措施能否落地。</summary>
        public static bool RenderScaleSupported()
        {
            return GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;
        }

        /// <summary>
        /// video.dynamicResolution 行的 apply：真调 <see cref="FrameGovernorHost.SetEnabled"/>。
        /// 宿主缺席（boot 前 / EditMode / 已 Shutdown）只告警不抛，值仍持久化。
        /// </summary>
        public static void ApplyDrs(bool enabled)
        {
            FrameGovernorHost host = FrameGovernorHost.Instance;
            if (host == null)
            {
                ZLog.Warn("Settings",
                    $"[动态分辨率] FrameGovernorHost 未自举，{(enabled ? "开启" : "关闭")}仅存储不生效（下次启动由开机应用点重放）");
                return;
            }

            host.SetEnabled(enabled);
        }

        /// <summary>
        /// video.frameRateTarget 行的 apply：索引 → fps → <see cref="FrameGovernorHost.SetTargetFps"/>
        /// （TargetMs = 1000/fps）。宿主缺席只告警不抛。
        /// </summary>
        public static void ApplyTargetFps(int optionIndex)
        {
            float fps = TargetFpsFromOption(optionIndex);
            FrameGovernorHost host = FrameGovernorHost.Instance;
            if (host == null)
            {
                ZLog.Warn("Settings",
                    $"[帧率目标] FrameGovernorHost 未自举，目标 {fps:0}fps 仅存储不生效（下次启动由开机应用点重放）");
                return;
            }

            host.SetTargetFps(fps);
        }
    }
}
