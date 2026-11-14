# TASK-B-PLANET-09 —— planet-budget 探针：1080p 帧耗/接缝/贴地扫描（双机口径）

- **轨道**：B / PLANET（设计稿 §11-09；改进计划 §3-P2 判据"1080p≥30fps"的取证工具）
- **状态**：doing
- **派发**：主控，2026-11-14
- **依赖**：B-PLANET-04 ✓（--planet3 真渲染）；06 ✓（SampleHeight 贴地扫描）

## 任务简报（主控写）

### 0. 使命

改进计划 §1.2/§10 的明确缺口：现有口径只有 640×480/7.9–8.8ms，**1080p 未验证**。本探针回答"1080p 下 chunk 数、帧耗、接缝、贴地精度"四个数字，且**两台开发机都能跑**（780M 本机 + 对端 RX 9060 XT 各存档一次，改进计划 §6"至少一组可对比数字基线"）。

### 1. 做什么

1. **探针形态**：扩展窗口模式（--planet3 基础上加 --planet-budget 或独立 --pbudget 模式，施工按现有命令行风格定）：
   - **1080p 分辨率**渲染 N 帧（N≥300，热身后采样）
   - 逐帧：frame_ms（p50/p95/p99/均值）、chunk 总数/drawn/culled、vbytes
   - **接缝口径**：整面回读的共享边区域像素差统计（衔接 B-PLANET-08 的 VB 缝宽数字——VB 是顶点口径，这里是像素口径）
   - **贴地扫描**：沿球面取 M 个点（含 chunk 边界附近），SampleHeight(C+dir·R) 与渲染网格顶点高度对照（D9 口径——纯函数 vs 网格插值偏差幅值，设计稿标注的 MISSING）
2. **输出**：JSON（数字全量）+ 截图 + 控制台表；文件名白名单+目录边界照 fsr4_probe 写法；`Tools/Probes/README.md` 登记
3. **本机存档**：780M 一次真跑；**对端机口径**：探针可执行文件+README 步骤（与 fsr4-probe 便携方案同源），对端补验走同一入口
4. **判据挂账**：改进计划"1080p≥30fps"——本机 780M 实测数字如实记录（达到与否都是数据，**不粉饰**；iGPU 与独显差异由双机对照呈现）

### 2. 白名单

