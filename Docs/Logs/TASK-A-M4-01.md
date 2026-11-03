# TASK-A-M4-01 InteractSystem 交互系统 + 输入桥接 + FSM 敌人示例

状态: 待验收 | 派发: （派发时填） | 施工: 续做施工代理（2026-10-25） | 验收: （待填） | commit: （待填）

**前置**：A-M3-03 完成（InputSystem 已接）。

## 任务简报（主控写）

**目标**：M4 垂直切片第一半——"完整交互"落地：逻辑层 Interactable/Interactor 纯 C#、View 层射线扫描与 HUD 提示、交互模板（门/拾取/NPC 对话）、typed FSM（游戏流程+敌人 AI 示例）、Player 输入映射桥接 EventBus。

**设计要点**：
1. **逻辑层**（`Game/Domain/Interaction/`，纯 C# 零 UnityEngine——架构校验器把守）：
   - `IInteractable { InteractInfo Info; bool CanInteract(ActorId actor); void Interact(ActorId actor); }`、`InteractionRegistry`（注册/最近目标查询/事件 InteractionTargetChanged{info|null}）
   - 模板（纯逻辑）：`DoorEntity`（开/关状态+Interact 切换+事件 DoorStateChanged）、`PickupEntity`（拾取→背包数据+ItemPickedUp 事件；简单 `Inventory` 字典）、`DialogEntity`（节点表+当前行推进+DialogAdvanced 事件）
   - `Fsm<TOwner>`（typed 状态机：IState Enter/Exit/Update(dt)/CanTransition；Transition 表；ChangeState 保证 Exit 调用——课程 M14-71 教法）——**放 ZFrame.Gameplay**（框架模式库，供复用）
   - 敌人示例（Domain）：`EnemyFsm` Idle/Patrol(路点)/Chase(距离触发)/Dead 状态流转+`EnemyStateChanged` 事件；纯数据驱动（位置/距离由外部泵入）
2. **View 层**（`Game/View/Interaction/`）：
   - `InteractionScanner`（MonoBehaviour）：相机射线（Physics.Raycast，2m）→命中物体上的 `InteractableProxy`（MonoBehaviour 适配器：转发到 Domain 实体）→Registry.SetNearest；E 键（InputSystem Interact 动作）→Registry.Interact
   - HUD 提示：订阅 InteractionTargetChanged→简单世界空间提示 Text（"按 E 开门"样式，M3 框架外简版，报告注明）
   - `EnemyView`：订阅 EnemyStateChanged 切换显示（颜色/图标简版）
3. **输入桥接**（`ZFrame/View/Input/` 扩展）：Player map 的 Move/Interact/Pause → EventBus 命令事件（`MoveInputEvent{vector2}` 等），Domain 不碰 InputSystem（纯度）
4. 场景：`SCN_Game`（Setup 脚本生成：地面+玩家胶囊+门+拾取物+NPC+敌人+相机），BuildSettings 第三位
5. 单测（Game.Domain.Tests 新程序集+既有测试集）：FSM 全序（Enter/Exit/拒绝转换）与非法转换；门/拾取/对话三模板行为+事件；敌人状态流转（距离泵入断言）；Registry 最近目标选择；Inventory 计数
6. 自测：EditMode 全绿+Validate exit 0（**Domain 纯度扫描必须过**）

**约束**：可动 UnityProject/Assets/ZFrame/Gameplay/（FSM 入框架）、Assets/Game/、Assets/ZFrame/View/Input/、Assets/ZFrame/Tests/、Scenes/、ProjectSettings（BuildSettings 经脚本）、本任务日志；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0。

**交付物**：交互全链+FSM+敌人+SCN_Game+单测全绿+施工报告四节。

## 施工报告（施工代理写）

**施工代理**：续做代理（前一位代理网络中断，留半成品）｜**日期**：2026-10-25｜**未提交**（禁 git add/commit）

### 1. 对前身代码的审计结论与修复（如实）

先逐文件读完全部未跟踪半成品，再决定「补」还是「改」。结论：**大部分可留用，但 EnemyFsm 是编译必断的残缺件**。

