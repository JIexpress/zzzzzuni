# TASK-A-TECH-04 —— 效用 AI 第二阶段：个性权重（配置驱动）+ GOAP 前瞻原型

状态: doing | 派发: 2026-11-04 | 施工: general-purpose 施工子代理(第二次接手) | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

### 背景（用户批评，原话）

"npc 用 ai 得有一个条件判断和倾向吧，每个人的都不一样，或者叫权重，这个 demo 还是太简单了"——A-TECH-01 的三个 NPC 共用同一评估函数=复读机。本任务两层升级：**个性权重**（每 NPC 独立倾向）+ **GOAP 前瞻原型**（自主两步规划："工具在 A 处、目标在 B 处→先取工具再去修"）。

### 设计要点

1. **个性配置驱动**（配置系统实战——A-M1-02 的 ConfigTable）：`NPCPersonalityDef`（CFG_NpcPersonality 表）：每 NPC 一行权重（fatigueWeight/socialWeight/curiosityWeight/braveryWeight, 0.5~2.0）——UtilityBrain 分数=基础欲望曲线**×个性权重**（乘法或幂调制，报告选型）
2. **UtilityBrain 升级**：构造注入 Personality（无则默认全 1.0=旧行为兼容）；分带调试输出（每 NPC 的各行动得分排序可 dump）
3. **GOAP 最小规划器**（纯 C#）：`GoapAction{preconditions, effects, cost}` + `GoapPlanner.Plan(worldState, goal, actions, maxDepth=3)`——BFS/A* 最小实现；**实证两步规划**：场景新增"工具箱"（修理工具的前提）+"损坏装置"（修理目标）——单步不可行（无工具时修理动作 precondition 不满足）→ 规划器产出 [取工具→修理] 两步链 → NPC 执行链
4. **5 个不同个性 NPC 演示**（SCN_Utility 升级，Setup 幂等）：同场景同起点同世界状态 → 5 个 NPC 行为序列**两两不同**（个性分化实证，截图+状态序列）
5. 可证伪用例（EditMode）：同个性同状态→同选择（确定性回归）；**不同个性同状态→分化**（权重 1.5/0.5 的两个 NPC 选择不同——核心新证据）；GOAP 单步不可行→两步链产出；GOAP 无解→明确失败；GOAP 深度上限
6. 自测：EditMode+PlayMode 全绿（基线 928/58 只增不减）+ Validate exit 0（Domain 纯度）

### 约束

