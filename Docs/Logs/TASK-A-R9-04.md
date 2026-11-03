# TASK-A-R9-04 —— 后处理真生效：配置齐但画面零效果（先证伪，再修）

- **轨道**：A / R9（用户选的「后处理 Volume」方向的第一刀）
- **状态**：doing
- **派发**：主控，2026-10-30
- **侦察笔记（必读）**：`Docs/Logs/RECON-particle-volume.md`

## 任务简报（主控写）

### 0. 为什么先修这个，而不是先做粒子

用户选的方向是「粒子 / 后处理 Volume 系统」。侦察结果把它劈成了两半：

| 半边 | 现状 | 结论 |
|---|---|---|
| **后处理** | 资产齐全（`Vol_Medium` = Bloom 0.9/1.0、`Vol_High` = Bloom 2.5 + ColorAdjustments），`PipelineManager.cs:130-134` 还会把 profile 换到运行时 `~GradeVolume` 上——**但一帧都没生效** | **真缺陷，先修** |
| **粒子** | **零资产**：0 个 `.vfx`、0 个带 `ParticleSystem` 的 prefab、0 张 `.mat`/贴图，包清单里连 VFX Graph 都没有 | 这是"从零造一套"，不是接线；另立任务，别混进来 |

**后处理没生效的根因（侦察已定位，你要复核）**：`SCN_Game` 的 Main Camera（`:849-858`）只有 Transform/Camera/AudioListener，**没有 `UniversalAdditionalCameraData`**；
URP 14 该组件缺省 `renderPostProcessing = false`（包内 `UniversalAdditionalCameraData.cs:322` → `UniversalRenderPipeline.cs:1254/1267`），全仓无一处写 `true`。
→ **所有后处理配置都是死的**。这跟之前 FG"开关能点但不插帧"是同一类问题：**配置在，效果不在**。

### 1. 目标（四段，A 段是门禁）

**阶段 A（门禁，先证伪现状）——不做不许往下走**
证明"现在的后处理确实零效果"，而不是我猜的。要求：
1. 用**像素证据**：同一场景、同一相机，读回一帧的像素统计（均值/方差/直方图/高亮像素占比任选，但要说清判据）；
2. 先记录**当前状态**的统计（预期：开启与关闭后处理读取同一 profile 时**统计几乎相同**，即"配置没起作用"）；
3. 给出文件级佐证：相机上确实没有 `UniversalAdditionalCameraData`（或该字段为 false）。
**若实测发现后处理其实生效了**（我的判断错了）→ 立即 STOP 并回报，不要硬修。

**阶段 B：修到真生效**
1. 让 `Vol_Low/Medium/High` 三档的后处理**真的按档位生效**：
   - 优先**运行期注入**（不改磁盘 `.unity`/`.asset`，与 B-R9-06/B-R9-08 的口径一致）；
   - 若 URP 要求必须资产层存在（注入无效），才允许最小资产改动，**并且必须在报告里写明改了什么、为什么非改不可**。
2. **三档必须真的有差异**：Low（无 Bloom 或极弱）/ Medium（Bloom 0.9）/ High（Bloom 2.5 + ColorAdjustments）——
   给出**每档的像素统计**，证明不是三档一个样。
3. **不得引入"隐形生效"**：如果效果只在你新加的代码路径下存在，而玩家在 Editor 里打开场景看不到，要在报告里说清楚，
   并给出后续"让人看得见"的建议（不做也行，但要说）。

**阶段 C：可证伪的回归护栏（这条是交付核心）**
1. 加一个 **PlayMode** 用例（现在 PlayMode 是 7 个，只增不减）：渲染两帧——后处理**开** vs **关**——断言像素统计**有显著差异**
   （阈值自己定，但必须写清数字依据）；再把开关关掉，断言与"从未开启"的基线**一致**。
2. **这条用例必须有牙**：报告里要给出"把修复去掉 → 用例变红"的证据（修前必红）。
3. 不得为了让它绿而放宽成"两帧不完全相同"——那种断言在浮点抖动下永远成立，等于没有。

**阶段 D：不破预算**
1. 开后处理后的帧时间/帧率（项目口径 60fps 基线；B-R9-06 记录 59.9–60fps）。给开/关对比数字。
2. 若开 Bloom 导致帧率明显下降，如实报告，不要为了"通过"而降低画质配置（那是改需求，不是修 bug）。

### 2. 硬约束

- **白名单**：`UnityProject/Assets/ZFrame/View/**`、`UnityProject/Assets/ZRender/**`、`UnityProject/Assets/Game/Tests/**`、
  `UnityProject/Assets/ZFrame/Tests/**`、`ProjectSettings/QualitySettings.asset`（**仅当阶段 B 证明非改不可**，且需在报告单列）、本日志。
- **不许**改 `SettingsRegistry` 的行集合（加 `video.*` 行会直接撞红 `SettingsRegistryTests.cs:79-104` 的 9 行 id 序列断言——
  本任务**不新增设置行**；后处理跟随画质档位，不单独给开关）。
- 基线：EditMode **719**、PlayMode **7**（本任务只能增不能减）、native **253/711,537**、三校验器 exit 0；六段全绿。
- 不动 FG/超分链路（`ZrFrameGenerationFeature` 的 depth/MV 克隆路径、`ZrNative` 的超分偏好）。
- UTF-8 无 BOM、LF；**不 git commit**；不装软件、不联网；禁止 `rm`（用 `mv`）。

