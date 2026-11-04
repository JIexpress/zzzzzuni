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

> 本结论由**独立验收代理**（与施工不同实例）填写，只认本次会话自跑 / 自查所得证据；施工报告中的数字仅作旁证并明确标注，未当作本代理的实测结论。

### 验收代理标识
- 角色：A-GPU-01 **独立验收代理**（非施工代理；本会话未修改任何业务代码、未 commit、未触碰 A-R9-14 文件）。
- 日期：2026-11-04（实跑 19:02–19:05 CST；results.xml UTC 11:02:59Z / 11:03:59Z）。
- 仓库 HEAD：进场 `2a848da`，收场 `2a848da`（未产生新提交）。
- 可写范围：仅本文件「## 验收结论」一节；UTF-8 无 BOM + LF。

### 总体结论
**PASS with 受限**

V2（静态口径）、V3、V4、V5 判定 PASS；V1 为**受限，未目测**（题目预期的批处理限制）；V6 实跑 EditMode 全绿、PlayMode 唯一失败经定位属**并行任务 A-R9-14 污染**，非 HUD 自身缺陷（按主控口径不记为 HUD FAIL）。核心门控编译期 + 运行期双证据齐全、HUD 自身零回归、现场干净、无 commit、无变异残留。

### V1 — 编辑器 Play Mode 按 F3 开/关 HUD、显示六项指标
**判定：受限，未目测**（未假装按过 F3；批处理无法合成按键且无 GUI 渲染负载）。

静态代码证据：
- F3 分支：`Hud/PerformanceHud.cs:27` `ToggleKey = KeyCode.F3`；`:137-140` `Input.GetKeyDown(ToggleKey) → SetVisible(!_visible)`；`:152-169` `SetVisible` 内 `Start/Stop + RefreshText`。
- 文本字段：`:171-182` `RefreshText()` 拼接 `FPS / Draw / SetPass / Tris / VRAM / GPU` 六行（`:174-180`）。
- GUI 绘制调用：`:186-207` `OnGUI()`：`GUI.depth = -1000`（`:202`）、`GUI.Box(rect, GUIContent.none)`（`:205`）、`GUI.Label(..., _text, _labelStyle)`（`:206`）；`:188` `if (!_visible) return;`。
- 门控接线：`:65-79` `AutoBoot`、`:84-91` `TrySetVisible` 均先过 `PerformanceHudGate.IsAvailable`。
- 机检（非目测）：`Tests/Debug/PerformanceHudTests.cs:231-235` `ToggleKey_IsF3` 断言 F3 常量，实跑绿。

### V2 — 数值合理
**判定：PASS（静态口径）/ 实测数字受限**（不编造 DrawCall 实测值）。

