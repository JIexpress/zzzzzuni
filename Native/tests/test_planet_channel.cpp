// =============================================================================
// test_planet_channel.cpp —— 行星 chunk 通道六导出单测（TASK-B-PLANET-04）
//
// 覆盖（全部无需 GPU——GPU 上传/释放/绘制属验证程序 --planet3 与 Unity Player）：
//   1. 内嵌 planet2.hlsl 与 Native/shaders/planet2.hlsl 逐字节一致
//      （first_draw/scene_draw 同规矩——DLL 在 Player 里没有仓库路径，内嵌副本
//      不得与仓库 shader 漂移）
//   2. ChunkCreate 校验链全表（§4.5）：NULL → ABI → RANGE 桶（faceId/lodLevel/
//      cellsPerEdge/cx/cy/neighborMask 高 4 位/reserved0）→ NONFINITE → STATE
//   3. 创建-销毁账目（§7.3② 可测化）：createTotal − destroyTotal == liveChunks
//      全程恒等；pendingUploads/pendingFrees 延迟计数（§8 延迟一帧的可观测面）；
//      vertexBytes 在上传边界前为 0（VB 字节随上传产生）
//   4. 代际防误用（§7.3 第 1 层）：Destroy 后旧句柄全路径失效（Destroy/SetOrigin/
//      SetLod → HANDLE）；新句柄代际必增
//   5. 满容量 FULL（§4.5：liveChunks+pending ≥ 8192）+ 满表账目对账
//   6. SetLod：期望档记录（pendingLod 计数）+ RANGE（lod>1 / 高 4 位掩码）+
//      HANDLE；同帧多次最后生效（直接覆盖语义）
//   7. ClearAll（§7.3 第 3 层）：句柄全失效 + 记账保恒等 + pendingFrees 排队
//   8. 单 chunk 顶点生成耗时基准（补设计稿 §1.2 MISSING——数字进施工报告）
//
// 进程级单例隔离：每个用例以 TestResetForUnitTests() 起步（清 chunk 表/统计/
// 上下文镜像；不动相机原点——B-PLANET-05 用例自管理），与 ZR_Upscaler 测试复位
// 同策。全仓只有本文件调用 chunk 导出，跨文件无顺序耦合。
// =============================================================================
#include "doctest/doctest.h"

#include "zplanet/planet_math.h" // 单 chunk 基准口径对照（生成路径即生产路径）
#include "zplugin/zplugin.h"
#include "zrhi/d3d12/zrhi_d3d12_unity_target.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <limits>
#include <string>
#include <vector>

namespace {

/// 用例前复位：清 chunk 表/统计/上下文镜像（相机原点不动——B-PLANET-05 语义）
void ResetPlanetChannel()
{
    zr::unity_target::planet::TestResetForUnitTests();
}

/// SetWorld 便捷封装（seed 1337 = B-TECH-04 kP2Seed；中心 (0,-R,0) 与窗口
/// planet2 模式同构图——face 0 (+Z) 区块贴近世界原点）
void SetTestWorld()
{
    ZrPlanetWorld world {};
    world.structSize = static_cast<uint32_t>(sizeof(ZrPlanetWorld));
    world.seed = 1337u;
    world.centerAbs[0] = 0.0;
    world.centerAbs[1] = -zplanet::kPlanetRadiusM;
    world.centerAbs[2] = 0.0;
    REQUIRE(ZR_Planet_SetWorld(&world) == ZR_PLANET_OK);
}

/// 构造合法 desc（face 0 中心格，远档，cellsPerEdge=32）
ZrPlanetChunkDesc MakeDesc(uint32_t cx = 16u, uint32_t cy = 16u, uint32_t lod = 0u)
{
    ZrPlanetChunkDesc desc {};
    desc.structSize = static_cast<uint32_t>(sizeof(ZrPlanetChunkDesc));
    desc.faceId = 0u;
    desc.lodLevel = lod;
    desc.cx = cx;
    desc.cy = cy;
    desc.cellsPerEdge = 32u;
    desc.neighborMask = 0u;
    desc.reserved0 = 0u;
    // 锚点 = chunk 中心球面点（建议口径，§4.2）：绝对坐标（行星中心系）
    const int32_t stride = 15; // 远档 res-1
    const int32_t total = 32 * stride;
    const int32_t gU = zplanet::ChunkVertexIndex(static_cast<int32_t>(cx), stride / 2, stride);
    const int32_t gV = zplanet::ChunkVertexIndex(static_cast<int32_t>(cy), stride / 2, stride);
    const zplanet::Vec3d center = { 0.0, -zplanet::kPlanetRadiusM, 0.0 };
    const zplanet::PlanetChunkVertex v = zplanet::PlanetChunkVertexAtGlobal(
        center, 0, gU, gV, total, 1337u);
    desc.originAbs[0] = v.posAbs.x;
    desc.originAbs[1] = v.posAbs.y;
    desc.originAbs[2] = v.posAbs.z;
    return desc;
}

/// 读整个文件（二进制）；失败返回 false
bool ReadWholeFile(const char* path, std::string& out)
{
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    if (!file) {
        return false;
    }
    const std::streampos length = file.tellg();
    if (length <= 0) {
        return false;
    }
    file.seekg(0, std::ios::beg);
    out.resize(static_cast<size_t>(length));
    return static_cast<bool>(file.read(&out[0], length));
}

/// CRLF → LF（仓库规范是 LF，检出件可能带 CR）
std::string NormalizeLineEndings(const std::string& text)
{
    std::string out;
    out.reserve(text.size());
    for (size_t i = 0u; i < text.size(); ++i) {
        if (text[i] == '\r') {
            continue;
        }
        out.push_back(text[i]);
    }
    return out;
}

/// 账目恒等式断言（§7.3②）：createTotal − destroyTotal == liveChunks
void RequireAccountInvariant()
{
    ZrPlanetStats stats {};
    REQUIRE(ZR_Planet_GetStats(&stats) == ZR_PLANET_OK);
    CHECK(stats.structSize == static_cast<uint32_t>(sizeof(ZrPlanetStats)));
    const uint64_t live = stats.createTotal - stats.destroyTotal;
    MESSAGE("account: create=", stats.createTotal, " destroy=", stats.destroyTotal,
        " live=", stats.liveChunks, " pendingUp=", stats.pendingUploads,
        " pendingFree=", stats.pendingFrees, " pendingLod=", stats.pendingLod,
        " vbytes=", stats.vertexBytes);
    REQUIRE(live == static_cast<uint64_t>(stats.liveChunks));
}

} // namespace

TEST_CASE("planet channel: 内嵌 planet2.hlsl 与仓库 shader 逐字节一致")
{
    uint32_t embeddedSize = 0u;
    const char* embedded = zr::unity_target::EmbeddedPlanet2Hlsl(&embeddedSize);
    REQUIRE(embedded != nullptr);
    CHECK(embeddedSize > 0u);

    std::string file;
    REQUIRE(ReadWholeFile("Native/shaders/planet2.hlsl", file));
    const std::string normalized = NormalizeLineEndings(file);
    // 逐字节一致（同 NUL 终止口径：embeddedSize 不含 NUL）
    REQUIRE(normalized.size() == embeddedSize);
    CHECK(std::memcmp(normalized.data(), embedded, embeddedSize) == 0);
}

