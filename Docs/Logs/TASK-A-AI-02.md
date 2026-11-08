# TASK-A-AI-02 —— AI 队友协作（GOAP 社交协调层）

状态: doing | 派发: 2026-11-04 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

### 背景（ROADMAP 方向四 A-AI-02）

效用 AI/GOAP/记忆态度/个性均已就绪（A-TECH-01/04、A-AI-01）。缺的是**多个自主 NPC 之间的协调**：AI 队友（跟随玩家、分担任务、不抢同一目标）与敌对群体的简单协调（包抄不重叠）。

### 设计要点（纯 C# Domain）

1. `Game/Domain/AI/Coordination/`：
   - `TaskBoard`（任务板：NPC 可发布/认领任务——认领互斥，同任务一个 NPC；任务有位置+类型+到期）——GOAP 的前置条件天然适配任务认领（认领=效果达成）
   - `SquadCoordinator`（队伍：成员列表+编队位置槽（玩家周围环形槽位）+成员间任务不冲突）
   - 敌对协调最小版：AttackAssignment（按距离+态度分派目标，避免全员打同一个）
2. **与既有系统集成**：UtilityBrain 评估时把 TaskBoard 任务作为候选行动（认领成本=距离）；SquadCoordinator 管理 DataKernel 群体的攻击分派（A-AND-08 迁移后的 kernel 数据读——只读）
3. 演示：玩家+2 队友 NPC（跟随+协防）+3 敌人——分派不重叠（断言）；队友认领拾取任务不冲突（断言）
4. 可证伪用例（EditMode）：任务认领互斥；编队槽唯一；分派不重叠；任务到期回收；GOAP 规划消费 TaskBoard 任务（两步链先认领再执行）
5. 自测：EditMode 全绿（基线 960+新增）+Validate exit 0

### 约束

