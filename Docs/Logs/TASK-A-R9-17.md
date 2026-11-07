# TASK-A-R9-17 —— URP 变体剥离的真实收益（变体数 + 构建时长对照）

> 补 A-R9-16 的核心受限项。当时 `stripped=14` 只是**规则命中数**，不是减少的变体数；
> **真实构建变体数 / 构建时长对照从未做过**。如果剥离其实没省时间，那 21 条规则 + 36 条用例就是白做。
> 本任务只做测量，不改剥离器规则。**禁止 git commit**。

## 要回答的问题

在同一台机器、同一命令、同一平台同一档位下：
- 剥离**关**（默认）→ 构建变体数 = ? 构建耗时 = ?
- 剥离**开** → 构建变体数 = ? 构建耗时 = ?
- 差值是多少？剥离器是否真的删除了变体（`IPreprocessShaders` 是否真被调用）？

## 步骤（照 A-R9-16 R7 走，不要自创）

### 1）开变体日志（唯一需要改的资产，事后精确还原）
`UnityProject/Assets/UniversalRenderPipelineGlobalSettings.asset:33` `m_ShaderVariantLogLevel: 0` → `1`。
- **只改这一行**，改前记下原值
- 若 `1` 时日志仍无变体行，可试 `2`（更详细），报告里说明用了哪一档
- 这 3 个 `URP_*.asset:113` 也有同名字段，**先只改 GlobalSettings**，不够再逐个试并记录

### 2）环境准备
- `Tools/check_machine_quiet.ps1` 确认 QUIET
- PowerShell 侧清 8 个代理变量；**Bash 侧禁止转调 powershell** → 用 **PowerShell 工具**
- Unity 单实例：查 `Unity.exe` 进程 + 删 `UnityProject/Temp/UnityLockfile`
- 跑完 Unity 可能僵死：轮询 results，5 分钟无退出则 `taskkill //PID //F`

### 3）关态构建（基线）
- 确认剥离器关：EditorPrefs `ZFrame.UrpVariantStripper.Enabled` 默认 false（**先确认，别假设**）
- 跑 `Tools/build_player.ps1`（Windows x64，比 Android 快得多）
- 记录：脚本总耗时、Unity 日志里的变体汇总行（`total variants` / 每个 shader 的 `variants: N`）
- 变体行在哪：构建日志或 `Editor.log`，把**实际命令与日志路径**写进报告

### 4）开态构建（对照）
- `Unity.exe -batchmode -executeMethod ZFrame.Editor.UrpVariantStripper.SetEnabledFromCommandLine -urpStripEnabled true`（注意：**不要加 `-quit`**）
- **同一命令、同一机器、同一平台、同一档位**再跑一次 `build_player.ps1`
- 记录同样两个量

### 5）还原
- `git checkout -- UnityProject/Assets/UniversalRenderPipelineGlobalSettings.asset`（**只这个单文件**）
- EditorPrefs 恢复 false
- **不要整仓 checkout**；`zrender.dll` 时间戳噪声按纪律**不要**丢弃
- 报告里贴 `git status` 证明只还原了该还原的

### 6）误伤检查（若构建出包）
- 装包（或跑 exe）看：粉红材质 / Bloom / 阴影 / 色调映射是否异常

## 判据

- 给出**两组数字**（变体数、构建耗时）+ **差值**，且注明测量方式（哪个日志、哪一行）
- 明确回答：`IPreprocessShaders` **有没有真被调用**、有没有真删掉变体
- 如果测不出差异（例如 batchmode 根本不打变体日志、或 Windows 变体本来就少），**如实说测不出** + 说明卡在哪一步、下次换 Android 要注意什么
- 数字必须来自实际日志，**不许估算、不许编**

## 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- **不改剥离器规则**（本任务只测量）；不改游戏内容
- 资产改动**精确到单文件**还原，不整仓 checkout
- 说「变快了」必须附「怎么测的」，两个数字必须同源同法

## 环境纪律（真实踩过）

