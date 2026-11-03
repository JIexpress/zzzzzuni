# TASK-A-M3-01 UI 框架：MVP + 分层 Canvas + 面板栈（M3 地基）

状态: doing | 派发: 2026-10-25 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：A 轨 M3 第一件——UI 框架核心：分层 Canvas（渲染顺序固定）、面板栈（Push/Pop/暂停恢复）、完整生命周期、MVP 装配（Presenter 纯 C# 可单测）、PanelConfig 数据驱动。设置界面（M3-02）在此基础上只写数据。

**设计要点**：
1. `ZFrame/View/UI/`：
   - `UIConsts`：层级常量 Background=0/HUD=100/Panel=200/Popup=300/Toast=400/Loading=500/Debug=900/Top=1000（每层一个独立 Canvas+CanvasScaler，根 `UIRoot`（RuntimeInitializeOnLoadMethod 自建， DontDestroyOnLoad，含 EventSystem 检查创建））
   - `PanelConfig`（ScriptableObject 或纯类+工厂，取简：纯类记录）：prefabKey、layer、blockMask（是否阻断下层输入）、cachePolicy（None/Once）、animIn/Out（枚举：None/Fade，本期只 None+Fade 简版）
   - 生命周期接口 `IUIView { void OnInit(object args); void OnShow(); void OnPause(); void OnResume(); void OnHide(); void OnDestroy(); }`
   - `UIPresenter`（抽象纯 C#）：持有 IUIView 引用（构造注入），`Attach/Detach`；订阅 EventBus 的 scope 管理（EventScope 集成）
   - `UIManager`：`Push<TView>(config, args)`/`Pop(config?)`——栈语义：Push 后次顶 OnPause（若 blockMask 阻断）、Pop 后次顶 OnResume；同面板重复 Push 拒绝；按层归 Canvas（SetParent+排序）；加载走 IResourceService（prefab key），列表项池化留接口；`PopAll(layer)`；事件 PanelOpened/PanelClosed（EventBus）
   - MVP 装配：`UIManager.Push` 内反射/工厂创建 Presenter 并 Attach（注册表 `UIBinding.View→Presenter` 静态字典，Setup 时注册——避免反射扫全程序集）
2. 示例面板（入库，框架验收用）：`UI_TestPanel`（一个按钮+文本，Presenter 订阅测试事件改文本、按钮 Fire 事件）—— prefab 经 Setup 脚本生成（代码建 UGUI 结构），resource key "ui_test_panel"
3. 单测（ZFrame.Debug.Tests 追加；真 UGUI 在 EditMode 可用（Canvas/按钮可构造）——若不稳可抽象 IUIView 用 Fake 测栈逻辑+真 prefab 冒烟 1 例）：
   - 面板栈：Push 两个→次顶 OnPause 调用序、Pop→OnResume；重复 Push 拒绝；跨层互不干扰
   - 生命周期全序断言（Fake View 记录调用序列：OnInit→OnShow→(Pause/Resume)*→OnHide→OnDestroy）
   - Presenter：Attach 后 EventBus 事件驱动 View 更新（Fake View）、Detach 后退订（事件不再触达）
   - 层级：Push 到不同 layer → transform 父级 Canvas 正确
   - blockMask：阻断层 Popup 压 HUD 时 HUD 收到 OnPause
4. 自测：EditMode 全绿 + ValidateFromCommandLine exit 0（Unity 纪律）

**约束**：可动 `UnityProject/Assets/ZFrame/View/`、`UnityProject/Assets/UI/`（示例 prefab）、`UnityProject/Assets/ZFrame/Tests/`、本任务日志；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0；UGUI 事件系统（InputSystemUIInputModule 兼容留注释——InputSystem M4 接）。

**交付物**：框架全件+示例面板+单测全绿+施工报告四节。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理 | **完成**: 2026-10-25 | **自测**: EditMode 261/261 全绿（新增 35 例）、ValidateFromCommandLine exit 0（arch_report: 违规数 0, PASS）

### 1. 交付物清单

