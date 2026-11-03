# TASK-A-R9-03 —— A-R9-02 遗留清账：失效化不能把唯一的自愈凭据删掉

> 施工代理按本简报实现并自测，第 2 节写施工报告。**禁止 git commit**。验收由另一代理执行。

## 任务简报（主控写）

### 背景

A-R9-02（DRS 残留自愈）第 3 轮验收 PASS，但留了 2 条同族遗留。本质是**同一个判断反了**：

`PersistJournalEntry` 落盘失败 → `AbandonRenderScaleMeasure` 放弃本会话的 RenderScale measure。
它做两件事：① 回滚已脏资产；② 失效化日志条目。问题在于——

- **遗留 1**：回滚写的**返回值被忽略**（`FrameGovernorHost.cs:471` 附近
  `if (!Mathf.Approximately(...) && TryWriteScale(urp, baseline))` 只控制日志输出）。
  回滚被拒时资产仍是脏值（如 0.95），但紧接着 `InvalidateJournalEntries(urp)` 把唯一能自愈的条目删了
  → 下次启动无日志 → **资产永久脏值**。验收实测：`asset=0.95 journalExists=False nextBootStatus=NoJournal`。
- **遗留 2**：`InvalidateJournalEntries` 在剩余条目 `Save` 失败时直接 `_recoveryJournal.Delete()` **整份删除**
  （`FrameGovernorHost.cs:520` 附近）。多资产场景下这会连带抹掉**其他资产**的有效保护。

### 主控定的契约（关键判断已定，不要另起方案）

**核心原则：宁留勿删。** 现注释的推理是"过期条目会导致永久损失"，但事实相反——
恢复时走 G3 校验（资产当前值 == `lastWritten` 才回原值），因此：

- **过期条目 = 安全失败**：值不匹配 → 判为"第三方已改" → 拒绝恢复 + 清理 + 告警。不会误伤。
- **缺失条目 = 不可逆损失**：资产脏了却没有凭据，谁都救不回来。

据此：

1. **回滚结果必须被判定**：`AbandonRenderScaleMeasure` 中 `TryWriteScale(urp, baseline)` 的返回值要真正参与分支。
2. **回滚成功（或本就未脏）** → 按现状失效化条目（资产已回原值，条目无用；删不掉也无碍，下次走 G3 安全拒绝）。
3. **回滚失败** → **必须保留该资产的条目**（这是唯一的自愈凭据），并打一条可读告警说明
   "rollback refused; journal entry kept so the next boot can heal it"。**不得**调用会删除它的失效化路径。
4. **`Save` 失败时禁止整份 `Delete()`**：保留磁盘上的原文件不动。理由同上——保留只会让某条走 G3 安全拒绝，
   删除会让**所有**资产失去保护。注释需写明这个推理（替换现在那段"missing note costs less than a stale note"）。
5. 第 3 轮已 PASS 的行为不得回归：写前落盘顺序、会话级停用、单条告警、G3 不覆盖、损坏隔离、`Shutdown()` 清理。

### 必须新增的测试（真断言，修复前必红）

- **T4 `RollbackRefused_KeepsJournalEntry_AndNextBootHeals`**：
  构造"日志可写、资产可写降级 → 再让日志写失败 → 且回滚写被拒"（用可注入的 renderScale target 拒绝写入）。
  断言：磁盘上**仍存在**该资产条目、`lastWritten` 等于资产当前脏值；随后模拟下次启动 → **能恢复**、不出现永久脏值。
- **T5 `JournalResaveFails_DoesNotWipeOtherAssetsEntries`**：
  两个资产各有一条目 → 触发失效化且剩余条目的 `Save` 失败 → 断言**文件仍存在**且另一个资产的条目**仍在**（未被整份删除）。
- 若现有用例的断言与上述契约冲突，**只改必要的那几处**，并在报告中逐处说明理由（第 3 轮验收会查"是否为过而改"）。

### 硬约束

- 只改 `UnityProject/Assets/ZFrame/View/Pipeline/**` 与 `Tests/Pipeline/**`。
- `FrameGovernor.cs` / `GovernorLadder.cs` / `GovernorTuning.cs` / `SettingsBootApplier.cs` **零改动**。
- **不动任何 `URP_*.asset`**（改了即 FAIL）；不动 `ProjectSettings/`。
- **不改 `ZLog` 通道可见性**（全项目既有问题，不在本任务范围）。
- `.cs` 无 BOM、LF。**禁止 git commit**。

