# TASK-A-R9-15 —— 千人演练场：开箱可见 + 真实像素证据

状态: doing | 派发: 2026-11-04 | 施工: builder-r9-15（general-purpose） | 验收: verifier-r9-15（须与施工不同代理） | commit: （待填）

## 任务简报（主控写）

### 背景

A-R9-13（`5f49a42`）验收 **PASS with 受限**，V3 明确：`KernelCrowdHost.AutoEnable` 默认 **false**，群体靠编辑器菜单 `KernelCrowdMenu` 手动开启；PlayMode 用例是显式 `EnsureCrowd()`，证明的是组件/提交层，**不是「进 SCN_Game 即开箱可见」**；`-batchmode` 下真实 GPU DrawCall 与像素可见性未取证。本任务把这两个受限项补上。

### 前置调研（施工第一件事，别跳过）

读 `Docs/Logs/TASK-A-R9-10.md` 与 `Assets/Game/Tests/View/` 下的相机差分 / 像素证据用例，查清 A-R9-13 施工把 `AutoEnable` 设为 false 的**具体原因**（污染了哪条像素断言、差分量级），再决定方案。报告里必须写清「为什么原来要关」以及「现在为什么可以开」。

### 目标

1. **默认可见**：进 `SCN_Game` 群体自动可见（`AutoEnable` 打开或按场景门控自动挂载），英雄侧玩法 / FSM / 场景布局零改动。
2. **不破坏既有像素用例**：A-R9-10 的相机差分 / 像素证据用例继续全绿。若确实冲突，给出可证伪的协调方式（例如把群体排除在差分采样区、或让差分用例显式关闭群体），**严禁删除、放宽、跳过既有断言**，也不许靠加重试 / 改容差蒙过去。
3. **真实像素证据**：落盘两张可复核的图（群体开 / 关）+ 像素差统计，证明群体真的出现在画面里，而不是「只有 1 次提交调用」。可选路径：DevServer `shot` 端点（`127.0.0.1:17321`，A-DEV-01/02，gameview 源已验证与相机源不同）、PlayMode `ScreenCapture` + 读回像素、或 Editor 截图。报告写明测量方式、图片路径、差异数字与判定阈值。
4. **真实 GPU DrawCall**：若能在 Editor Play Mode 用 `ProfilerRecorder` / Frame Debugger 取到可信值就给实测；批处理下取不到就如实标受限，**不编数字**。
5. **测试**：PlayMode 新增用例断言「默认进入场景后群体可见」+ 像素差异；EditMode **880** / PlayMode **46** 只增不减；arch/config/naming 三校验器 exit 0；native 311 不降。

### 约束

- 白名单：`UnityProject/Assets/Game/**`、`UnityProject/Assets/ZFrame/Tests/**`、`Docs/Logs/TASK-A-R9-15.md`（仅「施工报告」节）、`Docs/Demo.md`（仅当体验项变化）、`Docs/Parity/**`（仅当落图片证据）。
- 禁止：`Native/**`、`ZRender/**`、`ZFrame/View/Hud/**`、`Assets/Audio/**`、`ZFrame/View/Audio/**`、`TASKS.md` / `HANDOFF.md` / `WORKLOG.md` / `README.md` / `DECISIONS.md`；不 `git add` / 不 `git commit`；不手改 `.meta`；UTF-8 无 BOM + LF。
- **保持 A-R9-13 的批量绘制机制**：单次 `Graphics.DrawMeshInstanced` 提交 1000 实例不得退化；`EachBatches` 游标迭代与 `Dispose()` 生命周期不得破坏。
- 不要 `git checkout --` 丢弃 `UnityProject/Assets/Plugins/ZRender/zrender.dll`（含 12 个 `ZK_*` 导出，是本任务运行前提）。
- 唯一可写的日志节是「施工报告」；**不要写「验收结论」**，不要勾选验收清单。

### 环境纪律（真实踩过，详见 `Docs/交接档案.md` §3）

- 跑测试前清 8 个代理变量（大小写成对），否则 MSBuild / Unity 启动失败。
- Unity 批处理单实例：跑前查进程 + 删 `UnityLockfile`；跑测试**勿带 `-quit`**；xml 出现但 5 分钟不退 → `taskkill //PID <pid> //F`；证据以本次新写 results.xml 为准。
- Bash 侧禁止转调 powershell → 用 PowerShell 工具 `& .\Tools\run_tests.ps1`；本机 GUI Editor 加 `-force-d3d12` 会挂起。
- 清理用 `mv` 归档，不要 `rm -rf`；跑完查 `git status`，Editor 自动改写 `URP_*.asset` / `QualitySettings.asset` / `ProjectSettings` 时只针对这几个具体文件还原。
- 跨跑性能比较前跑 `Tools/check_machine_quiet.ps1`；不可信时改用同一次运行内 A/B 对照。

### 铁律

施工与验收必须不同代理；一任务一 commit；日志四节齐全；验收代理须亲自做「修前必红」定向变异；证据不齐写受限，绝不把「部分完成」说成「已完成」。

## 施工报告（施工代理写）

（待填写）

## 验收清单（主控写）

- [ ] V1 默认可见：PlayMode 真断言（行号 + 实跑数字），非「菜单可开」
- [ ] V2 像素证据：两图路径 + 差异统计 + 测量方式 + 判定阈值，可复核
- [ ] V3 A-R9-10 相机差分用例不回归，且既有断言强度未降（diff 证明）
- [ ] V4 单次批量绘制仍成立；EditMode ≥880 / PlayMode ≥46 / native 311；三校验器 exit 0
- [ ] V5 报告四节齐全 + 验收代理亲自复做定向变异（变红 → 还原 → 复绿 → 零残留）

## 验收结论（验收代理写）

（待填写）
