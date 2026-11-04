# TASK-A-R9-16 —— URP 变体收敛（12288 变体成本中心）

状态: doing | 派发: 2026-11-04 | 施工: builder-r9-16（general-purpose） | 验收: verifier-r9-16（须与施工不同代理） | commit: （待填）

## 任务简报（主控写）

### 背景（离线体检已核实的硬事实）

- A-AND-06 的 IL2CPP Android 正式包构建耗时 **29 分 16 秒**，其中 **URP 着色器变体 12288 个 ≈ 10 分钟** + IL2CPP C++ 编译 1418 个 `.o` + Gradle。这是目前最明确的构建成本中心。
- `ProjectSettings/GraphicsSettings.asset`：4 个 `*Stripping` 全 **0**、`m_PreloadedShaders: []`、`m_ShaderVariantLogLevel: 0`、`m_AlwaysIncludedShaders` 8 项。
- `Assets/Settings/Pipeline/URP_Medium.asset`：`m_UseSRPBatcher: 1`、`m_ShaderVariantLogLevel: 0`、`m_RequireDepthTexture: 0`、`m_RequireOpaqueTexture: 0`、`m_MSAA: 2`、`m_RenderScale: 1`。
- 激活管线判定（锁版本）：**Unity 2022.3.62f3c1 + URP 14.0.11**，Windows x64（780M 核显）/ Android IL2CPP arm64。内置管线 / HDRP 结论不适用。

### 目标（按此实现，细节可斟酌但报告说明）

1. **可复核的变体计数口径**：先调研 Unity 2022.3 在 `-batchmode` 下可用的统计 API（`ShaderUtil` / `IPreprocessShaders` 回调计数 / 构建日志解析），落一个 Editor 侧工具（菜单项或 `Tools/` 脚本），输出「当前 URP shader 的关键字组合与变体数」。**若 batchmode 拿不到真实变体数，如实标受限**，并给出「下次 Windows / Android 构建时如何测量」的可执行步骤，不许编数字。
2. **剥离器**：实现 `UnityEditor.Build.IPreprocessShaders`（Editor 程序集），按**可配置规则**剥离本工程明确用不到的 keyword 组合（候选：`DEBUG_DISPLAY`、`_WRITE_RENDERING_LAYERS`、`_ADDITIONAL_LIGHTS_VERTEX`、`_MAIN_LIGHT_SHADOWS_CASCADE` 与画质档不匹配的组合等——**施工须先核对实际 shader 与档位矩阵，不许照抄这份候选表**）。
   - **默认关闭 / 白名单保护**：必须有一个显式开关（EditorPrefs 或配置文件）才启用剥离，避免误伤导致粉红材质；报告写清「误伤的判据」与回滚方式。
   - **不许把 URP 必需变体剥掉**：至少覆盖当前三档（URP_Low/Medium/High）与 Volume 效果所需的组合。
3. **纯函数规则 + EditMode 测试**：把「该 keyword 组合是否剥离」抽成可测纯函数，测试用例覆盖命中 / 不命中 / 边界（空关键字、大小写、平台分支），**变异有牙**。
4. **改前改后对照口径**：给出「规则命中数 / 预计减少的变体数」的可复核测量方法，附改前改后的同一命令输出。**不承诺帧率、不编构建时长**；若无法实测，明确写「未做完整构建对照」及原因。
5. **零资产污染**：不改 `URP_*.asset`、`QualitySettings.asset`、`GraphicsSettings.asset`、游戏内容、材质、场景；不改原生 C++；不改 `TASKS/HANDOFF/WORKLOG/README/DECISIONS`。
6. **回归**：EditMode ≥ **880**、PlayMode ≥ **48**、native **311** 不降、三校验器 exit 0；不得引入新的编译警告（/W4 /WX 是原生口径，C# 侧零 error CS）。

### 允许的降级出口（如实即可，不算 FAIL）

若 Unity 2022.3 的批处理环境确实无法统计真实变体数、或 `IPreprocessShaders` 在本工程 URP 包版本下行为不可验证，则交付「规则 + 纯函数 + 单测 + 静态体检报告 + 可执行测量步骤」，并在报告中把「真实构建变体数 / 构建时长对照」明确列为**未验证**，不得写成已收敛。

### 约束

- 白名单：`UnityProject/Assets/ZFrame/Editor/**`、`UnityProject/Assets/ZRender/Editor/**`（若合适）、`UnityProject/Assets/ZFrame/Tests/**`、`Tools/**`（仅当加统计命令）、`Docs/Logs/TASK-A-R9-16.md` 的「## 施工报告」节、`Docs/Standards/**`（仅当写规范）。
- 禁止：`Native/**`、`Assets/Game/**`、`Assets/Audio/**`、`Assets/Settings/Pipeline/*.asset`、`ProjectSettings/**`、`TASKS.md` / `HANDOFF.md` / `WORKLOG.md` / `README.md` / `DECISIONS.md`；不 `git add` / 不 `git commit`；UTF-8 无 BOM + LF（写完自查，Windows 脚本别写成 CRLF）；`.meta` 由 Unity 生成，不手改。
- 不要 `git checkout --` 丢弃 `UnityProject/Assets/Plugins/ZRender/zrender.dll`；不要整仓 checkout。
- 唯一可写日志节是「施工报告」；**不要写「验收结论」**，不要勾选验收清单。

### 环境纪律（真实踩过，详见 `Docs/交接档案.md` §3）

- 跑测试前清 8 个代理变量（大小写成对），否则 MSBuild / Unity 启动失败。
- Unity 批处理单实例：跑前查进程 + 删 `UnityLockfile`；跑测试**勿带 `-quit`**；xml 出现但 5 分钟不退 → `taskkill //PID <pid> //F`；证据以本次新写 results.xml 为准。
- Bash 侧禁止转调 powershell → 跑 .ps1 用 PowerShell 工具；本机 GUI Editor 加 `-force-d3d12` 会挂起。
- 清理用 `mv` 归档，不要 `rm -rf`；跑完查 `git status`。

### 铁律

施工与验收必须不同代理；一任务一 commit；日志四节齐全；验收代理须亲自做「修前必红」定向变异；「说变快了必须附改前改后的测量方式」；结论必须锁 Unity 大版本 + 渲染管线 + 目标机型。证据不齐写受限，绝不把「部分完成」说成「已完成」。

## 施工报告（施工代理写）

**结论：按简报「允许的降级出口」交付。** 规则 + 纯函数 + EditMode 单测 + 计数工具 + 静态体检 + 可执行测量步骤全部落地并可复核；**真实构建变体数 / 构建时长对照本轮未验证**（batchmode 无构建、`IPreprocessShaders` 不触发、变体日志 level=0），**未宣称 12288 已收敛**、不承诺帧率、不编构建时长。以下所有数字均来自本轮实跑输出。

---

### R1 环境与锁定（结论口径）

| 项 | 值 | 证据 |
|---|---|---|
| Unity | **2022.3.62f3c1** | 计数工具 `UnityVersion : 2022.3.62f3c1`；`AGENTS.md` |
| 渲染管线 | **URP**（`manifest.json` / `packages-lock.json` 声明 **14.0.11**；本机 `Library/PackageCache` 目录为 **14.0.12**） | `manifest.json:5`、`packages-lock.json:68-69`、磁盘目录实测 |
| 目标机型 | Windows x64（AMD 780M / D3D12 主）、Android IL2CPP arm64 | 简报硬事实；计数工具运行时 `ActiveBuild : Android` |
| 激活管线 | `GraphicsSettings.m_CustomRenderPipeline` guid `8b1b4d79…` = `URP_Medium.asset` | `GraphicsSettings.asset:41` + `URP_Medium.asset.meta` |
| HEAD / 工作区 | 派发 `6e97690`，派发时干净；**本轮无新 commit** | `git log --oneline -1` |

