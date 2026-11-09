# TASK-A-AI-04 —— 可供性接场景（NPC 真的走过去用）

> 前置：A-AI-03（机制层）已闭环。本刀把「注册表 + 打分」接到**真实场景锚点**，
> 并让一个 NPC 真的**朝最近的可供性走过去并占用它**。

## 一、目标

1. **场景锚点**：`SceneAffordances`（MonoBehaviour）在 SCN_Game 加载时，把场景里已有的物件
   注册成可供性（**不新建任何可见物体**）：
   - 优先按名字找锚点（`ENV_Barrel` → Work、`ENV_Tree` → Rest、NPC 聚集点 → Social）；
   - 找不到就用固定兜底坐标（文档里写明是兜底）。
2. **会走的 NPC**：`AffordanceSeeker`（MonoBehaviour）：
   - 每个 seeker 有自己的 `UtilityBrain`（Idle + 4 种可供性动作）；
   - 选中动作后朝目标以 `MoveSpeed`（默认 1.5 m/s）移动；到达 ≤ 半径即 `Claim` 并停住；
   - `Stop()` 释放占用；不接管已有 `NpcUtilityView` 的移动（**默认不挂到任何现有 NPC**）。
3. **可测**：PlayMode 里给一个 NPC 手动挂 seeker，1~2 秒内能观察到它朝最近锚点移动并 Claim。

## 二、现状（施工前必读）

- `Game/Domain/AI/Affordance/`：`Affordance`、`AffordanceRegistry`、`AffordanceAction`（A-AI-03 产物）。
- `UtilityBrain` / `UtilityAction`：动作注册与 `Tick(state, time)`。
- `NpcUtilityView`：现有 NPC 的视图/移动；**不要改它**。
- `SurvivalLoop` / `BuildMode` 的挂载范式：`sceneLoaded` 门控 + 默认待机。

## 三、设计要点

- `SceneAffordances` 只注册**数据**（位置/种类/半径/容量），不建渲染物——像素用例零影响。
- 锚点查找用 `GameObject.Find`（仅场景加载时一次，不在 Update 里）；找不到记一条日志并走兜底。
- `AffordanceSeeker` 不自动挂载；提供 `Configure(registry, brain)` 供测试注入。
- 移动只改 `transform.position`（XZ），**不碰 Rigidbody/Animator 参数**，避免与现有 NPC 驱动打架。
- 到达判定：`Vector3.Distance(XZ) <= affordance.Radius`；到达后 `Claim`，若 Claim 失败（被别人占了）则重新选。
- 超时保护：单个目标追 10 秒未到达 → 放弃并释放（防卡死）。

## 四、白名单

- `UnityProject/Assets/Game/View/AI/SceneAffordances.cs`（新增）
- `UnityProject/Assets/Game/View/AI/AffordanceSeeker.cs`（新增）
- `UnityProject/Assets/Game/Tests/PlayMode/AffordanceScenePlayModeTests.cs`（新增）
- `Docs/Logs/TASK-A-AI-04.md`、`TASKS.md`（仅本任务行）

**不动**：A-AI-03 的 `Affordance*` 语义、`NpcUtilityView`、`UtilityBrain`、`SurvivalLoop`、`BuildMode`。

## 五、验收清单

| # | 断言 | 判据 |
|---|---|---|
| V1 | 场景注册 | SCN_Game 加载后 `SceneAffordances.Instance.Registry.Count >= 3`，种类覆盖 Work/Rest/Social |
| V2 | 零可见物 | 注册过程不新建任何 Renderer（统计场景 Renderer 数量前后一致） |
| V3 | 会走 | PlayMode：给 NPC 挂 seeker + `StartSeeking()`，1.5s 后 NPC 到最近锚点的距离显著减小 |
| V4 | 占用 | 到达后 `affordance.Occupants == 1`，`IsAvailable == false`（容量 1 时） |
| V5 | 停止 | `Stop()` 后占用释放、NPC 不再移动 |
| V6 | 不干扰 | 不挂 seeker 时 NPC 位置完全不变（默认零影响） |
| V7 | 变异复红 | 把到达判定改成「永不满足」→ V4 必红；恢复后全绿 |

