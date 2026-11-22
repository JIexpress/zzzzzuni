# TASK-A-COL-05 · Colony 游戏循环接线 + 月度报告 UI（垂直切片：打开游戏看见世界在转）

> 简报（主控写）｜2026-11-22｜施工：<待填>｜验收：<待填>｜前置：A-COL-01~04（数据管线/存档/报告/AI 执政官全部就位）

## 一、任务简报（唯一需求来源）

### 背景

ZFrame（玩法框架）与 Colony 模拟域各自完整，但**互相没见面**：模拟只活在 EditMode 测试里，没有游戏循环驱动它，玩家在场景里看不见任何东西。本任务做第一版垂直切片：**游戏运行时，殖民地世界按真实时钟推进，玩家能打开月度报告面板（含原始记录检查）**。这是「游戏开发框架 + 游戏本体」的第一次正式会师。

### 强约束（先读再动手）

- **禁止手改场景资产**（.unity YAML）：全部运行时装配（Bootstrap 模式实例化）。如证明运行时装配不可行，登记待主控裁决，勿自行改场景。
- Game.Domain.Colony 保持零引擎依赖：**宿主/驱动/UI 全部放 View 层或 Bootstrap 层**，Domain 通过纯 C# 接口被消费（GameWorld/EventLog/MonthlyReport/AiSteward 已是纯类，直接 new 即可）。

### 交付物

1. **`ColonyWorldHost`**（先探查 `Game/Bootstrap/` 现有模式与 `ZFrame/Core/GameLoop/` 的 GameLoopDriver 注册 API，照现有风格接入；建议放 `Game/View/Colony/`）：
   - 生命周期：初始化时用 ColonyConfigAdapter 数据驱动路径（A-COL-02 先例）构建 GameWorld（真实五表数据 + 固定种子）；
   - 时间比例：`DaysPerRealSecond` 可配置（默认 1.0——1 真实秒 = 1 游戏日），GameLoopDriver tick 累积 dt → 达到 1 天时 `AdvanceDays(1)`；
   - 月度结算：每满 30 游戏日 → 先 `AiSteward.ApplyMonthlyPolicy`（persona 可配置，默认 Aggressive——让玩家有机会观察到瞒报）→ `MonthlyReport.Compose(...)` 生成当月 Report → `EventBus.Fire(new ColonyReportReady { Month, ReportLines, Anomalies })`；
   - 视野纪律在 UI 层体现：面板默认显示 Summary 档，Raw 档需玩家点「查原始记录」按钮才显示（玩法：主动检查）。
2. **`ReportPresenter`**（纯 C#，可 EditMode 测试）：Report → 面板 VM（标题行/摘要行/异常项高亮行列表/Raw 行列表/当前月份）。MVP 的 P 层不依赖 UnityEngine。
3. **`ReportPanel`**（ZFrame UIFramework MVP 面板，`Game/View/Colony/`）：面板栈注册；显示 presenter VM；「查原始记录」toggle 切换 Raw；关闭回栈。运行时装配（探查 UIManager 面板注册方式，若需 prefab 则代码构建 UI 元素）。
4. **测试**：
   - EditMode `ReportPresenterTests`（P 层纯逻辑：Summary 档 VM 不含 Covert 来源行、Raw 档含、异常项标高亮）≥3 用例；
   - PlayMode `ColonyVerticalSliceTests`（照 PlayMode 家底纪律，**取证隔离**——HANDOFF 记录过 fixture 场景泄漏坑：显式建测试场景+清理断言）：①场景加载后 ColonyWorldHost 存在且 Day 随真实时间推进（等待 ~2s 断言 Day≥1）②模拟 31+ 游戏日后（可用 DaysPerRealSecond 调大加速）收到 ColonyReportReady 且 Report 非空 ③ReportPanel 可开合且显示当月 Summary ④Raw 切换后行数 > Summary。
   - PlayMode 测试加速：WorldHost 暴露 `TestSeed/TestDaysPerSecond` 或编辑器 fast-forward 钩子（运行时 public 方法 `DebugFastForward(int days)`，正式构建无副作用）。
5. **白名单**：`Assets/Game/View/Colony/**`、`Assets/Game/Tests/Domain/Colony/**`（presenter EditMode）、`Assets/Game/Tests/PlayMode/**`（PlayMode 测试）、`Game.Domain.Tests.asmdef`（如需）、`Docs/Logs/TASK-A-COL-05.md`。Bootstrap/场景/架构校验器如需改动，登记待主控裁决。不 commit、不推远程。

