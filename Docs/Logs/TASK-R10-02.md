# TASK-R10-02 —— 独立代码审计（"测试绿灯照不到"的缺陷）

- **轨道**：跨轨（A+B）
- **状态**：done（验收结论：**部分可信**——复核 8 条 0 误报，2 条分级偏高、1 条文件位置写错，已按验收更正）
- **派发**：主控，2026-10-29

## 任务简报（主控写）

### 为什么做这个

先例：2026-10-26 阶段收口后做过一次独立审计，抓出一批**测试全绿但代码有真缺陷**的问题——
描述符槽耗尽未校验导致堆外写、Vulkan 描述符池 layout 违规（本机 AMD 侥幸放过）、`Present` HRESULT 静默吞错、资源泄漏。
那次审计的价值高于同期任何新功能。

自那之后又跑了 **R7（Vulkan 后端）/ R8（多线程与特性）/ R9（帧生成全链）三轮大改**，
新增了大量原生代码（`zfgprobe/` 代理与 hook、`zrhi/vulkan/`、FG 集成）与 Unity 侧接线。**再没审过。**

### 审计范围（按风险排序，可裁剪但要在报告说明）

**第一优先：本轮新增、复杂度最高**
1. `Native/zfgprobe/`——`zfg_dxgi_proxy.cpp`（DXGI 代理）、`zfg_probe_hook.cpp`（vtable hook）、
   `zfg_probe_wrap.cpp`（FFX 代理交换链）、`zfg_probe_dll.cpp`
   关注：**COM 引用计数**（AddRef/Release 配对、返回给宿主的对象谁负责释放）、**线程安全**（渲染线程 vs 安装线程、`thread_local` 语义）、
   环境变量解析（`ZFG_HOOK`/`ZFG_HOOK_LOG` 缓冲区与截断）、失败路径是否真回退、DllMain 里做了什么（**loader lock 死锁**风险）
2. `Native/zrhi/vulkan/`——设备/队列/交换链/描述符/PSO/屏障
   关注：`VkResult` 是否全部检查、对象销毁顺序、`vkWaitForFences` 超时处理、descriptor indexing 上限与 fallback、
   交换链重建（resize 路径是否存在但未测）
3. `Native/zrender/`——RenderGraph（生命周期/别名复用/屏障合并）
   关注：**资源别名复用的生命周期证明**（是否可能读写同一内存）、屏障合并是否漏 barrier、
   图执行中途失败时的资源回收

**第二优先：既有但高风险**
4. `Native/zrhi/d3d12/`——描述符堆、命令列表、围栏
5. `Native/zupscaler/`——FSR/FG 上下文生命周期、分辨率变化时的重建
6. `Native/zkernel/`——SoA 组件增删的边界（B-R8-03 曾修过 Remove 越位写，同类问题是否还有）
7. `UnityProject/Assets/ZFrame/`（Core/Data/View/Gameplay）——**存档原子性**、设置日志（A-R9-02/03 那套）、
   事件订阅泄漏、`IDisposable`/`OnDestroy` 配对、异步/协程中断路径
8. `UnityProject/Assets/ZRender/`——P/Invoke 封送（字符串/数组/回调的生命周期）、原生句柄与托管对象生命周期错配

### 审计方法（必须按这个来）

1. **先读测试，再读实现**：找出"测试覆盖了哪里"，然后**专挑测试没覆盖的分支**（错误路径、边界值、并发窗口、失败回退）
2. **每条结论必须给**：`文件:行号` + 代码片段 + **为什么现有测试抓不到** + 触发条件 + 后果（崩溃/泄漏/静默错误/数据损坏）+ 置信度（确定/高/中）
3. **允许写小探针验证**（临时程序或单测），但**不许改产品代码**——本任务只出清单，修复另开任务
4. **禁止**：纯风格问题、命名建议、"建议加注释"、"可以考虑重构"这类无缺陷内容。**清单要短而硬，不要长而软**

### 输出格式（写进本日志"审计报告"节）

```
## P0（会导致崩溃/数据损坏/安全边界）
### A-1 <一句话标题>
- 位置：path:line
- 代码：<关键片段 3-5 行>
- 为什么测试抓不到：<具体说明>
- 触发条件：<什么输入/时序>
- 后果：<崩溃/泄漏/静默错误>
- 置信度：确定/高/中
- 建议验证方式：<一句话>

## P1（功能错误或资源泄漏，特定条件下触发）
...
## P2（健壮性/可维护性风险，不阻塞）
...
## 审计未覆盖的区域（诚实说明）
```

### 硬约束

- **只读审计**：不改产品代码、不改测试、不 git commit
- 只写 `Docs/Logs/TASK-R10-02.md`
- 允许在 `TempLogs/r10_02/` 放探针与日志
- **不许把"看起来可疑"写成"确定缺陷"**——置信度必须诚实；不确定的放 P2 并标注
- 若发现某条其实是既有设计取舍（如"关态克隆管线 3% 开销"），不算缺陷，可在"设计观察"里一句话提及

### 交付

1. 审计报告写入本日志，按 P0/P1/P2 分级，**总数控制在 25 条以内**（宁精不滥）
2. 回传 ≤20 行摘要：各级条数、P0 每条一句话、你认为最值得先修的三条

---

## 审计报告（审计代理写）

- **审计对象**：HEAD `4dfab96`（工作区干净），只读，未改任何产品代码/测试，未 `git add`/`commit`。
- **方法**：先读测试（`Native/tests/*`、`UnityProject/Assets/*/Tests/*`）确定覆盖边界，再逐行读实现，专挑
  "测试明确没走到的分支"（错误路径/边界值/并发窗口/失败回退）。原生侧共读了约 5.6k 行（zfgprobe 全量、
  zrhi/vulkan 关键路径、zrender RG 全量、zkernel 全量、upscaler_chain/fg_ffx 生命周期），Unity 侧读
  SaveSystem / UI / FG 接线 / P-Invoke。
- **统计**：P0 1 条、P1 9 条、P2 7 条，共 17 条。
- **未编译任何探针**：本机 VS/CMake 可用，但编译会改写 `Native/build/` 与受跟踪的 `zrender.dll`（§3.7），
  与"只读"约束冲突，故全部结论以静态证据 + 仓内既有日志为准（引用了 `TempLogs/r9_04/*.log` 的实测行）。

---

## P0（会导致崩溃/数据损坏/安全边界）

### A-1 DXGI 代理对 5–7 参的导出只转发 4 个参数，`ppDevice` 被写到未初始化栈槽指向的野地址
- **位置**：`Native/zfgprobe/zfg_dxgi_proxy.cpp:38`、`:345-355`（错误断言在 `:11`）
- **代码**：
  ```cpp
  // :11  "本表 20 个导出均 ≤ 4 个参数，x64 全走 rcx/rdx/r8/r9"   ← 事实不成立
  using GenericFn = uintptr_t(WINAPI*)(uintptr_t, uintptr_t, uintptr_t, uintptr_t);   // :38
  extern "C" uintptr_t WINAPI DXGID3D10CreateDevice(uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t d)
  { return Forward(6u, a, b, c, d); }                                                 // :345-349
  ```
  `DXGID3D10CreateDevice` 的 MS 文档签名是 **7 参**（`hModule, pFactory, pAdapter, Flags, pExtensions, NumExtensions, ppDevice`）；
  `DXGID3D10CreateLayeredDevice` 亦 ≥ 6 参。