## 六、施工报告

### 6.1 摘要

把 A-AI-03 的「注册表 + 打分」接到 SCN_Game 的真实锚点，并给一个 NPC 装上会走路的
`AffordanceSeeker`。**Domain 层一行未改**（`Affordance*` / `UtilityBrain` / `UtilityAction` /
`NPCWorldState`），`NpcUtilityView` / `SurvivalLoop` / `BuildMode` 也一行未改——两把刀都只加在
`Game/View/AI/` 新目录里。

- **`SceneAffordances`**（MonoBehaviour）：`RuntimeInitializeOnLoadMethod + sceneLoaded` 门控，
  只在 SCN_Game 加载时生成宿主（与 SurvivalLoop / BuildMode 同范式，`DontDestroyOnLoad`）。
  登记 3 条纯数据：`scene.work.barrel` ← `ENV_Barrel`（r=1.2，容量 1）、`scene.rest.tree` ←
  `ENV_Tree_01`（r=1.5，容量 1）、`scene.social.gathering` ← `NPC`（r=2.0，容量 2）。
  `GameObject.Find` 只在加载时调一次；找不到打 warning 并退到固定兜底（`WorkFallback (3.2,0,2.9)` /
  `RestFallback (-4,0,-3)` / `SocialFallback (-2.2,0,2.2)`）。**零 Renderer**：不 CreatePrimitive、
  不建材质，只建一个空宿主 GameObject。`RegisterSceneAffordances()` 是幂等重扫入口（V2 用），
  重扫会整体丢弃旧条目（含旧占用），语义 = 「世界换了一张图」。
- **`AffordanceSeeker`**（MonoBehaviour，**不自动挂载**）：`Configure(registry, brain = null)` 是注入入口；
  brain 为 null 时自建默认大脑 = `Idle` + 4 个 `AffordanceAction`（Rest/Work/Social/Eat，名字
  `Affordance.<Kind>`）。`StartSeeking()` 立刻选一次目标；`Update` 每帧以 `MoveSpeed`（默认 **1.5 m/s**）
  只改 transform 的 **XZ**（Y 与旋转原样）；`FlatDistanceTo <= Radius` 即 `Claim` 并停住；
  `Stop()` 释放占用且不再移动；单目标 **10s** 未达 → 释放并停止（warning 日志）。
  测试面：`ManualDrive` + `SimulateForTests(seconds, step)`（固定步长，与 SurvivalLoop 的
  `SimulateForTests` 同口径），`CurrentTarget / IsSeeking / IsArrived / LastChoice / State` 可断言。
- **一个必须写下来的实现决策（踩过）**：`AffordanceAction.Score` 走 `FindBest`，而 `FindBest` 会跳过
  「已满」的条目——如果 seeker 一直攥着自己上一拍 Claim 的目标，下一拍它会把自己占的条目看成满员、
  该动作分数归零，NPC 就在 Work/Rest/Social 之间来回抖（永远走不到、也永远不触发 10s 超时；首轮 PlayMode
  正是这样红了 V4/V5/超时 3 条）。修法：**每拍先 `ReleaseOwnedExcept(null)` 再 `brain.Tick`**，本拍结束
  时赢家重新 Claim——世界对打分始终可见，外部观察到的占用仍是连续的（拍内释放、拍末恢复，单线程无并发）。
  这段顺序在 `Step` 里有注释，验收代理可重点复核。
- **V7 变异复红已实做**：把两处到达判定改成 `<= -1f`（永不满足）→ PlayMode **109/110，仅 V4 红**；
  还原（md5 与变异前逐字节一致）→ **110/110 全绿**。变异版 xml 留档
  `TempLogs/task_aaai04_mutant_playmode.xml`。

### 6.2 改动清单

