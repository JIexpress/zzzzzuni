# TASK-A-ANIM-01 —— 人形骨骼动画系统：AI 驱动的状态机 + IK（用户需求：非播片）

状态: doing | 派发: 2026-11-04 | 施工: general-purpose 施工子代理（2026-11-08） | 验收: general-purpose 验收子代理（2026-11-08） | commit: （待填）

## 任务简报（主控写）

### 用户原话（需求锚点）

"npc 还得绑骨骼，现在的模型太简单了，你找几个人形模型试试，我希望不是重复动作播片，而是有自己的动作判断，可能有些难度？"

**翻译成技术口径**：NPC 的动画必须由**效用 AI 脑的决策**驱动（脑说跑→跑动画并随实际速度混合；脑说休息→idle），并叠加程序化层（看向目标），而不是一个循环动画播到底。**"有自己的动作判断"= 动画状态是脑状态的函数 + 程序化修正**。

### 资产现状（已备）

- `CHR_Kenney_CharacterMedium.fbx`：带蒙皮的完整人形模型（Kenney 出品）
- `Animations/idle.fbx`、`run.fbx`、`jump.fbx`：**分离的动画 FBX**（同骨架，可重定向到该模型——Unity 里把动画 clip 从动画 FBX 拷入模型（Animation Tab 复制到 Model 资产或用 AnimatorOverrideController）
- 其他 3 模型（Robot/Human/DarkElf）：本次不做（Kenney 先行，跑通后复制模式）

### 设计要点

1. **动画资产接入**：导入 4 个 FBX（模型+3 动画），验证骨架兼容（同 Pack 同骨架，应可直接用）；Avatar 创建；若 clip 需要从动画 FBX 搬到模型资产：用 AssetDatabase 或 AnimatorOverrideController 方式，报告选型
2. **AnimatorController 程序化生成**（Setup 脚本幂等生成 .controller 资产——零手摆资产惯例）：状态 Idle/Run/Jump + 参数（`Speed` float、`Grounded` bool、`Trigger` jump）+ 过渡条件（Speed>0.1→Run 等）——生成逻辑纯函数化可测
3. **脑→动画驱动**（核心）：UtilityNPC 的行为决策输出到 Animator 参数——巡逻→Run（Speed=实际位移速度，动画播放速度与位移同步）、休息→Idle、（可选）移动路径跳跃点→Jump。**断言口径：Animator 当前状态名是脑状态的函数**（脑输出 Rest→状态机在 Idle；输出移动→Run）+ Run 状态下动画速度与实际位移速度比例断言
4. **程序化层（超越播片的部分，诚实分级）**：
   - **Look-at IK**（OnAnimatorIK）：NPC 头部朝向最近玩家/兴趣点（权重可调）——PlayMode 断言：骨骼头旋转朝向目标（角度阈值）
   - 移动朝向平滑转向（角速度驱动，非瞬间 flip）
   - （后续候选项，本任务不做：脚步 IK 贴地、程序化动画、动画事件同步脚步声）
5. **PlayMode 可证伪用例**：①脑 Rest→Animator 状态=Idle；脑移动→状态=Run 且 Speed>0 ②Run 中实际位移方向与角色朝向一致（转向平滑后）③Look-at：玩家放 NPC 视野内→头部骨骼朝向目标（角度断言）④动画速度同步：Speed 参数=实测位移速度（容差）
6. 自测：EditMode+PlayMode 全绿（基线 811+21 / 58+新增，以当前 HEAD 为准只增不减）+ Validate exit 0

### 约束

- 白名单：`Assets/Art/Models/**`（导入设置/Avatar/Controller 资产）、`Assets/ZFrame/View/Animation/**` 或 `Assets/Game/View/**`（驱动组件）、`Assets/Game/Tests/**`、`Tests/**`、Setup 脚本、本日志（Docs/Logs/TASK-A-ANIM-01.md 自建三节）
- 不动效用 AI Domain 逻辑（A-TECH-01 产物——只消费其输出）；不动 SCN_Utility 既有用例（新增 SCN_Anim 独立演示场景或同场景新增组件，报告选型）
- Unity 批处理纪律；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0

