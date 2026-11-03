# TASK-A-R9-12 —— CommandSystem 命令模式（原始清单最后一块拼图）

状态: doing | 派发: 2026-11-04 | 施工: general-purpose 施工子代理（2026-11-04，自测全绿） | 验收: general-purpose 验收子代理（2026-11-04，**PASS**） | commit: （待填）

## 任务简报（主控写）

### 背景

需求达成对照（Docs/需求达成对照.md）发现原始清单 17 项中唯一缺口：**命令模式**（用户原话："将操作封装为对象，便于实现撤销/重做、操作回放或网络同步中的指令传输"）。全仓 grep `ICommand|CommandManager` 零实现。

**产品切入点（真实需求，非为做而做）**：设置页的撤销目前是"快照整体恢复"（A-M1-03 语义）；交互/拾取等玩法操作没有撤销。本任务交付通用 CommandSystem，并接一个**真实消费场景**：**拾取撤销**（玩家误拾物品可 Ctrl+Z / 屏上按钮撤销——拾取改背包+场景，正是命令模式的教科书场景）。

**设计要点（课程 M15-75 教法为基，扩展）**：
1. `ZFrame/Gameplay/Commands/`（纯 C# 零 UnityEngine，架构校验器把守）：
   - `ICommand { string Label { get; } void Execute(); void Undo(); }`（Execute/Undo 幂等可重入约定写 XML doc）
   - `CommandManager`：执行栈/撤销栈分离（容量上限滚动窗口——M15-75 坑点：栈无限增长）、`Execute(cmd)`（执行即入栈）、`Undo()`/`Redo()`（Redo 需撤销栈弹出重执行——经典双栈语义）、`CanUndo/CanRedo`、清空 Clear（场景切换）
   - 事件 `CommandExecuted/Undone/Redone{label}`（EventBus，供 HUD 显示"撤销：拾取木桶"）
   - 与服务定位器集成：`Services.Register<ICommandManager>`
2. **玩法接线**（Game/Domain/Interaction 最小扩展）：`PickupEntity.Interact` 产出 `PickupCommand`（封装：背包移除+实体隐藏；Undo=恢复）——Inventory/实体状态的 Undo 必须真实回滚（不是假撤销）；GameplayFxBindings 的火花不撤销（视觉不做时间旅行，语义写清）
3. 触发：PC 端 Ctrl+Z（Input System Player map 或 UI 快捷键，最小实现）+ 设置页"游戏性"无新增（就地交互）
4. 单测（EditMode）：执行/撤销/重做全序、双栈语义（undo 后新 execute 清 redo 栈——经典语义）、容量滚动、事件载荷、Pickup 命令真实回滚（Inventory+实体状态）、Clear
5. 自测：EditMode 全绿（基线 811 只增不减）+ Validate exit 0（Domain 纯度）

### 约束

- 白名单：`Assets/ZFrame/Gameplay/**`、`Assets/Game/**`、`Tests/**`、本日志；不动既有 SettingsModel 撤销语义（那是快照恢复，与命令栈并存，报告说明两者关系）
- Unity 批处理纪律；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0

## 施工报告（general-purpose 施工子代理，2026-11-04）

### ① 实现说明（对照简报逐条）

**1. 纯 C# 命令核 `Assets/ZFrame/Gameplay/Commands/`（4 文件，零 UnityEngine，架构校验器把守）**

