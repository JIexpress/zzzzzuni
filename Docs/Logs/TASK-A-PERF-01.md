# TASK-A-PERF-01 —— 第一版帧耗基线探针（清单第 1 条）

- **轨道**：A / PERF（`Docs/Planning/07-第一版清单-2026-11-13.md` 第 1 条「打开工程跑通一次，跑探针记下帧耗」）
- **状态**：已交付（一台机器、release Player、可复核）
- **执行**：施工子代理，2026-11-15，本机（AMD Radeon 780M / Ryzen 7 7840HS）
- **纪律**：未 commit、未改看板（TASKS/HANDOFF/WORKLOG）、未改玩法逻辑

---

## 1. 结论速览

**基线成立，但不是"零抖动"的基线。** 产出一份可重跑、可复核的 1080p 帧耗档：
`Tools/Probes/out/frame-baseline-2026-11-15T04-03-41.json`（第一轮）与 `-04-03-57.json`（第二轮）。

| 口径 | 两轮 p50 | 判据（两轮 p50 差 ≤5%） |
|---|---|---|
| `cpuFrameTime`（帧间隔） | 11.108 / 11.115 ms（= 90fps 上限） | **0.06%，稳** |
| `cpuMainThreadFrameTime` | 2.801 / 3.049 ms | **+8.9%，不稳** |
| `cpuRenderThreadFrameTime` | 0.967 / 0.819 ms | **−15.4%，不稳** |
| `gpuFrameTime` | 3.427 / 3.142 ms | **−8.3%，不稳** |

一句话：**帧间隔被 vsync 钉死在 90Hz（稳到 0.06%），但三条底层线程/GPU 时间的 p50 在亚毫秒到 3 毫秒量级上抖 8–15%**。同形（shape 一致，见 §3 的 SetPass/Batches 恒定），不同值。**不粉饰成"稳定"**——原因与可用的替代读法写在 §4。

另有一项**取不到就报缺**：`GC Allocated In Frame` 在本 release Player 里无 marker（Render/Memory/Internal/Scripts 四个 category × 两个名字别名，共 8 组全试），JSON 里是 `null` + `MISSING` + 试过的全部组合；没有用任何替代量（如 `GC.GetTotalMemory` 差值）冒充这个字段。

---

## 2. 口径与命令

### 2.1 锁定的口径（JSON 逐项落档）

| 项 | 值 | JSON 字段 |
|---|---|---|
| Unity | **2022.3.62f3c1** | `unityVersion` |
| 渲染管线 | **URP 14.0.11**（`URP_Medium` 资产，`UniversalRenderPipelineAsset`；`ZRenderBoot.IsActive=false`） | `renderPipeline.asset/type`（包版本运行期读不到 → `MISSING`，见下） |
| 色彩空间 | **Linear** | `colorSpace` |
| 质量档 | **当前档 Medium（index=1），原样未改**；`vSyncCount=1`、`antiAliasing=2`、`renderScale=1`、`targetFrameRate=-1` | `qualityLevel.*` |
| 分辨率 | **1920×1080 窗口**（实测 Screen=1920×1080、相机像素=1920×1080、Windowed） | `resolution.*` |
| 机 / GPU | 本机 **AMD Radeon 780M**（RDNA3 iGPU，`0x15BF`，`SystemInfo` 报 Direct3D12 [level 12.1]、显存 7778MB），5002 Ryzen 7 7840HS / 16 线程 / 15.5GB | `gpu.*` |
| 驱动版本 | **MISSING**：Unity `SystemInfo` 不暴露驱动版本（`graphicsDeviceVersion` 只给 API 级别）。宿主侧 WMI 值为 32.0.31019.2002，**只写在本文档、不写进 JSON**（跨进程来源不能冒充播放器内取数） | `driver=null` + `driverMissing` |
| 场景 | **SCN_Game**（经生产路径进入，见 2.3） | `scene.*` |
| 采样 | **warmup 120 帧 + 样本 600 帧**（实际处理 601 帧收敛，`sampleFramesObserved=601`） | `warmup` / `samples` / `sampleFramesObserved` |