- 白名单：`Assets/ZFrame/Gameplay/AI/**`（GOAP 若框架级）、`Assets/Game/Domain/AI/**`、`Assets/Game/View/**`、`Assets/Configs/**`（CFG_NpcPersonality）、Tests/**、本日志
- 不改 A-TECH-02/03 产物；Unity 批处理纪律；不 git commit；UTF-8 无 BOM/LF

## 施工报告（施工代理写）

**施工: general-purpose 施工子代理（第二次接手）** ｜ 2026-11-08 ｜ 未 git commit

### 1. 接手审计与对前代理工作的裁决

**接管现场**（git status + 逐文件审计）：首任代理已完成**全部产品代码与测试代码**，但从未实跑——本任务的 6 个红是纯"资产/场景未生成"缺口。

| 盘上产物 | 审计结论 | 裁决 |
|---|---|---|
| `Assets/ZFrame/Gameplay/AI/`：GoapState（STRIPS 原子集）/ GoapAction（前置+增删效果+代价，矛盾效果构造即抛）/ GoapPlan（链+总代价）/ GoapPlanner（BFS 最少步、注册序 tie-break、visited 集防环、maxDepth 封顶、无解显式 null） | 完整、纯 C#、注释含契约；架构纯度黑名单 token 零命中 | **采纳** |
| Domain：NPCPersonalityDef（CFG 行，4 权重 `[field: Range(0.5,2)]`）/ NpcPersonalityTable（ConfigTable&lt;T&gt;+FindRow 降级 null）/ UtilityTraits（4 特征标签）/ UtilityGoapAtoms（3 原子） | 完整；Game.Domain 已引用 ZFrame.Data，ScriptableObject 不在纯度黑名单 | **采纳** |
| UtilityBrain：构造注入 Personality（null=全 1.0）、`加权=原始分×特征权重`、PersonalityWeightFor、FormatRanking 分带 dump | 与 A-TECH-01 逐位兼容路径成立（EditMode 实证） | **采纳** |
| UtilityAction（trait 标签，未知标签抛）/ NPCWorldState（+Toolbox/Device/Broken 3 键）/ NpcUtilityAgent（Repair 中立职责 + GOAP 链执行：EnsureChain 规划→逐步走→修复事实回流）/ NpcUtilityView（CFG 表接线+传感器+反激活即全局事实） | 完整，手算复核欲望曲线与 Tuning 实值一致 | **采纳** |
| 配置管线：`Assets/Configs/CSV/npc_personality.csv`（5 行）+ `NpcPersonalityConfigSetup`（CsvImporter.Import 幂等） | 完整；`Assets/Configs/CFG_NpcPersonality.asset` **未生成**（简报预警项①） | 采纳，缺口本代理补 |
| `UtilitySceneSetup`（5 NPC+工具箱+损坏装置+表引用） | 完整；`SCN_Utility.unity` **未再生**（仍是 A-TECH-01 三 NPC 版，简报预警项②） | 采纳，缺口本代理补 |
| 测试：UtilityPersonalityTests **13** / GoapPlannerTests **13** / PlayMode 3 例 | 已写未跑；含 3 个真缺陷（下述） | 采纳，缺陷本代理修 |

**缺口定位的旁证**：2026-11-08 02:02 并行 ANIM 验收全量实跑显示 EditMode 960 中 3 红 / PlayMode 65 中 3 红，**6 红全部是本任务用例**（3 红=CFG 资产缺失、3 红=场景旧版），其余 957/62 全绿——与"只差生成+再生"的审计结论互证。

**本代理修复的前代理测试真缺陷（3 处，均在 Tests/** 白名单内）**：
1. **PlayMode①顺序依赖**：`sequences[i]` 按 `FindObjectsOfType` 返回序落位（不保证 npc-a 在前），首拍锚点可能整体错位 → 改为按 `IdIndex(view.NpcId)` 落位，锚点与 NPC 身份严格绑定。
2. **PlayMode②事件丢失**：场景激活后 ManualDrive 接管前若有真实帧先跑，Repair 首拍在订阅前就完成规划，`ChainPlanned` 丢失 → 断言"每个修复者必须先规划后执行"恒红。修法双保险：`LoadSceneAndCollectViews` 场景激活后**立即**收视图并置 ManualDrive（幂等复收）；订阅后 `SetDeviceBroken(false/true)` 重挂链条，重规划事件在受控驱动下必被捕获。
3. **PlayMode②轨迹断言用 3D 距离**：`sqrMagnitude<=0.36` 恒含 ΔY²（NPC Y=1 vs 工具箱 Y=0.35 → 0.4225>0.36），任何 NPC 都到不了"工具箱" → 改为水平(XZ)距离（与 EditMode 2D 版同语义），半径 0.6 > 到达半径 0.5。

**对前代理继承变更的裁决**：前代理把 A-TECH-01 的 2 条 PlayMode 用例（3-NPC 分化/疲劳往返）升级为 3 条 5-NPC 用例——**正当**：场景改 5 NPC 后旧断言（恰 3 个 view）必红；旧①本质由更强的 600 拍两两分化版覆盖；旧②"疲劳→Rest→恢复→切回"域级回归仍在 `NpcUtilityAgentTests`（EditMode）。A-TECH-02/03 产物零触碰。

### 2. 交付内容与分化实证（对照简报设计要点 1-5）

1. **个性配置驱动（配置系统实战）**：`npc_personality.csv` → `CsvImporter.Import`（A-M1-02 管线，`ZFrame.Data.ConfigEditor`）→ `Assets/Configs/CFG_NpcPersonality.asset`，行=balanced/sociable/curious/brave/lethargic（权重向量互异，测试断言两两不同）。Range 0.5~2.0 由 ConfigValidator 反射强制（实跑违规 0）。运行期消费走 `NpcPersonalityTable.FindRow`（未知 id 降级全 1.0，不抛）。
2. **调制选型=乘法**（报告口径）：单调（不翻转排序方向）、权重 1.0 恰为恒等（旧行为逐位兼容，`AllOnesDef_BehavesLikeNeutral` 断言 dump 逐字符相等）、0 欲望乘后仍 0、0.5~2.0 界定单特征最多 4 倍爬位。无标签行动（Repair 职责）不吃权重——个性只移偏好，不移义务。
3. **GOAP 两步链**：修理前置 `has_tool` 单步不可满足 → 规划器必产出 `[TakeTool → RepairDevice]`（EditMode/PlayMode 双断言链内容与 `Format()` 逐字符）；执行落空间顺序：先到工具箱（授予 has_tool 原子）后到装置（`DeviceRepaired` → Owner 反激活装置 → 全体下一拍 broken=false，零视图耦合）。**集成路径（谁调谁）**：UtilityBrain 选意图（Repair 登顶）→ `NpcUtilityAgent.ExecuteRepair` 首胜拍 `EnsureChain` 调 `GoapPlanner.Plan` → 链执行器逐步走。规划器不挑行动，Brain 不管前瞻。
4. **5 个个性 NPC 分化实证**：同出生点 (0,1,0)、同起始状态（疲劳 0）、同世界（玩家/掉落/工具箱/装置同位置）——唯一差异是 CFG 行。
   - **首拍锚点（手算，EditMode+PlayMode 双断言）**：balanced/brave/lethargic → Repair 0.9（压过 Pickup 0.85）；sociable → Follow 0.7×2.0=**1.4**；curious → Pickup 0.85×2.0=**1.7**。
   - **600 拍序列两两不同**：EditMode `FiveConfigPersonalities_IdenticalWorld_PairwiseDivergentSequences`（纯域级、共享世界控制器）+ PlayMode `SameStart_FivePersonalityNpcs_PairwiseDivergentSequences`（场景级）均断言 10 个两两 `SequenceEqual=false`。
   - **权重门控（同状态泵入分化）**：疲劳泵 0.9 后五者四行为——balanced Rest 1.0 / sociable Follow ≈0.99 / curious Pickup 1.7 / brave Repair 0.9 / lethargic Rest 2.0（`FatiguePump_SameStateFiveDifferentChoices_ByWeights` 断言 4 distinct）。
   - **截图（A-R9-15 口径：离屏探针相机+RenderTexture+ReadPixels→PNG）**：`Docs/Parity/utility_personality_divergence.png`（60s 末态：两青 Sociable 段跟玩家、蓝色 lethargic 在休息点、其余散开——同起点五者位置/行为肉眼可辨）+ `utility_personality_goap_chain.png`（红色 Repair NPC 链中段、装置尚未修复）。两张已亲眼查验非空非黑。
   - **GOAP 链空间顺序**：EditMode 断言 toolboxTick<deviceTick（2D 距离）+ PlayMode 轨迹同断言（XZ 距离）。
5. **可证伪用例矩阵（EditMode 26 条 = 13 个性 + 13 GOAP）**：同个性同状态确定性回归（含 CFG 行注入 + 100 拍轨迹逐拍相等）；不同个性分化（权重 2.0/0.5 手算值断言 + 阈值下压空转 0.08×0.5<0.05）；GOAP 两步链产出/前置已满足单步/无解 null×2/深度上限 1 败 2 成/maxDepth=0 抛/已满足空计划/同输入同计划/不改输入/互逆动作 visited 防环/非法入参/矛盾效果抛。

### 3. 自测数字（全部实跑取证）

| 项 | 命令 | 结果 |
|---|---|---|
| CFG 生成 ×1 | `-executeMethod Game.View.Editor.NpcPersonalityConfigSetup.SetupFromCommandLine` | exit 0，added=5 |
| CFG 幂等复跑 ×1 | 同上 | exit 0，**added=0, updated=5, errors=0**（不重复建行） |
| 场景再生 ×1 | `-executeMethod Game.View.Editor.UtilitySceneSetup.SetupFromCommandLine` | exit 0，5×NPC_A~E + Toolbox + BrokenDevice，5 处 `_personalityTable` guid 接线+5 个个性 id |
| 场景幂等复跑 ×1 | 同上 | exit 0，仍 5 NPC；EditorBuildSettings 全场景保留（SCN_Anim/SCN_Assembly 未挤掉） |
| **EditMode 全量** | `-runTests -testPlatform EditMode` → `TempLogs/utech04_editmode.xml` | **960/960/0**（2026-11-08 02:09Z；分解 928 基线 + 本任务 26 + ANIM 6，只增不减）。本任务 26 条逐条核对全 Passed（清单见 utech04_editmode.xml）。PlayMode 用例定稿后再复跑一次终态：`utech04_editmode2.xml` 02:35Z **960/960/0**，主张基于最终代码态 |
| **PlayMode 全量** | 同法 → `TempLogs/utech04_playmode3.xml` | **65/65/0**（02:26Z；分解 58 − 前代理替换 2 + 本任务 3 + ANIM 6）。第 1 轮 64/65（缺陷 2）、第 2 轮 64/65（缺陷 3）、修复后第 3 轮全绿 |
| arch 校验器 | `-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine` | **exit 0，违规 0**（`TempLogs/arch_report.txt` 10:30:22；asmdef 矩阵 + Game/Domain 纯度均过） |
| config 校验器 | `ZFrame.Data.ConfigEditor.ConfigValidator.ValidateFromCommandLine` | **exit 0，违规 0**（全 ScriptableObject 反射扫描含 CFG_NpcPersonality 的 Range 0.5~2.0） |
| 文件规范 | 脚本核查 19 个任务文件 | 全部 UTF-8 无 BOM、LF |

**环境噪声记录（非回归）**：①EditMode 首跑 Unity 写完 xml 后僵尸不退 → 按纪律 taskkill，证据取自本次新写 xml；②`Docs/Parity/crowd_r9_15_on.png` 是 A-R9-15 用例全量跑自动再生的证据文件（接管时已因前轮实跑呈 M）→ 按 §3.7 先例 `git checkout --` 恢复 HEAD，机制已披露；③`utility_tech01_*.png` 的 M 早于本代理会话（08:30），未触碰、留主控聚类。

### 4. 遗留与边界（如实口径）

- **batchmode 无屏**：分化截图是离屏探针相机像素证据（480×270，A-R9-15 同口径），非主相机目测帧；真实 GPU 目测仍属"编辑器人工取证"遗留（A-R9-15 已挂账，非本任务新增）。
- **GOAP 是最小 BFS**：最少步优先、`TotalCost` 仅随计划携带未参与 A*——简报明示允许；升级点已留（代价模型在 GoapAction.Cost / GoapPlan.TotalCost 上）。
- **修复事实的并发语义**：装置为共享世界对象，同拍多 NPC 抵达时由视图步进序决定唯一修复者（先步进者修复→反激活→其余链作废），400 拍用例实证稳定；"修复者归属"未做确定性承诺（FindObjectsOfType 序不保证），用例断言不依赖该序。
- **前代理 asmdef 改动**：`Game.Domain.Tests.asmdef` +`ZFrame.Data`（CFG 表加载必需），在 Tests/** 白名单内。
- **CSV 权重列与 ConfigRow.Id 约定**：首列 Id 由 CsvImporter 管线约定；若未来加第七/八个特征，需同步 UtilityTraits + NPCPersonalityDef + CSV 表头 + FormatRanking（无运行期反射耦合）。
- 本任务未动：A-TECH-02/03 产物、A-ANIM-01 产物（Animations/、SCN_Anim、Kenney 系列、NpcAnimatorPlayModeTests）、`zrender.dll`。未 git commit（治理规则）。

## 验收清单（主控写）

- [x] V1 EditMode 全绿（数字写入结论）；核心新用例真实（权重分化/GOAP 两步链/无解——行号）
- [x] V2 **分化实证亲核**：PlayMode 或 EditMode 状态序列——5 个同状态 NPC 行为两两不同（截图+序列）
- [x] V3 GOAP 规划器：两步链/无解/深度上限用例；规划器与 UtilityBrain 的集成路径（谁调谁）审查
- [x] V4 个性表走 ConfigTable（配置系统实战——CFG 表读取行号）；Validate exit 0（自跑）
- [x] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

验收: general-purpose 验收子代理 ｜ 日期: 2026-11-08 ｜ 结论: **PASS**

验收方法独立性：与施工（第二次接手代理）不同代理；未改任何产品/测试代码，仅新增本节与本日志头部字段。EditMode/PlayMode/三校验器/CFG 幂等复跑全部由验收亲跑（Unity 2022.3.62f3c1 批处理、测试无 -quit、证据以 results.xml 为准）。证据文件：`TempLogs/acc_utech04_editmode.xml`、`TempLogs/acc_utech04_playmode.xml`、`TempLogs/acc_utech04_{arch,config,naming,cfg3}.log`。

- **V1 PASS**：亲跑 EditMode **960/960/0**（acc_utech04_editmode.xml，2026-11-08 10:40；EditMode 首跑 XML 落盘后 Unity 僵尸不退按纪律 taskkill，证据取本次新写 XML）、PlayMode **65/65/0**（acc_utech04_playmode.xml，exit 0 干净退出）。本任务 26 条 EditMode 用例逐名核对全 Passed（13 GoapPlannerTests + 13 UtilityPersonalityTests），核心新用例逐名核到：权重分化 `UtilityPersonalityTests.DifferentPersonalities_SameState_DifferentWinners`（断言 0.85×2=1.7 与 0.7×2=1.4 手算值）+ `FiveConfigPersonalities_IdenticalWorld_PairwiseDivergentSequences`；GOAP 两步链 `GoapPlannerTests.SingleStepInfeasible_PlannerProducesTwoStepChain`（断言 `TakeTool -> RepairDevice` 逐字符）+ `UtilityPersonalityTests.RepairChain_PlannerProducesTwoSteps_AgentWalksToolboxThenDevice`（toolboxTick<deviceTick 空间顺序）；无解 `UnreachableGoal_ReturnsNull` + `PreconditionsUnmetAnywhere_ReturnsNull`；深度上限 `DepthCap_TooShallowFails_DeeperSolves`（maxDepth=1 null / =2 可解）+ `DepthCapZero_Throws`。PlayMode 3 条逐名 Passed（分化 600 拍两两 / 两步链场景执行 / 权重门控 4 distinct）。前序套件无删减：UtilityBrainTests 10 + NpcUtilityAgentTests 11 + UtilityCurvesTests 7 全绿在场。
- **V2 PASS（分化实证亲核）**：EditMode `FiveConfigPersonalities_IdenticalWorld_PairwiseDivergentSequences`（UtilityPersonalityTests.cs:219-279）断言 5 个 CFG 行注入的 agent 同一世界泵流 600 拍、10 个两两 `SequenceEqual=false` + 首拍锚点（balanced/brave/lethargic→Repair 0.9、sociable→Follow 1.4、curious→Pickup 1.7，手算复核吻合）；PlayMode `SameStart_FivePersonalityNpcs_PairwiseDivergentSequences`（UtilityNpcScenePlayModeTests.cs:55-105）场景级同断言（同出生点 ±0.5 内两两 <0.5）。两张截图验收亲眼查验非空非黑：`Docs/Parity/utility_personality_divergence.png`（12865 B——青色 Sociable 段跟玩家、蓝色 lethargic 在休息点、位置肉眼可辨）与 `utility_personality_goap_chain.png`（10933 B——红色 Repair NPC 链中段、装置未修复）。CFG 表走 CsvImporter 管线实证：`npc_personality.csv` 5 行 → `NpcPersonalityConfigSetup.Setup`（NpcPersonalityConfigSetup.cs:39-42 `CsvImporter.Import`）→ CFG_NpcPersonality.asset 5 行 YAML 逐行核对权重向量互异且与 CSV 一致；**幂等验收亲跑复现**：`acc_utech04_cfg3.log` exit 0，`added=0, updated=5, missingInCsv=0, errors=0`（施工方两轮 added=5 → added=0/updated=5 亦在 utech04_cfg.log/cfg2.log 在案）。
- **V3 PASS（GOAP 规划器审查）**：`GoapPlanner.Plan`（ZFrame/Gameplay/AI/GoapPlanner.cs:54-123）为 BFS 前向搜索——Queue frontier（:83-84）逐层展开（:86-120），首个满足目标的状态即返回（:110-113）保证最少步数、注册序 tie-break（:94 + :10-11 契约，`SameInput_SamePlan_Deterministic` 锁死）；无解显式返回 null（:122，`UnreachableGoal_ReturnsNull` 断言"不得返回空计划伪装成功"）；深度上限 `node.Chain.Count >= maxDepth` 不再展开（:89-92），maxDepth<1 抛（:71-74）；visited 集（GoapState.ToKey 排序规范化键，:82/:115-118 + GoapState.cs:87-103）防互逆动作环（`MutuallyUndoingActions_TerminateViaVisitedSet`）；不改输入（clone :84/:102 + `Planner_DoesNotMutateInputs`）；矛盾效果构造即抛（GoapAction.cs:42-50）。**集成路径（谁调谁）**：UtilityBrain 选意图（UtilityBrain.cs:151-216，加权=原始分×特征权重 :167）→ NpcUtilityAgent.ExecuteRepair 首胜拍 `EnsureChain`（NpcUtilityAgent.cs:551）→ `GoapPlanner.Plan`（:603，DefaultMaxDepth=3）→ ChainPlanned 事件（:607）→ 链执行器逐步走（:557-578，TakeTool 步到工具箱半径内置 has_tool，RepairDevice 步到装置置 broken=false + DeviceRepaired）→ Owner 反激活装置（NpcUtilityView.cs:244-251）→ 全体下一拍 PumpWorld broken=false（:198-204）。规划器不挑行动、Brain 不管前瞻，分层干净。饿死防护另有门控：无工具箱感知 Repair 欲望归零（NpcUtilityAgent.cs:454-470，`BrokenDeviceWithoutToolbox_NoRepairDesire` 断言改选 Follow）。
- **V4 PASS（前代理 3 缺陷修复评估，均语义正确）**：①顺序依赖——`sequences[IdIndex(view.NpcId)]` 按身份落位（UtilityNpcScenePlayModeTests.cs:66-71），消除 FindObjectsOfType 返回序与断言锚点的耦合，修法正确且最小；②激活帧规划丢失——双保险成立：`LoadSceneAndCollectViews` 场景激活后立即 `CollectAndTakeOver`（:253-264，ManualDrive 先于任何真实帧 Update，幂等复收防晚激活视图）+ 订阅后 `SetDeviceBroken(false/true)` 重挂链（:132-135，SetDeviceBroken(false) 置 `_chain=null` NpcUtilityAgent.cs:335-343 → 下次 Repair 胜拍必重规划发 ChainPlanned，事件在受控驱动下必被捕获），语义正确；③ΔY 轨迹——几何核实成立：NPC Y 恒 1（出生 (0,1,0)、ApplyTransform 保留 y NpcUtilityView.cs:207-211），工具箱 Y=0.35/装置 Y=0.3（UtilitySceneSetup.cs:45-46）→ 3D 距离 ≥0.65 → sqrMagnitude ≥0.4225 > 0.36 恒红，旧断言确属不可通过；改 XZ 水平距离（UtilityNpcScenePlayModeTests.cs:195-203/:298-303）与域内 Vec2 平面移动语义一致，半径 0.6 > RepairArriveRadius 0.5 余量成立。
- **V5 PASS**：三校验器验收亲跑全 exit 0 违规 0——ArchitectureValidator（含 Game/Domain 纯度）、ConfigValidator（含 CFG_NpcPersonality Range 0.5~2.0 反射扫描）、AssetNameValidator（`acc_utech04_{arch,config,naming}.log`）；20 个任务文本文件逐字节核验全 UTF-8 无 BOM + LF；施工报告四节齐全（接手审计/交付内容与分化实证/自测数字/遗留边界），接手审计评估成立：其"首任半成品采纳 + 6 红纯资产缺口"结论有 ANIM 验收并行实跑旁证（3 红=CFG 缺失 + 3 红=场景旧版，与 TASK-A-ANIM-01.md 验收结论逐字一致）；白名单核查通过——A-TECH-02/03 产物零触碰（Assembly/Planet 套件在场全绿），Game.Domain.Tests.asmdef +ZFrame.Data 在 Tests/** 白名单内，EditorBuildSettings 全场景保留仅 ANIM 并行新增 SCN_Anim。

**验收瑕疵（不降级，留痕）**：①施工报告 §2.4 引用的 EditMode/PlayMode 用例文件行号按本次代码态核对全部存在、无漂移；②`UtilitySceneSetup` 场景幂等（每次重建）与 CFG 幂等（按 Id 更新）口径不同属各自约定，报告已如实区分；③无其他发现。施工报告自测数字（960/65/三校验器/幂等四项）与验收亲跑数字全部一致，无虚报。

**遗留移交（均为施工报告已如实声明的口径，非本任务新增）**：batchmode 无屏——分化截图为离屏探针相机像素证据，真实 GPU 目测仍属 A-R9-15 挂账的编辑器人工取证；GOAP 为最小 BFS（最少步优先、TotalCost 仅随计划携带未参与 A*，简报明示允许，升级点已留 GoapAction.Cost/GoapPlan.TotalCost）；同拍多 NPC 抵达共享装置时修复者归属由视图步进序决定（用例断言不依赖该序）；GOAP 第三步（更深的链/动态代价）未做（简报未要求）。
