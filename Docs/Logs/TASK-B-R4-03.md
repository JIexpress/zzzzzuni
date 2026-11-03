# TASK-B-R4-03 图形 PSO + 首绘：RG 驱动绘制列表真渲染（R4 真正收官）

状态: doing | 派发: 2026-10-24 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：R4 最后一块——真 GPU 绘制：zrhi 增加 GraphicsPSO（根签名/CBV/顶点布局/深度状态）、命令表绘制命令、HLSL 着色器编译（本期运行时 D3DCompile，DXC 升级留注释）；窗口程序用 **RG+draw list 驱动**渲染 cube.zmesh 旋转（Lambert 光照+深度测试）。完成后 R4 里程碑收口。

**设计要点**：
1. `zrhi_device.h` 加法扩展：
   - `ZrShaderDesc{stage: VS|PS, bytecode ptr/size}` / `IZrShader`
   - `ZrGraphicsPsoDesc{vs, ps, ZrFormat rtvFormat, dsvFormat(可选), blend: Opaque, depthTest/write: bool, 顶点布局: Pos3f+Normal3f+Uv2f（固定 semantics 期版）, cullMode}` / `IZrPipelineState`
   - 根签名固定布局 v1：`b0` 常量缓冲（float4x4 mvp + float4 lightDir+lightColor，两个 CBV 槽 `b0/b1` 或合一——实现自定，报告说明）；`IZrCommandList` 增加 `SetPipelineState/SetVertexBuffers(hnd)/SetIndexBuffer(hnd)/SetGraphicsConstantBuffer(slot, ZrBuffer)/DrawIndexedInstanced(indexCount,1,0,0,0)/SetRenderTarget(rt, depth)`/`SetViewportScissor(w,h)`
   - 常量缓冲：`CreateBuffer(hostVisible)` 每帧 Map 写入
2. `zrhi/d3d12/`：Root Signature 1.0 序列化（D3D12SerializeRootSignature）、PSO 创建（D3D12_GRAPHICS_PIPELINE_STATE_DESC）、DSV 堆、视口；**着色器编译**：`Tools/shader_compile.ps1`（d3dcompiler_47 via PowerShell Add-Type 或直接运行时 D3DCompileFromFile——取运行时方案则 zrhi_d3d12_device 内封装 D3DCompile，错误 HRESULT 带信息抛出；**DXC/SM6 升级路径写注释**）。着色器源码 `Native/shaders/first_draw.hlsl`（VS: mvp 变换；PS: Lambert N·L+环境项+uv checker 调试项）
3. 深度：CreateImage(D32Float, DepthStencilAttachment) + DSV 视图；RG 里 Imported 资源（深度+后台缓冲）
4. 窗口程序 `zrhi_d3d12_window.cpp` 升级（模式 `--draw` 或直接改主流程，报告说明）：
   - 加载 `Native/tests/data/cube.zmesh` → CreateBuffer(VB/IB，经 B-R2-03 UPLOAD 链)
   - 每帧：draw list Add(cube 项) → BuildBatches → RG：Import 后台/深度 → ClearPass → **DrawPass（execute 回调里 SetPipelineState/常量/顶点/DrawIndexedInstanced）** → PresentPass
   - mvp 常量随帧旋转（60 帧）；光照固定方向
   - 校验（无 readback 3D 断言能力，用启发式）：①InfoQueue 0 错误 ②**中心区域像素≠清屏色**（readback 1×1 于中心，Cube 占中心——若失败重试 3 帧不同相位）③60 帧无设备移除
   - 通过行：`D3D12 FIRST DRAW OK`
5. mock 单测（接口层）：PSO desc 校验（缺 shader 拒绝等纯逻辑）+命令序列记录（SetPSO→SetVB→DrawIndexed 顺序断言）
6. CMake 挂接；/W4 /WX 零警告

**约束**：只动 `Native/zrhi/`（加法）、`Native/zrender/`、`Native/shaders/`（新）、`Native/tests/`、`Native/CMakeLists.txt`、`Tools/`（若加脚本）、本任务日志；**不跑 Unity 命令**（dll 部署由 build_native.ps1 自动，Unity 侧暂不消费——下任务接）；不 git commit；UTF-8 无 BOM/LF/#pragma once。

**交付物**：PSO/命令/着色器/深度+RG 绘制集成+`D3D12 FIRST DRAW OK` 实证+施工报告四节。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理 | 日期: 2026-10-24

### 一、交付内容