显示侧事实（影响解读，不能省）：**显示器 2880×1800 @ 90Hz**，窗口 1920×1080 居中；`vSyncCount=1` 所以帧间隔上限 = 90fps。

### 2.2 探针实现（`UnityProject/Assets/Game/Bootstrap/FrameBaselineProbe.cs`，新增 1 文件）

- **惰性启用**：`RuntimeInitializeOnLoadMethod(AfterSceneLoad)` 里判断 `-framebaseline`，无参数直接 `return`（编辑器同样直接 return）。实测无参数冒烟跑 `Player.log` 里 `FrameBaseline` 出现 **0 次**。
- **到场景**：等主菜单真的在场上（`FindObjectOfType<MainMenuView>`）→ 等 30 帧 → 调 `GameBootstrap.StartNewGame()`（与"开始"按钮同一个 public 入口）→ 等 `SCN_Game` 激活且 `SceneLoader.IsLoading=false`。**不直载场景**，否则测的不是玩家那条装配链。
- **采**：每帧 `FrameTimingManager.CaptureFrameTimings()` + `GetLatestTimings()`（四项 ms）；每帧读五个 `ProfilerRecorder.LastValue`（`SetPass Calls Count` / `Batches Count` / `Triangles Count` / `Vertices Count` / `GC Allocated In Frame`）。统计 = **p50/p95/p99/mean/max**（+min/samples），分位用 nearest-rank `sorted[clamp((int)(f*n+0.5),0,n-1)]`（与既有探针同式）。
- **不填 0 的规则**：帧计时 ≤0 计 `frameTiming.invalid*`；渲染计数器读到 0（recorder 刚启动那 1 帧）计 `zeroed*Samples` 并剔除（实测每轮各 1 条）；`gcAllocated` 不剔 0（0 字节是真实值）。整项 0 条有效样本 → `null` + `MISSING`。
- **写档**：`Build/Windows64/ZFrame_Data` → 上溯 3 级 = 仓库根，断言 `UnityProject/` 与 `Tools/Probes/` 存在，再拼 `Tools/Probes/out/frame-baseline-<UTC>.json`；文件名白名单（字母/数字/`_`/`-`/单点扩展名）+ `Path.GetFullPath` 边界断言（同 env-probe）。写完 `Application.Quit(0)`。
- **一处设置改动（必须记账）**：`ProjectSettings.asset` 的 `enableFrameTimingStats: 0 → 1`。这是 `FrameTimingManager` 的前置开关（不开则四项恒 0/不可用），属**测量设置**、不是画质改动；已写进每份 JSON 的 `notes`。质量档、后处理、renderScale 一律未动。

### 2.3 命令（可原样重跑）

```bash
# 1) 打包（含探针；脚本自带单一实例/删锁/冒烟）
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_player.ps1
#    本次：Unity exit 0，bundleVersion=0.1.0.37，smoke PASS（20s 存活 + 启动链标记）

# 2) 两轮（同输入同参数，窗口 1920×1080）
cd C:/Users/1/Desktop/zcode/GameFramework
Build/Windows64/ZFrame.exe -framebaseline -screen-width 1920 -screen-height 1080 \
  -screen-fullscreen 0 -logFile TempLogs/framebaseline_r1.log
Build/Windows64/ZFrame.exe -framebaseline -screen-width 1920 -screen-height 1080 \
  -screen-fullscreen 0 -logFile TempLogs/framebaseline_r2.log
```

退出码：两轮均 **0**（JSON 已落盘）。启动环境纪律：先确认无 Unity/Player 进程、`UnityProject/Temp/UnityLockfile` 不存在、PS 侧清掉 `HTTP_PROXY/http_proxy` 重复大小写变量（`Start-Process` 会因大小写重复的环境字典失败）。

---

## 3. 数字

### 3.1 两轮（正式判据对）

单位 ms；`n=600` 每项。

