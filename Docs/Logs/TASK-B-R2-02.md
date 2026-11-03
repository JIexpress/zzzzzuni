# TASK-B-R2-02 D3D12 后端基座（设备/交换链/命令表/清屏）+ 窗口验证程序

状态: done(验收PASS) | 派发: 2026-10-23 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：实现 zrhi 的第一个真实后端（D3D12）：设备/队列/交换链/命令表 Begin/End/批量 Barrier/ClearColorImage，配一个 Win32 窗口验证程序自动校验清屏颜色。这是"Vulkan 语义抽象层"落到真实 GPU 的第一步。

**设计要点**：
1. `Native/zrhi/d3d12/` 新目录（建议 `zrhi_d3d12_device.h/.cpp`、`zrhi_d3d12_commandlist.h/.cpp`、`zrhi_d3d12_translate.h/.cpp`），实现 B-R2-01 定义的 `IZrDevice`/`IZrCommandList` 子集：
   - 设备：D3D12 DEBUG layer 可用则开启（附 InfoQueue 错误捕获，Error 级以上记入日志并可通过接口查询计数）；`GetQueue(Graphics)`；`WaitIdle`（fence 信号+等待）
   - 交换链：`CreateSwapchain(void* hWnd, w, h, ZrFormat(BGRA8Unorm), ZrPresentMode)`——3 缓冲 FLIP 模型；PresentMode 映射：Fifo→SyncInterval1、Mailbox→SyncInterval0、Immediate→SyncInterval0+允许撕裂（映射表写注释）；AcquireNextImage/Present 语义对齐 Vulkan（当前帧索引跟踪）
   - 命令表：`Begin/End`、`ResourceBarrier(批量)`（**ZrImageLayout→D3D12_RESOURCE_STATES 用显式 switch 查表**，严禁隐式数值转换——两套枚举序不同，B-R2-01 已知风险）、`ClearColorImage`（经 UAV/RTV clear；交换链后台缓冲用 RTV clear 路径）
   - RTV 描述符堆管理（每交换链一组）
2. `zrhi_d3d12_translate.h`：`ZrFormat→DXGI_FORMAT`、`ZrImageLayout→D3D12_RESOURCE_STATES`、PresentMode→参数，三个映射全部做成可单测的纯函数
3. 窗口验证程序 `Native/tests/zrhi_d3d12_window.cpp`（新 exe 目标 `zrhi_d3d12_window`，仅 Release，不进 ctest）：
   - 创建 640×480 Win32 窗口 → 设备+交换链 → 循环 60 帧：Acquire→Barrier(Present→RenderTarget)→Clear（按帧序循环红/绿/蓝）→Barrier(RenderTarget→Present)→Present
   - **自动校验**：每隔 10 帧把后台缓冲 CopyTextureRegion 到 readback 缓冲，校验中心像素 RGB 与预期清屏色匹配（容差 ±2）；
   - 结束：WaitIdle、销毁全部资源、检查 InfoQueue Error 计数为 0；全部通过打印 `D3D12 WINDOW SMOKE OK` 并退出码 0
   - `--headless` 模式（无窗口，HWND=null 时）：跳过交换链，用离屏 Commit 资源走同样 Clear+readback 校验路径（**沙箱若拒绝窗口创建则用此模式验收**，报告注明实际用的哪种）
4. doctest 单测（进 ctest，纯映射逻辑，无需 GPU）：三个映射函数全枚举断言（每个 ZrFormat 有 DXGI 对应、每个 ZrImageLayout 有状态对应且无重复误配、PresentMode 三值参数正确）
5. CMake：d3d12 后端并入 zrhi 库（同 dll）；window exe 链接 d3d12.lib/dxgi.lib，MBCS/UNICODE 自定但须零警告
6. 语义注释：实现文件头部注释说明本后端与 Vulkan 语义的对应关系（Swapchain Present≈vkQueuePresentKHR 等）

**约束**：只动 `Native/zrhi/`（含新 d3d12/ 子目录）、`Native/tests/`、`Native/CMakeLists.txt`、本任务日志；不引第三方（d3d12.h 用 Windows SDK，BuildTools 自带）；COM 指针统一 `Microsoft::WRL::ComPtr`；/W4 /WX 零警告；不 git commit；UTF-8 无 BOM/LF。