TEST_CASE("planet channel: ChunkCreate 校验链全表（NULL→ABI→RANGE→NONFINITE→STATE）")
{
    ResetPlanetChannel(); // 清上下文镜像——ERR_STATE 判定只依赖本文件（不依赖跨文件序）
    SetTestWorld(); // 再置世界（本用例主体需要）——STATE 负例走通道侧复核

    uint64_t handle = 0u;
    ZrPlanetChunkDesc desc = MakeDesc();

    // ---- NULL（最靠前）----
    CHECK(ZR_Planet_ChunkCreate(nullptr, &handle) == ZR_PLANET_ERR_NULL);
    CHECK(ZR_Planet_ChunkCreate(&desc, nullptr) == ZR_PLANET_ERR_NULL);
    // ---- ABI（structSize 不符；布局未知不得继续解引用）----
    desc = MakeDesc();
    desc.structSize = 8u;
    CHECK(ZR_Planet_ChunkCreate(&desc, &handle) == ZR_PLANET_ERR_ABI);
    desc.structSize = 4096u;
    CHECK(ZR_Planet_ChunkCreate(&desc, &handle) == ZR_PLANET_ERR_ABI);
    // ---- RANGE 桶（§4.5 逐项）----
    desc = MakeDesc();
    desc.faceId = 6u;
    CHECK(ZR_Planet_ChunkCreate(&desc, &handle) == ZR_PLANET_ERR_RANGE);
    desc = MakeDesc();
    desc.faceId = 0xFFFFFFFFu;
    CHECK(ZR_Planet_ChunkCreate(&desc, &handle) == ZR_PLANET_ERR_RANGE);
    desc = MakeDesc();
    desc.lodLevel = 2u; // v1 两档锁死
    CHECK(ZR_Planet_ChunkCreate(&desc, &handle) == ZR_PLANET_ERR_RANGE);
    desc = MakeDesc();
    desc.cellsPerEdge = 0u;
    CHECK(ZR_Planet_ChunkCreate(&desc, &handle) == ZR_PLANET_ERR_RANGE);
    desc = MakeDesc();
    desc.cellsPerEdge = 4097u;
    CHECK(ZR_Planet_ChunkCreate(&desc, &handle) == ZR_PLANET_ERR_RANGE);
    desc = MakeDesc();
    desc.cx = desc.cellsPerEdge; // 面内格 u 越界（zplugin.h 声明域 0..cellsPerEdge-1）
    CHECK(ZR_Planet_ChunkCreate(&desc, &handle) == ZR_PLANET_ERR_RANGE);
    desc = MakeDesc();
    desc.cy = desc.cellsPerEdge;
    CHECK(ZR_Planet_ChunkCreate(&desc, &handle) == ZR_PLANET_ERR_RANGE);
    desc = MakeDesc();
    desc.neighborMask = 0x10u; // 高 4 位保留必须 0
    CHECK(ZR_Planet_ChunkCreate(&desc, &handle) == ZR_PLANET_ERR_RANGE);
    desc = MakeDesc();
    desc.neighborMask = 0x80u;
    CHECK(ZR_Planet_ChunkCreate(&desc, &handle) == ZR_PLANET_ERR_RANGE);
    desc = MakeDesc();
    desc.reserved0 = 7u;
    CHECK(ZR_Planet_ChunkCreate(&desc, &handle) == ZR_PLANET_ERR_RANGE);
    // ---- NONFINITE（originAbs 任一分量）----
    const double qnan = std::numeric_limits<double>::quiet_NaN();
    const double inf = std::numeric_limits<double>::infinity();
    desc = MakeDesc();
    desc.originAbs[1] = qnan;
    CHECK(ZR_Planet_ChunkCreate(&desc, &handle) == ZR_PLANET_ERR_NONFINITE);
    desc = MakeDesc();
    desc.originAbs[2] = inf;
    CHECK(ZR_Planet_ChunkCreate(&desc, &handle) == ZR_PLANET_ERR_NONFINITE);
    // ---- STATE（SetWorld 前置——通道侧镜像被 Reset 清空，权威快照仍在，
    //      走通道复核路径；不写 outHandle）----
    zr::unity_target::planet::TestResetForUnitTests();
    desc = MakeDesc();
    handle = 0xDEADu;
    CHECK(ZR_Planet_ChunkCreate(&desc, &handle) == ZR_PLANET_ERR_STATE);
    CHECK(handle == 0xDEADu); // 失败不写 outHandle（§14 组 3 契约）
    // ---- 全链通过（合法 desc）----
    SetTestWorld();
    desc = MakeDesc();
    CHECK(ZR_Planet_ChunkCreate(&desc, &handle) == ZR_PLANET_OK);
    CHECK(handle != 0u); // 0 恒无效（§4.1）
    RequireAccountInvariant();
    ResetPlanetChannel();
}

TEST_CASE("planet channel: 创建-销毁账目对账 + 延迟计数（pendingUpload/pendingFree）")
{
    ResetPlanetChannel();
    SetTestWorld();

    // 创建：PENDING_UPLOAD 状态（延迟上传——§7.1 上传时机 = 下一 RenderFrame）
    uint64_t h1 = 0u;
    ZrPlanetChunkDesc desc = MakeDesc(16u, 16u, 0u);
    REQUIRE(ZR_Planet_ChunkCreate(&desc, &h1) == ZR_PLANET_OK);
    ZrPlanetStats stats {};
    REQUIRE(ZR_Planet_GetStats(&stats) == ZR_PLANET_OK);
    CHECK(stats.liveChunks == 1u);
    CHECK(stats.pendingUploads == 1u); // 待渲染线程上传
    CHECK(stats.pendingFrees == 0u);
    CHECK(stats.createTotal == 1u);
    CHECK(stats.destroyTotal == 0u);
    CHECK(stats.vertexBytes == 0u); // VB 字节随上传边界产生（GPU 上传延迟一帧）
    CHECK(stats.lastFrameMs == 0.0); // 尚无 RenderFrame
    CHECK(stats.avgFrameMs == 0.0);
    RequireAccountInvariant();

    // 销毁：句柄立即失效（代际+1），GPU 资源下一 RenderFrame 释放——pendingFrees
    // 计数在 Destroy 与 RenderFrame 之间可观测（延迟释放计数，§8）
    REQUIRE(ZR_Planet_ChunkDestroy(h1) == ZR_PLANET_OK);
    REQUIRE(ZR_Planet_GetStats(&stats) == ZR_PLANET_OK);
    CHECK(stats.liveChunks == 0u); // 句柄已失效——live 不含 PENDING_FREE
    CHECK(stats.pendingUploads == 0u);
    CHECK(stats.pendingFrees == 1u); // 待渲染线程释放（延迟释放计数）
    CHECK(stats.createTotal == 1u);
    CHECK(stats.destroyTotal == 1u);
    RequireAccountInvariant();

    // 混合序列全程对账：create×5 → destroy×2 → create×3 → destroy×1
    uint64_t handles[8] = {};
    for (int i = 0; i < 5; ++i) {
        desc = MakeDesc(static_cast<uint32_t>(10u + i), 16u, 0u);
        REQUIRE(ZR_Planet_ChunkCreate(&desc, &handles[i]) == ZR_PLANET_OK);
        RequireAccountInvariant();
    }
    REQUIRE(ZR_Planet_GetStats(&stats) == ZR_PLANET_OK);
    CHECK(stats.liveChunks == 5u);
    REQUIRE(ZR_Planet_ChunkDestroy(handles[0]) == ZR_PLANET_OK);
    RequireAccountInvariant();
    REQUIRE(ZR_Planet_ChunkDestroy(handles[1]) == ZR_PLANET_OK);
    RequireAccountInvariant();
    for (int i = 5; i < 8; ++i) {
        desc = MakeDesc(static_cast<uint32_t>(10u + i), 16u, 0u);
        REQUIRE(ZR_Planet_ChunkCreate(&desc, &handles[i]) == ZR_PLANET_OK);
        RequireAccountInvariant();
    }
    REQUIRE(ZR_Planet_ChunkDestroy(handles[2]) == ZR_PLANET_OK);
    RequireAccountInvariant();
    REQUIRE(ZR_Planet_GetStats(&stats) == ZR_PLANET_OK);
    // 1（h1）+ 5 + 3 = 9 创建；1 + 2 + 1 = 4 销毁；live = 9 − 4 = 5
    CHECK(stats.liveChunks == 5u);
    CHECK(stats.createTotal == 9u);
    CHECK(stats.destroyTotal == 4u);
    ResetPlanetChannel();
}

TEST_CASE("planet channel: 代际防误用——Destroy 后旧句柄全路径失效（§7.3 第 1 层）")
{
    ResetPlanetChannel();
    SetTestWorld();

    uint64_t h1 = 0u;
    ZrPlanetChunkDesc desc = MakeDesc(16u, 16u, 0u);
    REQUIRE(ZR_Planet_ChunkCreate(&desc, &h1) == ZR_PLANET_OK);

    // 释放前一切正常
    CHECK(ZR_Planet_ChunkSetOrigin(h1, 1.0, 2.0, 3.0) == ZR_PLANET_OK);
    CHECK(ZR_Planet_SetLod(h1, 1u, 0u) == ZR_PLANET_OK);

    REQUIRE(ZR_Planet_ChunkDestroy(h1) == ZR_PLANET_OK);
    // 旧句柄全路径失效：Destroy（重复释放）/SetOrigin/SetLod 一律 HANDLE（D4——
    // 释放必须可检测，区别于 ZR_Unity_DestroyMesh 的 void 静默风格）
    CHECK(ZR_Planet_ChunkDestroy(h1) == ZR_PLANET_ERR_HANDLE);
    CHECK(ZR_Planet_ChunkSetOrigin(h1, 1.0, 2.0, 3.0) == ZR_PLANET_ERR_HANDLE);
    CHECK(ZR_Planet_SetLod(h1, 0u, 0u) == ZR_PLANET_ERR_HANDLE);

    // 从未创建的句柄（槽位越界/代际 0）同样 HANDLE
    CHECK(ZR_Planet_ChunkDestroy(0u) == ZR_PLANET_ERR_HANDLE);
    CHECK(ZR_Planet_ChunkDestroy(0x0000000100000000ull) == ZR_PLANET_ERR_HANDLE); // gen=0
    CHECK(ZR_Planet_ChunkDestroy(0xFFFFFFFFFFFFFFFFull) == ZR_PLANET_ERR_HANDLE); // 槽越界
    CHECK(ZR_Planet_ChunkSetOrigin(0xFFFFFFFFFFFFFFFFull, 1.0, 2.0, 3.0)
        == ZR_PLANET_ERR_HANDLE);

    // 重创同格 chunk：必然得到与旧句柄不同的句柄（doctest 进程无 RenderFrame，
    // 旧槽位处于 PENDING_FREE 不可复用 → 新句柄落新槽；代际防误用的核心断言是
    // "旧句柄全路径失效 + 新旧句柄不混淆"，同槽代际推进由 (h2>>32)==1 与旧句柄
    // 仍失效共同锁定）
    uint64_t h2 = 0u;
    desc = MakeDesc(16u, 16u, 0u);
    REQUIRE(ZR_Planet_ChunkCreate(&desc, &h2) == ZR_PLANET_OK);
    CHECK(h2 != h1);
    CHECK((h2 & 0xFFFFFFFFull) != (h1 & 0xFFFFFFFFull)); // PENDING_FREE 槽位不复用
    CHECK((h2 >> 32) == 1u); // 新槽位首代际
    CHECK(ZR_Planet_ChunkSetOrigin(h2, 4.0, 5.0, 6.0) == ZR_PLANET_OK);
    CHECK(ZR_Planet_ChunkDestroy(h1) == ZR_PLANET_ERR_HANDLE); // h1 仍失效
    CHECK(ZR_Planet_ChunkDestroy(h2) == ZR_PLANET_OK);
    RequireAccountInvariant();
    ResetPlanetChannel();
}

