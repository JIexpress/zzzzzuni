# TASK-A-R9-01 侦察笔记 —— 设置界面接「FrameGovernor 动态分辨率」+「帧生成(FG)」

> 只读侦察，未修改任何代码。所有路径相对仓库根 `C:/Users/1/Desktop/zcode/GameFramework/`。
> 行号以侦察时（2026-10-28）磁盘内容为准。拿不准的标 `【待确认】`。

---

## 0. 三条前置事实更正（简报里的假设与代码不符）

1. **「超分」行不是"能力不满足灰显"，是"标签后缀"**：不支持的厂商项由
   `UpscalerSettingOptions.BuildOptionLabels` 在选项文案后追加「（本机不支持）」，
   `SettingsRowFactory.BuildEnumRow` 从不设置 `interactable`（`SettingsRowFactory.cs:277-348`）。
   所以"照抄超分行做灰显"没有现成先例——真灰显要新增字段 + 工厂改动（见 ④-步骤3）。
2. **「渲染比例」行不存在**：`SettingsRegistry` 的类注释（`SettingsRegistry.cs:22`）写着
   "渲染比例 is a STORED placeholder"，但 `BuildVideoPage`（`SettingsRegistry.cs:109-172`）
   里没有这一行。文档漂移，勿据此施工。
3. **`requiresRestart` 是死字段**：`SettingDefinition.RequiresRestart`（`SettingDefinition.cs:65-66`）
   被赋值（`:116`）后在**全项目无任何消费点**（grep 只有定义与赋值两处），UI 不会提示重启。

---

## ① 关键文件地图

| 文件（绝对路径） | 作用 | 关键行 |
|---|---|---|
| `UnityProject/Assets/ZFrame/View/Pipeline/FrameGovernor.cs` | 治理器纯 C# 核心（无 UnityEngine） | 类 `:28`；`Enabled :40`；`TargetMs :43`；`Level :49`；`CurrentState :52`；`Tick :80` |
| `UnityProject/Assets/ZFrame/View/Pipeline/FrameGovernorHost.cs` | MonoBehaviour 薄适配：挂循环、落地措施、恢复基线 | `AutoBoot :85-96`；`Initialize :104-146`；注册循环 `:141`；`SetEnabled :153-173`；`SetTargetFps :176-183`；`Tick :228-254`；`EnableGovernor :256-283`；`RestoreBaseline :301-329`；`ApplyState :331-360`；`ApplyRenderScale :362-382` |
| `UnityProject/Assets/ZFrame/View/Pipeline/GovernorLadder.cs` | 措施阶梯（RenderScale 0.75~1.0 步长 0.05 → Shadows → PostFx → TargetFps 放弃档） | 常量 `:26-31`；`Create :68`；`MeasureAtLevel :143`；`StateAtIndex :153` |
| `UnityProject/Assets/ZFrame/View/Pipeline/GovernorTuning.cs` | 迟滞参数（±10%，N=5s > M=2s） | `Default :40` |
| `UnityProject/Assets/ZFrame/View/Pipeline/RecorderFrameTelemetry.cs` | 遥测：GPU "GPU Frame Time" / CPU 墙钟回退 | `GpuFrameMs :80-91`；`Pump :101-110` |
| `UnityProject/Assets/ZFrame/View/Settings/SettingsRegistry.cs` | **五页签的唯一数据源**（代码里的定义表，无 SO/CSV） | `BuildDefaults :94-104`；视频页 `:109-172`；超分行 `:158-162`；画质档位 `:151-154` |
| `UnityProject/Assets/ZFrame/View/Settings/SettingDefinition.cs` | 一行设置的数据结构 + 工厂方法 | `Toggle :122-130`；`EnumSetting :155-174`；`Apply :63`；`RequiresRestart :66` |
| `UnityProject/Assets/ZFrame/View/Settings/SettingsRowFactory.cs` | 类型→UGUI 控件（纯代码生成） | `CreateRow :102-171`；`BuildToggleRow :175-208`；`BuildEnumRow :277-348` |
| `UnityProject/Assets/ZFrame/View/Settings/SettingsModel.cs` | 值状态 + settings.json 持久化 + 即时 apply | `Set :88-93`；`Save :142-171`；`Load :221-269`；`ApplyValue :284-294` |
| `UnityProject/Assets/ZFrame/View/Settings/UpscalerSettingOptions.cs` | **要照抄的样板**：选项映射 + 能力门控标注 + apply 入口 | `BuildOptionLabels :69-91`；`ApplyOptionIndex :98-111` |
| `UnityProject/Assets/ZFrame/View/Settings/SettingsPanelView.cs` | 打开时按 registry 逐行生成；无滚动区 | `SelectPage :163-198`；`Update(Esc) :278-284` |
| `UnityProject/Assets/ZFrame/View/Settings/SettingsPanelPresenter.cs` | 三按钮语义；`Set` 即 apply | `OnSettingChanged :73-95`；`OnApplyRequested :97-102` |
| `UnityProject/Assets/ZFrame/View/Settings/SettingsEntry.cs` | HUD「设置」入口；共享 model | `OpenSettings :88-116`；`Load :98` |
| `UnityProject/Assets/Game/Bootstrap/GameBootstrap.cs` | 主菜单/暂停打开设置；**唯一另一处 Load** | `OpenSettings :188-206`；`Load :197` |
| `UnityProject/Assets/ZFrame/Core/Upscaler/IUpscalerSettings.cs` | 跨程序集设置服务契约 + caps 结构 | 接口 `:12-32`；`UpscalerCapsInfo :35-60` |
| `UnityProject/Assets/ZRender/Runtime/ZrUpscalerSettings.cs` | 实现 + 自注册 + 优雅降级 | 实现 `:62-177`；P/Invoke 缝 `:22-54`；自注册 `:184-194` |
| `UnityProject/Assets/ZRender/Bindings/ZrNative.cs` | **唯一 DllImport 集中地** | 声明 `:12-43`；门面 `:66-85`；caps 结构 `:91-102` |
| `Native/zplugin/zplugin.h` / `zrender.def` | dll 导出清单（9 个） | `zplugin.h:21-104`；`zrender.def:10-18` |
| `Native/zupscaler/fg_ffx.h` / `fg_ffx.cpp` | FG 封装（FrameGenerator） | **"本模块无 C 导出" `:35-36`**；`FrameGenerator :171-250` |
| `UnityProject/Assets/ZFrame/Editor/ArchitectureValidator.cs` | asmdef 矩阵 + 逻辑层纯度 | 矩阵 `:31-81`；GUID 引用豁免 `:111-114`；`ValidateAsmdef :144-188` |
| `UnityProject/Assets/ZFrame/View/ZFrame.View.asmdef` | View 程序集（URP 走 GUID 引用） | `:4`（`GUID:df38…`=URP Core，`GUID:15fc…`=URP Runtime） |

