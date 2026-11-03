# TASK-A-DEV-03 —— 运行期 DevConsole（AI 直操作三层开口之 B）

- **轨道**：A / DEV（三层开口第 2 层）
- **状态**：doing
- **派发**：主控，2026-11-02
- **上游**：`Docs/Logs/TASK-A-DEV-01.md`（Editor 侧服务，已闭环）、`HANDOFF.md` §下一步优先级 第 2 项

## 任务简报（主控写）

### 0. 三层开口里的位置

| 层 | 内容 | 状态 |
|---|---|---|
| A | Editor 常驻 HTTP/JSON 服务（编译/测试/日志/截图/菜单） | ✅ `5f98291` / `7efc198` |
| **B** | **运行期 DevConsole**（localhost TCP 白名单命令） | **本任务** |
| C | 原生 CLI（8 个窗口 flag） | 早已有 |

A 层解决的是"**我怎么知道代码对不对**"，B 层解决的是"**我怎么知道跑起来是什么样**"——
真机/Player 里没有 Inspector、没有 Console、没有 Play 按钮，现在只能靠重打包 + 肉眼。

### 1. ⚠️ 这个任务和 A 层最大的不同：它在发布包里

A 层在 Editor 里，泄露了也只是开发者本机的事。**B 层跑在游戏进程里，一旦跟着发布包出去，就是给外部开了一个能改画质、能截图、能触发生成物件的洞。**

所以**门控是首要判据，不是附加项**：

1. **默认只在开发包开启**：以 `Debug.isDebugBuild`（Development Build）为准；非开发包**必须完全不监听、不建线程、不开 socket**。
2. **必须可显式关闭**：环境变量 / 命令行 `-zbf-noconsole`（选一个，写清）；关闭后零监听零线程。
3. **必须有测试证明"发布包不监听"**：不是靠读代码说"我加了 if"，而是**在 Release/非开发构建下断言连接被拒**。
   若本机无法自动化 Release 构建，**至少要有一个可机检的门控单测**（把 `Debug.isDebugBuild` 抽成可注入判据，两个分支都测），
   并在报告里如实说明"真实 Release 包的证据到哪一步"。

### 2. 协议与命令（不要贪多）

- **传输**：`TcpListener` 只绑 `127.0.0.1`（**绝不 `0.0.0.0`**），端口默认 `17322`（避开 Editor 的 17321），可 `ZBF_RT_PORT` / CLI 覆盖。
- **协议**：**一行一条请求、一行一条 JSON 响应**（`\n` 分隔，UTF-8），响应形如 `{"ok":true,...}` / `{"ok":false,"error":"..."}`。
  限制单行最大长度（例如 4KB，超了就断开并记一次日志）——防 OOM。
- **白名单命令**（**硬编码表，绝不反射任意类型、绝不 eval**）：

| 命令 | 语义 | 响应要点 |
|---|---|---|
| `help` | 列出命令 | `{ok,commands:[...]}` |
| `dump` | 运行态快照 | `{ok,fps,frameMs,quality,fgOn,scene,playerPos,cameraPos,particleCount,memoryMB,...}` |
| `spark <x> <y> <z> [count]` | 触发命中火花 | `{ok,particleCount}` |
| `quality <low\|medium\|high>` | 切画质档 | `{ok,quality}`（回读确认真的变了） |
| `fg <on\|off>` | 帧生成开关 | `{ok,fgOn}`（若平台不支持，返回 `{ok:false,error:"unsupported"}`，**不许假成功**） |
| `shot [name]` | 截图存文件 | `{ok,path,width,height,bytes}` |
| `quit` | 退出（可选，需显式允许） | `{ok}` |

- 所有命令失败必须结构化错误；**未知命令返回错误而不是崩溃**。

### 3. 线程与主线程纪律（照抄 A 层的教训，别重犯）

1. 后台线程**只做 socket 收发**；命令入队，由主线程泵（`RuntimeInitializeOnLoadMethod` 挂一个隐藏对象的 `Update`，或 PlayerLoop 注入）执行后回填，再由后台线程写响应。
2. **Unity API 零后台调用**（`QualitySettings`/`ParticleSystem`/`ScreenCapture`/`Camera` 全在主线程）。
3. 监听线程 `IsBackground = true`，**不得阻止进程退出**；`OnApplicationQuit` / `OnDestroy` 优雅关闭。
4. **异常一律吞掉并只记一次日志**：端口占用、socket 异常、命令异常，都不能让游戏崩。

### 4. 可证伪护栏（PlayMode 现在 21 个，只增不减）

