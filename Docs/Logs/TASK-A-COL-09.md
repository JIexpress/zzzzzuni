# TASK-A-COL-09 · 第二批内容接入：关系网事件/贸易事件/人生节点三表进引擎

> 简报（主控写）｜2026-11-23｜施工：<待填>｜验收：<待填>｜前置：A-COL-02（表类生成流程）、A-COL-06（自主行为）、A-COL-08（探针）

## 一、任务简报（唯一需求来源）

### 背景

内容管线第二批已机检合格（均在 `C:\Users\1\WorkBuddy\杂项\参考资料\GLM产出\`）：
- `任务6_关系网事件40条.json/.csv`（40 条：恩怨/人情债/师徒裂痕/表面亲密私下敌对五组合各 8；字段含双方 name+job、grievance/loyalty/org 变化量 ±0.30 内 0.05 倍数、visibility 三档、latent 潜伏后果）
- `任务7_城间贸易事件20条.csv`（20 条 T01-T20：五类各 4；地名 impact 串、price_impact 10~80%、stock_impact 10~50%、均 5% 倍数、Covert≥8）
- `任务9_人生节点190条.csv`（190 条 L001-L190：100 人全覆盖每人 1-2 条、六类、text≤40 字、rewrite 145 条格式「从『…』改为『…』」、trigger 天数区间、settlement_effect）

本任务把三份数据接入引擎：三张配置表（走 A-COL-02 表类零手写流程）+ 三个消费点 + 存档兼容。

### 交付物

1. **三张配置表**（照 A-COL-02 流程：CSV 注解表头+Id 首列 → 生成行/表类 → 菜单导入 CFG_*Table.asset）：
   - `ColonyRelationsTable`（40 行，id R01-R40）、`ColonyTradesTable`（20 行，沿用 T01-T20）、`ColonyLifeTable`（190 行，沿用 L001-L190）——源 CSV 在 GLM产出，若缺 Id 列/列名不符生成器契约，复制到 `_asset_staging/colony_csv/` 修头后再进管线（登记差异）
2. **消费逻辑**（Domain，`Game/Domain/Colony/`）：
   - **关系网事件**：`ColonyRelationEngine`——GameWorld.DailyTick 每日按 visibility 条件触发（Covert 每日最多 1 条、Personal 需玩家在场/线人、Public 直接入日志）：匹配双方关系边（按姓名在 NamedById 找 id，无边的临时补一条 Colleague 边）→ 状态变化写入（grievance/loyalty/org，方向按事件语义字段）→ EventLog（Covert 为主）→ aftermath 非空进 Delayed 队列（默认 30 天）。**已触发事件不重复**（HashSet 记录，进存档）。
   - **贸易事件**：`ColonyTradeEngine`——按 trigger 天数区间/条件触发（每事件首个满足日触发一次）：解析 impact 串影响 MarketState 对应节点（价格/库存百分比）+ EventLog（Public/Covert 按 visibility）。
   - **人生节点**：`ColonyLifeEngine`——NamedNpc.DailyTick（或 GameWorld 每日循环）查该 NPC 的 trigger 天数区间命中 → 事件文本入 Log（Personal）→ rewrite 字段解析「从『X』改为『Y』」→ 真实改写 npc.Want/Conflict/Secret（旧值留痕进事件文本）→ settlement_effect 解析写入对应 Settlement。**已触发生命节点不重复**（进存档）。
   - 三引擎统一由 `ColonyContentLoadedWorld`（或 GameWorld 挂点）驱动，**AutonomyEnabled 等既有开关行为不变**。
3. **存档兼容**：ColonySaveData 增已触发关系事件/贸易事件/人生节点的进度集合；Restore 后不重放。S 系既有断言不破。
4. **数据驱动测试** `ColonyContentBatch2Tests`（EditMode，走 ColonyConfigAdapter 扩展）：
   - `E1_ThreeTablesLoad`：40/20/190 行；抽查 3 行字段与源 CSV 逐字一致。
   - `E2_RelationEngineFires`：推进天数 → 关系事件触发 → 双方状态变化与 CSV 数值一致 + Covert 进 Truth 不进 PlayerFeed；aftermath 进延迟队列。
   - `E3_TradeEngineDrifts`：贸易事件触发 → MarketState 对应节点价格/库存按 impact 变化（5% 倍数精确断言）。
   - `E4_LifeRewrite`：命中的 NPC want/conflict/secret 被改写且旧值出现在事件文本；settlement_effect 落到对应聚落。
   - `E5_NoReplayAfterRestore`：存档→Restore→续推 → 已触发内容不重复触发。
   - `E6_PerfBound`：含三引擎的 DailyTick×30 天 < 80ms（自主行为 50ms 红线之上留余量）。
5. **白名单**：`Assets/Game/Domain/Colony/**`、`Assets/Game/Configs/**`（CSV+SO）、`Assets/ZFrame/Data/Config/Generated/**`（生成类）、`Assets/Game/Tests/**`、`Assets/Game/View/Colony/ColonyConfigAdapter.cs`（如需投影扩展）、`_asset_staging/colony_csv/**`、`Docs/Logs/TASK-A-COL-09.md`。其余登记待裁决。不 commit、不推远程。

### 自测要求

编译 exit 0 → EditMode 全量 failed=0 total≥1321（1315+E1-E6）→ PlayMode 两轮同形 failed=0 → arch exit 0 → 变异自证（改坏 E2 或 E4 一处 → RED → 还原 sha256 一致 → GREEN）。**Unity 撞锁纪律沿用（用户可能再开编辑器：只等待重试）。**

### 验收清单 V1-Vn（验收代理独立亲证）

- V1 编译零 CS error。
- V2 EditMode 全量 failed=0 total≥1321（记录确切值）；26 个旧用例抽查零回归（V1_V7/D1_D4/S1_S5/M1_M5/P1_P4/R1_R4）。
- V3 PlayMode 两轮同形 failed=0；探针 -Gate 仍 PASS（内容没变锚不破）。
- V4 架构校验 exit 0；Domain 纪律不变。
- V5 变异复红（验收自选与施工不同位置，先落备份 sha256，还原逐字节一致；哑弹如实登记重做）。
- V6 三表行数 40/20/190 与源 CSV 抽查逐字一致；git status 改动仅限白名单；未 commit。

## 二、施工报告

施工：general-purpose 施工子代理｜2026-11-23｜全程环境纪律执行（单实例检查/无 -quit 跑测/xml 为准/xml 落盘后 Unity 滞留即核实为本批进程后 taskkill；全程零接触用户编辑器——本轮 tasklist 证实用户编辑器未运行）

### 1. 摘要

- **三张配置表全部走通 A-CONF-02/A-COL-02 既有管线**：源 CSV（GLM产出 任务6/7/9）复制到 `_asset_staging/colony_csv/` 修头（缺 Id 列/列名不符生成器契约——差异明细见 §4-1，数据单元格逐字不动，脚本逐格比对 0 mismatch）→ 注解表头 CSV 落 `Assets/Configs/CSV/`（colonyRelations/colonyTrades/colonyLife.csv）→ 导入钩子同批生成行类+表类 6 个产物（`Assets/ZFrame/Data/Config/Generated/`）→ **真菜单链路**（Selection 指向 csv + `EditorApplication.ExecuteMenuItem("Assets/ZFrame/Import as Config Table")`，临时探针用后即删）导入 3 个 `CFG_*Table.asset`（added=40/20/190、missingInCsv=0、**errors=0**，资产路径由 `CsvExporter.TableAssetPathFor` 推导）。表类名 = 简报点名的 ColonyRelationsTable/ColonyTradesTable/ColonyLifeTable。
- **三引擎进 Domain（`Game/Domain/Colony/` 新增 4 文件）**：`ColonyRelationEngine`（visibility 闸门：Covert 每日 Id 序 1 条/Personal 需 PresentRegion 或 Informants 覆盖/Public 直接入日志；按姓名索引具名层匹配双方，无边临时补 Colleague 边；d_* 六增量钳 0..1 写入；EventLog 带 `[关系]` 前缀；aftermath 非「无」进 Delayed 队列默认 30 天）+ `ColonyTradeEngine`（触发日=EventId×5 的确定性调度——**登记**：CSV 无 trigger 列；impact 串「地名：货物 ±N%」逐段解析，命中市场节点（建局只给人口前二：双河城/残垣城）才落数字 BasePrice/Stock，其余地名留痕不入数字；EventKind.Market）+ `ColonyLifeEngine`（trigger「第 X-Y 天」硬门槛（190/190 命中；「且其 … 时优先」为叙事语气不设门槛——**登记**）；rewrite 解析「从『X』改为『Y』」→ 真实改写 Want/Conflict/Secret，**真实旧值**留痕进事件文本（内容侧 rewrite_text 的『X』是转述、与卡字段逐字不同占 145/145，check_t9 只验格式——**登记**）；settlement_effect 逐段解析：平均怨气/忠诚/组织度→当事人所在聚落聚合值、「N 人 …」→当事人一人、产能 ±N%→聚落市场节点 BasePrice×(1∓N/100)、库存 ±N%→Stock×(1+N/100)、「持续 N 天」随文本留痕一次性入账不复原——**登记**）。三引擎由 `ColonyContentEngines` 聚合，GameWorld.DailyTick 既有循环内挂点（`Content?.SettleDaily(this)`）：**Content 为 null 的世界零开销、行为逐位不变**，引擎零 Rng 消耗（确定性扫描，既有随机序列不变），AutonomyEnabled 等既有开关语义不变。
- **建局注入与存档兼容**：`ColonyWorldData` 增 `Content`（ColonyContentBundle），`ColonyDataLoader.BuildWorld` 仅在 Content 非 null 时注入挂点（五表路径零变化）；`ColonySaveData` 增 FiredRelations/FiredTrades/FiredLife 三进度集合（内容本体不入档——建局输入可重投影，进度才随档），Capture 采集、Restore 以 `ColonyContentEngines.FromProgress` 重建（内容空表，调用方 `Attach` 回填后续推不重放）；codec（ColonySaveBridge）写侧恒写出三节、**读侧宽容（旧存档无这三节按空集读回——存档兼容）**。
- **`ColonyConfigAdapter`（Game.View 生产侧）扩展**：新增 `ToContentBundle`（三表→ColonyContentBundle 1:1 投影）与编辑器通道 `LoadContentBundle`；**五表 `ToWorldData`/`LoadWorldData` 签名与行为不变**——生产宿主 ColonyWorldHost 未接第二批内容（**登记**：A-COL-08 探针锚基线以宿主世界为基线，接内容须主控重制锚，见 §4-4）；测试侧 ColonyDataDrivenTests 的五表 Adapter 亦零改动（D/S 系世界行为不变）。
- **`ColonyContentBatch2Tests`（E1-E6，EditMode，数据驱动建局走 View 侧生产适配器）**：E1 三表 40/20/190 行+首末行抽查逐字（R01/T01/L001/L190 含 impact 串/trigger 原文）；E2 推进天数触发关系事件（第 1 天恰好 6 Public+首 Covert R01；d_* 聚合增量与 CSV 逐值一致；R01 进 Truth 不进 PlayerFeed；aftermath 30 天后经延迟队列回账；第 2 天 Covert 限额复位+在场触发 R02+线人触发 R05）；E3 T03@Day15 精确漂移残垣城 BasePrice×1.10/Stock×0.80（双河城零漂移、重复结算不二次生效）；E4 L013@Day60 唯一活跃日改写 secret+真实旧值留痕、L008@Day981「平均忠诚 +0.05」精确落到青石城聚落；E5 存档（Capture→codec 往返→Restore）→进度集合逐集合一致→Attach 回填续推 20 天→已触发零重放+T06-T09 照常触发+跨档延迟后果照常回账；E6 含三引擎的 DailyTick×30 天 **0.064s < 80ms**。
- 自测六段全绿（数字见 §3）：编译 exit 0、EditMode 全量 **1321/1321/0**（≥1321 达标）、PlayMode 两轮 **127/126/0/1 skipped 同形**、探针 **-Gate PASS 28/28**、arch exit 0（28 asmdef/14 条目/0 违规）、变异自证 RED→还原 sha256 逐字节一致→GREEN。

### 2. 交付清单

| 文件 | 角色 |
|---|---|
| `_asset_staging/colony_csv/colonyRelations.csv`（不入库，staging） | 修头后源表：补 Id 列（R01-R40）+ 注解表头；数据单元格与源逐字一致（脚本比对） |
| `_asset_staging/colony_csv/colonyTrades.csv` / `colonyLife.csv` | 同上（T01-T20/L001-L190 沿用源 Id 列，仅表头改名+注解） |
| `Assets/Configs/CSV/colonyRelations.csv`（+meta） | 40 行进管线（staging 同字节拷贝） |
| `Assets/Configs/CSV/colonyTrades.csv`（+meta） | 20 行进管线 |
| `Assets/Configs/CSV/colonyLife.csv`（+meta） | 190 行进管线 |
| `Assets/ZFrame/Data/Config/Generated/ColonyRelationsConfigRow.cs / ColonyRelationsConfigTable.cs`（+meta，其余 4 个同型） | A-CONF-02 生成产物（导入钩子写出、`GenerateAllFromCommandLine` 幂等确认），非手写 |
| `Assets/Configs/CFG_ColonyrelationsTable.asset` / `CFG_ColonytradesTable.asset` / `CFG_ColonylifeTable.asset`（各+meta） | 真菜单链路导入（约定路径），added=40/20/190、errors=0、不换 GUID |
| `Assets/Game/Domain/Colony/ColonyContentInput.cs`（+meta） | 三表 plain 输入行（ColonyRelationEventInput/ColonyTradeEventInput/ColonyLifeEventInput）+ ColonyContentBundle + ColonyContentLookup（姓名索引/visibility 解析） |
| `Assets/Game/Domain/Colony/ColonyRelationEngine.cs`（+meta） | 关系网事件引擎（visibility 闸门/边匹配/d_* 写入/aftermath 延迟 30 天） |
| `Assets/Game/Domain/Colony/ColonyTradeEngine.cs`（+meta） | 贸易事件引擎（触发日=EventId×5 登记；impact 串→市场节点） |
| `Assets/Game/Domain/Colony/ColonyLifeEngine.cs`（+meta） | 人生节点引擎（天数区间门槛/rewrite 改写留痕/settlement_effect 解析） |
| `Assets/Game/Domain/Colony/ColonyContentEngines.cs`（+meta） | 三引擎聚合（GameWorld.Content 挂点载体；FromProgress/Attach 通道） |
| `Assets/Game/Domain/Colony/GameWorld.cs`（修改） | 增 `Content` 字段 + DailyTick 挂点（Content null 零开销） |
| `Assets/Game/Domain/Colony/ColonyDataLoader.cs`（修改） | ColonyWorldData 增 Content；BuildWorld 注入挂点（五表路径零变化） |
| `Assets/Game/Domain/Colony/ColonySaveData.cs`（修改） | 增 FiredRelations/FiredTrades/FiredLife 三进度集合 |
| `Assets/Game/Domain/Colony/ColonySaveCapture.cs`（修改） | Capture 采集三进度集合（Content null 留空集） |
| `Assets/Game/Domain/Colony/ColonySaveRestore.cs`（修改） | Restore 重建进度引擎（无进度保持 null；内容空表待 Attach） |
| `Assets/Game/Domain/Colony/GameEvent.cs`（修改） | EventKind 追加 Relation/Life 两值（尾部追加，既有 byte 值不变；无穷举 switch 消费方——MonthlyReport 未列 Kind 落「其他」，安全） |
| `Assets/Game/Tests/Domain/Colony/ColonySaveBridge.cs`（修改） | codec 写侧恒写 firedRelations/firedTrades/firedLife 三节；读侧宽容（旧档兼容） |
| `Assets/Game/View/Colony/ColonyConfigAdapter.cs`（修改） | 增 ToContentBundle + LoadContentBundle（五表 ToWorldData/LoadWorldData 零改动） |
| `Assets/Game/Tests/Domain/Colony/ColonyContentBatch2Tests.cs`（+meta） | E1-E6 数据驱动测试 |
| `Docs/Logs/TASK-A-COL-09.md` | 本报告 |

- **未动**：asmdef（28 不变）、ArchitectureValidator 矩阵（14 条目零 diff）、ZFrame 产品代码（CsvImporter/CsvExporter/生成器零 diff）、A-COL-02 的测试侧五表 Adapter（D/S 系世界行为不变）、ColonyWorldHost/ReportPanelView（宿主不接第二批内容，§4-4）。未 commit。
- 临时件（已清）：`Assets/ZFrame/Data/Editor/__TempAcol09ImportBatch.cs`（真菜单链路探针，一次运行后删除，git status 无残留）；staging/比对脚本在 `TempLogs/acol09_stage_csv.py`（TempLogs 不入库）。证据文件：`TempLogs/acol09_codegen1.log`、`acol09_import1.log` + `acol09_import_report.txt`、`acol09_compile5.log`、`runtests_editmode.xml`、`acol09_playmode_r1/r2.xml`、`acol09_arch1.log`+`arch_report.txt`、`acol09_mutant.xml`、`acol09_restored.xml`、`acol09_ColonyContentBatch2Tests.cs.orig`。
- git status 改动面 = 上表全部（白名单内）+ 4 个**先于本任务存在**的 `Tools/Probes/out/planet-budget-*.png` 未跟踪件（A-COL-02 同款登记）。PlayMode 产生的 5 个 `Docs/Parity/*.png` 噪声已 git checkout 丢弃（A-COL-05 先例）。

### 3. 自测数字（全部亲跑，证据在 TempLogs/）

| 段 | 结果 | 证据 |
|---|---|---|
| 编译 | exit 0，0 CS error | `acol09_compile5.log`（compile1-4 轮共修 9→3→18→1 处 CS error，均为本轮新文件编译错：缺 using/作用域/误删类体，如实登记） |
| CSV 修头+生成 | staging 脚本逐格比对 0 mismatch、40/20/190 行、无 BOM/LF；批处理 `GenerateAllFromCommandLine` exit 0（表数 11/错误表数 0/PASS，三新表「无变化」=导入钩子已先写出） | `acol09_stage_csv.py` 输出 + `acol09_codegen1.log` |
| 三表导入 | 真菜单链路 3×menu=True；`added=40/20/190, updated=0, missingInCsv=0, errors=0`（约定路径 `Assets/Configs/CFG_Colony*Table.asset`） | `acol09_import1.log`（847/872/897 行）+ `acol09_import_report.txt` |
| EditMode 全量 | 第一轮 **1321/1320/1**（E2 失败=测试自身顺序缺陷，见 §4-5）→ 修复后第二轮 **1321/1321/0**（0 skipped；E1-E6 全 Passed，E6 实测 0.064s）；26 个旧用例抽查零回归（V1/V2/V5/D1/S3/M1/P1 逐条 Passed） | `acol09_editmode1.log` + `runtests_editmode.xml`（1321/1321/0） |
| PlayMode 两轮 | **127/126/0/1 skipped 两轮同形**（127 个用例清单逐一同名；1 skipped = `-acol08probe` 惰性探针全量轮零开销，基线形态） | `acol09_playmode_r1.xml` / `acol09_playmode_r2.xml` |
| 探针 -Gate | **PASS，28/28 锚全部 OK**（内容没变锚不破——宿主世界未接第二批内容） | `probe_colony_report` 输出（PROBE_VERDICT: PASS） |
| arch | exit 0，asmdef 扫描 28 / 矩阵 14 条目 / **违规 0** | `acol09_arch1.log` + `arch_report.txt` |
| 变异自证 | 先落备份 sha256 `3bc0bf55d5d11ff8ea75b671768d7672a84ce11fc72e76ba8f315beeadc0407e` → 变异 E4（聚落忠诚断言 0.05f→0.04f）→ 过滤跑 **total=6 failed=1，E4 RED**「Expected: 0.59 But was: 0.60」→ 还原后 sha256 与备份逐字节一致 → 过滤跑 **6/6/0 GREEN** | `acol09_ColonyContentBatch2Tests.cs.orig` / `acol09_mutant.xml` / `acol09_restored.xml` |
| 规范 | 交付 .cs/.csv 全部 UTF-8 无 BOM / LF / 末尾换行（字节审计脚本） | `acol09_stage_csv.py` 断言 + 手工核验 |

### 4. 风险 / 登记（如实，含待主控裁决项）

1. **CSV 修头差异明细（简报授权的 staging 修头，数据单元格零改写）**：
   - 任务6（→colonyRelations.csv）：①**补 Id 列**（源无 Id 列，首列 event_id 为 1..40 整数 → 按行序 Id=R01..R40，EventId 列保留源值）；②列名 snake→注解 Pascal：`event_id→EventId:int`、`npc_a→NpcA`、`npc_b→NpcB`、`pair_type→PairType`、`topic→Topic`、`event→Event`、`d_a_grievance→DAGrievance:float`、`d_a_loyalty→DALoyalty:float`、`d_a_org→DAOrg:float`、`d_b_*→DBGrievance/DBLoyalty/DBOrg:float`、`visibility→Visibility`、`latent→Latent`、`aftermath→Aftermath`；③行尾 CRLF→LF。数据单元格脚本逐格比对 0 mismatch。
   - 任务7（→colonyTrades.csv）：①首列 `id`→`Id`（T01-T20 源已有）；②列名 Pascal 化（+`places→Places`、`event_kind→EventKind`、`price_impact→PriceImpact`、`stock_impact→StockImpact`）；数据逐格 0 mismatch。
   - 任务9（→colonyLife.csv）：①首列 `id`→`Id`（L001-L190 源已有）；②`name→Name`、`npc→Npc`、`event_type→EventType`、`trigger→Trigger`、`text→Text`、`rewrite_field→RewriteField`、`rewrite_text→RewriteText`、`settlement_effect→SettlementEffect`（全 string）；数据逐格 0 mismatch。
   - 命名决策：CSV stem 用驼峰（colonyRelations/colonyTrades/colonyLife）→ 生成行类 ColonyRelationsConfigRow/…、表类 **ColonyRelationsTable/ColonyTradesTable/ColonyLifeTable**（简报点名表名达成）；表资产名由产品代码 `TableAssetPathFor` 从小写 stem 推导 = `CFG_ColonyrelationsTable.asset`/`CFG_ColonytradesTable.asset`/`CFG_ColonylifeTable.asset`（既有约定，若主控要求资产名亦驼峰需改产品代码推导规则——登记待裁决）。白名单「Assets/Game/Configs/**」按 A-COL-02 先例解释为 ConfigSystem 三件套约定路径（`Assets/Configs/CSV/`+`Assets/Configs/CFG_*.asset`+`Assets/ZFrame/Data/Config/Generated/`）。
2. **三引擎触发语义说明（简报冲突/数据缺口，按确定性缺省落地并登记）**：
   - **贸易事件无 trigger 列**：简报写「按 trigger 天数区间/条件触发」，但任务7 CSV 无该列（机检 17 列固定）。落地为确定性调度：事件 T{i} 触发日 = EventId×5（T01@5…T20@100，20 条跨 5~100 天、每日至多 1 条自然错峰），「每事件首个满足日触发一次」= Day ≥ 触发日的首个结算日。**待主控裁决**（若主控给出官方触发列需改表+引擎一行）。
   - **贸易事件的 d_* 六数值列未消费**：简报贸易引擎消费面=impact 串→MarketState+EventLog（未提 NPC 状态）；d_* 留在表内未落状态（关系引擎才消费同形字段）。**待主控裁决**（若需消费可复用关系引擎 Apply 同款写入）。
   - **人生节点 trigger 附加条件**：190/190 都有「第 X-Y 天」区间（硬门槛）；带「且其 grievance>0.5 时优先」的条目按「优先=叙事语气」处理不设硬门槛（check_t9 对该语气亦只校验写法）。
   - **rewrite_text 旧值≠卡字段**：145 条 rewrite 的『X』是内容侧转述（与卡 want/conflict/secret 逐字比对 0/145 命中；check_t9 只验格式）。引擎「旧值留痕」落**真实旧值**进事件文本（E4 以真实旧值断言），并登记 E4 断言不依赖『X』==卡值。
   - **settlement_effect 映射（48 种行业前缀词 → 引擎可落对象）**：`平均怨气/忠诚/组织度`→当事人聚落 AvgGrievance/AvgLoyalty/Organization（83/190 条含「平均」段）；`N 人怨气/忠诚/组织度`→当事人一人（人数为叙事强度标注）；`产能 ±N%`→聚落市场节点 BasePrice×(1∓N/100)（产能升=收购价降的行情修正）；`库存 ±N%`→Stock×(1+N/100)；「持续 N 天」一次性入账不复原（复原型时效效果需引擎级定时器，超 E1-E6 断言面）。市场节点只有建局人口前二地区（双河城/残垣城）——impact/产能/库存段地名未命中节点时**仅事件文本留痕不入数字**。全部映射待主控裁决（表驱动聚落映射需要更多聚落才能精确落位）。
3. **EventKind 扩张**：追加 `Relation`/`Life` 两值（尾部追加，既有字节值不变）。消费方核查：无穷举 switch（MonthlyReport 未列 Kind 落「其他」、ReportPresenter 过滤按具体 Kind、Delayed 队列按 byte 往返）——零回归（EditMode/PlayMode 全绿即证）。贸易事件复用 `EventKind.Market`。
4. **生产宿主未接第二批内容（登记）**：ColonyWorldHost 仍走五表 `LoadWorldData`，`ColonyWorldData.Content` 恒 null——SCN_Game 实况（含 PlayMode C 系与 A-COL-08 探针锚基线）行为逐位不变；挂点已就位（E 系全量在用），宿主接线=一行 `data.Content = ColonyConfigAdapter.LoadContentBundle(config)`，**须与主控同步重制探针锚基线后再接**（探针 28 锚为宿主世界内容基线，接内容会改月报行数）。测试侧五表 Adapter 同样未扩（D/S 系世界不变）。
5. **EditMode 第一轮 1 红（测试自身缺陷，如实登记）**：E2 的「第 2 天闸门断言」误排在 `AdvanceDays(31)`（aftermath 回账段）之后——AdvanceDays 按日把剩余 18 条 Covert 逐日耗尽，第 2 天专属断言失去前提（引擎行为正确：每日 1 条正是规格）。修复=断言重排序（先闸门后回账），第二轮全量 1321/1321/0。非引擎缺陷。
6. **E6 红线余量**：30 天实测 0.064s（<80ms，余量 ≈1.25×）；其中三引擎增量（40+20+190 条逐日扫描 + 每引擎每日 100 项姓名索引）在自主行为基线（T5≈0.019s）之上增加约 0.04s 量级。若主控要更细分账可再加分段计时探针（本任务未做，避免扩面）。
7. **未跑**：naming/config/native 段（不在简报自测口径；EditMode 全量已覆盖 naming 未覆盖的回归面）。**环境注记**：变异 RED 轮与还原 GREEN 轮 xml 落盘后 Unity 各滞留一次（编译后台化未退），tasklist 核实均为本批 batchmode 进程后按纪律 taskkill，证据以 xml 为准；全程未出现用户编辑器进程。
8. **简报冲突登记汇总**：①白名单「Assets/Game/Configs/**」与实际约定路径「Assets/Configs/**」的措辞差（§4-1，按 A-COL-02 先例解释）；②贸易表缺 trigger 列（§4-2 第一条）；③ColonyWorldData.Content 注入点在 Domain（`Game/Domain/Colony/` 白名单内）而非 ColonyContentLoadedWorld（简报「或 GameWorld 挂点」的后者，零新类冲突）；④资产文件名小写 stem 推导（§4-1 尾）。除此之外简报无冲突，以上均未改白名单外文件。

## 三、验收清单

验收：general-purpose 验收子代理｜2026-11-23｜与施工不同代理，全部数字独立亲证（简报铁律：不采信施工结论）。证据文件均落 `TempLogs/acol09acc_*`（TempLogs 不入库）。

### V1 编译零 CS error — PASS

自跑 `Unity.exe -batchmode -quit`（独立于施工轮）：**UNITY_EXIT_CODE=0**，日志 `acol09acc_compile1.log` 中 `error CS` 出现 **0** 次。

### V2 EditMode 全量 failed=0 total≥1321 — PASS（确切值 1321/1321/0）

自跑两轮（run_tests.ps1 editmode 段，证据 xml 为准）：

- 第一轮（机器 BUSY 窗口，机检记录 i4Tools 占 25% CPU）：**1321/1320/1**——唯一失败 `E6_PerfBound`（实测 101ms > 80ms 红线）。**如实登记**：E6 是性能红线用例，负载敏感（用户进程 i4Tools 不能动、不 taskkill，等待负载回落）；空载（CPU LoadPercentage=1）复跑通过。这是环境噪声，非代码回归（与 HANDOFF「空载护栏」口径一致：性能数字只在空载可信）。
- 第二轮（空载）：**total=1321 passed=1321 failed=0 skipped=0，result=Passed**（≥1321 达标）。xml：`TempLogs/runtests_editmode.xml`。

**抽查零回归（xml 点名，实际核验为类级全过，覆盖强于点名）**：

| 系 | 用例 | 结果 |
|---|---|---|
| V（ColonyVerticalLoopTests） | `V1_PolicyEnact_DelayedBlowup`、`V7_EventBusBridge`（类 7/7 全 Passed） | Passed |
| D（ColonyDataDrivenTests） | `D2_BuildWorldFromData`、`D3_RealMerchantEndToEnd`（类 4/4 全 Passed） | Passed |
| S（ColonySaveLoadTests） | `S1_StateEquivalence`、`S5_SaveSystemRoundTrip`（类 5/5 全 Passed） | Passed |
| M（ColonyReportTests） | `M1_SummaryFlagsAnomalies`、`M3_OverOptimizationDivergence`（类 5/5 全 Passed） | Passed |
| P（ReportPresenterTests） | `P1_SummaryVmHidesCovertRows_RawContainsThem`、`P2_AnomalyLinesAreCompleteHighlightedRows`（类 4/4 全 Passed） | Passed |
| R（AffordanceTests，A-AI-05v2 预约系） | R1 `Reserve_BlocksOthersFindBest_SelfStillSelectable_ReleaseRestores`、R2 `Reserve_IsIdempotent_…`、R3 `Claim_AfterOwnReserve_ConvertsToOccupancy_ReleaseClearsBoth`、R4 `Release_ByOtherAgent_KeepsReservation_RepeatReleaseReturnsFalse`（类 17/17 全 Passed） | Passed |
| E（ColonyContentBatch2Tests） | `E1_ThreeTablesLoad`…`E4_LifeRewrite`（类 6/6 全 Passed，E5/E6 亦过） | Passed |

### V3 PlayMode 两轮同形 failed=0 + 探针 -Gate PASS — PASS

- 两轮独立跑：**127/126/0/1 skipped，两轮逐名一致**（脚本比对：round1 独有名 0、round2 独有名 0、逐用例 result 分歧 0、Failed 0；xml duration 两轮均 ≈169.6s）。1 skipped = `ColonyReportProbeTests.R1_ReportPanelProbe_…`（-acol08probe 惰性探针全量轮零开销，登记的基线形态）。证据：`acol09acc_playmode_r1.xml` / `acol09acc_playmode_r2.xml`（127 用例清单逐一同名）。
- 探针 `-Gate`：**PROBE_VERDICT: PASS（28/28 锚全部 OK，runner exit 0）**——Summary/异常/Raw（rowsRendered=23）数值锚精确比对、`raw.paidGateConsumedIntel=True` 情报点闸门、6 项像素地板全过。**内容锚不破**（与登记一致：生产宿主未接第二批内容，SCN_Game 实况零变化）。

### V4 架构校验 + Domain 纪律 — PASS

- arch 段 exit 0；报告 `TempLogs/arch_report.txt`：**asmdef 扫描 28 / 期望矩阵条目 14 / 违规 0 / PASS**（asmdef 与 ArchitectureValidator 矩阵零 diff，与施工报告一致）。
- Domain 纪律（亲查 5 个新引擎文件 `ColonyContentInput/ColonyRelationEngine/ColonyTradeEngine/ColonyLifeEngine/ColonyContentEngines.cs`）：**零 UnityEngine / 零 UnityEditor / 零 ZFrame.Data 引用**；`Game.Domain.Colony.asmdef` 仍仅 `references: [ZFrame.Core]`、`noEngineReferences: true`——纪律不变。

### V5 变异复红（异位） — PASS（一次命中，无哑弹）

- **位置与施工不同**：施工变异 E4 聚落忠诚断言（0.05f→0.04f）；本验收变异 **E3 市场 5% 倍数断言**——`ColonyContentBatch2Tests.cs` E3 中 `Assert.AreEqual(priceBefore * 1.10f, yuanheng.BasePrice, …)` 改为 `* 1.05f`（T03 残垣城泵阀 +10% 的精确倍数断言，验其灵敏度）。
- **先落备份**：变异前 sha256 `3bc0bf55d5d11ff8ea75b671768d7672a84ce11fc72e76ba8f315beeadc0407e`（=施工报告登记的原始值，三方互证），备份 `TempLogs/acol09acc_ColonyContentBatch2Tests.cs.orig`（gitignored，仓外留痕）。
- **RED**：过滤跑 `Game.Domain.Tests.ColonyContentBatch2Tests` → **total=6 passed=5 failed=1**，失败正是 `E3_TradeEngineDrifts`：「Expected: 83.999996… But was: 88.0」（80×1.05 vs 80×1.10——5% 倍数偏差被精确抓到，断言有牙）。xml：`acol09acc_mutant.xml`（Unity exit 2）。
- **还原**：cp 备份回原路径，sha256 `3bc0bf55…` 与备份**逐字节一致**；该文件 untracked，无 git diff 残留。
- **GREEN**：同过滤跑 **total=6 passed=6 failed=0**（E1-E6 全过）。xml：`acol09acc_restored.xml`（Unity exit 0）。
- 哑弹登记：**无**（变异一次命中 RED）。全程未 taskkill 任何 Unity（变异/GREEN 轮均自行退出，exit code 均捕获）。

### V6 三表行数/源 CSV 逐字 + git 白名单 + 未 commit — PASS

- **行数**：管线 CSV（`Assets/Configs/CSV/`）41/21/191 行（含注解表头）= **40/20/190 数据行**；三个 `CFG_Colony*Table.asset` 内 `<Id>k__BackingField` 计数 **40/20/190**。
- **逐字一致（全表逐格，强于 3 行抽查）**：脚本解析 CSV（RFC4180 引号承载）源 vs 管线——relations 600 格、trades 340 格、life 1710 格，**0 mismatch**；差异面仅施工报告 §4-1 登记的修头（relations 补 Id 列且序列 R01..R40 正确、三表仅表头改名）。asset 内 R01 行 15 字段与 CSV 逐字段零 mismatch（unicode 转义还原后比对）。源文件实名登记：任务 9 实际为 `任务9_人生节点.csv`（简报写「任务9_人生节点190条.csv」，同一文件，内容 190 条吻合）。
- **git 白名单**：HEAD `f893ddb` 未动、**未 commit**。改动 49 项 = 8 个 tracked 修改（全在 `Game/Domain/Colony/`、`Game/Tests/Domain/Colony/`、`Game/View/Colony/ColonyConfigAdapter.cs`——后者是简报白名单点名件）+ 41 个 untracked（任务日志、三表 CSV+meta、3 asset+meta、6 生成类+meta、4 Domain 引擎+meta、E 系测试+meta 全在白名单；另 4 个 `Tools/Probes/out/planet-budget-*.png` 为**先于本任务存在**的未跟踪件，A-COL-02 同款登记）。主控认可项⑥：白名单「Assets/Game/Configs/**」按 A-COL-02 先例解释为 `Assets/Configs/**` 约定路径——按此口径全部改动在白名单内。
- PlayMode 产生的 4 个 `Docs/Parity/*.png` 噪声按 A-COL-05 先例 `git checkout` 丢弃，工作区无验收残留（临时变异已逐字节还原）。

### 环境纪律执行记录

全程每轮 Unity 启动前 `tasklist` 核实无 Unity.exe/无 UnityLockfile（本轮用户编辑器未运行）；Unity 一律经 Git Bash 启动；测试跑不带 -quit；证据以 xml 为准；所有轮次自行退出（exit code 捕获），无僵尸、无 taskkill。机检 BUSY 一轮（i4Tools）已如实记录并空载复跑。

## 四、验收结论

**PASS**（V1-V6 全过，独立亲证）。

| 项 | 判定 | 关键证据 |
|---|---|---|
| V1 编译 | PASS | 自跑 batchmode exit 0、0 error CS |
| V2 EditMode | PASS | 自跑 **1321/1321/0**（≥1321）；点名 V/D/S/M/P/R/E 系类级全 Passed 零回归 |
| V3 PlayMode | PASS | 两轮 **127/126/0/1 skip 逐名同形**；探针 -Gate **PASS 28/28** exit 0 |
| V4 架构 | PASS | arch exit 0（28 asmdef/14 条目/0 违规）；新引擎文件零 UnityEngine/零 ZFrame.Data，asmdef 未变 |
| V5 变异 | PASS | 异位 E3 倍数断言 1.10f→1.05f → RED 6/5/1（Expected 84.0 vs 88.0）→ 还原 sha256 逐字节一致 → GREEN 6/6/0；无哑弹 |
| V6 数据/git | PASS | 40/20/190 行；源 CSV 逐格 2650 格 0 mismatch + asset R01 零 mismatch；改动全白名单；未 commit |

- **第一轮 EditMode 1 红（E6_PerfBound 101ms>80ms）如实登记**：机检 BUSY（i4Tools 25% CPU）窗口内的负载噪声，空载复跑通过；性能红线用例对机器负载敏感是已知模式（HANDOFF 空载护栏），不构成代码回归。
- 施工报告六项待裁决经主控全部认可，验收侧已在代码中逐项核实落地一致（EventId×5 调度 / 贸易 d_* 未消费 / rewrite 真实旧值留痕 / settlement_effect 段式映射 / ColonyWorldHost 零接线（grep 证实）/ Configs 路径先例口径）。
- 附带核验（超出清单）：交付 15 个 .cs/.csv 全部 UTF-8 无 BOM / LF / 末尾换行（file-conventions 合规）；CSV 修头逐格脚本比对与 asset 三方互证。
- 验收全程零代码修改（唯一临时变异已逐字节还原）、零 taskkill、未 commit。任务可交主控入账。
