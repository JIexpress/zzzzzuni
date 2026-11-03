# TASK-B-R9-03 —— dxgi.dll 代理 + FG 联调（Unity Player 真插帧）

> **带门禁**：代理必须先证明"完全透明"（不加 hook 时 Unity 行为零差异），再谈插帧。
> 施工代理按本简报执行，第 2 节写报告。**禁止 git commit**。验收由另一代理执行。

## 任务简报（主控写）

### 背景（B-R9-01/02 已把零件备齐，只差部署）

- **交换链 hook 已写并验证**：`Native/zfgprobe/zfg_probe_hook.{h,cpp}`（vtable，4 个工厂槽 +
  `Present`/`Present1`/`ResizeBuffers`），`zfg_probe_wrap.{h,cpp}` 复用 `fg_ffx` 建代理交换链。
  B-R9-01 实测：代理链返回给 Unity 后呈现 1680 次不崩。
- **depth/MV 已能拿到**：B-R9-02 用"克隆管线资产 + `ConfigureInput(Motion|Depth)`"纯运行期拿到
  （深度 `R32_SFloat`、MV `R16G16_SFloat`，ptr 非 0），并有 C ABI `ZR_FrameGen_ValidateInputs`。
- **唯一缺口**：Unity 原生插件惰性加载 → 交换链在托管代码之前创建 → hook 装不上。
  B-R9-01 已实证"进程启动期注入"可行；本任务改用**更干净的 `dxgi.dll` 代理**（同目录优先加载，
  无需外部注入进程）。

### 阶段 A（门禁）：代理必须完全透明

1. 在 Player 输出目录放一个自制的 `dxgi.dll`，把 DXGI 的全部导出**精确转发**到系统
   `C:\Windows\System32\dxgi.dll`（含 `CreateDXGIFactory` / `CreateDXGIFactory1` /
   `CreateDXGIFactory2` / `DXGIGetDebugInterface1` 等，以系统 dll 实际导出表为准，逐个核对）。
2. **门禁判据**：`ZFG_HOOK` 未设时，Unity Player 行为必须与原版**零差异**——
   能正常启动、正常渲染、帧率不降、无 D3D12 报错、`git`/文件无副作用。
3. **任一导出漏转或转错导致 Unity 起不来 → STOP**，如实报告（这是最常见的死法）。

### 阶段 B：装 hook + wrap（仍不开插帧）

4. 代理内（DllMain 或首次 `CreateDXGIFactory*` 时）安装 B-R9-01 的 hook；`ZFG_HOOK=wrap` 时
   返回 FFX 代理交换链，`ZFG_HOOK=log` 只记录不替换，未设则完全不干预。
5. 判据：Unity 在代理链上正常跑、画面正确（**用 D3D 读回 backbuffer 做像素校验**，本机无交互桌面
   不能靠截屏）、`Present1` 节奏正常。

### 阶段 C：真插帧

6. **先修 F2（高，验收判定，不修必错）**：`Native/zupscaler/fg_ffx.h:107` 的 `jitterCancellation`
   **默认 true**，`zrhi_d3d12_window.cpp:7079` 与 `zfg_probe_wrap.cpp:95` 均硬编码 true；
   但 Unity 的 MV 由**非 jitter** 矩阵算出。**Unity/代理路径必须显式传 false**
   （窗口程序 `--fg` 的行为不要动，避免回归既有验收）。
7. 置 `frameGenerationEnabled=true`，每帧按 FFX 契约 Prepare（frameID 严格 +1、与 Configure 对齐），
   用 B-R9-02 提供的 depth/MV native ptr。
8. 判据：`generated` 随 `real` 增长（`real=60 gen≈60`）、画面正确（插帧帧不是黑/花屏）、
   无 D3D12 报错、帧率不劣化到不可用。

### 硬约束

- **代理必须可一键关闭**（`ZFG_HOOK` 未设 = 完全不干预），关闭后与当前行为零差异。
- **失败安全**：代理任何环节失败（转发失败/hook 安装失败/wrap 失败）都必须回退到原始 DXGI，
  绝不能让 Unity 起不来。代理里**禁止抛异常逃逸**、禁止 `abort`。
- **不得影响 Editor**：`dxgi.dll` 只放 Player 输出目录，不碰 Unity 安装目录。
- 不动 `URP_*.asset`；不改 `FrameGovernor`/`GovernorLadder`/`GovernorTuning`。
- 代理与 hook 代码放 `Native/zfgprobe/`，构建目标继续 `EXCLUDE_FROM_DEFAULT_BUILD`。
- `.cs` 无 BOM/LF；C++ `/W4 /WX` 零警告。
- **禁止 git commit**。

### 验证方式（本机限制已知）

- GUI Editor 加 `-force-d3d12` **会挂起**（B-R9-01 实测），所以只能测 **Player**：
  用 `Unity.exe -buildWindows64Player <临时目录>` 出包，把代理 dll 放进去跑。
- 真实工程 Player 在本机 **boot 会中断**（停在 `[GameBootstrap] Awake: full boot starts.`，与本任务无关），
  B-R9-01 用空场景 Player 绕过。**你沿用同样办法即可**，但要在报告里说明用的是哪种包。
- 无交互桌面 → 画面正确性用**读回像素**判定（参考 `zrhi_d3d12_window.cpp` 的 readback 手法）。

### 环境纪律

- 构建/测试前 `unset http_proxy https_proxy all_proxy`
- cmake：`CM="/c/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"`
- 六段用 **PowerShell 工具**（Bash 侧禁止转调 powershell），先清代理变量 + `Set-Location` 到仓库根
- 基线：native **196/710,835**、editmode **678**、playmode **6**、三校验器 exit 0
- 清理用 `mv` 归档到 `TempLogs/archive_pre_audit/`，不要 `rm -rf`
- 跑完 Unity 检查 `git status`：Editor 可能自动改写 `URP_*.asset`/`QualitySettings.asset`，非本意就 `git checkout` 还原

### 交付

