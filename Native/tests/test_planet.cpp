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
#include <cstring>

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

// =============================================================================
// TASK-B-TECH-04 增量：cube-sphere chunk 地形 + 高度噪声 + LOD 两档迟滞
//（与 --planet2 窗口模式共享 planet_math.h §④ 实现）
// =============================================================================

TEST_CASE("cube-sphere: 六面基右手系 + 标准化映射方向归一/面心正确")
{
    const zplanet::Vec3d axes[6] = { { 0.0, 0.0, 1.0 }, { 1.0, 0.0, 0.0 },
        { 0.0, 0.0, -1.0 }, { -1.0, 0.0, 0.0 }, { 0.0, 1.0, 0.0 }, { 0.0, -1.0, 0.0 } };
    for (int f = 0; f < 6; ++f) {
        const zplanet::CubeFaceBasis b = zplanet::CubeFaceBasisOf(f);
        // 基单位长 + 两两正交 + 右手系（t1×t2 = n）
        CHECK(zplanet::VecLen(b.n) == doctest::Approx(1.0).epsilon(1e-12));
        CHECK(zplanet::VecLen(b.t1) == doctest::Approx(1.0).epsilon(1e-12));
        CHECK(zplanet::VecLen(b.t2) == doctest::Approx(1.0).epsilon(1e-12));
        CHECK(zplanet::VecDot(b.n, b.t1) == doctest::Approx(0.0).epsilon(1e-12));
        CHECK(zplanet::VecDot(b.n, b.t2) == doctest::Approx(0.0).epsilon(1e-12));
        CHECK(zplanet::VecDot(b.t1, b.t2) == doctest::Approx(0.0).epsilon(1e-12));
        const zplanet::Vec3d cross = zplanet::VecCross(b.t1, b.t2);
        CHECK(cross.x == doctest::Approx(b.n.x).epsilon(1e-12));
        CHECK(cross.y == doctest::Approx(b.n.y).epsilon(1e-12));
        CHECK(cross.z == doctest::Approx(b.n.z).epsilon(1e-12));
        // 面中心 (u,v)=(0,0) 方向 = 面法线；角点方向仍单位长
        const zplanet::Vec3d c = zplanet::CubeSphereDir(f, 0.0, 0.0);
        CHECK(c.x == doctest::Approx(axes[f].x).epsilon(1e-12));
        CHECK(c.y == doctest::Approx(axes[f].y).epsilon(1e-12));
        CHECK(c.z == doctest::Approx(axes[f].z).epsilon(1e-12));
        const zplanet::Vec3d corner = zplanet::CubeSphereDir(f, 1.0, 1.0);
        CHECK(zplanet::VecLen(corner) == doctest::Approx(1.0).epsilon(1e-12));
        // 球面半径不随映射畸变：面内任意点方向单位长（标准化映射的核心性质）
        for (int s = 0; s <= 4; ++s) {
            const double t = -1.0 + 0.5 * static_cast<double>(s);
            const zplanet::Vec3d d = zplanet::CubeSphereDir(f, t, t * 0.6);
            CHECK(zplanet::VecLen(d) == doctest::Approx(1.0).epsilon(1e-12));
        }
    }
}

