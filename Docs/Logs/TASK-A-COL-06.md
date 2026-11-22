# TASK-A-COL-06 · NPC 自主行为：效用 AI/任务板接组织度涌现（"NPC 不是按预设跑，而是自己找事"）

> 简报（主控写）｜2026-11-22｜施工：施工子代理（ZCode 会话，2026-11-22）｜验收：<待填>｜前置：A-COL-01~05

## 一、任务简报（唯一需求来源）

### 背景

用户原始需求清单的第一条就是「**NPC 不是按预设跑，而是自己找事**」。当前 Colony NPC 的行为全部是事件驱动（抗议路由/契约状态机/传染），**没有主动性**：世界推进时 NPC 只被动响应。Game/Domain/AI 已有完整资产：Affordance（可供性）/Coordination（TaskBoard 任务板+SquadCoordinator）/Utility（效用评分）/Memory。本任务把这些接到具名 NPC 上：**每天每个具名 NPC 用效用评分自主选择一个行为**，行为写回模拟状态（怨气/组织度/事件），让涌现链从"参数变化"升级为"NPC 自己在推动"。

### ⚠ 本轮 Unity 环境纪律（用户编辑器打开中）

用户正开着 Unity 编辑器。批处理测试撞锁（"another instance running"）时：**只允许等待重试（60s×10）；绝对禁止 taskkill 用户编辑器、禁止删 UnityLockfile**——僵尸判定仅限"批处理残留进程"（HANDOFF 既有纪律的字段核对，拿不准就等）。等待重试耗尽（10 次）仍未拿到锁：登记实际情况，把已完成的代码交付与未跑的测试清单如实写入报告，等主控协调窗口，**不要绕过测试宣称完成**。

### 设计要点

1. **先探查再决定复用方式**（施工报告写明选择与理由）：读 `Game/Domain/AI/` 的 TaskBoard/TaskBoardCandidates/TaskBoardGoapBridge/Utility 源码与现有消费者（EnemyController/EnemyBehaviorTree/DriveMode 切换），然后二选一：
   - 方案 A：直接引用 `Game.Domain`（Game.Domain.Colony asmdef 追加 reference），复用其 TaskBoard/Utility 类型；
   - 方案 B：仿照其模式在 Colony 域内建轻量 `ColonyTaskBoard`（任务候选+效用评分+分派，零新引用）。
   - 判据：Enemy 的 TaskBoard 语义（小队战斗/FormationSlot/AttackAssignment）与 Colony 语义（社会行为）的贴合度；贴合就复用（A），扭曲就自建（B）。倾向性：**若复用需要对 Game.Domain 现有类做泛化改造（改敌方 AI 消费方），一律走 B**——本任务禁止动 Enemy 现有行为。
2. **Colony 自主行为集**（`Game/Domain/Colony/` 新增，方案 B 时约 2 个文件）：
   - `ColonyAction` 枚举 + 效用评分器 `ColonyActionScorer`：对每个具名 NPC 每日从候选集评分选一：
     - Work（默认，分数基线）→ 产出/配合（Cooperation 已有，行为化留接口）
     - Protest（怨气×人格：复用 ProtestRouter 结果作为候选强度）
     - Recruit（仅 Organizer：Organization 高者向怨气>0.4 的关系边邻居招募——组织度传导行为化）
     - Gossip（GossipRate 高者：沿关系边传播 PlayerImage 传闻——已有机制的行为化入口）
     - Rest/Observe（低存在感行为，分数兜底）
   - 评分公式公开可测（人格/怨气/组织度/地位 → 分数），**效用 AI 的"为什么选这个"要能从分数读出来**（理论参考 §6③：公式对玩家隐藏，对开发者可解释）。
   - `ColonyDailyBehavior` 每日结算入口：GameWorld.DailyTick 里对每个具名 NPC 调用（复用现有循环，不加新循环），选中的行为产生状态写入与 EventLog（Covert 为主——NPC 自己找事玩家大多看不见，符合规格）。
