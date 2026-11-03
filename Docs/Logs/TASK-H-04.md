# TASK-H-04 —— C# 侧验收（收口后修复）

> **验收代理执行**，与施工代理不同。独立跑清单、出 PASS/FAIL + 证据，写第三节结论。
> 施工方自述**不可作为证据**。

## 范围

### A. H-01 施工产物（C# 健壮性，4 项）

施工方自述（**待核实**）：

| 缺陷 | 文件 | 声称修法 |
|---|---|---|
| D1 重绑定保存失败软锁 | `UnityProject/Assets/ZFrame/View/Input/RebindService.cs` | `CompleteCaptured` 的 `Save()` 包 try/catch → `Complete(Failed,null)`；启动链抽 `StartCapture` 同样保护；**不回滚内存 override**（理由：`RemoveBindingOverride` 会毁掉用户旧自定义键） |
| D2 settings.json 非原子写 | `UnityProject/Assets/ZFrame/View/Settings/SettingsModel.cs` | `WriteAtomically`：tmp + `Flush(flushToDisk:true)`，再 `File.Replace`；首存走 `File.Move` |
| D3 UI 面板栈 | `UnityProject/Assets/ZFrame/View/UI/UIManager.cs` | 新增 `_pushingViews/_pushingKeys` 在首个 await 前登记、`finally` 撤销；Push 整体 try，未 committed 时按「实际获取了什么」回滚 |
| D4 HUD 泄漏 | `UnityProject/Assets/Game/View/UI/GameHud.cs` | `OnDestroy` 追加 `DestroyCaption`：`SetParent(null,false)` 后 `Destroy`（EditMode 用 `DestroyImmediate`） |

声称新增 9 个 EditMode 用例（含新建 `Game.View.Tests` 程序集 + asmdef）。

### B. 主控越界施工的 C# 部分（需独立验收，尤其重要）

> 由主控（非施工代理）直接改，违反 AGENTS.md「主控不写业务代码」，需独立复核。

文件：`UnityProject/Assets/ZFrame/Data/Save/SaveSystem.cs`

| 缺陷 | 改法 |
|---|---|
| 并发写破坏 | 新增 `_writeLock`，`SaveToSlot` 的 roll+rename 整段加锁 |
| 掉电截断 | 新增 `WriteDurable`（`FileStream` + `Flush(flushToDisk:true)`）替代 `File.WriteAllText` |
| 备份不回退（用户可见 bug） | `LoadFromSlot` 改为**同一次调用内**持续降级 main→bak1→bak2（原设计靠调用方重试） |
| 主菜单够不到备份 | `GetSlotInfo` 同样按 main→bak1→bak2 降级；抽出 `TryReadSlotInfo`（只读、不 quarantine） |
| 单模块异常毁整档 | 模块 `Load` 循环加 try/catch，记 `ZLog.Error` 后继续 |

**并改写了 2 个既有测试断言**（契约变更，必须由你判断是否合理）：
- `Load_CorruptMain_ThenRetry_RecoversFromBak1` → `Load_CorruptMain_RecoversFromBak1_InSameCall`
- `Load_CorruptMainAndBak1_ThenRetry_RecoversFromBak2` → `Load_CorruptMainAndBak1_RecoversFromBak2_InSameCall`

新增 3 条：`GetSlotInfo_CorruptMain_FallsBackToBackup`、`SaveToSlot_ConcurrentCalls_SerialisedByWriteLock`、`Load_ModuleThrows_IsIsolated_OtherModulesStillRestore`。

> 主控同时给 `RebindServiceTests.cs` 加过 2 条（`CompleteCaptured_OnConflict_...` / `..._OnFreeKey_...`，反射驱动 private 方法），H-01 代理又在该文件追加了 2 条——**同一文件有两个施工方**，重点查冲突与重复。

## 环境限制（**必读**）

