# TASK-A-R9-18 —— 打开 URP 自带后处理变体剥离（`m_StripUnusedPostProcessingVariants`）

> A-R9-17 的结论：自研 21 条规则 0 命中（结构性冗余），但顺带发现 URP 自带的
> `m_StripUnusedPostProcessingVariants` 一直是 `0`（关）。本任务测这个开关有没有真收益。
> **禁止 git commit**。

## 基线（A-R9-17 已实测，无需重跑关态）

| 项 | 值 |
|---|---|
| 编译变体数 | **7830** |
| 编译Σ | 210.00 s |
| Unity 构建 | 284.9 s |
| 档位 | StandaloneWindows64 / Release / d3d11 / 冷 ShaderCache |
| Unity / URP | 2022.3.62f3c1 / 14.0.12 |

## 要改的（只一处）

`UnityProject/Assets/UniversalRenderPipelineGlobalSettings.asset:28`
```
m_StripUnusedPostProcessingVariants: 0  →  1
```
**只改这一行**，别动 `m_StripUnusedVariants`（已是 1）和 `m_StripUnusedLODCrossFadeVariants`（已是 1）。

## 步骤

1. **开变体日志**（数变体必需）：同文件 `:33` `m_ShaderVariantLogLevel: 0 → 1`，事后还原
2. 改 `m_StripUnusedPostProcessingVariants: 1`
3. QUIET 检查 + 清代理 + 单实例（查 Unity.exe + 删 Temp/UnityLockfile）
4. **冷 ShaderCache**：`mv` 掉 `Library/ShaderCache` + `ShaderCache.db`（否则会走增量，样本作废——A-R9-17 踩过）
5. **构建一次**：`Tools/build_player.ps1 -TimeoutSec 1800`（同一命令同档位）
6. **记四个量**（与基线同法）：
   - 编译变体数 = 所有 `compiled N variants` 的 N 求和
   - 编译Σ = 上述行的秒数求和
   - Unity 构建 = 日志 `##utp` 的 `duration`
   - 墙钟 = Stopwatch
7. **误伤检查**（本任务关键，剥后处理变体可能真删掉要用的）
   - 跑构建出的 exe，看：Bloom 是否还在 / 色调映射 / 阴影 / 有没有粉红材质 / 黑屏
   - 用现有截图对比基线（`Docs/Parity/` 下有历史图，或自己跑 `-zrender` 抓一张）
8. **还原**：`git checkout -- UniversalRenderPipelineGlobalSettings.asset`（单文件），
   `Library/ShaderCache` 不必还原（可再生）；贴 `git status`

## 判据

- 给出**变体数 / 编译Σ / Unity 构建 / 墙钟**四个量 + 与基线的差值
- 明确回答：打开后**变体少了多少**？是否值得？
- **误伤结论**：画面有没有变化（附证据：截图对比或像素差）
- 如果变体没降或降得很少，如实说（可能这个项目后处理变体本来就少）
- 数字来自日志，不估算

## 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- 不改剥离器规则；不改游戏内容
- 资产改动**精确到单文件**还原，不整仓 checkout；`zrender.dll` 噪声不丢弃
- 说「变快了」必须附「怎么测的」，两个数字同源同法

## 环境纪律（每条真实踩过）

- 构建前 Bash `unset http_proxy https_proxy all_proxy`；**Bash 侧禁止转调 powershell** → 用 **PowerShell 工具**；PowerShell 清 8 个代理变量
- Unity 批处理**单实例**；**勿加 `-quit`**；僵死则 `taskkill //PID //F`
- **GUI Editor 加 `-force-d3d12` 会挂起，别加**
- PS 5.1 对原生 exe 加 `2>&1` 会把退出码变 1（假失败）
- **每次测量前清 ShaderCache**，否则样本作废
- 清理用 `mv` 归档 `TempLogs/archive_pre_audit/`

---

## 施工报告（施工代理写）

