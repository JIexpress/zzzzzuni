# TASK-A-CONF-02 —— 表类薄壳 + 注册行自动化（连表类也零手写）

- **轨道**：A / CONF（A-CONF-01 遗留 §6.1 的收尾）
- **状态**：todo（施工完成，待验收）
- **派发**：主控，2026-11-15（本轮接手指令）
- **来源**：`Docs/Logs/TASK-A-CONF-01.md` §6「遗留风险 / 建议」第 1 条：
  「表类薄壳仍手写：`XConfigTable : ConfigTable<XRow>` 10 行 + `CsvImporter.TableByCsvName` 一行注册……
  如要『连表类都零手写』，生成器可加表类发射 + 注册表自动化」

## 任务简报（主控未落盘，施工代理按派发令代拟；来源=上列遗留条 + TASKS.md 行）

### 0. 现状（施工前实测）

- 行类型已由 `ConfigRowCodegen` 生成（A-CONF-01）；**表类仍手写**：`ZFrame/Data/Config/EnemyConfigTable.cs` 10 行。
- **注册仍手写**：`CsvImporter.TableByCsvName = { ["enemy"] = typeof(EnemyConfigTable) }`，加一张表必须改这行。
- 既有链路：CSV 表头注解 → 导入钩子/菜单/批处理生成行类型 → 右键 `Import as Config Table` 建资产。

### 1. 做什么

1. 生成器同批发射**表类薄壳**（`XConfigTable : ConfigTable<XConfigRow>` + `[CreateAssetMenu]`），落 `Generated/`。
2. **注册自动化**：删掉手写字典，改为反射发现全部 `ConfigTable<TRow>` 派生类，按类名约定映射 csv 名。
3. **首个真实消费者迁移**：`EnemyConfigTable` 变生成产物（GUID 不变，资产引用不破）。
4. **演示表走全自动链路**：新增一张 CSV → 校验 → 生成行类+表类 → 注册 → 建资产 → `ConfigService` 可读。
5. **生成物可复现**：同输入两次生成逐字节一致（且第二次不重写、mtime 不动）。
6. 保持 `CsvImporter` / `CsvExporter` / `ConfigValidator` 既有行为不变。

### 2. 约束

- 不引第三方代码生成依赖；不新增 asmdef（27 不变）；不动 ZRender/Native。
- 生成器与产物入库；UTF-8 无 BOM / LF；不 commit、不改看板。

### 3. 完成判据

- 「加一张表 = 放 CSV → 重导入 → 行类型/表类自动出现 → 直接导入/读取」，全程零手写。
- 入库产物与生成器输出逐字节同步（防漂移守卫用例）。
- 全量 EditMode / PlayMode / arch / naming 全绿，既有 Config 用例零回退。

## 施工报告（施工子代理，2026-11-15）

### 1. 交付内容

| 文件（`UnityProject/Assets/` 下） | 角色 |
|---|---|
| `ZFrame/Data/Editor/ConfigTableCodegen.cs`（新增） | 纯发射器：`TableClassNameForRow`（XConfigRow→XConfigTable）、`Generate` 发 `[CreateAssetMenu(fileName="CFG_XTable", menuName="ZFrame/CFG X Table")]` + 空壳类；确定性输出，走既有 `WriteIfChanged` |
| `ZFrame/Data/Editor/ConfigTableRegistry.cs`（新增） | 反射注册表：扫描全部程序集里非抽象 `ConfigTable<TRow>` 派生类，键 = 类名去 `ConfigTable` 后缀转小写（`EnemyConfigTable`→`enemy`）；候选按（程序集名，全名）排序保证确定性；重复键进 `Conflicts` 不静默 |
| `ZFrame/Data/Editor/ConfigCodeGenerator.cs`（修改） | `TableClassNameFor` / `TableSourcePathFor`；`GenerateForRowTypeCore` 现在同批写行类 + 表类；菜单/批处理日志统计表类写入 |
| `ZFrame/Data/Editor/ConfigCodegenReport.cs`（修改） | 新增 `WroteTableFile` / `TableClassName`，报告文本带上表类行 |
| `ZFrame/Data/Editor/CsvImporter.cs`（修改） | 删除手写 `TableByCsvName`；改用 `ConfigTableRegistry`；新增程序化入口 `ImportRegisteredCsv(csvPath, tableAssetPath=null)`；**修掉 A-CONF-01 遗留的反射重载缺陷**（见 §3） |
| `ZFrame/Data/Config/Generated/EnemyConfigTable.cs`（迁移，原 `Config/` 下） | 生成形态；`.meta` 原样迁移，GUID `e18b8bc01fdb0aa4fad8e4d3adfac06b` 不变 → `Assets/Configs/CFG_EnemyTable.asset` 的 `m_Script` 引用零改动 |
| `Configs/CSV/wave.csv` + `Generated/WaveConfigRow.cs` + `Generated/WaveConfigTable.cs`（新增） | 演示表（全自动链路）：`Id:string,Count:int[1..100],Interval:float[0..60],Boss:bool`，3 行 |
| `ZFrame/Tests/Data/ConfigTableCodegenTests.cs`（新增） | 9 例：可复现、产物同步、注册自动发现、未知 csv、全链路资产+读取、坏表头零产物、命名约定 |

