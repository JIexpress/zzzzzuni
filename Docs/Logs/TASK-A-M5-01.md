# TASK-A-M5-01 编辑器工具集：CSV 工作台 UI + UI 校验器 + 资产命名检查

状态: todo | 派发: 2026-10-25 | 施工: 施工代理（CodeBuddy/GLM）| 验收: （待填） | commit: （待填）

**前置**：无硬前置（可与 B 轨并行）。

## 任务简报（主控写）

**目标**：A-M5 第一件——开发效率工具：①配置工作台 EditorWindow（表格式编辑 SO+导出 CSV+全表校验按钮）②UI 面板校验器（prefab 缺 CanvasScaler/锚点违规/引用丢失检查）③资产命名规范检查器（前缀表扫描，违规清单窗口+批处理）。

**设计要点**：
1. `ZFrame/Editor/Tools/ConfigWorkbench.cs`：EditorWindow——选定 ConfigTable 资产→表格化显示（行=Row 列=字段，SerializedProperty 驱动）→行内编辑/增删行→按钮：导出 CSV（复用 A-M1-02 CsvExporter）/校验当前表（复用 ConfigValidator）/全表扫描（Assets/Configs 下所有表逐个校验汇总）
2. `ZFrame/Editor/Tools/UiValidator.cs`：扫 Assets/UI 的 prefab——Root 无 CanvasScaler 告警、RectTransform 锚点不在预设集合（拉伸/角落/中心）告警、组件引用丢失（null 且 scene 引用）告警；输出窗口列表（prefab+路径+问题），可点击定位
3. `ZFrame/Editor/Tools/AssetNameValidator.cs`：按规范 §5 前缀表（CHR_/ENV_/WPN_/UI_/CFG_/MAT_/TX_/AUD_/SCN_/MIX_）扫 Assets 主要目录，违规清单窗口+`ValidateFromCommandLine` 模式（并入架构校验报告或独立报告文件，违规 exit 1——**先跑一遍现有工程，存量违规要么修复要么白名单**，报告记录决策）
4. 菜单统一 `ZFrame/Tools/` 下；三工具单测（纯逻辑部分：命名规则判定函数/UI 规则判定数据）
5. 自测：EditMode 全绿+Validate exit 0+每个工具开窗冒烟（批处理 -executeMethod 静态入口）

**约束**：可动 Assets/ZFrame/Editor/、Tests/、本任务日志；存量资产若需改名/修复仅限测试资产（TestCube 等），报告列清单；不 git commit；UTF-8 无 BOM/LF。

**交付物**：三工具+单测+存量整改清单+施工报告四节。

## 施工报告（施工代理写）

施工时间: 2026-10-25 22:20~23:00 ｜ 施工代理: CodeBuddy（GLM）｜ 未 git commit（按铁律交主控提交）

### 1. 实现摘要

1. **CSV 配置工作台** `ZFrame/Editor/Tools/ConfigWorkbench.cs`（菜单 `ZFrame/Tools/配置工作台`）
   - 选定 ConfigTable 派生资产（Project 选中自动绑定 / ObjectField 手选）→ SerializedProperty 驱动的表格视图（列头=行类型公共属性，基类 ConfigRow 在前，与 CSV 列序一致；`[field: SerializeField]` 自动属性映射到 `<Name>k__BackingField`）；
   - 行内编辑直接写回资产（有未保存修改黄色提示）、`+ 添加行`（arraySize++）、逐行 `×` 删除；
   - 按钮：**导出 CSV**（反射调用泛型 `CsvExporter.Export`——复用 A-M1-02，非重写；文件名按 `CFG_XxxTable→xxx.csv` 逆向约定，纯函数 `CsvFileNameFor` 可单测）、**校验当前表**（复用 `ConfigValidator.ValidateRows`）、**全表扫描**（`Assets/Configs` 下所有 ScriptableObject 逐表校验汇总）；
   - 批处理入口 `ZFrame.Editor.ConfigWorkbench.ValidateAllFromCommandLine`：写 `TempLogs/workbench_scan_report.txt`，违规 exit 1。