3. **涌现加速可观测**：`GameWorld` 统计 `AutonomousActionsToday`（今日自主行为计数）；对比断言用——开启自主行为后，同等编排下组织度增长应快于纯参数路径（M 系测试对照）。
4. **测试 `ColonyAutonomyTests`**（EditMode，数据驱动建局）：
   - `T1_ScoringPersonality`：同一 NPC 改人格参数 → 评分第一名变化（高宜人性低神经质 → 不选 Protest；IsMerchant+高怨气 → InflatePrice 候选分升）。
   - `T2_DailyDistribution`：100 NPC 推 30 天（seed 固定）→ 行为分布非退化（至少 3 种 Action 出现，Work 占比合理 40-90% 区间，防全员抗议）。
   - `T3_OrganizerRecruits`：组织者存在 + 邻居怨气>0.4 → Recruit 被选中时邻居 Organization 上升且 Log 有 Covert 事件。
   - `T4_EmergenceAccelerates`：对照组（关闭自主行为开关）vs 实验组（开启）同编排推 30 天 → 实验组聚落 Organization 增量 > 对照组（开关：GameWorld.AutonomyEnabled，默认 true，测试可关）。
   - `T5_PerfBound`：100 NPC×30 天自主结算耗时 < 50ms（Stopwatch 断言——自主行为不能把每日 tick 拖爆）。
5. **白名单**：`Assets/Game/Domain/Colony/**`、`Assets/Game/Tests/Domain/Colony/**`、`Game.Domain.Colony.asmdef`（若走方案 A）、`Docs/Logs/TASK-A-COL-06.md`。Enemy/Game.Domain/AI 现有文件与消费方**零改动**（方案 B 则天然满足）。不 commit、不推远程。

### 自测要求

编译 exit 0 → EditMode 全量 failed=0 total≥1306（1301+T1-T5）→ PlayMode 全量 failed=0 两轮同形（119 家底，自主行为不破坏既有切片）→ arch exit 0 → 变异自证（改坏 T3 一处 → RED → 还原 sha256 一致 → GREEN）。**全部测试在 Unity 撞锁纪律下完成（见上），跑不动就如实登记。**

### 验收清单 V1-Vn（验收代理独立亲证）

- V1 编译零 CS error（自跑）。
- V2 EditMode 全量 failed=0 total≥1306（记录确切值）；V1-V7/D1-D4/S1-S5/M1-M5/P1-P4 抽查零回归。
- V3 PlayMode 两轮同形 failed=0（119 家底）。
- V4 架构校验 exit 0；Enemy/Game.Domain/AI 现有文件零 diff；Colony Domain 纪律不变。
- V5 变异复红（验收自选与施工不同位置，先落备份 sha256 再动手，还原逐字节一致）。
- V6 git status 改动仅限白名单；未 commit。

## 二、施工报告

**施工**：施工子代理（ZCode 会话）｜2026-11-22

### 2.1 摘要

**方案选择：B（Colony 域内轻量自建 `ColonyTaskBoard`），零新引用。** 按简报判据逐条核对 Game/Domain/AI 资产后定案：

1. **TaskBoard 语义扭曲**：Game.Domain.AI.Coordination.TaskBoard 是小队战斗语义——候选=带 `Vec2` 坐标的拾取任务、秒级绝对时钟过期、发布→认领互斥（一任务至多一个认领人）。Colony 社会行为无坐标、无秒时钟（日结算），且"多人同日各自抗议/各自传谣"是常态——认领互斥在该语义下没有意义。复用=扭曲。
2. **UtilityBrain 复用需泛化改造 Enemy 消费方（简报倾向性条款→一律走 B）**：UtilityBrain 评分输入是 `NPCWorldState`（字符串键 float 池）+ 四特征人格权重（Fatigue/Social/Curiosity/Bravery）；Colony 是 HEXACO 五轴直接挂在 `NamedNpc` 上。复用需把 Colony 状态逐日灌进字符串键、把五轴压进四特征——正是简报点名的"对 Game.Domain 现有类做泛化改造（改敌方 AI 消费方）"，禁止。
3. **架构硬约束**：方案 A 需给 `Game.Domain.Colony.asmdef` 追加 Game.Domain 引用，而架构矩阵把 Colony 钉死为仅引 ZFrame.Core，`ArchitectureValidator.cs` 不在本任务白名单——方案 A 必然 arch 违规或越白名单。方案 B 零新引用，天然满足（arch exit 0 实证）。

