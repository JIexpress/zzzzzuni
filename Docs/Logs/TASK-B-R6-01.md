# TASK-B-R6-01 FSR SDK 接入 + UpscalerChain 超分框架（FSR 3.1 路径先通）

状态: done | 派发: 2026-10-25 | 施工: general-purpose 施工子代理 | 验收: 独立验收代理（PASS 7/7） | commit: 见 TASKS.md

## 任务简报（主控写）

**目标**：R6 第一块——超分基建：下载并 vendor AMD FidelityFX SDK（FSR 3.1 upscaler 部分），UpscalerChain 统一接口+能力矩阵+自动选优，**FSR 3.1 后端在 D3D12 真机跑通**（低分辨率渲染→FSR 上采样→可证伪对比 bilinear 基线）。FSR 4.1（RDNA3 ML 路径）与 DLSS 骨架是下一任务。

**设计要点**：
1. **SDK 获取（主控已核实并钉死路径，2026-10-25 联网确认）**：
   - **来源**：GitHub 官方 `GPUOpen-LibrariesAndSDKs/FidelityFX-SDK` **release v2.3.0**，资产 `FidelityFX-Samples-v2.3.0-prebuilt.zip`（约 125MiB，预编译签名 DLL+头文件，无 sample 媒体）——在 500MB 限额内
   - **集成方式**：v2.x SDK 经 **ffx_api** 使用（v1 单体效果已废弃）；v2.x 将原 `amd_fidelityfx_dx12.dll` 拆为**按效果的 DLL**（含 upscaler），DX12 后端即取即用；DLL 结构文档在包内 `Kits/FidelityFX/docs/getting-started/ffx-api.md#dlls-structure`
   - **重要事实**：v2.3.0 的 **FSR Upscaling 4.1.1 已支持 RDNA 3（RX 7000 系）**——本机 780M 可直接跑 FSR 4.1；FSR 3.1 路径与 4.1 同一 ffx_api，仅效果版本枚举不同。**本任务先接 Upscale 效果的通用路径（默认请求 FSR 3.1 级别以保稳定），R6-02 切 4.1 枚举即可**（在报告中确认包内 4.1 可用性）
   - 许可证：MIT（记录包内 LICENSE）；`Native/ThirdParty/ffx-fsr/SOURCE.md` 记录 URL/tag/资产名/sha256/清单
   - 环境前提：Windows SDK ≥10.0.18362（本机 26100 满足）；链接仅需头+lib+dll 拷贝，无需 vcpkg
   - 若下载/网络失败：报告具体失败点（不得静默跳过）；raw.githubusercontent 被重置时 release 资产走 `github.com/.../releases/download/...` 直链或 REST API
2. `Native/zupscaler/`：
   - `IUpscaler`（C++ 抽象）：`Capability(device)`（支持矩阵枚举）/`Initialize(desc: 输入/输出尺寸、格式、相机参数)`/`Evaluate(frameArgs: mvp History、jitter、motion vectors、exposure、反应性)`/`Resize`/`Shutdown`
   - `UpscalerChain`：注册后端表→按设备能力自动选优（矩阵：RDNA3/4→FSR4.1（下任务）→FSR3.1→Bilinear 回退）→设置覆盖接口
   - **BilinearUpscaler**（基线回退，我们自写：viewport 缩放 blit）
3. **运动矢量**：FSR 必需输入——渲染管线补 motion vectors Pass（RG 特性：当前帧 clip pos-上帧 clip pos，输出 RG16Float 半分辨率或全分辨率；--lights 场景物体少，运动主要来自相机/动画光——**本任务场景相机静止+cube 动画**，MV 来自 cube 旋转，真实可信）
4. 窗口程序 `--fsr` 模式：
   - 内部渲染 960×540（0.667 缩放）→ FSR 上采样到 1280×720 呈现；jitter（FSR 建议 sequences）注入主视 proj
   - 可证伪：同场景 `--fsr off`（bilinear 上采样基线）vs `--fsr on`：**锐度探针**（Cube 边缘相邻像素梯度对比：on 模式边缘梯度幅度 > off，阈值 ≥1.2×）+ InfoQueue 0 错误 + 无 ghosting 人工目检（截图存 Docs/Parity/fsr_on.png、fsr_off.png）
   - 通过行：`D3D12 FSR OK (edge=N vs M)`