| 指标 | 轮次 | p50 | p95 | p99 | mean | max |
|---|---|---|---|---|---|---|
| `cpuFrameTime` | R1 | 11.1082 | 11.2293 | 11.3129 | 11.1125 | 11.3129 |
| | R2 | 11.1147 | 11.2914 | 11.7177 | 11.1131 | 11.7177 |
| `cpuMainThreadFrameTime` | R1 | 2.8007 | 4.2745 | 4.9811 | 2.9862 | 4.9811 |
| | R2 | 3.0487 | 4.1378 | 6.0872 | 3.1432 | 6.0872 |
| `cpuRenderThreadFrameTime` | R1 | 0.9671 | 1.7742 | 2.4170 | 1.0465 | 2.4170 |
| | R2 | 0.8185 | 1.4045 | 2.0200 | 0.9071 | 2.0200 |
| `gpuFrameTime` | R1 | 3.4265 | 4.7952 | 5.0278 | 3.6592 | 5.0568 |
| | R2 | 3.1417 | 4.1883 | 5.6150 | 3.3462 | 5.6150 |

渲染统计（两轮几乎逐帧恒定，这是"同形"的证据）：

| 指标 | R1 | R2 |
|---|---|---|
| SetPass Calls | p50=28（p95=p99=max=28，mean=28.0000） | p50=28（同左） |
| Batches | p50=84（p99=84，mean=83.7483） | p50=84（p99=84，mean=83.7433） |
| Triangles | p50=106782（p95=106856，max=106864） | p50=106786（p95=106850，max=106860） |
| Vertices | p50=97051（p95=97199，max=97215） | p50=97059（p95=97187，max=97207） |

### 3.2 四轮离散度（额外两轮只为回答"到底抖多少"，不是挑数）

| 指标 | R1 | R2 | R3 | R4 | 离散度 (max−min)/mean |
|---|---|---|---|---|---|
| `cpuFrameTime` p50 | 11.1082 | 11.1147 | 11.1080 | 11.1110 | **0.06%** |
| `cpuMainThread` p50 | 2.8007 | 3.0487 | 3.0578 | 3.3063 | **16.6%** |
| `cpuRenderThread` p50 | 0.9671 | 0.8185 | 0.8670 | 0.8021 | **19.1%** |
| `gpuFrameTime` p50 | 3.4265 | 3.1417 | 3.2653 | 3.2405 | **8.7%** |
| `cpuMainThread` p95 | 4.2745 | 4.1378 | 4.0567 | 4.3301 | 6.5% |

四轮文件：`frame-baseline-2026-11-15T04-03-41.json` / `-04-03-57.json` / `-04-04-13.json` / `-04-04-29.json`。

### 3.3 判据结论（照简报原话，不改口径）

> 两轮 p50 差异 ≤5% 才算稳定。

- `cpuFrameTime`：0.06% → **稳**。
- 三项底层时间：8.3%–15.4% → **不稳**（超过门限）。按纪律如实记录 + 查原因，见 §4.1。
- 「同输入两次跑数字应同形」：**形状成立**（字段齐全、量级一致、SetPass/Batches 逐帧恒定、Triangles 只差 ±8），**数值不成立**（三项 p50 超门限）。

### 3.4 施工中被自己抓住的两个缺陷（一并记账）

1. **首版 JSON 有尾逗号**（严格 JSON 解析器不接受）。首轮 `frame-baseline-2026-11-15T03-55-50.json` 已从 `out/` 撤出、存到 `TempLogs/frame-baseline-2026-11-15T03-55-50.rejected-trailing-commas.json`（数字是真的，格式是错的，不留在 `out/` 里误导下游）。修复后写档统一去掉块尾逗号，现四份文件 `python json.load` 全部解析通过。
2. **`urpPackageVersion` 曾写成 `0.0.0.0`**：2022.3 的 URP 程序集 `InformationalVersion` 缺失、`FileVersion=0.0.0.0`，那不是版本号。改为 `null` + `MISSING`（口径以 `UnityProject/Packages/manifest.json=14.0.11` 为准，写在 `missing` 原因里）。

---

## 4. 解读、已知不代表性、下一步

### 4.1 为什么 p50 抖 8–15%（如实归因，不猜成结论）

