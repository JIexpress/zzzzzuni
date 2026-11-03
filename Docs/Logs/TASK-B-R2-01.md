# TASK-B-R2-01 RHI 核心语义层（类型/句柄/资源状态模型，无后端实现）

状态: doing | 派发: 2026-10-23 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：在 `Native/zrhi/` 建立 Vulkan 语义的 RHI 核心抽象（纯接口与数据类型，**不含任何后端实现**，D3D12 后端是下一任务）：格式/用途/布局/访问/管线阶段枚举体系、泛代句柄表、资源屏障数据模型、设备与命令表接口。

**设计要点**：
1. `zrhi_types.h`（枚举+traits，语义对齐 Vulkan，命名 Zr 前缀）：
   - `ZrFormat`：至少 RGBA8Unorm/BGRA8Unorm/RGBA8sRGB/R8Unorm/R16Float/RG16Float/R11G11B10UFloat/RGBA16Float/R32Float/RGBA32Float/R32Uint/D32Float/D24UnormS8/D16Unorm + `ZrFormatTraits{bytesPerBlock, blockW, blockH, hasDepth, hasStencil, isColor}` traits 表覆盖**每个**枚举值（static_assert 或单测保证）
   - `ZrImageUsage` 位标志（TransferSrc/Dst, Sampled, Storage, ColorAttachment, DepthStencilAttachment）；`ZrBufferUsage`（TransferSrc/Dst, Uniform, Storage, Vertex, Index, Indirect）
   - `ZrImageLayout`：Undefined/General/ColorAttachmentOptimal/DepthStencilAttachmentOptimal/DepthStencilReadOnlyOptimal/ShaderReadOnlyOptimal/TransferSrcOptimal/TransferDstOptimal/Present
   - `ZrAccessBits` 与 `ZrPipelineStageBits`（对齐 VkAccessFlagBits/VkPipelineStageFlagBits 子集，Graphics/Compute/Transfer 三域）
   - `ZrPresentMode`(Fifo/Mailbox/Immediate)、`ZrSampleCount`(1/2/4/8)、`ZrQueueType`(Graphics/Compute/Transfer)
2. `zrhi_handles.h/.cpp`：强类型句柄 `ZrImage/ZrBuffer/ZrImageView/ZrSampler/ZrCommandList/ZrSwapchain/ZrPipelineState/ZrShader`（各为包装 uint32 的 struct，防误用）；`ZrHandleTable<T>` 泛代表（index:24 bit + generation:8 bit，Acquire/Release/TryGet，stale 句柄 TryGet 返回 false；generation 回绕保护）
3. `zrhi_barriers.h`：`ZrImageBarrier/ZrBufferBarrier` POD（prev/new layout、src/dst access、src/dst stage、整资源或子资源 range）+ 辅助函数 `ZrIsReadOnlyLayout/ZrAccessCompatible`（只读区间可共享屏障的判定基础，R3 屏障合并会用）
4. `zrhi_device.h`：抽象接口（纯虚，本任务无实现）：`IZrDevice`（Create*/Destroy*/GetQueue/WaitIdle）、`IZrCommandList`（Begin/End、ResourceBarrier(批量)、CopyBuffer、ClearColorImage、BeginRenderPass 风格的 BeginPass/EndPass 占位声明）。接口注释注明与 Vulkan 对应概念（不需要 VK 头文件依赖！语义映射写成注释）
5. doctest 单测（`tests/test_zrhi_handles.cpp`、`tests/test_zrhi_types.cpp`）：
   - 句柄表：分配/释放/复用槽位 generation+1、stale 句柄拒绝、满表扩容
   - traits：每个 ZrFormat 枚举值 traits 完整且数值合理（如 RGBA8Unorm=4 字节/块、D24UnormS8=4）
   - 枚举：usage 位可组合、layout 只读判定、access/stage 位互不重叠（bit mask 断言）
6. CMake 挂接新文件与测试；`/W4 /WX` 零警告

**约束**：只动 `Native/zrhi/`、`Native/tests/`、`Native/CMakeLists.txt`、本任务日志；不引 Vulkan SDK（本任务零 VK 依赖）；不 git commit；UTF-8 无 BOM/LF/#pragma once。

**交付物**：类型+句柄表+屏障模型+接口头+单测全绿+施工报告（含 Vulkan 概念映射表：每个 Zr 类型→对应 Vulkan 概念一行说明）。

## 施工报告（施工代理写）

### ① 摘要

