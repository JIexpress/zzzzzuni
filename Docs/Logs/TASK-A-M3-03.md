# TASK-A-M3-03 输入重绑定（Input System 包 + InputBlocker 接入 + 键位 UI）

状态: done | 派发: 2026-10-25 | 施工: 前身半成品 + 接管续做 | 验收: 独立验收代理（PASS 7/7） | commit: 见 TASKS.md

## 任务简报（主控写）

**目标**：M3 收尾——Input System 包接入：动作映射资产、运行时按键重绑定（冲突检测+恢复默认）、设置界面"操作"页签从占位变实接、InputBlocker 与 UI 输入协同（Esc 开关设置面板真正走 Input）。

**设计要点**：
1. 包：manifest.json 加 `"com.unity.inputsystem": "1.7.0"`（官方包；注意 ProjectSettings activeInputHandler 需设为 InputSystem 或 Both——脚本改 ProjectSettings.asset 字段 `activeInputHandler: 2`（Both），报告说明）
2. `Assets/ZFrame/View/Input/`：
   - `InputActions.inputactions` 资产（Setup 脚本生成 JSON 或手写入库）：ActionMap "Player"（Move WASD/LeftStick、Interact E/ButtonSouth、Pause Esc/Start）+"UI"（Navigate/Submit/Cancel/Point/Click——UI 用默认）
   - 生成的 C# 类（`-executeMethod` 或编辑器生成，入库）
   - `RebindService`：
     - `Rebind(actionKey, onComplete(onConflict|done))`：`PerformInteractiveRebinding`，排除鼠标（Move 保留键鼠两类可选）
     - **冲突检测**：重绑完成后扫同 ActionMap 全 binding，同路径+同布局冲突→自动回退并回调 conflict
     - `ResetAll()`（恢复 JSON 资产默认）；`GetBindingDisplay(actionKey)`（当前键位显示串）
     - 持久化：`InputSystem.SaveBindingOverridesAsJson/LoadFromJson`→`Application.persistentDataPath/input_bindings.json`（根目录可注入，同先例）
   - InputBlocker 接入：InputBlocker.Active 时 `Player` ActionMap 禁用（UI map 常开）——真正接通 M2-01 留的接入点
   - Esc 语义：设置面板打开时 Cancel/Escape→Presenter 已有逻辑（A-M3-02 Esc 轮询改事件驱动或保留，报告说明）
3. 设置界面"操作"页签实接（改 A-M3-02 的占位）：每动作一行（显示名+当前键位按钮+恢复默认按钮）；点击键位按钮→进入重绑定等待态（按钮显示"按任意键…"）→完成/冲突反馈（Toast 文本行即可）；页面底部"全部恢复默认"
4. 单测（追加）：冲突检测纯逻辑（给定 binding 表+新路径→判冲突，抽出可测函数）；持久化 JSON roundtrip（注入目录）；InputBlocker 与 ActionMap 开关联动（EditMode 可测 enabled 状态）；GetBindingDisplay 格式
5. 自测：EditMode 全绿 + ValidateFromCommandLine exit 0（Unity 纪律；装包后全工程编译证明零破坏）

**约束**：可动 `UnityProject/Packages/manifest.json`（加包）、`UnityProject/ProjectSettings/`（activeInputHandler）、`UnityProject/Assets/ZFrame/View/`、`UnityProject/Assets/Input/`（或 Settings 资产目录，报告说明）、`UnityProject/Assets/ZFrame/Tests/`、本任务日志；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0。

**交付物**：包接入+动作资产+RebindService+操作页签实接+InputBlocker 接通+单测全绿+施工报告四节。

## 施工报告（施工代理写）

**施工时间**：2026-10-25 18:20（接管续做，基于前身半成品）｜**自测**：EditMode 361/361 全绿 + 架构校验 exit 0

### 1. 实现摘要

