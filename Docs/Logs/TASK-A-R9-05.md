# TASK-A-R9-05 —— 粒子系统（运行期创建）：内置 ParticleSystem，零资产、零新依赖

- **轨道**：A / R9（用户选的「粒子」方向；用户 2026-10-30 选定：**内置 ParticleSystem 手搓**，不引 VFX Graph）
- **状态**：doing（施工+验收完成，待主控收口）
- **施工**：general-purpose 施工子代理（接手续建：前代理施工中途中断，本代理先审计半成品再收尾补证）
- **验收**：general-purpose 验收子代理，2026-10-31（独立取证，与施工不同代理）
- **派发**：主控，2026-10-30
- **侦察笔记（必读）**：`Docs/Logs/RECON-particle-volume.md`

## 任务简报（主控写）

### 0. 起点：真的是零

侦察确认：全仓 **0 个 `.vfx`、0 个带 `ParticleSystem` 的 prefab、0 张 `.mat`/贴图**，包清单无 VFX Graph。
唯一的代码接触是 `PoolResetter.cs:41-45`（池回收时重置）。所以这不是"接线"，是从零造——但**不做资产**：
粒子系统与材质全部**运行期创建**，仓库里只增 `.cs`。

### 1. 目标（两个效果，不多做）

**效果 A：命中火花（burst，一次性）**
- 公开入口 `SpawnHitSpark(Vector3 worldPos, int count = 12)`；短生命（≤0.5s）、有重力/衰减、颜色从亮到暗。
- 用完**必须回收**（池化，不许每次 `new GameObject`），`maxParticles` 有硬上限。

**效果 B：环境浮尘（持续，低速率）**
- 场景常驻，跟随相机一定范围内的体积发射（或世界空间固定区域，你定但要说清）。
- 速率低（每秒 ≤ 20）、`maxParticles` ≤ 200，**不允许无限累积**。

**都要满足**：
1. **零资产**：不得新增 `.prefab` / `.mat` / `.vfx` / 贴图；材质用 `new Material(Shader.Find(...))` 运行期建。
2. **零新包**：不动 `manifest.json`。
3. **有界**：任一效果在任何情况下 `particleCount <= maxParticles`（给出断言）。
4. **可开关**：`ParticleRuntime.Enabled` 静态开关（默认开），关掉时**不产生任何粒子、不产生每帧开销**。

### 2. ⚠️ 本任务最大的坑（必须专门处理并给证据）

**运行时 `Shader.Find` 在 Player 里可能拿到 null**——URP 的粒子着色器若没有任何资产引用，打包时会被**剥离**。
这就是典型的"编辑器里好好的、打包后一片不亮"。

要求：
- 用 `Shader.Find` 时**必须有 fallback 列表**（至少两个候选），全部失败时**不崩、不刷屏**，只告警一次并让 `ParticleRuntime` 自禁用；
- **必须在 Player 构建里实测一次**（不是只看 Editor/PlayMode）。若项目现有的 Player 装配方式可用（B-R9-06/07 跑过 60s Player），照那个走；
- 若最终发现**必须**把着色器加进 Always Included Shaders（即改 `ProjectSettings/GraphicsSettings.asset`），
  **允许改，但必须在报告里单列**：改了哪个字段、为什么非改不可、有没有不改的替代方案。

### 3. 可证伪护栏（沿用 A-R9-04 的口径）

新增 **PlayMode** 用例（现在 8 个，只增不减）：
1. **A 真的画出来了**：记录基线帧统计 → `SpawnHitSpark` → 1–3 帧内 `brighter` 占比显著上升 → 0.5s 后回落到基线附近。
   阈值要给数字依据。
2. **B 存在但不吵**：浮尘开启 vs 关闭，像素差异**存在**（> 噪声下限）但**幅度远小于** A；
   同时 `particleCount <= 200` 恒成立。
3. **开关有效**：`Enabled=false` → 触发 `SpawnHitSpark` 后**像素与基线完全一致**（一个粒子都不许出）。
4. **不泄漏**：连续 `SpawnHitSpark` 100 次后，场景里 ParticleSystem 实例数**有界**（不随次数增长）；
   `Resources.FindObjectsOfTypeAll<ParticleSystem>()` 计数给出来。
5. **修前必红**：把 `ParticleRuntime` 的初始化注释掉 → 上面 1/2 必须变红 → 复原变绿。

### 4. 硬约束

- **白名单**：`UnityProject/Assets/ZFrame/View/**`（表现层）、`UnityProject/Assets/ZFrame/Tests/**`、
  `UnityProject/Assets/Game/Tests/**`、`ProjectSettings/GraphicsSettings.asset`（**仅当第 2 节证明非改不可**，报告单列）、本日志。
