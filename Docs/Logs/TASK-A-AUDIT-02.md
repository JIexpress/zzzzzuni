# TASK-A-AUDIT-02 —— 修复审计 A-1/A-2/A-3（DevServer 安全 + 并发上限 + 触屏控件去重）

> 三条缺陷来自 `Docs/Logs/TASK-A-AUDIT-01.md`。**先复现、再修、再证明**。施工代理写第 2 节报告。
> **禁止 git commit**。验收由另一代理执行。

## 任务简报（主控写，2026-11-01）

### 背景

第二轮独立审计（`TASK-A-AUDIT-01`）出 9 条，本任务修其中**最该先修的 3 条**。

---

### A-1（P0，安全）DevServer 无来源校验 + 通配 CORS + 默认开启 + `/menu` 直通

**缺陷事实**（审计已坐实，你要先自己复现一遍再修）：
- `Editor/DevServer/DevHttp.cs:282` 每个响应都带 `Access-Control-Allow-Origin: *`
- `Editor/DevServer/DevRouter.cs:170` `/menu` 的 `path` 直接来自 query → `EditorApplication.ExecuteMenuItem(path)`，**无白名单**
- `Editor/DevServer/DevServerBootstrap.cs:82` 默认开启（装了工程就监听）
- 全目录没有任何 `Origin` / `Host` / token 校验

**后果**：开发者开着 Editor 时，浏览器打开任意第三方页面，一个 form POST（简单请求，**不触发预检**）即可让
Editor 执行**任意菜单项**（删资源、跑构建、切 Play）；且因通配 ACAO，`GET /console` 的正文能被 `fetch` 读走。

**修法要求（必须同时满足安全与可用）**：
1. **`Host` 头校验**：只接受 `127.0.0.1:<port>` 或 `localhost:<port>`，其余 403。
   （目的：挡 DNS rebinding——那种情况下连 Host 都指向攻击者域名。）
2. **`Origin` 校验**：
   - **无 `Origin` 头 → 放行**（`curl` / `Tools/dev.ps1` 不带 Origin，必须继续可用）；
   - `Origin` 是 `http://127.0.0.1[:port]` / `http://localhost[:port]` → 放行；
   - 其他任何 `Origin`（含 `null`）→ **403 并记日志**。
3. **去掉通配 CORS**：不再无条件回 `Access-Control-Allow-Origin: *`。可以回具体 origin（仅限第 2 条允许的那些），
   或者干脆不回——**目标是不让任意网页读到响应**。
4. **`/menu` 收紧**：默认只允许**白名单前缀**（例如仅项目自定义的 `Tools/...` 或显式配置的路径），
   或者要求一个**随机会话 token**（启动时打到 Console / 写进 `Tools/dev.ps1` 读取的临时文件）。
   二选一，选简单可靠的那个，说明理由。**不允许仍然"任意 path 直通"**。
5. **默认姿态**：默认值改为**关闭**或至少"仅在本机且已显式开启"。若你判断"默认关闭会让人以为坏了"，
   可以保留默认开启但必须让 1–4 全部到位——**在报告里说明你的选择和理由**。

**验收判据（验收会亲做）**：
- 带 `Origin: http://evil.example` 的 POST → 403，且**不执行**菜单项；
- 无 Origin 的 curl → 正常（`dev.ps1` 不被破坏）；
- 响应头中**不再出现** `Access-Control-Allow-Origin: *`；
- `Host: evil.example` 的请求 → 403。

---

### A-2（P1，稳定性）`DevServerCore` 无并发上限/无队列上限，每连接阻塞池线程 300s

审计已指出：**B 层（DevConsole）修过同一问题**——`RuntimeDevConsoleServer` 有 `MaxConcurrentClients = 8`、
`MaxQueuedRequests = 64` 并有对应测试；A 层漏了。

**修法**：照 B 层口径加两道上限（并发 + 队列），超限返回**结构化错误**（`{ok:false,error:"..."}`），
不要静默丢弃、不要让连接挂到 300s。

**验收判据**：第 9 个并发连接得到明确拒绝；队列满时同样明确拒绝；正常单连接不受影响。

---

### A-3（P1，功能）`TouchControlsView.Spawn` 去重时 `Destroy` 尚未生效 → 第二次进 SCN_Game 触屏控件静默消失

`Destroy()` 是**延迟到帧末**才真正销毁，而 `Spawn` 里的去重判断用的是"当前还找得到那些对象"，
于是返回了**本帧待销毁**的旧 overlay，且不重连 bridge → 第二次进入游戏后触屏控件没了。
**桌面验收永远绿**（因为桌面不生成触屏控件），所以这条只能靠真机或 PlayMode 用例抓。

