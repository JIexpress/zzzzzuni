# TASK-B-R9-09 —— Vulkan 交换链真重建（OUT_OF_DATE 恢复路径补全）

状态: doing | 派发: 2026-10-31 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

**前置**：R10-03 完成（帧闸/needsRecreate 粘性已就绪——本任务把"跳过"升级为"重建后恢复"）。

## 任务简报（主控写）

**目标**：R10-03 遗留补全——VK 后端 `vkCreateSwapchainKHR` 目前仅建链时调用一次，OUT_OF_DATE 后 needsRecreate 粘性导致帧被持续跳过（设计上安全但不恢复）。本任务实现**真重建路径**：窗口 resize 后交换链按新尺寸重建、渲染恢复、与 D3D12 行为对齐（D3D12 侧 HookResizeBuffers 已有状态机先例）。

**设计要点**：
1. **重建流程**（zrhi_vulkan_device.cpp，vkCreateSwapchainKHR:1290 处拆出 `RecreateSwapchain()`）：
   - 触发：帧闸 needsRecreate 粘性标记 → Begin/或 Present 前检到 → 重建
   - 旧资源排空与销毁：vkQueueWaitIdle → 销毁旧 swapchainImageViews（保留 imageviews 数组结构）→ vkDestroySwapchainKHR；**framebuffers/renderTarget 视图若引用旧交换链 image 需同步重建**（查现有深度/后台缓冲视图依赖，报告说明实际依赖面）
   - 重建：vkGetSwapchainImagesKHR 重新枚举→新 imageviews→尺寸更新→needsRecreate 清除→下一帧正常渲染
   - 语义与 B-R7-01 既有约定对齐（负视口/CCW 不变——尺寸变了视口跟随）
2. **验证**（可证伪，照 A-4 的"窗口变小"口径）：
   - selftest/探针：Vulkan 后端起窗→运行 N 帧→模拟 resize（**真机窗口 resize 事件**或 API 强制 oldSwapchain 重建路径）→重建后继续渲染且颜色校验仍过→`VULKAN RESIZE OK (old=WxH new=WxH serial=N→M)`
   - 单测：重建状态机纯逻辑（needsRecreate→重建→清除）mock 用例；重建失败（e.g. 零尺寸）→回粘性不挂死
3. D3D12 侧零改动；对照：`--backend both` 在 resize 后两侧均恢复
4. CMake 挂接；/W4 /WX 零警告

**约束**：只动 `Native/zrhi/vulkan/`、`Native/tests/`、`Native/CMakeLists.txt`、本任务日志；不 git commit；UTF-8 无 BOM/LF；VK 不泄漏。

**交付物**：重建路径+可证伪验证+单测+施工报告四节（含依赖面说明）。

## 施工报告（施工代理写）

### ① 修了什么（含依赖面说明）

R10-03 遗留补全：VK 后端 OUT_OF_DATE 后从"安全跳过（永不恢复）"升级为"真重建后恢复"。

**依赖面说明（逐项查代码取证，非推断）**：

| 依赖项 | 引用什么 | 重建时怎么处理 |
|---|---|---|
| RenderPass/Framebuffer | **不存在**——本基座用动态渲染（Vulkan 1.3 核心，`zrhi_vulkan_device.h` 头注既定契约），全仓零 `vkCreateFramebuffer`/`vkCreateRenderPass` | 无需处理（简报中"framebuffer 引用旧 image"一项实际不成立） |
| 后台缓冲视图（真依赖面） | 调用方经 `device->CreateImageView({.image=后台缓冲句柄, .viewType=Rtv})` 建的 VkImageView（验证程序按索引缓存 `rtvViews[index]`，见 `zrhi_d3d12_window.cpp` lights/draw/r5 各 VK 路径） | **设备侧统一销毁**：句柄表无遍历接口 → `CreateImageView` 对 `swapchainOwned` 图像的视图登记进 `m_swapchainViews`（`zrhi_vulkan_device.cpp:895`），重建时 `DestroySwapchainDependentViews()`（:1403）按"desc.image ∈ 该链后台缓冲"匹配销毁（走 `Destroy(ZrImageView)` 公共路径，含 bindless 槽回收）；调用方旧视图句柄 stale 化（TryGet 拒绝=安全失败），须按新句柄重建。`Destroy(ZrSwapchain)` 销毁路径同策收口（:1860，活视图挂死图像属 UB） |
| 深度图/DSV | 独立 `VkImage`（非 swapchainOwned） | 不受重建影响，零改动 |
| renderFinished 信号量 | 每后台缓冲一个，随链整组 | 销毁重建（数量可变）；`imageAvailable` 与链无关，保留复用 |
| 挂起 acquire/present 握手 | `m_pendingAcquire/m_pendingPresentSignal/m_pendingImageIndex` | 排空后防御性清零（:1447 一带） |
| 后台缓冲 ZrImage 句柄 | 句柄表条目（图像本体归链所有，无 vkDestroyImage） | 旧句柄 Release + 重枚举后新句柄 Acquire（:1519 一带） |