| 前身文件 | 审计结论 | 处置 |
|---|---|---|
| `Gameplay/Fsm/Fsm.cs` | **通过**。逐条核对：`ChangeState` 先 `previous.Exit` 再 `_current = next` 再 `next.Enter`（Fsm.cs:191-193），Exit 在 `try` 内先于 Enter 执行，Enter 抛异常也不会吞掉 Exit；非法路径（未登记对/自环/未知状态/`CanTransition` 否决/Enter-Exit-Update 内嵌套 ChangeState）全部 `return false` 且不触发任何回调；`Current` 只读，状态只能经 `Start`/`ChangeState` 写入（全文仅 139、192 两处赋值）。**未改动一行** | 保留 |
| `Gameplay/Fsm/IState.cs` | **通过**。生命周期契约与 Fsm 实现一致 | 保留 |
| `Gameplay/Math/Vec2.cs` | **通过**。`MoveTowards` 的 `maxDelta<=0` 返回原值、到达不越过，与 Unity 语义一致；零向量归一化返回 Zero | 保留 |
| `Gameplay/Input/InputCommandEvents.cs` | **通过**。空 `readonly struct` 可 `default(T)` 派发，无装箱 | 保留 |
| `Domain/Interaction/InteractionRegistry.cs` | **通过**。事件走 EventBus（104、117 行）；未注册实例不得成为 nearest；注销当前 nearest 会清空并补发事件（防悬空提示）。`SetNearest(null)` 执行清除但返回 false——语义略反直觉，但 XML doc 已明写，单测按文档断言，**不改**（改则牵动调用方语义） | 保留 |
| `Domain/Interaction/{ActorId,InteractInfo,InteractionCandidate,InteractionTargetChanged,DoorStateChanged,ItemPickedUp,DialogAdvanced}.cs` | **通过**。全部纯数据、零引擎类型 | 保留 |
| `Domain/Interaction/{DoorEntity,PickupEntity,DialogEntity,Inventory}.cs` | **通过**。三模板都是「实例事件 + EventBus」双通道；`CanInteract` 守卫在 `Interact` 内重复校验；拾取/对话终态后 `Info.Prompt` 清空 | 保留 |
| `Domain/Enemy/{EnemyStateId,EnemyTuning,EnemyStateChanged}.cs` | **通过**。Tuning 构造期做区间校验（`LoseRange >= ChaseRange >= 0`），迟滞语义清晰 | 保留 |
| `Domain/Enemy/EnemyFsm.cs` | **失败（致命）**：引用 `EnemyIdleState` / `EnemyPatrolState` / `EnemyChaseState` / `EnemyDeadState` / `EnemyStateBase` **五个类型，磁盘上一个都不存在**（前身只写了 FSM 外壳就断了）→ 全工程无法编译。另外 Idle 计时器没有宿主字段，而 `IState` 契约要求状态实例无状态 | **修复**：新建 5 个状态文件；在 `EnemyFsm` 补 `internal float IdleElapsed`（第 96-101 行）作为计时器宿主 |
| 全部前身文件 | 无 `.meta` | Unity 导入时已自动生成（未手改 guid） |

补充说明：`EnemyFsm` 的 `public event Action<EnemyStateChanged> EnemyStateChanged` 与同名事件类型共存，C# 允许（成员名/类型名不同声明空间），编译已验证通过。

### 2. 实现摘要

**Domain（纯 C#，零 UnityEngine）**

- `Enemy/EnemyStateBase.cs` + 四个状态：`Idle`（计时到且**有路点**才转 Patrol，玩家进入 ChaseRange 立即转 Chase）、`Patrol`（走路点、到点换索引、空路点回落 Idle、被玩家打断）、`Chase`（追最后泵入的玩家位置，`> LoseRange` 才放弃——滞回防抖）、`Dead`（终态：转换表无出边 + `CanTransition` 恒 false）。
- `Interaction/InteractionWorld.cs`（新增组合根）：一份 `InteractionRegistry` + 一份 `Inventory` + `ActorId Player`，进程级 `Shared` 供 View 取用，`ResetShared()` 供测试/换场景；`Interact()` 转调 `Registry.InteractNearest`。

**View（Game.View，单向依赖 Domain）**