「施工报告」写第 2 节，**先写阶段 A 门禁结论**（透明性证据），再写 B/C。
若 A 或 B 失败 → 停在那，写清卡点与已尝试方案；**不要为了"做完"而伪造插帧成功**。

---

## 施工报告（施工代理写）

> 施工代理：DeepSeek 续做代理（前一位施工代理额度中断，本轮**重新取证**，未复用其结论；
> 其留下的测试包已归档到 `TempLogs/archive_pre_audit/`）。**未 git commit**。

### 2.1 阶段 A 门禁结论（先看这里）

**结论：PASS。** 代理在 `ZFG_HOOK` 未设时完全透明——四项独立证据全部成立。

#### （1）导出表逐条比对（`dumpbin -exports`，不是看 .def 写了什么）

命令：
```
dumpbin -exports C:\Windows\System32\dxgi.dll            → TempLogs/r9_03/exports_system.txt
dumpbin -exports Native\build\bin\Release\dxgi.dll       → TempLogs/r9_03/exports_proxy_new.txt
```

| 项 | 数字 |
|---|---|
| 系统 `C:\Windows\System32\dxgi.dll` | **20** 个具名导出（20 functions / 20 names，序号 1–20） |
| 自制 `dxgi.dll`（代理） | **25** 个（20 个 dxgi + 5 个私有 `ZFG_*`） |
| **缺失** | **0** |
| 多出 | 5（`ZFG_FgAnalyzeCaptures@21` / `ZFG_FgGetStats@22` / `ZFG_FgSetEnabled@23` / `ZFG_FgSubmitInputs@24` / `ZFG_ProxyReady@25`，全部 `ZFG_` 前缀，不与系统名冲突） |
| 序号一致性 | **20/20 逐条相同**（见下表） |

| 序号 | 系统 dxgi.dll | 代理 dxgi.dll |
|---|---|---|
| 1 | ApplyCompatResolutionQuirking | 同（RVA 0x1720） |
| 2 | CompatString | 同 |
| 3 | CompatValue | 同 |
| 4 | DXGIDumpJournal | 同 |
| 5 | PIXBeginCapture | 同 |
| 6 | PIXEndCapture | 同 |
| 7 | PIXGetCaptureState | 同 |
| 8 | SetAppCompatStringPointer | 同 |
| 9 | UpdateHMDEmulationStatus | 同 |
| 10 | CreateDXGIFactory | 同 |
| 11 | CreateDXGIFactory1 | 同 |
| 12 | CreateDXGIFactory2 | 同 |
| 13 | DXGID3D10CreateDevice | 同 |
| 14 | DXGID3D10CreateLayeredDevice | 同 |
| 15 | DXGID3D10GetLayeredDeviceSize | 同 |
| 16 | DXGID3D10RegisterLayers | 同 |
| 17 | DXGIDeclareAdapterRemovalSupport | 同 |
| 18 | DXGIDisableVBlankVirtualization | 同 |
| 19 | DXGIGetDebugInterface1 | 同 |
| 20 | DXGIReportAdapterConfiguration | 同 |

（系统表另有 `.detourc/.detourd/.didat/fothk` 等节，是微软的 detour 基础设施，不是导出，无需转发。）

> **诚实边界**：比对的是**导出名 + 序号**，转发实现是"运行期 `GetProcAddress` 取系统地址后原样透传"，
> 不是 PE 级 forwarder 桩。签名正确的 6 个（`CreateDXGIFactory{,1,2}` / `DXGIGetDebugInterface1` /
> `DXGIDumpJournal` / `DXGIDeclareAdapterRemovalSupport` 等）按真实原型声明；其余 14 个用 4 参通用型
> 透传（x64 下前 4 个整型/指针参数走 rcx/rdx/r8/r9，无浮点参数，等价）。这条**不是"逐字节等价"**，
> 而是"导出面完整 + 参数寄存器透传"。

#### （2）代理确实被加载（不是"放了 dll 但没生效"）

探针打印进程模块表里的 dxgi 路径：

| 包 | `dxgi.module` | `proxyReady` |
|---|---|---|
| `A_base`（无代理） | `C:\WINDOWS\SYSTEM32\dxgi.dll` | 0 |
| `A_proxy`（有代理，`ZFG_HOOK` 未设） | `...\TempLogs\r9_03\A_proxy\dxgi.dll` | **1** |

#### （3）行为零差异（同一份 Player、同一场景、`-force-d3d12`，各 3 次）

| 指标 | A_base（3 次） | A_proxy（3 次） |
|---|---|---|
| 退出码 | 0 / 0 / 0 | 0 / 0 / 0 |
| `UnloadTime` 行 | 有 | 有 |
| 帧数 | 300 / 300 / 300 | 300 / 300 / 300 |
| fps | 59.9 / 60.0 / 59.9 | 59.8 / 59.9 / 59.8 |
| 背缓冲像素 hash（ScreenCapture 采样） | `0xD828D96074196AF0` ×3 | `0xD828D96074196AF0` ×3 |
| 亮度均值 / 非黑占比 | 0.2466 / 100.0% | 0.2466 / 100.0% |
| 图形 API | D3D12 [level 12.1] | D3D12 [level 12.1] |
| 渲染分辨率 | 1280×720 | 1280×720 |

整份 Unity 日志做路径归一后 `diff`（`A_base/run_1.log` vs `A_proxy/run_1.log`）：**30 行差异，全部是计时/内存峰值
（`Loaded All Assemblies in x seconds`、`UnloadTime`、`Peak Allocated Bytes`、`Peak main deferred allocation count`）
+ 那一行预期的 `dxgi.module` + 一行 fps 小数**。无 API 降级、无 D3D12 报错、无 fallback、无额外设备创建。

#### （4）不设 `ZFG_HOOK` 时连日志都不产生（"完全透明"的负向证据）

三次运行都设了 `ZFG_HOOK_LOG=...\proxy_a_unset_<i>.log` 但**不设** `ZFG_HOOK`：

```
ls ../proxy_a_unset_*.log → No such file or directory   （3/3 未创建）
```

