# TASK-A-M1-02 ConfigSystem 配置系统（SO 表 + CSV 导入 + 校验）

状态: doing | 派发: 2026-10-24 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：ZFrame.Data 第二件——配置系统：ScriptableObject 配置表为运行时源，CSV 双向工具（导入生成/更新 SO、导出回 CSV），启动构建类型化索引 O(1) 查询，编辑器数据校验（重复 ID/空引用/数值越界）。

**设计要点**：
1. `ZFrame/Data/Config/`：
   - 泛型基座：`ConfigRow`（抽象，`string Id` 唯一键）；`ConfigTable<TRow> : ScriptableObject where TRow : ConfigRow`（`List<TRow> rows`）
   - `ConfigService : IConfigService`：`Load(table)` 按表类型注册；`TRow Get<TRow>(string id)`（内部 `Dictionary<(Type,string),TRow>`，Load 时构建，查询 O(1)）；`IReadOnlyList<TRow> All<TRow>()`；注册进 Services 的 `ZFrameData.Init()` 扩展（幂等）；Reload 支持（编辑器热重载钩子预留）
   - 示例表（入库，供测试与后续使用）：`EnemyConfigRow : ConfigRow {Id, int Hp, float Speed, int Damage}` + `CFG_EnemyTable.asset`（3 行数据）
2. CSV 工具（Editor，`ZFrame/Data/Editor/`）：
   - 约定：首行表头=字段名（Id 必须首列）；类型映射 int/float/bool/string
   - `CsvImporter.Import(csvPath, tableAssetPath)`：无 SO 则创建、有则按 Id 行更新（新增行追加，CSV 中缺失行不删、标记报告）；类型转换失败收集全部错误一次性报（不中途停）
   - `CsvExporter.Export(tableAsset, csvPath)`
   - 菜单：右键 csv → Import；表 SO Inspector 加按钮（CustomEditor 简版：表格化显示+Export 按钮）
   - 示例 `Configs/CSV/enemy.csv`（与 CFG_EnemyTable 内容一致，入库）
3. 校验器（Editor）：`ConfigValidator.Validate(table)`：重复 Id、空 Id、数值越界（row 上可加 `[Range]` 特性反射读取）、枚举字段字符串拼写（本期范围：前三项）；结果列表窗口+返回值供测试；批处理入口 `ValidateFromCommandLine`（沿用架构校验器模式，违规 exit 1）
4. 单测（ZFrame.Data.Tests 追加）：
   - ConfigService：Load 后 Get O(1) 命中/未命中返回 null/All 数量；同表重复 Load 幂等
   - CSV：导入新建/按 Id 更新（改数值后重导入生效）/错误收集（坏类型+缺 Id 一次全报）；导出再导入 roundtrip 数据一致
   - 校验器：构造含重复 Id/空 Id/越界的临时表 → 三类违规各检出
5. 自测：EditMode 全绿 + ValidateFromCommandLine exit 0（Unity 纪律/撞锁重试）

**约束**：只动 `UnityProject/Assets/ZFrame/Data/`（含 Editor 子目录）、`UnityProject/Assets/Configs/`（示例 csv）、`UnityProject/Assets/ZFrame/Tests/`、本任务日志；CSV 解析**自写**（不引第三方 CSV 库）；不 git commit；UTF-8 无 BOM（CSV 用 UTF-8 无 BOM——Excel 兼容性在报告注明）；架构校验器保持零违规（Data 的 Editor 代码放 Editor 子目录+editor-only asmdef 或 #if，按现有矩阵模式）。

**交付物**：基座+服务+示例表/CSV+导入导出+校验器+单测全绿+施工报告四节。

## 施工报告（施工代理写）

### 一、接手背景与实现清单

前次施工代理已落盘大部分实现（代码齐全但**从未通过编译**，且缺资产/缺失部分测试/全部 .meta）。本次接手核对后补齐并修复，最终交付：

**运行时（ZFrame.Data 程序集，`Assets/ZFrame/Data/Config/`）**
- `ConfigRow`：抽象行基类，`string Id` 唯一键（CSV 首列）
- `ConfigTable<TRow> : ScriptableObject`：`List<TRow> rows` + `AddRow/ClearRows`（工具与测试用）
- `IConfigService` / `ConfigService`：`Load/Get/All/Reload/Clear`；O(1) 索引为 `Dictionary<(Type rowType, string id), ConfigRow>`（ConfigService.cs:16-17），`All` 走按类型存放的 `List<TRow>`（ConfigService.cs:21-23），查询 O(1)；重载为替换语义；空 Id/null 行跳过并告警；`Reload` 为 `Load` 别名（编辑器热重载预留）
- 示例：`EnemyConfigRow`（Id/Hp/Speed/Damage，带 `[field: Range]`）+ `EnemyConfigTable`；资产 `Assets/Configs/CFG_EnemyTable.asset`（3 行：slime/goblin/orc，由 `ConfigAssetSetup.Generate` 从 CSV 导入生成，已入库）
- 注册：`ZFrameData.Init()` 幂等注册 `IConfigService`（ZFrameData.cs:41-44），`Shutdown` 反清理