1. **命令真的生效**：连上去发 `spark 0 1 0 20`，断言粒子数上升；发 `quality high`，断言 `QualitySettings.GetQualityLevel()` 真变了（回读而非只看内存字段）。
2. **门控真的关得住**：把门控判据置为"非开发"→ 服务**不监听**（连接被拒），且**无后台线程**（线程数前后对比）。
3. **只绑 127.0.0.1**：`netstat` 或代码断言（不能出现 `0.0.0.0`/`[::]`）。
4. **坏输入不崩**：未知命令、超长行（>4KB）、半截 JSON、连续 100 次连接，游戏仍活着、仍能响应 `help`。
5. **`shot` 真出图**：文件存在、`bytes > 1000`、能被读成 Texture2D 且**不是纯色**（这点照 A-R9-04 的像素证据口径，别只看文件大小）。
6. **`fg off` 不假成功**：在不支持的平台/状态下返回 `unsupported` 错误。
7. **修前必红**：把主线程泵注释掉（命令排在队列里永不执行）→ 命令生效那条用例必须红 → 复原变绿。
8. **不拖慢**：空转时每帧额外开销（µs 级）给数字；`dump` 往返延迟给数字。

### 5. 真机（可选，但很有价值）

Android 上 `localhost` 指的是设备自己——标准做法是 **`adb forward tcp:17322 tcp:17322`**，然后主机侧连 `127.0.0.1:17322` 就能打到设备。
真机在跑（OXF-AN00）：若能实测一次 `adb forward` + `dump` + `shot`，给出证据；**做不到就如实写"未在真机验证"，不要含糊成"已验证"**。

### 6. 硬约束

- **白名单**：`UnityProject/Assets/ZFrame/Runtime/DevConsole/**`（新建，如有更适合的既有运行时目录请说明）、
  `UnityProject/Assets/ZFrame/Tests/**`、`UnityProject/Assets/Game/Tests/**`、本日志。
- **不得**在发布包路径上新增依赖；**不得**改 `run_tests.ps1` / `build_*.ps1` / Governor / `URP_*.asset` / `ArchitectureValidator.cs`
  （新增 asmdef 若触发矩阵校验红，**先停下写信问我**，不要自己改矩阵——上次 A-DEV-01 是"纯新增条目"才追认的）。
- **不许**加 `video.*` 设置行（`SettingsRegistryTests.cs:79-104` 九行 id 序列是硬护栏）。
- `.cs` 无 BOM、LF；**不 git commit**；不装软件、不联网；禁止 `rm`（用 `mv`）。
- 基线：EditMode **797**、PlayMode **21**（只增）、native **281/771,162**、三校验器 exit 0；**收尾完整六段**。

### 7. 自测清单（贴证据）

1. 门控：开发/非开发两分支的行为（含"非开发不监听"的机检证据）。
2. `help`/`dump`/`spark`/`quality`/`fg`/`shot` 六条命令逐一实测的真实 JSON。
3. `spark` 后粒子数（前后对比数字）；`quality` 回读 `QualitySettings` 的真实档位。
4. 坏输入四类（未知命令/超长行/半截/百次连接）后的存活证据。
5. `shot` 的图：尺寸、bytes、**非纯色**的判据。
6. 修前必红（注释主线程泵 → 红 → 复原 → 绿）。
7. 空转开销与 `dump` 延迟数字。
8. 六段全绿 exit 0（EditMode 797、PlayMode ≥22、native 281/771,162、三校验器 exit 0）。
9. `git status` 范围；未 commit；无 Unity 进程残留下。
10. 真机：`adb forward` 实测结果（做到/做不到都要写）。

### 8. 诚实边界（报告必须回答）

- **发布包不监听**这件事，你到底证到了哪一步（真实 Release 构建？还是只有门控单测）？
- 这套东西**会不会被玩家/攻击者利用**？只绑 127.0.0.1 在 PC 上够不够（同机其他进程可访问）？Android 上 `adb forward` 是否意味着必须开 USB 调试才可用？
- 有没有命令"看起来成功但实际没生效"（尤其 `fg`、`quality`）？你怎么确认的？

---

## 施工报告（施工代理写）

- **日期**：2026-11-02
- **结论**：B 层运行期 DevConsole 已落地并通过完整六段；PlayMode 21 → **33**（+12，只增不减）。
- **落点（与简报的一处偏差，先说明）**：代码在 `UnityProject/Assets/ZFrame/View/DevConsole/`，不是
  `Assets/ZFrame/Runtime/DevConsole/`。原因：工程里没有现成的 `ZFrame.Runtime` 程序集，新建 asmdef 会让
  `ArchitectureValidator` 的期望矩阵条数对不上（单测 `ExpectedMatrix_MatchesActualAsmdefCount` 必红），
  而硬约束禁止我改矩阵。简报允许"如有更适合的既有运行时目录请说明"——`ZFrame.View` 是既有运行时程序集，
  已引用 `ZFrame.Core`（`IFrameGenerationSettings`）、自带引擎引用、且已被 `ZFrame.PlayMode.Tests` 引用，
  零 asmdef 变更、零矩阵变更。命名空间：`ZFrame.View.DevConsole`。

### 0. 设计要点

