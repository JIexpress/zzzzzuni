# TASK-H-01 —— A 轨 C# 健壮性修复（收口后独立审查发现）

> 施工代理按本简报实现并自测，写「施工报告」到本文档第二节。**禁止 git commit**。
> 验收由另一个代理独立执行（第三节清单）。

## 任务简报（主控撰写）

### 背景

项目已于 FINAL-01 阶段收口（40 任务闭环，原生 161 用例/704,980 断言 + EditMode 542 + PlayMode 5 全绿）。
阶段收口后的独立审查（2026-10-26）发现一批**测试绿灯照不到**的健壮性缺陷——多为异常路径、
失败回滚与资源生命周期。本任务修其中 A 轨（C#）部分。

### 缺陷清单（每条都已在代码中定位，勿扩大范围）

**D1 — 重绑定保存失败导致设置项永久卡死（严重：UI 软锁）**

- 位置：`UnityProject/Assets/ZFrame/View/Input/RebindService.cs:143`（`Save()` 调用）、`:216`（`Start()`）
- 现状：`CompleteCaptured` 调 `Save()` 无异常保护；`Rebind` 启动流程无 try。落盘抛 `IOException`
  （磁盘满/目录只读）时 `OnComplete` 永不触发，`Settings/SettingsRowFactory.cs:421-429` 的行
  卡在「按任意键…」且 `interactable=false`；`Start()` 抛异常则 `_operation` 残留，`RebindService.cs:189`
  此后永久拒绝新重绑定（`_operation != null` 早退）。
- 期望：保存失败必须走**单一出口** `Complete(...)` 报告 `RebindOutcome.Failed`（不吞异常、不卡死）；
  `Start()` 失败时清空 `_operation`/`_pending` 并回调 Failed。日志用 `ZLog.Error("Input", ...)`。
- 验收要能证明：构造一个会抛 IOException 的保存路径（如注入只读目录），断言
  `onComplete` 收到 `Failed` 且 `IsRebinding == false`，且随后还能发起新重绑定。

**D2 — settings.json 非原子写（数据丢失）**

- 位置：`UnityProject/Assets/ZFrame/View/Settings/SettingsModel.cs:138-165`
- 现状：直接 `File.WriteAllText`。写入中断（掉电/异常）会把文件截断，`Load` 容错后**静默**回默认，
  玩家设置无声丢失。同仓库的 `SaveSystem` 已用 tmp+rename，此处不一致。
- 期望：改为 tmp+rename 原子写，并 `Flush(flushToDisk: true)` 落盘（参照
  `ZFrame/Data/Save/SaveSystem.cs` 的 `WriteDurable` 写法，可提取共用或就近实现，勿新增跨层依赖）。
  注意 settings 与 save 分属不同程序集，**不要**把 SaveSystem 的内部方法直接 public 出去。

**D3 — UI 面板栈：淡入期可二次 Push + 异常路径泄漏引用**

- 位置：`UnityProject/Assets/ZFrame/View/UI/UIManager.cs:161-164`、`:127-148`
- 现状：`await FadeAsync` 发生在 `_stack.Add` 之前，淡入尚未完成时同一面板可被第二次 Push
  （92 行查重只看 `_stack`）；`Push` 在 `_stack.Add` 之前抛异常时 `_resources.Release` 不执行，
  引用计数泄漏。
- 期望：查重覆盖「正在淡入」的中间态（加 in-flight 集合或以占位先行入栈）；Push 失败路径
  `try/finally` 保证已获取的资源被 Release。不要改变现有面板栈的对外语义（栈序/事件）。

**D4 — HUD 文本随场景泄漏（内存泄漏）**

- 位置：`UnityProject/Assets/Game/View/UI/GameHud.cs:49-50`、`:71`（`OnDestroy`），
  `Game/Bootstrap/GameBootstrap.cs:337-339`