**注册键约定（三处同源，已写进注释）**：行 `Pascal(stem)+ConfigRow`、表 `Pascal(stem)+ConfigTable`、资产
`Assets/Configs/CFG_<Pascal(stem)>Table.asset`（`CsvExporter.TableAssetPathFor` 既有约定不变）。

### 2. 全自动链路实测（演示表 wave）

命令（Unity 批处理，单实例纪律：先删锁文件）：

```
Unity.exe -batchmode -projectPath .../UnityProject \
  -executeMethod ZFrame.Data.ConfigEditor.ConfigCodeGenerator.GenerateAllFromCommandLine
```

`TempLogs/aconf02_codegen1.log` exit 0 + `TempLogs/config_codegen_report.txt`：

```
Assets/Configs/CSV/enemy.csv
  写入: 否（无变化或出错）
  表类: EnemyConfigTable（无变化）
Assets/Configs/CSV/npc_personality.csv
  跳过: 表头无类型标注（裸表头 = 仅 CSV↔SO 导入的旧式表，非代码生成输入）
Assets/Configs/CSV/wave.csv
  写入: 否（无变化或出错）
  表类: WaveConfigTable（无变化）
表数: 3, 错误表数: 0 / 结果: PASS
```

> 首次「写入: 否」是因为 CSV 落盘后的资产导入阶段，`ConfigCsvPostprocessor` 已经在同一进程里先跑过一遍
> 并写出了 `WaveConfigRow.cs` / `WaveConfigTable.cs`——这恰好是「放 CSV → 自动出现」的实测证据；
> 随后 `executeMethod` 再跑一次确认幂等（详见 §4）。

「可读」证据（`ConfigTableCodegenTests.FullChain_WaveCsv_ImportRegistersAsset_AndConfigServiceReadsIt`，已全绿）：
`ImportRegisteredCsv` → 建 `__AutoTest_CFG_WaveTable.asset` → `Added=3` → `ConfigService.Load(table)` →
`Get<WaveConfigRow>("w3")` 断言 `Count=12 / Interval=4.0f / Boss=true`，用例收尾删除临时资产。

### 3. 施工中发现并修掉的既有缺陷（A-CONF-01 的）

- **现象**：全链路用例首跑红——`System.InvalidOperationException : not a generic method definition`
  （`TempLogs/runtests_editmode.xml` 内 `FullChain_…` 用例 stack trace 指向 `CsvImporter.cs:231`）。
- **根因**：A-CONF-01 的右键导入路径用
  `typeof(ScriptableObject).GetMethod("CreateInstance", new[]{typeof(Type)})` 取到的是**非泛型**
  `CreateInstance(Type)`，再 `.MakeGenericMethod(tableType)` 必然抛异常。
- **影响面**：`Assets/ZFrame/Import as Config Table` 菜单路径**从未被实跑**（A-CONF-01 验收 V2 只实测了批处理，
  菜单为读码确认），所以该缺陷一直潜伏；批处理与导入钩子（只生成源码）不受影响。
- **修法**：按 `IsGenericMethodDefinition && GetParameters().Length == 0` 找 `CreateInstance<T>()` 泛型重载；
  加注释记录此坑。修后全链路用例绿（1263→1264 通过数）。
- 归属说明：修复点在本任务重写的同一方法内（反射桥），**未改变 Import/Export/Validate 的语义**。

### 4. 可复现（同输入两次逐字节一致）

- 用例 `GenerateForRowType_EmitsRowAndTable_SecondRunIsByteIdentical`：首次 `WroteFile/WroteTableFile` 均为 true；
  第二次均为 false，两文件字节数组 `CollectionAssert.AreEqual` 相等，**mtime 不变**。
- 批处理二次跑 `TempLogs/aconf02_codegen2.log` exit 0，报告全部「无变化」；产物 md5：

```
b62195245b82a5852c42e4e5eb80d214  Generated/EnemyConfigRow.cs
e17775fbc833f09b5c1edf617b7969b3  Generated/EnemyConfigTable.cs
cdb151c537803160f0e9fbc7b6b9aead  Generated/WaveConfigRow.cs
b3d29b4da944484935db9fe9a8247910  Generated/WaveConfigTable.cs
```

- 防漂移守卫 `CommittedArtifacts_MatchGeneratorOutput_ForEnemyAndWave`：把 enemy/wave 生成到临时目录，
  与入库四个产物逐字节比较——**入库产物 = 生成器当前输出**（改 CSV 表头不重生成即红）。

### 5. 测试与校验（本任务全量亲跑）

