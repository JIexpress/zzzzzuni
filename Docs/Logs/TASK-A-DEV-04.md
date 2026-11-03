# TASK-A-DEV-04 —— DevConsole 硬化：收紧 API 面 + 限流配额 + 测试脆弱性

- **轨道**：A / DEV（承接 A-DEV-03 验收的 V2/V11 附注与 6 条 flaky 点）
- **状态**：doing
- **派发**：主控，2026-11-02
- **上游**：`Docs/Logs/TASK-A-DEV-03.md` 验收结论（V2/V11、问①②③、剩余风险 1–4 条）

## 任务简报（主控写）

A-DEV-03 功能全通过（12/12），但验收留了三个真问题。**本任务只做硬化，不加新命令、不改协议。**

### H1（最重要）发布包里不该有"能翻转门控的入口"

**现状**（验收实测）：`SetOverrideForTests(true)`、**可写的** `public static Func<bool> DevelopmentBuildProbe`、**无门控的** `TryStart(port)`
这三条都能在进程内把门控翻过来。外部输入打不开（环境变量/CLI/PlayerPrefs 都只能关），但这意味着
**发布包的安全性完全靠"没人调用这三个 API"**，而不是靠"它们不在"。

**要求**：
1. 三者收敛为 `internal` + `#if UNITY_INCLUDE_TESTS`（或 `InternalsVisibleTo` 测试程序集），做到**发布包编出来的 DLL 里根本不存在这些符号**；
2. `DevelopmentBuildProbe` 不得是公开可写字段（改成内部只读/注入式）；
3. **必须给证据**：用 `BuildOptions.None` 构建一次非开发包（验收已证明可行，见 `PlayerBuild.cs:477`），
   然后在产物里证明——①这些符号/字符串**不出现**（`grep`/strings 或反编译）；②运行时**不监听**（连接被拒）；
   ③作为对照，开发包产物里**能**找到（否则说明你的 grep 方法不可信）。

> 验收明确说过：**换程序集不是解法**（新建 asmdef 照样编进 player；`defineConstraints: DEVELOPMENT_BUILD` 会让 PlayMode 全测不到）。
> 所以**不要新增 asmdef**，就地收紧 API 面。

### H2 限流与配额（防"开发包被同机恶意进程刷死"）

1. **并发连接上限**（建议 ≤8）：超了直接拒绝并记一次日志，不排队饿死主线程；
2. **单连接读超时**已有 10s，保持；但要有**总请求速率上限**（例如 50 次/秒，超了返回 `{"ok":false,"error":"rate limited"}`);
3. **`shot` 配额**：限频（如 ≥200ms 一次）+ 目录内文件总数上限（如 200 个，超了拒绝并提示），
   **防"无认证写盘 → 磁盘填满"**（这是验收点名的头号危险命令）；
4. 所有限流必须是**结构化错误**，不许静默丢弃。

### H3 测试脆弱性（验收点名 6 条，逐条处理）

| # | 问题 | 要求 |
|---|---|---|
| 1 | `FindFreePort()` 先 bind `:0` 再释放 = TOCTOU | 改为"固定端口 + 失败重试"或"绑定后直接沿用同一 socket"，给理由 |
| 2 | `TryConnect` 把"超时"当"被拒"→ 慢机器**假绿** | 必须区分 `timeout` / `refused`，断言写清各自期望 |
| 3 | 100 次连接依赖 ThreadPool，忙时撞 10s | 降低依赖或延长超时并说明 |
| 4 | `Process.Threads.Count` 在 Mono 恒 0 → **线程断言形同虚设** | 改用 `ListenThreads` 之类真实判据；**或删掉假断言**（留着假断言比没有更坏） |
| 5 | `shot` 依赖 `Shader.Find("Unlit/Color")` 与场景相机，换管线会红 | 加 fallback 或明确标注前提；拿不到时跳过而不是红 |
| 6 | Unity 批处理偶发僵尸（3 次里 1 次） | 不改脚本；在报告里确认"这是环境问题不是本任务引入的"，并给出判定方法 |

### 硬约束

- 白名单：`UnityProject/Assets/ZFrame/View/DevConsole/**`、`UnityProject/Assets/ZFrame/Tests/**`、`UnityProject/Assets/Game/Tests/**`、本日志。
- **不新增 asmdef**、不改 `ArchitectureValidator.cs`、不改 `run_tests.ps1`/`build_*.ps1`/Governor/`URP_*.asset`。
- **不加新命令、不改协议**（一行请求/一行 JSON、4KB 上限、127.0.0.1:17322 全不变）；不改现有命令语义。
- `.cs` 无 BOM、LF；**不 git commit**；不装软件、不联网；禁止 `rm`（用 `mv`）。
- 基线：EditMode **797**、PlayMode **33**（只增不减）、native **281/771,162**、三校验器 exit 0；**收尾完整六段**。
- 跑完确认无 Unity 进程残留、`Temp/UnityLockfile` 无 0 字节垃圾。

### 自测清单（贴证据）

1. **H1 符号消失**：非开发包产物里 `grep` 不到 `SetOverrideForTests`/`DevelopmentBuildProbe`/`TryStart`（贴命令与输出），
   且这份产物**运行时不监听**（连接被拒）。