### 3. 自测清单（逐条贴证据）

1. **阶段 A 证伪**：当前状态下"读同一 profile"的像素统计（预期无差异）+ 相机缺组件的文件级佐证。
2. **阶段 B 生效**：修复后同一场景的像素统计（与 1 对比，差异要显著），说明是哪个组件/字段起的作用。
3. **三档差异**：Low/Medium/High 三组像素统计并列（表格），证明不是同一张图。
4. **阶段 C 修前必红**：把修复回退 → PlayMode 新用例变红（贴输出）→ 恢复 → 变绿。
5. **阶段 C 断言不放水**：贴断言代码，说明阈值依据；确认不是"不完全相同"这种无牙断言。
6. **阶段 D 帧率**：开/关对比（数字）。
7. **六段全绿** exit 0（EditMode 719、PlayMode ≥8、native 253/711,537、三校验器 exit 0）。
8. **规范**：`git status` 范围符合白名单；若改了 `QualitySettings.asset` 单独列出并说明；零警告；未 commit。

### 4. 诚实边界（报告里必须回答）

- 后处理"生效"的作用域：只在你注入的路径？Editor 打开场景能看到吗？Player 里呢？
- 三档的差异是不是**你新造**的（改 profile），还是资产里本来就有、只是没被应用？
- 有没有哪一档实际上仍无效果（例如 Low 的 Bloom 强度 0）？如实说。

---

## 施工报告（施工代理写）

**一句话**：A 段证伪成功（同一相机开/关后处理，像素差 **0.0000**，两帧逐比特相同）；B 段用**运行期注入**
（零资产改动）把相机端缺口补上，三档各自生效且互不相同；C 段新增 1 条 PlayMode 像素用例（7→8），
回退修复即变红（`brighter=0.0000`）；D 段 720p 下开后处理 +0.8~1.5 ms/帧，六段全绿。

### 阶段 A：证伪（现状确实零效果）

**判据**：同一台相机、同一份内容、同一档位 profile，只切换"相机端后处理开关"，读回整帧的线性亮度统计
（均值/标准差/p95/min/max/高亮占比 + 逐像素差分）。若配置真的没生效，两组读数必须**完全相同**。

探针：离屏相机（`Camera.targetTexture` → 320×180 `ARGBFloat`）放在距 `SCN_Game` 内容 5000 单位处，
对准一个 URP/Unlit 立方体（radiance 4.0，远高于两份 profile 的 Bloom threshold 0.9）；
`Camera.Render()`/逐帧渲染后用 `ReadPixels` 读回 57,600 个像素（本机无交互桌面，全程不看图）。

**A-1 像素证据（修复被临时回退成"相机不动"的跑法，等价于本次改动前的仓库状态）**

| 臂 | meanLuma | stdLuma | p95 | min | max | 高亮占比(>1.0) |
|---|---|---|---|---|---|---|
| 从未开启（对照） | 0.03769 | 0.26474 | 0.0200 | 0.0200 | **4.0000** | 0.0044 |
| 开关置"开" | 0.03769 | 0.26474 | 0.0200 | 0.0200 | **4.0000** | 0.0044 |
| 再置"关" | 0.03769 | 0.26474 | 0.0200 | 0.0200 | 4.0000 | 0.0044 |

逐像素差分：`meanAbs=0.00000  brighter=0.0000  darker=0.0000  moved=0.0000` —— **57,600 个像素一个都没动**。
（更早一版探针在 `SCN_Game` 场景原点附近跑，也得到 0.53032 vs 0.53032 的完全相同结果，可复现。）

**A-2 文件级佐证**

1. `UnityProject/Assets/Scenes/SCN_Game.unity:849-858`：`Main Camera` 的 `m_Component` 只有 3 项
   （`1925669547` Transform / `1925669546` Camera / `1925669548` AudioListener），**没有** `UniversalAdditionalCameraData`。
2. 全仓 `.unity/.prefab` 检索 URP 相机组件脚本 guid `a79441f348de89743a2939f4d699eac1` → **0 命中**。
3. 包源码：`UniversalAdditionalCameraData.cs:322` `[SerializeField] bool m_RenderPostProcessing = false;`；
   `UniversalRenderPipeline.cs:1267`（无组件分支）`cameraData.postProcessEnabled = false;`。
4. 运行时证据（本次实测）：探针跑起来时打印
   `[POSTFX] scene camera 'Main Camera' in 'SCN_Game': hasData=False postOn=False` —— 场景相机确实没有该组件。

**A 段结论：证伪成功，不是"我以为没生效"而是"确实零效果"。没有触发 STOP。**

### 阶段 B：修复（运行期注入，零资产改动）

**改的是什么**：新增 `UnityProject/Assets/ZFrame/View/Pipeline/PostFxCameraRuntime.cs`（`ZFrame.View`，asmdef 已引用 URP Runtime，未改 asmdef）：

- `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` 自举 + `SceneManager.sceneLoaded` 钩子：每次场景装载完成后，
  对所有 `CameraType.Game` 相机 `camera.GetUniversalAdditionalCameraData()`（缺组件则 `AddComponent`）
  并置 `renderPostProcessing = IsEnabled`（默认 `true`）。