> **版本漂移如实登记**：`manifest.json` 与 `packages-lock.json` 均为 `14.0.11`，但磁盘 `Library/PackageCache/com.unity.render-pipelines.universal@14.0.12`。本报告的 shader/keyword 核对取自磁盘缓存内**实际被编译的** 14.0.12 源码；对外结论仍锁 `14.0.11`（声明版本），并注明该 patch 漂移。内置管线 / HDRP 结论不适用。

---

### R2 调研先行：`-batchmode` 下变体统计手段（试过什么 / 哪条可行 / 哪条不可行）

计数工具 `UrpVariantCounter.ProbeShaderUtilApi()` 在 batchmode 里**反射枚举 `UnityEditor.ShaderUtil` 全部静态方法**（实测 30 个变体/关键字相关），并逐条实调，输出在 `/tmp/r9_16_counter_final.log`（本次新写）。结论：

| 试过 | 结果 | 可行性 |
|---|---|---|
| **试1** `ShaderUtil.GetShaderVariantCount` | **不存在**于本 Unity 版本的 `ShaderUtil`（枚举出的 30 个方法里无此名） | ❌ 不可行 |
| **试2** `ShaderUtil.GetShaderGlobalKeywords(Shader)` / `GetShaderLocalKeywords(Shader)` | 均为 **nonpublic、反射可调**；URP/Lit 各返回 **59** 个关键字 | ✅ 可枚举关键字（拿不到「本次编译的变体数」） |
| **试3** `ShaderUtil.GetVariantCount(Shader, Boolean)` | **nonpublic、反射可调**；URP/Lit → `13919720572114`，URP/Particles/Unlit → `295194` | ⚠️ 仅 **potential / 理论组合数**，不是构建实际编译变体数（Lit 那个 1.4e13 量级即反证）。**本报告不用它做任何改前改后对照** |
| 旁证：公开 API `GetShaderData(Shader)`、`GetPassKeywords(...)`、`PassHasKeyword(...)`、`GetShaderPlatformKeywordsForBuildTarget(...)`；nonpublic `GetShaderVariantEntriesFiltered`、`CompileShaderVariant`、`PreprocessShaderVariant`、`GetCurrentShaderVariantCollectionVariantCount` | 存在 | 均为「单 shader / 单变体 / SVC」维度，非「一次完整构建实际编译了多少变体」 |
| **试4** `IPreprocessShaders` 回调计数 | 该接口**只在真实 Player / AssetBundle 构建时回调**；batchmode 不构建 ⇒ 不触发 | ❌ 本轮不可得（记入未验证） |
| **试5** 构建日志解析 | `Assets/UniversalRenderPipelineGlobalSettings.asset:33 m_ShaderVariantLogLevel: 0` 不产出变体日志；且本任务**禁止改** `GraphicsSettings.asset` / `URP_*.asset` | ❌ 本轮不可行 |

**调研结论**：Unity 2022.3.62f3c1 的 `-batchmode`（无构建）**拿不到真实构建变体数**。`ShaderUtil` 反射路径可枚举关键字、可拿 potential 估计，但都不等于「本次完整构建实际编译的变体数」；`IPreprocessShaders` 与变体日志两条路都需要真实构建。因此本任务的**可行口径改为「静态单关键字组合的规则命中数」**——输入集合来自真实 shader 源码、完全可复核、不编数字。

顺带核对到 **12288 变体的直接来源**：`GraphicsSettings.asset:37` 的 `m_AlwaysIncludedShaders` 第 8 项 guid `0406db5a14f94604a8c57ccfbc9f3b46` = URP 包 `Shaders/Particles/ParticlesUnlit.shader.meta`（`grep -rl` 实证）。`ParticleRuntime.cs:27-39` 注释解释了为何必须 always-include（Player 里 `Shader.Find` 否则返回 null → 粒子发黑）。该 always-include 使该 shader 全变体进入构建，与 A-AND-06 报告的「12288 变体 /Particles Unlit pass ≈10 min」一致。**12288 是 A-AND-06 的历史构建数字，本轮未重新实测**。

URP 侧另一事实：`UniversalRenderPipelineGlobalSettings.asset` 的 `m_StripUnusedVariants: 1`（URP 自带 `ShaderScriptableStripper` 已开）、`m_StripUnusedPostProcessingVariants: 0`（后处理变体未被 URP 剥）、`m_StripDebugVariants: 1`；三档 URP asset 内 `m_Prefilter*` 字段多为 1（URP 构建期写入的预过滤数据）。**本剥离器与 URP 自带 stripper 部分重叠，不宣称能据此进一步降低 12288**。

---

### R3 交付物清单（全部在白名单内）

| 文件 | 内容 |
|---|---|
| `UnityProject/Assets/ZFrame/Editor/UrpVariantRules.cs` | **纯函数** `Evaluate` + **21 条可配置规则**（每条带判据）+ **16 个白名单保护关键字**；零 `UnityEditor.Build` / `UnityEngine` 依赖 |
| `UnityProject/Assets/ZFrame/Editor/UrpVariantStripper.cs` | `IPreprocessShaders` 适配层 + EditorPrefs 总开关（默认关）+ 逐条禁用 + 菜单项 + 命令行设开关 |
| `UnityProject/Assets/ZFrame/Editor/UrpVariantCounter.cs` | 计数工具 + `ShaderUtil` 调研探测 + 改前/改后双栏 + 必需变体自检 |
| `UnityProject/Assets/ZFrame/Tests/Editor/UrpVariantRulesTests.cs` | **36 条** EditMode 用例（命中/不命中/空/大小写/平台/Development/白名单/可配置/治理） |
| `Tools/run_urp_variant_counter.sh` | 可直接重跑的统计命令（纯 ASCII、LF、无 BOM） |
| `Docs/Logs/TASK-A-R9-16.md` 本节 | 施工报告 |

Unity 自动生成的 4 个 `.cs.meta` 由 Editor 生成，未手改。

---

### R4 计数工具与本次输出

**可直接重跑的命令**（Git Bash；本机勿加 `-force-d3d12`，勿从 Bash 转调 powershell）：
```bash
cd C:/Users/1/Desktop/zcode/GameFramework
bash Tools/run_urp_variant_counter.sh
# 等价于：
"c:/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe" -batchmode -quit \
  -projectPath "C:/Users/1/Desktop/zcode/GameFramework/UnityProject" \
  -logFile - -executeMethod ZFrame.Editor.UrpVariantCounter.RunFromCommandLine
```
亦可菜单 `ZFrame/URP 变体/统计与规则命中`。本轮实跑 **exit=0、零 `error CS`**，完整输出在 `/tmp/r9_16_counter_final.log`。

**输入集合 K（可复核）**：工具从 `PackageInfo.FindForAssetPath(...).resolvedPath` 解析 URP 包根目录，静态读取 4 个本工程实际用到的 shader 的 `#pragma` 行、去重排序：