2. **H1 对照组**：开发包产物里**能**找到同样符号（证明 grep 方法有效，不是"什么都没找到"的假阴性）。
3. **H1 门控仍可用**：收紧后 PlayMode 里测试仍能开启服务（走 `InternalsVisibleTo`），33 条用例照旧全绿。
4. **H2 并发**：并发连接超上限时的结构化拒绝证据。
5. **H2 速率**：超速请求返回 `rate limited` 的证据。
6. **H2 shot 配额**：连发 `shot` 到上限后被拒（给数字），且磁盘没有被刷爆。
7. **H3 逐条**：六条各给"改了什么 + 证据"；其中第 4 条若选择删断言，要说明为什么删比留更好。
8. **回归**：六段全绿 exit 0（EditMode 797、PlayMode ≥33、native 281/771,162、三校验器 exit 0）。
9. `git status` 范围；未 commit；无进程/锁残留。

### 诚实边界（报告必须回答）

- 收紧后，**发布包里还剩什么能开服务**？（应该什么都没有——给证据，不要只给结论）
- 限流的数值（8 并发 / 50 qps / 200 文件）是谁定的、依据是什么？会不会误伤自己的调试？
- H3 里有没有哪条你只能"标注不能修"？如实说。

---

## 施工报告（施工代理写）

- **日期**：2026-11-02
- **结论**：H1 / H2 / H3 全部落地；六段全绿（exit 0）；PlayMode **33 → 36**（+3，只增不减）。
- **没有新增 asmdef、没有改协议、没有加命令**；白名单外零改动。

### 0. 改动面

| 文件 | 改动 |
|---|---|
| `RuntimeDevConsoleGate.cs` | **删** `public static Func<bool> DevelopmentBuildProbe`（可写字段）；**删** `SetOverrideForTests`；`IsEnabled()` 直接读 `Debug.isDebugBuild` |
| `RuntimeDevConsole.cs` | `TryStartIfAllowed` → `public TryEnable(port, out error)`（门控在内部，调用方无法绕过）；原**无门控 `public TryStart` 删除**，实体降为 `private StartListener`；新增 `internal TryEnableForTests(...)` / `internal ActiveClientsForTests`（`#if UNITY_EDITOR \|\| UNITY_INCLUDE_TESTS \|\| DEVELOPMENT_BUILD`） |
| `RuntimeDevConsoleServer.cs` | 并发上限 8（超了当场结构化拒绝 + 记一次日志，**不排队**）；请求速率 50/s（1s 固定窗口）；`TryStart` → `TryBind`（消灭最后一个"不带门控的启动"符号）；`ActiveClients` 计数 |
| `RuntimeDevConsoleCommands.cs` | `shot` 两道配额：≥200ms/次、目录 ≤200 个 PNG；都在**渲染之前**判定，被拒时不做 GPU/磁盘工作 |
| `DevConsoleAssemblyInfo.cs`（新） | `[assembly: InternalsVisibleTo("ZFrame.PlayMode.Tests")]`，只在 Editor/测试/开发构建下编译 |
| `DevConsoleBuildProbe.cs`（新，`#if UNITY_EDITOR`） | H1 取证用的两个构建入口（`BuildNonDevelopment` / `BuildDevelopment`），不改 `build_player.ps1` |
| `RuntimeDevConsolePlayModeTests.cs` | H3 六条 + 3 条 H2 用例；`FindFreePort` 删除 |

**关于"internal + `#if UNITY_INCLUDE_TESTS`"的字面执行**：`UNITY_INCLUDE_TESTS` 在 **Editor 编译的运行时程序集**里并不保证定义（它不是"编辑器里恒有"的宏），照字面写会让 PlayMode 直接编不到测试接缝。所以条件写成
`#if UNITY_EDITOR || UNITY_INCLUDE_TESTS || DEVELOPMENT_BUILD` —— 这三个宏在**非开发包（发布包）里一个都不定义**，产物照样一个符号都没有（下面 grep 是实证，不是推理）。

另外，`SetOverrideForTests` / `DevelopmentBuildProbe` 是**直接删除**而不是"藏起来"：它们唯一的用途就是"进程内翻转判据"，删掉比 `#if` 掉更彻底，测试也不再需要它们（见 H3）。

### 1. H1 证据①：符号在非开发包 DLL 里真的不存在（含对照组）

构建（`BuildOptions.None` / `BuildOptions.Development`，产物在 `Build/DevConsoleProbe/{release,dev}`）：

```
[ZBF-BUILD] building release options=None -> ...\Build\DevConsoleProbe\release\ZFrame.exe
[ZBF-BUILD] release result=Succeeded errors=0 size=84395768
[ZBF-BUILD] building dev options=..., Development -> ...\Build\DevConsoleProbe\dev\ZFrame.exe
[ZBF-BUILD] dev result=Succeeded errors=0 size=111634463
```

两份产物都是 **Mono**（都有 `MonoBleedingEdge`、都没有 `GameAssembly.dll`），程序集可直接对比：