**结论：这个开关有量，但量在「后处理里项目没用的那几支」上，不在本项目在用的 Bloom/色调映射上。**
变体 **7830 → 7096（−734，−9.4%）**，Unity 自报构建耗时 **284.9 → 238.5 s（−46.4 s，−16.3%）**，
包体 `Player 81.95 → 81.78 MB`；画面在**像素级与基线一致（差异低于同一次运行内的动画噪声）**，
Bloom / 色调映射 / 阴影 / 材质全部正常，无粉红、无黑屏。

一句话取舍：**对「编译变体数 / 打包体积 / 构建内耗时」有价值（约 −9% / −175 KB / −46 s），
对墙钟没有可见收益（+55.8 s，落在基线噪声带内，不宣称变快）。**

### R1 四个量（与基线同源同法）

同一台机器、同一入口 `Tools/build_player.ps1 -TimeoutSec 1800`、同档位
StandaloneWindows64 / Release / 非 Development / 冷 ShaderCache（每次构建前 `mv` 掉
`Library/ShaderCache` + `ShaderCache.db`，见 R6）。

| 量 | 基线（关，A-R9-17 的 F 次） | 本次（开） | 差值 | 口径 |
|---|---|---|---|---|
| 编译变体数 | **7830** | **7096** | **−734（−9.37%）** | 所有 `finished in X seconds ... compiled N variants` 的 N 求和 |
| 编译Σ | **210.00 s** | **193.78 s** | **−16.22 s（−7.72%）** | 同上行的 X 求和（407 / 369 行） |
| Unity 构建 | **284.9 s**（284 919 ms） | **238.5 s**（238 471 ms） | **−46.45 s（−16.30%）** | 日志 `##utp:{"type":"PlayerBuildInfo"...}` 末尾 `duration` |
| 命令墙钟 | **343.6 s** | **399.4 s** | **+55.8 s** | PowerShell `Stopwatch` 包住整条脚本（含 20 s 冒烟 + 报告） |

- 块数也变了（同一个解析器，`Compiling shader "X" pass "Y"` 计数）：**419 块 → 400 块（−19）**；
  其中「scriptable stripping 后 = 0、因此根本不产生 `compiled` 行」的块 **12 → 31（+19）**。
  于是 `compiled` 行 **407 → 369**（= 419−12 → 400−31；−38 = 19 块整体消失 + 19 块变成 0 变体、不再出 `compiled` 行）。
- 墙钟 **+55.8 s 不能当收益/损失**：A-R9-17 同一关态连跑三次的墙钟是 328.3 / 343.6 / 409.1 s
  （跨度 80.8 s），本次 +55.8 s 完全在噪声带内，且方向与 Unity 内 `duration` 相反。
- `Player 81.95 → 81.78 MB`（166 文件不变），差额定位在
  `ZFrame_Data/globalgamemanagers.assets`：**1 709 948 → 1 530 556 B（−179 392 B ≈ −175 KB）**；
  `globalgamemanagers.assets.resS` 大小不变（5699 808 B），说明少掉的是变体索引/元数据，不是 shader 字节码本身。

### R2 少的 734 个变体是什么（逐 shader，来自两份日志的同一个解析器）

按 `Compiling shader "X" pass "Y"` 切块、取 `After scriptable stripping` 与 `compiled N variants`：

| Shader | 关（compiled） | 开（compiled） | 差值 | 说明 |
|---|---|---|---|---|
| `Hidden/Universal Render Pipeline/UberPost` | 721 | **31** | **−690** | 通用后处理合成；项目实际用到的特性组合保留 31 支 |
| `Hidden/Universal Render Pipeline/BokehDepthOfField` | 20 | **0** | **−20** | 项目未使用（Vol_* 里没有 DOF） |
| `Hidden/Universal Render Pipeline/GaussianDepthOfField` | 14 | **0** | **−14** | 同上 |
| `Hidden/Universal Render Pipeline/CameraMotionBlur` | 6 | **0** | **−6** | 同上 |
| `Hidden/Universal Render Pipeline/PaniniProjection` | 4 | **0** | **−4** | 同上 |
| 其余 379 块（非上述 5 个 shader） | — | — | **0** | `After scriptable stripping` / `compiled` 逐块相同 |

