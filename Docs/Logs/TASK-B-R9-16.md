# TASK-B-R9-16 —— GPU 工具链（方案 §5.7）：DrawCall/显存 HUD + RenderDoc 一键捕获 + RG 导出

状态: doing | 派发: 2026-11-04 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

### 背景

需求达成对照（Docs/需求达成对照.md）第二区确认缺口：方案 §5.7 承诺的 GPU 工具链未建——且已被两次验收实际感知（B-R9-07 "描述符级证明需 RenderDoc 之类外部工具"、R9-10 的 VK 离屏排查全程盲飞）。

**目标（三件，全部走已有 DevServer/调试面板基建）**：
1. **DrawCall/显存 HUD**：调试面板（A-M0-05 的 DebugPanelRoot）新增渲染统计页——每帧 DrawCall 数（从命令表计数器——B-R2-02 起窗口程序有 DrawIndexed 计数，**经 DevConsole 暴露**）、RT 分辨率、显存（已建资源字节统计 zrhi_device——GetDeviceStats 扩展 or 现有 stats 汇总）
2. **RenderDoc 一键捕获**：DevConsole 新增白名单命令 `renderdoc.capture`——通过 RenderDoc in-application API（`renderdoc_app.h` 通用头，公开单头文件）在目标帧触发捕获；不引第三方运行时（RenderDoc 本体仍是用户手动开），产出 .rdc 到固定目录+返回路径。若 RenderDoc API 不可用（未挂接）→ 明确返回错误
3. **RG 导出**：RenderGraph 编译产物导出为文本/JSON（pass 列表+依赖边+屏障批次+别名计划——B-R3-01 数据都在）经 DevConsole 命令 `rg.export` 写盘；给排查"哪个 pass 黑屏/性能"提供数据

### 约束与要求

- 单测：JSON/文本导出格式纯函数、HUD 统计聚合纯函数、renderdoc 命令的参数与错误路径
- 变异验证 ≥1 条（如 HUD 统计断言改错即红）
- 自测：build 全绿（基线 281/771,162 只增不减）+ `--lights`/`--assets` 回归 + DevConsole 命令实测（`--devconsole` 若需开 flag 沿用 A-DEV-03 机制）
- **不碰 Unity**（Unity 侧 HUD 由 A 轨后续任务经 DevConsole 读数，本任务只做原生暴露）

**约束**：只动 `Native/zrender/`、`Native/zfgprobe/`（若 renderdoc 挂接在窗口程序层则不动）、`Native/tests/`、`Native/CMakeLists.txt`、`Native/ThirdParty/renderdoc/`（单头文件+SOURCE.md）、本任务日志；不 git commit；/W4 /WX；UTF-8 无 BOM/LF。

## 施工报告（施工代理写）

- **日期**：2026-11-04
- **结论**：三件交付全部落地（HUD 统计 / `renderdoc.capture` / `rg.export`）+ 原生 `--devconsole` 通道，
  原生单测 **281 → 301 用例 / 771,162 → 771,417 断言**（+20/+255，只增不减）全绿；`--lights` / `--assets` 回归通过；
  DevConsole 六类命令实测（含 4 类错误路径）+ 长行/违约客户端存活实测；变异验证 1 条（恰 1 用例 2 断言红，复原转绿）。
- **落点**：
  - `Native/zrender/rg_export.h/.cpp`（新）——RG 编译产物导出纯函数（JSON/文本）
  - `Native/zrender/gpu_hud.h/.cpp`（新）——HUD 统计聚合纯函数（字节估算 + 滑窗聚合 + 两种格式化）
  - `zrender/devconsole_proto.h/.cpp`（新）——DevConsole 协议纯逻辑（白名单解析/信封/对象并包）
  - `zrender/renderdoc_capture.h/.cpp`（新）——RenderDoc 应用内 API 封装（挂接/捕获/错误码）
  - `zrender/rg_render_graph.h/.cpp`（改）——**加法** 4 个 const 查询接口（导出数据源，详见下）
  - `Native/ThirdParty/renderdoc/renderdoc_app.h` + `SOURCE.md`（新）——官方单头 v1.37
  - `Native/tests/test_rg_export.cpp / test_gpu_hud.cpp / test_renderdoc_capture.cpp / test_devconsole_proto.cpp`（新，20 用例）
  - `Native/tests/zrhi_d3d12_window.cpp`（改）——`--devconsole` 模式（TCP 白名单控制台 + 三命令接线）
  - `Native/CMakeLists.txt`（改）——4 个 zrender_core 源 + 4 个测试源 + ws2_32 + ThirdParty include
  - `UnityProject/Assets/Plugins/ZRender/zrender.dll`（M）——build_native.ps1 第 4 步自动部署（ADR-004 构建产物入库）

