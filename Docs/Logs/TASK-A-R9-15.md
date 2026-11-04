# TASK-A-R9-15 —— 千人演练场：开箱可见 + 真实像素证据

状态: doing | 派发: 2026-11-04 | 施工: builder-r9-15（general-purpose） | 验收: verifier-r9-15（须与施工不同代理） | commit: （待填）

## 任务简报（主控写）

### 背景

A-R9-13（`5f49a42`）验收 **PASS with 受限**，V3 明确：`KernelCrowdHost.AutoEnable` 默认 **false**，群体靠编辑器菜单 `KernelCrowdMenu` 手动开启；PlayMode 用例是显式 `EnsureCrowd()`，证明的是组件/提交层，**不是「进 SCN_Game 即开箱可见」**；`-batchmode` 下真实 GPU DrawCall 与像素可见性未取证。本任务把这两个受限项补上。

### 前置调研（施工第一件事，别跳过）

读 `Docs/Logs/TASK-A-R9-10.md` 与 `Assets/Game/Tests/View/` 下的相机差分 / 像素证据用例，查清 A-R9-13 施工把 `AutoEnable` 设为 false 的**具体原因**（污染了哪条像素断言、差分量级），再决定方案。报告里必须写清「为什么原来要关」以及「现在为什么可以开」。

### 目标

1. **默认可见**：进 `SCN_Game` 群体自动可见（`AutoEnable` 打开或按场景门控自动挂载），英雄侧玩法 / FSM / 场景布局零改动。
2. **不破坏既有像素用例**：A-R9-10 的相机差分 / 像素证据用例继续全绿。若确实冲突，给出可证伪的协调方式（例如把群体排除在差分采样区、或让差分用例显式关闭群体），**严禁删除、放宽、跳过既有断言**，也不许靠加重试 / 改容差蒙过去。
3. **真实像素证据**：落盘两张可复核的图（群体开 / 关）+ 像素差统计，证明群体真的出现在画面里，而不是「只有 1 次提交调用」。可选路径：DevServer `shot` 端点（`127.0.0.1:17321`，A-DEV-01/02，gameview 源已验证与相机源不同）、PlayMode `ScreenCapture` + 读回像素、或 Editor 截图。报告写明测量方式、图片路径、差异数字与判定阈值。
4. **真实 GPU DrawCall**：若能在 Editor Play Mode 用 `ProfilerRecorder` / Frame Debugger 取到可信值就给实测；批处理下取不到就如实标受限，**不编数字**。
5. **测试**：PlayMode 新增用例断言「默认进入场景后群体可见」+ 像素差异；EditMode **880** / PlayMode **46** 只增不减；arch/config/naming 三校验器 exit 0；native 311 不降。

### 约束

- 白名单：`UnityProject/Assets/Game/**`、`UnityProject/Assets/ZFrame/Tests/**`、`Docs/Logs/TASK-A-R9-15.md`（仅「施工报告」节）、`Docs/Demo.md`（仅当体验项变化）、`Docs/Parity/**`（仅当落图片证据）。
- 禁止：`Native/**`、`ZRender/**`、`ZFrame/View/Hud/**`、`Assets/Audio/**`、`ZFrame/View/Audio/**`、`TASKS.md` / `HANDOFF.md` / `WORKLOG.md` / `README.md` / `DECISIONS.md`；不 `git add` / 不 `git commit`；不手改 `.meta`；UTF-8 无 BOM + LF。
- **保持 A-R9-13 的批量绘制机制**：单次 `Graphics.DrawMeshInstanced` 提交 1000 实例不得退化；`EachBatches` 游标迭代与 `Dispose()` 生命周期不得破坏。
- 不要 `git checkout --` 丢弃 `UnityProject/Assets/Plugins/ZRender/zrender.dll`（含 12 个 `ZK_*` 导出，是本任务运行前提）。
- 唯一可写的日志节是「施工报告」；**不要写「验收结论」**，不要勾选验收清单。

### 环境纪律（真实踩过，详见 `Docs/交接档案.md` §3）

- 跑测试前清 8 个代理变量（大小写成对），否则 MSBuild / Unity 启动失败。
- Unity 批处理单实例：跑前查进程 + 删 `UnityLockfile`；跑测试**勿带 `-quit`**；xml 出现但 5 分钟不退 → `taskkill //PID <pid> //F`；证据以本次新写 results.xml 为准。
- Bash 侧禁止转调 powershell → 用 PowerShell 工具 `& .\Tools\run_tests.ps1`；本机 GUI Editor 加 `-force-d3d12` 会挂起。
- 清理用 `mv` 归档，不要 `rm -rf`；跑完查 `git status`，Editor 自动改写 `URP_*.asset` / `QualitySettings.asset` / `ProjectSettings` 时只针对这几个具体文件还原。
- 跨跑性能比较前跑 `Tools/check_machine_quiet.ps1`；不可信时改用同一次运行内 A/B 对照。

### 铁律

施工与验收必须不同代理；一任务一 commit；日志四节齐全；验收代理须亲自做「修前必红」定向变异；证据不齐写受限，绝不把「部分完成」说成「已完成」。

## 施工报告（施工代理写）

**施工**: builder-r9-15（general-purpose 施工代理） ｜ **日期**: 2026-11-04 ｜ **进场 HEAD**: `5742883` ｜ **自测**: 五段全绿（见 §六）

### 一、前置调研结论：A-R9-13 为何把 `AutoEnable` 设为 false，现在为何能安全打开

#### 1.1 原始理由（A-R9-13 口径：只有「有可能」，从未量化）

A-R9-13 施工报告 §2.4 / §6.2 与验收 V3 的原文是「自动绘制 1000 个方块**有可能**污染 A-R9-10 的 SCN_Game 像素差分」「会牵动 A-R9-10 的 SCN_Game 相机差分用例」，并把「既有 PlayMode 44/44/0 全绿」当作取舍依据。**但 A-R9-13 从头到尾没有指出具体污染哪条断言、也没有给出任何差分量级数字**——「44/44/0 全绿」只能证明「关着的时候是绿的」，不能证明「开着会污染」。本任务第一件事就是把这条从未被验证的顾虑查实并量化。