| 需求 | 实现 | 关键位置 |
|---|---|---|
| 冲突检测（纯逻辑） | `BindingConflictDetector` 拆两个 partial 文件：`BindingConflictDetector.cs` **零 UnityEngine 引用**（纯静态判定），`BindingConflictDetector.CaptureMap.cs` 只做 `InputActionMap → BindingSnapshot[]` 投影（唯一接触引擎的一半） | `FindConflict` 44 行 / `CaptureMap` 24 行 |
| 冲突规则 | ①同 map 内**有效路径**（`overridePath ?? path`）相等即冲突；②composite 头（`IsComposite`）不参与；③composite 部件参与；④排除自身（actionKey+bindingIndex）；⑤无路径的空槽不参与；⑥顺序稳定（首命中胜出，文案可测） | 类 XML doc 列了 5 条契约 |
| 重绑定 | `RebindService.Rebind`：`PerformInteractiveRebinding(bindingIndex)` + `WithCancelingThrough("<Keyboard>/escape")`（Esc→Cancelled）+ `WithControlsExcluding("<Mouse>")`（排除鼠标）；完成后跑冲突检测 → 冲突则 `RemoveBindingOverride` 回滚并回调 `Conflict`+冲突快照，无冲突则 `Save()` 后回调 `Done`；`!Application.isPlaying` 或已有重绑定 → 立即回调 `Failed`（不抛异常）；操作对象统一 `Dispose`（回调前释放，允许回调里再起重绑定） | `Rebind` 155/179 行、`CompleteCaptured` 311 行、`Complete` 345 行 |
| 持久化 | `SaveBindingOverridesAsJson()` → `{root}/input_bindings.json`（UTF-8 无 BOM）；启动 `Load()` 调 `LoadBindingOverridesFromJson`；root 可注入（默认 `persistentDataPath`）；文件缺失/空/损坏/无 `bindings` 键均静默跳过（幂等） | `Load` 87 行、`Save` 134 行 |
| 恢复默认 | `ResetAll()` 清全 asset override + 持久化空态；`ResetBinding(actionKey)` 只清单动作 | 268/280 行 |
| 键位显示 | `GetBindingDisplay` → `InputControlPath.ToHumanReadableString(..., OmitDevice)`（"E" 而非 "Keyboard E"）；空/未知 → "未绑定" | 238/252 行 |
| InputBlocker 接通 | `InputBlocker` 新增 `Changed` 事件（Acquire/Release/Reset 后触发，计数逻辑本身不变、仍零引擎依赖）；新增 `PlayerInputGate`：`Active>0 → Player.Disable()`、归零 → `Player.Enable()`，**UI map 常开**，构造即应用当前态、可 `Sync()` 轮询、`Dispose` 退订 | `PlayerInputGate` 25/43 行 |
| 运行时装配 | 新增 `InputRuntime`（`AfterSceneLoad` 自启）：持有 `ZFrameControls` clone + `RebindService`（启动 Load）+ `PlayerInputGate`；`SettingsEntry.OpenSettings` 把 `InputRuntime.Bindings` 注入面板 | `InputRuntime.cs` |
| 操作页签实接 | `SettingType.Binding`（动作名+键位按钮+恢复默认按钮）与 `SettingType.BindingReset`（底部"全部恢复默认"）；`SettingDefinition.Binding/BindingReset` 工厂 + `ActionKey` 字段；`SettingsRowFactory` 两个分支 + `CreateStatusLine`（Toast 文本行）；`SettingsRegistry.BuildControlsPage` 换成 3 个真实动作行 + 1 个重置行；等待态文案"按任意键…"（按钮同时禁用防叠加）、完成/冲突/取消/不可用四类反馈；"全部恢复默认"后**原地刷新**各键位文案（不重建行，避免 EditMode 在点击回调里销毁按钮） | `BuildBindingRow` 380 行、`BuildBindingResetRow` 445 行、`CreateStatusLine` 491 行、`BuildControlsPage` 203 行、`SettingsPanelView.RefreshBindingCaptions` |
| 无值类型语义 | 新增 `SettingTypeExtensions.CarriesValue()`（Toggle/Slider/Enum=true），`SettingsModel` 的 6 处 `== SettingType.Action` 判断统一改为 `CarriesValue()`：键位行不进 `_values`、不校验、不写 settings.json | `SettingsModel` 49/69/123/145/207/250 行 |

