# TASK-A-M3-04 移动键四向重绑定收尾（复合轴分行）

状态: done | 派发: 2026-10-25 | 施工: 施工代理 | 验收: 独立验收代理（PASS 6/6） | commit: 见 TASKS.md

**前置**：A-M3-03 完成（RebindService / SettingType.Binding / 操作页签已实接，验收 PASS 7/7，Unity 基线 361 用例）。

## 任务简报（主控写）

**背景**：A-M3-03 交付时，"操作"页签对 `Player/Move` 只重绑了**主绑定**（WASD 复合轴的 W 部件），A/S/D 与手柄 leftStick 不受影响。验收代理将此登记为遗留项。对玩家而言"改移动键只改了上"属可见缺陷，故单开本任务收尾。

**目标**：把"移动"从一行拆成**四个方向行**（移动·上/左/下/右），各自重绑 WASD 复合轴对应的部件；手柄 leftStick 保持资产默认（不提供重绑入口，UI 上注明）。冲突检测、持久化、恢复默认沿用 A-M3-03 已交付机制，不重写。

**设计要点**：
1. **不改 RebindService 的核心重绑定/冲突/持久化逻辑**——它已有 `Rebind(string actionKey, int bindingIndex, Action<RebindOutcome, BindingSnapshot>)` 重载，本任务只做"把正确的 bindingIndex 喂给它"
2. `SettingDefinition.Binding(...)` 增加**显式 bindingIndex** 参数（可选，默认 -1 = 沿用现有"主绑定"推断）；`SettingsRowFactory` 的 Binding 分支按传入索引取 `GetBindingDisplay`
3. `SettingsRegistry.BuildControlsPage()` 改为 6 个 Binding 行 + 1 个 BindingReset 行：
   - 移动·上 → `Player/Move` 的 W 部件索引
   - 移动·左 → A 部件索引
   - 移动·下 → S 部件索引
   - 移动·右 → D 部件索引
   - 交互 → `Player/Interact` 主绑定
   - 暂停 → `Player/Pause` 主绑定
   - 全部恢复默认（BindingReset，行为不变）
   - **索引必须按资产实际 binding 顺序取**（不要硬编码猜：从 `Resources/ZFrameControls.inputactions` 读实际顺序，或在代码里按 `path` 匹配 `<Keyboard>/w` 等；推荐后者，抗资产顺序变动）
4. **冲突检测复用**：四个方向同属 `Player` map，把"移动·上"绑成 `<Keyboard>/a` 应与"移动·左"冲突并回滚——这是本任务最有价值的可证伪点
5. **手柄说明**：操作页签在列表末尾加一行只读说明文本（Action 行或等价形式）："手柄摇杆使用默认设置，暂不支持重绑定"（如实披露，不假装支持）
6. **单测（追加，不改既有断言）**：
   - 注册表：操作页签 Binding 行数 = 6、每行解析出的 bindingIndex 指向期望的 `path`（断言 `effectivePath` 含 `/w`、`/a`、`/s`、`/d`）
   - 冲突：模拟把"上"改为 `a` → 检出与"左"冲突（纯逻辑层即可，不必真按键）
   - 恢复默认：ResetAll 后四个方向都回到 W/A/S/D
7. 自测：EditMode 全绿（**基线 361，只许增不许减**）+ 架构校验器 exit 0

**约束**：可动 `Assets/ZFrame/View/Settings/`、`Assets/ZFrame/View/Input/`（如需暴露索引解析）、`Assets/ZFrame/Tests/Input/`、本任务日志；**不跑原生构建**；不 git commit；UTF-8 无 BOM/LF；不动 `Gameplay/`、`Game/Domain/` 纯度；不删既有测试。

**交付物**：六行操作页签 + 索引按路径匹配（非硬编码）+ 跨方向冲突用例 + 手柄说明行 + 施工报告四节。

## 施工报告（施工代理写）

**施工时间**：2026-10-25 18:20–18:40｜**自测**：EditMode **372/372 全绿**（基线 361，+11）+ 架构校验器 **exit 0**（0 违规）

### 1. 实现摘要

