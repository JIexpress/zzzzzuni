// =============================================================================
// bilinear_upscaler.cpp —— 自写 Bilinear 基线后端（TASK-B-R6-01）
//
// viewport 缩放 blit：全屏三角（SV_VertexID，零顶点缓冲）+ 线性采样输入颜色，
// 输出呈现分辨率。D3D12 原生自包含（根签名/PSO/CB/SRV/RTV 堆全在内部创建），
// 不依赖 zrhi——超分基线可独立于渲染管线验证。
//
// 资源状态契约（调用方负责屏障）：
//   inputColor 进入 Evaluate 时须处 PIXEL_SHADER_RESOURCE；
//   output     进入 Evaluate 时须处 RENDER_TARGET。
//
// 着色器：Native/shaders/upsample_bilinear.hlsl，Initialize 时经 D3DCompile
// （SM5.0，与 zrhi 窗口程序同口径）——源码由调用方经 UpscalerInitDesc 读入。
// =============================================================================
#include "zupscaler/zupscaler.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <cstdio>
#include <cstring>
#include <new>

#include <d3d12.h>
#include <d3dcompiler.h>
#include <dxgi.h>

namespace zr {
namespace {

/// CBufBilinear（16B）——upsample_bilinear.hlsl 镜像
struct BilinearConstants {
    float srcTexel[4]; // x=1/srcW y=1/srcH（unused 保留对齐）
};

/// Bilinear 基线后端（每次 Initialize 复位全部 D3D12 资源；Shutdown 幂等）
class BilinearUpscaler final : public IUpscaler {
public:
    ~BilinearUpscaler() override { Shutdown(); }

    UpscalerBackend Backend() const override { return UpscalerBackend::Bilinear; }

    const char* BackendVersion() const override
    {
        return "builtin bilinear (viewport blit, SM5.0)";
    }