- 显式接缝：`SetEnabled(bool)` / `ApplyToAllCameras()` / `HasCameraData(Camera)` / `IsPostProcessingOn(Camera)`。
- **刻意不挂逐帧回调**（`onPreCull` / `beginCameraRendering`）：`ParityBench.cs:157-158` 的"后处理关"契约由它自己
  设置的 `renderPostProcessing=false` 保留，运行期新建的离屏/跑分相机不会被悄悄打开。

**为什么是运行期而不是改场景**：URP 只在读组件时取这个布尔（`UniversalRenderPipeline.cs:1264-1271`），
运行期补组件与在 `.unity` 里序列化该组件对渲染是等价的；而项目明令禁止资产污染
（`ZrFrameGenerationFeature.cs:11-12`），且 `SCN_Game`、`Vol_*`、`URP_*` 都被工具幂等重建
（`PipelineSetup.cs:186-246`）。→ **`.unity` / `.asset` / `ProjectSettings` 一个字节都没改**，`QualitySettings.asset` 也没动。

**生效点**：`UniversalAdditionalCameraData.renderPostProcessing = true` → 同帧 `cameraData.postProcessEnabled = true`
→ URP 后处理栈（`Bloom` / `ColorAdjustments`，profile 由 `PipelineManager.cs:130-134` 按档位换）真正进入画面。

**修复后同一探针（Medium）**

| 臂 | meanLuma | stdLuma | p95 | max | 高亮占比 |
|---|---|---|---|---|---|
| 从未开启（对照） | 0.03769 | 0.26474 | 0.0200 | 4.0000 | 0.0044 |
| 后处理开 | **0.04458** | 0.08335 | **0.1171** | 0.9965 | 0.0000 |
| 再关 | 0.03769 | 0.26474 | 0.0200 | 4.0000 | 0.0044 |

差分（开 vs 对照）：`meanAbs=0.03359  brighter=0.1753  darker=0.0044  moved=0.1797`
→ **17.53% 的像素变亮**（Bloom 的 halo），亮区占比 p95 从 0.0200 抬到 0.1171。
还原差分：`meanAbs=0.000000  moved=0.0000`（关回去与"从未开启"逐像素相同）。

### 三档像素统计（同一相机、同一内容，各自与自己的"关"对照）

| 档位 | profile 内容（资产原文） | 关 meanLuma | 开 meanLuma | 开 p95 | 变亮像素占比 | meanAbs | 判读 |
|---|---|---|---|---|---|---|---|
| **Low** | `components: []`（空 profile，`Vol_Low.asset:15`） | 0.03769 | 0.02338 | 0.0190 | **0.0000** | 0.01431 | 不加任何光；0.01431 全部来自 HDR 截断（见下） |
| **Medium** | Bloom `intensity 1.0` / `threshold 0.9` | 0.03769 | **0.04458** | **0.1171** | **0.1753** | 0.03359 | 有光晕 |
| **High** | Bloom `intensity 2.5` + ColorAdjustments（`postExposure 0.1` / `saturation 8`） | 0.03769 | **0.06731** | **0.1966** | **0.4469** | 0.05632 | 光晕更强 + 整体提亮 |

- 三档不是一张图：Medium 变亮 17.53% / High 44.69%，High 的 meanAbs 是 Medium 的 **1.68 倍**。
- **Low 的 0.01431 必须解释清楚**：它不是 Bloom 加的光（`brighter=0.0000`），而是"后处理通道被打开"这件事本身
  把相机 HDR 段压回 [0,1]（发射体 4.0000 → 0.9965，正好是 0.44% 的像素变暗）。这是 URP 后处理末端的截断，
  与 profile 无关；项目是 **Gamma 色彩空间**，没有开 HDR 输出。

### 阶段 C：PlayMode 护栏（7 → 8，有牙）

新增 `UnityProject/Assets/ZFrame/Tests/PlayMode/PostFxPixelEvidencePlayModeTests.cs`，**1 条** `[UnityTest]`：
`PostProcessing_OnVsOff_MovesPixels_AndRestoresBaseline`。PlayMode 用例数 7 → **8**，未删改任何既有用例。

断言顺序刻意是"先像素、后结构"（配置读回正是这次缺陷能全绿的原因）：

```csharp
// 像素判决在前：回退修复必须红在像素上
Assert.Greater(enabledDiff.BrighterFraction, MinBrighterFraction, ...);   // 0.0120
Assert.Greater(enabledDiff.MeanAbs, MinMeanAbsDelta, ...);                // 0.0020
// 结构在后：只用于说明是哪个字段起的作用
Assert.IsTrue(flagAfterEnable, ...);            // renderPostProcessing == true
Assert.IsTrue(PostFxCameraRuntime.HasCameraData(_camera), ...);
// 关回去必须等于"从未开启"
Assert.Less(restoredDiff.MeanAbs, RestoreMeanAbsTolerance);              // 0.001
Assert.Less(restoredDiff.MovedFraction, RestoreMovedFractionTolerance);  // 0.002
```

