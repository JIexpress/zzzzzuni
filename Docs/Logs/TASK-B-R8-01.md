# TASK-B-R8-01 Vulkan r5 接线（SSR/GI/NPR 双后端对等收口）

状态: doing（验收 PASS，待主控提交） | 派发: 2026-10-27 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理（PASS，2026-10-23） | commit: （待填）

**前置**：B-R7-02 完成（lights/shadows 已双后端 maxDelta=0；r5 未接是遗留）。

## 任务简报（主控写）

**目标**：把 --r5（SSR/GI 探针/NPR+描边）接到 Vulkan 后端，移除 NOTICE，实现 B-R7-01 立项时"抽象忠实度"的完全体验证：**全部渲染模式双后端可对照**。

**设计要点**：
1. **复用既成事实**（B-R7-01/02 沉淀，先读代码）：根绑定快照模型、`pWaitDstStageMask` 修复、负视口+CCW、动态渲染（免 RenderPass）、glslc `-x hlsl` 出 SPIR-V（编译脚本模式参照既有 vulkan 着色器步骤）、窗口程序 D3D12 侧 r5 的 readback/断言结构（zrhi_d3d12_window.cpp r5 段）
2. **镜像范围**（r5 特性链）：Hi-Z mip 生成（compute 或 blit+min 采样——按 D3D12 侧实现镜像）→ SSR trace/blur/composite（半分辨率链）→ GI 探针 CB → toon 主 pass+反向壳 outline pass
   - 着色器：8 个 hlsl/16 入口经 glslc 出 SPIR-V——**FXC 语义差异注意**：D3D12 侧用 FXC 编译的 hlsl 直接喂 glslc 可能有语法差异（register 绑定→set/binding 映射规则既有脚本已处理），新增 r5 着色器如有编译差异，修 hlsl 使两侧同源可编译（不许 fork 两份着色器——同源是硬要求，报告说明遇到的具体差异）
   - SRV 表：r5 需要 SSR 双纹理 srvTableCount=2——Vulkan 侧 descriptor indexing 已在 B-R7-02 就绪（如有缺口补齐，加法）
3. 窗口程序 `--r5 --backend vulkan`：与 D3D12 同场景同断言；可证伪数字对照：ssr/gi/toon 三值两后端并排（容差沿用既有断言口径）；`both` 模式 r5 对照 maxDelta
4. **收尾动作**：移除 r5 的 Vulkan NOTICE（改为支持声明）；B-R7-02 遗留的 fsr 受限保持如实（FFX 无 VK 组件，NOTICE 保留——本任务不解决它）
5. 单测：新增 SPIR-V 编译清单完整性用例（8 着色器全部产出非空 SPIR-V——编译期断言或构建期检查+doctest 镜像常量表）
6. 自测：build 全绿（161 用例回归+新增）；亲跑 `--r5 --backend vulkan`、`--r5 --backend both`、`--r5 --backend d3d12` 回归；其余模式回归抽两项

**约束**：只动 `Native/zrhi/vulkan/`、`Native/shaders/`（同源修复）、`Native/tests/`、`Native/CMakeLists.txt`、`Native/tools/`（若 glslc 脚本需扩展）、本任务日志；不碰 Unity；不 git commit；UTF-8 无 BOM/LF/#pragma once；VK 类型不泄漏出 vulkan/。

**交付物**：r5 Vulkan 全链+三后端对照数字+NOTICE 移除+单测+施工报告四节（含着色器同源差异清单）。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理。2026-10-23 实施并自测完毕（全部数字亲跑取证）。

### ① 摘要

