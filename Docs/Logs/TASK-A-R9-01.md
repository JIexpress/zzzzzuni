# TASK-A-R9-01 —— 视频页接 FrameGovernor：动态分辨率 + 帧率目标（并修"设置了不生效"）

- **轨道**：A / R9（愿望清单 #2 的接线部分）
- **状态**：doing
- **派发**：主控，2026-10-28
- **侦察笔记（施工必读）**：`Docs/Logs/TASK-A-R9-01-recon.md`（360 行，全部结论带文件路径+行号）

## 任务简报（主控写）

### 0. 范围裁决（主控已定，不要自行扩大或缩小）

差异附录 A.2 挂账的两项是「动态分辨率开关」与「帧率目标行」——FrameGovernor（A-R8-01）与 FG（B-R8-02）都已建成，但设置界面没接。

**本次只做两件事**：

1. 视频页新增 **动态分辨率 开/关**（接 FrameGovernor 的 DRS 使能）
2. 视频页新增 **帧率目标**（Enum，接 FrameGovernor 的目标帧时）
3. 并修掉幕后根因：**设置的生效时机**——侦察确认 `SettingsModel.Load()` 只在"打开设置面板"时被调用，冷启动不加载（`SettingsModel.cs:221-269`）。若不修，两行开关做出来也是摆设。

**明确不做**：

- **不做 FG 开关**。侦察证据：`Native/zupscaler/fg_ffx.h:35-36` 明写"本模块无 C 导出"，`zrender.def` 仅 9 个符号不含 FG，`UnityProject/Assets/Plugins` 里无 FG dll——Unity 侧**没有任何合法途径启用 FG**。
  主控裁决：**禁止**为凑数量加一个"能点但无效"的开关或一个永远灰的死行；FG 的 Unity 接线另立 **B-R9-01**（原生加 C ABI + dll 部署 + 绑定），不在本任务内。
- 不改 FrameGovernor 的核心算法（A-R8-01 已验收）；若确实必须改动其内部逻辑，**先停下回主控**。
- 不动其它四个页签、不动超分行的既有行为。

### 1. 已知锚点（来自侦察，仍需自行复核）

| 位置 | 作用 |
|---|---|
| `View/Pipeline/FrameGovernorHost.cs:85-96` | 自举；`:141` 挂 View 阶段；`:153` `SetEnabled`；`:362` 写 URP `renderScale` |
| `View/Settings/SettingsRegistry.cs:109-172` | 视频页是**唯一数据源**；超分行在 `:158-162` |
| `View/Settings/UpscalerSettingOptions.cs:80-91` | 现有"能力门控"的实现方式是**标签后缀**（不是灰显） |
| `View/Settings/SettingsModel.cs:221-269` | `Load()` 只被"打开面板"调用——本任务要修的根因 |
| `Tests/.../SettingsRegistryTests.cs:78-97` | 行数/定义的既有断言 |
| `Tests/.../SettingsPanelSmokeTests.cs:100/123/142` | 硬编码了 6/6/7 行——新增两行后**必须同步更新**（这是预期改动，不算放宽） |

### 2. 硬约束

- **零破坏默认值**：未改过设置时，行为必须与当前完全一致（DRS 默认关、帧率目标默认"不限"）。判据见 §4-5。
- **不许出现"能点但不生效"的控件**：每一行的值必须真的到达 `FrameGovernorHost`；若某条路径下确实无法生效（例如 ZRender 管线 vs URP 管线对 `renderScale` 的支持差异），
  按现有 `UpscalerSettingOptions.cs:80-91` 的**标签后缀**风格诚实标注，并在施工报告里写清"哪条路径生效、哪条不生效、为什么"。
- **持久化**：走既有 settings.json 通道（键名风格与现有一致），不得另起存储。
- **分层与校验**：新增文件必须落在 View 层既有 asmdef 内；架构校验器（arch 段）必须 exit 0。**不新建程序集**，否则要同步 asmdef 矩阵登记。
- **测试要真断言**：新增用例必须能证伪（例如"改了目标帧率后 FrameGovernor 的目标帧时确实变了"、"冷启动后值来自 settings.json 而非默认值"），不许只断言"不为 null"。
- 不装软件、不联网、**不 git commit**。
- 文件规范：`.cs` UTF-8 无 BOM，与既有文件换行一致。

### 3. 环境纪律（本机实测）

- 跑 Unity **只能**通过 `Tools/run_tests.ps1`（内部已内建单实例/删锁/无 `-quit`/走 Git Bash/轮询 results.xml 五条纪律）。**不要**自己裸调 `Unity.exe -batchmode`。
- 跑之前确认无残留 `Unity.exe`；跑测试用 Bash 工具 + `unset http_proxy https_proxy all_proxy`。
- 全量六段约 5–6 分钟。**只跑相关段即可迭代**：`-Segments editmode`（改 C# 时最快）。
- 收尾时**必须跑一次完整六段**（不能只跑 editmode 就宣称完成）。
- 禁止 `rm`；清理用 `mv`。

### 4. 自测清单（施工必做，报告逐条贴证据）

1. **六段全量**：`Tools/run_tests.ps1` → 六段全绿 exit 0；native 仍 190/710,800；editmode 用例数 **≥651 且等于 651+新增数**（写明新增了几条）。
2. **两行真的渲染出来**：数据驱动 Smoke 测试须覆盖新行（行数从 6/6/7 更新为含新行的真实值），并贴出断言证据。
3. **持久化 roundtrip**：改值 → 落盘 settings.json → 重新 Load → 值保持（贴 JSON 片段）。
4. **开机生效（本任务核心）**：模拟冷启动（不经"打开设置面板"的路径）→ FrameGovernor 的使能/目标帧时**已等于 settings.json 里的值**。
   必须给出可复跑的证据（EditMode 用例名 + 断言，或启动日志里打印生效值）。**只写到"Load 被调用"不算通过**，要看到值抵达 FrameGovernor。
