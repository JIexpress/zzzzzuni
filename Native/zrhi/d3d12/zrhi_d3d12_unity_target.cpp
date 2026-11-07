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
//   RG(Import color+depth → ClearPass → DrawPass → [ReadbackPass]) →
//   手动屏障 [ColorAttachmentOptimal|TransferSrcOptimal] → ShaderReadOnlyOptimal →
//   Submit → WaitIdle →（回读帧）Map 中心像素
// 返回时纹理恒为 PIXEL_SHADER_RESOURCE 且 GPU 已完成——这既是 Unity 采样的前提，
// 也是"不需要跨队列 fence"的全部理由。
//
// 帧内容（TASK-B-UNITY-03 起由调用方决定，两条路径共用上面的 RG/屏障/回读）：
//   - 场景路径：调用方 SetCamera + DrawMesh 提交了内容 → 画 Unity 的物件
//     （常量 176B，scene_draw.hlsl；逐物件 world/mvp/albedo）
//   - 回落路径：没有提交 → 仍画原来的三 cube 演示（first_draw.hlsl / 96B 常量），
//     保证 --draw 与既有验证路径行为不变
//
// 几何/着色器：cube 与 --draw 同源（.zmesh cube 的 24 顶点 ±0.5 + first_draw.hlsl）。
// 但 DLL 在 Player 里没有仓库路径，无法运行时读文件，故：
//   - 顶点/索引以常量内嵌（数值与 Native/tests/data/cube.zmesh 完全一致）
//   - 两份 HLSL 都以字符串内嵌，**逐字**等于 Native/shaders/{first_draw,scene_draw}.hlsl
//     （first_draw 有单测做字节比对；scene_draw 同规矩，改文件请同步内嵌副本）
// =============================================================================
#include "zrhi/d3d12/zrhi_d3d12_unity_target.h"

#include <chrono>
#include <cmath>
#include <cstring>

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

// -----------------------------------------------------------------------------
// 嵌入式场景着色器：逐字等于 Native/shaders/scene_draw.hlsl（改 shader 请改文件
// 后再整段同步到这里；两份不一致会造成"改了没生效"的假象）。
// -----------------------------------------------------------------------------
const char kSceneHlsl[] = R"ZRSCENEHLSL(// =============================================================================
// scene_draw.hlsl —— Unity 场景提交着色器（TASK-B-UNITY-03）
//
// 与 first_draw.hlsl 的关系：**不改** first_draw.hlsl。那份是 --draw 与
// B-UNITY-02 的内嵌基线（单测逐字节比对它，且 --draw 的常量布局是 96B），
// 场景提交需要"逐物件世界矩阵 + 逐物件颜色"，常量布局因此多两块，独立成
// 本文件（内嵌副本在 zrhi_d3d12_unity_target.cpp 的 kSceneHlsl）。
//
// 根签名约定的 b0 常量布局（176B，D3D 行主序存储）：
//   row_major float4x4 mvp      // 64B：proj * view * world
//   row_major float4x4 world    // 64B：model（法线要用它的 3x3，见下）
//   float4 lightDir             // 16B：xyz = 指向光源的方向（世界空间，已归一化）
//   float4 lightColor           // 16B：rgb = 光色
//   float4 albedo               // 16B：rgb = 物件基色（Unity sharedMaterial 色）
//
// 顶点输入与 first_draw.hlsl 完全一致（v1 固定布局，stride 32B）：
//   POSITION float3 | NORMAL float3 | TEXCOORD0 float2
//
// 为什么法线要用 world 矩阵：Unity 的顶点法线是**模型空间**的，顶点数据按
// mesh 缓存（同一 mesh 被多个 transform 复用），所以法线只能在 VS 里随物件
// 变换——否则所有旋转过的物件都会照出面黑的背光面。
// =============================================================================

cbuffer CBufSceneDraw : register(b0)
{
    row_major float4x4 mvp;
    row_major float4x4 world;
    float4 lightDir;
    float4 lightColor;
    float4 albedo;
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
    // 法线转到世界空间（旋转 + 均匀缩放；非均匀缩放由 PS 的 normalize 兜住量级）
    output.normal = mul((float3x3)world, input.normal);
    output.uv = input.uv;
    return output;
}

float4 PsMain(PsIn input) : SV_Target
{
    float3 n = normalize(input.normal);
    float3 l = normalize(lightDir.xyz);
    // 双面光照：地面 Plane 与部分资产的绕序不保证，背光面给半强度而不是全黑
    float ndl = saturate(dot(n, l));
    // 环境项抬到 0.45：ZRender 没有 URP 的球谐环境光，纯 Lambert 会把背光面压成
    // 剪影（实测胶囊/角色发黑），这里用固定环境项兜住"能看清"这条底线。
    float3 ambient = float3(0.45f, 0.47f, 0.52f);
    float3 lit = lightColor.rgb * ndl;
    return float4(albedo.rgb * (ambient + lit), 1.0f);
}
)ZRSCENEHLSL";

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
static_assert(sizeof(kCubeVertices) / sizeof(kCubeVertices[0]) == kCubeVertexCount,
    "cube vertex table size must match kCubeVertexCount");
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
//
// TASK-B-UNITY-04：颜色/深度目标改成**双槽**（0/1 轮流），配合 fence 做帧间并行。
// 单槽在"不等本帧完成"的前提下必然被下一帧覆盖，宿主会采到画了一半的内容。
constexpr uint32_t kTargetSlots = 2u;
/// 判定"这一帧真的被阻塞过"的阈值（毫秒）：低于它的等待只是 fence 查询开销
constexpr double kBlockedThresholdMs = 0.05;