- 构建前 `unset http_proxy https_proxy all_proxy`（Bash）；PowerShell 清 8 个变量
- **PATH 上的 cmake 是 MSYS2 版无写权限** → 原生构建走 `Tools/build_native.ps1`
- Unity 批处理**单实例**；**勿加 `-quit`**
- **GUI Editor 加 `-force-d3d12` 会挂起，别加**
- PS 5.1 对原生 exe 加 `2>&1` 会把退出码变 1（假失败）
- 清理用 `mv` 归档 `TempLogs/archive_pre_audit/`

---

## 施工报告（施工代理写）

**结论：完整构建对照做成了，答案是「没收益」。** 在同一台机器、同一命令、同一平台同一档位
（StandaloneWindows64 / Release / d3d11 / 冷 ShaderCache）下：

| | 编译变体数 | 着色器编译耗时Σ | Unity 构建耗时 | 命令墙钟 |
|---|---|---|---|---|
| 剥离**关**（默认） | **7830** | **210.00 s** | **284.9 s** | **343.6 s** |
| 剥离**开** | **7830** | **206.88 s** | **280.3 s** | **383.4 s** |
| 差值（开−关） | **0** | −3.12 s | −4.6 s | **+39.8 s** |

`IPreprocessShaders` **真被调用**（探针实测 419 次），但**一个变体都没删**（`wouldStrip` 合计 **0**）。
−3.12 s / −4.6 s 落在关态自身重复跑（三次：204.28 / 205.52 / **210.00** s、252.8 / 272.5 / **284.9** s）的
噪声带内，而墙钟反而 **+39.8 s**（方向相反）⇒ **不宣称变快**，本档位测不出收益。

### R1 口径锁定（同源同法）

| 项 | 值 | 证据 |
|---|---|---|
| Unity | **2022.3.62f3c1** | 构建日志首部 `Initialize engine version: 2022.3.62f3c1` |
| 管线 | **URP 14.0.12**（声明 14.0.11，磁盘 PackageCache 14.0.12；同 A-R9-16 R1 登记） | `Library/PackageCache/com.unity.render-pipelines.universal@14.0.12` |
| 平台/档位 | **StandaloneWindows64**（PlayerBuild 的 `BuildOptions.None`、非 Development、d3d11） | 构建日志 `##utp:{"type":"PlayerBuildInfo"...}` + 探针首行 `platform=StandaloneWindows64 development=False` |
| 构建入口 | `Tools/build_player.ps1`（Unity 由 Git Bash 拉起，**无 `-quit`**） | `Tools/build_player.ps1:312` |
| 变体日志档位 | **`m_ShaderVariantLogLevel = 1`**（只用了这一档，未用到 2） | `UnityProject/Assets/UniversalRenderPipelineGlobalSettings.asset:33` |
| 剥离器开关 | EditorPrefs `ZFrame.UrpVariantStripper.Enabled`（原本**键不存在**⇒ 默认 false） | 见 R5 |

### R2 开变体日志（只改一行，已还原）

改前 `git diff` 精确一行：
```
-  m_ShaderVariantLogLevel: 0
+  m_ShaderVariantLogLevel: 1
```
**生效证据**：日志出现 **419** 个 `Full variant space:` 块、**407** 条 `compiled N variants` 行；
关态构建没开日志时（A-R9-16 R2）这些行一条都没有。档位 `1` 已足够，未升到 `2`。

### R3 测量方式（四个量，全部来自同一份日志/同一条命令）

每次构建后把 `TempLogs/build_player_unity.log` 逐次另存，四个量同源：

1. **编译变体数** = 所有 `finished in X seconds. ... compiled N variants ...` 行的 `N` 求和。
   样例（关态，`TempLogs/r9_17_build_off_unity.log:666`）：
   ```
       finished in 149.18 seconds. Local cache hits 0 (0.00s CPU time), remote cache hits 0 (0.00s CPU time), compiled 6144 variants (2384.14s CPU time), skipped 0 variants
   ```
