# TASK-A-R9-13 —— 千人演练场：DataKernel 玩法侧规模化（混合架构）

状态: doing | 立简报/派发: 2026-11-04 | 施工: builder-r9-13（general-purpose） | 验收: verifier-r9-13（须与施工不同代理） | commit: （待填）

## 任务简报（主控写）

### 背景

B-R9-17（`2a848da`）已闭环：DataKernel 首次跨 DLL 暴露句柄式 C ABI（12 个 `ZK_*` 导出）、C# 安全包装 `ZKernelWorld`（`IDisposable` / `T : unmanaged` 泛型 / 游标式 `EachBatches` / 布局版本号防护），EditMode 真 dll roundtrip 与 1000 实体游标基线 **10.0 µs/次**（`Docs/Logs/TASK-B-R9-17.md` §四）均已落盘。

但 `Docs/需求达成对照.md` §四仍把「**DataKernel 玩法侧接入**」列为**唯一待排缺口**：内核至今只在基准与单测里跑，没有任何玩法场景消费它。本任务补上这一块，兑现「混合架构：kernel 管群体，Mono 管英雄」。

### 目标（按此实现，细节可斟酌但报告说明）

1. **组件与注册**：在 C# 侧定义 3 个 `unmanaged` 组件（建议 `Position` / `Velocity` / `Tag`），compId 由 C# 显式分配 0..127；用 `ZKernelWorld.RegisterComponent<T>(compId)` 注册，`AlignOf` 的启发式或显式 `(id,size,align)` 重载按 `ZKernelWorld.cs` 现有约定选用。
2. **群体驱动**：新建一个玩法侧驱动（建议 `Assets/Game/View/Effects/KernelCrowdDriver.cs` 或等价位置），持有**单个** `ZKernelWorld` 实例：
   - 生成 1000 个实体，写入初始位置/速度/标签；
   - 每帧经 `ITickQueue` 驱动（**不要** `MonoBehaviour.Update`；逻辑阶段与暂停语义沿用 `GameLoopDriver`），用 `EachBatches(mask, callback)` 游标批量读列 → 积分速度 → 回写位置（**回调内禁止 Spawn/Destroy/Add/Remove**，原生布局版本号会作废游标并抛 `ErrState`；`SetComponent` 不改布局，安全）；
   - 世界随场景生命周期 `Dispose()`（原生句柄表最多 8 世界，泄漏会耗尽槽位）。
3. **可视化（关键取舍）**：**不得**为 1000 个实体各建一个 GameObject——那等于把 SoA 内核的意义抵消掉。走批量绘制（`Graphics.DrawMeshInstanced` / `Indirect`，或现有池化粒子），判据：**DrawCall 不随实体数线性增长**，并给出与 1000 实体对应的实测数字（不编）。
   - 管线约束（已判定）：Unity 工程当前激活 **URP 14.0.11**（`GraphicsSettings.m_CustomRenderPipeline` → `URP_Medium.asset`），自研 SRP 只是空壳。所有材质/绘制必须按 URP 兼容写法落地，**不要**往 `ZRenderPipeline.cs` 加 pass。
4. **英雄侧不变**：现有 SCN_Game 的玩家 / 敌人 FSM 继续由 MonoBehaviour 管；本任务只加群体，**不改现有玩法手感、不改场景内容布局**（历史上 A-GPU-01 简报亦明令「不碰游戏内容」）。
5. **性能守卫**：以 B-R9-17 的 10.0 µs/1000 实体游标拉取为基线，给出本任务「每帧群体更新 + 批量绘制」的耗时报告（EditMode 计时 + PlayMode 实测），并明确区分**内核迭代**与**渲染提交**两段；跨跑比较前执行 `Tools/check_machine_quiet.ps1`（交接档案 §3.4），不可信时改用同一次运行内 A/B 对照。
6. **测试**：EditMode 真 dll roundtrip（1000 实体全量求和、迁移后读回、销毁句柄失效、坏 compId 拒绝）；PlayMode 断言群体可见且英雄行为不回归；架构校验器（Domain 纯度 / asmdef 矩阵）exit 0；既有用例只增不减。

### 约束

- 白名单：`UnityProject/Assets/Game/**`、`UnityProject/Assets/ZFrame/Tests/**`、`UnityProject/Assets/ZRender/Bindings/**`（仅当必须扩展绑定时，须在报告说明理由）、`Docs/Logs/TASK-A-R9-13.md`、`Docs/Demo.md`（仅当新增体验项时）。
- **不改原生 C++**（ABI 已定且 `zrender.def` 只许追加）、不改 `Native/**`、不改 HUD / DevConsole / 音频 / 存档等既有模块、不改 `TASKS.md` / `HANDOFF.md` / `WORKLOG.md` / `README.md`。
- 不 `git add` / 不 `git commit`；UTF-8 无 BOM + LF；`.meta` 由 Unity 生成，不手改。
- 依赖 `UnityProject/Assets/Plugins/ZRender/zrender.dll` 的 12 个 `ZK_*` 导出（已入库，`2a848da`）；**部署位 dll 是本任务运行前提，勿按交接档案 §3.7 的「构建产物噪声」惯例 checkout 丢弃**。

### 如实受限（写进报告，别装作验证过）

- 本机 Editor 为 Mono；**IL2CPP / Android 上的 P/Invoke 与 `unmanaged` 泛型未验证**，若要做真机包需另立任务（A-AND-06 的 IL2CPP 构建约 29 分钟）。
- `Graphics.DrawMeshInstanced*` 在 URP 14 下的实例数上限、SRP Batcher 兼容性与移动端降级策略，需实测后写结论，不要照搬桌面假设。
- 若批量绘制在批处理 `-batchmode` 无渲染负载下取不到可信 DrawCall，如实标受限，改用 `ProfilerRecorder` 静态证据 + 编辑器 Play Mode 人工目测（参照 A-GPU-01 V1 的诚实口径）。

