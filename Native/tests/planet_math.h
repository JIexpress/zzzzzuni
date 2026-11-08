#pragma once
// =============================================================================
// planet_math.h —— TASK-B-TECH-03 行星尺度最小切片：纯数学内核（无渲染依赖）
//
// 双消费方：tests/zrhi_d3d12_window.cpp（--planet 窗口模式）与
// tests/test_planet.cpp（doctest）。头文件仅依赖标准库（<cmath>/<limits>/
// <vector>），保证单测与窗口程序验证的是同一份实现。
//
// 三块能力（对应 Docs/TECH-ROADMAP.md 方向三切片目标）：
//   ① 浮点原点重定位（floating origin）：
//      相机绝对坐标 double 逐帧累计（飞 100,000m 量级无精度损失）；
//      世界物体保存 double 绝对坐标；渲染偏移 = float(objAbs - camAbs)
//      （double 域相减后再取 float——「重定位正确路径」）。
//      对照「naive 路径」= 无 double、无重定位的全 float 管线：
//      float(objAbs) - float(camAbs)——大坐标（>10km）相减把相对位置精度
//      吞到 ULP(100km)=7.8125mm，近场顶点逐帧抖动（抖动根源）。
//      相机相对当前原点偏移超过阈值（缺省 10km，简报口径）时重定位：
//      原点吸附到相机，所有物体相对原点的坐标回到阈值量级内
//      （「所有物体 transform 平移回原点附近」）。
//   ② 球面重力积分器：a = -μ·r̂/r²（μ=地球参数 3.986004418e14 m³/s²），
//      velocity Verlet（辛积分，能量有界振荡——守恒断言的前提）；
//      活力公式（vis-viva）解析近/远地点作数值断言基准。
//   ③ 球面几何助手：切平面偏移 → 球面点（半径 6.371e6m），
//      供程序化行星网格（地形片/远盘/整球）双精度建模。
// =============================================================================

#include <cmath>
#include <limits>
#include <vector>

namespace zplanet {

// ---- 行星常量（地球参数，简报口径）-------------------------------------------
constexpr double kPlanetRadiusM = 6.371e6;    // 行星半径 m
constexpr double kGravityMu = 3.986004418e14; // 引力常数 μ，m³/s²

// ---- double 三维向量（行星中心系绝对坐标，单位 m）-----------------------------
struct Vec3d {
    double x = 0.0;
    double y = 0.0;
    double z = 0.0;
};

inline Vec3d VecAdd(const Vec3d& a, const Vec3d& b)
{
    return { a.x + b.x, a.y + b.y, a.z + b.z };
}

inline Vec3d VecSub(const Vec3d& a, const Vec3d& b)
{
    return { a.x - b.x, a.y - b.y, a.z - b.z };
}

inline Vec3d VecScale(const Vec3d& a, double s)
{
    return { a.x * s, a.y * s, a.z * s };
}

inline double VecDot(const Vec3d& a, const Vec3d& b)
{
    return a.x * b.x + a.y * b.y + a.z * b.z;
}

inline Vec3d VecCross(const Vec3d& a, const Vec3d& b)
{
    return { a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x };
}

inline double VecLen(const Vec3d& a)
{
    return std::sqrt(VecDot(a, a));
}

// ---- float 精度工具：幅值 m 处的 float ULP（相邻可表示值的间距）----------------
// 顶点抖动的量化下限：float 相对位置在幅值 m 处的分辨率即 ULP(m)
//（1e5m 处 = 2^-7 = 7.8125mm；10m 处 ≈ 9.5e-7m）。
inline double FloatUlpAt(double m)
{
    if (!(m > 0.0)) {
        return 0.0;
    }
    const float f = static_cast<float>(m);
    const float next = std::nextafter(f, std::numeric_limits<float>::infinity());
    return static_cast<double>(next) - static_cast<double>(f);
}

// =============================================================================
// ① 浮点原点重定位
// =============================================================================

struct FloatingOrigin {
    double camAbs[3] = { 0.0, 0.0, 0.0 };    // 相机绝对坐标（double 逐帧累计）
    double originAbs[3] = { 0.0, 0.0, 0.0 }; // 当前浮点原点（绝对坐标）
    double thresholdM = 10000.0;             // 重定位阈值（简报口径 10km）
    int relocationCount = 0;                 // 累计重定位次数
    double lastRelocationOffsetM = 0.0;      // 最近一次触发时相机相对旧原点的偏移
    double maxOriginOffsetM = 0.0;           // 历史最大相机-原点偏移（重定位前峰值）

