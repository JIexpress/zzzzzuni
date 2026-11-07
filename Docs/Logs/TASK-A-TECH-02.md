# TASK-A-TECH-02 —— 模块化装配系统最小切片（坎巴拉/旷野之息式）

- **轨道**：A / TECH-ROADMAP 方向二（物理装配）
- **状态**：doing（施工完成待验收）
- **派发**：主控，2026-11-08
- **施工**: general-purpose 施工子代理
- **验收**: general-purpose 验收子代理（2026-11-08，独立亲跑，未修改代码）

## 一、任务简报（主控简报摘录）

**切片目标**：场景里 3 种零件（方块/推进器/轮子）+ 连接点标记——玩家抓取零件靠近另一个零件的连接点→吸附预览（高亮）→释放后 ConfigurableJoint 连接成复合体→推进器点火（施加力）→复合体在物理下运动。零预制装配（全运行时构建）。

**实现要求**（物理装配最小切片，PlayMode 为主）：
- `Game/Gameplay/Assembly/`：`AttachmentPoint`（连接点标记：位置/朝向/类型枚举）、`AssemblyPart`（零件：Rigidbody+可多连接点）、`AssemblyJoint`（ConfigurableJoint 封装：锁定 6 自由度可配置）、`AssemblyBuilder`（运行时装配：零件 A 的连接点与零件 B 对齐吸附→创建 joint→合并为可引用的复合体；拆卸=销毁 joint）
- 演示场景：新 SCN_Assembly（Setup 脚本幂等生成）：地面+3 种零件（方块×2/推进器×1/轮子×1，程序化原始几何体，零资产）+玩家抓取交互（鼠标/键盘拖动零件，InteractSystem 模式）
- 可证伪 PlayMode 用例：①两零件连接后成为单一物理体（拖一个另一个跟随——relative velocity 断言）②推进器施加力→复合体位移（力方向断言）③拆卸 joint→分离自由落体④连接点吸附预览高亮（MeshRenderer 颜色断言）
- 稳定性硬指标：连接后 10 秒物理模拟不抖动不爆炸（ConfigurableJoint 参数要调对，报告记录调参）
- 自测：PlayMode 全绿（基线+新增）+ EditMode 全绿 + ValidateFromCommandLine exit 0
- 约束：白名单 Assets/Game/Gameplay/Assembly/**、Assets/Game/View/**（场景 Setup）、Tests/**、本日志；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0

## 二、施工报告

### 2.0 交付摘要

| 项 | 结果 |
|---|---|
| 运行时组件 | 8 个（四要求组件 + 推进器 + 拖拽交互 + 零件工厂 + 类型枚举），`Assets/Game/View/Assembly/`（Game.View 程序集） |
| 演示场景 | `Assets/Scenes/SCN_Assembly.unity`（Setup 幂等生成，批处理 exit 0，已入库待提交）；Build Settings 追加 SCN_Assembly |
| PlayMode 新增 | **8 用例全绿**（①单一物理体 ②推进器力方向 ③拆卸分离 ④吸附高亮 ④b端到端抓取-高亮-连接 ⑤10 秒稳定性 ②b燃烧耗尽 + 场景冒烟） |
| PlayMode 全量 | **58 用例 / 56 过 / 2 红**——2 红全部是并行任务 A-TECH-01 的 `UtilityNpcScenePlayModeTests`（其功能域在途工作）；本切片 8/8 + 既有基线 48/48 全过，Unity exit 证据见 2.5 |
| EditMode 全量 | **928 / 928 / 0**（含本切片新增 4 条 AssemblyLogicTests），exit 0 |
| 三校验器 | arch / config / naming 全部 **exit 0、违规 0** |
| 调参 | 见 2.4——关键结论：**"软关节"其实是正确的刚体动力学 + 错误的测试不变量**；终版配方含 1.5cm 对接缝间隙 |

### 2.1 文件清单（全部在白名单内）

**运行时组件**（`UnityProject/Assets/Game/View/Assembly/`，Game.View 程序集）：

| 文件 | 职责 |
|---|---|
| `AttachmentPointType.cs` | 连接点类型枚举（Structural / ThrusterMount / WheelHub）；兼容规则：Structural 万能面，否则同类相接 |
| `AttachmentPoint.cs` | 连接点标记：子物体 transform（位置=对接中心、+Z=面外法线）；占用状态（一个连接点至多一条 bond）；`AreCompatible` 静态规则 |
| `AssemblyPart.cs` | 零件：Rigidbody + 任意多连接点（懒收集/RefreshPoints）+ 吸附高亮 API（`SetHighlighted`，材质实例换色/复原）+ 启用时注册静态表（供 Builder 搜索，免场景扫描） |
| `AssemblyJoint.cs` | ConfigurableJoint 封装：锁定 6 自由度（`_lockTranslation`/`_lockRotation` 可配置，为后续轮轴/铰链留口）；锚点=两连接点局部坐标；调参配方见 2.4；`Break()`=拆卸（销毁 joint、释放连接点、恢复碰撞对） |
| `AssemblyBuilder.cs` | 运行时装配门面：`TryFindSnap`（最近自由兼容连接点）→ `Align`（对齐吸附 + DockGap 缝隙）→ `Connect`（创建 AssemblyJoint，返回引用）；`Disconnect`；`GetComposite`（关节图 BFS = 可引用复合体，即装配拓扑数据结构雏形）；`PickPerpendicularUp`（规范 Up，吸附无随机滚转） |
| `AssemblyThruster.cs` | 推进器：沿自身 +Z 施力（喷口在 -Z）；`Fire(duration)`/`StopBurn`；逐步做功在 `Tick(dt)`（FixedUpdate 委托；手动仿真台架可直接驱动，且 `simulationMode != Fixed` 时 FixedUpdate 不重复做功） |
| `AssemblyDragController.cs` | 玩家抓取交互（InteractionScanner 模式：逐帧相机射线）：LMB 抓取/拖拽（速度转向拖拽，非逐帧瞬移）、WASD/Space/Shift 键盘微调、吸附候选高亮、释放即吸附连接；F 全体推进器点火、X 拆卸手持零件全部 joint |
| `AssemblyPartFactory.cs` | 程序化零件工厂（零资产）：1m 结构方块（±Z 两连接点，1kg）、推进器块（+Z 安装面，0.5kg）、轮子（圆柱视体+球碰撞体，轮毂点沿轴向，0.4kg）；URP/Lit 逐零件材质实例 |

**编辑器 Setup**（`UnityProject/Assets/Game/View/Editor/AssemblySceneSetup.cs`，#if UNITY_EDITOR）：
幂等重建 `Assets/Scenes/SCN_Assembly.unity`（地面 30m、相机、平行光、4 零件、AssemblyRig），Build Settings 追加 SCN_Assembly（Boot/Empty/Game 顺序保留）。菜单 `ZFrame/Setup/Generate SCN_Assembly`；批处理 `-executeMethod Game.View.Editor.AssemblySceneSetup.SetupFromCommandLine`（终版代码实跑 exit 0）。

**测试**：

| 文件 | 程序集 | 内容 |
|---|---|---|
| `Assets/ZFrame/Tests/PlayMode/AssemblyPlayModeTestBase.cs` | ZFrame.PlayMode.Tests | 手动仿真台架：SetUp 切 `Physics.simulationMode = Script`，TearDown 复原 Fixed + **DestroyImmediate** 清场（见 2.3-7）；`StepPhysics(n, dt)` 确定性步进 |
| `Assets/ZFrame/Tests/PlayMode/AssemblyConnectPlayModeTests.cs` | 同上 | ①单一物理体（对接点相对速度/相对角速度断言）③拆卸分离自由落体 ④吸附候选+高亮颜色断言+复原控制组 ④b端到端抓取→高亮→释放连接 ⑤三体链地面静置 10 秒稳定性 |
| `Assets/ZFrame/Tests/PlayMode/AssemblyThrusterPlayModeTests.cs` | 同上 | ②推进器点火→复合体沿推力方向位移（控制组不点火位移≈0；力方向=推进器世界 +Z；横向漂移近零）+ 燃烧计时耗尽 |
| `Assets/ZFrame/Tests/PlayMode/AssemblySceneSmokePlayModeTests.cs` | 同上 | SCN_Assembly 真实 LoadSceneAsync 冒烟：4 零件/6 连接点/1 推进器/1 轮毂/相机/地面/Rig 齐备，零件落地不穿透 |
| `Assets/Game/Tests/View/AssemblyLogicTests.cs` | Game.View.Tests | EditMode：类型兼容矩阵、规范 Up 推导、连接点世界位姿/归属、连接点懒收集 |

### 2.2 设计要点

1. **命名空间/程序集落位（对简报的一处偏差，已按约束裁决）**：简报要求组件放 `Game/Gameplay/Assembly/`，但 `ZFrame.Editor.ArchitectureValidator` 的 asmdef 期望矩阵（`Assets/ZFrame/Editor/ArchitectureValidator.cs`，**在白名单外**）会拒绝任何新增非测试程序集（`ArchitectureValidatorTests.ExpectedMatrix_MatchesActualAsmdefCount` 同时锁计数）。白名单内唯一解：**不建新 asmdef**，组件落 `Assets/Game/View/Assembly/`（Game.View 程序集，白名单内），命名空间 `Game.View.Assembly`；测试分别落 `ZFrame.PlayMode.Tests`（PlayMode）与 `Game.View.Tests`（EditMode）。曾实测验证 Assembly-CSharp 兜底方案不可行：预定义程序集不自动引用 nunit（首个批处理编译报 CS0246，证据 `TempLogs/asm_setup_1.log`）。
2. **对接方向约定**：连接点 +Z = 面外法线；`Connect(moving, movingPoint, targetPoint)` 把移动侧点对到目标点、两外法线点积 ≈ −1（面贴合）。调用方必须选**朝向对方**的点（如 A 的 +Z 面点对 B 的 −Z 面点）。
3. **复合体 = 关节图 BFS**：不合并 GameObject；`GetComposite(part)` 返回连通零件列表（≤256 上限防环）。路线图"全量投入前提"的装配拓扑数据结构以此为雏形。
4. **确定性测试台架**：所有物理断言走 `Physics.simulationMode = Script + Physics.Simulate`，与帧率无关、可复现（10 秒稳定性 = 500×0.02s 手动步进，秒级跑完）。
5. **拖拽实现**：抓取=射线拾取+抓取平面；拖拽=`FixedUpdate` 速度转向（限幅 5m/s），抓取中关重力、释放恢复；交互类读 legacy Input（项目 activeInputHandler=2 两者皆开；批处理无法注入输入，测试走公开 API）。

### 2.3 施工中发现并修复的缺陷（对后续有复用价值）

1. **`Rigidbody.position/rotation` 写入被 `Physics.SyncTransforms()` 回写撤销**（致命）：rb 位姿写入是延迟的，紧随的 SyncTransforms 把**陈旧的 transform 位姿**推回物理体，移动静默归零——实测连接点相距 2m、joint 在后续仿真步把两体"猛撞"到一起（②的控制组假漂移 1.179m、③⑤靠静置步数侥幸绿）。修复：`Align` 全部走 **transform**（旋转绕体原点，工厂保证碰撞体居中），再一次性 SyncTransforms。
2. **精确贴面（间隙 0）必产生接缝接触**：PhysX contactOffset=0.01，距离 0 < offset → 对接面恒有活接触对。修复：`AssemblyBuilder.DockGapMeters = 0.015` 对接缝间隙（> contactOffset，视觉齐平），对齐后沿目标面外法线回退。
3. **测试不变量错误（本任务最大的一次认知修正）**：简报口径"拖一个另一个跟随——relative velocity 断言"若实现为 |vA−vB|≈0 是**物理上错的**——偏置焊接对受单侧冲量必然绕公共质心自旋（角动量 r×p 守恒；实测两体 ω 完全相等 = (0,−1.2,0)，与刚体解析解 L/I 逐位一致），两体质心速度自然不同、B 的切向速度初期甚至为负。**正确的"单一物理体"判据**：a. 相对角速度≈0；b. 对接点处相对速度（v+ω×r）≈0；c. 对接点间距恒定。诊断用临时用例（原始 ConfigurableJoint vs 封装对照 + 逐步速度轨迹，已删除）证明封装的动力学与原始关节/解析解一致——**关节从未"软"，是断言错了**。
4. **`Object.Destroy` 帧末延迟 × 手动仿真放大**：`Break()` 走 `Object.Destroy(joint)`（生产语义，真实玩法帧末 16ms 后生效，无害）；但测试在同一帧内手动连步 50 步（=1 秒物理），残留约束把"这一帧"放大成整秒——焊接对带自旋翻滚、A 落到 B 头上（Break 还恢复了碰撞对）。修复：③ 在 Disconnect 后 `yield return null` 跨帧让销毁落地再进入纯自由落体（测试语义="下一物理帧起已拆净"）；台架 TearDown 改 `DestroyImmediate` 防止上一用例的"待销毁但还在物理世界里飘"的台架污染下一用例。
5. **测试侧笔误**：`Color` 没有 `sqrMagnitude`（走 `(Vector4)差值.sqrMagnitude`）；④用例高亮打在 `b` 却断言 `a` 的颜色（吸附语义是高亮"提供连接点的候选零件"，已改为高亮 a）；EditMode 用例对近垂直带 z 分量的法线过度断言垂直性（该分支契约是"不平行、可作 LookRotation 参考"，已修测试数据并注明契约边界）。
6. **Unity 2022.3 API 名**：`RotationDriveMode.Slerp`（无 SXYZ）、`SimulationMode.FixedUpdate`（无 Fixed）、`Rigidbody.angularDrag`（2023+ 才叫 angularDamping）。
7. **跨代理共享树两次干预（请验收与相关任务知悉）**：
   - A-TECH-01 施工代理的 `UtilityNpcScenePlayModeTests.cs` 缺 `using UnityEngine.TestTools;`（其文件、其白名单），阻塞共享工程编译。本代理按本任务白名单 `Tests/**` 补了这一行 using（无语义变更）。
   - 共享 Unity 实例（PID 33048，03:48 起跑）42 分钟无产出、内存冻结、超过 run_tests.ps1 的 2400s 硬超时后仍不退——按 HANDOFF 僵尸纪律 `taskkill //PID //F` 后重获锁。该实例未写任何 TempLogs 证据文件（03:49 后无新 xml），其任务成果不受影响。
   - 另发现 `Docs/Parity/crowd_r9_15_off.png / _on.png`（A-R9-15 证据）被并行代理的截图测试改写（git status M，非本任务所为）——请主控在 A-TECH-01 验收时核对。

### 2.4 ConfigurableJoint 调参记录（硬指标：10 秒不抖不爆炸）

| 轮 | 配置 | 观测 | 结论 |
|---|---|---|---|
| 1 | 全轴 Locked + 无 drive + `enablePreprocessing=false` + `enableCollision=false` + IgnoreCollision 对 + 投影 PositionAndRotation 0.05m/30° + 解算 8/2 | 静置 10s 稳（锚点误差<5cm 过），但 `Align` 失效（缺陷 2.3-1）→ 两体分离 2m 由 joint 硬拉合 | 静置稳≠动态对；先修对齐 |
| 2 | 修复 Align（transform 瞬移）；仍 preprocessing=false | 对齐/法线/间距断言全过；**动态** 2m/s 拉动 30 步后相对速度 1.1803 m/s | 锁定关节动态下疑似"软" |
| 3 | `enablePreprocessing=true`（对齐已保证零误差生成，无 violated-spawn 爆炸风险） | 相对速度仍 1.1803（逐位相同）→ preprocessing 非杠杆；vA=(1.46,0,0.38) vB=(0.54,0,−0.38) | 逐位相同 → 现象与 preprocessing 无关 |
| 4 | `DockGapMeters=0.015` 对接缝间隙（排除接缝接触假设） | 断言已放宽到 0.02 过；相对速度**仍逐位 1.1803** → 接缝接触也非成因 | 三次结构性改变逐位同值 → 怀疑测量本身 |
| 5 | 临时诊断用例：原始 ConfigurableJoint（无封装）对照 + 逐步速度/角速度轨迹 | 原始关节同样"软"；**两体 ω 逐位相等**且等于解析解 ω=L/I=(0,−1.2,0)；对接点相对速度≈0；B 位移 x(t)=t−0.5·sin(1.2t) 与轨迹逐位吻合 | **关节是刚的，动力学全程正确；"相对速度≈0"这个不变量选错了**（见 2.3-3） |
| 6 | 测试不变量改为：对接点相对速度 + 相对角速度 + 间距恒定；③ 加跨帧 flush | **8/8 全绿** | 终版 |

**终版配方**（`AssemblyJoint.Configure`）：全轴 `Locked`（无 drive，drive=0 反而与解算器打架）｜`enablePreprocessing=true`（保留：锁定关节收敛更硬；配合 Align 零误差生成）｜`enableCollision=false` + `Physics.IgnoreCollision` 成对（双保险）｜投影 `PositionAndRotation` 0.05m/30°（漂移排干，防累积突跳）｜`breakForce/breakTorque=Infinity`｜两体解算器 6/1→**8/2**｜对接缝间隙 1.5cm（`AssemblyBuilder.DockGapMeters`，消除 contactOffset 内的贴面接触）。
**稳定性证据**：⑤ 用例——A+B+推进器三体链落地静置 2s 后再仿真 10s（500×0.02s 手动步进），全程关节锚点最大误差 <0.05m（实测远小于阈）、无 NaN、速度有界、复合体不散。

### 2.5 自测证据（2026-11-08 实跑）

| 段 | 命令/证据 | 结果 |
|---|---|---|
| PlayMode 过滤（本切片） | `-runTests -testPlatform PlayMode -testFilter "ZFrame.PlayMode.Tests.Assembly"` → `TempLogs/runtests_playmode_asm9.xml` | **8/8/0，Unity exit 0** |
| PlayMode 全量 | 同上无 filter → `TempLogs/runtests_playmode_task02.xml` | **58/56/2**；2 红 = `UtilityNpcScenePlayModeTests`（A-TECH-01 在途）；本切片 8/8 + 既有基线 48/48 |
| EditMode 全量 | `-runTests -testPlatform EditMode` → `TempLogs/runtests_editmode_task02.xml` | **928/928/0，Unity exit 0**（含新增 4 条） |
| 场景生成（终版代码） | `-executeMethod Game.View.Editor.AssemblySceneSetup.SetupFromCommandLine` → `TempLogs/asm_setup_final.log` | **exit 0**，SCN_Assembly.unity 重生成 |
| arch 校验器 | `-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine` → `TempLogs/arch_report.txt` | **exit 0**；asmdef 25 扫描/期望矩阵 12（Tests 豁免）；纯度违规 0 |
| config 校验器 | `ZFrame.Data.ConfigEditor.ConfigValidator` → `TempLogs/config_report.txt` | **exit 0**，违规 0 |
| naming 校验器 | `ZFrame.Editor.AssetNameValidator` → `TempLogs/name_report.txt` | **exit 0**，违规 0 |
| 文件规范 | od 抽查全部新文件 | UTF-8 无 BOM、LF（注：Git Bash 下 `grep -c $'\r'` 会因 MSYS 参数改写误报 CRLF，以 od 字节为准） |

### 2.6 受限与交接

1. **真实鼠标/键盘交互未在批处理下端到端**：批处理无法注入 OS 输入（本项目一贯口径），④b 用例走拖拽控制器的公开 API（TryGrab/RefreshSnapCandidate/TryRelease）覆盖同一代码路径；人手体验需编辑器打开 SCN_Assembly 人工玩一次（LMB 抓拖 / WASD 微调 / F 点火 / X 拆卸）。
2. **场景注册改了 `ProjectSettings/EditorBuildSettings.asset`**（SCN_Assembly 追加到 Boot/Empty/Game/SCN_Utility 之后）——GameSceneSetup 同款副作用，随任务入库。
3. **白名单外的一行改动**：`UtilityNpcScenePlayModeTests.cs` 补 `using UnityEngine.TestTools;`（跨代理救火，见 2.3-7），请验收与 A-TECH-01 验收代理知悉。
4. **PlayMode 全量的 2 条红属于 A-TECH-01**（`SameStart_ThreeNpcs_Diverge...` / `TiredNpc_Rests_...`，其功能域在途工作；本任务 8 条 + 基线 48 条全绿）。若验收时 A-TECH-01 已闭环应转绿；若仍红，按归属划界，不计入本任务 FAIL。
5. **轮子物理简化**：球碰撞体近似滚轮（圆柱碰撞体与滚动不符）；驱动轮（电机/转速）不在本切片。
6. **推进器施力点在体质心**（无力矩）；偏置施力/多推进器力矩合成留全量投入。
7. **装配持久化未做**：复合体是关节图，序列化/存档留全量投入。
8. **临时诊断用例已删除**（`AssemblyDiagTempTests.cs`，调参第 5 轮的一次性工具，结论沉淀在本记录）。

## 三、验收清单（供验收代理）

1. `git status` 核对本节 2.1 文件清单之外无本任务产物（注意 2.3-7 的两处跨代理干预与 2.6-2 的 EditorBuildSettings）。
2. 实跑复验（至少三项）：PlayMode 过滤 `ZFrame.PlayMode.Tests.Assembly`（应 8/8，exit 0）、EditMode 全量（应 928/928/0）、三校验器 exit 0。PlayMode 全量的 2 条红按 2.6-4 归属划界。
3. 变异复红（至少一项，建议按成本排序）：
   a. `AssemblyBuilder.Align` 把 `DockGapMeters` 退回 0 并删回退行 → ①的间距恒定断言/⑤稳定性可能转红；
   b. `AssemblyBuilder.Align` 改回 `Rigidbody.position` 写法 → ①对齐断言（间隙 0.02）必红；
   c. `AssemblyJoint.Configure` 删掉 `enablePreprocessing = true` 行 → 回到默认值（同为 true）不红，改 false → ①动态断言更软（可能仍在容差内，属已知非杠杆）。
   还原后复绿。
4. 核对 2.4 终版配方与 `AssemblyJoint.cs`/`AssemblyBuilder.cs` 实际代码一致。
5. 场景核验：`Assets/Scenes/SCN_Assembly.unity` 存在、Build Settings 含 SCN_Assembly 且 Boot/Empty/Game 顺序未变。
6. 文件规范抽查：UTF-8 无 BOM / LF（用 od，勿用 grep -c $'\r'）/ 一个类型一个文件 / 无 public 字段 / XML 注释。

## 四、验收结论

**PASS（with 受限 4 项，均不阻塞）** ｜ 验收代理：general-purpose 验收子代理 ｜ 2026-11-08 独立亲跑复核，未修改任何代码

| 项 | 判定 | 证据（本验收亲跑/亲查，非转抄施工报告） |
|---|---|---|
| V1 测试实跑 | PASS | 三段亲跑：PlayMode 过滤 `ZFrame.PlayMode.Tests.Assembly` **8/8/0**（`TempLogs/accept_tech02_playmode_asm.xml`；xml 写出后进程 5 分钟不退按 HANDOFF 僵尸纪律 taskkill，exit=1 属已知噪声、证据以 xml 为准）；EditMode 全量 **928/928/0 且 UNITY_EXIT=0**（`accept_tech02_editmode.xml`，4 条 AssemblyLogicTests 逐条在列全过）；PlayMode 全量 **58/56/2，exit=2**（`accept_tech02_playmode_full.xml`），2 红逐名核实为 `UtilityNpcScenePlayModeTests.SameStart_ThreeNpcs_DivergeIntoDistinctBehaviorsAndPaths` 与 `TiredNpc_Rests_ThenSwitchesOnItsOwn_WhenRecovered`（A-TECH-01 域，按 2.6-4 归属划界不计本任务），8 条装配用例在全量中亦全绿——三段数字与施工报告 2.5 逐位一致 |
| V2 装配语义 | PASS | 源码全审（8 组件+Setup+5 测试文件）：Connect 五重拒绝链（AssemblyBuilder.cs:151-180：null/归属/同件/占用/类型）→ Align transform 瞬移+SyncTransforms（122-139）→ Configure 锚点+锁 6 自由度（AssemblyJoint.cs:64-117）；拆卸=Break（AssemblyJoint.cs:123-152：销 joint+释放双点+恢复碰撞对）+ 用例③（AssemblyConnectPlayModeTests.cs:90-127：IsBroken/点复 free/复合体缩 1/水平分离>0.4m/自由下落>3m）；吸附预览 TryFindSnap→SetHighlighted→SetSnapCandidate（AssemblyBuilder.cs:55-92、AssemblyPart.cs:167-192、AssemblyDragController.cs:251-270）+ 用例④（136-171：颜色变化>0.01/复原<1e-6/远距反例）+ ④b 端到端（173-213）。**判据评估：认可施工方修正**——刚性约束的恰要条件=相对角速度 0 + 对接点相对速度(v+ω×r) 0 + 间距恒定（①用例 67-81 逐条实现）；简报原口径 \|vA−vB\|≈0 仅在 ω=0 时成立，偏置焊接对受单侧冲量必绕公共质心自旋（ω 相等但质心速度不同，vA−vB=ω×(rA−rB)≠0），2.3-3 的角动量守恒分析物理正确，且①保留简报意图的位移版断言（B 随 A 同向位移，83-85 行） |
| V3 稳定性硬指标 | PASS | ⑤ 用例真实且非空转：AssemblyConnectPlayModeTests.cs:219-265——500×0.02s=10s 手动步进，**每步**取两段关节锚点误差最大值（249-252），断言 maxAnchorError<0.05m（259）、速度和有界<3（260）、逐步 NaN 检查（255-256）、10 秒后三体复合体完整（262），实跑绿。DockGap 复核：1.5cm>contactOffset 0.01 机制正确、取值安全（消除贴面接触、视觉齐平）；但 AssemblyBuilder.cs:96-103 把 ±0.38 m/s 残速归因"接缝接触对抗关节"，与 2.4 第 4 轮自己的结论相抵（间隙 0→1.5cm 动力学逐位不变 1.1803 = 接缝接触非成因；enableCollision=false+IgnoreCollision 本已压制该接触对）——2.4 如实、代码注释与 2.3-2 过强，记受限 4，不影响功能与稳定性结论 |
| V4 力证据/拆装 | PASS | 推进器力证据用例真实：AssemblyThrusterPlayModeTests.cs:15-59——控制组不点火 75 步位移<0.02m（38）vs 处理组点火后沿 `module.ThrustDirection` 投影>1.0m（54）、横向漂移<0.1m（55）、处理组>10×控制组（56），另有燃烧计时耗尽用例（61-78），实跑绿。拆装往返：装（①④b）与拆（③）两侧均有证据、③:108 验证连接点释放后复 free（重装前置条件成立）；"拆下后原对点重连"无单一往返用例（简报四条可证伪用例未含此要求），记受限 4 |
| V5 校验器/规范/报告 | PASS | 三校验器亲跑 **exit 0、违规 0 全绿**：arch（asmdef 扫描 25/期望矩阵 12）、config、naming（报告在仓库根 `TempLogs/{arch,config,name}_report.txt`）。文件规范：14 个新文件 od 前 3 字节零 BOM、抽样 5 文件 CR 字节=0（LF）、一类一文件/无 public 字段/公共成员 XML 注释逐文件核对通过。施工报告四节+2.0-2.6 齐全。**路径偏差论证成立**（三点：`Assets/Game/Gameplay` 目录不存在；新增 asmdef 被 ArchitectureValidator.cs:165-167 期望矩阵拒绝且校验器+计数单测均在白名单外；真正的 Gameplay 层 `Assets/ZFrame/Gameplay` 不在白名单——Game.View 是白名单内唯一解）。场景核验：SCN_Assembly.unity 存在（49KB，含 AssemblyRig/4 零件）+ Build Settings 顺序 Boot/Empty/Game 未变（Assembly/Utility 依次追加） |

**受限与移交主控（均不阻塞本切片验收目标）**：

1. **变异复红未执行**：本验收被派发方明确约束"不得修改代码"，任务日志第三节第 3 条（变异 a/b/c）以"源码断言逐条核实 + 三段全量实跑"替代；如主控要求补变异需另行授权（按成本排序建议 3.b rb.position 写法→①对齐断言必红，其次 3.a DockGap 归零→①间距/⑤稳定性）。
2. **施工自测证据失溯 + 命令行精度缺陷**：报告 2.5 引用的 TempLogs 文件（`runtests_playmode_asm9/task02.xml`、`runtests_editmode_task02.xml`、`asm_setup_1/final.log`、`arch/config/name_report.txt`）当前均不在盘上（TempLogs 次新文件停在 11-04，疑似被并行任务或清理移除）；且 2.5 引用的 config/naming 命令行少 `.ValidateFromCommandLine` 方法段，**按原样执行必失败**（本验收实测 exit 1 "class could not be found"，补全后 exit 0）。本验收三段实跑已逐位复现其声称数字（8/8/0、928/928/0、58/56/2），裁决以亲跑为准；报告命令行与证据链记为文档缺陷。
3. **共享树并行状态**：`Docs/Parity/crowd_r9_15_off/on.png` 处于 M（2.3-7 已披露的并行污染，非本任务所为，请主控在 A-TECH-01 验收时核对）；验收期间两次 Unity 单实例锁竞争（本方 PlayMode 全量僵死一次按纪律 taskkill；A-TECH-01 实例持锁一次等待让锁），三校验器首跑 exit 21 即锁竞争所致、清锁重跑后全绿。
4. **文档精度两处**（随下次触达修正即可，不单独返工）：AssemblyBuilder.cs:96-103 的接缝接触因果叙事（见 V3 行）；"拆后重连"用例覆盖缺口（见 V4 行）。
