# TASK-A-AND-05 —— 补 EventSystem 跨场景存活回归用例

> 小任务：只加测试，**不改产品代码**。施工代理按本简报执行，第 2 节写报告。**禁止 git commit**。

## 任务简报（主控写，2026-11-01）

### 背景

A-AND-04 顺带修了一个阻断级缺陷（`fcc59ad`）：`UIRoot.EnsureCreated()` 原先只给 host 加
`DontDestroyOnLoad`，而 EventSystem 是**同级 root 对象** → 单模式加载 SCN_Game 时随 SCN_Boot 被卸载，
且 `_instance != null` 早退不再补建 → **SCN_Game 内所有 UGUI 派发全死**（摇杆/按钮/暂停面板全废）。

修法：把 EventSystem 挂到 UIRoot 下（`host.transform.SetParent(root, false)`），继承持久化。
**验收明确指出：这次修复没有回归用例锁死**——现有 797 条用例里没有一条断言"换场景后 EventSystem 仍存活"，
属于"这次没人写"而非"不可能被写"。本任务补上这条缺口。

### 目标

加一条 **PlayMode** 用例，语义是：**UIRoot 已创建并加载过另一个场景后，`EventSystem.current` 仍非空。**
要点：

1. 必须**真的经过一次场景加载**（`SceneManager.LoadScene`，单模式），而不是只 `Destroy` 一个对象——
   缺陷的本质就是"场景卸载把 EventSystem 带走"。
2. 断言 `EventSystem.current != null`，且该组件**挂在 `UIRoot` 之下**（`transform.IsChildOf(uiRoot.transform)`）——
   后者锁死"修法本身"（挂到 root 下继承持久化），而不只是"恰好还在"。
3. 再补一条**能反映症状**的断言：卸载后 UGUI 派发仍可工作（至少 `EventSystem.current.IsActive()` 为真，
   更好是构造一次 pointer 事件被接收）。**做不到就如实说明，不要造假。**

### 验收标准（验收代理会查）

- **用例必须有牙**：把 A-AND-04 的修复改回旧值（去掉 `SetParent(root, false)` 那句，让 EventSystem 变回 root 对象）
  → 新用例**必须红**；还原后复绿。**这是本任务的核心判据**。
- 不回归：EditMode **797**、PlayMode **20 → 21**（新增 1 条）、原生 281/771,162 不受影响。
- 只加测试，不改产品代码（`git status` 不得出现 `UIRoot.cs` 等产品文件）。

### 环境纪律（真实踩过）

- Unity 批处理**单实例**：跑前查进程 + 删 `UnityProject/Temp/UnityLockfile`
- 跑测试**勿带 `-quit`**；退出可能僵死 → 轮询 `results.xml`，5 分钟有 xml 无退出即 `taskkill //PID //F`
- 证据以 `results.xml` 为准
- 构建前 `unset http_proxy https_proxy all_proxy`；**Bash 侧禁止转调 powershell** → 用 **PowerShell 工具**
- 清理用 `mv` 归档到 `TempLogs/archive_pre_audit/`，不要 `rm -rf`
- 跑完查 `git status`：Editor 自动改写 `URP_*.asset`/`QualitySettings.asset` 非本意就 `git checkout` 还原

### 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- **不改产品代码**（`UIRoot.cs` 等一律不动）；`.cs` 无 BOM、LF

---

## 施工报告（施工代理写）

> 施工日期 2026-11-01。只加了一条 PlayMode 用例；**产品代码零改动**（变异后已还原，md5 与 git diff 双证）。
> **未 git commit**（施工代理铁律）。所有数字来自本轮自己跑的 `results.xml`。

### 2.0 结论速览

