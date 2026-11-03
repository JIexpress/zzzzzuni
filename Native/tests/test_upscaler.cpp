// =============================================================================
// test_upscaler.cpp —— 超分模块单测（TASK-B-R6-01 建 / B-R6-02 扩，doctest）
//
// 覆盖（B-R6-01 简报第 5 点 + B-R6-02 第 4 点）：
//   1. 能力矩阵（mock 设备各 vendor 代际 → 期望后端 supported 判定；含 Dlss 门控）
//   2. 默认优先序（Dlss→Fsr4→Fsr3→Fsr2→Bilinear）与截断
//   3. UpscalerChain 选优：最高位胜出 / 工厂空回退 / Initialize 失败回退 /
//      能力不满足跳过 / 覆盖钉死 / 覆盖失败显式不回退 / 尝试计数 /
//      三后端选优矩阵（DLSS 不可用 → 回退 FSR）
//   4. UpscalerChain 状态机：未初始化 Evaluate/Resize 拒绝、重复初始化拒绝、
//      Shutdown 后拒绝、再初始化恢复
//   5. MV Pass 布局常量（ZrUpscalerMotionVectorConstants == 128B，与
//      motion_vectors.hlsl CBufMotionVectors 镜像）+ MV 像素换算
//   6. FSR jitter 序列（官方公式：phaseCount 与 Halton(2,3) 已知值）
//   7. FFX provider 版本选择（B-R6-02：FSR4 只认 4.x；3.1/2.3 回退链）
//
// mock 设计：每 backend 独立工厂（CreateMockFsr4/Fsr3/Fsr2/Bilinear），
// Initialize 结果按 backend 查表注入（g_mockInitResults）——与链的尝试顺序
// 无关，确定性成立。实例析构自动从登记表摘除（链 Shutdown 会 delete），
// 测试尾部无需手动 delete，杜绝双重释放。
// =============================================================================
#include "doctest/doctest.h"

#include "zupscaler/zupscaler.h"

// TASK-H-02 D4：Bilinear 资源生命周期用例需要一个真实 D3D12 设备。用 WARP
// （软件适配器）——无需真实 GPU/窗口；WARP 不可用时用例打印原因并降级。
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_5.h>

#include <cstddef>
#include <cstring>
#include <map>
#include <string>
#include <vector>

namespace {

/// 测试内版本名拷贝（避免 C4996；/W4 /WX 零警告）
void CopyVersionName(char* dst, const char* src)
{
    const size_t capacity = 32u;
    for (size_t i = 0u; i + 1u < capacity; ++i) {
        dst[i] = src[i];
        if (src[i] == '\0') {
            return;
        }
    }
    dst[capacity - 1u] = '\0';
}

using zr::IUpscaler;
using zr::UpscalerBackend;
using zr::UpscalerBackendEntry;
using zr::UpscalerChain;
using zr::UpscalerDeviceCaps;
using zr::UpscalerInitDesc;
using zr::UpscalerFrameArgs;

// ---- mock 后端（记录调用与尺寸；Initialize 结果按 backend 查表注入）------------
struct MockUpscaler;

/// Initialize 结果注入表（按 backend；缺省 true = 成功）
std::map<UpscalerBackend, bool> g_mockInitResults;

bool MockInitResultFor(UpscalerBackend backend)
{
    const auto it = g_mockInitResults.find(backend);
    return (it == g_mockInitResults.end()) ? true : it->second;
}

std::vector<MockUpscaler*>* MockInstances();

struct MockUpscaler : IUpscaler {
    UpscalerBackend backend = UpscalerBackend::Bilinear;
    bool initialized = false;
    int initCalls = 0;
    int resizeCalls = 0;
    int evalCalls = 0;
    int shutdownCalls = 0;
    uint32_t inW = 0, inH = 0, outW = 0, outH = 0;
    uint32_t resizeInW = 0, resizeInH = 0, resizeOutW = 0, resizeOutH = 0;

    MockUpscaler()
    {
        MockInstances()->push_back(this);
    }
    ~MockUpscaler() override
    {
        auto& v = *MockInstances();
        for (size_t i = 0; i < v.size(); ++i) {
            if (v[i] == this) {
                v.erase(v.begin() + static_cast<std::ptrdiff_t>(i));
                break;
            }
        }
    }