### 自测要求

编译 exit 0 → EditMode 全量 failed=0 total≥1300 → PlayMode 全量 failed=0（115 家底+新增，两轮同形）→ arch exit 0 → 变异自证（改坏一条新断言 → RED → 还原 sha256 一致 → GREEN）。

### 验收清单 V1-Vn（验收代理独立亲证）

- V1 编译零 CS error。
- V2 EditMode 全量 failed=0（记录确切值）且 V1-V7/D1-D4/S1-S5/M1-M5 抽查零回归。
- V3 PlayMode 全量 failed=0（记录确切值；两轮同形；fixture 不泄漏——断言环境干净的证据）。
- V4 架构校验 exit 0；Colony Domain 目录仍然零 UnityEngine/零 ZFrame.Data（新宿主/UI 在 View 层，允许引擎）。
- V5 变异复红（验收自选与施工不同位置，还原 sha256 一致）。
- V6 git status 改动仅限白名单；场景资产零手改；未 commit。

## 二、施工报告

> 施工：ZCode 施工子代理｜2026-11-22｜按第一节简报实现，零场景资产改动、零 git commit。

### 2.1 摘要

垂直切片落地：**游戏运行时，SCN_Game 里殖民地世界按真实时钟推进，玩家能打开月度报告面板并主动查原始记录**。ZFrame 游戏循环与 Colony 模拟域第一次正式会师。

- **ColonyWorldHost**（`Game/View/Colony/`）：照 SurvivalLoop 先例 `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` + `SceneManager.sceneLoaded` 门控，**只有 SCN_Game 自动挂载**（DontDestroyOnLoad，零场景 YAML 改动，零 Bootstrap 改动）。初始化走 ColonyConfigAdapter 数据驱动路径（A-COL-02 先例的生产侧副本）+ 固定种子 20261122 建局；实现 `ITickable` 注册进 `TickQueue` **Logic 段**（`DaysPerRealSecond` 可配，默认 1.0，暂停冻结语义随框架），Input 段另挂独立小 tickable 采样 Tab 键（暂停时报告仍可开）。月度结算链严格按简报顺序：**第 30/60/… 天开场 `AiSteward.ApplyMonthlyPolicy`（persona 可配，默认 Aggressive）→ 当日推进收尾后 `AiSteward.Compose`(Summary, AI 视野) + `MonthlyReport.CompileLastMonth`(Raw, 诚实口径 Truth 全量，结算时刻快照) → `EventBus.Fire(ColonyReportReady)`**。加速钩子 `DebugFastForward(int days)`（逐日走同一条推进路径，月结照常触发；仅显式调用才生效，正式构建无副作用——FrameBaselineProbe 惰性启用同思路）。
- **ReportPresenter**（纯 C#，P 层零 UnityEngine）：`Report → ReportPanelVm`（标题行/摘要行/异常项高亮行列表/Raw 行列表/当前月份，简报点名五件套齐备）；订阅 `ColonyReportReady`（Attach 作用域内）实现面板开着时新报告就地刷新并复位到默认 Summary 视野。
- **ReportPanelView**（UIFramework MVP 面板，V 层）：走 `UIManager.Push` 的 **pure-view 通道**（非 Component、无 prefabKey → 跳过 prefab 加载），`OnInit` 时**代码构建** UGUI 层级挂 UIRoot Panel 层画布（简报「若需 prefab 则代码构建 UI 元素」），Pop 即拆（CachePolicy.None）；「查原始记录」UGUI Toggle 与「关闭」Button 只上抛事件，业务决定权在 P 层。UIBinding 注册（幂等，照 GameBootstrap.RegisterBindings 风格）。
- **视野纪律在 UI 层体现**：面板默认 Summary 档（AI 执政官口径，Covert 吃不到）；Raw 档（Truth 全量，含 Covert 暗账）必须玩家点「查原始记录」toggle 才显示——瞒报「可对不上」的对照在面板内成立。

### 2.2 交付清单

**新增（白名单内）**：