    // 触发判据：|camAbs - originAbs| > threshold（严格大于：恰在阈值不触发，
    // 避免恰好在阈值上反复重定位）。按三维欧氏距离判定（斜向飞行不漏触发）。
    static bool ShouldRelocate(const double* camAbs, const double* originAbs,
        double thresholdM)
    {
        const double dx = camAbs[0] - originAbs[0];
        const double dy = camAbs[1] - originAbs[1];
        const double dz = camAbs[2] - originAbs[2];
        const double distSq = dx * dx + dy * dy + dz * dz;
        return distSq > thresholdM * thresholdM;
    }

    bool ShouldRelocateNow() const
    {
        return ShouldRelocate(camAbs, originAbs, thresholdM);
    }

    // 相机 double 累计一步；同步维护历史最大偏移（重定位判据的可观测口径）
    void MoveCameraBy(double dx, double dy, double dz)
    {
        camAbs[0] += dx;
        camAbs[1] += dy;
        camAbs[2] += dz;
        const double ox = camAbs[0] - originAbs[0];
        const double oy = camAbs[1] - originAbs[1];
        const double oz = camAbs[2] - originAbs[2];
        const double off = std::sqrt(ox * ox + oy * oy + oz * oz);
        if (off > maxOriginOffsetM) {
            maxOriginOffsetM = off;
        }
    }

    // 重定位：原点吸附到相机绝对坐标。物体 double 绝对坐标不变（绝对语义），
    // 「平移回原点附近」由 objRel = objAbs - originAbs 的新值体现（调用方按需
    // 重算锚定几何）。返回触发时相机相对旧原点的偏移量（证据值）。
    double Relocate()
    {
        const double ox = camAbs[0] - originAbs[0];
        const double oy = camAbs[1] - originAbs[1];
        const double oz = camAbs[2] - originAbs[2];
        const double off = std::sqrt(ox * ox + oy * oy + oz * oz);
        originAbs[0] = camAbs[0];
        originAbs[1] = camAbs[1];
        originAbs[2] = camAbs[2];
        ++relocationCount;
        lastRelocationOffsetM = off;
        return off;
    }

    // 渲染偏移（重定位正确路径）：double 域相对化后取 float。
    // 精度由相对幅值决定（近场 ~µm），与相机离原点多远无关。
    static void RenderOffsetF(const double* objAbs, const double* camAbs, float* out)
    {
        out[0] = static_cast<float>(objAbs[0] - camAbs[0]);
        out[1] = static_cast<float>(objAbs[1] - camAbs[1]);
        out[2] = static_cast<float>(objAbs[2] - camAbs[2]);
    }

