# TASK-A-R9-02 —— DRS 残留自愈：renderScale 崩溃后不留脏值

- **轨道**：A / R9（承接 A-R9-01 验收风险 #4）
- **状态**：doing
- **派发**：主控，2026-10-28

## 任务简报（主控写）

### 1. 问题（这是 A-R9-01 放大的既有风险）

`FrameGovernorHost.ApplyRenderScale()`（`View/Pipeline/FrameGovernorHost.cs:357-374`）在治理降级时**直接写**
`UniversalRenderPipelineAsset.renderScale`，原值只记在**内存字典** `_touchedRenderScales` 里，靠 `Shutdown()`/`OnDestroy()` 恢复。

后果链（必须一次斩断）：

1. 治理把 `renderScale` 从 1.0 降到 0.75，此时 URP 资产在编辑器里被改脏（`m_RenderScale` 落盘）。
2. 进程崩溃 / 被强杀 / 编辑器炸掉 → `Shutdown()` 没机会跑 → **内存里的原值随进程消失，盘上留下 0.75**。
3. 下次启动：`Initialize` 里 `GetCurrentRenderScale()` 读到的是 **0.75**，于是把 0.75 当"用户原值"记下来 → 阶梯从 0.75 起算。
4. **复利**：每崩一次基线再降一档，最终画面糊到无法使用，且玩家没有任何办法知道原因。

A-R9-01 让 DRS **开机自动开启**（`SettingsBootApplier`），把这条从"手动开才可能踩"放大成"存过档就每次开机都可能踩"。

### 2. 目标（三条，缺一不算完成）

- **G1 崩溃自愈**：下次启动时，若检测到上次未正常收尾，**自动把 URP 资产的 renderScale 恢复到崩溃前的用户原值**，并留下可诊断日志。
- **G2 正常路径不退化**：正常退出仍走既有 `Shutdown()` 恢复，且**不留下任何恢复日志文件**。
- **G3 不覆盖第三方修改**：若盘上当前值与"上次被治理写入的值"不一致，说明有别的途径改过它（玩家手改资产 / 资源重导入），**此时不得恢复**，只告警并清理日志，避免把别人的正当修改改回去。

### 3. 设计要求（主控定契约，实现细节你定但必须满足）

1. **写前落盘（顺序不可颠倒）**：**首次**修改某个 URP 资产的 `renderScale` **之前**，必须已把恢复日志原子写盘。
   先写后改——顺序反了就等于没有日志。
2. **日志内容**：按 **QualitySettings 档位索引 + 资产名 + 原值 + 最后一次写入值** 记录（本工程 URP 资产按 Quality 三档绑定，用
   `QualitySettings.GetRenderPipelineAssetAt(i)` 可枚举，**不要**依赖 `AssetDatabase`，保持 Player 侧可用）。
3. **日志位置**：`Application.persistentDataPath` 下固定文件名（如 `governor_recovery.json`），**原子写**（临时文件 + `File.Replace`/`Move`，参考 `Data/Save/SaveSystem.cs` 的既有做法）。
4. **开机自愈的执行序**：必须**早于** `SettingsBootApplier` 应用设置（否则会先按脏基线算阶梯再恢复，白算一轮）。
   用 `RuntimeInitializeOnLoadMethod` 时明确排序理由并写进注释。
5. **恢复的幂等与清理**：恢复成功/放弃（G3 情形）/日志损坏，三种路径都必须**删除日志**，绝不允许下次再走一遍。
6. **日志损坏要能扛**：JSON 解析失败不得抛穿启动流程，判为"无法判定"→ 告警 + 隔离（重命名 `.corrupt_`）+ 继续启动。
7. **Player 侧写资产会失败**：`renderScale` 赋值若在 Player 里不生效/抛错，`try/catch` 一次告警即可，**不得每帧刷屏**。

### 4. 硬约束

- **不得改帧率治理的核心决策**：迟滞状态机、措施阶梯（`GovernorLadder`）、降级策略一律不动。本任务只加"持久化 + 自愈"这一层。
- **测试不得污染真实 URP 资产**：EditMode 用例里**禁止**直接改真实 `URP_*.asset`。必须通过可注入的资产访问器（接口/fake）测，
  或对 `ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>()` 的临时实例测。**验收会检查 `git status` 里 `URP_*.asset` 不得出现修改。**
- 不引入第三方库（JSON 用工程已有的 Newtonsoft）。
- 不装软件、不联网、**不 git commit**。
- 触碰文件：`View/Pipeline/**`（Host 与其新增恢复组件）、`View/Settings/SettingsBootApplier.cs`（仅排序相关）、`Tests/Pipeline/**`、本日志。

### 5. 自测清单（施工必做，逐条贴证据）

1. **崩溃模拟（核心）**：写一份恢复日志模拟"上次崩溃"（原值 1.0 / 最后写入 0.75）→ 让资产当前值为 0.75 → 执行开机自愈 →
   断言资产回到 **1.0**、日志**已删除**、有告警日志。
2. **正常路径**：开启治理 → 触发一次降级 → 调 `Shutdown()` → 断言资产回原值、**日志文件不存在**。
3. **不覆盖第三方修改（G3）**：日志记"最后写入 0.75"，但当前值是 0.9（别人改过）→ 断言**不做任何写入**、日志被清理、打明确告警。
4. **顺序**：用 fake 记录调用顺序，断言"日志已存在"早于"第一次写 renderScale"。
5. **日志损坏**：写一段非法 JSON → 启动流程不抛异常、`.corrupt_` 隔离产物存在、后续可继续。
6. **复利斩断**：连续两轮"崩溃→自愈"，断言基线始终是 1.0，**不出现 0.75→0.5625 这种复利**。
7. **六段全量**：全绿 exit 0；native 仍 190/710,800；editmode = 663 + 新增数（写明新增几条）。
8. **资产未被污染**：`git status --short` 中**不得出现 `URP_*.asset`**；改动清单完整。
9. **诚实边界**：写明本机制在 Player（资产只读）与 Editor 的行为差异、以及"日志写盘失败"时的降级行为。

### 6. 交付物

- 代码 + 测试 + 本日志「施工报告」（含 9 条证据原文、"写前落盘 → 崩溃 → 自愈"的完整时序说明、诚实边界）。
- 不动 HANDOFF/TASKS/WORKLOG（主控闭环时刷新）。

---

## 施工报告（施工代理写）

**结论**：DRS 的 `renderScale` 现在有崩溃自愈。首次写资产**之前**先把「原值 / 最后写入值 / 档位索引 / 资产名」原子落盘
（`governor_recovery.json`）；正常 `Shutdown()`/`SetEnabled(false)` 恢复后删日志；下次启动若发现日志残留，
只在该资产当前值**仍等于「最后写入值」**时恢复原值，否则只告警不动手（G3）。三条路径（成功 / 放弃 / 日志损坏）都消耗日志。
六段全量全绿 exit 0；editmode **673 = 663 + 10**（新增 10 条）；native 仍 190/710,800；`git status` 里**没有 `URP_*.asset`**。

### 1. 机制与时序（这是本任务的全部要害）

写路径（`FrameGovernorHost.ApplyRenderScale`，`View/Pipeline/FrameGovernorHost.cs`）：

```
GovernorLadder 决定 renderScale = 0.75
 1. _touchedRenderScales[urp] = urp.renderScale        // 内存：原值 1.0
 2. PersistJournalEntry(urp, 1.0, 0.75)                // 盘：governor_recovery.json（.tmp → File.Replace 原子）
 3. _renderScaleTarget.WriteScale(urp, 0.75)           // 最后才动资产
    ↑ 顺序反了就等于没有日志：盘上留下 0.75，而"原值 1.0"只活在进程里
进程被强杀（第 3 步之后、Shutdown 之前）
 盘上状态：资产 m_RenderScale = 0.75（编辑器退出时会把资产写盘），日志 = {original 1.0, lastWritten 0.75}
下次启动：
 4. RenderScaleRecovery.AutoBoot()            [RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]
      → 读日志；按 (档位索引, 资产名) 找到资产（QualitySettings.GetRenderPipelineAssetAt，无 AssetDatabase）
      → 当前 0.75 == lastWritten 0.75 → WriteScale(资产, 1.0) 并 Warn 一条
      → 删除日志（绝不重复走）
 5. SettingsBootApplier.AutoBoot()            [AfterSceneLoad] → SettingsModel.Load() → SetEnabled(true)
      → EnableGovernor 捕获的基线 = 1.0，不是 0.75 ⇒ 复利被斩断
```

**排序理由（设计点 4）**：Unity 保证所有 `BeforeSceneLoad` 回调都先于第一个 `AfterSceneLoad` 回调；
`SettingsBootApplier`（唯一在启动时调 `SettingsModel.Load` 的地方）是 `AfterSceneLoad`，所以自愈必然先做。
放在 `AfterSceneLoad` 之后修是没用的——阶梯已经从脏基线建好了。与 `FrameGovernorHost` 自己的 `BeforeSceneLoad`
自举谁先谁后无所谓：那条路径只建一个 disabled 的占位 core，不写资产，真正的基线在 `SetEnabled` 里才捕获。

**可注入的两个缝（硬约束：测试不许碰真实 URP 资产）**：
`FrameGovernorHost.Initialize(telemetry, IRenderScaleTarget, RenderScaleRecoveryJournal)`。
- `IRenderScaleTarget`：`ActiveAsset / QualityLevelCount / GetAssetAt / IndexOf / WriteScale`；生产实现
  `QualitySettingsRenderScaleTarget`（`GraphicsSettings.currentRenderPipeline` + `QualitySettings.GetRenderPipelineAssetAt`），
  测试用 `ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>()` 的一次性实例。
- `RenderScaleRecoveryJournal`：根目录可注入，测试一律用临时目录（连既有两个 fixture 也改成临时目录了）。

**日志格式**（`{persistentDataPath}/governor_recovery.json`，UTF-8 无 BOM，`.tmp` 落盘 + `File.Replace`）：
```json
{
  "version": 1,
  "entries": [
    { "qualityLevel": 2, "assetName": "URP_High", "originalScale": 1.0, "lastWrittenScale": 0.75 }
  ]
}
```

### 2. 九条自测证据（简报 9 条 + 第 10 条侧重边界的用例）

#### 证据 1 —— 崩溃模拟：1.0 复原、日志删除、有告警

