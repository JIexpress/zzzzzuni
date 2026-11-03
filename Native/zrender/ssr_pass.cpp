// =============================================================================
// ssr_pass.cpp —— SSR 纯逻辑实现（TASK-B-R5-03）
// =============================================================================
#include "zrender/ssr_pass.h"

#include <cmath>

// =============================================================================
// SsrMipChainPlan
// =============================================================================
uint32_t SsrMipChainPlan(uint32_t width, uint32_t height, uint32_t maxMips,
    SsrMipLevel* outLevels)
{
    if (width == 0u || height == 0u || maxMips == 0u || outLevels == nullptr) {
        return 0u;
    }
    // 级数上限：整链到 1×1 所需级数 = floor(log2(max(w,h))) + 1
    const uint32_t longest = (width > height) ? width : height;
    const uint32_t fullChain
        = static_cast<uint32_t>(std::floor(std::log2(static_cast<double>(longest)))) + 1u;
    uint32_t levels = (maxMips < fullChain) ? maxMips : fullChain;

    uint32_t w = width;
    uint32_t h = height;
    for (uint32_t i = 0; i < levels; ++i) {
        outLevels[i].width = w;
        outLevels[i].height = h;
        w = (w > 1u) ? w / 2u : 1u;
        h = (h > 1u) ? h / 2u : 1u;
    }
    return levels;
}

void SsrBlurTapOffsets(float outOffsets[ZR_SSR_BLUR_TAPS][2])
{
    // 对角 4 tap（顺时针自左上）
    outOffsets[0][0] = -1.0f;
    outOffsets[0][1] = -1.0f;
    outOffsets[1][0] = 1.0f;
    outOffsets[1][1] = -1.0f;
    outOffsets[2][0] = -1.0f;
    outOffsets[2][1] = 1.0f;
    outOffsets[3][0] = 1.0f;
    outOffsets[3][1] = 1.0f;
}

// =============================================================================
// RecordSsrPasses —— RG 声明（HiZBuild / SsrTrace / SsrBlur / SsrComposite）
// =============================================================================
void RecordSsrPasses(RgGraph& graph, const SsrPassHandles& handles, const RgExecuteFn& hiZExec,
    const RgExecuteFn& traceExec, const RgExecuteFn& blurExec, const RgExecuteFn& compositeExec)
{
    // HiZBuild：读深度（DSV 写→只读走 split barrier 计划）+ 写 Hi-Z 链。
    // 整链 mip 子资源的状态推进由 execute 内自管（per-mip RENDER_TARGET ↔
    // PIXEL_SHADER_RESOURCE），RG 层按整资源 ColorAttachment 声明——报告注明。
    graph.AddPass("HiZBuild",
        [&](RgPass& pass) {
            pass.Read(handles.depth, ZrImageLayout::DepthStencilReadOnlyOptimal,
                ZrAccessBits::DepthStencilAttachmentRead, ZrPipelineStageBits::FragmentShader);
            pass.Write(handles.hiz, ZrImageLayout::ColorAttachmentOptimal,
                ZrAccessBits::ColorAttachmentWrite, ZrPipelineStageBits::ColorAttachmentOutput);
        },
        hiZExec != nullptr ? hiZExec : RgExecuteFn());
    // SsrTrace：半分辨率 UV 空间 march（Hi-Z 加速）+ 反射颜色采样
    graph.AddPass("SsrTrace",
        [&](RgPass& pass) {
            pass.Read(handles.hiz, ZrImageLayout::ShaderReadOnlyOptimal,
                ZrAccessBits::ShaderRead, ZrPipelineStageBits::FragmentShader);
            pass.Read(handles.sceneColor, ZrImageLayout::ShaderReadOnlyOptimal,
                ZrAccessBits::ShaderRead, ZrPipelineStageBits::FragmentShader);
            pass.Write(handles.ssrTrace, ZrImageLayout::ColorAttachmentOptimal,
                ZrAccessBits::ColorAttachmentWrite, ZrPipelineStageBits::ColorAttachmentOutput);
        },
        traceExec != nullptr ? traceExec : RgExecuteFn());
    // SsrBlur：4-tap 空间模糊（对角 ±半 texel）
    graph.AddPass("SsrBlur",
        [&](RgPass& pass) {
            pass.Read(handles.ssrTrace, ZrImageLayout::ShaderReadOnlyOptimal,
                ZrAccessBits::ShaderRead, ZrPipelineStageBits::FragmentShader);
            pass.Write(handles.ssrBlur, ZrImageLayout::ColorAttachmentOptimal,
                ZrAccessBits::ColorAttachmentWrite, ZrPipelineStageBits::ColorAttachmentOutput);
        },
        blurExec != nullptr ? blurExec : RgExecuteFn());
    // SsrComposite：场景 + 反射合成到输出目标（Imported 写 = 图根）
    graph.AddPass("SsrComposite",
        [&](RgPass& pass) {
            pass.Read(handles.ssrBlur, ZrImageLayout::ShaderReadOnlyOptimal,
                ZrAccessBits::ShaderRead, ZrPipelineStageBits::FragmentShader);
            pass.Read(handles.sceneColor, ZrImageLayout::ShaderReadOnlyOptimal,
                ZrAccessBits::ShaderRead, ZrPipelineStageBits::FragmentShader);
            pass.Write(handles.back, ZrImageLayout::ColorAttachmentOptimal,
                ZrAccessBits::ColorAttachmentWrite, ZrPipelineStageBits::ColorAttachmentOutput);
        },
        compositeExec != nullptr ? compositeExec : RgExecuteFn());
}