**没有被动的（关键）**：
`Hidden/Universal Render Pipeline/Bloom` **24 → 24**、`FinalPost` **98 → 98**、`LutBuilder` **8 → 8**、
`Particles/Unlit` 6144 → 6144。即：**本项目 Volume 里真正在用的 Bloom / 色调映射 / LUT 调色，一支都没删。**

机制侧对得上 URP 14.0.12 源码：`ShaderBuildPreprocessor.GatherShaderFeatures()`
在开关打开时走 `GetSupportedFeaturesFromVolumes()`——扫描 `Assets` 下所有 `t:VolumeProfile`，
只保留其中出现过的后处理特性（`Library/PackageCache/com.unity.render-pipelines.universal@14.0.12/Editor/ShaderBuildPreprocessor.cs:246,310`）。
本项目 `Settings/Pipeline/Vol_High.asset` / `Vol_Medium.asset` 有 Bloom、没有 DOF/MotionBlur/Panini，
所以删的正好是「没用的那几支」。

### R3 误伤检查（画面证据，不是「看起来正常」）

**怎么取的图**：`TempLogs/TASK-A-R9-18/capture2.ps1`——启动构建出的 exe（`-screen-width 1280 -screen-height 720
-screen-fullscreen 0`，URP 路径，不加 `-zrender`），等 18 s，按颜色定位菜单第一个蓝色按钮（开始）→
鼠标点击进入 `SCN_Game` → 等 12 s → `PrintWindow(PW_RENDERFULLCONTENT)` 抓两帧（间隔 3 s）。
关态基线图来自**改造前**的构建产物（`Build/build_report.txt` 记的是 21:50 那次 off 构建，
`ZFrame_Data/*` mtime 21:49），开态图来自本次构建（23:10）。

**对比口径**：只比 1280×720 的客户区（裁掉窗口标题栏/边框），逐像素取三通道**最大**差值
（`ImageChops.lighter(r,g,b)`），另算 32 bin 亮度直方图与 8×8 块均值（结构层，抗单像素动画）。

| 对比 | mean max\|d\| | >8 像素占比 | 8×8 块亮度差(mean/max) | 亮度直方图 L1 |
|---|---|---|---|---|
| 关态**同一次运行内**两帧（噪声底） | 2.2395 | **2.979%** | 0.703 / 14.0 | 0.00716 |
| 开态**同一次运行内**两帧（噪声底） | 2.2137 | **2.747%** | 0.766 / 15.0 | 0.00880 |
| **关 vs 开（跨构建）** | **0.6938** | **1.722%** | **0.203 / 2.0** | 0.01095 |
| 菜单 关 vs 开 | 0.0835 | **0.000%**（max\|d\|=7） | 0.078 / 2.0 | 0.02329 |

读法：
- **跨构建的差异比同一次运行内「守卫移动 + 尘埃粒子」造成的噪声还小**（0.69 vs 2.24；1.72% vs 2.98%；
  块均值 0.203 vs 0.703，块最大 2 vs 14）。也就是说画面差异全部落在动画相位上，**没有渲染层变化**。
- 菜单（纯 UI）跨构建 **最大像素差 7，超过 8 的像素 0 个**。
- 差值图 `on_game_a_diffx8_off_vs_on.png`（×8 放大）除**尘埃粒子方块 + 守卫轮廓**外全黑：
  天空、地面、阴影区、几何体本体差值为 0 ⇒ 没有全局调色/色调映射偏移。
