// =============================================================================
// zrhi_d3d12_unity_target.h —— Unity 共享渲染目标（TASK-B-UNITY-02）
//
// 定位：B-UNITY-01 打通了"包住宿主设备 + 对宿主纹理清屏"，但那需要宿主把
// **backbuffer** 交出来。验收已判定该路径有两条硬阻断（PRESENT→RENDER_TARGET
// 无隐式提升；跨队列 fence 不覆盖 Unity 队列），因此本文件走另一条：
//
//   ZRender 渲染到自己的共享纹理 → 导出 NT 句柄 → Unity 采样这张纹理并贴屏
//
// 状态与同步全部关在本模块内部（自己建、自己转、自己等），Unity 侧不碰
// backbuffer 状态、也不需要跨队列 fence。
//
//   CreateTarget(w,h)  ≈ vkCreateImage(SHARED) + vkGetMemoryFdKHR（D3D12：
//                        D3D12_HEAP_FLAG_SHARED + CreateSharedHandle）
//   GetTargetHandle()  ≈ 取上面的外部句柄（Unity CreateExternalTexture 的入参）
//   RenderFrame()      ≈ 一帧渲染 + vkCmdPipelineBarrier(SHADER_READ_ONLY) + 等待
//   ReleaseTarget()    ≈ vkDestroyImage + CloseHandle
//
// 线程模型：单线程（Unity 渲染线程）；与 zrhi 其余部分一致。
// 头文件**刻意不包含 D3D12 头**（zplugin 的 C ABI 层只 include 本头）。
// =============================================================================
#pragma once

#include <cstdint>

// TASK-B-PLANET-04：行星通道 API 消费 zplugin.h 的 POD 定宽结构（ZrPlanetChunkDesc /
// ZrPlanetStats / ZrPlanetWorld——B-PLANET-02 冻结布局）。仅头文件类型消费、零链接
// 依赖：zrhi 不调用 zplugin 的导出函数（方向始终是 zplugin 薄转发 → 本模块），
// include 不构成模块耦合（与 zrhi→zrender_core 的既有方向性约束同类）。
#include "zplugin/zplugin.h"