- `ICommand.cs`：`string Label { get; } void Execute(); void Undo();`。幂等/可重入约定写入 XML doc（contract 三条：Execute/Undo 必须幂等可重入、Label 全生命周期稳定、抛异常即 fail-fast）。
- `CommandManager.cs : ICommandManager`：
  - **双栈**：`_undoStack`（执行历史）与 `_redoStack`（重做链）分离；`Execute` = 先跑命令再入栈；`Undo` 弹出→撤销→压入 redo 栈（CommandManager.cs:111-121）；`Redo` 弹出→重执行→压回 undo 栈（:135-146）。
  - **双栈经典语义（简报 V1 锚点）**：任何新 `Execute` 先 `_redoStack.Clear()`——undo 后再执行新命令，被放弃的时间线不可重做（CommandManager.cs:93，注释"双栈经典语义：新的执行断开重做链"；用例 `Execute_AfterUndo_ClearsTheRedoStack_ClassicDoubleStackTrap`，CommandManagerTests.cs:125，断言 CanRedo=false/Redo()=false/被弃命令零重放）。
  - **容量滚动（M15-75 坑点）**：`CommandManager(int capacity = 64)`，`TrimToCapacity()` 溢出时 `RemoveAt(0)` 丢**最旧**且**不回滚**（已生效状态保持，只是退出撤销窗口，CommandManager.cs:173-180）；容量滚动用例断言 c1/c2 的 `UndoCount==0`（丢了≠被撤销）。RedoDepth 天然 ≤ Capacity（每条 redo 都来自 undo 栈），无需单独限容。
  - **事件**：每次栈变迁经 `EventBus.Fire` 发 `CommandExecuted/CommandUndone/CommandRedone`（各只带 `Label`，CommandEvents.cs），供 HUD 显示"撤销：拾取 gold×3"；`Clear()` 两栈清空且**不发**任何命令事件（场景切换语义）。
  - **fail-fast 细节**：Execute 抛异常的命令不入栈；Undo 抛异常的命令**两栈皆离**（不半记录、不假撤销，用例 `ThrowingUndo_DropsTheCommandFromBothStacks` 锁定）；重入（命令回调/栈事件回调里再调 Execute/Undo/Redo）抛 `InvalidOperationException`（EnterGuard，:159，用例锁定嵌套调用零副作用）。
- `ICommandManager.cs`：Services 注册面向的接口（CanUndo/CanRedo/UndoDepth/RedoDepth/Execute/Undo/Redo/Clear），语义契约写 XML doc。

**2. 玩法接线（Game/Domain/Interaction 最小扩展，V2 真实回滚）**

- `Inventory.cs` 新增 `TryRemove(itemId, amount)`（:41-74）：回滚原语——覆盖不足返回 false 零变异（杜绝负数/幻影栈）；**减到零删 key**，使 undo 能复原拾取前的精确背包形态（含 `DistinctCount`）。
- `PickupCommand.cs : ICommand`：`Execute → PickupEntity.ApplyPickup`，`Undo → PickupEntity.RevertPickup`；`Label = "拾取 {itemId}×{amount}"`。
- `PickupEntity.cs`：`Interact` 产出 `PickupCommand` 并经 `Services.TryGet<ICommandManager>` 路由（:102-117）——注册了管理器则入栈可撤销；**没注册则命令照跑不入栈**（抽象测试/无头场景保持 R9-12 前行为，既有 PickupEntityTests 零改动全绿即证）。`ApplyPickup`（:131-147，幂等：已拾取即 no-op）/`RevertPickup`（:153-176，**真实回滚**：`TryRemove` 把量从背包拿走 + `IsPickedUp=false` 恢复提示/CanInteract + 发 `ItemReturned`；背包被外来系统掏空时**抛异常拒绝假撤销**）。新事件 `ItemReturned{Actor,ItemId,Amount,TotalCount}`（ItemReturned.cs，镜像 ItemPickedUp 的"实体拥有通知"约定）。
- **火花不撤销（语义写清）**：GameplayFxBindings.cs 类文档新增一条（"Sparks are NOT undoable"）——绑定层**刻意不订阅** `ItemReturned`，原火花已花掉不回收（视觉不做时间旅行）；redo 重发 `ItemPickedUp` 会再爆一次火花，语义是"拾取重新发生了一次"（新视觉事件，非恢复）。

**3. 触发（最小实现，全部在白名单内）**

- `GameBootstrap.cs:126` 新增 boot 步 `RunStep("command manager", EnsureCommandManager)`（:237-246，`TryGet` 守卫防 `GameRoot.Shutdown` 清场后重开机重复注册）；离场钩子 `OnBeforeSceneUnload` 在 `InteractionWorld.ResetShared()` 旁 `commands.Clear()`（:535-539，旧场景实体的命令不得跨场景撤销）。
- `GameHud.cs`：屏上按钮"撤销 (Ctrl+Z)"（`CreateUndoButton` :337，HUD canvas 右上角，UGUI 纯色零资产，桌面/触屏同路）+ PC 端 `Update()` 轮询 Ctrl+Z（:181，Esc 轮询先例；**未走 Input System Player map**——该改 ZFrame/View 越白名单，简报允许"或 UI 快捷键"）+ `UndoHint` 提示行（订阅三命令事件显示"执行/撤销/重做：{label}"，空栈显示"没有可撤销的操作"）+ 订阅 `ItemReturned` 回填拾取计数（撤销后计数不残留）。按钮 onClick 与 Ctrl+Z 同走 `TryUndo()`（:193）。
- 设置页"游戏性"零新增（简报允许就地交互）；SettingsModel 零改动。