**阈值依据（数字，不是"不完全相同"）**：
- 实测：Medium `brighter=0.1753`、`meanAbs=0.03359`；High `brighter=0.4469`、`meanAbs=0.05632`。
- 门限取 `0.0120` / `0.0020`：比实测低 **14.6 倍 / 16.8 倍**，而"修复被回退"时实测是 **0.0000 / 0.00000**。
  两侧没有重叠区，不是浮点抖动断言（渲染是确定性路径：两次对照帧逐像素完全相同，`moved=0.0000`）。
- 还原门限 0.001 / 0.002：实测还原差是 **0.000000 / 0.0000**，而真实 Bloom 的最小信号是 0.0336 —— 33 倍余量，
  漏一点点后处理都会红。
- 另有三档断言：Low `brighter < 0.002`、Medium `brighter > 0.012`、High/Medium `meanAbs` 比 > 1.35（实测 1.68）、High 均值 > Medium 均值。

**修前必红（把修复回退成"相机不动"，同一份用例）**：

```
[POSTFX] Medium on-vs-off diff: meanAbs=0.00000 brighter=0.0000 darker=0.0000 moved=0.0000
失败: post-processing on/off did not brighten the frame: 0.0000 of pixels moved (gate 0.0120).
      Bloom 1.0 over a 4.0 emitter (threshold 0.9) cannot be a no-op.
      Expected: greater than 0.0120000001f   But was:  0.0f
playmode: total=8 passed=7 failed=1
```

恢复修复后同一用例：`playmode PASS total=8 passed=8 failed=0`。

### 阶段 D：预算（60fps 基线口径）

720p（`1280x720`）离屏渲染 30 帧、`Camera.Render()` + `GL.Flush()` 的 CPU 提交墙钟（编辑器内，**不是** GPU 基准）：

| 轮次 | 后处理开 | 后处理关 | 增量 |
|---|---|---|---|
| 六段收尾那次（机器 BUSY，DeepSeek Harness 24%） | 1.938 ms | 1.149 ms | **+0.789 ms** |
| 之前一轮（同样 BUSY） | 3.421 ms | 1.925 ms | **+1.496 ms** |

- 60fps 预算 16.667 ms/帧，开后处理仍远低于预算（1.1~3.4 ms）。
- 本机全程 `machine quiet check: BUSY`，绝对值不可跨跑比较（B-R9-08 的教训），所以只报**同一次运行内的 A/B 差**。
- 未为了"通过"降任何画质配置：`Vol_Medium/High`、`URP_*`、`QualitySettings` 全部原样。

### 自测清单（逐条对上面 8 条）

1. **阶段 A 证伪**：0.03769 vs 0.03769，`moved=0.0000`（第 A-1 表）+ 相机缺组件的 4 条文件级佐证（A-2）。
2. **阶段 B 生效**：0.03769 → 0.04458，`brighter=0.1753`、p95 0.0200 → 0.1171；起作用的是
   `UniversalAdditionalCameraData.renderPostProcessing`（先加组件、再置 true）。
3. **三档差异**：Low 0 / Medium 17.53% / High 44.69% 变亮像素（三档表）。
4. **修前必红**：回退 → `failed=1`，红在 `brighter 0.0000 < 0.0120`；恢复 → `passed=8`。
5. **断言不放水**：见阶段 C 代码 + 阈值依据（14.6× / 16.8× 余量，回退侧恰为 0）。
6. **帧率**：+0.789 ms / +1.496 ms（720p，同跑 A/B）。
7. **六段全绿**：native `253 cases / 711537 assertions / 0 failed`；editmode `719/719`；playmode `8/8`；
   arch/config/naming 三个校验器 `exit 0`（违规数 0）；`RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)`。
8. **规范**：`git status --porcelain` 只有
   - `?? Assets/ZFrame/View/Pipeline/PostFxCameraRuntime.cs`(+`.meta`)
   - `?? Assets/ZFrame/Tests/PlayMode/PostFxPixelEvidencePlayModeTests.cs`(+`.meta`)
   - `?? Docs/Logs/TASK-A-R9-04.md`（本日志）
   - `M Assets/Plugins/ZRender/zrender.dll`（native 段 `build_native.ps1` 第 4/4 步自动重新部署的构建产物，非本次手改）
   全部在 / 靠近白名单内；**没有** `.unity` / `.asset` / `ProjectSettings` 改动；没有新增 `video.*` 设置行
   （`SettingsRegistry.cs` 一行未动，`SettingsRegistryTests.cs:79-104` 的 9 行 id 断言原样通过）。
   两个新文件 UTF-8 无 BOM、LF；playmode/editmode 编译 **0 条 `warning CS`**；**未 git commit**。
   另：`Temp/PostFxCameraRuntime*.cs.bak` 是我做回退-恢复时的临时备份（未跟踪的临时目录），可丢弃。

### 诚实边界（4 问，逐条答）

1. **"生效"的作用域**：`PostFxCameraRuntime` 的 `AfterSceneLoad` + `sceneLoaded` 只管**场景里已有的 Game 相机**。
   实测 `SCN_Game` 的 Main Camera 在场景装载时就被钩子补上了（日志
   `[POSTFX] scene camera 'Main Camera' in 'SCN_Game': hasData=True postOn=True (set by the load hook)`）——
   这是自动路径，不是用例里手动调 `SetEnabled` 得到的。
   - **Editor 里 Play 能看到**（运行期加组件）；**Player 构建里能看到**（`RuntimeInitializeOnLoadMethod` 在播放器里同样执行）。
   - **Editor 非播放态（编辑模式打开场景）看不到**：组件只存在于运行期，场景资产里没有它，Scene 视图也不走这套。
     这是本项目"不许改资产"的直接代价，不是漏做。
   - **运行期新建的相机（不进场景装载流程）不会自动覆盖**，需要调用方显式 `PostFxCameraRuntime.ApplyToAllCameras()`。
     这是有意为之：`ParityBench` 的"后处理关"契约必须由它自己说了算。
