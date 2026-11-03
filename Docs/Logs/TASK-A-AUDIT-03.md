# TASK-A-AUDIT-03 —— 修审计余项 A-4 / B-1 / B-3 / B-5

> 四条来自 `TASK-A-AUDIT-01.md`。**先复现、再修、再证明**。施工代理写第 2 节报告。**禁止 git commit**。

## 任务简报（主控写，2026-11-03）

主控已筛过：**B-2（SO_REUSEADDR）、B-4（Stop 不停已接连接）不修**——理由见 HANDOFF"已知接受"，
**不要顺手改它们**。本任务只修下面 4 条。

---

### A-4（P1）`UIRoot` 重收养路径必然重复建 8 个 layer canvas

**缺陷**：`View/UI/UIRoot.cs` 幂等判据 `_layerCanvases.Count == UIConsts.LayerCount`（`:106`）只看**内存字典**，
而 `_layerCanvases` 是 `readonly` 非序列化字段（`:34-35`），**domain reload 后必然为空**；但 `[Layer:*]`
子对象作为场景对象**存活**。于是重收养路径（`:69-75`）里 `0 != 8` → 再建一整套。
`LayerCanvasCount` 仍报 8，掩盖实际 16 个。

**后果**：reload 前已 Push 的面板仍挂在**旧** canvas 上继续渲染和接收 raycast，而 `UIManager`（同样被重置）
只认新 canvas → **屏幕上两套 UI 叠着**，旧那套点得动但谁也管不了。

**为什么测试抓不到**：`EnsureCreated_IsIdempotent_AndAdoptsSingleRoot` 连调两次，第二次走 `_instance != null`
早返回（`:64-67`），**重收养分支一次都没进**；没有用例模拟"字典被清空、子对象还在"。

**修法要求**：幂等判据不能只信内存字典。至少满足其一（选最简单的，说明理由）：
- 重收养时**从真实子对象重建** `_layerCanvases`（扫 `transform` 下 `[Layer:*]`），再判断缺哪些；
- 或判据改为"字典数量正确 **且** 每个 layer 在 transform 下确实存在且挂在正确父级"。

**必须有 PlayMode 用例锁死**：模拟"`_layerCanvases` 为空 + 子 canvas 存活"→ 再次 `EnsureCreated` 后
**canvas 总数仍为 8**（不是 16）。用例改回旧逻辑必须变红。

---

### B-1（P2，主控上调为数据安全）`SaveSystem.SaveToSlot` 先移走 main 再落盘，且写入序列无 `try/catch`

**缺陷**：`Data/Save/SaveSystem.cs:176-201` —— 先把 `main` `File.Move` 到 `bak1`（main 此刻已不存在），
再 `WriteDurable(tmp, json)`（真正慢的 `Flush(flushToDisk:true)` 在 `:215`），最后 `tmp → main`。
与类文档（`:16-19`）承诺的"先写 tmp 再替换"**相反**。整段无 `try/catch`：
磁盘满 / 权限 / 杀软占用导致 `WriteDurable` 抛异常时，**main 已经没了**。

**为什么测试抓不到**：`SaveSystemTests` 34 条**没有任何写失败注入**——没有只读目录、没有 `IOException` 桩、
没有"tmp 写一半"用例；失败用例全在**读**路径。

**修法要求**：改成真正的"写 tmp（含落盘 flush）→ 成功后才滚动 main→bak1 → tmp→main"，
并加 `try/catch` 保证失败时**至少 main 或 bak1 之一可用**（不能出现三份全无）。
异常要么向上抛、要么返回明确失败结果——**不许静默吞掉**。

**必须有写失败注入用例**：让 `WriteDurable` 阶段失败（只读目录或桩），断言**旧 main 内容仍可读回**。
用例改回旧顺序必须变红。

---

### B-3（P2）`UIRoot.EnsureEventSystem` 认任何 EventSystem，不校验是否持久 → A-AND-04 修复会失效

**缺陷**：`EnsureEventSystem` 只要拿到某个 EventSystem 就认为完事，**不校验它是否持久**
（是否在 UIRoot 下 / 是否 `DontDestroyOnLoad` 层）。若当前场景**自带一个普通 EventSystem**（不持久），
则换场景时它照样被卸载 → A-AND-04 修的"SCN_Game 内 UGUI 全死"**原样复发**。

