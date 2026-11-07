// =============================================================================
// test_planet.cpp —— TASK-B-TECH-03 行星尺度最小切片纯逻辑单测
//
// 覆盖（与 tests/planet_math.h 共享实现——窗口程序 --planet 验证同一份代码）：
//   - 浮点原点：double→float 相对化精度（100km 飞行抖动审计：naive mm 级 →
//     重定位后 µm 级）；ULP 幅值表；naive 误差不超过 ULP(幅值) 上界
//   - 重定位阈值：严格大于判据（恰在阈值不触发/斜向欧氏距离判定）；
//     重定位后渲染偏移逐位不变；物体相对新原点回到阈值量级内
//   - 球面几何：切平面偏移 → 球面点（半径 6.371e6m）曲率下沉 ≈ t²/(2R)；
//     法线/高程自洽
//   - 球面重力：椭圆轨道能量/角动量守恒（velocity Verlet）+ 近/远地点符合
//     活力公式（容差内）；抛射体弹道远地点符合能量守恒预期 + 撞击检测
// =============================================================================

#include "doctest/doctest.h"

#include "planet_math.h"

#include <cmath>

// =============================================================================
// 浮点原点重定位
// =============================================================================

TEST_CASE("浮点原点: 100km 飞行抖动审计——naive mm 级抖动 vs 重定位后 µm 级")
{
    const zplanet::JitterAuditResult r = zplanet::JitterAudit(100000.0, 1.0, 10.37);
    CHECK(r.steps == 100000);
    // ULP(100000) = 2^-7 = 0.0078125（naive 相对位置误差的理论上界）
    CHECK(r.ulpAtFlightM == doctest::Approx(0.0078125).epsilon(0.001));
    // naive 全 float 大坐标相减：抖动真实发生（mm 级）且不超 ULP 上界
    CHECK(r.naiveMaxErrM > 0.001);
    CHECK(r.naiveMaxErrM <= r.ulpAtFlightM + 1e-9);
    // 重定位后（double 域相对化）：恢复 float 在近场幅值的精度（µm 级）
    CHECK(r.relocatedMaxErrM < 1e-6);
    CHECK(r.relocatedMaxErrM >= 0.0);
    // 改善 ≥ 两个数量级（实测 ~10³×）
    CHECK(r.naiveMaxErrM > 100.0 * r.relocatedMaxErrM);
}

TEST_CASE("浮点原点: ULP 幅值表——量化误差随幅值增长（抖动的分辨率根源）")
{
    // float 尾数 23 显式位：x ∈ [2^e, 2^(e+1)) 处 ULP(x) = 2^(e-23)（2 的幂处精确）
    CHECK(zplanet::FloatUlpAt(65536.0) == doctest::Approx(0.0078125).epsilon(1e-9));   // 2^-7
    CHECK(zplanet::FloatUlpAt(131072.0) == doctest::Approx(0.015625).epsilon(1e-9));   // 2^-6
    CHECK(zplanet::FloatUlpAt(100000.0) == doctest::Approx(0.0078125).epsilon(0.001));
    CHECK(zplanet::FloatUlpAt(1000.0) < 0.0001);   // 1km 处 ~6.1e-5
    CHECK(zplanet::FloatUlpAt(10.0) < 1e-6);       // 10m 处 ~9.5e-7
    // 单调性：幅值越大分辨率越粗
    CHECK(zplanet::FloatUlpAt(100000.0) > 1000.0 * zplanet::FloatUlpAt(10.0));
}

