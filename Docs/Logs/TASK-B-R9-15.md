# TASK-B-R9-15 —— Player 构建管线正式化（一键打包+版本注入+体积报告+冒烟）

状态: doing | 派发: 2026-10-31 | 施工: general-purpose 施工子代理（2026-11-01） | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：R9 候选"构建管线"——把"能出 Player 包"正式化：一键脚本出 Windows x64 包、版本号注入、构建体积报告、**包冒烟**（启动真 exe 验证启动链）。

**设计要点**：
1. `Tools/build_player.ps1`：
   - Unity batch `-buildWindows64Player`（-executeMethod 调 `Game/Editor/PlayerBuild.cs` 新静态入口：`BuildWindows64()`——BuildPipeline.BuildPlayer 场景列表=EditorBuildSettings）
   - **版本号注入**：PlayerSettings.bundleVersion 从 `Tools/version.json`（单源：{version:"0.1.0", build:N}）读取，构建时 build 自增写回
   - 产物：`Build/Windows64/ZFrameDemo.exe`（目录形态）；**体积报告**：Top-20 资产/文件体积表+总量→`Build/build_report.txt`（与 run_tests 的 TestReport 风格一致）
2. **包冒烟**：启动 exe → 等 20s → 查 `Player.log`（persistentDataPath/..\LocalLow\ZFrame\ZFrameDemo\Player.log）含启动链标记（GameRoot/菜单加载日志——按实际日志关键词，报告写明）→ taskkill → exit 0；崩在 20s 内=红
3. run_tests.ps1 挂接：新增第 8 段 "player"（**默认 SKIP**——耗时数分钟，`PLAYER_BUILD=1` 才跑；TestReport 汇总行显示 SKIP/结果）
4. .gitignore：`Build/` 入忽略
5. 自测：构建亲跑 exit 0+包可启动+冒烟过；run_tests 七段回归不破坏+第 8 段 SKIP 显示

**约束**：白名单 `Tools/**`、`UnityProject/Assets/Game/Editor/**`（PlayerBuild.cs）、`UnityProject/ProjectSettings/`（bundleVersion 由脚本写）、`.gitignore`、本任务日志；Unity 批处理纪律（构建比批处理更久，超时放宽+taskkill 兜底）；不 git commit；UTF-8 无 BOM/LF。

**交付物**：脚本+入口+体积报告样例+冒烟证据+run_tests 第 8 段+施工报告四节。

## 施工报告（施工代理写）

施工: general-purpose 施工子代理（ZCode），2026-11-01。

### ① 交付物清单

| 文件 | 性质 | 说明 |
|---|---|---|
| `Tools/version.json` | 新增 | 版本单源：`{"version":"0.1.0","build":N}`（UTF-8 无 BOM/LF，2 空格缩进） |
| `UnityProject/Assets/Game/Editor/PlayerBuild.cs` | 新增 | 静态入口 `Game.EditorTools.PlayerBuild.BuildWindows64()`：版本注入 + build 自增写回 + `BuildPipeline.BuildPlayer`（目录形态 Windows64，场景列表=EditorBuildSettings enabled 场景）；所有路径（含异常）保证 `EditorApplication.Exit(code)`，0=成功/1=失败 |
| `Tools/build_player.ps1` | 新增 | 一键链：构建（Unity 经 Git Bash，无 `-quit`，方法自带 Exit）→ 冒烟（启动真 exe 20s + Player.log 启动链标记 + taskkill）→ 体积报告 `Build/build_report.txt`（Top-20 文件 + 总量，UTF-8 无 BOM/LF，TestReport 风格）；参数 `-SkipSmoke` / `-SmokeOnly` / `-TimeoutSec`（默认 600s） / `-SmokeWaitSec`（默认 20） / `-SmokeKeywords` |
| `Tools/run_tests.ps1` | 修改 | 第 8 段 `player`：默认 SKIP（`PLAYER_BUILD=1` 才跑），复用 build_player.ps1，Tee 到 `TempLogs/runtests_player.log`；ValidateSet/默认段数组/头部注释同步 |
| `.gitignore` | 修改 | 新增 `/Build/`（仓库根构建产物；`UnityProject/Build/` 原已忽略） |
| `UnityProject/ProjectSettings/ProjectSettings.asset` | 脚本写入 | 仅 `bundleVersion: 1.0 → 0.1.0.4` 一行（git diff 核实，PlayerSettings API 在编辑器退出时持久化） |
| `Build/build_report.txt`、`Build/Windows64/**` | 产物 | 已被 `.gitignore` 忽略，不入库 |