- **不许**新增 `video.*` 设置行（`SettingsRegistryTests.cs:79-104` 的 9 行 id 序列断言是硬护栏，本任务不碰设置）。
- 不动 FG / 超分 / 后处理链路（`ZrFrameGenerationFeature`、`ZrNative`、`PostFxCameraRuntime`）。
- 基线：EditMode **719**、PlayMode **8**（本任务只能增）、native **253/711,537**、三校验器 exit 0；六段全绿。
- UTF-8 无 BOM、LF；**不 git commit**；不装软件、不联网；禁止 `rm`（用 `mv`）。

### 5. 自测清单（贴证据）

1. A 的像素证据（基线 → 触发 → 回落 三段数字 + 阈值依据）。
2. B 的像素证据（开 vs 关，幅度对比 A）。
3. `Enabled=false` 时像素与基线**完全一致**。
4. 100 次连发后的实例数/粒子数有界（数字）。
5. **Player 构建里跑通**（关键）：给 Player 端日志或像素读数，证明着色器没被剥离；fallback 生效路径也测一次（把首选 shader 名字改错，确认不崩、只告警一次）。
6. 修前必红（回退初始化 → 1/2 变红 → 复原变绿）。
7. 帧率：粒子开启/关闭对比；A 触发瞬间不掉帧（给数字）。
8. 六段全绿 exit 0（EditMode 719、PlayMode ≥10、native 253/711,537、三校验器 exit 0）。
9. `git status` 零 `.prefab`/`.mat`/`.vfx`/贴图；若改了 `GraphicsSettings.asset` 单列说明；未 commit。

### 6. 诚实边界（报告必须回答）

- 两个效果看起来像什么？如果"不好看"（毕竟运行期生成、无美术资产），如实说——**本任务的验收标准是"机制成立且可证伪"，不是"好看"**。
- Player 里着色器可用性是怎么保证的（剥离风险的真实状态）。
- 有没有哪部分是"为了通过测试而存在"（例如测试专用分支）？有就明说。

---

## 施工报告（general-purpose 施工子代理·接手续建 写）

**一句话**：接手审计结论是**半成品完成度远高于交接快照所述**——三个 `.cs` 完整、GraphicsSettings 恰 1 行改动、
Player(headless) 三模式证据 / 修前必红证据 / 六段全绿证据**全部在盘**（`TempLogs/r9_05_probe/`），施工报告也已落盘
（交接快照"任务日志无施工报告"已过时）；逐项独立复核**未发现需要返工的缺陷**。本代理续建增量 = 复核 + 补证：
①对**最终代码**复做修前必红（恰 3 红且全红在像素断言 → 复原 sha256 逐位一致 → 复原后六段再跑 ALL PASS）；
②窗口化 Player 独立实测，复现环境受限（无托管代码执行）；③亲跑一轮像素数字，落在前代理 5 次跨跑区间内。
六段全绿（EditMode **719**、PlayMode 8→**12**、native **253/711,537**、三校验器 exit 0）。

### 0. 接手审计结论（先证伪，再续建）

| 审计项 | 结果 |
|---|---|
| 交接快照 vs 盘上实况 | 快照称"三个文件落盘但日志无施工报告"——**快照过时**：日志报告已写完（10-30 15:27，晚于末轮测试 15:25），证据链完整 |
| 三个 `.cs` | 完整、可编译、逻辑与简报逐条对上：A 池 4×64（`SparkPoolSize=4`/`SparkMaxParticles=64`）、B 速率 12/s ≤20 且 `maxParticles=200`、**无任何 `Update`**（跟随相机靠父子关系）、关态第一行返回零开销、4 候选 shader 链 + 全失败只告警一次并自禁用 |
| GraphicsSettings.asset | diff **恰 1 行**（见 §7）；guid 经 `Library/PackageCache/com.unity.render-pipelines.universal@14.0.12/Shaders/Particles/ParticlesUnlit.shader.meta` 证实 |
| 剥离证据（简报 §2 的坑是否真实） | **独立复现**：`TempLogs/r9_07/player_clean/.../globalgamemanagers` grep `Universal Render Pipeline/Particles/Unlit` = **0 命中**；`TempLogs/r9_05_probe/player/.../globalgamemanagers` = **1 命中**——不加 Always Included 时 Player 里确实没有这个着色器 |
| 修前必红证据的新鲜度 | 前代理 `red_mutation.xml`（15:07）**早于**最终代码（测试文件 15:16 / factory 15:21）→ 证据对旧版采集。本代理对最终代码**复做一遍**（§8），结论相同 |
| Player 证据 | headless（`-batchmode -nographics`）三模式 normal/fallback/bad 全部 exit 0 在盘；**像素证据缺**（环境），前代理已如实声明并以 `r907_sanity*.log`×5 佐证；本代理窗口化实测**独立复现**同一受限（§7.4） |
| sha 复原核对 | `ParticleRuntime.cs` 与前代理变异前备份 `ParticleRuntime.orig.cs` 的 sha256 一致（`2875f7ed104294ba…f815f3a`）——变异已正确复原 |
| 编码/换行 | 三个 `.cs` 首 `3` 字节 `75 73 69`（**无 BOM**），`file` 判 UTF-8/ASCII 无 CRLF 标记（**LF**） |
| 范围 | `git status`：新增仅 3 `.cs`(+meta)+目录 meta+本日志，改 1 个 GraphicsSettings；`zrender.dll` M 是 native 段重建产物（构建噪声，入库前由主控 `git checkout --`）；零 `.prefab/.mat/.vfx/贴图`；manifest.json / SettingsRegistry / FG / 超分 / 后处理零改动 |