2. **着色器编译耗时Σ** = 同上行的 `X` 求和（单块串行耗时之和，非墙钟）。
3. **Unity 构建耗时** = 日志末尾 `##utp:{"type":"PlayerBuildInfo","version":2,...}` 的 `duration` 字段
   （Unity 自报，含 `Writing asset files` / `Postprocess built player` 分步）。
4. **命令墙钟** = PowerShell `Stopwatch` 包住整条 `.\Tools\build_player.ps1 -TimeoutSec 1800`
   （含 20 s 冒烟与报告生成，两次同口径）。

命令（两次完全相同，只差前置开关）：
```bash
# 关态
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_player.ps1 -TimeoutSec 1800
# 开态（先置开关，不加 -quit；日志 TempLogs/r9_17_setpref_on.log:334 打印 Enabled = True）
"C:/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe" -batchmode \
  -projectPath ".../UnityProject" \
  -executeMethod ZFrame.Editor.UrpVariantStripper.SetEnabledFromCommandLine \
  -urpStripEnabled true -logFile ".../TempLogs/r9_17_setpref_on.log"
# 再跑同一条 build_player.ps1
```
> 关键前提：每次测量构建前 **`mv` 掉 `Library/ShaderCache` + `ShaderCache.db`**，否则 Unity 会走
> 「脚本增量」（实测有一次只跑 47.9 s、什么都没编译，样本作废，见 R8-1）。三次关态 + 两次开态都按此清缓存。

### R4 两组数字（全部实测，逐次留档）

| # | 状态 | 清 ShaderCache | 探针 | 编译变体数 | 编译块 | 编译Σ | Unity 构建 | 命令墙钟 | 日志 |
|---|---|---|---|---|---|---|---|---|---|
| A | 关 | 是 | 无 | **7830** | 407 | 204.28 s | 252.8 s | 409.1 s | `r9_17_build_off_unity.log` |
| B | 开 | 是 | 无 | **7830** | 407 | 207.47 s | 264.5 s | 311.7 s（postprocess 报错，见 R8-2） | `r9_17_build_on_fail_unity.log` |
| D | 关 | 是 | 有 | **7830** | 407 | 205.52 s | 272.5 s | 328.3 s | `r9_17_build_off2_unity.log` |
| **E** | **开** | **是** | **有** | **7830** | 407 | **206.88 s** | **280.3 s** | **383.4 s** | `r9_17_build_on5_unity.log` |
| **F** | **关** | **是** | **有** | **7830** | 407 | **210.00 s** | **284.9 s** | **343.6 s** | `r9_17_build_off3_unity.log` |

**同条件主对照 = F（关，探针）vs E（开，探针）**：变体数 **7830 vs 7830（差 0）**；
编译Σ 210.00 → 206.88 s（−3.12 s，**−1.5%**）；Unity 构建 284.9 → 280.3 s（−4.6 s，**−1.6%**）；
墙钟 343.6 → 383.4 s（**+39.8 s**）。

**噪声带（关态自身三次）**：编译Σ 204.28 / 205.52 / 210.00（跨度 **5.72 s**）；
Unity 构建 252.8 / 272.5 / 284.9（跨度 **32.1 s**）；墙钟 409.1 / 328.3 / 343.6（跨度 **80.8 s**）。
⇒ 开态的两个「改善」都小于关态自己的抖动，且墙钟方向相反；**不能用它证明变快**。
变体数则是**确定性的**：5 次构建（含不带探针的 A/B）全部 7830、407 块、分布逐块一致（`diff` 空）。

### R5 `IPreprocessShaders` 是否真被调用、是否真删了变体

构建期回调无法从外部观测，本轮用**一次性探针**（`ZFrame.Editor.R917ProbePreprocessShaders`，
只观测不删除；测量后已移出仓库，见 R6）实测，结果原样摘录：

```
[probe] FIRST CALL at 2026-11-07 21:36:35  UrpStripEnabled=True  platform=StandaloneWindows64  development=False
[probe] shader=Universal Render Pipeline/Particles/Unlit pass=ForwardLit variants=6144 validKeywords=2/37 wouldStrip=0 keywordSpaceValid=False
```
（开态 `TempLogs/r9_17_probe_on5.log`；关态 `TempLogs/r9_17_probe_off3.log` 首行为 `UrpStripEnabled=False`）