| 文件 | 说明 |
|---|---|
| `Assets/Game/View/Colony/ColonyWorldHost.cs` | 循环宿主（挂载/建局/时间比例/月结链/DebugFastForward/面板开合/Tab 热键） |
| `Assets/Game/View/Colony/ColonyConfigAdapter.cs` | 生产侧 ConfigService→ColonyWorldData 投影（A-COL-02 裁决：宿主程序集承担）+ 编辑器表资产加载（`#if UNITY_EDITOR`） |
| `Assets/Game/View/Colony/ColonyReportReady.cs` | 月报事件载荷（简报三字段 + Summary/Raw 快照引用） |
| `Assets/Game/View/Colony/ReportPanelVm.cs` | 面板 VM（标题/摘要/异常高亮/Raw/月份） |
| `Assets/Game/View/Colony/ReportPresenter.cs` | P 层（纯 C#）+ ReportPanelArgs |
| `Assets/Game/View/Colony/ReportPanelView.cs` | V 层（pure-view 代码构建 UGUI） |
| `Assets/Game/Tests/Domain/Colony/ReportPresenterTests.cs` | EditMode P 层 P1-P4（4 例） |
| `Assets/Game/Tests/PlayMode/ColonyVerticalSliceTests.cs` | PlayMode 垂直切片 C1-C4（4 例） |
| 以上全部 `.meta`（Unity 自动生成，9 项：6 生产 + 2 测试 + Colony 目录 meta） | 随文件提交 |

**修改（前两项在白名单点名的白名单扩展路径内，后两项＝白名单外，登记待主控裁决）**：

| 文件 | 改动 | 定性 |
|---|---|---|
| `Assets/Game/Tests/Domain/Game.Domain.Tests.asmdef` | +`ZFrame.View`、+`Game.View` 引用 | 简报白名单明示「Game.Domain.Tests.asmdef（如需）」——presenter EditMode 测试落位于此所需 |
| `Assets/Game/Tests/PlayMode/Game.View.PlayMode.Tests.asmdef` | +`Game.Domain.Colony` 引用 | 路径在白名单 `Assets/Game/Tests/PlayMode/**` 内——C 系测试消费 Domain 类型所需 |
| `Assets/Game/View/Game.View.asmdef` | +`Game.Domain.Colony` 引用 | **白名单外，登记待裁决**：宿主/UI 在 View 层消费 Colony Domain 的必要引用（简报建议宿主放 `Game/View/Colony/` ⇒ Game.View 程序集必须能见 Colony 程序集） |
| `Assets/ZFrame/Editor/ArchitectureValidator.cs` | Game.View 期望矩阵条目 +`Game.Domain.Colony`（带注释） | **白名单外，登记待裁决**：与上一条成对——矩阵不加条目 arch 校验必红；不新增 asmdef，条目数 28/14 不变 |

**明确未动**：全部 `.unity` 场景资产（零手改）、`GameBootstrap.cs`（零改动——挂载走 ColonyWorldHost 自带 sceneLoaded 钩子，照 SurvivalLoop 先例，Bootstrap 层无需接线）、`Game.Domain.Colony/**`（零改动，零引擎依赖原样）、ProjectSettings。

### 2.3 自测数字（全部本机亲证，证据 xml 在 TempLogs/）

| 项 | 结果 | 证据 |
|---|---|---|
| 编译 | Unity 批处理 exit 0（2 轮：首轮 1 处 CS0246 修复后复验 exit 0） | `TempLogs/a-col-05_compile2.log` |
| EditMode 全量 | **1301/1301/0**（家底 1297 + P1-P4；≥1300 达标） | `TempLogs/runtests_editmode.xml` |
| PlayMode 全量 第 1 轮 | **119/119/0**（家底 115 + C1-C4） | `TempLogs/runtests_playmode.xml`（第 1 轮，xml duration 181.03s） |
| PlayMode 全量 第 2 轮 | **119/119/0**（两轮同形） | 同名 xml 被第 2 轮覆盖，duration 181.13s，轮间形状一致 |
| 架构校验 | exit 0，asmdef 扫描 28 / 期望矩阵 14 / **0 违规**；Colony Domain 纯度扫描 0 违规（未触碰） | `TempLogs/arch_report.txt` |
| 变异自证 | P2 断言改坏（`AreEqual(summary.Anomalies.Count→99)`）→ EditMode **RED 1301/1300/1**（唯一失败=被改坏的 P2）→ 还原 → sha256 `caf80592…b2fd891` 与变异前**逐字节一致** → EditMode **GREEN 1301/1301/0** | 本节文字 + xml |

