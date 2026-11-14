# 探针 · 登记表

规则见 `../Docs/Planning/约法三章-2026-11-13.md` 第三节。一句话：**真跑、真输入、缺就报缺、可复核。**
探针不接入引擎任何流程、不改引擎行为，只负责回答"到底是什么"。产出物写 `out/`，带时间戳。

## 登记

| 探针 | 测什么 | 怎么跑 | 最近一次结果 | 状态 |
|---|---|---|---|---|
| `env-probe.js` | 引擎提交、C#/场景文件数、Unity 版本与许可证、播放器 FFX 运行时、磁盘（仓库自适应，两台开发机均可跑） | `node Tools/Probes/env-probe.js` | 本机 2026-11-13：10 项取到 / 3 项 MISSING（见 out/env-2026-11-13T16-56-35.json 与修正后 17 时档） | 可用 |
| `fsr4-probe` | 本机 FSR 4.1 真实能力：caps（含 fsr4ProbeMode 三态）+ UPSCALER 族真设备/null provider 对照 + FG 族双设备对照（原生 exe，两机可跑，跑法见下节） | `Native/build/bin/Release/fsr4_probe.exe`（build_native.ps1 构建后直接跑；或拷 exe+FFX DLL 集免仓库跑） | 本机 780M 2026-11-14：**负对照成立**（null=真设备=2 条 3.1.5/2.3.4、fsr4Available=0、fsr4ProbeMode=0）；FG 族两路均 Available（3.1.6）；**阳性对照 MISSING（本机硬件）**——对端机 RX 9060 XT 补验步骤见下节（`out/fsr4-2026-11-13T22-28-29.json`） | 可用 |
| `linear-bridge` | ZRender 输出与 URP 同机位亮度差（Editor 工具，程序化契约场景，不依赖任何 .unity） | Unity 批处理 `-executeMethod ZFrame.Render.Probes.LinearBridgeProbe.RunFromCommandLine`（勿带 -quit，自退出）；或菜单 ZRender > Probes > Linear Bridge Compare | 本机 2026-11-14（HEAD 06f3478，780M，Linear + URP_Medium）：**桥较 URP 直出亮 12.5%**（帧级均值 luma 61.22→68.88，0-255 域；中心半幅 +8.5%）；p50=0、p95=57.85、p99=77.86（\|Δ\| 分位）；原生中心像素回读 (13,26,38) 与 kClearColor 注释值逐字节一致 | 可用 |
| `planet-budget` | 1080p 下 chunk 数/帧耗分布（p50/p95/p99/mean）+ 接缝像素口径（清屏色泄漏）+ 贴地扫描（SampleHeight vs 网格插值偏差，D9） | `Native/build/bin/Release/zrhi_d3d12_window.exe --pbudget`（build_native.ps1 构建后跑，跑法见下节；两机可跑） | 本机 780M 2026-11-14：1080p 帧耗 p50=1.490ms（671fps）/mean=654fps，**判据 1080p≥30fps MET（口径=30-chunk 测试场景/drawn=4，非全行星 6144）**；30 chunk（drawn=4/culled=26/vbytes=1468416）；接缝 17/17 帧零清屏色泄漏（2.138mm/px 足迹）；贴地偏差近档 max 197.8m / 远档 925.9m / 格点锚点≈0（`out/planet-budget-2026-11-14T00-14-24.json`） | 可用 |
| `abi-precision` | 四点×双口径 double→float 提交精度：口径 A 纯数学（float(目标−相机) 重定位正确路径 vs 全 float naive 大坐标 + JitterAudit A-WORLD-01 口径）+ 口径 B ABI 全链（SetWorld→ChunkCreate→SetCameraOrigin/GetCameraOrigin→模型平移 float(anchor−cam)→端到端顶点位置损失 + SampleHeight float 输出）——§9 预算表逐环节对照 | `Native/build/bin/Release/abi_precision_probe.exe`（build_native.ps1 构建后跑；纯 CPU+dll 导出面，无 GPU 依赖；JSON 落 out/，文件名白名单+边界断言照 fsr4-probe） | 本机 DESKTOP-08EO3II 2026-11-14：四点 A.correct 全部 ≤0.5 ULP(D)（2.41µm@100m→1.37mm@100km）；B.origin round-trip 4/4 逐位；row5 顶点局部 7.81mm（实测幅值 284.7km 半 ULP 内）；row7 平移近场 2.41µm/8.84µm/0.333mm ≤0.488mm 达标、角偏差 ≤1.15e-4 px；row10 SampleHeight 0.058mm；naive@planet-frame 0.05~0.41m（差 6.8~5.3e4 倍）——§9 实测 5 环节全达标（`out/abi-precision-2026-11-14T00-47-07.json` + 复跑 `-55.json` 逐位一致） | 可用 |
| `gpu-probe` | 显卡型号/显存/驱动 | 未编写（需 DXGI/WMI 采集器） | — | **缺** |

