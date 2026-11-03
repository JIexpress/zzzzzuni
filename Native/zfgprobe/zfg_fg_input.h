// =============================================================================
// zfg_fg_input.h —— 代理与 hook 之间的 FG 每帧输入契约（TASK-B-R9-03）
//
// 为什么单独一个头：C# 侧（托管 probe）→ dxgi.dll 代理 → zfg_hook.dll 两跳
// 握手，三处都必须用**同一份 POD 布局**。跨模块边界只传 POD + void*，异常不得
// 跨越（AGENTS.md / B-R9-01 C ABI 纪律）。
//
// 字段口径（依据 B-R9-02 实测，见 Docs/Logs/TASK-B-R9-02.md）：
//   - width/height 为**渲染分辨率**（= 交换链尺寸，本集成不做上采样）；
//   - depth/mv 为 ID3D12Resource*（Texture.GetNativeTexturePtr()），
//   - mvScale 取 (W, -H)：Unity 的 MV 是"半个 NDC"增量，乘 W 得像素（y 向下）；
//   - jitter 恒 0：URP 的 MV 用 _NonJitteredViewProjMatrix 算，不含 TAA 抖动，
//     因此 jitterCancellation 必须为 false（F2）；
//   - reset：相机不连续/首帧/尺寸变化 → FFX 丢历史。
// =============================================================================
#pragma once

#include <cstdint>

/// 结构版本（不匹配 → 接收方按"非法输入"拒收，不猜布局）
constexpr uint32_t kZfgFgInputsVersion = 1u;

struct ZfgFgFrameInputs {
    uint32_t structSize;      // = sizeof(ZfgFgFrameInputs)
    uint32_t version;         // = kZfgFgInputsVersion
    void* depthResource;      // ID3D12Resource*（非 MSAA 的深度拷贝，R32_SFloat）
    void* mvResource;         // ID3D12Resource*（R16G16_SFloat）
    uint32_t width;           // 渲染分辨率
    uint32_t height;
    uint32_t depthState;      // FfxApiResourceState 位组合（PrepareV2 读取状态）
    uint32_t mvState;
    float jitterX;            // 渲染分辨率亚像素；本集成恒 0
    float jitterY;
    float mvScaleX;           // 像素换算（本集成 = width）
    float mvScaleY;           // （本集成 = -height）
    float frameTimeMs;        // 上一帧耗时
    float fovY;               // 垂直 FOV（弧度）
    float nearZ;
    float farZ;
    float camPos[3];
    float camUp[3];
    float camRight[3];
    float camForward[3];
    int32_t reset;            // 0 = 延续历史；1 = 复位
    int32_t reserved;
};

/// 统计出口（探针/验收读取"generated 随 real 增长"的直接证据）
///
/// TASK-B-R9-06：追加 haveInputs/reserved（尾插，40 → 48 字节）。C# 侧
/// ZfgFgStatsNative 必须逐字段对齐；改动布局时**两侧同时改**（该结构没有版本
/// 字段：多出的尾巴由接收方按 0 读，少写不会越界，多写才会）。
struct ZfgFgStats {
    uint64_t realPresents;    // 真帧计数
    uint64_t generatedPresents; // 插帧计数
    uint64_t prepareCount;    // 成功派发 PrepareV2 的次数
    uint64_t prepareFailed;   // PrepareV2/Configure 失败次数
    uint32_t enabled;         // 当前是否在插帧（上一帧 WrapOnFrame 的结论）
    uint32_t lastReturnCode;  // 最后一次 ffx 返回码
    uint32_t haveInputs;      // 1 = 至少收到过一次合法 depth/MV 提交（含已过期）
    uint32_t reserved;        // 0；对齐到 48 字节
};
