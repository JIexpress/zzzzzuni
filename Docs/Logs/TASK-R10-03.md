# TASK-R10-03 —— 修复审计 P1（原生批：代理转发 / vtable 槽 / 交换链尺寸 / Vulkan 错误处理）

- **轨道**：B / R10
- **状态**：todo
- **派发**：主控，2026-10-29
- **施工**：general-purpose 施工子代理（接手续建，2026-10-30；前代理 workbuddy 限额中断的半成品已完成审计、续建与自测）
- **验收**：general-purpose 验收子代理（独立，与施工不同代理），2026-10-30
- **来源**：`Docs/Logs/TASK-R10-02.md` 审计报告 + 其验收结论（**分级已按验收更正，以下为更正后口径**）

## 任务简报（主控写）

### 要修的 4 组（按验收建议顺序）

#### A-2（最高性价比，先做）工厂 vtable 槽装错
- **事实**：`Native/zfgprobe/zfg_probe_hook.cpp` 里 `kSlotCreateSwapChainForComposition = 17`，
  但按 SDK 10.0.26100 头文件逐槽核对：**17 实为 `GetSharedResourceAdapterLuid`，Composition 在槽 24**（`IDXGIFactory2Vtbl` 共 25 项）
- **后果**：错误槽里是真实 dxgi 指针，`PatchSlotTracked` 静默放行，日志还打印 `composition=0x…` 制造"已接管"假象
- **要求**：改常量 + 补一条**槽位守卫用例**（按 vtbl 索引断言每个被 patch 的槽对应预期函数名/序号），
  并在日志里区分"patch 成功"与"槽位校验通过"

#### A-4 交换链重建后尺寸不更新
- **事实**：`g_displayWidth/Height` 全仓仅一处赋值（`:840-841`）；`HookResizeBuffers` 只转发；`g_captureReady` 无重置点
- **后果**：① FG 永久静默失效（只一行日志）；② `CaptureObserver` 按旧尺寸对新后台缓冲 `CopyTextureRegion` → 窗口变小即越界
- **要求**：`ResizeBuffers` 后更新尺寸 + 重置 capture 状态 + 重新校验 FG 输入尺寸；**加一条"尺寸变化后 FG 仍工作/安全停用"的用例**
  （原生单测或探针均可，要能复现"窗口变小"这条路径）

#### A-1 代理转发参数不足（P1，触发面窄但 5 行可修）
- **事实**：`zfg_dxgi_proxy.cpp` 对 `DXGID3D10CreateDevice`（文档签名 7 参）只转发 4 个参数；文件头 `:11` 的"20 个导出均 ≤4 参"断言为假
- **后果**：第 5–7 参取未初始化栈槽 → `ppDevice` 写到野地址（崩溃级，但本仓/UnityPlayer 均无该导出调用点）
- **要求**：**逐导出核对系统 dxgi.dll 的真实参数个数**（不是看 .def，是看头文件/文档），补齐所有 >4 参的转发；
  修正 `:11` 的注释断言；补一条"导出参数个数表"的静态用例（表驱动，新增导出漏配即红）

#### A-5 / A-6 Vulkan 错误处理
- **A-5**：`VK_ERROR_OUT_OF_DATE_KHR` 被当"打印后继续" → 登记一个永不 signal 的信号量（另：`index` 是初始化为 0，验收更正了审计的"未写入"措辞）
- **A-6**：`vkQueueSubmit` 返回值丢弃；`UINT64_MAX` 等 fence（验收更正：最小化时返回 OUT_OF_DATE 不会阻塞，但**非最小化路径仍需检查**）
- **要求**：OUT_OF_DATE → 走"标记需重建 + 本帧跳过提交"的明确路径（不要继续用旧链）；所有 `VkResult` 非成功路径要有统一处理与日志；
  补用例覆盖"模拟 OUT_OF_DATE 后不再提交到旧链"

