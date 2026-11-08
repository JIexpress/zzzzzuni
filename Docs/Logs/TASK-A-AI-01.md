# TASK-A-AI-01 —— NPC 记忆与态度系统（效用 AI 第三层：个性×态度=行为）

状态: doing | 派发: 2026-11-04 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

### 背景

用户方向四第一刀。A-TECH-04 已有个性权重（CFG 表），但 NPC **没有记忆**——被玩家攻击过也照常巡逻。本任务：记忆条目→态度聚合→**调制效用权重**，让"同事件不同 NPC 不同反应、同一 NPC 随经历改变"成立。

### 设计要点（纯 C# Domain，零 UnityEngine）

1. `Game/Domain/AI/Memory/`：
   - `MemoryEntry { EventType Type; float Valence(-1~1); float Timestamp; EntityRef Source; }`（事件类型枚举：AttackedBy/GiftedFrom/SawDeath/PlayerNear...）
   - `NpcMemory`：条目列表（容量上限 32，滚动丢弃最旧）+ `Add(entry)` + `Aggregate(attitudeType)`——聚合公式：按类型权重的**时间衰减加权和**（近期权重大，半衰期参数纯函数可测）
2. **态度**：`NpcAttitude { float Fear; float Anger; float Trust; }`——从记忆聚合（AttackedBy→Fear+Anger↑；GiftedFrom→Trust↑）；聚合系数每 NPC 可配（与个性表同源——**勇敢个性恐惧系数低**，跨系统联动）
3. **调制效用**：UtilityBrain 评估时态度参与——恐惧高→逃跑类行动分数乘增益、攻击类乘抑制（公式纯函数可测；具体调制点在 NpcUtilityAgent 的欲望函数处，报告行号）
4. **触发源**：现有玩法事件挂接——玩家攻击敌人（EnemyAttackRequested 或受伤事件）→目标 NPC 记忆写入；玩家拾取在 NPC 视野→信任微增（事件已有）
5. 演示与可证伪用例（EditMode）：
   - 同一 AttackedBy 事件→Fear 高 NPC 选逃跑、Anger 高 NPC 选反击（**同事件不同反应**——个性×态度核心证据）
   - 时间衰减：注入旧记忆 vs 新记忆→态度不同（半衰期纯函数断言）
   - 容量滚动：33 条→最旧丢弃
   - 调制可逆：无新事件→态度随时间回落→行为恢复基线
6. 自测：EditMode 全绿（基线 928+新增）+ Validate exit 0

### 约束

