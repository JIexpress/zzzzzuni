// =============================================================================
// test_zrhi_pso_draw.cpp —— 图形 PSO 与绘制命令序列单测（TASK-B-R4-03）
//
// 接口层（无 GPU）：
//   1. ZrValidateGraphicsPsoDesc 纯逻辑校验：缺 shader 拒绝 / 非法格式拒绝 /
//      深度状态矛盾拒绝 / 合法描述放行
//   2. MockDevice::CreatePipelineState 契约对齐真后端：非法 desc 返回无效句柄
//   3. 命令序列记录：SetPipelineState → SetVertexBuffers → SetIndexBuffer →
//      SetGraphicsConstantBuffer → DrawIndexedInstanced 顺序断言（mock 命令表
//      结构化记录逐字段核对）
//
// doctest main 由 test_smoke.cpp 提供（多编译单元共享）。
// =============================================================================
#include "doctest/doctest.h"

#include "rg_mock_device.h"

namespace {

/// 合法基准描述（vs/ps 由调用方注入；D32Float 深度 + 背面剔除 + 深测深写）
ZrGraphicsPsoDesc MakeValidDesc(ZrShader vs, ZrShader ps)
{
    ZrGraphicsPsoDesc d;
    d.vertexShader = vs;
    d.pixelShader = ps;
    d.rtvFormat = ZrFormat::BGRA8Unorm;
    d.dsvFormat = ZrFormat::D32Float;
    d.blend = ZrBlendMode::Opaque;
    d.depthTest = true;
    d.depthWrite = true;
    d.cullMode = ZrCullMode::Back;
    return d;
}

} // namespace

TEST_CASE("ZrValidateGraphicsPsoDesc：缺 shader 拒绝（vs/ps 各自独立）")
{
    ZrShader validVs = ZrShader::FromRaw(0x01000001u);
    ZrShader validPs = ZrShader::FromRaw(0x01000002u);

    ZrGraphicsPsoDesc missingVs = MakeValidDesc(ZrShader {}, validPs);
    CHECK_FALSE(ZrValidateGraphicsPsoDesc(missingVs));

    ZrGraphicsPsoDesc missingPs = MakeValidDesc(validVs, ZrShader {});
    CHECK_FALSE(ZrValidateGraphicsPsoDesc(missingPs));

    ZrGraphicsPsoDesc missingBoth = MakeValidDesc(ZrShader {}, ZrShader {});
    CHECK_FALSE(ZrValidateGraphicsPsoDesc(missingBoth));

    CHECK(ZrValidateGraphicsPsoDesc(MakeValidDesc(validVs, validPs)));
}

TEST_CASE("ZrValidateGraphicsPsoDesc：格式非法拒绝")
{
    ZrShader vs = ZrShader::FromRaw(0x01000001u);
    ZrShader ps = ZrShader::FromRaw(0x01000002u);

    // rtvFormat 为深度格式（非 isColor）拒绝
    ZrGraphicsPsoDesc depthRtv = MakeValidDesc(vs, ps);
    depthRtv.rtvFormat = ZrFormat::D32Float;
    CHECK_FALSE(ZrValidateGraphicsPsoDesc(depthRtv));

    // rtvFormat 为 Count 哨兵拒绝
    ZrGraphicsPsoDesc sentinelRtv = MakeValidDesc(vs, ps);
    sentinelRtv.rtvFormat = ZrFormat::Count;
    CHECK_FALSE(ZrValidateGraphicsPsoDesc(sentinelRtv));

    // dsvFormat 为颜色格式（无深度分量）拒绝
    ZrGraphicsPsoDesc colorDsv = MakeValidDesc(vs, ps);
    colorDsv.dsvFormat = ZrFormat::RGBA8Unorm;
    CHECK_FALSE(ZrValidateGraphicsPsoDesc(colorDsv));
}

