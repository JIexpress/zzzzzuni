// =============================================================================
// rg_render_graph.h —— 手写 RenderGraph 纯逻辑层（TASK-B-R3-01）
//
// 三层结构（Record -> Compile -> Execute）：
//   - Record  : RgBuilder 语义（CreateTransient/Import/AddPass，setup 内 Read/Write）
//   - Compile : 依赖建边（写->读 / WAW 按声明序）+ 拓扑稳定排序 + 死 Pass 剔除
//               + Transient 生命周期区间 + 别名池槽计划（区间不重叠共享槽）
//               + 环检测（明确错误码）
//   - Barrier : RgBarrierPlanner 逐 pass 推进资源状态——仅真实状态变化处产出
//               屏障；只读共享（读-读零屏障）；同 pass 屏障合并为单次批量提交；
//               DSV 写->读 输出 split barrier 计划（begin/end 对，执行归下任务）
//   - Execute : 按编译序经 IZrCommandList 发屏障批次 + 调 execute 回调
//               （RgContext 句柄解析：未声明访问返回无效句柄并记错）
//
// 仅使用 zrhi 的 POD 与接口类型（zrhi 零改动）。真 D3D12 执行是下一任务。
// 命名遵循 file-conventions.md §3；前缀 rg_/Rg。
// =============================================================================
#pragma once

#include <cstdint>
#include <functional>
#include <string>
#include <vector>

#include "zrhi/zrhi_device.h"

// =============================================================================
// 基础类型
// =============================================================================

/// 资源句柄：index 指向图内资源槽，gen 防陈旧句柄（槽不复用，gen 保留扩展）
struct RgHandle {
    uint32_t index = 0xFFFFFFFFu;
    uint32_t gen = 0;

    constexpr bool IsValid() const noexcept { return gen != 0u; }
    constexpr bool operator==(const RgHandle& o) const noexcept
    {
        return index == o.index && gen == o.gen;
    }
    constexpr bool operator!=(const RgHandle& o) const noexcept { return !(*this == o); }
};

/// 资源驻留类别：Transient=图内临时（参与别名复用）；Imported=外部传入
/// （如交换链后台缓冲，不参与别名，且写它的 Pass 是天然的图根）
enum class RgResidency : uint32_t {
    Transient,
    Imported,
};

/// 资源类别（图像/缓冲二选一）
enum class RgResourceType : uint32_t {
    Image,
    Buffer,
};

/// 资源声明描述（Transient 填 desc；Imported 填设备句柄）
struct RgResourceDesc {
    const char* name = "rg_res";
    RgResidency residency = RgResidency::Transient;
    RgResourceType type = RgResourceType::Image;
    ZrImageDesc imageDesc {};   // type == Image 时有效
    // 是否真的填了 imageDesc。注意不能靠 imageDesc.width==0 判断——ZrImageDesc 自身
    // 默认就是 RGBA8Unorm 1x1（{ } 零初始化也会得到 1x1），正是历史占位值的来源。
    bool hasImageDesc = false;
    ZrBufferDesc bufferDesc {}; // type == Buffer 时有效
    ZrImage importedImage{};   // Imported 图像的设备句柄
    ZrBuffer importedBuffer{}; // Imported 缓冲的设备句柄

    static RgResourceDesc TransientImage(const char* name_, const ZrImageDesc& desc)
    {
        RgResourceDesc d;
        d.name = name_;
        d.residency = RgResidency::Transient;
        d.type = RgResourceType::Image;
        d.imageDesc = desc;
        d.hasImageDesc = true;
        return d;
    }
    static RgResourceDesc TransientBuffer(const char* name_, const ZrBufferDesc& desc)
    {
        RgResourceDesc d;
        d.name = name_;
        d.residency = RgResidency::Transient;
        d.type = RgResourceType::Buffer;
        d.bufferDesc = desc;
        return d;
    }
    static RgResourceDesc ImportImage(const char* name_, ZrImage deviceImage)
    {
        RgResourceDesc d;
        d.name = name_;
        d.residency = RgResidency::Imported;
        d.type = RgResourceType::Image;
        d.importedImage = deviceImage;
        d.hasImageDesc = false; // 未填：Import 时用注册的反查器补全
        return d;
    }
    // 带真实尺寸/格式的重载：rg.export / rg.dump 导出 Imported 资源时要有真实 desc，
    // 否则只能输出占位值（历史问题：depth 显示成 RGBA8Unorm 1×1，真实是 D32Float W×H）。
    static RgResourceDesc ImportImage(const char* name_, ZrImage deviceImage, const ZrImageDesc& desc)
    {
        RgResourceDesc d;
        d.name = name_;
        d.residency = RgResidency::Imported;
        d.type = RgResourceType::Image;
        d.importedImage = deviceImage;
        d.imageDesc = desc;
        d.hasImageDesc = true;
        return d;
    }
    static RgResourceDesc ImportBuffer(const char* name_, ZrBuffer deviceBuffer)
    {
        RgResourceDesc d;
        d.name = name_;
        d.residency = RgResidency::Imported;
        d.type = RgResourceType::Buffer;
        d.importedBuffer = deviceBuffer;
        return d;
    }
};