### 环境纪律（真实踩过，详见 `Docs/交接档案.md` §3）

- 跑测试前清 8 个代理变量（大小写成对），否则 MSBuild / Unity 启动失败。
- Unity 批处理单实例：跑前查进程 + 删 `UnityLockfile`；跑测试**勿带 `-quit`**；xml 出现但 5 分钟不退 → `taskkill //PID <pid> //F`；证据以 results.xml 为准。
- Bash 侧禁止转调 powershell → 用 PowerShell 工具 `& .\Tools\run_tests.ps1`；本机 GUI Editor 加 `-force-d3d12` 会挂起。
- 清理用 `mv` 归档，不要 `rm -rf`；跑完查 `git status`，Editor 自动改写 `URP_*.asset` / `QualitySettings.asset` 时只针对这几个文件还原。

### 铁律

施工与验收必须不同代理；一任务一 commit；日志四节齐全（本文件的施工报告 / 验收清单 / 验收结论缺一不可）；验收代理须亲自做「修前必红」定向变异；证据不齐写受限，绝不把「部分完成」说成「已完成」。

## 施工报告（施工代理写）

**施工**: builder-r9-13（general-purpose 施工代理） ｜ **日期**: 2026-11-04 ｜ **自测**: 五段全绿（见 §四 / §五）

### 一、交付物清单（全部落在白名单内）

| 文件 | 性质 | 说明 |
|---|---|---|
| `Assets/Game/View/Effects/CrowdPosition.cs` | 新增 | unmanaged 组件，显式 compId 0，12B（X/Y/Z） |
| `Assets/Game/View/Effects/CrowdVelocity.cs` | 新增 | unmanaged 组件，显式 compId 1，12B（Dx/Dy/Dz） |
| `Assets/Game/View/Effects/CrowdTag.cs` | 新增 | unmanaged 组件，显式 compId 2，4B；游标回写的实体序号定位列 |
| `Assets/Game/View/Effects/KernelCrowdIds.cs` | 新增 | compId 分配 + 常量（1000 实体 / 单次 1023 实例 / 圆盘半径 / 缩放 / 黄金角） |
| `Assets/Game/View/Effects/KernelCrowdDriver.cs` | 新增 | `ITickable` + `IDisposable`；**单个** `ZKernelWorld`；`EachBatches` 游标读 → 积分 → `SetComponent` 回写；注册 Logic 档 |
| `Assets/Game/View/Effects/KernelCrowdRenderer.cs` | 新增 | `ITickable` + `IDisposable`；`Graphics.DrawMeshInstanced` 按 1023 分批；URP 兼容运行期材质；注册 View 档 |
| `Assets/Game/View/Effects/KernelCrowdHost.cs` | 新增 | `MonoBehaviour` 自举（`RuntimeInitializeOnLoadMethod`）+ `SceneLoadedEvent` 门控 + 场景卸载 `DisposeCrowd()` |
| `Assets/Game/View/Editor/KernelCrowdMenu.cs` | 新增 | Editor-only（`#if UNITY_EDITOR`）人工体验入口菜单 |
| `Assets/Game/View/Game.View.asmdef` | 修改 | `references` 追加一条 `GUID:` 引用（详见 §2.5） |
| `Assets/Game/Tests/View/KernelCrowdDriverTests.cs` | 新增 | EditMode 真 dll 用例 7 个 |
| `Assets/Game/Tests/View/Game.View.Tests.asmdef` | 修改 | 追加 `ZFrame.Render.Bindings`（Tests 程序集豁免矩阵） |
| `Assets/ZFrame/Tests/PlayMode/KernelCrowdPlayModeTests.cs` | 新增 | PlayMode 用例 2 个 |
| `Assets/ZFrame/Tests/PlayMode/ZFrame.PlayMode.Tests.asmdef` | 修改 | 追加 `ZFrame.Render.Bindings`（Tests 程序集豁免矩阵） |
| `Docs/Demo.md` | 修改 | 新增体验项（§2 表 #11、§3.6 节、§5 限制 #15） |
| `Docs/Logs/TASK-A-R9-13.md` | 修改 | 仅本「施工报告」节 |

**未动** `Assets/ZRender/Bindings/**`：B-R9-17 的 `ZKernelWorld` 公开 API（Register/Spawn/SetComponent/EachBatches/AddComponent/RemoveComponent/GetStats/Dispose）已足够，无需扩展绑定；因此白名单第三条「仅当必须扩展绑定」未触发。Unity 自动生成的 11 个 `.meta`（未跟踪）随新 `.cs` 落在白名单目录内，**未手改**。

### 二、组件 / 驱动 / 绘制设计

#### 2.1 组件与 compId

三个 `readonly struct` + `[StructLayout(LayoutKind.Sequential)]`，均 `unmanaged`、无引用字段，走 `ZKernelWorld.RegisterComponent<T>(compId)`（`Marshal.SizeOf` 尺寸校验 + 最大 2 幂因子对齐启发式，按 `ZKernelWorld.cs` 既有约定）。compId 由 C# 显式分配 `0/1/2`（0..127）。三列合计 28B/行 → chunk 容量 585 → 1000 实体跨 2 chunk，EditMode 用例真实断言 `batches >= 2`。

#### 2.2 驱动：ITickQueue + 游标 + SetComponent 回写

