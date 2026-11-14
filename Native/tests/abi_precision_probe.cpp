// =============================================================================
// abi_precision_probe.cpp —— 四点×双口径 double→float 提交精度实测探针
//（TASK-B-PLANET-10）
//
// 回答一个问题：行星大坐标从 double 绝对域提交到 float 渲染域，四档相机-目标
// 距离（100m/1km/10km/100km）下每个环节真实丢多少米 / 多少 ULP——第三人可复核、
// 第二台机器可复跑（纯 CPU + dll 导出面路径，无 GPU 依赖）。
//
// 双口径（设计稿 P2-行星ABI设计.md §9/§11 B-PLANET-10；改进计划 §5）：
//   口径 A（纯数学）：double 绝对坐标 → 相机相对 float 的位置损失（m+ULP 双单位）。
//     - correct = float(objAbs − camAbs)（double 域相减后一次 cast——重定位正确
//       路径，planet_math.h FloatingOrigin::RenderOffsetF 即生产同式）
//     - naive@planet-frame = float(objAbs) − float(camAbs)（全 float 大坐标直接
//       相减——RenderOffsetNaiveF；本探针几何下坐标幅值 ~6.37e6 m）
//     - naive@origin-frame（A-WORLD-01 / JitterAudit 口径）：相机自原点飞 D、
//       近场物体恒在前方 10.37m——naive 抖动 vs 重定位后，两口径并记（§9
//       "10km 两口径并记"先例；A-WORLD-01 7.8mm@100km 锚点即此口径）
//   口径 B（ABI 全链）：真实 SetCameraOrigin + chunk 提交路径的端到端顶点位置
//     损失。生产链每一步都实测：
//     1) zrender.dll（LoadLibrary 生产导出面——--planet3/--pbudget 同策）：
//        ZR_Planet_SetWorld → ZR_Planet_ChunkCreate（校验链 + 账目）→
//        ZR_Unity_SetCameraOrigin → ZR_Unity_GetCameraOrigin（round-trip 逐位）
//        → ZR_Planet_SampleHeight（§9 表第 10 环）
//     2) 顶点字节 = 生产 GenerateChunkVerts 本体（zrhi 静态库白盒钩子
//        TestGenerateChunkVertsForUnitTests——ChunkCreate 内部同函数，零复制
//        零漂移，B-PLANET-08 测量探针同策；喂上下文的 SetWorld 走静态 zplugin
//        同源实现——与 dll 各自独立实例，互不触碰）
//     3) 模型平移 t = float(anchorAbs − camOrigin)（unity_target.cpp 逐绘 CB
//        口径：double 域相减后一次 cast）；viewPos = local + t（float32 加法，
//        planet2.hlsl VS mul(model,·) 口径）；真值 = posAbs − camOrigin
//        （double 域度量，B-PLANET-08 (4) 同式）
//
// §9 预算表逐环节对照（JSON budget9 节——数据不是断言，达标/超出都如实落盘）：
//   row1/2/8/9 非本探针口径（托管累计 / 重定位触发 / 托管 view / GPU 光栅）——
//   写明归属不伪造数字；row3/4/5/7/10 本探针实测；row6 归 B-PLANET-08 存档。
//
// 几何口径：seed=1337、中心 (0,−R,0)、face 0 chunk (16,16) 近档（res 64）——
// 与 test_planet_channel / --planet3 / planet-budget 同构图同种子。chunk 锚点 =
// 面内中心格顶点（stride/2=31，即 8192 顶点网格真实顶点——local(anchor) 恒 0，
// 锚点路径自身零损失自证）；相机 = 锚点沿球面法向悬停 D（|target−cam| = D）。
// 目标顶点分两档报告：锚点顶点（平移量化本体，§9 表第 7 环四点验证）与全块
// 4352 顶点最大值（含 221km 级 chunk 角点——顶点局部 float 化上界，§9 第 5 环；
// 提交口径不含可见性剔除，角点在近场不可见——如实标注）。
//
// JSON 落盘口径（照 fsr4_probe / env-probe.js——Mimosa 复查口径）：文件名白名单
// ^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)+$（天然不含路径分隔符与连续点）+ 最终路径
// 必须以常量 out 根为前缀的边界断言；不拼接任何未校验路径；拿不到的项一律
// MISSING+原因，不填默认值。
//
// 退出码：0 = 探针完整跑完且 JSON 已落盘（dll 缺失是数据不是失败——口径 A 照常
//         采集，口径 B 按 MISSING 记录）；1 = JSON 未写出（out 不可建/不可写）。
//
// 边界（约法三章）：探针不进 zrender.dll、不进 Unity 绑定、不进 zrender_test
// （精度是测量不是过/不过——设计稿口径，380 用例零改动）；dll 只读消费
// （ChunkCreate 一次 + Destroy/ClearAll 兜底，无 RenderFrame——pendingUpload
// 永不升 GPU，零 D3D12 设备依赖）。
// =============================================================================

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <limits>
#include <string>
#include <vector>

#include "zplanet/planet_math.h" // FloatUlpAt / FloatingOrigin::RenderOffsetF /
                                 // JitterAudit / PlanetChunkVertexAtGlobal（生产头，header-only）
#include "zplugin/zplugin.h"     // 行星 ABI 类型 / 错误码 / 函数指针 typedef（声明 only）

// 生产符号白盒（zrhi 静态库直连——B-PLANET-08 测量探针同策：被调方是生产
// GenerateChunkVerts 本体，取回的就是 memcpy 进 GPU VB 的字节）。
namespace zr {
namespace unity_target {
namespace planet {
uint32_t TestGenerateChunkVertsForUnitTests(uint32_t faceId, uint32_t cx, uint32_t cy,
    uint32_t cellsPerEdge, uint32_t tier, const double originAbs[3],
    std::vector<uint8_t>& out);
} // namespace planet
} // namespace unity_target
} // namespace zr

namespace {

// ---- dll 导出面函数指针（zplugin.h 已有行星组 typedef；版本组本探针本地声明）----
typedef uint32_t (*ZrApiVersionFn)(void);
typedef const char* (*ZrGetVersionFn)(void);

// ---- 生产顶点布局镜像（unity_target.cpp "v1 固定顶点布局" 32B；B-PLANET-08 同款）--
struct ProbeVertex {
    float pos[3];
    float normal[3];
    float uv[2];
};
static_assert(sizeof(ProbeVertex) == 32, "ProbeVertex must mirror TargetVertex (32 bytes)");

// unity_target.cpp:51 同值同型（float kPi——ChunkArcM/SkirtDropM 的位精确复刻）。
// 裙边真值必须与生产逐位同式，否则 (1) 自证会在裙边顶点出现假失配。
constexpr float kPiF = 3.14159265358979f;

// ---- 小工具：JSON 转义 / 时间戳 / 数值 -----------------------------------------

std::string JsonEscape(const std::string& in)
{
    std::string out;
    out.reserve(in.size() + 8);
    for (const char c : in) {
        switch (c) {
        case '"': out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\b': out += "\\b"; break;
        case '\f': out += "\\f"; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        default:
            if (static_cast<unsigned char>(c) < 0x20u) {
                char buf[8] = {};
                std::snprintf(buf, sizeof(buf), "\\u%04X",
                    static_cast<unsigned>(static_cast<unsigned char>(c)));
                out += buf;
            } else {
                out += c;
            }
        }
    }
    return out;
}

// %.17g：double 往返可复现（第三人重跑数字逐位一致的前提）
std::string JsonNum(const double v)
{
    char buf[40] = {};
    std::snprintf(buf, sizeof(buf), "%.17g", v);
    return std::string(buf);
}

std::string UtcIso()
{
    std::time_t now = std::time(nullptr);
    std::tm utc {};
    ::gmtime_s(&utc, &now);
    char buf[32] = {};
    std::strftime(buf, sizeof(buf), "%Y-%m-%dT%H:%M:%SZ", &utc);
    return std::string(buf);
}

std::string UtcStampFile()
{
    // 与 env-probe.js / fsr4_probe 同口径：ISO 串的 ':' '.' → '-'，截 19 字符
    std::time_t now = std::time(nullptr);
    std::tm utc {};
    ::gmtime_s(&utc, &now);
    char buf[32] = {};
    std::strftime(buf, sizeof(buf), "%Y-%m-%dT%H-%M-%S", &utc);
    return std::string(buf);
}

/// 等价于 env-probe.js 的 /^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)+$/：仅字母/数字/
/// _/- 分段 + 单点分隔（≥1 个点），天然不含路径分隔符与连续点，杜绝 ../ 与
/// 分隔符注入；再对最终路径做 out 目录边界断言（Mimosa 复查口径）。
bool IsValidProbeFileName(const std::string& name)
{
    if (name.empty()) {
        return false;
    }
    bool inSegment = false;
    bool sawDot = false;
    for (const char c : name) {
        if (c == '.') {
            if (!inSegment) {
                return false; // 连续点/以点开头（含 ".."）→ 拒绝
            }
            sawDot = true;
            inSegment = false;
        } else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
            || (c >= '0' && c <= '9') || c == '_' || c == '-') {
            inSegment = true;
        } else {
            return false; // 非白名单字符（含 / \ : 等）→ 拒绝
        }
    }
    return inSegment && sawDot;
}

// ---- 输出目录解析（照 fsr4_probe：仓库根标记 = Tools/Probes/README.md 登记表）--

