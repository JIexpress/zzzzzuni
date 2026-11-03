# TASK-A-M0-04 GameLoopDriver + Logger + GameRoot 启动流程

状态: doing（验收 PASS 待主控闭环） | 派发: 2026-10-23 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：ZFrame.Core 收口三件：统一驱动 GameLoopDriver（消灭 Update 满天飞）、分频道 Logger、组合根 GameRoot（服务注册与初始化顺序）。核心逻辑做成**可 EditMode 单测的纯 C# 类**，MonoBehaviour 只做薄适配。

**设计要点**：
1. `GameLoop/TickQueue.cs`（纯 C#）：`Register(ITickable, TickStage)`/Unregister；`TickAll(TickStage, float dt)` 按注册序遍历；`enum TickStage { Input, Logic, View, Late }`（int 顺序即执行序）；遍历中注册延后到本轮结束（防遍历中改集合）
2. `GameLoop/IGameTime.cs`：`float DeltaTime/unscaled/TimeScale/IsPaused`；实现 `GameTime` 纯类（时间由外部泵入 `Advance(dt)`）——逻辑层将来注入用，测试无需 Unity
3. `GameLoop/Scheduler.cs`（纯 C#，注入时间源 `Func<float> now`）：`Delay(sec, Action)` 返回取消句柄、`Repeat(sec, Action)`、`Tick(now)` 泵入；句柄 Cancel；清空 Clear（场景切换）
4. `GameLoop/GameLoopDriver.cs`（MonoBehaviour，唯一 Update 宿主）：Update 依次 `TickQueue.TickAll(Input→Logic)`（用 GameTime.DeltaTime）、帧末调 `EventBus.FlushQueued()`、LateUpdate 跑 Late 档；持有 GameTime+Scheduler 并 Tick；`IsPaused` 时 Logic 档跳过（View/UI 不冻结）——XML doc 说明设计意图
5. `Logging/ZLog.cs`：`ZLog.Info/Warn/Error(string channel, string msg)`；频道过滤（静态 HashSet 白名单+最低级别）；运行时全部走 `UnityEngine.Debug.Log*` 包装；Error 时 `EventBus.Fire(new LogErrorEvent{Channel,Message})`；`LogErrorEvent` 定义在同文件（struct）
6. `GameRoot.cs`（MonoBehaviour，组合根）：Awake 去重（重复实例销毁）+DontDestroyOnLoad；初始化顺序：ZLog → GameTime/Scheduler → TickQueue →（EventBus 静态无需注册）→ 向 `Services` 注册 `IGameTime`/`Scheduler` 等；`static GameRoot Instance` 懒加载兜底； OnDestroy 按逆序 `Services.Clear()`
7. 单测（ZFrame.Core.Tests 追加，纯类为主）：
   - TickQueue：跨档顺序（Input 先于 Logic）、遍历中注册安全、Unregister 后不再 Tick
   - Scheduler：Delay 到点触发/未到不触发、Repeat 周期、Cancel、时间注入正确
   - GameTime：Advance 累计、TimeScale 缩放、暂停
   - ZLog：频道过滤（未开频道不输出——可用返回 bool 的 `WouldLog` 或注入输出动作测试）、Error 事件触发（EventBus 订阅验证）
   - GameRoot：EditMode 下 `new GameObject()+AddComponent<GameRoot>()` 后 Services 可 Get<IGameTime>；重复创建第二实例被去重销毁（DestroyImmediate 场景注意）——若 Awake 依赖运行时导致 EditMode 不可测，改为可测的 `GameRoot.Bootstrap()` 公共静态方法（组件内 Awake 调用），单测直接调 Bootstrap，报告说明
8. 自测：EditMode 全绿（遵守 HANDOFF 的 Unity 批处理纪律）

**约束**：只动 `UnityProject/Assets/ZFrame/Core/`、`UnityProject/Assets/ZFrame/Tests/`、本任务日志；Unity 纪律（清锁/轮询/taskkill）；不 git commit；UTF-8 无 BOM/LF。