- ProfilerRecorder 名称：`PerformanceHudSampler.cs:24/27/30/33` = `"Draw Calls Count"` / `"SetPass Calls Count"` / `"Triangles Count"` / `"GPU Frame Time"`；类别 `ProfilerCategory.Render`（`:94-96`），GPU 依次探测 `{Render, Internal}`（`:35-39, :98-109`）。跨文件对照：`Game/Editor/ParityBench.cs:187` 用 `ProfilerCategory.Render, "Draw Calls Count"`；`View/Pipeline/RecorderFrameTelemetry.cs:30` `GpuMarkerName = "GPU Frame Time"`、`:33-37` 同款 `{Render, Internal}` 探测——与项目既有同套写法一致。
- GPU ns→ms：`PerformanceHudSampler.cs:85` `nanoseconds * 1e-6`；对照 `RecorderFrameTelemetry.cs:89` `_gpu.LastValue * 1e-6f; // nanoseconds -> milliseconds`，换算一致。
- VRAM 口径：`PerformanceHudSampler.cs:61-75` `Profiler.GetAllocatedMemoryForGraphicsDriver()`，注释明确「graphics driver 分配量（字节），不是显存总量」；`HudTextFormat.cs:35-40` `/ 1048576.0` → MiB（标签 "MB"）。符合简报「graphics driver 分配量非整卡显存」。
- 取不到值降级（不出 NaN）：`PerformanceHudSampler.cs:147-152` `Read` → `!Valid/Count==0/负值` 均 0；`:82-86` `GpuMilliseconds` 不可用/无样本/≤0 → `-1.0`；`:66-74` `GraphicsMemoryBytes` catch → 0；`HudTextFormat.cs:18,37` 负值钳 0、`:43-51` `Milliseconds` NaN/∞/负 → `"—"`、`:54-58` `Fps` 同理；`View/DebugPanel/FpsCounter.cs:69-75` 无样本 Fps=0。测试 `Megabytes_...NeverPrintsNaN` / `Milliseconds_AndFps_HandleZeroAndUnavailable`（`PerformanceHudTests.cs:107-134`）实跑 3/3 绿。
- FPS 平滑：`PerformanceHud.cs:30` 30 帧窗口（≈0.5s@60fps）+ `:145` `_fps.Tick(Time.unscaledDeltaTime)`。
- 受限：批处理无渲染负载、无 GUI 目测，**未**核对 DrawCall 与 Frame Debugger 同量级，不给实测数字。
- 观察项（非缺陷）：`PerformanceHudSampler.cs:139` 用 `ProfilerRecorderOptions.Default`，而 `RecorderFrameTelemetry.cs:60-61` 用 `SumAllSamplesInFrame | WrapAroundWhenCapacityReached`；一帧多段时 GPU 读数口径可能略异，但 ns→ms 换算一致。

### V3 — Release 打不开（双证据）
**判定：PASS**

- **编译期**：`PerformanceHud.cs:1` `#if UNITY_EDITOR || DEVELOPMENT_BUILD || UNITY_INCLUDE_TESTS` 至 `:242` `#endif` **包裹整个文件**（含整个 `PerformanceHud` 类、F3 轮询 `:137`、ProfilerRecorder 引用、全部测试接缝 `:209-239`）。Release 玩家包三符号皆不定义 → 托管程序集里无此类型、无 F3 入口、无采样器。配套：`Tests/Debug/ZFrame.Debug.Tests.asmdef:21-23` `defineConstraints: ["UNITY_INCLUDE_TESTS"]`、`:10-12` `includePlatforms: ["Editor"]`，解释测试程序集为何能编译到该类（并非 Release 可开）。
- **运行期**：`PerformanceHudGate.cs:28-31` `IsAvailable(Func<bool>)` 只认注入的 `isDevelopmentBuild()`，`null` 探针 fail-closed；生产判据由 `PerformanceHud.cs:93-96` `IsDevelopmentBuild() => Debug.isDebugBuild` 提供。`PerformanceHudGate.cs:52-59` `ShouldStartVisible = IsAvailable && IsRequested`，其中 `:57` `if (!IsAvailable(...)) return false;`；`IsRequested`（`:34-46`）只解析 `-zfhud` / `ZBF_HUD ∈ {1,true,on,yes}`（`:62-70`）。三个入口 `AutoBoot:69`、`TrySetVisible:86`、`TrySetVisibleForTests:219` 都先过 `IsAvailable` → Release + 所有参数仍 false，`ReleaseBuild_RefusesToShow_AndCreatesNoHost` 甚至不建宿主。
- **门禁测试是否有牙（核对）**：
  - 静态逐行推理：注释掉 `PerformanceHudGate.cs:57` 的 `if (!IsAvailable(...)) return false;` 后，`ShouldStartVisible(() => false, new[]{"-zfhud"}, envOn)` 将返回 `IsRequested(...)=true`，而 `PerformanceHudTests.cs:67-69`（及 `:70-71`）断言 `Assert.IsFalse(...)` → 必然变红，故判据有牙。
  - 实跑 EditMode：`PerformanceHudGateTests` 4/4、`PerformanceHudTests` 4/4 全 Passed（见 V6），门禁用例确实被执行且为绿。
  - **定向变异写入：未做**（原因见「变异复做结果」）；施工方 `TempLogs/hud_mutant_results.xml`（total=4 / passed=3 / failed=1，2026-11-03 16:41Z）仅作旁证，**非本代理复做**。