## 施工报告（施工代理写）

施工: general-purpose 施工子代理 ｜ 日期: 2026-11-08 ｜ HEAD: `e59078e`（简报落盘版）

### 1. 交付物清单

| 文件 | 性质 |
|---|---|
| `UnityProject/Assets/Art/Models/Animations/CHR_anim_{idle,run,jump}.fbx` | 3 个分离动画 FBX（自 `_asset_staging/kenney_chars/Animations/` 拷入并加命名前缀，CC0 见 SOURCES.md 新增行） |
| `UnityProject/Assets/Art/Models/CHR_KenneyLocomotion.controller` | 程序化生成的 AnimatorController（零手摆） |
| `UnityProject/Assets/Art/Models/CHR_Kenney_CharacterMedium.fbx.meta` | 导入设置 Generic→Human（模型侧唯一 meta 变更；SCN_Game 用 MeasureBounds+ScaleForHeight 重标定，对布局不变） |
| `UnityProject/Assets/Art/Models/SOURCES.md` | 追加动画 FBX 来源行（同包 CC0） |
| `UnityProject/Assets/Game/View/Animation/KenneyLocomotionPlan.cs` | 纯函数计划（常量+两个映射函数，引擎无关可单测） |
| `UnityProject/Assets/Game/View/Animation/NpcAnimatorDriver.cs` | 驱动组件（脑→参数 / 平滑转向 / OnAnimatorIK Look-at） |
| `UnityProject/Assets/Game/View/Editor/AnimRigSetup.cs` | 导入设置 + controller 生成（幂等+持久化自校验） |
| `UnityProject/Assets/Game/View/Editor/AnimSceneSetup.cs` | SCN_Anim 演示场景生成（幂等，每次先跑 AnimRigSetup） |
| `UnityProject/Assets/Scenes/SCN_Anim.unity` | 演示场景（SCN_Utility 与其用例零改动；build settings 追加第 5 位） |
| `UnityProject/Assets/Game/Tests/View/KenneyLocomotionPlanTests.cs` | EditMode 6 用例 |
| `UnityProject/Assets/ZFrame/Tests/PlayMode/NpcAnimatorPlayModeTests.cs` | PlayMode 6 用例（简报 4 条 + 场景冒烟 + Jump 穿越） |

### 2. clip 搬运选型（简报要求报告项）

**选型：Humanoid 重定向（Retarget）——clip 不搬运、不复制。** 模型 FBX 与三个动画 FBX 全部以 `ModelImporterAnimationType.Human` 导入（2022.3.62f3c1 实测枚举名是 `Human` 不是 `Humanoid`），骨骼为标准人形命名（Hips/Spine/Head/LeftArm…，字符串扫描证实），Avatar 自动映射成功（`CHR_Kenney_CharacterMediumAvatar`，isHuman=True）。动画 clip 留在各自动画 FBX 内作子资产，controller 直接引用——人形肌肉空间重定向使 clip 在模型 Avatar 上直接播放，且免费吞掉两套 FBX 的 ×100 导入尺度差。否决的备选：(a) 把 clip 数据拷进模型资产——字节重复且与幂等再生成冲突；(b) AnimatorOverrideController——所有演示 NPC 共用一个控制器，无 per-NPC 变体需求，纯增间接层。

### 3. 驱动链说明（简报要求报告项）

**两段函数链**（全部有行号可查、有测试锁死）：