在 `Native/zrhi/` 建立 Vulkan 语义的 RHI 核心抽象层（纯数据类型 + 纯虚接口，零后端实现、零 Vulkan 头依赖）：

- **zrhi_types.h**：`ZrFormat`（14 格式 + Count 哨兵）与 `ZrFormatTraits` 逐值全覆盖（static_assert 表长 + 单测独立期望表双重保证）；用途位标志 `ZrImageUsage`/`ZrBufferUsage`；`ZrImageLayout`（9 布局）；`ZrAccessBits`（15 位，**位值与 VkAccessFlagBits 逐位对齐**，跳过 Host 域，static_assert 锁定）与 `ZrPipelineStageBits`（11 位，位值对齐 VkPipelineStageFlagBits）及 Graphics/Compute/Transfer 三域分组常量；`ZrPresentMode`/`ZrSampleCount`（数值=VkSampleCountFlagBits 位值）/`ZrQueueType`/`ZrFilter`/`ZrSamplerAddressMode`。位标志统一 `enum class` + `ZR_DEFINE_BITWISE_ENUM` 生成 `| & ~ |= &= == !=`，跨体系混算编译期报错。
- **zrhi_handles.h/.cpp**：句柄位域 `index:24 | generation:8`；八类强类型句柄（CRTP 基座 `ZrHandle<Self>`，`static_assert(!is_convertible)` 证防误用）；泛代表 `ZrHandleTable<T>`（薄封装）→ `ZrHandleTableBase` 槽位引擎（.cpp pimpl 实现）：LIFO 空闲链复用、Release 代际 +1、stale 拒绝、代际 1..255（0 保留给零值句柄）、**255 代耗尽槽位永久退役不回绕**（回绕保护）、容量上限 0xFFFFFF 槽保证 `0xFFFFFFFF` 哨兵唯一。
- **zrhi_barriers.h**：`ZrImageBarrier`/`ZrBufferBarrier`/`ZrSubresourceRange` POD（static_assert POD）+ `ZrIsReadOnlyLayout`/`ZrIsReadOnlyAccess`/`ZrAccessCompatible`（读-读兼容、任一侧含写即冲突——R3 屏障合并判定基础）。
- **zrhi_device.h**：描述 POD（Buffer/Image/ImageView/Sampler/Shader/PipelineState/Swapchain/Pass）+ 纯虚 `IZrDevice`（Create×8 / Destroy×8 强类型重载 / GetCommandList / GetQueue / WaitIdle）、`IZrCommandList`（Begin/End/ResourceBarrier 批量/CopyBuffer/ClearColorImage/BeginPass/EndPass 占位）、`IZrQueue` 占位；全部方法注释标注 Vulkan 对应。
- 公共头 `zrhi/zrhi.h` 聚合四个内部头（内部头不出模块目录，符合 file-conventions §3）。

### ② 交付清单

| 文件 | 行数 | 内容 |
|---|---|---|
| `Native/zrhi/zrhi_types.h` | 311 | 新增：枚举体系 + traits 表 + 位标志基座 |
| `Native/zrhi/zrhi_handles.h` | 127 | 新增：强类型句柄 + 泛代表声明 |
| `Native/zrhi/zrhi_handles.cpp` | 121 | 新增：槽位引擎实现（LIFO/代际/退役） |
| `Native/zrhi/zrhi_barriers.h` | 94 | 新增：屏障 POD + 只读判定 |
| `Native/zrhi/zrhi_device.h` | 179 | 新增：Desc POD + 三个纯虚接口 |
| `Native/zrhi/zrhi.h` | 26 | 修改：聚合暴露语义层（保留 ZRHI_GetVersion） |
| `Native/tests/test_zrhi_types.cpp` | 272 | 新增：9 用例（traits 全枚举/位组合/重叠/只读/POD） |
| `Native/tests/test_zrhi_handles.cpp` | 233 | 新增：6 用例（打包/复用/扩容/回绕/拒绝/独立） |
| `Native/CMakeLists.txt` | 122 | 修改：zrhi 库挂 zrhi_handles.cpp；测试挂两文件 |

规范核查：全部 UTF-8 无 BOM、LF（od 实测 0 个 CR 字节）、文件尾单换行、无尾随空格、头文件 `#pragma once`；`#include` 扫描零 Vulkan/VK 头命中。

### ③ 自测结果

`powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1` **ALL GREEN**：

