# TASK-A-GPU-01 —— 性能 HUD（DrawCall / SetPass / 显存 / FPS）

> 方案 §5.7 的「DrawCall/显存/带宽 HUD」一项，至今空白。本任务做**第一步**，只做 Unity 侧、不碰原生、不引外部工具。
> 施工代理按本简报执行，第 2 节写报告。**禁止 git commit**。验收由另一代理执行。

## 任务简报（主控写，2026-11-04）

### 背景与边界

`Docs/框架设计方案.md` §5.7 承诺「GPU 调试工具：RenderDoc/PIX 一键捕获、RG 甘特图导出、
DrawCall/显存/带宽 HUD、自动跑分回归」。现状盘点（已核实）：

| 项 | 现状 |
|---|---|
| 自动跑分回归 | ✅ 已有（`Tools/bench_regression.ps1` + `bench_baseline.json`，B-R9-10） |
| DrawCall/显存 HUD | ❌ 无（全仓无相关实现） |
| RG 甘特图导出 | ❌ 无 |
| RenderDoc/PIX 一键捕获 | ❌ 无（本机也未装 RenderDoc） |

**本任务只做 HUD**，其余两项另立。**不要顺手做甘特图或 RenderDoc 集成。**

### 现状事实（别重复摸索）

- `Native/zrhi/zrhi_device.h:210` 的 `ZrDeviceStats` 只有：`aliasHeapCount` / `imageCount` /
  `bufferCount` / `presentFailureCount` / `lastPresentHresult` / `deviceRemoved` /
  `descriptorSetAllocFailureCount` / `barrierTruncationCount`。
  **没有 DrawCall、没有显存字节、没有 GPU 时间** → 所以本任务**不要**从原生取数，走 Unity 侧的
  `ProfilerRecorder` / `Profiler.GetAllocatedMemoryForGraphicsDriver()`。
- 项目已有 `RuntimeDevConsole`（`127.0.0.1:17322`，白名单命令 `help/dump/spark/quality/fg/shot`），
  **不要把它和 HUD 混在一起**，但可以考虑让 HUD 数据也能被 `dump` 读出来（可选，见下文）。

### 任务

1. **HUD 组件**（Unity 侧，`ZFrame/View` 或独立小模块）：
   - 显示：**FPS**、**DrawCall**、**SetPass Calls**、**三角面数**、**显存占用**（graphics driver 分配量）、
     以及（若可用）**每帧 GPU 时间**。
   - 实现建议：`ProfilerRecorder` 读 `Draw Calls Count` / `SetPass Calls Count` / `Triangles Count`；
     `Profiler.GetAllocatedMemoryForGraphicsDriver()` 取显存；FPS 自行按帧间隔平滑（0.5s 窗口）。
   - 呈现：屏幕角落一层半透明文字叠加，**不拦截射线**（不能挡住游戏 UI 的点击）。
   - 数值刷新频率限制在 **2-4 Hz**（每帧刷文本会自己影响帧率，让读数失真）。
2. **门控（重要，参照 DevConsole 的教训）**：
   - **默认关闭**。开启方式：Development Build 下按快捷键（如 `F3`）切换，或命令行 `-zfhud`，
     或环境变量 `ZBF_HUD=1`。
   - **Release 包默认不可开启**（与 `RuntimeDevConsole` 同一套思路：非开发包不响应快捷键/参数）。
     **不要**做成"release 里按 F3 也能开"——那是给发布包开一个免费的性能后门。
   - 开关状态要能被测试覆盖（门控逻辑抽成纯函数/可注入探测，便于 EditMode 测）。
3. **开销**：`ProfilerRecorder` 常驻采样本身有成本，**关闭时不应持续采样**（懒启动），
   并在报告里给出"开启前后帧率差异"的粗略观察（不要求精确基准）。
4. **测试**：
   - EditMode 覆盖：数值格式化（如 1234 → `1.2k`、显存 MB 换算、0 值不显示 NaN）、
     门控三态（dev+快捷键开 / release 拒绝 / 参数开）、刷新节流。
   - **改回旧逻辑必须变红**（至少门控那条要有牙）。
   - 回归：EditMode 811 / PlayMode 41 不降。
5. **不要在真机上跑**（本任务只到编辑器/单测；真机验证另立，避免占用 29 分钟构建）。

### 判据

- 编辑器 Play Mode 下按 F3 能显示/隐藏 HUD，数值合理（DrawCall 与 Frame Debugger 同量级即可）；
- Release 配置下**无论按什么键都不显示**（有测试或明确代码证据）；
- 关闭时不持续采样；
- 既有测试不回归；
- 不改游戏内容、不改原生、不碰 DevConsole 的端口与命令。

### 环境纪律（真实踩过）

- Unity 批处理**单实例**：跑前查进程 + 删 `UnityProject/Temp/UnityLockfile`
- 跑测试**勿带 `-quit`**；退出僵死 → 轮询 `results.xml`，5 分钟有 xml 无退出即 `taskkill //PID //F`
- 构建前 `unset http_proxy https_proxy all_proxy`；**Bash 侧禁止转调 powershell** → 用 **PowerShell 工具**
- 本机 GUI Editor 加 `-force-d3d12` **会挂起**，别加
- 清理用 `mv` 归档到 `TempLogs/archive_pre_audit/`，不要 `rm -rf`
- 跑完查 `git status`：Editor 自动改写 `URP_*.asset`/`QualitySettings.asset` 非本意就 `git checkout` 还原
- 注意：本地 `Native/build` 目前是**无 Vulkan 配置**（上一任务留下的），与本任务无关，别去"修"它

### 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- 不改 Governor 三件套、原生代码、Unity 安装目录；`.cs` 无 BOM、LF
- **不碰游戏内容**（角色/场景/关卡/手感），用户明确说这块不重要，别花时间

---

## 施工报告（施工代理写）

（待填）

---

## 验收清单（供验收代理）

- [ ] V1 编辑器 Play Mode 按 F3 能开/关 HUD，显示 FPS/DrawCall/SetPass/三角面/显存
- [ ] V2 数值合理（DrawCall 与 Frame Debugger 或 Profiler 同量级，不是 0 或天文数字）
- [ ] V3 **Release 配置下无法开启**（代码证据 + 测试；不是"按了没反应"就算过，要能说明为什么）
- [ ] V4 关闭时不持续采样（懒启动有证据）
- [ ] V5 刷新节流生效（不是每帧刷）；HUD 不拦截点击
- [ ] V6 回归：EditMode 811 / PlayMode 41 不降；未改原生/游戏内容；未 commit；现场干净

## 验收结论（验收代理写）

（待填）