**4. 与 SettingsModel 快照撤销的关系（V4，并存不冲突）**

| 维度 | SettingsModel 快照撤销（A-M1-03） | CommandSystem 命令栈（本任务） |
|---|---|---|
| 作用对象 | 设置面板的**配置值**（音量/画质等 UI 状态） | **玩法世界状态**（背包、实体） |
| 语义 | `RevertToSnapshot`：整包快照恢复，恢复后模型回到打开面板时刻 | 逐命令双栈：栈序 LIFO 回滚/重放，新执行断开 redo |
| 入口 | 面板"撤销更改"按钮（UI 内闭环，不入命令栈） | Ctrl+Z / HUD 撤销按钮（游戏内） |
| 生命周期 | 面板打开期间持有快照，关面板即弃 | boot 注册常驻，场景切换 `Clear` |

两者**零交集零互调**：SettingsModel 恢复不发 `ICommand` 也不订阅命令事件；命令栈不触碰任何设置键。若未来想把设置撤销改造成命令式，那是 SettingsModel 的**内部重构**，`ICommandManager` 接口可直接复用——本任务刻意不迁移（简报约束"不动既有 SettingsModel 撤销语义"）。

**5. 实现中的三个裁决（施工自证，供验收复核）**

- **Undo 抛异常 = 命令两栈皆离**（不压回 redo）：备选"压回 redo 保序"会在重做链里留一条无法撤销的命令，更糟；fail-fast + 丢弃 + 用例锁定是诚实语义。
- **重入守卫**：命令回调里再 Execute 会交错时间线毁掉双栈不变量；6 行守卫 + 用例，复合操作走"一个 ICommand 自己驱动子命令"的正当路径（不被禁）。
- **RevertPickup 抛异常而非静默 false**：与项目 fail-fast 哲学（Services 缺服务即抛）一致；背包被掏空属外来系统违约，静默吞掉会制造假绿。

### ② 变更清单（全在白名单内）

| 文件 | 变更 |
|---|---|
| `Assets/ZFrame/Gameplay/Commands/ICommand.cs` | 新增（接口 + 幂等契约 XML doc） |
| `Assets/ZFrame/Gameplay/Commands/CommandEvents.cs` | 新增（三个 EventBus 事件结构） |
| `Assets/ZFrame/Gameplay/Commands/ICommandManager.cs` | 新增（Services 面向的接口） |
| `Assets/ZFrame/Gameplay/Commands/CommandManager.cs` | 新增（双栈+容量滚动+事件+重入守卫） |
| `Assets/Game/Domain/Interaction/Inventory.cs` | +`TryRemove`（+38 行） |
| `Assets/Game/Domain/Interaction/PickupCommand.cs` | 新增 |
| `Assets/Game/Domain/Interaction/ItemReturned.cs` | 新增（回滚通知事件） |
| `Assets/Game/Domain/Interaction/PickupEntity.cs` | Interact 改走命令栈；+ApplyPickup/RevertPickup/+ItemReturned 事件（+67 行） |
| `Assets/Game/Bootstrap/GameBootstrap.cs` | boot 注册 ICommandManager（+守卫）；离场 Clear（+30 行） |
| `Assets/Game/View/UI/GameHud.cs` | 撤销按钮/Ctrl+Z/UndoHint/ItemReturned 计数回填（+167 行） |
| `Assets/Game/View/Effects/GameplayFxBindings.cs` | 类文档 +"火花不撤销"语义（+7 行，零行为变更） |
| `Assets/Game/Tests/Domain/CommandManagerTests.cs` | 新增 11 用例 |
| `Assets/Game/Tests/Domain/PickupCommandTests.cs` | 新增 8 用例 |
| `Assets/Game/Tests/Domain/InventoryTests.cs` | +4 TryRemove 用例 |
| `Assets/Game/Tests/View/GameHudUndoTests.cs` | 新增 4 用例（按钮 onClick 驱动，EditMode 无法合成按键——A-AND-04 先例） |
| `Assets/Game/Tests/View/GameHudLeakTests.cs` | HUD canvas 基线 2→4（两 caption+提示行+按钮根），leak 断言扩到 4 对象 |

