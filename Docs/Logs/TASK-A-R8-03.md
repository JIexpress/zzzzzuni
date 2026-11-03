# TASK-A-R8-03 行为树框架（组合节点/黑板/Running 语义）+ 敌人 AI 升级示例

状态: doing | 派发: 2026-10-27 | 施工: general-purpose 施工子代理(接手续建)（2026-10-27 完成，EditMode 651/651 全绿 + arch/naming 校验器 exit 0） | 验收: general-purpose 验收子代理（2026-10-27，**PASS 6/6**） | commit: （待填）

## 任务简报（主控写）

**目标**：R8 愿望清单——行为树框架（课程 M14-72 教法：Sequence/Selector/Decorator/Condition/Action/黑板/Running 三态），与既有 Fsm 并存互补；把 M4-01 的敌人示例从 Fsm 升级为行为树驱动（保留 Fsm 版对照）。

**设计要点**：
1. `ZFrame/Gameplay/BehaviorTree/`（纯 C# 零 UnityEngine，架构校验器把守）：
   - `BTStatus { Success, Failure, Running }`
   - 节点基类 `BtNode { BTStatus Tick(BtContext ctx); void Reset(); }`（Reset 清 Running 态防重入——课程坑点）
   - 组合：`Sequence`（顺序，Running 从子继续）、`Selector`（优先级，前 Success 即返回）、`Parallel`（策略：全部成功/任一成功）
   - 装饰：`Invert`、`Repeat(n|forever)`、`Cooldown(sec)`、`Succeeder`
   - 叶子：`BtCondition(Func<BtContext,bool>)`（构造期缓存委托防闭包 GC——课程 M14 坑点）、`BtAction(Func<BtContext,BTStatus>)`
   - `Blackboard`：类型化键值（TryGet/Set/泛型缓存），`BtContext{Blackboard, Owner, float Dt}`
   - `BehaviorTreeRunner`：持根节点，`Tick(dt)`；事件 `BehaviorTreeRooted`（可选）
2. 敌人 AI 升级（`Game/Domain/AI/`，纯 C#）：
   - `EnemyBehaviorTree`：Selector[ 抬头看 Dead → Chase(视野内) → Attack(攻击距离) → Patrol(路点) ]——黑板键：玩家位置/自身位置/路点表；Condition 全部基于黑板数据（外部泵入，同 M4-01 模式）；Attack 动作发 `EnemyAttackRequested` 事件
   - 与 Fsm 版**并存对照**：`EnemyController` 可选驱动模式（enum），默认行为树，报告说明两者语义差异
3. 单测（追加到既有 Domain 测试集）：
   - 组合语义：Sequence 三态推进/中断恢复（Running 后从该子继续不重头）、Selector 短路、Parallel 两策略
   - 装饰：Invert/Repeat(n 次数正确)/Cooldown 冷却窗口/Reset 清 Running（Running 中 Reset 后从头——防重入用例）
   - 条件委托缓存：同一委托实例复用（无每 tick 新闭包——可用委托引用相等断言）
   - 黑板类型安全（错型 TryGet false）
   - 敌人树：黑板泵入距离序列断言状态转移（视野进入→Attack 距离→事件触发）
4. 自测：EditMode 全绿+Validate exit 0（Unity 纪律）

**约束**：只动 `UnityProject/Assets/ZFrame/Gameplay/`、`UnityProject/Assets/Game/`、`UnityProject/Assets/ZFrame/Tests/`、本任务日志；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0。

**交付物**：框架全件+敌人升级+单测全绿+施工报告四节。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理(接手续建) ｜ 完成时间: 2026-10-27 ｜ 自测: **EditMode 651/651 全绿（results.xml failed=0）+ 架构校验器 exit 0 + 命名校验器 exit 0**

### ① 接手半成品 + 审计结论

前代理被限额中断，盘上只有 `ZFrame/Gameplay/BehaviorTree/` 15 个 .cs（git `??` 未跟踪、**无 .meta**=Unity 从未导入过），任务日志无施工报告。逐文件审计结论：

- **框架 15 件全部可用，语义与简报一致，审计未发现需返工的缺陷**：`BTStatus` 三态；`BtNode.Tick/Reset` 双方法契约（Reset=清 Running 记忆防重入）；`Sequence/Selector` 带 resume 内存（Running 从该子继续不重头）且 Reset 向下传播；`Parallel` 双策略（`ParallelPolicy.RequireAll/RequireOne`，完成/中止时 `ResetAll` 防子节点残留 Running）；装饰 `Invert/Repeat(n|Forever)/Cooldown/Succeeder`（Running 一律穿透，Cooldown 以 `ctx.Dt` 累计、构造即就绪）；叶子 `BtCondition/BtAction` 构造期缓存委托并暴露 `Predicate/Action` 供引用相等断言；`Blackboard` 类型安全（错型 `TryGet` 返 false 不转换）；`BtContext`（黑板/Owner/Dt）；`BehaviorTreeRunner` 单 context 就地改 Dt 零分配 + `BehaviorTreeRooted` 事件 + 非正 dt 忽略。全部纯 C# 零 UnityEngine。
- 缺失部分（本次续建）：`Game/Domain/AI/` 敌人升级整套不存在；单测不存在。
- 首轮 Unity 实跑揪出我方初版 1 个编译错（`EnemyBehaviorTree.TryReadRange` out 参数未定值赋值，CS0177）与 1 个设计错（详见④-2），均已修复后全绿——即"半成品从未编译过"的风险已用实跑闭合。

