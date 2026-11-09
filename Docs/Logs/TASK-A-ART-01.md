# TASK-A-ART-01 —— 换掉抽象小人：接入人形角色（Mixamo Vanguard）

> 用户原话：「找个像人一点的模型，别像现在这么抽象的」。
> 现有角色是 Kenney 方块人 + 为占位用的胶囊；本任务换成一个**带骨骼、带 Idle/Run/Walk** 的人形。

## 一、素材（已就绪）

- 源：`three.js` 示例资产 `Soldier.glb`（Mixamo「Vanguard」人形，23 根骨骼，动作 `Idle/Run/Walk/TPose`）。
- 已用 Blender 5.2 转成 FBX：`GameFramework/TempLogs/blender/CHR_Soldier.fbx`（3.3MB，动作已推 NLA 导出）。
- **入库要求**：复制到 `UnityProject/Assets/Art/Models/Humanoid/CHR_Soldier.fbx`，并在 `Docs/SOURCES.md`
  记一行来源（Mixamo/three.js 示例，署名见源站）；不要引入来源不明的商业素材。

## 二、目标

1. **导入正确**：`Animation Type = Humanoid`、Avatar 自动生成成功（无 "Invalid avatar"）、
   `Idle/Run/Walk` 三段动画可被 Animator 引用；材质不粉红（URP/Lit）。
2. **替换 NPC 外观**：SCN_Game 里的 NPC（至少 `npc-anim-*` 这组）用新人形替换 Kenney 方块人；
   **保持 `NpcUtilityView` / `NpcAnimatorDriver` / 动画状态机契约不变**（控制器可复用，Humanoid 重定向）。
3. **不动玩法**：碰撞体尺寸、脚底贴地高度、`LookAt`/脚步 IK 仍工作；测试全绿。
4. **视觉证据**：`Docs/Parity/demo_stage_playerview.png` 重拍，人形可辨（截图里不再是一坨方块/胶囊）。

## 三、设计要点

- 优先**只换网格与 Avatar**：Animator Controller 若本身是 Humanoid 状态机，可直接复用；
  若 clip 绑定 Kenney 骨骼路径导致失效，就把控制器里的 clip 换成新模型的同名动作（Idle/Walk/Run）。
- 高度对齐：导入后量一次模型高度（Vanguard 约 1.8m），把 `Transform` 缩放到与现有 NPC 一致
  （现有角色约 1.0~1.2 单位高），脚底对齐 y=0。
- 材质：`CHR_Soldier` 自带的贴图若丢，先用 `MAT_CHR_Human` 兜底（不允许粉红）。
- 像素用例：换模型会改变画面，`*PixelEvidence*` 是**同帧 A/B 差分**，理论上不受影响；
  若有绝对阈值用例变红，**先报告再改**，不许为了绿灯去放宽门限。

## 四、白名单

- `UnityProject/Assets/Art/Models/Humanoid/**`（新增）
- `UnityProject/Assets/Scenes/SCN_Game.unity`（仅 NPC 外观引用）
- `UnityProject/Assets/Game/View/**`（仅在**必须**修 Avatar/控制器绑定时）
- `Docs/SOURCES.md`、`Docs/Logs/TASK-A-ART-01.md`、`TASKS.md`（仅本任务行）

**不动**：`NpcAnimatorDriver` 的 IK/跳跃语义、`SurvivalLoop`、`BuildMode`、AI 域。

## 五、验收清单

| # | 断言 | 判据 |
|---|---|---|
| V1 | Avatar 合法 | 导入后 `avatar.isValid`，无报错日志 |
| V2 | 动画可用 | Idle/Walk/Run 三段 clip 存在且时长 > 0 |
| V3 | 场景已替换 | SCN_Game 中 NPC 的 SkinnedMeshRenderer 引用新模型（非 Kenney） |
| V4 | 不回归 | EditMode/PlayMode 全绿（基线 1140 / 110） |
| V5 | 视觉证据 | 重拍 `demo_stage_playerview.png`，人形轮廓可辨（附前后对比说明） |
| V6 | 来源合规 | `Docs/SOURCES.md` 有记录 |

## 六、施工报告

### 1. 素材入库与导入（V1 / V2）