用例 `CrashJournal_RestoresOriginal_DeletesJournal_AndWarns`（本次全量运行 `TempLogs/runtests_editmode.xml` 的 `<output>` 原文）：
```
crash: journal(original=1.00, lastWritten=0.75) + asset=0.75 -> status=Restored, restored=1, asset=1.00, journalExists=False
```
断言：`asset.renderScale == 1.0`、`report.Status == Restored`、`File.Exists(journal.FilePath) == false`、
并且告警列表里存在同时含 `did not shut down cleanly` / `0.75` / `1.00` 的一条（断言消息即"the heal must be diagnosable in the log"）。

#### 证据 2 —— 正常路径：回原值、无日志残留

用例 `NormalShutdown_RestoresAsset_AndLeavesNoJournal`（真宿主 + 假资产 + 临时日志目录）：
```
normal path: drove asset to 0.95 (journal existed=True), Shutdown() -> asset=1.00, journalExists=False
```
前半段还断言了"治理中日志**存在**"（证明不是靠"根本不写日志"蒙过去的 G2），后半段断言 `Shutdown()` 后文件消失。

#### 证据 3 —— G3：不覆盖第三方修改、清日志、明确告警

用例 `Recovery_WhenCurrentDiffersFromLastWritten_DoesNotWriteAnything`（日志 lastWritten=0.75，盘上 0.90）：
```
G3: journal lastWritten=0.75, disk=0.90 -> status=Abandoned, writes=[], asset=0.90, journalExists=False
```
`writes=[]` 是关键：`IRenderScaleTarget.WriteScale` **一次都没被调用**（不是"写了同样的值"）；
`Has.No.Member(1.0f)` 额外锁死"1.0 绝不能盖掉 0.9"；告警含 `NOT restoring` 与 `0.90`。

#### 证据 4 —— 顺序：日志已存在早于第一次写 renderScale

用例 `Journal_IsAlreadyOnDisk_BeforeTheFirstRenderScaleWrite`：fake 的 `WriteScale` 在**被调用的那一刻**读盘，
把日志文件内容存进 `JournalAtFirstWrite`：
```
order: writes=[0.95], journalAtFirstWrite=present
```
断言还逐字校验当时的日志内容：`"originalScale": 1.0`、`"lastWrittenScale": 0.95`、`"assetName": "URP_High"`
（即"盘上那条记录的就是即将写下去的那个值"，不是事后补的）。

**可证伪性（变异对照，本次实跑）**：把 `PersistJournalEntry` 挪到 `TryWriteScale` 之后、并去掉 G3 的等值判断，
editmode 跑出 **恰好 2 红**，就是这两条：
```
Journal_IsAlreadyOnDisk_BeforeTheFirstRenderScaleWrite -> Failed
  msg: the recovery journal must already exist when the FIRST renderScale write happens
  OUT: order: writes=[0.95], journalAtFirstWrite=MISSING
Recovery_WhenCurrentDiffersFromLastWritten_DoesNotWriteAnything -> Failed
  msg: G3: the value on disk must be left alone
  OUT: G3: journal lastWritten=0.75, disk=0.90 -> status=Restored, writes=[1], asset=1.00, journalExists=False
（对照记录留在 TempLogs/mutation_editmode.xml；改回后全绿）
```

#### 证据 5 —— 日志损坏：不抛、隔离、可继续

用例 `CorruptJournal_DoesNotThrow_IsQuarantined_BootContinues`（写 `{ this is not a journal `）：
```
corrupt: status=Corrupt, quarantined='governor_recovery.json.corrupt_20261028063636', writes=[], fixedPathExists=False
```
断言：`Assert.DoesNotThrow`、状态 `Corrupt`、`writes=[]`（"无法判定"时一个值都不猜）、固定路径已空、
`.corrupt_*` 恰有 1 个；随后再跑一次 `Recover` 得到 `NoJournal`（证明不会陷进死循环）。

#### 证据 6 —— 复利斩断：连续两轮崩溃，基线恒为 1.0

用例 `TwoCrashCycles_BaselineStaysAtOne_NoCompoundingShrink`：每轮"写日志 → 写 0.75 → 不回滚（模拟崩溃）→ 开机自愈"：
```
compounding: adopted baselines=[1,1], writes=[0.75,1,0.75,1]
```
`adopted baselines=[1,1]` = 两轮会话捕获到的基线都是 1.0；`writes` 里只有 `0.75/1.0` 的来回，**没有 0.5625**。
同一用例还断言自愈之后真宿主 `_host.Governor.Ladder.OriginalRenderScale == 1.0`，且第一档写 `0.95`
（若基线还是 0.75，第一档会是 0.7125）——即"值真的抵达 governor 内部阶梯"，不是只看了文件。

#### 证据 7 —— 六段全量全绿（exit 0），native 不变，editmode = 663 + 10

`TestReport.txt`（`Generated: 2026-10-28 14:39:53  (wall 0:04:33)`）：
```
native   PASS   cases=190 asserts=710800 failed=0; ctest: 100% tests passed (1 entry: zrender_test) 00:11
editmode PASS   total=673 passed=673 failed=0 skipped=0 7.9433171s(xml)                            01:10
playmode PASS   total=6 passed=6 failed=0 skipped=0 7.6058163s(xml)                               01:20
arch     PASS   exit=0; report: TempLogs/arch_report.txt                                          00:40
config   PASS   exit=0; report: TempLogs/config_report.txt                                        00:35
naming   PASS   exit=0; report: TempLogs/name_report.txt                                          00:35
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)
```
- native 仍 **190 用例 / 710,800 断言**（未动 `Native/**`，`git status Native/` 为空）。
- editmode **673 = 663 + 10**：新增 10 条全部在 `Tests/Pipeline/RenderScaleRecoveryTests.cs`，xml 里
  `SUITE RenderScaleRecoveryTests total=10 passed=10 failed=0`。10 条用例名：
  `CrashJournal_RestoresOriginal_DeletesJournal_AndWarns`、
  `NormalShutdown_RestoresAsset_AndLeavesNoJournal`、
  `Recovery_WhenCurrentDiffersFromLastWritten_DoesNotWriteAnything`、
  `Journal_IsAlreadyOnDisk_BeforeTheFirstRenderScaleWrite`、
  `CorruptJournal_DoesNotThrow_IsQuarantined_BootContinues`、
  `TwoCrashCycles_BaselineStaysAtOne_NoCompoundingShrink`、
  `JournalWriteFailure_KeepsGoverning_WithExactlyOneWarning`、
  `AssetWriteRefusal_WarnsOnce_AndGovernanceKeepsRunning`、
  `JournalEntryForVanishedAsset_IsAbandoned_NotThrown`、
  `JournalEntry_StillResolves_WhenQualityLevelOrderChanged`。
- arch 原文：`asmdef 扫描数: 24（期望矩阵条目: 11，Tests 程序集豁免）… 违规数: 0 结果: PASS`
  —— **没有新建程序集**，两个新 `.cs` 都落在既有 `ZFrame.View` 内。
- arch 原文：`asmdef 扫描数: 24（期望矩阵条目: 11，Tests 程序集豁免）… 违规数: 0 结果: PASS`
  —— **没有新建程序集**，两个新 `.cs` 都落在既有 `ZFrame.View` 内。

#### 证据 8 —— 资产未被污染 + 改动清单

`git status --short`（`git status --short | grep -i URP_` → `none`）：
```
 M UnityProject/Assets/Plugins/ZRender/zrender.dll
 M UnityProject/Assets/ZFrame/Tests/Pipeline/FrameGovernorHostTests.cs
 M UnityProject/Assets/ZFrame/Tests/Pipeline/FrameGovernorSettingsTests.cs
 M UnityProject/Assets/ZFrame/View/Pipeline/FrameGovernorHost.cs
?? Docs/Logs/TASK-A-R9-02.md
?? UnityProject/Assets/ZFrame/Tests/Pipeline/RenderScaleRecoveryTests.cs
?? UnityProject/Assets/ZFrame/Tests/Pipeline/RenderScaleRecoveryTests.cs.meta
?? UnityProject/Assets/ZFrame/View/Pipeline/RenderScaleRecovery.cs
?? UnityProject/Assets/ZFrame/View/Pipeline/RenderScaleRecovery.cs.meta
?? UnityProject/Assets/ZFrame/View/Pipeline/RenderScaleRecoveryJournal.cs
?? UnityProject/Assets/ZFrame/View/Pipeline/RenderScaleRecoveryJournal.cs.meta
```
`git diff --stat`：
```
 UnityProject/Assets/Plugins/ZRender/zrender.dll    | Bin 20480 -> 20480 bytes
 .../Tests/Pipeline/FrameGovernorHostTests.cs       |  17 ++-
 .../Tests/Pipeline/FrameGovernorSettingsTests.cs   |   4 +-
 .../ZFrame/View/Pipeline/FrameGovernorHost.cs      | 162 +++++++++++++++++++--
 4 files changed, 165 insertions(+), 18 deletions(-)
```
逐条说明：
- 新增 `View/Pipeline/RenderScaleRecoveryJournal.cs`（日志 IO）、`View/Pipeline/RenderScaleRecovery.cs`
  （`IRenderScaleTarget` + 生产实现 + `Recover` + `BeforeSceneLoad` 钩子）、`Tests/Pipeline/RenderScaleRecoveryTests.cs`（10 条）。
- `FrameGovernorHost.cs`：只加"持久化 + 自愈"一层（缝、写前落盘、恢复后删日志、写失败一次告警）；
  **`FrameGovernor.cs` / `GovernorLadder.cs` / `GovernorTuning.cs` 零改动**（`git diff --stat` 对它们为空），
  迟滞状态机与阶梯定义一行没动。
- 两个既有测试 fixture 只加"把日志目录指向临时目录"的一行（`Initialize` 多两个可选参），**没有增删/弱化任何断言**。
- 两个新 `.cs` 首字节 `75 73 69`（无 BOM）、行尾与同目录既有文件一致（LF）。
  三个新 `.meta` 由 Unity 自动生成，未手改。
- `zrender.dll` 的变化**不是本任务的改动**：`Tools/run_tests.ps1` 的 native 段按既有流程"构建 + 部署"重写，
  本次 md5 `c20ef91cd5cb598bc4ead49cf1d53bfc` 与 A-R9-01 验收记录的 md5 完全一致（同尺寸、同内容），
  且 `git status Native/` 为空。未做任何回滚（不 git commit / 不用 destructive 命令）。