---

## ② FrameGovernor 接线现状

### 2.1 类型与程序集
- 纯核心：`ZFrame.View.FrameGovernor`（`FrameGovernor.cs:28`，普通 sealed class，零 UnityEngine 依赖）。
- 宿主：`ZFrame.View.FrameGovernorHost`（`FrameGovernorHost.cs:45`，`MonoBehaviour, ITickable`，`[DisallowMultipleComponent]`）。
- 程序集：**ZFrame.View**（`UnityProject/Assets/ZFrame/View/ZFrame.View.asmdef`）。命名空间 `ZFrame.View`。
  不新增程序集 → 不需要动 asmdef 矩阵。

### 2.2 公开 API（可被设置界面调的全部入口）
| 成员 | 位置 | 语义 |
|---|---|---|
| `static Instance` | `FrameGovernorHost.cs:67` | 自举实例，**boot 前为 null** |
| `bool IsGovernorEnabled` | `:70` | 当前是否在治理（默认 false） |
| `float TargetFps` | `:73` | 目标帧率（默认 `DefaultTargetFps = 60`，`:50`） |
| `FrameGovernor Governor` | `:76` | 纯核心，可读 `Level` / `CurrentState` / `LastSampleUsedGpu` |
| `IFrameTelemetry Telemetry` | `:79` | 诊断 |
| `void Initialize(IFrameTelemetry = null)` | `:104-146` | 挂循环 + 建禁用占位核心；幂等 |
| **`void SetEnabled(bool)`** | `:153-173` | 开/关 DRS 治理；开启时捕获用户基线 |
| **`void SetTargetFps(float)`** | `:176-183` | TargetMs = 1000/fps；≤0 回落 60 |
| `void Shutdown()` | `:192-225` | 恢复基线 + 注销循环 |
| `void Tick(float dt)` | `:228-254` | 每帧泵遥测 + 走阶梯 + 落措施 + 发事件 |

**没有**的 API（施工时别假设有）：
- 没有"画质档位/分辨率区间"设置入口：RenderScale 带宽是编译期常量
  （`GovernorLadder.cs:27-31`：min 0.75 / max 1.0 / step 0.05），`Create` 的参数虽可传，
  但 `FrameGovernorHost.EnableGovernor` 固定用默认值（`:272`）。
- 没有 Tuning 注入口：固定 `GovernorTuning.Default`（`FrameGovernorHost.cs:273`）。
- 没有"查询当前缩放百分比"的属性；只能 `Governor.CurrentState.RenderScale`（`:52` → `GovernorState.cs`）。

### 2.3 谁构造 / 谁驱动
- 自举：`[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)] AutoBoot`（`:85-96`）→
  新建 `GameObject("~FrameGovernorRoot")` + `DontDestroyOnLoad` + `AddComponent<FrameGovernorHost>().Initialize()`。
  **场景/Prefab 里没有这个组件**，也不在 GameBootstrap 里。
- 循环：`Initialize` 内 `GameRoot.EnsureInstance()`（`:135-139`）+ `Services.Get<ITickQueue>().Register(this, TickStage.View)`（`:141`）。
- 每帧：`GameLoopDriver` 在 View 阶段 `TickAll(TickStage.View, …)`
  （`UnityProject/Assets/ZFrame/Core/GameLoop/GameLoopDriver.cs:62`）→ `FrameGovernorHost.Tick`（`:228`）。
  刻意不走 `Update`（`:14-16` 注释）。