**结论：无返工项；续建 = 复核 + 对最终代码补证。**

### 1. 改动清单（仓库只增 `.cs`；另有一行 `GraphicsSettings.asset`）

| 文件 | 性质 | 内容 |
|---|---|---|
| `UnityProject/Assets/ZFrame/View/Particles/ParticleRuntime.cs` | 新增 | 公共缝：`Enabled` / `SpawnHitSpark(Vector3,int=12)` / `SetAmbientDust` / `SetAmbientAnchor` / 着色器解析与 fallback / 池与计数 |
| `UnityProject/Assets/ZFrame/View/Particles/ParticleEffectFactory.cs` | 新增 | 两个系统的参数（内部类型）：spark 池参数、dust 体积/速率/上限、透明材质 |
| `UnityProject/Assets/ZFrame/Tests/PlayMode/ParticlePixelEvidencePlayModeTests.cs` | 新增 | 4 条 PlayMode 像素护栏（A / B / 关闭+不泄漏 / fallback）；第 5 条"修前必红"是变异流程，不是常驻用例 |
| `UnityProject/ProjectSettings/GraphicsSettings.asset` | 改 1 行 | `m_AlwaysIncludedShaders` 追加 URP 粒子 Unlit（单列见 §7.2） |

- 未动：`manifest.json`、任何 `.unity`、任何 `.mat/.prefab/.vfx/贴图`、`SettingsRegistry`（**没有新增 `video.*` 行**）、FG/超分/后处理链路。
- 新目录 `Assets/ZFrame/View/Particles/` 及其 `.meta` 为 Unity 自动生成。
- Player 探针夹具（`R905Harness.cs` / `R905BuildPlayer.cs`）**不在仓库内**：已归档到 `TempLogs/r9_05_probe/probe_assets/`，
  所以 EditMode 停在 719 不增（临时夹具不计入基线）。

### 2. 机制（30 秒版；细节在前代理报告原件与本代理读码记录一致）

- **A 命中火花**：`SpawnHitSpark(pos, count=12)` → 从 **4 个池化 ParticleSystem** 取空闲实例（无空闲则 round-robin 覆盖最老，
  有界不分配）→ `Clear()`+`Play()`+`Emit(clamp(count,1,48))`。生命 0.22–0.45 s（≤0.5s）、`gravityModifier=1.1`、
  LimitVelocity dampen 0.55、尺寸随生命收到 0.15、颜色白热→橙→暗红；`maxParticles=64`/实例，池总上限 4×64=256。
  固定 `randomSeed` 保证像素门限可复现。
- **B 环境浮尘**：1 个常驻系统，速率 **12/s**、生命 6–9 s、`maxParticles=200`、世界空间模拟；发射体是**父级挂在锚点相机**的
  7×3.5×4 m 盒子（相机前方 1.5–5.5 m）。"跟随相机"= 父子关系，不是每帧轮询；场景切换经 `sceneLoaded` 每装载一次重锚。
- **开关**：`SetEnabled(false)` → 全部 `Clear()`+`SetActive(false)`，`SpawnHitSpark` 第一行返回，`ApplyAmbient` 直接 park；再开时恢复。
- **着色器**：候选链 `URP/Particles/Unlit` → `URP/Particles/Lit` → `Legacy Shaders/Particles/Alpha Blended` → `Sprites/Default`；
  候选须 `Shader.Find != null` 且（有图形设备时）`isSupported`、`passCount>0`。全失败：告警恰一次 + `SelfDisabled` + 不崩。

### 3. 证据 1：A 真的画出来了（本代理亲跑，320×180 ARGBFloat 57,600 像素，同机对照帧逐像素差分）