| 项 | 结果 |
|---|---|
| 新增用例 | `CrossSceneLoad_EventSystemStaysUnderUIRoot_AndStillDispatchesPointer`（1 条） |
| 所在文件 | `UnityProject/Assets/ZFrame/Tests/PlayMode/UIRootCrossSceneEventSystemPlayModeTests.cs`（新增，含 `.meta` guid `65363f14…`） |
| 触发方式 | 真的走 `SceneManager.LoadSceneAsync("SCN_Empty", LoadSceneMode.Single)`（不是 Destroy 对象） |
| 断言 | ①`EventSystem.current != null`；②实例是换场景**前的那一个**（`AreSame`，不是重建）；③`transform.IsChildOf(uiRoot.transform)`（锁死修法）；④`IsActive()` / `currentInputModule != null`；⑤`RaycastAll` 命中 UIRoot 下的探针并收到 down/up/click |
| **变异验证** | 删掉 `host.transform.SetParent(root, false)` → 该用例**红**，失败点正是"换场景后 `EventSystem.current` 为 null"；还原后复绿 |
| PlayMode | **21 / 21 passed**（基线 20 + 新增 1），`TempLogs/and05/playmode_b.xml` |
| EditMode | **797 / 797 passed**（不变），`TempLogs/and05/editmode.xml` |
| 原生 | **281 cases / 771,162 assertions，0 failed**（未改原生，直接跑既有 Release 二进制） |
| 产品代码 | **零改动**：`UIRoot.cs` md5 `cc6f4c19…` 变异前后一致，`git diff` 为空 |
| 现场 | `git status` 只有 2 个新增未跟踪文件；无 `URP_*.asset` / `QualitySettings.asset` 改写 |

### 2.1 用例做了什么

文件：`Assets/ZFrame/Tests/PlayMode/UIRootCrossSceneEventSystemPlayModeTests.cs`（`ZFrame.PlayMode.Tests` 程序集，
无 BOM、LF；用例类里内含一个 `PointerProbe` 计数组件与一个铺满 Popup 层的透明 `Image` 探针）。

执行序列：

1. `UIRoot.EnsureCreated()` 拿到活的 root（PlayMode 下 `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` 已先行自举）。
   前置断言只要求"加载前存在一个 EventSystem"——**这条在修复前后都成立**（修复前它是个根对象，只是会被场景带走），
   故意不在这里断言父子关系，否则用例会在换场景之前就因"实现形态"失败，牙咬不到真正的缺陷。
2. 在 UIRoot 的 Popup 层画布下建一个铺满的透明 `Image` 探针（`raycastTarget = true`），并另建一个普通场景对象
   `AAND05_SceneMarker` 作"场景确实被单模式卸载"的证据。
3. **缺陷触发点**：`SceneManager.LoadSceneAsync("SCN_Empty", LoadSceneMode.Single)`，等 `activeScene == SCN_Empty`，再跨一帧
   让被卸载场景对象的 `Destroy` 落地。
4. 断言：
   - `AAND05_SceneMarker == null`（证明真的发生了单模式卸载，不是空转）；
   - `uiRoot != null`（host 因 `DontDestroyOnLoad` 存活）；
   - **`EventSystem.current != null`** —— 症状本体；
   - **`AreSame(加载前实例, 当前实例)`** —— 是"活下来"而不是"事后重建"（重建意味着中间有若干帧派发全死）；
   - **`eventSystem.transform.IsChildOf(uiRoot.transform)`** —— 锁死修法本身（挂到 root 下才继承持久化）；
   - `isActiveAndEnabled` / `IsActive()` / `currentInputModule != null` —— 派发链路的可用性；
   - **指针事件被接收**：`eventSystem.RaycastAll(pointer, hits)` 命中探针 → `ExecuteEvents.Execute` 依次投递
     `pointerDown/Up/Click` → 探针计数各为 1。

`SCN_Empty` 的选取理由：在 Build Settings 里（index 1），既不是 SCN_Boot 也不是 SCN_Game，加载不会触发任何自举/存档钩子，
`GameBootstrap.OnSceneLoaded` 对非 Boot/Game 场景直接 `return`，因此本用例不会污染别的用例。

### 2.2 指针派发的诚实边界（没做到的部分）

批处理里没有可驱动的 OS 指针设备，`StandaloneInputModule.Process()` 只能读 `Input.mousePosition`，喂不进合成点击。
所以本用例做的是**模块命中之后的那一段**：`EventSystem.RaycastAll`（走 UIRoot 层画布的 `GraphicRaycaster`）+
`ExecuteEvents` 派发到命中的 `IPointerDownHandler`。这恰好是缺陷杀死的环节（EventSystem 没了 → 命中与派发都没了），
**但不是"模拟一次真鼠标点击"**，也没有覆盖 `StandaloneInputModule` 自身的输入读取。此点如实记录，不当作已覆盖。