**修法**：不要在"待销毁"状态上做去重判断。可行方向（自己选，说明理由）：
- 维护一个显式的"当前 owner"引用，`Spawn` 先解绑旧 owner 再重建；
- 或用 `DestroyImmediate`（谨慎：仅限明确的非运行期路径）；
- 或去重改为"按 owner/bridge 标识"，而不是按"场景里有没有这些组件"。

**验收判据（必须有 PlayMode 用例锁死）**：
- 连续两次进入游戏场景，第二次 `TouchControlsView` 仍然存在且 bridge 已重连；
- 该用例在**改回旧逻辑时必须变红**（有牙）。

---

### 通用要求

- 三条一起修，但**各自可独立验证**，报告分三节。
- 修完必须**回归**：EditMode 797 / PlayMode 36（A-3 新增用例会变 37+）/ 原生 281/771,162 不回归。
- **不要顺手改别的**（审计还有 6 条 P1/P2 没派，别扩大战线）。

### 环境纪律（真实踩过）

- Unity 批处理**单实例**：跑前查进程 + 删 `UnityProject/Temp/UnityLockfile`
- 跑测试**勿带 `-quit`**；退出僵死 → 轮询 `results.xml`，5 分钟有 xml 无退出即 `taskkill //PID //F`
- 构建前 `unset http_proxy https_proxy all_proxy`；**Bash 侧禁止转调 powershell** → 用 **PowerShell 工具**
- 本机 GUI Editor 加 `-force-d3d12` **会挂起**，别加
- 清理用 `mv` 归档到 `TempLogs/archive_pre_audit/`，不要 `rm -rf`
- 跑完查 `git status`：Editor 自动改写 `URP_*.asset`/`QualitySettings.asset` 非本意就 `git checkout` 还原

### 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- 不改 `FrameGovernor`/`GovernorLadder`/`GovernorTuning`；不动 Unity 安装目录
- `.cs` 无 BOM、LF

---

## 施工报告（施工代理写）

- **基线**：HEAD `d00708e`，工作区干净；Unity 2022.3.62f3c1；未 commit、未改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`/
  `FrameGovernor`/`GovernorLadder`/`GovernorTuning`，未动 Unity 安装目录。
- **证据目录**：`TempLogs/TASK-A-AUDIT-02/`（含 `editor_repro.log`、`a1_repro.txt`、`a1_fixed.txt`、`a2_concurrent.txt`、
  `probe_results.xml`、`mutant_playmode2.xml`、`playmode_results4.xml`、`editmode_results.xml`、`native_test.log`）。
  临时探针已归档 `TempLogs/archive_pre_audit/TempA2ProbeTests.cs.txt`（跑完删除，不计入交付代码）。

---

### 2.1 A-1（P0，安全）DevServer 来源校验 + 去通配 CORS + `/menu` 收紧 + 默认姿态

#### 2.1.1 改前复现（先证明缺陷存在）

环境：GUI Editor 打开工程（`Assets` 首次导入约 6 分钟，等 `[DevServer] listening on http://127.0.0.1:17321 (domain load #1)`），
`curl` 直连。原始响应见 `TempLogs/TASK-A-AUDIT-02/a1_repro.txt`。

| # | 请求 | 结果 |
|---|---|---|
| R1a | `GET /health`（无 Origin） | `200`，响应头含 `Access-Control-Allow-Origin: *` |
| R1b | `POST /menu?path=Tools/ZFrame/Dev Server/Log status` + `Origin: http://evil.example` | **`200 OK`** + `{"ok":true,"path":"Tools/ZFrame/Dev Server/Log status"}` + `ACAO: *` |
| R1c | 同上，`Host: evil.example`（无 Origin） | **`200`**，照样执行（无 Host 校验） |
| R1d | `GET /console?limit=1` + `Origin: null` | `200` + 正文（任意页面可 `fetch` 读走，含绝对路径与堆栈） |

**"确实执行了"的硬证据**：`Log status` 这个菜单项自己会打日志。`editor_repro.log:13717/13733/13764` 出现
`[DevServer] running=True port=17321 pending=0 domainReloads=1` —— 即 R1b/R1c 的请求**真的**进了
`EditorApplication.ExecuteMenuItem`，不是只回了 200。

（顺带记录一个副作用证据：R1b 第一次发出时主线程正在导入，请求被排在 `_queue` 里、`pending` 从 2 递减到 0，
说明那批请求既没被丢弃也没被校验——正是 A-2 描述的"无上限排队"。）

#### 2.1.2 修法

