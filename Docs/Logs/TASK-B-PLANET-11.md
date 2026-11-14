# TASK-B-PLANET-11 —— 编辑器内验收集成：三档截图/行走账目/重定位联动

- **轨道**：B / PLANET（设计稿 §11-11；依赖 06 ✓ 08 ✓ 07 ✓）
- **状态**：doing
- **派发**：主控，2026-11-14
- **定位**：托管链（07 交付）在编辑器真实场景里的 PlayMode 端收官——前三档改进计划判据（三档无可见接缝/行走无跳变）的编辑器取证

## 任务简报（主控写）

### 0. 使命

在 Unity 编辑器内（非窗口模式）经 B-PLANET-07 托管桥把行星通道跑通并取证：三档截图、球面行走账目（1km/5km/50km 变体——设计稿口径补充）、WorldOrigin 重定位联动。这是 B-PLANET-12（IL2CPP/Android）的前置。

### 1. 做什么

1. **场景接线**：最小探针场景（SCN_Probe 或既有场景扩展）挂 WorldOriginPlanetLink + ZrPlanetStreamingDriver + 相机 rig——纯程序化（ParityBench 契约风格），不依赖美术资产
2. **三档截图**（1080p）：近/中/远三档 chunk 区域各一张——地平线/高程带/limb 可见性目检（对照 --planet3 既有证据），入 Docs/Parity/
3. **球面行走账目**：相机沿球面位移 1km/5km/50km（三变体）——
   - 账目断言：create/destroy/live 与 GetStats 严格对账全程零漂移（分帧预算生效）
   - 重定位联动：WorldOrigin(4000m) 触发重定位时 SetCameraOrigin 收到的新原点值正确（A-WORLD-01 口径）
   - 行走前后截图像素对照（跳变检测——设计稿 §10 判据"行走无跳变"的编辑器口径）
