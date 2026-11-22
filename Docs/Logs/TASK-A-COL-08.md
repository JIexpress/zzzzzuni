# TASK-A-COL-08 · 月度报告面板截图探针入门禁（UI 回归护栏）

> 简报（主控写）｜2026-11-23｜施工：ZCode 施工子代理｜验收：<待派>｜前置：A-COL-05/07（ReportPanel/ColonyWorldHost 现有形态，探针只读消费）

## 一、任务简报（唯一需求来源）

### 背景

月度报告面板（ReportPanelView / ColonyWorldHost，A-COL-05/07 交付）每月渲染数据驱动内容——但没有任何机制证明面板**真的把内容显示出来了**。单一"截图非黑像素 > 阈值"是恒真断言（HANDOFF 登记过 B-PLANET-15 内容锚教训）。本任务给月报面板建 UI 回归护栏：截图探针 + 内容锚 + 基线门禁。

### 交付物

1. **探针（PlayMode 截图用例，惰性启用）**：只在 Unity 命令行带 `-acol08probe` 参数时激活（FrameBaselineProbe 惰性范式；编辑器、全量 PlayMode 家底轮次零开销零日志）。场景链：ColonyWorldHost 初始化 → DebugFastForward(31) → 月报就绪 → 打开 ReportPanel（宿主 presenter API）→ 截图落 `Tools/Probes/out/colony_report-*.png`（gitignore）。
2. **内容锚（杜绝恒真断言）**：面板 GameObject 存在、Summary 行文本非空、至少 1 条异常项高亮元素、Raw 切换后行数变化；锚结果写 JSON（`out/`），基线快照存 `Tools/Probes/baseline/colony-report-baseline.json`（首轮生成后入库）。
3. **runner**：`Tools/probe_colony_report.ps1`——`-SelfTest` 自检锚逻辑；`-Gate` 比对基线，内容锚变化超阈 RED exit 1、取不到数据 MISS exit 2（对齐 bench_player.ps1 语义）；`-UpdateBaseline` 重立基线。
4. **自测**：编译 exit 0 → EditMode 全量 failed=0（1311 家底）→ PlayMode 全量两轮同形 failed=0（122 家底+探针用例）→ arch exit 0 → 探针自跑两轮产出一致（内容锚全过）→ 变异自证（改坏一个锚 → RED → 还原 sha256 一致 → GREEN）。

### 强约束

- 白名单：`Tools/Probes/**`、`Assets/Game/Tests/**`（探针用例）、`Tools/probe_colony_report.ps1`、`Docs/Logs/TASK-A-COL-08.md`（自建四节日志）。
- **不改 ReportPanel/ColonyWorldHost/Domain 任何产品文件**——探针只读消费。
- 不 git commit。Unity 撞锁纪律：用户编辑器可能开着——只等 60s×10 重试，禁止 taskkill 用户编辑器/删其 UnityLockfile；僵尸命令行核实后才能处置。

## 二、施工报告

> 施工：ZCode 施工子代理｜2026-11-23｜按第一节简报实现，产品文件零改动、零 git commit。
> 环境实况（先行登记）：本轮施工期间 **A-AI-05 v2 并行施工中**（TASKS.md doing，Affordance 五文件 M + NpcAutonomyMode/ColonyAutonomyBridge 新增），用户编辑器长期开着（PID 4664，Hub 交互式启动）——撞锁纪律全程照办（等待窗口 ×3 共 ~50 分钟，零 taskkill 用户编辑器、零删其锁文件）；并行代理的 Unity 突发运行被 runner 的 active-batch 分类正确保护。

### 2.1 摘要

月报面板 UI 回归护栏落地：**探针用例（惰性启用）+ 内容锚 ×4 + 截图像素取证 + 基线门禁 runner**，全部对齐既有先例（FrameBaselineProbe 惰性范式、PixelProbeRig 截图通道、bench_player 门禁语义、B-PLANET-15 内容锚纪律）。