| 文件 | 内容 |
|---|---|
| `Native/zrhi/zrhi_types.h` | +22 行：`ZrShaderStage`（Vertex/Pixel）、`ZrBlendMode`（v1 仅 Opaque）、`ZrCullMode`（None/Back/Front）三枚举（均注释 Vulkan/D3D12 对应） |
| `Native/zrhi/zrhi_device.h` | `ZrShaderDesc` 增补 `stage` 字段；新增 `ZrGraphicsPsoDesc`（vs/ps/rtvFormat/dsvFormat(可选，Count=无深度)/blend/depthTest/depthWrite/cullMode，顶点布局 v1 固定 Pos3f+Normal3f+Uv2f 注释约定）+ `constexpr ZrValidateGraphicsPsoDesc` 纯逻辑校验（:117/:134）；`IZrCommandList` 加法扩展 7 个纯虚：`SetPipelineState`(:377)/`SetVertexBuffers`/`SetIndexBuffer`/`SetGraphicsConstantBuffer`/`DrawIndexedInstanced`(:394)/`SetRenderTarget`/`SetViewportScissor`。**占位 `ZrPipelineStateDesc` 被 `ZrGraphicsPsoDesc` 取代**（R2-01 占位注释本就写明"由管线任务补全"，即本任务；旧结构无任何消费者，见"零破坏"节） |
| `Native/zrhi/d3d12/zrhi_d3d12_translate.h/.cpp` | PSO 纯映射（可单测）：`ZrD3D12_CullMode`(:158)/`ZrD3D12_Blend`(:170)/`ZrD3D12_GraphicsInputLayout`(:182，固定 3 元素 POSITION/NORMAL/TEXCOORD0 + `ZR_D3D12_GRAPHICS_VERTEX_STRIDE=32`)/`ZrD3D12_GraphicsPsoDesc`(:201，desc→`D3D12_GRAPHICS_PIPELINE_STATE_DESC` 全字段：根签名指针/输入布局/光栅/混合/深度(LESS+写掩码)/RTV·DSV 格式) |
| `Native/zrhi/d3d12/zrhi_d3d12_device.h/.cpp` | `ZrD3D12ShaderObj`（DXBC 持有）/`ZrD3D12PsoObj`（rootSignature+pipelineState）载荷 + 句柄表 + `ResolveShader/ResolvePipelineState`；`CreateShader`(:671)/`CreatePipelineState`(:684，入口跑 `ZrValidateGraphicsPsoDesc` 拒绝非法描述)/`CompileHlsl`(:720，运行时 `D3DCompile`，失败把 HRESULT+编译器错误文本（带行号）打印到 stderr 后返回无效句柄)/`Destroy(ZrShader|ZrPipelineState)`；原生扩展点 `D3D12ViewCpuOf`（验证程序深度清屏用，先例 `D3D12ResourceOf`） |
| `Native/zrhi/d3d12/zrhi_d3d12_commandlist.h/.cpp` | 7 个绘制命令实现：`SetPipelineState`(:249，SetGraphicsRootSignature+SetPipelineState+IASetPrimitiveTopology(TRIANGLELIST))、`SetVertexBuffers`(:263，stride=32 固定布局)、`SetIndexBuffer`(:285，R32_UINT)、`SetGraphicsConstantBuffer`(:300，`SetGraphicsRootConstantBufferView` 直绑 GPU VA)、`DrawIndexedInstanced`(:313，参数一一对应)、`SetRenderTarget`(:323，OMSetRenderTargets，DSV 可选)、`SetViewportScissor`(:344) |
| `Native/shaders/first_draw.hlsl`（新） | VS：`row_major float4x4 mvp` 变换；PS：Lambert N·L + 环境项 0.25 + uv 8x8 checker 调试项。CBufPerDraw（b0，96B：mvp+lightDir+lightColor）。**DXC/SM6 升级路径写于文件头与 CompileHlsl 注释**：dxc.exe /T vs_6_0|ps_6_0（-spirv 出 Vulkan 轨 SPIR-V），`ZrShaderDesc{code,codeSize}` 字节码契约不变，消费方零改动 |
| `Native/tests/test_zrhi_pso_draw.cpp`（新） | 5 用例：PSO desc 校验（缺 shader/非法格式/深度矛盾，:37/:54/:75）、MockDevice 契约对齐（:105）、**命令序列 SetPSO→SetVB→SetIB→SetCB→DrawIndexed 顺序断言 + 结构化参数核对**（:130，关键断言 `CHECK(iSetPso < iSetVb)` :186、`CHECK(iSetCb < iDraw)` :189） |
| `Native/tests/rg_mock_device.h` | MockCommandList 补齐 7 命令记录（calls 序列 + 结构化 drawCalls/vertexBufferCalls/constantBufferCalls）；MockDevice `CreateShader`（空 code 拒绝）/`CreatePipelineState`（`ZrValidateGraphicsPsoDesc` 拒绝，契约对齐真后端） |
| `Native/tests/zrhi_d3d12_window.cpp` | 新增 `--draw [zmeshPath] [hlslPath]` 模式（默认 `Native/tests/data/cube.zmesh` + `Native/shaders/first_draw.hlsl`），旧三模式（WINDOW/RESOURCE/RG SMOKE）原样保留（本任务回归证据） |
| `Native/CMakeLists.txt` | zrhi 链接 `d3dcompiler`；`zrender_test` 挂 `test_zrhi_pso_draw.cpp` |

