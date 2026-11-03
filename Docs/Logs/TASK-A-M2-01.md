# TASK-A-M2-01 SceneLoader 场景系统（异步加载/进度/场景间数据传递）

状态: done | 派发: 2026-10-24 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理（PASS 7/7） | commit: 8ca8d23

## 任务简报（主控写）

**目标**：A 轨 M2 第一件——场景系统：异步加载、进度事件、加载期间输入屏蔽、场景间 Payload 传递（不依赖静态变量）、进出场景钩子（存档/资源释放预留挂点）。UI 框架是 M3，加载屏本期用极简 Overlay（OnGUI 进度条，M3 后可换正式 UI）。

**设计要点**：
1. `ZFrame/View/Scene/`（放 View 程序集）：
   - `ScenePayload`：静态 `Dictionary<string, object>` 的类型安全封装？不——用实例：`SceneLoader.LoadScene(key, payload)`，payload 存 `Dictionary<string,string>`（字符串约定，跨域简单）+ `string ConsumePayload(key)`；XML doc 说明 M3 后可扩强类型
   - `SceneLoader`（MonoBehaviour，GameRoot 自动 AddComponent——GameRoot 在 Core，不能引 View！**同 DebugPanelRoot 模式：RuntimeInitializeOnLoadMethod 自建宿主或静态门面 `SceneLoaderFacade`**，报告说明挂载方式，保证 Core 零感知）
   - `LoadSceneAsync(string sceneName, Dictionary<string,string> payload = null)`：`SceneManager.LoadSceneAsync` allowSceneActivation=false 模式；进度事件 `ProgressEvent{float normalized}`（0..1，含激活段平滑——假进度插值：到达 0.9 后按剩余时间缓动到 1，纯函数可测）；加载期间 `InputBlocker.Active=true`（新小组件：静态计数器，InputBlocker.IsAnyBlocking()——InputSystem 桥接是 M4，本期只提供计数器 API，文档注明接入点）
   - 完成事件 `SceneLoadedEvent{name}` / 开始事件（EventBus）
   - 卸载钩子：`static event Action OnBeforeSceneUnload`（供 ResourceService.UnloadAllUnused/PoolRegistry 收缩挂接——本期接上这两个调用，方案 §6.5 要求）
   - 极简加载 Overlay：OnGUI 黑底+进度条（`#if DEVELOPMENT_BUILD || UNITY_EDITOR` 不限制——这是玩家可见功能，正常编译；样式极简）
2. 测试场景：`SCN_Empty`（空场景，BuildSettings 第二位）——由测试 Setup 生成或入库
3. 单测（ZFrame.View.Tests 新程序集 或并入现有 Debug.Tests——引用矩阵允许 View 测试吗？现有 ZFrame.Debug.Tests 引 Core+View+Editor 先例——复用该程序集追加）：
   - 进度平滑纯函数：0.9 前线性、0.9 后缓动到 1、单调不回退
   - Payload：set→consume 语义（consume 后清空）、无 payload null 安全
   - InputBlocker 计数：acquire/release 配对、嵌套计数
   - OnBeforeSceneUnload 触发顺序（真异步场景加载在 EditMode 难测——标注为 PlayMode 冒烟范围，报告说明）
4. 自测：EditMode 全绿 + ValidateFromCommandLine exit 0（Unity 纪律）

**约束**：只动 `UnityProject/Assets/ZFrame/View/`、`UnityProject/Assets/ZFrame/Tests/`、`UnityProject/Assets/Scenes/`（新 SCN_Empty）、`UnityProject/ProjectSettings/EditorBuildSettings.asset`（场景列表，脚本改）、本任务日志；不 git commit；UTF-8 无 BOM/LF；架构校验器保持 exit 0。

**交付物**：SceneLoader+Overlay+InputBlocker+单测+施工报告四节（含挂载方式说明）。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理 | 完成: 2026-10-24 | 未 git commit

### 1. 交付物清单（全部新建/改动均落盘）

代码（`UnityProject/Assets/ZFrame/View/Scene/`，命名空间 `ZFrame.View`，ZFrame.View 程序集，引用 Core/Data/Gameplay 合法）：

