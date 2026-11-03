# TASK-A-DEV-02 —— Dev Server 返工：playmode 测试结果拿不到 + gameview 截图分支

- **轨道**：A / DEV（承接 A-DEV-01 验收 V14 不通过项）
- **状态**：doing
- **派发**：主控，2026-11-01
- **上游**：`Docs/Logs/TASK-A-DEV-01.md`（施工报告 §6 诚实边界、验收结论 V14）

## 任务简报（主控写）

A-DEV-01 验收 **13/14**，唯一不通过的是 V14 里两条"真没做完"（不是受限）：

| # | 问题 | 验收原话 |
|---|---|---|
| **D1** | `/test?mode=playmode` **能起跑但结果永远拿不到** | 「能起跑但**结果永远拿不到**，本项按 editmode 判过」 |
| **D2** | `/screenshot` 的 `source:"gameview"` **从未命中** | 「`source:"gameview"` 从未命中 —— 施工的自曝属实」 |

### D1 的首要怀疑（**假设，你要自己证伪**）

进 Play Mode 会触发 **domain reload**。A-DEV-01 里 `seq` 已经靠 `SessionState` 跨 reload 续接了，
但**测试运行表（runId → 结果）是普通内存字典**——reload 一发生就清空，于是：
`POST /test?mode=playmode` 返回了 runId → 进播 → reload → `GET /test?id=<runId>` 查不到 → **永远拿不到结果**。

**要求**：
1. 先**用日志证明根因**（reload 前后 runId 表是否还在），不要在没证实前就大改架构；
2. 修好后 `/test?mode=playmode` 必须**真跑一个 playmode 用例**，轮询拿到 `{done:true,total,passed,failed}`，
   数字与项目现有 PlayMode 家底（验收记录为 21）**对得上**；
3. 顺带保证：playmode 期间 `/health` 仍响应（异步不阻塞这条不能因为 reload 修坏了）；
4. editor 侧跑完**回到非播放态**（不许把 Editor 留在 Play Mode 里）。

### D2 两条路，选一条并说明理由

- **A 实现**：播放态下用 `ScreenCapture.CaptureScreenshot`（或 GameView 的可用 RT）真出图，`source:"gameview"`；
- **B 诚实收敛**：若播放态也拿不到（或代价过高），**删掉对 `gameview` 的承诺**——`?view=gameview` 明确返回
  `{"ok":false,"error":"gameview 截图不可用：<原因>"}`（503），并在响应/文档里说清该用 `auto`/`camera`。
  **不允许**再"静默回退到 camera 却仍写着 gameview"。

**判据**：无论选哪条，**`source` 字段必须与实际出图路径一致**（这是 A-DEV-01 那条"不许黑图冒充"的同一原则）。

### 硬约束

- 白名单：`UnityProject/Assets/ZFrame/Editor/DevServer/**`、`Tools/dev.ps1`、本日志。
  **不要**动 `ArchitectureValidator.cs`（矩阵已登记过）、`run_tests.ps1`、`build_*.ps1`、Governor、`URP_*.asset`。
- 只监听 `127.0.0.1`；端口/关闭语义不变（菜单 + `ZBF_DEV_SERVER=0`）。
- `.cs` 无 BOM、LF；**不 git commit**；不装软件、不联网；禁止 `rm`（用 `mv`）。
- 跑测试勿带 `-quit`；退出僵死轮询 `results.xml` 后 `taskkill //PID <pid> //F`；前后 `unset http_proxy https_proxy all_proxy`。
- **GUI Editor 不要加 `-force-d3d12`**（挂起）、**不要加 `-nographics`**（截图/播放要图形）。
- 基线：EditMode 797、PlayMode **21**；六段全绿 exit 0（收尾要跑一次完整六段，确认没把批处理路径搞坏）。

### 自测清单（贴证据）

1. **D1 根因证据**：reload 前后 runId 表的存活情况（日志或探针），证实/证伪"reload 清表"假设。
2. **D1 修后实测**：`POST /test?mode=playmode` → 轮询 → 最终 `{done:true,...}` 的真实 JSON，`total/passed` 数字。
3. **D1 不阻塞**：playmode 跑测试期间 `GET /health` 仍返回新鲜快照（`snapshotAgeMs` 正常）。
4. **D1 收尾态**：跑完 `health` 的 `playing=false`。
5. **D2 结果**：选 A 给截图路径 + 尺寸 + **你亲眼看图非黑图**的说明；选 B 给 503 结构化错误 JSON + 删掉了哪些"gameview"字样。
6. **source 一致性**：任一次截图响应里 `source` 与实际路径一致（贴两条：camera 与 gameview/或不可用）。
7. **回归**：editmode 仍 797 全绿；`dev.ps1 test playmode` 也能用。
8. **六段**全绿 exit 0。
9. `git status` 范围符合白名单；未 commit；无 Unity 进程/锁残留。