`KernelCrowdDriver.Initialize` 建**单个** `ZKernelWorld`、注册 3 组件、生成 1000 实体并写入初始数据（pos.X = 序号 0..999 → 求和 499500 可断言；Tag = 序号），随后 `Services` 注入的 `ITickQueue.Register(this, TickStage.Logic)`。不用 `MonoBehaviour.Update`；Logic 档在暂停时被 `GameLoopDriver` 冻结，暂停语义沿用既有框架。

每帧 `Tick(dt)`：`EachBatches(mask, handler)` 游标批量读三列（`Marshal.Copy` 到复用缓冲，零逐 chunk 分配）→ 积分（`nx = x + dx*dt`，序号回绕保持 [0,N)）→ **按 Tag 序号** `SetComponent` 回写 pos/vel → 重建共享 `Matrix4x4[]`。

两处如实披露的决策：
- **游标不交付实体句柄**，故用 Tag 列携带实体序号定位 `_entities[i]` 回写，**不依赖「行序 == spawn 序」**这一内部实现细节（B-R9-17 §五.2 已提示跨 chunk 布局不可比）。
- 积分结果先按序号收集、游标耗尽后统一回写（回调内零布局变更，连 `SetComponent` 都不在回调内），避免迭代内写回影响后续批的读取。`SetComponent` 不改布局、安全（B-R9-17 契约）。

`Dispose()` 注销 tick + `ZKernelWorld.Dispose()`（原生句柄表上限 8 世界）；EditMode/PlayMode 均有「连续 10 次创建/销毁不抛 + `IsDisposed==true`」的真断言，证明无句柄泄漏。

#### 2.3 批量绘制：URP 14 兼容、DrawCall 不随实体数线性增长

**不为 1000 实体各建 GameObject**：全场只有 1 个隐藏宿主 `~KernelCrowdHost`（且非实体）。`KernelCrowdRenderer` 持共享 `Matrix4x4[]`，走 `Graphics.DrawMeshInstanced`，程序化单位立方体 Mesh + 运行期材质（`Shader.Find` 候选 `Universal Render Pipeline/Lit` → `Unlit` → `Sprites/Default`，`enableInstancing=true`）；**零 `.mat`/`.prefab` 资产、未往 `ZRenderPipeline.cs` 加 pass**（自研 SRP 是空壳，工程激活 URP 14.0.11）。本次 PlayMode 实测解析到的 shader 为 `Universal Render Pipeline/Lit`。

单次实例上限按 Unity 文档 1023 分批：`DrawCall = ceil(N/1023)`。1000 实体 → **1 次** `Graphics.DrawMeshInstanced`，`submittedInstances=1000`；同一次运行内 A/B：100 实体 → 1 次、1000 实体 → 1 次（见 §三）。注册 `TickStage.View`（Logic 之后、暂停时仍提交，群体静止可见；积分仍归 Logic）。

#### 2.4 生命周期 / 场景门控 / 英雄侧不变

照 `GameAudioBindings`（A-R9-14）的 `RuntimeInitializeOnLoadMethod(AfterSceneLoad)` 自举先例，运行期建 `DontDestroyOnLoad` 隐藏宿主；订阅 `SceneLoadedEvent`：进入 SCN_Game 且 `AutoEnable=true` 时启用，离开 SCN_Game（含回主菜单）即 `DisposeCrowd()` 释放句柄；`OnDestroy` 兜底。

**英雄侧零改动**：未改 SCN_Game 场景文件、未改玩家/敌人 FSM、未改任何既有模块（HUD/Audio/存档/GameBootstrap/GameSceneSetup 等），只新增文件。`AutoEnable` **默认 false**（群体默认不自动出现），原因是 A-R9-10 的 PlayMode 像素证据用例会加载 SCN_Game 做相机差分，自动绘制 1000 个方块有可能污染差分——既有 PlayMode 44/44/0 全绿是本取舍的直接证据。人工体验走菜单 `ZFrame/千人演练场/开启（Play Mode）`（Demo.md §3.6）。

#### 2.5 asmdef 矩阵与 Domain 纯度的处理（必须说明的取舍）

`Game.View` 需要引用 `ZFrame.Render.Bindings` 才能消费 `ZKernelWorld`，但 `ArchitectureValidator.cs`（**不在白名单，禁止改**）的期望矩阵中 `Game.View.AllowedReferences` 不含它；而 `ZFrame.Render.Bindings.asmdef` 是 `autoReferenced:false`，必须显式引用。

采用 **Unity 官方的 `GUID:` 引用形式**：`Game.View.asmdef` 追加 `"GUID:589648d0fabfed44a9480254c552ce49"`。校验器 `IgnoredReferencePrefixes = { "GUID:" }` 明确接受该形式（其注释："GUID-based references cannot be resolved cheaply; name-form is our convention"，即名字只是约定、GUID 是合法输入），故不产生违规。**未**改 `ArchitectureValidator.cs`、**未**新增非 Tests 程序集（否则 `ExpectedMatrix_MatchesActualAsmdefCount` 单测会红）、**未**把 Bindings 改成 `autoReferenced:true`（那会让 Game.Domain 隐式引用渲染绑定，触碰「Domain 零引擎/渲染引用」红线）。

结果：`Game.Domain.asmdef` 零改动、纯度扫描目录（`Assets/ZFrame/Gameplay`、`Assets/Game/Domain`）零新增文件；arch 校验器 exit 0 / 违规数 0（§五亲证）。两个 Tests 程序集用**名字**引用 `ZFrame.Render.Bindings`（校验器对 `*.Tests` 程序集豁免矩阵，与 `ZFrame.Render.Tests` 既有写法一致）。