**改动清单**（git diff 实测：`+271/-22` device.cpp、`+42` device.h、`+285/0` 窗口程序、`+1` CMake、2 个新文件；D3D12 侧 `git diff -- Native/zrhi/d3d12 Native/zfgprobe` 为空，实证零改动）：

- 新增 `Native/zrhi/vulkan/zrhi_vulkan_recreate.h`（117 行）：纯逻辑（无 vulkan.h）——`ResolveRecreateExtent()`（currentExtent 权威 / 零尺寸拒绝 / 0xFFFFFFFF 回落 / min-max 钳制）+ `SwapchainResizeLedger`（from/to 尺寸 + serial + failedAttempts 台账），照 R10-03 A-4 `zfg_probe_resize.h` 先例（产品与单测共用一份状态机）。
- `zrhi_vulkan_device.cpp`：
  - `Recreate()`（:1436）——①能力查询+尺寸裁决（零尺寸=最小化 → 拒绝、粘性保留）→ ②`vkDeviceWaitIdle` 排空+挂起握手清零 → ③**先建新链**（oldSwapchain 传参；失败时旧链/旧视图全完好，粘性保留可重试）→ ④销毁依赖面视图+旧链 → ⑤旧句柄释放+重枚举+新图像/信号量 → ⑥台账推进+`OnSwapchainRecreated()` 清粘性。中途任何失败均返回 false 且状态可自愈（下帧入口以新链为 oldSwapchain 再走全流程）。
  - 触发点 `AcquireNextImage()`（:1619）——帧闸 `NeedsRecreate()` 粘性 → 帧入口先重建（简报"Begin 前检到"落点）；**每帧最多一次尝试，无自旋无阻塞**；失败日志限流（首次+每 300 次）。
  - `PickPresentMode()`（:86）从 CreateSwapchain 拆出（建链/重建共用，防两份裁决分叉）；CreateSwapchain 尾部登记 `m_desc`+台账（:1373）。
  - `RecreateSwapchain()`/`GetSwapchainResizeInfo()` 公开 API（:1380/:1389，后者供取证行取数）。
- `zrhi_vulkan_device.h`：Swapchain 增 `Recreate()`/`m_desc`/`m_ledger`；Device 增依赖面登记表 `m_swapchainViews` + 两个公开方法；"已知坑"注释更新为"已补全"。
- `Native/tests/zrhi_d3d12_window.cpp` `RunResizeModeVulkan()`（:11605，+285 行零删改）+ main 分发（:11912）：起窗 640x480 → 30 帧红色清屏（每 5 帧 readback）→ `SetWindowPos` **真改客户区 480x360**（真机 resize 事件，非 API 直调）→ OUT_OF_DATE/SUBOPTIMAL → 帧闸粘性 → Acquire 入口自动重建（产品路径，程序不干预）→ 重建后 readback 仍=红 → `VULKAN RESIZE OK (old=WxH new=WxH serial=N→M)`。判定项：recreate 观测+from/to 尺寸精确匹配+pre 6/6 post ≥2 全过+validationErrors=0+undeclaredUses=0。
- `Native/tests/test_zrhi_vulkan_recreate.cpp`（新，248 行，5 个 TEST_CASE，:64/:104/:141/:177/:219）+ CMakeLists:313 挂接。

### ② 自测证据（全部亲跑）

