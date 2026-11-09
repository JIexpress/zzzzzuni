# TASK-A-AI-03 —— 可供性扫描（让 NPC 从「7 个固定动作」走向「自己找事」）

> 出处：`Docs/TECH-VALIDATION.md` §7 第 2 项。现状：`UtilityBrain` 只在 7 个编译期动作里排序，
> 世界本身不参与决策——NPC 感知不到「这里有张床/这里有活可干」。
> 本刀只做**机制层**：世界可供性注册 + 扫描 + 接入效用打分；接场景与队友是下一刀。

## 一、现状（施工前必读）

- `Game/Domain/AI/Utility/UtilityBrain.cs`：`Add(UtilityAction)` / `Tick(NPCWorldState, time)` → 选中动作名；有 `SelectionThreshold`、`LastRanking`。
- `UtilityAction.cs`：`Score(NPCWorldState)` + `Execute()`，构造用委托。
- `NPCWorldState.cs`：键值表（`Keys.Fatigue` 等）。
- `NpcUtilityAgent.cs`：把 brain 包在 NPC 上（**本刀不要改它的行为**）。

## 二、目标

1. **可供性数据**（纯逻辑）：
   - `AffordanceKind` 枚举：`Rest / Work / Social / Eat`（4 种够用，别开一堆）。
   - `Affordance`：`Id`、`Kind`、`Position`（Vector3，XZ 用）、`Radius`、`Capacity`、`Occupants`、`IsAvailable`。
2. **注册表**：`AffordanceRegistry`
   - `Register/Unregister`、`Count`；
   - `Claim(affordance, agentId)` / `Release(affordance, agentId)`：容量占用，重复 Claim 幂等；
   - `FindBest(Vector3 from, AffordanceKind kind, float maxDistance)`：返回最近的**可用**可供性（距离 ≤ maxDistance、未满员），无则 null；
   - 扫描要有预算：`ScanBudget`（默认 64），超出直接返回 null 并计数 `BudgetExceededCount`（**为 20+ NPC 预留**）。
3. **接入效用**：`AffordanceAction`（工厂）
   - `Create(registry, kind, agentId, positionProvider)` → `UtilityAction`；
   - `Score(state)`：无可用可供性 → 0；有 → 按「距离越近分越高 × 需求权重」给 0..1；
   - `Execute()`：Claim 该可供性（并记录 `LastClaimed` 供测试断言）。
4. **测试**：EditMode 覆盖注册/容量/距离/预算/动作打分；PlayMode 一条：3 个可供性 + 1 个 brain，Tick 后选中的是最近那个。

## 三、设计要点

- 纯逻辑层不依赖 UnityEngine 的 MonoBehaviour；`Vector3` 可以用（Domain 已依赖 UnityEngine，见现有 AI 代码）。
- **不要**改 `UtilityBrain.Tick` 的语义；`AffordanceAction` 只是多注册一个 `UtilityAction`。
- 打分公式写死并注释：`score = needWeight * clamp01(1 - distance/maxDistance) * (available?1:0)`；需求权重从 `NPCWorldState` 取（例如 `Fatigue` 高 → Rest 权重高），**键名沿用现有 Keys，不许新造一套状态系统**。
- 预算语义：`FindBest` 单次最多检查 `ScanBudget` 个；超限不抛，计数并返回已找到的最优。
- 不做：寻路、接场景触发器、队友编队（留给 A-AI-04）。

## 四、白名单

- `UnityProject/Assets/Game/Domain/AI/Affordance/**`（新增：Affordance / AffordanceKind / AffordanceRegistry / AffordanceAction）
- `UnityProject/Assets/Game/Tests/Domain/AffordanceTests.cs`（新增）
- `UnityProject/Assets/Game/Tests/PlayMode/AffordancePlayModeTests.cs`（新增，可只 1 条）
- `Docs/Logs/TASK-A-AI-03.md`、`TASKS.md`（仅本任务行）

**不动**：`UtilityBrain` / `UtilityAction` / `NpcUtilityAgent` / `NPCWorldState` 的既有语义。

## 五、验收清单