TEST_CASE("浮点原点: 重定位触发阈值——严格大于 + 斜向欧氏距离判定")
{
    using zplanet::FloatingOrigin;
    const double origin[3] = { 0.0, 0.0, 0.0 };
    const double threshold = 10000.0;
    // 恰在阈值：不触发（严格 >，避免阈值上反复重定位）
    const double camAtThreshold[3] = { threshold, 0.0, 0.0 };
    CHECK_FALSE(FloatingOrigin::ShouldRelocate(camAtThreshold, origin, threshold));
    // 超阈值：触发
    const double camBeyond[3] = { threshold + 0.5, 0.0, 0.0 };
    CHECK(FloatingOrigin::ShouldRelocate(camBeyond, origin, threshold));
    // 斜向：|offset| 判据而非逐轴——(6000,6000,6000) 模长 10392 > 10000 触发
    const double camDiag[3] = { 6000.0, 6000.0, 6000.0 };
    CHECK(FloatingOrigin::ShouldRelocate(camDiag, origin, threshold));
    // (5000,5000,5000) 模长 8660 < 10000 不触发
    const double camDiag2[3] = { 5000.0, 5000.0, 5000.0 };
    CHECK_FALSE(FloatingOrigin::ShouldRelocate(camDiag2, origin, threshold));
    // 负向同样触发（对称性）
    const double camNeg[3] = { -threshold - 1.0, 0.0, 0.0 };
    CHECK(FloatingOrigin::ShouldRelocate(camNeg, origin, threshold));
}

TEST_CASE("浮点原点: 重定位后渲染偏移逐位不变 + 物体回到原点附近 + 计数账目")
{
    zplanet::FloatingOrigin fo;
    fo.thresholdM = 10000.0;
    // 物体：绝对坐标 (20000, 5, 3000)（相机将飞到它附近）
    const double objAbs[3] = { 20000.0, 5.0, 3000.0 };
    // 相机 double 累计飞到 (19998, 2, 3001)——距原点 ~20221m > 10km
    fo.MoveCameraBy(19998.0, 2.0, 3001.0);
    CHECK(fo.maxOriginOffsetM == doctest::Approx(20221.6).epsilon(0.001));
    REQUIRE(fo.ShouldRelocateNow());
    float relBefore[3] = {};
    zplanet::FloatingOrigin::RenderOffsetF(objAbs, fo.camAbs, relBefore);
    const double offset = fo.Relocate();
    CHECK(offset == doctest::Approx(20221.6).epsilon(0.001));
    CHECK(fo.relocationCount == 1);
    CHECK(fo.lastRelocationOffsetM == doctest::Approx(offset).epsilon(1e-9));
    CHECK_FALSE(fo.ShouldRelocateNow()); // 原点=相机 → 立即复位不触发
    // 渲染偏移逐位不变（重定位只动原点账目，不动绝对坐标与相对几何）
    float relAfter[3] = {};
    zplanet::FloatingOrigin::RenderOffsetF(objAbs, fo.camAbs, relAfter);
    CHECK(std::memcmp(relBefore, relAfter, sizeof(relBefore)) == 0);
    // 物体相对新原点回到阈值量级内（「所有物体 transform 平移回原点附近」）
    const zplanet::Vec3d relOrigin = zplanet::VecSub({ objAbs[0], objAbs[1], objAbs[2] },
        { fo.originAbs[0], fo.originAbs[1], fo.originAbs[2] });
    CHECK(zplanet::VecLen(relOrigin) < fo.thresholdM + 100.0);
    // 连续飞行再触发：计数与账目单调（恰在阈值不触发——位移须严格越过）
    fo.MoveCameraBy(10000.0, 0.0, 0.0);
    CHECK_FALSE(fo.ShouldRelocateNow()); // 恰 10km：严格 > 判据
    fo.MoveCameraBy(0.5, 0.0, 0.0);
    CHECK(fo.ShouldRelocateNow());
    CHECK(fo.Relocate() == doctest::Approx(10000.5).epsilon(1e-9));
    CHECK(fo.relocationCount == 2);
    // 相对偏移的 double 精度与「谁减谁」无关（绝对语义自洽）
    CHECK(fo.camAbs[0] == doctest::Approx(29998.5).epsilon(1e-9));
    CHECK(fo.originAbs[0] == doctest::Approx(29998.5).epsilon(1e-9));
}