| 段 | 结果 | 证据 |
|---|---|---|
| EditMode | **1264/1264 全绿**（0 failed / 0 skipped，14.4s） | `TempLogs/runtests_editmode.xml`（1255 + 本任务 9） |
| PlayMode | **115/115 全绿** | `TempLogs/runtests_playmode.xml` |
| arch | **PASS，asmdef 扫描 27 / 违规 0** | `TempLogs/arch_report.txt` |
| naming | **PASS，违规 0** | `TempLogs/name_report.txt` |
| 批处理生成 | exit 0 ×2，第二次全「无变化」 | `TempLogs/aconf02_codegen1.log` / `aconf02_codegen2.log` / `config_codegen_report.txt` |

- 既有 Config 用例（`CsvImporterTests` / `ConfigValidatorTests` / `ConfigServiceTests` / `ConfigRowCodegenTests`）零改动全绿。
- `npc_personality.csv`（裸表头、行类型在 `Game.Domain`）行为不变：生成器跳过、注册表无表类、导入链路不受影响。

### 6. 风险 / 遗留（如实）

1. **反射扫描成本**：`ConfigTableRegistry` 首次访问遍历全部已加载程序集（懒加载 + 缓存 + `Invalidate()`）。
   Editor-only，量级毫秒；不影响运行时。
2. **命名即约定**：注册键完全由类名推导（去 `ConfigTable` 后缀）。若有人手写一个不符合约定的表类，
   会被静默注册成别的键——`Conflicts` 只覆盖重键，不覆盖"名字怪"。建议后续在架构校验器加一条命名检查
   （本任务未做，避免扩大范围）。
3. **表类 `[CreateAssetMenu]` 与手写版菜单名一致**（`ZFrame/CFG Enemy Table`），但 Unity 菜单不持久化，
   不存在迁移问题；`CFG_EnemyTable.asset` 靠 .meta GUID 继续保持引用。
4. **`ConfigTableRegistry` 只认当前 AppDomain 已加载程序集**：新生成的表类需一次编译/导入后才可见
   （正常流程：CSV 导入 → 生成 → 编译 → 注册表下次访问即为新类型）。批处理 `GenerateAll` 与测试同进程时，
   新类型不在内存里——已有用例用「提交产物 + 编译后」路径覆盖，未构造同进程热注册场景。
5. **未做的事**：没有生成 `.asset` 实例（演示表只到"可建、可读"；提交二进制资产无必要）；
   没有给 `ConfigAssetSetup` 加通用批处理入口（它仍是 enemy 专用一次性工具，未改）。
6. 未改 Native / ZRender / asmdef / 看板；未 commit。工作区另有**先于本任务存在**的改动
   （`Docs/Parity/crowd_*`、`npc_attitude_*`、`utility_personality_*` PNG 与 `Native/tests/test_planet_channel.cpp`、
   `test_zfg_env.cpp`），非本任务触碰。

## 验收清单（施工代理建议，验收代理逐条跑并出证据）

- V1 范围：新增/修改文件 = 上表所列（含 6 个新 .meta 与 1 个迁移 .meta）；无 asmdef 增删（27）；`CsvExporter.cs` / `ConfigValidator.cs` 零改动。
- V2 表类生成：跑批处理 → `WaveConfigTable.cs` 带 `<auto-generated>`、源表路径、`[CreateAssetMenu]`；坏表头零产物（用例）。
- V3 注册自动化：`grep -n "TableByCsvName"` 应无命中；`ConfigTableRegistry` 反射发现 enemy/wave 两表（用例 + 亲跑）。
- V4 迁移：GUID `e18b8bc01fdb0aa4fad8e4d3adfac06b` 新旧一致；`CFG_EnemyTable.asset` 引用不破；`EnemyConfigTableInspector` 仍能挂上。
- V5 全链路：`FullChain_…` 用例亲跑（Added=3、字段值断言、临时资产清理）。
- V6 可复现：两次生成字节一致 + mtime 不变 + 批处理第二次全「无变化」，md5 与 §4 对照。
- V7 全量：EditMode 1264 / PlayMode 115 / arch 27-0 / naming 0 亲跑。
- V8 规范：新增/修改文本 UTF-8 无 BOM / LF；生成物入库。
- V9 缺陷归属：核对 §3 的反射重载缺陷确属 A-CONF-01 遗留（`git show d15faad:...CsvImporter.cs` 可见 `GetMethod(..., new[]{typeof(Type)}).MakeGenericMethod`）。

## 验收结论

**判定：不通过（有阻塞项）**——生成/注册/可复现三项独立复现为真，但**「实跑菜单链路确认不再抛」失败**：
`Assets/ZFrame/Import as Config Table` 在「资产尚不存在」的表上仍然抛异常，**并会清空同名的既有表资产**。
施工的「已修」只修了链路里的第一个坑（MakeGenericMethod），第二个坑（默认资产路径含 `..`）被用例绕过了 → **假绿**。

（验收子代理，2026-11-15 11:05–11:12 本地亲跑；未采信施工数字）

### A. 基线四段（本次亲跑）