struct OutLocation {
    std::string mode;    // "repo-root" | "portable-exe-dir"
    std::string outDir;  // 常量 out 根（独立于文件名计算——边界断言基准）
    std::string exeDir;
};

OutLocation ResolveOutLocation()
{
    char exePathA[MAX_PATH] = {};
    ::GetModuleFileNameA(nullptr, exePathA, MAX_PATH);
    std::string exeDir(exePathA);
    const size_t slash = exeDir.find_last_of("\\/");
    if (slash != std::string::npos) {
        exeDir = exeDir.substr(0, slash);
    }

    std::string cursor = exeDir;
    for (int depth = 0; depth < 8; ++depth) {
        const std::string marker = cursor + "\\Tools\\Probes\\README.md";
        if (::GetFileAttributesA(marker.c_str()) != INVALID_FILE_ATTRIBUTES) {
            return {"repo-root", cursor + "\\Tools\\Probes\\out", exeDir};
        }
        const size_t parentSlash = cursor.find_last_of("\\/");
        if (parentSlash == std::string::npos || parentSlash < 2) {
            break; // 已到盘根
        }
        cursor = cursor.substr(0, parentSlash);
    }
    // 免仓库拷贝模式：out 目录落在 exe 同目录（便携档，随 exe 走）
    return {"portable-exe-dir", exeDir + "\\out", exeDir};
}

struct MissingItem {
    std::string name;
    std::string reason;
};

// ---- 精度量小件 ---------------------------------------------------------------

/// 三分量最大绝对误差（double 域度量——B-PLANET-08 (4) 同式）
double MaxComponentLoss(const double rendered[3], const double truth[3])
{
    double loss = 0.0;
    for (int c = 0; c < 3; ++c) {
        const double dev = std::fabs(rendered[c] - truth[c]);
        if (dev > loss) {
            loss = dev;
        }
    }
    return loss;
}

/// 三维欧氏误差（double 域）
double EuclidLoss(const double rendered[3], const double truth[3])
{
    double sum2 = 0.0;
    for (int c = 0; c < 3; ++c) {
        const double d = rendered[c] - truth[c];
        sum2 += d * d;
    }
    return std::sqrt(sum2);
}

/// 角偏差像素当量 @1080p / 水平 FOV 60°（设计稿 §9 表第 7 行锚点口径：
/// 0.488mm@600m ≈ 0.0015px → f_px = 960/tan(30°) = 1662.77）
double AngularPx1080p(const double lossM, const double distM)
{
    if (!(distM > 0.0)) {
        return 0.0;
    }
    constexpr double kFocalPx1080pHfov60 = 960.0 / 0.57735026918962576451; // tan(30°)
    return (lossM / distM) * kFocalPx1080pHfov60;
}

} // namespace

// =============================================================================
// main
// =============================================================================