2. **UI 面板校验器** `ZFrame/Editor/Tools/UiValidator.cs` + 纯逻辑 `UiAnchorRules.cs`（菜单 `ZFrame/Tools/UI 面板校验`）
   - 扫 `Assets/UI` 全部 prefab（只读）：① Root 带 Canvas 但缺 CanvasScaler → 告警（面板 prefab 不带 Canvas 时由 UIRoot 分层 Canvas 承载，属正常形态，不告警——对简报的落地解释，见已知风险②）；② RectTransform 锚点不在预设集合（拉伸/角落/边中点/中心）→ 告警（判定在 `UiAnchorRules.IsPreset/PresetName` 纯函数）；③ missing script 组件 → **错误级**、自研组件上为空的场景引用字段（声明类型派生 Component/GameObject）→ 告警；
   - 清单窗口逐条可点击定位（PingObject）+ 严重度图标；批处理入口 `UiValidator.ValidateFromCommandLine` 写 `TempLogs/ui_report.txt`，**仅错误级 exit 1**（简报三类均为"告警"级，退出码策略在报告头声明）。
3. **资产命名检查器** `ZFrame/Editor/Tools/AssetNameValidator.cs` + 纯逻辑 `AssetNamingRules.cs`（菜单 `ZFrame/Tools/资产命名检查`）
   - 按 file-conventions §5 前缀表（CHR_/ENV_/WPN_/UI_/CFG_/MAT_/TX_/AUD_/SCN_/MIX_）扫 `Assets/`，跳过路径含 `Plugins/ThirdParty/Editor` 段与 `.meta/.cs/.asmdef`；**只读不改**——发现违规仅列清单，绝不自动重命名；
   - 白名单决策（存量处置，全部白名单、零修复、零改名，理由如下）：
     | 范围 | 理由 |
     |---|---|
     | `UI/Resources/ui_*` | M3-01 资源键约定（`prefabKey "ui_xxx"` 经 ResourcesProvider 加载，改名破坏运行时加载） |
     | `Settings/` 整目录 | M2 渲染管线基建（URP 档位/Volume/ZRenderPipeline/URP 全局设置），共 16 资产 |
     | 根目录 `UniversalRenderPipelineGlobalSettings.asset` | URP 包自动生成 |
     | `ZFrame/Tests/` | 测试资产（TestCube.prefab/TestAsset.asset，简报允许的处置选项之一，避免 .meta guid 联动风险） |
   - 批处理入口 `AssetNameValidator.ValidateFromCommandLine` 写 `TempLogs/name_report.txt`，违规 exit 1。
4. **单测**：新建 `ZFrame.Editor.Tests` 程序集（Tests 程序集豁免架构矩阵，含 `.Tests` 后缀使 ArchitectureValidatorTests 的矩阵计数断言保持平衡）——命名规则判定（前缀正例 10 条/反例/大小写敏感/空词干/白名单/不适用扩展名）、锚点规则（拉伸/角落/边中点/中心/任意比例点）、CSV 文件名推导。工作台与 UI 校验器的 Unity 资产依赖部分（SerializedProperty 编辑、prefab 加载）无法纯逻辑化，测试策略=批处理静态入口实跑 + 既有 CsvExporter/ConfigValidator 单测覆盖其复用核心。
5. 菜单统一收在 `ZFrame/Tools/` 下；全部新文件 UTF-8 无 BOM + LF（od 字节级核验）。

### 2. 变更文件清单（全部新增，无既有文件改动）

| 文件 | 说明 |
|---|---|
| `UnityProject/Assets/ZFrame/Editor/Tools/ConfigWorkbench.cs` | CSV 配置工作台 |
| `UnityProject/Assets/ZFrame/Editor/Tools/UiValidator.cs` | UI 面板校验器 |
| `UnityProject/Assets/ZFrame/Editor/Tools/UiAnchorRules.cs` | 锚点规则纯逻辑 |
| `UnityProject/Assets/ZFrame/Editor/Tools/AssetNameValidator.cs` | 命名检查器 |
| `UnityProject/Assets/ZFrame/Editor/Tools/AssetNamingRules.cs` | 命名规则纯逻辑 |
| `UnityProject/Assets/ZFrame/Tests/Editor/ZFrame.Editor.Tests.asmdef` | 新测试程序集（豁免矩阵） |
| `UnityProject/Assets/ZFrame/Tests/Editor/AssetNamingRulesTests.cs` | 命名规则单测 |
| `UnityProject/Assets/ZFrame/Tests/Editor/UiAnchorRulesTests.cs` | 锚点规则单测 |
| `UnityProject/Assets/ZFrame/Tests/Editor/ConfigWorkbenchTests.cs` | CSV 文件名推导单测 |

（对应 `.meta` 由 Unity 批处理自动生成，未手改 guid。TASKS/HANDOFF/WORKLOG 未动。）

### 3. 自测结果（Git Bash 实跑，证据在 TempLogs/）

