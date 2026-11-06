// =============================================================================
// zrhi_d3d12_unity_target.cpp —— Unity 共享渲染目标实现（TASK-B-UNITY-02）
//
// 一句话：ZRender 把 cube 画进**自己拥有的共享纹理**，导出 NT 句柄让 Unity 采样。
//
// 为什么不是画 Unity 的 backbuffer（验收已判定，不要回退到那条）：
//   ① backbuffer 状态：flip-model 下帧边界是 PRESENT，PRESENT→RENDER_TARGET 无
//      隐式提升，屏障必须由我们插，而我们不知道 Unity 的行程序列；
//   ② 跨队列同步：本桥自建 DIRECT 队列，WaitIdle 只排空自己的队列，不覆盖
//      Unity 队列上未完成的渲染——画一半就被 Unity 采样是必然的竞态。
// 自己拥有纹理后两条同时消失：纹理只由本队列写，写完 fence 等完成，Unity 只读。
//
// 每帧顺序（RenderFrame）：
//   RG(Import color+depth → ClearPass → CubeDrawPass → [ReadbackPass]) →
//   手动屏障 [ColorAttachmentOptimal|TransferSrcOptimal] → ShaderReadOnlyOptimal →
//   Submit → WaitIdle →（回读帧）Map 中心像素
// 返回时纹理恒为 PIXEL_SHADER_RESOURCE 且 GPU 已完成——这既是 Unity 采样的前提，
// 也是"不需要跨队列 fence"的全部理由。
//
// 几何/着色器：与 --draw 同源（.zmesh cube 的 24 顶点 ±0.5 + first_draw.hlsl）。
// 但 DLL 在 Player 里没有仓库路径，无法运行时读文件，故：
//   - 顶点/索引以常量内嵌（数值与 Native/tests/data/cube.zmesh 完全一致）
//   - HLSL 以字符串内嵌，**逐字**等于 Native/shaders/first_draw.hlsl
//     （tests/test_zrhi_unity_target.cpp 做字节比对，漂移即红）
// =============================================================================
#include "zrhi/d3d12/zrhi_d3d12_unity_target.h"

#include <cmath>
#include <cstring>

#include "zrender/draw_list.h"
#include "zrender/rg_render_graph.h"
#include "zrhi/d3d12/zrhi_d3d12_device.h"