- 入库：`TempLogs/blender/CHR_Soldier.fbx`（3,342,956 B）→ `UnityProject/Assets/Art/Models/Humanoid/CHR_Soldier.fbx`
  （`Humanoid/` 目录与其 `.meta` 一并新增）。
- 导入（幂等脚本 `Assets/Game/View/Editor/HumanoidCharacterSetup.cs`，批处理入口
  `SetupFromCommandLine`）：`animationType=Human`、`importAnimation=true`、`optimizeGameObjects=false`
  （`GetBoneTransform` / IK 需要真实骨骼）、`importCameras/Lights=false`、`materialLocation=InPrefab`、
  `avatarSetup=CreateFromThisModel`。
- Avatar：`CHR_SoldierAvatar`，`isHuman=True`、`isValid=True`（V1）。
- Clip（V2）：源 FBX 有 8 个 take（Blender NLA 同时导出了 `Character|*` 与 `*.001` 两套）；
  脚本按名字去掉 `Character|` 前缀、丢弃带 `.` 的重复 take，只保留并改名：
  `Idle 1.958s` / `Walk 1.000s` / `Run 0.667s`，三者 `loopTime=true`（`clip.isLooping` 复核通过）。
- 控制器：`Assets/Art/Models/Humanoid/CHR_SoldierLocomotion.controller`，用**模型自带**的 Idle/Run
  生成 3 状态（Idle/Run/Jump）/ 6 过渡 / IK Pass，参数与 `KenneyLocomotionPlan` 完全一致。
  源资产没有 Jump take，Jump 状态复用 Idle；该控制器只给 SCN_Game 的静态 NPC 用（SCN_Anim 的
  行走 NPC 继续用 Kenney 控制器，见 §2）。
- **踩坑（已处置）**：FBX 里带了一个 Blender 参考球 `Icosphere`（`localScale=100`），它把缩放测量
  从 1.832 m 抬到 2.832 m、`minY` 拉到 −1.0；若照旧测量会把角色缩小到 2/3 并整体沉入地下。
  脚本在测量前剔除所有非 SkinnedMeshRenderer 的 MeshRenderer（记录在 `TempLogs/humanoid_setup.txt`）。
- 材质：模型自带 `VanguardBodyMat` / `Vanguard_VisorMat` **导入即为 URP/Lit**（无贴图、baseColor 0.91 灰白），
  按“优先用模型自带”保留；脚本仍逐槽守卫，非 URP / 空槽才回退 `MAT_CHR_Human`（杜绝粉红）。

### 2. 场景替换（V3）

- **SCN_Game**：不整场重建（现有 committed 场景带有生成后追加的内容——A-CONT-01 的草地地面材质，
  `GameSceneSetup` 重建会丢掉），改为就地补丁（`PatchGameSceneFromCommandLine`）：
  - `NPC` 根对象保留：`InteractableProxy`、`CapsuleCollider`（0.9/0.95/0.9）与位置全部不变；
  - 原来的胶囊 `MeshRenderer` 仅置 `enabled=0`（碰撞/交互不受影响）；
  - 新增子对象 `CHR_Soldier`（FBX prefab 实例）：`SkinnedMeshRenderer` = `vanguard_Mesh`
    + `vanguard_visor`，Avatar=`CHR_SoldierAvatar`，Controller=`CHR_SoldierLocomotion`（V3）。
- **SCN_Anim**：`npc-anim-a` / `npc-anim-c` 改用同一人形，由 `AnimSceneSetup` 重建场景：
  - `NpcUtilityView` 仍在 shell 上、`NpcAnimatorDriver` 仍在带 Animator 的模型上；
  - 控制器仍是 `CHR_KenneyLocomotion`（Humanoid 肌肉空间重定向到 Mixamo 骨架），
    `NpcAnimatorPlayModeTests` 对控制器名 / Avatar / 2 个 NPC 的钉子全部保持；
  - `_modelYawOffsetDeg` 保持 0（见 §3 的骨架翻转方案，避免与“位移方向=朝向”用例冲突）。
- 生成器同步：`GameSceneSetup.BuildNpc` 也改为挂人形，未来重建 SCN_Game 不会退回胶囊。

### 3. 尺寸 / 脚底对齐 / 朝向（实测值）