| 要求 | 实现 |
|---|---|
| ① Host 只认回环 | 新增 `Editor/DevServer/DevGuard.cs`：`IsLoopbackHost` 只接受 `127.0.0.1` / `localhost`（大小写不敏感）可带纯数字端口；空/缺失、`127.0.0.1.evil.example`、IPv6 字面量一律拒 |
| ② Origin 校验 | `TryAcceptOrigin`：无 Origin → 放行；`http(s)://` + 回环主机 + 无 userinfo → 放行并把该 origin 回填给响应头；其余（含字面量 `null`、`file://`）→ 403 |
| ③ 去通配 CORS | `DevHttp.cs:279` `WriteResponse(..., string corsOrigin = null)`：删掉 `Access-Control-Allow-Origin: *`，只回**具体** origin + `Vary: Origin`；403/4xx 不回任何 ACAO |
| ④ `/menu` 收紧 | `DevRouter.cs:34` 白名单前缀，默认 `{ "Tools/" }`，可用 `ZBF_DEV_MENU_PREFIXES`（分号分隔）追加；白名单外 → `403 menu path not allowed`，**不进** `ExecuteMenuItem` |
| ⑤ 默认姿态 | **保留默认开启**，但 1–4 全部生效；启动日志打印 `guard: loopback Host/Origin only, /menu allowlist`（`DevServerBootstrap.cs:121`） |
| 日志 | 闸门拒绝在后台线程入队（`DevServerCore._pendingLogs`），主线程 `Pump` 每 tick 最多 8 条泵出为 `[DevServer] request rejected: ...`（后台线程仍然零 Unity API） |

#### 2.1.3 修复后验证（同一 Editor 进程，改动触发 domain load #2）

`TempLogs/TASK-A-AUDIT-02/a1_fixed.txt` + 线路级探针（`probe_results.xml` 的 `A1_WireLevel_OriginHostAndCors`）：

| 场景 | 结果 |
|---|---|
| 无 Origin `GET /health`（curl / `dev.ps1` 姿态） | `200 OK`，**响应头里没有任何 ACAO** |
| `Origin: http://evil.example` `POST /menu` | `403 Forbidden`，`{"ok":false,"error":"origin not allowed: 'http://evil.example' ..."}`，**菜单项未执行** |
| `Origin: null` `POST /menu` | `403 Forbidden`（同上原因为 `'null'`） |
| `Host: evil.example` | `403 Forbidden`，`{"ok":false,"error":"host not allowed: 'evil.example' ..."}` |
| `Origin: http://localhost:3000` | `200`，且只回 `Access-Control-Allow-Origin: http://localhost:3000` + `Vary: Origin` |
| 无 Origin `POST /menu?path=Tools/...` | `200`，正常执行（脚本通道未破坏） |
| 无 Origin `POST /menu?path=Edit/Play` | `403 menu path not allowed: 'Edit/Play'`，未执行 |
| `Origin: http://evil.example` `GET /console` | `403`，正文读不到 |

"未执行"的核对：`grep running=True editor_repro.log` 在 domain #2 段只有 **2** 条（=F5+F6 两次允许），F2/F3/F4 三次被拒的
请求一条都没落地；同时 `grep "Access-Control-Allow-Origin: \*" a1_fixed.txt` = **0 命中**；
`grep "\[DevServer\] request rejected"` = 4 条（3 条 `/menu` + 1 条 `/console`）。

---

### 2.2 A-2（P1，稳定性）并发上限 + 队列上限

#### 2.2.1 修法（照 B 层 `RuntimeDevConsoleServer` 口径）

`Editor/DevServer/DevServerCore.cs`：

- `MaxConcurrentClients = 8`（:23）、`MaxQueuedRequests = 64`（:26），公开常量便于测试/诊断；
- accept 处 `Interlocked.Increment` 计数，超限**当场** `RejectClient` → `503` + `{"ok":false,"error":"too many concurrent connections (max 8)"}`，
  不入队、不占 ThreadPool 线程；`ServeClient` 的 `finally` 负责 `Interlocked.Decrement`；
- 入队前 `_queue.Count >= MaxQueuedRequests` → `503` + `{"ok":false,"error":"too many pending requests (max 64); retry later"}`，不入队、不等待；
- 保留 300s 业务超时（`/compile`、`/test` 本来就要等长任务），但它现在最多只压在 8 条连接上。

**实现中实测出来的第二个坑（已修）**：第一版 `RejectClient` 用 `using (NetworkStream)` 写 503 —— `NetworkStream` 释放会立刻
关 socket，而对端通常已经发来请求字节，Windows 遇"未读数据 + close"回 **RST**，客户端只收到响应头、**正文被丢**
（实测 `Content-Length: 62` 但 body 为空）。修法：写完后 `Shutdown(SocketShutdown.Send)` + 以 50ms 预算把残留读干，再 `Close`。
修复后正文确定性送达（下方实测即为证据）。

#### 2.2.2 实测