- 跑完无 Unity 残留进程；真实存档目录 `%USERPROFILE%/AppData/LocalLow/ZFrame/ZFrame` 下仍只有 `saves/`，
  **没有 `governor_recovery.json`**（PlayMode 真实走了一次开机自愈钩子，干净退出后不留日志）。

#### 证据 9 —— 诚实边界（详见 §3）

- Player：URP 资产在包内只读，`renderScale` 赋值通常不落盘（或直接抛），所以自愈在 Player 里多半是空转；
  一旦赋值抛异常，`try/catch` 只告警一次、治理继续走（`AssetWriteRefusal_WarnsOnce_AndGovernanceKeepsRunning`：
  `write refusal: attempts=3, asset=1.00, writeWarnings=1` —— 三次尝试、零落地、一条告警）。
- Editor：资产会被写脏并可能随编辑器退出/保存落盘 —— 这正是本机制的目标场景。
- 日志写盘失败：不阻塞治理（`asset=0.85` 仍降了），但本会话**失去崩溃安全**，且只告警一次（`journalWarnings=1`）。

**补充实测（第 10 条用例与两条解析用例的原样输出）**：
```
write refusal: attempts=3, asset=1.00, writeWarnings=1
vanished: status=Abandoned, writes=[], journalExists=False
level reorder: journal level=0, asset now at level=1 -> status=Restored, asset=1.00
```

### 3. 诚实边界（必须让验收知道的三件事）

| 场景 | 行为 |
|---|---|
| **Editor / 开发机**（资产可写、编辑器会把资产写盘） | 完整生效：写前落盘 → 崩溃 → 下次启动回原值。这是本机制的目标场景，证据 1/2/3/4/6 全部在此路径上。 |
| **Player 构建**（URP 资产在包内只读） | `renderScale` 只改运行时内存，通常不落盘 ⇒ 每次启动资产本来就是"原值"，自愈基本是空转（日志解出条目 → 当前值等于 lastWritten 才会写，写不进就一次告警后清日志）。也就是说：**Player 里崩溃不会留下脏值，但也意味着 Player 里这条自愈没有可修的东西**；它不构成负担，也不是"在 Player 里救回了画质"的证据。写被拒时的行为有真用例守：`AssetWriteRefusal_WarnsOnce_AndGovernanceKeepsRunning` → `attempts=3, asset=1.00, writeWarnings=1`。 |
| **日志写盘失败**（磁盘满 / 目录不可写 / 权限） | 降级而非阻断：`Journal.Save` 返回 false，宿主**照常**写 renderScale（治理不因日志失败而停），只打**一条**告警 `crash recovery is DISABLED for this session`；本会话失去崩溃安全（原值只回到内存里），下次正常退出仍会恢复。实测 `asset=0.85, journalWarnings=1`（连续三档 0.95→0.90→0.85，每次 Save 都失败，仍只有一条告警）。 |
| **日志损坏** | 判为"无法判定"：`.corrupt_<stamp>` 隔离 + 一条告警 + 继续启动，**不猜任何值**（`writes=[]`），且固定路径清空 ⇒ 下次是干净的 `NoJournal`。 |
| **资产已不存在 / 档位重排** | 按 (档位索引 → 资产名) 解析：索引对上但名字不符则退化为全档位按名字扫；仍找不到就"放弃 + 清日志 + 告警"（`JournalEntryForVanishedAsset_IsAbandoned_NotThrown`、`JournalEntry_StillResolves_WhenQualityLevelOrderChanged`）。 |
| **钩子自身的被调用**（`RuntimeInitializeOnLoadMethod`） | EditMode 里引擎不会调用它，10 条用例证明的是 `Recover()` 函数体；钩子真的被调用只能靠 PlayMode 真实启动（本次 playmode 6/6 绿、真实存档目录事后无日志残留）与静态检查（特性在、唯一调用方、`BeforeSceneLoad` < `AfterSceneLoad`）。这条缝与 A-R9-01 的开机应用点同类，属已知边界。 |

**没做的事（明确声明）**：
- 不改 `Application.targetFrameRate`、不改阶梯档位、不改迟滞参数、不改 `PipelineManager` 的档位切换语义。
- 不做"多进程/多实例共享日志"的并发保护（同一台机器只有一个游戏实例写 `persistentDataPath`；`File.Replace` 已保证
  单写者的原子性；写入用 `FileShare.None` 打开 `.tmp`）。
- 不在日志里记录 asset 的 GUID/路径（Player 侧拿不到，且按档位+名字足够；改名换资产的极端情形会退化为"放弃+告警"）。


## 验收清单（主控写）

> 独立取证，不复用施工数字。9 项全 PASS 才通过。
> **本任务的失败模式有两种：①"自愈"只在单测里成立、真启动不走；②测试为了好写而污染真实 URP 资产（留下脏值）。两项都要专门查。**

1. **V1 六段全量**：`Tools/run_tests.ps1` → 六段全绿 exit 0；native **仍 190/710,800**（不该被影响）；editmode **673 = 663 + 10**（自己数 diff 新增用例核对）；playmode 6；三校验器 exit 0。
2. **V2 崩溃自愈（核心，自己构造）**：手工造恢复日志（原值 1.0 / 最后写入 0.75）并把某 URP 资产当前值置为 0.75 →
   触发自愈路径 → 断言：资产回到 **1.0**、日志**已删除**、有告警。"构造"不许只用施工写的用例，要自己写一份日志文件验证。
3. **V3 写前落盘顺序**：读实现确认"日志写盘"确实在"首次写 renderScale"之前（不是 catch 里补写、不是写完资产再补日志）。
   若实现用 `try/finally` 之类掩盖顺序，按 **FAIL** 处理。
4. **V4 不覆盖第三方修改（G3）**：日志记"最后写入 0.75"、资产当前 0.9 → 断言**不发生写入**、日志被清理、告警文案能让人看懂"为什么没恢复"。
5. **V5 正常路径零残留**：开启治理 → 降级 → `Shutdown()` → 资产回原值、`persistentDataPath` 下**恢复日志不存在**。
6. **V6 日志损坏可扛**：写非法 JSON → 启动流程**不抛**、`.corrupt_` 隔离产物存在、后续启动正常。
7. **V7 复利斩断**：连续两轮"崩溃 → 自愈"，基线恒为 1.0，**不允许出现 0.75 → 0.5625**。
8. **V8 测试未污染资产**：`git status --short` 中**不得出现任何 `URP_*.asset`**（出现即 FAIL，无论理由）；同时确认 `ProjectSettings/` 无意外改动。
9. **V9 治理核心未动 + 范围规范**：`FrameGovernor.cs` / `GovernorLadder`（迟滞与阶梯）**零改动**；
   改动落在 `View/Pipeline/**`、`SettingsBootApplier.cs`（仅排序）、`Tests/Pipeline/**`、本日志；`.cs` 无 BOM；无 Unity 残留；未 commit。

**须在结论中回答**：①自愈的执行序是否真的早于 `SettingsBootApplier`（给出你怎么确认的：特性顺序/调用点/日志）；
②这套机制在 **Player（资产只读）** 下是"有效"还是"空转"——施工的诚实边界声明是否与代码一致；
③是否存在"日志写盘失败时仍然去改资产"的路径（若存在按 **FAIL**，因为这会让崩溃后无法自愈）。

## 验收结论（验收代理写）

**验收代理**：与施工不同的代理；以下数字均为本次独立复跑/独立构造成果，**未复用施工报告数字**。
**耗时与命令**：`powershell -NoProfile -ExecutionPolicy Bypass -File Tools/run_tests.ps1`（经 PowerShell 工具运行，跑前清空 6 个大小写代理变量）；自有用例与真实 PlayMode 启动直接用
`"C:/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe" -batchmode -projectPath UnityProject -runTests -testPlatform <Edit|Play>Mode -testResults ... -logFile ...`（不经 `-quit`）。

### 逐条结论

| 项 | 结论 | 一行证据 |
|---|---|---|
| V1 六段全量 | **PASS** | `RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)`、`EXIT=0`；native `190 cases / 710800 assertions / 0 failed`；editmode `total=673 passed=673 failed=0`；playmode `total=6 passed=6 failed=0`；arch/config/naming `PASS: exit 0`。增量自数：HEAD 的 `[Test]/[UnityTest]/[TestCase]` 计数 **506** → 工作树 **516**（+10，无删减），新文件 `RenderScaleRecoveryTests` 在 xml 里恰好 10 条，`ZFrame.Pipeline.Tests total=76` → **673 = 663 + 10** |
| V2 崩溃自愈（自造日志） | **PASS** | 自己 `File.WriteAllText` 手写 `governor_recovery.json`（original 1.0 / lastWritten 0.75，**不经生产 Save**）+ 一次性 `CreateInstance<UniversalRenderPipelineAsset>` 置 0.75 → `RenderScaleRecovery.Recover(root, fake)`：`AUDIT-V2: status=Restored restored=1 asset=1.00 journalExists=False warnings=[... did not shut down cleanly — renderScale of 'URP_AuditHigh' restored 0.75 -> 1.00 (quality level 0).]` |
| V3 写前落盘顺序 | **PASS** | 读代码：`FrameGovernorHost.ApplyRenderScale`（`FrameGovernorHost.cs:408-431`）先 `PersistJournalEntry(...)`（→`RenderScaleRecoveryJournal.Save`（`RenderScaleRecoveryJournal.cs:108-144`）内 `.tmp`+`Flush(flushToDisk:true)`+`File.Replace`）再 `TryWriteScale(...)`，两语句顺序直读，**没有任何 try/finally 或 catch 补写**。独立探针（fake 在**被调用的那一刻**读盘）：`AUDIT-V3: journalAtFirstWrite={ "version": 1, "entries": [ { "qualityLevel": 0, "assetName": "URP_AuditHigh", "originalScale": 1.0, "lastWrittenScale": 0.95 }` |
| V4 G3 不覆盖第三方 | **PASS** | 手写日志 lastWritten=0.75 + 资产 0.90 → `AUDIT-V4: status=Abandoned writes=[] asset=0.90 journalExists=False`，告警原文可读：`is 0.90 but the journal says the governor left 0.75 — value was changed by something else, NOT restoring 1.00.` |
| V5 正常路径零残留 | **PASS** | `AUDIT-V5: dirty=0.95 journalDuring=True afterShutdown=1.00 journalExists=False`（先证明治理期间日志确实存在，再证明 `Shutdown()` 后文件消失；真实 `persistentDataPath` 事后只有 `saves/`） |
| V6 日志损坏 | **PASS** | 手写 `]] not json [[` → `AUDIT-V6: status=Corrupt quarantined=1 writes=[] fixedExists=False`，`Assert.DoesNotThrow` 通过，紧随的第二次 `Recover` 返回 `NoJournal` |
| V7 复利斩断 | **PASS** | 两轮"手写日志→写 0.75→不自愈（模拟崩溃）→开机自愈"：`AUDIT-V7: baselines=[1,1] writes=[0.75,1,0.75,1] asset=1.00`，`Has.No.Member(0.5625)` 成立 |
| V8 资产未污染 | **PASS** | 六段跑完 + 我自己的 PlayMode 真实启动之后：`git status --short \| grep -i URP_` → `none`；`git status --short -- UnityProject/ProjectSettings/` 为空；三份 `URP_*.asset` 的 `m_RenderScale: 1` 未变 |
| V9 核心未动 + 范围 | **PASS** | `git diff --stat -- FrameGovernor.cs GovernorLadder.cs GovernorTuning.cs SettingsBootApplier.cs` **为空**；改动只有 `View/Pipeline/FrameGovernorHost.cs`(+162/-18)、`View/Pipeline/RenderScaleRecovery{,Journal}.cs`、`Tests/Pipeline/*`、本日志；三份新 `.cs` 首字节 `75 73 69`（无 BOM）、`crlf=0 / lf=全部`；`git log -1` 仍为 `eaf32be`（**未 commit**）；`tasklist` 无 `Unity.exe` 残留 |
| 必答③（日志写盘失败仍改资产） | **FAIL** | 见下「三、必答问题③」——代码路径 + 独立实测同时成立 |