**交付物**：后端实现+映射单测+窗口/headless 验证程序（两种模式都能跑）+施工报告（含实测输出：帧数、颜色校验行、InfoQueue 计数、实际使用模式）。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理 | 2026-10-23

### ① 摘要

实现 zrhi 首个真实后端（D3D12 基座）：设备（DEBUG layer + InfoQueue 捕获 /
GetQueue / WaitIdle fence）+ 交换链（FLIP_DISCARD 3 缓冲、Acquire-Present 语义、
per-buffer fence）+ 命令表（Begin/End、批量 Barrier 显式 switch 查表转译、
ClearColorImage 双路径 RTV/UAV）+ RTV 堆（每交换链一组 + 设备级共享堆）+ 三个
纯函数映射表 + doctest 全枚举单测 + 窗口验证程序（60 帧红绿蓝清屏、每 10 帧
readback 中心像素 ±2 容差校验、UAV 清屏校验、InfoQueue 计数 0 检查、
`D3D12 WINDOW SMOKE OK`、`--headless` 降级）。构建 /W4 /WX 零警告全绿；窗口模式
与 headless 模式实测均通过、退出码 0。

### ② 交付清单

新增（`Native/zrhi/d3d12/`，均 `#pragma once`、UTF-8 无 BOM、模块内部头）：
- `zrhi_d3d12_translate.h/.cpp`（41+87 行）—— `ZrD3D12_Format`（14 格式全枚举
  switch）、`ZrD3D12_ResourceStates`（9 布局全枚举 switch，无任何隐式整型转换）、
  `ZrD3D12_PresentParams`（映射表含注释：Fifo→Sync1、Mailbox→Sync0、
  Immediate→Sync0+ALLOW_TEARING）
- `zrhi_d3d12_device.h/.cpp`（235+634 行）—— `ZrD3D12Device::Create`（DEBUG
  layer 可用则开启 + InfoQueue SetBreakOnSeverity 关闭 + Error 级以上计数/打印/
  排空）、DIRECT 命令队列、WaitIdle（fence 信号+事件等待）、`CreateSwapchain`
  （CreateSwapChainForHwnd，FLIP_DISCARD、3 缓冲、撕裂能力检测降级）、帧槽环
  （分配器+提交 fence，Reset 前等待完成）、设备级 RTV/UAV 描述符堆、句柄表全
  生命周期管理、`ZrD3D12Swapchain`（GetCurrentBackBufferIndex 取帧索引 +
  per-buffer fence 等待 ≈ vkAcquireNextImageKHR；Present ≈ vkQueuePresentKHR）、
  `ZrD3D12Queue`（Submit 委托）
- `zrhi_d3d12_commandlist.h/.cpp`（64+207 行）—— Begin（帧槽分配器 Reset + 绑定
  UAV 堆）/End；批量 ResourceBarrier（ZrImageLayout→states 经映射函数转译，
  子资源 range 展开/整资源 ALL_SUBRESOURCES，同布局跳过；缓冲屏障 D3D12 无对应
  已注释）；ClearColorImage（hasRtv→ClearRenderTargetView，否则
  ClearUnorderedAccessViewFloat）；CopyBuffer；BeginPass/EndPass 占位

新增（`Native/tests/`）：
- `test_zrhi_d3d12_translate.cpp`（136 行，进 ctest）—— 三个映射全枚举独立期望
  表逐值比对 + 两两互异断言（无重复误配）+ Present 参数三值断言
- `zrhi_d3d12_window.cpp`（504 行，新 exe 目标 `zrhi_d3d12_window`，仅 Release、
  不进 ctest）—— 窗口模式：640×480 Win32 窗口 + BGRA8Unorm 交换链，60 帧
  Acquire→Barrier(Present→RenderTarget)→Clear(红绿蓝循环)→Barrier(→Present)
  →Present；每 10 帧 CopyTextureRegion 到 READBACK 缓冲校验中心像素（±2）；
  尾声 UAV 路径校验（离屏 RGBA32Float storage，General 清屏→readback 浮点比对）；
  WaitIdle→销毁全部资源→InfoQueue 计数→`D3D12 WINDOW SMOKE OK`；`--headless`
  跳过交换链走离屏 Commit 资源同路径（RGBA 字节序），窗口创建被拒时自动降级并
  打印说明