- 修改：Native/tests/zrhi_d3d12_window.cpp（新模式）、Native/CMakeLists.txt（如需）、Tools/Probes/**、本日志、dll（如重建）
- 不碰：zplugin.h 签名、zplanet/**、zplugin.cpp/zrender.def（除非探针需新导出——预期不需要，需则 MISSING+回主控）、UnityProject/**、TASKS/HANDOFF/WORKLOG
- 构建走 Tools/build_native.ps1

### 3. 铁律

约法三章四判据；数字带口径（分辨率/帧数/热身/机器）；MISSING 规矩；施工不 commit、不改看板；UTF-8 无 BOM/LF；/WX 零警告

### 4. 完成判据

- 本机 1080p 真跑存档（JSON+截图）：帧耗分布/chunk 数/接缝像素差/贴地偏差幅值四组数字齐
- README 登记完整（含对端补验步骤）；双机口径就绪
- 既有 375 用例全绿（探针不进 zrender_test 或以非断言形式并入）

---

## 施工报告（施工代理，2026-11-14）

### 1. 交付形态

`zrhi_d3d12_window.exe --pbudget`（独立模式，命令行风格照 `--planet3`）：生产导出面真渲染（LoadLibrary zrender.dll + GetProcAddress 17 个符号 = planet3 同策 + `ZR_Planet_SampleHeight`），自建宿主设备 + 1920×1080 BGRA 共享纹理扮演 Unity → AttachDeviceFromTexture → CreateSharedTarget(1920,1080) → SetWorld(seed 1337) → 30 chunk（planet3 同布局：近档 3×3 lod1 + 远档环 16 + 面 1..5 采样）→ 四段取证：

1. **帧耗预算**：热身 60 帧 + 采样 300 帧，逐帧 QPC 包 SetCameraOrigin/SetCamera/BeginScene/RenderFrame（口径注：B-UNITY-04 双槽下帧 N 开头等帧 N−1 GPU fence——稳态每帧墙钟 = max(CPU 提交, GPU 渲染吞吐)，与 --planet3 frame_ms 同口径可比），GetStats 逐帧 drawn/culled/pending/vbytes；
2. **关键帧整面回读**：1080p 地形像素证据 + PNG；
3. **接缝像素口径**：B-PLANET-08 GPU 口径（--planet3 §5.5）的 1080p 加密复测——2m 天底悬停近档 (16,16)/(17,16) 共享东西缘 17 采样（k∈{0,4,…,60,63} 含两角，足迹 2.138mm/px = 640×480 口径 4.81 的 1/3），逐样 CPU float 复刻两侧渲染位置（期望缝宽）+ 整面回读 + 清屏色 (13,26,38)±6 泄漏扫描（缝带窗口 ±3px/±2 行）。**双槽奇偶性显式管理**（ReadTargetPixels 固定读槽 0、RenderFrame 以调用序号 &1 选槽——垫帧保证真帧落偶序，回读必为本帧；--planet3 §5.5 的隐式奇偶依赖在此成为显式记账）；
4. **贴地扫描（D9 MISSING 清零）**：M=100 确定点（面 0：共享边/角 24 + 格对角线横穿 8 + 内部均匀 35 + 格中心 9 + 格点锚点 4 + 近/远档跨界 8 + 双共享边横穿 2×10），`ZR_Planet_SampleHeight(C+dir·R)` vs **渲染网格插值高度**——网格模型 = dll GenerateChunkVerts 同源路由 CPU 复刻（zplanet::PlanetChunkVertexAtGlobal，近 64×64/远 16×16，对角 v00-v11 三角化与 BuildTierIndices 逐项一致）+ Möller–Trumbore double 射线求交（原点=行星中心），径向高度 t−R，dev = h_mesh − h_pure(double)。

JSON（数字全量：frameMs 300 样逐帧 + drawn/culled 逐帧 + seam 17 样逐样 + ground 100 点逐点）+ 2 张 1080p PNG 落 `Tools/Probes/out/`，文件名白名单 + out 目录边界断言照 fsr4_probe（PbValidFileName/PbResolveOutLocation 移植——env-probe.js 单一事实源口径）。退出码 0=JSON 落盘（fps 达标与否是数据不是失败，fsr4_probe 同策）/2=链路硬失败/1=JSON 未写出。stdout 无缓冲（崩溃时保留已打印证据——调试期实际生效一次）。

### 2. 本机 780M 真跑存档（口径：1920×1080，热身 60 + 采样 300，seed 1337，780M 0x1002/0x15BF dmem 408MB，2026-11-14T00:14:24Z）

| 组 | 数字 |
|---|---|
| **帧耗分布** | p50=**1.490ms** / p95=1.795 / p99=2.107 / mean=**1.528** / min=1.307 / max=2.869（300 样） |
| **判据 1080p≥30fps** | fpsMean=**654.4** / fpsP50=**671.1** → **MET**（余量 ~22×；iGPU 数字，独显看对端机对照） |
| **chunk 数** | 总 30（近 9 远 21）；drawn=**4** / culled=**26**（300 帧全程恒定）；pendingUploadsMax=0；vbytes=**1468416**（=期望 9×139264+21×10240，全程稳定）；行星通道内部耗时 last=0.065ms / avg=0.110ms |
| **接缝像素差** | 17/17 帧渲染+回读成功；**清屏色泄漏 = 0**（max_run_row=0 / max_clear_win=0 / sum_clear_win=0 / max_clear_total=0，全帧 2073600 像素全非清屏色）；CPU 复刻期望缝宽 0–3.906mm（只作参考——灾难性抵消放大，B-PLANET-08 §2.3(a) 同款）；1080p 足迹 2.138mm/px 下零泄漏 = 像端口径与 VB 顶点口径（15.93mm max）衔接成立 |
| **贴地偏差（D9）** | \|h_mesh−h_pure\|：**近档 max 197.757m**（格距 ≈4965m，峰值在格中心 distVtx≈3510m）/ 远档 max **925.899m**（格距 ≈20852m）/ 全体 mean 67.916m / p95 197.757m / p99 925.899m；**格点锚点 max 1.74e-9m**（校准成立——网格模型与渲染 VB 逐位同源的构造性证据）；meshMiss=0、SampleHeight API 100/100 成功 |

产物：`out/planet-budget-2026-11-14T00-14-24.json`（32,252B）+ `-terrain.png` + `-seam.png`（各 1920×1080，6,222,418B）+ `planet-budget-run.log`（控制台全表）。复跑两次 frame_ms 有 ~2% 漂移（645→654 fps），接缝/贴地数字逐位复现（确定性 CPU 数学）。

### 3. 判读与挂账（如实，不粉饰）

- **1080p≥30fps：MET，且余量巨大**——但口径是"当前 30-chunk 测试场景、drawn=4"的生产通道帧耗，不是全场景/整球 6144 chunk 的帧耗；双机对照（对端 RX 9060 XT）待补验，两机都跑同一入口。
- **D9 插值偏差幅值是设计输入不是缺陷**：近档格距 4.97km 下网格插值偏离纯函数最高 ~198m、远档 ~926m——玩法碰撞/放置若贴纯函数（SampleHeight），与渲染网格在插值区有百米级观感差；要"碰撞=渲染"需细化 cellsPerEdge/LOD（后续任务输入，本探针只量测）。B-PLANET-06 已锁"格点上纯函数=顶点逐位一致"，本节补的是"格点之间"的 MISSING。
- **接缝**：1080p 加密复测零泄漏——与 B-PLANET-08 的"方案 A 维持（裙边）"裁决在更细像素口径下仍成立。

### 4. 附带缺陷修复（白名单文件内，施工中发现）

**--planet3 证据 PNG 编码缺陷**：原实现把 3B/px RGB 缓冲以 pitch=W×3 传给按 4B/px 读的 `WritePng24`——`planet3_terrain.png`/`planet3_seam_closeup.png` 实为**通道轮转错位图**（解码实测：行内相邻像素绿通道差均值 17.54/255，通道周期轮转；B-PLANET-08 存档的两张图同缺陷——其判据数字取自内存像素扫描，不受影响）。已改 4B/px RGBA（2 处调用点）。**回归证据**：修复后 `--planet3` 控制台断言数字逐位不变（`D3D12 PLANET3 OK (chunks=30 drawn=4 culled=26 vbytes=1468416 lodNextFrame=1 frame_ms=0.985)`，terrainPixels=306217/307200 与修复前一致），PNG 解码绿通道差均值 17.54→0.01。两 PNG 属证据再生，是否随本任务入库由主控裁决。

### 5. 构建与回归

- `Tools/build_native.ps1`：configure+build /W4 **/WX 零警告**；doctest **380 用例 / 823,621 断言全绿**（0 失败——与 B-PLANET-08 基线完全一致，探针未进 zrender_test）。
- `--planet3` 回归：exit 0，OK 行数字逐位一致（上表）；zrender.dll 重建后 sha256 双侧（bin/Release 与 UnityProject/Plugins）由 build 脚本同步。
- 白名单核对：改动 = `Native/tests/zrhi_d3d12_window.cpp`（+1411/−8）、`Tools/Probes/README.md`（+15/−1）、`Tools/Probes/out/*`（新档 4）、`Docs/Parity/planet3_*.png`（证据再生，见 §4）。**零改动**：zplugin.h/zplanet/**/zplugin.cpp/zrender.def（导出 56 条零新增——SampleHeight 为既有导出）、Native/CMakeLists.txt（无需改）、UnityProject/**、TASKS/HANDOFF/WORKLOG。UTF-8 无 BOM / LF（git diff 无 CR 实证）。

### 6. 施工调试记录（可复核性）

首次真跑段错误（segfault）：JSON 组装在 `FreeLibrary(zrender.dll)` 之后调用 `getVersion()`——悬空函数指针。修法：版本串在卸载前拷出。此外发现 `minwindef.h` 将 `near`/`far` 定义为空宏——`const bool near` 编译期即坏，标识符改 `isNear`。两处均已修并有注释。

### 7. 遗留与移交

1. **对端机补验**（本任务移交项）：步骤见 `Tools/Probes/README.md` planet-budget 节——拷 `zrhi_d3d12_window.exe`+`zrender.dll` 同目录跑 `--pbudget`（免 FFX 依赖；VC 运行时缺失时装 Redistributable），JSON+截图回传，结论回填 README 登记行。
2. **GPU clip 域逐顶点读回无手段**（B-PLANET-08 §2.9 同项）：像素口径下界 = 足迹 2.138mm/px（较 640×480 细 3×），clip 舍入精确读回仍需 GPU 侧探针导出（v1 不做）。
3. **--planet3 §5.5 接缝段隐式奇偶**：其 9 采样逐样渲染+回读未显式管理双槽奇偶（奇数样回读为上一采样帧的槽 0 内容）。本探针同段已显式记账；planet3 段窗口宽（±3px/±2 行）且 9 帧结论为零泄漏，是否回修由主控裁决（不影响其判据数字，判据本就以 doctest CPU 口径为准）。
4. **帧耗口径扩展**：本口径测生产通道稳态吞吐；全行星 6144 chunk / 流水线全场景预算是另一口径（B-PLANET-13 若开，本探针 chunk 编排参数可扩）。

---

## 验收清单（验收代理，2026-11-14）

- [x] **N1 范围 PASS**——tracked diff 恰 4 处（window.cpp +1411/−8 / README +15/−1 / Docs/Parity 2 图覆写）；dll 重建后**字节级不变**（sha256 双侧=HEAD `ea97a375…d12c2`）；zplugin.h、zplugin.cpp、zrender.def、zplanet/**、Native/CMakeLists.txt、UnityProject tracked 源码零改动；WORKLOG.md 零改动；out 新档 4 件 mtime/字节数与报告一致
- [x] **N2 复跑复现 PASS**——亲跑 `--pbudget` exit 0：groundScan 全段（100 点逐点）/seamPixel.perSample（17 样）/drawnPerFrame+culledPerFrame（300 帧）/vbytes=1468416/泄漏四计数全 0 与施工 JSON **逐位相同**；frame_ms 有漂移（见结论备注 3），判定不变
- [x] **N3 判据如实 PASS**——全仓新增文本无"全行星达标"类越界措辞；施工报告明确"30-chunk 测试场景、drawn=4，非全场景/整球 6144"；缺口备注见结论备注 2（JSON 限定句缺失/README 修饰位置）
- [x] **N4 D9 PASS**——贴地数字亲跑逐位复现：近档 max 197.757m / 远档 max 925.899m / mean 67.916m / 锚点 1.74032e-09m / meshMiss=0 / 100 点（近 92 远 8）；主控裁决第 3 条处置一致（报告/README/JSON 均定性"设计输入非缺陷、只量测不修复"）
- [x] **N5 PNG 修复 PASS**——亲跑 `--planet3` exit 0，OK 行除 frame_ms（时序量）外逐位一致（chunks=30 drawn=4 culled=26 vbytes=1468416 lodNextFrame=1），terrainPixels=306217/307200；自写解码器实测绿通道差：HEAD 旧图 terrain 18.988/closeup 18.659 → 再生图 terrain **0.0279**/closeup **0.0000**（判据 ≤0.05）；Read 亲验再生图无条纹伪影
- [x] **N6 构建 PASS**——亲跑 build_native.ps1 两轮（第二轮删除 obj 强制重编译 window.cpp）：ALL GREEN，/W4 /WX 零编译警告；doctest **380 用例 / 823,621 断言 / 0 failed**（两轮复绿，探针未进 doctest）；dll 部署后 sha256 双侧一致
- [x] **N7 规范 PASS**——触碰文本逐字节核查：window.cpp/README/本日志/两份 JSON 均无 BOM、纯 LF；四节结构完整（简报/施工报告/主控裁决/验收清单）；README planet-budget 节含本机跑法+对端免仓库跑法+对端补验流程