## 已存档的两机基线（env-probe）

| 机 | 时间 | 关键结果 | 证据 |
|---|---|---|---|
| 游戏机（RX 9060 XT/RDNA4） | 2026-11-13 | HEAD cdf4eb2；Unity 2022.3.62f3c1；FFX 运行时 3 个（另有 1 个 amd_ags_x64.dll 属 AGS 非 FFX）；**许可证 MISSING**（批处理不可用）；磁盘 D 剩 359.5GB | `out/env-2026-11-13T07-11-24.json`、`out/env-2026-11-13T09-20-14.json` |
| 引擎机（780M/RDNA3，本机） | 2026-11-13 | HEAD cdf4eb2；556 C# / 6 场景；许可证在位；**Build/Windows64 播放器无 FFX DLL（MISSING，如实记录）**；磁盘 C 剩 124GB | `out/` 本机时间戳档 |

## linear-bridge 探针（TASK-B-PROBE-02，2026-11-14 交付）

- **怎么跑**（第三次复跑数字应逐位一致——两次独立跑 61.2226/68.877 完全相同）：
  ```bash
  "C:/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe" -batchmode \
    -projectPath "C:/Users/1/Desktop/zcode/GameFramework/UnityProject" \
    -logFile Tools/Probes/out/linear-bridge-run.log \
    -executeMethod ZFrame.Render.Probes.LinearBridgeProbe.RunFromCommandLine
  # 勿带 -quit（方法自带 EditorApplication.Exit）；退出码 0=已写档（OK 或 MISSING 都是数据）
  ```
  产物：`out/linear-bridge-<UTC时间戳>.json` + `-urp.png` + `-zrbridge.png` + `-diff.png`（文件名白名单 + out 目录边界断言，同 env-probe 口径）。
- **口径**：程序化临时场景（--parity 契约常量：机位 (0,1.6,-4) 朝 +Z、1280×720、地面 20×20 灰 0.5 + 两 cube；探针差异：关阴影、URP 背景取原生清屏色同值）。URP 帧 = 活跃管线直出（sRGB 显示域）；桥帧 = 既有 Bindings 驱动（attach→共享目标→UNORM 外部视图→SetCamera/DrawMesh→RenderFrame×3→生产 ZRender/Blit 直通回读），与 `ZRenderPipeline.cs:243` 同参——被量化的正是这条 UNORM 视图链。
- **结果解读（"略暗"从此有数，但方向反了）**：HANDOFF 渲染体检记"UNORM 视图画面偏暗是已知代价"。实测桥较 URP 直出**亮** 12.5%（帧级）。差值不是背景（p50=0，背景两管线逐字节相同），来自物件区：原生场景着色器是 Lambert+固定环境项 0.45（`zrhi_d3d12_unity_target.cpp:1100-1121`），且桥把 Unity 材质的 sRGB 基色当线性值直接乘光——URP 侧是 PBR+环境 0.12+sRGB 解码。这是**复合差**（着色模型+颜色语义+视图链），JSON 已记录双方全部光照常量可拆读。对 P1-b（typeless 方案）的含义：换 sRGB 视图只影响视图链一项，先要拆出着色/颜色语义项才能看到它的净效果。
- **交叉验证锚点**：原生 `CenterRGBA=(13,26,38)` 与 `kClearColor` 注释"8bit (13,26,38)"逐字节一致；cube B -X 面 ndl=0 → 桥帧该面 = albedo×ambient 精确吻合；两帧几何逐像素对齐（矩阵链复刻正确）。

