# RECON —— 粒子 / 后处理 Volume 系统（R9 方向）施工前侦察

- 侦察日期：2026-10-30
- 仓库：`GameFramework`（Unity 2022.3.62f3c1；manifest 声明 URP 14.0.11，PackageCache 实解 14.0.12）
- 性质：**只读侦察**。除本文件外未创建/修改任何文件。
- 取证方式：文件逐行阅读 + GUID 反查（.meta）+ 全仓 grep。拿不准的一律标【待确认】。

---

## 0. 一句话结论

出最终画面的是 **URP**；Volume 资产与切档链路齐全，但**相机从来没开后处理**，所以 Bloom / ColorAdjustments 目前是"配置就绪、画面零效果"；粒子是**零资产**（连一个 prefab 都没有）。

---

## ① 生产渲染路径（含装配点）

**URP 出画面；ZRender 自研 SRP 在 Unity 侧是空壳，只旁路供 FG 的 depth/MV + 超分偏好。**

| 事实 | 证据（路径:行号） |
|---|---|
| 三档画质各自绑定一个 URP 资产 | `UnityProject/ProjectSettings/QualitySettings.asset:7`（`m_CurrentQuality: 1`）、`:47`（Low → guid `da3b02b7…` = `URP_Low`）、`:96`（Medium → `8b1b4d79…` = `URP_Medium`）、`:145`（High → `7a595067…` = `URP_High`） |
| 默认管线也指向 URP_Medium | `ProjectSettings/GraphicsSettings.asset:40` `m_CustomRenderPipeline: {fileID: 11400000, guid: 8b1b4d790d1a8054ca8e5e0aa04c8700}` |
| URP 实际参数（Medium） | `Assets/Settings/Pipeline/URP_Medium.asset:19-30`（Forward、`m_SupportsHDR: 1`、`m_MSAA: 2`、`m_RenderScale: 1`、`m_UpscalingFilter: 0`）、`:70` `m_EnableRenderGraph: 0`、`:81` `m_VolumeFrameworkUpdateMode: 0`（= Every Frame，见包内 `Runtime/Data/UniversalRenderPipelineAsset.cs:335`） |
| 自研管线不画任何东西 | `Assets/ZRender/Runtime/ZRenderPipeline.cs:18-25` —— `Render()` 对每台相机只 `context.Submit()`，无 draw、无 pass |
| 自研管线资产在 ProjectSettings 中零引用 | `Assets/Settings/ZRenderPipeline.asset`（guid `96151baf3a575ea4387625cd848f4737`）在全部 `.asset/.unity/.prefab` 中查不到引用；唯一入口是编辑期 `Assets/ZFrame/View/Pipeline/RenderPipelineSelector.cs:114`（`GraphicsSettings.defaultRenderPipeline = asset`），且运行期调用会抛异常（:93-97） |
| 静态 Renderer 上没有任何 Feature | `Assets/Settings/Pipeline/URP_Low|Medium|High_Renderer.asset:18` `m_RendererFeatures: []`；`:21` `postProcessData` → 包内 `PostProcessData.asset`（guid `41439944…`） |

**运行期唯一的"接管/挂接"点**：`ZrFrameGenerationRuntime.TryInstallPipeline` → `Install`
- `Assets/ZRender/Runtime/ZrFrameGenerationFeature.cs:115-158`（入口/守卫）、`:292-334`（实现）：`Instantiate` 一份 URP 资产副本 + `Instantiate` renderer data + `rendererFeatures.Add(ZrFrameGenerationFeature)`，最后 `QualitySettings.renderPipeline = clone`（`:330`）。
- 文件头 `:11-12` 明写"为什么不改 URP_*.asset：资产污染是明令禁止的"。
- 该 Feature 的全部作用：`ConfigureInput(Motion | Depth)`（`:458`）+ 把 depth/MV 的 `ID3D12Resource*` 交原生（`:226-259`）。

