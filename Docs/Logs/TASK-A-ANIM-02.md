# TASK-A-ANIM-02 —— 动画扩展（一）：跳跃接真实垂直位移

> 路线图出处：`Docs/LLM-HANDOFF-NOTES.md` §7「动画扩展（Jump 接线/脚步 IK/程序化层）」的第一刀。
> 上游任务：A-ANIM-01（脑→动画驱动链，已完成 `Docs/Logs/TASK-A-ANIM-01.md`）。

## 一、任务简报（主控）

### 背景与现状（已核实）

- `NpcAnimatorDriver.RequestJump()` 当前只做 `_animator.SetTrigger("Jump")`：**状态机穿过 Jump，角色原地不动**。
- `Step()` 里 `Grounded` 参数**写死 true**（"no vertical physics in this slice"），Jump→Idle 的 `exitTime + Grounded` 条件因此永远满足——落地是假的。
- 控制器 `CHR_KenneyLocomotion` 只有 4 条过渡：Idle→Run、Run→Idle、Run→Jump、Jump→Idle。**没有 Idle→Jump**，站着起跳不会进 Jump 状态。
- `NpcUtilityView` 每帧只写 XZ（`new Vector3(_agent.Position.X, position.y, _agent.Position.Y)`），**Y 保留**——表现层可以安全接管 Y。
- 数据侧是 2D（`NpcUtilityAgent.Position` 为 Vec2），本任务**不加 sim 的 Y 轴**，跳跃是纯表现层位移。

### 目标

1. 跳跃成为真实的垂直运动：抛物线上升→下降→落地归零，`Grounded` 参数反映真值。
2. 控制器补齐 **Idle→Jump** 与 **Jump→Run**（落地后按实测速度回 Run 或 Idle），过渡仍由 `AnimRigSetup` 生成，**不手改 .controller**。
3. 弧线做成**纯函数**（引擎无关），EditMode 可断言；运行时只消费它。
4. 禁止二段跳：空中再次请求被忽略并可观测。
5. 不碰输入资产、不碰 Domain、不动场景布局、不改 `Game.View` 的 asmdef。

### 设计要点（实现方向，不是唯一写法）

- **纯函数进 `KenneyLocomotionPlan`**（该类当前只依赖 `System`/`System.Math`，保持）：
  - `JumpPeakHeight`（建议 0.75 m）、`JumpGravity`（9.81）、`JumpAirTime`（由 `v0 = sqrt(2gh)`、`T = 2v0/g` 推出）；
  - `JumpHeightAt(float elapsedSeconds)`：`h = v0·t − ½g·t²`，`t` 夹在 `[0, T]`，两端为 0；
  - `AirborneAt(float elapsedSeconds)`：`t < T` 为 true；负数/超时按"已落地"处理，不抛。
  - 新常量：`IdleToJumpBlend = 0.05f`、`JumpToRunBlend = 0.1f`。
- **驱动器**（`NpcAnimatorDriver`）：
  - `RequestJump()` 改为返回 `bool`（起跳成功 true，空中被拒 false）；**既有调用方忽略返回值，不得破坏**。
  - 记录基准 Y（首次 Step 或 Awake 捕获），每步把 `transform.localPosition.y` 设为 `baseY + JumpHeightAt(elapsed)`。
  - `Grounded` 参数：空中 false、落地 true；落地瞬间把 Y 精确归位。
  - 诊断属性：`JumpRequestCount` / `IgnoredJumpCount` / `LastJumpHeight`（测试与 DevConsole 用）。
  - `ManualDrive` 语义不变；`Step(dt)` 仍是唯一推进入口。
- **控制器生成**（`AnimRigSetup`）：
  - 新增 Idle→Jump（条件 `Jump` trigger，无 exit time）、Jump→Run（`hasExitTime=true`、`exitTime=JumpExitTime`、条件 `Speed > LocomotionSpeedThreshold`）。
  - 持久化校验从"4 条过渡"改为 6 条（保留"每条过渡有 destination + conditions"的既有断言口径）。
  - 生成后必须重跑 `SetupFromCommandLine`，产物 `CHR_KenneyLocomotion.controller` 进白名单。

### 约束（白名单，越界即返工）