    UpscalerBackend Backend() const override { return backend; }
    const char* BackendVersion() const override { return "mock"; }
    bool Initialize(const UpscalerInitDesc& desc) override
    {
        initCalls += 1;
        if (MockInitResultFor(backend) && desc.nativeDevice != nullptr && desc.inputWidth > 0u
            && desc.inputHeight > 0u && desc.outputWidth > 0u && desc.outputHeight > 0u) {
            initialized = true;
            inW = desc.inputWidth;
            inH = desc.inputHeight;
            outW = desc.outputWidth;
            outH = desc.outputHeight;
            return true;
        }
        return false;
    }
    bool Resize(uint32_t inputWidth, uint32_t inputHeight, uint32_t outputWidth,
        uint32_t outputHeight) override
    {
        resizeCalls += 1;
        if (!initialized) {
            return false;
        }
        resizeInW = inputWidth;
        resizeInH = inputHeight;
        resizeOutW = outputWidth;
        resizeOutH = outputHeight;
        return true;
    }
    bool Evaluate(const UpscalerFrameArgs&) override
    {
        evalCalls += 1;
        return initialized;
    }
    void Shutdown() override
    {
        shutdownCalls += 1;
        initialized = false;
    }
    bool IsInitialized() const override { return initialized; }
};

std::vector<MockUpscaler*>* MockInstances()
{
    static std::vector<MockUpscaler*> instances;
    return &instances;
}

IUpscaler* CreateMockFsr4()
{
    auto* m = new MockUpscaler();
    m->backend = UpscalerBackend::Fsr4;
    return m;
}
IUpscaler* CreateMockDlss()
{
    auto* m = new MockUpscaler();
    m->backend = UpscalerBackend::Dlss;
    return m;
}
IUpscaler* CreateMockFsr3()
{
    auto* m = new MockUpscaler();
    m->backend = UpscalerBackend::Fsr3;
    return m;
}
IUpscaler* CreateMockFsr2()
{
    auto* m = new MockUpscaler();
    m->backend = UpscalerBackend::Fsr2;
    return m;
}
IUpscaler* CreateMockBilinear()
{
    auto* m = new MockUpscaler();
    m->backend = UpscalerBackend::Bilinear;
    return m;
}

/// 逐后端 supported 适配（UpscalerSupportedFn 签名 = (caps)；用真实能力矩阵）
bool SupportedMockDlss(const UpscalerDeviceCaps& caps)
{
    return zr::UpscalerBackendSupported(UpscalerBackend::Dlss, caps);
}
bool SupportedMockFsr4(const UpscalerDeviceCaps& caps)
{
    return zr::UpscalerBackendSupported(UpscalerBackend::Fsr4, caps);
}
bool SupportedMockFsr3(const UpscalerDeviceCaps& caps)
{
    return zr::UpscalerBackendSupported(UpscalerBackend::Fsr3, caps);
}
bool SupportedMockFsr2(const UpscalerDeviceCaps& caps)
{
    return zr::UpscalerBackendSupported(UpscalerBackend::Fsr2, caps);
}
bool SupportedMockBilinear(const UpscalerDeviceCaps& caps)
{
    return zr::UpscalerBackendSupported(UpscalerBackend::Bilinear, caps);
}

/// 构造 mock 条目（supported 用真实能力矩阵谓词——矩阵语义随之被测）
UpscalerBackendEntry MakeMockEntry(UpscalerBackend backend)
{
    UpscalerBackendEntry entry {};
    entry.backend = backend;
    switch (backend) {
    case UpscalerBackend::Dlss:
        entry.supported = &SupportedMockDlss;
        entry.create = &CreateMockDlss;
        break;
    case UpscalerBackend::Fsr4:
        entry.supported = &SupportedMockFsr4;
        entry.create = &CreateMockFsr4;
        break;
    case UpscalerBackend::Fsr3:
        entry.supported = &SupportedMockFsr3;
        entry.create = &CreateMockFsr3;
        break;
    case UpscalerBackend::Fsr2:
        entry.supported = &SupportedMockFsr2;
        entry.create = &CreateMockFsr2;
        break;
    default:
        entry.supported = &SupportedMockBilinear;
        entry.create = &CreateMockBilinear;
        break;
    }
    return entry;
}

UpscalerDeviceCaps MakeCaps(bool runtime, bool fsr4, uint32_t vendorId = 0x1002u)
{
    UpscalerDeviceCaps caps;
    caps.vendorId = vendorId;
    caps.deviceId = 0x7480u;
    caps.fsrRuntimePresent = runtime;
    caps.fsr4Available = fsr4;
    caps.nvidiaHardware = (vendorId == 0x10DEu);
    caps.dlssRuntimePresent = false;
    return caps;
}

/// NVIDIA 机型：dlssRuntime 控制 Streamline 运行时是否齐备
UpscalerDeviceCaps MakeNvidiaCaps(bool dlssRuntime, bool fsrRuntime = false, bool fsr4 = false)
{
    UpscalerDeviceCaps caps = MakeCaps(fsrRuntime, fsr4, 0x10DEu);
    caps.dlssRuntimePresent = dlssRuntime;
    return caps;
}

UpscalerInitDesc MakeInitDesc()
{
    UpscalerInitDesc desc;
    desc.inputWidth = 960u;
    desc.inputHeight = 540u;
    desc.outputWidth = 1280u;
    desc.outputHeight = 720u;
    desc.cameraFovY = 1.0472f;
    desc.cameraNear = 0.3f;
    desc.cameraFar = 60.0f;
    desc.nativeDevice = reinterpret_cast<void*>(static_cast<std::uintptr_t>(0x1234)); // 非空哨兵
    return desc;
}

} // namespace