### 0. 架构：为什么分四层

```
┌─ Native/tests/zrhi_d3d12_window.cpp --devconsole ─────────────────────────┐
│  TCP 传输（127.0.0.1:17323，监听线程只收发）+ 帧循环内执行 + 响应回写        │
├─ zrender/devconsole_proto.*（纯逻辑，可单测）─────────────────────────────┤
│  白名单解析 / 参数个数 / 错误码 / ok-error 信封 / 对象并包                  │
├─ zrender/gpu_hud.* + rg_export.* + renderdoc_capture.*（纯函数/封装）──────┤
│  统计聚合与格式化 ｜ 编译产物→JSON/文本 ｜ RenderDoc 挂接与捕获             │
├─ zrhi（零改动）＋ rg_render_graph（零语义改动，只加 const 查询）────────────┤
└───────────────────────────────────────────────────────────────────────────┘
```

简报约束"不动 zrhi" ⇒ `ZrDeviceStats` 无法加字节字段 ⇒ 显存走**调用方台账 + 纯函数估算**（见决策 ①）。
DevConsole 沿用 A-DEV-03 机制：一行请求/一行 JSON、单行 4KB 上限、硬编码白名单、后台线程只收发、
命令在"主线程"（此处为渲染帧循环）执行、未知命令结构化报错。

### 1. 交付①：DrawCall/显存 HUD（`hud` 命令 + 文本格式）

- **DrawCall 计数器**：`--devconsole` 帧循环的 `CubeDrawPass` execute 回调内逐次 `DrawIndexedInstanced`
  计数（zrhi_d3d12_window.cpp:13667 `frameDrawCalls += 1u`，与 B-R2-02/--parity 的插桩同口径——真实调用点
  计数，非估算）。本场景 3 cube ⇒ 每帧 3 次，hud 实测返回 `"drawCalls":3,"drawCallsPeak":3`。
- **RT 分辨率**：640×480（交换链/离屏目标尺寸）。
- **显存字节**：本模式自建资源台账（交换链 3 缓冲 + 深度 + VB/IB + 3×256B 常量缓冲）逐项经
  `ZrEstimateImageBytes`（按格式 traits 的 bytesPerBlock/block 逐 mip 链求和 ×arrayLayers）/`ZrEstimateBufferBytes`
  估算 ⇒ 实测 `memoryBytes: 4,916,880`（手工核算一致：3×1,228,800 + 1,228,800 + 768(VB) + 144(IB) + 768(CB)）。
  **口径=估算**（不含后端对齐填充/交换链隐式缓冲），用途是趋势与量级；设备全量字节需 GetDeviceStats 扩展（决策①）。
- **设备统计透传**：`imageCount/bufferCount/aliasHeapCount/presentFailureCount/deviceRemoved` 来自
  `ZrDeviceStats`（含 H-02/H-05/R10-05 全部字段）。
- **聚合纯函数**：`ZrHudAggregator`（60 帧环形窗口：最新值 + 窗口峰值 + 平均帧耗 + fps=1000/均值，空窗口
  快照全零不臆造）；`ZrHudFormatJson`（单行 JSON，%.4f 定点确定性）/`ZrHudFormatText`（单行文本）。
  单测 test_gpu_hud.cpp 5 用例（估算公式手工核算 / 峰值≠末样本 / 环形覆盖 / 设备透传 / 格式确定性）。
- **Unity 侧 HUD**：按简报不在本任务（A 轨后续经 DevConsole 读本命令的 JSON）。

### 2. 交付②：RenderDoc 一键捕获（`renderdoc.capture` 命令）