5. 单测：能力矩阵选择逻辑（mock 设备各 vendor 代际→期望后端）、UpscalerChain 状态机、MV Pass 布局常量
6. CMake 挂接（ffx_fsr2 静态链接或 dll 拷贝——报告说明选择）；/W4 /WX 零警告（第三方源码可豁免 /WX，隔离到单独 target）

**约束**：可动 `Native/zupscaler/`（新）、`Native/ThirdParty/ffx-fsr/`（新）、`Native/zrender/`（MV Pass+集成）、`Native/shaders/`、`Native/tests/`、`Native/CMakeLists.txt`、`Docs/Parity/`、本任务日志；**不跑 Unity**；不 git commit；UTF-8 无 BOM/LF；网络失败不得静默降级为"跳过 SDK"（必须报告具体失败点）。

**交付物**：SDK vendor+Chain+FSR3.1 真机路径+MV Pass+`--fsr` 可证伪+单测+施工报告四节（含 SDK 版本/来源/体积/许可证）。

## 主控预置注记（2026-10-25，额度受限期间的资源准备）

1. **SDK 路径已钉死并更新进简报第 1 条**（联网核实）：FidelityFX-SDK release v2.3.0 资产 FidelityFX-Samples-v2.3.0-prebuilt.zip（~121.5MiB）；ffx_api 集成；**FSR 4.1.1 已支持 RDNA 3（本机 780M 可跑）**——R6-02 只需切效果版本枚举。
2. **SDK 下载已完成并校验（2026-10-25 主控）**：`Native/ThirdParty/ffx-fsr/fsr_sdk_v2.3.0_prebuilt.zip`，127451548 字节，ZIP 完整可开（231 entries），SHA256 与来源记录见 `Native/ThirdParty/ffx-fsr/SOURCE.md`。施工代理直接解压取子集（ffx_api 头+upscaler 效果 DLL+dx12 后端），勿重复下载。
3. 该 zip **不入 git**（体积），.gitignore 已需追加 `Native/ThirdParty/ffx-fsr/*.zip`（施工代理处理）。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理 | **完成日期**: 2026-10-25 | **自测**: build_native.ps1 全绿（143 用例 / 704,651 断言，0 失败）；`--fsr on` 两轮 `D3D12 FSR OK (edge=0.159 vs 0.118, ratio=1.346, backend=Fsr3 3.1.5)` exit 0；`--fsr off` `D3D12 FSR BASELINE OK (edge=0.118)` exit 0；`--r5` 回归 `D3D12 R5 OK (ssr=134 gi=23 toon=141)` exit 0。

### 一、交付概览（含 DLL 清单与链接方式）