- 白名单：`Assets/Game/Domain/AI/**`、`Assets/Configs/**`（态度系数若入表）、Tests/**、本日志
- 不改 A-TECH-04 的 GOAP/个性表语义（态度作为新维度叠加——报告说明关系）；Unity 批处理纪律；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0

## 施工报告（施工代理写）

施工: general-purpose 施工子代理 ｜ 日期: 2026-11-08 ｜ 自测: **EditMode 1031/1031/0（exit 0）+ PlayMode 80/80/0（exit 0）+ 架构校验器违规 0 PASS（exit 0）**

### 1. 交付概览

按简报 6 条设计要点全量交付，纯 C# Domain（新代码零 UnityEngine，架构校验器实证）：

| 简报要点 | 交付 | 关键位置 |
|---|---|---|
| ① 记忆条目+滚动存储 | `MemoryEntry`（Type/Valence −1..1/Timestamp/Source，越界 fail-fast）、`EntityRef`（值类型归因，可由 `ActorId` 派生）、`NpcMemory`（容量 32 滚动丢最旧 + `Add` + `Aggregate(type,now,halfLife)` 时间衰减加权和） | `Assets/Game/Domain/AI/Memory/`：MemoryEntry.cs / EntityRef.cs / NpcMemory.cs:49,82 |
| ② 态度聚合（纯函数+半衰期） | `NpcAttitudeMath.DecayWeight = 0.5^(age/halfLife)`（NpcAttitudeMath.cs:43）、`Evaluate(memory,profile,now)` 按类型系数×衰减加权，效价负→威胁(Fear/Anger)、正→善意(Trust)，维度钳制 0..1（:72）；`NpcAttitude`（Fear/Anger/Trust）；`NpcAttitudeProfile` 每 NPC 系数（半衰期 20s 默认） | NpcAttitudeMath.cs / NpcAttitude.cs / NpcAttitudeProfile.cs |
| ③ 态度调制效用（触点在 NpcUtilityAgent 欲望函数） | **调制触点行号见 §2**；恐惧→逃跑增益 ×(1+2·fear)、恐惧→攻击抑制 ×(1−0.8·fear)、愤怒→攻击增益 ×(1+1.5·anger)、信任→跟随增益 ×(1+0.5·trust)——公式全部纯函数（NpcAttitudeMath.cs:111,127,141） | NpcUtilityAgent.cs:610-640,566-575 |
| ④ 触发源挂接 | 受伤钩子 `NoteAttackedBy/NoteSawDeath/NoteGiftedFrom/NotePlayerNear`（时钟同源打戳，NpcUtilityAgent.cs:438-459）+ `NpcMemoryFeed` 桥接**既有事件**：`EnemyAttackRequested` 视野内→SawDeath 恐惧记忆（NpcMemoryFeed.cs:73）、`ItemPickedUp` 视野内→PlayerNear 信任微增 0.1（:92） | NpcMemoryFeed.cs / NpcUtilityAgent.cs:427 |
| ⑤ 演示与可证伪用例 | 同事件异反应 / 半衰期衰减 / 容量滚动 / 调制可逆 / 同源联动 / 兼容性——EditMode 30 条 + PlayMode 场景 1 条（截图） | §3 证据矩阵 |
| ⑥ 自测 | EditMode 1031/1031/0 + Validate exit 0 | §4 自测记录 |

### 2. 关键实现与决策

**2.1 聚合公式（纯函数，可手算）**：`Fear/Anger += decay × threat × FearGain/AngerGain(type)`、`Trust += decay × goodwill × TrustGain(type)`，其中 `decay = 0.5^(age/halfLife)`、`threat = max(0,−valence)`、`goodwill = max(0,valence)`，维度在 `NpcAttitude` 构造钳制 0..1。中性系数：AttackedBy→恐惧 0.6/愤怒 0.8；SawDeath→恐惧 0.5；GiftedFrom→信任 0.5；PlayerNear→信任 0.1（微增）。手算锚：1 条 fresh 攻击 → Fear 0.6/Anger 0.8；半衰期后 ×0.5，两个半衰期后 ×0.25。

**2.2 态度不落盘、每拍重推**：`NpcUtilityAgent.Tick`（NpcUtilityAgent.cs:485）每拍 `NpcAttitudeMath.Evaluate(_memory, _profile, _clock)` 重derive态度并泵入世界快照（:533-543 新增 Keys.Fear/Anger/Trust/ThreatDistance + `HasThreat()`，NPCWorldState.cs 纯增量）——"无新事件→随半衰期回落→行为恢复基线"由数学免费给出（可逆性用例 NpcAttitudeBehaviorTests.cs:181 实证）。

**2.3 态度系数不入 CFG 表（决策）**：简报允许"态度系数若入表"。施工选择**不入表**：`NpcAttitudeProfile.ForPersonality(NPCPersonalityDef)`（NpcAttitudeProfile.cs:97）从 CFG_NpcPersonality 行**派生**系数——恐惧增益 ÷BraveryWeight（勇敢 2.0 → 0.3，胆小 0.5 → 1.2）、受击愤怒增益 ×BraveryWeight（勇敢 → 1.6）、信任增益 ×SocialWeight——**同源联动零表改动**，A-TECH-04 的表语义/CSV/资产/校验管线全部原样（"不改个性表语义"约束零风险满足），且 profile 仍可自由构造（每 NPC 可配）。行为差异由用例 `Profile_FromPersonality_BraveHasLowFearGain_HighAngerGain`（NpcMemoryAttitudeTests.cs:203）锁死。

**2.4 调制触点（报告行号，简报硬性要求）**——欲望函数处，公式本体纯函数：
- **恐惧→逃跑增益**：`NpcUtilityAgent.ScoreFlee`（**NpcUtilityAgent.cs:610-621**，:620 调 `NpcAttitudeMath.FleeDesire`）＝ proximity × fear × (1+2×fear)——恐惧同时门控（0→不逃）与增益（1→×3）；
- **恐惧→攻击抑制 + 愤怒→攻击增益**：`NpcUtilityAgent.ScoreCounterattack`（**NpcUtilityAgent.cs:628-640**，:639 调 `CounterDesire`）＝ proximity × anger × (1+1.5×anger) × (1−0.8×fear)——恐惧 1 压到 ×0.2；
- **信任→跟随增益**：`NpcUtilityAgent.ScoreFollow`（**NpcUtilityAgent.cs:566-575**，:573 调 `FollowTrustGain`）＝ ×(1+0.5×trust)，trust 0 → ×1 逐位兼容；
- 配套：新候选注册 :311-312（Flee/Counterattack，无威胁/无态度时欲望恒 0）、态度重推 :485、快照泵入 :533-543、执行器 ExecuteFlee/ExecuteCounterattack（背离/逼近威胁移动）。

**2.5 与 A-TECH-04 的关系（约束说明）**：GOAP 修理链与个性表语义零改动；态度是**新增正交维度**——新候选不带 UtilityTraits 标签（个性权重 1.0），仅受态度调制；个性对态度的影响走 2.3 的系数派生（个性×态度=行为的乘法两级）。兼容性：无记忆时新候选恰 0 分、调制恒 ×1，旧行为逐位不变（`WithoutMemory_NewAttitudeActionsScoreZeroAndNeverChosen` NpcAttitudeBehaviorTests.cs:154 + 全部既有用例通过实证）。

**2.6 触发源集成路径**：Domain 提供 `NpcUtilityAgent.Note*`（受伤直写钩子——上游输入/战斗结算把"玩家攻击该 NPC"接到此处）与 `NpcMemoryFeed`（把既有 `EnemyAttackRequested`/`ItemPickedUp` 事件按视野半径转写成 SawDeath/PlayerNear 记忆，纯 Domain 无 EventBus 依赖）；视图层（Game.View，本任务白名单外）零改动——PlayMode 演示直接经 `NpcUtilityView.Agent` 泵入，证明现有场景资产即刻获得态度层。

### 3. 可证伪证据矩阵（30 EditMode + 1 PlayMode，全部 Passed）

| 用例 | 位置 | 可证伪断言（手算锚） |
|---|---|---|
| **同事件异反应（核心）** | NpcAttitudeBehaviorTests.cs:70 | 同一 AttackedBy（同型/同效价/同时间域/同源）：胆小(勇敢 0.5) fear 1.2→1.0 → **Flee 3.0**；勇敢(2.0) anger 1.6→1.0 → **Counter 1.9**；后续 5 拍反应互斥 |
| 同事件 CFG 行驱动 | NpcAttitudeBehaviorTests.cs:126 | brave 行→Counter 1.9 / lethargic 行→Flee 3.0（配置驱动） |
| 调制可逆 | NpcAttitudeBehaviorTests.cs:181 | 新伤→Flee；同事件回拨 40s（2 半衰期）→恐惧 0.30、逃跑 0.48<0.7 → 与无记忆基线同选择；记忆仍在（衰减≠删除） |
| 兼容性 | NpcAttitudeBehaviorTests.cs:154 | 无记忆：Flee/Counter 恰 0 分、20 拍永不登顶、态度恒 Neutral |
| 刺激门控 | NpcAttitudeBehaviorTests.cs:213 | 有恐惧无威胁感知 → 不逃（情绪需要刺激） |
| 信任增益 | NpcAttitudeBehaviorTests.cs:228 | 两次赠礼 trust 1.0 → Follow 0.7→1.05（手算） |
| 半衰期纯函数 | NpcMemoryAttitudeTests.cs:31 | 1→0.5→0.25→0.125；age 严格单调；非正半衰期抛 |
| 容量滚动 | NpcMemoryAttitudeTests.cs:87 | 33 条→恰 32 条、最旧（t=0）被丢、队尾 t=32 |
| 聚合公式 | NpcMemoryAttitudeTests.cs:125,149,180,194 | 类型过滤 + 衰减加权和 −1.5 手算；混合条目 0.6/0.8/0.25 手算；钳制 1.0；空记忆→Neutral |
| 同源联动 | NpcMemoryAttitudeTests.cs:203,222,234 | 勇敢恐惧系数 0.3<胆小 1.2、愤怒 1.6>0.4；null 行→中性；增益不跨维度泄漏 |
| 调制公式 | NpcMemoryAttitudeTests.cs:245-269 | FleeDesire(1,1)=3 / (0.5,0.5)=0.5；CounterDesire(1,0)=2.5、(1,1)=0.5、(0.8,0.6)=0.9152；FollowTrustGain 1→1.5 |
| 触发源 | NpcMemoryAttitudeTests.cs:272-334 | EnemyAttackRequested 视野内→SawDeath 记忆（source=enemy-1，恐惧 0.4 手算）、视野外→无；ItemPickedUp 视野内→信任 0.1、无主/视野外→忽略；非法半径抛 |
| **PlayMode 场景** | NpcAttitudeScenePlayModeTests.cs（Game.View.PlayMode.Tests） | SCN_Utility 五 CFG NPC 同场攻击：首拍 a/b/c/e→Flee、d(brave)→Counterattack（手算锚）；10 拍窗口仅 d 反击、d 绝不逃跑、≥3 恐惧者真实逃跑；截图 `Docs/Parity/npc_attitude_divergence.png`（**"npc-b:Flee" vs "npc-d:Counterattack" 标签同框可辨**） |

### 4. 自测记录与环境备注

| 项 | 命令/文件 | 结果 |
|---|---|---|
| EditMode 全量 | `-runTests -testPlatform EditMode` → `TempLogs/aaai01_editmode.xml` | **1031/1031/0，exit 0**（2026-11-08 18:40Z）。基线 1001（HANDOFF 983 + 并行 A-AND-09 增量 18）+ 本任务 30，只增不减；30 条逐名核对全 Passed |
| PlayMode 全量 | `-runTests -testPlatform PlayMode` → `TempLogs/aaai01_playmode.xml` | **80/80/0，exit 0**（终态复跑 18:47Z，含本任务场景用例+截图再生；基线 79 + 本任务 1） |
| 架构校验器 | `-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine` → `TempLogs/aaai01_arch.log` | **exit 0，违规数 0，结果 PASS**（:335-336）——新增 Memory 命名空间纯度过审（零 UnityEngine） |
| 文件规范 | 全部触及文件 | UTF-8 无 BOM、LF、末尾换行（逐文件 `file`+od 实测）；一类型一文件；命名/私有字段 `_camelCase`/公共 API XML 注释合规 |

**对既有用例的唯一触碰（披露）**：`NpcUtilityAgentTests.InvalidSetup_Throws`（NpcUtilityAgentTests.cs:231）的默认候选集**清点断言** 5→7——本任务按简报把 Flee/Counterattack 注册进 NpcUtilityAgent 默认候选（无态度时恒 0 分、旧行为逐位兼容，见 2.5），清点数随设计事实更新并加注释；该文件其余用例及其余全部既有用例**零改动通过**。

**环境噪声记录（非本任务回归）**：①与并行 A-AND-09 代理共用 Unity：其长时间持锁导致 4 次批量启动撞锁退出 21（"another Unity instance"）——按纪律改为 2s 粒度盯 `UnityLockfile` 空隙抢启动，全部运行最终干净拿到锁，无强杀他人进程；②首跑发现其未收口的 `CrowdLodPipelinePlayModeTests.cs:166` Vector3−Vector4 编译错误阻断全项目编译——**未代改**（对方代理随即自行修复，本代理仅重试）；③首跑 Unity 曾在写完日志后僵尸不退（PID 40000，日志含完整 Shutdown 序列）→ 按纪律 taskkill，证据以重跑 XML 为准；④Edit/PlayMode 均以最终代码态复跑，主张基于最终态 XML。

**遗留与建议**：Trust 维度已聚合、已泵入快照并调制 Follow（增益可测），社交类行动（结伴/送礼回礼）留给 A-AI-02 GOAP 社交协调层；`NpcMemoryFeed` 目前由 Owner 手动喂事件，接 EventBus 订阅是 View/Bootstrap 层一行事（白名单外，未动）。

## 验收清单（主控写）

- [x] V1 EditMode 全绿（数字写入结论）；核心用例真实（同事件异反应/衰减/滚动——行号）
- [x] V2 聚合公式纯函数+半衰期断言（行号）；态度调制效用触点（NpcUtilityAgent 行号）
- [x] V3 个性×态度联动用例（勇敢 NPC 同事件不同反应——行号）
- [x] V4 Domain 纯度+Validate exit 0（自跑）；既有用例零改动通过
- [x] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

**验收: PASS**（2026-11-08，验收子代理独立亲跑，与施工不同代理；除下述变异复做的临时改动外未改任何产品/测试代码，变异已逐字恢复并以复跑 XML 证明）

- **V1 PASS** —— EditMode 亲跑 **1031/1031/0**（`TempLogs/aaai01_accept_editmode.xml`；首跑 XML 完整落盘后 Unity 僵尸不退，按纪律 taskkill，证据以 XML 为准）；PlayMode 亲跑 **80/80/0 且 exit 0 干净退出**（`TempLogs/aaai01_accept_playmode.xml`）。A-AI-01 新 30 条逐名全 Passed（NpcMemoryAttitudeTests 23 + NpcAttitudeBehaviorTests 7，逐名清单见本验收 XML）；A-AND-09 新 12 条（CrowdLodPipelineTests）同场全绿；PlayMode 本任务场景用例 `NpcAttitudeScenePlayModeTests.SameAttackScene_FearfulNpcsFlee_BraveNpcCounterattacks` Passed（0.78s，截图当场再生）。既有套件无删减：UtilityBrainTests 10 / NpcUtilityAgentTests 11 / UtilityCurvesTests 7 / UtilityPersonalityTests 13 / GoapPlannerTests 13 与 A-TECH-04 验收口径逐一对上（只增不减）。数字调和：1031 = 既有 989 + 本任务 30 + A-AND-09 12（施工报告"基线 1001=983+18"中的 18 实为 12 A-AND-09 + 6 HANDOFF 快照漂移，净算式与总数无误，见瑕疵①）。
- **V2 PASS** —— `NpcAttitudeMath.cs:43-56` `DecayWeight = Math.Pow(0.5, age/halfLife)`（:55），非正/NaN/∞ 半衰期抛（:45-48），未来时间戳按满权重（:50-53）；`Evaluate`（:72-101）按类型系数 `profile.FearGain/AngerGain/TrustGain` × 衰减加权（:95-97），threat=max(0,−valence)/goodwill=max(0,valence)（:93-94），维度钳制 0..1 落在 `NpcAttitude` 构造（NpcAttitude.cs:36-41 + Clamp01 :75-88）；容量 32 滚动：`NpcMemory.cs:21`（DefaultCapacity=32）+ `:49-60`（满则 `RemoveAt(0)` 丢最旧），断言 `NpcMemoryAttitudeTests.cs:87`（33→恰 32、t=0 被丢、队尾 t=32）+ 非默认容量 :110；手算锚全过（1→0.5→0.25→0.125 :31；聚合 −1.5 :125；混合 0.6/0.8/0.25 :149；钳 1.0 :180；空记忆 Neutral :194）。
- **V3 PASS** —— 调制触点逐行核实：恐惧→逃跑增益 `NpcUtilityAgent.cs:610-621`（:620 `FleeDesire = proximity×fear×(1+2×fear)`）；愤怒→反击增益+恐惧抑制 `:628-640`（:639 `CounterDesire = proximity×anger×(1+1.5×anger)×(1−0.8×fear)`）；信任→Follow `:566-575`（:573 `FollowTrustGain = 1+0.5×trust`，trust 0 ⇒ ×1 逐位兼容）；公式与 `NpcAttitudeMath.cs:111/127/141` 本体及 XML doc、常量（FearFleeBoost 2 / AngerAttackBoost 1.5 / FearAttackSuppress 0.8 / TrustFollowBoost 0.5）逐项一致；配套全在场：候选注册 :311-312、Note* 钩子 :438-459、每拍重推 :485、快照泵入 :533-543、`NPCWorldState.cs` 纯增量（新 Keys Fear/Anger/Trust/ThreatDistance + HasThreat，diff 核对无删改）。
- **V4 PASS** —— 核心用例锚点亲核：`NpcAttitudeBehaviorTests.cs:70` 同一 AttackedBy（:79-80 同拍写入，同型/同效价/同时间域/同源）→ :89 胆小 **Flee**（手算 3.0 = 1×1×(1+2×1)）、:90 勇敢 **Counter**（手算 1.9 = 2.5×(1−0.8×0.3)），后续 5 拍反应互斥（:98-102）；CFG 行驱动 :126-148（brave 行→Counter 1.9 / lethargic 行→Flee 3.0，`Assets/Configs/CFG_NpcPersonality.asset` 真行加载）；个性系数由 CFG 行派生 `NpcAttitudeProfile.ForPersonality`（**NpcAttitudeProfile.cs:97-113**：恐惧增益 ÷BraveryWeight、受击愤怒 ×BraveryWeight、信任 ×SocialWeight，null→Neutral，SafeWeight 下限 0.1）。**变异复做 1 次**：`FearFleeBoost 2f→−2f`（临时，已恢复）→ EditMode **1031→1026 通过/5 红**——`SameAttackedByEvent_FearfulNpcFlees_BraveNpcCounterattacks`、`ConfigRows_BraveCounterattacks_LethargicFlees`、`FleeDesire_FearGatesAndAmplifies_HandComputed` 等 5 条恰红（`TempLogs/aaai01_accept_editmode_MUTANT.xml`）→ 逐字恢复（:24 复核，文件内零变异痕迹）→ 复跑 **1031/1031/0**（`TempLogs/aaai01_accept_editmode_RESTORED.xml`）。调制公式真实可证伪、用例真实咬合。
- **V5 PASS** —— Domain 纯度亲 grep：新 `Domain/AI/Memory/*.cs` 8 文件 + 改动的 NPCWorldState/NpcUtilityAgent 零 UnityEngine（`Domain/AI` 下仅 A-TECH-04 既有 SO 定义 NPCPersonalityDef/NpcPersonalityTable 含，本任务未触碰）；三校验器验收亲跑全 **exit 0、违规 0、PASS**：ArchitectureValidator（`aaai01_acc_arch2.log:335-336`，含 Game/Domain 纯度）、ConfigValidator（`aaai01_acc_config.log:475-476`）、AssetNameValidator（`aaai01_acc_naming.log:334-335`）；文件规范抽查 12 个触及文件全 UTF-8 无 BOM（首 3 字节 od 实测）+ LF + 末尾换行（`file` 逐个），Memory/ 8 个 .cs.meta 齐全、一类型一文件、公共 API XML 注释在场；施工报告四节齐全（简报/施工报告/验收清单/本节），白名单内核对：触及文件均在 `Domain/AI/**`、`Tests/**`、本日志、`Docs/Parity` 证据图（既有惯例）之内，`Configs/**` 按决策 2.3 零改动。

**验收瑕疵（不降级，留痕）**：①施工报告 §4"基线 1001（HANDOFF 983 + 并行 A-AND-09 增量 18）"归因不精——A-AND-09 实为 12 条 EditMode，另 6 条为 HANDOFF 983 快照滞后（committed 树实测既有 989），净算式 989+12+30=1031 与双方施工报告总数一致，非测试完整性问题；②`Docs/Parity` 4 张 PNG（npc_attitude_divergence / crowd_r9_15_off / crowd_r9_15_on / utility_personality_goap_chain）的工作区改动时间戳（19:02-03Z）系**本验收 PlayMode 复跑**当場再生所致（像素证据类用例每跑重写），非施工越权改动；③本验收三轮 Unity 批处理（EditMode/变异/恢复）均遇"结果文件完整落盘后 Unity 僵尸不退"，均按纪律 taskkill、证据一律以 XML/日志为准（与施工报告环境噪声③同源）；期间曾与并行 A-AND-09 验收代理的 runtests EditMode 共用 Unity 相遇一次（对方撞本方残留锁 exit 21，未强杀对方进程，等锁空隙后重跑）。

**验收裁决**：**PASS**——施工报告自测数字（EditMode 1031/PlayMode 80/校验器/截图）与验收亲跑结果全部一致，无虚报；V1-V5 全过，提交主控走 commit+闭环刷新。