### 硬约束

- 只改 `Native/zfgprobe/`、`Native/zrhi/vulkan/`、`Native/tests/`、`Native/CMakeLists.txt`（如需）
- **不动 D3D12 后端既有行为**、不动 FFX 契约、不动 Unity 侧
- 每条修复都要有**能证明它修好了的证据**（新用例 / 探针输出 / 前后日志对比）
- **不许把"改了代码"当"修好了"**：A-2 要给槽位核对表，A-4 要给尺寸变化前后的 FG 状态，A-1 要给参数个数表
- `/W4 /WX` 零警告；UTF-8 无 BOM/LF；**不 git commit**
- 基线：native **196 用例 / 710,837 断言**，只增不减

### 自测

```bash
# 构建（先清代理变量，见交接档案 §3.2）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1
ctest --test-dir Native/build -C Release
```

### 交付

施工报告写入 `Docs/Logs/TASK-R10-03.md`：每条 → 改了什么（行号）→ 证据 → 是否新增用例。

---

## 施工报告（施工代理写）

- **施工**：general-purpose 施工子代理（接手续建），2026-10-30
- **接手现场**：前代理（workbuddy）限额中断，盘上有未提交半成品（git status：zfgprobe 多文件 + vulkan device + 4 个新测试 untracked）。本代理先审计完成度，再补缺陷、补证据、跑全量自测。未 git commit。

---

### ① 接手审计结论（半成品完成度）

前代理的半成品**四组主体全部已写**（含 4 个新测试文件、4 个新头文件），总体质量高，但有 **2 处会导致构建失败/证据失真的缺陷**，由本代理修复：

| # | 审计发现 | 判定 | 处置 |
|---|---|---|---|
| W1 | `test_zfg_dxgi_exports.cpp` 对 20 个代理导出直接取地址（`ArityOf(&Name)`），但 `zfg_dxgi_proxy.cpp` 只参与 `zfg_dxgi` SHARED 目标（CMakeLists:464-466），**不进 zrender_test → 必然 LNK2019**。而把该 TU 挪进测试目标也不可取：zrhi 内部 `::CreateDXGIFactory*` 调用会在测试进程内解析到代理转发函数（行为劫持） | 构建级缺陷 | 改为**不求值**探测：`ZFG_DECLARED_ARITY(fn) = ArityOf(static_cast<decltype(&(fn))>(nullptr))`（test_zfg_dxgi_exports.cpp:38-46）——只取声明类型、不 odr-use；"声明 arity == 定义 arity"由 zfg_dxgi_proxy.cpp 的编译锁死（见②A-1 变异证据） |
| W2 | `INFO("def path: ", usedPath)`：doctest 的 INFO 展开为 `mb << "def path: ", usedPath`——逗号运算符吃掉第二个操作数，**变量从不进日志** | 证据失真 | 改为流式 `INFO("def path: " << usedPath)`（test_zfg_dxgi_exports.cpp:272） |
| W3 | `test_zfg_factory_slots.cpp` 只 include `<dxgi1_2.h>` 未先 include `<windows.h>`（CINTERFACE 需在任何 COM 头之前就位，依赖 dxgi.h 的间接包含顺序） | 健壮性 | 显式 `#include <windows.h>`（test_zfg_factory_slots.cpp:26-33） |
| W4 | 其余核对全部通过：`FlushGameQueue` 前向声明与 ：343 定义签名一致；CaptureObserver 锁配平（单 acquire、所有出口单 release）；删除的 `BaseName`/`SlotsIndex` 无残留引用；`.def` 20 导出与表一一对应；帧闸常量与 vulkan.h 枚举值一致（且被 static_assert 钉住） | — | 无需改动 |

---

### ② 逐条修复（改了什么 → 证据 → 用例）

#### A-2 工厂 vtable 槽 17 → 24（Composition）