| 文件 | 内容 |
|---|---|
| `SceneLoader.cs` | MonoBehaviour 加载器（allowSceneActivation=false 门控 + 假进度缓动 + EventBus 三事件 + 卸载钩子 + Payload 交接） |
| `SceneLoaderFacade.cs` | 静态门面（Load/IsLoading/DisplayedProgress/ConsumePayload/TryPeekPayload/OnBeforeSceneUnload 转发），供其他层调用 |
| `ScenePayload.cs` | `Dictionary<string,string>` 封装，`ConsumePayload(key)` 读后即焚；null 安全 |
| `InputBlocker.cs` | 静态嵌套计数器：`Acquire()`（返回 IDisposable）/`Release()`/`IsAnyBlocking()`/`Reset()`；超释放抛异常 |
| `SceneProgressMath.cs` | 纯函数：`Remap`（0.9 前线性/0.9 后 smoothstep 缓动到 1/单调）、`EaseTail`、`NonRegressing` |
| `SceneLoadingOverlay.cs` | 极简 OnGUI 加载 Overlay：黑底全屏+居中进度条+百分比文案；**无 `#if DEVELOPMENT_BUILD` 包裹**（玩家可见功能，全构建类型编译），M3 后换正式 UI |
| `SceneLoadStartedEvent.cs` / `SceneLoadProgressEvent.cs` / `SceneLoadedEvent.cs` | EventBus 事件 struct（样式对齐 SaveCorruptEvent） |

Editor（`UnityProject/Assets/ZFrame/View/Editor/`）：`SceneProjectSetup.cs`——`#if UNITY_EDITOR` 包裹（Data/Editor/ConfigAssetSetup.cs 同模式，因 asmdef 内 Editor 子目录需宏守卫）。菜单 `ZFrame/Setup/Generate SCN_Empty + Build Settings`；批处理 `-executeMethod ZFrame.View.SceneProjectSetup.SetupFromCommandLine`。幂等：生成 `Assets/Scenes/SCN_Empty.unity`（DefaultGameObjects：内置 Camera+Light，无玩法内容），并把 BuildSettings 写为 `[SCN_Boot, SCN_Empty]`（SCN_Empty 第二位）。已实跑，产物与 meta 一并入库。

测试（`UnityProject/Assets/ZFrame/Tests/Debug/`，复用 ZFrame.Debug.Tests 程序集，asmdef 追加引用 `ZFrame.Data`——Tests 程序集被架构矩阵豁免）：
- `SceneSystemTests.cs`：SceneProgressMathTests 9 例 + ScenePayloadTests 8 例 + InputBlockerTests 5 例
- `SceneLoaderHookTests.cs`：5 例（卸载钩子 seam/ResourceService 调用顺序/Facade 转发/Payload null 安全/空场景名拒绝）

### 2. 挂载方式说明（Core 零感知，验收清单第 3 条证据）

**与 DebugPanelRoot 完全同模式：`RuntimeInitializeOnLoadMethod` 自建宿主，Core 全程不参与。**
- `SceneLoader.cs:84` `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]` → `AutoBoot()`（`SceneLoader.cs:85-95`）：自建 `~SceneLoaderRoot` GameObject + `DontDestroyOnLoad` + `AddComponent<SceneLoader>`。
- **Core 零引用证据**：`grep -rn "SceneLoader|InputBlocker" Assets/ZFrame/Core/` → **0 命中**（GameRoot 不感知；依赖边只有 View→Core，asmdef 矩阵未动、校验 PASS）。
- 选 BeforeSceneLoad（DebugPanel 用 AfterSceneLoad）：加载器必须在首个场景前就位，第 0 帧即可被请求加载。
- 对外入口是 `SceneLoaderFacade` 静态门面；`SceneLoader.ConsumePayload/TryPeekPayload` 为静态 API（payload 交接状态亦为 static，单例语义一致）。

### 3. 加载时序与关键决策