| 问题 | 实测答案 | 证据 |
|---|---|---|
| 回调**有没有**被调用 | **有**：419 次（1 条首调用 + 418 条 shader/pass 记录），覆盖全部 407 个编译块 | 两份探针日志行数 |
| 开关在构建进程里**读到了吗** | **读到了**：开态 `UrpStripEnabled=True`、关态 `False`（说明 EditorPrefs 跨进程生效，不是「设置没读到」） | 首调用行 |
| **真删了多少变体** | **0**。418 个块 `wouldStrip` 全为 0，合计 **0**；与「开/关变体数都是 7830」完全吻合 | `grep -c 'wouldStrip=0'` = 418，`wouldStrip=[1-9]` = 0 |
| 为什么一条都没删 | 到达回调的变体集合里**已经没有那 21 条规则针对的关键字组合**；且部分 shader 连关键字都解析不全 | 下表的 `keywordSpace` 解析结果 |

`shader.keywordSpace.FindKeyword()` 对剥离器 37 个「关心关键字」的解析率（编辑器内实测，
`TempLogs/r9_17_diag.log:447/456/465/474`）：

| Shader | keywordSpace 关键字总数 | 解析成功 |
|---|---|---|
| `Universal Render Pipeline/Lit` | 59 | **15 / 37** |
| `Universal Render Pipeline/Unlit` | 21 | **6 / 37** |
| `Universal Render Pipeline/Particles/Unlit`（6144 变体那块） | 26 | **2 / 37**（只有 `_SCREEN_SPACE_OCCLUSION`、`DEBUG_DISPLAY`） |
| `Hidden/Universal Render Pipeline/UberPost` | 22 | **17 / 37**（全是白名单保护的 Bloom/Tonemap 关键字） |

结论拆成两句，避免混淆：
1. **调用链成立**：`IPreprocessShaders` 被调用、开关被读到、判定（`Evaluate`）被执行 —— 这部分 A-R9-16 的「未验证」现在**已验证**。
2. **删除量为零**：即便 Lit 能解析出 15 个关键字，其变体里也没有启用这些关键字的组合 —— 因为它们
   早已被 URP 自带的 `m_StripUnusedVariants: 1` / `m_StripDebugVariants: 1` / `m_Prefilter*` 先剥掉
   （与 A-R9-16 R10.6「21 条规则与 URP 自带剥离重叠」的静态判断一致，本轮由实测确认）。
   对 `Particles/Unlit`（6144 变体、占总量 78%）更彻底：37 个关键字只解析出 2 个，规则结构性无法命中。

### R6 还原证明（只动了该动的）

- **开关**：EditorPrefs `ZFrame.UrpVariantStripper.Enabled_h1688558220` 已删除
  ⇒ 恢复「键不存在 = 默认 false」的原始状态（`UrpVariant pref values left: 0`）。
- **变体日志**：`git checkout -- UnityProject/Assets/UniversalRenderPipelineGlobalSettings.asset`（**单文件**），
  第 33 行已回到 `m_ShaderVariantLogLevel: 0`。
- **构建副作用**（设计内，非本任务改动）：`Tools/version.json`（build 32→36）与
  `UnityProject/ProjectSettings/ProjectSettings.asset`（bundleVersion 递增）被构建写回，
  已分别 `git checkout --` **单文件**还原到 build=32。
- **探针**：`R917ProbePreprocessShaders.cs` + `.cs.meta` 已 `mv` 到 `TempLogs/archive_pre_audit/r9_17_probe/`，
  仓库内不再存在。
- **`git status --porcelain` = 空**（写本报告之前实测，见下），`git log -1` 仍是 `4496d87`，**未 commit**。
- 未整仓 checkout；`UnityProject/Assets/Plugins/ZRender/zrender.dll` 未动（git status 无它）。
- 归档而非直删：`Library/ShaderCache` 三次已 `mv` 到 `TempLogs/archive_pre_audit/r9_17_shadercache_*`。