```
Shaders/Particles/ParticlesUnlit.shader   (always-included 第 8 项 / 12288 来源)
Shaders/Lit.shader                         (Shader.Find 硬编码)
Shaders/Unlit.shader                       (Shader.Find 硬编码)
Shaders/PostProcessing/UberPost.shader     (Vol_Medium/High 的 Bloom/ColorAdjustments 落点)
```
> 解析只取 `#pragma multi_compile*|shader_feature*` 行的**显式 token**；`_` 占位、行尾 `//` 注释（如 `// forward-only variant`）、`multi_compile_fog`/`multi_compile_instancing` 等无显式 token 的内建维度不凭空补关键字。修正行尾注释截断后 **K = 73 个**（首次解析曾误收 `forward-only`/`variant` 两个注释词得 75，已修并重跑）。

**本次关键输出（原样摘录）**：
```
UnityVersion   : 2022.3.62f3c1
Pipeline       : URP (manifest.json / packages-lock.json = 14.0.11；本机 PackageCache 目录 = 14.0.12)
ActiveBuild    : Android
StripperEnabled(default off): False
去重关键字全集 K = 73 个：[DEBUG_DISPLAY, DIRLIGHTMAP_COMBINED, ... _USE_FAST_SRGB_LINEAR_CONVERSION]
必需关键字自检: keep=17 / 17 误伤=0 => OK（未剥任何必需变体）
```
口径声明：K 是**静态单关键字组合**，工具输出的是**规则命中数**，不是真实构建变体数。

---

### R5 剥离规则与判据（核对实际 shader 与三档矩阵，未照抄候选表）

**三档矩阵核对（逐个打开 YAML）**：
- `URP_Low/Medium/High.asset`：`m_AdditionalLightsRenderingMode: 1`(PerPixel) / `m_AdditionalLightShadowsSupported: 0` / `m_SupportsLightLayers: 0` / `m_ReflectionProbeBlending: 0` / `m_ReflectionProbeBoxProjection: 0` / `m_SupportsLightCookies: 1` / `m_MixedLightingSupported: 1` / `m_EnableLODCrossFade: 1` / `m_MainLightShadowsSupported: 1` 三档一致；`m_ShadowCascadeCount` Low=1 / Medium=2 / High=2；`m_SoftShadowsSupported` Low=0 / Medium=0 / **High=1**；`m_PrefilteringModeForwardPlus: 0` / `m_PrefilteringModeDeferredRendering: 0`。
- `URP_{Low,Medium,High}_Renderer.asset`：`m_RenderingMode: 0`(Forward)、`m_UseNativeRenderPass: 0`、**`m_RendererFeatures: []`**（无 SSAO / Decal / ScreenSpaceShadows）。
- `Vol_Low.asset` `components: []`；`Vol_Medium.asset` = **Bloom**；`Vol_High.asset` = **Bloom + ColorAdjustments** ⇒ UberPost 的 `_BLOOM_*` / `_TONEMAP_*` / `_HDR_GRADING` 为必需。
- `QualitySettings.asset`：三档各自绑定 URP_Low/Medium/High，`m_PerPlatformDefaultQuality` 全 1、`excludedTargetPlatforms: []`；`RenderPipelineSelector.cs:24-29` 注释确认 tier matrix 运行期按画质档切换 ⇒ **三档都可能在同一包内被激活，剥离只取「三档并集都不需要」的保守交集，不能按「当前激活档」剥**。
- 关键字精确名取自 URP `Runtime/UniversalRenderPipelineCore.cs :: ShaderKeywordStrings` 与 `ParticlesUnlit.shader` / `UberPost.shader` 的 `#pragma`，未凭空拼写。

**21 条规则（节选判据，完整在 `UrpVariantRules.cs`）**：

| 关键字 | 判据（为什么本工程用不到） |
|---|---|
| `_ADDITIONAL_LIGHTS_VERTEX` | 三档 `m_AdditionalLightsRenderingMode=1`(PerPixel)；`m_PrefilteringModeAdditionalLight=3`(SelectPixelAndOff) 印证 |
| `_ADDITIONAL_LIGHT_SHADOWS` / `_CASTING_PUNCTUAL_LIGHT_SHADOW` | 三档 `m_AdditionalLightShadowsSupported=0` |
| `_LIGHT_LAYERS` / `_WRITE_RENDERING_LAYERS` | 三档 `m_SupportsLightLayers=0` + `m_RendererFeatures=[]` |
| `_REFLECTION_PROBE_BLENDING` / `_REFLECTION_PROBE_BOX_PROJECTION` | 三档对应字段 0 |
| `_FORWARD_PLUS` / `_RENDER_PASS_ENABLED` / `_DEFERRED_STENCIL` / `_DEFERRED_FIRST_LIGHT` | 三档 Forward、`m_PrefilteringModeForwardPlus=0`、`m_PrefilteringModeDeferredRendering=0`、`m_UseNativeRenderPass=0` |
| `_SCREEN_SPACE_OCCLUSION` / `_MAIN_LIGHT_SHADOWS_SCREEN` | `m_RendererFeatures=[]` 无 SSAO / ScreenSpaceShadows feature |
| `_DBUFFER_MRT1/2/3` / `_DECAL_NORMAL_BLEND_{LOW,MEDIUM,HIGH}` / `_DECAL_LAYERS` | 无 Decal renderer feature；URP `m_PrefilterDBufferMRT*=1` |
| `DEBUG_DISPLAY` | `m_StripDebugVariants=1` + 三档 `m_PrefilterDebugKeywords=1`；**仅正式构建**（`ReleaseBuildOnly=true`，Development 保留） |

**核对后推翻简报候选表之处（故意不照抄）**：
- **`_MAIN_LIGHT_SHADOWS_CASCADE` 不剥**。三档 `m_MainLightShadowsSupported=1`、Medium/High cascade=2、运行期可切档；URP `Editor/ShaderBuildPreprocessor.cs:646-648` 明确注「Cascade count can be changed at runtime, so include both of them」。用例 `MainLightShadowsCascade_NotInStripRules` 锁死此结论。
- `_WRITE_RENDERING_LAYERS` 核对后成立（保留为可审计补充），但 URP `m_PrefilterWriteRenderingLayers=1` 已在处理，不宣称新增收益。
- `DEBUG_DISPLAY` 只在**非 Development** 成立，故设 `ReleaseBuildOnly` 分支，不无条件剥。

**白名单保护（16 个）**：`_DISTORTION` / `_CHROMATIC_ABERRATION` / `_BLOOM_LQ` / `_BLOOM_HQ` / `_BLOOM_LQ_DIRT` / `_BLOOM_HQ_DIRT` / `_HDR_GRADING` / `_TONEMAP_ACES` / `_TONEMAP_NEUTRAL` / `_FILM_GRAIN` / `_DITHERING` / `_GAMMA_20` / `_LINEAR_TO_SRGB_CONVERSION` / `_USE_FAST_SRGB_LINEAR_CONVERSION` / `HDR_INPUT` / `HDR_ENCODING`（来自 `UberPost.shader` pragma + Vol 三档）。命中任一即 Keep，**优先级高于剥离规则**。三档必需的光照关键字（`_MAIN_LIGHT_SHADOWS` / `_MAIN_LIGHT_SHADOWS_CASCADE` / `_SHADOWS_SOFT` / `_ADDITIONAL_LIGHTS` / `_LIGHT_COOKIES` / `LOD_FADE_CROSSFADE`）根本不在规则表内，天然 Keep。计数工具 `必需关键字自检: keep=17 / 17 误伤=0 => OK`。

---

### R6 默认关闭 / 白名单保护 / 误伤判据 / 回滚方式