**改了什么**：
- 槽号上收为单一事实来源 `Native/zfgprobe/zfg_factory_slots.h`（新文件）：`kVtblSlots[]`（:61-70）+ 具名常量（:73-79），**Composition = 24**。产品代码 `zfg_probe_hook.cpp` 改为 `using zfg::kSlot*`（:50-60），本地常量删除。
- **运行期槽位守卫**（patch 之前跑，`zfg_probe_hook.cpp`）：`IsSystemDxgiAddress`（:949，按"完整路径目录 == GetSystemDirectoryA + 基名 dxgi.dll"判定，防同名代理误判）→ `VerifyFactorySlots`（:977-1041，四条判据：槽号 < VtblCount / 槽内指针落在系统 dxgi / lastOfInterface 不变量（Composition+1 == 25）/ 被 patch 槽两两不同）→ `VerifySwapchainSlots`（:1043-1056）。守卫失败 = 整体放弃安装（:1500-1503、:1189-1193）。
- "patch 成功"与"槽位校验通过"在日志里分开打：`patch ok: slot=…`（:1110）与 `slot guard: … -> PASS/VERIFY PASS`（:1017-1037）；patch 总结行带槽号（:1538）。

**槽位核对表**（SDK 10.0.26100 `IDXGIFactory2Vtbl`，共 25 项；与单测按 `offsetof(IDXGIFactory2Vtbl, member)/8` 反算一致）：

| 槽 | 方法 | 被 patch |
|---|---|---|
| 0-2 | IUnknown（QI/AddRef/Release） | |
| 3-6 | IDXGIObject（SetPrivateData×2/GetPrivateData/GetParent） | |
| 7-11 | IDXGIFactory：EnumAdapters/MakeWindowAssociation/GetWindowAssociation/**CreateSwapChain(10)**/CreateSoftwareAdapter | 10 |
| 12-13 | IDXGIFactory1：EnumAdapters1/IsCurrent | |
| 14 | IsWindowedStereoEnabled | |
| **15** | CreateSwapChainForHwnd | ✓ |
| **16** | CreateSwapChainForCoreWindow | ✓ |
| 17 | **GetSharedResourceAdapterLuid**（旧实现误当 Composition） | |
| 18-23 | Register/UnregisterStereoStatus(OcclusionStatus)(Window/Event) | |
| **24** | **CreateSwapChainForComposition**（Vtbl 最后一项） | ✓ |

交换链族（`IDXGISwapChain1Vtbl` 29 项）：Present **8** / ResizeBuffers **13** / Present1 **22**。

**证据（真机 selftest，log = `TempLogs/r10_03/zfg_probe_selftest.log`）**：
```
slot guard: CreateSwapChain slot=10 fn=0x… module='C:\WINDOWS\system32\dxgi.dll' -> PASS
slot guard: CreateSwapChainForHwnd slot=15 … -> PASS
slot guard: CreateSwapChainForCoreWindow slot=16 … -> PASS
slot guard: CreateSwapChainForComposition slot=24 … -> PASS
slot guard: VERIFY PASS (4 factory slot(s); composition=24 == IDXGIFactory2Vtbl[25]-1)
patch ok: slot=24 0x00007FFEE225E140 -> 0x00007FF770A323E0
factory vtable patched (slots: createSwapChain=10 forHwnd=15 coreWindow=16 composition=24)
```
**变异证据（用例有牙，V1）**：把常量改回 17 → `zrender_test` **3 用例 / 4 断言红**，且全部落在守卫用例（test_zfg_factory_slots.cpp:102/122/123/172：`kSlotCreateSwapChainForComposition+1 != kIdxgiFactory2VtblCount`、`!= luid`、`== composition` 失败）；恢复 24 → 全绿。守卫语义（SDK 布局）由 `test_zfg_factory_slots.cpp`（CINTERFACE 反算，6 个 TEST_CASE）静态锁定。

#### A-4 交换链重建后尺寸不更新