- **为什么测试抓不到**：`Native/tests/zfg_probe_selftest.cpp` 只走 `CreateDXGIFactory1 → CreateSwapChainForHwnd → Present×3 →
  ResizeBuffers`（第 70–140 行），20 个转发导出里只碰到 3 个；Unity/URP 只用 `CreateDXGIFactory2`。阶段 A 门禁
  （B-R9-03）用 `dumpbin` 比对的是**导出名与序号**，dumpbin 不显示参数个数，所以"签名截断"这一类缺陷在
  门禁与 ctest 里都没有任何断言面。
- **触发条件**：宿主或任何注入的兼容/捕获层调用 `DXGID3D10CreateDevice`（老 D3D10 互操作路径）。
- **后果**：第 5–7 个参数由被调函数从**本转发函数自己的栈帧**读取（x64 只有前 4 个走寄存器），内容未初始化；
  `DXGID3D10CreateDevice` 会把新建的 `ID3D10Device*` 写到第 7 参 `ppDevice` 指向的野地址 → 任意地址写 / AV。
  这是"每份 Player 都随包分发"的 shim 里的内存破坏面，不是纯理论问题。
- **置信度**：确定（签名个数由 MS 文档固定；转发层只声明并传递 4 个）
- **建议验证方式**：写一个 7 参的假宿主在 Release 下调 `DXGID3D10CreateDevice`，看是否在写 `ppDevice` 时 AV。

---

## P1（功能错误或资源泄漏，特定条件下触发）

### A-2 工厂 vtable 槽 17 被当作 `CreateSwapChainForComposition`，实际是 `GetSharedResourceAdapterLuid`
- **位置**：`Native/zfgprobe/zfg_probe_hook.cpp:55`、打补丁处 `:1344-1348`
- **代码**：
  ```cpp
  constexpr size_t kSlotCreateSwapChainForComposition = 17u;   // :55
  ok = PatchSlotTracked(g_factoryVtbl, kSlotCreateSwapChainForComposition,
           reinterpret_cast<void*>(&HookCreateSwapChainForComposition), ...);
  ```
- **事实核对**（按 SDK 10.0.26100 `dxgi.h` / `dxgi1_2.h` 逐条数）：`IDXGIFactory` 有 **5** 个方法
  （EnumAdapters 7 / MakeWindowAssociation 8 / GetWindowAssociation 9 / CreateSwapChain 10 / **CreateSoftwareAdapter 11**），
  `IDXGIFactory1` 追加 12/13，`IDXGIFactory2`：IsWindowedStereoEnabled 14、ForHwnd **15** ✓、CoreWindow **16** ✓、
  **GetSharedResourceAdapterLuid 17**、…、CreateSwapChainForComposition 在 **24**。15/16 是对的，17 错了 7 个槽。
- **为什么测试抓不到**：selftest 只建 ForHwnd 链，Unity 桌面也不走 composition/共享资源；`PatchSlotTracked`
  只校验"槽非空 + 不是自家代码"，槽 17 里是真实 dxgi 函数指针 → 静默成功，日志还会打印
  `composition=0x…`（`:1358-1363`），看起来像装上了，实际装错。
- **触发条件**：宿主调用 `GetSharedResourceAdapterLuid`（UWP/共享纹理/部分捕获与合成工具）。
- **后果**：hook 以 5 参签名接 3 参调用，`outSwapChain` 取到 r9 的垃圾；只要 `SUCCEEDED(hr)` 成立就
  `*outSwapChain` 解引用并 `EnsureSwapchainHooks(*outSwapChain)` → AV。另一面：真正的
  `CreateSwapChainForComposition`（24）永不 hook → 走 Composition 的宿主静默拿不到插帧。
- **置信度**：确定（vtable 布局由接口定义固定；已逐槽核对头文件）
- **建议验证方式**：加一条断言/自测：对 `IDXGIFactory2` 实例打印 `vtbl[i]` 所属导出（`GetProcAddress` 反查），核对 24 号槽。

### A-3 代理链用自造 desc 建链，丢掉宿主的 Flags/BufferUsage/SwapEffect，却返回 S_OK
- **位置**：`Native/zfgprobe/zfg_probe_wrap.cpp:518-528`；调用点 `Native/zfgprobe/zfg_probe_hook.cpp:644`
- **代码**：
  ```cpp
  scd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
  scd.SwapEffect  = DXGI_SWAP_EFFECT_FLIP_DISCARD;
  scd.Flags       = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;   // 只保留这一位
  ```
  `HookCreateSwapChainForHwnd` 只把 `Width/Height/Format/BufferCount` 传进来，`desc->Flags / BufferUsage /
  SwapEffect / Scaling / AlphaMode / SampleDesc` 全部丢弃，成功后 `return S_OK`（`:667`）。
- **证据（本仓自己的日志）**：`TempLogs/r9_04/audit_fg_b.log:11` 记录 Unity 请求
  `usage=0x30 effect=3 flags=0x842`，代理实际建成 `usage=0x20 effect=4 flags=0x40`。
  按 SDK 枚举：`usage 0x30 = SHADER_INPUT(0x10) | RENDER_TARGET_OUTPUT(0x20)`；
  `flags 0x842 = ALLOW_TEARING(0x800) | FRAME_LATENCY_WAITABLE_OBJECT(0x40) | ALLOW_MODE_SWITCH(0x2)`；
  `effect 3 = FLIP_SEQUENTIAL`、`4 = FLIP_DISCARD`。即：**后台缓冲的 SHADER_INPUT 位、ALLOW_TEARING 位、
  FLIP_SEQUENTIAL 语义三者都被静默丢掉**。
- **为什么测试抓不到**：B-R9-03/04 的判据是"不崩 + 像素非黑 + 呈现 2×"，没有任何用例比对"请求 desc vs 实际 desc"；
  selftest 走原生路径不 wrap。功能正确性被"能跑"掩盖。
- **触发条件**：宿主把后台缓冲当 SRV 采样（Unity 请求了 SHADER_INPUT）、依赖 FLIP_SEQUENTIAL 的"缓冲内容保留"、
  或依赖 ALLOW_TEARING 的呈现节奏。
- **后果**：① 后台缓冲作 SRV 的路径（GrabPass/相机叠加/外部合成/某些 UI 合成）静默失效或行为改变；
  ② 帧节奏从"允许撕裂"变成"由 FFX 内部链决定"，实测 `tearing` 计数仍在涨且 `ok=7722/7722`——
  说明 FFX 代理层自己补了/吞了这个位，**行为依赖 FFX 内部实现，不受本仓控制**；
  ③ 返回 S_OK 让宿主以为拿到了自己请求的那条链，无从感知差异。
