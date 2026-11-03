// IFrameGenerationSettings.cs —— 插帧（frame generation）设置服务接口（TASK-B-R9-06）。
//
// 放 Core 的原因与 IUpscalerSettings 相同：ZFrame.View 不引用 ZFrame.Render.Bindings
// （架构矩阵），设置页只能经 Services 定位器消费接口，实现留在 ZFrame.Render。
//
// 契约：实现必须**永不抛异常**。原生不可用的每一种形态（Editor 无代理、非 D3D12、
// dxgi.dll 不是代理、hook 未装、URP 管线缺席）都折算成
// <see cref="FrameGenerationCapability"/> 的一档 + 一行原因，供设置页在标签上
// 诚实标注并禁用该行 —— 不许让用户拨一个必然不生效的开关。

namespace ZFrame.Core
{
    /// <summary>插帧能力档位（门控矩阵的三档 + 两个细分原因）。</summary>
    public enum FrameGenerationCapability
    {
        /// <summary>可用：Player + D3D12 + URP + 代理链已建。</summary>
        Available = 0,

        /// <summary>编辑器内：dxgi 代理不生效（DllImport 落到系统 dxgi.dll）。</summary>
        EditorOnly = 1,

        /// <summary>非 D3D12：原生侧已门控为纯透传，插帧没有落点。</summary>
        NotD3D12 = 2,

        /// <summary>dxgi 代理缺失 / hook 未装 / 符号不可用（含"链接尚未建立"）。</summary>
        ProxyUnavailable = 3,

        /// <summary>当前渲染管线不是 URP：拿不到 depth/MV（URP 是 MV 的唯一来源）。</summary>
        PipelineUnsupported = 4,
    }

    /// <summary>一次统计读数（逐字段拷贝，不按原生布局封送）。</summary>
    public struct FrameGenerationStatsInfo
    {
        /// <summary>代理链是否已建立（原生 <c>ZFG_FgGetStats</c> 返回 1）。</summary>
        public bool LinkReady;

        /// <summary>是否至少收到过一次合法 depth/MV 提交（门禁判据之一）。</summary>
        public bool HaveInputs;

        /// <summary>上一帧是否真的在插帧。</summary>
        public bool Enabled;

        /// <summary>真帧计数。</summary>
        public ulong RealPresents;

        /// <summary>插帧计数（门禁判据之一）。</summary>
        public ulong GeneratedPresents;

        /// <summary>成功派发 PrepareV2 次数。</summary>
        public ulong PrepareCount;

        /// <summary>失败次数（达标要求 0）。</summary>
        public ulong PrepareFailed;

        /// <summary>最后一次 ffx 返回码（达标要求 0）。</summary>
        public uint LastReturnCode;

        /// <inheritdoc />
        public override string ToString()
        {
            return "link=" + LinkReady + " inputs=" + HaveInputs + " enabled=" + Enabled
                + " real=" + RealPresents + " generated=" + GeneratedPresents
                + " prepare=" + PrepareCount + " fail=" + PrepareFailed + " rc=" + LastReturnCode;
        }
    }

    /// <summary>插帧设置服务（设置页选"插帧"时的唯一入口）。</summary>
    public interface IFrameGenerationSettings
    {
        /// <summary>当前环境能不能真的插帧（= <see cref="QueryCapability"/> 为 Available）。</summary>
        bool IsAvailable { get; }

        /// <summary>能力档位（每次实时查询；不缓存失败以外的结论）。</summary>
        FrameGenerationCapability QueryCapability();

        /// <summary>一行原因（写日志/装配说明用；可用时为空串）。</summary>
        string CapabilityDetail { get; }

        /// <summary>
        /// 运行期开关：true = 请求插帧，false = 纯透传（不提交输入、不 Prepare、
        /// **不销毁重建 FFX 上下文**）。不可用/被原生拒绝时返回 false（不抛）。
        /// </summary>
        bool TrySetEnabled(bool enabled);

        /// <summary>读统计（原生不可用时返回 false 且 stats 为默认值）。</summary>
        bool TryGetStats(out FrameGenerationStatsInfo stats);
    }
}
