# TASK-B-R9-10 自动跑分回归（基准基线门禁接入 run_tests）

状态: doing | 派发: 2026-10-31 | 施工: general-purpose 施工子代理（2026-10-31） | 验收: general-purpose 验收子代理（2026-10-31，PASS） | commit: （待填）

## 任务简报（主控写）

**目标**：R9 候选"GPU 工具链+自动跑分回归"——把现有基准（zkernel_bench 迁移吞吐/zrender_bench 合批/窗口程序帧耗计数）变成**可门禁的回归防线**：脚本采集→与入库基线 JSON 对比→超容差即红。防"性能悄悄倒退"（方法论：性能回归进 CI）。

**设计要点**：
1. `Tools/bench_regression.ps1`：
   - 采集：跑 `zkernel_bench.exe`（解析迁移吞吐 M 行/s、Compact ms）+ `zrender_bench.exe`（合批节省率/排序毫秒）+ `zrhi_d3d12_window.exe --lights` 的帧耗行（既有输出已含 ms）——**解析器对每个数字标来源**（stdout 格式化行，程序化输出优先：给三个 benchmark 程序加 `--json` 输出行（单行 JSON，不破坏现有人类可读输出））
   - 基线：`Tools/bench_baseline.json`（首次运行 `--update-baseline` 生成入库；含机器标记+日期）
   - 门禁：每指标容差（吞吐类 -20% 红、耗时类 +30% 红——时序抖动敏感，容差从宽）；`--gate` 模式 exit 1，默认 report-only（输出对比表）
   - 环境防抖：跑前查 `Tools/check_machine_quiet.ps1`（workbuddy 已建）不安静则 SKIP 并注明
2. 原生侧：三个 benchmark 程序加 `--json` 单行输出（zkernel_bench/zrender_bench 既有；window --lights 帧耗已有固定行，解析器直接吃）
3. run_tests.ps1 挂接：新增第 7 段 "bench"（**默认 report-only**，TestReport 汇总行显示；`BENCH_GATE=1` 环境变量才门禁）——不改既有六段语义
4. 单测：解析器纯函数（C++ 侧 json 行生成正确性 doctest；PS 侧解析用 Pester 或简化为内联自测——报告选型）+ 容差判定纯函数用例
5. 自测：全量六段回归不破坏；bench 段 report-only 亲跑产出对比表；`--update-baseline` 后再跑全 PASS；**注入回归**（临时把基线改低 50%）→ gate 红 → 恢复

**约束**：只动 `Native/tests/`（benchmark 程序）、`Tools/`、`Native/CMakeLists.txt`、`Tools/bench_baseline.json`、本任务日志；不碰 Unity；不 git commit；UTF-8 无 BOM/LF。

**交付物**：--json 输出+回归脚本+基线入库+run_tests 第 7 段+门禁验证+施工报告四节（含容差依据）。

## 施工报告（施工代理写）

施工: general-purpose 施工子代理 ｜ 日期: 2026-10-31 ｜ 状态: 施工完成待验收

### ① 交付清单（全部落盘，未 commit）