- **置信度**：高（desc 被丢弃、四组数值差异都有本仓日志为证）；具体后果 中（ALLOW_TEARING 一项已被实测证伪为"不报错"）
- **建议验证方式**：把 `desc` 原样透传给 FFX（只补 waitable 位），跑 `--fg` 后比对 `desc` 行与请求一致；
  再补一条"后台缓冲作 SRV"的用例。

### A-4 交换链重建后 `g_displayWidth/Height` 不更新：FG 静默失效 + 读回按旧尺寸越界拷贝
- **位置**：`Native/zfgprobe/zfg_probe_wrap.cpp:840-841`（只在建链时写一次）、`:221`/`:542`（拷贝 box 用它）、
  `:898`（尺寸不等就关插帧）；`Native/zfgprobe/zfg_probe_hook.cpp:866-880`（ResizeBuffers 只转发，不通知 wrap）
- **代码**：
  ```cpp
  g_displayWidth = width;  g_displayHeight = height;                       // :840-841 全仓仅此一处写
  const D3D12_BOX box {0u, 0u, 0u, g_displayWidth, g_displayHeight, 1u};   // :221 / :542
  if (want && haveInputs && (in.width != g_displayWidth || in.height != g_displayHeight)) { haveInputs = false; }  // :898
  ```
- **为什么测试抓不到**：HANDOFF 明写"`ResizeBuffers` 路径未验（两条链日志里调用 0 次）"；单测只有 mock；
  selftest 的 ResizeBuffers 打的是**原生**链（不 wrap），根本进不到 `CaptureObserver`。
- **触发条件**：FG 开着时玩家拖窗口改尺寸（flip 模型必走 `ResizeBuffers`）。
- **后果**：① 托管侧随后提交新尺寸 → `enabled=false`，**插帧永久静默失效**（只打一行 `s_sizeWarned` 日志）；
  ② `CaptureObserver` 仍按旧尺寸对新后台缓冲做 `CopyTextureRegion`——窗口变小时 box 超出源资源（D3D12 未定义行为，
  实测可能设备移除），变大时只读到左上角旧区域（像素证据失真）。
- **置信度**：高（状态不更新是确定事实；设备移除取决于驱动对越界拷贝的处理，故不写"确定崩溃"）
- **建议验证方式**：在 selftest 里 wrap 后 `ResizeBuffers` 到更小尺寸，开 D3D12 调试层看是否报 box 越界。

### A-5 Vulkan：`AcquireNextImage` 的 OUT_OF_DATE/失败路径继续往下走，Present 等一个永不 signal 的信号量
- **位置**：`Native/zrhi/vulkan/zrhi_vulkan_device.cpp:1346-1363`、`:1373-1390`
- **代码**：
  ```cpp
  if (result == VK_ERROR_OUT_OF_DATE_KHR || result == VK_SUBOPTIMAL_KHR) {
      std::printf("... resize unsupported\n");        // 打印完继续
  } else if (result != VK_SUCCESS) {
      assert(false && "AcquireNextImage FAILED");     // Release 下被编掉
      return 0u;
  }
  m_currentIndex = index;                              // OUT_OF_DATE 时 index 未被写入
  m_owner->SetPendingAcquire(imageAvailable, renderFinished[index], index);
  ```
- **为什么测试抓不到**：ctest 里 Vulkan 只有 SPIR-V 存在性（`test_zrhi_vulkan_spv`）与翻译层单测
  （`test_zrhi_vulkan_translate`），**没有任何用例创建交换链**；窗口程序 `--backend vk` 只跑过正常路径。
- **触发条件**：窗口最小化/拖到另一台显示器/全屏切换（OUT_OF_DATE），或设备丢失。
- **后果**：`VK_ERROR_OUT_OF_DATE_KHR` 时 `vkAcquireNextImageKHR` **不会** signal `imageAvailable`，代码却把它登记为
  pending acquire → 下一次 `Submit` 的 `pWaitSemaphores` 等一个永不 signal 的二进制信号量 → 队列永久卡死（窗口无响应）。
  硬失败分支同理：Present 去等上一帧已被消费的 `renderFinished[i]`，同样永久挂起。
- **置信度**：高
- **建议验证方式**：在窗口程序里最小化再恢复，观察是否挂死（或用 `VK_ERROR_OUT_OF_DATE_KHR` 注入桩）。

### A-6 Vulkan：`vkQueueSubmit` 返回值不检查 + `Begin` 里 `UINT64_MAX` 等 fence，设备丢失后整进程假死
- **位置**：`Native/zrhi/vulkan/zrhi_vulkan_device.cpp:1652`；`Native/zrhi/vulkan/zrhi_vulkan_commandlist.cpp:128`
- **代码**：
  ```cpp
  vkQueueSubmit(m_queue, 1u, &submitInfo, cmd->m_submitFence);   // 返回值直接丢弃
  cmd->m_pendingSubmit = true;
  ...
  vkWaitForFences(dev, 1u, &m_submitFence, VK_TRUE, UINT64_MAX);  // 下一帧 Begin
  ```
- **为什么测试抓不到**：同 A-5——没有提交路径的自动化用例；失败只在 TDR/驱动重置时出现，正常跑永远绿。
- **触发条件**：TDR、驱动重置、任何 `vkQueueSubmit` 返回非 SUCCESS 的情形。
- **后果**：fence 永不 signal → 下一帧 `Begin()` 在渲染线程永久阻塞 → 进程假死（比"报错退出"更难诊断）。
  同类问题：`vkAcquireNextImageKHR` 用 `UINT64_MAX` 超时，窗口最小化时会直接阻塞在 acquire 上。
- **置信度**：高（代码事实）；触发概率低
- **建议验证方式**：把两处返回值接进 `GetDebugErrorCount` 同级的计数并在 `WaitIdle` 前断言，再用注入桩打一次失败。

### A-7 FFX 每帧输入只存裸指针、还允许复用 2 次：资源被 Unity 释放后仍被 PrepareV2 引用
- **位置**：`Native/zfgprobe/zfg_probe_wrap.cpp:92-93`、`:874-884`（reuse 分支）、`:695-696`（`args.nativeDepth = in.depthResource`）
- **代码**：
  ```cpp
  constexpr int kMaxInputReuse = 2;
  } else if (g_reuseLeft > 0) { g_reuseLeft -= 1; in = g_inputs; haveInputs = true; }
  ```
  `ZfgFgFrameInputs.depthResource/mvResource` 是裸 `ID3D12Resource*`，`WrapSubmitInputs` 只做 `memcpy`，不 AddRef。
- **为什么测试抓不到**：`test_framegen_inputs` 只测 `WrapSubmitInputs` 的参数校验（structSize/version/空指针）；
  真机跑的是"每帧都有新输入"的理想节奏（HANDOFF：`prepareOk:generated = 1:1`），reuse 分支 3847 帧只触发 1 次，
  且那次没有伴随资源销毁。