- **粉红/品红材质计数 = 0/921 600**（关、开、两帧、菜单全部为 0）；近黑像素 15 360 恒定（是 UI 黑底，
  不是黑屏）；平均 RGB 关 (154.512,169.026,183.378) vs 开 (154.535,169.066,183.436)，逐通道差 < 0.06。
- **关于 Bloom 的诚实说明**：本场景里 Bloom 没有「肉眼一眼可见」的强光晕，所以不靠肉眼断言，
  而靠两条硬证据：① `Bloom` shader 变体 **24 → 24** 一支没删（R2），② 差值图除动画元素外全黑。
  若将来要验 Bloom 的**可见性**，应另用 `PostFxPixelEvidencePlayModeTests` 那种带光晕的探针场景，不属本任务。
- 人工复核图见 `on_game_a.png`：天空渐变、地面阴影、角色材质、HUD 全部正常，与 `base_off2_game_a.png` 同景。

证据文件（`TempLogs/TASK-A-R9-18/`）：
`base_off2_menu.png` / `base_off2_game_a.png` / `base_off2_game_b.png`（关态基线）、
`on_menu.png` / `on_game_a.png` / `on_game_b.png`（开态）、
`*_diffx8_*.png`（差值图 ×8 放大）、`noise_off` / `noise_on` / `off_vs_on` / `menu_off_vs_on` 四组统计
（由 `compare.py` 生成，可复跑：`python compare.py base_off2_game_a.png on_game_a.png off_vs_on`）。

### R4 构建步骤分解（解释 −46.4 s 从哪来）

同一条 `##utp` 里的分步（ms，关 → 开）：

| 步骤 | 关 | 开 | 差 |
|---|---|---|---|
| Writing asset files（含着色器编译） | 249 967 | 196 381 | **−53 586** |
| Building Resources/unity_builtin_extra | 18 424 | 8 543 | −9 881 |
| Build scripts DLLs | 4 032 | 7 363 | +3 331 |
| Postprocess built player | 4 695 | 14 957 | +10 262 |
| 其余小步（ProducePlayerScriptAssemblies / Building scenes / … 合计） | 7 801 | 11 227 | +3 426 |
| **合计 duration** | **284 919** | **238 471** | **−46 448** |

即：省的主要是「写资产 + 编译」这一段（−53.6 s），被 postprocess（+10.3 s）/脚本 DLL（+3.3 s）/
准备脚本程序集等其余步骤（+3.4 s）这类与变体无关的波动吃掉一部分。分步数据同源于两份 `##utp` 行，不是估算。

### R5 环境与命令（怎么测的）

```
# 1) 只改两行（git diff 见 R6），勿动 m_StripUnusedVariants / m_StripUnusedLODCrossFadeVariants
UnityProject/Assets/UniversalRenderPipelineGlobalSettings.asset:28  0 -> 1
UnityProject/Assets/UniversalRenderPipelineGlobalSettings.asset:33  0 -> 1   (数变体必需)
# 2) QUIET 检查：Tools/check_machine_quiet.ps1 -SampleSeconds 5  ->  VERDICT: QUIET
# 3) 单实例：无 Unity.exe；Temp/UnityLockfile 不存在
# 4) 冷缓存：mv Library/ShaderCache -> TempLogs/archive_pre_audit/r9_18_shadercache_230403
#            mv Library/ShaderCache.db -> .../r9_18_shadercache_db_230403
# 5) PS 侧清 http_proxy/https_proxy/all_proxy/no_proxy（含大小写变体，实测原有 4 个：
#    HTTP_PROXY,HTTPS_PROXY,https_proxy,http_proxy；清理后 0 个）
# 6) powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_player.ps1 -TimeoutSec 1800
#    Stopwatch 包住整条命令 -> 399.4 s；Unity exit 0；冒烟 PASS（alive 20s + 三条启动链标记）
```