### 二、关键设计决策（简报允许自定项）

1. **根签名布局（简报"两个 CBV 槽 b0/b1 或合一——实现自定"）**：**合一单 CBV b0**。`D3D12SerializeRootSignature` 1.0（`CreateFirstDrawRootSignature`，zrhi_d3d12_device.cpp:638）：1 个根参数 = 根 CBV b0（ShaderVisibility=ALL，VS 用 mvp、PS 用光照，同一常量缓冲），Flags=`ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT`（PSO 带输入布局时必需）。根描述符直绑 GPU VA（`SetGraphicsRootConstantBufferView`），不经描述符堆。与 first_draw.hlsl 的 CBufPerDraw（row_major mvp+lightDir+lightColor=96B）逐字段对应，行号如上。
2. **常量缓冲**：每绘制项一个 256B hostVisible（UPLOAD）缓冲，创建时持久 Map，每帧 memcpy 写入（mvp 随帧旋转 60 帧一圈，光照固定 (0.5,0.8,0.6) 归一化）。VB/IB 走 `CreateBuffer(initData)` 的 B-R2-03 UPLOAD 中转上传链。
3. **窗口程序取"加 `--draw` 模式"**（简报二选一）：旧模式输出是 R2/R3 的回归证据，保留完整；`--draw` 专注首绘。窗口创建失败自动降级离屏目标（RGBA8Unorm），RG 路径两模式同构。
4. **深度资源**：`CreateImage(D32Float, DepthStencilAttachment)` + `CreateImageView(Dsv)`（B-R2-03 已有 DSV 堆路径）+ RG `Import`（与后台缓冲一样均为 Imported，非 Transient）。深度清屏用原生扩展点 `ClearDepthStencilView`（RHI 尚无 ClearDepth 命令，先例=既有 CopyTextureRegion 插桩），放在 ClearPass 内（该 Pass 声明了 depth 写 → 此刻深度已处 DEPTH_WRITE 态，合法）。每帧末手动屏障 深度 DEPTH_WRITE→COMMON，使下一帧 RG 的"起点 Undefined/COMMON"假设与设备真实状态/状态表三方一致（PRESENT==COMMON==0 为 D3D12 自身设计）。
5. **draw list 消费**（验收第 5 条）：DrawPass execute 回调遍历 `drawList.Batches()`（:945），批内逐项 `itemIndex = batch.startIndex + i`（:947）→ `SetGraphicsConstantBuffer(0, cbHandles[itemIndex])`(:948) → `DrawIndexedInstanced(36,1,0,0,0)`(:949)。3 个 cube 项（不同横向位置）聚成 1 批（shader/material/mesh 全同），深度测试开启。
6. **`IZrShader` 落地形态说明**：zrhi 全线是句柄+返回码风格（`ZrShader` 句柄 + `ZrHandleTable`），着色器无需接口类，故 `IZrShader` 以 `ZrShader` 句柄 + `ZrD3D12ShaderObj` 载荷实现（同 `IZrBuffer` 之外的所有资源形态）；`IZrPipelineState` 同理。
7. **剔除取 `ZrCullMode::None`**（实证发现）：Unity LH 烘焙的 cube 在本 LH 投影下屏幕绕序呈 CCW，D3D12 默认正面判定（CW）+ Back 剔除会把 cube 全部剔除（中心像素恒为清屏色）；改 None 后首绘可见。绕序与资产管线的统一约定留给后续任务（translate 的 `FrontCounterClockwise=FALSE` 为 D3D12 标准缺省）。

### 三、自测证据