// =============================================================================
// 1. 能力矩阵（各 vendor 代际 → 期望后端）
// =============================================================================
TEST_CASE("UpscalerBackendSupported: capability matrix")
{
    // AMD + 运行时 + FSR4 provider：FSR 家族全可用（Dlss 无 N 卡恒 false）
    {
        const auto caps = MakeCaps(true, true);
        CHECK(zr::UpscalerBackendSupported(UpscalerBackend::Dlss, caps) == false);
        CHECK(zr::UpscalerBackendSupported(UpscalerBackend::Fsr4, caps));
        CHECK(zr::UpscalerBackendSupported(UpscalerBackend::Fsr3, caps));
        CHECK(zr::UpscalerBackendSupported(UpscalerBackend::Fsr2, caps));
        CHECK(zr::UpscalerBackendSupported(UpscalerBackend::Bilinear, caps));
    }
    // AMD 780M（RDNA3 iGPU，provider 未枚举 4.x）：Fsr4 拒绝 → 回落 Fsr3
    {
        const auto caps = MakeCaps(true, false);
        CHECK_FALSE(zr::UpscalerBackendSupported(UpscalerBackend::Fsr4, caps));
        CHECK(zr::UpscalerBackendSupported(UpscalerBackend::Fsr3, caps));
        CHECK(zr::UpscalerBackendSupported(UpscalerBackend::Fsr2, caps));
        CHECK(zr::UpscalerBackendSupported(UpscalerBackend::Bilinear, caps));
    }
    // NVIDIA + Streamline 运行时：Dlss 可用（FSR 也可用，优先序 Dlss 更高）
    {
        const auto caps = MakeNvidiaCaps(true, true, false);
        CHECK(zr::UpscalerBackendSupported(UpscalerBackend::Dlss, caps));
        CHECK(zr::UpscalerBackendSupported(UpscalerBackend::Fsr3, caps));
        CHECK_FALSE(zr::UpscalerBackendSupported(UpscalerBackend::Fsr4, caps));
    }
    // NVIDIA 但 Streamline 运行时缺失：Dlss 拒绝（门控第二条件）
    {
        const auto caps = MakeNvidiaCaps(false, true, false);
        CHECK_FALSE(zr::UpscalerBackendSupported(UpscalerBackend::Dlss, caps));
        CHECK(zr::UpscalerBackendSupported(UpscalerBackend::Fsr3, caps));
    }
    // AMD 即便误报 dlssRuntime：nvidiaHardware=false → Dlss 仍拒绝
    {
        auto caps = MakeCaps(true, false);
        caps.dlssRuntimePresent = true;
        CHECK_FALSE(zr::UpscalerBackendSupported(UpscalerBackend::Dlss, caps));
    }
    // Intel 无运行时：仅 Bilinear
    {
        const auto caps = MakeCaps(false, false, 0x8086u);
        CHECK_FALSE(zr::UpscalerBackendSupported(UpscalerBackend::Dlss, caps));
        CHECK_FALSE(zr::UpscalerBackendSupported(UpscalerBackend::Fsr4, caps));
        CHECK_FALSE(zr::UpscalerBackendSupported(UpscalerBackend::Fsr3, caps));
        CHECK_FALSE(zr::UpscalerBackendSupported(UpscalerBackend::Fsr2, caps));
        CHECK(zr::UpscalerBackendSupported(UpscalerBackend::Bilinear, caps));
    }
    // 运行时缺位时即便 fsr4Available 误报也不允许 Fsr4（组合门控）
    {
        const auto caps = MakeCaps(false, false);
        CHECK_FALSE(zr::UpscalerBackendSupported(UpscalerBackend::Fsr4, caps));
    }
}

TEST_CASE("UpscalerBackendName: stable names")
{
    CHECK(std::string(zr::UpscalerBackendName(UpscalerBackend::Bilinear)) == "Bilinear");
    CHECK(std::string(zr::UpscalerBackendName(UpscalerBackend::Fsr2)) == "Fsr2");
    CHECK(std::string(zr::UpscalerBackendName(UpscalerBackend::Fsr3)) == "Fsr3");
    CHECK(std::string(zr::UpscalerBackendName(UpscalerBackend::Fsr4)) == "Fsr4");
    CHECK(std::string(zr::UpscalerBackendName(UpscalerBackend::Dlss)) == "Dlss");
    // 枚举值稳定（C ABI 映射依赖：pref = 值 + 1）
    CHECK(static_cast<uint32_t>(UpscalerBackend::Bilinear) == 0u);
    CHECK(static_cast<uint32_t>(UpscalerBackend::Fsr4) == 3u);
    CHECK(static_cast<uint32_t>(UpscalerBackend::Dlss) == 4u);
}

// =============================================================================
// 2. 默认优先序
// =============================================================================
TEST_CASE("UpscalerDefaultPriority: Dlss -> Fsr4 -> Fsr3 -> Fsr2 -> Bilinear")
{
    zr::UpscalerBackend order[8] = {};
    const uint32_t n = zr::UpscalerDefaultPriority(order, 8u);
    REQUIRE(n == 5u);
    CHECK(order[0] == UpscalerBackend::Dlss);
    CHECK(order[1] == UpscalerBackend::Fsr4);
    CHECK(order[2] == UpscalerBackend::Fsr3);
    CHECK(order[3] == UpscalerBackend::Fsr2);
    CHECK(order[4] == UpscalerBackend::Bilinear);

    // 截断：capacity=2 只取前两个
    zr::UpscalerBackend truncated[2] = {};
    CHECK(zr::UpscalerDefaultPriority(truncated, 2u) == 2u);
    CHECK(truncated[0] == UpscalerBackend::Dlss);
    CHECK(truncated[1] == UpscalerBackend::Fsr4);
}

// =============================================================================
// 3. Chain 选优
// =============================================================================
TEST_CASE("UpscalerChain: auto select picks highest priority supported backend")
{
    UpscalerChain chain;
    chain.ClearBackends();
    chain.RegisterBackend(MakeMockEntry(UpscalerBackend::Bilinear));
    chain.RegisterBackend(MakeMockEntry(UpscalerBackend::Fsr3));
    chain.RegisterBackend(MakeMockEntry(UpscalerBackend::Fsr4));

    // RDNA4：Fsr4 胜出，仅尝试 1 次
    REQUIRE(chain.SelectAndInitialize(MakeCaps(true, true), MakeInitDesc()));
    CHECK(chain.ActiveBackend() == UpscalerBackend::Fsr4);
    CHECK(chain.LastAttemptCount() == 1u);
    CHECK(chain.IsInitialized());

    // 后端实例确实收到初始化尺寸
    REQUIRE(MockInstances()->size() == 1u);
    CHECK(MockInstances()->back()->inW == 960u);
    CHECK(MockInstances()->back()->outH == 720u);

    chain.Shutdown(); // 实例随 Shutdown 释放（析构自登记表摘除）
    CHECK(MockInstances()->empty());
}

