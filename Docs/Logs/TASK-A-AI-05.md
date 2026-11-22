# TASK-A-AI-05（v2）—— 自主行为场景可见化：Colony 自主行为接场景 + 预约态 + G 键/HUD

> **v2 合流改写（2026-11-22）**：评估简报 `Docs/Planning/A-AI-05合流评估-2026-11-22.md` → 主控裁决采纳
> 选项 B（重新定义）。原 v1 目标（G 键挂 AffordanceSeeker + 预约态）保留为子集，新增合流主目标——
> 把 A-COL-06 已落地的 Colony Autonomy（模拟层自主行为）做成 SCN_Game 场景可见。v1 原文完整归档于
> 文末 §八，一字未涂改。

## 一、目标（v2）

1. **合流主目标——自主行为场景可见化**：用户需求#1「NPC 不是按预设跑，而是自己找事」已由 A-COL-06
   在模拟层交付（`ColonyActionScorer`/`ColonyTaskBoard`/`ColonyDailyBehavior`，`GameWorld.DailyTick`
   挂点），但 SCN_Game 可见性只有 Tab 报告面板——看得到"选了什么"，看不到"在做什么"。本刀把它做成
   场景可见：NPC 在场景里的自主移动/行为可视化呈现。
   **实现层走 Game.View**：arch 矩阵允许 Game.View 同时引用 Game.Domain 与 Game.Domain.Colony
   （`ArchitectureValidator.cs:100-107`）；`ColonyWorldHost`（sceneLoaded 挂载 + Tab 热键，
   `ColonyWorldHost.cs:52`）是现成先例。**Colony Domain 五文件零改动**（`AutonomyEnabled`/
   `AutonomousActionsToday`/`AutonomousChoicesToday` 已存在，View 只读）。
2. **G 键统一呈现两层自主（原 v1 目标保留）**：SCN_Game 按 **G** 开/关——
   - 场景层：给 N 个闲置 NPC（默认 3 个，优先 `npc-anim-*`）挂 `AffordanceSeeker` 并 `StartSeeking()`
     （自主移动的可见部分，原 v1 机制原样）；关：全部 `Stop()` 并移除 seeker（NPC 回原驱动）；
   - Colony 层：把 `GameWorld.AutonomousChoicesToday`/`AutonomousActionsToday` 投影为场景可见形式
     （行为标签/事件浮字/HUD——**施工前探查后定，施工报告写明选择与理由**；最小形态=闲置 NPC 行为
     标记，进阶的"具名 NPC 场景角色化"若规模失控则登记为后续任务，本刀只做最小可见化）；
   - **默认关**（像素用例零影响，与 SurvivalLoop/BuildMode 同一纪律）。
3. **预约态（原 v1 目标保留，核心验收）**：把「选中即 Claim」改成：
   - 选中时 `Reserve(agentId)`（**不占容量**，只防止别人再选同一个）；
   - 到达（≤ 半径）才 `Claim()` 转正式占用；
   - 放弃/停止/超时 → `Release()` 同时清预约；
   - 容量满/被预约 → `FindBest` 跳过它。
   预约态只影响 seeker 之间的占座公平，不波及 Colony——独立成 V 子项，可单独砍。
4. **HUD**：显示「自主模式：开/关」+ 当前 seeker 数 + `AutonomousActionsToday`（IMGUI 或 UGUI
   纯视通道，施工时定，参照 ReportPanelView 先例）。
5. **不回归**：默认关时场景 NPC 位置逐位不变、Colony 模拟行为零变化；既有测试全绿。

## 二、现状（2026-11-22 合流评估复核，证据见评估简报）

- **缺口一（预约态）**：`AffordanceRegistry` 仍只有 `Claim/Release/FindBest`
  （`AffordanceRegistry.cs:99-131`），无 `Reserve/ReservedBy`（grep 全 Assets 零命中）；「选中即占座」
  仍在——`AffordanceAction.Execute` 于选中时即 Claim（`AffordanceAction.cs:21-22、204-214`），
  `AffordanceSeeker` 到达后重复 Claim（`AffordanceSeeker.cs:259/348`）。
- **缺口二（G 键/NpcAutonomyMode/HUD）**：全 Assets 无 `KeyCode.G`、无 `NpcAutonomyMode.cs`
  （grep 零命中）。
- **缺口三（场景可见）**：Colony 自主行为只写状态/事件，无空间输出；SCN_Game 可见性仅 Tab 报告面板
  （`ColonyWorldHost.cs:52`）。
- **已有资产（复用不重写）**：`AffordanceSeeker`（`Game/View/AI/AffordanceSeeker.cs`，A-AI-04：
  brain+移动+Claim+10s 超时，每拍先 Release 再 Tick 防自占用抖动）；`SceneAffordances` 3 锚点
  （`SceneAffordances.cs:138-140`：ENV_Barrel/ENV_Tree_01/NPC）；Colony 侧 `AutonomyEnabled`
  （`GameWorld.cs:33`，默认 true）与两个计数器（`GameWorld.cs:35/38`）。
