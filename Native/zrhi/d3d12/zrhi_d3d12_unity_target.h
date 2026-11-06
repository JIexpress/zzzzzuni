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

} // namespace unity_target
} // namespace zr