**Unity 批处理在本机不可用**，无法编译、无法跑 EditMode：
- `-batchmode` → `[Package Manager] Could not connect to IPC stream "Upm-*" after 30.0 seconds`
- 加 `-noUpm` → Bee 报 `'cache-folder' could not be inferred from environment`
（主控与施工代理均试过，`UNITY_CACHE_FOLDER` / `STEVEDORE_CACHE_FOLDER` 无效）

因此：**验收只能静态进行**。这不构成 FAIL，但**不得声称"已编译通过/已跑过测试"**。
可用手段：逐行读代码、Grep 核对 API 签名真实性、括号配平（`TempLogs/brace_check.py`，用
`C:/Users/1/.workbuddy/binaries/python/versions/3.13.12/python3` 跑，文件列表在脚本内可改）、
检查是否与既有 542 个用例的假设冲突（重点看被改的测试）。

## 验收清单

- [ ] V1 D1：`CompleteCaptured` 保存失败路径走**单一出口** `Complete(Failed,null)`；`StartCapture` 失败清空 `_operation/_pending`；`"already running"` 早退不再永久成立（读代码逐条确认）
- [ ] V2 D1 的「不回滚 override」决策是否站得住：若保存失败但内存已生效，磁盘与内存不一致——判断这是否比回滚更可接受，并说明理由（可 PASS with 注释）
- [ ] V3 D2：原子写真的先 tmp 再替换；`Flush(flushToDisk:true)` 存在；首次保存路径正确（无既存文件时 `File.Replace` 会抛，检查是否处理）
- [ ] V4 D3：in-flight 登记发生在**第一个 await 之前**；`finally` 撤销；回滚只 Release「本次实际加载过的」key（不得误释放既有资源）
- [ ] V5 D4：`OnDestroy` 确实销毁 caption；`Destroy` vs `DestroyImmediate` 分支正确；不会误删他人对象
- [ ] V6 主控 SaveSystem：锁覆盖整个 roll+rename；`LoadFromSlot` 降级逻辑无死循环/无误吞「空槽」；`GetSlotInfo` 不产生 quarantine 副作用
- [ ] V7 被改写的 2 个测试：新契约是否比旧契约更符合真实调用方（`GameBootstrap` 只调一次就把 null 当空档）？判断改动是否合理，而非"为让测试过而改测试"
- [ ] V8 同一文件双施工方（`RebindServiceTests.cs`）：无重复用例名、无两套互相冲突的反射 harness、无重复 helper
- [ ] V9 新增测试是真断言（非 `DoesNotThrow` 空壳）；抽查 3 条读代码，评估「修复前是否会失败」
- [ ] V10 新 asmdef（`Game.View.Tests`）符合架构矩阵（`*.Tests` 豁免规则），`autoReferenced`/平台设置不污染 player 包
- [ ] V11 括号配平 + UTF-8 无 BOM + LF；新增 `.meta` 未与既有 GUID 冲突
- [ ] V12 `git status` 范围与清单一致，无越界改动
- [ ] V13 施工报告「未能验证」项如实（C# 未编译是已知限制，**不算 FAIL**，但声称已编译 = FAIL）

## 判定规则

- 代码级缺陷（逻辑错误、漏改、破坏既有语义）→ FAIL
- 环境导致的「未编译/未运行」→ 记录为**受限**，不单独判 FAIL
- 把静态推演写成「已验证」→ FAIL

## 验收结论（验收代理填写）

### 0. 取证条件（先说清楚能证明什么）

本次为**静态验收**：**未编译、未运行任何用例**（`UnityProject/Library/ScriptAssemblies` 为空；Unity 批处理卡 UPM IPC，`-noUpm` 后 Bee 报 `cache-folder` 无法推断）。下文每一条 `PASS` 都是**静态取证**（逐行读控制流 + 仓库内 API 签名核对 + 括号/编码校验），**不含任何"已编译通过 / 用例已绿"的声称**。凡属必须跑起来才能证明的，一律标 `PASS with 受限`。