1. **vsync 把帧间隔钉平，把抖动赶到线程时间里**：`cpuFrameTime` p50/p95/p99 = 11.108/11.229/11.313（差 <1%），因为 90Hz 刷新节拍决定了帧间隔；而主线程只用了 2.8–3.3ms、渲染线程 0.8–1.0ms、GPU 3.1–3.4ms，剩下的 7–8ms 是等 vsync。**等 vsync 的帧里，线程工作时间的绝对值很小（0.15–0.33ms 的差异就占 8–15%）**，操作系统调度、iGPU 频率爬坡都会直接体现在百分比上。
2. **绝对差很小**：R1→R2 三项的绝对变化是 `+0.248ms / −0.149ms / −0.285ms`。对"优化是否有效"的判断，这个量级（<0.3ms）本身就在噪声里；要用 p95 + 多轮中位数（见 3.2 的 p95 离散 6.5%，比 p50 的 16.6% 更稳）。
3. **已排除的项**：
   - **独显切换**：本机只有一个显卡适配器（WMI `Win32_VideoController` 仅 AMD Radeon 780M，无独显），不存在混合显卡切换路径。
   - **后台节流**：`runInBackground=1`，采样期无其他负载；两轮均从同一 exe、同一参数、同一场景出生点起跑。
   - **帧计时未开**：`gpuTimerFrequency=1e9`、`cpuTimerFrequency=1e11`、`invalid*=0`（600 条全有效），说明 FrameTimingManager 真的在出数，不是"全 0 被平均"。
4. **仍未定位的部分（不假装已定位）**：R1→R4 主线程 p50 单调上升（2.80→3.31）而 GPU p50 下降（3.43→3.24），与 iGPU 频率/功耗状态或 Windows 调度预热方向一致，但**没有采集到时钟/功耗证据**，所以只记为"未定位的 run-to-run 漂移"，不写成结论。

### 4.2 解读：p50 / p95 差距说明什么

- `cpuMainThread`：p50 3.05ms、p95 4.14ms、p99 6.09ms（R2）→ 典型帧 3ms，但 5% 的帧比典型帧多 1.1ms，1% 的帧多 3ms。**这是"有周期性尖峰"的形状**（GC/物理/UI 刷新一类），不是均匀变慢。做优化时先盯 p95/p99 的来源，别只优化 p50。
- `gpuFrameTime`：p50 3.14ms、p95 4.19ms → 尾帧比典型帧高 33%；1080p/URP_Medium 下 iGPU 的稳态成本约 3.4ms（≈292fps 的理论上限），离 90Hz 的 11.1ms 预算有 ~3 倍余量。
- `cpuFrameTime`：p50≈p95≈p99（11.11/11.29/11.72）→ 帧间隔被 vsync 拉平，玩家感知的"帧率"在这台机器上由刷新率决定，不由渲染成本决定。**这正是"当前基线不能用来判断还有多少性能余量"的原因**，也是下一步要补的口径（见 4.4）。

### 4.3 已知不代表性（三条硬边界）

1. **编辑器 ≠ 播放器**：本次是 **release Player**（Mono、`BuildOptions.None`、无 Profiler 附着）。编辑器 Play 模式有 Editor 循环、场景视图、Profiler 采样开销，同一场景的帧耗会明显更高；两边数字不可混用、不可互相"校对"。
2. **无玩家输入**：采样期不按键、不动鼠标、不移动相机。真实游玩有相机运动（视锥变化→可见物件数变化）、UI 刷新、拾取/交互逻辑，主线程与 draw call 都会变。**本基线是"出生点静止"的下界，不是"玩起来"的典型值**。
3. **静态场景**：SCN_Game 是垂直切片出生点（Ground/Door/Pickup/Player/Enemy/NPC + 一盏方向光），不是战斗/多目标/粒子密集场景；且只有这一台机器（780M iGPU）、一次会话、600 帧（≈6.7 秒）——**不能外推到独显机、不能外推长时段热稳态**。附带的场景非完全静态证据：Triangles 四轮 106778–106786（±8），SetPass/Batches 恒定，说明有极少量逐帧变化的几何（HUD/NPC 朝向一类）。

### 4.4 变更清单与下一步

**改动（全部可逆、未 commit）**