TEST_CASE("planet channel: 满容量 FULL（8192 槽）+ 满表账目对账")
{
    ResetPlanetChannel();
    SetTestWorld();

    // 远档全量灌满（8192 = ZR_PLANET_MAX_CHUNKS；§4.5 槽位满 → ERR_FULL）
    uint64_t handle = 0u;
    uint32_t slotOfFirst = 0u;
    for (uint32_t i = 0; i < ZR_PLANET_MAX_CHUNKS; ++i) {
        ZrPlanetChunkDesc desc = MakeDesc(i % 32u, (i / 32u) % 32u, 0u);
        desc.faceId = (i / (32u * 32u)) % 6u;
        const int rc = ZR_Planet_ChunkCreate(&desc, &handle);
        if (rc != ZR_PLANET_OK) {
            MESSAGE("FULL 早于 8192：i=", i, " rc=", rc);
            REQUIRE(rc == ZR_PLANET_OK);
        }
        if (i == 0u) {
            slotOfFirst = static_cast<uint32_t>(handle & 0xFFFFFFFFull);
        }
    }
    ZrPlanetStats stats {};
    REQUIRE(ZR_Planet_GetStats(&stats) == ZR_PLANET_OK);
    CHECK(stats.liveChunks == ZR_PLANET_MAX_CHUNKS);
    CHECK(stats.createTotal == ZR_PLANET_MAX_CHUNKS);
    CHECK(stats.destroyTotal == 0u);
    CHECK(stats.pendingUploads == ZR_PLANET_MAX_CHUNKS);
    RequireAccountInvariant();

    // 满表再创建 → FULL（不写 outHandle）
    ZrPlanetChunkDesc desc = MakeDesc(0u, 0u, 0u);
    handle = 0x1234u;
    CHECK(ZR_Planet_ChunkCreate(&desc, &handle) == ZR_PLANET_ERR_FULL);
    CHECK(handle == 0x1234u);

    // ClearAll（§7.3 第 3 层）：句柄全失效 + 全部进 PENDING_FREE 排队 + 记账保恒等
    ZR_Planet_ClearAll();
    REQUIRE(ZR_Planet_GetStats(&stats) == ZR_PLANET_OK);
    CHECK(stats.liveChunks == 0u);
    CHECK(stats.pendingFrees == ZR_PLANET_MAX_CHUNKS); // 渲染线程排队释放
    CHECK(stats.destroyTotal == ZR_PLANET_MAX_CHUNKS); // bulk 释放按销毁记账
    CHECK(stats.createTotal == ZR_PLANET_MAX_CHUNKS);
    RequireAccountInvariant();
    // 旧句柄全失效（首个句柄验证代际推进）
    const uint64_t firstHandle = (1ull << 32) | static_cast<uint64_t>(slotOfFirst);
    CHECK(ZR_Planet_ChunkDestroy(firstHandle) == ZR_PLANET_ERR_HANDLE);
    ResetPlanetChannel();
}

TEST_CASE("planet channel: SetLod 期望档记录 + RANGE/HANDLE + 同帧最后生效")
{
    ResetPlanetChannel();
    SetTestWorld();

    uint64_t h = 0u;
    ZrPlanetChunkDesc desc = MakeDesc(16u, 16u, 0u);
    REQUIRE(ZR_Planet_ChunkCreate(&desc, &h) == ZR_PLANET_OK);

    ZrPlanetStats stats {};
    // 远档创建后无待应用档位
    REQUIRE(ZR_Planet_GetStats(&stats) == ZR_PLANET_OK);
    CHECK(stats.pendingLod == 0u);

    // 设期望档 → pendingLod 计数（下一 RenderFrame 应用——§5.2/D6；"下一帧应用"
    // 的行为断言在验证程序 --planet3 的 lodChangeTotal 通道内完成）
    CHECK(ZR_Planet_SetLod(h, 1u, 0u) == ZR_PLANET_OK);
    REQUIRE(ZR_Planet_GetStats(&stats) == ZR_PLANET_OK);
    CHECK(stats.pendingLod == 1u);

    // 同帧多次最后生效（直接覆盖——远→近→远回到无待应用）
    CHECK(ZR_Planet_SetLod(h, 0u, 0u) == ZR_PLANET_OK);
    REQUIRE(ZR_Planet_GetStats(&stats) == ZR_PLANET_OK);
    CHECK(stats.pendingLod == 0u);
    CHECK(ZR_Planet_SetLod(h, 1u, ZR_PLANET_EDGE_NEG_U | ZR_PLANET_EDGE_POS_V) == ZR_PLANET_OK);
    REQUIRE(ZR_Planet_GetStats(&stats) == ZR_PLANET_OK);
    CHECK(stats.pendingLod == 1u);

    // RANGE：lodLevel > 1 / neighborMask 高 4 位非 0（§14 组 3）
    CHECK(ZR_Planet_SetLod(h, 2u, 0u) == ZR_PLANET_ERR_RANGE);
    CHECK(ZR_Planet_SetLod(h, 0u, 0x10u) == ZR_PLANET_ERR_RANGE);
    CHECK(ZR_Planet_SetLod(h, 1u, 0xF0u) == ZR_PLANET_ERR_RANGE);
    // 合法四缘位全部接受
    CHECK(ZR_Planet_SetLod(h, 1u,
            ZR_PLANET_EDGE_NEG_U | ZR_PLANET_EDGE_POS_U | ZR_PLANET_EDGE_NEG_V
                | ZR_PLANET_EDGE_POS_V)
        == ZR_PLANET_OK);
    // HANDLE：无效句柄
    CHECK(ZR_Planet_SetLod(0xFFFFFFFFFFFFFFFFull, 0u, 0u) == ZR_PLANET_ERR_HANDLE);
    CHECK(ZR_Planet_SetLod(0u, 0u, 0u) == ZR_PLANET_ERR_HANDLE);
    RequireAccountInvariant();
    ResetPlanetChannel();
}

TEST_CASE("planet channel: ChunkSetOrigin 语义（NaN 拒绝/HANDLE/顶点不重算口径）")
{
    ResetPlanetChannel();
    SetTestWorld();

    uint64_t h = 0u;
    ZrPlanetChunkDesc desc = MakeDesc(16u, 16u, 0u);
    REQUIRE(ZR_Planet_ChunkCreate(&desc, &h) == ZR_PLANET_OK);

    // NaN/Inf 任一分量拒绝（§4.3）——错误路径不动锚点
    const double qnan = std::numeric_limits<double>::quiet_NaN();
    const double inf = std::numeric_limits<double>::infinity();
    CHECK(ZR_Planet_ChunkSetOrigin(h, qnan, 0.0, 0.0) == ZR_PLANET_ERR_NONFINITE);
    CHECK(ZR_Planet_ChunkSetOrigin(h, 0.0, inf, 0.0) == ZR_PLANET_ERR_NONFINITE);
    CHECK(ZR_Planet_ChunkSetOrigin(h, 0.0, 0.0, -inf) == ZR_PLANET_ERR_NONFINITE);
    // 行星尺度合法平移（行星整体移动的托管批量口径，§4.3）
    CHECK(ZR_Planet_ChunkSetOrigin(h, 6.4e6, 0.0, -6.371e6) == ZR_PLANET_OK);
    CHECK(ZR_Planet_ChunkSetOrigin(0xFFFFFFFFFFFFFFFFull, 1.0, 1.0, 1.0)
        == ZR_PLANET_ERR_HANDLE);
    RequireAccountInvariant();
    ResetPlanetChannel();
}