**修法要求**：找到的 EventSystem 必须校验持久性；不满足就**用我们自己的**（挂到 UIRoot 下），
而不是直接采信场景里碰到的那个。注意保留 A-AND-04/05 已建立的语义（EventSystem 是 UIRoot 的子物体）。

**必须有 PlayMode 用例**：场景里预置一个**非持久** EventSystem → 换场景后 `EventSystem.current` 仍非空
且是我们那份。用例改回旧逻辑必须变红。

---

### B-5（P2）DevConsole 命令超时后仍在主线程执行——副作用发生在"已告知失败"之后

**缺陷**：命令超时后服务端已向客户端返回失败/超时，但该命令**仍会在主线程被执行**，
副作用在"已经告诉调用方失败"之后发生。调用方据此重试会造成**重复执行**。

**修法要求**：超时即**放弃执行**（出队时检查是否已超时/已取消，是则直接丢弃并记日志），
或者改为"超时也一定执行完但返回成功"——**二选一，语义必须自洽**，不能一边报失败一边照做。

**必须有测试**：构造超时场景，断言副作用**没有发生**（或按你选的语义断言一致）。

---

### 通用要求

- 四条一起修，但**各自可独立验证**，报告分四节。
- 回归：EditMode **797** / PlayMode **38 → 41+**（三条新用例）/ 原生 **281 用例 771,162 断言**。
- **不要顺手改 B-2 / B-4**，也不要扩大战线。

### 环境纪律（真实踩过）

- Unity 批处理**单实例**：跑前查进程 + 删 `UnityProject/Temp/UnityLockfile`
- 跑测试**勿带 `-quit`**；退出僵死 → 轮询 `results.xml`，5 分钟有 xml 无退出即 `taskkill //PID //F`
- 构建前 `unset http_proxy https_proxy all_proxy`；**Bash 侧禁止转调 powershell** → 用 **PowerShell 工具**
- 本机 GUI Editor 加 `-force-d3d12` **会挂起**，别加
- 清理用 `mv` 归档到 `TempLogs/archive_pre_audit/`，不要 `rm -rf`
- 跑完查 `git status`：Editor 自动改写 `URP_*.asset`/`QualitySettings.asset` 非本意就 `git checkout` 还原

### 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- 不改 Governor 三件套；不动 Unity 安装目录；`.cs` 无 BOM、LF

---

## 施工报告（施工代理写）

- **施工代理**：TASK-A-AUDIT-03 施工（与验收非同一代理），2026-11-03。
- **改动范围**：`View/UI/UIRoot.cs`、`Data/Save/SaveSystem.cs`、`View/DevConsole/RuntimeDevConsoleServer.cs`、
  `View/DevConsole/RuntimeDevConsole.cs`、`Tests/Data/SaveSystemTests.cs` + 3 个新 PlayMode 用例文件。
  **未触碰 B-2（SO_REUSEADDR）/B-4（Stop 不停已接连接）**，未动其它模块。
- **证据目录**：`TempLogs/TASK-A-AUDIT-03/`（`editmode_*.xml|log`、`playmode_*.xml|log`、
  `mutant_playmode*.xml`、`mutant_editmode.xml`、`mutation_backup/*.fixed.cs`）。

### 2.1 A-4（P1）UIRoot 重收养重复建 8 个 layer canvas

#### 缺陷复现证据

1. **代码事实**（未改动前的 HEAD）：`BuildLayersIfMissing` 的判据是 `_layerCanvases.Count == UIConsts.LayerCount`
   （`UIRoot.cs:106`），而 `_layerCanvases` 是 `readonly` **非序列化**字段（`:34-35`）→ domain reload 后必为空；
   `[Layer:*]` 子对象是场景对象，reload 后照样在。于是重收养路径 `0 != 8` → 走 `Clear()` + 重建 → **16 个 host**，
   而 `LayerCanvasCount` 仍报 8（正是它把缺陷藏住的）。
2. **用例级复现**：新增用例在**把修复摘掉**（只保留字典判据）的变异版下必红，见 2.1 变异结果：
   `re-adoption built a second full set of layer canvases (16 hosts instead of 8)`，`Expected: 8 / But was: 16`。
   这同时证明"旧判据 + 存活子对象"这一状态组合确实会重复建。