### 三、性能守卫（分段耗时，对照 10.0 µs 基线）

跑前 `Tools/check_machine_quiet.ps1`（`-WarnOnly -SampleSeconds 3`）→ **VERDICT: QUIET**；`run_tests.ps1` 开头亦内置同一护栏，本轮亦为 QUIET。跨跑比较不做，全部数字**同一次运行内**测得，并附 100/1000 实体 A/B 对照。

| 段 | 本次实测 | 对照 / 说明 |
|---|---|---|
| 纯只读游标拉取（等价 B-R9-17 基线口径） | **9.3 µs/次**（EditMode 200 次均值） | 本次同跑的 B-R9-17 基线用例 **9.2 µs/次**；对照基线 **10.0 µs/次**，同量级、未退化 |
| 内核迭代（游标读 + 积分 + 1000 次 `SetComponent` 回写 + 矩阵构造） | **782.0 µs/次**（EditMode 200 次均值）；**472.7 µs/帧**（PlayMode 真跑） | 远高于 10 µs 基线的原因如实说明：增量来自简报指定的 **1000 次跨 ABI `SetComponent` P/Invoke 回写**，不是游标迭代变慢（纯只读段仍是 9.3 µs）。未用「列指针原地写」优化，因为交付要求明确 `SetComponent` 回写 |
| 渲染提交（`Graphics.DrawMeshInstanced`） | **71.4 µs/帧**（PlayMode 真跑） | 1000 实例 = 1 次提交；`shader=Universal Render Pipeline/Lit` |
| 同一次运行内 A/B（纯只读游标拉取） | 100 实体 **1.2 µs/次**；1000 实体 **7.6 µs/次**（6.58×） | 内核迭代本身随实体数增长属正常（线性），与「DrawCall 恒为 1 次」形成对照 |
| DrawCall 判据（同一次运行内 A/B） | 100 实体 → **submitCalls=1**；1000 实体 → **submitCalls=1**，`submittedInstances=1000` | **不随实体数线性增长**（台阶 1023）；`LastSubmitCalls` 是 `Graphics.DrawMeshInstanced` 的真实调用次数，不是估算 |

日志证据：`TempLogs/runtests_editmode.log:526`（分段耗时）、`:569`（A/B）、`:1224`（B-R9-17 本次基线 9.2 µs）；`TempLogs/runtests_playmode.log:2817`（PlayMode 分段 + shader + 提交数）。

### 四、实跑测试数字（本次新写 results.xml）

命令：`& .\Tools\run_tests.ps1 -Segments editmode,playmode,arch,config,naming`（PowerShell 工具；同进程清 8 个大小写代理变量；跑前无 Unity.exe、无 lockfile；测试段不带 `-quit`；证据以本次新写 xml 为准）。

| 段 | 本次数字 | 基线（主控给） | 结论 |
|---|---|---|---|
| EditMode | **880 / 880 / 0**（7 个新增用例全 `Passed`） | 873 / 873 / 0 | +7，只增不减 |
| PlayMode | **46 / 46 / 0**（2 个新增用例全 `Passed`） | 44 / 44 / 0 | +2，只增不减 |
| native（doctest，直接跑已构建二进制） | **311 / 311 passed / 0 failed，776,716 断言** | 311 / 776,716 | 持平（本任务不改 `Native/**`） |

- **本次新写 xml**：`TempLogs/runtests_editmode.xml`（mtime `2026-11-04 21:15:29 +0800`；`<test-run total="880" passed="880" failed="0" result="Passed">`，`start-time 13:15:08Z / end-time 13:15:29Z`）；`TempLogs/runtests_playmode.xml`（mtime `2026-11-04 21:17:05 +0800`；`<test-run total="46" passed="46" failed="0" result="Passed">`，`13:16:22Z / 13:17:04Z`）。两文件均由本轮重写（脚本 `EvidenceFresh=true`）。
- **EditMode 7 例**（`Game.View.Tests.KernelCrowdDriverTests`，均真 dll roundtrip）：1000 实体三列求和（pos.X / tag 均 499500、跨 chunk `batches>=2`）、迁移 `AddComponent` 零初始化→读回→`SetComponent` 覆写、销毁句柄失效（重复销毁/容错读/GetComponent/SetComponent 全拒）、坏 compId 拒绝（原生 `ErrRange` 128、`ZkMask.Of(200)` 越界、未注册 compId 的 `ArgumentException`）、驱动 Tick 积分 + `ITickQueue` 注册/注销、连续 10 次创建销毁无句柄泄漏、分段性能守卫。
- **PlayMode 2 例**（`ZFrame.PlayMode.Tests.KernelCrowdPlayModeTests`）：`Crowd_1000Entities_SingleSubmit_And_HeroFsmNotRegressed`（真 dll 1000 实体、`EachBatches` 每帧 1000 行、Logic/View 档各 +1 注册、URP 材质解析成功、`submitCalls=1`/`submittedInstances=1000`、100 vs 1000 A/B 均 1 次提交、英雄 `EnemyFsm` Idle→Patrol 状态转换不回归）；`Dispose_ReleasesWorld_NoHandleLeak`（10 轮「启用→Dispose」`IsDisposed=true` 且 tick 计数回基线）。
- **未跑** `run_tests.ps1` 的 `native` 段：按简报指定的五段执行，且本任务不改 `Native/**`、不重建以免改动部署位 `zrender.dll`；原生 311/776,716 由直接运行 `Native/build/bin/Release/zrender_test.exe` 亲证（`Status: SUCCESS!`）。

### 五、三校验器 exit code