/// Pass 访问性质：Read=SRV/只读采样；Write=RTV/UAV/DSV 写/传输写；Present=呈现
enum class RgPassAccessKind : uint32_t {
    Read,
    Write,
    Present,
};

/// Pass 内单条资源访问声明（layout/access/stage 未显式给出时按 desc 用途推导）
struct RgPassAccess {
    RgHandle resource;
    RgPassAccessKind kind = RgPassAccessKind::Read;
    ZrImageLayout layout = ZrImageLayout::Undefined;         // 图像：目标布局
    ZrAccessBits access = ZrAccessBits::None;
    ZrPipelineStageBits stage = ZrPipelineStageBits::None;
    bool explicitSync = false; // true=调用方显式指定三元组；false=按用途推导
};

// =============================================================================
// 编译产物数据模型
// =============================================================================

/// 别名池槽计划：生命周期（first-use..last-use，按编译序 pass 序号）不重叠的
/// Transient 资源共享同一槽；重叠资源必然异槽。
struct RgAliasPlan {
    struct Entry {
        RgHandle resource;
        uint32_t firstUse = 0; // 编译序 pass 序号（含）
        uint32_t lastUse = 0;  // 编译序 pass 序号（含）
        uint32_t slot = 0;
    };
    std::vector<Entry> entries; // 按 Transient 资源声明序
    uint32_t slotCount = 0;

    /// 查资源槽位；非 Transient 或未知句柄返回 -1
    int32_t SlotOf(RgHandle resource) const;
};

/// split barrier 计划：被后续 pass 读的 DSV 写——begin 在写 Pass 完成后立刻发、
/// end 延到读 Pass 之前（TASK-B-R3-02 起由执行器真实下发：D3D12 原生
/// BEGIN_ONLY/END_ONLY 语义，translate 层映射 flag）
struct RgSplitBarrier {
    RgHandle resource;
    uint32_t beginAfterPass = 0; // 写 Pass 的编译序序号（begin 在其后）
    uint32_t endBeforePass = 0;  // 读 Pass 的编译序序号（end 在其前）
    ZrImageBarrier barrier{};    // end 处补可见性的屏障内容
};

/// 屏障计划：按 pass 分组的批量屏障 + split 计划 + 总数（供合并效果量化断言）
struct RgBarrierPlan {
    std::vector<std::vector<ZrImageBarrier>> perPassImageBatches;
    std::vector<std::vector<ZrBufferBarrier>> perPassBufferBatches;
    // 与上述批次逐条平行的资源句柄（TASK-B-R3-02）：批次内 barrier.image 为编译期
    // 占位（Transient 句柄此刻尚不存在），执行器据此替换为当帧真资源句柄
    std::vector<std::vector<RgHandle>> perPassImageBatchResources;
    std::vector<std::vector<RgHandle>> perPassBufferBatchResources;
    // A-10（TASK-R10-07）：与 perPassImageBatches 逐条平行的"镜像位"——true 表示该条
    // 是某条 split 计划的**等价内联表达**（split 是优化，不是唯一表达）。Execute
    // 直接下发它（无需理解 splits）；ExecuteAliased 跳过它走 split 半边，避免同一
    // 转换发两遍。既有批次在该 vector 内一律为 false。
    std::vector<std::vector<bool>> perPassImageBatchSplitMirror;
    std::vector<RgSplitBarrier> splits;