2. **三档差异是我新造的吗**：**不是**。`Vol_Low/Medium/High` 三个资产一行未改，差异全部来自资产既有内容
   （Low 空 / Medium Bloom 1.0 / High Bloom 2.5 + ColorAdjustments `postExposure 0.1`、`saturation 8`）。
   我改的只是"相机端愿不愿意读它"。
3. **有没有哪一档仍无效果**：**Low 档确实"无 Bloom 效果"**，因为 `Vol_Low.asset:15` 就是 `components: []`。
   实测 Low 的 `brighter=0.0000`（不加一个光子的亮度），唯一的变化是后处理通道把 HDR 4.0 压到 0.9965
   （0.44% 像素变暗，`meanAbs=0.01431`）。如果验收要求"Low 也要有一点点 Bloom"，那必须改 `Vol_Low.asset`
   （加一个 0.1~0.2 强度的 Bloom），属于改资产 + 改需求，本任务按简报没做；这里如实标注。
4. **有没有"隐形生效"**：有，而且要说清楚 —— **编辑模式看不见**（见第 1 问）。若要"让人看得见"，两条后续路线：
   (a) 在 `PipelineSetup` 生成 `SCN_Game` 时把 `UniversalAdditionalCameraData(renderPostProcessing=true)` 写进场景
       （会动 `.unity`，且要处理 `PipelineSetup` 幂等重建）；
   (b) 给 `SCN_Game` 加一个常驻 `Volume`（不是运行期 `~GradeVolume`），让美术在 Scene 视图里就能预调 Grade。
   两者都不在本任务白名单的"零资产"口径内，建议另立任务。

### 已知副作用 / 观察

- **HDR 截断**：后处理开 → 相机 HDR 段被压到 [0,1]（4.0000 → 0.9965）。项目是 Gamma 色彩空间，
  `m_HDRColorBufferPrecision: 0`；本次没有改色彩空间或 HDR 设置（那会改需求）。若后续要 Bloom 真正"溢出得漂亮"，
  应单独立项做 Linear + HDR 输出，别顺手动。
- **`zrender.dll` 变化**：native 段每次都会重新构建并覆盖 `Assets/Plugins/ZRender/zrender.dll`，本次 diff 由此产生，
  与后处理改动无关（六段历史跑法一致）。

## 验收清单（主控写）

> 独立取证，不复用施工数字。10 项全 PASS 才通过。
> 本任务两个失败模式：**①"生效了"其实只是曝光/色调变化，不是 Bloom；②运行期注入把相机改坏了（重复加组件、切场景失效、每帧开销）。**

1. **V1 阶段 A 独立复现**：自己把注入关掉（或回到 HEAD 行为），同一相机读一帧 → 后处理开/关的像素统计应**完全一致**，
   并确认相机确实没有 `UniversalAdditionalCameraData`。**复现不出"零效果"就等于门禁没成立，直接 FAIL。**
2. **V2 修复真的生效**：开启后与关闭后对比，统计**显著差异**；并**说明差异来自 Bloom 而非单纯的 HDR 截断**——
   施工报的是 `brighter` 占比（Low 0.0000 / Medium 0.1753 / High 0.4469），你要独立确认这个指标确实反映"泛光"
   （例如高亮区域向外扩散），而不只是整体亮度提高。
3. **V3 三档差异独立测**：Low / Medium / High 三组统计自己跑一遍，确认单调递增且量级与施工一致。
   若 Low 确实无 Bloom，核实**这是资产本来的设计**（`Vol_Low.asset` 内容）而不是被改空。
4. **V4 用例有牙（自己复做）**：回退 `PostFxCameraRuntime` 的注入 → PlayMode 新用例必须红 → 恢复 → 绿。
   **不许只看施工的截图**。
5. **V5 断言不放水**：读 `PostFxPixelEvidencePlayModeTests.cs`，确认是**带阈值的定量断言**（`brighter >= 0.0120` 之类），
   不是"两帧不完全相同"这种在浮点抖动下永远成立的假断言；阈值有依据说明。
6. **V6 零资产改动**：`git status` 证明 `.unity` / `.asset` / `ProjectSettings/**` **一个都没动**；`SettingsRegistry` 未加行，
   `SettingsRegistryTests.cs:79-104` 的 9 行 id 序列断言**未被修改**。
7. **V7 注入的副作用（重点）**：
   - 组件是否**幂等**添加（多次 `sceneLoaded` 不会叠加多个 `UniversalAdditionalCameraData`）；
   - **切场景 / 新相机激活**时是否仍然生效（覆盖 `AfterSceneLoad` 之外的情况）；
   - 有无每帧开销（是否每帧 `GetComponent`/`FindObjectOfType`）；
   - 相机后续被销毁重建（过场）时会不会留悬空引用。