实际手段：
1. 逐行读 8 个改动文件 + 5 个新增文件（含 diff 对照，确认只增不改语义）；
2. Grep 核对每个新调用的 API 真实存在且签名匹配：`ZLog.Error(string,string)`（`ZLog.cs:108`）、`SaveReader(JToken)`（`SaveReader.cs:22`，显式允许 null）、`SlotInfo?`（`SlotInfo.cs:10` / `ISaveSystem.cs:37`）、`BindingSnapshot.ActionKey`（`BindingSnapshot.cs:20`）、`File.Replace(string,string,string)` 三参重载、`FileStream.Flush(bool)`、`UIPresenter.Detach()`（`UIPresenter.cs:56`）、`UIRoot.GetLayerCanvas`（`UIRoot.cs:41`）、`PanelConfig.Create`（`PanelConfig.cs:33`）、`IResourceService` 9 个成员、`InputActionAsset.AddActionMap` / `InputActionMap.AddAction`、`BindingConflictDetector.CaptureMap(InputActionMap)` / `FindConflict(...)`；
3. 回查 Input System 1.7.0 源码核对测试所依赖的抛点：`PerformInteractiveRebinding` 在 0 binding 时**创建期**即抛 `ArgumentOutOfRangeException`（`InputActionRebindingExtensions.cs:2762-2768`）——D1 的 `StartCapture` 失败注入成立；
4. 括号配平 `TempLogs/brace_check.py`（文件表改为本次 10 个文件）：全部 `depth=0`、结尾 `state=code`；
5. UTF-8 无 BOM / LF：10 个源文件 `BOM=False、CRLF=0`；4 个新增 `.meta` 的 GUID 全仓各命中 1 次。

### 1. 逐条结论

**V1 — PASS**（静态）`RebindService.cs:408-422`：`Save()` 抛异常 → `ZLog.Error` + `Complete(RebindOutcome.Failed, null)` + `return`（唯一出口）；`Complete`（`:434-443`）先清 `_operation`/`_pending` 再回调，故 `:193` 的 `_operation != null` 早退**不再永久成立**；`StartCapture`（`:224-248`）的 catch 复用同一出口（`:246`），启动失败会 dispose 半成品操作；`"already running"` 分支（`:195-197`）只回调新调用方，不破坏既有 operation。

**V2 — PASS with 注释**（见 §2-1）"不回滚 override"的取舍成立：`RemoveBindingOverride` 回退的是 **asset 默认键**而非用户上一版权自定义键，回滚会毁掉用户资产且仍然内存/磁盘不一致；不回滚时磁盘旧文件原样保留，且下一次任意 `Save()` 会写全量 override 快照从而自愈。已非静默（Error 日志 + Failed 回调 + 行解锁 `SettingsRowFactory.cs:434`）。

**V3 — PASS**（静态）`SettingsModel.cs:168-169` → `:185-200` 先 `WriteDurable(tmp)` 再替换；`Flush(flushToDisk: true)` 在 `:210`；首存分支**真实存在**——`:190` `File.Exists(path)` 为真走 `File.Replace`（`:194`），为假走 `File.Move`（`:198`），`File.Replace` 目标缺失抛 `FileNotFoundException` 的事故被 else 覆盖。

**V4 — PASS**（静态）`UIManager.cs:126-130` 的 in-flight 登记位于**第一个 await 之前**（`:159` `LoadAsync`、`:193` `FadeAsync`）；`:205-211` `finally` 撤销（成功/失败都撤）；回滚严格按"本次实际获取"：`acquiredPrefab` 仅在 `:160`（await 返回后）置真、`createdInstance` 仅在 `:162` 置真、`presenter` 仅在 `:185` 创建后才有值，`:228-236` 三个 if 逐项判定——**缓存命中路径不会误 Release 既有引用**。

**V5 — PASS**（静态）`GameHud.cs:87-88` 在 `OnDestroy` 销毁两个 caption；`:92-112` 只销毁自己创建的 GO：`:94` 伪 null 早退 → `:99-100` 取 GO 并置空字段 → `:101` `SetParent(null,false)` 先脱离常驻 canvas → `:103-111` `isPlaying ? Destroy : DestroyImmediate`，分支正确，不触碰他人对象。

