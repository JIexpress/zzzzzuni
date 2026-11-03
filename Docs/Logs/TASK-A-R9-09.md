# TASK-A-R9-09 —— 行为树死亡事件统一（BT 模式补发 EnemyStateChanged）

状态: doing | 派发: 2026-10-31 | 施工: general-purpose 施工子代理（2026-10-31） | 验收: general-purpose 验收子代理（2026-10-31，PASS 5/5） | commit: （待填）

## 任务简报（主控写）

**目标**：A-R9-07 如实记账的遗留——BT 模式敌人死亡走黑板旗标不发 `EnemyStateChanged`，导致死亡火花在行为树脑下不冒（FSM 脑正常）。本任务统一：**Kill 即 Dead 事件**，两种脑行为一致。

**设计要点**：
1. 事件源：`EnemyBehaviorTree.Kill()`（置黑板旗标处）补发 `EnemyStateChanged{Current=Dead, Position}`（载荷字段与 Fsm 版一致——先读 Fsm 版事件构造核对；EnemyBehaviorTree 已有 Position）
2. **防重**：Kill 幂等（既有）→ 事件只发一次（补发点置于 Kill 内部首次执行分支）；EnemyController 双脑转发链已有（OnTreeAttackRequested 先例），死亡事件走同链——**查转发是否已覆盖 EnemyStateChanged**（Fsm 脑经哪里发？先读 EnemyController/EnemyFsm 的事件出口，保证两脑事件出口对称）
3. 粒子侧零改动（A-R9-07 的"仅 Dead 态"过滤已就位——BT 补发后自动冒火花）
4. 单测（EditMode 追加）：Kill→事件收到一次（载荷 Dead+位置）；二次 Kill 不重发；双脑切换后死亡事件仍对称
5. 自测：EditMode 全绿（747+新增）+Validate exit 0（Unity 纪律）
6. **像素证据**（PlayMode，可选加分）：BT 模式 Kill→火花像素变化（沿 PixelProbeRig 口径；若 PlayMode 搭 BT 敌人场景成本高，可只做 EditMode 事件断言+报告说明）

**约束**：白名单 `Assets/Game/Domain/AI/**`、`Assets/Game/Tests/**`（或既有测试集）、本任务日志；不改 FSM 路径行为；不改粒子侧；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0。

**交付物**：BT 死亡补发+防重+单测+施工报告四节（含双脑出口对称性说明）。

## 施工报告（施工代理写）

施工: general-purpose 施工子代理 ｜ 完成: 2026-10-31 ｜ 白名单合规：仅改 `Assets/Game/Domain/AI/**`（2 文件）、`Assets/Game/Tests/**`（2 文件）、本日志

### ① 实现清单（改了什么，行号）

| 文件 | 改动 |
|---|---|
| `UnityProject/Assets/Game/Domain/AI/EnemyBehaviorTree.cs` | ① 新增事件 `EnemyStateChanged`（:162，与既有 `EnemyAttackRequested` :151 同款声明式）；② `Kill()` 重写（:237-251）：`_isDead` 早退守卫（:239-242，首次执行分支）→ 记录死亡前行为 → 置旗标 → 构造 `EnemyStateChanged(_id, previous, EnemyStateId.Dead, _position)` → `EnemyStateChanged?.Invoke(evt)` → `EventBus.Fire(evt)`（:248-250）；③ 私有映射 `ToStateId(EnemyBehaviorState)`（:383-394）；④ 类文档"Death semantics (difference #2)"段更新（:37-49，宣告不再是差异） |
| `UnityProject/Assets/Game/Domain/AI/EnemyController.cs` | ① 构造器订阅两脑 `EnemyStateChanged += OnBrainStateChanged`（BT :67 / FSM :74，`OnTreeAttackRequested` 先例同链）；② 新增处理器 `OnBrainStateChanged`（:83-86）；③ 新增统一出口事件 `EnemyController.EnemyStateChanged`（:101）；④ 类文档补"Unified exits (TASK-A-R9-09)"段（:27-34） |