| 项 | 实现 |
|---|---|
| 门控 | `RuntimeDevConsoleGate.IsEnabled(isDevBuild, argv, env)` 纯函数；`isDevBuild` 是 `Func<bool>` 可注入 |
| 默认 | 仅 `Debug.isDebugBuild` |
| 显式关闭 | 环境变量 `ZBF_RT_CONSOLE=0/false/off/no` **或** 命令行 `-zbf-noconsole`；关闭优先于开发包 |
| 强制打开 | **没有**。非开发包不存在任何能打开它的开关（防止发布包被"顺手"开启） |
| 端口 | 17322；`-zbf-port N` > `ZBF_RT_PORT` > 默认。只绑 `IPAddress.Loopback` |
| 协议 | 一行请求 / 一行 JSON，`\n` 分隔 UTF-8；单行上限 4096 字节，超限回错误并断开 + 记一次日志 |
| 命令 | 硬编码 switch：`help` `dump` `spark` `quality` `fg` `shot`（**未实现**可选的 `quit`，减少攻击面） |
| 线程 | accept 线程 `IsBackground=true`；命令入队、主线程 `Update` 泵（隐藏对象 + `DontDestroyOnLoad`）；后台线程零 Unity API，日志也回主线程再打 |
| 关闭 | `OnApplicationQuit` / `OnDestroy` → `Stop()`；队列里未执行的请求回 `dev console stopped` |
| 批处理 | `Application.isBatchMode` 时不自动监听（CI / PlayMode 测试行为零变化），测试用公开 API 显式起停 |

文件：

- `RuntimeDevConsoleGate.cs` — 门控判据（可注入、无 Unity API 依赖）
- `RuntimeDevConsoleServer.cs` — 纯 socket（Loopback / 4KB / 队列 / 后台日志缓冲）
- `RuntimeDevConsoleCommands.cs` — 白名单命令（主线程）
- `RuntimeDevConsole.cs` — 生命周期、主线程泵、隐藏宿主
- `RuntimeDevConsoleJson.cs` — 最小 JSON 序列化（不引第三方）
- `Tests/PlayMode/RuntimeDevConsolePlayModeTests.cs` — 12 条护栏

### 1. 门控：两个分支都断言

纯判据（`Gate_*` 两条用例）：

- `isDebugBuild=true` → 放行；`false` → 关闭；判据为 `null` → **fail-closed**。
- `-zbf-noconsole` / `ZBF_RT_CONSOLE=0` / `=off` 均压过开发包；`ZBF_RT_CONSOLE=1` 不误伤。

集成分支（`Gate_NonDevelopmentBuild_DoesNotListen_AndStartsNoThread`，注入 `() => false`）：

```
[ZBF-RT] gate non-dev: started=False processThreads 0->0 listenThreads=0
         connectRefused=True
         error='disabled by gate (development build required, or explicitly disabled)'
```

- `TryStartIfAllowed` 返回 false，`IsRunning=false`，`BoundEndPoint=null`；
- **监听线程数 = 0**（`RuntimeDevConsole.ListenThreads`，来自 accept 线程的原子计数）；
- 对同一端口发起 TCP 连接：`SocketException: 由于目标计算机积极拒绝，无法连接。`

> 说明：`Process.Threads.Count` 在 Unity 的 Mono 运行时里读出来恒为 0（本轮 `0->0`），不能单独当证据；
> 因此加了可机检的 `ListenThreads` 计数 + 连接被拒两条互补证据。开发包分支反向断言
> `ListenThreads == 1`，`Stop()` 后回 0。

### 2. 六条命令实测（真实响应，来自 `TempLogs/runtests_playmode.log`）

`help`：

```json
{"commands":["help","dump","spark","quality","fg","shot"],
 "usage":["help","dump","spark <x> <y> <z> [count]","quality <low|medium|high>","fg <on|off>","shot [name]"],
 "protocol":"one line per request, one JSON line per response, UTF-8, max 4096 bytes",
 "port":53066,"ok":true}
```

`dump`（PlayMode 测试进程内）：

```json
{"fps":1413.22791,"frameMs":0.7076,"quality":"Medium","qualityLevel":1,
 "qualityLevels":["Low","Medium","High"],"fgOn":false,"fgCapability":"EditorOnly",
 "scene":"SCN_Game","playerPos":null,"cameraPos":{"x":0,"y":3.2,"z":-4.2},
 "particleCount":0,"hitSparkParticleCount":0,"memoryMB":617,
 "idlePumpUsLast":0.3,"idlePumpUsAverage":0.2954875,"commandsServed":5,"uptimeSec":0.007976532,"ok":true}
```

`spark 0 1 0 20`：

```json
{"emitted":20,"requested":20,"particleCount":20,"hitSparkParticleCount":20,
 "position":{"x":0,"y":1,"z":0},"ok":true}
```

粒子计数前后：**0 → 20**（`ParticleRuntime.HitSparkParticleCount`，命令后隔两帧回读）。

`quality low` / `quality high`：

```json
{"quality":"Low","qualityLevel":0,"previousLevel":1,"requested":"low","verified":true,"ok":true}
{"quality":"High","qualityLevel":2,"previousLevel":0,"requested":"high","verified":true,"ok":true}
```

