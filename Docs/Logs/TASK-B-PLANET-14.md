# TASK-B-PLANET-14 —— 流式排名重建阈值修复：纯海拔爬升漏判

- **轨道**：B / PLANET（B-PLANET-11 验收发现的生产缺口，S 级）
- **状态**：doing
- **派发**：主控，2026-11-14
- **缺陷**：`ZrPlanetChunkStreamer.UpdateRanking`（:613-615 附近）阈值 `turn>0.703° || lenDrift>5%` 只看路径长度漂移——**纯海拔爬升 258km 时 lenDrift=4.04%<5% 永不重建**，期望集停留贴地 4 块（B-PLANET-11 实测 live=4，中档视野缺 chunk；改道 400km drift=6.27% 才触发）

## 任务简报（主控写）

### 1. 做什么

1. **阈值判据扩展**：在 `lenDrift`/`turn` 之外加入**位移幅值判据**——如 `|Δpos_world| > max(500m, 0.02×chunk弧长)`（具体常数施工定，写明依据：600m→258km 爬升必须触发、贴地微动不触发）——或把 lenDrift 的分母口径改为含径向分量的总位移（选型写报告）
2. **锚定测试**（EditMode）：
   - 纯海拔爬升 600m→258km → 重建触发（本缺陷的复现用例转绿）
   - 贴地微动（<500m 漫游）→ 不触发（防过敏感）
   - 既有阈值用例（B-PLANET-07 的 3 条边界用例）不破坏