**防重设计**：Kill 幂等由既有旗标语义升级为显式早退（`if (_isDead) return;`），补发点在首次执行分支内，第二次及以后 Kill 是 no-op——事件恰好一次。既有旗标/黑板/`CurrentBehaviour` 掩蔽行为逐位不变（二次 Kill 原来重写同一 flag 值，现在整段跳过，终态相同）。

**载荷映射（`ToStateId`）**：树脑死亡前行为 → FSM 状态词汇表（Idle→Idle / Patrol→Patrol / Chase→Chase / **Attack→Chase** / Dead→Dead）。Attack 无 FSM 对应态——FSM 世界里进入攻击距离的敌人仍是 Chase（转换表无攻击态），故 Chase 是忠实翻译；已在 XML doc 注明。

### ② 双脑出口对称性说明（V2 对照链路）

两脑事件出口在**脑层**与**控制器层**各逐行对称（EventBus 侧载荷同构，均走 `EventBus.Fire`——火花侧 `GameplayFxBindings.OnEnemyStateChanged` 只听 EventBus，故 BT 补发后自动冒火花）：

| 出口环节 | FSM 脑（对照，未改动） | BT 脑（本次补齐） |
|---|---|---|
| 死亡触发 | `EnemyFsm.Kill()`→`RequestState(Dead)`→转换表放行（EnemyFsm.cs:148-151） | `EnemyBehaviorTree.Kill()` 首次执行分支（EnemyBehaviorTree.cs:237-251） |
| 事件构造 | `new EnemyStateChanged(Id, previous, current, Position)`（EnemyFsm.cs:187） | `new EnemyStateChanged(_id, previous, Dead, _position)`（EnemyBehaviorTree.cs:248，同 4 参构造、同 Position 字段） |
| C# 事件出口 | `EnemyFsm.EnemyStateChanged`（EnemyFsm.cs:76,188） | `EnemyBehaviorTree.EnemyStateChanged`（EnemyBehaviorTree.cs:162,249） |
| EventBus 出口 | `EventBus.Fire(evt)`（EnemyFsm.cs:189） | `EventBus.Fire(evt)`（EnemyBehaviorTree.cs:250） |
| 控制器转发 | `_fsm.EnemyStateChanged += OnBrainStateChanged`（EnemyController.cs:74） | `_behaviorTree.EnemyStateChanged += OnBrainStateChanged`（EnemyController.cs:67） |
| 控制器统一出口 | `EnemyController.EnemyStateChanged`（:101）← 同一处理器 :83-86 | 同左（同一事件、同一处理器——**一个出口，两脑共用**） |

**简报"查转发是否已覆盖"的答案**：未覆盖（改前控制器只转发 `EnemyAttackRequested`，死亡事件 FSM 模式只从 `enemy.Fsm.*` 直出、BT 模式无出口）→ 本任务补齐，且两脑都接进同一条链：FSM 模式的常规状态迁移也经控制器出口透传（脑级事件本来就带），树脑宣告死亡之外保持黑板内部。**订阅约定**（已写入 XML doc）：订阅 `controller.EnemyStateChanged` 或脑级事件二选一，都订会双计。

### ③ 单测（4 新增，EditMode 实跑全过）