```
$ for s in ...; do printf "%-24s rel=%s dev=%s\n" "$s" \
    "$(grep -a -o "$s" Build/DevConsoleProbe/release/ZFrame_Data/Managed/ZFrame.View.dll | wc -l)" \
    "$(grep -a -o "$s" Build/DevConsoleProbe/dev/ZFrame_Data/Managed/ZFrame.View.dll | wc -l)"; done

SetOverrideForTests      rel=0 dev=0     ← 已删除（不是藏起来）
DevelopmentBuildProbe    rel=0 dev=0     ← 已删除（不是藏起来）
TryStart                 rel=0 dev=0     ← 无门控启动入口，改名 + 降私有
TryEnableForTests        rel=0 dev=1     ← 测试接缝，只在 dev 里有
ActiveClientsForTests    rel=0 dev=1     ← 测试判据，只在 dev 里有
RuntimeDevConsole        rel=17 dev=17   ← 反向 sanity：代码确实编进去了
```

- 前两行"两边都是 0"不是 grep 失效，是**这两个符号被整段删掉了**；第三行同理（`TryStart` 现在连子串都不存在，`Try*` 全表里也没有它）。
- 反过来 `RuntimeDevConsole`（17）与 `TryEnableForTests`（dev=1 / rel=0）说明 grep 在这个文件上是**有分辨力**的：找得到该找到的，找不到该消失的。
- 字符串字面量是 UTF-16（ASCII grep 天然找不到），单独用 Python 按 `utf-16-le` 数：release 与 dev 都有 `ZBF-RT`×5、`DevConsole listening`×1、`-zbf-noconsole`×2。也就是说**发布包里这套代码和日志文本确实还在**，缺的只是"能打开它"的入口。

### 2. H1 证据②：同一份 release 产物运行时不监听

```
pid=26088 alive=True
listen_17322_count=0
CONNECT_FAILED: 由于目标计算机积极拒绝，无法连接。
connect_ms=2091
ZBF-RT hits in Player.log = 0
```

### 3. H1 证据③：对照组（dev 产物）能找到符号，而且真的在监听

```
listen_17322=127.0.0.1:17322 Listen
response={"commands":["help","dump","spark","quality","fg","shot"],... ,"port":17322,"ok":true}
ZBF-RT hits in Player.log = 1
[ZBF-RT] DevConsole listening on 127.0.0.1:17322 (development build, whitelist: help,dump,spark,quality,fg,shot)
```

> `connect_ms=2091` 这个数别忽略：本机连一个**未监听**的回环端口，Windows 要约 2.07 秒才回 RST。这正好是 H3② 的现场——把"2s 后报拒绝"当被拒没问题，把"1.5s 还没结果"当被拒就是假绿。

### 4. H1 证据④：收紧后门控仍可用

PlayMode **36/36 绿**，其中两条门控用例照旧：

- `Gate_DevelopmentBuild_Listens_AndAnswersHelp`：`TryEnableForTests(() => true, port)` → 监听 1 个线程、`help` 回 `ok:true`、`Stop()` 后线程归零；
- `Gate_NonDevelopmentBuild_DoesNotListen_AndStartsNoThread`：`TryEnableForTests(() => false, port)` → `started=False`、`IsRunning=false`、`ListenThreads 0`、连接 **Refused**（不是 Timeout）。

测试能调 internal 接缝，靠的是 `DevConsoleAssemblyInfo.cs` 的 `InternalsVisibleTo("ZFrame.PlayMode.Tests")`。

### 5. H2：数值、依据、证据

| 限制 | 值 | 依据 | 会不会误伤自己的调试 |
|---|---|---|---|
| 并发连接 | **8** | 每个客户端在服务端占一个 ThreadPool 工作线程、最长 5s（读超时）。8 足够"几个终端 + 一个脚本"同时用，再往上就是洪水；超了**当场拒绝**（不排队，不占工作线程） | 手工调试 1–2 条连接，不会 |
| 请求速率 | **50 / s**（1s 固定窗口） | 人手打字 < 5/s；脚本连发也就几十。50 留了约 10× 余量，但 1s 内仍能被突发打满 | 会：`for i in $(seq 1 200)` 这种压测会被限。这是**预期行为**，错误里带 `retry in Nms`，不会断连 |
| `shot` | **≥200ms/次** + **目录 ≤200 个 PNG** | 200ms = 5/s，够"手点几下"；200 个文件是硬上限，防止"无认证写盘填满磁盘" | 连拍第二发会被 `shot rate limited`；等 200ms 即可 |

四条都是**结构化错误**，没有一条静默丢弃：

```json
{"ok":false,"error":"too many concurrent connections (max 8)"}
{"ok":false,"error":"rate limited (max 50 requests/s; retry in 480ms)"}
{"ok":false,"error":"shot rate limited: at least 200ms between shots (last 10ms ago)"}
{"ok":false,"error":"shot quota exceeded: 200 files in .../DevConsole/screenshots (max 200); delete old screenshots to continue"}
```

实测（本轮 green run 的 PlayMode log）：

```
[ZBF-RT] connection rejected: concurrent limit 8 reached
[ZBF-RT] H2 concurrency: {"ok":false,"error":"too many concurrent connections (max 8)"}
[ZBF-RT] H2 rate: total=120 ok=50 limited=70 exceptions=0
[ZBF-RT] H2 shot quota: files=200 second='...shot rate limited...' third='...shot quota exceeded: 200 files...'
```