- `Interaction/InteractableProxy.cs`：MonoBehaviour 适配器。按序列化 `InteractableKind` 构造 Domain 模板，`OnEnable` 注册 / `OnDisable` 注销到共享 registry；本身不含任何玩法规则。
- `Interaction/InteractionScanner.cs`：`Physics.RaycastAll` → 逐命中取 `InteractableProxy`（子碰撞体去重）→ 组装 `InteractionCandidate` → **由 Domain 的 `PickNearest` 裁决** → `Registry.SetNearest`；同时订阅 `InteractInputEvent` → `Registry.InteractNearest`。
- `Interaction/InteractionPromptHud.cs`：订阅 `InteractionTargetChanged` → 世界空间 `TextMesh` 显示 `Info.Prompt`，空提示/丢目标即隐藏，`LateUpdate` 跟随当前代理并朝向相机。
- `Enemy/EnemyView.cs`：持有 `EnemyFsm`，每帧泵入玩家 XZ、`Tick`、回写 transform；订阅 `EnemyStateChanged` 切颜色（Idle 灰/Patrol 黄/Chase 红/Dead 深灰）+ 头顶状态文字。
- `Player/PlayerMotor.cs`：`MoveInputEvent` 的消费者（相机相对移动 + 重力）。**简报未列**，为让 Move 桥接有消费者、切片可走可玩而补的最小实现（约 80 行）。
- `Editor/GameSceneSetup.cs`：`#if UNITY_EDITOR` 生成 `SCN_Game`（相机/平行光/地面 20×20/玩家胶囊+CharacterController/门/拾取物/NPC/敌人/扫描器），并把 BuildSettings 排成 `[0] SCN_Boot, [1] SCN_Empty, [2] SCN_Game`（保留其它条目相对顺序）。

**输入桥接（ZFrame.View）**

- `Input/PlayerInputBridge.cs`（新增）：`Move.performed/canceled`、`Interact.performed`、`Pause.performed` → `EventBus.Fire(MoveInputEvent/InteractInputEvent/PauseInputEvent)`（PlayerInputBridge.cs:79/90/101）。
- `Input/InputRuntime.cs`（改）：在 `EnsureCreated` 中随 gate 一起创建 bridge，暴露 `Bridge`；新增 `Shutdown()` 便于测试/重引导。
- Domain / Gameplay 侧**零 InputSystem 引用**（见 §4 证据）。

### 3. 变更文件清单

新增（23 个文件 + 1 场景，另附 Unity 自动生成的 meta）：

| 路径 | 说明 |
|---|---|
| `UnityProject/Assets/Game/Domain/Enemy/EnemyStateBase.cs` | 状态基类（StateId 映射 + 默认钩子） |
| `.../Enemy/EnemyIdleState.cs` `EnemyPatrolState.cs` `EnemyChaseState.cs` `EnemyDeadState.cs` | 四态实现（前身缺失件） |
| `.../Game/Domain/Interaction/InteractionWorld.cs` | 交互切片组合根 |
| `.../Game/View/Interaction/InteractableKind.cs` `InteractableProxy.cs` `InteractionScanner.cs` `InteractionPromptHud.cs` | 交互 View 层 |
| `.../Game/View/Enemy/EnemyView.cs` | 敌人显示层 |
| `.../Game/View/Player/PlayerMotor.cs` | Move 消费者（最小） |
| `.../Game/View/Editor/GameSceneSetup.cs` | SCN_Game 生成器 |
| `.../Game/Tests/Domain/Game.Domain.Tests.asmdef` + 8 个测试文件 | 新测试程序集（**未登记架构矩阵**） |
| `UnityProject/Assets/ZFrame/View/Input/PlayerInputBridge.cs` | 输入桥接 |
| `UnityProject/Assets/Scenes/SCN_Game.unity` | 切片场景（31210 B） |

修改：

| 路径 | 改动 |
|---|---|
| `.../Assets/Game/Domain/Enemy/EnemyFsm.cs` | 补 `internal float IdleElapsed`（唯一改动） |
| `.../Assets/ZFrame/View/Input/InputRuntime.cs` | 创建/暴露 `PlayerInputBridge`，新增 `Shutdown()` |
| `.../UnityProject/ProjectSettings/EditorBuildSettings.asset` | 追加 SCN_Game 至第 3 位（由 Setup 脚本写入） |