- **触发条件**：托管侧某帧没提交（pass 未入队、相机切换、`_CameraDepthTexture` 为空）而宿主继续 Present → 复用旧指针；
  此时 Unity 已因改画质档/切分辨率/切相机销毁重建了 depth/MV 纹理。
- **后果**：`PrepareV2` 拿到已释放的 `ID3D12Resource*` → 命令表引用野资源 → 设备移除/花屏。
- **置信度**：中（裸指针是确定事实；Unity 是否会在"无提交帧"的窗口内释放资源取决于时序）
- **建议验证方式**：在 `WrapSubmitInputs` 里对两个指针做 `AddRef`/`Release` 配对（或在 reuse 超过 1 次时强制 stale）。

### A-8 UI 淡入淡出的 Task 在协程被打断时永不完成 → `UIManager.Push` 永久挂起、面板永久"在途"
- **位置**：`UnityProject/Assets/ZFrame/View/UI/UIAnimRunner.cs:43-61`；`UIManager.cs:193`（`await FadeAsync`）、
  `:115/:126`（in-flight 标记）、`:204-208`（finally 才清除标记）
- **代码**：
  ```csharp
  var completion = new TaskCompletionSource<bool>();
  StartCoroutine(FadeRoutine(group, from, to, duration, completion));
  return completion.Task;              // 协程被 StopAllCoroutines/宿主失活打断时永不 SetResult
  ```
- **为什么测试抓不到**：EditMode 走 `!Application.isPlaying` 的立即完成分支（`UIAnimRunner.cs:37-41`），
  **协程体一行都不执行**；PlayMode 的 6 条用例不覆盖"淡入途中 UIRoot 失活/面板被销毁"。
- **触发条件**：0.15s 淡入期间 UI root 被 `SetActive(false)`/销毁、或面板实例被销毁（`group` 变成 Unity fake-null →
  `group.alpha` 抛 `MissingReferenceException`，协程终止且异常被协程机吞掉）。
- **后果**：`await` 永不返回 → `Push` 的 `finally` 不执行 → `_pushingViews` 永久保留该 viewType，之后每次 Push 同一面板
  都 `return false`；调用方（含存档/过场流程）也永远 await 不返回 → UI 软锁。
- **置信度**：高
- **建议验证方式**：PlayMode 用例：Push 一个 `AnimIn=Fade` 的面板后立刻销毁实例，断言 Task 在超时内完成（当前会超时）。

### A-9 相机切换时旧的 `requiresDepthOption=On` 永不还原
- **位置**：`UnityProject/Assets/ZRender/Runtime/ZrFrameGenerationFeature.cs:172-183`；
  `ZrFrameGenerationQualityOverrides.cs` 的 `BindCamera`（直接覆盖 `_camera`，不先还原旧目标）
- **代码**：
  ```csharp
  if (camera != s_preparedCamera) {
      s_quality.BindCamera(new UrpFrameGenerationCameraDepthTarget(additional));  // 覆盖上一个相机，不还原
      s_preparedCamera = camera;
  }
  ```
- **为什么测试抓不到**：B-R9-08 的用例只注入**一个**桩相机，断言"开→On / 关→原值"；多相机与换相机是
  HANDOFF 明列的未覆盖项。
- **触发条件**：FG 开着时活动相机变化（过场相机 → 玩家相机、相机销毁重建、多相机叠加）。
- **后果**：旧相机的 `requiresDepthOption` 永久停在 `On`（URP 每帧多渲一遍深度）；`RestoreQualityOverrides` 只还原
  最后一个绑定的相机，关掉 FG 也救不回来，要重启进程。
- **置信度**：高
- **建议验证方式**：EditMode 用两个桩相机走生产入口，断言第二个相机绑定时第一个已还原。

### A-10 `RgGraph::Execute()` 丢掉 split 屏障（`PlanBarriers` 对 split 场景不产内联屏障）
- **位置**：`Native/zrender/rg_render_graph.cpp:641-654`（split 分支不 push 内联批次）、`:912-946`（`Execute` 只发内联批次）
- **代码**：
  ```cpp
  if (desc.type == RgResourceType::Image && dsvWriteBefore && toReadOnly && ...) {
      m_barrierPlan.splits.push_back(split);        // 只记计划，不进 perPassImageBatches
  } else if (...) { /* 内联屏障 */ }
  ...
  void RgGraph::Execute(...) { /* 只读 perPassImageBatches/perPassBufferBatches，从不读 splits */ }
  ```
- **为什么测试抓不到**：所有 split 的**下发**断言都在 `ExecuteAliased`（`test_rg_execute.cpp:104-140`）；
  场景 9（`test_rg_barriers.cpp:145-188`）只 `Compile()` 后断言 `plan.SplitCount()`，**从不 Execute**；
  另两个走 `Execute` 的用例（`:139`/`:240`）图里没有"深度写→深度读"。
- **触发条件**：任何调用公共 API `RgGraph::Execute` 且图里存在 DSV 写→读的用例。
- **后果**：D3D12/VK 上都缺一道 `DEPTH_WRITE → DEPTH_READ` 转换 → 深度采样读到未定义 tile 状态（花屏/脏读）。
  当前生产路径走 `ExecuteAliased`，故属潜伏缺陷，但 `Execute` 是对外公开 API。
- **置信度**：高
- **建议验证方式**：把场景 9 改成 `Execute` 并断言 `commandList.barrierCalls` 至少有一条 `DepthStencilReadOnlyOptimal`（当前为 0）。

---

## P2（健壮性/可维护性风险，不阻塞）

### B-1 嵌套重入标志未保存/恢复，嵌套层级 >1 时失效（正是 B-R9-03 那个双推 frameID 的根因）
- **位置**：`Native/zfgprobe/zfg_probe_hook.cpp:804-811`、`:854-861`
- **代码**：`if (outerWrapped) { t_inWrappedPresent = true; } … if (outerWrapped) { t_inWrappedPresent = false; }`
- **为什么测试抓不到**：正确性依赖"FFX 的 Present1 内部只再调一次 Present、且在同一线程、且在调用期间"这条隐式不变式，
  全仓没有任何断言或日志校验它；B-R9-04 的实证只覆盖了 1 层嵌套。
- **触发条件**：FFX 版本变化后 Present1 内部连调两次 Present，或出现 Present1→Present→Present1 的更深嵌套。
- **后果**：内层返回时把标志清成 false，外层后续的嵌套 Present 被当成应用级 → `WrapOnFrame` 再跑一次 →
  frameID 双推进 / 一次 Prepare 对两次 Present（generated≈0 的那类症状）。
- **置信度**：中
- **建议验证方式**：改成保存/恢复旧值（`const bool prev = t_inWrappedPresent; … t_inWrappedPresent = prev;`）即可闭环。