- 并发用例是"真的顶满"才断言：8 个连接逐个建立，轮询 `ActiveClientsForTests` 到 8 再发第 9 个；之后释放 8 个连接，`help` 仍 `ok:true`（服务没被拒绝逻辑带崩）。
- 速率用例是 6 线程 × 20 次突发（120 次），`ok=50 / limited=70 / exceptions=0`；等 1.2s 后恢复 `ok:true`。
- shot 用例把目录填到 200 个**小文件**，验完把本次创建的文件全删掉，并断言目录回到原规模（磁盘没有被刷爆）。
- 限流状态挂在**每个 server 实例**上，`Stop()`/重启即清零，用例之间不互相污染。

### 6. H3 六条（逐条）

| # | 处理 | 证据/理由 |
|---|---|---|
| 1 `FindFreePort` TOCTOU | **删掉**。改成 `StartConsole()`：从 17620 起**真启动**，绑不上就换下一个（最多 40 个）——没有"先 bind :0 再释放"的窗口，失败重试用的就是真实绑定结果 | 8 个命令用例全部走它，日志里每次都是 `listening on 127.0.0.1:1762x`；负向用例另用 `FindQuietPort`（扫描到"连接 Result=Refused"的端口），同样不用 bind 探测 |
| 2 timeout 当 refused | `ConnectProbe` 返回 `Connected / Refused / Timeout / Error` 四态；非开发包用例**只接受 Refused**，Timeout 直接判红并打印 detail | 本机实测 Refused 要 ~2.07s，窗口设 5s（扫描）/8s（断言后）；1.5s 的旧窗口会把 2.07s 的拒绝误判成 Timeout（本轮真踩到过，见 RED 记录） |
| 3 100 次连接依赖 ThreadPool | 保留 100 次，但**全程串行**（连上即断，永不并发），不依赖 ThreadPool 的并发调度；注释里写明"并发压力由 H2 用例覆盖" | `BadInput` 用例 2.3s 跑完；并发行为改由 `H2_ConcurrentOverLimit` 用 8+1 条连接专门压 |
| 4 `Process.Threads.Count` 假断言 | **删除**（连同 `System.Diagnostics.Process` 的断言与 `threadsBefore/After` 变量）。真判据换成 `RuntimeDevConsole.ListenThreads`（accept 线程的原子计数）：非开发包必须 0、Stop 后必须 0、dev 分支必须 1 | Mono 下 `Process.Threads.Count` 恒 0，`after <= before + 1` 永远成立——它连"起了线程"都测不出来，留着只会让评审误以为有护栏。删掉的代价为零：真线程数有更好的判据 |
| 5 `shot` 依赖 `Unlit/Color` + 场景相机 | 着色器 fallback 链：`Unlit/Color → Universal Render Pipeline/Unlit → Sprites/Default`；**整条链都拿不到** → `Assert.Ignore`（跳过而不是红）。非纯色断言：SRP 下若渲染出纯色（离屏相机在 URP 的已知前提）→ Ignore；内置管线（本工程）仍严格断言 | 本轮内置管线：`shot: ... 640x480 27063 bytes firstPixel=(0,0,0) uniform=False`；相机本来就自建（`~ZBF-ShotCamera`），不依赖场景里有没有相机 |
| 6 批处理僵尸 | **不改脚本**。本轮 4 次 Unity 启动（release 构建 / dev 构建 / 2 次测试）**全部正常退出**，未复现 | 判定方法：`results.xml` 已写出 + Unity.exe 在宽限期（300s）后仍存活 = 僵尸（runner 会 taskkill 并认 xml 为证据）；同一提交换机器/换时段不复现 → 环境问题。本轮没有出现"跑完不退" |

### 7. 修前必红（H2 三处限流）

把三处判断临时改成 `if (false && ...)`（并发、速率、shot 的两道），跑 PlayMode：

```
FAIL: tests failed: total=36 passed=33 failed=3 (Unity exit=2)
H2_ConcurrentOverLimit_RejectedWithStructuredError   ← 第 9 个连接拿到了 help 的 ok:true
H2_RequestsOverRateLimit_ReturnRateLimited           ← burst of 120 requests (limit 50/s) produced no rate limited response
H2_ShotQuota_RateLimitedAndFileCapEnforced           ← 第二发 shot 真的写出了 zbf-quota-2.png
```

证据留档 `TempLogs/dev04_red_h2.xml` / `.log`。复原后 `sha256sum -c TempLogs/dev04_h2_sha_before.txt` 两文件 **OK**，重跑 **36/36 绿**。

### 8. 六段（`Tools/run_tests.ps1`，exit 0）

| 段 | 结果 |
|---|---|
| native | PASS，cases=**281** / asserts=**771,162** / failed=0；ctest 100%（1 entry） |
| editmode | PASS，total=**797** passed=797 failed=0 skipped=0 |
| playmode | PASS，total=**36** passed=36 failed=0 skipped=0（基线 33，+3） |
| arch | PASS，exit 0 |
| config | PASS，exit 0 |
| naming | PASS，exit 0 |

`TestReport.txt`：`Verdict: ALL PASS (6 passed, 0 skipped, 0 failed)`。

### 9. 范围与残留