允许改动：
- `UnityProject/Assets/Game/View/Animation/KenneyLocomotionPlan.cs`
- `UnityProject/Assets/Game/View/Animation/NpcAnimatorDriver.cs`
- `UnityProject/Assets/Game/View/Editor/AnimRigSetup.cs`
- `UnityProject/Assets/Game/Tests/View/KenneyLocomotionPlanTests.cs`
- `UnityProject/Assets/ZFrame/Tests/PlayMode/NpcAnimatorPlayModeTests.cs`
- `UnityProject/Assets/Art/Models/CHR_KenneyLocomotion.controller`（**只能由生成器写**）
- `Docs/Logs/TASK-A-ANIM-02.md`
- `TASKS.md`（仅本任务行）

禁止：改 `NpcUtilityView` / Domain / 输入资产 / 其它场景 / 其它控制器；不 `git commit`；不改 `.meta` 手写内容；UTF-8 无 BOM + LF。

### 自测命令

```bash
# 1) 重生成控制器（脚本内自带 Exit(0)，不要加 -quit）
"C:/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe" -batchmode \
  -projectPath "C:/Users/1/Desktop/zcode/GameFramework/UnityProject" \
  -executeMethod Game.View.Editor.AnimRigSetup.SetupFromCommandLine -logFile -
# 2) EditMode（纯函数）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/run_tests.ps1 -Segments editmode
# 3) PlayMode（场景级）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/run_tests.ps1 -Segments playmode
```

Unity 单实例纪律：跑前查 `Unity.exe` 进程 + 删 `UnityProject/Temp/UnityLockfile`；证据以 `TempLogs/runtests_*.xml` 为准；跑测试**勿带 -quit**。

### 验收清单（可证伪，带阈值）

| # | 断言 | 判据 |
|---|---|---|
| V1 | 弧线纯函数 | `JumpHeightAt(0)=0`、`JumpHeightAt(T)=0`（±1e-4）、峰值=`JumpPeakHeight`（±1e-3）、对称 `h(t)=h(T−t)`（±1e-4） |
| V2 | 落地判定 | `AirborneAt(t<T)=true`、`AirborneAt(T)=false`；`t<0`/`T=0` 不抛异常 |
| V3 | 控制器结构 | 重新生成后：**3 参数**（Speed/Grounded/Jump，本任务不新增参数）/ 3 状态 / **6 条过渡**，含 Idle→Jump 与 Jump→Run；持久化校验通过（简报初稿误写"4 参数"，以本节为准） |
| V4 | 真位移 | PlayMode：起跳后 Y 上升到 ≥ `baseY+0.3` 再回落，`T` 后回落基准 ±1e-3；`Grounded` 由 false 回 true |
| V5 | 无二段跳 | 空中再次 `RequestJump()` 返回 false，`IgnoredJumpCount=1`，Y 轨迹不重置 |
| V6 | 变异复红 | ①`Grounded` 写回恒 true → V4 必红；②去掉 Idle→Jump → 站着起跳用例必红；逐字节恢复后全绿 |

既有用例 `RequestJump_TraversesTheJumpState_AndReturns` 必须保持绿（允许从 Idle 起跳后仍能穿越 Jump）。

## 二、施工报告（施工代理填）

- **施工**: 施工子代理（ZCode / DeepSeek-V4.1-Flash），2026-11-09 04:40–05:05
- **摘要**: 见下四小节
- **改动清单**: 见 §2
- **自测数字**: 见 §3
- **风险/未验证**: 见 §4

### 1. 摘要

- 跳跃从"状态机空转、角色原地不动"变成**真实垂直位移**：弧线 `h(t)=v0·t−½g·t²`
  与落地判定 `AirborneAt` 都做成 `KenneyLocomotionPlan` 里的**纯函数**
  （该类仍只依赖 `System`，未引入 UnityEngine；峰值 0.75 m、g=9.81、T=0.7821 s 全由
  `v0=√(2gh)`、`T=2v0/g` 推出，无手写魔数）。
- `NpcAnimatorDriver.RequestJump()` 改为返回 `bool`：起跳成功 true，空中被拒 false 且
  `IgnoredJumpCount++`；既有调用方（PlayMode 用例语句式调用）零改动继续编译。
- `Grounded` 参数由真值驱动（`!_isAirborne`）：空中 false、落地当帧 true；落地当帧把
  `localPosition.y` 精确写回基准（不留浮点残差）。基准 Y 惰性捕获（首次 `Step`/`RequestJump`），
  不依赖 `Awake`——规避本项目 EditMode 下 Awake/OnDestroy 不保证触发的坑。