- 现状：HUD 文本 parent 到 `DontDestroyOnLoad` 的 UIRoot canvas（`UIRoot.cs:84`），宿主
  GameObject 随场景销毁；`OnDestroy` 只解订阅不销毁文本 → 每次进 `SCN_Game` 泄漏 2 个 Text，
  旧文案残留在新场景。
- 期望：`OnDestroy` 销毁自己创建的文本对象（先 `SetParent(null)` 或直接 `Destroy`），
  保证进出场景 N 次后 UIRoot 下不残留 HUD 文本。

### 约束

- 只改上述 4 处及其直接依赖，**不做无关重构**、不动 asmdef 依赖方向、不动 `.meta`。
- 每个修复必须配测试（EditMode 优先）。测试要能在**修复前失败、修复后通过**——空洞断言
  （`Assert.DoesNotThrow` 后不校验状态）一律不接受。
- 参考已修复的同类写法：`SaveSystem.cs`（本轮已加锁 + `WriteDurable` + 同调用降级）。
- 文件规范：UTF-8 无 BOM、LF 换行。

### 环境纪律（**必读，否则会浪费大量时间**）

1. **Unity 批处理在本机当前环境不可用**：`-batchmode` 会因 `[Package Manager] Could not connect to
   IPC stream "Upm-*" after 30.0 seconds` 退出码 1；加 `-noUpm` 可绕过 UPM，但 Bee 构建后端随即
   报 `'cache-folder' could not be inferred from environment`（试过 `UNITY_CACHE_FOLDER` /
   `STEVEDORE_CACHE_FOLDER` 均无效）。**因此 C# 侧无法编译验证**——这是环境限制，不是你的失败。
   请以「逐行静态审查 + API 签名核对 + 括号配平校验」为准，并在施工报告中**明确标注未编译**。
2. **编译器/构建器会被重复代理变量搞崩**：环境里同时存在 `HTTP_PROXY` 与 `http_proxy`
   （还有 `HTTPS_PROXY`/`https_proxy`），MSBuild 的 `ProcessStartInfo.EnvironmentVariables`
   用大小写敏感字典，会抛「已添加项。字典中的关键字:"HTTP_PROXY"…」。**跑任何构建前先**：
   `unset http_proxy https_proxy all_proxy`。
3. **PowerShell 工具无法执行外部 exe**（报"无法在管道中间运行文档"），`Start-Process` 也会被
   安全钩子拦截。**用 Bash 工具调用 cmake/可执行文件**。
4. **cmake 不在 PATH**，用：
   `"/c/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"`
   （构建：`"$CM" --build build --config Release --parallel`）。
5. **safe-delete 钩子会拦 `Remove-Item`/批量删除**：需要清理旧证据文件时用 `mv` 归档到
   `TempLogs/archive_pre_audit/`，不要删。
6. 本任务只改 C#，**不需要**跑原生构建。

### 交付物

1. 4 处修复 + 对应测试
2. 本文档第二节「施工报告」：改了什么（文件:行号）、为什么、如何自测、**哪些未能验证及原因**
3. 不要改 TASKS.md / HANDOFF.md / WORKLOG.md（主控负责）

---

## 施工报告（施工代理填写）

### 0. 验证条件（先说清楚"测过什么、没测过什么"）

- **C# 未编译、测试未运行**。原因：本项目 Unity 批处理不可用（`-batchmode` 卡 UPM IPC 30s 退出码 1；`-noUpm` 后 Bee 报 `'cache-folder' could not be inferred`），本机也没有可用的独立 C# 编译器（`csc` 调用被安全钩子拦截）。**本报告不含任何"已编译通过/测试已绿"的声称**。
- 实际采用的验证手段：
  1. 逐行静态审查（每个改动区块重新通读、确认类型/可见性/`using` 齐全）；
  2. 用 Grep 在仓库内核对所有新调用的 API 真实存在（签名逐个对照）：`ZLog.Error(string,string)`、`UIPresenter.Detach()`、`UIRoot.GetLayerCanvas`、`InputActionAsset.AddActionMap`、`InputActionMap.AddAction`、`InputActionType.Button`、`RebindOperation.Start()` 抛点、`File.Replace`/`FileStream.Flush(bool)`；
  3. 括号配平 + 编码校验脚本（沿用 `TempLogs/brace_check.py` 的算法，文件列表换成本次 8 个改动文件）：8 个文件 `depth=0`、结尾状态 `code`，全部 **UTF-8 无 BOM / LF**；
  4. 新增 `.meta` 与既有同类文件结构逐字节比对（仅 GUID 不同）、GUID 全仓唯一（重复 0）；新 asmdef JSON 可解析。
