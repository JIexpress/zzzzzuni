# TASK-B-TECH-03 —— 行星尺度最小切片（浮点原点重定位 + 球面重力 + 球体渲染雏形）

- **轨道**：B / TECH（Docs/TECH-ROADMAP.md 方向三切片；原生 C++/zrender 窗口程序，无 Unity 依赖）
- **状态**：done（独立验收 PASS，2026-11-08；V1-V5 全项亲证）
- **施工**：general-purpose 施工子代理
- **验收**：general-purpose 验收子代理（与施工不同代理）
- **派发**：主控，2026-11-08
- **通过行**：`D3D12 PLANET OK (origin=RELOCATED@90694m jitter=0.0028125m/0.0000001m)`

---

## 一、任务简报（主控口径）

方向三（行星尺度无缝世界）的最小验证切片，在 zrender 窗口程序（`zrhi_d3d12_window.exe`）内完成，
**无 Unity 依赖**。三演示：

1. **浮点原点重定位**：相机位置 double 累计（模拟飞 100,000m），世界物体相对相机的偏移用
   float；相机偏移超阈值（10,000m）时重定位（所有物体 transform 平移回原点附近），
   验证重定位前后**顶点级抖动消除**（顶点数值断言口径）。
2. **球面行星雏形**：半径 6.371e6m 的球（程序化网格），双精度行星中心坐标 + float 局部
   渲染坐标；相机贴地（地表 2m）看球面地平线曲率。
3. **球面重力轨迹**：抛射体按 `a = -μ·r̂/r²` 数值积分（μ=地球参数 3.986004418e14），轨迹线
   渲染；数值断言轨迹近地点/远地点符合能量守恒（活力公式）预期。

**验收口径**：新模式 `--planet` 整合三演示（自动飞行相机：贴地→爬升→入"轨道"视角），
关键帧截图 `Docs/Parity/planet_*.png`（3 张：贴地/爬升/远观）+ 通过行
`D3D12 PLANET OK (origin=RELOCATED@Xm jitter=before/after)`；doctest 覆盖浮点原点相对化、
球面重力积分器（能量守恒）、重定位阈值逻辑；`Tools/build_native.ps1` 全绿（基线 311/771,162
只增不减）；三张截图施工代理 Read 亲验。

**白名单**：`Native/tests/`（窗口程序 + planet_math.h + doctest）、`Native/shaders/`（planet.hlsl）、
`Native/CMakeLists.txt`、`Docs/Parity/`、本日志；不碰 Unity；不 git commit；/W4 /WX；
UTF-8 无 BOM/LF/#pragma once；VK 不泄漏。

---

## 二、施工报告（general-purpose 施工子代理）

### 1. 交付物清单

| 文件 | 状态 | 内容 |
|---|---|---|
| `Native/tests/planet_math.h` | 新增（384 行） | 纯数学内核（无渲染依赖）：浮点原点状态机/抖动审计、球面重力 velocity Verlet + 活力公式、球面切平面几何；被窗口程序与 doctest 双方共享（同一份实现） |
| `Native/tests/test_planet.cpp` | 新增（262 行） | doctest ×8 用例 / 79 断言宏（能量守恒等循环断言运行期展开后贡献数百次运行期断言） |
| `Native/shaders/planet.hlsl` | 新增（115 行） | 单 PSO 三模式（0 地形片/1 球体/2 纯色）：高程着色 + 等高线 + 渲染空间 1km 网格（fwidth AA）、伪大陆 + 极冠 + 大气边缘、距离雾对齐清屏色 |
| `Native/tests/zrhi_d3d12_window.cpp` | 修改 | `RunPlanetMode()`（~1,160 行）+ main() `--planet` 分发 + 文件头 TASK-B-TECH-03 块 + `#include "planet_math.h"` |
| `Native/CMakeLists.txt` | 修改 | `zrender_test` 注册 `tests/test_planet.cpp`（1 行） |
| `Docs/Parity/planet_ground.png` / `planet_climb.png` / `planet_orbit.png` | 新增 | 640×480 关键帧截图 ×3（施工代理 Read 亲验，见 §4） |