**改了什么**：
- 纯逻辑状态机 `Native/zfgprobe/zfg_probe_resize.h`（新）：`ApplySwapchainResize`（:47-71，更新尺寸/作废采集/推进 consumedStamp/DXGI(0,0) 语义保守处理）、`FrameInputMatchesDisplay`（:74-79）、`ClampCopyExtent`（:83-89）。产品与单测共用。
- `zfg_probe_wrap.cpp`：全局 `g_displayWidth/Height/g_captureReady` 收敛为 `DisplayState g_display`（:63）；resize 时读回资源**整组退休**（`g_captureRetired` :127、`RetireCaptureBank` :171-196——先 GPU 追平再换槽，下一帧 `EnsureCapture`（:199-260）按新尺寸重建；中途失败释放已建槽位，顺带修掉审计 B-6 的泄漏面 ：247-258）；`CaptureObserver`/`RecordNativeCaptureAfter`/`ComputePixelStats` 的拷贝 box 与扫描范围全部改 `ClampCopyExtent(源实际尺寸, 槽位容量)`（:300-302/:643-652/:388-402）——**越界根因结构性堵死**；`WrapOnSwapchainResized`（:1025-1053：尺寸推进 + 复用额度清零 + 计数）与 `WrapNoteSwapchainSize`（:1000-1023，原生链建链也登记）；`WrapOnFrame` 尺寸校验改为实时判定 + 状态切换日志（:1100-1114）；`WrapShutdown` 统一释放两代槽位（:1239-1266）。
- `zfg_probe_hook.cpp`：`HookResizeBuffers` 成功后调 `WrapOnSwapchainResized` 并更新链档案（:896-903）；`HookCreateSwapChain`（:620）与 `HookCreateSwapChainForHwnd`（:704）登记初始尺寸。声明见 `zfg_probe_wrap.h:33-41`。

**证据（尺寸变化前后的 FG 状态，log 模式真机 selftest）**：
```
zfg wrap: swapchain resized 320x200 -> 256x152 (accepted=1 sizeChanged=1 captureReset=0 inputsRevalidated=1 serial=2)
zfg wrap: swapchain resized 256x152 -> 352x232 (accepted=1 sizeChanged=1 captureReset=0 inputsRevalidated=1 serial=3)
```
旧实现在这两行**根本不会出现**（只转发不通知）。`serial` 2→3 连续推进证明状态机真实写入；`captureReset=0` 属 log 模式预期（采集银行在 wrap 模式才建）。
**用例**：`test_zfg_resize.cpp`（7 个 TEST_CASE，直接驱动产品同一份纯逻辑）：1920x1080→1280x720（变小主路径：尺寸更新/采集作废/旧输入拒收）、变大、同尺寸重建、(0,0) 语义、**"旧尺寸输入被拒（FG 安全停用）→ 新尺寸输入被接受（FG 恢复）"**、`ClampCopyExtent` 钳制、连续 resize 的 serial 推进。selftest 另加 3 组断言（zfg_probe_selftest.cpp:140-146/169-182）。

#### A-1 代理转发参数不足（P1）

**改了什么**：
- 单一事实来源 `Native/zfgprobe/zfg_dxgi_exports.h`（新）：`kDxgiExports[20]`（名字/真实参数个数/声明形参个数/证据等级 :57-79）+ 20 个导出声明（:114-163）；文件头 ：11 的假断言在 `zfg_dxgi_proxy.cpp:11-15` 改写为"8 槽窗口全量透传，任何 ≤8 参导出不会截断"。
- `zfg_dxgi_proxy.cpp`：4 参 `GenericFn`/`Forward` → 8 槽 `GenericFn8`/`ForwardArgs`（:45、:282-295）；20 个导出全部按表声明转发。

**参数个数表**（param=真实参数个数，decl=声明形参个数=转发覆盖）：