- 新增测试均为 EditMode（唯一新增 asmdef `Game.View.Tests` 为 Editor-only），断言形态是**状态断言**，不是 `DoesNotThrow` 空壳。

### 1. 改动文件清单

| 文件 | 性质 |
|---|---|
| `UnityProject/Assets/ZFrame/View/Input/RebindService.cs` | D1 修复 |
| `UnityProject/Assets/ZFrame/Tests/Input/RebindServiceTests.cs` | D1 测试（+2 用例、+2 辅助方法；该文件本轮已有其他代理的未提交改动，本次只追加） |
| `UnityProject/Assets/ZFrame/View/Settings/SettingsModel.cs` | D2 修复 |
| `UnityProject/Assets/ZFrame/Tests/Debug/SettingsModelTests.cs` | D2 测试（+3 用例） |
| `UnityProject/Assets/ZFrame/View/UI/UIManager.cs` | D3 修复 |
| `UnityProject/Assets/ZFrame/Tests/Debug/UIPushRobustnessTests.cs` **（新增）** | D3 测试（+2 用例 + 1 个 spy 资源服务） |
| `UnityProject/Assets/Game/View/UI/GameHud.cs` | D4 修复 |
| `UnityProject/Assets/Game/Tests/View/GameHudLeakTests.cs` **（新增）** | D4 测试（+2 用例） |
| `UnityProject/Assets/Game/Tests/View/Game.View.Tests.asmdef` **（新增）** | D4 测试程序集（Editor-only，`*.Tests` 命名 → 架构校验矩阵豁免） |
| `*.meta`（4 个新增） | 新增文件的 `.meta`（新 GUID，未改任何既有 `.meta`） |

未触碰：`TASKS.md` / `HANDOFF.md` / `WORKLOG.md` / 任何既有 asmdef / 任何既有 `.meta` / `SaveSystem.cs`。未做 git commit。

### 2. D1 — 重绑定保存失败软锁（`RebindService.cs`）

**改法**

1. `RebindService.cs:408-422`（`CompleteCaptured`）：把裸调 `Save()` 包进 `try/catch`；保存抛异常时 `ZLog.Error("Input", …)` + `Complete(RebindOutcome.Failed, null)` 后 `return`，即**唯一出口**仍是 `Complete`（先 `Dispose` 操作、清空 `_operation`/`_pending`，再回调），回调必然触发一次。
2. `RebindService.cs:213` + 新增 `StartCapture`（`:224-248`）：启动流程（`PerformInteractiveRebinding` 链 + `Start()`）移进私有方法并加 `try/catch`，失败时同样 `ZLog.Error("Input", …)` + `Complete(Failed, null)` —— `Complete` 会 dispose 半成品操作并清空 `_operation`/`_pending`，因此 `:192` 的 `_operation != null` 早退不会永久成立。
3. 类注释 `:31-37` 补了"保存失败也必回调 Failed"的契约说明。

**为什么这样改**