| # | 断言 | 判据 |
|---|---|---|
| V1 | 注册/注销 | Register 后 Count+1；Unregister 后 -1；重复 Unregister 不抛 |
| V2 | 容量 | Capacity=1 时 Claim 一个后 `IsAvailable=false`；Release 后恢复；重复 Claim 幂等 |
| V3 | 最近可用 | 两个同类可供性，`FindBest` 返回更近的；最近那个满员时返回次近的 |
| V4 | 需求权重 | `Fatigue` 高时 Rest 动作分 > 疲劳低时；`Social` 动作在无可用可供性时分为 0 |
| V5 | 预算 | 注册 100 个、`ScanBudget=10` → `BudgetExceededCount>0` 且不抛、仍返回最优 |
| V6 | 接入效用 | brain 注册 Idle + AffordanceAction：有可供性时选中动作名 = 可供性动作；撤掉后回 Idle |
| V7 | 变异复红 | 把 `FindBest` 的可用性判断去掉（满员也返回）→ V3 必红；恢复后全绿 |

## 六、施工报告

### 6.1 摘要

只做机制层：世界侧可供性注册 + 最近可用扫描 + 接进效用打分。`UtilityBrain` / `UtilityAction` /
`NpcUtilityAgent` / `NPCWorldState` **一行未改**，`AffordanceAction` 只是多注册一个普通 `UtilityAction`。

- **`AffordanceKind`**：`Rest / Work / Social / Eat` 四种，不加第五种。
- **`Affordance`**：`Id / Kind / Position(Vector3) / Radius / Capacity / Occupants / IsAvailable`。
  容量槽位由可供性自己持有（`HashSet<string>` 代理 id）：`Occupants` 是真实占用数，而不是计数器快照；
  同一 agentId 重复占位不增长（幂等），满员时 `IsAvailable=false`。距离统一用 `FlatDistanceTo`（只算 XZ，
  Y 不影响判断——坡上坡下同一张床不该显得更远）。
- **`AffordanceRegistry`**：`Register / Unregister(返回 bool，重复/null 不抛) / Count / Claim / Release`；
  `FindBest(from, kind, maxDistance)` 线性扫描注册顺序，只接受 `Kind` 匹配、`IsAvailable`、距离 ≤
  `maxDistance` 的候选，严格小于才替换（等距 → 先注册者胜，确定性平局规则）。`ScanBudget` 默认 **64**，
  单次扫描最多访问这么多个**已注册项**（错种类/满员也计入访问数）；超限不抛、`BudgetExceededCount++`、
  返回已找到的最优。重复注册同一实例与容量 <1 / 半径 <0 / 空 id 一律 fail-fast。
- **`AffordanceAction`**：`Create(registry, kind, agentId, positionProvider)` → 标准 `UtilityAction`
  （名字默认 `Affordance.<Kind>`，可用 6 参重载覆盖 `maxDistance`/`name`）。打分写死并注释：
  `score = needWeight × clamp01(1 − distance/maxDistance) × (available ? 1 : 0)`；无可用可供性时恰为 0。
  `Execute` 用同一查找重新取一次最近可用者并 `Claim`，记录 `LastClaimed`（测试锚点）。
  另给 `CreateTracked(...)` 返回句柄（供测试读 `LastClaimed`、供 owner 调 `ReleaseClaim()` 释放）。
- **需求权重（只用既有 Keys，不新造状态）**：`Rest ← Fatigue`（越累越想要，键缺席=0）；
  `Work ← DeviceBroken`（坏装置=1.0，否则 0.5 基线）；`Social ← Trust`（0.5 + 0.5×trust）；
  `Eat ← 0.5`（尚无饥饿键，先给基础需求基线，A-AI-04 接场景时再泵真实值）。个性标签：
  Rest→`fatigue`、Social→`social`，Work/Eat 为中立职责（权重 1.0）。
- **命名空间取 `Game.Domain.AI.Affordances`（复数）**：C# 不允许同一父命名空间下同时存在同名
  命名空间与类型（`Game.Domain.AI.Affordance` 命名空间 + `Affordance` 类会 CS0101），目录名仍按白名单
  为 `Affordance/`。

### 6.2 改动清单

| 文件 | 类型 | 说明 |
|---|---|---|
| `UnityProject/Assets/Game/Domain/AI/Affordance/AffordanceKind.cs` | 新增（23 行） | 4 种枚举；sha256 `c09954640ebe3abc…` |
| `UnityProject/Assets/Game/Domain/AI/Affordance/Affordance.cs` | 新增（125 行） | 数据 + 容量槽位 + XZ 距离；sha256 `b5b09ded2176c7ba…` |
| `UnityProject/Assets/Game/Domain/AI/Affordance/AffordanceRegistry.cs` | 新增（181 行） | 注册/注销/Claim/Release/FindBest/预算；sha256 `0bbf9a12ed38dd48…` |
| `UnityProject/Assets/Game/Domain/AI/Affordance/AffordanceAction.cs` | 新增（218 行） | 效用动作工厂 + 打分公式 + LastClaimed；sha256 `76105ca3ff37c74a…` |
| `UnityProject/Assets/Game/Tests/Domain/AffordanceTests.cs` | 新增（268 行，13 条） | V1-V6 纯逻辑 |
| `UnityProject/Assets/Game/Tests/PlayMode/AffordancePlayModeTests.cs` | 新增（44 行，1 条） | 3 可供性 + 1 brain 选最近 |
| `UnityProject/Assets/Game/Domain/AI/Affordance.meta` 及四个 `.cs.meta`、两个测试 `.cs.meta` | 新增（Unity 自动生成） | 未手改 |
| `Docs/Logs/TASK-A-AI-03.md` | 修改 | 本节 |