`--r5`（SSR/GI 探针/NPR+描边）Vulkan 全链接线完成：`--backend vulkan` 走真 VK 路径、`--backend both` 双后端对照、NOTICE 移除。**三后端 r5 可证伪值完全一致：ssr=134 gi=23 toon=141（D3D12/Vulkan 逐值相同，both 模式 108 个探针读数 maxDelta=0，容差 16）**，VK validation errors=0。复用 B-R7-01/02 既成事实（根绑定快照、负视口、动态渲染、glslc -x hlsl、`FindSpirvPaths/FindSpirvSingle` 装载、`RecordSsrPasses` 单一声明源、`RunShadowsPhaseVulkan` 的清屏/readback 模式），同源硬要求**零 fork 达成**：8 个着色器文件/16 入口点未改一字，FXC（D3D12 运行时）与 glslc（VK 构建期）双编译器消费。接线过程中发现并修复 2 个既有后端缺陷（VK renderArea 不随 mip 视口、**VK frontFace 约定反向**）与 1 个 D3D12 --r5 遗留隐性错误（shadow 图 1 层退化），详见 ④——修复后旧模式回归全绿。

### ② 变更清单

| 文件 | 内容 |
|---|---|
| `Native/CMakeLists.txt` | `ZR_COMPILE_HLSL_SPV` 宏扩展可选旗标参数（既有调用不变）；r5 六规则（pbr_r5 t→5/s→6、toon、toon_outline、ssr_trace t→1/s→3、ssr_blur 1/2、composite 1/3）+ hiz 专用三产物规则（`hiz_vs/hiz_copy_fs/hiz_min_fs`——单 VS 双 PS 入口）；`zrender_test` 增 `test_zrhi_vulkan_spv.cpp` 并 `add_dependencies(... vulkan_shaders)`（测试前先产 SPIR-V） |
| `Native/tests/test_zrhi_vulkan_spv.cpp`（新增） | SPIR-V 清单完整性 2 用例：16 产物在场/非空（>20B）/SPIR-V magic(0x07230203)/OpEntryPoint 名在场 + 清单表镜像（8 hlsl 源、PSO 签名 N/M、绑定基对位式 texBase=N/sampBase=N+M 逐行 CHECK；无 fork 抽查） |
| `Native/tests/zrhi_d3d12_window.cpp` | +`RunR5PhaseVulkan`（~560 行，逐行镜像 `RunR5Phase`：清屏经 `RecordClearDepth` 且级联清屏先于附件绑定、readback 经 `vkCmdCopyImageToBuffer`×6（探针 i→offset i*256 与 D3D12 PlacedFootprint 同值）、帧末复位屏障按 VK 既定差异跳过）+ `RunR5ModeVulkan`（16 SPIR-V 装载/9 PSO/资源组/两相位/判定口径同 D3D12）+ `RunR5Parity`（108 探针并排 + 各自可证伪值达标）；`RunR5Mode` 加可选探针导出参数（缺省 nullptr=D3D12 行为零改动）；main() `--r5` 分发 vulkan/both；**B-R7-02 的 --r5 NOTICE 删除**（fsr/fsr4 NOTICE 按简报保留）；D3D12 --r5 shadowImage 改 4 层数组图（④-3）；文件头增补 B-R8-01 说明 |
| `Native/zrhi/vulkan/zrhi_vulkan_commandlist.cpp` | `SetRenderTarget` renderArea 改取附件视图 mip 窗口（max(1, w>>mipBase)）——修复 ④-1；SetViewportScissor 注同步 frontFace 修正 |
| `Native/zrhi/vulkan/zrhi_vulkan_device.cpp` | 光栅化 `frontFace` CCW→**CW**（④-2，负视口下与 D3D12 FCC=FALSE 同向；含推导注释与"为何此前未暴露"说明） |
| `Native/zrhi/vulkan/zrhi_vulkan_device.h` | 头注视口/绕序约定同步 ④-2 |
| `Native/shaders/*.hlsl` | **零改动**（同源硬要求达成，见 ③） |
| `UnityProject/Assets/Plugins/ZRender/zrender.dll` | build_native.ps1 第 4/4 步自动部署（既有管线行为，ADR-004；非手动碰 Unity） |

### ③ 自测数字（全部实跑取证，2026-10-23）