| 文件 | 改动 |
|---|---|
| `UnityProject/Assets/Game/Bootstrap/FrameBaselineProbe.cs`（+`.meta`） | 新增探针（惰性启用、采 4+5 项、分位统计、JSON 落 out/、自退出） |
| `UnityProject/ProjectSettings/ProjectSettings.asset` | `enableFrameTimingStats: 0 → 1`（FrameTimingManager 前置开关；已在 JSON notes 记账） |
| `Tools/Probes/README.md` | 登记一行 + 一节探针说明 |
| `Docs/Logs/TASK-A-PERF-01.md`（本文件）、`Docs/Planning/帧耗基线-2026-11-15.md` | 交付文档 |
| `Tools/version.json` / `Build/` | 打包脚本自动 +1 版本号与产物（既有流程，非手工改动） |

**下一步（本任务不做，留给排期）**

1. **GC Allocated In Frame 取不到**：release Player 无该 marker（8 组合全试）。要拿这个数只能出 **Development Build** 跑一次（口径会变，必须单独出档、单独命名），或另写一个"每帧托管堆增量"的独立探针（明确标注是代理量）。**不要**把它塞进本 JSON 的 `gcAllocatedInFrame`。
2. **余量口径**：vsync 把帧间隔钉平后，本基线只能说明"够 90Hz"，说不清余量。要测余量需要一次**明确标注的 vsync=0 / 更高刷新率**对照跑（口径变更写进 JSON `notes`），本次未做——不做是为了不把"改设置后的好数字"混进基线。
3. **稳定性**：若后续要把 5% 门限当硬判据，应改为"多轮 p50 中位数 ± 5%"或"p95 ± 5%"，并把单轮噪声（本机实测 8.7–19.1%）写进判据，否则任何优化都会"通不过验收"。

---

## 5. 补做（同日续做：cost 口径 + 包络判据）

### 5.1 为什么要补

上一轮的 `cpuFrameTime p50=11.108ms` 恰好等于 90Hz 刷新间隔——**量到的是 vsync 钳制，不是渲染成本**。只要真实成本 < 11ms，帧间隔恒为 11.1ms，这样的基线测不出任何回归。补齐两件事：① 增加"关 vsync"的成本口径；② 把"两两差 ≤5%"换成"3 轮中位数 ± 包络"。

### 5.2 探针改动（只动探针，未动玩法）

| 改动 | 内容 |
|---|---|
| 新开关 | `-framebaseline-vsync0`（`CostModeFlag`）；与 `-framebaseline` 互斥优先取 cost |
| 采样期动作 | `QualitySettings.vSyncCount = 0`、`Application.targetFrameRate = -1`；**采样一结束立刻还原原值**（日志 `vsync restored`，JSON `vsync.restoredAfterSampling=true`） |
| 新字段 | `mode` = `asShipped` / `cost`（原 `mode="player"` 废止，另留 `runtime="player"`）；`vsync.original{vSyncCount,targetFrameRate}` 与 `vsync.sampling{...}` **两侧都留痕**；`focus{atSamplingStart,lostFocusFrames}` |
| notes | 新增一句：`asShipped 用于"玩家实际体验"，cost 用于"回归对比"`；vsync 行改为两侧数值并列 |
| missing | 新增一条：播放器内无电源/性能模式/GPU 占用查询 API（宿主侧查证见规划文档 §5） |

构建：`build_player.ps1` → Unity exit 0，bundleVersion **0.1.0.38**，smoke PASS。

### 5.3 跑法

```bash
# cost ×3（判据集）
Build/Windows64/ZFrame.exe -framebaseline-vsync0 -screen-width 1920 -screen-height 1080 \
  -screen-fullscreen 0 -logFile TempLogs/framebaseline_cost1.log   # ×3（cost1/2/3）
# asShipped ×2（新构建，mode 字段才是 "asShipped"；首版四轮保留不删）
Build/Windows64/ZFrame.exe -framebaseline -screen-width 1920 -screen-height 1080 \
  -screen-fullscreen 0 -logFile TempLogs/framebaseline_ship1.log   # ×2（ship1/2）
```

五轮全部 `exit=0`，`focused=true`、`lostFocusFrames=0`。文件对应：