#### 1.2 代码级定位：具体会污染哪条像素断言

像素证据用例的实际位置是 `UnityProject/Assets/ZFrame/Tests/PlayMode/ModelStagePixelEvidencePlayModeTests.cs`（**简报写的 `Assets/Game/Tests/View/` 下并不存在该差分用例，实测在 `Assets/ZFrame/Tests/PlayMode/`，如实记录此路径差异**）。两条 `[UnityTest]` 的差分假设都是「两帧之间只有 4 个 CHR 模型开关（或一次拾取火花）」：

- `ScnGame_ChristmasModels_ChangeTheCameraImage`：`ModelStagePixelEvidencePlayModeTests.cs:139` `Assert.Greater(diff.MovedFraction, 0.010)`、`:143` `Assert.Greater(diff.DarkerFraction, 0.005)`；控制帧/证据帧只切 `characterRoots` 的 `SetActive`（`:113-126`）。探针相机 `rig.Build(new Vector3(-3f,1.6f,-5.5f), new Vector3(1.5f,0.9f,2.5f))`（`:108`），`PixelProbeRig.cs:30-56` 给的是 320×180 / FOV 60 / **farClip=20** / 背景 0.02。
- `ScnGame_PickupInteract_SparksBesideTheRealModel`：`:200` `Assert.Greater(diff.BrighterFraction, 0.004)`、`:203` `Assert.Greater(diff.MeanAbs, 0.002)`，另有 `:196-199` 的精确计数断言。

群体圆盘 `KernelCrowdIds.cs:28` `FieldRadius=9`、实例 y≈0.55–1.15（`KernelCrowdDriver.cs:231-242`），落在上述相机 -5.5 → +2.5 的视锥内且距相机 <20m，**会被 `Graphics.DrawMeshInstanced` 画进探针相机**；且群体每帧在 Logic 档积分移动（`KernelCrowdDriver.cs:131-188`）、View 档重新提交，control / withModels 两帧之间位置不同 → 给差分注入额外 moved / darker / brighter 像素。

其余 4 个像素证据套件（`Particle` / `GameplayFx` / `VolumeCrossfade` / `PostFx`）的探针相机都架在 `PixelProbeRig.FarPark=(5000,5000,5000)` 附近（farClip=20），群体在原点半径 9 内，被远裁剪天然排除，**不受影响**；`PostFx` 里 `Camera.main` 的 real-content 段只有 timing 日志、无像素门限断言。所以真正需要协调的只有 A-R9-10 这一条相机差分。

#### 1.3 关键事实：A-R9-13 的顾虑在其自身门控下从未真正发生

`KernelCrowdHost`（A-R9-13 版）只订阅自定义 `EventBus<SceneLoadedEvent>`，而该事件**仅**由 `SceneLoader.FinishLoad()` 触发（`ZFrame/View/Scene/SceneLoader.cs:270`）。A-R9-10 的 `LoadGameScene()` 用的是 `SceneManager.LoadSceneAsync(GameBootstrap.GameSceneName)` 直连（`:217`，注释明写 "no boot chain"），**不经过 SceneLoader、不 fire `SceneLoadedEvent`**。因此按 A-R9-13 现有门控，A-R9-10 加载 SCN_Game 时群体压根不会自动启用 —— 这解释了为什么 A-R9-13 始终拿不出污染量级：它守的是一条本用例根本不经过的路径，顾虑从未被实证触发过。

#### 1.4 差分量级（本任务实测，见 §四）

用**与 A-R9-10 完全相同的相机几何** `(-3,1.6,-5.5) -> (1.5,0.9,2.5)` 实测「群体开 / 关」（`TempLogs/runtests_playmode.log:2766`，本次五段实跑）：

| 量 | 实测 | A-R9-10 门限 | 倍数 |
|---|---|---|---|
| `moved` (变化像素占比) | **0.1253**（12.53%） | `> 0.010` | 12.5× |
| `darker` | **0.0490**（4.90%） | `> 0.005` | 9.8× |
| `brighter` | 0.0764 | — | — |
| `meanAbs` | 0.04706 | — | — |

**污染的实质说清**：这两条断言都是 `Assert.Greater`（下限）。群体若在 control / withModels 两帧都出现且每帧积分移动，差分会被群体自身运动注入一个比门限高一个数量级的量，**不是把断言变红，而是把「模型可见性」的判别力架空成假绿**（控制帧本应只有静态背景，却凭空多出大片 moved 像素）。若群体静止，则两帧共同背景抵消、反而无害 —— 所以旧报告的「有可能」既没说清机制、也没说清量级。本任务给出可复现的 12.53% / 4.90% 作为量级结论。

#### 1.5 现在能安全打开的依据

1. 门控改为按「激活场景名」判定（§三.1），覆盖全部加载路径，真正「进 SCN_Game 即开箱可见」，不依赖某条特定加载链。
2. 采用简报明确授权的协调方式「**让差分用例显式关闭群体**」：A-R9-10 的 `SetUp` 置 `AutoEnable=false` + `DisposeCrowd()`，`TearDown` 复原 `true`。**既有断言一字未动，不删 / 不放宽 / 不跳过、不加重试、不改容差**；协调只在 setup 层隔离视觉干扰源，且可证伪（§1.4 的量级即隔离失效时会混入的东西）。
3. 每次 `sceneLoaded` 遇到非 SCN_Game 场景即 `DisposeCrowd()`，杜绝 `Graphics.DrawMeshInstanced` 跨场景 / 跨 PlayMode 用例的状态泄漏（这是比旧门控更严格的清理，旧版只在收到别的场景的 `SceneLoadedEvent` 时才清理）。
4. 实测闭环：PlayMode 48/48/0，其中 A-R9-10 两条差分用例 `moved=0.1288 / darker=0.0986`（`runtests_playmode.log:2861`）、火花 `brighter=0.0156 / meanAbs=0.01322`（`:2893`），与 A-R9-10 历史值（moved 0.1316 / darker 0.1031）同量级，门限未动 → 用例语义未变。