| 段 | 结果 | 证据 |
|---|---|---|
| EditMode | **1264/1264 passed**（本任务 9 例全 Passed） | `TempLogs/runtests_editmode.xml`（`start-time 02:58:03Z`；末次 `03:12` 复跑同 1264） |
| PlayMode | **115/115 passed** | `TempLogs/runtests_playmode.xml` |
| arch | **PASS，27 / 0** | `TempLogs/arch_report.txt` |
| naming | **PASS，0** | `TempLogs/name_report.txt` |

### B. 逐项

**V-1 可复现（PASS）**——同一 CSV 连跑两次批处理生成（`ConfigCodeGenerator.GenerateAllFromCommandLine`，两次 exit 0）：

```
acc_codegen_run1.log / acc_codegen_run2.log（本机实测）
四产物 md5 两次均与入库一致：b6219524…/e17775fb…/cdb151c5…/b3d29b4d…（与施工 §4 逐位同）
报告两次均「写入: 否（无变化）」「表类: …（无变化）」「结果: PASS」，diff 只差首行时间戳
```
证据：`TempLogs/acc_codegen_report1.txt`、`acc_codegen_report2.txt`。同输入 mtime 不动（用例
`GenerateForRowType_EmitsRowAndTable_SecondRunIsByteIdentical` 亦在本次 1264 内通过）。

**V-2 注册自动化（PASS）**——`grep -n "TableByCsvName"` 全库仅剩 `ConfigTableRegistry.cs:13` 的注释一处，手写字典已删。
反射发现与读取我另做了一次独立程序化调用（不依赖施工用例）：
`CsvImporter.ImportRegisteredCsv("Assets/Configs/CSV/wave.csv", "Assets/Configs/__AccNorm_CFG_WaveTable.asset")`
→ `Added=3`、`rows=3`；施工用例 `FullChain_…` 亦断言 `w3: Count=12 / Interval=4.0 / Boss=true` 且 Passed。
证据：`TempLogs/acc_menu_probe2.log`。

**V-3 菜单链路（FAIL，阻塞）**——我用临时探针实跑了**真正的菜单项**
（`EditorApplication.ExecuteMenuItem("Assets/ZFrame/Import as Config Table")`，Selection 指向 `wave.csv`），
不是只调 `ImportRegisteredCsv`：

```
waveAssetPath=Assets/Configs/CSV/../CFG_WaveTable.asset      ← CsvExporter.TableAssetPathFor 产出非规范路径
enemyAssetPath=Assets/Configs/CSV/../CFG_EnemyTable.asset
load enemy via ..= False                                      ← 资产明明存在，带 .. 的路径却 Load 不到
load wave  via ..= False
CreateAsset(..) probe: afterCreateAsset exists=True objNull=True  ← 文件建出来了，但传入的对象被销毁
executeMenuItem=True  rows=0                                  ← 菜单「成功」，enemy 表却被清空
executeMenuItem=True  rows=NULL                               ← wave 根本建不起来
ImportRegisteredCsv(wave, 规范化路径): Added=3 rows=3          ← 只换掉路径就全通
```

Unity 自己的日志给出异常本体（`TempLogs/acc_menu_probe_unity.log:828-836`）：

```
ArgumentNullException: Value cannot be null. Parameter name: target
  at UnityEditor.EditorUtility.SetDirty(UnityEngine.Object)
  at ZFrame.Data.ConfigEditor.CsvImporter.Import[TRow](...)  [CsvImporter.cs:181]
  at ZFrame.Data.ConfigEditor.CsvImporter.ImportByTableType(...) [CsvImporter.cs:253]
  at ZFrame.Data.ConfigEditor.CsvImporter.ImportSelectedCsv()  [CsvImporter.cs:275]
```

**因果链（读码 + 实测互证）**：`TableAssetPathFor`（`CsvExporter.cs:22-28`）用 `{dir}/../CFG_XTable.asset` 拼路径
→ `AssetDatabase.LoadAssetAtPath` 对该路径恒返回 null（连已存在的 enemy 资产也 Load 不到）
→ 进「新建」分支 `CreateAsset`，Unity 建出文件但**销毁传入对象**（`objNull=True`）
→ `ImportRows` 作用在已销毁对象上、`EditorUtility.SetDirty(table)` 收到 fake-null → 抛 `ArgumentNullException`。
菜单的 `try` 在 Unity 内部被吞成一条 Console error，UI 上「看起来点了没反应」。

**副作用（数据损坏，已恢复）**：对 `enemy.csv` 走该菜单会先 Load 失败、再用空表 `CreateAsset` 覆盖
`Assets/Configs/CFG_EnemyTable.asset`：实测 **781 B → 415 B、`_rows: []`、`.meta` 同时被改写**。
我已 `git checkout --` 还原两文件并核对 `m_Script guid: e18b8bc01fdb0aa4fad8e4d3adfac06b` 未变，`git status` 已干净。

