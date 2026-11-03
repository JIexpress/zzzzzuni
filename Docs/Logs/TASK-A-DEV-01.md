# TASK-A-DEV-01 —— Editor 常驻本地服务（消灭批处理冷启动/僵死/锁）

> **带门禁**：先证明"服务能起、能响应、不拖慢 Editor"，再实现其余命令。
> 施工代理按本简报执行，第 2 节写报告。**禁止 git commit**。验收由另一代理执行。

## 任务简报（主控写，2026-11-01）

### 为什么做（真实痛点，不是我编的）

本项目每个任务都要跑 `Tools/run_tests.ps1` 或 Unity 批处理，代价是：
**冷启动 3–5 分钟**、**退出可能僵死（要轮询 results.xml 再 taskkill）**、**单实例要求（跑前删 UnityLockfile）**。
这套流程在交接档案 §3 里记了多条血泪。结果是每次任务有大量时间花在"等 Unity"，而不是"改代码"。

方案 #2 的 A 部分就是解决它：**在一个已经打开的 Editor 里挂常驻 HTTP/JSON 服务**，让编译/测试/日志/截图/菜单
都能秒级触发并拿到结构化结果。

### 目标（最小可用集，不要贪多）

在 Editor 进程内起一个**只监听 `127.0.0.1`** 的本地服务，提供 JSON API：

| 端点 | 语义 | 返回 |
|---|---|---|
| `GET /health` | 存活 + 状态 | `{ok, unityVersion, compiling, playing, scene, domainReloadCount}` |
| `POST /compile` | `AssetDatabase.Refresh()` 并等编译结束 | `{ok, errors:[{file,line,message}], warnings:n, ms}` |
| `GET /console?since=<seq>` | Console 尾 | `{entries:[{seq,type,message,stack}], next}` |
| `POST /test?mode=editmode\|playmode` | 触发测试（**异步**，立即返回 `runId`） | `{runId}` |
| `GET /test?id=<runId>` | 轮询结果 | `{done, total, passed, failed, failures:[...]}` |
| `POST /screenshot[?name=]` | 截图存文件 | `{path, width, height}` |
| `POST /menu?path=<MenuItem路径>` | 触发菜单项 | `{ok}` |

### 技术要点（**这几条是关键，别踩**）

1. **不要用 `HttpListener`** —— Windows 上走 http.sys 需要 URL ACL（`netsh http add urlacl`），
   Unity 进程没权限会直接抛 `HttpListenerException: Access is denied`。
   **用 `TcpListener` + 手写极简 HTTP/1.1**（只解析请求行 + `Content-Length`，响应 `Connection: close`）。够用且零权限依赖。
2. **Unity API 只能在主线程**：后台线程只负责 socket 收发；请求要**入队**，由 `EditorApplication.update`
   在主线程泵出来执行，结果回填后再由后台线程写响应。不要在任何 Unity API 上从后台线程调用。
3. **Domain reload 会杀线程**：用 `[InitializeOnLoad]` 静态构造 + `AssemblyReloadEvents.afterAssemblyReload`
   重新拉起监听；旧监听要能优雅关闭（端口复用 `SO_REUSEADDR`，或固定端口 + 启动时先探测占用再杀旧实例前先确认是自己的）。
4. **测试命令必须异步**：`TestRunnerApi` 是异步回调，接口立即返回 `runId`，结果存内存字典供轮询。
   **绝不阻塞主线程等测试结束**（会卡死 Editor）。
5. **端口**：默认固定（例如 `17321`），可用环境变量 `ZBF_DEV_PORT` 覆盖；启动失败只打日志，**不能让 Editor 崩**。

### 阶段 A（门禁）

只实现 `GET /health` + 生命周（启动/domain reload 重启/关闭）。
判据：①外部 `curl http://127.0.0.1:17321/health` 返回合法 JSON；②**Editor 操作不受影响**（不卡顿、能正常编译/播放）；
③**关掉服务后 Editor 行为与现在零差异**（用设置项或环境变量关闭）。
三条不过 → 停，报告卡点（尤其"Editor 被拖慢"这类，宁可放弃也不能破坏开发体验）。

### 阶段 B：其余端点

按上表实现 `/compile` `/console` `/test` `/screenshot` `/menu`。要求：
- 所有端点**失败要返回结构化错误**（`{ok:false, error:"..."}`），不要 500 空响应或抛异常。
- `/screenshot` 支持 GameView 与 SceneView（GameView 优先；拿不到就明确报"不可用"，不要返回黑图冒充）。
- `/console` 用 `Application.logMessageReceivedThreaded` 环形缓冲（容量自定，例如 2000 条），带自增 `seq`。

### 阶段 C：最小客户端（证明它真的好用）

写一个 `Tools/dev.ps1`（PowerShell）封装常用调用（`dev.ps1 health` / `dev.ps1 test editmode` / `dev.ps1 log`），
并用它**实测跑一次 editmode 测试**，把耗时与"传统批处理"对比出来（传统方式可引用交接档案里的 3–5 分钟口径，
或你自己实测一次作对照）。**这个对比数字是本任务的价值证明，必须有。**

### 硬约束