### 2.3 变异验证（核心判据，我自己动手做的）

**改了什么**：在 `UIRoot.EnsureEventSystem()` 里把 A-AND-04 的修复整段删掉（等价于回到 `fcc59ad` 之前）：

```diff
-            if (root != null)
-            {
-                host.transform.SetParent(root, false);
-            }
+            // MUTATION (TASK-A-AND-05): SetParent removed on purpose — the EventSystem
+            // goes back to being a root object, exactly like the pre-fcc59ad code.
```

**第一轮（红，但判为"牙不够"）**：`-testFilter ZFrame.PlayMode.Tests.UIRootCrossSceneEventSystemPlayModeTests`
→ `TempLogs/and05/mutation_red.xml`：`total=1 passed=0 failed=1`，失败消息是
`precondition: A-AND-04 parents the EventSystem under the UIRoot`。
问题：它**在换场景之前**就因为实现形态挂了，等于只锁了"代码长什么样"，没锁"换场景之后会怎样"。这是假牙的表亲，不交付。

**第二轮（红在正确的位置，采信）**：把该前置断言降级为"只断言加载前有 EventSystem"，在**仍处变异态**下重跑
→ `TempLogs/and05/mutation_red2.xml`：`total=1 passed=0 failed=1`，失败消息：

```
EventSystem.current is null after a single-mode scene load — UGUI dispatch is dead in the new scene
(pre-A-AND-04 behaviour, fcc59ad)
  Expected: not null / But was: null
  ... UIRootCrossSceneEventSystemPlayModeTests.cs:104
```

失败点落在第 104 行（换场景后的第一条断言），且它前面的 `_sceneMarker == null`（第 97 行）已经通过——说明场景确实被卸载了，
红的正是缺陷本身。**红 1 条**（本轮只有这 1 条用例在过滤范围里）。

**怎么还原的**：变异前先 `cp UIRoot.cs → TempLogs/and05/UIRoot.cs.bak`，还原用 `cp` 覆盖回去（不靠手工撤销，避免残留空格/换行差异），
然后核对：

- `md5sum` 变异前后 **`cc6f4c198199d3aff16b8d954c25fe12`** 完全一致（备份文件同值）；
- `git diff -- UnityProject/Assets/ZFrame/View/UI/UIRoot.cs` → **空**；
- `git status --short` → 只有 2 个新用例文件（`.cs` + `.meta`），无任何产品文件。

**还原后复绿**：全量 PlayMode `TempLogs/and05/playmode_b.xml` → `testcasecount=21 total=21 passed=21 failed=0`。

### 2.4 测试家底（本轮实测）

| 套件 | 数字 | 证据 |
|---|---|---|
| PlayMode | **21 / 21 passed，0 failed**（20 → 21，只增 1） | `TempLogs/and05/playmode_b.xml`（`testcasecount="21" total="21" passed="21" failed="0"`） |
| EditMode | **797 / 797 passed，0 failed**（不变） | `TempLogs/and05/editmode.xml`（`testcasecount="797" total="797" passed="797" failed="0"`） |
| 原生 | **281 cases / 771,162 assertions，0 failed** | 直接跑 `Native/build/bin/Release/zrender_test.exe` |

单条过滤跑的留档：`single_case_pass_pre_refine.xml`（变异前的初版用例 1/1 绿，用于确认用例本身可跑通——
之后删掉了"加载前就断言父子关系"的那条前置断言，定稿版的绿由全量 `playmode_b.xml` 里的同名用例给出）、
`mutation_red.xml`（第一轮变异，红在错误位置）、`mutation_red2.xml`（第二轮变异，红在正确位置）。
环境失败的 8 次试探日志归档在 `TempLogs/and05/env_probe_failures/`（未删）。

### 2.5 环境踩坑（本轮新增，供后续复用）