### 一、必答问题①：自愈执行序是否真的早于 `SettingsBootApplier`

**是（已验证到结构性 + 真实启动两半，日志时间轴那一路不可得，如实标注）。**

1. 特性序：`RenderScaleRecovery.AutoBoot` = `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`（`RenderScaleRecovery.cs:178`）；
   `SettingsBootApplier.AutoBoot` = `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]`（`SettingsBootApplier.cs:32`，本次**零改动**，`git diff` 为空）。
   Unity 语义保证所有 `BeforeSceneLoad` 回调先于第一个 `AfterSceneLoad` 回调。
2. 调用点唯一性：全仓非测试代码里 `SettingsModel.Load()` 只有两处——`SettingsBootApplier.ApplyFromDisk()`（启动）与
   `SettingsEntry.OpenSettings()`（用户打开面板，远晚于启动）；`ApplyFromDisk` 的唯一调用者就是那个 `AfterSceneLoad` 钩子。
3. 真实启动实测：我往 `%USERPROFILE%/AppData/LocalLow/ZFrame/ZFrame/governor_recovery.json` 手写一条
   `{level 1, "URP_Medium", original 1.0, lastWritten 1.0}`，再跑**真实 PlayMode 启动**（6/6 绿）——启动后该文件**已被消费删除**、
   无 `.corrupt_` 残留、真实 URP 资产按生产 `QualitySettingsRenderScaleTarget` 被解析到。⇒ 自愈钩子在**真实启动**（不只是单测函数体）确实执行，
   施工"只在单测里成立"的第一种失败模式**不成立**。
4. **局限（不作 PASS 依据）**：我本想用日志时间轴直接比对两条钩子的先后，但实测该路径拿不到——
   `TempLogs/audit_r9_02_play.log`（1478 行）里 **一条 governor/settings 行都没有**，原因见「四、观察 2」（ZLog 通道未启用）。
   故"顺序"结论来自 load type 语义 + 唯一调用点，不是日志时间轴，不写成"日志已验证"。
5. 另核：`FrameGovernorHost` 自己的 `BeforeSceneLoad` 只 `new FrameGovernor(GovernorLadder.Create(tier, GetCurrentRenderScale()))` 建 **disabled 占位**（`FrameGovernorHost.cs:172`），
   真基线在 `EnableGovernor()` 里用 `GetCurrentRenderScale()` **重算**（`FrameGovernorHost.cs:283-299`），
   所以两条 `BeforeSceneLoad` 的相互顺序确实无关——施工这段说法与代码一致。

### 二、必答问题②：Player（资产只读）下是"有效"还是"空转"

**结论：Player 下是空转；施工的诚实边界声明与代码一致，且这次声明没有夸大。**

- 代码事实：生产 target 只有一条写路径 `urp.renderScale = scale`（`RenderScaleRecovery.cs:82-88`）。Player 包里 URP 资产序列化在包内只读，
  赋值只改运行时内存、无法落盘 ⇒ 上次会话"根本留不下 0.75 的脏盘"，本机制没有可修的东西；若赋值抛异常，两处都只告警一次
  （`RenderScaleRecovery.cs:252-270` 走 `failed++` 一次；`FrameGovernorHost.TryWriteScale` 用 `_warnedScaleWriteFailure` 一次性），不刷屏。
- 与施工声明逐条比对：其「诚实边界」表第 2 行写的正是"Player 里崩溃不会留下脏值，但这条自愈没有可修的东西"，与代码一致；
  它**没有**声称"在 Player 里救回了画质"。
- **未独立验证的部分（如实标注）**：本次没有 Player 构建产物，我没有在真 Player 里复现"只读拒绝"；该点只做了代码审读 + 施工用例
  （`AssetWriteRefusal_...` 我自己复跑全量时是通过的，但我没有独立构造那份只读场景）。

### 三、必答问题③：是否存在"日志写盘失败时仍然去改资产"的路径 —— **存在，按清单判 FAIL**

**代码**（决定性）：`FrameGovernorHost.PersistJournalEntry`（`FrameGovernorHost.cs:471-497`）把 `Save(...)` 的返回值只用来发一次告警，
**随即返回 void**；`ApplyRenderScale` 紧接着**无条件**执行 `if (!Mathf.Approximately(...) && TryWriteScale(urp, scale))`。
即：日志写盘失败 → 一条 `crash recovery is DISABLED for this session` 告警 → **照样把资产改成 0.95/0.90**。
这直接违反简报设计点 1「首次修改 renderScale **之前**，必须已把恢复日志原子写盘」（日志没落盘就不构成"写前落盘"）。

**独立实测**（自有用例 `Audit_Q3_JournalWriteFailure_StillWritesAsset`：把日志目录位置放成一个**普通文件**，让每次 `Save` 都失败）：
```
AUDIT-Q3: journalRootIsFile=True journalExists=False asset=0.90 writes=[0.95,0.9] journalWarnings=1
```
—— 文件系统拒绝写日志，资产仍被连降两档，盘上**没有任何日志**。此刻强杀进程 → 下次启动读到 0.90 当用户原值 → 正是本任务要斩断的复利。

**次生后果（比设计点本身更严重，且是同一根因）**：`ApplyRenderScale` 每次写都会先 Save，所以**第一次成功、后续失败**（磁盘写满的典型时序）时，
盘上留下的是**过期**的 `lastWritten=0.95`，而资产实际已到 0.90 → 下次启动命中 G3（当前 0.90 ≠ 0.95）→ `Abandoned`、**拒绝恢复**、只删日志
（V4 已实测该分支行为：`writes=[] asset=0.90`）。也就是说这条路径不但失去自愈，还会把资产**永久钉在 0.90**。

**修复方向（交主控重派，不由验收改代码）**：把 `PersistJournalEntry` 改成返回 bool；返回 false 时**放弃本次写资产**（至少放弃该资产的第一次写），
即"无日志不降级"。同时建议给"后续 Save 失败"加补偿：失败时立即把该资产恢复成本次会话基线并抛掉该条目，避免盘上留下过期 lastWritten。

### 四、额外项：`zrender.dll` 处于 M —— 判定「范围外的构建副产物」，**不判 FAIL**

证据（四条互相独立）：
1. `git status --short Native/` 为空；`find Native -name "*.cpp/*.h" -newer dll` 只有测试文件 `test_zrhi_vulkan_spv.cpp`（内容与 HEAD 一致，仅 mtime）。
2. 工作区 dll 与 `Native/build/bin/Release/zrender.dll` **md5 完全相同** `c20ef91cd5cb598bc4ead49cf1d53bfc`——就是 `run_tests.ps1` 第 4 步
   `deploy zrender.dll to Unity plugins` 的产物（本次六段日志原文：`copied: zrender.dll -> ...\Assets\Plugins\ZRender`）。
3. 与 HEAD 入库版逐字节/逐节比对：仅 **540 字节**不同，`strings` 差异只有 `04:33:09`→`09:59:27`、`Oct 27 2026`→`Oct 28 2026`
   （`__DATE__/__TIME__`）及其引起的字符串表移位（.text 里 23 处差异全是 `lea reg,[rip+…]` 的位移量）；
   **导出符号集完全一致（各 9 个，无增无减）**。
4. 历史：该 dll 在 `d27445d / be2adcf / 6c8d85a / 09115dd` 等 commit 里反复随重建变化（ADR-004 规定 dll 入库）。

⇒ 是重建副产物，**不含未声明的原生改动**。**但主控提交时请注意**：HEAD 入库版比当前源码的构建产物**旧**（工作区多一个 `ffxCreateContext` 串），
即"入库 dll ≠ 当前源码构建物"这一既有不一致，建议本次 commit 把这个重建 dll 一并带上，或另立治理项校正。

### 五、发现的问题清单（按严重度）

1. **【高·判 FAIL】日志写盘失败时仍写资产**（必答③，见上）。后果：磁盘满/权限/目录异常时本任务静默退化为"没有本机制"，且第二次起会把资产钉在脏值上。
2. **【中，同一根因的可见性】`ZLog` 通道默认全关，告警在生产环境完全看不见**：全仓非测试代码只有 `ZLog.EnableChannel` 的定义、**没有一处调用**；
   而 `ZLog.Write` 在 `WouldLog(channel,level)` 为假时连 sink 都不走（`ZLog.cs:122-131`）。实测真实 PlayMode 启动日志 1478 行里 0 条 governor/settings 行
   （自愈确实执行了，只是话没说出来）。⇒ G1 要求的"留下**可诊断**日志"在生产里落空；这属全项目既有问题（A-R9-01 的"governor enabled"同样不可见），
   但本任务的验收点正好踩在它上面。建议立治理项：`Warn/Error` 无条件输出，或启动时显式 enable 若干通道。**本条不是本任务引入，故不单独计 FAIL。**