    bool Initialize(const UpscalerInitDesc& desc) override
    {
        // 幂等复位：无条件走一遍 Shutdown（TASK-H-02 D4）。不能只在 m_initialized
        // 为真时清理——上一次 Initialize 中途失败会留下已创建的根签名/PSO，而
        // m_initialized 仍为 false，直接跳过清理会让下方赋值覆盖指针造成泄漏。
        Shutdown();
        if (desc.nativeDevice == nullptr || desc.inputWidth == 0u || desc.inputHeight == 0u
            || desc.outputWidth == 0u || desc.outputHeight == 0u || desc.bilinearHlsl == nullptr
            || desc.bilinearHlslSize == 0u) {
            std::printf("  bilinear: Initialize FAILED (invalid desc)\n");
            return false;
        }
        auto* device = static_cast<ID3D12Device*>(desc.nativeDevice);
        m_device = device;
        m_inputWidth = desc.inputWidth;
        m_inputHeight = desc.inputHeight;
        m_outputWidth = desc.outputWidth;
        m_outputHeight = desc.outputHeight;
        m_rtvFormat = static_cast<DXGI_FORMAT>(desc.outputDxgiFormat);

        // ---- 着色器编译（SM5.0 DXBC，失败明确报错）------------------------------
        ID3DBlob* vsBlob = nullptr;
        ID3DBlob* psBlob = nullptr;
        ID3DBlob* errBlob = nullptr;
        HRESULT hrVs = D3DCompile(desc.bilinearHlsl, desc.bilinearHlslSize, "upsample_bilinear",
            nullptr, nullptr, "VsMain", "vs_5_0", 0u, 0u, &vsBlob, &errBlob);
        if (FAILED(hrVs)) {
            PrintCompileError("VsMain", errBlob);
            ReleaseBlobs(vsBlob, psBlob, errBlob, nullptr);
            Shutdown(); // 失败回滚（TASK-H-02 D4）
            return false;
        }
        if (errBlob != nullptr) { // 成功时可能携带告警 blob（亦可能为 null）
            errBlob->Release();
            errBlob = nullptr;
        }
        HRESULT hrPs = D3DCompile(desc.bilinearHlsl, desc.bilinearHlslSize, "upsample_bilinear",
            nullptr, nullptr, "PsMain", "ps_5_0", 0u, 0u, &psBlob, &errBlob);
        if (FAILED(hrPs)) {
            PrintCompileError("PsMain", errBlob);
            ReleaseBlobs(vsBlob, psBlob, errBlob, nullptr);
            Shutdown(); // 失败回滚（TASK-H-02 D4）
            return false;
        }
        if (errBlob != nullptr) {
            errBlob->Release();
            errBlob = nullptr;
        }

        // ---- 根签名：b0 根 CBV + t0 描述符表 + s0 静态线性采样器 -----------------
        D3D12_ROOT_PARAMETER params[2] = {};
        params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
        params[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
        params[0].Descriptor.ShaderRegister = 0u;
        params[0].Descriptor.RegisterSpace = 0u;
        D3D12_DESCRIPTOR_RANGE srvRange {};
        srvRange.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        srvRange.NumDescriptors = 1u;
        srvRange.BaseShaderRegister = 0u;
        srvRange.RegisterSpace = 0u;
        srvRange.OffsetInDescriptorsFromTableStart = D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND;
        params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        params[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
        params[1].DescriptorTable.NumDescriptorRanges = 1u;
        params[1].DescriptorTable.pDescriptorRanges = &srvRange;

        D3D12_STATIC_SAMPLER_DESC sampler {};
        sampler.Filter = D3D12_FILTER_MIN_MAG_MIP_LINEAR; // 双线性：本后端的全部内容
        sampler.AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        sampler.AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        sampler.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        sampler.MipLODBias = 0.0f;
        sampler.MaxAnisotropy = 1u;
        sampler.ComparisonFunc = D3D12_COMPARISON_FUNC_NEVER;
        sampler.BorderColor = D3D12_STATIC_BORDER_COLOR_OPAQUE_BLACK;
        sampler.MinLOD = 0.0f;
        sampler.MaxLOD = D3D12_FLOAT32_MAX;
        sampler.ShaderRegister = 0u;
        sampler.RegisterSpace = 0u;
        sampler.ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;

        D3D12_ROOT_SIGNATURE_DESC rsDesc {};
        rsDesc.NumParameters = 2u;
        rsDesc.pParameters = params;
        rsDesc.NumStaticSamplers = 1u;
        rsDesc.pStaticSamplers = &sampler;
        rsDesc.Flags = D3D12_ROOT_SIGNATURE_FLAG_NONE;
        ID3DBlob* rsBlob = nullptr;
        if (FAILED(D3D12SerializeRootSignature(&rsDesc, D3D_ROOT_SIGNATURE_VERSION_1, &rsBlob,
                &errBlob))) {
            PrintCompileError("RootSignature", errBlob);
            ReleaseBlobs(vsBlob, psBlob, rsBlob, errBlob);
            Shutdown(); // 失败回滚（TASK-H-02 D4）
            return false;
        }
        const HRESULT hrRs = device->CreateRootSignature(0u, rsBlob->GetBufferPointer(),
            rsBlob->GetBufferSize(), IID_PPV_ARGS(&m_rootSignature));
        rsBlob->Release();
        ReleaseBlobs(nullptr, nullptr, nullptr, errBlob);
        if (FAILED(hrRs)) {
            ReleaseBlobs(vsBlob, psBlob, nullptr, nullptr);
            std::printf("  bilinear: CreateRootSignature FAILED (hr=0x%08lX)\n",
                static_cast<unsigned long>(hrRs));
            Shutdown(); // 失败回滚（TASK-H-02 D4）
            return false;
        }

        // ---- PSO（无深度、不透明、无输入布局——SV_VertexID 生成顶点）--------------
        D3D12_GRAPHICS_PIPELINE_STATE_DESC psoDesc {};
        psoDesc.InputLayout = { nullptr, 0u };
        psoDesc.VS = { vsBlob->GetBufferPointer(), vsBlob->GetBufferSize() };
        psoDesc.PS = { psBlob->GetBufferPointer(), psBlob->GetBufferSize() };
        psoDesc.RasterizerState = DefaultRasterizer();
        psoDesc.BlendState = DefaultBlend();
        psoDesc.DepthStencilState = DefaultDepth();
        psoDesc.SampleMask = 0xFFFFFFFFu;
        psoDesc.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        psoDesc.NumRenderTargets = 1u;
        psoDesc.RTVFormats[0] = m_rtvFormat;
        psoDesc.SampleDesc.Count = 1u;
        psoDesc.pRootSignature = m_rootSignature;
        // PSO 创建期间 VS/PS 字节码指针必须存活（CreateGraphicsPipelineState 即时
        // 消费；成功后 blob 方可释放——曾经"驱动偶发 E_FAIL"实为提前释放的
        // use-after-free，修复后无需重试）
        HRESULT hrPso = device->CreateGraphicsPipelineState(&psoDesc, IID_PPV_ARGS(&m_pso));
        ReleaseBlobs(vsBlob, psBlob, nullptr, nullptr);
        if (FAILED(hrPso)) {
            std::printf("  bilinear: CreateGraphicsPipelineState FAILED (hr=0x%08lX)\n",
                static_cast<unsigned long>(hrPso));
            Shutdown(); // 失败回滚：已建的根签名必须释放（TASK-H-02 D4 泄漏点）
            return false;
        }

        // ---- 常量缓冲（UPLOAD，持久映射）----------------------------------------
        D3D12_HEAP_PROPERTIES heap {};
        heap.Type = D3D12_HEAP_TYPE_UPLOAD;
        D3D12_RESOURCE_DESC cbDesc {};
        cbDesc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        cbDesc.Width = 256u;
        cbDesc.Height = 1u;
        cbDesc.DepthOrArraySize = 1u;
        cbDesc.MipLevels = 1u;
        cbDesc.SampleDesc.Count = 1u;
        cbDesc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        if (FAILED(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &cbDesc,
                D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&m_cb)))) {
            std::printf("  bilinear: CB CreateCommittedResource FAILED\n");
            Shutdown(); // 失败回滚（TASK-H-02 D4）
            return false;
        }
        if (FAILED(m_cb->Map(0u, nullptr, &m_cbMapped)) || m_cbMapped == nullptr) {
            std::printf("  bilinear: CB Map FAILED\n");
            Shutdown(); // 失败回滚（TASK-H-02 D4）
            return false;
        }

        // ---- SRV 堆（shader visible，1 槽）+ RTV 堆（非 shader visible，1 槽）-----
        D3D12_DESCRIPTOR_HEAP_DESC srvHeapDesc {};
        srvHeapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        srvHeapDesc.NumDescriptors = 1u;
        srvHeapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        if (FAILED(device->CreateDescriptorHeap(&srvHeapDesc, IID_PPV_ARGS(&m_srvHeap)))) {
            std::printf("  bilinear: SRV heap create FAILED\n");
            Shutdown(); // 失败回滚（TASK-H-02 D4）
            return false;
        }
        D3D12_DESCRIPTOR_HEAP_DESC rtvHeapDesc {};
        rtvHeapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        rtvHeapDesc.NumDescriptors = 1u;
        rtvHeapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_NONE;
        if (FAILED(device->CreateDescriptorHeap(&rtvHeapDesc, IID_PPV_ARGS(&m_rtvHeap)))) {
            std::printf("  bilinear: RTV heap create FAILED\n");
            Shutdown(); // 失败回滚（TASK-H-02 D4）
            return false;
        }
        m_rtvHeapCpu = m_rtvHeap->GetCPUDescriptorHandleForHeapStart();

        m_initialized = true;
        std::printf("  bilinear: Initialize OK (%ux%u -> %ux%u, rtv fmt=%d)\n", m_inputWidth,
            m_inputHeight, m_outputWidth, m_outputHeight, static_cast<int>(m_rtvFormat));
        return true;
    }