1. **build_native.ps1 全绿**（配置+构建+测试，/W4 /WX 零警告）：`ALL GREEN: configure + build + test succeeded`；**89 用例 307763 断言全过**（84 回归 + 5 新增，全为 doctest CHECK/REQUIRE 实断言）。
2. **亲跑 --draw（Git Bash，仓库根）**：exit 0，关键输出：
   ```
   zrhi_d3d12_window: --draw zmesh: vertices=24 indices=36 (interleaved 32B/vert)
   zrhi_d3d12_window: --draw shaders compiled (runtime D3DCompile, SM5.0 DXBC)
   zrhi_d3d12_window: --draw draw list: items=3 batches=1 (state-clustered)
     frame 10 readback center RGB=(151,146,141), clear≈(13,26,38) -> CUBE (first draw visible)
     frame 20 readback center RGB=(27,60,141),  clear≈(13,26,38) -> CUBE (first draw visible)
     frame 30 readback center RGB=(10,22,54),   clear≈(13,26,38) -> CUBE (first draw visible)
     frame 40 readback center RGB=(54,54,54),   clear≈(13,26,38) -> CUBE (first draw visible)
     frame 60 readback center RGB=(30,68,159),  clear≈(13,26,38) -> CUBE (first draw visible)
   zrhi_d3d12_window: [FIRST DRAW] frames=60 center checks=6 passed=6 undeclared uses=0
   zrhi_d3d12_window: [FIRST DRAW] device removed reason = 0x00000000 (S_OK)
   zrhi_d3d12_window: InfoQueue error count = 0
   D3D12 FIRST DRAW OK
   ```
   中心 1x1 像素 6/6 非清屏色且逐帧颜色随旋转/光照变化（Lambert+checker 生效）；60 帧完成、无设备移除、InfoQueue 0 错误。**本机调试层未装（图形工具可选功能），InfoQueue 计数为降级 0**（程序打印降级说明，与 HANDOFF 注意事项一致）。
3. **着色器编译错误路径真实报错**（验收第 4 条，已亲试）：坏 hlsl（未声明标识符）→ stderr：
   ```
   [zrhi][d3d12] D3DCompile FAILED (hr=0x80004005, profile=vs_5_0, entry=VsMain)
   [zrhi][d3d12] compiler output:
   Shader@0x... (2,38-50): error X3004: undeclared identifier 'broken_syntax'
   ```
   程序打印 `--draw shader compilation FAILED (see stderr)` 并 exit 1。
4. **旧接口零破坏**：`zrhi_d3d12_window.exe`（默认模式）exit 0：`D3D12 WINDOW SMOKE OK` + `D3D12 RESOURCE SMOKE OK` + `D3D12 RG SMOKE OK`；`--zmeshcheck` exit 0。
5. **调试过程中的两个真实修复**（供验收代理重点复查）：①`D3D12_BOX` 字段序是 (left,top,**front**,right,bottom,**back**)，1x1 中心回读初版写错序导致设备移除（zrhi_d3d12_window.cpp readback 处已修正+注释）；②Imported 资源的 `RgResourceDesc::ImportImage` 不携带 usage → RG 屏障派生把后台缓冲 Write 推导为 General(UAV 态)，对交换链缓冲非法 → 设备移除；改用**显式同步三元组**声明（与 R3-02 legacy FinalPass 同模式），ClearPass/CubeDrawPass 的 Write 均已显式。

### 四、约束自查

- 只动 `Native/zrhi/`（接口加法+后端）、`Native/zrender/`（未动——本任务无需）、`Native/shaders/`（新）、`Native/tests/`、`Native/CMakeLists.txt`、本任务日志；未跑任何 Unity 命令；未 git commit。
- 文件规范：UTF-8 无 BOM（新文件首字节 `2f 2f 20` 实测）；.gitattributes `eol: lf` 强制（工作区 CRLF 由 `core.autocrlf=true` 管理，提交即 LF，与全仓现状一致）；头文件 `#pragma once`（zrhi_types.h/translate.h/rg_mock_device.h 实测各 1）。
- 遗留说明：`TempLogs/` 下调试脚本已删；验证程序中无残留诊断开关（已全部清理）。

## 验收清单（主控写）

1. build_native.ps1 全绿（既有 84 用例回归+新增）
2. 亲跑窗口程序：`D3D12 FIRST DRAW OK`、中心像素非清屏色证据行、60 帧完成、InfoQueue 计数
3. 代码审查：PSO desc→D3D12 映射在 translate（行号）；根签名布局与实现一致（行号）；DrawIndexed 命令流顺序（mock 单测断言行号）
4. 着色器编译错误路径有真实报错（临时改坏 hlsl 跑一次验证报错信息——验收代理可做，或审查 HRESULT 处理行号）
5. RG DrawPass 回调真实消费 draw list 批（行号：批→DrawIndexed 映射）
6. zrhi 旧接口零破坏（回归全过）
7. 文件规范+施工报告完整