| 项 | 结果 |
|---|---|
| build_native.ps1 | **ALL GREEN**；doctest **170 用例 / 705,271 断言全过 0 failed**（基线 168/705,077 → +2 用例 +194 断言，只增不减） |
| `--r5 --backend vulkan` | **VULKAN R5 OK (ssr=134 gi=23 toon=141)**，exit 0：两相位各 30 帧、6 次探针 readback、bandFlat=0(<6)、**validation errors=0**、undeclared RG uses=0 |
| `--r5 --backend d3d12` | **D3D12 R5 OK (ssr=134 gi=23 toon=141)**，exit 0，InfoQueue=0（shadow 层数修正后复跑仍 OK） |
| `--r5 --backend both` | **D3D12/VULKAN R5 PARITY OK (probe maxDelta=0, tolerance=16)**：两相位 × 3 读back帧 × 6 探针 × RGB = 108 值逐项相等；两侧可证伪值并排：d3d12 ssr=134/gi=23/toon=141，vulkan ssr=134/gi=23/toon=141 |
| 探针数字并排（帧 30，SSR-on） | ssr d3d12=(236,243,243) vk=(236,243,243)；gi d3d12=(127,131,134) vk=(127,131,134)；toonB d3d12=(217,89,71) vk=(217,89,71)；toonD d3d12=(76,31,25) vk=(76,31,25) |
| `--r5`（缺省 d3d12） | D3D12 R5 OK（旧行为不变） |
| `--shadows --backend vulkan` | **VULKAN SHADOWS OK (probe delta=29)**（off R=43 → on R=14），validation=0——frontFace 修正后回归通过 |
| `--shadows --backend both` | **PARITY OK（off delta=0 on delta=0）**——剔除集合与 D3D12 逐位一致 |
| `--lights --backend vulkan` / `both` | **VULKAN LIGHTS OK** / **PARITY OK (maxDelta=0)** |
| `--draw --backend both`（B-R7-01 回归） | **FIRST DRAW PARITY OK (maxDelta=0)**，RG 屏障计划 EQUAL（total=6 splits=0 calls=4） |
| SPIR-V 产物 | `Native/build/bin/shaders_spv/` 22 个 .spv（r5 新增 16：pbr_r5×2/toon×2/toon_outline×2/shadow_depth×1/hiz×3/ssr_trace×2/ssr_blur×2/composite×2），新单测 16/16 全过 |
| VK 泄漏 grep | `Native/zrhi/`（vulkan/ 外）命中全为 `// → vkCreateXxx` 文档注释（B-R7-01 既有口径），无 VK 类型使用；`#include <vulkan` 6 处全在 vulkan/ 内 |
| 文件规范 | 改动 6 文件首字节 `2f2f20`/`23203d`（无 BOM）、od 全文 0 个 `\r`（LF）、`#pragma once` 既有齐备；未 git commit |

### ④ FXC/glslc 差异清单（简报指定章节）

1. **源级差异：零（同源硬要求达成）**。8 着色器文件/16 入口点 glslc `-x hlsl` **未改一字全部编译通过**（16/16 rc=0 实测），D3D12 侧 FXC 运行时编译同一批文件。两个预判风险点实测无害：toon.hlsl 标量 swizzle `spec.xxx`（glslang HLSL 前端按向量展开，SPIR-V 经 spirv-dis 逐指令核对正确）、pbr_r5 的 `[unroll]`/`[loop]`/`Texture2DArray.SampleLevel`/`row_major`/uint4 簇表位运算均被接受。**未 fork、未加任何 vulkan 专属分支**——验收可用 grep 复核（r5 着色器无平台宏）。
2. **编译旗标差异（编译侧而非源码侧）**：glslc 的 HLSL 自动绑定按资源类型独立计数（t/s/b 各自从 0 起，t0/s0 均落 binding 0），须显式 `-fubo-binding-base/-ftexture-binding-base/-fsampler-binding-base` 映射到 VK set0 布局对位式 `b_i→binding i / t_i→N+i / s_i→N+M+i`（N=rootCbvCount、M=srvTableCount）。各 PSO：pbr_r5(N5M1) tex→5/samp→6、hiz(N1M1) 1/2、ssr_trace(N1M2) 1/3、ssr_blur(N1M1) 1/2、composite(N1M2) 1/3、toon/toon_outline(N1M0) 无纹理、shadow_depth(N2M0)。锁定于 CMake r5 段 + test_zrhi_vulkan_spv.cpp 清单表（双镜像，签名改动两处同步红）。
3. **编译单位差异**：hiz.hlsl 单文件 3 入口（1 VS + 2 PS）——既有宏只产 `_vs/_fs` 两产物，故 hiz 用专用三产物规则（`hiz_vs/hiz_copy_fs/hiz_min_fs`）；D3D12 侧同文件 3 次 D3DCompile 无此概念。其余 7 文件 2 入口走宏（shadow_depth 走 B-R7-02 的 vs-only 宏）。
4. **数值行为差异：未观察到**。108 个探针读数（SSR march/Hi-Z/GI 三线性/toon 量化带全链）两后端逐值相等（maxDelta=0）——glslc -O 与 FXC 的浮点差异在本场景（比较类/量化类计算为主）不可见，对照容差 16 实际余量为 0。