新增 .cs 均核验 UTF-8 无 BOM（首字节即正文）；换行随仓库工作树现状（core.autocrlf=true 下全仓 .cs 磁盘态均为 CRLF，`.gitattributes eol=lf` 提交时归一，与既有文件一致）。未 git commit；未触碰 Native/**、ZFrame/View/**、SettingsModel 与并行任务的未跟踪文件。

### ③ 自测数字

| 项 | 结果 |
|---|---|
| EditMode 全量（第 3 次运行） | **852/852 PASS，failed=0，exit 0**，证据 `TempLogs/runtests_editmode.xml`（基线 811 → 852 只增不减：本任务 +27，并行任务新测试 +14） |
| 本任务 27 个新用例逐一核对 results.xml | 27/27 `Passed`（含 V1 双栈语义用例、V2 真实回滚用例） |
| 首次运行 3 红 | 均为施工用例自身断言写错（断错计数器/断错守卫触发点/误期 ThrowingCommand 记日志），产品行为与设计一致——已修用例复跑全绿（过程留痕：3 次批处理运行记录在 TestReport.txt 历史） |
| 架构校验器 ValidateFromCommandLine | **exit 0**（`TempLogs/arch_report.txt`：asmdef 25 个、纯度扫描 Assets/ZFrame/Gameplay + Assets/Game/Domain、**违规数 0**） |
| 命名校验器（顺手跑） | exit 0（`TempLogs/name_report.txt`） |
| Unity 批处理纪律 | 全程经 `Tools/run_tests.ps1`（单实例检查/删 UnityLockfile/测试**无 -quit**/轮询 results.xml/5 分钟 xml 宽限+taskkill 兜底），三次运行均干净退出 |

### ④ 遗留

1. **Ctrl+Z 无 EditMode 用例**：`Input.GetKey` 无法在批处理下合成（A-AND-04 键盘侧同一限制）；按钮路径（同一 `TryUndo`）已由 GameHudUndoTests 锁定，快捷键仅多一层 `Input.GetKeyDown` 判断。真机/桌面上按一次即可人工复核。
2. **暂停态可撤销**：GameHud.Update 不受 timeScale=0 影响（PausePanel 同理），暂停面板打开时 Ctrl+Z 仍会撤销拾取。语义无害（撤销的是已发生的世界状态），最小实现未加暂停门控——若验收认为需要，一行 `IsPaused` 检查即可补。
3. **redo 后火花重爆**：redo 重发 `ItemPickedUp` → GameplayFxBindings 再爆一次火花。已按"拾取重新发生"语义写进文档（①-2）；若要求 redo 不出火花需 Domain 层加"重放标记"，超出本任务最小实现。
4. 验收清单 V1-V3 要求验收代理**亲自复跑**校验器与双栈/回滚用例定位行号（施工自证不替代验收，R10-06 先例）；变异点建议：`CommandManager.cs:93` 删 `_redoStack.Clear()` → 双栈用例恰红；`PickupEntity.cs:160` 把 `TryRemove` 换成无条件成功 → 回滚用例红。
5. 撤销按钮当前不随 `CanUndo` 置灰（恒可点，空栈回退到提示文案）——HUD 极简取舍，非缺陷。


## 验收清单（主控写）

- [ ] V1 EditMode 全绿（数字写入结论）；双栈语义用例真实（undo 后 execute 清 redo——经典陷阱，行号）
- [ ] V2 Pickup 撤销真实回滚用例（Inventory+实体状态，非假撤销——行号）
- [ ] V3 Domain 纯度：校验器 exit 0（自跑）；纯核零 UnityEngine（行号）
- [ ] V4 与 SettingsModel 快照撤销的关系说明（并存不冲突）
- [ ] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

**验收: general-purpose 验收子代理（2026-11-04）——PASS**（V1-V5 全过；两个变异点均亲自复做命中、已字节级恢复；未 git commit）

