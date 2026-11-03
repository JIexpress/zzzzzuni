# TASK-B-R8-02 帧生成（FFX FrameGeneration D3D12 集成 + 能力实证）

状态: doing | 派发: 2026-10-27 | 施工: general-purpose 施工子代理（2026-10-28 完成） | 验收: general-purpose 验收子代理（2026-10-28） | commit: （待填）

## 任务简报（主控写）

**目标**：R8 最后一项——FSR 帧生成（FrameGeneration）经 ffx_api 集成到 D3D12 窗口程序。**诚实预期（先读清再动手）**：①FG 组件存在（zip 内 `amd_fidelityfx_framegeneration_dx12.dll`，仅 D3D12）②FG 官方支持矩阵大概率**不含核显**（780M 是 RDNA3 iGPU，FSR4 先例已证伪核显路径）③FG 集成需要 present 回调接管呈现时序——复杂度最高且可能 destabilize 现有模式。因此本任务验收口径：**集成代码+运行时能力实证+逻辑单测**；若核显被拒→如实列受限（同 DLSS 先例）；**若 API 层面可用则给出帧计数翻倍证据**。

**设计要点**：
1. vendor：从已下载 zip 提取 `amd_fidelityfx_framegeneration_dx12.dll`+`amd_fidelityfx_loader_dx12.dll`+ags 到 `Native/ThirdParty/ffx-fsr/bin/`（B-R6-01 已 vendor 头文件——确认 ffx_api 的 FG 头 `ffx_api.h`/frame generation desc 已含，缺则补头文件），SOURCE.md 追加记录
2. `Native/zupscaler/fg_ffx.h/.cpp`：
   - `FrameGenerator`：`Create(d3d12Device, swapchain 尺寸)`→`Configure(desc: MV 资源、present 回调、UI 区域)`→`Present 包装`（FG 模式下经 SDK 回调呈现：真帧+插帧）→`Shutdown`
   - 能力门控：`QuerySupport()`——ffx_api 创建 context 的返回码即事实（SDK 拒绝=iGPU 不支持→返回枚举并打印原因）
   - 与 UpscalerChain 关系：FG 是独立呈现层组件，不进 IUpscaler（上采样在 pass 内，FG 在 present 层）——注释说明
3. 窗口程序 `--fg` 模式（独立 flag，不破坏现有模式）：MV 复用 R6-01 的 MV Pass；present 路径切换到 FrameGenerator；**验证**：
   - 若 SDK 接受：present 回调计数 60 帧→统计每秒真帧+插帧数（**帧计数翻倍≈2x 证据**）+ InfoQueue 0 错误；通过行 `D3D12 FG OK (real=N gen=M)`
   - 若 SDK 拒绝（预期）：打印确切错误码与原因→**列受限**，仍 exit 0（受限不是失败）+ `D3D12 FG LIMITED (reason=...)`
   - 两种结果都要截图/日志留档
4. 单测：配置逻辑纯函数（尺寸/格式校验、能力枚举映射）；mock 后端的 Chain 集成点
5. 自测：build 全绿（180 回归+新增）；亲跑 `--fg` 记录实际结果；`--draw` 回归确保 present 改动未破坏默认模式

**约束**：只动 `Native/zupscaler/`、`Native/ThirdParty/ffx-fsr/`（bin 补充+SOURCE.md）、`Native/tests/`、`Native/CMakeLists.txt`、`Native/zrender/`（present 路径加法）、本任务日志；不碰 Unity；不 git commit；UTF-8 无 BOM/LF/#pragma once。

**交付物**：FG 集成+运行时能力实证（OK 或 LIMITED 二选一如实）+单测+施工报告四节（含与官方支持矩阵对照）。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理 | **完成日期**: 2026-10-28 | **自测**: build_native.ps1 ALL GREEN（**190 用例 / 710,800 断言**，基线 180/710,633，+10 用例 +167 断言，0 失败，/W4 /WX 零警告，BUILD_EXIT=0）

### 一、交付概览

