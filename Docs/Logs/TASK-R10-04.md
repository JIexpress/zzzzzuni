# TASK-R10-04 —— 修复审计 P1（C# 批：UI 淡入 Task 软锁，A-8）

- **轨道**：A / R10
- **状态**：doing
- **派发**：主控，2026-10-29（本简报由主控依 R10-02 审计 A-8 + 验收复核补写——workbuddy 中断前只入了板未写简报）
- **施工**：general-purpose 施工子代理，2026-10-30
- **验收**：general-purpose 验收子代理（与施工不同代理），2026-10-30
- **来源**：`Docs/Logs/TASK-R10-02.md` 审计 A-8（验收复核：成立；关键事实——**EditMode 走 `!Application.isPlaying` 立即完成分支，协程体一行不执行，故既有 EditMode 测试结构性照不到此路径**）

## 任务简报（主控写）

### 缺陷（A-8，已审计确认）

`UIAnimRunner.cs:43-61` 的淡入用 `TaskCompletionSource`+协程：协程被 `StopAllCoroutines`/宿主失活打断（0.15s 淡入窗口内 UIRoot `SetActive(false)`/面板实例销毁）时**永不 SetResult** → `UIManager.cs:193` 的 `await FadeAsync` 永久挂起 → 面板永久 in-flight（in-flight 查重让同面板再也无法 Push）→ **UI 软锁**。`group` 变 fake-null 时协程内抛 `MissingReferenceException` 被协程机吞掉，同样永不完成。

### 修复要求

1. **单一完成保证**：`UIAnimRunner` 的淡入必须**恰好完成一次**（Completed/Cancelled/Failed 三态之一）——任何中断路径（宿主失活/对象销毁/异常）都要落到 TCS 的某个 SetXxx，不得永久悬挂
2. **可测性改造**（关键）：把"协程被打断→TCS 落定"的判定逻辑抽成**纯方法或可注入接缝**（如 `CompleteIfInterrupted(state)` 或 runner 暴露 `AbortActive()`），使 EditMode 无需真协程即可测试该路径；**不许**为测协程把逻辑留在不可测的协程体内
3. **UIManager 消费端**：`await FadeAsync` 处对 Cancelled/Failed 的处理——面板栈状态不得悬挂（in-flight 清除、面板按语义处置：Push 淡入失败→面板仍入栈但标记异常或回滚，**语义自定但必须写进 XML doc 并有测试**）
4. **失败回滚**：沿用 H-01 D3 的 in-flight/资源回滚约定（Push 失败路径 Release 已获取资源）

### 测试要求（修前必红、修后必绿；空洞断言不接受）

- EditMode：通过新接缝模拟"淡入中断"→ TCS 落定为 Cancelled、in-flight 清除、面板栈状态一致、同面板可再次 Push
- EditMode：正常完成路径回归（既有用例不红）
- PlayMode：如可行加一条真协程中断用例（UIRoot 失活）；不可行则报告说明

### 约束

- 只动 `UnityProject/Assets/ZFrame/View/UI/`（UIAnimRunner/UIManager 及新接缝文件）、`UnityProject/Assets/ZFrame/Tests/`、本任务日志
- 不改面板栈对外语义（栈序/事件名）；不动 H-01 已有回滚逻辑的语义
- 基线：EditMode **706 用例** / PlayMode 6——只增不减；UTF-8 无 BOM/LF；架构校验器 exit 0；**不 git commit**
- 自测：`powershell -NoProfile -ExecutionPolicy Bypass -File Tools/run_tests.ps1 -Segments editmode`（或全量），Unity 批处理纪律照 HANDOFF

### 自测

EditMode 全绿 → ValidateFromCommandLine exit 0 → 报告写明"修前必红"的证明（可临时回退修复验证一次并恢复）。

## 施工报告（施工代理写）

施工：general-purpose 施工子代理，2026-10-30。全部改动仅触及简报授权范围（`UnityProject/Assets/ZFrame/View/UI/`、`UnityProject/Assets/ZFrame/Tests/`、本日志），未 git commit，UTF-8 无 BOM / LF（逐文件 cmp 核过）、无尾随空格。