修改：
- `Native/zrhi/zrhi_device.h` —— 加法扩展（详见 ④ 设计偏差 1）
- `Native/CMakeLists.txt` —— zrhi 静态库并入 d3d12 三源文件（PRIVATE 链
  d3d12/dxgi 系统库）；zrender_test 增加映射单测；新目标 zrhi_d3d12_window
  （EXCLUDE_FROM_DEFAULT_BUILD 仅 Release 构建，模式同 zkernel_bench）

### ③ 自测结果

- `powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1`：
  **ALL GREEN**（configure + build /W4 /WX 零警告 + 测试 + dll 部署）。
- doctest：`34 test cases | 34 passed | 0 failed`，`6651 assertions | 6651 passed`
  （含新增映射单测：格式 14 值/布局 9 值/呈现 3 值全枚举断言）。
- `Native/build/bin/Release/zrhi_d3d12_window.exe` **窗口模式实测通过**（本环境
  允许窗口创建，未触发降级；--headless 亦实测通过）。**实际使用模式：window**
  （headless 另行单独跑通）。关键输出：
  ```
  zrhi_d3d12_window: device created, debug layer UNAVAILABLE (degraded mode)
  zrhi_d3d12_window: window 640x480 + swapchain (BGRA8Unorm, 3 buffers, FLIP_DISCARD, Fifo/SyncInterval=1)
  frame  1/60: clear=RED   backbuffer=0
  ...（60 帧循环，backbuffer 0/1/2 轮转）...
    frame 10 readback center RGB=(255,0,0) A=255, expected (255,0,0), tolerance +/-2 -> OK
    frame 20 readback center RGB=(0,255,0) A=255, expected (0,255,0), tolerance +/-2 -> OK
    frame 30 readback center RGB=(0,0,255) A=255, expected (0,0,255), tolerance +/-2 -> OK
    frame 40 readback center RGB=(255,0,0) A=255, expected (255,0,0), tolerance +/-2 -> OK
    frame 50 readback center RGB=(0,255,0) A=255, expected (0,255,0), tolerance +/-2 -> OK
    frame 60 readback center RGB=(0,0,255) A=255, expected (0,0,255), tolerance +/-2 -> OK
    UAV clear readback center RGBA=(1.000000,0.000000,1.000000,1.000000), expected (1.0,0.0,1.0,1.0) -> OK
  zrhi_d3d12_window: mode=window, frames=60, color checks=6 (interval 10), UAV check=1
  zrhi_d3d12_window: InfoQueue error count = 0
  D3D12 WINDOW SMOKE OK      （退出码 0）
  ```
  headless 模式输出同构（`mode=headless`，6 次校验 + UAV 校验 OK，退出码 0）。
- **调试层降级说明（验收清单第 3 条）**：本机未安装 D3D12 调试层（Windows
  可选功能 "Graphics Tools" 缺失，`D3D12GetDebugInterface` 失败），程序打印
  `debug layer UNAVAILABLE (degraded mode)`，InfoQueue 计数以降级 0 报告。调试
  层相关代码路径（EnableDebugLayer/InfoQueue 捕获）已实现并编译通过，但本机无
  法实测真实 InfoQueue 消息；建议验收环境安装 Graphics Tools 后复跑以获得
  Error=0 的强证据。
- 未运行 `Tools/run_tests.ps1`（会启动 Unity 批处理，与并行施工代理的
  UnityProject/ 工作冲突；本任务构建自测按简报以 build_native.ps1 为准）。

### ④ 设计偏差（相对简报的补充决定）

1. **接口加法扩展**（简报写 `CreateSwapchain(void* hWnd, ...)`，B-R2-01 已定
   desc 传参）：保持 `CreateSwapchain(const ZrSwapchainDesc&)` 签名不变，在
   `ZrSwapchainDesc` 增加 `void* windowHandle`（≈ surface/HWND）。配套新增
   `IZrSwapchain`（AcquireNextImage/GetCurrentImage/Present）、`IZrQueue::Submit`、
   `IZrDevice::GetSwapchain/GetDebugErrorCount`——全部为加法，既有纯虚签名零
   改动（现有单测不受影响）。
