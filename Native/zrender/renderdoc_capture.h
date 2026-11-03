// =============================================================================
// renderdoc_capture.h —— RenderDoc in-application API 封装（TASK-B-R9-16，
// 方案 §5.7「RenderDoc/PIX 一键捕获入口」）
//
// RenderDoc 本体（UI/replay）不进构建——应用侧只消费公开单头 renderdoc_app.h
// 的 in-application API：进程被 RenderDoc UI 启动注入后，renderdoc.dll 就在
// 进程内，经 GetModuleHandleA + GetProcAddress("RENDERDOC_GetAPI") 取得函数表，
// 即可编程触发帧捕获并指定 .rdc 落盘路径模板。
//
// **挂接判定是本封装的核心契约**：进程内没有 renderdoc.dll（独立启动、未经
// RenderDoc 注入）→ Attach() 返回 ModuleNotFound，之后一切捕获操作返回 false
// 且 LastError()=NotAttached——**明确报错，绝不静默假成功**（DevConsole 命令
// `renderdoc.capture` 据此返回 {"ok":false,"error":"renderdoc_not_attached"}）。
//
// 第三方头只在 renderdoc_capture.cpp 内 include（公共头不泄漏 RenderDoc 符号）；
// 测试经 AttachWithGetApi 接缝注入假 API 表（test_renderdoc_capture.cpp），
// 无需安装 RenderDoc 即可覆盖挂接成功路径。
// =============================================================================
#pragma once

#include <cstdint>
#include <string>

namespace zrender {

/// 挂接/捕获错误码（明确、可测；文本见 ZrRenderDocErrorText）
enum class ZrRenderDocError : uint32_t {
    None = 0,
    ModuleNotFound, // 进程内无 renderdoc.dll（未经 RenderDoc UI 注入启动）
    ProcNotFound,   // dll 在但缺 RENDERDOC_GetAPI 导出（异常注入态）
    ApiRejected,    // RENDERDOC_GetAPI 返回空表（请求版本不被支持）
    NotAttached,    // 未挂接就调用捕获操作
    CaptureFailed,  // EndFrameCapture 返回 0（手动帧捕获未完成）
};

/// 错误码 -> 人类可读文本（DevConsole detail / 日志）
const char* ZrRenderDocErrorText(ZrRenderDocError error);

/// 与 RENDERDOC_GetAPI 相同签名的函数指针类型（uint32_t = RENDERDOC_Version）。
/// 公共头不 include renderdoc_app.h，用弱类型签名表达；.cpp 内转换。
using ZrRenderDocGetApiFn = void* (*)(uint32_t version, void** outApiPointers);

/// RenderDoc 应用内 API 封装（不可拷贝；线程约定：与设备调用同线程——渲染线程）
class ZrRenderDocCapture {
public:
    ZrRenderDocCapture() = default;
    ~ZrRenderDocCapture() = default;
    ZrRenderDocCapture(const ZrRenderDocCapture&) = delete;
    ZrRenderDocCapture& operator=(const ZrRenderDocCapture&) = delete;

    /// 生产入口：GetModuleHandleA("renderdoc.dll") -> GetProcAddress
    /// ("RENDERDOC_GetAPI") -> GetAPI(eRENDERDOC_API_Version_1_6_0)。
    /// 幂等：已挂接时直接返回 None（不重复取表）。
    ZrRenderDocError Attach();

    /// 测试/注入接缝：直接给 GetAPI 函数指针（跳过 dll 探测）。
    /// getApi 为 nullptr -> ApiRejected（保持"无表不挂接"不变式）。
    ZrRenderDocError AttachWithGetApi(ZrRenderDocGetApiFn getApi);

    bool IsAttached() const { return m_api != nullptr; }
    ZrRenderDocError LastError() const { return m_lastError; }

    /// .rdc 落盘路径模板（不含扩展名；RenderDoc 自动追加 _<frame>.rdc）。
    /// 未挂接 -> false + NotAttached。
    bool SetCaptureFilePathTemplate(const char* pathTemplate);

    /// 触发帧捕获（present 边界语义——本帧 Present 后落盘）。未挂接 -> false。
    bool TriggerCapture();

    /// headless/无 present 场景的显式帧捕获半边（device = RENDERDOC_DevicePointer，
    /// D3D12 传 ID3D12Device*；window 可为 nullptr）。成对使用。
    bool StartFrameCapture(void* device, void* window);
    /// EndFrameCapture 返回 0 -> false + CaptureFailed（捕获未落盘）。
    bool EndFrameCapture(void* device, void* window);

    /// 已完成捕获数（未挂接返回 0）
    uint32_t GetNumCaptures() const;

    /// 第 idx 个捕获文件的绝对路径（UTF-8；idx 无效/未挂接返回 false）
    bool GetCapturePath(uint32_t idx, std::string& outPath) const;

private:
    ZrRenderDocError AttachFromGetApi(ZrRenderDocGetApiFn getApi);
    void* m_api = nullptr; // RENDERDOC_API_1_6_0*（真实类型只进 .cpp）
    ZrRenderDocError m_lastError = ZrRenderDocError::None;
};

} // namespace zrender