TEST_CASE("planet channel: 单 chunk 顶点生成耗时基准（补设计稿 §1.2 MISSING）")
{
    ResetPlanetChannel();
    SetTestWorld();

    // 口径：QPC 墙钟、ZK/ChunkCreate 全路径（校验 + 锁外规范路由顶点生成 + 独占
    // 发布）、Release 构建单线程。K 次均值；数字进施工报告（不在此写死阈值——
    // 仅防退化的宽松上界，CI 噪声不敏感）。
    constexpr uint32_t kFarIters = 64u;
    constexpr uint32_t kNearIters = 8u;
    double farMs = 0.0;
    double nearMs = 0.0;
    LARGE_INTEGER freq {};
    QueryPerformanceFrequency(&freq);
    LARGE_INTEGER t0 {};
    LARGE_INTEGER t1 {};

    for (uint32_t i = 0; i < kFarIters; ++i) {
        ZrPlanetChunkDesc desc = MakeDesc(i % 32u, (i / 2u) % 32u, 0u);
        uint64_t h = 0u;
        QueryPerformanceCounter(&t0);
        const int rc = ZR_Planet_ChunkCreate(&desc, &h);
        QueryPerformanceCounter(&t1);
        REQUIRE(rc == ZR_PLANET_OK);
        REQUIRE(h != 0u);
        farMs += static_cast<double>(t1.QuadPart - t0.QuadPart) * 1000.0
            / static_cast<double>(freq.QuadPart);
        REQUIRE(ZR_Planet_ChunkDestroy(h) == ZR_PLANET_OK);
    }
    for (uint32_t i = 0; i < kNearIters; ++i) {
        ZrPlanetChunkDesc desc = MakeDesc(i % 32u, (i / 2u) % 32u, 1u);
        uint64_t h = 0u;
        QueryPerformanceCounter(&t0);
        const int rc = ZR_Planet_ChunkCreate(&desc, &h);
        QueryPerformanceCounter(&t1);
        REQUIRE(rc == ZR_PLANET_OK);
        nearMs += static_cast<double>(t1.QuadPart - t0.QuadPart) * 1000.0
            / static_cast<double>(freq.QuadPart);
        REQUIRE(ZR_Planet_ChunkDestroy(h) == ZR_PLANET_OK);
    }
    const double farUs = farMs * 1000.0 / static_cast<double>(kFarIters);
    const double nearUs = nearMs * 1000.0 / static_cast<double>(kNearIters);
    MESSAGE("单 chunk 生成耗时（ChunkCreate 全路径，Release）：远档(320 顶点)="
        << farUs << " us/chunk；近档(4352 顶点)=" << nearUs << " us/chunk");
    // 顶点数比例 sanity：近档顶点数 ≈ 13.6× 远档，耗时同量级以上
    CHECK(nearUs > farUs * 4.0);
    // 宽松退化上界（防回归写死阈值；实测数字进报告）
    CHECK(farUs < 5000.0);
    CHECK(nearUs < 50000.0);
    RequireAccountInvariant();
    ResetPlanetChannel();
}

// =============================================================================
// TASK-B-PLANET-08：接缝专项（先测量后裁决）
// =============================================================================

// ---- 生产符号白盒声明（测试二进制经 zrhi 静态库直连，零复刻漂移）----------------
// GenerateChunkVerts 是生产顶点生成函数本体（zrhi_d3d12_unity_target.cpp 编译
// 单元，与 zrender.dll 同源；memcpy 进 GPU VB 的就是它的输出字节）。planet_detail
// 位于匿名命名空间（内部链接）不可直连，经 TestGenerateChunkVertsForUnitTests
// 外部链接薄桥转发到生产函数本体（unity_target.cpp，零复制零漂移）。其签名
// **不含 neighborMask**——v1「掩码不进几何路径」（设计稿 §5.2 方案 A）的构造性
// 证据。布局镜像 SeamVBVertex 以逐顶点逐位比对自证（见测量用例 (1)）。
// TestNeighborMaskForUnitTests / TestSeamChecksForUnitTests /
// TestPromoteUploadsForUnitTests 是 B-PLANET-08 测试钩子（unity_target.cpp，与
// TestResetForUnitTests 同策：不出 def、生产行为零改动）——分别锁定 neighborMask
// v1「校验+记录」的记录面（GetStats 无掩码字段、ZrPlanetStats 布局冻结）与
// G4-R1 seam 核查门的「远档跳过/近档仍核查」行为面（seamCheckFails 只反映失配，
// 无法区分「查了全绿」与「没查」）。
namespace zr {
namespace unity_target {
namespace planet {
uint32_t TestNeighborMaskForUnitTests(uint64_t handle);
uint64_t TestSeamChecksForUnitTests(void);
uint32_t TestPromoteUploadsForUnitTests(void);
uint32_t TestGenerateChunkVertsForUnitTests(uint32_t faceId, uint32_t cx, uint32_t cy,
    uint32_t cellsPerEdge, uint32_t tier, const double originAbs[3],
    std::vector<uint8_t>& out);
void TestSeamCheckNearForUnitTests(uint32_t faceId, uint32_t cx, uint32_t cy,
    uint32_t cellsPerEdge, uint32_t tier);
} // namespace planet
} // namespace unity_target
} // namespace zr

namespace {

/// 生产 TargetVertex 布局镜像（unity_target.cpp「v1 固定顶点布局」32B）
struct SeamVBVertex {
    float pos[3];
    float normal[3];
    float uv[2];
};
static_assert(sizeof(SeamVBVertex) == 32, "SeamVBVertex must mirror TargetVertex (32 bytes)");

/// 缝宽分布桶（mm）：0 / ≤0.5 / ≤1 / ≤2 / ≤4 / ≤8 / ≤16 / >16
uint32_t SeamBucket(double seamM)
{
    const double mm = seamM * 1000.0;
    if (mm <= 0.0) {
        return 0u;
    }
    if (mm <= 0.5) {
        return 1u;
    }
    if (mm <= 1.0) {
        return 2u;
    }
    if (mm <= 2.0) {
        return 3u;
    }
    if (mm <= 4.0) {
        return 4u;
    }
    if (mm <= 8.0) {
        return 5u;
    }
    if (mm <= 16.0) {
        return 6u;
    }
    return 7u;
}

/// 一个口径的缝宽样本聚合
struct SeamAggregate {
    double maxM = 0.0;
    double sumM = 0.0;
    uint32_t count = 0u;
    uint32_t bucket[8] = {};
    void Add(double seamM)
    {
        if (seamM > maxM) {
            maxM = seamM;
        }
        sumM += seamM;
        ++count;
        ++bucket[SeamBucket(seamM)];
    }
    double MeanM() const { return (count > 0u) ? sumM / static_cast<double>(count) : 0.0; }
};

void ReportSeamAggregate(const char* label, const SeamAggregate& a)
{
    // 注意：doctest MESSAGE 对 const char* 操作数走指针→bool 串化（输出 "1"），
    // 标签须包 std::string 才能按字符串打印（字面量走 char[N] 数组路径不受影响）。
    MESSAGE(std::string(label) << ": n=" << a.count << " max=" << a.maxM * 1000.0
                               << "mm mean=" << a.MeanM() * 1000.0
                               << "mm  分布[=0,(0,.5],(.5,1],(1,2],(2,4],"
                                  "(4,8],(8,16],>16]mm = [" << a.bucket[0] << ","
                               << a.bucket[1] << "," << a.bucket[2] << "," << a.bucket[3]
                               << "," << a.bucket[4] << "," << a.bucket[5] << ","
                               << a.bucket[6] << "," << a.bucket[7] << "]");
}

} // namespace

