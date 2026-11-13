# TASK-B-FSR-02 —— P1 能力三态诚实化 + 全仓写死能力表面审计

- **轨道**：B / FSR（U 盘改进计划 P1 + 用户 2026-11-14 追加要求）
- **状态**：todo（**依赖 B-FSR-01 闭环后开工**——probeMode 语义建立在真设备探测已修复的基础上）
- **派发**：主控，2026-11-14（简报预写）
- **来源**：`Docs/Planning/ZRender-改进计划-2026-11-13.md` §3-P1；用户口头反馈：对端机 demo 里 FSR 选项是**写死的**而非真实探针，换机即暴露——**探针精神不只作用于 FSR 一个选项，所有发现不能实现的地方都要如实写出来**

## 任务简报（主控写）

### 0. 为什么这条比 P0 更值钱

P0 修的是一个 bug；P1 修的是「以后所有 bug 都会伪装成不支持」这个机制。现状 `ZrUpscalerCaps` 只有一个布尔 `fsr4Available`，三种情况（硬件不支持 / FFX 运行时 DLL 缺失 / 根本没测——这次踩的就是这种）在 UI 上表现完全一样，无法追溯。

### 1. 做什么

1. **结构体**：`ZrUpscalerCaps`（`Native/zplugin/zplugin.h`）追加 `uint32_t fsr4ProbeMode;` 放在**结构体末尾**（structSize 版本化协议允许向后兼容追加）：
   - `0` = 真实设备探测（可信）
   - `1` = 保守回退（设备创建失败走了 null 路径——**结果可能失真，UI 必须标"未检测"**）
   - `2` = 未探测（探测未执行或异常）
   - 递增 `ZR_ApiVersion`（当前值见 zplugin.cpp，先读再 +1）；同步检查 C# 侧绑定结构（`UnityProject/Assets/ZRender/Bindings/ZrNative.cs`）镜像字段
2. **C# 三态文案**：设置页（`ZFrame/View/Settings/UpscalerSettingOptions.cs` + `ZRender/Runtime/ZrUpscalerSettings.cs`）FSR4 行按状态显示三种文案，映射规则：
   - `fsrRuntimePresent=0` → 「运行时缺失（FFX DLL 未找到）」
   - `fsr4ProbeMode!=0 && fsr4Available=0` → 「未检测（探测回退，结果不可信）」
   - `fsr4ProbeMode=0 && fsr4Available=0` → 「本机不支持」
   - `fsr4Available=1` → 显示实际版本（provider 版本串，如已有）
3. **全仓写死能力表面审计（本任务核心，用户点名）**：
   - grep 全仓（C# + C++ + demo/窗口模式）所有能力/选项/版本**硬编码**处：写死的 "FSR"/"DLSS"/版本串/支持布尔/选项枚举值
   - 范围至少覆盖：`ZFrame/View/Settings/`、`ZFrame/Core/Upscaler/`、`ZRender/Runtime/`、`ZRender/Bindings/`、`Native/tests/zrhi_d3d12_window.cpp`（窗口 demo 的 `--fsr/--fsr4` 模式选择逻辑）、设置注册表
   - 判据：**每一个用户可见的能力声明都必须源自 `ZR_Upscaler_QueryCaps` / provider 枚举的真实输出**；发现写死处一律改为读真实探测结果，做不到的如实标"未检测/不支持+原因"
   - 已知待修：`zplugin.h` 里 `fsr4Available` 字段注释「保守：device=null 枚举」在 B-FSR-01 修复后已失真——改为「真实设备探测（回退时 fsr4ProbeMode!=0）」
4. **注释诚实化**：`fsr_upscaler.cpp` 内所有涉及探测能力的注释过一遍，删掉/改写一切"与真实设备一致"类未实测声明

### 2. 测试

1. 原生 doctest（test_upscaler.cpp）：probeMode 三值语义（null 回退路径 → mode=1；真实设备路径 → mode=0；CI 无硬件时断言 mode!=0 与 fsr4Available=false 自洽，不得断言有硬件）
2. EditMode：三态文案映射表驱动用例（三态各一断言 + 未知值兜底）；既有 `UpscalerSettingsTests`/`ZrUpscalerSettingsTests` 全绿
3. `Tools/build_native.ps1` + `zrender_test.exe --success=false` 全绿（只增不减）；Unity EditMode 跑 ZFrame+ZRender 相关测试段

### 3. 铁律（约法三章）

- 不许把「未验证」写成「已验证」；UI 文案不许出现无来源的"支持"
- 施工不 commit、不改看板/HANDOFF；一任务一 commit 由主控执行
- UTF-8 无 BOM / LF；/WX 零警告；零第三方依赖

