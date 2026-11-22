# TASK-A-COL-03 · Colony 存档/读档（SaveSystem 接入 NpcSlot 确定性实例化）

> 简报（主控写）｜2026-11-22｜施工：<待填>｜验收：<待填>｜前置：A-COL-01（16afa34）、A-COL-02（数据管线）

## 一、任务简报（唯一需求来源）

### 背景

《声望认知与人口》§4.4 的存档方案：**种子固定 → 结果可复现**；存档只需要存"哪些 slot 已实例化"+ 已实例化的内容，读档后同一个 NPC 性格不变。A-COL-01/02 已有 GameWorld（具名/聚落/背景 slot/契约/抗议/声望/支线延迟队列）与数据驱动建局。本任务给世界加上**存档/读档**。

### 设计要点

1. **`ColonySaveData`**（纯 DTO，`Game/Domain/Colony/`）：worldSeed、day、背景层已实例化 slot 列表 + 每个档案字段（name/job/origin/baseLoyalty/grievanceBias/speechTrait）、具名 NPC 全量真实状态（loyalty/grievance/organization/factionLean/maskStrength/knownLoyalty/lastObserved/recognitionScore + 契约 stage/violations/daysInStage/cooperation/intelCarried + protest level/forms/isCovert/discovered + culture 八浮点 + MF 六权重 + image.entries/beliefs）、聚落参数（含 grievanceFloor/leanMean/leanVariance/namedNpcs）、地区（policyExecution/policyBenefit/deeds/rumorPool）、governor（production/tradeValue/militaryDeed/credibility/promises/repressionCount/lastVisit/informants/intel 列表含 boughtDay/validDays/forged）、延迟后果队列（dueDay/kind/where/text/vis）、市场（stock/basePrice/convoys）、关系边（affinity/publicAffinity/type/trust/debt）。
2. **`ColonySaveCapture` / `ColonySaveRestore`**（静态类，纯 Domain）：`Capture(GameWorld) → ColonySaveData`；`Restore(ColonySaveData) → GameWorld`。Restore 后：`EnsureProfile` 用原种子重新生成时必须与存档前逐字段一致（种子确定性兜底，档案字段仅存"已实例化标记"+覆盖值）；关系图按存档边重建；`ColonyEventPump` 游标重置。
3. **SaveSystem 桥**（`Game/Tests/Domain/Colony/` 测试侧，沿用 A-COL-02 的 Adapter 先例——Game.Domain.Colony 保持零 ZFrame.Data 依赖）：把 ColonySaveData 经 ZFrame SaveSystem 的 payload 通道做序列化往返（先读 `ZFrame/Data/` SaveSystem 源码确认 payload/序列化 API；SaveData→byte[]/json→SaveData）。
4. **测试 `ColonySaveLoadTests`**（NUnit EditMode，数据驱动建局走 A-COL-02 的 ColonyConfigAdapter 路径）：
   - `S1_StateEquivalence`：建局推进 45 天（含违背契约/镇压×2/商人抗议编排）→ Capture → Restore → 逐字段断言：具名 NPC 忠诚/怨气/契约阶段/配合度/面具、聚落四参数、地区声望输入、governor 信誉。
   - `S2_DeterministicProfile`：restore 后 `EnsureProfile(slotX)` 与存档前同一 slot 的档案逐字段一致（§4.4「读档后他性格变了」禁止发生）。
   - `S3_WorldContinues`：restore 后 `AdvanceDays(7)` → 事件流非空；组织度/怨气沿既有轨迹继续（残垣城若已过阈值 A，restore 后组织者仍存在且不重复具名化）。
   - `S4_SlotFootprint`：存档里 profiled slot 数 < 背景层总数 10%（玩家一辈子深聊不过 200 人，§4.2 表：无档案 8B/槽）；DTO 序列化后大小 < 1MB（1000 slot fixture 口径）。
   - `S5_SaveSystemRoundTrip`：经 ZFrame SaveSystem payload 通道往返后 Capture 比较等价（字段级）。

### 约束（白名单）

- `Assets/Game/Domain/Colony/**`（新增 DTO/Capture/Restore）
- `Assets/Game/Tests/Domain/Colony/**`（新增测试与 SaveSystem 桥）
- `Docs/Logs/TASK-A-COL-03.md`
- 其余（asmdef/架构校验器）如需改动，登记待主控裁决，勿自行扩大。不 commit、不推远程。