### 二、交付物清单（全部落在白名单内）

| 文件 | 性质 | 说明 |
|---|---|---|
| `UnityProject/Assets/Game/View/Effects/KernelCrowdHost.cs` | 修改 | `AutoEnable` 默认 `true`；门控加订阅 Unity 原生 `SceneManager.sceneLoaded` + `ApplySceneGate(场景名)`；`OnDestroy` 退订；`EnsureCrowd`/`DisposeCrowd` 与单次提交机制**一字未动** |
| `UnityProject/Assets/ZFrame/Tests/PlayMode/ModelStagePixelEvidencePlayModeTests.cs` | 修改 | **仅** `SetUp`/`TearDown` 各加隔离行（`AutoEnable=false` + `DisposeCrowd()` / 复原 `true`）；两个 `[UnityTest]` 的断言、门限、容差全部零改动 |
| `UnityProject/Assets/ZFrame/Tests/PlayMode/KernelCrowdPixelEvidencePlayModeTests.cs` | 新增 | 2 个 PlayMode 用例：默认可见真断言 + 真实像素证据（自建离屏相机 `RenderTexture`+`ReadPixels`、两图落盘、差统计、A-R9-10 几何污染量化、GPU DrawCall 尽力采样） |
| `Docs/Parity/crowd_r9_15_off.png` / `crowd_r9_15_on.png` | 新增（图片证据） | 群体关 / 开两图，320×180 RGB，由测试运行内真实读回像素编码落盘 |
| `Docs/Demo.md` | 修改 | §2 表 #11、§3.6 怎么开 / 证据、§5 限制 #15：体验项由「需菜单手动开」改为「开箱可见」 |
| `Docs/Logs/TASK-A-R9-15.md` | 修改 | 仅本「施工报告」节 |

Unity 自动生成的 `KernelCrowdPixelEvidencePlayModeTests.cs.meta`（未跟踪）落在白名单目录内，**未手改**。未新增 / 修改任何 asmdef（新用例用到的 `Game.Bootstrap` / `Game.View` / `ZFrame.Core` / `ZFrame.View` / `Unity.Profiling` 均已在 `ZFrame.PlayMode.Tests.asmdef` 覆盖范围内）。

### 三、默认可见方案与像素用例协调方式

#### 3.1 门控（默认可见）

`KernelCrowdHost.AutoEnable { get; set; } = true`（默认开箱可见）。`Awake` 同时订阅 `EventBus<SceneLoadedEvent>`（生产 boot chain：`SceneLoader.FinishLoad` fire）与 `SceneManager.sceneLoaded`（Unity 原生，覆盖 `SceneLoader`、测试里 `SceneManager.LoadSceneAsync` 直连等所有加载方式），两者都汇入 `ApplySceneGate(string sceneName)`：

- `sceneName == "SCN_Game"` 且 `AutoEnable` 且 `Services.TryGet(out ITickQueue)` 就绪 → `EnsureCrowd()`；
- 其它场景（含回主菜单）→ `DisposeCrowd()` 释放原生 World 句柄。

`AutoBoot`（`RuntimeInitializeOnLoadMethod(AfterSceneLoad)`）也按当前激活场景门控一次；`OnDestroy` 退订原生事件。这样真实玩家进 SCN_Game（boot chain）与 PlayMode 直连加载 SCN_Game 都会自动启用，PlayMode 用例可对「进场景」这一步做真断言，而非「菜单可开」。

#### 3.2 与 A-R9-10 像素差分用例的协调（可证伪）

`ModelStagePixelEvidencePlayModeTests.SetUp()` 开头两行：`KernelCrowdHost.AutoEnable = false; KernelCrowdHost.EnsureInstance().DisposeCrowd();`；`TearDown()` 末尾 `KernelCrowdHost.AutoEnable = true;`。由于 `LoadGameScene()` 直连加载 SCN_Game 会触发 `sceneLoaded`，门控在 `AutoEnable=false` 时走「不启用」分支，群体不进入探针相机采样；SetUp 的 `DisposeCrowd()` 还顺带清掉任何上一用例的残留。

**可证伪**：若把这两行隔离去掉，`ApplySceneGate` 会在 SCN_Game 自动启用群体，§1.4 实测的 12.53% moved / 4.90% darker 就会混进控制帧与证据帧，差分被群体运动主导 —— 协调方式不是靠放宽断言蒙过去，而是「把干扰源移出采样区」，且干扰量级已被独立量化。

#### 3.3 保持 A-R9-13 不变量

`KernelCrowdDriver`（`EachBatches` 游标 + Logic 档 + `SetComponent` 回写）、`KernelCrowdRenderer`（单次 `Graphics.DrawMeshInstanced`、URP 运行期材质、View 档、`Dispose()` 释放材质/Mesh/World）**源码零改动**；英雄侧玩法 / FSM / 场景布局 / 既有模块（HUD、Audio、存档、GameBootstrap、GameSceneSetup）零改动；场景文件未改。PlayMode 实测 `submitCalls=1 / submittedInstances=1000 / shader=Universal Render Pipeline/Lit`（`runtests_playmode.log:2810`）。

### 四、真实像素证据（本任务核心，已达成）

#### 4.1 测量方式

`KernelCrowdPixelEvidencePlayModeTests.cs` 自建离屏探针相机（非 `PixelProbeRig`，以便自定义 farClip 与落盘 RGB）：`RenderTexture(320×180, ARGB32)` → `Camera.targetTexture` → 引擎渲染（URP）→ `Texture2D.ReadPixels` + `GetPixels` 读回**真实渲染像素**。`CrowdProbe.Frame` 同时保存 `Color[]`（用于 `EncodeToPNG` 落盘）与由其算出的 luma（用于差统计），两者同源。同一运行内、同一相机、`EnsureCrowd()`（开）/ `DisposeCrowd()`（关）A/B 对照，各 `yield return null` ×2 等帧让 `GameLoopDriver.Update` 的 `TickAll(View)` 提交生效；SCN_Game 静态内容（地面 / 模型 / 光照）在两帧都存在、差分抵消，敌人巡逻 `SetActive(false)` park、环境尘埃 `SetAmbientDust(false)`（与 A-R9-10 同口径）。