| 项 | 结果 | 证据 |
|---|---|---|
| EditMode 全量 | **542/542 全绿, exit 0**（基线 489 → +53，只增不减） | `TempLogs/m5_01_editmode.xml`: `total="542" passed="542" failed="0" inconclusive="0" skipped="0"` |
| 架构校验器 | **exit 0** | `TempLogs/m5_01_arch.log` + arch_report |
| 命名检查器自跑本仓库 | **exit 0, 违规 0** | `TempLogs/name_report.txt`: "违规数: 0 / 结果: PASS"（首跑曾报 `Settings/ZRenderPipeline.asset` 1 处，按上表白名单处置后归零） |
| UI 校验器自跑 | **exit 0，错误 0 / 告警 7** | `TempLogs/ui_report.txt`：7 条锚点告警（ui_main_menu 4 按钮 + ui_pause_panel 3 按钮，(0.5, 0.22~0.58) 任意比例点锚），如实列出，按"只读不改"未修复 |
| 配置工作台全表扫描 | **exit 0（1 张表 / 0 违规）** | `TempLogs/workbench_scan_report.txt` |
| 三工具冒烟 | 三者批处理静态入口均亲跑（无 -quit 的测试跑 4 次、-quit 的工具跑 4 次，全程单实例+先删 UnityLockfile） | 各 TempLogs/m5_01_*.log |

### 4. 已知风险

1. **7 条存量锚点告警**：M3-01/M4 的 UI prefab 按钮使用任意比例点锚（如 (0.5,0.34)），不在预设集合。属设计选择而非错误，按"只报告不自动修复"保留；如需收敛可后续统一改为预设锚点+offset（涉及 prefab 重排，风险高于收益，未动）。
2. **CanvasScaler 规则的落地解释**：简报原文"Root 无 CanvasScaler 告警"，但本框架面板 prefab 不带 Canvas（UIRoot 运行时创建分层 Canvas+CanvasScaler，见 `UIRoot.cs`）——若按字面实现则 4 个 prefab 全部误报。实现为"Root 带 Canvas 却缺 CanvasScaler 才告警"，已在类头注释与报告声明。
3. **UI 校验器退出码策略**：简报三类问题均称"告警"，故 missing script 定为错误级（exit 1）、其余告警级不阻断 exit 0——策略写入报告头，验收时请按此口径核对 exit 0。
4. **UiValidator 引用丢失检查的边界**：Unity 序列化层面"从未赋值"与"引用丢失"均落为 null（不可区分），实现按"自研组件上为空的场景引用字段"报告（告警级）；当前 4 个 prefab 无此类字段，输出 0 条。
5. **命名白名单是代码内常量**（`AssetNamingRules.WhitelistedDirectories/FileNames` + `UI/Resources/ui_*` 规则）：未做成资产/文件配置，新增白名单需改代码+单测。规模小（4 类），暂不引入配置文件复杂度。
6. **工作台"导出 CSV"为资产→CSV 单向覆盖**：按 TableAssetPathFor 逆向约定写 `Assets/Configs/CSV/`，同名文件会被覆盖（M1-02 导入器才是 CSV→资产方向，双向使用时以最新编辑侧为准）。


## 验收清单（主控写）

1. EditMode 全绿+Validate exit 0（含命名检查）
2. 三工具批处理静态入口各自 exit 0 亲跑
3. 命名规则判定单测（正例反例）；UI 规则判定单测
4. 存量违规处置记录（修了什么/白名单了什么）
5. 工作台与 A-M1-02 CSV/校验器复用（行号证据，非重写）
6. 文件规范+施工报告完整

## 验收结论（验收代理写）

验收时间: 2026-10-25 23:05~23:10 ｜ 验收代理: 独立验收代理（CodeBuddy/GLM，与施工非同实例）｜ 总结论: **PASS 7/7**

### 逐项裁决