3. **【中低】既有用例仍直接改真实 URP 资产**：`FrameGovernorHostTests` 走 `Initialize(_telemetry, null, ...)`（target 传 null ⇒ 生产 target），
   在 EditMode 里把真实 `URP_*.asset` 的 `renderScale` 写到 0.95 再靠 `RestoreBaseline` 还原（`_originalScales` 断言）。本次全绿、盘面干净，
   但一旦中途抛异常/被 kill，真实资产会停在脏值内存态（下次编辑器保存即落盘）。简报硬约束"测试禁止改真实资产"在既有用例上并未成立，本任务只把日志目录挪走了。建议后续把这两条 fixture 也换成 fake target。
4. **【低】`SettingsBootApplier` 未被改动**（允许而非必须）：排序依赖"两个不同 load type"这一点没有代码注释交叉引用 `RenderScaleRecovery`，
   将来若有人把自愈改成 `AfterSceneLoad` 不会被任何断言拦住（现有 10 条用例全都是直接调 `Recover()`，不覆盖钩子先后）。
5. **【低/观察】`RenderScaleRecovery` 只按 `urp.name` 匹配资产**：三个档位若出现同名资产（不同文件夹）会退化到第一次命中；施工已在"没做的事"里声明，实测无影响。

### 六、现场还原确认

- 手工植入的 `persistentDataPath/governor_recovery.json`：**已被真实启动消费**（这是被测行为本身）；植入的 `settings.json`：已 `mv` 到
  `TempLogs/archive_pre_audit/planted_settings.json`（该文件原本不存在），目录恢复为只有 `saves/`。
- 我的临时审计用例 `Assets/ZFrame/Tests/Pipeline/_AuditR902_Temp.cs`（+`.meta`）：已整体 `mv` 到 `TempLogs/archive_pre_audit/`，工程内无残留。
- 还原后 `git status --short` 与验收开始时**逐行一致**（仅 4 个 M + 6 个 ??，`URP_*.asset` 与 `ProjectSettings/` 均无变化）；`git log -1` 仍 `eaf32be`；
  无 `Unity.exe` 残留进程（只剩 Unity Hub / Licensing 常驻服务）。
- 证据文件：`TempLogs/audit_r9_02_six.log`、`TestReport.txt`（gitignore 内）、`TempLogs/audit_r9_02.xml`（我的 7 条用例 7/7 绿）、
  `TempLogs/audit_r9_02_unity.log`、`TempLogs/audit_r9_02_play.{xml,log}`、`TempLogs/archive_pre_audit/`。

### 最终裁决

**FAIL**（V1–V9 九项全部 PASS，唯一 FAIL 落在清单"须在结论中回答"的第③条：**存在"日志写盘失败仍去改资产"的路径**，
清单明文规定"若存在按 FAIL"，且实测该路径会把资产钉在脏值上）。修复面很小、只涉及 `FrameGovernorHost.PersistJournalEntry` 的返回值与
`ApplyRenderScale` 的一个分支，建议**重派一轮**（附上观察 2 的可见性修复更佳），不必退回设计。

---

## 返工简报（主控，第 2 轮施工）

> 第 1 轮施工的 V1–V9 九项全部 PASS，唯一 FAIL 是「日志写盘失败仍改资产」。修复面小，**不退回设计**。

### 缺陷（已由验收代理实测，附复现）

- `FrameGovernorHost.ApplyRenderScale` 无条件调用 `PersistJournalEntry(...)` 后继续写 `urp.renderScale`；
  `PersistJournalEntry` 返回 `void`，`Journal.Save` 返回的 `false` 只用于发一条告警，**不影响是否写资产**。
- 实测（把日志目录位置放成普通文件）：`journalExists=False asset=0.90 writes=[0.95,0.9]` —— 无日志保护却已降级。
- **次生后果**：若"首次 Save 成功、后续失败"，磁盘上会留下**过期 `lastWritten`**；下次启动命中原值校验 → 判为
  "第三方已改（G3）" → 拒绝恢复 → 资产被**永久钉在脏值**。这条比"本会话失去保护"更严重。

### 主控定的契约（必须按此实现，不接受替代方案）

1. **日志落盘成功 = 写 `renderScale` 的前置条件**。`PersistJournalEntry` 改为返回 `bool`（或等价可判定形式）。
2. 落盘失败时：
   - **不写**资产（`ApplyRenderScale` 直接返回，保持当前值）；
   - 打**一条**告警（不刷屏，与既有 `journalWarnings=1` 语义一致）；
   - **本会话停止后续降级尝试**（避免每个 tick 反复失败/刷日志），并在日志里说明"DRS disabled: crash recovery unavailable"；
   - **必须把已存在的日志条目失效化**（删除该槽位条目或整体作废），保证磁盘上不留过期 `lastWritten` ——
     这是堵住"永久钉死"的关键，不能只 return。
3. 既有 PASS 行为不得回归：正常写入路径仍"日志先落盘 → 再写资产"；`Shutdown()` 清理；G3 不覆盖；损坏隔离。

### 必须新增的测试（真断言，修复前必红）

- T1 `JournalSaveFails_DoesNotWriteAsset`：构造 Save 失败 → 断言 `urp.renderScale == 原值`、无写入发生、告警恰好 1 条。
- T2 `JournalSaveFailsAfterFirstSuccess_InvalidatesStaleEntry`：先成功写一次（模拟已降级到 0.95）→ 再让 Save 失败 →
  断言磁盘上**不存在**含 `lastWritten=0.95` 的有效条目；随后模拟下一次启动，断言**不会**因过期条目而拒绝恢复（不出现"永久 0.95"）。
- T3 `JournalSaveFails_GovernanceDoesNotLoop`：连续多 tick 失败，告警仍为 1 条且资产恒为原值。

### 硬约束

- 只改 `View/Pipeline/**` 与 `Tests/Pipeline/**`；`FrameGovernor.cs`/`GovernorLadder` 零改动；不动 URP 资产；不 commit。
- **不要**在本任务改 `ZLog` 通道可见性（验收观察 2 属全项目既有问题，主控已记入遗留，勿扩大范围）。
- 自测必须跑**六段全量**并贴实际数字（native 190/710,800 不应变；editmode 673 + 新增用例数）；`.cs` 无 BOM。

### 交付

施工报告追加到本文件末尾（新小节「施工报告（第 2 轮）」），含：改法、T1–T3 实际输出、六段实测数字、未验证项。

---

## 施工报告（第 2 轮）

**结论**：唯一 FAIL 项（"日志写盘失败仍写 `urp.renderScale`"）已按主控契约修掉——**落盘成功是写资产的前置条件**。
落盘失败时：不写资产 → 把本会话已落地的值回滚到本会话基线 → 把该资产的日志条目失效化（磁盘上不留过期 `lastWritten`）
→ 本会话剩余时间停用 RenderScale 这一项措施 → 只打**一条**告警。次生后果（过期 `lastWritten` → 下次启动被判 G3 →
"永久钉在脏值"）一并堵死。新增 T1–T3 三条真用例（六段全量里 editmode **676 = 673 + 3**），全部绿；
变异回退成第 1 轮代码则**恰好这 3 条 + 被改写的旧降级用例共 4 红**。

### 1. 改法（文件:行号）

改动只在 `View/Pipeline/**` 与 `Tests/Pipeline/**`：

| 位置 | 改动 |
|---|---|
| `View/Pipeline/FrameGovernorHost.cs:84` | 新增字段 `_renderScaleMeasureDisabled`（会话级，一旦置真不再复位）。 |
| `View/Pipeline/FrameGovernorHost.cs:412-455` `ApplyRenderScale` | ①会话级停用后直接返回（不再每个 tick 重试）；②`PersistJournalEntry` 返回 `false` → `AbandonRenderScaleMeasure(...)` + `return`，**两条语句直读，没有任何 try/finally 或 catch 补写**；③只有落盘成功后才把资产记进 `_touchedRenderScales` 并 `TryWriteScale`。 |
| `View/Pipeline/FrameGovernorHost.cs:469-494` `AbandonRenderScaleMeasure`（新） | **顺序**：先把本会话已脏的资产回滚到基线（`_touchedRenderScales` 里记的用户原值）→ 再失效化日志条目 → 最后置停用标志 + 一条告警（`DRS disabled: crash recovery unavailable`）。回滚在前是有意的：即使失效化也失败（同一块坏盘），盘上也已经是用户原值，下次启动没有可修的东西。 |
| `View/Pipeline/FrameGovernorHost.cs:503-521` `InvalidateJournalEntries`（新） | 丢掉该资产的条目；条目清空 → 整份 `Delete()`；还有别的条目 → 用剩余条目重写一次，重写也失败则**整体作废** `Delete()`（契约允许的"整体作废"）。宁可丢本会话的崩溃安全，也不留过期 `lastWritten`。 |
| `View/Pipeline/FrameGovernorHost.cs:560-577` `PersistJournalEntry` | `void` → **`bool`**：返回 `_recoveryJournal.Save(...)` 的结果；不再在此处发告警（告警归 `AbandonRenderScaleMeasure`，保证只一条）。 |
| `View/Pipeline/RenderScaleRecoveryJournal.cs:100` | 新增 `TempFilePath`（原子写用的 `.tmp` 路径），`Save` 改用它；同时也是 T2 模拟"盘满"的抓手。 |
| `View/Pipeline/RenderScaleRecoveryJournal.cs:104-111` | `Save` 的文档改成"返回值是判决而不是备注：调用方不得在没记下原值时弄脏资产"。语义未变（返回 false 的时机与异常集合完全没动）。 |
| `Tests/Pipeline/RenderScaleRecoveryTests.cs:345/380/432` | 新增 T1/T2/T3；`:315` 把第 1 轮的 `JournalWriteFailure_KeepsGoverning_WithExactlyOneWarning` 改写成 `JournalWriteFailure_DisablesRenderScaleMeasure_ButOtherMeasuresLand`（旧断言"measures must still land"正是本次被裁决为 FAIL 的行为，故必须反过来断言；同时补了"其它措施照旧落地"这条更强的断言，没有删弱任何断言）；`:550` 新增 `BlockedJournal()`（用"目录位置被一个普通文件占住"制造**真实** IO 失败，不 mock 日志类）。 |

`FrameGovernor.cs` / `GovernorLadder.cs` / `GovernorTuning.cs` / `SettingsBootApplier.cs` **零改动**
（`git diff --stat` 对这四个文件为空）；迟滞状态机、阶梯定义、开机应用点一行没动。

### 2. 新语义的完整时序（写失败的两条时间线）