3. **一处必须如实说明的过程**：本用例第一版**只清了静态 `_instance`、没清实例字典**，因此在变异版下也是绿的
   （`mutant_playmode.xml`：41 条里只有 B-3/B-5 两条红，A-4 绿）。这说明"半模拟"抓不到缺陷——真实 reload 丢的是
   **静态引用 + 非序列化实例字段两者**。已改为两者都清（并加前置断言
   `LayerCanvasCount == 0` 且 `[Layer:*] 子对象 == 8`），改后才对变异版必红（`mutant_playmode2.xml`）。

#### 修法

`BuildLayersIfMissing()` 先调用新增的 `AdoptExistingLayers()`：**以真实子物体为准重建字典**
（只认直接子级、名字形如 `[Layer:<UILayer>]` 且带 `Canvas` 的 host；重复名以第一个为准），
然后只补建**字典里确实缺失**的 layer（`ContainsKey` 跳过已有的），不再无条件 `Clear()` 重建。
选择"重建索引"而不是"判据同时校验子对象"的理由：重建索引同时修好了**第二类状态**（字典有 8 条但子对象被销毁/挪走），
且不需要在两处维护同一套"是否存在"的判断；代价是每次 `BuildLayersIfMissing` 多一次 O(childCount) 扫描，
而该函数只在 `EnsureCreated` 的非早返回路径（创建/重收养）里调用，不在每帧路径上。

#### 验证

- 用例：`ZFrame.PlayMode.Tests.UIRootReloadAdoptionPlayModeTests.ReAdoption_AfterStaticsAreLost_ReusesTheSurvivingLayerCanvases`
  （`Tests/PlayMode/UIRootReloadAdoptionPlayModeTests.cs`）。做法：取现存的 8 个 canvas →
  清空 `_instance` 与 `_layerCanvases`（反射，生产代码不留测试口子）→ `EnsureCreated()`。断言：
  ① 同一个 root 被重收养；② **host 总数仍 8**（不是 16）；③ 每个 layer 映射到的**就是 reload 前那个 Canvas 实例**
  （`AreSame`，即"旧的 UI 与 UIManager 认的是同一套"）；④ 父级仍是 root、sortingOrder 不变。
- 变异：把 `AdoptExistingLayers();` 去掉（恢复字典判据）→ 该用例红（16/8）；还原后绿。

### 2.2 B-1（数据安全）SaveSystem 先移走 main 再落盘

#### 缺陷复现证据

1. **代码事实**（HEAD `:176-201`）：`File.Move(main, bak1)` → `WriteDurable(tmp, json)` → `File.Move(tmp, main)`，
   整段无 `try/catch`。真正慢且易失败的 `Flush(flushToDisk:true)`（`:215`）发生在 main 已被移走之后。
2. **写失败注入（怎么做的，重点回答）**：产品代码里**没有加任何测试接缝**。做法是让 `.tmp` 路径上存在一个**同名目录**：
   `Directory.CreateDirectory(main + ".tmp")`。`WriteDurable` 用 `new FileStream(path, FileMode.Create, ...)` 打开该路径时
   必然抛异常（本机为 `UnauthorizedAccessException`），而 main / bak1 一个字都没动——这正是"写 tmp 阶段失败"。
   比只读目录更精准（只打中 tmp，不影响 main 的读取），且不依赖权限配置，可重复。
3. **用例级复现**：变异版（恢复 HEAD 顺序 + 去掉恢复逻辑）下红：
   `the previous main save disappeared after a failed write ... Expected: True / But was: False`（`mutant_editmode.xml`）。

#### 修法

`SaveToSlot` 改为：`WriteDurable(tmp, json)`（含 flush）**成功之后**才滚动 `bak2←bak1`、`bak1←main`，最后 `tmp→main`；
整段包在 `try/catch` 里，`catch` 先 `RecoverAfterFailedWrite(main, bak1, tmp)` 再 **`throw;`**（不静默吞，调用方
`GameBootstrap` 照旧打失败日志）。恢复逻辑只做两件 best-effort 的事：① 若 main 已不在而 bak1 在（失败落在最后一次 rename），
把 bak1 **复制**回 main（bak1 保留），保证"当前存档"路径仍可读；② 删掉半截 `.tmp`。恢复自身的异常只吞在自己的
cleanup 范围内（注释写明），原始异常永远向上抛。

#### 验证