- `git status`：`M` 5 个文件（`RuntimeDevConsole{,.Commands,.Gate,.Server}.cs`、`RuntimeDevConsolePlayModeTests.cs`）+ 新增 `DevConsoleAssemblyInfo.cs`、`DevConsoleBuildProbe.cs` 及其 `.meta`、本日志；`TASKS.md` 的改动在开工前就有，不是本次产生；`DevConsole.meta` 是上一任务的未提交产物。**未 commit**。
- 未改 `run_tests.ps1` / `build_*.ps1` / `ArchitectureValidator.cs` / Governor / `URP_*.asset` / 任何 asmdef。
- 收尾：无 `Unity.exe` 残留；`UnityProject/Temp/UnityLockfile` 不存在；所有新增/修改 `.cs` **无 BOM、LF**。
- 新增证据产物：`Build/DevConsoleProbe/{release,dev}/`、`TempLogs/dev04_build_{release,dev}.log`、`TempLogs/dev04_red_h2.{xml,log}`、`TempLogs/dev04_h2_sha_before.txt`。

### 10. 三个诚实边界

**(1) 收紧后，发布包里还剩什么能开服务？**

公开面只剩一个：`RuntimeDevConsole.TryEnable(int port, out string error)`。它内部调 `RuntimeDevConsoleGate.IsEnabled()`，判据是 `Debug.isDebugBuild`（非开发包恒 false），命令行/环境变量**只能关不能开**（`IsExplicitlyDisabled` 只认 `-zbf-noconsole` 与 `ZBF_RT_CONSOLE∈{0,false,off,no}`，`ResolvePort` 只改端口）。

其余入口全部在编译期消失（上面 grep 全 0）：`TryEnableForTests`、`ActiveClientsForTests`、`StartListener`（private 且唯一调用点是已门控的 `TryEnable`/`AutoStart`）。

**唯一还"能"的路径是进程内执行代码**——反射调私有成员、注入 DLL、改 `Assembly-CSharp`。这和"任何 Unity 游戏都能被进程注入"是同一等级的问题，不是"发布包自带入口"，本任务消除不了，如实写在这里。

还有一句必须说清：**这段代码和日志文本仍然在发布包里**（17 处 `RuntimeDevConsole` 符号 + 5 处 `ZBF-RT` 字面量）。安全性来自"没有正向开关"，而不是"代码不在包里"。想让代码彻底离开包，得走 `defineConstraints: DEVELOPMENT_BUILD`（PlayMode 就再也测不到）+ 改矩阵，本次按简报明确不做。

**(2) 8 / 50 / 200 是谁定的、依据是什么、会不会误伤自己？**

是我按"单机开发工具"的用量拍的**保守经验值**，不是测出来的最优值，也不是从别处抄的：

- 并发 8：受限于"每个客户端占一个 ThreadPool 工作线程、最长 5s"。8 足够覆盖"几个终端 + 一个小脚本"，同时把洪水限制在 8 个工作线程内。
- 50 qps：人手 < 5/s，自动脚本几十/s；50 留 10× 余量。**会误伤**压测型脚本（连发 200 条会被限），这是设计意图，不是副作用；错误里带 `retry in Nms`，不静默。
- 200 个文件：按开发机 1920×1080 的 PNG（2–3MB/张）算，最坏约 0.4–0.6GB——不小，所以真正的护栏是"**有限** + 5/s 的写入速率"，而不是 200 这个数。要更严可以降到 50，本次按简报给的示例值取 200。

**(3) H3 里有没有只能"标注不能修"的？**

有两条：

- **#6 批处理僵尸**：只能给判定方法，修不了（脚本不让改，而且本轮没复现）。它的本质是 Unity 批处理进程退出偶发挂起，与本任务改动无关。
- **#5 的 SRP 分支**：本工程是内置管线，URP 那条 fallback + Ignore 只是"前提不满足时不红"的写法，**没有在真正的 URP 工程里验证过**。

另外一条不在 H3 但必须说：`shot` 的配额是**防刷爆**，不是**防越权**——同机任意进程仍然可以无认证连上开发包、改画质、截图（只绑 127.0.0.1 挡的是外网）。本次按简报"不改协议"没有做 token，协议面一个字节都没动。

### 11. 复现命令

```powershell
# 六段（本机）
powershell -NoProfile -ExecutionPolicy Bypass -Command "& 'Tools/run_tests.ps1' -Segments @('native','editmode','playmode','arch','config','naming')"
```

```bash
# H1 取证：两份产物（Git Bash 启动 Unity；跑前 unset 代理变量）
unset http_proxy https_proxy all_proxy
"/c/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe" -batchmode \
  -projectPath "C:/Users/1/Desktop/zcode/GameFramework/UnityProject" \
  -executeMethod ZFrame.View.DevConsole.DevConsoleBuildProbe.BuildNonDevelopment \
  -logFile "C:/Users/1/Desktop/zcode/GameFramework/TempLogs/dev04_build_release.log"
# 换 BuildDevelopment 得到对照组

# 符号对比
for s in SetOverrideForTests DevelopmentBuildProbe TryStart TryEnableForTests ActiveClientsForTests RuntimeDevConsole; do
  printf "%-24s rel=%s dev=%s\n" "$s" \
    "$(grep -a -o "$s" Build/DevConsoleProbe/release/ZFrame_Data/Managed/ZFrame.View.dll | wc -l)" \
    "$(grep -a -o "$s" Build/DevConsoleProbe/dev/ZFrame_Data/Managed/ZFrame.View.dll | wc -l)"
done
```

```bash
# 手工连开发包（一行请求 / 一行响应）
#   echo help | nc 127.0.0.1 17322
# 关闭：set ZBF_RT_CONSOLE=0  或命令行 -zbf-noconsole
```