| 轮次 | JSON | 说明 |
|---|---|---|
| cost-1 | `frame-baseline-2026-11-15T04-10-33.json` | 采样期同跑 `\GPU Engine(*)` 快照（见 5.5） |
| cost-2 | `-04-10-49.json` | |
| cost-3 | `-04-11-01.json` | |
| asShipped-1 | `-04-11-40.json` | 新构建，`mode="asShipped"` |
| asShipped-2 | `-04-11-55.json` | 同上 |
| 首版 asShipped ×4 | `-04-03-41/57`、`-04-04-13/29.json` | `mode="player"`，**保留**作交叉核对 |

### 5.4 数字（p50 中位数 ± 包络）

| 指标 | cost 中位数 | min–max | 包络 | asShipped 中位数 | min–max | 包络 |
|---|---|---|---|---|---|---|
| 主线程 | **3.6070 ms** | 3.5753–3.6715 | **2.67%** | 2.9711 ms | 2.7982–3.1440 | 11.64% |
| 渲染线程 | **0.7520 ms** | 0.7027–0.7791 | **10.16%** | 0.9678 ms | 0.8796–1.0560 | 18.23% |
| GPU | **3.1367 ms** | 3.1196–3.1608 | **1.31%** | 3.5786 ms | 3.4757–3.6815 | 5.75% |
| 帧间隔 | **3.7672 ms** | 3.6847–3.7748 | **2.39%** | 11.1105 ms | 11.1099–11.1110 | 0.01%（vsync 节拍，非成本） |

cost 渲染统计：SetPass 28、Batches 84 恒定，Triangles 106736–106738、Vertices 96959–96963。
判据：**两次对比超过包络才算真变化**；cost 口径下 GPU 的"真变化"门限是 **±0.04ms（1.31%）**，比 asShipped 的 ±0.21ms（5.75%）严 5 倍。

### 5.5 抖动三查（GPU p50 包络 1.31%，<10% 未触发，仍留证）

| 项 | 结果 |
|---|---|
| iGPU 后台占用 | 采样期 GPU 引擎计数器上同时有 player(pid 43968)、**dwm**(pid 1184, 3d high-priority)、**WorkBuddy**(pid 26948, 3d)、System(pid 4, copy)；静置复测 WorkBuddy 单进程 ≈7.1%。dwm 是合成器常驻，WorkBuddy 是本代理宿主 UI——记录不掩盖，也未去关它 |
| 插电 / 性能模式 | **插电**（`Win32_Battery.BatteryStatus=2`，95%）；方案 = **平衡**（GUID `381b4222-…`），非高性能 |
| 窗口焦点 | 5 轮全部 `focused=True`、`lostFocusFrames=0`（`runInBackground=1`） |

### 5.6 这一轮抓到的新事实（写进规划文档）

1. **cost 帧间隔 p95≈11.14ms 是 DWM 合成节拍**：`vSyncCount=0` 去掉的是 Unity 的等待，窗口模式下桌面合成仍按 90Hz 挡回约 5% 的帧——**cost 口径只用 p50 做回归，p95 不作尾帧读**。
2. **两套口径不可跨比**：同一指标 asShipped vs cost 差 12–22%（主线程 +21%、渲染线程 −22%、GPU −12%），因为等待被计入/排除的位置不同。
3. **cost 的稳定性明显更好**（GPU 1.31% vs 5.75%；主线程 2.67% vs 11.64%）——回归对比应当只用 cost。

---

## 验收结论

- **验收**：验收子代理，2026-11-15，同一台机器；**不采信施工数字**，逐份读 JSON + 自跑 5 轮 cost。
- **总判定**：**有条件通过（可闭环，但"1.31% 包络"不能当硬门限）**。基线档本身成立、可重跑、可复核；施工对包络的解释属于过度解读，见 §2。
- **纪律**：未改代码、未 commit（只新增本次复跑的 5 份 JSON 与 `TempLogs/verify_*.log`）；结束确认无 `ZFrame.exe`/`Unity.exe` 残留、无 `UnityLockfile`。

### 1. 读数一致性 —— 通过

