// zupscaler.cpp —— 超分模块占位实现（TASK-B-R0-01）
#include "zupscaler/zupscaler.h"

extern "C" {

const char* ZUP_GetVersion(void)
{
    return ZR_VERSION_STRING; // 由 CMake 注入，单一事实来源
}

} // extern "C"

namespace zr {

bool ZupResizeWithinLimits(uint32_t inputWidth, uint32_t inputHeight, uint32_t outputWidth,
    uint32_t outputHeight, uint32_t maxInputWidth, uint32_t maxInputHeight,
    uint32_t maxOutputWidth, uint32_t maxOutputHeight)
{
    // 尺寸上限是资源分配契约的一部分（TASK-H-02 D2）：输入/输出都必须 ≤ 创建时
    // 上限，任一维度为 0 或输出小于输入（下采样）一律拒绝。
    if (inputWidth == 0u || inputHeight == 0u || outputWidth == 0u || outputHeight == 0u) {
        return false;
    }
    if (inputWidth > maxInputWidth || inputHeight > maxInputHeight) {
        return false;
    }
    if (outputWidth > maxOutputWidth || outputHeight > maxOutputHeight) {
        return false; // 输出资源按 maxOutput* 分配，超限 dispatch 会写越分配内存
    }
    if (outputWidth < inputWidth || outputHeight < inputHeight) {
        return false; // 超分后端不接受下采样
    }
    return true;
}

} // namespace zr
