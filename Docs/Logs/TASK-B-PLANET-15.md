# TASK-B-PLANET-15 —— ZRenderPipeline 多相机渲染策略 + 探针内容锚断言

- **轨道**：B / PLANET（B-PLANET-11 验收发现的生产缺口，S 级）
- **状态**：doing
- **派发**：主控，2026-11-14
- **缺陷**：`ZRenderPipeline.Render`（ZRenderPipeline.cs:112/153/156）只渲 `cameras[0]`——会话存在第二相机（如 PlayMode 探针 RT 相机）时其 RT 永不被管线写入，ReadPixels 读到池化陈旧内存；B-PLANET-11 施工+验收两轮取证均被此污染（fixture 隔离才正确）。配套：探针断言无内容锚（nonBlack>0.2 恒真）。

## 任务简报（主控写）

### 1. 做什么

1. **多相机策略**（选型三选一，写报告）：
   - a) 管线支持多相机：按 cameras 列表逐相机走行星通道/共享目标（成本高，v1 行星通道单槽架构）
   - b) 显式策略：管线渲 cameras[0]（主相机），其余相机**显式跳过并打一行日志**（首帧一次，不刷屏）——把静默降级变显式可诊断
   - c) 相机白名单/标记：带特定标记的相机才进管线
   - **倾向 b**（最小诚实化：多相机支持属后续需求，当前缺的是"静默错误"变"显式可见"）；如选 a 需评估双槽架构影响，MISSING+回主控