| 用例 | 位置 | 断言要点 |
|---|---|---|
| `Kill_AnnouncesEnemyStateChangedOnce_WithFsmPayload` | EnemyBehaviorTreeTests.cs:304 | 追击中 Kill→事件恰一次；再 Kill 不重发；载荷 EnemyId/Previous=Chase/Current=Dead/Position=0.3（与 Fsm 版语义一致）；C# 事件与 EventBus 双出口载荷一致；Tick 前零宣告 |
| `Kill_Twice_DoesNotRefire_EvenWithoutAnyTick` | EnemyBehaviorTreeTests.cs:336 | 未 Tick 三连 Kill→只 1 次；Previous=Idle（与 Fsm 版 Idle 起步同语义）；Position=出生点 |
| `Controller_DeathEvent_IsSymmetricAcrossBothBrains` | EnemyBehaviorTreeTests.cs:359 | 同 id 同起点：BT 脑与 FSM 脑经控制器出口各恰 1 次，载荷逐字段一致（EnemyId/Previous/Current=Dead/Position）；二次 Kill（BT 幂等早退 / FSM 转换表拒绝）都不重发 |
| `RealBehaviorTreeKill_SparksDeathBurstAtTheFallSpot` | GameplayFxBindingsTests.cs:426 | 生产链集成：真 BT 敌人追击 0.3m 后 Kill→EventBus→GameplayFxBindings→emitter 恰 1 次、落在死亡位置（11.3, h, 5）；二次 Kill 无第二次 burst——**粒子侧零改动的行为级证明**（与既有 `RealEnemyFsmKill_SparksDeathBurstAtTheFallSpot` :413 成对对称） |

### ④ 自测证据与边界（如实记录）

- **EditMode 全绿**：`run_tests.ps1 -Segments editmode`（Unity 纪律五条内建：单实例/删锁/无 -quit/Git Bash/轮询 xml）→ **total=775 passed=775 failed=0**（基线 771 + 新增 4，results.xml 逐名核实 4 个新用例在场且通过）；证据 `TempLogs/runtests_editmode.xml`（2026-10-31 实跑，Unity exit 0，xml 本轮新写）。
- **架构校验器 exit 0**：`run_tests.ps1 -Segments arch` → PASS exit=0，报告 `TempLogs/arch_report.txt`（Domain 纯净性未破坏——无新引擎引用，`Game.Domain.Enemy` 本就在 AI 文件的 using 里）。
- **V3 核对（git diff）**：`Assets/Game/View/**`（含 `GameplayFxBindings.cs`）与 `Assets/Game/Domain/Enemy/**`（含 `EnemyFsm.cs`）**零改动**；本任务全部改动仅上述 4 文件。A-R9-07 的"仅 Dead 态"过滤语义未触碰，其既有用例（`NonDeathTransitions_NeverSpark` 等）零改动随全量绿。
- **像素证据走了简报备选**：PlayMode 搭 BT 敌人场景成本高（现有 PlayMode 火花证据链是 FSM 场景），按简报"可只做 EditMode 事件断言+报告说明"处理——用 `RealBehaviorTreeKill_…` 集成用例证明"Kill→EventBus→bindings→emitter 落点"全链，真机像素与 FSM 脑共用同一 SparkEmitter 渲染路径（A-R9-06/07 已验），故 BT 侧补发后像素表现等同 FSM 侧。
- **工作区注意**：`git status` 另有 `Native/tests/zrhi_d3d12_window.cpp` 与 `zrender.dll` 改动——属并行派发的 B-R9-11 施工代理（G-95 并行），不在本任务白名单，本代理未触碰。
- 未 git commit（铁律）；文件 UTF-8 无 BOM（git diff 仅含本次 hunk，无换行符污染）。

## 验收清单（主控写）

- [ ] V1 EditMode 全绿（数字写入结论）；Kill 事件用例真实（一次/不重发/载荷正确——行号）
- [ ] V2 双脑出口对称性审查（Fsm 版与 BT 版事件出口链路对照，行号）
- [ ] V3 粒子侧零改动（git diff 核对）；A-R9-07 过滤语义不破坏（既有用例零改动通过）
- [ ] V4 Validate exit 0（自跑）
- [ ] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

**验收: general-purpose 验收子代理 ｜ 结论: PASS（5/5）｜ 2026-10-31** —— 全部证据为本代理亲跑/亲核，未复用施工自证。