**编辑器（`Assets/ZFrame/Data/Editor/`，`#if UNITY_EDITOR` 包裹于 Data 程序集内，未新增 asmdef——架构矩阵零改动）**
- `CsvUtility`：自写 CSV 基本集解析/转义（逗号、双引号转义 `""`、引号内逗号/换行、CRLF/LF/CR、可选 BOM）。**限制（已注释注明）**：定界符不可配置、无注释行、不加 trim、未闭合引号抛 FormatException
- `CsvImporter`：`ImportRows`（纯函数，可测）+ `Import`（文件/资产包装：无 SO 则创建、有则按 Id 行更新，新增追加、CSV 缺失行**不删、标记** MissingInCsv）；错误**一趟收集全量一次报**；表头首列必须 Id；类型映射 int/float/bool/string（InvariantCulture）；右键 csv 菜单 `Assets/ZFrame/Import as Config Table`
- `CsvExporter`：`BuildCsv`（纯）+ `Export`（UTF-8 **无 BOM**、LF）；`TableAssetPathFor` 按约定 `Configs/CSV/x.csv → Configs/CFG_XTable.asset`（首字母大写）
- `ConfigValidator`：重复 Id/空 Id/[Range] 越界（**同时查属性与 backing field 特性**——`[field: Range]` 落在 backing field 上）；`ValidateAllConfigAssets` 扫描全部配置 SO；结果列表窗口（菜单 ZFrame/配置校验）；批处理入口 `ValidateFromCommandLine` 写 `TempLogs/config_report.txt`，违规 exit 1
- `EnemyConfigTableInspector`：表格化显示 + 从 CSV 导入（按 Id 更新）/导出 CSV/校验此表 三个按钮

**测试（`Assets/ZFrame/Tests/Data/`）**
- `ConfigServiceTests`（7 例）：Init 注册路径、Get 命中/未命中 null、All 数量、同表重复 Load 替换语义、Reload 别名、空 Id 跳过
- `CsvImporterTests`（9 例）：空表导入新建、按 Id 更新（改数值重导入生效）、缺失行标记不删、坏类型+缺 Id 同趟全报、未知列报错、表头校验、导出→导入 roundtrip 数据一致、CSV 转义往返、资产命名约定
- `ConfigValidatorTests`（6 例）：干净表 0 违规、重复 Id/空 Id/Range 越界三类各检出、多类违规同趟全报、非泛型核心入口

### 二、本次修复的前次遗留缺陷（6 处）

1. `ConfigRow.cs` 缺 `using UnityEngine;`（CS0246 SerializeField）
2. `IConfigService.cs` 缺 `using System.Collections.Generic;`（CS0246 IReadOnlyList，连带 CS0738）
3. `ConfigAssetSetup.cs` `const` 插值字符串为 C# 10 特性（Unity 2022.3 = C# 9）→ 改 `static readonly`
4. `ConfigValidator.cs` 静态类内直接调 `GetWindow<...>`（CS0103）→ `EditorWindow.GetWindow<...>`
5. `CsvImporter.Import` 对泛型 ScriptableObject `CreateInstance<ConfigTable<TRow>>()` 返回 null（Unity 不支持泛型 SO 实例化）→ `Import` 增加 `Func<ConfigTable<TRow>> tableFactory` 参数，由具体表类型创建（右键菜单经 `Delegate.CreateDelegate` 绑定 `CreateInstance<具体表>`）
6. `ConfigService.All<TRow>` 将 `List<ConfigRow>` 强转 `IReadOnlyList<TRow>`（InvalidCastException）→ 内部改存 `Dictionary<Type, object>`（实为 `List<TRow>`），O(1) 不变；另修 `ConfigValidator` 读取 `[field: Range]`（backing field 特性）与 `TableAssetPathFor` 大小写约定（CFG_EnemyTable）

### 三、自测证据（Unity 批处理纪律：清锁/单实例/轮询 xml/taskkill）

- EditMode：`TempLogs/m1_02_editmode.xml` → `testcasecount="113" result="Passed" total="113" passed="113" failed="0"`（本轮新增 22 例：ConfigService 7 + CsvImporter 9 + ConfigValidator 6；上轮基数 91）
- 架构校验：`-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine` → **exit 0**，`TempLogs/arch_report.txt` 违规数 0 PASS（asmdef 扫描 15，无新增程序集）
- 配置校验：`-executeMethod ZFrame.Data.ConfigEditor.ConfigValidator.ValidateFromCommandLine` → **exit 0**，`TempLogs/config_report.txt` 违规数 0 PASS
- 资产生成：`ConfigAssetSetup.Generate` → `[ConfigAssetSetup] imported: added=3, updated=0, missingInCsv=0, errors=0`
- 过程中按纪律处置 2 次僵死 Unity.exe（taskkill //F）后重跑成功

### 四、文件规范与说明

