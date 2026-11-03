# TASK-B-R7-02 Vulkan 路径接线收尾（--lights/--shadows/--r5/--fsr）

状态: doing（施工完成待验收） | 派发: 2026-10-26 | 施工: general-purpose 施工子代理(接手续建) | 验收: general-purpose 验收子代理 | commit: （待填）

**前置**：B-R7-01 完成（Vulkan 后端真机跑通 `--draw`，双后端逐像素一致；多根 CBV/SRV 表能力已备好）。

## 任务简报（主控写）

**背景**：B-R7-01 验收（PASS with 受限）披露：窗口程序的 `--lights/--shadows/--r5/--fsr` 四个模式只有 D3D12 路径，Vulkan 侧运行时显式 NOTICE 不静默回退。R7 里程碑口径是"同场景 D3D12/Vulkan 双后端一致"，`--draw` 已证，本任务补完其余模式。

**目标**：四个模式在 Vulkan 后端跑通并与 D3D12 可对照：`--lights`（簇光源多 cube）、`--shadows`（CSM）、`--r5`（SSR/GI/NPR 特性）、`--fsr`（超分 on/off）。

**设计要点**：
1. **优先级**：`--lights`（R5 里程碑的代表场景）→ `--fsr`（R6 交付）→ `--shadows` → `--r5`；**做到哪一个算哪一个，但每个都要可证伪对照，不许只接一半还不报告**
2. **复用 B-R7-01 的既成事实**：根绑定快照模型、`pWaitDstStageMask` 修复、负视口+CCW 屏幕空间复刻、动态渲染（免 RenderPass）、glslc `-x hlsl` 出 SPIR-V——先读 `Native/zrhi/vulkan/` 现有实现与 `zrhi_d3d12_window.cpp` 里 D3D12 侧各模式的 readback/断言结构
3. **对照口径与 B-R7-01 相同**：同模式两后端各跑 30/60 帧，中心/采样像素并排对照给数字（容差沿用既有断言）；超分模式两后端的 FFX 路径本就同源（`amd_fidelityfx_upscaler_dx12.dll` 是 D3D12 后端组件——**若 FFX 无 Vulkan 组件可用，如实报告并改为 Vulkan 侧 Bilinear-only 对照或列受限，不许装作通过**）
4. 单测：新接线涉及的结构/常量两侧镜像（static_assert 或等值断言）
5. 自测：build 全绿；四模式（或如实报告完成数）亲跑 + d3d12 回归；用例数只增不减（基线 **160 用例 / 704,967 断言**）

**约束**：可动 `Native/zrhi/vulkan/`、`Native/tests/`、`Native/CMakeLists.txt`、本任务日志；不碰 Unity；不 git commit；UTF-8 无 BOM/LF；VK 类型不泄漏出 vulkan/ 子目录。

**交付物**：N 个模式双后端对照数字（N=4 或如实更少）+ 单测 + 施工报告四节（含"哪些没做、为什么"）。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理(接手续建)。前代理（workbuddy 平台）施工中途被限额中断，盘上半成品未提交；本代理审计后续建收尾。

### ① 摘要（接手 workbuddy 半成品 + 审计结论）

**审计结论**：盘上半成品（6 文件 / +2073 行）完成度远高于"中断点"预期——`--lights`/`--shadows` 两条 Vulkan 路径（含双后端对照函数与 main() 分发）**已写完**，`--r5`/`--fsr`/`--fsr4` 为显式 NOTICE（不静默回退），VK 后端修复（描述符布局改 SAMPLED_IMAGE+SAMPLER 分离、深度图视图 aspect/格式归源、级联层清屏窗口、split 屏障 end 半边 no-op）齐备，CMake 已加 pbr_shadow（绑定基偏移）/shadow_depth（vs-only）SPIR-V 规则，单测 +1 用例。**首跑 build_native.ps1 即 ALL GREEN（161 用例/704,980 断言）**——半成品不是"编译不过的断点"，而是"未自测未写报告的完成态"。本代理工作 = 审计 + 逐模式亲跑取证 + 修复缺陷（仅 1 处：`--r5` NOTICE 注释"6 着色器"实为 8 个着色器文件/16 入口点，已订正）+ 补齐证据与本报告。