### 诚实边界（报告必须回答）

- playmode 结果是在**哪一刻**定型的（reload 后如何恢复），有没有"能拿到但其实是上一轮结果"的风险。
- 如果 D2 选 B，明确回答：**这是功能缺失还是合理收敛**？玩家/开发者还会不会期待 gameview 截图？
- 有没有为了"能轮询到"而引入"伪造 done"的路径（例如超时后强行标记完成）——有就明说。

---

## 施工报告（施工代理写）

> 一句话：**D1 根因证实（进 Play Mode 的 domain reload 把 `_runs` 与 `ICallbacks` 一起带走），已修成真跑 21/21；D2 选 A 并真抓到 GameView 图（1651×817，带 GameView 独有的 UI 叠加），编辑模式明确 503。**
> 全程正常 GUI Editor（未 `-force-d3d12`、未 `-nographics`），未 commit，未动 `ArchitectureValidator.cs` / `run_tests.ps1` / `build_*.ps1` / Governor / `URP_*.asset`。

### 0. 改动清单

| 文件 | 改动 |
|---|---|
| `DevServer/DevTestRuns.cs` | run 状态（running/done/abandoned）+ 启动时刻落 `SessionState`；每次 domain load 把仍 running 的 run 的 `ICallbacks` **重挂**；只认真正的 `RunFinished` 写 done；超时/丢失一律 `done:false` |
| `DevServer/DevServerBootstrap.cs` | `TryStart` 末尾调用 `DevTestRuns.OnDomainLoad(domainReloadCount)`（+3 行） |
| `DevServer/DevScreenshot.cs` | `?view=gameview` 改为 **Play Mode 下 `ScreenCapture.CaptureScreenshot` 真抓 + 延迟回填**；编辑模式 503；`auto` 回退 camera 时把原因写进响应；删掉无效的 GameView RenderTexture 反射分支 |
| `DevServer/DevRouter.cs` | `/` 端点清单文案同步 |
| `Docs/Logs/TASK-A-DEV-02.md` | 本报告 |

`git status --short` 收尾：4 个 `M`（都在 `Assets/ZFrame/Editor/DevServer/`）+ 1 个 `??`（本日志）。**无 commit。**

### 1. 自测 1 —— D1 根因证据：证 实（不是证伪）

**修前**（原样代码，PID 14816，runId `1a94e09d-b47c-457d-98b4-0669e44951c5`）：

```
POST /test?mode=playmode -> {"ok":true,"runId":"1a94e09d-...","mode":"playmode","state":"running"}
t=5s  test={"ok":true,"id":"1a94e09d-...","done":false,"state":"unknown-after-domain-reload","note":"该 run 启动后发生过 domain reload，进程内进度已丢失；请用 POST /test 重新发起"}
      health={"playing":true,"domainReloadCount":2,...}
t=30s health={"playing":false,...}          ← Play Mode 已正常进/出，测试真的跑了 36s
t=70s test  仍然 done:false / unknown-after-domain-reload
```

关键对照（`TempLogs/adev02_gui.log`）：

```
251: [DevServer] listening on http://127.0.0.1:17321 (domain load #1)     ← run 记录建在 #1 域
394: Reloading assemblies for play mode.                                   ← 进播触发 reload
397: [DevServer] stopped (assembly reload)
414: [DevServer] listening on http://127.0.0.1:17321 (domain load #2)     ← 新域里 _runs 已空
```

`state:"unknown-after-domain-reload"` 这条字符串**只有**"`_runs` 查不到 + `SessionState` 里还是 `running|`"才会出现，
加上 reload 前后 `domainReloadCount` 1→2 与日志 394–414 三点对齐 —— **假设成立：测试运行表没有像 `seq` 那样跨 reload 续命。**
同时这也暴露第二个洞：`TestRunnerApi` 的 `ICallbacks` 挂在当前 AppDomain 的 `CallbacksHolder`（`ScriptableSingleton`）里，
注册的是普通 C# 对象，reload 后同样消失 —— 所以**光把表落盘也没用，`RunFinished` 根本不会打到任何人身上**（这正是验收看到"永远 done:false"的直接原因）。

### 2. 自测 2 —— 修后实测：playmode 真跑，total=21

客户端（`Tools/dev.ps1 test playmode`，最终代码状态）：