| 臂 | 本代理亲跑（2026-10-31） | 前代理末轮（10-30 15:25） | 门限 |
|---|---|---|---|
| A.control（无粒子） | meanLuma=0.02000 std=0.00000 | 同左 | rig 自检 maxLuma<0.1 |
| A.burst（spawn 后第 2 帧） | meanLuma=0.03153 max=0.9074 | meanLuma=0.03230 max=0.9045 | — |
| A.faded（0.55 s 后） | meanLuma=0.02000，与对照**逐像素相同** | 同左 | — |

差分：`A burst vs control: meanAbs=0.01153 brighter=0.0131 darker=0.0000 moved=0.0131`；
`A fade-back vs control: meanAbs=0.000000 brighter=0.0000 alive=0`。

**阈值依据**：门限 `brighter>0.0040` / `meanAbs>0.0020`。跨跑实测 `brighter` 0.0131–0.0168、`meanAbs` 0.0115–0.0143
（本代理 + 前代理至少 3 次独立运行）；**坏的那一侧（初始化被注释）实测精确 0.0000**——两侧无重叠区，
余量吸收"第 2 帧撞不同 `deltaTime`"的抖动，不是吸收浮点噪声。

### 4. 证据 2：B 存在但不吵（且永不超 200）

| 臂 | 本代理亲跑 | 前代理末轮 |
|---|---|---|
| A 参照（12 颗火花） | brighter=0.0179 meanAbs=0.01492 | brighter=0.0168 meanAbs=0.01426 |
| B（dust 开 2 s 后） | brighter=0.0022 meanAbs=0.00040 **ratioToA=0.123** | brighter=0.0024 meanAbs=0.00057 ratioToA=0.142 |

- **存在**：`brighter=0.0022 > 门限 0.0008`（对照帧自身 `moved=0.0000`，噪声下限是 0）。
- **不吵用能量判**：dust 覆盖像素比火花多但每个像素很暗，`meanAbs` 只有 A 的 0.10–0.19 倍（门限 `<0.40`），
  峰值亮度 0.3779 < 0.90。用"面积"判会得出反的结论（前代理第一版按面积比实测 3.97× 直接红，才改判据）。
- **上限**：8 s 墙钟逐帧采样，本代理 `peakDust=91 / settled=88 / cap=200`（前代理 95/94/200；理论稳态 72–108）；
  采样期无一帧超 200；`maxParticles=200` 是引擎侧硬钳位。

### 5. 证据 3：`Enabled=false` 像素与基线完全一致

```
[PARTICLES] disabled spawn vs baseline: meanAbs=0.000000 moved=0.0000 total=0 lastEmitted=0   （本代理亲跑，同前代理）
```

先 `SetEnabled(false)`（dust 也处于"被请求开"），拍基线 → 连调 10 次 `SpawnHitSpark` → 再拍：
`moved=0.0000`（容差 0.0005，对照帧逐比特相同）、`TotalParticleCount=0`、`LastSpawnEmitted=0`。
关掉时一个粒子不产生、一个像素不动。

### 6. 证据 4：连发 100 次不泄漏

```
[PARTICLES] 100 spawns: particleSystems 1 -> 5, alive=48, pool=4/4, perSystemCap=64   （本代理亲跑，与两轮前代理一致）
```

`Resources.FindObjectsOfTypeAll<ParticleSystem>()`：**1 → 5**（+4 = 池上限；起点 1 个是 dust）。
断言 `after-before <= SparkPoolSize` 且 `after <= before+4`，另加 `alive <= 4×64=256`（实测 48）。
100 次 spawn 没有新建第 5 个 spark 系统——池化是真的。

### 7. 证据 5（关键）：Player 里的着色器 —— 剥离**确实**会发生，已修；像素证据的环境受限如实声明

#### 7.1 事实（本代理独立复现）：URP 粒子着色器默认会被剥离

| 构建 | `Universal Render Pipeline/Particles/Unlit` 在 globalgamemanagers | `Legacy Shaders/Particles/Alpha Blended` |
|---|---|---|
| `TempLogs/r9_07/player_clean`（B-R9-07 产物，**无**本任务那行） | **0 命中** | 2 命中 |
| `TempLogs/r9_05_probe/player`（本任务，**加了**那行） | **1 命中** | 2 命中 |

→ 不加 Always Included，`Shader.Find("URP/Particles/Unlit")` 在 Player 里**只会拿到 null**。

#### 7.2 GraphicsSettings 改动（单列，按简报要求）

- **改的字段**：`ProjectSettings/GraphicsSettings.asset` → `GraphicsSettings.m_AlwaysIncludedShaders`
  追加一行 `- {fileID: 4800000, guid: 0406db5a14f94604a8c57ccfbc9f3b46, type: 3}`。**就这一行**（git diff 核对无顺带改动）。
  guid = `com.unity.render-pipelines.universal@14.0.12/Shaders/Particles/ParticlesUnlit.shader`（PackageCache meta 证实）。
