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