    bool Resize(uint32_t inputWidth, uint32_t inputHeight, uint32_t outputWidth,
        uint32_t outputHeight) override
    {
        if (!m_initialized || inputWidth == 0u || inputHeight == 0u || outputWidth == 0u
            || outputHeight == 0u) {
            return false;
        }
        m_inputWidth = inputWidth;
        m_inputHeight = inputHeight;
        m_outputWidth = outputWidth;
        m_outputHeight = outputHeight;
        return true;
    }

    bool Evaluate(const UpscalerFrameArgs& args) override
    {
        if (!m_initialized || args.nativeCmdList == nullptr || args.inputColor == nullptr
            || args.output == nullptr) {
            return false;
        }
        auto* cmd = static_cast<ID3D12GraphicsCommandList*>(args.nativeCmdList);
        auto* inputColor = static_cast<ID3D12Resource*>(args.inputColor);
        auto* output = static_cast<ID3D12Resource*>(args.output);
        (void)args.inputDepth;
        (void)args.inputMotionVectors;
        (void)args.jitterX;
        (void)args.jitterY;
        (void)args.sharpness;

        // CB：源 texel 尺寸（线性采样的 uv 映射无需目标尺寸）
        BilinearConstants cb {};
        cb.srcTexel[0] = 1.0f / static_cast<float>(m_inputWidth);
        cb.srcTexel[1] = 1.0f / static_cast<float>(m_inputHeight);
        std::memcpy(m_cbMapped, &cb, sizeof(cb));

        // SRV（源颜色）/ RTV（输出）——每帧写入描述符槽（描述符堆复用）
        m_device->CreateShaderResourceView(inputColor, nullptr, m_srvHeap->GetCPUDescriptorHandleForHeapStart());
        m_device->CreateRenderTargetView(output, nullptr, m_rtvHeap->GetCPUDescriptorHandleForHeapStart());

        const D3D12_VIEWPORT viewport
            = { 0.0f, 0.0f, static_cast<float>(m_outputWidth), static_cast<float>(m_outputHeight),
                  0.0f, 1.0f };
        const D3D12_RECT scissor = { 0, 0,
            static_cast<LONG>(m_outputWidth), static_cast<LONG>(m_outputHeight) };
        ID3D12DescriptorHeap* heaps[1] = { m_srvHeap };

        cmd->SetDescriptorHeaps(1u, heaps);
        cmd->SetGraphicsRootSignature(m_rootSignature);
        cmd->SetPipelineState(m_pso);
        cmd->SetGraphicsRootConstantBufferView(0u, m_cb->GetGPUVirtualAddress());
        cmd->SetGraphicsRootDescriptorTable(1u, m_srvHeap->GetGPUDescriptorHandleForHeapStart());
        cmd->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        cmd->RSSetViewports(1u, &viewport);
        cmd->RSSetScissorRects(1u, &scissor);
        cmd->OMSetRenderTargets(1u, &m_rtvHeapCpu, FALSE, nullptr);
        cmd->DrawInstanced(3u, 1u, 0u, 0u);
        return true;
    }

