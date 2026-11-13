# TASK-B-PLANET-05 —— SetCameraOrigin + GetCameraOrigin 实现（§3.3 协议）

- **轨道**：B / PLANET（设计稿 §11-05；依赖 B-PLANET-02 已闭环 f5e6aa1——接口冻结）
- **状态**：todo
- **派发**：主控，2026-11-14

## 任务简报（主控写）

### 0. 使命

行星 ABI 十个导出里最小的一对先落地（S 级）：实现 `ZR_Unity_SetCameraOrigin(double x,y,z)` / `ZR_Unity_GetCameraOrigin(double*)`——B-PLANET-04 的 chunk 通道要消费这个相机原点做 double 域相减，它是精度链路的第一环。

### 1. 做什么（按设计稿 §3.3/§14 协议逐条实现）

1. **实现**（Native/zplugin/zplugin.cpp 或设计稿指定的实现落点）：
   - `SetCameraOrigin`：NaN/Inf 拒绝（`ZR_UNITY_ERR_ARGS`——注释已在 B-PLANET-02 扩充）；跨帧持久（`BeginScene` 不清，D2）；跨线程语义按 §8（主线程写+渲染线程读的同步口径照设计稿，若 §8 指定 SRWLOCK/快照规则照做）
   - `GetCameraOrigin`：out 空指针 → `ZR_UNITY_ERR_NULL`（沿用既有错误码约定）
   - 初始值 (0,0,0)；每帧由 RenderFrame 消费（本任务先存取+读回，RenderFrame 消费行为属 B-PLANET-04——**本任务不改渲染路径**）
2. **def**：这两个符号加入 `Native/zplugin/zrender.def` 导出（首个行星导出落 def——B-PLANET-02 口径"def 随实现任务追加"自此开始）；C# 绑定已声明（B-PLANET-02），实现后 P/Invoke 真通
3. **门控**：`ZrPlanetNative.EnsurePlanetChannelReady()`（≥3）已在——测试走门面调用验证
4. **doctest**（设计稿 §11-05 指定）：Set/Get round-trip 逐位（含大数值 6.4e6m 量级）、NaN 拒绝、Inf 拒绝、跨帧持久（两次 Begin/EndFrame 间不清）、初始零
5. **构建**：build_native.ps1 ALL GREEN /WX；zrender_test.exe ≥354 只增不减；dll 重部署（导出数 46→48，施工报告记录 def diff）

### 2. 白名单