**未动**：`Native/zrhi/`（零改动——深度策略选"相位伸缩 near/far"而非对数深度，见 §5 受限）、
Vulkan 路径（planet.hlsl 不进 `vulkan_shaders` 清单，VK 零泄漏）、Unity 侧全部文件
（git 状态里 `UnityProject/` 的改动来自并行任务的在跑 Unity 会话，与本任务无关）。

### 2. 三演示的设计与实现

**① 浮点原点重定位**（`planet_math.h::FloatingOrigin` + 窗口程序帧循环）
- 相机绝对坐标 double 逐帧累计（`MoveCameraBy`）；世界物体保存 double 绝对坐标；
  渲染偏移一律 `float(objAbs - camAbs)`（**double 域相减后取 float**——重定位正确路径）。
- 触发判据 `ShouldRelocate`：`|camAbs - originAbs| > 10,000m`（**严格大于**，恰在阈值不触发；
  三维欧氏距离，斜向飞行不漏判）。
- 重定位 `Relocate()`：原点吸附到相机；物体绝对坐标不变（绝对语义），「所有物体 transform
  平移回原点附近」由 objRel = objAbs − originAbs 的新值体现；贴地相位（对地 <3km）时
  **近场地形片随动重锚**（锚点吸到相机正下球面点，36,864 顶点 double 重算——真实引擎的
  chunk rebase 形态）。
- naive 对照路径 `RenderOffsetNaiveF`：`float(objAbs) − float(camAbs)`（全 float 大坐标相减）——
  抖动根源的数学模型。

**② 球面行星雏形**（程序化网格三档，双精度建模 → float 相对渲染）
- 行星中心 double 绝对坐标 `(0, −6.371e6, 0)`（切点在原点正下方）；任意切平面偏移经
  `SpherePointFromTangent` 精确落球面（`p = C + R·normalize(R·up + t1·e1 + t2·e2)`，double 域）。
- **近场地形片**：±24km、192² 网格（250m 格距），贴地相位绘制；锚点随重定位重锚。
- **远盘**：极坐标 160 环 ×160 扇（半径 1100km，静态拓扑），3km ≤ 对地高度 <1000km 绘制，
  锚点逐帧吸附相机正下点——覆盖真地平线（61.7km 高度时地平线在 889km），盘缘 1100km
  略越地平线被几何折叠遮挡，地平线轮廓 = 真几何地平线（曲率可见的关键设计）。
- **整球**：UV 64×48（顶点 float 局部坐标，相对行星中心；入轨距离下 0.5m float 量化不可见），
  对地高度 ≥1000km 绘制；model 矩阵承载 double→float 的中心相对偏移。
- 深度策略：**相位伸缩 near/far**（贴地 near=1/far=150km；爬升 near=min(0.5·alt, 8e6)、
  far=2.5·(距心+R)；入轨 near=8e6/far≈8.1e7）——TECH-ROADMAP「near/far 相机或对数深度」
  的前者；对数深度留作 zrhi 加法任务（本任务零 zrhi 改动）。

**③ 球面重力轨迹**（`IntegrateGravity`：velocity Verlet，辛积分——能量有界振荡，守恒断言的前提）
- **弹道抛射体**：发射点切平面偏移 (前方 9km，右舷 1.2km)（升弧段落进贴地视锥），当地 up =
  球面法线，v₀ = 1200m/s @ 仰角 60°（600 水平 + 1039.23 沿 up），dt=0.05s 积分至 |r| < R
  （撞击），270 采样点；渲染为橙色 ribbon 线带（RHI v1 仅 TRIANGLELIST——相机朝向
  billboard 窄四边形拼线，宽度按距离比例 ≈ 恒定像素宽，双 VB 共享四边形索引模式表）。
- **低轨椭圆**：r₀ = R+200km，v₀ = 7750m/s 切向（< 环绕速度 7791.6），dt=0.5s 积分 1.05 周期
  （周期 5,223.6s），262 采样点；对地 ≥100km 绘制青色 ribbon（入轨视角环抱行星 limb）。