| 项 | 实测 |
|---|---|
| 模型 scale=1 包围盒（剔杂后） | **1.848 × 1.832 × 0.444 m**，`minY=0.000`（无 ×100 单位惊喜，Vanguard 约 1.83 m） |
| SCN_Game NPC 胶囊量得高度 | **1.900 m**（size 0.900/1.900/0.900，`minY=0.050`，根物体 y=1） |
| SCN_Game 人形缩放 | 目标 1.900 m → **localScale=1.091582**（父级 0.95 ⇒ 世界 1.0370）；落位后 **1.815 × 1.900 × 0.436，`minY=0.000`** |
| SCN_Anim 人形缩放 | 目标 1.75 m（与旧 NPC 一致）→ **localScale=0.955134**；运行时蒙皮 AABB 1.457 × 1.812 × 1.465，面罩中心 y=1.475 |
| 脚底 | 两个场景均按“量测后平移 `−minY`”落位，脚底 = 世界 y 0 |

朝向：该 FBX 的**视觉前向是 −Z**（Blender 导出约定，`Character` 节点自带 Z-up→Y-up 的 X180）。
- SCN_Game 静态 NPC：保持原生朝向（yaw 0 正对镜头，截图里是正面），不涉及行走语义。
- SCN_Anim 行走 NPC：在**骨架节点** `Character` 上再合成 180° 世界 Y（`Ry180 * 原生 X180`，不能直接替换，
  否则角色会侧躺），使“视觉前向 = transform.forward = 位移方向”；驱动器的 `_modelYawOffsetDeg` 保持 0。
  两个工程细节（都真实踩到并写进脚本注释）：① 必须在**绑定控制器之后**再翻，否则 Animator 重绑定会覆盖；
  ② batch 模式没有 Undo 栈，必须 `PrefabUtility.RecordPrefabInstancePropertyModifications`，
  否则 prefab 子节点覆盖在保存时被丢弃。

### 4. 自测与视觉证据（V4 / V5 / V6）

- **EditMode 1140 / 1140**（基线 1140，最终态复跑）：`TempLogs/runtests_editmode.xml`，Unity exit 0。
- **PlayMode 110 / 110**（基线 110）：`TempLogs/runtests_playmode.xml`（最终态复跑，含模型差分用例）。
- **一次真实回归与处置**：首次 PlayMode 为 109/110——`RunState_DisplacementDirectionMatchesFacing`
  实测 180°。原因是我先用了 `_modelYawOffsetDeg=180` 来补视觉朝向，而该用例按 `transform.forward` 判定。
  **处置方式**：没有改测试、没有改门限——改为“骨架翻转 + offset=0”，让 `transform.forward` 与视觉前向同时为真；
  复跑 110/110。`*PixelEvidence*` 的绝对阈值一个都没动（模型开/关差分在同一次 PlayMode 里仍绿）。
- **视觉证据**：`Docs/Parity/demo_stage_playerview.png` 已用
  `-executeMethod Game.View.Editor.StageScreenshot.RunFromCommandLine` 重拍（1280×720，机位 0/3.2/−4.2）。
  自检结论：门左侧的 NPC 为直立人形，头盔/护甲/双手/双靴轮廓清晰，Idle 姿态（不是 T-pose）；
  不再是胶囊或方块。特写：`TempLogs/art01_npc_crop.png`。
  说明：Animator 在编辑模式不推帧，原截图一直是绑定 T-pose；`StageScreenshot` 现用 `AnimationMode`
  在 35% 处采样 Idle 后再渲染（批处理日志 `TempLogs/unity_stage_shot.log` 里 `posed=1`）。
- **SCN_Anim 检查图**：`TempLogs/art01_scn_anim.png`（人形直立、视觉前向 +Z，与驱动方向一致）。
- **V6**：新增 `Docs/SOURCES.md` 一行（three.js 示例 `Soldier.glb` / Mixamo Vanguard，Blender 5.2 转 FBX）。
- 清理：回退了跑测试产生的无关 `Docs/Parity/*.png` 噪声，以及 `AnimSceneSetup` 重建时顺带重写的
  `CHR_KenneyLocomotion.controller`（内容语义相同，避免无意义 diff）。

