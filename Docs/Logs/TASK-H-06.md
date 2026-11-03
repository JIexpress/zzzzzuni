# TASK-H-06 —— 修复 EditMode 唯一失败用例（GameHudLeakTests）

> 施工代理按本简报实现并自测，写「施工报告」到本文档第二节。**禁止 git commit**。
> 验收: general-purpose 验收子代理（2026-10-27 独立实跑取证，与施工代理不同人）

## 任务简报（主控撰写）

- 失败用例：`Assets/Game/Tests/View/GameHudLeakTests.cs`
  `HudHostDestroyed_LeavesNoCaptionsOnThePersistentUIRoot`——断言 `DestroyImmediate(host)` 后
  UIRoot 上无残留字幕，实际残留 2 个。
- 根因：**本项目 EditMode 下 `DestroyImmediate` 不回调 `MonoBehaviour.OnDestroy`**
  （既有先例：GameRoot.Initialize/Shutdown、AudioService.Shutdown 接缝——A-M3-02 施工日志有记录）；
  workbuddy 的 H-01 修复（`GameHud.cs` OnDestroy→DestroyCaption，生产逻辑本身正确）依赖 OnDestroy
  回调，EditMode 不触发。
- 修法（按项目接缝先例）：`GameHud` 增加公共 `Shutdown()`（含防重入 bool；OnDestroy 调它；
  字幕销毁/退订逻辑移入），测试改为显式调 `gameHud.Shutdown()` 后再 `DestroyImmediate(host)` 并断言；
  **不改栈序/事件语义，不扩大范围**。
- 约束：只动 `UnityProject/Assets/Game/View/UI/GameHud.cs`、
  `UnityProject/Assets/Game/Tests/View/GameHudLeakTests.cs`、本任务日志；不 git commit；
  UTF-8 无 BOM/LF。自测 `Tools/run_tests.ps1 -Segments editmode`，EditMode 557/557、exit 0。

## 施工报告（施工代理撰写）

### 改动 1 — `UnityProject/Assets/Game/View/UI/GameHud.cs`（接缝化）

- 新增字段 `private bool _shutdown;`（防重入）。
- 新增公共 `public void Shutdown()`：原 `OnDestroy` 的全部拆除逻辑**原样迁入**（顺序不变）——
  ①逐个 `Dispose` 事件退订 token 并清空 `_tokens`/`_enemies`；②`DestroyCaption(ref _pickupText)`、
  `DestroyCaption(ref _enemyText)`。开头 `if (_shutdown) return;` 幂等防重入（未 Build 也安全）。
  文档注释写明接缝动机（EditMode 不驱动 MonoBehaviour 回调，仿
  `AudioService.Shutdown`＝GameRoot.Initialize/Shutdown 先例，TASK-H-06）。
- `private void OnDestroy()` 改为仅转发 `Shutdown()`——播放模式（SCN_Game 场景卸载销毁宿主）路径
  **完全不变**：引擎触发 OnDestroy→Shutdown，栈序与事件语义零改动。
- 类头注释同步更新（OnDestroy 字样改为 Shutdown 接缝描述，注明 TASK-H-06）。

### 改动 2 — `UnityProject/Assets/Game/Tests/View/GameHudLeakTests.cs`（显式驱动接缝）

- `HudHostDestroyed_LeavesNoCaptionsOnThePersistentUIRoot`：`Build(_root)` 后、
  `DestroyImmediate(host)` 前，显式调 `gameHud.Shutdown()`（带注释说明 EditMode 不会自动触发
  OnDestroy，显式调用即播放模式 OnDestroy→Shutdown 的同一路径）。三处断言（childCount 复位、
  `PickupCounter`/`EnemyHint` 不在持久 canvas）**原样保留**；仅把失败消息里的 "OnDestroy" 措辞
  改为 "Shutdown" 以保准确。3 次循环结构、断言次序、事件语义均未动。
- 类头注释补一段接缝说明；`HudCaptions_AreStillCreatedOnTheHudCanvas` 用例未改动。

### 自测证据（实跑）

- 命令：`powershell -NoProfile -ExecutionPolicy Bypass -File Tools/run_tests.ps1 -Segments editmode`
  （先 `unset http_proxy https_proxy all_proxy`，按 HANDOFF 大小写代理变量纪律）。
- 结果：**exit 0**；`TestReport.txt`：`editmode -> PASS，total=557 passed=557 failed=0 skipped=0`；
  证据文件 `TempLogs/runtests_editmode.xml`（2026-10-27 00:11 实跑）。
- 定向确认：XML 中
  `Game.View.Tests.GameHudLeakTests.HudHostDestroyed_LeavesNoCaptionsOnThePersistentUIRoot` 与
  `...HudCaptions_AreStillCreatedOnTheHudCanvas` 均 `result="Passed"`。
- 文件规范：两文件均 UTF-8 无 BOM、LF（`head -c 3` 无 BOM 字节、`grep -c $'\r'` = 0）。

### 范围声明

仅改动上述两个 .cs 文件与本日志；未 git commit；未动 GameBootstrap/asmdef/.meta。

## 验收清单（验收代理填写）

验收人：general-purpose 验收子代理（与施工代理不同人，未修改任何代码）。验收时间：2026-10-27 00:17–00:23，全部证据为验收代理亲跑/亲查。