**zrender.dll 在 Unity 侧只吃四类调用**（`Assets/ZRender/Bindings/ZrNative.cs:12-47`）：版本/构建信息、`.zmesh` 加载（MeshLoad/Stats/Free）、超分偏好（`ZR_Upscaler_SetPreferred/GetActive/QueryCaps`）、FG 输入校验（`ZR_FrameGen_*`）。
**没有任何"把 URP 颜色缓冲交给原生超分/合成"的路径** —— FG 输入结构里只有 `Depth` + `MotionVectors`（`ZrFrameGenerationFeature.cs:226-259`）。

→ 结论：**URP 出最终画面**；ZRender 只做（a）FG 的 depth/MV 供给、（b）超分后端"偏好"透传。"两套并存"的实际形态是「URP 画面 + 原生 FG/超分旁路」，不是「URP 出画面 + ZRender 做超分/FG 的画面合成」。

---

## ② Volume / 后处理现状

### 2.1 清单

| 组件 / 资产 | 具体内容 | 是否生效（画面） | 由谁驱动 |
|---|---|---|---|
| `Vol_Low`（`Assets/Settings/Pipeline/Vol_Low.asset:15`） | `components: []`（空 profile） | 不适用（无组件） | PipelineManager 切档 |
| `Vol_Medium`（`:19-25`、`:62`） | Bloom：`threshold 0.9`（:21）、`intensity 1`（:24）、`scatter 0.5`、`skipIterations 1`、`maxIterations 6` | **否** | 同上 |
| `Vol_High`（`:21-24`、`:63-77`、`:90`） | Bloom `intensity 2.5`（:24）+ `ColorAdjustments`：`postExposure 0.1`（:63）、`saturation 8`（:75） | **否** | 同上 |
| `~GradeVolume`（运行时生成，非资产） | `Volume{ isGlobal = true, priority = 10 }`，`sharedProfile` 换成档位 profile | **否**（相机端未开） | `PipelineManager.cs:156-168`（建宿主）、`:130-134`（换 sharedProfile） |
| Tonemapping / Vignette / DOF / LensFlare / 自定义 post pass | **不存在**（三档 profile 只有 Bloom + ColorAdjustments；全仓 0 个 `.shader`/`.shadergraph`，只有 `Native/shaders/*.hlsl`） | — | — |
| **自定义 `VolumeComponent`** | **无**。全仓 `.cs` 无任何 `: VolumeComponent` 子类；只有 Editor 侧 `PipelineSetup` 用 Unity 内置 `Bloom`/`ColorAdjustments`（`Assets/ZFrame/View/Editor/PipelineSetup.cs:207-225`） | — | — |

### 2.2 为什么"不生效"（本次侦察最关键的事实）

1. `Assets/Scenes/SCN_Game.unity:849-858` 的 `Main Camera` 的 `m_Component` 只有 3 项：`Transform` / `Camera`（:868 起）/ `AudioListener`（:935 起）。**没有 `UniversalAdditionalCameraData`**。
   全仓 `.unity` / `.prefab` 检索该脚本 guid `a79441f348de89743a2939f4d699eac1` → **0 命中**。
2. URP 14 该组件的字段默认值就是关：`Library/PackageCache/com.unity.render-pipelines.universal@14.0.12/Runtime/UniversalAdditionalCameraData.cs:322` `[SerializeField] bool m_RenderPostProcessing = false;`。
3. URP 每帧用它赋 `cameraData.postProcessEnabled`：同包 `Runtime/UniversalRenderPipeline.cs:1254`；**无该组件时** `:1267` 直接 `= false`。
4. 全仓代码只在基准里显式写过这个值：`Assets/Game/Editor/ParityBench.cs:157-158`（`renderPostProcessing = false`，契约要求），**没有任何一处写 `true`**。

→ Volume 框架链路（资产 → 全局 Volume → VolumeManager 每帧更新）是通的，但**相机端把后处理关着**，Bloom / ColorAdjustments 在画面上不存在。这是典型的"看起来很完整、实际不可见"，验收时若只断言 `CurrentGradeProfile.name == "Vol_High"`（现成用例：`Assets/ZFrame/Tests/Pipeline/PipelineManagerTests.cs:112-127`）会得到"绿但不真"。

