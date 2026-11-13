#pragma once
// =============================================================================
// planet_math.h —— tests 本地薄 include-shim（TASK-B-PLANET-03 生产化提升后）
//
// §①（浮点原点）§③（球面几何）§④（cube-sphere/chunk/LOD/地平线）已提升至
// 生产目录 Native/zplanet/planet_math.h（语义零改动），本文件不再持有其实现，
// 仅转发生产头。保留本 shim 的两个原因：
//   1. §②（球面重力积分器）**不提升**——原生通道不含物理（设计稿 §12-1 明确
//      不做清单），连同其专属常量 kGravityMu 一起留在 tests（见下方原文代码）；
//   2. 消费者 tests/zrhi_d3d12_window.cpp（--planet/--planet2 窗口模式，本任务
//      白名单之外不可改）仍 #include "planet_math.h"——薄 shim 防其断链，
//      且窗口程序与 doctest 继续验证同一份 §①③④ 实现（生产头）。
// 消费者清单（TASK-B-PLANET-03 施工时查证）：
//   - Native/tests/test_planet.cpp（doctest；已改指生产头，另经本文件取 §②）
//   - Native/tests/zrhi_d3d12_window.cpp（--planet/--planet2 窗口模式；仍经本 shim）
//   - Native/zplugin/zplugin.h 仅注释提及（:417），无 #include、无代码依赖
// =============================================================================

#include "zplanet/planet_math.h" // 生产头：§①③④ + Vec3d/向量算术/FloatUlpAt/kPlanetRadiusM

#include <cmath>  // §② std::sqrt / std::fabs
#include <vector> // §② IntegrateResult::pathAbs

namespace zplanet {

// ---- 行星常量（地球参数，简报口径）——§② 重力专属 μ 留在 tests ----------------
constexpr double kGravityMu = 3.986004418e14; // 引力常数 μ，m³/s²

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

} // namespace zplanet
