# TASK-A-M1-03 SaveSystem 存档系统（多槽位/原子写/备份/迁移/ISaveable）

状态: doing | 派发: 2026-10-24 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：A 轨数据层收尾——玩家进度存档系统：多槽位、槽位元数据（时间戳/游戏时长/版本）、原子写、滚动自动备份、损坏预案、版本迁移链、ISaveable 模块化收集。A 轨 M1 随此收口。

**前置**：需装官方包 `com.unity.nuget.newtonsoft-json`（Packages/manifest.json 加 `"com.unity.nuget.newtonsoft-json": "3.2.1"`，标准包，参照 ADR-005 先例）。

**设计要点**：
1. `ZFrame/Data/Save/`：
   - `ISaveable { string SaveKey { get; } void Save(SaveWriter w); void Load(SaveReader r); }`（SaveWriter/SaveReader 包 Newtonsoft JToken：写字典 Set(string,JToken)、读 TryGet；null 安全）
   - `SaveData`（ JObject 容器 + `Version int` + `Meta`：存档时间/游戏时长/显示名）
   - `SaveSystem : ISaveSystem`：
     - `Register(ISaveable)`（重复 Key 拒绝）；`SaveToSlot(int slot)`：收集全部 ISaveable → J 对象 → **原子写**（先 `.tmp` 再 File.Replace/Move，覆盖前把旧档滚成 `.bak1`/`.bak2`——自动备份保留最近 2 份）
     - `LoadFromSlot(slot)`：主档缺失尝试 .bak1→.bak2；解析失败把坏档改名 `.corrupt_yyyyMMddHHmmss` 留现场并返回 null（错误事件 `SaveCorruptEvent{slot,reason}` 经 EventBus）
     - **版本迁移**：`IMigration { int FromVersion; JObject Migrate(JObject data); }` 注册链，按 FromVersion 升序执行到当前版本；迁移失败同损坏预案
     - 槽位元数据查询 `SlotInfo? GetSlotInfo(slot)`（不解析全档，只读 meta——meta 存档头部独立 J 段）；`DeleteSlot`；`SlotCount=3` 常量
     - `AutoSave()` 钩子（本期手动调用；定时/事件触发留接口）
     - 路径：`Application.persistentDataPath/saves/slot_{n}.json`（测试可注入根目录——构造函数参数，**必须可注入**否则单测写真实用户目录）
   - `ZFrameData.Init()` 扩展注册（幂等）
2. 单测（ZFrame.Data.Tests 追加，注入 temp 根目录）：
   - 保存→加载 roundtrip（两个 ISaveable 模块各自数据）
   - 原子性：保存后 tmp 文件不存在；连续 3 次保存后 bak1/bak2 滚动正确（内容=前两次）
   - 损坏：手写坏 JSON→Load 返回 null+主档被改名 .corrupt+自动读 bak1 恢复成功
   - 迁移：v1 数据经迁移链升到当前（构造旧结构用例）；未知更高版本拒绝
   - 槽位元数据正确（时间/时长/版本）；删除槽位；重复 Register 抛
   - Key 冲突与 null 安全（读不存在字段返回默认）
3. 自测：EditMode 全绿 + ValidateFromCommandLine exit 0（Unity 纪律）

**约束**：可动 `UnityProject/Assets/ZFrame/Data/`、`UnityProject/Packages/manifest.json`（仅加 Newtonsoft 包）、`UnityProject/Assets/ZFrame/Tests/`、本任务日志；不 git commit；UTF-8 无 BOM/LF；不写真实 persistentDataPath（测试全用注入目录）。

**交付物**：SaveSystem 全件+单测全绿+施工报告四节（含包版本与回滚说明）。

## 施工报告（施工代理写）

### 一、实现清单

**前置依赖**：`Packages/manifest.json` 加入 `"com.unity.nuget.newtonsoft-json": "3.2.1"`（官方标准包，解析源 packages.unity.cn，首次批处理已成功下载进 PackageCache）；`packages-lock.json` 为 Unity 自动生成的伴随变更（仅新增该包条目，无其他改动）。`ZFrameData.Version` 0.1.1 → 0.1.2。