### 2. 变更文件清单

**新增（View）**
- `UnityProject/Assets/ZFrame/View/Input/BindingConflictDetector.cs`（纯逻辑，无 UnityEngine）
- `UnityProject/Assets/ZFrame/View/Input/BindingConflictDetector.CaptureMap.cs`（引擎投影）
- `UnityProject/Assets/ZFrame/View/Input/RebindService.cs`
- `UnityProject/Assets/ZFrame/View/Input/IBindingSettings.cs`（UI↔输入层接缝，便于假件单测）
- `UnityProject/Assets/ZFrame/View/Input/PlayerInputGate.cs`
- `UnityProject/Assets/ZFrame/View/Input/InputRuntime.cs`

**新增（测试）**
- `UnityProject/Assets/ZFrame/Tests/Input/ZFrame.View.Input.Tests.asmdef`（references：ZFrame.Core / ZFrame.View / Unity.InputSystem + nunit，Editor-only）
- `BindingConflictDetectorTests.cs`(10) / `RebindServiceTests.cs`(18) / `PlayerInputGateTests.cs`(7) / `SettingsBindingRowTests.cs`(13)

**修改**
- `View/Scene/InputBlocker.cs`（+`Changed` 事件）
- `View/Settings/SettingType.cs`（+Binding/BindingReset + `CarriesValue`）
- `View/Settings/SettingDefinition.cs`（+`ActionKey` + 两个工厂）
- `View/Settings/SettingsModel.cs`（无值类型统一判定）
- `View/Settings/SettingsRowFactory.cs`（两个新分支 + 状态行 + `CreateButton` 辅助 + 命名常量）
- `View/Settings/SettingsRegistry.cs`（操作页真实定义）
- `View/Settings/SettingsPanelView.cs`（注入 `IBindingSettings`、状态行、原地刷新键位文案、`RowCount` 排除状态行）
- `View/Settings/SettingsPanelPresenter.cs`（无值类型注释明确）
- `View/Settings/SettingsPanelArgs.cs`（+`Bindings`，`Create` 加可选参）
- `View/Settings/SettingsEntry.cs`（注入 `InputRuntime.Bindings`）
- `Tests/Debug/SettingsRegistryTests.cs`（占位断言 → 真实绑定表断言）
- `Tests/Debug/SettingsPanelSmokeTests.cs`（操作页行数 1 → 4）

### 3. 与简报的偏差（如实报告）

1. **新 asmdef 未登记进 `ArchitectureValidator` 期望矩阵**——不是遗漏，是**不能登记**：校验器对名字以 `.Tests` 结尾/含 `.Tests.` 的程序集**整体豁免**（`ArchitectureValidator.cs:143-149`），而 `ArchitectureValidatorTests.ExpectedMatrix_MatchesActualAsmdefCount` 断言"矩阵条目数 == 非测试 asmdef 数"。给 `ZFrame.View.Input.Tests` 加矩阵条目会让该用例变红。已按现有豁免机制处理（与 `ZFrame.Debug.Tests`/`ZFrame.View.Tests` 同例），校验器实跑 PASS（扫描 18 个 asmdef、0 违规）。**简报此条与校验器现有实现冲突，请主控裁决是否要改校验器口径。**
2. **多加了 `SettingType.BindingReset`（第 5 种类型）**：简报只建议加 `Binding`，但"页面底部全部恢复默认"与"每动作一行"是两种不同控件（前者无动作名/无键位列）。用一个类型 + `ActionKey==null` 表示会变成语义重载，故拆成独立类型，`SettingsRowFactory` 一个分支、`SettingsModel` 走同一套"无值"判定。
3. **冲突判定只比路径、不比 layout**：简报设计要点写"同路径+同布局"，本次按接管提示"同有效路径即冲突"。理由：有效路径本身已含设备布局（`<Keyboard>/e` vs `<Gamepad>/buttonSouth` 不会相等），`BindingSnapshot` 也无 layout 字段（前身契约）。若后续需要区分 processors/interactions，再扩快照。
4. **"每动作一行"= 该动作的主 binding**：`Rebind(actionKey)` 取该动作**第一个非 composite 头**的 binding（Move → WASD 的 up/W；Interact/Pause → 键盘位）。一次交互只能改一个 binding，Move 的四向整体重绑定不在本任务范围（另提供 `Rebind(actionKey, bindingIndex, cb)` 供后续用）。
5. **设置面板 Esc 仍保留 A-M3-02 的 legacy `Input.GetKeyDown` 轮询**：简报允许"改事件驱动或保留，报告说明"。保留原因：本任务的输入接线目标是 Player/UI ActionMap 与 InputBlocker，Esc 关闭面板属于 UI 导航语义，改事件驱动需接 `InputSystemUIInputModule`（A-M4 迁移项）；保留现状可避免与 Input System 的 UI 模块半接不接的状态。