| 交付物 | 位置 | 取证 |
|---|---|---|
| FG vendor 子集 | `Native/ThirdParty/ffx-fsr/bin/`：`amd_fidelityfx_framegeneration_dx12.dll`（40,085,776 B，SHA256 `02297bee…2f18` 与 zip 条目一致）+ `amd_ags_x64.dll`（179,408 B，`b27b070c…8da2`）；`framegeneration/include/` 三官方头（镜像 SDK 树布局，相对 include 无改动可解析） | `dumpbin -exports` 实测 FG dll 直接导出 ffx_api 五函数；导入表仅系统 dll；SOURCE.md 已追加"B-R8-02 追加"节（三头 git blob sha1 与 tag v2.3.0 树一致：`5ca54864…`/`e7e514dd…`/`8bc71ca2…`） |
| FrameGenerator 封装 | `Native/zupscaler/fg_ffx.h`（公共头，`#pragma once`，不依赖 d3d12/ffx 头）+ `fg_ffx.cpp`（`Create` :418 / `CreateFgSwapchainForHwnd` :508 / `ConfigureFrame` :579 / `DispatchPrepare` :620 / `PresentFrame` :668 / `WaitForPresents` / `Shutdown` :707；能力门控 `QuerySupport` :400；present 蹦床 :307、frameGeneration 蹦床 :363；mock 注入点 :763） | 与 UpscalerChain 关系注释在 fg_ffx.h 头注（独立呈现层组件，不进 IUpscaler——上采样在 pass 内、FG 在 present 层）；两上下文模型（FG 效果 + 代理交换链）按官方 frame-interpolation-api/swap-chain 文档契约，desc 链含 SDK 2.1+ 必填的 Version desc |
| 纯函数（单测锁定） | `ZupFgClassifySupport` :188 / `ZupFgReturnCodeName` :199 / `ZupFgMapResourceState` :228 / `ZupFgValidateCreateDesc` :261 / `ZupFgFrameCountsDoubled` :277 | `ZupFgMapResourceState` 与官方 `backend/dx12/ffx_dx12.cpp ffxGetDX12StateFromResourceState` 逐项对齐（12 态 + 未知→COMMON 兜底） |
| `--fg` 窗口模式 | `Native/tests/zrhi_d3d12_window.cpp`（模式注 :126，`RunFgMode` :6999，main 派发 `--fg`） | 独立 flag 不破坏现有模式；MV 复用 R6-01 的 motion_vectors.hlsl（MvPass MarkRoot，两帧 mvp 各含当帧 jitter）；present 路径切换：每帧 Configure(frameID+1)→场景 RG（BGRA8 直渲染 displaySize，FG 输入色契约）→CopyResource 入代理交换链游戏缓冲（COMMON→COPY_DEST→COMMON）→PrepareV2（深度+MV，COMPUTE_READ）→代理交换链 Present（SDK 回调插帧+合成+计数）；OK/LIMITED/FAILED/TIMEOUT 四路径（节头注列明口径） |
| 单测 +10 用例 | `Native/tests/test_fg_ffx.cpp`（纯函数矩阵 :399/:422/:453/:472/:485 + mock 状态机 :501/:544/:682/:714/:735） | `--test-case="fg:*"` 实跑 **10/10 用例 / 167 断言全过**；mock ffx 五函数表按 desc header.type 分派，desc 链逐字段断言（含 Version 链/旗标位/PrepareV2 全字段）；深度/MV 用真 WARP 资源（官方 `ffxApiGetResourceDX12` 包装内部真调 GetDesc——哨兵指针会段错误，开发期实测） |
| CMake 接线 | `Native/CMakeLists.txt`（zupscaler +fg_ffx.cpp +framegeneration include；zrender_test +test_fg_ffx.cpp +ffx include；窗口 +ffx include + POST_BUILD 补拷 FG/AGS dll） | 构建后 exe 目录实有 4 个 FFX dll |

### 二、`--fg` 真机实测（关键发现：核显被接受且产出插帧）

**结果行（两次实跑一致，exit 0）**：

```
D3D12 FG OK (real=60 gen=60, screenshot=Docs/Parity/fg_real_frame.png)
```