5. **零破坏对照**：settings.json 不存在（全新用户）时 → DRS 关、帧率不限，六段全绿，native/editmode/playmode 数字与基线一致（editmode 只多出新增用例）。
6. **诚实边界**：写明 URP 管线与 ZRender 管线各自哪些生效；FG 为何缺席（引用 fg_ffx.h 与 zrender.def 证据）。
7. `git status --short` + `git diff --stat`：改动清单完整列出（本任务允许改的目录：`UnityProject/Assets/ZFrame/View/**`、对应 `Tests/**`、本日志；若动了 `ZRender/**` 必须在报告里解释必要性）。

### 5. 交付物

- 代码改动 + 测试改动 + 本日志「施工报告」（含 7 条证据原文、数据流说明"设置值 → FrameGovernor"的完整链路、诚实边界）。
- 不动 HANDOFF/TASKS/WORKLOG/差异附录（主控闭环时刷新）。

---

## 施工报告（施工代理写）

**结论**：两行已接上 FrameGovernor（值真的抵达 `FrameGovernorHost`/`FrameGovernor.TargetMs`），
冷启动不生效的根因已修（新增开机应用点，走既有 `SettingsModel.Load()` 通道）；六段全绿 exit 0。
范围外零改动：`FrameGovernor.cs` / `FrameGovernorHost.cs` / 其它四页签 / 超分行一行业务逻辑都没动（见证据 7 的 diff）。
**主控裁决已执行**：裁决 1 —— 不做"不限"，保留 30/60/120、默认 60；裁决 2 —— 帧率目标行加诚实后缀
「（DRS不锁帧）」、与既有「帧率上限」语义互斥、补一条"DRS 关时只存值、无任何运行行为"的证伪用例。
两条最终文案：**`动态分辨率`**（非 URP 时 `动态分辨率（仅降档）`）、**`帧率目标（DRS不锁帧）`**。

### 1. 「设置值 → FrameGovernor」完整数据流

**A. 面板内即时生效**
1. `SettingsRegistry.BuildVideoPage()`（`SettingsRegistry.cs:109-192`）用两条 `SettingDefinition` 定义行
   （`video.dynamicResolution` Toggle / `video.frameRateTarget` Enum）——这是唯一数据源，无 prefab/SO 手摆。
2. 打开面板时 `SettingsRowFactory.CreateRow` 生成真控件：Toggle（`:175-208`）/ Dropdown（`:277-348`）。
3. 用户拨动 → `SettingsPanelView.OnRowValueChanged` → `SettingsPanelPresenter.OnSettingChanged`
   → `SettingsModel.Set<T>`（`:88-93`）→ `ValidateAndNormalize` → `ApplyValue`（`:284-294`）
   → `definition.Apply(value)`（**即时应用**，不等"应用"按钮）。
4. `Apply` 进 `FrameGovernorSettingOptions.ApplyDrs(bool)` / `ApplyTargetFps(int)`
   → `FrameGovernorHost.Instance.SetEnabled(bool)`（`FrameGovernorHost.cs:153-173`）/
   `SetTargetFps(float)`（`:176-183`）。
5. Host 内部：`SetEnabled(true)` → `EnableGovernor()`（`:256-283`）捕获用户基线（tier / renderScale / grade 权重）
   并建 `FrameGovernor { Enabled = true, TargetMs = 1000/_targetFps }`；`SetTargetFps` 在治理中直接改写
   `_governor.TargetMs`（`:179-182`）。
6. 每帧：`ITickQueue`（TickStage.View）→ `FrameGovernorHost.Tick`（`:228-254`）→ `FrameGovernor.Tick`
   用 `TargetMs * (1 ± 迟滞)` 判定 → `ApplyState`（`:331-360`）落地：
   `RenderScale`→`UniversalRenderPipelineAsset.renderScale`（`:362-382`）、`Shadows`→`PipelineManager.SetTier`（只降不升）、
   `PostFx`→grade volume weight。

**B. 落盘**「应用并关闭」→ `SettingsPanelPresenter.OnApplyRequested` → `SettingsModel.Save()`（`:142-171`）
→ 键即 `SettingDefinition.Id`（`video.dynamicResolution` bool / `video.frameRateTarget` int），
`.tmp` + `File.Replace` 原子写；Esc 撤销不写。

**C. 冷启动（本任务修的根因）**
1. `FrameGovernorHost.AutoBoot`（`[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`，`:85-96`）先建宿主 →
   `Instance` 可用。
2. `SettingsBootApplier.AutoBoot`（**新增**，`[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]`）
   → `new SettingsModel(SettingsRegistry.BuildDefaults())` → `SettingsModel.Load()`（`:221-269`）
   —— **与面板完全同一条代码路径**，按 settings.json 的键序逐条重放 `Apply`，因此同样走到上面 A-4/A-5/A-6。
3. 因为两条新行在注册表里排在 `video.qualityTier` **之后**（`:166-182`），`Load` 重放时档位先落定，
   governor 才捕获基线（侦察 R18 的顺序依赖）。
4. 无 settings.json → `Load()` 返回 0，一个 `Apply` 都不触发 → 引擎状态与改动前逐字段一致（零破坏）。

> 关键点：`FrameGovernor.TargetMs` 就是迟滞判定的预算（`FrameGovernor.cs:101-102`），
> 所以用例直接断言 `host.Governor.TargetMs`，而不是"Load 被调用过"。

### 2. 七条自测证据

#### 证据 1 —— 六段全量全绿（exit 0）

最后一次收尾全量（`TestReport.txt`：`Generated: 2026-10-28 12:46:58 (wall 0:02:23)`，含主控裁决 2 的全部改动）：

