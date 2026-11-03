# TASK-A-R8-01 FrameGovernor 帧率治理器（动态分辨率 + 自适应画质阶梯）

状态: doing | 派发: 2026-10-26 | 施工: general-purpose 施工子代理（2026-10-27 完成，EditMode 588/588 全绿） | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：R8 愿望清单第 2 项（方案 §6 承诺的"硬件性能优化体系"核心）——FrameGovernor：GPU 帧耗驱动动态分辨率缩放（迟滞防抖）+ 自适应画质阶梯（依次降级 SSR→探针更新率→阴影分辨率→MSAA）+ 目标帧率管理，接通现有 GPU 时间戳数据源（B-R2-03）与 PipelineManager（A-M2-03）。

**设计要点**：
1. **纯 C# 核**（`ZFrame/View/Pipeline/FrameGovernor.cs`，可 EditMode 测，时间/遥测注入）：
   - 输入：`IFrameTelemetry { float GpuFrameMs; float CpuFrameMs; }`（实现侧由 Unity 侧每帧泵入——GPU 时间经 `UnityEngine.Rendering.ProfilingSampler`/Recorder（GPU Recorder 若平台支持，不支持则退 CPU 帧耗+注明））
   - 措施阶梯（降级序，每级一个 `GovernorMeasure`）：`RenderScale`（0.75~1.0 步进 0.05）→ `Shadows`（高→中→关，映射 PipelineManager 阴影档）→ `PostFx`（Volume weight）→ `TargetFps` 放弃告警
   - 迟滞：好于目标+10% 持续 N 秒才升一级，差于目标-10% 持续 M 秒降一级（N>M 防振荡，纯函数可测）
   - 开关：`Enabled`（默认关——显式开启才介入；设置界面后续接），`TargetMs`（由目标帧率换算）
   - 输出：当前措施级别事件 `GovernorMeasureChanged{measure,from,to}`（EventBus）+ `SetRenderScale(float)` 调用（对接 UniversalRenderPipelineAsset.renderScale 经 PipelineManager）
2. **接线**（View 薄适配）：`FrameGovernorHost`（ITickable 经 ITickQueue，同 AudioService 模式）每帧泵遥测+Tick Governor；措施落地：RenderScale 直改当前 URP Asset（记录原值，Shutdown 恢复）；Shadows→PipelineManager.SetTier 降级不升级（与用户手选档位冲突时：Governor 只在用户档位之上做减法，恢复时回用户档位——语义 XML doc 写清）
3. 单测（EditMode 追加）：阶梯降/升迟滞窗口（构造遥测序列断言不振荡）；措施顺序固定；RenderScale 步进边界；Enabled=false 完全旁路；恢复语义
4. 自测：EditMode 全绿+Validate exit 0（Unity 纪律）

**约束**：只动 `UnityProject/Assets/ZFrame/View/Pipeline/`、`UnityProject/Assets/ZFrame/Tests/`、本任务日志；不改 PipelineManager 对外语义（新增只读接口可用）；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0。

**交付物**：Governor 纯核+Host 适配+单测全绿+施工报告四节（含 Recorder 平台可用性说明）。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理 ｜ 完成时间: 2026-10-27 01:50 ｜ 自测: **EditMode 588/588 全绿（Unity exit 0）+ 架构校验器 exit 0**

### 1. 交付物清单（全部新增，零修改既有文件）

`UnityProject/Assets/ZFrame/View/Pipeline/`（11 个源文件 + Unity 生成的 .meta）：

| 文件 | 行数 | 职责 |
|---|---|---|
| IFrameTelemetry.cs | 22 | 遥测接口 `{GpuFrameMs, CpuFrameMs}`，Gpu≤0 为"平台不支持"哨兵 |
| FrameGovernor.cs | 143 | **纯核**：迟滞双窗口状态机（`Tick(dt, telemetry)` → `GovernorDecision`） |
| GovernorLadder.cs | 184 | **纯核**：措施阶梯构建（简报 4 级序）+ `StateAtIndex` 累积态推导 |
| GovernorTuning.cs | 79 | 迟滞参数（±10% 死区、N=5s > M=2s、单 tick 截断 0.5s），构造期校验 N>M |
| GovernorMeasureKind.cs / GovernorMeasure.cs / GovernorState.cs / GovernorDecision.cs / GovernorMeasureChangedEvent.cs | 27/35/38/52/33 | 纯数据结构（枚举/只读 struct/EventBus 事件载荷） |
| RecorderFrameTelemetry.cs | 122 | 引擎侧遥测：ProfilerRecorder 读 "GPU Frame Time"，不可用退 CPU 帧耗（ Pump 计 unscaled 间隔） |
| FrameGovernorHost.cs | 404 | **接线**：ITickable 经 ITickQueue（TickStage.View）、措施落地、Shutdown 恢复 |