namespace zr {
namespace unity_target {
namespace {

// 与 --draw 同值的清屏色（0.05,0.10,0.15 → 8bit (13,26,38)）
constexpr float kClearColor[4] = {0.05f, 0.10f, 0.15f, 1.0f};
constexpr uint32_t kDrawItems = 3u;      // 三个 cube 横向排开（与 --draw 同布局）
constexpr uint32_t kReadbackInterval = 30u; // 每 30 帧回读一次中心像素（全程都回读太浪费屏障）
constexpr uint32_t kReadbackPitch = 256u;   // READBACK 行距（256B 对齐）
constexpr float kPi = 3.14159265358979f;

// 常量布局（96B）——与 first_draw.hlsl 的 CBufPerDraw 一一对应
struct DrawConstants {
    float mvp[16];     // row_major
    float lightDir[4]; // xyz = 指向光源
    float lightColor[4];
};
static_assert(sizeof(DrawConstants) == 96, "DrawConstants must be 96 bytes");

// v1 固定顶点布局：Pos3f+Normal3f+Uv2f（32B，与 ZrD3D12_GraphicsInputLayout 一致）
struct TargetVertex {
    float pos[3];
    float normal[3];
    float uv[2];
};
static_assert(sizeof(TargetVertex) == 32, "TargetVertex must be 32 bytes");

// -----------------------------------------------------------------------------
// 嵌入式 HLSL：逐字等于 Native/shaders/first_draw.hlsl（勿单独改动本段——
// 改 shader 请改 .hlsl 再把文件内容整段复制过来，单测会校验两者字节一致）
// -----------------------------------------------------------------------------
const char kFirstDrawHlsl[] = R"ZRHLSL(// =============================================================================
// first_draw.hlsl —— 首绘着色器（TASK-B-R4-03）
//
// VS：mvp 变换（模型旋转随帧更新，经 CBufPerDraw 每帧写入）
// PS：Lambert N·L + 环境项 + uv checker 调试项（8x8 棋盘，肉眼/像素校验可辨）
//
// 根签名 v1（与 zrhi_d3d12_device.cpp CreateFirstDrawRootSignature 一致）：
//   单一根 CBV b0 —— CBufPerDraw，常量布局（96B）：
//     row_major float4x4 mvp   // 64B：clip = mvp * float4(pos,1)（列向量约定，
//                              //      row_major 存储 ⇒ CPU 侧行主序写入）
//     float4 lightDir          // 16B：xyz = 指向光源的方向（已归一化）
//     float4 lightColor        // 16B：rgb = 光色
//
// 顶点输入（v1 固定布局，与 ZrD3D12_GraphicsInputLayout 一致，stride 32B）：
//   POSITION float3 | NORMAL float3 | TEXCOORD0 float2
//
// 编译：本期运行时 D3DCompile（FXC，vs_5_0 / ps_5_0 → DXBC），入口
//   VsMain / PsMain（zrhi_d3d12_device.cpp CompileHlsl）。
// DXC/SM6 升级路径：改用 dxc.exe /T vs_6_0|ps_6_0（-spirv 出 Vulkan 轨 SPIR-V）；
// 本文件语法为 SM5/SM6 通用子集，无需修改源码即可切换编译器。
// =============================================================================

cbuffer CBufPerDraw : register(b0)
{
    row_major float4x4 mvp;
    float4 lightDir;
    float4 lightColor;
};

struct VsIn
{
    float3 pos : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
};

struct PsIn
{
    float4 pos : SV_POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
};

PsIn VsMain(VsIn input)
{
    PsIn output;
    output.pos = mul(mvp, float4(input.pos, 1.0f));
    output.normal = input.normal;
    output.uv = input.uv;
    return output;
}

float4 PsMain(PsIn input) : SV_Target
{
    // Lambert：N·L（含零面截断）+ 环境项兜底（背光面不完全黑）
    float3 n = normalize(input.normal);
    float ndl = saturate(dot(n, normalize(lightDir.xyz)));
    float3 lit = lightColor.rgb * ndl;
    float3 ambient = float3(0.25f, 0.25f, 0.25f);

    // uv checker（8x8 棋盘调试项：黑白蓝三色可辨，用于像素级校验证据）
    float2 cell = floor(input.uv * 8.0f);
    float checker = fmod(cell.x + cell.y, 2.0f);
    float3 albedo = lerp(float3(0.85f, 0.85f, 0.85f), float3(0.15f, 0.35f, 0.85f), checker);

    return float4(albedo * (ambient + lit), 1.0f);
}
)ZRHLSL";

// 立方体 24 顶点（±0.5，与 Native/tests/data/cube.zmesh 同尺寸；6 面 × 4 顶点）
const TargetVertex kCubeVertices[24] = {
    // +X
    {{0.5f, -0.5f, -0.5f}, {1.0f, 0.0f, 0.0f}, {0.0f, 0.0f}},
    {{0.5f, -0.5f, 0.5f}, {1.0f, 0.0f, 0.0f}, {1.0f, 0.0f}},
    {{0.5f, 0.5f, 0.5f}, {1.0f, 0.0f, 0.0f}, {1.0f, 1.0f}},
    {{0.5f, 0.5f, -0.5f}, {1.0f, 0.0f, 0.0f}, {0.0f, 1.0f}},
    // -X
    {{-0.5f, -0.5f, 0.5f}, {-1.0f, 0.0f, 0.0f}, {0.0f, 0.0f}},
    {{-0.5f, -0.5f, -0.5f}, {-1.0f, 0.0f, 0.0f}, {1.0f, 0.0f}},
    {{-0.5f, 0.5f, -0.5f}, {-1.0f, 0.0f, 0.0f}, {1.0f, 1.0f}},
    {{-0.5f, 0.5f, 0.5f}, {-1.0f, 0.0f, 0.0f}, {0.0f, 1.0f}},
    // +Y
    {{-0.5f, 0.5f, -0.5f}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f}},
    {{0.5f, 0.5f, -0.5f}, {0.0f, 1.0f, 0.0f}, {1.0f, 0.0f}},
    {{0.5f, 0.5f, 0.5f}, {0.0f, 1.0f, 0.0f}, {1.0f, 1.0f}},
    {{-0.5f, 0.5f, 0.5f}, {0.0f, 1.0f, 0.0f}, {0.0f, 1.0f}},
    // -Y
    {{-0.5f, -0.5f, 0.5f}, {0.0f, -1.0f, 0.0f}, {0.0f, 0.0f}},
    {{0.5f, -0.5f, 0.5f}, {0.0f, -1.0f, 0.0f}, {1.0f, 0.0f}},
    {{0.5f, -0.5f, -0.5f}, {0.0f, -1.0f, 0.0f}, {1.0f, 1.0f}},
    {{-0.5f, -0.5f, -0.5f}, {0.0f, -1.0f, 0.0f}, {0.0f, 1.0f}},
    // +Z
    {{-0.5f, -0.5f, 0.5f}, {0.0f, 0.0f, 1.0f}, {0.0f, 0.0f}},
    {{0.5f, -0.5f, 0.5f}, {0.0f, 0.0f, 1.0f}, {1.0f, 0.0f}},
    {{0.5f, 0.5f, 0.5f}, {0.0f, 0.0f, 1.0f}, {1.0f, 1.0f}},
    {{-0.5f, 0.5f, 0.5f}, {0.0f, 0.0f, 1.0f}, {0.0f, 1.0f}},
    // -Z
    {{0.5f, -0.5f, -0.5f}, {0.0f, 0.0f, -1.0f}, {0.0f, 0.0f}},
    {{-0.5f, -0.5f, -0.5f}, {0.0f, 0.0f, -1.0f}, {1.0f, 0.0f}},
    {{-0.5f, 0.5f, -0.5f}, {0.0f, 0.0f, -1.0f}, {1.0f, 1.0f}},
    {{0.5f, 0.5f, -0.5f}, {0.0f, 0.0f, -1.0f}, {0.0f, 1.0f}},
};
constexpr uint32_t kCubeVertexCount = 24u;
constexpr uint32_t kCubeIndexCount = 36u;
const uint32_t kCubeIndices[36] = {
    0u, 1u, 2u, 0u, 2u, 3u,       // +X
    4u, 5u, 6u, 4u, 6u, 7u,       // -X
    8u, 9u, 10u, 8u, 10u, 11u,    // +Y
    12u, 13u, 14u, 12u, 14u, 15u, // -Y
    16u, 17u, 18u, 16u, 18u, 19u, // +Z
    20u, 21u, 22u, 20u, 22u, 23u, // -Z
};

// ---- 行主序 4x4 矩阵助手（列向量约定：clip = P·V·M·pos；与 --draw 同源）--------
void MatMultiply(const float* a, const float* b, float* out)
{
    float r[16];
    for (int row = 0; row < 4; ++row) {
        for (int col = 0; col < 4; ++col) {
            float sum = 0.0f;
            for (int k = 0; k < 4; ++k) {
                sum += a[row * 4 + k] * b[k * 4 + col];
            }
            r[row * 4 + col] = sum;
        }
    }
    std::memcpy(out, r, sizeof(r));
}

void MatPerspectiveFovLH(float fovY, float aspect, float zn, float zf, float* out)
{
    const float f = 1.0f / std::tan(fovY * 0.5f);
    const float m[16] = { f / aspect, 0.0f, 0.0f, 0.0f, 0.0f, f, 0.0f, 0.0f, 0.0f, 0.0f,
        zf / (zf - zn), -zn * zf / (zf - zn), 0.0f, 0.0f, 1.0f, 0.0f };
    std::memcpy(out, m, sizeof(m));
}

void MatRotationX(float angle, float* out)
{
    const float c = std::cos(angle);
    const float s = std::sin(angle);
    const float m[16] = { 1.0f, 0.0f, 0.0f, 0.0f, 0.0f, c, -s, 0.0f, 0.0f, s, c, 0.0f, 0.0f,
        0.0f, 0.0f, 1.0f };
    std::memcpy(out, m, sizeof(m));
}

void MatRotationY(float angle, float* out)
{
    const float c = std::cos(angle);
    const float s = std::sin(angle);
    const float m[16] = { c, 0.0f, s, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, -s, 0.0f, c, 0.0f, 0.0f,
        0.0f, 0.0f, 1.0f };
    std::memcpy(out, m, sizeof(m));
}

void MatTranslation(float x, float y, float z, float* out)
{
    const float m[16] = { 1.0f, 0.0f, 0.0f, x, 0.0f, 1.0f, 0.0f, y, 0.0f, 0.0f, 1.0f,
        z, 0.0f, 0.0f, 0.0f, 1.0f };
    std::memcpy(out, m, sizeof(m));
}

ZrImageBarrier MakeBarrier(ZrImage image, ZrImageLayout prev, ZrImageLayout next,
    ZrAccessBits srcAccess, ZrPipelineStageBits srcStage, ZrAccessBits dstAccess,
    ZrPipelineStageBits dstStage)
{
    ZrImageBarrier barrier {};
    barrier.image = image;
    barrier.prevLayout = prev;
    barrier.newLayout = next;
    barrier.srcAccess = srcAccess;
    barrier.dstAccess = dstAccess;
    barrier.srcStage = srcStage;
    barrier.dstStage = dstStage;
    return barrier;
}

// 目标与整条渲染链（单线程；进程内单例）
struct TargetState {
    ZrD3D12Device* device = nullptr; // 包装对象（CreateFrom 产物）
    ZrImage color {};
    HANDLE sharedHandle = nullptr;
    ZrImage depth {};
    ZrImageView colorRtv {};
    ZrImageView depthDsv {};
    ZrBuffer vb {};
    ZrBuffer ib {};
    ZrBuffer cb[kDrawItems] {};
    void* cbMapped[kDrawItems] {};
    ZrShader vs {};
    ZrShader ps {};
    ZrPipelineState pso {};
    ZrCommandList cmd {};
    IZrQueue* queue = nullptr;
    ZrBuffer readback {};
    void* readbackMapped = nullptr;
    uint32_t width = 0u;
    uint32_t height = 0u;
    uint32_t frameCount = 0u;
    uint32_t readbackCount = 0u;
    uint32_t centerRGBA[4] = {0u, 0u, 0u, 0u};
    uint32_t lastError = 0u;
};

TargetState g_target;

/// 释放目标与整条渲染链（保留设备包装：尺寸变化后可直接重建，无需重新接入）
void DestroyTargetObjects()
{
    if (g_target.device == nullptr) {
        g_target = TargetState {};
        return;
    }
    ZrD3D12Device* device = g_target.device;
    device->WaitIdle();
    if (g_target.readback.IsValid()) {
        device->Destroy(g_target.readback);
    }
    if (g_target.pso.IsValid()) {
        device->Destroy(g_target.pso);
    }
    if (g_target.ps.IsValid()) {
        device->Destroy(g_target.ps);
    }
    if (g_target.vs.IsValid()) {
        device->Destroy(g_target.vs);
    }
    for (uint32_t i = 0; i < kDrawItems; ++i) {
        if (g_target.cb[i].IsValid()) {
            device->Destroy(g_target.cb[i]);
        }
        g_target.cbMapped[i] = nullptr;
    }
    if (g_target.ib.IsValid()) {
        device->Destroy(g_target.ib);
    }
    if (g_target.vb.IsValid()) {
        device->Destroy(g_target.vb);
    }
    if (g_target.depthDsv.IsValid()) {
        device->Destroy(g_target.depthDsv);
    }
    if (g_target.colorRtv.IsValid()) {
        device->Destroy(g_target.colorRtv);
    }
    if (g_target.cmd.IsValid()) {
        device->Destroy(g_target.cmd);
    }
    if (g_target.depth.IsValid()) {
        device->Destroy(g_target.depth);
    }
    if (g_target.color.IsValid()) {
        device->Destroy(g_target.color);
    }
    // NT 句柄与资源生命周期独立：资源已释放，句柄本身仍须关掉（否则句柄泄漏）
    if (g_target.sharedHandle != nullptr) {
        CloseHandle(g_target.sharedHandle);
    }
    device->WaitIdle();
    g_target = TargetState {};
    g_target.device = device;
}

/// 建 VB/IB/CB/PSO/命令链（目标与深度已就位）。失败返回 false。
bool BuildRenderObjects(ZrD3D12Device* device)
{
    ZrBufferDesc vbDesc {};
    vbDesc.size = sizeof(kCubeVertices);
    vbDesc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
    g_target.vb = device->CreateBuffer(vbDesc, kCubeVertices);
    ZrBufferDesc ibDesc {};
    ibDesc.size = sizeof(kCubeIndices);
    ibDesc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
    g_target.ib = device->CreateBuffer(ibDesc, kCubeIndices);
    if (!g_target.vb.IsValid() || !g_target.ib.IsValid()) {
        return false;
    }

    // 每绘制项一个 hostVisible 缓冲（持久 Map，每帧写 96B 常量）
    for (uint32_t i = 0; i < kDrawItems; ++i) {
        ZrBufferDesc cbDesc {};
        cbDesc.size = 256u; // 根 CBV 对齐裕量
        cbDesc.usage = ZrBufferUsage::Uniform;
        cbDesc.hostVisible = true;
        g_target.cb[i] = device->CreateBuffer(cbDesc);
        IZrBuffer* iface = device->GetBuffer(g_target.cb[i]);
        g_target.cbMapped[i] = (iface != nullptr && g_target.cb[i].IsValid()) ? iface->Map() : nullptr;
        if (g_target.cbMapped[i] == nullptr) {
            return false;
        }
    }

    g_target.vs = device->CompileHlsl(
        ZrShaderStage::Vertex, kFirstDrawHlsl, sizeof(kFirstDrawHlsl) - 1u, "VsMain");
    g_target.ps = device->CompileHlsl(
        ZrShaderStage::Pixel, kFirstDrawHlsl, sizeof(kFirstDrawHlsl) - 1u, "PsMain");
    if (!g_target.vs.IsValid() || !g_target.ps.IsValid()) {
        return false;
    }

    ZrGraphicsPsoDesc psoDesc {};
    psoDesc.vertexShader = g_target.vs;
    psoDesc.pixelShader = g_target.ps;
    psoDesc.rtvFormat = ZrFormat::BGRA8Unorm;
    psoDesc.dsvFormat = ZrFormat::D32Float;
    psoDesc.blend = ZrBlendMode::Opaque;
    psoDesc.depthTest = true;
    psoDesc.depthWrite = true;
    // 剔除 None：与 --draw 一致的取舍（资产绕序统一归资产管线任务）
    psoDesc.cullMode = ZrCullMode::None;
    g_target.pso = device->CreatePipelineState(psoDesc);
    if (!g_target.pso.IsValid()) {
        return false;
    }

    g_target.cmd = device->CreateCommandList(ZrQueueType::Graphics);
    g_target.queue = device->GetQueue(ZrQueueType::Graphics);
    if (!g_target.cmd.IsValid() || g_target.queue == nullptr) {
        return false;
    }

    // 中心 1x1 回读缓冲（证明纹理里真有画面，而非"Blit 已调用"）
    ZrBufferDesc rbDesc {};
    rbDesc.size = kReadbackPitch;
    rbDesc.readback = true;
    g_target.readback = device->CreateBuffer(rbDesc);
    if (!g_target.readback.IsValid()) {
        return false;
    }
    IZrBuffer* rbIfc = device->GetBuffer(g_target.readback);
    g_target.readbackMapped = (rbIfc != nullptr) ? rbIfc->Map() : nullptr;
    return g_target.readbackMapped != nullptr;
}

} // namespace