| 项 | 施工声称 | JSON 三轮实测（cost） | 复算中位数 | 偏差 |
|---|---|---|---|---|
| 主线程 p50 | 3.6070 ms（2.67%） | 3.6070 / 3.5753 / 3.6715 | 3.6070 | **0.000%** |
| 渲染线程 p50 | 0.7520 ms（10.16%） | 0.7520 / 0.7027 / 0.7791 | 0.7520 | **0.000%** |
| GPU p50 | 3.1367 ms（1.31%） | 3.1367 / 3.1196 / 3.1608 | 3.1367 | **0.000%** |
| 帧间隔（asShipped） | 11.1105 ms | 11.1110 / 11.1099 | 11.1105 | **0.000%** |

包络复算（(max−min)/median）与文档一致：2.39% / 2.67% / 10.16% / 1.31%；asShipped 侧 11.64% / 18.23% / 5.75% / 0.01% 亦一致。四份 cost/asShipped JSON 均 `python json.load` 通过（无尾逗号）。**抄录误差 0，远优于 ±0.5% 门限。**

### 2. 独立复跑 —— 量级通过，单轮**不保证**落在施工包络内

`Build/Windows64/ZFrame.exe -framebaseline-vsync0 -screen-width 1920 -screen-height 1080 -screen-fullscreen 0`，构建 0.1.0.38，全部 `exit=0`、`status=ok`。

| # | 文件（UTC） | 主线程 | 渲染线程 | GPU | 帧间隔 | 落包络（GPU） |
|---|---|---|---|---|---|---|
| A（判据对） | 04-15-41 | 3.3681 | 0.7104 | **3.0681** | 3.4343 | ✗ −2.2%（低于下界 3.0956） |
| B（判据对） | 04-15-55 | 3.5761 | 0.7544 | **3.1236** | 3.6635 | ✓ |
| C（带外挂 GPU 计数器轮询，仅作解释） | 04-16-29 | 3.8068 | 0.8586 | **3.3690** | 3.9811 | ✗ +7.4% |
| D（复核） | 04-17-02 | 3.5780 | 0.7350 | **3.1242** | 3.6915 | ✓ |
| E（复核） | 04-17-12 | 3.4956 | 0.7420 | **3.1088** | 3.5992 | ✓ |

- **GPU 是否落在 3.10~3.18 区间**：5 轮中 **3 轮在**（3.1236 / 3.1242 / 3.1088），1 轮略低于（3.0681，−2.2%），1 轮高于（3.3690，+7.4%）。五轮中位数 **3.1236**（相对施工 3.1367 −0.42%），整体量级吻合。
- **跨轮包络（本次实测，5 轮）**：GPU **9.63%**、主线程 **12.27%**、渲染线程 **19.97%**、帧间隔 14.93% —— 是施工包络（1.31% / 2.67% / 10.16% / 2.39%）的 **3–7 倍**。
- **结论**：施工的 1.31% 只是"同一 30 秒内三连跑"的离散度，不是跨会话可复现的门限。§5.4「GPU 真变化门限 ±0.04ms」**不予采信**；作为回归判据必须改成"≥5 轮、跨 ≥2 会话的中位数 ± 实测跨轮包络（GPU ≈10%）"。
- **环境（未变的，逐项列）**：插电 `BatteryStatus=2` / 95%；电源方案=平衡（`381b4222-…`）；无独显（仅 780M）；后台 GPU 占用 WorkBuddy(pid 26948) 5.4–7.5%、dwm(pid 1184) 瞬时 8%、System(pid 4, copy) 3.2%；跑前无 ZFrame/Unity 进程；5 轮全部 `focused=true`、`lostFocusFrames=0`、1920×1080、Quality=1(Medium)、采样期 vSync=0。
- **不掩盖的部分**：A 轮偏低、C 轮偏高**未定位**。A 轮耗时 25s（B/D/E 均 10s）、GPU 计时无效帧 190（B 为 80），与冷启/PSO 编译方向一致但**未采到证据**；C 轮与我并发跑 GPU 计数器轮询重合，**不能排除自扰动**，故 C 不计入判据。按纪律只记为"跨轮漂移，未定位"。

### 3. 测法审计（读码 + 运行时冒烟）