- 控制器由 `AnimRigSetup` 重新生成补齐 **Idle→Jump**（Jump 触发器，无 exit time）与
  **Jump→Run**（exit time 0.9 + Speed>0.1 + Grounded，排在 Jump→Idle 之前），共 **6 条过渡**；
  未手改 `.controller`。生成器 DELETE+CREATE 后 **guid 未变**（`9257ec50d6c67b241b65da2c0bb9cb6f`），
  `SCN_Anim.unity` 的 Animator 引用依旧有效，因此**无需重跑 AnimSceneSetup**、场景零改动。
- 只碰白名单内文件；未 `git commit`；五个文本文件逐字节核验 UTF-8 无 BOM + LF。

### 2. 改动清单（逐文件）

| 文件 | 改动 |
|---|---|
| `UnityProject/Assets/Game/View/Animation/KenneyLocomotionPlan.cs` | 新增 `JumpPeakHeight=0.75`、`JumpGravity=9.81`、`JumpInitialSpeed`（`static readonly`，`√(2gh)`）、`JumpAirTime`（`2v0/g`≈0.782042）、`IdleToJumpBlend=0.05`、`JumpToRunBlend=0.1`；新增纯函数 `JumpHeightAt(float)`（t 夹在 [0,T]，两端恰为 0，负/NaN/超时读作 0 不抛）与 `AirborneAt(float)`（`[0,T)` 为 true，负/NaN false）。类文档补 Y 轴一段。**仍只 `using System` + `Game.Domain.AI.Utility`**（原文件已有后者，无新增引擎依赖） |
| `UnityProject/Assets/Game/View/Animation/NpcAnimatorDriver.cs` | `RequestJump()` → `bool`（无 animator/空中均 false，空中计 `IgnoredJumpCount`，成功计 `JumpRequestCount` 并 `SetTrigger`）；`Step()` 里写死 `SetBool(Grounded, true)` 换成 `AdvanceJump(dt)`：累加 `_jumpElapsed`、按纯函数写 `localPosition.y = baseY + h`、落地当帧 `_isAirborne=false` 且高度归 0、`SetBool(Grounded, !_isAirborne)`；新增 `EnsureBaseY()` 惰性捕获（`_hasBaseY`，非 Awake）；新增诊断属性 `JumpRequestCount`/`IgnoredJumpCount`/`LastJumpHeight`。`ManualDrive` 语义、`Step(dt)` 唯一入口、`OnAnimatorIK`、转向逻辑均未变 |
| `UnityProject/Assets/Game/View/Editor/AnimRigSetup.cs` | `ApplyPlan` 新增 `IdleToJump`（条件 `If Jump`，`hasExitTime=false`，blend 0.05）与 `JumpToRun`（`hasExitTime=true`、`exitTime=JumpExitTime`、条件 `Speed>0.1` + `Grounded`，blend 0.1，**创建顺序在 JumpToIdle 之前**——Unity 取第一条条件通过的过渡，移动者落地回 Run、站立者落到 Jump→Idle）；`VerifyPersisted` 增加"3 状态"与"总过渡数=6"断言，报告文本 4→6 同步；类文档与生成的 report 行同步 |
| `UnityProject/Assets/Game/Tests/View/KenneyLocomotionPlanTests.cs` | 新增 3 个纯函数用例：`JumpArc_Constants_DeriveFromPeakHeightAndGravity`（v0/T 由公式反推）、`JumpHeightAt_IsAClampedSymmetricParabola`（V1）、`AirborneAt_IsTrueOnlyInsideTheArc`（V2）；`Plan_Constants_MatchTheBrief` 补两条 blend；控制器三用例（构建/重建等价/落盘重载）由 4 过渡口径扩到 6，并逐条断言 Idle→Jump（无 exit time + Jump 条件）与 Jump→Run（exitTime + Speed>0.1 + Grounded）；新增 `CountTransitions` 助手 |
| `UnityProject/Assets/ZFrame/Tests/PlayMode/NpcAnimatorPlayModeTests.cs` | ⑤ `RequestJump_TraversesTheJumpState_AndReturns` 保留并补"返回 true"断言；新增 ⑥ `Jump_LiftsTheBody_AlongTheArc_AndLandsExactlyOnTheBase`（V4：采样 2 s，抬升≥0.3 且≈峰值、落地归位 ±1e-3、Grounded false→true）、⑦ `AirborneJumpRequest_IsIgnored_WithoutResettingTheArc`（V5）、⑧ `StandingJump_FromIdle_EntersJumpState`（V6② 的正向证据，冻结平面驱动保留垂直步进）；文件头注释补 ⑥⑦⑧ |
| `UnityProject/Assets/Art/Models/CHR_KenneyLocomotion.controller` | **生成器产物**（`SetupFromCommandLine`，未手改）：6 条过渡、3 参数、3 状态、IK pass=1 |
| `Docs/Logs/TASK-A-ANIM-02.md` | 本节施工报告 |