### 2.4 措施落地路径（改 DRS 到底改了谁）
- `RenderScale` → `GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset` 的
  `.renderScale`（`ApplyRenderScale :362-382`）；**只对 URP 有效**，非 URP 只告警一次（`:377-381`）。
  写前记录每个被碰过的 asset 原值（`:366-369`），关闭/销毁时逐个还原（`:309-319`）。
- `Shadows` → `PipelineManager.SetTier` **只降不升**（`:334-345`，钳制上界 = 用户档位），
  且 `SetTier` 会写 PlayerPrefs（`PipelineManager.cs:143-144`）+ 发 `TierChangedEvent`（`:147-150`）。
- `PostFx` → `PipelineManager.Instance.GradeVolume.weight`（`:384-392`）。
- `TargetFps` → 只打 WARN（`:249-253`），同时 `GovernorMeasureChangedEvent` 上 EventBus（`:247`）。

### 2.5 现有配置入口
**零个**。`FrameGovernorHost` 的 11 个字段全 private（`:54-64`），无 `[SerializeField]`、无 SO、无 settings.json 键。
唯一"可配置"是常量 `DefaultTargetFps = 60`（`:50`）。
外部消费者：全项目 grep `FrameGovernorHost` / `GovernorMeasureChangedEvent` / `SetEnabled(`，
**除自身与测试外无任何调用点**——这正是"已建未接线"的证据。

A-R8-01 施工日志自己也确认了（`Docs/Logs/TASK-A-R8-01.md:85`）：
> 设置页 DRS/目标帧率行实接（简报"设置界面后续接"）：入口即 `FrameGovernorHost.Instance.SetEnabled(bool)/SetTargetFps(float)`。

---

## ③ 帧生成 FG 的 C# 可达性 —— **当前完全不可达**

### 3.1 原生侧已实现（B-R8-02 交付）
- `Native/zupscaler/fg_ffx.h` / `Native/zupscaler/fg_ffx.cpp`：`FrameGenerator` 类
  （`QuerySupport` / `Create` / `CreateFgSwapchainForHwnd` / `ConfigureFrame` / `DispatchPrepare` /
  `PresentFrame` / `WaitForPresents` / `Shutdown`，`fg_ffx.h:171-250` 区段）。
- 纯函数：`ZupFgClassifySupport`、`ZupFgMapResourceState`、`ZupFgValidateCreateDesc`、
  `ZupFgFrameCountsDoubled`、`ZupFgReturnCodeName`（`fg_ffx.h:44-89`）。
- 能力门控事实源：`FrameGenSupport { Unavailable / NoProvider / RejectedByRuntime / Available }`
  + `FrameGenProbe { support, returnCode, providerName }`（`fg_ffx.h:47-63`）。

### 3.2 关键结论：**没有跨 DLL 的 C ABI，因此 C# 一行也调不到**
- 头文件原话（`Native/zupscaler/fg_ffx.h:35-36`）：
  > `// C ABI 纪律：本模块无 C 导出（窗口验证程序/单测静态链接使用），跨 DLL 的 FG`
  > `// 入口留待 zplugin 任务。`
- `Native/zplugin/zplugin.h` 的 `extern "C"` 块（`:17-107`）只有：
  `ZR_ApiVersion / ZR_GetVersion / ZR_GetBuildInfo`（`:21-27`）、
  `ZR_MeshLoadFromMemory / ZR_MeshStatsGet / ZR_MeshFree`（`:35-43`）、
  `ZR_SetBackend / ZR_GetBackend`（`:51-54`）、
  `ZR_Upscaler_SetPreferred / GetActive / QueryCaps`（`:97-104`）。**无任何 FG 符号**。
- `Native/zplugin/zrender.def` 白名单只列 9 个符号（`:10-18`），同样无 FG。
- C# 侧 `UnityProject/Assets/ZRender/Bindings/ZrNative.cs` 的 DllImport 只有
  API 版本/构建信息（`:12-19`）、.zmesh 三条（`:24-31`）、超分三条（`:36-43`）。
- 佐证：全仓 `grep -i "framegen|frame generation|帧生成" --include=*.cs UnityProject/Assets` → **0 命中**。

### 3.3 能力门控（capability query）在 C# 侧怎么拿 —— 超分那套的完整链路
1. 契约在 Core：`IUpscalerSettings`（`ZFrame/Core/Upscaler/IUpscalerSettings.cs:12-32`）
   —— `IsAvailable` / `QueryCaps()` / `TrySetPreferred(int)` / `TryGetActiveBackend(out int)`；
   `UpscalerCapsInfo`（`:35-60`）是逐字段拷贝的托管快照（**没有 FG 字段**）。
2. 实现在 Render：`ZrUpscalerSettings`（`ZRender/Runtime/ZrUpscalerSettings.cs:62-177`）
   —— 捕获 `DllNotFoundException` / `EntryPointNotFoundException`，折算成"不可用"+ 一次性 WARN（`:166-176`）。
