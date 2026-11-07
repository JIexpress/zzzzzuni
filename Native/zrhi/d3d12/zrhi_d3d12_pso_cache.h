// =============================================================================
// zrhi_d3d12_pso_cache.h —— D3D12 PSO 磁盘缓存（TASK-B-PSO-01）
//
// 机制：Windows 官方 ID3D12PipelineLibrary（ID3D12Device1::CreatePipelineLibrary）
//   - 设备就绪后尝试从磁盘 blob 反序列化库；失败 → 空库；空库也失败 → 静默禁用
//   - CreatePipelineState 先 library->LoadGraphicsPipeline(name=desc 稳定哈希)：
//       命中 → 直接返回驱动缓存的 PSO；
//       未命中 → 设备直接创建（兜底）→ library->StorePipeline 入册，退出时落盘
//   - 任何库路径失败 → 回退 ID3D12Device::CreateGraphicsPipelineState（兜底优先）
//   - 退出时 Serialize() → 临时文件 + FlushFileBuffers + MoveFileEx 原子替换写盘
//     （FlushFileBuffers 只在退出/显式刷写路径，不在每帧路径）
//   - 文件坏了（截断/改字节/版本不符/换 GPU）→ 识别 → 删除重建 → 绝不崩溃
//
// 文件布局（头 72B + name 数组 + D3D12 库 blob）：
//   magic / 格式版本 / 头尺寸 / blob 尺寸 / blob FNV-1a / name 数量 / name FNV-1a
//   / GPU 指纹（vendorId/deviceId/subSysId/revision）
//   name 数组 = 每个 uint64 哈希（即 PSO 名字；用于区分"命中"与"库内新建"）
//
// 路径：环境变量 ZFRAME_PSO_CACHE
//   未设置     → exe 同目录 pso_cache.bin（不可写则 %LOCALAPPDATA%\ZFrame\pso_cache.bin）
//   0/off      → 禁用缓存（基线/对照测量用）
//   其他       → 视为缓存文件路径（相对/绝对均可）
//
// 线程模型：与 zrhi 一致（单线程独占）。
// =============================================================================
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d12.h>
#include <wrl/client.h>

#include <cstdint>
#include <string>
#include <unordered_set>
#include <vector>

/// 缓存运行统计（TASK-B-PSO-01 判据用；日志与验证程序共读）
struct ZrPsoCacheStats {
    bool supported = false;     // ID3D12PipelineLibrary 可用且库对象就绪
    bool diskLoaded = false;    // 本次进程从磁盘成功反序列化
    uint32_t hits = 0;          // 库命中（库内已有同名 PSO）
    uint32_t misses = 0;        // 库未命中（库内新建，本次编译）
    uint32_t fallbacks = 0;     // 库路径失败 → 直接创建次数（兜底）
    uint32_t directCreates = 0; // 无缓存路径（能力不支持/被禁用）直接创建次数
    double loadMs = 0.0;        // 启动加载（读盘 + 反序列化）耗时
    double createMs = 0.0;      // PSO 创建累计耗时（命中 + 未命中 + 兜底）
    double compileMs = 0.0;     // 其中：驱动直接编译（未命中/兜底）累计
    double storeMs = 0.0;       // 其中：StorePipeline（入库）累计
    uint64_t serializeBytes = 0; // 本次写盘的 D3D12 库 blob 字节数
};

/// PSO 磁盘缓存（设备持有；CreatePipelineState 内部使用）
class ZrD3D12PsoCache final {
public:
    ZrD3D12PsoCache() = default;
    ~ZrD3D12PsoCache() = default;
    ZrD3D12PsoCache(const ZrD3D12PsoCache&) = delete;
    ZrD3D12PsoCache& operator=(const ZrD3D12PsoCache&) = delete;

    /// 设备就绪后调用：能力探测 + 磁盘加载。**任何失败都只是禁用缓存**，
    /// 返回 false；调用方不受影响（设备照常工作）。
    bool Initialize(ID3D12Device* device);

    /// 退出/显式刷写：库有新条目则 Serialize + 原子写盘，并打印统计。
    /// 幂等；未启用/无新增时不写盘。
    void Flush();

    /// 创建图形 PSO：优先库（按 desc 哈希命名），失败回退设备直接创建。
    /// 返回最终 HRESULT；输出参数语义与 ID3D12Device::CreateGraphicsPipelineState 一致。
    HRESULT CreateGraphicsPipelineState(const D3D12_GRAPHICS_PIPELINE_STATE_DESC& desc,
        const void* vsCode, size_t vsBytes, const void* psCode, size_t psBytes,
        uint32_t rootCbvCount, uint32_t srvTableCount, REFIID riid, void** outPipelineState);

    const ZrPsoCacheStats& Stats() const { return m_stats; }
    bool IsActive() const { return m_library != nullptr; }
    const std::wstring& Path() const { return m_path; }

private:
    /// desc 稳定哈希（命名用）：版本前缀 + D3D12 desc 全状态位 + 两段着色器字节码
    /// + 根签名布局计数（rootCbvCount / srvTableCount）。
    static uint64_t HashPipelineDesc(const D3D12_GRAPHICS_PIPELINE_STATE_DESC& desc,
        const void* vsCode, size_t vsBytes, const void* psCode, size_t psBytes,
        uint32_t rootCbvCount, uint32_t srvTableCount);

    void ResolvePath();
    bool ReadCacheFile(std::vector<uint64_t>& names, std::vector<uint8_t>& blob);
    void DeleteCacheFile();
    bool CreateEmptyLibrary();
    bool WriteCacheFile(const std::vector<uint64_t>& names, const void* blob, size_t blobBytes);
    void LogStats(const char* when) const;

    Microsoft::WRL::ComPtr<ID3D12Device> m_device;
    Microsoft::WRL::ComPtr<ID3D12PipelineLibrary> m_library;
    std::vector<uint8_t> m_diskBlob; // 反序列化用的磁盘 blob 保活（防运行时仍引用）
    std::wstring m_path;
    std::vector<uint64_t> m_names;             // 库内条目（稳定顺序，落盘顺序）
    std::unordered_set<uint64_t> m_nameLookup; // 命中判定 O(1)
    ZrPsoCacheStats m_stats;
    bool m_initialized = false;
    bool m_dirty = false;
    bool m_rebuilt = false; // 库内容失效后已重建过一次（防死循环）
    // GPU 指纹（缓存文件兼容性前置校验；驱动版本变更由 CreatePipelineLibrary 拒绝）
    uint32_t m_vendorId = 0;
    uint32_t m_deviceId = 0;
    uint32_t m_subSysId = 0;
    uint32_t m_revision = 0;
};