**飞行动画（--planet，300 帧恒离屏 640×480，--assets 同策）**：贴地（0-119：2m 高度 +Z
170m/帧，帧 100 起俯仰 -3°→-25°）→ 爬升（120-209：alt = 2 + 2×10⁷·s⁵ 至 2 万 km；视线中心
恒保持地平线下 5°——行星恒占视野中下部、天顶恒为天空，探针高度不变性；alt>1.2e7 混向
行星中心）→ 入轨远观（210-299：距行星中心 2.6 万 km，方位角 15°→55° 慢扫，正视行星中心）。

### 3. 数值证据（--planet 实跑，exit 0）

**抖动对比（JitterAudit：0→100,000m 直线飞行、1m 步进 double 累计、近场物体恒在相机前
10.37m——"重定位后相机-物体相对位置精度恢复 float 精度"的顶点数值断言）**：

| 路径 | 相对位置最大量化误差 | 口径 |
|---|---|---|
| naive（无重定位，全 float 大坐标相减） | **0.0028125 m**（≈ULP(100km)=7.8125mm 的 36%，mm 级抖动） | 误差上界 = ULP(幅值) |
| 重定位后（double 域相对化 → float） | **0.0000001 m**（0.1µm，≈ULP(10.37m)/2） | 近场 float 精度 |
| **改善倍数** | **≥28,000×** | doctest 断言 >100× |

贴地相位逐帧实况探针（8 个近场偏移 ×3 轴 ×120 帧）：naiveMax = 0.0007813m（相机距原点
10.17km 时 ULP(10.17km)）vs relocatedMax = 0.0000008m（~977×）——**渲染循环内**重定位前后
的实况对比，非纯离线审计。

**球面重力数值断言（活力公式解析基准 vs 数值轨迹）**：

| 量 | 数值轨迹 | 活力公式解析 | 相对误差 | 断言容差 |
|---|---|---|---|---|
| 椭圆近地点 rp | 6,442,700.6 m | 6,442,699.5 m | **1.80e-07**（≈1.1m） | <1e-4 |
| 椭圆远地点 ra | 6,571,000.0 m | 6,571,000.0 m | **3.40e-15** | <1e-4 |
| 椭圆能量漂移 \|Δε\|/\|ε₀\| | **3.57e-09** | 0（守恒） | — | <1e-6 |
| 弹道远地点 | 6,426,789.6 m（地表上 55.79km） | 6,426,789.6 m | **3.46e-10** | <1e-3 |
| 弹道撞击 | t=215.4s 落地（impacted ✓） | — | — | ✓ |

velocity Verlet (dt=0.5s) 下能量漂移 3.57e-09 显著低于显式 Euler 在同参数下的 ~1e-3 量级
漂移——**守恒断言对非辛积分器有区分力**（doctest 阈值 1e-6 抓得住）。

**重定位账目（渲染循环内实发）**：全程 **158 次**重定位；#1 @帧 60（偏移 10,030.0m——贴地
飞行穿越 10km 阈值的实发证据）；#2 @帧 119（20,060m）；飞行段（≤100.5km）内最后一次
@ **90,694.1m**（即通过行 `RELOCATED@90694m`）；全程最大相机-原点偏移 30,298km（爬升 +
入轨段每帧重定位，机制尺度不变性的实跑证据）。

**探针（readback 比对，32 检查帧）**：值探针 32/32（贴地地面 ≠ 清屏色 / 爬升行星面 ≠ 清屏 /
入轨行星中心 ≠ 清屏）；天空/太空探针 13/13（爬升地平线上方 ≈ 清屏色 / 入轨角落太空 ≈
清屏色 ±2）。InfoQueue 0 错误、无设备移除、RG 零未声明使用。

### 4. 关键帧截图（施工代理 Read 亲验）

- `planet_ground.png`（帧 40，贴地 2m）：地形网格 + 地平线折叠 + 右侧橙色弹道升弧段出画 +
  中轴远弧段降线（fog 提亮后可见）+ 右舷 ×8 立方体标记（帧 60 重定位跨越它）+ 清屏色天空。