    // 渲染偏移（naive 对照路径）：全 float 大坐标直接相减。
    // 精度被 ULP(绝对幅值) 封顶——相机远离原点时近场顶点抖动的根源。
    static void RenderOffsetNaiveF(const double* objAbs, const double* camAbs, float* out)
    {
        out[0] = static_cast<float>(objAbs[0]) - static_cast<float>(camAbs[0]);
        out[1] = static_cast<float>(objAbs[1]) - static_cast<float>(camAbs[1]);
        out[2] = static_cast<float>(objAbs[2]) - static_cast<float>(camAbs[2]);
    }
};

// ---- 抖动审计：0→flightM 直线飞行（每步 stepM，double 累计），近场物体恒在
// 相机前方 nearOffsetM 处（objAbs = camAbs + nearOffsetM——模拟"贴着相机的
// 近场顶点"，如地形块锚点/编队物体；真实相对位置恒为 nearOffsetM）。逐帧对比：
//   naive     = float(float(objAbs)) - float(float(camAbs)) 的相对位置误差
//   relocated = float(objAbs - camAbs)（double 域相减）的相对位置误差
// 返回两者的全程最大误差。预期：naive ≈ ULP(flightM)（mm 级），
// relocated ≈ ULP(nearOffsetM)/2（µm 级）——重定位消除顶点级抖动的数值证据。
struct JitterAuditResult {
    int steps = 0;
    double naiveMaxErrM = 0.0;     // 无重定位：相对位置最大量化误差（m）
    double relocatedMaxErrM = 0.0; // 重定位后：同口径最大误差（m）
    double ulpAtFlightM = 0.0;     // ULP(flightM)——naive 误差的理论上界
};

inline JitterAuditResult JitterAudit(double flightM, double stepM, double nearOffsetM)
{
    JitterAuditResult result;
    result.ulpAtFlightM = FloatUlpAt(flightM);
    const int steps = (stepM > 0.0 && flightM > 0.0)
        ? static_cast<int>(flightM / stepM)
        : 0;
    result.steps = steps;
    double cam[3] = { 0.0, 0.0, 0.0 };
    for (int i = 0; i < steps; ++i) {
        cam[0] += stepM; // double 累计（重定位路径的相机状态）
        const double obj[3] = { cam[0] + nearOffsetM, 0.0, 0.0 };
        float relNaive[3] = {};
        float relReloc[3] = {};
        FloatingOrigin::RenderOffsetNaiveF(obj, cam, relNaive);
        FloatingOrigin::RenderOffsetF(obj, cam, relReloc);
        const double errNaive = std::fabs(static_cast<double>(relNaive[0]) - nearOffsetM);
        const double errReloc = std::fabs(static_cast<double>(relReloc[0]) - nearOffsetM);
        if (errNaive > result.naiveMaxErrM) {
            result.naiveMaxErrM = errNaive;
        }
        if (errReloc > result.relocatedMaxErrM) {
            result.relocatedMaxErrM = errReloc;
        }
    }
    return result;
}

// =============================================================================
// ② 球面重力积分器（a = -μ·r̂/r²）
// =============================================================================

// 引力加速度（r 相对行星中心，m；μ 可替换以适配缩放单位测试）
inline Vec3d GravityAccel(const Vec3d& r, double mu)
{
    const double rLen = VecLen(r);
    const double invCube = 1.0 / (rLen * rLen * rLen); // 1/r³
    return VecScale(r, -mu * invCube);                 // -μ·r̂/r²
}

// 引力状态：r = pos - planetCenter（m），v（m/s）
struct GravityState {
    Vec3d r;
    Vec3d v;
};

// 活力公式轨道要素（二体问题解析基准；ε<0 束缚轨道）：
//   ε = v²/2 - μ/r；a = -μ/(2ε)；h = |r×v|；e = √(1+2εh²/μ²)；
//   近地点 r_p = a(1-e)，远地点 r_a = a(1+e)。
struct OrbitInfo {
    double eps = 0.0;     // 比机械能 J/kg
    double sma = 0.0;     // 半长轴 a（m）
    double ecc = 0.0;     // 离心率 e
    double perigeeM = 0.0; // a(1-e)（m）
    double apogeeM = 0.0;  // a(1+e)（m）
    double hMag = 0.0;     // 比角动量 |r×v|（m²/s）
};

inline OrbitInfo OrbitFromState(const GravityState& s, double mu)
{
    OrbitInfo o;
    const double rLen = VecLen(s.r);
    const double v2 = VecDot(s.v, s.v);
    o.eps = 0.5 * v2 - mu / rLen;
    const Vec3d h = VecCross(s.r, s.v);
    o.hMag = VecLen(h);
    o.sma = -mu / (2.0 * o.eps);
    const double term = 1.0 + 2.0 * o.eps * o.hMag * o.hMag / (mu * mu);
    o.ecc = std::sqrt(term > 0.0 ? term : 0.0);
    o.perigeeM = o.sma * (1.0 - o.ecc);
    o.apogeeM = o.sma * (1.0 + o.ecc);
    return o;
}

// 轨道周期 T = 2π√(a³/μ)（束缚轨道；ε≥0 时返回 -1）
inline double OrbitPeriodS(const OrbitInfo& o, double mu)
{
    if (o.eps >= 0.0 || o.sma <= 0.0) {
        return -1.0;
    }
    return 2.0 * 3.14159265358979323846 * std::sqrt(o.sma * o.sma * o.sma / mu);
}

// velocity Verlet 数值积分结果
struct IntegrateResult {
    std::vector<Vec3d> pathAbs; // 采样轨迹（绝对坐标 = planetCenterAbs + r）
    double rMinM = 0.0;         // |r| 最小值（近地点数值轨迹）
    double rMaxM = 0.0;         // |r| 最大值（远地点数值轨迹）
    double eps0 = 0.0;          // 初始比机械能
    double epsDriftMax = 0.0;   // max|ε(t) - ε0|（能量守恒断言输入）
    double h0 = 0.0;            // 初始比角动量
    double hDriftMax = 0.0;     // max||h(t)| - h0|（角动量守恒断言输入）
    bool impacted = false;      // |r| < impactRadiusM 提前终止（撞击地面）
    int steps = 0;              // 实际积分步数
    double elapsedS = 0.0;      // 模拟历时
};

// velocity Verlet（辛积分；dt 恒定）：
//   r' = r + v·dt + a(r)·dt²/2；a' = a(r')；v' = v + (a+a')·dt/2
// impactRadiusM > 0：|r| < impactRadiusM 视为撞击并终止（行星表面 r=R；
// 发射点抬高 2m 防首步误判）。sampleEvery > 0 时按步数间隔采样 pathAbs。
inline IntegrateResult IntegrateGravity(const GravityState& s0, double mu, double dt,
    int maxSteps, double impactRadiusM, int sampleEvery, const Vec3d& planetCenterAbs)
{
    IntegrateResult res;
    GravityState s = s0;
    const OrbitInfo o0 = OrbitFromState(s, mu);
    res.eps0 = o0.eps;
    res.h0 = o0.hMag;
    res.rMinM = VecLen(s.r);
    res.rMaxM = res.rMinM;
    Vec3d acc = GravityAccel(s.r, mu);
    if (sampleEvery > 0) {
        res.pathAbs.push_back(VecAdd(planetCenterAbs, s.r));
    }
    for (int i = 0; i < maxSteps; ++i) {
        const Vec3d rNew = VecAdd(VecAdd(s.r, VecScale(s.v, dt)),
            VecScale(acc, 0.5 * dt * dt));
        const Vec3d accNew = GravityAccel(rNew, mu);
        const Vec3d vNew = VecAdd(s.v, VecScale(VecAdd(acc, accNew), 0.5 * dt));
        s.r = rNew;
        s.v = vNew;
        acc = accNew;
        res.steps = i + 1;
        res.elapsedS = static_cast<double>(res.steps) * dt;
        const double rLen = VecLen(s.r);
        if (rLen < res.rMinM) {
            res.rMinM = rLen;
        }
        if (rLen > res.rMaxM) {
            res.rMaxM = rLen;
        }
        const OrbitInfo o = OrbitFromState(s, mu);
        const double epsDrift = std::fabs(o.eps - res.eps0);
        const double hDrift = std::fabs(o.hMag - res.h0);
        if (epsDrift > res.epsDriftMax) {
            res.epsDriftMax = epsDrift;
        }
        if (hDrift > res.hDriftMax) {
            res.hDriftMax = hDrift;
        }
        if (sampleEvery > 0 && (res.steps % sampleEvery) == 0) {
            res.pathAbs.push_back(VecAdd(planetCenterAbs, s.r));
        }
        if (impactRadiusM > 0.0 && rLen < impactRadiusM) {
            res.impacted = true;
            break;
        }
    }
    return res;
}

// =============================================================================
// ③ 球面几何助手（程序化行星网格双精度建模）
// =============================================================================

// 切平面锚点基（球面点 A 处的局部切平面）：up = normalize(A - C)；
// e1/e2 为切平面正交基（e1 取世界 +X 与 up 的叉积方向——up 接近 ±X 时退化，
// 本切片锚点始终贴近世界 +Y 半球，不触发退化）。
struct TangentBasis {
    Vec3d up;
    Vec3d e1;
    Vec3d e2;
};

inline TangentBasis TangentBasisAt(const Vec3d& anchorAbs, const Vec3d& planetCenterAbs)
{
    TangentBasis b;
    b.up = VecSub(anchorAbs, planetCenterAbs);
    const double upLen = VecLen(b.up);
    b.up = VecScale(b.up, 1.0 / upLen);
    // e1 = normalize(cross(+X, up))——锚点在 +Y 半球时非退化
    b.e1 = VecCross({ 1.0, 0.0, 0.0 }, b.up);
    const double e1Len = VecLen(b.e1);
    b.e1 = VecScale(b.e1, 1.0 / e1Len);
    b.e2 = VecCross(b.up, b.e1);
    return b;
}

// 切平面偏移 (t1, t2)（相对锚点，m）→ 球面点绝对坐标（精确球面，双精度）：
//   d = R·up + t1·e1 + t2·e2；p = C + R·d/|d|
// 同时给出：高程 h = R·(R/|d| - 1)（相对锚点切平面，≤0——曲率下沉量）与
// 球面法线 n = d/|d|。这就是"贴地看球面地平线曲率"的几何来源：
// 偏移 t 处的曲率下沉 ≈ t²/(2R)（24km 处 ≈ 45m，双精度保留、float 渲染）。
struct SpherePointResult {
    Vec3d posAbs;
    double heightM = 0.0;
    Vec3d normal;
};

inline SpherePointResult SpherePointFromTangent(const Vec3d& planetCenterAbs,
    const TangentBasis& basis, double t1, double t2)
{
    SpherePointResult out;
    const Vec3d d = VecAdd(VecAdd(VecScale(basis.up, kPlanetRadiusM),
        VecScale(basis.e1, t1)), VecScale(basis.e2, t2));
    const double dLen = VecLen(d);
    out.normal = VecScale(d, 1.0 / dLen);
    out.posAbs = VecAdd(planetCenterAbs, VecScale(out.normal, kPlanetRadiusM));
    out.heightM = kPlanetRadiusM * (kPlanetRadiusM / dLen - 1.0);
    return out;
}

// =============================================================================
// ④ cube-sphere chunk 地形（TASK-B-TECH-04 增量——上文①②③语义零改动）
//
// 四块能力（--planet2 模式与 test_planet.cpp doctest 共享同一实现）：
//   ① cube-sphere 映射：立方体六面 grid → 标准化立方体映射（面内点归一化）贴球，
//      避免极点挤压；六面基右手系（t1×t2=n），面棱两侧 u/v 精确对应（±1 端点
//      恰为小整数运算——跨面共享棱顶点方向逐位一致，见 CubeSphereDir 注）。
//   ② 高度噪声纯函数：整数格点哈希（种子混入）→ value noise（quintic fade）→
//      4 octave fBm。同种子同输入恒同输出（确定性），不同种子输出不同（可配）。
//      顶点高度 = f(单位球方向)——与 chunk/LOD/采样路由完全解耦：同点无论由
//      哪个 chunk 哪一档计算，同方向位形必得同高度（LOD 切换前后同点高度一致
//      的数学基础）。
//   ③ chunk 顶点规范采样：面内 chunk 格 (cx,cy) 的 res×res 顶点网格以全局索引
//      g = cx·stride + i（stride = res−1，chunk 间共享边顶点）表达；面坐标
//      u = 2g/total − 1（total = 面内 chunk 数·stride）。相邻 chunk 的共享边
//      由同一整数 g 表达 → 同一 double 表达式 → u/v 逐位一致 → 球面点/高度/
//      绝对坐标逐位一致（同档无缝的构造性保证；跨档 T 型连接由渲染侧裙边
//      遮蔽——选型见任务日志）。
//   ④ LOD 两档迟滞选择：dist < nearDist − hyst 进入近档（高分）；dist >
//      nearDist + hyst 退回远档（低分）；迟滞带内保持原档位（阈值附近无抖动
//      切换）。纯函数，状态仅 prevLod。
// =============================================================================

// ---- 地形噪声常量 ---------------------------------------------------------------
constexpr uint32_t kTerrainNoiseOctaves = 4u;  // fBm 层数（幅值逐半衰减）
constexpr double kTerrainBaseWavelengthM = 36000.0; // 基频地形主波长（36km）
constexpr double kTerrainMaxAmpM = 2400.0;     // 地形起伏幅值上界（±2.4km）

// ---- uint32 混合哈希（PCG 风格 avalanche；确定性纯函数）--------------------------
inline uint32_t PlanetHashU32(uint32_t x)
{
    x ^= x >> 16;
    x *= 0x7feb352du;
    x ^= x >> 15;
    x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

// 三维整数格点 → [0,1) 标量。种子混入首级；坐标逐级级联（负坐标按补码回绕——
// 确定性不受影响：同格点恒同输出）。
inline double PlanetLatticeValue(int32_t ix, int32_t iy, int32_t iz, uint32_t seed)
{
    uint32_t h = PlanetHashU32(seed ^ 0x9e3779b9u);
    h = PlanetHashU32(h ^ (0x85ebca6bu + static_cast<uint32_t>(ix)));
    h = PlanetHashU32(h ^ (0xc2b2ae35u + static_cast<uint32_t>(iy)));
    h = PlanetHashU32(h ^ (0x27d4eb2fu + static_cast<uint32_t>(iz)));
    return static_cast<double>(h) * (1.0 / 4294967296.0);
}

// 3D value noise（quintic fade 三线性插值），输出 [0,1]。
inline double PlanetValueNoise(double x, double y, double z, uint32_t seed)
{
    const double fx = std::floor(x);
    const double fy = std::floor(y);
    const double fz = std::floor(z);
    const int32_t ix = static_cast<int32_t>(fx);
    const int32_t iy = static_cast<int32_t>(fy);
    const int32_t iz = static_cast<int32_t>(fz);
    const double tx = x - fx;
    const double ty = y - fy;
    const double tz = z - fz;
    const double ux = tx * tx * tx * (tx * (tx * 6.0 - 15.0) + 10.0);
    const double uy = ty * ty * ty * (ty * (ty * 6.0 - 15.0) + 10.0);
    const double uz = tz * tz * tz * (tz * (tz * 6.0 - 15.0) + 10.0);
    const double v000 = PlanetLatticeValue(ix, iy, iz, seed);
    const double v100 = PlanetLatticeValue(ix + 1, iy, iz, seed);
    const double v010 = PlanetLatticeValue(ix, iy + 1, iz, seed);
    const double v110 = PlanetLatticeValue(ix + 1, iy + 1, iz, seed);
    const double v001 = PlanetLatticeValue(ix, iy, iz + 1, seed);
    const double v101 = PlanetLatticeValue(ix + 1, iy, iz + 1, seed);
    const double v011 = PlanetLatticeValue(ix, iy + 1, iz + 1, seed);
    const double v111 = PlanetLatticeValue(ix + 1, iy + 1, iz + 1, seed);
    const double z00 = v000 + (v100 - v000) * ux;
    const double z10 = v010 + (v110 - v010) * ux;
    const double z01 = v001 + (v101 - v001) * ux;
    const double z11 = v011 + (v111 - v011) * ux;
    const double y0 = z00 + (z10 - z00) * uy;
    const double y1 = z01 + (z11 - z01) * uy;
    return y0 + (y1 - y0) * uz;
}

// 地形高度纯函数：输入单位球方向（调用方保证 |d|≈1），输出高程 m ∈
// [−kTerrainMaxAmpM, +kTerrainMaxAmpM]。4 octave fBm（输入偏移避开原点对称性）。
// 基频换算：方向分量 1 ≈ 弧长 R（行星半径）——噪声格胞 1 对应地表
// kTerrainBaseWavelengthM 米，故 baseFreq = R / 波长（≈177/半径）。
// 确定性契约：同 (seed, dx, dy, dz) 位形 → 同输出位形（doctest 逐位断言）。
inline double PlanetTerrainHeightM(uint32_t seed, double dx, double dy, double dz)
{
    double sum = 0.0;
    double amp = 1.0;
    double norm = 0.0;
    const double baseFreq = kPlanetRadiusM / kTerrainBaseWavelengthM;
    double freq = baseFreq;
    for (uint32_t o = 0; o < kTerrainNoiseOctaves; ++o) {
        const double n = PlanetValueNoise(dx * freq + 11.3, dy * freq + 7.7,
            dz * freq + 3.1, seed + o * 1013904223u);
        sum += amp * (n - 0.5);
        norm += amp;
        amp *= 0.5;
        freq *= 2.0;
    }
    return (sum / norm) * 2.0 * kTerrainMaxAmpM;
}

// ---- cube-sphere 六面基 -----------------------------------------------------------
// 面序：0=+Z 1=+X 2=−Z 3=−X 4=+Y 5=−Y。t1 = u 方向、t2 = v 方向，右手系
// （t1×t2 = n）。侧四面共享 t2=+Y 使相邻侧面在共享棱上 v 同轴（棱对应简单）；
// 上下面 t2 沿 ∓Z 使与四个侧面的棱对应为 v ↔ ∓1 端点（小整数运算，逐位一致）。
struct CubeFaceBasis {
    Vec3d n;  // 面法线（单位）
    Vec3d t1; // u 方向（单位）
    Vec3d t2; // v 方向（单位）
};

inline CubeFaceBasis CubeFaceBasisOf(int faceId)
{
    CubeFaceBasis b;
    switch (faceId) {
    case 0:  b.n = { 0.0, 0.0, 1.0 };  b.t1 = { 1.0, 0.0, 0.0 };  b.t2 = { 0.0, 1.0, 0.0 }; break;
    case 1:  b.n = { 1.0, 0.0, 0.0 };  b.t1 = { 0.0, 0.0, -1.0 }; b.t2 = { 0.0, 1.0, 0.0 }; break;
    case 2:  b.n = { 0.0, 0.0, -1.0 }; b.t1 = { -1.0, 0.0, 0.0 }; b.t2 = { 0.0, 1.0, 0.0 }; break;
    case 3:  b.n = { -1.0, 0.0, 0.0 }; b.t1 = { 0.0, 0.0, 1.0 };  b.t2 = { 0.0, 1.0, 0.0 }; break;
    case 4:  b.n = { 0.0, 1.0, 0.0 };  b.t1 = { 1.0, 0.0, 0.0 };  b.t2 = { 0.0, 0.0, -1.0 }; break;
    default: b.n = { 0.0, -1.0, 0.0 }; b.t1 = { 1.0, 0.0, 0.0 };  b.t2 = { 0.0, 0.0, 1.0 }; break;
    }
    return b;
}

// 标准化立方体映射：面坐标 (u,v ∈ [−1,1]) → 单位球方向。
// p = n + u·t1 + v·t2（立方体面上的点）→ p/|p|。
// 跨面棱逐位一致性：共享棱两侧 (u,v) 均恰为 ±1 端点，p 的分量全为小整数
// 加乘（±1·±1 等），IEEE754 下两侧算得逐位相同的有理分量 → 归一化后逐位相同。
inline Vec3d CubeSphereDir(int faceId, double u, double v)
{
    const CubeFaceBasis b = CubeFaceBasisOf(faceId);
    const Vec3d p = VecAdd(VecAdd(b.n, VecScale(b.t1, u)), VecScale(b.t2, v));
    return VecScale(p, 1.0 / VecLen(p));
}

// ---- chunk 顶点规范采样 -------------------------------------------------------------
// chunk (cx,cy)（面内 chunk 格坐标，0..cellsPerEdge−1）res×res 顶点网格的
// 全局顶点索引：g = cx·stride + i（stride = res−1；chunk 间共享边顶点复用）。
// 相邻 chunk 共享边：A 的 i=stride 与 B(cx+1) 的 i=0 同为 g=(cx+1)·stride——
// 同一整数 → 同一 double 表达式 → 面坐标逐位一致（无缝的构造性基础）。
inline int32_t ChunkVertexIndex(int32_t c, int32_t i, int32_t stride)
{
    return c * stride + i;
}

// 全局顶点索引 → 面坐标（规范 double 表达式；g/total 均为精确整数 double，
// 商为正确舍入结果——跨档共享端点（(cx+1)/cellsPerEdge 同有理数）亦逐位一致）。
inline double ChunkFaceCoord(int32_t g, int32_t total)
{
    return 2.0 * static_cast<double>(g) / static_cast<double>(total) - 1.0;
}

// chunk 顶点完整语义：球面方向 + 噪声高程 + 绝对坐标（行星中心系，double）。
// pos = center + dir·(R + h)——位移沿球面法线（渲染光照法线 = dir）。
struct PlanetChunkVertex {
    Vec3d posAbs;  // 绝对坐标（含地形位移）
    Vec3d normal;  // 球面法线（= 归一化方向；未含地形坡度）
    double heightM; // 噪声高程 m
};

inline PlanetChunkVertex PlanetChunkVertexAt(const Vec3d& planetCenter, int faceId,
    double u, double v, uint32_t seed)
{
    PlanetChunkVertex out;
    out.normal = CubeSphereDir(faceId, u, v);
    out.heightM = PlanetTerrainHeightM(seed, out.normal.x, out.normal.y, out.normal.z);
    out.posAbs = VecAdd(planetCenter, VecScale(out.normal, kPlanetRadiusM + out.heightM));
    return out;
}

// 规范路由（chunk 网格顶点）：全局索引 → 面坐标 → 顶点。app 与 doctest 均经
// 此函数，保证「共享边逐位一致」断言的就是渲染走的同一条路径。
inline PlanetChunkVertex PlanetChunkVertexAtGlobal(const Vec3d& planetCenter,
    int faceId, int32_t gU, int32_t gV, int32_t total, uint32_t seed)
{
    return PlanetChunkVertexAt(planetCenter, faceId, ChunkFaceCoord(gU, total),
        ChunkFaceCoord(gV, total), seed);
}

// ---- LOD 两档迟滞选择 ---------------------------------------------------------------
// 返回 1=近档（高分 64×64）/ 0=远档（低分 16×16）。迟滞：进近档需严格
// dist < nearDist − hyst；退远档需严格 dist > nearDist + hyst；带内保持原档。
// 边界语义（doctest 锁定）：dist 恰等于 nearDist ∓ hyst 时均不切换（严格不等式）。
inline int PlanetLodLevel(int prevLod, double distM, double nearDistM, double hystM)
{
    if (prevLod >= 1) {
        return (distM > nearDistM + hystM) ? 0 : 1;
    }
    return (distM < nearDistM - hystM) ? 1 : 0;
}

// 球面地平线距离（对地高度 altM）：√((R+alt)² − R²)（精确式，含 alt² 项）。
inline double PlanetHorizonDistM(double altM)
{
    const double r = kPlanetRadiusM + altM;
    return std::sqrt(r * r - kPlanetRadiusM * kPlanetRadiusM);
}

} // namespace zplanet