| 导出 | param | decl | 依据 |
|---|---|---|---|
| ApplyCompatResolutionQuirking | 4 | 8 | Unknown（不可证→8 槽全转发） |
| CompatString / CompatValue | 2 | 8 | Unknown |
| CreateDXGIFactory / CreateDXGIFactory1 | 2 | 2 | MsDoc |
| CreateDXGIFactory2 | 3 | 3 | MsDoc |
| **DXGID3D10CreateDevice** | **7** | **8** | **PublicProto（R10-02 A-1 截断点）** |
| **DXGID3D10CreateLayeredDevice** | **6** | **8** | **PublicProto（截断点）** |
| **DXGID3D10GetLayeredDeviceSize** | **7** | **8** | **PublicProto（截断点）** |
| DXGID3D10RegisterLayers | 2 | 8 | PublicProto |
| DXGIDeclareAdapterRemovalSupport / DXGIDisableVBlankVirtualization | 0 | 0 | MsDoc |
| DXGIDumpJournal | 1 | 8 | MsDoc |
| DXGIGetDebugInterface1 | 3 | 3 | MsDoc |
| DXGIReportAdapterConfiguration | 1 | 8 | Unknown |
| PIXBeginCapture | 2 | 8 | MsDoc |
| PIXEndCapture | 1 | 8 | MsDoc |
| PIXGetCaptureState | 0 | 8 | MsDoc |
| SetAppCompatStringPointer | 2 | 8 | Unknown |
| UpdateHMDEmulationStatus | 1 | 8 | Unknown |

> 证据等级诚实标注：`PublicProto` = 未文档化导出按公开流传原型（Wine/RE），个数存疑但**声明 ≥ 真实**即可保证不截断；`Unknown` = 一律 8 槽。多传参数在 Win64 安全（调用方清栈、被调方只读自己声明的槽）。

**用例**：`test_zfg_dxgi_exports.cpp`（5 个 TEST_CASE）：声明 arity 与表逐条一致 / `decl ≥ param` 且 `param ≤ kForwardSlots(8)` / 回归钉（DXGID3D10* 的 param==7/6/7 且声明 ≠4）/ 下标枚举同源 / `.def` 每个导出都在表里（新增导出漏配即红）。
**变异证据（编译锁）**：把 `DXGID3D10CreateDevice` 声明截回 4 参 → `zfg_dxgi_proxy.cpp(352): error C2733`（extern "C" 定义无法共存）**构建失败**；恢复 → 全绿。即"声明 arity == 定义 arity"由编译器强制，单测锁的是"表 == 声明"这一层。

#### A-5 / A-6 Vulkan 错误处理

**改了什么**：
- `zrhi/vulkan/zrhi_vulkan_frame_gate.h`（新）：`ClassifyVkResult`（:40-48，**唯一转译点**：Success/Suboptimal/OutOfDate/SurfaceLost/DeviceLost/Error）+ 纯逻辑帧闸 `FrameGate`（:70-164）：acquire 失败/过期 → 本帧不提交不呈现且**不登记挂起信号量**；submit 失败 → 本帧不呈现；needsRecreate 粘性直到 `OnSwapchainRecreated`；跳过/失败/重建请求计数可读。
- `zrhi_vulkan_device.cpp`：常量与真实枚举的 `static_assert` 五连（:22-30，SDK 变值即编译红）；`AcquireNextImage`（:1359-1379）：失败一律分类进帧闸后**直接返回、绝不 `SetPendingAcquire`**（旧实现的"打印后继续"路径删除）；`Present`（:1392-1412）：`ShouldPresentThisFrame()` 拦截 + 结果统一进 `OnFramePresent`；`SubmitCommandList`（:1718-1740）：`CanSubmit()` 为假 → 丢弃挂起信号量并跳过 `vkQueueSubmit`；提交返回值进 `OnSubmit`，**失败不置 `m_pendingSubmit`**（下一帧 Begin 的 `vkWaitForFences(UINT64_MAX)` 不会等一个永不 signal 的 fence——A-6 的永久阻塞链闭环）；三个入口日志全部只在状态变化/限流时打。
- `zrhi_vulkan_device.h`：帧闸 API 与成员（:311-321、:377-382）。