### 自测（必做，贴实际输出）

`unset http_proxy https_proxy all_proxy` 后跑六段全量
`powershell -NoProfile -ExecutionPolicy Bypass -File Tools/run_tests.ps1`：
基线 native **190/710,800**（不应变）、editmode **676**、playmode **6**、三校验器 exit 0；新增用例后 editmode > 676。
跑完确认 `git status --short` 无 `URP_*.asset`、无 `Unity.exe` 残留。
（`Assets/Plugins/ZRender/zrender.dll` 会因六段重构建变 M，属正常副产物。）

### 环境纪律

- 构建/测试前 `unset http_proxy https_proxy all_proxy`（重复大小写代理变量会让 MSBuild 抛「字典重复键」）
- cmake 不在 PATH：`CM="/c/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"`
- **PowerShell 工具无法执行外部 exe** → 一律用 Bash 工具
- 清理用 `mv` 归档到 `TempLogs/archive_pre_audit/`，不要 `rm -rf`

### 交付

「施工报告」写入第 2 节：文件:行号、改法、T4/T5 实际输出、六段实测数字、改动了哪些既有断言及理由、未验证项。

---

## 施工报告（施工代理写）

**结论**：两条同族缺陷按「宁留勿删」改完，判断点各自挪了一处——回滚**结果参与分支**（失败即保留条目），
`Save` 失败**不再整份删除**（原文件不动）。六段全绿 `EXITCODE=0`；native 仍 **190/710,800**、editmode **678 = 676 + 2**、
playmode 6、三校验器 exit 0。**既有断言一条未改**（15 条 `RenderScaleRecoveryTests` = 13 条旧 + 2 条新）。
定向变异（把「回滚失败保留条目」改回无条件删除）**恰好 T4 变红**、T5 保持绿，见 §5。

### 1. 改法（文件:行号）

| 位置 | 改动 |
|---|---|
| `View/Pipeline/FrameGovernorHost.cs:471-529` `AbandonRenderScaleMeasure` | 拆成三支：①资产已在基线 → 移除内存账本；②回滚写成功 → 移除账本 + 打「rolled back」Info；③**回滚写被拒 → `rollbackRefused = true`，条目与内存基线都保留**（`Shutdown()` 还会再试一次），并把内存条目的 `LastWrittenScale` 拉回资产的真实当前值（`PersistJournalEntry` 之前已把它推进到"写失败的那个新值"，内存与磁盘必须一致，否则将来一旦被重新序列化就会造出一条假 `lastWritten`）。:509-518 只在**回滚没被拒**时才走 `InvalidateJournalEntries`；被拒时改打一条可读告警：`... is still 0.95 — rollback refused; journal entry kept so the next boot can heal it back to 1.00.` |
| `View/Pipeline/FrameGovernorHost.cs:546-570` `InvalidateJournalEntries` | `Save` 失败分支的 `_recoveryJournal.Delete()` → **不删除、原文件原样留着**，只补一条 Info 说明"上一份文件被保留，该条目已过期、下次启动会被 G3 拒掉"。`_journalEntries.Count == 0`（无别人可保护）时仍 `Delete()`，与原行为一致。 |
| `View/Pipeline/FrameGovernorHost.cs:531-545`（文档注释） | 用新推理**替换**旧推理「a missing note costs less than a stale note」：恢复走 G3（资产当前值 == `LastWrittenScale` 才写），所以**过期条目=安全失败**（拒恢复+清理+告警），**缺失条目=不可逆损失**（脏资产没凭据 → 下次开机当用户基线，且会连带剥掉**其它资产**的保护）。`AbandonRenderScaleMeasure` 的注释同步改成「只有回滚真的落地才失效化」。 |
| `Tests/Pipeline/RenderScaleRecoveryTests.cs:456-520` | 新增 **T4** `RollbackRefused_KeepsJournalEntry_AndNextBootHeals`。 |
| `Tests/Pipeline/RenderScaleRecoveryTests.cs:522-597` | 新增 **T5** `JournalResaveFails_DoesNotWipeOtherAssetsEntries`。 |

`FrameGovernor.cs` / `GovernorLadder.cs` / `GovernorTuning.cs` / `SettingsBootApplier.cs` **零改动**
（`git diff --name-only` 对这四个文件为空）。`git diff --stat`：`FrameGovernorHost.cs +69/-10`、`RenderScaleRecoveryTests.cs +143`，
合计 **202 insertions, 10 deletions**（删除的 10 行全是被替换掉的旧注释与旧的 `Delete()` 分支，没有删任何断言）。

