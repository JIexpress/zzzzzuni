# TASK-A-AI-01 —— NPC 记忆与态度系统（效用 AI 第三层：个性×态度=行为）

状态: doing | 派发: 2026-11-04 | 施工: （待填） | 验收: （待填） | commit: （待填）

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

（待填）

## 验收清单（主控写）

- [ ] V1 EditMode 全绿（数字写入结论）；核心用例真实（同事件异反应/衰减/滚动——行号）
- [ ] V2 聚合公式纯函数+半衰期断言（行号）；态度调制效用触点（NpcUtilityAgent 行号）
- [ ] V3 个性×态度联动用例（勇敢 NPC 同事件不同反应——行号）
- [ ] V4 Domain 纯度+Validate exit 0（自跑）；既有用例零改动通过
- [ ] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

（待填）
