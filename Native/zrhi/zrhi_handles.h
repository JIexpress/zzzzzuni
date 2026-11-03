// =============================================================================
// zrhi_handles.h —— 强类型资源句柄 + 泛代句柄表（TASK-B-R2-01）
//
// 句柄编码：uint32 = index:24 bit（低）+ generation:8 bit（高）
//   - 每种资源一个独立 struct（ZrImage/ZrBuffer/... 包装 uint32，防误传类别）
//   - 代际 1..255（0 保留：零值句柄永远无效；INVALID 哨兵 0xFFFFFFFF 永远无效）
//   - 槽位代际耗尽（255 用完）即退役，不回绕——防旧句柄"复活"（回绕保护）
//
// ZrHandleTable<T>：泛代表（Acquire/Release/TryGet + 计数查询）。
// Vulkan 对应：句柄 ≈ VkImage/VkBuffer/...（Vulkan 用指针，Zr 用 index+gen
// 换取失效检测与 O(1) 校验）；生命周期对应 vkCreate*/vkDestroy*。
// 线程模型：单线程独占访问（同 zkernel 约定，无锁）。
// =============================================================================
#pragma once

#include <cstdint> // uint32_t

// ---- 句柄位域常量 -------------------------------------------------------------
constexpr uint32_t ZR_HANDLE_INVALID = 0xFFFFFFFFu;      // 无效哨兵（index/gen 双最大值）
constexpr uint32_t ZR_HANDLE_INDEX_MASK = 0x00FFFFFFu;   // 低 24 bit：槽位索引
constexpr uint32_t ZR_HANDLE_GEN_SHIFT = 24;             // 高 8 bit：代际
constexpr uint32_t ZR_HANDLE_GEN_MASK = 0xFFu;
constexpr uint32_t ZR_HANDLE_MAX_SLOTS = 0xFFFFFFu;      // 容量上限（保证哨兵永不与合法句柄撞值）
constexpr uint32_t ZR_HANDLE_GEN_FIRST = 1u;             // 首个代际（0 保留）
constexpr uint32_t ZR_HANDLE_GEN_LAST = 255u;            // 末代（再释放即退役）

// =============================================================================
// ZrHandle<Self> —— 句柄基座（CRTP：FromRaw 返回派生类型）
// =============================================================================
template <class Self>
struct ZrHandle {
    uint32_t raw = ZR_HANDLE_INVALID;

    /// 槽位索引（低 24 bit）
    constexpr uint32_t Index() const noexcept { return raw & ZR_HANDLE_INDEX_MASK; }

    /// 代际（高 8 bit）
    constexpr uint32_t Generation() const noexcept
    {
        return (raw >> ZR_HANDLE_GEN_SHIFT) & ZR_HANDLE_GEN_MASK;
    }

    /// 形式有效性（非哨兵值）；权威校验以句柄表 TryGet 为准
    constexpr bool IsValid() const noexcept { return raw != ZR_HANDLE_INVALID; }

    constexpr bool operator==(const ZrHandle& o) const noexcept { return raw == o.raw; }
    constexpr bool operator!=(const ZrHandle& o) const noexcept { return raw != o.raw; }

    /// 由打包 raw 值构造（供句柄表与后端使用；普通调用方不应手工拼句柄）
    static constexpr Self FromRaw(uint32_t rawValue) noexcept
    {
        Self h;
        h.raw = rawValue;
        return h;
    }
};

// ---- 八类资源句柄（各为独立 struct，编译期防跨类别误用）------------------------
struct ZrImage : ZrHandle<ZrImage> {};           // → VkImage
struct ZrBuffer : ZrHandle<ZrBuffer> {};         // → VkBuffer
struct ZrImageView : ZrHandle<ZrImageView> {};   // → VkImageView
struct ZrSampler : ZrHandle<ZrSampler> {};       // → VkSampler
struct ZrCommandList : ZrHandle<ZrCommandList> {}; // → VkCommandBuffer
struct ZrSwapchain : ZrHandle<ZrSwapchain> {};   // → VkSwapchainKHR
struct ZrPipelineState : ZrHandle<ZrPipelineState> {}; // → VkPipeline + VkPipelineLayout
struct ZrShader : ZrHandle<ZrShader> {};         // → VkShaderModule
struct ZrQueryPool : ZrHandle<ZrQueryPool> {};   // → VkQueryPool（TASK-B-R2-03：时间戳）
// → VkDeviceMemory 子分配等价物（TASK-B-R3-02：placed resource 落位堆，别名复用载体）
struct ZrAliasHeap : ZrHandle<ZrAliasHeap> {};

// =============================================================================
// ZrHandleTableBase —— 泛代表槽位引擎（raw 句柄 + void* 载荷；实现在 .cpp）
// =============================================================================
class ZrHandleTableBase {
public:
    ZrHandleTableBase();
    ~ZrHandleTableBase();
    ZrHandleTableBase(const ZrHandleTableBase&) = delete;
    ZrHandleTableBase& operator=(const ZrHandleTableBase&) = delete;

    /// 分配槽位：空闲链优先（LIFO），无空闲则追加新槽（扩容）。
    /// 返回打包 raw 句柄；槽位耗尽（达 ZR_HANDLE_MAX_SLOTS）返回 ZR_HANDLE_INVALID。
    uint32_t AcquireRaw(void* payload);

    /// 释放并使句柄失效：代际 +1 供下次分配；代际将回绕（>255）则槽位永久退役。
    /// 句柄无效（stale/越界/哨兵）返回 false。
    bool ReleaseRaw(uint32_t raw);

    /// 校验取值：stale（代际失配/未占用/已释放/越界/零值/哨兵）返回 false。
    bool TryGetRaw(uint32_t raw, void*& outPayload) const;

    /// 槽位总数（含空闲与退役）
    uint32_t Capacity() const;

    /// 存活句柄数
    uint32_t AliveCount() const;

    /// 退役槽位数（generation 回绕保护触发）
    uint32_t RetiredCount() const;

private:
    struct Impl;
    Impl* m_impl;
};

// =============================================================================
// ZrHandleTable<T> —— 强类型泛代表（薄封装：T 为八类句柄之一）
// =============================================================================
template <class T>
class ZrHandleTable {
public:
    /// 分配：返回新句柄（失败返回形式无效句柄，raw == ZR_HANDLE_INVALID）
    T Acquire(void* payload = nullptr) { return T::FromRaw(m_base.AcquireRaw(payload)); }

    /// 释放（stale/无效句柄返回 false；释放后同 index 复用时 generation +1）
    bool Release(T handle) { return m_base.ReleaseRaw(handle.raw); }

    /// 校验取值（stale 拒绝）：有效时写 outPayload 并返回 true
    bool TryGet(T handle, void*& outPayload) const
    {
        return m_base.TryGetRaw(handle.raw, outPayload);
    }

    uint32_t Capacity() const { return m_base.Capacity(); }
    uint32_t AliveCount() const { return m_base.AliveCount(); }
    uint32_t RetiredCount() const { return m_base.RetiredCount(); }

private:
    ZrHandleTableBase m_base;
};