- **架构硬约束**：`Game.Domain.Colony` 只许引 ZFrame.Core 且 `noEngineReferences:true`
  （asmdef + `ArchitectureValidator.cs:86-89`）——Colony 侧不得引用 Game.Domain.AI 或引擎；合流桥
  只能建在 Game.View。
- **勘误记录（主控 2026-11-22 认领）**：v1 派单曾述"Enemy 三文件含 G 键自主实现"有误——
  `EnemyDriveMode` 是构造期战斗双脑选择（`EnemyDriveMode.cs:8-22`），`EnemyBehaviorTree` 无自主
  语义。Enemy 三文件与本法无交集，**不碰**。
- **施工前必复核**：v1 简报（2026-11-09）之后 SCN_Game 仅经 A-R9-19（色彩重定标）与 ColonyWorldHost
  挂载（零场景资产改动），但开工时仍需复验锚点与 `npc-anim-*` 名单仍否成立（评估简报标注【推测：
  复验成本低】）。

## 三、设计要点（v2）

- Game.View 新增跨域桥组件（`ColonyWorldHost` 同款 sceneLoaded 门控，SCN_Game only）：
  - G 键翻转「自主模式」：挂/卸 seeker（原 v1 机制）+ Colony 行为投影开关（同键同状态，两层一致呈现）；
  - Colony 投影从 `AutonomousChoicesToday` 只读（每日清零，`GameWorld.cs:126-127`），不得写回模拟状态；
  - HUD 汇总两层状态（模式/seeker 数/今日自主行为数）。
- 预约态：`Affordance` 加 `ReservedBy`（string agentId，空 = 无）；`Reserve` 幂等；`IsAvailable`
  语义扩展为「未满员 **且** 未被他人预约」；**自己**的预约不算占位（同级判定看 agentId）。
  不破坏 A-AI-03 的 13 条既有断言：无预约场景下 `IsAvailable` 行为必须与之前逐位一致。
- `NpcAutonomyMode`（`Game/View/AI/`）：`Toggle()` / `IsEnabled` / `ActiveSeekerCount`；
  默认不挂任何 seeker；关闭时 seeker `Destroy` 并复原 NPC 状态（复原代价大则只 `Stop()`+停用组件
  并登记为受限）。
- G 键与 Tab（报告）/DebugFastForward 热键排冲突，施工时核对输入路由。
- 不碰：`UtilityBrain`、`NpcUtilityView` 的既有逻辑、`SurvivalLoop`、`BuildMode`。

## 四、白名单（v2）

- `UnityProject/Assets/Game/View/AI/AffordanceSeeker.cs`、`NpcAutonomyMode.cs`（新增）、`SceneAffordances.cs`（如需）
- `UnityProject/Assets/Game/View/Colony/**`（跨域桥组件 + 热键路由）
- `UnityProject/Assets/Game/Domain/AI/Affordance/Affordance.cs`、`AffordanceRegistry.cs`（加预约）
- `UnityProject/Assets/Game/Tests/Domain/AffordanceTests.cs`（补预约用例）
- `UnityProject/Assets/Game/Tests/PlayMode/AffordanceScenePlayModeTests.cs`（补自主模式/合流用例）
- `Docs/Logs/TASK-A-AI-05.md`、`TASKS.md`（仅本任务行）

**不碰**：Colony Domain（`Autonomy/` 五文件、`GameWorld`）、Enemy 三文件、所有 asmdef
（Game.View 已被矩阵允许两域引用，无需改）、`SurvivalLoop`、`BuildMode`。

## 五、验收清单（v2，V 编号沿用 + 扩充）

| # | 断言 | 判据 |
|---|---|---|
| V1 | 预约语义 | A 预约后 B 的 `FindBest` 跳过该条；A 自己仍能选中；Release 后恢复可被选 |
| V2 | 到达才占 | 选中后到达前 `Occupants==0`；到达后 `==1` |
| V3 | 放弃清预约 | 超时 / `Stop()` 后预约与占用都清零 |
| V4 | G 键自主模式 | 开启后 `ActiveSeekerCount==3` 且 NPC 开始移动、Colony 投影可见；关闭后 seeker 归零、NPC 不再移动、投影无残留 |
| V5 | 默认零影响 | 未开启时场景 NPC 位置逐位不变；Colony 模拟行为开关前后零变化；`*PixelEvidence*` 全绿 |
| V6 | 旧断言不破 | A-AI-03 的 13 条 + A-AI-04 的 8 条 + Colony T1-T5 全绿 |
| V7 | 变异复红 | 去掉「预约排除」判断 → V1 必红；恢复后全绿 |
| V8 | 边界干净 | Colony Domain 零改动（git status 无 `Colony/**` 条目）；arch exit 0 |

