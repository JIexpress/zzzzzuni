// =============================================================================
// test_renderdoc_capture.cpp —— RenderDoc 封装单测（TASK-B-R9-16）
//
// 覆盖简报要求的「renderdoc 命令的参数与错误路径」的封装层部分：
//   1. 未挂接：一切捕获操作 false + NotAttached（明确报错，不静默假成功）；
//   2. GetApi 注入 nullptr -> ApiRejected 且不挂接；
//   3. 假 API 表（经 AttachWithGetApi 接缝，无需安装 RenderDoc）：
//      挂接成功 → SetCaptureFilePathTemplate/TriggerCapture/Start/End 真实转发
//      （假表记录调用），GetNumCaptures/GetCapturePath 回读；
//   4. EndFrameCapture 返回 0 -> CaptureFailed；
//   5. 幂等 Attach；生产入口 Attach() 在未注入进程内返回 ModuleNotFound
//      （本机/CI 均未经 RenderDoc UI 启动 doctest；若真被注入则降级跳过）。
// =============================================================================
#include "doctest/doctest.h"

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#include "renderdoc/renderdoc_app.h"
#include "zrender/renderdoc_capture.h"

namespace {

/// 假 API 表 + 调用记录（只填本封装用到的函数指针；其余保持 nullptr——
/// 封装若错误调用了未设置的槽会直接崩溃，等价于红）
struct FakeRenderDoc {
    RENDERDOC_API_1_6_0 api {};
    std::vector<std::string> calls;
    std::string pathTemplate;
    std::string capturePath = "C:/captures/zframe_frame123.rdc";
    uint32_t numCaptures = 0u;
    uint32_t endFrameResult = 1u;

    FakeRenderDoc()
    {
        RegisterApi();
    }

    /// 清空调用记录与可变状态并重登记函数指针（每个用例独立起点）
    void Reset()
    {
        calls.clear();
        pathTemplate.clear();
        numCaptures = 0u;
        endFrameResult = 1u;
        RegisterApi();
    }

    void RegisterApi()
    {
        api = RENDERDOC_API_1_6_0 {}; // 先整体清零（未登记槽保持 nullptr）
        api.SetCaptureFilePathTemplate = [](const char* pathtemplate) {
            Self()->pathTemplate = pathtemplate;
            Self()->calls.push_back("SetCaptureFilePathTemplate");
        };
        api.TriggerCapture = []() { Self()->calls.push_back("TriggerCapture"); };
        api.StartFrameCapture = [](RENDERDOC_DevicePointer, RENDERDOC_WindowHandle) {
            Self()->calls.push_back("StartFrameCapture");
        };
        api.EndFrameCapture = [](RENDERDOC_DevicePointer, RENDERDOC_WindowHandle) -> uint32_t {
            Self()->calls.push_back("EndFrameCapture");
            return Self()->endFrameResult;
        };
        api.GetNumCaptures = []() -> uint32_t {
            Self()->calls.push_back("GetNumCaptures");
            return Self()->numCaptures;
        };
        api.GetCapture = [](uint32_t idx, char* filename, uint32_t* pathlength,
                            uint64_t*) -> uint32_t {
            if (idx >= Self()->numCaptures) {
                return 0u;
            }
            const uint32_t len = static_cast<uint32_t>(Self()->capturePath.size());
            if (filename != nullptr) {
                std::memcpy(filename, Self()->capturePath.c_str(), len + 1u);
            }
            if (pathlength != nullptr) {
                *pathlength = len;
            }
            return 1u;
        };
    }

    static FakeRenderDoc* Self()
    {
        static FakeRenderDoc instance;
        return &instance;
    }
};

/// GetAPI 假实现：返回假表指针（记录请求版本供断言）
uint32_t g_requestedVersion = 0u;
void* FakeGetApi(uint32_t version, void** outApiPointers)
{
    g_requestedVersion = version;
    *outApiPointers = &FakeRenderDoc::Self()->api;
    return *outApiPointers;
}

void* NullGetApi(uint32_t, void** outApiPointers)
{
    *outApiPointers = nullptr;
    return nullptr;
}

/// 每用例独立假表状态的 RAII 守卫
struct FakeReset {
    FakeReset()
    {
        FakeRenderDoc::Self()->Reset();
        g_requestedVersion = 0u;
    }
};

} // namespace

