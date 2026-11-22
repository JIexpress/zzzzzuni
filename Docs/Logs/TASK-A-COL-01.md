# TASK-A-COL-01 · ColonyCore 模拟核心迁入 Game.Domain.Colony + 「政策→抗议→报告」垂直闭环

> 简报（主控写）｜2026-11-22｜施工：general-purpose 施工子代理｜验收：general-purpose 验收子代理（独立亲证+双变异复红）｜commit 16afa34

## 一、任务简报（唯一需求来源）

### 背景

用户已在新仓库（`C:\Users\1\WorkBuddy\杂项\ColonyCore\` + `NpcSystem\`，net8 控制台，29 项自测全 PASS）完成太空殖民地游戏的模拟核心：NPC 信念契约/无声抗议/聚落涌现/地区声望/认知五档/关系网三通道/文化系数/政策系统/支线延迟回找/市场状态表/台词语气。设计规格 14 份在 `C:\Users\1\WorkBuddy\杂项\设计\`。

本任务把它迁入本仓库 Game 垂直切片的 Domain 层（保持零引擎依赖），并用 EditMode 测试跑通最小垂直闭环：**颁布政策 → 文化加权怨气 → 商人涨价抗议 → 玩家观察取得间接证据 → 延迟后果到账 → 事件经 ZFrame EventBus 发布**。这是游戏本体（殖民地管理）在 ZFrame 上的第一个真实接线。

### 交付物

1. **`UnityProject/Assets/Game/Domain/Colony/`**（全部新建）：
   - `Game.Domain.Colony.asmdef`：`references: ["ZFrame.Core"]`，`noEngineReferences: true`，`autoReferenced: true`，`rootNamespace: "Game.Domain.Colony"`。
   - 迁入并适配以下源文件（只迁逻辑，逐文件适配规则见下）：
     - 自 `C:\Users\1\WorkBuddy\杂项\NpcSystem\Core\`：`Rng.cs`、`NpcSlot.cs`、`Belief.cs`、`NamedNpc.cs`、`Region.cs`、`SilentProtest.cs`、`Settlement.cs`、`CultureFrame.cs`、`PlayerImage.cs`、`RelationshipGraph.cs`
     - 自 `C:\Users\1\WorkBuddy\杂项\ColonyCore\`：`Sim\GameEvent.cs`、`Sim\GameWorld.cs`、`Sim\HardConstraints.cs`、`Player\Governor.cs`、`Policy\PolicySystem.cs`、`Quest\QuestSystem.cs`、`Dialogue\DialogueBank.cs`、`Economy\MarketState.cs`
   - **不迁**：`NpcSystem\Data\SeedLoader.cs`（GLM 硬编码路径）、`NpcSystem\Program.cs`、`ColonyCore\Data\*`（md/JSON 解析）、`ColonyCore\App\Program.cs`（控制台宿主）。测试一律用内存种子数据。
   - **新增** `ColonyEventBridge.cs`：把 `GameWorld.Log` 的新事件推到 ZFrame EventBus（读 `ZFrame/Core/EventBus/` 源码 + `ZFrame/Tests/EventBusTests.cs` 确认 `Publish/Subscribe` 签名后照抄用法）。事件载荷类 `ColonyEventDto { uint Day; string Kind; string Where; string Text; byte Visibility; }`。桥接做成 GameWorld 的可选扩展方法或独立泵类（每 tick 后调用 `Pump.GameWorld`），**不改动 GameWorld 现有公开行为**。

2. **适配规则（C# 9 / asmdef 环境差异）**：
   - Unity 2022.3 = C# 9：`namespace X;` 文件级命名空间 → 改块状 `namespace X { ... }`；其余特性（switch 表达式、元组模式、`new()`、range `[^n]`/`[..n]`、`Math.Clamp`、`HashSet` 值元组）均在 C# 9 / .NET Standard 2.1 内，可用。
   - **无 ImplicitUsings**：每文件补齐显式 `using System;` / `System.Collections.Generic;` / `System.Linq;` 等。
   - 命名空间统一：`NpcSystem` → `Game.Domain.Colony`；`ColonyCore.Sim` → `Game.Domain.Colony.Sim`；`ColonyCore.Policy` → `Game.Domain.Colony.Policy`；`ColonyCore.Player` → `Game.Domain.Colony.Player`；`ColonyCore.Quest` → `Game.Domain.Colony.Quest`；`ColonyCore.Dialogue` → `Game.Domain.Colony.Dialogue`；`ColonyCore.Economy` → `Game.Domain.Colony.Economy`。
   - **Rng 必须保持 sealed class**（源码注释说明了原因：struct 按值传参会冻结随机序列）。
   - 删除一切 `Console` 使用（Domain 层禁止控制台输出）；`DialogueBank`/`GameWorld` 等源文件确认无 Console。
   - `DataLoader.DefaultJsonPath` 类引用不存在了——`NpcDirectory` 里若引用了被删类型需一并清理（`NpcDirectory.Origins` 被 `Settlement.TrySpawnOrganizer` 使用，注意保留）。
   - 文件规范：UTF-8 无 BOM、LF。

3. **`UnityProject/Assets/Game/Tests/Domain/Colony/ColonyVerticalLoopTests.cs`**（新建，加入既有 `Game.Domain.Tests.asmdef`——先读该 asmdef 的 references 并**追加** `"Game.Domain.Colony"`）：
   - 内存 fixture（不读任何外部文件）：3 地点+聚落、8 具名 NPC（1 船坞装配工带明确 Wont/Secret、1 走私贩 IsMerchant、其余任意）、2 政策（1 挖坑 IsTrap、1 暗药 IsDarkMedicine）、1 支线（坟山北坡式：GrievanceAbove 0.35 + DelayDays 180 + 三选项）、`NpcDirectory(1000, seed, regions, rng)`。
   - 用例（可执行断言，非烟雾测试）：
     - `V1_PolicyEnact_DelayedBlowup`：颁布挖坑政策(execution 0.4) → 地区 PolicyVerdict 含「说得好听」；`AdvanceDays(31)` → 延迟爆雷事件已入 EventLog。
     - `V2_MerchantProtest_PriceAndPretext`：商人 Grievance≥0.6 → `ProtestRouter.ChooseProtest` 含 InflatePrice；`ProtestEffects.Price` multiplier ∈ (1.0, 1.6]，假理由 =「最近供应紧张」。
     - `V3_Observation_YieldsIndirectEvidence`：`Governor.Observe` 循环≤6 次 → `DiscoveredByPlayer == true` 且产生 Personal 事件。
     - `V4_BeliefContract_SilenceThenLeave`：违背 Wont → Doubt；推进 35 天 → Silent 且 Cooperation<0.9；再推进 30 天 → Left 且 `IntelCarried == Secret`（阶段三不可逆：再 OnViolation 仍 Left）。
     - `V5_Repression_SpawnsOrganizer`：`RepressionCount=3` → 14 天后平均 MaskStrength 上升；聚落两次 `Apply(Harsh)` → Organization>0.35 → 周结后存在 `Role=="Organizer"` 的具名 NPC 且 `GrievanceFloor>0`。
     - `V6_InformationAsymmetry`：`PlayerFeed(1, ...)` 数量 < `All` 数量，且 Covert 事件不进 feed。
     - `V7_EventBusBridge`：桥接后 Publish/Subscribe 收到 ≥2 条 ColonyEventDto（至少 1 条 Policy、1 条 Protest），Day/Text 字段与 EventLog 一致。
   - 测试风格照抄 `Game/Tests/Domain/` 既有用例（fixture 结构、NUnit 写法、命名规范）。

### 约束（白名单，越界=FAIL）

- `UnityProject/Assets/Game/Domain/Colony/**`（新建）
- `UnityProject/Assets/Game/Tests/Domain/Colony/**`（新建）
- `UnityProject/Assets/Game/Tests/Domain/Game.Domain.Tests.asmdef`（仅追加 references 一项）
- `Docs/Logs/TASK-A-COL-01.md`（本日志）
- 不 git commit；不改 ZFrame/Game 其他任何文件；不动 Native/；不推远程。

### 自测要求（施工代理必跑）

1. 首轮编译+生成 .meta：`"C:/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe" -batchmode -quit -projectPath "C:/Users/1/Desktop/zcode/GameFramework/UnityProject" -logFile -` → log 无 CS 错误。
2. EditMode 全量（**勿带 -quit**，轮询 results xml 出现即算完成，挂 5 分钟再 taskkill）：结果 `failed=0`，总数 **≥1283**（1276 家底 + 7 新增）。
3. 架构校验器 exit 0（从 HANDOFF/NOTES 找命令；找不到则在日志登记所用方法与结果）。
4. 变异自证：临时改坏 V2 一个断言（如把 1.6 改 1.0）→ 该用例 RED → 还原 → 全绿。两份 log 片段留进施工报告。

### 验收清单 V1-Vn（验收代理独立亲证，可证伪、带阈值）

- V1：Unity 批处理编译零 CS error（log 证据）。
- V2：EditMode 全量 failed=0 且总数 ≥1283（results xml 证据；记录通过数）。
- V3：架构校验 0 违规；`Game.Domain.Colony` 程序集零 UnityEngine 类型引用（抽查 asmdef + 任一文件无 `using UnityEngine`）。
- V4：V1-V7 七用例逐条 PASS（xml 里逐条点名）。
- V5：变异复红证据成立（施工报告两份 log + 验收自做一次不同位置的变异）。
- V6：文件规范抽查：UTF-8 无 BOM、LF、命名空间/asmdef 符合简报。
- V7：`git status` 显示改动仅限白名单。

## 二、施工报告

> 施工：general-purpose 施工子代理｜2026-11-22

### 2.1 摘要

18 个源文件按适配规则逐条迁入 `UnityProject/Assets/Game/Domain/Colony/`（块状命名空间、显式 using、`NpcSystem`→`Game.Domain.Colony`、`ColonyCore.*`→`Game.Domain.Colony.*`、Rng 保持 sealed class、全部源文件核实零 Console/DataLoader 残留），新增 `ColonyEventDto` + `ColonyEventPump` 事件桥（只读 `world.Log.All` 游标前进，Fire=ZFrame 的 Publish，不改 GameWorld 任何公开行为），新建 `ColonyVerticalLoopTests`（V1-V7 七用例全真实断言，内存种子 fixture：3 地点+聚落、8 具名 NPC、2 政策、坟山北坡式支线、`NpcDirectory(1000, seed, regions, rng)`，种子 20261122 固定→全链确定性）。自测四步全部执行：编译 exit 0 零 CS 错误、EditMode total=1283（1276 家底+7 新增，符合 ≥1283）、变异复红→还原（sha256 逐字节一致）→复跑同形。**V1-V7 七用例在两轮全量 EditMode 中均 PASS。**

**简报冲突处置（已请示用户，用户裁决「严格守白名单」）**：新增 `Game.Domain.Colony.asmdef` 必然触发架构校验器「不在期望矩阵中」违规 + `ZFrame.Debug.Tests.ArchitectureValidatorTests` 两例红（矩阵条目数动态校验）。白名单禁止改 `ZFrame/Editor/ArchitectureValidator.cs`，故两处红灯**按用户裁决原样保留并登记为待主控修补**（修补只需一条矩阵条目，见 2.4 风险 R1）。除此之外的其余 EditMode 用例与纯度扫描零违规零失败。

### 2.2 交付清单（git status 全部落在白名单内）

**新建 `UnityProject/Assets/Game/Domain/Colony/`（21 文件，UTF-8 无 BOM / LF，.meta 由 Unity 批处理生成）**：

| # | 文件 | 来源→适配 |
|---|---|---|
| 1 | `Rng.cs` | NpcSystem.Core；**保持 sealed class**；补 `System.Collections.Generic`（IReadOnlyList） |
| 2 | `NpcSlot.cs` | NpcSystem.Core（含 NpcProfile/NpcDirectory）；保留 `NpcDirectory.Origins`（Settlement.TrySpawnOrganizer 依赖）；无 DataLoader 残留（已核实） |
| 3 | `Belief.cs` | NpcSystem.Core（Stage/LeaveStyle/BeliefContract） |
| 4 | `NamedNpc.cs` | NpcSystem.Core（Recognition/NamedNpc/RecognitionRules） |
| 5 | `Region.cs` | NpcSystem.Core（DeedType/LocalDeed/RegionState） |
| 6 | `SilentProtest.cs` | NpcSystem.Core（ProtestForm/SilentProtest/ProtestRouter/ProtestEffects） |
| 7 | `Settlement.cs` | NpcSystem.Core（Decision/Settlement） |
| 8 | `CultureFrame.cs` | NpcSystem.Core（CulturalFrame/OffenseEvent） |
| 9 | `PlayerImage.cs` | NpcSystem.Core |
| 10 | `RelationshipGraph.cs` | NpcSystem.Core（RelationType/RelationshipEdge/RelationshipGraph） |
| 11 | `GameEvent.cs` | ColonyCore/Sim（EventVisibility/EventKind/GameEvent/EventLog） |
| 12 | `GameWorld.cs` | ColonyCore/Sim；跨命名空间 using 全部改 `Game.Domain.Colony.*` |
| 13 | `HardConstraints.cs` | ColonyCore/Sim（HintState/HintLevel/TeachingSystem/RadiationBudget） |
| 14 | `Governor.cs` | ColonyCore/Player（PrestigeSystem/Governor/IntelReport） |
| 15 | `PolicySystem.cs` | ColonyCore/Policy（PolicyDef/PolicySystem） |
| 16 | `QuestSystem.cs` | ColonyCore/Quest（TriggerKind/QuestDef/QuestSystem） |
| 17 | `DialogueBank.cs` | ColonyCore/Dialogue |
| 18 | `MarketState.cs` | ColonyCore/Economy（MarketNode/MarketState） |
| 19 | `ColonyEventDto.cs` | 新增：`{ uint Day; string Kind; string Where; string Text; byte Visibility; }`（简报原文逐字段） |
| 20 | `ColonyEventBridge.cs` | 新增：`ColonyEventPump.Pump(GameWorld)`——每 tick 后调用，读 `world.Log.All` 游标切片，逐条 `EventBus.Fire`（简报所称 Publish 在 ZFrame 的实际 API 名为 **Fire**，已按 EventBus.cs + EventBusTests.cs 源码确认照抄用法）；`Reset()` 供重放 |
| 21 | `Game.Domain.Colony.asmdef` | `references:["ZFrame.Core"]`、`noEngineReferences:true`、`autoReferenced:true`、`rootNamespace:"Game.Domain.Colony"` |

**新建 `UnityProject/Assets/Game/Tests/Domain/Colony/ColonyVerticalLoopTests.cs`**：V1-V7 七用例，fixture 纯内存（不读外部文件），编排对照原 `ColonyCore/App/Program.cs` 第 1~15 天剧本（该宿主按简报不迁入，其职责由测试 fixture 承担）。

**修改 `UnityProject/Assets/Game/Tests/Domain/Game.Domain.Tests.asmdef`**：references 仅追加 `"Game.Domain.Colony"` 一项（其余零改动）。

**白名单核对**：`git status` 改动仅 `Game/Domain/Colony/**`、`Game/Tests/Domain/Colony/**`、`Game.Domain.Tests.asmdef`、本日志（另有 4 个 `Tools/Probes/out/*.png` 为任务开始前已存在的未跟踪文件，非本次产物）。未 git commit；未动 Native/；未推远程。

### 2.3 自测数字（按简报顺序，全部真实执行）

1. **批处理编译+meta**：`Unity.exe -batchmode -quit -projectPath … -logFile TempLogs/col01_compile.log` → **exit 0，error CS 计数 0**，Colony 目录 21/21 文件全部生成 .meta（含 `Game/Domain/Colony.meta`、`Game/Tests/Domain/Colony.meta` 两个文件夹 meta）。证据：`TempLogs/col01_compile.log`。
2. **EditMode 全量**（`Tools/run_tests.ps1 -Segments editmode`，未带 -quit，轮询 xml，xml 出现即判完成）：**total=1283 / passed=1281 / failed=2 / skipped=0**（results xml `TempLogs/runtests_editmode.xml`，Unity exit=2 由上述两例红导致）。**failed=2 逐条点名：`ZFrame.Debug.Tests.ArchitectureValidatorTests.ExpectedMatrix_MatchesActualAsmdefCount`、`…ValidateAll_CurrentProject_HasZeroViolations`——即 2.1 所述登记缺口，与 Colony 代码无关**。简报阈值「总数 ≥1283」达成（1276+7）；「failed=0」被 2.1 裁决冲突阻断，除这两例登记红外全绿。**V1-V7 七用例逐条 PASS**（xml 可点名）。
3. **架构校验器**：命令取自 `Tools/run_tests.ps1` arch 段（`-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine`，报告写 `TempLogs/arch_report.txt`）。结果：**exit 1，违规数 1**——`[asmdef] Game\Domain\Colony\Game.Domain.Colony.asmdef: 程序集 'Game.Domain.Colony' 不在期望矩阵中`；**纯度扫描（Assets/Game/Domain 含 Colony 全部新码）0 违规**——Colony 程序集零 UnityEngine 类型/引擎 API 引用（`noEngineReferences:true` + 纯度黑名单双证）。exit 0 同样被 2.1 裁决冲突阻断，属同一缺口。
4. **变异自证**（简报指定：把 V2 的 1.6 改 1.0）：
   - 变异前 sha256：`b466ad391bf6…c6286fd98`（`TempLogs/col01_sha_before.txt`）。
   - **RED**（`-runTests -testFilter Game.Domain.Tests.ColonyVerticalLoopTests`，无 -quit，轮询 xml 出现后按僵死纪律 taskkill）：xml `TempLogs/col01_mutation.xml` → total=7 / **failed=1**，log 片段①：`Failed Game.Domain.Tests.ColonyVerticalLoopTests.V2_MerchantProtest_PriceAndPretext ｜ 涨幅钳制在 1.6 以内（涨一倍玩家立刻识破） Expected: less than or equal to 1.0f But was: 1.22000003f`（其余 6 例仍绿）。
   - **还原**：sha256 与变异前**逐字节一致**（`TempLogs/col01_sha_after.txt` diff 为空）。
   - **全绿**：还原后全量 EditMode 复跑（`TempLogs/col01_editmode_run2.log`）→ total=1283 / passed=1281 / failed=2，failed 仍仅为两例登记矩阵用例；log 片段②：`Passed Game.Domain.Tests.ColonyVerticalLoopTests.V2_MerchantProtest_PriceAndPretext`（七用例逐条 Passed，与首轮同形）。

### 2.4 风险与遗留

- **R1（登记缺口，需主控动手，施工白名单禁改）**：`ZFrame/Editor/ArchitectureValidator.cs` 期望矩阵缺 `Game.Domain.Colony` 条目 → arch 段 exit 1 + EditMode 两例红。修补照 X-ARCH-01 先例只加一条（AllowedReferences 以 asmdef 实际为准）：
  `["Game.Domain.Colony"] = new AsmdefRule { AllowedReferences = new[] { "ZFrame.Core" } },`
  补上后 arch exit 0、EditMode 回到 failed=0 total=1283，无需改任何其他文件（`ExpectedMatrix_MatchesActualAsmdefCount` 为动态统计，自动满足）。**用户 2026-11-22 裁决：本任务严格守白名单，此项留待主控。**
- **R2（fixture 标定，如实登记）**：V4 的「35 天后 Cooperation<0.9」按源码衰减规则只有沉默期（第 31 天起）才生效，1.0 起始 35 天只能到 0.96。fixture 给装配工契约 `Cooperation=0.90`（关系已有裂痕的具名 NPC，简报对具名 NPC 契约初值未约束）→ 35 天后 0.86，断言保持可证伪（衰减机制失灵则 0.90 不过 <0.9）。
- **R3（编排层职责，如实登记）**：迁入代码没有任何路径写 `EventKind.Protest` 事件（原 demo 宿主只 Console 打印涨价）。垂直闭环的「涨价成为可发布事件」由测试编排层按宿主职责写入 `world.Log`（Kind=Protest/Public，内容来自 `ProtestRouter`+`ProtestEffects.Price` 的真实输出），引擎代码零改动；V7 断言 DTO 与 EventLog 逐字一致。
- **R4**：V3/V7 观察发现概率 0.45×6 次，依赖固定种子 20261122 的确定性随机流——本轮两轮全量 + 一次过滤跑全部 PASS（V3 0.002s），种子改动需复验。
- **R5**：`RegionState._nextId` 与 `RelationshipGraph._fakeIntimateCount` 为迁入源码的静态状态，跨用例累积（源码原样保留）；不影响任何断言，但正式接线后若复用同一进程需注意。
- **R6（命名口径）**：简报行文用「Publish/Subscribe」，ZFrame EventBus 实际 API 为 `Fire/FireQueued/Subscribe/FlushQueued`（照源码确认），桥用 `Fire` 立即派发；如主控要队列语义可改 `FireQueued`+每帧 `FlushQueued`，接口不变。

### 2.5 修补后复跑（主控批注 R1 批准项执行，2026-11-22）

- **改动**：`UnityProject/Assets/ZFrame/Editor/ArchitectureValidator.cs` **+7 行**（git diff --stat：1 file changed, 7 insertions(+)，仅此一处）——期望矩阵在相邻 `["Game.Domain"]` 条目后新增 `["Game.Domain.Colony"] = new AsmdefRule { AllowedReferences = new[] { "ZFrame.Core" } }`，照 X-ARCH-01 先例与相邻条目风格，附依据注释。除该行外零其他文件改动；未 git commit。
- **arch 段复跑**（`powershell -NoProfile -ExecutionPolicy Bypass -File Tools/run_tests.ps1 -Segments arch`）：**PASS，exit 0，违规 0**（asmdef 扫描 28，期望矩阵条目 14，Tests 程序集豁免；纯度扫描仍 0 违规）。证据：`TempLogs/runtests_arch.log`、`TempLogs/arch_report.txt`（`结果: PASS`）。
- **EditMode 全量复跑**（同 runner `-Segments editmode`，未带 -quit，轮询 xml）：**PASS，total=1283 / passed=1283 / failed=0 / skipped=0**（results xml `TempLogs/runtests_editmode.xml`，runner log `TempLogs/col01_editmode_run3.log`）。原两例红 `ZFrame.Debug.Tests.ArchitectureValidatorTests.ExpectedMatrix_MatchesActualAsmdefCount` / `…ValidateAll_CurrentProject_HasZeroViolations` 全部转绿；**V1-V7 七用例逐条 PASS**（xml 可点名）。
- **简报门槛清算**：原 2.3 中被 R1 阻断的三项全部达成——EditMode `failed=0` ✓、总数 `1283 ≥ 1283` ✓、架构校验器 `exit 0` ✓。加上 2.3 已达成的编译 0 CS 错误、.meta 生成、变异复红/还原证据，简报自测四项与其阈值**全部满足**。
- **白名单核对（含批准追加项）**：`git status` 仅 `Game.Domain.Tests.asmdef`（M）、`ZFrame/Editor/ArchitectureValidator.cs`（M，+7 行=批准项）、`Game/Domain/Colony/**` 与 `Game/Tests/Domain/Colony/**`（新建）、本日志；`Tools/Probes/out/*.png` 4 个为任务前已存在的未跟踪文件。

### 主控批注（主控 2026-11-22）

- **R1 裁决**：批准白名单追加一项——允许修改 `ZFrame/Editor/ArchitectureValidator.cs` 的期望矩阵，**仅限新增一条** Game.Domain.Colony 条目（照 X-ARCH-01 先例与相邻条目风格）：`["Game.Domain.Colony"] = AllowedReferences:["ZFrame.Core"]`。
- **R2 认可**：V4 fixture `Cooperation=0.90` 起始，简报阈值以实际断言为准，不再返工。
- **R3 认可**：Protest 事件由测试编排层写入，引擎零改动，不再返工。
- 本批注为追加，不涂改上述施工报告原文；修补执行与复跑证据见 2.5。

## 三、验收清单

> 验收：general-purpose 验收子代理（与施工不同代理）｜2026-11-22｜铁律：独立亲证，全部数字为本验收代理自跑，不引用施工报告数字。

### V1 编译零 CS error —— PASS

- 自跑：`Unity.exe -batchmode -quit -projectPath UnityProject -logFile TempLogs/accept_v1_compile.log`（纯编译检查，-quit 允许）→ **UNITY_EXIT_CODE=0**。
- `grep -c "error CS" accept_v1_compile.log` → **0**（log 内仅有 Licensing token / curl 42 等机器噪声，无 CS 编译错误）。
- `Game/Domain/Colony/` 下 20 个 .cs + 1 个 asmdef 全部生成 .meta（含两个文件夹 meta），共 21 个。

### V2 EditMode 全量 —— PASS

- 自跑：`powershell -NoProfile -ExecutionPolicy Bypass -File Tools/run_tests.ps1 -Segments editmode`（**未带 -quit**；runner 内置纪律：单实例检查、删 UnityLockfile、轮询 results xml、xml 出现 5 分钟未退 taskkill；旧 xml 预删，仅本轮重写文件作证据）→ **runner exit 0**。
- results xml `TempLogs/runtests_editmode.xml`（本轮 start-time 2026-11-22 03:49:58Z）：**total=1283 / passed=1283 / failed=0 / skipped=0，result="Passed"**。
- 阈值核对：1283 ≥ 1283（1276 家底 + 7 新增）✓，failed=0 ✓。

### V3 架构校验 + 零引擎依赖 —— PASS

- 动态：自跑 `-Segments arch`（`-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine`）→ **exit 0**；`TempLogs/arch_report.txt`（@11:52:43，本轮生成）：asmdef 扫描 28、期望矩阵条目 14（含新增 `Game.Domain.Colony`）、纯度扫描目录含 `Assets/Game/Domain`（覆盖 Colony 全部新码）、**违规数 0、结果 PASS**。
- 静态：`Game/Domain/Colony/*.cs` 全 20 文件 `using UnityEngine` 命中 0、`UnityEngine` 任意 token 命中 0、`Console.` 命中 0；`Rng` 为 `public sealed class`（Rng.cs:12）。
- asmdef 实测：`name/rootNamespace = Game.Domain.Colony`、`references:["ZFrame.Core"]`、`noEngineReferences:true`、`autoReferenced:true`、`allowUnsafeCode:false`——与简报逐字段一致。

### V4 七用例逐条点名 —— PASS

results xml（V2 同一轮）逐条 `result="Passed"`：

| 用例 | 结果 | 耗时 |
|---|---|---|
| Game.Domain.Tests.ColonyVerticalLoopTests.V1_PolicyEnact_DelayedBlowup | Passed | 0.064s |
| …V2_MerchantProtest_PriceAndPretext | Passed | 0.006s |
| …V3_Observation_YieldsIndirectEvidence | Passed | 0.004s |
| …V4_BeliefContract_SilenceThenLeave | Passed | 0.004s |
| …V5_Repression_SpawnsOrganizer | Passed | 0.003s |
| …V6_InformationAsymmetry | Passed | 0.003s |
| …V7_EventBusBridge | Passed | 0.005s |

### V5 变异复红（验收独立做，位置与施工不同）—— PASS

- 施工变异位置：V2 的 1.6 钳制（简报示例）。**本验收自选不同位置：V4 断言 `IntelCarried == Secret` 改恒假**（ColonyVerticalLoopTests.cs 第 311 行，`eng.Secret` → `eng.Secret + "##MUT##"`，IntelCarried 为 string，恒不等）。
- 变异前 sha256（PowerShell Get-FileHash，字节级）：`b466ad391bf649245b62f99e41309a8e032a787e22b128b75152407c6286fd98`。
- **RED**：自跑 `-runTests -testPlatform EditMode -testFilter Game.Domain.Tests.ColonyVerticalLoopTests`（无 -quit；纪律脚本：单实例/删锁/轮询 xml/300s 宽限 taskkill；Unity 自行退出 exit 2）→ `TempLogs/accept_mut_red.xml`：**total=7 / passed=6 / failed=1**，唯一红 `V4_BeliefContract_SilenceThenLeave`，其余 6 例仍绿。log 片段①（xml failure message 原文）：
  ```
  离开的人必须带走他的 Secret（MVP 5）
  Expected string length 20 but was 13. Strings differ at index 13.
  Expected: "他偷偷改装了净水泵的计量阀##MUT##"
  But was:  "他偷偷改装了净水泵的计量阀"
  ```
- **还原**：sha256 = `b466ad39…c6286fd98`，与变异前逐字节一致 ✓。
- **GREEN**：复跑同过滤（Unity xml 落盘后挂起 >300s，按 HANDOFF 纪律 `taskkill //PID 14972 //F`，证据以 xml 为准）→ `TempLogs/accept_mut_green.xml`：**total=7 / passed=7 / failed=0**，七用例全 Passed。log 片段②：`<test-run … total="7" passed="7" failed="0" … result="Passed">`，`V4_BeliefContract_SilenceThenLeave … result="Passed"`。

### V6 文件规范抽查 —— PASS

- 字节级检查（PowerShell ReadAllBytes，权威；MSYS2 grep 的 CR 计数经查为其文本模式假象）：22 个新文本文件（20 .cs + 1 asmdef + 1 测试 .cs）**全部 BOM=False、CRbytes=0（纯 LF）、endsWithLF=True**。
- 深抽 7 个：`Rng.cs`、`GameWorld.cs`、`ColonyEventBridge.cs`、`PolicySystem.cs`、`ColonyVerticalLoopTests.cs`、`Belief.cs`、`NpcSlot.cs`——同上全部合规，且命名空间落位正确（`Game.Domain.Colony` 及 .Sim/.Policy/.Player/.Quest/.Dialogue/.Economy 与简报映射一致）。
- `ColonyEventDto` 字段与简报原文逐字段一致：`{ uint Day; string Kind; string Where; string Text; byte Visibility; }`；`ColonyEventPump.Pump(GameWorld)` 用 `EventBus.Fire`（已对照 `ZFrame/Core/EventBus/EventBus.cs` 签名：`Subscribe<T> → IDisposable`、`Fire<T>(in T)`，用法正确），只读 `world.Log.All` 游标前进，不改 GameWorld 公开行为。

### V7 git status 白名单 —— PASS

- 验收开始与结束两次 `git status --porcelain` 完全一致，改动仅限：
  - `M Game/Tests/Domain/Game.Domain.Tests.asmdef`（diff 仅追加 `"Game.Domain.Colony"` 一项）✓ 白名单
  - `M ZFrame/Editor/ArchitectureValidator.cs`（diff --stat: 7 insertions，仅新增 `["Game.Domain.Colony"] = AllowedReferences:["ZFrame.Core"]` 一条矩阵条目 = 主控 2026-11-22 批准追加项）✓ 批准范围
  - `?? Game/Domain/Colony(.meta)+`、`?? Game/Tests/Domain/Colony(.meta)+`、`?? Docs/Logs/TASK-A-COL-01.md` ✓ 白名单
  - `?? Tools/Probes/out/*.png` ×4（文件名时间戳 2026-11-14/15，早于任务日 2026-11-22，任务开始前已存在的未跟踪文件，非本任务产物）
- HEAD = `33d30fd`（G-182），无 TASK-A-COL-01 commit → **未 commit ✓**；未动 Native/；未推远程。

## 四、验收结论

**PASS**（2026-11-22，验收子代理独立亲证，V1-V7 七项全过，零阻断遗留）。

- 简报自测四项阈值全部达成：编译 0 CS error、EditMode failed=0 且 total=1283≥1283、架构校验 exit 0 违规 0、变异复红证据成立（施工一份 + 本验收独立一份且位置不同）。
- 白名单纪律（含主控批准的 ArchitectureValidator.cs 一条矩阵条目）核对无越界；未 commit。

遗留项（不阻断，登记备查）：

1. `Tools/Probes/out/*.png` 4 个未跟踪文件为任务开始前遗留，建议主控闭环时一并处置（入 gitignore 或删除）。
2. V5 验收 GREEN 轮 Unity 在 xml 落盘后挂起，按纪律 taskkill 收尸——证据以 xml 为准（7/7 全绿），系本机已知 Unity 退出问题，非 Colony 代码问题。
3. 沿用施工报告 R4 登记：V3/V7 观察发现概率依赖固定种子 20261122 的确定性随机流，种子改动需复验。