### 自测要求

编译 exit 0 → EditMode 全量 failed=0 且 total ≥ 1287+5 → arch exit 0 → 变异自证（改坏 S2 一处断言 → RED → 还原 sha256 一致 → GREEN，log 留证）。

### 验收清单 V1-Vn（验收代理独立亲证）

- V1 编译零 CS error（自跑）。
- V2 EditMode 全量 failed=0，total 记录确切值 ≥1292；A-COL-01 V1-V7 与 A-COL-02 D1-D4 不回归（xml 点名抽查）。
- V3 架构校验 exit 0；S1-S5 逐条 xml Passed。
- V4 变异复红（验收自选与施工不同位置，还原 sha256 一致）。
- V5 git status 改动仅限白名单。

## 二、施工报告

> 施工：施工子代理（ZCode 会话）｜2026-11-22｜按简报 §一实现，未 commit / 未推远程。

### 2.1 摘要

- **Colony 存档/读档落地**：`ColonySaveData`（纯 DTO）+ `ColonySaveCapture.Capture(GameWorld)` / `ColonySaveRestore.Restore(ColonySaveData)`（纯 Domain，零 ZFrame.Data 依赖）逐字段覆盖简报清单的全部状态；SaveSystem 桥放测试侧（`ColonySaveBridge`：`ColonySaveModule : ISaveable` payload 模块 + `ColonySaveCodec` 手工字段映射），沿用 A-COL-02 的 ColonyConfigAdapter 先例——payload 走 ZFrame SaveSystem 的 `modules.colony-world.world` 节，`SaveData→json→SaveData` 往返由 SaveSystem 原子写/三候选恢复承载。
- **§4.4 确定性实例化**：背景层目录本体不入档——Restore 用原种子**重放建局流**（`seed^ContentRngSalt`，先重放「每卡 MF×6 抽取段」再按建局同式构造 `NpcDirectory`），槽位采样（BaseAttitude/RegionId）与建局逐位一致；已实例化槽再 `RestoreSlot` 回填存档标记位 + 档案覆盖值（覆盖值与种子重生成逐字段一致，S2③ 双向断言兜底）。盐值与抽取数收编为 `ColonyDataLoader.ContentRngSalt / MoralFoundationDraws` 常量，建局与重放共用一份出处。
- **关系图按存档边重建**（不走 `AddFromCard`——那条路会重新掷骰并污染怨气）；具名地区映射抽取为 `GameWorld.DefaultRegionOf`（RegisterNamed 与 Restore 共用一份实现），组织者按聚落 `namedNpcs` 反推注册地；`BeliefContract` 的 30/60 天升级线私有字段经 internal 通道随档，读档后沉默/离开时点不断档；`ColonyEventPump` 游标随「日志从零重建」归零（S3 以新 pump + Consumed==日志条数断言）。
- **测试 S1-S5 全绿**（数据驱动建局走 ColonyConfigAdapter，编排含违背契约/镇压×2/商人抗议/深聊背景层/延迟后果跨存档点）：EditMode 全量 **1292/1292/0**（家底 1287+5），A-COL-01 V1-V7 与 A-COL-02 D1-D4 xml 点名零回归。

### 2.2 交付清单

新增（白名单内）：
| 文件 | 内容 |
|---|---|
| `Assets/Game/Domain/Colony/ColonySaveData.cs` | 纯 DTO：worldSeed/day/背景层已实例化槽+档案字段/具名全量真实状态（真实状态+人格+契约+抗议+文化八浮点+MF 六权重+image.entries/beliefs）/聚落/地区(deeds/rumorPool)/governor/延迟后果队列/市场/关系边 |
| `Assets/Game/Domain/Colony/ColonySaveCapture.cs` | `Capture(GameWorld)`：只读采集，Contract/Image 缺失 fail-fast |
| `Assets/Game/Domain/Colony/ColonySaveRestore.cs` | `Restore(ColonySaveData)`：流重放建背景层 + 覆盖值回填 + 边重建 + 地区映射 + 内容库回填 |
| `Assets/Game/Tests/Domain/Colony/ColonySaveBridge.cs` | `ColonySaveModule(ISaveable)` payload 模块 + `ColonySaveCodec`（DTO↔JToken 手工 1:1 映射，浮点 float→double 精确往返；可空字符串 null 性保留） |
| `Assets/Game/Tests/Domain/Colony/ColonySaveLoadTests.cs` | S1 状态等价 / S2 确定性档案 / S3 世界继续 / S4 槽位足迹 / S5 SaveSystem 往返 |