```
A. 首次写就失败（T1 的路径）
   rung=0.95 → PersistJournalEntry → Save=false
     → 不写资产（Attempts=0，连"写被拒"都不发生）
     → InvalidateJournalEntries：条目清空 → 删文件（本来也没有）
     → measure 停用 + 一条告警（DSR disabled: crash recovery unavailable）
   结果：asset=1.00，盘上无日志，会话后续 rung（0.90/0.85/…）一律不再碰 renderScale

B. 先成功后失败（T2 的路径，即验收说的"次生后果"）
   rung=0.95 → Save=true → 资产 0.95（盘上 lastWritten=0.95）
   rung=0.90 → Save=false
     → 回滚：资产 0.95 → 1.00（本会话基线）
     → 失效化：条目清空 → 删文件 ⇒ 盘上不再有 lastWritten=0.95
     → measure 停用 + 一条告警
   下次启动：NoJournal（不是 G3）⇒ 资产不会被"永久钉死"在脏值
```

### 3. T1–T3 实际输出（六段全量 `TempLogs/runtests_editmode.xml` 的 `<output>` 原文）

```
T1: journalExists=False, asset=1.00, writes=[], writeAttempts=0, rungs=1, journalWarnings=1
T2: staleEntry(lastWritten=0.95)=False, asset=1.00, journalExists=False, writes=[0.95,1], journalWarnings=1
T3: asset=1.00, writes=[], writeAttempts=0, rungs=10/10, tier=Low, journalWarnings=1
```
字段含义与断言：
- **T1**（`JournalSaveFails_DoesNotWriteAsset`）：`writeAttempts=0` 是"连 `WriteScale` 都没被调用"（不是"写了同样的值"）；
  `rungs=1` 证明阶梯**确实走了**、被跳过的是 renderScale 落地，不是治理没跑；`journalWarnings=1` 且文案含 `DRS disabled`。
- **T2**（`JournalSaveFailsAfterFirstSuccess_InvalidatesStaleEntry`）：`staleEntry=False` 由 `TryRead` 解析**已落盘的文件**得出
  （不是只看 `File.Exists`）；`writes=[0.95,1]` 说明回滚是真的写回 1.0；随后真的跑了一次 `RenderScaleRecovery.Recover`，
  得到 `NoJournal`、资产仍 1.00 ⇒ "不会因过期条目拒绝恢复、不出现永久 0.95"。
- **T3**（`JournalSaveFails_GovernanceDoesNotLoop`）：42 tick 走满 10 档（`rungs=10/10`，`tier=Low` 证明影子档照常降级），
  告警仍只有 **1** 条、`writes=[]`、资产恒 1.00。
- 改写后的旧用例：`journal write failure: asset=1.00 (measure skipped), tier=Low, writes=[], journalWarnings=1`
  —— renderScale 不动，但 tier 已经降到 Low，即"只停这一项措施，治理本身不退化"。

**可证伪性（变异对照，本次实跑）**：把 `ApplyRenderScale` 改回第 1 轮的"无条件写"（`if (false && !PersistJournalEntry(...)){...}` +
无条件 `PersistJournalEntry`），editmode 跑出 **4 红**，正是这四条（对照留在 `TempLogs/mutation_r9_02_round2.xml`）：
```
T1 → Failed  msg: T1: the asset must stay at its original value when the journal cannot be written
              Expected: 1.0f But was: 0.949999988f
              OUT: T1: journalExists=False, asset=0.95, writes=[0.95], writeAttempts=1, rungs=1, journalWarnings=0
T2 → Failed  msg: T2: a stale lastWritten=0.95 would be read as a third-party edit on the next boot (G3) and never healed
              Expected: False But was: True
              OUT: T2: staleEntry(lastWritten=0.95)=True, asset=0.90, journalExists=True, writes=[0.95,0.9], journalWarnings=0
T3 → Failed  msg: T3: the asset stays at its original value for the whole session  Expected: 1.0f But was: 0.75f
              OUT: T3: asset=0.75, writes=[0.95,0.9,0.85,0.8,0.75], writeAttempts=5, rungs=10/10, tier=Low, journalWarnings=0
旧降级用例 → Failed  msg: an asset may not be dirtied while its original value is unrecorded  Expected: 1.0f But was: 0.75f
              OUT: journal write failure: asset=0.75 (measure skipped), tier=Low, writes=[0.95,0.9,0.85,0.8,0.75], journalWarnings=0
```
T2 那条红就是验收报告的复现原样（`journalExists=True asset=0.90 writes=[0.95,0.9]`）——过期 `lastWritten=0.95` 真的留在了盘上。
变异跑完已用备份原样恢复（`ABANDON-DEBUG`/`UnityEngine.Debug.Log` 残留在最终文件里 `grep` 计数为 0）。

### 4. 六段实测数字

`unset http_proxy https_proxy all_proxy` 后 `powershell -NoProfile -ExecutionPolicy Bypass -File Tools/run_tests.ps1`；
`TestReport.txt`（`Generated: 2026-10-28 21:57:37  (wall 0:03:21)`）：
```
native   PASS   cases=190 asserts=710800 failed=0; ctest: 100% tests passed (1 entry: zrender_test) 00:10
editmode PASS   total=676 passed=676 failed=0 skipped=0 5.4993663s(xml)                            00:50
playmode PASS   total=6 passed=6 failed=0 skipped=0 5.9253679s(xml)                                00:50
arch     PASS   exit=0; report: TempLogs/arch_report.txt                                          00:30
config   PASS   exit=0; report: TempLogs/config_report.txt                                        00:30
naming   PASS   exit=0; report: TempLogs/name_report.txt                                          00:30
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)
```
- native 仍 **190 用例 / 710,800 断言**（未动 `Native/**`）。
- editmode **676 = 673 + 3**：`Tests/Pipeline/RenderScaleRecoveryTests.cs` 的 `[Test]` 计数 **13**（第 1 轮 10 + T1/T2/T3，
  旧降级用例是改写不是新增），xml 里 `ZFrame.Pipeline.Tests total=79 passed=79 failed=0`。
- arch 原文：`违规数: 0  结果: PASS`（未新建程序集，两个新方法都落在既有 `ZFrame.View` 内）。
- 跑完真实存档目录 `%USERPROFILE%/AppData/LocalLow/ZFrame/ZFrame` 仍只有 `saves/`，无 `governor_recovery.json`。

### 5. 现场是否干净

```
 M UnityProject/Assets/Plugins/ZRender/zrender.dll
 M UnityProject/Assets/ZFrame/Tests/Pipeline/FrameGovernorHostTests.cs
 M UnityProject/Assets/ZFrame/Tests/Pipeline/FrameGovernorSettingsTests.cs
 M UnityProject/Assets/ZFrame/View/Pipeline/FrameGovernorHost.cs
?? Docs/Logs/TASK-A-R9-02.md
?? UnityProject/Assets/ZFrame/Tests/Pipeline/RenderScaleRecoveryTests.cs(+.meta)
?? UnityProject/Assets/ZFrame/View/Pipeline/RenderScaleRecovery.cs(+.meta)
?? UnityProject/Assets/ZFrame/View/Pipeline/RenderScaleRecoveryJournal.cs(+.meta)
```
- `git status --short | grep -i "URP_.*asset"` → **none**；`git status --short -- UnityProject/ProjectSettings/` → 空。
- 2 个既有测试 fixture 的 `M` 是第 1 轮就有的（只多加了一行"日志目录指向临时目录"），本轮未再动它们。
- `zrender.dll` 仍是六段重构建的副产物：工作区 dll 与 `Native/build/bin/Release/zrender.dll` 的 md5 完全一致
  `c20ef91cd5cb598bc4ead49cf1d53bfc`（与 A-R9-01 验收记录的 md5 相同），未做任何手工回滚。
- 三个被改/新增的 `.cs` 首字节 `75 73 69`（无 BOM）、`crlf=0`；`git log -1` 仍 `eaf32be`（**未 commit**）；`tasklist` 无 `Unity.exe`。

### 6. 未验证项与残余风险（诚实边界）

1. **"盘全满"的真实场景没在本机复现**：T1/T3 用"目录位置被普通文件占住"、T2 用"`.tmp` 路径被目录占住"制造 IO 失败，
   与磁盘满在 `IOException/UnauthorizedAccessException` 这一层的效果等价，但**没有**在真的写满的卷上跑过。
2. **失效化本身也可能失败**：若"重写剩余条目"和"删除日志"都失败（同一块坏盘的极端情形），盘上会留下旧文件名下的过期条目。
   此时资产已被回滚到用户原值，下次启动会走 G3（`当前值 1.00 ≠ lastWritten 0.95`）→ 只告警 + 清日志、**不做错误写入**，
   不会钉死脏值，但会多一条"被人改过"的误报。这条路径没有用例（无法用文件系统确定性构造"连删除都失败"）。
3. **会话级停用不可逆**：`_renderScaleMeasureDisabled` 一旦置真，直到宿主 `Shutdown()` 都不会再恢复（用户在同一次运行里
   关掉再打开 DRS 也不会重试）。这是照契约"本会话停止后续降级尝试"做的，但没有用例覆盖"关→开"的重启语义。
4. **多资产场景只做了单资产验证**：`InvalidateJournalEntries` 里"还有别的条目就重写剩余条目"这一支没有用例
   （T1/T2/T3 都只有一个资产）。代码路径直读可确认，但没有红绿证据。
5. **Player 构建未实测**（与第 1 轮相同）：本轮改动只影响"日志写不进去"的分支，Player 里资产只读仍走
   `TryWriteScale` 的拒绝路径（`AssetWriteRefusal_WarnsOnce_AndGovernanceKeepsRunning` 仍绿）。
6. **ZLog 可见性未动**（按主控要求）：告警在生产环境仍然看不见，属验收观察 2 的全项目遗留问题。


---

## 验收结论（第 3 轮）

**验收代理**：与两轮施工、第 1 轮验收均不同一代理。
**取证方式**：所有数字与结论来自本轮**独立复跑/独立构造**；施工报告、施工的 xml 仅作为"待核对的主张"，未当作证据。
**命令**：`powershell -NoProfile -ExecutionPolicy Bypass -File Tools/run_tests.ps1`（跑前 `unset http_proxy https_proxy all_proxy HTTP_PROXY HTTPS_PROXY ALL_PROXY`；因 Bash 侧禁止转调 powershell，改用 PowerShell 工具直接执行 .ps1，不经 Bash 包一层）；
自建探针与变异跑用 `Unity.exe -batchmode -projectPath UnityProject -runTests -testPlatform EditMode -testFilter … -testResults … -logFile …`（Bash，不经 `-quit`）。