| 判据 | 证据 |
|---|---|
| 第 9 条并发连接明确拒绝 | 对**运行中的 Editor** 用 8 条空闲 TCP 连接占满（`a2_concurrent.txt`），第 9 条 `GET /health` 立刻得到 `HTTP/1.1 503 Service Unavailable` + `{"ok":false,"error":"too many concurrent connections (max 8)"}`，耗时 340ms（含 curl 启动）；释放后恢复 `200`。Editor 侧日志 `[DevServer] connection rejected: concurrent limit 8 reached` |
| 队列满明确拒绝 | 探针 `A2_QueueFull_RequestIsRejectedWithStructuredError`：反射把内部队列填到 64，随后一条真实 `GET /console` → `503` + `{"ok":false,"error":"too many pending requests (max 64); retry later"}`，`Pending` 仍为 64（被拒请求不入队） |
| 正常单连接不受影响 | 探针 `A2_Concurrent...` 释放后 / `F1` / `F6` 均 `200`；`Pending` 从 0 起，`ActiveClients` 归零 |

**诚实说明**：并发上限 8 意味着 `pending ≤ 8`（每条连接最多入队 1 条），因此**队列满这条路径从 front door 构造不出来**。
保留 64 是 B 层同型的第二道冗余闸门（若将来放宽并发），实测只能靠探针直接填队列——这条已在下方"未验证项"里标出。

---

### 2.3 A-3（P1，功能）`TouchControlsView.Spawn` 去重命中"待销毁"实例

#### 2.3.1 修法与理由

`View/Input/Touch/TouchControlsView.cs:138`：

```csharp
TouchControlsView existing = hud.GetComponentInChildren<TouchControlsView>(true);
if (existing != null && !existing._shutdown)   // ← 只把"活着"的实例当命中
{
    existing._bridge = bridge;                 // ← 复用路径也要重绑当前 bridge（A-3 次要面）
    return existing;
}
```

选 `_shutdown` 作为判据的理由：它在 `Teardown` 里**先于** `Destroy` 排队置位，所以"`_shutdown == true` 且对象还在"恰好等于
"本帧待销毁"。因此不需要新增 owner 注册表/静态引用（会引入新的生命周期状态，还要处理多 UIRoot），也不用把运行期改成
`DestroyImmediate`（会改变"帧末销毁"语义、并可能让 SceneLoader 同帧回调看到半拆状态）。

#### 2.3.2 PlayMode 用例（`Assets/ZFrame/Tests/PlayMode/TouchControlsSceneReentryPlayModeTests.cs`）

| 用例 | 断言内容 |
|---|---|
| `SecondSceneVisit_KeepsTheOverlay_AliveAndWiredToTheBridge` | 两次**真实** single-mode 载入（SCN_Empty）+ 每次用真实 `GameHud.Build`（与 `GameBootstrap.OnSceneLoaded` 同一句，走生产 `Spawn`）；第二次进场后：控件存在且 active、只有 1 套、`AreNotSame` 旧实例、`Bridge` 是 `InputRuntime.Bridge`、stick/按钮齐全，并且**按一下"互动"真的发出 `InteractInputEvent`** |
| `Spawn_DuringTheDestroyWindow_BuildsANewOverlay_InsteadOfReturningTheDoomedOne` | 帧精确机制：`Shutdown()` 后**同一帧**再 `Spawn`，必须拿到新实例；跨过帧末销毁后仍在、只有 1 套、bridge 已接上 |

#### 2.3.3 变异验证（有牙）

把 `Spawn` 去重临时还原成旧逻辑 `if (existing != null) { return existing; }`，跑本文件两条用例（`mutant_playmode2.xml`）：

```
total="2" passed="1" failed="1"
Spawn_DuringTheDestroyWindow_... : Failed
  Expected: not same as <[TouchControls] (ZFrame.View.TouchControlsView)>
  But was:  <[TouchControls] (ZFrame.View.TouchControlsView)>
SecondSceneVisit_...             : Passed
```

还原修复后再跑：`2/2 passed`（`restored_playmode.xml`）。

**一处必须如实说明的反证**：`SecondSceneVisit_...` 在**旧逻辑下也是绿的**。我还先用"菜单 → SCN_Game → 暂停 → 回主菜单 →
再开始"的真实启动链写过一遍，旧逻辑下同样是**绿**的（`mutant_playmode.xml`：2 条里又是 1 绿 1 红，绿的是启动链那条）。
原因：单模式换场景时旧 HUD 随场景卸载被销毁，比下一次 `Spawn` 早一帧，`Destroy` 的窗口与"新 HUD 构建"不重叠。
所以审计 A-3 里标注"未坐实"的那条时序（`SceneLoader.FinishLoad` 与旧 `OnDestroy` 同帧）**在本机没有复现**。
结论按实际证据写：**机制缺陷确定存在并已锁死（同帧窗口用例对旧逻辑必红）；"第二次进 SCN_Game 控件静默消失"这条用户可见
症状未复现**。修复仍然正确：它去掉了这个潜在窗口，并且补上了"复用实例不重连 bridge"这一次要缺陷。

附带的产品面最小改动：`TouchControlsView.Bridge` 只读属性（:103），供用例直接断言"已重绑"，避免用"按一下看看"这种依赖
输入门禁状态的间接判据。