| 文件 | 变更 | 内容 |
|---|---|---|
| `Native/tests/bench_json.h` | 新增 | 单行 JSON 生成纯函数（`zbench::JsonEscape` / `JsonLineWriter`（Metric 定点 %.4f、MetricU64 整数、Finish 幂等防重复闭合）/ `HasJsonFlag`），三个 benchmark 程序共用；行前缀 `BENCH_JSON ` 供解析器无歧义提取 |
| `Native/tests/test_bench_json.cpp` | 新增 | doctest 12 用例 25 断言：转义矩阵/逐字节精确格式/单行保证（无 \n\r、前缀在行首、闭合完整）/数字两次运行逐位一致/argv 扫描/--json=1 不算开关/nullptr 安全 |
| `Native/tests/zkernel_bench.cpp` | 修改 | `main(argc,argv)` + `--json`：人类可读输出**之后**追加一行 BENCH_JSON（10 指标：spawn/each/migrate/compact 全量）；不带 --json 输出逐字节不变 |
| `Native/tests/zrender_bench.cpp` | 修改 | 同上（10 指标：A/B 场景 add/sort/batch/saved_pct/batches）；重构 main 把两次 RunScenario 结果先存变量再 Report（数字与既有人类可读行同源） |
| `Native/tests/zrhi_d3d12_window.cpp` | 修改 | ① D3D12 `RunLightsMode` 加 CPU 墙钟逐帧采集 + 末尾固定帧耗行 `[LIGHTS] frame_ms avg/min/max/n (frame1 excluded)` + `--json` 单行；② `forceHeadless` 参数（`--headless --lights` 跳过窗口/交换链走既有离屏回退——窗口 Present 垂直同步会把帧耗钉在刷新间隔、门禁失明）；③ Vulkan `RunLightsVulkan` 加同款帧耗行+`--json`（**有意不接** forceHeadless，见⑤-1）；④ main 的 `--headless` 进预扫描（原实现 `--lights` 先 return 导致 headless 永远置不上位），`--json` 全 argv 扫描 |
| `Native/CMakeLists.txt` | 修改 | zrender_test 源列表追加 `tests/test_bench_json.cpp`（一行） |
| `Tools/bench_regression.ps1` | 新增 | 采集（每程序默认跑 3 次取**逐指标中位**）→解析（BENCH_JSON 行优先，window 的人类可读 frame_ms 行为文档化 fallback）→与 `bench_baseline.json` 对比→对比表+verdict；`-Gate`/`-UpdateBaseline`/`-SelfTest`/`-SkipQuietCheck`/`-RunsPerProgram`；`BENCH_GATE=1` 环境变量等效 -Gate |
| `Tools/bench_baseline.json` | 新增 | 基线入库：schema/created/updated/machine（CPU 品牌）/machine_quiet（溯源标记，见⑤-3）/6 门禁指标 |
| `Tools/run_tests.ps1` | 修改 | ① ValidateSet+默认段追加 `bench`（第 7 段，**默认 report-only**：红只在表与本行 detail 呈现，永不使套件失败）；`BENCH_GATE=1`→子脚本 gate（RED/MISS→exit 1→bench 行 FAIL→套件 exit 1）；`BENCH_SKIP_QUIET=1`→透传 -SkipQuietCheck（调试逃生口）；bench 行映射 PASS/SKIP(REPORT-ONLY、机器忙)/FAIL(ERROR 崩溃或 exit≠0) ② 附带修复（详见⑤-4）：native 段 ctest 解析改候选列表（PATH ctest 写被拒时重试 VS BuildTools ctest），恢复 "ctest: 100% tests passed" 证据，判定语义零改动（仍仅补充性注释、失败仍 throw） |

### ② 关键选型与理由

1. **`BENCH_JSON ` 行前缀 + 单行**：解析器按 `^BENCH_JSON\s+(\{.*\})$` 提取，不依赖任何人类可读行格式（格式漂移=解析失败=门禁失明，故 C++ 侧用 doctest 逐字节钉死格式）。doctest 断言格式即简报设计点 4 的"C++ 侧 json 行生成正确性 doctest"。
2. **PS 侧解析/容差用例选型：内联自测（-SelfTest），不用 Pester**——仓库无 Pester 基建，引入新依赖违反最小改动；-SelfTest 零依赖、任何机器可跑（`Tools/bench_regression.ps1 -SelfTest`），28 用例含容差边界（恰 -20%/+30% 过、越界一毫米即红）、解析（噪声中提 3 行/坏 JSON 跳过/程序过滤/fallback 行）、中位、逐指标容差覆盖、Compare-Metrics 判定矩阵（OK/RED/MISS/NEW）、未知 kind 必抛（防 MetricDefs 笔误静默放行）。
3. **每程序 3 次取中位**：单次跑的小基准会被偶发后台尖峰污染（实测 compact_ms 出过 2.77ms vs 典型 1.4ms）；中位吸收单轮离群。CPU 频移的**整段漂移**中位救不了（见④），由逐指标容差覆盖。
4. **--lights 用 D3D12 headless（离屏）测帧耗**：窗口模式 Present 受垂直同步节拍（实测 VK 窗口帧均 10.4-11.4ms vs 离屏 1.3-2.0ms），低于刷新间隔的回归对门禁完全不可见；离屏路径每帧 WaitIdle 强同步、无 vsync，读数稳定。
5. **采集顺序**：先 check_machine_quiet（不安静→SKIP 并注明，exit 0；-UpdateBaseline 忙时拒绝并说明"带载写基线毒化未来全部对比"），再采集——护栏在数据之前，脏数据根本不产生。
6. **基线 schema**：数值与 kind 解耦——基线只存值+机器标记+日期；kind 与容差在脚本 `MetricDefs`（带可选 TolThroughput/TolTime 逐指标覆盖）。改门禁集合/容差不用动基线文件格式。