struct TargetState {
    ZrD3D12Device* device = nullptr; // 包装对象（CreateFrom 产物）
    ZrImage color[kTargetSlots] {};
    HANDLE sharedHandle[kTargetSlots] = {nullptr, nullptr};
    ZrImage depth[kTargetSlots] {};
    ZrImageView colorRtv[kTargetSlots] {};
    ZrImageView depthDsv[kTargetSlots] {};
    // 帧同步（TASK-B-UNITY-04）：本队列自己的 fence，帧 N 提交后 Signal，
    // 下一帧开头等 N-1 的完成。事件对象只建一次（WaitForSingleObject 复用）。
    ID3D12Fence* syncFence = nullptr;
    HANDLE syncEvent = nullptr;
    uint64_t syncValue = 0u;                              // 已 Signal 的最大值
    uint32_t readableSlot = 0u;                            // 宿主可安全采样的槽位
    bool pendingReadback = false;                          // 上一帧的回读结果待取
    double syncLastMs = 0.0;                               // 最近一次等待耗时
    double syncTotalMs = 0.0;                              // 累计等待耗时
    uint32_t syncFrames = 0u;                              // 发生过等待的帧数
    uint32_t syncBlocked = 0u;                             // 其中真的被阻塞的帧数
    ZrBuffer vb {};
    ZrBuffer ib {};
    // 逐绘常量缓冲池（TASK-B-UNITY-03）：按需增长到本帧绘制项数，之后复用。
    // 上限 = kMaxSceneDraws；每项 256B（根 CBV 对齐裕量），持久 Map。
    ZrBuffer cb[kMaxSceneDraws] {};
    void* cbMapped[kMaxSceneDraws] {};
    uint32_t cbCount = 0u;
    ZrShader vs {};
    ZrShader ps {};
    ZrPipelineState pso {};
    ZrShader sceneVs {};       // 场景提交变体（176B 常量，见 scene_draw.hlsl）
    ZrShader scenePs {};
    ZrPipelineState psoScene {};
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

// ---- TASK-B-UNITY-03：场景提交（相机 / 网格表 / 绘制表）-------------------------
// 全部定容静态存储：每帧不分配、不上传顶点（性能底线）。绘制表每帧由
// BeginScene 清空、DrawMesh 追加、RenderFrame 消费。

/// 一帧内的一次绘制（定容数组元素；88B）
struct SceneDrawCmd {
    uint32_t meshSlot = 0u;
    uint32_t meshGeneration = 0u;
    float world[16] = {}; // 行主序 model（平移量在下标 3/7/11）
    float rgba[4] = {1.0f, 1.0f, 1.0f, 1.0f};
};
static_assert(sizeof(SceneDrawCmd) == 88, "SceneDrawCmd must be 88 bytes");

/// 网格句柄表项：句柄 = (代际<<32)|槽位，与 zplugin 的 .zmesh 表同一约定
struct MeshEntry {
    bool used = false;
    uint32_t generation = 0u; // 从 1 起；释放即 +1（0 = 槽从未用过）
    ZrBuffer vb {};
    ZrBuffer ib {};
    uint32_t indexCount = 0u;
};

constexpr uint32_t kMeshSlots = 512u;

/// 场景着色器常量（176B）——与 scene_draw.hlsl 的 CBufSceneDraw 一一对应
struct SceneDrawConstants {
    float mvp[16];   // proj * view * world（行主序，列向量约定）
    float world[16]; // 法线要用的 model
    float lightDir[4];
    float lightColor[4];
    float albedo[4];
};
static_assert(sizeof(SceneDrawConstants) == 176, "SceneDrawConstants must be 176 bytes");

/// 帧内绘制项（渲染链消费的形态：绑定什么、画多少）
struct FrameDraw {
    ZrBuffer vb {};
    ZrBuffer ib {};
    uint32_t indexCount = 0u;
};

struct SceneState {
    bool cameraSet = false;
    float view[16] = {};
    float proj[16] = {};
    uint32_t drawCount = 0u;
    SceneDrawCmd draws[kMaxSceneDraws];
    uint64_t uploadCount = 0u;  // CreateMesh 成功次数（应恒等于 mesh 种类数）
    uint64_t frameCount = 0u;   // 走场景路径的帧数
    uint64_t drawTotal = 0u;    // 累计绘制项
    uint32_t lastDrawCount = 0u;
};

SceneState g_scene;
MeshEntry g_meshes[kMeshSlots];
/// 本帧实际要画的项（RenderFrame 填；定容静态，避免每帧分配）
FrameDraw g_frameDraws[kMaxSceneDraws];

uint64_t MakeMeshHandle(uint32_t slot, uint32_t generation)
{
    return (static_cast<uint64_t>(generation) << 32) | static_cast<uint64_t>(slot);
}

MeshEntry* LookupMesh(uint64_t handle)
{
    const uint32_t slot = static_cast<uint32_t>(handle & 0xFFFFFFFFull);
    const uint32_t generation = static_cast<uint32_t>(handle >> 32);
    if (slot >= kMeshSlots || generation == 0u) {
        return nullptr;
    }
    MeshEntry& entry = g_meshes[slot];
    if (!entry.used || entry.generation != generation) {
        return nullptr;
    }
    return &entry;
}


/// 等本队列自己的 fence 到 value（TASK-B-UNITY-04）。顺手记一笔等待耗时：
/// "CPU 等待下降了"这条判据的现场数字就来自这里。
void WaitForSync(uint64_t value)
{
    if (g_target.syncFence == nullptr || g_target.syncEvent == nullptr) {
        return;
    }
    const auto start = std::chrono::steady_clock::now();
    if (g_target.syncFence->GetCompletedValue() < value) {
        g_target.syncFence->SetEventOnCompletion(value, g_target.syncEvent);
        WaitForSingleObject(g_target.syncEvent, INFINITE);
    }
    const std::chrono::duration<double, std::milli> elapsed =
        std::chrono::steady_clock::now() - start;
    g_target.syncLastMs = elapsed.count();
    g_target.syncTotalMs += g_target.syncLastMs;
    ++g_target.syncFrames;
    if (g_target.syncLastMs >= kBlockedThresholdMs) {
        ++g_target.syncBlocked;
    }
}

/// 当前在途帧数（已 Signal 但 GPU 还没做完的帧；0 = 队列已排空）
uint32_t FramesInFlight()
{
    if (g_target.syncFence == nullptr) {
        return 0u;
    }
    const uint64_t completed = g_target.syncFence->GetCompletedValue();
    if (completed >= g_target.syncValue) {
        return 0u;
    }
    const uint64_t inFlight = g_target.syncValue - completed;
    return (inFlight > kTargetSlots) ? kTargetSlots : static_cast<uint32_t>(inFlight);
}

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
    if (g_target.psoScene.IsValid()) {
        device->Destroy(g_target.psoScene);
    }
    if (g_target.scenePs.IsValid()) {
        device->Destroy(g_target.scenePs);
    }
    if (g_target.sceneVs.IsValid()) {
        device->Destroy(g_target.sceneVs);
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
    // 常量缓冲池按实际增长量释放（不是 kMaxSceneDraws：绝大多数场景只用到几十个）
    for (uint32_t i = 0; i < g_target.cbCount && i < kMaxSceneDraws; ++i) {
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
    for (uint32_t slot = 0; slot < kTargetSlots; ++slot) {
        if (g_target.depthDsv[slot].IsValid()) {
            device->Destroy(g_target.depthDsv[slot]);
        }
        if (g_target.colorRtv[slot].IsValid()) {
            device->Destroy(g_target.colorRtv[slot]);
        }
    }
    if (g_target.cmd.IsValid()) {
        device->Destroy(g_target.cmd);
    }
    for (uint32_t slot = 0; slot < kTargetSlots; ++slot) {
        if (g_target.depth[slot].IsValid()) {
            device->Destroy(g_target.depth[slot]);
        }
        if (g_target.color[slot].IsValid()) {
            device->Destroy(g_target.color[slot]);
        }
    }
    // NT 句柄与资源生命周期独立：资源已释放，句柄本身仍须关掉（否则句柄泄漏）
    for (uint32_t slot = 0; slot < kTargetSlots; ++slot) {
        if (g_target.sharedHandle[slot] != nullptr) {
            CloseHandle(g_target.sharedHandle[slot]);
        }
    }
    // fence 与等待事件也要跟着目标一起放（像素级泄漏计数器会看到它们）
    if (g_target.syncFence != nullptr) {
        g_target.syncFence->Release();
    }
    if (g_target.syncEvent != nullptr) {
        CloseHandle(g_target.syncEvent);
    }
    device->WaitIdle();
    g_target = TargetState {};
    g_target.device = device;
}

/// 把逐绘常量缓冲池扩到 needed 个（只增长、不回收；每帧调用只做一次比较）。
/// 为什么要池：每个绘制项要各自一份 96B/176B 常量，而 SetGraphicsConstantBuffer
/// 只接受"整块缓冲"，因此一项一缓冲；按需建一次后写内容，避免每帧建缓冲。
bool EnsureConstantBuffers(uint32_t needed)
{
    if (needed > kMaxSceneDraws || g_target.device == nullptr) {
        return false;
    }
    while (g_target.cbCount < needed) {
        ZrBufferDesc cbDesc {};
        cbDesc.size = 256u; // 根 CBV 对齐裕量
        cbDesc.usage = ZrBufferUsage::Uniform;
        cbDesc.hostVisible = true;
        ZrBuffer buffer = g_target.device->CreateBuffer(cbDesc);
        if (!buffer.IsValid()) {
            return false;
        }
        IZrBuffer* iface = g_target.device->GetBuffer(buffer);
        void* mapped = (iface != nullptr) ? iface->Map() : nullptr;
        if (mapped == nullptr) {
            g_target.device->Destroy(buffer);
            return false;
        }
        g_target.cb[g_target.cbCount] = buffer;
        g_target.cbMapped[g_target.cbCount] = mapped;
        ++g_target.cbCount;
    }
    return true;
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

    // 逐绘常量缓冲池：先长到 legacy 三 cube 的量，场景路径再按需继续长
    if (!EnsureConstantBuffers(kDrawItems)) {
        return false;
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

    // 场景提交变体：同一顶点布局/深度/剔除，只是常量布局多两块（world + albedo）。
    // 单独一套 shader/PSO 而不改 first_draw.hlsl —— 后者是 --draw 与 B-UNITY-02
    // 的基线，动它会连带改 --draw 的常量布局与像素校验。
    g_target.sceneVs = device->CompileHlsl(
        ZrShaderStage::Vertex, kSceneHlsl, sizeof(kSceneHlsl) - 1u, "VsMain");
    g_target.scenePs = device->CompileHlsl(
        ZrShaderStage::Pixel, kSceneHlsl, sizeof(kSceneHlsl) - 1u, "PsMain");
    if (!g_target.sceneVs.IsValid() || !g_target.scenePs.IsValid()) {
        return false;
    }
    ZrGraphicsPsoDesc scenePsoDesc = psoDesc;
    scenePsoDesc.vertexShader = g_target.sceneVs;
    scenePsoDesc.pixelShader = g_target.scenePs;
    g_target.psoScene = device->CreatePipelineState(scenePsoDesc);
    if (!g_target.psoScene.IsValid()) {
        return false;
    }

    g_target.cmd = device->CreateCommandList(ZrQueueType::Graphics);
    g_target.queue = device->GetQueue(ZrQueueType::Graphics);
    if (!g_target.cmd.IsValid() || g_target.queue == nullptr) {
        return false;
    }

    // 帧同步原语（TASK-B-UNITY-04）：本队列自己的 fence + 可复用的事件对象。
    // 用独立 fence 而不是 IZrDevice::WaitIdle，因为后者是"排空到此刻"——
    // 那样每帧都得等本帧干完，正是本任务要消掉的串行化。
    if (g_target.syncFence == nullptr) {
        ID3D12Device* nativeDevice = device->D3D12Device();
        if (nativeDevice == nullptr
            || FAILED(nativeDevice->CreateFence(0u, D3D12_FENCE_FLAG_NONE,
                IID_PPV_ARGS(&g_target.syncFence)))) {
            return false;
        }
        g_target.syncEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (g_target.syncEvent == nullptr) {
            return false;
        }
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
    if (g_target.color[0].IsValid()) {
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
    for (uint32_t slot = 0; slot < kTargetSlots; ++slot) {
        void* shared = nullptr;
        g_target.color[slot] = g_target.device->CreateSharedImage(colorDesc, &shared);
        if (!g_target.color[slot].IsValid() || shared == nullptr) {
            g_target = TargetState {}; // device 包装也已被释放
            return kErrDevice;
        }
        g_target.sharedHandle[slot] = static_cast<HANDLE>(shared);
    }

    ZrImageDesc depthDesc {};
    depthDesc.format = ZrFormat::D32Float;
    depthDesc.width = width;
    depthDesc.height = height;
    depthDesc.usage = ZrImageUsage::DepthStencilAttachment;
    for (uint32_t slot = 0; slot < kTargetSlots; ++slot) {
        g_target.depth[slot] = g_target.device->CreateImage(depthDesc);

        ZrImageViewDesc rtvDesc {};
        rtvDesc.image = g_target.color[slot];
        rtvDesc.viewType = ZrImageViewType::Rtv;
        g_target.colorRtv[slot] = g_target.device->CreateImageView(rtvDesc);

        ZrImageViewDesc dsvDesc {};
        dsvDesc.image = g_target.depth[slot];
        dsvDesc.viewType = ZrImageViewType::Dsv;
        g_target.depthDsv[slot] = g_target.device->CreateImageView(dsvDesc);
    }

    g_target.width = width;
    g_target.height = height;
    g_target.readableSlot = 0u;

    bool viewsOk = true;
    for (uint32_t slot = 0; slot < kTargetSlots; ++slot) {
        if (!g_target.depth[slot].IsValid() || !g_target.colorRtv[slot].IsValid()
            || !g_target.depthDsv[slot].IsValid()) {
            viewsOk = false;
        }
    }

    if (!viewsOk || !BuildRenderObjects(g_target.device)) {
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
    if (g_target.sharedHandle[0] == nullptr) {
        *outHandle = 0u;
        return kErrState;
    }
    *outHandle = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(g_target.sharedHandle[0]));
    return kOk;
}

int GetTargetResource(void** outResource)
{
    // 兼容旧入口（B-UNITY-02）：返回 0 号槽位。双槽路径请用 GetTargetResourceAt。
    return GetTargetResourceAt(0u, outResource);
}

int GetTargetResourceAt(uint32_t index, void** outResource)
{
    if (outResource == nullptr) {
        return kErrNull;
    }
    if (index >= kTargetSlots) {
        *outResource = nullptr;
        return kErrArgs;
    }
    if (!g_target.color[index].IsValid() || g_target.device == nullptr) {
        *outResource = nullptr;
        return kErrState;
    }
    *outResource = g_target.device->D3D12ResourceOf(g_target.color[index]);
    return (*outResource != nullptr) ? kOk : kErrDevice;
}

int GetReadableTargetIndex(uint32_t* outIndex)
{
    if (outIndex == nullptr) {
        return kErrNull;
    }
    *outIndex = g_target.readableSlot;
    return kOk;
}

int GetSyncStats(SyncStats* out)
{
    if (out == nullptr) {
        return kErrNull;
    }
    out->structSize = static_cast<uint32_t>(sizeof(SyncStats));
    out->frames = g_target.frameCount; // RenderFrame 调用数（不是等待次数）
    out->blockedFrames = g_target.syncBlocked;
    out->framesInFlight = FramesInFlight();
    out->lastWaitMs = g_target.syncLastMs;
    out->avgWaitMs = (g_target.syncFrames > 0u)
        ? (g_target.syncTotalMs / static_cast<double>(g_target.syncFrames))
        : 0.0;
    return kOk;
}

int RenderFrame(void)
{
    if (g_target.device == nullptr) {
        return kErrNoDevice;
    }
    if (!g_target.color[0].IsValid() || g_target.queue == nullptr || !g_target.cmd.IsValid()) {
        return kErrState;
    }
    ZrD3D12Device* device = g_target.device;
    IZrCommandList* cmdList = device->GetCommandList(g_target.cmd);
    ID3D12GraphicsCommandList* nativeCmd = device->D3D12CommandListOf(g_target.cmd);
    if (cmdList == nullptr || nativeCmd == nullptr) {
        ++g_target.lastError;
        return kErrDevice;
    }

    // ---- 帧首同步（TASK-B-UNITY-04）：只等**上一帧**（N-1）的 fence --------------
    // 帧 N 提交后不等自己：syncValue 在这一刻 = 帧 N-1 的信号值，等它完成即可。
    // 帧 N-1 在上一个宿主帧就提交了，中间隔了 Unity 自己的一整帧 → 绝大多数时候
    // 已经完成，这里的等待是 0；旧的 WaitIdle 则要等本帧（刚提交）干完。
    if (g_target.syncValue > 0u) {
        WaitForSync(g_target.syncValue);
    }

    // 双槽：本帧画 slot，宿主采样 otherSlot（= 上一帧那份，刚刚确认完成）
    const uint32_t slot = g_target.frameCount & 1u;
    const uint32_t otherSlot = slot ^ 1u;

    // 中心像素回读延后一帧取：等完帧 N-1 之后它的 COPY 才真正落盘
    if (g_target.pendingReadback && g_target.readbackMapped != nullptr) {
        const uint8_t* px = static_cast<const uint8_t*>(g_target.readbackMapped);
        // BGRA 字节序：纹理存的是 B,G,R,A
        g_target.centerRGBA[0] = px[2];
        g_target.centerRGBA[1] = px[1];
        g_target.centerRGBA[2] = px[0];
        g_target.centerRGBA[3] = px[3];
        ++g_target.readbackCount;
        g_target.pendingReadback = false;
    }

    // ---- 帧内容：调用方提交优先，无提交回落硬编码三 cube ------------------------
    // 这个分叉是任务判据 V1/V4 的分水岭：有相机 + 有绘制项 = 画 Unity 的场景；
    // 否则保持 B-UNITY-02 的演示（--draw 与既有验证不受影响）。
    const bool scenePath = g_scene.cameraSet && g_scene.drawCount > 0u;
    uint32_t frameDrawCount = 0u;
    ZrPipelineState framePso = g_target.pso;
    if (scenePath) {
        if (!EnsureConstantBuffers(g_scene.drawCount)) {
            ++g_target.lastError;
            return kErrDevice;
        }
        float vp[16];
        MatMultiply(g_scene.proj, g_scene.view, vp);
        const float lightDirS[3] = {0.5f, 0.8f, 0.6f};
        const float invLenS = 1.0f
            / std::sqrt(lightDirS[0] * lightDirS[0] + lightDirS[1] * lightDirS[1]
                  + lightDirS[2] * lightDirS[2]);
        for (uint32_t i = 0; i < g_scene.drawCount; ++i) {
            const SceneDrawCmd& cmd = g_scene.draws[i];
            const MeshEntry* mesh = LookupMesh(
                MakeMeshHandle(cmd.meshSlot, cmd.meshGeneration));
            if (mesh == nullptr) {
                continue; // 句柄在提交后被释放：跳过即可，不毁整帧
            }
            SceneDrawConstants constants;
            std::memset(&constants, 0, sizeof(constants));
            MatMultiply(vp, cmd.world, constants.mvp);
            std::memcpy(constants.world, cmd.world, sizeof(constants.world));
            constants.lightDir[0] = lightDirS[0] * invLenS;
            constants.lightDir[1] = lightDirS[1] * invLenS;
            constants.lightDir[2] = lightDirS[2] * invLenS;
            constants.lightColor[0] = 1.0f;
            constants.lightColor[1] = 0.95f;
            constants.lightColor[2] = 0.9f;
            constants.lightColor[3] = 1.0f;
            std::memcpy(constants.albedo, cmd.rgba, sizeof(constants.albedo));
            std::memcpy(g_target.cbMapped[frameDrawCount], &constants, sizeof(constants));
            g_frameDraws[frameDrawCount].vb = mesh->vb;
            g_frameDraws[frameDrawCount].ib = mesh->ib;
            g_frameDraws[frameDrawCount].indexCount = mesh->indexCount;
            ++frameDrawCount;
        }
        framePso = g_target.psoScene;
    }

    if (!scenePath) {
        // ---- 回落路径：绕 Y 匀速旋转 + 固定俯仰，三个 cube 横向排开（B-UNITY-02 原样）
        const float angle = 2.0f * kPi * static_cast<float>(g_target.frameCount % 120u) / 120.0f;
        const float lightDir[3] = {0.5f, 0.8f, 0.6f};
        const float invLen = 1.0f
            / std::sqrt(lightDir[0] * lightDir[0] + lightDir[1] * lightDir[1]
                  + lightDir[2] * lightDir[2]);
        const float lightColor[4] = {1.0f, 0.95f, 0.9f, 1.0f};

        float proj[16];
        MatPerspectiveFovLH(kPi / 3.0f,
            static_cast<float>(g_target.width) / static_cast<float>(g_target.height), 0.1f, 100.0f,
            proj);
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
            g_frameDraws[i].vb = g_target.vb;
            g_frameDraws[i].ib = g_target.ib;
            g_frameDraws[i].indexCount = kCubeIndexCount;
        }
        frameDrawCount = kDrawItems;
    }

    const bool doReadback = (g_target.frameCount % kReadbackInterval) == 0u;

    // ---- 一帧：RG（Import → Clear → CubeDraw → [Readback]）------------------------
    cmdList->Begin();
    RgGraph graph;
    RgHandle colorH = graph.Import(RgResourceDesc::ImportImage("zr_shared_target", g_target.color[slot]));
    RgHandle depthH = graph.Import(RgResourceDesc::ImportImage("zr_depth", g_target.depth[slot]));

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
            nativeCmd->ClearRenderTargetView(device->D3D12ViewCpuOf(g_target.colorRtv[slot]), kClearColor,
                0u, nullptr);
            nativeCmd->ClearDepthStencilView(device->D3D12ViewCpuOf(g_target.depthDsv[slot]),
                D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0u, 0u, nullptr);
        });

    graph.AddPass("ZRTargetSceneDraw",
        [&](RgPass& pass) {
            pass.Write(colorH, ZrImageLayout::ColorAttachmentOptimal,
                ZrAccessBits::ColorAttachmentWrite, ZrPipelineStageBits::ColorAttachmentOutput);
            pass.Write(depthH, ZrImageLayout::DepthStencilAttachmentOptimal,
                ZrAccessBits::DepthStencilAttachmentWrite, ZrPipelineStageBits::LateFragmentTests);
        },
        [&](RgContext&) {
            cmdList->SetPipelineState(framePso);
            cmdList->SetRenderTarget(g_target.colorRtv[slot], g_target.depthDsv[slot]);
            cmdList->SetViewportScissor(g_target.width, g_target.height);
            // 逐项绑定：场景里每个物件可能是不同 mesh（VB/IB 都得换），
            // 同一 mesh 连续出现时只换常量缓冲（常见情形：一批敌人/道具）。
            ZrBuffer boundVb {};
            ZrBuffer boundIb {};
            for (uint32_t i = 0; i < frameDrawCount; ++i) {
                const FrameDraw& draw = g_frameDraws[i];
                if (draw.vb.raw != boundVb.raw) {
                    ZrBuffer vbs[1] = {draw.vb};
                    cmdList->SetVertexBuffers(vbs, 1u);
                    boundVb = draw.vb;
                }
                if (draw.ib.raw != boundIb.raw) {
                    cmdList->SetIndexBuffer(draw.ib);
                    boundIb = draw.ib;
                }
                cmdList->SetGraphicsConstantBuffer(0u, g_target.cb[i]);
                cmdList->DrawIndexedInstanced(draw.indexCount, 1u, 0u, 0u, 0u);
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
    ZrImageBarrier toSrv = MakeBarrier(g_target.color[slot], layoutNow,
        ZrImageLayout::ShaderReadOnlyOptimal,
        doReadback ? ZrAccessBits::TransferRead : ZrAccessBits::ColorAttachmentWrite,
        doReadback ? ZrPipelineStageBits::Transfer : ZrPipelineStageBits::ColorAttachmentOutput,
        ZrAccessBits::ShaderRead, ZrPipelineStageBits::FragmentShader);
    cmdList->ResourceBarrier(&toSrv, 1u, nullptr, 0u);

    // 深度回 Undefined（下一帧 RG 起点假设；与 --draw 一致）
    ZrImageBarrier depthReset = MakeBarrier(g_target.depth[slot],
        ZrImageLayout::DepthStencilAttachmentOptimal, ZrImageLayout::Undefined,
        ZrAccessBits::DepthStencilAttachmentWrite, ZrPipelineStageBits::LateFragmentTests,
        ZrAccessBits::None, ZrPipelineStageBits::TopOfPipe);
    cmdList->ResourceBarrier(&depthReset, 1u, nullptr, 0u);

    cmdList->End();
    g_target.queue->Submit(g_target.cmd);

    // ---- 提交后只 Signal，不排空（TASK-B-UNITY-04）--------------------------------
    // 旧实现这里 device->WaitIdle()：CPU 必须等本帧 GPU 干完才返回，帧间完全串行。
    // 现在只把"本帧完成"记进 fence：下一帧开头等 N-1 的完成，本帧的完成由再下一帧
    // 回收。宿主采样的是上一帧那个槽位（已确认完成），画面不会被撕裂。
    ++g_target.syncValue;
    if (g_target.syncFence != nullptr) {
        device->D3D12Queue()->Signal(g_target.syncFence, g_target.syncValue);
    }

    graph.FlushRetired(*device);

    ++g_target.frameCount;
    // 交给宿主的永远是"上一帧那份"；帧 0 还没有上一帧 → 保持 0 号槽
    if (g_target.frameCount >= 2u) {
        g_target.readableSlot = otherSlot;
    }
    if (scenePath) {
        ++g_scene.frameCount;
        g_scene.drawTotal += frameDrawCount;
        g_scene.lastDrawCount = frameDrawCount;
    }
    if (doReadback) {
        // 回读结果要等这一帧真正完成才有效 → 下一帧的帧首同步之后取
        g_target.pendingReadback = true;
    }
    return kOk;
}

void ReleaseTarget(void)
{
    // 只释放目标与渲染链，保留设备包装：尺寸变化时可直接再 CreateTarget，
    // 不必回头找宿主要一张纹理重新接入。进程退出时随 DLL 卸载一并回收。
    DestroyTargetObjects();
    // TASK-B-PSO-01：Unity 侧设备包装按设计不在 ReleaseTarget 销毁（DLL 卸载才回收），
    // 析构里的自动刷写因此不会在播放器退出时发生 —— 这里显式刷写 PSO 缓存
    // （幂等：没有新条目时不写盘；尺寸变化时只是空刷写，代价可忽略）。
    if (g_target.device != nullptr) {
        g_target.device->FlushPsoCache();
    }
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
        reinterpret_cast<uintptr_t>(g_target.sharedHandle[0]));
    for (uint32_t i = 0; i < 4u; ++i) {
        outStats->centerRGBA[i] = g_target.centerRGBA[i];
    }
    outStats->lastError = g_target.lastError;
    return kOk;
}

bool HasTarget(void)
{
    return g_target.color[0].IsValid();
}

const char* EmbeddedFirstDrawHlsl(uint32_t* outSize)
{
    const uint32_t size = static_cast<uint32_t>(sizeof(kFirstDrawHlsl) - 1u);
    if (outSize != nullptr) {
        *outSize = size;
    }
    return kFirstDrawHlsl;
}

// ---- TASK-B-UNITY-03：场景提交 ------------------------------------------------

int SetCamera(const float* view16, const float* proj16)
{
    if (view16 == nullptr || proj16 == nullptr) {
        return kErrNull;
    }
    std::memcpy(g_scene.view, view16, sizeof(g_scene.view));
    std::memcpy(g_scene.proj, proj16, sizeof(g_scene.proj));
    g_scene.cameraSet = true;
    return kOk;
}

int CreateMesh(const float* vertices, uint32_t vertexCount, uint32_t strideFloats,
    const uint32_t* indices, uint32_t indexCount, uint64_t* outHandle)
{
    if (vertices == nullptr || indices == nullptr || outHandle == nullptr) {
        return kErrNull;
    }
    *outHandle = 0u;
    if (g_target.device == nullptr) {
        return kErrNoDevice;
    }
    // 只支持 v1 固定顶点布局（Pos3f+Normal3f+Uv2f = 8 float / 32B）：
    // 与 first_draw.hlsl / ZrD3D12_GraphicsInputLayout 的 stride 同源。
    if (vertexCount == 0u || indexCount == 0u || strideFloats != 8u) {
        return kErrArgs;
    }

    uint32_t slot = kMeshSlots;
    for (uint32_t i = 0; i < kMeshSlots; ++i) {
        if (!g_meshes[i].used) {
            slot = i;
            break;
        }
    }
    if (slot == kMeshSlots) {
        return kErrState; // 槽位耗尽（512 种 mesh 对常规场景足够）
    }

    MeshEntry& entry = g_meshes[slot];
    ZrBufferDesc vbDesc {};
    vbDesc.size = static_cast<uint64_t>(vertexCount) * 32u;
    vbDesc.usage = ZrBufferUsage::Vertex | ZrBufferUsage::TransferDst;
    const ZrBuffer vb = g_target.device->CreateBuffer(vbDesc, vertices);
    ZrBufferDesc ibDesc {};
    ibDesc.size = static_cast<uint64_t>(indexCount) * 4u;
    ibDesc.usage = ZrBufferUsage::Index | ZrBufferUsage::TransferDst;
    const ZrBuffer ib = g_target.device->CreateBuffer(ibDesc, indices);
    if (!vb.IsValid() || !ib.IsValid()) {
        if (vb.IsValid()) {
            g_target.device->Destroy(vb);
        }
        if (ib.IsValid()) {
            g_target.device->Destroy(ib);
        }
        return kErrDevice;
    }

    if (entry.generation == 0u) {
        entry.generation = 1u; // 槽位首次启用
    }
    entry.used = true;
    entry.vb = vb;
    entry.ib = ib;
    entry.indexCount = indexCount;
    ++g_scene.uploadCount;
    *outHandle = MakeMeshHandle(slot, entry.generation);
    return kOk;
}

void DestroyMesh(uint64_t handle)
{
    MeshEntry* entry = LookupMesh(handle);
    if (entry == nullptr || g_target.device == nullptr) {
        return;
    }
    // 缓冲可能被上一帧的命令列表引用：等 GPU 排空再放（调用频率 = mesh 种类数，非每帧）
    g_target.device->WaitIdle();
    if (entry->vb.IsValid()) {
        g_target.device->Destroy(entry->vb);
    }
    if (entry->ib.IsValid()) {
        g_target.device->Destroy(entry->ib);
    }
    entry->vb = ZrBuffer {};
    entry->ib = ZrBuffer {};
    entry->indexCount = 0u;
    entry->used = false;
    entry->generation += 1u; // 代际推进：旧句柄此后全部失效
}

int DrawMesh(uint64_t handle, const float* world16, const float* rgba)
{
    if (world16 == nullptr) {
        return kErrNull;
    }
    const MeshEntry* mesh = LookupMesh(handle);
    if (mesh == nullptr) {
        return kErrHandle;
    }
    if (g_scene.drawCount >= kMaxSceneDraws) {
        return kErrState;
    }
    SceneDrawCmd& cmd = g_scene.draws[g_scene.drawCount];
    cmd.meshSlot = static_cast<uint32_t>(handle & 0xFFFFFFFFull);
    cmd.meshGeneration = static_cast<uint32_t>(handle >> 32);
    std::memcpy(cmd.world, world16, sizeof(cmd.world));
    if (rgba != nullptr) {
        std::memcpy(cmd.rgba, rgba, sizeof(cmd.rgba));
    } else {
        cmd.rgba[0] = 1.0f;
        cmd.rgba[1] = 1.0f;
        cmd.rgba[2] = 1.0f;
        cmd.rgba[3] = 1.0f;
    }
    ++g_scene.drawCount;
    return kOk;
}

int BeginScene(void)
{
    if (g_target.device == nullptr) {
        return kErrNoDevice;
    }
    g_scene.drawCount = 0u;
    return kOk;
}

int EndScene(void)
{
    if (g_target.device == nullptr) {
        return kErrNoDevice;
    }
    g_scene.lastDrawCount = g_scene.drawCount;
    return kOk;
}

int GetSceneStats(SceneStats* out)
{
    if (out == nullptr) {
        return kErrNull;
    }
    out->structSize = static_cast<uint32_t>(sizeof(SceneStats));
    out->drawCount = g_scene.lastDrawCount;
    uint32_t meshes = 0u;
    for (uint32_t i = 0; i < kMeshSlots; ++i) {
        if (g_meshes[i].used) {
            ++meshes;
        }
    }
    out->meshCount = meshes;
    out->reserved = 0u;
    out->uploadCount = g_scene.uploadCount;
    out->sceneFrameCount = g_scene.frameCount;
    out->drawTotal = g_scene.drawTotal;
    return kOk;
}

} // namespace unity_target
} // namespace zr