### 4. 完成判据

- 任何一次"不支持"都能说出三类原因之一（UI 可见 + caps 字段可编程读取）
- 全仓审计清单（命中文件×处理结果）进施工报告——包括"查了哪些地方、每处的结论"
- CI 无硬件环境全绿；本机真机复核 UI 显示真实状态

## 施工报告（施工代理，2026-11-14 01:55）

### 0. ABI 变更摘要（v1 → v2）

- `ZrUpscalerCaps` 末尾追加 `uint32_t fsr4ProbeMode`（32B → 36B；追加位置遵循 structSize 版本化协议）；`ZR_ApiVersion` 1 → 2。
- structSize 校验链核验：原生侧 `zplugin.cpp:265` 用 `sizeof(ZrUpscalerCaps)` 动态比对（自动 36B）；C# 侧 `ZrUpscalerSettings.cs` 用 `Marshal.SizeOf(typeof(ZrUpscalerCapsNative))` 动态取值（自动 36B）——两侧均无写死数字，重编即自洽。旧 dll（32B）+ 新 C#（36B）组合会命中 `-3 ZR_UPSCALER_ERR_SIZE` → 走既有优雅降级路径（不可用 + 告警一次），不炸 UI。
- `ZR_ApiVersion` 的全部消费方同步递增：`Native/tests/test_smoke.cpp`（断言 1u→2u）、`ZrPlugin.cs ExpectedApiVersion`（1→2）、`ZrPluginTests`（用例改名 + 断言 2u）。
- 内部档案 `zr::UpscalerDeviceCaps` 同步追加 `uint32_t fsr4ProbeMode = 2`（默认"未探测"——零初始化不会被误读成"真设备探测可信"；C++ 结构不入 ABI，尺寸断言 12→16B 同步更新）。

### 1. probeMode 三态实现要点

- **语义**（zplugin.h ABI 注释 = 单一事实源，zupscaler.h 内部档案同文）：`0`=真实设备探测（FFX provider 枚举拿到真 ID3D12Device*，结论可信）；`1`=保守回退（建备失败走 device=null 枚举——FFX null 枚举返回截断列表，结果可能失真，消费方必须按"未检测"处理）；`2`=未探测（provider 枚举未执行，典型=FFX 运行时缺席；此时 fsr4Available 恒 0）。未知值（未来扩展）一律按"未检测"处理，不得当作 0。
- **实现**（`upscaler_caps.cpp`）：`fsrRuntimePresent==0` → mode=2（运行时都缺席，枚举根本没跑）；否则 `probeDevice!=nullptr` → 0 / `nullptr` → 1。B-FSR-01 的缓存语义未动：`zplugin.cpp` 的 `g_upscalerCapsReady` 门控首调探测一次、缓存命中零探测零建备，probeMode 随缓存一起透传。
- **窗口验证程序**（`zrhi_d3d12_window.cpp` RunFsrMode）：该路径手持真实设备自填 caps——补 `fsr4ProbeMode = fsrRuntimePresent ? 0 : 2`，并把 caps 证据行与 FSR4 UNAVAILABLE 判定行都加上探测口径字段（原判定行把"运行时缺席"与"硬件无 4.x"两种原因混在一句固定文案里，现在 `fsrRuntime=%d` 如实区分）。
- **C# 三态文案**（`UpscalerSettingOptions.BuildFsr4StatusText`，判定序即优先序）：服务缺席/查询失败（Available=false）→「未检测（原生内核未启用）」；Fsr4Available=1 →「可用」（C ABI v2 无 provider 版本串导出，**不编造版本号**，读口属后续任务）；FsrRuntimePresent=0 →「运行时缺失（FFX DLL 未找到）」；Fsr4ProbeMode!=0（含未知值兜底）→「未检测（探测回退，结果不可信）」；仅当 probeMode=0 且运行时在位且 4.x 未枚举 →「本机不支持」（唯一合法的"不支持"路径）。标签格式：本机不支持=既有「（本机不支持）」后缀、可用=「（可用）」、其余带原因的状态用「名称：状态」避免双括号嵌套。FSR3/DLSS 行同建铁律修正：服务缺席（根本没测）也标「未检测（原生内核未启用）」，不再无来源地宣称"本机不支持"；服务在位时其门控位（FsrRuntimePresent/DlssAvailable）是真实探测输出，维持"（本机不支持）"。

### 2. 改动文件清单（git diff --numstat，+增/−删；均为本任务改动，ZFrame/Data/** 未触碰）