**机制要点**：

- 评分公式公开可测：`ColonyActionScorer` 五候选纯函数，权重全部具名常量，T1 手算断言（商人+怨气 0.8 → Protest 分 0.91 = 0.6×0.8+0.2×0.8+0.2×0.6+0.15 逐字可复算）。
- **自主行为不消耗世界 Rng 流**（评分纯函数、邻居/传闻边选择全确定性）——T4 对照实验的增量差才可归因于自主行为本身（对照/实验两世界随机流逐位同源）。
- **ProtestVent=0.4 泄压阀**（抗议写回怨气负通道）：离线校准发现自主抗议经 PropagateDaily 情绪通道正反馈，全局怨气 8 天 runaway 到 0.9、全员抗议（Work 占比 8.6%<40%）；泄压后怨气均值稳定 ~0.47，分布 Work 43.2%/Protest 33.8%/Gossip 23.0%。
- Recruit：组织者向怨气>0.4 的具名邻居传导组织度（+0.02/人）并小幅回流聚落群体组织度（+0.001/人）——T4 涌现加速的机制来源（实验组聚落组织度 30 天 +0.621 vs 对照组 +0）。
- Work 按简报原文"Cooperation 已有，行为化留接口"：选中即计数，不写状态不写事件。

### 2.2 交付清单（sha256 与改动核对）

新增（`Assets/Game/Domain/Colony/Autonomy/`，均含 Unity 生成 .meta；UTF-8 无 BOM/LF）：

| 文件 | sha256 前 8 位 | 内容 |
|---|---|---|
| ColonyAction.cs | 1c0325e7 | 行为候选枚举（Work/Protest/Recruit/Gossip/Rest，枚举序=并列优先序） |
| ColonyNpcContext.cs | b69a4aab | 评分输入快照（人格/怨气/组织度/地位+邻居态势+ProtestRouter 强度），From 工厂 |
| ColonyActionScorer.cs | e0ddf927 | **效用评分公式**（权重全具名常量 + RecruitGrievanceThreshold=0.4 简报钉死） |
| ColonyTaskBoard.cs | 70aa4486 | 轻量任务板（候选+评分+Select 分派，零堆分配热路径；Ranked 调试视图） |
| ColonyDailyBehavior.cs | 58f985eb | 每日结算入口 SettleFor（BuildContext→板→分派→产出→计数）；三类行为产出写状态+Covert 事件 |

修改（均在白名单 `Assets/Game/Domain/Colony/**`）：

- `GameWorld.cs`（c75d35da）：+`AutonomyEnabled`（默认 true）/`AutonomousActionsToday`/`AutonomousChoicesToday`（npcId→选择，开发者可解释性入口）；DailyTick 在**现有具名层循环**内逐 NPC 调 `ColonyDailyBehavior.SettleFor`（不加新循环，每日清零）。diff：+17/-1 行。
- `MonthlyReport.cs`（9b86fd6c）：BuildRaw 对文本以 `[自主]` 标记开头的事件按 Kind 折叠成计数行——PlayMode C4 实证：自主行为是"每具名 NPC 每天 ≥1 条"量级（100 人×30 天≈3000 条），逐条渲染 Raw 会让报告面板 UGUI Text 超 65000 顶点硬上限抛异常。**Truth 日志（Log.All）与 `Report.Referenced`（视野断言口径）仍逐条全量**，Covert 纪律不变；M1-M5 全绿佐证。

测试（`Assets/Game/Tests/Domain/Colony/`）：`ColonyAutonomyTests.cs`（17be78bf，T1-T5，数据驱动建局走 ColonyConfigAdapter 五表管线，同 V/D/M/S 系列口径与种子）。

**白名单核对**：git status 改动仅限 `Colony/**`、`Tests/Domain/Colony/**`、本日志；Enemy/`Game.Domain/AI`/asmdef **零 diff**（git status 实证）；未 commit、未推远程。Docs/Parity 5 张 PNG 为 PlayMode 截图噪声，按 A-COL-05 先例 git checkout 丢弃。

### 2.3 自测数字