int AttachDeviceFromTexture(void* d3d12Texture)
{
    if (d3d12Texture == nullptr) {
        return kErrNull;
    }
    if (g_target.device != nullptr) {
        return kOk; // 幂等：已接入同一张宿主设备的包装就复用（ReleaseTarget 保留包装）
    }
    ID3D12Resource* resource = static_cast<ID3D12Resource*>(d3d12Texture);
    ID3D12Device* device = nullptr;
    if (FAILED(resource->GetDevice(IID_PPV_ARGS(&device))) || device == nullptr) {
        return kErrDevice;
    }
    // GetDevice 已 AddRef；CreateFrom 内部再持有自己的引用，故这里立即释放
    ZrD3D12Device* wrapper = ZrD3D12CreateDeviceFrom(device);
    device->Release();
    if (wrapper == nullptr) {
        return kErrDevice;
    }
    g_target = TargetState {};
    g_target.device = wrapper;
    return kOk;
}

int CreateTarget(uint32_t width, uint32_t height)
{
    if (width == 0u || height == 0u || width > 16384u || height > 16384u) {
        return kErrArgs;
    }
    if (g_target.device == nullptr) {
        return kErrNoDevice;
    }
    if (g_target.color.IsValid()) {
        if (g_target.width == width && g_target.height == height) {
            return kOk; // 同尺寸：复用现有目标（每帧重建会泄漏 —— 明令禁止）
        }
        return kErrState; // 尺寸变化：调用方须先 ReleaseTarget 再重建
    }

    ZrImageDesc colorDesc {};
    colorDesc.format = ZrFormat::BGRA8Unorm;
    colorDesc.width = width;
    colorDesc.height = height;
    // TransferSrc：中心像素回读要走 COPY_SOURCE；Sampled：语义上声明"会被采样"
    colorDesc.usage = ZrImageUsage::ColorAttachment | ZrImageUsage::TransferSrc
        | ZrImageUsage::Sampled;
    void* shared = nullptr;
    g_target.color = g_target.device->CreateSharedImage(colorDesc, &shared);
    if (!g_target.color.IsValid() || shared == nullptr) {
        g_target = TargetState {}; // device 包装也已被释放
        return kErrDevice;
    }
    g_target.sharedHandle = static_cast<HANDLE>(shared);

    ZrImageDesc depthDesc {};
    depthDesc.format = ZrFormat::D32Float;
    depthDesc.width = width;
    depthDesc.height = height;
    depthDesc.usage = ZrImageUsage::DepthStencilAttachment;
    g_target.depth = g_target.device->CreateImage(depthDesc);

    ZrImageViewDesc rtvDesc {};
    rtvDesc.image = g_target.color;
    rtvDesc.viewType = ZrImageViewType::Rtv;
    g_target.colorRtv = g_target.device->CreateImageView(rtvDesc);

    ZrImageViewDesc dsvDesc {};
    dsvDesc.image = g_target.depth;
    dsvDesc.viewType = ZrImageViewType::Dsv;
    g_target.depthDsv = g_target.device->CreateImageView(dsvDesc);

    g_target.width = width;
    g_target.height = height;

    if (!g_target.depth.IsValid() || !g_target.colorRtv.IsValid() || !g_target.depthDsv.IsValid()
        || !BuildRenderObjects(g_target.device)) {
        DestroyTargetObjects();
        return kErrDevice;
    }
    return kOk;
}