### 2.3 画质档位怎么驱动后处理（三条叠加，不是单一路径）

1. **换 QualitySettings level → 换整套 URP 资产**：`Assets/ZFrame/View/Pipeline/PipelineManager.cs:118-121`（`QualitySettings.SetQualityLevel(..., applyExpensiveChanges: true)`）。
2. **换 Profile 引用（不是改参数）**：`PipelineManager.cs:130-134` `_gradeVolume.sharedProfile = profile`；设计说明 `:22-26` 明写"换 shared 引用、不 Instantiate、不做 profile 实例化、权重保持 1"。
3. **FrameGovernor 降档时代码直改 `volume.weight`**：`Assets/ZFrame/View/Pipeline/FrameGovernorHost.cs:44`（注释）/ `:296-300`（开治理时记原值）/ `:642-648`（应用）；梯子常量 `GovernorLadder.cs:34`（`{0.5f, 0f}`）与 `:118-134`（只收严格低于用户权重的档）。

### 2.4 关于"HANDOFF 提过 R9 做过 fp16 / bloom 阈值"——**查无此事**

- `HANDOFF.md` / `WORKLOG.md` / `TASKS.md` 全文 grep `fp16|FP16|Bloom|bloom|后处理|Volume`：**0 命中**（HANDOFF 只在 `:218`、`:334` 把"粒子/后处理 Volume"列为**未排期候选**）。
- Bloom `threshold = 0.9` 的真实出处是 **A-M2-03**：`Assets/ZFrame/View/Editor/PipelineSetup.cs:207-225`（Medium：`intensity 1`；High：`intensity 2.5`；两者 `threshold 0.9`、`scatter 0.5`），并由 `:249-258`（`ProfileMatches`）与 `Assets/ZFrame/View/Editor/PipelineVerification.cs:111-124` 校验（Low 空 / Medium Bloom / High Bloom+ColorAdjustments）。
- R9 真正改过的相关项是 **MSAA**（插帧把 2→1、关闭即还原）：`HANDOFF.md:55-56`、`ZrFrameGenerationFeature.cs:17-25`、`:377-386`、`ZrFrameGenerationQualityOverrides`。
- 若"fp16"指 HDR 缓冲精度：三档资产都是 `m_HDRColorBufferPrecision: 0`（= `_32Bits`，URP 内部通常 R11G11B10f，见包内 `UniversalRenderPipelineAsset.cs:159-168`），**没有任何 R9 任务改过它**。

---

## ③ 粒子现状：**零资产**

- **包**：`UnityProject/Packages/manifest.json:16` 有内置 `com.unity.modules.particlesystem`；**没有** `com.unity.visualeffectgraph`（全文检索 0 命中）→ 无 VFX Graph，无 GPU 粒子能力。
- **资产**：`Assets` 下 0 个 `.vfx`、0 个文件名含 `particle/vfx` 的资产、0 个带 `ParticleSystem` 的 `.prefab`（`.unity/.prefab` 全文 grep `ParticleSystem` → 0 命中）。`Assets/Prefabs/` 目录**本身就是空的**（只有 `.meta`）。
- **代码**：唯一接触点是对象池回收 `Assets/ZFrame/Core/ObjectPool/PoolResetter.cs:41-45`（`Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear)`），配套测试 `Assets/ZFrame/Tests/ObjectPoolTests.cs:238-241` 用运行时 `new GameObject("ParticleTarget", typeof(ParticleSystem))` 造，**无资产**。
- **场景**：`SCN_Game.unity` 只有 Ground / Pickup / Door / Enemy / Player / Main Camera / Directional Light（全 primitive）；敌人反馈靠改 `material.color`（`Assets/Game/View/Enemy/EnemyView.cs:99-119`），没有命中火花/拖尾/环境氛围粒子。
- **材质/贴图**：`Assets` 下 **0 个 `.mat`、0 张 `.png/.tga/.psd`**；材质全部运行期 `new Material(Shader.Find(...))`（例如 `ParityBench.cs:112-133`）。`Assets/Art/` 只有空目录。