- 用例：`ZFrame.Data.Tests.SaveSystemTests.SaveToSlot_TmpWriteFails_KeepsThePreviousMainReadable`。
  断言链：写失败**必须**有异常逃出（`Assert.Catch<Exception>`，且类型是 IO/UnauthorizedAccess）→ `File.Exists(main)` 为真
  → "至少 main 或 bak1 之一可用" → main 内容仍是**上一代**（health=7，失败那次携带的是 99）→ `LoadFromSlot` 仍能载回 7。
- 变异：恢复 HEAD 顺序并去掉恢复逻辑 → 用例红（main 不存在）；还原后绿。

### 2.3 B-3（P2）EnsureEventSystem 认任何 EventSystem

#### 缺陷复现证据

1. **代码事实**（HEAD `:144`）：`if (Object.FindObjectOfType<EventSystem>() != null) return;` —— 场景自带的也算数；
   只有**自建**的那份才会 `SetParent(root)`（`:157-160`）。而 `EnsureCreated` 在 `_instance != null` 时早返回（`:64-67`），
   换场景后不会补建 → 非持久 ES 随场景销毁 → UGUI 全死（A-AND-04/fcc59ad 原样复发）。
2. **用例级复现**：变异版（恢复"碰到任何 ES 就返回"）下红：
   `the live EventSystem ('AUDIT03_SceneAuthoredEventSystem') must be OUR persistent one under the UIRoot, not the scene's copy`，
   `Expected: True / But was: False`（`mutant_playmode2.xml`）。

#### 修法

`EnsureEventSystem` 先取 `FindObjectOfType<EventSystem>()`，只有**持久性成立**才采信：本框架对"持久"的定义就是
A-AND-04 的语义——**它是 UIRoot 的子物体**（root 是 DontDestroyOnLoad 对象，子物体继承之），实现为 `IsPersistentFor()`。
不满足时**不销毁**场景作者的对象（那是场景数据），而是 `enabled = false` 把它关掉（否则两个 EventSystem 会抢
`EventSystem.current`、每次指针派发两遍，Unity 也会告警），并 `ZLog.Warn("UI", ...)` 记一条；然后照旧在 root 下建
`[UIRoot.EventSystem]`（`EventSystem` + `StandaloneInputModule`）。A-AND-04/05 的"EventSystem 是 UIRoot 子物体"语义不变。

#### 验证

- 用例：`ZFrame.PlayMode.Tests.UIRootForeignEventSystemPlayModeTests.SceneAuthoredNonPersistentEventSystem_IsNotAdopted_OursSurvivesTheSceneLoad`。
  做法：删掉我们的 ES → 场景根上放一个**非持久** ES（`EventSystem` + `StandaloneInputModule`）→ 清静态引用
  （模拟 reload）→ `EnsureCreated()`。断言：`EventSystem.current` 非空、**是我们那份**（在 root 下）、root 重新拥有 ES、
  场景那份被关掉；随后真实 `LoadSceneAsync(SCN_Empty, Single)`（并用"场景 marker 必须被销毁"证明真的发生了单模式卸载）后，
  `EventSystem.current` 仍非空、仍是 root 的子物体、`currentInputModule` 仍在。
- 变异：恢复"任何 ES 都算数"→ 红（`current` 指向场景那份）；还原后绿。

### 2.4 B-5（P2）DevConsole 超时后仍在主线程执行

#### 缺陷复现证据

1. **代码事实**（HEAD）：`_queue.Enqueue(command)` → `command.Wait(CommandTimeoutMs)` 失败 → 回 `timeout` → `return`；
   但命令还躺在队列里，下一次 `Pump` 照常 `Execute` + `Complete`。即"已告知失败，随后照做"。
2. **用例级复现**：变异版（泵里去掉超时/放弃判据）下红：
   `the timed-out command was executed anyway: the caller was told 'failed' but the side effect happened`，
   `Expected: 1 / But was: 0`（`QualitySettings.GetQualityLevel()` 被真的改掉了，`mutant_playmode2.xml`）。

#### 修法（二选一，这里选"超时即放弃执行"）

理由：另一条语义（"超时也一定执行完并返回成功"）无法撤销**已经发给调用方**的失败结果，必须让调用方知道它其实成功了，
反而要求客户端配合；而"放弃"对调用方是最保守、最不易误解的契约：**失败 = 没发生**，重试安全。

- `RuntimeDevCommand` 增加 `_deadlineMs`（入队时按注入的超时算出）、`IsExpired`、`TryBeginExecute()`、
  `TryAbandon()`（`Interlocked.CompareExchange` 三态：Pending/Executing/Abandoned）。