**为什么是用例抓不到的假绿**：`ConfigTableCodegenTests.FullChain_…`（:179）显式传了
`"Assets/Configs/__AutoTest_CFG_WaveTable.asset"` 这个**规范化路径**，绕过了 `TableAssetPathFor`；
菜单路径与程序化入口共用 `ImportByTableType`，但**默认路径解析这一环从未被执行**。
施工 §3「修后全链路用例绿」只证明 `CreateInstance<T>()` 泛型重载那一处修对了（这点我确认成立：调用已越过 :253 进入 `Import`）。

**归属**：`TableAssetPathFor` 的 `..` 拼法**是 A-CONF-01 遗留**（`git diff HEAD -- CsvImporter.cs` 中旧菜单同样调用
`CsvExporter.TableAssetPathFor(csvPath)`），不是本任务引入的回归；但本任务把「菜单已实跑、不再抛」写成了结论，
这一条与事实不符。

**最小修法（建议，本次未改码）**：`TableAssetPathFor` 用 `Path.GetFullPath`/`Directory.GetParent(dir)` 归一化，
或直接 `$"{Path.GetDirectoryName(dir)}/CFG_{pascal}Table.asset"`；另建议加一条用例覆盖「默认路径」分支，
并给 `Import<TRow>` 在 `CreateAsset` 后补一次 `table == null` 复检（销毁态要能显式报错，不能落到 SetDirty 上）。

### C. 规范 / 范围

- 新增/修改文件与施工表一致；`CsvExporter.cs`、`ConfigValidator.cs` 零 diff；无 asmdef 增删（arch 27）。
- 新文件均无 BOM（`ConfigTableRegistry.cs`/`ConfigTableCodegen.cs` 首字节 `236966`，其余 `2f2f20`）；
  工作区 CRLF、`.gitattributes` 归一为 LF（入库字节为 LF）。
- `CFG_EnemyTable.asset` GUID `e18b8bc0…` 保持。

### D. 受限 / 说明

1. 为实跑菜单我放了临时探针 `Assets/ZFrame/Data/Editor/__AccMenuProbe.cs`（含 .meta），跑完已删除；
   `git status` 复核无残留。探针日志：`TempLogs/acc_menu_probe2.log`、`acc_menu_probe_unity.log`。
2. 无法在 batchmode 下驱动真实右键 UI，等价性是「菜单项字符串 + 校验委托 + Selection」三者齐备，等价于用户右键点该菜单。
3. 施工 §6-4「注册表只认已加载程序集」保留意见成立，本次未构造热注册场景。

## 返工（2026-11-15）

> 起因：验收 §B V-3 实测「`Assets/ZFrame/Import as Config Table` 在资产尚不存在的表上仍抛异常，并清空同名既有表」。
> 两个真 bug（`..` 路径 / CreateAsset 先建后毁）+ 一个假绿用例。本节只记返工内容，前文保留不改。

### 1. Bug ① 路径未规范化（`CsvExporter.TableAssetPathFor`）

**修法**：新增 `CsvExporter.NormalizeAssetPath(path)`——

- `Path.GetFullPath` 折叠 `.`/`..`/重复分隔符，统一正斜杠；
- 相对路径按**项目根**（`Application.dataPath/..`）解析，不用进程 CWD（batchmode 下 CWD 不可靠）；
- 再相对化回项目根，因此结果必然形如 `Assets/...`；恰好等于项目根时返回 `""`。

`TableAssetPathFor` 入参、出参各过一遍归一化，拼接仍是既有约定（`<CSV 目录的父目录>/CFG_<Pascal>Table.asset`），
所以对外语义只有「路径变规范」这一处变化：`Assets/Configs/CSV/enemy.csv` → `Assets/Configs/CFG_EnemyTable.asset`。

**双保险**：`CsvImporter.Import` 入口也归一化一次 `tableAssetPath`。即使外部（旧调用点/脚本）传了带 `..` 的路径，
也会落到同一个资产上，不会再出现「Load 不到 → 当成不存在 → 覆盖」。

覆盖用例（`ZFrame/Tests/Data/CsvImporterTests.cs`）：

| 用例 | 断言 |
|---|---|
| `TableAssetPathFor_FollowsCfgConvention` | 旧期望 `CSV/../CFG_EnemyTable.asset` 改成正典路径 + 不含 `..` |
| `TableAssetPathFor_IsCanonical_ForExistingAndMissingAsset` | enemy（**已存在**）与 wave（**不存在**）两路都：以 `Assets/` 开头、无 `..`/`.`/`//`、正斜杠、幂等；enemy 路径能直接 `LoadAssetAtPath` 到 |
| `NormalizeAssetPath_CollapsesDotSegments_AndStaysProjectRelative` | `..`、反斜杠、`.`+`//` 三种写法归一到同一路径；`Assets/..` → `""`；`null` 透传 |

### 2. Bug ② CreateAsset 破坏性（`CsvImporter.Import`）