- 全部新文件 UTF-8 **无 BOM**、LF、文件尾换行（python 字节级复核通过）；未手改任何 meta guid；未 git commit
- **CSV Excel 兼容性**：文件为 UTF-8 无 BOM（符合仓库规范）。Excel 双击打开含非 ASCII 字符的 CSV 会按系统 ANSI 解码导致乱码；如需 Excel 直开可手动"数据→自文本"选 UTF-8，或未来加 BOM 开关（本期限于规范不做）
- 未新增 asmdef，`ArchitectureValidator` 期望矩阵零改动；Data 的 Editor 代码走 `#if UNITY_EDITOR`（既有矩阵模式允许）
- 遗留建议（不阻塞）：`ConfigValidator` 枚举拼写校验为简报"本期范围外"，后续任务如需可扩展

## 验收清单（主控写）

1. 自跑 EditMode 全绿（用例数写入结论）
2. 导入更新语义实证：改 CSV 数值→重导入→运行时 Get 返回新值（用例或验收代理实操）
3. 错误收集一次性全报（坏类型+缺 Id 同批检出）用例存在
4. roundtrip 用例（导出→导入数据一致）存在
5. 校验器三类违规检出用例；批处理 ValidateFromCommandLine exit 0（自跑）
6. ConfigService 经 ZFrameData.Init 注册（Services 可 Get）；O(1) 字典实现行号
7. 文件规范抽查+施工报告完整

## 验收结论（验收代理写）

**验收: general-purpose 验收子代理（与施工不同代理，未改任何代码）｜日期 2026-10-24｜结论 PASS（7/7）**

1. **PASS**——自跑 EditMode（Unity 纪律：单实例确认/清锁/轮询/2 次僵死 taskkill 后复跑成功）：`TempLogs/accept_m1_02_editmode.xml` → `testcasecount="113" result="Passed" total="113" passed="113" failed="0" skipped="0"`；fixture 分布 ConfigServiceTests 7 + CsvImporterTests 9 + ConfigValidatorTests 6 = 新增 22，基数 91，与施工报告吻合
2. **PASS**——导入更新语义用例实证：`CsvImporterTests.ImportRows_ExistingIds_UpdatesInPlace_NewIdsAppended`（CsvImporterTests.cs:46-66）改 slime 数值 30→99 重导入后断言新值生效（本次 113 全绿中实测通过）；另验 `Configs/CSV/enemy.csv` 与 `CFG_EnemyTable.asset` 三行数据逐字段一致
3. **PASS**——一次性全报用例存在：`ImportRows_BadTypeAndEmptyId_AllErrorsCollectedInOnePass`（CsvImporterTests.cs:84-98）坏类型(Hp=abc)+缺 Id 同批，断言 `report.Errors.Count == 2` 且分别指明字段与"Id 为空"，实测通过
4. **PASS**——roundtrip 用例存在：`Roundtrip_ExportThenImport_DataIdentical`（CsvImporterTests.cs:123-146）导出→导入逐行逐字段断言一致，另有 `Roundtrip_SpecialCharacters_SurviveEscapeAndParse`，实测通过
5. **PASS**——校验器三类违规检出用例齐备（ConfigValidatorTests：重复 Id/空 Id/Range 越界各检出+多类同趟全报 6 例，实测通过）；自跑 `ConfigValidator.ValidateFromCommandLine`（Start-Process -Wait 捕获真实退出码）→ **exit 0**，`TempLogs/config_report.txt`（19:40:04 新鲜产出）违规数 0 PASS；自跑 `ArchitectureValidator.ValidateFromCommandLine` → **exit 0**，`TempLogs/arch_report.txt`（19:40:22）asmdef 扫描 15、违规数 0 PASS
6. **PASS**——注册路径实证：`ZFrameData.Init()` 幂等注册 `IConfigService`（ZFrameData.cs:41-44），用例 `Init_RegistersConfigService_IntoServices` 断言 `Services.TryGet(out IConfigService _)` 通过；O(1) 实现：`ConfigService.cs:16-17` 字典索引 `Dictionary<(Type rowType, string id), ConfigRow>`，Get 走 TryGetValue（:56-64），All 走按类型 `List<TRow>`（:21-23/:67-70）
7. **PASS**——文件规范字节级抽查 19 个新/改动文件：UTF-8 无 BOM（head -c3 均非 efbbbf）、LF（od 逐字节核实，初查 grep CR 计数为误报）、文件尾换行齐；enemy.csv 无 BOM；.meta 齐全（Configs 3 + Config 6）；未手改 meta guid；施工报告四节（简报/施工报告/验收清单/验收结论）齐全，"修复 6 缺陷"逐条有对应代码可查；施工代理未 git commit（HEAD 仍为 25d9cb2 G-22）

**备注**：本机 Unity 批处理进程存在退出挂死通病（EditMode 与批处理各现 1 次，日志已达关机终点但进程不退），均按 HANDOFF 纪律轮询超时后 taskkill //F 处置，证据以落盘 xml/报告为准；为获取可信退出码，两个校验器改用 PowerShell Start-Process -Wait 捕获，均为真实 exit 0。验收过程零代码改动，未 git commit。
