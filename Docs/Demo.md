# Demo —— ZFrame 垂直切片 + 真实模型舞台（R9-10 换代，TASK-A-R9-10；R9-14 补音频段）

> 本文档描述"能跑能玩"的垂直切片：Boot 场景全启动链 → 主菜单 →
> SCN_Game（移动/交互/敌人/拾取/暂停/设置/存读档 + **4 人物围圈 + 6 环境
> props 的真实模型舞台** + **事件驱动 BGM/SFX**）→ 回主菜单。
> 上一版（M4-02，2026-10-25）记录在 git 历史；本版以 2026-10-31 实测为准
> （证据：`Docs/Parity/demo_stage_*.png`、`Docs/Parity/demo_stage_log.txt`、
> `Docs/Logs/TASK-A-R9-10.md`；音频部分以 2026-11-04 实测为准，
> 见 `Docs/Logs/TASK-A-R9-14.md` 与 §3.5）。

## 1. 怎么跑

### 1.1 编辑器 Play（推荐的人工体验路径）

1. Unity Hub 打开工程：`UnityProject/`（Unity 2022.3.62f3c1）。
2. 打开场景 `Assets/Scenes/SCN_Boot.unity`（启动场景，Build Settings 第 0 位）。
3. 点击 Play。启动链自动执行：GameRoot 服务初始化 → 主菜单面板弹出。
4. 点"开始"进 SCN_Game：场景里现在是**真实 FBX 模型**——4 个角色围成一圈
   站在玩家活动区四周，树木/岩石/木桶/栅栏/石板路点缀边缘；金币拾取物
   就在最靠门的那个橙色角色（Kenney）脚边。

### 1.2 批处理命令（Git Bash，仓库根为 `C:/Users/1/Desktop/zcode/GameFramework`）

```bash
U="C:/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe"
P="C:/Users/1/Desktop/zcode/GameFramework/UnityProject"

# ① 模型资产准备（10 FBX 导入设置 + MAT_ 材质；幂等）
"$U" -batchmode -quit -projectPath "$P" \
  -executeMethod Game.View.Editor.ModelAssetSetup.SetupFromCommandLine \
  -logFile TempLogs/model_setup.log

# ② 重建切片玩法场景 SCN_Game（M4 切片 + R9-10 模型舞台；幂等）
"$U" -batchmode -quit -projectPath "$P" \
  -executeMethod Game.View.Editor.GameSceneSetup.SetupFromCommandLine \
  -logFile TempLogs/scene_setup_r910.log

# ③ 重建切片启动资产（两个 UI prefab + SCN_Boot 装配；幂等）
"$U" -batchmode -quit -projectPath "$P" \
  -executeMethod Game.Bootstrap.Editor.BootSceneSetup.SetupFromCommandLine \
  -logFile TempLogs/demo_setup.log

# ④ 舞台截图 + 尺寸记录（产出 Docs/Parity/demo_stage_*.png + demo_stage_log.txt）
"$U" -batchmode -quit -projectPath "$P" \
  -executeMethod Game.View.Editor.StageScreenshot.RunFromCommandLine \
  -logFile TempLogs/stage_screenshot.log

# ⑤ 启动链截图（编辑态模拟，产出 Docs/Parity/demo_boot.png）
"$U" -batchmode -quit -projectPath "$P" \
  -executeMethod Game.Bootstrap.Editor.DemoBootFlow.RunFromCommandLine \
  -logFile TempLogs/m4_02_demoboot.log
```

> 纪律：跑前确认无 Unity 实例（`tasklist | grep Unity.exe`）并删
> `UnityProject/Temp/UnityLockfile`；一次一个实例；测试**勿带 -quit**
> （上面 ①-⑤ 是 executeMethod 自退出的生成器，带 -quit）；
> 证据以 results.xml / 生成的 png+log 为准；xml 已生成但 5 分钟未退出
> → `taskkill //PID <pid> //F`。

### 1.3 测试回归（本任务的两条像素证据）

```bash
rm -f UnityProject/Temp/UnityLockfile
"$U" -batchmode -projectPath "$P" \
  -runTests -testPlatform PlayMode \
  -testResults "$P/TempLogs/r910_playmode.xml" -logFile "$P/TempLogs/r910_playmode.log"
```