| 文件 | 类型 | 说明 |
|---|---|---|
| `UnityProject/Assets/Game/View/AI/SceneAffordances.cs` | 新增（175 行） | 场景锚点 → 3 条可供性；零 Renderer；兜底 + 日志；幂等重扫；sha256 `a7d21032d83184d6…` |
| `UnityProject/Assets/Game/View/AI/AffordanceSeeker.cs` | 新增（396 行） | Idle+4 默认大脑；1.5 m/s XZ 移动；到达 Claim；Stop 释放；10s 超时；`Configure` 注入；sha256 `e9c7633a8c27b5d4…` |
| `UnityProject/Assets/Game/Tests/PlayMode/AffordanceScenePlayModeTests.cs` | 新增（288 行，8 条） | V1-V6 + 默认大脑组成 + 超时放弃；sha256 `05214675855289a0…` |
| `UnityProject/Assets/Game/View/AI.meta`、`View/AI/*.cs.meta`、`Tests/PlayMode/AffordanceScenePlayModeTests.cs.meta` | 新增（Unity 自动生成） | 未手改 |
| `Docs/Logs/TASK-A-AI-04.md` | 修改 | 本节 |

未改：`Affordance.cs / AffordanceRegistry.cs / AffordanceAction.cs / AffordanceKind.cs`、
`UtilityBrain.cs / UtilityAction.cs / NPCWorldState.cs`、`NpcUtilityView.cs`、`SurvivalLoop.cs`、
`BuildMode.cs`、任何 asmdef、`TASKS.md`（看板无 A-AI-04 行，按白名单「仅本任务行」不新增）。
编码：三个新 `.cs` 均 UTF-8 无 BOM + LF（脚本校验输出 `no BOM LF`）。
测试跑产生的 `Docs/Parity/*.png` 噪声已 `git checkout --` 还原，工作区只剩本任务文件。

### 6.3 自测数字（真跑，证据 `TempLogs/*.xml`）

| 段 | 命令 | 结果 | 基线 |
|---|---|---|---|
| EditMode | `Tools/run_tests.ps1 -Segments editmode` | **1140 / 1140 通过，0 失败**（10.02s） | 1140（持平，本刀不加 EditMode 用例） |
| PlayMode | `Tools/run_tests.ps1 -Segments playmode` | **110 / 110 通过，0 失败**（73.25s） | 102（**+8**，即本次 8 条） |
| PlayMode（变异） | 到达判定 `<= Radius+eps` → `<= -1f` | **109 / 110，1 失败 = V4**（预期红） | — |
| PlayMode（还原） | 同上还原后复跑 | **110 / 110 通过，0 失败** | — |

新增 8 条（xml 内逐条可见，全部 Passed）：
`V1_SceneLoad_RegistersWorkRestAndSocial`（Count≥3 + 种类覆盖）、
`V2_Registration_CreatesNoRenderers`（重扫前后 Renderer 数量相等且条目数稳定）、
`V3_Seeker_WalksTowardTheAnchor`（真实 `Update` + `WaitForSeconds(1.2f)`，距离 6m → 4.2m，>1m）、
`V4_Seeker_ClaimsOnArrival`（3s 步行后 `IsArrived`、`Occupants=1`、`IsAvailable=false`）、
`V5_Stop_ReleasesAndFreezes`（Stop 后 Occupants=0、再模拟 1s 位置逐位不变）、
`V6_WithoutSeeker_TheSceneNpcNeverMoves`（无 seeker 且场景 NPC 0.5s 位置不变）、
`DefaultBrain_IsIdlePlusFourAffordanceActions`（ActionCount=5，Ranking 含 4 种 Affordance.*）、
`Seeker_AbandonsAfterTimeout_AndReleases`（AbandonSeconds=1s 时放弃并释放）。

**锚点命中**：最终 PlayMode 日志中 `anchor ... not found` 计数 **0**——`ENV_Barrel` / `ENV_Tree_01` /
`NPC` 三个名字全部命中，兜底路径未被走到（兜底仍有用例外的代码路径，靠日志可查）。
**设备纪律**：每轮跑前 `tasklist` 无 Unity 进程；脚本自动删 `UnityProject/Temp/UnityLockfile`；
`-runTests` 不带 `-quit`；所有实际启动的轮次均由 `results.xml` 落地后 Unity 自行 exit（正常轮 Exit 0、
变异轮 Exit 2），**未 taskkill**。
**环境处置**：首轮启动被 `Duplicate case-variant variables {HTTPS_PROXY/https_proxy}` 拦下
（交接档案 §3.2 已知坑）——在调用 shell 内先清掉大小写重复的代理变量再同进程调用脚本即通过；
`Tools/run_tests.ps1` 与任何仓库文件均未改。
**证据留档**：`TempLogs/task_aaai04_editmode_green.xml`、`TempLogs/task_aaai04_playmode_green.xml`、
`TempLogs/task_aaai04_mutant_playmode.xml`（均为本轮真实落盘文件副本）。