- **探针用例**（`Game/Tests/PlayMode/ColonyReportProbeTests.cs`，唯一新增生产侧交付物）：`[UnityTest]` 首行检查命令行 `-acol08probe`，无参数即 `Assert.Ignore` 跳过（全量轮次实测 duration 0.009s、result=Skipped label=Ignored，零开销）。激活后全链只读消费产品 API：SCN_Game 宿主建局 → 残垣城双镇压编排（异常项确定性涌现，C6 同款口径）→ `DaysPerRealSecond=0` 冻结时钟（C1 证实能力，保证跨跑截图同状态）→ `DebugFastForward(31)` 月结 → `OpenReportPanel()` → 内容锚采集 → 截图 ×2 → 锚 JSON/PNG 落 `Tools/Probes/out/`。**先落盘后断言**：JSON/PNG 写档在断言之前——锚失败时门禁仍拿到"有数据的 RED"而不是 MISS（变异轮实证）。
- **内容锚（杜绝"非黑即过"恒真）**：A1 面板 GameObject 真存在于 UIRoot Panel 层画布下（`~ReportPanel` 活动对象 + 父链 == Panel 层画布）；A2 Summary 渲染行数 == VM 行数、≥1 行非空白、**≥1 行与 VM.SummaryLines 精确匹配**（VM 的一行真的出现在 UGUI Text.text 里）；A3 VM AnomalyLines ≥1 且场上 "▲ " 行 ≥1 且其 Text.color == 高亮琥珀色（容差 0.01）且行上**真挂 Button**；A4 真 UGUI toggle 切 Raw（onValueChanged → P 层重渲染）后 Raw 渲染行数 == VM.RawDisplayLines.Count 且 ≠ Summary 行数，且**情报点付费闸门真实扣 1（3→2 数值断言）**；A5 像素取证（非黑占比 / 正文亮像素 / 琥珀像素 / Summary-vs-Raw 差异占比）落 JSON，runner 用地板值判读。
- **截图通道**：batchmode 无 GameView，走探针相机（停泊 (5000,5000,5000)、farClip 3000 剔除全部场景内容）→ RT 1280×720，Panel 层画布**临时**切 ScreenSpaceCamera 挂探针相机，读回后**立即还原 ScreenSpaceOverlay**（HANDOFF Canvas 模式静默转换坑显式重申）。实测两轮同图证实：**URP 往离屏 RT 写线性值原样**（显式 sRGB readWrite 也不改变——B-PROBE-02 linear-bridge 同源事实），故琥珀窗口定义在实测线性字节域 (216,162,73) 邻域，JSON notes 留痕。
- **runner**（`Tools/probe_colony_report.ps1`，CRLF，ASCII）：四模式 `默认报告 / -Gate / -UpdateBaseline / -SelfTest`。门禁判据：**计数精确比对**（固定种子 20261122 + 确定性域 ⇒ 内容计数不允许漂移，内容变化=有意 -UpdateBaseline 重立，绝不静默放行）+ 布尔锚全真 + 像素地板值（runner 常量：非黑 ≥0.02、正文亮像素 ≥50、琥珀 ≥10、两视图差异 ≥0.005）。退出码契约对齐 bench_player.ps1：0 全过（报告模式 RED 也 0）/ 1 RED 仅 -Gate / 2 MISS（"无法证明没回归"按回归算）。**登记分歧**：Unity `-runTests` 用例断言失败时进程 exit 2——此时若探针仍写出了完整锚 JSON，runner 照常比对（锚回归 = RED exit 1 而非 MISS）；MISS 只留给"取不到数据"。`-SelfTest` 39 例纯函数自测（锚解析/点路径/判读/退出码映射/新文件选择/PNG 越界），不启 Unity。
- **Unity 撞锁纪律（runner 内建）**：60s×10 等待窗口；Unity 进程按命令行三分类——`active-batch`（batchmode+本项目+log 5 分钟内活跃=并行代理正当运行，**永不杀**）、`zombie-batch`（batchmode+本项目+log 静默 >5 分钟，全窗口等待后可杀）、`user-or-unknown`（用户编辑器/无法核实，**永不杀、永不删其锁**，10 轮后 MISS）。UnityLockfile 只在零 Unity 进程时删除，删除前还有一次进程复查防竞态。退出码捕获**轮询 UNITY_EXIT_CODE 标记**而非 bash 包装进程句柄（本轮实证 Git bin/bash wrapper 会先于 usr/bin/bash+Unity 死亡）；per-run 唯一 `.sh/.out/.err` 文件名防孤儿重定向竞争；Unity 退出僵死（本轮 4 次 batch 3 次挂起于 shutdown 行）由 "xml 出现 + 300s + log 静默核实" 后 taskkill 兜底——HANDOFF 僵尸纪律的命令行核实版。