- **默认关闭**：`EditorPrefs` 键 `ZFrame.UrpVariantStripper.Enabled`，默认 `false`；`OnProcessShader` 首行即 `if (!IsEnabled) return;`。本轮计数工具实测 `StripperEnabled(default off): False`。未显式置 true 时**一个变体都不删**，默认状态不可能出现粉红材质。
- **显式开关**：菜单 `ZFrame/URP 变体/启用剥离（谨慎）` / `关闭剥离（回滚）`；或 `-executeMethod ZFrame.Editor.UrpVariantStripper.SetEnabledFromCommandLine -urpStripEnabled true`。
- **逐条可配置**：`ZFrame.UrpVariantStripper.DisabledRules`（逗号分隔 keyword）可停用单条规则；`Evaluate` 亦支持传入自定义 `rules` 数组完整替换规则表。
- **误伤判据**：开启剥离后做一次完整构建，若运行期出现粉红/洋红材质、Bloom 或 ColorAdjustments 后处理消失、阴影 / 额外光异常，即判定误伤。
- **回滚方式**：关闭总开关（菜单或上述 EditorPrefs 置 false / 删键）后重新构建即可。因**不改任何资产**、剥离只发生在构建期内存（`compilerDataList.RemoveAt`），回滚零残留、无需 `git checkout` 任何文件、无需重编 C++。
- **零资产污染**：本剥离器不写 `URP_*.asset` / `QualitySettings.asset` / `GraphicsSettings.asset` / 材质 / 场景 / 游戏内容 / `ProjectSettings/**` / 原生 C++。

---

### R7 改前 / 改后对照（同一命令）；未做完整构建对照

**同一条命令**（`bash Tools/run_urp_variant_counter.sh` → `RunFromCommandLine`）在**同一次运行**内对**同一输入集合 K=73** 分别用「空规则表（改前基线 = 剥离器未配置 / 默认关闭）」与「本任务 21 条规则（改后）」评估，原样输出：

```
[改前] 规则表为空（剥离器未配置 / 默认关闭，不删任何组合）
        evaluated=73  stripped=0  kept=73  whitelistProtected=16
[改后] 本任务核对后的 21 条规则
        evaluated=73  stripped=14  kept=59  whitelistProtected=16
        delta_stripped=+14
  改后命中(可剥)关键字 [14]: [DEBUG_DISPLAY, _ADDITIONAL_LIGHTS_VERTEX,
        _ADDITIONAL_LIGHT_SHADOWS, _CASTING_PUNCTUAL_LIGHT_SHADOW, _DBUFFER_MRT1,
        _DBUFFER_MRT2, _DBUFFER_MRT3, _FORWARD_PLUS, _LIGHT_LAYERS,
        _MAIN_LIGHT_SHADOWS_SCREEN, _REFLECTION_PROBE_BLENDING,
        _REFLECTION_PROBE_BOX_PROJECTION, _RENDER_PASS_ENABLED, _SCREEN_SPACE_OCCLUSION]
  白名单保护命中: [HDR_ENCODING, HDR_INPUT, _BLOOM_HQ, _BLOOM_HQ_DIRT, _BLOOM_LQ,
        _BLOOM_LQ_DIRT, _CHROMATIC_ABERRATION, _DISTORTION, _DITHERING, _FILM_GRAIN,
        _GAMMA_20, _HDR_GRADING, _LINEAR_TO_SRGB_CONVERSION, _TONEMAP_ACES,
        _TONEMAP_NEUTRAL, _USE_FAST_SRGB_LINEAR_CONVERSION]
  口径: 上列数字是「静态单关键字组合的规则命中数」，不是真实构建变体数。
        真实构建变体数 / 构建时长对照：未验证（batchmode 无构建；变体日志 level=0）。
```

> **明确声明：未做完整构建对照。** `14` 是静态单关键字组合的规则命中数，**不是**「减少的变体数」，更不能换算成构建时长。不承诺帧率、不编构建时长。真实构建变体数 / 构建时长对照列为**未验证**。

**下次 Windows / Android 构建时的可执行测量步骤**（补上本轮受限项，供主控另派或验收执行）：
1. **开变体日志**（本任务禁改，需单独授权）：把 `Assets/UniversalRenderPipelineGlobalSettings.asset` 的 `m_ShaderVariantLogLevel`（或对应 URP asset 同名字段）从 `0` 调到非零，构建后**精确还原该单文件**；或用 `ShaderVariantCollection` + `ShaderUtil.GetCurrentShaderVariantCollectionVariantCount` 落盘统计。
2. **空载 + 清代理**：跑 `Tools/check_machine_quiet.ps1` 确认 QUIET；PowerShell 侧清 8 个代理变量、Bash 侧 `unset http_proxy https_proxy all_proxy`；单实例（查 `Unity.exe` + 删 `Temp/UnityLockfile`）。
3. **关态构建**（改前）：确认 `ZFrame.UrpVariantStripper.Enabled=false`（默认），跑 `Tools/build_android.ps1`（Android IL2CPP arm64，默认 IL2CPP）或 `Tools/build_player.ps1`（Windows x64）。记录脚本耗时 + Unity 日志中每个 shader 的 `variants` 行 / `total variants` 汇总行。
4. **开态构建**（改后）：`Unity.exe -batchmode -executeMethod ZFrame.Editor.UrpVariantStripper.SetEnabledFromCommandLine -urpStripEnabled true`（设 EditorPrefs），**同一命令、同一机器、同一平台、同一档位**再构建一次，记录同样两个量。
5. **对照口径**：改前 / 改后都用「变体数 + 构建耗时」两个来自同一测量方式的数字，遵守铁律「说变快了必须附改前改后的测量方式」，并锁 Unity `2022.3.62f3c1` + URP（`14.0.11` 声明 / `14.0.12` 缓存）+ 目标机型。
6. **误伤检查**：装包观察粉红材质 / Bloom / ColorAdjustments / 阴影；有异常立即按 R6 回滚。
7. 构建可能改写 `ProjectSettings.asset` / `version.json`（A-AND-06 设计内行为）与 `URP_*.asset`（URP prefilter 写回），**不要整仓 checkout**，只按纪律精确处理具体文件；`zrender.dll` 时间戳噪声按简报**不要** `git checkout --` 丢弃。

---

### R8 实跑测试数字与 xml 路径

命令（PowerShell 工具，先清 8 个代理变量）：
```powershell
foreach ($n in @('http_proxy','HTTP_PROXY','https_proxy','HTTPS_PROXY','all_proxy','ALL_PROXY','no_proxy','NO_PROXY')) { [Environment]::SetEnvironmentVariable($n,$null,'Process') }
& .\Tools\run_tests.ps1 -Segments editmode,playmode,arch,config,naming
```

| 段 | 本轮结果 | 基线 | 证据 |
|---|---|---|---|
| EditMode | **916 / 916 / 0**（+36 条新用例） | 880 | `TempLogs/runtests_editmode.xml`（本轮新写）、`TestReport.txt` |
| PlayMode | **48 / 48 / 0** | 48 | `TempLogs/runtests_playmode.xml` |
| arch | **exit 0** | exit 0 | `TempLogs/arch_report.txt` |
| config | **exit 0** | exit 0 | `TempLogs/config_report.txt` |
| naming | **exit 0** | exit 0 | `TempLogs/name_report.txt` |
| native | **311 / 311 passed，776716 / 776716 assertions** | 311 / 776716 | 直跑 `Native/build/bin/Release/zrender_test.exe`（未重建，避免 `zrender.dll` 时间戳噪声） |
| 汇总 | **ALL PASS（5 passed, 0 failed）** | — | `RESULT: ALL PASS` |

