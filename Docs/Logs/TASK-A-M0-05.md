# TASK-A-M0-05 调试面板 v1 + 架构校验器（M0 收口）

状态: 待commit | 派发: 2026-10-23 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：A 轨 M0 收口两件：①运行时调试面板 v1（IMGUI/OnGUI 实现——UI 框架 M3 才有，先可用后好看）②编辑器架构校验器（asmdef 矩阵 + 逻辑层纯度扫描，可批处理跑，防分层腐化的强制手段）。

**设计要点**：
1. `ZFrame/View/DebugPanel/`（注意：调试面板放 ZFrame.View 程序集，运行时组件）：
   - `FpsCounter.cs`（纯 C#，可测）：滑动窗口帧耗时→FPS/ms/最差帧；`Tick()` 泵入耗时
   - `ZLogRingBuffer.cs`（纯 C#，可测）：环形队列（容量 256）记录 {时间,级别,频道,消息}；ZLog 增加静态挂载点 `static event Action<LogLevel,string,string> OnLog`（ZLog.cs 在 Core，小心不引入 View 依赖——挂载点放 ZLog，RingBuffer 在 View 订阅）
   - `DebugPanelRoot.cs`（MonoBehaviour，OnGUI）：F1 切换（`UnityEngine.Input.GetKeyDown`）；面板内容：FPS/ms/最差帧、日志页（最近日志+按级别过滤+清空）、服务清单（Services.GetAllRegistered）、池统计（PoolRegistry 各池 Active/Pooled/Instantiate 次数——PoolRegistry 若无统计聚合 API 可加 `GetAllStats()`）；挂到 GameRoot（GameRoot.Awake 时 `gameObject.AddComponent<DebugPanelRoot>()`？不——GameRoot 在 Core 不能引用 View！改为：DebugPanelRoot 自懒加载（[RuntimeInitializeOnLoadMethod] 或首帧自动创建 GameObject），**不得反向依赖**，报告说明挂载方式）
   - 面板仅在 Development Build/Editor 启用（`#if DEVELOPMENT_BUILD || UNITY_EDITOR` 包裹）
2. `ZFrame/Editor/ArchitectureValidator.cs`（editor 程序集 ZFrame.Editor）：
   - **asmdef 矩阵校验**：内置期望矩阵（与方案 §3.2 一致，写为数据表）；扫描 Assets/ 所有 .asmdef，引用超集=违规，列出（路径+实际引用+违规项）
   - **逻辑层纯度扫描**：扫描 `Assets/ZFrame/Gameplay/`、`Assets/Game/Domain/` 下 .cs 源码，禁用 token 白名单制：`GameObject|MonoBehaviour|Component|Camera|Transform|FindObjectOfType|Object.Find|UnityEngine.SceneManagement`（`Vector2/3/4|Quaternion|Mathf|Color` 等数学类型放行——按 using 与 token 精确匹配，注释行忽略）；违规列出 文件:行号:token
   - 输出：Editor 窗口（菜单 ZFrame/架构校验）显示报告；`public static bool ValidateAll()` 供测试；批处理模式：`-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine` 把报告写到 `TempLogs/arch_report.txt`，有违规时 `EditorApplication.Exit(1)`（CI 可用）
3. 单测（ZFrame.Core.Tests 或新建 View 测试程序集——按放哪边引用最少决定，报告说明）：
   - FpsCounter：注入耗时序列→FPS/最差帧正确
   - RingBuffer：容量滚动覆盖、级别过滤
   - ZLog.OnLog 挂载点触发
   - 校验器：`ValidateAll()` 当前工程==0 违规（自证清白）；asmdef 矩阵表与实际文件数一致
4. 自测：EditMode 全绿（Unity 纪律）+ 批处理 `-executeMethod ...ValidateFromCommandLine` 跑一遍 exit 0 + 报告文件存在

**约束**：可动 `UnityProject/Assets/ZFrame/`（Core 的 ZLog.cs 挂载点小改、View、Editor）、`UnityProject/Assets/ZFrame/Tests/`、本任务日志；**不得让 Core 引用 View/Editor**（校验器会查）；Unity 纪律；不 git commit；UTF-8 无 BOM/LF。

**交付物**：调试面板三件+校验器+单测+批处理验证+施工报告四节。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理｜**时间**: 2026-10-23/24｜**自测结果**: EditMode 75/75 全绿；批处理 ValidateFromCommandLine exit 0 + 报告 PASS

### 一、交付清单（新增/修改文件）