- **build_native.ps1 全绿**：`258 用例 / 717,626 断言，0 failed`（基线 253/711,498 → **+5 用例 +6,128 断言，只增不减**；其中 device_stats 用例的两条 barrier 截断 WARNING 为既有钉子行为，非本任务引入）。
- **resize 真机验证 4 连跑全过**（3 次稳定性 + 1 次收尾）：
  ```
  VULKAN RESIZE OK (old=640x480 new=480x360 serial=0→1)
  ```
  证据链：pre 6/6 → WM_SIZE → `swapchain SUBOPTIMAL — recreate flagged` → `swapchain RECREATED 640x480 -> 480x360 (serial=1) — rendering resumes` → `acquire recovered (needsRecreate=0)` → post 9/9 全红(255,0,0)；`skipped=0 failedAttempts=0 validationErrors=0 undeclared=0`，exit 0。
- **定向变异证伪（有牙实证）**：把 `AcquireNextImage` 的重建触发条件改为永假（`failedAttempts == 0xDEADBEEF`，/W4 安全形态）→ 重建 → 亲跑：`VULKAN RESIZE FAILED (recreate=0 color=1 ledger=1 recreateOk=0 skipped=0 errors=0)`、**exit=1**、present 持续 `result=1 (needsRecreate=1, recreateRequests=1)` 永不恢复、serial 恒 0——钉死"probe 判定吃的是重建路径本身，不是像素巧合"。随后从备份**字节级还原**（cmp 通过）、重建复绿。
- **--lights both 回归**：`D3D12 LIGHTS OK` + `VULKAN LIGHTS OK` + `D3D12/VULKAN LIGHTS PARITY OK (maxDelta=0 tolerance=16)`，exit 0（--lights --backend vulkan 单跑亦 OK）。
- **VK 不泄漏**：首版重建漏销旧链 renderFinished 信号量——vkDestroyDevice 泄漏报告（3 个 VkSemaphore）当场暴露，补销毁+清空（防回归注释已留）后泄漏报告消失，且上述全部回归无 leaked 对象。

### ③ 遗留与风险（如实）

1. **建链时 imageExtent 不做 currentExtent 钳制**为既有行为（B-R7-01 起），本任务只在重建路径按 currentExtent 权威裁决；若未来出现"建链尺寸≠窗口客户区"的调用方，首帧即 OUT_OF_DATE → 自动重建恢复（行为安全但值得知晓）。
2. 重建中途失败（信号量创建等）会留下"新链无缓冲"的中间态——粘性保留+下帧入口重试可自愈（实测未触发，纯逻辑推演+状态可重入设计），单测覆盖的是裁决/台账/粘性层，资源层中间态无注入式单测。
3. `SurfaceLost`/`DeviceLost` 也走重建入口：能力查询/建链会失败 → 粘性保留、帧持续跳过——与 R10-03 基线行为一致（不挂死、不恢复），属既有边界非本任务回归。
4. `--resize` 模式只覆盖"窗口变小"主路径（A-4 口径）；变大/最小化恢复路径由单测的裁决矩阵与状态机覆盖，未做真机专跑。
5. `UnityProject/Assets/Plugins/ZRender/zrender.dll` 的 M 为构建产物噪声（HANDOFF 既定：主控提交前 `git checkout --`）；工作区内另有若干 UnityProject C# 文件的 M 系本任务开始前已存在（A-R9-07 相关），未触碰。

### ④ 交付物核对（对照简报）

- [x] 重建流程：WaitIdle→销毁旧视图/链→重枚举→新视图→尺寸更新→清 needsRecreate（`Recreate()` :1436）
- [x] 依赖面说明（①节表格：无 framebuffer；真依赖面=调用方后台缓冲视图，设备侧收口）
- [x] resize 可证伪验证：`VULKAN RESIZE OK (old=640x480 new=480x360 serial=0→1)` + 变异即红 exit 1
- [x] 重建状态机单测：5 用例（裁决矩阵/台账/主路径/失败回粘性 1000 帧不挂死/Present 侧对称）
- [x] D3D12 零改动（git diff 实证）；`--lights both` 回归亲跑 PASS
- [x] /W4 /WX 零警告（含变异过程中 C4127 拦截的实证）；UTF-8 无 BOM/LF（od 实测）；不 commit


## 验收清单（主控写）

- [ ] V1 build 全绿（用例数写入结论，基线 253/711,498 只增不减）
- [ ] V2 亲跑 resize 验证：重建后渲染恢复+颜色校验过+`VULKAN RESIZE OK` 数字行
- [ ] V3 状态机单测（重建清除/失败回粘性）真实断言行号
- [ ] V4 D3D12 零改动（git diff 核对）；`--lights both` 回归亲跑
- [ ] V5 VK 泄漏 grep+文件规范+施工报告四节完整