- `planet_climb.png`（帧 148，对地 61.7km）：**1km 网格随球面弯沉、地平线弧形弯曲清晰可见**
  （真几何地平线，非贴图），橙色弹道从地表直贯上天球，天空 = 清屏色。
- `planet_orbit.png`（帧 255，距心 2.6 万 km）：行星球体（深蓝海洋 + 绿色伪大陆 + 极冠 +
  大气边缘光），**青色低轨椭圆环抱行星 limb**（rp 71km / ra 200km 贴 limb 环），四周太空 =
  清屏色。

施工过程真实踩坑（记录防复发）：爬升/入轨关键帧（帧 148/255）与每 10 帧 readback 节拍
不重合 → ReadbackPass 未在关键帧执行 → 全帧 readback 缓冲读到帧 40 陈旧数据 → 三张 PNG
一度完全相同（探针全绿是假绿——探针节拍帧恰好新鲜且相位判定碰巧都能过）。**修复 =
关键帧帧强制走 ReadbackPass**；三张截图 Read 亲验差异后才算证据。教训：**截图证据必须
亲验内容，不能只看 png=1**。

### 5. 已知受限（如实登记）

1. **深度策略为相位伸缩 near/far，非对数深度**：入轨相位（near=8,000km）下行星 limb 处
   椭圆 ribbon 与球面深度接近，理论上有 z-fighting 窗口（实拍截图未见）；对数深度属
   `Native/zrhi/` 加法任务（本任务按白名单零 zrhi 改动）。
2. **爬升段俯仰上提示轴切换**（|f.y|>0.9 时 up-hint 从 +Y 换 +Z）有轻微 roll 跳变（帧 ~205），
   不在任何关键帧截图内；真实引擎用四元数球面插值消解，属雏形简化。
3. **抛射体撞击后轨迹终段沉入地形**：撞击判据 |r|<R，最后 ~1 步（dt=0.05s × 落速 ~1,039m/s
   ≈ 52m）穿入地表下，视觉上轨迹末端扎进地面——物理口径正确，渲染不裁剪。
4. **球体远景 float 量化**：整球顶点 float 局部坐标（ULP(6.371e6)=0.5m）+ 中心偏移 float
   （入轨距离 ULP≈2m）——入轨视角不可见，但近距（<100km）看球面会暴露；近距场景由
   地形片/远盘（double 建模）承接，分工即浮点原点纪律本身。
5. **Unity 侧集成未做**（简报全量投入前提之一：「与 Unity 侧集成方案设计」——属切片通过
   后的下一个任务，不在本切片白名单内）。

### 6. 自测记录

- `powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1`：
  configure + build（/W4 /WX 零警告）+ doctest **342 用例 / 777,011 断言全绿**
  （本任务新增 8 用例；HEAD 静态计数 334；简报基线 311/771,162【HANDOFF 2026-11-05 快照】
  只增不减 ✓）。step 4/4（zrender.dll 拷贝 Unity 插件）因并行任务的在跑 Unity 会话锁定
  dll 报"正由另一进程使用"——**已核实构建产物与已部署 dll md5 逐字节相同**
  （`bce2e620367fbace35c2833757692c29`，本任务未改任何库代码），拷贝语义已满足，
  属环境噪声非回归。
- `zrhi_d3d12_window.exe --planet`：exit 0，通过行
  `D3D12 PLANET OK (origin=RELOCATED@90694m jitter=0.0028125m/0.0000001m)`，
  证据行齐全（§3），连跑 4 次数值稳定（审计/重力数字逐位一致）。
- 文件规范：四个新增/修改源文件均 UTF-8 无 BOM（首字节 `//`/`#pr`）+ LF（0 CR 行）；
  planet_math.h `#pragma once` ✓。

---

## 三、验收清单（供独立验收代理复跑）

1. **构建 + 单测**：仓库根执行 `powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1`
   → 期望 doctest `342 | 342 passed | 0 failed`（若 Unity 锁 dll，step 4/4 的失败为环境噪声，
   核对两份 zrender.dll md5 一致即可；构建与 doctest 段必须绿）。