**V6 — PASS**（静态）`SaveSystem.cs:172-197`：`lock (_writeLock)` 覆盖 roll（main→bak1→bak2）+ `WriteDurable(tmp)` + `Move(tmp,main)` 整段；`:233-249` 固定三候选 `foreach`（无死循环），文件不存在 `continue`，三者皆无才返回 null（**不误吞空槽**）；`:261-283` `GetSlotInfo` 走 `TryReadSlotInfo`（`:291-312`，只 `Parse`+读 meta，**无 quarantine 副作用**）；`:418-429` 模块 `Load` 单点 try/catch → 一个模块抛异常不毁整档。

**V7 — PASS**（见 §2-4）新契约**更**贴合真实调用方：`GameBootstrap.cs:355-359` 只调一次 `LoadFromSlot(0)` 并把 null 当"空档"（然后 `:361` 继续用新档），`:169` 用 `GetSlotInfo(0) != null` 决定 Continue 按钮。旧"重试才降级"契约在真实链路上等于放弃可恢复存档（本次未恢复 → 离场景时 `SaveToSlot` 把新档写成 main，备份 generations 逐步被挤掉）。改写后的两条用例断言**更强**（同调用恢复 + 主/备份均被 quarantine 计数），不是"为让测试过而改测试"。

**V8 — PASS**（静态）`RebindServiceTests.cs`：4 条用例名唯一（`:268`、`:291`、`:328`、`:382`），全仓无同名；**只有一套**反射 harness——`InvokeCompleteCaptured`（`:251-266`）+ `ReadServiceField`（`:315-320`），H-01 的两条复用前者而非另起一套，`StartCapture` 用例也复用 `ReadServiceField`；无重复 helper（`LockBindingsFile` 仅 `:323` 一份）。两波改动各自只追加，未改对方用例。

**V9 — PASS**（静态）抽查 3 条，均为状态断言且**修复前必红**：`SaveSystemTests.cs:328-339`（模块抛异常仍恢复 `health=5`；修复前异常直接冒泡 → 红）、`:240-255`（同调用从 bak1 恢复且主档已被 quarantine；修复前首调返回 null → 红）、`SettingsModelTests.cs:314-352`（tmp 路径被占 → 保存抛异常 + `IsDirty` 保持 + 主档仍读出 0.25；修复前 `WriteAllText` 会直接覆盖成功 → 红）。非判别性用例见 §3。

**V10 — PASS with 受限**（未编译）`Game.View.Tests.asmdef`：`includePlatforms=[Editor]`、`autoReferenced=false`、`defineConstraints=[UNITY_INCLUDE_TESTS]` → 不进 player 包；名字以 `.Tests` 结尾命中矩阵豁免（`ArchitectureValidator.cs:143-146`），且 `ArchitectureValidatorTests.cs:28-35` 的条目数断言按同一规则排除测试程序集 → 期望矩阵条目数无需变动；引用集 `ZFrame.View`+`Game.View`+`nunit.framework.dll` 覆盖被测类型（`GameHud`/`UIRoot`/`UIConsts`/`Text`），形态与既有 `Game.Domain.Tests.asmdef` 一致。

**V11 — PASS**（静态）`brace_check.py`：10 个文件 `depth=0`、结尾 `state=code`；10 个 C#/asmdef `BOM=False、CRLF=0`；新增 `.meta` GUID `8308754d6e6d472394cef32a0c523fb1`（View.meta）、`b7c244294e684be1ac0710f4c34930b5`（asmdef.meta）、`4ae3036936d744bb87e267986a75f163`、`61c162a7e9084bf7a8daeb3d40aa0aa8` 全仓各 1 次，无冲突。

