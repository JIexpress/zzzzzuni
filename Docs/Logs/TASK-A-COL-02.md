# TASK-A-COL-02 · GLM 内容五数据源走 ConfigService CSV 管线 + 数据驱动建局测试

> 简报（主控写）｜2026-11-22｜施工：general-purpose 施工子代理（2026-11-22）｜验收：<待填>｜前置：A-COL-01（16afa34）

## 一、任务简报（唯一需求来源）

### 背景

A-COL-01 把 ColonyCore 模拟核心迁入 `Game/Domain/Colony/`，V1-V7 垂直闭环测试用**内存 fixture**（硬编码 3 地点/8 NPC/2 政策）。真实内容数据在 `C:\Users\1\WorkBuddy\杂项\参考资料\GLM产出\`（100 NPC 卡/120 地点/40 政策/12 支线/300 台词，2026-11-22 机检验收 40 项全 PASS）。

主控已完成 md/JSON → CSV 转换，五份 CSV 在 **`_asset_staging/colony_csv/`**（不入库，施工时复制到配置资产目录）：

| 文件 | 行数 | 列 |
|---|---|---|
| npcs.csv | 100 | id,name,age,job,origin,want,wont,conflict,secret,speech,relation_to_power |
| places.csv | 120 | id,name,type,population,industry,blurb,decay,ongoing |
| policies.csv | 40 | id,name,desc,benefit,cost,who_unhappy,difficulty,is_trap,is_dark_medicine |
| quests.csv | 12 | name,trigger,involved,core,option_a..c,aftermath,delay_months,world_advances |
| dialogue.csv | 300 | role,topic,line |

### 交付物

1. **五张配置表走 ZFrame ConfigSystem**（先读 `Docs/Logs/TASK-A-CONF-02.md` 全文——那是"表类零手写：行类+表类同批生成+反射注册"的既有流程与坑位记录；再读 `ZFrame/Data/` 的 ConfigService/CsvImporter/ConfigWorkbench 源码）：
   - `ColonyNpcsTable`（100 行）、`ColonyPlacesTable`（120）、`ColonyPoliciesTable`（40）、`ColonyQuestsTable`（12）、`ColonyDialogueTable`（300）
   - CSV 资产与 SO 放置位置、导入路径、菜单链路一律按 A-CONF-02 的约定与坑位（路径归一、存在则就地更新不换 GUID）
   - `is_trap/is_dark_medicine/decay/ongoing/world_advances` 落为 bool 字段；`delay_months` int
2. **`ColonyDataLoader`**（`Game/Domain/Colony/` 新增）：从 ConfigService 读五表 → 组装 GameWorld（BindRegions=places 人口前三建 RegionState+Settlement（type 含"城"0.15/"堡"-0.15/"港"0.10/其他 0.05，decay -0.05，ongoing -0.10）→ RegisterNamed=npcs 全量按 origin 分文化（船民 Rural/难民 Mix(Rural,Doctrine,0.5)/地球 Atomized/其余 Mix(Rural,Atomized,0.5)，MF 随机、Quirk=speech 前 12 字）→ PolicyLibrary/Quests.Library（TKind：name=="坟山北坡" 用 GrievanceAbove 0.35 主地区[1]，其余 DayReached 15+i*8；DelayDays=delay_months*30；WorldAdvances=world_advances）/Dialogue.Load）。逻辑与 A-COL-01 测试 fixture 及 `ColonyCore/App/Program.cs` 建局段一致，只是数据来源换成真实表。**文化/人格映射常量写进 ColonyDataLoader 并注释来源（NPC规格第三部分 §21 + SeedLoader 启发式）**。
3. **`ColonyDataDrivenTests`**（`Game/Tests/Domain/Colony/` 新增，NUnit EditMode）：
   - `D1_LoadAllFiveTables`：五表行数=100/120/40/12/300；npcs[0] 的 name/job 非空；policies 中 is_trap 计数=5、is_dark_medicine 计数=5；dialogue 的 role 分布 {A,C,D,E}(+B 若有)=每 role 60 条。
   - `D2_BuildWorldFromData`：DataLoader 建局 → Named.Count==100、Regions.Length==3、Quests.Library.Count==12；断言"坟山北坡"在库里且 DelayDays>0。
   - `D3_RealMerchantEndToEnd`：从表里找 IsMerchant（走私贩/废料回收商/赌场老板）→ Grievance=0.6 → ChooseProtest 含 InflatePrice → Price multiplier ∈(1.0,1.6]。
   - `D4_DialogueSpeakFromTable`：任一映射到 role 的 NPC Speak 返回非空且带 tone 标签（`[` 开头）。
   - **不改动 A-COL-01 的既有 V1-V7 测试**（保持独立：内存 fixture 用例证明逻辑、数据驱动用例证明管线）。
4. **白名单**：`Assets/Game/Domain/Colony/**`、`Assets/Game/Tests/Domain/Colony/**`、配置资产目录（按 ConfigSystem 约定，含 .meta）、`Game.Domain.Tests.asmdef`（如需）、`ZFrame/Editor/ArchitectureValidator.cs`（**仅当**新 asmdef/表类需要矩阵条目——照 A-COL-01 主控批注先例，最小 diff）、`Docs/Logs/TASK-A-COL-02.md`、`_asset_staging/colony_csv/`（CSV 源，若决定入库）。不 commit、不推远程。

### 自测要求（施工代理必跑）

1. 批处理编译 exit 0 零 CS error（含 SO/表类生成物）。
2. EditMode 全量：failed=0 且 total ≥ 1283+新增用例数（D1-D4 ≥4 → ≥1287）。
3. 架构校验 exit 0（矩阵条目如需追加照先例最小 diff）。
4. 变异自证：改坏 D1 一个断言（如 100→99）→ RED → 还原 sha256 一致 → GREEN，log 留证。

### 验收清单 V1-Vn（验收代理独立亲证）

- V1 编译零 CS error（自跑）。
- V2 EditMode 全量 failed=0，total 记录确切值且 ≥1287；V1-V7 旧用例不回归（xml 点名至少 V1_V2_V5 仍 Passed）。
- V3 架构校验 exit 0；D1-D4 逐条 xml Passed。
- V4 变异复红（验收自选与施工不同位置，还原 sha256 一致）。
- V5 五表行数与主控 CSV 一致（100/120/40/12/300）；抽 3 个 NPC 字段与 CSV 源逐字一致（防转码损坏）。
- V6 git status 改动仅限白名单。

## 二、施工报告

### 1. 摘要

- **五张 GLM 内容表全部走通 A-CONF-02 既有管线**：注解表头 CSV 落 `Assets/Configs/CSV/`（npcs/places/policies/quests/dialogue）→ 导入钩子同批生成行类+表类共 10 个产物（`Assets/ZFrame/Data/Config/Generated/`）→ **真菜单链路**（Selection 指向 csv + `EditorApplication.ExecuteMenuItem("Assets/ZFrame/Import as Config Table")`）导入为 5 个 `CFG_*Table.asset`（默认路径由 `CsvExporter.TableAssetPathFor` 推导，added=行数、errors=0）→ `ConfigService` 五表可读。
- **`ColonyDataLoader`**（`Game/Domain/Colony/ColonyDataLoader.cs` 新增）：`BuildWorld(seed, ColonyWorldData)` 逐式复刻 `ColonyCore/App/Program.cs` 建局段——places 人口前三建 RegionState+Settlement（态度常量：type 含「城」+0.15 /「堡」-0.15 /「港」+0.10 /其他 +0.05，decay -0.05，ongoing -0.10；聚落忠诚 0.55 / 怨气 0.35+0.05+0.10）；100 卡全量按 SeedLoader 启发式映射人格/忠诚/信念契约（24 职业信条表、relation_to_power 四态、每卡 `Rng(Hash(seed,i))`），origin 分文化（船民 Rural / 难民 Mix(Rural,Doctrine,0.5) / 地球 Atomized / 其余 Mix(Rural,Atomized,0.5)，来源注释=NPC规格第三部分 §21 + SeedLoader）、MF 共享流 `Rng(seed^0xC0FFEE)` 六维 0.2~0.9、Quirk=speech 前 12 字、背景层 NpcDirectory(100_000)；PolicyLibrary=40 条、Quests.Library=12 条（**「坟山北坡」特例 GrievanceAbove 0.35 @ 主地区[1]**，其余 DayReached 15+i*8；DelayDays=delay_months×30；WorldAdvances=world_advances）、Dialogue.Load 300 条。
- **架构登记（简报内部约束的消解，待主控确认）**：`Game.Domain.Colony` 按 A-COL-01 矩阵**只引用 ZFrame.Core、noEngineReferences=true**，编译上不可能直接引用 ZFrame.Data（ConfigService/ConfigTable 均在 UnityEngine 侧）。落地为：ColonyDataLoader 收 **plain 输入行**（`ColonyNpcInput` 等五个 DTO，与生成行类字段 1:1，同文件定义于 Colony 内）；**ConfigService→DTO 的 1:1 投影放测试侧 `ColonyConfigAdapter`**（Game.Domain.Tests 本就引用 ZFrame.Data+Game.Domain.Colony，零 asmdef 改动）。数据流仍是 ConfigService 五表 → GameWorld；生产侧接线时由宿主程序集（如 Game.Bootstrap）承担同款投影（届时矩阵再加引用条目，不在本任务范围）。
- **`ColonyDataDrivenTests`**（`Game/Tests/Domain/Colony/ColonyDataDrivenTests.cs` 新增，D1-D4 全真实断言，V1-V7 零改动）：
  - **D1**：五表行数=100/120/40/12/300；npcs[0] 逐字抽查（郑大镐/矿工/54/殖民地第二代/怀疑）；is_trap=5、is_dark_medicine=5 且不兼任；台词 role A/B/C/D/E 各 60。
  - **D2**：建局 Named.Count==100、Regions.Length==3、Quests.Library.Count==12；前三地区=双河城/残垣城/青石城（含聚落人口 86000）；坟山北坡在库、DelayDays=180>0、GrievanceAbove 0.35@主地区[1]；日程解锁 11 条 15→103 天；DialogueBank.Total==300；难民 21 张 Culture.Target==Doctrine、船民 23 张 ==Person。
  - **D3**：首个 IsMerchant=id 57 白汀（走私贩，来自 CSV）→ Grievance=0.6 → ChooseProtest 含 InflatePrice → Price 倍率 ∈(1.0,1.6] 且带假理由。
  - **D4**：矿工（JobToRole→C）Speak 返回非空、`[` 开头 TU_* tone 标签，正文剥离口癖后缀后**逐字命中台词表 C/寒暄 池**（10 条）。
- 自测四段全绿（数字见 §3）：编译 exit 0、EditMode 全量 **1287/1287/0**（两次：变异前 + 还原后）、arch exit 0（**矩阵零 diff**，28 asmdef/14 条目/0 违规）、变异自证 RED→还原 sha256 一致→GREEN→全量复绿。

### 2. 交付清单

| 文件 | 角色 |
|---|---|
| `Assets/Configs/CSV/npcs.csv`（+meta） | 五表 CSV：主控 staging 换注解表头（`Id:string,Name:string,Age:int,…,RelationToPower:string`），数据行与源逐字一致 |
| `Assets/Configs/CSV/places.csv`（+meta） | 同上（`Id:string,…,Population:int,…,Decay:bool,Ongoing:bool`）；bool 列 0/1→false/true（见 §4-1） |
| `Assets/Configs/CSV/policies.csv`（+meta） | 同上（`…,IsTrap:bool,IsDarkMedicine:bool`）；bool 列同 §4-1 |
| `Assets/Configs/CSV/quests.csv`（+meta） | 补首列 Id（Q01..Q12，管线契约）+ 注解表头；world_advances 0/1→false/true |
| `Assets/Configs/CSV/dialogue.csv`（+meta） | 补首列 Id（D001..D300）+ 注解表头；数据行与源逐字一致 |
| `Assets/ZFrame/Data/Config/Generated/NpcsConfigRow.cs / NpcsConfigTable.cs`（+meta，其余 8 个同型） | A-CONF-02 生成产物（导入钩子自动写出，postprocessor+批处理二次确认幂等「无变化」），非手写 |
| `Assets/Configs/CFG_NpcsTable.asset / CFG_PlacesTable / CFG_PoliciesTable / CFG_QuestsTable / CFG_DialogueTable`（各+meta） | 表资产，真菜单链路导入（默认约定路径），errors=0；100/120/40/12/300 行 |
| `Assets/Game/Domain/Colony/ColonyDataLoader.cs`（+meta） | 数据驱动建局：五 DTO + BuildWorld + 文化/态度/人格映射常量（注释标来源：Program.cs 建局段 / §21 / SeedLoader 启发式） |
| `Assets/Game/Tests/Domain/Colony/ColonyDataDrivenTests.cs`（+meta） | D1-D4 + ColonyConfigAdapter（ConfigService→DTO 投影） |
| `Docs/Logs/TASK-A-COL-02.md` | 本报告 |

- **未动**：asmdef（27+Tests 不变，arch 28/0）、`ZFrame/Editor/ArchitectureValidator.cs`（矩阵 14 条目零 diff）、A-COL-01 的 V1-V7、ZFrame 产品代码（CsvImporter/CsvExporter 等零 diff）、`_asset_staging/`（主控 staging 原样，不入库——.gitignore 排除）。未 commit。
- 临时件（已清）：`Assets/ZFrame/Data/Editor/__TempColonyImportBatch.cs`（真菜单链路批处理探针，两次运行后删除，git status 无残留）；转换/校验脚本在 `TempLogs/`（acol02_boolfix.py / acol02_verify_csv.py，不入库）。git status 全部改动=上表 + 4 个**先于本任务存在**的 `Tools/Probes/out/planet-budget-*.png`（接管前即未跟踪）。

### 2.3 数据修复同步（主控裁决 2026-11-22 第二轮）

- **主控四项裁决，均认可并落定**：①bool 列 0/1→false/true（产品 TryConvert 约定优先，同值语义）；②ColonyConfigAdapter 放测试侧、Domain 收 plain DTO（架构正确——Game.Domain.Colony 保持零配置资产依赖，生产接线时由宿主程序集承担投影）；③白名单按「配置资产目录按约定」三件套解释 + quests/dialogue 补 Id 列；④quests.csv 选项列占位（A/B/C）确认为**主控转换脚本 bug**（抓了字母而非文本），staging 侧已修复（option_a/b/c 现为完整选项文本，12 行，Id 列 Q01-Q12 与本施工契约一致）。
- **同步修补 a（数据）**：修复后的 staging quests.csv 同步至 `Assets/Configs/CSV/quests.csv`——保持既有注解表头（`Id:string,…,WorldAdvances:bool`）与 Id 首列格式；`world_advances` 仍按 0/1→false/true 同值语义重编码。python 逐字段比对（RFC4180 解析）：**0 mismatch，且差异恰好只有 OptionA/OptionB/OptionC 三列（12 行全部）**，其余字段（含 aftermath 的「延迟：数周后」原文）逐字未动。
- **同步修补 a（导入）**：真菜单链路重跑（同款临时探针，用后即删）——quests **updated=12 / missingInCsv=0 / errors=0**（就地更新，不换 GUID），其余四表同轮幂等重导均 errors=0（`acol02_import3.log` + `acol02_import_report.txt`）；选项文本落资产抽查 Q01-Q03 OptionA **3/3 逐字命中** `CFG_QuestsTable.asset`（`acol02_opts_check.py`）。三段选项文本含 ASCII 逗号，由 RFC4180 引号承载，`CsvUtility` 正常解析（errors=0 即证）。
- **同步修补 b（复跑数字）**：D1-D4 过滤跑 **total=4 passed=4 failed=0**（`acol02_filter2.xml`）；EditMode 全量 **1287/1287/0**、0 skipped（`runtests_editmode.xml`），V1-V7 全部仍 Passed——数据修复零回归。D2 的日程断言（15→103 天）与「坟山北坡」特例在新数据下不变（支线顺序/名称/delay_months 未动）。
- **同步修补 c**：本小节即记录；§4-4 的占位登记补注「已修复（见 2.3）」。仍未 git commit。

### 3. 自测数字（全部亲跑，证据在 TempLogs/）

| 段 | 结果 | 证据 |
|---|---|---|
| 编译 | exit 0，0 CS error | `acol02_compile1.log` |
| 行类+表类生成 | 批处理 `GenerateAllFromCommandLine` exit 0，8 表 PASS，五新表「表类: …（无变化）」= 导入钩子已先写出、二次幂等 | `acol02_codegen1.log` + `config_codegen_report.txt` |
| 五表导入 | 真菜单链路 exit 0；第二次就地更新 added=0 updated=行数 **errors=0**（不换 GUID） | `acol02_import1.log` / `acol02_import2.log` + `acol02_import_report.txt` |
| EditMode 全量 | **1287/1287/0**（= 家底 1283 + D1-D4），0 skipped；两次（变异前 + 还原后）同形；V1-V7 全 Passed；数据修复后第三轮 1287/1287/0 见 §2.3 | `runtests_editmode.xml`（13:39 mtime 一轮；D1-D4 与 V1-V7 逐条 Passed） |
| arch | exit 0，asmdef 扫描 28 / 矩阵 14 条目 / **违规 0** | `acol02_arch1.log` + `arch_report.txt`（13:11） |
| 变异自证 | D1 断言 100→99 → 过滤跑（ColonyDataDrivenTests）**total=4 failed=1，D1 Failed**「Expected: 99 But was: 100」→ 还原后 sha256 与备份逐字节一致（`e1b0eaf92bc948588eb7b698eecf2fc7383928de325f782a3441c1a38a7f6e22`）→ 过滤跑 4/4 GREEN → 全量复跑 1287/1287/0 | `acol02_mutant.xml` / `acol02_ColonyDataDrivenTests.cs.orig` / `acol02_restored.xml` |
| 数据保真 | staging↔Assets 五表逐字段比对（csv 模块 RFC4180 解析）：**0 mismatch**（bool 五列按同值语义 0/1↔false/true 计入） | `acol02_verify_csv.py` 输出 |
| 规范 | 交付 .cs/.csv 全部 UTF-8 无 BOM / CR=0 / 末尾换行 / 无尾随空格 | 字节审计脚本 |

### 4. 风险 / 登记（如实，含待主控裁决项）

1. **bool 编码转换（已按既有约定处置）**：主控 staging 的 decay/ongoing/is_trap/is_dark_medicine/world_advances 用 `0/1`，而产品 `CsvImporter.TryConvert` 走 `Convert.ChangeType`（只认 `true/false`，`wave.csv` 先例即 `false/true`）→ 首次导入这五列全部「无法转换为 Boolean」（errors=240/80/12）。**未改 ZFrame 产品代码（白名单外）**，改在 Assets 侧 CSV：五列 0/1→false/true（同值语义，python 逐字段比对 0 mismatch）。**主控 CSV 的 bool 语义不变；若主控坚持保留 0/1 字面，需要改 TryConvert（白名单外，登记待裁决）**。V5 的「3 个 NPC 字段逐字一致」不受影响（npcs.csv 无 bool 列，且 D1 已逐字抽查 5 字段）。
2. **白名单解释（登记）**：10 个 Generated .cs 落在 `Assets/ZFrame/Data/Config/Generated/`——简报白名单写「配置资产目录（按 ConfigSystem 约定）」，而 A-CONF-02 的 ConfigSystem 约定是三件套（`Assets/Configs/CSV/` + `Assets/ZFrame/Data/Config/Generated/` + `Assets/Configs/CFG_*.asset`），生成物不落 Generated 就违背交付物 1 的「表类零手写」既有流程。已按此解释执行；若主控对白名单另有逐字限定，请裁决（删掉这些文件会直接破坏五表链路）。
3. **quests/dialogue 补 Id 列（管线契约）**：两表主控转换无首列 Id，而 `ConfigRow.Id` 契约（首列必须 Id）不可绕；导入时补 Q01..Q12 / D001..D300，其余字段 0 mismatch。主控 CSV 未含 Id 的差异属结构性补列，非数据改写。
4. **quests.csv 的选项占位**：主控转换把 option_a..c 写成字面 `A/B/C`（md 原文的三段选项文本未随 CSV 带过来）→ `QuestDef.Options=["A","B","C"]`（结算按选项号给框架级效果，与 ColonyCore 同构）。aftermath 三行 delay_months=0 的保留「延迟：数周后」原文——与 ColonyCore 正则行为一致（只剥「N个月」）。→ **已修复**：主控确认为其转换脚本 bug，staging 选项列已带完整文本，本施工已同步重导（见 §2.3）。
5. **D3 的确定性依赖**：白汀 conflict 82 字 >30 → Neuroticism=0.65 → 抗议路由必非空；该链路依赖 SeedLoader 启发式常量与 npcs.csv 第 58 行内容。若人格映射或数据改动需复验 D3。
6. **适配层位置**：`ColonyConfigAdapter` 在测试程序集（Game.Domain.Tests）——生产接线（Bootstrap→Colony 建局）落地时需由宿主程序集复用同款投影，届时矩阵为宿主加 Game.Domain.Colony 引用条目；本任务未替生产侧接线做决定（超出简报范围）。
7. **未跑**：PlayMode / naming / native（不在简报自测口径；EditMode 全量已含 naming 未覆盖的回归面）。**环境注记**：批处理纪律全程执行（单实例/清锁/无 -quit 跑测试/xml 轮询）；过滤跑后 Unity 一次 5 分钟不退（xml 已落盘）按 HANDOFF 纪律 `taskkill //F`，后续批次正常。
8. **简报内部冲突登记汇总**：①bool 列编码（§4-1，已按 ConfigSystem 既有约定在 Assets 侧消解，产品代码未动）；②「ColonyDataLoader 在 Colony 且直接读 ConfigService」与 A-COL-01 装配约束互斥（§1 第三条，DTO+适配器消解）；③白名单未逐字列 Generated 路径（§4-2）。三处均未改白名单外文件，等主控验收时裁决；除此之外简报无冲突。

## 三、验收清单

验收：general-purpose 验收子代理（与施工不同代理）｜2026-11-22｜铁律=独立亲证：V1-V6 全部自跑，不采信施工报告数字；证据统一前缀 `TempLogs/acol02acc_*`（与施工证据区分）。环境纪律全程执行（单实例检查/无锁文件/跑测不带 -quit/xml 轮询/xml 落盘后 Unity 滞留即按纪律 taskkill）。

| 项 | 结果 | 独立证据（验收亲跑） |
|---|---|---|
| V1 编译零 CS error | **PASS** | 自跑批处理 `Unity.exe -batchmode -quit -projectPath …` exit 0（`acol02acc_compile.log`，14:00）；`grep -c "error CS"` = **0** |
| V2 EditMode 全量 | **PASS** | 自跑 `-runTests -testPlatform EditMode`（无 -quit，轮询 xml）：`acol02acc_editmode.xml`（14:02）**total=1287 passed=1287 failed=0 skipped=0**（= 家底 1283 + D1-D4 4；≥1287 达标）。点名 xml：`ColonyVerticalLoopTests.V1_PolicyEnact_DelayedBlowup / V2_MerchantProtest_PriceAndPretext / V5_Repression_SpawnsOrganizer` 三条均 **Passed**（V3/V4/V6/V7 亦逐条 Passed——A-COL-01 零回归） |
| V3 架构校验 + D1-D4 | **PASS** | 自跑 `-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine` **exit 0**（`acol02acc_arch.log`）；本次重写的 `arch_report.txt`（14:04:47）：asmdef 扫描 28 / 矩阵条目 14 / **违规 0** / PASS，与施工版报告（13:11）diff 仅时间戳一行；D1-D4 在验收自跑的 `acol02acc_editmode.xml` 逐条 **Passed** |
| V4 变异复红 | **PASS** | **自选与施工不同位置**：`ColonyDataDrivenTests.cs` 第 237 行 D3 断言 `Assert.LessOrEqual(multiplier, 1.6f, …)` → **1.0f**（施工变异的是 D1 的 100→99）。**RED**：过滤跑（`-testFilter Game.Domain.Tests.ColonyDataDrivenTests`）total=4 **failed=1**，D3_RealMerchantEndToEnd **Failed**：「Expected: less than or equal to 1.0f **But was: 1.36000001f**」（`acol02acc_mutant.xml` 14:05）。**还原**：`cp` 自变异前备份（`acol02acc_ColonyDataDrivenTests.cs.orig`），sha256 三方一致 = `e1b0eaf92bc948588eb7b698eecf2fc7383928de325f782a3441c1a38a7f6e22`（变异前原文件 = 备份 = 还原后，亦与施工报告登记值相同）。**GREEN**：过滤跑 4/4/0（`acol02acc_restored.xml` 14:11）+ 全量复跑 **1287/1287/0**（`acol02acc_final_full.xml`，06:19Z）——变异零残留 |
| V5 数据保真 | **PASS** | 验收自写脚本 `acol02acc_verify.py`（`python -I` 独立实现，非施工脚本）：①行数 staging 与 Assets **双侧** 100/120/40/12/300；②staging↔Assets 五表逐字段 **3452 cells，0 mismatch**（bool 五列 0/1↔false/true 同值语义）；③抽 **npcs id 0/49/99 三行**全字段逐字一致（郑大镐/矿工/殖民地第二代…通岱/义体技师…东郭息/义体技师）；④抽 **quests Q01/Q07 两行**逐字一致，**重点 OptionA/B/C 完整选项文本逐字命中**（§2.3 刚修复的数据，验收确认修复已同步 Assets 且无回归）；⑤Assets CSV ↔ `CFG_*.asset` YAML 反序列化五表全量（100/120/40/12/300 行）0 mismatch（bool 1/0、长串 ~120 字符硬折行归一后比对）。输出固化 `acol02acc_verify_output.txt`：RESULT: ALL PASS |
| V6 白名单 + 未 commit | **PASS** | `git status --porcelain` 50 条**全部 `??`**：`git diff` 与 `git diff --cached` 均空（零已跟踪文件修改、零 staged）；`git log` 顶部 `c3cd561`（G-183 A-COL-01 入账）——**A-COL-02 未 commit**。构成：白名单内 **45**（`Assets/Configs/CSV/` 10 + `Assets/Configs/CFG_*.asset` 10 + `Assets/ZFrame/Data/Config/Generated/` 20 + ColonyDataLoader.cs(+meta) 2 + ColonyDataDrivenTests.cs(+meta) 2 + 本日志 1）+ 白名单外登记 **5**（`Docs/Logs/TASK-A-COL-03.md` = 主控并发任务简报、非本施工产物；`Tools/Probes/out/planet-budget-*.png` 4 = 接管前既存未跟踪，施工如实登记）。施工临时件 `__TempColonyImportBatch.cs` 确认无残留 |

附：规范抽验（file-conventions）——交付 .cs/.csv 七个关键文件 BOM=False / CR=0 / 末尾 LF / UTF-8 解码通过（验收字节级亲测）；`ColonyDataLoader.cs` 含来源注释（Program.cs 建局段 / NPC规格第三部分 §21 / SeedLoader 启发式）。验收全程除临时变异（已还原）外零代码修改。

## 四、验收结论

**PASS —— V1-V6 全绿，同意入账（commit 由主控执行）。**

- 施工报告的关键数字经独立亲证全部成立：编译 0 CS error；EditMode **1287/1287/0**（两次：验收首轮 + 变异还原后终轮）；arch exit 0（28 asmdef/14 条目/0 违规，与施工版报告内容逐字节一致）；staging↔Assets↔CFG 资产三级数据比对 0 mismatch。
- **变异复红以不同位置独立复现**：D3 multiplier 上限 1.6f→1.0f → RED（But was 1.36000001f）→ 还原 sha256 三方一致 → GREEN 4/4 → 全量 1287/1287/0。断言-失败-还原-复绿链路完整，测试非烟雾。
- **§2.3 数据修复验收通过**：修复后的 quests 选项文本（OptionA/B/C）在 staging、Assets CSV、CFG_QuestsTable.asset 三处逐字一致；D1-D4 与 V1-V7 在新数据下零回归（1287 全量两次全绿）。
- 施工登记项复核意见：①bool 0/1→false/true（Assets 侧同值语义）——认可，ZFrame 产品代码未动；②Generated 三件套路径按 A-CONF-02 约定——认可，删即破坏管线；③ColonyConfigAdapter 放测试程序集（Domain 收 plain DTO）——认可，架构矩阵约束下的正确解，生产接线由宿主程序集复用投影；④白名单外 5 个未跟踪文件均非本施工引入，按 A-COL-01 主控批注先例不阻验收。
- 验收环境注记：两轮过滤跑 xml 落盘后 Unity 滞留不退（变异轮自行退出、还原轮 PID 11412 强杀），按 HANDOFF 环境纪律处置，证据以 xml 为准；验收自身零代码残留（变异还原 sha256 一致），新增文件仅 `TempLogs/acol02acc_*` 证据（gitignore 内）与本日志两节。