### V4 — 关闭时不持续采样
**判定：PASS**

- Start/Stop 成对：`PerformanceHudSampler.cs:90-113` `Start()`（建 4 个 recorder，`:112` `LiveSamplerCount++`）/ `:116-128` `Stop()`（`:120-123` Dispose 全部，`:127` `LiveSamplerCount--`）；`:130-133` `Dispose()` 调 Stop。
- 宿主侧：`PerformanceHud.cs:161` 可见→`Start()`、`:166` 隐藏→`Stop()`；`:123`(OnDestroy)、`:129`(OnApplicationQuit)、`:227-236`(ShutdownForTests) 亦 Stop。
- LiveSamplerCount 归零：`PerformanceHudTests.cs:199-218` `Hidden_DoesNotSample_Visible_StartsAndStops` 断言 `baseline+1` → 回到 `baseline`，实跑绿。
- 不可见时 Update 早退：`PerformanceHud.cs:142` `if (!_visible) return;`（仅先做一次 `GetKeyDown`，`:137-140`），之后才 `:145` `_fps.Tick` / `:146` 节流 / `:148` `RefreshText`（`:171-182` 读计数器 + 拼串）。
- AutoBoot 在 batchmode 直接返回：`PerformanceHud.cs:68` `if (Application.isBatchMode) return;`（方法体第一句）。

### V5 — 节流 4 Hz（区间 [0.25,0.5]）+ 不拦截点击
**判定：PASS**

- 节流：`HudRefreshThrottle.cs:13-19` 常量 `MinIntervalSeconds=0.5f`(2Hz) / `MaxIntervalSeconds=0.25f`(4Hz) / `DefaultIntervalSeconds=0.25f`(4Hz)；`:25-37` 构造器对 `interval < 0.25 || > 0.5 || NaN || ∞` 抛 `ArgumentOutOfRangeException`（合法区间确为 `[0.25,0.5]`）；`:46-57` `TryConsume` 距上次不足间隔返回 false。`PerformanceHud.cs:146` 以 `Time.unscaledTimeAsDouble` 驱动，命中才 `:148` `RefreshText()`；`:160-162` 首次显示 Reset + 立即刷新一次。
- 测试：`Refresh_IsThrottledToFourHertz`（`:138-149`，0.10/0.24 拒绝、0.25 放行）、`Interval_IsKeptWithinTwoToFourHertz`（`:151-165`，0.1f/0.75f/NaN 抛异常）、`Reset_AndClockRollback...`（`:167-180`），实跑 3/3 绿。
- 不拦截点击：
  - 全文搜索 `Hud/` 目录：`Event.current` 仅出现在 `PerformanceHud.cs:20` 的**注释**里，**无**任何 `Event.current.Use()` 调用。
  - 宿主无 Canvas/Graphic/Collider：`PerformanceHud.cs:98-108` `EnsureHost` 仅 `new GameObject` + `AddComponent<PerformanceHud>`；`PerformanceHudTests.cs:241-252` `HudHost_HasNoRaycastSurfaces` 断言 `Canvas==null`、`Collider==null`、`childCount==0`、`hideFlags=HideInHierarchy`，实跑绿。
  - `GUI.depth = -1000`（`:202`）只决定 IMGUI 绘制顺序，不消费事件；控件仅 `GUI.Box` / `GUI.Label`（`:205-206`），无交互控件。

### V6 — 回归（实跑 + 现场核对）
**判定：PASS（HUD 自身零回归）/ 受限（脚本级 RESULT: FAILED 由并行任务污染）**