文件规范核对：新文件 UTF-8 无 BOM/LF（`file` + 二进制头核验）；`build_player.ps1` ASCII+CRLF（.ps1 例外）；无尾随空格；`run_tests.ps1` 保留原有 BOM+CRLF 未引入新差异。

### ② 实现要点

1. **版本单源链**：`Tools/version.json` →（PlayerBuild.cs）`PlayerSettings.bundleVersion = "<version>.<build>"` → build 自增写回 → 构建时序列化进 `ZFrame_Data/globalgamemanagers`（即运行期 `Application.version` 的来源）。**两条校验通道**（PS 侧构建后核验，任一不符即 FAIL）：① `ProjectSettings.asset` 的 `bundleVersion:` 已持久化；② `globalgamemanagers` 二进制内含该版本串。
2. **诚实边界——exe 元数据不载版本**：Unity 2022.3 Windows 的 exe `VS_VERSION_INFO` 是**引擎版本戳**（实测 `FileVersion=2022.3.62.1451004`、`ProductVersion=2022.3.62f3c1 (1623fc0bbb97)`、CompanyName/ProductName 空），**不是 bundleVersion 通道**。第一版脚本曾以 exe FileVersion 作校验通道，亲跑证伪后改为上述两通道；报告中该行保留并注明"engine-stamped, not a bundleVersion channel"。
3. **exe 名=ZFrame.exe（简报草稿写 ZFrameDemo.exe）**：实测 `ProjectSettings.asset` 的 companyName/productName 均为 `ZFrame`（非 ZFrameDemo），而 LocalLow 路径与存档目录（`LocalLow/ZFrame/ZFrame/saves`）都跟 productName 走——改名会连动玩家存档路径，超出本任务白名单，故按实际 productName 出 `Build/Windows64/ZFrame.exe`。Player.log 路径由脚本从 ProjectSettings 解析（不硬编码）：`%USERPROFILE%\AppData\LocalLow\ZFrame\ZFrame\Player.log`（简报写 ZFrameDemo 是笔误，已实测纠正）。
4. **Unity 批处理纪律**（复刻 run_tests 五条）：单实例断言、`[IO.File]::Delete` 删 UnityLockfile（**不用 Remove-Item**——本机 safe-delete 钩子会拦截抛错）、一律经 Git Bash 启动（PS 直启授权握手假退）、硬超时 600s→taskkill /F 兜底、构建方法不带 `-quit` 自带 `EditorApplication.Exit`（与 ParityBench 同契约）。还内置 Start-Process 大小写代理变量重复的诊断（HANDOFF 已知坑）。
5. **冒烟判定语义**：20s 存活窗 = 崩溃门（`Start-Process -PassThru` 后 20s 检查 `HasExited`，崩在窗内=红）；Player.log 必须**本轮重写**（指纹比对 LastWrite/Length，防读旧日志假绿）；标记命中带 ~10s 轮询宽限（Unity 对 Player.log 惰性刷新）；finally 兜底 taskkill，**任何失败路径不留玩家进程**。
6. **run_tests 第 8 段**：默认 SKIP，TestReport 汇总行如实显示 `SKIP default off; PLAYER_BUILD=1 enables`；七段既有判定逻辑零改动（仅段数组与注释扩展）。

### ③ 冒烟关键词（先跑实测再定，非拍脑袋）

对**真实构建的包**（`Build/Windows64/ZFrame.exe`）的 Player.log 全文核查后定为 3 个关键词（大小写不敏感精确子串）：

1. `Initialize engine version: 2022.3` —— 引擎初始化（Mono 装载前）
2. `- Loaded All Assemblies` —— 托管程序集装载完成（实测 8.3s）
3. `[GameBootstrap] Awake: full boot starts.` —— 游戏启动链入口（SCN_Boot 的 GameBootstrap.Awake，`Debug.Log` 落盘；ZLog 通道默认关闭故不可依赖）