关键用例（`ZFrame.PlayMode.Tests.ModelStagePixelEvidencePlayModeTests`）：

- `ScnGame_ChristmasModels_ChangeTheCameraImage`——加载**已提交的 SCN_Game**，
  四个 CHR 根隐藏/显示两帧像素差分：模型必须真实改变相机图像
  （moved/darker 双门限）；同一趟对每个 CHR 渲染器做"无粉红 + URP 着色器"核对。
- `ScnGame_PickupInteract_SparksBesideTheRealModel`——走**真实域路径**
  （场景里的 PickupEntity.Interact → ItemPickedUp 事件 → GameplayFxBindings →
  ParticleRuntime.SpawnHitSpark），火花必须点亮在 Kenney 模型旁的像素上
  （brighter ≥ 0.004，与 R9-05/06 口径一致）。

## 2. 看什么（体验清单）

| # | 玩法 | 操作 | 预期 |
|---|---|---|---|
| 1 | 启动链 | 进 Play | 主菜单弹出（无存档时只有 开始/设置/退出） |
| 2 | 移动 | WASD / 手柄左摇杆 | 胶囊玩家相机相对移动（第三人称固定镜头），穿过真实模型组成的舞台 |
| 3 | 交互 | 走近门/金币/NPC，按 **E** | 世界空间提示"按 E 开门/拾取/对话"；门开/关状态切换、金币入包（HUD 计数增加）、NPC 台词逐行推进；**拾取瞬间金币旁绽放火花**（R9-06 事件链，位置在模型旁） |
| 4 | 敌人 | 靠近/远离敌人 | Idle 灰 → Patrol 黄（巡逻路点）→ Chase 红（追击）→ Dead 深灰；死亡时该位置也有一簇火花 |
| 5 | 粒子（持续） | 站在场景里看 | 命中火花（攻击/拾取/门开合/敌人死亡/对话完结 5 源，同帧同点合并）+ 相机锚定的环境浮尘 |
| 6 | 暂停 | **Esc** | 暂停面板；玩家与敌人停止移动；再按恢复 |
| 7 | 设置 | 主菜单/暂停面板点"设置" | 五页签：画面（超分五选项 + 画质档/DRS/帧率目标 + 插帧开关）、操作（重绑定）、音频、辅助、其他；"应用并关闭"持久化 |
| 8 | 存读档 | 暂停 → 回主菜单 | 离开 SCN_Game 自动存槽位 0（背包+位置）；主菜单出现"继续" |
| 9 | 音频 | 进 Play 听 | 主菜单起 BGM（cave 主题，AudioService 交叉淡化双轨）；进 SCN_Game 走 SceneLoadedEvent→PlayBgm 切游戏轨；拾取"叮"（金币声）、门开合"吱呀"、敌人攻击"劈砍"（3D 定位）、面板弹出"咔哒"（2D）；音频页签四总线滑杆即时生效（MIX_AudioMixer 已实接） |
| 10 | 截图证据 | —— | `Docs/Parity/demo_stage_playerview.png`（游戏相机所见）、`demo_stage_overview.png`（全景俯瞰）、`demo_stage_face_*.png` ×4（朝向标定）、`demo_stage_log.txt`（每个模型的 scale/finalSize 记录）、`demo_boot.png`（启动链） |

**真实模型舞台布局**（`Assets/Game/View/ModelStageLayout.cs`，单一事实源）：

- 4 角色：CHR_Kenney（门旁东 30°）、CHR_Robot（东 110°）、CHR_Human（南 200°，
  男+女一对）、CHR_DarkElf（西 290°）——半径 4.5 m 围圈；朝向经 face 截图标定：
  **仅 Kenney 面向圆心，Robot/Human/DarkElf 背对**（原生朝向因模型而异，见第 2 步标定）。
- 6 props：两种树各 ×2 在地面边缘（|x|,|z| ≥ 7）、ENV_Rock 在东南、
  ENV_Barrel 在 Kenney 旁、ENV_Fence ×2 在北缘、ENV_Road 石板路从玩家起点
  向北延伸（下沉嵌入地面，只露 2 cm 顶面）。