| 项 | 实测值 |
|---|---|
| 适配器 | AMD Radeon 780M Graphics（vendor=0x1002 device=0x15BF，RDNA3 iGPU） |
| provider 枚举 | FRAMEGENERATION 族命中 **3.1.6**（FG 效果头版本 4.0.1、交换链 3.1.7） |
| ffxCreateContext（FG 效果） | rc=0（OK）——**未拒绝核显** |
| 代理交换链（ForHwnd） | rc=0，wrapped IDXGISwapChain4 + frame latency waitable 就绪 |
| 60 帧计数 | **real=60 generated=60**——60 个应用帧 → 120 次呈现（**帧计数翻倍≈2x 达成**），present 回调逐帧 `isGeneratedFrame` 区分（SDK pacing 线程原子计数） |
| 设备健康 | device removed reason=0x0（S_OK）；InfoQueue errors=0（**注意：debug layer 本机未装，degraded 模式下该计数恒 0——与 --draw/--fsr 同口径，非强证据**） |
| 截图 | `Docs/Parity/fg_real_frame.png`（640×480 PNG，922,218 B，人工过目：地面+cube+CSM 阴影正确） |

**与 AMD 官方支持矩阵对照（诚实预期 vs 实测）**：

- 简报预期"FG 官方矩阵大概率不含核显，780M 预期被拒"——**实测不成立（往好方向）**：FSR 3.1.x 帧生成是 vendor-agnostic 计算着色器路径（官方矩阵：RX 5700+/RTX 20+/Intel Arc，无 ML 硬件要求），与 FSR 4 的"RDNA4 独显专属"不同类——780M 被运行时接受且 60/60 插帧产出。FSR4 先例的"核显被拒"不能外推到 FG 3.1.x。来源：AMD FSR 3 官方支持页（经 Extremetech/Videocardz/Guru3D 转述，RX 5700+ / RTX 20+ / Arc）。
- **诚实边界（如实声明）**：① `gen=60` 证据口径 = 代理交换链完成插帧调度与呈现（isGeneratedFrame 呈现计数）+ 无 ffx 错误回调 + 无设备移除；**插帧像素内容未做逐像素断言**（无 readback 通道到插帧内部资源），人工过目仅覆盖真帧源截图。② 官方矩阵按**桌面独显**叙述、未列 iGPU 最低档——本机跑通属"vendor-agnostic 路径在 RDNA3 iGPU 的实证"，不构成"官方支持 780M"的声明。③ `--fg` 每帧 CPU 22–25ms（pacing 按 vsync 半频节流，符合 FG 帧率≈2×帧率的预期形态）。

**开发期修复的真实 bug（集成契约取证）**：首次实跑 `frame latency waitable TIMEOUT (result=WAIT_FAILED)`——官方代理交换链仅在应用传入的 `DXGI_SWAP_CHAIN_DESC1.Flags` 含 `DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT` 时创建帧延迟信号量（FrameInterpolationSwapchainDX12.cpp :1206 判定），缺位则 GetFrameLatencyWaitableObject 返回空句柄。已在 `CreateFgSwapchainForHwnd` 置该位并复跑通过（两次实跑一致）。

### 三、回归与边界

- `--draw` 回归：exit 0，`D3D12 FIRST DRAW OK`（60 帧中心校验 6/6、InfoQueue errors=0）——默认 present 路径零破坏
- `--fsr` 回归：exit 0，`D3D12 FSR OK (edge=0.159 vs 0.118, ratio=1.346, backend=Fsr3 3.1.5)`——与 R6-01 记录一致，UpscalerChain 路径零破坏
- 未碰 Unity；未 git commit；触碰文件仅限约束清单（zupscaler/ffx-fsr/tests/CMakeLists/本日志）
- 已知边界：① 插帧像素级断言缺通道（见上"诚实边界"）② FG 仅 D3D12（SDK v2.3.0 无 FG VK 组件，`--backend vulkan` 下打印 NOTICE 后走 D3D12 路径）③ 帧起步等待超时 → 硬退出 exit 3（防验收无限挂起；正常路径两次实跑未触发）④ debug layer 未装 → InfoQueue 计数为 degraded 口径（HANDOFF 已在册的既有环境限制）
- 文件规范：新增文件 UTF-8 无 BOM；工作区 CRLF 与仓库现状一致（`.gitattributes` `eol=lf` 提交时统一归一）

## 验收清单（主控写）

1. build 全绿（用例数写入结论）
2. 亲跑 `--fg`：OK（帧计数翻倍证据）或 LIMITED（错误码+原因）——与施工报告结论一致
3. 亲跑 `--draw` 回归（默认 present 路径未破坏）
4. 能力门控/配置纯函数单测真实断言（行号）
5. SOURCE.md 追加记录核对
6. 文件规范+施工报告完整（与 AMD 官方支持矩阵对照说明）

## 验收结论（验收代理写）