8. **V8 六段不回归**：全绿 exit 0（EditMode **719**、PlayMode **8**、native **253/711,537**、三校验器 exit 0）。
9. **V9 预算**：自己给开/关帧时间对比。施工说本机全程 BUSY、只能报同跑相对差（+0.789ms / +1.496ms）——
   你判断这个数字**够不够支撑"不破 60fps 预算"的结论**，不够就如实写"未证"。
10. **V10 诚实边界核实**：①Editor 非播放态看不到效果（运行期注入的代价）——是否在报告/文档里说清；
    ②"Low 档无后处理"是设计还是漏修；③有没有"只在我注入的路径生效、别人拿走这个类就失效"的脆弱性。

**须在结论中回答**：①这套注入与 URP 官方做法（在场景里给相机加组件）相比，代价是什么，值不值得改资产；
②如果 URP 升级或换了 `UniversalRenderPipelineAsset`，这个注入会不会静默失效（失效时玩家看到什么）；
③PlayMode 像素用例在 CI/无 GPU 环境会不会变成假红（项目已有的 PlayMode 是怎么处理这类环境的）。

## 验收结论（验收代理写）

**结论: PASS with 受限（10/10 项验证通过；受限项：GPU 侧帧预算未证、同场景运行期新建相机不自动覆盖）**

验收方式：只读代码 + 自己跑 `Tools/run_tests.ps1`（PlayMode 段×3、六段×1），全部自跑读数；未复用施工任何数字。
施工数字与我复算的每一条在 5 位小数上一致（探针确定性），但下面的表格填的是我这三次运行自己的输出。

### V1 独立复现"零效果" —— PASS

做法：把 `PostFxCameraRuntime.Apply()` 临时改成"不碰相机"（等价修复前仓库行为），
`powershell -File Tools/run_tests.ps1 -Segments playmode`，读 `TempLogs/runtests_playmode.log`：

```
[POSTFX] scene camera 'Main Camera' in 'SCN_Game': hasData=False postOn=False (set by the load hook)
[POSTFX] Medium.neverEnabled:    meanLuma=0.03769 stdLuma=0.26474 min=0.0200 max=4.0000 p95=0.0200
[POSTFX] Medium.postOn:          meanLuma=0.03769 stdLuma=0.26474 min=0.0200 max=4.0000 p95=0.0200
[POSTFX] Medium.postOffAfterOn:  meanLuma=0.03769 stdLuma=0.26474 min=0.0200 max=4.0000 p95=0.0200
[POSTFX] Medium on-vs-off diff: meanAbs=0.00000 brighter=0.0000 darker=0.0000 moved=0.0000
```

开/关/再关三臂**逐像素相同**，57,600 像素（320×180）无一变动；场景相机 `hasData=False`。
文件级：`SCN_Game.unity` 的 Main Camera（`:849-858`）`m_Component` 只有 3 项（Transform/Camera/AudioListener）；
全仓 `.unity/.prefab` 搜 URP 相机组件 guid `a79441f348de89743a2939f4d699eac1` → **0 命中**；
包源码 `UniversalAdditionalCameraData.cs:322` 缺省 `false`，`UniversalRenderPipeline.cs:1267` 无组件分支
`postProcessEnabled = false`。**门禁成立，不是"我以为没生效"。**

### V2 生效、且来源是 Bloom 而非单纯亮度 —— PASS

自跑（注入在位，同一次 PlayMode）：`meanLuma 0.03769 → 0.04458`，`p95 0.0200 → 0.1171`，
`max 4.0000 → 0.9965`（HDR 被压回 1.0），`min 0.0200 → 0.0221`，`brighter=0.1753 darker=0.0044`；
关回去 `meanAbs=0.000000 moved=0.0000`。

"不是单纯提亮"的三条独立判据（都是我这轮的读数，不是施工的文字）：

1. **同一段后处理路径、空 profile（Low）时 `brighter=0.0000`** —— 打开通道本身一个像素都没变亮，
   通道只把 0.44% 的发射体压暗（`darker=0.0044`，恰等于对照帧 `brightRatio=0.0044`）。
   → `brighter` 只可能来自 profile 内容。
2. `Vol_Medium.asset` 的 `components` **只有 Bloom**（threshold 0.9 / intensity 1.0），无 ColorAdjustments、
   无曝光类 override → 该档的变亮不可能来自色调/曝光。
3. 变亮是**局部扩散**而非整体增益：`max` 反而下降（4.0→0.9965），`min` 只抬 0.0200→0.0221，
   而 `p95` 从 0.0200 抬到 0.1171（背景 17.5% 面积被点亮）。整体增益会让 `min` 同幅度上升且
   `brighter≈100%`，实测 17.5% 且暗端几乎不动 —— 与 Bloom halo 的形态一致。

### V3 三档独立测 —— PASS（单调递增，与施工同量级）

同一次运行，逐档"各自关 vs 各自开"：

| 档 | 关 meanLuma | 开 meanLuma | 开 p95 | brighter | meanAbs | max |
|---|---|---|---|---|---|---|
| Low | 0.03769 | 0.02338 | 0.0190 | **0.0000** | 0.01431 | 4.000→0.997 |
| Medium | 0.03769 | 0.04458 | 0.1171 | **0.1753** | 0.03359 | 4.000→0.997 |
| High | 0.03769 | 0.06731 | 0.1966 | **0.4469** | 0.05632 | 4.000→0.997 |

