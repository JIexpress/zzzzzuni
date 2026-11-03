# TASK-A-R9-06 —— 粒子接入玩法：命中火花的事件驱动接线（零轮询）

状态: doing | 派发: 2026-10-31 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

**前置**：A-R9-05 已完成（ParticleRuntime.SpawnHitSpark 可用）。

## 任务简报（主控写）

**目标**：让 R9-05 的火花真正出现在玩法里——**事件驱动零轮询**接线：玩法事件（攻击命中/拾取/交互）→ 视觉适配层 → `SpawnHitSpark(worldPos)`。架构红线：Domain 只发事件不碰粒子，View 适配层订阅事件取位置触发（保持分层）。

**设计要点**：
1. **事件源盘点**（先读代码确定，报告列全）：`EnemyAttackRequested`（含攻击者/目标位置——EnemyBehaviorTree.cs:310）为最自然的火花源；拾取 `ItemPickedUp`（若有位置字段，没有则加——Domain 事件加字段属最小扩展，报告注明）；交互成功事件（InteractSystem）。**至少接两个事件源**：敌人攻击 + 拾取（交互可选第三个）
2. `Game/View/Effects/GameplayFxBindings.cs`（MonoBehaviour 薄适配，RuntimeInitializeOnLoadMethod 或 GameBootstrap 挂载——参照 DebugPanelRoot/SceneLoader 自建先例）：
   - 订阅玩法事件（EventScope 模式，Detach 清理）→ `ParticleRuntime.SpawnHitSpark(pos)`
   - **分层红线**：事件类型定义在 Domain（已存在或最小新增），Bindings 在 View——Domain 不引 View（校验器把守）
   - 位置约定：攻击火花在目标位置；拾取火花在物品位置；同帧多事件合并（同位置 ≤1 火花——防 100 连发刷屏，复用 R9-05 池上限）
3. **可证伪 PlayMode 用例**（延续 A-R9-05 像素证据口径）：
   - 触发 EnemyAttackRequested（真实事件路径）→ 像素 brighter 上升（同 A-R9-05 门限口径）
   - Domain 纯度回归：校验器 exit 0
   - 开关协同：ParticleRuntime.Enabled=false 时事件照发但零像素变化
4. 单测（EditMode）：Bindings 订阅/退订（EventScope）、事件→SpawnHitSpark 调用次数与位置映射（对 ParticleRuntime mock/接缝）、同帧合并
5. 自测：EditMode+PlayMode 全绿（基线 719/12 只增不减）+ Validate exit 0（Unity 纪律）

