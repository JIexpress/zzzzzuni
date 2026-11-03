// =============================================================================
// renderdoc_capture.cpp —— RenderDoc 应用内 API 封装实现（TASK-B-R9-16）
//
// 唯一 include renderdoc_app.h 的编译单元（公共头不泄漏第三方符号，
// 见 renderdoc_capture.h 节头注与 ThirdParty/renderdoc/SOURCE.md）。
// =============================================================================
#include "zrender/renderdoc_capture.h"

#include <cstdio>

#include "renderdoc/renderdoc_app.h"

#if defined(_WIN32)
#include <windows.h>
#endif

namespace zrender {

const char* ZrRenderDocErrorText(ZrRenderDocError error)
{
    switch (error) {
        case ZrRenderDocError::None: return "ok";
        case ZrRenderDocError::ModuleNotFound:
            return "renderdoc.dll not loaded in this process (launch via RenderDoc UI)";
        case ZrRenderDocError::ProcNotFound:
            return "renderdoc.dll present but RENDERDOC_GetAPI export missing";
        case ZrRenderDocError::ApiRejected:
            return "RENDERDOC_GetAPI rejected the requested API version";
        case ZrRenderDocError::NotAttached:
            return "RenderDoc API not attached in this process";
        case ZrRenderDocError::CaptureFailed:
            return "EndFrameCapture reported failure (capture not saved)";
        default: return "unknown renderdoc error";
    }
}

ZrRenderDocError ZrRenderDocCapture::AttachFromGetApi(ZrRenderDocGetApiFn getApi)
{
    if (getApi == nullptr) {
        m_lastError = ZrRenderDocError::ApiRejected;
        return m_lastError;
    }
    if (m_api != nullptr) {
        m_lastError = ZrRenderDocError::None; // 幂等：已挂接
        return m_lastError;
    }
    void* table = nullptr;
    // 请求 1.6.0（该单头支持的最高 v1.x；RenderDoc 侧对更高请求给出
    // ≤ 请求版本的最高可用表，见 renderdoc_app.h 头注）
    const uint32_t requested = static_cast<uint32_t>(eRENDERDOC_API_Version_1_6_0);
    getApi(requested, &table);
    if (table == nullptr) {
        m_lastError = ZrRenderDocError::ApiRejected;
        return m_lastError;
    }
    m_api = table;
    m_lastError = ZrRenderDocError::None;
    return m_lastError;
}

ZrRenderDocError ZrRenderDocCapture::Attach()
{
    if (m_api != nullptr) {
        m_lastError = ZrRenderDocError::None; // 幂等
        return m_lastError;
    }
#if defined(_WIN32)
    // 挂接机制（renderdoc_app.h 官方约定）：RenderDoc UI 启动目标程序时把
    // renderdoc.dll 注入进进程；应用侧 GetModuleHandle 探测 + GetProcAddress
    // 取 RENDERDOC_GetAPI 入口。独立启动时模块不存在 -> 明确的 ModuleNotFound。
    HMODULE module = ::GetModuleHandleA("renderdoc.dll");
    if (module == nullptr) {
        m_lastError = ZrRenderDocError::ModuleNotFound;
        return m_lastError;
    }
    void* proc = reinterpret_cast<void*>(::GetProcAddress(module, "RENDERDOC_GetAPI"));
    if (proc == nullptr) {
        m_lastError = ZrRenderDocError::ProcNotFound;
        return m_lastError;
    }
    return AttachFromGetApi(reinterpret_cast<ZrRenderDocGetApiFn>(proc));
#else
    m_lastError = ZrRenderDocError::ModuleNotFound; // 非 Windows：本仓仅 Win32 后端
    return m_lastError;
#endif
}

ZrRenderDocError ZrRenderDocCapture::AttachWithGetApi(ZrRenderDocGetApiFn getApi)
{
    return AttachFromGetApi(getApi);
}

const RENDERDOC_API_1_6_0* AsApi(void* api)
{
    return static_cast<const RENDERDOC_API_1_6_0*>(api);
}

bool ZrRenderDocCapture::SetCaptureFilePathTemplate(const char* pathTemplate)
{
    if (m_api == nullptr) {
        m_lastError = ZrRenderDocError::NotAttached;
        return false;
    }
    AsApi(m_api)->SetCaptureFilePathTemplate(pathTemplate);
    m_lastError = ZrRenderDocError::None;
    return true;
}

bool ZrRenderDocCapture::TriggerCapture()
{
    if (m_api == nullptr) {
        m_lastError = ZrRenderDocError::NotAttached;
        return false;
    }
    AsApi(m_api)->TriggerCapture();
    m_lastError = ZrRenderDocError::None;
    return true;
}

bool ZrRenderDocCapture::StartFrameCapture(void* device, void* window)
{
    if (m_api == nullptr) {
        m_lastError = ZrRenderDocError::NotAttached;
        return false;
    }
    AsApi(m_api)->StartFrameCapture(device, window);
    m_lastError = ZrRenderDocError::None;
    return true;
}

bool ZrRenderDocCapture::EndFrameCapture(void* device, void* window)
{
    if (m_api == nullptr) {
        m_lastError = ZrRenderDocError::NotAttached;
        return false;
    }
    const uint32_t ok = AsApi(m_api)->EndFrameCapture(device, window);
    m_lastError = ok != 0u ? ZrRenderDocError::None : ZrRenderDocError::CaptureFailed;
    return ok != 0u;
}

uint32_t ZrRenderDocCapture::GetNumCaptures() const
{
    if (m_api == nullptr) {
        return 0u;
    }
    return AsApi(m_api)->GetNumCaptures();
}

bool ZrRenderDocCapture::GetCapturePath(uint32_t idx, std::string& outPath) const
{
    if (m_api == nullptr) {
        return false;
    }
    char path[1024] = {};
    uint32_t pathLength = 0u;
    const uint32_t ok = AsApi(m_api)->GetCapture(idx, path, &pathLength, nullptr);
    if (ok == 0u) {
        return false;
    }
    const uint32_t usable = pathLength < sizeof(path) ? pathLength : sizeof(path) - 1u;
    outPath.assign(path, usable);
    return true;
}

} // namespace zrender