---

### 2.4 三处设计选择与理由（简报要求逐条说明）

1. **`/menu` 选"白名单前缀"而不是"随机会话 token"**
   白名单是静态、fail-closed、无密钥生命周期的：token 要跨 domain reload 轮换、要落一个 `dev.ps1` 能读的文件、每条 `/menu`
   都要带密钥——三种额外状态都是新的失败面；漏配白名单的后果只是"某个菜单项调不动"，可见且能立刻用环境变量补。
   代价明确：`Edit/Play`、`Assets/*` 这类菜单**不再**能从 HTTP 触发（这正是收紧的目的），`Tools/dev.ps1` 的用法示例已同步改。
2. **默认姿态保留"默认开启"**
   简报第 5 条允许，且 1–4 全部到位后"默认开启"不再等于"任意网页可驱动 Editor"。改成默认关闭会让 `dev.ps1`、六段脚本、
   各人的调试习惯在升级后**静默失效**（服务没起来时症状是"连不上"，很容易被当成坏了）；启动日志现在会打印 guard 姿态，
   关闭仍有三条显式通道（EditorPrefs / 菜单 / `ZBF_DEV_SERVER=0`）。
3. **A-2 保留 300s 业务超时，只把"拒绝"做成即时**
   `/compile`、`/test` 的设计前提就是"主线程被长任务占住时要能等"，把它砍短会破坏既有工作流；真正的风险是"来者不拒 ×
   每连接一条池线程"。加并发上限后池线程占用被封顶在 8，超限请求在 accept 阶段就拿到结构化 503，不会挂到 300s。

### 2.5 回归数字

| 套件 | 结果 | 证据 |
|---|---|---|
| EditMode | **797 / 797**（0 failed） | `TempLogs/TASK-A-AUDIT-02/editmode_results.xml` |
| PlayMode | **38 / 38**（基线 36 + 本任务 2 条） | `playmode_results4.xml` |
| 原生 | **281 用例 / 771,162 断言**，0 failed | `native_test.log`（exit 0） |

PlayMode 全量共跑 4 次，如实记录：第 1 次 36/38（我的用例 + `DoorToggleEvents_BrightenTheFrame...` 像素证据
`0.0003 < 0.0040`）、第 2 次 37/38（仅我的用例，原因是它依赖真实启动链、被前面 fixture 遗留的 SCN_Game 干扰，已改为自给自足）、
第 3 次超时（同上，改为自给自足之前）、第 4 次 **38/38 全绿**。那条像素证据失败与 `Docs/Logs/TASK-A-AND-03.md:90` 记录的
"一次性抖动 0.0004 < 0.0040"同型（新程序集首次 Play 会话的 GPU 预热），后续两次全绿；本任务未触碰渲染代码。

### 2.6 未验证项（如实披露）

1. **真机（Android）A-3 表现未跑**：无设备；结论停在"机制 + PlayMode 帧精确用例"。
2. **A-3 的用户可见症状未复现**：见 2.3.3，真实启动链在旧逻辑下也是绿的（本机静态时序如此）。审计原文对此标注"未坐实"，本轮把它落成事实。
3. **浏览器真实表单 POST 未跑**：用带 `Origin` 头的 curl 做等价复现（浏览器跨站 `form` POST 必带 Origin、且不发预检）；
   没有构造 DNS rebinding 的真实攻击链。
4. **`Tools/dev.ps1` 修复后端到端未重跑**：GUI Editor 已关闭，未再拉起。等价路径已验（无 Origin 的 `POST /menu` 200、
   `GET /health` 200），`Invoke-RestMethod` 不带 Origin。
5. **队列上限只能靠探针验证**（原因为并发 8 使 `pending ≤ 8`），front door 不可达。
6. **`DevScreenshot`/`DevTestRuns` 未受本轮改动影响**（只走 `DevResponse` + 同一 `WriteResponse`），未单独回归其 defer 路径。

### 2.7 现场

`git status`：**6 改（含本日志）+ 2 新增（含 Unity 生成 .meta）+ `Tools/dev.ps1` 2 行**，无 `URP_*.asset` / `QualitySettings.asset` 改动：

```
 M Docs/Logs/TASK-A-AUDIT-02.md          （本报告）
 M Tools/dev.ps1                          （menu 用法示例改为白名单内的 Tools/ 路径）
 M .../Editor/DevServer/DevHttp.cs          （去通配 ACAO，corsOrigin 参数，403 Forbidden）
 M .../Editor/DevServer/DevRouter.cs        （/menu 白名单前缀 + 环境变量追加）
 M .../Editor/DevServer/DevServerBootstrap.cs（guard 姿态日志 + 后台拒绝日志泵）
 M .../Editor/DevServer/DevServerCore.cs    （并发/队列上限 + RejectClient + 日志队列）
 M .../View/Input/Touch/TouchControlsView.cs（去重判据 + 重绑 bridge + Bridge 只读属性）
?? .../Editor/DevServer/DevGuard.cs(+.meta)
?? .../Tests/PlayMode/TouchControlsSceneReentryPlayModeTests.cs(+.meta)
```