TEST_CASE("UpscalerChain: null factory falls back to next candidate")
{
    UpscalerChain chain;
    chain.ClearBackends();
    chain.RegisterBackend(MakeMockEntry(UpscalerBackend::Fsr3));
    chain.RegisterBackend(MakeMockEntry(UpscalerBackend::Bilinear));

    // Fsr4 占位槽位（工厂 null，RDNA4 caps）：计入尝试、直接回退 Fsr3
    UpscalerBackendEntry fsr4Stub {};
    fsr4Stub.backend = UpscalerBackend::Fsr4;
    fsr4Stub.supported = &SupportedMockFsr4;
    fsr4Stub.create = nullptr;
    chain.RegisterBackend(fsr4Stub);

    REQUIRE(chain.SelectAndInitialize(MakeCaps(true, true), MakeInitDesc()));
    CHECK(chain.ActiveBackend() == UpscalerBackend::Fsr3);
    CHECK(chain.LastAttemptCount() == 2u); // Fsr4 尝试（工厂 null）+ Fsr3 成功
    chain.Shutdown();
    CHECK(MockInstances()->empty());
}

TEST_CASE("UpscalerChain: init failure falls back to next candidate")
{
    UpscalerChain chain;
    chain.ClearBackends();
    chain.RegisterBackend(MakeMockEntry(UpscalerBackend::Fsr3));
    chain.RegisterBackend(MakeMockEntry(UpscalerBackend::Fsr2));
    chain.RegisterBackend(MakeMockEntry(UpscalerBackend::Bilinear));
    g_mockInitResults[UpscalerBackend::Fsr3] = false; // 注入 Fsr3 初始化失败

    REQUIRE(chain.SelectAndInitialize(MakeCaps(true, false), MakeInitDesc()));
    CHECK(chain.ActiveBackend() == UpscalerBackend::Fsr2);
    CHECK(chain.LastAttemptCount() == 2u);
    chain.Shutdown();
    CHECK(MockInstances()->empty());
    g_mockInitResults.clear();
}

TEST_CASE("UpscalerChain: unsupported candidates skipped (attempt count reflects reality)")
{
    UpscalerChain chain;
    chain.ClearBackends();
    chain.RegisterBackend(MakeMockEntry(UpscalerBackend::Fsr3));
    chain.RegisterBackend(MakeMockEntry(UpscalerBackend::Bilinear));

    // 无运行时：Fsr3 被能力矩阵拒绝（不算尝试）→ Bilinear
    REQUIRE(chain.SelectAndInitialize(MakeCaps(false, false), MakeInitDesc()));
    CHECK(chain.ActiveBackend() == UpscalerBackend::Bilinear);
    CHECK(chain.LastAttemptCount() == 1u);
    chain.Shutdown();
    CHECK(MockInstances()->empty());
}

TEST_CASE("UpscalerChain: override pins selection and fails explicitly without fallback")
{
    UpscalerChain chain;
    chain.ClearBackends();
    chain.RegisterBackend(MakeMockEntry(UpscalerBackend::Fsr3));
    chain.RegisterBackend(MakeMockEntry(UpscalerBackend::Bilinear));

    // 覆盖 Bilinear：即使更高位可用也只走基线（--fsr off 语义）
    chain.SetOverride(UpscalerBackend::Bilinear);
    REQUIRE(chain.SelectAndInitialize(MakeCaps(true, false), MakeInitDesc()));
    CHECK(chain.ActiveBackend() == UpscalerBackend::Bilinear);
    CHECK(chain.LastAttemptCount() == 1u);
    chain.Shutdown();

    // 覆盖 Fsr3 但运行时缺位：显式失败、不静默回退 Bilinear
    chain.ClearOverride();
    chain.SetOverride(UpscalerBackend::Fsr3);
    CHECK_FALSE(chain.SelectAndInitialize(MakeCaps(false, false), MakeInitDesc()));
    CHECK_FALSE(chain.IsInitialized());

    // 清除覆盖 → 自动模式恢复（Bilinear 可用）
    chain.ClearOverride();
    REQUIRE(chain.SelectAndInitialize(MakeCaps(false, false), MakeInitDesc()));
    CHECK(chain.ActiveBackend() == UpscalerBackend::Bilinear);

    chain.Shutdown();
    CHECK(MockInstances()->empty());
}