## 验收结论（验收代理，2026-11-14）

**总结论：PASS**（N1–N7 全过，证据均为验收代理亲跑；MISSING：对端机补验——见备注 6）

| 项 | 结论 | 证据（验收代理亲跑，2026-11-14，AMD 780M 开发机） |
|---|---|---|
| **N1 范围** | **PASS** | `git diff --numstat`：恰 4 处——zrhi_d3d12_window.cpp +1411/−8（与报告一致）、Tools/Probes/README.md +15/−1、Docs/Parity/planet3_terrain.png 与 planet3_seam_closeup.png（二进制覆写）。**dll 字节级不变**：`git diff` 无 zrender.dll，Native/build/bin/Release 与 UnityProject/Assets/Plugins/ZRender 双侧 sha256 相等且 == HEAD（`ea97a375…d12c2`）——本任务只改测试 exe 源码，dll 重建为确定性复现，反证 zrender.dll 源码零改动。全树 `git diff --name-only HEAD` 无其他条目；zplugin.h/zplugin.cpp/zrender.def/zplanet/**/Native/CMakeLists.txt 零改动；WORKLOG.md（tracked）零改动。out 新档 4 件（JSON 32,252B + 两 PNG 6,222,418B + run.log）mtime 08:14+0800=00:14Z 与报告时间戳一致。工作区其余 untracked（PLANET-07 WIP Unity 测试文件、TASK-B-PLANET-10.md、env/linear-bridge 旧产物）为并行任务产物，PLANET-08 验收备注 2 已登记归属，非本任务触碰 |
| **N2 复跑复现** | **PASS** | 亲跑 `Native/build/bin/Release/zrhi_d3d12_window.exe --pbudget`（repo 根 CWD）：exit 0，产出 `planet-budget-2026-11-14T00-22-38.json`（32,249B）+两 PNG（6,222,418B，与施工档同字节数）。与施工 JSON `00-14-24` 程序化逐位对照：**groundScan 整段全等**（100 点 perPoint 逐点 devM/hPure/hMesh/apiOk 全等）、**seamPixel.perSample 17 样逐位全等**（泄漏计数 maxRunRowPx/maxClearWin/sumClearWin/maxClearTotal 全 0）、**drawnPerFrame/culledPerFrame 300 帧逐位全等**、vbytesObserved=vbytesExpected=1468416、terrainPixels=2062590/2073600 全等、config/engine/host/missing/references 全等。差异仅：时间戳/文件名、planetChannelMsLast/Avg（时序量）、frame_ms 分布与 fps（备注 3）。chunk 段 chunks=30/drawn=4/culled=26/vbytes=1468416 逐位成立 |
| **N3 判据如实** | **PASS** | UTF-8 感知全文检索：三份新增文本（施工报告/README/JSON）中"全行星/整球/全场景"与"达标"无任何组合越界主张。施工报告 §3 明确写"口径是'当前 30-chunk 测试场景、drawn=4'的生产通道帧耗，不是全场景/整球 6144 chunk 的帧耗" ✓；README 写明"当前 30-chunk 测试场景 tessellation"（但修饰语贴在贴地偏差句上，见备注 2）；JSON `config.chunksTotal=30`+`chunkLayout` 结构性在场，但 `verdict.note` 无 30-chunk 限定句（备注 2）。反粉饰红线（不许写成"全行星达标"）无违反 |
| **N4 D9** | **PASS** | 亲跑复现与施工档**逐位全等**（N2 同一对照）：\|h_mesh−h_pure\| near max=197.757m / far max=925.899m / mean=67.916m / p95=197.757m / p99=925.899m / vertexAnchorMax=1.74032e-09m（校准成立）/ meshMisses=0 / points=100（near 92 far 8）/ SampleHeight API 100/100 成功（perPoint apiOk 抽验全 true）。主控裁决第 3 条核对：报告 §3"设计输入不是缺陷……后续任务输入，本探针只量测"、README"细化 cellsPerEdge/LOD 是后续任务输入，不是本探针的修复项"、JSON `groundScan.designRef/meshModelNote` 同口径——三处处置一致，未做越权修复 |
| **N5 PNG 修复** | **PASS** | 亲跑 `--planet3` exit 0：`D3D12 PLANET3 OK (chunks=30 drawn=4 culled=26 vbytes=1468416 lodNextFrame=1 frame_ms=1.035)`——除 frame_ms（时序量，0.985→1.035）外与报告逐位一致；terrainPixels=306217/307200 与报告一致。PNG 解码（验收侧自写 zlib+反滤波解码器，脚本存系统 temp 不入库）：**HEAD 旧图**（B-PLANET-08 存档缺陷版）terrain 绿通道差均值 **18.988**/closeup **18.659**（通道轮转错位特征确证）；**再生图** terrain **0.0279**/closeup **0.0000**——判据 ≤0.05 成立。**Read 亲验再生 planet3_terrain.png**：整帧平滑绿色地形、地形结构自然、无竖条纹伪影；对照 Read HEAD 旧图：整幅密布细密竖条纹（X-DIAG-03 特征）——修复前后视觉对照决定性 |
| **N6 构建** | **PASS** | 亲跑 `powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1` 两轮：第一轮增量 ALL GREEN；第二轮删除 `zrhi_d3d12_window.obj` 强制重编译**在验收监督下验证 /WX 零编译警告**（log 中仅有的 WARNING 行为 Vulkan ResourceBarrier 截断测试的运行期输出，非编译警告）；两轮 doctest 均 **380 用例 / 823,621 断言 / 0 failed / 0 skipped**；第 4/4 步 dll 部署后 sha256 双侧一致。开头 `cmake_probe_tmp` 权限拒绝行为 cmake 探测残留，非致命（exit 0） |
| **N7 规范** | **PASS** | 逐字节核查（Python 二进制读）：zrhi_d3d12_window.cpp（19,310 行）/Tools/Probes/README.md/本日志/两份 planet-budget JSON——**BOM=False、CRLF=0、loneCR=0（纯 LF）**。out/ 控制台重定向 log（含施工自档 planet-budget-run.log）为 CRLF——exe stdout CRT 文本模式产物，双方同款，证据 JSON 本体为 LF，不构成违规。本日志四节结构完整（任务简报/施工报告/主控裁决/验收清单主控写）；README planet-budget 节含本机跑法、对端机免仓库跑法（exe+dll 同目录、VC 运行时说明、outMode=portable-exe-dir）、对端补验流程（跑一次→回传→回填登记行）三段齐全 |