- **不改现有 `Tools/run_tests.ps1` / `build_*.ps1`**（并存，不替换；批处理仍是 CI 的正路）。
- 服务**必须可关闭**（菜单开关 + 环境变量），关闭后零副作用。
- 只监听 `127.0.0.1`（**不得 `0.0.0.0`**，避免把开发机暴露到局域网）。
- 监听线程必须 `IsBackground = true`，Editor 关闭时不得阻止退出。
- 放在 `UnityProject/Assets/ZFrame/Editor/DevServer/`（Editor-only asmdef 或放在既有 Editor 程序集内）。
- 不改 `FrameGovernor`/`GovernorLadder`/`GovernorTuning`；不动 `URP_*.asset` 与 Unity 安装目录。
- `.cs` 无 BOM、LF。**禁止 git commit**。

### 环境纪律（真实踩过）

- Unity 批处理**单实例**：跑前查进程 + 删 `UnityProject/Temp/UnityLockfile`
- 跑测试**勿带 `-quit`**；退出僵死 → 轮询 `results.xml`，5 分钟有 xml 无退出即 `taskkill //PID //F`
- 构建前 `unset http_proxy https_proxy all_proxy`；**Bash 侧禁止转调 powershell** → 用 **PowerShell 工具**
- 清理用 `mv` 归档到 `TempLogs/archive_pre_audit/`，不要 `rm -rf`
- 跑完查 `git status`：Editor 自动改写 `URP_*.asset`/`QualitySettings.asset` 非本意就 `git checkout` 还原
- 本机 **GUI Editor 加 `-force-d3d12` 会挂起**（B-R9-01 实测）——本任务需要**正常打开 Editor**，不要加该参数

### 交付

「施工报告」写第 2 节：**先写阶段 A 门禁结论**（三条判据的证据），再写 B/C + 耗时对比。

---

## 施工报告（施工代理写，2026-11-01 夜·接手续建）

> 接手时现状：10 个 `.cs`（1963 行）+ asmdef + `Tools/dev.ps1` 已在盘上但**从未编译过**，报告空白。
> 本轮做了三件事：①把它编译成绿的；②跑通阶段 A 三条门禁并逐条取证；③补齐 B/C 实测与耗时对比。
> **未 commit。未改 `run_tests.ps1` / `build_*.ps1` / Governor / URP。** 全程正常 GUI Editor（未加 `-force-d3d12`、未加 `-nographics`）。

### 0. 编译体检（先做）

批处理 `Unity.exe -batchmode -nographics -quit -projectPath UnityProject`：

- `EXIT=0`；日志 `AssetDatabase: script compilation time: 12.934842s`，产出 `ZFrame.Editor.DevServer.dll`（`[683/691 7s] Csc …ZFrame.Editor.DevServer.dll`）。
- 全流程墙钟 **1m53s**（含 import + 编译 + 退出）。**现有 1963 行一次编译通过，零 `error CS`**，无需推倒。
- 期间发现两个真实问题（见 §5）：新 asmdef 未登记进架构校验矩阵（导致 2 个既有单测红）、`dev.ps1` 中文乱码。

### 1. 阶段 A 门禁 —— 三条判据逐条取证

**判据① curl 通（真实返回，非人为构造）**

```
$ curl -s -w "\nHTTP=%{http_code} time=%{time_total}s\n" http://127.0.0.1:17321/health
{"ok":true,"unityVersion":"2022.3.62f3c1","pid":27096,"compiling":false,"importing":false,"playing":false,
 "paused":false,"scene":"","sceneDirty":false,"focused":false,"domainReloadCount":1,"port":17321,
 "uptimeMs":35766,"pendingRequests":0,"idlePumpTicks":1183,"idlePumpUsAvg":0.78,"idlePumpUsMax":3.2,
 "snapshotAgeMs":90,"snapshotIsStale":false}
HTTP=200 time=0.019498s
```

Editor 侧对应日志：`[DevServer] listening on http://127.0.0.1:17321 (domain load #1)`。

**判据② Editor 不被拖慢（量化 + 行为双证）**

- 空转开销：`EditorApplication.update` 每帧只做一次时间戳 → 平均 **0.50–2.93 µs/帧**，峰值 3.2–4.3 µs（reload 那一帧出现过一次 428/704 µs 的离群值，属 domain reload 抖动）。
- 编译照常：`POST /compile` 无改动 **71 ms**；改一行 `.cs` 真实重编译 **4863 ms** → `{"ok":true,"errors":[],"warnings":0,"ms":4863}`。
- 播放照常：`POST /menu?path=Edit/Play` 进播 → health `"playing":true`；再点一次退回 `"playing":false`。
- 测试期间照常响应：`POST /test?mode=editmode` 跑了 1s 时 curl `/health` 仍返回新鲜快照（`snapshotAgeMs:257`，`idlePumpTicks` 持续增长）——主线程没被测试阻塞。
- 结论：**不拖慢**。设计上 `/health` 走后台线程快路径（只读主线程发好的快照，零 Unity API），主线程忙时也不会被队列堵住。

**判据③ 关掉服务后 Editor 行为零差异**

| 关闭方式 | 证据 |
|---|---|
| 菜单 `Tools/ZFrame/Dev Server/Enabled` 取消勾选 | `[DevServer] stopped (menu disable)`；`netstat` 中 `17321` 的 **LISTENING 计数 = 0**（只剩 TIME_WAIT）；`curl --noproxy '*'` → **HTTP=000 / curl exit=7（连接被拒）**；Editor 进程仍在（PID 27096，1.98 GB），日志无任何异常 |
| 重启后设置项仍关闭 | 启动日志只有 2 行 `[DevServer] disabled by preference ZFrame.DevServer.Enabled`（每个 domain load 一行），**无监听、无 update hook、无 console hook**，编译正常 |
| 环境变量 `ZBF_DEV_SERVER=0` | 日志只有 `[DevServer] disabled by env ZBF_DEV_SERVER`，LISTENING=0，curl exit=7 |
| 退出 | 服务运行时用菜单 `File/Exit` 退出，**15s 干净退出、无僵死**（监听线程 `IsBackground=true` 不拦退出） |