实跑：PowerShell 工具先清 `http_proxy/HTTP_PROXY/https_proxy/HTTPS_PROXY/all_proxy/ALL_PROXY/no_proxy/NO_PROXY` 的 Process 级变量（回显「剩余代理变量: 已清空」），再以 `&` 调用 `Tools/run_tests.ps1 -Segments editmode,playmode`；脚本自带查进程 / 删 `UnityLockfile` / 经 Git Bash 启动 / 无 `-quit` / xml 僵尸 `taskkill` / 证据新鲜度校验。证据以 results.xml 为准：

| 平台 | results.xml | total | passed | failed | skipped | Unity exit |
|---|---|---|---|---|---|---|
| EditMode | `TempLogs/runtests_editmode.xml` | **873** | **873** | **0** | 0 | 0 |
| PlayMode | `TempLogs/runtests_playmode.xml` | **44** | **43** | **1** | 0 | 2 |

脚本 SUMMARY：`editmode PASS`；`playmode FAIL`；`RESULT: FAILED (1 failed: playmode)`。xml 时间戳 19:03 / 19:04，脚本回显 `evidence: results.xml written by this run`，为本次新写而非陈旧文件。

- HUD 自身 14 条 EditMode 全绿（逐 fixture）：`PerformanceHudGateTests 4/4`、`HudTextFormatTests 3/3`、`HudRefreshThrottleTests 3/3`、`PerformanceHudTests 4/4`（均 `result="Passed" failed="0"`）。
- EditMode 873 ≥ 简报基线 811，且全绿 → 不降。
- **唯一失败归因（并行任务污染，非 HUD 缺陷）**：`ZFrame.PlayMode.Tests.GameAudioBindingsPlayModeTests.SceneLoadedEvent_StartsAReallyPlayingBgmTrack_VolumeRidesTheLoopTick`，报错 `after 1 s of loop ticks the crossfade must have faded the track up (volume > 0.5) ... Expected: greater than 0.5f / But was: 0.0f`，堆栈 `UnityProject/Assets/ZFrame/Tests/PlayMode/GameAudioBindingsPlayModeTests.cs:144`。该文件是 A-R9-14 的**未提交新增文件**（`git status` 中 `?? ...GameAudioBindingsPlayModeTests.cs`）。PlayMode xml 中 `PerformanceHud|HudTextFormat|HudRefreshThrottle|HudGate` **0 命中**，`result="Failed"` 的 test-case **恰好 1 个**；`GameAudioBindingsPlayModeTests` fixture 共 3 用例（2 过 1 挂）→ 44 = 41 原有 + 3 新增(A-R9-14)，**原有 41 条全绿**，HUD 未引入 PlayMode 回归。
- **反馈主控**：请 A-R9-14 处理 `GameAudioBindingsPlayModeTests.cs:144`（AudioService loop tick / crossfade 未推进）；本代理**未越界替它修**，未把该 FAIL 记为 HUD 自身缺陷。
- 未改原生代码 / 未改游戏内容：`git show --name-only a2a2c9e`（HUD 交付 commit）共 14 个文件，全部为 `ZFrame/View/Hud/` 6 个 `.cs` + `.meta`、`Hud.meta`、`Tests/Debug/PerformanceHudTests.cs`(+meta)、`Docs/Logs/TASK-A-GPU-01.md`；对 `Native/|Assets/Game/|DevConsole|Assets/Audio/|ZFrame/View/Audio` 过滤 **0 命中**。
- 未 commit：进场 / 收场 HEAD 同为 `2a848da`，`git log` 无新增提交；本代理未执行任何 `git commit` / `git add`。HUD 交付物由主控在 `a2a2c9e feat(hud)`（2026-11-04 00:54，zcode-orchestrator）提交，符合「施工不 commit、主控提交」治理，非违规。
- git status 摘要：改动**全部属于 A-R9-14**（`Docs/Demo.md`、`Assets/Audio/**`、`ZFrame/View/Audio/{AudioBus,AudioService}.cs`、`Game/View/Effects/GameAudioBindings.cs`、`Game/Tests/View/GameAudioBindingsTests.cs`、`ZFrame/Tests/PlayMode/GameAudioBindingsPlayModeTests.cs`、`Docs/Logs/TASK-A-R9-14.md`），与 HUD 无关；HUD 目录零未提交改动。跑测期间并行任务自身新增 `M Docs/Logs/TASK-A-R9-14.md`（进场时无），非本代理所为，未触碰。
- 现场干净：无 `URP_*.asset` / `QualitySettings.asset` / `ProjectSettings` 改写（**无需还原**）；无残留 `Unity.exe`、无 `UnityProject/Temp/UnityLockfile`；`TestReport.txt` 与 `TempLogs/` 均被 `.gitignore` 忽略，不污染工作区。