### B-2 环境变量缓冲截断：`ZFG_HOOK` / `ZFG_HOOK_LOG` 超长时读未初始化/无终止符的缓冲
- **位置**：`Native/zfgprobe/zfg_dxgi_proxy.cpp:101-106`；`Native/zfgprobe/zfg_probe_hook.cpp:1370-1377`
- **代码**：`char mode[32]; GetEnvironmentVariableA("ZFG_HOOK", mode, 32); … std::strcmp(mode, "0")`
- **为什么测试抓不到**：所有真机跑都用短值（`fg`/`wrap`）；没有任何用例覆盖"值 ≥ 32 字符"。
- **触发条件**：部署脚本把 `ZFG_HOOK`/`ZFG_HOOK_LOG` 设成超长值（`MAX_PATH` 对日志路径尤其容易）。
- **后果**：`GetEnvironmentVariableA` 缓冲不足时返回所需长度但**不保证 NUL 结尾**（内容未定义）→ `strcmp` 越界读，
  或日志路径变成乱码 → `CreateFileA` 失败 → **证据静默丢失**（§3.5 已经丢过一次证据）。
- **置信度**：高（API 语义确定）；后果依赖具体值
- **建议验证方式**：用 64 字符的 `ZFG_HOOK_LOG` 跑一次，看日志是否落在预期路径。

### B-3 `WrapShutdown()` 无任何调用点、也未导出：FFX 上下文与代理交换链永不关停
- **位置**：`Native/zfgprobe/zfg_probe_wrap.cpp:1030`（定义）、`zfg_probe_wrap.h:64`（声明）；
  `zfg_probe_dll.cpp` 的 5 个导出里没有它
- **为什么测试抓不到**：真机验收只看运行期指标，进程退出后没人校验"上下文是否 DestroyContext / waitable 是否 CloseHandle"；
  单测只测 `WrapCreateSwapChainForHwnd` 的失败路径。
- **触发条件**：每次正常退出。
- **后果**：FFX 的 pacing 线程与代理交换链活到进程终止（`g_device`/`g_queue` 的 AddRef 也永不 Release）；
  退出期若 DLL 先卸载、FFX 线程仍在跑，可能出现退出崩溃/卡住（`WrapShutdown` 注释自己写着"进程退出路径上没有可靠的关停时机"）。
- **置信度**：高（无调用点是事实）；后果 中
- **建议验证方式**：导出 `ZFG_FgShutdown` 并在 `Application.quitting` 调；先确认退出是否偶发崩溃。

### B-4 Vulkan bindless 只校验了 sampled-image 的 update-after-bind 上限，storage-image 未校验
- **位置**：`Native/zrhi/vulkan/zrhi_vulkan_device.cpp:237-246`（只查 `…UpdateAfterBindSampledImages`）；
  binding 1 是 `STORAGE_IMAGE` 数组，长度同为 `kBindlessCapacity`（`:283-296`）
- **为什么测试抓不到**：只在 780M（AMD 桌面驱动，两个上限都很高）上跑过；没有"低上限设备"的模拟。
- **触发条件**：`maxPerStageDescriptorUpdateAfterBindStorageImages` 或 `maxDescriptorSetUpdateAfterBindStorageImages`
  小于 `kBindlessCapacity` 的设备（部分移动/老驱动）。
- **后果**：`vkCreateDescriptorSetLayout` 返回错误 → `Create()` 直接返回 nullptr → 后端"不可用"，
  而实际上把容量调小就能跑（误报为不支持）。
- **置信度**：高（代码事实）；影响面取决于设备
- **建议验证方式**：把两个 storage-image 上限也纳入同一守卫，并按 min(limits) 决定容量。

### B-5 `EmitBarriers` 超过 128 条静默截断
- **位置**：`Native/zrhi/vulkan/zrhi_vulkan_commandlist.cpp:18,183,217`
- **代码**：`for (uint32_t i = 0; i < imageBarrierCount && imageCount < ZR_VULKAN_MAX_BARRIERS; ++i)`
- **为什么测试抓不到**：单次 pass 的屏障批次在现有图里远小于 128，用例没有构造大批次。
- **触发条件**：某个 pass 一次声明 >128 条屏障（大图/多资源批处理）。
- **后果**：多出的屏障被丢弃且无日志 → 缺失同步（花屏/脏读），排查成本极高。
- **置信度**：高（截断是代码事实）；触发需要大图
- **建议验证方式**：截断时打一条 warning 并计入 `GetDeviceStats`。

### B-6 `EnsureCapture` 中途失败泄漏已建读回资源，下次重试再建一遍
- **位置**：`Native/zfgprobe/zfg_probe_wrap.cpp:173-183`
- **代码**：`for (int i = 0; i < kCaptureSlots; ++i) { ... if (FAILED(...)) return false; g_capture[i].resource = res; }`
- **为什么测试抓不到**：只在显存不足时发生，真机从未触发。
- **触发条件**：第 N 个 `CreateCommittedResource` 失败（显存压力）。
- **后果**：已创建的 N 个读回资源既不在 `g_captureReady` 也不被释放，且下一次 Present 会再建 N 个 → 泄漏放大。
- **置信度**：中
- **建议验证方式**：失败时把 0..i-1 的 resource Release 并置空。

### B-7 wrap 路径不校验 `outSwapChain != nullptr`：建了代理链又丢掉，仍返回 S_OK
- **位置**：`Native/zfgprobe/zfg_probe_hook.cpp:661-667`
- **代码**：`auto* sc1 = static_cast<IDXGISwapChain1*>(wrapped); … if (outSwapChain != nullptr) { *outSwapChain = sc1; sc1->AddRef(); } return S_OK;`
- **为什么测试抓不到**：正常宿主不会传空 `ppSwapChain`（DXGI 自己会返回 E_INVALIDARG），selftest 也不传空。
- **触发条件**：宿主传 `ppSwapChain == nullptr`。
- **后果**：原生语义应返回 `E_INVALIDARG`；这里返回 S_OK、不写输出、且已创建整条 FFX 代理链（泄漏 + 后续 `g_fg != nullptr`
  导致再也无法 wrap 第二条链）。
- **置信度**：中
- **建议验证方式**：入口先判 `outSwapChain == nullptr` 直接返回 `E_INVALIDARG`。

---

## 设计观察（不是缺陷，别当清单项修）

1. FG 关态克隆管线约 3% 开销（B-R9-07 既有设计）；`TestPanelView` 仍随 Player 包、ctest 探测不稳——均为
   `Docs/交接档案.md §5` 已登记的技术债，本次不重复计入。
2. 别名堆复用未显式下发 D3D12 **aliasing barrier**（`rg_render_graph.cpp:733-741/775-777` 只有 Undefined→X 的转换屏障）。
   D3D12 文档对"COMMON 出发的转换屏障是否等价于 aliasing barrier"的表述我无法在本机证实，故**不作为缺陷上报**，
   建议在真机 + 调试层下补一次 `CopyTextureRegion`/采样读回验证。