- 原缺陷是"回调永不触发"→ 行卡在「按任意键…」且 `interactable=false`，且 `_operation` 残留会永久拒绝新重绑定。二者都是"状态机没有单一出口"造成的，所以统一收敛到既有 `Complete(...)`（它本来就是为"回调允许重入发起新 rebind"设计的：先 dispose 再回调）。
- **保存失败不回滚内存中的 override**：`RemoveBindingOverride` 会把这个 binding 直接退回 asset 默认键，若用户之前已经自定义过该键（例如 F），保存失败反而会**毁掉**一个本来可用的自定义键，且磁盘上仍是 F —— 内存/磁盘照样不一致。因此选择"保留本次会话新键 + 明确报 Failed + 磁盘旧文件原样保留"，语义是"持久化失败，改动仅本会话有效"，不静默（有 `ZLog.Error` + 回调 Failed + 面板 onFeedback）。这一取舍已写进代码注释，供验收判断。
- `catch` 用的是宽 `catch (Exception)` 而不是 `when (IOException…)` 过滤器：这里的硬约束是"回调必须恰好触发一次，UI 绝不能被留在中间态"，任何原因（磁盘、权限、锁、Input System 自身）都必须走同一条出口。

**自测举证**（`RebindServiceTests.cs`）

- `CompleteCaptured_WhenPersistFails_ReportsFailed_AndUnlocksTheRow`（`:328-380`）：先用 `Save()` 落一份真实文件，然后 `new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)` 独占锁住文件 → `Save()` 必抛 `IOException`（等价磁盘满/只读目录）。断言：① 不抛异常到调用方；② 回调收到 `Failed` 且 snapshot 为 null；③ `IsRebinding == false`；④ 反射读 `_pending` 为 null（残留回调不会二次触发）；⑤ 磁盘旧文件仍可 `Load()` 且仍是旧键 `<Keyboard>/f`（失败没截断旧文件）；⑥ 事后 `Rebind(...)` 仍能进入并被应答（不再被残留操作短路）。
- `StartCapture_WhenTheCaptureCannotStart_ReportsFailed_AndLeavesNoResidue`（`:382-424`）：构造一个 0 binding 的 `InputActionAsset`（`InputSystem` 在 `PerformInteractiveRebinding` 里必抛 `ArgumentOutOfRangeException`），反射直接调 `StartCapture` 走真实 catch 分支。断言：不逃逸、回调恰好 1 次且为 `Failed`、`_operation`/`_pending` 均为 null、`IsRebinding == false`。
- 修复前失败点：`InvokeCompleteCaptured` 会把 `IOException` 抛回测试（`DoesNotThrow` 失败）、`outcome` 永远为 null；新用例还断言 `StartCapture` 方法存在（修复前无此方法，直接失败）。

### 3. D2 — settings.json 非原子写（`SettingsModel.cs`）

**改法**

- `SettingsModel.cs:169`：`Save()` 末尾由 `File.WriteAllText(FilePath, …)` 改为 `WriteAtomically(FilePath, json)`。
- `:36` 新增私有常量 `TempSuffix = ".tmp"`；`:185-200` 新增 `WriteAtomically`：先 `WriteDurable(tmp, content)`，再 `File.Replace(tmp, path, null)`（目标已存在，一步替换）；首次保存目标不存在时 `File.Move(tmp, path)`。`:203-212` 新增 `WriteDurable`：`FileStream(FileMode.Create, FileAccess.Write, FileShare.None)` 写入 + `Flush(flushToDisk: true)`，UTF-8 无 BOM（与 `SaveSystem.WriteDurable` 同写法）。
- `Save` 的 XML 注释同步更新（`:136-141`）。

**为什么这样改**

- 直接 `File.WriteAllText` 会先截断主文件再写：掉电/异常 → 主文件半截 → `Load` 容错后静默回默认，玩家设置无声丢失。tmp+rename 让"主文件要么是旧的完整版、要么是新的完整版"，`Flush(flushToDisk:true)` 则防"rename 成功但内容是空的"。
- **就近实现而不复用 `SaveSystem`**：settings 在 `ZFrame.View`、save 在 `ZFrame.Data`，把 `SaveSystem` 的内部 `WriteDurable` 提升为 public 会污染存档层 API、并让设置层依赖存档实现细节；简报明确要求不要这样做。两份 10 行实现比一条跨层公开 API 更划算。
- 未加 `.bak` 代际备份：settings 是单文件、可由默认值重建，简报只要求 tmp+rename+flush；保持最小改动。