luma = `0.2126R + 0.7152G + 0.0722B`；`MoveEpsilon = 0.02` 与 `PixelProbeRig.FrameDiff.MoveEpsilon` / A-R9-10 口径一致；判定阈值 `MinChangedFraction = 0.010`（变化像素占比下限，真断言 `Assert.Greater`，非恒真）。

#### 4.2 两图路径（可复核）

| 图 | 路径 | 大小 | md5 |
|---|---|---|---|
| 群体关 | `Docs/Parity/crowd_r9_15_off.png` | 11424 B | `3ffd658ee02a4b068ccce41da3c8647d` |
| 群体开 | `Docs/Parity/crowd_r9_15_on.png` | 20504 B | `84155e932e02f97b3b74dbcb160f4b2d` |

两图均 320×180 RGB、PNG 文件头 `89504e470d0a1a0a` 有效、md5 不同（非复制）。落盘日志 `runtests_playmode.log:2707 / 2727`。图片查看工具视觉确认：off 图为 SCN_Game 灰色地面 + 角色 + 树 / 栅栏，**无群体**；on 图同机位多出一整片密集的白色小方块阵列（1000 实例），差异肉眼可辨。off 图 md5 两次运行一致（纯静态场景确定性），on 图因群体每帧积分移动而每次不同 —— 恰好反证它是实时渲染读回、不是预置贴图。

#### 4.3 像素差统计（本次五段实跑，`runtests_playmode.log:2747`）

主视角 `eye=(0,11,-13) -> look=(0,0.6,2)`，farClip=60，FOV=60，总像素 320×180 = **57600**：

| 指标 | 实测 | 阈值 / 判定 |
|---|---|---|
| 变化像素数 `changedPixels` | **2287 / 57600** | — |
| 变化占比 `changedFraction` | **0.0397**（3.97%） | `> 0.010` → **通过**（约 3.97× 余量） |
| `moved` | 0.0397 | — |
| `brighter` / `darker` | 0.0247 / 0.0150 | — |
| `meanAbs` | 0.01340 | — |

这是**真实 URP 渲染读回像素**的差统计，不是「`Graphics.DrawMeshInstanced` 被调用 1 次」的提交层计数；`Assert.Greater(diff.ChangedFraction, 0.010)` 是可证伪的真断言（若群体在 URP 下没画出来，该值趋近 0、断言变红）。

补充（前置调研量化，`:2766`）：A-R9-10 相机几何 `(-3,1.6,-5.5) -> (1.5,0.9,2.5)` 下 `moved=0.1253 / darker=0.0490 / brighter=0.0764 / meanAbs=0.04706 / changedFraction=0.1253`，即「若群体不隔离会叠加进 A-R9-10 差分」的量级。

#### 4.4 独立二次复核的尝试与受阻（如实）

曾尝试用 PowerShell `System.Drawing.Bitmap` 独立读回两张 PNG 重算差统计以做二次复核，被本机安全策略拦截（`Add-Type` 运行时编译 / 加载 .NET 代码被拒）。按环境纪律未换等价命令重试。复核依据因此为：① 用例内同源读回像素的差统计；② 两图 md5 / 尺寸 / PNG 文件头 / 视觉内容差异；③ 验收代理可自行用任意图像工具对两张 PNG 重新比对。此受限不影响「两图 + 差统计」证据本身成立。

### 五、真实 GPU DrawCall —— **受限 / 未达成**（如实，不编数字）

在 batchmode PlayMode 里用 `Unity.Profiling.ProfilerRecorder(ProfilerCategory.Render, "Draw Calls Count")` 尽力采样，`runtests_playmode.log:2785` 读到 `LastValue = 86`（群体开态）。**但不把它当作可信的「群体 GPU DrawCall」**，理由：

- 86 是**整场景一帧的总 DrawCall**（含 SCN_Game 全部模型 / 地面 / 道具 / URP passes），不是群体贡献；
- 未做开 / 关 A/B 对照，无法从总值中分离出群体那 1 次实例化提交的增量；
- `-batchmode` 无真实呈现负载，Frame Debugger 不可用，该 stat 与真机 GUI Play Mode 不等价。

按简报「批处理下取不到就如实标受限」的口径，**真实 GPU DrawCall 判受限 / 未达成**。可证的仍是提交层静态事实：`LastSubmitCalls = 1`、`submittedInstances = 1000`（`runtests_playmode.log:2810`），以及 §四的真实像素证据；**没有**把「1 次提交调用」冒充成 GPU / 像素证据。

### 六、实跑测试数字与 xml 路径（本次新写证据）

命令（PowerShell 工具，同进程清 8 个大小写代理变量；跑前 `tasklist` 无 Unity.exe、无 `UnityProject/Temp/UnityLockfile`；测试段**不带** `-quit`；`machine quiet check: VERDICT: QUIET`）：

```
& "C:\Users\1\Desktop\zcode\GameFramework\Tools\run_tests.ps1" -Segments editmode,playmode,arch,config,naming
```

| 段 | 本次数字 | 基线 | 结论 |
|---|---|---|---|
| EditMode | **880 / 880 / 0** | 880 | 持平（≥880） |
| PlayMode | **48 / 48 / 0** | 46 | **+2**（两个新增用例全 `Passed`） |
| native（直接跑已构建 `Native/build/bin/Release/zrender_test.exe`，未重建） | **311 / 311 passed / 0 failed，776,716 断言，`Status: SUCCESS!`** | 311 / 776,716 | 持平 |
| arch | **exit 0** | 0 | `TempLogs/arch_report.txt`：asmdef 扫描 25（期望矩阵 12，Tests 豁免）、违规数 0、PASS |
| config | **exit 0** | 0 | `TempLogs/config_report.txt`：违规数 0、PASS |
| naming | **exit 0** | 0 | `TempLogs/name_report.txt`：违规数 0、PASS |

`TestReport.txt`：`RESULT: ALL PASS (5 passed, 0 skipped, 0 failed)`。