### 逐条结论

| 项 | 结论 | 一行证据（本轮实测） |
|---|---|---|
| 契约 1：`PersistJournalEntry` 返回可判定值 | **PASS** | `FrameGovernorHost.cs:560` 签名 `private bool PersistJournalEntry(...)`，`:575` `return _recoveryJournal.Save(...)`；`ApplyRenderScale:440` 用 `if (!PersistJournalEntry(...)) { AbandonRenderScaleMeasure(...); return; }`，两条语句直读，无 try/finally、无 catch 补写 |
| 契约 2：失败→不写资产/一条告警/会话停降级/失效化条目 | **PASS**（含一处措辞性偏差，见「二」） | 我的 A1：`asset=1.00 writes=[] attempts=0 rung=1 journalWarnings=1 journalOnDisk=False`（阶梯确实进了 1 档，被跳过的是落地）；A2：首成功后失败 → `readStatus=Missing entries=-1 stale0.95=False asset=1.00 writes=[0.95,1] warnings=1` |
| 契约 3：既有 PASS 行为不回归 | **PASS** | 本轮六段全绿；第 1 轮 6 条既有行为用例（写前落盘 / G2 清理 / G3 / 损坏隔离 / 复利斩断 / 消失资产）在本轮 `runtests_editmode.xml` 里逐条 passed，`RenderScaleRecoveryTests` 13/13 |
| A 修好没有（自建构造） | **PASS** | 见「一」：A1/A2 全绿；**次生后果已用"真·下一次启动"验到**：`adoptedBaseline=1.0000 firstRung=0.95`（若 0.95 被钉住，这里会是 0.9025 或直接 NoJournal+0.95） |
| B 5 处旧用例改写是否正当 | **PASS** | 见「二」：实际是 **1 处改写 + 3 条新用例 + 1 个新 helper**（不是我任务书里写的"5 处旧断言"）；改写方向与裁决一致，且我自己的定向变异证明新断言有牙（只删 `InvalidateJournalEntries` 一行 → 恰好 T2 一条红） |
| C 六段回归 | **PASS** | `TestReport.txt`（`Generated: 2026-10-28 22:25:29`）：native `190 / 710800 / 0 failed`、editmode `676/676`、playmode `6/6`、arch/config/naming `exit=0`、`RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)`、`EXITCODE=0`；新增 = **3**（工作树 `[Test]` 计数 519 − HEAD 506 = 13，其中第 1 轮 10 + 本轮 3） |
| D 范围与现场 | **PASS** | `git status --short` 与验收开始时逐行一致（4 M + 6 ??），无 `URP_*.asset`，`ProjectSettings/` 空；`FrameGovernor.cs`/`GovernorLadder.cs`/`GovernorTuning.cs`/`SettingsBootApplier.cs` **逐个** `git diff --name-only` 为空；6 个 .cs `bom=False / crlf=0 / lf=行数`；`git log -1` 仍 `eaf32be`；`tasklist` 无 `Unity.exe` |
| E 变异证据可信度 | **PASS** | `TempLogs/mutation_r9_02_round2.xml` 真实存在（393,248 B，`testcasecount=676 total=676 passed=672 failed=4`），4 条红**恰好**是 T1/T2/T3 + 被改写的旧降级用例，栈行号 `…Tests.cs:409 / :449 / :362 / :330` 与**当前**磁盘文件逐行吻合（断言分别落在 T2 的 stale 断言、T3 的 asset 断言、T1 的 asset 断言、旧用例的 asset 断言） |

### 一、A：我自己构造的取证（三个探针，跑完已归档）

探针 `Assets/ZFrame/Tests/Pipeline/_AuditR903_Temp.cs`（3 条用例，三次运行后 3/3 绿），全部用 `CreateInstance<UniversalRenderPipelineAsset>()` 的一次性资产 + `%TEMP%` 目录，**没有碰真实 `URP_*.asset`、也没有碰 `persistentDataPath`**。

1. **A1「首次就写不进去」**（把日志根路径用一个普通文件占住，制造真实 IO 拒绝；不 mock 日志类）
   `AUDIT-A1: asset=1.00 writes=[] attempts=0 rung=1 journalWarnings=1 journalOnDisk=False`
   ⇒ 资产保持原值、`WriteScale` **一次都没被调用**（`attempts=0`，不是"写了同值"）、阶梯确实进了 1 档（跳过的是落地而非治理）、告警恰好 1 条、盘上无日志。
2. **A2「先成功后失败」—— 本修复的真正价值点**（第 1 档成功落到 0.95 且盘上 `lastWritten=0.95`，随后把 `.tmp` 路径占成目录让 Save 失败）
   `AUDIT-A2: readStatus=Missing entries=-1 stale0.95=False asset=1.00 journalExists=False writes=[0.95,1] warnings=1`
   —— 注意 `readStatus=Missing` 是**解析落盘文件**得出的（不是只看 `File.Exists`）：整份日志已被删掉，不存在"过期 `lastWritten`"，且 `writes=[0.95,1]` 说明回滚是**真的写回** 1.0（不是只改内存账本），`0.90` 那一档没落地。
   **然后我真的模拟了"下一次启动"**：销毁宿主 → 清掉我人为放置的 `.tmp` 目录（模拟一块恢复可写的盘）→ 新建宿主 + `SetEnabled(true)`：
   `AUDIT-A2-nextBoot: adoptedBaseline=1.0000 firstRung=0.95 level=1`
   ⇒ 新会话捕获的基线是 **1.0**、第一档仍写 0.95。**"因 G3 拒绝恢复 → 永久钉在 0.95"没有发生**。（对照：若基线被钉在 0.95，第一档会是 0.9025；若走了 G3，`Recover` 会返回 `Abandoned` 而不是 `NoJournal`。）
   *（插曲，如实记录：我第一次跑 A2 时忘记拆掉自己放的 `.tmp` 目录，新会话的日志同样写不进去，于是 `firstRung=1.00`、`level=1` —— 这恰好反证了"没有日志就不落地"在**第二次启动**也成立，不是我想要的主断言，拆掉阻塞后重跑得到上面的结果。）*
3. **A3「回滚也写不进去」（观察项，不参与裁决）**：首档成功后把资产改成"只读"（`WriteScale` 抛）且同时让日志写失败 →
   `AUDIT-A3-observe: asset=0.95 journalExists=False nextBootStatus=NoJournal journalWarnings=1`
   ⇒ 回滚失败时盘上留下 0.95 而日志被删，下次启动 `NoJournal` → 会把 0.95 当基线。**这是遗留风险，不是契约违反**（见「五」第 1 条）。

**施工的 T2 是否真的验了"下一次启动不复用"**（任务书要求我读代码确认，而不是只断言条目被删）：读了 `RenderScaleRecoveryTests.cs:380-424`。它在 `:400-402` 用 `journal.TryRead` 解析落盘文件判 `staleEntryOnDisk`（不是只看存在性），`:411` 断言资产已回 1.0，`:413` 断言回滚是真实写入，`:419-423` 真的调了一次 `RenderScaleRecovery.Recover` 并断言 `status == NoJournal` 且资产仍 1.0。**结论：验到了核心（过期条目不存在 + 恢复路径不会拒绝 + 资产不在脏值）；但它停在"Recover 返回 NoJournal"，没有再建一个宿主去断言下一会话的**阶梯基线**。这一步我用自己的 A2 补上了，结论一致。**故判定"真验了"，强度略低于我的构造。**

### 二、B：5 处改写的逐处判断（**先纠正事实：不是 5 处旧断言**）

**取证限制（先说明，避免把推测当已验证）**：`git diff UnityProject/Assets/ZFrame/Tests/Pipeline/RenderScaleRecoveryTests.cs` **返回空**——该文件自第 1 轮起就是 `??`（从未 `git add`），仓库里没有它的第 1 轮版本，`git stash` 为空，全盘也搜不到备份。所以我**无法**做逐字节 diff。我用了三条独立线索交叉：(a) 第 1 轮变异运行的 `TempLogs/mutation_editmode.xml` 用例名单与 `<output>` 原文；(b) 第 2 轮 `runtests_editmode.xml` 同名单与原文；(c) 当前源码逐行阅读 + 我自己的定向变异。

**对照结果**：第 1 轮 10 条 → 第 2 轮 13 条；差异只有「改名 1 条 + 新增 3 条」，其余 6 条用例的 `<output>` 与第 1 轮逐字相同（另 2 条仅多出 `TestContext.WriteLine` 打印行，第 1 轮报告里已引用过同样文字）。任务书点名的 5 个位置，实际性质是：

| 位置（当前行号） | 实际改了什么 | 判断 |
|---|---|---|
| `:315` `JournalWriteFailure_DisablesRenderScaleMeasure_ButOtherMeasuresLand` | **唯一被改写的旧用例**（原名 `JournalWriteFailure_KeepsGoverning_WithExactlyOneWarning`）。旧输出是 `journal write failure: asset=0.85 (measures still land)`；新断言为 `asset==1.0`、`Writes` 为空、`tier==Low`、告警==1 | **正当**。旧断言锁定的正是第 1 轮被裁决 FAIL 的行为（日志没写进去还把资产改了），必须反过来；新版本**没有掏空**："治理仍在跑"这条约束由 `PipelineManager.CurrentTier == Low`（走完整个 RenderScale 段后必然降档）承担，比旧版的"asset 变了"更有区分度。我自己的变异（删掉失效化）里这条用例仍绿、T2 独红，说明它没有被写成恒真 |
| `:345` `JournalSaveFails_DoesNotWriteAsset`（T1） | 新增。断言 `Governor.Level==1`（阶梯真走了）、`asset==1.0`、`Writes` 空、`Attempts==0`、告警==1 且含 `DRS disabled` | **非恒真**：施工变异（回退成无条件写）→ 该条红（`But was: 0.949999988f`）；本轮六段里绿 |
| `:380` `JournalSaveFailsAfterFirstSuccess_InvalidatesStaleEntry`（T2） | 新增。解析落盘文件判 stale、断言回滚写 1.0、再跑 `Recover` 得 `NoJournal` | **非恒真，且是"一针见血"的那条**：我只删掉产品代码里 `InvalidateJournalEntries(urp);` **一行**（保留"失败不写资产"），filter 跑 13 条 → **恰好 1 红，就是 T2**（`staleEntry(lastWritten=0.95)=True`）。说明它测的就是失效化本身，不是搭便车 |
| `:432` `JournalSaveFails_GovernanceDoesNotLoop`（T3） | 新增。42 tick 走满 10 档、`asset==1.0`、`Writes` 空、告警==1 | **非恒真**：施工变异下红（`But was: 0.75f`，`writes=[0.95,0.9,0.85,0.8,0.75]`） |
| `:550` `BlockedJournal()` | 新增 helper：用"日志根位置被一个普通文件占住"制造**真实** IO 失败，不 mock 日志类 | **正当**，比"假日志返回 false"更接近真实盘故障；T1/T3/改写后的旧用例都用它 |