### 5. 角色材质（纯白修复）

问题：Blender 导出的 FBX 不带任何贴图，内嵌 `VanguardBodyMat` / `Vanguard_VisorMat` 虽然 shader 是
URP/Lit，但只剩 `_BaseColor ≈ 0.91` 的灰白，实拍就是一具纯白塑料人。本步只改材质，模型 / 骨架 /
Animator / 碰撞体 / 场景结构全部未动。

- **Body** `Assets/Art/Models/Humanoid/MAT_CHR_SoldierBody.mat`：`Universal Render Pipeline/Lit`，
  `_BaseColor {0.30, 0.34, 0.40}`（低饱和深蓝灰，装甲底色）、`_Metallic 0.55`、`_Smoothness 0.55`。
- **Visor** `Assets/Art/Models/Humanoid/MAT_CHR_SoldierVisor.mat`：同 shader，
  `_BaseColor {0.08, 0.10, 0.14}`（深色）、`_Metallic 0.80`、`_Smoothness 0.85`（半反光）。
- **无贴图依赖**：两个材质的 14 个 `m_TexEnvs` 槽全部 `fileID: 0`，`_BaseMap` / `_MainTex` 为空；
  不存在悬空 GUID，杜绝粉红/丢图路径。
- **绑定**：`SCN_Game` → `NPC/CHR_Soldier` 下 `vanguard_Mesh` → Body、`vanguard_visor` → Visor，
  以 **prefab 实例级覆盖**（`m_Materials.Array.data[0]`）写入，`SkinnedMeshRenderer`、骨骼、Avatar、
  `CHR_SoldierLocomotion` 与 `CapsuleCollider` 均保持原样。`SCN_Anim` 的两个人形未改（不在本步证据范围）。
- **入口**：`HumanoidCharacterSetup.ApplySoldierMaterialsFromCommandLine`（幂等：材质已存在则只刷参数；
  逐 renderer 按名字派发，绑定数 ≠ 2 直接抛错）。报告：`TempLogs/soldier_materials.txt`。
- **视觉证据**：`Docs/Parity/demo_stage_playerview.png` 重拍（1280×720，机位 0/3.2/−4.2，`posed=1`）；
  特写 `TempLogs/art01_npc_crop.png`（480×780，取 playerview 中 143×233 区域 LANCZOS 放大 ≈3.35×）。
  自检：角色不再是纯白，呈深蓝灰装甲；头盔面罩、胸甲、腰带弹匣、双臂、双腿与靴子轮廓仍清晰可辨，
  与草地背景对比足够。
- **回归**：EditMode **1140 / 1140**、PlayMode **110 / 110**（`TempLogs/runtests_editmode.xml`、
  `TempLogs/runtests_playmode.xml`，均 exit 0）；未改测试、未改门限。跑测试顺带改写的
  `Docs/Parity/*` 噪声图已回退，仅保留本任务要求的 `demo_stage_playerview.png`。

改动文件清单：
- 新增 `UnityProject/Assets/Art/Models/Humanoid/{CHR_Soldier.fbx, CHR_SoldierLocomotion.controller}`（+ `.meta`）
- 新增 `UnityProject/Assets/Art/Models/Humanoid/{MAT_CHR_SoldierBody.mat, MAT_CHR_SoldierVisor.mat}`（+ `.meta`，§5）
- 新增 `UnityProject/Assets/Game/View/Editor/HumanoidCharacterSetup.cs`（导入 / 控制器 / 场景补丁 / 材质 / 诊断入口）
- 修改 `UnityProject/Assets/Game/View/Editor/AnimSceneSetup.cs`（NPC 换人形 + 朝向校准）
- 修改 `UnityProject/Assets/Game/View/Editor/GameSceneSetup.cs`（生成器同步挂人形）
- 修改 `UnityProject/Assets/Game/View/Editor/StageScreenshot.cs`（Idle 采样后再截图）
- 修改 `UnityProject/Assets/Scenes/SCN_Game.unity`（NPC 外观）、`SCN_Anim.unity`（两个 npc-anim 外观 + 骨架朝向）
- 新增 `Docs/SOURCES.md`；本日志
- 未 commit（施工代理不入库）


## 七、验收结论（待填）