```
native   PASS   cases=190 asserts=710800 failed=0; ctest: 100% tests passed (1 entry: zrender_test) 00:05
editmode PASS   total=663 passed=663 failed=0 skipped=0 2.77693s(xml)                               00:35
playmode PASS   total=6 passed=6 failed=0 skipped=0 4.7948172s(xml)                                 00:40
arch     PASS   exit=0; report: TempLogs/arch_report.txt                                            00:20
config   PASS   exit=0; report: TempLogs/config_report.txt                                          00:20
naming   PASS   exit=0; report: TempLogs/name_report.txt                                            00:20
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)
```
- native 与基线一致：**190 用例 / 710,800 断言**（未动 `Native/**`）。
- editmode：**663 = 651 + 12**（新增 12 条，全部在 `Tests/Pipeline/FrameGovernorSettingsTests.cs`：
  首批 9 条 + 主控裁决 2 补的 3 条）。
- arch 报告原文：`asmdef 扫描数: 24（期望矩阵条目: 11，Tests 程序集豁免）… 违规数: 0 结果: PASS`
  —— **没有新建程序集**，三个新 `.cs` 都落在既有 `ZFrame.View` / `ZFrame.Pipeline.Tests` asmdef 内。

新增的 12 条（用例名即证据）：
`VideoPage_HasDynamicResolutionToggle_DefaultsOff_WiredLive`、
`VideoPage_HasTargetFpsEnum_MapsTo30_60_120`、
`FrameRateCapAndFrameRateTarget_AreSemanticallyDistinct`（裁决 2-②）、
`GovernorRowLabels_FitTheLabelColumn_WithoutTruncation`（裁决 2-①，防标签被截断）、
`ApplyDrs_ReachesGovernorHost_EnableState`、
`ApplyTargetFps_UpdatesGovernorTargetMs_LiveWhileEnabled`、
**`TargetFps_WhileDrsOff_StoresValueOnly_WithNoRuntimeEffect`**（裁决 2-③）、
`Apply_WithoutBootstrapHost_StillStoresTheValue`、
`ColdStart_AppliesPersistedGovernorSettings_WithoutOpeningThePanel`、
`ColdStart_WithoutSettingsFile_LeavesGovernorDefaultsUntouched`、
`SaveThenLoad_RoundTripsGovernorRows`、
`DrsLabel_IsHonest_WhenRenderScaleCannotLand`。

#### 证据 2 —— 两行真的渲染出来（数据驱动 Smoke）

`SettingsPanelSmokeTests.Push_GeneratesTabBarAndVideoRows_FromRegistry` 的行数硬编码按预期 6→8，并新增了对**真控件**的断言：

```csharp
Assert.AreEqual(8, view.RowCount, "video page row count comes straight from the registry definitions");
...
Toggle drs = container.Find("Row_video.dynamicResolution/Toggle").GetComponent<Toggle>();
Assert.IsNotNull(drs, "动态分辨率 must render as a real Toggle");
Assert.IsFalse(drs.isOn, "默认关（零破坏：与 governor 默认 Enabled=false 一致）");
Dropdown target = container.Find("Row_video.frameRateTarget/Dropdown").GetComponent<Dropdown>();
CollectionAssert.AreEqual(new[] { "30", "60", "120" }, target.options.Select(o => o.text).ToArray());
Assert.AreEqual(FrameGovernorSettingOptions.DefaultTargetFpsOptionIndex, target.value, "默认 60fps = governor 的 DefaultTargetFps");
```
同步更新的既有断言：`SettingsPanelSmokeTests.cs:137`（切回 video）6→8、`:156`（"加一行不改布局"演示）7→9；
`SettingsRegistryTests.VideoPage_HasEightDefinitionsWithExpectedTypes` 的类型/id 数组 6→8 项。
（这些是简报点名的**预期改动**，不是放宽断言。）

#### 证据 3 —— 持久化 roundtrip（改值 → 落盘 → 重新 Load → 值保持）

用例 `SaveThenLoad_RoundTripsGovernorRows`（`SettingsModel` 注入临时目录，绝不碰真实 `persistentDataPath`）。
测试输出里的真实落盘内容（`TempLogs/runtests_editmode.xml` 的 `<output>`）：

```json
{
  "version": 1,
  "settings": {
    ...
    "video.upscaler": 0,
    "video.dynamicResolution": true,
    "video.frameRateTarget": 2,
    "video.shadowDistance": 2,
    ...
  }
}
```
断言：`StringAssert.Contains("\"video.dynamicResolution\": true", json)`、
`StringAssert.Contains("\"video.frameRateTarget\": 2", json)`；随后新建 model + `Load()`
→ `Get<bool>("video.dynamicResolution") == true`、`Get<int>("video.frameRateTarget") == 2`。
键名风格与既有 `video.*` 一致，未另起存储。

#### 证据 4 —— 开机生效（本任务核心）

用例 `ColdStart_AppliesPersistedGovernorSettings_WithoutOpeningThePanel`：
在临时目录写一份只有两个键的 settings.json（模拟玩家上次存的档）→ 建宿主（默认不治理）→
调 `SettingsBootApplier.ApplyFromDisk(tempRoot)`（**不经任何面板/`SettingsEntry`/`GameBootstrap` 路径**）→ 断言宿主状态：

```csharp
Assert.GreaterOrEqual(applied, 2, "两条 governor 设置都必须被识别并重放");
Assert.IsTrue(_host.IsGovernorEnabled, "冷启动（不经设置面板）后 governor 必须已经开着 …");
Assert.AreEqual(30f, _host.TargetFps, "目标帧率必须来自 settings.json，而不是默认 60");
Assert.AreEqual(1000f / 30f, _host.Governor.TargetMs, 1e-3f, "值必须抵达 governor 内部预算（TargetMs），不是只把 model 读了一遍");
Assert.IsTrue(_host.Governor.Enabled);
```

同一用例打印的实测值（`TempLogs/runtests_editmode.xml` 第 1809 行）：
```
cold start: applied=2, governorEnabled=True, targetFps=30, targetMs=33.33
```
即：开机应用后 `IsGovernorEnabled=True`、`TargetFps=30`、`Governor.TargetMs=33.33ms`（= 1000/30）。