2. **探针内容锚**：ZrPlanetProbePlayModeTests 的像素断言加内容锚——至少一个"必须出现行星特征色"（高程带绿色域/limb 圆盘几何）的断言，杜绝"池化陈旧内存也 nonBlack 恒真"的盲区
3. **测试**：PlayMode 新增双相机场景用例（第二相机存在时主相机渲染不受影响+第二相机被显式跳过有日志）；EditMode ≥1235/PlayMode ≥114/arch/naming 全绿
4. **白名单**：ZRenderPipeline.cs、ZrPlanetProbePlayModeTests.cs、其日志；Native/** 零改动；其余不碰

### 2. 铁律

约法三章；施工不 commit、不改看板；UTF-8 无 BOM/LF；零第三方依赖；Unity 批处理纪律照旧（PlayMode 全套轮注意 B-PLANET-14/15 两刀叠加后的时序——AssemblyConnect 已有 0.051 余量）

### 3. 完成判据

- 多相机场景行为显式化（选型落地+日志）；内容锚断言入探针；全量绿
- fixture 隔离取证纪律可降级为"双相机场景也可正确取证"

## 验收结论（2026-11-15 接管轮）

**总结论：FAIL**（C1/C2/C4 PASS，C3 FAIL：PlayMode 全量 115 例 5 红、两轮同形；红点全部落在本任务新增的内容锚上——锚是对的，暴露的是探针 fixture 在全量套件下被泄漏场景污染，施工方"depth=-1000 即可全量正确取证"的假设不成立）

> 本轮全部数字为本验收代理亲跑亲写（Unity 批处理独占，2026-11-15 01:25—01:43 本地时段），不采信任何既有报告数字。环境备注：本机进程环境同时存在 `HTTPS_PROXY`/`https_proxy` 大小写重复，`run_tests.ps1` 经 Start-Process 启动 Unity 会直接失败（环境块构造异常）；先清空代理变量再跑即可（本轮每条命令前置清理，非代码问题）。

### C1 多相机行为显式化 —— PASS（实现与定向取证）；受限（全量套件下该用例不可达）

- 实现核对（读码）：`ZRenderPipeline.cs:112` `if (cameras.Length > 1 && !_multiCameraSkipLogged)`；日志体 `:121-124` 点名 cameras[0] 与全部被跳过相机；渲染面仍只认 `cameras[0]`（`:137` `_sceneBridge.Submit(cameras[0])`、`:178` `context.SetupCameraProperties(cameras[0])`、`:181` Blit→CameraTarget）——选型 b（显式跳过 + 首帧一行日志）落地正确，主相机路径零改动。
- 用例：`Probe_DualCamera_MainCameraUnaffected_SecondCameraExplicitlySkipped`（`ZrPlanetProbePlayModeTests.cs:485`），fixture 隔离会话（`-testFilter Game.View.PlayMode.Tests.ZrPlanetProbePlayModeTests`，TempLogs/bp15_base_class.xml，5/5 passed，92.7s）：
  - 第二相机加入后主相机同位姿重渲染 **moved=0.000000 / meanAbs=0.0000**（限 0.02，`:537`），green 保持 0.5472；
  - 多相机日志 **恰 1 行**（整份会话日志 `grep -c 多相机会话` = 1，不刷屏）：`[ZRender] 多相机会话（2 台）：只渲染 cameras[0]='~PlanetProbeCamera'（行星通道单槽架构，其余相机显式跳过：不渲染、不贴屏）：~PlanetProbeSecondCamera`；
  - 探针相机 depth=-1000（`:930`）→ 第二相机 -999，`cameras[0]` 语义成立。
- **受限**：全量套件中该用例在第二相机加入**之前**就红在 `dualcam-base` 绿色域锚（0.0d vs 0.15），双相机断言（`:527/:537`）根本没被执行（见 C3）。故 C1 结论只在"探针 fixture 独跑"下成立。

### C2 探针内容锚 —— PASS（非 nonBlack 恒真，且经污染样本反证）

- 断言清单（行号）：近档 `:325-327`（绿域 0.5472 ≥ 0.15 + 天空/地形带 + 管线中心对照）；中档 `:341-342`（无绿域锚，改双带锚——距离雾压平地形，物理行为）；远档 `:356-358`（绿域 ≥0.04 + limb 四角清屏 ≥0.95 / 盘心清屏 ≤0.05 / 圆盘非清屏 ≥0.08 + 中心对照）；行走取证帧 `:578`；中心对照实现 `:1238-1290`（RT 中心 == 原生 1×1 回读的 sRGB 变换 ±1，先等一次新鲜 ReadbackCount）。
- 亲跑实测（隔离会话，TempLogs/bp15_base_class.xml 输出节）：near green=0.5472、skyTopClear=1.0000/bottomClear=0.0000；far green=0.1166、corners 1.000/1.000/1.000/1.000、centerClear=0.0000、diskFrac=0.1922；中心对照三档全中——near `rt=(120,153,144)` vs native `(47,81,70)`→srgb `(119,153,143)`（差 1）；far `rt=(109,130,98)` vs srgb `(110,131,98)`（差 1）；mid `(64,90,108)` vs `(64,90,108)`（0）。
- **独立离线复核（本代理复算，不复用施工方脚本结论）**：手工解码归档 PNG——正确帧 `TempLogs/acc-p11-myfixture-pngs/planet_ed_near.png` nonBlack=1.0000 / green=0.5472 / 中心 (119,153,143)；**污染帧** `TempLogs/acc-p11-construction-pngs/planet_ed_near.png` **nonBlack 仍 =1.0000 而 green=0.0000、中心 (130,133,139) 灰**——直接证明旧判据恒真、新锚可分辨；far 正确帧 green=0.1166/disk=0.1922/四角 1.000/盘心清屏 0.0000，污染帧 green=0.0000/右上左下角 0.000/盘心清屏 0.1250（三条锚全失配）。阈值余量（0.15 vs 0.5472；0.04 vs 0.1166；0.08 vs 0.1922）均 ≥2.8x。
- 结论：C2 判据达成，且内容锚是"有牙"的（见下方变异）。

### C3 全量绿 + arch/naming —— FAIL（PlayMode 两轮 5 红，确定性）

- **EditMode PASS**：`Tools/run_tests.ps1 -Segments editmode` → **total=1235 passed=1235 failed=0 skipped=0**（21.47s，TempLogs/runtests_editmode.xml，本轮新写）——对齐基线 ≥1235。
- **PlayMode FAIL**：`-Segments playmode` 两轮逐位同形 —— **total=115 passed=110 failed=5**（轮1 01:34 / 轮2 01:41，TempLogs/bp15_full_run2.xml + TempLogs/runtests_playmode.xml）。用例数 115 = 基线 114 + 新增双相机 1（数量达标），但 5 红全部是 `ZrPlanetProbePlayModeTests` 的整族：
  | 用例 | 失败点 | 断言值 |
  |---|---|---|
  | Probe_DualCamera_...Skipped | `:502` dualcam-base 绿域锚 | 0.0d vs >0.15 |
  | Probe_ThreeTierScreenshots_CaptureEvidence | `:325` near 绿域锚 | 0.0d vs >0.15 |
  | Probe_Walk1km_... | `:578` walk1k:before | 0.0d vs >0.15 |
  | Probe_Walk5km_... | `:578` walk5k:before | 0.0d vs >0.15 |
  | Probe_Walk50km_... | `:578` walk50k:before | 0.0d vs >0.15 |
- **根因（本轮定位，非相机排序）**：同一份 fixture 独跑 5/5 绿（bp15_base_class.xml），全量套件 5/5 红——差别在会话场景内容：探针测试期间 `ZRenderPipeline` 的场景扫描日志为 **`renderer=54 可读=6`**（全量，bp15_full_run2.log:1675/2087/2499/2911/3298）对 **`renderer=0 可读=0`**（隔离，bp15_base_class.log 5 次全 0）。即前序场景类用例（`AffordanceScenePlayModeTests.cs:243`、`SurvivalLoopPlayModeTests.cs:23/96` 等 `LoadSceneAsync("SCN_Game")`）**加载 SCN_Game 后未卸载/未回空场景**，探针帧被泄漏场景占据（与施工方归档的污染样本 signature 一致：green=0.0000）。施工方的 `depth=-1000` 只解决了"相机排序"，未解决"场景泄漏"，故完成判据第 2 条（"fixture 隔离取证纪律可降级"）**未达成**——本轮实测结论相反：探针取证仍然**必须** fixture 隔离，且现在会红着提示你。
- **arch PASS / naming PASS**：`-Segments arch,naming` 各 exit=0（TempLogs/arch_report.txt、TempLogs/name_report.txt，本轮新写）；两文件 UTF-8 无 BOM（首字节 2f2f20）、CR 计数 0。
- **流程缺陷（如实登记，非 C1-C4 技术判据）**：本日志截至验收时只有「任务简报」一节，**施工报告节缺失**（AGENTS.md「四节齐全」不满足）。本轮按接管指令只追加验收结论节，未代写施工报告。

### C4 Native/** 零改动 —— PASS

`git diff --name-only` 仅 4 条（ZrPlanetProbePlayModeTests.cs、ZRenderPipeline.cs、Tools/version.json、ProjectSettings.asset），`git status --short -- Native UnityProject/Assets/Native` 为空；`Tools/Probes/out/**`、`TempLogs/**` 为未跟踪证据，不入库。

### 变异记录（亲自做，理由：C1 的核心可证伪主张是"第二相机存在必须显式留痕"）

- **变异点**：`ZRenderPipeline.cs:112` `cameras.Length > 1` → `cameras.Length > 99`（复现"静默降级"：第二相机存在但永不产生日志，渲染路径不变，最小单 token 改动）。
- **红**：`-testFilter ...Probe_DualCamera_...Skipped` → **total=1 passed=0 failed=1**，失败消息「多相机会话必须有「[ZRender] 多相机会话」显式跳过日志」，栈顶 `ZrPlanetProbePlayModeTests.cs:527`（TempLogs/bp15_mutant_log.xml）。
- **恢复**：逐字节回改同 token，`sha256sum` = `02e861bfb77837541c7faa7840149379586345a532de9ac27579773bb271b17b`（与变异前记录一致）。
- **复绿**：同 filter → **total=1 passed=1 failed=0**（1.9s，TempLogs/bp15_restored_log.xml），且日志仍恰 1 行。
- 另注：内容锚的"有牙"由污染样本离线复核反证（污染帧 nonBlack=1.0000 通过、绿域 0.0000 必红），无需二次变异。

### 版本号不一致（ProjectSettings vs Tools/version.json）—— 历史/设计使然，建议不动

- 现状：`ProjectSettings.asset` bundleVersion **0.1.0.34** / AndroidBundleVersionCode **12**；`Tools/version.json` build **34** / versionCode **13**。
- 判定依据：`PlayerBuild.PrepareVersion` 注入 version.json 的值，构建成功后 `CommitVersion` 把 build/versionCode **各 +1 写回**（PlayerBuild.cs:150-185）；`ReleaseVersion.BundleVersion => Version + "." + (Build + 1)`（ReleaseVersion.cs:58）。故 **ProjectSettings = 最后一次产出的包，version.json = 下一次要用的号**，两者天然差 1（versionCode）且 bundleVersion 编号 = version.json 的 build（34↔34）。HEAD 提交里同样是这套关系（32/10 vs 32/11）。
- 来源：`Build/build_report.txt`（13:17）+ `Tools/Probes/out/acceptance-build-run*.log` 记载本树发生过两次 player build（注入 0.1.0.33/11 → 写回 33/12；注入 0.1.0.34/12 → 写回 34/13），时间 13:13—13:17，**早于本任务施工（测试文件 mtime 17:15）**，属前序验收构建的副作用；同批 `Standalone: 0`（scriptingBackend 显式化）也是 Unity 构建写回，零行为变化。
- 处置建议（不自行修改）：保持原样，由主控闭环提交时按路径甄别，把 `Tools/version.json` + `ProjectSettings.asset` 单列一笔 chore（或干脆不提交、由下次构建自然覆盖）；**不要**手工把两者改成相等——version.json 是 versionCode 单调性的唯一来源，回改会造成号段重复或跳号。

### 工作区终态

`git checkout --` 还原了 4 张测试噪声 PNG（Docs/Parity/crowd_r9_15_off|on.png、npc_attitude_divergence.png、utility_personality_divergence.png；utility_personality_goap_chain.png 已被测试重写回原值）。当前未提交改动恰为：ZrPlanetProbePlayModeTests.cs、ZRenderPipeline.cs（施工态）+ Tools/version.json、ProjectSettings.asset（前序构建副作用），无额外代码改动、未 commit。

### 给主控的重派建议（若判重派）

1. **修根因（推荐）**：探针 fixture 在 `[UnitySetUp]` 里强制干净场景（新建空场景或卸载泄漏场景/销毁所有非 rig 根对象），而不是靠相机 depth 打补丁——本轮证据表明污染源是 **SCN_Game 泄漏的 54 个 renderer**，不是相机排序；或在 `ZRenderSceneBridge.Submit` 侧明确"行星通道启用时不提交场景物件"的优先级语义（需设计裁决，属生产行为变更）。
2. 若维持"探针必须独跑"：则须把该 fixture 从全量 PlayMode 套件中隔离（显式 skip + 说明），并如实改写完成判据第 2 条——但那样"全量绿"是以不跑为代价，建议不采用。
3. 顺带：施工报告节补写（四节齐全），并把本轮 5 红的 signature 记入 HANDOFF「已知偶发/待修」。第 1 次 FAIL 计（上限 3 轮）。

## 接管轮追加简报（2026-11-15，主控）

**验收 FAIL 的根因已定位**：全量 PlayMode 中，前序用例（`AffordanceScenePlayModeTests` / `SurvivalLoopPlayModeTests` 等）
`LoadSceneAsync("SCN_Game")` 后未卸载，探针帧被遗留场景占据（`renderer=54 可读=6` vs 隔离时 `renderer=0`），
绿域锚因此 0.0 —— 锚是对的，**取证环境不干净**。`depth=-1000` 只解决相机排序，不解决场景泄漏。

**本轮只改测试，不改管线**（白名单：`ZrPlanetProbePlayModeTests.cs`）：

1. 探针 fixture 启动时执行**显式隔离**：
   - 新建空场景并 `SetActiveScene`（`SceneManager.CreateScene("~ProbeEmpty")`）；
   - 把其余已加载场景里的根对象**销毁/禁用**（只保留探针自己的宿主与相机）；
   - 除探针相机外，其余 `Camera.allCameras` 一律 `enabled=false`（不依赖 depth 排序）；
   - 断言隔离结果：`FindObjectsOfType<Renderer>().Length` 为 0（或仅探针自身），否则直接 `Assert.Fail` 并打印残留对象名——**环境不干净就要红，不许静默继续**。
2. fixture 结束时恢复：销毁探针场景/相机，避免污染后续用例。
3. 补「施工报告」节（四小节），并记录本次隔离改动的实测数字（隔离前 renderer=54 / 隔离后 0）。

**完成判据**：
- `Tools/run_tests.ps1 -Segments playmode` **全量 115/115**（连跑两轮同形）；
- EditMode 1235 不回归；
- 探针 5 条不再红，且「环境不干净」时能以明确信息失败（可故意加一个脏对象验证一次，再撤销）。

## 施工报告（接管轮）

### 1. 摘要

按接管轮追加简报只动 `ZrPlanetProbePlayModeTests.cs`：把「探针取证环境」从"靠 depth 排序碰运气"改成 fixture 的**前置硬条件**——`[UnitySetUp]` 先建空场景 `~ProbeEmpty` 并置活动场景，销毁其余已加载场景的根对象（全量上下文实测 22 个根 / 57 个 renderer），关掉除探针相机外的全部相机，然后**断言全局非探针 Renderer 数必须为 0**，不满足就 `Assert.Fail` 并点名残留对象与所属场景（环境不干净必须红，不再静默取证）。`[UnityTearDown]` 拆掉隔离场景，不污染后续用例。全量 PlayMode 两轮 **115/115**（同形）、EditMode **1235/1235** 不回归；脏环境变异实测按预期以明确信息变红。

关键时序：隔离必须发生在**切换 ZRender 管线之前**——管线实例首次 `Render` 才 `Rescan` 一次场景（`ZRenderSceneBridge.Rescan`，之后 120 帧才补扫），泄漏物件若在首次扫描时还在，会整场留在提交表里，事后销毁救不回来。

### 2. 改动清单

只改 `UnityProject/Assets/Game/Tests/PlayMode/ZrPlanetProbePlayModeTests.cs`（无新增文件、无其他测试/管线/Native 改动）：

| 位置 | 改动 |
|---|---|
| `using` | 追加 `UnityEngine.SceneManagement` |
| 字段区 | `ProbeSceneName="~ProbeEmpty"` / `ProbeObjectPrefix="~PlanetProbe"` / `_probeScene` / `_probeSceneOwned` / `_isolationSummary` |
| `SetUpProbe` | D3D12 门控之后、**切管线之前** `yield return IsolateEnvironmentAsync()`；`BuildRig()` 之后 `DisableForeignCameras()` + `AssertEnvironmentClean("setup")` |
| `TearDownProbe` | 末尾 `yield return UnloadProbeSceneAsync()`（先让 rig 销毁生效一帧，再卸场景） |
| 新方法 | `IsolateEnvironmentAsync`（建空场景 + SetActive + 销毁遗留根）、`DisableForeignCameras`、`AssertEnvironmentClean`、`UnloadProbeSceneAsync`、`IsProbeObject` / `IsPersistentScene` / `HasVisualContent` 三个纯判定 |
| 文件头 | 追加「接管轮」段：depth 只解决相机排序、场景泄漏才是根因 |

隔离实现口径（三条硬约束，都不依赖 depth）：

1. **空场景**：`SceneManager.CreateScene("~ProbeEmpty")` + `SetActiveScene` —— 此后 `new GameObject`（rig/第二相机）都落在探针场景里，探针物件与"遗留物件"按 `scene` 天然可分。
2. **清根**：只遍历 `Transform.parent == null` 的根（子物件随根一起销毁，避免逐个 Destroy 抖动）；跳过探针自身（`~PlanetProbe` 前缀）与 **DontDestroyOnLoad 里无渲染物的持久管理器**（框架单例，不是污染源，误删会打坏后续用例）；带 `Renderer`/`Camera` 的持久根一律销毁。`Destroy` 延迟到帧末，故隔离协程末尾 `yield return null` 两帧后才断言（否则断言看到的还是"尚未消失"的旧状态）。
3. **关相机**：用 `FindObjectsOfType<Camera>(true)` 而不是 `Camera.allCameras`（后者只含 enabled 相机，已禁用的泄漏相机也要按住，防后续系统把它打开）。

断言两条：① 非探针 `Renderer` 数 == 0（`FindObjectsOfType<Renderer>(true)`，含未激活；失败时逐条打印 `类型 '对象名'（场景='场景名'）`，上限 4000 字符）；② 启用相机只能是探针相机。两条都在 SetUp 里做一次；双相机用例后续自建第二相机不受影响（那时不清场也不重复断言）。

### 3. 自测数字

环境备注（同验收结论）：本机进程环境有 `HTTPS_PROXY`/`https_proxy` 大小写重复，`run_tests.ps1` 经 `Start-Process` 起 Unity 会直接失败——本轮每条命令都在**同一个 PowerShell 进程内先删掉全部 `*proxy*` 变量并做大小写去重**再跑（非代码问题）。Unity 批处理纪律照旧（先查 `Unity.exe`、删 `UnityProject/Temp/UnityLockfile`、以 xml 为判据）。

| 证据 | 命令 | 结果 |
|---|---|---|
| 隔离场景数（全量套件上下文） | `TempLogs/bp15_takeover_pm_run1.xml` / `bp15_takeover_pm_run2.xml` | 首个探针用例 **隔离前 renderer=57，销毁根=22**；隔离后 **renderer=0 / 非探针残留=0 / 启用相机=1**。后续 4 条用例 隔离前 renderer=0（SCN_Game 的根已被首条清空） |
| 隔离 fixture 单跑 | `-testFilter Game.View.PlayMode.Tests.ZrPlanetProbePlayModeTests`，`TempLogs/bp15_takeover_fixture.xml` | **total=5 passed=5 failed=0**（94.5s）；隔离前 renderer=0 / 销毁根=0（本来就干净，与验收"独跑 5/5 绿"一致） |
| 内容锚（逐位同验收基线） | 同上 xml | near 0.5472、far 0.1166、walk1k/5k/50k:before 0.5472、dualcam-base 0.5472；dualcam 段 `moved=0.000000 meanAbs=0.0000 green=0.5472 secondDepth=-999`；多相机会话日志恰 1 行 |
| **全量 PlayMode 第 1 轮** | `Tools/run_tests.ps1 -Segments playmode` | **total=115 passed=115 failed=0 skipped=0**（177.4s，`TempLogs/bp15_takeover_pm_run1.xml`） |
| **全量 PlayMode 第 2 轮** | 同上 | **total=115 passed=115 failed=0 skipped=0**（176.9s，`TempLogs/bp15_takeover_pm_run2.xml`）——两轮同形（隔离前 57/22、绿域 0.5472/0.1166、多相机日志 1 行均一致） |
| **EditMode** | `Tools/run_tests.ps1 -Segments editmode` | **total=1235 passed=1235 failed=0 skipped=0**（13.7s，`TempLogs/runtests_editmode.xml`）——基线 1235 不回归 |
| **脏环境变异（门牙验证）** | 临时在 `AssertEnvironmentClean("setup")` 之前 `GameObject.CreatePrimitive(Cube)` 命名 `~TEMP_DIRTY_CUBE`，单条用例 `-testFilter ...Probe_Walk1km_...` | **total=1 passed=0 failed=1**（1.5s，`TempLogs/bp15_takeover_dirty.xml`）：`环境隔离失败（setup）：…现存 1 个非探针 Renderer…残留对象：- MeshRenderer '~TEMP_DIRTY_CUBE'（场景='~ProbeEmpty'）Expected: 0 But was: 1`，栈顶 `AssertEnvironmentClean`——明确信息 + 点名对象/场景，符合判据；**该临时对象已撤销**（`grep -n "TEMP-DIRTY\|~TEMP_DIRTY"` 无命中） |

### 4. 风险

1. **隔离是破坏性操作**（销毁其他场景根对象）：若将来某用例依赖前序遗留的场景内容（例如不自己 `LoadSceneAsync` 就直接用 `SCN_Game` 里的物件），会被本轮隔离清掉并变红——这是**有意的**（脏环境必须暴露），但也意味着新增此类用例时须自带场景。本轮两轮全量 115/115 说明现有套件均自建场景，无此依赖。
2. **DontDestroyOnLoad 的取舍**：无渲染物的持久管理器**保留**（避免误伤框架单例），有渲染物/相机的**销毁**。判据是"是否污染画面"而非"归属哪个用例"；若将来出现"持久且必须在场的渲染物"（如常驻后处理相机），它会被销毁——但下一次取证会立刻暴露缺失，不会静默。
3. **断言只做一次**（fixture 启动、取证之前）：若某用例在 SetUp 之后、取证之前又往场景里塞渲染物，本轮不会红（避免逐帧 `FindObjectsOfType` 的代价）。当前 5 条用例里只有双相机用例加一台带 RT 的第二相机，而它被管线显式跳过，不属污染。
4. **TearDown 卸载场景的条件**：`SceneManager.sceneCount > 1` 才卸（Unity 不允许卸载最后一个场景）；只剩探针场景时保留空场景，对后续用例等价于空场（实测 teardown 后 `剩余场景数=1`，即只留 DontDestroyOnLoad）。
5. **`ProbeCameraDepth=-1000` 保留**：场景隔离已使相机排序不再是取证前提，但 depth 仍是"探针恒居 cameras[0]"的第二道保险（双相机用例的语义靠它），不回退。

## 复验结论（2026-11-15 接管轮 II）

**总结论：PASS（可闭环）**。独立复跑全量 PlayMode 两轮 **115/115 同形** + 恢复后第三轮 115/115，EditMode **1235/1235**；I1/I2/I3 三项成立；变异（只删"销毁遗留根"一个动作）全量 **110/115 必红**、逐字节恢复后复绿。

> 本轮数字全部为复验代理亲跑亲写（Unity 批处理独占，2026-11-15 02:02—02:20 本地时段），不采信任何既有报告数字。取证纪律：每条命令前置清空 `*proxy*` 大小写重复变量（本机 `HTTP_PROXY`/`http_proxy` 并存会让 `Start-Process` 构造环境块失败）+ 删 `UnityProject/Temp/UnityLockfile`；证据一律以 `TempLogs/*.xml` 为准。环境备注：本轮每次启动 `run_tests.ps1` 都打印 `[warn] cannot delete previous results.xml`（本机 safe-delete trash 失败），但脚本指纹判据均给出 `evidence: results.xml written by this run`，未复用旧文件，取证有效；此为本机工具链环境问题，非本任务代码问题。

### I1 隔离有效性 —— PASS

- **隔离后计数为 0**：5 个探针会话全部打印 `isolate(setup): renderer=0 非探针残留=0 启用相机=1 场景数=2 探针场景='~ProbeEmpty'`（两轮逐字相同）。首条用例为 `isolate(pre): 隔离前 renderer=57 销毁根=22 保留持久管理器=17`，其余 4 条 `隔离前 renderer=0`（SCN_Game 的 22 个根已被首条清空）——独立复现了施工报告"隔离前 57 / 销毁 22 / 隔离后 0"。
- **全量下 5 条探针不再红**：两轮均 `total=115 passed=115 failed=0`；探针 5 例固定位于套件第 31—35 位，两轮的 `fullname + result` 序列**逐项相等**（脚本比对 `identical-order-and-results: True`）。
- **内容锚实测与上轮基线逐位一致**：near / dualcam-base / walk1k:before / walk5k:before / walk50k:before `greenFrac=0.5472`（floor 0.15），far `0.1166`（floor 0.04）；`dualcam: moved=0.000000 meanAbs=0.0000 green=0.5472 secondDepth=-999`；多相机日志恰 1 行（`cameras[0]='~PlanetProbeCamera'`，显式跳过 `'~PlanetProbeSecondCamera'`）。

### I2 隔离不伤邻 —— PASS

- **拆场**：每个探针用例结束均打印 `isolate(teardown): 已卸载 '~ProbeEmpty'，剩余场景数=1`（5/5）；探针相机/宿主/驱动由 `DestroyRig()`（:1174）销毁，1080p/证据 RT 与管线资源在同段 teardown 释放。
- **邻接**：探针之后的用例 **#36—#115 全部 Passed**（AddressablesSmoke / AssemblyConnect 族 / BootChain / 像素证据族 / RuntimeDevConsole / Touch / UI / UtilityNpc 等 80 例），探针之前的 #1—#30 亦全绿。
- **保留口径**：DontDestroyOnLoad 中无渲染物的持久管理器保留 17 个（全量）/13 个（单例跑），后续用例未因误删单例而失败——与施工报告一致。

### I3 诚实性 —— PASS（读码 + 亲造脏对象）

- **读码**：`AssertEnvironmentClean`（:414—462）两条硬断言——① `Assert.AreEqual(0, foreign, ...)`：非探针 `Renderer` 数必须为 0，失败逐条打印 `类型 '对象名'（场景='场景名'）`（上限 4000 字符）；② `Assert.AreEqual("<none>", foreignCamera, ...)`：除探针相机外不得有启用相机。调用点 `:279`（装配后、取证前），**不存在"打日志后继续"的分支**。
- **亲造脏对象**：在 `AssertEnvironmentClean("setup")` 之前 `CreatePrimitive(Cube)` 命名 `~TEMP_DIRTY_CUBE_VERIFIER`，单例 `-testFilter ...Probe_Walk1km_...` → **total=1 passed=0 failed=1**（`TempLogs/verify2_dirty.xml`，38s），消息「环境隔离失败（setup）：…现存 1 个非探针 Renderer…残留对象：`- MeshRenderer '~TEMP_DIRTY_CUBE_VERIFIER'（场景='~ProbeEmpty'）`」，栈顶 `AssertEnvironmentClean (System.String phase)`。
- **撤销后复绿**：同 filter → **total=1 passed=1 failed=0**（`TempLogs/verify2_dirty_reverted.xml`），测试文件 sha256 回到 `b735781e…`（与注入前逐字节一致，`MUTATION/TEMP_DIRTY` 残留计数 0）。

### 变异记录（复验代理独立做）

- **变异点**：`ZrPlanetProbePlayModeTests.cs:370` 删掉 `UnityEngine.Object.Destroy(go);`——只跳过"销毁遗留场景根"这一个隔离动作，断言与其他逻辑不变。
- **红**：全量 `Tools/run_tests.ps1 -Segments playmode` → **total=115 passed=110 failed=5**（Unity exit=2，`TempLogs/verify2_mutant_skipdestroy.xml`）。5 红全部是 `ZrPlanetProbePlayModeTests`，消息一致：「环境隔离失败（setup）：…现存 57 个非探针 Renderer…残留对象：ParticleSystemRenderer 'AmbientDust'（场景='SCN_Game'）、MeshRenderer 'StateLabel'（场景='SCN_Game'）…」，栈顶 `AssertEnvironmentClean`——同时证明隔离动作**承重**、且脏环境走的是"明确失败并点名对象/场景"而非静默取证。
- **恢复**：`cp` 备份回写 + `cmp` 逐字节一致 + sha256 = `b735781eea54784531fb90320cfd170200b2409df5e4bc44e45accce841e93bc`（与变异前记录一致）。
- **复绿**：全量 PlayMode → **total=115 passed=115 failed=0**（177.53s，`TempLogs/verify2_pm_restored.xml`）。

### 独立复跑数字

| 轮次/段 | 结果 | xml 时长 | 证据（sha256 前 8 位） |
|---|---|---|---|
| PlayMode 第 1 轮 | **total=115 passed=115 failed=0 skipped=0** | 174.4081436s | `TempLogs/verify2_pm_run1.xml`（eb2761b0） |
| PlayMode 第 2 轮 | **total=115 passed=115 failed=0 skipped=0**（与第 1 轮同形） | 174.4134295s | `TempLogs/verify2_pm_run2.xml`（6d1ce22f） |
| PlayMode 变异轮 | total=115 passed=110 failed=5 | — | `TempLogs/verify2_mutant_skipdestroy.xml` |
| PlayMode 恢复轮 | **total=115 passed=115 failed=0** | 177.5282938s | `TempLogs/verify2_pm_restored.xml`（e3cccdec） |
| EditMode | **total=1235 passed=1235 failed=0 skipped=0** | 13.3422475s | `TempLogs/verify2_em.xml`（9b545bfc） |
| 单例脏对象 / 撤销 | 0/1 failed → 1/1 passed | 38s / 48s | `verify2_dirty.xml` / `verify2_dirty_reverted.xml` |

### 零改动核验

- `git diff --name-only` 仍为 4 条：`ZrPlanetProbePlayModeTests.cs`、`ZRenderPipeline.cs`、`Tools/version.json`、`ProjectSettings.asset`。
- **`ZRenderPipeline.cs` 接管轮零改动**：sha256 = `02e861bfb77837541c7faa7840149379586345a532de9ac27579773bb271b17b`（与上一轮验收记录及上轮变异恢复值一致），mtime 01:38 早于接管轮施工（测试文件 01:52）；diff 内容仍只有上轮"多相机显式跳过日志"25 行。
- **`Native/**` 零改动**：`git diff --name-only -- Native UnityProject/Assets/Native` 为空。
- **工作区噪声**：5 张 `Docs/Parity/*.png` 被测试重写（crowd_r9_15_off/on、npc_attitude_divergence、utility_personality_divergence、utility_personality_goap_chain），已 `git checkout --` 还原；本轮未改代码、未 commit。

### 受限（如实登记）

1. **断言只做一次**（SetUp 内）：SetUp 之后、取证之前若再塞渲染物不会被抓——现套件无此形态（双相机用例只加一台被管线显式跳过的相机），属判据边界。
2. **探针识别靠名前缀** `~PlanetProbe`：将来若出现同前缀的非探针物件会被误豁免。
3. **隔离是破坏性操作**：会销毁其他用例遗留的场景根（含带渲染物的持久根）。115/115 证明现套件均自带场景，但新增"依赖前序遗留场景"的用例会红（有意设计）。
4. **仅 D3D12 编辑器当场**：非 D3D12 走 `Assert.Ignore`；Player 现场未覆盖（与既有口径一致）。
5. `run_tests.ps1` 的 results.xml 删除告警（safe-delete fail-closed）依赖指纹判据兜底，建议运维侧知悉。

**是否可闭环：是。** 完成判据三条全部达成——① 全量 PlayMode 115/115 连跑两轮同形（另有恢复后第三轮 115/115）；② EditMode 1235/1235 不回归；③ 探针 5 条不再红，且环境不干净时以明确信息 `Assert.Fail`（亲造脏对象验证一次并撤销，sha256 证明代码复原）。提交建议：本任务两文件（测试 + 管线）一笔 `TASK-B-PLANET-15`，`Tools/version.json`/`ProjectSettings.asset` 按前序构建副作用单列 chore。