3. `zfg_probe_dll.cpp:68` 在 DllMain 里 `CreateThread`：线程要等 loader lock 释放才真正开跑（`Sleep(50)` 兜底），
   实测未死锁；但该线程无 join，`FreeLibrary` 时存在极小窗口的 UAF。归入 B-3 的同一类生命周期问题。
4. `DumpSummaryImpl` 对 `g_chains[i].sc` 调 `GetLastPresentCount`（`zfg_probe_hook.cpp:468-495`），而链记录**从不随
   交换链销毁移除**（没有 Release 钩子）。宿主重建交换链后再调 `ZFG_FgAnalyzeCaptures` 就是对已释放对象调虚函数（UAF）。
   当前只有验收探针会调这个入口（C# 侧无绑定、`R908Harness` 已剥离），故不升级为缺陷。

---

## 审计未覆盖的区域（诚实说明）

1. **所有真机路径**：本任务未启动 Unity、未跑 GPU 程序。凡是"需要真实设备才能确认后果"的结论（A-4 设备移除、
   A-7 野资源、A-5/A-6 挂起）都只到"代码路径确定 + 后果待实测"这一步，置信度已按此标注。
2. **Unity 侧渲染/存档的既有治理面**：`RenderScaleRecovery` / `RenderScaleRecoveryJournal` / `FrameGovernorHost` 的
   落盘-写资产时序（A-R9-02/03 已三轮返工，HANDOFF 已登记债务 #3）本次只做了接口一致性抽查，未再逐行审。
3. **`Native/zrender/` 的算法实现**：`shadows.cpp` / `ssr_pass.cpp` / `gi_probes.cpp` / `cluster_lighting.cpp` 只读了
   H-02 修过的边界，未逐行审算法正确性（本次按简报把 zrender 的审计面收在 RenderGraph）。
4. **`Native/zupscaler/`**：只读了 `upscaler_chain.cpp` 状态机与 `fg_ffx.cpp` 的上下文/交换链生命周期；
   `fsr_upscaler.cpp` / `dlss_upscaler.cpp` / `bilinear_upscaler.cpp` 内部未审（H-02 修过其 Resize 上限与初始化泄漏）。
5. **`Native/zrhi/d3d12/`**：只抽查了 H-02 修复点（7 处 `Allocate()` 校验、Present HRESULT 记录、围栏等待）。
   描述符堆容量规划、根签名/PSO 缓存未审。
6. **`Native/zkernel/`**：读了 `RemoveRowSwapBack` / `MigrateComponent` / `Compact` / `EachBatches` 的边界，
   未审测试覆盖，也未做大规模压力推演（B-R8-03 已修过一次 Remove 越位写）。
7. **`Native/zplugin/`、`Tools/*.ps1`、Unity 编辑器工具**（`Editor/Tools/*`、`ArchitectureValidator`）未审。
8. **Editor 内行为**：FG 在 Editor 明确不装配（`TryInstallPipeline` 直接拒绝），因此 Editor 侧只审了"拒绝路径"。
9. **并发**：没有做线程模型的形式化推演；`g_scHooks`/`g_chains` 的无锁读（`FindHooks`/`FindChain` 在锁外读
   `g_scHookCount`）按 x86 内存序判断为良性，未列为缺陷，但也没有证据证明在所有平台安全。

## 验收清单（主控写）

- [ ] V1 抽样复核：从 P0/P1 中各抽 2 条，验收代理独立读代码确认缺陷成立（行号、逻辑链对得上）
- [ ] V2 反证检查：至少抽 1 条 P0/P1，尝试构造"其实不会发生"的反例；若反例成立 → 该条判不成立
- [ ] V3 分级合理性：P0 是否真到"崩溃/数据损坏"级别，有无把 P2 拔高
- [ ] V4 诚实性：置信度标注是否与证据匹配；"测试抓不到"的理由是否具体（不是"没测"两个字）
- [ ] V5 只读性：`git log -1` 未变、`git status` 无产品代码改动
- [ ] V6 覆盖面说明：未审计区域是否如实列出

## 验收结论（验收代理写）

- **验收对象**：本日志"审计报告"节（HEAD `4dfab96`），**不是**产品代码。
- **方法**：对每条被指位置独立读代码 → 用本机 Windows SDK 10.0.26100 头文件核对 ABI 槽位 → 用仓内既有产物
  （`TempLogs/r9_04/player/UnityPlayer.dll` 导入表）与既有日志做反证 → 检查分级/诚实性/只读性/覆盖面。
  未改任何产品代码、未编译、未 `git add`/`commit`。
- **复核范围**：8 条（P0 全部 1 条 + P1 抽 7 条：A-1/A-2/A-3/A-4/A-5/A-6/A-8/A-10）。
- **一句话**：**8 条缺陷全部成立、0 误报**；但有 **1 条 P0 分级偏高、1 条 P1 分级偏高、1 条位置写错文件、
  2 处证据表述与自身日志不符**。总结论 **部分可信**。

---

### V1 抽样复核（关键）

| 条目 | 我独立核对的位置 | 判定 |
|---|---|---|
| A-1 | `zfg_dxgi_proxy.cpp:38` / `:282-292` / `:345-348` | 成立 |
| A-2 | `zfg_probe_hook.cpp:55` / `:1344-1347` / `:1359-1366` + SDK 头 | 成立（17/24 两个数字**精确正确**） |
| A-3 | `zfg_probe_hook.cpp:644-645` / `:667` + `zupscaler/fg_ffx.cpp:535-542` | 成立，**位置字段写错文件** |
| A-4 | `zfg_probe_wrap.cpp:840-841` / `:221` / `:542` / `:898` + `zfg_probe_hook.cpp:866-887` | 成立 |
| A-5 | `zrhi_vulkan_device.cpp:1342-1363` / `:1373-1390` | 成立（两处措辞需修正） |
| A-6 | `zrhi_vulkan_device.cpp:1652` + `zrhi_vulkan_commandlist.cpp:128` | 成立 |
| A-8 | `UIAnimRunner.cs:37-41` / `:52-65` + `UIManager.cs:124-128` / `:193` / `:205-207` | 成立 |
| A-10 | `rg_render_graph.cpp:641-654` / `:912-946`（`splits` 只在 `:797`/`:857` 被消费） | 成立，分级偏高 |

#### A-1（P0）签名截断 —— 成立

`Forward(size_t, uintptr_t×4)`（`:282-292`）用 `GenericFn`（`:38`，4 参函数指针）调用真实地址（`:291`）；
`DXGID3D10CreateDevice`（`:345-348`）只声明 4 个形参并把 4 个透传给 `Forward`。MS 文档签名 7 参
（`hModule, pFactory, pAdapter, Flags, pExtensions, NumExtensions, ppDevice`）→ 第 5–7 参只能从 `Forward`
自己的栈帧读，未初始化。同族的 `DXGID3D10CreateLayeredDevice`（`:350-354`）同样是 4 参截断。
文件头 `:11` 那句"本表 20 个导出均 ≤ 4 个参数"**是错的**——这是缺陷的根，不是笔误。