Unity 由 `build_player.ps1` 内部用 Git Bash 拉起（无 `-quit`）；构建**前**的 QUIET 判定为 `VERDICT: QUIET`
（构建期间未再采样，这一项是前置护栏，不是构建期监测）。
日志：`TempLogs/TASK-A-R9-18/r9_18_build_on_unity.log`（454 638 B，369 条 `compiled` 行）。

### R6 还原证明（只动该动的）

- **URP 资产**：`git checkout -- UnityProject/Assets/UniversalRenderPipelineGlobalSettings.asset`（单文件），
  现在 `:28 m_StripUnusedPostProcessingVariants: 0`、`:33 m_ShaderVariantLogLevel: 0`。
- **构建副作用**（非本任务改动，但会脏树，已逐个单文件还原）：
  `Tools/version.json`（build 32→33、versionCode 11→12）、
  `UnityProject/ProjectSettings/ProjectSettings.asset`（bundleVersion 0.1.0.32→0.1.0.33、
  AndroidBundleVersionCode 10→11）均 `git checkout --` 单文件回退。
- **`git status --short` = 空**（写本报告之前实测；本报告落盘后应只有本文件一条 `M`），HEAD 未动，**未 commit**。
- 未整仓 checkout；`UnityProject/Assets/Plugins/ZRender/zrender.dll` 未动（git status 无它）。
- 进程/锁：无 `Unity.exe`、无 `ZFrame.exe`、无 `UnityProject/Temp/UnityLockfile`。
- `Library/ShaderCache` 由本次构建重新生成（可再生，按简报不还原）；关态旧缓存归档在
  `TempLogs/archive_pre_audit/r9_18_shadercache_230403`。
- **注意**：`Build/Windows64/` 现在躺着的是**开态**产物（不入库，简报未要求还原）；仓库源码侧已全部还原。

### R7 结论与建议

1. **变体少了 734（−9.37%）**，全部来自项目未使用的后处理（DOF / MotionBlur / Panini）与 UberPost 的未用组合；
   Bloom/FinalPost/LUT 一支没删。
2. **值得开**：编译量 −7.7%、Unity 构建 −46.4 s（−16.3%）、包体 −175 KB，而画面零差异。
   风险边界（写清，别误读）：URP 的机制是**构建期扫描 `Assets` 下所有 `t:VolumeProfile`**，
   所以以后在 Volume 里新加 DOF / 运动模糊 / Panini，会被自动纳入、不会丢变体；
   真正的风险只在于「后处理特性不是由 VolumeProfile 声明，而是运行时代码直接开 keyword / 自建
   ScriptableRendererFeature 且不走 Volume」的用法——本工程当前没有这种用法（`PostFx` 走 `Vol_*` 资产）。
3. **只能宣称「Unity 自报构建耗时」变短**：`duration` 284.9 → 238.5 s；**墙钟 +55.8 s 不作数**
   （关态自身三次 328.3 / 343.6 / 409.1 s，跨度 80.8 s，本次落在带内）。
4. 若要继续压：下一个候选是大头 `Particles/Unlit` 6144 变体（本项目里由 URP 自带 `m_StripUnusedVariants`
   已处理过一轮，A-R9-17 的 21 条自研规则对它 0 命中），需另开任务，不在本任务范围。

---

## 验收清单（供验收代理）

- [x] V1 四个量与基线同源同法
- [x] V2 变体数降了多少，是否值得
- [x] V3 误伤检查有画面证据（不是「看起来正常」）
- [x] V4 资产精确还原
- [x] V5 未改规则；未 commit；现场干净

## 验收结论（验收代理写）

**判决：PASS with 受限**（数字全部独立复算通过；残留限制是「只在 Windows/D3D11 验过」+「画面是跨构建对比而非同帧 A/B」+「30 支的逐支覆盖未在运行时枚举」）。
**一句话：该开——前提是后处理特性一律由 `Assets` 下的 VolumeProfile 声明；Android 与「运行时动态加后处理」两条路径未验，故先只在 Windows 档位打开。**