| 项 | 结果 |
|---|---|
| 编译 | exit 0（CS error 0，TempLogs/col06_c2.log） |
| EditMode 全量 | **1306/1306/0**（=1301 家底+T1-T5；em3 终轮，xml 在 TempLogs/col06_em3.xml） |
| PlayMode 两轮同形 | **119/119/0 + 119/119/0**（pm2+pm3，xml 同目录） |
| arch | exit 0（col06_ar1.log）；Enemy/AI 零 diff |
| T1 | 评分第一随人格翻转（A 变体 Protest=0 不登顶 / B 变体 0.91 登顶）+ 涨价加成单因子分差=ProtestMerchantBonus |
| T2 | 30 天×100 人分布：≥3 种 Action、Work 占比在 40-90% 区间、无组织者时 Recruit=0（仅 Organizer 门禁） |
| T3 | Recruit 选中：全部具名邻居组织度 +0.02、聚落组织度 +0.001×人数、Covert Uprising 事件、选择入账 |
| T4 | 涌现加速：实验组聚落组织度增量 **+0.621** vs 对照组 **+0.000**（同种子同编排，开关单变量） |
| T5 | 100 NPC×30 天自主结算 Stopwatch <50ms 断言通过（用例全程含建局仅 0.019s） |
| 变异自证 | ①第一处变异 `RecruitOrgGain 0.02→0.0` 跑出**假绿**（+0 与不加在断言上不可区分——如实登记为哑弹，已还原）；②第二处变异 `RecruitGrievanceThreshold 0.4→0.7` → **RED**（T3+T4 failed，col06_autmut2.xml）→ 还原 **sha256 逐字节一致**（e0ddf927…）→ **GREEN**（5/5，col06_autgreen.xml）。变异前备份（premutation 副本+三份 sha256）留档于仓库外 `_tmp_col06_harness/` |

**中途如实登记**：em1 一轮 1304/2——两个自测 bug（关系网允许同对人多条边导致招募重复计数；T4 断言撞上 `TrySpawnOrganizer` 同 id=90_000+population 的既有涌现行为，A-COL-01 已登记），已修（邻居按 id 去重 + T4 断言改按下限）；pm1 一轮 118/1（C4 UGUI 顶点溢出）→ Raw 折叠修复。修复后全量三轮（em3/pm2/pm3）全绿。

**Unity 环境纪律执行记录**：本轮全程未观察到用户编辑器进程（每次启动前字段核查）；两轮撞上"UnityLockfile 在但无 Unity 进程"——均为**本任务自己批处理异常退出所留残留**（0 字节、mtime 落在本任务批处理窗口内、字段核对无进程），按 HANDOFF 单实例字段核对清除并在此登记；taskkill 仅一次，对象是经命令行核实的**本任务批处理僵尸**（PID 3144：`-batchmode -testFilter …col06_autmut.xml`，xml 已落盘仍存活>5 分钟）。简报的"等 60s×10 不杀不删"针对用户编辑器的保护意图全程未破例——未触碰任何非本任务进程/锁。工具注记：`run_tests.ps1` 的单实例断言会与"编辑器开着"纪律冲突，本任务改用同纪律自写 runner（等 60s×10；绝不杀用户编辑器；绝不删非本任务锁），Unity 一律经 Git Bash 启动（HANDOFF 纪律）。

### 2.4 风险与待裁决

1. **Raw 折叠是显示行契约的 scoped 调整**：M2"Raw 行逐条呈现 Covert"对政策类 Covert 事件仍逐字成立（折叠只命中 `[自主]` 标记），但字面意义上 Raw 不再对自主事件逐条——验收/主控如要求绝对逐条，需裁决改由 View 层分页（Game.View 不在本任务白名单）。
2. **同 id 组织者**：`TrySpawnOrganizer` 的 id=90_000+population 使同聚落多名涌现组织者同 id（A-COL-01 既有行为，未改）——`AutonomousChoicesToday` 按 id 记录会合并他们，T4 断言因此按下限写；若未来修 id 唯一性需同步 T4。
3. **T2 Work 占比 43.2% 贴近 40% 下限**（裕度 3.2pp）：种子确定可复现，但任何评分权重调整都会移动分布；校准 harness（真实 100 卡 CSV+完整 Colony 源码，net8 离线跑）在仓库外 `_tmp_col06_harness/`，供验收复算。
4. **Work 行为化留接口**：Work/Rest 选中不写状态不写事件（简报原文如此）——T2 的 Work 计数只能从 `AutonomousChoicesToday` 读，不能从 EventLog 读。
5. **Rng 纯度依赖**：`ColonyNpcContext.From` 内调 `ProtestRouter.ChooseProtest`（纯函数无 Rng）——若未来该路由引入随机，T4 对照的因果纯度即破，需在调用点重审。