### ② 交付清单

**A. 框架（前代理半成品，审计通过后原样保留，15 文件）**：`Assets/ZFrame/Gameplay/BehaviorTree/` 下 `BTStatus / BtNode / BtContext / Blackboard / BtCondition / BtAction / Sequence / Selector / Parallel / ParallelPolicy / Invert / Repeat / Cooldown / Succeeder / BehaviorTreeRunner`（`.cs`+Unity 自动生成 `.meta`）。

**B. 敌人 AI 升级（本次新建，`Assets/Game/Domain/AI/`，纯 C#）**：
| 文件 | 内容 |
|---|---|
| `EnemyBehaviorTree.cs` | 行为树大脑（EnemyFsm 的升级对照版）。树形=简报原文 Selector[Dead → Chase(视野内) → Attack(攻击距离) → Patrol(路点)]（BuildTree :232）；黑板键常量 `Keys`（玩家位置/自身位置/路点表 + IsDead/三档距离阈值，构造期泵入静态半、每 tick 泵入传感半）；**条件只读黑板**（改键即改行为）；Attack 动作发 `EnemyAttackRequested`（owner 事件 + `EventBus.Fire`，同 `EnemyStateChanged` 纪律）；公开 `CurrentBehaviour/Position/WaypointIndex/IsDead/Blackboard/Root/Runner/LastStatus` 供驱动与测试。核心设计：chase 动作在进入攻击距离时**让位（Failure）**——Selector 见 Success 即短路，若 chase 在位返回 Success 则 Attack 永远轮不到；让位后同一 tick 落入 Attack 分支（ChaseAction :282 注释） |
| `EnemyAttackRequested.cs` | 只读结构事件（EnemyId/EnemyPosition/PlayerPosition） |
| `EnemyBehaviorState.cs` | 行为读数枚举 Idle/Patrol/Chase/Attack/Dead（FSM 的 EnemyStateId 无 Attack，故独立枚举） |
| `EnemyDriveMode.cs` | 驱动模式枚举：`BehaviorTree=0`（**默认**）/`Fsm=1` |
| `EnemyController.cs` | 模式开关外壳：统一面（Id/Position/IsDead/SetPlayerPosition/Tick/Kill/CurrentBehaviour/EnemyAttackRequested），按模式持有 `EnemyFsm` 或 `EnemyBehaviorTree`，FSM 状态映射到统一读数 |