// =============================================================================
// 4. Chain 状态机
// =============================================================================
TEST_CASE("UpscalerChain: state machine guards and transitions")
{
    UpscalerChain chain;
    chain.ClearBackends();
    chain.RegisterBackend(MakeMockEntry(UpscalerBackend::Fsr3));

    // 未初始化：Evaluate/Resize 拒绝
    UpscalerFrameArgs args;
    CHECK_FALSE(chain.Evaluate(args));
    CHECK_FALSE(chain.Resize(480u, 270u, 640u, 360u));

    // 初始化 → Evaluate 通过、调用计数 1
    REQUIRE(chain.SelectAndInitialize(MakeCaps(true, false), MakeInitDesc()));
    REQUIRE(MockInstances()->size() == 1u);
    MockUpscaler* active = MockInstances()->back();
    CHECK(chain.Evaluate(args));
    CHECK(active->evalCalls == 1);

    // Resize 通过且转发尺寸
    CHECK(chain.Resize(480u, 270u, 640u, 360u));
    CHECK(active->resizeCalls == 1);
    CHECK(active->resizeInW == 480u);
    CHECK(active->resizeOutH == 360u);

    // 已初始化重复 SelectAndInitialize 拒绝（须先 Shutdown）
    CHECK_FALSE(chain.SelectAndInitialize(MakeCaps(true, false), MakeInitDesc()));
    CHECK(chain.IsInitialized());

    // Shutdown → Evaluate/Resize 拒绝、IsInitialized false、Shutdown 转发 1 次
    chain.Shutdown();
    CHECK_FALSE(chain.IsInitialized());
    CHECK_FALSE(chain.Evaluate(args));
    CHECK_FALSE(chain.Resize(480u, 270u, 640u, 360u));
    CHECK(active->shutdownCalls == 1);

    // 再初始化恢复（状态机闭环）
    REQUIRE(chain.SelectAndInitialize(MakeCaps(true, false), MakeInitDesc()));
    CHECK(chain.IsInitialized());
    CHECK(chain.Evaluate(args));

    // 析构兜底 Shutdown（无泄漏/无双重释放——析构路径覆盖）
    chain.Shutdown();
    CHECK(MockInstances()->empty());
}

TEST_CASE("UpscalerChain: three-backend selection matrix (Dlss -> Fsr4 -> Fsr3 -> Bilinear)")
{
    // 三后端（Dlss/Fsr4/Fsr3）+ 基线，按设备能力逐格断言选优结果。
    struct Case {
        const char* name;
        UpscalerDeviceCaps caps;
        UpscalerBackend expected;
    };
    const Case cases[] = {
        {"NVIDIA+DLSS+FSR", MakeNvidiaCaps(true, true, false), UpscalerBackend::Dlss},
        // N 卡但 Streamline 运行时缺失 → 跳过 Dlss，落 Fsr3
        {"NVIDIA no-SL runtime", MakeNvidiaCaps(false, true, false), UpscalerBackend::Fsr3},
        // AMD 780M：Fsr4 provider 未枚举 → Fsr3
        {"AMD 780M (no FSR4)", MakeCaps(true, false), UpscalerBackend::Fsr3},
        // AMD + FSR4 provider（RX 9000/7000 离散卡）→ Fsr4
        {"AMD with FSR4 provider", MakeCaps(true, true), UpscalerBackend::Fsr4},
        // 无 FSR 运行时 → 基线
        {"no runtime", MakeCaps(false, false, 0x8086u), UpscalerBackend::Bilinear},
    };

    for (const Case& c : cases) {
        UpscalerChain chain;
        chain.ClearBackends();
        chain.RegisterBackend(MakeMockEntry(UpscalerBackend::Bilinear));
        chain.RegisterBackend(MakeMockEntry(UpscalerBackend::Fsr3));
        chain.RegisterBackend(MakeMockEntry(UpscalerBackend::Fsr4));
        chain.RegisterBackend(MakeMockEntry(UpscalerBackend::Dlss));
        CAPTURE(c.name);
        REQUIRE(chain.SelectAndInitialize(c.caps, MakeInitDesc()));
        CHECK(chain.ActiveBackend() == c.expected);
        chain.Shutdown();
        CHECK(MockInstances()->empty());
    }
}

TEST_CASE("UpscalerChain: Dlss factory unavailable (no N card) falls back without error")
{
    // 默认注册表（Dlss 真工厂 + Fsr4 真工厂 + Fsr3/Fsr2 + Bilinear）：
    // 本机无 Streamline 运行时 → CreateDlssStreamlineUpscaler 返回 nullptr，
    // 能力矩阵也先拒；AMD 780M 上最终落到 Fsr3（不报错、不崩溃）。
    UpscalerChain chain;
    chain.ClearBackends();
    UpscalerBackendEntry dlss {};
    dlss.backend = UpscalerBackend::Dlss;
    dlss.supported = &SupportedMockDlss;
    dlss.create = &CreateMockDlss; // 工厂可用但能力矩阵拒绝（等价于无 N 卡）
    chain.RegisterBackend(dlss);
    chain.RegisterBackend(MakeMockEntry(UpscalerBackend::Fsr3));
    chain.RegisterBackend(MakeMockEntry(UpscalerBackend::Bilinear));

    REQUIRE(chain.SelectAndInitialize(MakeCaps(true, false), MakeInitDesc()));
    CHECK(chain.ActiveBackend() == UpscalerBackend::Fsr3);
    CHECK(chain.LastAttemptCount() == 1u); // Dlss 被能力矩阵拒绝（不计尝试）
    chain.Shutdown();
    CHECK(MockInstances()->empty());
}

// =============================================================================
// 5. MV Pass 布局常量与像素换算
// =============================================================================
TEST_CASE("Motion vector constants: 128B layout mirrors motion_vectors.hlsl")
{
    CHECK(sizeof(zr::ZrUpscalerMotionVectorConstants) == 128u);
    CHECK(offsetof(zr::ZrUpscalerMotionVectorConstants, mvpCur) == 0u);
    CHECK(offsetof(zr::ZrUpscalerMotionVectorConstants, mvpPrev) == 64u);
}

TEST_CASE("ZupMotionVectorScale: NDC delta to pixel units (y down)")
{
    float scale[2] = {};
    zr::ZupMotionVectorScale(960u, 540u, scale);
    CHECK(scale[0] == doctest::Approx(480.0));
    CHECK(scale[1] == doctest::Approx(-270.0)); // y 取反：D3D12 视口 y 向下
    zr::ZupMotionVectorScale(1920u, 1080u, scale);
    CHECK(scale[0] == doctest::Approx(960.0));
    CHECK(scale[1] == doctest::Approx(-540.0));
}