未 `git commit`、未 `git add`。改动文件全部 UTF-8 **无 BOM / LF**（逐文件 `od` + `grep -c $'\r'` 复核；`Tools/dev.ps1`
保留原 BOM 与 LF）。临时探针 `TempA2ProbeTests.cs` 已 `mv` 归档，不在 `Assets` 内。

---

---

## 验收清单（供验收代理）

- [ ] V1 A-1 复现：改前带恶意 Origin 的请求**确实**能执行菜单项（先证明缺陷存在，不是凭空修）
- [ ] V2 A-1 修复：恶意 Origin → 403 且不执行；无 Origin curl 正常；无 `ACAO: *`；`Host: evil.example` → 403
- [ ] V3 A-2：并发超限/队列超限均返回结构化拒绝，不挂 300s
- [ ] V4 A-3：PlayMode 连续两次进入场景控件仍在；**改回旧逻辑用例变红**（有牙）
- [ ] V5 回归：EditMode 797 / PlayMode 36→37+ / 原生 281/771,162
- [ ] V6 未扩大战线（没顺手改审计其余条目）；未 commit；现场干净

## 验收结论（验收代理写）

- **验收代理**：TASK-A-AUDIT-02 验收（与施工非同一代理），2026-11-03。
- **方法**：**不以施工报告为证据**。A-1 起真实 GUI Editor（不带 `-force-d3d12`）打真 HTTP；A-1 的"改前"用**变异重建**
  取证（把三道闸门在源码里临时还原成 HEAD 行为、重启 Editor 亲打，再还原）；A-3 做独立变异；回归三套自跑。
- **证据目录**：`TempLogs/TASK-A-AUDIT-02/verify/`（`editor_verify.log`、`a1_verify.txt`、`a1_prefix_repro.txt`、
  `editor_mutant_aprefix.log`、`a2_verify.txt`、`mutant_touch_playmode.xml`、`playmode_full.xml`、`editmode_full.xml`、
  `native_verify.log`、`a1_restored_recheck.txt`、`editor_restored.log`、`final_md5.txt`、`mutation_backup/`）。
- **DevServer 默认姿态**：仍是**默认开启**（`EditorPrefs` 缺省 `true`）。本轮**未改默认值**，无需额外开启步骤即可验；
  三道闸门（Host/Origin/`/menu` 白名单）在进程内无条件生效（见 V2）。

---

### V1 A-1 改前复现 —— **PASS**

做法：把 `DevServerCore` 的 `DevGuard.Authorize` 调用、`DevHttp` 的 ACAO、`DevRouter` 的 `IsMenuPathAllowed`
临时还原成 HEAD 行为（变异备份 `verify/mutation_backup/`），重启 Editor（`editor_mutant_aprefix.log:296` 起监听），用 curl 亲打：

| 请求 | 实测 |
|---|---|
| `POST /menu?path=Tools/ZFrame/Dev Server/Log status` + `Origin: http://evil.example` | **`HTTP/1.1 200 OK`** + `Access-Control-Allow-Origin: *` + `{"ok":true,...}` |
| `POST /menu?path=Window/General/Console`（无 Origin，**白名单外**） | **`200`**，`{"ok":true,"path":"Window/General/Console"}` |
| 同上探针 + `Host: evil.example` | **`200`** + `ACAO: *` |
| `GET /console?limit=3` + `Origin: http://evil.example` | **`200`**，2867 字节正文被完整读走 |

**"菜单项真被执行了"的硬证据**（不靠施工自述）：该探针菜单项自己打日志，变异版日志里出现 2 条
`[DevServer] running=True port=17321 pending=0`；`GET /console` 抓到的正文里，堆栈是
`UnityEditor.EditorApplication:ExecuteMenuItem` ← `DevRouter:ExecuteMenu (DevRouter.cs:193)` —— 即请求确实一路走到
`ExecuteMenuItem`。缺陷成立，施工报的 4 条"改前"事实与本轮独立复现一致（本断言来自实测，不采信报告）。

还原后 `md5sum` 与变异前逐字节一致（见 V4 末尾同一张表）。

### V2 A-1 修复 —— **PASS**

同一份被验收代码（`DevGuard` 启用态）起 GUI Editor（`editor_verify.log:254` `listening ... guard: loopback Host/Origin only, /menu allowlist`），
探针仍用 `Tools/ZFrame/Dev Server/Log status`（无害、可观察；**全程未用破坏性菜单项**）：