**交付**：四模式中 **--lights、--shadows 两模式 Vulkan 接线完成且双后端对照精确一致（maxDelta=0 / 探针 delta=0）**；--fsr/--fsr4 因 FFX SDK 无 Vulkan 组件如实列受限（证据见④）；--r5 未接线（NOTICE 显式，理由见④）。

### ② 变更清单（相对 600604e 的工作区改动，未 commit）

| 文件 | 内容 |
|---|---|
| `Native/CMakeLists.txt` | +`ZR_COMPILE_HLSL_SPV_VS_ONLY` 宏（depth-only 着色器）；pbr_shadow.hlsl 显式绑定基编译（`-fubo-binding-base 0 -ftexture-binding-base 4 -fsampler-binding-base 5`——glslc HLSL 的 t/s 独立描述符须显式对位 set0 布局）；shadow_depth.hlsl vs-only 规则 |
| `Native/zrhi/vulkan/zrhi_vulkan_device.h` | +`ViewIsDepthSampling()`（SRV 描述符布局选择依据：深度图采样走 DEPTH_STENCIL_READ_ONLY） |
| `Native/zrhi/vulkan/zrhi_vulkan_device.cpp` | 深度图 +MUTABLE_FORMAT；CreateImageView 深度源图像 aspect/格式归源规则 + `VkImageViewUsageCreateInfo` 收窄（D32 深度孪生解释采样视图，对位 D3D12 R32Float 解释）；RecordClearDepth 清屏范围跟随 DSV 视图 mip/层窗口（CSM 逐级联层清屏不波及已绘层） |
| `Native/zrhi/vulkan/zrhi_vulkan_commandlist.cpp` | set0 布局 COMBINED_IMAGE_SAMPLER → **SAMPLED_IMAGE + SAMPLER 分离**（glslc `-x hlsl` 把 Texture2DArray/SamplerState 出成两个 UniformConstant；B-R7-01 combined 方案无带纹理 shader 实测未暴露）；深度采样描述符 imageLayout=DEPTH_STENCIL_READ_ONLY_OPTIMAL；split 屏障 end 半边 no-op（双发触发 VUID-VkImageMemoryBarrier-oldLayout-01197）；depth-only 渲染 pColorAttachments=nullptr |
| `Native/tests/zrhi_d3d12_window.cpp` | +`RunLightsVulkan`（~450 行：簇光多 cube 场景 VK 镜像，vkCmdCopyImageToBuffer 中心 1×1+地面横带 640×4 readback，行紧缩=D3D12 RowPitch 同值 static_assert）+ `RunLightsParity`；+`RunShadowsPhaseVulkan`/`RunShadowsVulkan`（~700 行：CSM 4 级联 depth-only pass + 阴影图采样主 pass，探针 1×1 readback）+ `RunShadowsParity`；main() 分发 `--lights/--shadows --backend vulkan\|both`；`--r5/--fsr/--fsr4` NOTICE 显式；`RunLightsMode`/`RunShadowsMode` 加可选 out 参数（缺省空=D3D12 行为零改动）；本代理订正 r5 NOTICE 注释（6→8 着色器文件） |
| `Native/tests/test_zrhi_vulkan_translate.cpp` | +1 用例"ZrVulkan 阴影接线镜像：格式/布局/访问/阶段/剔除逐值"（13 断言：D32/R32F 格式映射、深度三态布局分轨、DepthStencil 读写访问位、LateFragmentTests/FragmentShader 阶段、Front/None 剔除） |
| `UnityProject/Assets/Plugins/ZRender/zrender.dll` | build_native.ps1 第 4/4 步自动部署（既有管线行为，ADR-004 dll 入库；非手动碰 Unity） |