TEST_CASE("cube-sphere: 跨面共享棱两侧方向逐位一致（±1 端点小整数运算）")
{
    // 基表（CubeFaceBasisOf）的棱对应关系：
    //   侧面链 0→1→2→3（u=+1 ↔ 下一面 u=−1，v 同轴 t2=+Y）；
    //   上/下面 ↔ 各侧面（面0 v=+1 ↔ 面4 v=−1、面0 v=−1 ↔ 面5 v=+1，u 同参数）。
    // 棱端点 (±1) 使面内点分量全为小整数加乘 → 两侧方向逐位相同（IEEE754）。
    for (int s = 0; s <= 4; ++s) {
        const double t = -1.0 + 0.5 * static_cast<double>(s);
        // 侧面链：0(u=+1)↔1(u=−1)、1(u=+1)↔2(u=−1)、2(u=+1)↔3(u=−1)，v 同值
        const zplanet::Vec3d e01a = zplanet::CubeSphereDir(0, 1.0, t);
        const zplanet::Vec3d e01b = zplanet::CubeSphereDir(1, -1.0, t);
        CHECK(std::memcmp(&e01a, &e01b, sizeof(e01a)) == 0);
        const zplanet::Vec3d e12a = zplanet::CubeSphereDir(1, 1.0, t);
        const zplanet::Vec3d e12b = zplanet::CubeSphereDir(2, -1.0, t);
        CHECK(std::memcmp(&e12a, &e12b, sizeof(e12a)) == 0);
        const zplanet::Vec3d e23a = zplanet::CubeSphereDir(2, 1.0, t);
        const zplanet::Vec3d e23b = zplanet::CubeSphereDir(3, -1.0, t);
        CHECK(std::memcmp(&e23a, &e23b, sizeof(e23a)) == 0);
        // 顶/底盖：0(v=+1)↔4(v=−1)、0(v=−1)↔5(v=+1)，u 同参数
        const zplanet::Vec3d e04a = zplanet::CubeSphereDir(0, t, 1.0);
        const zplanet::Vec3d e04b = zplanet::CubeSphereDir(4, t, -1.0);
        CHECK(std::memcmp(&e04a, &e04b, sizeof(e04a)) == 0);
        const zplanet::Vec3d e05a = zplanet::CubeSphereDir(0, t, -1.0);
        const zplanet::Vec3d e05b = zplanet::CubeSphereDir(5, t, 1.0);
        CHECK(std::memcmp(&e05a, &e05b, sizeof(e05a)) == 0);
    }
}

TEST_CASE("噪声纯函数: 同种子同输出逐位确定 + 种子可配 + 值域有界 + 空间连续")
{
    // 采样集：六面 9×9 网格方向（覆盖全部面，含跨面棱端点）
    auto sampleHeight = [](uint32_t seed, size_t* outCount) {
        double sum = 0.0;
        size_t n = 0;
        for (int f = 0; f < 6; ++f) {
            for (int j = 0; j <= 8; ++j) {
                for (int i = 0; i <= 8; ++i) {
                    const double u = -1.0 + 0.25 * static_cast<double>(i);
                    const double v = -1.0 + 0.25 * static_cast<double>(j);
                    const zplanet::Vec3d d = zplanet::CubeSphereDir(f, u, v);
                    const double h = zplanet::PlanetTerrainHeightM(seed, d.x, d.y, d.z);
                    sum += h;
                    ++n;
                }
            }
        }
        if (outCount != nullptr) {
            *outCount = n;
        }
        return sum;
    };
    // 同种子两次全量采样：逐位一致（确定性契约—— memcmp double 位形）
    size_t count = 0;
    const double sumA = sampleHeight(1337u, &count);
    const double sumB = sampleHeight(1337u, nullptr);
    CHECK(count == 6u * 9u * 9u);
    CHECK(std::memcmp(&sumA, &sumB, sizeof(double)) == 0);
    // 逐点核对（不只合计量——防和序掩盖差异）：同输入同输出位形
    {
        const zplanet::Vec3d d = zplanet::CubeSphereDir(2, 0.375, -0.625);
        const double h1 = zplanet::PlanetTerrainHeightM(42u, d.x, d.y, d.z);
        const double h2 = zplanet::PlanetTerrainHeightM(42u, d.x, d.y, d.z);
        CHECK(std::memcmp(&h1, &h2, sizeof(double)) == 0);
    }
    // 种子可配：不同种子 statistically 不同（逐点核对至少多数方向输出不同）
    {
        int diffCount = 0;
        int total = 0;
        for (int f = 0; f < 6; ++f) {
            for (int j = 0; j <= 2; ++j) {
                for (int i = 0; i <= 2; ++i) {
                    const double u = -1.0 + 0.5 * static_cast<double>(i);
                    const double v = -1.0 + 0.5 * static_cast<double>(j);
                    const zplanet::Vec3d d = zplanet::CubeSphereDir(f, u, v);
                    const double hA = zplanet::PlanetTerrainHeightM(1u, d.x, d.y, d.z);
                    const double hB = zplanet::PlanetTerrainHeightM(2u, d.x, d.y, d.z);
                    if (std::memcmp(&hA, &hB, sizeof(double)) != 0) {
                        ++diffCount;
                    }
                    ++total;
                }
            }
        }
        CHECK(diffCount > total / 2); // 全同 = 种子未生效；全异也可疑，过半即可
    }
    // 值域有界：|h| ≤ kTerrainMaxAmpM（全采样集）
    for (int f = 0; f < 6; ++f) {
        for (int j = 0; j <= 8; ++j) {
            for (int i = 0; i <= 8; ++i) {
                const double u = -1.0 + 0.25 * static_cast<double>(i);
                const double v = -1.0 + 0.25 * static_cast<double>(j);
                const zplanet::Vec3d d = zplanet::CubeSphereDir(f, u, v);
                const double h = zplanet::PlanetTerrainHeightM(1337u, d.x, d.y, d.z);
                CHECK(h >= -zplanet::kTerrainMaxAmpM);
                CHECK(h <= zplanet::kTerrainMaxAmpM);
            }
        }
    }
    // 空间连续（C0）：方向微扰 → 高度变化远小于幅值（value noise 光滑插值）
    {
        const zplanet::Vec3d d = zplanet::CubeSphereDir(0, 0.25, 0.25);
        const double eps = 1e-5;
        const double h0 = zplanet::PlanetTerrainHeightM(1337u, d.x, d.y, d.z);
        const zplanet::Vec3d d2 = zplanet::CubeSphereDir(0, 0.25 + eps, 0.25);
        const double h1 = zplanet::PlanetTerrainHeightM(1337u, d2.x, d2.y, d2.z);
        CHECK(std::fabs(h1 - h0) < 0.01 * zplanet::kTerrainMaxAmpM);
    }
    // 地形确有起伏（采样集内非全零/全常数——否则"地形起伏可见"无从谈起）
    {
        double hMin = 1e30;
        double hMax = -1e30;
        for (int f = 0; f < 6; ++f) {
            for (int j = 0; j <= 8; ++j) {
                for (int i = 0; i <= 8; ++i) {
                    const double u = -1.0 + 0.25 * static_cast<double>(i);
                    const double v = -1.0 + 0.25 * static_cast<double>(j);
                    const zplanet::Vec3d d = zplanet::CubeSphereDir(f, u, v);
                    const double h = zplanet::PlanetTerrainHeightM(1337u, d.x, d.y, d.z);
                    hMin = std::min(hMin, h);
                    hMax = std::max(hMax, h);
                }
            }
        }
        CHECK(hMax - hMin > 0.2 * zplanet::kTerrainMaxAmpM); // 起伏 ≥ 20% 幅值
    }
}