## 验收结论（验收代理写）

**验收**: general-purpose 验收子代理 | 日期: 2026-10-24 | 结论: **PASS**（7/7 项通过，独立复跑，未跑 Unity，未改任何代码）

1. build_native.ps1 全绿：亲跑输出 `ALL GREEN: configure + build + test succeeded`，doctest `test cases: 89 | 89 passed`、`assertions: 307763 | 307763 passed`（84 回归+5 新增核实），/W4 /WX 下日志零编译警告。
2. 亲跑 `Native/build/bin/Release/zrhi_d3d12_window.exe --draw`（仓库根）：exit 0，末行 `D3D12 FIRST DRAW OK`；中心 1x1 回读 6/6 非清屏色（frame 10/20/30/40/50/60，RGB 如 (151,146,141)、(30,68,159) 等，均≠clear≈(13,26,38) 且逐帧随旋转变化）；`frames=60`、`device removed reason = 0x00000000 (S_OK)`；`InfoQueue error count = 0`（本机调试层未装，程序打印降级说明）。
3. 代码审查通过：PSO desc→D3D12 全字段映射在 translate 纯函数 `ZrD3D12_GraphicsPsoDesc`（zrhi_d3d12_translate.cpp:201-269，光栅:218-228/混合:231-243/深度 LESS:248-252/RTV·DSV 格式:258-262），输入布局 `ZrD3D12_GraphicsInputLayout`(:182-199，POSITION/NORMAL/TEXCOORD0 stride 32)；根签名单 CBV b0（zrhi_d3d12_device.cpp:638-669，`D3D12_ROOT_PARAMETER_TYPE_CBV` b0/Visibility ALL/ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT）与 first_draw.hlsl:23-28 `cbuffer CBufPerDraw : register(b0)`（row_major mvp+lightDir+lightColor=96B）逐字段一致；`ZrGraphicsPsoDesc` 接口注释（zrhi_device.h:113-116）同声明此布局。
4. mock 单测命令序列断言真实存在：Native/tests/test_zrhi_pso_draw.cpp:130-224，顺序断言 `CHECK(iSetPso < iSetVb)`(:186)、`CHECK(iSetCb < iDraw)`(:189)、全序(:190)，结构化参数核对(:193-208)；PSO 校验用例 :37/:54/:75。
5. RG DrawPass 真消费批：zrhi_d3d12_window.cpp:945-951，`drawList.Batches()` → `batch.startIndex + i`(:947) → `SetGraphicsConstantBuffer(0, cbHandles[itemIndex])`(:948) → `DrawIndexedInstanced(indexCount,1,0,0,0)`(:949)；坏 hlsl 实测（验收自造临时文件经 `--draw <zmesh> <hlsl>` 传入）：exit 1，stderr 输出 `D3DCompile FAILED (hr=0x80004005, profile=vs_5_0…)` + 编译器错误文本含行列号（`error X3004: undeclared identifier`），HRESULT 处理在 zrhi_d3d12_device.cpp:743-753。
6. 旧接口零破坏：89 用例含 84 回归全过；默认模式亲跑 exit 0（WINDOW/RESOURCE/RG 三 SMOKE OK）+ `--zmeshcheck Native/tests/data/cube.zmesh` exit 0；git diff 抽查 Native/zrhi/ 全部为加法，唯一删改是占位 `ZrPipelineStateDesc`→`ZrGraphicsPsoDesc` 取代与占位 stub 实现落地，全仓 grep 确认 `ZrPipelineStateDesc` 仅存于一处注释（zrhi_device.h:109），零消费者，与施工报告"零破坏"节一致。
7. 文件规范+报告：抽查 6 文件首字节均 `2f 2f 20`（UTF-8 无 BOM）；3 个头文件 `#pragma once` 各 1；.gitattributes `eol: lf` 强制（worktree CRLF 为 core.autocrlf=true 全仓现状，提交即 LF）；施工报告四节齐全，两个调试修复（D3D12_BOX 字段序 zrhi_d3d12_window.cpp:965-966 注释在案；Imported 显式同步三元组 :909-916）与 CCW 绕序发现（报告 二.7，translate:220 `FrontCounterClockwise=FALSE` + `ZrCullMode::None`）均记录完整，验收逐条复核属实。

备注：git 工作区中 TASK-A-M1-03.md 与 UnityProject 部分改动属并行任务 A-M1-03（commit 4c5aa0f 并行派发），不在本任务验收范围。未 git commit，验收后状态交主控裁决。
