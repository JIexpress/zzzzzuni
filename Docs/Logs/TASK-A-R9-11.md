# TASK-A-R9-11 —— 修复 Dropdown 模板无效错误（SettingsRowFactory 枚举行）

状态: doing | 派发: 2026-10-31 | 施工: general-purpose 施工子代理(接手续建) | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

### 用户实报（编辑器 Console 红错误）

`The dropdown template is not valid. The template must have a child GameObject with a Toggle component serving as the item.`——场景为 SCN_Game（非 Play 态亦出现）。来源判断：**SettingsRowFactory 为枚举设置生成的 Dropdown**（A-R9-02/数据驱动行）——UGUI Dropdown 要求 Template 层级下有名为 Item 的子物体且含 Toggle 组件；程序化创建时若只建了 Dropdown 骨架没搭 Template/Item/Toggle 结构，运行/编辑期即报此错。

### 修复要求

1. 定位报错 Dropdown 的确切创建点（SettingsRowFactory 的枚举行；**先实证**——在编辑器 Console 报错时点错误定位调用栈，或 grep Dropdown 创建代码；确认场景里哪个对象挂着它）
2. 按 UGUI 标准结构补全 Dropdown 模板：Template→Viewport(ScrollRect)+Content→Item(Toggle+Label)——**手工搭结构易错，优先做法：改用运行期克隆内置模板**（如从 TextMeshPro Dropdown 或自建一次正确的模板树，工厂函数产出结构正确的 Dropdown），模板构建逻辑收敛到一个纯函数+单测
3. **修后必红转绿**：EditMode 用例——用工厂建一个枚举行→断言 Dropdown.template 下存在含 Toggle 的 Item 子物体（修前该断言红）+ dropdown.Show() 不抛（若 EditMode 可测；不可测则 PlayMode 一条）
4. 回归：既有 Settings/RowFactory 用例零改动通过；EditMode 全绿（基线 782 只增不减）；设置面板其余行类型（Toggle/Slider/Action）不受影响

### 约束

- 白名单：`Assets/ZFrame/View/Settings/**`、`Assets/ZFrame/View/UI/**`（若模板辅助放这）、`Tests/**`、本日志
- 不改 SettingsRegistry 定义语义（行数护栏 `SettingsRegistryTests.cs:79-104` 不可动）
- Unity 批处理纪律（清锁/轮询/taskkill/勿带 -quit）；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0

## 施工报告（general-purpose 施工子代理，接手续建，2026-10-31）

### ① 接手审计结论（含 SettingsModel 改动裁决）

前代理施工中途被中断，盘上留四文件半成品（任务日志无施工报告）。接手后逐项对照 uGUI 包源码（`Library/PackageCache/com.unity.ugui@1.0.0`）审计，**结论：全部采纳，零返工**：

1. **根因实证**：用户报错逐字位于包源码 `Dropdown.cs:615`（`SetupTemplate`）：模板内 `GetComponentInChildren<Toggle>()` 为空或挂在模板自身即 LogError 该句并判 `validTemplate=false`（列表不打开）。`SetupTemplate` 全文件仅由 `Show()` 调用（`Dropdown.cs:764-767`），编辑器内（含非 Play 态）同样可触发，与"非 Play 态亦出现"相容。旧 `BuildEnumRow`（HEAD 版）的 Item 为 `new GameObject("Item", typeof(RectTransform))` 只挂 Item Label 文本——**无 Toggle**，正是报错根因。全仓程序化 Dropdown 创建点仅此一处（grep 核对）。

