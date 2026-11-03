# TASK-A-R9-08 —— Volume 权重渐变（画面风格切换动画，方案 §6.4 承诺）

状态: doing | 派发: 2026-10-31 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：方案 §6.4 承诺的"运行时 Volume 权重渐变接口"——场景/风格切换时 Volume profile 不硬切而是**权重渐变**（0.8s 等功率交叉），纯 C# 核可测 + PlayMode 像素证据（延续 A-R9-04/05 口径）。

**设计要点**：
1. **纯 C# 核**（`ZFrame/View/Pipeline/VolumeCrossfade.cs`，可 EditMode 测）：
   - `VolumeCrossfadeState`：`Start(from,to,duration)`→`Tick(dt)` 返回 (wFrom,wTo)——**等功率交叉**（复用 A-M2-02 AudioService 的 CrossfadeState 语义：a²+b²≡1 或同款曲线，报告选型）；完成后停在 (0,1)；`Abort` 立即落定
   - 双 profile 权重语义：URP Volume 的 `weight`（0..1）作用于**同一 Volume 组件换 profile** 不行（sharedProfile 换即全局）——实现路径：**两个 Volume 组件叠放**（A=旧 profile weight=1-x，B=新 profile weight=x，同一 GameObject 同优先级叠加），PipelineManager.SetTier 现在是"单 Volume 换 sharedProfile"（A-M2-03 实现）——本任务把 SetTier 的 profile 切换升级为双 Volume 渐变（**保持 SetTier 对外语义不变**，换档位时自动渐变；新增独立接口 `CrossfadeToProfile(profile, duration)` 供场景风格用）
2. Host 适配：`VolumeCrossfadeHost`（ITickable 经 ITickQueue，AudioService 模式）；Shutdown 恢复目标 profile 全权重
3. 与既有协作：PipelineManager.SetTier 改造需**既有用例零改动通过**（A-M2-03 的 SetTier 三轮验证用例）——渐变期间 GetTier 立即返回新档（语义：档位是意图，渐变是表现）
4. 可证伪 PlayMode（延 A-R9-04 像素口径）：
   - 渐变中帧序列像素**逐步变化**（N 采样点单调趋近目标，非一步跳变）——与硬切对比：硬切 1 帧到位、渐变 ≥3 帧过渡（帧数证据）
   - 渐变完成像素=直接设置目标 profile 的像素（一致性别名）
   - Abort 行为
5. 单测（EditMode）：曲线端点/中点、Abort、完成停稳、Enabled 协同
6. 自测：EditMode+PlayMode 全绿（基线 747/16 只增不减）+ Validate exit 0（Unity 纪律）

**约束**：白名单 `Assets/ZFrame/View/Pipeline/**`、`Tests/**`、`ProjectSettings`（如需，报告单列）、本日志；**既有 SetTier 用例零改动**；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0。

**交付物**：纯核+双 Volume 渐变改造+像素证据+单测+施工报告四节。

## 施工报告（施工代理写）

**日期**: 2026-10-31 ｜ **施工**: general-purpose 施工子代理 ｜ **自测**: EditMode 771/771 + PlayMode 18/18 + arch/config/naming 三校验器 exit 0（全走 `Tools/run_tests.ps1`，单实例/删锁/无 -quit/轮询 results.xml 纪律由脚本内建）。

### ① 设计与实现（按简报四要点）

**1. 纯核 `VolumeCrossfadeState`**（`Assets/ZFrame/View/Pipeline/VolumeCrossfadeState.cs`，新增，148 行）