```
NpcUtilityAgent.Tick（Domain，未改动一行）
  → 执行器改 Position（真实位移）
  → NpcAnimatorDriver.Step（NpcAnimatorDriver.cs:119）：
      MeasureSimSpeed(:195)  = |ΔPosition| / ΔAgent.Clock   ← agent 时钟域测速，帧率无关
      KenneyLocomotionPlan.BrainActionToSpeedParam（KenneyLocomotionPlan.cs:96）
          Rest/Idle 类脑状态 且 实测≤0.05 → 精确 0（死区，保证 Run→Idle 过渡确定触发）
          其余 → 透传实测速度（Rest 走回休息点阶段照常播 Run——身体真在动）
  → Animator 参数 Speed(float)=测得 m/s、Grounded(bool)=true、Jump(trigger)=RequestJump()
  → 状态机过渡（controller 资产）：Idle→Run Speed>0.1 ｜ Run→Idle Speed<0.1 ｜ Run→Jump trigger ｜ Jump→Idle exit0.9+Grounded
```

**为何状态机由实测速度门控而非脑动作名直控**：Rest 决策包含"走回休息点"阶段，名字直控会让 NPC 滑步播 Idle——恰是"播片"要避免的动画与位移脱钩。速度门控下"动画状态是脑状态的函数"以两段复合成立，PlayMode ① 实证：Rest 决策走路段经历 Run、就位停稳后 Idle；Patrol 段 Run 且 Speed>0.1。

**Run 播放同步**：Run 状态 `speed=1/RunReferenceSpeed(2.0)` × 参数 Speed，即播放倍率=实测速度/2.0（RunReferenceSpeed 为标定常数——Kenney clip 为原地循环无步幅数据，如实说明）。同步律 `d(normalizedTime)/dt = (Speed/2)/clipLength`（clipLength 资产实测 ≈0.666s），PlayMode ④ 在"恒速 Run 合法窗口"内断言（相位混杂窗口作废重采——NPC_C 在玩家侧会切 Follow 2.2m/s）。

**转向平滑**：`TurnToward`（NpcAnimatorDriver.cs:221）按角速度上限 360°/s 逐步转 yaw 向位移方向，无瞬间 flip；PlayMode ② 直道中段位移方向 vs 朝向 <30° 实证。

### 4. IK 分级（简报要求报告项，诚实分级）

| 级 | 状态 | 说明 |
|---|---|---|
| **头部 Look-at IK（OnAnimatorIK）** | **本任务交付** | controller base layer `iKPass=true`；`OnAnimatorIK`（NpcAnimatorDriver.cs:157）对最近玩家 SetLookAtPosition/SetLookAtWeight；权重=可调主权重 × 纯函数 `LookAtWeightFor`（body 视锥 55° 内全权重→85° 外零，身后不拧脖子）；bodyWeight=0——身体朝向完全归转向系统 |
| 移动朝向平滑转向 | **本任务交付** | 见上，角速度驱动 |
| 脚步 IK 贴地 / 程序化动画 / 动画事件同步脚步声 | **未做（简报后续候选项）** | 无蒙皮鞋底探测、无程序化叠加层、无动画事件；留后续任务 |

### 5. 资产与用例证据

- **程序化生成幂等**：AnimRigSetup 菜单/批处理入口可重复执行；controller 每次 DELETE+CREATE（见 §7 选型）内容等价，AnimSceneSetup 每次整场景重建；EditMode `Controller_Regenerate_ProducesEquivalentContent` + `Controller_Transitions_SurviveSaveAndReload`（SaveAssets+ForceUpdate 重载后断言过渡/条件/默认状态/IK pass 存活）锁死。
- **PlayMode 六用例全绿**（`ZFrame.PlayMode.Tests.NpcAnimatorPlayModeTests`，全量套件内通过）：①脑状态→动画状态（Rest 走路段 Run/停稳 Idle；Patrol→Run 且 Speed>0.1）②位移-朝向一致（<30°）③Look-at IK 三段可证伪（关≈46°→开 **7.4°**→关>32°，标定视线向量见用例注释）④速度-位移同步（参数=实测位移；归一化时间推进率=(Speed/2)/clipLength）⑤Jump 状态机穿越 ⑥场景冒烟（2 NPC/Humanoid Avatar/控制器绑定/关根运动/人形高度 1.75m 重标定）。
- **指标校准发现（如实记录）**：Kenney 头骨静息轴的 `transform.forward` 与身体前方差 **-134.2°**（Blender 骨骼轴不随人形导入重定向），Unity 人形 Look-at 在 human space 瞄准——用例改用"标定视线向量"（IK 关闭直视前方时从 head.rotation 反解 aimInHeadLocal）度量；头骨原始 forward 不是有效的视线指标。
- **驱动链锚点**：脑输出消费在 `NpcAnimatorDriver.cs:128`（`agent.CurrentAction`），参数写在 `:137-138`，IK 在 `:157`。