| # | 项 | 裁决 | 证据 |
|---|---|---|---|
| 1 | 自跑 EditMode 全绿 | **PASS** | 验收代理实跑（Git Bash、无 -quit、跑前删 UnityLockfile、单实例）：`TempLogs/acc_m5_01.xml` = `total="542" passed="542" failed="0" inconclusive="0" skipped="0"`，exit 0。542 = 基线 489 + 53，只增不减 ✓ |
| 2 | 工具复用既有服务 | **PASS** | 导出复用：`ConfigWorkbench.cs:219-222` 反射调用 `CsvExporter.Export`（泛型 MakeGenericMethod，非重写）；校验复用：`ConfigWorkbench.cs:250`（全表扫描）、`:291`（批处理入口）、`:322`（单表）均调 `ConfigValidator.ValidateRows`。无复制逻辑 |
| 3 | 命名规则对照规范 + 只读 | **PASS** | 前缀表 `AssetNamingRules.cs:32-35`（CHR_/ENV_/WPN_/UI_/CFG_/MAT_/TX_/AUD_/SCN_ 与 file-conventions.md §5 一致；MIX_ 为简报补充，用于 MIX_AudioMixer.asset）；只读确认：全目录 grep 无 `RenameAsset/File.Move/Directory.Move/File.Delete/AssetDatabase.DeleteAsset/MoveAsset` 调用（0 命中） |
| 4 | 白名单合理性 | **PASS** | 逐条核实：① `UI/Resources/ui_*`×4 —— 资源键约定属实（`TestPanelView.cs:23` prefabKey "ui_test_panel"、`SettingsUiSetup.cs:33` "ui_settings_panel"，经 ResourcesProvider 加载，改名破坏运行时）② `Settings/` 整目录 11 资产 —— 全为 URP 档位/Renderer/Volume/ZRenderPipeline 基建，无前缀合理 ③ 根目录 UniversalRenderPipelineGlobalSettings.asset —— URP 包生成，存在 ✓ ④ `ZFrame/Tests/`×3 —— 测试资产，简报允许。全工程受检资产穷举核对：**无真违规被白名单掩盖**（白名单内 CFG_PipelineTiers、MAT_TestCube 本就合规命中前缀，优先判 Ok）；白名单外全部合规（SCN_×3/CFG_EnemyTable/MIX_AudioMixer） |
| 5 | 7 条锚点告警核实 | **PASS** | 抽 2 条对照 prefab YAML：`ui_main_menu.prefab:587-588` m_AnchorMin/Max (0.5,0.34) = SettingsButton 告警值 ✓；`ui_pause_panel.prefab:146-147` (0.5,0.32) = QuitToMenuButton 告警值 ✓。其余 5 条数值在 YAML 中逐一比对全部吻合。全部为 `[Warning]` 级；退出码策略在报告头与 `ui_report.txt` 均写明"仅错误级 exit 1"，错误=0 → exit 0，与施工报告口径一致 |
| 6 | 架构校验器自跑 + asmdef | **PASS** | 验收代理实跑 `-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine`（带 -quit）：**exit 0**，报告"asmdef 扫描数: 21（期望矩阵条目: 10，Tests 程序集豁免）/ 违规数: 0 / PASS"（`TempLogs/acc_m5_01_arch.log`）。Tests 豁免逻辑在 `ArchitectureValidator.cs:143-144`（`.Tests` 后缀/`.Tests.` 包含即豁免矩阵），`ZFrame.Editor.Tests.asmdef` 命名命中豁免，矩阵期望仍为 10 未被撑爆 |
| 7 | 防作弊 | **PASS** | `git status`：仅新增文件（Tools/×5 + Tests/Editor/×4 + meta）与 `Docs/Logs/TASK-A-M5-01.md` 改动；`git diff --stat` 仅该日志（+62/-2，即施工报告写入）——**既有测试零删改零放宽**。新增测试抽 3 条均为真断言：`AssetNamingRulesTests.MissingPrefix_Violation`（断言 Violation + 理由含"无合法前缀"）、`UiAnchorRulesTests.EdgeMidpoints_ArePreset`（断言 PresetName="边中点"）、`ConfigWorkbenchTests.StandardTableName_MapsToSnakeCsv`（断言 "CFG_EnemyTable"→"enemy.csv"）。新增 53 用例在 acc_m5_01.xml 中逐条可见（ZFrame.Editor.Tests 程序集，含参数化展开） |

### 与施工报告不符处

1. **小出入（不影响结论）**：施工报告称 Settings/ 白名单"共 16 资产"，实测该目录资产为 **11 个**（1 ZRenderPipeline + 6 URP_*/Renderer + 3 Vol_* + 1 CFG_PipelineTiers）。差异不影响白名单决策正确性，疑为统计口径（含 .meta 或笔误），记录在案。
2. 其余各项（542 用例、exit 0、7 条告警数值、白名单四类范围、复用关系）与施工报告**完全一致**。

### 遗留项（不阻塞验收，转后续决策）

1. 7 条存量锚点告警保留（设计选择，"只报告不自动修复"）；如需收敛另行开任务统一改预设锚点。
2. 命名白名单为代码内常量（`AssetNamingRules.WhitelistedDirectories/FileNames`），新增白名单需改代码+单测——规模小可接受，若白名单增长建议外置配置。
3. 命名检查器扫描跳过路径含 `Editor` 段的目录——若未来在 Editor 目录下放需命名管理的资产会被跳过，当前无此类资产。

**总结论：PASS（7/7）。A-M5-01 可闭环，交主控 commit。**