**交付物**：五件实现+单测全绿+施工报告四节。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理｜**日期**: 2026-10-24｜**结果**: EditMode 64/64 全绿

### 1. 交付物清单

实现（均在 `UnityProject/Assets/ZFrame/Core/`，命名空间 `ZFrame.Core`，.meta 均由 Unity 自动生成）：

| 文件 | 内容 |
|---|---|
| `GameLoop/ITickable.cs` | Tick 接口（消灭 Update 满天飞的订阅点） |
| `GameLoop/TickQueue.cs` | `enum TickStage { Input, Logic, View, Late }`（int 序即执行序）+ `TickQueue` 纯 C#：Register/Unregister/TickAll/Clear/Count；遍历中 Register 延后到本轮结束（`_pendingRegistrations`）；遍历中 Unregister 走 tombstone（快照数组 + `_removedDuringTick` 集合跳过），避免 List.Remove 移位导致漏 tick |
| `GameLoop/IGameTime.cs` | `DeltaTime/UnscaledDeltaTime/TimeScale/IsPaused/TotalUnscaled` |
| `GameLoop/GameTime.cs` | 纯 C#，时间外部泵入 `Advance(unscaledDt)`；暂停时 DeltaTime=0，TotalUnscaled 照常累计 |
| `GameLoop/Scheduler.cs` | 纯 C#，注入 `Func<float> now`；`Delay/Repeat → SchedulerHandle`（Cancel/IsActive）、`Tick(now)`、`Clear`；回调内再调度延迟到下一次 Tick；已触发的一次性 timer 用 tombstone + Tick 末 RemoveAll 压缩 |
| `GameLoop/GameLoopDriver.cs` | 唯一 Update 宿主（MonoBehaviour 薄适配）：`Init(time,queue,scheduler)` 后，Update 顺序 = Advance(Time.unscaledDeltaTime) → Input → （未暂停时）Logic + Scheduler.Tick → `EventBus.FlushQueued()` → View；LateUpdate → Late（用 UnscaledDeltaTime）。暂停只冻 Logic 档和 Scheduler，Input/View/Late 不冻结。XML doc 写明设计意图 |
| `Logging/ZLog.cs` | `LogLevel{Info,Warn,Error}` + `struct LogErrorEvent{Channel,Message}`（同文件）+ `static ZLog`：`Info/Warn/Error(channel,msg)`、频道 HashSet 白名单（Enable/DisableChannel）、`MinimumLevel`、`WouldLog`、可注入输出 sink（`SetSink`，默认包 UnityEngine.Debug.Log*）、`Error` 无条件 `EventBus.Fire(LogErrorEvent)`（**即使频道被过滤也发事件**，错误不静默）、`Reset()` |
| `GameRoot.cs` | MonoBehaviour 组合根：`Awake→Initialize()`（public seam，见下）做去重（重复实例销毁；EditMode 用 DestroyImmediate）+ `DontDestroyOnLoad`（仅 PlayMode，EditMode 调用会抛异常）+ `Bootstrap()` + 创建 GameLoopDriver 子物体并 Init；`static Bootstrap()` 幂等：ZLog.Reset → GameTime → Scheduler（接 GameTime.TotalUnscaled 时钟）→ TickQueue → Services 注册 `IGameTime/GameTime/Scheduler/TickQueue`；`static Shutdown()` 逆序清理（Scheduler.Clear → TickQueue.Clear → ZLog.Reset → Services.Clear）；`Instance` 静态 + `EnsureInstance()` 懒加载兜底；`OnDestroy` 仅根实例触发 Shutdown |