### 6.4 风险与未验证

1. **锚点是「名字契约」**：场景重命名/隐藏 `ENV_Barrel`、`ENV_Tree_01`、`NPC` 任一，都会静默退到兜底
   坐标（有 warning，但 V1 仍会绿——它只断言 Kind 覆盖）。这是简报明确允许的降级，不是缺陷；若后续
   美术改场景，需要同步改常量或补一条「锚点必须命中」的严格用例。
2. **占用是「拍内释放、拍末恢复」**：单线程下等价于持续占用，但如果将来有其他系统在 `Update` 顺序中
   插到 seeker 的 `Step` 内部去查询占用，会看到一拍的空窗。当前全体调用方都是同帧顺序访问，无此问题。
3. **计时用 seeker 自己的 `_clock`**（`Update` 累加 `Time.deltaTime`）：`Time.timeScale = 0` 时超时
   也会暂停；若将来需要「暂停也计时」需换 `unscaledDeltaTime`。本刀无暂停需求。
4. **`Affordance.Position` 是注册时快照**：Social 锚点取的是 NPC 当前位置，NPC 以后会走动时，可供性
   不会跟着走（需重扫或改成动态位置）。当前 SCN_Game 的 NPC 是静态交互物，无影响。
5. **Host 常驻（DontDestroyOnLoad）**：离开 SCN_Game 后宿主仍在，注册表保留最后一次快照；它不渲染、
   不驱动任何东西，属于惰性残留。重进 SCN_Game 会重扫覆盖。若验收认为「离开场景应清空」，需要加
   `sceneUnloaded` 钩子（本刀未做，避免超出白名单语义）。
6. **V3 是唯一走真实帧时间的用例**：批处理机器忙时 1.2s 内位移可能略少于预期，断言阈值留了 1m
   （预期 1.8m）余量；其余用例全部固定步长，不受帧率影响。最终轮机器静默检查为 QUIET，已通过。
7. **未验证**：多 seeker 抢同一容量 1 的锚点（A-AI-03 已覆盖注册表层，但本刀没做双 NPC 场景竞争用例）；
   到达后的「持续使用」表现（本刀只要求停住 + 占用，没有做动画/收益）。

## 七、验收结论（验收代理独立复核，2026-11-09）

> 口径：不采信施工数字，全部自己跑；`Tools/run_tests.ps1` 未改，调用 shell 内清掉大小写
> 重复的代理变量（HANDOFF §3.2 已知坑）。证据一律以本轮 `TempLogs/runtests_*.xml` 为准。

### 7.1 两组数字（本轮真跑）

| 段 | xml（本轮落盘） | start-time | 结果 |
|---|---|---|---|
| EditMode | `TempLogs/runtests_editmode.xml` | 2026-11-09 13:19:15Z | **1140 / 1140 通过，0 失败**（1140 用例） |
| PlayMode | `TempLogs/runtests_playmode.xml` | 2026-11-09 13:16:39Z | **110 / 110 通过，0 失败**（70.89s） |

8 条新用例本轮全部 Passed（xml 逐条可见）：
`V1_...RegistersWorkRestAndSocial` 0.35s、`V2_...CreatesNoRenderers` 0.34s、
`V3_...WalksTowardTheAnchor` 1.54s、`V4_...ClaimsOnArrival` 0.35s、
`V5_Stop_ReleasesAndFreezes` 0.44s、`V6_...TheSceneNpcNeverMoves` 0.88s、
`DefaultBrain_IsIdlePlusFourAffordanceActions` 0.43s、`Seeker_AbandonsAfterTimeout_AndReleases` 0.38s。
本轮 PlayMode 日志中 `anchor '...' not found in SCN_Game` 计数 = **0**：
`ENV_Barrel` / `ENV_Tree_01` / `NPC` 三个名字全部命中（场景里前两者是 Prefab 实例改名，运行时
`GameObject.Find` 可见），未走兜底。