### 1. 实现摘要（单一完成保证 + 可测接缝 + 消费端语义）

**新增类型**（`View/UI/`，文件名=类型名）：
- `UIAnimOutcome.cs`（public enum，19 行）：`Completed / Cancelled / Failed`——Cancelled=宿主（UIRoot）失活/销毁、目标本身完好；Failed=淡入目标对象中途被销毁。
- `FadeSession.cs`（internal，67 行）：一次淡入的**单一完成载体**。`Settle(outcome)`（:42-51）以 `IsSettled` 门闩保证**恰好一次**：第一个 outcome 赢，其后任何 `Settle`/`CompleteIfInterrupted` 均为 no-op 并返回 false（晚到的自然完成、中断清扫、看门狗三方竞争不可能双落定或改写调用方观测）。`CompleteIfInterrupted(targetLost)`（:62-65）即审计要求的**纯判定接缝**：只依据"目标是否已失"选择 Failed/Cancelled 并落定 TCS，所有中断路径汇入此一处。

**`UIAnimRunner.cs` 重写**（63→176 行，对外仍只有 FadeIn/FadeOut，返回类型 `Task`→`Task<UIAnimOutcome>`）：
- 每个运行期淡入建一个 session 进注册表 `_active`（:119-126）。
- 中断路径全部落 TCS：①宿主失活/销毁 → `OnDisable()`（:85-89，Unity 先于 OnDestroy 调用）→ `AbortActive()`（:69-82，清扫注册表逐个 `CompleteIfInterrupted`）；②目标中途销毁 → 协程体**每帧 fake-null 检查**（:148-152），含**末帧写回前的第二次检查**（:159-162，旧代码 `group.alpha = to` 在最后一次 yield 后无检查，正是同款漏洞）；③其余无回调中断（如外来 `StopAllCoroutines`）→ `Update()` 帧戳看门狗（:93-110，活例程每帧盖章，死例程 ≤3 帧内被判中断落定）——三路全汇入 `CompleteIfInterrupted`，**无任何路径可让 awaiter 永久悬挂**。
- **EditMode 可测接缝**：`HoldFadesForTests`（internal，:40-47，生产代码永不置位）——置位后 `Fade` 只注册在途 session、不开协程不写 alpha，即"运行期 fade 在 StartCoroutine 与完成之间"的确切状态；EditMode 结构性不执行协程体（`!Application.isPlaying` 立即完成分支保留，行为不变），故接缝是唯一能在 EditMode 驱动中断的方式。
- EditMode 瞬时路径行为不变（写 alpha + 返回 Completed），保证既有用例零改动。

**`UIManager.cs` 消费端**（语义已写入 `Push` XML doc :102-110 与 `FadeAsync` doc :455-460）：
- `Push` 内 `await FadeAsync`（:203）改收 outcome；**非 Completed → 回滚**：不 commit 直接抛 `InvalidOperationException`（:204-218），由既有 H-01 D3 finally（:221-252）执行 Detach/Destroy/Release——栈、in-flight 标记、PanelOpenedEvent 全部停在"从未 Push 过"的状态，同面板立即可再 Push。选择"回滚+抛"而非"入栈+标记异常"的理由：Failed 时实例已被外部销毁，入栈等于把 fake-null 实例钉在栈上；且既有半路失败（坏 prefab/OnInit 抛）本就抛 InvalidOperationException，口径一致。
- `FadeAsync`（:457-476）返回 outcome，await 不再可能悬挂；`RunFadeOut`（:484-492）的 fire-and-forget 淡出 outcome 有意不观测（无人 await），中断时 session 仍落定，无悬挂任务残留。
- `DestroyObject`（:495-509）加 null 早退（Failed 回滚时实例可能已被外部销毁，语义与原 Destroy(null) no-op 等价，仅显式化）。
- 面板栈对外语义零改动：栈序/事件名/Pop/PopAll/Evict/H-01 回滚约定全部未动。