未改：`NpcUtilityView.cs`（只读确认：`ApplyTransform` 每帧 `new Vector3(agent.X, position.y, agent.Z)`，Y 保留、只写 XZ）、Domain、输入资产、`SCN_Anim.unity`、其它 `.controller`、任何 asmdef、任何 `.meta`。

### 3. 自测数字

**① 控制器重生成**（`-executeMethod Game.View.Editor.AnimRigSetup.SetupFromCommandLine`，无 `-quit`，exit=0）：

```
=== controller: DELETED previous (in-place rebuild loses transitions in 2022.3)
=== controller: CREATED Assets/Art/Models/CHR_KenneyLocomotion.controller
  params=[Speed:Float, Grounded:Bool, Jump:Trigger] default=Idle transitions=[Idle>Run Speed>0.1,
  Run>Idle Speed<0.1, Run>Jump trig, Idle>Jump trig, Jump>Run exit0.9+Speed>0.1+grounded,
  Jump>Idle exit0.9+grounded] baseLayer.iKPass=true
  persisted verify: 3 params / 3 states / default=Idle / 6 transitions with destinations+conditions / IK pass — OK
```

落盘 YAML 逐项核对（`CHR_KenneyLocomotion.controller`）：`AnimatorStateTransition` 计数 **6**；
`IdleToJump`（mode 1 / `Jump` / `m_HasExitTime: 0`）、`JumpToRun`（mode 3 / `Speed` 0.1、
mode 1 / `Grounded`、`m_ExitTime: 0.9`、`m_HasExitTime: 1`）、`JumpToIdle`（mode 1 / `Grounded`、
`m_HasExitTime: 1`）；`m_IKPass: 1`；`Speed` 状态绑定 `m_SpeedParameter: Speed`。
guid 保持 `9257ec50d6c67b241b65da2c0bb9cb6f`（`.meta` md5 与生成前一致）。

**② EditMode**（`Tools/run_tests.ps1 -Segments editmode`）：
**total=1102 / passed=1102 / failed=0 / skipped=0**（xml 用时 10.3354946 s；证据 `TempLogs/runtests_editmode.xml`）。
基线 1099 → 1102（+3，新增纯函数用例）。

**③ PlayMode**（`Tools/run_tests.ps1 -Segments playmode`）：
**total=88 / passed=88 / failed=0 / skipped=0**（xml 用时 62.9260248 s；证据 `TempLogs/runtests_playmode.xml`）。
基线 85 → 88（+3，新增 ⑥⑦⑧）。

新增 6 条用例在 xml 中均为 Passed：
`JumpArc_Constants_DeriveFromPeakHeightAndGravity`、`JumpHeightAt_IsAClampedSymmetricParabola`、
`AirborneAt_IsTrueOnlyInsideTheArc`、`Jump_LiftsTheBody_AlongTheArc_AndLandsExactlyOnTheBase`、
`AirborneJumpRequest_IsIgnored_WithoutResettingTheArc`、`StandingJump_FromIdle_EntersJumpState`。

**④ 真位移实测**（⑥ 的 `Debug.Log`，抄自 `runtests_playmode.xml`）：

```
[A-ANIM-02] jump: baseY=-0.0003 peakY=0.7497 rise=0.7500 T=0.7821 landedY=-0.0003
             lastJumpHeight=0.0000 grounded=True
```

即：抬升 0.7500 m（= `JumpPeakHeight`），滞空 0.7821 s（= `JumpAirTime`），落地回到 baseY
（差 0.0000，断言口径 ±1e-3），落地 `Grounded=true`。