TEST_CASE("chunk 共享边: 同档相邻 chunk 共享边顶点逐位一致（规范路由）")
{
    // 面内 4×4 chunk、近档 64×64 顶点（stride=63）：A=(1,0) 与 B=(2,0) 共享
    // 全局索引 g=(2)·63；doctest 走渲染同一条规范路由 PlanetChunkVertexAtGlobal。
    const zplanet::Vec3d center = { 0.0, -zplanet::kPlanetRadiusM, 0.0 };
    const int face = 0;
    const int32_t cells = 4;
    const int32_t res = 64;
    const int32_t stride = res - 1;
    const int32_t total = cells * stride;
    const uint32_t seed = 1337u;
    for (int32_t j = 0; j < res; ++j) {
        // A 右缘 i=res−1 与 B 左缘 i=0：全局索引恒等（整数证据）
        const int32_t gA = zplanet::ChunkVertexIndex(1, stride, stride);
        const int32_t gB = zplanet::ChunkVertexIndex(2, 0, stride);
        REQUIRE(gA == gB);
        REQUIRE(gA == 2 * stride);
        const int32_t gV = zplanet::ChunkVertexIndex(0, j, stride);
        const zplanet::PlanetChunkVertex a
            = zplanet::PlanetChunkVertexAtGlobal(center, face, gA, gV, total, seed);
        const zplanet::PlanetChunkVertex b
            = zplanet::PlanetChunkVertexAtGlobal(center, face, gB, gV, total, seed);
        // 位置/法线/高程逐位一致（double 位形 memcmp）
        CHECK(std::memcmp(&a.posAbs, &b.posAbs, sizeof(a.posAbs)) == 0);
        CHECK(std::memcmp(&a.normal, &b.normal, sizeof(a.normal)) == 0);
        CHECK(std::memcmp(&a.heightM, &b.heightM, sizeof(a.heightM)) == 0);
    }
    // 垂直向邻居 A=(0,1) / B=(0,2) 同理
    for (int32_t i = 0; i < res; ++i) {
        const int32_t gV = zplanet::ChunkVertexIndex(1, stride, stride);
        const int32_t gV2 = zplanet::ChunkVertexIndex(2, 0, stride);
        REQUIRE(gV == gV2);
        const int32_t gU = zplanet::ChunkVertexIndex(0, i, stride);
        const zplanet::PlanetChunkVertex a
            = zplanet::PlanetChunkVertexAtGlobal(center, face, gU, gV, total, seed);
        const zplanet::PlanetChunkVertex b
            = zplanet::PlanetChunkVertexAtGlobal(center, face, gU, gV2, total, seed);
        CHECK(std::memcmp(&a.posAbs, &b.posAbs, sizeof(a.posAbs)) == 0);
        CHECK(std::memcmp(&a.heightM, &b.heightM, sizeof(a.heightM)) == 0);
    }
    // 非共享顶点必不相同（防「恒等比较」假绿：取 A 内部 vs B 内部错位点）
    {
        const zplanet::PlanetChunkVertex a = zplanet::PlanetChunkVertexAtGlobal(center,
            face, zplanet::ChunkVertexIndex(1, stride - 1, stride),
            zplanet::ChunkVertexIndex(0, 5, stride), total, seed);
        const zplanet::PlanetChunkVertex b = zplanet::PlanetChunkVertexAtGlobal(center,
            face, zplanet::ChunkVertexIndex(2, 1, stride),
            zplanet::ChunkVertexIndex(0, 5, stride), total, seed);
        CHECK(std::memcmp(&a.posAbs, &b.posAbs, sizeof(a.posAbs)) != 0);
    }
}

