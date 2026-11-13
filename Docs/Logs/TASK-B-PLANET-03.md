# TASK-B-PLANET-03 —— 行星数学生产化（planet_math §①③④ → Native/zplanet/，语义零改动）

- **轨道**：B / PLANET（设计稿 `Docs/Planning/P2-行星ABI设计.md` §11 第三条）
- **状态**：doing
- **派发**：主控，2026-11-14
- **依赖**：无（设计稿口径可与 02 并行；02 已闭环 f5e6aa1）

## 任务简报（主控写）

### 0. 使命

把行星数学从测试代码提升为生产模块——B-PLANET-04/06 的实现要消费它，不能继续从 tests 里 include。**语义零改动**是本任务的灵魂：15 条 doctest 逐位回归全绿就是唯一权威判据。

### 1. 做什么（白名单纪律，设计稿 §11-03 原文）

1. **提升范围**：`Native/tests/planet_math.h` 的 §①（浮点原点）§③（球面几何）§④（cube-sphere/chunk/LOD/地平线）→ 新生产目录 **`Native/zplanet/`**；头文件仅依赖标准库特性保持（无 Unity/无 D3D/无 zrhi 依赖）
2. **不提升**：§②（重力）留在 tests——原生通道不含物理（设计稿 §12-1 明确不做清单）
3. **语义零改动**：函数签名/算法/常量/舍入行为逐字节保持；只动文件位置与 include 结构（header guard、命名空间如原文件已有则保持）
4. **测试改指**：`test_planet.cpp` 的 15 条 doctest 改 include 生产头；**逐位回归**——改前后测试输出（含全部断言数值）一致；`planet_math.h` 在 tests 里保留一个薄 include-shim（`#include "zplanet/..."`）以免其他测试文件 include 断链（如无其他消费者则可直接删除，施工时查清消费者清单再定，写进报告）
5. **CMakeLists**：zplanet 加入构建（header-only 或按现结构；zrender/zplugin 对其零依赖要求不变——**本任务不改 zplugin.h/zrender 任何文件**）
6. **构建与测试**：build_native.ps1 ALL GREEN（/WX 零警告）；zrender_test.exe ≥354 用例只增不减；15 条行星 doctest 全绿

### 2. 白名单（越界=FAIL）