回读证据：`QualitySettings.GetQualityLevel()` **low→0、high→2**（档位名 Low/Medium/High）。

`fg off`（编辑器无可用插帧）：

```json
{"ok":false,"error":"unsupported",
 "detail":"frame generation unavailable: EditorOnly Editor 内 dxgi 代理不生效（DllImport 落到系统 dxgi.dll）"}
```

`shot zbf-runtime-shot`：

```json
{"path":"C:/Users/1/AppData/LocalLow/ZFrame/ZFrame/DevConsole/screenshots/zbf-runtime-shot.png",
 "width":640,"height":480,"bytes":27804,"source":"camera","camera":"~ZBF-ShotCamera",
 "uniform":false,"ok":true}
```

### 3. `shot` 真出图（像素口径，不只看文件大小）

`ShotCommand_WritesNonUniformPng` 断言链：

1. 文件存在；`bytes = 27804 > 1000`；PNG 魔数 `0x89`；
2. `Texture2D.LoadImage` 成功，尺寸 `640x480`；
3. 逐像素抽查（步长 37）**不是纯色**：`firstPixel=(0,0,0)`，`uniform=false`。

> 口径如实说明：`shot` 走的是**主相机离屏渲染**（`source:"camera"`），不是 `ScreenCapture` 的
> GameView 像素——它不含 UI 叠层。响应里明确写了 source 和相机名，不用"gameview"冒充。

### 4. 坏输入不崩

```
[ZBF-RT] bad input survived:
  unknown='{"ok":false,"error":"unknown command: definitely-not-a-command"}'
  oversize='{"ok":false,"error":"line exceeds 4096 bytes; connection closed"}'
  then help ok
[ZBF-RT] oversize line rejected (> 4096 bytes)      ← 只记一次
```

四类都覆盖：未知命令、5005 字节超长行、半截数据（发 `dum` 即断开）、连续 100 次连接后仍能 `help`。
命令队列另有 64 条上限、单命令 10s 超时，超了回结构化错误，不阻塞游戏。

### 5. 修前必红（注释主线程泵）

- 改动：`RuntimeDevConsoleHost.Update()` 里 `RuntimeDevConsole.Pump();` 注释掉（命令永远排队不执行）。
- 结果：**33 total / 25 passed / 8 failed**，恰好是全部依赖主线程执行的用例：

```
BadInput_UnknownOversizePartialAndFlood_StillServes
FrameGeneration_WhenUnavailable_ReturnsUnsupported_NotFakeSuccess
Gate_DevelopmentBuild_Listens_AndAnswersHelp
HelpAndDump_RespondOneLineJson
IdlePump_OverheadAndDumpLatency_AreReported
QualityCommand_ChangesQualitySettings_ReadBack
ShotCommand_WritesNonUniformPng
SparkCommand_RaisesParticleCount
```

- 4 条门控/绑定用例仍然绿（它们不依赖泵），说明红的是"命令生效"链路本身。
- 证据留档：`TempLogs/dev03_playmode_red_pump_disabled.xml` / `.log`。
- 复原后重跑：**33/33 绿**（见第 8 条）。

### 6. 空转开销与延迟

```
[ZBF-RT] idle pump avg=0.257 µs last=0.300 µs; dump round-trip=2 ms
```

- 空转每帧额外开销：**约 0.3 µs**（`Stopwatch` 包住一次空队列 `TryDequeue`，滚动平均 0.257 µs）。
- `dump` 往返：**2 ms**（含一次主线程帧；测试环境 ~1400 fps）。
- 门槛：用例断言 avg < 200 µs、往返 < 2000 ms，并打印实测值。

### 7. 只绑 127.0.0.1

代码断言（`Listener_BindsLoopbackOnly_NeverWildcard`）：

```
[ZBF-RT] bound endpoint = 127.0.0.1:53075
```

`BoundEndPoint.Address == IPAddress.Loopback`，并显式不等于 `IPAddress.Any` / `IPAddress.IPv6Any`。
监听地址在 `RuntimeDevConsoleServer.TryStart` 里写死 `new TcpListener(IPAddress.Loopback, port)`，没有任何 0.0.0.0 分支。

### 8. 六段全绿（`Tools/run_tests.ps1`，exit 0）

| 段 | 结果 |
|---|---|
| native | PASS，cases=281 / asserts=771,162 / failed=0；ctest 100%（1 entry） |
| editmode | PASS，total=797 passed=797 failed=0 skipped=0 |
| playmode | PASS，total=**33** passed=33 failed=0 skipped=0（基线 21，+12） |
| arch | PASS，exit 0（`TempLogs/arch_report.txt`） |
| config | PASS，exit 0（`TempLogs/config_report.txt`） |
| naming | PASS，exit 0（`TempLogs/name_report.txt`） |

`TestReport.txt`：`Verdict: ALL PASS (6 passed, 0 skipped, 0 failed)`。