框架核心（`Assets/ZFrame/View/UI/`）：
- `UIConsts.cs` — `UILayer` 八层枚举（Background=0/HUD=100/Panel=200/Popup=300/Toast=400/Loading=500/Debug=900/Top=1000），枚举值即层 Canvas sortingOrder；`AllLayers[8]`/`LayerCount`/`SortOrder()`。
- `UIRoot.cs` — 自建根：`[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` 引导 + `EnsureCreated()`（幂等，可测；static 丢失时从场景图回收孤根）；每层一个独立 Canvas+CanvasScaler(ScaleWithScreenSize 1920×1080)+GraphicRaycaster；DontDestroyOnLoad（仅 Play 模式）；EventSystem 检查创建（StandaloneInputModule，M4 InputSystem 换 InputSystemUIInputModule 的注释留在该方法处）；根上挂 `UIAnimRunner`。
- `PanelConfig.cs`（纯类+`Create` 工厂）、`PanelCachePolicy.cs`（None/Once）、`PanelAnim.cs`（None/Fade）。
- `IUIView.cs` — 六段生命周期接口；`UIViewBase.cs` — MonoBehaviour 基类，**`IUIView.OnDestroy` 用显式接口实现**（避免 Unity 把名为 OnDestroy 的公有方法当销毁消息重复回调），子类覆写 `OnViewDestroy()`。
- `UIPresenter.cs` / `UIPresenterOfT.cs` — 抽象纯 C#（无 UnityEngine using）；构造注入 IUIView；Attach 时先 `EventBus.OpenScope()` 再进 OnAttach，Detach 时 scope.Dispose() 批量退订；幂等。
- `UIBinding.cs` — View→Presenter 静态注册表（`Func<IUIView,UIPresenter>` 工厂字典）；`EnsureDefaults()` 幂等装示例绑定（TestPanelView→TestPanelPresenter）。
- `UIManager.cs` — `Push<TView>(config, args)`（async Task<bool>，重复类型/重复 prefabKey 拒绝返回 false；缓存复用→预制体实例化→纯 C# 实例三路）/`Pop(config?)`/`PopAll(layer)`/`EvictPanelCache()`/`TryGetView<T>()`；栈语义：自顶向下重算 paused——上方任一 blockMask 面板存在则下层 OnPause，解除则 OnResume（只发转换沿）；暂停面板实例的 GraphicRaycaster 同步禁用；生命周期全序 OnInit→Attach→OnShow→(Pause/Resume)*→OnHide→Detach→OnDestroy；prefab 经 IResourceService.LoadAsync 按键加载、Pop 销毁时 Release；cachePolicy=Once 只缓存实例（OnDestroy 顺延至 Evict）；PanelOpened/PanelClosed 事件 Fire 到 EventBus；编辑模式用 DestroyImmediate 保证断言即时性（运行时 Destroy）。
- `UIAnimRunner.cs` — 根上的协程宿主，PanelAnim.Fade 的 CanvasGroup alpha 简版渐变（运行时；编辑模式瞬时完成保证测试确定性）；`IUIWidgetPool.cs` — 列表项池化留的接口（本期不实现，见取舍）。
- 事件：`PanelOpenedEvent.cs`/`PanelClosedEvent.cs`（prefabKey/viewType/layer）。

示例面板：`UI/Sample/`（`TestPanelView`：全屏阻断 Popup、状态文本+按钮，按钮 onClick→`EventBus.Fire(UiTestButtonClickedEvent)`；`TestPanelPresenter`：纯 C#，订阅按钮事件与 `UiTestSetStatusEvent` 驱动文本；`UiTestButtonClickedEvent.cs`/`UiTestSetStatusEvent.cs`）。预制体由 `View/Editor/UISetup.cs`（#if UNITY_EDITOR，先例同款）代码生成 UGUI 结构入库：`Assets/UI/Resources/ui_test_panel.prefab`（全屏 raycastTarget Image=物理阻断+CanvasGroup+TestPanelView 串接+StatusText+Button，菜单 ZFrame/Setup/Generate UI_TestPanel prefab，批处理 `SetupFromCommandLine` exit 0，幂等+重载后校验）。

单测（`Assets/ZFrame/Tests/Debug/`，35 例，Support 提供 FakeUIView/FakeAltView/FakeThirdView/FakeStatusView/FakeStatusPresenter/FakeResourceService/UIRecordLog）：栈语义 11 例、生命周期全序与缓存 6 例、Presenter 纯 C# 5 例、层级/根结构 7 例、prefab 冒烟 3 例、注册表 3 例。

### 2. MVP 装配模式（验收项 4 说明）