**选定方案：存在则就地更新**（不选 `DeleteAsset`+`CreateAsset`）。理由：后者会重签 GUID、重写 `.meta`，
场景/预制体/Inspector 上对表的引用全部断掉，`CFG_EnemyTable.asset` 的既有身份也会丢；而「就地更新」只是
`LoadAssetAtPath` + `ImportRows`（按 Id 更新/追加）+ `SetDirty` + `SaveAssets`，GUID 与引用零变化。

落地为三道闸：

1. `LoadAssetAtPath<ConfigTable<TRow>>` 命中 → 原地更新（正常路径）；
2. 命中不到、但该路径上已有**别的** ScriptableObject → 抛
   `表资产 X 已存在但类型不是 ConfigTable<TRow>（实际 Y），拒绝覆盖`，绝不 `CreateAsset` 压过去；
3. 确实新建 → `CreateAsset` 之后立刻复检 `table == null`（Unity 在路径冲突等情况下会销毁传入对象，
   此时是 fake-null），显式抛「CreateAsset 后表对象已失效」——不再让它一路走到
   `EditorUtility.SetDirty` 变成验收看到的那句 `ArgumentNullException: ... target`。

覆盖用例（`ZFrame/Tests/Data/ConfigTableCodegenTests.cs`）：
`FullChain_WaveCsv_MenuEntry_DoesNotWipeExistingTable`——预置一张 `CFG_WaveTable`（1 行 `keep_me/77/2.5/true`）
→ 菜单导入同一 CSV → 断言资产仍可 Load、**GUID 不变**、行数 4、`keep_me` 三字段原值、csv 的 `w3` 已写入。

### 3. 拆假绿：`FullChain` 改走真菜单入口

- **删掉**：`FullChain_WaveCsv_ImportRegistersAsset_AndConfigServiceReadsIt`（自己传
  `Assets/Configs/__AutoTest_CFG_WaveTable.asset`，`TableAssetPathFor` 从未被执行）。
- **换成**（不出现任何手写资产路径，路径只从产品代码读回来做断言）：

| 新用例 | 做什么 |
|---|---|
| `FullChain_WaveCsv_MenuEntry_CreatesAssetAtDefaultPath_AndConfigServiceReadsIt` | `Selection.activeObject = wave.csv` → `EditorApplication.ExecuteMenuItem("Assets/ZFrame/Import as Config Table")`（返回值必须 true）→ 资产在默认路径上出现、3 行、`ConfigService.Get("w3")` = 12/4.0/true |
| `FullChain_WaveCsv_MenuEntry_DoesNotWipeExistingTable` | 同上入口，预置资产不丢（见 §2） |
| `ImportRegisteredCsv_DefaultPath_IsNormalized_AndCreatesAsset` | 程序化入口**不传路径**也落到正典默认位置 |

**非假绿的证据（变异跑）**：把 `TableAssetPathFor` 临时改回旧的 `return $"{dir}/../CFG_..."`（其余不动），
只跑两个菜单用例：

```
TempLogs/aconf02_rework_mutant.xml  total="2" passed="0" failed="2"
  FAIL 菜单默认路径必须是规范化路径
       Expected: "Assets/Configs/CFG_WaveTable.asset"
       But was:  "Assets/Configs/CSV/../CFG_WaveTable.asset"
  FAIL 既有表资产必须仍然存在且可 Load（不得先建后毁）   Expected: not null  But was: null
```

即：旧写法下这两个用例都是红的（含「既有表被弄没」这一条），修复后绿 → 用例确实覆盖了默认路径与破坏性。
变异文件已按备份逐字节还原（`md5sum -c` = `9ee1b50bea10be98540fe6121532f60d` OK），工作区无残留资产。

### 4. 菜单链路实跑证据（`TempLogs/runtests_editmode.log`，修复版最终一轮）

```
797: [CsvImporter] Assets/Configs/CSV/wave.csv -> Assets/Configs/CFG_WaveTable.asset: added=3, updated=0, missingInCsv=0, errors=0
861: [CsvImporter] Assets/Configs/CSV/wave.csv -> Assets/Configs/CFG_WaveTable.asset: added=3, updated=0, missingInCsv=1, errors=0
923: [CsvImporter] Assets/Configs/CSV/wave.csv -> Assets/Configs/CFG_WaveTable.asset: added=3, updated=0, missingInCsv=0, errors=0
```

- 目标路径为 `Assets/Configs/CFG_WaveTable.asset`（无 `..`）——由产品代码算出，用例未传；
- 861 行是「预置表」用例：`missingInCsv=1`（预置的 `keep_me` 不在 CSV 里，按既有语义保留不删）→ 行数 4；
- 全日志 `ArgumentNullException` 命中 **0** 次。

### 5. 全量四段（本次亲跑，修复版）