3. 注册：`ZrUpscalerBootstrapper`（`:184-194`）用 `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`
   向 `Services.Register<IUpscalerSettings>`（EditMode 不触发 → 测试自己注入桩）。
4. View 消费：`UpscalerSettingOptions.BuildOptionLabels` 走
   `Services.TryGet(out IUpscalerSettings)`（`:69-73`），能力未知一律标注不支持（`:80-91`）。
5. 为什么必须绕这一圈：**asmdef 矩阵禁止 `ZFrame.View` 引用 `ZFrame.Render`**
   （`ArchitectureValidator.cs:44-49` 只允许 Core/Data/Gameplay/InputSystem；
   设计方案 §3.2 `Docs/框架设计方案.md:115-131` 同样禁止）。

### 3.4 FG 若要在 Unity 里真开，缺口清单（不是本任务能塞进"加一行"的）
| 缺口 | 位置 | 现状 |
|---|---|---|
| C ABI 导出（Enable/Disable/QuerySupport/Present 钩子） | `Native/zplugin/zplugin.h` + `zplugin.cpp` + `zrender.def` | 不存在 |
| C# P/Invoke | `ZRender/Bindings/ZrNative.cs:12-43` | 不存在 |
| Core 契约 | `ZFrame/Core/Upscaler/`（只有 `IUpscalerSettings.cs`） | 不存在（需新接口，否则 View 无法引用） |
| Render 实现 + 自注册 | `ZRender/Runtime/ZrUpscalerSettings.cs:184-194` 同模式 | 不存在 |
| FG 运行时 dll 部署进 Unity | `UnityProject/Assets/Plugins/ZRender/` 只有 `zrender.dll` | `amd_fidelityfx_framegeneration_dx12.dll` 只被 CMake 拷到演示 exe 旁（`Native/CMakeLists.txt:419-424`） |
| Unity 呈现接管 | FG 需包装 `IDXGISwapChain4`，所有 Present 走包装链（`fg_ffx.h:20-33`） | Unity 的 Present 归引擎所有，本框架无钩子；**【待确认】需评估 Unity 原生插件 + 自建 swapchain 的可行性** |
| 每帧输入 | 深度 + 运动矢量 + 相机位姿（`FrameGenPrepareArgs`，`fg_ffx.h:127-150`） | 当前渲染走 URP，框架未接管 MV/深度；**ZRenderPipeline 仍是空壳**（`ZRender/Runtime/ZRenderPipeline.cs:19-25` 只 Submit） |

**结论**：`帧生成 开/关` 这一行在本任务里只能做成
（a）"存储型占位 + 如实说明"（有先例：`gameplay.*`、`accessibility.colorBlindMode`），或
（b）"能力恒为不可用 → 不可交互/标注不支持"。
**绝不能做成能拨动却什么都不发生的开关**——那就是假开关。

---

## ④ 设置系统加一行的完整步骤

### 4.1 组织方式（事实）
- **数据驱动，数据在代码里**：`SettingsRegistry.BuildDefaults()`（`SettingsRegistry.cs:94-104`）
  直接 `new SettingsPage(...)` 五个（视频/音频/操作/游戏性/无障碍）。
  无 SO 资产、无 CSV、无 Inspector 输入。类注释明说：
  "ADDING AN OPTION TO THE UI MEANS ADDING ONE `SettingDefinition` LINE HERE"（`:11-16`）。
- 行控件在打开时由 `SettingsRowFactory.CreateRow` 生成（`SettingsPanelView.cs:179-188`），
  布局是 `VerticalLayoutGroup`，**加行不改布局**。
- 页容器/行容器尺寸来自 prefab：`RowHost` 固定 `1400×560`（`ZFrame/View/Editor/SettingsUiSetup.cs:184-189`），
  **没有 ScrollRect**。

### 4.2 现有行的类型与回调注册方式
| 类型 | 构造 | 控件 | 值 | 持久化 | apply 时机 |
|---|---|---|---|---|---|
| `Toggle` | `SettingDefinition.Toggle(id, 名, 默认bool, apply, requiresRestart)`（`:122-130`） | `UnityEngine.UI.Toggle`（工厂 `:175-208`） | bool | 是 | 拨动即 `Set` → `Apply` |
| `Slider` | `Slider(id, 名, 默认, min, max, apply)`（`:133-152`） | `Slider`（`:210-275`） | float（clamp） | 是 | 拖动即刻 |
| `Enum` | `EnumSetting(id, 名, 默认索引, 选项[]， apply, requiresRestart)`（`:155-174`） | `Dropdown`（`:277-348`） | int 索引 | 是 | 选中即刻 |
| `Action` | `Action(id, 名, onTrigger)`（`:177-183`） | `Button`（`:350-375`） | 无 | 否 | 点击直接跑回调 |
| `Binding` / `BindingReset` / `Note` | `:199-231` | 键位按钮 / 宽按钮 / 只读文本 | 无 | 否 | 行内自行处理 |