### 2. 修前必红证明（定向变异，恰好 6 红）

新接缝是新增 API，旧代码无法编译新测试（EditMode 旧代码结构性照不到中断路径，审计已确认），故按 B-R9-08 先例做**定向变异**：在全新管道上以一行变异**行为等价复现旧缺陷**——`FadeSession.Settle` 永不落定 TCS（即旧代码"中断后永不 SetResult"）。证据 `TempLogs/evidence_r1004_editmode_MUTATED_6red.xml`：**total=715，恰好 6 红**，且红的正是新用例中走中断路径的 6 条：

| 变异下红（6） | 变异下绿 |
|---|---|
| `UIAnimRunnerTests.AbortActive_PendingFade_SettlesCancelled_AndUnregisters` | 全部 706 条既有用例 |
| `UIAnimRunnerTests.AbortActive_TargetDestroyedMidFade_SettlesFailed` | `UIAnimRunnerTests.FadeIn_EditMode_CompletesInstantlyWithCompleted` |
| `UIAnimRunnerTests.CompleteIfInterrupted_TargetLost_IsFailed_AliveTarget_IsCancelled` | `UIAnimRunnerTests.FadeOut_NullGroup_CompletesInstantly` |
| `UIAnimRunnerTests.Settle_IsExactlyOnce_FirstOutcomeWins` | `UIPushFadeInterruptTests.Push_FadeCompletes_PanelCommitted_WithAlphaOne` |
| `UIPushFadeInterruptTests.Push_FadeCancelled_RollsBack_ReleasesResources_AllowsRepush` | |
| `UIPushFadeInterruptTests.Push_FadeFailedByInstanceDestroy_RollsBack_AllowsRepush` | |

即：用例对 A-8 行为**有牙**（中断不落定必红），对正常完成路径**不误伤**。PlayMode 真协程用例在旧代码下结构性必红（旧代码中断后 Task 永不完成 → 预算内 `push.IsCompleted` 断言失败），与审计 A-8 的"建议验证方式"一致。变异已恢复（`grep MUTATION` 零命中），恢复后全绿。

### 3. 自测证据（全部实跑，Unity 批处理纪律经 run_tests.ps1：单实例/删锁/无 -quit/results.xml 轮询）

| 段 | 结果 | 证据 |
|---|---|---|
| editmode（修复后） | **715/715 PASS**（基线 706 + 新增 9，只增不减） | `TempLogs/evidence_r1004_editmode_green.xml` |
| playmode（修复后） | **7/7 PASS**（基线 6 + 新增 1） | `TempLogs/evidence_r1004_playmode_green.xml` |
| 变异轮 editmode | **恰好 6 红**（见上） | `TempLogs/evidence_r1004_editmode_MUTATED_6red.xml` |
| arch / config / naming | 三校验器 **exit 0** | `TempLogs/arch_report.txt` / `config_report.txt` / `name_report.txt` |
| native | 未重跑——本任务零 native 文件改动，基线以 R10-03 验收（222 用例全绿）为准 | — |

新增测试清单（9+1）：
- `Tests/Debug/UIAnimRunnerTests.cs`（6）：EditMode 瞬时回归 ×2；纯接缝 `CompleteIfInterrupted`（targetLost→Failed / 存活→Cancelled）、`Settle` 恰好一次（首 outcome 赢）；`AbortActive` 挂起→Cancelled 落定+注册表清空、目标销毁→Failed。
- `Tests/Debug/UIPushFadeInterruptTests.cs`（3）：端到端消费端——Cancelled/Failed 两条中断路径断言 push 落定（不悬挂）+ 回滚（栈空、PanelOpenedEvent 零触发、prefab 引用计数归零、画布无孤儿实例）+ in-flight 清除（同面板立即再 Push 成功）+ 异常消息携带 outcome；正常完成提交回归 ×1。
- `Tests/PlayMode/UIFadeInterruptPlayModeTests.cs`（1）：**真协程中断**——Push 停在真淡入协程内（断言未完成），`UIRoot.SetActive(false)`（审计触发条件）→ push 在实时预算内落定、回滚断言同上、重新激活后同面板可再开。帧循环带实时上限（BootChain 军纪），回归退化为干净断言失败而非批处理挂死。