### R7 误伤检查（开态包）

- 开态构建**完整成功**（exit 0），冒烟 PASS：`alive 20s`、`Player.log` 三条启动链标记齐全。
- 开态 `Player.log`（`LocalLow/ZFrame/ZFrame/Player.log`，21:41:18）grep `error|Shader|pink|magenta|subshader` **零命中**。
- 体积与关态同口径：`Player 81.95 MB / 166 files`（两次构建报告一致）。
- 结构上也不可能有误伤：**实际删除 0 个变体**，产物与关态等价。

### R8 踩到的坑（下次照做，别重踩）

1. **增量构建会让你白测**：只改代码/只删 `Library/ShaderCache.db` 不够——一旦 Unity 判定内容未变，
   会走 `Run script only build`（实测第 3 次构建 47.9 s、0 个 `compiled` 行、变体数取不到）。
   **必须 `mv` 掉整个 `Library/ShaderCache` + `ShaderCache.db`** 才会重编着色器。
2. **`Library/Bee` 状态文件重命名错误**：一次构建在最后 postprocess 阶段失败
   （`bee_backend: error: Unable to rename state file 'Library/Bee/TundraBuildState.state' => '.map'`，
   `build failed: result=Failed errors=1`）。与剥离器无关，重跑即通过。
   注意：本次**没有**删 `Library/Bee`（本机 safe-delete 钩子拦批量删除），两次正式对照都在 Bee 热态下跑，条件一致。
3. **墙钟噪声 80 s**：同配置关态三次 328.3 / 343.6 / 409.1 s。**本机不适合用墙钟证明个位数百分比的收益**，
   要看日志里的 `compiled N variants` 与编译 Σ。
4. **热缓存会把收益抹平**：ShaderCache 热时变体编译接近免费（`Local cache hits` 拉满），
   任何「少编变体」的收益都被缓存吃掉；要测必须冷缓存，且两边都冷。
5. **`keywordSpace` 在构建期不是全量**：Particles/Unlit 只解析 37 个里的 2 个。若将来要按关键字剥离，
   应改用 `ShaderUtil.GetShaderGlobalKeywords/ GetShaderLocalKeywords`（A-R9-16 R2 试2 实证可用）
   或在 shader 源码层做静态裁剪，而不是依赖构建期 `FindKeyword`。
6. **换 Android 前必读**：Android 变体空间更大（A-AND-06 历史 12288），但**同样的坑一个不少**——
   ① URP 自带 stripper 大概率已把 21 条规则覆盖（本轮 Windows 实测为 0 增量，Android 未必更好）；
   ② `IPreprocessShaders` 在 Android/IL2CPP 下是否同样被调用，必须**用同样的探针再验一次**，别默认成立；
   ③ Android 构建慢（29 分钟量级），必须冷 ShaderCache 才看得见变体收益，否则 Gradle/IL2CPP 噪声远大于剥离收益；
   ④ 同一台机器上 Windows 与 Android 的变体数不可互相换算。

### R9 证据文件清单（全部本轮新写，`TempLogs/` 下）

`r9_17_build_off_unity.log`（关A）、`r9_17_build_on_fail_unity.log`（开B）、
`r9_17_build_off2_unity.log`（关D）、`r9_17_build_on5_unity.log`（开E）、`r9_17_build_off3_unity.log`（关F）、
`r9_17_build_on3_unity.log`（作废的脚本增量样本）、`r9_17_probe_on5.log` / `r9_17_probe_off3.log`（探针）、
`r9_17_diag.log`（keywordSpace 诊断）、`r9_17_setpref_on.log`（开关置 true）、
`r9_17_build_{off,on,on3,off2,on5,off3}.console.txt`（脚本控制台）、`Build/build_report.txt`（末次构建报告）。


---

## 验收清单（供验收代理）

- [ ] V1 两组数字同源同法（同一个日志、同一行口径）
- [ ] V2 明确回答 `IPreprocessShaders` 是否被调用、是否真删变体
- [ ] V3 资产精确还原（`git status` 证明无多余改动）
- [ ] V4 若测不出，如实说明卡点，无编造
- [ ] V5 未改剥离器规则；未 commit；现场干净