### ③ 自测数字（全部实跑取证，2026-10-26）

| 项 | 结果 |
|---|---|
| build_native.ps1 | **ALL GREEN**；单测 **161 用例 / 704,980 断言全过**（基线 160/704,967 → +1 用例 +13 断言，只增不减） |
| `--lights --backend vulkan` | **VULKAN LIGHTS OK**，exit 0：60 帧、6 次读回校验（centerPass=6 groundLitPass=6 animPairs=3/5 达标 ≥3/5）、**validation errors=0** |
| `--lights --backend both` | **D3D12/VULKAN LIGHTS PARITY OK (maxDelta=0, tolerance=16)**；RG 屏障计数 EQUAL：d3d12=vulkan=[total=6 splits=0 calls=4 begin=0 end=0 heaps=0 transients=0]；6 个校验帧中心像素逐帧 RGB 完全一致（如 check1 d3d12=(29,32,27) vulkan=(29,32,27)） |
| `--shadows --backend vulkan` | **VULKAN SHADOWS OK (probe delta=29)**，exit 0：off 相位探针(250,389) R=43 → on 相位 R=14（可证伪对比 delta=29 ≥ min 10）、**validation errors=0** |
| `--shadows --backend both` | **D3D12/VULKAN SHADOWS PARITY OK**：off d3d12=43 vulkan=43 **delta=0**；on d3d12=14 vulkan=14 **delta=0**（同一探针点同口径 readback 并排对照） |
| `--r5 --backend vulkan` | NOTICE 显式打印（未接线声明），D3D12 路径照常：**D3D12 R5 OK (ssr=134 gi=23 toon=141)**，exit 0 |
| `--fsr on --backend vulkan` | NOTICE 显式打印（FFX 无 VK 组件），D3D12 照常：**D3D12 FSR OK (edge=0.159 vs 0.118, ratio=1.346, backend=Fsr3 3.1.5)**，exit 0 |
| `--fsr4 --backend vulkan` | NOTICE 显式打印，D3D12 照常运行，exit 0 |
| d3d12 回归 | `--draw`（默认）**D3D12 FIRST DRAW OK**（InfoQueue=0）；`--lights`（默认）**D3D12 LIGHTS OK**；`--draw --backend both` **PARITY OK maxDelta=0**（B-R7-01 行为无回归——本轮描述符布局/split 屏障改动不破坏首绘路径） |
| VK 泄漏 grep | `Native/zrhi/`（除 vulkan/）grep `VkBuffer\|VkImage \|vkCmd\|VK_STRUCTURE_TYPE\|VkFormat `：命中全为文档注释（B-R7-01 既有模式），无实际类型使用；真实 VK API 仅 vulkan/ 子目录与双后端测试骨架（B-R7-01 既成事实） |
| 文件规范 | 全部改动文件 UTF-8 无 BOM（head -c 3 = "// "）、LF（git ls-files --eol: i/lf w/lf）、#pragma once 齐备 |

### ④ 未做模式及原因（如实）

1. **--fsr / --fsr4 Vulkan 接线——FFX 无 Vulkan 组件，列受限**（非本任务可解）：
   - 证据 ①：`Native/ThirdParty/ffx-fsr/fsr_sdk_v2.3.0_prebuilt.zip` 共 **231 entries，`vk|vulkan` 匹配 **0** 条**（PowerShell ZipFile API 实测）；
   - 证据 ②：`Native/ThirdParty/ffx-fsr/bin/` 仅 `amd_fidelityfx_loader_dx12.dll` + `amd_fidelityfx_upscaler_dx12.dll`（dx12 only）；
   - 结论：FFX 超分运行时在 Vulkan 侧无对位物（与 780M 核显 FSR4 不可用同属外部依赖受限）。**运行时显式 NOTICE**（"FFX SDK v2.3.0 ships no Vulkan component - dx12 dlls only"），D3D12 路径照常，未装作通过。Bilinear-only VK 对照未做：FFX on/off 对比的本体是 FFX 算法，VK 侧 Bilinear 无法构成"同模式对照"，做了也是假对照。