## 验收结论（验收代理写）

**PASS**（2026-10-31，验收代理独立亲跑，与施工不同代理；未改任何代码，仅写本节与头部"验收:"）

- **V1 build 全绿：PASS** —— 验收代理亲跑 `Tools/build_native.ps1`：`258 用例 | 258 passed | 0 failed`、`assertions: 717626 | 717626 passed`、`ALL GREEN`；基线 253/711,498 → +5 用例 +6,128 断言，只增不减，与施工报告数字一致。
- **V2 resize 亲证：PASS** —— 4 连跑 `zrhi_d3d12_window.exe --resize` 全部 `VULKAN RESIZE OK (old=640x480 new=480x360 serial=0→1)`、exit=0、`validationErrors=0 undeclared=0 skipped=0 failedAttempts=0`；取色行实证：pre 6/6 全 (255,0,0)、post-recreate 9/9 全 (255,0,0)（`swapchain RECREATED ... rendering resumes` + `acquire recovered (needsRecreate=0)` 均观测到）。
- **V3 状态机单测：PASS** —— `test_zrhi_vulkan_recreate.cpp` 5 个 TEST_CASE 行号 :64/:104/:141/:177/:219 与报告一致；零尺寸拒绝真实断言 :74-82/:130（`CHECK_FALSE(minimized.accepted)`/`CHECK_FALSE(ledger.RecordSuccess(0u,360u))`）；1000 帧失败重试不挂死 :189-198 逐帧真实断言（每帧恰 +1 failedAttempts、粘性保留、serial 恒 0、尺寸不被假更新），:202-216 恢复出口。回粘语义审查：`Recreate()`（device.cpp :1436）零尺寸/建链失败路径均 `RecordFailure()` 返回 false 且旧链完好，粘性由 frame_gate（:134 清除 / :136 读取）保持；`AcquireNextImage()`（:1617，触发点 :1623）每帧入口至多一次尝试，无自旋无阻塞——与报告声称一致。
- **V4 D3D12 零改动+回归：PASS** —— `git diff --stat -- Native/zrhi/d3d12 Native/zfgprobe` 输出为空（实证零改动）；亲跑 `--lights --backend both`（仓库根目录）：`D3D12 LIGHTS OK` + `VULKAN LIGHTS OK` + `D3D12/VULKAN LIGHTS PARITY OK (maxDelta=0 tolerance=16)`（6 组中心像素两侧逐位相同），exit=0。
- **V5 泄漏/规范/报告：PASS** —— 泄漏 grep：信号量 create:1349/:1364 ↔ 重建销毁:1548/重建:1559/:1573/析构:1598/:1602，交换链 create:1334/:1497 ↔ destroy:1517/:1605，配对完整；validation 开启的 resize/lights 实跑日志 0 条 leaked 报告。文件规范：新文件与改动文件字节级核验 0 个 CR、无 BOM（2f 2f 20 / 23 20 54 开头）。施工报告四节（①②③④）齐全；依赖面表逐项与代码核对一致：全仓 `vkCreateFramebuffer|vkCreateRenderPass` 0 命中（"无 framebuffer 依赖"成立）、`m_swapchainViews` 登记 :895、`DestroySwapchainDependentViews` :1403 且重建+`Destroy(ZrSwapchain)` 双路收口、挂起握手清零、后台缓冲句柄 Release/重 Acquire、`imageAvailable` 保留复用——全部属实。

**备注（不影响 PASS）**：① 施工报告"触发点 AcquireNextImage()（:1619）"实际函数行 :1617（触发 if 在 :1623），±2 行笔误；② 变异证伪（报告②节）系施工代理自证，验收代理依"不改代码"纪律未复演——但 probe 的 `recreateOk` 判定吃设备侧台账（仅产品 `Recreate()` 内 `RecordSuccess` 可推进 serial 且打印 `RECREATED` 行），像素巧合无法伪造该证据链，可证伪性成立；③ 验收首轮 `--lights both` 曾报 PARITY FAILED，系验收代理从 `bin/Release` 目录运行致相对路径 zmesh 打不开，从仓库根目录重跑即 PASS（已复核，非代码问题）。