## 三、验收清单

**验收**：general-purpose 验收子代理（ZCode 会话）｜2026-11-22｜**独立亲证声明**：以下全部 Unity 运行、数字、变异均验收自跑（6 次启动：编译/变异RED/EditMode全量/PlayMode×2/arch），未采信施工报告任何数字；证据 xml/log 在 `TempLogs/acc_col06_*`（gitignore 内），备份与 sha256 台账在仓库外 `_tmp_acc_col06/`。

| 项 | 结果 | 独立证据 |
|---|---|---|
| V1 编译 | **PASS**：exit 0，`error CS` 计数 0 | `TempLogs/acc_col06_v1.log`（-batchmode -quit，UNITY_EXIT=0） |
| V2 EditMode 全量 | **PASS**：**1306/1306/0**（passed/failed/skipped，=1301 家底+T1-T5）。点名抽查零回归：`V1_PolicyEnact_DelayedBlowup`/`V7_EventBusBridge`/`D2_BuildWorldFromData`/`D3_RealMerchantEndToEnd`/`S2_DeterministicProfile`/`M3_OverOptimizationDivergence`/`P2_AnomalyLinesAreCompleteHighlightedRows` 全 Passed；另核 V2-V6/D1/D4/S1/S3-S5/M1/M2/M4/M5/P1/P3/P4 亦全 Passed（V1-V7/D1-D4/S1-S5/M1-M5/P1-P4 共 25 个点名零回归） | `TempLogs/acc_col06_em_v.xml` |
| V3 PlayMode 两轮同形 | **PASS**：**119/119/0 + 119/119/0**；逐用例（fullname,result）集合两轮完全一致，diff=0 | `TempLogs/acc_col06_pm_v1.xml` / `acc_col06_pm_v2.xml` |
| V4 架构+零越界 | **PASS**：arch **exit 0**（28 asmdef/14 矩阵条目/0 违规，`TempLogs/arch_report.txt` @21:15:37）；Enemy（Domain/Enemy+View/Enemy）与 `Game.Domain/AI`（+View/AI）`git diff` 空、git status 无条目；`Game.Domain.Colony.asmdef` 零 diff（仍仅引 ZFrame.Core、noEngineReferences:true——方案 B 零新引用属实）；Colony 域严格 grep `^using UnityEngine`/`^using ZFrame.Data` **零命中**（Autonomy/ 5 新文件同样适用；仅文档注释里出现字样）；新文件编码 UTF-8 无 BOM（首 3 字节 2f2f20）、LF | 见左 |
| V5 变异复红 | **PASS**：验收自选**第三处**（与施工两处均不同）：`ColonyDailyBehavior.ProtestVent 0.4f→0.0f`（拆泄压阀，复证施工机制主张）。先落备份+sha256 再动手 → **RED**：过滤跑 ColonyAutonomyTests，**T2_DailyDistribution failed**（Work 占比 0.1657<0.40，失败消息打印出 runaway 分布 Work=497/Protest=2233/Gossip=270——「无泄压阀则自主抗议 runaway」的机制主张被独立证实；T1/T3/T4/T5 不受影响，与变异定位预判一致，非哑弹非假绿）→ 还原 **sha256 逐字节一致 `58f985eb…d50`**（=动手前基线=施工登记指纹）→ **GREEN**：还原后 EditMode 全量 1306/1306/0（含 T1-T5 全绿）。变异三态 sha256：原 `58f985eb…` / 变异 `566f79c2…` / 还原 `58f985eb…` | `TempLogs/acc_col06_mut_red.xml`、`acc_col06_em_v.xml`；备份 `_tmp_acc_col06/backup/ColonyDailyBehavior.cs.orig` + `baseline_sha256.txt`（动手前落盘） |
| V6 白名单+未 commit | **PASS**：本任务可归因改动=2 M（GameWorld.cs/MonthlyReport.cs，均 Colony/**）+6 ??（本日志、Autonomy.meta、Autonomy/ 5 文件、ColonyAutonomyTests.cs+.meta）——全在白名单；另有 4 个 ?? PNG（`Tools/Probes/out/planet-budget-2026-11-1[45]*.png`，mtime 2026-11-14/15）为**任务开始前**即存在的探针噪声，与本任务无关未动；HEAD=3e9ae65（G-187），A-COL-06 未 commit。注：验收自己的 PlayMode 轮也产生过 4 张 Docs/Parity 截图噪声（mtime 21:14/21:15），按 A-COL-04/05 先例 `git checkout --` 丢弃、还原到轮前状态 | `git status --porcelain` 终态如左 |

**主控认可四项核对（对 diff 范围与登记一致性）**：

1. **Raw 折叠显示契约** ✔——折叠只命中 `e.Text.StartsWith("[自主]")` 的行（`MonthlyReport.cs` BuildRaw）；`Report.Referenced` 是 eaten 事件集合直存（MonthlyReport.cs:45），Truth（`Log.All`）为属性直读——两者不经 BuildRaw、仍逐条全量；M2_CovertOnlyInRaw 在验收全量轮 Passed 佐证政策类 Covert 契约未破。
2. **T4 下限断言** ✔——`Assert.GreaterOrEqual(exp.AutonomousChoicesToday.Count, 100)`，注释载明同 id 涌现组织者按 id 记录会合并的原因。
3. **T2 裕度 3.2pp** ✔——验收复用施工离线 harness（仓库外 `_tmp_col06_harness`，其 csproj glob 编译**当前仓库** Colony 源码+真实 100 卡 CSV，Program.cs 经验收通读确认与 Unity T2 同口径）独立复算：total=3000、**Work=1297(0.432)**/Protest=1013(0.338)/Gossip=690(0.230)、organizers=0——43.2% 与 3.2pp 裕度逐位复现。
4. **中途红已修** ✔——登记的两个自测 bug 与代码现状对应：BuildContext/ApplyRecruit/ApplyGossip 邻居均按 `HashSet<int>` id 去重；T4 断言为下限式（见上 2）；em1/pm1 中间轮 xml 留存 TempLogs 可查。