## fsr4-probe 探针（TASK-B-PROBE-01，2026-11-14 交付）

- **怎么跑（本机）**：`powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1` 构建后直接跑 `Native/build/bin/Release/fsr4_probe.exe`（CMake POST_BUILD 已把 4 个 FFX dll 拷到 exe 目录；JSON 落 `Tools/Probes/out/fsr4-<UTC时间戳>.json`，文件名白名单 + out 目录边界断言同 env-probe 口径）。退出码 0 = JSON 已落盘（**FSR4 支持与否是数据不是失败**——负对照同样是有效结果）；1 = out 目录不可写等致命 IO。
- **怎么跑（对端机，免仓库）**：把 `fsr4_probe.exe` + 4 个 FFX DLL（`amd_fidelityfx_upscaler_dx12.dll`、`amd_fidelityfx_loader_dx12.dll`、`amd_fidelityfx_framegeneration_dx12.dll`、`amd_ags_x64.dll`，在 exe 同目录或 `Native/ThirdParty/ffx-fsr/bin/`）拷到对端机**同一目录**直接运行。exe 实际导入表（dumpbin 实证）：d3d12/dxgi/KERNEL32/UCRT（Win10/11 自带）+ **MSVCP140/VCRUNTIME140/VCRUNTIME140_1**——对端机缺 VC++ 2015-2022 x64 运行时时装一次 Redistributable，或从任意开发机拷这 3 个 DLL 进同目录。免仓库模式 JSON 落 exe 同目录 `out/`（outMode=portable-exe-dir；本机已实测该模式，数字与仓库模式逐位一致）。也可整仓同步后在对端跑 build_native.ps1 重建（等价）。
- **读什么（对照表口径）**：caps 行 `fsr4Available`/`fsr4ProbeMode`（0=真设备探测可信 / 1=null 回退不可信 / 2=未探测）；UPSCALER 族 `device=null` vs `device=real` 两条 provider 列表（条数+版本名）；FG 族两路 `support/rc/providerName`。JSON 的 `comparison.shape` = negative-control（两路一致）或 positive-control（null 截断 < 真设备完整，含 4.x）。
- **结果判读（B-FSR-01 裁决口径）**：
  - **阳性对照成立**（对端 RX 9060 XT 应复现）：`null=2 条 < real=4 条（含 4.1.2）`、`fsr4Available=1`、`fsr4ProbeMode=0` → B-FSR-01 修复效果实证（null 口径会把 FSR4 误判为不支持）。复验后把 JSON 与一行结论回填到 `Docs/Logs/TASK-B-FSR-01.md`（B-PROBE-01 移交项）与本表"最近一次结果"列。
  - **负对照成立**（本机 780M 实测 2026-11-14）：`null=真设备=2 条（3.1.5/2.3.4）`、`fsr4Available=0`、`fsr4ProbeMode=0` → "不支持"是硬件真相（RDNA3 iGPU 不注册 4.x，SDK v2.3.0 文档要求 7000/9000 系离散卡）而非探测缺陷——与 B-FSR-01 施工/验收存档逐位一致。
  - FG 族（FRAMEGENERATION）：780M 上两路均 Available（provider 3.1.6，与 `zrhi_d3d12_window --fg` 既有实测一致）——FG 族双设备对照首次有数（B-FSR-02 审计 A2 的"未做对照"项在此补上本机一半）。
- **边界**：探针是独立 exe——不进 zrender.dll、不进 Unity 绑定、不进 zrender_test（375 用例不受影响）；`ZR_Upscaler_QueryCaps`/`ZR_ApiVersion` 只读消费，引擎行为零改动。

## planet-budget 探针（TASK-B-PLANET-09，2026-11-14 交付）