**T4 的构造与断言**（真 IO 失败，不 mock）：rung 1 正常落地（资产 0.95 + 盘上 `lastWritten=0.95`）→ 同时制造两个失败：
`.tmp` 路径被一个**目录**占住（`Save` 真失败）+ `FakeTarget.RefuseWrites = true`（回滚写真被拒）→ 断言
①资产仍是 0.95（回滚确实没落地）②**文件仍在** ③解析落盘文件得到 1 条、`lastWritten == 资产当前值`、`originalScale == 1.0`
④告警含 `rollback refused` + `journal entry kept`；然后**真的模拟下一次启动**（拆掉 `.tmp` 占位、恢复可写 → `RenderScaleRecovery.Recover`），
断言 `Restored`、资产回 1.0、日志被消费。

**T5 的构造与断言**：两个资产各一条目（rung 1 → `URP_High` 0.95；把 fake 的 active 切到 `URP_Medium`，rung 2 → 0.90，同一份文件里两条）
→ 占住 `.tmp` 后 rung 3（0.85）无法落盘 → `URP_Medium` 回滚**成功**（这一步是关键：它保证真的走到 `InvalidateJournalEntries` 的"剩余条目重写"分支）
→ 断言①文件仍在 ②**逐字节等于失败前的内容**（`fileUnchanged=True`，不只是"存在"）③`URP_High` 的 0.95 条目仍在
④下一次启动 `Partial`：`URP_High` 被治好（1.0），`URP_Medium` 的过期条目被 G3 拒绝、一次写入都没有。

### 2. T4 / T5 实际输出（本次六段 `TempLogs/runtests_editmode.xml` 的 `<output>` 原文）

```
T4: readStatus=Ok, entries=1, keptLastWritten=0.95, asset=0.95, journalExists=True, writes=[0.95], journalWarnings=1
T4 nextBoot: status=Restored, restored=1, asset=1.00, journalExists=False
T5: journalExists=True, fileUnchanged=True, assetHigh=0.95, assetMedium=1.00, writes=[0.95,0.9,1], journalWarnings=1
T5 nextBoot: status=Partial, restored=1, abandoned=1, assetHigh=1.00, assetMedium=1.00
```
- T4 的 `journalExists=True` + `keptLastWritten=0.95 == asset=0.95` 正是验收第 3 轮那份 `asset=0.95 journalExists=False nextBootStatus=NoJournal`
  的反面；`nextBoot=Restored` 证明**不是只断言"条目还在"**，而是真的能治。
- T5 的 `writes=[0.95,0.9,1]` 说明 `URP_Medium` 的回滚是真写回 1.0（不是只改账本），因此失效化分支确实被执行；
  `fileUnchanged=True` 是逐字节比对（`File.ReadAllText(...) == fileBefore`）。

### 3. 六段实测数字

`unset http_proxy https_proxy all_proxy` 后跑 `Tools/run_tests.ps1`（Bash 侧禁止转调 powershell，
按第 3 轮验收的同样办法在 **PowerShell 工具**里先 `Remove-Item Env:*proxy*` 再执行 .ps1，`Set-Location` 到仓库根）：

```
native   PASS   cases=190 asserts=710800 failed=0; ctest: 100% tests passed (1 entry: zrender_test) 00:12
editmode PASS   total=678 passed=678 failed=0 skipped=0 8.6595634s(xml)                            02:37
playmode PASS   total=6 passed=6 failed=0 skipped=0 6.1266899s(xml)                                00:51
arch     PASS   exit=0; report: TempLogs/arch_report.txt                                          00:25
config   PASS   exit=0; report: TempLogs/config_report.txt                                        00:30
naming   PASS   exit=0; report: TempLogs/name_report.txt                                          00:25
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)        EXITCODE=0
```
- native **190 / 710,800 未变**；editmode **678 = 676 + 2**（只加 T4/T5，未删未改名任何用例）。
- 顺带：`Assets/Plugins/ZRender/zrender.dll` 本次**没有**变成 `M`（`git status` 里不出现），即重建产物与入库版逐字节一致。

### 4. 既有断言：一条都没改