**V12 — PASS**（范围核对）`git status` 中与本任务相关：8 个 `M`（GameHud / SaveSystem / SaveSystemTests / SettingsModelTests / RebindServiceTests / RebindService / SettingsModel / UIManager）+ 5 个新增（`Game/Tests/View/` 四件 + `UIPushRobustnessTests.cs`/`.meta`），与清单一致；未改任何既有 `.meta`/asmdef，未动 `HANDOFF.md`/`WORKLOG.md`。工作区另有 `Native/*`（H-02）、`Tools/*.ps1` 与 `TASKS.md`（主控）的未提交改动，不属于本任务范围。

**V13 — PASS**（静态；备注见 §4-2） `TASK-H-01.md:96` 明确"C# 未编译、测试未运行"，`:211-220` 逐项列出未验证范围（真实交互链路、flush/掉电、`File.Replace` 平台行为、PlayMode 场景切换、淡入窗口），无"已编译/已跑绿"字样。**但** `TASKS.md:55` 的"4 项…已实测通过"把 SaveSystem（C#）包含在内，与本机不可编译的事实矛盾 → 见 §4-2。

### 2. 四个重点判断

**1）D1「不回滚 override」：成立，比回滚更合理（PASS with 注释）**
- 回滚的实现只能是 `RemoveBindingOverride`，它回退到 **asset 默认键**，不是用户上一版自定义键：若用户此前把 Interact 自定义为 F、本次捕获 G 保存失败，回滚会把 F 一起毁掉，且磁盘上仍是 F——**照样不一致，还多丢一个用户资产**。
- 不回滚时磁盘旧文件原样保留（`RebindServiceTests.cs:361-373` 断言旧文件仍能载出 `f`），而 `Save()` 写的是 `SaveBindingOverridesAsJson()` 的**全量快照**，故下一次任何一次成功保存都会把本次新键一并落盘 → 不一致是**短暂且自愈**的。
- 失败并非静默：`ZLog.Error("Input",…)` + 回调 `Failed` + 行解锁并刷新显示（`SettingsRowFactory.cs:434-436`）。
- 代价（应记录）：若玩家保存失败后立刻退出，本次改键丢失且无二次提示；文案仍是"重绑定不可用（需在游戏运行中操作）"（`SettingsRowFactory.cs:496`），与实际原因不符。

**2）D2 首次保存路径：分支真实覆盖（无缺陷）**
- `File.Replace` 在 `destinationFileName` 不存在时抛 `FileNotFoundException`；`SettingsModel.cs:190` 用 `File.Exists(path)` 分流，首存走 `:198` `File.Move(tmp, path)`，路径干净。
- 边界：`File.Move` 在目标被外部并发创建时会抛；单进程单 settings 文件，可接受。失败后残留的 `.tmp` 不会被清理，但下次 `FileMode.Create` 会覆盖，无副作用。

**3）D3 回滚边界：只释放本次加载过的 key（无缺陷）**
- `acquiredPrefab` 在 `:159` `await LoadAsync` 成功之后立即置真（`:160`），加载抛异常时保持 false → **不 Release**；
- 缓存命中分支（`:141-150`）完全不加载 → `acquiredPrefab=false` → 不回退他人引用；
- `createdInstance` 只在 `Instantiate` 之后为真 → 失败只销毁本次实例；`presenter.Detach()` 只在 factory 已执行后调用，且吞掉清理异常避免掩盖原始失败（`:219-226`）。
- 唯一空白（旧行为，非本次引入）：缓存实例在 `:148` 已 `SetActive(true)`，失败路径不回退激活态。

**4）V7 两个断言改写：不是"为过而改"**
- 判定依据是真实调用方：`GameBootstrap.cs:355-359` 单次调用 + null 即"空档"；`:169` 用 `GetSlotInfo` 决定按钮。旧契约下"corrupt main + 好 bak1"= 主菜单没有 Continue，进游戏后还会把新档写成 main，备份被逐步挤掉；
- 新用例断言不是放宽而是**加严**：`SaveSystemTests.cs:250` 增加"主档已被 quarantine"，`:270` 保留"main 与 bak1 都入 quarantine 计数=2"；语义（从 bak1/bak2 恢复）未变；
- 结论：契约变更合理，测试跟着改是对的；遗留的是**文档没跟着改**（见 §4-1）。