- **V1 PASS**——亲跑 EditMode 两轮：施工态 `Tools/run_tests.ps1 -Segments editmode` → exit 0、**852/852**、failed=0（`TempLogs/runtests_editmode.xml`，"results.xml written by this run"）；变异恢复后再确认 → 852/852 Passed、UNITY_EXIT_CODE=0（`TempLogs/ar912_final_editmode.xml`）。**27 个新用例逐名核对 results.xml 全部 `Passed`**（CommandManagerTests 11 + PickupCommandTests 8 + InventoryTests.TryRemove 4 + GameHudUndoTests 4）。
- **V2 PASS**——双栈语义用例真实：`CommandManager.cs:93` `_redoStack.Clear()` 为锚；`CommandManagerTests.cs:125-147` `Execute_AfterUndo_ClearsTheRedoStack_ClassicDoubleStackTrap` 断言 CanRedo=false / RedoDepth=0 / Redo()=false / 被弃命令 b.ExecuteCount==1（零重放）；容量滚动 `:150-184`（c1/c2 UndoCount==0，丢≠撤销）；重入守卫 `:238-252`（嵌套调用零副作用）；Undo 抛异常两栈皆离 `:255-274`。**变异复做**：删除 CommandManager.cs:93 的 Clear → 恰 2 用例红（`Execute_AfterUndo_ClearsTheRedoStack_ClassicDoubleStackTrap`、`PickupCommandTests.Undo_ThenRepick_StartsNewTimeline_NoRedo`，断言"Expected: False But was: True"）→ 恢复字节级一致（cmp 通过）→ 复跑全绿。
- **V3 PASS**——真实回滚审查：`PickupEntity.cs:160` 经 `Inventory.TryRemove`（`Inventory.cs:44-71`：不足返回 false 零变异、减到零删 key 复原 DistinctCount）回滚，`:167` 恢复 IsPickedUp/提示语，`:162-165` 背包被外来掏空**抛异常拒绝假撤销**（用例 `PickupCommandTests.cs:192-208`）。**变异复做**：让 RevertPickup 跳过 Inventory 恢复（TryRemove 分支短路）→ PickupCommandTests **5/8 红**（"REAL rollback… Expected: 0 But was: 3"、"expected InvalidOperationException But was: null"、ItemReturned 载荷/多物品逆序/重拾时间线）→ 恢复字节级一致（cmp 通过）→ 复跑全绿。
- **V4 PASS**——自跑 `ArchitectureValidator.ValidateFromCommandLine` → **exit 0**（`TempLogs/arch_report.txt` @01:19:47：asmdef 25、纯度扫描 Assets/ZFrame/Gameplay; Assets/Game/Domain、违规数 0）；纯核零 UnityEngine：`grep -rn UnityEngine Assets/ZFrame/Gameplay/Commands/` **零命中**（CommandManager.cs 仅 using System/System.Collections.Generic/ZFrame.Core；ZFrame.Gameplay.asmdef references 仅 ZFrame.Core）；**SettingsModel 关系评审**：并存不冲突成立——`Assets/ZFrame/View/Settings/SettingsModel.cs:109/119` 的 TakeSnapshot/RevertToSnapshot 是面板内整包快照恢复（配置值、UI 生命周期），与命令栈（玩法世界状态、逐命令双栈 LIFO、Ctrl+Z/HUD 入口、场景切换 Clear）四维分离；双向 grep 零互调（SettingsModel 无 ICommand/CommandManager 引用，命令核无 Settings 引用），施工报告的关系说明准确。
- **V5 PASS**——新增 9 个 .cs 首三字节均正文（无 BOM）；磁盘态 CRLF 与既有 tracked 文件一致（如 ItemPickedUp.cs 48/48 CRLF，施工报告已如实声明 .gitattributes 提交归一）；新文件 .meta 全部齐备（Commands/ 4+4、Commands.meta、3 个测试 meta）；HUD 漏检基线 2→4 同步扩断言（GameHudLeakTests）；施工日志四节齐全（简报/施工报告①②③④/验收清单/本结论），遗留 4+1 条均为诚实披露（Ctrl+Z 无 EditMode 用例、暂停态可撤销、redo 火花重爆、按钮不置灰）。

**过程记录**：① 验收期间与并行任务（B-R9-16）共享 Unity 单实例，3 次被单实例纪律让路后抢到空闲窗口完成收尾运行——过程性事项，不涉本任务代码。② 附带观察（非缺陷）：`CommandManager.Clear()` 不经 EnterGuard 重入守卫（仅 Execute/Undo/Redo 有守卫），语义上 Clear 无回调窗口、风险极低，留作后续审计观察项。③ 两处变异文件经 cmp 字节级验证恢复原状，工作树除本日志外无验收痕迹。