namespace zr {
namespace unity_target {

// 错误码（0 = 成功；与 zplugin.h 的 ZR_UNITY_* 一一对应）
constexpr int kOk = 0;           // 成功
constexpr int kErrNull = -1;     // 空指针参数
constexpr int kErrNoDevice = -2; // 尚未接入宿主设备
constexpr int kErrDevice = -3;   // 设备包装 / D3D12 资源创建失败
constexpr int kErrState = -5;    // 状态错误（未建目标就渲染 / 重复建目标 / 绘制项溢出）
constexpr int kErrArgs = -6;     // 参数非法（尺寸越界 / 顶点布局不支持）
constexpr int kErrHandle = -7;   // 句柄无效（TASK-B-UNITY-03：mesh 句柄）

/// 一帧的统计（跨 ABI 的 POD；zplugin 转成 ZrUnityTargetStats）。
/// centerRGBA 来自渲染完的**纹理回读**（不是屏幕抓取）——它是"这张纹理里到底
/// 有什么颜色"的直接证据，与 --draw 的中心像素校验同源。
struct TargetStats {
    uint32_t frameCount = 0;     // RenderFrame 成功次数（真调用计数）
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t readbackCount = 0;  // 回读次数（每 kReadbackInterval 帧一次）
    uint64_t sharedHandle = 0;   // NT 句柄（0 = 未创建）
    uint32_t centerRGBA[4] = {0u, 0u, 0u, 0u}; // 最近一次回读的中心像素（8bit）
    uint32_t lastError = 0;      // 最近一次失败的 ZR_UNITY_ERR_*（0 = 无）
};

/// 由宿主（Unity）的一张 D3D12 纹理反查其设备并包装。
/// 为什么是这个入口：托管侧只有 Texture.GetNativeTexturePtr() 给出的
/// ID3D12Resource*，拿不到 ID3D12Device*；资源自己能反查设备
/// （ID3D12Resource::GetDevice）。重复调用幂等（释放旧包装）。
int AttachDeviceFromTexture(void* d3d12Texture);

/// 创建共享颜色目标（BGRA8，单 mip/单层/单采样）。已存在则先释放再重建（尺寸变化）。
int CreateTarget(uint32_t width, uint32_t height);

/// 取 NT 共享句柄（跨进程/跨 API 交接用）。
int GetTargetHandle(uint64_t* outHandle);

/// 取目标纹理的原生资源指针（同进程内交给宿主包装成外部纹理用）。
/// 为什么要两条路：Unity 2022.3 的 D3D12 后端对 CreateExternalTexture 的 nativeTex
/// 期望 ID3D12Resource*；传 NT 句柄会在首帧崩在 UnityPlayer（实测 0xc0000005）。
int GetTargetResource(void** outResource);

/// 渲染一帧到**当前槽位**，帧末把纹理转到 PIXEL_SHADER_RESOURCE 后提交并 Signal
/// 本队列的 fence；**不排空队列**（TASK-B-UNITY-04）。可安全采样的槽位由
/// <see cref="GetReadableTargetIndex"/> 给出（= 上一帧那份，已确认完成）。
///
/// 帧内容由**调用方**决定（TASK-B-UNITY-03）：
///   - 本帧经 SetCamera + 至少一次 DrawMesh 提交了内容 → 画提交的内容；
///   - 没有提交（或只有相机没有绘制项）→ 回落 B-UNITY-02 的硬编码三 cube 演示，
///     这样 zrhi_d3d12_window --draw 与既有验证路径的行为完全不变。
int RenderFrame(void);

// ---- TASK-B-UNITY-03：场景提交（相机 + 网格 + 绘制）-----------------------------
//
// 调用顺序（每帧）：
//   SetCamera(view, proj)                        // 行主序（本引擎约定，见下）
//   BeginScene()                                 // 清空本帧绘制表（相机保留）
//   DrawMesh(handle, world, rgba) × N            // N ≤ kMaxSceneDraws
//   RenderFrame()                                // 真正录制并提交
//   EndScene()                                   // 可选：仅置状态位（计数用）
//
// **矩阵约定（最容易翻车的地方）**：本模块与 first_draw.hlsl 一致，用**行主序
// 存储 + 列向量**：clip = mvp * float4(pos,1)，平移量落在下标 3/7/11。Unity 的
// Matrix4x4 是列主序存储（平移量落在 12/13/14），因此托管侧必须**按元素转置**
// （dst[r*4+c] = m[r,c]）后再传进来，直接 memcpy 会得到转置画面。
//
// 深度约定：PSO 固定 D3D 标准深度（LESS，清 1.0，near→0/far→1）。调用方给的
// proj **必须是标准 [0,1] 深度且 NDC +Y 朝上**；Unity 侧要先把
// GL.GetGPUProjectionMatrix 的 Y 翻转与反向 Z 归一化掉（见 ZRenderSceneBridge）。

constexpr uint32_t kMaxSceneDraws = 1024u; // 单帧绘制项上限（超出返回 kErrState）

/// 设置本帧相机。view/proj 各 16 个 float（行主序，见上）；空指针返回 kErrNull。
int SetCamera(const float* view16, const float* proj16);

/// 上传一个网格（顶点 32B/顶点：pos3f + normal3f + uv2f，与 first_draw.hlsl 的
/// 输入布局同源；strideFloats 必须是 8）。成功返回 kOk 并写 outHandle。
/// 每份网格只该上传一次——调用方按 mesh 缓存句柄，别每帧调本函数。
int CreateMesh(const float* vertices, uint32_t vertexCount, uint32_t strideFloats,
    const uint32_t* indices, uint32_t indexCount, uint64_t* outHandle);

/// 释放网格（句柄立即失效，代际推进）。无效句柄静默忽略。
void DestroyMesh(uint64_t handle);

/// 提交一次绘制：world 为 16 float 行主序 model 矩阵，rgba 为基色（可为空 = 白）。
int DrawMesh(uint64_t handle, const float* world16, const float* rgba);

/// 开始新一帧：清空绘制表（相机与网格句柄表保留）。
int BeginScene(void);

/// 结束本帧（仅置状态位并累计统计；真正提交在 RenderFrame）。
int EndScene(void);

/// 场景提交统计（POD，跨 ABI 用；"mesh 只上传一次"的证据在此）。
struct SceneStats {
    uint32_t structSize;      // 由实现填 sizeof(SceneStats)
    uint32_t drawCount;       // 最近一帧提交的绘制项数（0 = 走了 cube 回落）
    uint32_t meshCount;       // 句柄表里存活的网格数
    uint32_t reserved;        // 对齐占位（保持 8B 字段对齐）
    uint64_t uploadCount;     // 累计 CreateMesh 成功次数（应恒等于 mesh 种类数）
    uint64_t sceneFrameCount; // 走过场景路径的帧数
    uint64_t drawTotal;       // 累计提交的绘制项数
};

/// 取场景统计；out 为空返回 kErrNull。
int GetSceneStats(SceneStats* out);

// ---- TASK-B-UNITY-04：帧同步（fence 双缓冲）----------------------------------
//
// 背景：B-UNITY-02/03 每帧 Submit 后 `WaitIdle()`，CPU 必须等 GPU 把本帧干完才
// 返回——完全没有帧间并行。现在改成：
//   帧 N 提交后只等**帧 N-1** 的 fence（上一帧早已完成），本帧的完成由下一帧回收；
//   纹理与深度各两套（双缓冲），交给宿主采样的是上一帧那份（已确认完成）。
// 这样 CPU 每帧的等待从"整帧 GPU 时间"降到"通常 0"，画面也不会被撕裂（宿主采样的
// 永远是已经画完的槽位，而不是正在写的槽位）。
//
// 为什么必须双缓冲纹理：单张纹理在"不等本帧"的前提下必然被下一帧覆盖，宿主会采到
// 画了一半的内容；两套槽位后写读分离，才谈得上不撕裂。

/// 同步统计（POD；"CPU 等待下降"的现场证据）。
struct SyncStats {
    uint32_t structSize;    // 由实现填 sizeof(SyncStats)
    uint32_t frames;        // RenderFrame 调用数
    uint32_t blockedFrames; // 实际阻塞过（等待 ≥ kBlockedThresholdMs）的帧数
    uint32_t framesInFlight;// 当前在途帧数（0..2；WaitIdle 版恒为 0）
    double lastWaitMs;      // 最近一次 fence 等待耗时
    double avgWaitMs;       // 累计平均等待耗时
};

/// 取同步统计；out 为空返回 kErrNull。
int GetSyncStats(SyncStats* out);

/// 取第 index 个（0/1）颜色目标的 ID3D12Resource*（宿主为每个槽位各建一张外部纹理）。
/// index 越界返回 kErrArgs。
int GetTargetResourceAt(uint32_t index, void** outResource);

/// 取"已完成、可以安全采样"的槽位号（= 上一帧渲染的槽位；帧 0 之后才有意义）。
int GetReadableTargetIndex(uint32_t* outIndex);

// ---- TASK-B-UNITY-05：整面回读 -------------------------------------------------
//
// 为什么需要它：**同一块共享显存在 D3D12 里可以有两个 ID3D12Resource 对象**
// （DLL 自己那个 + 宿主 OpenSharedHandle 出来的那个），而 D3D12 的**状态跟踪是
// 按对象走的**。宿主在自己那个对象上做 PSR→COPY_SOURCE 的屏障，并不能把"另一个
// 对象用 RTV 写进去的内容"变成可线性拷贝的状态：实测（AMD 780M）整面
// CopyTextureRegion 回读会得到"每 8 行只有 1 行有内容、其余 7 行为 0"的结果，
// 而行距是对的（RowPitch 2560 = width*4，两个对象的脚印完全一致）。
// 反过来，先用**真正被渲染的那个对象**把状态落回 COMMON，同一个 imported 对象
// 立刻能读到全部 480 行 —— 这是本函数的全部理由：状态只由拥有它的那侧转。
//
// 语义：
//   · 读槽位 0（= GetTargetResource / GetTargetHandle 暴露的那一张）；
//   · 先把本队列排空到此刻，再转 COPY_SOURCE → 拷贝 → 转回 PIXEL_SHADER_RESOURCE，
//     读完纹理仍然处于 Unity 可采样态；
//   · **逐行**用设备给的真实脚印拷贝（RowPitch/SlicePitch 一律问
//     GetCopyableFootprints，绝不假设 width*4），dstRowPitch 是调用方缓冲的行距；
//   · 返回时 outWidth/outHeight 写实际尺寸（可为空）。
//
// dst 为空 → kErrNull；dstRowPitch < width*4 → kErrArgs；无目标 → kErrState。
int ReadTargetPixels(void* dst, uint32_t dstRowPitch, uint32_t* outWidth, uint32_t* outHeight);

/// 释放目标与所有渲染资源（解除设备包装，不动宿主设备本身）。
void ReleaseTarget(void);

/// 取统计（outStats 为空返回 kErrNull）。
int GetStats(TargetStats* outStats);

/// 当前是否已建好可渲染的目标。
bool HasTarget(void);

/// 诊断/单测用：模块内嵌的 first_draw.hlsl 源码指针（不出 ABI，仅供
/// tests/test_zrhi_unity_target 与 Native/shaders/first_draw.hlsl 做逐字节比对，
/// 守住"嵌入式副本不得与仓库里的 shader 漂移"）。outSize 写入字节数（不含 NUL）。
const char* EmbeddedFirstDrawHlsl(uint32_t* outSize);

/// 诊断/单测用（TASK-B-PLANET-04）：内嵌 planet2.hlsl 源码指针（不出 ABI，
/// 与 Native/shaders/planet2.hlsl 做逐字节比对——first_draw 同规矩）。
const char* EmbeddedPlanet2Hlsl(uint32_t* outSize);

// ---- TASK-B-PLANET-04：行星 chunk 通道（句柄表 + 顶点生成 + GPU 生命周期）--------
//
// 设计依据：Docs/Planning/P2-行星ABI设计.md §4（生命周期状态机）/§4.5（校验链）、
// §7（内存所有权/泄漏防护四层）、§8（线程/延迟一帧）。这些函数是 zplugin.cpp 六导出
// （ChunkCreate/ChunkDestroy/ChunkSetOrigin/SetLod/GetStats/ClearAll）的实现落点，
// 同时承接 SetCameraOrigin 存储的 B-PLANET-05→04 接管迁移（zplugin 侧变为薄转发，
// ABI 语义不变）——zrhi 不反向依赖 zplugin，因此渲染线程可读的状态必须落在本模块。
//
// 状态机（每槽位）：EMPTY → PENDING_UPLOAD（顶点已生成待上传） → LIVE →
// PENDING_FREE（句柄已失效、VB 保留到下帧，期间仍被绘制——先画后删） → EMPTY。
// GPU 上传/释放/档位应用只在渲染线程 RenderFrame 边界（§8 延迟一帧）。
namespace planet {

/// 设置行星上下文镜像（seed + 行星中心，米）。由 ZR_Planet_SetWorld 校验通过后
/// 调用（zplugin 持有 B-PLANET-06 权威快照，本模块持有渲染线程用的镜像副本——
/// LOD 重建在 RenderFrame 内重生成顶点时需要 seed/center）。v1 初始化期调用。
int SetWorldContext(uint32_t seed, const double centerAbs[3]);

/// 设置相机绝对原点（行星中心系，米）。B-PLANET-05 存储接管后的唯一落点；
/// zplugin 的 Set/Get 薄转发至此。渲染线程在 RenderFrame 读它算 chunk 模型平移
/// （double 域相减后一次 cast，§3.2/D3）。
void SetCameraOrigin(double x, double y, double z);

/// 取相机绝对原点（outOrigin 写 3 元素；调用方保证非空——zplugin 已校验）。
void GetCameraOrigin(double* outOrigin);

/// 创建 chunk（zplugin 已过 NULL/ABI/RANGE/NONFINITE/STATE 校验链）：
/// 顶点在锁外按规范路由生成（§8），独占锁内占槽发布（FULL 在此裁决）。
/// 成功写 *outHandle 并返回 ZR_PLANET_OK。
int ChunkCreate(const ZrPlanetChunkDesc& desc, uint64_t* outHandle);

/// 销毁 chunk：句柄立即失效（代际+1，独占锁内）；VB 下一 RenderFrame 释放
/// （期间仍绘制一帧——先画后删）。无效/重复句柄返回 ZR_PLANET_ERR_HANDLE。
int ChunkDestroy(uint64_t handle);

/// 更新锚点绝对坐标。顶点不重算（局部坐标相对锚点不变，§4.3）。
int ChunkSetOrigin(uint64_t handle, double x, double y, double z);

/// 设置期望 LOD 档位与邻居掩码（zplugin 已过 RANGE 校验）。下一 RenderFrame 应用。
int SetLod(uint64_t handle, uint32_t lodLevel, uint32_t neighborMask);

/// 取统计快照（共享锁；账目恒等式 createTotal − destroyTotal == liveChunks 全程成立
/// ——ClearAll/桥级释放按销毁记账保持恒等，§7.3）。
int GetStats(ZrPlanetStats* out);

/// 一次性清空全部 chunk（句柄全失效 + 渲染线程排队释放；销毁记账保恒等）。
void ClearAll(void);

/// 测试钩子（不出 def；doctest 进程级单例复位——与 ZR_Upscaler 测试复位同策）：
/// 清空全部 chunk/统计/上下文镜像（不动相机原点——B-PLANET-05 用例自管理）。
void TestResetForUnitTests(void);

} // namespace planet

} // namespace unity_target
} // namespace zr