#### A-2（P1）槽位装错 —— 成立，且槽位号经两次独立核对

用 `C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\shared\dxgi.h` 与 `dxgi1_2.h`，
分别按 **C++ `virtual` 声明顺序** 与 **C 风格 `IDXGIFactory2Vtbl` 结构体成员顺序** 各数一遍，结果一致：

```
0-2   IUnknown          : QueryInterface / AddRef / Release
3-6   IDXGIObject       : SetPrivateData / SetPrivateDataInterface / GetPrivateData / GetParent
7-11  IDXGIFactory      : EnumAdapters / MakeWindowAssociation / GetWindowAssociation /
                          CreateSwapChain(10) / CreateSoftwareAdapter(11)
12-13 IDXGIFactory1     : EnumAdapters1(12) / IsCurrent(13)
14    IsWindowedStereoEnabled
15    CreateSwapChainForHwnd          ← 代码写 15，正确
16    CreateSwapChainForCoreWindow    ← 代码写 16，正确
17    GetSharedResourceAdapterLuid    ← 代码当成 Composition，装错
18-23 RegisterStereoStatusWindow / RegisterStereoStatusEvent / UnregisterStereoStatus /
      RegisterOcclusionStatusWindow / RegisterOcclusionStatusEvent / UnregisterOcclusionStatus
24    CreateSwapChainForComposition   ← 真正该 patch 的槽
```

`IDXGIFactory2Vtbl` 共 25 个函数指针，Composition 是第 25 项 → 下标 **24**。**审计报的 17 和 24 都对**，
不是估的（本机 SDK 的 `IDXGIFactory2` 只声明 11 个方法，与 MS 文档列的 13 个不同；两种数法下 17 仍是
`GetSharedResourceAdapterLuid`，24 仍是 Composition，结论不变）。

补充实证：`TempLogs/r9_04/audit_fg_b.log:5` 打印 `composition=0x00007FFDF3A30440`，落在同文件 `:1` 记录的
系统 dxgi 地址区间（real=0x7FFDF39A0000）内——说明该槽里躺着的是 **dxgi 的另一个真实导出**，与"装错槽
且 `PatchSlotTracked` 静默放行"完全吻合。`PatchSlotTracked`（`:902-946`）只校验"非空 + 非自家代码"，
确实无法识别装错。

#### A-4（P1）重建后尺寸不更新 —— 成立

`g_displayWidth/Height` 全仓赋值点只有 `:58-59`（初始化 0）与 `:840-841`（建链时写一次）；`g_captureReady`
只出现在 `:117/:139/:184/:595`，**没有任何一处重置**；`HookResizeBuffers`（`zfg_probe_hook.cpp:866-887`）
只转发 + 打日志。→ resize 后 capture 资源仍是旧尺寸、尺寸比较 `:898` 仍用旧值。三条链闭合，成立。

#### A-5 / A-6（P1）—— 成立

`AcquireNextImage`（`:1342-1363`）在 `OUT_OF_DATE/SUBOPTIMAL` 分支只 printf，然后照常
`SetPendingAcquire(imageAvailable, renderFinished[index], index)`；`vkAcquireNextImageKHR` 在该结果下不
signal 信号量 → 下一次 Submit 的 `pWaitSemaphores` 等一个永不 signal 的二进制信号量。`vkQueueSubmit`
（`:1652`）返回值丢弃、`Begin`（`commandlist.cpp:128`）用 `UINT64_MAX` 等 fence。均成立。

#### A-8（P1）UI 软锁 —— 成立

`Fade`（`UIAnimRunner.cs:37-41`）在 `!Application.isPlaying` 时直接 `Task.CompletedTask`，协程体一行不执行；
`FadeRoutine`（`:52-65`）只在自然结束时 `TrySetResult`，协程被停（GameObject 失活）或 `group` 变
fake-null 抛 `MissingReferenceException` 时 TCS 永不完成。`UIManager` 的 `_pushingViews.Add` 在第一个
`await` 之前（`:124`），清理在 `finally`（`:207`）——await 不返回则 finally 不执行。且 `FadeAsync`（`:431-448`）
**没有任何超时/取消**。成立。补充核对：`Tests/Debug/UIPushRobustnessTests.cs` 无 `Fade`/`SetActive(false)`
中途销毁用例，审计的"测试抓不到"属实。

#### A-10（P1）split 屏障丢失 —— 成立

`splits` 只在 `:797` 与 `:857` 被读（都在 `ExecuteAliased` 里）；`RgGraph::Execute`（`:912-946`）只读
`perPassImageBatches/perPassBufferBatches`。`test_rg_barriers.cpp:164-188`（场景 9）确实只 `Compile()` +
断言 `SplitCount()==1`，**从不 Execute**；走 split 下发的断言全在 `test_rg_execute.cpp:108-130`
（`ExecuteAliased`）。成立。

---

### V2 反证检查（关键）

**对象：A-1（P0）。** 尝试构造三条"其实不会发生"：

1. **"宿主根本不调这个导出"** —— 查 `TempLogs/r9_04/player/UnityPlayer.dll` 的导入名：
   只有 `CreateDXGIFactory`(×4) / `CreateDXGIFactory1`(×3) / `CreateDXGIFactory2`(×2)，
   **没有任何 `DXGID3D10*` 导入**；`ZfgR903.exe` 同样没有。→ **反例部分成立**：随包分发的 Player 正常路径
   永远走不到这个导出。
2. **"只有 GetProcAddress 才够得着"** —— 成立。第三方覆盖层/捕获层/兼容层会取这些老 D3D10 互操作导出，
   但这是外部条件，不是本仓路径。
3. **"真实 dxgi 该导出可能直接失败、不写 ppDevice"** —— 本机无法证实（要跑探针，与只读约束冲突），
   **不能作为免除理由**：即使它返回失败，垃圾参数也已经塞进被调函数了。

**判定**：反例 1 成立 → **缺陷本身成立**（代码确实错，且"20 个导出均 ≤4 参"的断言确实为假），
但 **P0 的立论不成立**——"每份 Player 都随包分发"容易被读成"随包就会崩"，实际是"随包但仓内无调用点"。
→ **A-1 需降级 P0 → P1**。

**对象：A-4（附带反证）。** 假设"resize 时 capture 会被重建"——`g_captureReady` 全仓无重置点，
`g_displayWidth/Height` 无第二处赋值。反例不成立 → A-4 成立。

---

### V3 分级合理性

- **A-1：P0 → 建议 P1。** 后果（野指针写）确实是崩溃级，字面符合 P0 定义；但触发面在仓内为零（见 V2）。
  按"要不要立刻停手"的实务口径应排 P1。**这是唯一一条需要降级的 P0**。
- **A-10：P1 → 建议 P2。** `Execute` 是公开 API（`rg_render_graph.h:274`），但全仓调用点只有 5 处测试
  （`test_rg_barriers.cpp:139/240`、`test_rg_compile.cpp:55/114/222`），生产全走 `ExecuteAliased`。
  审计自己已写"属潜伏缺陷"——**无生产调用点的公共 API 潜在错误应是 P2**。