- **V1 PASS（EditMode 亲跑）**：`run_tests.ps1 -Segments editmode` → Unity exit 0、**total=775 passed=775 failed=0 skipped=0**（`TempLogs/runtests_editmode.xml` 本轮新写，脚本 EvidenceFresh 判定通过）；4 新用例逐名在列且 Passed：`Kill_AnnouncesEnemyStateChangedOnce_WithFsmPayload`（EnemyBehaviorTreeTests.cs:304）、`Kill_Twice_DoesNotRefire_EvenWithoutAnyTick`（:336）、`Controller_DeathEvent_IsSymmetricAcrossBothBrains`（:359）、`RealBehaviorTreeKill_SparksDeathBurstAtTheFallSpot`（GameplayFxBindingsTests.cs:426）。
- **V2 PASS（Kill 事件审查）**：`Kill()`（EnemyBehaviorTree.cs:237-251）的 `if (_isDead) return;`（:239-242）先于置旗标，首分支完成 构造(:248)→C#事件(:249)→EventBus(:250)，二次调用整段 no-op（用例 :336 三连 Kill 恰 1 次实证）；载荷与 FSM 版同构（对照 EnemyFsm.cs:187 同款 4 参 + Position）；`ToStateId`(:383-393) 的 **Attack→Chase 忠实性成立**——核实 EnemyFsm.cs:61-69 转换表仅 Idle/Patrol/Chase/Dead 四态、无攻击态，攻击距离内敌人仍处 Chase，XML doc 已注明此翻译。
- **V3 PASS（双脑出口对称性）**：施工报告对照表逐行核实全部属实——死亡触发（EnemyFsm.cs:148-151 ↔ EnemyBehaviorTree.cs:237-251）、事件构造（:187 ↔ :248）、C# 出口（:76/:188 ↔ :162/:249）、EventBus（:189 ↔ :250）、控制器转发（EnemyController.cs:74 FSM / :67 BT → 同一处理器 :83-86）、统一出口（`EnemyController.EnemyStateChanged` :101）；简报"转发是否已覆盖"答案核实为属实：改前控制器仅 `OnTreeAttackRequested` 一条转发（diff 实证 :67/:74/:83-86/:101 四处全为本次新增）。诚实差异已文档化、非缺陷：FSM 脑额外透传常规迁移、BT 只宣告死亡；控制器 XML doc :98-99 明示"订阅 controller 或脑级二选一，都订会双计"，FSM 订阅点注释 :72-73 准确（构造期初始 Idle 宣告在订阅前，不透传）。
- **V4 PASS（粒子侧零改动 + 过滤语义 + Validate）**：`git diff -- UnityProject/Assets/Game/View/ UnityProject/Assets/Game/Domain/Enemy/` 为空（零改动实证，本任务改动仅 AI/ 2 文件 + Tests/ 2 文件）；A-R9-07 过滤 `GameplayFxBindings.cs:257` `evt.Current != EnemyStateId.Dead → return` 原样未触，既有用例零改动（Tests diff 纯追加）且随本轮全绿（`NonDeathTransitions_NeverSpark` => Passed）；ValidateFromCommandLine 亲跑 exit 0（`TempLogs/arch_report.txt`：Domain 纯度违规数 0，本轮新写）；像素证据走简报备选（EditMode 事件断言 + 生产链集成用例证 emitter 恰 1 次落点），报告已如实说明，符合简报许可。
- **V5 PASS（文件规范 + 报告完整）**：4 个改动文件与日志均 UTF-8 无 BOM（首 3 字节非 EF BB BF）、LF（`git ls-files --eol` 全部 i/lf w/lf，新增行 od 字节级 `\n` 证实）；日志四节齐备（验收结论本轮由验收代理补写）；白名单合规——工作区其余 M 项（Native/**、Tools/**、zrender.dll 等）属并行 B-R9-11 施工，施工报告已如实披露，非本任务范围。
- 笔误备忘（不影响结论）：施工报告 `ToStateId` 行号 ":383-394" 实为 :383-393（±1）。