### 4. 文件清单与如实记录

- 改：`UnityProject/Assets/ZFrame/View/UI/UIAnimRunner.cs`（63→176 行）、`UIManager.cs`（484→518 行）
- 增：`View/UI/UIAnimOutcome.cs`、`View/UI/FadeSession.cs`（.meta 为 Unity 批处理自动生成，随文件提交）
- 增测试：`Tests/Debug/UIAnimRunnerTests.cs`、`Tests/Debug/UIPushFadeInterruptTests.cs`、`Tests/PlayMode/UIFadeInterruptPlayModeTests.cs`（.meta 同上）
- git 状态核过：除上述与本日志外零改动（TestReport.txt 已 gitignore）；未 commit

如实记录（施工过程两轮返工，最终以全绿为准）：
1. PlayMode 用例首版"等 2 秒再中断"是**测试自身 bug**（淡入仅 0.15s，push 自行完成后 precondition 假红）——改为等 1 帧即中断，0.15s 窗口内在途是确定的。
2. 同文件第二轮编译错（deadline 变量误删），修复后终绿。
3. 残留边界（不阻塞）：看门狗仅覆盖 PlayMode（EditMode 无 Update 且瞬时路径不注册 session，无此需求）；`UIManager.Instance` 静态在 PlayMode 用例中被测试自建 manager 覆写，该用例按类名字母序最后执行且收尾 PopAll，无后续消费方。

## 验收清单（主控写）

- [ ] V1 EditMode 全绿（≥706+新增），新用例在修前必红（验收复做一次回退验证）
- [ ] V2 单一完成保证审查：所有中断路径落到 TCS（行号）；接缝可测性设计合理
- [ ] V3 UIManager 消费端语义有测试；in-flight 清除与回滚不破坏 H-01 约定
- [ ] V4 Validate exit 0（自跑）；面板栈对外语义未变（既有 UI 测试零改动通过）
- [ ] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

**验收：PASS（5/5）** —— 验收子代理独立实跑取证，未复用施工方任何一次运行结果；全程未改业务代码（仅临时变异→已恢复）、未 git commit。