未改：`UtilityBrain.cs` / `UtilityAction.cs` / `NpcUtilityAgent.cs` / `NPCWorldState.cs` / 任何 asmdef /
`TASKS.md`（看板无 A-AI-03 行，按白名单「仅本任务行」不新增）。编码：UTF-8 无 BOM + LF
（`head -c 3` 非 `EF BB BF`，`grep -c $'\r'` = 0）。

### 6.3 自测数字（真跑，证据 `TempLogs/runtests_*.xml`）

| 段 | 命令 | 结果 | 基线 |
|---|---|---|---|
| EditMode | `Tools/run_tests.ps1 -Segments editmode` | **1140 / 1140 通过，0 失败**（11.61s） | 1127（**+13**，即本次 13 条） |
| PlayMode | `Tools/run_tests.ps1 -Segments playmode` | **102 / 102 通过，0 失败**（70.62s） | 101（**+1**，即本次 1 条） |

新增 EditMode 13 条（`Game.Domain.Tests.AffordanceTests`，xml 内逐条可见）：注册/注销/重复注销 2 条、
容量 1 与容量 2 各 1 条、最近可用 / 满员退次近 / 距离与 XZ 口径 3 条、预算超限与默认 64 共 2 条、
Rest 手算打分（0.9 疲劳 × 0.5 = **0.45**；0.2 × 0.5 = **0.1**）1 条、无可用=0 且 Score 不占用 1 条、
Work/Social 需求权重与个性标签 1 条、brain 接入选最近 + 撤掉回 Idle + ReleaseClaim 1 条。
PlayMode 1 条：`Tick_WithThreeAffordances_SelectsTheNearest`——3m/6m/9m 三张床，注册顺序打乱，
Tick 一拍选中 **3m** 那张并占用其容量。

**设备纪律**：跑前 `tasklist` 无 Unity 进程、脚本自动删 `UnityProject/Temp/UnityLockfile`；`-runTests`
不带 `-quit`；两轮都由 `results.xml` 落地后 Unity 自行 exit（Exit 0），**未 taskkill**。
**环境处置**：两轮首次启动均被 `Duplicate case-variant variables {HTTPS_PROXY/https_proxy}` 拦下
（交接档案 §3.2 已知坑）——在调用 shell 内清掉大写代理变量（保留小写）后同进程重跑即通过，脚本与仓库未改。
**噪声**：`results.xml` 删除被本机 safe-delete 钩子拒绝，脚本按指纹判新（`evidence: results.xml written
by this run`），证据可信；未触发 stale 判定。

### 6.4 风险与未验证

1. **V7 变异复红未实做**（本任务自测口径 V1-V6）：靶点已就位——删掉 `FindBest` 里的
   `|| !candidate.IsAvailable`，`FindBest_SkipsFullNearest_ReturnsNextNearest` 必红。留给验收代理实测。
2. **个性权重可把分推过 1**：`AffordanceAction` 原始分 0..1，`UtilityBrain` 还会乘 CFG 个性权重
   （0.5..2.0），最终排名分可能 >1。这是既有 brain 语义（乘法调制），不是本刀新增；Rest/Social 动作带
   trait 后会被个性放大/缩小，测试断言只打在 `UtilityAction.Score`（未乘权）上。
3. **占用的释放责任在 owner**：`Execute` 只 Claim，不自动 Release（简报语义）。NPC 死亡/销毁/换目标时
   必须调 `ReleaseClaim()` 或 `registry.Release(...)`，否则床位永占；本刀无 NPC 生命周期，A-AI-04 接场景
   时必须补释放钩子。这是已知设计缺口，不是遗漏。
4. **预算按「访问项」计数，不按「同类候选」**：场景里混入大量异类可供性时，排在预算外的同类项会被跳过；
   语义已写进类注释并有测试（100 个同类 + 预算 10）。若 A-AI-04 的单场景可供性超过 64，需要调大
   `ScanBudget` 或把注册表按 Kind 分桶——本刀刻意不提前分桶。