### A1 变体数与归属（我自己解析两份日志，未重跑构建）

解析器 `TempLogs/TASK-A-R9-18/audit_recount.py`（按 `Compiling shader "X" pass "Y"` 切块，取 `finished in ... compiled N variants`）。
输入：关 = `TempLogs/r9_17_build_off3_unity.log`（A-R9-17 的 F 次，21:50），开 = `TempLogs/TASK-A-R9-18/r9_18_build_on_unity.log`（23:10）。

| 量 | 关（off3） | 开 | 差 | 施工自述 | 复核 |
|---|---|---|---|---|---|
| 块数 | 419 | 400 | −19 | −19 | ✅ |
| 有 `compiled` 行 | 407 | 369 | −38 | −38 | ✅ |
| **Σ compiled N** | **7830** | **7096** | **−734（−9.37%）** | 同 | ✅ **相同** |
| Σ 秒 | 210.00 | 193.78 | −16.22（−7.72%） | 同 | ✅ **相同** |
| `##utp` duration | 284 919 ms | 238 471 ms | −46 448 ms（−16.30%） | 同 | ✅ **相同** |

五分项（我逐块求和，不是引用施工表）：

| Shader | 关 | 开 | 差 |
|---|---|---|---|
| UberPost | 721（vp 1 + fp 720） | **31（vp 1 + fp 30）** | −690 |
| BokehDepthOfField | 20 | 0 | −20 |
| GaussianDepthOfField | 14 | 0 | −14 |
| CameraMotionBlur | 6 | 0 | −6 |
| PaniniProjection | 4 | 0 | −4 |
| **5 项合计** | | | **−734** |

**全表校验**：两侧各 **81** 个 shader，除上述 5 个外，另外 **76** 个 shader 的 compiled 逐个 `+0` — 734 全部被五分项解释，无第三个来源。
**独立交叉核对**：日志里 `d3d11 (total internal programs: X, unique: Y)` 逐 shader 求和 = **14 132 → 13 398（−734）**，与 compiled 差值一致；
（14 132 = 7830 编译 + 6302 同一次构建内第二阶段命中本地缓存的重复计数；两侧命中数都是 6302，故差值不受影响。）

编译耗时归因（同法逐 shader 求和）：UberPost 16.59→0.56（**−16.03 s**）、Bokeh 1.37→0、Gaussian 0.65→0、MotionBlur 0.42→0、Panini 0.13→0；其余小波动 +2.38 s 抵消后 **−16.22 s**。收益几乎全来自 UberPost。

### A2 「未动」的三个（本任务最关键的安全断言）

| Shader | 关 | 开 | 结论 |
|---|---|---|---|
| Bloom | 24（8 块：Prefilter 4/4、Blur H 2/2、Blur V 2/2、Upsample 4/4） | 24（逐块同数） | ✅ 属实 |
| FinalPost | 98（1+48+1+48） | 98（1+48+1+48） | ✅ 属实 |
| LutBuilder | Ldr 2 + Hdr 6 = **8** | 2 + 6 = **8** | ✅ 属实 |
| Particles/Unlit | **6268**（主块 ForwardLit(fp) 6144） | **6268**（主块 6144） | ✅ 属实 |

说明一处口径：施工表写 `Particles/Unlit 6144→6144`，指的是该 shader 的主编译块；整个 shader 的 compiled 合计是 6268→6268。两种读法都是「一支没删」，不算夸大。

### A3 UberPost 721→31 会不会删掉在用的（判断，含依据与不确定）

**结论：就本项目当前状态，31 支够用（静态判断，非运行时证）；但不保证未来在代码里动态开后处理时仍够。**