1. **PowerShell 用 `&` 起 `Unity.exe` 不会等它**——`Unity.exe` 是 GUI 子系统程序，`&` 立刻返回（`$LASTEXITCODE` 为空），
   外层 shell 一结束，编辑器进程被连带回收，日志断在许可握手处（表现像"Unity 启动即死"）。**必须用
   `Start-Process -FilePath Unity.exe -ArgumentList ... -Wait -PassThru` 再读 `$p.ExitCode`**。这一坑让我白跑 8 轮
   （`probe1..probe8.log`）。
2. 许可阶段 `Access token is unavailable; failed to update` 后偶发长时间无输出，属正常重试，不必处理；但**代理变量会干扰**
   （`http_proxy=127.0.0.1:51167` 对 `api.unity.com` 返回 403），跑 Unity 时不要带代理变量（只对原生构建另说）。
3. 有一次 `DisplayProgressbar: Compiling Scripts` 卡死 10 分钟（bee_backend 空转）：`Stop-Process` 清掉
   `Unity/bee_backend/UnityPackageManager` + 删 `Temp/UnityLockfile` 后重跑即正常。本轮 `playmode.log`（卡死那份）作废，
   证据取 `playmode_b.xml`。
4. `TestReport.txt` 未生成/未入库；临时 xml/log 全在 `TempLogs/and05/`（历史留档，未 `rm`）。

### 2.6 给验收代理的提示

- V3 请自己动手做一遍变异：删 `UIRoot.EnsureEventSystem()` 里的 `if (root != null) { host.SetParent(root, false); }`，
  跑 `-testFilter ZFrame.PlayMode.Tests.UIRootCrossSceneEventSystemPlayModeTests`，应看到**红在 `EventSystem.current is null` 那条**
  （不是红在别的断言上）；还原后 `md5sum UnityProject/Assets/ZFrame/View/UI/UIRoot.cs` 应为 `cc6f4c198199d3aff16b8d954c25fe12`。
- 该用例会把活跃场景留在 `SCN_Empty`（全量 PlayMode 21 条已复跑证明不影响其它用例）。


---

## 验收清单（供验收代理）

- [ ] V1 用例真的经过一次单模式场景加载（不是只 Destroy 对象）
- [ ] V2 断言包含"EventSystem 挂在 UIRoot 之下"（锁死修法本身）
- [ ] V3 **有牙**：去掉 `SetParent(root, false)` → 新用例红；还原 → 绿（自己动手做，不采信自述）
- [ ] V4 PlayMode 20 → 21，EditMode 797 不回归，原生不受影响
- [ ] V5 只加测试，产品代码零改动（`git status` 佐证）

## 验收结论（验收代理写）

> 验收日期 2026-11-01，验收代理独立取证，未采信第 2 节任何自述数字。
> 全部证据为本轮自跑：`TempLogs/archive_pre_audit/and05_audit/`（`playmode_baseline.xml/.log`、`playmode_mutation.xml/.log`、
> `playmode_restored.xml`、`editmode.xml/.log`、`UIRoot.cs.bak`）。产品代码变异后已还原（md5 + `git diff` 双证）。

### 逐条裁决

**V1 —— 用例真的经过一次单模式 `SceneManager.LoadScene`：PASS**
证据：`UIRootCrossSceneEventSystemPlayModeTests.cs:91` 为 `yield return SceneManager.LoadSceneAsync(OtherSceneName, LoadSceneMode.Single)`（`OtherSceneName="SCN_Empty"`，Build Settings index 1，已确认非 Boot/Game）；实测侧 `playmode_mutation.log:522` 只有一行 `Loaded scene 'Assets/Scenes/SCN_Empty.unity'`（该轮只跑本用例），且同轮 `:97` 的 `_sceneMarker == null` 断言通过（失败点在其后的 `:104`）→ 旧场景对象确被单模式卸载，非空转。

**V2 —— 断言含 `EventSystem.current != null` 且 `transform.IsChildOf(uiRoot.transform)`：PASS**
证据：`:104` `Assert.IsNotNull(eventSystem, "EventSystem.current is null after a single-mode scene load…")`；`:112` `Assert.IsTrue(eventSystem.transform.IsChildOf(uiRoot.transform), …)`——锁死的是修法本身，而非"恰好还在"；另 `:108` `AreSame(加载前实例, 当前实例)` 额外排除"销毁后重建"，`:117-120` 覆盖 `isActiveAndEnabled / IsActive / currentInputModule`。