// =============================================================================
// 6. FSR jitter 序列（官方公式已知值）
// =============================================================================
TEST_CASE("ZupJitterPhaseCount: ceil(8 * display / render)")
{
    CHECK(zr::ZupJitterPhaseCount(960u, 1280u) == 11u); // ceil(10.667)
    CHECK(zr::ZupJitterPhaseCount(960u, 960u) == 8u);
    CHECK(zr::ZupJitterPhaseCount(960u, 1920u) == 16u);
    CHECK(zr::ZupJitterPhaseCount(0u, 1280u) == 1u); // 退化输入守卫
    CHECK(zr::ZupJitterPhaseCount(960u, 0u) == 1u);
}

TEST_CASE("ZupJitterOffset: Halton(2,3) known values and phase wrap")
{
    float x = 99.0f;
    float y = 99.0f;
    zr::ZupJitterOffset(0u, 8u, &x, &y);
    CHECK(x == doctest::Approx(0.0));        // halton(1,2)=0.5
    CHECK(y == doctest::Approx(-1.0 / 6.0)); // halton(1,3)=1/3

    zr::ZupJitterOffset(1u, 8u, &x, &y);
    CHECK(x == doctest::Approx(-0.25));     // halton(2,2)=0.25
    CHECK(y == doctest::Approx(1.0 / 6.0)); // halton(2,3)=2/3

    // 相位回绕：index % phaseCount
    float a = 0.0f;
    float b = 0.0f;
    zr::ZupJitterOffset(8u, 8u, &a, &b);
    zr::ZupJitterOffset(0u, 8u, &x, &y);
    CHECK(a == doctest::Approx(x));
    CHECK(b == doctest::Approx(y));
}

// =============================================================================
// 7. 内置 Bilinear 工厂（无 D3D12 设备时 Initialize 须明确失败——不崩溃）
// =============================================================================
TEST_CASE("CreateBilinearUpscaler: instance available, init fails cleanly without device")
{
    IUpscaler* up = zr::CreateBilinearUpscaler();
    REQUIRE(up != nullptr);
    CHECK(up->Backend() == UpscalerBackend::Bilinear);

    UpscalerInitDesc desc = MakeInitDesc();
    desc.nativeDevice = nullptr; // 无设备
    CHECK_FALSE(up->Initialize(desc));
    CHECK_FALSE(up->IsInitialized());
    CHECK_FALSE(up->Evaluate(UpscalerFrameArgs{}));
    up->Shutdown(); // 幂等
    delete up;
}

// =============================================================================
// 8. FFX provider 版本选择（B-R6-02：FSR4 能力查询 mock 分支）
// =============================================================================
TEST_CASE("ZupSelectProviderVersion: Fsr4 requires a 4.x provider (780M list rejected)")
{
    // 本机真实枚举结果：只有 3.1.5 / 2.3.4
    zr::ZupProviderVersion providers[2] = {};
    providers[0].versionId = 0xF5A5CA1E00C01005ull;
    CopyVersionName(providers[0].name, "3.1.5");
    providers[1].versionId = 0xF5A5CA1E00803004ull;
    CopyVersionName(providers[1].name, "2.3.4");

    uint64_t id = 0xDEADBEEFull;
    char name[32] = "unchanged";
    CHECK_FALSE(zr::ZupSelectProviderVersion(
        UpscalerBackend::Fsr4, providers, 2u, &id, name, sizeof(name)));
    CHECK(id == 0xDEADBEEFull); // 未命中不得改写输出
    CHECK(std::string(name) == "unchanged");

    // Fsr3 在同一列表上命中 3.1.5
    REQUIRE(zr::ZupSelectProviderVersion(
        UpscalerBackend::Fsr3, providers, 2u, &id, name, sizeof(name)));
    CHECK(id == 0xF5A5CA1E00C01005ull);
    CHECK(std::string(name) == "3.1.5");

    // Fsr2 命中 2.3.4
    REQUIRE(zr::ZupSelectProviderVersion(
        UpscalerBackend::Fsr2, providers, 2u, &id, name, sizeof(name)));
    CHECK(id == 0xF5A5CA1E00803004ull);
}

TEST_CASE("ZupSelectProviderVersion: Fsr4 picks 4.1.1 when the provider is enumerated")
{
    // RX 9000/7000 离散卡上的期望列表（mock）：4.1.1 与 3.1.5 并存
    zr::ZupProviderVersion providers[3] = {};
    providers[0].versionId = 0xF5A5CA1E00C01005ull;
    CopyVersionName(providers[0].name, "3.1.5");
    providers[1].versionId = 0xF5A5CA1E01001001ull;
    CopyVersionName(providers[1].name, "4.1.1");
    providers[2].versionId = 0xF5A5CA1E00803004ull;
    CopyVersionName(providers[2].name, "2.3.4");

    uint64_t id = 0u;
    char name[32] = {};
    REQUIRE(zr::ZupSelectProviderVersion(
        UpscalerBackend::Fsr4, providers, 3u, &id, name, sizeof(name)));
    CHECK(id == 0xF5A5CA1E01001001ull);
    CHECK(std::string(name) == "4.1.1");

    // 4.x 优先于 3.1（枚举顺序无关：把 4.x 放最后）
    zr::ZupProviderVersion reordered[3] = {providers[0], providers[2], providers[1]};
    REQUIRE(zr::ZupSelectProviderVersion(
        UpscalerBackend::Fsr4, reordered, 3u, &id, name, sizeof(name)));
    CHECK(std::string(name) == "4.1.1");
}