依据（源码 + 资产，均为静态取证）：
1. `ShaderScriptableStripper.StripVolumeFeatures_UberPostShader()`（`.../universal@14.0.12/Editor/ShaderScriptableStripper.cs:260-293`）**只删 9 个 keyword 组中的 6 个**：`_DISTORTION`(LensDistortion)、`_CHROMATIC_ABERRATION`、Bloom 四支、`_TONEMAP_ACES`、`_TONEMAP_NEUTRAL`、`_FILM_GRAIN`；**其余 keyword（HDR/PC 编码、gamma/sRGB 转换、Dithering、ScreenCoordOverride）一支不动**。因此「删掉的」在机制上被限定为「Volume 没声明的后处理特性」。
2. 工程里**全部** VolumeProfile 资产只有 3 个（按 VolumeProfile 脚本 GUID 全盘搜）：`Vol_High`（Bloom active=1 + ColorAdjustments active=1）、`Vol_Medium`（Bloom）、`Vol_Low`（空）。没有 Tonemapping / DOF / MotionBlur / Panini / FilmGrain / ChromaticAberration / ColorLookup / LensDistortion。
3. 运行时不构造 Volume：`PipelineManager` 用 `GradeVolume.sharedProfile` 挂**资产**（`PipelineCrossfadeTests.cs:121` 等），没有 `new VolumeProfile()` / 直接开 keyword 的代码；唯一的 `ScriptableObject.CreateInstance<VolumeProfile>()` 在 `View/Editor/PipelineSetup.cs:203`，是 Editor 期生成资产（落在 `Assets` 下，会被构建期扫描到）。
4. 数量对得上：`Full variant space 23040 → After settings filtering 960 → After scriptable 30`；剩 30 与「Bloom 5 支（off/LQ/HQ/LQ_DIRT/HQ_DIRT）× 分级 2 支（off/_HDR_GRADING）× gamma 3 支」的乘积一致。**（组合分解是推断，日志不逐支枚举 keyword；但被删 keyword 的集合是源码确定的。）**
5. 运行侧无缺 shader 迹象：开态 `player_on.log` 中 `error|pink|magenta|subshader` 命中 **0**；截图无粉红/黑屏（见 A4）。

不确定/边界（必须写明）：
- 若将来把后处理做成**运行时**加组件（非 VolumeProfile 资产）或代码直开 `_TONEMAP_ACES` 等 keyword，构建期扫描不到，会掉效果。本工程当前没有这种写法。
- 新增后处理只要写进 `Assets` 下任一 VolumeProfile，下次构建会自动纳入（URP 是「扫描 Assets 全部 t:VolumeProfile」，偏保守），这是可接受的使用约束。
- 逐支 30 个 variant 的 keyword 组合**没有**在运行时 dump 验证；若要求 100% 确定，需要一次 `Shader.Find`/变体枚举探针。

### A4 画面证据可信度

我自己 `python compare.py` 复跑了四组对比，数字与施工表**逐项一致**（0.6938 / 2.2395 / 2.2137 / 0.0835 等）。

| 对比 | mean max\|d\| | >8 占比 | 8×8 块 mean/max | 判读 |
|---|---|---|---|---|
| 关态同一次运行内两帧（噪声底） | 2.2395 | 2.979% | 0.703 / 14 | 动画噪声基线 |
| 开态同一次运行内两帧（噪声底） | 2.2137 | 2.747% | 0.766 / 15 | 噪声水平稳定 |
| **跨构建 关 vs 开** | **0.6938** | **1.722%** | **0.203 / 2.0** | 比噪声底更小 |
| 菜单 关 vs 开 | 0.0835 | **0.000%**（max 7） | 0.078 / 2.0 | 纯 UI 差 ≤7/255 |

**对比方法核实**：`capture2.ps1` 同一脚本、同机同分辨率（1280×720 客户区）、同等待时序（18 s → 点「开始」→ 12 s → 两帧间隔 3 s），`PrintWindow(PW_RENDERFULLCONTENT)`。关态图 23:03 抓取，当时 `Build/Windows64` 还是 21:50 的关态构建；开态图 23:13 抓取（开态构建 23:10 完成）。**是跨构建的两次运行，不是同一次运行内的 A/B**；但两侧时序对齐，守卫/尘埃的相位接近，这正好解释「跨构建差 < 同运行内差」。