1. `LoadSceneAsync`：重入保护（加载中再请求 → ZLog.Error 拒绝）→ `NotifyBeforeSceneUnload()`（`SceneLoader.cs:150-161`：先 `OnBeforeSceneUnload?.Invoke()`，再 `Services.TryGet<IResourceService>` 则 `UnloadAllUnused()`（`:154-157`），后 `PoolRegistry.ClearAll()`（`:159`，池收缩，与 PoolRegistry 自身"Call on scene switches"契约一致））→ EventBus `SceneLoadStartedEvent` → `SceneManager.LoadSceneAsync` + `allowSceneActivation=false` → `InputBlocker.Acquire()`。
2. Update 泵：`SceneProgressMath.Remap(raw)` 线性段；raw 到 0.9 门后启动时长下限 `MinTailSeconds=0.4s`（unscaled）的 smoothstep 尾段缓动到 1，尾段走完才置 `allowSceneActivation=true`（短加载也有可读进度条）；进度仅变化时发 `SceneLoadProgressEvent`；全程 `NonRegressing` 防回退。
3. 完成：`SceneLoadedEvent` + `InputBlocker.Release()`（配对）+ payload 从 pending 提升为 current，供 `ConsumePayload`（读后即焚）。
4. 钩子设计：`OnBeforeSceneUnload` 是存档等外部系统的挂点（本期未强加订阅者）；ResourceService/PoolRegistry 两个内置清理由加载器直接调用（方案 §6.5 要求"本期接上"）。`NotifyBeforeSceneUnload` 是公开 seam，EditMode 可测时序（事件先于资源清理，测试断言真实）。

### 4. 测试与 PlayMode 冒烟说明

- **EditMode：`TempLogs/m2_01_editmode.xml` → `testcasecount="169" result="Passed" total="169" passed="169" failed="0" skipped="0"`（基数 142 + 新增 27）**。
- 真异步场景加载（started→progress→门控激活→loaded 全链路、allowSceneActivation 行为）依赖 PlayMode 的 SceneManager 泵，**EditMode 无法覆盖，标注为 PlayMode 冒烟范围**（验收/后续任务可用 SCN_Empty 做冒烟：从 SCN_Boot 调 `SceneLoaderFacade.LoadSceneAsync("SCN_Empty")` 观察事件与 Overlay）。EditMode 侧已用公开 seam `NotifyBeforeSceneUnload` 覆盖钩子触发顺序（stub IResourceService 断言"事件先于 UnloadAllUnused"）。

### 5. 自测证据（Unity 纪律）

- 三次批处理均 PowerShell `Start-Process -Wait` 捕获真实退出码；跑前确认无 Unity.exe、删 `UnityProject/Temp/UnityLockfile`；本轮无僵死进程、无撞锁重试。
- Setup：`-executeMethod ZFrame.View.SceneProjectSetup.SetupFromCommandLine` → **exit 0**（`TempLogs/m2_01_setup.log`）；SCN_Empty.unity+meta 入库，`EditorBuildSettings.asset` 读验：SCN_Boot（enabled）→ SCN_Empty（enabled）第二位。
- EditMode：**exit 0**，169/169（`TempLogs/m2_01_editmode.xml`）。
- 架构校验：`-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine` → **exit 0**，`TempLogs/arch_report.txt`：违规数 0 PASS（asmdef 扫描 15，期望矩阵 10 条未改）。

### 6. 过程记录与偏差

- 过程修复 2 次编译错（首跑 exit 1）：① `_currentPayload` 实例字段被静态方法引用 → 改 static；② 测试文件漏 `using ZFrame.View;`。修复后复跑全绿。低级失误，记录备查。
- 文件规范自检：新增 12 文件全部 UTF-8 无 BOM、LF、尾换行、无尾随空格（od/grep 逐文件核验）；一个类型一个文件（测试文件按 DebugPanelTests 先例分组命名）。
- 约束遵守：仅动 `View/`、`Tests/`、`Scenes/`、`ProjectSettings/EditorBuildSettings.asset`（经 SceneProjectSetup 脚本）、本任务日志与 TempLogs 运行证据；未 git commit。

## 验收清单（主控写）

1. 自跑 EditMode 全绿（用例数写入结论）
2. 进度平滑/单调性纯函数断言真实；Payload consume 语义用例；InputBlocker 嵌套配对用例
3. Core 零感知：SceneLoader 不被 Core 引用（挂载模式行号证据）
4. 卸载钩子已接 ResourceService/PoolRegistry（行号）
5. ValidateFromCommandLine exit 0（自跑）
6. 文件规范抽查+施工报告完整
7. SCN_Empty 在 BuildSettings（读 asset 或验收时脚本核对）