### ③ 容差依据（简报要求；2026-10-31 本机实测）

| 指标 | kind | 容差 | 实测依据 |
|---|---|---|---|
| migrate_rows_per_sec | throughput | **-35%**（逐指标覆盖） | 7 次独立运行 18.27M–30.80M 行/s，max/min=**1.69×**，且呈双模（{18–22M} 与 {26–31M} 两簇——CPU 频移整段漂移，短计时窗 ~3ms 无法跨簇平均；脚本级中位仍会整段落在慢簇）。-20% 类默认会在正常机器状态间假红；-35% 仍能钉住结构性回归（真 2× 回归 15M 级轻松击穿） |
| compact_ms | time | +30%（类默认） | 7 次运行 1.34–2.77ms（max/min 2.06×，含一次离群）；脚本级中位后典型 1.42–1.93ms，+30% 覆盖日常抖动；离群由中位吸收 |
| sort_ms_a / sort_ms_b | time | +30%（类默认） | 多轮 3.46–5.27ms（≈±18%），10 万项排序 30 次平均窗 ~120ms 稳定；+30% 足够 |
| saved_pct_b | throughput | -20%（类默认） | **确定性指标**（固定种子 RNG→batches=5120 恒定→94.88% 恒定），零抖动；-20% 只会挡真回归 |
| frame_ms_avg | time | +30%（类默认） | D3D12 headless 三连跑 1.79/1.93/2.03ms（±13%），冷启动首跑 2.86ms 离群由中位吸收；与 --parity 的 avg_frame_ms 同口径（排除首帧） |

容差判定为**从宽边界语义**：吞吐 `current ≥ baseline×(1-tol)`、耗时 `current ≤ baseline×(1+tol)`，恰好 -20%/+30% 判 PASS（自测用例钉死边界）。机器不安静时根本不产生数字（SKIP），容差只兜"安静机器上的正常抖动"——两层防线不重叠。

### ④ 自测证据（全部亲跑，2026-10-31）

1. **build_native 全绿**：`powershell -File Tools/build_native.ps1` → ALL GREEN；doctest **270 用例 / 717,652 断言 / 0 failed**（任务基线 258 → +12，全部来自新增 test_bench_json.cpp；258 用例全在）。ctest 补充证据 "100% tests passed (1 entry: zrender_test)"。
2. **--json 三程序亲跑**：zkernel_bench / zrender_bench / zrhi_d3d12_window 各产出单行 BENCH_JSON（样例见 ⑤-2 前的对话证据）；不带 --json 时人类可读输出与旧行为一致（zrender_bench 两跑对照确认零变化）。
3. **--lights 既有模式回归零破坏**：`--draw`（D3D12+VK headless）→ FIRST DRAW OK；`--lights` 窗口（D3D12+VK）→ LIGHTS OK；`--lights --backend both` → RG BARRIER PLAN PARITY EQUAL + CENTER PIXEL PARITY maxDelta=0。
4. **run_tests 第 7 段**：`-Segments bench` → exit 0；机器忙时 bench 行 SKIP（护栏真实生效的现场证据：java 25.3%/Taskmgr 23.9% 两次拦截采集与 -UpdateBaseline）；静默窗口内 report-only → 对比表 6 行 + "BENCH_VERDICT: PASS (report-only; 6/6 within tolerance)"。
5. **gate 全 PASS**：恢复基线后 `-Gate` → 表格 6/6 OK、exit 0（经 run_tests：bench 行 PASS、套件 ALL PASS exit 0）。
6. **注入回归（V3 预演，验收可复做）**：基线 6 指标全部 ÷2 → `-Gate` → **4 RED**（compact_ms +34.0% / sort_ms_a +104.1% / sort_ms_b +90.0% / frame_ms_avg +106.2%；吞吐类基线减半反而显改善故 OK，符合方向语义）→ **exit 1**；经 run_tests（`BENCH_GATE=1`+`BENCH_SKIP_QUIET=1`）→ bench 行 **FAIL**、TestReport "RESULT: FAILED (1 failed: bench)"、**套件 exit 1**；恢复基线（备份字节级还原）→ 6/6 PASS、exit 0。红→绿→红→绿四段日志齐全。
7. **SelfTest**：`-SelfTest` → 28/28 ok，exit 0。
8. **run_tests 六段不破坏**：`-Segments native` → PASS 270/717,652 exit 0（本次未跑 Unity 段——任务纪律；editmode/playmode/arch/config/naming 五段代码零改动，diff 仅 param 集合+注释+ctest 候选解析+新增第 7 段块）。