## 验收清单（主控写）

> 独立取证，不复用施工数字。10 项全 PASS 才通过。
> 本任务两个失败模式：**①"符号消失了"其实是 grep 方法失效（假阴性）；②限流只写在文档里，实际没生效。**

1. **V1 发布包符号真的消失**：自己用 `BuildOptions.None` 构建（或复核施工产物），在 `ZFrame.View.dll` 里查
   `SetOverrideForTests` / `DevelopmentBuildProbe` / `TryStart`。**必须同时给对照组**——开发包里这些符号**能找到**，
   且某两个"两边都在"的字符串计数相同（证明 grep 有分辨力，不是扫了个空文件）。
2. **V2 发布包运行时不监听**：亲自跑 release 产物 → `netstat` 无 `17322`、外部连接被**积极拒绝**、Player 日志无 `ZBF-RT` 记录。
3. **V3 收紧后门控仍可用**：PlayMode **36** 条全绿（说明 `InternalsVisibleTo` / `UNITY_INCLUDE_TESTS` 路线没把测试能力砍掉）。
4. **V4 并发上限**：自己开 9 个并发连接，第 9 个必须被**结构化拒绝**（不是静默挂起、不是排队）。
5. **V5 速率上限**：超速请求返回 `rate limited`（贴真实 JSON）。
6. **V6 `shot` 配额**：限频（≥200ms）与目录上限两道都实测到；若真写满 200 个文件代价太大，
   可用注入小上限/小配额的方式验证**判据本身**生效，但要说清"验证的是逻辑不是真实 200 个文件"。
7. **V7 H3 关键项复核（尤其②）**：`TryConnect` 四态是否真区分 `Refused`/`Timeout`；
   **验证"旧的 1.5s 窗口会假绿"这件事是否成立**（本机 Refused 要 2.07s）——这是本次最有价值的一条修正，值得独立确认。
8. **V8 六段不回归**：全绿 exit 0（EditMode **797**、PlayMode **36**、native **281/771,162**、三校验器 exit 0）。
9. **V9 范围与残留**：`git status` 仅白名单 + 本日志；**未新增 asmdef**；未 commit；无 Unity 进程/锁残留；`.cs` 无 BOM+LF。
10. **V10 诚实边界核实**：施工自曝两条"只能标注"（批处理僵尸未复现、URP 分支未真验）——判"方案限制"还是"没做完"；
    并回答"**发布包里还剩什么能开服务**"这件事它证到了哪一步。

**须在结论中回答**：①`#if UNITY_INCLUDE_TESTS` 这条路对 IL2CPP 剥离/调试有无副作用；
②8 并发 / 50 qps / 200 文件 三个数值会不会误伤真实调试（例如连着截 10 张图看动画）；
③H3④把假断言删掉后，**线程泄漏还有没有别的看护**（真泄漏了谁会红）。

## 验收结论（验收代理写）

**结论: PASS with 受限（10/10）** —— 十项全部自跑复现，无一项靠施工数字；"受限"只指三处**如实标注、本任务不允许消除**的边界（发布包内仍带代码与字符串、URP 分支未真验、无认证不变），不是没做完。
验收日期 2026-11-02；验收代理与施工不同代理；未 commit、未改业务代码、未删文件（只在 `TempLogs/archive_pre_audit/` 归档与 `mv` 清理自建临时物）。

> 取证前提：我把施工那两份产物 `mv` 到 `archive_pre_audit/audit04_{release,dev}_prev` 后**重新各构建一次**，确保我审的是"当前源码的产物"而不是别人的缓存。
> 全新 release 的 `ZFrame.View.dll`（02:18:16）sha256 = `c202a300…f6f746`，与归档旧产物**逐字节相同** → 缓存复用不是"产物过期"。

### 逐项证据（全部本轮新跑）