→ **粒子 = 零资产 + 零代码路径 + 零着色器**。任何粒子切片都是从 0 开始。

---

## ④ 设置页接口面

### 4.1 `video.*` 全部 9 行（`Assets/ZFrame/View/Settings/SettingsRegistry.cs:112-205`）

| # | id | 类型 | 落点 | 行号 |
|---|---|---|---|---|
| 1 | `video.fullscreenMode` | Enum | `Screen.fullScreenMode` | `:116-133` |
| 2 | `video.vsync` | Toggle | `QualitySettings.vSyncCount` | `:134-142` |
| 3 | `video.frameRateCap` | Enum | `Application.targetFrameRate` | `:143-153` |
| 4 | `video.qualityTier` | Enum | `PipelineManager.SetTier` | `:154-157` |
| 5 | `video.upscaler` | Enum | `UpscalerSettingOptions.ApplyOptionIndex` → 原生 C ABI | `:161-165` |
| 6 | `video.dynamicResolution` | Toggle | `FrameGovernorHost.SetEnabled` | `:170-174`（id 定义 `FrameGovernorSettingOptions.cs:29`） |
| 7 | `video.frameRateTarget` | Enum | `FrameGovernorHost.SetTargetFps` | `:178-183`（id `:32`） |
| 8 | `video.shadowDistance` | Enum | `QualitySettings.shadowDistance` | `:184-191` |
| 9 | `video.frameGeneration` | Toggle | 插帧 ZFG（Editor/D3D12/代理/URP 四重门控） | `:199-204`（id `FrameGenerationSettingOptions.cs:24`） |

另有无障碍 3 行（`:335-345`），其中 **`accessibility.colorBlindMode` 是 stored placeholder，注释明写"R6 后处理体积接入时在此补 apply 回调（Daltonize 滤镜 Volume override）"**（`:337-339`）—— 这是"后处理类设置行"的现成预留接口先例。

### 4.2 落链路（加一行后如何真的到达渲染）

`SettingsRegistry.BuildDefaults`（`:97-107`）→ `SettingsModel.Load`（`Assets/ZFrame/View/Settings/SettingsModel.cs:221-269`，逐条 `TryGetDefinition` + 类型转换 + `ApplyValue`）→ `ApplyValue`（`:283-294`，`definition.Apply?.Invoke(value)`）→ 冷启动入口 `SettingsBootApplier.AutoBoot`（`SettingsBootApplier.cs:32-36`；`:44-56` 从磁盘应用，`AfterSceneLoad` 保证晚于 `FrameGovernorHost` 的 `BeforeSceneLoad` 自举）。

### 4.3 加"后处理/粒子质量"行要动的地方（事实，不是建议）

1. `BuildVideoPage` 里加一条 `SettingDefinition`；控件类型只支持 `Toggle / Slider / EnumSetting / Note`（`SettingDefinition.cs:133 / 147 / 169 / 236`）。
2. **测试必红**：`Assets/ZFrame/Tests/Debug/SettingsRegistryTests.cs:79-104` 用两个 `CollectionAssert.AreEqual` 把"9 行的类型序列 + id 序列"钉死，并在 `:108-110` **按下标**断言第 5/6 行（`video.Settings[5]`、`[6]`）。加行必须同任务内改该用例。
3. 若要做成"行 → 后处理"，apply 回调需要一个**宿主**（参照 `FrameGovernorHost` 那种 `RuntimeInitializeOnLoadMethod` 自举单例）；否则只能改 SO 资产（见 ⑦ 风险 2）。

---

## ⑤ 性能验证手段（"不破 60fps"能用什么证）

