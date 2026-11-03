// =============================================================================
// test_framegen_inputs.cpp —— FG 输入（depth/MV）交接校验单测（TASK-B-R9-02 阶段 B）
//
// 覆盖：
//   1. 合法描述：R32F 深度 + RG16F MV → OK，报告字段（尺寸/每像素字节数）填满
//   2. D32F 深度附件形态同样接受（URP 真实深度附件的 fmt=Depth 口径）
//   3. 空指针：desc/out 为 null、纹理句柄为 null → 明确错误码
//   4. ABI：structSize 不符 → ZR_FG_INPUT_ERR_ABI（不读其它字段）
//   5. 尺寸：0 维、超上限、调用方上限本身非法 → ZR_FG_INPUT_ERR_SIZE
//   6. 尺寸不一致 → ZR_FG_INPUT_ERR_MISMATCH
//   7. 格式：depth/MV 白名单外取值 → ZR_FG_INPUT_ERR_FORMAT
//
// 说明：本组函数是纯逻辑（不建设备、不碰 FFX），指针只做非空判定——
// 因此用非空哨兵地址即可覆盖全部判定分支。
// =============================================================================
#include "doctest/doctest.h"

#include "zplugin/zplugin.h"

#include <cstring>

namespace {

/// 非空哨兵：只为过"非空"判定，函数不解引用
void* const kFakeDepth = reinterpret_cast<void*>(static_cast<uintptr_t>(0x1000));
void* const kFakeMv = reinterpret_cast<void*>(static_cast<uintptr_t>(0x2000));

ZrFgInputDesc MakeDesc()
{
    ZrFgInputDesc desc {};
    desc.structSize = static_cast<uint32_t>(sizeof(ZrFgInputDesc));
    desc.depth = kFakeDepth;
    desc.depthWidth = 320u;
    desc.depthHeight = 180u;
    desc.depthFormat = ZR_FG_FORMAT_R32_SFLOAT;
    desc.motionVectors = kFakeMv;
    desc.mvWidth = 320u;
    desc.mvHeight = 180u;
    desc.mvFormat = ZR_FG_FORMAT_R16G16_SFLOAT;
    desc.maxDimension = 0u;
    return desc;
}

} // namespace

TEST_CASE("ZR_FrameGen_ValidateInputs: accepts URP depth copy + motion vector")
{
    ZrFgInputDesc desc = MakeDesc();
    ZrFgInputReport report {};
    CHECK(ZR_FrameGen_ValidateInputs(&desc, &report) == ZR_FG_INPUT_OK);
    CHECK(report.structSize == static_cast<uint32_t>(sizeof(ZrFgInputReport)));
    CHECK(report.accepted == 1u);
    CHECK(report.depthOk == 1u);
    CHECK(report.mvOk == 1u);
    CHECK(report.width == 320u);
    CHECK(report.height == 180u);
    CHECK(report.depthBytesPerPixel == 4u);
    CHECK(report.mvBytesPerPixel == 4u);

    // 真实深度附件（Depth 附件口径）同样合法
    desc.depthFormat = ZR_FG_FORMAT_D32_SFLOAT;
    CHECK(ZR_FrameGen_ValidateInputs(&desc, &report) == ZR_FG_INPUT_OK);
    CHECK(report.accepted == 1u);
}

TEST_CASE("ZR_FrameGen_ValidateInputs: null arguments are rejected")
{
    ZrFgInputDesc desc = MakeDesc();
    ZrFgInputReport report {};

    CHECK(ZR_FrameGen_ValidateInputs(nullptr, &report) == ZR_FG_INPUT_ERR_NULL);
    CHECK(ZR_FrameGen_ValidateInputs(&desc, nullptr) == ZR_FG_INPUT_ERR_NULL);

    desc.depth = nullptr;
    CHECK(ZR_FrameGen_ValidateInputs(&desc, &report) == ZR_FG_INPUT_ERR_PTR);
    CHECK(report.accepted == 0u);
    CHECK(report.width == 0u);

    desc = MakeDesc();
    desc.motionVectors = nullptr;
    CHECK(ZR_FrameGen_ValidateInputs(&desc, &report) == ZR_FG_INPUT_ERR_PTR);
}