- 尺寸：每个模型"实测包围盒 → 目标高度"换算（角色 1.70-1.90 m、树 4.2-5.2 m、
  岩 1.0 m、桶 0.95 m、栅栏 1.1 m、路长 9 m），原始尺寸与 scale 记录在
  `Docs/Parity/demo_stage_log.txt` 与任务日志。

## 3. 如何添加一个新模型（分步指南，按本任务实际走过的路）

> 以下每一步都是本任务（TASK-A-R9-10）真实执行过的路径；命令在仓库根的
> Git Bash 里跑。示例以一个假想的 `CHR_Knight.fbx` / `ENV_Well.fbx` 为准。

### 第 0 步：放文件 + 记来源

1. 把 FBX 拷进 `UnityProject/Assets/Art/Models/`，命名带前缀
   （CHR_ / ENV_ / WPN_ / UI_，见 `Docs/Standards/file-conventions.md` §5，
   资产命名校验器会查）。
2. 在同目录 `SOURCES.md` 表里加一行：来源 URL、许可证、精度档
   （CC-BY 条目对外发布需署名）。**不要**把 zip/原文件入库（原始包留在
   `_asset_staging/`，不入库）。

### 第 1 步：Unity 导入（拿到 .meta）

```bash
rm -f UnityProject/Temp/UnityLockfile
"$U" -batchmode -quit -projectPath "$P" \
  -executeMethod Game.View.Editor.ModelAssetSetup.SetupFromCommandLine \
  -logFile TempLogs/model_setup.log
```

首次导入自动生成 `.meta`（必须与 FBX 一起提交；禁止手改 guid）。

**坑 1（尺寸）**：Blender 导出的 FBX 常带 ×100 的节点缩放，导入后角色实测
180-208 m 高（本任务探针实测，见 `TempLogs/model_probe.txt` 的
`worldSize` 列）。**不要信文件自带单位**——第 3 步的场景生成器会"实测
包围盒 → 除以目标高度"，你只需要在第 2 步给对目标身高。

**坑 2（相机/灯）**：.blend 转出来的 FBX 可能夹带 Blender 场景的
Camera/Lamp 节点——导入后就是会渲染的 Camera/Light 组件（跑 Play 时
画面会被它们抢走）。`ModelAssetSetup` 已统一关
`importCameras`/`importLights`（幂等，重跑无害）。

**坑 3（材质粉红的真相）**：本项目的 FBX 自带材质，Unity 2022.3 在 URP
工程里导入时**自动用 URP/Lit 生成**——编辑器里其实不粉；真正的病是
"没有贴图→白灰一片"。处置见第 2 步。另外**不要**照网上教程把
materialLocation 改 External + AddRemap 来换材质——本任务实测：改完
Unity 会把内嵌材质全部自动抽到 `Art/Models/Materials/` 下（14 个散装
.mat），而且残留的 remap 表会让内嵌材质再也生不出来（一堆空槽）。
本项目最终做法 = 保持 InPrefab + 场景侧显式赋 MAT_。

### 第 2 步：登记材质与布局（改两个表）

1. **材质颜色**：`Assets/Game/View/Editor/ModelAssetSetup.cs` 的
   `BaseColor` 表加一行（模型名 → 合理纯色；本任务无贴图，全部用纯色：
   人物肤色/布色、树叶绿、岩石灰等）。`ModelFiles` 数组加上文件名。
   `MAT_<模型名>.mat`（URP/Lit，`_Smoothness=0.25`）由**第 3 步场景生成
   （GameSceneSetup）时自动创建**——只跑本步不建材质（本任务实测验证过
   这一时机）。
2. **摆放**：`Assets/Game/View/ModelStageLayout.cs`——
   - 角色 → `Characters` 数组加 `MakeRing("CHR_Knight", 角度, 目标身高)`
     （自动上圈、自动面朝圆心）；
   - 物件 → `Props` 数组加 `new Placement("ENV_Well", 位置, yaw, 目标高度)`；
   - **朝向标定**：`ForwardYawOffset` 里加你的模型（先填 0）。**原生朝向
     因模型而异**：本任务实测 Kenney 原生 -Z（offset 0 正确），而
     Robot/Human/DarkElf 上圈后背对圆心——各自 offset 已按
     `demo_stage_face_*` 截图标定进 ModelStageLayout。你的模型若背对，
     同样把 offset 改 ±90/180 再跑一次第 ④ 条截图命令对着
     `demo_stage_face_*` 图核对。