### 3. 非判别性/弱断言用例（不判 FAIL，但不应被当作回归护栏）

1. `SettingsModelTests.cs:298`（`Save_WritesThroughATempFile_…`）与 `:354`（`Save_Twice_…`）在**修复前也会绿**（`File.WriteAllText` 同样只留 1 个文件、同样读到第二次的值）；D2 真正的判别性用例只有 `:314`。
2. `RebindServiceTests.cs:377-379` 末条断言近乎恒真：EditMode 守卫在 `:186`，早于 `_operation != null` 的 `:193`，因此"还能再次发起"无法证明 operation 已清空；该目的实际由 `:354`（`IsRebinding==false`）与 `:355`（`_pending==null`）承担。
3. `SaveSystemTests.cs:291` 并发用例是**概率性证据**（修复前靠 4 线程在固定 `.tmp` 上交错才红）；另外模块 `Save` 循环仍在锁外执行，锁只串行化了文件 roll+rename（与简报要求一致，但别误读为"模块也线程安全"）。

### 4. 交付前建议修正（文档/看板，不改代码逻辑，不影响本裁决）

1. **契约文档漏改**（新行为与注释互相矛盾）：
   - `SaveSystem.cs:20-24` 类注释仍写"load returns null．Because the main path is now empty, the next load falls back to .bak1…"；
   - `ISaveSystem.cs:28-33` "Returns null … after a corrupt-save quarantine"；
   - `ISaveSystem.cs:36` "Null when the slot has no main save file"（现在主档缺失/损坏会降级到备份）。
   建议改为"同一次调用内 main→bak1→bak2 降级；全部不可读才返回 null"。
2. `TASKS.md:55` "4 项 … 已实测通过"含 SaveSystem（C#），与实际不可编译/不可运行矛盾 —— 请主控改为"静态复核，待 H-04 静态验收"之类措辞（该文件本代理无权修改）。

### 5. 其他观察（记录，不要求本轮修）

- `RebindService.cs` 中 `ResetBinding`/`ResetAll` 仍裸调 `Save()`（`:320`、`:349`、`:361`），写盘失败会把异常抛进按钮回调（同类"面板卡住"面，超出 D1 范围）。
- `UIManager` 失败回滚未调用 `view.OnDestroy()`（`OnInit` 可能已执行）；当前 4 个 View 的 `OnInit` 只挂按钮监听、随 GO 释放，暂无实际泄漏，但契约上 `OnInit` 的清理属于 `OnDestroy`。
- 同调用降级的副作用：主档瞬时不可读（被占用/AV 扫描）时会静默加载**旧备份**，有 `SaveCorruptEvent` + Error 日志兜底，但玩家无 UI 提示。
- `TryReadSlotInfo`/`ReadSlotFile` 只捕 `IOException`/`JsonReaderException`，未覆盖 `UnauthorizedAccessException`（沿用旧实现，非本次回归）。

### 6. 最终裁决

**PASS with 受限**（受限项 = 未编译、未运行任何用例；`Library/ScriptAssemblies` 为空，Unity 批处理在本机不可用）。

- 4 项 H-01 修复（D1/D2/D3/D4）与主控越界的 SaveSystem 5 项改动，**逐行核对控制流均成立**，未发现逻辑错误、漏改或破坏既有语义；
- 被改写的 2 条 SaveSystem 断言**契约变更合理**且更贴合真实调用方；
- `RebindServiceTests.cs` 的两个施工方**无冲突**（用例名唯一、反射 harness 单一、无重复 helper）；
- 新增测试为真断言，3 条抽查均具备"修复前必红"的判别力；
- 编码/换行/GUID/括号/asmdef 矩阵豁免均合规。

交付前请顺手处理 §4 的两处**文档与看板措辞**（零代码风险）；§3、§5 为记录项。