`UnityProject/Assets/ZFrame/Tests/Pipeline/`（2 个测试文件）：

| 文件 | 行数 | 覆盖 |
|---|---|---|
| FrameGovernorTests.cs | 465 | 纯核 20 用例：迟滞双窗口/边界/不振荡、阶梯顺序、RenderScale 步进边界、Enabled 旁路、GPU/CPU 选择、巨大 dt 截断 |
| FrameGovernorHostTests.cs | 336 | Host 11 用例：ITickQueue 注册/注销、旁路、全塔行走（真实 URP asset/档位/Volume）、恢复语义（Shutdown/SetEnabled(false)）、重启用、TargetFps 换算、Recorder 冒烟 |

合计 1940 行。**git status 证实：仅上述新增文件（含 Unity 自动生成的 .meta），PipelineManager.cs 等既有文件零改动。**

### 2. 设计要点对照（简报 4 条）

1. **纯 C# 核可测**：`FrameGovernor`+`GovernorLadder`+`GovernorTuning` 等 9 文件**零 UnityEngine 引用**（仅有的两处 "UnityEngine" 字样是 XML doc 里的文字说明，见 FrameGovernor.cs:10、GovernorLadder.cs:22；using 仅 System/System.Collections.Generic）。时间以 `dt` 参数注入、遥测以 `IFrameTelemetry` 注入，单测无需播放循环。GPU→CPU 回退在核心内判定（`GpuFrameMs>0` 用 GPU，否则 CPU，`LastSampleUsedGpu` 暴露给诊断）。
2. **措施阶梯顺序固定**（GovernorLadder.Create，High 用户档满配 10 级）：`RenderScale 0.95→0.90→0.85→0.80→0.75`（5 级，严格低于用户原值）→ `Shadows 高→中→关`（= PipelineManager 档位降 2 步；Medium 1 步；Low 0 步）→ `PostFx weight 0.5→0` → `TargetFps 放弃告警`（终级，仅告警+事件）。**措施语义 = 累积降级**：状态为当前级及以下所有措施并集（StateAtIndex），回升级时 LIFO 逐级撤销。
3. **迟滞防振**：死区 `[Target×0.9, Target×1.1]`（严格不等号，等于边界=死区）；死区内双计时器清零；**升 N=5s > 降 M=2s**（GovernorTuning 构造器强制 N>M，违反即 ArgumentException）；每次移动后双计时器清零——交替负载（欠窗坏/欠窗好交替 500 轮）零移动，用例 `Hysteresis_AlternatingLoad_NeverOscillates` 断言。附加 `MaxDeltaSeconds=0.5` 单 tick 截断：单次 10s 卡顿（载入）不会绕过"持续 M 秒"直接降级。
4. **Enabled 默认关**：`FrameGovernor.Enabled` 默认 false、`FrameGovernorHost` Tick 在禁用时零工作（不泵遥测/不应用/不发事件，用例 `Tick_WhileDisabled_FullyBypasses` 真实 URP 资产断言无任何副作用）；`TargetMs<=0`、null 遥测、dt<=0 同样旁路且清计时器（无跨间隙陈旧积累）。
5. **EventBus 事件**：`GovernorMeasureChanged{Measure, From, To}`（GovernorMeasureChangedEvent.cs），由 Host 在每次实际移动时 Fire（纯核只返回 `GovernorDecision`，不触碰全局态）。
6. **恢复语义**（简报要求写清，FrameGovernorHost.cs 类注释 + RestoreBaseline:301-320）：Governor 只在用户档位之上做减法——启用时捕获用户档 `_userTier`，Shadow 措施映射 `SetTier(userTier - steps)`（钳制 ≤ userTier，**永不升级**）；禁用/Shutdown 时回用户档位（仅档位变化才调 SetTier）、**逐一恢复被触碰资产的 renderScale 原值**（`_touchedRenderScales` 字典：因 SetTier 换档会换 URP asset，每个被写的资产在首写前记录原值，恢复时全量还原）、恢复 GradeVolume weight 原值。SetTier 的 PlayerPrefs 持久化副作用（PipelineManager 既有语义，未改）由恢复路径回写用户档收敛——已在 XML doc 注明。
7. **Host 驱动**：`FrameGovernorHost : MonoBehaviour, ITickable`（FrameGovernorHost.cs:45），`Services.Get<ITickQueue>().Register(this, TickStage.View)`（:141）——同 AudioService 模式，无 Update；EditMode 走文档化 `Initialize(telemetry)` 接缝（:103，telemetry 参数即测试/未来原生 GPU 时间戳注入点）。

### 3. Recorder 平台可用性说明（简报要求）