### 4. 自测结果（实跑取证）

**① EditMode 全量**（命令见任务简报；无 `-quit`）
- 结果：`testcasecount="361" result="Passed" total="361" passed="361" failed="0" inconclusive="0" skipped="0"`
- 基线 313 → 现 **361**（+48，无删改降级；仅把 1 条占位断言用例改写为真实断言，用例数 1:1 保持）
- 证据：`TempLogs/m3_03_editmode.xml`（361 个 `<test-case>`）、`TempLogs/m3_03_editmode.log`（0 条 `error CS`）
- 断言数说明：Unity Test Framework 的 XML 里 `asserts="0"` 是**适配器不填充**（历史任务同此现象），故以源码断言调用数为辅证：新增 4 个测试文件共 **146 处 `Assert.*`**（冲突检测 20 / 重绑定 52 / InputBlocker 26 / 操作页 UI 48）
- 覆盖对照：冲突检测纯函数 10 例（同路径/异路径/composite 头不参与/部件参与/排除自身/空表/空路径/首命中）、持久化 roundtrip 注入临时目录 5 例（保存→重载→override 生效、无 BOM、文件缺失/空/损坏不抛）、InputBlocker→ActionMap 联动 7 例、`GetBindingDisplay` 格式 3 例、操作页 UI 13 例

**② 架构校验器**
- 命令：`-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine`（带 `-quit`），进程退出码 **0**
- 报告：`TempLogs/arch_report.txt` → `asmdef 扫描数: 18（期望矩阵条目: 10，Tests 程序集豁免）｜违规数: 0｜结果: PASS`
- 证据日志：`TempLogs/m3_03_arch.log`

**③ 文件规范**：新增/改动 .cs 与 .asmdef 全部 UTF-8 **无 BOM**、LF（`file` 检查 ALL_LF）；`.meta` 由 Unity 导入自动生成（Input/ 目录 6 个 + Tests/Input/ 5 个，均已落盘）。

### 5. 已知风险与未做项

1. **真实按键捕获未在自动化里跑过**：`PerformInteractiveRebinding` 需要 play mode + 设备事件，EditMode 用例只覆盖到"守卫立即 Failed / 冲突判定 / 回滚 / 持久化 / UI 状态机"。真按键链路建议 A-M4-02 的 PlayMode 冒烟补一条（本任务不新增 PlayMode 套件，避免拖慢基线）。
2. **Move 只重绑 WASD 的单个部件**（见偏差 4）；四向/双设备整体重绑定未做。
3. **冲突时的持久化策略**：冲突分支**不写文件**（旧 override 回滚后文件仍是旧状态），因此"冲突后重启"看到的是旧键位——符合预期，但若后续引入"冲突自动换键"需重新设计。
4. **设置面板 UI 只测到行工厂层**：`SettingsPanelSmokeTests` 只断言操作页行数=4；真实 prefab 里点键位按钮的端到端路径未在 EditMode 跑（缺 `IBindingSettings` 注入的 prefab 级用例，因为 `UITestEnv` 在 Debug.Tests 程序集内不可跨程序集复用）。
5. **`InputRuntime` 无关闭路径**：静态单例随域重载回收，未提供 `Shutdown()`（当前无调用方需要）。
6. **`RebindService.Dispose` 时若有进行中的重绑定，回调不会触发**（服务销毁即放弃），调用方需自行兜底 UI 恢复——当前唯一调用方 `InputRuntime` 不做销毁。