### 第 3 步：生成场景（幂等，不手摆）

```bash
"$U" -batchmode -quit -projectPath "$P" \
  -executeMethod Game.View.Editor.GameSceneSetup.SetupFromCommandLine \
  -logFile TempLogs/scene_setup.log
```

生成器（`Assets/Game/View/Editor/GameSceneSetup.cs`）会：实例化 FBX →
实测包围盒缩放到目标尺寸 → 沉到地面（`SinkTopAboveGround` 的薄片物件如
路面只露顶）→ 每个渲染器槽位赋 `MAT_<模型>`（防粉红的决定性一步）→
关掉 FBX 自带的 LOD1/LOD2 兄弟渲染器（这些包的 LOD 是三个平铺
MeshRenderer，不关会 z-fight）。场景从零重建，重复跑结果一致。

**坑 4（蒙皮包围盒虚胖）**：SkinnedMeshRenderer 的实时 AABB 被骨架的
×100 缩放撑大（本任务 Robot 实测 2.28 m "宽"），测量必须用
`sharedMesh.bounds × localToWorldMatrix`（GameSceneSetup.MeasureBounds
已处理）；否则缩放会偏小。

### 第 4 步：验证（像素，不是"感觉在"）

```bash
# 截图 + 尺寸记录（placed=10/10、shots=6/6 才 exit 0）
"$U" -batchmode -quit -projectPath "$P" \
  -executeMethod Game.View.Editor.StageScreenshot.RunFromCommandLine \
  -logFile TempLogs/stage_screenshot.log
```

看 `Docs/Parity/demo_stage_log.txt`（scale/finalSize/minY）与四张
`demo_stage_face_*.png`（面朝圆心=标定成功）。再跑 1.3 节的 PlayMode
像素证据（新模型若要进证据链，照
`ModelStagePixelEvidencePlayModeTests` 的写法加一条：隐藏/显示差分或
事件→火花差分）。

### 第 5 步（可选）：烘焙进 ZRender 自研管线

选中模型的 Mesh（Project 窗口点 FBX 里的 mesh 子资产，或场景中带
MeshFilter 的物体）→ 菜单 `ZFrame/ZMesh/Bake Selection`（`ZMeshBaker`，
TASK-B-R4-01）→ 同目录生成 `.zmesh` v1（位置/法线/uv0/索引 SoA 列式，
小端），供 ZRender 原生内核（数据内核/RHI）直接吃。批量路径见
`ZMeshBaker.BakeBuiltinCubeToRepoData` / `BakeGalleryToRepoData` 的写法
（B-R9-13 的 assets gallery 即此路线，产物在 `Native/tests/data/*.zmesh`）。

### 步骤总览（一图流）

```
FBX 入库(命名+来源) → ModelAssetSetup(导入设置+MAT_)
  → ModelStageLayout(摆哪/多大/朝向) → GameSceneSetup(生成场景)
  → StageScreenshot(截图+尺寸记录) → PlayMode 像素证据
  → (可选) ZMeshBaker → .zmesh 进 ZRender
```

## 3.5 如何添加一个新音效（音频段，TASK-A-R9-14 实路）

> 以下每一步都是本任务（A-R9-14）真实执行过的路径。现状：BGM 一首
> （`AUD_bgm_cave.ogg`）、SFX 四枚（pickup/door/hit/ui_click，全部 CC0）。
> 键→文件映射只有一个规则（`AudioService.AssetPrefix`）：
> 资源键 `sfx_pickup` → 资产文件 `Assets/Audio/Resources/AUD_sfx_pickup.ogg`
> ——键保持 `bgm_/sfx_` 前缀（API 稳定），文件按命名规范带 `AUD_`。

### 第 0 步：放文件

把 ogg/wav（Unity 原生支持，单文件 ≤2MB）拷进
`UnityProject/Assets/Audio/Resources/`，命名 `AUD_sfx_<名>.ogg`
（命名校验器查 `AUD_` 前缀；`.meta` 由 Unity 首次导入自动生成，
必须与音频一起提交）。

### 第 1 步：记来源

`Assets/Audio/SOURCES.md` 表里加一行：文件、来源 URL、许可证
（CC-BY 对外发布需署名）。原始 zip 包留在 `_asset_staging/`（gitignore，不入库）。