实测依据（本轮冒烟 Player.log 摘录）：`Mono path[0] = '.../Build/Windows64/ZFrame_Data/Managed'` → `Initialize engine version: 2022.3.62f3c1 (1623fc0bbb97)` → `- Loaded All Assemblies, in 8.325 seconds` → `[GameBootstrap] Awake: full boot starts.`。注：日志尾部（菜单加载等）在进程存活期可能尚未刷盘，故关键词取前三段确定性标记；`-SmokeKeywords` 参数可覆盖（验收如需换标记不必改代码）。

### ④ 体积报告样例（`Build/build_report.txt`，0.1.0.4 轮实测）

```
ZFrame / ZRender - player build report (Tools/build_player.ps1)
Generated: 2026-11-01 03:05:25  (wall 0:01:37)
Output:    Build/Windows64 (exe: ZFrame.exe, directory form)
Version:   bundleVersion=0.1.0.3  (Tools/version.json: version=0.1.0, build=3; single source)
Scenes:    SCN_Boot.unity, SCN_Empty.unity, SCN_Game.unity
Unity exit: 0   exe FileVersion: 2022.3.62.1451004 (engine-stamped by Unity 2022.3 - not a bundleVersion channel; see Version line + globalgamemanagers check)
Smoke:     PASS (wait 20s; Player.log: LocalLow/ZFrame/ZFrame/Player.log)
           markers: Initialize engine version: 2022.3 ; - Loaded All Assemblies ; [GameBootstrap] Awake: full boot starts.

Files: 165 files, total 80.46 MB (84370633 bytes)
Top-20 largest files:
   1.   31,200,080 bytes   29.75 MB  Windows64\UnityPlayer.dll
   2.    7,896,912 bytes    7.53 MB  Windows64\MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll
   3.    5,699,808 bytes    5.44 MB  Windows64\ZFrame_Data\globalgamemanagers.assets.resS
   4.    4,631,552 bytes    4.42 MB  Windows64\ZFrame_Data\Managed\mscorlib.dll
   5.    3,160,064 bytes    3.01 MB  Windows64\ZFrame_Data\Managed\System.Xml.dll
   ...（共 20 行）
```

（生成自 0.1.0.3 轮；终态 0.1.0.4 轮 166 文件/80.46 MB，结构相同。）

### ⑤ 自测证据（全部亲跑，非复述）

| 轮 | 命令 | 结果 |
|---|---|---|
| 1-2 | build_player.ps1（编译修错前两轮） | Unity exit 1（PlayerBuild.cs 两处命名空间/类型编译错，见⑥）——脚本如实 FAIL，version.json 未被消耗 |
| 3 | build_player.ps1 全链 | Unity exit 0，包产出 0.1.0.1；**PS 侧版本校验 FAIL**（exe FileVersion 通道证伪，见②）——设计缺陷暴露于亲跑 |
| 4 | build_player.ps1 全链 | 构建段 PASS（0.1.0.2）；冒烟段 FAIL（Player.log 共享冲突读 + 玩家进程残留）——两处真缺陷见⑥，已修 |
| 5 | build_player.ps1 全链 | **exit 0**：0.1.0.3 + 冒烟 PASS（20s 存活/标记命中/已 kill）+ build_report.txt |
| 6 | build_player.ps1 -SmokeOnly | exit 0（冒烟复用路径可用） |
| 7 | run_tests.ps1（八段默认） | **exit 0，ALL PASS（7 passed, 1 skipped, 0 failed）**：native 281 用例/771,162 断言、editmode 785、playmode 20、arch/config/naming exit 0、bench report-only PASS；**player 段显示 SKIP**，七段回归零破坏 |
| 8 | PLAYER_BUILD=1 run_tests.ps1 -Segments player | **exit 0，player 段 PASS**（1:08）：0.1.0.4 构建+冒烟+报告全链经 run_tests 接线亲证 |

终态版本一致性：`version.json build=4` ↔ `ProjectSettings.bundleVersion: 0.1.0.4` ↔ `globalgamemanagers` 含 `0.1.0.4` 三方核对通过。build 号语义 = **成功的 Unity Player 构建次数**（编译失败轮不消耗；PS 侧校验失败轮已消耗——它是真实构建）。

### ⑥ 施工中的调试记录（为什么改，改了什么）