真实启动路径上的对应日志（仅当磁盘上有 settings.json 时打印，值为宿主实测而不是 model 镜像）：
`SettingsBootApplier.DescribeGovernorState()` → `governor enabled={host.IsGovernorEnabled}, target={host.TargetFps}fps, targetMs={...}ms`；
且启动顺序有硬保证：`FrameGovernorHost` 在 **BeforeSceneLoad** 自举，应用点在 **AfterSceneLoad**。

#### 证据 5 —— 零破坏对照（没有 settings.json = 全新用户）

- 用例 `ColdStart_WithoutSettingsFile_LeavesGovernorDefaultsUntouched`：空目录 → `ApplyFromDisk` 返回 **0**
  → `IsGovernorEnabled == false`、`TargetFps == 60`（宿主默认）、`Governor.TargetMs` 与调用前**完全相同**、`Governor.Enabled == false`。
- 用例 `Apply_WithoutBootstrapHost_StillStoresTheValue`：宿主未自举时 `Set` 不抛（只告警），值仍落库 —— 防止 R3 NRE。
- 本机真实存档目录 `%USERPROFILE%/AppData/LocalLow/ZFrame/ZFrame` 下**只有 `saves/`，没有 settings.json**，
  因此本次六段里的 PlayMode（会真实执行开机应用点）就是一次货真价实的"全新用户冷启动"：
  **playmode 6/6 passed**，governor 依然关着，native/editmode 数字与基线口径一致（editmode 仅多出 12 条新增用例）。
- 既有用户（有 settings.json 但缺这两个键）：`Load` 跳过未知键，两行取定义默认值 → 行为同改动前。

#### 证据 6 —— 诚实边界（见 §3 详述）

- URP：`RenderScale` 措施真落地（写 `UniversalRenderPipelineAsset.renderScale`）。
- ZRender 自研管线：`renderScale` 无落点（`FrameGovernorHost.ApplyRenderScale :362-382` 只认 URP，非 URP 只 WARN 一次）；
  此时行标签追加 `（仅降档）`（`DrsLabel_IsHonest_WhenRenderScaleCannotLand` 锁住该文案），
  治理本身仍有降档/降后处理两条措施 —— 不是死开关。
- FG：**本任务不含**，见 §3 的三条硬证据。

#### 证据 7 —— 改动清单

`git status --short`
```
 M UnityProject/Assets/Plugins/ZRender/zrender.dll
 M UnityProject/Assets/ZFrame/Tests/Debug/SettingsPanelSmokeTests.cs
 M UnityProject/Assets/ZFrame/Tests/Debug/SettingsRegistryTests.cs
 M UnityProject/Assets/ZFrame/View/Settings/SettingsRegistry.cs
?? Docs/Logs/TASK-A-R9-01-recon.md
?? Docs/Logs/TASK-A-R9-01.md
?? UnityProject/Assets/ZFrame/Tests/Pipeline/FrameGovernorSettingsTests.cs
?? UnityProject/Assets/ZFrame/Tests/Pipeline/FrameGovernorSettingsTests.cs.meta
?? UnityProject/Assets/ZFrame/View/Settings/FrameGovernorSettingOptions.cs
?? UnityProject/Assets/ZFrame/View/Settings/FrameGovernorSettingOptions.cs.meta
?? UnityProject/Assets/ZFrame/View/Settings/SettingsBootApplier.cs
?? UnityProject/Assets/ZFrame/View/Settings/SettingsBootApplier.cs.meta
```
`git diff --stat`
```
 UnityProject/Assets/Plugins/ZRender/zrender.dll    | Bin 20480 -> 20480 bytes
 .../ZFrame/Tests/Debug/SettingsPanelSmokeTests.cs  |  24 ++++++++++++++++++---
 .../ZFrame/Tests/Debug/SettingsRegistryTests.cs    |  16 ++++++++++++--
 .../ZFrame/View/Settings/SettingsRegistry.cs       |  23 +++++++++++++++++++-
 4 files changed, 57 insertions(+), 6 deletions(-)
```
说明：
- 新增 3 个 `.cs` + 3 个由 Unity 自动生成的 `.meta`（未手改 meta），全部在既有 asmdef 内；
- `SettingsRegistry.cs` 只加了两条定义 + 类注释更新；`FrameGovernor.cs` / `FrameGovernorHost.cs` **零改动**；
- `Assets/Plugins/ZRender/zrender.dll` 的二进制变化是 `Tools/run_tests.ps1` 的 native 段按既有流程
  "build → deploy 到 Unity Plugins" 重写产物（同尺寸 20480B，无源码改动）。是否随本任务提交由主控决定，
  我未做任何回滚（不 git commit / 不用 destructive 命令）。

### 3. 诚实边界

| 管线 | 动态分辨率（DRS） | 帧率目标 |
|---|---|---|
| URP（本工程 QualitySettings 三档各自绑 URP asset） | **全生效**：renderScale 阶梯 0.75~1.0 + 降档 + 降后处理 | **生效**：改的是 `FrameGovernor.TargetMs`，与管线无关 |
| ZRender 自研 SRP（`ZRenderPipeline` 仍是空壳） | **只部分生效**：`renderScale` 无落点（Host 只认 `UniversalRenderPipelineAsset`，非 URP 打一条 WARN），实际只剩"降档 + 降后处理"；行标签如实标注为 `动态分辨率（仅降档）` | **生效**（同上，纯预算参数） |
| 内置管线 | 同 ZRender：只剩降档/降后处理 | 生效 |

- 帧率目标只影响 **governor 的判定预算**，不改 `Application.targetFrameRate`；真正锁帧仍由既有的
  `video.frameRateCap` 行负责（侦察 R5/R17 的既有分工，本任务未改）。
- DRS 与目标帧率**只在 DRS=开时才有画面效果**：DRS=关时目标帧率仍会写进宿主（可观测 `host.TargetFps`），
  只是不驱动任何措施 —— 这是设计上的先后关系，不是"能点不生效"：UI 上已由行标签 `帧率目标（DRS不锁帧）`
  明说生效条件与"不锁帧"（裁决 2-①），行为侧由 `TargetFps_WhileDrsOff_StoresValueOnly_WithNoRuntimeEffect`
  证明 DRSOff 时阶梯/`renderScale` 一动不动，而治理中改值即时生效（`ApplyTargetFps_UpdatesGovernorTargetMs_LiveWhileEnabled`）。