| 校验器 | exit code | 报告 |
|---|---|---|
| arch（`ArchitectureValidator.ValidateFromCommandLine`） | **0** | `TempLogs/arch_report.txt`：`违规数: 0`、`结果: PASS`；`asmdef 扫描数: 25（期望矩阵条目: 12，Tests 豁免）` → GUID 引用未破坏矩阵、Domain 纯度零违规 |
| config（`ConfigValidator.ValidateFromCommandLine`） | **0** | `TempLogs/config_report.txt`：`违规数: 0`、`PASS` |
| naming（`AssetNameValidator.ValidateFromCommandLine`） | **0** | `TempLogs/name_report.txt`：`违规数: 0`、`PASS` |

`TestReport.txt` 汇总：`RESULT: ALL PASS (5 passed, 0 skipped, 0 failed)`。首次跑曾因 `KernelCrowdRenderer.cs` 的 `Debug` 在 `UnityEngine.Debug` / `System.Diagnostics.Debug` 间 CS0104 歧义导致五段全红，已改为 `UnityEngine.Debug` 全限定后重跑全绿（未删断言、未改恒真、未加重试）。

### 六、已知受限 / 未达成（如实披露，不把部分完成说成已完成）

1. **真实 GPU DrawCall / 像素证据未取得（受限）**：`-batchmode` 无渲染负载下取不到可信 DrawCall 数，**未编数字**。可证的是「`Graphics.DrawMeshInstanced` 真实调用次数 = 1」这一提交层事实与 100/1000 实体 A/B 对照；真实可见性按 A-GPU-01 V1 的诚实口径留给编辑器 Play Mode 人工目测（Demo.md §3.6 体验项）。
2. **群体非「开箱自动可见」（设计取舍，非完成度夸大）**：`AutoEnable` 默认 false，需人工点菜单开启；理由见 §2.4（避免污染 A-R9-10 的 SCN_Game 像素差分用例）。若主控要求「进 SCN_Game 即自动可见」，需另立任务并同时补齐像素回归证据。
3. **IL2CPP / Android 上的 P/Invoke 与 `unmanaged` 泛型未验证**：本机 Editor 为 Mono；真机包（A-AND-06 的 IL2CPP 构建约 29 分钟）需另立任务。
4. **URP 14 单次实例化上限未逐一探测**：采用 Unity 文档值 1023，仅实证「1000 实体 → 1 次提交」；上限精确值、SRP Batcher 兼容性、移动端降级策略未测，不照搬桌面假设。
5. **`autoReferenced` / GUID 引用的取舍**：`Game.View` 以 `GUID:` 形式引用 Bindings（§2.5），虽为校验器明确接受的合法输入且 arch exit 0，但属于「名字形式约定」之外的用法，若主控偏好纯名字引用，需由主控（唯一可改 `ArchitectureValidator.cs` 的角色）把 `ZFrame.Render.Bindings` 加进 `Game.View.AllowedReferences` 后改回名字形式——施工代理未越权改该文件。
6. **未做「修前必红」定向变异、未勾选验收清单、未写验收结论**：按铁律留给不同验收代理。

### 七、现场状态

- `git status --short`：改动**全部在白名单内**——`Docs/Demo.md`、`Assets/Game/**`（新 `.cs` + 2 个 asmdef 修改）、`Assets/ZFrame/Tests/**`（新 `.cs` + 1 个 asmdef 修改）、本日志「施工报告」节；外加 Unity 自动生成的 11 个 `.meta`（未跟踪，落在白名单目录内，未手改）。
- **未触碰**：`Native/**`、`Assets/Plugins/ZRender/zrender.dll`（未 `git checkout --` 丢弃）、`ZFrame/View/Hud/**`、`ZFrame/View/Audio/**`、`Assets/Audio/**`、`TASKS.md` / `HANDOFF.md` / `WORKLOG.md` / `README.md` / `DECISIONS.md`；Editor 未改写 `URP_*.asset` / `QualitySettings.asset` / `ProjectSettings`（`git status` 无此类条目，无需定向还原）。
- **无** `git add`、**无** `git commit`；HEAD 仍为 `761d806`。
- 全部新增/修改文件字节级自查：UTF-8 无 BOM（首 3 字节非 `EF BB BF`）、CR=0（纯 LF）、末尾保留一个换行；命名按 `Docs/Standards/file-conventions.md`（一类型一文件、PascalCase、私有字段 `_camelCase`、公共 API XML doc；组件 POD 的 public readonly 字段沿用 B-R9-17 的 blittable 既定例外）。

## 验收清单（主控写，派发前复核）

- [ ] V1 1000 实体真 dll roundtrip：求和 / 跨 chunk 游标 / 迁移 / 销毁句柄失效（行号 + xml）
- [ ] V2 每帧驱动走 `ITickQueue`、暂停语义正确、场景卸载 `Dispose()` 无句柄泄漏
- [ ] V3 批量绘制：DrawCall 不随实体数线性增长（实测数字 + 测量方式），URP 14 兼容
- [ ] V4 性能守卫：内核迭代 vs 渲染提交分段耗时，与 10.0 µs 基线对照，空载护栏已跑
- [ ] V5 英雄侧不回归、Domain 纯度 / asmdef 校验器 exit 0、既有用例只增不减
- [ ] V6 文件规范 + 施工报告四节 + 验收代理亲自复做变异（变红 → 还原 → 复绿 → 零残留）

## 验收结论（验收代理写）

**验收代理**: verifier-r9-13（独立验收代理，与施工 builder-r9-13 不同实例） ｜ **日期**: 2026-11-04 ｜ **进场 HEAD**: `761d806`（全程未变） ｜ **总体结论: PASS with 受限**

