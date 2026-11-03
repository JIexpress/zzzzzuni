// UpscalerSettingOptions.cs —— 视频页"超分"行（video.upscaler）的单一数据源（TASK-B-R6-03）：
// 选项顺序、Enum 索引↔原生偏好的唯一映射、QueryCaps 能力门控标注与 apply 入口。
// 设计约束：View 层不得引用 ZFrame.Render.Bindings（设计方案 §3.2 asmdef 矩阵），
// 故原生访问一律经 ZFrame.Core 的 IUpscalerSettings 服务（Services 定位器，同 IAudioService 模式）。

using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>
    /// "超分"行的选项数据与行为：
    /// - 选项顺序 = 自动 / 关 / FSR 3.1 / FSR 4.1 / DLSS 5，索引即 settings.json 持久值；
    /// - <see cref="OptionIndexToPreferred"/> 把索引映射到原生 ZrUpscalerPreferred
    ///   （0=Auto、1=Bilinear、3=FSR3、4=FSR4、5=DLSS；越界/未知一律回退 Auto——
    ///   自动即本设置默认值，由原生 Chain 按能力选优）；
    /// - <see cref="BuildOptionLabels"/> 按 <see cref="IUpscalerSettings.QueryCaps"/>
    ///   给不支持的厂商项追加"（本机不支持）"——能力未知（服务未注册或原生不可用）
    ///   时三个厂商项同样标注，不许让用户选一个必然失败的项还无提示；
    /// - <see cref="ApplyOptionIndex"/> 是该行 apply 的唯一入口：服务在 → 真调
    ///   TrySetPreferred；服务不在或拒绝 → ZLog 警告（值仍持久化），永不抛异常。
    /// </summary>
    public static class UpscalerSettingOptions
    {
        /// <summary>选项索引：自动（原生 Chain 按能力矩阵选优）。</summary>
        public const int OptionAuto = 0;

        /// <summary>选项索引：关（原生语义 = Bilinear 兜底，不过超分）。</summary>
        public const int OptionOff = 1;

        /// <summary>选项索引：FSR 3.1。</summary>
        public const int OptionFsr3 = 2;

        /// <summary>选项索引：FSR 4.1。</summary>
        public const int OptionFsr4 = 3;

        /// <summary>选项索引：DLSS 5。</summary>
        public const int OptionDlss = 4;

        /// <summary>选项总数。</summary>
        public const int OptionCount = 5;

        /// <summary>出厂默认：自动。</summary>
        public const int DefaultOptionIndex = OptionAuto;

        /// <summary>本机不支持的选项在 UI 上的统一标注后缀。</summary>
        public const string UnsupportedSuffix = "（本机不支持）";

        private static readonly string[] BaseOptionNames = { "自动", "关", "FSR 3.1", "FSR 4.1", "DLSS 5" };

        /// <summary>
        /// Enum 索引 → 原生 ZrUpscalerPreferred。越界/未知值回退 Auto（0）；
        /// "关"映射 Bilinear（1）——原生没有独立"关"，Bilinear 即不过超分的兜底后端。
        /// </summary>
        public static int OptionIndexToPreferred(int optionIndex)
        {
            return optionIndex switch
            {
                OptionOff => 1,  // Bilinear
                OptionFsr3 => 3, // FSR3
                OptionFsr4 => 4, // FSR4
                OptionDlss => 5, // DLSS
                _ => 0,          // 自动 + 越界/未知 → Auto（原生 Chain 选优）
            };
        }

        /// <summary>
        /// 能力门控标注（经 Services 解析服务的便捷重载；无注册 = 能力未知）。
        /// </summary>
        public static string[] BuildOptionLabels()
        {
            return BuildOptionLabels(
                Services.TryGet(out IUpscalerSettings upscaler) ? upscaler : null);
        }

        /// <summary>
        /// 能力门控标注：FSR 3.1 看 FsrRuntimePresent、FSR 4.1 看 Fsr4Available、
        /// DLSS 5 看 DlssAvailable；服务为 null 或能力不可用（Available=false）时
        /// 三个厂商项视为不支持，全部标注。
        /// </summary>
        public static string[] BuildOptionLabels(IUpscalerSettings upscaler)
        {
            UpscalerCapsInfo caps = upscaler != null ? upscaler.QueryCaps() : default;
            return new[]
            {
                BaseOptionNames[OptionAuto],
                BaseOptionNames[OptionOff],
                MarkIfUnsupported(BaseOptionNames[OptionFsr3], caps.Available && caps.FsrRuntimePresent),
                MarkIfUnsupported(BaseOptionNames[OptionFsr4], caps.Available && caps.Fsr4Available),
                MarkIfUnsupported(BaseOptionNames[OptionDlss], caps.Available && caps.DlssAvailable),
            };
        }

        /// <summary>
        /// video.upscaler 行的 apply：映射后真调原生 TrySetPreferred（即
        /// ZR_Upscaler_SetPreferred）。服务缺席/原生不可用/越界只告警不抛——
        /// 值仍由 SettingsModel 持久化，下次会话带能力重试。
        /// </summary>
        public static void ApplyOptionIndex(int optionIndex)
        {
            int preferred = OptionIndexToPreferred(optionIndex);
            if (!Services.TryGet(out IUpscalerSettings upscaler))
            {
                ZLog.Warn("Settings", $"[超分] IUpscalerSettings 未注册（zrender 原生内核未启用），选择 {optionIndex} 仅存储不生效");
                return;
            }

            if (!upscaler.TrySetPreferred(preferred))
            {
                ZLog.Warn("Settings", $"[超分] 偏好未生效（optionIndex={optionIndex} → preferred={preferred}；原生不可用或越界）");
            }
        }

        private static string MarkIfUnsupported(string name, bool supported)
        {
            return supported ? name : name + UnsupportedSuffix;
        }
    }
}