**挂接机制（renderdoc_app.h 官方约定，全部在 zrender/renderdoc_capture.cpp）**：
1. RenderDoc UI 以 "Launch Application" 启动目标进程时把 `renderdoc.dll` 注入进进程；
2. 应用侧 `GetModuleHandleA("renderdoc.dll")` 探测（**:71**）→ 无模块 ⇒ `ModuleNotFound`（明确错误，不假成功）；
3. `GetProcAddress(module, "RENDERDOC_GetAPI")`（**:76**）取入口 ⇒ 缺导出 ⇒ `ProcNotFound`；
4. `GetAPI(eRENDERDOC_API_Version_1_6_0)`（**:50**，该单头支持的最高 v1.x）⇒ 空表 ⇒ `ApiRejected`；
5. 拿到 `RENDERDOC_API_1_6_0` 函数表后：`SetCaptureFilePathTemplate`（**:104**，.rdc 落盘路径模板，RenderDoc
   自动追加 `_<帧号>.rdc`）+ `TriggerCapture`（**:115**，present 边界落盘）+ `GetNumCaptures/GetCapture`
   （**:147/:157**，落盘后取真实 .rdc 绝对路径）；离屏（无 present）用 `StartFrameCapture/EndFrameCapture`
   手动半边（zrhi_d3d12_window.cpp:13525/:13731）。
6. **不引第三方运行时**：RenderDoc 本体（UI/replay）仍是用户手动启动；本程序只探测、不注入。

**窗口程序接线**（zrhi_d3d12_window.cpp `--devconsole` 模式）：
- 启动即 `Attach()`（**:13416**）并打印状态；未挂接时命令返回
  `{"ok":false,"error":"renderdoc_not_attached","detail":"start this exe via RenderDoc UI (File > Launch Application) - ..."}`（**:13509**）——**简报硬要求"未挂接时明确报错"的实证见自测§4**；
- 挂接时：可选 `[name]` 参数即时换模板（`<dir>/<name>`）→ `TriggerCapture`（窗口模式）/Start-End（离屏）→
  帧 end 轮询 `GetNumCaptures`，落盘即打印真实路径（**:13752**）并记入后续响应的 `lastCapturePath`；
- 落盘目录固定：`Native/build/renderdoc/`（构建产物区，不入库；`--devconsole-dir` 可覆盖）。

**测试接缝**：`AttachWithGetApi(getApi)`（公共头不泄漏 RenderDoc 符号，头文件只有弱类型函数指针签名）——
单测用假 API 表覆盖挂接成功路径的每一次转发（test_renderdoc_capture.cpp 4 用例：未挂接错误路径 / GetApi
拒绝 / 假表转发+回读+EndFrame 失败=CaptureFailed / 生产入口 ModuleNotFound）。

### 3. 交付③：RG 导出（`rg.export [path] [json|text]` 命令）

- **数据源（零新增状态）**：`RgGraph` 加 4 个 const 查询（rg_render_graph.h:316-333）：`PassNameAt` /
  `PassAccessesAt` / `ResourceDescAt`+`ResourceHandleAt` / `Edges()`——B-R3-01 既有编译产物
  （CompiledOrder/CulledPasses/AliasPlan/BarrierPlan/LifetimeOf）原样可查，语义零改动。
- **导出内容**（rg_export.cpp，两函数皆纯函数——同输入逐字节一致）：
  - JSON 单文档：`rgExportVersion/error/passCount/alivePasses/culledPasses` + `resources`（含图像尺寸/格式、
    缓冲字节）+ `passes`（执行序+decl+访问集）+ `culled` + `edges`（decl→decl 依赖边）+ `aliasPlan`
    （slotCount+区间条目）+ `barriers`（逐 pass 批次含 layout/access/stage 名字化 + split 镜像位 +
    splitList）+ `lifetimes`（甘特区间）；
  - 文本：人类可读同源视图（资源带 slot/lifetime 区间、pass 读写集、逐 pass 屏障批次、边表、split 计划）。
- **命令语义**：参数 `[path] [json|text]`（json 缺省）；格式值域错误 ⇒ `bad_format`；写盘失败 ⇒
  `write_failed`；当帧 RG 编译失败 ⇒ `rg_compile_failed`（请求不静默丢）；响应带
  `path/format/bytes/frame/passes/barriers`。缺省路径 `Native/build/renderdoc/rg_export.json`。
- **实测**：导出文件经 Python `json.loads` 严格解析通过，passes=[ClearPass,CubeDrawPass,PresentPass]、
  逐 pass 屏障批次 [2,2,1] 总 5、边 [0→2,0→1,1→2]、别名槽 0（本图无 Transient）。
- 单测 test_rg_export.cpp 5 用例（JSON 全字段 / culled / 文本视图 / 确定性+名表 / 编译失败骨架）+
  **形状护栏**（首尾括号配对、花括号计数相等、无 `"Image,` 漏引号形态——开发期实测抓到两处拼接 bug 后补的牙）。

### 4. 自测证据

