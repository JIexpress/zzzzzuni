# TASK-A-AUDIT-01 —— 第二轮独立代码审计（测试绿灯照不到的缺陷）

> 本任务**只出缺陷清单，不改代码**。修复另开任务。施工代理按本简报执行，第 2 节写审计报告。
> **禁止 git commit**。复核由另一代理执行。

## 任务简报（主控写，2026-11-01）

### 为什么再来一轮

上一轮（R10-02）审计抓出 **17 条真缺陷**，复核 8 条 **0 误报**——包括描述符槽耗尽未校验、Vulkan 描述符池
layout 违规、`Present` HRESULT 静默吞错、资源泄漏。**那次审计的价值高于同期任何新功能。**

自那之后又落地了大量新代码，**全部没被独立审过**：
- R10 各修复批（P1/P2 共 14 条）本身就引入了新逻辑
- **A-DEV 三层开口**：Editor 常驻服务（HTTP/TCP）、运行期 DevConsole（**在发布包里开 socket**）、`Tools/dev.ps1`
- **A-AND**：触屏摇杆/按钮、EventSystem 跨场景生命周期修复

### 审计范围（按风险排序）

**P0 优先——新增且安全/稳定性敏感**

1. **`UnityProject/Assets/ZFrame/**/RuntimeDevConsole*`（A-DEV-03/04）**
   这是唯一**随发布包出厂并监听 TCP** 的代码。关注：门控是否真的不可绕（`Debug.isDebugBuild` / 环境变量 / 命令行三态）、
   命令白名单可否被参数注入绕过、限流配额能否被并发绕过、socket 生命周期（Editor 退出/场景卸载/异常路径是否泄漏）、
   **非开发包是否真的零 socket**、日志里是否泄漏敏感信息
2. **`UnityProject/Assets/ZFrame/Editor/DevServer/`（A-DEV-01/02）**
   手写 HTTP 解析（请求行/头/Content-Length）——关注：畸形请求、超长头、`Content-Length` 与实际不符、
   并发多连接、端口占用与重启竞态、**后台线程是否真的零 Unity API 调用**、请求队列无界增长（内存）
3. **`UnityProject/Assets/ZFrame/View/Input/Touch/`（A-AND-04）**
   关注：指针 id 泄漏（down 无 up）、多指交叉、UI 元素销毁时未解绑、`Screen.safeArea` 变化时重算、
   在非触摸设备被误创建的路径
4. **`UIRoot` / EventSystem 生命周期（A-AND-04/05 修复）**
   关注：`DontDestroyOnLoad` 与父子关系的组合是否在所有加载路径下都成立（不只单模式）、
   重复 `EnsureCreated` 的幂等性、Editor 退出播放模式时的清理

**P1——既有高风险但上轮未深挖**

5. `Native/zfgprobe/`（DXGI 代理 vtable hook）——COM 引用计数、loader lock、失败回退的真实性
6. `Native/zupscaler/`——FG/FSR 上下文在分辨率变化/设备丢失时的重建
7. `Native/zkernel/`——SoA 增删边界（B-R8-03 修过 Remove 越位写，同类是否还有）
8. `UnityProject/Assets/ZFrame/Core|Data`——存档原子性、事件订阅泄漏、`IDisposable` 配对

> 范围可裁剪，但**裁了哪块、为什么，必须在报告里说明**。

### 审计方法（沿用 R10-02，必须照做）

1. **先读测试，再读实现**：先摸清"测试覆盖了哪里"，然后**专挑测试没覆盖的分支**——
   错误路径、边界值、并发窗口、失败回退、异常中断。
2. 每条结论必须给：`文件:行号` + 代码片段 + **为什么现有测试抓不到** + 触发条件 + 后果
   （崩溃/泄漏/静默错误/数据损坏/安全越界）+ 置信度（确定/高/中）。
3. 允许写**临时探针**验证（小程序/单测），**不许改产品代码**；探针用完归档到 `TempLogs/archive_pre_audit/`。
4. **禁止**：纯风格、命名、"建议加注释"、"可以考虑重构"这类无缺陷内容。
   **清单要短而硬，不要长而软**——宁可 8 条真缺陷，不要 30 条注水。

### 输出格式（写进本日志"审计报告"节）

```
## P0（崩溃/数据损坏/安全边界）
### A-1 <一句话标题>
- 位置：`path:line`
- 代码：<关键片段，3-8 行>
- 为什么测试抓不到：<具体说明>
- 触发条件：<可复现的最小条件>
- 后果：<崩溃/泄漏/静默错误/...>
- 置信度：确定/高/中

## P1（功能错误/资源泄漏/可维护性风险）
...
## P2（低危/理论问题）
...
## 已排除（值得记录但不算缺陷）
...
```