### ⑤ 遗留与风险（如实）

1. **发现 B-R7-02 潜在缺陷（未修，超本任务范围）**：`--backend vulkan --headless --lights`（本任务首次组合出的离屏 VK lights）readback **全零**（探针取证 center/strip 前 8 字节含 alpha 全 00——copy 结果不可见，非"渲染黑"），`groundLit=0/anim=0/5` → VULKAN LIGHTS FAILED exit 1；窗口模式（B-R7-02 验证过的路径）复测 **OK** 未被本任务破坏。处置：VK 路径**有意不接 forceHeadless**（代码注释+本条留证），`--headless --lights` 在 VK 下保持旧行为（建窗）；建议另立任务查 VK 离屏 readback。
2. **基线 machine_quiet 溯源标记为 SKIPPED**：建基线时本机持续忙（FinalShell java 20-49% + Taskmgr 20-26%，用户进程，未杀）——护栏如实拒绝带载建基线多次，为不阻塞交付按 `-SkipQuietCheck` 建基线并在 JSON 打标；对比时每次黄字提醒。**影响**：compact_ms 基线 1.93ms 偏高（安静态约 1.35-1.65），该指标门禁偏松，其余 5 指标在安静区间内。**建议验收在安静机器重跑一次 `Tools/bench_regression.ps1 -UpdateBaseline`（一条命令，自动覆盖），或闭环后主控择机重跑**。
3. **gate 模式在带载机器会 SKIP（按简报"不安静则 SKIP"）**——若需在本机验证 gate 判定链，用 `BENCH_GATE=1 BENCH_SKIP_QUIET=1`（本任务留的逃生口，④-6 即此路径）。
4. **附带发现（已顺手修复，判定语义零改动）**：本机 PATH 上的 MSYS2 ctest 近期起被拒写 build 树（"Cannot create log file: LastTest.log"，LastTest.log 停在 10-30 08:17；非本任务改动引入），run_tests native 段原会降级为 "ctest: binary not usable here"。已改为候选列表（PATH ctest 失败→重试 VS BuildTools ctest），实测恢复 "ctest: 100% tests passed"。同因导致 bash 直调 MSYS2 cmake 失败——build_native 的探测回退机制（VS cmake）一直在正确兜底，无需改动。
5. **仓库里与本任务无关的并行 WIP**（PipelineManager.cs/Volume*/TASK-A-R9-08.md 等另一任务改动）：本代理未触碰；`zrender.dll` 为本任务构建产物变更（tracked 二进制，按既有口径提交前 `git checkout --` 丢弃）。
6. **未做**：Unity 段（editmode/playmode/arch/config/naming）按任务纪律未执行——挂接只是 run_tests.ps1 的纯增段编辑，六段语义经 diff 复核零改动，native 段亲跑绿。


## 验收清单（主控写）

- [ ] V1 build 全绿（用例数只增不减，数字写入结论）
- [ ] V2 亲跑 bench 段 report-only：对比表产出、数字与基线同数量级；gate 模式全 PASS exit 0
- [ ] V3 注入回归复做：基线改低 → gate 红 → 恢复（证据）
- [ ] V4 容差判定纯函数用例真实（行号）；--json 输出稳定可解析（两次运行格式一致）
- [ ] V5 六段回归不破坏（run_tests 全绿）+文件规范+施工报告完整（容差依据）

## 验收结论（验收代理写）

验收: general-purpose 验收子代理 ｜ 日期: 2026-10-31 ｜ 结论: **PASS（5/5 全过）**——全程未跑 Unity、未改任何代码、未 commit；基线文件验后字节级还原（sha1 4c47ac9c 前后一致）。