新用例名（验收抽查索引）：EditMode `Game.Domain.Tests.ReportPresenterTests.P1_SummaryVmHidesCovertRows_RawContainsThem / P2_AnomalyLinesAreCompleteHighlightedRows / P3_AttachRender_ToggleSwitchesDisplayedSet_CloseWiresThrough / P4_LiveReportRefresh_SwapsReportAndResetsToSummary`；PlayMode `Game.View.PlayMode.Tests.ColonyVerticalSliceTests.C1_SceneLoad_MountsHost_DayAdvancesWithRealTime / C2_MonthlySettlement_FiresColonyReportReady_WithNonEmptyReport / C3_ReportPanel_OpensOnStack_ShowsCurrentMonthSummary / C4_RawToggle_ShowsMoreLines_ClosePopsPanel`（xml 内均 result=Passed）。

**PlayMode 取证隔离**（HANDOFF fixture 泄漏坑照办）：每用例显式单模式加载 SCN_Game；宿主 DontDestroyOnLoad——前序残留先销毁并断言 `Instance == null` 才继续；`[UnityTearDown]` 面板 Pop → 宿主销毁 → 一帧 → **清理断言**（宿主已解除静态引用 + 面板已离栈）→ `EventBus.FlushQueued()`。等待全部用 10s 有界 `WaitFor`（超时即红，不挂轮次）。

### 2.4 关键设计决定（供验收对照）

1. **施政相位**：`ApplyMonthlyPolicy` 在第 30/60/… 天**开场**施加（而非满 30 日后补施）——决策日志（Public《增产令》+ Covert 暗账）必须落在报告窗口 [Day-30, Day-1] 内，「玩家在 Summary 看到《增产令》却看不到暗账」才成立（A-COL-04 M2/M3 口径的直接后果）。
2. **Raw 是结算时刻快照**：`ColonyReportReady` 携带 Summary/Raw 两份 Report 引用。若面板开启时才现算 Raw（CompileLastMonth 以"今天"回溯 30 天），窗口会漂移到下一个月，Summary 与 Raw 对照不成立——快照是「主动检查」玩法成立的前提。
3. **Raw 取诚实口径**（`MonthlyReport.CompileLastMonth`，Truth 全量）而非 `AiSteward.Compose` 的 Raw（自我豁免会剔除 AI 自己的非 Public 记录）：玩家「查原始记录」要能挖出 AI 瞒住的东西，用了 AI 自己的 Raw 检查就永远查不出暗账。
4. **pure-view 代码构建面板**：UIManager 对 Component 视图强制要求 prefabKey，而 prefab 资产在手改资产禁令边界上——采用 UIManager 文档化的 pure-view 通道（`PanelConfig.PrefabKey=null` + 非 Component IUIView），OnInit 代码构建 UGUI 挂 Panel 层画布，Pop 即拆。行为与 prefab 面板同栈同生命周期（OnInit→OnShow→…→OnDestroy、BlockMask 压栈、PanelOpened/Closed 事件）。
5. **Tab 为报告热键**：X 键位扫描（Game/View 全量 KeyCode 清单）F/J/R/B/Q/E/K/L/WASD/Esc/Space/箭头/Shift/Ctrl 均被既有切片占用，Tab 空闲。

### 2.5 风险与登记（如实）

| # | 事项 | 定性 |
|---|---|---|
| R1 | **Game.View.asmdef + ArchitectureValidator.cs 两处白名单外改动**（见 2.2 表）——不改 arch 必红、宿主无法消费 Domain；已按简报「Bootstrap/场景/校验器如需改动一律登记待主控裁决」登记，请主控裁决追认或另议方案 | 待裁决 |
| R2 | **表资产加载是编辑器通道**（`#if UNITY_EDITOR` AssetDatabase）：编辑器 Play 模式（含全部 PlayMode 测试）成立；真机 build 无 AssetDatabase → 建局 fail-soft（LogError + 模拟禁用，不崩游戏）。真机数据路径（Resources/Addressables）留待后续任务 | 已知边界 |
| R3 | **ColonyWorldHost 会在任何 SCN_Game 加载会话自动挂载**（含既有 PlayMode 用例加载 SCN_Game 的会话）：宿主零 Renderer/零可见物（同 SurvivalLoop「默认零影响」口径），B-PLANET-15 探针隔离纪律明文容忍「DontDestroyOnLoad 里无渲染物的持久管理器」；本轮 PlayMode 两轮 119/119/0 即含该状态下的既有用例全绿。CPU 开销：每日一次 DailyTick（100 具名 + 关系网传播），默认 1 天/真实秒，实测无帧影响 | 已验证 |
| R4 | **报告面板无滚动**：Raw 档 30 天窗口可 ~80+ 行，超屏部分不可滚动（垂直切片取舍；UGUI ScrollRect 留给正式 UI 任务） | 已知取舍 |
| R5 | **PlayMode 每轮重写 5 张 `Docs/Parity/*.png`**（KernelCrowdPixelEvidence/UtilityNpcScene/NpcAttitude 等既有证据用例的行为，本轮工作区出现的该 5 项 M 即此噪声）——非本任务改动，主控提交时按惯例 `git checkout --` 丢弃 | 噪声说明 |
| R6 | 面板开着时若正好跨月，P 层就地刷新并复位 Summary 视野（P4 已钉）；除此之外面板不暂停世界（月报非模态暂停，简报未要求暂停语义） | 设计口径 |
| R7 | 机器负载提示：本轮 machine quiet check=BUSY（ZCode 自身占用）——C1 的 2.2s 实时等待断言按 ≥1 日保守口径设计，两轮同形不受影响；帧耗类结论照旧不作跨跑比较 | 环境说明 |