| 检查 | 结论 | 证据 |
|---|---|---|
| 无 `-framebaseline` 零开销零日志 | **通过** | `FrameBaselineProbe.cs:112-130`：`Application.isEditor` → 无开关 `return`，在其前不建 GameObject、不打日志。运行时冒烟（无开关，12s 与 28s 各一次被强杀）`Player.log` 中 `FrameBaseline` 出现 **0 次**，`out/` 无新档 |
| `-framebaseline-vsync0` 只改 `vSyncCount/targetFrameRate` 且采样后恢复 | **通过** | `BeginSampling:300-311` 仅这两项；`Finish:515-521` 在写档前恢复，日志 `vsync restored: vSyncCount=1 targetFrameRate=-1`，JSON `vsync.restoredAfterSampling=true` |
| 是否偷降画质 | **否** | JSON `qualityLevel`：index=1/Medium、antiAliasing=2、renderScale=1、恢复后 vSyncCount=1；`QualitySettings.asset` Medium 段 `antiAliasing: 2 / vSyncCount: 1`、`m_CurrentQuality: 1`；`URP_Medium.asset` `m_MSAA: 2 / m_RenderScale: 1`；`git diff` 仅 3 行（bundleVersion、enableFrameTimingStats、AndroidBundleVersionCode 由打包脚本递增），`Assets/Settings`、场景、相机后处理**零改动** |

两处**记录性瑕疵**（不影响结论，建议后续修）：

1. `HasCommandLineFlag` 调 `Environment.GetCommandLineArgs()`（一次性 `string[]` 分配）发生在早退之前 —— "零开销"严格说是"零逐帧开销 + 一次启动分配"。
2. asShipped 下 `vsync.restoredAfterSampling` 写的是 `CostMode`（=false），语义应为"无需恢复"，下游容易读成"恢复失败"。
3. `enableFrameTimingStats: 0→1` 是**工程级**开关（对今后所有 Player 构建生效，含发布版），不只是探针内部开关；开销小但应记账，建议只在 Development 构建开启或明确接受。

### 4. JSON 契约 —— 通过（1 处口径不清）

- **文件名白名单**：`frame-baseline-<UTC>.json`，`IsWhitelistedFileName` 限字母/数字/`_`/`-`/单点，天然排 `..`；重名退 `-r2`。
- **out 目录边界**：`Path.GetFullPath(outRoot+fileName)` + `StartsWith(outRoot)` + 文件名等值断言，越界抛异常（exit=3）。
- **MISSING 只出现在取不到处**：`gcAllocatedInFrame` 六项全 `null`+`samples=0`（recorder 8 组合全试，`missing[]` 逐条留证）、`driver=null`、`urpPackageVersion=null`（`FileVersion=0.0.0.0` 不冒充）。**未发现任何字段拿 0 冒充缺失值**；`zeroed*Samples`/`invalid*` 是剔除/无效计数，属实。
- **口径不清（建议补）**：cost 轮 `invalidGpuFrameTime`=80–190 而 `gpuFrameTime.samples`=600 —— `samples` 是"已入样的有效条数"（到 600 截断），无效条数只在缓冲未满时累计，JSON 里**读不出有效比例**（本次约 76–88% 的 timing 有 GPU 值）。建议加 `timingSamplesSeen` 或 `validRatio`，否则 cost 的 GPU p50 是"有 GPU 计时的那部分帧"的 p50，读者无法自查。

### 5. 可闭环与否 + 受限

- **可闭环**：清单第 1 条「跑通工程 + 记帧耗基线」成立 —— 构建可跑（0.1.0.38，exit=0）、探针可重跑、口径全落档、数值逐位可复核、无残留进程。
- **受限（本次实测确认，不是引用）**：
  1. **iGPU 跨轮抖动 9.6–20%**：单轮基线可存档，但不能作 ±1% 级回归门限；
  2. **无玩家输入**：出生点静止，非"玩起来"典型值；
  3. **静态场景**：五轮 SetPass=28、Batches=84 恒定、Triangles 106736–106738，不能外推到战斗/粒子/独显。
- **建议下一步**：① 用 10 轮跨 ≥3 会话重定 cost 包络；② 补 `validRatio`；③ `enableFrameTimingStats` 收进 Development 构建；④ 出 Development Build 单独解 `GC Allocated In Frame`（另立口径，不回填本 JSON）。