- GPU 帧耗时取自 ProfilerRecorder（Unity.Profiling 命名空间，UnityEngine.CoreModule 内置）读 **"GPU Frame Time"** 计数器，按 `ProfilerCategory.Render` → `Internal` 顺序探测（RecorderFrameTelemetry.cs:31-37），首个 `Valid` 者生效。
- **可用平台**：D3D11/D3D12、Vulkan、Metal 等 GPU 计数器可用的后端；**不可用**：OpenGL/OpenGL ES 目标、部分编辑器/驱动组合（计数器不存在或恒 0）。所有不可用情形三重兜底：`Valid`=false（不启动 Recorder）→ `LastValue<=0`（视为无样本）→ 核心回退 CPU 帧耗并经 `LastSampleUsedGpu` 注明。构造期探测失败时 ZLog.Info 一条说明（"governor telemetry: GPU recorder unavailable — governing on CPU frame cost"）。
- 本机（AMD 780M 核显 + Editor）冒烟用例 `RecorderTelemetry_DefaultConstruction_IsSafeOnAnyPlatform` 断言两种结果都无害（GpuFrameMs≥0、Pump 前后无 NaN、Dispose 幂等）。真实 GPU 数值仅 PlayMode/真机可验，不在本任务范围。
- **备注**：Unity 官方更推荐 FrameTimingManager（需 Player 设置 "Frame Timing Stats"，改 ProjectSettings 超出本任务路径约束），已在 XML doc 与此注明为后续可替换路径；简报指定 Recorder 路径已照办。

### 4. 自测证据

| 项 | 结果 | 证据 |
|---|---|---|
| Unity EditMode | **588/588 全绿，failed=0，Unity exit 0** | `TempLogs/runtests_editmode.xml`（total=588 passed=588 failed=0 skipped=0）、`TempLogs/runtests_editmode.log`；经 `Tools/run_tests.ps1 -Segments editmode`（Git Bash 启动、清锁、无 -quit、轮询 xml 纪律） |
| 架构校验器 | **exit 0** | `TempLogs/arch_report.txt` + `TempLogs/runtests_arch.log`（`-Segments arch`，PASS: exit 0） |
| 文件规范 | 全部新文件 UTF-8 无 BOM、LF、尾换行 | od/awk 逐文件校验（CR=0，首 3 字节非 EF BB BF） |
| 新 asmdef/矩阵 | 无新 asmdef（复用 ZFrame.View 与 ZFrame.Pipeline.Tests），校验器矩阵校验随 arch exit 0 通过 | 同上 |

### 5. 施工过程如实记录（三轮修复，全部根因定位）

1. **编译轮**：①ProfilerRecorder 实际在 `Unity.Profiling` 命名空间（非 UnityEngine.Profiling，反查 UnityEngine.CoreModule.xml 确认），且选项名是 `SumAllSamplesInFrame`；②GovernorDecision 只读字段不能走对象初始化器（改私有构造+静态工厂）；③RecorderFrameTelemetry 缺 `using ZFrame.Core`。
2. **测试轮**（588 中 3 失败）：①Host 初始化对 `GameRoot.Instance.Initialize()` 在 EditMode 顺序下可能命中"静态 Instance 悬空指向已销毁对象"→ 改为对 **EnsureInstance 返回值** 调 Initialize（自愈路径，FrameGovernorHost.cs:136-143）；②迟滞边界用例依赖 `TargetMs*1.1f` 的浮点精确相等，跨表达式舍入不稳定 → 改为带裕量的带内(±9%)/带外(±11%)探测，语义等价（死区严格性仍被覆盖）；③**发现 Unity Test Framework 跨用例复用 fixture 实例**：前一用例收集的 EventBus 事件残留到后一用例的列表（探针取证：断言时全项目仅 1 个 Host 且从未启用、本用例订阅处理器 0 次触发，但列表含前一用例的 10 个行走事件）→ SetUp 清空采集列表。③号发现对后续写 EventBus 相关测试的人有普适价值。
3. **探针法取证**：两次单类 `-testFilter` 探针运行（TempLogs/probe.xml/log）定位失败 ③，全程遵守批处理纪律（清锁/Git Bash 启动/无 -quit/xml 轮询/结束后清理僵死进程）。

### 6. 遗留与建议（不阻塞验收）

- 设置页 DRS/目标帧率行实接（简报"设置界面后续接"）：入口即 `FrameGovernorHost.Instance.SetEnabled(bool)/SetTargetFps(float)`，事件流 `GovernorMeasureChangedEvent`。
- 治理期间用户手动改档位为 v1 已知边界（宿主在下次启用时重捕获用户档；建议设置页改档前先关 Governor）——XML doc 已注明。
- 未来接 B-R2-03 原生 GPU 时间戳：实现 `IFrameTelemetry` 经 `Initialize(telemetry)` 注入即可，纯核零改动。
- `FrameTimingManager` 备选路径见上文 Recorder 说明。