## 验收清单（主控写）

1. 自跑 EditMode 全绿（用例数写入结论）——装包零破坏证明
2. 冲突检测纯函数真实断言用例；持久化 roundtrip（注入目录）用例
3. InputBlocker→ActionMap enable/disable 联动用例（行号）
4. 操作页签实接：重绑定等待态/冲突反馈/恢复默认按钮逻辑（代码行号）；设置面板 Esc 语义保持
5. activeInputHandler 设置核对（读 ProjectSettings）
6. ValidateFromCommandLine exit 0（自跑）
7. 文件规范+施工报告完整

## 验收结论（验收代理写）

**验收代理**：独立验收代理（与施工代理非同一实例）｜**验收时间**：2026-10-25 18:20–18:32｜**总结论**：**PASS**（7/7 通过；2 项受限/遗留已登记，未发现作弊或断言放宽）

### 逐项结论

**1. 自跑 EditMode 全绿 —— PASS**
实跑命令按简报原样（**无 `-quit`**），跑前确认无 Unity.exe 进程并删除 `UnityLockfile`，进程自行退出（77s，退出码 0，无僵死、未动用 taskkill）。
- `TempLogs/acc_m3_03.xml` 根节点：`testcasecount="361" result="Passed" total="361" passed="361" failed="0" inconclusive="0" skipped="0"`；文件内 361 个 `<test-case>`，`result="Failed"` 命中 0 条。
- `TempLogs/acc_m3_03.log`：`error CS` 命中 0 条（装包后全工程编译零破坏）。
- 基线 313 → **361**（+48），与新增四个 fixture 精确对齐：`BindingConflictDetectorTests` 10 + `RebindServiceTests` 18 + `PlayerInputGateTests` 7 + `SettingsBindingRowTests` 13 = 48；四个 fixture 均出现在 XML 的 `fullname` 列表中。

**2. 冲突检测纯函数是真断言 —— PASS**
`Assets/ZFrame/Tests/Input/BindingConflictDetectorTests.cs`：
- 同路径冲突 36-49（断言 `conflict.ActionKey=="Player/Interact"`、`BindingIndex==0`，非仅非空）
- 不同路径不冲突 53-59（`IsNull`）
- composite 头不参与 63-75（同路径 `IsNull` + `IsConflictRelevant(map[0])==false`）
- composite 部件参与 79-93（`ActionName=="Move"` + `IsCompositePart==true`）
- 排除自身 97-110（自身 `IsNull`；换 bindingIndex、换 action 均 `IsNotNull`，三向对照）
- 空表/空路径 137-142、144-151；空槽不参与 122-133；首命中稳定性 155-167
断言全部比对**期望值**，无恒真式；被测 `BindingConflictDetector.cs` 全文零 `UnityEngine` 引用（纯函数成立，引擎投影隔离在 `.CaptureMap.cs`）。

**3. 持久化 roundtrip（注入目录）真实 —— PASS**
`RebindServiceTests.cs`：
- 59-82 `Save_ThenLoad_RestoresTheOverride_OnAFreshAsset`：`ApplyOverride("<Keyboard>/f")` → `Save()` → `ZFrameControls.FromResources()` 新建资产 → `Load()` → 断言 `effectivePath=="<Keyboard>/f"` 且 `GetBindingDisplay` 为 `"F"`（真写盘→真重载→真生效）
- 96-103 `Load_MissingFile_IsSilentNoOp`：`DoesNotThrow` + `Load()==false` + 默认键位仍 `"<Keyboard>/e"`
- 105-114 空文件、116-125 损坏文件同样不抛；34/44-47 用 `Path.GetTempPath()+Guid` 注入并清理，不碰真实 `persistentDataPath`
- 写盘实现 `RebindService.cs:141-143` 用 `new UTF8Encoding(false)`；84-94 另断言文件无 BOM