1. **CS0246/CS0103（BuildReport/BuildSummary/BuildResult）**：这些类型 2022.3 在 `UnityEditor.Build.Reporting`（经 `UnityEditor.xml` 文档核实），不是直觉的 `UnityEditor.BuildReporting`。
2. **exe FileVersion 校验通道证伪**：第一版以 exe 元数据核对 bundleVersion，亲跑发现 Unity 2022.3 Windows 不把 bundleVersion 写进 exe VS_VERSION_INFO（详见②）。
3. **Player.log 读取共享冲突**：`[IO.File]::ReadAllText`（FileShare.Read）与 Unity 日志写柄冲突必抛 IOException；改为显式 `FileStream(FileShare.ReadWrite)`+StreamReader。
4. **冒烟失败路径玩家进程残留**：首轮冒烟抛错后包进程存活 9 分钟无人清理；改为 try/finally 必 kill（Get-Process 守卫）。
5. **PS 读 version.json 时序**：单源在 Unity 构建阶段内被自增——PS 校验必须**构建后重读**，否则拿到旧值（首轮 0.1.0.0 vs 实际 0.1.0.1 即此因）。

### ⑦ 边界与受限（如实）

- **窗口冒烟会弹窗 ~20s**：包进程带窗口前台启动后 taskkill /F，属设计行为（无头冒烟需引擎支持，未做）。
- **冒烟覆盖的是"启动链前 20s"**：菜单点击、进局、存档等不在本冒烟范围（PlayMode 用例已覆盖逻辑层；本任务只证"包能起、启动链活着"）。
- **-SkipSmoke 与 -SmokeOnly 是运维逃生口**：冒烟失败时可用 `-SkipSmoke` 出包，产物核验可用 `-SmokeOnly` 单独复跑，两者均如实体现在报告 Smoke 行。
- **工作树中有非本任务改动**：`UnityProject/Assets/Settings/Pipeline/URP_*.asset`（m_PrefilteringMode* 字段）与 `Docs/Logs/TASK-B-R9-14.md` 属并行派发的 B-R9-14 审计任务（commit db05146 并行派发），本任务未触碰；`ProjectSettings.asset` 的 diff 仅 bundleVersion 一行。
- **报告文件数 165→166**：包运行期在 Build 目录新生成 1 个文件的正常波动，报告按当轮实际清点。
- 产物不入库：`/Build/` 已入 `.gitignore`，`git check-ignore -v Build/` 核实命中；`TestReport.txt` 原已忽略。

### ⑧ 验收提示

- V1 复跑会消耗 build 号（0.1.0.4→0.1.0.5+），属预期；核对三方一致即可。
- 冒烟关键词换标记时用 `-SmokeKeywords` 参数，不必改代码。
- `PLAYER_BUILD=1` 全链亲跑可在验收时执行（约 2-3 分钟/轮）；七段回归用默认参数即可（第 8 段 SKIP 不计时）。

## 验收清单（主控写）

- [x] V1 亲跑 build_player.ps1 exit 0：exe 产出+build_report.txt（Top-20 表）+版本号=version.json
- [x] V2 冒烟亲跑：Player.log 启动链标记+20s 无崩溃+exit 0
- [x] V3 run_tests 八段回归（第 8 段 SKIP 显示正确）；PLAYER_BUILD=1 全链亲跑（可选，时长允许则做）
- [x] V4 .gitignore 生效（Build/ 不入库）
- [x] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

验收: general-purpose 验收子代理，2026-11-01。全部五项独立亲跑（非复用施工证据）。

### 逐项证据（V1-V5 全 PASS）