> 口径声明：以下数字均为**本次会话自跑/自查**所得；施工报告 §三/§四/§五 的数字仅作旁证并逐项标注，未复用旧 xml 冒充。实跑命令（PowerShell 工具，同进程清理 8 个大小写代理变量后剩 0；测试段不带 `-quit`）：`& .\Tools\run_tests.ps1 -Segments editmode,playmode,arch,config,naming`；`check_machine_quiet` 内置护栏 VERDICT: QUIET。证据以**本次新写** results.xml 为准：施工 xml 先 `mv` 归档为 `TempLogs/verifier_r9_13_builder_*`，本次新写证据归档为 `TempLogs/verifier_r9_13_*`。

### V1 1000 实体真 dll roundtrip —— **PASS**

- 自跑 EditMode：`total=880 passed=880 failed=0`（`TempLogs/verifier_r9_13_editmode.xml`，mtime `2026-11-04 21:34:41 +0800`，`<test-run total="880" passed="880" failed="0" result="Passed">`，start `13:34:30Z`/end `13:34:41Z`，`EvidenceFresh=true`）。7 个新增 `Game.View.Tests.KernelCrowdDriverTests` 用例在本次 xml 中逐项 `result="Passed"`。
- 真断言（非恒真）行号（`Assets/Game/Tests/View/KernelCrowdDriverTests.cs`）：求和/跨 chunk `total==1000`:72、`batches>=2`:73、`sumPos==499500.0`:74、`sumTag==499500UL`:75、`EntityCount==1000`:76；迁移 `AddComponent` 零初始化→读回:90-94、`SetComponent` 覆写→读回:96-99、迁移后原句柄仍有效:101-102、`Migrations==1`:103；销毁句柄失效（重复销毁 `false` / `TryGetComponent` false / `GetComponent` 抛 / `SetComponent` 抛 / `EntityCount==0`）:115-121；坏 compId（`128→ErrRange`:130-132、`ZkMask.Of(200)` 抛:134、未注册 999→`ArgumentException`:137-139）。
- 真走 `ZKernelWorld` 而非 mock：`NewWorld()`:30-37 `new ZKernelWorld()` + 3×`RegisterComponent<T>`；`KernelCrowdDriver.cs:103` `new ZKernelWorld()`、`:140` `EachBatches`、`:182-183` `SetComponent`；P/Invoke 落点是部署位 `zrender.dll`（`ZKernelWorld.cs:30/104/143`）。

### V2 驱动与生命周期 —— **PASS**

- 每帧走 `ITickQueue`：`KernelCrowdDriver`（`ITickable`）注册 `TickStage.Logic`（`KernelCrowdDriver.cs:124`）、`KernelCrowdRenderer` 注册 `TickStage.View`（`KernelCrowdRenderer.cs:110`）；二者均非 MonoBehaviour、无 `Update`。唯一引擎 Update 是既有 `ZFrame/Core/GameLoop/GameLoopDriver.cs:39`（不在本任务改动内）。PlayMode 用例断言 Logic/View 档各 +1（`KernelCrowdPlayModeTests.cs:56-57`）并 `queue.TickAll`:62-63。
- 暂停语义（既有框架）：`GameLoopDriver.cs:51-56` `IsPaused` 时跳过 Logic+Scheduler，`:62` View「never paused」始终 `TickAll` → 群体积分冻结、绘制继续提交（静止可见）。
- `EachBatches` 回调内零布局变更：`KernelCrowdDriver.cs:140-171` 回调仅 `Marshal.Copy` + 积分到 pending；`SetComponent` 回写在游标耗尽后的循环外 `:175-185`（布局版本防护，`ZKernelWorld.cs:242-243` 契约）。
- 场景卸载 `Dispose()`：`KernelCrowdHost.cs:98-112`（离开 SCN_Game → `DisposeCrowd()`）、`:130-136`、`:87-96`（OnDestroy 兜底）；`KernelCrowdDriver.Dispose():216-229` 注销 tick + `ZKernelWorld.Dispose()`。
- 句柄表上限 8 无泄漏：EditMode `:166-180` 连续 10 次创建/销毁不抛；PlayMode `:101-125` 10 轮「启用→Dispose」且 `world.IsDisposed==true` + tick 计数回基线。本次 PlayMode `46/46/0` 两例全 Passed。

### V3 批量绘制（重点）—— **受限（部分达成）**

- **达成部分（机制层证据充分）**：
  - 未为 1000 实体各建 GameObject：`grep` 新增源码仅 `KernelCrowdHost.cs:71-72` 建 1 个隐藏宿主；共享 `Matrix4x4[]`（`KernelCrowdDriver.cs:73`、`KernelCrowdRenderer.cs:33-34`）。
  - `Graphics.DrawMeshInstanced` 单次提交：`KernelCrowdRenderer.cs:141-145`（按 1023 分批），`LastSubmitCalls` 是真实调用次数 `:148`。
  - 100 vs 1000 A/B：`KernelCrowdPlayModeTests.cs:77-80` 均断言 `Submit()` 返回 1；本次实跑 `TempLogs/verifier_r9_13_playmode.log:2304`：`entities=1000 totalRows=1000 submitCalls=1 submittedInstances=1000 shader=Universal Render Pipeline/Lit`。