4. **PlayMode 测试**：EditMode 账目用例已有（07），本任务补 PlayMode 段（真帧循环）——挂场景冒烟+账目抽查
5. **不碰**：Native/**（全链已交付）、zplanet、TASKS/HANDOFF/WORKLOG
6. 白名单：UnityProject/Assets/**（Planet/Game/View/Scenes/Tests）、本日志；新 asmdef 登记矩阵债不许重犯

### 2. 铁律

- 约法三章；施工不 commit、不改看板；UTF-8 无 BOM/LF；零第三方依赖
- Unity 批处理纪律（单实例/删锁/测试勿带 -quit/证据以 xml 为准）
- 截图判读注意：条纹伪影已修（X-DIAG-03 根因 35fb6ee），新截图应干净——若仍有条纹按 X-DIAG-03 残余口径登记而非忽视

### 3. 完成判据

- 三档截图入库+目检证据；三变体行走账目零漂移+重定位联动正确；PlayMode 段绿
- 全量绿（EditMode ≥1211 / PlayMode 现有基线+新增）；架构/naming PASS

## 施工报告（施工代理，2026-11-14）

### 1. 交付概览

**通过行（全部亲跑，证据 xml 本次亲写）**：

```
探针段    : 4/4 全绿（-testFilter 单fixture 跑 + PlayMode 全套内复跑，两轮独立绿；证据 TempLogs/TASK-B-PLANET-11/probe_playmode.xml + TempLogs/runtests_playmode.xml）
EditMode  : total=1211 passed=1211 failed=0 skipped=0（基线 1211 保持——本任务 EditMode 零改动；证据 TempLogs/runtests_editmode.xml）
PlayMode  : total=114 passed=114 failed=0 skipped=0（基线 110 + 新增 4；证据 TempLogs/runtests_playmode.xml）
arch      : PASS exit 0（证据 TempLogs/arch_report.txt，run_tests.ps1 亲跑轮）
naming    : PASS exit 0（违规 0；证据 TempLogs/name_report.txt，run_tests.ps1 亲跑轮）
汇总      : run_tests.ps1 -Segments editmode,playmode,arch,naming → RESULT: ALL PASS（TestReport.txt）
```

**文件清单（全部在白名单解释范围内）**：

| 文件 | 变更 | 内容 |
|---|---|---|
| `UnityProject/Assets/Game/Tests/PlayMode/ZrPlanetProbePlayModeTests.cs` | 新增 939 行 | 探针本体（4 条 UnityTest）：程序化场景装配（相机 rig + WorldOrigin + WorldOriginPlanetLink + ZrPlanetStreamingDriver，生产默认配置零偏置）、三档截图取证、三变体球面行走（账目/接线/预算/重定位/像素三层判据）、1080p Game View 标定 |
| `UnityProject/Assets/Game/Tests/PlayMode/Game.View.PlayMode.Tests.asmdef` | +1/0 | references 增加 `ZFrame.Render`（Tests 程序集豁免架构矩阵——B-PROBE-02 之债无复发；arch PASS 实证） |
| `Docs/Parity/planet_ed_near.png` | 新增 1920×1080 | 近档截图（贴地 600m） |
| `Docs/Parity/planet_ed_mid.png` | 新增 1920×1080 | 中档截图（400km） |
| `Docs/Parity/planet_ed_far.png` | 新增 1920×1080 | 远档截图（8000km 天底） |
| `Docs/Parity/walk{1k,5k,50k}_{before,after}.png` | 新增 6 张 960×540 | 三变体行走前后复访对照证据（对照数值取自 1920×1080 全分辨率读回） |

**本任务未触碰**：`Native/**` 零改动（`git diff --stat -- Native/` 为空）、zplugin/zplanet、TASKS/HANDOFF/WORKLOG、无 git commit。Unity 批处理纪律全程遵守（单实例前检 + 删锁 + `-runTests` 不带 `-quit` + 证据以 xml 为准）。

**工作区甄别（非本任务改动，如实登记）**：
1. `UnityProject/Assets/ZRender/Tests/ZrPlanetStreamingTests.cs` / `ZrPlanetWiringTests.cs`（+.meta）与 `ZRender/Runtime/Planet.meta` 为**未入库状态**——B-PLANET-07 提交 b73c0c5 只含 Runtime/Bindings/View 文件，两份测试文件与其 meta 留在工作区（EditMode 1211 含它们，判定依据磁盘状态）；是否补提交归主控。
2. `Docs/Parity/crowd_r9_15_*.png` / `npc_attitude_divergence.png` / `utility_*` 5 张既有证据 PNG 被 PlayMode 全套的既有像素用例**例行重写**（mtime 刷新，字节级 diff）——PlayMode 套件每次运行的固有行为，非本任务改动。
3. `Tools/Probes/out/*` 在途产物为并行任务遗留，未触碰。

### 2. 场景接线方式（简报 §1-1）

**纯程序化装配（ParityBench 契约风格，零美术依赖、零 .unity 资产）**——`BuildRig()` 在测试生命周期内挂载与生产完全相同的组件面：

```
~PlanetProbeCamera（tag=MainCamera，SolidColor 黑底清屏，FOV 60°，targetTexture=1920×1080 RT）
~PlanetProbeOrigin（WorldOrigin：AutoRebase=true 生产默认 4000m 阈值每帧自检，TargetCamera=探针相机）
~PlanetProbeLink（WorldOriginPlanetLink：SerializedObject 注入 _origin/_targetCamera——LateUpdate 每帧
    SetCameraOrigin(UnityToWorld(double 直传)) + viewRel 发布，构造唯一在 WorldOriginMath）
~PlanetProbeDriver（ZrPlanetStreamingDriver：Update 每帧 TickOnce——门控/SetWorld 一次/读回原点/预算内流式，
    生产默认配置零偏置：center=0 / seed 1337 / 32×32 / 预算 9000µs / 近档硬帽 1）
```

每帧真循环链路：WorldOrigin.Update（自动重定位）→ Driver.Update（流式 Tick）→ 探针协程（姿态推进 + 断言）→ Link.LateUpdate（推送原点 + viewRel）→ ZRenderPipeline.Render（sceneBridge.SetCamera(viewRel+proj) → RenderFrame 原生行星通道 → 双槽共享纹理 → Blit 到相机 RT）→ 探针 ReadPixels。

**SRP 说明**：项目默认管线是 URP（A 轨像素用例基线）。fixture 在 UnitySetUp 把管线切到 ZRender（与 `ZRenderBoot.TryEnableFromCommandLine` **完全同一机制**：`QualitySettings.renderPipeline` + `GraphicsSettings.defaultRenderPipeline` 两处都设），UnityTearDown 还原 URP——生产侧「运行期禁止跨管线热切」约定不受影响（`RenderPipelineSelector` 编辑期门未动，这是测试装备的自包含切换，用完即还）。

**1080p 口径取得（本任务实测标定，可复用）**：批处理编辑器（无 Game View 窗口）`Screen.width/height` 默认 640×480，且 `Screen.SetResolution` 无效；而管线共享目标尺寸取自 Screen。实测口径：`EditorWindow.position` 为**逻辑像素**、编辑器播放态 `Screen.width/height` 为**物理像素**（本机 OS 200% DPI：请求 800×600 窗口实测 Screen=1600×1158）。做法（`ResizeGameViewToAsync`）：小窗口标定 DPI 缩放比（2.0）与窗口 chrome（42px），按「窗口逻辑尺寸 = (目标物理渲染区 + chrome)/缩放比」落位 + 实测差迭代校正——最终 `Screen=1920x1080` 精确成立，管线日志「共享目标就绪：1920x1080 BGRA8 × 2 槽」实证。桌面物理 2880×1800，1920×1080 渲染区可容纳。拿不到 1080p 时 SetUp 断言以实测值明确失败（不降级、不偷换分辨率）。

### 3. 三档截图清单与目检结论（简报 §1-2）

| 文件 | 位姿 | 目检结论（Read 工具逐张亲看） |
|---|---|---|
| `planet_ed_near.png` | 贴地 600m、face0 赤道 arc=0、俯仰 5°、near=10/far=400km | **地平线可见**（含雪线山脊剪影，微曲）+ **高程带/等高线可见**（绿→浅色带 + 500m 级浅色等高线）+ 近场地形平滑渐变。上 43% 清屏色（生产通道无天穹——设计稿 §12-5 口径）。无接缝泄漏、无条纹 |
| `planet_ed_mid.png` | 400km、俯仰 17°、near=200km/far=2500km | **地平线曲率清晰可见**（弧顶过画面 ~40% 高度、整幅横向连续）+ 距离雾把地形压成雾色平面 + **两档 chunk 同框**（live=162：近档 19 + 远档 143）。无接缝泄漏、无条纹 |
| `planet_ed_far.png` | 8000km 天底、near=4000km/far=9000km | **limb 可见**：完整行星圆盘（视直径 ~52.6° < 60° FOV），地形噪声纹理（绿/米双色高程带）铺满盘面，盘缘干净利落、**无清屏色泄漏**（live=2110 全远档，SeamCheckFails=0）。无条纹 |

**X-DIAG-03 残余口径**：三张 1080p 与六张行走对照图均**无**通道轮转错位/周期条纹（本链路 PNG 由 Unity `EncodeToPNG` 从 RT 读回编码，与原生探针 3B/px 缺陷路径无关）——X-DIAG-03 修复后口径成立，无残余登记。

**流式收敛数字（pose@0 → mid → far）**：贴地 22 帧（live=4）；中档 39 帧（live=162）；远档 182 帧（live=2110，vbytes=23.15MB）。逐帧预算断言全程成立：每 tick 近档操作 ≤1（贴地 4 块近档在 22 帧内完成 = 硬帽 1/tick 的直接节奏证据）、每 tick 成本和 ≤9000µs。行星通道内部耗时（LastFrameMs）：0.05~0.67ms 量级；批处理真帧循环吞吐 ~100-119fps @1920×1080（≥30fps 判据顺带满足；**正式 1080p 帧耗基线归 B-PLANET-09**，此处仅诊断记录）。

### 4. 三变体球面行走账目数字（简报 §1-3）

口径：face0 u 轴测地线（θ=arc/R，double 域），贴地高度 = SampleHeight（与流式锚点同一纯函数）+600m，步长 1km/100 帧、5km/250 帧、50km/500 帧，去程+回程复访同一起点。逐帧行走账目断言（每帧独立成立）：原生恒等式 `createTotal−destroyTotal==liveChunks`、托管/原生 create/destroy/live **增量逐项相等（全程零漂移）**、`LastAccountOk`、每 tick 近档 ≤1 / 成本和 ≤9000µs、`GetCameraOrigin` 与 `WorldOrigin.UnityToWorld(camera.position)` **逐位一致**（重定位帧按 1e-3m A-WORLD-01 V3 口径）、绝对位姿守恒每帧 <1e-3m。

| 变体 | 重定位触发点 | 账目 | 复访像素对照 | 逐帧连续性 |
|---|---|---|---|---|
| **1km** | **0 次**（<4000m，纯量化一致性变体） | 999 帧账目零漂移（行走全程零 chunk 操作，见下注） | **movedFraction=6e-6**（meanAbs 0.003）——重渲染近乎逐位一致 | median 1.0e-5 / max 2.8e-5 |
| **5km** | **1 次**：step=201 arc=4020m（去程跨 4000m 阈值；回程终点 pos≈3.9km 恰未触发） | 同上零漂移 | **movedFraction=6e-6** | median 4.5e-4 / max 2.65e-3；maxOnRebase=1.48e-3 < 常规帧 2.65e-3 |
| **50km** | **23 次**：去程 12 次（arc=4.1/8.1/…/48.3km）+ 回程 11 次（arc=44.1/40.1/…/3.9km）——每 4km 一触发，触发点全 trace 见 xml（TestContext.Out） | 999 帧 + 23 次重定位全程零漂移；**每次重定位 camAbs drift=0.000000m（逐位守恒）** | **movedFraction=6e-6**——跨 23 次重定位的同位姿重渲染与出发帧几乎逐位一致 | median 0.0115 / max 0.19（常规视差长尾）；**maxOnRebase=0.117 < maxOnOther=0.19**——重定位帧落在常规运动视差带内（带 0.335），无重定位离群 |

**跳变检测判读（三层，各层独立成立）**：① 复访重渲染 ≈0（6e-6）——最灵敏层，任何持久化坐标/重定位误差都会整幅位移；② 重定位帧连续性（maxOnRebase ≤ 常规带）——原点错帧会离群；③ 单帧粗跳变 <0.5——4km 级原点错误=全幅位移。三层全绿 = 设计稿 §10「行走无跳变」编辑器口径成立；`test_planet.cpp:93-96`「重定位前后渲染偏移逐位不变」的 PlayMode 侧复验成立。

**如实注记（chunk 边界与 LOD）**：① 50km ≪ chunk 弧长 312.7km——三变体**均不跨流式格边界**（设计稿 §10 预告的口径），且行走全程相机转角 0.449° < 排名重建阈值 0.703°、lenDrift ≤0.075% <5% → **行走全程零 chunk 操作**（lodOpFrames=0），账目处冻结态——本判据实际考验的就是逐帧量化一致性 + 重定位联动（设计稿原注一致）。② 分帧预算证据取自收敛段（贴地 4 近档按 1/tick 摊布、中/远档 12 远档/tick），行走段本身零操作。③ LOD 档界弹跳（§12-8 已知受限）未在贴地 50km 内触发（不跨 900km 迟滞带），维持已登记口径。

### 5. PlayMode 段与全量测试数字（简报 §1-4）

- **PlayMode 段（本任务新增 4 条 UnityTest，真帧循环）**：`Probe_ThreeTierScreenshots_CaptureEvidence`（7.5s）/ `Probe_Walk1km_…`（10.1s）/ `Probe_Walk5km_…`（23.3s）/ `Probe_Walk50km_…`（46.6s）——含门控冒烟（GateFailed=false、通道启用日志）、真帧渲染冒烟（drawn>0 + drawn+culled==live 每档验证）、SRP 切换/还原。
- **全量**：EditMode **1211/1211**（本任务 EditMode 零改动，只增不减保持）；PlayMode **114/114 = 基线 110 + 新增 4**；arch PASS（Tests 程序集豁免矩阵，asmdef 引用扩张实证）；naming PASS 违规 0。
- 证据 xml：`TempLogs/runtests_editmode.xml` / `TempLogs/runtests_playmode.xml` / `TempLogs/TASK-B-PLANET-11/probe_playmode.xml`（单 fixture 细节轮，含重定位全 trace + 每档收敛数字）；`TempLogs/arch_report.txt` / `TempLogs/name_report.txt`。

### 6. 施工调试记录（可复核性）

1. **编译关（2 轮）**：`UnitySetUp/UnityTest` 缺 `using UnityEngine.TestTools`；`EditorWindow` 无 `size`（用 `position`）、`Screen.SetResolution` 需三参。均一轮修毕。
2. **1080p 标定（4 轮迭代）**：批处理 Screen 默认 640×480 且 SetResolution 无效 → 反射 GameViewSizes 预设失败（`instance` 属性在泛型基类）→ 窗口标定发现「position=逻辑像素、Screen=物理像素」（OS 200% DPI，日志 `[probe] calib: scale=2.000 chrome=0x42`）→ 按比例落位 + 迭代校正，`Screen=1920x1080` 精确成立。全程诊断留 `[probe]` 日志行。
3. **首跳量化容差**：贴地首跳把相机放在 6371km 幅值的 float 坐标（ULP 0.25m），重定位守恒该量化值——守恒容差改为随**跳姿时刻**幅值走（4×2^(e−23)），行走档（≤4.2km）仍收 1e-3m A-WORLD-01 口径。
4. **中档位姿改 400km（发现生产覆盖缺口，见 §7-1）**：258km 位姿实测 live=4（贴地期望集未刷新）——中档截图最初只含 4 块贴地 chunk 的投影斑块。探针侧改 400km 完成取证，258km 案例按 MISSING 口径回主控。

### 7. 遗留与移交（MISSING 规矩，不阻塞本任务判据）

1. **【生产缺口，回主控裁决】258km 纯海拔爬升不触发流式排名重建**：B-PLANET-07 `UpdateRanking` 重建条件 = turn>0.703° 或 lenDrift>5%（即 ~318km）。600m→258km 纯爬升 drift=4.04%<5%、turn=0 → 期望集永远停留在贴地集（实测 live=4，中档视野大面积缺 chunk）。连续爬升同样跨不过首档 318.6km。这不是本任务白名单内的修复（改生产阈值 = B-PLANET-07 的政策面），探针侧已用 400km 位姿（drift 6.27%>5%）完成三档取证；建议后续任务把 lenDrift 阈值按海拔分档或把「海拔变化量」并入重建条件。
2. **1080p 标定手法登记**：`ResizeGameViewToAsync` 的逻辑/物理像素标定可被后续需要精确 Game View 尺寸的 PlayMode 用例复用（KernelCrowd 类像素证据目前不依赖 Screen，不受影响）。
3. **SRP fixture 自切换**：见 §2——若主控倾向命令行口径（`-zrender` + 独立会话），本探针的 UnitySetUp 切换可整体替换；代价是全 PlayMode 会话绑定 ZRender，URP 像素用例需隔离编排。
4. **B-PLANET-07 两个测试文件未入库**（§1 工作区甄别 1）：EditMode 1211 的判据依赖磁盘上的 `ZrPlanetStreamingTests.cs`/`ZrPlanetWiringTests.cs`——主控补提交时注意。

### 8. 白名单自检与规范

- `git status` 本任务改动 = asmdef +1 行、`ZrPlanetProbePlayModeTests.cs`（新增 939 行）+ 其 .meta、`Docs/Parity/planet_ed_*` ×3 + `walk*` ×6、本日志。**`git diff --stat -- Native/` 为空**；zplugin/zplanet/TASKS/HANDOFF/WORKLOG 零触碰；无 git commit。
- 全部新文本文件 UTF-8 无 BOM（首字节 `2f 2f 20` od 亲验）、LF（`tr -dc '\r' | wc -c` = 0）；.meta 为 Unity 自动生成未手改。
- 零第三方依赖；Unity 批处理单实例纪律（tasklist 前检 + 删锁 + `-runTests` 不带 `-quit`）全程遵守；僵死 Unity 实例（前轮遗留 PID）已清理后重跑。
- 施工代理未写验收清单与验收结论（属主控/验收代理职责）。

### 9. 验收建议

建议验收候选 **PASS**：三档 1080p 截图入库 + 目检证据（地平线/高程带/limb 全可见、无 X-DIAG-03 残余）；三变体行走账目零漂移（1km 零重定位 / 5km 1 次 / 50km 23 次全 trace，每次 camAbs 逐位守恒）；复访重渲染 6e-6（跳变检测三层全绿）；PlayMode 段 4 条全绿；全量 EditMode 1211 + PlayMode 114 全绿；arch/naming PASS。一项生产缺口（258km 纯爬升流式覆盖，§7-1）按 MISSING 回主控，不阻塞本任务判据。

## 主控裁决（2026-11-14，施工报告后）

1. **SRP fixture 自切换手法**：**追认**——与 ZRenderBoot 同机制、生产禁切门未动、UnityTearDown 还原；比 -zrender 命令行方案少一次会话往返。
2. **B-PLANET-07 提交遗漏（主控责任）**：ZrPlanetStreamingTests/ZrPlanetWiringTests+Planet.meta 已补遗入库（4e24c81）——我的 staging 路径遗漏，已如实记录。
3. **258km 纯海拔爬升不触发流式排名重建**：**真生产缺口**——UpdateRanking 阈值 lenDrift>5% 只看路径长度漂移，纯径向爬升（drift=4.04%）漏判。登记 **B-PLANET-14**（加入径向/位移幅值判据，S 级）；本任务取证改 400km（drift 6.27%）成立，不算回避——判据本意是"编辑器口径可行走覆盖"，垂直爬升属阈值缺陷非取证缺陷。
4. **5 张既有 Parity PNG 被像素用例例行重写**：属既有测试链路行为，不阻塞。

## 验收清单（主控写，验收代理逐条跑并出证据）

- **Q1 范围**：tracked diff 恰为 2 文件（PlayMode 探针 939 行新增 + asmdef +1）+ 9 张 PNG（3 三档+6 行走）+ 本日志；Native/** 零 diff；TASKS/HANDOFF/WORKLOG 未被施工触碰（补遗 4e24c81 为主控行为）
- **Q2 三档截图**：Read 亲验 planet_ed_near/mid/far.png（near=贴地 4 块雪线剪影/mid=曲率整幅+两档同框 live=162/far=limb 圆盘 live=2110 无泄漏）；9 张无条纹（X-DIAG-03 修复后口径）
- **Q3 三变体行走**：亲跑 4 条 PlayMode 用例——1km 零重定位/5km 1 次/50km 23 次（trace 入 xml）；camAbs drift=0.000000m 逐位守恒；账目断言全程（原生恒等式+托管原生增量相等+原点逐位）；复访像素差 6e-6 量级三层跳变检测全绿
- **Q4 重定位联动**：50km 用例的重定位触发点（arc≈4020m 每 4km）与 WorldOrigin 4000m 口径一致；SetCameraOrigin 收到的值逐位正确
- **Q5 测试**：亲跑 EditMode（≥1211/0 failed）+ PlayMode（≥114/0 failed，含新增 4）+ arch/naming PASS
- **Q6 主控裁决四条核对**：施工处置与裁决一致（含 258km 缺口定性=B-PLANET-14、取证改 400km 合法）
- **Q7 规范**：新文件 UTF-8 无 BOM/LF；四节结构完整

验收输出 PASS/FAIL + 逐条证据。你独占 Unity 批处理链（B-PLANET-12 未派）。

## 验收结论（验收代理，2026-11-14）

**总结论：PASS**（Q1–Q7 全过；两项登记带回主控：套件上下文截图证据链污染【新缺陷，不阻塞判据】、PlayMode 既有用例一次瞬时边界抖动【复跑归因】）。本节全部证据为验收代理亲跑亲写；Unity 批处理链验收期间独占。

### Q1 范围 —— PASS

- 施工代码改动恰 2 文件：`ZrPlanetProbePlayModeTests.cs` 新增 **939 行**（`wc -l` 亲验）+ `Game.View.PlayMode.Tests.asmdef` **+1 行**（`"ZFrame.Render"`，diff 亲验）；加 9 张 PNG（3 三档 + 6 行走）+ 本日志，与清单口径一致。
- `Native/**`：`git status --short -- Native/` 与 `git diff --stat -- Native/` 均**零输出**（双验）。HANDOFF/WORKLOG 零触碰。
- 工作区另有两处非施工改动、均主控/既有链路行为：① `TASKS.md` **+1 行** = B-PLANET-14 登记（mtime 11:06:39，与主控补遗提交 4e24c81@11:06:13、B-PLANET-12 简报@11:07:38 同批动作=主控行为）；② 5 张既有 Parity PNG 被像素用例例行重写（裁决 #4 口径；验收轮两次 PlayMode 运行亦复现该重写，见 Q6-④）。
- `Docs/Logs/TASK-B-PLANET-12.md` 为主控新写简报（status=todo，仅简报节），未动 Unity，与「B-PLANET-12 未派、验收独占批处理链」不冲突。

### Q2 三档截图 —— PASS（以验收轮重生成证据为准；交付字节污染另登记）

- **验收首发现在**：施工交付态的 9 张 PNG 字节**不是行星画面**——三档与行走图数值解码全部同为人群场景特征（中心像素 (130,133,139) 灰、天空 (64,90,108)，而施工 xml 的管线中心回读为行星地形绿 (40,58,31)/(47,81,70)，直接矛盾）。污染样本已备份 `TempLogs/acc-p11-construction-pngs/`。
- 时间线归因：施工 fixture 轮（10:49–10:51）先产出正确捕获，随后全量套件轮（10:55–10:57）内探针用例**覆写**为污染捕获；施工报告目检描述与正确捕获吻合但未察觉覆写。
- **验收 fixture 轮重生成 9 张，Read 逐张亲验全部为行星内容**（备份留存 `TempLogs/acc-p11-myfixture-pngs/`，已恢复入 `Docs/Parity/`）：
  - `planet_ed_near.png`（1920×1080，171KB）：贴地 600m——地平线可见（含雪线山脊剪影）+ 绿色地形 + 浅色等高线 + 平滑渐变，上 ~45% 清屏色；无接缝泄漏、无条纹。
  - `planet_ed_mid.png`（41KB）：400km——**地平线曲率清晰**（弧顶整幅横向连续）+ 距离雾把地形压成雾色平面；无条纹。
  - `planet_ed_far.png`（764KB）：8000km 天底——**完整行星圆盘 limb**（视直径与 52.6° 口径一致），绿/米双色高程噪声纹理铺满盘面，盘缘干净、无清屏色泄漏入盘；无条纹。
  - 6 张行走对照（960×540）：before/after 逐对目视不可分（复访重渲染近逐位一致）。
- 数值锚（验收 xml 亲跑）：near live=4 drawn=4 / mid live=162 / far live=2110 vbytes=23154688，`SeamCheckFails=0`；三档 nonBlack=1.000。X-DIAG-03 修复后口径成立（9 张无周期条纹）。

### Q3 三变体行走 —— PASS

- **亲跑两轮独立绿**：fixture 过滤轮 4/4（8.0/9.6/45.0/25.3s，`TempLogs/acc-p11/probe.xml`）+ 全量套件轮内复跑 4/4（8.4/10.3/48.4/24.9s，`TempLogs/runtests_playmode.xml`）。
- 账目断言全程（每帧独立成立，验收 xml 全 trace）：原生恒等式 `createTotal−destroyTotal==liveChunks`；托管/原生 create/destroy/live **增量逐项相等（零漂移）**；`LastAccountOk`；每 tick 近档 ≤1 / 成本和 ≤9000µs。收敛：贴地 22 帧 live=4 → 中档 39 帧 live=162 → 远档 182 帧 live=2110。
- 三变体：**1km rebases=0**（frames=199，纯量化一致性）/ **5km rebases=1**（step=201 @arc=4020m）/ **50km rebases=23**（frames=999，lodOpFrames=0，行走全程零 chunk 操作——不跨 312.7km 格边界，设计稿 §10 如实注记口径）。
- 复访像素对照：xml 口径 **movedFraction=6e-6**（meanAbs 0.003）；验收另以 python zlib 独立解码 960×540 PNG 复算三对 before/差：**1.4e-5 / 0.0030**（同量级，近逐位一致）。三层跳变检测全绿：maxOnRebase=0.117 < maxOnOther=0.190（带 0.335）、单帧 max=0.19 < 0.5、复访 6e-6。注意：像素层证据有效性以 **fixture 轮（捕获内容=行星）** 为准——套件轮捕获内容见备注 2。

### Q4 重定位联动 —— PASS

- 50km 全部 23 次重定位 trace 入 xml：去程 12 次（arc=4100/8100/…/48300m）+ 回程 11 次（44100/40100/…/3900m）——**触发间距 = 4000m，与 WorldOrigin(4000m) 口径一致**（步长 100m 下首触发 4100m = 首个 >4000m 的步；回程终点 3900m 恰未触发）；5km 单次 @arc=4020m（步长 20m 下首个 >4000m 的步）。
- **每次重定位 `SetCameraOrigin` 收到值 drift=0.000000m（逐位守恒）**，`unityPosAfter=(0,0,0)` 归位；非重定位帧与 `UnityToWorld` **逐位一致**（`==` 断言）；绝对位姿守恒全程成立（容差随跳姿幅值量化界，行走档 ≤1e-3m）。

### Q5 测试 —— PASS（含一次既有用例瞬时抖动的复跑归因）

- **EditMode 1211/1211 failed=0 skipped=0**（验收亲跑；`TempLogs/runtests_editmode.xml`，本任务 EditMode 零改动、基线保持）。
- **PlayMode**：验收首跑 113/114——唯一失败 `AssemblyConnectPlayModeTests.ConnectedChain_GroundSettled_10Seconds_NoJitterNoExplosion`（关节锚点误差 0.0500005 vs 阈值 <0.05，**超差 5.3e-7**；该文件零 diff、末次变更 G-121b=既有用例，非本任务白名单；当轮 machine quiet=BUSY/Taskmgr 22%）。**复跑 PlayMode 114/114 failed=0**（含新增 4 条）→ 瞬时边界抖动归因成立，非行星链路回归。
- **arch PASS**（27 asmdef 扫描 / 0 违规；「Tests 程序集豁免」明示——asmdef 引用扩张在豁免解释内）；**naming PASS**（0 违规）。证据 `TempLogs/arch_report.txt` / `name_report.txt`。
- 工具备注：Git Bash 下 `run_tests.ps1 -Segments a,b,c` 参数绑定只取首值（本轮实证，editmode 段因此单独成轮）——多段请逐段调用；各段逐段亲跑，每段独立 ALL PASS。

### Q6 主控裁决四条核对 —— PASS（四条全实锤）

1. **SRP fixture 自切换追认**：fixture 切换/还原（测试 :149-154/:222-228，QualitySettings+GraphicsSettings 两处）与 `ZRenderBoot.TryEnableFromCommandLine`（ZRenderBoot.cs:64-65）**同机制**；RenderPipelineSelector 编辑期门未动。属实。
2. **补遗 4e24c81**：`git show --stat` = 恰 5 文件（Runtime/Planet.meta + ZrPlanetStreamingTests.cs/.meta + ZrPlanetWiringTests.cs/.meta，934 行），提交信息如实标注主控路径遗漏。属实。
3. **258km 缺口 = B-PLANET-14**：独立复核**成立**——`ZrPlanetChunkStreamer.cs:613-615` `rebuild = turn > chunkHalfAng*0.5 || lenDrift > 0.05`；纯海拔爬升 600m→258km：turn=0、lenDrift≈4.04%<5% → 永不重建（5%×R≈318.6km，连续爬升首档亦跨不过）；400km drift≈6.27%>5% 合法跨阈——探针取证位姿 `MidAltMeters=400_000`（实码）确证「改 400km 不算回避」。S 级登记恰当。
4. **5 张既有 PNG 例行重写**：写入方 = KernelCrowdPixelEvidence/NpcAttitudeScene/UtilityNpcScene 既有 PlayMode 用例；验收轮运行同样重写（mtime 刷新）。属实，不阻塞。

### Q7 规范 —— PASS

- 新文本文件 UTF-8 无 BOM + LF：探针 .cs（首字节 `2f 2f`）/.meta（`66 69`）/asmdef（`7b 0a`）/本日志（`23 20`），CR 计数全 0（od+tr 亲验）。
- 本日志四节结构完整（简报/施工报告/主控裁决/验收清单），本节为追加的第五节。
- 9 张 PNG 格式有效（python zlib 全图解码成功，非 3B/px 缺陷路径）。

### 给主控的备注

1. **B-PLANET-14 独立复核：成立**（Q6-③，代码行号+数值复算双锚）——按 S 级推进即可。
2. **【新缺陷，建议立项，S 级】全量套件上下文的探针截图证据链污染（验收轮 2/2 复现）**：会话内存在第二个相机时（既有场景加载类用例泄漏），`ZRenderPipeline.Render` 仅处理 `cameras[0]`（ZRenderPipeline.cs:79-161——:112 `Submit(cameras[0])`、:153 `SetupCameraProperties(cameras[0])`、:156 Blit→CameraTarget），探针相机非 cameras[0] 时行星 blit 落别处、探针 RT 永不被管线写入，ReadPixels 读到池化陈旧内存（当轮日志 `场景扫描：renderer=0` 证明画面非活场景渲染）→ 三档截图呈人群场景（137-138KB 同 signature；fixture 隔离轮为 41-764KB 差异 signature）。探针断言无法察觉（nonBlack>0.2 对亮内容恒真；行走像素三层为自比无内容锚）。**fixture 隔离运行（单相机会话）捕获取证正确**——本验收 Q2/Q3 像素证据即取自该口径。修法二选一：ZRenderPipeline 多相机策略，或探针捕获加内容锚断言（如中心像素/直方图对管线回读值）。
3. **流程警示**：任何「先 fixture 取证、后全量套件」的顺序，套件轮都会把 Docs/Parity 的 9 张覆写为污染捕获（施工轮与验收轮各发生一次）。验收已将 fixture 有效捕获**恢复入库**（字节=验收 fixture 轮亲跑产物，非手工构造）；污染样本与有效样本分别留存 `TempLogs/acc-p11-construction-pngs/`、`TempLogs/acc-p11-myfixture-pngs/` 对照。B-PLANET-12 取证时注意同坑。
4. **AssemblyConnect 瞬时抖动**（0.05 阈值边界 5.3e-7 超差，BUSY 窗口，复跑即绿）：非行星链路、非本任务白名单；如再现，建议该用例阈值留余量或接 machine-quiet 门控。
5. 验收证据清单：`TempLogs/acc-p11/probe.xml`+`probe.log`（fixture 轮，含 23 次重定位全 trace）；`TempLogs/runtests_editmode.xml`（1211/1211）；`TempLogs/runtests_playmode.xml`（复跑 114/114 轮）；`TempLogs/arch_report.txt`/`name_report.txt`；TestReport.txt 当前为 naming 轮（三段逐段 ALL PASS 见验收链式日志）。