- socket 侧 `Wait` 失败即 `TryAbandon()`，然后再回 timeout → 该命令此后**永远不会**被执行。
- `Pump` 出队后先判 `IsExpired || !TryBeginExecute()`：命中就 `Complete(...)`（回 `ok:false / not executed`，
  不会有人再读到）+ `LogOnce("dropped: expired/abandoned before execution")`，**不执行**，也不计入 `CommandsServed`。
- `Pump` 拆成 `Pump()`（泵 `_server`）与 `Pump(RuntimeDevConsoleServer)`；生产路径完全不变。
- 超时值做成 `RuntimeDevConsoleServer(int port, int commandTimeoutMs)` 的 internal 重载（生产只有默认 10s），
  用例才可以 60ms 内构造这个前提。

#### 验证

- 用例：`ZFrame.PlayMode.Tests.RuntimeDevConsoleTimeoutPlayModeTests.TimedOutCommand_IsDiscarded_AndItsSideEffectNeverHappens`。
  做法：起一个**独立**、注入 60ms 超时的 server → 后台线程发 `quality <非当前档>` → 主线程**不泵**
  （这正是"主线程忙"的生产前提，客户端拿到 `ok:false / command timeout`）→ 再 `Pump(server)`。
  断言：队列已清空、`QualitySettings.GetQualityLevel()` **等于原值**（副作用没发生）。
- 变异：泵里去掉 `IsExpired/TryBeginExecute` 判据 → 红（档位被改）；还原后绿。

### 2.5 回归数字

| 套件 | 结果 | 证据 |
|---|---|---|
| EditMode | **798 / 798**（0 failed；797 基线 + 1 条 B-1 写失败注入用例） | `TempLogs/TASK-A-AUDIT-03/editmode_final.xml` |
| PlayMode | **41 / 41**（0 failed；38 基线 + 本任务 3 条） | `TempLogs/TASK-A-AUDIT-03/playmode_final.xml` |
| 原生 | **281 用例 / 771,162 断言**，0 failed | 直接跑既有 `Native/build/bin/Release/zrender_test.exe`（见 2.6-2） |

三条新用例在最终运行里逐条 `result="Passed"`（`playmode_final.xml`）。

### 2.6 未验证项（如实披露）

1. **A-4 的"真 domain reload"未实测**：批处理下无法触发真实 Assembly Reload，用例是**状态等价模拟**
   （静态引用 + 非序列化字典都清空、子对象保留）。"Unity reload 后会重跑 `RuntimeInitializeOnLoadMethod`"仍只是引擎文档结论。
2. **原生侧未重新构建**：本任务无任何原生源码改动；`Native` 下没有比该二进制更新的 `.cpp/.h/CMakeLists.txt`。
   尝试用 PowerShell 工具跑 `Tools/build_native.ps1` 时被沙箱拒绝（`Error creating directory ... Native\build\cmake_probe_tmp:
   Permission denied`），而纪律禁止 Bash 侧转调 powershell，故改为直接执行既有 Release 二进制（结果与基线逐字一致）。
   验收如需重建，请在非沙箱 PowerShell 里跑一次。
3. **B-1 的写失败是"目录占位"注入**，不是真的磁盘满/杀软占用；语义等价（都是 `FileStream` 打开 `.tmp` 失败），
   但真实故障下的"半截 tmp"形态没有复现。
4. **B-3 的持久性判据 = "是否挂在 UIRoot 下"**。若未来有**别的系统**创建的 DontDestroyOnLoad EventSystem，
   本实现会把它关掉（刻意的单一所有权语义）——该场景未测，也未在任务书范围内。
5. **B-5 的生产 10s 超时未真等**：用例注入 60ms，走的代码路径（deadline/abandon/Pump 判据）与 10s 完全相同。
6. **真机（Android）未跑**：无设备；A-4/B-3 的"屏幕上两套 UI"/"UGUI 全死"两条用户可见症状未在设备上复现，
   结论停在机制 + 帧精确用例。

### 2.7 现场