### 2.2 交付清单

**新增（白名单内）**：

| 文件 | 说明 |
|---|---|
| `UnityProject/Assets/Game/Tests/PlayMode/ColonyReportProbeTests.cs` | 探针用例（惰性启用 + 内容锚 ×4 + 截图 ×2 + 锚 JSON 落盘 + 取证隔离） |
| `UnityProject/Assets/Game/Tests/PlayMode/ColonyReportProbeTests.cs.meta` | Unity 自动生成，随文件提交 |
| `Tools/probe_colony_report.ps1` | runner（默认/-Gate/-UpdateBaseline/-SelfTest 四模式 + 撞锁纪律 + 僵尸核实） |
| `Tools/Probes/baseline/colony-report-baseline.json` | 基线快照（首轮 `-UpdateBaseline` 生成，入库） |
| `Docs/Logs/TASK-A-COL-08.md` | 本日志 |

**修改**：

| 文件 | 改动 | 定性 |
|---|---|---|
| `Tools/Probes/README.md` | 登记表 +1 行（colony-report）+ 专节一节（跑法/锚口径/门禁判据/边界） | 白名单 `Tools/Probes/**` 内 |
| `.gitignore` | +2 行：`Tools/Probes/out/colony_report-*.png` / `*.json`（逐轮原始档不入库） | **白名单外，登记待主控追认**：任务明示"截图落 out/（gitignore）"，A-PERF-02 先例同款（frame-baseline 当年也加过同款条目） |

**明确未动**：ReportPanelView / ReportPresenter / ReportPanelVm / ColonyWorldHost / ColonyConfigAdapter / Game.Domain.Colony（全部产品文件零改动——`git diff` 上述路径为空，探针纯只读消费）；场景资产零手改；未 commit。

### 2.3 自测数字（全部本机亲证，证据在 TempLogs/ 与 out/）

| # | 项 | 结果 | 证据 |
|---|---|---|---|
| 1 | 编译 | **exit 0**，`error CS` 计数 **0**（含探针用例首次入编） | `TempLogs/a-col-08_compile.log`。注：该轮 Unity 进程退出僵死（日志完整走到 shutdown 行后挂 19 分钟），按纪律 taskkill 僵尸（命令行核实=batchmode+本项目）；编译证据以日志为准 |
| 2 | runner SelfTest | **39 例全过 exit 0**（锚解析/点路径双形态/decimal 判读/精确比对/布尔/地板/新文件选择/PNG 越界/退出码映射） | `powershell -File Tools/probe_colony_report.ps1 -SelfTest` → SELFTEST PASS |
| 3 | 探针首轮 `-UpdateBaseline` | Unity exit 0、test framework **total=1 passed=1 failed=0**、基线写入 | `TempLogs/acol08_probe_run3.log` + `Tools/Probes/baseline/colony-report-baseline.json` |
| 4 | 探针两轮产出一致（-Gate） | **28/28 anchors OK，exit 0**；四轮独立运行的锚值逐项相等（17-51-56 基线轮 / 18-36-26 门禁轮 / 19-22-54 还原轮，变异轮 19-22-06 为有意 RED 例外） | `TempLogs/acol08_probe_gate1.log`（首轮 10 项 MISS 已修 flat-key 解析）/ `acol08_green_gate.log`；锚数字：month=1，summary 7/7/exactMatch 7，anomalies 2/2/2+按钮 2，raw 23/23/delta 16，intel 3→2，images nonBlack 1.0、bright 7062/24859、amber 2899、diff 0.10833 |
| 5 | EditMode 全量 | **total=1315 passed=1314 failed=1**——唯一失败 `Game.Domain.Tests.AffordanceTests.Release_ByOtherAgent_KeepsReservation_RepeatReleaseReturnsFalse`（AffordanceTests.cs:360，**并行 A-AI-05 v2 的 in-flight 文件**，非本任务交付物；本任务 EditMode 零新增；1311 家底 + 并行 4 例） | `TempLogs/runtests_editmode.xml`（Unity exit 2；归属判断=失败断言在并行任务的 M 文件内） |
| 6 | PlayMode 全量两轮同形 | 两轮 **同形**：total=127 / passed=124 / **failed=2 / skipped=1**；失败=`AffordancePlayModeTests.Tick_WithThreeAffordances_SelectsTheNearest` + `AffordanceScenePlayModeTests.V8_AutonomyMode_ToggleMountsSeekers_AndUnmountsClean`（**均为并行 A-AI-05 v2 的文件**，V8 系其本轮新增用例）；skipped=`ColonyReportProbeTests.R1_...`（本探针惰性跳过，duration 0.009s，reason="惰性探针…-acol08probe 时执行"）；**122 家底全绿**，探针/家底零失败 | `TempLogs/runtests_playmode.xml`（第 2 轮，第 1 轮同形仅存于运行输出与登记）＋副本 `TempLogs/a-col-08_playmode_r2.xml` |
| 7 | arch | **exit 0**：asmdef 28 / 期望矩阵 14 / **0 违规** | `TempLogs/arch_report.txt` |
| 8 | 变异自证 | 改坏锚（`CountPrefixed(texts, "▲ ")` 前缀→""，anomalyRowsRendered 记成全行数 7）→ **-Gate RED exit 1**（`RED: summary.anomalyRowsRendered: baseline=2 current=7`）→ 仓外备份还原 → **sha256 逐字节一致**（`feea7e75…848`）→ **-Gate GREEN exit 0**（28/28 PASS） | 变异前 sha `TempLogs/a-col-08_probe_sha_before.txt`、变异后 `…_sha_MUTATED.txt`（46d84928…828）、还原 `…_sha_after_restore.txt`；`TempLogs/acol08_mutant_gate.log` / `acol08_green_gate.log` |