未触碰：`TASKS.md` / `HANDOFF.md` / `WORKLOG.md` / `ArchitectureValidator.cs`（Tests 程序集按 143-149 行豁免，**刻意不登记矩阵**，否则 `ArchitectureValidatorTests.cs:34` 会红）。工作区另有他代理的 `TASKS.md`、`Docs/Logs/TASK-B-R6-02/03.md` 变更，与本任务无关，未动。

### 4. 自测结果

**① EditMode（`TempLogs/m4_01_editmode.xml`，最终态复跑）**

```
<test-run id="2" testcasecount="466" result="Passed" total="466" passed="466" failed="0" inconclusive="0" skipped="0" ... duration="4.21" />
```

- 基线 372 → **466 全绿，净增 94，零删减、零放宽**（`result="Failed"` 计数 0）。
- 新增用例分布：`EnemyFsmTests` 21 / `FsmTests` 20 / `InteractionRegistryTests` 17 / `Vec2Tests` 8 / `DoorEntityTests` 8 / `DialogEntityTests` 8 / `InventoryTests` 6 / `PickupEntityTests` 6。
- 覆盖：FSM 全序（Enter/Exit 顺序、Exit 先于 Enter、Enter 抛异常仍先 Exit、自环/未登记/未知/否决/嵌套拒绝、Tick 只泵当前态）、三模板行为+双通道事件、敌人四态距离泵入（含滞回、路点回绕、Dead 终态）、Registry 最近目标与 `PickNearest`、Inventory 计数。

**② 架构校验（`TempLogs/arch_report.txt`，exit 0）**

```
asmdef 扫描数: 19（期望矩阵条目: 10，Tests 程序集豁免）
纯度扫描目录: Assets/ZFrame/Gameplay; Assets/Game/Domain
违规数: 0
结果: PASS
```

`Domain/Gameplay 纯度扫描零违规`；另手工 grep `Game/Domain` + `ZFrame/Gameplay` 全部 `.cs`：`InputSystem`/`UnityEngine`/`Game.View` 仅出现在**注释**中（InputCommandEvents.cs:7-14、Vec2.cs:8），代码零引用。

**③ 场景与 BuildSettings**

```
[GameSceneSetup] created Assets/Scenes/SCN_Game.unity; build settings: [0] SCN_Boot.unity, [1] SCN_Empty.unity, [2] SCN_Game.unity
```

场景内含 Main Camera / Directional Light / Ground / Player(CharacterController+PlayerMotor+AimOrigin) / Door / Pickup / NPC / Enemy / InteractionSystem(Scanner+Hud)；序列化字段抽查：门 `_kind:0 _id:door-main`、拾取 `_kind:1 _amount:3`、NPC `_kind:2` 三行台词、敌人 `_waypoints` 三点 + `_player`/`_renderer` 已连。

**④ 规范**：全部新增文本 UTF-8 无 BOM + LF（逐文件校验）；`.meta` 由 Unity 生成、guid 未手改。

### 5. 已知风险与偏离（请验收方裁定）

1. **射线原点偏离简报字面**：简报写「相机 `Physics.Raycast` 2m」，但第三人称相机距玩家 4m+，纯相机 2m 射线在本切片**永远打不到道具**。实现为：`_rayOrigin` 为空时回退相机（严格符合简报），场景里把 `_rayOrigin` 指向玩家胸口 `AimOrigin`、方向仍取**相机前向**（即「玩家看向哪」）。若验收坚持相机原点，把该字段置空即可，无需改代码。
2. **HUD 字体**：世界空间 `TextMesh` + `LegacyRuntime.ttf`（沿用 `UISetup` 既有做法），该字体对中文可能缺字形，实机若出现方块需换字体——简报允许「M3 框架外简版」，未接 UGUI。
3. **`PlayerMotor` 属简报外新增**（理由见 §2）。若视为越界，可删除该文件与场景中对应组件，不影响其余交付。
4. **`InteractionWorld.Shared` 是进程级静态**，跨场景不自动清理；A-M4-02 做场景流时需在场景卸载钩子调 `ResetShared()`。
5. **`EnemyView` 用 `_renderer.material`** 会实例化材质（运行时正常；编辑器下会留下材质实例，不进资产）。
6. **`GameSceneSetup` 是「重建」而非「增量」**：重跑会覆盖对 SCN_Game 的手工修改（换来的是产物与脚本永远一致）。
7. **未做 PlayMode 冒烟**（简报把冒烟放在 A-M4-02），因此「按 E 开门」的端到端手感未经真机验证；本报告只担保 EditMode + 静态装配正确。
8. Unity 批处理日志有 `[Licensing::Client] Error: HandshakeResponse` 噪音，为环境预存在问题，不影响 exit code 与测试结果。