- 回调注册：`SettingDefinition.Apply`（`Action<object>`，`:63`）→
  `SettingsModel.ApplyValue` 在 `_values` 写入后 `definition.Apply?.Invoke(value)`（`SettingsModel.cs:284-294`）。
  UI 侧 `onValueChanged` → `SettingsPanelView.OnRowValueChanged`（`:252-260`）→ `SettingChanged` →
  `SettingsPanelPresenter.OnSettingChanged`（`:73-95`，按类型 `_model.Set(...)`）。
- 能力门控的**唯一现成做法**见 0-1：拼选项文案，不改 interactable。

### 4.3 逐步骤：新增「动态分辨率 开/关」+「帧生成 开/关」

#### 步骤 1（新建 1 个文件，可选但建议）`UnityProject/Assets/ZFrame/View/Settings/FrameGovernorSettingOptions.cs`
照 `UpscalerSettingOptions.cs` 的职责边界写（选项/映射/门控/apply 单一数据源）：
- 常量：`DrsSettingId = "video.dynamicResolution"`、`FgSettingId = "video.frameGeneration"`、
  `UnsupportedSuffix = "（本机不支持）"`（可复用 `UpscalerSettingOptions.UnsupportedSuffix:46`）。
- `ApplyDrs(bool on)`：`if (!Application.isPlaying) return;` → `FrameGovernorHost.Instance?.SetEnabled(on);`
  （**必须空安全**：`Instance` 在 boot 前为 null，EditMode 测试也为 null）。
- `ApplyFg(bool on)`：当前无后端 → 只 `ZLog.Warn("Settings", "[帧生成] 原生 C ABI 未导出…")`，返回。
- `IsFgSupported()`：现在恒 `false`（等 B 轨 C ABI 落地后改成查服务 caps）。

> 同一程序集 `ZFrame.View`，**不需要**改 asmdef / 校验器矩阵。

#### 步骤 2（改 1 处）`UnityProject/Assets/ZFrame/View/Settings/SettingsRegistry.cs`
在 `BuildVideoPage`（`:109-172`）里，**紧跟 `video.upscaler` 块（`:158-162`）之后、`video.shadowDistance`（`:163`）之前**插入两行：

```csharp
// 动态分辨率 — LIVE：直连 FrameGovernorHost（A-R8-01 的"设置界面后续接"）。
SettingDefinition.Toggle(
    "video.dynamicResolution", "动态分辨率", false,
    apply: value => FrameGovernorSettingOptions.ApplyDrs((bool)value)),
// 帧生成 — 占位：原生 C ABI 未导出（B-R8-02 明确"本模块无 C 导出"）。
SettingDefinition.Toggle(
    "video.frameGeneration", "帧生成" + (supported ? "" : UnsupportedSuffix), false,
    apply: value => FrameGovernorSettingOptions.ApplyFg((bool)value)),
```
默认值取 `false`：与 `FrameGovernor.Enabled` 默认关一致（`FrameGovernor.cs:11-12,40`），
且不改变玩家现状（现有 governor 从不自动开启）。

#### 步骤 3（可选，只有真要"灰显"才做）灰显能力门控
现状工厂**没有** interactable 支持。最小改法（3 处）：
1. `SettingDefinition.cs`：加 `public Func<bool> IsAvailable { get; }`（`:32` 类内）、
   私有 ctor 加参数（`:84-95`）、`Toggle`/`EnumSetting` 工厂加可选参数（`:122-130` / `:155-174`）。
2. `SettingsRowFactory.cs`：`BuildToggleRow`（`:175-208`）末尾 `toggle.interactable = definition.IsAvailable?.Invoke() ?? true;`
   （Enum 同理加在 `:347` 之后）。
3. 行工厂测试补一条 "IsAvailable=false → interactable=false"（`ZFrame/Tests/Debug/SettingsRowFactoryTests.cs`，现有 9 例风格见 `:44-76`）。
> 注意：这会让 `SettingDefinition` 的公共形状变化（8 个工厂方法签名都受影响），
> 属于跨页改动，建议单独一个切片而不是塞进本任务。

#### 步骤 4（必改，否则测试红）见 ⑤。

#### 步骤 5（必做，否则"开机不生效"）持久化的**生效时机**
- 立即生效：`SettingsModel.Set` → `ApplyValue` → `Apply`（`SettingsModel.cs:88-93, 284-294`）。
  Presenter 的 `OnSettingChanged` 已经在拨动时调 `Set`（`:73-95`），所以**面板内**是即时的。