1. **build**：`Tools/build_native.ps1` ALL GREEN——configure + /W4 /WX 零警告 + doctest
   **301 用例 / 771,417 断言全过**（基线 281/771,162，+20/+255 只增不减；Vulkan SDK 本机在位，含 6 个
   Vulkan 测试文件）。zrender.dll 已随脚本部署 Unity 插件目录。
2. **回归**：`--lights` → `D3D12 LIGHTS OK`（centerPass=6 groundLitPass=6 anim=OK InfoQueue=0，exit 0）；
   `--assets` → `D3D12 ASSETS GALLERY OK (models=4)`（probePasses 6/6×4，exit 0）。既有模式零破坏。
3. **DevConsole 实测**（真实 TCP 客户端，端口 17323；本机窗口模式 vsync≈90fps）：
   - `help` → `{"ok":true,"commands":["help","hud","rg.export [path] [json|text]","renderdoc.capture [name]","quit"]}`
   - `hud` → `{"ok":true,"drawCalls":3,"drawCallsPeak":3,"rtWidth":640,"rtHeight":480,"memoryBytes":4916880,"imageCount":4,"bufferCount":5,"aliasHeapCount":0,"presentFailureCount":0,"deviceRemoved":false,"frameMs":11.0914,"fps":90.1599,"frames":60}`
   - `rg.export` → `{"ok":true,"path":"Native/build/renderdoc/rg_export.json","format":"json","bytes":2183,"frame":433,"passes":3,"barriers":5}`（文件严格解析通过）
   - `rg.export <txt> text` → 同构成功；`rg.export x:\no\...` → `{"ok":false,"error":"write_failed",...}`
   - `renderdoc.capture`（两次，含带 name 参数）→ `{"ok":false,"error":"renderdoc_not_attached","detail":"start this exe via RenderDoc UI (File > Launch Application) - renderdoc.dll not loaded in this process (launch via RenderDoc UI)"}`
   - `bogus` → `unknown_command`；`hud extra` / `rg.export a b c` → `bad_arguments`+usage；空行 → `empty_line`；
     `rg.export out.xml` 格式值域 → `bad_format`
   - `quit` → `{"ok":true}` 且进程优雅退出（日志 `--devconsole done: frames=463 exports=2`）
   - 加固项：`--devconsole-port 17400` 覆盖生效；>4KB 长行 → `{"ok":false,"error":"line_too_long",...}`；
     客户端发半行即断开（违约）后服务端存活且继续应答。
4. **变异验证（V4，实施层面变异而非改断言——更强形态）**：把 `ZrHudAggregator::Snapshot` 的峰值口径
   `if (drawCalls > peak)` 改成无条件赋值（= 取末样本）→ **恰 1 用例红**（aggregator 用例，2 断言红：
   `CHECK(4 == 6)` / `CHECK(90 == 106)`），其余 300 用例不受影响；复原后 301/771,417 全绿。
   ⚠️ 附带自曝一处"假牙"修正：初版聚合用例压入 2/4/6（峰值=末样本，变异下不红）——按 HANDOFF 里 A-4
   的教训先加固断言（峰值≠末样本两组）再做变异，确保牙是真的。
5. **文件规范**：全部新/改文件 UTF-8 无 BOM、LF、末尾换行（python 字节级审计 CRLF=0 bareCR=0 BOM=False）；
   新头文件全带 `#pragma once`；`/W4 /WX` 零警告（WX 强制）。