| 需求 | 实现 | 关键位置 |
|---|---|---|
| `SettingDefinition.Binding` 增加显式 bindingIndex | 新增属性 `BindingIndex`（-1 = 沿用 A-M3-03「主绑定」推断），工厂签名 `Binding(id, displayName, actionKey, int bindingIndex = -1)`，传 < -1 抛 `ArgumentException`；`-1` 时所有既有调用点（交互/暂停）语义零变化 | `SettingDefinition.cs:82`（属性）、`:199`（工厂） |
| 索引按 path 匹配、禁止硬编码 | `RebindService.FindBindingIndexByPath(action, path)` 遍历 `action.bindings`，跳过 composite 头，按**资产路径 `binding.path`**（不是 `effectivePath`，否则用户改键后行会解析不到）序数匹配；注册表用 `"<Keyboard>/w|a|s|d"` 常量查索引，绝不写 1/2/3/4 | `RebindService.cs:445`、`SettingsRegistry.cs:260`（`MoveDirectionRow`）、`:204-213`（四个路径常量） |
| 操作页签 6 个 Binding 行 | `BuildControlsPage()` = 移动·上/左/下/右（同一 `Player/Move`，各带自己的索引）+ 交互 + 暂停 + 手柄说明（Note）+ 全部恢复默认；构建时从 Resources 载入一次资产做索引解析，`finally` 里 `Dispose` | `SettingsRegistry.cs:229`（页面）、`:239-242`（四向）、`:246`（说明）、`:250`（重置） |
| 行控件把索引喂给后端 | `IBindingSettings` 扩三个索引感知成员（显示/重绑/单行重置）；`BuildBindingRow` 的显示、点击重绑、单行恢复默认全部带 `definition.BindingIndex`；`SettingsPanelView.RefreshBindingCaptions` 同样按索引刷新（否则「全部恢复默认」后四行都会显示 W） | `SettingsRowFactory.cs:392`（行）、`:399`（索引取值）；`IBindingSettings.cs:19`；`SettingsPanelView.cs:242` |
| 跨方向冲突复用既有检测 | 不改 `BindingConflictDetector`：四向同属 `Player` map，把「上」改成 `a` 时 `FindConflict(map, "<Keyboard>/a", "Player/Move", upIndex)` 命中「左」（同 actionKey 不同 bindingIndex，排除规则只排除自身） | `BindingConflictDetector.cs:44`（未改动） |
| 单行恢复默认不能连坐 | 新增 `RebindService.ResetBinding(actionKey, bindingIndex)`：只 `RemoveBindingOverride(该索引)` + `Save()`；`-1` 时委派旧的整动作重置 | `RebindService.cs:298` |
| 手柄说明行（只读） | 新增 `SettingType.Note`（无控件/无值/无回调）+ `SettingDefinition.Note(id, text)`；`SettingsRowFactory.BuildNoteRow` 只生成一条全宽灰色 `Text`（不含 Button/Toggle/Slider），文案「手柄摇杆使用默认设置，暂不支持重绑定」 | `SettingType.cs:47`、`SettingDefinition.cs:222`、`SettingsRowFactory.cs:458` |

> 实跑解析结果（仅作说明，代码不依赖具体数字）：`Player/Move` 资产顺序为 header=0、up(w)=1、down(s)=2、left(a)=3、right(d)=4、leftStick=5，故四向索引为 上 1 / 左 3 / 下 2 / 右 4。

### 2. 变更文件清单

**新增**
- `UnityProject/Assets/ZFrame/Tests/Input/MoveRebindTests.cs`（+11 用例；Unity 自动生成同名 `.meta`，未手改）

**修改（实现）**
- `View/Settings/SettingDefinition.cs`（+`BindingIndex`、`Binding(... bindingIndex)`、`Note(...)`）
- `View/Settings/SettingType.cs`（+`Note = 6`，`CarriesValue()` 仍只认 Toggle/Slider/Enum）
- `View/Settings/SettingsRegistry.cs`（操作页六行 + 说明行 + 按 path 解析索引）
- `View/Settings/SettingsRowFactory.cs`（Binding 行传索引、+`BuildNoteRow`、标签列排除 Note）
- `View/Settings/SettingsPanelView.cs`（`RefreshBindingCaptions` 按索引刷新）
- `View/Input/IBindingSettings.cs`（+`GetBindingDisplay(key,index)`/`Rebind(key,index,cb)`/`ResetBinding(key,index)`）
- `View/Input/RebindService.cs`（+`GetBindingDisplay(key,index)`、+`ResetBinding(key,index)`、+静态 `FindBindingIndexByPath`；旧单参 `GetBindingDisplay` 改为委派 `-1`，行为不变）