| 项 | 判定 | 我的证据 |
|---|---|---|
| V1 三符号消失 + 对照组 | **PASS** | 自建两份产物均 Mono（都有 `MonoBleedingEdge`、都无 `GameAssembly.dll`）。`grep -a -o` 同一文件双向计数：`SetOverrideForTests` 0/0、`DevelopmentBuildProbe` 0/0、`TryStart` 0/0、`TryEnableForTests` **0/1**、`ActiveClientsForTests` **0/1**、`RuntimeDevConsole` **17/17**、`StartListener` 1/1、`MaxConcurrentClients` 1/1。UTF-16 字面量另数：`ZBF-RT` 5/5、`DevConsole listening` 1/1、`-zbf-noconsole` 2/2。**对照组成立**：同一个 grep 在同一文件能数出 1（dev 独有接缝），也能数出两边相同的 17 —— 不是"扫了个空文件"的假阴性 |
| V2 release 运行不监听 | **PASS** | 自建 release 起进程（PID 22552，存活、269MB）：`netstat -ano \| grep 17322` 无；**该 PID 一条 socket 都没有**（连 UDP 都没建）；外部连接 `REFUSED 2051ms`；`Player.log` 里 `ZBF-RT` **0 条**。且排除"钩子没跑"：`RuntimeInitializeOnLoads.json` 两份产物都注册 `RuntimeDevConsole.AutoStart (loadTypes=0)`，日志已走到 `[GameBootstrap] Awake`（场景已加载）；dev 同路径在同一位置打出 listening（见 V2 对照） |
| V3 收紧后门控仍可用 | **PASS** | `run_tests.ps1` PlayMode **36/36、skipped=0**；其中 15 条 DevConsole 用例，`Gate_DevelopmentBuild_Listens_AndAnswersHelp`/`Gate_NonDevelopmentBuild_...` 都靠 `TryEnableForTests`（`InternalsVisibleTo`）跑通，`ListenThreads` 1→0 断言生效 |
| V4 并发上限 | **PASS** | 我直接压 dev 产物：开 8 条持有连接，第 9 条在 **3ms** 内拿到 `{"ok":false,"error":"too many concurrent connections (max 8)"}`（不挂起、不排队、不静默丢）；释放后 `help` 恢复 `ok:true`。日志同步出现 `[ZBF-RT] connection rejected: concurrent limit 8 reached` |
| V5 速率上限 | **PASS** | 6 线程 × 20 = **120 次突发**：`ok=50 limited=70 exceptions=0`（0.20s）；真实 JSON `{"ok":false,"error":"rate limited (max 50 requests/s; retry in 892ms)"}`；等 1.3s 后 `help` 恢复——数字正好卡在 50，不是"文档里写了" |
| V6 shot 配额 | **PASS** | 两道都是真实数字：①第一发 `ok:true`（640×360、3781B），**14ms** 后第二发 → `shot rate limited: at least 200ms between shots (last 14ms ago)`；②把目录填到 **203** 个 PNG 后再发 → `shot quota exceeded: 203 files in .../screenshots (max 200)`。清理后目录回到我进入时的 2 个文件（未刷盘）。附注：dev 产物停在 boot 场景，shot 抓到 `~BootClearCamera`、`uniform:true`——命令如实回报 uniform，没有假称非纯色 |
| V7 H3② 四态 / 假绿 | **PASS** | 代码：`ConnectOutcome{Connected,Refused,Timeout,Error}`，SocketException 按 `SocketErrorCode` 分流，未在窗口内完成才归 `Timeout`；非 dev 用例**只接受 Refused**。独立实测**假绿成立**：同一未监听端口，1.5s 窗口 **3/3 判 TIMEOUT**（1502/1509/1503ms），8s 窗口 **3/3 判 REFUSED**（2012/2033/2022ms）；release 产物连 17322 也是 REFUSED **2051ms**。→ 旧 1.5s 窗口确实把"2.07s 才回的拒绝"读成超时，把超时当被拒就是假绿 |
| V8 六段回归 | **PASS** | 我跑 `run_tests.ps1 -Segments native,editmode,playmode,arch,config,naming`：**exit 0，ALL PASS (6 passed, 0 skipped, 0 failed)**，wall 3:38。native 281 / 771,162 / 0（ctest 1 entry 100%）；editmode 797/797；**playmode 36/36 skipped=0**；arch/config/naming 各 exit 0 |
| V9 范围与残留 | **PASS** | `git status --short` 只有白名单（5 个 M + `DevConsoleAssemblyInfo.cs`、`DevConsoleBuildProbe.cs` 及 meta + 本日志）+ 开工前就有的 `TASKS.md`/`DevConsole.meta`；**无新增 asmdef**；HEAD 仍 `b7c5432`（未 commit）；无 `Unity.exe`/`ZFrame.exe`；`UnityProject/Temp/UnityLockfile` 不存在；7 个 .cs 首字节非 BOM、CRLF 计数 0 |
| V10 诚实边界 | **PASS（有附注）** | ①僵尸：**环境限制**成立——我本轮 3 次 Unity 启动里复现 1 次（第一次 release 构建 `Build Finished: Success` 后 Unity.exe 又活了 9 分钟，taskkill 收尾）；另 2 次正常退出，与施工"偶发"一致。②URP 分支：**方案限制**——工程是内置管线、任务禁改 URP 资产，标注可接受；但"没在真 URP 工程验过"是事实，不能算覆盖。③"发布包还剩什么能开服务"：我复核 release DLL，公开面只剩 `TryEnable`（内部判据 `Debug.isDebugBuild`）与 `Stop`，`StartListener` 是 private，`TryEnableForTests`/`ActiveClientsForTests` 不在；17 处 `RuntimeDevConsole` + 5 处 `ZBF-RT` 字面量仍在包里。**施工这条说法属实，没有夸大**——安全性来自"没有正向开关"，不是"代码不在包里" |

### 末尾 3 问

**① `#if UNITY_INCLUDE_TESTS` 这条路对 IL2CPP 剥离/调试有无副作用？**

release 侧**零副作用**：`#if` 是预处理期移除，成员在编译产物里根本不存在，IL2CPP 的 managed stripping 面对"不存在的成员"无事可做，也不存在"被 strip 掉后留下空引用"这类问题；`InternalsVisibleTo` 是编译期特性，运行时零开销、零 IL 残留。
但有两点必须说清：
- 本工程**实际只走 Mono**（`ProjectSettings.asset` 里 `scriptingBackend` 只有 `Android: 0`，两份产物都带 `MonoBleedingEdge`、都没有 `GameAssembly.dll`），所以"IL2CPP 下符号也不在"这句我只能从编译期语义回答，**没有产物证据**。换 IL2CPP 后取证对象要改成 `GameAssembly.dll` + metadata，`ZFrame.View.dll` 那条 grep 不再适用。
- `UNITY_INCLUDE_TESTS` 会在"打包测试程序集"的构建里被定义（`BuildOptions.IncludeTestAssemblies`）。真有人那样发 release，两个 internal 接缝会回来——但它们仍是 `internal` 且只 friend `ZFrame.PlayMode.Tests`，攻击面是"包里多了一个测试程序集"，不是"多了一个 public 后门"。本工程构建脚本从不传这个选项（grep 为空）。
- 调试：`DEVELOPMENT_BUILD` 分支让 dev 包保留接缝，这是设计预期；release 本来就不该有控制台，删掉不损失任何调试能力（PlayMode 在 Editor 里跑）。