2. **--planet**：仓库根执行 `Native/build/bin/Release/zrhi_d3d12_window.exe --planet`
   → 期望 exit 0 + 通过行 `D3D12 PLANET OK (origin=RELOCATED@...m jitter=...m/...m)` +
   证据行（审计 naive ≥1mm、relocated <1e-6m；椭圆 rpErr<1e-4、epsDrift<1e-6；弹道
   impacted=1、apogee err<1e-3；relocs ≥2）。
3. **截图亲验**（Read/ViewImage 人工过目）：`Docs/Parity/planet_ground.png`（地形 + 弹道 +
   立方体）、`planet_climb.png`（地平线曲率 + 网格弯沉 + 弹道上天）、`planet_orbit.png`
   （行星球 + 青色轨道环 + 太空）——三张必须内容互异。
4. **抖动数值断言独立复核**（可选）：doctest 用例「100km 飞行抖动审计」中把
   `relocatedMaxErrM < 1e-6` 放宽到 `< 1e-3` 后用例仍绿、收紧到 `< 1e-7` 应红
   （实测 relocatedMax=1e-7 量级，`<1e-7` 边界敏感——以复跑实测为准）；或把
   `ShouldRelocate` 的 `>` 改 `>=`，「恰在阈值不触发」断言应红（变异必红口径）。
5. **白名单核对**：`git status` 中本任务改动应仅含
   `Native/CMakeLists.txt`、`Native/tests/zrhi_d3d12_window.cpp`、`Native/tests/planet_math.h`、
   `Native/tests/test_planet.cpp`、`Native/shaders/planet.hlsl`、`Docs/Parity/planet_*.png`、
   本日志；`Native/zrhi/`、Vulkan 清单、Unity 侧零改动（UnityProject 下的改动属并行任务）。

## 四、验收结论

**PASS**（独立验收代理 general-purpose 亲证，2026-11-08；未跑 Unity，未改任何代码，未 commit。
验收过程中唯一写入 = 本日志本节与头部验收行。）