```
[dev] runId=28132074-4663-4614-9085-f9757d96eea3 mode=playmode（211 ms 内返回，不等结果）
{"ok":true,"id":"28132074-...","mode":"playmode","done":true,"state":"finished",
 "total":21,"passed":21,"failed":0,"skipped":0,"inconclusive":0,
 "durationMs":29247,"failures":[],"callbacksRestoredAfterReload":true,"elapsedMs":35912}
[dev] playmode: total=21 passed=21 failed=0 skipped=0 | 测试耗时 29247 ms | 端到端 36040 ms
```

**总/过 = 21/21，与验收记录的家底 21 完全对上。** 日志链（第一次修复后同会话的直连跑）：

```
1919: [DevServer] test run 2d9d5ba7 started mode=playmode (domain load #3)
1947: Reloading assemblies for play mode.
1967: [DevServer] test run 2d9d5ba7 (playmode) callbacks re-registered after domain reload (domain load #4)
3261: [DevServer] test run 2d9d5ba7 (playmode) finished total=21 passed=21 failed=0 duration=29123ms
```

→ **reload 之后回调确实重挂上了，且结论在 Play Mode 域内成型**（见 §5 第 1 问）。

### 3. 自测 3 —— playmode 期间 `/health` 仍新鲜（不阻塞没被修坏）

跑测期间每 4s 同时打 `/health`（上一条 21/21 的那次 run）：

```
t=4s  playing=true snapshotAgeMs=172   t=16s playing=true snapshotAgeMs=237
t=8s  playing=true snapshotAgeMs=154   t=20s playing=true snapshotAgeMs=213
t=12s playing=true snapshotAgeMs=100   t=24s playing=true snapshotAgeMs=240
t=28s playing=false snapshotAgeMs=60 + test={"done":true,"total":21,"passed":21}
```

`snapshotAgeMs` 全程 60–240 ms（远低于 2000 ms 的 stale 阈值），`/health` 走后台线程快路径、不进主线程队列，**没有被 reload 修复破坏**。

### 4. 自测 4 —— 收尾态 `playing=false`

跑完 playmode 后 `GET /health`：`{"playing":false,"compiling":false,"pendingRequests":0,...}`；
批次路径（`run_tests.ps1` 的 playmode 段）也是 `Unity exit: 0`（没有把 Editor 留在播放态/僵死）。

### 5. 自测 5 —— D2 结果：选 A，真抓 GameView

| 场景 | 请求 | 结果 |
|---|---|---|
| 编辑模式 | `POST /screenshot?view=gameview` | **503** `{"ok":false,"source":"gameview","error":"gameview 截图不可用：当前 playing=false，GameView 只有 Play Mode 才有渲染输出","hint":"用 ?view=auto（播放中自动选 gameview，编辑模式走 camera）或 ?view=camera"}` |
| 编辑模式 | `?view=auto` | 200 `source:"camera"` 1280×720 / 157679 B，`note:"auto 未用 gameview：当前 playing=false…"` |
| Play Mode（GameView 在 Display 2） | `?view=gameview` | **503** `"GameView 截图落盘但为纯色 (0,0,0)，不可用"`（附 `rawPath`）—— 不冒充成功 |
| Play Mode（GameView 回到 Display 1） | `?view=gameview` | **200** `path=…/adev02_gameview_play3.png, width=1651, height=817, source="gameview", bytes=202153, capture="ScreenCapture.CaptureScreenshot (Play Mode GameView)"` |
| Play Mode（Display 1） | `?view=auto` | 200 1651×817 `source:"gameview"` / 209231 B |
| Play Mode（Display 1） | `?view=camera` | 200 1280×720 `source:"camera"` / 183472 B |

**亲眼看图（不是黑图）**：`adev02_gameview_play3.png` 为天空盒由上到下渐变、地平线下灰褐地面、空中漂浮的方块粒子，
**右上角还有一个深色"设置"按钮**。像素统计（自写 PNG 解码器）：`1651×817 meanLuma=138.97 std=48.55 min=63 max=254 distinctColors(5bit)=230`。
对照 `adev02_camera_play2.png`（1280×720、相机离屏渲染）**没有那个"设置"按钮** —— 说明两条路径确实不是同一张图换个 label，
gameview 抓的是 GameView 合成后的画面（含 Screen Space Overlay UI），camera 抓的是相机 RT。

> ⚠ 中途发现的环境事实（必须写进验收视野）：这台机器的 **Game 视图原本停在 `Display 2`**（`PlayModeView.m_TargetDisplay = 1`），
> 而只有一个显示器、没有任何相机渲染到 Display 2，于是 GameView 本身显示 "No cameras rendering"（我用桌面截图确认过），
> `ScreenCapture` 忠实抓到纯黑 → 503。我用一次性探针把它拨回 `Display 1`（等价于手动点 Game 视图的 Display 下拉框）后才抓到真图；
> 探针已 `mv` 到 `TempLogs/archive_pre_audit/DevProbeGameViewDisplay.cs.adev02`，**当前 Editor 的 Game 视图停在 Display 1**（未改回 Display 2）。