- **FG 为什么缺席**（三条硬证据，另立 B-R9-01）：
  1. `Native/zupscaler/fg_ffx.h:35-36` 原文：`// C ABI 纪律：本模块无 C 导出（窗口验证程序/单测静态链接使用），跨 DLL 的 FG 入口留待 zplugin 任务。`
  2. `Native/zplugin/zrender.def:10-18` 的导出白名单只有 9 个 `ZR_*` 符号（ApiVersion/GetVersion/GetBuildInfo/
     MeshLoadFromMemory/MeshStatsGet/MeshFree/Upscaler_SetPreferred/Upscaler_GetActive/Upscaler_QueryCaps），**无任何 FG 符号**。
  3. `UnityProject/Assets/Plugins/ZRender/` 只有 `zrender.dll`（本次 native 段刚重新部署），
     `amd_fidelityfx_framegeneration_dx12.dll` 只被 CMake 拷到演示 exe 旁。
  ⇒ Unity 侧没有任何合法启用途径，因此**不加"能点无效"的开关，也不加永远灰的死行**（按主控裁决）。

#### 证据 8 —— 裁决 2-③ 的证伪用例：DRS 关时改目标帧率"只存值、无任何运行行为"

用例 `TargetFps_WhileDrsOff_StoresValueOnly_WithNoRuntimeEffect`（`Tests/Pipeline/FrameGovernorSettingsTests.cs`）：

```csharp
NewHost();                                   // DRS 关（默认）
_telemetry.CpuFrameMs = 60f;                 // 16.7fps —— 对任何预算都算过载
model.Set(TargetFpsSettingId, TargetFpsOption30);

Assert.AreEqual(TargetFpsOption30, model.Get<int>(TargetFpsSettingId), "值必须落库");
Assert.AreEqual(30f, _host.TargetFps, "值必须抵达宿主（只是不驱动措施）");
float scaleBefore = ActiveUrp().renderScale;
for (int i = 0; i < 10; i++) _host.Tick(0.5f);                     // 5 秒持续过载

Assert.IsFalse(_host.IsGovernorEnabled, "DRS 关 → 治理器不得被目标帧率顺手打开");
Assert.IsFalse(_host.Governor.Enabled);
Assert.AreEqual(0, _host.Governor.Level, "DRS 关 → 阶梯一步都不许走");
Assert.AreEqual(scaleBefore, ActiveUrp().renderScale, 1e-5f, "DRS 关 → 不得改 renderScale");

// 反证（这条让整个用例可证伪）：同一个目标值在 DRS 打开后必须立刻驱动措施
_host.SetEnabled(true);
for (int i = 0; i < 6; i++) _host.Tick(0.5f);
Assert.Greater(_host.Governor.Level, 0, "DRS 打开后，30fps 预算 + 60ms 帧耗必须触发降级");
Assert.Less(ActiveUrp().renderScale, scaleBefore, "降级必须真的落到 renderScale 上");
```
—— 前后两段构成对照：同样的 60ms 过载、同样的 30fps 目标，DRS 关时 `Level` 不动、`renderScale` 不动；
DRS 开后才走阶梯并真的写 `renderScale`。这证明"DRS 关时目标帧率不被消费"，而不是"没抛异常"。

**裁决 2-① 的落地与证据**（标签后缀 + 可见性）：
- 最终两行文案：`动态分辨率`（URP；非 URP 时 `动态分辨率（仅降档）`）、`帧率目标（DRS不锁帧）`。
- `SettingsPanelSmokeTests` 断言标签**真的渲染出来**：
  `container.Find("Row_video.frameRateTarget/Label").GetComponent<Text>().text == BuildTargetFpsLabel()`。
- `GovernorRowLabels_FitTheLabelColumn_WithoutTruncation` 用真实字体度量守住"后缀不会被截断"，
  实测（`TempLogs/runtests_editmode.xml` 的 `<output>`）：
  ```
  label '帧率目标（DRS不锁帧）' → 334.0px (column 340px)
  label '动态分辨率（仅降档）' → 300.0px (column 340px)
  ```
  （列宽 `SettingsRowFactory.LabelWidth = 340`；`RowHeight = 48` 只容一行，超长会被折行截掉，
  所以后缀必须压在这个宽度内 —— 这正是不采用"（开启动态分辨率后生效，不锁帧）"那种长文案的原因。）

**裁决 2-② 的落地与证据**：`FrameRateCapAndFrameRateTarget_AreSemanticallyDistinct`
断言 `video.frameRateCap.DisplayName == "帧率上限"`（既有文案不动）、目标行含"不锁帧"与"DRS"、
且目标行**不含**"上限" → 两行名称互斥，玩家不会把它当第二个锁帧开关。
`SettingsRegistryTests.VideoPage_HasEightDefinitionsWithExpectedTypes` 同步锁住两行的文案与选项数组。

### 4. 主控裁决已执行（原"偏差请示"）

**裁决 1（批准）：不做"不限"选项。** 最终实现 **30 / 60 / 120 三项，默认 60（= `FrameGovernorHost.DefaultTargetFps`）**。
保留理由（主控原文）：DRS 默认关 → governor 不治理 → 目标帧时无外部影响，零破坏成立；加"不限"要动
`FrameGovernor` 的预算语义，属明令不动的核心算法范围。行内语义与"不限"的关系写在该类文档里：
"不治理 = 不限"正是 DRS=关 的默认态，实际锁帧由 `video.frameRateCap` 负责。

**裁决 2（硬要求）：已全部执行** —— ①标签后缀（不新造灰显机制，照 `UpscalerSettingOptions` 的后缀风格）；
②与"帧率上限"语义互斥并锁进用例；③补"DRS 关时只存值、无运行行为"的证伪用例（证据 8）；
④`SettingsRegistryTests` 的 id/类型/文案数组同步更新（8 项）。