| 文件 | 行数 | 内容 |
|---|---|---|
| `Native/zupscaler/zupscaler.h` | +14/−3 | `UpscalerDeviceCaps` 追加 `fsr4ProbeMode=2` + 三态语义注释；`ZupProbeDeviceCaps` 契约注释改口径三态陈述 |
| `Native/zupscaler/upscaler_caps.cpp` | +14/−1 | 填 probeMode（运行时缺席→2；真设备→0；回退→1）；文件头注释补三态口径 |
| `Native/zplugin/zplugin.h` | +16/−3 | `ZrUpscalerCaps` 末尾追加 `fsr4ProbeMode` + 三态注释；`fsr4Available` 失实注释「保守：device=null 枚举」改为「真实设备探测；回退口径下可能失真——见 fsr4ProbeMode」；`ZR_ApiVersion` 注释（当前 2）；探测描述更新（短命真设备） |
| `Native/zplugin/zplugin.cpp` | +2/−1 | `ZR_ApiVersion` → 2u；QueryCaps 透传 `fsr4ProbeMode`（缓存语义零改动） |
| `Native/zupscaler/fg_ffx.cpp` | +7/−2 | **B-FSR-01 移交项**：`EnumerateFgProviders` 失实注释「FFX 返回与真机设备一致的保守列表」改为事实陈述（UPSCALER 族实测 null=2 vs 真设备=4，RX 9060 XT 2026-11-13；FRAMEGENERATION 族未做双设备对照，null 口径只能当"未检测"） |
| `Native/zupscaler/fg_ffx.h` | +5/−3 | **B-FSR-01 移交项**：`QuerySupport` 同型失实注释同法修正 |
| `Native/tests/test_smoke.cpp` | +3/−2 | ApiVersion 断言 1→2（用例名同步） |
| `Native/tests/test_upscaler.cpp` | +47/−4 | 第 9 节：sizeof 12→16B、两轮探测一致性补 probeMode；新增 9b 节「fsr4ProbeMode three-state semantics」——值域≤2、未探测不许阳性、无适配器分支（mode!=0 ∧ fsr4=false ∧ runtime?1:2）、有适配器分支（runtime 在→0 或 1，780M 实证 0；runtime 缺→2） |
| `Native/tests/test_upscaler_cabi.cpp` | +13/−1 | QueryCaps 成功路径补 probeMode 透传断言（≤2；mode=2⇒fsr4=0；runtime 缺⇒mode=2；无适配器⇒mode=1） |
| `Native/tests/zrhi_d3d12_window.cpp` | +10/−5 | RunFsrMode 补 `fsr4ProbeMode` 填充与 caps 证据行字段；FSR4 UNAVAILABLE 判定行加 `fsrRuntime=%d` 区分"DLL 缺失"与"硬件无 4.x" |
| `UnityProject/Assets/ZRender/Bindings/ZrNative.cs` | +9/−1 | `ZrUpscalerCapsNative` 末尾镜像 `Fsr4ProbeMode`（36B @v2 注释 + 三态 doc） |
| `UnityProject/Assets/ZRender/Bindings/ZrPlugin.cs` | +2/−2 | `ExpectedApiVersion` 1→2（注释注明 v2 内容） |
| `UnityProject/Assets/ZRender/Tests/ZrPluginTests.cs` | +3/−2 | ApiVersion 用例 2u（改名） |
| `UnityProject/Assets/ZFrame/Core/Upscaler/IUpscalerSettings.cs` | +15/−1 | `UpscalerCapsInfo` 追加 `Fsr4ProbeMode` + 逐字段 doc（逐字段拷贝路径，非布局封送，追加末尾安全） |
| `UnityProject/Assets/ZRender/Runtime/ZrUpscalerSettings.cs` | +5/−0 | QueryCaps 透传 `Fsr4ProbeMode`；文件头补 v2 口径说明 |
| `UnityProject/Assets/ZFrame/View/Settings/UpscalerSettingOptions.cs` | +104/−9 | 三态映射核心：`BuildFsr4StatusText`（纯函数）+ `BuildFsr4Label` + 4 个状态常量；FSR3/DLSS 行"服务缺席→未检测"修正；类注释同步 |
| `UnityProject/Assets/ZFrame/View/Settings/SettingsRegistry.cs` | +3/−1 | video.upscaler 行注释改三态陈述（行为经 BuildOptionLabels 自动生效） |
| `UnityProject/Assets/ZFrame/Tests/Debug/UpscalerSettingsTests.cs` | +120/−11 | 新增表驱动 `Fsr4Status_TableDriven_ThreeStatesAndFallbacks`（9 行映射表：三态各一 + 运行时缺失优先序 + 未知 probeMode 99/uint.MaxValue 兜底 + 服务缺席优先，状态文本与全标签双断言）；4 个既有用例按新语义更新（服务缺席→未检测；全能力→「（可用）」；probeMode=0 显式化） |
| `UnityProject/Assets/ZFrame/Tests/Debug/SettingsRegistryTests.cs` | +3/−2 | 无服务注册断言改「未检测（原生内核未启用）」 |
| `UnityProject/Assets/ZRender/Tests/ZrUpscalerSettingsTests.cs` | +8/−0 | 真 ABI 往返用例补 probeMode 断言（≤2；mode=2⇒Fsr4Available=false） |
| `UnityProject/Assets/Plugins/ZRender/zrender.dll` | 二进制 | build_native.ps1 步骤 4/4 自动部署；两侧 sha256 一致（`776ffedc…e359a1`，01:35 本轮构建） |

