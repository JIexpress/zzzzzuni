// =============================================================================
// zrhi_barriers.h —— 资源屏障数据模型（TASK-B-R2-01）
//
// POD 屏障描述（本任务只做数据模型与只读判定，执行由后端/命令表实现）：
//   - ZrImageBarrier  → VkImageMemoryBarrier（布局转换 + 子资源 range）
//   - ZrBufferBarrier → VkBufferMemoryBarrier（偏移/整段 range）
//   - ZrSubresourceRange → VkImageSubresourceRange（省略 aspectMask：
//     深度/模板作为整体同步，细分 aspect 由后端展开）
//
// 只读判定辅助（R3 屏障合并的判定基础）：
//   - ZrIsReadOnlyLayout : 布局是否纯读（多个只读区间可共享一道屏障）
//   - ZrAccessCompatible : 两段访问无写冒险即可共享屏障（读-读兼容）
// =============================================================================
#pragma once

#include "zrhi/zrhi_handles.h"
#include "zrhi/zrhi_types.h"

// ---- 子资源常量（→ VK_REMAINING_MIP_LEVELS / VK_REMAINING_ARRAY_LAYERS / VK_WHOLE_SIZE）
constexpr uint32_t ZR_REMAINING_MIP_LEVELS = 0xFFFFFFFFu;
constexpr uint32_t ZR_REMAINING_ARRAY_LAYERS = 0xFFFFFFFFu;
constexpr uint64_t ZR_WHOLE_SIZE = 0xFFFFFFFFFFFFFFFFull;

/// 图像子资源范围（→ VkImageSubresourceRange）
struct ZrSubresourceRange {
    uint32_t baseMipLevel = 0;
    uint32_t levelCount = ZR_REMAINING_MIP_LEVELS;   // 剩余全部 mip
    uint32_t baseArrayLayer = 0;
    uint32_t layerCount = ZR_REMAINING_ARRAY_LAYERS; // 剩余全部层

    /// 整资源范围（默认构造即整资源；显式表达意图用）
    static constexpr ZrSubresourceRange All() { return ZrSubresourceRange{}; }
};

/// 图像屏障（→ VkImageMemoryBarrier；省略队列所有权转移字段，后续任务补）
struct ZrImageBarrier {
    ZrImage image{};
    ZrImageLayout prevLayout = ZrImageLayout::Undefined; // 转换前布局（oldLayout）
    ZrImageLayout newLayout = ZrImageLayout::Undefined;  // 转换后布局（newLayout）
    ZrSubresourceRange subresources;                     // 整资源或子资源 range

    ZrAccessBits srcAccess = ZrAccessBits::None;         // 屏障前访问（srcAccessMask）
    ZrAccessBits dstAccess = ZrAccessBits::None;         // 屏障后访问（dstAccessMask）
    ZrPipelineStageBits srcStage = ZrPipelineStageBits::None; // 执行域（srcStageMask）
    ZrPipelineStageBits dstStage = ZrPipelineStageBits::None; // 可见域（dstStageMask）
};

/// 缓冲屏障（→ VkBufferMemoryBarrier）
struct ZrBufferBarrier {
    ZrBuffer buffer{};
    uint64_t offset = 0;
    uint64_t size = ZR_WHOLE_SIZE; // 整段或字节区间

    ZrAccessBits srcAccess = ZrAccessBits::None;
    ZrAccessBits dstAccess = ZrAccessBits::None;
    ZrPipelineStageBits srcStage = ZrPipelineStageBits::None;
    ZrPipelineStageBits dstStage = ZrPipelineStageBits::None;
};

/// 布局是否纯读：只读布局的连续使用互不冒险，可共享屏障。
/// 纯读 = ShaderReadOnlyOptimal（采样）/ DepthStencilReadOnlyOptimal（只读深度模板）
///       / TransferSrcOptimal（拷贝源）/ Present（呈现引擎只读消费）。
/// 非纯读 = Undefined（内容不保证）/ General（可读写）/ 两种 Attachment 最优
///          （读写）/ TransferDstOptimal（写入）。
inline bool ZrIsReadOnlyLayout(ZrImageLayout layout)
{
    switch (layout) {
    case ZrImageLayout::ShaderReadOnlyOptimal:
    case ZrImageLayout::DepthStencilReadOnlyOptimal:
    case ZrImageLayout::TransferSrcOptimal:
    case ZrImageLayout::Present:
        return true;
    default:
        return false; // Undefined/General/ColorAttachment/DepthStencilAttachment/TransferDst
    }
}

/// 访问掩码是否纯读（不含任何写访问位；None 视为纯读——无访问即无冒险）
inline bool ZrIsReadOnlyAccess(ZrAccessBits access)
{
    return (access & ZR_ACCESS_WRITE_MASK) == ZrAccessBits::None;
}

/// 两段访问是否兼容（无屏障即可共存/可共享同一道屏障）：
///   - 任一侧无访问（None）→ 兼容（不触碰内存）
///   - 两侧均纯读 → 兼容（读-读无冒险；R3 屏障合并依据）
///   - 任一侧含写访问 → 不兼容（RAW/WAR/WAW 均需屏障）
inline bool ZrAccessCompatible(ZrAccessBits a, ZrAccessBits b)
{
    if (a == ZrAccessBits::None || b == ZrAccessBits::None) {
        return true;
    }
    return ZrIsReadOnlyAccess(a) && ZrIsReadOnlyAccess(b);
}