TEST_CASE("planet channel: B-PLANET-08 共享边 float VB 缝宽实测（生产路径测量探针）")
{
    // 任务简报第 1 项：共享边同一顶点在相邻 chunk A/B 两侧 float VB 值差（米）。
    // 三层口径（引用数字时须连带口径）：
    //   (2) 每侧静态 float 化偏差 |ε| = |float(posAbs−anchor) − (posAbs−anchor)|
    //       ——VB 存储值对 double 真值的量化误差（设计稿 §9 表第 5 行的实测）。
    //   (3) VB 回读口径（无相机、静态）：abs = anchor + double(local)，
    //       seam = |absA − absB| = |εA − εB|——两次独立 float 舍入的绝对域发散。
    //       这是「VB 回读缝宽」的直测量（设计稿 §1.2 MISSING 本体）。
    //   (4) 渲染口径（端到端、相机相关）：t = float(anchor−cam)（生产
    //       unity_target.cpp 逐绘 CB 口径）+ viewPos = float(local + t)（
    //       planet2.hlsl VS mul(model,·) 平移加法），seam = |viewA − viewB|。
    //       相机 = 本缘中点上方 2m（可见性口径——缝宽必须在「这条缘可见」的
    //       相机位测量；远处相机的缝宽被 |anchor−cam| 的平移量化主导，角分辨率
    //       恒 ~1e-7 rad 级，不构成可见缝）。
    // 对照：设计稿 §5.1 估算「≤~31mm（2×ULP@221km）」标估算；本用例出实测。
    // 裙边裕度：生产 SkirtDropM = 2%×格距（注意：B-TECH-04 planet2 口径是
    // 2%×chunk 弧长 6.25km——两口径差如实登记，见任务日志）。
    ResetPlanetChannel();
    SetTestWorld();

    constexpr uint32_t kCells = 32u;
    constexpr uint32_t kSeed = 1337u; // 与 SetWorldContext（SetTestWorld）同种子
    const zplanet::Vec3d center = { 0.0, -zplanet::kPlanetRadiusM, 0.0 };

    // chunk 中心锚点（生产建议口径，MakeDesc/--planet3 同式）
    auto anchorOf = [&](uint32_t face, uint32_t cx, uint32_t cy, uint32_t tier)
        -> zplanet::Vec3d {
        const int32_t stride = (tier >= 1u) ? 63 : 15;
        const int32_t total = static_cast<int32_t>(kCells) * stride;
        const int32_t gU
            = zplanet::ChunkVertexIndex(static_cast<int32_t>(cx), stride / 2, stride);
        const int32_t gV
            = zplanet::ChunkVertexIndex(static_cast<int32_t>(cy), stride / 2, stride);
        return zplanet::PlanetChunkVertexAtGlobal(
            center, static_cast<int>(face), gU, gV, total, kSeed)
            .posAbs;
    };

    // 生产生成 + 布局镜像；返回锚点
    auto genChunk = [&](uint32_t face, uint32_t cx, uint32_t cy, uint32_t tier,
                        std::vector<SeamVBVertex>& verts) -> zplanet::Vec3d {
        const zplanet::Vec3d a = anchorOf(face, cx, cy, tier);
        const double origin[3] = { a.x, a.y, a.z };
        std::vector<uint8_t> bytes;
        const uint32_t n = zr::unity_target::planet::TestGenerateChunkVertsForUnitTests(
            face, cx, cy, kCells, tier, origin, bytes);
        const uint32_t res = (tier >= 1u) ? 64u : 16u;
        REQUIRE(n == res * res + 4u * res);
        REQUIRE(bytes.size() == static_cast<size_t>(n) * 32u);
        verts.resize(n);
        std::memcpy(verts.data(), bytes.data(), bytes.size());
        return a;
    };

    // 渲染口径相机（生产真实编排，上下文对照用）：0 = --planet3 贴地 600m（face0
    // 中心方向）；2 = 中空 50km。**聚合口径相机逐缘独立**：camEdge = 该缘中点
    // 上方 2m——缝宽可见性口径必须在「这条缘可见」的相机位测量；把远处相机的
    // 缝宽数字混入聚合会把不可见距离的平移/加法舍入（随 |anchor−cam| 增长）与
    // 可见缝混在一起（跨面缘在 600m 相机下位于 ~4400km 外、远超 87km 地平线，
    // 其 ~500mm 渲染差是 4400km 处 ULP(4.4e6m)=0.25m 的平移量化，角分辨率
    // ~1.2e-7 rad ≈ 0.0001px@1080p——不可见，不属「可见缝」口径）。
    zplanet::Vec3d cams[2];
    {
        const zplanet::Vec3d dir0 = zplanet::CubeSphereDir(0, 0.0, 0.0);
        const double h0 = zplanet::PlanetTerrainHeightM(kSeed, dir0.x, dir0.y, dir0.z);
        cams[0] = zplanet::VecAdd(
            center, zplanet::VecScale(dir0, zplanet::kPlanetRadiusM + h0 + 600.0));
        cams[1] = zplanet::VecAdd(
            center, zplanet::VecScale(dir0, zplanet::kPlanetRadiusM + h0 + 50000.0));
    }

    // 缘对应关系：EastWest = A 东缘(i=res−1,j=k) ↔ B 西缘(i=0,j=k)，k 沿 v 推进；
    // SouthNorth = A 南缘(i=k,j=res−1) ↔ B 北缘(i=k,j=0)，k 沿 u 推进。
    enum class SeamEdge { EastWest, SouthNorth };
    struct SeamPairSpec {
        uint32_t faceA;
        uint32_t cxA;
        uint32_t cyA;
        uint32_t faceB;
        uint32_t cxB;
        uint32_t cyB;
        SeamEdge edge;
        uint32_t tier;
        const char* label;
    };
    const SeamPairSpec pairs[] = {
        { 0u, 16u, 16u, 0u, 17u, 16u, SeamEdge::EastWest, 0u, "far  E-W 同面" },
        { 0u, 16u, 16u, 0u, 16u, 17u, SeamEdge::SouthNorth, 0u, "far  S-N 同面" },
        { 0u, 16u, 16u, 0u, 17u, 16u, SeamEdge::EastWest, 1u, "near E-W 同面" },
        { 0u, 16u, 16u, 0u, 16u, 17u, SeamEdge::SouthNorth, 1u, "near S-N 同面" },
        { 0u, 31u, 16u, 1u, 0u, 16u, SeamEdge::EastWest, 0u, "far  E-W 跨面(0|1)" },
        { 0u, 31u, 16u, 1u, 0u, 16u, SeamEdge::EastWest, 1u, "near E-W 跨面(0|1)" },
    };
    constexpr uint32_t kPairCount = sizeof(pairs) / sizeof(pairs[0]);

    SeamAggregate vbAgg;         // (3) VB 回读口径
    SeamAggregate renderAgg;     // (4) 渲染口径（逐缘 camEdge=缘中点上方 2m，可见性口径）
    double devMaxM = 0.0;        // (2) 每侧静态 float 化偏差最大值
    uint32_t doubleInvariantChecks = 0u;
    uint32_t replicaBitwiseChecks = 0u;

    for (uint32_t p = 0; p < kPairCount; ++p) {
        const SeamPairSpec& spec = pairs[p];
        const uint32_t res = (spec.tier >= 1u) ? 64u : 16u;
        const int32_t stride = (spec.tier >= 1u) ? 63 : 15;
        const int32_t total = static_cast<int32_t>(kCells) * stride;
        std::vector<SeamVBVertex> vertsA;
        std::vector<SeamVBVertex> vertsB;
        const zplanet::Vec3d aA = genChunk(spec.faceA, spec.cxA, spec.cyA, spec.tier, vertsA);
        const zplanet::Vec3d aB = genChunk(spec.faceB, spec.cxB, spec.cyB, spec.tier, vertsB);

        // 本缘可见性口径相机：缘中点（k = res/2）上方 2m（double 域地表点沿法向）
        const int32_t gUm = (spec.edge == SeamEdge::EastWest)
            ? (static_cast<int32_t>(spec.cxA) * stride + stride)
            : (static_cast<int32_t>(spec.cxA) * stride + static_cast<int32_t>(res / 2));
        const int32_t gVm = (spec.edge == SeamEdge::EastWest)
            ? (static_cast<int32_t>(spec.cyA) * stride + static_cast<int32_t>(res / 2))
            : (static_cast<int32_t>(spec.cyA) * stride + stride);
        const zplanet::PlanetChunkVertex em = zplanet::PlanetChunkVertexAtGlobal(
            center, static_cast<int>(spec.faceA), gUm, gVm, total, kSeed);
        const zplanet::Vec3d camEdge = zplanet::VecAdd(center,
            zplanet::VecScale(em.normal, zplanet::kPlanetRadiusM + em.heightM + 2.0));

        SeamAggregate pairVb;
        SeamAggregate pairRenderEdge;
        SeamAggregate pairRender600; // 上下文（仅同面缘——跨面缘 600m 下不可见）
        SeamAggregate pairRender50k; // 上下文（同上）
        for (uint32_t k = 0; k < res; ++k) {
            // 双侧共享顶点的全局索引与 VB 下标
            int32_t gUA = 0;
            int32_t gUB = 0;
            int32_t gVA = 0;
            int32_t gVB = 0;
            uint32_t idxA = 0u;
            uint32_t idxB = 0u;
            if (spec.edge == SeamEdge::EastWest) {
                gUA = static_cast<int32_t>(spec.cxA) * stride + stride;
                gUB = static_cast<int32_t>(spec.cxB) * stride + 0;
                gVA = static_cast<int32_t>(spec.cyA) * stride + static_cast<int32_t>(k);
                gVB = static_cast<int32_t>(spec.cyB) * stride + static_cast<int32_t>(k);
                idxA = k * res + (res - 1u);
                idxB = k * res + 0u;
            } else {
                gUA = static_cast<int32_t>(spec.cxA) * stride + static_cast<int32_t>(k);
                gUB = static_cast<int32_t>(spec.cxB) * stride + static_cast<int32_t>(k);
                gVA = static_cast<int32_t>(spec.cyA) * stride + stride;
                gVB = static_cast<int32_t>(spec.cyB) * stride + 0;
                idxA = (res - 1u) * res + k;
                idxB = 0u * res + k;
            }
            // (0) double 域逐位不变量（同档/跨面共享边的构造性缝合前提——
            //     B-TECH-04 seams 口径在本生产生成路径上的复认，全缘全顶点）
            const zplanet::PlanetChunkVertex vaD = zplanet::PlanetChunkVertexAtGlobal(
                center, static_cast<int>(spec.faceA), gUA, gVA, total, kSeed);
            const zplanet::PlanetChunkVertex vbD = zplanet::PlanetChunkVertexAtGlobal(
                center, static_cast<int>(spec.faceB), gUB, gVB, total, kSeed);
            REQUIRE(std::memcmp(&vaD.posAbs, &vbD.posAbs, sizeof(zplanet::Vec3d)) == 0);
            ++doubleInvariantChecks;
            // (1) 布局/路径自证：缓冲顶点 pos = float(posAbs − origin) 逐位
            const float expectA[3] = { static_cast<float>(vaD.posAbs.x - aA.x),
                static_cast<float>(vaD.posAbs.y - aA.y),
                static_cast<float>(vaD.posAbs.z - aA.z) };
            const float expectB[3] = { static_cast<float>(vbD.posAbs.x - aB.x),
                static_cast<float>(vbD.posAbs.y - aB.y),
                static_cast<float>(vbD.posAbs.z - aB.z) };
            REQUIRE(std::memcmp(expectA, vertsA[idxA].pos, sizeof(expectA)) == 0);
            REQUIRE(std::memcmp(expectB, vertsB[idxB].pos, sizeof(expectB)) == 0);
            ++replicaBitwiseChecks;
            // (2) 每侧静态 float 化偏差（上下文）
            for (int c = 0; c < 3; ++c) {
                const double posC = (c == 0) ? vaD.posAbs.x : ((c == 1) ? vaD.posAbs.y : vaD.posAbs.z);
                const double anchC = (c == 0) ? aA.x : ((c == 1) ? aA.y : aA.z);
                const double dev = std::fabs(
                    static_cast<double>(vertsA[idxA].pos[c]) - (posC - anchC));
                if (dev > devMaxM) {
                    devMaxM = dev;
                }
            }
            // (3) VB 回读口径：abs = anchor + double(local)（double 域度量，
            //     避免把两 float 之差再次 float 舍入）
            double absA[3] = { aA.x + static_cast<double>(vertsA[idxA].pos[0]),
                aA.y + static_cast<double>(vertsA[idxA].pos[1]),
                aA.z + static_cast<double>(vertsA[idxA].pos[2]) };
            double absB[3] = { aB.x + static_cast<double>(vertsB[idxB].pos[0]),
                aB.y + static_cast<double>(vertsB[idxB].pos[1]),
                aB.z + static_cast<double>(vertsB[idxB].pos[2]) };
            const double vbSeam = std::sqrt((absA[0] - absB[0]) * (absA[0] - absB[0])
                + (absA[1] - absB[1]) * (absA[1] - absB[1])
                + (absA[2] - absB[2]) * (absA[2] - absB[2]));
            vbAgg.Add(vbSeam);
            pairVb.Add(vbSeam);
            // (4) 渲染口径：t = float(anchor−cam)、viewPos = float(local + t)。
            //     聚合口径 = camEdge（本缘中点上方 2m，可见性口径）；600m/50km
            //     仅同面缘上下文（跨面缘在该相机位远超地平线，不可见）。
            auto seamAtCam = [&](const zplanet::Vec3d& cam) -> double {
                const float tA[3] = { static_cast<float>(aA.x - cam.x),
                    static_cast<float>(aA.y - cam.y), static_cast<float>(aA.z - cam.z) };
                const float tB[3] = { static_cast<float>(aB.x - cam.x),
                    static_cast<float>(aB.y - cam.y), static_cast<float>(aB.z - cam.z) };
                float viewA[3] = {};
                float viewB[3] = {};
                for (int c = 0; c < 3; ++c) {
                    viewA[c] = vertsA[idxA].pos[c] + tA[c]; // float32 加法（VS 口径）
                    viewB[c] = vertsB[idxB].pos[c] + tB[c];
                }
                const double dx = static_cast<double>(viewA[0]) - static_cast<double>(viewB[0]);
                const double dy = static_cast<double>(viewA[1]) - static_cast<double>(viewB[1]);
                const double dz = static_cast<double>(viewA[2]) - static_cast<double>(viewB[2]);
                return std::sqrt(dx * dx + dy * dy + dz * dz);
            };
            const double seamEdgeM = seamAtCam(camEdge);
            renderAgg.Add(seamEdgeM);
            pairRenderEdge.Add(seamEdgeM);
            if (spec.faceA == spec.faceB) {
                pairRender600.Add(seamAtCam(cams[0]));
                pairRender50k.Add(seamAtCam(cams[1]));
            }
        }
        MESSAGE("pair[" << std::string(spec.label) << " tier=" << spec.tier << " res="
                        << res << "] VB回读 max=" << pairVb.maxM * 1000.0 << "mm mean="
                        << pairVb.MeanM() * 1000.0 << "mm | 渲染口径(缘上2m) max="
                        << pairRenderEdge.maxM * 1000.0 << "mm mean="
                        << pairRenderEdge.MeanM() * 1000.0 << "mm");
        if (spec.faceA == spec.faceB) {
            MESSAGE("    上下文（同面缘）：600m max=" << pairRender600.maxM * 1000.0
                                                     << "mm 50km max="
                                                     << pairRender50k.maxM * 1000.0
                                                     << "mm（远处相机缝宽随 |anchor−cam| "
                                                        "增长，角分辨率恒 ~1e-7 rad 级"
                                                        "不可见；跨面缘在 600m 相机下"
                                                        "远超地平线，不测上下文）");
        }
    }

    // ---- 汇总与设计稿对照 --------------------------------------------------------
    const double ulp221kmM = zplanet::FloatUlpAt(221000.0); // 设计稿 §5.1 估算基准
    const double skirtFarM = 0.02 * 2.0 / (32.0 * 15.0) * (3.14159265358979323846 / 4.0)
        * zplanet::kPlanetRadiusM; // 生产 SkirtDropM(32, tier0) 实况（2%×格距）
    const double skirtNearM = 0.02 * 2.0 / (32.0 * 63.0) * (3.14159265358979323846 / 4.0)
        * zplanet::kPlanetRadiusM;
    MESSAGE("=== B-PLANET-08 实测汇总（seed=1337, cells=32）===");
    MESSAGE("ULP@221km=" << ulp221kmM * 1000.0 << "mm（设计稿 §5.1 估算基准，估算口径）");
    MESSAGE("每侧静态 float 化偏差 |ε| max=" << devMaxM * 1000.0 << "mm（理论上界 ULP/2="
                                            << ulp221kmM * 500.0 << "mm）");
    ReportSeamAggregate("VB 回读口径（无相机，|εA−εB|）", vbAgg);
    ReportSeamAggregate("渲染口径（缘上2m，可见性口径）", renderAgg);
    MESSAGE("生产裙边深（SkirtDropM=2%×格距实况）：far=" << skirtFarM << "m near=" << skirtNearM
                                                      << "m；对 VB 回读 max 裕度 far=×"
                                                      << skirtFarM / vbAgg.maxM << " near=×"
                                                      << skirtNearM / vbAgg.maxM
                                                      << "；对渲染口径 max 裕度 far=×"
                                                      << skirtFarM / renderAgg.maxM
                                                      << " near=×" << skirtNearM / renderAgg.maxM);
    MESSAGE("double 域逐位不变量复认 " << doubleInvariantChecks << " 顶点对全绿；布局/路径"
                                       << "逐位自证 " << replicaBitwiseChecks << " 顶点对全绿");

    // 断言（宽松上界防退化——实测数字进施工报告，不在此写死紧阈值）：
    REQUIRE(vbAgg.count == 240u); // 6 缘 × (16+16+64+64+16+64)
    REQUIRE(renderAgg.count == 240u);
    CHECK(devMaxM * 1000.0 < 10.0); // 理论 ULP/2 = 7.8125mm
    CHECK(vbAgg.maxM * 1000.0 < 25.0); // 理论 |εA−εB| ≤ sqrt(2)×ULP（双大分量合成）
    CHECK(renderAgg.maxM < 0.1); // 渲染口径（可见性相机）100mm 宽松上界
    // 空转防护：两次独立 float 舍入的测量不得恒零（恒零说明口径失效）
    CHECK(vbAgg.sumM > 0.0);
    CHECK(renderAgg.sumM > 0.0);
    ResetPlanetChannel();
}