- **曲线选型（简报点名的报告项）**：等功率余弦交叉——`wFrom = cos(t·π/2)`、`wTo = sin(t·π/2)`，t 为 0→1 归一进度。不变量 **a²+b²≡1** 全程成立，端点精确 (1,0)→(0,1)，中点双方各持 cos45°≈0.7071。与 A-M2-02 `AudioService.CrossfadeState` 语义 1:1 同款。备选被否：线性 (a+b=1) 会在中段压低叠加强度且破坏音频参照语义；smoothstep 无正交不变量。**实测**（EditMode `Curve_KeepsEqualPowerInvariant_AcrossTheWholeFade`）：20 采样点 a²+b² 偏差 ≤1e-5，wTo 单调升、wFrom 单调降。
- **API 对简报草图的两点修正**（如实记录）：
  - 文件名：简报写 `VolumeCrossfade.cs`，按 file-conventions §2「一个类型一个文件，文件名=类型名」落为 `VolumeCrossfadeState.cs`；
  - 签名：简报草绘 `Start(from,to,duration)`——`from/to` 若是 profile 引用则纯核必然依赖 UnityEngine（V4 禁止）。实现为 **`Start(duration, startPhase01)`**：核心只动画「from/to 两个角色槽」的权重对，`from/to` 到底绑到哪个物理 Volume 是 manager 的簿记。`Tick(dt)→(wFrom,wTo)`、完成后停在 (0,1)、`Abort()` 立即落定——三条硬性语义逐条落实。
- **Abort 语义判定**：「立即落定」实现为**落到目标 (0,1)**（档位/风格意图早已切换，表现层中止即提交意图，绝不冻结在半混合态）。`StartPhase01>0` 的重入进度是给 manager 中断续基用的接缝。
- **零 UnityEngine 取证**：全文件无任何 `using` 指令（第 1 行即 `namespace`），唯一外部依赖 `System.Math`（`CurrentWeights` 内，VolumeCrossfadeState.cs:141-142）。

**2. 双 Volume 叠放改造**（`PipelineManager.cs`，+274/-15）

- 结构：`~GradeVolume` 子物体上**同 GameObject 叠放两个 Volume 组件**（URP `Volume` 无 `[DisallowMultipleComponent]`，core 14.0.12 `Volume.cs:11-17` 实证），同为 global/priority 10。槽 A=`GradeVolume`（A-M2-03 原槽），槽 B=`GradeVolumeAlt`（常驻 weight=0；`VolumeManager.cs:469-470` 对 weight≤0 的 Volume 整体跳过——**精确 no-op**，Pixel 证据"snap back 与从未渐变帧 diff=0.0000"即其像素级实证）。
- **SetTier 自动渐变**：`ApplyTier` 第 2 步由"换 sharedProfile"升级为 `ApplyGradeProfile(profile, DefaultCrossfadeDuration=0.8f)`。**GetTier 对外语义不变**：`CurrentTier` 与 `CurrentGradeProfile` 在同一次 SetTier 调用内**立即**指向新档（意图先行，渐变只是表现）——A-M2-03 的 `PipelineManagerTests` 全部 9 条用例**零改动通过（git diff 该文件 = 0 行）**，`PipelineVerification.VerifyLiveTierSwitch` 的批处理校验（SetTier 后立刻读 profile 名）同因"意图先行"继续成立。
- **`CrossfadeToProfile(profile, duration)` 独立接口**：场景风格用。不动 QualitySettings/持久化/TierChangedEvent，仅渐变画面；duration≤0 = 硬切；与当前意图同 profile = no-op（同音频参照"已在目标轨即无事发生"）。
- **中断续基（超简报的补充设计，两分支均有 EditMode 用例钉住）**：渐变中途换目标时按**主导侧连续**重基——
  - from 侧主导（早中断）：from 槽 profile 与权重轨迹原样保留，新 profile 放入较弱的 to 槽（内容替换发生在小权重侧），时钟从当前相位续跑；
  - to 侧主导（晚中断）：**旧目标升级为新 from**（角色互换，权重轨迹连续），新 profile 放入另一槽，时钟镜像续跑（`1-phase`；cos(π/2−θ)=sin(θ) 保证被保留槽权重无跳变）。
  两分支的突变都被压在 ≤cos45°≈0.7071 的弱侧，且 `StartGradeFade` 末尾立即回写物理权重，状态与组件永不发散（PipelineCrossfadeTests 两分支各断言 4 个连续性点 + 收敛终态）。