- configure（VS 17 2022 x64）✓ / build Release **/W4 /WX 零警告** ✓（/WX 下任何警告即失败）
- doctest：**31 用例全过 / 6473 断言全过 / 0 失败**（其中本任务新增 15 用例，约 6042 断言；沿用 16 用例不回归）
- 关键用例：traits 独立期望表逐值比对（14 格式全枚举）；分配/释放/复用 gen+1；stale/零值/哨兵/越界/代际超前全拒绝；1000 连续分配扩容且索引互异 + 释放复用零扩容；**255 代耗尽退役 + 伪造末代句柄拒绝**；access/stage 单位互不重叠（popcount 断言）；读-读兼容与 RAW/WAR/WAW 冲突判定。

### ④ Vulkan 概念映射表

| Zr 类型/枚举 | Vulkan 对应概念 |
|---|---|
| `ZrFormat` | `VkFormat`（14 值子集，逐值注释标注 VK_FORMAT_*） |
| `ZrFormatTraits` / `ZR_FORMAT_TRAITS` | 无直接 API（vkGetPhysicalDeviceFormatProperties 查询量的编译期子集：块尺寸/字节/深度模板） |
| `ZrImageUsage` | `VkImageUsageFlagBits`（组合即 `VkImageUsageFlags`） |
| `ZrBufferUsage` | `VkBufferUsageFlagBits`（组合即 `VkBufferUsageFlags`） |
| `ZrImageLayout` | `VkImageLayout`（9 值一一对应，Present=PRESENT_SRC_KHR） |
| `ZrAccessBits` | `VkAccessFlagBits` 子集（**位值逐位对齐**，跳过 HOST_READ/WRITE；组合即 `VkAccessFlags`） |
| `ZrPipelineStageBits` | `VkPipelineStageFlagBits` 子集（位值对齐；NONE/TOP..BOTTOM 共 11 位） |
| `ZR_ACCESS_READ/WRITE_MASK` | 无直接对应（写足迹划分，同步语义推理用） |
| `ZR_STAGE_GRAPHICS/COMPUTE/TRANSFER_DOMAIN` | `VK_PIPELINE_STAGE_ALL_GRAPHICS_BIT` 语义子集 / 队列域划分 |
| `ZrPresentMode` | `VkPresentModeKHR`（FIFO/MAILBOX/IMMEDIATE） |
| `ZrSampleCount` | `VkSampleCountFlagBits`（数值 1/2/4/8 即位值） |
| `ZrQueueType` | `VkQueueFamilyProperties` 队列族三域（queueFlags） |
| `ZrFilter` | `VkFilter`（NEAREST/LINEAR 子集） |
| `ZrSamplerAddressMode` | `VkSamplerAddressMode`（5 值子集） |
| `ZrImage`/`ZrBuffer`/`ZrImageView`/`ZrSampler` | `VkImage`/`VkBuffer`/`VkImageView`/`VkSampler`（指针句柄→index+gen 值句柄） |
| `ZrCommandList` | `VkCommandBuffer` |
| `ZrSwapchain` | `VkSwapchainKHR` |
| `ZrPipelineState` | `VkPipeline` + `VkPipelineLayout` |
| `ZrShader` | `VkShaderModule` |
| `ZrHandleTable<T>`/`ZrHandleTableBase` | 无直接对应（vkCreate*/vkDestroy* 生命周期管理的泛代表化 + 失效检测） |
| `ZrSubresourceRange` | `VkImageSubresourceRange`（省略 aspectMask，后端展开深度/模板） |
| `ZrImageBarrier` | `VkImageMemoryBarrier`（省略队列所有权字段，后续任务补） |
| `ZrBufferBarrier` | `VkBufferMemoryBarrier`（offset/size=VK_WHOLE_SIZE） |
| `ZrIsReadOnlyLayout`/`ZrAccessCompatible` | 无直接 API（Vulkan 布局同步语义的推理判定：只读布局/读-读无冒险，Diligent 状态衰减思路） |
| `ZrBufferDesc`/`ZrImageDesc`/`ZrImageViewDesc` | `VkBufferCreateInfo`/`VkImageCreateInfo`/`VkImageViewCreateInfo`（子集） |
| `ZrSamplerDesc`/`ZrShaderDesc` | `VkSamplerCreateInfo`/`VkShaderModuleCreateInfo`（pCode=SPIR-V、pName=entryPoint） |
| `ZrPipelineStateDesc` | `VkGraphicsPipelineCreateInfo` + `VkPipelineLayout`（占位子集） |
| `ZrSwapchainDesc` | `VkSwapchainCreateInfoKHR`（minImageCount/presentMode） |
| `ZrPassDesc` | `VkRenderingInfo`（动态渲染）/ `VkRenderPassBeginInfo`（占位形状） |
| `IZrDevice` | `VkDevice` + `vkCreate*`/`vkDestroy*` 家族 + `vkGetDeviceQueue` + `vkDeviceWaitIdle` |
| `IZrCommandList` | `VkCommandBuffer` 记录族：`vkBeginCommandBuffer`/`vkEndCommandBuffer`/`vkCmdPipelineBarrier`/`vkCmdCopyBuffer`/`vkCmdClearColorImage`/`vkCmdBeginRendering`/`vkCmdEndRendering` |
| `IZrQueue` | `VkQueue`（Submit 占位，后端任务补 `vkQueueSubmit`/`vkQueueWaitIdle`） |
| `ZR_REMAINING_MIP_LEVELS/ARRAY_LAYERS`、`ZR_WHOLE_SIZE` | `VK_REMAINING_MIP_LEVELS`/`VK_REMAINING_ARRAY_LAYERS`、`VK_WHOLE_SIZE` |