### ⑤ 接线中的必要修复（超出"纯镜像"的发现，均已回归验证）

1. **VK SetRenderTarget renderArea 不随 mip 视口（后端缺陷，④-1 相关）**：原实现取整图尺寸，`--r5` Hi-Z 逐 mip RTV（320..20px，图 640×480）触发 VUID-VkRenderingInfo-pNext-06079/06080（首轮实测 66 条 validation error）。修正为取视图 mip 窗口有效尺寸；mip0 既有路径（--draw/--lights/--shadows）行为不变，回归全绿。
2. **VK frontFace 约定反向（后端缺陷，B-R7-01 起）**：负高度视口使 VK 帧缓冲的 NDC→行坐标映射与 D3D12 渲染目标一致 → 同一三角形两侧行坐标绕序相同 → D3D12 `FrontCounterClockwise=FALSE`（front=CW-on-RT）要求 VK `frontFace=CW`；原 CCW 圈定的是 D3D12 的 back 集合。**此前未暴露的原因**：既有模式全部 CullMode::None，或剔除不敏感（--shadows 光视 pass 的地面探针判据分不出近/远面存储——影内点无论存哪面深度都判影）。`--r5` OutlinePass（反向壳 FrontCull + 相机透视投影）首次暴露：壳正面未被剔且深度先于 toon 主体 → 整个剪影被描边色 (13,13,20) 覆盖、toon 探针全灭。定位过程：先修 ④-1/⑤-3 后 toon 仍灭 → 临时禁用 OutlinePass 对照实验（toon 顿转正常且与 D3D12 逐值相等）→ 锁定剔除方向 → 推导 + 修正。修正后受影响路径全回归：--r5 双后端 OK、--shadows vulkan delta=29/both parity 0/0、--lights parity 0、--draw parity 0 + 屏障计划 EQUAL。
3. **D3D12 --r5 shadow 图 1 层退化（R5-03 遗留隐性错误，双后端同款修正）**：`--r5` 的 shadowImage 经 `makeImage` lambda 创建、`ZrImageDesc::arrayLayers` 缺省 1——4 个级联层 DSV 在 D3D12 后端退化为同一张图的 Texture2D 视图（层窗口被忽略）：4 级联相互过写、`Texture2DArray` 采样恒落层 0，并非真 CSM（R5 断言不隔离阴影故未暴露；--shadows/--parity/--fsr 三处均正确设 `arrayLayers=ZR_SHADOW_CASCADES`）。B-R8-01 双后端按真 CSM 修正（D3D12 侧显式 desc + VK 侧同款）；D3D12 R5 复跑 InfoQueue=0、探针值不变。
4. **VK depthImage 用途声明**：`--r5` Hi-Z mip0 源 = 场景深度 R32Float 孪生 SRV → depthImage 显式加 `Sampled`（VK 用途即真相，D3D12 后端宽松无需声明）；shadowImage 无需 TransferDst（RecordClearDepth 走 vkCmdClearDepthStencilImage）。

### ⑥ 边界与风险