## 验收结论（验收代理写）

**取证方式**：未重跑构建（遵守铁律）。全部结论来自 `TempLogs/` 现有日志 + 剥离器/规则/探针源码
+ `Library/PackageCache/com.unity.render-pipelines.universal@14.0.12` 包源码审阅。

**总体判断：PASS（结论成立），附一项受限。** 变体数 7830 属实；`wouldStrip=0` 定性为 **a) 规则没有
命中任何存活变体**（不是 b，也不是 c）。但"21 条规则白写"应精确表述为**结构性冗余 + 大面积不可达**，
不是匹配逻辑写错。

### V1 重算变体数（7830 复核 → 属实）

自写脚本对每条 `finished in X seconds ... compiled N variants` 行的 N 求和（与施工报告同口径）：

| 日志 | `compiled` 行数 | **ΣN** | Σ编译耗时 | utp `duration` |
|---|---|---|---|---|
| `r9_17_build_off_unity.log`（关A） | 407 | **7830** | 204.28 s | — |
| `r9_17_build_off2_unity.log`（关D） | 407 | **7830** | 205.52 s | — |
| `r9_17_build_off3_unity.log`（关F，主对照） | 407 | **7830** | **210.00 s** | **284 919 ms** |
| `r9_17_build_on_fail_unity.log`（开B） | 407 | **7830** | 207.47 s | — |
| `r9_17_build_on5_unity.log`（开E，主对照） | 407 | **7830** | **206.88 s** | **280 330 ms** |
| `r9_17_build_on3_unity.log`（作废样本） | 0 | 0 | 0 | — |

- 主对照 F vs E：**7830 vs 7830，差 0**；编译Σ 210.00 vs 206.88（−3.12 s）；Unity 构建
  284.9 vs 280.3 s（−4.6 s）——与施工报告 R4 完全一致，**重算无出入**。
- 不止总数相同：把两边的 `compiled N variants` **逐块序列**取出 `diff`，407 行**逐行一致、diff 为空**；
  `Full variant space:` 块数 419 vs 419 也一致。⇒ 不是"总数碰巧抵消"，是同一变体空间。
- `r9_17_build_on3_unity.log` 0 条 `compiled` 行，与 R8-1「脚本增量未重编」吻合，作废样本判定正确。

**两次构建是否只差剥离开关 → 是（除版本号自增这一构建副作用）**

- 同一入口：`Tools/build_player.ps1:312` → `Game.EditorTools.PlayerBuild.BuildWindows64` →
  `UnityProject/Assets/Game/Editor/PlayerBuild.cs:709-715` 固定
  `target = BuildTarget.StandaloneWindows64` + `options = BuildOptions.None`（非 Development）。
- 两份日志同 `2022.3.62f3c1`、同 `d3d11`、同 407 块；探针首行 `platform=StandaloneWindows64 development=False`。
- 开关确实只在开态生效：开态探针首行 `UrpStripEnabled=True`，关态 `UrpStripEnabled=False`。
- 唯一非关差异：`Tools/version.json` build 33 vs 36 / `bundleVersion` 自增（构建写回，已还原 build=32），
  与变体/耗时无关。两边都按 R3 清了 `Library/ShaderCache`（archive 下 4 个目录为证）。
- 小瑕疵（不影响结论）：探针 418 条 shader 记录的 `variants` 求和是 **14132（≈1.8×7830）**，因为同一
  shader/pass 会被投递多次（6144 那块被记录 **2 次**）。所以 R5 的「419 次覆盖全部 407 个编译块」措辞
  不精确——419 是**调用次数**，不是唯一编译块数。

### V2 `wouldStrip=0` 定性：**a) 规则未命中任何存活变体**（b、c 均排除）

**先排除 c（判断对但没真删）**：`UrpVariantStripper.cs:181` 在 `decision.Strip` 时执行
`compilerDataList.RemoveAt(variantIndex)`——这就是 `IPreprocessShaders` 里删除变体的正确方式（该回调没有
`context.RemoveVariants` 这种 API）。删除动作已落地，不存在"判了不删"。