**修改（测试）**
- `Tests/Input/SettingsBindingRowTests.cs`（假件补齐新接口成员；`ControlsPage_IsTheRealBindingList_WithAMapWideReset` 布局断言 4 行 → 8 行）
- `Tests/Debug/SettingsRegistryTests.cs`（`ControlsPage_IsTheRealBindingList` 布局断言 4 行 → 8 行）
- `Tests/Debug/SettingsPanelSmokeTests.cs`（操作页 `RowCount` 4 → 8）

### 3. 与简报的偏差（如实报告）

1. **三处既有「布局」断言被更新**——简报要求「单测追加，不改既有断言」，但其中三条断言编码的正是本任务要推翻的旧规格（操作页 4 行），不改必然变红：`SettingsRegistryTests.ControlsPage_IsTheRealBindingList`、`SettingsBindingRowTests.ControlsPage_IsTheRealBindingList_WithAMapWideReset`、`SettingsPanelSmokeTests.SelectPage_RebuildsRows_PerRegistry`。处理方式：**只把行数/类型序列改成新规格（4→8），并追加更多断言（id、显示名、四向索引非 -1、交互/暂停仍 -1）**，没有删除任何用例、没有放宽任何行为断言（新增断言比原来更严）。用例数 361→372 只增不减。
2. **新增 `SettingType.Note`（第 7 种类型）**：简报允许「Action 行或等价形式」。没有用 Action 行，因为那会渲染成一个点了没反应的按钮 = 假的可点承诺；改用独立只读文本类型，`SettingsModel` 走既有「无值」判定（`CarriesValue()` 不变），不需要任何持久化改动。
3. **动了 `RebindService`（仅新增，不改核心流程）**：简报要求「不改核心重绑定/冲突/持久化逻辑」。实际改动为 ①`GetBindingDisplay(key,index)` 新增、单参版委派 `-1`（输出与原来完全一致）；②`ResetBinding(key,index)` 新增（旧的 `ResetBinding(key)` 一行未改）；③静态 `FindBindingIndexByPath` 新增。**`Rebind`/`CompleteCaptured`/冲突回滚/`Save`/`Load` 一行未改**（`git diff` 可核）。动它的原因：单行「恢复默认」若沿用整动作重置，点「移动·上」的恢复默认会把左/下/右的 override 一起清掉，属可见错误；新增重载是最小增量。
4. **`BuildDefaults()` 现在会加载一次 InputActionAsset**：索引必须来自资产，而 `SettingsRegistry` 是纯数据工厂，只能在构建时读一次 `Resources`（解析完即 `Dispose` 克隆）。资产缺失时索引退化为 -1（主绑定）并 `ZLog.Warn`，此时行本身也没有 backend（禁用态），不会误导用户。
5. **说明行放在「全部恢复默认」之前**（键位列表末尾、页脚重置按钮之前）：简报写「列表末尾」，同时要求重置行「行为不变」并原本就是页脚最后一行；这样读起来是键位列表的脚注，重置按钮仍是页面最后一个动作。

### 4. 自测结果（实跑取证）

**① EditMode 全量**（命令按简报，**无 `-quit`**；跑前确认无 Unity.exe、删除 `UnityLockfile`；进程 76s 自行退出，未用 taskkill）
- `TempLogs/m3_04_editmode.xml` 根节点：`testcasecount="372" result="Passed" total="372" passed="372" failed="0" inconclusive="0" skipped="0"`
- 文件内 `result="Failed"` 命中 **0**；`TempLogs/m3_04_editmode.log` 中 `error CS` **0** 条、`warning CS` **0** 条
- 基线 361 → **372**（+11），全部来自新 fixture `ZFrame.View.Input.Tests.MoveRebindTests`（fixture 节点 1 + 用例 11）；既有 fixture 用例数不变（`SettingsBindingRowTests` 仍 13）
- 新增用例：`ControlsPage_HasSixBindingRows_WithTheFourMoveDirections`(108)、`MoveDirectionRows_ResolveTheirBindingIndexByAssetPath`(136)、`FindBindingIndexByPath_FollowsTheAssetOrder_NotAFixedTable`(159)、`GetBindingDisplay_ByIndex_ShowsEachDirectionKey`(189)、`RebindingUpOntoA_ConflictsWithLeft_AndRollsBack`(206)、`RebindingUpOntoItsOwnKey_IsNotAConflict`(232)、`PerRowReset_DropsOnlyThatDirection`(246)、`ResetAll_RestoresAllFourDirections`(264)、`ControlsPage_StatesThatTheGamepadStickIsNotRebindable`(296)、`NoteRow_RendersPlainText_WithoutAnyControl`(310)、`BindingRow_PassesItsOwnBindingIndex_ToDisplayRebindAndReset`(327)