// =============================================================================
// 球面几何（行星渲染雏形的数学）
// =============================================================================

TEST_CASE("球面几何: 切平面偏移 → 球面点——曲率下沉符合 t²/(2R) 与精确球面")
{
    const zplanet::Vec3d center = { 0.0, -zplanet::kPlanetRadiusM, 0.0 };
    const zplanet::Vec3d anchor = { 0.0, 0.0, 0.0 }; // 切点在原点正上方
    const zplanet::TangentBasis basis = zplanet::TangentBasisAt(anchor, center);
    // 基自洽：单位长 + 正交
    CHECK(zplanet::VecLen(basis.up) == doctest::Approx(1.0).epsilon(1e-12));
    CHECK(zplanet::VecDot(basis.up, basis.e1) == doctest::Approx(0.0).epsilon(1e-12));
    CHECK(zplanet::VecDot(basis.e1, basis.e2) == doctest::Approx(0.0).epsilon(1e-12));
    // 锚点本身：高程 0、法线 = up
    const zplanet::SpherePointResult at0
        = zplanet::SpherePointFromTangent(center, basis, 0.0, 0.0);
    CHECK(at0.heightM == doctest::Approx(0.0).epsilon(1e-9));
    CHECK(at0.posAbs.x == doctest::Approx(0.0).epsilon(1e-9));
    CHECK(at0.posAbs.y == doctest::Approx(0.0).epsilon(1e-9));
    // 24km 处：曲率下沉 ≈ t²/(2R) = 5.76e8/1.2742e7 ≈ 45.2m（球面精确解应吻合）
    const zplanet::SpherePointResult at24k
        = zplanet::SpherePointFromTangent(center, basis, 24000.0, 0.0);
    CHECK(at24k.heightM < 0.0);
    CHECK(at24k.heightM == doctest::Approx(-24000.0 * 24000.0
        / (2.0 * zplanet::kPlanetRadiusM)).epsilon(0.001));
    // 精确球面：|pos - center| == R
    CHECK(zplanet::VecLen(zplanet::VecSub(at24k.posAbs, center))
        == doctest::Approx(zplanet::kPlanetRadiusM).epsilon(1e-12));
    // 法线 = (pos-center)/R
    const zplanet::Vec3d nExpected = zplanet::VecScale(
        zplanet::VecSub(at24k.posAbs, center), 1.0 / zplanet::kPlanetRadiusM);
    CHECK(zplanet::VecDot(at24k.normal, nExpected)
        == doctest::Approx(1.0).epsilon(1e-12));
    // 双精度下 2m 贴地高度可分辨（float 在 6.4e6 幅值处 ULP=0.5m——为何行星
    // 中心必须 double 的数值理由）
    const zplanet::SpherePointResult at2m
        = zplanet::SpherePointFromTangent(center, basis, 5048.0, 0.0);
    CHECK(at2m.heightM == doctest::Approx(-5048.0 * 5048.0
        / (2.0 * zplanet::kPlanetRadiusM)).epsilon(0.001));
    CHECK(at2m.heightM > -3.0); // 5km 处下沉 ~2m（地平线距离 √(2Rh)≈5km 的来源）
}

// =============================================================================
// 球面重力积分器
// =============================================================================