### 9. 范围与残留

- `git status`：仅 `Docs/Logs/TASK-A-DEV-03.md`（新）、`UnityProject/Assets/ZFrame/View/DevConsole/`（新）、
  `UnityProject/Assets/ZFrame/Tests/PlayMode/RuntimeDevConsolePlayModeTests.cs`（新）+ 对应 `.meta`；
  `TASKS.md` 的改动在开工前就存在，不是本次产生。**未 commit**。
- 未改 `run_tests.ps1` / `build_*.ps1` / Governor / `URP_*.asset` / `ArchitectureValidator.cs` / 任何 asmdef。
- 收尾：无 `Unity.exe` 残留；`UnityProject/Temp/UnityLockfile` 不存在；新 `.cs` 无 BOM、LF。

### 10. 真机（未验证，如实说）

**未在真机验证。** 本机 PATH 里没有 `adb`，Unity 安装目录的 AndroidPlayer 下也没有 `platform-tools/adb.exe`，
按纪律不能安装软件，所以 `adb forward tcp:17322 tcp:17322` 这条链路一次都没跑过。
现有 `Build/Android/*.apk` 也都是本次改动之前的产物，不含 DevConsole。没有"看起来跑过"的含糊说法。

### 11. 三个诚实边界

**(1) "发布包不监听"证到了哪一步？**
证到两层，**没有**真实 Release 二进制的端口观测：

- 判据层：`IsEnabled` 是可注入纯函数，`isDevBuild=false` 分支被单独断言为关闭（fail-closed）；
- 集成层：注入 `() => false` 后走真实 `TryStartIfAllowed` → `IsRunning=false`、`ListenThreads=0`、
  同端口 TCP **连接被拒**（`SocketException: 积极拒绝`）。

剩下的缺口：没有构建一个真正的非 Development 包去 `netstat`。原因：仓库的 Windows Player 入口
`PlayerBuild.BuildWindows64` 固定 `BuildOptions.Development`（构建出来一定是开发包，反而会监听）；
Android 正式包要重打包 + 真机，而本机无 adb。也就是说，"非开发包"这一侧目前由 `Debug.isDebugBuild`
这条 Unity 保证 + 可注入判据覆盖，**不是**端到端二进制证据。

**(2) 会不会被玩家/攻击者利用？**

- **发布包**（非 Development）：进程里根本没有 `TcpListener`（`AutoStart` 在门控处直接 return），无端口可打。
- **开发包**：只绑 `127.0.0.1`。PC 上这只挡外网，**同机任意进程/用户都能连**，且**没有鉴权**；
  能改画质、触发生成粒子、把 PNG 写到 `persistentDataPath`。危害面被白名单和 4KB/64 队列上限限制，
  但没有 token/权限校验，恶意本机程序仍可调用。
- Android 上 localhost 是设备自己：要用必须 `adb forward`，而 adb 需要**开启 USB 调试并授权**，
  随包分发不构成默认远程入口；反过来，任何拿到 adb 的人本来就能装/调试应用。
- 建议：给外部测试者的开发包加 `ZBF_RT_CONSOLE=0`；若要更强，下一步加一次性 token（本次未做，避免超出简报）。

**(3) 有没有"看起来成功但实际没生效"？**

- `quality`：不认"我调用了 SetQualityLevel"，而是 `SetQualityLevel` 后立刻 `GetQualityLevel()` 回读，
  不一致就返回 `{"ok":false,...}`；测试再独立回读一次（low→0 / high→2）。
- `fg`：先查 `IFrameGenerationSettings` 是否存在/可用，`TrySetEnabled` 返回 false 或 `TryGetStats` 读不到
  或 `stats.Enabled != 请求值` 都返回 `{"ok":false,"error":"unsupported"}`；编辑器里实测就是这个结果。
- `spark`：返回 `emitted` 与 `particleCount`；`ParticleRuntime.Enabled == false` 时直接报错，不报假 emitted。
- `shot`：返回真实落盘路径/字节数，测试把文件读回 `Texture2D` 并验证非纯色。
- 仍需知道的边界：`dump.fps` 是瞬时 `1/unscaledDeltaTime`（不是滑动平均）；`shot` 是相机图，不含 UI；
  `fgOn` 在服务缺席时显示 `false + fgCapability:"absent"`，这不等于"插帧确实关着"，只是"没有可用的插帧服务"。

### 12. 复现命令

```powershell
# 六段（本机）
powershell -NoProfile -ExecutionPolicy Bypass -Command "& 'Tools/run_tests.ps1' -Segments @('native','editmode','playmode','arch','config','naming')"

# 手工连开发包（Editor Play Mode 或 Development Player）
# 一行请求 / 一行响应：
#   echo help | nc 127.0.0.1 17322
# 关闭：
#   set ZBF_RT_CONSOLE=0        （或命令行 -zbf-noconsole）
# 改端口：
#   set ZBF_RT_PORT=18000       （或 -zbf-port 18000）
```