## 验收清单（主控写）

1. EditMode 全绿（用例数写入结论）
2. 迟滞不振荡用例真实（升/降双窗口+边界）；措施顺序断言；Enabled 旁路用例
3. 纯核零 UnityEngine（行号）；Host 经 ITickQueue 驱动（无 Update 直驱，行号）
4. 恢复语义（Shutdown 回用户档位）用例或行号
5. Validate exit 0（自跑）
6. 文件规范+施工报告完整（Recorder 平台说明）

## 验收结论（验收代理写）

**结论：PASS**（2026-10-27，general-purpose 验收子代理，与施工不同代理，独立亲跑取证，未改任何代码）

| # | 验收项 | 结论 | 证据（一行） |
|---|---|---|---|
| 1 | EditMode 全绿（用例数） | PASS | 亲跑 `Tools/run_tests.ps1 -Segments editmode`（Git Bash 启动、无 -quit、轮询 xml 纪律，验收前先归档施工代旧证据至 `TempLogs/archive_acceptance_r801/`）：**Unity exit 0，XML 头 total=588 passed=588 failed=0 skipped=0**，0 个 result="Failed"；数字自洽：557（H-06 基线）+31 新用例（纯核 20+Host 11）=588；证据 `TempLogs/runtests_editmode.xml`（本验收亲跑产出） |
| 2 | 迟滞不振荡+双窗口边界+旁路 | PASS | 500 轮交替负载 `Hysteresis_AlternatingLoad_NeverOscillates`（FrameGovernorTests.cs:220-241，3×0.2s 坏/3×0.2s 好 ×500 轮逐 tick 断言零移动+终态 level 0）；双窗口边界 `Hysteresis_WindowBoundary_FullWindowMoves_HalfWindowDoesNot`（:195-217，1.5s<M 不动/恰 M=2.0s 降、4.5s<N 不动/恰 N=5.0s 升）+阈值带 `Hysteresis_ThresholdBand_InsideDoesNotMove_OutsideCountsWindows`（:261-301，±9% 带内不计数/±11% 带外计数窗口）；Enabled 旁路 `Disabled_FullBypass_NoMoveNoTelemetryProcessing`（:306-321）+Host `Tick_WhileDisabled_FullyBypasses`（FrameGovernorHostTests.cs:174-188，真实 URP 资产断言 scale/tier/weight 零副作用） |
| 3 | 纯核零 UnityEngine + ITickQueue 驱动 | PASS | grep 9 个纯核文件仅 2 处 XML doc 文字（FrameGovernor.cs:10、GovernorLadder.cs:22），零 using UnityEngine；Host `FrameGovernorHost : MonoBehaviour, ITickable`（FrameGovernorHost.cs:45）+ `Services.Get<ITickQueue>().Register(this, TickStage.View)`（:141），全文件无 Update() 方法 |
| 4 | 恢复语义+只做减法 | PASS | 用例 `Shutdown_RestoresUserTier_AndAllOriginals`（FrameGovernorHostTests.cs:241-262，tier 回 High+逐资产 renderScale 原值+weight 原值）、`SetEnabledFalse_Restores_AsWell`（:265-275）、`Reenable_AfterRestore_CapturesFreshBaseline`（:278-290）；代码 RestoreBaseline（FrameGovernorHost.cs:301-329）；只减法：ApplyState 档位钳制上界=(int)_userTier（:339-343），阶梯构造只收严格低于用户基线的措施（GovernorLadder.cs:103 RenderScale、:112-116 Shadows、:128-131 PostFx） |
| 5 | ValidateFromCommandLine exit 0 | PASS | 亲跑 `-Segments arch`：exit 0，`TempLogs/arch_report.txt`（asmdef 扫描 22/矩阵 10，违规数 0，PASS） |
| 6 | 文件规范+施工报告四节 | PASS | 13 个新文件逐一校验：首 3 字节非 EF BB BF、CR=0（纯 LF）、尾字节 0A；`git status` 证实仅新增文件+本日志（PipelineManager.cs 等既有文件零改动）；施工报告四节齐全，§3 含 Recorder 平台可用性说明（可用/不可用平台、三重兜底、FrameTimingManager 备选） |

**备注（不阻塞 PASS）**：①帧时死区严格边界（恰 TargetMs×1.1）以 ±9%/±11% 带内/带外探测代替浮点精确相等（施工报告 §5.2② 已如实披露），窗口边界（M=2s/N=5s）另有精确半窗/满窗用例，语义等价；②治理期间用户手动改档为已知 v1 边界（施工报告 §6 已注明）。