TEST_CASE("ZrValidateGraphicsPsoDesc：深度状态矛盾拒绝")
{
    ZrShader vs = ZrShader::FromRaw(0x01000001u);
    ZrShader ps = ZrShader::FromRaw(0x01000002u);

    // 有深度附件：只写不测矛盾
    ZrGraphicsPsoDesc writeWithoutTest = MakeValidDesc(vs, ps);
    writeWithoutTest.depthTest = false;
    writeWithoutTest.depthWrite = true;
    CHECK_FALSE(ZrValidateGraphicsPsoDesc(writeWithoutTest));

    // 无深度附件（dsvFormat=Count）：开深度测试拒绝
    ZrGraphicsPsoDesc testWithoutDsv = MakeValidDesc(vs, ps);
    testWithoutDsv.dsvFormat = ZrFormat::Count;
    CHECK_FALSE(ZrValidateGraphicsPsoDesc(testWithoutDsv));

    // 无深度附件：开深度写入拒绝
    ZrGraphicsPsoDesc writeWithoutDsv = MakeValidDesc(vs, ps);
    writeWithoutDsv.dsvFormat = ZrFormat::Count;
    writeWithoutDsv.depthWrite = true;
    CHECK_FALSE(ZrValidateGraphicsPsoDesc(writeWithoutDsv));

    // 无深度附件且深度关闭：合法（纯颜色目标）
    ZrGraphicsPsoDesc noDepth = MakeValidDesc(vs, ps);
    noDepth.dsvFormat = ZrFormat::Count;
    noDepth.depthTest = false;
    noDepth.depthWrite = false;
    CHECK(ZrValidateGraphicsPsoDesc(noDepth));
}

TEST_CASE("MockDevice::CreatePipelineState：非法描述返回无效句柄（契约对齐真后端）")
{
    MockDevice device;

    // 缺 shader 的描述经 mock 创建被拒（模拟真后端 CreatePipelineState 校验路径）
    ZrGraphicsPsoDesc missingShaders = MakeValidDesc(ZrShader {}, ZrShader {});
    CHECK_FALSE(device.CreatePipelineState(missingShaders).IsValid());

    // 合法描述获得有效句柄
    ZrShaderDesc vsDesc {};
    vsDesc.stage = ZrShaderStage::Vertex;
    const uint8_t bytecode[4] = { 1u, 2u, 3u, 4u };
    vsDesc.code = bytecode;
    vsDesc.codeSize = sizeof(bytecode);
    ZrShader vs = device.CreateShader(vsDesc);
    CHECK(vs.IsValid());
    ZrShaderDesc psDesc = vsDesc;
    psDesc.stage = ZrShaderStage::Pixel;
    ZrShader ps = device.CreateShader(psDesc);
    CHECK(ps.IsValid());

    ZrPipelineState pso = device.CreatePipelineState(MakeValidDesc(vs, ps));
    CHECK(pso.IsValid());
}

