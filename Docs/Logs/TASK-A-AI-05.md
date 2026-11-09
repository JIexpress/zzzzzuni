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