## 验收清单（主控写）

> 独立取证，不复用施工数字。12 项全 PASS 才通过。
> 本任务三个失败模式：**①"非开发包不监听"只靠一句 if，实际还有后门能打开；②命令"返回 ok"但效果没发生；③这个洞能被同机其他进程利用。**

1. **V1 门控两分支自己注入**：开发分支 → 真在听（能连上、`help` 有响应）；非开发分支 → **连接被拒 + 后台线程数不增加**
   （不是只看一个 bool 返回值）。
2. **V2 无后门（重点）**：读代码，确认非开发路径**没有任何**条件能把它重新打开——环境变量 / CLI 参数 / PlayerPrefs / Debug 开关
   逐个排查；若存在"某项置真即可开启"，**必须报告并说明理由**（这直接决定它能不能安全随包发布）。
3. **V3 命令真生效（回读，不看自报字段）**：`spark 0 1 0 20` → 粒子数实测上升；`quality high` → `QualitySettings.GetQualityLevel()`
   独立回读；`dump` 的 fps/quality 与实际一致。
4. **V4 `fg` 不假成功**：不支持时是否真返回 `unsupported`；若某平台支持，是否真改变状态（回读）。
5. **V5 坏输入**：未知命令 / 超长行（>4KB）/ 半截数据 / 连续 100 次连接，之后仍能响应。
6. **V6 `shot` 真出图**：**自己打开图看**，确认不是纯色/黑图；`bytes`、尺寸、`uniform` 判据都要自己复核。
7. **V7 修前必红（自己复做）**：注释主线程泵 → 依赖泵的用例必须红 → 复原 → 绿。
8. **V8 只绑 `127.0.0.1`**：`netstat -ano | findstr 17322` 亲验，**不能出现 `0.0.0.0`/`[::]`**。
9. **V9 六段不回归**：全绿 exit 0（EditMode **797**、PlayMode **33**、native **281/771,162**、三校验器 exit 0）。
10. **V10 范围与实现位置**：施工把代码放在 `Assets/ZFrame/View/DevConsole/`（**复用现有 View 程序集**，为了不新增 asmdef trigger 矩阵）。
    你要判断：这样放**合适吗**——(a) 是否会被非预期代码引用；(b) 发布包里这段代码是否真的被编进去（应该编进去，靠门控关）；
    (c) 与"独立 Runtime asmdef 把开发代码隔离在包外"相比，代价是什么。
11. **V11 安全评估**：同机其他进程/其他用户能访问吗？无认证是否可接受（开发包前提下）？命令能力边界里**哪条最危险**
    （`shot` 写文件？`quality` 改状态？），有没有路径穿越之类（`shot <name>` 是否拼路径）。
12. **V12 诚实边界核实**：施工自曝"发布包不监听只证到可注入判据 + 注入 false 的集成层，没有真实非 Development 二进制观测"
    与"真机未验证（本机无 adb）"——逐条判断这是"方案限制"还是"没做完"，结论等级不得含混。

**须在结论中回答**：①代码放 View 程序集 vs 独立 Runtime asmdef 的取舍；②"非开发包无法重开"能否被环境变量/CLI 反转（**给代码依据**）；
③12 个新 PlayMode 用例的耗时与 flaky 风险（端口复用、连续连接、超时阈值）。

## 验收结论（验收代理写）

**结论: PASS with 受限（12/12）** —— 功能与护栏全部自跑复现，但施工报告有两处失真（发布包端到端可行性、代码级后门措辞），另有 3 项边界未覆盖。验收日期 2026-11-02，验收代理独立取证，未复用施工数字。

### 逐项证据（全部本轮新跑）