**本次新写 xml 与时间戳**（脚本 `EvidenceFresh=true`，输出 `evidence: results.xml written by this run`）：

- `TempLogs/runtests_editmode.xml`：mtime `2026-11-04 22:39:01 +0800`，`<test-run total="880" passed="880" failed="0" result="Passed">`，`start-time 14:38:34Z / end-time 14:39:01Z`。
- `TempLogs/runtests_playmode.xml`：mtime `2026-11-04 22:40:29 +0800`，`<test-run total="48" passed="48" failed="0" result="Passed">`，`start-time 14:39:48Z / end-time 14:40:28Z`。

**新增 2 个 PlayMode 用例**（`KernelCrowdPixelEvidencePlayModeTests`）：`DefaultEnterScene_CrowdIsAutoVisible`（断言 `AutoEnable` 默认 true、进 SCN_Game 后 `CrowdActive==true`、`EntityCount==1000`、URP 材质解析成功、`LastSubmitCalls==1`）；`CrowdOnVsOff_ChangesRealPixels_AndWritesTwoPngs`（两图落盘 + `changedFraction > 0.010` 真断言 + A-R9-10 几何污染量化 + GPU DrawCall 尽力采样）。

**一处需说明的环境噪音（非证据问题）**：脚本每段开头有 `[warn] cannot delete previous results.xml / stale report ... [safe-delete][SAFE_DELETE_FAIL_CLOSED]` —— 本机 safe-delete hook 让 `Remove-Item` 抛异常，但脚本内 `[System.IO.File]::Delete` 兜底删除成功，故 `EvidenceFresh` 判定为本次新写（输出 `results.xml written by this run`）、三份 validator 报告也无 `[STALE]` 标记。证据以本次新写 xml / 报告为准。

**未跑** `run_tests.ps1` 的 `native` 段：本任务不改 `Native/**`、不重建以免改动部署位 `zrender.dll`；native 311/776,716 由直接运行已构建二进制亲证。

### 七、已知受限 / 未达成（如实披露，不把部分完成说成已完成）

1. **真实 GPU DrawCall：受限 / 未达成** —— 见 §五。batchmode 下 `ProfilerRecorder` 只读到整场景总 DrawCall 86，无开 / 关 A/B、无法分离群体贡献、Frame Debugger 不可用；未编造「群体 GPU DrawCall = N」。
2. **独立二次 PNG 复核受阻** —— 见 §4.4，`Add-Type` 被本机安全策略拦截，按纪律未绕过；不影响两图 + 差统计证据成立。
3. **DevServer shot（gameview 源）路径未采用**：本机以 batchmode PlayMode 内 `RenderTexture + ReadPixels` 在**同一次运行内**做开 / 关 A/B，取到更可信的真实像素；DevServer 需 GUI Editor Play Mode 常驻服务，未纳入本证据链。
4. **IL2CPP / Android 上 P/Invoke 与 `unmanaged` 泛型未验证**（沿用 A-R9-13，本机 Editor 为 Mono）。
5. **URP 14 单次实例化上限 1023 未逐一探测**（沿用 A-R9-13），SRP Batcher 兼容性 / 移动端降级策略未测。
6. **未做「修前必红」定向变异、未勾选验收清单、未写「验收结论」**：按铁律留给与施工不同的验收代理。

### 八、现场状态

- `git status --short`：3 个 `M`（`Docs/Demo.md`、`UnityProject/Assets/Game/View/Effects/KernelCrowdHost.cs`、`UnityProject/Assets/ZFrame/Tests/PlayMode/ModelStagePixelEvidencePlayModeTests.cs`）+ 4 个 `??`（`Docs/Parity/crowd_r9_15_off.png`、`crowd_r9_15_on.png`、`KernelCrowdPixelEvidencePlayModeTests.cs` 及其 Unity 自动生成的 `.meta`），**全部在简报白名单内**。
- **未触碰**：`Native/**`、`ZRender/**`、`ZFrame/View/Hud/**`、`Assets/Audio/**`、`ZFrame/View/Audio/**`、`TASKS.md` / `HANDOFF.md` / `WORKLOG.md` / `README.md` / `DECISIONS.md`；`Assets/Plugins/ZRender/zrender.dll` 未 `git checkout --` 丢弃、git status 无其条目。
- Editor **未**改写 `URP_*.asset` / `QualitySettings.asset` / `ProjectSettings`（git status 零此类条目，无需定向还原）。
- HEAD 仍 `5742883`，**无** `git add`、**无** `git commit`；无残留 Unity.exe、无 `UnityProject/Temp/UnityLockfile`；本任务未做定向变异，零变异残留。
- 文本交付物字节级自查：`KernelCrowdHost.cs` / `ModelStagePixelEvidencePlayModeTests.cs` / `KernelCrowdPixelEvidencePlayModeTests.cs` / `Docs/Demo.md` / 本日志均 UTF-8 无 BOM（首 3 字节非 `EF BB BF`）、CR=0（纯 LF）、末尾保留单个换行；两张 PNG 为二进制图片，不适用文本编码约束，文件头有效。
- 只写本「施工报告」节；**未写「验收结论」、未勾选验收清单**。

## 验收清单（主控写）

- [ ] V1 默认可见：PlayMode 真断言（行号 + 实跑数字），非「菜单可开」
- [ ] V2 像素证据：两图路径 + 差异统计 + 测量方式 + 判定阈值，可复核
- [ ] V3 A-R9-10 相机差分用例不回归，且既有断言强度未降（diff 证明）
- [ ] V4 单次批量绘制仍成立；EditMode ≥880 / PlayMode ≥46 / native 311；三校验器 exit 0
- [ ] V5 报告四节齐全 + 验收代理亲自复做定向变异（变红 → 还原 → 复绿 → 零残留）

## 验收结论（验收代理写）

**验收代理**: verifier-r9-15（独立验收代理，与施工 builder-r9-15 不同实例） ｜ **日期**: 2026-11-04 ｜ **进场 HEAD**: `5742883`（全程未变） ｜ **总体结论: PASS with 受限**