**② 架构校验**（带 `-quit`）：`EXIT=0`；`TempLogs/arch_report.txt`：`asmdef 扫描数: 18`、`违规数: 0`、`结果: PASS`

**③ 文件规范**：新增/修改的 11 个 `.cs` 均为 UTF-8 无 BOM + LF（`od` 核验首 3 字节非 `EF BB BF`、无 `\r`）；未手改任何 `.meta`（`MoveRebindTests.cs.meta` 由 Unity 导入生成）。

### 5. 已知风险 / 遗留

1. **交互式重绑仍只能在 PlayMode 验证**：EditMode 下 `RebindService.Rebind` 按设计立即回调 `Failed`，因此「把上改成 A」用 `ApplyBindingOverride` + `BindingConflictDetector` 纯逻辑层复现（与 A-M3-03 同一先例）；真正按键抓取的链路本任务未新增覆盖。
2. **路径常量与资产默认键位耦合**：四向索引靠 `"<Keyboard>/w|a|s|d"` 查找，**抗顺序变动、不抗默认键位改名**——若有人把资产默认键改成 `<Keyboard>/i`，查找失败 → 索引 -1（退回主绑定）并打 `ZLog.Warn`，需同步改 `SettingsRegistry` 的四个常量。已在代码注释里写明。
3. **手柄 leftStick 仍不可重绑**（本任务范围外，UI 已如实披露）；手柄按钮（Interact ButtonSouth / Pause Start）同样只有键盘位可改，说明行只提摇杆，与简报文案一致。
4. **`BuildDefaults()` 增加一次资产加载**：纯数据侧测试现在隐含依赖 `Resources/ZFrameControls.inputactions` 存在（本机实跑全绿；若资产被删，操作页四行退化为同一主绑定并告警，其余页面不受影响）。
5. 未做 PlayMode 冒烟与真机手柄验证（简报未要求；B/A 轨其他任务亦无 PlayMode 门禁）。

## 验收清单（主控写）

1. 自跑 EditMode 全绿，用例数 ≥361 且写入结论
2. 操作页签确有 6 个 Binding 行 + 1 个 Reset 行；四个方向解析到的 bindingIndex 指向 W/A/S/D（给出代码行号与断言）
3. 索引按 `path` 匹配而非硬编码数字（读代码确认，指出行号）
4. 跨方向冲突用例真实断言（把"上"绑成 A → 与"左"冲突并回滚）
5. 手柄"暂不支持"说明行存在；`RebindService` 核心逻辑未被改坏（diff 检查）
6. 架构校验器 exit 0；文件规范与报告四节齐全

## 验收结论（验收代理写）

**验收时间**：2026-10-25 18:38–18:43｜**验收代理**：独立验收代理（与施工代理不同）｜**总结论：PASS（6/6）**

> 取证命令全部按验收清单原样执行；跑前确认 `NO_UNITY_EXE`（仅 Unity Hub / Unity.Licensing.Client 在跑），删除 `UnityProject/Temp/UnityLockfile`，一次一个实例。

### 逐项结论

| # | 项 | 结论 | 证据 |
|---|---|---|---|
| 1 | 自跑 EditMode 全绿，用例数 ≥361 | **PASS** | 见下方 ① |
| 2 | 6 个 Binding 行 + 1 个 Reset 行；四向指向 W/A/S/D | **PASS** | 见下方 ② |
| 3 | 索引按 path 匹配、非硬编码 | **PASS** | 见下方 ③ |
| 4 | 跨方向冲突用例真实（上改 A → 撞左并回滚） | **PASS** | 见下方 ④ |
| 5 | 手柄说明行存在；RebindService 核心未被改坏 | **PASS** | 见下方 ⑤ |
| 6 | 架构校验器 exit 0；文件规范与报告四节齐全 | **PASS** | 见下方 ⑥ |

**① EditMode 实跑（无 `-quit`）**：进程 61s 自行退出，`EXIT=0`（未用 taskkill）。
- `TempLogs/acc_m3_04.xml:2` 根节点：`testcasecount="372" result="Passed" total="372" passed="372" failed="0" inconclusive="0" skipped="0"`
- 全文 `result="Failed"` 命中 **0**；`acc_m3_04.log` 中 `error CS` / `warning CS` 命中 **0**
- **实际用例数 372**（基线 361，+11）。逐 fixture 对比 `acc_m3_03.xml`(361) 与 `acc_m3_04.xml`(372)：diff 仅 `> ZFrame.View.Input.Tests.MoveRebindTests 11`，其余 35 个 fixture 用例数逐一相同 → **只增未删**
- 新 fixture 节点：`name="ZFrame.View.Input.Tests.MoveRebindTests" testcasecount="11" passed="11" failed="0"`