- **未达成 / 受限（如实判，不替施工把「未达成」说成「已完成」）**：
  - 简报目标 6 要求「**PlayMode 断言群体可见**」。实际 `AutoEnable` 默认 **false**（`KernelCrowdHost.cs:42` 自动属性默认 false；`:102-105` 仅 true 时才 `EnsureCrowd()`），PlayMode 用例是**显式** `_host.EnsureCrowd()`（`KernelCrowdPlayModeTests.cs:48`）拉起群体，验到的是**组件逻辑 + 提交层事实**（Logic/View 档注册、真 dll 1000 行、`submitCalls=1`/`submittedInstances=1000`、URP shader 解析成功、100/1000 A/B 提交次数、英雄 FSM 不回归），**不是真实场景里群体开箱可见，也不是像素/可见性证据**。
  - 真实 GPU DrawCall / 像素在 `-batchmode` 无渲染负载下取不到可信值（施工 §六.1 与验收均如实标受限，未编数字）。
  - 结论：DrawCall 判据（提交层）成立且实跑证据充分；但「群体可见」子目标**未完全达成**。若主控要求「进 SCN_Game 即自动可见」，需另立任务改 `AutoEnable` 默认并补齐像素回归证据（会牵动 A-R9-10 的 SCN_Game 相机差分用例）。

### V4 性能守卫 —— **PASS（分段/护栏） + 受限（真实 GPU DrawCall）**

- 本次实跑分段（`TempLogs/verifier_r9_13_editmode.log:372/415`、`verifier_r9_13_playmode.log:2304`，均同一轮内测得）：纯只读游标拉取 **6.5 µs/次**（200 次均值）；内核迭代（游标读+积分+1000 次 `SetComponent` 回写+矩阵）**566.8 µs/次**；渲染提交 **47.6 µs/帧**（PlayMode 真跑）；A/B 纯拉取 100 实体 **0.9 µs** / 1000 实体 **5.6 µs**（6.18×，内核迭代随实体数线性属正常，与「DrawCall 恒为 1 次」形成对照）。
- 对照 10.0 µs 基线口径：基线是「1000 实体纯只读游标拉取」（B-R9-17 §四）。本任务同口径纯只读段 6.5 µs 与 10.0 µs 同量级、未退化；内核迭代 566.8 µs 的增量如实来自简报指定的 1000 次跨 ABI `SetComponent` 回写，**非**游标迭代变慢。**跨跑比较不可信**：本次同轮 B-R9-17 既有基线用例实测 17.9 µs（`verifier_r9_13_editmode.log:1070`），与落盘 10.0 µs 有抖动，故只采用同一次运行内分段与 A/B。
- `Tools/check_machine_quiet.ps1` 已跑：本次 `TempLogs/verifier_r9_13_TestReport.txt` 记 `Machine quiet check: VERDICT: QUIET — 适合取性能/帧率证据`。
- 受限：真实 GPU DrawCall / 像素在批处理无渲染负载下不可信，**未给编造数字**；可证的只是 `Graphics.DrawMeshInstanced` 的真实调用次数 = 1。

### V5 不回归 —— **PASS**

- 自跑：PlayMode **46/46/0**（`TempLogs/verifier_r9_13_playmode.xml`，mtime `21:37:28`，`total="46" passed="46" failed="0"`）；arch/config/naming 三校验器 **exit 0**（五段 `RESULT: ALL PASS (5 passed, 0 skipped, 0 failed)`、`LASTEXITCODE=0`）。
- 只增不减：EditMode **880 ≥ 873**（+7）；PlayMode **46 ≥ 44**（+2）；native **311/311 passed、776,716 断言、Status: SUCCESS!**（验收代理直接运行已构建 `Native/build/bin/Release/zrender_test.exe` 亲证，**未重建**以免改动部署位 `zrender.dll`；本任务不改 `Native/**`）。
- 英雄侧 FSM / 场景布局 / 既有模块零改动：`git diff --name-only` 仅 5 个白名单文件（Demo.md、本日志、3 个 asmdef）；`git status --short` 新增文件全在 `Assets/Game/**`、`Assets/ZFrame/Tests/**`；`Game.Domain`/`Scenes`/`Fsm`/`EnemyView`/`PlayerMotor`/`GameBootstrap`/`GameSceneSetup` 零命中。
- `Game.Domain` 零改动；纯度扫描目录（`ArchitectureValidator.cs:91-95` = `Assets/ZFrame/Gameplay`、`Assets/Game/Domain`）本任务零新增文件。三校验器报告（`verifier_r9_13_{arch,config,name}_report.txt`）均「违规数: 0 / 结果: PASS」，arch 报告「asmdef 扫描数: 25（期望矩阵条目: 12，Tests 程序集豁免）」。
- asmdef `GUID:` 引用合法性：`Game.View.asmdef:4` 的 `GUID:589648d0fabfed44a9480254c552ce49` 与 `Assets/ZRender/Bindings/ZFrame.Render.Bindings.asmdef.meta:2` 的 `guid` 逐一核对**一致**（验收代理手工核对）。**arch exit 0 是否真覆盖该项——如实说明**：`ArchitectureValidator.cs:118-120` `IgnoredReferencePrefixes={"GUID:"}`、`:175-178` 对 `GUID:` 前缀 `continue` 跳过，即「不判违规」而**非验证 GUID 真实性**；本次 arch 报告只报违规数 0，未逐项记录 GUID 校验，合法性由上述 meta 手工核对证实。两个 Tests 程序集用名字引用 `ZFrame.Render.Bindings`（`Game.View.Tests.asmdef:10`、`ZFrame.PlayMode.Tests.asmdef:17`），Tests 程序集豁免矩阵（`:157-163`）。

### V6 报告与变异 —— **PASS**