**运行时（`Assets/ZFrame/Data/Save/`，ZFrame.Data 程序集内新增目录，未新增 asmdef——架构矩阵零改动；Newtonsoft 为 autoReferenced 预编译 DLL，asmdef 无需显式引用）**

- `ISaveable`：`SaveKey`（稳定兼容锚点）/`Save(SaveWriter)`/`Load(SaveReader)`
- `SaveWriter`：包 JObject 模块段；`Set(string, JToken)` + string/int/float/bool 便利重载；null 值存显式 JSON null（不抛）；null/空 key 抛 ArgumentException
- `SaveReader`：包 JToken（null reader=空段，null 安全）；`TryGet`（缺 key/JSON null 均为 false）+ `GetString/GetInt/GetFloat/GetDouble/GetBool`（缺字段/JSON null/错类型一律返回调用方默认值，InvariantCulture，捕获 Format/InvalidCast/Overflow）
- `SaveMeta`：头部元数据（`savedAtUtc` ISO8601 RoundtripKind / `playtimeSeconds` / `displayName`），To/From JToken 均容错
- `SaveData`：文件根 `{ "version": n, "meta": {...}, "modules": { saveKey: {...} } }`；`ToJObject`/`TryParse`/`TryReadVersion`；字段名为常量
- `SlotInfo`：只读 struct（Slot/Version/SavedAtUtc/PlaytimeSeconds/DisplayName）
- `SaveCorruptEvent`：struct `{Slot, Reason, QuarantinedPath}`，经 `EventBus.Fire` 发布
- `IMigration`：`{int FromVersion; JObject Migrate(JObject data)}`——入参/返回都是**完整文件根**，版本号由 SaveSystem 负责戳（FromVersion+1），链必须连续
- `ISaveSystem` / `SaveSystem`：
  - `SlotCount=3`（`DefaultSlotCount` 常量）；`CurrentVersion=2` 常量
  - `Register`（重复 Key/空 Key 抛）、`Unregister`、`RegisterMigration`（重复 FromVersion 抛）、`Clear`
  - `SaveToSlot`：收集全部 ISaveable → 各自 JObject 段 → **原子写**：旧 main→`File.Move`滚成 `.bak1`（旧 bak1→`bak2`，先删 bak2，保留最近 2 份）→ 写 `.tmp`（UTF-8 无 BOM）→ `File.Move(tmp, main)`
  - `LoadFromSlot`：候选 main→`.bak1`→`.bak2`，**仅"文件缺失"才落下一候选**；解析失败/无版本字段/更高版本/迁移失败/迁移返回 null → 坏档改名 `slot_n.json[.bakX].corrupt_yyyyMMddHHmmss`（同秒冲突加 `_k` 后缀；改名本身失败则留原地）+ ZLog.Warn + `SaveCorruptEvent` + 返回 null——重试时主路径已空，自动读 bak1/bak2 恢复
  - 迁移：`while version < CurrentVersion` 按 `FromVersion` 查链逐级执行；未知更高版本（> CurrentVersion）拒绝走同一损坏预案；迁移成功后把模块段分发给已注册 ISaveable（缺失段用空 reader 调 Load，null 安全）
  - `GetSlotInfo`：只读查询，只取 meta 头部段（损坏返回 null，不做隔离副作用）
  - `DeleteSlot`：删 main/bak1/bak2/tmp（幂等，不动 .corrupt 留现场）
  - `AutoSave()`：写 `AutoSaveSlot`（默认 0，越界告警跳过）；定时/事件触发留调用方
  - 路径：`{root}/slot_{n}.json`；root 注入构造函数参数，缺省 `Application.persistentDataPath/saves`
- 注册：`ZFrameData.Init()` 幂等注册 `ISaveSystem`（`IsInitialized` 纳入判定），`Shutdown` Clear+反注册

