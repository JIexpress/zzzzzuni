// =============================================================================
// gpu_hud.h —— DrawCall/显存 HUD 统计（TASK-B-R9-16，方案 §5.7「HUD 计数」）
//
// 方案 §5.7 承诺的「DrawCall/显存/带宽计数 HUD」的原生数据层：本模块**只做
// 统计与格式化**（纯函数），不做任何渲染、不做 IO、不做线程同步——
//   - DrawCall 数：调用方（窗口程序/引擎）从命令表插桩计数器取（B-R2-02 起
//     窗口程序有 DrawIndexedInstanced 计数先例），本模块不做拦截
//   - 显存字节：调用方按自有资源台账汇总，或用本模块的两个估算纯函数
//     （按 ZrImageDesc/ZrBufferDesc 逐 mip 链估算；ZrDeviceStats 现只有计数
//     无字节，扩展 GetDeviceStats 属 zrhi 头改动，本任务约束不动 zrhi）
//   - RT 分辨率：调用方传当前 RT/交换链尺寸
//
// 聚合器：固定容量滑动窗口（缺省 60 帧），Push 覆盖最旧样本；快照给出
// 最新值 + 窗口峰值 + 平均帧耗/帧率。线程约定：仅渲染线程调用（无锁）。
// 输出两种格式：单行 JSON（DevConsole `hud` 命令响应）与单行文本（HUD 叠加）。
// 命名遵循 file-conventions.md §3；前缀 Zr。
// =============================================================================
#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "zrhi/zrhi_device.h"

namespace zrender {

// =============================================================================
// 资源字节估算（纯函数；显存占用的近似口径——与后端实际分配（对齐/填充/交换链
// 额外缓冲）存在差异，用于 HUD 趋势显示与资源量级判断，不作精确计量）
// =============================================================================

/// 图像字节估算：Σ_mip ceil(w/2^m/blockW)×ceil(h/2^m/blockH)×bytesPerBlock，
/// ×arrayLayers。blockW/blockH 走 ZrGetFormatTraits（压缩格式按块计）。
uint64_t ZrEstimateImageBytes(const ZrImageDesc& desc);

/// 缓冲字节估算：desc.size（不含隐式对齐填充）。
constexpr uint64_t ZrEstimateBufferBytes(const ZrBufferDesc& desc)
{
    return desc.size;
}

// =============================================================================
// HUD 样本与聚合
// =============================================================================

/// 单帧样本（窗口程序每帧采集一次）
struct ZrHudSample {
    uint32_t drawCalls = 0;  // 本帧 DrawIndexedInstanced（或等价绘制）调用数
    uint32_t rtWidth = 0;    // 当前 RT/交换链宽
    uint32_t rtHeight = 0;   // 当前 RT/交换链高
    double frameMs = 0.0;    // 本帧 CPU 帧耗（毫秒）
    uint64_t memoryBytes = 0; // 调用方口径的显存字节（估算汇总）
    ZrDeviceStats device {}; // 设备统计透传（资源计数/Present 失败/设备移除）
};

/// 聚合快照（DevConsole `hud` 命令与 HUD 文本的数据源）
struct ZrHudFrameStats {
    uint32_t drawCalls = 0;        // 最新一帧 DrawCall 数
    uint32_t drawCallsPeak = 0;    // 窗口内峰值
    uint32_t rtWidth = 0;
    uint32_t rtHeight = 0;
    uint64_t memoryBytes = 0;      // 最新一帧显存字节估算
    uint32_t imageCount = 0;       // 设备存活图像数（ZrDeviceStats 透传）
    uint32_t bufferCount = 0;
    uint32_t aliasHeapCount = 0;
    uint32_t presentFailureCount = 0;
    bool deviceRemoved = false;
    double frameMsAvg = 0.0;       // 窗口内平均帧耗
    double fps = 0.0;              // 1000/frameMsAvg（frameMsAvg<=0 时为 0）
    uint32_t frames = 0;           // 窗口内样本数（0 = 尚无样本）
};

/// 滑动窗口聚合器（缺省 60 帧；容量固定，Push 满后覆盖最旧）
class ZrHudAggregator {
public:
    /// window 至少为 1（传 0/越界时按 1 处理——防退化，构造期定死不动态扩容）
    explicit ZrHudAggregator(uint32_t window = 60u);

    void Push(const ZrHudSample& sample);

    /// 聚合当前窗口（无样本时返回全零快照——fps=0 且 frames=0，不臆造数字）
    ZrHudFrameStats Snapshot() const;

    void Reset();

    /// 容量（测试/断言用）
    uint32_t Window() const { return static_cast<uint32_t>(m_window.size()); }
    /// 当前样本数（<= Window()）
    uint32_t Count() const { return m_count; }

private:
    std::vector<ZrHudSample> m_window;
    uint32_t m_head = 0;   // 下一写入位
    uint32_t m_count = 0;  // 已写入样本数
};

// =============================================================================
// 格式化（纯函数；数字统一 %.4f 定点——确定性，两次运行逐位一致）
// =============================================================================

/// 单行 JSON（不含 DevConsole 信封——调用方拼 {"ok":true,...} 外壳）：
/// {"drawCalls":3,"drawCallsPeak":3,"rtWidth":640,"rtHeight":480,"memoryBytes":...,
///  "imageCount":..,"bufferCount":..,"aliasHeapCount":..,"presentFailureCount":..,
///  "deviceRemoved":false,"frameMs":0.0000,"fps":0.0000,"frames":60}
std::string ZrHudFormatJson(const ZrHudFrameStats& stats);

/// 单行文本（HUD 叠加/日志）：drawCalls=3 (peak 3) rt=640x480 mem=2.77MiB
/// images=6 buffers=5 heaps=0 presentFail=0 removed=0 frameMs=0.0000ms fps=0.0000
std::string ZrHudFormatText(const ZrHudFrameStats& stats);

} // namespace zrender