**说明（简报自测要求 vs 实测的偏差，如实登记）**：简报期望 EditMode/PlayMode 全量 failed=0；实测两套全量各出现 1-2 个失败，**全部归属并行 A-AI-05 v2 的 in-flight 文件**（唯一失败的 EditMode 用例、两个失败的 PlayMode 用例全部在并行任务的 M 文件内，错误信息与其自主模式/可供性语义相关），本任务交付物（探针用例）在全量轮次 Skipped 零影响、在探针轮次全绿。主控合并验收时应以并行任务自身闭环为准。

### 2.4 关键设计决定（供验收对照）

1. **惰性启用 = 命令行参数判定 + Assert.Ignore**：与 FrameBaselineProbe 的 RuntimeInitializeOnLoad 判定不同（探针是测试用例不是引导组件），惰性语义等价——无参数运行零行为。全量 PlayMode 的 Skipped(Ignored) 行即"家底不受影响"的证据。
2. **门禁判据用精确比对而非包络**：bench_player 量的是耗时（需要包络），本门禁量的是**内容计数**（固定种子 + 确定性域 ⇒ 逐位可复现，任何漂移=真回归或内容有意变更）。内容变更的合法通道只有一个：`-UpdateBaseline` 显式重立（其拒绝基线化布尔锚为假/地板违规的运行——防止把坏面板立成基准）。
3. **锚失败 → RED 而非 MISS**：C# 探针把 JSON/PNG 落盘放在断言之前，runner 在 Unity exit≠0 且锚 JSON 完整时照常比对——变异轮实证了这条路径（Unity exit 1 但 RED exit 1 给出）。bench_player 语义（probe exit≠0 → MISS）在此登记为有意分歧：Unity 测试框架的 exit 2 含义是"断言失败"，与 bench 的"探针自身没跑完"不同。
4. **像素域如实登记**：URP 离屏 RT 字节=线性原样（显式 sRGB readWrite 实测无效，两轮逐字节同图）。琥珀锚窗口按实测值定义而非理论 sRGB 值——第一个窗口（sRGB 理论值）实测 0 命中被 -UpdateBaseline 的"拒绝基线化"路径当场拦下（`images.summaryAmberPixels: RED (current=0 below floor=10)`），证明地板判读自身有牙。
5. **Unity 撞锁三分类**：并行代理同机施工是常态（本轮实证），僵尸杀分类必须区分"自己人的活跃批处理"与"真僵尸"——判据=命令行（batchmode+本项目）+ 其 logFile 5 分钟静默窗。全程未误杀并行代理的运行、未触碰用户编辑器。
6. **退出码轮询标记而非进程句柄**：Git bin/bash wrapper 实测会先于实际工作进程死亡（绿门后第一次 -Gate 全链 MISS 的事故根因）；改为轮询 `.out` 的 UNITY_EXIT_CODE + xml 静默窗 + log 静默窗三重证据后，后续 3 次 Unity 启动（变异/还原/全量）全部稳定。