**可信度评级：中上。** 支持「没有肉眼可见的渲染层变化」，不足以支持严格的「像素级完全一致」——理由：①两帧非同帧、场景有动画与粒子；②Bloom 在本场景视觉上很弱，无法用肉眼证明 Bloom 可见性（施工自己也只主张变体没删 + 差值图除动画元素外全黑，这一点是诚实且正确的）；③平均 RGB 差 < 0.06、菜单 0 个像素 >8，能排除「调色/色调映射整体偏移」这类会显著改画面的错误。若将来需要硬结论，应在同一进程内做「同帧、关开两套变体」的探针（现有 `PostFxPixelEvidencePlayModeTests` 路线）。

### A5 还原与现场（我实测）

- `git status --short` = 只有 `M Docs/Logs/TASK-A-R9-18.md`（本报告）；HEAD = **0132433**；无 commit。
- `UniversalRenderPipelineGlobalSettings.asset`：`:28 m_StripUnusedPostProcessingVariants: 0`、`:33 m_ShaderVariantLogLevel: 0`，`git diff` 无输出 → 已精确还原。
- `Tools/version.json`、`ProjectSettings/ProjectSettings.asset`：`git diff` 无输出 → 构建自增已回退。
- 无 `Unity.exe` / `ZFrame.exe` / `UnityCrashHandler64.exe` 残留（PowerShell `Get-Process` 返回空）；`UnityProject/Temp/UnityLockfile` 不存在。
- `Library/ShaderCache` 已由开态构建重新生成（23:06），关态旧缓存归档在 `TempLogs/archive_pre_audit/r9_18_shadercache_230403` + `..._db_230403`。
- `Build/Windows64` 现在是开态产物（166 文件，`globalgamemanagers.assets` 1 530 556 B）；`TASKS.md`/`HANDOFF.md`/`WORKLOG.md` 均未改。

### A6 未能独立复核的项（如实记录，不当成错）

- **包体基线 1 709 948 B / 81.95 MB**：关态构建产物已被开态覆盖，无法现场复算。可用的旁证：`Build/Installer/staging` 里 11-06 的关态资产 1 709 820 B，与本次 1 530 556 B 差 **−179 264 B**，与施工的 −175 KB 同量级。故「−175 KB」采信，标注为旁证而非直测。
- **墙钟 +55.8 s**：施工已明确「不宣称」，我也未采信——该项与 `##utp` duration 方向相反，且关态三次跨 80.8 s，属噪声。
- **Android / D3D12 / Vulkan**：未测。剥离逻辑是平台无关的构建期 keyword 裁剪，但各平台变体空间不同，收益与画面需另验。

### 五件事的直接回答

1. 变体数 = **7096**（复算一致）；五分项 = UberPost −690、Bokeh −20、Gaussian −14、MotionBlur −6、Panini −4。
2. 三个「未动」**全部属实**（Bloom 24、FinalPost 98、LutBuilder 8、Particles/Unlit 6268 逐块同数）；剥掉的确实全是本项目 Volume 没声明的后处理。
3. UberPost 31 支**在本项目当前资产下够用**（机制上只删 6 个 Volume keyword 组；3 个 Profile 里只有 Bloom/ColorAdjustments）；结论基于静态取证，运行时动态加后处理是未覆盖风险。
4. 画面一致性：**跨构建对比，差异低于同运行内噪声，菜单 0 像素 >8 → 可信度中上，支持「无可见回归」，不等于严格像素证明**。
5. 该不该开：**该开（PASS with 受限）**——Windows/D3D11 已验证真实收益且无可见回归；条件：后处理一律走 `Assets` 下的 VolumeProfile 声明，Android 需补验后再推广。
