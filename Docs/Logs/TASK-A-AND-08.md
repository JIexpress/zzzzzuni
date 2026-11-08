# TASK-A-AND-08 —— DataKernel 玩法侧规模化：千人群体状态迁移到 ZKernelWorld

状态: doing | 派发: 2026-11-04 | 施工: （待填） | 验收: （待填） | commit: （待填）

## 任务简报（主控写）

### 背景

B-R9-17 已打通 DataKernel 的 C# 通道（ZKernelWorld，游标拉取 10.2µs/千实体），但玩法侧（A-R9-13/15 的千人群体）仍用 C# 自管数组——**数据内核"玩法侧规模化"是最后的未验证承诺**。本任务：把群体实体状态（位置/朝向/生命/存活）迁移进 `ZKernelWorld`（SoA），群体行为更新走 kernel 游标读写，渲染仍走 DrawMeshInstanced（矩阵从 kernel 列数据构建）。

### 设计要点

1. **迁移范围**（先读 A-R9-13/15 现有实现再定）：群体单位的状态数组→kernel 组件（Position=自定义 blittable float3 组件；Health float；Alive→mask 位或 bool 组件）；行为逻辑（A-R9-13 的移动/巡逻）改写为对游标数据的读写
2. **性能口径（可证伪核心）**：迁移前后 A/B——同场景同实体数（1000）的帧耗与 Each 遍历耗时对比（FrameGovernor/Stopwatch 双口径）；**验收线**：kernel 路径单帧开销 ≤ C# 数组路径 +20%（数据进出 ABI 有 Copy 成本，如实记录；若显著劣化→如实报告并分析瓶颈，不硬凑）
3. **行为等价**：迁移前后群体视觉行为一致（像素证据沿用 A-R9-15 口径 on/off）
4. **kernel 真实性断言**：测试证明数据确实住在 kernel（world 销毁→行为停止/组件读回一致——防"名义迁移实际还是数组"）
5. 兼容：英雄侧（玩家/NPC Mono 体）不动；A-R9-15 的像素用例零改动通过（只换数据宿主）
6. 自测：EditMode+PlayMode 全绿（基线 811+新/48+新增）+ native 零改动（git diff Native/ 为空）+ Validate exit 0

### 约束

- 白名单：`Assets/Game/**`（群体系统+测试）、`UnityProject/Assets/ZRender/Bindings/**`（若需补充绑定小改）、本日志；native 零改动
- 每帧 P/Invoke 次数预算（游标 Begin/Next 每帧一次 Begin+若干 Next——不是每实体一次）
- Unity 批处理纪律；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0

## 施工报告（施工代理写）

（待填）

## 验收清单（主控写）

- [ ] V1 EditMode+PlayMode 全绿（数字写入结论）；A-R9-15 像素用例零改动通过
- [ ] V2 kernel 真实性断言用例（world 销毁→行为停止/组件读回一致——行号）
- [ ] V3 A/B 性能对比数字（迁移前/后帧耗+遍历耗时）入报告，验收线判定如实
- [ ] V4 P/Invoke 次数预算审查（每帧调用计数——行号）
- [ ] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

（待填）
