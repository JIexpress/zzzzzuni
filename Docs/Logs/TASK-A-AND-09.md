# TASK-A-AND-09 —— 人群四档 LOD 管线（L0≤20/L1≤50/L2 面片/L3 播片边界）

状态: doing | 派发: 2026-11-04 | 施工: （待填） | 验收: （待填） | commit: （待填）

## 任务简报（主控写）

### 背景（用户绑定约束，ROADMAP 方向四档位表）

人群渲染按精度四档：**L0 全精度≤20**（带骨骼动画，A-ANIM-01 的 NpcAnimatorDriver NPC）/ **L1 低模≤50**（程序化人形几百面）/ **L2 Impostor 面片**（billboard，数百）/ **L3 播片**（仅过场/固定机位——本任务只留接口与边界文档，不实现播片内容）。数量上限是**硬约束**（超限自动降级或拒绝，断言锁死）。

### 设计要点

1. `Game/View/Crowd/CrowdLODPipeline.cs`：统一管理四档——输入：DataKernel 群体状态（A-AND-08 迁移后的 kernel 数据）+ 相机位置；输出：各档位的渲染提交
   - **档位分配**：按与相机距离+重要性标记（剧情 NPC 永远 L0）——距离阈值+迟滞（防频繁切换）
   - **数量硬上限断言**：L0>20→自动降级最远者（或拒绝注册，报告选型）；L1>50 同理
   - L0 提交=NpcAnimatorDriver NPC（A-ANIM-01 产物）；L1=程序化低模人形（Primitive 组合或简单 mesh，几百面）；L2=billboard（CameraFacing+程序化人群纹理或纯色剪影）
2. **DataKernel 集成**：所有档位的实体状态统一从 ZKernelWorld 游标读（A-AND-08 已迁）——渲染档位只改表示法不改数据源
3. 切换平滑：L0↔L1 切换时位置/朝向连续（同坐标），无跳变（断言）
4. **可证伪 PlayMode 用例**：①注册 25 个全精度请求→恰 20 个 L0+5 个降级 L1（硬上限）②距离驱动：NPC 远移→档位降级（L0→L1→L2 断言）+迟滞（边界往返不振荡）③L2 billboard 面向相机断言④数量上限：L2 数百不超限
5. 自测：PlayMode 全绿（71+新增）+ EditMode 全绿（983+新增）+ Validate exit 0
6. **L3 边界文档**：播片实现留待过场任务（VideoPlayer+固定机位）；本任务在管线中预留 L3 枚举位与边界注释

### 约束

- 白名单：`Assets/Game/View/Crowd/**`（管线+L1/L2 表示）、`Assets/ZFrame/View/**`（若需）、`Assets/Game/Tests/**`、本日志
- 不动 DataKernel/kernel/效用 AI Domain 逻辑；A-ANIM-01 产物只消费；Unity 批处理纪律；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0

## 施工报告（施工代理写）

（待填）

## 验收清单（主控写）

- [ ] V1 PlayMode 全绿（数字写入结论）；四条可证伪用例真实（行号）
- [ ] V2 硬上限断言（L0>20 降级/拒绝——行号+变异复做 1 次）
- [ ] V3 档位切换迟滞+无跳变断言（行号）；L2 billboard 面向相机
- [ ] V4 kernel 数据源统一（渲染读 kernel 游标——行号）；L3 边界文档存在
- [ ] V5 Validate exit 0（自跑）+文件规范+施工报告四节完整

## 验收结论（验收代理写）

（待填）