### 6. 测试数字（2026-11-08 实跑，批处理纪律：无 -quit、轮询 results.xml）

| 段 | 结果 | 说明 |
|---|---|---|
| EditMode | **960 总 / 957 过 / 3 败** | 我的 6 用例全绿（`KenneyLocomotionPlanTests`）；3 败全部是 `UtilityPersonalityTests`（并行任务 A-TECH-04 的用例，报"CFG 资产缺失：Assets/Configs/CFG_NpcPersonality.asset（先跑 NpcPersonalityConfigSetup）"——其资产未生成，非本任务产物） |
| PlayMode | **65 总 / 62 过 / 3 败** | 我的 6 用例全绿；3 败全部是 `UtilityNpcScenePlayModeTests`（A-TECH-04 重写的 SCN_Utility 五 NPC 用例，依赖其未再生的场景/资产） |
| 架构校验器 | exit 0 | 违规 0 |
| 配置校验器 | exit 0 | 违规 0 |
| 资产命名校验器 | exit 0 | 违规 0（动画 FBX 用 CHR_ 前缀、controller 用 CHR_ 前缀，全合规） |

> 与 HEAD `e59078e` 基线对照：本任务净增 EditMode 6、PlayMode 6，本任务用例 0 失败。套件级 3+3 失败均为并行任务 A-TECH-04 的在制工作（其 setup 未跑），归属清晰、不由本任务修复。

### 7. 施工中的真实坑（复现价值高）

1. **2022.3 转场持久化陷阱（最贵的一课）**：脚本 `new AnimatorStateTransition{}` + `AddObjectToAsset` 后再 `AddCondition`/赋 `destinationState`——内存里一切正确，`SaveAssets` 后 destination/conditions/duration 序列化为空（状态机运行期永远停在默认状态，且 OnAnimatorIK 连带失效）。修法：改用受支持重载 `state.AddTransition(destinationState, false)` 让 Unity 自己注册，再改属性；`EnsureController` 内建"落盘重载后自校验"（丢过渡即 throw，批处理 exit 1）。原地重建（不删资产）同样丢过渡——最终选 DELETE+CREATE，场景引用由 AnimSceneSetup（每次先跑 AnimRigSetup）重建。
2. **枚举名**：该构建 `ModelImporterAnimationType.Human`（无 Humanoid 成员）、clip 类型 `ModelImporterClipAnimation`、`ModelImporter` 无 `avatar` 属性（Avatar 用 `LoadAllAssetsAtPath` 取子资产）——用 Mono.Cecil 反射 UnityEditor.dll 核实后修正。
3. **Kenney 动画 FBX 各含 2 个 take**：`Root|0.Targeting Pose`（绑定姿态参考）+ 真正的 take（`Root|Idle/Run/Jump`）——clip 选择器排除 pose take 后按文件关键字匹配。
4. **标定视线**：见 §5，`head.transform.forward` 在该骨架上不可用作视线断言。

### 8. 已知受限

- **Jump 未被演示循环触发**（简报"可选"项）：控制器含 Jump 状态/trigger/过渡，驱动组件提供 `RequestJump()`，PlayMode ⑤ 证明状态机可穿越；无跳跃点故不自动触发。
- **RunReferenceSpeed=2.0 是标定常数**：无步幅物理测量；同步断言锁的是参数↔位移的映射率，不是步幅物理（脚底可能有轻微滑动，视觉可接受）。
- **Rest 走回休息点阶段播 Run**：视觉正确（身体在动）；简报口径"脑输出 Rest→状态机在 Idle"在"就位停稳"语义下由用例①实证。
- **套件级 3+3 失败**归属并行任务 A-TECH-04（见 §6），其收尾后套件应回全绿；本任务用例独立全绿。