TEST_CASE("ZupSelectProviderVersion: non-FFX backends and empty input are rejected")
{
    zr::ZupProviderVersion providers[1] = {};
    providers[0].versionId = 1u;
    CopyVersionName(providers[0].name, "4.1.1");
    uint64_t id = 0u;
    char name[32] = {};
    CHECK_FALSE(zr::ZupSelectProviderVersion(
        UpscalerBackend::Bilinear, providers, 1u, &id, name, sizeof(name)));
    CHECK_FALSE(zr::ZupSelectProviderVersion(
        UpscalerBackend::Dlss, providers, 1u, &id, name, sizeof(name)));
    CHECK_FALSE(zr::ZupSelectProviderVersion(
        UpscalerBackend::Fsr4, providers, 0u, &id, name, sizeof(name)));
    CHECK_FALSE(zr::ZupSelectProviderVersion(
        UpscalerBackend::Fsr4, nullptr, 3u, &id, name, sizeof(name)));
    // 输出参数可为空（仅探测用）
    CHECK(zr::ZupSelectProviderVersion(
        UpscalerBackend::Fsr4, providers, 1u, nullptr, nullptr, 0u));
}

TEST_CASE("CreateFsr4Upscaler: instance available, init fails cleanly without device")
{
    // 真工厂（无设备 → 明确失败，不崩溃）；DLSS 工厂在无运行时时返回 nullptr
    IUpscaler* up = zr::CreateFsr4Upscaler();
    REQUIRE(up != nullptr);
    CHECK(up->Backend() == UpscalerBackend::Fsr4);
    UpscalerInitDesc desc = MakeInitDesc();
    desc.nativeDevice = nullptr;
    CHECK_FALSE(up->Initialize(desc));
    CHECK_FALSE(up->IsInitialized());
    delete up;

    CHECK(zr::CreateDlssStreamlineUpscaler() == nullptr); // 本机无 Streamline 运行时
    CHECK_FALSE(zr::ZupDlssRuntimeProbe());
    CHECK(std::string(zr::ZupDlssRuntimeVersion()).find("Streamline") != std::string::npos);
}

// =============================================================================
// 9. Bilinear 资源生命周期（TASK-H-02 D4：中途失败泄漏 / 持久映射未解除）
// =============================================================================
namespace {

/// WARP 设备（软件适配器；进程内单例，不主动销毁——避免与用例顺序耦合）
ID3D12Device* AcquireWarpDevice()
{
    static ID3D12Device* device = nullptr;
    static bool tried = false;
    if (tried) {
        return device;
    }
    tried = true;
    IDXGIFactory4* factory = nullptr;
    if (FAILED(::CreateDXGIFactory1(IID_PPV_ARGS(&factory))) || factory == nullptr) {
        return nullptr;
    }
    IDXGIAdapter* warp = nullptr;
    if (SUCCEEDED(factory->EnumWarpAdapter(IID_PPV_ARGS(&warp))) && warp != nullptr) {
        ::D3D12CreateDevice(warp, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device));
        warp->Release();
    }
    factory->Release();
    return device;
}

/// 当前引用计数（AddRef + Release 抵消：读取而不改变）
ULONG RefCountOf(IUnknown* obj)
{
    obj->AddRef();
    return obj->Release();
}

/// 与 Native/shaders/upsample_bilinear.hlsl 同构的最小可编译源（vs_5_0/ps_5_0）
const char* const kTestBilinearHlsl = R"(
cbuffer CBufBilinear : register(b0)
{
    float4 srcTexel;
};
Texture2D srcTex : register(t0);
SamplerState srcSampler : register(s0);
struct PsIn
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
};
PsIn VsMain(uint vid : SV_VertexID)
{
    PsIn output;
    const float2 uv = float2((vid << 1u) & 2u, vid & 2u);
    output.pos = float4(uv.x * 2.0f - 1.0f, 1.0f - uv.y * 2.0f, 0.0f, 1.0f);
    output.uv = uv;
    return output;
}
float4 PsMain(PsIn input) : SV_Target
{
    return srcTex.SampleLevel(srcSampler, input.uv, 0.0f);
}
)";

/// PSO 创建失败的注入源：VS 无 SV_Position 输出（管线结构非法）→ WARP 上
/// CreateGraphicsPipelineState 稳定返回 E_INVALIDARG。失败点正好在根签名创建之后、
/// CB/堆之前，落在 D4 的"中途失败"泄漏路径上。
const char* const kTestBilinearHlslBrokenPipeline = R"(
void VsMain(uint vid : SV_VertexID)
{
    // 故意不输出 SV_Position（PSO 结构非法）
}
float4 PsMain() : SV_Target
{
    return float4(1.0f, 0.0f, 0.0f, 1.0f);
}
)";

UpscalerInitDesc MakeBilinearDesc(ID3D12Device* device, uint32_t rtvFormat)
{
    UpscalerInitDesc desc = MakeInitDesc();
    desc.nativeDevice = device;
    desc.outputDxgiFormat = rtvFormat;
    desc.bilinearHlsl = kTestBilinearHlsl;
    desc.bilinearHlslSize = static_cast<uint32_t>(std::strlen(kTestBilinearHlsl));
    return desc;
}

} // namespace