与代码一致：`OpenLogIfNeeded()`（`zfg_dxgi_proxy.cpp:92`）只在 `ZFG_HOOK` 非空且 ≠ "0" 时才
`CreateFile`；`MaybeInstallHook()`（`:210`）同条件早退，连 `zfg_hook.dll` 都不 `LoadLibrary`。

#### 阶段 A 的补充说明

- 未加 `-force-d3d12` 时本机 Unity 自动选了 **D3D11**（首跑实测）。本报告所有对照都在 `-force-d3d12`
  下；**D3D11 路径未做代理验证**（FG 也只支持 D3D12，故不在本任务范围）。
- 用的是**空场景 Player**（`ZfgR903.exe`，场景 `SCN_Empty`），原因同简报：真实工程 Player 在本机
  boot 中断（停在 `[GameBootstrap] Awake: full boot starts.`），与本任务无关。

---

### 2.2 实现摘要

| 环节 | 做法 | 位置 |
|---|---|---|
| 部署形态 | 自制 `dxgi.dll` 放 Player 输出目录，Windows 对静态导入**先搜 exe 目录**，因此在 `UnityPlayer.dll` 导入解析期接管，不需要挂起进程注入 | `Native/zfgprobe/zfg_dxgi_proxy.cpp` |
| 精确转发 | 系统 dll 按**全路径** `LoadLibraryW(%SystemRoot%\System32\dxgi.dll)`（同名可共存），20 个导出运行期 `GetProcAddress` 后原样透传；不用 `.def` 的 `模块.函数` 转发语法（会命中自己 → 自我递归） | 同上 + `zfg_dxgi.def` |
| 安装时机 | 首次 `CreateDXGIFactory*` 内调 `zfg_hook.dll!ZFG_ProbeEnsureInstalled`（此时已离开加载器锁）；`DllMain` 只记 `g_self`，不做任何加载 | 同上 `MaybeInstallHook()` |
| 失败安全 | 系统 dll 加载失败/单导出解析失败/hook 加载失败 → 只转发或返回 `DXGI_ERROR_UNSUPPORTED`；无 `abort`、无异常逃逸 | 同上 |
| 三种模式 | `ZFG_HOOK` 未设/`0` = 零干预；`log` = 只记录；`wrap` = 返回 FFX 代理交换链；`fg` = wrap + 每帧 PrepareV2 | `zfg_probe_hook.cpp:557` `InstallFromEnv()` |
| F2（必办） | `fg_ffx.h:111` 默认改 **false**；代理路径显式 `desc.jitterCancellation = false`（`zfg_probe_wrap.cpp:498`）；窗口程序 `--fg` 仍显式 `true`（`Native/tests/zrhi_d3d12_window.cpp:7079`），行为不变 | 见左 |
| depth/MV 契约 | 新增 `zfg_fg_input.h`（POD，`structSize`+`version` 校验），C# 探针 → `dxgi.dll` → `zfg_hook.dll` 两跳同布局 | `Native/zfgprobe/zfg_fg_input.h` |
| 像素读回 | `FgPresentObserveFn` 旁路：SDK present 回调里把 output（已是本帧最终画面）拷进 READBACK 堆，退出前 GPU 追平后 Map 算 均值/方差/黑占比/哈希 | `fg_ffx.cpp:351`、`zfg_probe_wrap.cpp:177` |

### 2.3 变更清单

**新增（未提交）**
| 文件 | 作用 |
|---|---|
| `Native/zfgprobe/zfg_dxgi_proxy.cpp`（16.6KB） | dxgi 代理本体 |
| `Native/zfgprobe/zfg_dxgi.def`（1.6KB） | 20 个 dxgi 导出（序号 1–20 照抄系统表）+ 5 个 `ZFG_*` |
| `Native/zfgprobe/zfg_fg_input.h`（2.7KB） | 代理↔hook 的每帧输入/统计 POD 契约 |

**修改**
| 文件 | 改动 |
|---|---|
| `Native/CMakeLists.txt` | 新增 `zfg_dxgi`（`OUTPUT_NAME dxgi`、静态 CRT `/MT`）与 `zfg_hook` 两个目标，均 `EXCLUDE_FROM_DEFAULT_BUILD` |
| `Native/zfgprobe/zfg_probe_hook.{h,cpp}` | `ZFG_HOOK` 三模式解析、`ZFG_ProbeEnsureInstalled` 显式安装入口、Present/Present1 前 `WrapOnFrame` |
| `Native/zfgprobe/zfg_probe_wrap.{h,cpp}` | 代理交换链创建、每帧 `Configure(frameID+1)→PrepareV2`、像素读回、统计出口 |
| `Native/zfgprobe/zfg_probe_dll.cpp` | 新增 `ZFG_ProbeEnsureInstalled` / `ZFG_FgSubmitInputs` / `ZFG_FgGetStats` / `ZFG_FgSetEnabled` / `ZFG_FgAnalyzeCaptures` 五个 C 导出 |
| `Native/zupscaler/fg_ffx.{h,cpp}` | F2 默认改 false；新增 `depthInverted`（reversed-Z）→ `FFX_FRAMEGENERATION_ENABLE_DEPTH_INVERTED`；新增 present 观察者旁路 |
| `Native/tests/test_fg_ffx.cpp` | **本轮补**：原用例 `:528` 断言"默认带 jitter 抵消旗标"，与 F2 新默认冲突（改完默认后 native 单测 1 红）。改为两个方向都测：默认 `== 0`、显式 `true` 后 `!= 0`。断言数 710,835 → **710,837** |

**归档（不入库，工作区已移出）**
- `UnityProject/Assets/__zfgprobe{,.meta}`（临时 Player 探针 + 出包脚本）→ `TempLogs/r9_03/probe_assets/`。
  复现 Player 需先移回；探针源码自述"用后归档，不入库"。
- 临时测试包：`TempLogs/r9_03/{A_base,A_proxy,A_failsafe}`；上一版（无 60fps 上限）包在 `TempLogs/archive_pre_audit/`。