三条判据全部通过 → 继续阶段 B。**没有出现"Editor 被拖慢"的卡点。**

### 2. 阶段 B —— 端点实现与实测

| 端点 | 实测结果 |
|---|---|
| `GET /health` | 见上，19 ms 往返；含 `compiling/playing/scene/domainReloadCount/pendingRequests/idlePump*` |
| `POST /compile` | `{"ok":true,"errors":[],"warnings":0,"ms":4863}`；Deferred 完成（不阻塞主线程等编译），编译错误按 `{file,line,message}` 收集，超时 300s 后回结构化错误 |
| `GET /console?since=&limit=` | 环形缓冲 2000 条 + 自增 `seq` + `next`；`type` 正确区分 log/warning/error（负面用例里 `ExecuteMenuItem failed…` 被记为 `"type":"error"`）；**seq 跨 domain reload 续接**（reload 后从 3 继续，不再重置） |
| `POST /test?mode=` | 立即返回 `{"ok":true,"runId":"…","state":"running","poll":"/test?id=…"}`；**278–365 ms 返回**，绝不阻塞 |
| `GET /test?id=` | `{"done":true,"total":797,"passed":797,"failed":0,"durationMs":4293,"failures":[]}`；中途轮询可拿到 `done:false` |
| `POST /screenshot` | `{"ok":true,"path":"…/TempLogs/DevServer/screenshots/probe_auto.png","width":1280,"height":720,"source":"camera","bytes":157679}`；`?view=scene` → `825×429, source:sceneview`。**两张图我用肉眼看过：1280×720 那张是天空盒渐变+地面，825×429 那张是空场景灰度——不是黑图** |
| `POST /menu?path=` | `{"ok":true,"path":"Edit/Play"}`；`{"ok":true,"path":"Tools/ZFrame/Dev Server/Enabled"}` 可用于远程关服务 |

失败一律结构化、无 500 空响应（实测 6 条负向用例）：

```
GET  /nope            -> 404 {"ok":false,"error":"no route for GET /nope"}
GET  /compile         -> 405 {"ok":false,"error":"method not allowed: GET /compile（期望 POST）"}
POST /test?mode=bogus -> 400 {"ok":false,"error":"未知 mode 'bogus'，只支持 editmode|playmode"}
POST /menu（无 path） -> 400 {"ok":false,"error":"缺少 path 参数，例：/menu?path=Edit/Play"}
POST /menu?path=无此项 -> 404 {"ok":false,"error":"菜单项未找到或执行失败: No/Such/Item"}
GET  /test?id=nope    -> 404 {"ok":false,"error":"run 不存在: nope"}
```

### 3. 阶段 C —— 最小客户端 + 耗时对比（本任务的价值证明）

`Tools/dev.ps1` 提供 `health | compile | test <editmode|playmode> | log [-Since n] | shot [name] | menu <path>`，实测四条都通（`health`/`log`/`shot`/`test`）。

**同一套 797 个 editmode 用例，两种跑法：**

| 跑法 | 端到端墙钟 | 测试本体 | 说明 |
|---|---|---|---|
| 常驻服务 `dev.ps1 test editmode` | **6.0 s**（runId 278 ms 返回，端到端 6046 ms） | 3.0–4.3 s | Editor 已开着，无冷启动、无锁、无僵死 |
| 批处理 `Tools/run_tests.ps1 -Segments editmode` | **44.5 s**（segment 40 s） | 4.95 s（xml） | 含 3 s 机器静默采样 + Unity 进程启动/import/退出 |
| 交接档案口径（冷 Library / 整套） | 3–5 min | — | 本次 Library 已热，故取实测 44.5 s 作对照 |

→ **服务比批处理快约 7.4×（44.5 s → 6.0 s）**；若按简报的 3–5 分钟冷启动口径，是 30–50× 量级。
另：`/compile` 用它做增量编译 71 ms–4.9 s，而批处理每验一次都要 1m53s 起（本轮实测）。

### 4. domain reload 自恢复（V4 实测）

同一次 Editor 会话里累计 **5 次 domain load**，每次都是 `stopped (assembly reload)` → `listening on http://127.0.0.1:17321 (domain load #N)`，curl 全程可用（reload 完 3 s 内再次 curl 即通）。进/出 Play Mode 各触发一次 reload，服务同样自恢复。

### 5. 本轮改动清单（续建，不重写）

| 文件 | 改动 | 原因 |
|---|---|---|
| `ZFrame/Editor/DevServer/*.cs`（10 个） | 全部校验并转 LF（原文件已是 LF，无 BOM）；`DevServerBootstrap.TryStart` 增加 **首帧快照预热** `DevState.Refresh(...)` | 修掉 reload 后到第一个 update tick 之间 `/health` 只返回 `{"ok":true,"snapshotAgeMs":-1}` 的窗口（实测确实撞到过一次） |
| `DevConsoleRing.cs` | seq 起点/终点存 `SessionState` | 修掉 domain reload 后 seq 归 1、客户端 `since=` 增量拉取错位 |
| `ZFrame/Editor/ArchitectureValidator.cs` | 期望矩阵新增 `ZFrame.Editor.DevServer`（EditorOnly，允许 `UnityEngine.TestRunner`/`UnityEditor.TestRunner`） | 既有单测 `ExpectedMatrix_MatchesActualAsmdefCount` / `ValidateAll_CurrentProject_HasZeroViolations` 因新 asmdef 变红（原文即要求"新增程序集需先更新矩阵"） |
| `Tools/dev.ps1` | 顶部设 `[Console]::OutputEncoding = UTF8`；保持 CRLF+BOM（PS 5.1 中文需要） | 修中文乱码 |

