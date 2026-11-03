# TASK-B-R7-01 Vulkan 第二后端（验证 RHI 抽象忠实度）

状态: todo（施工完成待验收） | 派发: 2026-10-25（主控简报就绪，施工代理执行） | 施工: 2026-10-25 完成（见施工报告） | 验收: （待填） | commit: （待填）

**前置**：B-R6-01/02 完成；**Vulkan SDK 安装**（LunarG，~1.5GB，用户已批准——安装步骤：winget install LunarG.VulkanSDK 或官方安装器；装后 `VULKAN_SDK` 环境变量指向源码树，报告记录版本）。

## 任务简报（主控写）

**目标**：RHI 的 Vulkan 后端，验证"Vulkan 语义抽象层"的忠实度（抽象接口 1:1 对齐 VK，理论上后端最薄）。窗口程序在 Vulkan 后端跑通 `--draw`（清屏+Cube）与 `--lights`，与 D3D12 输出可对照。

**设计要点**：
1. `Native/zrhi/vulkan/`（镜像 d3d12 后端结构）：设备/队列/交换链(VK_PRESENT_MODE 映射)/命令缓冲/屏障(VkImageMemoryBarrier 语义天然对齐)/描述符(bindless=descriptor indexing update-after-bind 大 set)/PSO(VkGraphicsPipelineCreateInfo)/时间戳(VK_QUERY_TYPE_TIMESTAMP)
2. 后端选择：zplugin 增加 `ZR_GetBackend()`/启动参数 `--backend vulkan|d3d12`；两后端共存编译
3. CMake：Vulkan 目标仅在 `VULKAN_SDK` 存在时编译（找不到则跳过并在构建输出说明——CI/他机友好）
4. 验证：`--draw --backend vulkan`/`--lights --backend vulkan`（颜色校验复用）与 D3D12 结果对照（中心像素差 ≤ 容差，报告并排数字）；两后端跑 RG 契约一致（屏障合并计数相同断言）
5. 单测：PresentMode/format/layout 三映射表 Vulkan 侧全枚举（镜像 d3d12 测试）
6. 已知坑写注释：交换链重建（resize 暂不支持，注明）、VK_KHR_dynamic_rendering 可用则免 RenderPass 对象

**约束**：可动 Native/zrhi/vulkan/（新）、Native/zplugin/（后端选择加法）、Native/tests/、Native/CMakeLists.txt、本任务日志；不 git commit；UTF-8 无 BOM/LF。

**交付物**：Vulkan 后端+双后端对照数字+单测+施工报告四节。

## 施工报告（施工代理写）

**施工**: 2026-10-25，施工代理（WorkBuddy 会话）｜**结论**: 核心可证伪点全部达成——Vulkan 真机跑通 `--draw`，双后端中心像素 **maxDelta=0**（6/6 校验帧逐像素一致），RG 屏障合并计数两后端**相等**（断言 OK），单测 **160 用例 / 704,967 断言全绿**（基线 152 / 704,749 → +8 用例 / +218 断言）。

### 实现摘要