## 验收清单（主控写）

1. EditMode 全绿（用例数）；Domain 纯度扫描零违规（校验器自跑）
2. FSM：Enter/Exit 全序+非法转换拒绝用例；状态切换统一走 ChangeState（无绕过——行号）
3. 三模板行为+事件用例；敌人四态流转距离断言用例
4. View 适配器单向依赖（Domain 不引 View——grep）
5. 输入桥接：InputSystem→EventBus 事件行号；Domain 零 InputSystem 引用
6. SCN_Game 生成+BuildSettings 核对
7. 文件规范+施工报告完整

## 验收结论（主控代验收，2026-10-25）

> **独立性说明**：施工=子代理（含接手中断前身的审计），验收=主控，满足"不同代理"。子代理额度于验收窗口耗尽，故由主控按 ADR-006 先例中立亲跑取证；**遗留：配额恢复后可派独立代理复核**。

| # | 项 | 判定 | 证据（主控亲查/亲跑） |
|---|---|---|---|
| 1 | EditMode 全绿 + Domain 纯度 | **PASS** | `m4_01_editmode.xml`：`total=466 passed=466 failed=0`（基线 372 只增未减，+94）；主控自跑架构校验器（Git Bash，`takeover_arch2.log`）：exit 0，报告 @ 20:13 `asmdef 19 / 违规 0 / PASS` |
| 2 | FSM 全序 + 非法转换拒绝 + 无绕过 | **PASS** | `FsmTests.cs` 20 例、`EnemyFsmTests.cs` 21 例；`Fsm.cs` 中 `_current` 仅 L139（初始）与 L192（ChangeState 内）两处赋值——**无绕过路径** |
| 3 | 三模板 + 敌人四态 + Registry + Inventory | **PASS** | Door 8 / Pickup 6 / Dialog 8 / EnemyFsm 21 / InteractionRegistry 17 / Inventory 6 用例，均真断言 |
| 4 | Domain 不引 View（单向依赖） | **PASS** | `grep -rln "Game.View\|ZFrame.View" Assets/Game/Domain/` → 0 命中 |
| 5 | 输入桥接 + Domain 零 InputSystem | **PASS** | `grep -rln "InputSystem" Assets/Game/Domain/` → 0 命中；桥接在 View 层 `PlayerInputBridge`（并入 `InputRuntime`，行 79/90/101） |
| 6 | SCN_Game + BuildSettings | **PASS** | `EditorBuildSettings.asset` 顺序实测 `[SCN_Boot, SCN_Empty, SCN_Game]`，第三位 ✓ |
| 7 | 文件规范 + 报告四节 | **PASS** | 四节齐（施工节含对前身代码的审计与修复记录）；新 Tests asmdef 按 Tests 豁免未登记矩阵（与校验器实测一致） |

### 总结论

**PASS**（7/7）

### 遗留 / 施工偏离（已在施工报告披露，验收复核认可）

1. 前身代理遗留的 `EnemyFsm` 引用 5 个不存在类型（编译必断）——续做代理已补 5 个状态文件并加 Idle 计时宿主；此为**接手审计成果**，如实入档
2. 2m 射线在第三人称下打不到道具 → `_rayOrigin` 回退相机（指向玩家胸口、方向取相机前向）——简报外最小偏离，报告 §5 已请裁定，验收认可
3. `PlayerMotor` 为简报外最小补充（Move 命令的消费者）；HUD 用 TextMesh 简版（中文字体风险留 M4-02）
4. 交互全链的真实按键捕获 EditMode 覆盖不到 → PlayMode 冒烟归 A-M4-02