### 5. 交接提示（给验收/主控）

- 本轮修根因采用"开机把 settings.json 全量重放一遍"（与面板同一个 `Load()`），因此**其它页签的已存值也会在启动时生效**：
  这是根因修复的直接后果，不是改动了那些行；无 settings.json 时零影响。
- 仍存在的既有风险（本任务不碰，供验收参考）：打开面板会再次 `Load()` 重放（侦察 R9）、
  `renderScale` 写在 URP **资产**上（R6）、与"画质档位"在治理中改档的相互覆盖（R4/R10/R11）。


## 验收清单（主控写）

> 独立取证，不复用施工数字。8 项全 PASS 才通过。
> **本任务最容易出的问题是"接上了但不生效"和"为了绿灯改弱既有断言"，两项都要专门查。**

1. **V1 六段全量**：`Tools/run_tests.ps1` 全量 → 六段全绿 exit 0；native **仍 190/710,800**（不该被这次改动影响）；
   editmode 应 **663 = 651 + 12**（核对新增用例数与你自己数的 diff 是否一致）；playmode 6；三校验器 exit 0。
2. **V2 两行真渲染 + 文案互斥**：面板里确实生成 `动态分辨率`（Toggle）与 `帧率目标（DRS不锁帧）`（Dropdown）；
   与既有 **`帧率上限`** 行在文案上语义互斥、不混淆；标签不截断（**核对那条宽度断言是真测量，不是硬编码数字凑**）。
3. **V3 持久化 roundtrip**：改值 → settings.json 落盘（贴真实 JSON 片段）→ 重新 Load → 值保持。
4. **V4 开机生效（核心，必须自己复跑）**：不经"打开设置面板"的路径（冷启动/`SettingsBootApplier`）→
   FrameGovernor 的 `Enabled` 与 `TargetMs` **已是 settings.json 的值**；且必须有断言证明值**抵达 governor 内部预算**，
   而不是只读了 model。**只证明"Load 被调用"不算过。**
5. **V5 零破坏**：无 settings.json 的全新用户 → DRS 关、`Governor.Enabled=false`、`TargetMs` 与改动前一致；
   playmode 6/6 绿（它是一次真实冷启动）。
6. **V6 未偷改、未弱化**：逐行读 diff——①`FrameGovernor.cs` / `FrameGovernorHost.cs` 是否**零改动**（若有改动，理由是否充分、是否回主控报备过）；
   ②`SettingsPanelSmokeTests` 的 6/6/7 → 8/8/9 是**预期**，但必须核对**没有把断言写弱**（例如 `AreEqual(8,...)` 改成 `Greater(0,...)`）；
   ③`frameRateCap` 行的行为未被改动；④**没有偷偷加 FG 开关或死灰行**；⑤无 `Ignore`/注释掉用例/删断言。
7. **V7 诚实边界成立**：非 URP 管线时 DRS 只部分生效（只剩降档/后处理）——确认代码里真有对应实现（非 URP 打 WARN + 标签后缀 `（仅降档）`），
   而不是只在报告里写了句话。
8. **V8 范围与规范**：`git status` / `git diff --stat` 改动落在 `UnityProject/Assets/ZFrame/View/**` + `Tests/**` + 本日志；
   `.cs` 无 BOM、换行与既有一致；无 Unity 残留进程/锁；未 git commit。

**须在结论中回答**：①施工称"标签宽度只剩 6px 余量（334/340），字体/字号变动时该用例会先失败而非静默截断"——
这个保护是否真的成立（读断言实现，判断它是量测式还是魔法数字式）；②`FrameRateCapAndFrameRateTarget_AreSemanticallyDistinct`
这条断言是否只是字符串比对、有没有实际约束力。

## 验收结论（验收代理写）

**结论: PASS（8/8）**

> 取证方式：本代理独立复跑 `Tools/run_tests.ps1` 六段（Unity 2022.3.62f3c1），2026-10-28 12:50–12:53，总墙钟 2m33s，
> `RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)`；以下数字全部来自本次运行的 `TempLogs/*`，未引用施工报告。
> 跑前 `tasklist` 无 Unity.exe，跑后无残留进程、无 `Temp/UnityLockfile`。

### 逐项证据

1. **V1 PASS —— 六段全量全绿**
   `native PASS cases=190 asserts=710800 failed=0; ctest: 100% tests passed`（doctest 原文 `test cases: 190 | 190 passed | 0 failed | 0 skipped` / `assertions: 710800 | 710800 passed`）；
   `editmode PASS total=663 passed=663 failed=0 skipped=0 2.8018701s`；`playmode PASS 6/6 5.6004135s`；
   `arch/config/naming exit 0`（arch 原文 `asmdef 扫描数: 24（期望矩阵条目: 11）… 违规数: 0 结果: PASS`）。
   663 的构成已自行核对：新 fixture `FrameGovernorSettingsTests` 在 xml 里 `testcasecount="12" total="12" passed="12"`，
   而 `git diff` 对两个既有测试文件**无任何 `[Test]` 增删**（`grep '^[-+]\s*\[Test\]'` 空）⇒ 663 = 651 + 12。

2. **V2 PASS —— 两行真渲染、文案互斥、标签不截断**
   `SettingsPanelSmokeTests.Push_GeneratesTabBarAndVideoRows_FromRegistry` `result="Passed"`，其断言为真控件：
   `Assert.AreEqual(8, view.RowCount …)`、`Toggle drs = container.Find("Row_video.dynamicResolution/Toggle")…`、
   `Dropdown target = …"Row_video.frameRateTarget/Dropdown"`、`target.options == {"30","60","120"}`、
   `Row_video.frameRateTarget/Label` 文本 == `帧率目标（DRS不锁帧）`（渲染出的文本，不是常量自比）。
   `FrameRateCapAndFrameRateTarget_AreSemanticallyDistinct` Passed：`帧率上限` 原样（该行 `SettingsRegistry.cs:144-152` 未进 diff）、
   目标行含 `不锁帧`/`DRS`、不含 `上限`。宽度断言实测输出（本次运行 xml）：
   `label '帧率目标（DRS不锁帧）' → 334.0px (column 340px)` / `label '动态分辨率（仅降档）' → 300.0px (column 340px)`。