2. **--r5 Vulkan 接线——未做，工作量与优先级裁决**：
   - 面积：D3D12 侧 RunR5Phase+RunR5Mode 约 1300 行、**8 个着色器文件 / 16 个入口点**（pbr_r5/toon/toon_outline/shadow_depth/hiz/ssr_trace/ssr_blur/composite）需逐 PSO 配 SPIR-V 绑定基（各 PSO rootCbvCount/srvTableCount 不同）、Hi-Z mip 链逐 mip RTV、SSR 半分辨率链、GI 探针轮转、描边 pass——镜像量大于 lights+shadows 之和；
   - 简报优先级 r5 列末位且"做到哪个算哪个"；前代理已作同样裁决（NOTICE + 理由入码注释）。本代理评估后续建成本与验证调试风险（每个着色器可能有 glslc HLSL 语义缺口需逐个排障）超出本轮收尾预算，**保持 NOTICE 显式**（亲验打印），不装作通过、不接一半。
   - 遗留建议：若后续派发，宜单开任务按 hiz→ssr→composite 分段接线，每段可证伪。

### ⑤ 风险

- **VK readback 用 vkCmdCopyImageToBuffer 直调**（B-R7-01 既有模式）：测试骨架直用 VK API 换取精确对照，RHI 抽象的 readback 正式化留待后续。
- **深度孪生解释视图**依赖 D32+DEPTH aspect 采样（.r 返回深度值）语义——AMD 780M 实测与 D3D12 一致，其他 IHV 的 D32 采样行为理论上同规范但未多机验证。
- **pbr_shadow 绑定基硬编码于 CMake 注释与 device 布局两处**（N=4 M=1），若改 PSO root 签名须同步两处（单测镜像断言可部分兜底）。
- **--r5/--fsr NOTICE 后 D3D12 照跑**：`--backend vulkan` 的退出码是 D3D12 路径的——脚本若只看 exit code 会误判"VK 已跑"；NOTICE 行是唯一区分标志（验收清单第 3 项按 NOTICE 行亲验）。
- workbuddy 半成品的注释口径（如"6 着色器"）已订正一处；若验收发现其余口径偏差以实跑数字为准。

## 验收清单（主控写）

1. build 全绿（用例数 ≥160）
2. 亲跑每个已接线的模式：`--backend vulkan` 输出关键行 + 与 d3d12 对照数字
3. 未接线的模式：运行时 NOTICE 仍显式（不静默回退）——亲验
4. FFX Vulkan 组件可用性结论如实（查得到就给证据，查不到就列受限）
5. 单测真断言（行号）；VK 泄漏 grep 复查
6. d3d12 回归（--draw/--lights）
7. 文件规范 + 报告四节

## 验收结论（验收代理写）

**PASS**（2026-10-23，general-purpose 验收子代理独立实跑取证，七项逐项核验；未跑 Unity——本任务不涉 Unity）