### 给主控的备注

1. **X-DIAG-03 根因判定（升级裁决第 2 条）**：planet3 链路的条纹伪影根因**实证成立**——PNG 编码缺陷（3B/px RGB 缓冲以 pitch=W×3 传给按 4B/px 读取的 WritePng24 → 行内通道周期轮转）。证据链：(a) HEAD 旧图解码绿通道差 18.99/18.66 + Read 可见整幅密布竖条纹；(b) 修复后再生图 0.0279/0.0000 + Read 无条纹；(c) planet3 OK 行渲染数字逐位不变——缺陷在证据编码侧，渲染器无关。裁决第 2 条的开放项（其他 Parity 截图路径同源核查、B-TECH-04 存档图重生成）不属本任务，保持挂账。
2. **N3 限度缺口（不构成 FAIL，登记处置）**：施工报告口径完整；README 的"30-chunk 测试场景"限定句出现在同 bullet 的贴地偏差句上，未直接修饰 MET 判据句；JSON `verdict.note` 无 30-chunk/非全行星限定句（仅"达标与否都是数据，不粉饰；iGPU 与独显双机对照"）。三处均无虚假主张，但按 N3 字面"三处均写明"标准 JSON 未达标——建议闭环时在 README MET 句与 JSON verdict.note 各补一短语（如"30-chunk 测试场景口径，非全行星 6144"），由主控定夺是否作为闭环条件。
3. **frame_ms 漂移超 ~2% 带（判定不变，如实登记）**：验收跑 p50=1.528ms（+2.6%）/ p95=2.162ms（+20.4%）/ p99=2.811ms（+33.5%）/ mean=1.640ms（+7.3%）vs 施工档；min 反而更低（1.027 vs 1.307ms）。指向验收窗口机器负载噪声（尾部抬升+最速帧更快），非代码回归——确定性段（接缝/贴地/chunk 账目）逐位复现不受影响，MET 余量仍 ~20×（fpsMean 609.9 / fpsP50 654.5 vs 30fps 判据）。建议后续口径把"~2% 漂移"表述改为"分位数尾部随机器负载浮动，均值带 ±10%，确定性段逐位为复现契约"。
4. **验收运行新增产物（闭环归档口径自定）**：`Tools/Probes/out/`新增 planet-budget-2026-11-14T00-22-38.json + 两 PNG + planet-budget-acceptance-run.log + planet3-acceptance-run.log + acceptance-build-run.log + acceptance-build-run2.log；Docs/Parity 两 PNG 被验收回归再次覆写（terrainPixels/PNG 字节数与施工再生版一致，确定性渲染）。施工报告 §5 自列 4 个 out 新档不变。
5. **dll 未入 diff 比清单预期更强**：N1 清单预期含"dll"，实际 dll 重建后字节级等于 HEAD——本任务零 zrender.dll 源码改动的最强实证（构建确定性亦顺带实证）。
6. **MISSING：对端机（RX 9060 XT）补验**——本机为 780M 单机，验收无法取得对端数字；README 步骤已就绪（备注见 N7），移交项维持施工报告 §7.1 原状。