    void Shutdown() override
    {
        m_initialized = false;
        if (m_cb != nullptr) {
            // 持久映射必须先解除再 Release（TASK-H-02 D4）：映射未解除的资源
            // 在 Release 后仍被运行时持有，属于确定性泄漏
            if (m_cbMapped != nullptr) {
                m_cb->Unmap(0u, nullptr);
            }
            m_cbMapped = nullptr;
            m_cb->Release();
            m_cb = nullptr;
        }
        if (m_srvHeap != nullptr) {
            m_srvHeap->Release();
            m_srvHeap = nullptr;
        }
        if (m_rtvHeap != nullptr) {
            m_rtvHeap->Release();
            m_rtvHeap = nullptr;
        }
        if (m_pso != nullptr) {
            m_pso->Release();
            m_pso = nullptr;
        }
        if (m_rootSignature != nullptr) {
            m_rootSignature->Release();
            m_rootSignature = nullptr;
        }
        m_device = nullptr;
    }

    bool IsInitialized() const override { return m_initialized; }

private:
    static void PrintCompileError(const char* what, ID3DBlob* errBlob)
    {
        if (errBlob != nullptr) {
            std::printf("  bilinear: %s compile FAILED: %s\n", what,
                static_cast<const char*>(errBlob->GetBufferPointer()));
        } else {
            std::printf("  bilinear: %s compile FAILED (no error blob)\n", what);
        }
    }
    static void ReleaseBlobs(ID3DBlob* a, ID3DBlob* b, ID3DBlob* c, ID3DBlob* d)
    {
        if (a != nullptr) a->Release();
        if (b != nullptr) b->Release();
        if (c != nullptr) c->Release();
        if (d != nullptr) d->Release();
    }
    static D3D12_RASTERIZER_DESC DefaultRasterizer()
    {
        D3D12_RASTERIZER_DESC r {};
        r.FillMode = D3D12_FILL_MODE_SOLID;
        r.CullMode = D3D12_CULL_MODE_NONE;
        r.FrontCounterClockwise = FALSE;
        r.DepthBias = D3D12_DEFAULT_DEPTH_BIAS;
        r.DepthBiasClamp = D3D12_DEFAULT_DEPTH_BIAS_CLAMP;
        r.SlopeScaledDepthBias = D3D12_DEFAULT_SLOPE_SCALED_DEPTH_BIAS;
        r.DepthClipEnable = TRUE;
        r.MultisampleEnable = FALSE;
        r.AntialiasedLineEnable = FALSE;
        r.ForcedSampleCount = 0u;
        r.ConservativeRaster = D3D12_CONSERVATIVE_RASTERIZATION_MODE_OFF;
        return r;
    }
    static D3D12_BLEND_DESC DefaultBlend()
    {
        D3D12_BLEND_DESC b {};
        b.AlphaToCoverageEnable = FALSE;
        b.IndependentBlendEnable = FALSE;
        for (UINT i = 0; i < D3D12_SIMULTANEOUS_RENDER_TARGET_COUNT; ++i) {
            D3D12_RENDER_TARGET_BLEND_DESC& rt = b.RenderTarget[i];
            rt.BlendEnable = FALSE;
            rt.LogicOpEnable = FALSE;
            rt.SrcBlend = D3D12_BLEND_ONE;
            rt.DestBlend = D3D12_BLEND_ZERO;
            rt.BlendOp = D3D12_BLEND_OP_ADD;
            rt.SrcBlendAlpha = D3D12_BLEND_ONE;
            rt.DestBlendAlpha = D3D12_BLEND_ZERO;
            rt.BlendOpAlpha = D3D12_BLEND_OP_ADD;
            rt.LogicOp = D3D12_LOGIC_OP_NOOP;
            rt.RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
        }
        return b;
    }
    static D3D12_DEPTH_STENCIL_DESC DefaultDepth()
    {
        D3D12_DEPTH_STENCIL_DESC d {};
        d.DepthEnable = FALSE;
        d.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ZERO;
        d.DepthFunc = D3D12_COMPARISON_FUNC_ALWAYS;
        d.StencilEnable = FALSE;
        return d;
    }

    bool m_initialized = false;
    uint32_t m_inputWidth = 0u;
    uint32_t m_inputHeight = 0u;
    uint32_t m_outputWidth = 0u;
    uint32_t m_outputHeight = 0u;
    DXGI_FORMAT m_rtvFormat = DXGI_FORMAT_R8G8B8A8_UNORM;
    ID3D12Device* m_device = nullptr;
    ID3D12RootSignature* m_rootSignature = nullptr;
    ID3D12PipelineState* m_pso = nullptr;
    ID3D12Resource* m_cb = nullptr;
    void* m_cbMapped = nullptr;
    ID3D12DescriptorHeap* m_srvHeap = nullptr;
    ID3D12DescriptorHeap* m_rtvHeap = nullptr;
    D3D12_CPU_DESCRIPTOR_HANDLE m_rtvHeapCpu {};
};

} // namespace

IUpscaler* CreateBilinearUpscaler()
{
    return new (std::nothrow) BilinearUpscaler();
}

} // namespace zr
