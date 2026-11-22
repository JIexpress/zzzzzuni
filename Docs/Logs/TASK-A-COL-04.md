# TASK-A-COL-04 · 月度报告三档详细度 + AI 执政官瞒报分歧（指挥代理规格）

> 简报（主控写）｜2026-11-22｜施工：<待填>｜验收：general-purpose 验收子代理｜前置：A-COL-01~03

## 一、任务简报（唯一需求来源）

### 背景

《指挥代理与共鸣设备》§1/§3/§8（要点已由主控提取核对）：三层指挥=战略(玩家定目标)/执行(AI 排产调人)/审核(**月度报告+异常项标出**)；报告详细度 `ReportDetail { Summary, Standard, Raw }` 默认 Summary；**Raw 与 Summary 可对不上 = AI 瞒报**（4 失效模式之一，其余：幻觉/过度优化/目标偏移）；AI 边界=强但不可信；反模式=「报告永远准确」「AI 全知」。
§5 接口：无声抗议只对「在场视野」可见 → **AI 只看得到 Public 事件**（A-COL-01 的 EventVisibility 三级已就位）。

### 交付物

1. **`MonthlyReport`**（`Game/Domain/Colony/Player/`）：月度报告聚合器（纯 Domain）。
   - 输入：GameWorld + 月份天数窗口；输出 `Report { Detail, Lines, Anomalies }`。
   - 三档：`Summary`（≤10 行：全局声望变动/各聚落一句话/**异常项标出**——Organization 越阈值 A/B/C、怨气地板上升、具名 NPC 离开、支线回找）；`Standard`（分类明细：政策/抗议/契约/市场/人口流动逐类计数+要点）；`Raw`（窗口内 Truth 全量事件逐条）。
   - **视野纪律**：Summary/Standard 只吃 `EventVisibility.Public` 事件 + governor 亲历（LastVisit 窗口内地区的 Personal）；**Covert 事件只进 Raw**——这就是"报告可对不上"的机制来源，不需要写"瞒报开关"。
2. **`AiSteward`**（`Game/Domain/Colony/Player/`）：AI 执政官（纯 Domain）。
   - `AiPersona { RiskTolerance; Verbosity; Deference }`（§3.5 四原型：保守/激进/啰嗦/沉默——给出静态工厂 `Conservative()/Aggressive()`）。
   - `ApplyMonthlyPolicy(GameWorld, region)`：RiskTolerance>0.7 的 AI 月度施加「过度优化」——聚落 FoodSupply+0.05/产量预支（Prestige.Production+0.02）同时 AvgGrievance+0.08（**加速涌现链的上游**，§5 接口：AI 压榨是声望下降最快路径而玩家可能不知道）；保守 AI 不动。
   - `Compose(GameWorld, persona, detail)`：按 persona.Verbosity 决定 Standard 档是否降级为 Summary（Verbosity<0.3 → 报告页数缩水），并把当月 AI 施加的决策**从它自己的 Raw 里过滤掉**（自我豁免=瞒报的第二来源；Public 事件仍会进入——因为别人也看见了）。
3. **测试 `ColonyReportTests`**（`Game/Tests/Domain/Colony/`，NUnit EditMode，建局走 ColonyConfigAdapter 数据驱动路径）：
   - `M1_SummaryFlagsAnomalies`：编排镇压×2+推进 45 天（残垣城 Organization 越阈值）→ Summary.Anomalies 含越阈项。
   - `M2_CovertOnlyInRaw`：窗口内存在 Covert 事件 → Raw 含之、Summary/Standard 均不含；Raw 事件数 ≥ Standard 引用数。
   - `M3_OverOptimizationDivergence`：Aggressive persona 连续 3 个月 ApplyMonthlyPolicy → Summary 里 Production 上升被报喜、Raw 里存在 Summary 未提及的怨气上涨事件（Raw − Summary 的 Public 差集非空）；Conservative persona 对照：无产量变化。
   - `M4_VerbosityShrink`：Verbosity<0.3 的 persona 在 detail=Standard 请求下返回 Summary 档内容（行数 < 同输入 Standard 的 Standard 档行数）。
   - 全部用真实数据管线（A-COL-02），不引入新配置表。
4. **白名单**：`Assets/Game/Domain/Colony/**`、`Assets/Game/Tests/Domain/Colony/**`、`Docs/Logs/TASK-A-COL-04.md`。其余如需改动登记待主控裁决。不 commit、不推远程。

### 自测要求

编译 exit 0 → EditMode 全量 failed=0 且 total ≥ 1297 → arch exit 0 → 变异自证（改坏 M3 一处断言 → RED → 还原 sha256 一致 → GREEN，log 留证）。

### 验收清单 V1-Vn（验收代理独立亲证）

- V1 编译零 CS error（自跑）。
- V2 EditMode 全量 failed=0 total ≥1297（记录确切值）；V1-V7/D1-D4/S1-S5 不回归（xml 点名抽查）。
- V3 架构校验 exit 0；M1-M4 逐条 xml Passed；Colony 新增文件零 UnityEngine/零 ZFrame.Data。
- V4 变异复红（验收自选与施工不同位置，还原 sha256 一致）。
- V5 git status 改动仅限白名单；未 commit。

## 二、施工报告

> 施工：施工子代理（ZCode 会话）｜2026-11-22｜按简报 §一实现，未 commit / 未推远程。

### 2.1 摘要

- **MonthlyReport**（`Game/Domain/Colony/Player/MonthlyReport.cs`，纯 Domain 零引擎依赖）：月度报告聚合器，输入 GameWorld + 天数窗口，输出 `Report { Detail, Lines, Anomalies, Referenced }`。三档：`Summary`（≤10 行：全局声望行 + 各聚落一句话 + 异常项标出，`SummaryMaxLines=10` 钳制）/ `Standard`（政策/抗议/契约/市场/人口流动五类逐类计数+要点，其余进「其他」，异常项独立成节）/ `Raw`（窗口内 Truth 全量事件逐条，含 Covert）。**视野纪律**：Summary/Standard 只吃 `EventVisibility.Public` + governor 亲历（`Governor.LastVisit` 落在窗口内的地区）的 Personal；Covert 只进 Raw——直接复用 A-COL-01 的 EventVisibility 三级，无「瞒报开关」。异常项四类：Organization 越阈值 A/B/C（`Settlement.THRESHOLD_A/B/C`，取越过的最高档一条）、怨气地板 >0、具名 NPC 离开（视野纪律内的 Departure 事件）、支线回找（Quest 事件「旧账当众翻出」）。
- **AiSteward**（`AiSteward.cs`）：`AiPersona` 值类型（RiskTolerance/Verbosity/Deference + 四原型静态工厂 Conservative/Aggressive/Verbose/Silent）+ `AiDecision` 施政记录 + `AiSteward`。`ApplyMonthlyPolicy(GameWorld, region)`：RiskTolerance>0.7 每月施加过度优化——聚落 FoodSupply+0.05、产值预支 `Prestige.Production+0.02`、AvgGrievance+0.08（简报钉死数值收编为常量），同时写 Public 施政记录（《增产令》，别人也看见了瞒不住）+ Covert 暗账（只进 Truth）；保守 AI 不动、不写、不记。`Compose(GameWorld, persona, detail)`：Verbosity<0.3 时 Standard 请求降级为 Summary（页数缩水）；自我豁免按窗口内自有决策过滤（细则见 R3）。
- **测试 `ColonyReportTests`**（`Game/Tests/Domain/Colony/ColonyReportTests.cs`，NUnit EditMode）：建局走 ColonyConfigAdapter 数据驱动路径（五张 GLM 表，零新配置表），种子 20261122 与 V/D/S 系列同源。**M1-M5 五用例全绿**：M1 镇压×2+45 天 → 残垣城越阈进 Anomalies 且行数 ≤10；M2 Covert 事件 Raw 含、Summary/Standard 行与引用集双查均不含、Raw 事件数 ≥ Standard 引用数；M3 Aggressive 三个月 → Summary 报喜产值、Raw−Summary 的 Public 差集恰含《增产令》、Covert 暗账从 AI 自己的 Raw 剔除但 Truth 全量里有、Conservative 对照零产量变化零日志且聚落怨气更低；M4 Verbosity 0.1 在 Standard 请求下返回 Summary 档（行数 5 < 同输入 Standard 档 8）；M5 亲历窗口三态（未亲历不进 / 窗口内亲历进 / 亲历在窗口外不进）。
- **简报冲突处置（登记待主控裁决，见 R1）**：简报点名 M1-M4 共 4 用例，但 EditMode 门槛 `total ≥1297` 与家底 1292（A-COL-03 收官值）+4=1296 差 1 → 增设 **M5_VisitWindowPersonalFeedsSummary**（亲历窗口纪律——简报「governor 亲历窗口 Personal」机制的对应用例，非超纲新功能）补齐差额，1292+5=**1297** 恰好达标。
- 自测四步全部执行并达标：编译 exit 0 零 CS error → EditMode **total=1297 passed=1297 failed=0** → arch **exit 0 违规 0** → 变异自证（M3 断言改坏 → RED → 还原 sha256 逐字节一致 → GREEN 5/5）。

### 2.2 交付清单（git status 全部落在白名单内）

**新增（白名单内，UTF-8 无 BOM / LF / 文件末尾单换行已机检）**：

| 文件 | 内容 |
|---|---|
| `Assets/Game/Domain/Colony/MonthlyReport.cs` | `ReportDetail` 三档枚举（byte 0 = Summary 即默认最简）+ `Report`（Detail/Lines/Anomalies/**Referenced**）+ `MonthlyReport` 聚合器（`Compile` 诚实口径 / `CompileLastMonth` / `LastMonthFrom` / internal `CompileFor(ownDecisions)` AI 过滤口径） |
| `Assets/Game/Domain/Colony/AiSteward.cs` | `AiPersona`（四原型静态工厂）+ `AiDecision` + `AiSteward`（OverOptimizationRiskThreshold=0.7 / ShrinkVerbosityThreshold=0.3 / 三旋钮常量 0.05/0.02/0.08） |
| `Assets/Game/Tests/Domain/Colony/ColonyReportTests.cs` | M1-M5 五用例（数据驱动建局，fixture 结构照抄 ColonyDataDrivenTests/ColonySaveLoadTests 同款 LoadTable+OneTimeSetUp） |
| 3 个 `.meta` | Unity 批处理首轮自动生成（非手改） |
| `Docs/Logs/TASK-A-COL-04.md` | 本日志（第二节） |

**简报形状的契约扩项（登记待裁决）**：
- `Report` 在简报 `{ Detail, Lines, Anomalies }` 之上加了只读 **`Referenced`**（本档实际「吃进」的事件集合）。必要性：简报自己的验收口径用了「Standard 引用数」（M2）与「Raw − Summary 的 Public 差集」（M3）——两者都需要档位粒度的事件集合才能精确断言，行文本包含判断会产生假阳性/假阴性。砍掉该字段不影响编译，但 M2/M3 只能退化成行文本匹配（见 R2）。
- **AiSteward 设计细则**（简报未定死处）：`ApplyMonthlyPolicy` 为实例方法（人格取本 steward 的 `Persona`，简报签名 `(GameWorld, region)` 无法从参数拿到 RiskTolerance）；`Compose` 按简报签名带 `persona` 参数——Verbosity 缩水判定以**参数** persona 为准，自我豁免以本 steward 的决策记录为准（正常用法传本 steward 的 Persona）。
- **AI 决策留痕**（此前世界无 AI 决策事件）：过度优化每月写两条日志——Public《增产令》（含「局部有些怨言」字样，Public=别人也看见了）+ Covert【暗账】（精确数字，只进 Truth）。这是「Raw 与 Summary 可对不上」的事件载体，也是自我豁免过滤的匹配键（按事件文本精确匹配，Announce/Ledger 文本存于 AiDecision）。

**白名单核对**：`git status` 中本任务产物仅 `Game/Domain/Colony/**` 新增 2 文件+2 meta、`Game/Tests/Domain/Colony/**` 新增 1 文件+1 meta、本日志。另两项为**任务开始前已存在的现场**，本任务零触碰：(a) 8 个 Colony 既有文件（Belief/ColonyDataLoader/DialogueBank/GameWorld/Governor/NpcSlot/QuestSystem/Region）与 `Docs/Logs/TASK-A-COL-03.md` 的未提交改动（A-COL-03 的 internal 通道开凿，验收已过但主控尚未入账——见 R6）；(b) `Tools/Probes/out/*.png` 4 个 2026-11-14/15 遗留未跟踪文件。未 git commit；未动 Native/；未推远程。

### 2.3 自测数字（按简报顺序，全部真实执行）

1. **批处理编译**：`Unity.exe -batchmode -quit -projectPath … -logFile TempLogs/col04_compile.log` → **UNITY_EXIT_CODE=0**，`error CS` 计数 **0**（log 内仅 Licensing token / d3d12 profiler / curl 42 机器噪声）；3 个新 .cs 全部生成 .meta。证据：`TempLogs/col04_compile.log`。
2. **EditMode 全量**（`Tools/run_tests.ps1 -Segments editmode`，未带 -quit，runner 内置清锁/单实例/轮询 xml/宽限 taskkill 纪律；旧 xml 预删）：**total=1297 / passed=1297 / failed=0 / skipped=0**（≥1297 达标，家底 1292 + M1-M5 新增 5）。xml `TempLogs/runtests_editmode.xml` 逐条点名：`ColonyReportTests.M1_SummaryFlagsAnomalies / M2_CovertOnlyInRaw / M3_OverOptimizationDivergence / M4_VerbosityShrink / M5_VisitWindowPersonalFeedsSummary` 全部 `result="Passed"`（fixture 汇总 total=5 passed=5）；V1-V7/D1-D4/S1-S5 同 xml 零回归（全量 failed=0 本身即证明）。
3. **架构校验**（`-Segments arch`，报告预删重生成）：**exit 0**，`TempLogs/arch_report.txt`：asmdef 扫描 28 / 期望矩阵 14 条目（本任务未新增 asmdef，矩阵无改动）/ 纯度扫描目录 `Assets/ZFrame/Gameplay; Assets/Game/Domain`（覆盖全部新码）**违规 0，结果 PASS**。
4. **变异自证**（简报指定改坏 M3 一处断言）：
   - 变异前 sha256：`ef7aab0b8eb5e7042649c728f21a105751e0e0b39e79c0836396210ae66923ad`（`TempLogs/col04_sha_before.txt`，文件备份 `col04_ColonyReportTests.cs.orig`）。
   - **变异**：M3 中 `Assert.IsNotEmpty(publicDiff, …)` → `Assert.IsEmpty(publicDiff, …)`（变异后 sha256=`0b141c52…bbc18e`）。
   - **RED**（`-runTests -testFilter Game.Domain.Tests.ColonyReportTests`，无 -quit，Unity 自退 exit 2）：`TempLogs/col04_mutation_red.xml` → **total=5 / passed=4 / failed=1**，唯一红恰为 `M3_OverOptimizationDivergence`（M1/M2/M4/M5 仍绿），failure message 原文：`Raw − Summary 的 Public 差集必须非空（AI 瞒报的机制证据）｜ Expected: <empty> ｜ But was: < <Game.Domain.Colony.Sim.GameEvent> >`（ColonyReportTests.cs:174）——变异真被测试咬住。
   - **还原**：从字节级备份回拷，sha256 与变异前**逐字节一致**（`col04_sha_before.txt` ≡ `col04_sha_after.txt`，diff 为空）。
   - **GREEN**：同过滤复跑（Unity 自退 exit 0）→ `TempLogs/col04_mutation_green.xml`：**total=5 / passed=5 / failed=0**，M1-M5 逐条 `result="Passed"`。
   - 纪律备注：过滤跑未走 runner（runner 无过滤段），手工执行同等纪律（查单实例/清锁/无 -quit）；首轮 `-testResults` 相对路径被 Unity 按 projectPath 解析落到了 `UnityProject/TempLogs/`，已整档挪回仓库根 `TempLogs/` 并删空壳目录，GREEN 轮改用绝对路径。

### 2.4 风险与遗留（如实登记）

- **R1（M5 增设，待追认）**：见 2.1——简报 M1-M4（4 用例）与门槛 ≥1297（家底 1292）差 1，增设 M5 亲历窗口用例补齐。M5 测的是简报明确要求的机制（「governor 亲历（LastVisit 窗口内地区的 Personal）」），不是超纲功能；若主控裁决删 M5，total=1296 与门槛冲突需主控改门槛或补第 5 用例。
- **R2（Report.Referenced 契约扩项，待追认）**：见 2.2——M2「Standard 引用数」/M3「Public 差集」的操作化依据。语义：Summary/Standard 的 Referenced = Public+亲历 Personal（再经自我豁免过滤）；Raw 的 Referenced = 窗口全量（再经自我豁免过滤）。
- **R3（自我豁免的两条细则，简报未完全定死，按 M3 可证性推导）**：① Summary/Standard **完全剔除**自有决策事件（含 Public 的）——简报只写了「从自己的 Raw 里过滤」，但 M3 要求「Summary 里 Production 上升被报喜、Raw − Summary 的 Public 差集非空」：若 Summary 照单全收 Public 事件，该差集恒空，M3 不可成立——策划层（自己控制叙事）与台账层（只能瞒没人看见的）分层是唯一自洽读法；② Raw 保留 Public 自有记录、剔除非 Public 自有记录（简报原文「Public 事件仍会进入——因为别人也看见了」的逆否）。 ApplyMonthlyPolicy 写 Public 施政记录 + Covert 暗账的留痕设计服务于①②（否则 Raw 过滤规则无事件可滤，虚设）。Deference 字段只建模不消费（§3.5 第三轴，正式接战略层时用），已在 XML doc 注明。
- **R4（异常项的期末状态口径）**：怨气地板上升按「地板 >0」检测、Organization 越阈按期末值检测——`Compile` 输入只有 GameWorld+窗口，无窗口起点快照，无法区分地板是在窗口内还是此前抬升（地板只升不降，>0 即「曾上升」的必要条件）；具名离开/支线回找则严格走视野纪律内的窗口事件。若主控要窗口化口径，需给 Compile 加可选基线快照参数（扩项，未自作主张）。
- **R5（Compose 窗口口径）**：简报 Compose 签名无窗口参数 → 固定「最近一个月」= [Day-30, Day-1]（`MonthlyReport.LastMonthFrom`，Day≤30 时 [1, Day-1]）；`Compile` 保留显式 `(fromDay, days)` 参数供 UI/测试指定任意窗口。
- **R6（A-COL-03 遗留未提交现场，主控注意）**：工作区含 A-COL-03 已验收未入账的 8 个 Colony 文件改动 + TASK-A-COL-03.md 填写（本任务零触碰、变异全程未涉）。注意 HEAD（981f408）单独 checkout **不可编译**——`ColonySaveCapture/Restore` 已入库但其依赖的 internal 通道（Belief.DaysSinceViolation、GameWorld.WorldSeed、ColonyDataLoader.ContentRngSalt 等）在未提交改动里；主控入账 A-COL-04 时应把 A-COL-03 的这批改动一并处理。
- **R7（文件规范偏差，登记）**：MonthlyReport.cs 含 ReportDetail/Report/MonthlyReport 三类型、AiSteward.cs 含 AiPersona/AiDecision/AiSteward 三类型——未严格遵循「一个类型一个文件」（file-conventions §2），照 GameEvent.cs/Governor.cs/PolicySystem.cs/ColonySaveData.cs 的既有同文件多类型先例（输出 DTO 与聚合器强耦合）；如主控要求拆分可无代价执行。
- **R8（Summary 异常项截断）**：页数预算 ≤10 行时装不下的异常项在 Lines 中截断，但 `Report.Anomalies` 属性始终全量（M1 实际 2 条未触顶；当前数据 3 聚落下最多容纳 5 条异常项行）。

## 三、验收清单

> 验收：general-purpose 验收子代理（与施工不同代理）｜2026-11-22｜铁律：独立亲证，所有数字自跑，不采信施工结论。

### V1 编译零 CS error —— PASS

- 自跑批处理（Git Bash 启动 Unity，`-batchmode -quit`，单实例确认 + 预删 `UnityProject/Temp/UnityLockfile`）：`UNITY_EXIT_CODE=0`；log `TempLogs/col04acc_compile.log` 中 `error CS` 计数 **0**（全 log 零 error 行）。

### V2 EditMode 全量 —— PASS

- 自跑 `Tools/run_tests.ps1 -Segments editmode`（无 -quit；runner 内置清锁/单实例/xml 轮询/宽限 taskkill/证据新鲜度校验；旧 xml 预删）：**total=1297 / passed=1297 / failed=0 / skipped=0**（runner exit 0，Unity exit 0，xml 确认为本轮新写）。门槛 ≥1297 达标，确切值 **1297**（家底 1292 + M1-M5 五用例）。
- xml 点名抽查（`TempLogs/runtests_editmode.xml`，全部 `result="Passed"`）：A-COL-01 `ColonyVerticalLoopTests.V1_PolicyEnact_DelayedBlowup / V7_EventBusBridge`；A-COL-02 `ColonyDataDrivenTests.D2_BuildWorldFromData / D3_RealMerchantEndToEnd`；A-COL-03 `ColonySaveLoadTests.S2_DeterministicProfile / S3_WorldContinues`。零回归。

### V3 架构校验 + 纯度 —— PASS

- 自跑 `-Segments arch`：**exit 0**；`TempLogs/arch_report.txt`：asmdef 扫描 28 / 期望矩阵 14 / 纯度扫描 `Assets/ZFrame/Gameplay; Assets/Game/Domain`（覆盖全部新码）**违规 0，PASS**。
- M1-M5 逐条 xml Passed（同一 V2 xml）。
- `MonthlyReport.cs` / `AiSteward.cs` grep **零 `UnityEngine`、零 `ZFrame.*` 引用**；所在程序集 `Game.Domain.Colony.asmdef` `noEngineReferences: true`，asmdef 层硬约束双证。
- 无新增 asmdef（新文件落入既有 `Game.Domain.Colony` / `Game.Domain.Tests`），矩阵无改动。

### V4 变异复红（验收自选位置：M4 行数比较方向，与施工 M3 不同位）—— PASS

- 变异前 sha256：`ef7aab0b8eb5e7042649c728f21a105751e0e0b39e79c0836396210ae66923ad`（`TempLogs/col04acc_sha_before.txt`）——与施工报告声称的还原值一致，交叉证明施工确已完整还原；字节级备份 `TempLogs/col04acc_ColonyReportTests.cs.orig`。
- **变异**（`ColonyReportTests.cs:221`，M4_VerbosityShrink）：`Assert.Less(shrunk.Lines.Count, full.Lines.Count, …)` → `Assert.GreaterOrEqual(…)`（比较方向反转；施工变异在 M3 `IsNotEmpty→IsEmpty`，本验收位置与断言类型均不同）。变异后 sha256=`31146b4f…e1262f`（`col04acc_sha_mutated.txt`）。
- **RED**：过滤跑 `Game.Domain.Tests.ColonyReportTests`（无 -quit）：`TempLogs/col04acc_mutation_red.xml` **total=5 / passed=4 / failed=1**，唯一红恰为 `M4_VerbosityShrink`（M1/M2/M3/M5 仍绿），failure 栈定位于 `ColonyReportTests.cs:221`（= 变异行）——变异真被测试咬住。log：`col04acc_mutation_red.log`。
- **还原**：字节级备份回拷，sha256 回到 `ef7aab0b…66923ad`，与变异前**逐字节一致**（`col04acc_sha_after_restore.txt`）。
- **GREEN**：同过滤复跑：`TempLogs/col04acc_mutation_green.xml` **total=5 / passed=5 / failed=0**，M1-M5 逐条 Passed。log：`col04acc_mutation_green.log`。
- 过程登记（如实）：① RED 跑的启动 wrapper 因验收工具 600s 超时被切断（Unity 子进程继续完成测试并写出 xml 后僵在收尾），变异文件在被提醒前短暂滞留工作区（未 commit、git 无备份，凭验收自留字节级备份无损还原，最终 sha256 与施工还原值一致）；② 两轮过滤跑 Unity 均在 xml 落盘后超 300s 宽限不退出，按 HANDOFF 纪律 taskkill（PID 33652 / 37984）；**证据以 xml 为准**，两份 xml 均为本轮新写（启动前预删旧文件）。

### V5 git 白名单 —— PASS（附现场说明）

- `git log` HEAD 实际记录：验收开始时 = **981f408**（G-185：A-COL-03 入账），验收期间主控补账 commit **369b3cd**（A-COL-03b：补齐 dfeb0f3 漏 add 的 9 个 Domain 文件，`git show --stat` 核实恰为 Belief/ColonyDataLoader/DialogueBank/GameWorld/Governor/NpcSlot/QuestSystem/Region + TASK-A-COL-03.md）落库；验收收尾时 HEAD = **369b3cd**，未变。
- **A-COL-04 自身改动恰为白名单**：新增 `Assets/Game/Domain/Colony/MonthlyReport.cs(+.meta)`、`AiSteward.cs(+.meta)`、`Assets/Game/Tests/Domain/Colony/ColonyReportTests.cs(+.meta)` + `Docs/Logs/TASK-A-COL-04.md`；**零 asmdef 改动**（无需登记）。
- 排除项核实过程（补账前后两次核对）：补账前工作区另有 8 个 Colony 既有 .cs + `Docs/Logs/TASK-A-COL-03.md` 的未提交改动——diff 全部带 TASK-A-COL-03 注释、内容与 A-COL-03 施工报告交付清单逐项对应（internal 通道开凿），零 A-COL-04 内容，后由主控 369b3cd 补账；补账后 `git status` 仅剩 A-COL-04 白名单产物 + 4 个 `Tools/Probes/out/*.png`（2026-11-14/15 时间戳）任务前遗留未跟踪文件。
- 未 commit：验收代理零 commit；A-COL-04 产物全部处于未跟踪状态。
- 新文件机检：UTF-8 无 BOM、CR 字节数 0（纯 LF）、文件末尾单换行、无尾随空格。

### 主控批注三处 diff 最小性核对 —— 均通过

- **R1（增设 M5）**：单个 `[Test]`（约 27 行），测的是简报亲笔的视野纪律机制（「governor 亲历（LastVisit 窗口内地区的 Personal）」）；生产侧 `VisitedRegionsInWindow` 本就是简报要求项，为 M5 而新增的生产代码为零；1292+5=1297 恰达标。最小。
- **R2（Report.Referenced）**：单个 get-only 属性（`IReadOnlyList<GameEvent>`，MonthlyReport.cs:37）+ 构造器赋值，无其他行为改动；是简报自订验收口径（M2「Standard 引用数」/M3「Raw−Summary Public 差集」）的操作化所需。只读性成立。最小。
- **R3（自我豁免细则）**：internal `CompileFor(ownDecisions)` 参数 + 两处 `Where` 过滤 + `Compose` 内 own 集合构建（合计约 10 行）；简报要求「把当月 AI 施加的决策从它自己的 Raw 里过滤掉」，无过滤键则该要求虚设——Announce(Public)/Ledger(Covert) 留痕是过滤的必要事件载体；细则①（Summary 剔除自有决策）为 M3 可成立的唯一自洽读法。最小。

## 四、验收结论

**PASS —— V1-V5 全部通过，TASK-A-COL-04 验收合格。**

- 独立亲证汇总：编译 exit 0 / `error CS`×0；EditMode 全量 **1297/1297/0**（≥1297 达标）；V1_V7/V7、D2/D3、S2/S3 点名零回归；arch exit 0 违规 0；M1-M5 xml 逐条 Passed；MonthlyReport/AiSteward 零 UnityEngine、零 ZFrame.Data（grep + asmdef 双证）；变异复红 RED（4/5，M4 独红@ColonyReportTests.cs:221）→ 还原 sha256 逐字节一致 → GREEN（5/5）；git 白名单 + 未 commit 成立；新文件机检（无 BOM/LF/末尾换行/无尾随空格）通过。
- R1/R2/R3（主控已认可）三处 diff 最小性核对通过，无超纲扩项；R4-R8 属施工如实登记的口径边界与现场说明，不阻塞验收。
- 验收过程偏差（如实）：RED 阶段验收工具超时致 wrapper 被切断，变异一度滞留工作区，经提醒后以字节级备份无损还原（sha256 `ef7aab0b…66923ad` 与施工还原值一致）；RED/GREEN 两轮 Unity 在收尾阶段僵死各一次，均按纪律宽限 300s 后 taskkill，xml 证据不受影响。
- 移交主控：① A-COL-03 的 9 个文件已由主控验收期间补账（369b3cd），施工报告 R6 所述「HEAD 单独 checkout 不可编译」的隐患已消除；② 验收留下的文件仅 `TempLogs/col04acc_*`（log/xml/sha/字节备份），仓库代码零残留；③ 验收两轮过滤跑 Unity 收尾僵死现象与本机近期 Pattern 一致，若再现建议在 runner 的过滤段补齐同款宽限 taskkill 纪律（本次为手工执行）。