TEST_CASE("ZR_FrameGen_ValidateInputs: struct size mismatch is an ABI error")
{
    ZrFgInputDesc desc = MakeDesc();
    ZrFgInputReport report {};
    desc.structSize = static_cast<uint32_t>(sizeof(ZrFgInputDesc)) - 4u;
    CHECK(ZR_FrameGen_ValidateInputs(&desc, &report) == ZR_FG_INPUT_ERR_ABI);
    CHECK(report.accepted == 0u);
}

TEST_CASE("ZR_FrameGen_ValidateInputs: size rules (zero, over limit, bad maxDimension)")
{
    ZrFgInputDesc desc = MakeDesc();
    ZrFgInputReport report {};

    desc.depthWidth = 0u;
    CHECK(ZR_FrameGen_ValidateInputs(&desc, &report) == ZR_FG_INPUT_ERR_SIZE);

    desc = MakeDesc();
    desc.mvHeight = 0u;
    CHECK(ZR_FrameGen_ValidateInputs(&desc, &report) == ZR_FG_INPUT_ERR_SIZE);

    // 超过默认上限 16384（depth 与 MV 同尺寸，先撞尺寸规则而不是 MISMATCH）
    desc = MakeDesc();
    desc.depthWidth = 16385u;
    desc.mvWidth = 16385u;
    CHECK(ZR_FrameGen_ValidateInputs(&desc, &report) == ZR_FG_INPUT_ERR_SIZE);

    desc = MakeDesc();
    desc.maxDimension = 32769u; // 调用方上限本身超硬上限
    CHECK(ZR_FrameGen_ValidateInputs(&desc, &report) == ZR_FG_INPUT_ERR_SIZE);

    // 显式放宽上限后同一尺寸应通过
    desc = MakeDesc();
    desc.depthWidth = 8192u;
    desc.depthHeight = 8192u;
    desc.mvWidth = 8192u;
    desc.mvHeight = 8192u;
    desc.maxDimension = 16384u;
    CHECK(ZR_FrameGen_ValidateInputs(&desc, &report) == ZR_FG_INPUT_OK);
    CHECK(report.width == 8192u);
}

TEST_CASE("ZR_FrameGen_ValidateInputs: depth and motion vector sizes must match")
{
    ZrFgInputDesc desc = MakeDesc();
    ZrFgInputReport report {};
    desc.mvWidth = 640u;
    CHECK(ZR_FrameGen_ValidateInputs(&desc, &report) == ZR_FG_INPUT_ERR_MISMATCH);

    desc = MakeDesc();
    desc.mvHeight = 360u;
    desc.mvWidth = 320u;
    CHECK(ZR_FrameGen_ValidateInputs(&desc, &report) == ZR_FG_INPUT_ERR_MISMATCH);
}

TEST_CASE("ZR_FrameGen_ValidateInputs: format whitelist + bytes per pixel")
{
    ZrFgInputDesc desc = MakeDesc();
    ZrFgInputReport report {};

    desc.depthFormat = ZR_FG_FORMAT_R16G16_SFLOAT; // 深度不接受 MV 的格式
    CHECK(ZR_FrameGen_ValidateInputs(&desc, &report) == ZR_FG_INPUT_ERR_FORMAT);

    desc = MakeDesc();
    desc.mvFormat = ZR_FG_FORMAT_R32_SFLOAT; // MV 不接受单通道浮点
    CHECK(ZR_FrameGen_ValidateInputs(&desc, &report) == ZR_FG_INPUT_ERR_FORMAT);

    desc = MakeDesc();
    desc.depthFormat = ZR_FG_FORMAT_UNKNOWN;
    CHECK(ZR_FrameGen_ValidateInputs(&desc, &report) == ZR_FG_INPUT_ERR_FORMAT);

    CHECK(ZR_FrameGen_InputBytesPerPixel(ZR_FG_FORMAT_R32_SFLOAT) == 4u);
    CHECK(ZR_FrameGen_InputBytesPerPixel(ZR_FG_FORMAT_D32_SFLOAT) == 4u);
    CHECK(ZR_FrameGen_InputBytesPerPixel(ZR_FG_FORMAT_R16G16_SFLOAT) == 4u);
    CHECK(ZR_FrameGen_InputBytesPerPixel(ZR_FG_FORMAT_UNKNOWN) == 0u);
    CHECK(ZR_FrameGen_InputBytesPerPixel(12345u) == 0u);
}