### 变异复做结果
**未做（明确写「未做」及原因）**。原因：本次【严格约束】把可写范围限定为「唯一可写文件 = `Docs/Logs/TASK-A-GPU-01.md` 的验收结论一节」，且「不得修改任何业务代码」。定向变异需临时改写并还原 `PerformanceHudGate.cs`（业务代码），与上述禁止性约束冲突；禁止性约束优先于 V3 中的「推荐」。为避免越界，改以「静态逐行推理（见 V3）+ 实跑门禁用例为绿 + 施工方 mutant xml 旁证（标注非我复做）」核对门控有牙。
- 施工方旁证（**非本代理复做**）：`TempLogs/hud_mutant_results.xml` total=4 / passed=3 / failed=1；施工报告 §7 称变红者为 `ReleaseBuild_RefusesEveryEnableRequest`。
- **无变异残留**：未触碰任何业务代码；`git status` 中 HUD 目录零改动，HUD 文件 `git diff` 为空。

### 已知受限清单
1. V1 未目测 F3 开/关与屏幕显示（批处理无法合成按键、无 GUI 渲染负载）；仅静态代码证据 + F3 常量机检，**未假装按过 F3**。
2. V2 未目测六项实测数值，未与 Frame Debugger / Profiler 同量级核对；**不编造 DrawCall 等实测数字**。
3. V3 未自行做定向变异写入（约束不允许）；「有牙」缺本代理的动态复红实证，以静态推理 + 实跑绿 + 施工旁证替代。
4. 未构建 Release / Dev 玩家包、未做二进制符号检查（简报不跑真机）；「Release 打不开」由编译期 `#if` + 运行期门禁 + 单测三证据支撑。
5. V6 PlayMode 1 条失败来自 A-R9-14 并行任务（已定位 `GameAudioBindingsPlayModeTests.cs:144` 并反馈主控，未越界修）；脚本级 `RESULT: FAILED` 由此产生，非 HUD 缺陷。
6. GPU 时间依赖平台计数器，批处理 / 部分平台 `!Valid` → 显示 `—`（预期降级）；显存为 graphics driver 分配量、非整卡显存，换管线 / 平台读数会变。
7. 观察项（非缺陷）：HUD sampler 用 `ProfilerRecorderOptions.Default`，与 `RecorderFrameTelemetry` 的 `SumAllSamplesInFrame|WrapAroundWhenCapacityReached` 不同，一帧多段时 GPU 读数口径可能略异；ns→ms 换算一致。

### 现场状态（数字 + 变异残留）
- EditMode **873/873/0**（exit 0）；PlayMode **44/43/1**（exit 2，唯一失败 = A-R9-14 `GameAudioBindingsPlayModeTests.cs:144`）；HUD 自身 **14/14** 全绿。
- HEAD `2a848da` → `2a848da`，**新增 commit = 0**；HUD 交付 commit `a2a2c9e`（主控提交，早于本次验收）。
- 工作区未提交改动：**全部 = A-R9-14**，HUD 相关 **0**；无 URP / QualitySettings / ProjectSettings 改写；无残留 Unity 进程 / 锁文件。
- **变异残留：无**（未做变异写入；未修改任何业务代码；本代理唯一写入为本文「验收结论」一节）。