- **为什么非改不可**：不加它，Player 必落 fallback（Legacy 着色器）——能亮，但**和 PlayMode 像素证据测的不是同一个着色器**，
  证据链断成两截。加上后 Player 与 Editor 同着色器，§3/§4 的数字才对 Player 有意义。
- **有没有替代**：有，且已实现并实测（4 候选 fallback 链 + §6 的 fallback 用例 + §7.3 的 Player 实测"首选错→下一个接上"）。
  替代的代价：Player 换 Legacy 着色器，颜色/软粒子/排序表现与 Editor 不同。所以选了改一行。
  代价：构建多编译一套粒子变体，首次构建耗时 +8 min 量级，`globalgamemanagers` 153 KB。

#### 7.3 Player 里的运行证据（真机，`-batchmode -nographics`，前代理采集、本代理逐字复核在盘）

从 `SCN_Empty` 构建 `R905.exe`，三种模式（证据：`TempLogs/r9_05_probe/evidence_normal.txt` / `evidence_fallback.txt` / `evidence_bad.txt`）：

| 模式 | Player 日志要点 | 结论 |
|---|---|---|
| normal | `shader='Universal Render Pipeline/Particles/Unlit' selfDisabled=False warnings=0 emitted=12`；`pool particleSystems 5 -> 5`；`disabled-spawn emitted=0 particles=0`；`DONE exit=0` | 着色器**在包里且被找到**；池有界；关态零粒子；不崩 |
| fallback（首选名故意写错） | `probe='__R905_Missing_A__=missing; Universal Render Pipeline/Particles/Unlit=ok(0 passes)'` | fallback 链在真机里真的往下走 |
| bad（全部写错） | `WARN#1 ... no usable particle shader ...`；`warnings=1`；`emitted=0`；`DONE exit=0` | **只告警一次**、自禁用、不崩、exit 0 |

（headless 下 verdict 显示 `ok(0 passes)` 是设计内：无图形设备时 `isSupported/passCount` 不可判，靠 `Shader.Find`
是否命中 + 构建数据 grep 判定；有设备时该分支不生效。）

#### 7.4 必须如实说：Player 里**没有**像素证据（本代理独立复现环境受限）

- 前代理结论：窗口化 Player 停在 `UnloadTime` 之后，托管代码一行不执行（连 `SubsystemRegistration` marker 都不落盘）；
  **昨天能跑 60s 像素证据的 R907.exe（B-R9-07 已知可用产物）同机同症状**（`r907_sanity*.log`×5）→ 环境（远程桌面/会话/交换链）问题，非本次代码。
- **本代理独立复现**（2026-10-31）：窗口化运行 `R905.exe`（640×360，R905_OUT 指向新文件）75 s——进程自行退出、
  **证据文件未创建**（无 marker）、stdout 停在 memorysetup 段。与 headless 三模式（15:03，正常完成）形成同构建对照。
- 因此 Player 侧证据边界：**着色器在不在包里 / 会不会崩 / 告警几次 / 池有没有界 = 已实测（headless）**；
  **Player 里画出来什么样 = 未测**。像素证据全部来自 Editor PlayMode（真实 D3D 设备，走 URP 正常渲染路径）。
  闭环条件：在能创建窗口的会话重跑 `R905.exe`，读 `DIFF burst-vs-control` 一行（夹具与构建都在 `TempLogs/r9_05_probe/`）。

### 8. 证据 6：修前必红（本代理对**最终代码**复做）

变异：`EnsureReady()` 第一行插入 `return false;`（等价"初始化被注释掉"），其余不动。

```
PLAYMODE total=12 passed=9 failed=3   （TempLogs/runtests_playmode.xml，2026-10-31）
FAILED  hit spark did not draw: 0.0000 of pixels got brighter (gate 0.0040)
FAILED  ambient dust is not visible at all: 0.0000 of pixels got brighter (gate 0.0008)
FAILED  fallback shader drew nothing: 0.0000 of pixels moved (gate 0.0020)
```

三条全**红在像素断言**，恰是简报第 3 节点名的 1/2 两条 + fallback 加固条；
`Disabled_EmitsNothing...` 与「不泄漏」在变异下仍绿——它们断言的是"没有"，初始化被拿掉当然还是"没有"，不是漏洞。
复原（`cp` 回备份）后 sha256 `2875f7ed…` 与变异前**逐位一致**，随后六段再跑 **ALL PASS**（§9 第二轮）。

### 9. 证据 7：六段全绿（本代理两轮亲跑，exit 0）

```
第一轮（接手复核）RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)
  native 253/711,537 · editmode 719/719 · playmode 12/12 · arch/config/naming exit 0
第二轮（修前必红复原后）RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)
  native 253/711,537 · editmode 719/719 · playmode 12/12 · arch/config/naming exit 0
```