修改（均为白名单目录内的最小通道开凿，全部带 TASK-A-COL-03 注释）：
`GameWorld.cs`（+WorldSeed 字段；`RegisterSingle` 转 public 供 Restore 复用；地区 switch 抽取为 `DefaultRegionOf`；`_delayed` 队列 internal 读写通道）、`NpcSlot.cs`（`GetSlot/ProfiledSlotIds/TryGetProfile/RestoreSlot`）、`Belief.cs`（`DaysSinceViolation` internal 通道）、`Governor.cs`（`PrestigeSystem.RestoreCredibility`）、`Region.cs`（deeds/rumorPool internal 只读）、`QuestSystem.cs`（延迟回找队列 internal 读写，按下标回填 fail-fast）、`DialogueBank.cs`（`AllLines`）、`ColonyDataLoader.cs`（`ContentRngSalt/MoralFoundationDraws` 常量收编）。

**白名单外改动（登记待主控裁决）**：
1. `Assets/Game/Tests/Domain/Game.Domain.Tests.asmdef`：`precompiledReferences` 增加 `"Newtonsoft.Json.dll"`（对齐 ZFrame.Data.Tests 同款）。**必要性**：payload 通道的 `ISaveable.Save(SaveWriter)` 签名含 `JToken`、codec 基于 JObject——测试侧桥不可能绕开 Newtonsoft；首次编译 84 个 CS0246 全部由此起。Tests 程序集在架构矩阵豁免名单内，arch 校验不受影响（28 asmdef/0 违规）。
2. **DTO 增配三节**（简报字段清单之外）：`Policies` / `Quests`(+`QuestDelayed`) / `Dialogue`。理由：简报钉死 `Restore(ColonySaveData)` 是唯一输入，而政策库/支线库/台词库来自 ConfigService 数据不可由种子重导——不随档走则读档世界支线全空、政策不可颁布、「世界不等你」到期回找丢失（含 QuestDef 运行态 Triggered/Resolved/TriggeredDay，避免读档后触发重放）。如主控裁决删减，砍掉这三节不影响 S1-S5 通过，但读档世界行为残缺需另立任务承接。

### 2.3 自测数字（证据落盘）

| 门 | 结果 | 证据 |
|---|---|---|
| 编译 exit 0 | `UNITY_EXIT_CODE=0`，0 CS error（第一轮 84 CS0246=asmdef 缺 Newtonsoft，已修） | `TempLogs/col03_compile3.log` |
| EditMode 全量 | **total=1292 passed=1292 failed=0**（≥1292 达标；S1-S5 xml 全部 `result="Passed"`；V1-V7/D1-D4 点名零回归） | `TempLogs/runtests_editmode.xml` |
| 架构校验 | exit 0：asmdef 28 扫描 / 期望矩阵 14 条目 / **违规 0**（纯度扫描含 Assets/Game/Domain 新文件） | `TempLogs/arch_report.txt` |
| 变异自证 | S2 断言改坏（`GrievanceBias +0.5f`）→ EditMode **1292/1291/1**，失败点恰为 `S2_DeterministicProfile`（GrievanceBias 计数信息）→ 还原后 **sha256 逐字节一致**（`e825873b52eebdaa…bc910` 前后相同）→ 复跑 **1292/1292/0** GREEN | `TempLogs/col03_mutation_red.log`、`TempLogs/col03_restore_green.log` |

S4 实测口径：生产存档（10 万背景层 + 106 具名 + 500 边 + 五内容库）序列化 ≈0.4MB < 1MB；1000-slot fixture 深聊 80 人（<10%）同样 < 1MB——体积不随背景层总数增长（目录本体不入档，§4.2 表）。

### 2.4 风险与边界（如实登记）