**C. 单测（追加到既有 Domain 测试集 `Assets/Game/Tests/Domain/`，50 条）**：
- `BehaviorTreeTests.cs`（39 条）：Sequence 三态推进/失败短路/**中断恢复不重头**（:98）/**Reset 防重入：Running 中 Reset 后从头**（:122）；Selector 优先级短路（:143）/Reset 后从最高优先级重评估（:193）；**Parallel 双策略** RequireAll 任一失败中止并 Reset Running 子（:227）/RequireOne 任一成功即胜（:239）/完成即 Reset 子节点状态（:257）；Repeat n 次数精确+计数复原（:288）/Forever；Cooldown 窗口（含跨边界入口检查语义，:330）；构造期**委托缓存引用相等**（:395/:405）；黑板类型安全错型 false（:502）等；runner 事件/零分配 context/Reset 传播（:440 等）。
- `EnemyBehaviorTreeTests.cs`（11 条）：**黑板泵入距离序列断言状态转移** Patrol→Chase→Attack+事件（:116，EventBus+owner 双订阅）；攻击冷却节流+原地硬直（:145）；**LoseRange 迟滞仅护 mid-Running 追击**（:168）；**追击中击杀→尸体不动/不发事件**（:186）；**黑板改键即改行为**（:207）；**成品树委托逐 tick 引用稳定**（:219）；**Controller 默认=行为树**（:250）与 **Fsm 版保留可切、FSM 模式绝不发攻击事件**（:274）。

### ③ 自测数字（全部本代理亲跑，GitBash+清锁+无 -quit+轮询 xml 纪律）

| 项 | 结果 | 证据 |
|---|---|---|
| Unity EditMode | **651/651 全绿，failed=0**（601 基线 + 50 新，算术自洽；新夹具 39+11 已按 classname 点数核对） | `TempLogs/runtests_editmode.xml`（`result="Passed"`、`failed=0`、`result="Skipped"` 计 0）；Unity 进程在 xml 落盘后未于 300s 宽限期内退出，由 run_tests.ps1 纪律 taskkill——results.xml 为权威证据（脚本判 PASS[zombie killed]） |
| 架构校验器 | **exit 0**：asmdef 扫描 24/期望矩阵 11 条/纯度扫描 `ZFrame/Gameplay`+`Game/Domain`/**违规 0** | `TempLogs/arch_report.txt`（Domain 纯度含新增 AI 5 件） |
| 命名校验器 | exit 0 | `TempLogs/name_report.txt` |
| 文件规范 | 新文件 UTF-8 无 BOM / LF（od+file 逐个核过） | 本代理执行记录 |
| git | 未 commit；工作区仅新增（无既有文件改动） | `git status --porcelain` 全为 `??` |

### ④ 偏差（对简报的自主决策，均已在代码注释与本报告说明）

1. **Chase/Attack 分支次序照简报原文**（Dead→Chase→Attack→Patrol，Attack 优先级低于 Chase）。因 Selector"首个 Success 即返回"，chase 分支在进入攻击距离后改为**让位 Failure**（而非返回 Success），同一 tick 落入 Attack——语义正确且有 `AttackCooldown`/`DistanceSequence` 用例证明；若简报原意是 Attack 更高优先级，交换两分支即可，行为等价。
2. **施工过程实修两处**：①`TryReadRange` out 未定值赋值（CS0177，Unity 实跑暴露）；②上述 chase 让位设计错（首版返回 Success 导致 Attack 分支不可达，5 条用例红后改定）。
3. **黑板键在简报三键（玩家位置/自身位置/路点表）之外扩了 IsDead/路点索引/三档距离阈值**：阈值入黑板是为了兑现"Condition 全部基于黑板数据"（否则条件闭包 Tuning 常量）。
4. **View 层未动**：简报设计点 2 把敌人升级圈定在 `Game/Domain/AI/`（纯 C#），验收清单也未要求 View；Demo 场景的 `EnemyView` 仍驱动 FSM 版（正是要保留的对照组）。`EnemyController` 已备好统一面，View 接入属后续小改动。
5. **死亡语义**：FSM 版 Kill 即时打断（表驱动）；BT 版因普通 Selector 的 resume 内存，死亡在分支边界生效（动作全部对 IsDead 快速失败，最多一个 stale tick 由 `CurrentBehaviour` 计算属性掩蔽，:186 用例覆盖）。

### ⑤ 风险

1. **BT 版与 FSM 版非逐位等价**（这正是"语义差异报告"要求的内容）：迟滞窗口（FSM 任意时刻按 LoseRange 退避；BT 仅护 mid-Running 追击，分支重评估后按 ChaseRange 重新判定）、死亡打断时机（见④-5）、FSM 版无 Attack。消费方若期望 FSM 逐帧等价行为需读本节。
2. **普通 Selector 的 resume 内存不重检高优先级分支**（课程即如此教）：若后续给敌人加"高优先级打断"需求（受击硬直等），需引入 Reactive Selector 变体或外部 `Runner.Reset()`，框架本身不拦。
3. **Attack 的 `Succeeder(Cooldown(...))` 组合**：冷却窗口内 Attack 分支仍 Success（原地硬直不掉回巡逻），事件由 Cooldown 节流——若产品要"冷却期转身离开"，需把 Succeeder 换成让位式动作。
4. **EditMode 末轮为 zombie-taskkill 取证**（xml 完整、failed=0）：验收代理照常以 xml 为权威证据即可；如需 Unity 自然退出 0 的形态，重跑一遍 `-Segments editmode` 大概率自然退出（前两轮曾自然退出 exit 2）。

（施工报告完）

## 验收清单（主控写）

1. EditMode 全绿（用例数写入结论）
2. 三态推进/中断恢复/Reset 防重入用例真实（行号）；Parallel 双策略用例
3. 条件委托缓存断言（引用相等）行号
4. 敌人树黑板驱动状态转移用例；Fsm 版保留可切（行号）
5. Domain 纯度：校验器 exit 0（自跑）
6. 文件规范+施工报告完整

## 验收结论（验收代理写）

**验收: general-purpose 验收子代理（独立于施工代理，未修改任何代码）｜ 2026-10-27 ｜ 结论: PASS 6/6**

验收方式：全部证据本代理亲跑/亲读取证，未复用施工方结论；自跑前先将施工方证据备份为 `TempLogs/acc_r803_constr_*`（防覆盖），随后亲跑三段校验取自己的证据。

| # | 清单项 | 结论 | 证据（本代理亲取） |
|---|---|---|---|
| 1 | EditMode 全绿（用例数写入结论） | **PASS** | 亲跑 `Tools/run_tests.ps1`（editmode,arch,naming 三段，脚本 exit 0）：亲跑产生的 `TempLogs/runtests_editmode.xml` 头属性 `testcasecount="651" total="651" passed="651" failed="0" skipped="0" result="Passed"`，且本次 Unity **自然退出 exit 0**（非 zombie-taskkill）；新夹具按 `<test-case>` 元素逐点数：BehaviorTreeTests=39、EnemyBehaviorTreeTests=11（+2 为 TestFixture 的 test-suite 元素），601 基线+50 新=651 算术自洽 |
| 2 | 组合语义用例真实（行号） | **PASS** | `BehaviorTreeTests.cs`：Sequence 三态推进 :67/:80 + **Running 中断恢复不重头** :98（:112 断言 a 不被重 tick）；Selector 优先级短路 :143（:153 低优先级不许被 tick）；**Parallel 双策略** RequireAll 任一失败中止并 Reset Running 子 :227（:235）、RequireOne 任一成功即胜 :239（:246）、完成即 Reset 子节点 :257；**Reset 清 Running 防重入** :122（:133 清记忆、:135 从第一个子重 tick、:136-137 脚本从头+Reset 只生效一次）；Cooldown 窗口含跨边界入口检查语义 :330（:343）；**委托引用相等** AreSame :407（BtCondition.Predicate）/ :421（BtAction.Action） |
| 3 | 敌人树黑板驱动+双脑可切+chase 让位 | **PASS** | `EnemyBehaviorTreeTests.cs`（11 条全数核对）：**黑板泵入距离序列驱动转移** Patrol→Chase→Attack :116（EventBus+owner 双订阅 :121-123，Attack 事件触发断言 :137-141）；冷却节流+原地硬直 :145；LoseRange 迟滞 :168；追击中击杀尸体不动不发事件 :186；黑板改键即改行为 :207；**双脑可切（EnemyDriveMode）** 默认=BehaviorTree :250（:256）/Fsm 版保留可切且绝不发攻击事件 :274（:282/:290）；**chase 让位→Attack 可达性**由 :132-137 直接断言（进入攻击距离同 tick 到 Attack+恰好一次挥击），源码 `EnemyBehaviorTree.cs` ChaseAction :295-300 让位实现与注释一致、BuildTree :232-248 与简报树形（Dead→Chase→Attack→Patrol）一致 |
| 4 | Domain 纯度 | **PASS** | 架构校验器亲跑 exit 0：`TempLogs/arch_report.txt`（asmdef 扫描 24/期望矩阵 11 条/纯度含 Gameplay+Game/Domain/**违规 0**）；命名校验器亲跑 exit 0（`name_report.txt` 违规 0）；grep 亲证框架 `ZFrame/Gameplay/BehaviorTree/`（15 文件）与 `Game/Domain/AI/`（5 文件）**零 UnityEngine 引用**（grep 无匹配） |
| 5 | 文件规范抽查（含前代理无 .meta 问题） | **PASS** | 前代理"无 .meta=Unity 从未导入"的问题已闭合：15 框架件+5 AI 件+2 测试文件均有 Unity 生成的 .meta，BehaviorTree/AI 两个文件夹 .meta 也在（git status `??` 清单亲核）；22 个新 .cs 逐文件亲验 UTF-8 **无 BOM**（首 3 字节 od 非 efbbbf）+ **LF**（`tr -dc '\r'` 逐文件 0 字节，早期 grep -c $'\r' 计数是 Git Bash 误报，已用 od+tr 双重取证排除） |
| 6 | 施工报告四节完整（语义偏差与风险如实） | **PASS** | 任务日志四节齐全（简报/施工报告/验收清单/验收结论）；施工报告含接手审计①、交付清单②、自测数字③、**偏差如实④**（④-1 chase 让位 Failure 才可达 Attack 的关键语义偏差、④-2 自曝两处返工、④-3 黑板键扩展、④-4 View 未动、④-5 死亡语义分支边界生效）、**风险如实⑤**（⑤-1 明示"BT/FSM 非逐位等价"+迟滞/死亡打断/无 Attack 三项差异、⑤-2 普通 Selector 无高优先级重检、⑤-3 冷却期硬直、⑤-4 zombie 取证形态说明）——消费方必须读⑤-1 |

操作提示（给后续代理）：`run_tests.ps1 -Segments` 经 `powershell -File` 传逗号列表会因参数验证直接失败（exit 1、无副作用，本代理亲历）；需用 `powershell -Command "& '…run_tests.ps1' -Segments editmode,arch,naming"` 形式调用。

（验收结论完）