- 新增 36 条用例全部落在 `ZFrame.Editor.Tests`；EditMode 从 880 → 916，**只增不减**。
- EditMode 首跑出现「xml 已写但 Unity 5 分钟未退」→ 按纪律 `taskkill`（`PASS (taskkilled zombie)`，exit=1 是僵尸被杀，证据取本次新写 results.xml）；复绿那次 Unity 正常 exit 0、40s 完成。属 `交接档案 §3` 已知环境噪声，非回归。
- PlayMode 像素证据用例改写了 `Docs/Parity/crowd_r9_15_on.png`（非本任务改动）。派发时工作区干净，已**针对该单个文件** `git checkout --` 精确还原；未整仓 checkout、未碰 `zrender.dll`。
- C# 侧零 `error CS`（计数工具实跑 `Aborting batchmode` 为 0；首轮曾因 `PackageInfo` 二义性编译失败，已用完全限定名修复并重跑 exit=0）。未改 `Native/**`，无新增原生编译告警。

---

### R9 定向变异（用例有牙）

| 步 | 动作 | EditMode 结果 |
|---|---|---|
| 基线绿 | 规则表 21 条 | **916 / 916 / 0** |
| **改规则** | 从 `UrpVariantRules.StripRules` **删除** `_ADDITIONAL_LIGHTS_VERTEX` 一条（21→20） | — |
| 必红 | `-Segments editmode` | **916 / 912 / 4**，段 **FAIL / exit 1** |
| 还原复绿 | 精确加回该规则 | **916 / 916 / 0**，exit 0 |

变红的 4 条（从 `TempLogs/runtests_editmode.xml` 解析，全部来自 `UrpVariantRulesTests`）：
```
1. AdditionalLightsVertex_IsStripped
2. NonReleaseOnlyRule_StillStrips_InDevelopmentBuild
3. NullElementInKeywords_DoesNotThrow
4. StripRulesCount_Locked
```
**零残留**：变异前 / 还原后 `UrpVariantRules.cs` 的 sha256 完全一致
`d129bcd4c1af37438996e63587bc09bce704c9b470d3e4324ff1e9bed4a11482`；规则数回到 21、靶心关键字存在、`MUTANT` 字样 0 命中。

---

### R10 已知受限 / 未验证（不写成已收敛）

1. **真实构建变体数 / 构建时长对照：未验证**（走了简报的降级出口）。原因见 R2：batchmode 无构建 ⇒ `IPreprocessShaders` 不触发；`m_ShaderVariantLogLevel=0` ⇒ 构建日志无变体行；本任务禁改相关资产。测量步骤见 R7。
2. **`IPreprocessShaders` 在本工程 URP 14 下的实际删除行为：未验证**。适配层编译通过、纯函数有 36 条单测，但该回调只在真实 Player / AB 构建触发；本轮未做完整构建，故「实际删了哪些变体 / 是否影响 12288」均未取证。
3. **`ShaderUtil.GetVariantCount` 的 potential 数（Lit 13919720572114 / ParticlesUnlit 295194）仅作 API 可行性实证**，绝不可当作构建变体数或写进任何改前改后对照。
4. **12288 / 29 分 16 秒是 A-AND-06 的历史构建数字**，本轮未重跑构建、未重新实测该数字；本轮只核对到其来源链（always-included `ParticlesUnlit.shader`）。
5. **版本 patch 漂移**：声明 `14.0.11` vs 磁盘缓存 `14.0.12`（R1）。结论锁大版本 + 管线 + 机型，换 Unity 大版本 / 换管线 / 换机型需重新判定。
6. **规则收益未量化**：21 条规则与 URP 自带 `m_Prefilter*` / `ShaderScriptableStripper` 部分重叠；「能比 URP 现有剥离再少多少变体」未验证，**不宣称收敛**。

---

### R11 现场状态

| 项 | 状态 |
|---|---|
| 改动是否全在白名单 | ✅ `git status --porcelain` 仅 9 项：`Tools/run_urp_variant_counter.sh` + 3 个 `ZFrame/Editor/*.cs` + 1 个 `ZFrame/Tests/Editor/*.cs` + Unity 自动生成的 4 个 `.cs.meta`；全部落在 `ZFrame/Editor/**`、`ZFrame/Tests/**`、`Tools/**` 白名单内 |
| 零资产污染 | ✅ `git diff --stat -- ProjectSettings/** Assets/Settings/Pipeline/ Assets/Plugins/ZRender/zrender.dll Assets/Game/ Assets/Audio/ TASKS.md HANDOFF.md WORKLOG.md README.md DECISIONS.md` **输出为空**；未改材质 / 场景 / 游戏内容 / 原生 C++ |
| 无 commit | ✅ `git log --oneline -1` 仍 `6e97690`；未 `git add` / 未 `git commit` |
| UTF-8 无 BOM + LF | ✅ 5 个新文件逐字节 Python 复核：`bom=False cr=0 crlf=0 lf>0`（`UrpVariantRules.cs` 258 / `UrpVariantStripper.cs` 267 / `UrpVariantCounter.cs` 466 / `UrpVariantRulesTests.cs` 234 / `run_urp_variant_counter.sh` 18 行） |
| 无变异残留 | ✅ R9 sha256 一致、规则 21 条、`MUTANT` 0 命中 |
| `.meta` | ✅ Unity 自动生成，未手改 |
| Unity 残留 | ✅ `tasklist` 无 `Unity.exe`；`UnityProject/Temp/UnityLockfile` 不存在 |
| 验收结论节 | ✅ 未写、未勾选验收清单 |

## 验收清单（主控写）

- [ ] V1 变体计数工具可运行，给出实测数字与命令；拿不到则如实标受限并给可执行测量步骤
- [ ] V2 剥离器默认关闭 / 白名单保护，规则有判据（不是照抄候选表），误伤可回滚
- [ ] V3 纯函数规则 EditMode 用例真实（行号），变异有牙
- [ ] V4 改前改后同一命令输出对照；未做完整构建对照须明确写「未验证」
- [ ] V5 零资产污染（`git status` 证明）、回归数字、报告四节 + 验收亲自复做变异

## 验收结论（验收代理写）

- **验收代理**：`verifier-r9-16`（与施工 `builder-r9-16` 不同实例；本会话独立取证，施工日志数字仅作旁证并标注）
- **验收日期**：2026-11-05
- **总体结论**：**PASS with 受限（有条件通过）** —— V1–V5 无 FAIL，全部证据为本会话自跑 / 自查；按简报「允许的降级出口」交付。
- **是否可提交**：**可提交（交由主控 commit）**。受限项已如实挂账、未把「部分完成」写成「已收敛」。本代理未 `git add` / 未 `git commit`。

### 一、V1 计数工具与数字口径 —— PASS（重点项，含 14 裁决）

**自跑命令**：`bash Tools/run_urp_variant_counter.sh`（Git Bash，脚本 `Tools/run_urp_variant_counter.sh:15-18` = `-batchmode -quit -executeMethod ZFrame.Editor.UrpVariantCounter.RunFromCommandLine`）。本次输出日志 `TempLogs/verifier_r9_16_counter_20261105_002643.log`（本次新写 2026-11-05 00:27:19，125211 B），**SCRIPT_EXIT=0、Unity return code 0、日志 `error CS` 计数 0**。本次原样输出（摘录）：