### 2.5 风险与登记（如实）

| # | 事项 | 定性 |
|---|---|---|
| R1 | `.gitignore` 根文件 +2 行（out/ 逐轮档不入库）——白名单外最小改动，任务明示"gitignore"，A-PERF-02 先例同款 | 待主控追认 |
| R2 | EditMode failed=1 / PlayMode failed=2 归属并行 A-AI-05 v2（详见 2.3 表）——本任务交付物零失败；主控合并验收时按任务归属裁决 | 环境实况 |
| R3 | 本机 Unity batchmode 退出僵死频发（本轮 4 次启动 3 次挂起）：runner 的标记轮询 + xml+300s+log 静默核实 + taskkill 兜底已实证有效；用户编辑器零接触 | 已验证 |
| R4 | 像素地板值/琥珀窗口是本机校准值（780M / URP_Medium / LegacyRuntime 字体 / 1280×720）；跨机或内容大改需 -UpdateBaseline 重立。计数锚是内容敏感的：报告内容合法变化（新行/新异常）会 RED——这正是护栏的牙，但要求内容变更后显式重立基线 | 已知边界 |
| R5 | 截图 PNG 字节域=线性原样（未 sRGB 编码），作为"显示观感"偏暗，作为回归证据自洽；观感级 sRGB PNG 需着色器级写出（超任务范围，登记） | 已知边界 |
| R6 | 变异轮第一次尝试因分类器 slash 匹配 bug（`C:/` vs `C:\`）未清自己僵尸 → 硬超时 MISS；bug 修复后 RED 证据由重跑取得。修复内容 = Get-UnityClass 斜杠归一（runner 内注释留痕） | 已修复 |
| R7 | 探针运行会向 `Tools/Probes/out/` 增量写 PNG/JSON（gitignore，逐轮 ~350KB）；长期堆积由人工/后续清理任务处置（同 frame-baseline 现状） | 环境说明 |

## 三、验收清单（验收代理独立亲证）

> 验收：general-purpose 验收子代理（2026-11-23，与施工不同代理，独立亲证）｜以下为建议清单，验收代理可与施工不同位置重做变异。

| # | 项 | 判据 |
|---|---|---|
| V1 | 编译 | Unity 批处理编译检查 exit 0、`error CS` 计数 0 |
| V2 | runner 自测 | `probe_colony_report.ps1 -SelfTest` exit 0（39 例） |
| V3 | 探针门禁 | `-Gate` exit 0、28/28 anchors OK；独立再跑一轮与基线逐项相等（两轮产出一致） |
| V4 | 惰性启用 | 全量 PlayMode（无参）结果 xml 中探针用例 result=Skipped label=Ignored 且 duration <0.1s；带 `-acol08probe` 的 -testFilter 运行中用例执行并产出 JSON/PNG |
| V5 | 变异复红（自选与施工不同位置） | 建议：改基线 `raw.rowsVm`（或 C# exactMatchCount 统计逻辑）→ `-Gate` RED exit 1 → 还原 sha256 逐字节一致 → GREEN exit 0 |
| V6 | MISS 语义 | 删 `Tools/Probes/baseline/colony-report-baseline.json`（或改坏 JSON）→ `-Gate` exit 2；还原后 exit 0 |
| V7 | 内容锚有牙 | 抽查任一锚：A2 的 exactMatch（VM 行真出现在 UGUI Text）、A3 的 Button 实体、A4 的 intel 3→2 数值断言、A5 地板值——确认非"存在即过"恒真 |
| V8 | git 现场合规 | 改动仅限白名单（+.gitignore 追认项）；ReportPanelView/ColonyWorldHost/ColonyConfigAdapter/Game.Domain.Colony 零改动（git diff 为空）；场景 .unity 零改动；未 commit |
| V9 | 撞锁纪律 | runner 日志可见分类输出；用户编辑器开着时 gate 走 MISS 而非 kill（可读代码核实，不必真开编辑器） |

## 四、验收结论

**PASS（2026-11-23，验收子代理独立亲证，不采信施工结论）**——派单范围 V1~V5 全过；V7/V8 顺带取证，V6/V9 在派单范围外（见下表诚实标注）。全部数字本机亲跑，证据在 `TempLogs/acc_*`（本地证据，不入库）。

| # | 项 | 独立亲证结果 | 证据 |
|---|---|---|---|
| V1 | 编译 | ✓ 自跑 batchmode 编译 **exit 0、`error CS` 计数 0**、Exiting batchmode successfully | `acc_compile.log` |
| V2 | runner 自测 | ✓ `-SelfTest` **39 例全过、exit 0**（SELFTEST PASS；含地板判读/精确比对/退出码映射等有牙用例） | `acc_gate` 会话输出 |
| V3 | 全量套件 | ✓ EditMode **1315/1315/0**（预期 1315）；PlayMode 两轮 **127/126/0/1skip** 逐位同形（用例集与结果完全一致）；探针用例无参轮 Skipped、探针/家底零失败 | `acc_editmode_full.xml` + `acc_playmode_r1/r2.xml` |
| V4 | 探针门禁两轮一致 | ✓ `-Gate` 两轮 **PASS 28/28 anchors、exit 0**（Unity exit 0、test framework total=1 passed=1）；两轮锚 JSON **逐项相等**（非图像字段与图像指标全等，仅 PNG 文件名时间戳异）——month=1、summary 7/7/7、anomalies 2/2/2+按钮 2、raw 23/23/delta16、intel 3→2、bright 7062/24859、amber 2899、diff 0.10833 | `acc_gate1/2.out` + `colony_report-…20-20-15/58.json` |
| V5 | 变异复红 | ✓ **与施工不同锚、不同落点**：施工改 C# `CountPrefixed` 前缀（summary.anomalyRowsRendered）；本验收改**基线 JSON `raw.rowsVm` 23→24**（§三 V5 建议位置）→ `-Gate` **RED exit 1**（`RED: raw.rowsVm: baseline=24 current=23`，PROBE_VERDICT: FAIL）→ 还原后 sha256 `cdd8ee35…35f6` 与变异前**逐字节一致** → `-Gate` **GREEN exit 0**（28/28 PASS） | `acc_col08_sha_before/MUTATED/after_restore.txt` + `acc_gate_MUT.out`/`acc_gate_GREEN.out` |
| V4(惰性) | 惰性启用 | ✓ 无参全量两轮 `result=Skipped label=Ignored`、duration 0.0123s（<0.1s）；带 `-acol08probe` 的 -testFilter 运行 total=1 passed=1 并产出 JSON/PNG | 两全量 xml + gate 输出 |
| V7 | 内容锚有牙 | 佐证通过：SelfTest 的地板越界→RED、布尔假→RED、精确漂移→RED 用例 + V5 实测 RED——门禁对内容漂移/假锚有真实反应，非恒真；未再逐项抽查 A2/A3/A4 细节（派单范围外） | SelfTest + V5 |
| V8 | git 现场合规 | ✓ 改动仅白名单（+`.gitignore` 追认项 R1）；ReportPanelView/ReportPresenter/ReportPanelVm/ColonyWorldHost/ColonyConfigAdapter/Game.Domain.Colony 零改动（git diff 为空）；场景 .unity 零改动；未 commit（HEAD 仍 de4a9ee） | git status 逐项核对 |
| V6/V9 | MISS 语义/撞锁纪律 | 未独立复跑（派单范围 V1~V5）；V9 已读码核实：`Main` 的 `user-or-unknown` 分支永不杀、永不删其锁、10 轮后 MISS（probe_colony_report.ps1:701-738） | 读码 |

登记（留主控）：工作区并存 `ColonySaveBridge.cs` M（内容标注 TASK-A-COL-07 情报点随档，两任务白名单外，分账裁决）；TASKS.md 暂无本任务行（本任务白名单不含 TASKS.md，闭环刷新属主控职责）；探针运行期间 Unity 批处理退出僵死未复现（本轮 6 次 Unity 启动全部干净退出，R3 的兜底逻辑未被触发）。