3. **回归**：EditMode ≥1223/PlayMode ≥114/arch/naming 全绿；B-PLANET-11 的 50km 三层跳变检测不受影响（如需复跑抽查）
4. **白名单**：ZrPlanetChunkStreamer.cs（+.meta 如触发）、其测试文件、本日志；Native/** 不碰；其余不碰

### 2. 铁律

约法三章；施工不 commit、不改看板；UTF-8 无 BOM/LF；零第三方依赖

### 3. 完成判据

- 纯爬升场景重建触发（复现用例绿）；贴地微动不触发；既有用例全绿；常数有依据

## 施工报告（施工代理写，2026-11-14）

### 1. 交付概览

**通过行（全部亲跑，证据 xml/报告本次亲写）**：

```
EditMode: total=1235 passed=1235 failed=0 skipped=0（基线 1223 → +12 只增不减；证据 TempLogs/runtests_editmode.xml）
PlayMode : 4 轮全量套件 113/114——唯一失败 AssemblyConnectPlayModeTests.ConnectedChain_GroundSettled_
           10Seconds_NoJitterNoExplosion（既有刀锋态抖动，与本任务无语义路径，A/B 归因矩阵见 §6；
           本任务白名单不含该文件 → MISSING 回主控）；4 条行星探针用例 4 轮全绿
arch     : PASS exit 0（TempLogs/arch_report.txt，亲跑轮）
naming   : PASS exit 0（违规 0；TempLogs/name_report.txt，亲跑轮）
```

**文件清单（全部在白名单解释范围内；git diff --numstat 实测）**：

| 文件 | 变更 | 内容 |
|---|---|---|
| `UnityProject/Assets/ZRender/Runtime/Planet/ZrPlanetChunkStreamer.cs` | +71/−2（765→834 行） | UpdateRanking 加第三条重建判据（位移幅值）；新增常数对 RebuildMinMoveMeters=500/RebuildMoveChunkFraction=0.02 + 纯函数 `RebuildMoveThresholdMeters(uint)`（internal，测试共用同口径）；新增 public 诊断计数 `RankingRebuildCount`；类头/方法注释更新 |
| `UnityProject/Assets/ZRender/Tests/ZrPlanetStreamingTests.cs` | +429/−2（478→905 行） | 12 条锚定测试（§4）+ 助手改造（AnchorUv/CameraOverAnchor 参数化 cells、GridConfig、Converge、TangentTowardX）+ 头注释第五类 |
| `Docs/Logs/TASK-B-PLANET-14.md` | 本节追加 | 本日志 |

**本任务未触碰**：`Native/**`（`git diff --stat -- Native/` 为空）、zplugin/zplanet、TASKS/HANDOFF/WORKLOG、无 git commit。

**工作区甄别（非本任务改动，如实登记）**：

1. `Docs/Parity/` 13 张 PNG 为 PlayMode 套件运行的**例行重写**（B-PLANET-11 主控裁决 #4 先例）。亲验（python zlib 解码）：planet_ed_near/mid/far + walk50k_before 当前工作区中心像素均为 (130,133,139)/天区 (64,90,108)——与 B-PLANET-11 验收登记的**污染 signature 一致**（B-PLANET-15 多相机缺口仍在途，套件轮捕获污染复现；HEAD 态的 planet_ed_mid 本身亦为该 signature——污染早于本任务）。行星 PNG 证据有效性以 **fixture 隔离轮**为准（验收 Q2/Q3 既有口径），本任务不触碰该批文件。
2. `Tools/version.json` / `UnityProject/ProjectSettings/ProjectSettings.asset` / 其余 4 张 crowd/utility PNG：任务开始前即为改动态（前序会话遗留），本任务零触碰。
3. `Tools/Probes/out/*` 未跟踪在途产物：前序遗留，零触碰。
4. 本任务诊断脚本留存 `TempLogs/task-b-planet-14/`（geom_probe.py 几何复算 + 各轮证据快照 playmode_run{2,4,6}_fail / run5_baseline_pass / filter_with_diff / mini_diff 等；TempLogs 不入树）。

### 2. 选型（简报 §1-1 第 1 点：加位移幅值判据 **或** 改 lenDrift 口径）

**选型：加第三条判据 `|ΔcamRel| > max(500m, 0.02 × chunk 弧长)`（简报建议式），保留既有 turn/lenDrift 两条不动。**

"改 lenDrift 口径"案为何不可行：lenDrift = |camLen−lastLen|/lastLen，分母是**相机离行星中心的距离**（≈R=6371km 量级）。纯爬升 258km 的缺陷场景里，分子（径向位移 257.4km）**本身就是含径向分量的总位移**——换分子口径得到的是同一个 4.04%；换分母只是把钝感换个刻度（要修好必须把分母降到 ≪R 的参照量，例如 chunk 弧长——那等价于位移幅值判据的相对化外衣，不如绝对幅值直白）。故选简报第一案。

**判据语义**：位移 = 自**上次排名重建**起的累计位移（_lastCam 仅在重建时更新）——重建响应**累计**位移而非单帧跳变（连续爬升跨阈由 T9 钉住）。lenDrift 判据在新判据下对合法轨道（离星距离 ≥ R）被完全支配（0.05×R≈318.6km ≫ 全 cells 域阈值上界 200.15km），仅在对心简并态（lastLen < R，如离星 100km 处 +10km 径移：lenDrift=10%>5% 而 |Δ|=10km<阈值）可达——**保留为简并态兜底**（B-PLANET-07 原判据不删，既有用例全部原样通过）。

### 3. 常数依据（简报：具体常数施工定，写明依据）

**形式**：`moveLimit = max(RebuildMinMoveMeters=500, RebuildMoveChunkFraction=0.02 × R·(π/2)/cells)`，其中 chunk 弧长 = 一块 chunk 的赤道地表弧长（cells=32 时 312,735.7m——与 B-PLANET-11 报告引用的"chunk 弧长 312.7km"同式同值）。

**阈值表（RebuildMoveThresholdMeters 亲算，T1-T3 钉死）**：

| cells | 阈值 (m) | 主导项 |
|---|---|---|
| 1 | 200,150.87 | 2% 弧长 |
| 2 | 100,075.43 | 2% 弧长 |
| 8 | 25,018.86 | 2% 弧长 |
| 32（生产） | **6,254.71** | 2% 弧长 |
| 256 | 781.84 | 2% 弧长 |
| 400 | 500.38 | 2% 弧长（恰高于地板） |
| ≥401 | 500.00 | 地板 |

**锚点一（缺陷复现，必须触发）**：600m→258km 纯爬升位移 257.4km——阈值全域上界 200.15km（cells=1 极端粗格）< 257.4km → **任意合法格数必触发**；生产格数（32）下 6.25km 即触发（爬升到 ~6.9km 高度时），不再是旧判据的 318.6km 永不触发。
**锚点二（防过敏感，必须不触发）**：贴地微动 <500m（任意方向）< 地板项 500m（严格不等式 `moveSq > moveLimit²`，恰等不触发——与 DecideLodLevel 严格边界同语义）→ **任意格数永不触发**；且 500m 爬升使地平线角增长 ≤ acos(R/(R+500)) ≈ 0.0125 rad < CullMarginRad(0.05)——期望集意图不受扰。
**0.02 的敏感性依据（陈旧量界）**：重建间隔内排名的陈旧量 = 未重建径向位移 T 造成的地平线角增长 ΔhA = acos(R/(R+T))（低空最坏）。对 T = 0.02·R·π/(2·cells)：**ΔhA < chunkHalfAng + CullMarginRad 对全部 cells 1..4096 成立**（全程 sweep 亲验，最紧 slack = 0.030014 rad @ cells=39；cells=32：0.044293 < 0.074544）——陈旧量始终落在既有剔除包络（半块角+裕度）内，流式覆盖不劣化。对照：旧判据（5% lenDrift）允许陈旧量至 acos(R/(R+318.6km)) ≈ 0.3155 rad ≫ 0.0745——正是缺 chunk 缺陷的机理。该界已固化为回归锁定测试 T12（改任一常数破坏此界即红）。

### 4. 锚定测试清单与数字（简报 §1-2；EditMode 12 条，全部首轮 Passed，证据 runtests_editmode.xml 逐条 result=Passed）

| # | 用例 | 断言与数字 |
|---|---|---|
| T1 | `RebuildMoveThreshold_DefaultGrid_IsTwoPercentChunkArc` | cells=32 阈值 = 6254.71m = 2%×312735.7m；地板不生效 |
| T2 | `RebuildMoveThreshold_FloorActive_OnDenseGrids` | cells=400→500.38（地板不生效）/401→恰 500.0（接管）/512、4096→500.0 |
| T3 | `RebuildMoveThreshold_MonotonicAcrossGrids_AndClimbAnchor` | 全域单调不增 + ≥500m；cells=1→200150.87 < 257400（锚点一与格数无关） |
| T4 | `Tick_PureAltitudeClimb600mTo258km_TriggersRebuild` | cells=8 贴 chunk00 锚向：重建计数 1→2（**缺陷锚点**：turn=0、lenDrift=4.04%<5% 旧判据全静默，旧代码必红）；期望集 5→25（几何复算），收敛后 Created/Desired 增长；账目恒等 |
| T5 | `Tick_PureAltitudeClimb_ProductionGrid_GroundSetExpands` | **生产格数+生产默认配置缺陷复现**：face0 中心向贴地 600m 期望集**恰 4 块**（B-PLANET-11 实测 live=4 同口径复现）→ 爬升 258km 重建 1→2 → 收敛期望集 ≥100（复算 112）；账目/托管原生对账 |
| T6 | `Tick_GroundTangentialMicroMove499m_DoesNotRebuild` | 切向 499m：三判据全静默；**零操作**、活跃数/Create 计数稳定（锚点二切向臂） |
| T7 | `Tick_GroundRadialMicroMove499m_DoesNotRebuild` | 径向 499m（最贴近原缺陷方向的反向用例）：同上（锚点二径向臂） |
| T8 | `Tick_DisplacementThresholdBoundary_IsStrict` | 阈值×(1−1e-6) 不触发 / ×(1+1e-6) 触发——严格不等式（恰在阈值不切换，DecideLodLevel 同边界语义） |
| T9 | `Tick_ContinuousClimb_AccumulatesToThreshold` | +20km 不过阈（基线冻结）→ 累计 +26km 跨阈触发——**累计语义**（缺陷第二形态"连续爬升跨不过首档"） |
| T10 | `Tick_TangentialDisplacement_RebuildFiresWithoutDesiredSetChange` | 纯切向 30km（e1 去径向分量单位向量）：turn=0.0047<0.049、lenDrift=1.1e-5（**旧判据全静默，旧代码必红**）、\|Δ\|=30000>25018.86 → 触发；几何复算期望集**与迟滞带均不变**、活跃块距离变化不跨 810/990km 迟滞带 → **重建 tick 零操作**——RankingRebuildCount 是触发唯一观测量（诊断计数存在依据） |
| T11 | `Tick_StationaryCamera_RebuildsExactlyOnce` | 相机不动 32 tick：重建恰 1 次（稳态零重建——噪声免疫；逐 tick 重建会常驻帧预算） |
| T12 | `RebuildThreshold_StalenessBound_HoldsAcrossGrids` | §3 陈旧量界 sweep cells 1..4096 全过（常数选取依据的回归锁定） |

既有 3 条边界用例（`DecideLodLevel_FarSide/NearSide/HysteresisBandKeepsPreviousTier`）与全部既有用例原样通过（EditMode 1235 = 1223+12，0 失败）。
几何复算工具：`TempLogs/task-b-planet-14/geom_probe.py`（CubeSphereDir/锚点表/锥判据的 Python 逐式镜像，与 C# double 域同源；断言边界裕度 ≥0.19° ≫ libm 噪声，确定性成立）。

### 5. B-PLANET-11 50km 三层跳变检测影响核查（简报 §1-3）

新判据下 50km 行走（位移 50km > 阈值 6.25km）**排名重建必然触发**（约每 6.25km 一次，去/回程各 ~8 次）——这是修复生效的直接体现。对 B-PLANET-11 判据的影响（4 轮探针用例全绿，trace 入 xml）：

- **lodOpFrames=0 保持**：期望集在 0.449° 行走弧内无块跨界（几何复算：锥边 5.057°，最近锥外块 5.571°，行走后 ~5.12° 仍在外）→ 全程零 chunk 操作，B-PLANET-11 报告"账目冻结态"数字口径不变；
- **三层跳变检测全绿**：run2 trace walk50k——revisit movedFraction 0.009、maxOnRebase=0.0026 < maxOnOther=0.0060（带自洽）、单帧 max=0.0060 < 0.5；**rebases=23 与 B-PLANET-11 报告逐项一致**（重定位触发与流式判据解耦）；
- 新建/销毁块（若有）均在可见地平线之外（预取锥 5.06° ≫ 地平线 0.786°，曲率遮蔽 ~12.6km 级 ≫ 地形幅值）——像素连续性不受扰。

### 6. PlayMode 唯一失败的归因（A/B 矩阵，全部亲跑）

失败用例：`AssemblyConnectPlayModeTests.ConnectedChain_GroundSettled_10Seconds_NoJitterNoExplosion`（关节锚点误差 0.0500005335 vs 阈值 0.0500000007，**超差恒为 5.3e-7**）。该测试 = 500 步 `Physics.Simulate(0.02f)` 同步仿真，与帧率/渲染无关，但 PhysX 多线程求解在机器负载下非逐位确定——误差在 0.05 阈值上为刀锋态。

| 轮次 | 代码 | 范围 | ConnectedChain | 误差值 |
|---|---|---|---|---|
| run1/run2/run4/run6 | 本任务 diff | 全量套件 | **FAIL ×4** | 0.0500005335（四次逐位相同） |
| run3/run5 | HEAD（stash 本任务 diff） | 全量套件 | PASS ×2 | — |
| filter | 本任务 diff | **单 fixture** | PASS 5/5 | — |
| mini | 本任务 diff | **probes+fixture 两 fixture** | PASS 9/9 | — |
| B-PLANET-11 验收轮 | **无本任务 diff** | 全量套件 | **FAIL ×1** | 0.0500005（同一 5.3e-7）→ 复跑 PASS |
| G-177/B-PLANET-12 | 无本任务 diff | 全量套件 | PASS（历史绿） | — |

判读：

1. **无语义路径**（fixture 单独 5/5 + probes 配对 9/9 全过）——本 diff 不改该 fixture 可达的任何状态（行星流式不在其场景内实例化；探针 fixture 行为差 = Walk50km 时长 +1.0~1.7s，来自 ~16 次排名重建的帧节奏扰动，是**修复生效的固有成本**）。
2. **失败形态先于本任务存在**：B-PLANET-11 验收轮在无本 diff 时记录了**同一用例同一 5.3e-7 超差**（其验收建议原文："该用例阈值留余量或接 machine-quiet 门控"）。
3. 但 4/4 vs 2/2 的套件内相关性真实存在（本 diff 的 ~1-4s 套件时序再分配与刀锋态的互动），机制不透明——**如实回主控**。本机 quiet 检查全程 BUSY（Taskmgr 24~31%），负载无法区分两组。
4. **MISSING（白名单外，回主控裁决）**：`UnityProject/Assets/ZFrame/Tests/PlayMode/AssemblyConnectPlayModeTests.cs:259` 阈值留余量（如 0.05 → 0.051，误差量级 5.3e-7）或接 machine-quiet 门控——B-PLANET-11 验收建议的落地。文件不在本任务白名单，未动。
5. 全量套件其余 113 条（含 4 条行星探针）在 4 轮中全部通过；证据快照留存 `TempLogs/task-b-planet-14/playmode_run{2,4,6}_fail_assemblyconnect.xml`、`playmode_run5_baseline_pass.xml`。

### 7. 遗留风险（如实登记，不阻塞缺陷修复判据）

1. **AssemblyConnect 刀锋态抖动（§6）**：PlayMode "≥114 全绿"判据在本机当前态下 4 轮未达成，唯一失败项与本任务无语义路径且先例早于本任务；归主控裁决（修法 = 阈值余量/门控，文件白名单外）。
2. **lenDrift 判据保留**（§2）：合法轨道下被新判据支配（永不先触发）——保留是零行为风险的冗余 + 简并态兜底；若后续任务认为冗余碍读，可在删除时以 T8（新判据边界）+ 简并态分析复核。
3. **RankingRebuildCount 为 public 诊断面**：生产 API 面新增一个只读 long 属性（与 CreatedTotal/DesiredCount/DeferredWorkCount 同模式）——naming/arch PASS，无序列化/Player 面影响。
4. **阈值与 cells 联动**：阈值随 CellsPerEdge 收窄（稠密格下地板 500m 生效）——cells=4096 极端配置下陈旧量界由 CullMarginRad 主导（0.0125 < 0.05 ✓），已入 T12 sweep 锁定。
5. **B-PLANET-15（多相机管线缺口）在途**：全量套件轮的行星 PNG 捕获污染复现（§1-1 亲验 signature）——与本任务无关，证据有效性以 fixture 隔离轮为准。

### 8. 白名单自检与规范

- `git status` 本任务改动 = 白名单 2 文件（+71/−2、+429/−2）+ 本日志追加；`git diff --stat -- Native/` 为空；TASKS/HANDOFF/WORKLOG 零触碰；无 git commit。
- 17 个 tracked 改动文件中 13 张 Docs/Parity PNG = 套件运行例行重写（§1-1，B-PLANET-11 裁决 #4 先例）、version.json/ProjectSettings.asset = 任务前既有改动态——均非本任务施工。
- 新旧文本 UTF-8 无 BOM（两 .cs 首字节 `2f 2f 20` od 亲验）、CR 计数 = 0（tr 亲验）；零第三方依赖；Unity 批处理单实例纪律（tasklist 前检 + run_tests.ps1 内建删锁/Git Bash 启动/-runTests 不带 -quit）全程遵守。
- 施工代理未写验收清单与验收结论（属主控/验收代理职责）。

### 9. 验收建议

**建议验收候选 PASS（带一项回主控裁决）**：缺陷修复本体全绿——EditMode 1235/1235（基线 1223 + 12 锚定测试，首轮全绿、只增不减）、两锚点（258km 纯爬升必触发 / 贴地微动 <500m 不触发）+ 边界严格性 + 累计语义 + 切向判别 + 稳态零重建 + 陈旧量界共 12 条钉死；arch/naming PASS；B-PLANET-11 50km 三层跳变判据不受影响（§5，4 轮探针全绿、lodOpFrames=0 保持）。一项 MISSING 回主控：AssemblyConnect 刀锋态抖动（§6）——先例早于本任务、与本任务无语义路径（A/B 矩阵 8 轮亲跑），修法在白名单外文件；主控可 (a) 追认 PlayMode 113/114 归因并另行派发阈值余量修正，或 (b) 授权本任务追加修正后复跑终验。

## 主控裁决（2026-11-14，施工报告后）

1. **选型确认**：第一案（位移幅值判据）——"改 lenDrift 口径"不可行的论证成立（分子口径循环）。常数 0.02×弧长与 500m 地板的 acos 陈旧量界分析**接受**。
2. **PlayMode 113/114 MISSING 裁决**：AssemblyConnect 刀锋态（5.3e-7 超差）→ **派 X-FLAKY-01 微修**（容差 0.051+四层依据入码，负载窗连跑两轮 114/114 已实证）；本任务验收的 PlayMode 判据以"X-FLAKY-01 落地后全绿"为准，验收代理捎带核对其落地与两轮数字。
3. 过压形态签名（maxSpeed 语义发散）登记为运维知识（HANDOFF 待办区），不构成本任务事项。

## 验收清单（主控写，验收代理逐条跑并出证据）

- **B1 范围**：tracked diff 恰为 ZrPlanetChunkStreamer.cs(+71/−2)/ZrPlanetStreamingTests.cs(+429/−2)/本日志；Native/** 零 diff；TASKS/HANDOFF/WORKLOG 未被施工触碰（X-FLAKY-01 的 AssemblyConnectPlayModeTests.cs 为并行微修，归其任务）
- **B2 判据实现**：读码核对第三判据 `moveSq > moveLimit²`、常数对（RebuildMinMoveMeters=500/RebuildMoveChunkFraction=0.02）、纯函数 RebuildMoveThresholdMeters、RankingRebuildCount 诊断面
- **B3 锚定测试**：亲跑 12 条新增用例——T4 纯爬升触发（旧代码必红的缺陷锚点）/T5 生产格数贴地 4 块→爬升 ≥100/防过敏感×2/阈值边界±1e-6/累计语义/切向判别零操作重建/稳态零重建/T12 陈旧量界锁定
- **B4 回归**：亲跑 EditMode **1235/1235**（1223+12）/arch/naming PASS
- **B5 捎带 X-FLAKY-01（主控委托）**：①diff 核对（0.051+四层依据注释）②亲跑 PlayMode 全套一轮 114/114（负载或静机均可，与 X-FLAKY-01 两轮互证）③结论单独给出（主控据此单独 commit）
- **B6 50km 三层跳变**：亲跑 Walk50km 用例——重建现会触发（50km>6.25km 阈值）但 lodOpFrames=0、三层全绿、rebases=23 不变
- **B7 规范**：文本文件 UTF-8 无 BOM/LF；四节结构完整

验收输出 PASS/FAIL + 逐条证据；X-FLAKY-01 单独结论。你独占 Unity 批处理链。
## 验收结论（验收代理写，2026-11-14）

**总结论：PASS**（B1-B7 全过；以下证据全部本验收代理亲跑亲写，Unity 批处理链独占，证据 xml/报告均为本轮新写）

- **B1 范围 PASS**：`git diff --numstat` 实测 ZrPlanetChunkStreamer.cs +71/−2、ZrPlanetStreamingTests.cs +429/−2、本日志（新增未跟踪）；X-FLAKY-01 的 AssemblyConnectPlayModeTests.cs +17/−2 为并行微修归其任务；`git diff --stat -- Native/` 为空；TASKS/ Docs/HANDOFF.md Docs/WORKLOG.md 零 diff。其余改动态（14 张 Docs/Parity PNG 套件例行重写、Tools/version.json、ProjectSettings.asset 任务前既有）与前序登记一致，非本任务施工。
- **B2 判据实现 PASS**：读码核对——UpdateRanking 第三判据 `moveSq > moveLimit * moveLimit`（严格不等式）；常数对 `RebuildMinMoveMeters = 500.0` / `RebuildMoveChunkFraction = 0.02`；纯函数 `RebuildMoveThresholdMeters(uint) = max(500, 0.02 × R·(π/2)/cells)`（R = NativePlanetRadiusMeters = 6.371e6）；`RankingRebuildCount`（public long，重建块尾自增，诊断面成立）；`_lastCamX/Y/Z` 仅在重建路径（:717-719）写入——累计位移语义与施工报告 §2 一致。
- **B3 锚定测试 PASS**：diff 新增方法恰 12 条（T1-T12 与施工报告 §4 逐条对应）；EditMode 全套亲跑轮 12 条逐条 result=Passed（TempLogs/runtests_editmode.xml，16:09 本轮新写）；另亲跑 fixture 抽取轮（`-testFilter ZFrame.Render.Tests.ZrPlanetStreamingTests`，Git Bash 启动无 -quit，UNITY_EXIT_CODE=0）：fixture 29/29，12 条逐条 Passed（TempLogs/acc_b3_fixture.xml，16:30 本轮新写）。
- **B4 回归 PASS**：EditMode total=1235 passed=1235 failed=0 skipped=0（基线 1223 + 12 只增不减；本轮亲写 xml）；arch PASS exit 0、naming PASS exit 0（TempLogs/arch_report.txt / name_report.txt，16:10 本轮新写）；PlayMode 114/114（见 B5②验收轮3）。
- **B5 捎带 X-FLAKY-01 PASS**（独立结论见 TASK-X-FLAKY-01.md「验收结论（捎带）」）：①diff 核对——0.051f 门限 + 四层依据注释全部入码，B-PLANET-11 §202 档案出处抽查一致；②亲跑 PlayMode 全套三轮（下表）；③X-FLAKY-01 单独结论 PASS。
- **B6 50km 三层跳变 PASS**：walk50k 三轮全绿，trace 逐项核对——**rebases=23 三轮一致且与 B-PLANET-11 报告逐项一致**；**lodOpFrames=0 三轮保持**（新判据下 50km 行走排名重建必然触发但不产生任何块操作——行走弧内期望集无跨界，与施工报告 §5 论证一致）；三层连续性全绿（revisit movedFraction 0.001437/0.015085/0.014126 < 限、maxOnRebase 0.002711/0.002671/0.002253 < rebaseBand 0.25、单帧 max 0.006902/0.007292/0.007138 < 0.5）；账目 999 帧恒等（createTotal=destroyTotal=0、终态 live=4）。
- **B7 规范 PASS**：两 .cs + X-FLAKY-01 .cs + 两日志首字节 2f2f20 / 232054（UTF-8 无 BOM）、CR 计数 0（LF-only）；两日志四节结构完整。

**PlayMode 三轮明细与判读（B5② / B4 的 PlayMode 判据）**：

| 轮 | 结果 | 唯一失败（若有） | 失败断言点 | 当轮负载（quiet check） |
|---|---|---|---|---|
| 验收轮1 | 113/114 | ConnectedChain | maxSpeed=10.5035982（:275）——**锚点 0.051 本轮通过** | Taskmgr 30.8% 爆发（≈5/16 核） |
| 验收轮2 | 113/114 | ConnectedChain | maxSpeed=10.5035982（:275，与轮1 逐位同值）——**锚点 0.051 本轮通过** | Taskmgr 20% + ZCode 31.2%（含验收侧自造负载，如实登记） |
| 验收轮3 | **114/114** | — | — | **QUIET**，验收侧零并发 |

判读：(1) 0.051 锚点余量在两轮负载轮中均通过——修复本体正面验证（旧 0.05 门限在同等负载形态下即红，A/B 矩阵见 X-FLAKY-01 施工报告 §1）；(2) 两轮 maxSpeed 失败与 X-FLAKY-01 §3-2/§5-1 登记的过压形态签名**同值同点**（10.5036 / :275），为 ≥4 核级持续负载下 PhysX 线程饥饿的确定性轨迹（三轮逐位同值佐证其确定性），非两任务白名单内缺陷、非阈值余量可修对象（放宽 maxSpeed = 真语义放宽，禁止）；(3) PlayMode "≥114 全绿" 判据由验收轮3（quiet）114/114 达成，与 X-FLAKY-01 两轮负载窗 114/114 互证。

**给主控的备注**：(a) maxSpeed 过压签名建议留 HANDOFF「已知偶发」区并考虑 PlayMode 物理用例静机门控（运维项，不阻塞本验收）；(b) 验收期间机器 Taskmgr GUI 常驻 20-31% 爆发，两轮红均落其窗口内、quiet 轮全绿互为佐证；(c) 工作树待提交清单与两任务施工报告一致（B-PLANET-14 两 .cs + 本日志、X-FLAKY-01 一 .cs + 其日志），.cs↔.meta 无新增配对需求，PNG/version.json/ProjectSettings.asset 为非施工改动态请按目录甄别。