2. **`ZrImageLayout::Present → D3D12_RESOURCE_STATE_PRESENT` 在 SDK 26100 中
   数值等于 COMMON(0)**（d3d12.h 显式 `PRESENT = 0`，呈现态即公共态）。映射
   正确性不受影响（语义目标即 PRESENT）；单测互异断言排除 (Undefined, Present)
   这一对并加 `CHECK(PRESENT == COMMON)` 的 SDK 事实断言 + 注释说明。
3. 缓冲屏障在 D3D12 无对应（缓冲无资源状态，ExecuteCommandLists 边界隐式
   同步）：实现记录参数但零发出，注释标注（≈ Vulkan 语义差异点）。
4. Compute/Transfer 队列基座映射到 DIRECT 队列（三域包装对象保留类型查询），
   异步队列留后续任务；同布局（states 相同）屏障跳过——纯访问/阶段域同步
   （UAV 屏障等）留后续任务。
5. ClearColorImage 基座支持整资源（子资源 range 断言为 All）；readback 插桩
   经后端原生扩展点（`D3D12CommandListOf/D3D12ResourceOf`）记录
   CopyTextureRegion——RHI 级 CopyImageToBuffer 属后续任务，程序头注已说明。
6. 验证程序测试了 UAV 清屏路径（离屏 RGBA32Float，简报主路径为 RTV）作为
   双路径证据；desc.initialLayout 仅语义记录（D3D12 提交资源恒以 COMMON 创建，
   首次屏障转入使用态）。
7. 映射单测 include 后端内部头（简报明确要求对 translate 纯函数做单测，公共
   头 zrhi.h 不引任何 D3D12 头、不受影响）；设备级描述符槽位游标单调不回收
   （容量 128，基座从简）；`GetDebugErrorCount` 读取即排空（打印后
   ClearStoredMessages）。

### ⑤ 风险与遗留

- **调试层未实测**：InfoQueue 捕获路径在本机无法触发真实消息（见 ③ 降级说明）；
  建议装 Graphics Tools 后复跑窗口模式确认 Error=0。
- PRESENT==COMMON(0) 巧合若随 SDK 演进变化，行为不受影响（屏障仍合法），单测
  的 SDK 事实断言会显式报出提示复核。
- 设备级描述符堆固定 128 槽不回收：长会话/高频重建需扩容或回收器（后续任务）。
- 帧槽环由设备全局持有（8 槽）：假设"Begin 后须 End+Submit 才 Begin 下一表"
  （单在录命令表），多命令表并发记录需按表独立槽（后续任务）。
- 交换链仅窗口模式（windowHandle=null 直接失败返回无效句柄），headless 离屏
  场景由验证程序用 Commit 资源覆盖；窗口尺寸变化/重建流程未做（后续任务）。

## 验收清单（主控写）

1. `Tools/build_native.ps1` 全绿（含新增映射单测，全枚举断言核对）
2. `Native/build/bin/Release/zrhi_d3d12_window.exe` 验收代理亲自运行：优先窗口模式；若环境拒绝则 `--headless`；退出码 0 且输出含 `D3D12 WINDOW SMOKE OK` 与逐次颜色校验行
3. InfoQueue Error 计数为 0 的输出证据（或 DEBUG layer 不可用时注明降级）
4. 映射实现审查：无任何 ZrImageLayout→D3D12_RESOURCE_STATES 的隐式整型转换（读代码，switch 显式映射）
5. 全部资源释放路径存在（无泄漏的运行时证据：程序正常退出+WaitIdle 后再销毁；代码审查 ComPtr 使用）
6. 文件规范抽查（BOM/LF/#pragma once）
7. 施工报告完整（含两种模式实测说明）

## 验收结论（验收代理写）

**验收: general-purpose 验收子代理 | 2026-10-23 | 总判定: PASS**

逐项证据（全部独立亲测/亲审）：