**自测举证**（`SettingsModelTests.cs`）

- `Save_WritesThroughATempFile_AndLeavesNoResidue`（`:297-311`）：保存后目录里**只有 1 个文件**且以 `settings.json` 结尾（tmp 是 rename 掉的，不是拷贝），并且能被新 `SettingsModel` 完整 `Load`（3 条值）。
- `Save_WhenTheWriteFails_KeepsThePreviousMainFileIntact`（`:313-351`）：先落一版（slider=0.25），然后在 `settings.json.tmp` 路径上**建一个同名目录**（便携的"临时文件写不进去"注入手段），再改值保存。断言：① 保存确实抛 `IOException`/`UnauthorizedAccessException`（不静默吞掉）；② `IsDirty` 仍为 true（下次还会重试）；③ 主文件仍能 `Load` 出旧值 0.25（**修复前主文件会被新值覆盖 → 该断言必红**）。
- `Save_Twice_KeepsASingleCompleteFile`（`:353-367`）：连续两次保存后仍只有 1 个文件、读到的是第二次的值（验证 `File.Replace` 覆盖生效、不产生副本堆积）。

### 4. D3 — 面板栈二次 Push 与异常泄漏（`UIManager.cs`）

**改法**

1. `UIManager.cs:56-59` 新增两个 in-flight 集合：`_pushingViews`（`HashSet<Type>`）、`_pushingKeys`（`HashSet<string>`）。
2. `:110-121` 查重同时看 `_pushingViews` / `_pushingKeys` / `_stack`；`:126-130` 在**第一个 await 之前**登记 in-flight；`:206-211` 在 `finally` 里撤销登记（成功/失败都撤销，失败也不会把面板类型永久锁死）。
3. `:132-238` 把"取实例 → 挂父节点 → OnInit → Presenter.Attach → OnShow → 淡入 → `_stack.Add`"整体放进 `try`，`committed` 标记是否已入栈；`finally` 里 `!committed` 时按"本次实际获取了什么"回滚：`presenter?.Detach()`（吞清理异常，避免掩盖原始失败）→ 若实例是本次 `Instantiate` 的则 `DestroyObject` → 若本次 `LoadAsync` 过则 `_resources.Release(prefabKey)`。
4. 栈序、`ApplyPauseStates`、`PanelOpenedEvent` 的时机与内容完全不变（仍在 `finally` 之后执行）。

**为什么这样改**

- 淡入发生在 `_stack.Add` 之前，查重只看 `_stack` 就存在"打开中"的空窗；用 in-flight 集合覆盖整个窗口（prefab 加载 + 淡入），比"占位先行入栈"更不扰动栈序/暂停计算/事件顺序。
- 资源泄漏的根因是"获取（`LoadAsync` 引用计数 +1）在 `_stack.Add` 之前，释放只在 `PopAt`/`EvictPanelCache` 里"。失败路径必须自己把这一份引用还回去，否则每次失败都永久 +1；同时半成品实例会留在 layer canvas 上成为孤儿。用 `committed` 标记一次 `try/finally` 覆盖所有失败点（缺组件、`OnInit`/`OnShow` 抛、淡入被取消），比逐点 `catch` 可靠。

**自测举证**（`UIPushRobustnessTests.cs`）