TEST_CASE("球面重力: 椭圆轨道——能量/角动量守恒 + 近远地点符合活力公式")
{
    // r0 = R+200km（地表上方 200km），v0 = 7750 m/s 切向（< 环绕速度 7792）
    // → 椭圆轨道：近地点 ~6443km（地表上 ~72km），远地点 = r0
    zplanet::GravityState s0;
    s0.r = { 0.0, zplanet::kPlanetRadiusM + 200000.0, 0.0 };
    s0.v = { 0.0, 0.0, 7750.0 };
    const zplanet::OrbitInfo expect = zplanet::OrbitFromState(s0, zplanet::kGravityMu);
    CHECK(expect.eps < 0.0); // 束缚轨道
    CHECK(expect.perigeeM > zplanet::kPlanetRadiusM); // 近地点在地表之上（不撞地）
    CHECK(expect.apogeeM == doctest::Approx(zplanet::kPlanetRadiusM + 200000.0)
              .epsilon(1e-6)); // 初始点即远地点（切向速度 → 径向速度 0）
    const double periodS = zplanet::OrbitPeriodS(expect, zplanet::kGravityMu);
    REQUIRE(periodS > 0.0);
    // 积分 1.05 个周期（dt=0.5s；velocity Verlet）
    const int maxSteps = static_cast<int>(periodS * 1.05 / 0.5) + 1;
    const zplanet::IntegrateResult res = zplanet::IntegrateGravity(s0,
        zplanet::kGravityMu, 0.5, maxSteps, 0.0, 1, { 0.0, 0.0, 0.0 });
    CHECK_FALSE(res.impacted);
    CHECK(res.steps == maxSteps);
    // 能量守恒（velocity Verlet 有界振荡；显式 Euler 在此 dt/步数下漂移 ~1e-3 会被抓）
    CHECK(res.epsDriftMax / std::fabs(res.eps0) < 1e-6);
    // 角动量守恒（有心力）
    CHECK(res.hDriftMax / res.h0 < 1e-6);
    // 数值轨迹近/远地点 vs 活力公式解析值（相对容差 1e-4，远严于 dt 引入的 ~1e-6 误差）
    CHECK(std::fabs(res.rMinM - expect.perigeeM) / expect.perigeeM < 1e-4);
    CHECK(std::fabs(res.rMaxM - expect.apogeeM) / expect.apogeeM < 1e-4);
}

TEST_CASE("球面重力: 抛射体弹道——远地点符合能量守恒预期 + 撞击检测")
{
    // 地表 +2m 发射（防首步 |r|<R 误判），v0 = 1200 m/s、仰角 60°：
    // 水平 600 m/s（+Z）、竖直 1039.23 m/s（+Y；平面重力近似远地点 ~55km，
    // 球面重力下 g 随高度衰减 → 略高，由活力公式给解析值）
    zplanet::GravityState s0;
    s0.r = { 0.0, zplanet::kPlanetRadiusM + 2.0, 0.0 };
    s0.v = { 0.0, 1039.230484541326, 600.0 };
    const zplanet::OrbitInfo expect = zplanet::OrbitFromState(s0, zplanet::kGravityMu);
    CHECK(expect.eps < 0.0);
    const double apogeeAltM = expect.apogeeM - zplanet::kPlanetRadiusM;
    CHECK(apogeeAltM > 40000.0); // 量级合理（>40km）
    CHECK(apogeeAltM < 80000.0); // <80km（排除参数错位）
    const zplanet::IntegrateResult res = zplanet::IntegrateGravity(s0,
        zplanet::kGravityMu, 0.05, 20000, zplanet::kPlanetRadiusM, 16,
        { 0.0, -zplanet::kPlanetRadiusM, 0.0 });
    CHECK(res.impacted);           // 抛射体落回地面
    CHECK(res.rMaxM > res.rMinM);  // 有明显弹道拱点
    // 远地点（数值 rMax）vs 能量守恒解析预期（活力公式）：相对容差 1e-3
    CHECK(std::fabs(res.rMaxM - expect.apogeeM) / expect.apogeeM < 1e-3);
    // 弹道全程束缚在行星邻域
    CHECK(res.rMaxM < zplanet::kPlanetRadiusM + 200000.0);
    // 撞击即停：不深穿（dt=0.05s × 落速 ~1.2km/s → 首个 <R 步至多再下探 ~60m）
    CHECK(res.rMinM > zplanet::kPlanetRadiusM - 100.0);
    // 轨迹采样首点 = 发射点绝对坐标（行星中心 (0,-R,0) + r0）
    REQUIRE(res.pathAbs.size() >= 2u);
    CHECK(res.pathAbs[0].x == doctest::Approx(0.0).epsilon(1e-9));
    CHECK(res.pathAbs[0].y == doctest::Approx(2.0).epsilon(1e-6));
    CHECK(res.pathAbs[0].z == doctest::Approx(0.0).epsilon(1e-9));
    // 采样轨迹单调推进（z 向射程方向单调不减——发射方位 +Z）
    bool zMonotonic = true;
    for (size_t i = 1u; i < res.pathAbs.size(); ++i) {
        if (res.pathAbs[i].z < res.pathAbs[i - 1u].z - 1e-6) {
            zMonotonic = false;
        }
    }
    CHECK(zMonotonic);
    // 飞行历时量级（平面近似 2·v_z/g ≈ 212s；球面 + 高空低 g 略长）
    CHECK(res.elapsedS > 150.0);
    CHECK(res.elapsedS < 400.0);
}