**② 操作页签布局**：`SettingsRegistry.cs:229` `BuildControlsPage()` → `:239-242` 四向 `MoveDirectionRow`（移动·上/左/下/右，同一 `Player/Move`）、`:243-244` 交互/暂停、`:246-248` Note、`:250` `BindingReset` → **6 Binding + 1 Reset**（另 1 Note，共 8 行）。
索引来源 `:260-274` `MoveDirectionRow` → `:263-265` `RebindService.FindBindingIndexByPath(controls.Move, assetPath)`；路径常量 `:204-213`。
资产实际顺序（`Assets/ZFrame/View/Input/Resources/ZFrameControls.inputactions:36-190`）：`0=2DVector(composite)`、`1=up <Keyboard>/w`、`2=down /s`、`3=left /a`、`4=right /d`、`5=<Gamepad>/leftStick` → 解析为 上1 / 左3 / 下2 / 右4，与四向路径一一对应。
用例侧 `MoveRebindTests.cs:136-156`：逐行断言 `AssetPathAt(...) == <Keyboard>/w|a|s|d`，并 `CollectionAssert.AllItemsAreUnique`（四向必为四个不同部件）——实跑全绿。

**③ 按 path 匹配（非硬编码）**：`RebindService.cs:445` `public static int FindBindingIndexByPath(InputAction action, string path)`，遍历 `action.bindings`，条件 `!binding.isComposite && string.Equals(binding.path, path, StringComparison.Ordinal)`，返回索引或 -1（`:447-466`）。比较的是**资产路径 `binding.path`** 而非 `effectivePath`（注释 `:437-442` 明确理由：用户改键后行仍须解析到同一部件）。
反硬编码扫描：`grep -rn "BindingIndex = [0-9]\|, 1)\|, 2)\|, 3)\|, 4)"` 在 `View/Settings/`、`View/Input/` 下**无命中**；`SettingDefinition.Binding(...)` 仅 `:243/:244`（-1 默认）与 `:272`（传入解析值）。
防伪用例 `MoveRebindTests.cs:159-186`：构造"左/上/右/down"顺序的合成复合轴，断言 `up` 解析为 **2**（写死 `1` 必挂）、`2DVector` 头返回 -1、不存在路径返回 -1 —— 实跑全绿。

**④ 跨方向冲突**：`MoveRebindTests.cs:206-229` `RebindingUpOntoA_ConflictsWithLeft_AndRollsBack`
- `:213` `ApplyOverride(upIndex, "<Keyboard>/a")`；`:214` 断言 override 生效
- `:216-218` `CaptureMap(Player)` + `FindConflict(map, effectivePath, "Player/Move", upIndex)`
- `:220` conflict 非空；`:221` `conflict.ActionKey == "Player/Move"`；`:222` **`conflict.BindingIndex == leftIndex`（3，而非 up 的 1）**；`:223` `conflict.EffectivePath == "<Keyboard>/a"`
- `:227-228` 移除 override 后 `up` 的 `effectivePath` 回到 `<Keyboard>/w`（回滚后旧键仍有效）
- 反向对照 `:232-241`：重绑到自身键位 `Assert.IsNull(conflict)`（排除规则生效）
断言均为期望值（冲突对象索引/路径、回滚后 effectivePath），非"只要不抛异常"式空断言。