TEST_CASE("planet channel: B-PLANET-08 跨档 T 型几何失配实测（double 域上下文）")
{
    // 上下文（裙边真正遮蔽的对象，与 float 缝 mm 级对照）：生产档界
    // 近(16,16) 东缘 ↔ 远(17,16) 西缘，u = 2·255/480−1 = 2·1071/2016−1。
    // 远缘第 k 顶点处「远缘顶点 vs 近缘折线（相邻近缘顶点弦线）」的三维偏差
    // = 两档折线的 T 型几何缝（float 之前、与相机无关的静态几何量）。
    // 63/15 = 4.2 非整数格距比（B-TECH-04：强制一致采样被否决的原因）。
    ResetPlanetChannel();
    SetTestWorld();
    constexpr uint32_t kSeed = 1337u;
    const zplanet::Vec3d center = { 0.0, -zplanet::kPlanetRadiusM, 0.0 };
    const int32_t strideF = 15;
    const int32_t strideN = 63;
    const int32_t totalF = 32 * strideF;
    const int32_t totalN = 32 * strideN;
    double tjMaxM = 0.0;
    double tjSumM = 0.0;
    for (int32_t k = 0; k <= strideF; ++k) {
        const int32_t gVf = 16 * strideF + k; // 远缘全局 v 索引（240..255）
        const zplanet::PlanetChunkVertex fv = zplanet::PlanetChunkVertexAtGlobal(
            center, 0, 17 * strideF, gVf, totalF, kSeed);
        const double gNCont = static_cast<double>(gVf) * 4.2; // 近缘连续参数
        const int32_t g0 = static_cast<int32_t>(gNCont);
        const double fr = gNCont - static_cast<double>(g0);
        const zplanet::PlanetChunkVertex n0 = zplanet::PlanetChunkVertexAtGlobal(
            center, 0, 16 * strideN + strideN, g0, totalN, kSeed);
        const zplanet::PlanetChunkVertex n1 = zplanet::PlanetChunkVertexAtGlobal(
            center, 0, 16 * strideN + strideN, g0 + 1, totalN, kSeed);
        const double ix = n0.posAbs.x + (n1.posAbs.x - n0.posAbs.x) * fr;
        const double iy = n0.posAbs.y + (n1.posAbs.y - n0.posAbs.y) * fr;
        const double iz = n0.posAbs.z + (n1.posAbs.z - n0.posAbs.z) * fr;
        const double dx = fv.posAbs.x - ix;
        const double dy = fv.posAbs.y - iy;
        const double dz = fv.posAbs.z - iz;
        const double devM = std::sqrt(dx * dx + dy * dy + dz * dz);
        if (devM > tjMaxM) {
            tjMaxM = devM;
        }
        tjSumM += devM;
    }
    MESSAGE("跨档 T 型几何失配（double 域，远缘顶点 vs 近缘弦线）：max=" << tjMaxM
                                                                      << "m mean="
                                                                      << tjSumM / 16.0 << "m");
    // 对照尺：float VB 缝（前用例）mm 级 << 本几何失配 m 级；生产裙边深
    // far=417m / near=99m（2%×格距）对本失配的覆盖裕度在日志裁决节对照。
    CHECK(tjMaxM > 0.0);
    ResetPlanetChannel();
}