| # | 攻击 | 实测关键响应行 | 菜单是否执行 |
|---|---|---|---|
| R1 | `POST /menu` + `Origin: http://evil.example` | **`HTTP/1.1 403 Forbidden`** `{"ok":false,"error":"origin not allowed: 'http://evil.example' ..."}` | **否**（`running=True` 计数 0） |
| R2 | `Origin: null` | `403`，`origin not allowed: 'null'` | 否 |
| R3 | `Host: evil.example` | `403`，`host not allowed: 'evil.example' (only 127.0.0.1 / localhost)` | 否 |
| R4 | `GET /console?limit=3` + `Origin: evil` | `403`，正文（Console 内容）**读不到** | — |
| R5 | 无 `Origin`（curl/dev.ps1 姿态） | `200`，`{"ok":true,...}` | **是**（计数 0→1） |
| R6 | 无 `Origin` 但 `path=Edit/Play`（白名单外） | `403`，`menu path not allowed: 'Edit/Play'（允许前缀：Tools/...）` | **否**（日志无 `Entering Playmode`） |
| R7 | `Origin: http://localhost:3000`（回环） | `200` + **`Access-Control-Allow-Origin: http://localhost:3000`** + `Vary: Origin` | 是 |

- **无 `ACAO: *`**：`grep -c 'Access-Control-Allow-Origin: \*' a1_verify.txt` = **0**（全部响应）。
- **403 记日志**：`editor_restored.log` 有 `[DevServer] request rejected: origin not allowed ... [POST /menu]`、
  `... host not allowed ... [POST /menu]`。
- **`Tools/dev.ps1` 未被打坏**：真跑 `powershell -File Tools/dev.ps1 health` → 完整 JSON（`"ok": true`）；
  `dev.ps1 menu "Tools/ZFrame/Dev Server/Log status"` → `{"ok": true, ...}`（**这条施工自述"未端到端重跑"，本轮补上**）。
- **额外绕过尝试**（全部 `403`）：`Origin: http://127.0.0.1.evil.example`、`http://127.0.0.1@evil.example`（userinfo 夹带）、
  `http://evil.example/127.0.0.1`、`file://`、`Host: 127.0.0.1.evil.example:17321`；裸 socket 发**两个 Host**
  （`127.0.0.1:17321` 在前、`evil.example` 在后）→ `403 host not allowed: 'evil.example'`（头字典后写覆盖，fail-closed）。
  正控制 `Host: 127.0.0.1:17321` 无 Origin → `200`。

### V3 A-2 并发/队列上限 —— **PASS with 受限**

并发（阈值按施工实现 `MaxConcurrentClients = 8`）：先占 8 条空闲 TCP 连接，再发第 9 条 `GET /health`：

```
HTTP/1.1 503 Service Unavailable
Content-Length: 62
{"ok":false,"error":"too many concurrent connections (max 8)"}
[curl time_total=0.001800s]
```
释放 8 条后同一请求 `200 OK`（`time_total=0.001866s`）。**没有挂 300s**，结构化正文完整送达（施工说的 RST 丢正文已不成立）。

**受限项（照实写）**：`MaxQueuedRequests = 64` 这条**front door 永远构造不出来**——并发上限 8 使"在途请求数 ≤ 8"，
队列不可能到 64。所以我**无法亲测**"队列满返回结构化拒绝"；只能说该分支与已实测的 503 走同一个 `WriteResponse/DevResponse.Error`
（`DevServerCore.cs:197-201`），并原样保留。施工的"队列上限只能靠探针验证"这一披露属实，本项按"冗余闸门、不可达"记账
（**建议**：要么把队列上限降到 < 8 让它有意义，要么删掉以免误以为是有效保护）。

### V4 A-3 —— **PASS with 受限**

- 用例已在仓内：`Tests/PlayMode/TouchControlsSceneReentryPlayModeTests.cs`（2 条：真实两次 `SCN_Empty` 载入 + 同帧
  `Shutdown`/`Spawn` 的帧精确机制）。
- **我自己做变异**（不是沿用施工的 xml）：把 `Spawn` 去重改回旧逻辑 `if (existing != null) { return existing; }`，
  跑该文件（`mutant_touch_playmode.xml`）：

```
total="2" passed="1" failed="1"
Spawn_DuringTheDestroyWindow_BuildsANewOverlay_InsteadOfReturningTheDoomedOne : Failed
  the doomed instance must never be handed out again
  Expected: not same as <[TouchControls] ...>  But was: <[TouchControls] ...>   (Tests/.../TouchControlsSceneReentryPlayModeTests.cs:165)
SecondSceneVisit_KeepsTheOverlay_AliveAndWiredToTheBridge                     : Passed
```
  → 旧逻辑下该用例**恰好变红**、断言点与缺陷一一对应（有牙）。