- 落盘：只有「应用并关闭」调 `Save()`（`SettingsPanelPresenter.cs:97-102`）；Esc = 回滚不写（`:116-121`）。
- 回放：`Load()` 会**逐条重放 Apply**（`SettingsModel.cs:252-265`），但**只在打开面板时调用**：
  - `SettingsEntry.OpenSettings` → `_sharedModel.Load()`（`SettingsEntry.cs:97-98`）
  - `GameBootstrap.OpenSettings` → `_settingsModel.Load()`（`GameBootstrap.cs:196-197`）
  - **boot 阶段没有任何 Load**（`GameBootstrap` 只在打开面板时构造 model，`:196`）。
  ⇒ 玩家上次开了 DRS 并保存，**下次启动到打开设置面板之前，governor 仍是关的**。
  要让"存了就一直生效"，必须新增一个 boot 期应用点（二选一）：
  a. 在 `FrameGovernorHost.AutoBoot`（`:85-96`）之后读一次设置（需一个不依赖 UI 的读盘入口，`SettingsModel` 已可用：`new SettingsModel(SettingsRegistry.BuildDefaults()); Load();`）；
  b. 新建 `ZFrame/View/Settings/SettingsBootApplier.cs`（`[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]`），只对"必须开机生效"的 id 做 apply。
  > 方案 b 更干净，但会新增 1 个文件（仍属 ZFrame.View，不动矩阵）。

#### 步骤 6（FG 真生效的前置，属 B 轨，不属本任务）
见 ③-3.4 的六项缺口。

### 4.4 持久化细节（加行后自动获得）
- 键 = `SettingDefinition.Id`，值按类型写 bool/float/int（`SettingsModel.cs:157-163`）；
  Toggle 只认 JSON `Boolean`（`:344-351`），类型不符静默丢弃。
- 文件：`Application.persistentDataPath/settings.json`（`:48-50`），版本 1（`:30`），
  `.tmp` + `File.Replace` 原子写（`:185-200`）。
- 未知 id / 高版本文件全部容忍跳过（`:239-244, 254-258`）→ 旧档升级不会炸。
- 没有 per-setting 迁移逻辑；新键在旧文件里缺失 = 用默认值。

---

## ⑤ 测试与校验器的连带影响

### 5.1 会被"加行"直接打红的现成断言（必改）
| 文件 | 行 | 断言 | 加 2 行后 |
|---|---|---|---|
| `ZFrame/Tests/Debug/SettingsRegistryTests.cs` | `:78-97` | `VideoPage_HasSixDefinitionsWithExpectedTypes`：类型数组 6 项 + id 数组 6 项 | 必须改成 8 项（含新 id），否则必红 |
| `ZFrame/Tests/Debug/SettingsPanelSmokeTests.cs` | `:100` | `Assert.AreEqual(6, view.RowCount)` | → 8 |
| `ZFrame/Tests/Debug/SettingsPanelSmokeTests.cs` | `:123` | `Assert.AreEqual(6, view.RowCount)`（切回 video） | → 8 |
| `ZFrame/Tests/Debug/SettingsPanelSmokeTests.cs` | `:128-142` | "加一行"演示：`BuildDefaults().Pages[0]` + 1 行，断言 `RowCount == 7`（`:142`） | → 9 |

> `SettingsModelTests.cs`（22 例）用自建 3 行 registry，与页形状无关，不受影响。
> `SettingsRegistryTests` 其余 13 例按页/按 id 断言，不受影响（音频 4 / 操作 8 / 游戏性 3 / 无障碍 3 均未变）。
> 无障碍/音频/操作页行数断言分别在 `:156, :178, :247`——本次不动。

### 5.2 测试模式
- 全部 **EditMode NUnit**，`ZFrame.Debug.Tests` / `ZFrame.Pipeline.Tests` 两个 asmdef，
  `includePlatforms: ["Editor"]`、`defineConstraints: ["UNITY_INCLUDE_TESTS"]`、`overrideReferences: true`。
- 设置系统：数据驱动 Smoke（真 prefab `ui_settings_panel` + 真 `UIManager` + 临时目录 model，
  `SettingsPanelSmokeTests.cs:14-22, 32-54`）；行工厂用真 UGUI 组件断言（`SettingsRowFactoryTests.cs`）。
- 服务缺席路径的测试样板：`UpscalerSettingsTests.cs:16-99`（`StubUpscalerSettings` + `Services.Register` / `Services.Clear`），
  `:122-175` 演示"ServicePresentButNativeUnavailable"与 apply 透传。
  → 新行的测试**照这个写**：注册 `FrameGovernorHost` 不可用（EditMode `Instance==null`）时 apply 不抛。

### 5.3 FrameGovernor 已有测试覆盖到哪一层
| 文件 | 例数 | 覆盖 |
|---|---|---|
| `ZFrame/Tests/Pipeline/FrameGovernorTests.cs` | 20 | 纯核心：迟滞边界、死区、N>M、钳制、dt 上限、旁路、GPU/CPU 回退 |
| `ZFrame/Tests/Pipeline/FrameGovernorHostTests.cs` | 11 | 适配层：`ITickQueue` 注册/注销（`:140-160`）、默认禁用（`:162-171`）、禁用全旁路（`:174`）、走塔顺序+事件（`:193`）、首级落 renderScale（`:227`）、Shutdown 恢复（`:241`）、关→恢复（`:265`）、重开捕获新基线（`:278`）、无启用 Shutdown（`:293`）、TargetFps 换算（`:308`）、Recorder 冒烟（`:323`）|
- **没有**的一层：**"设置行 → FrameGovernorHost"** 的集成用例（因为行不存在）；
  也**没有** PlayMode 用例验证真实降分辨率观感。