**⑤ 环境备注**：`run_tests.ps1` 首跑被 **`HTTP_PROXY`/`http_proxy` 大小写重复变量**
挡住（Start-Process 构造环境块失败，HANDOFF §3.2 已知坑）：在调用会话内把代理变量归一化为单一
`HTTP_PROXY`/`HTTPS_PROXY` 后两条命令均通过。此为运行环境操作，未改仓库内任何脚本。

### 4. 风险与未验证

1. **简报内部矛盾（请验收/主控裁决）**：验收清单 V3 写"**4 参数** / 3 状态 / 6 条过渡"，
   但简报"设计要点"只新增两条**过渡**、未提出任何新参数；同一简报的"关键约束复述"也未提第 4 个参数。
   施工按"设计要点 + 既有断言口径（`必须恰好三个参数`）"实现，**控制器仍是 3 参数**
   （`Speed:Float` / `Grounded:Bool` / `Jump:Trigger`），`VerifyPersisted` 的 `parameters.Length != 3`
   断言与 EditMode 参数断言保持不变。若确需第 4 个参数，请指明其名称/类型/用途，否则会是一条
   无人消费的死参数（与本任务"参数集不变"的口径冲突）。
2. **V6 变异复红未由施工执行**（属验收动作，避免与验收代理重复且需改源码后逐字节恢复）。
   施工已把可证伪钩子写进用例：①"`Grounded` 写回恒 true"→ ⑥ 的 `sawAirborneFlag`
   （`Jump_LiftsTheBody_...` 中 `Assert.IsTrue(sawAirborneFlag, ...)`）必红；②"去掉 Idle→Jump"→
   ⑧ 的 `WaitForState(animator, JumpState, 3f, ...)` 必红，且生成器 `transitionCount != 6` 会直接 throw。
3. **超出简报字面的一处增强**：`Jump→Run` 除 `Speed>0.1` 外**额外加了 `Grounded` 条件**。
   动机：`hasExitTime=0.9` 是"0.9 × clip 长度"，Jump clip 若短于 T=0.782 s，状态机会在**身体仍在空中**
   时离开 Jump，与"落地后按实测速度回 Run 或 Idle"矛盾；加 `Grounded` 把过渡与 clip 时长解耦
   （简报"设计要点"已声明是"实现方向，不是唯一写法"）。未量化 Jump clip 实际时长（不在本任务白名单，
   也未在测试中读取）——该增强使这条无需量化。
4. **空中仍按实测平面位移写 `Speed`**：未做空中减速/失速，`Jump→Run` 的判断依赖它。若后续要在
   起跳时冻结 Speed，需同步改 `Step` 的写入顺序（`Jump→Run` 会立即失效）。
5. **baseY 标定前提**：基准取首次驱动步的 `localPosition.y`（= `AnimSceneSetup` 摆好的"脚落地"偏移）。
   若将来运行时改变模型子节点的父级/局部偏移，需要重新标定；本任务未覆盖该场景。
6. **CrowdLOD L0 的 `SyncL0` 会 `SetPositionAndRotation` 覆盖 L0 表示的世界位姿**，与"驱动器写
   localPosition.y"共存未验证——当前唯一挂 `NpcAnimatorDriver` 的场景是 `SCN_Anim`（按脚本 guid 核对），
   `SCN_Crowd` 未挂载，故本次不影响；若将来把驱动器接入人群 LOD，跳跃中会被 `SyncL0` 覆盖。
7. 只跑了简报要求的 `editmode`/`playmode` 两段，**未跑** `arch`/`config`/`naming`（未新增/移动 asmdef、
   未新增资产命名单元）。Unity 全程单实例，`Temp/UnityLockfile` 每次启动前为空，两次运行均正常
   `Unity exit: 0`（无僵死，未用 taskkill）。

## 三、验收结论（验收代理填，与施工不同代理）

- **验收**：验收子代理（ZCode / DeepSeek-V4.1-Flash），2026-11-09 05:00–05:30；与施工代理不同。
- **总判定**：**PASS**。V1–V6 全过；两处变异均亲手复红、逐字节恢复后全绿；控制器 6 过渡。
- **独立复跑**：下列数字全部由本代理亲跑，未采信施工报告。