5. **Score 与 Execute 各查一次**：两拍之间世界可能变化（例如别人先占了），此时 Execute 会 Claim 新的
   最近可用者或空转；`LastClaimed` 可能与打分时的目标不同。属预期（动作执行以执行时世界为准），未做
   「分数-执行目标」绑定。
6. **Eat 需求是常量 0.5**：现有 `NPCWorldState.Keys` 没有饥饿通道，按简报「不许新造状态系统」只给基线；
   真实饥饿要等 A-AI-04 泵入后再改权重函数。
7. **不做寻路 / 不接场景触发器 / 不接队友编队**（简报明确留给 A-AI-04）；新目录零场景引用，
   对现有 `SCN_Game` / `SCN_Utility` 零行为影响。
8. **未跑 arch/config/naming 三段**（本任务自测口径只有 editmode/playmode）；新增只有 .cs + 自动 .meta，
   无资产命名风险，建议主控闭环前补全量。**未 commit**。

## 七、验收结论

**验收代理独立复跑（不采信施工数字），判定：PASS（V1-V7 全过）。**

### 7.1 独立复跑（真跑，证据 `TempLogs/runtests_*.xml`）

| 段 | 命令 | 结果 | 对照 |
|---|---|---|---|
| EditMode | `Tools/run_tests.ps1 -Segments editmode` | **1140 / 1140 通过，0 失败**（9.47s，start 11:42:06Z） | 与施工期 1140 一致 |
| PlayMode | `Tools/run_tests.ps1 -Segments playmode` | **102 / 102 通过，0 失败**（67.02s，11:44:53–11:46:00Z） | 与施工期 102 一致 |

新增 13 条 EditMode 全部出现在 XML（`Game.Domain.Tests.AffordanceTests`，逐条 `result="Passed"`），
PlayMode `Tick_WithThreeAffordances_SelectsTheNearest` 亦 Passed。环境处置：调用 shell 删除大写
`HTTPS_PROXY/HTTP_PROXY`（保留小写）后 Unity 正常启动；脚本与仓库未改。

### 7.2 逐项判定

| # | 判定 | 证据（用例 + 数字 + 代码行） |
|---|---|---|
| V1 | PASS | `RegisterAndUnregister_CountTracksEntries_RepeatUnregisterDoesNotThrow`（AffordanceTests.cs:34）：Count 1→2→1；重复 Unregister / null Unregister 均 false 不抛。`Register_SameInstanceTwice_Throws`（:54）。实现 `AffordanceRegistry.cs:64/88`。 |
| V2 | PASS | `CapacityOne_ClaimFills_ReleaseRestores_RepeatClaimIsIdempotent`（:67）：Claim 后 Occupants=1、`IsAvailable=false`；第二代理 Claim=false 且计数不变；同代理重复 Claim=true 且仍 1；Release 后恢复可用，重复 Release=false。`CapacityTwo_SecondAgentFits_ThirdIsRejected`（:89）：2 容量第 3 人 false。实现 `Affordance.cs:76/103`。 |
| V3 | PASS | `FindBest_ReturnsNearestAvailable_OfTheRequestedKind`（:106）：注册顺序打乱仍返回 2m 者；`FindBest_SkipsFullNearest_ReturnsNextNearest`（:122）：2m 满员→返回 6m，全满→null；`FindBest_RespectsMaxDistance_OnTheXZPlaneOnly`（:142）：Y=50 不增大距离、2m 门限→null。实现 `AffordanceRegistry.cs:143`（可用性过滤 :163，XZ 距离 `Affordance.cs:91`）。 |
| V4 | PASS | `RestScore_ScalesWithFatigue_AndIsZeroWithoutAffordance`（:193）：0.9×0.5=**0.45**、0.2×0.5=**0.1**；`Score_NoAvailableAffordance_IsZero_AndScoreDoesNotClaim`（:209）：Social 无可用=0、满员后=0，且 Score 后 Occupants 仍 1（纯读不 Claim）；`WorkNeed_FollowsDeviceBroken_SocialNeed_FollowsTrust`（:228）：Work 坏=1.0/否则 0.5、Social=0.5+0.5×trust、Rest trait=fatigue。实现 `AffordanceAction.cs:151/188`。 |
| V5 | PASS | `FindBest_ScanBudgetExceeded_CountsAndStillReturnsBestFound`（:157）：注册 100 个 + `ScanBudget=10` → `DoesNotThrow`、`BudgetExceededCount>0`、仍返回索引 5 的最近者（1m）；`DefaultScanBudget_CoversSmallRegistries_WithoutCounting`（:175）：默认 64、10 个不计数、`ScanBudget=0` 抛。实现 `AffordanceRegistry.cs:155-159`（超限 break + 计数，不抛）。 |
| V6 | PASS | `Brain_SelectsAffordanceAction_WhenAvailable_FallsBackToIdleWhenRemoved`（:242）：真 `UtilityBrain` + 真 registry，选中 `"Affordance.Rest"`、`LastClaimed=bed`、Occupants=1；`Unregister` 后回 `Idle`；`ReleaseClaim` 后 Occupants=0。实现 `AffordanceAction.cs:204`（Execute 内 Claim）。 |
| V7 | PASS | 见 7.3，变异复红并逐字节恢复。 |

