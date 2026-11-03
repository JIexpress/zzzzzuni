// =============================================================================
// ssr_pass.h —— 屏幕空间反射（SSR）CPU 侧纯逻辑与 RG 声明（TASK-B-R5-03）
//
// 简化版 SSR（简报指定）：
//   - 全屏深度 → Hi-Z mip 链（≤6 级，min 衰减）；射线在屏幕 UV 空间步进
//     （固定 32 步 + 二分细化 4 步），逐步按步距升 mip 采样 Hi-Z 加速
//   - 半分辨率 trace RT；4-tap 空间模糊；屏幕边缘 fade
//   - 简化边界（施工报告诚实清单）：无时间降噪/历史复用；反射法线取地平面
//     常数 (0,1,0)（应用目标=地面，非 G-buffer 法线）；粗糙度阈值经
//     sceneColor.a 通道近似（全量版应读 G-buffer roughness）；阈值之上回落
//     环境项（GI 探针填充）；静态采样器 Point/Clamp（无双线性/各向异性）
//
// 与 GPU 侧的契约：
//   Native/shaders/ssr_trace.hlsl 的 CBufSsr 与 SsrTraceConstants（static_assert
//   锁定）逐字节一致；ssr_blur.hlsl 的 4-tap 偏移与 SsrBlurTapOffsets 一致；
//   Hi-Z mip 链级别数与 hiz.hlsl / 窗口程序 --r5 的 HiZBuild 一致。
//
// RecordSsrPasses：SSR 特性的 RG 资源依赖声明单一来源（单测以 mock 设备验证
// 编译/生命周期/屏障顺序；窗口程序 --r5 以真实 execute 回调经同一函数录图）。
//
// 仅纯 CPU 逻辑（zrhi 接口类型用于 RG 声明，无后端依赖）。
// 命名遵循 file-conventions.md §3。
// =============================================================================
#pragma once

#include <cstdint>

#include "zrender/rg_render_graph.h"

// =============================================================================
// 常量（简报指定；GPU 侧 ssr_trace.hlsl / hiz.hlsl 注释同步）
// =============================================================================
constexpr uint32_t ZR_SSR_STEP_COUNT = 32;   // 固定步进数
constexpr uint32_t ZR_SSR_BISECT_STEPS = 4;  // 二分细化步数
constexpr uint32_t ZR_SSR_BLUR_TAPS = 4;     // 空间模糊 tap 数
constexpr uint32_t ZR_SSR_MAX_MIPS = 6;      // Hi-Z mip 链上限（简报：最多 6 级）
constexpr uint32_t ZR_SSR_HALF_RES = 2;      // trace/blur RT 半分辨率因子
constexpr float ZR_SSR_ROUGHNESS_MAX = 0.3f; // 粗糙度阈值（> 阈值回落环境项）
constexpr float ZR_SSR_STEP_METERS = 0.12f;  // 视空间固定步长（米）
constexpr float ZR_SSR_EDGE_FADE = 0.08f;    // 屏幕边缘 fade 宽度（UV）

// =============================================================================
// SsrMipChain —— Hi-Z mip 链计划（纯函数）
// =============================================================================
struct SsrMipLevel {
    uint32_t width = 0;
    uint32_t height = 0;
};

/// Hi-Z mip 链：level 0 = 原尺寸，逐级减半（每轴 min(size/2, 1)），
/// 级数 = min(maxMips, floor(log2(max(w,h))) + 1)。
/// 返回级数（≥1）；非法参数（w/h=0 或 out 为空）返回 0。
uint32_t SsrMipChainPlan(uint32_t width, uint32_t height, uint32_t maxMips,
    SsrMipLevel* outLevels);

// =============================================================================
// 4-tap 空间模糊核（对角 ±半 texel；GPU 侧 ssr_blur.hlsl 同偏移）
// =============================================================================
/// outOffsets[4][2] = {(−1,−1),(+1,−1),(−1,+1),(+1,+1)}（乘半 texel 前的核）
void SsrBlurTapOffsets(float outOffsets[ZR_SSR_BLUR_TAPS][2]);

// =============================================================================
// SsrTraceConstants —— trace pass b0 布局（与 ssr_trace.hlsl CBufSsr 一致）
//   invViewProj  row_major：ndc(uv,depth) → world（射线起点重建）
//   viewProj     row_major：world → ndc（march 点投影回屏幕）
//   projParams   x=A=zf/(zf-zn) y=B=zn*zf/(zf-zn) z=nearZ w=farZ
//   ssrParams    x=粗糙度阈值 y=步长(米) z=边缘 fade 宽度 w=SSR 开关
//   screenParams x=全宽 y=全高 z=半宽 w=半高
//   camPos       xyz=相机世界位置
// 合计 64+64+16+16+16+16 = 192B
// =============================================================================
struct SsrTraceConstants {
    float invViewProj[16];
    float viewProj[16];
    float projParams[4];
    float ssrParams[4];
    float screenParams[4];
    float camPos[4];
};
static_assert(sizeof(SsrTraceConstants) == 192, "SsrTraceConstants must be 192 bytes");

// =============================================================================
// RecordSsrPasses —— RG 特性声明（SSR Hi-Z 资源依赖的单一来源）
// =============================================================================
/// SSR 各 Pass 依赖的 RG 资源句柄（Imported/Transient 均可）
struct SsrPassHandles {
    RgHandle depth;      // 场景深度（Hi-Z 源；DSV 写→读走 split barrier）
    RgHandle hiz;        // Hi-Z mip 链（HiZBuild 写 → Trace 读）
    RgHandle sceneColor; // 场景颜色（Main 系写 → Trace/Composite 读）
    RgHandle ssrTrace;   // trace RT（Trace 写 → Blur 读）
    RgHandle ssrBlur;    // 模糊 RT（Blur 写 → Composite 读）
    RgHandle back;       // 合成目标（Composite 写）
};

/// 录制 4 个 Pass（声明+execute；execute 为空指针 = 无操作）：
///   HiZBuild   : Read(depth, DepthReadOnly) Write(hiz)——mip 链整链生成
///                （pass 内子资源状态由 execute 自管，报告注明）
///   SsrTrace   : Read(hiz) Read(sceneColor) Write(ssrTrace)——半分辨率 march
///   SsrBlur    : Read(ssrTrace) Write(ssrBlur)——4-tap 空间模糊
///   SsrComposite: Read(ssrBlur) Read(sceneColor) Write(back)——场景+反射合成
/// 调用方须保证句柄有效且执行序即上序（AddPass 声明序 = 编译拓扑输入）。
void RecordSsrPasses(RgGraph& graph, const SsrPassHandles& handles,
    const RgExecuteFn& hiZExec, const RgExecuteFn& traceExec, const RgExecuteFn& blurExec,
    const RgExecuteFn& compositeExec);