| 段 | 结果 | 证据 |
|---|---|---|
| EditMode | **1268/1268 passed**，0 failed / 0 skipped，12.4s | `TempLogs/runtests_editmode.xml`（`start-time 2026-11-15 03:31:21Z`） |
| PlayMode | **115/115 passed** | `TempLogs/runtests_playmode.xml` |
| arch | **PASS，asmdef 27 / 违规 0** | `TempLogs/arch_report.txt` |
| naming | **PASS，违规 0** | `TempLogs/name_report.txt` |

1268 = 1264 − 1（删掉的假绿用例）+ 3（菜单/默认路径新用例）+ 2（路径规范化新用例）。
上一轮 1264 里本任务的 11 例全部仍绿（含可复现 / 防漂移 / 注册发现）。

### 6. 范围与规范

- 改动文件：`ZFrame/Data/Editor/CsvExporter.cs`、`ZFrame/Data/Editor/CsvImporter.cs`、
  `ZFrame/Tests/Data/CsvImporterTests.cs`、`ZFrame/Tests/Data/ConfigTableCodegenTests.cs`、本日志。
  未动 asmdef（27）、未动 `ConfigValidator.cs`、未动 ZRender/Native 与生成器产物。
- 四个改动文件均 UTF-8 无 BOM + LF（首字节 `236966`/`757369`/`2f2f20`，`\r` 计数 0）。
- 未 commit、未改看板。
- 环境注记：本轮首次启动被 `HTTPS_PROXY`/`https_proxy` 大小写重复变量挡住（`run_tests.ps1` 的 Start-Process
  环境块冲突）；删掉小写变体后正常，属机器环境问题非代码问题。



## 复验结论（2026-11-15 接管轮）

**判定：通过（可闭环）**——上一轮 FAIL 的三条证据（真菜单抛异常 / 清空既有表 / 用例假绿）本轮全部**自行实跑复验为「已修复」**，
且变异回旧写法能重新变红并清楚暴露「既有表被清空」。全部证据为本轮亲跑，未采信施工与上一轮数字。

### 1. 真菜单实跑（PASS）

临时探针 `__AccConf02Probe`（跑完已删）只调 `EditorApplication.ExecuteMenuItem`，Selection 指向 `Assets/Configs/CSV/wave.csv`：

```
TableAssetPathFor(wave,missing)=Assets/Configs/CFG_WaveTable.asset
ExecuteMenuItem("Assets/ZFrame/Import as Config Table") returned True
asset at Assets/Configs/CFG_WaveTable.asset: rows=3
ConfigService Count=12 / Interval=4.0 / Boss=true / rows=3
== UNITY ERROR/EXCEPTION LOGS == (none)
RESULT: PASS
```

- 目标路径由产品代码给出，**不含 `..`**，以 `Assets/` 起；
- 菜单命中 3 次，日志均为 `wave.csv -> Assets/Configs/CFG_WaveTable.asset`；整份 Unity 日志 `ArgumentNullException` 命中 **0**。
- 证据：`TempLogs/acc2_probe.log`（复绿轮同内容副本 `acc2_probe_restored.log`）、`TempLogs/acc2_probe_unity1.log:824/854/882`、`TempLogs/acc2_restored_unity.log:759/789/817`。

### 2. 破坏性回归（PASS，自己造）

探针现场预置 `CFG_WaveTable`（1 行 `keep_me/77/2.5/true`，CSV 中不存在），再走同一菜单：

```
seeded guid=9ed3c76a3f432084b82684b861b2811e bytes=539 rows=1
ExecuteMenuItem returned True
after guid=9ed3c76a3f432084b82684b861b2811e bytes=902 rows=4
PASS guid unchanged / row count == 4 / keep_me preserved
PASS keep_me values intact [Count=77 Interval=2.5 Boss=True] / w3 imported [Count=12]
```

产品日志同轮给出 `missingInCsv=1`（预置行按既有语义保留）：`acc2_restored_unity.log:789`、`TempLogs/acc2_restored_tests.log:914`。
GUID 不变 = 就地更新（不是 Delete+Create），与返工方案一致。

### 3. 路径断言（PASS，「存在 / 不存在」两态）

| 情形 | `TableAssetPathFor` 返回 | 断言 |
|---|---|---|
| 资产已存在（enemy） | `Assets/Configs/CFG_EnemyTable.asset` | 无 `..`、`Assets/` 起、可直接 `LoadAssetAtPath`（True） |
| 资产不存在（wave，删除后） | `Assets/Configs/CFG_WaveTable.asset` | 无 `..`、等于约定路径 |
| 菜单刚建出（wave，已存在） | `Assets/Configs/CFG_WaveTable.asset` | 与「不存在」时逐字符相同（幂等） |

证据：`acc2_probe.log` §PATH 三段（`PASS enemy path canonical` / `PASS missing path equals expected` / `PASS existing path == missing path`）。

### 4. 变异（红 → 逐字节恢复 → 复绿）