### 3. 全仓写死能力表面审计清单（命中 × 处理结果）

| # | 审计面（查了哪里） | 命中/结论 | 处理 |
|---|---|---|---|
| A1 | `Native/zplugin/zplugin.h:90` `fsr4Available` 注释 | 失实：「保守：device=null 枚举」（B-FSR-01 后已不成立） | **已改**：真实设备探测 + 回退口径指向 fsr4ProbeMode |
| A2 | `Native/zupscaler/fg_ffx.cpp:125-127`（移交项） | 失实：「FFX 返回与真机设备一致的保守列表」（FRAMEGENERATION 族） | **已改**：事实陈述——同族失实已在 UPSCALER 族双设备实测证伪（RX 9060 XT null=2 vs 真=4 含 4.1.2），FG 族未做双设备对照、null 口径只能当"未检测"；窗口/探针路径均传真设备（grep 全部 3 个调用点核实：`zrhi_d3d12_window:10531`、`zfg_probe_wrap:1126`、`zfg_probe_selftest:203`，无一传 null） |
| A3 | `Native/zupscaler/fg_ffx.h:161-163`（移交项） | 同 A2 的头文件副本 | **已改**：同 A2 口径 |
| A4 | `Native/zupscaler/upscaler_caps.cpp:15`、`zupscaler.h:264` | 事实正确但口径未显式化（"保守回退"无字段可查） | **已改**：补 fsr4ProbeMode 三态口径引用 |
| A5 | `Native/zupscaler/fsr_upscaler.cpp` 全文 | 注释版本串（3.1.5/2.3.4/4.1.2）均为**带出处的实测记录**非写死判定；`BackendVersion()` 返回 provider 枚举的真实 name（未命中时 "ffx runtime (version unknown)" 如实）；"4."前缀族选择是逻辑非能力声明 | 无需改（B-FSR-01 已诚实化，本轮复核确认） |
| A6 | `Native/zupscaler/dlss_upscaler.cpp` `ZupDlssRuntimeVersion` | 版本串=编译期头版本 + `runtime present/absent` 实况 | 无需改（如实） |
| A7 | `Native/tests/zrhi_d3d12_window.cpp` --fsr/--fsr4 模式选择与判定 | 模式选择由真实探测驱动（无硬件门控写死）；但 FSR4 UNAVAILABLE 固定理由串把"DLL 缺失"与"硬件无 4.x"混为一谈 | **已改**：判定行加 `fsrRuntime=%d`（"needs discrete RX 7000/RX 9000" 标明出处=SDK v2.3.0 文档口径）；caps 证据行加 probeMode |
| A8 | `UnityProject/Assets/ZFrame/View/Settings/UpscalerSettingOptions.cs` | FSR4 行单布尔门控——三态（硬件不支持/运行时缺失/没测）在 UI 上不可区分（本任务靶心） | **已改**：三态映射（见 §1） |
| A9 | `UpscalerSettingOptions` FSR3/DLSS 行 + `SettingsRegistry` | 服务缺席（根本没测）标「（本机不支持）」= 无来源的否定声明（同型失实） | **已改**：服务缺席一律「未检测（原生内核未启用）」；服务在位时门控位为真实探测输出，维持原样 |
| A10 | `UnityProject/Assets/ZFrame/View/Settings/FrameGenerationSettingOptions.cs` + `ZrFrameGenerationSettings.cs` | FG 能力已是多档位诚实模式（Available/EditorOnly/NotD3D12/PipelineUnsupported/ProxyUnavailable 各自点名原因；`SuffixFor` 故意不写 default 兜底防"替新档撒谎"） | 无需改（本任务三态设计与既有 FG 模式同构，互为印证） |
| A11 | `UnityProject/Assets/ZRender/Bindings/`（ZrNative/ZrPlugin/ZrNativeBridge/ZrMesh/ZrFgProxyNative 等） | 无能力/版本写死声明；caps 为逐字段镜像（本次同步 36B）；`ExpectedApiVersion` 已随 v2 递增 | ZrNative/ZrPlugin **已改**（镜像字段+版本），其余无需改 |
| A12 | `UnityProject/Assets/ZRender/Runtime/`（除 ZrUpscalerSettings 外全扫 FSR/DLSS/支持/Available） | 仅 ZrFrameGenerationSettings 的 FG 能力枚举（A10，诚实）与 ZrSceneMath 的"保守"（视锥体裁剪语义，非能力声明） | 无需改 |
| A13 | `UnityProject/Assets/ZFrame/Core/Upscaler/IUpscalerSettings.cs` | Fsr4Available doc「核显通常为 false」为经验陈述无来源 | **已改**：改为"可信度须结合 Fsr4ProbeMode 判读" |
| A14 | C# 全仓 grep 写死版本串（4.1.2/3.1.5/2.3.4 等） | 用户可见面零命中；仅测试期望串与注释里的带出处实测记录 | 无需改 |
| A15 | C++ 全仓 grep「保守/conservative/与真机一致」 | 能力声明类失实全部清零（B-FSR-01 五文件 + 本轮 fg_ffx 两处）；其余命中均为非能力语义（AABB 保守近似、保守释放策略等，事实正确） | 已清零 |