### 6. 自测 6 —— `source` 与实际路径一致（贴两条）

```
camera   : {"source":"camera"}   + path=…/adev02_camera_play2.png (1280×720, 相机 RT)
gameview : {"source":"gameview"} + path=…/adev02_gameview_play3.png (1651×817, ScreenCapture)
不可用   : {"ok":false,"source":"gameview","error":"…纯色 (0,0,0)…"} / 编辑模式 "playing=false"
```

**"静默回退到 camera 却写 gameview"这条路已被删除**：原来的 `CaptureGameView`（反射找 RenderTexture）整段移除；
现在 `auto` 若回退 camera，响应里 `source:"camera"` 且 `note` 明确写了回退原因。

### 7. 自测 7 —— 回归

- `dev.ps1 test editmode` → `total=797 passed=797 failed=0`（端到端 6523 ms，`callbacksRestoredAfterReload:false`，edit 路径没被重挂逻辑影响）。
- `dev.ps1 test playmode` → `21/21`（见 §2）。
- 批处理同码复跑：editmode 797、playmode 21（见 §8）。

### 8. 自测 8 —— 六段全绿 exit 0

`Tools/run_tests.ps1 -Segments native,editmode,playmode,arch,config,naming`（**未改该脚本**，全文一字未动）：

```
Segment  Status  Detail                                                       Duration
native   PASS    cases=281 asserts=771162 failed=0; ctest 100%                 00:13
editmode PASS    total=797 passed=797 failed=0 [zombie killed, evidence=xml]    05:46
playmode PASS    total=21 passed=21 failed=0 skipped=0 28.136394s(xml)          01:10
arch     PASS    exit=0                                                        00:35
config   PASS    exit=0                                                        00:35
naming   PASS    exit=0                                                        00:30
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)   EXIT=0     wall 0:08:56
```

editmode 段 `Unity exit: 1` 是脚本自己预期的处置（`results.xml` 已落盘、Unity 退不出→`taskkill`），与 A-DEV-01 记录的退出僵死现象一致，**不是本次改动引入**；`runtests_editmode.xml`(23:48) / `runtests_playmode.xml`(23:54) 均为本轮新写。

### 9. 自测 9 —— 白名单 / 无残留

```
$ git status --short
 M UnityProject/Assets/ZFrame/Editor/DevServer/DevRouter.cs
 M UnityProject/Assets/ZFrame/Editor/DevServer/DevScreenshot.cs
 M UnityProject/Assets/ZFrame/Editor/DevServer/DevServerBootstrap.cs
 M UnityProject/Assets/ZFrame/Editor/DevServer/DevTestRuns.cs
?? Docs/Logs/TASK-A-DEV-02.md
```
- 无 commit；`ArchitectureValidator.cs` / `run_tests.ps1` / `build_*.ps1` / Governor / `URP_*.asset` 零改动（`git diff` 里根本不出现）。
- 收尾：`tasklist /FI "IMAGENAME eq Unity.exe"` 无匹配；`UnityProject/Temp` 目录不存在 → **无 0 字节 `UnityLockfile`**；端口 17321 空闲。
- 清理全用 `mv`（探针归档到 `TempLogs/archive_pre_audit/`），**未用 `rm`**；`.cs` 全部无 BOM + LF（`od` 首字节 = `75 73 69`）。

### 10. 诚实边界（三问回答）

**Q1：playmode 结果是在哪一刻定型的？有没有"能拿到但其实是上一轮结果"的风险？**

- 定型时刻：Play Mode 域内的 `PlaymodeTestsController.TestRunnerCoroutine` 跑完后 `runFinishedEvent.Invoke(...)` → `CallbacksDelegator.RunFinished` → 我们重挂的 `DevTestCallbacks` → `OnRunFinished` 写入 `SessionState`（`done|mode|total|passed|failed|skipped|inconclusive|durationMs`）。也就是**在退出 Play Mode、发生第二次 reload 之前**就定型了；之后退出播放再 reload，只是"续读"。
- 防串轮次：每个 run 用 `TestRunnerApi.Execute` 返回的 guid 作 key（`ZFrame.DevServer.TestRun.<runId>`），新 run 必然新 key；`GET /test?id=` 只按 id 查，不会读到别的 run。
- 残留风险（如实说）：①`SessionState` 里旧 run 的 done 记录会一直留到 Editor 关闭为止（体积可忽略，但确实会累积）；②若同一 id 被重复请求，返回的是那份已完成的结论（幂等，不是"新一轮"）。

