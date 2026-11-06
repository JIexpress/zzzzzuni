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
constexpr int kErrState = -5;    // 状态错误（未建目标就渲染 / 重复建目标）
constexpr int kErrArgs = -6;     // 尺寸非法（0 或超上限）

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

/// 渲染一帧到共享目标，返回前把纹理转到 PIXEL_SHADER_RESOURCE 并等待 GPU 完成。
int RenderFrame(void);

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