未改：`run_tests.ps1`、`build_*.ps1`、`FrameGovernor*`、`URP_*.asset`、Unity 安装目录。`git status` 只有 1 个 `M`（ArchitectureValidator）+ 3 个未跟踪项（`Tools/dev.ps1`、`DevServer/`、`DevServer.meta`）。**无 commit。**

### 6. 诚实边界（没做到的 / 没验到的）

1. **`/screenshot` 的 GameView 分支未命中**：编辑模式下 GameView 没有可读 RenderTexture，`auto` 实际走的是"相机离屏渲染"回退（`source:"camera"`）。`source:"gameview"` 这条路本轮**没有实证**；`view=scene` 走的是 SceneView 相机。拿不到图时返回的是 `{"ok":false,"error":"截图不可用: …"}`（503），不会拿纯色图冒充。
2. **`/test?mode=playmode` 未实测**（只跑了 editmode）。代码路径同一套 `TestRunnerApi`，但 playmode 的 reload/进播交互没有取证。
3. `scene` 字段一直是空串：本次启动的 Editor 没有打开项目场景（Untitled），`GetActiveScene().IsValid()==false` 时按设计回空串，**不是 bug**，但验收时可能看着像没实现。
4. 服务"关闭"时仍会打 1 行 `[DevServer] disabled by preference/env …` 日志（每个 domain load 一行）。无监听、无 hook、无线程，但严格意义上不算"零日志"。
5. `/console` 的**内容**在 reload 时会清空（环形缓冲在 AppDomain 里），只有 `seq` 续接；客户端 `since=` 跨越多次 reload 时会"跳号但不重复"。
6. 44.5 s 的批处理数字是"进程冷、Library 热"；真正冷 Library 的 3–5 分钟口径引自交接档案，本轮未重新造冷环境复现。
7. 曾用 `taskkill //PID`（不带 `/F`，等于关窗）与菜单 `File/Exit` 退出 Editor；`Temp/UnityLockfile` 的 0 字节残留已 `mv` 到 `TempLogs/archive_pre_audit/`，未 `rm`。
8. 环境副作用：`ZFrame.DevServer.Enabled` 这个 EditorPrefs（`HKCU\…\Unity Editor 5.x\ZFrame.DevServer.Enabled_h2952857941`）在阶段 A 判据③里被关过，**已改回 DWord=1**，最终一次干净启动确认服务自动拉起。当前**没有 Unity 进程在跑**。

### 7. 复现命令

```powershell
# 1) 正常打开 Editor（不要 -force-d3d12 / -nographics），无需批处理
# 2) 外部验证
curl.exe -s http://127.0.0.1:17321/health
# 3) 客户端
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/dev.ps1 health
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/dev.ps1 test editmode
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/dev.ps1 log -Since 0
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/dev.ps1 shot my_shot
# 4) 关掉：菜单 Tools/ZFrame/Dev Server/Enabled，或启动时 ZBF_DEV_SERVER=0
```

---

## 验收清单（供验收代理）

> 独立取证，不复用施工数字。**这活必须亲手开 Editor 才验得动**——不接受"施工贴的 curl 输出"。
> 三个失败模式：**①服务只在施工那台机器的某次会话里通**；②为了变绿**改了校验器**（放宽而非新增）；③"快 7.4×"是口径游戏。

- [ ] V1 阶段 A：`/health` 真能 curl 通；Editor 不被拖慢；关闭后零差异
- [ ] V2 未用 `HttpListener`（无 URL ACL 依赖）；只监听 127.0.0.1（不是 0.0.0.0）
- [ ] V3 主线程纪律：Unity API 全部经 `EditorApplication.update` 泵，**后台线程零 Unity 调用**
- [ ] V4 domain reload 后服务自动恢复（实测：改一行代码触发重编译，再 curl 仍通）
- [ ] V5 `/test` 异步不阻塞（跑测试期间 Editor 仍可操作）；轮询能拿到正确 passed/failed
- [ ] V6 `/screenshot` 真出图（能打开看，不是黑图）；拿不到时返回明确错误而非假成功
- [ ] V7 `/console` 带 seq、能增量拉取；错误日志类型正确
- [ ] V8 失败返回结构化错误，无 500 空响应/异常逃逸
- [ ] V9 `Tools/dev.ps1` 可用；**耗时对比数字有**（服务 vs 批处理）
- [ ] V10 现有 Tools 脚本未改；未动 Governor/URP/Unity 安装目录；未 commit

### 主控追加 V11–V14

- [ ] **V11 架构矩阵改动必须"只新增、不放宽"**：`ArchitectureValidator.cs` 的 diff **只能新增 `ZFrame.Editor.DevServer` 这一条**，
  不得改动/删除既有条目的 `AllowedReferences`、不得把 `EditorOnly` 改成 false、不得新增"通配/跳过"逻辑。
  **同时核实**：该文件原文是否真有"新增程序集需先更新矩阵"的要求（施工这么说的，要有出处），
  即：**这是合规补登记，还是事后为自己的红开绿灯**。