`RenderScaleRecoveryTests` 的 13 条旧用例（含 T1/T2/T3、被第 2 轮改写过的
`JournalWriteFailure_DisablesRenderScaleMeasure_ButOtherMeasuresLand`）**断言零改动**，`git diff` 里该文件只有 143 行新增。
新语义不会让任何旧断言变味，理由：
- 旧用例里走「回滚失败」路径的**一条都没有**（第 3 轮验收只能用手写探针 A3 才构造出来），所以本次只影响新分支；
- 走「回滚成功 → 失效化」的 T2 仍期望文件消失，而该路径仍是 `_journalEntries.Count == 0 → Delete()`，一字未动；
- 第 2 轮的两条计数断言（`JournalFailureWarnings == 1`）只统计含 `crash recovery unavailable` 的告警，
  新增的 `rollback refused ... journal entry kept` 与其不冲突（T4 实测 `journalWarnings=1`）。

### 5. 定向变异（证明 T4/T5 不是恒真）

把 `AbandonRenderScaleMeasure` 的 `if (rollbackRefused) {keep} else {Invalidate}` 整段换回**无条件 `InvalidateJournalEntries(urp)`**
（即第 3 轮验收判定的那版行为），只跑本类 15 条 —— `TempLogs/mutation_r903.xml`：
```
total 15 passed 14 failed 1        (Unity 进程 exit=1)
RED RollbackRefused_KeepsJournalEntry_AndNextBootHeals
  OUT: T4: readStatus=Missing, entries=-1, keptLastWritten=NaN, asset=0.95, journalExists=False, writes=[0.95], journalWarnings=1
  MSG: T4: a dirty asset without its note can never be healed — the entry must be kept   Expected: True  But was: False
```
**恰好 1 红**，且复现的就是验收原话（`asset=0.95 journalExists=False`）；T5 保持绿（它走的是回滚成功路径，与"保留条目"无关），
说明两条用例各打各的点。变异后已用备份原样还原：`md5 4207ef5e4b66f0272f154cc59041d782`（与变异前一致），
`grep -c "AUDIT MUTATION"` = 0，随后六段全绿。

### 6. 现场

```
 M UnityProject/Assets/ZFrame/Tests/Pipeline/RenderScaleRecoveryTests.cs
 M UnityProject/Assets/ZFrame/View/Pipeline/FrameGovernorHost.cs
?? Docs/Logs/TASK-A-R9-03.md
```
- `git status --short | grep -i "URP_.*asset"` → **none**；`git status --short -- UnityProject/ProjectSettings/` → 空；无 `Unity.exe` 残留。
- `git log -1` 仍 `58425dc`（**未 commit**）；`TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md` 未动。
- 两个 `.cs`：`bom=False`、`crlf=0`、LF。
- 备份/证据：`TempLogs/archive_pre_audit/FrameGovernorHost.cs.preMutationR903`、`TempLogs/mutation_r903.{xml,log}`、
  `TempLogs/r903_targeted.{xml,log}`（施工过程中的定向跑）、`TempLogs/runtests_editmode.xml`（六段内的 T4/T5 原文）。

### 7. 未验证项与诚实边界

1. **"保留条目"只在回滚被拒的窄路径生效**：一旦回滚成功，仍按原契约删条目 / 删文件。也就是说本任务没有把
   "所有失效化都改成保留"，只是把**会造成不可逆损失**的那一支改掉；残留的过期条目靠 G3 拒恢复（T5 的 `nextBoot=Partial` 已实证）。
2. **`InvalidateJournalEntries` 重写失败时不再有"清空"手段**：这是契约要的取舍。极端情况下（同一块坏盘、且其它资产也脏）文件会一直是旧的，
   代价是每次启动可能多一条 G3 误报（"被别的东西改过"），不会写错值。**该路径没有独立的红绿用例**——T5 只证明"文件没被删、另一个资产的条目还在"，
   没有断言"重写成功后文件变干净"（那条老路径行为未变，但也无用例）。
3. **多资产失效化的"重写成功"分支仍无用例**（第 2 轮未验证项 #4 的延续）：本轮 T5 只覆盖"重写失败"这一半。
4. **真 Player 构建 / 真磁盘写满**仍未实测（与前两轮相同）：T4/T5 用"目录占住路径"制造 `UnauthorizedAccessException`，同族但不等价于 `ERROR_DISK_FULL`。
5. **`Shutdown()` 会再试一次回滚**：本任务选择在回滚被拒时**保留** `_touchedRenderScales` 里的基线（原来是先 `Remove` 再回滚），
   所以关服时会再写一次；若那时可写，资产被治好、条目被摘、文件被清 —— 这条**有利**路径**没有用例**：
   T4 的 TearDown 里资产已被 `Recover` 治回 1.0，走的是"已在基线、直接摘条目"那一支，**没有真的触发这次重写**（如实标注）。