- **V1 PASS**：亲跑 `Tools/run_tests.ps1`（Unity 批处理五条纪律内建）——EditMode **715/715**（`TempLogs/runtests_editmode.xml`，验收自跑 2026-10-30 03:20，含 9 条新用例逐条 Passed、706 条既有用例计数核对=715−9）；PlayMode **7/7**（`TempLogs/runtests_playmode.xml`，验收自跑 03:22 与恢复后复跑 03:35 各一次，含真协程中断用例 `Push_FadeInterruptedByRootDeactivation_SettlesWithRollback_AndAllowsRepush`）。**修前必红复做**：读 FadeSession.Settle（`FadeSession.cs:42-51`，IsSettled 门闩+TrySetResult）与接缝设计后，以自选变异独立复现——注释 `UIAnimRunner.OnDisable` 内的 `AbortActive()` 调用（宿主失活路径失去落定点；此时看门狗 `Update` 亦停转，无其他路径可救）→ 复跑 PlayMode 段 **恰好 1 红** 且红的正是真协程中断用例（断言消息 "the interrupted fade must settle within the budget (pre-fix: hang forever)"，其余 6 用例不误伤）→ 恢复后 `grep ACCEPTANCE-MUTATION` 全仓 0 命中、git diff 无残留、复跑 PlayMode 复绿。证据：`TempLogs/evidence_r1004_playmode_MUTATED_acceptance_1red.xml`（与施工方 EditMode 变异 6 红证据 `evidence_r1004_editmode_MUTATED_6red.xml` 互补，两条变异覆盖两条不同中断路径）。
- **V2 PASS**：三路中断全落 TCS，独立核对行号——①宿主失活/销毁：`UIAnimRunner.OnDisable`（UIAnimRunner.cs:85-91，失活与销毁都会先调）→ `AbortActive`（:69-83）→ 逐 session `CompleteIfInterrupted`（:76）；②目标中途销毁：协程体逐帧 fake-null 检查（:145-152）+ **末帧写回前第二次检查**（:159-163，堵住旧代码最后一行 `alpha = to` 无检查的同款漏洞）→ `EndActive(Failed)`（:170-174）；③其余中断（外来 StopAllCoroutines）：`Update` 帧戳看门狗（:93-110，活例程 :155 每帧盖章，死例程 ≤3 帧被判中断 :104-108）。`Settle` 恰一次独立判断正确：所有调用方均在 Unity 主线程回调内（无并发 Settle），`IsSettled` 门闩先置位再 `TrySetResult`（TrySetResult 本身天然一次），晚到完成/重复中断均为 no-op——`Settle_IsExactlyOnce_FirstOutcomeWins` 用例证实首个 outcome 赢、不可改写。
- **V3 PASS**：`UIManager.Push` await（UIManager.cs:203）收 outcome，非 Completed 不 commit 直接抛 `InvalidOperationException`（:204-216），由**未改动的** H-01 D3 finally（:228-261，git diff 证实该块零改动）执行回滚：in-flight 双标记清除（:230-234）→ presenter Detach（:244）→ 实例销毁（:251-254）→ prefab 引用 Release（:256-259）；`PanelOpenedEvent` 仅 commit 后才触发（:265）。XML doc（Push :102-110 / FadeAsync :454-460）逐条语义与实现及测试断言一致（栈空/事件零触发/引用计数归零/画布无孤儿/异常消息携带 outcome/同面板立即可再 Push——`UIPushFadeInterruptTests` 3 条 + PlayMode 1 条全覆盖）。`DestroyObject` null 早退（:501-507）与 Failed 回滚语义等价显式化；`RunFadeOut` outcome 有意不观测但 session 仍落定，无悬挂任务残留。
- **V4 PASS**：三校验器验收自跑全 exit 0——arch（`TempLogs/arch_report.txt`）/ config（`TempLogs/config_report.txt`）/ naming（`TempLogs/name_report.txt`）。面板栈对外语义未变：`git status` 核对改动仅 `UIAnimRunner.cs`+`UIManager.cs` 两个源文件与新增文件，**任何既有测试文件零改动**；706 条既有用例（含 UIStackTests 11 / UILifecycleTests 6 / UIPushRobustnessTests 2 / UIHierarchyTests 7 等 UI 全家）在验收自跑中全绿。
- **V5 PASS**：8 个触及文件逐个核过——UTF-8 无 BOM（首 3 字节非 EF BB BF）、LF（CR=0）、无尾随空格；`wc -l` 与施工报告一致（UIAnimRunner 63→176、UIManager 484→518）；新文件 .meta 齐全；internal 接缝经 `View/AssemblyInfo.cs:13` InternalsVisibleTo 合法可达。施工报告四节齐全，定向变异 6 红证明经 XML 逐条核实（失败的正是 6 条中断路径用例，失败消息与"Settle 永不落定"变异语义自洽，4.7s 快速失败无假挂）、PlayMode 两轮返工如实记录（第 1 轮测试自身 bug、第 2 轮编译错）。

**验收残留观察（不阻塞，供后续任务参考）**：①看门狗仅 PlayMode 有效（EditMode 无 Update 且瞬时路径不注册 session）——施工已如实披露，EditMode 侧由测试接缝覆盖，可接受；②PlayMode 用例覆写 `UIManager.Instance` 静态——按类名字母序最后执行且收尾 PopAll，施工已披露，可接受；③native 段未重跑——本任务零 native 改动，基线以 R10-03 验收（222 用例全绿）为准，符合简报范围约定。