**再排除 b（探针统计错、实际删了）**，三层证据：

1. **探针与生产代码同源**：探针 `R917ProbePreprocessShaders.cs:85/101/112` 与剥离器
   `UrpVariantStripper.cs:148/160/166` 是同一套逻辑（`shader.keywordSpace.FindKeyword()` →
   `data.shaderKeywordSet.IsEnabled(localKeyword)` → `Evaluate`），且与 URP 官方
   `ShaderScriptableStripper.cs:83/186` 用法完全相同。**不存在"shaderFeatureFlags 位掩码 ↔ 关键字名解码错误"。**
2. **删除量有独立旁证**：探针 `callbackOrder=500`，排在剥离器（10）之后，拿到的是剥离器处理后的同一个
   List。若剥离器真删了，探针看到的 `variants` 必然变小。实测 6144 那块探针仍报 `variants=6144`，构建日志
   同块也是 `compiled 6144 variants`（关态 `r9_17_build_off_unity.log:666` 等）。**删除量确为 0，探针掩盖不了删除。**
3. **关态对照**：关态（剥离器第 125 行直接 return，`UrpStripEnabled=False`）探针仍 418 条全 `wouldStrip=0`。
   说明 0 来自"规则匹配不到"，与开关、删除代码无关。

**为什么一条都中 → 规则判据 ≠ 真实变体空间（提示猜想验证：方向成立，但不是解码 bug）**

- 规则语义是"**任一**目标关键字被启用即剥"（`UrpVariantRules.cs:242-249` 单关键字精确匹配，不是组合匹配）。
- 到达回调的关键字空间严重不足（探针 418 条按 `validKeywords` 分布，本代理实测）：

  | 可解析关键字数 | 记录数 | 变体数 | 说明 |
  |---|---|---|---|
  | **0 / 37** | **273（65%）** | 527 | 该 shader 根本没声明这 37 个关键字，规则连评估前提都不成立 |
  | 1/37 | 28 | 44 | |
  | **2/37** | 74 | **12 665** | 含 **6144 变体的 `Particles/Unlit`（占总量 78%）**，只有 `_SCREEN_SPACE_OCCLUSION`、`DEBUG_DISPLAY` |
  | 15/37 | 10 | 27 | 仅 `Universal Render Pipeline/Lit` |
  | 17/37 | 2 | 721 | `UberPost`，且全是白名单关键字 |

- 更关键：**21 条规则的关键字，恰好全部是 URP 14.0.12 自带过滤先剥掉的那批**（依据的是同一套 URP 资产配置）：
  - 无条件 `[ShaderKeywordFilter.RemoveIf(true, ...)]`：`_LIGHT_LAYERS`、`_RENDER_PASS_ENABLED`、
    `_MAIN_LIGHT_SHADOWS_SCREEN`、`_WRITE_RENDERING_LAYERS`、`_DBUFFER_MRT1/2/3`、`_DECAL_LAYERS`
    （`Runtime/Data/UniversalRenderPipelineAssetPrefiltering.cs:40-52,122,153-165`）；
  - 条件预过滤：`_ADDITIONAL_LIGHTS_VERTEX`(SelectPixelAndOff)、`_ADDITIONAL_LIGHT_SHADOWS`、
    `_FORWARD_PLUS`、`_SCREEN_SPACE_OCCLUSION`、`_DEFERRED_FIRST_LIGHT`、`DEBUG_DISPLAY`（同文件 75-116）；
  - pass 级剥离：`ShaderScriptableStripper.cs:377-595`（LightLayers / AdditionalLights* / SSAO /
    punctual shadow / DecalNormalBlend / ReflectionProbe* / RenderPassEnabled 等）。
  - 顺带：`_DEFERRED_STENCIL` 在 URP 14 里只剩一个字符串常量（`UniversalRenderPipelineCore.cs:1266`），
    没有任何 shader 声明 → 该条规则天然 0 命中。