**② 8 并发 / 50 qps / 200 文件会不会误伤真实调试？**

- **8 并发**：手工调试 1–2 条连接，不会误伤。会误伤的只有"每条命令新开连接且从不关闭"的脚本——它在第 9 条拿到 3ms 的结构化错误，不断连、不挂起，关掉旧连接即恢复。
- **50 qps**：`for i in $(seq 1 200)` 这种压测会被限（第 51 条起 `rate limited`），1s 后自愈，错误里带 `retry in Nms`。这是设计意图，不是副作用。
- **200ms 的 `shot`**：**这一条会真的挡住"连截 10 张看动画"**——连发 10 张只有第 1 张成功，其余 9 张被拒，必须按 5 fps 节奏（10 帧 ≈ 2s）。用它做逐帧捕获会难受；要逐帧录应该新开"录制"命令，而本任务明确"不加命令"，所以如实记为**已知取舍**而非 bug。
- **200 文件**：调试够用；但施工按 1920×1080/2–3MB 估的"0.4–0.6GB"偏小——宽高各自 clamp 到 1920（不是 16:9），噪点图 1920×1920 单张可达 5–8MB，实际上限接近 **1–1.6GB**。数量护栏成立，量级说小了。

**③ 删掉 `Process.Threads.Count` 假断言后，线程泄漏还有没有看护？真泄漏了谁会红？**

有，但只覆盖两层：
- **accept 线程有硬看护**：`ListenThreads` 是 `_liveAcceptThreads` 的原子计数，`AcceptLoop` 里 `Interlocked.Increment`、`finally` 里 `Decrement`。dev 分支断言 **==1**、`Stop()` 后断言 **==0**（`Gate_DevelopmentBuild_Listens_AndAnswersHelp`）；非 dev 分支断言 **0 且前后不变**。accept 线程漏掉 → 这两条直接红。
- **连接槽有看护**：`ActiveClients` 在 `H2_ConcurrentOverLimit` 里被要求"释放后 5s 内回到 0"；每个测试客户端另有 25s/10s deadline，泵死或 worker 卡死都会红。
- **没有**：进程级线程总数。Mono 的 `Process.Threads.Count` 恒 0（A-DEV-03 实测 0→0），换 `ThreadPool` 计数同样会被共用线程池污染，仍是假判据。所以真实缺口是"**计数正确、但内部 ThreadPool 线程慢性增长**"这种泄漏，当前测试看不见。
- 但它有界：并发上限 8 + 每连接 5s 读超时，最坏 8 个工作线程、5s 自愈，不构成无界泄漏。一句话：**accept 线程漏 → 门控用例红；连接槽漏 → 并发用例红；ThreadPool 线程数缓慢增长 → 没人红（已知缺口）。**

### 与施工报告的出入（不影响判定）

1. **§1 表格的"对照组"措辞会误导**：`TryStart rel=0 dev=0` 排在"对照组"标题下，读起来像"开发包里能找到 TryStart"。实际它被删了，dev 也是 0；真正起对照作用的是 `TryEnableForTests`/`ActiveClientsForTests`（dev=1）与 `RuntimeDevConsole`（17/17）。结论不假，排版会让人误读。
2. **§10 的磁盘量级偏小**（见问②，实际接近 1–1.6GB）。
3. **§6#6"本轮 4 次全部正常退出"**与我复现的 1 次僵尸不冲突（不同时段），但说明"偶发"是真的，不是主观描述。

### 剩余风险

- **无认证不变**（协议冻结）：同机任意进程仍可连 dev 包改画质、截图；8/50/200 只防刷爆，不防越权。
- **200 文件 × 最大分辨率 ≈ GB 级上限**；5/s 写速意味着约 40s 可填满。
- **IL2CPP 未验**（本工程 Mono）：编译期语义上无风险，但"包内无符号"的直接证据只覆盖 Mono。
- **ThreadPool 线程数无看护**（见问③）。

### 取证留档（`TempLogs/`）

- `audit04_build_release2.log` / `audit04_build_dev.log`（两份自建产物，`Succeeded errors=0`）
- `audit04_player_release.log`（`ZBF-RT` 0 条）/ `audit04_player_dev.log`（listening + concurrency rejected）
- `audit04_probe.py`（V4/V5/V6 探针原文）+ 终端输出已抄进上表
- `audit04_six.log`、`runtests_{editmode,playmode}.xml`、`TestReport.txt`
- `archive_pre_audit/audit04_{release,dev}_prev`（施工原产物，用于哈希比对）、`archive_pre_audit/audit04_shot_dummies/`（V6 的 200 个占位 PNG，已 mv 归档）、`archive_pre_audit/audit04_player_*.log`
