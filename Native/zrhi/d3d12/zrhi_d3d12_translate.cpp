// zrhi_d3d12_translate.cpp —— 三个纯函数映射表实现（TASK-B-R2-02）
//
// 语义对应（Vulkan → D3D12）：
//   - ZrFormat ≈ VkFormat：逐值 switch 到等义 DXGI_FORMAT（通道序/位宽一致）
//   - ZrImageLayout ≈ VkImageLayout：逐值 switch 到 D3D12_RESOURCE_STATES。
//     两套枚举序完全不同，本文件是唯一转译点（严禁隐式整型转换）
//   - PresentMode 映射表（→ IDXGISwapChain::Present 参数）：
//       Fifo      → SyncInterval 1（垂直同步排队，必支持；≈ VK_PRESENT_MODE_FIFO_KHR）
//       Mailbox   → SyncInterval 0（FLIP 模型下即时替换待呈现缓冲，晚撕裂；
//                   ≈ VK_PRESENT_MODE_MAILBOX_KHR）
//       Immediate → SyncInterval 0 + DXGI_PRESENT_ALLOW_TEARING（立即呈现可能
//                   撕裂；≈ VK_PRESENT_MODE_IMMEDIATE_KHR。撕裂需交换链创建时带
//                   DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING 且适配器支持，后端做能力
//                   检测，不支持时降级为普通 SyncInterval 0）
#include "zrhi/d3d12/zrhi_d3d12_translate.h"

#include <cassert>

DXGI_FORMAT ZrD3D12_Format(ZrFormat format) noexcept
{
    switch (format) {
    case ZrFormat::RGBA8Unorm: return DXGI_FORMAT_R8G8B8A8_UNORM;
    case ZrFormat::BGRA8Unorm: return DXGI_FORMAT_B8G8R8A8_UNORM;
    case ZrFormat::RGBA8sRGB: return DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
    case ZrFormat::R8Unorm: return DXGI_FORMAT_R8_UNORM;
    case ZrFormat::R16Float: return DXGI_FORMAT_R16_FLOAT;
    case ZrFormat::RG16Float: return DXGI_FORMAT_R16G16_FLOAT;
    case ZrFormat::R11G11B10UFloat: return DXGI_FORMAT_R11G11B10_FLOAT;
    case ZrFormat::RGBA16Float: return DXGI_FORMAT_R16G16B16A16_FLOAT;
    case ZrFormat::R32Float: return DXGI_FORMAT_R32_FLOAT;
    case ZrFormat::RGBA32Float: return DXGI_FORMAT_R32G32B32A32_FLOAT;
    case ZrFormat::R32Uint: return DXGI_FORMAT_R32_UINT;
    case ZrFormat::D32Float: return DXGI_FORMAT_D32_FLOAT;
    case ZrFormat::D24UnormS8: return DXGI_FORMAT_D24_UNORM_S8_UINT;
    case ZrFormat::D16Unorm: return DXGI_FORMAT_D16_UNORM;
    default:
        assert(false && "ZrD3D12_Format: 非法 ZrFormat 枚举值");
        return DXGI_FORMAT_UNKNOWN;
    }
}

D3D12_RESOURCE_STATES ZrD3D12_ResourceStates(ZrImageLayout layout) noexcept
{
    switch (layout) {
    // Undefined = 内容不保证 → COMMON（D3D12 提交资源的初始态，Vulkan
    // VK_IMAGE_LAYOUT_UNDEFINED 的等价起点；首次屏障再转入实际使用态）
    case ZrImageLayout::Undefined: return D3D12_RESOURCE_STATE_COMMON;
    // General = 万能读写（Vulkan 中 UAV 读写的典型布局）→ UNORDERED_ACCESS
    //（采样+读写并发的完全体 D3D12 需状态组合，基座取 UAV 主用例）
    case ZrImageLayout::General: return D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
    // ColorAttachmentOptimal → RENDER_TARGET
    case ZrImageLayout::ColorAttachmentOptimal: return D3D12_RESOURCE_STATE_RENDER_TARGET;
    // DepthStencilAttachmentOptimal → DEPTH_WRITE（读写）
    case ZrImageLayout::DepthStencilAttachmentOptimal: return D3D12_RESOURCE_STATE_DEPTH_WRITE;
    // DepthStencilReadOnlyOptimal → DEPTH_READ（只读）
    case ZrImageLayout::DepthStencilReadOnlyOptimal: return D3D12_RESOURCE_STATE_DEPTH_READ;
    // ShaderReadOnlyOptimal → 两级着色器读（Vulkan 单一采样布局，D3D12 拆两态）
    case ZrImageLayout::ShaderReadOnlyOptimal:
        return D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE
            | D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
    // TransferSrcOptimal → COPY_SOURCE
    case ZrImageLayout::TransferSrcOptimal: return D3D12_RESOURCE_STATE_COPY_SOURCE;
    // TransferDstOptimal → COPY_DEST
    case ZrImageLayout::TransferDstOptimal: return D3D12_RESOURCE_STATE_COPY_DEST;
    // Present → PRESENT（≈ VK_IMAGE_LAYOUT_PRESENT_SRC_KHR）。注意：D3D12 SDK 中
    // D3D12_RESOURCE_STATE_PRESENT == D3D12_RESOURCE_STATE_COMMON == 0（呈现态即
    // 公共态，d3d12.h 显式如此定义），故与 Undefined 的映射数值重合——这是 D3D12
    // 自身设计而非误配；屏障转 PRESENT 语义上仍表达"交给呈现引擎消费"
    case ZrImageLayout::Present: return D3D12_RESOURCE_STATE_PRESENT;
    default:
        assert(false && "ZrD3D12_ResourceStates: 非法 ZrImageLayout 枚举值");
        return D3D12_RESOURCE_STATE_COMMON;
    }
}