| # | 项 | 结论 | 证据（实跑/实查） |
|---|---|---|---|
| 1 | build 全绿 + 用例数 | PASS | 亲跑 build_native.ps1：**ALL GREEN**，doctest **161 用例 / 704,980 断言全过 0 失败**（基线 160/704,967 → +1 用例 +13 断言，与报告一致，只增不减） |
| 2 | 已接线模式亲跑 | PASS | `--lights --backend vulkan`：**VULKAN LIGHTS OK** exit 0，60 帧 centerPass=6/6 groundLitPass=6/6 animPairs=3/5，**validation errors=0**；`--lights --backend both`：**PARITY OK (maxDelta=0 tolerance=16)**，6 帧中心像素逐帧同值（如 check1 双侧 (29,32,27)），RG 屏障计划 EQUAL（total=6 splits=0 calls=4）；`--shadows --backend vulkan`：**VULKAN SHADOWS OK (probe delta=29)**（off R=43 → on R=14），validation=0；`--shadows --backend both`：off/on 探针双侧均 delta=0 |
| 3 | 未接线模式 NOTICE 亲验 + FFX 证据 | PASS | `--fsr on --backend vulkan` 与裸 `--fsr --backend vulkan` 首行即 NOTICE（"FFX SDK v2.3.0 ships no Vulkan component - dx12 dlls only"），D3D12 照常 **FSR OK (ratio=1.346, backend=Fsr3 3.1.5)** exit 0；`--r5 --backend vulkan` NOTICE 显式 + **D3D12 R5 OK (ssr=134 gi=23 toon=141)** exit 0——均不静默、不假通过；FFX 证据亲查：zip 231 entries 中 `vk|vulkan` 匹配 **0**（PowerShell ZipFile 实测复现），`bin/` 仅 `amd_fidelityfx_{loader,upscaler}_dx12.dll`，SOURCE.md 全部条目 dx12 only |
| 4 | 单测真断言 + VK 泄漏复查 | PASS | `Native/tests/test_zrhi_vulkan_translate.cpp:225` 新用例"ZrVulkan 阴影接线镜像：格式/布局/访问/阶段/剔除逐值"，**13 个真 CHECK 断言**（行 229-254，逐值比对 VK_* 常量，非空壳）；VK 泄漏：`Native/zrhi/`（vulkan/ 外）剥离注释后仅 `zrhi_types.h:251,253` 两处 static_assert 消息字符串（B-R7-01 既有文档口径），无 VK 类型使用，`#include vulkan` 零泄漏（6 处全在 vulkan/ 内）；tests/ 骨架直用 VK API 系 B-R7-01 既成且报告风险①已声明 |
| 5 | d3d12 回归 | PASS | `--draw`（默认）exit 0，**InfoQueue error count=0**，RESOURCE/RG/WINDOW SMOKE 全 OK；`--lights`（默认）**D3D12 LIGHTS OK** exit 0；`--draw --backend both` **FIRST DRAW PARITY OK (maxDelta=0 tolerance=16)**——描述符布局/split 屏障改动未破坏 B-R7-01 首绘路径 |
| 6 | 报告四节完整 | PASS | 任务简报/施工报告（①摘要②变更清单③自测数字④未做模式及原因⑤风险）/验收清单/验收结论四节齐；**2/4 如实声明**（④-1 FFX 无 VK 组件列受限含双证据、④-2 r5 未做给出面积/优先级/遗留分段建议），r5 注释订正核实（zrhi_d3d12_window.cpp:9311"8 着色器文件/16 入口点"）。口径微瑕：③ 中"--draw（默认）D3D12 FIRST DRAW OK"实跑行文为"D3D12 WINDOW SMOKE OK"（FIRST DRAW PARITY OK 在 --backend both），实质（exit 0/InfoQueue=0/无回归）成立，按报告⑤约定以实跑为准，不判 FAIL |
| 7 | 文件规范抽查 | PASS | 6 个改动文件 + 本日志：首字节 `// `/`# T`（**无 BOM**）；od 字节级验证换行为 `\n` 无 `\r`（**LF**，git ls-files --eol: i/lf w/lf 全部命中；注：MSYS grep -c '\r' 在本机会假报 CRLF，以 od/git 为准）；`zrhi_vulkan_device.h` 含 `#pragma once`，.cpp/.txt 不适用 |

**裁决**：七项全 PASS。2/4 模式接线 + 2 模式显式受限/未接的如实申报符合简报"做到哪一个算哪一个、不许接一半不报告"的口径；数字全部可复现。--r5 分段接线建议与 VK readback 正式化留待后续任务。**待主控裁决状态流转与 commit。**