### 7.3 变异复红（验收代理亲自执行）

- 靶点：`AffordanceRegistry.cs:163` 删除 `|| !candidate.IsAvailable`（满员也返回）。
- 变异前 sha256 `0bbf9a12ed38dd48e2363268eaf10014258aa16420aba17e51b6ed9c18254000`；
  变异后 `6e16222aaaf4fb555225c7ae24a426ca8e639944750b55996fd5b8fd11bdb2b5`。
- 变异后 EditMode：**1138 / 1140，2 失败**——`FindBest_SkipsFullNearest_ReturnsNextNearest`
  （V3，失败消息「最近者满员时必须退到次近者（V7 变异靶点）」）与
  `Score_NoAvailableAffordance_IsZero_AndScoreDoesNotClaim`（V4，消息「满员后打分必须归 0」）：
  同一可用性谓词被扫描与打分两处真依赖，V3 必红成立（V4 连带红，属预期）。
- 恢复：由备份 `cp` 回原文件，`sha256sum -c` OK、`cmp` 逐字节一致（sha 回到 `0bbf9a12…254000`）。
  证据：`TempLogs/acc_aaai03_mutant_editmode.xml`、`acc_aaai03_registry_sha_before.txt`。
- 恢复后复跑：EditMode **1140/1140**、PlayMode **102/102**，全绿。

### 7.4 重点复核

- **零改动**：`git diff HEAD` 对 `UtilityBrain.cs / UtilityAction.cs / NpcUtilityAgent.cs / NPCWorldState.cs`
  输出为空（`git diff --name-only` 无条目）；`git status` 仅有白名单新增。运行 PlayMode 的既有像素用例会改写
  `Docs/Parity` 三张被跟踪截图，验收后已 `git checkout --` 还原，工作区回到施工完成态。
- **打分公式**：`AffordanceAction.cs:200-201` = `NeedWeight × clamp01(1 − distance/maxDistance)`；
  `:194-196` 无可用可供性直接返回 0（即简报的 `× available` 项）；需求键只取既有
  `NPCWorldState.Keys`（Fatigue / DeviceBroken / Trust），未新造状态。
- **预算**：`AffordanceRegistry.cs:155-159` 超限仅 `break` + `BudgetExceededCount++`，不抛，返回已找到最优。
- **PlayMode 真实性**：`AffordancePlayModeTests.cs:19-41` 用真 `AffordanceRegistry` + 真 `UtilityBrain.Tick`，
  断言 `LastClaimed`/`Occupants`，非写死期望。
- **编码**：6 个新 `.cs` 均 UTF-8 无 BOM、磁盘 LF；`git check-attr` = `text:auto eol:lf`。

### 7.5 受限与噪声（如实）

1. 恢复态一次 PlayMode 出现 1 条无关偶发红：`ZFrame.PlayMode.Tests.AssemblyConnectPlayModeTests.ConnectedChain_GroundSettled_10Seconds_NoJitterNoExplosion`，锚点误差 **0.0500005335** vs 门限 `< 0.05`（超 5.3e-7 m，物理边界抖动），与本刀零关系；原样复跑即 **102/102** 通过（失败快照留证 `TempLogs/acc_aaai03_restored_playmode_fail.xml`）。
2. 两次 EditMode 段 Unity 写完 XML 后未自行退出，脚本按纪律 `taskkill`（XML 为 `Passed 1140/1140`，退出码 1 是僵尸清理而非测试失败）；首次干净运行 exit 0。验收结束已确认无残留 Unity 进程。
3. 未跑 arch/config/naming（本任务验收口径只有 editmode/playmode；新增仅 `.cs` + Unity 自动 `.meta`）。

**总判定：PASS / V1-V7 全过。**