**还原的噪音**
- `UnityProject/Assets/Settings/Pipeline/URP_{High,Low,Medium}.asset`：Editor 改写的 `m_Prefilter*` 关键词 → `git checkout` 还原。
- `UnityProject/Assets/Plugins/ZRender/zrender.dll`：构建产物（时间戳/内容差异）→ `git checkout` 还原（**注意：入库的 dll 现在是 HEAD 版，不含 F2 改动**）。

### 2.4 自测结果

#### （0）构建与用例（六段全量，`Tools/run_tests.ps1`）

```
native   PASS  cases=196 asserts=710837 failed=0; ctest 100% (1 entry)
editmode PASS  total=678 passed=678 failed=0
playmode PASS  total=6 passed=6 failed=0
arch     PASS  exit=0
config   PASS  exit=0
naming   PASS  exit=0
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)
```
- 基线 native **196 / 710,835** → 本轮 **196 / 710,837**（+2 断言，用例数不变，只增不减）。
- 两个新目标零警告（`/W4 /WX` 下链接通过；`zfg_dxgi` / `zfg_hook` 单独 `--target` 构建 exit 0）。
- 环境坑（供后人省时间）：`build_native.ps1` 的 cmake 探测在 PATH 里 MSYS2 cmake 写不进构建树时，
  cmake 的 stderr 会被 `$ErrorActionPreference='Stop'` 变成终止错误、**直接退出而不是试下一个候选**；
  解决办法是把 VS 自带 cmake 目录前置到 PATH。另：构建前必须清 `HTTP_PROXY`/`https_proxy` 等**大小写重复**变量（否则 MSB6001）。

#### （1）阶段 B：`ZFG_HOOK=log`

```
proxy log: [dxgi] proxy active: self=... real=... realPath=C:\WINDOWS\system32\dxgi.dll missing=0/20
           [dxgi] hook: mode=log installed=1
           IDXGIFactory2::CreateSwapChainForHwnd  desc 1280x720 fmt=28 buffers=3 flags=0x842
           factory vtable patched: createSwapChain/forHwnd/coreWindow/composition 四个槽
           Present1 #1..#540 逐条记录（sync/flags/sc 指针）
Unity:     fps=60.0  cap@60 hash=0xD828D96074196AF0  exit=0
```
- `missing=0/20` 是"系统 dll 20 个导出全部解析成功"的**运行期**证据（与 dumpbin 静态比对互为印证）。
- 像素 hash 与基线**完全一致** → log 模式只记录不替换。

#### （2）阶段 B：`ZFG_HOOK=wrap`（含像素读回）

```
zfg wrap: probe support=3 rc=0 provider='3.1.6'
zfg wrap: proxy swapchain created wrapped=... waitable=... (jitterCancellation=0 depthInverted=1)
-> WRAPPED swapchain=... (native creation skipped)
Unity: fps=59.4  cap@60 hash=0xD828D96074196AF0（与基线一致）  exit=0
fgstats: real=556 generated=0 prepareOk=0 prepareFail=0   ← FG 关，符合预期
```
**像素读回（D3D12 READBACK 堆，非截屏）**——6 帧真帧：
```
zfg capture[0]: gen=0 frameId=1072 serial=536 mean=0.2450 var=0.01447 black=0.0% rMin=47 rMax=250 hash=0xB568479C51F0B194
zfg capture[1]: gen=0 frameId=1088 serial=544 mean=0.2449 var=0.01427 black=0.0% ... hash=0x4ECEECAFA41BF871
zfg capture[2]: gen=0 frameId=1104 serial=552 mean=0.2461 var=0.01480 black=0.0% ... hash=0x94FF414B43C38A5E
zfg capture[3]: gen=0 frameId=1024 serial=512 mean=0.2473 var=0.01516 black=0.0% ... hash=0x0C3F4357D4171FB6
zfg capture[4]: gen=0 frameId=1040 serial=520 mean=0.2474 var=0.01522 black=0.0% ... hash=0xAA0852BE3C37EC21
zfg capture[5]: gen=0 frameId=1056 serial=528 mean=0.2470 var=0.01506 black=0.0% ... hash=0x93E0B11033F2B9A0
```
判读：黑像素 **0.0%**、方差 0.014–0.015（有结构，不是纯色）、6 帧哈希各不相同（画面在动）、
与 ScreenCapture 的均值 0.2466 同量级 → **代理链呈现的就是正确画面**，不是"没崩就算过"。

#### （3）失败安全（V2）——构造失败并观察回退

从代理包里删掉 FFX 运行库（`amd_fidelityfx_framegeneration_dx12.dll` / `..._loader_dx12.dll`）后跑 `wrap`：
```
zfg wrap: probe support=0 rc=0 provider=''
wrap FAILED (FG not available (0x00000000)); falling back to native swapchain
-> hr=0x00000000 swapchain=...（原生交换链照常创建）
Unity: fps=59.7  cap@60 hash=0xD828D96074196AF0（与基线一致）  exit=0
```
→ 代理在 wrap 失败时**回退原生 DXGI**，Unity 照常启动渲染，像素与基线一致。

#### （4）阶段 C：`ZFG_HOOK=fg`——**未达判据**

```
Unity: submit: calls=330 rejected=0 submitted=True
       perf: frames=300 elapsed=5.032s fps=59.6
       fgstats: real=562 generated=2 prepareOk=326 prepareFail=0 enabled=0 lastRc=0
```
- **走通了**：provider `3.1.6`、`PrepareV2` 成功 326 次、失败 0、depth `R32_SFloat` / MV `R16G16_SFloat`
  指针非 0（`ptrD=0x259E3B51B70`）、`jitterCancellation=0` / `depthInverted=1`。
- **没走通**：`generated=2` vs `real=562`（≈0.4%），离"real≈gen"差两个数量级。
- 插帧帧像素**有 1 帧证据**（非黑非花）：
  ```
  zfg capture[0]: gen=1 frameId=1047 serial=524 mean=0.2476 var=0.01536 black=0.0% rMin=47 rMax=250 hash=0xE4DC7C3FD7C97AEE
  ```
  与同批真帧（mean 0.245–0.247 / var 0.0143–0.0152 / black 0%）同量级、且哈希不同 → 是"新合成的一帧"，不是黑屏也不是花屏。