> 口径声明：以下全部数字均为**本次会话自跑 / 自查**所得；施工报告数字仅作旁证并逐项标注，未复用旧 xml。实跑命令（PowerShell 工具，同进程清 8 个大小写代理变量后剩 0；测试段不带 `-quit`；不加 `-force-d3d12`）：`& .\Tools\run_tests.ps1 -Segments editmode,playmode,arch,config,naming`。施工 xml 先 `mv` 归档为 `TempLogs/verifier_r9_15_builder_*`，本次新写证据归档为 `TempLogs/verifier_r9_15_*`；证据以**本次新写** results.xml 为准（脚本均输出 `results.xml written by this run`）。

### V1 默认可见 —— **PASS**

- 自跑 PlayMode（修复态全量轮）：`total=48 passed=48 failed=0`（`TempLogs/verifier_r9_15_playmode.xml`，mtime `2026-11-04 23:00:23`，`<test-run total="48" passed="48" failed="0" result="Passed">`，start `14:59:41Z`/end `15:00:22Z`，Unity exit 0）。
- 「进 SCN_Game 自动可见」是**真断言**，非「菜单可开」、非「显式 EnsureCrowd 才算」（`KernelCrowdPixelEvidencePlayModeTests.cs`）：
  - `:67-68` `Assert.IsTrue(KernelCrowdHost.AutoEnable, "默认须为 true")`；`:71` `host.DisposeCrowd()` 从干净态起；`:74` `SceneManager.LoadSceneAsync(GameBootstrap.GameSceneName)` **直连加载**，全用例不调用 `EnsureCrowd()`，靠 `sceneLoaded` 门控自动拉起；
  - `:81` `Assert.IsTrue(host.CrowdActive)`（`active=True`）、`:84` `Assert.AreEqual(1000, host.Driver.EntityCount)`（`entities=1000`）、`:85-86` URP 材质解析成功、`:87-88` `Assert.AreEqual(1, host.Renderer.LastSubmitCalls)`（`submitCalls=1`，不得退化为逐实体）。
  - 本次 `verifier_r9_15_playmode.log:2829`：`default visible: active=True entities=1000 submitCalls=1 submittedInstances=1000 shader=Universal Render Pipeline/Lit`。V5 变异把 `AutoEnable` 改回 false 后该用例立刻变红（见 V5），证明断言有牙、非恒真。
- 门控与生命周期（`KernelCrowdHost.cs`）：`:96` 订阅 Unity 原生 `SceneManager.sceneLoaded`、`:119-122` `OnUnitySceneLoaded → ApplySceneGate(scene.name)`、`:129-141` `ApplySceneGate`（非 SCN_Game → `DisposeCrowd()`；SCN_Game + `AutoEnable` + `Services.TryGet(ITickQueue)` 就绪 → `EnsureCrowd()`）、`:67-72` `AutoBoot(AfterSceneLoad)` 按当前激活场景门控一次、`:99-109` `OnDestroy` 退订原生事件 + `DisposeCrowd()`、`:159-165` `DisposeCrowd()` 释放 renderer/driver（跨场景/跨用例无泄漏）。`git diff` 证实 `EnsureCrowd()`/`DisposeCrowd()` 与单次提交机制**一字未动**。

### V2 像素证据（重点） —— **PASS**

- **ViewImage 亲眼看两图**（本代理亲眼复核，非只信施工自述）：
  - `Docs/Parity/crowd_r9_15_off.png`：黑色背景、灰色梯形地面平台，中央几个深色角色模型/物体，左侧绿色球状树冠，右侧深色栅栏结构与绿色描边；地面干净，**无任何群体方块**。
  - `Docs/Parity/crowd_r9_15_on.png`：同机位、同灰色地面，但地面上**密密麻麻铺满成片白色小方块阵列**（约 1000 实例），从近端到远端高密度分布。两图差异肉眼极其明显 → on 图确多出成片群体实例。
- **独立重算像素差**（Python 3.13 标准库 `struct`+`zlib` 自解码 PNG，非 .NET `Add-Type`；命令为 `python - <<'PY' …` 解析 IHDR/IDAT/反 filter 后按 `0.2126R+0.7152G+0.0722B`、`MoveEpsilon=0.02` 重算）：
  - 【施工落盘图，已备份 `TempLogs/verifier_r9_15_builder_crowd_{off,on}.png`】：`changedPixels=2287/57600`、`changedFraction=0.0397`、`moved=0.0397`、`brighter=0.0247`、`darker=0.0150`、`meanAbs=0.01340` —— 与施工 §4.3 数字**一字不差**。
  - 【本次自跑新写图】：`changedPixels=2245/57600`、`changedFraction=0.0390`、`moved=0.0390`、`brighter=0.0244`、`darker=0.0145`、`meanAbs=0.01336` —— 与本次 `verifier_r9_15_playmode.log:2766` **一字不差**；与施工版数值微差源于群体每帧积分移动（非确定性），两值均 > 判定阈值 `0.010`（约 3.9× 余量）。
  - md5：off `3ffd658ee02a4b068ccce41da3c8647d`（跨运行恒定，纯静态场景确定性）、on 施工版 `84155e932e02f97b3b74dbcb160f4b2d` / 本次自跑版 `1b42aab9118906dbb93a2ee03d478a65`（每次不同）→ **两图 md5 不同、非复制**，且反证实时渲染读回、非预置贴图。尺寸 `320×180=57600`、8bit/RGB（IHDR colortype=2）、PNG 头 `89504e470d0a1a0a` 有效、11424/20504 B（本次自跑 on 20357 B）。