新增：
- `UnityProject/Assets/ZFrame/View/DebugPanel/FpsCounter.cs` —— 滑动窗口帧统计（纯 C#，零 UnityEngine 引用）：`Tick()` 泵入耗时，`Fps/AverageMs/WorstMs/SampleCount`
- `UnityProject/Assets/ZFrame/View/DebugPanel/ZLogRingBuffer.cs` —— 环形日志缓冲（纯 C#，容量注入，默认 256）：`ZLogEntry{Time,Level,Channel,Message}`；时间戳由可注入 `TimestampProvider` 提供（保持纯 C# 可测）；`Enqueue(level,channel,message)` 签名与 `ZLog.OnLog` 直接兼容
- `UnityProject/Assets/ZFrame/View/DebugPanel/DebugPanelRoot.cs` —— MonoBehaviour + OnGUI，整文件 `#if DEVELOPMENT_BUILD || UNITY_EDITOR` 包裹；F1 切换；四个页签：概览(FPS/平均帧/最差帧/缓冲占用)、日志(级别过滤 Info/Warn/Error+清空)、服务(Services.GetAllRegistered)、池(PoolRegistry.GetAllStats)
- `UnityProject/Assets/ZFrame/Editor/ArchitectureValidator.cs` —— 校验器（窗口 ZFrame/架构校验 + `ValidateAll()` + `ValidateFromCommandLine`）
- `UnityProject/Assets/ZFrame/Tests/Debug/`（新子目录）—— `ZFrame.Debug.Tests.asmdef` + `DebugPanelTests.cs`（FpsCounter 4 用例/RingBuffer 3 用例/ZLog.OnLog 2 用例）+ `ArchitectureValidatorTests.cs`（2 用例）

修改（Core，最小化）：
- `Core/Logging/ZLog.cs`：加 `public static event Action<LogLevel,string,string> OnLog`（L59），`Write` 中**过滤前**触发（L124）——面板可捕获被频道过滤掉的消息；Core 无任何 View 引用
- `Core/ObjectPool/ComponentPool.cs`：加 `internal interface IPoolStatsSource {PoolName,Stats()}`，`ComponentPool<T>` 实现之（非泛型桥接用）
- `Core/ObjectPool/PoolRegistry.cs`：加 `GetAllStats()` 聚合各池 {PoolName, PoolStats(Active/Pooled/Peak/Instantiate)}

### 二、设计决策说明（对应简报要点）

1. **挂载方式（无反向依赖）**：`DebugPanelRoot` 用 `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` 自建隐藏 `~DebugPanelRoot` GameObject + DontDestroyOnLoad（L38-45）；GameRoot/Core 完全不感知。依赖方向只有 View→Core（ZFrame.View asmdef 引用 Core，符合 §3.2 矩阵）
2. **OnLog 订阅链**：事件定义在 Core 的 ZLog（L59）→ View 的 DebugPanelRoot.Awake 订阅 `ZLog.OnLog += logs.Enqueue`（L55）/OnDestroy 退订（L60）
3. **测试程序集选址**：新建单一 `ZFrame.Debug.Tests`（Tests/Debug/ 子目录——Unity 同目录多 asmdef 会报错，首跑踩坑后移入子目录），引用 Core+View+Editor 三个——比建两个测试程序集文件更少；`*.Tests*` 名字在矩阵校验中豁免
4. **asmdef 期望矩阵**：12 条目削减为 10 条目实际存在的（ZFrame.Render.Editor/Game.Editor 落地时需补条目，代码内有 NOTE 提示，单测 `ExpectedMatrix_MatchesActualAsmdefCount` 强制一致性）；GUID: 前缀引用豁免；`*.Editor` 程序集额外校验 includePlatforms 含 Editor
5. **纯度扫描**：词边界正则黑名单（GameObject/MonoBehaviour/Component/Camera/Transform/FindObjectOfType/Object.Find/UnityEngine.SceneManagement）；数学类型不在黑名单=放行；剥除 `//` 与跨行 `/* */` 注释后扫描；报告 文件:行号:token

### 三、自测证据

- EditMode：`TempLogs/EditMode-results2.xml` —— **total=75 passed=75 failed=0**（含既有 Core/Render 用例 + 本次 11 个新用例）
- 批处理：`-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine` → **exit 0**；报告 `TempLogs/arch_report.txt`（仓库根）：违规数 0，结果 PASS，asmdef 扫描数 13（10 非测试 + 3 Tests 豁免）
- Unity 纪律遵守记录：run3 进程写出结果后挂起 → 确认 xml 已生成 → taskkill //F → 清 UnityLockfile 重跑；run4 因锁冲突 "already open" 失败（exit 21）→ 杀残留进程后 run5 成功
- 文件规范：全部 UTF-8 无 BOM、LF（`file`+`grep -r $'\r'` 验证）；.meta 交由 Unity 自动生成未手改