### 4. 构建与测试数字（2026-11-14 01:16–01:55 本机实跑；构建前代理变量核查=本机 env 无代理变量）

- `Tools/build_native.ps1`（两轮：三态实现后 + 窗口 caps 修正后）：**两轮 ALL GREEN**（configure + build /W4 /WX 零警告 + 全量 doctest + 部署 4/4 步全过）。
- `zrender_test.exe --success=false`（repo 根 CWD，01:38）：**351 用例 / 778,743 断言，0 failed / 0 skipped**（B-FSR-01 基线 350/778,737 → **+1 用例 +6 断言，只增不减**；+1=9b 节新用例，+6=新用例 4[REQUIRE+3 CHECK] + 第 9 节一致性 1 + cabi 透传 1）。
- Unity EditMode（`Tools/run_tests.ps1 -Segments editmode`，01:53:52Z 完成，单实例纪律内建、results.xml 为证）：**total=1167 passed=1166 failed=1**，`error CS`=0。唯一失败 = `ZFrame.Data.Tests.CsvImporterTests.ImportRows_AnnotatedTypeMismatch_IsReported`（`CsvImporterTests.cs:196`）——**A-CONF-01 并行代理未提交 WIP**（git diff 可见其 ZFrame/Data/** 活动区 +58 行新增用例，本任务铁律禁触该区），与本任务改动零交集。**本任务相关 4 个 fixture 全绿**：`UpscalerSettingsTests` 16/16（含新表驱动用例）、`SettingsRegistryTests` 14/14、`ZrUpscalerSettingsTests` 8/8（真 dll v2 往返含 probeMode 断言）、`ZrPluginTests` 5/5（含 ApiVersion=2）。
- `zrhi_d3d12_window.exe --fsr4`（01:45，exit=2=受限非缺陷，日志 `TempLogs/fsr4_bfsr02_run.log`）：caps 行 `vendor=0x1002 device=0x15BF fsrRuntime=1 fsr4Available=0 **fsr4ProbeMode=0**`；null=2 条 vs 真设备=2 条（3.1.5/2.3.4，与本机硬件真相一致）；UNAVAILABLE 判定行如实区分 `fsrRuntime=1`（DLL 在位，"无 4.x"确系硬件结论）。

### 5. 完成判据自证

- **任何一次"不支持"都能说出原因**：UI 面——FSR4 行四态（运行时缺失/未检测×2/本机不支持/可用）+ FSR3/DLSS 行未检测兜底；编程面——`fsr4ProbeMode` 经 C ABI v2 可读（三态+未知值约定）。"本机不支持"出现当且仅当 probeMode=0（真设备探测可信）。
- **本机真机复核**：doctest 780M 专属断言（vendorId==0x1002 ∧ deviceId==0x15BF → probeMode==0）随全量绿通过 = 真设备路径实证；EditMode 真 dll 往返（ZrPluginTests ApiVersion=2 + ZrUpscalerSettingsTests probeMode 值域）= 托管链路实证。
- **CI 无硬件可过**：9b 节对 vendorId==0 分支只断言 mode!=0 ∧ fsr4=false ∧ (runtime?1:2)，未断言任何硬件阳性；cabi 同理（无适配器→mode=1）。

### 6. 遗留风险与移交

1. **EditMode 唯一失败归属 A-CONF-01**：其 WIP（`CsvImporterTests`）在本轮 EditMode 编译时已入 ScriptAssemblies（共享 Library）。建议主控在 A-CONF-01 闭环后复跑 EditMode 全量确认 1167+ 全绿——本任务相关段已绿，无需重跑即可验收本任务。
2. **CI（GitHub Actions L1）未实跑**（本机不可代验）：无硬件分支逻辑已被 doctest 的 vendorId==0 分支覆盖且与实现逐条对应，风险低；待 CI 轮确认。
3. **provider 版本串读口缺位**：C ABI v2 无 provider 版本导出，UI 的"可用"态不带版本号（如实标注、不编造）。若需版本号进 UI，属后续任务（需新 C ABI 导出 + v3 递增）。
4. **未知 probeMode 的原生侧产出禁令靠值域断言把守**（doctest ≤2 + cabi ≤2）：未来实现若产出 >2，消费方按"未检测"兜底（UI 已实现），测试会红——有牙。
5. **FrameGeneration 探针的双设备阳性对照**（A2 中"FG 族未做双设备对照"）与 B-FSR-01 的 B-PROBE-01 移交同理：下次同步对端机时补跑 `--fg` 拿 FG 族 null vs 真设备对照数字。

### 7. 验收建议

建议**验收 PASS 候选**：ABI 变更三点（追加字段/版本递增/两侧 structSize 自洽）+ 三态语义（原生填装/ABI 透传/UI 映射/未知值兜底）+ 全仓审计 15 项（8 项已改、7 项核实无需改）+ 原生 351/778,743 全绿 + EditMode 相关段全绿 + 移交注释两项闭环。验收代理注意：EditMode 全量的 1 failed 需按 §6.1 归因核对（勿计入本任务）；`zrender_test` 需从 repo 根跑（TASK-G-63 已知 CWD 口径）。

## 验收清单（主控写，验收代理逐条跑并出证据）

- **V1 ABI**：`ZrUpscalerCaps` 末尾追加 `fsr4ProbeMode`（structSize 协议一致，36B）；`ZR_ApiVersion` 1→2；全部消费方同步（test_smoke / `ZrPlugin.ExpectedApiVersion` / ZrPluginTests）；旧 dll + 新 C# 的 `-3` 优雅降级路径未被破坏
- **V2 三态语义**：`upscaler_caps.cpp` 填装——`fsrRuntimePresent=0`→2；真设备建成功→0；回退→1；缓存透传且缓存语义未改
- **V3 绑定**：`ZrNative.cs` 镜像字段 + structSize 动态值（无写死数字）
- **V4 UI 映射**：`BuildFsr4StatusText` 判定序与简报一致；未知值兜底为「未检测」；「本机不支持」是唯一合法否定（probeMode=0 且 runtime 在且 4.x 未枚举）；FSR3/DLSS 行无来源否定声明已修
- **V5 审计抽查**：`fg_ffx.cpp:125-127`/`fg_ffx.h:161-163` 移交项已改事实陈述；全部 3 个 `QuerySupport` 调用点传真设备（逐一确认）；审计清单 15 项中随机抽 3 项复核（结论与代码一致）
- **V6 测试**：`build_native.ps1` 全绿；repo 根跑 `zrender_test.exe --success=false` ≥351 用例 0 failed；EditMode 本任务 4 fixture 全绿（UpscalerSettingsTests 16/16、SettingsRegistryTests 14/14、ZrUpscalerSettingsTests 8/8、ZrPluginTests 5/5，以 results.xml 为证或重跑过滤）；**1 failed 归因核对**——确认 `CsvImporterTests.ImportRows_AnnotatedTypeMismatch_IsReported` 位于 A-CONF-01 活动区且该区存在未提交 WIP（git status/diff），不计入本任务
- **V7 范围纪律**：B-FSR-02 的 git diff 文件集与施工 numstat 一致；**未触碰** `ZFrame/Data/**`（A-CONF-01 WIP）、`Docs/TECH-VALIDATION.md`（B-PLANET-01 产物）、TASKS/HANDOFF/WORKLOG
- **V8 规范**：UTF-8 无 BOM / LF；/WX 编译零警告
- **V9**：`zrender.dll` 两侧 sha256 一致并已部署
- **V10**：施工报告四节结构完整（简报/施工报告/验收清单/验收结论由验收补末节）

验收输出 PASS/FAIL + 逐条证据；EditMode 全量的 1 failed 按归因单独标注。

## 验收结论（验收代理独立复跑，2026-11-14 02:05）

**总结论：PASS**（V1–V10 全过；1 项备注提请主控归因、1 项 MISSING 不阻塞，见末尾备注）。

| # | 结论 | 证据（验收代理独立采集） |
|---|---|---|
| V1 | **PASS** | `zplugin.h:87-107`：`ZrUpscalerCaps` 9 字段（v1 前 8 字段 + 末尾 `fsr4ProbeMode`），ABI 历史注释 32B→36B；`zplugin.cpp:30` `return 2u`；structSize 校验链动态：`zplugin.cpp:265` `sizeof(ZrUpscalerCaps)` 比对 + `:266` 命中 `ZR_UPSCALER_ERR_SIZE(-3)`；消费方同步：`test_smoke.cpp:18-22`（CHECK 2u）、`ZrPlugin.cs:14`（ExpectedApiVersion=2）、`ZrPluginTests.cs:13-17`（断言 2u）；`-3` 优雅降级未破坏：`ZrUpscalerSettings.cs:98-102`（code!=0 → WarnOnce+default，不炸 UI），EditMode `Stub_NativeSizeError_QueryCaps_ReturnsUnavailableWithoutThrow` = Passed |
| V2 | **PASS** | `upscaler_caps.cpp:99-103`：`fsrRuntimePresent==0 → mode=2`；`probeDevice!=nullptr → 0`；否则 `1`；`:89` `fsr4Available = fsrRuntimePresent && ZupFsr4Probe(probeDevice)`（短路——运行时缺席则枚举未执行，与 mode=2 自洽）；`zupscaler.h:76` 内部档案 NSDMI `=2`；缓存语义零改动：`zplugin.cpp:196/221-223` `g_upscalerCapsReady` 门控未动，diff 仅 +1 行透传（`:278`） |
| V3 | **PASS** | `ZrNative.cs:184-203`：`ZrUpscalerCapsNative` 末尾镜像 `Fsr4ProbeMode`（36B @v2 注释）；`ZrUpscalerSettings.cs:94` `Marshal.SizeOf(typeof(ZrUpscalerCapsNative))` 动态取值——grep C# 绑定/运行时无写死 size 数字；`IUpscalerSettings.cs:63-75` 托管档案镜像 + 可信度判读 doc |
| V4 | **PASS** | `UpscalerSettingOptions.cs` `BuildFsr4StatusText` 判定序：Available=false→「未检测（原生内核未启用）」；Fsr4Available→「可用」（如实不编版本号）；!FsrRuntimePresent→「运行时缺失（FFX DLL 未找到）」；ProbeMode!=0→「未检测（探测回退，结果不可信）」（未知值兜底，99/uint.MaxValue 覆盖）；仅 probeMode=0∧运行时在∧4.x 未枚举→「本机不支持」（唯一否定路径）。FSR3/DLSS 行 `MarkIfUnsupported(name,supported,probed)`：!probed→「未检测（原生内核未启用）」。与简报映射规则一致；表驱动用例 9 行全绿（V6） |
| V5 | **PASS** | 移交项已改事实陈述：`fg_ffx.cpp:125-131`、`fg_ffx.h:161-165`（引用 B-FSR-01 实测 null=2 vs 真设备=4 含 4.1.2，FG 族未做双设备对照→null 口径只能当"未检测"）；QuerySupport 全部 3 个调用点逐一核实传真设备：`zrhi_d3d12_window.cpp:10536`（`device->D3D12Device()`）、`zfg_probe_wrap.cpp:1126`（ID3D12CommandQueue::GetDevice 所得）、`zfg_probe_selftest.cpp:203`（D3D12CreateDevice 建 wrapDevice）——无一传 null；审计清单抽查 4 项（A1 zplugin.h 注释、A7 窗口 caps 行+`fsrRuntime=%d`、A8 三态映射、A13 IUpscalerSettings doc）与代码一致；A15 复核：全仓 grep「与真机一致」仅剩更正后的引述性文字，fsr_upscaler.cpp 唯一「保守」命中为资源状态缺省（非能力声明，A5 结论成立） |
| V6 | **PASS** | 独立复跑 `Tools/build_native.ps1`：ALL GREEN（configure+/W4 /WX build+doctest+deploy 4/4），`351 cases / 778,743 assertions, 0 failed, 0 skipped`（B-FSR-01 基线 350/778,737 → 只增不减）；repo 根独立复跑 `zrender_test.exe --success=false` exit=0 同数；EditMode 以 `TempLogs/runtests_editmode.xml`（01:57:31Z，在本任务全部改动之后、任务文件集 `-newer` 复核零改动）为证：**total=1167 passed=1167 failed=0**，4 fixture 全绿：UpscalerSettingsTests **16/16**（含 `Fsr4Status_TableDriven_ThreeStatesAndFallbacks`）、SettingsRegistryTests **14/14**、ZrUpscalerSettingsTests **8/8**、ZrPluginTests **5/5**（含 `EnsureLoaded_ReturnsApiVersion2`）。**1 failed 归因核对成立且已消解**：施工期 01:53:52Z 那轮 `CsvImporterTests.ImportRows_AnnotatedTypeMismatch_IsReported` 失败——该文件属 A-CONF-01 活动区（git diff `+58` 行未提交 WIP），归因成立；01:57:31Z 重跑中该用例已转绿（A-CONF-01 后续施工修复），本轮 1167 全绿，不计入本任务（与本任务文件集零交集） |
| V7 | **PASS**（含 1 备注） | git status tracked 文件集与「本任务 21 文件 + A-CONF-01 WIP（ZFrame/Data/** 7 文件 + enemy.csv）+ B-PLANET-01（Docs/TECH-VALIDATION.md）」精确匹配（程序化 diff 比对 EXACT_MATCH）；HANDOFF/WORKLOG 未触碰；未跟踪文件全部归属他任务日志/P2 设计稿/A-CONF-01 产物。**备注**：`TASKS.md` 含 B-FSR-02 行状态更新（todo→doing(施工完成,验收中;ApiVersion 2)，与 A-CONF-01 行同批更新）——施工铁律禁改看板，疑为主控派发验收时的动作，请主控归因确认（内容仅为状态行，无失实陈述） |
| V8 | **PASS** | 20 个本任务代码文件 `git ls-files --eol` 全部 `i/lf w/lf` + `.gitattributes` `* text=auto eol=lf`；无 BOM（首 3 字节均为内容前缀，非 EF BB BF）；/W4 /WX 下 ALL GREEN = 零警告为编译强制（warning 即 error） |
| V9 | **PASS** | `sha256sum` 双侧一致：`776ffedc49120cdba2ca973a8d72ec92f7d1a0db8aa38f35fb45544e6de359a1`（Native/build/bin/Release 与 UnityProject/Assets/Plugins/ZRender），与施工报告 `776ffedc…e359a1` 一致；本次验收复跑构建 4/4 步再次自动部署 |
| V10 | **PASS** | 简报/施工报告/验收清单三节齐备；本「验收结论」节由验收代理追加于末节（本文件为本任务唯一被验收代理修改的文件） |

**真机复核（验收代理独立跑）**：`zrhi_d3d12_window.exe --fsr4`（02:03，exit=2=FSR4 不可用为受限非缺陷，日志 `TempLogs/fsr4_fsr02_acceptance_run.log`）：caps 行 `vendor=0x1002 device=0x15BF fsrRuntime=1 fsr4Available=0 fsr4ProbeMode=0 nvidia=0 dlssRuntime=0`——780M 真设备路径 probeMode=0 实证（与 doctest 780M 专属断言互证）；UNAVAILABLE 判定行 `fsrRuntime=1` 如实区分"DLL 在位、确系硬件无 4.x"。

**MISSING 与备注（给主控）**：
1. **CI（GitHub Actions L1）未实跑**——本机不可代验（同施工报告遗留，双方一致）。无硬件分支逻辑已被 doctest 9b 节 vendorId==0 分支（mode!=0 ∧ fsr4=false ∧ runtime?1:2）+ cabi 无适配器→mode=1 覆盖，风险低，待 CI 轮确认。
2. **TASKS.md 归因**（V7 备注）：B-FSR-02 行状态行被更新；若非主控所为则属施工越界，需主控裁决；不影响代码与测试结论。
3. **移交项不阻塞**：FG 族双设备阳性对照（待对端机补跑 `--fg`，随 B-PROBE-01）、provider 版本串 UI 读口（需新 C ABI 导出）均为如实登记的后续任务。
4. EditMode 全量已 1167 全绿（01:57 轮），施工报告 §6.1 建议的"A-CONF-01 闭环后复跑"条件已自然满足，无需再等。