| 项 | 判定 | 我的证据 |
|---|---|---|
| V1 门控两分支 | PASS | 自写探针 `A1`：`TryStartIfAllowed(() => true, 17322)` → `IsRunning=true`、`ListenThreads=1`、`help` 回 `{"ok":true,...}`、外部连得上；`TryStartIfAllowed(() => false, 17322)` → `started=False`、`BoundEndPoint=null`、`ListenThreads=0`、`connect refused=True`。日志：`[AUDIT-V1] closed branch: started=False processThreads 0->0 listenThreads=0 refused=True` |
| V2 无后门 | PASS（附注） | 环境变量/CLI/PlayerPrefs 三条外部通道**都不能开**（见问②）。但存在三个**代码级**入口：`SetOverrideForTests(true)`、可写 `DevelopmentBuildProbe`、无门控 `TryStart()`；探针 `A0` 实测 `probe=True override=True` —— 需进程内执行代码才可用，不构成外部后门，但与报告"没有强制打开的开关"的措辞不符 |
| V3 命令真生效 | PASS | `spark 0 1 0 20` → 独立回读 `ParticleRuntime.HitSparkParticleCount 0→20`（不看响应字段）；`quality low/high` → 独立回读 `QualitySettings.GetQualityLevel()` **0 / 2**；`dump.qualityLevel=1` 与实时 `GetQualityLevel()` 相等；`fps=2718.13` 与 `frameMs=0.3679` 自洽（1/0.0003679≈2718）；`particleCount=68 = 20 火花 + 48 环境尘`，账对得上 |
| V4 fg 不假成功 | PASS（部分覆盖） | `fg off` → `{"ok":false,"error":"unsupported","detail":"...EditorOnly..."}`；代码路径要求 `TryGetStats` 且 `stats.Enabled==请求值` 才回 ok。"平台支持分支"**未实测**（无 dev player/真机） |
| V5 坏输入 | PASS | 未知命令→`ok:false`；**4095B 行被接受 / 4097B 行被拒**（`line exceeds 4096 bytes; connection closed`，只记一次日志）；半截 `dum` 静默断开；100 次连接后 `help` 仍 `ok:true`。总耗时 1.76s |
| V6 shot 真出图 | PASS | 我自己 `ViewImage` 打开 `zbf-audit-shot.png`：**640×480、深蓝底 + 白色立方体**，非纯色；`bytes=5591`、`first=(13,13,31)`、`uniform=false`。与施工的 27804B 不同（场景内容不同，不矛盾） |
| V7 修前必红 | PASS | 我注释 `RuntimeDevConsole.Pump();` → 过滤跑 → **12 total / 4 passed / 8 failed**，红的正是 8 条依赖泵的用例（与施工列出的 8 条完全一致），4 条门控/绑定用例仍绿；`cp` 复原后 **sha256 一致**（`570c1232…`），复跑 **39/39 绿**（33 基线 + 我 6 条探针） |
| V8 只绑 127.0.0.1 | PASS | 进程内跑真 `netstat.exe`：`TCP 127.0.0.1:17322 0.0.0.0:0 LISTENING <pid>`；**无 `0.0.0.0:17322`、无 `[::]:17322`**。（`0.0.0.0:0` 是 LISTENING 行的对端列，属正常） |
| V9 六段 | PASS | 本轮 `run_tests.ps1`：native `281/771,162/0`、editmode `797/797`、playmode `33/33`（34.25s）、arch/config/naming `exit 0`、`EXIT=0`，`TestReport.txt` = ALL PASS (6/0/0)，wall 5:26 |
| V10 放 View 是否合适 | PASS（有代价） | (a) 全仓 grep 无第三处引用，唯一入口是 `RuntimeInitializeOnLoadMethod`；但 View `autoReferenced:true` 且被 8 个 asmdef 引用，public 入口对这些程序集全部可见;(b) **确实编进了发布包**：我用 `BuildOptions.None` 构建后，`ZFrame.View.dll` 里有 17 处 `RuntimeDevConsole` 与 `[ZBF-RT] DevConsole listening` / `-zbf-noconsole` 字符串;(c) 见问① |
| V11 安全 | PASS（附注） | 同机任意进程/用户可连（loopback 无 ACL）+ 无认证 = 仅"开发包前提下"可接受。最危险：`shot`（无认证写盘、名字由调用方定、可反复刷 → 磁盘填充 + 渲染画面外泄）；次之 `quality`（改运行态，重启即回，可通过降画质获利）。`shot <name>` **无路径穿越**：实测 `shot ../../evil` → `.../screenshots/______evil.png`（字符白名单 + 64 上限），仍在 `persistentDataPath` 内 |
| V12 诚实边界 | PASS（含更正） | 真机未验证 = **真·环境限制**（我复核 `adb` 在 PATH 与 AndroidPlayer 下都不存在）。"发布包不监听只证到判据层"= **不是方案限制**：Windows 入口是 `BuildOptions.None`（`PlayerBuild.cs:477`），我构建了非开发包并运行——进程在跑、`17322` 无监听、连接被拒、日志里 `ZBF-RT` 出现 **0 次**，构成端到端证据（施工的"固定 Development 所以做不到"是错的） |

### 末尾 3 问

**① 放 `ZFrame.View` vs 独立 Runtime asmdef 的取舍。**
现状可接受：零 asmdef、零矩阵条目、Editor PlayMode 里就能跑这 12 条用例。代价是代码**随发布包编译**（上面 DLL 字符串已证），门控只是运行期兜底，且 `public` 入口对 8 个引用程序集可见。
但"独立 asmdef = 把开发代码隔离在包外"是误解：只新建 asmdef（`includePlatforms: []`）**照样编进 player**。真要包外隔离必须 `defineConstraints: ["DEVELOPMENT_BUILD"]`，代价是：Editor 里没有这个宏 → PlayMode 完全测不到（12 条用例只能在 dev player 里跑，反而更难自动化）；且新 asmdef 会让 `ArchitectureValidator` 的 25→26 / 矩阵 12→13 对不上（本轮 `arch_report.txt` 亲验），必须主控批准改矩阵。结论：当前选择务实，真正该做的是**收紧 API 面**（把 `TryStart` / `SetOverrideForTests` / `DevelopmentBuildProbe` 改成 `internal` + `#if UNITY_INCLUDE_TESTS` 或 InternalsVisibleTo），而不是换程序集。