TEST_CASE("命令序列：SetPSO→SetVB→SetIB→SetCB→DrawIndexed 顺序与参数断言")
{
    MockDevice device;
    MockCommandList& cmd = device.commandList;

    // 前置：mock 侧造出有效 PSO/缓冲句柄
    ZrShaderDesc shaderDesc {};
    const uint8_t bytecode[4] = { 1u, 2u, 3u, 4u };
    shaderDesc.code = bytecode;
    shaderDesc.codeSize = sizeof(bytecode);
    ZrShader vs = device.CreateShader(shaderDesc);
    ZrShader ps = device.CreateShader(shaderDesc);
    REQUIRE(vs.IsValid());
    REQUIRE(ps.IsValid());
    ZrPipelineState pso = device.CreatePipelineState(MakeValidDesc(vs, ps));
    REQUIRE(pso.IsValid());

    ZrBuffer vb = device.CreateBuffer(ZrBufferDesc {});
    ZrBuffer ib = device.CreateBuffer(ZrBufferDesc {});
    ZrBuffer cb = device.CreateBuffer(ZrBufferDesc {});
    REQUIRE(vb.IsValid());
    REQUIRE(ib.IsValid());
    REQUIRE(cb.IsValid());
    ZrImageView rtv = device.CreateImageView(ZrImageViewDesc {});
    ZrImageView dsv = device.CreateImageView(ZrImageViewDesc {});

    // ---- 记录一帧 DrawPass 的标准命令流（顺序即调用序）--------------------------
    cmd.Begin();
    cmd.SetRenderTarget(rtv, dsv);
    cmd.SetViewportScissor(640u, 480u);
    cmd.SetPipelineState(pso);
    cmd.SetVertexBuffers(&vb, 1u);
    cmd.SetIndexBuffer(ib);
    cmd.SetGraphicsConstantBuffer(0u, cb);
    cmd.DrawIndexedInstanced(36u, 1u, 0u, 0u, 0u); // cube.zmesh：36 索引
    cmd.End();

    // ---- 顺序断言：SetPSO < SetVB < SetIB < SetCB < DrawIndexed ----------------
    auto indexOf = [&](const char* name) -> int {
        for (size_t i = 0; i < cmd.calls.size(); ++i) {
            if (cmd.calls[i] == name) {
                return static_cast<int>(i);
            }
        }
        return -1;
    };
    const int iSetPso = indexOf("SetPipelineState");
    const int iSetVb = indexOf("SetVertexBuffers");
    const int iSetIb = indexOf("SetIndexBuffer");
    const int iSetCb = indexOf("SetGraphicsConstantBuffer");
    const int iDraw = indexOf("DrawIndexedInstanced");
    REQUIRE(iSetPso >= 0);
    REQUIRE(iSetVb >= 0);
    REQUIRE(iSetIb >= 0);
    REQUIRE(iSetCb >= 0);
    REQUIRE(iDraw >= 0);
    CHECK(iSetPso < iSetVb); // 简报要求的 SetPSO→SetVB 顺序
    CHECK(iSetVb < iSetIb);
    CHECK(iSetIb < iSetCb);
    CHECK(iSetCb < iDraw);   // 常量绑定先于绘制
    CHECK(iSetPso < iDraw);  // 核心：SetPSO→...→DrawIndexed 全序

    // ---- 结构化参数核对 ----------------------------------------------------------
    REQUIRE(cmd.drawCalls.size() == 1u);
    CHECK(cmd.drawCalls[0].indexCountPerInstance == 36u);
    CHECK(cmd.drawCalls[0].instanceCount == 1u);
    CHECK(cmd.drawCalls[0].startIndexLocation == 0u);
    CHECK(cmd.drawCalls[0].baseVertexLocation == 0);
    CHECK(cmd.drawCalls[0].startInstanceLocation == 0u);
    REQUIRE(cmd.vertexBufferCalls.size() == 1u);
    REQUIRE(cmd.vertexBufferCalls[0].buffers.size() == 1u);
    CHECK(cmd.vertexBufferCalls[0].buffers[0] == vb);
    CHECK(cmd.boundPso == pso);
    CHECK(cmd.boundIndexBuffer == ib);
    REQUIRE(cmd.constantBufferCalls.size() == 1u);
    CHECK(cmd.constantBufferCalls[0].slot == 0u); // v1 根签名单 CBV b0
    CHECK(cmd.constantBufferCalls[0].buffer == cb);
    CHECK(cmd.viewportWidth == 640u);
    CHECK(cmd.viewportHeight == 480u);

    // 多项绘制：批内逐项 SetCB→DrawIndexed 交替（draw list 消费形态）
    cmd.calls.clear();
    cmd.drawCalls.clear();
    cmd.constantBufferCalls.clear();
    ZrBuffer cb2 = device.CreateBuffer(ZrBufferDesc {});
    cmd.SetGraphicsConstantBuffer(0u, cb);
    cmd.DrawIndexedInstanced(36u, 1u, 0u, 0u, 0u);
    cmd.SetGraphicsConstantBuffer(0u, cb2);
    cmd.DrawIndexedInstanced(36u, 1u, 0u, 0u, 0u);
    REQUIRE(cmd.drawCalls.size() == 2u);
    REQUIRE(cmd.constantBufferCalls.size() == 2u);
    CHECK(cmd.constantBufferCalls[1].buffer == cb2);
    // 交替顺序：每帧常量绑定紧跟一次绘制
    CHECK(indexOf("SetGraphicsConstantBuffer") < indexOf("DrawIndexedInstanced"));
}