- **限制（施工已如实披露，我复核属实）**：真正对应用户症状的 `SecondSceneVisit_...` 在旧逻辑下**仍是绿的**；有牙的只有
  "同帧销毁窗口"那条机制用例。即"第二次进场景控件消失"这一**用户可见症状仍未被红/绿可区分的用例锁死**，现有套件锁的是机制。
  修复本身正确且必要，但这条锁的强度应记为"机制级"，不是"症状级"。
- **还原证明**：`md5sum -c touchview_pretest_md5.txt` → `OK`（`f252e7734f2b6f126a608013a40d56c7`，与变异前逐字节一致），
  `git diff --stat` 该文件 = `21 insertions(+), 2 deletions(-)`（与施工报告一致）。

### V5 回归（三套自跑） —— **PASS**

| 套件 | 实测 | 证据 |
|---|---|---|
| EditMode | `testcasecount="797" passed="797" failed="0"` | `verify/editmode_full.xml` |
| PlayMode | `testcasecount="38" passed="38" failed="0"`（含本任务 2 条） | `verify/playmode_full.xml` |
| 原生 | `test cases: 281 | 281 passed | 0 failed`；`assertions: 771162 | 771162 passed`；exit 0 | `verify/native_verify.log` |

本轮 PlayMode 一次全绿（未复现施工记录的 `DoorToggleEvents` 像素抖动）。原生侧另跑了完整
`Tools/build_native.ps1`（configure+build+test+deploy）：**`exit=0`**，尾部 `ALL GREEN ... succeeded`，同样 281/771,162
（`verify/build_native_verify.log`）。

### V6 未扩大战线 / 未 commit / 现场干净 —— **PASS**

- `git status`：7 改 + 4 新，与施工报告清单**完全一致**；`git diff --stat` = **445 insertions(+), 14 deletions(-)**（与报告同数）。
  改动面只有 `Editor/DevServer/*`（A-1/A-2）+ `View/Input/Touch/TouchControlsView.cs`（A-3）+ `Tools/dev.ps1` 注释 + 本日志
  → 审计其余 6 条（A-4…A-9）**一条未碰**。
- **未 commit**：`git log -1` 仍为 `d00708e`；未 `git add`。
- **禁改文件**：`TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`/`FrameGovernor`/`GovernorLadder`/`GovernorTuning` 均无改动。
- **进程/现场**：验收起的 3 个 GUI Editor 全部已 `taskkill` 关闭，无 `Unity.exe` 残留（仅剩 TIME_WAIT 套接字），
  `UnityProject/Temp/UnityLockfile` 已清；`git status` 无 `URP_*.asset`/`QualitySettings.asset` 等 Editor 自动改写。

---

### 观察（不影响本次裁决，供主控决定是否另派）

1. **队列上限是死代码**（见 V3）：并发 8 让 `pending` 结构上 ≤ 8，`MaxQueuedRequests = 64` 永不触发。
2. **`/menu` 白名单外拒绝不记日志**（只有 Host/Origin 拒绝进 `_pendingLogs`）；`Edit/Play` 被拒在日志里无痕，排障时不可见。
3. **白名单粒度是前缀**：`Tools/` 含 `Tools/ZFrame/Dev Server/Enabled`（可关掉服务）与三个校验器菜单项——都是无害/项目自用，
   但"前缀"不等于"白名单枚举"，将来 `Tools/` 下新增破坏性项会被自动放行。
4. **`DevServerBootstrap.cs:121` 日志串里混了全角右括号**（`... /menu allowlist）`），纯观感问题。
5. **验收自身操作事故（如实披露）**：我在 A-3 变异还原时误执行了一次 `git checkout -- .../TouchControlsView.cs`，把修复版
   回退到 HEAD。已按本日志 §2.3 的 diff 原文重建，并以**逐字节 md5 校验**（`f252e77...` 与误操作前完全一致）确认恢复；
   随后重启 Editor 复核 R1'/R3'/R6' 三发攻击仍全部 `403`（`a1_restored_recheck.txt`），PlayMode/EditMode/原生三套在恢复后重跑全绿。
   交付状态与施工交接时**完全相同**（diffstat 445/14 亦相同）。

---

### 最终裁决

**通过（PASS）**，附两条受限记账与五条观察。

- A-1（P0）：**已实测关闭**——四个攻击面（恶意 Origin / `null` / 恶意 Host / 带 Origin 读 `/console`）全部 `403` 且菜单未执行，
  通配 ACAO 归零，脚本通道（curl + `dev.ps1`）完好；且有变异级"改前"对照证明缺陷真实存在。**这是安全项，本结论只基于亲打请求，不基于读码或施工自述。**
- A-2（P1）：并发上限实测有效（第 9 条 1.8ms 拿结构化 503，不挂 300s）；队列上限**不可达**，只能算冗余闸门。
- A-3（P1）：机制修复正确、用例有牙（变异必红），但"第二次进场景"的用户可见症状仍只有绿用例守护。