### 四、遗留/提示

- `ZFrame.Render.Editor`、`Game.Editor` 程序集创建时必须同步 ArchitectureValidator 期望矩阵（单测会拦截）
- 面板 UI 为 IMGUI 临时实现（M3 UI 框架落地后替换），中文标签在 Development Build 需项目字体支持（Editor 下正常）
- 未 git commit（按纪律留给主控）

## 验收清单（主控写）

1. 自跑 EditMode 全绿（用例数写入结论）
2. 验收代理独立跑批处理 ValidateFromCommandLine：exit 0 + 报告文件生成（再故意无破坏性验证：改报告内容核对格式即可，不要求造违规）
3. 纯度扫描白名单合理性核对（数学类型放行、场景 API 拦截）
4. Core→View 反向依赖不存在（ZLog.OnLog 在 Core、订阅在 View，行号证据）；DebugPanelRoot 挂载方式无反向依赖
5. FpsCounter/RingBuffer 纯 C# 可测（无 UnityEngine）
6. 面板功能源码核对：F1 开关、日志过滤、服务清单、池统计四块齐全；仅 Editor/Development 生效的编译宏
7. 文件规范+施工报告完整

## 验收结论（验收代理写）

**验收**: general-purpose 验收子代理｜**时间**: 2026-10-24｜**结论**: **PASS**（7/7 通过）

1. **PASS** — 独立跑 EditMode（清锁+独立 Unity 实例）：`TempLogs/EditMode-verify.xml` result=Passed，**total=75 passed=75 failed=0**（含本次 11 个新用例：FpsCounter 4 + RingBuffer 3 + ZLog.OnLog 2 + 校验器 2），与施工报告一致
2. **PASS** — 独立批处理 `-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine`：**exit 0**；`TempLogs/arch_report.txt` 生成，内容：asmdef 扫描数 13（期望矩阵 10 + 3 Tests 豁免）、违规数 0、结果 PASS（验收后已删除该临时报告文件，无残留 Unity 进程）
3. **PASS** — ArchitectureValidator.cs L83-93 黑名单正则：GameObject/MonoBehaviour/Component/Camera/Transform/FindObjectOfType/Object.Find/UnityEngine.SceneManagement 全部词边界拦截（L92 场景 API 在列）；L81-82 注释明确数学类型（Vector2/3/4、Quaternion、Mathf、Color）不在黑名单=放行；L210-243 剥除 `//` 与跨行 `/* */` 注释后扫描，注释不误报
4. **PASS** — 反向依赖不存在：`OnLog` 事件定义在 Core `ZLog.cs` L59、L124 过滤前触发；订阅在 View `DebugPanelRoot.cs` L55（Awake 订阅）/L60（OnDestroy 退订）；挂载机制 L38-48 `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` 自建 `~DebugPanelRoot` GameObject，GameRoot/Core 零感知；ZFrame.View asmdef 引用 Core 符合矩阵（校验器批处理 0 违规自证）
5. **PASS** — FpsCounter.cs 仅 `using System;`（L1），ZLogRingBuffer.cs 仅 `using System/System.Collections.Generic/ZFrame.Core`（L1-3）；两文件零 UnityEngine 代码引用（仅 doc 注释提及 "no UnityEngine" 字样），纯 C# 可测
6. **PASS** — DebugPanelRoot.cs：L69 F1 切换；L103-109 概览（FPS/平均帧/最差帧/缓冲占用）；L111-146 日志页（Info/Warn/Error 级别过滤 L115-128 + 清空 L130-133）；L148-164 服务清单（Services.GetAllRegistered L152）；L166-184 池统计（PoolRegistry.GetAllStats L170，Active/Pooled/Peak/Instantiate 四项）；整文件 `#if DEVELOPMENT_BUILD || UNITY_EDITOR`（L1/L187）包裹
7. **PASS** — 6 个交付文件 UTF-8 无 BOM（首 3 字节非 EF BB BF）、LF（CR 计数 0）；施工报告四节齐全（简报/报告/清单/结论），交付清单与磁盘文件一致

临时文件已清理：EditMode-verify.xml/.log、arch-verify.log、arch_report.txt、UnityLockfile 均删除；未 git commit（按纪律留主控）。