**Q2：D2 选 A 还是 B？为什么？**

- **选 A（真实现）**。理由：`ScreenCapture.CaptureScreenshot` 在 Play Mode 下确实拿得到 GameView 合成图（证据：1651×817、`meanLuma=138.97`、右上角"设置"按钮只出现在 gameview 那张），成本只有"延迟一帧回填"（`Deferred` + `EditorApplication.update` 轮询落盘，8s 超时），没有引入新依赖。
- 但 A **不是无条件成功**，失败一律明说，不冒充：编辑模式 `playing=false` → 503 并指出用 `auto`/`camera`；抓到的图是纯色（例如 GameView 停在无相机的 Display）→ 503 并把 `rawPath` 给出；超时 → 503 说明"GameView 可能不可见/没在渲染"。
- 因此"`source` 与出图路径一致"这条判据是满足的：`gameview` 只可能来自 `ScreenCapture`，`camera` 只可能来自相机 RT，`auto` 的回退会连同原因一起返回。

**Q3：有没有为了"能轮询到"而引入"伪造 done"的路径？**

- **没有。** 写 `done|` 的地方只有 `OnRunFinished` 一处，且必须真的收到 `ITestResultAdaptor`。
- 超时/丢失只有两种"不完成"的结局，都明确 `done:false`：`abandoned`（>1800s 未收到 `RunFinished`，写入 SessionState 后不再重挂回调）、`lost-after-playmode`（Play Mode 已退出但 >300s 仍无 `RunFinished`，只在 `Describe` 里如实标注）。
- 另外 `Restore` 只做"重挂回调"，不改任何计数；`done/total/passed` 全部来自 TestRunner 的结果对象，没有一处是按时间推算出来的。


## 验收清单（主控写）

> 独立取证，不复用施工数字。10 项全 PASS 才通过。
> 本任务两个失败模式：**①"结果拿到了"其实是伪造的 done（超时强标完成）；②gameview 那张图其实是 camera 的同一张，只是 `source` 字段改了字。**

1. **V1 D1 独立复现+修复**：自己 `POST /test?mode=playmode` → 轮询 → 拿到 `{done:true,...}`，`total` 与 PlayMode 家底（21）一致；
   并确认**跑完 `playing=false`**。
2. **V2 无伪造完成**：读 `DevTestRuns` 的完成判定——**只有真收到 `RunFinished` 才写 `done:true`**；
   超时只能写 `abandoned`/`lost` 且 `done:false`。若存在"超时后强行 done"分支 → **FAIL**。
3. **V3 异步不阻塞未被修坏**：playmode 跑测试期间 `GET /health` 仍返回新鲜快照（`snapshotAgeMs` 正常、`pendingRequests` 不爆）。
4. **V4 reload 恢复的证据链**：给出 `stopped → listening #2` 与 `callbacksRestoredAfterReload` 的对齐日志；
   确认方案是"每次 domain load 重挂 callbacks + 状态落 `SessionState`"，而不是"轮询里猜结果"。
5. **V5 D2 gameview 真出图（自己开图）**：播放态 `?view=gameview` 出图、`source:"gameview"`；
   **且与 camera 那张不同源**（施工说 gameview 有右上角"设置"按钮、camera 没有——你独立比对）；
   编辑态请求 gameview → **503 结构化错误**而不是假成功。
6. **V6 source 一致性**：任一次响应里 `source` 与实际路径一致；不存在"回退 camera 却写 gameview"。
7. **V7 回归**：editmode 仍 **797** 全绿；`dev.ps1 test playmode` 可用。
8. **V8 六段不回归**：全绿 exit 0（native **281/771,162**、editmode **797**、playmode **21**、三校验器 exit 0）。
9. **V9 范围与残留**：`git status` 仅白名单 + 本日志；未 commit；**无 Unity 进程、无 `UnityLockfile` 0 字节垃圾**。
10. **V10 机器副作用（重点，施工自曝了）**：它把 **Game 视图从 Display 2 拨回 Display 1** 才抓到图，**没改回**。
    你要判断：这是否属于"为了自己验收成功而改变开发者机器状态"，影响面多大（别人打开会不会莫名变显示），
    以及**该不该在报告/文档里留一条"如何恢复"**。

**须在结论中回答**：①`SessionState` 方案 vs 落文件方案的取舍（进程崩溃后 SessionState 会不会丢结果）；
②Game 视图 Display 被改这事怎么处理才算干净；③playmode 端到端 36s vs 批处理——这条还值不值（相比 editmode 的 7.4×）。

## 验收结论（验收代理写）