### 7.2 逐项判定

| # | 判定 | 证据（用例 + 数字/行号） |
|---|---|---|
| V1 | **PASS** | 用例 `V1_SceneLoad_...`（Passed 0.35s）：`LoadSceneAsync("SCN_Game")` 真实加载（测试:241-248）后断言 `Count>=3` 且 Kind 覆盖 Work/Rest/Social（测试:30-41）。代码注册 3 条：`SceneAffordances.cs:138-140`。本轮日志：`[SceneAffordances] SCN_Game registered 3 affordances`。 |
| V2 | **PASS** | 用例 `V2_...CreatesNoRenderers`（Passed 0.34s）：重扫前后 `FindObjectsOfType<Renderer>().Length` 相等且条目数稳定（测试:55-63）。代码核对：本类唯一新建对象是空宿主 `new GameObject("~SceneAffordances")` + `AddComponent<SceneAffordances>`（`SceneAffordances.cs:103-105`），无 `CreatePrimitive`/`AddComponent<Renderer>`/材质，`RegisterSceneAffordances` 只写数据（126-143）。 |
| V3 | **PASS** | 用例 `V3_...WalksTowardTheAnchor`（Passed 1.54s）：走真实 `Update` 路径（`AffordanceSeeker.cs:106-114`），`WaitForSeconds(1.2f)` 后距离必须减少 >1m（测试:88-91）；6m 起点、1.5m/s×1.2s 预期 1.8m，阈值留 0.8m 余量。 |
| V4 | **PASS** | 用例 `V4_...ClaimsOnArrival`（Passed 0.35s）：固定步长 `SimulateForTests(3f)` 后 `IsArrived==true`、`Occupants==1`、`IsAvailable==false`、距离 ≤ 半径（测试:115-119）；到达 Claim 在 `AffordanceSeeker.cs:263/277 → Arrive() → 348`。 |
| V5 | **PASS** | 用例 `V5_Stop_ReleasesAndFreezes`（Passed 0.44s）：`Stop()` 后 `Occupants==0`、`IsAvailable==true`、`IsSeeking==false`，再模拟 1s 位置逐位相等（测试:144-152）；实现 `AffordanceSeeker.cs:154-160`。 |
| V6 | **PASS** | 用例 `V6_...TheSceneNpcNeverMoves`（Passed 0.88s）：`FindObjectOfType<AffordanceSeeker>()==null`（测试:166），0.5s 后场景 NPC 位置 `AreEqual` 逐位相等（测试:174）。代码侧：seeker 无 `RuntimeInitializeOnLoadMethod`，全仓无 `AddComponent<AffordanceSeeker>`（仅测试），场景/预制体对新脚本 guid 零引用；`SceneAffordances` 无 `Update`，只有加载时一次性登记。 |
| V7 | **PASS** | 见 7.3 变异记录：变异轮 **109/110，唯一失败 = V4**；逐字节恢复（md5 `49cb29ad5684c73b85b6930ba5f38476`、sha256 `e9c7633a…c50d`，与变异前/施工报告一致）后 **110/110 + 1140/1140 全绿**。 |

### 7.3 变异复红记录（亲自实做）

1. 变异点：`AffordanceSeeker.cs:263` 与 `:277` 两处
   `_target.FlatDistanceTo(transform.position) <= _target.Radius + ArriveEpsilon` → `<= -1f`（永不满足）。
   变异后 md5 `15919cfc97a8071ed52b7fb5d228bb89`。
2. 跑 PlayMode：`total=110 passed=109 failed=1`，唯一失败
   `V4_Seeker_ClaimsOnArrival`，消息「3m 外 3s 内必须走到半径 1.5m 内 Expected: True But was: False」。
   留档：`TempLogs/task_aaai04_verifier_mutant_playmode.xml`。
3. 逐字节恢复：md5 回到 `49cb29ad5684c73b85b6930ba5f38476`、sha256 回到
   `e9c7633a8c27b5d4db9292f986d0e71dd404cce8c6d38e24ccc7ce64a983c50d`（与施工报告前缀一致）。
