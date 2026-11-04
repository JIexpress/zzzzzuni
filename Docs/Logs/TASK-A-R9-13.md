# TASK-A-R9-13 —— 千人演练场：DataKernel 玩法侧规模化（混合架构）

状态: doing | 立简报/派发: 2026-11-04 | 施工: builder-r9-13（general-purpose） | 验收: verifier-r9-13（须与施工不同代理） | commit: （待填）

## 任务简报（主控写）

### 背景

B-R9-17（`2a848da`）已闭环：DataKernel 首次跨 DLL 暴露句柄式 C ABI（12 个 `ZK_*` 导出）、C# 安全包装 `ZKernelWorld`（`IDisposable` / `T : unmanaged` 泛型 / 游标式 `EachBatches` / 布局版本号防护），EditMode 真 dll roundtrip 与 1000 实体游标基线 **10.0 µs/次**（`Docs/Logs/TASK-B-R9-17.md` §四）均已落盘。

但 `Docs/需求达成对照.md` §四仍把「**DataKernel 玩法侧接入**」列为**唯一待排缺口**：内核至今只在基准与单测里跑，没有任何玩法场景消费它。本任务补上这一块，兑现「混合架构：kernel 管群体，Mono 管英雄」。

### 目标（按此实现，细节可斟酌但报告说明）

1. **组件与注册**：在 C# 侧定义 3 个 `unmanaged` 组件（建议 `Position` / `Velocity` / `Tag`），compId 由 C# 显式分配 0..127；用 `ZKernelWorld.RegisterComponent<T>(compId)` 注册，`AlignOf` 的启发式或显式 `(id,size,align)` 重载按 `ZKernelWorld.cs` 现有约定选用。
2. **群体驱动**：新建一个玩法侧驱动（建议 `Assets/Game/View/Effects/KernelCrowdDriver.cs` 或等价位置），持有**单个** `ZKernelWorld` 实例：
   - 生成 1000 个实体，写入初始位置/速度/标签；
   - 每帧经 `ITickQueue` 驱动（**不要** `MonoBehaviour.Update`；逻辑阶段与暂停语义沿用 `GameLoopDriver`），用 `EachBatches(mask, callback)` 游标批量读列 → 积分速度 → 回写位置（**回调内禁止 Spawn/Destroy/Add/Remove**，原生布局版本号会作废游标并抛 `ErrState`；`SetComponent` 不改布局，安全）；
   - 世界随场景生命周期 `Dispose()`（原生句柄表最多 8 世界，泄漏会耗尽槽位）。
3. **可视化（关键取舍）**：**不得**为 1000 个实体各建一个 GameObject——那等于把 SoA 内核的意义抵消掉。走批量绘制（`Graphics.DrawMeshInstanced` / `Indirect`，或现有池化粒子），判据：**DrawCall 不随实体数线性增长**，并给出与 1000 实体对应的实测数字（不编）。
   - 管线约束（已判定）：Unity 工程当前激活 **URP 14.0.11**（`GraphicsSettings.m_CustomRenderPipeline` → `URP_Medium.asset`），自研 SRP 只是空壳。所有材质/绘制必须按 URP 兼容写法落地，**不要**往 `ZRenderPipeline.cs` 加 pass。
4. **英雄侧不变**：现有 SCN_Game 的玩家 / 敌人 FSM 继续由 MonoBehaviour 管；本任务只加群体，**不改现有玩法手感、不改场景内容布局**（历史上 A-GPU-01 简报亦明令「不碰游戏内容」）。
5. **性能守卫**：以 B-R9-17 的 10.0 µs/1000 实体游标拉取为基线，给出本任务「每帧群体更新 + 批量绘制」的耗时报告（EditMode 计时 + PlayMode 实测），并明确区分**内核迭代**与**渲染提交**两段；跨跑比较前执行 `Tools/check_machine_quiet.ps1`（交接档案 §3.4），不可信时改用同一次运行内 A/B 对照。
6. **测试**：EditMode 真 dll roundtrip（1000 实体全量求和、迁移后读回、销毁句柄失效、坏 compId 拒绝）；PlayMode 断言群体可见且英雄行为不回归；架构校验器（Domain 纯度 / asmdef 矩阵）exit 0；既有用例只增不减。