## 五·六、施工报告（v2）（2026-11-23，施工代理 ZCode）

> 派单 TASK-A-AI-05（v2）按 §一~§五 执行；白名单逐项遵守（Colony Domain / Enemy / asmdef /
> SurvivalLoop / BuildMode 零改动，见 git status 核对）。本节四节齐全：摘要 / 交付清单 / 自测数字 /
> 风险与登记。

### 一、摘要

v2 三件套全部落地，实现层全部在 Game.View（arch 矩阵既有许可），Colony Domain 零触碰：

1. **预约态（Domain）**：`Affordance` 增 `ReservedBy`（null=无）+ `IsAvailableFor(agentId)`
   （自己的预约不算占位）+ `IsAvailable` 扩展为「未满员 **且** 未被任何人预约」——无预约场景与
   A-AI-03 逐位一致；`AffordanceRegistry` 增 `Reserve`（幂等），`Release` 改**双清**（占用+预约），
   `FindBest` 增 agentId 参数（3 参签名委托 null，匿名视角被任何预约挡）——**预约排除判断即 V7
   变异靶点**；`AffordanceAction.Execute` 选中改 `Reserve`（不占容量），到达才由 seeker `Claim`
   转正式占用；`AffordanceSeeker` 的「每拍先 Release」扩展为 stake 级（占用+预约），放弃/超时/
   Stop/销毁全走双清。
2. **自主模式（View/AI）**：`NpcAutonomyMode`（新增）——`Toggle()/SetEnabled()/IsEnabled/
   ActiveSeekerCount/IdleCandidateCount`；sceneLoaded 门控 SCN_Game（SceneAffordances 同款）；
   默认关（零 seeker、零可见物）；开启=给闲置 NPC 挂 `AffordanceSeeker` 并 StartSeeking（上限
   3 个）；关闭=seeker Stop+Destroy、NPC 位置逐位复位、被借走的 `NpcUtilityView` 启用态复位
   ——**无残留**。候选解析是场景实况探测（非写死名单，见风险②）。
3. **G 键 + Colony 投影 + HUD（View/Colony 跨域桥）**：`ColonyAutonomyBridge`（新增）——G 采样
   （Update，旧 Input，与 SurvivalLoop 同范式）翻转自主模式，seeker 与投影**同键同状态**（事件+
   每拍轮询双保险）；IMGUI HUD 四行（模式/seeker 数/Colony 今日自主行为数/行为分布），只在开启时
   产出像素；Colony 数据**只读** `AutonomousChoicesToday`/`AutonomousActionsToday`（每日清零语义
   不受影响，绝不写回——V9 用例钉死）。

### 二、交付清单（白名单逐项）

| 文件 | 变更 |
|---|---|
| `Game/Domain/AI/Affordance/Affordance.cs` | +`ReservedBy`/`IsAvailableFor`/`TrySetReservation`/`ClearReservation`；`IsAvailable` 扩展（未满员且未被任何人预约）；`TryAddOccupant` 改走 `IsAvailableFor`（预约人可转正） |
| `Game/Domain/AI/Affordance/AffordanceRegistry.cs` | +`Reserve`；`Release` 双清（`RemoveOccupant \|\| ClearReservation`，返回任一发生即 true）；`FindBest` 增 4 参 agentId 重载（3 参委托 null；**V7 变异靶点=`!candidate.IsAvailableFor(agentId)`**） |
| `Game/Domain/AI/Affordance/AffordanceAction.cs` | `Execute` 改 `Reserve`（选中即预约）；`Score`/`Execute` 走 agent-aware `FindBest(_agentId)`；`LastClaimed` 名字保留（护既有断言锚）语义文档更新；`ReleaseClaim` 双清 |
| `Game/View/AI/AffordanceSeeker.cs` | `ReleaseOwnedExcept`→`ReleaseStakesExcept`（占用+预约，`HoldsStakeOn` 判定）；到达 `Arrive()` Claim 失败时连预约一起清；驻留态 Claim 失败防御处理；头注更新预约态契约 |
| `Game/View/AI/NpcAutonomyMode.cs` | **新增**：宿主 `~NpcAutonomyMode`（sceneLoaded 门控 SCN_Game，DontDestroyOnLoad，场景重载自愈重挂）；候选解析①NpcUtilityView 携带者（`npc-anim-` 前缀优先，名字序）②不足补社交锚点；挂载捕获位置+停用原驱动、卸载复位；演示口径 `DeviceBroken=true` 起手（见风险④） |
| `Game/View/Colony/ColonyAutonomyBridge.cs` | **新增**：G 键路由 + 投影可见性同步 + IMGUI HUD；`BuildDistributionText`（纯聚合）+`BuildHudText`（测试断言口径）+`LabelOf`（五行为中文） |
| `Game/Tests/Domain/AffordanceTests.cs` | +4 预约用例（R1 预约语义[含动作层打分断言]/R2 幂等与可用性/R3 到达才占+双清/R4 他人 Release 不动预约）；T13 两处断言随新契约更新（风险①） |
| `Game/Tests/PlayMode/AffordanceScenePlayModeTests.cs` | +V7 场景级预约（A/B 双 seeker：B 不得选被约目标）、+V8 自主模式挂/卸（含复位与无残留）、+V9 投影同步+只读（HUD 文本断言）、+`ColonyProjection_DistributionText` 纯函数；`CleanupAutonomy` 取证隔离 |
| `Docs/Logs/TASK-A-AI-05.md`、`TASKS.md` | 本报告 + 本任务行状态 |