```
 M UnityProject/Assets/ZFrame/Data/Save/SaveSystem.cs
 M UnityProject/Assets/ZFrame/Tests/Data/SaveSystemTests.cs
 M UnityProject/Assets/ZFrame/View/DevConsole/RuntimeDevConsole.cs
 M UnityProject/Assets/ZFrame/View/DevConsole/RuntimeDevConsoleServer.cs
 M UnityProject/Assets/ZFrame/View/UI/UIRoot.cs
?? UnityProject/Assets/ZFrame/Tests/PlayMode/RuntimeDevConsoleTimeoutPlayModeTests.cs(+.meta)
?? UnityProject/Assets/ZFrame/Tests/PlayMode/UIRootForeignEventSystemPlayModeTests.cs(+.meta)
?? UnityProject/Assets/ZFrame/Tests/PlayMode/UIRootReloadAdoptionPlayModeTests.cs(+.meta)
```

无 `URP_*.asset` / `QualitySettings.asset` 等 Editor 自动改写；无 `Unity.exe` 残留、无 `UnityProject/Temp/UnityLockfile`；
未 `git commit`、未 `git add`；未改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md` 与 Governor 三件套；
`.cs` 均为 UTF-8 无 BOM（工作区 CRLF 是 `core.autocrlf=true` 的检出形态，入库按 `.gitattributes` 归一为 LF）。
变异备份与被替换的临时日志放在 `TempLogs/TASK-A-AUDIT-03/`（含 `mutation_backup/`），未删除任何既有证据。


---

## 验收清单（供验收代理）

- [ ] V1 A-4：PlayMode 用例覆盖"字典空+子对象在"，canvas 总数仍 8；**改回旧逻辑变红**
- [ ] V2 B-1：写失败注入后旧 main 仍可读回；**改回旧顺序变红**；无静默吞异常
- [ ] V3 B-3：场景预置非持久 EventSystem → 换场景后仍存活且是我们那份；**改回旧逻辑变红**
- [ ] V4 B-5：超时后副作用不发生（或语义自洽且被断言）；改回旧逻辑变红
- [ ] V5 回归：EditMode 797 / PlayMode 38→41+ / 原生 281/771,162
- [ ] V6 未动 B-2/B-4；未 commit；现场干净

## 验收结论（验收代理写）

- **验收代理**：TASK-A-AUDIT-03 验收（与施工非同一代理），2026-11-03。全部结论由本次独立运行取得，
  证据在本目录 `TempLogs/TASK-A-AUDIT-03/audit/`（`md5_before.txt`、`mut1..mut4_*.xml/.log`、
  `editmode_audit.xml`、`playmode_audit.xml`、`native_audit.txt`、`mutation_backup_audit/SaveSystem.fixed.cs`）。
  第 2 节施工报告只作线索，**未作为证据采信**。

### 逐条

| 项 | 判定 | 证据（本次自跑） |
|---|---|---|
| V1 A-4 | **PASS** | 变异（去掉 `AdoptExistingLayers()`，恢复字典判据）→ `mut1_v1_playmode.xml`：`Expected: 8 / But was: 16`，`result="Failed"`；还原后该用例在 `playmode_audit.xml` 中 `result="Passed"` |
| V2 B-1 | **PASS** | 变异（整文件回退到 `HEAD` 版 SaveSystem = 旧顺序 + 无恢复）→ `mut2_v2_editmode.xml`：`the previous main save disappeared... Expected: True / But was: False`；还原后 `SaveToSlot_TmpWriteFails_KeepsThePreviousMainReadable` `result="Passed"`（`editmode_audit.xml`） |
| V3 B-3 | **PASS** | 变异（恢复 `if (FindObjectOfType<EventSystem>() != null) return;`）→ `mut3_v3_playmode.xml`：`the live EventSystem ('AUDIT03_SceneAuthoredEventSystem') must be OUR persistent one... Expected: True / But was: False`；还原后 `result="Passed"` |
| V4 B-5 | **PASS** | 变异（`Pump` 里删掉 `IsExpired \|\| !TryBeginExecute()` 判据）→ `mut4_v4_playmode.xml`：`timed-out command was executed anyway... Expected: 1 / But was: 0`（档位被改）；还原后 `result="Passed"` |
| V5 回归 | **PASS with 受限** | EditMode **798/798**（0 failed，`editmode_audit.xml`）；PlayMode **41/41**（0 failed，三条新用例逐条 `Passed`）；原生 **281 用例 / 771,162 断言 / 0 failed**（`native_audit.txt`，exit 0）。**原生未重建**：`Native/build/bin/Release/zrender_test.exe` 时间戳 2026-11-01 02:47，`git status --porcelain Native/` 为空，且 `find Native -path Native/build -prune -o -type f -newer <该 exe>` 无输出（无源码比二进制新）——跑的是现有二进制，属"未重建"的受限通过 |
| V6 现场 | **PASS** | `git status` 仅施工自述的 5 个 `.cs` 改动 + 本日志 + 3 个新用例（含 .meta），无 `URP_*.asset`/`QualitySettings.asset`/`.unity`/`.prefab` 改动；`git log -1` 仍为 `b6deba7`（**未 commit**）；无 `Unity.exe` 进程、无 `UnityProject/Temp/UnityLockfile`；B-2 的 `ReuseAddress`（`RuntimeDevConsoleServer.cs:149`）与 `Stop()`（diff 中无该 hunk）**未动**，B-4 未动 |

### 四个必答问题

1. **A-4 用例是否真有牙 → 是。** `UIRootReloadAdoptionPlayModeTests.cs:94-108` 的 `SimulateDomainReload`
   同时清 `_instance`（静态）**和** `_layerCanvases`（实例、非序列化的那份），子对象保留；并在触发前断言
   `LayerCanvasCount == 0` 且 `[Layer:*] 子对象 == 8`（`:55-58`），即先证明"字典空 + 子对象在"这一状态确已构造。
   牙口在 `CountLayerHosts`（`:67`，量层级而非 `LayerCanvasCount`）与 `AreSame(canvasesBefore[layer], ...)`
   （`:78`）。本次变异实测 16/8，且前置断言未先失败——说明红的是判据本身，不是前置条件。
   施工自述的"第一版只清静态、变异下仍绿"这一历史，在当前版本已不复现（现版变异确红）。
2. **B-1 是否真不吞异常 → 是。** `SaveSystem.SaveToSlot` 顺序为
   `WriteDurable(tmp)`（含 `Flush(flushToDisk:true)`，`:265-272`）→ `bak2←bak1`、`bak1←main` → `tmp→main`；
   `catch (Exception)` 只调 `RecoverAfterFailedWrite`（其自身仅 best-effort 清理并只吞自己的 IO/权限异常）后
   **`throw;`** 重抛。用例 `Assert.Catch<Exception>` 真抓到类型为 IO/UnauthorizedAccess 的异常（绿运行通过该断言链）。
   写失败注入**未给产品代码加接缝**：`grep ForTests|Inject|Seam|Hook|#if|Delegate` 于 `SaveSystem.cs` 无命中
   （唯一命中是类文档里既有的"构造注入存档根目录"），注入方式是 `Directory.CreateDirectory(main + ".tmp")`
   让 `FileStream(FileMode.Create)` 在 tmp 路径失败。