```
UnityVersion   : 2022.3.62f3c1
Pipeline       : URP (manifest.json / packages-lock.json = 14.0.11；本机 PackageCache = 14.0.12)
ActiveBuild    : Android
StripperEnabled(default off): False
变体/关键字相关静态方法数: 30
  [试1] GetShaderVariantCount: 不存在于本 Unity 版本的 ShaderUtil
  [试2] GetShaderGlobalKeywords / GetShaderLocalKeywords 可行，URP/Lit 各返回 59 个关键字
  [试3] GetVariantCount 存在（nonpublic），签名 (Shader, Boolean)
        URP/Lit -> potential variant count = 13919720572114
        URP/Particles/Unlit -> potential variant count = 295194
去重关键字全集 K = 73 个
[改前] evaluated=73  stripped=0   kept=73  whitelistProtected=16
[改后] evaluated=73  stripped=14  kept=59  whitelistProtected=16   delta_stripped=+14
  改后命中(可剥)关键字 [14]: [DEBUG_DISPLAY, _ADDITIONAL_LIGHTS_VERTEX, _ADDITIONAL_LIGHT_SHADOWS,
        _CASTING_PUNCTUAL_LIGHT_SHADOW, _DBUFFER_MRT1, _DBUFFER_MRT2, _DBUFFER_MRT3,
        _FORWARD_PLUS, _LIGHT_LAYERS, _MAIN_LIGHT_SHADOWS_SCREEN, _REFLECTION_PROBE_BLENDING,
        _REFLECTION_PROBE_BOX_PROJECTION, _RENDER_PASS_ENABLED, _SCREEN_SPACE_OCCLUSION]
  必需关键字自检: keep=17 / 17 误伤=0 => OK（未剥任何必需变体）
```

- **K=73 属实**（施工报 73）：工具静态解析 4 个真实 shader 的 `#pragma` 显式 token（`UrpVariantCounter.cs:216-285`），本次日志列出 4 个 `scanned:` 相对路径与去重全集 73 项。施工 R4 记「首次解析误收注释词得 75、修正后 73」为旁证，本次实跑即为修正后口径。
- **★ 明确裁决：14 是「规则命中数」，不是「减少的变体数」**。`UrpVariantCounter.Count()`（`UrpVariantCounter.cs:182-211`）对 K=73 个**静态单关键字**逐一调用纯函数 `UrpVariantRules.Evaluate`（每次只传一个关键字），`stripped` 字段 = 被判定 Strip（命中剥离规则）的单关键字个数。改前空规则表 → 0；改后 21 条规则 → 73 个关键字中有 14 个命中（即 `K ∩ 规则表关键字 = 14`，见上列 14 个命中名），故 `stripped=0→14`、`delta_stripped=+14`。一个 shader 变体是关键字的**组合**而非单个关键字；本工具未枚举任何组合、未做真实构建、`IPreprocessShaders` 未触发，因此 14 **不是**「减少的变体数」，**不能**换算成「变体减少 14 个 / 构建变快 / 12288 已收敛」。`stripped=0→14` 的完整含义是「被规则判定为可剥的单关键字个数从 0 增到 14」，即规则命中数增加 14。
- **口径表述核对（判 FAIL 或要求改口径的触发条件）**：全仓检索 `收敛|变体减少|减少的变体|变快|构建变快|已收敛|降低.*变体|快了`，报告与源码中命中均为**否定式/要求式**（`TASK-A-R9-16.md:27`「不得写成已收敛」、`:49`「未宣称 12288 已收敛」、`:204`「14 是…规则命中数，**不是**减少的变体数」、`:270`「不宣称收敛」；`UrpVariantCounter.cs:31-34`、`:143-144` 口径声明）。代码字段名 `stripped`/`delta_stripped` 指「规则命中计数」，紧跟口径声明。**报告或代码无任何把规则命中数表述成「变体减少 / 构建变快 / 已收敛」的地方 → 不判 FAIL、无需改口径。**
- **调研结论复核（自己查证，未只信施工）**：
  - `ShaderUtil.GetShaderVariantCount` 不存在：本次反射枚举出的 30 个方法里无此名、`[试1]` 报「不存在」；本机 `C:/Program Files/Unity 2022.3.62f3c1/Editor/Data/Managed/UnityEditor.dll` 静态字符串检索 `GetShaderVariantCount` = **0 命中**，`GetVariantCount`/`GetShaderGlobalKeywords`/`GetShaderLocalKeywords` 各 ≥1 命中 → **属实**。
  - `GetVariantCount` 只给 potential 理论数：本次反射签名 `(Shader, Boolean) -> UInt64`，URP/Lit 返回 `13919720572114`（1.4e13 量级）、Particles/Unlit `295194`，日志自注「静态 potential 估计，非完整构建实际编译变体数」；施工 R2/R10.3 亦声明绝不用于改前改后对照 → **属实**。
  - `batchmode 下 IPreprocessShaders 不触发`：接口契约（只在真实 Player/AB 构建回调）+ 本次 counter 实跑 `StripperEnabled(default off): False` 且全程无 `OnProcessShader` 删除日志；施工 R10.2 已将其列为未验证 → **如实**。
  - `m_ShaderVariantLogLevel=0 无变体日志`：实测 `URP_Low/Medium/High.asset:113` 三档均 `m_ShaderVariantLogLevel: 0`、`Assets/UniversalRenderPipelineGlobalSettings.asset:33 m_ShaderVariantLogLevel: 0` → **属实**。
  - **12288 来源链**：`GraphicsSettings.asset:37` `m_AlwaysIncludedShaders` 第 8 项 guid `0406db5a14f94604a8c57ccfbc9f3b46` 实测指向 URP 包 `Shaders/Particles/ParticlesUnlit.shader.meta`，与 `TASK-A-AND-06.md:109,211` 的「12288 变体 ≈10 min」对应；该数字为历史构建值，本轮未重测。

### 二、V2 剥离器与规则判据 —— PASS with 受限