**注册表，无反射扫描**：`UIBinding` 是静态 `Dictionary<Type, Func<IUIView,UIPresenter>>`（`UIBinding.cs` 第 13-14 行），Setup 时经 `Register<TView>(factory)` 显式登记，`UIManager.Push` 在第 151 行 `UIBinding.TryGetFactory(viewType, ...)` 查表工厂创建并 Attach——全程无程序集扫描/特性反射（`Activator` 仅用于纯 C# 测试视图的实例化，非 Presenter 装配）。Presenter 纯 C#：`UIPresenter.cs`/`UIPresenterOfT.cs`/`TestPanelPresenter.cs` 均无 `using UnityEngine`，仅依赖 ZFrame.Core 的 EventBus/EventScope。

### 3. 测试取舍（验收项 2/3 说明）

- **真 UGUI 的稳定性边界**：EditMode 下 UGUI Canvas/按钮本身可构造且冒烟 3 例全过（真 ResourceService+真 prefab+按钮 onClick 驱动全链路）；但施工中发现**测试程序集里的 MonoBehaviour 无法 AddComponent**（Unity 静默返回 null，批处理诊断实证：TestPanelView/DebugPanelRoot 正常、StubPanelView 恒 null）——因此"组件视图"路径的栈/阻断用例改用入库的真 `ui_test_panel` prefab（TestPanelView 属 ZFrame.View 运行时程序集）+ FakeResourceService 多键注册解决，栈语义/生命周期全序仍按简报用纯 Fake View 测。
- **生命周期全序**用单条共享时序日志（UIRecordLog）断言两个面板 10 次调用的精确全局顺序（含 Pause/Resume 交错与两次 Pop 的 Hide/Destroy 段）。
- **阻断**语义分三层验证：纯 Fake（OnPause 转换沿）、组件视图（GraphicRaycaster.enabled 翻转）、prefab 自带全屏 raycastTarget Image（物理层点击不落下层）。
- **IUIWidgetPool**：简报要求"列表项池化留接口"——已留接口与注释（`IUIWidgetPool.cs`），面板实例复用走 cachePolicy=Once，本期不实现池（避免与生命周期所有权冲突），M4+ 列表需求落地时再实现。

### 4. 偏离与备注（供验收/主控知悉）

- **prefab 文件名 `ui_test_panel.prefab`**：简报指定 resource key "ui_test_panel"，而 Resources 路径在 Android 上大小写敏感，故文件名与 key 完全一致（偏离 file-conventions §5 的 `UI_` 前缀示例，属键名一致性的有意取舍；Addressables 迁移时可一并显式化）。
- **Fade 简版**：AnimIn 在 OnShow 后非阻塞淡入；AnimOut 仅对 cachePolicy=Once 存活实例 fire-and-forget 淡出（销毁路径直接销毁，避免生命周期等待动画）；运行时协程驱动、编辑模式瞬时，测试不受影响。
- **EventSystem 输入模块**：StandaloneInputModule（legacy）；M4 接 InputSystem 时在 `UIRoot.EnsureEventSystem` 单点换 InputSystemUIInputModule（已留注释）。
- `UI/Resources/ui_test_panel.prefab` 中 `m_Name:`/`m_EditorClassIdentifier:` 行尾空格是 Unity YAML 序列化原生输出（与既有资产一致），非手写尾随空格，未改动。
- 架构校验器矩阵未动（无新 asmdef；ZFrame.View→Core/Data/Gameplay 引用未变）；报告 `TempLogs/arch_report.txt`：违规数 0，PASS。

## 验收清单（主控写）

1. 自跑 EditMode 全绿（用例数写入结论）
2. 面板栈语义用例核对（Pause/Resume 序/重复 Push 拒/跨层独立/阻断）
3. 生命周期全序断言用例；Presenter EventBus 驱动+Detach 退订用例
4. MVP 装配注册表模式（无反射扫描全程序集——行号）；Presenter 纯 C#（无 UnityEngine，行号）
5. 层级常量与 UIRoot 结构核对（8 层）
6. ValidateFromCommandLine exit 0（自跑）
7. 文件规范+施工报告完整

## 验收结论（验收代理写）

**验收**: general-purpose 验收子代理 | **日期**: 2026-10-25 | **结论: PASS（7/7）**