3. **EditMode / PlayMode / 原生数字**：798 / 41 / 281 用例 771,162 断言，全部 0 failed。
4. **现场**：变异全部还原，`md5sum -c TempLogs/TASK-A-AUDIT-03/audit/md5_before.txt` **20/20 OK**，
   产品代码 diff 恢复为验收前的 `274 insertions / 20 deletions`（5 个文件，与验收前逐字一致）；未 commit。

### 遗留（不影响 PASS，如实登记）

- **A-4 仍是状态等价模拟**：批处理下无真实 Assembly Reload，"reload 后会重跑 `RuntimeInitializeOnLoadMethod`"
  仍为引擎文档结论（施工 2.6-1 已披露，本次未推翻）。
- **B-5 语义**："超时即放弃"自洽（失败=没发生），但**已开始执行**的命令无法撤销：若 `Pump` 已越过
  `TryBeginExecute` 而客户端此刻超时，`TryAbandon` 会失败、命令照常执行完——这是任何队列的固有竞态，
  缺陷本身（"已告知失败、随后在队列里照做"）已被判据堵死，未在本次任务范围内额外处理。
- **原生未重建**（见 V5）；B-1 的失败注入是"目录占位"而非真磁盘满/杀软占用（语义同为 `FileStream` 打开 tmp 失败）。

### 最终裁决

**PASS**（V5 为 PASS with 受限——受限点仅为"原生未重建"，已用 git/时间戳证明源码未变，不影响交付）。
四条缺陷均"先复现（变异必红）→ 修复后必绿"，用例有牙，异常不被吞，无测试接缝污染产品代码，回归数字达标，现场干净。