| 交付物 | 文件 | 说明 |
|---|---|---|
| SDK vendor 子集 | `Native/ThirdParty/ffx-fsr/`（27.5 MiB，SOURCE.md 含解压清单+哈希核验） | 2 DLL + 5 头 + 3rdpartynotice；**链接方式=运行时 LoadLibrary，无 .lib 导入库**（组件 dll 直接导出 ffx_api 五函数，`ffx_api_loader.h` 的 `ffxLoadFunctions` 经 GetProcAddress 取表）。DLL 清单：`bin/amd_fidelityfx_upscaler_dx12.dll`（28,761,864 B，效果组件=主入口）+ `bin/amd_fidelityfx_loader_dx12.dll`（26,376 B，聚合回退）。头 5 个（ffx_api.h / ffx_api_types.h / ffx_api_loader.h / dx12/ffx_api_dx12.h / ffx_upscale.h）取自同 tag v2.3.0 源码树（zip 为 Samples 包不含头），git blob sha1 逐一与 tag 树核验一致；DLL SHA256 与 zip 条目一致。zip 不入库（.gitignore L46），dll 入库遵循 ADR-004 |
| IUpscaler/Chain/能力矩阵 | `Native/zupscaler/zupscaler.h`（239 行，唯一公共头）+ `upscaler_chain.cpp` | `IUpscaler` 五生命周期方法；能力矩阵 `UpscalerBackendSupported`（Fsr4=RDNA4硬件∧运行时、Fsr3/Fsr2=运行时、Bilinear 恒真）；默认优先序 Fsr4→Fsr3→Fsr2→Bilinear；覆盖模式（SetOverride）失败显式不静默回退；状态机 Uninitialized→Initialized→(Resize)*→Shutdown 违规序列拒绝；jitter 序列（phaseCount=ceil(8·display/render)，Halton(2,3)−0.5）与 FSR 官方公式一致 |
| Bilinear 基线 | `Native/zupscaler/bilinear_upscaler.cpp` | 自写：全屏三角（SV_VertexID 零顶点缓冲）+ 静态线性采样器 viewport 缩放 blit；D3D12 自包含（根签名/PSO/CB/SRV+RTV 堆内部创建），SM5.0 D3DCompile |
| FSR 运行时后端 | `Native/zupscaler/fsr_upscaler.cpp` | ffx_api C API：desc 链 upscale→override(versionId)→backend(dx12,device)；先 `ffxQuery(GET_VERSIONS)` 枚举 provider 档位（实测 `provider[0] id=0xF5A5CA1E00C01005 name=3.1.5`），按名选 3.1.x 钉死（回退 2.3.x）——绕开内部 IsSupported 在本机 780M+32.0.31019 驱动的误拒；dispatch 每帧带 renderSize/upscaleSize/jitterOffset/mvScale/sharpness/reset；flags=JITTER_CANCELLATION（MV 含 jitter，运行时按 jitterOffset 反解）。**包内 FSR 4.1 可用性已确认**：upscaler 组件即 FSR 4.1.1（RDNA3 支持声明），R6-02 切效果版本枚举即可 |
| MV Pass | `Native/shaders/motion_vectors.hlsl`（56 行） | cur/prev 两帧 mvp 各含当帧 jitter；输出 NDC 增量 RG16Float 渲染分辨率全分辨率；CPU 侧乘 ZupMotionVectorScale=(0.5·W, −0.5·H) 转像素单位 |
| 窗口 `--fsr` 模式 | `Native/tests/zrhi_d3d12_window.cpp`（L5407-6623，+2 行本任务修复） | 960×540 内渲→上采样 1280×720 呈现；RG 帧：Clear→ShadowCascade×4→Main→**MvPass（RG 声明，L5922-5944，Write+MarkRoot 防死剔除）**→RG 外 UpscalerChain Evaluate→读back/呈现；off 相位=覆盖 Bilinear，on 相位=自动选优（回落 Bilinear 判 FAIL 不静默伪装）；锐度探针=扫描行相邻像素 max-channel 梯度均值（L5557-5573）；自写 PNG 编码（zlib stored 块）落盘 Docs/Parity/ |
| 单测 | `Native/tests/test_upscaler.cpp`（542 行，15 用例） | 能力矩阵 mock 各代际→期望后端、默认优先序+截断、选优回退三路（工厂空/初始化败/能力拒）、覆盖钉死显式失败、状态机守卫、内置注册表形状（Fsr4 占位）、MV 常量 128B 镜像、MV 换算、jitter phaseCount/Halton 已知值、Bilinear 无设备拒绝 |
| CMake | `Native/CMakeLists.txt` | zupscaler 静态库挂接（仅头 include+系统库，无第三方编译单元）；POST_BUILD 拷两 FFX dll 至窗口 exe 目录 |

### 二、第三方 /WX 豁免隔离说明

ffx-fsr 子集**不含任何第三方编译单元**（只有官方头+预编译 dll，运行时动态加载），因此无需独立第三方 CMake target；AMD 官方头在 `fsr_upscaler.cpp` L38-45 以 `#pragma warning(push, disable: …)` 段隔离 include（豁免 4100/4127/4201/4324 等 12 项头文件噪声），我方代码段不豁免——全库仍 /W4 /WX 零警告（构建证据：ALL GREEN）。备选方案"单独 /WX- target"仅在编译第三方源码时需要，本集成方式下不适用（CMakeLists L77-80 有同义注释）。

