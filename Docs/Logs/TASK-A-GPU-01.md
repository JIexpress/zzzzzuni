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

### 1. 交付物（全部新增，未改动任何既有文件）

| 文件 | 作用 |
|---|---|
| `UnityProject/Assets/ZFrame/View/Hud/PerformanceHud.cs` | HUD 宿主（MonoBehaviour）：F3 切换、4 Hz 文本、IMGUI 叠加、懒启动接线 |
| `UnityProject/Assets/ZFrame/View/Hud/PerformanceHudGate.cs` | 门控纯函数（开发包判定 + `-zfhud` / `ZBF_HUD` 解析） |
| `UnityProject/Assets/ZFrame/View/Hud/PerformanceHudSampler.cs` | ProfilerRecorder 采样器（Start/Stop 成对，IDisposable） |
| `UnityProject/Assets/ZFrame/View/Hud/HudTextFormat.cs` | 数值格式化（k/M、MiB、ms、不可用占位） |
| `UnityProject/Assets/ZFrame/View/Hud/HudRefreshThrottle.cs` | 刷新节流（2–4 Hz 硬约束） |
| `UnityProject/Assets/ZFrame/Tests/Debug/PerformanceHudTests.cs` | 14 条 EditMode（门控 4 / 格式化 3 / 节流 3 / 组件 4） |

`.meta` 由 Unity 导入时生成；`.cs` 均为 UTF-8 无 BOM + LF。未改 Governor、原生、DevConsole、游戏内容。

### 2. 指标与数据来源（全部 Unity 侧，未从原生取数）

| 行 | 来源 |
|---|---|
| `FPS` | 复用既有 `FpsCounter`（30 帧滑动窗口 ≈ 0.5 s @60fps） |
| `Draw` | `ProfilerRecorder(Render, "Draw Calls Count")` |
| `SetPass` | `ProfilerRecorder(Render, "SetPass Calls Count")` |
| `Tris` | `ProfilerRecorder(Render, "Triangles Count")` |
| `VRAM` | `Profiler.GetAllocatedMemoryForGraphicsDriver()`（graphics driver 分配量，不是显存总量） |
| `GPU` | `ProfilerRecorder("GPU Frame Time")`，Render → Internal 依次探测（与 `RecorderFrameTelemetry` 同一套写法），纳秒 ×1e-6；平台无此计数器时显示 `—` |

任一 recorder 取不到值（`!Valid` / 无样本 / 批处理不可用）都退化为 `0` 或 `—`，不抛异常、不出 NaN。

### 3. 门控：Release 为什么打不开（两道墙）

**第一道是编译期（最强）：** `PerformanceHud.cs` 从第一行到文件尾都包在
`#if UNITY_EDITOR || DEVELOPMENT_BUILD || UNITY_INCLUDE_TESTS` 内。Release 玩家包不定义任何
一个符号 → 发布包托管程序集里**没有这个类型**：没有 F3 的 `Input.GetKeyDown`、没有
`ProfilerRecorder`、也没有 `TrySetVisible`。这不是"按了没反应"，是代码不存在。

**第二道是运行期：** `PerformanceHudGate` 保持编译，但
- `IsAvailable(probe)` 只认 `Debug.isDebugBuild`，`null` 探针 fail-closed；
- `ShouldStartVisible = IsAvailable && IsRequested`，而 `IsRequested` 只解析 `-zfhud` /
  `ZBF_HUD ∈ {1,true,on,yes}`；
- 所以 **Release + `-zfhud` + `ZBF_HUD=1` 仍然 false**；`TrySetVisibleForTests` 也先过
  `IsAvailable`，注入 `() => false` 时**连宿主 GameObject 都不创建**（测试断言 `IsHosted == false`）。
- 默认（开发包、无参数）不可见；开发包内 F3 才切换。没有"强制打开"的公开入口。

### 4. 关闭时真的不采样

- `ProfilerRecorder` 只在 `SetVisible(true) → Sampler.Start()` 创建；
  `SetVisible(false) → Stop() + Dispose()`（`internal static LiveSamplerCount` 归零，可机检）。
- 不可见时 `Update` 只做一次 `GetKeyDown(F3)`（等按键不是采样），随后立即 `return`：
  不 tick FPS、不读计数器、不拼字符串。