### V1 弧线纯函数 —— PASS

| 判据 | 本代理实测 | 结论 |
|---|---|---|
| `JumpHeightAt(0)=0` | 0（精确） | 过 |
| `JumpHeightAt(T)=0`（±1e-4） | 0（精确，T=0.7820619 s） | 过 |
| 峰值 = `JumpPeakHeight`（±1e-3） | `h(T/2)=0.750000`，误差 0.000e+000 | 过 |
| 对称 `h(t)=h(T−t)`（±1e-4） | 最大偏差 1.192e-7（f=0.25）；f=0.1/0.333/0.4 均 ≤1.2e-7 | 过 |

独立复算（按 C# float32 逐步模拟）：`v0=√(2·9.81·0.75)=3.83601356 m/s`、`T=2v0/g=0.7820619 s`，与 `KenneyLocomotionPlan.cs:126/132` 一致；中段样本 `h(0.1)=0.3345514`、`h(T−0.1)=0.3345513` 严格落在 `(0, 0.75)`。边界 `h(-1)=h(3T)=h(NaN)=0`，不抛。用例 `JumpHeightAt_IsAClampedSymmetricParabola` Passed（`TempLogs/accept_anim02_final_editmode.xml`）。

### V2 落地判定 —— PASS

`AirborneAt(0)=true`、`A(0.999T)=true`、`A(T)=false`、`A(2T)=false`、`A(-0.5)=false`、`A(NaN)=false`、`A(float.MaxValue)=false`，全部不抛。代码 `KenneyLocomotionPlan.cs:213-221`；用例 `AirborneAt_IsTrueOnlyInsideTheArc` Passed。

### V3 控制器结构 —— PASS

- **3 参数** `Speed:Float` / `Grounded:Bool` / `Jump:Trigger`（`CHR_KenneyLocomotion.controller:221-233`）；**3 状态** Idle/Run/Jump（默认 Idle）。
- **6 过渡**（`AnimatorStateTransition:` 计数=6）：Idle→Run / Run→Idle / Run→Jump / **Idle→Jump**（`If Jump`，`hasExitTime=0`，`controller:34-52`）/ **Jump→Run**（`exitTime=0.9 + Speed>0.1 + Grounded`，`controller:286-307`）/ Jump→Idle（`exitTime=0.9 + Grounded`）。
- Jump 状态的过渡顺序：`m_Transitions[0]=JumpToRun`、`[1]=JumpToIdle`（`controller:194-197`）——移动者优先回 Run。
- 持久化校验：本代理亲自执行 `SetupFromCommandLine`，exit=0；报告 `persisted verify: 3 params / 3 states / default=Idle / 6 transitions with destinations+conditions / IK pass — OK`（`TempLogs/anim_rig_setup.txt`）。
- 简报 V3 初稿"4 参数"系主控笔误，已在校准后的 V3 写明 3 参数；施工按 3 参数实现，**不算缺陷**。

### V4 真位移 —— PASS

PlayMode ⑥ 实测（`TempLogs/accept_anim02_final_playmode.xml` / `runtests_playmode.log`）：

```
[A-ANIM-02] jump: baseY=-0.0003 peakY=0.7497 rise=0.7500 T=0.7821 landedY=-0.0003
             lastJumpHeight=0.0000 grounded=True
```

抬升 0.7500 m（≥0.3 且 = `JumpPeakHeight` ±0.1）；落地差 0.0000（±1e-3）；空中 `Grounded=false` 被采样到、落地回 `true`。用例 `Jump_LiftsTheBody_AlongTheArc_AndLandsExactlyOnTheBase` Passed（2.41 s）。

### V5 无二段跳 —— PASS

用例 `AirborneJumpRequest_IsIgnored_WithoutResettingTheArc` Passed（1.23 s）：空中第二次 `RequestJump()` 返回 false、`IgnoredJumpCount=1`、`JumpRequestCount` 不变、拒绝后两帧 Y 不低于 `yAtRejection−0.02`、`LastJumpHeight` 沿原弧线、最终按 T 落地归位且 `Grounded=true`。代码 `NpcAnimatorDriver.cs:190-194`。

### V6 变异复红 —— PASS（两次均本代理亲手改/跑/恢复）

**变异①**：`NpcAnimatorDriver.cs:278` 的 `!_isAirborne` → `true`（Grounded 恒真）。