**测试（`Assets/ZFrame/Tests/Data/SaveSystemTests.cs`，29 例；`ZFrame.Data.Tests.asmdef` precompiledReferences 追加 `Newtonsoft.Json.dll`——overrideReferences:true 下必须显式列出）**

覆盖简报全部条目：roundtrip（两模块）、tmp 不残留、3 连存 bak1/bak2 滚动内容断言（=前两次）、坏 JSON→null+改名+EventBus 事件、重试自动读 bak1、main+bak1 双坏自动读 bak2、v1 手工旧结构经迁移链升级（hp→health 重命名）、未知高版本拒绝、缺迁移步骤失败、迁移抛异常同预案、SlotInfo 元数据（时间窗/时长/版本）、空槽 null、DeleteSlot 删三件、重复 Register 抛、空 Key 抛、重复迁移抛、Unregister、reader 缺字段/null reader/JSON null/错类型默认值、writer Set(null)、模块段缺失用空 reader 不抛、槽越界 5 个 API 抛、AutoSave 写指定槽/越界不写、`ZFrameData.Init` 幂等注册+Shutdown 反注册、缺省根位于 persistentDataPath（仅断言路径字符串，不写盘）。**全部用例注入 `Path.GetTempPath()` 下的临时根目录，测试源码无任何真实 persistentDataPath 写入。**

### 二、关键设计取舍（3 条设计要点落位）

1. **原子写+滚动备份的顺序**：main→bak1、bak1→bak2 均为同目录原子 rename，之后 tmp→main 也是 rename；任意瞬间磁盘上要么有旧档（bak1/bak2）要么有新档（main），无空窗
2. **"损坏返回 null"与"自动读 bak1"的衔接**：简报两句按"损坏当次返回 null（隔离现场），重试时主路径已缺失→自动落 bak1/bak2"实现——与验收用例"Load 返回 null+改名 .corrupt+自动读 bak1 恢复成功"完全对应（恢复发生在下一次 Load）
3. **迁移链**：迁移函数操作完整文件根（meta 也可迁），版本号由 SaveSystem 统一戳 FromVersion+1，防迁移函数漏改/乱改版本字段；链断档（无 FromVersion=V 的注册项）按损坏预案处理

### 三、自测证据（Unity 批处理纪律：清锁/单实例/轮询 xml）

- 包解析：`Library/PackageCache/com.unity.nuget.newtonsoft-json@3.2.1` 就位（packages.unity.cn）
- EditMode：`TempLogs/m1_03_editmode.xml` → `testcasecount="142" result="Passed" total="142" passed="142" failed="0"`（新增 29 例，基数 113 全保留；首轮 141/142——测试 helper 通配符漏匹配 `.bak1.corrupt_*`，修 helper 后复跑全绿）
- 架构校验：`-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine` → **exit 0**，`TempLogs/arch_report.txt` 违规数 0 PASS
- 过程中无僵死 Unity（2 轮批处理均正常退出）

### 四、文件规范与说明

- 全部新文件 UTF-8 **无 BOM**、LF、文件尾换行（字节级复核通过）；.meta 全部由 Unity 自动生成，未手改；未 git commit
- 改动范围：`UnityProject/Assets/ZFrame/Data/`（Save/ 10 文件+ZFrameData.cs）、`UnityProject/Packages/manifest.json`（仅加包）+ packages-lock.json（包锁伴随，建议随 commit 入库）、`UnityProject/Assets/ZFrame/Tests/Data/`（测试+asmdef 引用）、本日志
- 回滚说明：还原 `manifest.json`/`packages-lock.json` 中 newtonsoft 两处、删除 `Assets/ZFrame/Data/Save/`（含 Save.meta）、还原 `ZFrameData.cs`、删除 `SaveSystemTests.cs(.meta)`、还原测试 asmdef 即完全回滚；无数据迁移、无资产变更
- 遗留建议（不阻塞）：`CurrentVersion` 现为 2，后续存档布局不兼容变更时需同步注册 IMigration（FromVersion=旧值）并 bump 该常量