6. **观察（非本任务引入）**：本次 T3 打印 `rungs=9/9`（第 2 轮记录是 `10/10`），因为运行时 `PipelineManager.CurrentTier` 是 Low，
   Shadows 段少一档；断言写的是 `Governor.Level == Ladder.Count`，与档位数无关，故不影响结论。数字差异如实记录，供验收对照时不必怀疑回归。
7. **ZLog 通道可见性未动**（按简报），新增的两条告警/Info 在生产环境仍不可见，沿用全项目遗留。

---

## 验收清单（供验收代理）

- [ ] V1 遗留1：回滚失败时条目**保留**且 `lastWritten` == 资产当前脏值；有 T4 举证
- [ ] V2 遗留1：模拟下次启动**真能恢复**（不是只断言"条目还在"）
- [ ] V3 遗留2：`Save` 失败不再整份 Delete；T5 证明另一资产条目幸存
- [ ] V4 无回归：第 3 轮 T1/T2/T3 仍绿；写前落盘顺序/会话级停用/单条告警/G3/损坏隔离/Shutdown 未变
- [ ] V5 六段独立实跑：native 190/710,800 不变、editmode ≥ 678、playmode 6、三校验器 exit 0（抄实测值）
- [ ] V6 既有断言改动逐处有理由，未"为过而改"（用 `git diff` 逐处判断）
- [ ] V7 范围：四个禁改文件 `git diff` 为空；无 `URP_*.asset`；`.cs` 无 BOM；未 commit
- [ ] V8 定向变异：删掉"保留条目"那一行应恰好让 T4 红（证明用例非恒真）

## 验收结论（验收代理写）

**最终裁决：PASS（V1–V8 全绿）**。契约「宁留勿删」在代码里真的落地了，且不是"用例哄人"：
我另写 5 条独立探针（自有 target / 全新 journal + 全新 asset 实例模拟下次启动 / `File.ReadAllBytes` 逐字节比对），
另做 2 次与施工方向不同的定向变异（M1 打 T4、M2 打 T5），六段全量自己重跑一遍。未改动任何产品代码。

### 逐条判定

| 项 | 判定 | 证据（本轮自测，非抄施工报告） |
|---|---|---|
| V1 遗留1 条目保留 + `lastWritten`==脏值 | **PASS** | 探针 A1：`.tmp` 被目录占住 + target 拒绝写入 → `asset=0.95, journalExists=True`，落盘原文 `"originalScale": 1.0 / "lastWrittenScale": 0.95`，告警含 `rollback refused; journal entry kept`。施工 T4 本轮 editmode 亦 passed |
| V2 下次启动真能恢复 | **PASS** | A1 后半段用**全新** `RenderScaleRecoveryJournal` + **全新** `UniversalRenderPipelineAsset(0.95)` → `status=Restored, restored=1, reloaded=1.00, writes=[1], journalExists=False`。是真治，不是"条目还在" |
| V3 遗留2 `Save` 失败不整份删 | **PASS** | 探针 A2：两资产两条目（316 B）→ 失效化重写失败 → 文件仍在、`bytesBefore==bytesAfter` 且 `SequenceEqual=True`（我自己按字节比）、`TryRead=Ok` 且 `AuditB_High@0.95` 幸存；下次启动 `Partial`（High→1.00，Medium 的过期条目被 G3 拒、零写入） |
| V4 无回归（第 3 轮行为） | **PASS** | 本轮六段 editmode 678/678；`RenderScaleRecoveryTests` 15/15：T1 `writes=[] attempts=0`、T2 `staleEntry=False asset=1.00 writes=[0.95,1]`、T3 `rungs=10/10 warnings=1`、顺序 `journalAtFirstWrite=present`、G3 零写入、损坏隔离、`NormalShutdown → asset=1.00 journalExists=False` |
| V5 六段独立实跑 | **PASS** | 清代理后在 PowerShell 工具跑 `Tools/run_tests.ps1`：native `190 cases / 710,800 assertions / 0 failed` + `ctest 100%`、editmode `678/678`、playmode `6/6`、arch/config/naming `exit=0`（arch 违规数 0），`RESULT: ALL PASS`，`EXITCODE=0`。HEAD 版 13 个 `[Test]` → 现 15 个，新增名恰为 T4/T5（无改名/删除） |
| V6 未"为过而改" | **PASS** | `git diff --numstat`：测试文件 **143/0**（纯新增），host **59/10**；逐行看被删的 10 行 = 6 行旧注释 + 旧 `Delete()` 分支 + 被重构掉的 `Remove`/`if`/`InvalidateJournalEntries(urp)` 调用行，**没有任何断言被删改**。故"既有断言一条未改"属实 |
| V7 范围与现场 | **PASS** | 四禁改文件 `git diff` 为空（`SettingsBootApplier.cs` 实际在 `View/Settings/`，按真路径核对）；`git status` 无 `URP_*.asset`、`ProjectSettings/` 空；两 `.cs` `bom=False, crlf=0`；HEAD 仍 `58425dc`；无 `Unity.exe` 残留；六段后 `zrender.dll` 仍未 M |
| V8 定向变异（用例非恒真） | **PASS** | 自做 M1（回滚失败也改回无条件失效化）→ 15 条中**恰好 T4 红**（`readStatus=Missing, journalExists=False, asset=0.95`），T5 绿；自做 M2（`Save` 失败改回 `_recoveryJournal.Delete()`）→ **恰好 T5 红**（`journalExists=False`），T4 绿。两次均还原，md5 回到 `4207ef5e4b66f0272f154cc59041d782` |