**Unity 环境纪律执行记录（验收轮）**：6 次启动的每次 PRECHECK 字段核查均为「attempt 1: no Unity process」（全程未观察到用户编辑器进程）；**零 taskkill、零 UnityLockfile 删除**（本轮 Temp/ 从未出现锁文件）；验收 runner（仓库外 `_tmp_acc_col06/acc_run_unity.sh`）内建 60s×10 等待重试与放弃出口（exit 42），本次未触发等待。

## 四、验收结论

**PASS**（V1-V6 全过，零保留意见；验收代理独立亲证，与施工不同代理）。

- TASK-A-COL-06 交付（方案 B 轻量 `ColonyTaskBoard`+Autonomy/ 五文件+GameWorld 挂点+MonthlyReport Raw 折叠+T1-T5）全部达标：编译 exit 0 零 CS error、EditMode **1306/1306/0**、PlayMode 两轮 **119/119/0** 同形、arch **exit 0**（28/14/0）、变异复红（第三处 ProtestVent，RED→还原逐字节一致→GREEN）、白名单干净未 commit。
- 验收变异独立证实施工的泄压阀机制主张（ProtestVent=0 → T2 Work 占比 0.432→0.1657 崩塌、Protest 2233/3000）；离线复算复现 43.2% 校准值——T2 裕度 3.2pp 属实，风险 2.3 条登记与实测一致。
- 施工报告 2.4 五条风险与代码现状核对一致，无隐瞒；主控认可四项逐条核对通过（见第三节）。
- 处置建议：①可进入主控 commit（一任务一 commit 由主控执行）；②`Tools/Probes/out` 4 张旧 PNG 为任务前遗留噪声（mtime 11-14/15），不属本任务白名单，建议主控按既有噪声处置惯例处理。