单测（`UnityProject/Assets/ZFrame/Tests/`，ZFrame.Core.Tests asmdef 引用不变）：
- `GameLoopTests.cs`：13 例 —— TickQueue 4 例（跨档顺序、遍历中注册延后、Unregister 停 tick、遍历中 Unregister 安全不漏他人）、GameTime 3 例（Advance 累计、TimeScale 只缩放 DeltaTime、暂停 DeltaTime=0 但实时钟继续）、Scheduler 6 例（Delay 到点触发/未到不触发/一次性不重复、Repeat 周期、Cancel、注入时间源生效、Clear、回调内调度延后）
- `ZLogTests.cs`：6 例 —— 开频道输出、未开频道过滤（WouldLog 断言）、MinimumLevel 过滤、DisableChannel 停输出、**Error 即使被频道过滤也触发 LogErrorEvent**（EventBus 订阅实证）、Info/Warn 不触发错误事件
- `GameRootTests.cs`：7 例 —— Bootstrap 注册四服务、幂等不重复注册、Scheduler 已接 GameTime 实时钟（3.5/4.0s 边界实证）、Shutdown 清空、Shutdown 后再 Bootstrap 换新实例、`Initialize()` 走完整 MonoBehaviour 路径（Instance/driver 子物体/services）、第二实例去重销毁（EditMode）

### 2. 自测证据

- 命令：`Unity.exe -batchmode -projectPath .../UnityProject -runTests -testPlatform EditMode -testResults <Temp xml> -logFile <Temp log>`（不加 `-quit`，同 A-M0-03 教训）
- **最终 results.xml 关键行**：`<test-run id="2" testcasecount="64" result="Passed" total="64" passed="64" failed="0" ...>`（A-M0-03 基线 38 例 + 本任务新增 26 例 = 64）
- 日志 `warning CS` 计数 0
- 遵守批处理纪律：每轮跑前确认无 Unity.exe + 删 UnityLockfile；xml 生成后按纪律 taskkill 残留 Unity 进程（20496/8424/14740）；临时 xml/log 已删除
- 迭代记录（3 轮失败→修复）：①首轮 CS0104（tests 中 `Object` 二义性 → 限定 `UnityEngine.Object`）②次轮 5 败：测试自身断言错 2 处 + TickQueue 遍历中 Remove 移位漏 tick（改快照+tombstone）+ EditMode 下 Awake 不执行（见下）③三轮 3 败：Registrar 测试辅助类重复注册、EditMode 下 DontDestroyOnLoad 抛异常（加 `Application.isPlaying` 守卫）→ 四轮全绿

### 3. Bootstrap 可测性说明

按简报第 6 条的备选方案落地：**`GameRoot.Bootstrap()` 为 public static 纯 C#**（幂等），`Shutdown()` 对称；组件 `Awake` 只是薄壳。EditMode 单测大部分直接调 Bootstrap/Shutdown，无需 GameObject。另发现 **EditMode 下普通 MonoBehaviour 的 Awake 根本不会被 AddComponent 触发**（PlayMode-only 生命周期），故把 Awake 主体提为 `public Initialize()`（Awake 调用它），测试显式调 `Initialize()` 覆盖去重/DontDestroyOnLoad 守卫/driver 接线的完整组件路径——这是除 Bootstrap 外的第二个可测 seam，已在 XML doc 注明。

### 4. 风险与备注

- `TickAll` 每帧对活跃 stage 做一次 `ToArray()` 快照（GC）：帧级 4 次小数组分配，M0 可接受；若后续 profiler 报 GC 压力，可换成双缓冲列表（列为 M0-05/性能任务的候选优化，不是缺陷）
- Scheduler 的 Repeat 若单帧跨越多个周期只补触发一次（NextFire += Interval）；当前无使用者依赖多 catch-up，先按最简语义实现
- ZLog 的 Error→EventBus 用立即 Fire（非 FireQueued），保证错误即时可观测；频道被过滤时事件仍发（测试已锁定该语义），若验收认为应一致过滤需改一行
- `ZFrame.Core.asmdef` references 仍为 `[]`，未动；纯类文件（TickQueue/Scheduler/GameTime/IGameTime/ITickable）源码无任何 `using UnityEngine`/代码引用（仅注释提及），MonoBehaviour 只出现在 GameLoopDriver/GameRoot
- GameRoot 注册了 `GameTime` 具体类与 `IGameTime` 双键（Driver 需要具体类型调 Advance/属性写）；后续若有第二实现再收窄
- 未 git commit（按纪律）；未触碰 Native/