**4. InputBlocker → ActionMap 联动 —— PASS**
`PlayerInputGateTests.cs`：49-66（`Acquire()` 后 `Player.enabled==false`、`UI.enabled==true`、`IsPlayerEnabled==false`；`Dispose()` 后 Player 恢复 true）、68-83（嵌套计数未归零不恢复）、38-47（空闲态 Player/UI 双开）、85-97（构造即应用当前阻塞态）。
实现：`PlayerInputGate.cs:25-30` 构造订阅 `InputBlocker.Changed` 并立即 `Sync()`；43-59 `Sync` 内 `UI.Enable()` 常开、Player 按 `Active` 开关；`InputBlocker.cs:32` 新增 `Changed` 事件，51-56/59-69 每次计数变更后触发（计数逻辑与零引擎依赖未变，diff 仅加事件与 `RaiseChanged`）。

**5. 操作页签实接 —— PASS**
- 占位已被取代：`SettingsRegistry.cs:203-216` 现为 3 个 `SettingDefinition.Binding`（`Player/Move`、`Player/Interact`、`Player/Pause`）+ 1 个 `BindingReset`（`controls.resetAll` "全部恢复默认"）；diff 显示原 `controls.rebind`「键位重绑定（即将支持）」Action 行已删除，全仓仅 `Tests/Debug/SettingsRowFactoryTests.cs:125` 还留该文案（无关的通用 Action 行用例）。
- 等待态：`SettingsRowFactory.cs:409-410` 点击后 `keyText.text=WaitingForInputText`（`"按任意键…"`，常量在 37 行）并禁用按钮；完成后 421-423 回填权威显示串 + 反馈。
- 冲突反馈：`SettingsRowFactory.cs:469-483` `DescribeOutcome` 四类文案（已绑定 / 键位冲突 + 点名占用动作 / 已取消 / 不可用），冲突分支回滚后显示旧键位（`RebindService.cs` 冲突时 `RemoveBindingOverride` 再回调）。
- 全部恢复默认：`SettingsRowFactory.cs:445-466` 调 `IBindingSettings.ResetAll()` 并抛 `onValueChanged`，`SettingsPanelView` 的 `RefreshBindingCaptions` 原地刷新各键位文案；反馈行由 `CreateStatusLine`（491-499）生成，`RowCount` 已排除该行。
- Esc 语义未破坏：`SettingsPanelView.cs:278-280` 仍轮询 `Input.GetKeyDown(KeyCode.Escape)` → `RequestClose()`（261 行），`SettingsPanelPresenter.cs:116-121` 仍是「回滚快照 → 关闭 → 从不保存」，diff 对该路径零改动；保留理由已在施工报告偏差 5 说明（简报允许"保留或事件驱动"）。

**6. activeInputHandler 核对 —— PASS**
`UnityProject/ProjectSettings/ProjectSettings.asset:664` → `activeInputHandler: 2`（Both）。另核 `Packages/manifest.json:3` → `"com.unity.inputsystem": "1.7.0"`。

**7. 架构校验器 exit 0 —— PASS（含口径说明）**
自跑（带 `-quit`）进程退出码 **0**（后台任务回传 `EXIT=0`）；`TempLogs/arch_report.txt`：`asmdef 扫描数: 18（期望矩阵条目: 10，Tests 程序集豁免）｜纯度扫描目录: Assets/ZFrame/Gameplay; Assets/Game/Domain｜违规数: 0｜结果: PASS`。
新程序集 `ZFrame.View.Input.Tests` **未**登记矩阵，但属既有豁免机制：`ArchitectureValidator.cs:143-149` 对 `.Tests` 结尾/含 `.Tests.` 的程序集整体豁免，且 `Tests/Debug/ArchitectureValidatorTests.cs:34` 断言「矩阵条目数 == 非测试 asmdef 数」——登记该测试程序集反而会让该用例变红。施工报告偏差 1 属实，本次判为合规豁免。**建议主控**收敛简报第 7 项口径（改为「新程序集需符合 Tests 豁免约定」，而非「必须登记矩阵」）。