- **测量方式核对**：`KernelCrowdPixelEvidencePlayModeTests.cs` `:231-253` 自建离屏相机 → `RenderTexture(320×180, ARGB32)` → `Camera.targetTexture`；`:255-272` `Capture()` 用 `RenderTexture.active` + `Texture2D.ReadPixels` + `GetPixels` 读回**真实 URP 渲染像素**；用例 `:101-169` 在**同一次运行内、同一相机**做 `DisposeCrowd`(关)→off / `EnsureCrowd`(开)→on 的 A/B（各 `yield null`×2 等帧，静态场景内容抵消，敌人 park、尘埃关）；`:142` `Assert.Greater(diff.ChangedFraction, 0.010)` 是可证伪的**像素差真断言**，**不是**把「1 次 `Graphics.DrawMeshInstanced` 提交调用」冒充像素证据；`:171-208` GPU DrawCall 仅尽力采样、明确不当证据。
- 施工自曝 §4.4「独立 PNG 二次复核被 `Add-Type` 安全策略拦截」：本代理**已用不同机制（Python 标准库）独立复核成功补上**（上述两次重算），该受限对 V2 不再构成缺口；V2 由「亲眼复核 + 两次独立重算 + 测量方式核对」全部成立，判 PASS。

### V3 A-R9-10 不回归且断言强度未降 —— **PASS**

- 自跑相关用例（含于 PlayMode 48/48/0）：本次 `verifier_r9_15_playmode.log:2880` `[R910] models vs control: darker=0.1019 moved=0.1332`；`:2912` `[R910] pickup spark: brighter=0.0156 meanAbs=0.01319 events=1 sparks=1 emitted=12`。
- `git diff -- ModelStagePixelEvidencePlayModeTests.cs`：**仅** SetUp 加 `KernelCrowdHost.AutoEnable=false; KernelCrowdHost.EnsureInstance().DisposeCrowd();`（`:57-58`）、TearDown 加 `KernelCrowdHost.AutoEnable=true;`（`:74`）；两个 `[UnityTest]` 断言体、门限常量（`:42-45` `MinModelMoved=0.010 / MinModelDarker=0.005 / MinSparkBrighter=0.004 / MinSparkMeanAbs=0.002`）、容差**一字未动**（diff 中零断言行，无删/放宽/跳过/重试/改容差）。
- 历史值核对：`TASK-A-R9-10.md:80/:113` 历史 `moved=0.1316 / darker=0.1031(0.1032)`；本次隔离后 `0.1332 / 0.1019` 同量级 → 用例语义未变。
- 施工「群体开时 moved=0.1253 / darker=0.0490 会把差分架空、隔离后 0.1288/0.0986 与历史同量级」这组对照**可复核**：本次自跑 `verifier_r9_15_playmode.log:2785` 用 A-R9-10 相机几何 `(-3,1.6,-5.5)->(1.5,0.9,2.5)` 实测 `moved=0.1253 darker=0.0488 brighter=0.0765 meanAbs=0.04717 changedFraction=0.1253`，与施工 `0.1253/0.0490/0.0764/0.04706` 同量级（群体移动致微差），均远超 A-R9-10 门限 0.010/0.005（12.5× / 9.8×），确证「不隔离会把 `Assert.Greater` 下限断言架空成假绿」的机制与量级成立；隔离后回到历史同量级。

### V4 不回归 —— **PASS**

- 自跑五段（PowerShell 工具，清 8 变量剩 0，`machine quiet check: VERDICT: QUIET`，测试段不带 `-quit`，脚本 exit 0）：
  - EditMode **880/880/0**（`TempLogs/verifier_r9_15_editmode.xml`，mtime `22:57:47`，`total="880" passed="880" failed="0" result="Passed"`，start `14:57:36Z`/end `14:57:46Z`）≥ 880 ✓
  - PlayMode **48/48/0**（`verifier_r9_15_playmode.xml`，mtime `23:00:23`）≥ 46（施工报 48 吻合）✓
  - arch / config / naming 均 **exit 0**（`verifier_r9_15_{arch,config,name}_report.txt` 均「违规数: 0 / 结果: PASS」，arch 报 asmdef 扫描 25 / 期望矩阵 12 / Tests 豁免）；`TestReport.txt`：`ALL PASS (5 passed, 0 skipped, 0 failed)`，`LASTEXITCODE=0`
  - native **311/311 passed、776,716 断言、`Status: SUCCESS!`**：直接运行已构建 `Native/build/bin/Release/zrender_test.exe` 亲证（**未重建**、未改 `Native/**`），与 A-R9-13 基线持平。如实记录：首次误从 build 目录运行致 6 个 `zmesh gallery` 用例因相对路径资源读不到而失败（311/305），改从仓库根（= `Native/CMakeLists.txt` 的 `WORKING_DIRECTORY`）重跑即 311/311 复绿，纯 cwd 问题、非回归。
- 单次批量绘制未退化：`verifier_r9_15_playmode.log:2829` `submitCalls=1 submittedInstances=1000 shader=Universal Render Pipeline/Lit`；`KernelCrowdRenderer.cs`/`KernelCrowdDriver.cs`/`KernelCrowdIds.cs` 等 A-R9-13 机制文件在 `git status` 零命中（未改），`EachBatches` 游标与 `Dispose()` 生命周期未破坏。
- 英雄侧 / 场景布局 / 既有模块零改动：`git diff --name-only` 仅 4 个白名单文件，`git status --short` 8 项全在白名单（`Assets/Game/**`、`Assets/ZFrame/Tests/**`、`Docs/Demo.md`、`Docs/Parity/**`、本日志）；禁止路径（`Native/**`、`ZRender/**`、`Hud/**`、`Audio/**`、5 个禁止文档、`Scenes/`、asmdef）经脚本核对 **0 命中**。

### V5 报告与变异 —— **PASS**