| 手段 | 位置 | 口径 | 可信度 |
|---|---|---|---|
| FrameGovernor（自适应治理） | `FrameGovernorHost.cs:58`（`DefaultTargetFps = 60`）、`:206-209`（`TargetMs = 1000/fps`）、梯子 `GovernorLadder.cs` | 运行时逐帧 CPU/GPU ms，超预算逐级降档 | 只证"治理器会动作"，不证绝对帧率 |
| RecorderFrameTelemetry | `RecorderFrameTelemetry.cs:29-31`（`"GPU Frame Time"` ProfilerRecorder）、`:80-94`（GPU 不可用退 CPU 墙钟） | GPU 帧耗 ns→ms；CPU unscaled 间隔 | 可断言数值/单调性（有 `FrameGovernorTests` 先例） |
| FPS HUD | `DebugPanelRoot.cs:26 / :74 / :105-107`；纯算法 `FpsCounter.cs:10-99` | 滑动窗口 FPS / 平均 / 最差 | 纯 C#，可单测 |
| **ParityBench（最接近"跑分"的现成物）** | `Assets/Game/Editor/ParityBench.cs:54-69`（入口，自带 `EditorApplication.Exit`）、`:166-208`（`camera.Render()` ×20 计时 + DrawCall）、`:218-244`（PNG + metrics 落盘） | 批处理建契约场景，1280×720，**CPU 墙钟均值** | **高**：有数字有 PNG，可写阈值。已有基线 `Docs/Parity/metrics_urp.txt`：`avg_frame_ms: 1.2310`、`HDR=True msaa=2`、`post processing off` |
| 空载护栏 | `Tools/run_tests.ps1:315-330`（调 `Tools/check_machine_quiet.ps1`，结果写 `TestReport.txt`） | 机器是否安静 | 证据质量闸，不是性能用例 |
| 六段回归 | `Tools/run_tests.ps1`（native/editmode/playmode/arch/config/naming） | 功能回归 | **无帧率断言** |

**缺口（明确没有的）**：
- 自动跑分回归不存在（`Docs/交接档案.md:47`、`HANDOFF.md:270` 均标"未实现"；只有一次性 B-R5-04 报告）。
- 没有 PlayMode 帧率/帧耗断言用例。
- `FrameTimingManager` 明确列为"后续"（`RecorderFrameTelemetry.cs:22-25`）。
- **ParityBench 的契约写死"后处理关、场景无 Volume"**（`ParityBench.cs:24-25` 头注释、`:157-158` 代码）→ 想量化 Bloom 成本必须新开一个变体方法（不动原 `RunFromCommandLine` 的契约）。

---

## ⑥ 候选切片

| 切片 | 工作量 | 可验证性（能否被自动化证伪） | 是否动资产 |
|---|---|---|---|
| **A. 让"后处理真的出画面"**：给 Main Camera 补 `UniversalAdditionalCameraData` 并置 `renderPostProcessing = true`（或运行期钩子设），配一条"开/关差可见"的断言 | **小** | **高**：EditMode 可断言 `camera.GetUniversalAdditionalCameraData().renderPostProcessing == true`；更强的是批处理 capture 两帧（Bloom on/off）比较亮区像素差 > 阈值（复用 ParityBench 骨架） | **动**（改 `SCN_Game.unity`）。零资产替代路径：运行期由代码补组件 + 设 true，则只动 `.cs` |
| **B. 后处理质量行**（`video.postFx` 之类）→ 运行期改 Volume override 值（Bloom 强度/质量档），而非换 SO | **中** | **中**：EditMode 断言 `volume.profile.TryGet<Bloom>(out b) && b.intensity.value == x` 属"同义反复"；要真证伪必须落到像素或帧耗 | **动**（`Vol_*.asset`，且会被 PipelineSetup 幂等重建覆盖）；只改运行期 override 则零资产 |
| **C. 粒子最小链路**（命中/拾取反馈，走 ObjectPool + `PoolResetter` 既有语义） | **中** | **中**：EditMode 可断言池复用后 `isStopped` / `particleCount == 0`（`ObjectPoolTests.cs:238-241` 有先例），"看起来对"不可测 | **动**（prefab/material）。可走 100% 运行期生成（`CreatePrimitive` + `AddComponent<ParticleSystem>`）→ 零资产 |
| **D. 量化 Bloom 成本**（ParityBench 变体：post off vs on 的 `avg_frame_ms` + PNG 像素差） | **小-中** | **高**：现成骨架，输出数字 + PNG，阈值可写死（如亮区均值上升 ≥ X、`avg_frame_ms` 增幅 ≤ Y%） | **不动**（临时场景不落盘，`ParityBench.cs:103`） |