- **V1 PASS**：`build_native.ps1` 亲跑 ALL GREEN——doctest **270 用例 / 717,652 断言 / 0 failed**（上任务基线 258/717,626，见 3b8f0f3 → **+12 用例/+26 断言**，全部来自新增 test_bench_json.cpp；施工报告①写"25 断言"实为 26，笔误不影响交付）；`run_tests -Segments native` 复跑再证 270 PASS + "ctest: 100% tests passed"（⑤-4 候选修复实测生效）。
- **V2 PASS**：安静窗口亲跑 report-only——对比表 6 行全 OK、数字与基线同数量级（migrate 31.4M vs 27.6M、compact 1.27 vs 1.93、sort 2.85/2.60 vs 2.87/2.82、saved_pct_b 恒 94.88、frame 1.53 vs 1.55）；`BENCH_GATE=1` gate **6/6 OK** "PASS (gate on; 6/6 within tolerance)"（真实基线全绿判定两次亲见）。exit 码映射以四组直接捕获证毕（输出重定向不经管道）：注入 FAIL→exit **1**、gate PASS→exit **0**（宽松基线注入映射证明，验后还原）、report-only（真实基线，带载红）→exit **0**、report-only 经 run_tests→套件 exit **0**。
- **V3 PASS**：备份→基线 compact_ms 1.9294→0.9647（÷2）→`-Gate`→compact_ms **RED**（+34.9%/+79.5% 两跑）→"FAIL (gate on; … beyond tolerance)"→**真实 exit 1**→字节级还原（sha1 一致）→全绿；吞吐类基线减半显改善判 OK（方向语义与施工④-6 一致）。附带实证：采集途中 Unity/WorkBuddy 带载 3 次令 sort/frame 环境性假红——恰证安静护栏必要；本机 WorkBuddy/ZCode 为白名单或阈值边缘常驻，**门禁级采数请在无并行代理/无用户负载的窗口进行**。
- **V4 PASS**：三程序各两跑 BENCH_JSON 行**格式逐字段一致**（键序/%.4f 定点/单行；确定性指标 saved_pct=0.0000/94.8800、batches=100000/5120 两跑逐字节同值）；容差判定纯函数用例真实有行号（Tools/bench_regression.ps1 L142-153 判定式，L209-218 吞吐 -20%/耗时 +30% 恰边界过、越界一毫米红，L220-222 未知 kind 必抛，L224-226 中位，L252-278 OK/RED/MISS/NEW 矩阵与 migrate -35% 覆盖）且 `-SelfTest` **28/28 ok exit 0** 亲跑；run_tests 第 7 段实测：report-only 下 2 红（带载）→"REPORT-ONLY (2 red, not gating)"→bench 行 SKIP→"RESULT: ALL PASS" 套件 exit 0——**默认 report-only 语义端到端成立**；diff 复核第 7 段为纯增段、六段语义零改动（native 段另实测 PASS）。
- **V5 PASS**：文件规范字节级核验——.cpp/.h/.json 全部 UTF-8 无 BOM + LF，.ps1 CRLF（规范例外）；run_tests.ps1 的 BOM 为 HEAD 既有（`git show HEAD` 字节一致，非本任务引入）；施工报告四节齐全（简报/施工报告①-⑤ 含容差依据③与实测数据/验收清单/验收结论）；git status 无验收残留（TestReport.txt、TempLogs/* 均已 gitignore）。

**两条遗留处置建议（对施工⑤）**：

1. **VK 离屏 readback 全零（⑤-1）——"另立任务"建议成立**。验收复测 `--backend vulkan --headless --lights`：本机走**窗口路径**（VK 有意不接 forceHeadless，交互会话建窗成功）→ VULKAN LIGHTS OK exit 0、validation error 0、帧均 10.9ms 符合窗口 vsync 剖面——B-R7-02 已验证路径未被本任务破坏；离屏 VK lights 按交付设计不可经 CLI 到达，"离屏全零"无法在交付形态下复现（与开发期观察+代码注释 zrhi_d3d12_window.cpp ~L8614 一致）。维持建议：另立任务核查 VK 离屏 readback 可见性（复现需先让 VK 路径可达离屏，例如临时接 forceHeadless 的实验分支）。
2. **基线 machine_quiet=SKIPPED（⑤-2）——"安静期重跑 -UpdateBaseline"建议成立且必要**。验收安静窗口实测 compact_ms 1.27-1.33ms vs 基线 1.9294（约 -33%），证实该指标门禁偏松（方向为"松"非"误红"，不阻塞 PASS）；对比时黄字提醒每次如实出现（本验收 6 次运行均见）。请主控闭环后择真正安静窗口（无并行任务、无用户进程）重跑 `Tools/bench_regression.ps1 -UpdateBaseline`，勿加 -SkipQuietCheck。

**验收备注**：① 施工报告①"12 用例 25 断言"应为 **26 断言**（717,652-717,626=26，逐用例点数亦 26），纯笔误；② Unity 五段按任务纪律未跑，六段不破坏以 diff 复核 + native 段两次亲证覆盖；③ 本验收期间检测到并行任务 A-R9-08 的改动与 Unity 运行（仓库内 PipelineManager/Volume* 等），未触碰。