- 四节齐全：任务简报（主控）/ 施工报告（施工，八节）/ 验收清单（主控）/ 本验收结论；施工未勾选验收清单、未写本结论（合规）。
- **亲自复做定向变异**（例外授权，仅改本任务 `KernelCrowdHost.cs` **1 行产品代码**，未改任何断言 / 用例体）：把 `AutoEnable` 改回 false（用户列的第一变异点），即 `:52` `= true;` → `= false;`。
  - 备份 `TempLogs/verifier_r9_15_KernelCrowdHost.cs.bak`（sha256 `38b664c02f722550dfa1739bc2a01f5890507c20f2ef3401a416629826c46eb2`、md5 `c14d8b3f2ceb5cc0e0b42b193d4e2a4d`、6702 B）；变异前 `cmp` 零差异，变异后 `diff` 恰好 `52c52` 一行；两个测试文件 sha256 变异前后未变（`fb2ba0e7…` / `e51d2239…`）。
  - 三次均本次自跑 playmode 段、各自新写 xml（EvidenceFresh=true）：
    - **修复态**：`48/48/0`（`verifier_r9_15_playmode.xml`，全量轮）
    - **变异态**：**`48/47/1`**（`verifier_r9_15_mutant_playmode.xml`，`result="Failed(Child)"`，Unity exit=2，`RESULT: FAILED (1 failed: playmode)`、脚本 exit 1；该轮 machine quiet 报 BUSY/rustc，仅影响耗时/帧率证据、不改用例判定）
    - **还原态**：`48/48/0`（`verifier_r9_15_restored_playmode.xml`，mtime `23:17:14`，`total="48" passed="48" failed="0" result="Passed"`，start `15:16:14Z`/end `15:17:14Z`，Unity exit 0、`RESULT: ALL PASS`、脚本 exit 0）
  - **变红用例名**：`ZFrame.PlayMode.Tests.KernelCrowdPixelEvidencePlayModeTests.DefaultEnterScene_CrowdIsAutoVisible`；变异态 xml 失败断言 `A-R9-15：AutoEnable 默认须为 true（进 SCN_Game 即开箱可见） / Expected: True / But was: False` —— 精确命中 V1 `:67`，证明用例非恒真。
  - **还原证据**：`cp` 备份覆盖回源文件 → `cmp` 零差异、sha256 双份一致、`:52` 恢复 `= true`、代码层 `=false` 0 命中、`git diff --name-only` 仍 4 个 M、`git status` 与进场 8 项一致 → **零残留**；变异期间未 `git add`、未 `git commit`。
- 「断言一字未动、产品代码只在白名单内」核对：见 V3 / V4；新增测试文件为 `??` 非修改，产品代码改动仅 `KernelCrowdHost.cs`（`Assets/Game/View/Effects/**`，白名单）。

### 受限清单（如实披露，不把部分完成说成已完成）

1. **真实 GPU DrawCall：受限 / 未达成** —— 施工 §五(`:159-167`) / §七.1(`:201`) 如实登记：batchmode 下 `ProfilerRecorder("Draw Calls Count")` 只读到整场景一帧总 DrawCall `LastValue=86`（无开/关 A/B、无法分离群体那 1 次实例化提交的增量、Frame Debugger 不可用），判受限，**未编造「群体 GPU DrawCall = N」**（`:167` 明确「没有把 1 次提交调用冒充成 GPU / 像素证据」）。本代理本次自跑 `verifier_r9_15_playmode.log:2804` 同样读到 86（群体开态、整场景总值），**我也取不到可信 A/B**，同标受限。可证的仍是提交层静态事实 `LastSubmitCalls=1 / submittedInstances=1000` 与 V2 真实像素证据。
2. 施工 §4.4「独立 PNG 二次复核被 Add-Type 拦截」—— **已由本代理用 Python 标准库独立复核解除**（V2），不再是 V2 缺口。
3. DevServer shot（gameview 源）路径未采用（施工 §七.3）：本次以 batchmode PlayMode 内 `RenderTexture + ReadPixels` 同一次运行 A/B 取真实像素，证据更可信。
4. IL2CPP / Android 上 P/Invoke 与 `unmanaged` 泛型未验证（沿用 A-R9-13，本机 Editor 为 Mono）。
5. URP 14 单次实例化上限 1023 未逐一探测、SRP Batcher 兼容性 / 移动端降级策略未测（沿用 A-R9-13）。
6. native 311 由直接运行**已构建**二进制亲证（未重建 `Native/**`、未跑 `run_tests.ps1` 的 native 段），数字与 A-R9-13 基线持平。

### 现场状态

- HEAD `5742883d8cf746651729e2c3d021fb557d8f48aa`（= 进场，全程未变），`git log -1` 仍 `5742883 G-122`，**未 git add / 未 git commit**。
- `git status --short`：4 `M`（`Docs/Demo.md`、`Docs/Logs/TASK-A-R9-15.md`、`KernelCrowdHost.cs`、`ModelStagePixelEvidencePlayModeTests.cs`）+ 4 `??`（`crowd_r9_15_off.png`、`crowd_r9_15_on.png`、`KernelCrowdPixelEvidencePlayModeTests.cs` 及 Unity 自动生成 `.meta`），**全部在简报白名单内**；禁止路径脚本核对 **0 命中**。
- Editor **未**改写 `URP_*.asset` / `QualitySettings.asset` / `ProjectSettings`（status 零此类条目，无需定向还原）；`Assets/Plugins/ZRender/zrender.dll` 在位、**未 `git checkout --` 丢弃**。
- 无残留 Unity.exe、无 `UnityProject/Temp/UnityLockfile`；变异残留 0（源文件 == 备份 `cmp` 零差异、代码层 `=false` 0 命中）。
- 文本交付物 6 个逐文件自查：UTF-8 无 BOM（BOM=False）、CR=0 纯 LF、末尾保留单换行 ✓。
- 证据归档：施工 `TempLogs/verifier_r9_15_builder_*`；本次新写 `TempLogs/verifier_r9_15_{editmode,playmode}.xml/.log`、`verifier_r9_15_{arch,config,name}_report.txt`、`verifier_r9_15_TestReport.txt`、`verifier_r9_15_crowd_{off,on}.png`；变异 `verifier_r9_15_{mutant,restored}_playmode.xml/.log`、`verifier_r9_15_KernelCrowdHost.cs.bak`。

**验收裁决**：**PASS with 受限，可提交**。V1–V5 全部 PASS；唯一核心受限为「真实 GPU DrawCall 在 `-batchmode` 下未取得」（施工如实登记、未编数字，验收同样取不到），须由主控签收；V2 的独立 PNG 二次复核缺口已由验收代理补齐。建议主控按「一任务一 commit」提交（本代理未提交、未改 TASKS/HANDOFF/WORKLOG/README/DECISIONS）。