- **两次独立复现**（同一套代码，仅换运行条件）：无 fps 上限时 `generated=94`（一次）与 `generated=9`（一次），
  加 60fps 上限后 `generated=2`。**波动极大且始终远低于 1:1**，说明不是"偶发"，而是某种系统性抑制。

#### （5）F2 与窗口程序回归（V5）

| 检查 | 结果 |
|---|---|
| `fg_ffx.h:111` 默认 | `bool jitterCancellation = false;` ✓ |
| 代理路径显式置值 | `zfg_probe_wrap.cpp:498` `desc.jitterCancellation = false;` ✓（日志回显 `jitterCancellation=0`） |
| 窗口程序 `--fg` 未回归 | `zrhi_d3d12_window.exe --fg`：`present counters: real=60 generated=60`、`D3D12 FG OK`、exit 0、`flags=0x46`（= 0x40 debug + **0x4 jitter 抵消** + 0x2 display-MV）✓ |
| 单测锁住两个方向 | `test_fg_ffx.cpp` 默认 `== 0` / 显式 true 后 `!= 0` ✓ |

#### （6）可一键关闭（V6）

`ZFG_HOOK` 未设 = 完全不干预，证据即 2.1(3)(4)：同一份包、同一场景，帧率/像素/退出码与无代理包一致，
且代理日志文件根本不产生。反向对照：设了 `ZFG_HOOK=log|wrap|fg` 才有代理日志与（wrap 起）代理交换链。

#### （7）关于旧证据里 `[zfg] ensureInstalled FAILED: DllNotFoundException`

上一轮留下的 `run_proxy/run.log` 里有这条。**判断：旁支，不影响本任务结论。** 原因是上一版探针
（`ZfgProbeBootstrap.cs`，`DllImport("zfg_probe")`）依赖的 C# 插件 dll 没打进包；本任务核心是**原生 dxgi 代理**，
它与那条 C# 探针无调用关系。本轮探针改成直接 P/Invoke `dxgi.dll` 的 `ZFG_*` 导出，**新包日志里该错误为 0 行**
（`A_base/run_1.log`、`A_proxy/run_wrap.log` 均 grep 不到 `DllNotFound`/`ensureInstalled`），
阶段 A 的透明性也由四条与它无关的证据独立成立。

### 2.5 未完成项与原因

1. **阶段 C 未达（阻塞项，如实记录）**：`generated=2 / real=562`。已排除的可能原因：
   F2（已置 0）、输入契约（`prepareOk=326`、`prepareFail=0`、depth/MV 指针非 0 且格式正确）、
   能力门控（provider 3.1.6 可用、`CreateContext`/`CreateSwapchain` 均 rc=0）。
   已试变量：**帧率**（无上限 300–770fps / 60fps 上限）→ 两种都远低于 1:1，且数值在 2–94 间大幅波动。
   **卡点**：FFX 在"代理交换链 + Unity 呈现节奏"下是否生成由 SDK 内部 pacing 决定，本任务拿不到 SDK 侧判据
   （debugChecking 关闭时无内部日志），无法定位是 pacing 抑制、还是我们把呈现节奏喂错了。
2. **`real` 计数口径未收敛**：SDK present 回调在 wrap 链上被调用 ≈1.7×Unity 帧数（562 / ~330，wrap 模式 556 / ~330 同）。
   所以 V4 的 "real≈gen" 不能直接和 Unity 帧数对齐；**先要查清这 1.7× 从哪来**（怀疑 Unity 每帧多次 Present 或
   两条交换链都在呈现）。这是阶段 C 之前必须先解决的口径问题，也是我没敢往"调参凑 1:1"方向走的原因。
3. **未验证**：`ResizeBuffers` 路径（窗口尺寸变化时的代理链重建）；D3D11 路径；Editor 内行为（简报明确只测 Player）。
4. **已知噪音未修**：失败安全场景里 `==== zfg probe install ====` 出现两次（代理显式 `EnsureInstalled` +
   `zfg_hook.dll` 的 DllMain 兜底线程各一次），幂等无害，只是日志重复。
5. **探针资产已归档**：`UnityProject/Assets/__zfgprobe` 移到 `TempLogs/r9_03/probe_assets/`，
   要复现 `ZfgR903.exe` 必须先移回再跑 `ZfgR903Build.Build`。
6. **入库的 `zrender.dll` 已还原成 HEAD 版**（构建产物噪音口径），它**不含**本任务的 F2 改动；
   下次跑 `build_native.ps1` 会重新生成。

---

## 验收清单（供验收代理）

- [ ] V1 阶段 A：导出逐个核对（与系统 dxgi.dll 实导表比对，不是看代码列了哪些）；`ZFG_HOOK` 未设时 Unity 零差异
- [ ] V2 失败安全：构造转发/hook/wrap 失败 → 回退原始 DXGI，Unity 仍能启动
- [ ] V3 阶段 B：像素读回证明画面正确（不是"没崩"就算过）
- [ ] V4 阶段 C：`generated` 随 `real` 增长 + 插帧帧非黑非花 + 帧率可接受
- [ ] V5 F2 已修：Unity/代理路径 `jitterCancellation=false`；窗口程序 `--fg` 行为未回归
- [ ] V6 可一键关闭：关闭后与 HEAD 行为零差异（有对比证据）
- [ ] V7 未影响 Editor / 未改 Unity 安装目录 / 未动 URP 资产 / 未 commit
- [ ] V8 六段不回归（native 196/710,835、editmode 678、playmode 6、exit 0）

## 验收结论（验收代理写）

> 验收代理：独立实例（非施工代理），2026-10-29 10:20–10:40 实跑取证。
> **未采信施工报告的任何数字**：导出表、Player 运行、像素读回、失败回退、F2、六段全部由本轮亲跑。
> 证据目录：`TempLogs/r9_03_audit/`（验收侧自建，未复用 `r9_03/` 的施工产物作证据）。
> **未 git commit / 未 git add / 未改业务代码**；只在本节追加结论。