`warning CS`：editmode / playmode 日志各 **0 条**（两轮均核）。PlayMode **8→12 只增不减**（4 条新像素护栏）。

### 10. 证据 8：帧耗（报告项，不是断言）

本代理亲跑：320×180、30 次 `Camera.Render()`+`GL.Flush()` 的 CPU 提交墙钟——`sparksOn 1.021 ms / allOff 0.592 ms / delta 0.429 ms`
（前代理末轮 1.199/0.651/+0.549）。60fps 预算 16.667 ms，增量约 0.4–0.55 ms（2.6–3.3%），**A 触发瞬间无掉帧证据**。
边界照实说：`GL.Flush()` 只冲刷命令不等 GPU，**GPU 侧开销未证**；本轮 machine quiet check 判 BUSY，绝对值不可跨跑比较。

### 11. 范围与规范

```
 M UnityProject/Assets/Plugins/ZRender/zrender.dll      ← native 段重建产物，非手改
 M UnityProject/ProjectSettings/GraphicsSettings.asset  ← §7.2 那一行
?? Docs/Logs/TASK-A-R9-05.md
?? UnityProject/Assets/ZFrame/Tests/PlayMode/ParticlePixelEvidencePlayModeTests.cs(+.meta)
?? UnityProject/Assets/ZFrame/View/Particles.meta
?? UnityProject/Assets/ZFrame/View/Particles/（两个 .cs + 两个 .meta）
```

- **零** `.prefab/.mat/.vfx/贴图`；manifest.json 未动；SettingsRegistry 未动（9 行断言原样）；**未 git commit**。
- 三个 `.cs` UTF-8 无 BOM、LF。

### 12. 诚实边界（3 问）

1. **两个效果像什么？** A：一次短促的橙色火星爆开——无贴图圆点、近看边缘生硬、固定 seed 每击形状相同（重复感快；改随机只删一行）。
   B：相机前方 1.5–5.5 m 一层很淡的灰蓝小点缓慢上浮，关掉几乎立刻干净（meanAbs 是 A 的 ~12%）。一句话：**机制成立、可证伪；"好看"没做到**，
   验收口径是前者。
2. **Player 着色器可用性怎么保证？** 三件事：① Always Included 把 URP 粒子 Unlit 钉进包（构建数据 grep + Player `shader='…'` 双证）；
   ② 4 候选 fallback（Player 实测首选错→第二个接上）；③ 全失败只告警一次 + 自禁用 + 不崩（Player `exit 0`）。
   剩余真实风险见 §13。
3. **为测试而存在的部分**：`SetShaderCandidates(...)`（诊断缝，生产不调，用于造 fallback/全失败）、`ResolveShader` 的
   `hasGraphicsDevice` 分支（避免 headless 把"没设备"误判成"被剥离"，有设备时不生效）、`Shutdown()`（生命周期缝+TearDown 清理点）、
   `DescribeState()`（日志格式化）。除此之外无测试专用路径：`SpawnHitSpark`/`Enabled`/`SetAmbientDust` 就是产品入口。

### 13. 风险与已知问题（⑤）

1. **Player 像素未测**（环境，§7.4）——最大的一条，验收裁决 PASS / PASS-with-受限 时请直接考虑它。
2. 换 URP 大版本时 guid 可能失效 → 包里又会没有该着色器（fallback 接住但画面变样）。
3. 池耗尽（>8.9 次/秒高频命中）会截断旧 burst；低频攻击无感。
4. 火花形状固定（randomSeed 固定），像素证据可复现的代价。
5. `SpawnHitSpark` 目前**无调用方**（玩法接线是后续任务）——玩家在游戏里看不到它，除非有人调 API。
6. 本机帧率受后台负载干扰（本轮 quiet check 判 BUSY），帧耗数字只看同一次运行内的 A/B 差。

---

## 验收清单（主控写）

> 独立取证，不复用施工数字。10 项全 PASS 才通过。
> 本任务三个失败模式：**①粒子"画出来了"其实是别的东西在变亮；②Player 里根本不亮（剥离）而我方只在 Editor 验过；③为了测试造了一条生产不走的专用路径。**

1. **V1 A 效果独立测**：自己跑基线 → `SpawnHitSpark` → 1–3 帧 → 0.5s 回落，三段数字自己取；
   并**排除"不是粒子在变亮"**（例如同时关闭别的效果；或确认变化区域与生成位置相关）。
