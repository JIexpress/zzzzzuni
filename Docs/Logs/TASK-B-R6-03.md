# TASK-B-R6-03 超分设置实接（Unity 侧：Bindings + 视频页 Enum）

状态: todo | 派发: 2026-10-25 | 施工: 施工代理 2026-10-25（自测全绿，见下节） | 验收: （待填） | commit: （待填）

**前置**：B-R6-02 完成（原生 C ABI 三函数已就绪并单测）。

## 任务简报（主控写）

**背景**：B-R6-02 原简报的 ③「设置界面实接」因 Unity 批处理单实例约束被拆出（见 TASK-B-R6-02.md 的"主控拆分注记"）。本任务承接该部分。

**目标**：把超分从"只能命令行 `--fsr` 开关"变成"设置界面可选可切"——视频页新增"超分"Enum（自动/关/FSR3/FSR4/DLSS），选择经 Unity Bindings → 原生 C ABI → UpscalerChain 生效。

**设计要点**：
1. **Unity 侧绑定**（`Assets/ZRender/Bindings/`）：对 B-R6-02 交付的 C ABI 三函数做 P/Invoke 封装（`ZR_Upscaler_SetPreferred(int)` / `ZR_Upscaler_GetActive()` / `ZR_Upscaler_QueryCaps(out caps)`），沿用既有 `ZrNative` 的封装风格与错误处理；dll 缺失/符号缺失时必须优雅降级（返回不可用 + 打日志），**不得抛异常炸 UI**
2. **设置页**（`Assets/ZFrame/View/Settings/SettingsRegistry.cs` 视频页）：新增 Enum 行"超分"，选项 `自动/关/FSR 3.1/FSR 4.1/DLSS 5`，默认"自动"；apply 回调从占位注释改为真实调用 SetPreferred
3. **能力门控**：`QueryCaps` 返回的能力决定哪些选项可用；不支持的项在下拉里标注（如"FSR 4.1（本机不支持）"）或禁用——**不许让用户选一个必然失败的项还不提示**
4. **自动模式语义**：preferred=Auto 时由原生 Chain 按能力矩阵选优（RDNA3→FSR4→FSR3→Bilinear），Unity 侧只负责传 Auto
5. **单测**：Enum 索引 ↔ preferred 枚举值映射（含越界/未知值回退）；Bindings 层在"原生不可用"桩下的降级行为
6. 自测：Unity EditMode 全绿（**基线 372，只许增不许减**）+ 架构校验器 exit 0

**约束**：可动 `Assets/ZRender/`（Bindings）、`Assets/ZFrame/View/Settings/`、`Assets/ZFrame/Tests/`、本任务日志；**不跑原生构建**（B-R6-02 已交付）；Unity 批处理纪律；不 git commit；UTF-8 无 BOM/LF；不动 `Gameplay/`、`Game/Domain/` 纯度。

**交付物**：C ABI 绑定 + 视频页超分 Enum 实接 + 能力门控提示 + 单测 + 施工报告四节。

## 施工报告（施工代理写，2026-10-25）

**施工**: 施工代理（B-R6-03 派发） | **完成日期**: 2026-10-25 | **自测**: Unity EditMode **489/489 全绿、0 失败、0 编译错误**（基线 466 → +23，只增不减）+ 架构校验器 **exit 0（19 asmdef / 0 违规）**

### 一、实现摘要