3. **V3 PASS —— 持久化 roundtrip（真实落盘原文）**
   `SaveThenLoad_RoundTripsGovernorRows` 的 `TestContext` 输出（`runtests_editmode.xml:1827+`）：
   ```
   "video.upscaler": 0,
   "video.dynamicResolution": true,
   "video.frameRateTarget": 2,
   "video.shadowDistance": 2,
   ```
   随后新建 model + `Load()` → `applied>=2`、`Get<bool>("video.dynamicResolution")==true`、`Get<int>("video.frameRateTarget")==2`。
   键名风格与既有 `video.*` 一致，未另起存储。

4. **V4 PASS（核心）—— 冷启动值抵达 governor 内部预算**
   `ColdStart_AppliesPersistedGovernorSettings_WithoutOpeningThePanel` 本次实测输出：
   `cold start: applied=2, governorEnabled=True, targetFps=30, targetMs=33.33`，
   断言落在 `_host.Governor.TargetMs == 1000f/30f (±1e-3)` 与 `Governor.Enabled`，而非"Load 被调用过"。
   路径核查：`SettingsBootApplier.cs:32` `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` → `ApplyFromDisk()` → `SettingsModel.Load()`
   → `ApplyValue` → `definition.Apply` → `FrameGovernorHost.SetEnabled` / `SetTargetFps`（`FrameGovernorHost.cs:153/176`）；
   `SetTargetFps` 在治理中直写 `_governor.TargetMs`（`:179-182`），`EnableGovernor` 用 `1000f/_targetFps` 建核心（`:273-277`）
   ⇒ 两行的 JSON 先后顺序不影响最终 `TargetMs`（已读码确认，非依赖重放顺序）。

5. **V5 PASS —— 无 settings.json 的全新用户零破坏**
   实测 `%USERPROFILE%/AppData/LocalLow/ZFrame/ZFrame` 下**只有 `saves/`，无 settings.json**（跑完再查仍无），
   因此本次 PlayMode 6/6 就是一次真实的全新用户冷启动（`SettingsBootApplier.AutoBoot` 真实执行、`applied=0`）。
   EditMode `ColdStart_WithoutSettingsFile_LeavesGovernorDefaultsUntouched` Passed：`applied==0`、
   `IsGovernorEnabled==false`、`Governor.Enabled==false`、`TargetFps` 与 `Governor.TargetMs` 与调用前逐值相同。

6. **V6 PASS —— 未偷改、未弱化**
   ① `git diff --stat -- UnityProject/Assets/ZFrame/View/Pipeline/` 输出为空 ⇒ `FrameGovernor.cs` / `FrameGovernorHost.cs` 零改动；
   ② `SettingsPanelSmokeTests` 6/6/7→8/8/9 是预期：`AreEqual(8,…)`/`AreEqual(9,…)` 仍是等值断言（未改成 `Greater`），
   并**新增**了 Toggle/Dropdown/选项数组/标签文本四条断言；`SettingsRegistryTests.VideoPage_HasEightDefinitionsWithExpectedTypes`
   的类型与 id 数组同步为 8 项（等值断言，非放宽）；
   ③ `video.frameRateCap` 块不在 diff 内（`SettingsRegistry.cs:144-152` 原文未动）；
   ④ `grep -rn "frameGeneration|帧生成" Assets/ZFrame/View Assets/ZFrame/Tests` → **0 命中**（无 FG 开关、无死灰行）；
   ⑤ 三个相关测试文件 `grep "\[Ignore|\[Explicit"` → 0 命中，无注释掉的用例。

7. **V7 PASS —— 非 URP 的"仅降档"在代码里真有实现**
   `FrameGovernorHost.cs:377-380`：`else if (!_warnedNonUrp) { … ZLog.Warn(LogChannel, "active pipeline is not URP — RenderScale measure unavailable (other measures still apply)."); }`
   （该文件本任务零改动，即既有真实实现）；新增侧 `FrameGovernorSettingOptions.cs:69` `DrsNonUrpSuffix = "（仅降档）"`，
   由 `BuildDrsLabel():100-109` 依 `GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset` 拼接，
   注册表 `SettingsRegistry.cs:170` 用 `BuildDrsLabel()` 取文案，`DrsLabel_IsHonest_WhenRenderScaleCannotLand` Passed 锁住两分支。

8. **V8 PASS（附一条偏差说明）—— 范围与规范**
   `git status --short`（见文末）改动为：`View/Settings/SettingsRegistry.cs` + 两个 `Tests/Debug/*.cs` 修改；
   新增 `View/Settings/FrameGovernorSettingOptions.cs`、`View/Settings/SettingsBootApplier.cs`、`Tests/Pipeline/FrameGovernorSettingsTests.cs`（各带 Unity 生成的 `.meta`）+ 本日志 + recon；
   三个新 `.cs` 首字节 `2f 2f 20` / `75 73 69`（**无 BOM**），行尾 CRLF 且与同目录既有文件一致；`git log -1` 仍为 `7cc1050`（未 commit）。
   **偏差**：`UnityProject/Assets/Plugins/ZRender/zrender.dll` 也在改动列表里，位于允许目录之外。
   本代理已证伪"手改"嫌疑：本次跑测前后该文件 md5 恒为 `c20ef91cd5cb598bc4ead49cf1d53bfc`，且 `git status Native/` 为空
   ⇒ 它是 `run_tests.ps1` native 段按既有流程（构建+部署）重写的确定性产物，非业务改动；是否随任务提交由主控决定。

### 清单末尾两问