// ---- 1/2. 错误路径：未挂接 / GetApi 拒绝 -------------------------------------------
TEST_CASE("renderdoc: operations before attach fail with explicit NotAttached")
{
    zrender::ZrRenderDocCapture capture;
    CHECK_FALSE(capture.IsAttached());
    CHECK(capture.LastError() == zrender::ZrRenderDocError::None);

    CHECK_FALSE(capture.TriggerCapture());
    CHECK(capture.LastError() == zrender::ZrRenderDocError::NotAttached);
    CHECK_FALSE(capture.SetCaptureFilePathTemplate("any/dir/capture"));
    CHECK(capture.LastError() == zrender::ZrRenderDocError::NotAttached);
    CHECK_FALSE(capture.StartFrameCapture(nullptr, nullptr));
    CHECK_FALSE(capture.EndFrameCapture(nullptr, nullptr));
    CHECK(capture.GetNumCaptures() == 0u);
    std::string path;
    CHECK_FALSE(capture.GetCapturePath(0u, path));
    // 错误文本非空（DevConsole detail 数据源）
    CHECK(std::string(zrender::ZrRenderDocErrorText(zrender::ZrRenderDocError::NotAttached))
        .find("not attached") != std::string::npos);
    CHECK(std::string(zrender::ZrRenderDocErrorText(zrender::ZrRenderDocError::ModuleNotFound))
        .find("renderdoc.dll") != std::string::npos);
}

TEST_CASE("renderdoc: null GetApi result is ApiRejected and stays unattached")
{
    zrender::ZrRenderDocCapture capture;
    CHECK(capture.AttachWithGetApi(nullptr) == zrender::ZrRenderDocError::ApiRejected);
    CHECK_FALSE(capture.IsAttached());
    CHECK(capture.AttachWithGetApi(&NullGetApi) == zrender::ZrRenderDocError::ApiRejected);
    CHECK_FALSE(capture.IsAttached());
    // 拒绝后捕获操作仍是明确的 NotAttached 路径
    CHECK_FALSE(capture.TriggerCapture());
    CHECK(capture.LastError() == zrender::ZrRenderDocError::NotAttached);
}

// ---- 3/4/5. 挂接成功路径（假表）+ 幂等 + EndFrame 失败 ------------------------------
TEST_CASE("renderdoc: attached via GetApi seam forwards calls and reports captures")
{
    FakeReset reset; // 每用例独立假表状态
    zrender::ZrRenderDocCapture capture;
    REQUIRE(capture.AttachWithGetApi(&FakeGetApi) == zrender::ZrRenderDocError::None);
    CHECK(capture.IsAttached());
    CHECK(g_requestedVersion == 10600u); // eRENDERDOC_API_Version_1_6_0

    // 挂接后错误码归零；路径模板真实转发
    CHECK(capture.LastError() == zrender::ZrRenderDocError::None);
    CHECK(capture.SetCaptureFilePathTemplate("Native/build/renderdoc/zframe"));
    CHECK(FakeRenderDoc::Self()->pathTemplate == "Native/build/renderdoc/zframe");

    // TriggerCapture / 手动 Start-End 转发
    CHECK(capture.TriggerCapture());
    CHECK(capture.StartFrameCapture(nullptr, nullptr));
    CHECK(capture.EndFrameCapture(nullptr, nullptr));
    const std::vector<std::string>& calls = FakeRenderDoc::Self()->calls;
    REQUIRE(calls.size() == 4u);
    CHECK(calls[0] == "SetCaptureFilePathTemplate");
    CHECK(calls[1] == "TriggerCapture");
    CHECK(calls[2] == "StartFrameCapture");
    CHECK(calls[3] == "EndFrameCapture");

    // 捕获回读：假表登记 1 个捕获
    FakeRenderDoc::Self()->numCaptures = 1u;
    CHECK(capture.GetNumCaptures() == 1u);
    std::string path;
    REQUIRE(capture.GetCapturePath(0u, path));
    CHECK(path == "C:/captures/zframe_frame123.rdc");
    CHECK_FALSE(capture.GetCapturePath(1u, path)); // 越界 -> false
    CHECK(capture.LastError() == zrender::ZrRenderDocError::None);

    // EndFrameCapture 返回 0 -> CaptureFailed（捕获未落盘，不假成功）
    FakeRenderDoc::Self()->endFrameResult = 0u;
    CHECK_FALSE(capture.EndFrameCapture(nullptr, nullptr));
    CHECK(capture.LastError() == zrender::ZrRenderDocError::CaptureFailed);

    // 幂等 Attach（已挂接再 Attach 直接 None，不重复取表）
    CHECK(capture.AttachWithGetApi(&FakeGetApi) == zrender::ZrRenderDocError::None);
    CHECK(capture.IsAttached());
}

TEST_CASE("renderdoc: production Attach reports ModuleNotFound when not injected")
{
    // 本封装的生产探测路径：进程内没有 renderdoc.dll -> ModuleNotFound。
    // doctest 进程不由 RenderDoc UI 启动，正常环境必走此分支；万一运行环境
    // 真被注入（renderdoc.dll 在进程内），降级为跳过而非误报。
    zrender::ZrRenderDocCapture capture;
    const zrender::ZrRenderDocError err = capture.Attach();
    if (capture.IsAttached()) {
        MESSAGE("renderdoc.dll present in test process -> skipping ModuleNotFound assertion");
        return;
    }
    CHECK(err == zrender::ZrRenderDocError::ModuleNotFound);
    CHECK_FALSE(capture.IsAttached());
    CHECK_FALSE(capture.TriggerCapture()); // 未挂接捕获仍明确报错
}