**结论: PASS（6/6）** | 验收: general-purpose 验收子代理 | 日期: 2026-10-28 | 全部证据为本代理独立亲跑/亲测，未复用施工代理输出。

1. **build 全绿 — PASS**：仓库根亲跑 `Tools/build_native.ps1`，SCRIPT_EXIT=0，`test cases: 190 | 190 passed`，`assertions: 710800 | 710800 passed`，`ALL GREEN`——用例数/断言数与施工报告（190/710,800）逐字一致。
2. **亲跑 `--fg` — PASS**：exit 0，`D3D12 FG OK (real=60 gen=60, screenshot=Docs/Parity/fg_real_frame.png)`；适配器 780M（vendor=0x1002 device=0x15BF）、provider 3.1.6、FG 效果与代理交换链两处 `ffxCreateContext rc=0`；real=60/gen=60 帧计数翻倍达成；log 同屏打印 `debug layer UNAVAILABLE (degraded)`——InfoQueue errors=0 的 degraded 口径与报告"非强证据"声明如实一致；截图 922,218 B 为本次实跑新写入。
3. **`--draw`/`--fsr` 回归 — PASS**：`--draw` exit 0 `D3D12 FIRST DRAW OK`（默认 present 路径零破坏）；`--fsr` exit 0 `D3D12 FSR OK (edge=0.159 vs 0.118, ratio=1.346, backend=Fsr3 3.1.5)` 与 R6-01 记录一致。
4. **vendor 核对 — PASS**：实测 FG dll SHA256 `02297BEE…2F18`（40,085,776 B）、AGS `B27B070C…8DA2`（179,408 B）与 SOURCE.md"B-R8-02 追加"节一致；dumpbin 实测 FG dll 恰好 5 导出（ffxConfigure/ffxCreateContext/ffxDestroyContext/ffxDispatch/ffxQuery）；三 FG 头 git blob sha1 实算 `5ca5486460…`/`e7e514ddb6…`/`8bc71ca262…` 与 SOURCE.md 声明一致。
5. **单测真实断言+WAITABLE 修复 — PASS**：亲跑 `--test-case="fg:*"` 10/10 用例 167 断言全过；10 个 TEST_CASE 行号逐一核实（test_fg_ffx.cpp :399/:422/:453/:472/:485 纯函数 + :501/:544/:682/:714/:735 mock 状态机）；断言为真实锁定非空转——desc 链逐字段（displayW/H/format/Version 链值/四旗标位）、swapchain desc 7 字段、PrepareV2 frameID 一致性、NO_PROVIDER 拒绝透传（:535-541），mock 表按 desc header.type 分派（:153-171），深度/MV 用真 WARP 资源；fg_ffx.cpp 行号核实：纯函数 :188/:199/:228/:261/:277、QuerySupport :400、Create :418、CreateFgSwapchainForHwnd :508、蹦床 :307/:363、mock 注入 :763；WAITABLE 修复实锤：fg_ffx.cpp:528-532 注释引 SDK 判定依据 + `scd.Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT`（:532），与 `--fg` 实跑 waitable 就绪互证。
6. **文件规范+报告完整 — PASS**：6 个新增文件（fg_ffx.h/.cpp、test_fg_ffx.cpp、三 FG 头）均无 BOM、有效 UTF-8；fg_ffx.h:40 `#pragma once`；工作区 CRLF 与既有仓库文件一致（抽查 upscaler_chain.cpp 同为 CRLF），`.gitattributes` `* text=auto eol=lf` 提交归一——报告披露如实；任务日志四节齐（简报/施工报告/验收清单/本结论）；施工报告含"实证≠官方背书"（诚实边界①②③：无逐像素断言通道、iGPU 属 vendor-agnostic 路径实证非官方支持声明、CPU 22-25ms 口径）。

**验收备注（不阻塞，供主控参考）**：① build_native.ps1 不 Set-Location，从非仓库根 CWD 运行时 zrender_test 的 SPIR-V 用例按相对路径定位会误报缺失（本代理首轮即踩中，从仓库根重跑即全绿）——既有脚本行为，非本任务引入，建议后续任务顺手修复；② git 工作区触碰文件（`git status`）均在简报约束清单内（zupscaler/ffx-fsr/tests/CMakeLists/本日志 + build 脚本 4/4 步自部署的 zrender.dll + 截图），未 git commit。