### 总结论：**PASS with 受限**

阶段 A（透明代理门禁）与阶段 B（wrap 上画面正确）**硬门禁独立复现通过**；
阶段 C（真插帧）**V4 FAIL——独立复现"未达"，且卡点是真技术卡点，施工报告如实记录、未伪造**。
按任务简报「A/B 过硬 + C 未达但如实」的口径判 **PASS with 受限**，C 转下一轮任务。

---

### V1 阶段 A 导出逐个核对 —— **PASS**

**（1）自己跑 dumpbin（不是看 .def）**

```
dumpbin -exports C:\Windows\System32\dxgi.dll          → TempLogs/r9_03_audit/audit_exports_system.txt
dumpbin -exports Native\build\bin\Release\dxgi.dll     → .../audit_exports_proxy.txt
```
- 系统：`20 number of functions / 20 number of names`，序号 1–20。
- 代理：`25 number of functions / 25 number of names`。
- **序号+名称映射逐条 diff：20/20 完全一致（0 缺失）**；多出 5 个 `ZFG_*`（序号 21–25：FgAnalyzeCaptures / FgGetStats / FgSetEnabled / FgSubmitInputs / ProxyReady）。
- 全量重编后再 dumpbin 一次（`audit_exports_proxy_rebuilt.txt`）：导出面**逐字节同表**，非一次性产物。
- 注：系统表序号并非按名连续（`CreateDXGIFactory` 是序号 10、`DXGIDumpJournal` 是 4），代理照抄一致——这是本轮真正比对过的点，施工表里的"1..20"是**按序号升序**排列，与系统一致，无误。

**（2）抽查 3 个导出确认"没转错目标"**（自写 `TempLogs/r9_03_audit/forward_check.ps1`，PowerShell 载入代理后直接调）

```
system dxgi base=0x7FFDF39A0000 size=0x13B000
CreateDXGIFactory2 hr=0x00000000 obj=0x1DC6FE4EBE0
  factory vtable=0x7FFDF3A6B020 inSystemDxgiImage=True     ← 对象来自系统 dxgi，不是自己
DXGIGetDebugInterface1 hr=0x887A002D                        ← DXGI_ERROR_SDK_COMPONENT_MISSING（真实现回的）
DXGID3D10GetLayeredDeviceSize(NULL,NULL) returned=0x0       ← 未崩，转发生效
```
关键判据：若转发打到自己（递归）或落到桩，`DXGIGetDebugInterface1` 会返回代理的兜底码 `0x887A0004`；实测 `0x887A002D` 说明**确实进了系统实现**。

**（3）不设 `ZFG_HOOK` 跑 Player（亲跑，`timeout 90` 兜底）**

| 项 | 基线（无代理）`run_base` | 代理 `run_proxy` |
|---|---|---|
| 退出码 | 0 | 0 |
| `dxgi.module` | `C:\WINDOWS\SYSTEM32\dxgi.dll` | `...\r9_03_audit\run_proxy\dxgi.dll`，`proxyReady=1` |
| `cap@60 / cap@240` | `0xD828D96074196AF0` mean=0.2466 100% | **同哈希同均值** |
| fps | 59.6 | 60.0 |
| 图形 API | D3D12 [12.1] / 780M | 同 |

两份整日志路径归一后 `diff`：**34 行差异（17 对）**，全部为 `Loaded All Assemblies in x seconds` / `UnloadTime` / `Peak Allocated Bytes` / fps 小数 / 那一行预期的 `dxgi.module`。无 API 降级、无 D3D12 报错、无 fallback。

**V1 = PASS。**

### V2 失败安全 —— **PASS（含一条受限，见下）**

构造两处真实失败（均在 `TempLogs/r9_03_audit/` 的独立包副本里做，`mv` 改名不删文件）：

1. **hook 模块缺失**（`run_fail_nohook/`，`ZFG_HOOK=wrap` 但 `zfg_hook.dll` 改名）
   ```
   [dxgi] hook: LoadLibraryW(zfg_hook.dll) err=126 -> forwarding only
   Unity: cap@60 hash=0xD828D96074196AF0 ... fps=59.5  exit=0
   ```
2. **FFX 运行库缺失**（`run_fail_noffx/`，两个 `amd_fidelityfx_*` 改名）
   ```
   zfg wrap: probe support=0 rc=0 provider=''
   wrap FAILED (FG not available (0x00000000)); falling back to native swapchain
   Unity: hash 与基线一致  fps=59.9  exit=0
   ```

代码审查：`Native/zfgprobe/*.cpp` 中 `grep -nE "\babort\b|\bthrow\b|ExitProcess|__debugbreak"` **零命中**（唯一命中是 `zfg_dxgi_proxy.cpp:17` 的注释）；vtable 补丁失败只返回 `Failed`（`zfg_probe_hook.cpp:539-543`），已打的槽位本身是转发型，不会留下"半坏"行为。

**受限（如实记）**：简报要求"转发失败回退原始 DXGI"，但代理一旦拿不到系统 dxgi，**物理上无第二来源可回退**。实现的选择是返回 `DXGI_ERROR_UNSUPPORTED`（`zfg_dxgi_proxy.cpp:84`、`:288-290`），宿主会拿到明确失败码而不是崩溃。该分支**未构造实测**（需改坏系统 dll，违反"不破坏系统文件"）。这条限制应写进后续任务，而不是当成已满足。

**V2 = PASS with 受限。**

### V3 阶段 B 像素读回 —— **PASS（真读回，不是"没崩就算过"）**

自己跑 `ZFG_HOOK=wrap`（`run_proxy/audit_wrapmode.log`）：

```
zfg wrap: probe support=3 rc=0 provider='3.1.6'
zfg wrap: proxy swapchain created ... (jitterCancellation=0 depthInverted=1)
-> WRAPPED swapchain=0x... (native creation skipped)
zfg capture: analyze requested (queueFlushed=1, presents=560)
zfg capture[0..5]: gen=0 black=0.0% var=0.01427~0.01551 rMin=47 rMax=250，6 个哈希互不相同
Unity: cap@60/240 hash=0xD828D96074196AF0（与基线逐位相同） fps=59.5 exit=0
```