    uint32_t InlineImageBarrierCount() const; // 不含 split 镜像（真实独立内联数）
    uint32_t InlineBufferBarrierCount() const;
    /// split 镜像条数（perPassImageBatches 中来自 split 的等价内联屏障）
    uint32_t SplitMirrorCount() const
    {
        uint32_t total = 0;
        for (const std::vector<bool>& flags : perPassImageBatchSplitMirror) {
            for (bool mirror : flags) {
                total += mirror ? 1u : 0u;
            }
        }
        return total;
    }
    uint32_t SplitCount() const { return static_cast<uint32_t>(splits.size()); }
    /// 总屏障数（内联 + split；镜像不重复计数——它与对应 split 是同一道转换）
    uint32_t TotalBarrierCount() const
    {
        return InlineImageBarrierCount() + InlineBufferBarrierCount() + SplitCount();
    }
};

/// 编译错误码（明确、可测）
enum class RgError : uint32_t {
    None = 0,
    CycleDetected,        // 依赖成环（含多 Pass 写同资源且读旁路造成的无序环）
    InvalidHandle,        // setup 声明了无效/未知句柄
    NoRoots,              // 图无根（无 Imported 写、无显式 Root）——全图皆死
};

/// 生命周期区间查询结果
struct RgLifetime {
    uint32_t firstUse = 0;
    uint32_t lastUse = 0;
    bool used = false; // 是否被任何存留 Pass 使用
};

// =============================================================================
// RgContext —— execute 回调内的句柄解析（未声明访问 -> 无效句柄 + 记错）
// =============================================================================
class RgGraph;

// 诊断用：Imported 图像的真实 desc 反查器。
//
// 背景：`ImportImage(name, image)` 不携带尺寸/格式，rg.export 只能输出占位值
// （历史现象：depth 显示成 RGBA8Unorm 1x1，真实是 D32Float W×H）。
// 宿主注册一次（如 zrhi 侧用 ZrD3D12Device::GetImageDesc），RgGraph::Import 在
// imageDesc 为空时自动补全——避免在几十个 Import 调用点各贴一遍 desc。
// 传 nullptr 取消注册。非线程安全（单渲染线程内设置一次即可）。
using RgImageDescResolver = bool (*)(ZrImage image, ZrImageDesc* out);

class RgContext {
public:
    /// 解析图像句柄到设备句柄（Imported=外部句柄；Transient=本帧创建的句柄）。
    /// 当前 Pass 未声明该句柄（setup 未 Read/Write/Present）→ 返回无效句柄并记错。
    ZrImage GetImage(RgHandle handle);
    /// 解析缓冲句柄（同上规则）
    ZrBuffer GetBuffer(RgHandle handle);

    /// 当前 Pass 执行期间累计的"未声明使用"错误数
    uint32_t UndeclaredUseCount() const { return m_undeclaredUses; }

private:
    friend class RgGraph; // RgGraph 在 Execute 中填充 m_graph/m_passDeclIndex
    RgContext() = default;
    const RgGraph* m_graph = nullptr;
    uint32_t m_passDeclIndex = 0xFFFFFFFFu;
    uint32_t m_undeclaredUses = 0;
};

// =============================================================================
// RgPass —— Record 阶段的 Pass 声明（setup 回调内操作）
// =============================================================================
class RgPass {
public:
    /// 只读访问（按资源用途推导 ShaderReadOnly / DepthReadOnly / TransferSrc 等）
    RgPass& Read(RgHandle handle);
    /// 显式同步三元组的只读访问（覆盖推导值）
    RgPass& Read(RgHandle handle, ZrImageLayout layout, ZrAccessBits access,
        ZrPipelineStageBits stage);
    /// 写访问（推导 ColorAttachment / DepthStencilAttachment / General / TransferDst）
    RgPass& Write(RgHandle handle);
    /// 显式同步三元组的写访问
    RgPass& Write(RgHandle handle, ZrImageLayout layout, ZrAccessBits access,
        ZrPipelineStageBits stage);
    /// 呈现访问（布局 Present；通常用于 Imported 后台缓冲的末 Pass）
    RgPass& Present(RgHandle handle);
    /// 显式标记为图根（死 Pass 剔除的反向可达起点）
    RgPass& MarkRoot() { m_root = true; return *this; }

