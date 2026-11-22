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

## 六、状态

**held（v2 简报已重写，待排期）**——触发条件：用户要求"看见 NPC 在场景里自己动"，或 A/COL 当前候选
消化完。v1 时代（2026-11-09 登记、2026-11-15 收尾确认）的 held 原因与施工前核查记录见文末 §八归档。

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