- 新增：`Native/zplanet/**`
- 修改：`Native/tests/test_planet.cpp`（include 行）、`Native/tests/planet_math.h`（shim 或删除）、`Native/CMakeLists.txt`（或所在层级 CMake 文件）、本日志
- **不碰**：zplugin.h/cpp、zrender/**、zupscaler/**、zkernel/**、UnityProject/**、TASKS/HANDOFF/WORKLOG
- 环境坑：原生构建一律走 Tools/build_native.ps1（裸调 cmake 会因 MSYS2 权限失败）

### 3. 铁律

- 约法三章：施工不 commit、不改看板；UTF-8 无 BOM/LF；/WX 零警告
- 语义零改动：施工报告必须含「逐位回归证据」——改前/改后 test_planet 输出 diff 为空（或逐项数值对照表）
- 消费者清单（谁 include planet_math.h）进施工报告

### 4. 完成判据

- `Native/zplanet/` 生产头在库；tests 15 条 doctest 改指生产头全绿且逐位一致
- 白名单零越界；zplugin/zrender 零改动
- B-PLANET-04/06 可直接 `#include` 生产头开工

## 施工报告（施工代理，2026-11-14）

### R1. 改动文件清单

```
 git diff --numstat（tracked）：
 12   1  Native/CMakeLists.txt
 18 452  Native/tests/planet_math.h
  2   1  Native/tests/test_planet.cpp
 新增：Native/zplanet/planet_math.h（469 行，untracked 新文件）
```

- `Native/zplanet/planet_math.h`（**新增**）：生产头，§①③④ + 共享基座（见 R2）
- `Native/tests/planet_math.h`（**改写为 shim**）：转发生产头 + §② 重力原文保留
- `Native/tests/test_planet.cpp`（**仅 include 行**）：`#include "planet_math.h"` 1 行
  → 生产头 `#include "zplanet/planet_math.h"` + shim `#include "planet_math.h"` 2 行
  （其余 0 字节改动；+1 行导致后续断言的 file:line 元数据整体 +1，见 R4）
- `Native/CMakeLists.txt`：新增 `zplanet` INTERFACE 目标（include 根 = Native/，
  不编译任何翻译单元）；`zrender_test` 链接之（仅测试 exe）；test_planet.cpp 源行注释更新
- **零改动**（git status 核验）：zplugin.h/cpp、zrender/**、zupscaler/**、zkernel/**、
  UnityProject/**、TASKS/HANDOFF/WORKLOG——白名单零越界
- 编码核验：4 个文件均 UTF-8 无 BOM（首字节 `23 70 72` 等）、0 个 CR 字节（LF）

### R2. 提升的函数清单（生产头 Native/zplanet/planet_math.h，namespace zplanet）

**共享基座**（§①③④ 共用）：
- `struct Vec3d`；`VecAdd / VecSub / VecScale / VecDot / VecCross / VecLen`
- `FloatUlpAt`（float ULP 幅值表工具，§① 抖动审计的理论上界来源）
- 常量 `kPlanetRadiusM = 6.371e6`（kGravityMu 不提升，随 §② 留 tests）

**§① 浮点原点重定位**：
- `struct FloatingOrigin`：`ShouldRelocate`（静态，严格 > + 三维欧氏距离）、
  `ShouldRelocateNow`、`MoveCameraBy`、`Relocate`、`RenderOffsetF`（重定位正确
  路径）、`RenderOffsetNaiveF`（naive 对照路径）
- `struct JitterAuditResult` + `JitterAudit`（0→flightM 抖动审计，naive vs relocated）

**§③ 球面几何助手**：
- `struct TangentBasis` + `TangentBasisAt`（切平面正交基）
- `struct SpherePointResult` + `SpherePointFromTangent`（切平面偏移→球面点/曲率下沉/法线）

**§④ cube-sphere/chunk/LOD/地平线**：
- 常量 `kTerrainNoiseOctaves / kTerrainBaseWavelengthM / kTerrainMaxAmpM`
- 噪声纯函数：`PlanetHashU32`、`PlanetLatticeValue`、`PlanetValueNoise`、`PlanetTerrainHeightM`
- cube-sphere：`struct CubeFaceBasis`、`CubeFaceBasisOf`、`CubeSphereDir`
- chunk 规范采样：`ChunkVertexIndex`、`ChunkFaceCoord`、`struct PlanetChunkVertex`、
  `PlanetChunkVertexAt`、`PlanetChunkVertexAtGlobal`
- `PlanetLodLevel`（两档迟滞）、`PlanetHorizonDistM`（地平线精确式）

**留在 tests（shim，不提升）**：§② 球面重力积分器全量——`kGravityMu`、
`GravityAccel`、`struct GravityState`、`struct OrbitInfo`、`OrbitFromState`、
`OrbitPeriodS`、`struct IntegrateResult`、`IntegrateGravity`（原生通道不含物理，
设计稿 §12-1）

### R3. 语义零改动核验（源级证据）

- `diff`（改前 tests/planet_math.h vs 新生产头）逐 hunk 核验：差异**仅有**
  ① 文件头注释重写 ② include 集合变化（`+<cstdint>`、`-<vector>`，均为 include
  结构调整）③ kGravityMu 常量行移出 ④ §② 块整体移除 ⑤ §④ banner 注释中
  「①②③」→「①③」一处措辞——**全部函数/结构体/常量正文 0 字节改动**
- shim 的 §② 块与原文逐字节核验：`git show HEAD:Native/tests/planet_math.h`
  第 209–332 行 vs shim 对应区域 `diff` 为空（SEC2-BLOCK: BYTE-IDENTICAL）；
  `kGravityMu` 行原文一致

### R4. 逐位回归证据（核心判据）

**方法**：改前基线（原实现编译的 zrender_test.exe）跑两次 + 改后跑一次，`-s`（逐
断言成功输出，含 `values:` 全部求值数值）与默认模式各抓全量输出做 diff。
证据文件存于仓库外临时目录 `C:\Users\1\AppData\Local\Temp\planet03\`（不入库）。

**① 行星 15 条 doctest 块（test_planet.cpp 全部断言，15 用例 / 1791 断言 / 5433 行）**：
- 基线两次自 diff：**空**（md5 `4bd4b9a6c1c28ff7d5728038ee72f41a`×2）——输出确定性成立
- 基线 vs 改后 raw diff：1806 行差异，**全部**为 `test_planet.cpp(NN):` file:line
  元数据（grep -vc 排除后 = 0 行）——+1 行号位移来自 include 行 1→2 的插入
  （白名单明示允许改 include 行），非语义差异
- 行号归一化后 diff：**EMPTY**（md5 `97de84c1685d1bde5da7809d36d558cd` 前=后）
  ——1791 条断言的表达式文本与求值数值逐字节一致，含全部 memcmp 逐位断言

**② 全奏默认输出（354 用例 / 778763 断言）**：
- 基线两次自 diff：仅 1 行——`test_zkernel_datakernel.cpp(551)` RDTSC 实测
  MESSAGE（8/9 vs 4/5 cycles/op，机器噪声，改前即存在）
- 基线 vs 改后 diff：**同样仅此 1 行**（4/5 cycles/op，同类噪声带内）；
  尾部 `[doctest] test cases: 354 | 354 passed | 0 failed` /
  `assertions: 778763 | 778763 passed` / `Status: SUCCESS!` 前后逐字节一致

**③ 已知预存问题（非本任务引入，零触碰）**：`-s` 模式全量跑在
`test_zfg_env.cpp(117)` SIGSEGV（崩溃点行号基线/改后逐字节一致：均 2102221 行）；
行星块在该点之前已完整跑完。默认模式（验收口径）无此现象。白名单之外，未处理。

### R5. 消费者清单与 shim 决策

`grep -rn planet_math` 全仓查证（实际 `#include` 消费者）：
1. `Native/tests/test_planet.cpp:17` —— doctest（已改指生产头 + shim 取 §②）
2. `Native/tests/zrhi_d3d12_window.cpp:240` —— `--planet/--planet2` 窗口模式，
   **消费 §②（GravityState/IntegrateGravity/kGravityMu）+ §①③④ 全量**；
   该文件不在本任务白名单（不可改）
3. `Native/zplugin/zplugin.h:417` —— 仅注释提及（RenderOffsetF 引用），无 include 无代码依赖

**shim 决策：保留**（非删除）。理由：消费者 2 存在且不可改、且需要 §②；§② 不提升
导致生产头不含重力，故 shim = `#include "zplanet/planet_math.h"`（薄转发）+ §② 原文
+kGravityMu。若未来 §② 另立 tests 头或窗口程序改指生产头+物理模块，shim 可删。
shim 对 `zplanet/planet_math.h` 的解析：zrender_test 经新 zplanet INTERFACE include
根；zrhi_d3d12_window 经 zrhi 既有 PUBLIC include 根（Native/）——零 CMake 增量即编译通过。

### R6. 构建与测试数字

- `Tools/build_native.ps1`（唯一构建通道，未裸调 cmake）：改前 ALL GREEN → 改后
  **ALL GREEN**（configure + build + test + dll 部署 4/4），/W4 /WX 下零警告
  （零警告由 /WX 强制：任何警告即构建失败）
- `zrender_test.exe`：**354 用例 / 778763 断言 / 0 failed**（改前=改后，只增不减
  判据满足——数量不变且逐位一致）
- 15 条行星 doctest 全绿（含于上述 354/778763，逐位证据见 R4）
- `zrhi_d3d12_window.exe` Release 默认构建随批重建成功（shim 路径 /WX 零警告）

### R7. 白名单核验（git status --porcelain）

```
 M Native/CMakeLists.txt            ← 白名单
 M Native/tests/planet_math.h       ← 白名单
 M Native/tests/test_planet.cpp     ← 白名单
?? Native/zplanet/                  ← 白名单（新增）
?? Docs/Logs/TASK-B-PLANET-03.md    ← 本日志（白名单）
?? Docs/Logs/TASK-B-PROBE-01.md / TASK-B-PROBE-02.md / Tools/Probes/out/env-*.json
                                    ← 施工前即存在的 untracked（基线 git status 快照有据），未触碰
```

### R8. 遗留风险

1. 生产头 include 集合调整：`+<cstdint>`（§④ 的 uint32_t/int32_t 此前靠传递
   包含获得，现自足声明）、`-<vector>`（§①③④ 不再使用 vector；shim 保留
   `<cmath>/<vector>` 供 §② 自足）。均为 include 结构调整，类型/求值零影响
   （R4 逐位证据覆盖）。未来消费方若依赖 planet_math.h 传递提供 `<vector>` 需自带。
2. `zrhi_d3d12_window.cpp` 对 shim 的解析依赖 zrhi 的 PUBLIC include 根（Native/）
   ——既有机制，未改动；若未来 zrhi 收窄 PUBLIC include，窗口程序目标需补
   `target_link_libraries(... zplanet)`。
3. B-PLANET-04/06 消费姿势：`target_link_libraries(<target> zplanet)` 后
   `#include "zplanet/planet_math.h"`（CMake 注释已写明）；zrender/zplugin 保持零依赖。
4. 预存 `-s` 全量跑 SIGSEGV（test_zfg_env.cpp:117，R4-③）与本任务无关，建议另开任务。
5. 证据文件在临时目录（Temp/planet03/），系统清理后不可复现——本报告已固化
   md5 与 diff 结论；如需复验，验收代理可按 R4 方法重跑。

### R9. 验收建议

**建议作为验收 PASS 候选**。核心判据（15 条行星 doctest 改指生产头 + 逐位回归
diff 为空 + ALL GREEN /WX + ≥354 用例不减 + 白名单零越界 + zrender/zplugin 零改动）
全部满足并附证据（R3/R4/R6/R7）。验收代理可重点复核：R4 的归一化 diff 方法、
R3 的源级 diff 结论、R7 的 git status 快照。

## 验收清单（主控写，验收代理逐条跑并出证据）

- **Z1 范围**：tracked diff 恰 4 处（CMakeLists.txt / planet_math.h shim / test_planet.cpp / 新增 zplanet/planet_math.h）；zplugin.h/cpp、zrender/**、zupscaler/**、zkernel/**、UnityProject/** 零改动；看板/HANDOFF/WORKLOG 未被施工触碰
- **Z2 语义零改动**：抽 3 个函数（建议 PlanetTerrainHeightM / FloatingOrigin::Relocate / CubeSphereDir）——生产头与 `git show HEAD:Native/tests/planet_math.h` 对应段 diff 仅注释/include/§② 移除；shim 的 §② 块与 HEAD 逐字节一致（施工称 BYTE-IDENTICAL，亲自 diff 复核）
- **Z3 逐位回归**：亲跑 `zrender_test.exe -s` 的行星 15 用例段（或全量），用施工同款方法（行号归一化 diff）复现"归一化后为空"；354 用例/778,763 断言 0 failed；RDTSC MESSAGE 行机器噪声不计
- **Z4 构建**：亲跑 build_native.ps1 ALL GREEN（/WX 零警告）；zrhi_d3d12_window.exe 随批重建成功（shim 解析经 zrhi PUBLIC include 根的机制核实在 CMake diff 里）
- **Z5 消费者纪律**：zrhi_d3d12_window.cpp 零 diff（白名单外消费者以 shim 满足，未改文件）
- **Z6 规范**：4 文件 UTF-8 无 BOM/LF；日志结构完整（简报/施工报告 R1-R9/验收清单/验收结论）

验收输出 PASS/FAIL + 逐条证据。你独占原生构建链。

## 验收结论（验收代理，2026-11-14，独立亲跑）

**总结论：PASS（Z1–Z6 全过）**

### Z1 范围 —— PASS

- `git status --short` / `git diff --numstat` 核验：施工 tracked 改动恰 4 处——
  `Native/CMakeLists.txt`(+12/1)、`Native/tests/planet_math.h`(+18/452)、
  `Native/tests/test_planet.cpp`(+2/1)、新增 `Native/zplanet/`（内**仅**
  `planet_math.h`，469 行 / 22,633 字节，无多余文件）
- 白名单外零 diff 实证（`git diff HEAD --` 全空）：`Native/tests/zrhi_d3d12_window.cpp`、
  `Native/zplugin/zplugin.h`、`Native/zplugin/zplugin.cpp`、`Native/zrender/**`、
  `Native/zupscaler/**`、`Native/zkernel/**`、`UnityProject/**`
- `HANDOFF.md` / `WORKLOG.md` / `Docs/LLM-HANDOFF-NOTES.md` 零 diff（看板/HANDOFF/
  WORKLOG 未被施工触碰）
- 备注：验收窗口内出现 `TASKS.md` +1 行（X-DIAG-02 backlog 登记）与 untracked
  `Docs/Logs/TASK-B-PLANET-05.md`——内容为指向本任务 R4-③/R8-4 所发现预存
  SIGSEGV 的登记/派发，属主控并行动作（R7 施工时快照无此两项），非施工越界

### Z2 语义零改动 —— PASS

- HEAD 基线确认：`git show HEAD:Native/tests/planet_math.h`（587 行）为 shim 化前
  最后全量原文（最后触碰提交 a8af2f5，HEAD=68d0944 未再动），直接作对照
- 全量 diff（HEAD 原文 vs `Native/zplanet/planet_math.h`）hunk 清点**仅 5 类**：
  ① 文件头注释重写 ② include 增删（+`<cstdint>` / -`<vector>`）③ kGravityMu 行
  移出 ④ §② 块整体移除（HEAD 210–333）⑤ §④ banner 措辞「①②③」→「①③」
  ——§①③④ 全部函数/结构体/常量正文 0 hunk 触碰
- 指定 3 函数抽取逐字节 diff（awk 按 signature→收尾 brace 切片）：
  `FloatingOrigin::Relocate` 13/13 行、`PlanetTerrainHeightM` 17/17 行、
  `CubeSphereDir` 6/6 行，三对全部 **IDENTICAL**
- shim §② 块：`HEAD 210–331 行` vs `shim 30–151 行`，122/122 行 diff **空**，
  md5 双方同为 `e324d24a57e929860880ed6dca414353`（施工 R3 称 BYTE-IDENTICAL，
  亲自复现成立）；`kGravityMu` 行原文逐字节一致

### Z3 逐位回归 —— PASS

- 独立重建基线（不依赖施工的临时产物）：`git worktree add --detach <仓外临时>
  HEAD` 检出 68d0944，经该 worktree 内 `Tools/build_native.ps1` 亲跑构建 ALL
  GREEN，得基线 `zrender_test.exe`（2,414,080B）；改后 exe（2,412,544B）来自
  主仓亲跑构建（Z4）
- `-s` 全量采集 3 次：基线×2 + 改后×1，各 **2,102,226 行**（行数三方一致）；
  行星段按 TEST CASE 头状态机抽取，各 **5,433 行 / 15 TEST_CASE / 1,791 条
  SUCCESS 断言**——与 R4 报告「15 用例 / 1791 断言 / 5433 行」逐项吻合
- 确定性自证：基线两次采集归一化后自 diff **空**（md5
  `5f76ebd5389f5f844906dd7afff38e8d` ×2）
- 基线 vs 改后（归一化 = 绝对路径前缀统一 + `test_planet.cpp(NN)` 行号归一）：
  diff **空**，三方 md5 同上——1,791 条断言的表达式文本与 `values:` 求值数值
  逐字节一致（含全部 memcmp 逐位断言）
- 原始（未归一化）diff 排除 file:line 元数据行后 **0 行**差异：+1 行号位移
  （基线 26/29 → 改后 27/30，来自 include 行 1→2 插入）与路径前缀为全部差异
- 默认模式全奏：基线 vs 改后归一化后**仅 1 行**差异 =
  `test_zkernel_datakernel.cpp(551)` RDTSC 实测 MESSAGE（a1/a2 = 5 vs 7
  cycles/op，机器噪声带内，验收清单明示不计；施工报告为 8/9 vs 4/5，同类）；
  尾部 `354 | 354 passed | 0 failed` / `assertions: 778763 | 778763 passed` /
  `Status: SUCCESS!` 两侧逐字节一致
- 预存问题复现（非本任务引入）：`-s` 全量在 `test_zfg_env.cpp(117)` SIGSEGV
  （doctest SEH 捕获记 1 failed/157 skipped），崩溃输出行两侧同位（均 2102221
  行）——与 R4-③ 一致；X-DIAG-02 已由主控登记，本验收不处理

### Z4 构建 —— PASS

- 亲跑 `Tools/build_native.ps1`（唯一构建通道，未裸调 cmake）：改后主仓
  **ALL GREEN**（configure+build+test+dll 部署 4/4），/W4 /WX 下零 warning 行；
  日志中 "FAILED" 字样均为负例测试的预期输出（fg/fsr/bilinear 等运行期负例），
  非构建失败
- `zrender_test.exe`：**354 用例 / 778,763 断言 / 0 failed**（基线同数，只增不减
  判据满足）
- `zrhi_d3d12_window.exe` 随批重建成功（`zrhi_d3d12_window.vcxproj -> ...exe`）
- shim 解析机制核实（CMake diff 内）：既有 `foreach(zkernel zrhi zrender_core
  zupscaler zplugin)` 赋 PUBLIC include 根 = `Native/`（本任务未触碰），
  `zrhi_d3d12_window` 链接 zrhi 继承该根 → shim 的 `planet_math.h` 与其嵌套
  `zplanet/planet_math.h` 均经此根解析，窗口目标零 CMake 增量；新增
  `zplanet` INTERFACE 目标（include 根 = Native/，不编 TU）仅被 zrender_test
  链接，zrender/zplugin 保持零依赖

### Z5 消费者纪律 —— PASS

- `git diff HEAD -- Native/tests/zrhi_d3d12_window.cpp` 为**空**（白名单外
  消费者零改动，以 shim 满足）
- 消费者清单复核（R5）：`test_planet.cpp:17` 已改指生产头 + 经 shim 取 §②；
  `zrhi_d3d12_window.cpp:240` 经 shim（§①③④ 生产头 + §② 原文）——窗口程序与
  doctest 继续验证同一份 §①③④ 实现；`zplugin.h:417` 仅注释提及，无 include

### Z6 规范 —— PASS

- 4 个施工文件 + 本日志：首 3 字节均非 `EF BB BF`（无 BOM），CR 字节计数均
  **0**（纯 LF）
- 日志结构完整：任务简报 / 施工报告 R1–R9 / 验收清单 / 验收结论（本节）

### 给主控的备注

1. 验收方法与施工 R4 的差异：基线不经施工临时目录复用，而是从 HEAD(68d0944)
   独立 worktree + 独立构建重建，结论独立成立；归一化额外多一层绝对路径前缀
   统一（基线 worktree 路径 vs 主仓路径），不影响逐位判定
2. 证据文件留存于仓外临时目录 `C:\Users\1\AppData\Local\Temp\planet03_acc\`
   （两次构建日志 / 三次 `-s` 采集 / 三次行星段抽取与归一化产物 / 默认模式
   采集）；基线 worktree 已 `git worktree remove --force` 拆除，主仓工作树无
   验收残留（当前 git status 与验收开始时一致）
3. 验收遵守铁律：未修代码、未 commit；施工 4 处改动仍为未提交工作树状态，
   待主控闭环流程处置
4. X-DIAG-02（`-s` 全量 SIGSEGV）确认基线即有（本验收在 HEAD 基线上复现同位
   崩溃），与 B-PLANET-03 施工无关