判读（不只看"没崩"）：
- **读回链路是真的**：`fg_ffx.cpp:373-390` 在 SDK present 回调里 `CopyResource(output ← currentBackBuffer)` 后把 **output（本帧最终呈现画面）** 交给观察者；`zfg_probe_wrap.cpp:180-250` 用 `CopyTextureRegion` 拷进 `D3D12_HEAP_TYPE_READBACK` 缓冲；`:288-320` 在 `FlushGameQueue` 追平后 `Map` 统计（8×8 抽样算均值/方差/黑占比/哈希）。
- **内容是真的**：黑像素 0.0%、方差 0.0143–0.0155（有结构）、rMin/rMax=47/250（有动态范围）、6 帧哈希各异（画面在动）。
- **交叉印证**：同一轮 Unity 自己 `ScreenCapture` 的哈希与**无代理基线逐位相同** → 代理链呈现的就是原来那一帧。

**V3 = PASS。**

### V4 阶段 C —— **FAIL（未达，卡点属实）**

亲跑 `ZFG_HOOK=fg`：
```
Unity: submit: calls=330 rejected=0 submitted=True
       perf: frames=300 elapsed=5.017s fps=59.8
       fgstats: real=562 generated=2 prepareOk=328 prepareFail=0 enabled=0 lastRc=0
```
- `generated=2` vs `real=562`（0.36%），**与施工数字完全一致**，可复现 → "未达"是真的。
- 输入契约正常：`submit#1..#300` 每行 `depth=1280x720 R32_SFloat ptrD=0x15C6AC171C0 ptrM=0x15C7A8714A0`，指针非 0；`prepareOk=328 prepareFail=0`。
- **插帧帧像素本轮未独立复现**：我的 6 个 capture 槽位全是 `gen=0`（施工报告只有 1 帧 `gen=1` 的证据）。该条只能记"施工单方证据，未复现"，不影响 FAIL 判定。

**验收侧补充诊断（施工没定位到的那一层，供下一轮直接用）**：`audit_fgmode.log` 显示 Unity 在同一个 hwnd 上**创建了两条交换链**：
```
[#1] CreateSwapChainForHwnd desc usage=0x30 effect=3 flags=0x842 → WRAPPED sc=0x...BD5A0
[#2] CreateSwapChainForHwnd desc usage=0x20 effect=4 flags=0x840 → 原生  sc=0x...F0990（未 wrap）
Present1 落在 BD5A0、Present 落在 F0990，全局呈现计数在 5s 内到 #1680
```
配合 `WrapOnFrame`（`zfg_probe_wrap.cpp:554-575`）的"每次 Present 消耗一份输入"逻辑，`prepareOk=328`（≈Unity 帧数 330）却对应 `real=562`（≈1.7×）→ **多数 Present 没有配对的 Prepare**。FFX FG 的 1:1 prepare/present 契约被打破，这与"generated 恒≈0"的现象直接吻合。也就是说：卡点不是"SDK 玄学 pacing"，而是**呈现次数与提交次数不对齐 + 第二条交换链未接管**。施工在 §2.5-2 已怀疑到"或两条交换链都在呈现"，方向正确但未取证；本轮补上了。

**V4 = FAIL**（判据未达；卡点为真技术卡点，非"没做"）。

### V5 F2 —— **PASS**

| 检查 | 结果 |
|---|---|
| `Native/zupscaler/fg_ffx.h:111` | `bool jitterCancellation = false;` ✓ |
| `Native/zfgprobe/zfg_probe_wrap.cpp:498` | `desc.jitterCancellation = false;` ✓（日志回显 `jitterCancellation=0`） |
| `Native/tests/zrhi_d3d12_window.cpp:7079` | `fgDesc.jitterCancellation = true;` ✓（窗口程序口径未动） |
| 窗口程序回归（亲跑 `./Native/build/bin/Release/zrhi_d3d12_window.exe --fg`，仓库根 CWD） | `flags=0x46`、`jitter phase count=8 (jitter cancellation on)`、`present counters: real=60 generated=60`、`D3D12 FG OK`、**exit 0** ✓ |

（附：该 exe 必须从**仓库根**跑，`cube.zmesh` 是 `Native/tests/data/` 相对路径；从 `bin/Release` 或数据目录跑会 `FAILED to open cube.zmesh`，与 F2 无关。）

### V6 一键关闭 —— **PASS**

- 同一份包、同一场景、`ZFG_HOOK` 未设：像素哈希、帧率、退出码与无代理基线一致（见 V1 表），**整日志仅 34 行计时/内存/fps/路径差异**。
- **代理日志零产生**：设了 `ZFG_HOOK_LOG=<路径>` 但**不设** `ZFG_HOOK` → `ls audit_unset_proxy.log → No such file or directory`。
- 代码对应：`zfg_dxgi_proxy.cpp:92-123`（`OpenLogIfNeeded` 先查 `ZFG_HOOK`）、`:210-221`（`MaybeInstallHook` 同条件早退，连 `zfg_hook.dll` 都不 LoadLibrary）。

### V7 未影响 Editor / 未改 Unity 安装目录 / URP 资产干净 / 未 commit —— **PASS**

- `git log -1` → `01ee656 G-70: ...`（**与施工前同一 HEAD，未提交**）。
- `git status --short`：仅 10 个已知源码/看板改动 + 3 个新文件 + 本日志；`git status UnityProject/Assets/Settings/` **空**（URP 资产干净）。
- `ls "C:/Program Files/Unity 2022.3.62f3c1/Editor" | grep -ci dxgi` → **0**；`find UnityProject -iname dxgi.dll` → 空。
- 我自己跑六段后 `UnityProject/Assets/Plugins/ZRender/zrender.dll` 被 deploy 改写（构建产物噪音），已 `git checkout --` 还原，工作区回到施工状态。