### 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- **不改产品代码**（只出清单）
- 环境纪律见 `Docs/交接档案.md` §3（Unity 单实例、勿带 `-quit`、清代理变量、Bash 禁转 powershell）
- 清理用 `mv` 归档，不要 `rm -rf`

---

## 审计报告（施工代理写）

- **审计对象**：HEAD `6a094e6`（工作区干净），**只读**——未改任何产品代码/测试，未 `git add`/`commit`，
  未动 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`。唯一写入是本日志与 `TempLogs/archive_pre_audit/`（已 gitignore）。
- **方法**：先读测试划定覆盖边界（`RuntimeDevConsolePlayModeTests.cs` 12 条、`TouchControlsTests.cs` 14 条、
  `UIHierarchyTests.cs`、`UIRootCrossSceneEventSystemPlayModeTests.cs`、`SaveSystemTests.cs` 34 条；
  **`Editor/DevServer/` 零测试**），再逐行读实现，专挑测试结构上走不到的分支：
  协议边界（浏览器/非 Unity 客户端）、并发窗口、失败回退、销毁时序、跨 AppDomain。
- **统计**：P0 **1** 条、P1 **3** 条、P2 **5** 条，共 **9** 条。
- **写了 1 个探针**（`TempLogs/archive_pre_audit/probe_reuseaddr.py` + `.log`）：坐实 Windows 上
  `SO_REUSEADDR` 允许同一 `127.0.0.1:port` 被两个 socket 同时 `bind+listen`（B-2 的证据，实测非推断）。
  其余结论为静态证据 + 仓内既有测试/产物，未启动 Unity、未跑设备。

---

## P0（崩溃/数据损坏/安全边界）

### A-1 Editor DevServer 无任何来源校验 + 通配 CORS + 默认开启 + `/menu` 直通 → 任意网页可让 Editor 执行任意菜单项、并读走 Console

- **位置**：`UnityProject/Assets/ZFrame/Editor/DevServer/DevHttp.cs:282`（响应头）、
  `Editor/DevServer/DevRouter.cs:79-82` 与 `:160-186`（`/menu` → `EditorApplication.ExecuteMenuItem(path)`）、
  `Editor/DevServer/DevServerBootstrap.cs:82`（默认开启）
- **代码**：
  ```csharp
  // DevHttp.cs:282 —— 每个响应都带这一行，包括 /console 的正文
  head.Append("Access-Control-Allow-Origin: *\r\n");

  // DevRouter.cs:170 —— path 直接来自 query，无白名单、无 Origin/Host 校验
  bool executed = EditorApplication.ExecuteMenuItem(path);

  // DevServerBootstrap.cs:82 —— 缺省 true：装了工程就监听，不需要任何人点菜单
  if (!EditorPrefs.GetBool(PrefEnabled, true)) { ... }
  ```
  全目录 `grep -niE "origin|referer|csrf|token"` 只命中上面那一行 `Access-Control-Allow-Origin: *`，
  **没有任何请求侧校验**（无 `Origin`、无 `Host`、无随机 token、无 `Sec-Fetch-*`）。
- **为什么测试抓不到**：`Editor/DevServer/` **一条测试都没有**（`find` 无 `*DevServer*Test*` / `*DevHttp*Test*`）。
  现有验收方式是 `Tools/dev.ps1` + 人肉 `curl`——两者都**不是浏览器**，都不带 `Origin`，所以"来源校验缺失"
  这类缺陷在验收方法上完全不可见；而 `Access-Control-Allow-Origin: *` 恰恰只在浏览器里才起作用。
- **触发条件**：开发者本机开着 Editor（默认就监听 `127.0.0.1:17321`），用浏览器打开任意第三方页面，页面里：
  ```html
  <form action="http://127.0.0.1:17321/menu?path=Assets/Reimport%20All" method="POST"></form>
  <script>document.forms[0].submit()</script>
  ```
  `form` POST 带 `application/x-www-form-urlencoded`，属 CORS **简单请求、不触发预检**；
  服务端不看 `Content-Type`、不看 `Content-Length`，照样解析并执行。同法可打 `/compile`、`/test?mode=playmode`、`/screenshot`。
- **后果**：① **任意菜单项执行**（`ExecuteMenuItem` 覆盖整个 Unity 菜单树：删除/移动资源、切 Play、
  跑构建、项目自定义 `Tools/*`）——等价于在 Editor 进程里执行开发者权限的任意操作；
  ② `GET /console` 因通配 ACAO 可被 `fetch` **读走正文**（`DevConsoleRing` 2000 条，含绝对路径、堆栈、可能的凭据/内容）；
  ③ DNS rebinding 下连 Host 都不可信，若将来补了 Origin 白名单也照样被绕。
  这不是理论：Docker/Jupyter/VS Code 的本地端口都栽在同一个模式上，而本服务**默认开启**且把整个菜单树当 API 暴露。
- **置信度**：确定（代码事实 + 浏览器同源策略事实；`EditorApplication.ExecuteMenuItem` 语义见 Unity 文档）
- **快速验证**：`curl -i -X POST "http://127.0.0.1:17321/menu?path=Edit/Play" -H "Origin: http://evil.example"`，
  看响应头是否回 `Access-Control-Allow-Origin: *` 且 `ok:true`（服务不区分 Origin，必回）。

---

## P1（功能错误/资源泄漏/可维护性风险）

### A-2 `DevServerCore` 既无并发上限也无队列上限，每条连接阻塞一个 ThreadPool 线程 300s

- **位置**：`Editor/DevServer/DevServerCore.cs:17`、`:105`、`:135-137`
- **代码**：
  ```csharp
  const int RequestTimeoutMs = 300000;                       // :17
  TcpClient captured = client;
  ThreadPool.QueueUserWorkItem(_ => ServeClient(captured));  // :105 —— 来者不拒，无并发计数
  ...
  _queue.Enqueue(request);                                   // :135 —— 无容量检查
  if (!request.WaitForResponse(RequestTimeoutMs)) { ... }    // :137 —— 每连接阻塞一个池线程，最长 5 分钟
  ```
- **为什么测试抓不到**：DevServer 无测试。而且**同为三层开口的 B 层已经修过同一个问题**——
  `RuntimeDevConsoleServer` 有 `MaxConcurrentClients = 8`（`:49`）、`MaxQueuedRequests = 64`（`:54`），
  并有 `H2_ConcurrentOverLimit_RejectedWithStructuredError` / `H2_RequestsOverRateLimit_ReturnRateLimited`
  两条用例锁着。A 层（Editor）反而一条都没有，**同一份设计纪律只落到了一半**。
- **触发条件**：主线程被导入/编译占住（正是 300s 超时的设计场景）时，本机任意进程并发发起 N 条连接。
  浏览器里 `for (let i=0;i<500;i++) fetch('http://127.0.0.1:17321/compile',{method:'POST'})` 即可（简单请求，无预检）。
- **后果**：① 每个连接占用一个 ThreadPool 工作线程最长 300s，ThreadPool 注入速率约 1 线程/500ms，
  Editor 自己的 `Task`/`async` 工作（资产导入、Addressables、TestRunner）被饿死；
  ② `_queue` 无上限，每条 `DevHttpRequest` 可带 **1MB** body（`DevHttp.cs:92` `MaxBodyBytes`）→ 内存线性增长；
  ③ 连接数不设限时 `AcceptLoop` 本身不阻塞，服务不会自我保护，只能等 Editor 变卡才被发现。
- **置信度**：高（代码事实；具体退化程度依机器与主线程占用时长）

### A-3 `TouchControlsView.Spawn` 的去重分支会返回"本帧待销毁"的旧 overlay，且不重连 bridge → 第二次进游戏场景后触屏控件永久失效

- **位置**：`View/Input/Touch/TouchControlsView.cs:120-124`（去重）、`:180-183`+`:219-227`（`Shutdown` 走 `Destroy` 延迟销毁）；
  调用时序 `Game/View/UI/GameHud.cs:124-128`、`Game/Bootstrap/GameBootstrap.cs:536-538`、
  `View/Scene/SceneLoader.cs:270`（`FinishLoad` 在同一个 `Update` 里发 `SceneLoadedEvent`）
- **代码**：
  ```csharp
  TouchControlsView existing = hud.GetComponentInChildren<TouchControlsView>(true);   // :120
  if (existing != null) { return existing; }                                          // :121-124

  // Teardown(destroySelf:true) 在 play 模式用的是 Destroy（延迟到帧末），不是 DestroyImmediate
  if (Application.isPlaying) { Destroy(gameObject); } else { DestroyImmediate(gameObject); }   // :219-227
  ```
  时序（同一帧内）：SCN_Game 卸载 → 旧 `GameHud.OnDestroy` → `Shutdown()` → `Destroy(overlay)`（**帧末才真正销毁**）
  → `SceneLoader.Update` 检测 `isDone` → `FinishLoad()` 同帧 `Fire(SceneLoadedEvent)`
  → 新 `GameHud.Build()` → `Spawn` → `GetComponentInChildren` **仍然找得到那个待销毁的 overlay** → 返回它
  → 帧末它被销毁 → 新 HUD 的 `_touchControls` 变成 fake-null，**此后再没有任何代码重建控件**（`Spawn` 每场景只调一次）。
- **为什么测试抓不到**：① `TouchControlsTests` 全部是 EditMode，`Teardown` 走 `DestroyImmediate` 分支，且断言紧跟其后
  ——**根本没有"延迟销毁窗口"这个时间维度**；② `Spawn_IsIdempotent_ForTheSameRoot` 断言的正是"返回同一个实例"
  （`:133-140`），把缺陷行为当成了契约；③ 没有任何用例做"两次连续的 SCN_Game 加载 + HUD 重建"，
  而 `UIRootCrossSceneEventSystemPlayModeTests` 只加载了 `SCN_Empty`（不建 HUD）。
- **触发条件**：一次进程内**第二次**进入 SCN_Game（主菜单 → 开始 → 暂停退出 → 再开始）。首次进入正常（此时没有旧 overlay）。
- **后果**：真机上摇杆与互动/暂停按钮**静默消失**（对象在、但 `_touchControls` 已失效且不再重建），
  而键盘/手柄路径正常——所以桌面验收永远绿，只有手机上"再玩一次就没法操作"。
  次要面：去重分支 `return existing` **不调用 `view.Build(bridge)`**，`_bridge` 停在上一次的实例上；
  若旧 bridge 已被 `Dispose`，摇杆会把输入写进一个死 bridge。
- **置信度**：高（`Destroy` 延迟到帧末 + `GetComponentInChildren` 同帧可见 + `FinishLoad` 与 `OnDestroy` 同帧，三条都是代码/引擎语义事实）；
  真机表现未实测（本任务未跑设备）

### A-4 `UIRoot` 重收养路径必然重复建 8 个 layer canvas（幂等判据只看内存字典，而字典在 domain reload 里正是丢的那个）

- **位置**：`View/UI/UIRoot.cs:69-75`（重收养）、`:106-109`（跳过判据）、`:111-139`（重建）
- **代码**：
  ```csharp
  foreach (UIRoot orphan in Object.FindObjectsOfType<UIRoot>(true)) {
      _instance = orphan;                       // :71 —— 场景里的 root 还在，子 canvas 也还在
      orphan.BuildLayersIfMissing();            // :72
  ...
  if (_layerCanvases.Count == UIConsts.LayerCount) { return; }   // :106 —— 判据是内存字典
  _layerCanvases.Clear();
  foreach (UIConsts.UILayer layer in UIConsts.AllLayers) { /* 新建 [Layer:x] 并挂到 transform 下 */ }
  ```
  `_layerCanvases` 是 `readonly` 非序列化字段（`:34-35`），domain reload 后**必然为空**，
  而 `[Layer:*]` 子对象作为场景对象**存活**——于是 `0 != 8` → 再建一整套。
- **为什么测试抓不到**：`EnsureCreated_IsIdempotent_AndAdoptsSingleRoot`（`Tests/Debug/UIHierarchyTests.cs:78-85`）
  连调两次 `EnsureCreated`，第二次走的是 `_instance != null` 的早返回（`:64-67`），**重收养分支一次都没进去**；
  全仓也没有任何用例模拟"字典被清空、子对象还在"。这正是"幂等性测试"与"重收养路径"不重合的盲区。
- **触发条件**：Play Mode 下改脚本触发 domain reload（Unity 会重跑 `RuntimeInitializeOnLoadMethod`，
  即 `BootstrapOnLoad` → `EnsureCreated`），或任何代码在 reload 后调 `EnsureCreated`。
  该场景是代码注释自己写明的目标场景（`:57-60`"re-adopts an orphaned root … domain reload disabled"）。
- **后果**：每次重载多 8 个 `Canvas + CanvasScaler + GraphicRaycaster`；reload 前已 Push 的面板仍挂在**旧**
  canvas 上继续渲染与接收 raycast，而 `UIManager`（同样被重置）只认新 canvas → **屏幕上是两套 UI 叠着**，
  旧的那套点得动但谁也管不了。`LayerCanvasCount` 仍报 8，掩盖了实际 16 个的存在。
- **置信度**：高（字典为空 + 子对象存活 + 判据只看字典，三点都是确定事实）；"reload 会重跑 RuntimeInitializeOnLoadMethod" 为引擎行为，中-高

---

## P2（低危/理论问题）

### B-1 `SaveSystem.SaveToSlot` 先把 main 滚走再落盘，且整条写入序列没有 `try/catch`

- **位置**：`Data/Save/SaveSystem.cs:176-201`
- **代码**：
  ```csharp
  if (File.Exists(main)) {
      if (File.Exists(bak1)) { if (File.Exists(bak2)) File.Delete(bak2); File.Move(bak1, bak2); }
      File.Move(main, bak1);            // ← main 此刻已不存在
  }
  WriteDurable(tmp, json);              // ← 抛 IOException 就到此为止（磁盘满/权限/被杀软占用）
  File.Move(tmp, main);
  ```
  与类文档（`:16-19`）承诺的"先写 tmp 再替换"相反：真正慢的一步（`Flush(flushToDisk:true)`，`:215`）
  发生在 main 已被移走之后。
- **为什么测试抓不到**：`SaveSystemTests` 34 条里**没有任何写失败注入**——没有只读目录、没有
  `IOException` 桩、没有"tmp 写一半"用例；所有失败用例都在**读**路径（`Load_Corrupt*`、`Load_ThrowingMigration`）。
  `Save_LeavesNoTmpFileBehind`（`:185`）只走成功路径。
- **触发条件**：磁盘满、存档目录被杀软/同步盘（OneDrive）临时独占、`persistentDataPath` 权限变化。
- **后果**：① `IOException` 直接从 `SaveToSlot` 逃到调用方（当前唯一生产调用点 `GameBootstrap.cs:498` 有 catch，
  所以表现为"存档失败"日志）；② 该槽位在写失败后**没有 main 文件**，只能落回 `.bak1`（内容 = 旧 main，故不丢档，
  但"当前存档"这一代消失了）；③ `.tmp` 残留且无人清理（`DeleteSlot` 之外没有清理点）；
  ④ 崩溃落在 `Move(main,bak1)` 与 `Move(tmp,main)` 之间时，`bak1` 与 `tmp` 同时存在，语义靠人工猜。
- **置信度**：高（代码事实）；实际触发依赖磁盘状态

### B-2 两个服务都用 `SO_REUSEADDR`，在 Windows 上等于放弃端口互斥（探针已坐实）

- **位置**：`Editor/DevServer/DevServerCore.cs:41`、`View/DevConsole/RuntimeDevConsoleServer.cs:111`
- **代码**：
  ```csharp
  _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
  // DevServerCore.cs:40 注释：「避免上一次 domain reload / 快速重启撞 TIME_WAIT」
  ```
- **探针证据**（`TempLogs/archive_pre_audit/probe_reuseaddr.py` + `.log`，本机实测）：两个 socket 都开
  `SO_REUSEADDR` 时，**同一 `127.0.0.1:17399` 两次 `bind+listen` 全部成功**；任一不开则第二次 `WinError 10048`。
- **为什么测试抓不到**：`RuntimeDevConsolePlayModeTests` 只断言"绑到 127.0.0.1、不是 0.0.0.0"（`:192-206`），
  从不验证"端口已被别人占时会失败"；`StartConsole()`（`:650-665`）把"绑定失败就换下一个端口"当成正常路径，
  反而**依赖**"失败会报错"这个前提——而开 ReuseAddress 后该前提不成立。
- **触发条件**：同机已有一个进程先绑了 17321/17322 并同样开了 ReuseAddress（普通 socket 不开则绑不上）。
- **后果**：① 端口占用检测失效——`TryStart` 成功不代表独占，日志却打印"listening on ..."，两条连接流被内核分发到谁身上未定义；
  ② 对本仓意义最大的是 `RuntimeDevConsoleServer`：这是**唯一随包出厂**的监听代码，"是否绑上了"这个机检前提变软；
  ③ Windows 上正确的写法是 `SO_EXCLUSIVEADDRUSE`（或干脆不开），复用语义与 Linux 不同。
- **置信度**：确定（平台行为已实测）；实际被利用需要同机另一进程配合（与 A-1 相比攻击面小）

### B-3 `UIRoot.EnsureEventSystem` 认任何 EventSystem，不校验它是否持久 → 场景自带 EventSystem 时 A-AND-04 的修复失效

- **位置**：`View/UI/UIRoot.cs:142-165`
- **代码**：
  ```csharp
  if (Object.FindObjectOfType<EventSystem>() != null) { return; }   // :144 —— 场景里的也算数
  ...
  if (root != null) { host.transform.SetParent(root, false); }      // :157-160 只有自建的才挂到 root 下
  ```
- **为什么测试抓不到**：`UIRootCrossSceneEventSystemPlayModeTests` 的前置是"UIRoot 先建、自己造出 ES"，
  所以测的永远是自建分支；`UIRoot_EnsuresAnEventSystem`（`UIHierarchyTests.cs:68-75`）只断言"存在一个 ES"，
  不区分它是不是 UIRoot 的孩子。当前 3 个 `.unity` 都不含 EventSystem（`grep -l EventSystem *.unity` 为空），
  所以现状绿——**缺陷只在"未来某场景/预制体自带 EventSystem"这条路径上**。
- **触发条件**：任一场景或常驻预制体带 `EventSystem`，且它在 UIRoot 首次 `EnsureCreated` 时已存在。
- **后果**：与 `fcc59ad` 修掉的那次真机故障完全同型——Single 模式换场景时该 ES 随场景销毁，
  而 `EnsureCreated` 在 `_instance != null` 时早返回、**不会补建**（`:64-67`），此后全局 UGUI 派发死亡。
  修复的守卫只覆盖了"我们自己造 ES"这条路径。
- **置信度**：高（逻辑链确定）；当前场景集下不触发

### B-4 DevConsole `Stop()` 只停 accept 线程：已接住的连接与池线程活到 10s，而 `ListenThreads == 0` 给出了"已彻底停止"的假象

- **位置**：`View/DevConsole/RuntimeDevConsoleServer.cs:131-152`（`Stop`）、`:265-274`（每连接阻塞等待）；断言点 `Tests/PlayMode/RuntimeDevConsolePlayModeTests.cs:183-185`
- **代码**：
  ```csharp
  public void Stop() {
      _running = false;
      try { _listener?.Stop(); } catch { }
      _listener = null;
      /* 只排空队列 + Join(500) accept 线程；已 accept 的 TcpClient 与 ServeClient 线程一概不管 */
  }
  ...
  if (!command.Wait(CommandTimeoutMs)) {  // 10000ms，Stop 之后仍会等满
  ```
  排空队列时 `Complete("dev console stopped")` 只覆盖**还在队列里**的命令；已被某个 worker 取走、
  正在 `Wait` 的那条等不到 `Pump`（宿主已销毁），必然等满 10s 才写超时。
- **为什么测试抓不到**：`Gate_DevelopmentBuild_Listens_AndAnswersHelp`（`:183-185`）在 `Stop()` 后
  `yield return null` 一帧就断言 `ListenThreads == 0`——它量的是 **accept 线程**，恰好是 `Stop` 唯一等的东西；
  没有任何用例在 `Stop` 之后再去连端口、或断言 `ActiveClients == 0`（虽然有 `ActiveClientsForTests` 这个口子）。
- **触发条件**：调用 `Stop()` 时有客户端正卡在命令等待里（主线程忙、或 `shot` 正在跑）。
- **后果**：`Stop()` 返回后端口已释放，但最多 8 条 socket + 8 个池线程还会存活最长 10s 并写出"timeout"响应；
  在 Editor 里紧接 `Stop()` 再 `StartListener` 同端口时，旧连接与"新会话"的行为边界不可预期。
  不影响游戏逻辑（无 Unity API），故列 P2。
- **置信度**：高（代码事实）；"造成实际故障"未观测到

### B-5 DevConsole 命令超时后仍会在主线程执行——副作用发生在"已告知失败"之后

- **位置**：`View/DevConsole/RuntimeDevConsoleServer.cs:265-272`、`View/DevConsole/RuntimeDevConsole.cs:225-245`
- **代码**：
  ```csharp
  _queue.Enqueue(command);                         // :266
  if (!command.Wait(CommandTimeoutMs)) {           // :268 客户端已收到 timeout
      WriteLine(stream, ErrorJson("command timeout ...")); return;   // 命令仍在 _queue 里
  }
  // Pump 之后照常取出并执行：
  response = RuntimeDevConsoleCommands.Execute(command.Line);        // RuntimeDevConsole.cs:234
  command.Complete(response);                                        // :243
  ```
- **为什么测试抓不到**：`IdlePump_OverheadAndDumpLatency_AreReported` 只断言往返 < 2000ms；
  超时路径（主线程被卡 10s）在任何用例里都不会发生——那正是它要构造的前提。
- **触发条件**：主线程单帧阻塞 > 10s（大场景加载、`shot` 排队、GC），期间客户端发 `shot`/`quality`/`fg`。
- **后果**：客户端收到 `ok:false/timeout` 并按"失败"处理（可能重试），但命令随后照样执行：
  `shot` 落一张盘（计入 200 张配额）、`quality` 切档、`fg` 切插帧。重试者会看到"失败但生效"，
  自动化脚本按"失败"分支走会与真实状态分叉。
- **置信度**：高（代码事实）；影响限于开发包 + 本地自动化

---

## 已排除（值得记录但不算缺陷）

1. **DevConsole 门控不可绕（确认）**：`RuntimeDevConsoleGate.IsEnabled()` 只有三步——`null → false`、
   `IsExplicitlyDisabled → false`、`return isDevelopmentBuild()`（`Gate.cs:52-60`）；`argv`/`env` **只能关不能开**；
   `Debug.isDebugBuild` 无运行时写入口；`TryEnableForTests`/`ActiveClientsForTests` 被
   `#if UNITY_EDITOR || UNITY_INCLUDE_TESTS || DEVELOPMENT_BUILD` 挡在发布包外（`RuntimeDevConsole.cs:97-114`）；
   `StartListener` 已收成 private（`:120`）。A-DEV-04 的 H1 三项（`SetOverrideForTests`/可写 probe/public `TryStart`）确已移除。
2. **DevConsole 命令白名单无注入（确认）**：硬 `switch` + 六个字面量（`Commands.cs:20-52`），无反射无 eval；
   `shot` 的文件名过 `Sanitize`（仅 `[a-zA-Z0-9_-]`，≤64，`Commands.cs:473-494`），`spark` 数值 `Clamp`（`:186`），
   `quality` 走名字匹配 + 序号上界检查（`:228-240`）。参数注入无面。
3. **DevConsole 限流不可并发绕过（确认）**：`TryConsumeRateBudget` 整段在 `lock (_rateLock)` 里做固定窗口扣减
   （`Server.cs:290-311`），不是"先读后写"；并发上限是 `Interlocked` 计数 + 立即拒绝（`:195-202`）。
4. **DevConsole 对浏览器攻击天然免疫（对照 A-1）**：它是**裸 TCP 行协议**，浏览器发来的
   `GET / HTTP/1.1` 首行被当作命令名 → `unknown command: GET` → 回一行 JSON（不是合法 HTTP 响应），
   `fetch` 必失败。所以"发布包里开了 socket"这件事**没有**引入 A-1 那种 CSRF 面。这是两层开口里 B 层比 A 层更安全的地方。
5. **存档模块级异常隔离**（`SaveSystem.cs:418-433`）会让一个模块失败、其余恢复、整体仍返回成功——
   随后一次自动保存会用"部分状态"覆盖三代备份。这是注释里明确写下的取舍（"Throwing here would leave earlier
   modules applied and later ones not"），按简报口径不算缺陷，登记在此供后续评估是否需要 `partial=true` 标记。
6. **备份滚动在崩溃点上的可恢复性**：`main` 被移走 → `tmp` 完整 → 崩在两者之间时 `bak1` 恒等于旧 `main`，
   `LoadFromSlot` 会按 main→bak1→bak2 顺序找回，**不丢档**。B-1 报的是异常路径与 `.tmp` 残留，不是丢档。

---

## 范围裁剪与未坐实（诚实说明）

### 裁掉了什么，为什么

| 区域 | 处置 | 理由 |
|---|---|---|
| `Native/zfgprobe/`（DXGI 代理/vtable hook） | **仅抽读**，未重审 | R10-02 已出 A-1/A-2/A-3/A-4/A-7/B-1/B-2/B-7 共 8 条，**至今未修**；在修复落地前重审同一批代码只会复述上一轮结论，拿不到新信息。本轮预算改投"从未被审过"的新代码。 |
| `Native/zupscaler/` | **裁剪** | 只核了 `upscaler_chain.cpp:280-287`（`Resize` 纯转发，失败返回 false 由调用方处置）与 `SelectAndInitialize` 的回退链（`:259-277`：`supported` 拒绝不计入 attempt、失败 `delete instance` 无泄漏）。FSR/FG 的上下文重建属 R10-02 已列面（A-4/B-3），未再逐行。 |
| `Native/zkernel/` | **定点复核，未全量** | 只打简报点名的增删边界：`RemoveRowSwapBack`（`zkernel.cpp:118-140`，`count==0` 的下溢由三个调用点的前置校验挡住）、`MigrateComponent` 的 `ZkBitsBelow(dst.mask, …)` 列槽位重映射（`:370-392`，Remove 时跳过被删列、其余列 dst/src 索引各自算，**正确**）、`Compact` 的 `need = ceil(count/cap)` 与 `rowEntity` 同步收缩（`:705-712`，一致）。零列 archetype 走 `_aligned_malloc(1, 1)`（`maxAlign` 默认 1），合法。**未发现新缺陷**，但这不等于 zkernel 全绿——`EachParallel` 的线程与异常边界没审。 |
| `ZFrame/Core|Data` 其余部分 | **只到存档 + GameHud** | 事件订阅泄漏抽查了 `GameHud`（`GameHud.cs:107-113`，`_tokens` 全量 Dispose，配对成立）。`EventBus`/`Services`/对象池/资源服务的生命周期未审——R10-02 已列过同一面且未修。 |
| Touch 的**真机**行为 | **未测** | 本任务未启动 Unity、未连设备。A-3 的结论停在"代码路径 + 引擎延迟销毁语义确定"，机型上的实际表现未复现。 |

### 我怀疑但没坐实的（不要按"缺陷"直接排任务）

1. **A-3 的真机表现**（标"未坐实"）：机制链是确定的，但"第二次进 SCN_Game 时 `FinishLoad` 与旧 HUD 的
   `OnDestroy` 落在同一帧"这一点，我是从 `SceneLoader.FinishLoad`（`:270`）与 Unity 的场景激活时序推出来的，
   **没有在设备或 PlayMode 里跑过两次连续加载来证实**。验证方式：PlayMode 里连续两次 `SCN_Game` 加载，
   断言第二次的 `TouchControlsView` 不是 pending-destroy 实例（或直接断言 `_touchControls` 在帧末仍非 null）。
2. **B-4 的实际影响**（标"未坐实"）：`Stop()` 后残留的连接是否会与"同端口重启的新会话"互相干扰，
   取决于内核如何分发已建立的连接；我没有构造实验。代码事实（不关客户端 socket、不等 worker）是确定的。
3. **A-4 的触发前提**（标"未坐实"）：domain reload 后 Unity 是否**确实**重跑
   `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]`，我依据的是引擎行为常识 + 代码注释自述的目标场景，
   本机没有做"改脚本触发 reload 并数 canvas 个数"的实验。这是 A-4 唯一的不确定环节（后果与判据本身是确定的）。
4. **`DevScreenshot._pending` 跨 domain reload**（未列为条目）：静态 `List<PendingCapture>` 持 `DevHttpRequest`，
   reload 时 `EditorApplication.update -= Tick` 不会执行、8s 超时也不会再跑 → 那些请求只能等 `ServeClient` 的 300s 超时。
   我没找到"reload 时正在等截图"的可复现路径，故只记在这里。

---

## 交付摘要

- **条数**：P0 **1**、P1 **3**、P2 **5**，共 **9** 条；另 6 条"已排除"、4 条"未坐实"。
- **最该先修的 3 条**：
  1. **A-1**（P0）——Editor 服务默认开启且对任意网页开放 `/menu` 与 `/console`，是本轮唯一"外部输入 → 任意操作"的通道。
  2. **A-3**（P1）——真机上"再玩一次就摸不到摇杆"，玩家可见且完全静默，桌面验收永远抓不到。
  3. **A-2**（P1）——与已修好的 B 层同型却漏了 A 层，补齐成本最低（照抄 8/64 两个常量 + 一条拒绝路径）。
- **可能影响发布**：**DevConsole 本体不构成发布风险**——门控实测不可绕、非开发包零 socket、对浏览器免疫（见"已排除"1/4）；
  唯一与发布有关的隐患是 **B-2**（`SO_REUSEADDR` 让"端口绑上"不再等于"独占"，发生在随包出厂的那段监听代码里），
  但它需要同机另一进程配合，且不泄露游戏数据。**A-1 与发布包无关**，它只影响开发者本机（也正因此更该立刻修，
  因为它把开发机上的 Editor 变成了可被任意网页驱动的执行器）。

---

## 复核清单（供复核代理）

- [ ] V1 每条缺陷都有 `文件:行号` + 代码片段，可定位
- [ ] V2 每条都说明了"为什么现有测试抓不到"（不是套话）
- [ ] V3 抽 3 条（含至少 1 条 P0）独立验证：真能复现或真能从代码推出
- [ ] V4 无注水条目（风格/命名/重构建议）
- [ ] V5 范围裁剪有说明；未改产品代码；未 commit

## 复核结论（复核代理写）

（待填）