- 修改：zplugin.cpp（+def）、zrender.def、（如实现需要）zrender 内部消费文件、对应测试文件、dll 产物、本日志
- 不碰：zplugin.h 签名（接口冻结——**发现签名要改=MISSING+停手回主控**）、UnityProject/**（绑定已在）、TASKS/HANDOFF/WORKLOG
- 环境坑：构建走 Tools/build_native.ps1

### 3. 铁律

- 约法三章：施工不 commit、不改看板；UTF-8 无 BOM/LF；/WX 零警告
- 设计稿是协议权威：持久性/拒绝规则/线程语义偏离=回主控裁决

### 4. 完成判据

- 两函数实现+def 导出 46→48；round-trip/拒绝/持久 doctest 全绿
- C# 门面真调通过（EditMode 或原生证据）
- 全量绿；dll 双侧部署一致

## 施工报告（施工代理，2026-11-14）

### R1. 改动文件清单

```
 git diff --numstat（tracked）：
 125   0  Native/tests/test_zrhi_unity_target.cpp
  43   0  Native/zplugin/zplugin.cpp
   4   0  Native/zplugin/zrender.def
   -   -  UnityProject/Assets/Plugins/ZRender/zrender.dll（binary 317440→317952 B，构建脚本自动部署）
 新增：无（未新增任何文件；doctest 并入既有 test_zrhi_unity_target.cpp，零 CMake 增量）
```

- `Native/zplugin/zplugin.cpp`：`+<cmath>` 1 行；文件尾（场景提交组之后、`extern "C"`
  收口之前）新增行星相机原点块——匿名 namespace 静态 `g_planetCameraOrigin[3]`
  + `ZR_Unity_SetCameraOrigin` / `ZR_Unity_GetCameraOrigin` 两实现（+43 行，全部为本任务）
- `Native/zplugin/zrender.def`：顶部任务注释 2 行 + EXPORTS 节 2 符号（+4 行），
  符号位置对齐 zplugin.h 组 1（`ZR_Unity_GetSceneStats` 之后、`ZK_WorldCreate` 之前）
- `Native/tests/test_zrhi_unity_target.cpp`：文件头覆盖清单追加 4 行 + includes
  （`<cstring>`/`<limits>`）+ 文件尾 5 个 TEST_CASE（+125 行）
- **零改动**（git status 核验）：`zplugin.h`（签名冻结——未触碰，实现与 B-PLANET-02
  冻结签名逐字段一致）、`Native/CMakeLists.txt`、`zrhi/**`/`zrender/**`（渲染路径零字节
  改动——本任务不改渲染路径，RenderFrame 消费属 B-PLANET-04）、UnityProject/** 源码
  （绑定已在，dll 重部署为构建脚本步骤 4/4 自动完成）
- 编码核验：3 个源文件均 UTF-8 无 BOM（首字节 `2f 2f 20`/`3b 20 7a`）、0 个 CR 字节（LF）
- 不碰：TASKS/HANDOFF/WORKLOG ✓；施工未 commit ✓

### R2. 实现要点（状态落点 / 锁 / 持久 / 拒绝）

**状态落点**：`zplugin.cpp` 匿名 namespace 进程级静态 `double g_planetCameraOrigin[3] = {0,0,0}`。
理由：本组是纯存取（设计稿 §8 表：GPU 触点 = 无），不属于 D3D12 目标模块
（`zr::unity_target`），故不进 zrhi——与 `g_rhiBackend`、网格句柄表同为 zplugin 层状态。
**本任务只做存取 + 读回；RenderFrame 的消费（chunk 模型平移 double 域相减）属
TASK-B-PLANET-04**（届时按其架构落点接管状态，迁移是机械操作，doctest 锁定 ABI 行为不变）。

**锁**：无（设计稿 §8 表明文：SetCameraOrigin/GetCameraOrigin 渲染线程、锁 = 无——
单线程约定下读写不竞争，与 zplugin 既有 `g_rhiBackend` 同口径；zplugin.h:198 约束不变）。

**拒绝规则**（§3.3）：`std::isfinite` 逐分量判——任一分量 NaN/Inf → `ZR_UNITY_ERR_ARGS`
（复用 zplugin.h:262 的 B-PLANET-02 扩充口径，不新增错误码）；拒绝时状态保持上一次
成功值（doctest 断言）。`GetCameraOrigin(nullptr)` → `ZR_UNITY_ERR_NULL`。

**持久性**（§3.3「BeginScene 不清」）：结构性保证——原点状态不在 `zr::unity_target`
的场景结构（`BeginScene` 只清 `drawCount`，相机与网格句柄表保留，zplugin.h 场景提交语义
「保留」侧）也不在 `unity_bridge`，帧边界函数物理上碰不到它；doctest 以
Begin/EndScene ×2 + Begin/EndFrame ×2 实测取证。

**初始值 / 末次生效**：初始 (0,0,0)（未 Set 就 RenderFrame 按行星中心 = 世界原点，
合法初始态不报错，§3.3 末条）；Set 覆写三元素，同帧多次调用最后一个生效（doctest 断言）。

**幅值语义**：只拒 NaN/Inf，无幅值钳制——DBL_MAX round-trip 逐位成功（doctest 锁定；
`1e308` 级有限值合法，double 域相减的溢出防护属消费方 B-PLANET-04 的域）。

### R3. def diff（46→48）

```diff
--- a/Native/zplugin/zrender.def
+++ b/Native/zplugin/zrender.def
@@ -12,6 +12,8 @@
 ; TASK-B-UNITY-03 新增：场景提交七导出（相机 + 网格 + 绘制 + 场景统计）。
 ; TASK-B-UNITY-04 新增：帧同步三导出（双槽资源 / 可读槽位 / 等待耗时统计）。
 ; TASK-B-UNITY-05 新增：整面回读一导出（真实脚印 + 本侧状态转换）。
+; TASK-B-PLANET-05 新增：行星相机原点两导出（Set/GetCameraOrigin；首个行星导出落
+; def——B-PLANET-02 口径"def 随实现任务追加"自此开始；后续行星导出随 04/06 追加）。
 LIBRARY zrender
@@ -48,6 +50,8 @@ EXPORTS
     ZR_Unity_BeginScene
     ZR_Unity_EndScene
     ZR_Unity_GetSceneStats
+    ZR_Unity_SetCameraOrigin
+    ZR_Unity_GetCameraOrigin
     ZK_WorldCreate
```

导出表实测（dumpbin //EXPORTS 对 `Native/build/bin/Release/zrender.dll`）：
`48 number of functions`（46→48 ✓）；`ZR_Unity_SetCameraOrigin` ordinal 45、
`ZR_Unity_GetCameraOrigin` ordinal 33。EXPORTS 节内零行内注释（R4-01 教训：中文注释
只在 LIBRARY 前的头部区）。

### R4. doctest 清单与数字（设计稿 §11-05 五条全覆盖）

并入 `Native/tests/test_zrhi_unity_target.cpp`（该文件即 ZR_Unity_* C ABI 的无 GPU
单测落点；零 CMake 增量）。用例顺序：doctest 缺省 order-by=file（文件内按行号序），
初始零用例置于本组最前；全仓只有本组用例写原点状态，其余用例一律自 Set 起步，不依赖跨用例执行序。

| # | TEST_CASE | 断言数 | 覆盖 |
|---|---|---|---|
| 1 | `planet abi: GetCameraOrigin 初始零与空指针拒绝` | 5 | 非零哨兵读回 (0,0,0) 三分量；`nullptr` → `ZR_UNITY_ERR_NULL` |
| 2 | `planet abi: Set/Get round-trip 逐位（6.4e6m 量级 + 负零 + 无幅值钳制）` | 36 | 6 组值（零向量 / 1.0,-2.5,0.125 / **−6.4e6, 6.4e6, 6.371e6（kPlanetRadiusM 量级）** / 9.01e6,−9.01e6,4.0e6+0.5（B-TECH-04 maxOriginOffset 量级）/ −0.0 符号位 / **DBL_MAX 无幅值钳制**）×（Set OK + Get OK + 3×memcmp 逐位）；另同帧两次 Set 最后生效（4.5,−6.25,7.0） |
| 3 | `planet abi: NaN 任一分量拒绝（ERR_ARGS，状态保持上次成功值）` | 9 | qNaN/sNaN 各分量位（x/y/z 三位 + sNaN）→ `ZR_UNITY_ERR_ARGS`；拒绝后读回仍 (10,20,30) |
| 4 | `planet abi: Inf 任一分量拒绝（ERR_ARGS，状态保持上次成功值）` | 8 | +Inf/−Inf 各分量位 → `ZR_UNITY_ERR_ARGS`；拒绝后读回仍 (−11.5,22.25,33.0) |
| 5 | `planet abi: 跨帧持久（Begin/EndScene 与 Begin/EndFrame 均不清）` | 3 | Set(1234.5,−6789.25,6.4e6) → BeginScene/EndScene ×2 + BeginFrame/EndFrame ×2（无设备返回 NO_DEVICE，不论返回值不得触状态）→ memcmp 逐位不变 |
| 合计 | 5 用例 | **61** |  |

全量数字（亲跑）：

- 原生 `zrender_test.exe`：**359 cases / 778,824 assertions，0 failed**
  （基线 354 / 778,763 → **+5 cases / +61 assertions 全为本任务**，只增不减 ✓ ≥354）
- 构建链：`Tools/build_native.ps1` **ALL GREEN**（configure + build + test + deploy 4/4）；
  编译旗标实读 `Native/CMakeLists.txt:27` = `/W4 /utf-8 /MP /WX`，构建零警告
- **渲染路径零改动**：`zrhi/**`、`zrender/**`、`zkernel/**`、`zupscaler/**` 零字节 diff

### R5. dll 双侧重部署

- 构建脚本步骤 4/4 自动拷贝：`Native/build/bin/Release/zrender.dll` →
  `UnityProject/Assets/Plugins/ZRender/zrender.dll`
- 双侧 sha256 **逐字节一致**：`8f3e0e6335330c4f54fa1cb9f65c820933ee4d595af67292a5e1a3ad3dfbe450`

### R6. C# 绑定与门面（只读核验 + EditMode 回归）

- 绑定签名（B-PLANET-02 已声明，`ZrNative.cs:129/132`）与冻结头逐字段比对一致：
  `int ZR_Unity_SetCameraOrigin(double,double,double)` ↔ `int(double,double,double)`；
  `int ZR_Unity_GetCameraOrigin([Out] double[])` ↔ `int(double*)`（double[] 封送 = double*）
- 门控：`ZrPlanetNative.EnsurePlanetChannelReady()`（≥3）+ `ApiVersion()==3`（B-PLANET-02
  已落，本任务未动）——EditMode 真跑：
  `Tools/run_tests.ps1 -Segments editmode` → Unity exit 0，fresh xml
  `TempLogs/runtests_editmode.xml`：**total=1182 passed=1182 failed=0 skipped=0**
  （与 B-PLANET-02 窗口总数一致，本次窗口无并行漂移；machine quiet check BUSY
  Taskmgr 24.5%——仅影响耗时数字可比性，不影响 PASS/FAIL）。新 dll 侧
  ZrPluginTests 8/8 全绿（含 `EnsureLoaded_ReturnsApiVersion3` 真 dll 与
  `PlanetNative_ChannelGate_PassesWithApiVersion3` 门控）
- 口径说明：P/Invoke 懒解析——两个新 extern 在托管侧**首次调用时**才解析符号；
  现无 EditMode 用例真调它们（首个真调属 B-PLANET-07 WorldOrigin 接线）。本任务
  真通证据为三层原生面：① def 导出（dumpbin 48，两符号在表）② 托管声明与冻结头
  逐字段一致 ③ C ABI doctest 359 全绿走的就是 dll 同一实现（静态链接同 TU）

### R7. 遗留风险

1. **B-PLANET-04 消费落点**：原点状态现为 zplugin.cpp 私有静态，zrhi 层渲染路径
   不可直读——B-PLANET-04 集成 RenderFrame 时需按其架构落点接管/迁移该状态
   （其白名单覆盖渲染路径文件；迁移机械，本任务 doctest 锁定行为，ABI 不变）。
   这是设计稿未指定实现落点下的最小组装面选择，非协议偏离。
2. **托管侧首次真调在 B-PLANET-07**：门控 `EnsurePlanetChannelReady` 仍是唯一护栏
   （B-PLANET-02 遗留①延续：绕过门控直接 P/Invoke 未实现符号会崩溃——本任务后
   Set/Get 两符号已可解析，该风险对本组两函数已消除，其余行星符号仍在）。
3. **精度链路下一环**：本组只保证 double 存取逐位无损能力；6.4e6m 大数值消费端
   （chunk 模型平移 double 域相减）精度属 B-PLANET-04/B-PLANET-10 探针口径。

### R8. 白名单与铁律自检

- 白名单：zplugin.cpp ✓、zrender.def ✓、测试文件 ✓、dll 产物 ✓、本日志 ✓——
  zplugin.h 零字节（签名冻结未触碰，无 MISSING）、CMakeLists 零字节、
  UnityProject/** 仅 dll 重部署（构建脚本自动步骤）✓
- 约法三章：施工未 commit ✓、看板未动 ✓、UTF-8 无 BOM / LF ✓、/WX 零警告 ✓
- 协议符合性：§3.3 逐条（NaN/Inf 拒绝 / 跨帧持久 BeginScene 不清 / 初始零 /
  末次生效 / ERR_NULL）+ §8 线程（渲染线程无锁）——**零偏离设计稿**，
  无需主控裁决项

## 验收清单（主控写，验收代理逐条跑并出证据）

- **C1 范围**：tracked diff 恰为 zplugin.cpp/zrender.def/test_zrhi_unity_target.cpp/dll/本日志 5 处；**zplugin.h 零 diff（签名冻结）**；zrhi/**、zrender/**（渲染路径）零 diff；UnityProject 源码零改动；TASKS/HANDOFF/WORKLOG 未被施工触碰
- **C2 def**：EXPORTS 实数 48（46→48），两符号插在 GetSceneStats 与 ZK_WorldCreate 之间；中文注释只在 LIBRARY 前头部区（R4-01 教训）；dumpbin 实测可复核
- **C3 协议**（读码+亲跑 doctest）：NaN（q/sNaN 三分量）/±Inf 拒绝→ERR_ARGS 且状态保持上次成功值；Get nullptr→ERR_NULL；初始 (0,0,0)；同帧多次 Set 末次生效；跨帧持久（Begin/EndScene×2+Begin/EndFrame×2 不清）；round-trip 含 6.4e6m/9.01e6m/−0.0/DBL_MAX memcmp 逐位
- **C4 测试**：亲跑 build_native.ps1 ALL GREEN /WX 零警告；repo 根 zrender_test.exe ≥359 用例 0 failed（基线 354/778,763 → 359/778,824，+5/+61 归因本任务）；EditMode fresh xml ≥1182 failed=0（ZrPluginTests 8/8 含门控真 dll）
- **C5 dll**：sha256 双侧一致（8f3e0e63…）
- **C6 状态落点裁决（主控已决，验收核对披露属实）**：zplugin.cpp 匿名 namespace 私有静态=接受——最小组装面；B-PLANET-04 集成时接管/迁移（其白名单覆盖），doctest 已锁 ABI 行为
- **C7 规范**：3 源文件+日志 UTF-8 无 BOM/LF；四节结构完整

验收输出 PASS/FAIL + 逐条证据。并行提示：B-PROBE-02 施工在 Unity 侧跑——EditMode 若遇单实例占用按纪律等待，不杀进程。

## 验收结论（验收代理，2026-11-14）

**总结论：PASS**——C1–C7 全部通过，零 MISSING，零协议偏离，无回主控裁决项。验收全程亲跑亲测，零改动零 commit（仅本节追加至本日志）。

| # | 结论 | 证据（验收代理亲测，2026-11-14） |
|---|---|---|
| C1 范围 | **PASS** | `git diff --numstat` tracked 恰 3 文件：test_zrhi_unity_target.cpp(+125/0)、zplugin.cpp(+43/0)、zrender.def(+4/0) + dll(binary，`git cat-file -s HEAD:` 实测旧 317,440 B→新 317,952 B) + 本日志（untracked 新增）——合计 5 处；`git diff --stat -- zplugin.h` 零字节（签名冻结 ✓）；zrhi/**、zrender/**、zkernel/**、zupscaler/** 零 diff（渲染路径 ✓）；HANDOFF.md/TASKS.md/WORKLOG.md 零 diff ✓；UnityProject 除 dll 外零 diff（源码 ✓）；`git status Native/` 无未跟踪文件；其余 untracked（TASK-B-PROBE-01/02.md、Tools/Probes/out/env-*.json）为并行 B-PROBE 在途件，不属本任务、不影响本判定 |
| C2 def | **PASS** | def EXPORTS 节 awk 机械计数 **48** 行（46→48 ✓）；两符号插在 `ZR_Unity_GetSceneStats` 与 `ZK_WorldCreate` 之间（def 52–55 行实读 ✓）；中文注释只在 LIBRARY 前头部区（15–16 行，EXPORTS 节内零注释，R4-01 教训 ✓）；dumpbin（MSVC 14.44）实测 `48 number of functions` / `48 number of names`，`ZR_Unity_GetCameraOrigin` ordinal 33、`ZR_Unity_SetCameraOrigin` ordinal 45——与 R3 数字逐项一致 |
| C3 协议 | **PASS** | 读码：`std::isfinite` 逐分量→`ZR_UNITY_ERR_ARGS`（拒绝不写状态）、Get `nullptr`→`ZR_UNITY_ERR_NULL`、匿名 namespace 静态初值 {0,0,0}、Set 覆写三元素（末次生效）——与 §3.3/§14 组 1 逐条对应；亲跑 `zrender_test.exe -s -tc="planet abi*"`：**5 cases / 61 assertions，全 passed 0 failed**。逐项：六组 round-trip（零向量 / 常规值 / −6.4e6,6.4e6,6.371e6 / 9.01e6,−9.01e6,4e6+0.5 / −0.0 符号位 / DBL_MAX 无幅值钳制）memcmp 逐位；qNaN/sNaN 三分量拒绝且状态保持 (10,20,30)；±Inf 拒绝且状态保持 (−11.5,22.25,33.0)；Begin/EndScene ×2 + Begin/EndFrame ×2 后 (1234.5,−6789.25,6.4e6) 逐位不变——§11-05 五条 doctest 覆盖属实 |
| C4 测试 | **PASS** | 亲跑 `Tools/build_native.ps1`：configure+build+test+deploy 4/4 **ALL GREEN**（旗标 `/W4 /utf-8 /MP /WX` 实读 Native/CMakeLists.txt:27，/WX 下通过=零警告）；全量 doctest **359 cases / 778,824 assertions，0 failed 0 skipped**；`--list-test-cases` 恰 5 个 "planet abi" 用例，`-tc` 过滤跑时 354 skipped 直接证明基线 354 + 新增 5（+61 assertions 归因本任务）；亲跑 `Tools/run_tests.ps1 -Segments editmode`：fresh xml `TempLogs/runtests_editmode.xml`（2026-11-14 04:08）`total=1182 passed=1182 failed=0 skipped=0`，ZrPluginTests fixture **8/8 Passed**（含真 dll 门控用例），全 xml 0 个 Failed 节点。C# 只读核验：ZrNative.cs:129/132 与冻结头逐字段一致（double[] 封送 double*）；门控 `EnsurePlanetChannelReady`（MinPlanetApiVersion=3u）与原生 `ZR_ApiVersion` return 3u 匹配 |
| C5 dll | **PASS** | sha256 双侧实测一致：`8f3e0e6335330c4f54fa1cb9f65c820933ee4d595af67292a5e1a3ad3dfbe450`（Native/build/bin/Release 与 UnityProject/Assets/Plugins/ZRender），均为 317,952 B |
| C6 状态落点 | **PASS** | 披露属实：zplugin.cpp 匿名 namespace `double g_planetCameraOrigin[3]` 私有静态（读码实见）；zplugin.h 零触碰；doctest 锁定 ABI 行为；B-PLANET-04 迁移路径已在 R7 披露——与主控裁决口径一致，最小组装面成立 |
| C7 规范 | **PASS** | 4 文件首字节实测无 BOM（`2f 2f 20` / `3b 20 7a` / `2f 2f 20` / `23 20 54`）、CR 字节计数 0（LF）✓；日志四节结构完整（简报 / 施工报告 R1–R8 / 验收清单 / 本验收结论）✓ |

### 验收备注（给主控）

1. **EditMode zombie-kill 说明**：本轮 run_tests.ps1 按其自带纪律第 5 条执行（results.xml 写入后 Unity 超 300s 未退出 → taskkill **本脚本自己启动的** PID，证据=xml）——**非** B-PROBE-02 并行占用：启动前实测无 Unity.exe 在跑，单实例纪律未触发；xml 内部数字完整（1182/1182/0/0），判据达成。机器 BUSY（Taskmgr 39.1%，同 R6 窗口现象）仅影响耗时数字可比性，不影响 PASS/FAIL。
2. **P/Invoke 真调口径**（R6 如实披露，验收认可）：托管侧两 extern 懒解析，首个真调在 B-PLANET-07；本任务"真通"证据 = ① dumpbin 两符号在导出表 ② 托管声明与冻结头逐字段一致 ③ C ABI doctest 全绿（与 dll 同实现静态链接同 TU）。
3. **并行在途件**：untracked 的 TASK-B-PROBE-01/02.md 与 Tools/Probes/out/env-*.json 属 B-PROBE 轨道，未纳入本任务判定。
4. 原生构建链本验收独占亲跑：build_native.ps1 与 zrender_test.exe（含 -tc 过滤抽取）均为验收代理直接执行，非转抄施工报告数字（转抄项仅 R3/R5 的序号/sha256——已由 dumpbin/sha256sum 实测复核一致）。