## 验收清单（主控写）

1. 自跑 EditMode 全绿（用例数写入结论）
2. 原子写+备份滚动真实断言（tmp 消失/ bak1=bak2 旧值）用例核对
3. 损坏预案链（主档→bak1→bak2→null+改名）用例核对
4. 迁移链执行与拒绝未知高版本用例核对
5. 注入根目录使用确认（无真实用户目录写入——grep 测试源码）
6. ValidateFromCommandLine exit 0（自跑）
7. 文件规范+施工报告完整

## 验收结论（验收代理写）

**结论：PASS（7/7 项全过）**。验收子代理独立复跑，与施工代理不同人，未修改任何代码。

1. **PASS** 自跑 EditMode：`TempLogs/accept_m1_03_editmode2.xml` → `testcasecount="142" result="Passed" total="142" passed="142" failed="0"`（SaveSystemTests 29 例全在内；遵守单实例纪律，跑前确认无 Unity.exe/锁文件）
2. **PASS** 原子写+备份滚动真实断言核对：`Save_LeavesNoTmpFileBehind`（SaveSystemTests.cs L166 断言 .tmp 不存在）；`Save_ThreeConsecutiveSaves_BackupsRollCorrectly`（L175 解析三份文件内容断言 main=3、bak1=2（旧值）、bak2=1（更旧值）、无 .bak3）
3. **PASS** 损坏预案链核对：`Load_CorruptMain_ReturnsNull_QuarantinesFile_FiresEvent`（L197：返回 null+主档消失+恰 1 个 .corrupt_* 改名+EventBus 收到 SaveCorruptEvent{slot,reason,quarantinedPath} 全字段断言）；`..._RecoversFromBak1`（L221 重试恢复 bak1=health 1）；`..._RecoversFromBak2`（L237 main+bak1 双坏→bak2 恢复+2 个隔离文件）；实现（SaveSystem.cs L194-215）候选链仅按"文件缺失"下落，与用例/报告取舍一致
4. **PASS** 迁移链核对：`Load_V1Save_MigratesToCurrent_AndRestoresModules`（L265 手工 v1 旧结构 hp→health，断言升到 CurrentVersion 且模块恢复）；`Load_UnknownNewerVersion_IsRejected_AndQuarantined`（L303 CurrentVersion+100 拒绝+隔离+事件 reason 含 "newer than supported"）；另有缺迁移步骤（L326）/迁移抛异常（L347）同预案两例
5. **PASS** 注入根目录确认：grep 测试源码仅 3 处注释+1 处字符串断言（SaveSystemTests.cs:599 `StringAssert.Contains(Application.persistentDataPath, ...)`，不写盘）；构造注入 `SaveSystem(string rootDirectory = null)`（SaveSystem.cs:79）；所有用例注入 `Path.GetTempPath()` 临时根（L89），TearDown 清理
6. **PASS** ValidateFromCommandLine 自跑 exit 0，`TempLogs/arch_report.txt` 违规数 0、结果 PASS（15 个 asmdef，矩阵 10 条目）
7. **PASS** 文件规范抽查：`Assets/ZFrame/Data/Save/` 10 文件+SaveSystemTests.cs+ZFrameData.cs 全部 UTF-8 无 BOM、无 CRLF（字节级检查）；Packages/manifest.json+packages-lock.json 均为 `com.unity.nuget.newtonsoft-json: 3.2.1`；测试 [Test] 计数 29 与报告一致；施工报告四节完整（含包版本、回滚说明、自测证据、设计取舍）

备注（不阻塞）：①验收首轮自跑误加 `-quit` 与 `-runTests` 同发导致测试被跳过（exit 0 无 xml），属验收方调用错误，去除 `-quit` 重跑即过——后续代理跑测试勿带 `-quit`；②损坏当次返回 null、备份恢复发生在下一次 Load，系简报两句的既定实现取舍，已核对一致；③未 git commit（遵纪律）。