TEST_CASE("chunk 共享边: 跨档共享端点逐位一致（远/近档同棱角点）+ 球面约束")
{
    // 远档 16×16（stride 15, total=480）与近档 64×64（stride 63, total=2016）
    // 在共享 chunk 棱端点处面坐标为同一有理数 (cx+1)/cells——正确舍入除法下
    // 逐位一致（T 型连接的中段偏差由渲染裙边遮蔽，端点仍精确）。
    const zplanet::Vec3d center = { 0.0, -zplanet::kPlanetRadiusM, 0.0 };
    const int face = 1;
    const int32_t cells = 4;
    const uint32_t seed = 1337u;
    const int32_t strideFar = 15;
    const int32_t strideNear = 63;
    const int32_t totalFar = cells * strideFar;
    const int32_t totalNear = cells * strideNear;
    for (int32_t c = 0; c <= cells; ++c) { // 棱上全部 chunk 角点
        const int32_t gFar = c * strideFar;
        const int32_t gNear = c * strideNear;
        const zplanet::PlanetChunkVertex vf = zplanet::PlanetChunkVertexAtGlobal(center,
            face, gFar, 0, totalFar, seed);
        const zplanet::PlanetChunkVertex vn = zplanet::PlanetChunkVertexAtGlobal(center,
            face, gNear, 0, totalNear, seed);
        CHECK(std::memcmp(&vf.posAbs, &vn.posAbs, sizeof(vf.posAbs)) == 0);
        CHECK(std::memcmp(&vf.heightM, &vn.heightM, sizeof(vf.heightM)) == 0);
    }
    // 球面约束：|pos − center| = R + h（逐点），h ∈ [−amp, +amp]；
    // 顶点确定性：同路由两次调用逐位一致。
    const zplanet::PlanetChunkVertex v = zplanet::PlanetChunkVertexAtGlobal(center, face,
        zplanet::ChunkVertexIndex(2, 17, strideNear),
        zplanet::ChunkVertexIndex(3, 41, strideNear), totalNear, seed);
    CHECK(v.heightM >= -zplanet::kTerrainMaxAmpM);
    CHECK(v.heightM <= zplanet::kTerrainMaxAmpM);
    const double radial = zplanet::VecLen(zplanet::VecSub(v.posAbs, center));
    CHECK(radial == doctest::Approx(zplanet::kPlanetRadiusM + v.heightM).epsilon(1e-9));
    const zplanet::PlanetChunkVertex v2 = zplanet::PlanetChunkVertexAtGlobal(center, face,
        zplanet::ChunkVertexIndex(2, 17, strideNear),
        zplanet::ChunkVertexIndex(3, 41, strideNear), totalNear, seed);
    CHECK(std::memcmp(&v.posAbs, &v2.posAbs, sizeof(v.posAbs)) == 0);
}