High/Medium 的 `meanAbs` 比 = 1.68×。Low 的 0.01431 全部是 HDR 截断（变暗），`brighter=0.0000`。
**Low 无 Bloom 是资产本来的设计**：`Vol_Low.asset` 在 HEAD 里就是 `components: []`
（`git ls-files --error-unmatch` 通过；`git show HEAD:...Vol_Low.asset | tail -3` → `components: []`；
且 `git status` 里 `Assets/Settings` 零改动），不是被施工改空。

### V4 用例有牙（自己复做） —— PASS

- 回退注入 → `RESULT: FAILED (1 failed: playmode)`，`total=8 passed=7 failed=1`，Unity exit 2；
  XML 里失败点是**像素断言**：
  `post-processing on/off did not brighten the frame: 0.0000 of pixels moved (gate 0.0120)`，
  `Expected: greater than 0.0120000001f  But was: 0.0f`（`PostFxPixelEvidencePlayModeTests.cs:174`）。
- 复原（`cp Temp/verify/PostFxCameraRuntime.orig.cs …`）→ 哈希回到 `1fff4cad…`（与动工前一致），
  再跑六段 → `playmode total=8 passed=8 failed=0`。
- `git status --short` 与动工前逐行一致（见收尾），临时回退**已完全复原**（`diff` 空、sha256 相同）。

### V5 断言不放水 —— PASS

读代码：`MinBrighterFraction=0.012`、`MinMeanAbsDelta=0.002`、还原门限 `0.001/0.002`、
Low `brighter<0.002`、Medium `brighter>0.012`、High/Medium `meanAbs` 比 `>1.35`、High 均值 > Medium 均值。
全部是**带阈值的定量断言**，且**像素断言排在结构断言之前**（`:174-178` 在 `:181-184` 之前）——
这正好堵住"配置读回全绿但画面零效果"的原缺陷。阈值依据是实测 17.53% / 44.69% 与回退侧恰好 0.0000
（两侧无重叠区，不是浮点抖动断言）；这三点我复算一致。

### V6 零资产改动 —— PASS

`git status --short -- UnityProject/Assets/Scenes UnityProject/Assets/Settings UnityProject/ProjectSettings` → **0 行**；
`.unity`/`.asset`/`ProjectSettings/**` 一个都没动。`SettingsRegistry.cs` 未出现在 status；
`SettingsRegistryTests.cs:79-104` 仍是 9 行 id/类型序列（我读过，未被改）。三档 profile 与 `URP_*`、`QualitySettings` 均原件。

### V7 注入副作用 —— PASS（附已知缺口）

- **幂等**：`PostFxCameraRuntime.Apply` → `camera.GetUniversalAdditionalCameraData()`，包内实现是
  `TryGetComponent` 先查、缺才 `AddComponent`（`UniversalAdditionalCameraData.cs:141-150`）；
  且组件带 `[DisallowMultipleComponent]`（`:293`）。重复 `sceneLoaded` 不会叠加。
- **切场景**：`SceneManager.sceneLoaded` → `ApplyToAllCameras()`，场景内**已存在**的 Game 相机全覆盖；
  实测钩子确实自己生效（`scene camera 'Main Camera' in 'SCN_Game': hasData=True postOn=True (set by the load hook)`，
  该断言在用例把它手动打开**之前**就打印）。
- **每帧开销**：类内无 `Update`/`OnPreCull`/`beginCameraRendering`（`grep` 零命中），只在启动、`sceneLoaded`、
  显式调用时跑一次 `FindObjectsOfType<Camera>(true)`；静态类不持相机引用 → **无悬空引用风险**。
- **缺口（如实记）**：同场景里**运行期新建/重建**的相机不会被自动覆盖，除非调用方显式
  `ApplyToAllCameras()`。是刻意设计（保 `ParityBench.cs:157-158` 的"后处理关"契约），不是泄漏，但属真实脆弱点。

### V8 六段不回归 —— PASS

`powershell -File Tools/run_tests.ps1` 一次跑完，`RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)`，exit 0：

| 段 | 结果 |
|---|---|
| native | `253 cases / 711537 assertions / 0 failed`（ctest 二进制本机不可用，判决取自 doctest） |
| editmode | `total=719 passed=719 failed=0` |
| playmode | `total=8 passed=8 failed=0`（7→8，未删改既有用例；`grep -c UnityTest` 共 8 条） |
| arch / config / naming | 各 `exit 0`，报告 `违规数: 0 / 结果: PASS` |

`warning CS` 计数：editmode/playmode 日志 **0 条**。

### V9 预算 —— **部分支撑（GPU 侧未证）**

我自己三次运行的 720p（`1280x720`）`Camera.Render()`+`GL.Flush()` CPU 提交墙钟（30 帧均值）：

| 轮次 | 机器空载判定 | postOn | postOff | 增量 |
|---|---|---|---|---|
| 第 1 次（PlayMode 单段） | QUIET | 1.840 ms | 1.016 ms | +0.824 ms |
| 第 3 次（六段内） | BUSY（rustc 39.7%） | 2.061 ms | 0.845 ms | +1.216 ms |