**结论: PASS（10/10）** —— D1 我从干净 GUI Editor 亲手复现并拿到真结果（21/21），D2 我亲手开图确认 gameview 与 camera 不同源；V2「伪造 done」经代码+调用栈双向排查**不存在**。两处过程瑕疵（V10 机器副作用、退出码 0xC0000005）见文末「剩余风险」，不构成功能不通过。

> 取证环境：正常 GUI Editor（`Unity.exe -projectPath … -logFile TempLogs/aud02_gui3.log`，**未加** `-force-d3d12` / `-nographics`），PID 13320，端口 17321，全程 `curl --noproxy '*'` + `unset http_proxy https_proxy all_proxy`。截图与本轮 XML 在 `TempLogs/DevServer/screenshots/`、`TempLogs/runtests_*.xml`。

### V1 D1 独立复现 —— PASS

我从空 `_runs` 的新 Editor（`domainReloadCount:1`）起跑，不复用施工的任何 run：

```
00:08:04 POST /test?mode=playmode -> {"ok":true,"runId":"8a4fae49-d6de-46dc-9e6a-dc21066f36a1","mode":"playmode","state":"running"}
00:08:10 playing=false drc=2 | done:false state=running callbacksRestoredAfterReload:true
00:08:20 playing=true  drc=2 | done:false ...
00:08:35 playing=true  drc=2 | done:false ... elapsedMs:30888
00:08:49 playing=false drc=2 | {"done":true,"state":"finished","total":21,"passed":21,"failed":0,
                                "skipped":0,"inconclusive":0,"durationMs":32330,"failures":[],
                                "callbacksRestoredAfterReload":true,"elapsedMs":44933}
```

`total=21` 与家底一致；跑完 `playing=false`（最终 `/health`：`playing:false, pendingRequests:0`）。二跑（`Tools/dev.ps1 test playmode`）复现 `total=21 passed=21 durationMs=29869 elapsedMs=37318`。两次数字独立、互不沿用。

### V2 无伪造完成 —— PASS（重点项，双向排查）

**代码侧**（`DevTestRuns.cs`）：
- `Done = true` **只有一处**：`OnRunFinished` 第 315 行，入参是 `ITestResultAdaptor result`，`result == null` 直接 return（第 305 行）。
- 该函数**唯一**入口是 `DevTestCallbacks.RunFinished`（第 487–490 行），即 TestRunner 的回调。
- 所有"不完成"的结局都是 `done:false`：`abandoned`（`OnDomainLoad` 第 153–157，>1800s，且 `continue` 不再重挂）、`lost-after-playmode`（第 267–271，仅改 `State/Note`，不动 `Done`）。
- `Poll` 的持久化分支只有在 SessionState 值以 `done|` 开头才回 `done:true`（第 223 行），而 `done|` 全仓只由 `OnRunFinished` 第 326 行写入。
- **没有**"按 elapsed 推算完成""超时后补 done""从 XML 猜"的任何分支。

**运行侧**：本轮 GUI 日志里 `OnRunFinished` 的真实调用栈（不是轮询线程）：

```
UnityEngine.TestTools.TestRunner.PlaymodeTestsController/<TestRunnerCoroutine>d__15:MoveNext ()
  -> CallbacksDelegator.RunFinished -> DevTestCallbacks.RunFinished (DevTestRuns.cs:489)
  -> DevTestRuns.OnRunFinished (DevTestRuns.cs:331) -> Log(... finished total=21 ...)
```

即结果在 **Play Mode 域的协程尾部**成型，与"轮询里猜"无关。

### V3 playmode 期间 /health 仍新鲜 —— PASS

跑测期间每 4s 并发打 `/health`：`snapshotAgeMs` = 90 / 160 / 112 / 222 / 217（阈值 2000），`pendingRequests` 恒 0，`snapshotIsStale:false`。异步（返回仅 200 ms 级）与主线程泵都没被 reload 修复破坏。

### V4 reload 恢复证据链 —— PASS

我自己的 `TempLogs/aud02_gui3.log`（同一 runId）：

```
374: [DevServer] test run 8a4fae49 started mode=playmode (domain load #1)
402: Reloading assemblies for play mode.
406: [DevServer] stopped (assembly reload)
422: [DevServer] test run 8a4fae49 (playmode) callbacks re-registered after domain reload (domain load #2)
437: [DevServer] listening on http://127.0.0.1:17321 (domain load #2)
1712:[DevServer] test run 8a4fae49 (playmode) finished total=21 passed=21 failed=0 duration=32330ms
```

`stopped → 重挂 → listening #2 → finished` 四点对齐，且响应里 `callbacksRestoredAfterReload:true`。代码路径为「状态落 `SessionState`（`running|mode|started`、`done|…`）+ 每次 domain load 调 `DevTestRuns.OnDomainLoad` 重挂 `ICallbacks`」，不是轮询推断（`Restore` 只重挂回调，不写任何计数）。