4. 恢复后复跑：PlayMode **110/110**（13:16:39Z）、EditMode **1140/1140**（13:19:15Z），全绿。

**注意（变异暴露的语义）**：变异轮 V5 仍绿，说明 V4 的 `Occupants==1` 并非"到达才占用"的独占证据——
`AffordanceAction.Execute` 在**选中动作时**就 Claim（`AffordanceAction.cs:212`），到达前占用已是 1；
V4 的红由 `IsArrived` 断言触发，满足 V7 判据，但"到达即 Claim"的时序未被单独锁定（见 7.5）。

### 7.4 重点复核

1. **零可见物**：代码层零渲染物（唯一新对象是空宿主，`SceneAffordances.cs:103-105`；无任何 Renderer/材质/
   Primitive API），V2 通过。**证据强度受限**：V2 的 `before` 在首次注册（Awake）之后才采样，严格说只证明
   "重扫不建 Renderer"；首次注册零可见物由代码审读 + 日志佐证，不是用例直接证明的。
2. **默认零影响**：V6 通过（NPC 位置逐位不变）；seeker 确认不自动挂载（无自动 AddComponent、场景/预制体
   guid 零引用、无 InitializeOnLoad）；`SceneAffordances` 常驻但无逐帧逻辑、不改任何物体。
3. **「每拍先释放再 Tick」复核**：理由成立——`FindBest` 会跳过满员条目（`AffordanceRegistry.cs:163`），
   不先释放则自己占的目标下一拍分数归零，动作来回抖。新问题评估：`Step` 内"释放 → Tick(Execute 立即
   Claim)"同步完成，单线程下其他 agent 无法插入，未发现"释放后被别人抢走"；但两点须记：
   ① 若将来有别的组件在同一帧 `Update` 里查占用，理论上能看到一拍空窗（施工风险 #2 属实）；
   ② 占用实际发生在**选中时**而非到达时，远处目标会被提前预订（最多 10s 超时才释放）。
   两者都不违反 V4/V5 判据，但与简报"到达 ≤ 半径即 Claim"的措辞有偏差。
4. **未误改既有 AI**：`git diff HEAD` 为空；`git status` 只剩本任务 5 个新增路径（测试跑产生的 3 张
   `Docs/Parity/*.png` 已 `git checkout --` 还原）。`Affordance*`、`UtilityBrain`、`NpcUtilityView`、
   `SurvivalLoop`、`BuildMode` 及 asmdef 零改动——与施工自述一致。
5. **是否假绿**：否。8 条用例全部 `LoadSceneAsync("SCN_Game")` 并断言 `SceneAffordances.Instance` 非空
   （测试:241-248），用的是场景加载真实产生的 registry；`SpawnSeeker` 只是把**同一个**场景 registry 注入，
   没有自建 registry 绕开加载路径。

### 7.5 受限与未验证

1. V2 的"首次注册零 Renderer"只能代码审读证明（见 7.4-1）。
2. V4 未锁定 Claim 时序（选中即占用）；如需严格锁定，应补一条"未到达时 Occupants==0"的用例
   （本轮不改白名单外文件、不新增用例）。
3. V3 是唯一真实帧时间用例，1m 阈值余量；本轮通过，但跨机器忙时仍需余量。
4. EditMode 每轮以 `exit=1` + xml 落盘 300s 后 taskkill 收尾（既有纪律），本轮 xml 新鲜且 Passed，
   与施工记录一致。
5. 多 seeker 抢容量 1、Social 锚点随 NPC 移动、离开 SCN_Game 清空注册表仍未覆盖（与施工风险 1/4/5/7 一致）。
6. `WorkFallback` 与真实 `ENV_Barrel` 坐标相同（3.2, 0, 2.9），无法用坐标区分命中/兜底；本轮以
   "anchor not found 警告 0 条"判定命中。

### 7.6 总判定

**PASS** —— V1-V7 全部通过：EditMode **1140/1140**、PlayMode **110/110**；变异 **109/110** 且唯一红为 V4；
恢复后哈希逐字节一致并复跑全绿。未改白名单外文件、未 commit，工作区已回到施工完成态
（仅本任务新增文件，PNG 噪声已还原）。