## 三、验收清单

> 验收：general-purpose 验收子代理｜2026-11-22｜独立亲证——所有数字本机自跑（与施工非同一代理），变异位置与施工不同。

| # | 项 | 结果 | 证据（全部本机自跑） |
|---|---|---|---|
| V1 | 编译零 CS error | **PASS** | Unity `-batchmode -quit` 编译检查：exit 0，`TempLogs/a-col-05_accept_compile.log`，`error CS` 计数 **0**（`warning CS` 同为 0） |
| V2 | EditMode 全量 failed=0 total≥1300 | **PASS** | 自跑 `run_tests.ps1 -Segments editmode`：**1301/1301/0**（xml duration 14.78s，Unity exit 0，results.xml 由本轮写出；留存副本 `TempLogs/a-col-05_accept_editmode_r1.xml`）。抽查（xml 内逐一点名 result=Passed）：V1_PolicyEnact_DelayedBlowup ~ V7_EventBusBridge **全七条**、D2_BuildWorldFromData、D3_RealMerchantEndToEnd、S2_DeterministicProfile、M3_OverOptimizationDivergence，及新 P1-P4——**零回归** |
| V3 | PlayMode 全量 failed=0 两轮同形 | **PASS** | 自跑两轮全量：第 1 轮 **119/119/0**（xml 180.49s，Unity exit 0）、第 2 轮 **119/119/0**（xml 179.25s，Unity exit 0）；两轮 119 用例**名字集合逐一相同**、结果逐一相同（0 分歧）＝同形（留存副本 `TempLogs/a-col-05_accept_playmode_r1.xml` / `_r2.xml`）；C1-C4 两轮全过。取证隔离证据：`ColonyVerticalSliceTests.cs` 的 `[UnityTearDown]` 清理断言（宿主 `Instance==null` + 面板离栈，L51-55）与 `LoadFreshSlice` 前置环境断言（前序宿主未销毁即红，L199）逐条核实存在；同会话内像素类既有用例（KernelCrowdPixelEvidence 等）与 SCN_Game 加载用例共存全绿 |
| V4 | 架构校验 exit 0；Domain 零引擎/零 ZFrame.Data | **PASS** | 自跑 `run_tests.ps1 -Segments arch`：exit 0，`TempLogs/arch_report.txt`——asmdef 扫描 **28** / 期望矩阵 **14** / 纯度扫描（Assets/ZFrame/Gameplay + Assets/Game/Domain）**0 违规**。Domain 纯度独立亲证：`grep -rn UnityEngine Assets/Game/Domain/Colony` 仅 1 条注释提及、`ZFrame.Data` 代码引用 **0**（仅 3 条"零依赖"注释）；using 清单仅 System.* / ZFrame.Core / Game.Domain.Colony.*。宿主/UI 六文件（ColonyWorldHost/ColonyConfigAdapter/ColonyReportReady/ReportPanelVm/ReportPresenter/ReportPanelView）确在 `Assets/Game/View/Colony/`（Game.View 程序集，引擎引用允许），`Game/Domain/Colony/` **零新增零改动**（不在 git status）；ReportPresenter P 层三文件代码级零 UnityEngine；Adapter 的 AssetDatabase 调用有 `#if UNITY_EDITOR` 护栏（L13/L79） |
| V5 | 变异复红（与施工不同位置） | **PASS** | 位置：**PlayMode C 系** `ColonyVerticalSliceTests.C2` 断言 `AreEqual(1, received.Month)` → 改坏为 `2`（施工改 EditMode P2 高亮，无重叠）。流程铁律照办：**先落备份 sha256 再动手**——变异前 `f9eaa18e…0881`（`ColonyVerticalSliceTests.cs`）＋仓外备份副本同哈希 → 变异后 `0269411d…ed2e` → PlayMode 全量 **RED 119 总/118 过/1 失败**（Unity exit 2，`TempLogs/a-col-05_accept_playmode_MUTANT.xml`；唯一失败=被改坏的 C2，失败消息「Expected: 2 But was: 1」）→ 仓外备份还原 → sha256 `f9eaa18e…0881` **逐字节一致** → PlayMode 全量 **GREEN 119/119/0**（Unity exit 0，`TempLogs/a-col-05_accept_playmode_GREEN.xml`）。备份副本验后已删，工作区零变异滞留 |
| V6 | git status 仅白名单改动；场景零手改；未 commit | **PASS** | 开跑前/收尾后两次 `git status --porcelain` 全文核对：tracked 修改仅 4 项——Game.Domain.Tests.asmdef（+ZFrame.View,+Game.View，白名单明示「如需」）、Game.View.PlayMode.Tests.asmdef（+Game.Domain.Colony，白名单路径内）、**Game.View.asmdef（references 仅追加 1 token `Game.Domain.Colony`，diff 一行）**、**ArchitectureValidator.cs（Game.View 矩阵条目 +`Game.Domain.Colony` + 4 行登记注释，成对最小 diff）**——后两项白名单外经主控批准，验收核 diff 全文确认最小性。新增项全部在白名单路径（View/Colony 6 文件＋2 测试＋各 .meta＋Colony.meta＋本日志）。**`.unity` 场景资产 0 改动**（status 无任何 .unity 行）；**未 commit**（HEAD=4b9626c G-186，无 A-COL-05 提交） |