**用例**：`test_zrhi_vulkan_result.cpp`（8 个 TEST_CASE，驱动产品同一份帧闸）：分类表、正常帧、"**模拟 OUT_OF_DATE 后不再提交到旧链**"（CanSubmit/CanPresent 均 false + NeedsRecreate 粘性 + 重建后恢复）、SUBOPTIMAL 仍可提交但标记重建、**提交失败本帧绝不呈现**（DeviceLost/Error 两分支 + 次帧恢复）、Present OUT_OF_DATE 标记重建、跳过计数跨帧累计、未 acquire 不得提交。

---

### ③ 自测（全部实跑取证）

| 项 | 结果 |
|---|---|
| `Tools/build_native.ps1`（已先 unset 大小写代理变量） | **ALL GREEN**，/W4 /WX 零警告 |
| 原生单测 `zrender_test.exe` | **222 用例 / 711,190 断言，0 failed**（基线 196/710,837 → **+26 用例 / +353 断言，只增不减**） |
| ctest（VS 自带 3.31.6，WORKING_DIRECTORY=仓库根） | **100% tests passed, 0 failed out of 1** |
| `zfg_probe_selftest.exe`（真 GPU，log → TempLogs/r10_03/） | **21/21 PASS**（A-2 槽位守卫 4 组断言 + A-4 缩/放两组断言全过） |
| D3D12 回归 `--draw` | `D3D12 FIRST DRAW OK`，exit 0，InfoQueue errors=0，device removed=0x0 |
| D3D12 回归 `--lights` | `D3D12 LIGHTS OK`，exit 0，InfoQueue errors=0，device removed=0x0 |
| 变异①（A-2 槽 24→17） | 恰好 3 用例/4 断言红，全在守卫用例；恢复后全绿 |
| 变异②（A-1 声明 8→4 参） | `error C2733` 构建失败（编译锁成立）；恢复后全绿 |
| 治理 | 未 git commit；改动仅限 `Native/zfgprobe/`、`Native/zrhi/vulkan/`、`Native/tests/`、`Native/CMakeLists.txt`、本日志；新文件 UTF-8 无 BOM / LF（逐个 `file` 核验）；`UnityProject/.../zrender.dll` 的 M 为构建产物噪声（按交接档案口径由主控提交前 `git checkout --` 丢弃） |

**环境新发现（建议主控记入交接档案）**：PATH 里的 `ctest` 是 msys64 的 MinGW 版 4.4.3（`/c/msys64/mingw64/bin/ctest`），本机对 `Native/build/Testing/Temporary/LastTest.log` 写入一律 Permission denied（bash/PowerShell/cmd 三种调用同挂，与测试结果无关）；用 VS BuildTools 自带 ctest（`C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\...\CMake\bin\ctest.exe`）正常。

---

### ④ 遗留（诚实边界）

