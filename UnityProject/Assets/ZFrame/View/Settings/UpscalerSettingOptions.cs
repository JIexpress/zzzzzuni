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
    ///   给厂商项标注真实状态（B-FSR-02 三态诚实化）：FSR4 行按 运行时缺失 /
    ///   未检测（探测回退）/ 本机不支持 / 可用 四种真实结论分别陈述；服务缺席
    ///   （根本没测）一律标"未检测（原生内核未启用）"，不许无来源地宣称"不支持"；
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

        /// <summary>FSR4 状态：真设备探测口径（probeMode=0）且运行时在位且 4.x 未枚举
        /// ——唯一允许出现"不支持"的路径（结论源自可信探测）。</summary>
        public const string Fsr4UnsupportedText = "本机不支持";

        // ---- FSR4 三态文案（TASK-B-FSR-02）：状态文本全部源自 QueryCaps 真实输出，
        // "本机不支持"只在真设备探测口径（probeMode=0）且运行时在位且 4.x 未枚举时
        // 出现——"没测过/测法失真"一律标"未检测"，不许伪装成"不支持"。 ----

        /// <summary>FSR4 状态：4.x provider 命中。C ABI v2 暂无 provider 版本串导出，
        /// 如实标"可用"（版本读口属后续任务，不许编造版本号）。</summary>
        public const string Fsr4AvailableText = "可用";

        /// <summary>FSR4 状态：FFX 运行时 dll 缺失（provider 枚举根本无法执行）。</summary>
        public const string Fsr4RuntimeMissingText = "运行时缺失（FFX DLL 未找到）";

        /// <summary>FSR4 状态：探测回退/未探测口径（probeMode!=0，含未知值兜底）——
        /// FFX 的 device=null 枚举返回截断列表，结论可能失真。</summary>
        public const string Fsr4UnprobedText = "未检测（探测回退，结果不可信）";

        /// <summary>原生内核未启用（服务缺席/查询失败）——"根本没测"，与
        /// "测了但不支持"必须分开陈述（铁律：不许无来源的不支持）。</summary>
        public const string UnprobedNoNativeText = "未检测（原生内核未启用）";

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
        /// 能力门控标注：FSR 3.1 看 FsrRuntimePresent、DLSS 5 看 DlssAvailable；
        /// FSR 4.1 走 <see cref="BuildFsr4StatusText"/> 三态映射（B-FSR-02）。
        /// 原生内核未启用（服务为 null 或 Available=false）= 根本没测 → 三个厂商项
        /// 一律标"未检测（原生内核未启用）"，不许无来源地宣称"本机不支持"。
        /// </summary>
        public static string[] BuildOptionLabels(IUpscalerSettings upscaler)
        {
            UpscalerCapsInfo caps = upscaler != null ? upscaler.QueryCaps() : default;
            return new[]
            {
                BaseOptionNames[OptionAuto],
                BaseOptionNames[OptionOff],
                MarkIfUnsupported(BaseOptionNames[OptionFsr3],
                    caps.Available && caps.FsrRuntimePresent, caps.Available),
                BuildFsr4Label(caps),
                MarkIfUnsupported(BaseOptionNames[OptionDlss],
                    caps.Available && caps.DlssAvailable, caps.Available),
            };
        }

        /// <summary>
        /// FSR4 行状态文本（三态 + 兜底，TASK-B-FSR-02）。判定序即优先序：
        /// 1. 原生内核未启用（<see cref="UpscalerCapsInfo.Available"/>=false）→
        ///    <see cref="UnprobedNoNativeText"/>（根本没测）；
        /// 2. Fsr4Available=1 → <see cref="Fsr4AvailableText"/>（版本串读口暂缺，如实标"可用"）；
        /// 3. FsrRuntimePresent=0 → <see cref="Fsr4RuntimeMissingText"/>（运行时缺失是
        ///    更具体的真实原因，优先于探测口径陈述）；
        /// 4. Fsr4ProbeMode!=0（含未知值兜底——未来版本扩展的口径值一律不信）→
        ///    <see cref="Fsr4UnprobedText"/>（结果不可信，不得当"不支持"）；
        /// 5. 其余（真设备探测口径 + 运行时在位 + 4.x 未枚举）→「本机不支持」
        ///    （唯一允许出现"不支持"的路径：结论源自可信探测）。
        /// </summary>
        public static string BuildFsr4StatusText(UpscalerCapsInfo caps)
        {
            if (!caps.Available)
            {
                return UnprobedNoNativeText;
            }

            if (caps.Fsr4Available)
            {
                return Fsr4AvailableText;
            }

            if (!caps.FsrRuntimePresent)
            {
                return Fsr4RuntimeMissingText;
            }

            if (caps.Fsr4ProbeMode != 0u)
            {
                return Fsr4UnprobedText;
            }

            return Fsr4UnsupportedText;
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

        /// <summary>
        /// FSR4 行标签：状态文本来自 <see cref="BuildFsr4StatusText"/>；"本机不支持"
        /// 沿用既有 <see cref="UnsupportedSuffix"/> 格式，"可用"用（可用）后缀，
        /// 其余（含原因明细的）状态用"名称：状态"格式避免双括号嵌套。
        /// </summary>
        private static string BuildFsr4Label(UpscalerCapsInfo caps)
        {
            string status = BuildFsr4StatusText(caps);
            if (status == Fsr4UnsupportedText)
            {
                return BaseOptionNames[OptionFsr4] + UnsupportedSuffix;
            }

            if (status == Fsr4AvailableText)
            {
                return BaseOptionNames[OptionFsr4] + "（" + Fsr4AvailableText + "）";
            }

            return BaseOptionNames[OptionFsr4] + "：" + status;
        }

        /// <summary>
        /// FSR3/DLSS 行标注：<paramref name="probed"/>=false（原生内核未启用）→
        /// "未检测（原生内核未启用）"；支持 → 原名；否则 → "（本机不支持）"
        /// （此时结论源自真实探测的门控位，有来源）。
        /// </summary>
        private static string MarkIfUnsupported(string name, bool supported, bool probed)
        {
            if (!probed)
            {
                return name + "：" + UnprobedNoNativeText;
            }

            return supported ? name : name + UnsupportedSuffix;
        }
    }
}