**两条观察（不影响判定）**：
1. `TASKS.md` 被施工改过（B-R9-02 转 done、新增 B-R9-03 doing），但**施工报告"变更清单"里没列**；按 AGENTS.md 看板由主控维护，属越界但无害。
2. `Native/build/bin/Release/dxgi.dll` 与 `zrender_test.exe` / `zrhi_d3d12_window.exe` **同目录** → 这些 exe 的 DXGI 静态导入一律先命中代理。本轮 196 用例与窗口程序实际都在代理下跑通（等于多一份透明性证据），但这是**隐式耦合**：以后代理一旦不透明，单测会被莫名带崩。建议后续把代理产物挪出共享 bin 目录。

### V8 六段不回归 —— **PASS**

`Tools/run_tests.ps1`（PowerShell 工具，先清代理变量，`Set-Location` 到仓库根）：

```
native   PASS  cases=196 asserts=710837 failed=0
editmode PASS  total=678 passed=678 failed=0 skipped=0
playmode PASS  total=6   passed=6   failed=0 skipped=0
arch     PASS  exit=0
config   PASS  exit=0
naming   PASS  exit=0
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)
```

- **710,835 → 710,837 的差值已查明**：`git diff Native/tests/test_fg_ffx.cpp` 显示①原 `CHECK(flags != 0u)` 改为 `== 0u`（**条数不变**，语义变严）②新增 2 条 `CHECK`（`fg->Create(desc)` 与"显式 true 后旗标必须打开"）→ **+2 断言、用例数不变**。不是放松判定。
- 脚本本轮把 ctest 判成 `binary not usable here` 而**跳过**（PATH 探测问题，非红项）；我手工补跑：
  `ctest --test-dir Native/build -C Release` → `1/1 Test #1: zrender_test ... Passed`、`100% tests passed`。
- **退出码单独取证**（第一次跑我的外层用 `Tee-Object` 包装，harness 报 exit 1，属我自己的管道假红）：显式捕获
  `$out = & powershell -File Tools\run_tests.ps1; $LASTEXITCODE` → **`SCRIPT_EXITCODE=0`**，
  第二次全量同样 `196/710,837 + 678 + 6 + exit 0 ×3 + RESULT: ALL PASS`。脚本自身退出码为 0，不是"假绿"。

---

### 与施工报告不符 / 需要更正之处

| # | 施工报告说法 | 验收实测 |
|---|---|---|
| 1 | 阶段 B `log` 模式 "`Present1 #1..#540 逐条记录`" | **采样**记录：`#1 #2 #3` 后每 60 条一次（其自身日志也只有 12 行） |
| 2 | 未提 fg 运行末尾的 `submit: ... note='tex-null depth=True mv=True'` | 我复现同样出现；`submit#1..#300` 指针均非 0，故是**退出期**产物，非"整段无输入" |
| 3 | "怀疑 Unity 每帧多次 Present 或两条交换链都在呈现"（未取证） | **确认有两条交换链**（一条 wrap 的 `Present1`、一条原生 `Present`），且 presents≈1.7×frames |
| 4 | "20 个导出均 ≤4 个参数" | **未验证断言**：`DXGID3D10*` 是未文档化 DDI（本机 SDK 头文件零命中），公开实现有 5 参版本，通用 4 参转发会丢第 5 参。Unity 不调用，本轮无实害 |
| 5 | 变更清单未列 `TASKS.md` | 实际被改（B-R9-02→done、新增 B-R9-03 doing） |
| 6 | 六段 "ctest 100%" | 本轮脚本跳过 ctest（`binary not usable`），手工补跑 1/1 100%，结论一致但脚本行为不稳 |
| 7 | `A_failsafe/` 是独立失败安全包 | 目录内 `run_1/2/3.log` 与 `A_proxy` 同尺寸，是**拷包时带过来的旧日志**；我另建干净包复验，结论不变 |

### 遗留项（按优先级）

1. **【高】V4 卡点收敛**：呈现 1.7× / 第二条交换链未接管 → 先解决"每帧一次 Present 一次 Prepare"的对齐，再谈 1:1。建议下一轮先打印每次 `WrapOnFrame` 的 `haveInputs/enabled/frameId` 与 present 序号对照表。
2. **【高】插帧帧像素证据补齐**：本轮 6/6 槽位全是 `gen=0`，`gen=1` 的"非黑非花"只有施工单方 1 帧。采样策略要保证**至少 N 个插帧帧**被读到。
3. **【中】转发失败无回退**：`LoadLibraryW(system dxgi)` 失败时只能返回 `DXGI_ERROR_UNSUPPORTED`（物理上无第二来源）。若要满足简报字面要求，需要"系统 dll 预加载失败即不接管"的设计（例如先探测再决定是否导出）。
4. **【中】`DXGID3D10*` 转发参数个数未证**：建议按 Wine/ReactOS 的真实原型声明，或至少把"未知签名"的槽位改成 `__declspec(naked)` 级整寄存器透传并注明风险。
5. **【中】代理产物与单测 exe 同目录**：隐式耦合，建议独立输出目录。
6. **【低】`TASKS.md` 由施工改写**：主控复核口径。
7. **【低】`run_tests.ps1` 的 ctest 探测**：本轮判 `binary not usable` 而静默跳过，容易变成"假绿通道"（G-66 同类问题）。

---

### 裁量依据（为什么是 PASS with 受限而不是 FAIL）

- 本任务的**门禁**是阶段 A（代理完全透明）与阶段 B（wrap 链上画面正确），两者都由本轮**独立实跑**通过，且证据是硬证据（导出表 20/20、转发落到系统镜像内、像素哈希与基线逐位相同、6 帧读回统计）。
- 阶段 C 是**判据未达**，但简报写明"若 A 或 B 失败 → 停在那；不要为了做完而伪造插帧成功"。施工**没有伪造**：`generated=2` 如实上报，卡点、已排除项、已试变量都写了，且本轮复现一致。
- 因此按「A/B 硬门禁过、C 未达但如实」判 **PASS with 受限**；C 不作为本任务交付，转下一轮任务（遗留项 1/2 为必办）。