1. **VK 交换链重建本身未建**：`OnSwapchainRecreated()` 是帧闸的清标记 API，但 VK 后端没有重建路径（`vkCreateSwapchainKHR` 仅建链时调用一次，zrhi_vulkan_device.cpp:1290）——OUT_OF_DATE 后 needsRecreate 按设计**保持粘性**，帧持续被跳过而不是用旧链。这与原代码"resize unsupported（已知坑）"的边界一致；本任务只修错误处理路径（挂死/假死），真重建是后续功能任务。
2. **A-4 的 wrap 模式插帧恢复只有纯逻辑证据**：`FrameInputMatchesDisplay` 停用→恢复由单测直接驱动产品函数；log 模式真机证据覆盖尺寸推进与安全停用计数；**FFX wrap 链上"拖窗口→FG 停用→新输入到达→恢复插帧"的端到端证据**需要 Unity Player 真机 resize（原 HANDOFF 本就登记"ResizeBuffers 路径未验"），留验收/后续真机项。
3. **A-1 的 8 槽窗口对"外部调用者少传参"的措辞**：`zfg_dxgi_exports.h:112`"未用到的槽位由调用方按 0 传"仅对**仓内转发侧**成立；外部调用者传什么读什么（寄存器/栈垃圾），真实实现按自己的原型读取、忽略多余槽，安全但注释措辞应收窄为"转发侧全量透传"。0 参导出（如 PIXGetCaptureState）声明 8 参读到的宿主垃圾会被原样转发再被忽略，无副作用。
4. **`g_display` 的并发口径沿用既有风格**：渲染线程写 / FFX pacing 线程读，与原 `g_displayWidth` 相同；退休银行机制已把"释放正在被写的读回资源"这一最尖锐的 UAF 窗口关掉（先 FlushGameQueue 再换槽、锁内迁移）。
5. 其余审计条目（A-3 desc 透传、A-7 裸指针 AddRef、B-1~B-5、B-7、Unity 侧 A-8/A-9）不在本任务四组内，未动。

## 验收清单（主控写）

- [ ] V1 A-2：槽位常量已改；**验收自己按 SDK 头逐槽核对**一次；守卫用例存在且改回旧值会红
- [ ] V2 A-4：尺寸更新路径存在且被调用（行号）；有"窗口变小"路径的用例/探针证据
- [ ] V3 A-1：参数个数表与系统 dxgi.dll 实导签名一致（抽查 3 个 >4 参导出）；表驱动用例存在
- [ ] V4 A-5/A-6：OUT_OF_DATE 走明确路径、`VkResult` 检查覆盖；有对应用例
- [ ] V5 回归：native ≥196/710,837；D3D12 后端行为未变（`--draw`/`--lights` 仍过）
- [ ] V6 治理：未 commit、零警告、无 BOM

## 验收结论（验收代理写）

- **验收**：general-purpose 验收子代理（独立实跑取证，不跑 Unity，未 git commit），2026-10-30
- **判定**：**PASS 6/6**