**V3 —— 有牙（验收代理亲自变异）：PASS**
证据：本人删掉 `UIRoot.EnsureEventSystem()` 里 `if (root != null) { host.transform.SetParent(root, false); }` 整段 → `UIRoot.cs` md5 `cc6f4c19…` → `1149c251a979bacc0f7a7c9d20e93d2c`（`git diff` 显示该 hunk 被替换）→ 跑 `-testFilter ZFrame.PlayMode.Tests.UIRootCrossSceneEventSystemPlayModeTests` → `playmode_mutation.xml`：`testcasecount="1" total="1" passed="0" failed="1"`，**红 1 条**，用例名 `CrossSceneLoad_EventSystemStaysUnderUIRoot_AndStillDispatchesPointer`，失败消息 `EventSystem.current is null after a single-mode scene load — UGUI dispatch is dead in the new scene (pre-A-AND-04 behaviour, fcc59ad) Expected: not null / But was: null`，栈定位 `UIRootCrossSceneEventSystemPlayModeTests.cs:104`；该轮日志 `error CS` 计数 0（红是断言，不是编译失败）。还原：`cp` 备份覆盖后 md5 回 `cc6f4c198199d3aff16b8d954c25fe12`，`git diff -- UnityProject/Assets/ZFrame/View/UI/UIRoot.cs` 为空，再跑同一 filter → `playmode_restored.xml` `total=1 passed=1 failed=0`（复绿）。**红在正确位置，不是"换场景前就因实现形态挂"的假牙。**

**V4 —— 家底不回归：PASS**
证据：PlayMode `playmode_baseline.xml`：`testcasecount="21" total="21" passed="21" failed="0"`，含新用例（`id=1029`，完整类名 `ZFrame.PlayMode.Tests.UIRootCrossSceneEventSystemPlayModeTests`），时长 0.41s；EditMode `editmode.xml`：`testcasecount="797" total="797" passed="797" failed="0"`；原生 `Native/build/bin/Release/zrender_test.exe`：`[doctest] test cases: 281 | 281 passed | 0 failed`、`assertions: 771162 | 771162 passed | 0 failed`。

**V5 —— 只加测试，产品代码零改动：PASS**
证据：跑完全部套件后 `git status --short` 仅三行——`M Docs/Logs/TASK-A-AND-05.md`（本文件的验收结论）、`?? …UIRootCrossSceneEventSystemPlayModeTests.cs`、`?? …cs.meta`（guid `65363f1426779e249885a0d655c24423`）；无 `UIRoot.cs` 等产品文件改动，无 `URP_*.asset` / `QualitySettings.asset` 被 Editor 改写。新测试文件为 UTF-8 无 BOM、LF（`od` 首三字节 `u s i`、`\r` 计数 0）。

### 记录（不影响裁决）

- 简报第 3 点"指针事件被接收"：用例做的是模块命中之后那一段（`EventSystem.RaycastAll` 走 UIRoot 层 `GraphicRaycaster` + `ExecuteEvents` 投递 down/up/click 到探针计数）。批处理无 OS 指针设备，**没覆盖 `StandaloneInputModule` 自身的 `Input.mousePosition` 读取**；施工在 §2.2 如实标注，未冒充"模拟真鼠标"。属"做不到就如实说明"，不计为造假。
- 施工自述的 `cc6f4c19…`、`21/797/281/771,162` 与本轮独立实测**全部一致**；但本轮结论只以自跑 XML 为准。

### 最终裁决

**PASS**（V1–V5 全通过；V3 核心判据由验收代理亲手复现：变异红 1 条于 `UIRootCrossSceneEventSystemPlayModeTests.cs:104`，还原后 md5 与 `git diff` 双证归位并复绿）。现场已还原：无 Unity 进程残留、无 `UnityLockfile`、产品代码与仓库状态干净，未执行 `git commit`。