### 约束

- 白名单：`UnityProject/Assets/Game/**`、`UnityProject/Assets/ZFrame/Tests/**`、`UnityProject/Assets/ZRender/Bindings/**`（仅当必须扩展绑定时，须在报告说明理由）、`Docs/Logs/TASK-A-R9-13.md`、`Docs/Demo.md`（仅当新增体验项时）。
- **不改原生 C++**（ABI 已定且 `zrender.def` 只许追加）、不改 `Native/**`、不改 HUD / DevConsole / 音频 / 存档等既有模块、不改 `TASKS.md` / `HANDOFF.md` / `WORKLOG.md` / `README.md`。
- 不 `git add` / 不 `git commit`；UTF-8 无 BOM + LF；`.meta` 由 Unity 生成，不手改。
- 依赖 `UnityProject/Assets/Plugins/ZRender/zrender.dll` 的 12 个 `ZK_*` 导出（已入库，`2a848da`）；**部署位 dll 是本任务运行前提，勿按交接档案 §3.7 的「构建产物噪声」惯例 checkout 丢弃**。

### 如实受限（写进报告，别装作验证过）

- 本机 Editor 为 Mono；**IL2CPP / Android 上的 P/Invoke 与 `unmanaged` 泛型未验证**，若要做真机包需另立任务（A-AND-06 的 IL2CPP 构建约 29 分钟）。
- `Graphics.DrawMeshInstanced*` 在 URP 14 下的实例数上限、SRP Batcher 兼容性与移动端降级策略，需实测后写结论，不要照搬桌面假设。
- 若批量绘制在批处理 `-batchmode` 无渲染负载下取不到可信 DrawCall，如实标受限，改用 `ProfilerRecorder` 静态证据 + 编辑器 Play Mode 人工目测（参照 A-GPU-01 V1 的诚实口径）。

### 环境纪律（真实踩过，详见 `Docs/交接档案.md` §3）

- 跑测试前清 8 个代理变量（大小写成对），否则 MSBuild / Unity 启动失败。
- Unity 批处理单实例：跑前查进程 + 删 `UnityLockfile`；跑测试**勿带 `-quit`**；xml 出现但 5 分钟不退 → `taskkill //PID <pid> //F`；证据以 results.xml 为准。
- Bash 侧禁止转调 powershell → 用 PowerShell 工具 `& .\Tools\run_tests.ps1`；本机 GUI Editor 加 `-force-d3d12` 会挂起。
- 清理用 `mv` 归档，不要 `rm -rf`；跑完查 `git status`，Editor 自动改写 `URP_*.asset` / `QualitySettings.asset` 时只针对这几个文件还原。

### 铁律

施工与验收必须不同代理；一任务一 commit；日志四节齐全（本文件的施工报告 / 验收清单 / 验收结论缺一不可）；验收代理须亲自做「修前必红」定向变异；证据不齐写受限，绝不把「部分完成」说成「已完成」。

## 施工报告（施工代理写）

（待派发后填写）

## 验收清单（主控写，派发前复核）

- [ ] V1 1000 实体真 dll roundtrip：求和 / 跨 chunk 游标 / 迁移 / 销毁句柄失效（行号 + xml）
- [ ] V2 每帧驱动走 `ITickQueue`、暂停语义正确、场景卸载 `Dispose()` 无句柄泄漏
- [ ] V3 批量绘制：DrawCall 不随实体数线性增长（实测数字 + 测量方式），URP 14 兼容
- [ ] V4 性能守卫：内核迭代 vs 渲染提交分段耗时，与 10.0 µs 基线对照，空载护栏已跑
- [ ] V5 英雄侧不回归、Domain 纯度 / asmdef 校验器 exit 0、既有用例只增不减
- [ ] V6 文件规范 + 施工报告四节 + 验收代理亲自复做变异（变红 → 还原 → 复绿 → 零残留）

## 验收结论（验收代理写）

（待派发后填写）