- 自举 `AutoBoot` 在 `Application.isBatchMode` 直接返回（CI/测试零扰动），非开发包同样返回。

### 5. 节流与"不挡点击"

- `HudRefreshThrottle` 默认 **0.25 s（4 Hz）**；构造器把合法区间钉在 `[0.25, 0.5]`（4–2 Hz），
  越界直接抛 `ArgumentOutOfRangeException`。首次显示立即刷新一次，之后按节流。
- 呈现只用 `GUI.Box` / `GUI.Label`：不调用 `Event.current.Use()`，宿主对象没有
  Canvas / Graphic / Collider / 子物件（测试断言）→ 不产生 GraphicRaycaster，也不参与物理射线。
  `GUI.depth = -1000` 只决定绘制顺序，不消费事件。

### 6. 开销观察（诚实口径）

- 隐藏：每帧 1 次 `GetKeyDown` + **0 个 recorder / 0 次计数器读取 / 0 次字符串分配**；宿主只是一个空 GameObject。
- 显示：每帧 1 次 FPS 环形缓冲写入；每 250 ms 3–4 次 recorder 读取 + 1 次约 120 字符的 StringBuilder 重建。
- 批处理 `-batchmode` 无渲染负载、无 vsync，前后 FPS 差不可信，**没有编数字**；真实 FPS 差与
  DrawCall 量级核对留给编辑器 Play Mode 的人工验收（V1/V2）。

### 7. 测试与变异

- 新增 **14** 条 EditMode：门控三态（dev 默认关 / dev 参数开 / release 全拒）、环境变量解析、
  999→`999` / 1234→`1.2k` / 999999→`1.0M`、0 值不出 NaN、ms/fps 占位符、节流 4 Hz 与区间约束、
  懒采样开关、Release 不建宿主、宿主无射线面。
- **EditMode 825/825 通过，exit 0**（`TempLogs/hud_editmode_results.xml`；811 基线 + 14 新增）。
- **PlayMode 41/41 通过，exit 0**（`TempLogs/hud_playmode_results.xml`）。
- **变异（有牙）**：把 `ShouldStartVisible` 里的 `if (!IsAvailable(...)) return false;` 注释掉 →
  仅 `ReleaseBuild_RefusesEveryEnableRequest` 变红（4 用例 3 过 1 挂，
  `TempLogs/hud_mutant_results.xml`）；还原后恢复全绿。
- 首轮曾有 1 挂：`Count(999999)` 输出 `1000.0k`，已改为进位 `1.0M` 并补断言。

### 8. 未验证项 / 风险

1. **未在 GUI 编辑器里按 F3、未目测数值**：批处理无法合成长按/按键，且无渲染负载。V1/V2 需验收方在
   Play Mode 人工确认（DrawCall 与 Frame Debugger 同量级）。
2. **未构建 Release/Dev 玩家包**（按简报不跑真机）。"Release 打不开"由编译期 `#if` + 单测双证据，
   未做二进制符号检查。
3. GPU 时间依赖平台计数器；编辑器 D3D11/12 一般可用，批处理下可能 `!Valid` → 显示 `—`，属预期。
4. 显存口径是 graphics driver 分配量（任务指定），不是整卡占用；换管线/换平台读数会变。

### 9. 现场状态

- **未 git commit**；本任务只新增 6 个 `.cs`（+ Unity 生成的 meta），`TASKS/HANDOFF/WORKLOG/README` 未动。
- 跑完 `git status`：无 `URP_*.asset` / `QualitySettings.asset` / `ProjectSettings` 被改写。
- **注意：工作区有并行代理的未提交改动**（`Native/zrender/*`、`Game/*`、`ZFrame/Gameplay/Commands` 等），
  非本任务产出。最后一次"仅跑 ZFrame.Debug.Tests"的过滤回归时，并行任务的
  `Game/Tests/View/GameHudUndoTests.cs` 缺 `using Game.View.UI;` 导致整树编译失败（未修，不属本任务边界）。
  在此之前已完整跑通的 836 用例运行中，我新增的 14 条全部通过；4 条失败均来自并行任务的
  `CommandManagerTests`×3 与 `GameHudLeakTests`×1，与本任务无关。


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