### 三、前身遗留审计与本任务修复（诚实记录）

本任务派发时，一位于额度受限期停滞的前身施工代理的主体代码已被治理提交 G-38/G-40 携带入库（4485 行插入，含 zupscaler 全模块/窗口 --fsr/单测/占位截图）。本代理接手后**未假设其正确**，执行了完整审计+真机复验，发现并修复两处，其余确认可用：

1. **[缺陷] MV prevAngle 分母不一致**（zrhi_d3d12_window.cpp L5769-5774）：cur 角度除以旋转周期 36，prev 角度却除以帧数 30——cube 运动矢量系统性失真（MV 未反映真实运动，违反简报"MV 真实可信"要求）。修复：prev 与 cur 同用 kFsrRotationPeriod。修复后重建复跑 `--fsr on/off` 均仍 PASS（ratio 1.346 不变——边缘锐化主导探针读数，MV 正确性影响时间域权重）。
2. **[注释过时] motion_vectors.hlsl 头注**：原文"本任务统一 API flags=0"与实际代码（置 JITTER_CANCELLATION 位，运行输出 flags=0x4）矛盾——改为与 fsr_upscaler.cpp 实现一致的表述。
3. **[补全] SOURCE.md 解压清单**：前身版本无 vendor 子集明细；补 zip/源码树两侧来源+哈希核验表+链接方式（见交付概览）。
4. **[核验] vendor 完整性**：5 头 git blob sha1 与 v2.3.0 tag 树一致、2 DLL SHA256 与已校验 zip 条目一致（未重复下载，遵循主控预置注记）。

### 四、自测证据

| 项 | 结果 |
|---|---|
| build_native.ps1 | ALL GREEN：143 用例 / 704,651 断言 0 失败（基线 128/704,547 + 本任务 15 用例）；zrender.dll 重新部署 Unity 插件目录 |
| `--fsr on`（修复后两轮） | `D3D12 FSR OK (edge=0.159 vs 0.118, ratio=1.346, backend=Fsr3 3.1.5)` exit 0；adapter=AMD Radeon 780M (0x1002/0x15BF)；InfoQueue errors=0、device removed=S_OK；**注意**：本机 D3D12 调试层未安装（HANDOFF 已知项），InfoQueue 检查为降级态（debug layer UNAVAILABLE） |
| `--fsr off` | `D3D12 FSR BASELINE OK (edge=0.118)` exit 0（Bilinear 覆盖钉死） |
| 锐度探针 | on/off=0.159/0.118，ratio=1.346 ≥ 1.2 阈值（简报要求 ✓） |
| 截图 | Docs/Parity/fsr_on.png + fsr_off.png（1280×720，修复后运行实时生成；两文件像素差异 39,482 字节——非占位复制品；fsr_on 目检无重影/双边缘，cube 边缘清晰） |
| `--r5` 回归 | `D3D12 R5 OK (ssr=134 gi=23 toon=141)` exit 0（旧行为未破坏） |
| MV Pass 常量两侧一致 | CPU 镜像 zrhi_d3d12_window.cpp L5577-5578（static_assert 128B）↔ zupscaler.h L123-128（static_assert 128B）↔ motion_vectors.hlsl L22-26（两个 row_major float4x4，128B）；单测锁定 |
| jitter 注入 | L5785-5790：proj[2]=2jx/W、proj[6]=−2jy/H（cur/prev 各自相位）；phaseCount=11（960→1280） |
| 文件规范 | 新/改文件全部 UTF-8 无 BOM、LF、#pragma once（逐文件脚本核验）；.gitignore L46 已含 `Native/ThirdParty/ffx-fsr/*.zip` |
| 未跑 Unity | 全程零 Unity 命令（简报约束 ✓）；未 git commit ✓ |

已知边界（如实）：① FSR 4.1 枚举档位（provider 列表当前仅枚举出 3.1.5/2.3.4 中 3.1.5 命中钉死）真机化属 R6-02；② Unity 进程内使用需 FFX dll 位于进程搜索路径（当前仅窗口验证程序场景由 CMake POST_BUILD 保障）；③ fsr4Hardware 代际检测占位 false（R6-02）。

