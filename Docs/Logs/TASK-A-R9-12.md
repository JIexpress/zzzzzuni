# TASK-A-R9-12 —— CommandSystem 命令模式（原始清单最后一块拼图）

状态: doing | 派发: 2026-11-04 | 施工: （待填） | 验收: （待填） | commit: （待填）

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

## 施工报告（施工代理写）

（待填）

## 验收清单（主控写）

- [ ] V1 EditMode 全绿（数字写入结论）；双栈语义用例真实（undo 后 execute 清 redo——经典陷阱，行号）
- [ ] V2 Pickup 撤销真实回滚用例（Inventory+实体状态，非假撤销——行号）
- [ ] V3 Domain 纯度：校验器 exit 0（自跑）；纯核零 UnityEngine（行号）
- [ ] V4 与 SettingsModel 快照撤销的关系说明（并存不冲突）
- [ ] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

（待填）