1. **架构决策（与简报的一处偏差，如实说明）**：简报默认"SettingsRegistry 的 apply 直接调 SetPreferred"，但设计方案 §3.2 与架构校验器矩阵**禁止 ZFrame.View 引用 ZFrame.Render.Bindings**（View 仅许 Core/Data/Gameplay/Unity.InputSystem）。故采用依赖倒置：Core 定义 `IUpscalerSettings` + `UpscalerCapsInfo`，ZFrame.Render（引用 Core+Bindings）实现，View 经 `Services` 定位器消费——与既有 `IAudioService` 完全同模式。链路仍为 **Enum → 映射 → preferred → C ABI**，简报语义（apply 真调 SetPreferred、Auto 由原生 Chain 选优、能力门控）全部达成。
2. **P/Invoke 绑定**（`ZrNative.cs`）：三 DllImport 集中于既有"唯一 DllImport 集中地"；`ZrUpscalerCapsNative`（32B 全定宽，对应 zplugin.h 的 `ZrUpscalerCaps`）；公开门面 `ZrUpscalerNative`（`ZrNative` 为程序集 internal，跨程序集消费经它，异常语义=裸 P/Invoke）。**优雅降级在 `ZFrame.Render.ZrUpscalerSettings`**：捕获 DllNotFound/EntryPointNotFound 与原生错误码（-2 RANGE/-3 SIZE），折算为"不可用"返回值 + `ZLog.Warn`（每实例只告警一次防刷屏），**永不抛异常炸 UI**。
3. **设置页**（`SettingsRegistry.cs:157-165`）：视频页超分行 `video.upscaler` 显示名改"超分"，选项 `自动/关/FSR 3.1/FSR 4.1/DLSS 5`，默认**自动**（0），apply 真调 `UpscalerSettingOptions.ApplyOptionIndex` → `IUpscalerSettings.TrySetPreferred`（即 `ZR_Upscaler_SetPreferred`）。
4. **映射**（`UpscalerSettingOptions.cs:54-64`）：自动→Auto(0)、关→Bilinear(1)（原生无独立"关"，Bilinear 即不过超分的兜底后端）、FSR 3.1→FSR3(3)、FSR 4.1→FSR4(4)、DLSS 5→DLSS(5)；**越界/未知值一律回退 Auto**。
5. **能力门控**（`UpscalerSettingOptions.cs:70-95`）：UGUI Dropdown 无逐项禁用能力，采用**标注**方案（简报允许）：`QueryCaps` 决定——FSR 3.1 看 `FsrRuntimePresent`、FSR 4.1 看 `Fsr4Available`、DLSS 5 看 `DlssAvailable`，不支持项追加"（本机不支持）"；服务未注册/原生不可用（能力未知）时三个厂商项全部标注，不给用户"必然失败还无提示"的选择。
6. **自动模式**：preferred=0 原样传原生，选优（RDNA3→FSR4→FSR3→Bilinear）由原生 UpscalerChain 负责，Unity 侧零二次决策。
7. **自注册**：`ZrUpscalerBootstrapper`（`[RuntimeInitializeOnLoadMethod]`，Play Mode 进入时幂等注册；注册本身不触碰原生库，dll 可用性由服务首次查询惰性判定）。EditMode 不触发——测试自行注册桩，这也正是"服务缺席降级"路径的天然测试场景。

### 二、变更文件清单

| 文件 | 变更 |
|---|---|
| `Assets/ZFrame/Core/Upscaler/IUpscalerSettings.cs` | **新增**：`IUpscalerSettings`（IsAvailable/QueryCaps/TrySetPreferred/TryGetActiveBackend）+ `UpscalerCapsInfo` |
| `Assets/ZRender/Bindings/ZrNative.cs` | 三 DllImport + `ZrUpscalerCapsNative`（命名空间级 public）+ 公开门面 `ZrUpscalerNative`（:63-89） |
| `Assets/ZRender/Runtime/ZrUpscalerSettings.cs` | **新增**：`IZrUpscalerApi` 缝隙 + `ZrUpscalerRealApi` + `ZrUpscalerSettings : IUpscalerSettings`（降级+ZLog）+ `ZrUpscalerBootstrapper` |
| `Assets/ZFrame/View/Settings/UpscalerSettingOptions.cs` | **新增**：选项常量 / `OptionIndexToPreferred`（:54）/ `BuildOptionLabels` 能力门控（:70-95）/ `ApplyOptionIndex`（:98） |
| `Assets/ZFrame/View/Settings/SettingsRegistry.cs` | 超分行占位→实接（:157-165）+ 类文档同步 |
| `Assets/ZRender/Tests/ZFrame.Render.Tests.asmdef` | 增引用 `ZFrame.Core`（Tests 程序集豁免矩阵） |
| `Assets/ZRender/Tests/ZrUpscalerSettingsTests.cs` | **新增** 8 用例：真 ABI 往返 4 + 桩降级（DllMissing/SymbolMissing/RANGE/SIZE）4 |
| `Assets/ZFrame/Tests/Debug/UpscalerSettingsTests.cs` | **新增** 15 用例：映射 3（含越界回退）+ 门控标注 4 + apply 链路 5 + registry 集成 3 |
| `Assets/ZFrame/Tests/Debug/SettingsRegistryTests.cs` | `VideoPage_Upscaler_IsStoredPlaceholder` → `IsWiredLive_WithCapabilityGatedLabels`（占位断言升级为实接断言，**加强非放宽**；Apply null→NotNull、4 选项→5 选项+标注） |

未动：`Native/`（零改动）、`Gameplay/`、`Game/Domain/`、`TASKS.md`/`HANDOFF.md`/`WORKLOG.md`。

### 三、自测结果（Git Bash 实跑，2026-10-25 20:51–20:59）