- **世界随机流不入档（简报清单如此，未自作主张扩）**：Restore 后 `world.Rng` 从种子位重新起算，读档后随机事件与「未读档的假想续跑」不逐位一致；确定性契约按简报口径只钉在 NpcSlot 实例化上（S2 断言）。若要全流逐位续跑需把 Rng 内部态入档，属简报扩项，留主控裁决。
- **未入档的边角**（简报清单未列）：`_migrantUntil`（在途移民集合，只影响"在途 N 人"一行日志）、Teaching/Hint 状态、`RelationshipGraph._fakeIntimateCount` 静态样本（Restore 不走 AddFromCard 故不受影响）、EventLog（设计如此：读档日志从零，pump 游标归零）。
- **组织者重复涌现为引擎既有行为**：`Settlement.TrySpawnOrganizer` 在组织度 > 阈值 A 且 `namedNpcs.Count<8` 时每周结都会以同一 id 再涌现一名组织者（A-COL-01 原行为，V5 用 FirstOrDefault 掩盖）。S3 的「不重复具名化」按**身份集合**断言（distinct id 不增、NamedById 唯一），未改动引擎行为——若主控判定该行为是 bug，应另立任务修 `TrySpawnOrganizer` 的去重条件，勿在本任务扩权。
- **S2 的流重放公式与建局顺序耦合**：`卡数×6 抽取` 重放依赖「建局流上 MF 段在目录构造之前」这一顺序（loader 与 V-fixture/S4 fixture 均满足）；若未来建局路径改变抽取模式，S2③ 的对照断言会红——这是设计内的护栏，不是隐患。
- **组织者档案字段的 null 性**：组织者（非卡片出身）的 Want/Secret/Speech 等为 null，codec 以显式 JSON null 往返保留；卡片 NPC 字段全部非空。
- **验收注意**：S1/S5 的字段级等价用自写 `FirstDiff`（路径+值定位）；关系边为集合语义，比较前按 (min,max) 归一排序（重建图的 AllEdges 枚举序是字典迭代序，合法换序不视为失配），其余字段全部按列表序严格比对。

## 三、验收清单

> 验收：general-purpose 验收子代理（与施工不同代理）｜2026-11-22｜**独立亲证**：以下全部数字/证据为验收自跑自采，未采信施工报告结论。环境纪律照交接档案 §3（清锁/查单实例/测试不带 -quit/xml 轮询）。

| # | 项 | 结果 | 证据（验收自跑，TempLogs/） |
|---|---|---|---|
| V1 | 编译零 CS error | **PASS**：`-batchmode -quit` 自跑批处理，`UNITY_EXIT_CODE=0`，log 内 `error CS` 计数 **0**（log 含真实编译活动：domain reloads=1、CompileScripts 1407ms，非空跑） | `col03_acc_compile.log` |
| V2 | EditMode 全量 | **PASS**：`-runTests`（**不带 -quit**）自跑全量，xml：**total=1292 passed=1292 failed=0 skipped=0**（≥1292 达标，记录确切值 1292）。点名抽查同一 xml：A-COL-01 `V1_PolicyEnact_DelayedBlowup`/`V5_Repression_SpawnsOrganizer` 均 Passed（V2/V3/V4/V6/V7 亦逐条核过全 Passed）；A-COL-02 `D1_LoadAllFiveTables`/`D2_BuildWorldFromData`/`D3_RealMerchantEndToEnd`/`D4_DialogueSpeakFromTable` 全 Passed——**零回归** | `col03_acc_editmode.xml`（fresh：本 run 写出） |
| V3 | 架构校验 + Domain 纯度 | **PASS**：自跑 `-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine`，exit 0；fresh 报告（15:01:56，施工旧报告已挪 `col03_acc_arch_report_prev_construction.txt` 防陈旧）：asmdef 扫描 28 / 期望矩阵 14 条目 / 纯度扫描（Assets/ZFrame/Gameplay; Assets/Game/Domain）**违规 0 / PASS**。抽查 `Game/Domain/Colony/` 全部 24 文件：`using UnityEngine` **零命中**；`ZFrame.Data` 字样仅 3 处**注释**（陈述零依赖，无任何代码引用）。S1-S5 逐条 xml Passed 见 V2 行 | `col03_acc_arch.log`、`arch_report.txt` |
| V4 | 变异复红（位置与施工不同） | **PASS**：施工变异在 S2 GrievanceBias，验收自选 **S3 推进天数**：`ColonySaveLoadTests.cs` S3 内 `restored.AdvanceDays(7)` → `(0)`（变异后 sha256=2bdbcca2…，确认已改）。RED：过滤跑 S3 → **total=1 failed=1**，失败点恰为 S3_WorldContinues 断言「restore 后 AdvanceDays(7) 事件流非空（世界没有停摆）/ Expected: not empty / But was: empty」（ColonySaveLoadTests.cs:353）——变异真被测试咬住。还原后 **sha256 前后逐字节一致 `e825873b52eebdaa0e25aedab622b749b5ff6c2b141120dbee3517c0e96bc910`**。GREEN：同过滤复跑 → **total=1 passed=1 failed=0** | RED `col03_acc_mutation_red.xml`；还原 `col03_acc_mutation_sha_before.txt`/`col03_acc_mutation_sha_after.txt`；GREEN `col03_acc_restore_green.xml` |
| V5 | 白名单外改动复核 + 未 commit | **PASS**（详见下） | 本节 git 亲查 |