TEST_CASE("LOD: 两档迟滞——阈值/带内保持/边界严格语义/振荡抑制")
{
    const double nearDist = 900000.0;
    const double hyst = 90000.0;
    // 远档 → 近档：进入需 dist < nearDist − hyst（严格）
    CHECK(zplanet::PlanetLodLevel(0, nearDist - hyst + 1.0, nearDist, hyst) == 0);
    CHECK(zplanet::PlanetLodLevel(0, nearDist - hyst, nearDist, hyst) == 0); // 恰在带缘不切
    CHECK(zplanet::PlanetLodLevel(0, nearDist - hyst - 1.0, nearDist, hyst) == 1);
    CHECK(zplanet::PlanetLodLevel(0, 0.0, nearDist, hyst) == 1);
    // 近档 → 远档：退出需 dist > nearDist + hyst（严格）
    CHECK(zplanet::PlanetLodLevel(1, nearDist + hyst - 1.0, nearDist, hyst) == 1);
    CHECK(zplanet::PlanetLodLevel(1, nearDist + hyst, nearDist, hyst) == 1); // 恰在带缘不切
    CHECK(zplanet::PlanetLodLevel(1, nearDist + hyst + 1.0, nearDist, hyst) == 0);
    CHECK(zplanet::PlanetLodLevel(1, 1.0e9, nearDist, hyst) == 0);
    // 迟滞带内任意距离保持原档（两方向）
    for (double d = nearDist - hyst; d <= nearDist + hyst; d += hyst / 10.0) {
        CHECK(zplanet::PlanetLodLevel(0, d, nearDist, hyst) == 0);
        CHECK(zplanet::PlanetLodLevel(1, d, nearDist, hyst) == 1);
    }
    // 振荡抑制：距离在阈值附近 ±0.4·hyst 抖动 200 次——无迟滞（hyst=0）每次
    // 过线即切档；有迟滞带整段保持，切换次数 ≤ 2。
    auto countSwitches = [&](double h) {
        int lod = 0;
        int switches = 0;
        for (int k = 0; k < 200; ++k) {
            const double d = nearDist + ((k % 2 == 0) ? -0.4 * hyst : 0.4 * hyst);
            const int next = zplanet::PlanetLodLevel(lod, d, nearDist, h);
            if (next != lod) {
                ++switches;
            }
            lod = next;
        }
        return switches;
    };
    CHECK(countSwitches(0.0) > 100);      // 无迟滞：阈值处逐帧抖动切换
    CHECK(countSwitches(hyst) <= 2);      // 迟滞带覆盖抖动幅度：恒稳定
    // 状态机口径：档位只依赖 (prevLod, dist) 纯函数——同输入同输出
    CHECK(zplanet::PlanetLodLevel(1, nearDist, nearDist, hyst)
        == zplanet::PlanetLodLevel(1, nearDist, nearDist, hyst));
}

TEST_CASE("LOD: 地平线距离——量级符合 √(2Rh) 与精确式")
{
    // 贴地 2m：≈5,048m（B-TECH-03 口径 √(2Rh)≈5km）；600m：≈87.4km；
    // 61.7km（B-TECH-03 爬升关键帧）：≈888.8km
    CHECK(zplanet::PlanetHorizonDistM(2.0) > 5040.0);
    CHECK(zplanet::PlanetHorizonDistM(2.0) < 5060.0);
    CHECK(zplanet::PlanetHorizonDistM(600.0) > 87400.0);
    CHECK(zplanet::PlanetHorizonDistM(600.0) < 87480.0);
    CHECK(zplanet::PlanetHorizonDistM(61700.0) > 888000.0);
    CHECK(zplanet::PlanetHorizonDistM(61700.0) < 890000.0);
    // 精确式 vs 近似式 √(2Rh)：exact/approx = √(1+h/2R) ∈ (1, 1+h/2R)——
    // 数学界（低空 h≪R 时偏差远小于 0.1%，高空按 h/2R 放宽）；单调性
    for (double alt : { 2.0, 100.0, 600.0, 61700.0, 3.0e5 }) {
        const double exact = zplanet::PlanetHorizonDistM(alt);
        const double approx = std::sqrt(2.0 * zplanet::kPlanetRadiusM * alt);
        CHECK(exact > approx);
        CHECK(exact < approx * (1.0 + alt / (2.0 * zplanet::kPlanetRadiusM)));
        CHECK(zplanet::PlanetHorizonDistM(alt + 1.0) > exact);
    }
}