TEST_CASE("planet channel: B-PLANET-08 neighborMask v1 行为定版锁定（校验+记录+几何零依赖）")
{
    // 裁决（TASK-B-PLANET-08，依据=任务日志测量节实测缝宽）：方案 A 维持——
    // 全缘裙边 + neighborMask v1 = 校验+记录、不进几何路径（设计稿 §5.2）。
    // 本用例锁定其可观测面：
    //   (a) 合法域 0..0xF 全遍历：ChunkCreate 接受 + 掩码值经测试钩子逐位读回
    //       （记录面）+ 账目恒等；
    //   (b) 高 4 位非法域全遍历：ChunkCreate / SetLod 一律 RANGE（校验面）；
    //   (c) SetLod 合法域全遍历：接受且记录更新（钩子读回）；
    //   (d) 几何零依赖：生成函数签名无掩码参数（构造性证据——白盒声明处注明）
    //       + 同格不同掩码两次创建均成功（v1 不去重契约不受掩码影响）。
    ResetPlanetChannel();
    SetTestWorld();

    for (uint32_t mask = 0u; mask <= 0xFu; ++mask) {
        ZrPlanetChunkDesc desc = MakeDesc(16u, 16u, 0u);
        desc.neighborMask = mask;
        uint64_t h = 0u;
        REQUIRE(ZR_Planet_ChunkCreate(&desc, &h) == ZR_PLANET_OK);
        CHECK(zr::unity_target::planet::TestNeighborMaskForUnitTests(h) == mask);
        RequireAccountInvariant();
        REQUIRE(ZR_Planet_ChunkDestroy(h) == ZR_PLANET_OK);
    }

    for (uint32_t bad = 0x10u; bad <= 0xF0u; bad += 0x10u) {
        ZrPlanetChunkDesc desc = MakeDesc(16u, 16u, 0u);
        desc.neighborMask = bad;
        uint64_t h = 0u;
        CHECK(ZR_Planet_ChunkCreate(&desc, &h) == ZR_PLANET_ERR_RANGE);
    }
    // 混合位（低 4 位合法 + 高 4 位非法）同样 RANGE——校验按掩码与 0xF0 相与
    ZrPlanetChunkDesc descMixed = MakeDesc(16u, 16u, 0u);
    descMixed.neighborMask = 0x11u;
    uint64_t hMixed = 0u;
    CHECK(ZR_Planet_ChunkCreate(&descMixed, &hMixed) == ZR_PLANET_ERR_RANGE);
    descMixed.neighborMask = 0xFFu;
    CHECK(ZR_Planet_ChunkCreate(&descMixed, &hMixed) == ZR_PLANET_ERR_RANGE);

    // (c) SetLod 路径：合法域全遍历接受 + 记录更新；非法域拒绝且不覆盖旧记录
    uint64_t h = 0u;
    ZrPlanetChunkDesc desc = MakeDesc(16u, 16u, 0u);
    REQUIRE(ZR_Planet_ChunkCreate(&desc, &h) == ZR_PLANET_OK);
    for (uint32_t mask = 0u; mask <= 0xFu; ++mask) {
        REQUIRE(ZR_Planet_SetLod(h, 0u, mask) == ZR_PLANET_OK);
        CHECK(zr::unity_target::planet::TestNeighborMaskForUnitTests(h) == mask);
    }
    REQUIRE(ZR_Planet_SetLod(h, 0u, 0x30u) == ZR_PLANET_ERR_RANGE);
    CHECK(zr::unity_target::planet::TestNeighborMaskForUnitTests(h) == 0xFu); // 旧记录保持
    // (d) 同格不同掩码不去重（v1 契约）
    uint64_t h1 = 0u;
    uint64_t h2 = 0u;
    ZrPlanetChunkDesc d1 = MakeDesc(3u, 4u, 0u);
    d1.neighborMask = 0u;
    ZrPlanetChunkDesc d2 = MakeDesc(3u, 4u, 0u);
    d2.neighborMask = 0xFu;
    REQUIRE(ZR_Planet_ChunkCreate(&d1, &h1) == ZR_PLANET_OK);
    REQUIRE(ZR_Planet_ChunkCreate(&d2, &h2) == ZR_PLANET_OK);
    CHECK(h1 != h2);
    CHECK(zr::unity_target::planet::TestNeighborMaskForUnitTests(h1) == 0u);
    CHECK(zr::unity_target::planet::TestNeighborMaskForUnitTests(h2) == 0xFu);
    RequireAccountInvariant();
    // 无效句柄读回 0（钩子自身的防护面）
    CHECK(zr::unity_target::planet::TestNeighborMaskForUnitTests(0u) == 0u);
    CHECK(zr::unity_target::planet::TestNeighborMaskForUnitTests(
              0xFFFFFFFFFFFFFFFFull)
        == 0u);
    ResetPlanetChannel();
}

namespace {
/// 创建 chunk 并推进到 LIVE（G4-R1 seam 核查直驱前置；返回句柄）
uint64_t CreateLiveChunk(uint32_t face, uint32_t cx, uint32_t cy, uint32_t tier)
{
    ZrPlanetChunkDesc desc = MakeDesc(cx, cy, tier);
    desc.faceId = face;
    uint64_t h = 0u;
    REQUIRE(ZR_Planet_ChunkCreate(&desc, &h) == ZR_PLANET_OK);
    return h;
}
} // namespace