V5 明细（对照主控追认的两处白名单外改动，逐一亲证）：

1. **`Game.Domain.Tests.asmdef`**：git diff 仅一处——`precompiledReferences` 数组增加 `"Newtonsoft.Json.dll"` 一行（nunit.framework.dll 后追加），无其他键改动；与 `ZFrame/Tests/Data/ZFrame.Data.Tests.asmdef` 的同款数组逐项一致（nunit + Newtonsoft），**与追认口径完全相符**。
2. **`ColonySaveData.cs` 三节**：该文件为白名单内新增（untracked，无 git diff 可言）；核其全文结构——简报点名的各节（背景层/具名全量状态/聚落/地区/governor/延迟队列/市场/关系边）逐一在位且字段齐全未改写，`Policies`/`Quests`(+`QuestDelayedState`)/`Dialogue` 三节位于文件**尾部 232-271 行独立成组**，带「简报清单之外，登记待裁决」注释与 XML doc 说明——**纯追加、不改写既有节**，与追认口径相符。
3. **白名单符合性全查**：`git status --porcelain` 改动=任务日志（白名单）+ `Game/Domain/Colony/` 下 8 个既有文件的最小通道开凿（白名单目录 `Assets/Game/Domain/Colony/**` 之内，逐行核过均为带 TASK-A-COL-03 注释的 internal 只读/回填通道，无行为改写）+ 白名单目录内新增 5 .cs 及其 .meta；白名单外仅上述 asmdef 一处（第 2 处 DTO 三节在白名单目录内、属简报字段清单外扩）。另有 `Tools/Probes/out/*.png` 4 个未跟踪文件为 2026-11-14/15 前任务遗留证据（早于本任务），与 A-COL-03 无关、未动。
4. **未 commit 亲证**：HEAD 仍为 `500f985`（G-184）；`git log --all -- <5 个新文件>` 输出为空——**施工与验收全程零 commit**。

## 四、验收结论

**PASS（V1-V5 全过，A-COL-03 验收通过）**。

- **数字（验收独立实测）**：编译 exit 0 且 0 CS error；EditMode 全量 **1292/1292/0**（家底 1287 + S1-S5 新增 5，与简报 ≥1292 相符）；架构校验 exit 0（28 asmdef / 14 矩阵条目 / 0 违规）；变异复红 RED(1 failed，失败点=变异断言)→sha256 逐字节还原→GREEN。
- **抽查无回归**：A-COL-01 V1-V7、A-COL-02 D1-D4 在验收自跑的全量 xml 中逐条 Passed。
- **纪律核验**：Game.Domain.Colony 零 `using UnityEngine`、零 ZFrame.Data 代码引用；两处白名单外改动与主控追认完全一致且无第三处；未 commit、未推远程。
- **验收侧零代码改动**：唯一变异（S3 推进天数 7→0）已逐字节还原（sha256 复验一致），工作区恢复至施工完成态。
- 证据均在 `TempLogs/col03_acc_*`（compile/editmode xml/arch/mutation red/sha before-after/restore green），验收后留存备查。