2. **重构正确性**：`CreateStandardDropdownControl()` 改用 `DefaultControls.CreateDropdown(new DefaultControls.Resources())`（简报点名的优先做法）。对照包源码 `DefaultControls.cs:557-617`，其结构恰为 Template(Image+ScrollRect)→Viewport(Image+Mask, showMaskGraphic=false)→Content→Item(**Toggle**，targetGraphic=Item Background/graphic=Item Checkmark)+Item Label+Scrollbar，且 `dropdown.template/captionText/itemText` 由 DefaultControls 接好——SetupTemplate 契约**由构造保证**。工厂收尾五项适配（改名 "Dropdown" 保住 `Row_<id>/Dropdown` 查找约定；删 Arrow 空精灵占位；重上色；28pt 字号行高；模板 `SetActive(false)`——DefaultControls 靠 Awake 藏模板而 EditMode 不跑 Awake）均不触碰契约。`DefaultControls.SetParentAndAlign` 的 `Undo.SetTransformParent` 带 `#if UNITY_EDITOR` 护栏（`DefaultControls.cs:150-158`），打包运行时走普通 SetParent，**无构建风险**；`options.Clear()` 丢弃 Option A/B/C 占位后 AddOptions，数据路径与旧版一致（既有 `EnumRow_MapsToDropdown_WithOptions` 零改动通过）。

3. **SettingsModel.cs +13 裁决：保留——测试接缝必要（实证承重），语义本身亦正确**。改动 = `Set<T>` 对"归一化后值与当前相等"短路（不 dirty、不重发 apply）。审计过程：
   - 机制属实：uGUI `Toggle.Rebuild`（`Toggle.cs:128-134`）在 `#if UNITY_EDITOR` 下 Prelayout 阶段 `onValueChanged.Invoke(m_IsOn)`——canvas 重建时把活值重新播报给监听者；presenter 把行控件 onValueChanged 全部转给 `model.Set`（`SettingsPanelPresenter.cs:79-85`）。
   - **实证为承重接缝（非纯理论）**：接手代理亲做定向变异——把该短路临时还原为 HEAD 语义重跑 EditMode 全量，**恰好 2 红**：新钉子用例 `Set_SameValue_IsNoOp_NotDirty_NoApplyCallback`，以及**本任务零改动的既有用例** `SettingsPanelSmokeTests.RevertButton_RestoresSnapshot_AndStaysOpen`（撤销更改触发行重建 → 回播当前值 → 旧语义把刚清干净的模型重新弄脏 → `Assert.IsFalse(_model.IsDirty)` 红）。复原短路后 785 全绿。即：DefaultControls 引入的 Undo 重建路径使"撤销后保持干净"这条既有契约只能靠模型侧挡回播维持。
   - 语义正确性：值未变的 Set 不构成变更（与控件侧 `SetIsOnWithoutNotify`/`SetValueWithoutNotify` 防回播纪律互补）；`RevertToSnapshot`/`ResetAll`/`Load` 走 `ApplyValue` 直写不经 `Set`，快照/恢复/开机应用语义零变化；逐条核对既有 SettingsModelTests 无与短路冲突的用例。

4. **新用例"修前必红"论证**（按简报以断言目标直接论证，其中第 3 条另获变异实证）：
   - `EnumRow_DropdownTemplate_FollowsUguiSetupTemplateContract`：断言目标（Item 载 Toggle、itemText 位于 Toggle 之下）在旧代码结构中**不存在**——`item.GetComponent<Toggle>()` 必为 null，`Assert.IsNotNull` 必红。
   - `EnumRow_DropdownShow_InstantiatesListWithTogglePerOption`：旧代码下 `Show() → SetupTemplate` 必 LogError 用户报错原文（Unity Test Framework 将未预期错误日志判失败），且 `Dropdown List` 不会实例化——双保险必红。
   - `Set_SameValue_IsNoOp_NotDirty_NoApplyCallback`：旧语义无短路，`IsDirty` 必真、applyLog 必非空——必红（变异运行中真实红过一次）。

### ② 变更清单