TEST_CASE("planet channel: B-PLANET-08/G4-R1 seam 核查门行为锁定（远档跳过/近档仍核查）")
{
    // 主控裁决（B-PLANET-07 G4-R1 归入本任务）：远档共享边 memcmp 核查是每块
    // ~150µs 固定税（6144 全量外推 ≈0.92s 渲染线程），采纳 3 行门——tier 0 直接
    // 返回。正确性依据：远档共享边逐位一致由构造性路由保证（同全局整数索引 →
    // 同一 double 表达式；本文件测量探针 (0) 全缘全顶点复认）+ 裙边遮蔽。
    // 本用例锁定：远档核查零发生（计数不动）/近档仍核查（计数推进、无失配）/
    // 无邻居不核查。seamCheckFails 只反映失配，无法区分「查了全绿」与「没查」
    // ——故以 g_seamChecks 计数钩子为观测面。
    ResetPlanetChannel();
    SetTestWorld();

    // 近档相邻两块：直驱核查 → 计数 +1（东缘命中）、无失配
    (void)CreateLiveChunk(0u, 16u, 16u, 1u);
    (void)CreateLiveChunk(0u, 17u, 16u, 1u);
    REQUIRE(zr::unity_target::planet::TestPromoteUploadsForUnitTests() == 2u);
    const uint64_t checks0 = zr::unity_target::planet::TestSeamChecksForUnitTests();
    zr::unity_target::planet::TestSeamCheckNearForUnitTests(0u, 16u, 16u, 32u, 1u);
    CHECK(zr::unity_target::planet::TestSeamChecksForUnitTests() == checks0 + 1u);
    ZrPlanetStats stats {};
    REQUIRE(ZR_Planet_GetStats(&stats) == ZR_PLANET_OK);
    CHECK(stats.seamCheckFails == 0u); // 近档共享边逐位全绿（生产路径直驱复认）

    // 远档相邻两块（同格不同档共存）：门生效 → 核查计数不动
    (void)CreateLiveChunk(0u, 16u, 16u, 0u);
    (void)CreateLiveChunk(0u, 17u, 16u, 0u);
    REQUIRE(zr::unity_target::planet::TestPromoteUploadsForUnitTests() == 2u);
    const uint64_t checks1 = zr::unity_target::planet::TestSeamChecksForUnitTests();
    zr::unity_target::planet::TestSeamCheckNearForUnitTests(0u, 16u, 16u, 32u, 0u);
    zr::unity_target::planet::TestSeamCheckNearForUnitTests(0u, 17u, 16u, 32u, 0u);
    CHECK(zr::unity_target::planet::TestSeamChecksForUnitTests() == checks1);
    REQUIRE(ZR_Planet_GetStats(&stats) == ZR_PLANET_OK);
    CHECK(stats.seamCheckFails == 0u);

    // 近档孤块（四邻不在表）：findNeighbor 不命中 → 不核查
    (void)CreateLiveChunk(0u, 5u, 5u, 1u);
    REQUIRE(zr::unity_target::planet::TestPromoteUploadsForUnitTests() == 1u);
    const uint64_t checks2 = zr::unity_target::planet::TestSeamChecksForUnitTests();
    zr::unity_target::planet::TestSeamCheckNearForUnitTests(0u, 5u, 5u, 32u, 1u);
    CHECK(zr::unity_target::planet::TestSeamChecksForUnitTests() == checks2);
    RequireAccountInvariant();
    ResetPlanetChannel();
}

TEST_CASE("planet channel: B-PLANET-08/G4-R1 seam 核查固定税复测（门后相邻≈散布消除）")
{
    // 复测口径（主控指定，同 B-PLANET-07）：远档 256 块相邻 vs 散布、3 轮取最小。
    // B-PLANET-07 门前后对照：相邻 166.4ms / 散布 187.0ms（每块税 ~150µs 级）；
    // 加门后预期相邻≈散布≈0（核查整体消失）。近档（门不覆盖）同步实测作为
    // 「被消除量」的量级对照：相邻=表扫+逐顶点 double 重算，散布=仅表扫。
    // 直驱 SeamCheckNear（白盒）= 税的本体（该函数即税所在，上传路径逐块调它）。
    ResetPlanetChannel();
    SetTestWorld();

    // 远档相邻 256 = face0 16×16 格；远档散布 160 = face1..5 各 32（素数步长
    // 防碰撞防相邻）；近档相邻 64 = face0 [16,24)² 8×8 格；近档散布 60 =
    // face1..5 各 12（与远档散布同格共存，tier 过滤隔离）。
    std::vector<uint64_t> handles;
    handles.reserve(768u);
    for (uint32_t cy = 0u; cy < 16u; ++cy) {
        for (uint32_t cx = 0u; cx < 16u; ++cx) {
            handles.push_back(CreateLiveChunk(0u, cx, cy, 0u));
        }
    }
    for (uint32_t face = 1u; face <= 5u; ++face) {
        for (uint32_t i = 0u; i < 32u; ++i) {
            handles.push_back(CreateLiveChunk(
                face, (i * 7u) % 32u, (i * 11u) % 32u, 0u));
        }
    }
    for (uint32_t cy = 16u; cy < 24u; ++cy) {
        for (uint32_t cx = 16u; cx < 24u; ++cx) {
            handles.push_back(CreateLiveChunk(0u, cx, cy, 1u));
        }
    }
    for (uint32_t face = 1u; face <= 5u; ++face) {
        for (uint32_t i = 0u; i < 12u; ++i) {
            handles.push_back(CreateLiveChunk(
                face, (i * 7u) % 32u, (i * 11u) % 32u, 1u));
        }
    }
    REQUIRE(handles.size() == 256u + 160u + 64u + 60u);
    REQUIRE(zr::unity_target::planet::TestPromoteUploadsForUnitTests()
        == 256u + 160u + 64u + 60u);

    LARGE_INTEGER freq {};
    QueryPerformanceFrequency(&freq);
    auto timeCalls = [&](uint32_t face, uint32_t cx0, uint32_t cy0, uint32_t strideX,
                         uint32_t strideY, uint32_t tier, uint32_t count,
                         uint32_t faceStride) -> double {
        // 3 轮取最小（同主控口径）；返回 µs/call
        double bestUs = 1.0e30;
        for (uint32_t round = 0u; round < 3u; ++round) {
            LARGE_INTEGER t0 {};
            LARGE_INTEGER t1 {};
            QueryPerformanceCounter(&t0);
            for (uint32_t i = 0u; i < count; ++i) {
                const uint32_t cx = (cx0 + (i % strideX) * faceStride) % 32u;
                const uint32_t cy = (cy0 + ((i / strideX) % strideY) * faceStride) % 32u;
                zr::unity_target::planet::TestSeamCheckNearForUnitTests(face, cx, cy, 32u, tier);
            }
            QueryPerformanceCounter(&t1);
            const double us = static_cast<double>(t1.QuadPart - t0.QuadPart) * 1e6
                / static_cast<double>(freq.QuadPart) / static_cast<double>(count);
            if (us < bestUs) {
                bestUs = us;
            }
        }
        return bestUs;
    };

    // 远档相邻：face0 [0,16)×[0,16) 格逐块核查（stride 1 → 步进相邻）
    const double farAdjUs = timeCalls(0u, 0u, 0u, 16u, 16u, 0u, 256u, 1u);
    // 远档散布：face1..5 素数步长格（无邻居命中；face 维度逐块换）
    double farScatUs = 1.0e30;
    for (uint32_t round = 0u; round < 3u; ++round) {
        LARGE_INTEGER t0 {};
        LARGE_INTEGER t1 {};
        QueryPerformanceCounter(&t0);
        for (uint32_t face = 1u; face <= 5u; ++face) {
            for (uint32_t i = 0u; i < 32u; ++i) {
                zr::unity_target::planet::TestSeamCheckNearForUnitTests(
                    face, (i * 7u) % 32u, (i * 11u) % 32u, 32u, 0u);
            }
        }
        QueryPerformanceCounter(&t1);
        const double us = static_cast<double>(t1.QuadPart - t0.QuadPart) * 1e6
            / static_cast<double>(freq.QuadPart) / 160.0;
        if (us < farScatUs) {
            farScatUs = us;
        }
    }
    // 近档相邻：face0 [16,24)² 8×8 格（stride 1 → 相邻；门不覆盖的被消除量）
    const double nearAdjUs = timeCalls(0u, 16u, 16u, 8u, 8u, 1u, 64u, 1u);
    // 近档散布：face1..5 素数步长（仅表扫——税中邻居扫描分量的对照）
    double nearScatUs = 1.0e30;
    for (uint32_t round = 0u; round < 3u; ++round) {
        LARGE_INTEGER t0 {};
        LARGE_INTEGER t1 {};
        QueryPerformanceCounter(&t0);
        for (uint32_t face = 1u; face <= 5u; ++face) {
            for (uint32_t i = 0u; i < 12u; ++i) {
                zr::unity_target::planet::TestSeamCheckNearForUnitTests(
                    face, (i * 7u) % 32u, (i * 11u) % 32u, 32u, 1u);
            }
        }
        QueryPerformanceCounter(&t1);
        const double us = static_cast<double>(t1.QuadPart - t0.QuadPart) * 1e6
            / static_cast<double>(freq.QuadPart) / 60.0;
        if (us < nearScatUs) {
            nearScatUs = us;
        }
    }

    MESSAGE("=== G4-R1 seam 核查固定税复测（3 轮取最小，µs/块）===");
    MESSAGE("远档相邻=" << farAdjUs << " 远档散布=" << farScatUs
                        << "（门后均应≈0，相邻≈散布）");
    MESSAGE("近档相邻=" << nearAdjUs << " 近档散布=" << nearScatUs
                        << "（门不覆盖——被消除量的量级对照；相邻−散布≈逐顶点重算分量）");
    MESSAGE("外推：远档 6144 全量门后核查开销 ≈" << farAdjUs * 6144.0 / 1000.0
                                                << "ms（门前 B-PLANET-07 实测 ≈920ms 级）");
    // 门效应断言：远档（门后）必须显著低于近档（未门）——量级差 ≥10× 即为门在
    //（实测预期 ≥100×；时序断言取宽界防 CI 噪声）
    CHECK(farAdjUs < nearAdjUs / 10.0);
    CHECK(farScatUs < nearScatUs / 10.0);
    // 核查全程零失配
    ZrPlanetStats stats {};
    REQUIRE(ZR_Planet_GetStats(&stats) == ZR_PLANET_OK);
    CHECK(stats.seamCheckFails == 0u);
    RequireAccountInvariant();
    ResetPlanetChannel();
}