- 改前 PlayMode **88/88**；改后 **88 总 / 87 过 / 1 红**，红为 `Jump_LiftsTheBody_AlongTheArc_AndLandsExactlyOnTheBase`，断言"空中 Grounded 参数必须为 false"（`NpcAnimatorPlayModeTests.cs:341`）。证据 `TempLogs/accept_anim02_mut1_playmode.xml`。
- 恢复后 sha256 前后均为 `13937B77…52213`（逐字节一致）。

**变异②**：注释 `AnimRigSetup.cs:419-423` 的 Idle→Jump 三句，重新生成控制器。

- 改前：生成 exit=0、**6 过渡**；改后：生成 **exit=1**，异常 `persisted controller has 5 transitions (expected 6: Idle>Run, ...)`，落盘控制器 **5 过渡**。证据 `TempLogs/accept_anim02_mut2_generate.log`。
- PlayMode **88 / 87 / 1 红**：`StandingJump_FromIdle_EntersJumpState`，"等待状态 Jump 超时 3s"。证据 `TempLogs/accept_anim02_mut2_playmode.xml`。
- 恢复后 `AnimRigSetup.cs` sha256 前后均为 `266C38C2…957D13`；重新生成 exit=0、验证 OK、**6 过渡**。

**变异后回绿**：PlayMode **88/88**（`TempLogs/accept_anim02_final_playmode.xml`）+ EditMode **1102/1102**（`TempLogs/accept_anim02_final_editmode.xml`）。基线为 1099/85，均 +3。

### 两个已知偏差的裁决

1. **V3 "4 参数" vs 3 参数**：主控笔误，已在 V3 校准；施工实现 3 参数与设计要点及既有断言一致。**PASS，不算缺陷**。
2. **Jump→Run 额外 `Grounded` 条件**：**PASS**。
   - 不会让"落地回 Run"失效：`AdvanceJump` 在落地同一帧把 `Grounded` 置 true（`NpcAnimatorDriver.cs:268-278`），条件在落地帧即满足。
   - 亲自观测轨迹（临时在 ⑥ 内加 3 帧状态日志，观测后逐字节恢复，sha256 `8A27C6…C96F` 前后一致）：离 Jump 的第一帧起连续 4 帧 `curRun=True / inTransition=False / speed=1.500`，**没有经过 Idle 的过渡帧**——移动者落地直接 Jump→Run，无 1 帧闪 Idle 的抖动。
   - 该条件还把退出时机与 clip 时长解耦：实测 T=0.7821 s 时，落地后至少 3 帧仍停在 Jump（exitTime > T），若不带 `Grounded` 则空中早退的风险取决于 clip 长度；带上后保证不空中早退。
   - 残余风险（未观测到）：若 clip 的 exitTime 明显晚于落地，且外部在"已落地但状态仍为 Jump"的窗口内再次 `RequestJump`，触发器会因 Jump 无自过渡而滞留，状态可能先闪一帧 Run 再回 Jump；本任务用例未覆盖该窗口。

### 受限项（如实）

- 只跑 `editmode`/`playmode`，未跑 `arch`/`config`/`naming`（本任务未新增/移动 asmdef、未新增资产命名单元）。
- 只在 Editor PlayMode 验证，未出 Player 包验证。
- 未覆盖父节点 Y 变化、CrowdLOD `SyncL0` 覆盖等非 `SCN_Anim` 场景。
- Jump clip 的确切时长未量化；只观测到 `exitTime > T`（落地后仍停在 Jump ≥3 帧）。
- 控制器由 `SetupFromCommandLine` 重新生成，语义与改前逐项一致（6 过渡/3 参数/3 状态/Jump 过渡顺序/exitTime/条件），但 fileID 重建导致文件 sha256 由 `DA7519…AA4C` 变为 `DAC7EF…2834`；`.meta` sha256 `298360…2A32` 与 guid `9257ec50d6c67b241b65da2c0bb9cb6f` 未变，场景引用不变。
- 测试运行会改写与本任务无关的 `Docs/Parity/crowd_r9_15_{on,off}.png`；已 `git checkout --` 恢复到 HEAD，最终 `git status` 与开工前文件集合一致。
- 未 `git commit`；未改场景、Domain、输入资产、`NpcUtilityView` 及其它控制器。