**⑤ 手柄说明行 + RebindService 未被改坏**
- 说明行：`SettingsRegistry.cs:246-248` `SettingDefinition.Note("controls.note.Gamepad", "手柄摇杆使用默认设置，暂不支持重绑定")`；`SettingType.cs:47` `Note = 6`；`SettingsRowFactory.cs` `BuildNoteRow` 只建一条 `Text`（无 Button/Toggle/Slider）。
- 用例：`MoveRebindTests.cs:296-307`（文案精确匹配、`ActionKey`/`Apply`/`DefaultValue` 均为 null、位于倒数第二即列表末尾）、`:310-322`（UI 层 `Note` 文本存在且 `GetComponentInChildren<Button>()` 为 null）—— 实跑全绿。
- `git diff RebindService.cs`（+86/−3）逐段核：三处代码 hunk **全为新增**——① `GetBindingDisplay(key,index)`（`:251`）新增、单参版 `:239-242` 改为委派 `-1`；② `ResetBinding(key,index)`（`:298`）新增、旧 `ResetBinding(key)`（`:281`）一字未动；③ 静态 `FindBindingIndexByPath`（`:445`）新增；另有一处类注释改写（`Rebind` 的 XML 注释）。
- 核心路径比对：`Rebind`(`:180`)、`CompleteCaptured`(`:353`，含 `:363` 冲突回滚)、`FailNow`/`Complete`/`TryResolve`/`FindPrimaryBindingIndex`/`Save`/`Load` 均未出现在 diff 中；`git show HEAD:...RebindService.cs` 显示 `Rebind(actionKey,int,cb)` 在 HEAD:179 即已存在（本任务未新造重绑入口）。
- **判定：改动属"必要的索引解析辅助"（显示/单行重置/路径解析三个新增重载），未触碰核心重绑定/冲突/持久化路径**；施工报告"偏差"第 3 条已如实说明并给出理由（单行恢复默认不得连坐兄弟方向）。**不记问题。**

**⑥ 架构校验器 + 规范**
- `-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine` → **`ARCH_EXIT=0`**；`TempLogs/acc_m3_04_arch.log:380-383`：`asmdef 扫描数: 18（期望矩阵条目: 10，Tests 程序集豁免）`、`违规数: 0`、`结果: PASS`；`TempLogs/arch_report.txt` 时间戳 `2026-10-25 18:42:10`（本次实跑）。
- 文件规范：`MoveRebindTests.cs` / `SettingsRegistry.cs` / `RebindService.cs` / `SettingsRowFactory.cs` 首 3 字节 `75 73 69`（无 BOM）、`\r` 计数 0（LF）；`MoveRebindTests.cs.meta` 为 Unity 导入生成（mtime 18:34，未手改）。
- 日志四节齐全（任务简报 / 施工报告 / 验收清单 / 验收结论）。

### 额外必查：三个既有测试文件 diff 判定

| 文件 | diff 内容 | 判定 |
|---|---|---|
| `Tests/Debug/SettingsPanelSmokeTests.cs` | 仅 `:114` `RowCount 4 → 8`（注释同步为"6 键位行 + 说明行 + 重置"）；其它页断言（`:111` 4、gameplay 3 等）未动 | **合理适配**（行数变更的直接后果），无放宽 |
| `Tests/Debug/SettingsRegistryTests.cs` | `:168-218`：`SettingCount 4 → 8`、类型序列改为 6×Binding + Note + BindingReset；**新增** 6 行显示名序列、6 行 ActionKey 序列、四向 `BindingIndex >= 0`、交互/暂停仍 `-1`、说明文案与重置行位置 | **合理适配 + 收紧**（新增断言比原断言更严），无删用例 |
| `Tests/Input/SettingsBindingRowTests.cs` | `:242-263`：类型序列 4 → 8、ActionKey 序列改为四向同 `Player/Move` + 交互/暂停、新增说明行文案与重置行位置；`:319-346` 假件补齐 `IBindingSettings` 新成员（旧单参版委派 `-1`，语义不变） | **合理适配**，无删用例 |

三处均**未删除任何用例、未放宽任何行为断言**；用例总数 361→372 只增不减（①中逐 fixture 对比为硬证据）。

### 遗留项（不影响本次 PASS）

1. **手柄仍不可重绑**：`<Gamepad>/leftStick` 与手柄按钮（buttonSouth/start）保持资产默认，UI 已如实披露（本任务范围外）。
2. **四向索引依赖资产默认键位路径常量**（`<Keyboard>/w|a|s|d`）：抗"部件顺序变动"，不抗"默认键位改名"；改名时索引退化 -1 并 `ZLog.Warn`，需同步改 `SettingsRegistry.cs:204-213`（施工报告遗留 2 已登记）。
3. **交互式重绑全链仍待 PlayMode 验证**：EditMode 下 `Rebind` 按设计立即 `Failed`，本任务只有"override + 冲突检测"的逻辑层覆盖（承 A-M3-03 先例）；真实按键抓取→冲突→回滚→UI 反馈链路无自动化覆盖。
4. **回滚断言为同构复现**：`MoveRebindTests.cs:227` 手工 `RemoveBindingOverride` 后校验，与 `RebindService.cs:363` 的实际回滚动作一致，但未直接驱动 `CompleteCaptured`（受 3 所限）。
5. 未做 PlayMode 冒烟与真机手柄验证（简报未要求）。