    const char* Name() const { return m_name; }
    bool IsRoot() const { return m_root; }
    const std::vector<RgPassAccess>& Accesses() const { return m_accesses; }

private:
    friend class RgGraph;
    RgPass() = default;
    const char* m_name = "";
    bool m_root = false;
    std::vector<RgPassAccess> m_accesses;
};

using RgSetupFn = std::function<void(RgPass&)>;
using RgExecuteFn = std::function<void(RgContext&)>;

// =============================================================================
// RgGraph —— 图本体：Record / Compile / Execute
// =============================================================================
/// ExecuteAliased 运行统计（TASK-B-R3-02：别名复用与 split 落地的量化证据）
struct RgExecuteStats {
    uint32_t aliasHeapsCreated = 0;  // 别名堆创建数（= 编译出的别名槽总数）
    uint32_t transientImages = 0;    // 本帧物化的 Transient 图像数（placed resource）
    uint32_t transientBuffers = 0;   // 本帧物化的 Transient 缓冲数
    uint32_t peakAliveTransients = 0; // 任一时刻存活 Transient 资源峰值
    uint32_t splitBegins = 0;        // BEGIN_ONLY 提交数
    uint32_t splitEnds = 0;          // END_ONLY 提交数
    uint32_t inlineBarrierCalls = 0; // 内联批量屏障提交数
};

class RgGraph {
public:
    RgGraph() = default;
    /// 带 desc 反查器构造（诊断/导出）：Imported 图像未显式给 desc 时，用该回调按句柄补全。
    /// 存成成员而非全局——全局在静态库/动态库双副本场景下写入与读取不是同一份符号
    /// （实测 resolver 读到 NULL），成员则随图实例走，不存在跨模块问题。
    explicit RgGraph(RgImageDescResolver resolver) : m_importDescResolver(resolver) {}

    /// 运行期替换反查器（传 nullptr 取消）
    void SetImageDescResolver(RgImageDescResolver resolver) { m_importDescResolver = resolver; }

    RgHandle CreateTransient(const RgResourceDesc& desc);
    RgHandle Import(const RgResourceDesc& desc); // desc.residency 必须为 Imported

    /// 声明 Pass：先调 setup（声明读写），Compile 时校验；execute 在 Execute 时回调
    RgPass& AddPass(const char* name, RgSetupFn setup, RgExecuteFn execute);

    /// 编译：建边 -> 环检测 -> 死 Pass 剔除 -> 稳定拓扑排序 -> 生命周期/别名
    /// 计划 -> 屏障计划。失败返回 false（GetLastError 给错误码）。
    bool Compile();

    /// 执行（须先 Compile 成功）：Transient 资源经 device 创建/销毁；
    /// 按 pass 序先发屏障批次（ResourceBarrier 单次批量）再回调 execute。
    void Execute(IZrDevice& device, IZrCommandList& commandList);

    /// 真后端执行（TASK-B-R3-02；须先 Compile 成功）：设备无关——只依赖
    /// IZrDevice/IZrCommandList 扩展接口（别名堆/placed image/split 屏障半边），
    /// D3D12 与 Mock 均可驱动。与 Execute 的差异：
    ///   - Transient 图像按别名槽落位 placed resource（每槽一堆，槽内串行复用——
    ///     真内存复用），生命周期区间内才存在（首次使用前创建、末次使用后销毁）
    ///   - 屏障批次的占位句柄替换为当帧真资源句柄后再提交
    ///   - split 计划真实下发：写 Pass 后 BEGIN_ONLY、读 Pass 前 END_ONLY
    /// 返回运行统计（别名堆数/峰值存活/split 提交数等，供验证程序断言）
    RgExecuteStats ExecuteAliased(IZrDevice& device, IZrCommandList& commandList);

    /// 物理销毁 ExecuteAliased 退役的资源与别名堆（GPU 完成等待后由调用方触发；
    /// 下一次 ExecuteAliased 入口亦自动冲刷）。退役 = 逻辑死亡（区间外不可用），
    /// 物理销毁延迟到 GPU 同步点后——D3D12 语义：命令表引用的资源在提交完成前
    /// 不得 Release。冲刷后 GetDeviceStats().imageCount 回落即"用后即毁"实证。
    void FlushRetired(IZrDevice& device);