- `Push_WhileTheSamePanelIsStillOpening_RejectsTheSecondPush`（`:41-75`）：EditMode 淡入是瞬时的（`UIAnimRunner` 明确说明），所以用**重入**复现同一空窗——spy 资源服务在 `LoadAsync` 内部回调里再 `Push` 同一个面板（此时第一个 Push 已登记 in-flight 但还没入栈）。断言：第二次返回 `false`、`StackCount == 1`、`LoadCount == 1`（被拒的那次**连 prefab 都不加载**）、该 key 引用计数为 1。修复前：第二次会加载 → 入栈，`nestedResults[0] == true`、`StackCount == 2`、引用计数 2 → 必红。
- `Push_WhenThePrefabHasNoViewComponent_ReleasesTheAcquiredReference`（`:77-118`）：注册一个根节点没有 `TestPanelView` 的 prefab → `Push` 抛 `InvalidOperationException`。断言：`LoadCount == 1` 且 `ReleasedKeys` 含该 key、`GetRefCount == 0`（引用计数回到基线）、`StackCount == 0`、layer canvas 子物体数回到基线（无孤儿实例）；随后**用同一 view type** 再 Push 一个正常 prefab 成功（证明 in-flight 标记已在失败路径清掉）。修复前：不 `Release` → `GetRefCount == 1`、`ReleasedKeys` 为空 → 必红。

### 5. D4 — HUD 文本随场景泄漏（`GameHud.cs`）

**改法**

- `GameHud.cs:74-89`（`OnDestroy`）：解订阅之后追加 `DestroyCaption(ref _pickupText)`、`DestroyCaption(ref _enemyText)`。
- `:92-112` 新增 `DestroyCaption`：`null`（含 Unity 伪 null）直接返回 → 先 `transform.SetParent(null, false)` 脱离常驻 canvas → 运行期 `Object.Destroy`，编辑器（EditMode 测试）`Object.DestroyImmediate`；字段置 null。
- 类注释 `:22-27` 说明"载体随场景销毁、文本挂在常驻 UIRoot 上，因此由 OnDestroy 显式销毁"。

**为什么这样改**

- 文本的父节点是 `DontDestroyOnLoad` 的 `UIRoot`（`UIRoot.cs:84`），载体 GameObject 在玩法场景里，随场景卸载销毁 → 文本留在常驻 canvas 上，每进一次 `SCN_Game` 多 2 个 Text，且旧文案继续显示。销毁责任只能落在 HUD 自己身上（`GameBootstrap` 只负责创建，见 `GameBootstrap.cs:347-350`，本次未改）。
- 先 `SetParent(null)` 再销毁：避免在常驻 canvas 上出现"已标记销毁但本帧仍在"的中间帧残留；EditMode 分支是为了让测试能立即断言（运行时 `Destroy` 延迟到帧末，行为一致）。

**自测举证**（`GameHudLeakTests.cs`，新 asmdef `Game.View.Tests`）

- `HudHostDestroyed_LeavesNoCaptionsOnThePersistentUIRoot`（`:44-71`）：循环 3 次「建 host → `Build(UIRoot.EnsureCreated())` → `DestroyImmediate(host)`」；每次断言 Build 后 HUD canvas 子物体 = baseline+2、销毁后 = baseline，并且 `Find("PickupCounter")` / `Find("EnemyHint")` 都为 null（用 Unity 的 `== null` 判定）。修复前：销毁后仍是 baseline+2 → 第 1 轮必红。
- `HudCaptions_AreStillCreatedOnTheHudCanvas`（`:73-92`）：防"为了不泄漏干脆不建"的退化解 —— 断言两个 caption 确实挂在 HUD canvas 上、初始文案为 `拾取计数：0`。

### 6. 如何自测（在有 Unity 的机器上）

```bash
# EditMode（含本次新增 9 个用例；新程序集 Game.View.Tests 会自动被发现）
"C:/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe" -batchmode -quit \
  -projectPath "C:/Users/1/Desktop/zcode/GameFramework/UnityProject" \
  -runTests -testPlatform EditMode -testResults TempLogs/editmode_h01.xml -logFile -
# 架构校验（新增 asmdef 为 *.Tests，矩阵豁免；期望条目数不变）
"C:/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe" -batchmode -quit \
  -projectPath ".../UnityProject" -executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine
```
红/绿判别：把 `UIManager.cs` 的 in-flight 登记与 `finally` 回滚、`SettingsModel.WriteAtomically`、`RebindService` 的两个 `catch`、`GameHud.DestroyCaption` 逐个临时还原，对应用例应分别变红（见上文每节的"修复前失败点"）。