- **完成停稳 + 归一（`RenormalizeGradeVolumes`）**：渐变完成的同一 tick 把定妆 profile 移回槽 A@1、B@0——像素级无差别（同 GO/同优先级/同帧内两次写先于渲染），但保住了 **A-M2-03 的"静止态=单 Volume"读数**：`FrameGovernorHost` 捕获/恢复 `GradeVolume.weight` 的既有行为不变（其 10 条既有用例零改动全绿即证）。
- **Shutdown 恢复**：`VolumeCrossfadeHost.Shutdown()`（含 OnDestroy）→ `manager.AbortGradeCrossfade()` → `SnapGradeProfile(_currentProfile)`：**目标 profile 全权重落定**（简报设计点 2 原文）。理由：host 一走没人再 Tick，冻结在半混合态等于画面永久半旧。EditMode 用例 `VolumeCrossfadeHost_PumpsTheManager_AndShutdownSettlesTargetFullWeight` 在渐变中途 Shutdown 并断言落定。

**3. Host 适配 `VolumeCrossfadeHost`**（`Assets/ZFrame/View/Pipeline/VolumeCrossfadeHost.cs`，新增）

- AudioService/FrameGovernorHost 同款挂载：`RuntimeInitializeOnLoadMethod(BeforeSceneLoad)` 自举 `~VolumeCrossfadeRoot`（DontDestroyOnLoad）→ `Initialize()` 幂等（`GameRoot.EnsureInstance` + 未 boot 则 `root.Initialize()`，绑定**返回的 root** 而非静态——FrameGovernorHost 的既知坑）→ `Services.Get<ITickQueue>().Register(this, TickStage.View)`。**不实现 Update**，帧序仍由 GameLoopDriver 独占。
- `Tick(dt)` 只做一件事：`PipelineManager.Instance?.TickGradeCrossfade(dt)`（manager 未挂时安全 no-op——boot 顺序未定义防御，有用例）。manager 侧 `TickGradeCrossfade` 设为 public 并写明驱动方，使 EditMode 测试与 host 共用同一入口（不走 InternalsVisibleTo——那要改白名单外的 AssemblyInfo.cs）。

**4. 单测 + PlayMode 像素证据**

- EditMode +24（747→**771**）：
  - `VolumeCrossfadeStateTests`（12 条，纯核）：起点/终点端点、中点 0.7071、a²+b²≡1 全程、单调性、完成停稳（完成后 5 次额外 Tick 权重纹丝不动）、Abort 中途落定 + 未启动时无害、duration≤0 快照、startPhase 重入、越界 phase 收敛、渐变中重启；
  - `PipelineCrossfadeTests`（12 条，manager 集成）：SetTier 起渐变且意图立即切换（档位=意图用例）、同档重入不重启、中点双 0.7071（真实组件上验证等功率）、完成归一停稳、Abort 落定目标全权重、早/晚中断两分支连续性、CrossfadeToProfile 不碰档位语义（quality level/持久化逐项断言）、零时长快照、null 拒绝、同意图 no-op、host 泵驱动 + Shutdown 恢复、host 在 manager 缺席时 Tick 无害。SetUp 先销毁残留 manager（保证"首次应用必快照"的前提不被其它夹具破坏）。
- PlayMode +2（16→**18**）：`VolumeCrossfadePixelEvidencePlayModeTests`（复用共享 `PixelProbeRig` + A-R9-04 的 4.0 辐射 Unlit 发射体，post 经 `PostFxCameraRuntime` 开启）。**三条可证伪判定全部是同机位帧对 diff，对后台负载免疫**（本机跑测时 quiet-check 判 BUSY，B-R9-08 教训在此适用）：
  1. **帧数证据**：硬切（duration 0）后第一帧即终点且其后 drift=0.0000（moved）——"硬切 1 帧到位"实证；0.8s 渐变实测 **34 个采样帧，33 帧渐变中，wTo 0.0436→0.9999 单调升、对目标距离 0.02181→0.00001 单调降**，与两端点差异均超 0.0015 的"真中间帧"约 23 帧（≥3 门限，回归退化成硬切时该数变 0，"能用"与"坏了"无重叠）；
  2. **一致性别名**：渐变完成帧 vs 直接设置目标帧 **meanAbs=0.000000 / moved=0.0000（逐位一致）**；附带"weight-0 第二 Volume + 归一"与从未渐变的 Medium 帧 diff 同为 0.0000；
  3. **Abort**：10s 长渐变第 3 帧中途 Abort，下一帧 vs 直接目标 **meanAbs=0.000000 / moved=0.0000**——一帧内逐位落定。