### 第 2 步：事件接线

`Assets/Game/View/Effects/GameAudioBindings.cs`——所有声音只从这里出
（Domain 层零音频引用是架构校验器红线）：

- 世界事件：照 `OnItemPickedUp` 的写法加一个 `EventBus.Subscribe<T>(OnX)`
  + `RequestSfx("<名>", ToWorld(evt.Position), "x:...")`（EventScope 统一回收）；
- UI 声：`RequestSfx("<名>", null, ...)`（2D）；
- BGM：新曲目是 `AUD_bgm_<名>.ogg` 入库后，把 `BgmIdForScene` 的场景映射表
  加一行（菜单/游戏各一轨；单轨现状=两场景同指 "cave"，交叉淡化照常工作）。

同帧去重不用接线方操心：`AudioService` 的 SfxLimiter（同 id 同帧 + 每帧预算 4）已兜底。

### 第 3 步：验证（可证伪，不是"听感觉"）

- EditMode 映射：`Game/Tests/View/GameAudioBindingsTests.cs`（真 EventBus +
  录音 mock：事件→sfx id/2D-3D/位置的断言）；
- PlayMode 真放：`ZFrame/Tests/PlayMode/GameAudioBindingsPlayModeTests.cs`
  （事件→**AudioSource.isPlaying** 的真断言：pickup→SFX 池真在放、
  SceneLoadedEvent→BGM 轨 isPlaying 且音量随游戏循环 Tick 爬升）。
  命令照 §1.3 跑（`-runTests -testPlatform PlayMode`，勿带 -quit）。

```
ogg 入库(AUD_ 前缀) → SOURCES.md 记来源
  → GameAudioBindings 接事件(EventScope, Domain 零引用)
  → SFX 自动走 SFX 总线 / BGM 走 BGM 总线(设置页四总线已实接)
  → EditMode 映射 + PlayMode isPlaying 双证据
```

## 4. 当前能力全清单（2026-10-31）

**玩法切片（SCN_Game）**：第三人称固定镜头移动（WASD/手柄，输入可重绑定）；
交互系统（射线 2 m + 域层择近，门开关/拾取入包/NPC 对话推进）；敌人 FSM+行为树
双脑可切（Idle/Patrol/Chase/Dead，HUD 状态行）；暂停（引擎 timeScale=0 +
框架 GameTime 冻结 Logic 档）；存读档（三槽位、原子写+滚动备份，自动存槽 0）；
数据驱动五页签设置面板（即时生效 + 持久化 settings.json）。

**真实模型舞台（R9-10）**：4 人物围圈（Kenney/Robot/Human×2/DarkElf，
朝向已按 face 截图标定：仅 Kenney 面向圆心、其余背对）+ 6 props 点缀
（双树×2/岩/桶/栅栏×2/石板路），全 FBX 实例化、
实测缩放、MAT_ URP Lit 材质、地面吸附；拾取物在 Kenney 旁——
拾取火花在模型旁绽放。

**粒子（R9-05/06/07）**：零资产运行期建材质的命中火花（5 类事件源：
攻击/拾取/门开合/敌人死亡/对话完结；同帧同点合并为 1；池化有界 ≤200）
+ 相机锚定环境浮尘；`ParticleRuntime.Enabled` 单开关（关=零粒子零开销）。
Domain 层零粒子引用（架构校验器实证）。

**音频（R9-14）**：AudioService 首次实战出声——BGM 随场景切换
（SceneLoadedEvent→映射表→交叉淡化双轨，boot 场景兜底起播）+ 三世界事件
SFX（拾取/门/攻击，3D 定位）+ 面板音（2D ui_click）；资产 5 枚 ogg 全 CC0
（`Assets/Audio/Resources/AUD_*.ogg`，来源见同目录 SOURCES.md）；
MIX_AudioMixer 移入 Resources 后设置页四总线滑杆真实生效；
GameAudioBindings 单点接线（EventScope，Domain 零音频引用）；
EditMode mock 映射 10 例 + PlayMode isPlaying 真断言 3 例。