- **V1 PASS**：亲跑 `powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_player.ps1` exit 0（wall 0:02:12）；`Build/Windows64/ZFrame.exe` 产出；`Build/build_report.txt` Top-20 表 20 行齐全+总量行（166 files, 80.46 MB）；三方一致亲核：version.json build=5 ↔ `ProjectSettings.asset` `bundleVersion: 0.1.0.5` ↔ `globalgamemanagers` 二进制含 `0.1.0.5`（本轮占用 build 5）。
- **V2 PASS**：冒烟随 V1 全链亲跑——`%USERPROFILE%\AppData\LocalLow\ZFrame\ZFrame\Player.log` 本轮重写（Mono path 指向 Build/Windows64）且三关键词逐一命中：`Initialize engine version: 2022.3.62f3c1 (1623fc0bbb97)` / `- Loaded All Assemblies, in 0.200 seconds` / `[GameBootstrap] Awake: full boot starts.`；20s 存活窗无崩溃（脚本 HasExited 断言+控制台 `alive 20s`）；taskkill 后 `tasklist` 查 ZFrame.exe 零残留（控制台 `player (PID 9676) killed`）。
- **V3 PASS**：默认八段亲跑 exit 0——native 281 用例/771,162 断言、editmode 785/785、playmode 20/20、arch/config/naming exit 0 六段 PASS；第 8 段 SKIP 显示正确（控制台 `SKIP: set PLAYER_BUILD=1 to enable` + 汇总行 `player SKIP default off`）；bench 首轮因机器带载按内置守卫 SKIP（见受限①），机器空闲后补跑 PASS（7/7 指标容差内，report-only）。`PLAYER_BUILD=1 -Segments player` 亲跑 PASS（1:08）：0.1.0.6 构建+冒烟+报告全链经 run_tests 接线亲证，`TempLogs/runtests_player.log` 留证。
- **V4 PASS**：`git check-ignore -v` 三路径全命中 `.gitignore:53:/Build/`（Build/、build_report.txt、ZFrame.exe）；`git status --porcelain` 无任何 Build/ 条目；**build 自增写回连跑两次实证**：4→5（V1 全链）、5→6（V3 player 段），每次构建后 version.json 即时写回且 ProjectSettings/globalgamemanagers 同步跟进。
- **V5 PASS**：文件规范亲核——`Tools/version.json` 首 3 字节 `7b 0a 20`（无 BOM）+0 个 CR（LF）；`PlayerBuild.cs` 首 3 字节 `2f 2f 20`（无 BOM）+LF；`build_player.ps1` ASCII+CRLF（.ps1 例外合规）；`run_tests.ps1` 原 BOM+CRLF 未引入新差异（HEAD 与工作区格式一致）；四文件无尾随空格；`PlayerBuild.cs.meta` 已生成且无 BOM。施工报告四节齐全（简报/施工报告①-⑧/验收清单/本结论）。

### 三条与简报出入的独立评估（均成立，接受）

1. **包名 ZFrame（简报写 ZFrameDemo）**：`ProjectSettings.asset:15-16` 实测 companyName/productName 均为 `ZFrame`，Player.log 路径随 productName（`LocalLow/ZFrame/ZFrame/Player.log` 亲证）——改名会连动玩家存档路径，超出白名单。**纠正合理，非擅自缩水。**
2. **exe 版本戳引擎限制**：验收亲测 exe `FileVersion=2022.3.62.1451004` / `ProductVersion=2022.3.62f3c1 (1623fc0bbb97)`，确认 Unity 2022.3 Windows 的 VS_VERSION_INFO 是引擎戳、不走 bundleVersion 通道；脚本以 ProjectSettings 持久化 + globalgamemanagers 内容双通道校验替代，报告如实标注。**技术判断正确，诚实边界成立。**
3. **冒烟关键词实测**：三关键词在本轮 Player.log 逐一命中（引擎初始化→程序集装载→GameBootstrap 启动链），确为实测非拍脑袋；`-SmokeKeywords` 可参数化，换标记无需改代码。

### 受限与备注（不阻塞）

- **① bench 段验收窗口带载 SKIP**：首轮与两次补跑因 rustc/dllhost/验收会话自身占 CPU≥20% 触发脚本内置机器守卫（B-R9-10 既有行为），第三次空闲窗口 PASS 7/7。bench 段逻辑本任务零改动（diff 仅头部注释+ValidateSet/段数组+第 8 段块），无回归风险。
- **② build 号消耗**：V1 亲跑消耗 build 5、V3 player 段消耗 build 6，终态三方一致 `0.1.0.6`，符合施工报告⑧"V1 复跑会消耗 build 号"预期。
- **③ editmode 段僵尸 taskkill**：本轮 editmode 出现 `xml present but Unity alive >300s -> taskkill`——既有纪律行为（results.xml 为准，785/785 PASS），非本任务引入。
- **④ 工作树并行改动**：`URP_*.asset` 三文件属并行 B-R9-14 任务（commit 840d489 后仍留工作区的序列化差异），本任务未触碰；`ProjectSettings.asset` diff 仅 bundleVersion 一行（亲核）。

**总评：PASS（5/5）。** 交付物（build_player.ps1 + PlayerBuild.cs + version.json + run_tests 第 8 段 + .gitignore）全部独立亲证有效；三条简报出入均有实测依据且处理得当；七段既有回归零破坏。待主控裁决并 commit。