## 验收结论（验收代理写）

**验收**: general-purpose 验收子代理（独立于施工代理，未改任何代码）| 验收时间: 2026-10-24 | **结论: PASS（7/7）**

| # | 项 | 结果 | 证据 |
|---|---|---|---|
| 1 | 自跑 EditMode 测试 | PASS | 验收代理自跑（无 -quit，`-runTests`，跑前确认无 Unity.exe 并删 UnityLockfile）：`TempLogs/accept_m2_01_editmode.xml` → `testcasecount="169" result="Passed" total="169" passed="169" failed="0" skipped="0"`，进程退出码 0 |
| 2 | 进度平滑纯函数断言真实 | PASS | `Tests/Debug/SceneSystemTests.cs`：线性段 `Remap_LinearBelowGate`(:23-32，斜率恒定断言)、缓动段 `Remap_EasedAboveGate`(:35-43，smoothstep 精确值 0.15625)、单调 `Remap_MonotonicNonRegressing_OverFullSweep`(:46-55，100 点全扫) + `EaseTail_Monotonic`(:75-84) + `NonRegressing_NeverMovesBackwards`(:87-91) |
| 3 | Payload consume / InputBlocker 用例 | PASS | consume 读后即焚：`SceneSystemTests.cs:111-123`（二次 consume 返 null+Count=0）；InputBlocker 嵌套配对：`:223-236`（两层 Acquire→逐层 Release）；超释放抛异常：`:250-257`（`OverRelease_Throws`，InvalidOperationException）；另有 Dispose token(:239-247) |
| 4 | Core 零感知 | PASS | `grep -rniE "SceneLoader|InputBlocker|ScenePayload|SceneProgressMath" Assets/ZFrame/Core/` → **0 命中**；挂载：`SceneLoader.cs:84` `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` + `:85-95` AutoBoot 自建 `~SceneLoaderRoot`（DebugPanelRoot 同模式，Core 不参与）；对外入口 `SceneLoaderFacade` |
| 5 | 卸载钩子顺序断言 | PASS | `SceneLoaderHookTests.cs:57-76` 断言"事件订阅者先于 UnloadAllUnused"（stub 在事件时刻采样 UnloadAllUnusedCalls==0，调用后==1）；ClearAll 位于事件与 UnloadAllUnused 之后仅由源码序保证（`SceneLoader.cs:150-160`：先 Invoke(:152)、再 UnloadAllUnused(:156)、后 PoolRegistry.ClearAll(:159)），无用例直接断言该第三步顺序——属次要缺口，不影响判定（事件先于资源清理这一关键时序已有真实断言） |
| 6 | ValidateFromCommandLine + SCN_Empty 位次 | PASS | 验收代理自跑 `-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine` → 退出码 0，`arch_report.txt` 违规数 0 PASS（asmdef 扫描 15）；`ProjectSettings/EditorBuildSettings.asset:8-13` 场景表 [SCN_Boot, SCN_Empty] 均 enabled，SCN_Empty 第二位，guid `6f613707...` 与 `SCN_Empty.unity.meta` 一致 |
| 7 | 文件规范 + 施工报告四节 | PASS | 抽查 14 文件（View/Scene 9 + View/Editor 1 + Tests 2 + SCN_Empty.unity + EditorBuildSettings.asset）：全部 UTF-8 无 BOM、纯 LF（od 逐字节核 CR=0）、尾换行、无尾随空格；git status 确认改动仅落在约束目录（另见并行 B-R5-01 的 Native 改动，非本任务）；报告四节齐全，§4 明确标注 PlayMode 冒烟范围（真异步全链路），§2 给出挂载方式说明 |

**遗留提示**（不阻塞 PASS）：① ClearAll 相对顺序建议后续补一条 EditMode 断言（可注册探针池后断言被清空）；② 真异步加载链路（started→progress→门控→loaded）仍待 PlayMode 冒烟，建议在 A-M2 收口或首个用场景的任务中用 SCN_Empty 执行一次。