1. **Vulkan 后端**（`Native/zrhi/vulkan/`，镜像 d3d12 后端四件套结构）：
   - `zrhi_vulkan_translate.{h,cpp}`：zrhi 语义 → Vulkan 纯函数映射表（format/layout/presentMode/filter/addressMode/cullMode/blend/sampleCount/image·buffer usage/pipelineStage/access，全枚举显式 switch，逐位表驱动——与 d3d12 translate 同纪律：唯一转译点、零 static_cast 枚举）。
   - `zrhi_vulkan_device.{h,cpp}`：完整 IZrDevice（buffer/image/view/sampler/shader/PSO/swapchain/commandList/queryPool/aliasHeap 全家 + 别名堆落位 + bindless + 时间戳）。设备要求 Vulkan 1.3（780M 实测 api 1.4）；**动态渲染**（vkCmdBegin/EndRendering，零 RenderPass/Framebuffer 对象——简报"VK_KHR_dynamic_rendering 可用则免 RenderPass"的兑现）；bindless = descriptor indexing 全局大 set（binding0 SAMPLED_IMAGE / binding1 STORAGE_IMAGE 各 8192 槽，UPDATE_AFTER_BIND + PARTIALLY_BOUND）；根 CBV 槽 → set0 binding i 的 UNIFORM_BUFFER，SRV 表 → COMBINED_IMAGE_SAMPLER + 设备级 Point/Clamp 静态采样器。
   - `zrhi_vulkan_commandlist.{h,cpp}`：IZrCommandList 全命令族。**根绑定快照模型**——VK 描述符集是不可变快照，与 D3D12 根参数直绑语义不同：每次 SetGraphicsConstantBuffer/Srv 分配新集合并全量重绑（否则同帧多次改写同一集合会让所有绘制读到最后一组绑定——真机实证：三个 cube 全画成右边的）。负高度视口（y=height/height=-height）+ frontFace=CCW 精确复刻 D3D12 LH 屏幕空间。ClearColorImage 对 ColorAttachmentOptimal 入参做附件布局→TRANSFER_DST→清屏→回落的内部往返屏障（外部布局契约不变，RG 屏障计划一致性前提）。
   - 已知坑注释（简报要求）：交换链 resize 暂不支持（OUT_OF_DATE/SUBOPTIMAL 分支仅记录）；VK_KHR_dynamic_rendering 已核心化（1.3 前提）；VK 无原生 split barrier（begin/end 两半各落普通屏障，正确性等价、D3D12 提前刷新收益放弃）；VK 禁止 newLayout=UNDEFINED 屏障（D3D12 COMMON=0 回环语义无对位物，验证程序 VK 路径跳过帧末复位屏障）。