int GetTargetHandle(uint64_t* outHandle)
{
    if (outHandle == nullptr) {
        return kErrNull;
    }
    if (g_target.sharedHandle == nullptr) {
        *outHandle = 0u;
        return kErrState;
    }
    *outHandle = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(g_target.sharedHandle));
    return kOk;
}

int GetTargetResource(void** outResource)
{
    if (outResource == nullptr) {
        return kErrNull;
    }
    if (!g_target.color.IsValid() || g_target.device == nullptr) {
        *outResource = nullptr;
        return kErrState;
    }
    *outResource = g_target.device->D3D12ResourceOf(g_target.color);
    return (*outResource != nullptr) ? kOk : kErrDevice;
}

int RenderFrame(void)
{
    if (g_target.device == nullptr) {
        return kErrNoDevice;
    }
    if (!g_target.color.IsValid() || g_target.queue == nullptr || !g_target.cmd.IsValid()) {
        return kErrState;
    }
    ZrD3D12Device* device = g_target.device;
    IZrCommandList* cmdList = device->GetCommandList(g_target.cmd);
    ID3D12GraphicsCommandList* nativeCmd = device->D3D12CommandListOf(g_target.cmd);
    if (cmdList == nullptr || nativeCmd == nullptr) {
        ++g_target.lastError;
        return kErrDevice;
    }

    // ---- 常量：绕 Y 匀速旋转 + 固定俯仰，三个 cube 横向排开 ----------------------
    const float angle = 2.0f * kPi * static_cast<float>(g_target.frameCount % 120u) / 120.0f;
    const float lightDir[3] = {0.5f, 0.8f, 0.6f};
    const float invLen = 1.0f
        / std::sqrt(lightDir[0] * lightDir[0] + lightDir[1] * lightDir[1]
              + lightDir[2] * lightDir[2]);
    const float lightColor[4] = {1.0f, 0.95f, 0.9f, 1.0f};

    float proj[16];
    MatPerspectiveFovLH(kPi / 3.0f, static_cast<float>(g_target.width) / static_cast<float>(g_target.height),
        0.1f, 100.0f, proj);
    const float view[16] = {1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f, 0, 0, 0, 0, 1.0f};
    float rotY[16];
    MatRotationY(angle, rotY);
    float rotX[16];
    MatRotationX(0.35f, rotX);
    float rx[16];
    MatMultiply(rotX, rotY, rx);
    float pv[16];
    MatMultiply(proj, view, pv);
    for (uint32_t i = 0; i < kDrawItems; ++i) {
        const float tx = (static_cast<float>(i) - 1.0f) * 1.6f;
        float trans[16] = {};
        MatTranslation(tx, 0.0f, 3.2f, trans);
        float model[16];
        MatMultiply(trans, rx, model);
        float mvp[16];
        MatMultiply(pv, model, mvp);
        DrawConstants constants;
        std::memcpy(constants.mvp, mvp, sizeof(mvp));
        constants.lightDir[0] = lightDir[0] * invLen;
        constants.lightDir[1] = lightDir[1] * invLen;
        constants.lightDir[2] = lightDir[2] * invLen;
        constants.lightDir[3] = 0.0f;
        std::memcpy(constants.lightColor, lightColor, sizeof(lightColor));
        std::memcpy(g_target.cbMapped[i], &constants, sizeof(constants));
    }

    // 绘制列表：3 项同一 mesh/材质 → 单批（RG DrawPass 真实消费批）
    DrawList drawList(16u);
    drawList.RegisterMesh(1u, kCubeVertexCount);
    for (uint32_t i = 0; i < kDrawItems; ++i) {
        RenderItem item;
        item.meshHandle = 1u;
        item.materialId = 7u;
        item.shaderId = 0u;
        item.lod = 0u;
        item.depth = 3.2f;
        item.translucent = false;
        if (!drawList.Add(item, 0x01u)) {
            return kErrDevice;
        }
    }
    drawList.Sort();
    drawList.BuildBatches();

    const bool doReadback = (g_target.frameCount % kReadbackInterval) == 0u;

    // ---- 一帧：RG（Import → Clear → CubeDraw → [Readback]）------------------------
    cmdList->Begin();
    RgGraph graph;
    RgHandle colorH = graph.Import(RgResourceDesc::ImportImage("zr_shared_target", g_target.color));
    RgHandle depthH = graph.Import(RgResourceDesc::ImportImage("zr_depth", g_target.depth));

    graph.AddPass("ZRTargetClear",
        [&](RgPass& pass) {
            pass.Write(colorH, ZrImageLayout::ColorAttachmentOptimal,
                ZrAccessBits::ColorAttachmentWrite, ZrPipelineStageBits::ColorAttachmentOutput);
            pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                ZrAccessBits::DepthStencilAttachmentWrite, ZrPipelineStageBits::LateFragmentTests);
        },
        [&](RgContext& ctx) {
            cmdList->ClearColorImage(ctx.GetImage(colorH), ZrImageLayout::ColorAttachmentOptimal,
                kClearColor, ZrSubresourceRange::All());
            nativeCmd->ClearRenderTargetView(device->D3D12ViewCpuOf(g_target.colorRtv), kClearColor,
                0u, nullptr);
            nativeCmd->ClearDepthStencilView(device->D3D12ViewCpuOf(g_target.depthDsv),
                D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0u, 0u, nullptr);
        });

    graph.AddPass("ZRTargetCubeDraw",
        [&](RgPass& pass) {
            pass.Write(colorH, ZrImageLayout::ColorAttachmentOptimal,
                ZrAccessBits::ColorAttachmentWrite, ZrPipelineStageBits::ColorAttachmentOutput);
            pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                ZrAccessBits::DepthStencilAttachmentWrite, ZrPipelineStageBits::LateFragmentTests);
        },
        [&](RgContext&) {
            cmdList->SetPipelineState(g_target.pso);
            cmdList->SetRenderTarget(g_target.colorRtv, g_target.depthDsv);
            cmdList->SetViewportScissor(g_target.width, g_target.height);
            ZrBuffer vbs[1] = {g_target.vb};
            cmdList->SetVertexBuffers(vbs, 1u);
            cmdList->SetIndexBuffer(g_target.ib);
            for (const ZrDrawBatch& batch : drawList.Batches()) {
                for (uint32_t i = 0; i < batch.count; ++i) {
                    const uint32_t itemIndex = batch.startIndex + i;
                    cmdList->SetGraphicsConstantBuffer(0u, g_target.cb[itemIndex]);
                    cmdList->DrawIndexedInstanced(kCubeIndexCount, 1u, 0u, 0u, 0u);
                }
            }
        });

    if (doReadback) {
        graph.AddPass("ZRTargetReadback",
            [&](RgPass& pass) {
                pass.Read(colorH, ZrImageLayout::TransferSrcOptimal, ZrAccessBits::TransferRead,
                    ZrPipelineStageBits::Transfer)
                    .MarkRoot();
            },
            [&](RgContext& ctx) {
                ID3D12Resource* texture = device->D3D12ResourceOf(ctx.GetImage(colorH));
                const UINT cx = g_target.width / 2u;
                const UINT cy = g_target.height / 2u;
                const D3D12_BOX box = {cx, cy, 0u, cx + 1u, cy + 1u, 1u};
                D3D12_TEXTURE_COPY_LOCATION dst {};
                dst.pResource = device->D3D12ResourceOf(g_target.readback);
                dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
                dst.PlacedFootprint.Footprint.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                dst.PlacedFootprint.Footprint.Width = 1u;
                dst.PlacedFootprint.Footprint.Height = 1u;
                dst.PlacedFootprint.Footprint.Depth = 1u;
                dst.PlacedFootprint.Footprint.RowPitch = kReadbackPitch;
                D3D12_TEXTURE_COPY_LOCATION src {};
                src.pResource = texture;
                src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
                src.SubresourceIndex = 0u;
                nativeCmd->CopyTextureRegion(&dst, 0u, 0u, 0u, &src, &box);
            });
    }

    if (!graph.Compile()) {
        cmdList->End();
        return kErrState;
    }
    graph.ExecuteAliased(*device, *cmdList);

    // ---- 帧末屏障：→ PIXEL_SHADER_RESOURCE（Unity 采样的前提）---------------------
    // 这是本任务成立的关键一步：返回前纹理必须处于"可采样"状态，Unity 才会把它当作
    // 一张普通纹理绑定 SRV（PIXEL|NON_PIXEL 两级都置上，见 translate 的布局映射）。
    const ZrImageLayout layoutNow = doReadback ? ZrImageLayout::TransferSrcOptimal
                                              : ZrImageLayout::ColorAttachmentOptimal;
    ZrImageBarrier toSrv = MakeBarrier(g_target.color, layoutNow,
        ZrImageLayout::ShaderReadOnlyOptimal,
        doReadback ? ZrAccessBits::TransferRead : ZrAccessBits::ColorAttachmentWrite,
        doReadback ? ZrPipelineStageBits::Transfer : ZrPipelineStageBits::ColorAttachmentOutput,
        ZrAccessBits::ShaderRead, ZrPipelineStageBits::FragmentShader);
    cmdList->ResourceBarrier(&toSrv, 1u, nullptr, 0u);

    // 深度回 Undefined（下一帧 RG 起点假设；与 --draw 一致）
    ZrImageBarrier depthReset = MakeBarrier(g_target.depth,
        ZrImageLayout::DepthStencilAttachmentOptimal, ZrImageLayout::Undefined,
        ZrAccessBits::DepthStencilAttachmentWrite, ZrPipelineStageBits::LateFragmentTests,
        ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
    cmdList->ResourceBarrier(&depthReset, 1u, nullptr, 0u);

    cmdList->End();
    g_target.queue->Submit(g_target.cmd);
    // 同队列 fence 排空：返回即 GPU 完成 → Unity 采样时纹理内容与状态都已就绪，
    // 不需要（也无法）让 Unity 的队列与本队列做跨队列同步。
    device->WaitIdle();
    graph.FlushRetired(*device);

    ++g_target.frameCount;
    if (doReadback) {
        const uint8_t* px = static_cast<const uint8_t*>(g_target.readbackMapped);
        // BGRA 字节序：纹理存的是 B,G,R,A
        g_target.centerRGBA[0] = px[2];
        g_target.centerRGBA[1] = px[1];
        g_target.centerRGBA[2] = px[0];
        g_target.centerRGBA[3] = px[3];
        ++g_target.readbackCount;
    }
    return kOk;
}

