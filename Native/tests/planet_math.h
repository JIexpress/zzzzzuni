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

} // namespace zplanet