TEST_CASE("球面重力: μ 参数化——几何相似缩放（r'=s·r, v'=v, μ'=s·μ, t'=s·t）")
{
    // 二体相似律：r'=s·r、v'=v（速度不变）、μ'=s·μ 时轨道几何逐点相似：
    //   ε 不变；a'=s·a；h'=s·h；e 不变；r_p'=s·r_p；r_a'=s·r_a；
    //   T'=2π√(s³a³/sμ)=s·T（dt'=s·dt——v'=dr'/dt' 不变的直接推论）
    const double scale = 1e-6;
    zplanet::GravityState s0;
    s0.r = { 0.0, zplanet::kPlanetRadiusM + 200000.0, 0.0 };
    s0.v = { 0.0, 0.0, 7750.0 };
    const zplanet::OrbitInfo full = zplanet::OrbitFromState(s0, zplanet::kGravityMu);
    zplanet::GravityState sScaled;
    sScaled.r = zplanet::VecScale(s0.r, scale);
    sScaled.v = s0.v; // 速度不变（相似律核心）
    const zplanet::OrbitInfo scaled
        = zplanet::OrbitFromState(sScaled, zplanet::kGravityMu * scale);
    CHECK(scaled.eps == doctest::Approx(full.eps).epsilon(1e-12));
    CHECK(scaled.ecc == doctest::Approx(full.ecc).epsilon(1e-12));
    CHECK(scaled.sma == doctest::Approx(full.sma * scale).epsilon(1e-9));
    CHECK(scaled.perigeeM == doctest::Approx(full.perigeeM * scale).epsilon(1e-9));
    CHECK(scaled.apogeeM == doctest::Approx(full.apogeeM * scale).epsilon(1e-9));
    // 缩放尺度下积分一个周期（dt'=s·dt），守恒性同步成立
    const double periodScaled = zplanet::OrbitPeriodS(scaled, zplanet::kGravityMu * scale);
    REQUIRE(periodScaled
        == doctest::Approx(zplanet::OrbitPeriodS(full, zplanet::kGravityMu) * scale)
               .epsilon(1e-9));
    const double dt = 0.5 * scale;
    const int maxSteps = static_cast<int>(periodScaled * 1.05 / dt) + 1;
    const zplanet::IntegrateResult res = zplanet::IntegrateGravity(sScaled,
        zplanet::kGravityMu * scale, dt, maxSteps, 0.0, 0, { 0.0, 0.0, 0.0 });
    CHECK_FALSE(res.impacted);
    CHECK(res.epsDriftMax / std::fabs(res.eps0) < 1e-6);
    CHECK(std::fabs(res.rMinM - scaled.perigeeM) / scaled.perigeeM < 1e-4);
    CHECK(std::fabs(res.rMaxM - scaled.apogeeM) / scaled.apogeeM < 1e-4);
}