- **IPreprocessShaders 实现 / 默认关闭**：`UrpVariantStripper.cs:42` 实现 `IPreprocessShaders`；`:71` 总开关 `EditorPrefs.GetBool("ZFrame.UrpVariantStripper.Enabled", false)` 默认 **false**；`:124-128` `OnProcessShader` 首行 `if (!IsEnabled) return;`；`:181` 命中才 `compilerDataList.RemoveAt`。本次 counter 实跑确认 `StripperEnabled(default off): False` → 默认状态不删任何变体，不可能出现粉红材质。
- **白名单保护 / 回滚**：`UrpVariantRules.cs:99-107` 16 个 `ProtectedKeywords`，`:205-221` 白名单优先于剥离规则；菜单启用/关闭 + `DisabledRulesPrefKey` 逐条停用 + 命令行 `-urpStripEnabled`（`UrpVariantStripper.cs:199-265`）。回滚 = 关总开关重新构建，因不改资产、剥离只在构建期内存，零残留（施工 R6）。
- **21 条规则确实基于三档矩阵（非照抄候选表）**：逐个打开 YAML 复核，与施工 R5 一致 —— `URP_Low/Medium/High.asset`：`m_AdditionalLightsRenderingMode:1`、`m_AdditionalLightShadowsSupported:0`、`m_SupportsLightLayers:0`、`m_ReflectionProbeBlending:0`、`m_ReflectionProbeBoxProjection:0`、`m_MainLightShadowsSupported:1`、`m_ShadowCascadeCount` Low=1/Medium=2/High=2、`m_SoftShadowsSupported` 0/0/1、`m_PrefilteringModeForwardPlus:0`、`m_PrefilteringModeDeferredRendering:0`；`URP_*_Renderer.asset:18,20,56` = `m_RendererFeatures:[]` / `m_UseNativeRenderPass:0` / `m_RenderingMode:0`；`Vol_Low.asset:15 components:[]`、`Vol_Medium`=Bloom、`Vol_High`=Bloom+ColorAdjustments；`QualitySettings.asset` 三档各绑一 URP asset、`excludedTargetPlatforms:[]`；`GraphicsSettings.asset:47-50` 4 个 `*Stripping:0`、`:38 m_PreloadedShaders:[]`；`GlobalSettings.asset:27-29,33` = StripDebug=1 / StripUnusedPostProcessing=0 / StripUnusedVariants=1 / LogLevel=0。规则表 `UrpVariantRules.cs:114-159` 共 **21 条**，每条带判据；11 个关键字精确名抽查全部命中 URP 14.0.12 `Runtime/UniversalRenderPipelineCore.cs :: ShaderKeywordStrings`，非凭空拼写。
- **复核「推翻 `_MAIN_LIGHT_SHADOWS_CASCADE`」依据**：本机 URP 14.0.12 `Editor/ShaderBuildPreprocessor.cs:646-648` 原文注释 `// Cascade count can be changed at runtime, so include both of them`，且 647/648 行把 `MainLightShadows` 与 `MainLightShadowsCascade` 两个 feature 一并置入（保留，不剥）；叠加三档 `m_MainLightShadowsSupported=1`、Medium/High `m_ShadowCascadeCount=2`、运行期切档 → **推翻候选表的结论成立**；用例 `UrpVariantRulesTests.cs:215-225 MainLightShadowsCascade_NotInStripRules` 锁死。
- **「17/17 误伤 0」可复核但须限定口径**：本次 counter 实跑输出 `keep=17/17 误伤=0 => OK`。复核实现（`UrpVariantCounter.cs:64-83,146-167`）为把 17 个**必需关键字**逐一以单元素集合传入 `Evaluate`，确认都不会被 Strip —— 验证的是「这 17 个关键字名不在 21 条规则表内」，属**关键字名级**自检，**并非**「17 个必需变体组合在真实构建中未被误伤」。白名单仅覆盖 16 个 Volume/UberPost 关键字；三档 URP 必需光照关键字靠「不在规则表内」天然 Keep，组合层（同时含可剥关键字 + 非白名单必需关键字）仍会命中规则被剥（设计上剥的是「含不需要维度的组合」，判据成立），但**组合级与真实构建删除行为未动态验证**（施工 R10.2 已列未验证）。→ 静态可复核 PASS，动态计入受限。

### 三、V3 纯函数与测试 —— PASS（变异复做见第四节）

- **自跑 EditMode 基线**：`& .\Tools\run_tests.ps1 -Segments editmode`（PowerShell 工具，跑前清 8 个代理变量、无 `-quit`）→ **916 / 916 / 0，exit 0**，本次新写 `TempLogs/runtests_editmode.xml`（2026-11-05 00:29:43），归档 `TempLogs/verifier_r9_16_editmode_baseline_20261105_003008.xml`。施工报 916，≥880 达标（+36 条新增用例，880→916 只增不减）。
- **36 条新增用例是真断言**：`UrpVariantRulesTests.cs:16-233`，用例数 = 15 个独立 `[Test]` + `RequiredKeyword_Kept`(10 TestCase, `:58-67`) + `VolumeProtectedKeyword_Kept`(7, `:75-81`) + `KeywordMatchIsCaseSensitive`(4, `:122-125`) = **36**；每条含 `Assert.IsTrue/IsFalse/AreEqual/AreNotEqual/Greater` 或 `StringAssert.Contains` 真断言。覆盖：命中（`:25-53`）、不命中/三档必需与 Volume 白名单（`:58-98`）、边界空关键字与 null 元素（`:103-120`）、大小写 `Ordinal`（`:122-132`，`UrpVariantRules.cs:215,244`）、平台分支（`:135-146`）、Development/ReleaseBuildOnly（`:149-163`）、可配置逐条禁用/自定义规则（`:168-185`）、治理与候选表推翻（`:190-232`）。

### 四、定向变异复做（验收代理亲自，「修前必红」）—— PASS

只改本任务新增的**一个**源码文件 `UrpVariantRules.cs`（未触碰测试断言 / 用例体，测试文件 sha256 `12aba75a…` 全程未变）：

| 步 | 动作 | 本次实跑结果 | 本次新写 xml / 证据 |
|---|---|---|---|
| 基线绿 | 规则表 21 条，sha256 `d129bcd4…` | **916 / 916 / 0，exit 0** | `TempLogs/runtests_editmode.xml` @00:29:43 |
| 变异 | 删除 `_ADDITIONAL_LIGHTS_VERTEX` 一条（21→20，sha256 `c537e928…`） | — | 规则数 20、靶心 0 命中 |
| **必红** | `-Segments editmode` | **916 / 912 / 4，段 FAIL / 脚本 exit 1（Unity exit=2）** | `TempLogs/runtests_editmode.xml` @00:31:51，归档 `verifier_r9_16_editmode_mutant_20261105_003225.xml` |
| 还原 | `cp` 备份回写 + `cmp` | **cmp 零差异**；sha256 回 `d129bcd4…`；规则回 21、靶心 `:116` 存在、`MUTANT` 0 命中；`git diff --name-only` 仅 `Docs/Logs/TASK-A-R9-16.md`（变异目标为未跟踪文件，git diff 天然零），`git status` 无新增异常项 | `/tmp/verifier_r9_16_UrpVariantRules.cs.bak`（仓库外） |
| 复绿 | `-Segments editmode,playmode,arch,config,naming` | **EditMode 916/916/0（result=Passed）** | `TempLogs/runtests_editmode.xml` @00:37:40 |

**变红用例 4 条**（从本次红 xml 用 `xml.etree.ElementTree` 解析 `test-case[result=Failed]`，与施工 R9 一致）：
```
1. ZFrame.Editor.Tests.UrpVariantRulesTests.AdditionalLightsVertex_IsStripped
2. ZFrame.Editor.Tests.UrpVariantRulesTests.NonReleaseOnlyRule_StillStrips_InDevelopmentBuild
3. ZFrame.Editor.Tests.UrpVariantRulesTests.NullElementInKeywords_DoesNotThrow
4. ZFrame.Editor.Tests.UrpVariantRulesTests.StripRulesCount_Locked
```
前后数字：`916/916/0 →（删一条规则）→ 916/912/4 →（字节级还原）→ 916/916/0`。**用例有牙、还原零残留**。

### 五、V4 改前改后对照与未验证项 —— PASS

- **同命令改前/改后可复核**：`run_urp_variant_counter.sh` → `RunFromCommandLine` 在**同一次运行**内对同一输入 K=73 分别用空规则表与 21 条规则评估（`UrpVariantCounter.cs:130-144`），本代理重跑得到与施工 R7 完全一致的 `改前 stripped=0 / 改后 stripped=14 / delta=+14`，可复核。
- **四项「未验证」如实列出**：报告 `R10`（`:265-270`）逐条列明 —— ①`真实构建变体数 / 构建时长对照：未验证`（`:265`，R7:204 同）；②`IPreprocessShaders 实际删除行为：未验证`（`:266`）；③`GetVariantCount potential 数仅作 API 实证、不得写入对照`（`:267`）；④`12288 / 29分16秒为 A-AND-06 历史数字、本轮未重测`（`:268`）；⑥`规则收益未量化、不宣称收敛`（`:270`）。**未做完整构建对照**在 `:183,204` 明确声明。
- **严禁把「部分完成」写成「已收敛」**：报告 `:49`「未宣称 12288 已收敛」、`:204`「14…不是减少的变体数」、`:270`「不宣称收敛」、`:263` 标题「不写成已收敛」；标题「URP 变体收敛」是任务名，结论走降级出口、未宣称已收敛 → 符合。
- **R7 下次 Windows/Android 测量步骤可执行**：`:206-213` 共 7 步（开变体日志并精确还原单文件、清代理+QUIET+单实例、关态构建、开态同命令同机同档构建、变体数+耗时对照口径、误伤检查、精确处理具体文件不整仓 checkout）；引用的 `Tools/build_android.ps1`、`Tools/build_player.ps1`、`Tools/check_machine_quiet.ps1` 均实测存在，步骤可照做。