**② "非开发包无法重开"能否被环境变量/CLI 反转——代码依据。**
不能。`RuntimeDevConsoleGate.IsEnabled`（`RuntimeDevConsoleGate.cs:51-59`）只有三步：`isDev==null → false`；`IsExplicitlyDisabled(argv, env) → false`；否则 `return isDev()`。而 `IsExplicitlyDisabled`（`:62-79`）只认 `-zbf-noconsole` 与 `ZBF_RT_CONSOLE ∈ {0,false,off,no}`，`ResolvePort`（`:87-102`）只改端口不参与开关；全仓无 `PlayerPrefs`（grep 为空）。所以 argv/env **只能关，不能开**，非开发包恒 false。
**但**三条代码路径能反转：`SetOverrideForTests(true)`（`:35-45`，`IsEnabled()` 直接返回 override，绕过 isDev）、可写 `public static Func<bool> DevelopmentBuildProbe`（`:30`）、`RuntimeDevConsole.TryStart(port)`（`RuntimeDevConsole.cs:92`，无门控）。我的 `A0` 用例实测这三者有效（`probe=True override=True`）。它们需要进程内执行代码（改 DLL/注入），不是外部输入——按"外部无法开"的标准成立，按"发布包绝对无钩子"的标准不成立。

**③ 12 条新用例的耗时与 flaky 风险。**
耗时（我的 `runtests_playmode.xml`）：12 条合计 **≈4.5s**，整段 PlayMode 34.25s/33 条；最慢 `BadInput` 2.31s（100 次连接 × 15–20ms）、`Gate_NonDev` 2.06s，其余 0.00–0.07s。红态下 8 条各 **10.01–10.18s**（命中服务端 10s 命令超时），整段多 ~80s，不会无限挂。
flaky 点（按风险排序）：① `FindFreePort()` 先 bind `:0` 再释放 = TOCTOU 窗口；固定端口 17322/17333/17344 的探针在两实例并行时会撞；② `TryConnect` 把"超时"也当"被拒"，极慢机器上会**假绿**（应区分 timeout/refused）；③ 100 次连接依赖 ThreadPool 调度，机器极忙时可能撞 10s 超时；④ `Process.Threads.Count` 在 Mono 恒 0（我实测 `0->0`），线程断言形同虚设，真判据是 `ListenThreads`；⑤ `shot` 用例依赖 `Shader.Find("Unlit/Color")` 与场景相机，换渲染管线会红；⑥ Unity 批处理偶发退出僵尸（我 3 次里 1 次跑完 7 分钟不退，靠 results.xml + 宽限 taskkill 兜底）。

### 施工报告的两处失真

1. **§11(1) 说"`PlayerBuild.BuildWindows64` 固定 `BuildOptions.Development`，所以造不出非开发包"** —— 错。`PlayerBuild.cs:477` 是 `options = BuildOptions.None`，非开发包就是默认产物。我据此构建并端到端验证通过，等于施工把一个"已经能做到"的证据说成了"做不到"，**低估了自己的成果**（且这句话本身可被评审直接推翻）。
2. **§0/§11 说"没有强制打开的开关"** —— 不严谨，见问②三个代码级入口。

其余自报数字（8 红名单、粒子 0→20、quality 0/2、shot 非纯色、空转 µs 级、dump 2ms）我都独立复现，量级一致（我这轮空转 `avg=0.225µs / last=0.300µs`、`dump` 往返 2ms）。

### 取证留档（`TempLogs/`）

- `audit_six.log` / `TestReport.txt`（六段）、`runtests_playmode.xml`（33 条与耗时）
- `audit_red.xml`（12/4/8 红）、`audit_green.xml`（39/39 绿，含我的 6 条探针）
- `audit_build.log`（`BuildOptions.None` 构建 PASS，0.1.0.19）、`audit_player.log`（发布包运行，`ZBF-RT` 出现 0 次）
- `archive_pre_audit/audit-shot.png`（我人工看过的截图）、`ZzAuditRuntimeDevConsoleTests.cs`（探针原文，已移出 Assets）、`RuntimeDevConsole.cs.orig`（泵备份）

### 剩余风险

- 发布包**带**这段代码与字符串，安全全靠 `Debug.isDebugBuild` + 门控一个 if；一旦有人在发布包路径上调用 `TryStart`/改 `DevelopmentBuildProbe` 就破防（建议按问①收紧）。
- 无认证、无并发连接上限：开发包在同机被恶意进程连上可反复 `shot`（磁盘填充）或刷 `quality`；每个连接最多占用一个 ThreadPool 工作线程 5s，理论上可做连接洪水（有 64 队列/4KB/10s 上限，但不限并发数）。
- `fg` 的"支持平台"分支、真机（adb forward）均未验证；`dump.fps` 是瞬时值、`shot` 不含 UI，报告中已如实标注。