- 备份：`TempLogs/acc2_CsvExporter.cs.orig`，sha256 `0671a70556790d0dd9ce34dc44e03c065ed9b45a47080dc237e45e2f21e45754`。
- 变异：把 `CsvExporter.NormalizeAssetPath` 方法体整体改回 `return path;`（不再折叠 `..`）。
- **红（用例级，`ConfigTableCodegenTests` 11 例）**：`total="11" passed="8" failed="3"`，Unity exit=2：

```
FAIL FullChain_WaveCsv_MenuEntry_CreatesAssetAtDefaultPath_AndConfigServiceReadsIt
     Expected: "Assets/Configs/CFG_WaveTable.asset"
     But was:  "Assets/Configs/CSV/../CFG_WaveTable.asset"
FAIL FullChain_WaveCsv_MenuEntry_DoesNotWipeExistingTable
     既有表资产必须仍然存在且可 Load（不得先建后毁）   Expected: not null  But was: null   ← 既有表被清空
FAIL ImportRegisteredCsv_DefaultPath_IsNormalized_AndCreatesAsset
     不传路径时必须建在 TableAssetPathFor 的默认（规范化）位置   Expected: not null  But was: null
```

变异版日志亦直接可见坏路径：`[CsvImporter] Assets/Configs/CSV/wave.csv -> Assets/Configs/CSV/../CFG_WaveTable.asset`（`acc2_mutant_tests.log:793/846`）。
（同变异下我的探针另跑一轮：Unity exit=1、11 条断言失败；预置表 539 B/1 行 → 414 B/3 行、GUID `2f148b9e…`→`42687e42…`、`keep_me` 消失——即「清空既有表」。该轮探针日志被随后复绿轮覆盖，仅保留此观察记录。）

- **恢复**：从备份 `cp` 回去，sha256 = `0671a705…754`（与备份逐字节一致），`git diff --stat` 仍只是返工原有 67+/4−，本轮**未改产品代码**。
- **复绿**：恢复后同过滤 11/11 passed（`TempLogs/acc2_restored_tests.xml`），日志三行导入路径均 `Assets/Configs/CFG_WaveTable.asset`（无 `..`），含 `missingInCsv=1`。

### 5. 四段全量（本次亲跑，恢复态）

| 段 | 结果 | 证据 / 本轮时间锚 |
|---|---|---|
| EditMode | **1268/1268 passed**，0 failed / 0 skipped | `TempLogs/runtests_editmode.xml`（`start-time 2026-11-15 03:37:03Z`） |
| PlayMode | **115/115 passed** | `TempLogs/runtests_playmode.xml`（`start-time 03:37:56Z`） |
| arch | **PASS，asmdef 27 / 违规 0** | `TempLogs/arch_report.txt`（`2026-11-15 11:41:22`） |
| naming | **PASS，违规 0** | `TempLogs/name_report.txt`（`11:41:51`） |

汇总：`TestReport.txt` `Verdict: ALL PASS (4 passed, 0 skipped, 0 failed)`（命令：`Tools/run_tests.ps1 -Segments editmode,playmode,arch,naming`）。
注：脚本对旧 `results.xml` / 报告的 safe-delete 报 `trash-failed`，但 editmode/playmode 走「fingerprint 变化才算本轮证据」闸（日志 `evidence: results.xml written by this run`），
arch/naming 报告时间戳为本轮 11:41，判为可信；不影响计数。

### 6. 规范 / 清理

- 本轮**未改任何产品代码**（变异已逐字节还原，sha 前后一致）；未 commit。
- 临时探针 `Assets/ZFrame/Data/Editor/__AccConf02Probe.cs`（含 .meta）已删除；`find Assets -name "__Acc*"` 无命中。
- 测试资产已清：`Assets/Configs/` 只剩 `CFG_EnemyTable.asset` / `CFG_NpcPersonality.asset`，无 `CFG_WaveTable.asset` 残留。
- 无新增 PNG/截图；`git status` 与接管前一致（仅返工文件 + 既有他人改动）。
- 结束确认 `Unity` 进程 0、`Temp/UnityLockfile` 不存在。

### 7. 受限

1. batchmode 无法驱动真实右键 UI，「菜单项字符串 + 同款 Selection + `ExecuteMenuItem` 返回 true」为等价路径（与上一轮判定口径相同）。
2. 变异证据以 `ConfigTableCodegenTests` 三条失败为文件级凭据；探针级变异轮次未留文件（被复绿轮覆盖），已在 §4 注明。
3. 本轮只验四段（editmode/playmode/arch/naming），未跑 config/bench/player/native（不在本任务口径内）。

### 8. 可闭环

三条 FAIL 证据逐条对应已消：菜单不再抛（0 次 ArgumentNullException）、目标路径规范化（无 `..`）、既有表不丢（GUID/行/值全保，`missingInCsv=1`）、
假绿用例已换成真菜单入口且变异可红。**建议闭环 A-CONF-02**；仅余上一轮已登记的观察项（注册表热加载、命名约定校验）作为后续非阻塞建议。

（验收子代理，2026-11-15 接管轮；11:24–11:45 本地亲跑，未采信施工数字）