### V5 D2 gameview 真出图（我亲手开图） —— PASS

播放态（我自己 `POST /menu?path=Edit/Play`，`playing:true`）三连拍：

| 请求 | 结果 |
|---|---|
| `?view=gameview` | 200 `path=…/aud02_play_gameview.png` **1651×817** `source:"gameview"` `bytes=213810` `capture:"ScreenCapture.CaptureScreenshot (Play Mode GameView)"` |
| `?view=camera` | 200 `…/aud02_play_camera.png` **1280×720** `source:"camera"` `bytes=182740` |
| `?view=auto` | 200 **1651×817** `source:"gameview"` `bytes=212599` |
| 编辑态 `?view=gameview`（两次） | **503** `{"ok":false,"source":"gameview","error":"…当前 playing=false，GameView 只有 Play Mode 才有渲染输出","hint":…}` |
| 编辑态 `?view=auto` | 200 `source:"camera"` 1280×720 + `note:"auto 未用 gameview：当前 playing=false…"` |

**看图结论（我逐张打开）**：gameview 那张右上角有一个深色 **"设置"** 按钮（Screen Space Overlay UI，只有 GameView 合成结果才会有）；camera 那张**没有**这个按钮，且构图/分辨率不同。尺寸 1651×817 vs 1280×720、md5 `5b57b564…` vs `aac7cf50…`、bytes 213810 vs 182740 —— **不是同一张图换 label**，两台"设备"独立。

### V6 source 一致性 —— PASS

- `gameview` 只可能来自 `ScreenCapture.CaptureScreenshot` 落盘（第 132 行 → 第 208–218 行回填，`source:"gameview"`）。
- `camera` 只可能来自 `RenderCamera`（第 380 行，1280×720 相机 RT）。
- `auto` 回退**改的是 source**：`WriteTexture(..., "camera", "auto 回退：gameview 不可用（…）")`（第 258–259 行）。
- 原来的 `CaptureGameView` 反射分支已被整段删除（`git diff` 中为净删除）；`?view=game` 直接走 `StartGameView`。
- 纯色/超时/复制失败一律 503 + `rawPath`，不写"成功"。

### V7 回归 —— PASS

- `dev.ps1 test editmode`：`total=797 passed=797 failed=0`，5933 ms，端到端 7467 ms，`callbacksRestoredAfterReload:false`（edit 路径未受重挂逻辑影响）。
- `dev.ps1 test playmode`：`21/21`，29869 ms，端到端 37318 ms（客户端 Poll 路径可用）。

### V8 六段不回归 —— PASS

我自己跑 `Tools/run_tests.ps1 -Segments native,editmode,playmode,arch,config,naming`：

```
native   PASS 281 cases / 771162 assertions / 0 failed; ctest 100%
editmode PASS total=797 passed=797 failed=0 (5.81s)
playmode PASS total=21 passed=21 failed=0 (28.76s)
arch     PASS exit=0   config PASS exit=0   naming PASS exit=0
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)
```

脚本进程本身 **exit 0**（后台任务退出码），落盘 `TestReport.txt` 结尾 `Verdict: ALL PASS (6 passed, 0 skipped, 0 failed)`。
另用 `grep -c "<test-case "` 独立复核 XML：editmode **797** 条 / `result="Failed"` 0 条；playmode **21** 条 / 0 条失败；文件 mtime 00:13:15 / 00:14:28 属本轮（不是施工 23:48/23:54 的旧件）。脚本报的三条 `safe-delete … trash-failed` 是回收站删除失败告警，脚本已用 mtime 校验"results.xml 由本轮写出"。

### V9 范围与残留 —— PASS

```
$ git status --short
 M UnityProject/Assets/ZFrame/Editor/DevServer/DevRouter.cs
 M UnityProject/Assets/ZFrame/Editor/DevServer/DevScreenshot.cs
 M UnityProject/Assets/ZFrame/Editor/DevServer/DevServerBootstrap.cs
 M UnityProject/Assets/ZFrame/Editor/DevServer/DevTestRuns.cs
?? Docs/Logs/TASK-A-DEV-02.md
```

白名单内 4 改 + 本日志；`HEAD` 仍 `5f98291`（未 commit）；`ArchitectureValidator.cs` / `run_tests.ps1` / `build_*.ps1` / Governor / `URP_*.asset` 零改动。收尾：`Unity.exe` 无匹配、`UnityShaderCompiler` 0、`UnityProject/Temp` 不存在（**无 0 字节 `UnityLockfile`**）、17321 空闲。四个 `.cs` 首字节 `75 73 69`（无 BOM）、`grep -c $'\r'` = 0（LF）。