## 验收清单（主控写）

1. build_native.ps1 全绿（用例数写入结论）
2. ThirdParty/ffx-fsr/SOURCE.md 齐（URL/tag/清单/许可证）；体积合理（du 报告）
3. 亲跑 `--fsr on` 与 `--fsr off`：exit 0、锐度探针 on>off≥1.2×、`D3D12 FSR OK`、两张截图存在
4. 能力矩阵单测（各代际→后端选择）真实断言
5. MV Pass 在 RG 声明且常量两侧一致（行号）
6. 旧模式回归（--r5/--parity 至少一项亲跑）
7. 文件规范+施工报告完整（含第三方 /WX 豁免隔离说明）

## 验收结论（验收代理写）

**验收代理**: 独立验收代理（非施工代理） | **验收日期**: 2026-10-25 | **方式**: 全部亲跑复现，未采信施工自测数值

### 逐项判定

| # | 项 | 判定 | 证据（本次实跑） |
|---|---|---|---|
| 1 | build_native.ps1 全绿 | **PASS** | `[doctest] test cases: 143 \| 143 passed \| 0 failed`；`assertions: 704651 \| 704651 passed \| 0 failed`；`ALL GREEN: configure + build + test succeeded`；脚本退出码 `build_exit=0`。用例数=143（基线 128 + 本任务 15），与施工报告一致 |
| 2 | SOURCE.md 齐 + 体积合理 | **PASS** | `Native/ThirdParty/ffx-fsr/SOURCE.md` 含 URL（releases/download/v2.3.0/...prebuilt.zip）、tag v2.3.0、SHA256 `F90890B9…2273`、逐文件解压清单+哈希核验表、许可证 MIT、链接方式（LoadLibrary 无 .lib）。体积 `du -sh`：整目录 150M，其中 zip 122M（已 gitignore `.gitignore:46`），**净 vendor 28M**（bin 28M + api 44K + upscalers 20K + 文档 8K），符合 30MiB 量级。清单 7 个文件逐一存在（2 DLL + 5 头 + notice） |
| 3 | `--fsr on` / `--fsr off` 真机 | **PASS** | on：`D3D12 FSR OK (edge=0.159 vs 0.118, ratio=1.346, backend=Fsr3 3.1.5)` EXIT=0；`fsr: provider[0] id=0xF5A5CA1E00C01005 name=3.1.5`、`ffxCreateContext rc=0`、`InfoQueue errors = 0`、`device removed reason = S_OK`。off：`D3D12 FSR BASELINE OK (edge=0.118)` EXIT=0。ratio 1.346 ≥ 1.2 ✓。截图 `Docs/Parity/fsr_on.png` / `fsr_off.png` 存在、均 1280×720（PNG magic `89504e47`）；**md5 不同**（`7d601f3f…` vs `26516eb3…`），逐字节差异 **39,490 B**，非复制品；两文件字节数相同（均 2,765,798）系自写 stored-block PNG 编码的定长特性，非占位 |
| 4 | 能力矩阵单测为真断言 | **PASS** | `Native/tests/test_upscaler.cpp:224-263`：5 组设备代际（RDNA4+RTV / RDNA3 / NVIDIA / Intel 无运行时 / 无运行时+RDNA4）共 17 条 `CHECK`/`CHECK_FALSE`，**含负向断言**（如 L237 `CHECK_FALSE(Fsr4, RDNA3 caps)`、L253-255 Intel 无运行时三连 FALSE）；调用的是生产函数 `zr::UpscalerBackendSupported`（实现 `upscaler_chain.cpp:43-58`，按 backend 分支真判断），非空跑/永真。另 L477 `CHECK(sizeof(ZrUpscalerMotionVectorConstants)==128u)` 与 L478-479 offsetof 为实测型断言 |
| 5 | MV Pass RG 声明 + 常量两侧一致 | **PASS** | RG 声明：`zrhi_d3d12_window.cpp:5922-5944`，`graph.AddPass("MvPass", …)` 内 `pass.Write(mvH, ColorAttachmentOptimal, ColorAttachmentWrite, ColorAttachmentOutput).MarkRoot()`（MarkRoot 防 RG 外消费者被死剔除），执行体设 `mvPso`/`mvRtv`/960×540 视口并绘地面+cube。常量：CPU 侧 `zupscaler.h:123-128`（`struct ZrUpscalerMotionVectorConstants` 2×float[16] + `static_assert(sizeof(...)==128)` L127）；窗口侧 `zrhi_d3d12_window.cpp:5577-5578`（`using MvPassConstants = zr::ZrUpscalerMotionVectorConstants;` + `static_assert(sizeof(MvPassConstants)==128)`）；GPU 侧 `motion_vectors.hlsl:22-26`（`cbuffer CBufMotionVectors : register(b0)` 内 `row_major float4x4 mvpCur/mvpPrev` = 2×64 = 128B）。三方一致，且单测 L475-480 锁定 |
| 6 | 旧模式回归 | **PASS** | `zrhi_d3d12_window.exe --r5` EXIT=0，`D3D12 R5 OK (ssr=134 gi=23 toon=141)`，`[R5] ssr delta=134 (min 8) gi delta=23 (min 3) toon delta=141 bandFlat=0 (max 5)`、`InfoQueue error count = 0`，旧行为未破坏 |
| 7 | 报告四节齐 + /WX 隔离说明 + 文件规范 | **PASS** | 四节：任务简报（L5）、施工报告（L39）、验收清单（L86）、验收结论（本节，原为空，本次填写）——齐。第三方 /WX 隔离说明在"二"节：ffx-fsr 无编译单元（仅头+预编译 dll，运行时 LoadLibrary），故无需独立第三方 target；AMD 头以 `#pragma warning(push/pop)` 段隔离，实测 `fsr_upscaler.cpp:38-45` 与该说明一致（disable 12 项），且 `CMakeLists.txt:77-80` 有同义注释、L27 全库 `/W4 /WX` 生效（ALL GREEN 即零警告）。文件规范：9 个新/改文件（zupscaler.h/upscaler_chain.cpp/bilinear_upscaler.cpp/fsr_upscaler.cpp/motion_vectors.hlsl/test_upscaler.cpp/SOURCE.md/CMakeLists.txt/本日志）逐字节核验 **UTF-8 无 BOM + LF** 全通过 |