### 7. 未能验证的项（如实列出）

1. **C# 未编译、9 个新用例与既有 542 个 EditMode 用例均未运行**（Unity 批处理不可用 + 独立编译器调用被安全钩子拦截）。所有"必红/必绿"结论都是**静态推理**，不是执行结果。
2. **D1 的真实交互式重绑定链路未覆盖**：`Application.isPlaying == false` 时 `Rebind` 直接早退，`Start()`/设备事件/`OnComplete` 的真实触发路径只能在 PlayMode 验证。现有与新增用例都是通过反射直接驱动 `CompleteCaptured` / `StartCapture`（沿用该文件既有的 reflection harness 约定），属于"同一份生产代码 + 人工构造的失败输入"。
3. **`StartCapture` 的失败注入是构造的**：生产路径下 `Rebind` 已先校验 binding 范围/composite，正常不会走到 Input System 抛错；用例用 0 binding 的临时 action 强制抛错来验证 catch 分支的恢复契约。其他真实失败原因（设备层未初始化等）未逐一构造。
4. **D2 的 flush 与"掉电"未真实验证**：`Flush(flushToDisk:true)` 只能靠代码审查确认；"中断不截断"用"临时路径被占 → 保存失败 → 主文件仍是旧值"间接证明，未做真实断电/进程 kill 测试。
5. **`File.Replace` 的平台行为**：Windows（本项目目标平台）走 `ReplaceFile`，无平台分支；若将来支持 Mono/Unix 目标，需补 `PlatformNotSupportedException` 兜底 —— 本次未做（未新增未验证的分支）。
6. **D4 的运行时路径**：测试在 EditMode 用 `DestroyImmediate` 断言；PlayMode 下走 `Object.Destroy`（帧末生效），"多进几次 `SCN_Game` 后 UIRoot 下无残留"未经真机/PlayMode 场景切换验证。
7. **D3 的淡入窗口**：EditMode 淡入是瞬时的，用例用"资源加载期间重入"复现同一 in-flight 窗口；真实的淡入期间二次点击未在 PlayMode 下复现（逻辑上同一集合、同一时机，但未执行）。
8. **已知遗留（未改，超出简报范围）**：`SettingsRowFactory.DescribeOutcome` 把 `Failed` 统一显示为「重绑定不可用（需在游戏运行中操作）」（`SettingsRowFactory.cs:498-509`），因此"保存失败"时行的反馈文案仍偏"需要 play mode"。行为已正确（行会解锁、可再次点击、有 `ZLog.Error` 与 Failed 回调），仅文案不够准确；该项属设置面板视图层，本次未动。


---

## 验收清单（主控撰写，验收代理执行）

- [ ] V1 D1：保存失败时 `onComplete` 收到 `Failed`，`IsRebinding == false`，且能再次发起重绑定（有测试举证）
- [ ] V2 D2：settings.json 写入路径为 tmp+rename + 落盘 flush；中断不会留下截断的主文件
- [ ] V3 D3：淡入期重复 Push 被拒；Push 异常路径无资源泄漏（引用计数回到基线）
- [ ] V4 D4：进出 `SCN_Game` 多次后 UIRoot 下无 HUD 文本残留
- [ ] V5 新增/修改的测试为**真断言**（非 DoesNotThrow 空壳），且能在修复前失败
- [ ] V6 未触碰无关文件；`git status` 改动范围与简报一致
- [ ] V7 施工报告「未能验证」项已如实标注（C# 未编译是已知环境限制，不算 FAIL，但不得声称已编译通过）

## 验收结论（验收代理填写）

（待填）