### ⑤ 风险

1. **接口占位面**：`BeginPass/EndPass`（ZrPassDesc）、`IZrQueue::Submit`、`ZrPipelineStateDesc` 为占位形状，后端/管线/RenderGraph 任务落地时签名可能微调（本任务已按简报声明为占位）。
2. **屏障简化**：`ZrImageBarrier` 省略队列所有权转移（queueFamilyOwnership）与 aspectMask 细分；`ZrAccessBits` 跳过 Host 域访问位——多队列与宿主回读场景需后续任务扩展字段。
3. **代际退役代价**：回绕保护以"槽位永久退役"实现（255 代/槽），极端高频重建场景会缓慢累积不可用槽；如成为瓶颈可改为稀疏代纪水位（风险已隔离在 Table 内部，接口不变）。
4. **layout 位值依赖**：`ZrImageLayout` 未采用 Vulkan 显式值（枚举序与 VkImageLayout 数值不同），后端桥接需查表而非强转——已在注释逐值标注对应关系。
5. **句柄表单线程**：无锁约定（同 zkernel）；多线程录制由上层按队列分表或后续加锁。

## 验收清单（主控写）

1. `Tools/build_native.ps1` 全绿（含新增单测）
2. 句柄表单测：泛代复用、stale 拒绝、扩容、generation 回绕保护——全过且用例真实断言（读测试源码核对）
3. `ZrFormat` 每个枚举值 traits 完整（单测遍历所有值断言非零/合理值）
4. 接口头文件审查：IZrDevice/IZrCommandList 方法齐全（简报第 4 条所列）且注释含 Vulkan 映射；零 Vulkan 头依赖（grep vkInclude/Vulkan/VKHeaders 无命中）
5. 屏障 POD 完整性：layout/access/stage/range 字段齐全；`ZrIsReadOnlyLayout` 行为正确（单测）
6. 文件规范抽查（BOM/LF/#pragma once）
7. 施工报告完整（含 Vulkan 概念映射表）

## 验收结论（验收代理写）

**总判定：PASS**（2026-10-23，验收全部 7 项独立自跑，未采信施工自测结论）