| 项 | 结果 | 证据 |
|---|---|---|
| EditMode | **489/489 passed, 0 failed, 0 skipped**（基线 466 → +23） | `TempLogs/r6_03_editmode.xml` 关键行：`<test-run … total="489" passed="489" failed="0" inconclusive="0" skipped="0">`；日志 0 个 `error CS` |
| 新增用例 | 23 个全部在册 | xml 内 `ZrUpscalerSettingsTests`（8）+ `UpscalerSettingsTests`（15）逐名可见 |
| 架构校验器 | **exit 0** | `TempLogs/arch_report.txt`：`asmdef 扫描数: 19（期望矩阵条目: 10，Tests 程序集豁免）`、`违规数: 0`、`结果: PASS` |
| 批处理纪律 | 遵守 | Git Bash 启动、跑前查无 Unity.exe + 删 UnityLockfile、测试命令未带 -quit、两次假失败（编译错误）均即时修复后重跑 |

### 四、已知风险

1. **settings.json 旧值语义漂移**：旧占位选项 `["关","FSR3","FSR4","DLSS"]` 改为 5 项后，历史持久化索引含义错位（旧 1=FSR3 → 新 1=关）。SettingsModel 对越界值安全忽略，但对 0..3 的旧值不报错。项目预发布、设置 UI 未对外开放，主控可裁决是否清一次用户侧 settings.json 或改 setting id 作废旧键。
2. **能力门控为"标注"而非"禁用"**：UGUI Dropdown 不支持逐项禁用；用户仍可选标注项，此时 apply 照常下发（原生 Chain 自行解析），选择时已有可见提示。
3. **FSR 4.1 / DLSS 5 真机可用性**：本机 780M 核显 FSR4 运行时不可用、DLSS 需 N 卡——UI 标注会如实显示"（本机不支持）"（与 B-R6-02 受限项一致，外部补验不变）。
4. **Play Mode 注册时机**：`RuntimeInitializeOnLoadMethod(BeforeSceneLoad)` 在禁用域重载（Enter Play Mode Options）下靠 TryGet 幂等防护；若未来有更早消费方需提前到 AfterAssembliesLoaded。
5. **`ZrUpscalerSettings` 告警每实例一次**：进程级重复提示被抑制（防刷屏），如需每次反馈可改回逐次告警。

## 验收清单（主控写）

1. EditMode 全绿（用例数 ≥372）；校验器 exit 0
2. P/Invoke 三函数封装齐全，dll/符号缺失时降级不抛（有单测或代码证据）
3. 视频页"超分" Enum 存在且 apply 真调 SetPreferred（行号）
4. 能力门控：不支持项有可见提示/禁用（行号）
5. 自动模式传 Auto 的链路（Enum→preferred→C ABI）
6. 既有设置项未被改坏（五页签仍齐、Esc 语义不变）
7. 文件规范 + 报告四节

## 验收结论（验收代理写）

**验收**: 验收代理（独立实例，非施工代理） | **验收日期**: 2026-10-25 | **总结论: PASS（7/7）**

证据文件：`TempLogs/acc_r6_03.xml`（EditMode）、`TempLogs/acc_r6_03.log`、`TempLogs/acc_r6_03_arch.log`（架构校验器）。均为验收代理 Git Bash 实跑。

### 逐项裁决

