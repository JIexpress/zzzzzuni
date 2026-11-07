// =============================================================================
// gpu_hud.cpp —— HUD 统计实现（TASK-B-R9-16）
//
// 纯函数实现：估算只依赖 desc 字段与格式 traits；聚合只依赖已 Push 的样本。
// 无全局状态、无 IO、无锁——多帧采样窗口见 gpu_hud.h 类注释。
// =============================================================================
#include "zrender/gpu_hud.h"

#include <cstdio>

namespace zrender {

// =============================================================================
// 资源字节估算
// =============================================================================
uint64_t ZrEstimateImageBytes(const ZrImageDesc& desc)
{
    const ZrFormatTraits traits = ZrGetFormatTraits(desc.format);
    uint64_t blockW = traits.blockW;
    uint64_t blockH = traits.blockH;
    if (blockW == 0u) {
        blockW = 1u; // 防御：traits 表异常时不做除零（正常路径不可达）
    }
    if (blockH == 0u) {
        blockH = 1u;
    }

    uint64_t total = 0u;
    uint64_t width = desc.width;
    uint64_t height = desc.height;
    for (uint32_t mip = 0; mip < desc.mipLevels; ++mip) {
        // 逐 mip：尺寸减半下取整（≥1）→ blocks = ceil(mipSize / blockSize)
        // 即 ceil(max(1, floor(w/2^m)) / blockW)；**不是** ceil(w/2^m)，勿改回
        const uint64_t blocksX = (width + blockW - 1u) / blockW;
        const uint64_t blocksY = (height + blockH - 1u) / blockH;
        total += blocksX * blocksY * traits.bytesPerBlock;
        width = width > 1u ? width / 2u : 1u;
        height = height > 1u ? height / 2u : 1u;
    }
    return total * desc.arrayLayers;
}

// =============================================================================
// 聚合器
// =============================================================================
ZrHudAggregator::ZrHudAggregator(uint32_t window)
{
    const uint32_t capacity = window == 0u ? 1u : window;
    m_window.resize(capacity);
}

void ZrHudAggregator::Push(const ZrHudSample& sample)
{
    m_window[m_head] = sample;
    m_head = (m_head + 1u) % static_cast<uint32_t>(m_window.size());
    if (m_count < m_window.size()) {
        m_count += 1u;
    }
}

ZrHudFrameStats ZrHudAggregator::Snapshot() const
{
    ZrHudFrameStats out;
    if (m_count == 0u) {
        return out; // 无样本：全零快照（frames=0 是"尚无数据"的明确标记）
    }

    // 样本按写入序回放（环形：head 为最旧……head+count-1 为最新）
    double frameMsSum = 0.0;
    for (uint32_t i = 0; i < m_count; ++i) {
        const uint32_t index
            = (m_head + m_window.size() - m_count + i) % static_cast<uint32_t>(m_window.size());
        const ZrHudSample& sample = m_window[index];
        frameMsSum += sample.frameMs;
        if (sample.drawCalls > out.drawCallsPeak) {
            out.drawCallsPeak = sample.drawCalls;
        }
        if (i + 1u == m_count) {
            // 最新样本：瞬时值类指标取最新（drawCalls/RT/显存/设备状态）
            out.drawCalls = sample.drawCalls;
            out.rtWidth = sample.rtWidth;
            out.rtHeight = sample.rtHeight;
            out.memoryBytes = sample.memoryBytes;
            out.imageCount = sample.device.imageCount;
            out.bufferCount = sample.device.bufferCount;
            out.aliasHeapCount = sample.device.aliasHeapCount;
            out.presentFailureCount = sample.device.presentFailureCount;
            out.deviceRemoved = sample.device.deviceRemoved;
        }
    }
    out.frames = m_count;
    out.frameMsAvg = frameMsSum / static_cast<double>(m_count);
    out.fps = out.frameMsAvg > 0.0 ? 1000.0 / out.frameMsAvg : 0.0;
    return out;
}

void ZrHudAggregator::Reset()
{
    m_head = 0u;
    m_count = 0u;
}

// =============================================================================
// 格式化
// =============================================================================
std::string ZrHudFormatJson(const ZrHudFrameStats& stats)
{
    char tmp[256];
    std::string out;
    out.reserve(256u);
    std::snprintf(tmp, sizeof(tmp),
        "{\"drawCalls\":%u,\"drawCallsPeak\":%u,\"rtWidth\":%u,\"rtHeight\":%u,"
        "\"memoryBytes\":%llu,\"imageCount\":%u,\"bufferCount\":%u,\"aliasHeapCount\":%u,"
        "\"presentFailureCount\":%u,\"deviceRemoved\":%s,\"frameMs\":%.4f,\"fps\":%.4f,"
        "\"frames\":%u}",
        stats.drawCalls, stats.drawCallsPeak, stats.rtWidth, stats.rtHeight,
        static_cast<unsigned long long>(stats.memoryBytes), stats.imageCount, stats.bufferCount,
        stats.aliasHeapCount, stats.presentFailureCount, stats.deviceRemoved ? "true" : "false",
        stats.frameMsAvg, stats.fps, stats.frames);
    out += tmp;
    return out;
}

std::string ZrHudFormatText(const ZrHudFrameStats& stats)
{
    char tmp[256];
    std::string out;
    out.reserve(192u);
    const double mib = static_cast<double>(stats.memoryBytes) / (1024.0 * 1024.0);
    std::snprintf(tmp, sizeof(tmp),
        "drawCalls=%u (peak %u) rt=%ux%u mem=%.2fMiB images=%u buffers=%u heaps=%u "
        "presentFail=%u removed=%d frameMs=%.4fms fps=%.4f",
        stats.drawCalls, stats.drawCallsPeak, stats.rtWidth, stats.rtHeight, mib,
        stats.imageCount, stats.bufferCount, stats.aliasHeapCount, stats.presentFailureCount,
        stats.deviceRemoved ? 1 : 0, stats.frameMsAvg, stats.fps);
    out += tmp;
    return out;
}

} // namespace zrender