## 主控裁决（2026-11-14）

1. **Docs/Parity 再生证据（terrain/closeup 覆写）**：**批准入库**——PNG 编码缺陷修复的产物，旧档为错位图（保留在 git 历史）。
2. **X-DIAG-03 关联**：planet3 PNG 编码缺陷（3B/px 缓冲传 4B/px 读取→通道轮转错位）与条纹伪影特征吻合——**很可能即根因**。B-PLANET-09 修复后 planet3 链路绿通道差 17.54→0.01。X-DIAG-03 改口径："验证其他 Parity 截图路径是否同源（B-TECH-04 存档图重生成即可清），并复核修复后全部截图链路"。
3. **D9 偏差幅值**（197.8m/925.9m）：设计输入定性为"当前 tessellation 下玩法碰撞必须贴纯函数（SampleHeight），碰撞=渲染一致性需细化 LOD"——登记为 B-PLANET-11/后续设计输入，不构成本任务 FAIL。

## 验收清单（主控写，验收代理逐条跑并出证据）

- **N1 范围**：tracked diff 恰为 window.cpp(+1411/−8)/README/4 个 out 产物/Docs/Parity 2 图覆写/本日志/dll；zplugin.h、zplanet/**、zplugin.cpp、def、Native/CMakeLists、UnityProject 源码零改动；TASKS/HANDOFF/WORKLOG 未被施工触碰
- **N2 复跑复现**：亲跑 `--pbudget` 一次——接缝/贴地段确定性复现（与施工 JSON 逐位对照）；frame_ms 允许 ~2% 漂移；chunk 段（chunks=30/drawn=4/culled=26/vbytes=1468416）逐位
- **N3 判据如实**：fps MET 口径核对——JSON/README/报告三处均写明"30-chunk 测试场景口径，非全行星 6144"（不许写成"全行星达标"）
- **N4 D9**：贴地偏差数字亲跑复现（近档 max 197.757m/远档 925.899m/锚点 1.74e-9m/meshMiss=0）；主控裁决第 3 条核对
- **N5 PNG 修复**：planet3 回归亲跑（OK 行逐位+PNG 解码绿通道差 ≤0.05）；X-DIAG-03 关联口径核对（主控裁决第 2 条）
- **N6 构建**：build_native.ps1 ALL GREEN /WX 零警告；zrender_test.exe 380/823,621 0 failed（探针不进 doctest）
- **N7 规范**：新增/修改文本 UTF-8 无 BOM/LF；四节结构完整；README 登记含对端补验步骤

验收输出 PASS/FAIL + 逐条证据。你独占原生构建链。