### 防作弊 / 防退化核查

| 核查项 | 结论 | 证据 |
|---|---|---|
| 是否删改既有测试让其变绿 | **否**（无删除，仅 2 文件改动） | `git status` 无 `D` 记录；`git diff --stat` 仅 `SettingsPanelSmokeTests.cs` +1/-1、`SettingsRegistryTests.cs` +20/-7 |
| `SettingsPanelSmokeTests.cs` 改动性质 | 合理适配 | 操作页 `RowCount` 断言 1 → 4（含说明），与新页签真实行数一致 |
| `SettingsRegistryTests.cs` 改动性质 | **收紧，非放宽** | 原占位断言（`>=1`、`Contains("即将支持")`）→ 精确 4 行、精确类型序列 `Binding×3+BindingReset`、精确 `ActionKey` 序列、并断言 `Apply==null` 与 `DefaultValue==null` |
| 是否把会失败的断言改恒真 | **否** | 新测试 grep `IsTrue(true)`/`Assert.Pass`/`[Ignore]`/`[Explicit]` 均 0 命中（唯一 "Ignored" 是方法名） |
| 施工报告断言数是否夸大 | **否**（吻合） | 实测 `Assert.` 计数：冲突 20 / 重绑定 52 / Gate 26 / 操作页 48 = 146，与报告自述一致 |
| Gameplay / Game.Domain 纯度 | **未被污染** | 两目录 `using UnityEngine|UnityEngine.` 命中 0；`ZFrame.View.asmdef` 仅 View 层引 `Unity.InputSystem`，矩阵 `ArchitectureValidator.cs:36-40` 已登记为允许引用 |
| 文件编码 | **合规** | 新增 .cs/.asmdef 首字节均为 ASCII（无 BOM）；工作区 CRLF 系全仓 `core.autocrlf=true`（既有已提交文件同样 CRLF），`.gitattributes` `* text=auto eol=lf` 会在 blob 侧归一化为 LF |

### 遗留问题 / 受限项

1. **受限**：真实按键捕获链路（`PerformInteractiveRebinding` + 设备事件）在 EditMode 无法覆盖，本次仅验证到「守卫立即 Failed / 冲突判定 / 回滚 / 持久化 / UI 状态机」。建议 A-M4-02 的 PlayMode 冒烟补一条（与施工已知风险 1 一致）。
2. **遗留（P2）**：`Rebind(actionKey)` 只改该动作的**主 binding**——"移动"一行实际只重绑 WASD 的 W，四向与手柄左摇杆未覆盖（施工偏差 4 / 风险 2 已披露）。与简报"每动作一行"字面不冲突，但玩家视角仍是半成品，建议 A-M4 补四向/双设备整体重绑定。
3. **口径待裁决**：验收清单第 7 项"新 asmdef 登记矩阵"与校验器现有 Tests 豁免机制相冲突，本次按豁免判 PASS（见第 7 项）。
4. **观察**：`SettingsPanelView.RowCount` 改为 `childCount - (状态行 ? 1 : 0)`，语义与"状态行不是行"一致、SmokeTests 仍断言 4，未放宽；但该属性隐含"至多一个非行子物体"的假设，后续再加页脚需同步修改。
5. **未复验项**：施工报告称 `.meta` 已全部落盘——本次以「EditMode 全绿 + 资产可从 Resources 加载（`RebindServiceTests.SetUp` 断言非空）」间接证实，未逐文件核对 meta GUID 唯一性（非本任务验收口径）。

**总结论：PASS**