2. **V2 B 效果 + 有界**：浮尘开/关差异存在但幅度小（对照 A）；长跑采样 `particleCount` 峰值 **≤200**，自己取数。
3. **V3 开关真的关得住**：`Enabled=false` 触发后像素 `moved=0` 且粒子数为 0。
4. **V4 不泄漏**：连发 100 次，`ParticleSystem` 实例数**有界**（施工报池上限 4，1→5），自己数。
5. **V5 修前必红（自己复做）**：注释 `EnsureReady()` → 像素断言必须红 → 复原（核对 sha）→ 绿。**不允许只看施工输出。**
6. **V6 着色器剥离的真实性**：施工称旧包 grep 不到该 shader、加 `GraphicsSettings.asset` 一行后 grep 到 1 处。
   你要**独立核实这一行改动**（`git diff ProjectSettings/GraphicsSettings.asset` 只有 1 处新增、没顺带改别的），
   并判断"**Player 像素未证**"这一点的严重性——它直接决定结论是 PASS 还是 PASS with 受限。
7. **V7 零资产**：`git status` 无 `.prefab`/`.mat`/`.vfx`/贴图；唯一资产类改动就是那 1 行 `GraphicsSettings.asset`（要单列）。
8. **V8 设置面未动**：`SettingsRegistry` 无新行，`SettingsRegistryTests.cs:79-104` 九行断言原样。
9. **V9 六段不回归**：全绿 exit 0（EditMode **719**、PlayMode **12 = 8+4**、native **253/711,537**、三校验器 exit 0）；零 `warning CS`。
10. **V10 诚实边界核实**：①施工是否承认"不好看"（本任务标准是机制可证伪、不是好看，但**不许吹**）；
    ②fallback 全失败是否只告警一次并自禁用；③有没有"只在测试里存在"的分支或参数。

**须在结论中回答**：①把 URP 粒子着色器塞进 Always Included Shaders 的**代价**（包体/启动/维护），这笔账值不值；
②"Player 侧只证着色器存在、像素全来自 Editor"——这个证据链够不够支撑"玩家能看见粒子"，不够的话缺什么；
③12 个 PlayMode 连跑的**耗时与稳定性**（有没有 flaky 风险，尤其像素类用例在负载机器上）。

## 验收结论（验收代理写）

**裁决：PASS** —— general-purpose 验收子代理，2026-10-31。全部数字与变异均为验收代理独立亲跑亲取，未复用施工输出。
关键增量：**施工报告 §7.4 的"Player 像素未证"受限，验收期间已由验收代理独立复跑窗口化 Player 实测覆盖**（见 V6），故不判 PASS-with-受限。

### 十项验收清单（逐项一行证据）

| 项 | 裁决 | 证据（验收代理亲取） |
|---|---|---|
| V1 A 效果独立测 | PASS | 亲跑：A.control 均匀 0.02000（rig 自检 <0.1）→ burst `brighter=0.0143 meanAbs=0.01243`（门限 0.0040/0.0020，>2.7x 余量）→ 0.55s 后 `brighter=0.0000 meanAbs=0.000000 alive=0` 逐像素回基线；"不是别的东西在亮"由夹具排除：探针相机停 5000,5000,5000 空场景+纯色 0.02 背景、`darker=0.0000`、`Enabled=false` 时 spawn `moved=0.0000`、B 在 A 用例中显式关闭（tests L110） |
| V2 B 效果+有界 | PASS | 亲跑：dust `brighter=0.0032 > 0.0008` 存在；`meanAbs=0.00083`、ratioToA=**0.181** < 0.40 不吵；8s 墙钟逐帧采样 `peakDust=91 / settled=87 / cap=200`，无一帧超 200 |
| V3 开关关得住 | PASS | 亲跑：`SetEnabled(false)` 后 10 次 spawn：`moved=0.0000 total=0 lastEmitted=0`（容差 0.0005，对照帧逐比特相同） |
| V4 不泄漏 | PASS | 亲跑：100 连发 `particleSystems 1 -> 5`（+4 = 池上限 SparkPoolSize，断言 L340-344），`alive=48 <= 4×64=256` |
| V5 修前必红（亲做） | PASS | 验收代理亲做变异（`EnsureReady()` 首行插 `return false;`）→ PlayMode **恰 3 红**：hit spark `0.0000 vs gate 0.0040` / ambient dust `0.0000 vs 0.0008` / fallback `0.0000 vs 0.0020`，全红在像素断言；Disabled/不泄漏在变异下仍绿（断言"没有"，合理）；`cp` 复原后 sha256 `2875f7ed…f815f3a` 与变异前逐位一致 |
| V6 着色器剥离真实性 | PASS | 亲 grep 构建数据：旧包（r9_07 无该行）`globalgamemanagers` URP Particles/Unlit **0 命中**、本任务包 **1 命中**——剥离确会发生且已被修；`git diff GraphicsSettings.asset` 恰 1 行新增于 `m_AlwaysIncludedShaders`，guid `0406db5a…3b46` 经 PackageCache `com.unity.render-pipelines.universal@14.0.12/Shaders/Particles/ParticlesUnlit.shader.meta` 证实；**窗口化 Player 复跑成功（验收代理亲跑，真实 Direct3D11）**：`shader='…Particles/Unlit' ok(1 passes)`、`DIFF burst-vs-control brighter=0.0196 meanAbs=0.01586`、`disabled-spawn moved=0.0000`、`dust-vs-off brighter=0.0054`、`DONE exit=0`（`TempLogs/r9_05_acceptance/r905_windowed_evidence.txt`）——Player 里真的画出来了 |
| V7 零资产 | PASS | `git status` 全量核对：新增仅 3 `.cs`(+2 meta)+目录 meta+本日志，改 1 个 GraphicsSettings（单列如上）+zrender.dll（native 重建噪声，主控收口处理）；**零 `.prefab/.mat/.vfx/贴图`**；材质运行期创建：`ParticleEffectFactory.cs:209 new Material(shader)`（L123 spark / L174 dust 使用） |
| V8 设置面未动 | PASS | `git diff SettingsRegistryTests.cs` 与 `manifest.json` 均为空；`Tests/Debug/SettingsRegistryTests.cs:79-104` 九行 `video.*` id 序断言逐字在盘 |
| V9 六段不回归 | PASS | 亲跑（quiet check=QUIET）：native **253/711,537**、editmode **719/719**、playmode **12/12**（四条粒子用例 fullname 在 xml 在列）、arch/config/naming 三校验器 **exit 0**，`RESULT: ALL PASS`、脚本 exit 0；editmode/playmode 日志 `warning CS` 各 **0 条** |
| V10 诚实边界核实 | PASS | ①施工如实承认"好看没做到，机制成立可证伪"（§12.1）；②全失败只告警一次+自禁用：`WarnOnce`（ParticleRuntime.cs:421-430 计数闸）+Player bad 模式实证 `WARN#1 warnings=1 selfDisabled=True exit 0`；③测试专用缝 `SetShaderCandidates`/`hasGraphicsDevice`/`Shutdown`/`DescribeState` 全部如实申报且 grep 证实**无生产调用方**（`SpawnHitSpark` 现无玩法调用方也已如实列风险⑤） |