void ReleaseTarget(void)
{
    // 只释放目标与渲染链，保留设备包装：尺寸变化时可直接再 CreateTarget，
    // 不必回头找宿主要一张纹理重新接入。进程退出时随 DLL 卸载一并回收。
    DestroyTargetObjects();
}

int GetStats(TargetStats* outStats)
{
    if (outStats == nullptr) {
        return kErrNull;
    }
    outStats->frameCount = g_target.frameCount;
    outStats->width = g_target.width;
    outStats->height = g_target.height;
    outStats->readbackCount = g_target.readbackCount;
    outStats->sharedHandle = static_cast<uint64_t>(
        reinterpret_cast<uintptr_t>(g_target.sharedHandle));
    for (uint32_t i = 0; i < 4u; ++i) {
        outStats->centerRGBA[i] = g_target.centerRGBA[i];
    }
    outStats->lastError = g_target.lastError;
    return kOk;
}

bool HasTarget(void)
{
    return g_target.color.IsValid();
}

const char* EmbeddedFirstDrawHlsl(uint32_t* outSize)
{
    const uint32_t size = static_cast<uint32_t>(sizeof(kFirstDrawHlsl) - 1u);
    if (outSize != nullptr) {
        *outSize = size;
    }
    return kFirstDrawHlsl;
}

} // namespace unity_target
} // namespace zr