### 三、自测数字（最终，锁窗后全链实测；锁窗前的独立编译与登记记录见 §五·六·五·背景）

- **独立编译验证（Unity 自带 Roslyn `csc.dll` + unityaot 引用集）**：Game.Domain 64 源 / Game.View
  89 源 / 三测试文件 —— 全部 exit 0（抓出并修复一处真实编译错误：NpcAutonomyMode 缺 using）。
- **EditMode 全量**：**1315 / 1315 / 0**（Round C2 + Round G 两轮；1311 家底 + R1-R4，T13 断言更新）。
- **PlayMode 两轮同形**：**127 / 126 / 0 / 1 skip**（Round D 与 Round E 逐位同形；skip =
  `ColonyReportProbeTests.R1_ReportPanelProbe_...`——A-COL-08 的条件跳过探针，非本任务；
  122 家底 + V7/V8/V9/Distribution + A-COL-08 新增；`*PixelEvidence*` 15/15 全绿）。
- **arch**：exit 0（28 asmdef，Colony/** 零改动，V8 边界干净）。
- **V7 变异复红**：备份 sha256（3f7ba4e9…）→ 去掉预约排除判断（`IsAvailableFor(agentId)`→
  `IsAvailable`）→ 过滤轮 `Game.Domain.Tests.AffordanceTests` **17/16/1 RED**（R1
  `Reserve_BlocksOthersFindBest_SelfStillSelectable_ReleaseRestores` 必红：A 自己也选不中被预约条目）
  → 还原 → **sha256 逐字节一致** → 还原态全量 **1315/1315/0**（Round G）。
- 文件规范：9 个涉改文件逐一核对 UTF-8 无 BOM、纯 LF、尾部换行齐全。

### 四、风险与登记（待主控裁决项）

1. **冲突（T13 断言更新）**：A-AI-03 的 `Brain_SelectsAffordanceAction_...` 断言「Execute 必须真正
   占用容量（Occupants==1）」与 v2 §一.3「选中改 Reserve、到达才 Claim」**直接矛盾**（同一行为两种
   契约）。按新契约更新该两处断言（Occupants==0 + ReservedBy=="npc-a"；ReleaseClaim 双清），测试
   原意图（选中 Rest/记录 LastClaimed/撤供回 Idle）不变。13 条用例数不变、12 条逐字未动。
2. **偏差（V4==3 不可达）**：开工复验（简报 §二 点名）实测——SCN_Game 无 `npc-anim-*`（该名单属
   SCN_Anim：NPC_A/NPC_C 携 `_npcId npc-anim-a/c`）；SCN_Game 无 NpcUtilityView（grep guid=0）；
   **【Round B 修正】`ENV_Barrel`/`ENV_Tree_01` 其实存在**（prefab 实例名覆盖在 `m_Modification`
   块里，开工时 grep `m_Name:` 漏检——ENV_Barrel@origin、ENV_Tree_01×2@(8.6,4.2)/(-7.4,-7.2)，
   SceneAffordances 用的是**真实锚点坐标**而非兜底）；闲置候选仍仅社交锚点「NPC」1 个（-2.2,1,2.2）。
   → 候选解析实况化，实际 seeker 数 = min(3, 候选数)，SCN_Game 现况=1；V8 用例按
   `min(DefaultSeekerLimit, IdleCandidateCount)` 断言。场景未来补 NPC 时数量自动增长，无需改代码。
3. **Colony 投影形态（施工前探查后定）**：选 **HUD 行为分布标签**（IMGUI 第三行：劳作 a · 抗议 b ·
   串联 c · 传闻 d · 休整 e + 今日总数）。理由：简报最小形态「闲置 NPC 行为标记」要求把 Colony 具名
   npcId 的行为标到场景对象头上，但 Colony 模拟具名 NPC（npcs.csv 100 人）与 SCN_Game 场景 NPC
   （交互锚点 npc-elder）**没有任何身份映射**——标上去是伪归因，违反项目「不伪造」纪律。聚合标签
   诚实呈现两层自主；「具名 NPC 场景角色化（需先建身份映射）」登记为后续任务候选。
4. **演示口径（Round B 实测修正）**：自主模式挂载的 seeker 以 `DeviceBroken=true` 起手（Work 满权重
   1.0）。真实锚点几何下 ENV_Tree_01 距唯一候选约 10.7m——满疲劳的 Rest 仅 1.0×0.46=0.46，输给
   「坐在自家社交点」的 Social 0.6（NPC 会原地社交、移动不可见——Round B 的 V8 失败即此）；
   DeviceBroken 让 Work = 1.0×prox(ENV_Barrel@3.1m)=0.845 决定性胜出，走向木桶的自主移动真正可见。
   NPCWorldState 是 seeker 自有状态（A-AI-04 测试同样直写），不是模拟数据。
5. **NpcUtilityView 停用-复位路径现况 0 命中**：SCN_Game 无 utility NPC；该路径按简报 §三「停用
   组件+复位」实现，供场景未来加入 utility NPC 时生效（SCN_Utility/SCN_Anim 不在本桥门控内）。
6. **新文件 .meta 未生成**：`NpcAutonomyMode.cs`/`ColonyAutonomyBridge.cs` 的 .meta 由 Unity 在下次
   导入时自动生成（AGENTS 纪律不手建；用户编辑器打开中，聚焦即导入）。
7. **暂停态可用性**：G 走 Update（暂停时仍运行），Tab 走 TickQueue Input 段（暂停不冻结）——两者
   暂停态均可开合，语义一致；G 在既有键位（Tab/F/J/R/B/E/Q/K/L/WASD/Space/Shift/Z+Ctrl/Esc/X）
   中空闲，DebugFastForward 是方法无热键，无冲突。
8. **白名单缺口（登记）**：v2 §四白名单未列 `AffordanceAction.cs`，但 §一.3 明文要求「AffordanceAction
   选中改 Reserve、到达才 Claim」——按需求正文执行（改 3 处：Score/Execute/ReleaseClaim），验收按
   本表核对。
9. **并发施工观察（如实登记）**：施工期间工作区出现非本任务的并行改动（`ColonySaveBridge.cs` 修改于
   本任务开工前已存在；施工中后期新增 `TASK-A-COL-08.md`/`ColonyReportProbeTests.cs`/
   `probe_colony_report.ps1`/`.gitignore`/`Tools/Probes/README.md` 改动）——疑似 A-COL-08 并行施工，
   本任务零触碰、互不依赖；主控 add 时注意分账。

### 五、补跑与修复（2026-11-23 锁窗后，主控派单续）

**背景**：首段施工期用户编辑器持锁，60s×10 重试全程失败（登记于早期版本 §三），当时以独立编译
（Roslyn+unityaot 引用集）三套 exit 0 交付并如实登记「运行时套件未跑」。锁窗到达后（Unity 进程
清零、无 lockfile）执行本节全链。

**复现（按主控给的 A-COL-08 实测数字）**：
- Round A（EditMode 全量）：**1315/1314/1** —— 失败 `AffordanceTests.Release_ByOtherAgent_...`
  （"容量 2：B 可占用一个名额 Expected: True But was: False"）。
- Round B（PlayMode 全量）：**127/124/2/1 skip** —— ①`AffordancePlayModeTests.Tick_WithThreeAffordances_
  SelectsTheNearest`（"Expected: 1 But was: 0"）；②`AffordanceScenePlayModeTests.V8_...`（"自主模式下
  NPC 必须开始移动 Expected: not equal (-2.2,1,2.2) But was: (-2.2,1,2.2)"）。

**三个失败逐个根因与修法（全是本刀在途用例的标定问题，产品语义零改动）**：

| # | 根因 | 修法 |
|---|---|---|
| R4（EditMode） | 用例自相矛盾：产品语义「被预约同时挡他人的选择**与**占用」（R3 自己断言了），R4 原稿却断言 B 可占容量 2 的另一名额 | 用例修正：`Claim(npc-b)` 改断言 `IsFalse`，补「B 无 stake→Release 返回 false 且不动 A 的预约」，其余断言不变 |
| Tick_WithThreeAffordances（PlayMode，A-AI-03 既有基线文件，非白名单——按主控「是用例标定问题就修用例」授权） | 与 T13 同类：该用例把「选中即 Claim」的旧契约钉死在 `near.Occupants==1`，我的 Execute 改 Reserve 后必然红 | 断言随新契约更新：`near.ReservedBy=="npc-playmode"` + `near.Occupants==0`（选中未到达不占容量）；原意图「Tick 必须选中最近的床」不变（`AreSame(near, LastClaimed)` 保留） |
| V8（PlayMode） | 两处叠加：①演示口径标定错——开工复验漏检 prefab 名字覆盖，误判 ENV_Tree_01 不存在走兜底坐标；真实锚点几何下 Rest 0.46 < Social 0.6（NPC 坐在自家社交点），满疲劳口径下 NPC 原地社交不动；②（顺带排查出的必红点）`Destroy` 是 Unity 帧末语义，关闭后同帧 `FindObjectOfType<AffordanceSeeker>` 会找到垂死 seeker | ①演示口径改 `DeviceBroken=true`（Work=0.845 决定性胜出，走向 3.1m 外木桶——风险④已修正）；②无残留断言改跨帧核对（`yield return null` 后再 FindObjectOfType），位置复位断言提前 |

**顺带加固（非 Round B 失败，复盘发现的潜在 flake）**：V9 的 `AreEqual(actionsBefore, ...)` 跨时间
比较**活的**模拟计数器——ColonyWorldHost 1 秒=1 游戏日，测试窗口跨到日结计数器合法变化（本轮靠
运气未触发）。改为只读性的正确观测：字典**引用稳定** + `BuildDistributionText` 纯函数改前改后
副本对照；计数行为由 A-COL-06 T1-T5 钉死。

**修复后全链数字（全部亲证）**：

| 环节 | 结果 |
|---|---|
| 独立编译复检（3 处修复后） | 三套 exit 0（顺带抓出并修复 V9 一处 `snapshot[kv.Value]`→`kv.Key` 笔误） |
| Round C2（EditMode 全量） | **1315/1315/0** ✓ |
| Round D（PlayMode 全量） | **127/126/0/1 skip** ✓，`*PixelEvidence*` 15/15 全绿 |
| Round E（PlayMode 全量，第二轮同形） | **127/126/0/1 skip** ✓ 逐位同形 |
| arch 校验器 | exit 0 ✓ |
| V7 变异复红 | 过滤轮 AffordanceTests **17/16/1 RED**（R1 必红：去预约排除后 A 自己也选不中被预约条目） |
| 还原 | sha256 `3f7ba4e9…` 与备份**逐字节一致**，排除判断回归 ✓ |
| Round G（还原态 EditMode 全量） | **1315/1315/0** ✓ 全绿 |

**噪声登记（如实）**：Round C（修复后首跑）唯一失败为 `ColonyAutonomyTests.T5_PerfBound`
（100 NPC×30 天 64.1ms > 50ms 红线）——A-COL-06 的墙钟性能测试，与本刀零 diff 的 Colony 域无关；
安静复跑（Round C2）T5 = 33.8ms PASS，Round G 亦全绿。判定为机器瞬时负载噪声，未改任何产物。
**环境纪律**：变异过滤轮的批处理进程滞留（结果已写出、日志已 shutdown），命令行核实为本会话自身
批处理（非用户编辑器）后 taskkill 处置；全程零触碰用户资产。

**V1-V8 终态**：V1 ✓（R1+V7）、V2 ✓（R3+V7 场景）、V3 ✓（R3/R4+V7）、V4 ✓（V8/V9）、V5 ✓
（V6+PixelEvidence 15/15+两轮 PlayMode）、V6 ✓（1315/0 含 13+8 旧断言——其中 T13/Tick 两处按新
契约更新，待主控追认）、V7 ✓（变异复红+还原）、V8 ✓（arch exit 0+Colony 零 diff+V9 只读断言）。

## 六、状态

**done(见施工报告，待验收)**——v2 施工+补跑收尾完成：全链亲证 EditMode **1315/1315/0**（C2/G 两轮）、
PlayMode **127/126/0 两轮同形**（PixelEvidence 15/15）、arch exit 0、V7 变异复红→sha256 逐字节还原→
Round G 全绿；锁窗期独立编译三套 exit 0 记录保留（§五·六·三）；三个在途用例失败的根因与修法见
§五·六·五（产品语义零改动，全部是用例标定问题——其中 Tick_WithThreeAffordances 为 A-AI-03 既有
基线文件，按主控补跑派单授权修正）；T13/Tick 断言更新与 V4==3 偏差待主控追认（§五·六·四.1/2、
§五·六·五）。v1 时代（2026-11-09 登记、2026-11-15 收尾确认）的 held 原因与施工前核查记录见文末
§八归档。

## 六·五、验收清单（v2 独立亲证，2026-11-23，验收子代理）

> 验收：general-purpose 验收子代理（与施工不同代理，独立亲证、不采信施工结论）。全部数字本机亲跑；
> 证据文件在 `TempLogs/acc_*`（本地证据，不入库）。

| # | 断言 | 独立亲证结果 | 证据 |
|---|---|---|---|
| V1 | 预约语义 | ✓ R1 `Reserve_BlocksOthersFindBest_SelfStillSelectable_ReleaseRestores` Passed、R2 `Reserve_IsIdempotent_...` Passed | `acc_editmode_full.xml`（全量 1315/1315/0） |
| V2 | 到达才占 | ✓ R3 `Claim_AfterOwnReserve_ConvertsToOccupancy_ReleaseClearsBoth` Passed（选中 Occupants==0 / 到达 ==1 断言在案） | 同上 |
| V3 | 放弃清预约 | ✓ R3/R4 `Release_ByOtherAgent_KeepsReservation_...` Passed（双清语义） | 同上 |
| V4 | G 键自主模式 | ✓ PlayMode V8 挂/卸（`ActiveSeekerCount==min(SeekerLimit,IdleCandidateCount)` 断言+复位无残留，主控追认的 min 式）、V9 投影同步+只读、`ColonyProjection_DistributionText` 纯函数——全 Passed；DeviceBroken 演示口径（主控追认）在案 | `acc_playmode_r1/r2.xml` |
| V5 | 默认零影响 | ✓ V6_WithoutSeeker NPC 位置不变 Passed；`*PixelEvidence*` 15/15 两轮全绿；Colony T1-T5 全绿（模拟行为零变化） | `acc_playmode_r1/r2.xml` |
| V6 | 旧断言不破 | ✓ A-AI-03 13 条（AffordanceTests 17 条中除 R1-R4 外逐条 Passed，含 T13 按追认新契约更新）+ A-AI-04 8 条（Scene 套件 V1-V6/DefaultBrain/Abandons）+ Colony T1-T5——全部 xml 点名 Passed | 两全量 xml |
| V7 | 变异复红 | ✓ **独立执行且变异位置与施工不同**：施工改 `AffordanceRegistry.FindBest` 过滤行；本验收改 `Affordance.IsAvailableFor` 本体（去预约排除判断，同靶点不同落点）。过滤轮 **17/14/3 RED**——R1 必红（"A 预约后 B 的 FindBest 必须跳过该条（V7 变异靶点）"），R2/R4 同语义红（合法击杀）；还原后 sha256 `95b5fcfa…b2eb0` 与变异前**逐字节一致**；还原态全量 **1315/1315/0** 全绿 | `acc_v7_mut_editmode.xml` + `acc_ai05_v7_sha_before/after_restore.txt` |
| V8 | 边界干净 | ✓ git status 无 Colony/** Domain 条目（`Domain/Colony` 产品路径零 diff、场景 .unity 零 diff）；arch 校验器亲跑 **exit 0**（asmdef 28 / 期望矩阵 14 / 违规 0，PASS） | git status + `TempLogs/arch_report.txt` |

环境纪律备注：变异过滤轮 Unity 进程滞留持锁（xml 已写出、挂起于 shutdown），按 HANDOFF 纪律**命令行核实**（batchmode+本项目+本会话自身批处理命令行）后 taskkill；由此造成的首次全量启动撞锁崩溃（exit 21）已核实根因并重跑成功。全程零触碰用户资产、零误杀。

## 六·六、验收结论（v2）

**PASS（2026-11-23，验收子代理独立亲证）**——V1~V8 全部通过（证据见 §六·五）。三项主控追认项核实落地：T13/Tick 断言随新契约更新（两用例 Passed、原意图保留）、V4 改 min 式（SCN_Game 现况 1 seeker，场景补 NPC 自动增长）、DeviceBroken 演示口径。白名单核对：涉改 11 文件全部归属本任务（v2 §四 8 文件 + `View/Colony/**` 跨域桥 + 简报 §一.3 明文要求的 `AffordanceAction.cs`（施工报告风险⑧登记）+ 主控授权的 Tick 用例文件），互不越界；Colony Domain 零 diff；未 commit（HEAD 仍 de4a9ee）。工作区并存项（非本任务，留主控分账）：`ColonySaveBridge.cs`（内容标注 TASK-A-COL-07 情报点随档）、`Docs/Parity` 5 个 PNG 与 `Tools/Probes/out/planet-budget-*.png`（前序任务噪声）。文件规范：全部涉改文件 UTF-8 无 BOM、纯 LF、尾部换行齐全（ps1 例外 CRLF）。

## 七、v2 合流改写记录

- 2026-11-22：主控裁决采纳评估选项 B（`Docs/Planning/A-AI-05合流评估-2026-11-22.md`），本简报由评估
  子代理改写为合流形态「自主行为场景可见化」。v1 原文（2026-11-09 简报 + held 登记）完整保留于 §八，
  一字未涂改。

## 八、归档：v1 原文（2026-11-09 ~ 2026-11-15，历史记录，勿改）

# TASK-A-AI-05 —— NPC 自主模式（默认关，G 键开）+ 预约态

> A-AI-04 已能把「可供性 → 走过去 → 占用」跑通，但 seeker **默认不挂**，玩家看不到；
> 且 Claim 发生在**选中**而非**到达**，等于提前占座。本刀把这两件事收掉。

## 一、目标

1. **自主模式**：SCN_Game 里按 **G** 切换「NPC 自主模式」：
   - 开：给场景里 N 个闲置 NPC（默认 3 个，优先 `npc-anim-*`）挂 `AffordanceSeeker` 并 `StartSeeking()`；
   - 关：全部 `Stop()` 并移除 seeker（NPC 回到原驱动）；
   - **默认关**（像素用例零影响，与 SurvivalLoop/BuildMode 同一纪律）。
2. **预约态**：把「选中即 Claim」改成：
   - 选中时 `Reserve(agentId)`（**不占容量**，只防止别人再选同一个）；
   - 到达（≤ 半径）才 `Claim()` 转正式占用；
   - 放弃/停止/超时 → `Release()` 同时清预约；
   - 容量满/被预约 → `FindBest` 跳过它。
3. **HUD**：IMGUI 显示「NPC 自主模式：开/关」与当前 seeker 数。
4. **不回归**：默认关时 NPC 位置逐位不变；既有测试全绿。

## 二、现状（施工前必读）

- `Game/View/AI/AffordanceSeeker.cs`（A-AI-04）：brain + 移动 + Claim + 10s 超时；**每拍先 Release 再 Tick**（防自占用抖动）。
- `Game/Domain/AI/Affordance/AffordanceRegistry.cs`：`Claim/Release/FindBest`（**本刀要加预约**，注意 A-AI-03 的 13 条用例必须继续绿）。
- `Game/View/AI/SceneAffordances.cs`：SCN_Game 锚点注册（3 条）。
- 挂载/输入范式：`SurvivalLoop`（旧 Input + IMGUI）。

## 三、设计要点

- 预约用 `ReservedBy`（string agentId，空 = 无）；`Reserve` 幂等；`IsAvailable` 语义扩展为
  「未满员 **且** 未被他人预约」；**自己**的预约不算占位（同级判定要看 agentId）。
- 不要破坏 A-AI-03 的既有断言：`IsAvailable` 在无预约场景下行为必须与之前完全一致。
- 自主模式管理器放在 `Game/View/AI/NpcAutonomyMode.cs`：`Toggle()` / `IsEnabled` / `ActiveSeekerCount`；
  `sceneLoaded` 门控 SCN_Game；**默认不挂任何 seeker**。
- 关闭时要把 seeker `Destroy` 并把 NPC 的 `NpcUtilityView` 状态复原（参考它原有的 idle 行为）；
  若复原代价大，就只 `Stop()` + 停用组件并记录为受限。

## 四、白名单

- `UnityProject/Assets/Game/Domain/AI/Affordance/Affordance.cs`、`AffordanceRegistry.cs`（加预约）
- `UnityProject/Assets/Game/View/AI/AffordanceSeeker.cs`、`NpcAutonomyMode.cs`（新增）
- `UnityProject/Assets/Game/Tests/Domain/AffordanceTests.cs`（补预约用例）
- `UnityProject/Assets/Game/Tests/PlayMode/AffordanceScenePlayModeTests.cs`（补自主模式用例）
- `Docs/Logs/TASK-A-AI-05.md`、`TASKS.md`（仅本任务行）

不碰：`UtilityBrain`、`NpcUtilityView` 的既有逻辑、`SurvivalLoop`、`BuildMode`。

## 五、验收清单

| # | 断言 | 判据 |
|---|---|---|
| V1 | 预约语义 | A 预约后 B 的 `FindBest` 跳过该条；A 自己仍能选中；Release 后恢复可被选 |
| V2 | 到达才占 | 选中后到达前 `Occupants==0`；到达后 `==1` |
| V3 | 放弃清预约 | 超时 / `Stop()` 后预约与占用都清零 |
| V4 | 自主模式 | G 开启后 `ActiveSeekerCount==3` 且 NPC 开始移动；关闭后 seeker 归零、NPC 位置不再变化 |
| V5 | 默认零影响 | 未开启时场景 NPC 位置逐位不变；`*PixelEvidence*` 全绿 |
| V6 | 旧断言不破 | A-AI-03 的 13 条 + A-AI-04 的 8 条全绿 |
| V7 | 变异复红 | 去掉「预约排除」判断 → V1 必红；恢复后全绿 |

## 六、施工报告

**未动工**（2026-11-09 收尾登记）：本任务简报已写，但施工与验收均未执行。经核实，白名单文件无任何改动——`NpcAutonomyMode.cs` 不存在、`AffordanceRegistry.cs` 无预约（`Reserve`/`ReservedBy`）痕迹，**无半成品代码**，属于干净的"待派"状态。

## 七、验收结论

**held（暂缓，非 done）**：用户决定进入正式游戏开发，本功能（NPC 自主模式 + 预约态）后置。理由：A-AI-03/A-AI-04 已把「可供性 → 走过去 → 占用」核心链路闭环，本任务只补"默认可见 + 预约语义"的玩法化收尾，不阻塞正式开发。恢复时按 §五 验收清单直接派施工即可，不影响既有 1140+110 用例基线。