- [ ] **V12 端口与旁观成本**：`netstat` 亲验只 `127.0.0.1:17321` 在听（无 `0.0.0.0`/`[::]`）；
  固定端口 17321 与本机其它项目并存时会不会撞（撞了怎么办：只打日志不崩？）。
- [ ] **V13 耗时对比的口径**：施工给 6.0s vs 44.5s（≈7.4×），但自认"进程冷、Library 热"、3–5 分钟口径引自档案。
  你要**自己复测一次 `dev.ps1 test editmode` 端到端**，并判断这个 7.4× 是否诚实（有没有把"测试本体 3–4.3s"与"端到端 6s"混着说）。
- [ ] **V14 诚实边界核实**：施工自曝 7 条（GameView 分支未命中、playmode 未实测、scene 空串、关闭仍打 1 行日志、
  console 内容 reload 清空只续 seq、44.5s 非冷 Library、EditorPrefs 被改回开）。逐条判断：**哪些是"设计如此"、哪些是"真没做完"**，
  并给出结论等级（真没做完的不得写成"受限"混过去）。

## 验收结论（验收代理写）

> 验收代理：另起一代理。全程**亲手开 GUI Editor（未加 `-force-d3d12`、未加 `-nographics`）、亲手 curl**，不引用施工数字（凡引用处均标注）。
> 共 3 次独立启动 Editor 会话（`TempLogs/audit_devserver_gui.log` / `gui2.log` / `gui3b.log`）+ 1 次批处理对照。
> 未 commit、未改业务代码（唯一临时探针 `_AuditReloadProbe.cs` 已 `mv` 到 `TempLogs/archive_pre_audit/`）。

**结论: PASS with 受限（13/14）**

受限/未通过的唯一项是 **V14**：施工自曝的 7 条边界里，第 2 条 `/test?mode=playmode` 实测是**坏路径**（不是"未实测"，是真没做完），已单列在下文，**不得按"受限"记过**。

---

### V1 阶段 A 三条判据 —— PASS

**① curl 通（我自己的输出，boot#1 PID 22344）**

```
$ curl --noproxy '*' -s -w "\nHTTP=%{http_code} time=%{time_total}s\n" http://127.0.0.1:17321/health
{"ok":true,"unityVersion":"2022.3.62f3c1","pid":22344,"compiling":false,"importing":false,"playing":false,
 "paused":false,"scene":"","sceneDirty":false,"focused":false,"domainReloadCount":1,"port":17321,
 "uptimeMs":10355,"pendingRequests":0,"idlePumpTicks":1172,"idlePumpUsAvg":1.47,"idlePumpUsMax":551.3,
 "snapshotAgeMs":173,"snapshotIsStale":false}
HTTP=200 time=0.018187s
```
Editor 侧：`[DevServer] listening on http://127.0.0.1:17321 (domain load #1)`（`gui.log:251`）。

**② Editor 不被拖慢（量化 + 行为）**

- 空转开销：`idlePumpUsAvg` **1.41–2.88 µs/帧**（三次会话各测一次），`idlePumpUsMax` 551/622/891 µs 均为导入/编译那一帧的离群值；`/health` RTT **1.8–25.5 ms**。
- 编译照常：无改动 `POST /compile` → `{"ok":true,"errors":[],"warnings":0,"ms":68}`（wall 71 ms）；新增一个 `.cs` 后 → `ms:8647`（真实重编译+reload）。
- 播放照常：`POST /menu?path=Edit/Play` → health `"playing":true`，再点一次 → `"playing":false`。
- 测试期间照常：editmode 跑测期间 `/health` 采样 20 次，`snapshotAgeMs` **最大 439 ms**，`idlePumpTicks` 2591→2722 持续增长，RTT 1.8–24.8 ms —— 主线程没被测试堵住。
- 结论：不拖慢。`/health` 走后台线程快路径（`DevRouter.TryFastPath` 只读快照），主线程忙时也不排队。

**③ 关闭后零差异**

| 关闭方式 | 我的实测 |
|---|---|
| 菜单 `Tools/ZFrame/Dev Server/Enabled`（`POST /menu`） | `[DevServer] stopped (menu disable)`（`gui.log:2994`）；`netstat` 17321 **LISTENING 计数 = 0**（只剩 TIME_WAIT）；`curl --noproxy '*'` → **HTTP=000 / exit 7**；Editor 进程仍在（PID 22344，1,939,752 K） |
| 运行时退出 Editor | 服务在跑时退出，**4 s 干净退出**（boot#1/#2 各一次，均 4 s），无僵死 → `IsBackground=true` 生效 |
| 重启后按 pref 自动拉起 | boot#2 冷启动后 `[DevServer] listening … (domain load #1)`，`/health` 20 s 即通 |
| 环境变量 `ZBF_DEV_SERVER=0` | **本次未实测**（需独占一次启动）。代码审查：`DevServerBootstrap.IsDisabledByEnvironment()` 认 `0/false/off/no`，命中即只 `Log("disabled by env …")` 后 return，无监听/无 hook |

> 关闭的"零差异"依据除上表外还有代码事实：`Stop()` 依次 `server.Stop()`（关 socket + 排空队列 + Join 500ms）、`DevConsoleRing.Uninstall()`（摘 `logMessageReceivedThreaded`）、`EditorApplication.update -= Pump`。

### V2 未用 HttpListener / 只听回环 —— PASS