| 项 | 结论 | 证据（验收代理亲跑/亲读） |
|---|---|---|
| V1 构建全绿 | PASS | 亲跑 `Tools/build_native.ps1`：configure + build（/W4 /WX 零告警）+ doctest `342 \| 342 passed \| 0 failed`、断言 `777011 \| 777011 passed \| 0 failed`、Status SUCCESS、ALL GREEN；本次 step 4/4 dll 部署亦成功（未遇 Unity 锁）。TEST_CASE 静态计数 HEAD=334 → 工作区 342（+8 全来自 test_planet.cpp） |
| V2 --planet | PASS | 亲跑两次 exit 0，数值逐位一致；通过行 `D3D12 PLANET OK (origin=RELOCATED@90694m jitter=0.0028125m/0.0000001m)`；证据行齐全：audit naive=0.0028125/relocated=0.0000001/ulp=0.0078125；ellipse rp=6442700.6 visviva=6442699.5 rpErr=1.80e-07 raErr=3.40e-15 epsDrift=3.57e-09；ballistic apogee=6426789.6 err=3.46e-10 flight=215.4s impacted=1 samples=270；relocs count=158 lastInFlight=90694.1 maxOriginOffset=30298063.8；live naiveMax=0.0007813/relocatedMax=0.0000008；checks=32 value=32/32 sky=13/13 png=1/1/1 |
| V2 截图亲验 | PASS | Read 亲读三张 PNG（640×480 RGB，md5 互异 a871…/3f01…/62d0…）：ground=贴地地形+地平线折叠+两条弹道线+清屏色天空（4× 放大核验地平线上棕色立方体标记）；climb=1km 网格随球面弯沉+地平线弧形曲率+弹道上天；orbit=行星球（海洋/伪大陆/极冠/大气缘光）+青色轨道环抱 limb+黑色太空。三张内容互异，与 §4 描述相符 |
| V3 数学审查 | PASS | velocity Verlet 公式正确（planet_math.h L296-303，标准形式）；GravityAccel=−μr/r³（L214-219）；活力公式 ε/a/e/rp/ra（L239-253）与 T=2π√(a³/μ)（L256-262）均标准。**独立 Python 复算**（同初始条件）：椭圆解析 rp=6,442,699.5/ra=6,571,000.0/T=5,223.6s，数值 rmin=6,442,700.6、rpErr=1.795e-07、raErr=3.402e-15、epsDrift=3.568e-09——与报告逐位一致；弹道（doctest 初始条件）impacted、t=215.4s、apogeeErr=3.44e-10；JitterAudit 复算 naiveMax=0.0028125（精确）、relocatedMax=1.144e-7、ulp(100km)=0.0078125 |
| V3 重定位状态机 | PASS | 纯函数：ShouldRelocate（L101-109，严格>阈值、三维欧氏）、MoveCameraBy/Relocate（L117-146，账目完整）、RenderOffsetF/NaiveF（L150-164）、JitterAudit（L181-207）。用例行号：阈值 test_planet.cpp L53-73（L60 恰在阈值不触发/L63 超阈值/L66,69 斜向欧氏/L72 负向）；执行与语义 L75-110（L87-91 触发计数、L95 重定位后渲染偏移 memcmp 逐位不变、L97-99 物体回阈值内、L101-105 恰 10km 不触发+0.5 触发、L106 计数）；抖动审计 L25-39；ULP 表 L41-51。渲染循环内实发核验：RELOCATE #1@帧60 offset=10,030.0m、#2@帧119 flightDist=20,060.0m（窗口 L7517-7529），轨道段每帧重定位 offset=192,498.1m 与方位扫描弧长独立核算一致，maxOriginOffset=30,298,063.8m 与爬升→入轨切换跳变独立核算一致 |
| V4 方法学 | PASS | ①对比公平：JitterAudit 同循环/同相机 double 轨迹/同物体位置/同真值 nearOffset 双路径同口径计误差（L190-205）；实况探针同口径（8 偏移×3 轴×120 帧，窗口 L7535-7552），naive/ relocated 同源 camAbs；②假绿闭环：窗口 L7786 `if (doReadback \|\| shotIndex >= 0)` 关键帧强制 ReadbackPass，全帧拷贝（L7833-7844）与探针（L7879）同帧同源新鲜，PNG 立即从该缓冲写出（L7940-7955）；checks=32=30 节拍帧+2 个非节拍关键帧（148/255），恰覆盖原漏拍帧——修复结构闭环，且三张 PNG 互异由验收代理独立复核 |
| V5 白名单/规范 | PASS | git 核对：本任务改动仅 `Native/CMakeLists.txt`（+1 行）、`Native/tests/zrhi_d3d12_window.cpp`（+1220/−0）、`Native/tests/planet_math.h`、`Native/tests/test_planet.cpp`、`Native/shaders/planet.hlsl`、`Docs/Parity/planet_*.png`×3、本日志；`Native/zrhi/` 零 diff；planet.hlsl 未进 vulkan_shaders SPV 清单（CMakeLists 仅 L347 一处 planet=测试注册），三个新文件零 VK/Vulkan 字样；工作区其余改动（crowd_r9_15*.png、TASK-A-TECH-02.md、UnityProject/**）属并行任务非本任务所为。文件规范：五个源/配置文件首 3 字节非 BOM、CR=0（LF）、planet_math.h L1 `#pragma once` ✓。日志四节齐全（本节为验收结论） |

**验收备注（4 处 cosmetic nit，均不影响 PASS，供后续任务参考）**：
1. 核心数字"改善 28,000×"系显示舍入放大：0.0028125/0.0000001=28,125，按未舍入 relocatedMax=1.144e-7 实为 **24,576×**（doctest 门 >100× 仍大幅裕量）。
2. §2③ prose"环绕速度 7791.6"不精确：v_circ(R+200km)=√(μ/r)=7,788.5 m/s（代码注释 7792 同源微偏）；无任何断言依赖该值。
3. §3"relocated ≈ULP(10.37)/2"措辞偏松：ULP/2=4.77e-7 是上界，实测 1.14e-7 在其内。
4. §5.3 穿地表深度 prose"~52m"，独立复算首步侵入径向 41.5m（同量级；断言门 R−100 内）。