- `Assets/ZFrame/View/Settings/SettingsRowFactory.cs`：`BuildEnumRow` 收敛为 `CreateStandardDropdownControl()`（DefaultControls 标准模板 + 面板风格适配 + 模板默认隐藏）；新增 `DestroyGenerated`（播放态 Destroy / 编辑态 DestroyImmediate，同 SettingsPanelView 纪律）。
- `Assets/ZFrame/View/Settings/SettingsModel.cs`：`Set<T>` 同值短路（+13 行，裁决见 ①-3，保留）。
- `Assets/ZFrame/Tests/Debug/SettingsRowFactoryTests.cs`：+2 用例（模板契约结构断言 / `Show()` 真开列表且每选项一个 Toggle）+ EditMode 退出协议 `CloseDropdownInEditMode`。
- `Assets/ZFrame/Tests/Debug/SettingsModelTests.cs`：+1 用例（同值 Set = no-op）。
- 合规：仅动 Settings/**、Tests/** 与本日志；`SettingsRegistryTests.cs:79-104` 行数护栏未触碰（不在 diff 中）；未 git commit；四文件核验 UTF-8 无 BOM（首字节即 `using`）、LF（0 CRLF）。

### ③ 自测数字

| 项 | 结果 |
|---|---|
| EditMode 全量（接手态，短路在位） | **785/785 PASS**（基线 782 + 新增 3，只增不减），exit 0，证据 `TempLogs/runtests_editmode.xml` |
| 定向变异（临时去 Set 短路重跑） | **783/785，恰好 2 红**（见 ①-3），证明短路承重 + 用例有牙 |
| 复原后 EditMode 复跑 | **785/785 PASS**，exit 0 |
| 新 3 用例在 results.xml 逐一核对 | 3/3 `Passed` |
| 架构校验器 ValidateFromCommandLine | **exit 0**（`TempLogs/arch_report.txt`） |
| 既有 Settings/RowFactory/Panel 用例 | 零改动全过（failed=0） |

（Unity 批处理纪律全程经 `Tools/run_tests.ps1`：单实例检查、删 UnityLockfile、无 `-quit`、Git Bash 启动、轮询 results.xml，三次运行均干净退出。）

### ④ 遗留

1. 验收清单 V2 要求验收代理**亲自复做**模板变异（改坏模板结构看红）——本报告未代做（施工自证不替代验收，R10-06 先例）；①-4 已给出结构论证，变异点建议取"删 Item 的 Toggle"或"把 itemText 挂到 Item 之外"。
2. `EnumRow_DropdownShow_...` 用反射调 `Dropdown.Start()` 并经 `CloseDropdownInEditMode` 清 `m_Dropdown/m_Blocker/m_Items` 退出——EditMode 无引擎驱动的既定绕法（用例内注释已说明：edit-mode artifact，非产品路径）；若未来 uGUI 升级改内部字段名，`?.` 链会静默降级（失败仍会体现在断言上，不会假绿于 Show 断言）。
3. DefaultControls 的 Scrollbar/Checkmark 精灵传 null（面板零资产纪律），Image 退化为纯色块渲染——视觉与面板其余行一致；若日后引入真精灵主题需重审此处配色适配。
4. "编辑器非 Play 态出现"的触发时机（用户在编辑器里如何走到 Show）无法从代码唯一复原——报错文本与结构根因唯一且已修，EditMode 用例已覆盖同根因路径；编辑器 GUI 手工点击场景不可单测。

## 验收清单（主控写）

- [ ] V1 用户报错不可复现：EditMode 或 PlayMode 下构造设置面板枚举行，Console 无该错误
- [ ] V2 模板结构断言用例真实（修前红/修后绿，验收复做变异一次）
- [ ] V3 EditMode 全绿（≥782+新增）；既有 Settings 用例零改动通过
- [ ] V4 Validate exit 0（自跑）；文件规范+施工报告四节完整

## 验收结论（验收代理写）

**总体判定: PASS**（V1–V4 全 PASS；全部验收动作为本代理亲跑，未复用施工方结论；两次变异现场均已按哈希复原）

- **V1 PASS**：亲跑 `Tools/run_tests.ps1 -Segments editmode`（单实例/清锁/无 -quit/Git Bash 纪律全程）——**785/785**（基线 782+3，只增不减）exit 0，results.xml 首行 `total="785" passed="785" failed="0"`；3 新用例逐名核对全 `Passed`：`SettingsRowFactoryTests.EnumRow_DropdownTemplate_FollowsUguiSetupTemplateContract`、`SettingsRowFactoryTests.EnumRow_DropdownShow_InstantiatesListWithTogglePerOption`、`SettingsModelTests.Set_SameValue_IsNoOp_NotDirty_NoApplyCallback`；用户报错不可复现——Show 用例走真 `Show()→SetupTemplate` 路径且 Unity Test Framework 对未预期 LogError 判失败，failed=0 即报错原文零出现。
- **V2 PASS**：静态对照包源码逐点核实——`Dropdown.cs:615` 原句与检查点（:612 Toggle 须存在且不在模板自身、:617 父须 RectTransform、:622 itemText 须在 Toggle 之下）、`DefaultControls.cs CreateDropdown` 产出 Template(Image+ScrollRect)→Viewport(Image+Mask)→Content→Item(**Toggle**, targetGraphic=Item Background/graphic=Item Checkmark)+Item Label+Scrollbar，工厂五项适配不触碰契约；**变异亲做**——工厂尾部 `DestroyImmediate(itemRect.GetComponent<Toggle>())` 全量重跑**恰 2 红**且正是两条新用例（失败消息即断言目标："Item must carry a Toggle component (UGUI SetupTemplate contract)" / "Show() must instantiate the option list from the template"），其余 783 全绿；删行复原后 sha256 与施工态逐位一致（`eb1a79d5…`）。
- **V3 PASS**：变异亲做独立复现——删 `Set<T>` 同值短路（还原 HEAD 语义）全量重跑**恰 2 红**：新钉子 `Set_SameValue_...` + **本任务零改动的既有用例** `SettingsPanelSmokeTests.RevertButton_RestoresSnapshot_AndStaysOpen`（撤销→行重建→编辑器期 `Toggle.Rebuild` 回播当前值→旧语义重新弄脏，`IsDirty` 断言红），与施工报告①-3 完全一致，证明 +13 行承重；最小必要成立——功能改动仅 4 行（if/return/注释），其余为文档注释；比较位置在 `ValidateAndNormalize` 之后（先钳制归一化再比较，正确）；`RevertToSnapshot`/`ResetAll`/`Load` 走 `ApplyValue` 直写不经 `Set`（源码核实），快照/恢复/默认/开机应用语义零变化；view 侧无更窄修法（行同步已用 `SetIsOnWithoutNotify`，回播源是 uGUI 编辑器期 `Toggle.Rebuild`（`Toggle.cs` Prelayout `onValueChanged.Invoke(m_IsOn)`），不可从外部关闭；全 `Set` 调用方逐一核对无一依赖同值重放）。
- **V4 PASS**：`ValidateFromCommandLine` 亲跑 **exit 0**（arch：asmdef 扫描 24/违规 0，`TempLogs/arch_report.txt`）；既有 Settings 用例零改动——`git diff` 测试文件**删除行数=0**（纯追加，`SettingsModelTests.cs`/`SettingsRowFactoryTests.cs` 逐行核对），`SettingsRegistryTests.cs:79-104` 行数护栏不在改动集，既有 782 用例随全量 failed=0 零失败通过；文件规范亲验——5 个改动文件 CR 字节=0（`tr -dc '\r' | wc -c`）且无 BOM（首 3 字节 od 核对，`757369`/`232054`）；日志四节齐全；施工报告①②③④与实测一致（含其自认未代做的 V2 变异，本次由验收补做）；变异复原后 EditMode 复跑再证 **785/785** exit 0（`TempLogs/runtests_editmode.xml`，23:1x 新鲜写入）。

验收过程对仓库的影响：仅临时改动 `SettingsRowFactory.cs`（V2）与 `SettingsModel.cs`（V3）各一处并均按 sha256 复原（`eb1a79d5…`/`e7c44cab…` 与施工态一致）；`TempLogs/acceptance_hashes_pre_mutation.txt` 留哈希底稿（不入库）；未 git commit；白名单外零改动。