- 白名单：`Assets/Game/Domain/AI/**`、`Assets/ZFrame/Gameplay/AI/**`（若框架级）、Tests/**、本日志
- 不改效用核/GOAP/记忆态度既有语义（只扩展）；Unity 批处理纪律；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0
- 演示场景 View 层最小（PlayMode 截图可选）

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理 ｜ 日期: 2026-11-08 ｜ 自测: **EditMode 1082/1082 全绿（基线 1031 + 新增 51）** + 架构校验器 **exit 0**（0 违规）

### 一、实现概览（纯 C# Domain，零引擎依赖）

新增程序集内目录 `UnityProject/Assets/Game/Domain/AI/Coordination/`（Game.Domain，命名空间 `Game.Domain.AI.Coordination`），8 个类型 8 文件（1297 行实现）：

| 文件 | 内容 |
|---|---|
| `CoordinationTask.cs` (91) | 任务记录：Id/Kind/Position/ExpiresAt/Status/ClaimedBy/ClaimedAt，`IsExpired(now)`（到期即失效：now >= ExpiresAt；+Infinity = 永不过期） |
| `CoordinationTaskStatus.cs` (19) | Open/Claimed（完成任务离板，板上只有两态） |
| `TaskBoard.cs` (231) | 任务板：`Publish`（板发 id task-N 单调递增）/ **`Claim` 认领互斥**（原子，第二人 false，持有人再认领也 false）/ `Complete`/`Abandon`（仅持有人）/ **`SweepExpired` 到期回收**（连陈旧 claim 一起清）/ `OpenTasks`/`AllTasks` 发布序稳定 |
| `FormationSlot.cs` (75) | 环形编队槽纯函数：角 = startAngle + 2π·i/n，**槽唯一由构造保证**；`AreDistinct` 两两相异断言助手 |
| `AttackAssignment.cs` (291) | 敌对分派纯函数：全部 (attacker,target) 对按**有效距离** = distance/(1+AngerAttackBoost×anger) 升序贪心（**距离+态度**，复用 NpcAttitudeMath.AngerAttackBoost=1.5），并列破序=攻击者序→目标序（全序排序，确定性）；每目标容量默认 1（**分派不重叠**），轮空者显式 null 不挤占 |
| `SquadCoordinator.cs` (280) | 队伍：成员列表（join 序）+ `UpdateFormation` 环形槽（成员 i 占槽 i/n）+ **成员任务不冲突**（`TryClaimTask`：一人至多一项 + 板互斥；Leave/Complete/Release 释放）+ `AssignDefense` 委派 AttackAssignment |
| `TaskBoardCandidates.cs` (139) | **UtilityBrain 集成**：开放任务 → UtilityAction 候选（名 `task:<id>`），**认领成本=距离**（UtilityCurves.Fall(distance,1,10)×0.9，手算锚点 d=2⇒0.8、d=8⇒0.2），Execute 委托真调 board.Claim；感知键缺失 ⇒ 欲望 0 |
| `TaskBoardGoapBridge.cs` (174) | **GOAP 集成**：原子 task:T:open/claimed/done + 算子 [ClaimTask:T（代价=距离）, ExecuteTask:T（代价 1）]；per-claimant 符号世界（他人的 claim 不可见，符号接地；持有人身份强制留在板上） |

### 二、设计要点对照（简报 5 条逐条）

1. **TaskBoard/SquadCoordinator/AttackAssignment** ✅ 见上表；GOAP 前置条件天然适配认领（认领=claimed 原子达成）在 TaskBoardGoapBridge 落实。
2. **与既有系统集成** ✅ UtilityBrain 路径：`TaskBoardCandidates.CreateClaimActions` 产出的真 UtilityAction 注册进真 `UtilityBrain`（测试 TaskBoardIntegrationTests.cs:32），执行委托真改板；SquadCoordinator 经 `SetMemberState`/`AssignDefense` 参数**只读接收** kernel 数据快照（kernel 位置由宿主泵入纯结构体——Game.Domain asmdef 矩阵禁止引用 ZFrame.Render.Bindings，故边界=泵入只读，kernel 本身零改动）。
3. **演示** ✅ 以 Domain 级 EditMode 演示交付（`SquadDemo_ClaimsAndSlotsAndDefense_AllDisjoint`，TaskBoardIntegrationTests.cs:202）：玩家原点+2 队友+3 敌人+2 拾取任务——编队槽唯一/认领不冲突/协防分派不重叠三断言齐。View 演示场景（Game/View、PlayMode）超出本任务白名单（`Assets/Game/Domain/AI/**`、`Assets/ZFrame/Gameplay/AI/**`、`Tests/**`、本日志），未动；简报已注明"View 层最小（可选）"，如需场景另派。
4. **可证伪用例** ✅ 五件全真（行号见第三节）。
5. **自测** ✅ EditMode 1082/1082 全绿 + 架构校验器 exit 0。

### 三、自测证据（可复核）

- **EditMode**：`TempLogs/aai02_editmode.xml`——`total=1082 passed=1082 failed=0 skipped=0 result=Passed`（Unity exit 0；未带 -quit，Git Bash 启动，单实例+清锁纪律遵守）。新增 51 用例分布：TaskBoardTests 12、FormationSlotTests 6、AttackAssignmentTests 10、SquadCoordinatorTests 13、TaskBoardIntegrationTests 10。
- **简报四件可证伪用例行号锚点**：
  - 任务认领互斥：`TaskBoardTests.cs:53 Claim_TwoClaimantsRace_ExactlyOneWins`（+69 同人再认领非获取、97 到期即拒）
  - 编队槽唯一：`FormationSlotTests.cs:59 RingPositions_ArePairwiseDistinct`（+72 负例碰撞；`SquadCoordinatorTests.cs:32 TwoMembers_GetDistinctRingSlots` 手算 (12,20)/(8,20)）
  - 分派不重叠：`AttackAssignmentTests.cs:46 ThreeAttackers_ThreeTargets_AllPairsDistinct`（+63 轮空 null；`SquadCoordinatorTests.cs:166 AssignDefense_NoOverlap`）；态度：`:92 AngrierAttacker_WinsEquidistantTarget`
  - 到期回收：`TaskBoardTests.cs:110 SweepExpired_RemovesExpiredTask_ReleasesStaleClaim`（陈旧 claim 同收）
  - GOAP 两步链：`TaskBoardIntegrationTests.cs:105 Goap_PlansTwoStepChain_ClaimThenExecute_WithDistanceCost`（TotalCost=距离5+执行1=6；`:131` 链逐步落板；`:152` 持有人单步重规划；`:174` 他人 claim 符号不可见）
- **架构校验器**：`ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine` **exit 0**，`TempLogs/arch_report.txt` 违规数 0（asmdef 矩阵 + 纯度扫描均过）。
- **既有语义零破坏**：`git status` 仅新增文件（Coordination/ 8 文件 + Tests/Domain/ 5 文件 + Unity 生成 .meta），**无任何既有文件 M 改动**；效用核/GOAP/记忆/个性代码零触碰（ZFrame/Gameplay/AI 四文件原样）。

### 四、边界与备注（验收提示）

- **Kernel 只读边界**：Domain 层不引 ZFrame.Render.Bindings（asmdef 矩阵约束，ArchitectureValidator 锁死）；kernel 群体位置由宿主读后以 `AttackerCandidate`/`TargetCandidate`/`SetMemberState` 纯结构体**泵入**，协调层只读该快照——A-AND-08 kernel 读路径零改动。
- **候选行动一拍有效期**：任务集动态（认领/过期/回收），`TaskBoardCandidates` 产出的候选闭合当拍 now 与任务快照，集成 agent 每拍重建（与 NpcUtilityAgent 静态候选"建一次"的约定不同，属动态集合的刻意偏差，已在类型文档注明；Brain 确定性契约不受影响——同开放集快照 ⇒ 同注册序 ⇒ 同排序）。
- **GOAP 深度上限**：两步链消耗 maxDepth 2；`GoapPlanner.DefaultMaxDepth=3` 内可再容一步前置。多任务串行规划需逐任务出目标（未做批量，简报未要求）。
- 测试中两处首轮失败（MoreAttackersThanTargets 断言选错胜者、TwoClaimantsRace 开放池计数）为测试笔误已修，实现零改动——二轮 1082 全绿。
- 文件规范：全部 UTF-8 无 BOM/LF/尾换行，自检通过；未 git commit（按纪律留主控）。

## 验收清单（主控写）

- [ ] V1 EditMode 全绿（数字写入结论）；认领互斥/编队槽唯一/分派不重叠/到期回收用例真实（行号）
- [ ] V2 与 UtilityBrain/GOAP 集成路径审查（行号）；既有语义零破坏（git diff 既有测试核对）
- [ ] V3 Domain 纯度+Validate exit 0（自跑）
- [ ] V4 文件规范+施工报告四节完整

## 验收结论（验收代理写）

**验收: PASS**（2026-11-08，验收子代理独立亲跑，与施工不同代理；除下述变异复做的临时改动外未改任何产品/测试代码，变异已逐字节恢复并以复跑 XML 证明，`cmp` 逐字节复核通过）

- **V1 PASS** —— EditMode 亲跑 **exit 0、1082/1082/0**（`TempLogs/aai02_verify_editmode.xml`：`total=1082 passed=1082 failed=0 skipped=0`；单实例+清锁纪律遵守，测试跑未带 -quit）。新增 **51 用例逐名核对全 Passed**：TaskBoardTests 12 + FormationSlotTests 6 + AttackAssignmentTests 10 + SquadCoordinatorTests 13 + TaskBoardIntegrationTests 10 = 51（逐条 `result="Passed"` 抽验 51/51，逐名清单见本验收 XML）。数字调和：HANDOFF 家底 1031（A-AI-01 闭环口径）+ 51 = 1082，与施工报告自测数一致。
- **V2 PASS** —— 四件可证伪用例真实（源码行号亲核）：认领互斥 `TaskBoardTests.cs:53`（第二人 false :59、持有人/状态断言 :61-65）+ 同人再认领非获取 :69 + 到期即拒 :97；编队槽唯一 `FormationSlotTests.cs:59`（6 槽两两相异）+ 负例 :72 + `SquadCoordinatorTests.cs:32` 手算 (12,20)/(8,20)；分派不重叠 `AttackAssignmentTests.cs:46`（三对互异）+ 轮空显式 null :63 + 态度 :92（怒 1 有效距离 0.8 < 2，手算与 `NpcAttitudeMath.cs:27` AngerAttackBoost=1.5 一致）+ `SquadCoordinatorTests.cs:166`；到期回收 `TaskBoardTests.cs:110`（陈旧 claim 同收、回收后认领必败 :123）。**变异复做 1 次**：`TaskBoard.cs:135` 认领互斥改放行（删 `Status == Claimed` 拒绝分支，临时）→ EditMode **1082→1077 通过/5 红**——恰为互斥依赖 5 条：`Claim_TwoClaimantsRace_ExactlyOneWins`、`Claim_SameClaimantReclaim_IsNotAnAcquisition`、`MemberHoldsAtMostOneTask_BoardMutexStillHolds`、`SquadDemo_ClaimsAndSlotsAndDefense_AllDisjoint`、`SquadDemo_CompletingTask_LetsTheOtherAgentReplan`（`TempLogs/aai02_verify_MUTANT_editmode.xml`）→ 逐字节恢复（`cmp` 通过，全源零变异痕迹）→ 复跑 **1082/1082/0**（`TempLogs/aai02_verify_RESTORED_editmode.xml`）。互斥真实可证伪、用例真实咬合。
- **V3 PASS** —— 集成路径逐行核实：UtilityBrain 侧 `TaskBoardCandidates.cs:113-123`（`OpenTasks()` → 真 `UtilityAction`，分数 `ScoreClaim` :121、执行委托 `board.Claim` :122）真注册进真 `UtilityBrain`（测试 `TaskBoardIntegrationTests.cs:38-45` brain.Add、:51 Tick 胜出、:54-56 板上真变 Claimed）；认领成本=距离手算锚亲核：`ScoreClaim` :133-137 = BaseDesire × `UtilityCurves.Fall`（UtilityCurves.cs:66-69 = 1−Ramp），d=2 ⇒ 0.9×8/9=0.8、d=8 ⇒ 0.9×2/9=0.2（测试 :60-80 逐位咬合）。GOAP 侧 `TaskBoardGoapBridge.cs:112-159`：开放任务产 [ClaimTask（前置 open、加 claimed、删 open、代价=距离 :134-139）, ExecuteTask（前置 claimed、加 done、删 claimed、代价 1 :140-145）] 算子对；持有人单步执行 :147-155；他人 claim 零算子（per-claimant 符号世界 :88-94/:161-172，测试 :174-185）；过期任务零原子零算子（:127-130，测试 :188-197）；两步链 TotalCost=6（测试 :105-128：dist((0,0),(3,4))=5+1，`GoapPlanner.Plan` 原签名消费、`DefaultMaxDepth=3` 内）。**既有语义零破坏**：`git diff HEAD` 仅 `Docs/Logs/TASK-A-AI-02.md`（本日志），UtilityBrain/UtilityAction/UtilityCurves/NpcUtilityAgent/GoapPlanner/GoapState/GoapAction/GoapPlan/NpcAttitudeMath 及全部既有测试 status 干净，1031 基线用例一个不少（总数调和吻合）。
- **V4 PASS** —— Domain 纯度亲 grep：新增 8 文件零 UnityEngine/UnityEditor/引擎类型，仅 System*、`Game.Domain.AI.{Memory,Utility}`、`ZFrame.Gameplay(.AI)`；`Game.Domain.asmdef` 引用仅 ZFrame.Core/Data/Gameplay（不引 ZFrame.Render.Bindings）；kernel 边界=宿主泵入只读快照（`SetMemberState`/`AssignDefense` 参数、`AttackerCandidate`/`TargetCandidate` 纯结构体），A-AND-08 kernel 文件零改动（git status 干净）。**ValidateFromCommandLine 自跑 exit 0**（`TempLogs/aai02_verify_arch.log`，报告 `TempLogs/arch_report.txt` @ 2026-11-08 23:45:14：asmdef 26 扫描、违规数 0、PASS）。
- **V5 PASS** —— 文件规范亲测：13 个触及 .cs 全 UTF-8 无 BOM（首 3 字节 od 实测）+ 纯 LF（CR 字节数 0）+ 末尾换行（0a），.gitattributes `eol=lf` 相容；新文件 .meta 齐全（Unity 生成，未手改）、一类型一文件、公共 API XML 注释在场；施工日志四节齐全（简报/施工报告/验收清单/本节）；白名单内核对：触及文件仅在 `Assets/Game/Domain/AI/**`、`Assets/Game/Tests/Domain/**`（既有 Domain 测试之家）与本日志，View 层零触碰。

**验收瑕疵（不降级，留痕）**：①演示以 Domain 级 EditMode 交付（`SquadDemo_ClaimsAndSlotsAndDefense_AllDisjoint`，TaskBoardIntegrationTests.cs:202 三断言齐），View 场景/PlayMode 截图未做——简报第 26 行明示"View 层最小（PlayMode 截图可选）"且 Game/View 在白名单外，施工报告 §3 已如实标注，属简报内合规取舍；②kernel 数据读按"宿主泵入只读快照"实现而非 Domain 直读 kernel——asmdef 矩阵禁止 Game.Domain 引 ZFrame.Render.Bindings（V4 校验器锁死），报告 §4 已注明且 kernel 零改动，语义（只读）不变，属被架构约束倒逼的等价实现；③本验收 3 轮 Unity 批处理证据以 XML/日志为准，均干净退出（无僵尸进程残留）；`TempLogs/arch_report.txt` 被本验收自跑覆盖（共享 scratch 路径），覆盖前施工版（23:34:25）同为违规 0 PASS，无信息损失。

**验收裁决**：**PASS**——施工报告自测数字（EditMode 1082/校验器 exit 0/零既有文件改动）与验收亲跑结果全部一致，无虚报；GOAP 两步链 TotalCost=6 与手算锚 0.8/0.2 亲核成立；V1-V5 全过，提交主控走 commit+闭环刷新（本验收未 git commit，按纪律）。
