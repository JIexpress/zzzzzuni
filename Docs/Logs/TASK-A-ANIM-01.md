# TASK-A-ANIM-01 —— 人形骨骼动画系统：AI 驱动的状态机 + IK（用户需求：非播片）

状态: doing | 派发: 2026-11-04 | 施工: （待填） | 验收: （待填） | commit: （待填）

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

（待填）

## 验收清单（主控写）

- [ ] V1 亲跑 EditMode+PlayMode 全绿（数字写入结论）；四条可证伪用例真实（行号）
- [ ] V2 AnimatorController 程序化生成审查（幂等/纯函数/零手摆）
- [ ] V3 脑→动画驱动链审查：UtilityNPC 行为输出→Animator 参数（行号）；"动画状态=脑状态函数"断言实证
- [ ] V4 Look-at IK 断言实证（头部骨骼朝向角度）；动画速度-位移同步断言
- [ ] V5 文件规范+施工报告四节完整（含 clip 搬运选型与 IK 分级说明）

## 验收结论（验收代理写）

（待填）
