// =============================================================================
// zrhi_d3d12_unity_bridge.h —— Unity 接入桥（TASK-B-UNITY-01）
//
// 定位：把"外部设备 + 外部纹理"这一组最小语义封装成后端内部接口，
// 由 zplugin 的 C ABI（ZR_Unity_*）逐条转发。**刻意不包含任何 D3D12 头**：
// zplugin 是纯 C ABI 层，不应被 D3D12 类型污染（它只 include 本头）。
//
// 语义（先求通，不求全）：
//   AttachDevice(device)      包装外部 ID3D12Device（ZrD3D12Device::CreateFrom），
//                             并准备桥私有的一帧命令链路（RTV 堆/分配器/命令表）。
//   BeginFrame(tex, w, h)     为传入纹理建 RTV（nullptr desc → mip0/全层，按资源格式）。
//   EndFrame()                记录 ClearRenderTargetView 并提交，WaitIdle 排空。
//
// 线程模型：单线程（Unity 渲染线程语义）；重复 Attach 幂等（先释放旧包装）。
// 状态绑定：纹理必须已处于 COMMON 状态（D3D12 隐式提升 COMMON→RENDER_TARGET 在
// 首次以 RTV 使用时生效）。**不做状态跟踪**：若调用方传入的纹理正处于其它显式
// 状态（如 COPY_DEST / PRESENT），本桥不插入屏障——这是"最小实现"的已知边界。
// =============================================================================
#pragma once

#include <cstdint>

namespace zr {
namespace unity_bridge {

// 错误码（0 = 成功；与 zplugin.h 的 ZR_UNITY_ERR_* 一一对应）
constexpr int kOk = 0;           // 成功
constexpr int kErrNull = -1;     // 空指针参数
constexpr int kErrNoDevice = -2; // 尚未 AttachDevice
constexpr int kErrDevice = -3;   // 外部设备包装失败（CreateFrom / 桥私有链路失败）
constexpr int kErrTexture = -4;  // 纹理不合法（非 TEXTURE2D / 尺寸不足）
constexpr int kErrState = -5;    // 帧状态错误（重复 BeginFrame / 无 BeginFrame 的 EndFrame）

/// 包装外部设备；返回 0 或 kErr*。已 Attach 时先释放旧包装（不释放宿主设备）。
int AttachDevice(void* d3d12Device);

/// 为纹理建 RTV；返回 0 或 kErr*。
int BeginFrame(void* backBufferTexture, uint32_t width, uint32_t height);

/// 对 BeginFrame 传入的纹理清屏并排空 GPU；返回 0 或 kErr*。
int EndFrame(void);

/// 释放桥资源并解除设备包装（宿主设备引用计数由宿主自己持有）。
int DetachDevice(void);

/// 自证/诊断：取当前包装的 ID3D12Device*（未 Attach 时返回 kErrNoDevice）。
int QueryAttachedDevice(void** outDevice);

} // namespace unity_bridge
} // namespace zr