```
$ grep -rn "HttpListener|0.0.0.0|IPAddress.Any|IPv6Any" Assets/ZFrame/Editor/DevServer/  -> NONE
$ netstat -ano | grep 17321
  TCP    127.0.0.1:17321    0.0.0.0:0    LISTENING    22344
```
实现为 `TcpListener(IPAddress.Loopback, port)` + `ReuseAddress`（`DevServerCore.cs:39-42`），HTTP/1.1 手写在 `DevHttp.cs`（只解析请求行 + 头 + Content-Length，响应 `Connection: close`）。全程 **无 `0.0.0.0`、无 `[::]` 监听**。

### V3 主线程纪律 —— PASS

- 后台线程只活在 `DevServerCore.cs`（Accept + ThreadPool `ServeClient`）与 `DevHttp.cs`（解析/写响应）。两文件 `using` 只有 `System.*`；`DevJson.cs` 同为纯 C# 手写序列化（后台线程写 400 响应时会走到它，若它调 Unity 就会炸——已确认没有）。
- 全目录 grep `UnityEditor.|UnityEngine.|EditorApplication|AssetDatabase|Debug.Log` 命中的只有 `DevServerCore.cs:11` 的一行**注释**。
- Unity API 调用点全部在主线程：`DevState.Refresh`（`Application/SceneManager/EditorApplication.isCompiling…`）只被 `Pump`（`EditorApplication.update`）和 `TryStart`（`[InitializeOnLoad]` cctor）调用；`DevRouter.Handle` / `DevCompile` / `DevScreenshot` / `DevTestRuns` / `DevConsoleRing.Install` 同样只从 `Pump` 进。
- 队列模型与简报一致：后台 `_queue.Enqueue` → 主线程 `Pump` 每帧最多泵 16 条 → `request.Complete()` 回填 → 后台线程写 socket；handler 异常被 `Dispatch` 兜成 500 结构化错误，不逃逸到 update 循环。

### V4 domain reload 自恢复 —— PASS（亲测 3 次 reload）

探针：新建 `Assets/ZFrame/Editor/_AuditReloadProbe.cs`（触发重编译，验收后 `mv` 归档，见收尾）→
```
POST /compile  -> {"ok":true,"errors":[],"warnings":0,"ms":8647}   HTTP=200 wall=8.65s
health AFTER   -> domainReloadCount:2, uptimeMs:5227, HTTP=200   ← reload 后立刻可 curl
```
进/出 Play Mode 又触发一次 reload：`domainReloadCount:3`，期间 `/health`、`/console`、`/test?id=` 全部正常。
Editor 日志配对：`stopped (assembly reload)` → `listening … (domain load #2)`（`gui.log:2749-2765`）。
另：`/compile` 在 reload 前仍能把 200 响应写完（`DevCompile.Finish` 里 `WaitForWritten(300)` 生效）。

### V5 `/test` 异步不阻塞 —— PASS（editmode）

```
POST /test?mode=editmode  -> 170 ms 返回 {"runId":"dd19d92f…","state":"running","poll":"/test?id=…"}
DONE end-to-end 6.67 s
{"done":true,"state":"finished","total":797,"passed":797,"failed":0,"skipped":0,"durationMs":5747,"failures":[]}
```
跑测期间同时打 `/health`（见 V1②）：20 个采样全部新鲜，`playing/compiling` 字段正确。
**playmode 例外**：见 V14 第 2 条——`/test?mode=playmode` 能起跑但**结果永远拿不到**，本项按 editmode 判过。

### V6 `/screenshot` 真出图 —— PASS（我自己开图看了）

```
POST /screenshot?view=auto  -> {"ok":true,"path":"…/aud_auto.png","width":1280,"height":720,"source":"camera","bytes":157679}
POST /screenshot?view=game  -> 同上（source 仍 camera）
POST /screenshot?view=scene -> {"width":825,"height":429,"source":"sceneview","bytes":27453}
```
两张图我都用看图工具确认：1280×720 = 天空盒由深蓝渐变到近白的地平线 + 下方灰褐色地面（默认场景真渲染）；825×429 = SceneView 相机的同一场景（上方一条亮带 + 地面），**均非黑图、非纯色**。
失败路径（代码级）：`RenderCamera`/`ReadRenderTexture` 都用 `IsUniform()` 抽样，纯色则 `DestroyImmediate` + `error`，`Handle` 返回 **503 `{"ok":false,"error":"截图不可用: …"}`**，不写文件、不冒充成功。本次会话没触发到该分支（场景一直有相机）。
**`source:"gameview"` 从未命中** —— 施工的自曝属实（见 V14 第 1 条）。

### V7 `/console` seq / 增量 / 类型 —— PASS

```
since=0 -> count 2, next 2, seq 1..2, 单调递增=true, types {log:1, error:1}
since=1 -> count 1, seqs [2], next 2          ← 增量正确
seq2    -> "ExecuteMenuItem failed because there is no menu named 'No/Such/Item'"  type=error   ← 类型正确
```
reload 后：`count 1, next 51`，首条 `seq 51 "[DevServer] listening … (domain load #2)"` —— **seq 跨 reload 续接**（SessionState），但**内容清空**，seq 3–50 丢号（见 V14 第 5 条）。

### V8 失败结构化、无 500 空响应 —— PASS（8 条负向，全部 JSON）