// TASK-B-R3-02：split barrier 半边映射（Zr split 语义 → D3D12 原生 flag）。
// BEGIN_ONLY 只发起不等待（GPU 提前开始缓存刷新），END_ONLY 在读侧边界等待并
// 补可见性——两者必须成对出现且同一屏障内容。
D3D12_RESOURCE_BARRIER_FLAGS ZrD3D12_SplitBarrierFlag(bool beginOnly) noexcept
{
    return beginOnly ? D3D12_RESOURCE_BARRIER_FLAG_BEGIN_ONLY
                     : D3D12_RESOURCE_BARRIER_FLAG_END_ONLY;
}

ZrD3D12PresentParams ZrD3D12_PresentParams(ZrPresentMode mode) noexcept
{
    switch (mode) {
    case ZrPresentMode::Fifo: return {1u, 0u}; // 垂直同步
    case ZrPresentMode::Mailbox: return {0u, 0u}; // SyncInterval 0 即时替换
    case ZrPresentMode::Immediate:
        return {0u, DXGI_PRESENT_ALLOW_TEARING}; // 撕裂需能力检测，后端降级处理
    default:
        assert(false && "ZrD3D12_PresentParams: 非法 ZrPresentMode 枚举值");
        return {1u, 0u};
    }
}

// =============================================================================
// TASK-H-02 D3：Present 结果分类（纯逻辑；分类规则见头文件注释）
// =============================================================================
void ZrD3D12PresentStatus::Note(long hresult) noexcept
{
    // 成功码含 DXGI_STATUS_OCCLUDED（窗口被遮挡/最小化）：属正常状态，不计失败
    if (hresult >= 0) {
        return;
    }
    failureCount += 1u;
    lastFailureHresult = hresult;
    if (hresult == DXGI_ERROR_DEVICE_REMOVED || hresult == DXGI_ERROR_DEVICE_RESET
        || hresult == DXGI_ERROR_DRIVER_INTERNAL_ERROR) {
        deviceRemoved = true; // 粘滞：设备一旦失效，后续帧状态不可信
    }
}

// =============================================================================
// TASK-B-R2-03：采样器映射
// =============================================================================
D3D12_FILTER ZrD3D12_Filter(ZrFilter mag, ZrFilter min, ZrFilter mip) noexcept
{
    // Vulkan 三字段（magFilter/minFilter/mipmapMode）独立 → D3D12 单一打包枚举：
    // 显式双 switch 展开 8 种组合（枚举序无规律，严禁按位拼装；D3D12 枚举名
    // 命名序为 MIN_MAG_MIP——MIN=缩小时即 minFilter、MAG=放大时即 magFilter）
    if (mag == ZrFilter::Nearest) {
        if (min == ZrFilter::Nearest) {
            switch (mip) {
            case ZrFilter::Nearest: return D3D12_FILTER_MIN_MAG_MIP_POINT;
            case ZrFilter::Linear: return D3D12_FILTER_MIN_MAG_POINT_MIP_LINEAR;
            default: break;
            }
        } else {
            switch (mip) {
            case ZrFilter::Nearest: return D3D12_FILTER_MIN_LINEAR_MAG_MIP_POINT;
            case ZrFilter::Linear: return D3D12_FILTER_MIN_LINEAR_MAG_POINT_MIP_LINEAR;
            default: break;
            }
        }
    } else {
        if (min == ZrFilter::Nearest) {
            switch (mip) {
            case ZrFilter::Nearest: return D3D12_FILTER_MIN_POINT_MAG_LINEAR_MIP_POINT;
            case ZrFilter::Linear: return D3D12_FILTER_MIN_POINT_MAG_MIP_LINEAR;
            default: break;
            }
        } else {
            switch (mip) {
            case ZrFilter::Nearest: return D3D12_FILTER_MIN_MAG_LINEAR_MIP_POINT;
            case ZrFilter::Linear: return D3D12_FILTER_MIN_MAG_MIP_LINEAR;
            default: break;
            }
        }
    }
    assert(false && "ZrD3D12_Filter: 非法 ZrFilter 枚举值");
    return D3D12_FILTER_MIN_MAG_MIP_POINT;
}