### 施工变异证据核对（B）

`TempLogs/mutation_r903.xml` 存在且内容与声称一致：`testcasecount=15 total=15 passed=14 failed=1`，唯一红 = `RollbackRefused_KeepsJournalEntry_AndNextBootHeals`，
失败文案 `a dirty asset without its note can never be healed`，输出 `journalExists=False, asset=0.95`（复现第 3 轮原症状）。
`TempLogs/mutation_r903.log` 的 COMMAND LINE 确为 `-runTests -testFilter ZFrame.Pipeline.Tests.RenderScaleRecoveryTests`；
`TempLogs/archive_pre_audit/FrameGovernorHost.cs.preMutationR903` 与当前文件 md5 一致（`4207ef5e...`），`grep -c "AUDIT MUTATION"` = 0 —— 施工确实还原了。

### 关于 T5 的 "fileUnchanged"（措辞精度）

施工断言用的是 `File.ReadAllText(...) == fileBefore`（**字符串**相等，不是字节级）；因日志文件是 UTF-8 无 BOM 写出，二者在这里等价。
我另用 `File.ReadAllBytes` + `SequenceEqual` 复核为 `True`（316 B == 316 B），结论成立，仅"逐字节"一词比实现略强。

### 新发现（既有问题，不计入 V1–V8 判定）

1. **探针 A4（已验证，非推测）**：`PersistJournalEntry` 先落盘、后写资产。当**日志写成功而资产写被拒**时，盘上 `lastWritten` 会超前于资产真实值
   （实测 `noteLastWritten=0.80 / asset=0.95`），下次启动 G3 判为"被别的东西改过"→ `status=Abandoned, restored=0, writes=[]`，脏值 0.95 留存。
   这与 A-R9-02 的"写前落盘"顺序同源，**本任务未引入也未加剧**（HEAD 行为相同），但属同一族"脏资产救不回来"的残留，建议后续任务处理
   （例如资产写被拒后把 `lastWritten` 回退到资产当前值，或改"资产写成功后再补记"）。
2. **环境观察**：我以 `-runTests` 跑探针时，Unity 有一次在测试完成、xml 已写全后卡在关服约 6 分钟（第二次同命令正常自行退出），已 `taskkill`。
   `run_tests.ps1` 自带 300s grace + taskkill，六段不受影响（本轮六段 Unity 均正常退出）。
3. 施工 `§7.6` 的 `rungs=9/9` 差异已核：我本轮实测 `rungs=10/10`，断言是 `Level == Ladder.Count`，与环境档位有关，不构成回归。

### 现场还原

- 我构造的临时探针 `_AuditR903_Probe.cs`/`.meta` 已 `mv` 到 `TempLogs/archive_pre_audit/`，仓库内 `Assets/ZFrame/Tests/Pipeline/` 无残留（本轮 678 条中不含探针）。
- 变异备份 `TempLogs/audit_r903_pre_mutation_backup.cs`；证据 `TempLogs/audit_probe2_r903.{xml,log}`、`audit_mutation_m1.*`、`audit_mutation_m2.*`、`audit_full_run.out`。
- `git status --short` 仅 3 项：两个被修改文件 + 本任务日志；**未 commit**，HEAD `58425dc`；产品代码在验收结束时与施工交付逐字节一致（md5 `4207ef5e...`）。