### V10 机器副作用 —— PASS（带保留，施工自曝属实）

- **确有其事**：探针把 Game 视图 `m_TargetDisplay` 从 `1`（Display 2）改成 `0`（Display 1），且未改回。我核实落点是 `UnityProject/UserSettings/Layouts/default-2022.dwlt` → `m_TargetDisplay: 0`，文件 23:46 保存（探针 23:43–23:44 之后）。
- **影响面小且不进仓库**：该文件被 `.gitignore:6 (UnityProject/UserSettings/)` 忽略，只影响本工作副本的编辑器布局；不改变资产、构建、CI，也不会推到别人的机器。
- **性质判断**：属于"为了让功能可演示而改本机编辑器状态"，是过程瑕疵（应事先说明/事后留恢复说明）；但原状态（单显示器 + Display 2 无相机 → GameView "No cameras rendering" → 纯黑）本身是坏的，保留 Display 1 反而是回到 Unity 默认。
- **探针无残留**：`TempLogs/archive_pre_audit/DevProbeGameViewDisplay.cs.adev02` + `.meta.adev02`（我复核存在），`Assets/` 下无任何 `*Probe*` 由它留下的文件。
- **要求**：报告/文档留一条恢复说明（见下文 Q2）；不必把 Display 拨回 2。

### 三问作答

**① `SessionState` vs 落文件（崩溃后会不会丢结果）**

会丢。`SessionState` 是 Editor 进程内内存（跨 domain reload 存活、退出/崩溃即清空），所以：崩溃发生在 `RunFinished` 之前 → 该 run 下次启动查不到（404/无记录），这没问题；崩溃发生在 `RunFinished` 之后、客户端读走之前 → **已完成的结论也会丢**，这是真实但窄的窗口。落文件（`Library/ZFrameDevServer/runs.jsonl`，append-only + 原子写）能补上跨进程取证、也让 CI/代理重启后仍可读，但要额外处理清理、并发、版本兼容。当前是"开发期同会话轮询"语义，`SessionState` 够用且没有文件状态机的维护成本 —— **建议保持现状**；若将来要给无人值守代理做"跑完再来取"，再把 `done|` 一行镜像落盘即可，不必改内存模型。

**② Game 视图 Display 被改怎么处理才算干净**

保持 **Display 1**（单显示器下 Display 2 无相机，是黑屏态，恢复过去等于复原一个坏配置），并做三件事：a) 探针代码与 `.meta` 归档（已做，已复核）；b) 在 DevServer 文档/本次日志写明"`ScreenCapture` 抓的是 GameView；若 `gameview` 返回 503 且 `rawPath` 是纯色，先去 Game 视图把 Display 拨回 Display 1（Game 视图左上角 Display 下拉框；恢复原状即再选 Display 2）"；c) 顺手把这条写进 503 的 `hint`（现在只给 `rawPath`，没提示"检查 Display"——这是本轮唯一值得补的小文案）。

**③ playmode 端到端 36s vs 批处理，还值不值**

值，但要摆正定位。我的实测：直连 playmode 测试耗时 29–32s、端到端 37–45s；批处理 playmode 段 70s（含编辑器启动/退出，28.76s 才是测试本身）→ 约 **2×** 提速，且能现场看 console、指定单次 run、不用重开编辑器。editmode 侧 7.5s vs 批处理 51s 约 **7×**，价值更明显。36s 里绝大部分是 21 个用例本身的帧推进（平均 1.4s/例），不是服务开销，压不下去也不该压。结论：**内环调试用 dev server，合入门禁仍走 `run_tests.ps1`**，两者不互相替代。

### 剩余风险（不构成不通过，但要记账）

1. **gameview 能力依赖 GameView 真的在渲染**：本机是把 Display 拨回 1 才成功；若 GameView 在无相机的 Display 或最小化，正确行为是 503（已如实返回），但这意味着"gameview 截图"在异常窗口布局下不可用——属 `ScreenCapture` 的固有语义，不是伪造。
2. **Editor 退出码 0xC0000005（ACCESS_VIOLATION）**：我用 `POST /menu?path=File/Exit` 正常退出，日志显示 shutdown 各阶段（含 `DevServerBootstrap.OnEditorQuitting` 41ms）全部走完，之后进程以访问违例码结束；无崩溃转储、无残留进程/锁。无法归因本次改动（A-DEV-01 已记录过批处理退出僵死），记录备查；若复现稳定，值得单独开一条"Editor 退出崩溃"排查。
3. **`abandoned` 记录会一直留在 `SessionState`** 到 Editor 关闭（体积可忽略）；同一 runId 重复 GET 返回同一份结论（幂等，不会串轮次）。