D3D12_TEXTURE_ADDRESS_MODE ZrD3D12_AddressMode(ZrSamplerAddressMode mode) noexcept
{
    switch (mode) {
    case ZrSamplerAddressMode::Repeat: return D3D12_TEXTURE_ADDRESS_MODE_WRAP;
    case ZrSamplerAddressMode::MirroredRepeat: return D3D12_TEXTURE_ADDRESS_MODE_MIRROR;
    case ZrSamplerAddressMode::ClampToEdge: return D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
    case ZrSamplerAddressMode::ClampToBorder: return D3D12_TEXTURE_ADDRESS_MODE_BORDER;
    // 镜像+钳制：D3D12 的 MIRROR_ONCE 即 Vulkan MIRROR_CLAMP_TO_EDGE 语义
    case ZrSamplerAddressMode::MirrorClampToEdge:
        return D3D12_TEXTURE_ADDRESS_MODE_MIRROR_ONCE;
    default:
        assert(false && "ZrD3D12_AddressMode: 非法 ZrSamplerAddressMode 枚举值");
        return D3D12_TEXTURE_ADDRESS_MODE_WRAP;
    }
}

// =============================================================================
// TASK-B-R4-03：图形 PSO 映射实现
// =============================================================================
D3D12_CULL_MODE ZrD3D12_CullMode(ZrCullMode mode) noexcept
{
    switch (mode) {
    case ZrCullMode::None: return D3D12_CULL_MODE_NONE;
    case ZrCullMode::Back: return D3D12_CULL_MODE_BACK;
    case ZrCullMode::Front: return D3D12_CULL_MODE_FRONT;
    default:
        assert(false && "ZrD3D12_CullMode: 非法 ZrCullMode 枚举值");
        return D3D12_CULL_MODE_NONE;
    }
}

ZrD3D12BlendParams ZrD3D12_Blend(ZrBlendMode mode) noexcept
{
    switch (mode) {
    case ZrBlendMode::Opaque:
        // 不透明直通：final = src（ONE/ZERO；→ VK_BLEND_FACTOR_ONE/ZERO 组合）
        return { D3D12_BLEND_ONE, D3D12_BLEND_ZERO, D3D12_BLEND_ONE, D3D12_BLEND_ZERO };
    default:
        assert(false && "ZrD3D12_Blend: 非法 ZrBlendMode 枚举值（v1 仅 Opaque）");
        return { D3D12_BLEND_ONE, D3D12_BLEND_ZERO, D3D12_BLEND_ONE, D3D12_BLEND_ZERO };
    }
}

const D3D12_INPUT_ELEMENT_DESC* ZrD3D12_GraphicsInputLayout(uint32_t* outElementCount) noexcept
{
    // v1 固定语义版顶点布局：Pos3f+Normal3f+Uv2f 紧凑交错（stride 32B）。
    // 升级路径：开放 ZrGraphicsPsoDesc 输入布局字段时，本表降级为后端默认预设
    static const D3D12_INPUT_ELEMENT_DESC kLayout[] = {
        { "POSITION", 0u, DXGI_FORMAT_R32G32B32_FLOAT, 0u, 0u,
            D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0u },
        { "NORMAL", 0u, DXGI_FORMAT_R32G32B32_FLOAT, 0u, 12u,
            D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0u },
        { "TEXCOORD", 0u, DXGI_FORMAT_R32G32_FLOAT, 0u, 24u,
            D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0u },
    };
    static_assert(sizeof(kLayout) / sizeof(kLayout[0]) == 3u, "v1 固定顶点布局为 3 元素");
    if (outElementCount != nullptr) {
        *outElementCount = 3u;
    }
    return kLayout;
}

