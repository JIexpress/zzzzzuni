# TASK-A-AI-02 —— AI 队友协作（GOAP 社交协调层）

状态: doing | 派发: 2026-11-04 | 施工: （待填） | 验收: （待填） | commit: （待填）

## 任务简报（主控写）

### 背景（ROADMAP 方向四 A-AI-02）

效用 AI/GOAP/记忆态度/个性均已就绪（A-TECH-01/04、A-AI-01）。缺的是**多个自主 NPC 之间的协调**：AI 队友（跟随玩家、分担任务、不抢同一目标）与敌对群体的简单协调（包抄不重叠）。

### 设计要点（纯 C# Domain）

1. `Game/Domain/AI/Coordination/`：
   - `TaskBoard`（任务板：NPC 可发布/认领任务——认领互斥，同任务一个 NPC；任务有位置+类型+到期）——GOAP 的前置条件天然适配任务认领（认领=效果达成）
   - `SquadCoordinator`（队伍：成员列表+编队位置槽（玩家周围环形槽位）+成员间任务不冲突）
   - 敌对协调最小版：AttackAssignment（按距离+态度分派目标，避免全员打同一个）
2. **与既有系统集成**：UtilityBrain 评估时把 TaskBoard 任务作为候选行动（认领成本=距离）；SquadCoordinator 管理 DataKernel 群体的攻击分派（A-AND-08 迁移后的 kernel 数据读——只读）
3. 演示：玩家+2 队友 NPC（跟随+协防）+3 敌人——分派不重叠（断言）；队友认领拾取任务不冲突（断言）
4. 可证伪用例（EditMode）：任务认领互斥；编队槽唯一；分派不重叠；任务到期回收；GOAP 规划消费 TaskBoard 任务（两步链先认领再执行）
5. 自测：EditMode 全绿（基线 960+新增）+Validate exit 0

### 约束

- 白名单：`Assets/Game/Domain/AI/**`、`Assets/ZFrame/Gameplay/AI/**`（若框架级）、Tests/**、本日志
- 不改效用核/GOAP/记忆态度既有语义（只扩展）；Unity 批处理纪律；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0
- 演示场景 View 层最小（PlayMode 截图可选）

## 施工报告（施工代理写）

（待填）

## 验收清单（主控写）

- [ ] V1 EditMode 全绿（数字写入结论）；认领互斥/编队槽唯一/分派不重叠/到期回收用例真实（行号）
- [ ] V2 与 UtilityBrain/GOAP 集成路径审查（行号）；既有语义零破坏（git diff 既有测试核对）
- [ ] V3 Domain 纯度+Validate exit 0（自跑）
- [ ] V4 文件规范+施工报告四节完整

## 验收结论（验收代理写）

（待填）