**后处理/画质（R9-04/08 + M2）**：PostFxCameraRuntime 运行期把
`renderPostProcessing=true` 补到所有相机；双 Volume 叠放按画质档
0.8 s 权重渐变（Low 空/Medium Bloom/High Bloom+调色）；PipelineManager
`SetTier` 切 QualitySettings + URP 资产 + Grade Volume 三件套并持久化。

**DRS/帧率/插帧（R9-01/02/03 + B-R9）**：设置页帧率目标（30/60/120，
FrameGovernor 预算判定）+ 动态分辨率（写前落盘日志、崩溃自愈）；插帧开关
（真生效、如实标注"抗锯齿降至 1×"；FG 走 dxgi.dll 代理 + FFX 3.1.6，
仅 Windows Player 生效，Editor 内不可用）。

**超分（B-R6）**：设置页五选项（自动/关闭/FSR 3/FSR 4/DLSS），FSR 3.1
本机核显真机验证过，FSR4/DLSS 为外部补验项；实际渲染路径由 Track B 按
preferred 后端选择。

**双管线/双后端（Track B + M2）**：URP 档位链（Low/Medium/High 三档资产）
+ 自研 ZRender 内核（D3D12/Vulkan 双后端全特性对等，RenderGraph/簇光/
PBR/CSM/SSR/GI/NPR），编辑器菜单 `ZFrame/Verification/...` 可核。

**编辑器工具链**：场景/UI/Boot 三个幂等生成器 + ModelAssetSetup +
StageScreenshot（本任务新增）+ ConfigWorkbench + UI 校验器 +
资产命名校验器 + 架构校验器（asmdef 矩阵门禁）+ ZMeshBaker。

## 5. 当前限制清单（诚实版）

1. **角色是 T-pose**：这批 FBX 没有可用的动画剪辑（探针 clips=0——
   Kenney/Robot 源页面的动画在 Blender 转换时丢失），绑定姿势即 T-pose；
   人形站立但无 idle/走路动画。接入动画 = 给实例配 AnimatorController，
   属后续任务。
2. **玩家死亡未实现**：切片没有生命值/伤害判定（HUD 提示行语义是
   "守卫被击败"）。
3. **敌人无攻击**：FSM/BT 只追击改色，接触玩家无伤害（攻击事件链已有，
   火花在玩家位置绽放——由测试驱动）。
4. **门无视觉动画**：门开/关是 Domain 状态翻转 + 提示文案变化，门体不
   动（R9-07 起有火花反馈）。
5. **NPC 对话只有推进没有对话框 UI**（世界空间提示可见 prompt 变化）。
6. **"退出"按钮在编辑器里不退出**（Unity 行为，打包后才真退）。
7. **模型无碰撞体**：FBX 不带 collider，玩家可穿过角色/树（M4 的
   Door/NPC 胶囊有碰撞）。要挡人 = 第 3 步生成器里给挂 MeshCollider。
8. **单色模型**：FBX 自带的按槽位配色（Robot 橙/灰/黑、DarkElf 发黑肤灰）
   在资产层保留；但场景里每个模型统一用 1 张 MAT_（本项目配色计划），
   子槽位细节暂不区分（一个模型多材质 = MAT_ 拆槽位，属后续打磨）。
9. **`demo_boot.png` 是编辑态模拟**（真实端到端启动链由 PlayMode 冒烟
   覆盖）；`demo_stage_*` 是编辑态相机渲染，与 Play 实际画面一致
   （同一场景内容、同一光照）。
10. **超分/插帧选项仅 Unity 侧实接**（FSR4/DLSS 需独显真机；插帧仅
    Windows Player）。
11. **交互射线 2 m**：更远的目标不响应；玩家需把目标对进固定视角的
    射线路径。
12. **中文字形风险**：HUD/世界空间文本用 `LegacyRuntime.ttf`，个别中文
    字符可能缺字形。
13. **BGM 单轨**：曲库只有一首 cave 主题，菜单/游戏映射同轨（交叉淡化
    照常工作，语义上仍"切轨"）；新 BGM = 一枚 `AUD_bgm_*.ogg` +
    `BgmIdForScene` 加一行（§3.5 第 2 步）。
14. **BGM 的 `AUD_bgm_cave.ogg` 来源 URL 缺失**：主控采集入库时未随附
    URL（简报口径 CC0）；SOURCES.md 已如实标注，补 URL 需查主控采集记录。