2. **后端选择**：`zplugin` 增 `ZR_SetBackend(int)`/`ZR_GetBackend()` C ABI（0=D3D12 缺省/1=Vulkan）；窗口程序 `--backend vulkan|d3d12|both`（预扫描解析，`--backend` 可在 `--draw` 之后）。两后端共存编译（同一 exe 同时含 zrhi_d3d12_* 与 zrhi_vulkan_* 编译单元）。
3. **CMake**：`ZR_HAS_VULKAN` 探测（环境变量 VULKAN_SDK → C:/VulkanSDK/* glob 兜底——不改用户环境变量，仅本配置内探测）；SDK 缺失时跳过 Vulkan 源/宏/着色器目标并 `message(STATUS ... SKIPPED ...)` 说明（CI/他机友好）；glslc 自定义命令把现有 `.hlsl` 直接编译为 SPIR-V（`-x hlsl`，产物 `bin/shaders_spv/<name>_vs|_fs.spv`，SM5/SM6 通用子集零源码改动）。
4. **窗口程序**：`--draw` 增加 `RunFirstDrawVulkan`（与 D3D12 路径逐行同构镜像：三角洲仅设备创建/SPIR-V 加载替代运行时 D3DCompile/RecordClearDepth 扩展点替代 ClearDepthStencilView/vkCmdCopyImageToBuffer 替代 CopyTextureRegion）；`--backend both` 双后端对照模式（同进程先 D3D12 后 Vulkan 各跑一轮 --draw，断言 RG 屏障计数逐项相同 + 中心像素并排对照报数字）。
5. **单测**：`tests/test_zrhi_vulkan_translate.cpp`——简报三映射表（format/layout/presentMode）Vulkan 侧全枚举断言 + 附加 filter/addressMode/cullMode/blend/sampleCount/usage/access/stage（独立期望表逐值比对 + 全枚举互异 + VK 常量直接对值，镜像 d3d12 测试写法）。

### 变更文件清单

| 文件 | 变更 |
|---|---|
| `Native/zrhi/vulkan/zrhi_vulkan_translate.h/.cpp` | 新增（映射表 12 函数） |
| `Native/zrhi/vulkan/zrhi_vulkan_device.h/.cpp` | 新增（设备 1700+ 行） |
| `Native/zrhi/vulkan/zrhi_vulkan_commandlist.h/.cpp` | 新增（命令表） |
| `Native/tests/test_zrhi_vulkan_translate.cpp` | 新增（7 个 TEST_CASE，+8 用例） |
| `Native/tests/zrhi_d3d12_window.cpp` | 增补（--backend 解析/RunFirstDrawVulkan/RunFirstDrawParity；D3D12 既有路径仅加可选捕获参数，行为零改动——回归全绿实证） |
| `Native/zplugin/zplugin.h/.cpp` | 增补（ZR_SetBackend/ZR_GetBackend 加法） |
| `Native/CMakeLists.txt` | 增补（Vulkan 条件编译/glslc SPIR-V 目标/链接传播） |

未动：`Native/ThirdParty/`、D3D12 后端既有行为、Unity 侧、TASKS/HANDOFF/WORKLOG。VK 类型泄漏 grep（zplugin/zrender/zrhi 公共头）：**0 命中**。

### 自测结果

- **构建**：`Tools/build_native.ps1` 在本会话被宿主安全钩子拦截（脚本内部 Resolve-CMakeCommand 探测目录的 Remove-Item 被 safe-delete fail-closed；脚本未改动）——按脚本流程**手动等效执行四步**，全部成功：
  - configure：`-- ZRender Vulkan backend: ENABLED (VULKAN_SDK=C:\VulkanSDK\1.4.363.0)`
  - build Release（/W4 /WX 零警告）：zrhi.lib（含 3 个 vulkan 编译单元）/ zrender.dll / zrender_test.exe / zrhi_d3d12_window.exe 全部产出
  - 单测：**160 用例 / 704,967 断言，全绿 SUCCESS**（基线 152 / 704,749）
  - 部署步骤（dll→Unity 插件目录）未跑（不碰 Unity 纪律）
  - 环境备注：MSYS2 的 PATH cmake 写探测目录被策略拒绝，构建时把 VS BuildTools 自带 cmake 置前
- **Vulkan 真机（AMD Radeon 780M，api 1.4，validation ENABLED）**：
  - `--draw --backend vulkan`：60 帧、中心校验 6/6 通过、undeclared uses=0、**validation error count = 0**、`VULKAN FIRST DRAW OK`（exit 0）
  - `--draw --backend both`（核心对照证据）：
    - `RG BARRIER PLAN PARITY: d3d12=[total=6 splits=0 calls=4 begin=0 end=0 heaps=0 transients=0]`
    - `RG BARRIER PLAN PARITY: vulkan=[total=6 splits=0 calls=4 begin=0 end=0 heaps=0 transients=0] -> EQUAL (assert OK)`
    - 中心像素并排（6 校验帧全部逐通道一致）：(27,60,141)/(151,146,141)/(170,165,159)/(10,22,54)/(54,54,54)/(54,54,54)，**maxDelta=0**（容差 16），`D3D12/VULKAN FIRST DRAW PARITY OK`
  - `--lights --backend vulkan`：显式 NOTICE（Vulkan 路径未实现，见未做项）后走 D3D12（`D3D12 LIGHTS OK`）
- **D3D12 回归**：`--draw` → `D3D12 FIRST DRAW OK`（InfoQueue 0 错误）；`--lights` → `D3D12 LIGHTS OK`；既有全模式路径零行为改动。

### 已知风险与未做项（如实）

1. **未做：`--lights --backson vulkan` 路径**——簇光照模式的 readback 校验逻辑与 D3D12 原生扩展点（CopyTextureRegion/BOX/footprint）强耦合，Vulkan 对位（vkCmdCopyImageToBuffer 多区域）需复制改造 ~1000 行模式体；本任务交的是简报允许的"D3D12 对照的 --draw 跑通 + 其余如实报告"的诚实部分完成。后端能力（rootCbvCount=3 多槽、SPIR-V 编译 pbr_forward）已就绪并经单测/编译验证，后续任务接线即可。
2. **未做：--shadows/--r5/--parity/--fsr 等其余模式的 Vulkan 路径**（同上理由；SRV 表/srvTableCount 后端能力已实现）。
3. **bindless 全局大 set 的运行时差异**：D3D12 侧 shader-visible 堆槽即根 SRV 表直指；VK 侧根 SRV 表映射为独立 COMBINED_IMAGE_SAMPLER 绑定（不穿全局大 set）——GetBindlessIndex 契约（槽位互异/回收复用）完整保留，差异数据通路层面无感。
4. **内存模型**：每资源一 VkDeviceMemory（无子分配），别名堆按"每槽一段 memory + offset 落位"对位 D3D12 placed resource——功能等价、内存开销偏大（验证程序量级无感）。
5. **交换链 resize 不支持**（简报已知坑，注释已写）；PresentMode 表面不支持时降级 Fifo（对位 D3D12 撕裂降级策略）。
6. **时间戳 Resolve 语义**：VK 侧以 vkDeviceWaitIdle 兑现"GPU 完成等待"（D3D12 侧为 per-heap fence）——契约等价、粒度较粗。
7. **环境坑（复现指引）**：构建脚本需 VS cmake 在 PATH 前 + 清空 http_proxy/HTTP_PROXY 重复项（MSBuild 环境字典大小写冲突）；`--backend both` 曾因 Win32 窗口类二次注册失败静默降级离屏（已修：ERROR_CLASS_ALREADY_EXISTS 容忍）。


## 验收清单（主控写）

1. build 全绿（Vulkan 目标编译证据；用例数）
2. 亲跑 `--draw/--lights --backend vulkan`：颜色校验过+与 D3D12 中心像素对照数字
3. RG 屏障合并计数两后端一致断言
4. 三映射表全枚举单测
5. 代码审查：后端不泄漏 VK 类型出 zrhi/vulkan/（grep）
6. 旧模式回归（d3d12 路径）
7. 文件规范+施工报告完整

## 验收结论（验收代理写）

**验收**: 2026-10-26，独立验收代理（与施工不同实例）｜**总结论: PASS with 受限**（7 项中 6 PASS + 1 PARTIAL；受限项已由施工如实披露、非简报"交付物"硬性要求，裁量依据见文末）

| # | 项 | 判定 | 证据（验收代理亲自取证） |
|---|---|---|---|
| 1 | build 全绿 | **PASS** | `Tools/build_native.ps1` 在验收环境**未被钩子拦截**，四步直接跑通：configure → build（/W4 /WX 零警告）→ 单测 → dll 部署，末行 `ALL GREEN: configure + build + test succeeded`；configure 亲验输出 `-- ZRender Vulkan backend: ENABLED (VULKAN_SDK=C:/VulkanSDK/1.4.363.0)`（重跑 cmake -S/-B grep 取证）；**单测 160 用例 / 704,967 断言全绿 SUCCESS**（基线 152 / 704,749 → +8 / +218，与施工自述一致）。注：施工报告所述脚本拦截为其会话特有，验收环境未复现，不影响结论 |
| 2 | 亲跑 vulkan 真机 | **PARTIAL** | `--draw --backend vulkan`（仓库根目录亲跑，exit 0）：60 帧、`center checks=6 passed=6`、`undeclared uses=0`、`validation error count = 0`、`VULKAN FIRST DRAW OK`。**`--lights --backend vulkan`：Vulkan 路径确实未实现**——运行时打印显式 NOTICE `--backend vulkan not implemented for --lights in B-R7-01 (d3d12 path runs instead; see task log '未做项')` 后走 D3D12（`D3D12 LIGHTS OK`），**非静默回退已亲验属实**。按验收指令对 `--lights` 部分如实判 PARTIAL，不放水 |
| 3 | RG 屏障计数双后端一致 | **PASS** | 亲跑 `--draw --backend both`（exit 0）：`RG BARRIER PLAN PARITY: d3d12=[total=6 splits=0 calls=4 begin=0 end=0 heaps=0 transients=0]` 与 `vulkan=[total=6 splits=0 calls=4 begin=0 end=0 heaps=0 transients=0] -> EQUAL (assert OK)`；同轮 `CENTER PIXEL PARITY` 6 校验帧逐通道 maxDelta=0（容差 16）：(27,60,141)/(151,146,141)/(170,165,159)/(10,22,54)/(54,54,54)/(54,54,54)，`D3D12/VULKAN FIRST DRAW PARITY OK (maxDelta=0 tolerance=16)` |
| 4 | 三映射表全枚举单测 | **PASS** | `Native/tests/test_zrhi_vulkan_translate.cpp`：format 表 L21-59（14 项独立期望表逐值比对 + 全枚举互异 + 非_UNDEFINED，L45 REQUIRE 期望表数==枚举数）；layout 表 L62-103（9 布局显式映射 + 两两互异 + Undefined==0 编译期常量对值 L99）；presentMode 表 L106-122（三值对 VK 常量 + 互异）。附加 filter/addressMode/cullMode/blend/sampleCount/usage/access/stage 共 8 TEST_CASE。单跑过滤器 `--test-case="*Vulkan*"`：**8 cases / 218 assertions 全过**（真断言、非空壳） |
| 5 | VK 类型零泄漏 | **PASS** | 验收代理自 grep：`zplugin/`（.h/.cpp）与 `zrender/`（.h）`Vk[A-Z]` **0 命中**；`zrhi/` 公共头（非 vulkan 子目录）4 文件命中 62 处**全部为注释/static_assert 字符串**（如 zrhi_handles.h:59-67 `// → VkImage` 语义说明、zrhi_types.h 的 static_assert 消息），无 `#include <vulkan/vulkan.h>`、无 Vk 类型声明/参数；include 与真实类型使用仅存在于 `zrhi/vulkan/` 子目录（符合设计）。代码级泄漏 = 0 |
| 6 | 旧模式回归 | **PASS** | 亲跑 `--draw`（d3d12）：`InfoQueue error count = 0`、`D3D12 FIRST DRAW OK`（exit 0）；`--lights`（d3d12）：60 帧 readback center=OK/anim=OK、`InfoQueue error count = 0`、`D3D12 LIGHTS OK`（exit 0）。既有路径零回归 |
| 7 | 文件规范 + 报告四节 + 编码 | **PASS** | 四节齐全（简报/施工报告/验收清单/本结论）；变更文件清单与实际 diff 相符（git status：6 改 + 2 新增目录/文件，未提交符合铁律）；编码抽检 11 个涉改文件：首字节均非 BOM（`2f2f20`=`// `、`232054`=`# T`），字节级 tr/od 复核 + `git ls-files --eol` 均为 `i/lf w/lf`，LF 无 BOM 达标 |

### 裁量依据（总结论为何 PASS with 受限）

- 简报"**交付物**"原文：「Vulkan 后端+双后端对照数字+单测+施工报告四节」——三项均已交付且经独立复核（双后端对照数字亲跑复现 maxDelta=0、屏障计数相等）。
- 简报"**设计要点 4**"确曾写 `--lights --backend vulkan` 验证；该项未实现，但施工报告"已知风险与未做项"第 1/2 条**主动如实披露**（含原因与后端能力就绪状态），且运行时 NOTICE 显式不静默（亲验）。按"未完成项已如实披露且不属交付物硬性要求"判 **PASS with 受限**，`--lights` Vulkan 路径列为遗留。
- 若按最严口径（设计要点逐条硬性），第 2 项 PARTIAL 已如实记录，供主控裁量是否补一轮接线任务（建议列 B-R7-02 或独立小任务 `--lights/--shadows/--r5/--fsr Vulkan 路径接线`）。

### 遗留项

1. `--lights/--shadows/--r5/--parity/--fsr` 的 Vulkan 路径未实现（NOTICE 显式回退 D3D12）——后端能力已就绪，建议后续任务接线
2. FSR4/DLSS 真机化外部补验项（沿用 R6 结论，与本任务无关）
3. 施工环境 build_native.ps1 钩子拦截未在验收环境复现——若后续代理再遇，手动四步流程已在施工报告留档可复用