## 验收清单（主控写）

1. 验收代理自跑 EditMode 全绿（用例数写入结论）
2. TickStage 顺序与遍历中注册安全有真实断言用例
3. Scheduler/GameTime 为纯 C#（源码无 UnityEngine 依赖，行号证据——MonoBehaviour 只在 Driver/GameRoot）
4. ZLog 频道过滤+Error→EventBus 事件有实证用例
5. GameRoot 初始化顺序可测（Bootstrap 模式或等价），Services 注册验证用例存在；重复实例去重验证
6. 文件规范抽查；ZFrame.Core.asmdef 引用仍为空
7. 施工报告完整

## 验收结论（验收代理写）

**验收代理**: general-purpose 验收子代理｜**日期**: 2026-10-24｜**结论**: **PASS**（7/7 全过）

1. **PASS** — 自跑 EditMode：results.xml `testcasecount="64" result="Passed" total="64" passed="64" failed="0"`（A-M0-03 基线 38 + 新增 26 = 64）；log `warning CS` 计数 0；遵守纪律（跑前无 Unity 进程+删 UnityLockfile，xml 20 秒生成后按纪律 taskkill 残留进程 14384，临时 xml/log 已删）
2. **PASS** — TickStage 顺序真实断言：`GameLoopTests.cs:30` `TickQueue_InputStageTicksBeforeLogic_WhenDrivenInOrder`（期望 `["input","input2","logic"]` 严格顺序）；遍历中注册安全：`:49` `RegistrationDuringTick_IsDeferredToNextRound` + `:109` `UnregisterDuringTick_IsSafe`（自移除后他人仍 tick）
3. **PASS** — 纯度：`TickQueue.cs/Scheduler.cs/GameTime.cs/IGameTime.cs/ITickable.cs` grep `UnityEngine|MonoBehaviour` 仅命中注释行（GameTime.cs:6、IGameTime.cs:5、ITickable.cs:7 均为 doc 注释），无 `using UnityEngine`、无代码引用；`MonoBehaviour` 子类仅 `GameRoot.cs:18`、`GameLoopDriver.cs:22`（另有 A-M0-03 既存 `ObjectPool/PoolResetter.cs:11`，非本任务产物）
4. **PASS** — ZLog：频道过滤 `ZLogTests.cs:44`（未开频道 0 输出+WouldLog 断言）、`:55` MinimumLevel、`:70` DisableChannel；Error→事件实证 `:81` `Error_FiresLogErrorEvent_OnEventBus_EvenWhenFiltered`（被过滤频道 sink 0 条但 EventBus 收到 1 次 LogErrorEvent，与实现"错误不静默"语义一致，已锁定）、`:104` Info/Warn 不触发
5. **PASS** — GameRoot 可测模式：`public static Bootstrap()/Shutdown()` + `public Initialize()` 双 seam；Services 注册 `GameRootTests.cs:44`（IGameTime/Scheduler/TickQueue 三键解析）、幂等 `:55`、Shutdown 清空 `:81`；重复实例去重 `:120` `SecondRootInstance_IsDeduplicated`（第二实例销毁、Instance 保持首个）
6. **PASS** — 文件规范抽查 9 个新文件（TickQueue/Scheduler/GameTime/GameLoopDriver/GameRoot/ZLog + 3 测试）：UTF-8 无 BOM（首字节非 EF BB BF）、CR 行数 0（LF）；`ZFrame.Core.asmdef` `"references": []` 未动
7. **PASS** — 施工报告四节齐全（交付清单/自测证据/Bootstrap 可测性说明/风险备注），迭代 3 败→修记录完整

备注（非缺陷）：TickAll 每帧 ToArray 的 GC 与 Repeat 单帧多周期只补一次，施工报告已声明，M0 可接受。