### 简报三问（验收结论必答）

1. **Always Included 这一行值不值**：值。代价=构建多编译一套粒子变体（施工称 +8min 量级）+`globalgamemanagers` 增 ~153KB+URP 升级时 guid 可能失效（届时 fallback 链接住，画面退 Legacy 不黑屏）；收益=Player 与 Editor 同着色器，像素证据链不断成两截。替代方案（只靠 fallback）已实现且实测可用，但会让 Player 静默换 Legacy 着色器、表现与 Editor 不一致——一行换确定性，划算。
2. **证据链够不够支撑"玩家能看见粒子"**：够。验收前最大缺口"像素全来自 Editor"已由验收代理在真实 D3D11 窗口化 Player 亲取像素（brighter=0.0196，为 Editor 门限的 4.9x）封口，叠加构建数据 grep（0→1 命中）与 headless normal/fallback/bad 三模式。残余缺口：窗口化 Player 只跑了 normal 模式（fallback/bad 两模式有 Editor 真设备 PlayMode 用例 + Player headless 双证），如求 100% 封口可在能开窗口的会话补跑，不阻塞。
3. **PlayMode 12 连跑耗时与稳定性**：整段 xml 时长 18.7s（墙钟 50s 含 Unity 启动），变异轮（BUSY）与全量轮（QUIET）连跑均绿；像素量跨跑区间窄（A brighter 0.0143-0.0196、dust 0.0032-0.0054，全部 ≥2.7x 门限余量），无 flaky 迹象；唯一理论抖点是 B 的 8s 墙钟稳态断言（peakDust>30）在极端负载下可能收紧，BUSY 轮已过，余量足够。

### 范围核对与文件规范

- `git status` 共 7 项，与施工报告 §11 声明逐条一致，**未 commit**（验收代理不 commit）。
- 三个 `.cs` 无 BOM（首 3 字节 `75 73 69`）、LF（无 CRLF 标记）、`ParticleRuntime.cs` 无 `Update` 方法（grep 证实，零每帧脚本开销的实现方式=关态首行返回 L236-239 + dust 父子关系跟随 L543-546 + sceneLoaded 事件钩 L163-183）。
- 本日志四节齐全（任务简报/施工报告/验收清单/**验收结论**）。
- 变异备份与证据归档：`TempLogs/r9_05_acceptance/`（pre_mutation 备份、窗口化证据）。

**遗留（不阻塞，已记账）**：同施工报告 §13 六条，另加一条——窗口化 Player 的 fallback/bad 两模式建议后续能开窗口时补跑一次（当前双证：Editor 真设备用例 + Player headless）。