| # | 项目 | 判定 | 证据 |
|---|---|---|---|
| 1 | 构建全绿 | PASS | 本代理独立执行 `Tools/build_native.ps1` 退出码 0、输出 `ALL GREEN: configure + build + test succeeded`；configure（VS 17 2022 x64）→ Release 构建 → doctest 31 用例/6473 断言全过。开头 `cmake_probe_tmp Permission denied` 一行经查 `build_native.ps1:40-52` 为脚本自带的 cmake 写探针回退逻辑（PATH cmake 拒写→回退 VS BuildTools cmake 3.31.6），属预期非错误 |
| 2 | 句柄表单测 | PASS | 读 `Native/tests/test_zrhi_handles.cpp` 源码核对：泛代复用（断言 `d.Index()==b.Index()` 且 `d.Generation()==b.Generation()+1`、旧句柄仍拒、载荷更新）、stale 拒绝（含零值/哨兵/越界/代际超前/重复释放五类）、扩容（1000 连续分配索引互异 + 释放一半复用零扩容 `Capacity()==capacityBefore`）、回绕保护（消费 gen 2..255 后 `RetiredCount()==1`、历史句柄含伪造末代全拒、新槽从 gen 1 起）——均为真实 CHECK/REQUIRE 断言非空跑；过滤复跑 `*generation*` 3 用例 805 断言、`*stale*` 1 用例 24 断言、全量 31/31 全过 |
| 3 | ZrFormat traits | PASS | `zrhi_types.h`：14 个格式枚举值（RGBA8Unorm..D16Unorm）与 `ZR_FORMAT_TRAITS[]` 14 行逐值按序对应（行内注释同名校对），`static_assert` 锁表长==`ZrFormat::Count`；单测 `test_zrhi_types.cpp:37-80` 用 14 项独立期望表**全枚举遍历**断言 bytes/blockW/blockH/isColor/hasDepth/hasStencil（REQUIRE 期望表长==Count），非抽查；过滤复跑 1 用例 102 断言全过；逐值数值复核合理（RGBA8=4、R8=1、R11G11B10=4、RGBA16F=8、RGBA32F=16、D24UnormS8=4、D16=2） |
| 4 | 接口审查+零 VK 依赖 | PASS | `zrhi_device.h`：`IZrDevice` 含 Create×8/Destroy×8 强类型重载/`GetQueue`(→vkGetDeviceQueue)/`WaitIdle`(→vkDeviceWaitIdle)；`IZrCommandList` 含 Begin/End/ResourceBarrier(批量指针+计数)/CopyBuffer/ClearColorImage/BeginPass/EndPass——简报第 4 条全覆盖，每方法注释标注 vk* 对应；grep 整个 `Native/`（排除 build/）零 `#include` vulkan/VK 头命中，所有 Vk/VK 字样均在注释（即简报要求的语义映射注释），零 Vulkan 头依赖 |
| 5 | 屏障 POD+只读判定 | PASS | `zrhi_barriers.h`：`ZrImageBarrier` 含 prev/new layout、src/dst access、src/dst stage、`ZrSubresourceRange`（baseMip/levelCount/baseArrayLayer/layerCount）齐全，`ZrBufferBarrier` 含 offset/size(VK_WHOLE_SIZE)；static_assert POD + 单测字段/默认值断言（过滤复跑 18 断言过）；`ZrIsReadOnlyLayout` 单测覆盖全部 9 个布局分支（4 只读 true + 5 读写 false，9 断言与枚举值一一对应，复跑全过） |
| 6 | 文件规范抽查 | PASS | 抽查 5 文件（zrhi_types/zrhi_handles.h/zrhi_handles.cpp/zrhi_barriers/zrhi_device）+2 测试文件：字节级 od 实测 CR(0x0D)=0 个（纯 LF）、首 3 字节 `2f 2f 20`（无 BOM）、头文件均有 `#pragma once`、尾随空格 0 行、文件尾单 0x0A。（注：首轮 Git Bash `grep -c $'\r'` 因文本模式误报全行命中，已以 od 字节级复核推翻） |
| 7 | 施工报告+映射表 | PASS | 任务日志四节齐全（简报/施工报告①-⑤/验收清单/验收结论）；映射表 35 行存在，抽查 5 行对照 Vulkan 规范均正确：ZrAccessBits 位值对齐（TransferWrite=0x1000、MemoryRead=1<<15、MemoryWrite=1<<16，跳过 Host 0x2000/0x4000 与 VkAccessFlagBits 一致）；ZrPipelineStageBits（TopOfPipe=1..BottomOfPipe=0x400 与 VkPipelineStageFlagBits 逐位一致）；IZrCommandList→vkBegin/EndCommandBuffer/vkCmdPipelineBarrier/vkCmdCopyBuffer/vkCmdClearColorImage/vkCmdBeginRendering/vkCmdEndRendering 对应无误；ZrSubresourceRange→VkImageSubresourceRange（省略 aspectMask）表述准确；D24UnormS8=4 字节与 VK_FORMAT_D24_UNORM_S8_UINT 一致 |

**保留意见（不阻塞 PASS，供后续任务参考）**：
1. 施工报告"⑤ 风险"已如实披露的占位面（BeginPass/EndPass、IZrQueue::Submit、ZrPipelineStateDesc）与屏障省略字段（队列所有权/aspectMask/Host 访问位），后端任务落地时需补全。
2. `ZrImageLayout` 枚举序与 VkImageLayout 数值不同（报告风险 4 已声明），D3D12/Vulkan 后端桥接须查表。
3. traits 期望表单测以"期望表长==Count"保证覆盖，枚举增删值时若期望表同步错位仍会被计数捕获，机制健全。

