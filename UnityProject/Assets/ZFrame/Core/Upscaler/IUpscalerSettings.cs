using System;

namespace ZFrame.Core
{
    /// <summary>
    /// 超分设置服务（TASK-B-R6-03）：设置界面操作原生 UpscalerChain 偏好的唯一入口。
    /// 实现位于 ZFrame.Render（<c>ZrUpscalerSettings</c>，经 ZFrame.Render.Bindings 走
    /// zrender.dll 的 C ABI 三导出）；设置 UI 经 <c>Services</c> 定位器消费本接口
    /// （与 IAudioService 同一模式）。原生不可用（dll 缺失 / 符号缺失 / ABI 不符）时
    /// 实现必须优雅降级：查询返回不可用、设置返回 false，不得抛异常炸设置 UI。
    /// </summary>
    public interface IUpscalerSettings
    {
        /// <summary>原生超分能力是否可用（zrender.dll 可加载且 C ABI 三导出可调用）。</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// 查询本机能力/状态快照；不可用时返回 <see cref="UpscalerCapsInfo.Available"/> 为
        /// false 的默认值（不抛异常）。
        /// </summary>
        UpscalerCapsInfo QueryCaps();

        /// <summary>
        /// 设置首选后端（原生 ZrUpscalerPreferred 编号：0=Auto、1=Bilinear、3=FSR3、
        /// 4=FSR4、5=DLSS）。越界/未知值与原生不可用一律返回 false（不抛异常）。
        /// Auto 语义由原生 UpscalerChain 按能力矩阵选优，本层只透传。
        /// </summary>
        bool TrySetPreferred(int preferred);

        /// <summary>取当前解析后的生效后端（1..5）；不可用或值越界返回 false。</summary>
        bool TryGetActiveBackend(out int activeBackend);
    }

    /// <summary>托管侧能力/状态快照（与原生 ZrUpscalerCaps 的语义字段逐项对应；逐字段拷贝，不按布局封送）。</summary>
    public struct UpscalerCapsInfo
    {
        /// <summary>原生能力查询是否成功（false = 其余字段无意义）。</summary>
        public bool Available;

        /// <summary>PCI vendor id（0x1002=AMD、0x10DE=NVIDIA…；探测失败为 0）。</summary>
        public uint VendorId;

        /// <summary>PCI device id（探测失败为 0）。</summary>
        public uint DeviceId;

        /// <summary>FFX 运行时 dll 可加载且导出齐全（FSR 3.x 的前置）。</summary>
        public bool FsrRuntimePresent;

        /// <summary>
        /// provider 枚举命中 4.x（FSR 4.1 的前置；核显通常为 false）。
        /// 结论可信度须结合 <see cref="Fsr4ProbeMode"/> 判读——回退口径下可能失真。
        /// </summary>
        public bool Fsr4Available;

        /// <summary>Streamline 运行时 + NVIDIA 硬件齐备（DLSS 5 的前置）。</summary>
        public bool DlssAvailable;

        /// <summary>用户偏好（原生 ZrUpscalerPreferred：0=Auto）。</summary>
        public int PreferredBackend;

        /// <summary>解析后的生效后端（1..5；Auto 时按能力选优）。</summary>
        public int ActiveBackend;

        /// <summary>
        /// Fsr4Available 的探测口径三态（B-FSR-02，镜像原生 ZrUpscalerCaps.fsr4ProbeMode）：
        /// 0 = 真实设备探测（provider 枚举拿到真 ID3D12Device*，结论可信）；
        /// 1 = 保守回退（建备失败走 device=null 枚举——FFX 的 null 枚举返回截断列表，
        ///     结果可能失真，UI 必须标"未检测"）；
        /// 2 = 未探测（provider 枚举未执行——典型：FFX 运行时缺席；此时
        ///     Fsr4Available 恒 false，不代表硬件不支持）。
        /// 未知值（未来版本扩展）一律按"未检测"处理，不得当作 0。
        /// </summary>
        public uint Fsr4ProbeMode;
    }
}