    // ---- 编译产物查询（测试/上层调度用）--------------------------------------
    const std::vector<uint32_t>& CompiledOrder() const { return m_compiledOrder; }
    const RgAliasPlan& AliasPlan() const { return m_aliasPlan; }
    const RgBarrierPlan& BarrierPlan() const { return m_barrierPlan; }
    RgError Error() const { return m_lastError; } // 不用 GetLastError：Win32 宏冲突
    /// 编译后被剔除的 Pass 声明序号列表
    const std::vector<uint32_t>& CulledPasses() const { return m_culledPasses; }
    /// Transient 资源生命周期（编译序 pass 序号）；非 Transient/未用返回 used=false
    RgLifetime LifetimeOf(RgHandle resource) const;
    /// 全图 Pass 总数（含被剔除者）
    uint32_t PassCount() const { return static_cast<uint32_t>(m_passes.size()); }

    // ---- 编译产物导出查询（TASK-B-R9-16：rg.export 数据源，纯只读）------------
    /// 声明序号的 Pass 名（越界返回空串；被剔除者同可查——供导出标注 culled）
    const char* PassNameAt(uint32_t declIndex) const;
    /// 声明序号 Pass 的访问声明列表（越界返回 nullptr；供导出 pass 读/写集）
    const std::vector<RgPassAccess>* PassAccessesAt(uint32_t declIndex) const;
    /// 图内资源描述数量（RgHandle.index 的合法取值范围 [0, ResourceCount())）
    uint32_t ResourceCount() const { return static_cast<uint32_t>(m_resources.size()); }
    /// 资源描述（index 越界返回 nullptr）
    const RgResourceDesc* ResourceDescAt(uint32_t index) const;
    /// 资源句柄（含 gen——LifetimeOf/SlotOf 的入参须带真实 gen，勿手工拼 1）
    RgHandle ResourceHandleAt(uint32_t index) const;
    /// 依赖边（declIdx -> 后继 declIdx 列表，声明序；Compile 后有效）
    const std::vector<std::vector<uint32_t>>& Edges() const { return m_edges; }
    /// 最近一次 Execute 期间 RgContext 记录的未声明使用总数
    uint32_t LastExecuteUndeclaredUses() const { return m_lastExecuteUndeclaredUses; }

private:
    friend class RgContext; // GetImage/GetBuffer 查 FindResource/PassAt
    RgImageDescResolver m_importDescResolver = nullptr; // Imported 图像 desc 反查（可空）
    struct ResourceEntry {
        RgResourceDesc desc;
        uint32_t gen = 1;
        ZrImage transientImage{};   // Execute 时创建的实际句柄
        ZrBuffer transientBuffer{};
    };
    struct PassEntry {
        RgPass pass;
        RgExecuteFn execute;
    };

    // 编译内部步骤
    bool BuildEdgesAndDetectCycle();
    void CullDeadPasses();
    bool TopologicalSort();           // 对存留 Pass，稳定（同层按声明序）
    void ComputeLifetimesAndAlias();  // 生命周期区间 + 别名槽分配
    void PlanBarriers();              // RgBarrierPlanner 核心

    const ResourceEntry* FindResource(RgHandle handle) const;
    const RgPass& PassAt(uint32_t declIndex) const; // 声明序号 -> Pass（友元用）

    std::vector<ResourceEntry> m_resources;
    // ExecuteAliased 退役队列（延迟物理销毁，见 FlushRetired 注）
    std::vector<ZrImage> m_retiredImages;
    std::vector<ZrBuffer> m_retiredBuffers;
    std::vector<ZrAliasHeap> m_activeAliasHeaps; // 当帧活跃别名堆（帧末整体退役）
    std::vector<PassEntry> m_passes;
    std::vector<uint32_t> m_compiledOrder;  // 存留 Pass 的声明序号，按执行序
    std::vector<uint32_t> m_culledPasses;   // 被剔除 Pass 的声明序号
    std::vector<std::vector<uint32_t>> m_edges; // declIdx -> 后继 declIdx 列表（去重）
    RgAliasPlan m_aliasPlan;
    RgBarrierPlan m_barrierPlan;
    RgError m_lastError = RgError::None;
    uint32_t m_lastExecuteUndeclaredUses = 0;
};