### 总结论

**PASS**（7/7）

### 遗留问题 / 受限项

1. **受限（环境）**：D3D12 调试层未安装，InfoQueue 为降级态（`debug layer UNAVAILABLE (degraded)`），`errors=0` 的强度弱于启用调试层时。属 HANDOFF 已记录的既有环境限制，非本任务缺陷。
2. **受限（简报范围）**：FSR 4.1 枚举真机化、Unity 进程内 dll 搜索路径、`fsr4Hardware` 代际检测均为 R6-02 范围，本任务不作判定。
3. **轻微不符（不影响判定）**：施工报告记"两文件像素差异 39,482 字节"，本次实测 **39,490 B**——差异来自施工报告取证于修复前/上一轮二进制，内容与判定不变（md5 仍不同）。建议后续引用数值以本次为准。
4. **轻微（非阻塞）**：`build_native.ps1` 会向 stderr 打一条 `Error creating directory …\Native\build\cmake_probe_tmp: Permission denied`（cmake 探测回退路径噪音），脚本自身退出码仍为 0、结果 ALL GREEN；建议后续任务顺手清理该噪音，避免 CI 误判。
5. **使用注意（非缺陷）**：`--fsr` / `--r5` 的 mesh 路径 `Native/tests/data/cube.zmesh` 为相对仓库根的路径，须在仓库根执行；在 exe 目录直接运行会 `FAILED to open cube.zmesh` EXIT=1。属既有约定，建议记入 README/工具脚本。

### 验收动作声明

- 未执行 `git commit` / `git add` / 任何改历史操作；仅追加本节内容。
- 未运行 Unity（遵守简报约束）；未下载任何资源。
- 施工与验收为不同代理（独立性满足）。