- **怎么跑（本机）**：`powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1` 构建后直接跑 `Native/build/bin/Release/zrhi_d3d12_window.exe --pbudget`（在仓库根跑；exe 运行期 LoadLibrary 同目录 zrender.dll 走生产导出面——`--planet3` 同策）。产物：`out/planet-budget-<UTC时间戳>.json` + `-terrain.png` + `-seam.png`（文件名白名单 + out 目录边界断言，fsr4-probe 同款）；控制台全表建议重定向存档（`> Tools/Probes/out/planet-budget-run.log 2>&1`）。退出码 0 = JSON 已落盘（**fps 达标与否是数据不是失败**，fsr4-probe 同策）；2 = 探针链路硬失败（dll/设备/chunk/渲染/回读，无 JSON）；1 = JSON 未写出。
- **怎么跑（对端机，免仓库）**：把 `zrhi_d3d12_window.exe` + `zrender.dll` 拷到对端机**同一目录**直接运行 `--pbudget`（exe 链接为静态 zrhi/zrender_core/zupscaler + 系统 d3d12/dxgi/ws2_32，FFX 组件为运行期按需 LoadLibrary——`--pbudget` 不消费 FFX，无需拷 FFX DLL；缺 VC++ 2015-2022 x64 运行时时装一次 Redistributable 或拷 MSVCP140/VCRUNTIME140/VCRUNTIME140_1 进同目录）。免仓库模式 JSON 落 exe 同目录 `out/`（outMode=portable-exe-dir）。也可整仓同步后在对端跑 build_native.ps1 重建（等价）。**对端补验流程**：对端跑一次 → 回传 JSON+截图 → 结论一行回填本表"最近一次结果"列与 `Docs/Logs/TASK-B-PLANET-09.md`。
- **测什么（四组数字，双机对照基线）**：
  1. **帧耗预算**：1920×1080、热身 60 帧 + 采样 300 帧，逐帧 frame_ms（QPC 包住 SetCameraOrigin/SetCamera/BeginScene/RenderFrame；B-UNITY-04 双槽下帧 N 开头等帧 N-1 的 GPU fence → 稳态每帧墙钟 = max(CPU 提交, GPU 渲染吞吐)，与 `--planet3` frame_ms 同口径可比）→ p50/p95/p99/mean/min/max + fps；逐帧 drawn/culled（GetStats 快照）+ vbytes。
  2. **接缝像素口径**：B-PLANET-08 GPU 口径的 1080p 加密复测——相机 2m 悬停近档 (16,16)/(17,16) 共享东西缘 17 个采样顶点（含两角）正上方、天顶向下，整面回读扫描清屏色 (13,26,38)±6 泄漏（缝带窗口 = 投影缝位 ±3px/±2 行）。1080p 足迹 2.138mm/px（640×480 口径 4.81mm/px 的 1/3）。判据 = 清屏色泄漏像素；expect_mm/expect_px 复刻列有灾难性抵消放大（B-PLANET-08 §2.3(a) 同款）只作参考。
  3. **贴地扫描（设计稿 D9 MISSING 清零）**：M=100 确定点（面 0：共享边/角/格对角线附近 + 近/远档跨界 + 格点锚点 + 均匀内部点），`ZR_Planet_SampleHeight(C+dir·R)`（dll 纯函数 ABI）vs 渲染网格插值高度（CPU 复刻 dll GenerateChunkVerts 同源路由 + BuildTierIndices 对角 v00-v11 三角化的射线求交，径向高度 t−R）。格点锚点偏差应≈0（校准项）；本节是**插值口径**，与 B-PLANET-08 的**顶点口径**（VB float 化缝宽 15.93mm max）不混。
  4. **判据挂账**：改进计划 §3-P2 "1080p≥30fps"——fpsMean 与 fpsP50 双口径对照，达标与否都是数据不粉饰。