6. **git status 范围**：仅本任务允许清单（Native/zrender、Native/tests、Native/ThirdParty/renderdoc、
   Native/CMakeLists.txt、本日志）+ zrender.dll（脚本自动部署）。**工作区另有 Unity 侧 M/?? 文件
   （GameBootstrap.cs、Interaction/*、Commands/ 等）——属并行派发的 A-R9-12 施工现场，本任务未触碰。**

### 5. 决策与偏差（验收代理请重点核对）

| # | 决策 | 理由 |
|---|---|---|
| ① | 显存=调用方台账 + `ZrEstimate*` 纯函数估算，而非扩 `ZrDeviceStats` | 简报给的两个选项中"GetDeviceStats 扩展"须改 `Native/zrhi/`（本任务禁改清单）；"现有 stats 汇总"只有计数无字节。估算口径在 gpu_hud.h 明注（不含对齐填充，趋势/量级用途） |
| ② | DevConsole 端口缺省 **17323** | 17321=Editor DevServer、17322=Unity 运行期 DevConsole（A-DEV-03），顺延；`--devconsole-port` 可覆盖 |
| ③ | `--devconsole` 为独立模式（D3D12，--draw 同源 PSO/资源链），不嵌进既有 60 帧模式 | 既有模式帧数固定会退出，控制台需长驻；独立模式零改动既有路径（--lights/--assets 回归佐证） |
| ④ | `hud` 响应经 `ZrDevConsoleMergeObject` 把 `ZrHudFormatJson` 单对象剥壳并入信封 | 首版实现产出 `{"ok":true,{...}}`（非法 JSON）被实测抓出；现响应为 `{"ok":true,"drawCalls":...}` 合法单行，且格式化函数保持独立可用 |
| ⑤ | renderdoc 挂接测试用假 API 表（`AttachWithGetApi` 接缝），不在 CI 装 RenderDoc | RenderDoc 本体 466MB+ 且需 UI 交互启动；接缝让成功路径的每次转发都有单测（未挂接错误路径另有真进程实测） |
| ⑥ | 简报"对照 --lights 已知 DrawCall=16 口径"——**实测口径澄清**：--lights 每帧真实 DrawIndexedInstanced = **2**（地面 quad 6 索引 + cube，zrhi_d3d12_window.cpp:1829/1835），"16"在仓库与日志中查无出处（疑为 9 光源/parity 场景的记忆混淆）。本任务 hud 返回的是插桩计数器真值：console 模式每帧 3（3 cube）。验收时请按"插桩点数一数一对"口径核对 | 不复核已知数字、只保证计数器真值 |

### 6. 未知与受限（不要当成已验证）

1. **真 RenderDoc 端到端未跑**：本机未安装 RenderDoc（不装 >2GB 软件属纪律）。挂接成功路径的证据 =
   假表单测（转发逐条断言，test_renderdoc_capture.cpp）+ 挂接链路代码审查（§2 行号）+ 未挂接错误路径
   真进程实测。真 .rdc 落盘验证待装 RenderDoc 后一条 `renderdoc.capture` 即可补。
2. **离屏（headless）的 RenderDoc 手动 Start/End 半边**只有单测与代码路径，无真 RenderDoc 实测。
3. **窗口关闭（WM_QUIT）优雅退出**已实现（本地消息泵），未人工点 X 实测（quit 命令路径已实测）。
4. HUD 文本/JSON 目前只在 DevConsole 暴露；Unity 调试面板页（A-M0-05）按简报归 A 轨后续任务。
5. `--devconsole` 模式创建真窗口（有 vsync）；无 `--headless` 变体（离屏降级仅窗口创建失败时触发）——
   控制台场景窗口模式即目标形态，未做无头参数。

## 验收清单（主控写）

- [ ] V1 build 全绿（用例数只增不减，数字写入结论）
- [ ] V2 亲跑：HUD 统计命令返回 DrawCall/显存数字（对照 --lights 已知 DrawCall=16 口径）；rg.export 产出文件（内容抽查 pass 名与屏障批次）
- [ ] V3 renderdoc.capture：未挂接时明确报错（证据）；挂接路径代码审查（行号）
- [ ] V4 变异复做 1 次（HUD 断言改错即红）
- [ ] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

- **日期**：2026-11-04
- **结论**：**PASS**（V1–V5 全部亲证通过；1 处报告表述瑕疵记录在案，不阻断；未跑 Unity、未 git commit、变异已复原）

| 项 | 判定 | 证据（验收代理亲证） |
|---|---|---|
| V1 build 全绿 | PASS | 亲跑 `Tools/build_native.ps1`：exit 0，`ALL GREEN: configure + build + test succeeded`；doctest **301/301 用例、771,417/771,417 断言、Status: SUCCESS**（与声明 301/771,417 一致，基线 281/771,162 只增不减）；/W4 /WX 全局生效（Native/CMakeLists.txt:27），零警告过 WX |
| V2 DevConsole 实测 | PASS | 亲起 `--devconsole` 进程 + 真实 TCP 客户端（协议=一连接一命令）逐条实测：`hud`→`{"ok":true,"drawCalls":3,"drawCallsPeak":3,"rtWidth":640,"rtHeight":480,"memoryBytes":4916880,"imageCount":4,"bufferCount":5,"aliasHeapCount":0,"presentFailureCount":0,"deviceRemoved":false,"frameMs":11.09,"fps":90.17}`（插桩点 zrhi_d3d12_window.cpp:13667 亲核；memoryBytes=4,916,880 与手工核算 3×1,228,800+1,228,800+768+144+768 一致）；`rg.export`→ok，2183 字节文件经 Python `json.loads` 严格解析通过，pass 名 **[ClearPass, CubeDrawPass, PresentPass]**、逐 pass 屏障 **[2,2,1] 总 5**、边 **[0→2,0→1,1→2]**、别名槽 0——与 §3 声明逐字一致（注：console 模式是自身 3-pass 场景图，非 --lights 的图，口径见决策⑥）；text 格式同构导出亦亲验；`renderdoc.capture`（不带/带 name 参数两次）→`{"ok":false,"error":"renderdoc_not_attached","detail":"start this exe via RenderDoc UI..."}` **明确报错不假成功**；附加错误路径全过：`bogus`→unknown_command、`hud extra`→bad_arguments+usage、空行→empty_line、`rg.export out.xml xml`→bad_format、`rg.export Z:/no/...`→write_failed；`quit`→`{"ok":true}` 且进程优雅退出（日志 `--devconsole done: frames=477 exports=3`） |
| V3 单测真实断言 | PASS | 4 新测试文件 20 用例全部**真断言**（无空转）：test_rg_export.cpp:88/170/194/223/247（JSON 全字段+与 BarrierPlan 交叉验证+形状护栏 :156-166/culled/文本/确定性/编译失败骨架）、test_gpu_hud.cpp:20/64/74/139/160（字节公式手工核算、峰值≠末样本两组敏感性断言 :109/:130、环形覆盖、设备透传、格式确定性）、test_renderdoc_capture.cpp:120/142/155/198（NotAttached/ApiRejected/假表逐转发+CaptureFailed+幂等/生产 ModuleNotFound）、test_devconsole_proto.cpp:19/66/77/124/136/160（白名单/错误路径/信封转义/并包） |
| V3 变异复做 | PASS | 实施层变异：gpu_hud.cpp:74 峰值 `if (drawCalls > peak)` 改无条件赋值（取末样本）→ 重建（须用 VS BuildTools cmake；PATH 上的 MSYS2 cmake 写构建树被拒，脚本注释已记载此事）→ **恰 1 用例红 / 2 断言红：test_gpu_hud.cpp(109) CHECK(4 == 6)、(130) CHECK(90 == 106)**，其余 300 用例不受影响——与施工报告 §4.4 逐字一致；复原（MD5 0460b571… 与原文件一致）后重建 **301/771,417 全绿 exit 0** |
| V4 回归 | PASS | `--lights`→exit 0，`D3D12 LIGHTS OK`（centerPass=6 groundLitPass=6 anim OK InfoQueue=0）；`--assets`→exit 0，`D3D12 ASSETS GALLERY OK (models=4)`（probePasses 6/6×4 errors=0）；`git diff rg_render_graph.h/.cpp`＝**14+33 行纯插入、0 删除**，全部为 4 个 const 只读查询（PassNameAt/PassAccessesAt/ResourceDescAt/ResourceHandleAt/Edges），既有语义零改动；决策⑥口径澄清亲核：--lights 每帧真实 DrawIndexed=2（:1829 地面 quad + :1835 cube），"16" 确查无出处 |
| V5 文件规范+报告完整 | PASS | 19 个新/改文件字节级审计（python）：BOM=0、CRLF=0、bareCR=0、末尾换行齐——ALL OK；4 新头文件全带 `#pragma once`；`Native/ThirdParty/renderdoc/renderdoc_app.h` 为官方 v1.37 单头（SOURCE.md 载明上游 blob 与 sha256 `0ab27bfa…` 本地实算一致，MIT）；本日志四节齐全（简报/施工报告/验收清单/本节）；§6 受限声明如实（真 RenderDoc 端到端未跑——挂接成功路径证据=假表单测+代码审查+未挂接真进程实测，验收认可该受限口径） |

**验收备注（不阻断）**：
1. 施工报告 §4.3 "`rg.export out.xml` 格式值域 → `bad_format`" 表述不准：单参数语义是 path（格式缺省 json），实测 `rg.export out.xml` 返回 ok:true 并成功写盘（亲证）；`bad_format` 的真实触发是第二参数∉{json,text}（`rg.export out.xml xml`→bad_format，实测通过）。机制本身存在且正确，属报告举例笔误，建议后续任务顺手修正该行。
2. DrawCall 口径（决策⑥）按"插桩点真值"核对通过：console 模式 3/帧（3 cube，:13667）、--lights 2/帧（:1829/:1835）；简报原"对照 --lights=16"无出处，采纳施工方澄清。