```
GET  /nope                   -> 404 {"ok":false,"error":"no route for GET /nope"}
GET  /compile                -> 405 {"ok":false,"error":"method not allowed: GET /compile（期望 POST）"}
POST /test?mode=bogus        -> 400 {"ok":false,"error":"未知 mode 'bogus'，只支持 editmode|playmode"}
POST /menu（无 path）         -> 400 {"ok":false,"error":"缺少 path 参数，例：/menu?path=Edit/Play"}
POST /menu?path=No/Such/Item -> 404 {"ok":false,"error":"菜单项未找到或执行失败: No/Such/Item"}
GET  /test?id=nope           -> 404 {"ok":false,"error":"run 不存在: nope"}
DELETE /console              -> 405 {"ok":false,"error":"method not allowed: DELETE /console（期望 GET）"}
GET  /                       -> 200 {"ok":true,"service":"ZFrame Dev Server","endpoints":[7 条]}
```
无一条空 body、无一条 500。

### V9 `dev.ps1` 可用 —— PASS

四条子命令亲测：`health`（784 ms 含 PowerShell 启动）／`log -Since 1`（返回 seq≥2 的 JSON）／`shot aud_devps1`（1280×720 camera）／`test editmode`（见 V13）。
失败态也体面：端口被外部进程占时 → `[dev] 无法访问 http://127.0.0.1:17321/health` + 三条排查提示，**exit 2**。
（我抓取管道里中文显示为乱码，是 PowerShell 工具的 GBK 解码问题；脚本自身已设 `[Console]::OutputEncoding = UTF8`，JSON 内容正确。）

### V10 未改既有脚本 / 未动 Governor·URP / 未 commit —— PASS

```
$ git status --short
 M Docs/Logs/TASK-A-DEV-01.md
 M UnityProject/Assets/ZFrame/Editor/ArchitectureValidator.cs
?? Tools/dev.ps1
?? UnityProject/Assets/ZFrame/Editor/DevServer.meta
?? UnityProject/Assets/ZFrame/Editor/DevServer/
$ git diff --stat
 Docs/Logs/TASK-A-DEV-01.md | 143 ++++++++++
 .../Editor/ArchitectureValidator.cs | 7 +
```
`Tools/run_tests.ps1`、`build_*.ps1`、`FrameGovernor*`、`URP_*.asset`、`QualitySettings.asset` **零改动**（批处理跑完 `git status` 仍是这 5 项，`TestReport.txt` 未入库）。无 commit。Unity 安装目录只被读取。

### V11 架构矩阵"只新增不放宽" + 出处核实 —— PASS（合规补登记）

diff 全文（`git diff`，+7 行，无删除、无修改既有条目）：
```diff
+                // TASK-A-DEV-01：Editor 常驻 Dev Server（TcpListener 手写 HTTP/1.1，只监听 127.0.0.1）。
+                // 额外依赖仅 TestRunner 的两个官方程序集（/test 端点用 TestRunnerApi + ICallbacks）。
+                ["ZFrame.Editor.DevServer"] = new AsmdefRule
+                {
+                    AllowedReferences = new[] { "UnityEngine.TestRunner", "UnityEditor.TestRunner" },
+                    EditorOnly = true,
+                },
```
- 既有 10 条 `AllowedReferences` **一个字符没动**；无 `EditorOnly=false`；无通配/跳过逻辑；新增项 `AllowedReferences` 只有两个 TestRunner 官方程序集（与 `/test` 实现相符）。
- **出处核实（施工这句话有没有依据）：有。** `ArchitectureValidator.cs:167` 自己的报错文案就写着「程序集 'x' 不在期望矩阵中（**新增程序集需先更新** ArchitectureValidator 期望矩阵）」；`:67-68` NOTE 同样要求「落地时**必须同步在此处补条目** —— 单测会校验矩阵条目数与实际 asmdef 数一致」；`Tests/Debug/ArchitectureValidatorTests.cs:35` 的断言消息也写「新增程序集须同步更新期望矩阵」。M0-05 档案 §65 亦有同款要求。
- **判定：合规补登记，不是事后开绿灯。** 施工是"先加 asmdef → 单测变红 → 按原文要求补登记"，且补的是最小条目（若走捷径应把新 asmdef 塞进豁免/通配，那是放宽）。

### V12 端口与旁观成本 —— PASS

- 只 `127.0.0.1:17321`（V2 已证）；反序测试：Unity 先占时，外部普通 `bind` 直接 **WinError 10048**，抢不走。
- **正序冲突（外部先占 17321，再冷启动 Editor）**：
```
[DevServer] 启动失败 127.0.0.1:17321 -> SocketException: 以一种访问权限不允许的方式做了一个访问套接字的尝试。
[DevServer] 启动失败 127.0.0.1:17321 -> SocketException: …（afterAssemblyReload 又兜底试了一次）
Unity.exe 31724  Console  1  1,748,468 K      ← Editor 照常起来，没崩
netstat: 127.0.0.1:17321 LISTENING 20096      ← 仍是外部进程，Unity 未抢
curl --noproxy '*' --max-time 3            -> HTTP=000 exit=28
dev.ps1 health                             -> [dev] 无法访问 … + 3 条排查提示，exit 2
```
- 结论：撞了**只打 warning 日志、不崩、不抢端口**，且客户端给出可操作的排查提示；`ZBF_DEV_PORT` 可换端口。代价是"服务静默不可用"，只能靠日志/`Dev Server/Log status` 发现——可接受。

### V13 耗时口径 —— PASS（7.4× 诚实，且偏保守）