- **结果判读**：`budget.frameMs`（分布）与 `verdict.fpsMean/fpsP50/met`（判据）；`chunks.*`（drawn/culled/vbytes——与本机 planet3 数字同量级即口径连贯）；`seamPixel.sumClearWin/maxRunRowPx`（>0 = 有缝泄漏，需回查）；`groundScan.devAbs`（max/p95 分近/远档看；`vertexAnchorMax`≠0 = 网格模型失准，数字不可信）。
- **本机 780M 存档（2026-11-14T00:14:24Z，Radeon 780M 0x1002/0x15BF，样本 300@1080p）**：frame_ms p50=1.490 / p95=1.795 / p99=2.107 / mean=1.528 / max=2.869 → fpsMean=654.4 / fpsP50=671.1，**MET**（余量 ~22×，口径同上=30-chunk 测试场景非全行星——iGPU 与独显差异看对端机对照）；drawn=4/culled=26 全程恒定，vbytes=1468416 稳定；接缝 17/17 帧零泄漏（max_run_row=0）；贴地偏差 |h_mesh−h_pure|：近档 max 197.757m / p95 197.757m / 远档 max 925.899m / 全体 mean 67.916m，格点锚点 max 1.74e-9m（校准成立）。注意：近/远档偏差幅值是**当前 30-chunk 测试场景 tessellation**（近档格距 ≈4965m、远档 ≈20852m）下的数字——玩法贴地若按纯函数判碰撞，网格插值区会有百米级偏差，这是 D9 MISSING 想暴露的事实（细化 cellsPerEdge/LOD 是后续任务输入，不是本探针的修复项）。
- **边界**：探针模式只进 `zrhi_d3d12_window.exe`（不进 zrender.dll/Unity 绑定/zrender_test——380 用例零改动全绿）；dll 导出零消费新增（SampleHeight 既有导出，def 56 条零改动）；`--planet3` 顺带修复截图编码缺陷（见下）。
- **附带修复（施工中发现，如实登记）**：`--planet3` 的两张证据 PNG（`Docs/Parity/planet3_terrain.png`/`planet3_seam_closeup.png`）原把 3B/px RGB 缓冲以 pitch=W×3 传给按 4B/px 读的 `WritePng24`——实为**通道轮转错位图**（B-PLANET-08 存档同缺陷；解码实测行内相邻像素绿通道差均值 17.54）。已改 4B/px RGBA（仅证据编码，控制台断言数字逐位不变——回归实测 `D3D12 PLANET3 OK (chunks=30 drawn=4 culled=26 vbytes=1468416 ...)` 与修复前一致）。

## abi-precision 探针（TASK-B-PLANET-10，2026-11-14 交付）

- **怎么跑（本机）**：`powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1` 构建后直接跑 `Native/build/bin/Release/abi_precision_probe.exe`（运行期 LoadLibrary 同目录 zrender.dll 走生产导出面——--planet3 同策）。产物：`out/abi-precision-<UTC时间戳>.json`（文件名白名单 + out 目录边界断言，fsr4-probe 同款）；控制台全表建议重定向存档。退出码 0 = JSON 已落盘（**精度是测量不是过/不过**——数字带口径引用）；1 = JSON 未写出。**纯 CPU + dll 导出面，零 D3D12 设备依赖**（ChunkCreate 延迟上传、无 RenderFrame 永不触 GPU），CI/无显卡机可跑。
- **怎么跑（对端机，免仓库）**：把 `abi_precision_probe.exe` + `zrender.dll` 拷到对端机**同一目录**直接运行（exe 静态链接 zkernel/zrhi/zrender_core/zplugin + 系统 d3d12/dxgi；缺 VC++ 2015-2022 x64 运行时时装一次 Redistributable 或拷 MSVCP140/VCRUNTIME140/VCRUNTIME140_1 进同目录）。免仓库模式 JSON 落 exe 同目录 `out/`（outMode=portable-exe-dir）。也可整仓同步后跑 build_native.ps1 重建（等价）。**对端补验流程**：对端跑一次 → 回传 JSON → 与本机数字对照（应逐位一致——纯 CPU 确定性计算，与 GPU 无关）→ 结论一行回填本表与 `Docs/Logs/TASK-B-PLANET-10.md`。
- **测什么（四点 100m/1km/10km/100km × 双口径，同一对 target-camera 几何可直接对照）**：
  1. **口径 A 纯数学**：`A.correct = float(objAbs−camAbs)`（重定位正确路径，RenderOffsetF 生产同式）vs `A.naive@planet-frame = float(objAbs)−float(camAbs)`（全 float 大坐标）+ `A.jitter@origin-frame`（JitterAudit(D,1.0,10.37)——A-WORLD-01 7.8mm@100km 同函数同参数口径）。m + ULP(D) 双单位。
  2. **口径 B ABI 全链**：zrender.dll 生产导出面 SetWorld→ChunkCreate（校验链+账目）→SetCameraOrigin→GetCameraOrigin（round-trip 逐位=§9 row3）→模型平移 `float(anchor−cam)`（unity_target.cpp 逐绘 CB 口径=§9 row7）→端到端顶点位置损失（顶点字节=生产 GenerateChunkVerts 本体经 zrhi 静态库白盒钩子——ChunkCreate 内部同函数零漂移，B-PLANET-08 同策；viewPos=local+t 为 planet2.hlsl VS 口径）。含锚点顶点（local≡0 构造自证）与全块 4352 顶点最大（含 284.7km 级角点——提交口径，可见性看角偏差列）。
  3. **SampleHeight float 输出**（§9 row10）：16 样本 dll 纯函数 ABI vs PlanetTerrainHeightM double 复刻。
  4. **§9 预算表逐环节对照**：JSON `budget9` 10 行——实测 5 环节（row3/4/5/7/10）+ row6 归 B-PLANET-08 存档 + row1/2/8/9 写明归属（不伪造数字）。