int main()
{
    const std::string probedAt = UtcIso();
    const std::string stampFile = UtcStampFile();

    std::printf("======================================================================\n");
    std::printf(" abi-precision  (TASK-B-PLANET-10)  %s\n", probedAt.c_str());
    std::printf(" 四点×双口径 double→float 提交精度实测"
                "（口径A 纯数学 / 口径B ABI 全链）\n");
    std::printf("======================================================================\n");

    std::vector<MissingItem> missing;

    // ---- 输出目录（常量根，独立于文件名计算）--------------------------------
    const OutLocation out = ResolveOutLocation();

    // ---- 主机信息 -----------------------------------------------------------
    constexpr DWORD kHostNameBufChars = 64;
    char hostNameA[kHostNameBufChars] = {};
    DWORD hostNameLen = kHostNameBufChars;
    const bool hostNameOk = ::GetComputerNameA(hostNameA, &hostNameLen) != FALSE;

    char exePathA[MAX_PATH] = {};
    ::GetModuleFileNameA(nullptr, exePathA, MAX_PATH);
    const std::string exePath(exePathA);

    std::printf("host.computerName : %s\n", hostNameOk ? hostNameA : "(取不到)");
    std::printf("host.exe          : %s\n", exePath.c_str());
    std::printf("out.mode          : %s -> %s\n", out.mode.c_str(), out.outDir.c_str());

    // ---- 几何常量（与 test_planet_channel / --planet3 / planet-budget 同构图）--
    constexpr uint32_t kSeed = 1337u;
    constexpr uint32_t kFace = 0u;
    constexpr uint32_t kCx = 16u;
    constexpr uint32_t kCy = 16u;
    constexpr uint32_t kCells = 32u;
    constexpr uint32_t kLod = 1u; // 近档（tier 1 → res 64；B-TECH-04 两档映射）
    constexpr uint32_t kRes = 64u;
    constexpr int32_t kStride = 63;
    constexpr int32_t kTotal = static_cast<int32_t>(kCells) * kStride; // 2016
    constexpr uint32_t kGridVerts = kRes * kRes;                       // 4096
    constexpr uint32_t kSkirtVerts = 4u * kRes;                        // 256
    constexpr uint32_t kAllVerts = kGridVerts + kSkirtVerts;           // 4352
    constexpr double kDistances[4] = {100.0, 1000.0, 10000.0, 100000.0};
    constexpr int kDistCount = 4;
    constexpr uint32_t kCenterIdx = 31u * kRes + 31u; // 锚点=中心格顶点（stride/2=31）

    const zplanet::Vec3d center = {0.0, -zplanet::kPlanetRadiusM, 0.0};

    // chunk 锚点 = 面内中心格顶点（生产建议口径 = chunk 中心球面点；取近档中心
    // 格顶点使其同时是一个真实网格顶点——local(anchor) 恒 0 的构造性自证）
    const int32_t anchorIdx = zplanet::ChunkVertexIndex(
        static_cast<int32_t>(kCx), kStride / 2, kStride);
    const zplanet::PlanetChunkVertex anchorV = zplanet::PlanetChunkVertexAtGlobal(
        center, static_cast<int>(kFace), anchorIdx, anchorIdx, kTotal, kSeed);
    const zplanet::Vec3d anchor = anchorV.posAbs;
    const double anchorArr[3] = {anchor.x, anchor.y, anchor.z};

    // 裙边深位精确复刻（SkirtDropM(32,1) = 0.02×ChunkArcM(32,1)；kPi float 同型）
    const double cellArcM = 2.0 / static_cast<double>(kTotal)
        * (static_cast<double>(kPiF) / 4.0) * zplanet::kPlanetRadiusM;
    const double skirtDropM = 0.02 * cellArcM;
    const double chunkArcM = cellArcM * static_cast<double>(kStride);

    std::printf("setup.seed          : %u  face=%u chunk=(%u,%u) cells=%u lod=%u(near res=%u)\n",
        static_cast<unsigned>(kSeed), static_cast<unsigned>(kFace),
        static_cast<unsigned>(kCx), static_cast<unsigned>(kCy),
        static_cast<unsigned>(kCells), static_cast<unsigned>(kLod),
        static_cast<unsigned>(kRes));
    std::printf("setup.anchor        : (%.17g, %.17g, %.17g) m\n", anchor.x, anchor.y,
        anchor.z);
    std::printf("setup.chunkArc      : %.6g m  cellArc=%.6g m  skirt=%.6g m\n", chunkArcM,
        cellArcM, skirtDropM);

    // ---- 口径 B 第 2 步：生产顶点字节（白盒钩子；静态 SetWorld 喂上下文）------
    //
    // 静态链接的 ZR_Planet_SetWorld（zplugin 静态库同源实现）只服务本进程内静态
    // zrhi 的 GenerateChunkVerts（g_seed/g_center 上下文）——与 dll 内实例互不
    // 触碰；dll 侧 SetWorld 在下方经导出面单独调用。
    ZrPlanetWorld world {};
    world.structSize = static_cast<uint32_t>(sizeof(ZrPlanetWorld));
    world.seed = kSeed;
    world.centerAbs[0] = center.x;
    world.centerAbs[1] = center.y;
    world.centerAbs[2] = center.z;
    const int staticSetWorldRc = ZR_Planet_SetWorld(&world); // 静态同源（非 dll）

    std::vector<uint8_t> vertBytes;
    std::vector<ProbeVertex> verts;
    std::vector<zplanet::Vec3d> posAbs; // 每顶点 double 真值（绝对坐标）
    uint32_t vertCount = 0u;
    bool vertsOk = false;
    if (staticSetWorldRc != ZR_PLANET_OK) {
        missing.push_back({"vertexBytes.staticSetWorld",
            "静态 ZR_Planet_SetWorld rc=" + std::to_string(staticSetWorldRc)
                + "（预期 0）——顶点字节与 §9 row5 自证按 MISSING 记录"});
    } else {
        vertCount = zr::unity_target::planet::TestGenerateChunkVertsForUnitTests(
            kFace, kCx, kCy, kCells, kLod, anchorArr, vertBytes);
        if (vertCount != kAllVerts
            || vertBytes.size() != static_cast<size_t>(vertCount) * 32u) {
            missing.push_back({"vertexBytes.generate",
                "生产钩子返回 " + std::to_string(vertCount) + " 顶点 / "
                    + std::to_string(vertBytes.size())
                    + " 字节（预期 4352 / 139264）——顶点量测按 MISSING 记录"});
        } else {
            verts.resize(vertCount);
            std::memcpy(verts.data(), vertBytes.data(), vertBytes.size());
            posAbs.resize(vertCount);
            // (a) 网格顶点真值：与生产同一规范路由 PlanetChunkVertexAtGlobal（逐位同源）
            for (uint32_t j = 0; j < kRes; ++j) {
                for (uint32_t i = 0; i < kRes; ++i) {
                    const int32_t gU = zplanet::ChunkVertexIndex(
                        static_cast<int32_t>(kCx), static_cast<int32_t>(i), kStride);
                    const int32_t gV = zplanet::ChunkVertexIndex(
                        static_cast<int32_t>(kCy), static_cast<int32_t>(j), kStride);
                    const zplanet::PlanetChunkVertex v = zplanet::PlanetChunkVertexAtGlobal(
                        center, static_cast<int>(kFace), gU, gV, kTotal, kSeed);
                    posAbs[j * kRes + i] = v.posAbs;
                }
            }
            // (b) 裙边顶点真值：生产同式（posAbs − normal·skirtDrop，双域 double）
            for (uint32_t e = 0; e < 4u; ++e) {
                for (uint32_t s = 0; s < kRes; ++s) {
                    const uint32_t i
                        = (e >= 2u) ? ((e == 2u) ? 0u : (kRes - 1u)) : s;
                    const uint32_t j
                        = (e >= 2u) ? s : ((e == 0u) ? 0u : (kRes - 1u));
                    const int32_t gU = zplanet::ChunkVertexIndex(
                        static_cast<int32_t>(kCx), static_cast<int32_t>(i), kStride);
                    const int32_t gV = zplanet::ChunkVertexIndex(
                        static_cast<int32_t>(kCy), static_cast<int32_t>(j), kStride);
                    const zplanet::PlanetChunkVertex v =
                        zplanet::PlanetChunkVertexAtGlobal(
                            center, static_cast<int>(kFace), gU, gV, kTotal, kSeed);
                    const zplanet::Vec3d dropped = zplanet::VecSub(v.posAbs,
                        zplanet::VecScale(v.normal, skirtDropM));
                    posAbs[kGridVerts + e * kRes + s] = dropped;
                }
            }
            vertsOk = true;
        }
    }

    // ---- 顶点自证 + §9 row5（顶点局部 float 化偏差——与距离无关，建块一次）-----
    bool centerLocalAllZero = false;
    uint32_t gridBitwiseMatches = 0u;
    uint32_t skirtToleranceMatches = 0u;
    double devMaxM = 0.0;   // row5 实测：max|double(local) − (posAbs−anchor)|
    double maxLocalM = 0.0; // max|posAbs − anchor|（chunk 角点幅值——ULP 界的幅值）
    if (vertsOk) {
        // 锚点顶点 local == (0,0,0) 逐位（构造性：anchor 即该顶点 posAbs）
        centerLocalAllZero = verts[kCenterIdx].pos[0] == 0.0f
            && verts[kCenterIdx].pos[1] == 0.0f && verts[kCenterIdx].pos[2] == 0.0f;
        for (uint32_t k = 0; k < vertCount; ++k) {
            const zplanet::Vec3d rel = zplanet::VecSub(posAbs[k], anchor);
            const double localMag = zplanet::VecLen(rel);
            if (localMag > maxLocalM) {
                maxLocalM = localMag;
            }
            double devV = 0.0;
            bool bitwise = true;
            for (int c = 0; c < 3; ++c) {
                const float expect = static_cast<float>(
                    (c == 0) ? rel.x : ((c == 1) ? rel.y : rel.z));
                if (expect != verts[k].pos[c]) {
                    bitwise = false;
                }
                const double dev = std::fabs(static_cast<double>(verts[k].pos[c])
                    - ((c == 0) ? rel.x : ((c == 1) ? rel.y : rel.z)));
                if (dev > devV) {
                    devV = dev;
                }
            }
            if (devV > devMaxM) {
                devMaxM = devV;
            }
            if (k < kGridVerts) {
                if (bitwise) {
                    ++gridBitwiseMatches;
                }
            } else {
                // 裙边容差口径（≤1 float ULP@|local|——kPi float 常量位级差异的
                // 容忍域；网格顶点仍逐位）
                const double tol = zplanet::FloatUlpAt(localMag) * 2.0 + 1e-12;
                if (devV <= tol) {
                    ++skirtToleranceMatches;
                }
            }
        }
    }
    const double ulpAtMaxLocal = zplanet::FloatUlpAt(maxLocalM);
    std::printf("verts               : %u 顶点（64^2+4x64）  local(anchor)=0 逐位=%s\n",
        static_cast<unsigned>(vertCount), centerLocalAllZero ? "YES" : "NO");
    std::printf("row5 顶点局部float化 : dev max=%.6g m  |local|max=%.6g m  ULP(|local|)=%.6g m\n",
        devMaxM, maxLocalM, ulpAtMaxLocal);
    std::printf("    grid 逐位 %u/%u  skirt 容差 %u/%u\n",
        static_cast<unsigned>(gridBitwiseMatches), static_cast<unsigned>(kGridVerts),
        static_cast<unsigned>(skirtToleranceMatches),
        static_cast<unsigned>(kSkirtVerts));
    if (!vertsOk) {
        std::printf("    MISSING（顶点字节不可得，见 missing）\n");
    }

    // ---- ULP 参照实测（FloatUlpAt——§9 理论对照值的实测校准）-------------------
    std::printf("ulp.FloatUlpAt      : 100m=%.6g  1km=%.6g  10km=%.6g  100km=%.6g m\n",
        zplanet::FloatUlpAt(100.0), zplanet::FloatUlpAt(1000.0),
        zplanet::FloatUlpAt(10000.0), zplanet::FloatUlpAt(100000.0));

    // ---- dll 导出面（口径 B 第 1 步：LoadLibrary 生产 dll——--planet3 同策）----
    HMODULE dll = ::LoadLibraryA("zrender.dll");
    const bool dllOk = (dll != nullptr);
    char dllPathA[MAX_PATH] = {};
    if (dllOk) {
        ::GetModuleFileNameA(dll, dllPathA, MAX_PATH);
    } else {
        missing.push_back({"dll.load",
            "LoadLibraryA(\"zrender.dll\") 失败（err="
                + std::to_string(::GetLastError())
                + "）——口径 B 全链与 SampleHeight 按 MISSING 记录；"
                  "在 Native/build/bin/Release 下运行可找到生产 dll"});
    }

    uint32_t dllApiVersion = 0u;
    std::string dllVersion;
    ZrUnitySetCameraOriginFn fnSetCamOrigin = nullptr;
    ZrUnityGetCameraOriginFn fnGetCamOrigin = nullptr;
    ZrPlanetSetWorldFn fnSetWorld = nullptr;
    ZrPlanetChunkCreateFn fnChunkCreate = nullptr;
    ZrPlanetChunkDestroyFn fnChunkDestroy = nullptr;
    ZrPlanetGetStatsFn fnGetStats = nullptr;
    ZrPlanetClearAllFn fnClearAll = nullptr;
    ZrPlanetSampleHeightFn fnSampleHeight = nullptr;
    bool abiOk = false;
    if (dllOk) {
        const auto fnApiVersion = reinterpret_cast<ZrApiVersionFn>(
            ::GetProcAddress(dll, "ZR_ApiVersion"));
        const auto fnGetVersion = reinterpret_cast<ZrGetVersionFn>(
            ::GetProcAddress(dll, "ZR_GetVersion"));
        fnSetCamOrigin = reinterpret_cast<ZrUnitySetCameraOriginFn>(
            ::GetProcAddress(dll, "ZR_Unity_SetCameraOrigin"));
        fnGetCamOrigin = reinterpret_cast<ZrUnityGetCameraOriginFn>(
            ::GetProcAddress(dll, "ZR_Unity_GetCameraOrigin"));
        fnSetWorld = reinterpret_cast<ZrPlanetSetWorldFn>(
            ::GetProcAddress(dll, "ZR_Planet_SetWorld"));
        fnChunkCreate = reinterpret_cast<ZrPlanetChunkCreateFn>(
            ::GetProcAddress(dll, "ZR_Planet_ChunkCreate"));
        fnChunkDestroy = reinterpret_cast<ZrPlanetChunkDestroyFn>(
            ::GetProcAddress(dll, "ZR_Planet_ChunkDestroy"));
        fnGetStats = reinterpret_cast<ZrPlanetGetStatsFn>(
            ::GetProcAddress(dll, "ZR_Planet_GetStats"));
        fnClearAll = reinterpret_cast<ZrPlanetClearAllFn>(
            ::GetProcAddress(dll, "ZR_Planet_ClearAll"));
        fnSampleHeight = reinterpret_cast<ZrPlanetSampleHeightFn>(
            ::GetProcAddress(dll, "ZR_Planet_SampleHeight"));
        dllApiVersion = (fnApiVersion != nullptr) ? fnApiVersion() : 0u;
        dllVersion = (fnGetVersion != nullptr && fnGetVersion() != nullptr)
            ? std::string(fnGetVersion())
            : std::string();
        const void* fns[8] = {fnSetCamOrigin, fnGetCamOrigin, fnSetWorld, fnChunkCreate,
            fnChunkDestroy, fnGetStats, fnClearAll, fnSampleHeight};
        bool allResolved = true;
        for (const void* f : fns) {
            if (f == nullptr) {
                allResolved = false;
            }
        }
        abiOk = allResolved;
        if (!allResolved) {
            missing.push_back({"dllGetProcAddress",
                "GetProcAddress 缺口（行星/相机导出面不全）——口径 B 按 MISSING 记录"});
        }
        std::printf("dll                 : ApiVersion=%u  version=%s\n",
            static_cast<unsigned>(dllApiVersion), dllVersion.c_str());
        std::printf("    %s\n", dllPathA);
    } else {
        std::printf("dll                 : MISSING（LoadLibrary 失败，见 missing）\n");
    }
    if (abiOk && dllApiVersion != 3u) {
        missing.push_back({"dll.apiVersion",
            "dll ZR_ApiVersion=" + std::to_string(dllApiVersion)
                + "（预期 3——设计稿 §11 B-PLANET-02 v3 口径）；口径 B 数字仍采集，"
                  "版本错位如实登记"});
    }

    // ---- dll 侧行星世界 + chunk 提交（校验链 + 账目；无 RenderFrame 不触 GPU）--
    int dllSetWorldRc = 0;
    int dllCreateRc = 0;
    uint64_t chunkHandle = 0u;
    bool accountInvariant = false;
    ZrPlanetStats stats {};
    if (abiOk) {
        ZrPlanetWorld dllWorld = world; // 同布局（structSize 已填）
        dllSetWorldRc = fnSetWorld(&dllWorld);
        if (dllSetWorldRc != ZR_PLANET_OK) {
            missing.push_back({"abi.setWorld",
                "dll ZR_Planet_SetWorld rc=" + std::to_string(dllSetWorldRc)
                    + "（预期 0）——口径 B 提交链按 MISSING 记录"});
            abiOk = false;
        }
    }
    if (abiOk) {
        ZrPlanetChunkDesc desc {};
        desc.structSize = static_cast<uint32_t>(sizeof(ZrPlanetChunkDesc));
        desc.faceId = kFace;
        desc.lodLevel = kLod;
        desc.cx = kCx;
        desc.cy = kCy;
        desc.cellsPerEdge = kCells;
        desc.neighborMask = 0u;
        desc.reserved0 = 0u;
        desc.originAbs[0] = anchor.x;
        desc.originAbs[1] = anchor.y;
        desc.originAbs[2] = anchor.z;
        dllCreateRc = fnChunkCreate(&desc, &chunkHandle);
        if (dllCreateRc != ZR_PLANET_OK || chunkHandle == 0u) {
            missing.push_back({"abi.chunkCreate",
                "dll ZR_Planet_ChunkCreate rc=" + std::to_string(dllCreateRc)
                    + " handle=" + std::to_string(static_cast<unsigned long long>(chunkHandle))
                    + "——口径 B 提交链按 MISSING 记录"});
            abiOk = false;
        } else {
            std::memset(&stats, 0, sizeof(stats));
            if (fnGetStats(&stats) != ZR_PLANET_OK) {
                missing.push_back({"abi.getStats", "dll ZR_Planet_GetStats 失败"});
            }
            // 账目恒等式（§7.3②）：createTotal − destroyTotal == liveChunks
            accountInvariant = (stats.createTotal - stats.destroyTotal)
                == static_cast<uint64_t>(stats.liveChunks);
            std::printf("abi.chunkCreate     : handle=0x%llX  live=%u pendingUp=%u "
                        "create=%llu account=%s\n",
                static_cast<unsigned long long>(chunkHandle),
                static_cast<unsigned>(stats.liveChunks),
                static_cast<unsigned>(stats.pendingUploads),
                static_cast<unsigned long long>(stats.createTotal),
                accountInvariant ? "OK" : "BROKEN");
        }
    }

    // ---- 四点循环：口径 A（纯数学）+ 口径 B（ABI 全链）--------------------------
    //
    // 相机 = 锚点沿球面法向悬停 D（|target−cam| = D；target = 锚点顶点）。
    // 口径 A 与口径 B 用同一对 (target, camera)——表内同距离可直接对照。
    struct DistRow {
        double d = 0.0;
        double actualDistM = 0.0;
        double ulpAtD = 0.0;
        // 口径 A
        double aCorrectMaxM = 0.0;
        double aCorrectEuclidM = 0.0;
        double aNaivePlanetMaxM = 0.0;
        double aNaiveUlpCapM = 0.0;
        int jitterSteps = 0;
        double jitterNaiveM = 0.0;
        double jitterRelocM = 0.0;
        double jitterUlpM = 0.0;
        // 口径 B
        int setRc = 0;
        int getRc = 0;
        bool originRoundtripBitwise = false;
        double originRoundtripMaxDiffM = 0.0;
        double bTransMaxM = 0.0;
        double bCenterMaxM = 0.0;
        double bChunkMaxM = 0.0;
        uint32_t bChunkMaxIdx = 0u;
        double bChunkMaxEuclidM = 0.0;
        double bChunkMaxCamDistM = 0.0;
        double bChunkMaxLocalM = 0.0;
    };
    DistRow rows[kDistCount] = {};

    for (int di = 0; di < kDistCount; ++di) {
        DistRow& r = rows[di];
        r.d = kDistances[di];
        r.ulpAtD = zplanet::FloatUlpAt(r.d);

        // 相机绝对坐标（double 域构造；悬停 → |anchor−C| = D 至 double 舍入级）
        const zplanet::Vec3d camRel = zplanet::VecSub(anchor, center);
        const zplanet::Vec3d up
            = zplanet::VecScale(camRel, 1.0 / zplanet::VecLen(camRel));
        const zplanet::Vec3d cam = zplanet::VecAdd(anchor, zplanet::VecScale(up, r.d));
        const double camArr[3] = {cam.x, cam.y, cam.z};
        const double trueRel[3] = {anchor.x - cam.x, anchor.y - cam.y, anchor.z - cam.z};
        r.actualDistM = std::sqrt(trueRel[0] * trueRel[0] + trueRel[1] * trueRel[1]
            + trueRel[2] * trueRel[2]);

        // ---- 口径 A（纯数学）----------------------------------------------------
        // correct：float(objAbs − camAbs)——重定位正确路径（生产同式 RenderOffsetF）
        float aCorrect[3] = {};
        zplanet::FloatingOrigin::RenderOffsetF(anchorArr, camArr, aCorrect);
        const double aCorrectD[3] = {static_cast<double>(aCorrect[0]),
            static_cast<double>(aCorrect[1]), static_cast<double>(aCorrect[2])};
        r.aCorrectMaxM = MaxComponentLoss(aCorrectD, trueRel);
        r.aCorrectEuclidM = EuclidLoss(aCorrectD, trueRel);

        // naive@planet-frame：float(objAbs) − float(camAbs)（全 float 大坐标）
        float aNaive[3] = {};
        zplanet::FloatingOrigin::RenderOffsetNaiveF(anchorArr, camArr, aNaive);
        const double aNaiveD[3] = {static_cast<double>(aNaive[0]),
            static_cast<double>(aNaive[1]), static_cast<double>(aNaive[2])};
        r.aNaivePlanetMaxM = MaxComponentLoss(aNaiveD, trueRel);
        // naive 理论幅值帽：坐标幅值的 ULP 上界（逐分量 max）
        for (int c = 0; c < 3; ++c) {
            const double magA = std::fabs(anchorArr[c]);
            const double magC = std::fabs(camArr[c]);
            const double cap = zplanet::FloatUlpAt((magA > magC) ? magA : magC);
            if (cap > r.aNaiveUlpCapM) {
                r.aNaiveUlpCapM = cap;
            }
        }

        // naive@origin-frame（A-WORLD-01/JitterAudit 口径）：自原点飞 D，近场 10.37m
        const zplanet::JitterAuditResult jr = zplanet::JitterAudit(r.d, 1.0, 10.37);
        r.jitterSteps = jr.steps;
        r.jitterNaiveM = jr.naiveMaxErrM;
        r.jitterRelocM = jr.relocatedMaxErrM;
        r.jitterUlpM = jr.ulpAtFlightM;

        // ---- 口径 B（ABI 全链）--------------------------------------------------
        if (abiOk) {
            // 1) SetCameraOrigin → GetCameraOrigin round-trip（§9 row3）
            r.setRc = fnSetCamOrigin(cam.x, cam.y, cam.z);
            double got[3] = {0.0, 0.0, 0.0};
            r.getRc = fnGetCamOrigin(got);
            r.originRoundtripBitwise = (r.setRc == ZR_PLANET_OK
                && r.getRc == ZR_PLANET_OK
                && std::memcmp(got, &cam.x, sizeof(got)) == 0);
            r.originRoundtripMaxDiffM = MaxComponentLoss(got, &cam.x);
            // 生产消费的是通道内存储值（= GetCameraOrigin 回读值）；round-trip
            // 逐位成立时 got == cam，平移以 got 计（最忠实口径）
            const double* camUsed = (r.getRc == ZR_PLANET_OK) ? got : &cam.x;

            // 2) 模型平移 t = float(anchorAbs − camOrigin)（unity_target.cpp 逐绘
            //    CB 口径：double 域相减后一次 cast——§9 row7 原生执行点）
            const float t[3] = {static_cast<float>(anchor.x - camUsed[0]),
                static_cast<float>(anchor.y - camUsed[1]),
                static_cast<float>(anchor.z - camUsed[2])};
            const double tD[3] = {static_cast<double>(t[0]), static_cast<double>(t[1]),
                static_cast<double>(t[2])};
            const double tTruth[3] = {anchor.x - camUsed[0], anchor.y - camUsed[1],
                anchor.z - camUsed[2]};
            r.bTransMaxM = MaxComponentLoss(tD, tTruth);

            if (vertsOk) {
                // 3) 端到端：viewPos = local + t（float32 加法，VS mul(model,·) 口径）
                //    真值 = posAbs − camOrigin（double 域度量）
                //    (a) 锚点顶点（local 恒 0——平移量化本体）
                const float view0[3] = {verts[kCenterIdx].pos[0] + t[0],
                    verts[kCenterIdx].pos[1] + t[1], verts[kCenterIdx].pos[2] + t[2]};
                const double view0D[3] = {static_cast<double>(view0[0]),
                    static_cast<double>(view0[1]), static_cast<double>(view0[2])};
                r.bCenterMaxM = MaxComponentLoss(view0D, tTruth);
                // (b) 全块最大（含 221km 级角点——提交口径，不含可见性剔除）
                for (uint32_t k = 0; k < vertCount; ++k) {
                    const float viewK[3] = {verts[k].pos[0] + t[0],
                        verts[k].pos[1] + t[1], verts[k].pos[2] + t[2]};
                    const double viewKD[3] = {static_cast<double>(viewK[0]),
                        static_cast<double>(viewK[1]), static_cast<double>(viewK[2])};
                    const double posRel[3] = {posAbs[k].x - camUsed[0],
                        posAbs[k].y - camUsed[1], posAbs[k].z - camUsed[2]};
                    const double lossK = MaxComponentLoss(viewKD, posRel);
                    if (lossK > r.bChunkMaxM) {
                        r.bChunkMaxM = lossK;
                        r.bChunkMaxIdx = k;
                        r.bChunkMaxEuclidM = EuclidLoss(viewKD, posRel);
                        r.bChunkMaxCamDistM = std::sqrt(
                            posRel[0] * posRel[0] + posRel[1] * posRel[1]
                            + posRel[2] * posRel[2]);
                        r.bChunkMaxLocalM = zplanet::VecLen(
                            zplanet::VecSub(posAbs[k], anchor));
                    }
                }
            } else {
                if (di == 0) {
                    missing.push_back({"abi.vertexChain",
                        "顶点字节不可得（见 vertexBytes.*）——口径 B 端到端顶点损失"
                        "按 MISSING 记录（平移环节 t=float(anchor−cam) 已实测）"});
                }
            }
        }

        // ---- 控制台行 -----------------------------------------------------------
        std::printf("\n[dist %8.1f m]  实距=%.9g m  ULP(D)=%.6g m\n", r.d, r.actualDistM,
            r.ulpAtD);
        std::printf("  A.correct          : %.6g m  (%.3g ULP)  euclid=%.6g m\n",
            r.aCorrectMaxM, r.aCorrectMaxM / r.ulpAtD, r.aCorrectEuclidM);
        std::printf("  A.naive@planet     : %.6g m  (%.3g ULP)  帽=%.6g m\n",
            r.aNaivePlanetMaxM, r.aNaivePlanetMaxM / r.ulpAtD, r.aNaiveUlpCapM);
        std::printf("  A.jitter@origin    : steps=%d naive=%.6g m reloc=%.6g m"
                    "（ULP(flight)=%.6g m）\n",
            r.jitterSteps, r.jitterNaiveM, r.jitterRelocM, r.jitterUlpM);
        if (abiOk) {
            std::printf("  B.origin roundtrip : bitwise=%s  maxdiff=%.6g m\n",
                r.originRoundtripBitwise ? "YES" : "NO", r.originRoundtripMaxDiffM);
            std::printf("  B.trans            : %.6g m  (%.3g ULP)  角偏差=%.3g px@1080p/hFOV60\n",
                r.bTransMaxM, r.bTransMaxM / r.ulpAtD,
                AngularPx1080p(r.bTransMaxM, r.actualDistM));
            if (vertsOk) {
                std::printf("  B.center(锚点)     : %.6g m  角偏差=%.3g px\n",
                    r.bCenterMaxM, AngularPx1080p(r.bCenterMaxM, r.actualDistM));
                std::printf("  B.chunkMax         : %.6g m  euclid=%.6g m  idx=%u  "
                            "camDist=%.6g m  |local|=%.6g m\n",
                    r.bChunkMaxM, r.bChunkMaxEuclidM,
                    static_cast<unsigned>(r.bChunkMaxIdx), r.bChunkMaxCamDistM,
                    r.bChunkMaxLocalM);
            } else {
                std::printf("  B.center/chunkMax  : MISSING（顶点字节不可得）\n");
            }
        } else {
            std::printf("  B.*                : MISSING（dll/导出面不可用）\n");
        }
    }

    // ---- §9 row10：SampleHeight float 输出（dll 纯函数 ABI，任意线程）-----------
    //
    // 16 采样点 = 近档网格 (i,j) ∈ {4,20,36,52}²（跨半块分布）；dir 复刻
    // normalize(pos − center)（zplugin.cpp 组 4 口径）；真值 = 同种子
    // PlanetTerrainHeightM（与 chunk 顶点同一生产头纯函数）。
    double shMaxDevM = 0.0;
    double shMaxAbsH = 0.0;
    int shRc = 0;
    int shOkCount = 0;
    if (abiOk && vertsOk) {
        constexpr uint32_t kShIdx[4] = {4u, 20u, 36u, 52u};
        for (uint32_t ju = 0; ju < 4u; ++ju) {
            for (uint32_t iu = 0; iu < 4u; ++iu) {
                const uint32_t vi = kShIdx[ju] * kRes + kShIdx[iu];
                const zplanet::Vec3d p = posAbs[vi];
                float hF = 0.0f;
                shRc = fnSampleHeight(p.x, p.y, p.z, &hF);
                if (shRc != ZR_PLANET_OK) {
                    continue;
                }
                ++shOkCount;
                const zplanet::Vec3d dir = zplanet::VecScale(
                    zplanet::VecSub(p, center),
                    1.0 / zplanet::VecLen(zplanet::VecSub(p, center)));
                const double hRef = zplanet::PlanetTerrainHeightM(
                    kSeed, dir.x, dir.y, dir.z);
                const double dev = std::fabs(static_cast<double>(hF) - hRef);
                if (dev > shMaxDevM) {
                    shMaxDevM = dev;
                }
                if (std::fabs(hRef) > shMaxAbsH) {
                    shMaxAbsH = std::fabs(hRef);
                }
            }
        }
        if (shOkCount == 0) {
            missing.push_back({"abi.sampleHeight",
                "dll ZR_Planet_SampleHeight 全部 rc=" + std::to_string(shRc)
                    + "——§9 row10 按 MISSING 记录"});
        }
    } else if (abiOk && !vertsOk) {
        missing.push_back({"abi.sampleHeight",
            "顶点真值不可得（见 vertexBytes.*）——采样点坐标缺失，row10 按 MISSING 记录"});
    }
    const double ulpAtMaxH = (shMaxAbsH > 0.0) ? zplanet::FloatUlpAt(shMaxAbsH) : 0.0;
    if (shOkCount > 0) {
        std::printf("\nsampleHeight        : n=%d rc=%d  max|h|=%.6g m  dev max=%.6g m"
                    "（ULP(max|h|)=%.6g m）\n",
            shOkCount, shRc, shMaxAbsH, shMaxDevM, ulpAtMaxH);
    }

    // ---- 清理（Destroy + ClearAll 兜底；账目复认）------------------------------
    bool cleanupAccountInvariant = false;
    uint32_t liveAfterCleanup = 0u;
    if (abiOk && chunkHandle != 0u) {
        const int destroyRc = fnChunkDestroy(chunkHandle);
        if (destroyRc != ZR_PLANET_OK) {
            missing.push_back({"abi.chunkDestroy",
                "dll ZR_Planet_ChunkDestroy rc=" + std::to_string(destroyRc)});
        }
        fnClearAll();
        std::memset(&stats, 0, sizeof(stats));
        if (fnGetStats(&stats) == ZR_PLANET_OK) {
            liveAfterCleanup = stats.liveChunks;
            cleanupAccountInvariant = (stats.createTotal - stats.destroyTotal)
                == static_cast<uint64_t>(stats.liveChunks);
            std::printf("cleanup             : create=%llu destroy=%llu live=%u "
                        "pendingFree=%u account=%s\n",
                static_cast<unsigned long long>(stats.createTotal),
                static_cast<unsigned long long>(stats.destroyTotal),
                static_cast<unsigned>(stats.liveChunks),
                static_cast<unsigned>(stats.pendingFrees),
                cleanupAccountInvariant ? "OK" : "BROKEN");
        }
    }

    // ---- row1 双 ULP 参照（计算口径确认，§9 表第 1 行）--------------------------
    const double ulp1e7 = [&]() {
        const double v = 1.0e7;
        const double next = std::nextafter(v, std::numeric_limits<double>::infinity());
        return next - v;
    }();

    // ---- budget9 逐环节对照（数据不是断言——达标/超出都如实落盘）-----------------
    const bool row5WithinFullUlp = vertsOk && (devMaxM <= ulpAtMaxLocal);
    const bool row5WithinHalfUlp = vertsOk && (devMaxM <= ulpAtMaxLocal * 0.5);
    const bool row7NearFieldClaim
        = abiOk && (rows[0].bTransMaxM <= 0.00048828125)   // ULP(4km)=0.488mm（D=100m）
        && (rows[1].bTransMaxM <= 0.00048828125)           // D=1km
        && (rows[2].bTransMaxM <= 0.00048828125);          // D=10km（近场口径 ≤4km 边界内）
    bool row10Within = false;
    if (shOkCount > 0) {
        row10Within = (shMaxDevM <= ulpAtMaxH) && (shMaxDevM <= 0.000244140625);
    }
    bool naiveWithinCap = true;
    for (int di = 0; di < kDistCount; ++di) {
        if (rows[di].aNaivePlanetMaxM
            > rows[di].aNaiveUlpCapM + rows[di].ulpAtD * 0.5 + 1e-12) {
            naiveWithinCap = false;
        }
    }

    std::printf("\n=== budget9 对照（§9 预算表逐环节；数据非断言）===\n");
    std::printf("row3 SetCameraOrigin : roundtrip bitwise %d/%d（参考=0 无损）\n",
        (rows[0].originRoundtripBitwise ? 1 : 0) + (rows[1].originRoundtripBitwise ? 1 : 0)
            + (rows[2].originRoundtripBitwise ? 1 : 0)
            + (rows[3].originRoundtripBitwise ? 1 : 0),
        abiOk ? kDistCount : 0);
    std::printf("row5 顶点局部float   : %.6g m vs ULP(|local|max)=%.6g m → 全ULP=%s 半ULP=%s\n",
        devMaxM, ulpAtMaxLocal, row5WithinFullUlp ? "达标" : "超出",
        row5WithinHalfUlp ? "达标" : "超出");
    std::printf("row7 平移近场≤0.488mm: %s（100m/1km/10km 实测 %.3g/%.3g/%.3g m）\n",
        row7NearFieldClaim ? "达标" : "超出", rows[0].bTransMaxM, rows[1].bTransMaxM,
        rows[2].bTransMaxM);
    std::printf("row10 SampleHeight   : %.6g m vs ULP(max|h|)=%.6g m / 0.244mm 参照 → %s\n",
        shMaxDevM, ulpAtMaxH,
        (shOkCount > 0) ? (row10Within ? "达标" : "超出") : "MISSING");
    std::printf("naive 幅值帽自检     : %s\n", naiveWithinCap ? "全距离 ≤帽" : "有超出");

    // ---- JSON 组装（文件名白名单 + out 边界断言，fsr4_probe 口径）---------------
    const std::string fileName = "abi-precision-" + stampFile + ".json";
    if (!IsValidProbeFileName(fileName)) {
        std::fprintf(stderr, "abi-precision: 输出文件名不合白名单，拒绝写文件: %s\n",
            fileName.c_str());
        return 1;
    }
    const std::string outRoot = out.outDir + "\\";  // 常量根
    const std::string outFile = outRoot + fileName; // basename 已过白名单
    if (outFile.compare(0, outRoot.size(), outRoot) != 0) {
        std::fprintf(stderr, "abi-precision: 输出路径越出探针 out 目录，拒绝写文件\n");
        return 1;
    }
    std::error_code fsEc;
    std::filesystem::create_directories(std::filesystem::path(out.outDir), fsEc);
    if (fsEc.value() != 0) {
        std::fprintf(stderr, "abi-precision: out 目录不可创建 (%s): %s\n", out.outDir.c_str(),
            fsEc.message().c_str());
        return 1;
    }

    std::string json;
    json += "{\n";
    json += "  \"probedAt\": \"" + probedAt + "\",\n";
    json += "  \"probe\": \"abi-precision\",\n";
    json += "  \"task\": \"TASK-B-PLANET-10\",\n";
    json += "  \"outMode\": \"" + out.mode + "\",\n";
    json += "  \"outFile\": \"" + JsonEscape(outFile) + "\",\n";
    json += "  \"host\": {\n";
    json += "    \"computerName\": \"" + std::string(hostNameOk ? hostNameA : "") + "\",\n";
    json += "    \"exePath\": \"" + JsonEscape(exePath) + "\"\n";
    json += "  },\n";
    json += "  \"engine\": {\n";
    json += "    \"apiVersion\": " + std::to_string(dllApiVersion) + ",\n";
    json += "    \"apiVersionExpected\": 3,\n";
    json += "    \"zrVersion\": \"" + JsonEscape(dllVersion) + "\",\n";
    json += "    \"dllPath\": \"" + JsonEscape(dllOk ? std::string(dllPathA) : "") + "\",\n";
    json += "    \"dllLoaded\": " + std::string(dllOk ? "true" : "false") + ",\n";
    json += "    \"linkMode\": \"zrender.dll via LoadLibrary (C ABI 导出面) + "
            "生产 GenerateChunkVerts via zrhi 静态库白盒钩子（ChunkCreate 内部同函数，"
            "零复制零漂移）；钩子上下文由静态 zplugin SetWorld 喂给（与 dll 各自"
            "独立实例）\"\n";
    json += "  },\n";
    json += "  \"setup\": {\n";
    json += "    \"seed\": " + std::to_string(kSeed) + ",\n";
    json += "    \"faceId\": " + std::to_string(kFace) + ",\n";
    json += "    \"cx\": " + std::to_string(kCx) + ",\n";
    json += "    \"cy\": " + std::to_string(kCy) + ",\n";
    json += "    \"cellsPerEdge\": " + std::to_string(kCells) + ",\n";
    json += "    \"lodLevel\": " + std::to_string(kLod) + ",\n";
    json += "    \"tierRes\": " + std::to_string(kRes) + ",\n";
    json += "    \"stride\": " + std::to_string(kStride) + ",\n";
    json += "    \"totalGrid\": " + std::to_string(kTotal) + ",\n";
    json += "    \"planetRadiusM\": " + JsonNum(zplanet::kPlanetRadiusM) + ",\n";
    json += "    \"centerAbs\": [" + JsonNum(center.x) + ", " + JsonNum(center.y) + ", "
        + JsonNum(center.z) + "],\n";
    json += "    \"anchorAbs\": [" + JsonNum(anchor.x) + ", " + JsonNum(anchor.y) + ", "
        + JsonNum(anchor.z) + "],\n";
    json += "    \"anchorIsGridVertex\": true,\n";
    json += "    \"anchorVertexIndex\": " + std::to_string(kCenterIdx) + ",\n";
    json += "    \"chunkArcM\": " + JsonNum(chunkArcM) + ",\n";
    json += "    \"cellArcM\": " + JsonNum(cellArcM) + ",\n";
    json += "    \"skirtDropM\": " + JsonNum(skirtDropM) + ",\n";
    json += "    \"vertexCount\": " + std::to_string(vertCount) + ",\n";
    json += "    \"distancesM\": [100, 1000, 10000, 100000],\n";
    json += "    \"cameraPlacement\": \"锚点沿球面法向悬停 D（|target−cam|=D，"
            "target=锚点顶点；口径A/B 同一对几何）\"\n";
    json += "  },\n";
    json += "  \"ulpReference\": {\n";
    json += "    \"method\": \"FloatUlpAt 实测（nextafter float）\",\n";
    json += "    \"ulp100m\": " + JsonNum(zplanet::FloatUlpAt(100.0)) + ",\n";
    json += "    \"ulp1km\": " + JsonNum(zplanet::FloatUlpAt(1000.0)) + ",\n";
    json += "    \"ulp10km\": " + JsonNum(zplanet::FloatUlpAt(10000.0)) + ",\n";
    json += "    \"ulp100km\": " + JsonNum(zplanet::FloatUlpAt(100000.0)) + ",\n";
    json += "    \"design9Values\": {\n";
    json += "      \"ulp100m\": 7.62939453125e-06,\n";
    json += "      \"ulp1km\": 6.103515625e-05,\n";
    json += "      \"ulp10km\": 0.0009765625,\n";
    json += "      \"ulp100km\": 0.0078125,\n";
    json += "      \"note\": \"设计稿 §9 理论对照值（计算）；本探针实测值应逐位一致\"\n";
    json += "    },\n";
    json += "    \"doubleUlpAt1e7m\": " + JsonNum(ulp1e7) + ",\n";
    json += "    \"doubleUlpAt1e7mDesign9\": 1.86264514923095703125e-09\n";
    json += "  },\n";
    json += "  \"staticDeviation\": {\n";
    json += "    \"row5VertexLocalMaxM\": " + JsonNum(devMaxM) + ",\n";
    json += "    \"row5MaxLocalM\": " + JsonNum(maxLocalM) + ",\n";
    json += "    \"row5UlpAtMaxLocalM\": " + JsonNum(ulpAtMaxLocal) + ",\n";
    json += "    \"row5WithinFullUlp\": " + std::string(row5WithinFullUlp ? "true" : "false")
        + ",\n";
    json += "    \"row5WithinHalfUlp\": " + std::string(row5WithinHalfUlp ? "true" : "false")
        + ",\n";
    json += "    \"centerVertexLocalAllZero\": "
        + std::string(centerLocalAllZero ? "true" : "false") + ",\n";
    json += "    \"gridBitwiseMatches\": " + std::to_string(gridBitwiseMatches) + ",\n";
    json += "    \"gridTotal\": " + std::to_string(kGridVerts) + ",\n";
    json += "    \"skirtToleranceMatches\": " + std::to_string(skirtToleranceMatches) + ",\n";
    json += "    \"skirtTotal\": " + std::to_string(kSkirtVerts) + ",\n";
    json += "    \"vertexSource\": \"production GenerateChunkVerts via unit-test hook "
            "(zrhi static lib, same source as dll)\"\n";
    json += "  },\n";
    json += "  \"distances\": [\n";
    for (int di = 0; di < kDistCount; ++di) {
        const DistRow& r = rows[di];
        json += "    {\n";
        json += "      \"distanceM\": " + JsonNum(r.d) + ",\n";
        json += "      \"actualCameraTargetDistM\": " + JsonNum(r.actualDistM) + ",\n";
        json += "      \"ulpAtD\": " + JsonNum(r.ulpAtD) + ",\n";
        json += "      \"aPureMath\": {\n";
        json += "        \"correctMaxM\": " + JsonNum(r.aCorrectMaxM) + ",\n";
        json += "        \"correctMaxUlpUnits\": " + JsonNum(r.aCorrectMaxM / r.ulpAtD)
            + ",\n";
        json += "        \"correctEuclidM\": " + JsonNum(r.aCorrectEuclidM) + ",\n";
        json += "        \"naivePlanetFrameMaxM\": " + JsonNum(r.aNaivePlanetMaxM) + ",\n";
        json += "        \"naivePlanetFrameUlpUnits\": "
            + JsonNum(r.aNaivePlanetMaxM / r.ulpAtD) + ",\n";
        json += "        \"naiveUlpCapM\": " + JsonNum(r.aNaiveUlpCapM) + ",\n";
        json += "        \"jitterOriginFrame\": {\n";
        json += "          \"method\": \"JitterAudit(D, 1.0, 10.37)——A-WORLD-01 口径 "
                "（相机自原点飞 D，近场物体恒前方 10.37m）\",\n";
        json += "          \"steps\": " + std::to_string(r.jitterSteps) + ",\n";
        json += "          \"naiveMaxErrM\": " + JsonNum(r.jitterNaiveM) + ",\n";
        json += "          \"relocatedMaxErrM\": " + JsonNum(r.jitterRelocM) + ",\n";
        json += "          \"ulpAtFlightM\": " + JsonNum(r.jitterUlpM) + "\n";
        json += "        }\n";
        json += "      },\n";
        json += "      \"bAbi\": {\n";
        if (abiOk) {
            json += "        \"setCameraOriginRc\": " + std::to_string(r.setRc) + ",\n";
            json += "        \"getCameraOriginRc\": " + std::to_string(r.getRc) + ",\n";
            json += "        \"originRoundtripBitwise\": "
                + std::string(r.originRoundtripBitwise ? "true" : "false") + ",\n";
            json += "        \"originRoundtripMaxDiffM\": "
                + JsonNum(r.originRoundtripMaxDiffM) + ",\n";
            json += "        \"translationMaxM\": " + JsonNum(r.bTransMaxM) + ",\n";
            json += "        \"translationUlpUnits\": "
                + JsonNum(r.bTransMaxM / r.ulpAtD) + ",\n";
            json += "        \"translationAngularPx1080pHfov60\": "
                + JsonNum(AngularPx1080p(r.bTransMaxM, r.actualDistM)) + ",\n";
            json += "        \"centerVertexMaxM\": " + JsonNum(r.bCenterMaxM) + ",\n";
            json += "        \"centerVertexAngularPx1080pHfov60\": "
                + JsonNum(AngularPx1080p(r.bCenterMaxM, r.actualDistM)) + ",\n";
            json += "        \"chunkMaxM\": " + JsonNum(r.bChunkMaxM) + ",\n";
            json += "        \"chunkMaxEuclidM\": " + JsonNum(r.bChunkMaxEuclidM) + ",\n";
            json += "        \"chunkMaxVertexIndex\": " + std::to_string(r.bChunkMaxIdx)
                + ",\n";
            json += "        \"chunkMaxCamDistM\": " + JsonNum(r.bChunkMaxCamDistM) + ",\n";
            json += "        \"chunkMaxLocalM\": " + JsonNum(r.bChunkMaxLocalM) + ",\n";
            json += "        \"chunkMaxAngularPx1080pHfov60\": "
                + JsonNum(AngularPx1080p(r.bChunkMaxM, r.bChunkMaxCamDistM)) + ",\n";
            json += "        \"scopeNote\": \"提交口径不含可见性剔除——chunkMax 含"
                    "近场不可见角点；角偏差（px 列）才是可见性口径\"\n";
        } else {
            json += "        \"status\": \"MISSING\",\n";
            json += "        \"reason\": \"zrender.dll 导出面不可用\"\n";
        }
        json += "      }\n";
        json += "    }";
        if (di + 1 < kDistCount) {
            json += ",";
        }
        json += "\n";
    }
    json += "  ],\n";
    json += "  \"sampleHeight\": {\n";
    if (shOkCount > 0) {
        json += "    \"samples\": " + std::to_string(shOkCount) + ",\n";
        json += "    \"rc\": " + std::to_string(shRc) + ",\n";
        json += "    \"maxAbsHeightM\": " + JsonNum(shMaxAbsH) + ",\n";
        json += "    \"maxDevM\": " + JsonNum(shMaxDevM) + ",\n";
        json += "    \"ulpAtMaxHeightM\": " + JsonNum(ulpAtMaxH) + ",\n";
        json += "    \"withinBudget9Row10\": "
            + std::string(row10Within ? "true" : "false") + ",\n";
        json += "    \"design9RefM\": 0.000244140625,\n";
        json += "    \"method\": \"dll ZR_Planet_SampleHeight(float 输出) vs "
                "PlanetTerrainHeightM(1337, normalize(pos−center)) double 复刻——"
                "seed/中心与 chunk 生成同源\"\n";
    } else {
        json += "    \"status\": \"MISSING\",\n";
        json += "    \"reason\": \"dll 导出面或顶点真值不可用\"\n";
    }
    json += "  },\n";
    json += "  \"stats\": {\n";
    if (abiOk && chunkHandle != 0u) {
        json += "    \"createRc\": " + std::to_string(dllCreateRc) + ",\n";
        json += "    \"handle\": \"" + std::to_string(static_cast<unsigned long long>(chunkHandle))
            + "\",\n";
        json += "    \"createTotal\": "
            + std::to_string(static_cast<unsigned long long>(stats.createTotal)) + ",\n";
        json += "    \"destroyTotal\": "
            + std::to_string(static_cast<unsigned long long>(stats.destroyTotal)) + ",\n";
        json += "    \"liveAfterCleanup\": " + std::to_string(liveAfterCleanup) + ",\n";
        json += "    \"accountInvariant\": "
            + std::string(accountInvariant && cleanupAccountInvariant ? "true" : "false")
            + ",\n";
        json += "    \"note\": \"无 RenderFrame——pendingUpload 永不升 GPU，零设备依赖\"\n";
    } else {
        json += "    \"status\": \"MISSING\",\n";
        json += "    \"reason\": \"dll 提交链不可用\"\n";
    }
    json += "  },\n";
    json += "  \"budget9\": [\n";
    // row1 / row2 / row3 / row4 / row5 / row6 / row7 / row8 / row9 / row10
    json += "    { \"row\": 1, \"name\": \"相机 double 逐帧累计\", "
            "\"status\": \"NOT_IN_PROBE\", "
            "\"reference\": \"double ULP@1e7m=2^-29=1.8626e-9 m（计算）\", "
            "\"measured\": \"doubleUlpAt1e7m 实算=" + JsonNum(ulp1e7)
        + "（确认参照；逐帧累计属托管 WorldPosition——非本探针口径）\" },\n";
    json += "    { \"row\": 2, \"name\": \"浮点原点重定位 4000m\", "
            "\"status\": \"NOT_IN_PROBE\", "
            "\"reference\": \"|cam−origin| ≤ 4000m → 下游 float 化幅值封顶\", "
            "\"measured\": \"托管 WorldOrigin 生产口径（A-WORLD-01 >= 触发）——"
            "原生只消费 SetCameraOrigin，其 double 直传在 row3 实测\" },\n";
    json += "    { \"row\": 3, \"name\": \"SetCameraOrigin 跨 ABI\", "
            "\"status\": \"" + std::string(abiOk ? "MEASURED" : "MISSING") + "\", "
            "\"reference\": \"0（double 直传无损）\", "
            "\"measured\": \""
        + std::string(abiOk
                ? ("round-trip 逐位 " + std::string(rows[0].originRoundtripBitwise
                          && rows[1].originRoundtripBitwise
                          && rows[2].originRoundtripBitwise
                          && rows[3].originRoundtripBitwise
                       ? "4/4"
                       : "<4/4")
                      + "，maxdiff="
                      + JsonNum(std::max(std::max(std::max(rows[0].originRoundtripMaxDiffM,
                                             rows[1].originRoundtripMaxDiffM),
                                     rows[2].originRoundtripMaxDiffM),
                          rows[3].originRoundtripMaxDiffM))
                      + " m")
                : "MISSING——dll 导出面不可用（见 missing 节）")
        + "\", "
            "\"withinBudget\": " + std::string(abiOk ? "true" : "false") + " },\n";
    json += "    { \"row\": 4, \"name\": \"chunk 锚点 double（绝对语义）\", "
            "\"status\": \"" + std::string(abiOk && centerLocalAllZero ? "MEASURED" : "PARTIAL")
        + "\", "
            "\"reference\": \"0（设计）\", "
            "\"measured\": \"desc.originAbs 经 dll ChunkCreate double 直传（账目自证）；"
            "顶点级自证在静态同源钩子路径 local(anchor)=0 逐位（锚点=中心格顶点）\", "
            "\"withinBudget\": " + std::string(centerLocalAllZero ? "true" : "false") + " },\n";
    json += "    { \"row\": 5, \"name\": \"顶点局部 float=float(posAbs−anchor)\", "
            "\"status\": \"" + std::string(vertsOk ? "MEASURED" : "MISSING") + "\", "
            "\"reference\": \"≤ ULP(~221km)=15.6mm（chunk 角点；计算）\", "
            "\"measured\": \""
        + std::string(vertsOk
                ? ("devMax=" + JsonNum(devMaxM) + " m @ |local|max=" + JsonNum(maxLocalM)
                      + " m（ULP(|local|max)=" + JsonNum(ulpAtMaxLocal) + " m）")
                : "MISSING——静态 SetWorld/顶点生成不可用（见 missing 节）")
        + "\", "
            "\"withinBudgetFullUlp\": " + std::string(row5WithinFullUlp ? "true" : "false")
        + ", "
            "\"withinBudgetHalfUlp\": " + std::string(row5WithinHalfUlp ? "true" : "false")
        + " },\n";
    json += "    { \"row\": 6, \"name\": \"共享边双侧 float 化静态缝\", "
            "\"status\": \"COVERED_ELSEWHERE\", "
            "\"reference\": \"≤ ~31mm（2×ULP，估算）\", "
            "\"measured\": \"本探针不重测——B-PLANET-08 生产路径实测 max 15.93mm "
            "（240 顶点对，跨面/同面/双档），达标（估算上界内）；见 "
            "Native/tests/test_planet_channel.cpp 测量探针与 TASK-B-PLANET-08 日志\" },\n";
    json += "    { \"row\": 7, \"name\": \"model 平移 float=float(anchor−camOrigin)\", "
            "\"status\": \"" + std::string(abiOk ? "MEASURED" : "MISSING") + "\", "
            "\"reference\": \"近场 ≤ ULP(4km)=0.488mm（仅近场成立）；远景角偏差亚毫像素"
            "（相对误差恒 ≈2^-24 rad 封顶）\", "
            "\"measured\": \""
        + std::string(abiOk
                ? ("四点 translationMaxM=" + JsonNum(rows[0].bTransMaxM) + "/"
                      + JsonNum(rows[1].bTransMaxM) + "/" + JsonNum(rows[2].bTransMaxM)
                      + "/" + JsonNum(rows[3].bTransMaxM) + " m（100m/1km/10km/100km）")
                : "MISSING——dll 导出面不可用（见 missing 节）")
        + "\", "
            "\"nearFieldClaimWithin\": " + std::string(row7NearFieldClaim ? "true" : "false")
        + ", "
            "\"angularPx1080pHfov60\": ["
        + JsonNum(AngularPx1080p(rows[0].bTransMaxM, rows[0].actualDistM)) + ", "
        + JsonNum(AngularPx1080p(rows[1].bTransMaxM, rows[1].actualDistM)) + ", "
        + JsonNum(AngularPx1080p(rows[2].bTransMaxM, rows[2].actualDistM)) + ", "
        + JsonNum(AngularPx1080p(rows[3].bTransMaxM, rows[3].actualDistM)) + "] },\n";
    json += "    { \"row\": 8, \"name\": \"view/proj float（托管相机相对构造）\", "
            "\"status\": \"NOT_IN_PROBE\", "
            "\"reference\": \"近场相对误差 <1e-6 m（重定位路径实测）\", "
            "\"measured\": \"托管侧构造——非本探针口径（test_planet.cpp:36 doctest 先例；"
            "A-WORLD-01 托管接线）\" },\n";
    json += "    { \"row\": 9, \"name\": \"GPU 光栅 float32\", "
            "\"status\": \"NOT_IN_PROBE\", "
            "\"reference\": \"与现状一致（非新增误差源）\", "
            "\"measured\": \"RHI 内部——非本探针口径（planet-budget 探针像素口径另测）\" },\n";
    json += "    { \"row\": 10, \"name\": \"高度输出 float（SampleHeight）\", "
            "\"status\": \"" + std::string(shOkCount > 0 ? "MEASURED" : "MISSING") + "\", "
            "\"reference\": \"≤ ULP(2.4km)≈0.244mm（计算）\", "
            "\"measured\": \""
        + std::string(shOkCount > 0
                ? ("devMax=" + JsonNum(shMaxDevM) + " m @ max|h|=" + JsonNum(shMaxAbsH)
                      + " m（ULP(max|h|)=" + JsonNum(ulpAtMaxH) + " m；n="
                      + std::to_string(shOkCount) + "）")
                : "MISSING——dll 导出面或顶点真值不可用（见 missing 节）")
        + "\", "
            "\"withinBudget\": " + std::string((shOkCount > 0 && row10Within) ? "true" : "false")
        + " }\n";
    json += "  ],\n";
    json += "  \"selfCheck\": {\n";
    json += "    \"dllLoaded\": " + std::string(dllOk ? "true" : "false") + ",\n";
    json += "    \"abiResolved\": " + std::string(abiOk ? "true" : "false") + ",\n";
    json += "    \"apiVersion3\": " + std::string(dllApiVersion == 3u ? "true" : "false")
        + ",\n";
    json += "    \"vertexBytesFromProductionHook\": "
        + std::string(vertsOk ? "true" : "false") + ",\n";
    json += "    \"centerVertexLocalAllZero\": "
        + std::string(centerLocalAllZero ? "true" : "false") + ",\n";
    json += "    \"gridLocalBitwiseAll\": "
        + std::string((vertsOk && gridBitwiseMatches == kGridVerts) ? "true" : "false")
        + ",\n";
    json += "    \"naiveWithinUlpCap\": " + std::string(naiveWithinCap ? "true" : "false")
        + ",\n";
    json += "    \"accountInvariant\": "
        + std::string(accountInvariant && cleanupAccountInvariant ? "true" : "false")
        + ",\n";
    json += "    \"ulpMatchesDesign9\": "
        + std::string((zplanet::FloatUlpAt(100000.0) == 0.0078125
                          && zplanet::FloatUlpAt(100.0) == 7.62939453125e-06)
                          ? "true"
                          : "false")
        + "\n";
    json += "  },\n";
    json += "  \"missing\": [\n";
    for (size_t i = 0; i < missing.size(); ++i) {
        json += "    { \"name\": \"" + JsonEscape(missing[i].name) + "\", \"reason\": \""
            + JsonEscape(missing[i].reason) + "\" }" + ((i + 1 < missing.size()) ? "," : "")
            + "\n";
    }
    json += "  ],\n";
    json += "  \"references\": [\n";
    json += "    { \"label\": \"A-WORLD-01 V6 实测锚点\", "
            "\"value\": \"float ULP@100km=7.8125mm；float direct error=1.0mm；"
            "double+offset 守恒 4.366e-11 m\", "
            "\"source\": \"Docs/Logs/TASK-A-WORLD-01.md §5.3\" },\n";
    json += "    { \"label\": \"设计稿 §9 理论对照值\", "
            "\"value\": \"ULP(100m)=7.6e-6 / ULP(1km)=6.1e-5 / ULP(10km)=0.98mm / "
            "ULP(100km)=7.8125mm；row5=ULP(221km)=15.6mm；row7 近场=ULP(4km)=0.488mm；"
            "row10=ULP(2.4km)=0.244mm\", "
            "\"source\": \"Docs/Planning/P2-行星ABI设计.md §9\" },\n";
    json += "    { \"label\": \"B-PLANET-08 顶点缝宽实测（row6 归属）\", "
            "\"value\": \"VB 回读缝宽 max 15.93mm / 渲染口径（缘上 2m）max ~8mm 级——"
            "全部在 §5.1 估算 ≤31mm 内\", "
            "\"source\": \"Docs/Logs/TASK-B-PLANET-08.md\" },\n";
    json += "    { \"label\": \"doctest 既有口径\", "
            "\"value\": \"naive@100km >1mm ≤ULP=7.8125mm；relocated <1e-6 m；"
            "ULP(1km)<1e-4（test_planet.cpp:48）\", "
            "\"source\": \"Native/tests/test_planet.cpp\" }\n";
    json += "  ]\n";
    json += "}\n";

    std::ofstream file(outFile, std::ios::binary | std::ios::trunc);
    if (!file) {
        std::fprintf(stderr, "abi-precision: 无法打开输出文件: %s\n", outFile.c_str());
        return 1;
    }
    file << json;
    file.close();
    if (file.fail()) {
        std::fprintf(stderr, "abi-precision: 输出文件写入失败: %s\n", outFile.c_str());
        return 1;
    }

    std::printf("\n[missing] %zu 项（不填默认值，如实记录）\n", missing.size());
    for (const MissingItem& item : missing) {
        std::printf("  MISSING | %s | %s\n", item.name.c_str(), item.reason.c_str());
    }
    std::printf("\nJSON -> %s\n", outFile.c_str());
    std::printf("exit=0（JSON 已落盘；精度是测量不是过/不过——对照表与 JSON 数字应一致）\n");
    return 0;
}