### 六、V5 零资产污染与回归 —— PASS

- **禁止路径 0 命中 / 改动全在白名单**：`git diff --name-only -- Native Assets/Game Assets/Audio 'Assets/Settings/Pipeline/*.asset' ProjectSettings TASKS.md HANDOFF.md WORKLOG.md README.md DECISIONS.md` → **空**；`git status --porcelain` 逐行核对禁止路径 → 0 命中。10 项改动全在 `ZFrame/Editor/**`、`ZFrame/Tests/Editor/**`、`Tools/**`、`Docs/Logs/TASK-A-R9-16.md`。
  - 口径小差异（如实登记）：施工 R11 称「仅 9 项」，实测进场/收口均为 **10 项**（1 个 `M Docs/Logs/TASK-A-R9-16.md` + 9 个 `??` = 4 `.cs` + 4 `.cs.meta` + 1 `.sh`），差异是施工漏数任务日志自身的 M，不影响判定。
- **自跑 PlayMode + 三校验器（本次新写证据）**：`& .\Tools\run_tests.ps1 -Segments editmode,playmode,arch,config,naming` → `RESULT: ALL PASS (5 passed, 0 skipped, 0 failed)`：
  - PlayMode **48 / 48 / 0，exit 0**（`TempLogs/runtests_playmode.xml` @2026-11-05 00:44:06，result=Passed，归档 `verifier_r9_16_playmode_restored_20261105_004659.xml`）
  - arch / config / naming **各 exit 0**；`TempLogs/{arch,config,name}_report.txt`（本次新写 00:44:35 / 00:45:04 / 00:45:33）均「违规数: 0 / 结果: PASS」
  - EditMode 复绿段 **916/916/0**（`TempLogs/runtests_editmode.xml` @00:37:40，result=Passed，归档 `verifier_r9_16_editmode_restored_20261105_004659.xml`）
- **回归数字核对**：EditMode **916 ≥ 880**、PlayMode **48 ≥ 48**；native **311 / 311 passed、776716 / 776716 assertions、Status SUCCESS、EXIT=0**（直跑 `Native/build/bin/Release/zrender_test.exe`，**未重建**，避免 `zrender.dll` 时间戳噪声）→ 311 不降。
- **新增文件 UTF-8 无 BOM + LF（字节级全查）**：9 个新文件（5 个 `.cs`/`.sh` + 4 个 `.meta`）逐字节 `python` 复核均 `bom=False cr=0 crlf=0 lf>0`（行数 `UrpVariantRules.cs 258 / UrpVariantStripper.cs 267 / UrpVariantCounter.cs 466 / UrpVariantRulesTests.cs 234 / run_urp_variant_counter.sh 18`）。
- **`.meta` 未被手改**：4 个 `.cs.meta` 均为标准 Unity `MonoImporter` 结构（fileFormatVersion:2 / guid / serializedVersion:2），无手改痕迹；为 Unity 自动生成。
- **报告四节齐全**：`## 任务简报（主控写）`(`:5`)、`## 施工报告（施工代理写）`(`:47`)、`## 验收清单（主控写）`(`:287`)、`## 验收结论（验收代理写）`(`:295`) —— 四节齐全；验收清单 5 条未勾选、由主控处置，验收未代勾。
- **PlayMode 测试副作用精确还原**：跑 PlayMode 后 `Docs/Parity/crowd_r9_15_off.png`、`crowd_r9_15_on.png` 被像素用例改写（进场时干净、非本任务改动，施工 R8 已记录此副作用）。验收**仅针对这两个具体文件** `git checkout --` 精确还原（非整仓 checkout、未碰 `zrender.dll`），`git status` 回到进场 10 项。

### 七、受限 / 未验证清单（必须挂账，不得视为已验证）

1. **真实构建变体数 / 构建时长对照：未验证**（batchmode 无构建；报告 R10.1 / R7:204 已列）。
2. **`IPreprocessShaders` 在本工程 URP 14 下的实际删除行为 / 是否影响 12288：未验证**（适配层编译通过、纯函数 36 条单测，但未做完整构建；R10.2）。
3. **12288 变体 / 29 分 16 秒**：A-AND-06 历史构建数字，本轮未重测，只核对到来源链（R10.4）。
4. **规则收益未量化**：21 条规则与 URP 自带 `m_Prefilter*` / `ShaderScriptableStripper` 部分重叠，不宣称收敛（R10.6）。
5. **必需变体组合级误伤未动态验证**：17/17 为「关键字名不在规则表内」级自检，白名单仅覆盖 16 个 Volume 关键字。
6. **版本 patch 漂移**：声明 `14.0.11` vs 磁盘 PackageCache `14.0.12`（R1/R10.5）；结论锁 Unity 2022.3.62f3c1 + URP + 780M 核显 / Android arm64，换大版本/管线/机型需重判。
7. **native 段未重跑构建**：仅直跑已有 `zrender_test.exe` 取证（311/776716），未触发 `build_native.ps1`。
8. **EditMode 复绿段僵尸噪声**：出现「xml 已写但 Unity 5 分钟未退」→ 脚本按纪律 `taskkill`（`PASS (taskkilled zombie)`，Unity exit=1 是僵尸被杀），证据取本次新写 results.xml（916/916/0 result=Passed）；基线跑与变异红跑均正常退出。属交接档案 §3 / 施工 R8 已知环境噪声，非回归。

### 八、现场状态

| 项 | 状态 |
|---|---|
| HEAD | `6e9769035433e77c70df37034b5c86e3f8937b65`（`6e97690`），**未变、无新 commit、未 `git add`** |
| 工作区 | 10 项（1 `M` 任务日志 + 9 `??`），全部在白名单，与进场一致 |
| URP/Quality/ProjectSettings 改写 | `URP_*.asset` / `QualitySettings.asset` / `ProjectSettings.asset` / `GraphicsSettings.asset` / `GlobalSettings.asset` / `zrender.dll` 均 **0 命中**（无需还原） |
| Unity 残留 | `tasklist` 无 `Unity.exe`；`UnityProject/Temp/UnityLockfile` 不存在 |
| 变异残留 | cmp 零差异、sha256 `d129bcd4…`、规则 21、靶心存在、`MUTANT` 0、`git diff --name-only` 零残留 |
| 证据 | 全部为本次会话自跑/自查；施工的旧 xml 已 `mv` 归档为 `verifier_r9_16_builder_*`，本次新写 xml/日志均带 `verifier_r9_16` 前缀，未复用旧 xml |

**证据链文件**：`TempLogs/verifier_r9_16_counter_20261105_002643.log`、`verifier_r9_16_editmode_baseline_20261105_003008.xml`、`verifier_r9_16_editmode_mutant_20261105_003225.xml`、`verifier_r9_16_editmode_restored_20261105_004659.xml`、`verifier_r9_16_playmode_restored_20261105_004659.xml`、`TempLogs/{arch,config,name}_report.txt`、`TestReport.txt`（`ALL PASS (5 passed, 0 failed)`）。