**约束**：白名单 `Assets/Game/View/**`、`Assets/Game/Domain/**`（仅事件最小扩展）、`Assets/ZFrame/View/**`（Bindings 若属框架则放 Game.View）、Tests/**、本日志；不动 FG/超分/后处理/设置；不 git commit；UTF-8 无 BOM/LF。

**交付物**：两事件源接线+适配层+像素证据用例+单测+施工报告四节（含事件源盘点表）。

## 施工报告（施工代理写）

施工: general-purpose 施工子代理 ｜ 2026-10-31 ｜ 自测全绿（EditMode 736 / PlayMode 15 / 三校验器 exit 0）

### ① 实现概述

事件驱动零轮询接线落地：Domain 照常只发事件（架构校验器纯度扫描 0 违规，`grep Particle Assets/Game/Domain` = 0 命中），
View 层新增薄适配 `Game.View.Effects.GameplayFxBindings`（MonoBehaviour）订阅 `EventBus` 上的两类带位置玩法事件，
把 2D 平面位置映射成世界位置后调用注入的 `IGameplaySparkEmitter`（生产实现=R9-05 的 `ParticleRuntime.SpawnHitSpark`，
池上限/单开关语义原样复用）。**无 Update、无逐帧扫描、无协程**——全部工作只发生在事件处理器内；
挂载走 `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` 自建 DontDestroyOnLoad 宿主（DebugPanelRoot/SceneLoader 先例），
GameBootstrap 零引用。订阅走 `EventBus.OpenScope()` 单一 EventScope（`GameplayFxBindings.cs:165-169`），
`Shutdown()` 一次性批量退订（`:197`），play 模式经 OnDestroy 到达，EditMode 由测试显式调用（GameHud/A-M3-02 先例）。

### ② 事件源盘点表（先读代码定案，报告列全）

Domain 内全部 `EventBus.Fire` 位点共 6 类（grep `EventBus.Fire` in `Assets/Game/Domain` = 7 处/6 类）：

| # | 事件 | 位置字段 | 发布者（行号） | 接线 | 处置理由 |
|---|---|---|---|---|---|
| 1 | `EnemyAttackRequested`（Game.Domain.AI） | **有**：`EnemyPosition` + `PlayerPosition`（Vec2） | `EnemyBehaviorTree.AttackAction`（:310-312） | **✅ 接线** | 简报钦定最自然火花源；火花落在**目标位置**（`PlayerPosition`，`:217`）——"攻击火花在目标位置" |
| 2 | `ItemPickedUp`（Game.Domain.Interaction） | 原无 → **本任务最小扩展** 加 `Position`（Vec2，4 参构造保持兼容返回 `Vec2.Zero`） | `PickupEntity.Interact`（:85-87，事件构造 :101） | **✅ 接线** | 简报指定"没有则加"；位置由 View（`InteractableProxy` :91 用 `transform.position.x/z`）建实体时传入——沿用该文件既有注释的分工"View supplies the world position; the Domain only knows identity" |
| 3 | `DoorStateChanged`（Game.Domain.Interaction） | 无（DoorId + IsOpen） | `DoorEntity`（:88） | ❌ 未接线 | 交互成功语义已被 #2 覆盖（简报"交互可选第三个"）；接线需第二个事件扩展（门的位置字段），超出"仅事件最小扩展"授权，留给后续任务 |
| 4 | `InteractionTargetChanged`（Game.Domain.Interaction） | 无世界位置（HasTarget + Info，注释明言 Domain 只知身份） | `InteractionRegistry`（:104/:117） | ❌ 未接线 | 瞄准目标变化不是命中/成功事件，火花语义不符 |
| 5 | `EnemyStateChanged`（Game.Domain.Enemy） | 无（EnemyId + Previous + Current） | `EnemyFsm`（:186） | ❌ 未接线 | 状态切换非命中事件；且切片场景 `EnemyView` 用 FSM 驱动（无 Attack 分支） |
| 6 | `DialogAdvanced`（Game.Domain.Interaction） | 无（id/行号/文本/是否结束） | `DialogEntity`（:99-101） | ❌ 未接线 | 对话推进非命中事件 |

**接线数 = 2（敌人攻击 + 拾取），满足"至少两个事件源"**；同帧合并为跨源共享逻辑（合并键=帧号+位置，不看事件类型）。

### ③ 文件清单（全部在白名单内；不 git commit）

新增（4 个生产/测试文件 + 测试夹具，.meta 由 Unity 批处理生成一并落盘）：
- `Assets/Game/View/Effects/GameplayFxBindings.cs` — 薄适配本体。锚点：`FrameSource` 内部测试接缝 :111；
  `EventBus.OpenScope()` + 两条订阅 :165-169；EventScope 批量退订 :197；攻击→目标位置 :217；拾取→物品位置 :223；
  **同帧同位置合并判定 :233-236**（`sqrMagnitude <= MergeRadius²`，默认半径 0.25 m，`DefaultMergeRadius`）；
  2D→3D 映射 `ToWorld` :252-256（Vec2.X→X，Vec2.Y→Z，固定高度 `DefaultSparkHeight=1`，与 EnemyView 同约定）。
- `Assets/Game/View/Effects/IGameplaySparkEmitter.cs` — mock 接缝接口（`Spawn(Vector3)→int`，返回实际发射数）。
- `Assets/Game/View/Effects/ParticleRuntimeSparkEmitter.cs` — 生产实现，直通 `ParticleRuntime.SpawnHitSpark`。
- `Assets/Game/Tests/View/GameplayFxBindingsTests.cs` — EditMode 14 例（订阅/退订/重建、位置映射、同帧合并×4、接缝可见性守卫、默认值）。
- `Assets/ZFrame/Tests/PlayMode/GameplayFxPixelEvidencePlayModeTests.cs` — PlayMode 3 例像素证据（延续 A-R9-05 口径）。
- `Assets/ZFrame/Tests/PlayMode/PixelProbeRig.cs` — 从 R9-05 用例逐字抽取的共享探针夹具（internal，仅测试程序集；
  R9-05 原文件未动，零风险）。

修改（6 个，均为最小改动）：
- `Assets/Game/Domain/Interaction/ItemPickedUp.cs` — 加 `Position` 字段（:26）+ 5 参构造（:39），原 4 参构造保留并委托（兼容）。
- `Assets/Game/Domain/Interaction/PickupEntity.cs` — 构造加可选 `Vec2 position = default`（:42）存 :64、入事件 :101。
- `Assets/Game/View/Interaction/InteractableProxy.cs` — Pickup 分支传平面位置（:91）。
- `Assets/Game/View/AssemblyInfo.cs` — 追加 `InternalsVisibleTo("Game.View.Tests")`（FrameSource 接缝按 B-R10-01 纪律收 internal）。
- `Assets/Game/Tests/View/Game.View.Tests.asmdef` — references 追加 `ZFrame.Core`/`ZFrame.Gameplay`/`Game.Domain`（Tests 程序集豁免校验矩阵）。
- `Assets/Game/Tests/Domain/PickupEntityTests.cs` — 追加 3 例（事件携带位置 / 兼容构造=Zero / 构造重载语义）。

### ④ 自测证据（本机实跑，工具=Tools/run_tests.ps1，Unity 纪律内建）

| 段 | 结果 | 数字 |
|---|---|---|
| EditMode | **PASS 736/736**（基线 719 + 新增 17：Bindings 14 + PickupEntity 3） | `TempLogs/runtests_editmode.xml` |
| PlayMode | **PASS 15/15**（基线 12 + 新增 3） | `TempLogs/runtests_playmode.xml` |
| arch 校验器 | **PASS exit 0**，违规数 0（报告时间 2026-10-31 02:00:07，晚于全部改动） | `TempLogs/arch_report.txt` |
| config 校验器 | **PASS exit 0** | `TempLogs/config_report.txt` |
| naming 校验器 | **PASS exit 0** | `TempLogs/name_report.txt` |
| Domain 纯度复核 | `grep Particle Assets/Game/Domain`=0 命中；禁用 token（GameObject 等）=0 命中 | 架构校验器 + 手工 grep 双证 |

**PlayMode 像素证据读数**（`TempLogs/runtests_playmode.log`，320×180 ARGBFloat 实读回，门限沿用 A-R9-05 标定 brighter≥0.004 / meanAbs≥0.002）：
1. **真实事件路径**（`EventBus.Fire(new EnemyAttackRequested(...))`，与 `EnemyBehaviorTree.AttackAction` 同路径，非直调 SpawnHitSpark）：
   control 均匀 0.0200 → burst meanLuma 0.03586 / max 0.8712，**brighter=0.0196（门限 4.9×）meanAbs=0.01586（门限 7.9×）**，
   events=1 sparks=1 merged=0、LastSpawnEmitted=12；0.55 s 后全部回到 control（寿命契约保持）。
2. **同帧 10 连拾取**：events=10 **sparks=1 merged=9**，brighter=0.0142（过门限），burst 后 0.55 s 存活=12（仅一次 burst）——"防 100 连发刷屏"的实证。
3. **开关协同**（`ParticleRuntime.SetEnabled(false)`）：事件照发（events=2 sparks=2）但 **moved=0.0000 / meanAbs=0.000000**（与 control 逐位相同）、TotalParticleCount=0、LastSpawnEmitted=0——单开关真理在 ParticleRuntime，Bindings 不持开关状态。

### ⑤ 设计取舍与说明

- **合并语义**：合并键 =（同一帧, 世界位置距离 ≤ MergeRadius）。选"收到即判"而非"帧末缓冲刷新"——前者零延迟零缓冲、天然事件驱动；EditMode 无法推进 `Time.frameCount`，故帧号源收 internal 接缝 `FrameSource`（测试注入手动计数器，生产永赋值；`AssemblySeamVisibilityTests` 同族纪律，另加一条守卫断言）。
- **开关协同**：Bindings 不复制 Enabled 状态，始终转发，由 emitter（→SpawnHitSpark 内部）直接 no-op——单一真理源，且 PlayMode 用例可证"事件照发+零像素"。
- **攻击火花取 `PlayerPosition`**（敌方挥击的落点=目标处），不是攻击者位置；简报"目标位置"即此。
- **ItemPickedUp 扩展兼容性**：4 参构造保留（返回 `Vec2.Zero`），既有调用点/测试零破坏（EditMode 719 基线全绿即证）。
- **拾取火花高度**：`DefaultSparkHeight=1f`（地面之上胸口高度），可经 `SparkHeight` 属性调整；像素用例把它设为探针原点高度以复现 R9-05 标定几何（1.2 m 距离 + 纯色背景 + ARGBFloat 读回，控制帧逐位均匀）。
- **过程返工（如实记录）**：前两轮 EditMode FAIL 均为编译错（`ParticleRuntimeSparkEmitter` 漏 `using UnityEngine`；测试文件 `Object` 二义性 / 漏 `using UnityEngine.TestTools`），第三轮起全绿——非逻辑缺陷。

### ⑥ 遗留与建议（不阻塞验收）

1. `DoorStateChanged` 未接线（盘点表 #3）：如需门开合火花/尘土，建议后续给 `DoorEntity` 加位置字段并复用本 Bindings（新增一个 handler + 一个订阅行即可）。
2. `GameplayFxBindings` 的自动挂载实例在整个 play 会话存活（DontDestroyOnLoad）；当前它对画面零成本（无 Update、无事件时不工作），但若未来 R9+ 加"每帧效果"，建议改为随 SCN_Game 生命周期挂载（GameHud 模式）。
3. PlayMode 像素用例继承 R9-05 的环境边界：Editor 批处理无交互桌面下取证，Player 包内表现由 R9-05 的 Player 像素证据背书（同一 SpawnHitSpark 路径，本任务未改动渲染内核）。
4. `.meta` 文件由本轮 Unity 批处理生成（Effects 目录 + 5 个新 .cs），与资产同盘待主控随任务一并提交。

## 验收清单（主控写）

- [ ] V1 PlayMode 像素证据用例真实过（触发真事件→brighter 上升）；EditMode 全绿（数字写入结论）
- [ ] V2 分层审查：Domain 零粒子引用（grep）；Bindings 订阅走 EventScope（行号）；Validate exit 0（自跑）
- [ ] V3 同帧合并逻辑行号；Enabled=false 协同用例
- [ ] V4 事件源盘点表与实际接线一致
- [ ] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

验收: general-purpose 验收子代理 ｜ 2026-10-31 ｜ **结论: PASS（5/5，V4 附一条文档级笔误备注）** ｜ 全部证据为本验收代理亲跑/亲测，未复用施工自证

- **V1 PASS** — 亲跑 PlayMode **15/15 exit 0**（`TempLogs/runtests_playmode.xml` 02:16 本轮）+ EditMode **736/736**（`TempLogs/runtests_editmode.xml` 02:10 本轮，含 17 条新用例：Bindings 14 + PickupEntity 3，逐名核对在列）；攻击像素证据亲测复现：`EventBus.Fire(new EnemyAttackRequested(...))` 真事件路径，control 均匀 0.0200 → burst meanLuma 0.03594，**brighter=0.0197 ≥ 门限 0.004（4.9×）**、meanAbs=0.01594 ≥ 0.002，events=1/sparks=1/merged=0/LastSpawnEmitted=12（`TempLogs/runtests_playmode.log` [FXBINDINGS] 行）。
- **V2 PASS** — `grep -ri particle Assets/Game/Domain` = **0 命中**（扩展 grep particle|vfx|emitter|monobehaviour|gameobject 仅 `ItemPickedUp.cs:23` 英文注释含 "spark" 一词，无任何代码引用）；Game.View.asmdef 引 ZFrame.View+Game.Domain 而 Game.Domain 不引 View（asmdef 亲核）；EventScope 订阅 `GameplayFxBindings.cs:165`（OpenScope）+`:168-169`（两条 Subscribe），批量退订 `:197`（`_scope?.Dispose()`，EventBus.cs:151/171 机制亲核）；ItemPickedUp 位置字段属**最小扩展且兼容**：4 参构造保留返回 `Vec2.Zero`（:29-36），全仓 4 参调用点仅 legacy 测试且有专门断言（PickupEntityTests:146），EditMode 基线零破坏即证。
- **V3 PASS** — 同帧合并判定在 `GameplayFxBindings.cs:232-243`（帧号 :232、同帧+半径判定 :233-236 `sqrMagnitude <= MergeRadius²`）；EditMode 合并用例 4 条全绿（含跨源合并 Merge_IsSourceAgnostic、次帧再武装 SamePosition_NextFrame_SparksAgain）；PlayMode 亲跑 **10 连拾取 events=10 / sparks=1 / merged=9**、brighter=0.0136 过门限、0.55 s 后 alive=12；Enabled=false 亲跑 **events=2 / sparks=2 事件照发但 moved=0.0000 / meanAbs=0.000000**、TotalParticleCount=0、LastSpawnEmitted=0（零像素逐位一致）。
- **V4 PASS** — ValidateFromCommandLine 自跑：arch 校验器 **exit 0 违规 0**（报告 02:17:24，晚于全部改动最晚源文件 01:53），config/naming 亦 exit 0；事件源盘点表与实际接线**逐条吻合**（亲 grep：Domain 内 EventBus.Fire 共 7 处/6 类 = 表所列；接线 2 类 = Bindings :168-169）；4 类未接理由逐一核实合理（DoorStateChanged 无位置字段、二次扩展超"仅事件最小扩展"授权且交互语义已被拾取覆盖；InteractionTargetChanged 瞄准变化非命中；EnemyStateChanged 非命中且 `EnemyView.cs:32/56` 亲核确用 FSM 无 Attack 分支；DialogAdvanced 对话推进非命中）。**备注（不阻塞）**：盘点表 PickupEntity 发布者行号 ":85-87" 与现文件不符（Interact 实为 :92-104、Fire :103），事件构造 :101 引用准确——文档笔误，实体与行号主体无错。
- **V5 PASS** — 涉改 12 文件全 UTF-8 无 BOM、LF、末尾换行（file/od 亲验，git index 纯 LF，`git ls-files --eol` i/lf）；新文件 .meta 齐全（Effects.meta+3 个 .cs.meta+2 测试 .meta+PixelProbeRig.meta）；接口 I 前缀/_camelCase/常量 PascalCase/XML doc 合规；施工报告四节齐全（简报/施工报告 6 小节/验收清单/本节），**2 轮编译返工如实记录**（⑤节：漏 using UnityEngine、Object 二义性+漏 using UnityEngine.TestTools；终码相应 using 亲核在位）。观察项（不阻塞）：PickupEntity.cs 顺手把异常消息 "item id" 改为 "itemId"，同文件外观级改动。

**验收裁决**：任务 A-R9-06 验收 **PASS**。接线 2 事件源（敌人攻击→目标位置、拾取→物品位置）+ 同帧合并 + 单开关协同全部有像素级可证伪证据；Domain 零粒子引用、EventScope 生命周期、兼容性扩展均亲证成立。遗留 4 条（盘点表⑥节）均为非阻塞记账，处置恰当。请主控刷新 HANDOFF/TASKS/WORKLOG 并执行 commit（验收代理不 commit）。