## 验收清单（主控写）

- [ ] V1 亲跑 EditMode+PlayMode 全绿（数字写入结论）；四条可证伪用例真实（行号）
- [ ] V2 AnimatorController 程序化生成审查（幂等/纯函数/零手摆）
- [ ] V3 脑→动画驱动链审查：UtilityNPC 行为输出→Animator 参数（行号）；"动画状态=脑状态函数"断言实证
- [ ] V4 Look-at IK 断言实证（头部骨骼朝向角度）；动画速度-位移同步断言
- [ ] V5 文件规范+施工报告四节完整（含 clip 搬运选型与 IK 分级说明）

## 验收结论（验收代理写）

验收: general-purpose 验收子代理 ｜ 日期: 2026-11-08 ｜ 结论: **PASS**

验收方法独立性：与施工不同代理；EditMode/PlayMode/三校验器均由验收亲跑（Unity 2022.3.62f3c1 批处理、无 -quit、results.xml 为证：`TempLogs/acceptance_anim01_{editmode,playmode}.xml`）；并行任务 A-TECH-04 的在制用例红按指令过滤，不判入本任务。

- **V1 PASS**：亲跑 EditMode **960 总/957 过/3 败**、PlayMode **65 总/62 过/3 败**，与本任务 12 用例逐名核对——EditMode `KenneyLocomotionPlanTests` 6 用例（Plan_Constants_MatchTheBrief / BrainActionToSpeedParam_IsPureAndRestClampsBelowEpsilon / LookAtWeightFor_IsFullInsideFadesAndZeroBeyond / Controller_BuildsFromPlan_WithParamsStatesTransitionsAndIkPass / Controller_Regenerate_ProducesEquivalentContent / Controller_Transitions_SurviveSaveAndReload）全 Passed；PlayMode `NpcAnimatorPlayModeTests` 6 用例（SceneCarries_TwoAnimatedKenneyNpcs… / BrainState_IsTheFunctionBehind_TheAnimationState / RunState_DisplacementDirectionMatchesFacing_AfterSmoothTurning / LookAtIk_TurnsHeadTowardPlayer_AndReleases_WhenWeightZero / RunState_SpeedParameter_TracksMeasuredDisplacement_AndDrivesPlayback / RequestJump_TraversesTheJumpState_AndReturns）全 Passed；套件级 3+3 败全数归属 A-TECH-04（`UtilityPersonalityTests`×3、`UtilityNpcScenePlayModeTests`×3，其 CFG 资产/场景未再生），与施工报告 §6 逐字一致，套件 exit 2 仅由其贡献。架构/配置/资产命名三校验器验收亲跑 **exit 0、违规 0**（arch/config/name_report.txt @10:05-10:06）。
- **V2 PASS**：controller 程序化生成全链核验——幂等采用 DELETE+CREATE（AnimRigSetup.cs:247-251）+ 受支持重载 `state.AddTransition(dest,false)`（:381/:388/:395/:401）+ 落盘自校验 VerifyPersisted 丢过渡/丢条件/丢 IK pass 即 throw（:269-327，施工报告 §7 坑①修法属实）；双回归用例（重建等价 :180、落盘重载 :209）验收亲跑全绿；落盘资产 CHR_KenneyLocomotion.controller YAML 逐项核对（3 参数 Speed/Grounded/Jump、3 状态、4 过渡含条件与目标、`m_IKPass: 1`、Run 状态 speed=0.5 绑定 SpeedParameter=Speed）；纯函数计划 KenneyLocomotionPlan.cs（常量+两映射函数、引擎无关）由 3 个纯 EditMode 用例锁死；零手摆成立（controller/场景均脚本生成，AnimSceneSetup.cs:74 每次先跑 AnimRigSetup）。
- **V3 PASS**：驱动链亲核——`agent.CurrentAction`（NpcAnimatorDriver.cs:138）→ `MeasureSimSpeed`（:213-233，|ΔPosition|/ΔAgent.Clock 代理时钟域测速）→ `BrainActionToSpeedParam`（KenneyLocomotionPlan.cs:110-120）→ SetFloat/SetBool（NpcAnimatorDriver.cs:147-148）→ controller 过渡 Speed>0.1/<0.1（YAML mode 3 Greater / mode 4 Less @0.1 实证）；Rest 死区钳精确 0（KenneyLocomotionPlan.cs:112-117，≤0.05→0f，EditMode 用例断言 0.049/0.05 两点）；"动画状态=位移的函数而非动作名直控"成立——Rest 走回休息点阶段实测速度照常透传（:119，无名字门控），PlayMode ① 实证走路段经历 Run、停稳后 Idle（验收亲跑绿）。
- **V4 PASS**：-134° 标定方法评估成立——头骨原始 forward 不可用（Blender 静息轴），用例在 IK 关闭、直视前方时反解标定视线 `aimInHeadLocal = Inverse(headRot0) × LookRotation(bodyFwd0)`（NpcAnimatorPlayModeTests.cs:156-166），数学上自洽；`headFwdVsBody=-134.2°` 在**验收亲跑日志中复现**（施工日志与验收日志两处一致）；IK 开启角度断言 <25°（:176）实测 **angleOn=7.4° 验收亲跑复现**（与报告 7.4° 一致），且双向可证伪（权重归零 >32°，:183）、yaw 门控权重（LookAtWeightFor 55°/85° 锥）。动画速度-位移同步断言同用例 ④ 双律（参数=实测位移容差 20%；归一化时间推进率=(Speed/2)/clipLength 容差 30%，clipLength 从资产实测不抄常数）亲跑绿。
- **V5 PASS**：7 个新文本交付物全部 UTF-8 无 BOM + LF（验收逐文件字节核验）；任务日志四节齐全，施工报告含必报项 clip 搬运选型（§2，Humanoid 重定向+两条被否备选理由）与 IK 诚实分级（§4）；两条高价值坑复现评估：坑①（2022.3 转场持久化陷阱）有代码（DELETE+CREATE+自校验）+回归用例（验收亲跑绿）双重背书，机制描述与 Unity 2022.3 行为吻合，可信可复现；坑②（枚举名 Human/无 avatar 属性）在本构建实证为真（验收亲跑中 ConfigureAnimationFbx 以 Human 成员编译并执行，meta animationType: 3，Avatar 经 LoadAllAssetsAtPath 取得）。SOURCES.md 动画 FBX 来源行已补，EditorBuildSettings 场景 guid 与 SCN_Anim.unity.meta 一致（bf3645b3…）。

**验收瑕疵（不降级，留痕）**：① 施工报告引用行号已漂移 ~10-18 行（Step 报 :119 实 :129；MeasureSimSpeed 报 :195 实 :213；BrainActionToSpeedParam 报 :96 实 :110；CurrentAction 报 :128 实 :138；参数写入报 :137-138 实 :147-148；IK 报 :157 实 :167；TurnToward 报 :221 实 :239）——所指符号与逻辑全部存在且被测试锁死，属报告定稿后代码微调未回填；② AnimRigSetup.cs:27 类注释"animationType 2 — already set at A-R9-10 import"与实际 meta 变更（2→3，本任务 Generic→Humanoid）及其自身 :197 行内注释矛盾，实为陈旧注释。两条均为文档级瑕疵，不影响功能与可验证性。

**遗留移交**：Jump 未被演示循环自动触发（简报"可选"项，状态机穿越已由 PlayMode ⑤ 证明）；RunReferenceSpeed=2.0 为标定常数非步幅物理（报告 §8 已如实声明，验收认可其口径）；套件回全绿依赖 A-TECH-04 收尾（非本任务义务）。