- **其余 6 条 P1 分级恰当**：A-2（装错槽 + 静默成功，会 AV）、A-3（desc 语义被改）、A-4（真机 resize
  必现）、A-5/A-6（队列永久挂死）、A-8（UI 软锁）。
- **P2 七条（B-1~B-7）未发现拔高**：都是"特定条件 + 后果不确定"，且都标了"中/高"并说明了依赖项。

---

### V4 诚实性

**做得好的**

- **A-3 的自我证伪是真的、且到位**：`TempLogs/r9_04/audit_fg_b.log:354` 确有
  `presents: total=7722 (Present=3861 Present1=3861) ok=7722 notOk=0 tearing=7260`。用它推翻
  "ALLOW_TEARING 必失败"，并把后果置信度降为"中"——没有藏，也没有把整条删掉了事（核心缺陷仍在）。
- **"为什么测试抓不到"全部具体**：抽查 8 条都能落到文件名/行号/具体断言（selftest 只碰 3 个导出、
  场景 9 只 `Compile()`、EditMode 走 `!isPlaying`、VK 无交换链用例），没有"没测"两个字了事。
- **不确定的地方标了不确定**：A-7/B-6 标"中"、A-10 自称"潜伏"、设计观察 2 明说"无法在本机证实故不上报"。

**需要修正的**

1. **A-3 位置写错文件**：引用的 `zfg_probe_wrap.cpp:518-528` 里**没有**这段代码（那里是 native capture
   的 `Reset`/barrier）。`scd.BufferUsage/SwapEffect/Flags` 实际在 **`Native/zupscaler/fg_ffx.cpp:535-542`**。
   按原位置派修复任务会找不到点。
2. **A-3 的 Flags 论断与自己的日志矛盾**：`audit_fg_b.log:17` 显示 FFX 真正建链用的是
   `usage=0x20 effect=4 flags=0x840`——`ALLOW_TEARING(0x800)` **被 FFX 补回去了**，真正丢的只有
   `ALLOW_MODE_SWITCH(0x2)`。报告在后果②里承认了，但标题仍写"丢掉宿主的 Flags"，会误导优先级。
3. **A-5 两处措辞过强**：① `index` 在 `:1343` 已初始化为 0，"index 未被写入"应为"index 保持 0"；
   ② "`vkAcquireNextImageKHR` 用 `UINT64_MAX` 超时，窗口最小化时会直接阻塞在 acquire 上"——最小化时该
   调用返回 `OUT_OF_DATE`，不会阻塞。结论不受影响，证据句要改。
4. 行号普遍有 ±3 行漂移（`:345-355` 实为 `:345-348`、`:1358-1363` 实为 `:1359-1366`、`:37-41` 实为 `:37-42`），
   不影响定位，但说明引用是手写的、没有逐条回读。

---

### V5 只读性 ✅

- `git log -1` = `4dfab96 G-81: 补 B-R10-01 commit 号并登记 R10-02 审计任务`（审计未提交）
- `git status --porcelain` 只有 ` M Docs/Logs/TASK-R10-02.md` 一处，**无产品代码/测试改动**
- 审计自述"未编译探针"的理由（编译会改写 `Native/build/` 与受跟踪的 `zrender.dll`）与交接档案 §5 债务 #6
  一致，可信

---

### V6 覆盖面 ✅

9 条未覆盖区域如实列出且点名到文件/模块；明确写了"未启动 Unity、未跑 GPU 程序"、"需要真实设备才能确认
后果的结论只到'代码路径确定'"。没有假装全审。唯一可更严之处：**没说明"20 个 DXGI 导出的参数个数从未与
系统实现逐一核对"**——而这正是 A-1 的成因（审计是"读出来"的，不是"验出来"的）。

---

### 逐条复核表（复核 8 / 17）

| 条目 | 原级别 | 我的判定 | 关键依据 |
|---|---|---|---|
| A-1 | P0 | **成立，降 P1** | 4 参转发 7 参导出为真；但 Player 导入表无 `DXGID3D10*`（V2） |
| A-2 | P1 | **成立** | 槽 17=`GetSharedResourceAdapterLuid`，Composition=24（SDK 头两次核对） |
| A-3 | P1 | **成立，位置需修正** | desc 确被丢弃；位置应为 `fg_ffx.cpp:535-542`；Flags 论断需收窄 |
| A-4 | P1 | **成立** | 尺寸只写一次、capture 不重建、ResizeBuffers 只转发 |
| A-5 | P1 | **成立** | OUT_OF_DATE 不 signal 却登记 pending acquire |
| A-6 | P1 | **成立** | `vkQueueSubmit` 返回值丢弃 + `UINT64_MAX` 等 fence |
| A-8 | P1 | **成立** | TCS 无超时无 finally，`_pushingViews` 在 finally 才清 |
| A-10 | P1 | **成立，降 P2** | `Execute` 从不读 `splits`；无生产调用点 |
| A-7 / A-9 / B-1~B-7 | — | **未复核**（抽样已覆盖 8/17） | 不应默认可信 |

---

### 总结论：**部分可信**

- **缺陷存在性可信**：复核的 8 条没有一条误报，全部能在代码里落到具体行、逻辑链闭合。
- **可直接排修复任务的**：A-2、A-4、A-5、A-6、A-8（分级与定位都准）。
- **须先修正再排任务的**：A-1（降 P1）、A-10（降 P2）、A-3（改位置 + 收窄 Flags 论断）。
- **未复核的 9 条**（A-7/A-9/B-1~B-7）在排任务前应按同样方法过一遍——本次抽样已证明该审计的行号引用
  存在 ±3 行漂移与一处跨文件错位，剩余条目不宜直接照单执行。

### 建议修复优先级

1. **A-2**——改一个常量（17→24），并在 `PatchSlotTracked` 里加"槽内函数是否属于预期导出"的自检。
   成本最低，且当前状态是"装错了还打印成功"，最误导人。
2. **A-8**——`Fade` 的 Task 加超时/`CancellationToken`，`OnDisable` 里 flush 未完成的 TCS。玩家可见的 UI 软锁。
3. **A-4**——`ResizeBuffers` 成功后更新 `g_displayWidth/Height` 并失效 capture。真机拖窗口必现。
4. **A-1**——`Forward` 加 5–7 参重载（顺带修 `CreateLayeredDevice`）。5 行改动，清掉一个内存破坏面；
   虽降 P1，仍应进第一批，因为改起来比写任务单还快。
5. **A-5 / A-6**——`OUT_OF_DATE` 直接跳过 Submit/Present；`vkQueueSubmit` 返回值进错误计数。
6. **A-3**——desc 原样透传（只补 waitable 位）；动手前先确认真机上是否真有"后台缓冲作 SRV"的用例。
7. **A-10**——`Execute` 补 splits 下发，或把 `Execute` 收成 internal、只留 `ExecuteAliased`。