TEST_CASE("BilinearUpscaler: 中途失败不留资源 / 重复 Initialize 不叠加（TASK-H-02 D4）")
{
    ID3D12Device* device = AcquireWarpDevice();
    if (device == nullptr) {
        MESSAGE("WARP D3D12 device unavailable -> D4 资源生命周期用例降级（未验证）");
        return;
    }
    const ULONG baseline = RefCountOf(device);
    IUpscaler* up = zr::CreateBilinearUpscaler();
    REQUIRE(up != nullptr);

    // (1) PSO 创建失败（VS 无 SV_Position）→ 根签名已建、PSO 未建；重复 4 次失败
    //     初始化不得累加设备引用（修复前每次覆盖 m_rootSignature 指针 → 泄漏）
    {
        UpscalerInitDesc broken
            = MakeBilinearDesc(device, static_cast<uint32_t>(DXGI_FORMAT_R8G8B8A8_UNORM));
        broken.bilinearHlsl = kTestBilinearHlslBrokenPipeline;
        broken.bilinearHlslSize = static_cast<uint32_t>(std::strlen(kTestBilinearHlslBrokenPipeline));
        for (int i = 0; i < 4; ++i) {
            CHECK_FALSE(up->Initialize(broken));
            CHECK_FALSE(up->IsInitialized());
        }
        CHECK(RefCountOf(device) == baseline);
    }

    // (2) 失败后再成功：合法格式 → Initialize OK；Shutdown 释放全部资源
    {
        const UpscalerInitDesc good
            = MakeBilinearDesc(device, static_cast<uint32_t>(DXGI_FORMAT_R8G8B8A8_UNORM));
        REQUIRE(up->Initialize(good));
        CHECK(up->IsInitialized());
        CHECK(RefCountOf(device) > baseline); // 资源确持设备引用（用例有判别力）
        up->Shutdown();
        CHECK_FALSE(up->IsInitialized());
        CHECK(RefCountOf(device) == baseline);
        up->Shutdown(); // 幂等
        CHECK(RefCountOf(device) == baseline);
    }

    // (3) 重建循环：Initialize → Shutdown 三轮，每轮引用计数回到基线（含 CB 解映射）
    {
        const UpscalerInitDesc good
            = MakeBilinearDesc(device, static_cast<uint32_t>(DXGI_FORMAT_R8G8B8A8_UNORM));
        for (int i = 0; i < 3; ++i) {
            REQUIRE(up->Initialize(good));
            CHECK(RefCountOf(device) > baseline);
            up->Shutdown();
            CHECK(RefCountOf(device) == baseline);
        }
    }

    delete up;
    CHECK(RefCountOf(device) == baseline);
}

// =============================================================================
// 10. Resize 输出上限（TASK-H-02 D2：目标输出超创建上限会写越分配的输出资源）
// =============================================================================
TEST_CASE("ZupResizeWithinLimits: 目标输出超出创建时上限被拒（TASK-H-02 D2）")
{
    // 创建时上限：输入 960×540、输出 1280×720（FSR 上下文按此分配资源）
    const uint32_t maxInW = 960u, maxInH = 540u, maxOutW = 1280u, maxOutH = 720u;
    CHECK(zr::ZupResizeWithinLimits(960u, 540u, 1280u, 720u, maxInW, maxInH, maxOutW, maxOutH));
    // 上限内缩小的动态分辨率：合法
    CHECK(zr::ZupResizeWithinLimits(640u, 360u, 1280u, 720u, maxInW, maxInH, maxOutW, maxOutH));
    // 恰好等于上限：合法（边界包含）
    CHECK(zr::ZupResizeWithinLimits(960u, 540u, 1280u, 720u, maxInW, maxInH, maxOutW, maxOutH));

    // 输出超上限（缺陷本体）：1280×720 → 1920×1080 / 3840×2160 / 单轴超限
    CHECK_FALSE(zr::ZupResizeWithinLimits(960u, 540u, 1920u, 1080u, maxInW, maxInH, maxOutW, maxOutH));
    CHECK_FALSE(zr::ZupResizeWithinLimits(960u, 540u, 3840u, 2160u, maxInW, maxInH, maxOutW, maxOutH));
    CHECK_FALSE(zr::ZupResizeWithinLimits(960u, 540u, 1280u, 2160u, maxInW, maxInH, maxOutW, maxOutH));
    CHECK_FALSE(zr::ZupResizeWithinLimits(960u, 540u, 3840u, 720u, maxInW, maxInH, maxOutW, maxOutH));

    // 输入超上限
    CHECK_FALSE(zr::ZupResizeWithinLimits(1280u, 720u, 1280u, 720u, maxInW, maxInH, maxOutW, maxOutH));
    // 下采样（超分后端不接受）
    CHECK_FALSE(zr::ZupResizeWithinLimits(960u, 540u, 640u, 360u, maxInW, maxInH, maxOutW, maxOutH));
    // 零维度
    CHECK_FALSE(zr::ZupResizeWithinLimits(0u, 540u, 1280u, 720u, maxInW, maxInH, maxOutW, maxOutH));
    CHECK_FALSE(zr::ZupResizeWithinLimits(960u, 540u, 1280u, 0u, maxInW, maxInH, maxOutW, maxOutH));
}

TEST_CASE("CreateFsrUpscaler: 未初始化时不接受任何 Resize（TASK-H-02 D2）")
{
    IUpscaler* up = zr::CreateFsrUpscaler();
    REQUIRE(up != nullptr);
    // 未 Initialize：上限未知（均为 0），Resize 一律拒绝且不记录尺寸
    CHECK_FALSE(up->Resize(960u, 540u, 1280u, 720u));
    CHECK_FALSE(up->Resize(960u, 540u, 3840u, 2160u));
    CHECK_FALSE(up->IsInitialized());
    delete up;
}