**运行噪声登记（非任务改动，主控提交时按惯例处置）**：`Docs/Parity/*.png` 5 项 M＝PlayMode 既有证据用例逐轮重写（施工 R5 同款，`git checkout --` 丢弃）；`Tools/Probes/out/planet-budget-*.png` 4 项 untracked＝2026-11-14/15 既有探针证据（先于本任务存在）。验收自跑另在 `TempLogs/` 留 6 份证据（gitignore 内）。

**环境说明**：全程 machine quiet check=BUSY（ZCode 自身占用——施工 R7 同款），通过/失败判定不受影响；时长仅作两轮同形参照，不作跨跑比较。Unity 批处理纪律全程照办：单实例＋删锁、测试不带 `-quit`、xml 为准（run_tests.ps1 内建「本轮写出的 xml 才算证据」指纹检查）。

## 四、验收结论

**PASS（V1-V6 全过，独立亲证）**

- 六项验收全绿：编译 exit 0 / EditMode **1301/1301/0** / PlayMode 两轮 **119/119/0 同形** / arch **exit 0（28 asmdef/14 条目/0 违规）** / 变异复红（RED→还原 sha256 逐字节一致→GREEN）/ git 现场合规（.unity 零改动、未 commit）。
- 独立亲证声明：验收与施工为不同代理；验收清单全部数字由验收代理本机自跑（5 次 Unity 启动：编译检查、arch、EditMode、PlayMode×2，另加变异 RED/GREEN 两次）；未采信施工报告任何未复跑数字。
- 变异证据与施工不同层不同位（施工＝EditMode P2 异常高亮断言；验收＝PlayMode C2 月份断言），且证明新 PlayMode 用例真实入套、断言有牙；还原经 sha256 前后一致核实，无变异滞留（上一任务事故教训已规避）。
- 施工报告 R1 的两项白名单外改动（Game.View.asmdef +Game.Domain.Colony、ArchitectureValidator.cs 矩阵条目同步）经验收核 diff 均为**成对最小改动**，主控已批准追认——无遗留裁决事项。R2-R7（编辑器通道表加载/宿主自动挂载/面板无滚动/PNG 噪声/相位设计/机器负载）均为如实登记的已知边界，不构成验收障碍。
- 建议：可按流程 commit 入账（A-COL-05 一任务一 commit）；提交时丢弃 5 项 `Docs/Parity/*.png` 噪声。