| 项 | 判定 | 证据（验收亲核/亲跑） |
|---|---|---|
| V1 A-2 | PASS | 验收亲按 SDK 10.0.26100 `shared/dxgi1_2.h:1776-1980` 的 C 风格 `IDXGIFactory2Vtbl` 逐槽数出 **25 项、CreateSwapChainForComposition=24**（17=GetSharedResourceAdapterLuid）；同法核 `IDXGISwapChain1Vtbl` 29 项 Present=8/ResizeBuffers=13/Present1=22——均与 `zfg_factory_slots.h` 一致；守卫用例 6 个 TEST_CASE 在场（test_zfg_factory_slots.cpp:81/96/115/129/178/208）＋运行期守卫（zfg_probe_hook.cpp:949/977/1043）；**变异亲做**：24→17 重建后恰 **3 用例/4 断言红**且全落守卫用例（"25 项且 Composition 是最后一项"/"回归钉"/"具名常量与槽位表同源"），恢复 24 → 全绿 |
| V2 A-4 | PASS | 尺寸更新路径在钩子内被真实调用：`HookResizeBuffers` 成功后调 `WrapOnSwapchainResized(width,height)`（zfg_probe_hook.cpp:896）→ `ApplySwapchainResize`（zfg_probe_wrap.cpp:1025-1053，采写作废/复用清零/serial 推进）；**"窗口变小"证据亲跑**：selftest 21/21 PASS exit 0，探针日志 `swapchain resized 320x200 -> 256x152 (…serial=2)` 与 `256x152 -> 352x232 (…serial=3)`；单测 7 例（test_zfg_resize.cpp:42 缩小主路径、:105 旧尺寸拒收→新尺寸恢复、:124 ClampCopyExtent） |
| V3 A-1 | PASS | 抽查 4 条：**DXGID3D10CreateDevice 真实 7 参**（ReactOS/Wine d3d10core 源码逐参核对：HMODULE/IDXGIFactory*/IDXGIAdapter*/flags/feature_levels/count/out device）、**DXGID3D10RegisterLayers 2 参**（同源）、CreateDXGIFactory2=3 与 DXGIGetDebugInterface1=3（SDK 10.0.26100 dxgi1_3.h:121-122）——与表一致；2 个 LayeredDevice 导出无逐字公开原型，表如实标 PublicProto 且按 8 槽全转发（**截断结构上不可能**）；表驱动用例 5 个 TEST_CASE 在场（声明 arity/覆盖判据/回归钉/下标同源/.def 对照）；**变异亲做**：声明 8→2 → `zfg_dxgi_proxy.cpp(352): error C2733` 构建红（编译锁成立），恢复 → 全绿 |
| V4 A-5/A-6 | PASS | OUT_OF_DATE 明确路径：`AcquireNextImage` 失败分类进帧闸后**直接返回、不 SetPendingAcquire**（zrhi_vulkan_device.cpp:1359-1372）；`SubmitCommandList` 的 `CanSubmit()`=false → **丢弃挂起信号量并跳过 vkQueueSubmit**（:1718-1727）；提交失败**不置 m_pendingSubmit**（:1730-1740，A-6 永久阻塞链闭环）；`static_assert` 五连钉枚举值（:22-30）；用例 8 个 TEST_CASE（test_zrhi_vulkan_result.cpp:67 "模拟 OUT_OF_DATE 后不再提交到旧链"＋CanSubmit/CanPresent 均 false＋粘性重建＋重建恢复、:107 提交失败绝不呈现） |
| V5 回归 | PASS | 亲跑 `Tools/build_native.ps1` **ALL GREEN：222 用例 / 711,190 断言，0 failed**（基线 196/710,837 → +26/+353，只增不减）；`zfg_probe_selftest.exe` 亲跑 **21/21 PASS exit 0**（含 A-2 槽位守卫 4 组、A-4 缩/放两组断言）；`--draw` 亲跑 **exit 0**（`D3D12 FIRST DRAW OK`，InfoQueue errors=0，device removed=0x0）；`--lights` 亲跑 **exit 0**（`D3D12 LIGHTS OK`，同上）——D3D12 后端行为未变 |
| V6 治理 | PASS | 未 commit（git log 顶仍 `225e1d8`，全部改动未暂存）；`/W4 /WX` 零警告（构建日志无 warning 行，步骤标签除外）；15 个触及文件**字节级核验 0 个 CR、无 BOM**（LF）；改动仅 `Native/zfgprobe/`、`Native/zrhi/vulkan/`、`Native/tests/`、`Native/CMakeLists.txt`、本日志＋`zrender.dll`（构建产物噪声，按交接档案口径由主控提交前 `git checkout --` 丢弃）——无越界 |

**验收遗留（不阻塞，如实登记）**：
1. A-4 wrap 模式"拖窗口→FG 安全停用→新尺寸输入到达→恢复插帧"的端到端证据需 Unity Player 真机 resize（与施工报告④-2 一致；原 HANDOFF 本就登记 ResizeBuffers 路径未验）。本验收按简报未跑 Unity，采信"纯逻辑单测＋探针 log 模式真机"两级证据。
2. A-1 对 2 个 LayeredDevice 导出的"真实参数个数"无独立旁证（表标 PublicProto/存疑），但安全判据（decl ≥ param、param ≤ 8 槽全转发）成立且被用例锁定——截断类危害已结构性排除。

**总判定：PASS（6/6）。可交主控收口（一任务一 commit；zrender.dll 噪声按既定口径处理）。**