**我自己复测（同一台机、Library 热）：**

| 跑法 | 端到端墙钟 | 测试本体 | 命令 |
|---|---|---|---|
| `dev.ps1 test editmode` | 脚本自报 **4156 ms**；含 PowerShell 启动 **4634 ms** | 2983 ms | `powershell -File Tools/dev.ps1 test editmode` |
| 直接打 API | **6670 ms**（轮询 350 ms 粒度，比脚本更钝） | 5747 ms | `POST /test` + 轮询 |
| 批处理 `run_tests.ps1 -Segments editmode` | **50061 ms**（segment 记 00:45） | 4389 ms（xml） | 同脚本，Unity 冷进程 |

- 口径核对：施工的 6.0 s 是**端到端**、3.0–4.3 s 是**测试本体**，表里两列分开写；44.5 s 也是批处理**端到端**、4.95 s 是它自己的 xml 本体。**没有把"测试本体"和"端到端"混着比。**
- 我实测比值：50061/4634 = **10.8×**（或 50061/4156 = 12.0×）> 施工的 7.4×。**7.4× 不是口径游戏，是保守值**（他那次服务端多了 1.8 s，我这次测试本体 2983 ms 更快）。
- 唯一可挑的：44.5 s 里含 `run_tests.ps1` 的 5 s 轮询粒度（`Start-Sleep -Seconds 5`），扣掉后基线约 45 s，倍数仍 ≈10×，方向不变。

### V14 诚实边界逐条判 —— **不通过（2 条"真没做完"）**

| # | 施工自曝 | 我的判定 | 依据 |
|---|---|---|---|
| 1 | `/screenshot` GameView 分支未命中，`auto` 实际走相机回退 | **真没做完（功能缺失，但不冒充）** | `auto`/`game` 两次都 `source:"camera"`；`CaptureGameView` 靠反射找 RenderTexture，编辑模式拿不到 → 该分支在本机等于死代码。好在回退产出的是**真图**，纯色时有 503 兜底，所以不构成"假成功" |
| 2 | `/test?mode=playmode` 未实测 | **真没做完，且比自曝更严重——是坏路径** | 我实测：`POST` 67 ms 返回 runId → 进 Play（`playing:true`）跑 36 s 退出，**轮询 246 s 始终 `done:false`**，最终固定返回 `state:"unknown-after-domain-reload"`。根因：`_runs` 记录与 `ICallbacks` 只活在 AppDomain，进 Play 必触发 reload → 记录丢失 → `TestRunnerApi.RunFinished` 回调也丢了 → 永远不会写 `done|` 到 SessionState。**该端点对 playmode 实质不可用** |
| 3 | `scene` 字段空串 | 设计如此 | 本会话确实没打开场景，`GetActiveScene().IsValid()==false` → 回空串；打开场景后行为未验（低风险） |
| 4 | 关闭时仍打 1 行 `disabled by preference/env` | 设计如此 | 可接受的可观测性，非残留 |
| 5 | console 内容 reload 清空、只续 seq | 设计如此（但需在文档写明） | 实测 `count 1 / next 51`：seq 2→51 之间 **49 条永久丢失**，`since=` 客户端会"跳号"。环形缓冲在 AppDomain 里，属结构限制；已披露 |
| 6 | 44.5 s 是"进程冷、Library 热"，3–5 min 引自档案 | 设计如此（诚实） | 我复现 50.06 s 同量级；未造冷 Library 环境（成本），档案口径未复核 |
| 7 | EditorPrefs 改回开、锁文件已归档、无进程残留 | 卫生项，已复核 | `HKCU\…\Unity Editor 5.x\ZFrame.DevServer.Enabled_h2952857941 = 1`；本轮收尾无 Unity 进程、无 `UnityProject/Temp`、17321 空闲 |

**V14 判定：不通过。** 第 2 条不是"没验到"而是"验了就知道不工作"，施工按"未实测"记录属于低估；第 1 条是明确的未完成分支。这两条**不得写成"受限"混过去**——第 2 条必须在 `DevTestRuns` 里把 run 状态落 `SessionState`（或 reload 后重挂回调）才算阶段 B 完整；第 1 条要么实现 GameView 取图，要么在 `?view=game` 显式返回"不可用"而不是静默回退。

---

### 收尾状态

```
$ git status --short
 M Docs/Logs/TASK-A-DEV-01.md
 M UnityProject/Assets/ZFrame/Editor/ArchitectureValidator.cs
?? Tools/dev.ps1
?? UnityProject/Assets/ZFrame/Editor/DevServer.meta
?? UnityProject/Assets/ZFrame/Editor/DevServer/
```
- 无 Unity 进程：`tasklist //FI "IMAGENAME eq Unity.exe"` → 无匹配；`UnityShaderCompiler.exe` 亦无。
- `netstat | grep 17321` → 空闲；`UnityProject/Temp` 不存在（**无 0 字节 `UnityLockfile`**）。
- 临时探针已 `mv` 归档：`TempLogs/archive_pre_audit/_AuditReloadProbe.cs.auditA01`（+ `.meta.auditA01`）。全程**未用 `rm`**。
- 取证产物：`TempLogs/audit_a_dev01/`（console 快照、dev.ps1 输出、批处理日志）、`TempLogs/audit_devserver_gui{,2,3b}.log`、`TempLogs/DevServer/screenshots/aud_{auto,game,scene,devps1}.png`。
- 未 commit；EditorPrefs 保持 `Enabled=1`（与施工交接状态一致）。
