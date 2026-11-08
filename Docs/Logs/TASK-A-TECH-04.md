# TASK-A-TECH-04 —— 效用 AI 第二阶段：个性权重（配置驱动）+ GOAP 前瞻原型

状态: doing | 派发: 2026-11-04 | 施工: （待填） | 验收: （待填） | commit: （待填）

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

（待填）

## 验收清单（主控写）

- [ ] V1 EditMode 全绿（数字写入结论）；核心新用例真实（权重分化/GOAP 两步链/无解——行号）
- [ ] V2 **分化实证亲核**：PlayMode 或 EditMode 状态序列——5 个同状态 NPC 行为两两不同（截图+序列）
- [ ] V3 GOAP 规划器：两步链/无解/深度上限用例；规划器与 UtilityBrain 的集成路径（谁调谁）审查
- [ ] V4 个性表走 ConfigTable（配置系统实战——CFG 表读取行号）；Validate exit 0（自跑）
- [ ] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

（待填）