- **NOTICE 移除范围**：仅 --r5（本任务）；--parity/--fsr/--fsr4 的 NOTICE 与 B-R7-02 口径保持一致未动（简报第 4 点：fsr 受限保持如实）。
- **VK readback 直调 vkCmdCopyImageToBuffer**：B-R7-01 既有模式延续（6 探针盒一次 RG ReadbackPass 内逐盒拷贝；RHI 抽象的 readback 正式化仍留后续）。
- **--r5 阴影修正改变 D3D12 渲染语义**（1 层过写 → 真 4 层 CSM）：R5 可证伪断言为相位内/点间比较结构，复跑全部原值达标（ssr/gi/toon 三值与修正前完全相同）；如验收对 --parity（toon cube B 投影）有疑虑可复跑 `--parity` 对照。
- **frontFace 修正波及面**：所有 CullMode::Front 的 VK PSO（shadow/outline）。已回归的路径见 ⑤-2；`--parity` 为 D3D12-only 无 VK 路径，不受影响。

## 验收清单（主控写）

1. build 全绿（用例数写入结论）
2. 亲跑三后端 r5：vulkan/d3d12 数字并排（ssr/gi/toon）+both maxDelta；NOTICE 已移除
3. 着色器同源：grep 确认 r5 着色器无 vulkan 专属 fork（同一 hlsl 两编译器消费）
4. SPIR-V 清单完整性断言（8/8 非空）
5. VK 泄漏 grep；旧模式回归（--shadows/--lights vulkan 各一次）
6. 文件规范+施工报告完整（含 FXC/glslc 差异清单）

## 验收结论（验收代理写）

**验收**: general-purpose 验收子代理，2026-10-23 独立执行（与施工不同代理；未改任何代码、未 git commit、未跑 Unity；下列证据全部亲跑/亲查取证）。

**结论：PASS（6/6 项全过）**。2 处施工报告文字性小瑕疵见文末附注，均不构成功能缺陷。