---

## ⑦ 风险清单

1. **【高】"Volume 已生效"是假象**：相机无 URP camera data（见 ②.2），任何只断言 profile/组件的用例都会"绿但不真"。后处理类切片必须把"相机端开关"列为第一验收项。
2. **【高】资产敏感 + 脚本会覆盖**：`URP_*` / `Vol_*` 由 `PipelineSetup` 幂等重写 —— `EnsureVolumeProfile` 发现与"差异表"不符就 `DeleteAsset` 重建（`PipelineSetup.cs:186-246`），`EnsureTierAssets`/`ApplyTierParams` 同理（`:136-174`）。手改这些资产会在下次 setup/校验时被静默回滚。`ZrFrameGenerationFeature.cs:11-12` 亦明写"资产污染是明令禁止的"。
3. **【中】加设置行必红**：`SettingsRegistryTests.cs:79-104` 是"9 行"硬断言（含按下标取第 5/6 行）。改它属"改测试"，简报里须写清"这是接口面扩展、不是放宽标准"，否则验收容易被判放宽。
4. **【中】超分不是后处理能力**：`ZR_Upscaler_*` 只存偏好/查能力（`ZrNative.cs:29-34`；`ZrUpscalerSettings.cs:6-7` "本层只透传，不做二次决策"），没有任何路径把 URP 颜色喂给原生超分。别把"超分"当既有后处理基线，也别指望它替 Bloom 兜底。**原生侧超分是否作用于最终呈现 → 【待确认】**（`zplugin.cpp:186-224` 只有偏好/能力解析，未见 dispatch 到 swapchain）。
5. **【中】帧耗证据弱**：唯一数字来自编辑器批处理的 `camera.Render()` **CPU 墙钟**（`metrics_urp.txt` 自述"非独立运行时 GPU 基准"），机器一忙读数能差 30%（`run_tests.ps1:315` 注释：430fps→310fps 的教训）。在编辑器路径上"不破 60fps"**证不了绝对值**，只能证相对增量。
6. **【中】ZRender RG 目前无法承接 Unity 侧后处理**：Unity 里的 `ZRenderPipeline` 是空壳（`ZRenderPipeline.cs:18-25`），原生 RenderGraph 只在独立可执行窗口（`Native/tests/zrhi_d3d12_window.cpp`）里跑通；原生侧也没有 bloom/tonemap/vignette pass（`Native/zrender` 只有 cluster_lighting / shadows / ssr / gi_probes）。`Docs/交接档案.md:45` 说的"与 ZRender 的 RG 结合"在当前代码上**没有落地通道**。
7. **【低】GPU 粒子要新增包**：manifest 无 VFX Graph（见 ③），引入即新增第三方依赖，按 ADR-002 精神需用户批准。
8. **【低】HDR 精度与 Bloom clamp 未验证**：三档均为 `_32Bits`（R11G11B10f），`Vol_High` 的 Bloom `clamp 65472` + `intensity 2.5` 在 HDR 下的过曝/带宽行为**从未被任何任务测过**（全仓无 fp16 记录，见 ②.4）。

---

## 附：本次侦察的边界

- 未运行 Unity、未跑任何测试（纯静态取证 + 包源码交叉验证）。
- 未修改任何资产/代码；本文件是唯一写出物。
- 待确认项：① 原生超分是否真的作用于最终呈现（⑦-4）；② `SCN_Boot.unity` 既无相机也无对象（173 行，只有 RenderSettings 等），引导场景如何呈现画面 → **【待确认】**（与本次方向无关，记录备查）。
