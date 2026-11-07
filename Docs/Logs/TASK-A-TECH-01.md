# TASK-A-TECH-01 —— 自主 NPC 效用 AI 最小切片

- **轨道**：A / TECH-ROADMAP 方向一（效用 AI）
- **状态**：doing（施工完成待验收）
- **派发**：主控，2026-11-08（**接手续建**：首次派工遇瞬时错误中断，盘上已有半成品）
- **施工**: general-purpose 施工子代理(接手续建)
- **验收**: general-purpose 验收子代理

## 一、任务简报（主控简报摘录）

**切片目标（ROADMAP 方向一）**：3 个 NPC 在场景里自主决策——系统提供"可能的行动+代价/收益评估函数"，NPC 根据自身状态与世界状态**自己排序选择**（疲劳→休息；发现掉落物→捡；玩家远→跟随；否则巡逻）。

**实现要求**：
- 纯 C# 效用核（Game/Domain/AI/Utility/）：UtilityAction/UtilityBrain/NPCWorldState——Tick 评估→选最高分→执行；阈值下 Idle；tie-break 确定性
- 场景演示（SCN_Utility 或并入既有 Setup 幂等生成）：3 NPC+休息点/掉落物/路点——自主分化
- 可证伪用例（EditMode）：同状态输入→排序确定；状态改变→选择改变（自主性证据序列断言）；PlayMode：分化行为截图
- 自测：EditMode 全绿（基线 928+新增）+ PlayMode 全绿（含 A-TECH-02 的 8 条装配用例，合计 ≥56）+ ValidateFromCommandLine exit 0
- 约束：Domain 零 UnityEngine（校验器 exit 0）；白名单 Assets/Game/**、Assets/ZFrame/Gameplay/**（若框架级）、Tests/**、本日志；不 git commit；UTF-8 无 BOM/LF

## 二、施工报告

### 2.0 接手审计（半成品逐件核对，先于一切续建）

首次派工的代理在 2026-11-08 03:17–03:49 已落盘 12 个文件后中断。逐件审计结论：

| 盘上半成品 | 审计结论 | 处置 |
|---|---|---|
| `Assets/Game/Domain/AI/Utility/` 五件（UtilityAction/UtilityBrain/NPCWorldState/UtilityCurves/NpcUtilityAgent + .meta） | **完整且正确**：Tick 全量评分→严格大于扫描（注册序 tie-break）→执行；阈值下 Idle；冷却按调用方泵入的时钟；NaN 分数不参选；分数函数全纯。手算核查各欲望曲线与执行闭包，无需改动 | 原样保留 |
| `Assets/Game/Tests/Domain/` 四件（UtilityBrainTests 10 / NpcUtilityAgentTests 11 / NPCWorldStateTests 7 / UtilityCurvesTests 7 = **35 条**） | **完整**，含全部简报要求的可证伪断言（同状态同排序、tie-break 双向对拍、阈值 Idle、自主性序列断言） | 原样保留 |
| `Assets/Game/View/Npc/NpcUtilityView.cs` | **完整**：确定性 `ManualDrive`/`Step` 测试缝、`SelectionHistory` 自主性历史、掉落物感知泵 | 原样保留 |
| `Assets/Game/View/Editor/UtilitySceneSetup.cs` | 基本完整；发现缺陷 ③（NPC id 下划线） | 修 1 行 |
| `Assets/Scenes/SCN_Utility.unity` | 已生成但带着缺陷 ③ 的 id（`npc_a`），且 Build Settings 已注册 | 幂等重生成 ×2 |
| `Assets/ZFrame/Tests/PlayMode/UtilityNpcScenePlayModeTests.cs` | 两处缺陷：缺 `using UnityEngine.TestTools;`（**A-TECH-02 代理已按其日志 2.3-7 补上**，审计确认在位）；疲劳切换断言 0.5 错锁机制点（缺陷 ①） | 修断言 |
| `ProjectSettings/EditorBuildSettings.asset` | 已含 SCN_Utility（首次派工所加），与 SCN_Assembly 共存、Boot/Empty/Game 顺序未变 | 重生成复确认 |

**关键澄清（对简报口径的一处修正）**：简报写"EditMode 基线 928+新增"。实核：A-TECH-02 轮次跑出的 928 **已包含在途的本任务 35 条**（分解 = 既有基线 893 + 本任务 35 = 928），并非"928 之上再加"。本任务 EditMode 终态 **928/928/0** 全绿即达标，无第 33/35 条漏网。

### 2.1 交付摘要

| 项 | 结果 |
|---|---|
| 纯 C# 效用核 | `Game.Domain.AI.Utility` 五类（Action/Brain/WorldState/Curves/Agent），**零 UnityEngine**（arch 校验器纯度扫描 `Assets/Game/Domain` 违规 0）；唯一依赖 `ZFrame.Gameplay.Vec2`（同为引擎无关数学类型） |
| 演示场景 | `Assets/Scenes/SCN_Utility.unity`：地面/静态玩家/休息点/3 掉落物/4 路点标记/**同起点 (0,1,0) 三个 NPC**（A 疲劳 0.95+恋休息 1.6× / B 恋捡取 1.6× / C 恋巡逻 3×）；Setup 幂等生成 exit 0 ×2（04:56、05:03） |
| EditMode | **928/928/0**（含本任务 35 条；Unity exit 0，测试执行 5.7s） |
| PlayMode | **58/58/0**（既有 48 + A-TECH-02 装配 8 + 本任务 2；满足 ≥56；Unity exit 0，38s） |
| 三校验器 | arch / config / naming **全部 exit 0、违规 0**（终态复跑 05:06–05:07） |
| 截图证据 | `Docs/Parity/utility_tech01_divergence.png`、`utility_tech01_switch.png`（480×270，非空，三 NPC 可见） |

### 2.2 自主性证据（简报验收口径逐条对应）

1. **同状态输入 → 排序确定**（EditMode `UtilityBrainTests`）：同一 NPCWorldState 两次 Tick，`LastRanking` 逐项相同、赢家相同；降序排列；同分按注册序（`TieA/TieC` 对拍 + Alpha/Beta 交换注册序双向证明 tie-break 是注册序而非名字序）；NaN 分数按无欲望处理。
2. **状态改变 → 选择改变（序列断言）**（EditMode `NpcUtilityAgentTests`）：
   - `SameNpc_RestedPatrols_TiredRests_RecoveredPatrolsAgain`：**同一只 NPC** 满态→Patrol，`SetFatigue(0.95)`→Rest，恢复→Patrol——`ActionChosen` 事件序列断言 `[Patrol, Rest, Patrol]`，全程只泵状态、不改任何代码/权重；
   - `StateChange_FatigueFlipsTheChoice`（疲劳翻转）与 `StateChange_PickupDesireDecaysWithInventory`（同一掉落物：空载 Pickup / 满载 Patrol，欲望随库存衰减）；
   - 确定性对拍：两只 agent 喂同一状态流走出逐位相同轨迹。
3. **引擎无关预演**（仓库外 dotnet 纯 C# 仿真，`net8.0` 直接编译 Domain 五类 + Vec2）：场景 1 分化 25×0.1s——A=Rest / B=Pickup / C=Patrol，两两平面距离 **7.46 / 5.30 / 5.80 m** 全部 >2m；场景 2 证实 NPC_A 自行切换发生在恢复途中（仿真记录切换疲劳 0.637）。此仿真仅作域级预证，最终以 Unity 实跑为准。
4. **PlayMode 分化 + 截图**（`UtilityNpcScenePlayModeTests`，SCN_Utility 真实 LoadSceneAsync）：三 NPC 同出生点（两两 <0.5m 断言）→ ManualDrive 确定性驱动 2.5s → **Rest/Pickup/Patrol 三种互不相同行为**（HashSet 计数=3）+ 两两距离 >2m + 截图落盘；时间维度：NPC_A 自疲劳 0.95 休息→恢复→**无人干预自行切换**（切换时疲劳 <0.65，历史序列以 Rest 开头且记录到切换）+ 截图落盘。两图肉眼可辨三胶囊颜色/位置分化（蓝=Rest 驻留休息点、橙=Pickup 扑向掉落物、黄=Patrol 走路点环线）。

### 2.3 施工中发现并修复的缺陷（含接手修复，供验收知悉）

1. **（测试）疲劳切换断言错锁机制点**：原断言 `Assert.Less(fatigueAtSwitch, 0.5f)` 与曲线设计矛盾——休息欲望 `1.6·Ramp(f, 0.35, 0.9)` 饱和段 1.6 高于一切候选（Pickup 封顶 0.85、Follow 0.7、Patrol 0.3），**切换只可能发生在恢复途中**；手算切换点 = 0.35 + 0.55·(0.7/1.6) ≈ **0.591**（休息欲望跌破玩家远端 Follow 封顶 0.7），仿真实测 0.637（掉落物仍在时改投 Pickup）/0.569。断言改为 `< 0.65f`（手算点+余量，仍证明净恢复 ≥0.30、拒绝饱和期被顶替），注释附完整手算。教训与 A-TECH-02 §2.3-3 同源：**断言口径必须先对机制手算，否则"看着严格"的阈值是错的**。
2. **（产品）Setup 生成的 NPC id 与断言身份不一致**：`UtilitySceneSetup.BuildNpc` 用 `npcId.ToLowerInvariant()` 得 `npc_a/b/c`（下划线），而测试与 View 序列化默认身份是 `npc-a/b/c`（连字符）→ 第一轮 PlayMode 两用例在 `ById("npc-a")` 即红（`场景必须包含 id 为 npc-a 的 NPC`，round-1 xml 留证）。修复：Setup 改 `ToLowerInvariant().Replace('_', '-')`（GameObject 名保持 `NPC_A` 可读大写），场景幂等重生成。**这条变异-复红-修复-复绿闭环已在施工内自然发生**（round-1 红 → 修复 → round-2 绿），验收可按 2.6-a 重放。
3. **（交接确认）** `UtilityNpcScenePlayModeTests.cs` 缺 `using UnityEngine.TestTools;` 曾阻塞共享工程编译，A-TECH-02 代理按其白名单补上（其日志 §2.3-7 记录）；本代理审计确认在位、无语义变更，致谢知悉。
4. **（环境噪声，非本任务产物）** `Docs/Parity/crowd_r9_15_*.png` 是 A-R9-15 既有截图用例的落盘目标，**每次 PlayMode 全量运行都会被重写**（本轮观察：off 图逐字节复原、on 图呈 M）。与 A-TECH-02 日志 §2.3-7 的发现互相印证——请主控收口时按 §3.7 处置，勿记为本任务改动。

### 2.4 设计要点（继承半成品，续建者必读）

1. **选型落位**：`Game.Domain.AI.Utility` 是与既有 FSM（`Game.Domain.Enemy`）/行为树（`Game.Domain.AI`）**并排的第三种脑**，不复用/不改动它们；世界快照是新键值仓（不挂 gameplay Blackboard），保持切片自包含可抽取。
2. **确定性契约**：分数函数纯（同状态同分）、迭代按注册序、tie-break 严格大于扫描（先注册先胜）、冷却按时钟不按墙钟、NaN 不参选——EditMode 用例逐条锁死。
3. **人格 = 权重乘子**：三分化不改大脑代码，仅序列化起始状态（疲劳/欲望权重）不同——这正是"自己排序选择"与"预设分支"的分界证据。
4. **场景幂等**：每次运行全量重建 SCN_Utility + Build Settings 重排（Boot/Empty/Game/Utility 优先，Assembly 殿后），artifact 恒与脚本一致。
5. **PlayMode 确定性**：`ManualDrive=true` 屏蔽帧率相关 Update，测试以 `Step(0.1f)` 步进；接管前最多放过 1–2 真实帧（位置容差 0.5m 内）。

### 2.5 自测证据（2026-11-08 实跑，命令与 xml 留档）

| 段 | 命令/证据 | 结果 |
|---|---|---|
| 场景生成（×2 幂等） | `-executeMethod Game.View.Editor.UtilitySceneSetup.SetupFromCommandLine` → `TempLogs/utech01_scene.log`、`utech01_scene2.log` | **exit 0** ×2；`_npcId: npc-a/npc-b/npc-c`（连字符） |
| EditMode 全量 | `-runTests -testPlatform EditMode -testResults TempLogs/utech01_editmode.xml` | **928/928/0，exit 0**（本任务 35 条逐一核对：Brain 10 + Agent 11 + WorldState 7 + Curves 7，Failed=0） |
| PlayMode 全量 | `-runTests -testPlatform PlayMode -testResults TempLogs/utech01_playmode.xml` | **58/58/0，exit 0**（本任务 2 条 Passed；A-TECH-02 装配 8 + 既有 48 全绿） |
| arch 校验器 | `-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine` → `TempLogs/arch_report.txt`（05:06） | **exit 0**；asmdef 25 扫描 / 纯度目录含 `Assets/Game/Domain`（**Domain 零 UnityEngine 达成**）/ 违规 0 |
| config 校验器 | `ZFrame.Data.ConfigEditor.ConfigValidator.ValidateFromCommandLine` → `TempLogs/config_report.txt` | **exit 0**，违规 0 |
| naming 校验器 | `ZFrame.Editor.AssetNameValidator.ValidateFromCommandLine` → `TempLogs/name_report.txt` | **exit 0**，违规 0 |
| 文件规范 | od 抽查全部新增/改动文件 | UTF-8 无 BOM、LF（含本轮改动的 Setup 与 PlayMode 测试） |
| 像素证据 | `Docs/Parity/utility_tech01_divergence.png`（11847B）、`utility_tech01_switch.png`（11237B） | 非空、三 NPC 分化肉眼可辨 |

（注：批处理跑测试未带 `-quit`；等锁期间按 60s×10 纪律轮询，本轮实测一次撞锁后下一分钟即获锁；两轮批处理无僵尸，watchdog 未触发。路径口径：`utech01_*` 的 log/xml 在 `UnityProject/TempLogs/`（本任务自定名）；三校验器报告按校验器自身约定写在仓库根 `TempLogs/`（`Application.dataPath/../..`），与历史轮次一致。）

### 2.6 验收变异建议（至少复红一项，还原复绿）

- a.（零成本，已被施工亲证）`UtilitySceneSetup.BuildNpc` 去掉 `.Replace('_', '-')` → 重生成场景 → PlayMode 两用例必红（round-1 xml 即此状态）；
- b. `UtilityBrain.Tick` 赢家扫描 `>` 改 `>=` → tie-break 翻转 → `Ranking_IsDescending_AndDeterministicOnTies` / `TieBreak_EarliestRegisteredWins_RegardlessOfLaterOrder` 红；
- c. `NpcUtilityAgent.Tuning.FatiguePerSecond` 默认值改 0 → 疲劳不再积累 → `SameNpc_RestedPatrols_TiredRests_RecoveredPatrolsAgain` 红（序列断言卡死）；
- d. `UtilityCurves.Ramp` 删端点钳制 → `UtilityCurvesTests` 端点用例红。

## 三、验收清单（供验收代理）

1. `git status` 核对本节 2.1/2.3 清单之外无本任务产物（Native/* 属 B-TECH-03；crowd_r9_15_on.png 为 A-R9-15 用例重写噪声，见 2.3-4；EditorBuildSettings 为 A-TECH-01/02 双任务共享落点）。
2. 实跑复验至少三项：EditMode 全量（应 928/928/0）、PlayMode 全量（应 58/58/0）、三校验器 exit 0。
3. 场景核验：`SCN_Utility.unity` 存在、Build Settings 含 SCN_Utility 且 Boot/Empty/Game 顺序不变、`_npcId` 为连字符形式。
4. 变异复红：按 2.6 至少一项，还原后复绿。
5. 截图核验：两张 `utility_tech01_*.png` 存在且非空（可用 ViewImage 目测三 NPC 分化）。
6. 文件规范抽检：UTF-8 无 BOM / LF（用 od，勿用 `grep -c $'\r'`）。
7. 交叉知悉：A-TECH-02 补的 using（2.3-3）；EditorBuildSettings 双任务共享（2.3-1 注）；crowd png 噪声（2.3-4）。

## 四、验收结论

**PASS**（2026-11-08，验收子代理独立实跑；全部证据为本验收亲跑/亲看，非转录施工数字）

- **V1 EditMode**：亲跑 Unity 2022.3.62f3c1 批处理 exit 0，`UnityProject/TempLogs/accept_tech01_editmode.xml` 实测 **928/928/0**（duration 5.75s）；35 条效用用例逐名在列——`UtilityBrainTests` 10 + `NpcUtilityAgentTests` 11 + `NPCWorldStateTests` 7 + `UtilityCurvesTests` 7，xml 内叶用例去重计数恰 35、Failed 计 0。
- **V1 PlayMode**：亲跑 exit 0，`accept_tech01_playmode.xml` 实测 **58/58/0**；`UtilityNpcScenePlayModeTests.SameStart_ThreeNpcs_DivergeIntoDistinctBehaviorsAndPaths` 与 `.TiredNpc_Rests_ThenSwitchesOnItsOwn_WhenRecovered` 两用例在列且 Passed。
- **V2 效用核**（行号）：`UtilityBrain.cs` Tick L120-185 全量评分（L128-143，冷却/NaN 剔除 L131-140）→ 稳定降序插入排序 L148-159 → 严格大于扫描 L163-172（先注册先胜=确定性 tie-break）→ 阈值下 Idle L164+L174-178 → 执行并记录 L180-184；冷却用调用方时钟非墙钟 L107-111/L182；`UtilityCurves.cs` L15-69 四曲线全纯（无状态/时钟/随机）；`NpcUtilityAgent.cs` 欲望函数 L285-315 纯读 state、执行闭包 L319-368 为唯一突变点、`ActionChosen` 仅变化时触发 L259-263。
- **V3 自主性**：EditMode 序列断言在 `NpcUtilityAgentTests.cs` L40-70（同一只 NPC 仅泵状态走出 [Patrol, Rest, Patrol]）+ L75-83（疲劳翻转）+ L86-95（库存衰减改选择）+ L173-188（双 agent 同状态流逐位同轨迹）；PlayMode 两截图亲看（含本验收 05:13 重跑重写版）：`utility_tech01_divergence.png` 同起点三 NPC 蓝=Rest(左下)/橙=Pickup(扑掉落物)/黄=Patrol(路点区) + 灰色静态玩家，肉眼分化；`utility_tech01_switch.png` 蓝 NPC 驻留休息点标记旁、B/C 黄色在路点区，与各自行为配色/位置自洽。
- **V4 纯度与校验器**：grep 亲证 `Assets/Game/Domain/AI/Utility/` 与全 `Assets/Game/Domain/` 零 `UnityEngine|UnityEditor`（NO MATCH），唯一依赖 `ZFrame.Gameplay.Vec2` 亦引擎无关；三校验器本验收亲跑 **exit 0×3**（arch 报告 05:14 纯度目录含 `Assets/Game/Domain` 违规 0；config 05:16 违规 0；naming 05:16 违规 0）。
- **V5 规范与报告**：13 个新增/改动文件 od 抽查全部无 BOM（首 3 字节均 ASCII）且 0 个 CR 字节（LF）；日志四节齐全（本节为验收结论）；产物归属核对 `git status` 无未申报 A-TECH-01 产物（crowd_r9_15_on.png=A-R9-15 用例重写噪声、EditorBuildSettings=与 A-TECH-02 共享、Native/*+planet_*=B-TECH-03、Assembly*=A-TECH-02，全部与 2.3 申报一致）。
- **清单项 3 场景**：`Assets/Scenes/SCN_Utility.unity` 在盘（50,349B），Build Settings 顺序 `SCN_Boot/Empty/Game/Utility/Assembly`（Boot/Empty/Game 未动），场景实测 `_npcId: npc-a/npc-b/npc-c`（L858/1240/1788，连字符）。
- **接手审计 2 缺陷评估（均成立、处置正确）**：①0.65 阈值——独立复算切换点：休息欲望 1.6·Ramp(f,0.35,0.9) 跌破 Pickup 0.85 ⇔ f≈0.642、跌破 Follow 0.7 ⇔ f=0.35+0.55·(0.7/1.6)≈0.591，两者均 <0.65 且 > 饱和段 0.9（拒绝"饱和期被顶替"），断言口径与机制吻合（测试注释 L118-123 推导完整）；②npc-a 下划线——根因成立：原 `ToLowerInvariant()` 产 `npc_a` 与断言/序列化默认 `npc-a` 失配（`ById` L174 报错文与 round-1 记录逐字吻合），修复在 `UtilitySceneSetup.cs` L192 `.Replace('_','-')`，场景重生成后实测连字符、round-2 绿。
- **清单项 4 变异复红：未执行**——主控指令"验收子代理不得修改代码"，本验收以静态机制审查+全量实跑替代（2.6-b/c 的红路径已由源码逐行确认：`>` 改 `>=` 必翻 tie-break、`FatiguePerSecond=0` 必卡死序列断言）；施工侧 2.3-2 的 round-1 红→修复→round-2 绿已构成一次自然变异闭环。
- **观察项（不阻断）**：① round-1 红证 xml 未在盘留存（`TempLogs` 仅 round-2 `utech01_playmode.xml`；红态由代码机制必然成立，非依赖该文件）；② 2.5 所称 Setup 日志打印 `_npcId: npc-a` 未在 `utech01_scene*.log` 找到（Setup 不打印 npcId，以场景文件实测为准，结论不变）。