- 中途修正记录（均为施工期自查，非返工）：①PlayMode asmdef 补 `Unity.RenderPipelines.Core.Runtime` 引用（VolumeProfile 类型）；②`PixelProbeRig.Frame/FrameDiff` 是嵌套类型需限定；③两条中断用例的期望值把"时间"误当"相位"（0.2s/0.6s 修正）；④rig sanity 门限从 MaxLuma>3.5 改为 >0.9——post 路径把 HDR 钳到 ~1.0 是 A-R9-04 已记录的最终 blit 行为，0.9 仍足以证明发射体压过 0.02 背景。

### ② 与既有的协作（约束逐条对账）

| 约束 | 结果 |
|---|---|
| 既有 SetTier 用例零改动 | **git diff `PipelineManagerTests.cs` = 0 行**（9/9 绿）；`FrameGovernorHostTests` 10/10 零改动绿（归一设计保住其 weight 读数）；`PipelineVerification` 批处理校验逻辑继续成立 |
| 白名单 | 全部改动在 `Assets/ZFrame/View/Pipeline/**`（2 新增+1 修改）、`Tests/**`（3 新增+2 修改+2 meta）、`UnityProject/Assets/ZFrame/Tests/PlayMode/ZFrame.PlayMode.Tests.asmdef`（Tests/** 内 +1 引用）、本日志。git status 中 `Native/**`、`Tools/run_tests.ps1`、`zrender.dll` 等改动属**并行任务 B-R9-10**，本任务未触碰 |
| 不 git commit | 遵守，工作区留给主控/验收 |
| UTF-8 无 BOM / LF | 6 个 .cs + 1 个 .asmdef 逐一 `tr -dc '\r' | wc -c` = 0 CR、无 BOM（head3 为正文首字节） |
| 架构校验器 exit 0 | arch **PASS exit 0**（测试程序集豁免矩阵，asmdef 引用合法）；顺带 config、naming 均 exit 0 |

**对既有用例的两处最小适配**（白名单内、均为语义保真而非放松，请验收重点复核）：
1. `PostFxPixelEvidencePlayModeTests.cs`（+25 行）：该测试假设 SetTier **瞬时换 profile**（SetTier 后仅 yield 1-2 帧即采集）。渐变化后其 Low 档门限实测读到 0.086 亮像素——因为它拍到的是 ~92% Medium 的中间态，不是 Vol_Low 的定妆画面。修法 = 新增 `WaitGradeSettled()`（10s 预算 + 断言，BootChain 防挂纪律）并插在 4 处 SetTier 与采集之间——**测试等的正是它一直语义假设的"定妆画面"**，门限数值一处未动。A-R9-04 的可证伪性不受影响（其注入回退即红的性质与采集时机无关）。
2. `ZFrame.PlayMode.Tests.asmdef`（+1 行）：补 `Unity.RenderPipelines.Core.Runtime`，与 `ZFrame.Pipeline.Tests` 既有引用对齐（测试程序集豁免架构矩阵，校验器实证 exit 0）。

### ③ 自测证据（本机实跑）

| 段 | 结果 | 证据 |
|---|---|---|
| EditMode | **771/771 PASS**（747+24，exit 0） | `TempLogs/runtests_editmode.xml`（run#3，Unity exit 0） |
| PlayMode | **18/18 PASS**（16+2，exit 0） | `TempLogs/runtests_playmode.xml`；[VOLXFADE] 证据行在该 xml 各用例 output 节 |
| arch / config / naming | **exit 0 ×3** | `TempLogs/arch_report.txt` 等 |
| 过程 | 前 3 次 editmode FAIL（2 次编译错 + 1 次 2 条用例红）与 1 次 playmode FAIL（2 条红）均当轮定位修复并复跑全绿；无跳过（skipped=0） | run_tests 各轮 stdout |

### ④ 遗留与边界（如实）

1. **Governor PostFx 权重与档位渐变的并发**：FrameGovernor 治理中若发生档位切换，`StartGradeFade` 会把 A/B 权重重置为 (1,0)，governor 已落地的 PostFx 降档（A.weight=0）在下一次 measure 变化前不重放。既有梯子顺序下（Shadows 先于 PostFx）治理自身的降档发生在 PostFx 写入前，无实际影响；外部并发切档属 v1 契约外（FrameGovernorHost 注释已言明设置页先关治理）。未在本次处理，建议 R9+ 若做"治理中切档"再统一。
2. **PostFx 测试的 settle 等待**使其在该机多耗约 1-3s（0.8s×~3 次有效等待），换取的是"测的必须是定妆画面"的语义正确性。
3. 渐变期间 `CurrentGradeProfile` 返回**意图 profile**（非画面上占主导的旧 profile）——这是"档位是意图"的直接推论，已被既有用例与 PipelineVerification 依赖；若未来有调用方需要"当前主导 profile"，需新增接口而非改此语义。
4. 方案 §6.4 承诺的"运行时接口"已由 `CrossfadeToProfile` 落地，但当前**无场景/玩法调用方**（简报未要求接线）；设置页 SetTier 路径已自动渐变。

## 验收清单（主控写）

- [ ] V1 EditMode 全绿（数字写入结论）；曲线端点/中点/Abort 用例真实（行号）
- [ ] V2 PlayMode 渐变帧数证据亲跑（≥3 帧过渡 vs 硬切 1 帧）；渐变完成=直接设置目标（一致性别名）
- [ ] V3 既有 SetTier 用例零改动通过（git diff 核对）；GetTier 立即返回新档语义用例
- [ ] V4 Validate exit 0（自跑）；纯核零 UnityEngine（行号）
- [ ] V5 文件规范+施工报告完整

## 验收结论（验收代理写）

**日期**: 2026-10-31 ｜ **验收**: general-purpose 验收子代理（与施工不同代理，未改任何代码，未 git commit） ｜ **结论: PASS（5/5）**

### 验收亲跑证据（全部本机实跑，非复用施工自证）

- **V1 EditMode 全绿 —— PASS**：验收自跑 `Tools/run_tests.ps1`（wrapper exit 0），EditMode **771/771 passed / 0 failed / 0 skipped**（`TempLogs/runtests_editmode.xml`，start-time 02:59:54Z，本轮写入实证；Unity 写完 xml 后僵死由脚本 300s 纪律 taskkill，判定=evidence=xml，符合 HANDOFF 轮询纪律）。曲线用例行号真实：端点 `VolumeCrossfadeStateTests.cs:20`（起点 (1,0)）与 `:33`（完成落 (0,1)+5 次额外 Tick 停稳）；中点 `:58`（双方 cos45°=0.7071）；全程不变量 `:70`（20 采样 a²+b²≤1e-5）；Abort `:109`（中途落定目标+幂等）；中断续基 `:161`（startPhase=0.5 即中点、剩余时间恰好 0.5s 收敛）。
- **V2 PlayMode 像素证据亲跑 —— PASS**：18/18 passed（`TempLogs/runtests_playmode.xml`，11:05:47–11:06:13，含 2 条 VOLXFADE 用例，quiet-check=QUIET）。硬切 arm：`brighter=0.3006`（门限 0.012）证明 Medium→High 真动像素，切后 drift `moved=0.0000` —— 硬切 1 帧到位；渐变 arm：**31 采样帧，wTo 0.0516→1.0000 单调升、对目标距离 0.02168→0.00000 单调降**，双端差异均超 0.0015 的真中间帧 **20 帧**（门限 ≥3；退化成硬切时为 0，"能用/坏了"无重叠）；一致性别名：渐变完成 vs 直接设置目标 **meanAbs=0.000000 / moved=0.0000**；snap-back（weight-0 第二 Volume+归一）vs 从未渐变帧 meanAbs=0.000000；Abort：中途 A=1.0000/B=0.0011 → 一帧后 vs 直接目标 **meanAbs=0.000000 / moved=0.0000**。
- **V3 既有用例零改动+意图先行 —— PASS**：`git diff PipelineManagerTests.cs = 0 行`（9/9 绿）、`FrameGovernorHostTests.cs = 0 行`（10/10 绿，归一设计保住 weight 读数）；`PipelineVerification.cs` 0 行改动，其 :228 读 `CurrentGradeProfile`（意图先行故批处理校验继续成立）。GetTier 立即返回新档用例：`PipelineCrossfadeTests.cs:107`（SetTier 后立刻断言 CurrentTier=Low + CurrentGradeProfile=目标 + A/B 槽 profile/weight 布局）；早晚中断续基两分支 `:200`/`:232`（连续性 4 点+收敛）。
- **V4 三校验器+纯核零 UnityEngine —— PASS**：验收自跑 arch/config/naming **exit 0 ×3**（`TempLogs/arch_report.txt` 等，asmdef +1 引用经测试程序集豁免矩阵合法）。纯核 `VolumeCrossfadeState.cs` 全文件无任何 `using`（:1 即 namespace），唯一外部依赖 `System.Math`（:142-143 全限定）；曲线与 A-M2-02 `AudioService/CrossfadeState.cs:99-100` 逐字同款（sin/cos 等功率）；Abort 落定 (0,1)（:100-105）、完成停稳（:136-139 非 fading 恒返 (0,1)）、startPhase 重入（:73-92）逐条核实。引用的 URP 事实亲验：`Volume.cs:11-12` 无 DisallowMultipleComponent（双 Volume 叠放可行）、`VolumeManager.cs:470` weight≤0 整体跳过（槽 B 静息=精确 no-op）。
- **V5 文件规范+报告完整 —— PASS**：7 个改动/新增文件逐一核 `head -c 3` 无 BOM（首字节即正文）、`tr -dc '\r'` CR=0（LF）；5 个新 .cs 的 .meta 齐全；白名单核对通过（`ProjectSettings` 零改动；`Native/**`、`Tools/run_tests.ps1`、`zrender.dll`、`TASK-B-R9-10.md` 属并行任务 B-R9-10，本任务未触碰）。施工报告四节齐全；**两处适配均判"语义保真、无放松"**：①`PostFxPixelEvidencePlayModeTests.cs` +25 行逐行核过——4 处 SetTier 后插 `WaitGradeSettled()`（10s 预算+断言，防挂纪律），等的是该用例一直假设的定妆画面，门限数值一处未动，A-R9-04 的可证伪性不受影响；②asmdef +1 行 `Unity.RenderPipelines.Core.Runtime`（VolumeProfile 类型所需，与 ZFrame.Pipeline.Tests 既有引用对齐）。**4 条遗留均如实、不阻塞**：①Governor 并发切档（v1 契约外，既有梯子顺序下无实际影响）；②PostFx 测试 +1~3s（换语义正确的代价）；③渐变期 CurrentGradeProfile=意图（"档位是意图"的直接推论，既有用例依赖）；④`CrossfadeToProfile` 暂无场景调用方（简报未要求接线，接口已交付且有 4 条 EditMode 用例钉住）。

### 验收备注（不阻塞，主控知悉）

1. 施工报告①节写 PipelineManager.cs "+274/-15"，实际 `git numstat` 为 **+301/-14**——末段文档注释补写导致的统计漂移，无实质影响，如实记录。
2. 本验收仅复核清单内项，未做变异测试（验收约束禁止改代码）；用例"有牙"由门限设计本身背书：中间帧门限与硬切退化路径无重叠（真中间帧 0 即红）、一致性门限 0.002 vs 实测 0.000000。
3. 施工自测轮次中的失败（2 编译错+1 两红+playmode 两红）与最终全绿同轮定位修复，与验收无关；验收轮一次通过，无返工。