| # | 项 | 结论 | 证据 |
|---|---|---|---|
| 1 | EditMode 全绿 | **PASS** | 验收代理自跑（Git Bash、无 -quit、跑前确认无 Unity.exe 实例并删 UnityLockfile）。`acc_r6_03.xml` 关键行：`<test-run … result="Passed" total="489" passed="489" failed="0" inconclusive="0" skipped="0">`；日志 0 个 `error CS`。实际用例 **489**（≥466 基线，+23）；新增 23 个逐名在册：`ZFrame.Render.Tests.ZrUpscalerSettingsTests` 8 + `ZFrame.Debug.Tests.UpscalerSettingsTests` 15 |
| 2 | P/Invoke 三函数 + 降级不抛 | **PASS** | 三 DllImport 集中于 `Assets/ZRender/Bindings/ZrNative.cs`（diff：`ZR_Upscaler_SetPreferred/GetActive/QueryCaps` 于 `ZrNative` internal 类，`ZrUpscalerCapsNative` 32B 全定宽），公开门面 `ZrUpscalerNative` 同文件。降级在 `ZrUpscalerSettings.cs:114-118/135-139/158-163`：捕获 `DllNotFoundException/EntryPointNotFoundException` + 原生错误码（-2/-3）→ 返回 `default`/`false` + `ZLog.Warn`（WarnOnce :167-176），无 throw 路径。单测 4 个降级用例：`ZrUpscalerSettingsTests.cs:96-143`（DllMissing/SymbolMissing/RANGE/SIZE 桩，断言 DoesNotThrow 语义=返回 false） |
| 3 | 视频页超分 Enum + apply 真调 | **PASS** | `SettingsRegistry.cs:158-162`：`"video.upscaler"` 显示名 `"超分"`，默认 `UpscalerSettingOptions.DefaultOptionIndex`（=OptionAuto=0，`UpscalerSettingOptions.cs:43`，即"自动"），`apply: value => UpscalerSettingOptions.ApplyOptionIndex((int)value)`；`UpscalerSettingOptions.cs:98-111` 经 `Services` → `IUpscalerSettings.TrySetPreferred`（即 C ABI SetPreferred）。单测 `UpscalerSettingsTests.cs:249-261` 验证 apply 链路真调服务 |
| 4 | 能力门控 | **PASS** | `UpscalerSettingOptions.cs:80-91` `BuildOptionLabels`：FSR3.1 看 `FsrRuntimePresent`、FSR4.1 看 `Fsr4Available`、DLSS5 看 `DlssAvailable`；`MarkIfUnsupported :113-116` 追加"（本机不支持）"。服务缺席/能力未知（Available=false）时厂商项全部标注（:82,87-89），无"必然失败还无提示"路径。单测 4 用例 `UpscalerSettingsTests.cs:93-154`。施工报告如实披露：UGUI Dropdown 不支持逐项禁用，采用标注方案（简报允许） |
| 5 | 自动模式链路 + 越界回退 | **PASS** | `UpscalerSettingOptions.cs:54-64` `OptionIndexToPreferred`：switch `_ => 0`（越界/未知回退 Auto），Auto 原样传 0 由原生 Chain 选优。单测：映射含越界 `UpscalerSettingsTests.cs:74-81`（-1/999/int.MinValue/int.MaxValue→0）、apply 传 0 `:170-178`、越界回退后仍下发 `:181-189` |
| 6 | 既有设置未破坏 | **PASS** | `SettingsRegistry.cs:94-104` 五页签齐全（视频/音频/操作/游戏性/无障碍）。`git diff` 对 SettingsRegistry.cs 仅涉超分行与文档注释；操作页 Binding 行（`SettingsRegistry.cs:239-250`，A-M3-03/04 四向+交互+暂停+恢复默认）原样未动。Esc 语义（`SettingsPanelView.cs:262,277-280` / `SettingsPanelPresenter.cs:118`，Esc=CANCEL revert 不保存）文件不在变更清单，零触碰 |
| 7 | 架构校验器 exit 0 | **PASS** | 验收代理实跑 `-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine`（带 -quit，Git Bash）：**exit 0**；日志 `违规数: 0`、`结果: PASS` |

### 防作弊审查（既有测试 diff 逐个判定）

被修改的既有测试文件仅 1 个：`ZFrame/Tests/Debug/SettingsRegistryTests.cs`。判定：**合理适配、加强非放宽**——
- `VideoPage_Upscaler_IsStoredPlaceholder` → `IsWiredLive_WithCapabilityGatedLabels`：`Assert.IsNull(Apply)` → `IsNotNull`（占位断言升级为实接断言）、4 选项 → 5 选项 + 门控标注断言 + 显示名/默认值断言，均为收紧。
- 其余 diff 为注释同步（"stored placeholder" → "wired live"），无删用例、无放宽既有断言。
- 其余既有测试文件（466 基线的其余用例）零改动；既有用例全量在本次实跑中通过。

### 与施工报告的核对

- 用例数 489（466+23）、两 fixture 用例数 8/15、架构校验器 exit 0：**与报告一致**，独立复现。
- 报告声明的架构偏差（View 不引用 Bindings，改走 `IUpscalerSettings` 依赖倒置）经核实属实且合理：`SettingsRegistry.cs` using 仅 `ZFrame.Core`，asmdef 矩阵校验 PASS。
- 文件规范：新增 5 个 .cs 均无 BOM（`od` 抽查文件头字节）、LF 换行，符合规范。
- 小笔误（不影响结论）：任务简报"验收清单"第 1 项仍写"基线 372"，未随 A-M4-01 后基线刷新为 466；以 HANDOFF/施工报告的 466 为准，489 满足任一口径。

### 遗留项（不阻塞本任务 PASS）

1. 施工报告"已知风险"1（settings.json 旧占位索引语义漂移，旧 1=FSR3 → 新 1=关）：主控裁决是否清用户侧 settings.json 或作废旧键。
2. 能力门控为标注而非禁用（UGUI Dropdown 平台限制，简报允许标注方案）：如需硬禁用需换控件或自定义下拉。
3. FSR 4.1 / DLSS 5 真机可用性：沿用 B-R6-02 外部补验项（本机核显/无 N 卡），UI 标注会如实显示"（本机不支持）"。