**① 标签宽度"6px 余量"是量测式还是魔法数字式？——量测式，但保护强度被施工报告略微高估。**
断言实现（`FrameGovernorSettingsTests.cs:143-168`）：真的 new 一个 `Text`，取 `Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")`，
`fontSize = 30`，读 `text.preferredWidth`，再 `Assert.LessOrEqual(width, SettingsRowFactory.LabelWidth)`
（`LabelWidth = 340f` 是引用常量，不是抄来的数）。334/300 是本次实测结果，不是写死的期望值 ⇒ 字号/字体/LabelWidth 一变，测量值就变，断言会真的失败。
**两处削弱**：(a) `Assume.That(width > 0f)` 在字体度量不可用时把用例转成 **Inconclusive（跳过）而非失败**——批次里 `skipped=0` 说明本次没触发，但它是一条"静默降级"通道；
(b) `fontSize = 30` 是硬编码字面量（注释写"SettingsRowFactory 的行标签字号"），而工厂实际字号是 `CreateRow` 里 `CreateText(…, 30, …)` 的实参
⇒ 若将来工厂把标签字号改成 32，本用例仍按 30 去量、**不会**报错，保护失效。建议（不改代码，仅记录）让工厂导出字号常量或在用例里读该常量。

**② `FrameRateCapAndFrameRateTarget_AreSemanticallyDistinct` 是否只是字符串比对？——是，纯文案约束，无行为约束力。**
该用例只做 `cap.DisplayName == "帧率上限"`、`target.DisplayName` 含 `不锁帧`/`DRS`、不含 `上限`、两者不相等（`FrameGovernorSettingsTests.cs:118-132`）。
它不检查 `frameRateCap → Application.targetFrameRate` 与 `frameRateTarget → FrameGovernor.TargetMs` 这两条行为路径是否真的分离
⇒ 若有人把目标行偷偷接到 `Application.targetFrameRate` 上，此用例仍会绿。
它的实际价值仅限"防止玩家把第二行误当第二个锁帧开关"的命名回归；真正的行为分离由别的用例守
（`ApplyTargetFps_UpdatesGovernorTargetMs_LiveWhileEnabled`：30fps→`TargetMs=33.33`、120fps→`8.33`；
`TargetFps_WhileDrsOff_StoresValueOnly_WithNoRuntimeEffect`：DRS 关时 `Level` 与 `renderScale` 一动不动，开后才降）。
按主控裁决 2-② 的要求（"语义互斥"）它完成了任务，但不应被当作行为级证据引用。

### 施工报告的准确性问题（不影响判定）

- **"`Load()` 按定义顺序重放"不成立**：`SettingsModel.cs:252` 是 `foreach (KeyValuePair… in settings)`，即 **JSON 键顺序**。
  施工报告 §C.3 与 `SettingsRegistry.cs:168` 的注释都写成"按定义顺序"。实际影响为零——`Save()` 按注册表顺序写盘（`:150`），
  所以正常存档的键序就是定义序；只有人手改过的 settings.json 才可能乱序，而此时两行的 `TargetMs` 仍与顺序无关（见 V4 路径核查）。
  建议主控在闭环时把那句注释改成"按 settings.json 的键序"。
- 其余数字与结论均与本代理实测一致（663/12、190/710800、6/6、exit 0），无常量夸大。

### 剩余风险（不阻断验收，供主控闭环参考）

1. **开机应用点的"钩子"本身无自动化覆盖**：`AutoBoot`（`AfterSceneLoad`）只有在磁盘上存在 settings.json 时才打印日志，
   而本机/CI 的真实存档目录没有该文件 ⇒ 每次跑测只能证明"`ApplyFromDisk` 的函数体正确"，不能证明"这个 `RuntimeInitializeOnLoadMethod` 真的被引擎调用"。
   本代理只做了静态确认（特性存在、唯一调用方、顺序在 `FrameGovernorHost` 的 BeforeSceneLoad 之后）。若要闭死这条缝，需一条写临时目录的 PlayMode 用例。
2. **全量重放的外溢**：开机应用点是"把 settings.json 全量重放一遍"，因此音频/操作/无障碍等页签的已存值也会在启动时生效（施工报告 §5 已披露）。无 settings.json 时零影响。
3. **`frameRateCap` 与 `frameRateTarget` 不联动**（既有风险 R5，本任务未碰）：上限设 30 而目标设 120（或反之）会让 governor 按错误预算反复降级或永不动作。
4. **`renderScale` 写在 URP 资产上**（既有风险 R6）：治理中途崩溃会把 `URP_*.asset` 的 `m_RenderScale` 留在低值；本任务未改该路径，但新增的"开机自动开启 DRS"会把这条风险从"玩家手动开才会遇到"扩大到"存过档就每次开机都遇到"。
5. 打开设置面板仍会再 `Load()` 重放一次（既有风险 R9），与开机应用点叠加会出现"面板一开，治理状态被存档值二次覆盖"。

### 收尾 `git status --short`

```
 M UnityProject/Assets/Plugins/ZRender/zrender.dll
 M UnityProject/Assets/ZFrame/Tests/Debug/SettingsPanelSmokeTests.cs
 M UnityProject/Assets/ZFrame/Tests/Debug/SettingsRegistryTests.cs
 M UnityProject/Assets/ZFrame/View/Settings/SettingsRegistry.cs
?? Docs/Logs/TASK-A-R9-01-recon.md
?? Docs/Logs/TASK-A-R9-01.md
?? UnityProject/Assets/ZFrame/Tests/Pipeline/FrameGovernorSettingsTests.cs
?? UnityProject/Assets/ZFrame/Tests/Pipeline/FrameGovernorSettingsTests.cs.meta
?? UnityProject/Assets/ZFrame/View/Settings/FrameGovernorSettingOptions.cs
?? UnityProject/Assets/ZFrame/View/Settings/FrameGovernorSettingOptions.cs.meta
?? UnityProject/Assets/ZFrame/View/Settings/SettingsBootApplier.cs
?? UnityProject/Assets/ZFrame/View/Settings/SettingsBootApplier.cs.meta
```

（`zrender.dll` 的二进制变化见 V8 的偏差说明：由 `run_tests.ps1` native 段重写、md5 可复现，非本任务源码改动。）