1. **构建全绿 PASS** — 亲跑 `build_native.ps1`：`ALL GREEN: configure + build + test succeeded`（/W4 /WX 零警告，8 目标全过）；doctest `34 cases | 6651 assertions | 0 failed`。映射单测确为全枚举断言：格式 14 值独立期望表逐值比对 + `REQUIRE(表长==ZrFormat::Count)`（test_zrhi_d3d12_translate.cpp:25-48，zrhi_types.h:23-40 确认恰 14 值）；布局 9 值逐值比对 + 互异（含 PRESENT==COMMON SDK 事实断言，:65-107，zrhi_types.h:168-178 确认恰 9 值）；呈现 3 值参数断言（:109-136）。过滤实跑证实 3 用例在套件中。
2. **窗口程序亲测 PASS** — 窗口模式（本环境允许建窗，未降级）：退出码 0、输出含 `D3D12 WINDOW SMOKE OK`、6 条颜色校验行逐条 OK（frame 10/20/30/40/50/60 中心像素 RGB 精确匹配预期、容差 ±2）+ UAV 路径校验 OK、60 帧后台缓冲 0/1/2 轮转正确；`--headless` 模式复跑亦退出码 0、同构输出。
3. **InfoQueue 证据 PASS（降级已注明）** — 程序打印 `debug layer UNAVAILABLE (degraded mode)` 及显式降级说明 `note: debug layer unavailable, InfoQueue count will report 0 (degraded)`（zrhi_d3d12_window.cpp:230-237），未谎报；与施工报告 ③/⑤ 的环境事实一致。
4. **无隐式整型转换 PASS** — grep 全目录 cast 复核：zrhi/d3d12/ 下零处 `ZrImageLayout→D3D12_RESOURCE_STATES` / `ZrFormat→DXGI_FORMAT` 整型直转；唯一转译点 zrhi_d3d12_translate.cpp:19-87（三函数全枚举显式 switch + default assert）；全部使用点均经映射函数：屏障 zrhi_d3d12_commandlist.cpp:88-89、图像格式 zrhi_d3d12_device.cpp:210、交换链格式 :277。其余 static_cast 均为尺寸（UINT16）/指针（HWND）/句柄表 payload 转换，无枚举跨 API 直转。
5. **资源释放路径 PASS** — COM 指针全 `Microsoft::WRL::ComPtr`（zrhi_d3d12_device.h:45/57/63-64/111/116/121/202-205/210/219-220；window.cpp:323-326），唯一裸 HANDLE m_fenceEvent 于析构 CloseHandle（device.cpp:142-145）；退出序列 window.cpp:473-490：WaitIdle(473)→Destroy 命令表(474)→Destroy 交换链(476，后台缓冲整组随私有堆释放，device.cpp:421-439)→Destroy 离屏/UAV 图(478-481)→readback ComPtr Reset(482-483)→DestroyWindow(484-486)→GetDebugErrorCount(489)→delete 设备(490，析构断言句柄表全空)。两模式实测均正常退出码 0。
6. **文件规范抽查 PASS（附观察）** — 抽查全部 8 个新文件：无 BOM（头三字节 `2f 2f 20`）、无尾随空格、末行有换行、3 个头文件 `#pragma once` 齐（cpp 无，正常）。观察（不判 FAIL）：磁盘行尾为 CRLF，与全仓既有已验收文件完全一致（HEAD 入库形态为 LF，`.gitattributes * text=auto eol=lf` 于 git add 时统一规范化；含本任务前所有任务的文件同形态，非本任务引入）。
7. **施工报告完整 PASS** — ①-⑤小节齐全（摘要/交付清单/自测结果/设计偏差/风险与遗留），两种模式实测说明明确（③：实际使用模式 window，--headless 另行单独跑通）。

**附注**：① `UnityProject/Assets/Plugins/ZRender/zrender.dll` 已存在（11776 字节，本次构建第 4 步亦重新拷贝成功），无需待补。② 观察项（后续任务建议，不影响判定）：window.cpp:327-330 readback 缓冲创建失败的提前 return 路径未显式销毁已建句柄（Release 下设备析构 assert 不触发）；设备级描述符槽 128 不回收（施工报告 ⑤ 已自曝）。

**总判定: PASS**（7/7 项通过；无 FAIL 项；观察项均已在施工报告 ④/⑤ 或本节记录）