D3D12_GRAPHICS_PIPELINE_STATE_DESC ZrD3D12_GraphicsPsoDesc(const ZrGraphicsPsoDesc& desc,
    ID3D12RootSignature* rootSignature, D3D12_SHADER_BYTECODE vs,
    D3D12_SHADER_BYTECODE ps) noexcept
{
    // 调用契约：desc 须先过 ZrValidateGraphicsPsoDesc（后端 CreatePipelineState
    // 入口已校验；本函数不做重复校验）
    D3D12_GRAPHICS_PIPELINE_STATE_DESC out {};
    out.pRootSignature = rootSignature; // D3D12 PSO 的根签名内嵌于 desc（非独立参数）
    out.VS = vs;
    out.PS = ps;

    // 输入布局：v1 固定（Pos3f+Normal3f+Uv2f，stride 32B）
    uint32_t elementCount = 0u;
    out.InputLayout.pInputElementDescs = ZrD3D12_GraphicsInputLayout(&elementCount);
    out.InputLayout.NumElements = elementCount;

    // 光栅化：实体填充 + desc.cullMode；正面判定默认 CW（D3D12 LH 约定）
    out.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID;
    out.RasterizerState.CullMode = ZrD3D12_CullMode(desc.cullMode);
    out.RasterizerState.FrontCounterClockwise = FALSE;
    out.RasterizerState.DepthBias = D3D12_DEFAULT_DEPTH_BIAS;
    out.RasterizerState.DepthBiasClamp = D3D12_DEFAULT_DEPTH_BIAS_CLAMP;
    out.RasterizerState.SlopeScaledDepthBias = D3D12_DEFAULT_SLOPE_SCALED_DEPTH_BIAS;
    out.RasterizerState.DepthClipEnable = TRUE;
    out.RasterizerState.MultisampleEnable = FALSE;
    out.RasterizerState.AntialiasedLineEnable = FALSE;
    out.RasterizerState.ForcedSampleCount = 0u;
    out.RasterizerState.ConservativeRaster = D3D12_CONSERVATIVE_RASTERIZATION_MODE_OFF;

    // 混合：v1 全 Opaque（RenderTarget0；BlendEnable=false 因子直通）
    const ZrD3D12BlendParams blend = ZrD3D12_Blend(desc.blend);
    out.BlendState.AlphaToCoverageEnable = FALSE;
    out.BlendState.IndependentBlendEnable = FALSE;
    out.BlendState.RenderTarget[0].BlendEnable = FALSE;
    out.BlendState.RenderTarget[0].LogicOpEnable = FALSE;
    out.BlendState.RenderTarget[0].SrcBlend = blend.src;
    out.BlendState.RenderTarget[0].DestBlend = blend.dest;
    out.BlendState.RenderTarget[0].BlendOp = D3D12_BLEND_OP_ADD;
    out.BlendState.RenderTarget[0].SrcBlendAlpha = blend.srcAlpha;
    out.BlendState.RenderTarget[0].DestBlendAlpha = blend.destAlpha;
    out.BlendState.RenderTarget[0].BlendOpAlpha = D3D12_BLEND_OP_ADD;
    out.BlendState.RenderTarget[0].LogicOp = D3D12_LOGIC_OP_NOOP;
    out.BlendState.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;

    // 深度状态：LESS 比较（near 更小者通过）；写掩码按 desc.depthWrite。
    // dsvFormat==Count（无深度附件）时 DepthEnable=false（校验层已保证此时
    // depthTest/depthWrite 均为 false）
    out.DepthStencilState.DepthEnable
        = (desc.dsvFormat != ZrFormat::Count && desc.depthTest) ? TRUE : FALSE;
    out.DepthStencilState.DepthWriteMask
        = desc.depthWrite ? D3D12_DEPTH_WRITE_MASK_ALL : D3D12_DEPTH_WRITE_MASK_ZERO;
    out.DepthStencilState.DepthFunc = D3D12_COMPARISON_FUNC_LESS;
    out.DepthStencilState.StencilEnable = FALSE;
    out.DepthStencilState.StencilReadMask = D3D12_DEFAULT_STENCIL_READ_MASK;
    out.DepthStencilState.StencilWriteMask = D3D12_DEFAULT_STENCIL_WRITE_MASK;

    // 目标格式：RTV 单目标（depth-only 管线无颜色目标——NumRenderTargets=0，
    // TASK-B-R5-02）；DSV 按有无深度附件映射（Count → DXGI_FORMAT_UNKNOWN）
    if (desc.depthOnly) {
        out.NumRenderTargets = 0u;
        out.RTVFormats[0] = DXGI_FORMAT_UNKNOWN;
    } else {
        out.NumRenderTargets = 1u;
        out.RTVFormats[0] = ZrD3D12_Format(desc.rtvFormat);
    }
    out.DSVFormat = desc.dsvFormat != ZrFormat::Count
        ? ZrD3D12_Format(desc.dsvFormat)
        : DXGI_FORMAT_UNKNOWN;

    out.SampleMask = 0xFFFFFFFFu;
    out.SampleDesc.Count = 1u;
    out.SampleDesc.Quality = 0u;
    out.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
    out.IBStripCutValue = D3D12_INDEX_BUFFER_STRIP_CUT_VALUE_DISABLED;
    return out;
}