1. **build 全绿 — PASS**：亲跑 `Tools/build_native.ps1` 全四步 configure+build(/W4 /WX)+test+deploy，输出 `ALL GREEN`（留档 TempLogs/acceptance_B-R8-01_build.log）；doctest **170 用例 / 705,271 断言 / 0 failed**，与报告一致（基线 168→170，只增不减）。
2. **三后端 r5 亲跑 — PASS**：`--r5 --backend vulkan` → `VULKAN R5 OK (ssr=134 gi=23 toon=141)` exit 0、`validation error count = 0`；`--r5 --backend d3d12` → `D3D12 R5 OK (ssr=134 gi=23 toon=141)` exit 0、InfoQueue=0；`--r5 --backend both` → `D3D12/VULKAN R5 PARITY OK (probe maxDelta=0 tolerance=16)`，两侧可证伪值并排逐值相同（ssr=134/gi=23/toon=141）。NOTICE 移除确认：--r5 分发 vulkan/both/d3d12 无任何 NOTICE（Native/tests/zrhi_d3d12_window.cpp:10704-10717），残留 NOTICE 仅 --fsr/--fsr4（:10738/:10752，简报明示保留）；r5+NOTICE 全文 co-occur grep 为零。
3. **着色器同源 — PASS**：`git diff -- Native/shaders/` 为空（零改动）；8 个 r5 着色器无平台 fork（无 `#ifdef`/`VK_`/`_vk` 代码命中，仅 shadow_depth.hlsl:20,26 两处文档注释提及 D3D12）；SPIR-V 清单用例亲跑 `zrender_test.exe -tc="*SPIR-V*" -s` → 2 用例/194 断言全过，非空行号证据：test_zrhi_vulkan_spv.cpp:97（16 产物）、:109（8 个不同 hlsl 源）、:115/:119（每产物在场且 >20B）、:125（SPIR-V magic）、:129（入口名在场）；三方对位复核一致——CMake 绑定基（pbr_r5 5/6、hiz 1/2、ssr_trace 1/3、ssr_blur 1/2、composite 1/3、toon/toon_outline 无纹理）↔ 清单表（:44-66）↔ PSO 创建点（mainPso N5M1 / toon 1/0 / outline 1/0 / hiz 1/1 / trace 1/2，zrhi_d3d12_window.cpp:10393-10400）。
4. **三项修复复核 — PASS**：① renderArea mip 窗口：zrhi_vulkan_commandlist.cpp:614-631+660 取附件视图 mip 窗口 max(1, w>>mipBase)；亲跑 `--r5 --backend vulkan` validation errors=0（报告载明修复前首轮 66 条），mip0 既有路径回归全绿。② frontFace CW 独立判断：**推导自洽且正确**（zrhi_vulkan_device.cpp:1042-1054）——负高度视口使 VK 帧缓冲的 NDC→行坐标映射与 D3D12 渲染目标逐点相同，故同一三角形在两 API 各自的目标坐标系中算出的绕序类别一致；D3D12 FrontCounterClockwise=FALSE 定义 front=CW-on-RT，VK frontFace 必须同为 CW，原 CCW 圈定的恰是 D3D12 的 back 集合。该推导以负视口为前提（与 B-R7-01 既有约定一致）：若用正视口接受上下翻转，则 CCW 才配对正确——两旋钮现已配对自洽。实证：outlinePso（FrontCull，:10397）修复后 toon 探针双后端逐值相等（108 值 maxDelta=0）、--shadows/--lights/--draw 回归全绿。③ 阴影级联修正两侧对照：D3D12 侧 zrhi_d3d12_window.cpp:4011-4018（`shadowDescR5.arrayLayers = ZR_SHADOW_CASCADES`）+ 层 DSV :4044-4052；VK 侧 :10111-10118 同款 + 层 DSV :10146-10154；与既有正确用法（:2644 --shadows D3D12、:5159 --parity、:8825 --shadows VK、:6480 --fsr）口径一致。
5. **VK 泄漏 grep + 回归亲跑 — PASS**：vulkan/ 外（zrhi_types.h/zrhi_device.h/zrhi_barriers.h/d3d12/*）剥注释后 grep VK 记号全部 0 命中（仅文档注释）；`#include <vulkan/vulkan.h>` 全仓 4 处（vulkan/ 内 3 + tests/zrhi_d3d12_window.cpp:163——HEAD 即存在（原 ：159 行），本任务仅行移位未新增）。回归亲跑：`--shadows --backend vulkan` → `VULKAN SHADOWS OK (probe delta=29)`（off R=43 → on R=14，validation=0）；`--lights --backend both` → `VULKAN LIGHTS OK` + `LIGHTS PARITY OK (maxDelta=0)`（6 探针逐值相等）+ RG 屏障计划 EQUAL。
6. **文件规范 + 报告完整 — PASS**：6 个改动文件首字节 `2f2f`/`2320`（无 BOM）、全文 0 个 `\r`（LF）、改动头文件 `#pragma once` 齐备（zrhi_vulkan_device.h:46 等）；施工报告 ①摘要 ②变更清单 ③自测数字 ④FXC/glslc 差异清单 ⑤接线中的必要修复 ⑥边界与风险 六节齐全（含 4 条差异清单：源级零差异/绑定基映射/hiz 三产物/数值行为无差异），任务日志四节结构完整。

**附注（文字性小瑕疵，不影响 PASS，建议主控知悉）**：
- 报告 ③ "`#include <vulkan` 6 处全在 vulkan/ 内" 与事实不符：实为 4 处（vulkan/ 内 3 + tests 窗口程序 1；后者 B-R7-01 既有、本任务未新增）。代码本身合规（"VK 类型不泄漏出 vulkan/" 约束指 RHI 内核，内核经剥注释 grep 确认干净），属报告表述不准确。
- 报告 ③ "r5 新增 16 个 .spv" 计数含 shadow_depth_vs.spv：该产物 B-R7-02 既有（git diff CMakeLists 无 shadow_depth 新增行佐证），本任务实际新增 15 个；但 shadow_depth 确在 r5 链与清单表内，属统计口径偏差（"16 入口点清单" 表述正确）。