**防"为过而改测试"的交叉检查**：T1/T2/T3 都建立在"阶梯确实在走"之上（`Level`/`rungs` 前置断言），所以不能靠"把 DRS 整个掏空"来骗过；而"掏空 DRS"会立刻让 `NormalShutdown_RestoresAsset…`（断言资产被写到 0.95）和 `Journal_IsAlreadyOnDisk…`（断言恰好一次写入且盘上是 0.95）变红。**结论：没有发现放宽到恒真、也没有发现删掉了真正的行为约束。**

### 三、C：六段全量（本轮实测，未复用施工数字）

```
native   PASS  cases=190 asserts=710800 failed=0; ctest 100% (1 entry)   00:11
editmode PASS  total=676 passed=676 failed=0 skipped=0                   00:55
playmode PASS  total=6 passed=6 failed=0 skipped=0                       00:50
arch     PASS  exit=0   config PASS exit=0   naming PASS exit=0
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)   EXITCODE=0
```
- native **190 / 710,800 未变**（`git status Native/` 为空）。
- `RenderScaleRecoveryTests` 在本轮 xml 里 `testcasecount=13 result=Passed 13/13`。
- 新增用例自数：`[Test]` 属性工作树 519 vs HEAD 506 = +13（第 1 轮 10 + 本轮 3）；editmode 673 → 676 = **+3**，与"只加 T1/T2/T3、旧用例改名不加数"相符。

### 四、D：范围与现场

- `git status --short` 与本轮开始时**逐行一致**（4 个 `M` + 6 个 `??`）；`grep -i "URP_.*asset"` → `none`；`git status --short -- UnityProject/ProjectSettings/` → 空。
- 四个"不许动"的文件**逐个**确认：`FrameGovernor.cs` / `GovernorLadder.cs` / `GovernorTuning.cs` / `SettingsBootApplier.cs` 的 `git diff --name-only` 全为空。
- 6 个被改/新增的 `.cs`：`bom=False`，`crlf=0`，`lf` 与行数一致（Python 逐字节统计，非 grep）。
- `git log -1` 仍 `eaf32be`（未 commit）；`Documents/Logs` 之外没动 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`；`tasklist` 无 `Unity.exe`。
- `zrender.dll` 仍是重建副产物：工作区与 `Native/build/bin/Release/zrender.dll` md5 同为 `c20ef91cd5cb598bc4ead49cf1d53bfc`（与施工声称一致，也与第 1 轮验收记录一致）——即本轮重建没有引入新的二进制漂移。

### 五、E：变异证据真伪

**真实、非伪造、非空文件**。四项互证：
1. 文件存在且完整：393,248 B，`<test-run … total="676" passed="672" failed="4">`（不是 0 字节、也不是全绿的重命名）。
2. 4 条红的名字与施工声称**完全一致**：`JournalSaveFails_DoesNotWriteAsset`、`JournalSaveFailsAfterFirstSuccess_InvalidatesStaleEntry`、`JournalSaveFails_GovernanceDoesNotLoop`、`JournalWriteFailure_DisablesRenderScaleMeasure_ButOtherMeasuresLand`。
3. `<failure>` 里的**栈行号**（`RenderScaleRecoveryTests.cs:362/409/449/330`）与我今天读的**磁盘文件逐行吻合**（分别是 T1 的 asset 断言、T2 的 stale 断言、T3 的 asset 断言、旧用例的 asset 断言）——伪造者要先伪造行号，且必须是这份文件的行号；配合 `<output>` 里的变异行为（`asset=0.95 writes=[0.95]`、`writes=[0.95,0.9] journalExists=True`）与"回退成无条件写"完全自洽。
4. 时间线自洽：xml `start-time 13:53:45Z`（=21:53:45 本地）/ 文件 mtime 21:53:50；`FrameGovernorHost.cs` mtime 21:54:04（变异后 14 秒还原）；随后 21:55:13 的全绿 `runtests_editmode.xml` 是还原后的运行。当前源码里 `grep "ABANDON-DEBUG|AUDIT MUTATION"` 计数为 0。
**补充（我自己的独立变异，见上表 T2）**：删一行 `InvalidateJournalEntries` → 13 条里恰好 1 红（T2）；还原后复跑 13/13 绿，且还原文件 md5 回到 `067fd91062e9e3d1b36fd4a80a2257f8`（与变异前一致）。这说明这套用例**不是一组恒真断言**。

### 六、遗留问题（均为**非 FAIL**，按严重度）

1. **【中·遗留，本轮新发现】"回滚写"也被平台拒绝时，会把可救的脏值变成不可救**：`AbandonRenderScaleMeasure`（`FrameGovernorHost.cs:469-494`）先 `_touchedRenderScales.Remove(urp)` 再 `TryWriteScale(baseline)`；若这次写被拒（Player 只读资产 / URP 赋值抛错），资产停在 0.95、随后 `InvalidateJournalEntries` 仍把日志删掉，下次启动 `NoJournal` → 0.95 被当作基线。我的 A3 实测到该状态：`asset=0.95 journalExists=False nextBootStatus=NoJournal`。
   **为什么不判 FAIL**：契约 2 明文允许"删除该槽位条目或**整体作废**"，且该路径要求"日志写失败"与"资产写失败"**同时**发生（窄），相比第 1 轮的"日志写失败仍无条件写资产"（必然发生）是数量级的收窄；代码注释也写明了取舍。**建议后续**：回滚失败时**保留**该条目（此时 `lastWritten` 与盘上值一致、并非过期），下次启动可以自愈。
2. **【中低·遗留，仅代码直读，未构造】**多资产场景 `InvalidateJournalEntries:517-520`：还有别的条目时重写剩余条目，重写失败就 `Delete()` **整份**。若此刻另一个资产仍是脏的（前一轮已降级、档位切换后本资产才失败），"整体作废"会重新制造"脏资产 + 无日志"。契约允许"整体作废"，但这条没有用例覆盖（施工在"未验证项 #4"里也自认了）。**如实标注：本轮未执行，仅读代码。**
3. **【低】会话级停用不可逆**：`_renderScaleMeasureDisabled` 一经置真，`SetEnabled(false)→true` 也不会重试（施工自认 #3）。与契约"本会话停止后续降级尝试"一致，但"关掉再开"算不算新会话没有定义。
4. **【低】ZLog 通道默认关闭**（第 1 轮观察 2 的延续）：告警在生产环境仍不可见。主控已明确本轮不动，沿用遗留。
5. **【低/既存】`FrameGovernorHostTests` 仍走生产 target 改真实 URP 资产**（第 1 轮遗留 #3）：本轮只把日志目录挪到临时目录，未换 fake target。本轮全绿、盘面干净，但中途被 kill 仍可能留下内存脏值。
6. **未验证（如实标注，不作 PASS 依据）**：真 Player 构建下的只读拒绝、真"磁盘写满"卷、`RuntimeInitializeOnLoadMethod` 钩子在真启动里的先后（本轮未再跑 PlayMode 注入日志，沿用第 1 轮的结构性结论：`BeforeSceneLoad` < `AfterSceneLoad` + `SettingsModel.Load` 唯一调用点）。

### 七、现场还原确认（我的构造会碰临时目录与产品代码，已逐项还原）

- 自建探针 `Assets/ZFrame/Tests/Pipeline/_AuditR903_Temp.cs`（+`.meta`）：已整体 `mv` 到 `TempLogs/archive_r903/`，工程内无残留。
- 我的变异备份 `TempLogs/archive_r903/FrameGovernorHost.cs.preMutation` 已 `cp` 回 `View/Pipeline/FrameGovernorHost.cs`：**md5 `067fd91062e9e3d1b36fd4a80a2257f8` 与变异前一致**，`AUDIT MUTATION` 标记 grep 计数 0，还原后复跑 `RenderScaleRecoveryTests` **13/13 绿**。
- 真实 `persistentDataPath`（`%USERPROFILE%/AppData/LocalLow/ZFrame/ZFrame`）全程只有 `saves/`，**没有** `governor_recovery.json`、也没有 `.corrupt_`；三个探针的临时目录都在 `%TEMP%` 且已随 TearDown 删除（`ls %TEMP%/zframe_audit_r903_*` 为空）。
- 真实 `URP_*.asset` 全程未被任何探针写入（探针一律 `CreateInstance` + `FakeTarget`）；六段跑完 `git status` 无 `URP_*.asset`。
- 还原后 `git status --short` 与验收开始时逐行一致（4 M + 6 ??）；`git log -1` 仍 `eaf32be`；无 `Unity.exe` 残留。
- 证据文件：`TempLogs/audit_r903.xml`（3/3 绿）、`audit_r903_restored.xml`（还原后 13/13）、`audit_r903_mutation2.xml`（我的定向变异，1 红=T2）、`TempLogs/archive_r903/`（探针源码 + 变异前备份 + 上述 xml/log）。

### 最终裁决

**PASS**。
上一轮唯一 FAIL（日志写盘失败仍写 `urp.renderScale`）的**根因**与**次生后果**都已按主控契约修掉，且我用与施工不同的构造独立验到了"下一次启动不再复用脏基线"这一真正价值点
（`readStatus=Missing / stale0.95=False / adoptedBaseline=1.0000 / firstRung=0.95`）；既有 PASS 行为（写前落盘、Shutdown 清理、G3、损坏隔离、复利斩断）六段复跑无回归；
5 处"改写"经查实为 1 处正当反转 + 3 条新增 + 1 个真实 IO 失败的 helper，未发现放宽到恒真或删约束；变异证据真实且能被我自己的定向变异交叉验证。
遗留问题（尤其第 1 条"回滚写被拒"）**不构成本轮 FAIL**，但建议主控记入治理项，在下一次触碰 `View/Pipeline/**` 时一并处理。