| # | 检查项 | 结果 | 证据 |
|---|---|---|---|
| 1 | 亲跑 EditMode 测试 | PASS | 验收代理实跑 `Tools/run_tests.ps1 -Segments editmode`（跑前先 unset 大小写代理变量、确认无 Unity 进程/无 UnityLockfile）：输出 `RESULT: ALL PASS (1 passed, 0 skipped, 0 failed)`；脚本源码（Tools/run_tests.ps1 末段）仅在 failedRows=0 分支 `exit 0`，故 ALL PASS 即 exit 0。本次实跑产物 `TempLogs/runtests_editmode.xml`（2026-10-27 00:18）：`total="557" passed="557" failed="0"`；XML 内 `Game.View.Tests.GameHudLeakTests.HudHostDestroyed_LeavesNoCaptionsOnThePersistentUIRoot` 与 `...HudCaptions_AreStillCreatedOnTheHudCanvas` 均 `result="Passed"`。注：Unity 写出 xml 后 300s 宽限未退出，脚本按纪律 taskkill（HANDOFF 纪律⑤ zombie 路径，证据以 xml 为准，unity exit=1 属既定处理，非失败） |
| 2 | GameHud.cs：Shutdown 幂等 / OnDestroy 仅转发 / 拆除逻辑与 H-01 版等价 / 播放模式零变化 | PASS | `GameHud.cs:44` 新增 `private bool _shutdown;`；`:87` `public void Shutdown()` 开头 `:89 if (_shutdown) return;` + `:94 _shutdown = true;`（幂等防重入；未 Build 亦安全——token 空表、DestroyCaption 判 null）；git diff 证实原 OnDestroy 拆除体逐行迁入且顺序未动（Dispose 退订→清 `_tokens`/`_enemies`→`DestroyCaption(ref _pickupText)`:107→`DestroyCaption(ref _enemyText)`:108）；`:111-114` `OnDestroy` 仅 `Shutdown();` 转发。播放模式零变化：`GameBootstrap.cs:348-350` 仅 AddComponent+Build（未改动、未调 Shutdown），场景卸载销毁宿主→引擎回调 OnDestroy→Shutdown，与原路径同栈序；`Build()` 与 `DestroyCaption()`（含 `Application.isPlaying` 分支）零改动 |
| 3 | GameHudLeakTests.cs 改动最小化 | PASS | git diff（HEAD vs 工作区）仅三类：类头注释 +4 行（接缝说明）、用例内注释 +3 行 + `gameHud.Shutdown();` 1 行调用（位于 `Build` 后、`DestroyImmediate(host)` 前）、失败消息一词 `"OnDestroy"`→`"Shutdown"`；3 次循环结构、三处断言（childCount 复位 / `PickupCounter` / `EnemyHint` 不在持久 canvas）原样未动；`HudCaptions_AreStillCreatedOnTheHudCanvas` 用例零改动 |
| 4 | 接缝先例一致性（行号对照） | PASS | 与 AudioService 逐点同构：`AudioService.cs:460` `public void Shutdown()` 开头 `:462 if (!_initialized) return;` + `:467 _initialized = false;`（防重入 bool）→ `:485 private void OnDestroy()` → `:487 Shutdown();`；GameHud：`:87 Shutdown()` / `:89 _shutdown` 守卫 / `:111 OnDestroy` → `:113 Shutdown();`——同一模式。GameRoot 同模式佐证：`GameRoot.cs:55 public void Initialize()`（`Awake:43` 转发）、`:87 OnDestroy` → `:94 Shutdown()`、`:137 public static void Shutdown()`（幂等）。GameHud 类头与两文件注释均引同一先例（A-M3-02） |
| 5 | 文件规范 + 范围 + 提交纪律 | PASS | 三文件（GameHud.cs / GameHudLeakTests.cs / 本日志）实测 UTF-8 无 BOM（首 3 字节 `usi`/`# T`，无 EF BB BF）、0 个 CR 字节＝纯 LF（`tr -dc '\r' | wc -c` = 0），与 .gitattributes `* text=auto eol=lf` 一致。git status 仅两 .cs 修改 + 本日志新增；`zrender.dll` 虽显示 M 但 mtime（00:01）早于两 .cs（00:10），属 H 轨 H-05 原生重编的未提交遗留，非 H-06 改动；GameBootstrap/asmdef/.meta 未动；未 git commit |

## 验收结论（验收代理填写）

**PASS**（2026-10-27，general-purpose 验收子代理，独立实跑取证，未复用施工方结论）

- ① 测试亲跑：exit 0（脚本 ALL PASS 分支）＋ EditMode **557/557**（failed=0）＋ 两个 GameHud 用例均 `result="Passed"`——证据 `TempLogs/runtests_editmode.xml`（00:18 验收实跑；施工方旧证据已归档 `TempLogs/archive_pre_audit/runtests_editmode.pre-H06-accept.xml`）与 `TestReport.txt`（00:23）
- ② 代码：`Shutdown()` 幂等（`GameHud.cs:89` 防重入 bool）、`OnDestroy`（:111）仅转发、拆除顺序与 H-01 版逐行等价（diff 证实未动栈序/事件语义）、播放模式路径零变化（GameBootstrap 未动，OnDestroy→Shutdown 同原拆除）
- ③ 测试改动最小：仅 +`Shutdown()` 显式调用与注释/措辞，断言结构与次序未动
- ④ 先例一致：与 `AudioService.Shutdown`（:460 / `_initialized` 守卫 / :485 OnDestroy 转发）逐点同构，`GameRoot.Initialize/Shutdown` 同模式
- ⑤ 规范/范围：三文件 UTF-8 无 BOM＋LF；改动仅两 .cs＋本日志；未 commit
- 附注（不属本任务判定）：HANDOFF.md「Unity 批处理在本机当前环境不可用」记载已过期——施工（00:11）与本次验收（00:23）两次经 `run_tests.ps1` 实跑成功，建议主控择机刷新该节