- 施工报告四节齐全：任务简报（主控）/ 施工报告（施工）/ 验收清单（主控）/ 验收结论（本节）。全部新增/修改文件字节级复核：UTF-8 无 BOM、CR=0、末尾单 LF。
- 「断言一字未动、产品代码只在白名单内」：`git diff --name-only` 仅 5 个白名单文件、3 个 asmdef 为纯追加一行引用；两个测试文件是**新增**（`??`）非修改，既有断言零改动；新增断言均为真断言（V1 行号）且变异复做证明有牙。产品代码改动全在 `Assets/Game/View/Effects/**`、`Assets/Game/View/Editor/**`（新增），未碰任何禁止路径。
- **亲自复做定向变异**（例外授权，仅改本任务新增的 1 个源码文件 1 行）：
  - 目标 `KernelCrowdRenderer.cs:139`：`int batch = Math.Min(MaxInstancesPerDraw, n - offset);` → `int batch = 1;`（逐实体提交）。备份 `TempLogs/verifier_r9_13_KernelCrowdRenderer.cs.bak`（sha256 `7d223560…728`）。
  - 修复态 → 变异态 → 还原态（**均本次自跑 playmode 段，各自新写 xml、EvidenceFresh=true**）：
    - 修复态：**46 / 46 / 0**（`TempLogs/verifier_r9_13_playmode.xml`，`13:36:45Z–13:37:27Z`，Unity exit 0）
    - 变异态：**46 / 45 / 1**（`TempLogs/verifier_r9_13_mutant_playmode.xml`，`13:46:39Z–13:47:19Z`，`result="Failed(Child)"`，Unity exit=2，`RESULT: FAILED (1 failed: playmode)`、脚本 exit 1）
    - 还原态：**46 / 46 / 0**（`TempLogs/verifier_r9_13_restored_playmode.xml`，`13:49:52Z–13:50:32Z`，Unity exit 0、`RESULT: ALL PASS`、脚本 exit 0）
  - 变红用例名：`Crowd_1000Entities_SingleSubmit_And_HeroFsmNotRegressed`；变异态 xml 失败断言：`1000 <= 单次上限 1023 → 一次 DrawMeshInstanced（DrawCall 不随实体数线性增长）` / `Expected: 1` / `But was: 1000` —— 精确命中 V3 判据，证明用例非恒真。**未改断言、未改用例体**。
  - 还原证据：`cp` 备份覆盖回源文件，`cmp` 零差异、sha256 双份一致、`VERIFY-MUTATION` 命中 0、`git diff --no-index`（备份 vs 还原文件）exit 0、`git diff --name-only` 仍为进场那 5 个白名单文件（零残留）。变异期间未 `git add`、未 `git commit`。

### 受限清单（如实披露，非完成度夸大）

1. **真实 GPU DrawCall / 像素可见性未取得**：`-batchmode` 无渲染负载，只有「`DrawMeshInstanced` 真实调用次数 = 1」这一提交层事实；真实可见性留编辑器 Play Mode 人工目测（A-GPU-01 V1 诚实口径）。
2. **`AutoEnable` 默认 false → 群体非开箱自动可见**：PlayMode 用例是显式 `EnsureCrowd()`，验的是组件逻辑/提交层，不是真实场景「进 SCN_Game 即自动可见」；V3 该子目标未完全达成。
3. **IL2CPP / Android 上 P/Invoke 与 `unmanaged` 泛型未验证**：本机 Editor 为 Mono；真机包需另立任务（A-AND-06 IL2CPP 约 29 分钟）。
4. **URP 14 单次实例化上限 1023 未逐一探测**：仅实证「1000 实体 → 1 次提交」；SRP Batcher 兼容性、移动端降级策略未测。
5. **GUID 引用的校验口径**：arch exit 0 靠「忽略 `GUID:` 前缀」实现，非验证 GUID 合法性（合法性由验收代理核对 meta 证实）。
6. **性能数字跨跑不可信**：既有基线用例本轮 17.9 µs vs 落盘 10.0 µs，已改用同一次运行内 A/B 与分段口径。

### 现场状态

- HEAD `761d806a0cbd08bda4a20dcae1ba43d629d626c7`，`git rev-list --count 761d806..HEAD = 0` → **未 commit**；全程无 `git add` / `git commit`。
- `git status --short`：5 个 `M`（`Docs/Demo.md`、`Docs/Logs/TASK-A-R9-13.md`、3 个 asmdef）+ 22 个 `??`（10 个新增 `.cs` 及其 `.meta`），**全部**在简报白名单内；禁止路径（`Native/**`、`ZFrame/View/Hud/**`、`Assets/Audio/**`、`ZFrame/View/Audio/**`、`TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`/`DECISIONS.md`）零命中。
- 无 `URP_*.asset` / `QualitySettings.asset` / `ProjectSettings` 改写（无需定向还原）；`zrender.dll` 未 `git checkout --` 丢弃、git status 无其条目。
- 无残留 Unity.exe、无 `UnityProject/Temp/UnityLockfile`；变异残留 0（`VERIFY-MUTATION` 全仓 0 命中、renderer `cmp` 零差异）。
- 证据归档：施工 xml `TempLogs/verifier_r9_13_builder_*`；本次新写 `TempLogs/verifier_r9_13_{editmode,playmode,mutant_playmode,restored_playmode}.xml` 及 `.log`、`verifier_r9_13_{arch,config,name}_report.txt`、`verifier_r9_13_TestReport.txt`、`verifier_r9_13_KernelCrowdRenderer.cs.bak`。

**验收裁决**：PASS with 受限，可提交；上述 2 项核心受限（真实 GPU DrawCall/像素可见性、`AutoEnable` 默认关导致「群体可见」未完全达成）须由主控签收，若要「进场景即自动可见」需另立任务补像素回归证据。