- `IShaderVariantStripper` 在 `IPreprocessShaders` **之前**执行，所以本回调拿到的已是"被 URP 去掉这 21 类
  关键字"的集合。**0 命中是结构性必然，不是偶发。**

**因此定性 = a**，但两句话要说清：
1. 对**本工程当前配置 + Windows 档**："21 条规则无收益"成立，撤销或重写是合理选择；
2. 原因不是"规则写错/统计错"，而是**与 URP 自带预过滤结构性重叠**，且对 65% 的记录这些关键字在该 shader
   上**根本不存在**。真要重写，方向应是"换目标"（例如 `m_StripUnusedPostProcessingVariants: 0` 的后处理
   变体），而不是换匹配方式。本轮铁律禁止改规则，此事留新任务。

**本项受限**：6144 变体块只有 2/37 可解析，意味着"未命中"里混着"不可评估"。将来若换目标关键字，必须先用
`ShaderUtil.GetShaderGlobalKeywords/GetShaderLocalKeywords`（A-R9-16 R8-5 已建议）验证可达性再谈收益；
本结论不外推到 Android/其它档位。

### V3 现场

| 项 | 实测 | 判定 |
|---|---|---|
| `git status --porcelain` | 仅 ` M Docs/Logs/TASK-A-R9-17.md` | 符合 |
| HEAD | `4496d87`（chore: 派发 A-R9-17） | 未 commit |
| Unity 残留进程 | `tasklist` 无 `Unity.exe` | 干净 |
| 锁文件 | 无 `UnityProject/Temp/UnityLockfile` | 干净 |
| `UniversalRenderPipelineGlobalSettings.asset:33` | `m_ShaderVariantLogLevel: 0` | 已还原 |
| 探针残留 | `Assets/` 下无 `R917Probe*`（已归档 `TempLogs/archive_pre_audit/r9_17_probe/`） | 干净 |
| 版本副作用 | `Tools/version.json` build=32、`bundleVersion: 0.1.0.32` | 已还原 |
| 构建副作用 | `zrender.dll` 时间戳未丢弃 | 符合纪律 |

### 验收清单复核

| 项 | 结论 | 依据 |
|---|---|---|
| V1 两组数字同源同法 | **PASS** | 同一 `compiled N variants` 口径重算，5 份有效日志全部 7830；F/E 逐块 diff 为空 |
| V2 明确回答调用/删除 | **PASS** | 419 次调用、开关读到位；删除量 0 有探针+构建日志双证 |
| V3 资产精确还原 | **PASS** | `git status` + 行 33 = 0 + 无残留进程/锁 |
| V4 测不出如实说明、无编造 | **PASS** | 噪声带与"不宣称变快"处理正确；本代理重算数字完全对得上 |
| V5 未改规则/未 commit/现场干净 | **PASS** | `git status` 仅本报告；规则文件相对 HEAD 无改动 |

### 三层判断

| 层 | 结论 | 说明 |
|---|---|---|
| **数据层**（变体数/耗时/同源同法） | **PASS** | 7830 复核通过，逐块一致；两次构建只差开关（版本号自增为构建副作用） |
| **结论层**（`wouldStrip=0` 定性、无收益） | **PASS with 受限** | 定性 a 成立且证据链闭合；受限在"2/37 覆盖盲区"与"不可外推 Android"；另 R5 的"419 次覆盖 407 块"措辞需修正为"419 次调用" |
| **纪律层**（不重跑/不提交/现场） | **PASS** | 无 commit、HEAD 未动、资产与版本均还原、无残留 |

**最终：PASS with 受限。** 施工报告的主结论（本档位无收益、`IPreprocessShaders` 真被调用但删除 0）**成立**；
需修正的只有两处表述：①"419 次覆盖 407 块"实为调用次数（同一 shader/pass 会被多次投递）；②"21 条规则白写"
应写成"与 URP 自带预过滤结构性重叠 + 对 65% 的记录关键字不可达"，据此决定撤销/重写时，应换目标而非改匹配逻辑。