1. **自跑 EditMode 测试 — PASS**：遵守 Unity 纪律（确认无 Unity.exe 残留、删 Temp/UnityLockfile 确认不存在、Start-Process 批处理、以 results.xml 为准、无 -quit）。结果 `TempLogs/accept_m3_01/editmode_results.xml`：**total=261, passed=261, failed=0, skipped=0**，Unity 进程 exit 0。其中 UI 新增 35 例全部在列：UIStackTests 11、UIHierarchyTests 7、UILifecycleTests 6、UIPresenterTests 5、UIPrefabSmokeTests 3、UIBindingTests 3。
2. **面板栈语义 — PASS**：`UIStackTests.cs` 逐例核对——阻断压层 `Push_BlockingPanel_PausesBelow_Pop_ResumesIt`（L33：PauseCount=1/ResumeCount=0，Pop 后 ResumeCount=1，只发转换沿）；非阻断不暂停 L50；重复 Push 拒绝 L61（重复类型，栈不变且 UIRecordLog 无新增触达）与 L73（跨类型重复 prefabKey 拒绝）；跨层独立 L81；`PopAll` 只弹本层且底层面板恢复 L128/L144；PanelOpened/PanelClosed 带 layer/key L160。
3. **生命周期全序 + Presenter — PASS**：`UILifecycleTests.cs` L32-62 `PushBlockPop_ExactLifecycleOrder` 用共享 UIRecordLog 断言两面板 10 次调用的精确全局顺序（OnInit→OnShow→OnPause→OnHide→OnDestroy→OnResume→OnHide→OnDestroy）；args 透传 L65、cachePolicy=Once 缓存复用/顺延销毁 L76、None 释放资源键 L116。`UIPresenterTests.cs` L55 `EventBusEvent_DrivesViewUpdate_WhileAttached`（Fire→View 更新）、L66 `Detach_Unsubscribes_EventsNoLongerReachTheView`（SubscriberCount 归零 + Fire 不再触达）、L83 幂等。
4. **MVP 装配 — PASS**：`UIBinding.cs` L17-18 静态 `Dictionary<Type, Func<IUIView,UIPresenter>>` 注册表、L30 显式 `Register<TView>`、L66 Setup 登记；`UIManager.cs` L151 `UIBinding.TryGetFactory` 查表装配。grep 实证 `View/UI/` 下无 `System.Reflection`/`GetTypes()`/特性扫描；`Activator` 仅 UIManager.cs L139 用于纯 C# 视图实例化（非 Presenter 装配）。Presenter 纯 C#：`UIPresenter.cs`/`UIPresenterOfT.cs`/`UIBinding.cs`/`Sample/TestPanelPresenter.cs` grep `using UnityEngine` 零命中（仅文档注释提及），仅依赖 ZFrame.Core。
5. **八层 UIRoot — PASS**：`UIConsts.cs` L18-43 八层值 0/100/200/300/400/500/900/1000 与简报一致，`AllLayers[8]` L46-56；`UIRoot.cs` L109-126 循环每层建独立 Canvas（L116 sortingOrder=层值）+CanvasScaler（ScaleWithScreenSize 1920×1080，L118-121）+GraphicRaycaster（L123）；L50 RuntimeInitializeOnLoadMethod 自建、L82-85 DontDestroyOnLoad（仅 Play）、L129-140 EventSystem 检查创建（StandaloneInputModule，M4 InputSystemUIInputModule 注释在 L138）。测试实证 `UIHierarchyTests.cs` L50-65（8 canvas+排序+组件）。
6. **ValidateFromCommandLine — PASS**：自跑（`TempLogs/accept_m3_01/run_arch.ps1`）exit 0；`TempLogs/arch_report.txt`：asmdef 17、违规数 0、PASS。
7. **文件规范 + 报告 — PASS**：抽查 8 个新 .cs（UIManager/UIRoot/UIPresenter/UIBinding/UIConsts/TestPanelPresenter/UIStackTests/UILifecycleTests）均 UTF-8 无 BOM、LF（od 抽字节 CR=0）；prefab `Assets/UI/Resources/ui_test_panel.prefab` 与 key 一致在库；施工报告四节齐全（简报/施工报告/验收清单/验收结论）。

**取舍披露评估（3 条，均接受）**：① 测试程序集 MonoBehaviour AddComponent 不可用的限制——组件视图路径改用入库真 prefab（TestPanelView 属运行时程序集）覆盖，UIHierarchyTests/UIPrefabSmokeTests 实证该路径真实有效，取舍合理；② prefab 文件名 `ui_test_panel` 偏离 `UI_` 前缀示例——为 Resources 键名大小写一致性（Android 敏感），有意取舍且已披露，可接受（Addressables 迁移时显式化）；③ Fade 简版+IUIWidgetPool 只留接口——与简报"本期只 None+Fade 简版/池化留接口"要求相符，非偏离。