- **结果判读**：`ulpReference`（FloatUlpAt 实测应与 §9 理论值逐位一致）；`distances[].aPureMath/bAbi`（四点双口径全数）；`staticDeviation`（row5 devMax 与幅值——注意实测 max|posAbs−anchor|=284.7km 修正 §9 的 ~221km 估算，结论不变）；`budget9[].withinBudget*`（达标/超出都是数据）；`selfCheck`（顶点逐位/账目/ULP 匹配）；`missing`（0 项=全量取证）。
- **本机 780M 存档（2026-11-14T00:47:07Z，Release /O2，seed 1337）**：A.correct 2.41026e-6/8.84198e-6/3.32562e-4/1.37250e-3 m（全部 ≤0.5 ULP(D)）；A.naive@planet 0.406/0.225/0.249/0.0533 m（行星中心系大坐标灾难——差 6.8~5.3e4 倍）；jitter@origin 100km naive=2.8125mm ∈(1mm,7.8125mm]（与 test_planet.cpp doctest 确定性复现一致）；B.origin round-trip 4/4 逐位 maxdiff=0；B.trans=B.center 逐距离相等（锚点零附加损失）；B.chunkMax 8.58~15.65mm、角偏差 ≤1.15e-4 px@1080p/hFOV60°；row5 devMax=7.8109mm；row10 devMax=5.8232e-5 m@max|h|=1055.51m；账目 create−destroy==live 全程 OK。**复跑 00-47-55 逐位一致**。
- **边界**：探针是独立 exe——不进 zrender.dll、不进 Unity 绑定、不进 zrender_test（380 用例零改动全绿）；dll 只读消费（ChunkCreate 一次 + Destroy/ClearAll 兜底）；几何口径=锚点悬停（|target−cam|=D 精确可控），生产任意相机位下角点损失的可见性口径看角偏差列（恒亚像素）。

## 已发现的环境事实（不填默认值）

1. **播放器 FFX DLL 两机不一致**：游戏机 Build/Windows64 有 3 个 FFX DLL（手工补的，HANDOFF 曾记"4 个"，多出的 1 个实为 AMD AGS）；引擎机播放器 0 个。→ 引擎机玩家包当前不能跑 FSR（属实）；原生测试 exe 目录（`Native/build/bin/Release/`）FFX DLL 齐备，引擎侧真机验证走 `zrender_test.exe`。
2. **引擎内现有 caps 探测传空设备**（`upscaler_caps.cpp:61`），结果不可信——修复见 TASK-B-FSR-01；正式探针见 TASK-B-PROBE-01。
3. 纯 Node 取不到显卡信息（gpu-probe 仍缺），**没有编造显卡型号**。