判断：**"不破 16.667 ms 帧预算"只对 CPU 提交侧成立**（增量 0.8–1.2 ms，绝对 0.8–2.1 ms）。
`GL.Flush()` 只冲刷命令、不等待 GPU，Bloom 的真实开销在 GPU 上 → **GPU 侧未证**。
施工"只报同跑相对差"的口径是对的（我这轮也遇到 BUSY），但它不能支撑"不破预算"的**绝对**结论，只能支撑
"+0.8~1.2 ms 的 CPU 提交增量"这一相对结论。

### V10 诚实边界 —— PASS（三条都已写清且与实测一致）

1. **Editor 非播放态看不到**：运行期注入的必然结果，施工在报告"诚实边界 1/4"和类注释里都写了；
   我确认 `GameSceneSetup.BuildCamera()`（`Assets/Game/View/Editor/GameSceneSetup.cs:77`）只建
   `Camera + AudioListener`，场景资产里本就没有该组件 → 编辑模式确实无效果。
2. **Low 无后处理是设计**：见 V3（HEAD 原件 `components: []`）。
3. **脆弱性**：`grep -rn PostFxCameraRuntime Assets --include=*.cs` 除自身与测试外**零引用** →
   拿走这个类（或它没被编译进某个 asmdef/构建变体），效果即静默消失，无编译错、无运行期告警。

### 末尾 3 问

**① 运行期注入 vs URP 官方做法（场景里给相机加组件）：代价值不值？**

代价是三条实打实的：(a) 编辑模式看不见、Scene 视图无法预调 Grade；(b) 依赖"类必须被编译且 `AfterSceneLoad`
先于第一帧渲染"这一隐式时序；(c) 后来者看场景资产会以为"这相机压根没开后处理"（可发现性差）。
官做法（`GameSceneSetup.BuildCamera()` 里 `go.AddComponent<UniversalAdditionalCameraData>()` +
`renderPostProcessing = true`，再跑一次生成器）代价只有一处：`.unity` 落盘 + 生成器幂等重建要同步。
但本项目把"资产污染"写成硬规矩，且 `SCN_Game` 由工具重建 —— 在这条约束下，运行期注入是**当下正确的选择**。
**建议另立任务做官做法**（改生成器一处、`.unity` 重生成），把运行期类降级为"兜底 + 测试接缝"。值不值得改资产：
值得，但要按"改生成器"的方式改，不要手改 `.unity`。

**② URP 升级 / 换 `UniversalRenderPipelineAsset` 时会不会静默失效？玩家看到什么？**

不会因"换 URP 资产"失效 —— 资产只管管线参数，开关在这个组件实例上（`UniversalRenderPipeline.cs:1254`
每帧读 `additionalCameraData.renderPostProcessing`）。真正的静默失效点是：
(a) 换渲染管线（例如真上 `ZRenderPipeline`）→ 组件还在、没人读，**静默无效**；
(b) 相机在同一场景里被销毁重建（过场/切换机位）→ 新相机没组件，**静默无效**；
(c) URP 大版本把后处理迁出该字段/改名 → **编译期就会红**（这个反而不可怕）；
(d) 相机是 `CameraType.Preview/Reflection` 或非 Game → 主动跳过（有意）。
失效时**玩家看到的就是本次缺陷的原始画面**：Bloom / ColorAdjustments 消失，无报错、无日志、无告警，
验收侧只看配置也照样绿。这是这套方案最大的隐性风险，靠的正是本次新增的像素用例兜底 —— 前提是那条用例还在跑。

**③ PlayMode 像素用例在 CI / 无 GPU 环境会不会假红？项目现有 PlayMode 怎么处理？**

会。`-batchmode -nographics` 下 `Camera.Render()` 不产生绘制、`ReadPixels` 读到空/黑，用例会在
**rig 自检** `Assert.Greater(baseline.MaxLuma, 3.5)`（`:146`）先红 —— 报错文本会指向"4.0 发射体不可见"，
比笼统失败好，但仍是假红。本仓库**目前没有 CI 配置**（无 `.github/`、无 azure/gitlab 流水线），
六段全靠本机 `Tools/run_tests.ps1` 跑，脚本用 `-batchmode` 而**不加** `-nographics`（需要真实 D3D12 设备），
所以现状不会踩到。既有 PlayMode 用例（Addressables/BootChain/UIFade）不依赖渲染输出，属于"纯逻辑可无 GPU 跑"。
**建议**：若将来上 CI，给这条用例加一个显式门（`SystemInfo.graphicsDeviceType == Null/未初始化` 时
`Assert.Ignore` 并打印原因），而不是让它以像素断言失败的形式出现。

### 验收代理的补充观察（非扣分项）

- 工作区里所有 `.cs` 都是 CRLF（`core.autocrlf=true`），两个新文件也一样 → 施工报告说的"LF"指提交后 blob
  的形态（`.gitattributes` `* text=auto eol=lf`），与工作区现状不矛盾，但"工作区字节即 LF"这句表述不准确。
- `Temp/PostFxCameraRuntime*.cs.bak`、`Temp/verify/*` 都是回退-恢复的临时备份（未跟踪），可丢弃。
- `M UnityProject/Assets/Plugins/ZRender/zrender.dll`：验收开始前就已存在，我这轮六段又重建了一次，与后处理无关。