- 新行必须补的用例（建议放 `ZFrame/Tests/Debug/FrameGovernorSettingsTests.cs`）：
  1) registry 里新 id 存在、类型 Toggle、默认 false、`Apply != null`；
  2) `Apply` 在 `FrameGovernorHost.Instance == null` 时不抛；
  3) 桩化后 apply 透传布尔（可给 Host 加 internal 测试缝，或在 `Services` 上做 `IFrameGovernorSettings` 抽象——**若走抽象则需新增 Core 接口**，见风险 R1）；
  4) FG 行：`EnumOptions`/`DisplayName` 带"（本机不支持）"或 toggle 不可交互（二选一，与 ③ 结论对齐）。

### 5.4 架构校验器的硬要求
- `ArchitectureValidator.ExpectedMatrix`（`ZFrame/Editor/ArchitectureValidator.cs:31-81`）：
  每个**非测试** asmdef 必须有条目，且 `references` 必须是白名单子集（`:158-178`）。
  未登记 → `"[asmdef] … 不在期望矩阵中（新增程序集需先更新 ArchitectureValidator 期望矩阵）"`（`:160`）。
- `ArchitectureValidatorTests.cs:19-35`：断言 `ExpectedMatrixCount == 全部 asmdef - 测试 asmdef`
  —— **新增/删除程序集必须同步改矩阵，否则红**。
- `ZFrame.View` 的白名单 = `Core/Data/Gameplay/Unity.InputSystem`（`:44-49`）；
  URP 两个程序集是 **GUID 引用**，被 `IgnoredReferencePrefixes` 豁免（`:111-114`），
  所以 `ZFrame.View.asmdef:4` 里那两个 GUID 合法。
- 纯度扫描目录只有 `Assets/ZFrame/Gameplay` 与 `Assets/Game/domain`（`:84-88`）——**Settings 不在扫描范围**。
- **本任务只要新增 `.cs` 到 `ZFrame.View`，矩阵/校验器零改动**。若有人想新建
  `ZFrame.View.Settings` 之类程序集，就必须加矩阵条目并改矩阵计数测试。

### 5.5 文件规范（施工时）
`Docs/Standards/file-conventions.md` + `AGENTS.md`：UTF-8 **无 BOM**、**LF**；
不手改 Unity 自动生成的 `.meta`（新 `.cs` 的 `.meta` 由编辑器导入时生成）。

---

## ⑥ 风险清单（每条都给"会导致什么现象"）

| # | 风险 | 现象 | 依据 |
|---|---|---|---|
| R1 | **FG 没有后端**：C ABI 不存在，设置行只能存值 | 开关能拨、settings.json 有值、**画面零变化**，玩家以为坏了 | `fg_ffx.h:35-36`；`zrender.def:10-18`；C# 侧 0 命中 |
| R2 | **开机不生效**：`Load()` 只在打开设置面板时调用 | 上次开了 DRS，重启进游戏**仍是关的**，直到第一次打开设置 | `SettingsEntry.cs:98`；`GameBootstrap.cs:197`；boot 路径无 Load |
| R3 | **`FrameGovernorHost.Instance` 可能为 null** | 从设置行调用时 NRE（EditMode 测试、或 boot 极早期/已 Shutdown） | `:67`（静态）、`:85-96`（仅 Play 自举）、`:221-224`（Shutdown 置 null） |
| R4 | **与「画质档位」互相打架** | 治理中玩家改档位：governor 下次 apply 用**旧** `_userTier` 把档位拉回；玩家看到档位自己跳回去 | `FrameGovernorHost.cs:30-35, 263, 334-345`；`PipelineManager.cs:106-153` |
| R5 | **与「帧率上限」不联动** | 上限设 30fps，governor target 仍 60 → 帧时间 33ms 被判超预算 → **无谓降分辨率**；上限 120 时 governor 从不动作 | `SetTargetFps` 独立（`:176-183`）；`video.frameRateCap` 只写 `Application.targetFrameRate`（`SettingsRegistry.cs:140-150`）；`DefaultTargetFps=60`（`:50`） |
| R6 | **renderScale 写在 URP **资产**上（不是运行时副本）** | 编辑器里跑完 Play 后 `URP_*.asset` 的 `m_RenderScale` 被改脏；中途崩溃/强杀则永久停在 0.75，下次启动画面糊 | `ApplyRenderScale :362-375` 直接写 `urp.renderScale`；资产现值 `m_RenderScale: 1`（`Assets/Settings/Pipeline/URP_{Low,Medium,High}.asset:29`） |
| R7 | **非 URP 管线时 DRS 静默失效** | 切到 ZRender 管线后开关仍然可拨，但只降档/降后处理，**分辨率不变**，日志只有一条 WARN | `ApplyRenderScale :377-381`；`RenderPipelineSelector` 支持双管线；`ZRenderPipeline` 是空壳（`ZRenderPipeline.cs:19-25`） |
| R8 | **FG 的交换链接管与 Unity 呈现冲突** | 就算补了 C ABI，直接开 FG 可能黑屏/卡死/present 时序错乱，需重建 swapchain 并自管 Present | `fg_ffx.h:20-33`（包装 IDXGISwapChain4） |
| R9 | **治理态被"重放"改回**：每次打开面板 `Load()` 都会重放全部 Apply | 玩家在别处手动开的治理，打开设置面板后被保存值覆盖关掉；反之亦然 | `SettingsModel.cs:252-265`；`SettingsEntry.cs:98` 每次打开都 Load |
| R10 | **`SetEnabled(true)` 幂等但不重捕基线** | 档位/分辨率基线变了以后再拨一次"开"，governor 仍按旧基线做减法 → 可能把玩家新选的档位压回去 | `:160-163`（同值直接 return）；`:256-283`（基线只在真启用时捕获） |
| R11 | **恢复语义会写 PlayerPrefs** | 关闭 DRS 时 `RestoreBaseline` → `SetTier(用户档)`，把档位重新持久化；若 governor 基线已陈旧，玩家会看到档位被"还原"成旧值 | `RestoreBaseline :301-307`；`PipelineManager.cs:142-144` |
| R12 | **测试硬编码行数** | 改完功能但没改断言 → 一红一大片，容易被误判"功能坏了" | `SettingsRegistryTests.cs:78-97`；`SettingsPanelSmokeTests.cs:100,123,142` |
| R13 | **面板没有滚动区，RowHost 固定 560 高** | 视频页 6→8 行仍能放下（8×48+7×8+32≈472）；若再加到 10 行以上会顶出可视区/压住底部按钮 | `SettingsUiSetup.cs:184-189`；`VerticalLayoutGroup` `SettingsRowFactory.cs:63-82` |
| R14 | **超分是"标签后缀"不是灰显** | 照"灰显"实施会去改 `SettingDefinition`/`SettingsRowFactory` 公共形状，牵连 8 个工厂方法与其他页测试 | `UpscalerSettingOptions.cs:80-91,113-116`；`SettingsRowFactory.cs:175-208`（无 interactable） |
| R15 | **`requiresRestart` 无效** | 若 FG 做成"需重启"，写 `requiresRestart: true` 也**不会**有任何 UI 提示 | `SettingDefinition.cs:65-66,116`（全项目零消费） |
| R16 | **Telemetry 回退到 CPU 时治理依据错位** | 无 "GPU Frame Time" 计数器的平台按 CPU 帧耗时治理；CPU 瓶颈场景会**持续降分辨率也救不回帧率**（还白白糊画面） | `RecorderFrameTelemetry.cs:73-76, 84-91`；`FrameGovernor.cs:88-91` |
| R17 | **目标帧率只影响 TargetMs，不影响 Application.targetFrameRate** | 设 `SetTargetFps(30)` 后引擎仍跑满，governor 会按 30fps 预算不停降级直到放弃档 | `FrameGovernorHost.cs:176-183` vs `SettingsRegistry.cs:147-149` |
| R18 | **Load 顺序依赖 JSON 键序** | 若新行排到 `video.qualityTier` 之前，重放时 governor 先按旧档位捕获基线、随后档位才变 → 基线错位（表现为恢复时回错档） | `SettingsModel.cs:252-265`（按 JSON 顺序）；建议新行插在 `qualityTier` **之后** |

### 最大风险（单点）
> **R1 + R2 的组合**：FG 行必然"假开关"（无 C ABI，见 ③），而 DRS 即使接上了，
> 因为 `Load()` 只在打开设置面板时发生（R2），**开机后并不会生效**。
> 两者叠加会让整条验收看起来"开关都做了，但游戏里什么都没变"——
> 建议简报把"开机应用点"和"FG 行的诚实标注（不可用/仅存储）"写成显式验收项，
> 否则这一任务很容易交付成"UI 有了、功能没感觉"。

---

## 附：一页清单（施工要动的文件）
**必动（3 个）**
1. `UnityProject/Assets/ZFrame/View/Settings/SettingsRegistry.cs`（`BuildVideoPage` 插两行定义）
2. `UnityProject/Assets/ZFrame/Tests/Debug/SettingsRegistryTests.cs`（`:78-97` 视频页形状）
3. `UnityProject/Assets/ZFrame/Tests/Debug/SettingsPanelSmokeTests.cs`（`:100,:123,:142` 行数）

**建议新增（1-2 个）**
4. `UnityProject/Assets/ZFrame/View/Settings/FrameGovernorSettingOptions.cs`（映射/门控/apply 单一数据源）
5. `UnityProject/Assets/ZFrame/Tests/Debug/FrameGovernorSettingsTests.cs`（新行语义 + 无宿主的空安全）
6. （若要"开机生效"）`UnityProject/Assets/ZFrame/View/Settings/SettingsBootApplier.cs`

**明确不需要动**
- `SettingsRowFactory.cs` / `SettingsPanelView.cs` / `SettingsPanelPresenter.cs` / `SettingsModel.cs` / prefab / 任何 `.asmdef` / `ArchitectureValidator.cs`
- 前提：只用现有 Toggle 类型 + 标签后缀门控（步骤 3 的灰显改造不做）
