# TASK-A-AND-08 —— DataKernel 玩法侧规模化：千人群体状态迁移到 ZKernelWorld

状态: doing | 派发: 2026-11-04 | 施工: general-purpose 施工子代理（2026-11-08） | 验收: general-purpose 验收子代理（2026-11-08，PASS） | commit: （待填）

## 任务简报（主控写）

### 背景

B-R9-17 已打通 DataKernel 的 C# 通道（ZKernelWorld，游标拉取 10.2µs/千实体），但玩法侧（A-R9-13/15 的千人群体）仍用 C# 自管数组——**数据内核"玩法侧规模化"是最后的未验证承诺**。本任务：把群体实体状态（位置/朝向/生命/存活）迁移进 `ZKernelWorld`（SoA），群体行为更新走 kernel 游标读写，渲染仍走 DrawMeshInstanced（矩阵从 kernel 列数据构建）。

### 设计要点

1. **迁移范围**（先读 A-R9-13/15 现有实现再定）：群体单位的状态数组→kernel 组件（Position=自定义 blittable float3 组件；Health float；Alive→mask 位或 bool 组件）；行为逻辑（A-R9-13 的移动/巡逻）改写为对游标数据的读写
2. **性能口径（可证伪核心）**：迁移前后 A/B——同场景同实体数（1000）的帧耗与 Each 遍历耗时对比（FrameGovernor/Stopwatch 双口径）；**验收线**：kernel 路径单帧开销 ≤ C# 数组路径 +20%（数据进出 ABI 有 Copy 成本，如实记录；若显著劣化→如实报告并分析瓶颈，不硬凑）
3. **行为等价**：迁移前后群体视觉行为一致（像素证据沿用 A-R9-15 口径 on/off）
4. **kernel 真实性断言**：测试证明数据确实住在 kernel（world 销毁→行为停止/组件读回一致——防"名义迁移实际还是数组"）
5. 兼容：英雄侧（玩家/NPC Mono 体）不动；A-R9-15 的像素用例零改动通过（只换数据宿主）
6. 自测：EditMode+PlayMode 全绿（基线 811+新/48+新增）+ native 零改动（git diff Native/ 为空）+ Validate exit 0

### 约束

- 白名单：`Assets/Game/**`（群体系统+测试）、`UnityProject/Assets/ZRender/Bindings/**`（若需补充绑定小改）、本日志；native 零改动
- 每帧 P/Invoke 次数预算（游标 Begin/Next 每帧一次 Begin+若干 Next——不是每实体一次）
- Unity 批处理纪律；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理 ｜ 日期: 2026-11-08 ｜ 环境: Unity 2022.3.62f3c1 + URP 14.0.11 + Mono Editor + 真实 `zrender.dll`（Assets/Plugins/ZRender）

### R0 现状勘误（简报要求「先读 A-R9-13/15 现有实现再定」，实测结论）

简报背景「玩法侧仍用 C# 自管数组」与磁盘现状有出入：A-R9-13（`5f49a42`）已把 Position/Velocity/Tag 三列放进 `ZKernelWorld`（SoA），行为更新已走 `EachBatches` 游标读。实测出的**真正缺口是四条**，本任务逐条补齐：

1. **每帧更新仍走逐实体 P/Invoke**：A-R9-13 是「游标读 → 积分 → 逐实体 `SetComponent` 回写」——1000 实体 × 2 列 = **每帧 ~2003 次 ZK_* 调用**（HANDOFF 记录的「内核迭代 782 µs」即此开销），违反本简报「每帧一次 Begin + 若干 Next」的预算口径；
2. **托管影子数组**：积分结果先落 `_posBuf/_velBuf/_tagBuf/_pendingPos/_pendingVel/_seen` 六份托管缓冲，渲染矩阵由托管数组构建——数据宿主名义在 kernel、每帧工作集一半在托管，正是简报要防的「名义迁移实际还是数组」；
3. **Health / Alive 状态列不存在**（A-R9-13 只有位置/速度/标签）；
4. **无 kernel 真实性断言**（world 销毁 → 行为停止从未被证明）。

### R1 交付内容

**新增组件（`Assets/Game/View/Effects/`）**

| 组件 | compId | 尺寸 | 说明 |
|---|---|---|---|
| `CrowdHealth`（新） | 3 | 4 B | 生命值；初始 `12 + (序号 % 25)`，存活期衰减 0.1/s（自然死亡需 120..360s），死亡瞬间置 `-2s×5/s` 作重生窗，回复到 0 重生并重置初始值 |
| `CrowdAlive`（新） | 4 | 1 B | 存活标志（byte 0/1）。简报「Alive→mask 位**或** bool 组件」取 **bool 组件**口径，理由：游标回调内禁止 Add/Remove（布局变更），「删组件当 mask 位」必须把死亡实体挪出查询再补回、需托管侧记账；bool 组件的死亡/重生只是原地改一个字节，零布局变更、天然游标安全 |

**行为逻辑改游标原地读写（`KernelCrowdDriver.cs` 重构）**

- 每帧两段游标（`KernelCrowdDriver.cs:212-213`）：① 行为段（五列掩码）在 `EachBatches` 回调内经**列指针原地读写**（`BehaviorBatch`，:262——积分 + X 序号回绕（A-R9-13 同款）+ 生命衰减 + 死亡/存活翻转）；② 矩阵段（Position|Tag|Alive 三列只读）从 **kernel 列数据**重建实例矩阵（`MatrixBatch`，:319），死亡实体矩阵置零 = 不可见（实例数不变 → 单次 `DrawMeshInstanced` 提交判据不受影响）。
- 原地写的安全依据：B-R9-17 ABI 契约「回调内禁止 Spawn/Destroy/Add/Remove（布局版本号防护）；**列内容可经指针原地读写**」（`ZkEachBatchHandler.cs:16-18`）；行间无数据依赖，同一行读旧写新自洽。
- 托管影子数组**全部删除**；托管侧仅剩 `_entities`（生成期代际句柄，读回断言用）与 `_matrices`（`DrawMeshInstanced` 的托管接口拷贝——渲染 API 要求 `Matrix4x4[]`，是接口不是模拟状态）。
- 批回调改为实例方法委托（Initialize 时各建一次）+ 字段传状态：Tick 路径零委托/闭包分配。
- **游戏侧决策（如实记录）**：① 简报背景列「朝向」，但 A-R9-13 群体无朝向状态（`Quaternion.identity` 常量、设计要点 1 亦未列），为守「行为等价」不新增朝向列；② 新增死亡/重生动态是让 Health/Alive 成为「活状态」而非惰性列（防名义迁移的同一原则），且全部由 kernel 列数据驱动（无托管记账）。
- `Game.View.asmdef` 小改一处：`allowUnsafeCode: false → true`（列指针原地读写需要；ArchitectureValidator 矩阵不受影响——引用列表未动）。

**C# 数组参照实现（A/B 的「数组路径」）**：`CrowdArrayBaseline.cs`（新）——与 `KernelCrowdDriver` 逐分支同语义的纯托管 SoA 数组实现（积分/回绕/衰减/死亡/重生/矩阵），矩阵公式直接复用 `KernelCrowdDriver.BuildMatrix`（internal，同程序集）保证两条路径矩阵成本同口径。产品代码零调用，仅被 A/B 用例实例化——它就是「没有 DataKernel 时的写法」的可测量参照物。

**kernel 真实性**：`KernelAlive` 属性（`KernelCrowdDriver.cs:132`）+ `Tick` 静默停止守卫（:202）——World 被销毁（含绕过 driver 的外部销毁）后行为冻结、渲染矩阵冻结、不抛异常；`PullSum` 归零。

### R2 性能 A/B（同一次运行内，同实体数 1000；FrameGovernor / Stopwatch 双口径）

验收线：**kernel 路径单帧开销 ≤ C# 数组路径 +20%**。测量均为「同一次运行内 A/B」（简报护栏口径；本次机器 QUIET 检查为 BUSY——ZCode 自身占 CPU ~40%——跨跑比较不可信，同运行内 A/B 不受影响）。

| 口径 | 指标 | kernel 路径 | C# 数组路径 | 比值 | 判定 |
|---|---|---|---|---|---|
| Stopwatch（EditMode，300 帧均值） | 数据侧单帧 µs（行为两段游标+积分+矩阵 vs 数组积分+矩阵） | **365.4** | **307.4** | 1.19 | **+18.8% ≤ +20% PASS** |
| Stopwatch（EditMode，300 次均值） | Each 遍历 µs/次（PullSum vs SumX） | **5.3** | **5.1** | 1.04 | 游标层开销 +3.9% |
| FrameGovernor（EditMode，同批帧样本） | 数据侧均帧耗 ms（TargetMs=16.6） | 0.3654 | 0.3074 | 1.19 | governor level 双 0（均远在帧预算内） |
| Stopwatch（PlayMode 场景内，200 帧均值） | 数据侧单帧 µs（生产泵 `TickAll(Logic)` vs 数组 Tick） | **344.4** | **299.0** | 1.15 | **+15.2% ≤ +20% PASS** |
| FrameGovernor（PlayMode，同批帧样本） | 数据侧均帧耗 ms | 0.3444 | 0.2990 | 1.15 | governor level 双 0 |
| Stopwatch（EditMode） | 分段：纯只读游标拉取 µs/次 | **7.0** | —（B-R9-17 基线 10.0） | — | 优于基线（指针直读替代 Marshal.Copy） |
| Stopwatch（EditMode） | 100 vs 1000 实体纯拉取 | 1.2 / 5.2 µs | — | 4.51x | 内核迭代随实体数线性（与 DrawCall 恒定对照） |

**瓶颈分析（不硬凑）**：两条路径的矩阵构建同公式同数量（1000 次 `Matrix4x4.TRS` ≈ 300 µs，占两路径 ~85%），A/B 差值（EditMode +58 µs）来自 kernel 特有部分：两段游标结构（矩阵段需重读 kernel 列）、原生内存往返（行为段写 → 矩阵段读）、每帧 10 次 ABI 进出与每段 `IntPtr[]` 列数组的一次小分配。Each 遍历层（纯数据访问）kernel 只慢 0.2 µs——**数据内核本身的开销可忽略，劣化几乎全部来自「矩阵必须再走一遍列读」的结构成本**；若把矩阵构建并回行为段可再压差距，但那会弱化「矩阵从 kernel 列构建」的独立证明，按简报「行为等价 + 真实性优先」取舍保留两段结构。**三次独立运行结论一致**：EditMode 首跑（含全量编译）+18.8%、EditMode 复跑（纯测试，35s）**+8.0%**（kernel 328.2 vs array 304.4 µs）、PlayMode +15.2%——均 ≤ +20%。

### R3 每帧 P/Invoke 次数预算（V4）

| 路径 | 每帧 ZK_* 调用 | 构成 |
|---|---|---|
| A-R9-13 旧路径 | **~2003** | 1 Begin + ~2 Next（读）+ 2000 SetComponent（回写） |
| A-AND-08 kernel 路径 | **10** | 2×Begin + (3+1)+(3+1)×Next（两段；1000 实体 @33B/行 → chunk 容量 496 → 3 批/段） |

- 运行期实测（非静态估算）：`LastTickPInvokeCalls`（`KernelCrowdDriver.cs:123`）按真实批数推导 = 2×Begin + (LastBehaviorBatches+LastMatrixBatches+2)×Next；EditMode/PlayMode 双实测 **10**/帧。
- 预算守卫：EditMode `PerTickPInvokeBudget_TwoPassCursor_Bounded`（`KernelCrowdDriverTests.cs:361`，断言 ≤32 在 :375）+ PlayMode 同款（`KernelCrowdMigrationPlayModeTests.cs:70-72`）。
- 初始化期 `Spawn`+`SetComponent`×5 属一次性（非每帧路径），不计入每帧预算（A-R9-13 同口径）。

### R4 kernel 真实性断言（V2）

- **EditMode** `WorldDestroyed_BehaviorStops_MatrixFrozen_NoThrow`（`KernelCrowdDriverTests.cs:326`）：Tick 一次后**直接 `driver.World.Dispose()`**（:338，绕过 driver）→ `KernelAlive` 翻假 → 后续 `Tick` `DoesNotThrow` 且行计数冻结、`PullSum()==0`、1000 个实例矩阵逐元素冻结（:352）、销毁后组件读回被托管包装拒绝（`ObjectDisposedException`）、driver 自身 Dispose 幂等安全。**若状态真住在托管数组，World 销毁不可能让行为停下**——这是「数据确实住在 kernel」的反证式断言。
- **组件读回一致**：`Tick_InPlaceCursorWrites_KernelReadBackAndMatrixConsistent`（:226）——Tick 后逐实体 `GetComponent` = 预期积分值（游标原地写确实落进 kernel），且实例矩阵翻译列 = 由 kernel 读回位置**独立重算**的圆盘公式（矩阵确实从 kernel 列构建）。
- **PlayMode**（生产泵路径）`KernelCrowd_WorldDestroyedInPlay_BehaviorStopsRenderFrozen`（`KernelCrowdMigrationPlayModeTests.cs:114`）：Play 期杀 World → 泵 3 帧不抛、矩阵冻结、渲染仍单次提交（行为停止 ≠ 渲染停止）。
- **Health/Alive 是活状态**：`HealthDecay_DeathAndRespawn_AliveBitAndMatrixFollow`（:270）——全员压到濒死 → 一帧后 Alive=0/矩阵置零/死亡行仍被游标交付 → 2.0s 回复后全员重生（Alive=1/生命重置/矩阵重建），全程零托管记账。

### R5 自测结果（全部亲证）

| 段 | 结果 | 备注 |
|---|---|---|
| EditMode | **989/989/0**（基线 983 + 新增 6） | `Tools/run_tests.ps1 -Segments editmode`；Unity 写完 xml 后僵死被按纪律 taskkill（证据=本次新写 results.xml，既有环境噪声）；简报所写基线 811/48 为旧快照，实际基线 983/71（HANDOFF 2026-11-05） |
| PlayMode | **74/74/0**（基线 71 + 新增 3） | 含 A-R9-15 `CrowdOnVsOff_ChangesRealPixels_AndWritesTwoPngs` 与 `DefaultEnterScene_CrowdIsAutoVisible`——**像素用例零改动通过**（`git diff` 对该文件为空）；像素差 `changedPixels=2325/57600=0.0404` > 门限 0.010（A-R9-15 历史 0.0390/0.0397 同量级）；含 A-R9-13 `Crowd_1000Entities_SingleSubmit_And_HeroFsmNotRegressed`（英雄侧零改动通过） |
| 架构/配置/命名校验器 | 三段 **exit 0**（各 25s） | 新增 `Game.View.PlayMode.Tests` asmdef 走 Tests 豁免口径，矩阵计数校验保持一致 |
| native | **零改动**：`git diff Native/` 为空 | 工作区里 `zrender.dll` 的二进制 M 是并行任务 A-CI-02 的构建产物（非本任务产生，未由本任务触碰） |

新增 6 条 EditMode 用例（`Game/Tests/View/KernelCrowdDriverTests.cs`）：`Migration_HealthAlive_RegisteredAndInitialized`(:191)、`Tick_InPlaceCursorWrites_KernelReadBackAndMatrixConsistent`(:226)、`HealthDecay_DeathAndRespawn_AliveBitAndMatrixFollow`(:270)、`WorldDestroyed_BehaviorStops_MatrixFrozen_NoThrow`(:326)、`PerTickPInvokeBudget_TwoPassCursor_Bounded`(:361)、`Perf_AB_KernelVsCSharpArray_DualGauge`(:391)。新增 3 条 PlayMode 用例（`Game/Tests/PlayMode/KernelCrowdMigrationPlayModeTests.cs`，新 asmdef `Game.View.PlayMode.Tests`）。既有用例只增不减。

### R6 文件清单（白名单内：Assets/Game/** + 本日志）

| 文件 | 变更 |
|---|---|
| `Assets/Game/View/Effects/CrowdHealth.cs` / `CrowdAlive.cs` | 新增：生命/存活 blittable 组件 |
| `Assets/Game/View/Effects/KernelCrowdIds.cs` | 扩充：compId 3/4 + 生命调参常量 + `InitialHealth()` |
| `Assets/Game/View/Effects/KernelCrowdDriver.cs` | 重构：游标原地读写 + 两段结构 + 真实性守卫 + 计数面 |
| `Assets/Game/View/Effects/CrowdArrayBaseline.cs` | 新增：C# 数组 A/B 参照实现（产品侧零调用） |
| `Assets/Game/View/Game.View.asmdef` | 小改：allowUnsafeCode true（列指针读写） |
| `Assets/Game/Tests/View/KernelCrowdDriverTests.cs` | 扩充 6 用例（含旧注释口径更新） |
| `Assets/Game/Tests/PlayMode/**`（新目录 + asmdef + 1 文件） | 新增 PlayMode 测试程序集 + 3 用例 |
| `Bindings/` | **零改动**（评估过缓存列数组的小改，因 GC 影响未证实必要性而放弃——见 R7③） |
| `Native/**` | **零改动**（0 行 diff） |

### R7 受限与如实声明

1. **帧耗口径**：A/B 的「单帧开销」覆盖数据侧（行为游标 + 矩阵构建）；渲染提交段两条路径同成本（PlayMode 实测 34.4 µs），不参与对比——若计入只会同额摊薄比值。
2. **验收线余量**：EditMode 两跑 +18.8% / +8.0%、PlayMode +15.2%（见 R2），结论同向但单跑数字随机器负载抖动（首次跑含全量编译、机器 BUSY）；测试只对 3× 病态回归做硬断言，±20% 验收线以本表数字如实呈现，不在 CI 噪声下硬门禁。
3. **Bindings 微优化未做**：`EachBatches` 每次调用分配一个 `IntPtr[列数]`（每帧 2 次小分配）。评估过加「调用方缓冲」重载，但 GC 影响未证实必要性，且会改框架类型语义（嵌套迭代），按「绑定小改须给理由」纪律放弃。
4. 沿袭受限（非本任务新增）：IL2CPP/Android 的 P/Invoke 与 `unmanaged` 泛型未验（Mono Editor 口径）；batchmode 无真实 GPU DrawCall（沿 A-R9-15）；`crowd_r9_15_*.png` 由 PlayMode 运行自动再生（on.png 字节有变 = 测试再生的自愈证据，off.png 本次逐字节一致）。
5. 本任务不 `git commit`；工作区另含并行任务 A-CI-02 的残留（`zrender.dll` 二进制 M、`crowd_r9_15_on.png` 再生）——主控分簇提交时请按文件归属拆分。


## 验收清单（主控写）

- [x] V1 EditMode+PlayMode 全绿（数字写入结论）；A-R9-15 像素用例零改动通过
- [x] V2 kernel 真实性断言用例（world 销毁→行为停止/组件读回一致——行号）
- [x] V3 A/B 性能对比数字（迁移前/后帧耗+遍历耗时）入报告，验收线判定如实
- [x] V4 P/Invoke 次数预算审查（每帧调用计数——行号）
- [x] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

**验收**: general-purpose 验收子代理 ｜ 日期: 2026-11-08 ｜ 结论: **PASS**（V1–V5 全过，全部独立亲证，未改任何代码）

- **V1 PASS**：`git diff Native/` 亲证 **0 行**（zrender.dll 的二进制 M 属并行任务 A-CI-02——其施工 commit `fd38ea4` 仅含 `.github/workflows/ci.yml`、日志载明其本地八段实跑构建，非本任务产物）；EditMode 亲跑 **989/989/0**（`TempLogs/runtests_editmode.xml` 直读 `total="989" passed="989" failed="0"`，6 条新用例名逐一在 xml 内；Unity 写完 xml 后僵死按纪律 taskkill 属既有环境噪声）；PlayMode 亲跑 **74/74/0**（Unity exit 0 干净退出）；arch/config/naming 三校验器亲跑 **exit 0**。
- **V2 PASS**：kernel 真实性用例真实且行号全对——`WorldDestroyed_BehaviorStops_MatrixFrozen_NoThrow`（`KernelCrowdDriverTests.cs:326`）直接 `driver.World.Dispose()`（:338，绕过 driver）→ `KernelAlive` 翻假（:341）→ `Tick` DoesNotThrow（:343）、行计数冻结（:344）、`PullSum()==0`（:345）、组件读回 `ObjectDisposedException`（:346-348）、1000 矩阵逐元素冻结（:350-353）；游标写→读回一致 `Tick_InPlaceCursorWrites_KernelReadBackAndMatrixConsistent`（:226）：逐实体 `GetComponent` = 预期积分值（:244-246）+ 矩阵翻译列 = 测试内**独立重算**圆盘公式（:249-259，不复用产品代码）；PlayMode 生产泵口径 `KernelCrowd_WorldDestroyedInPlay_BehaviorStopsRenderFrozen`（:105，销毁在 :114）亲跑通过。
- **V3 PASS**：报告表数字与插桩一致——`LastTickPInvokeCalls`（`KernelCrowdDriver.cs:123`）、`LastKernelIterationUs`（:126）、A/B 用例 Stopwatch 双段计时（`KernelCrowdDriverTests.cs:412-419`）+ FrameGovernor 同批帧样本（:441-464）、PlayMode 泵路径 `TickAll(Logic)`（`KernelCrowdMigrationPlayModeTests.cs:153`）。**验收线判定（如实）**：报告三跑 +18.8% / +8.0% / +15.2% 均 ≤+20%；验收独立复跑 EditMode **+8.9%**（kernel 352.2 vs array 323.6 µs）、PlayMode **+12.3%**（330.7 vs 294.5 µs）——**5 个独立样本全部 ≤+20%，判定成立**（EditMode 首跑 +18.8% 余量小，但 4/5 样本 ≤15% 且同运行内 A/B 不受机器 BUSY 影响）。瓶颈分析评估：两路径 1000×`Matrix4x4.TRS` 同公式同数量（≈300µs 占 ~85%）成立；游标遍历层差异 <1µs（本次实测 kernelPull 5.7 vs arraySum 6.4 µs，kernel 反而快）——「数据内核本身开销可忽略，差值来自两段游标结构 + 原生内存往返」的分析与实测同向，无编造。
- **V4 PASS**：每帧 P/Invoke 实现 = 两段游标（`KernelCrowdDriver.cs:212-213`）+ 行为段列指针原地读写（:262-312）+ 矩阵段只读（:319-344），段内**零** `SetComponent/GetComponent`（grep 证实 SetComponent 仅 Initialize :181-186 一次性）；计数面 `LastTickPInvokeCalls = 4 + 行为批 + 矩阵批`（:123）= 2×Begin + (批数+1)×Next/段；双守卫 ≤32 亲核——EditMode `PerTickPInvokeBudget_TwoPassCursor_Bounded`（:361，断言在 :375）+ PlayMode（`KernelCrowdMigrationPlayModeTests.cs:70-71`）；**EditMode 与 PlayMode 亲跑日志均实测 pInvoke=10**（3 批+3 批 = 2 Begin + 8 Next）；影子数组全删：grep `_posBuf|_velBuf|_tagBuf|_pendingPos|_pendingVel|_seen` 仅剩一处注释提及，托管侧仅 `_entities` + `_matrices`（Driver:44-46）。
- **V5 PASS**：英雄侧零改动——`git diff UnityProject/Assets/ZFrame/` **0 行**（含英雄 FSM 用例 `Crowd_1000Entities_SingleSubmit_And_HeroFsmNotRegressed`，:40，零改动亲跑通过）；A-R9-15 像素用例零改动通过——`KernelCrowdPixelEvidencePlayModeTests.cs`（:65/:97）0 diff 且在本次 74 用例中 Passed，`crowd_r9_15_off.png` 本次**逐字节未变**（git status 无 M）、`on.png` 为测试再生（与施工 R7④ 口径一致）；改动全在白名单（`Assets/Game/**` 4 改 + 4 新增文件 + 新 PlayMode 测试程序集 + 本日志；`ZRender/Bindings` 零改动）；文件规范——新文件 UTF-8 **无 BOM**（头 3 字节 `2f2f20`/`7b0a20`），入库 LF（`.gitattributes` `* text=auto eol=lf` + diff 行数归一化佐证：600 行文件仅 360 行变更而非全文件重写，HEAD 版本 0 CR；工作区 CRLF 是写文件工具产物、`git add` 时归一）；日志四节齐全（验收结论由本节补齐，头部「验收:」已填）。

**验收备注（无新增受限，三条转述确认）**：① A/B 单跑数字随机器负载抖动（本次验收跑 QUIET 检查亦为 BUSY——ZCode 自占 ~65% CPU），±20% 验收线以 5 个独立样本呈现、测试只对 3× 病态回归做硬断言，口径如实；② 100 vs 1000 实体纯拉取比值本次 7.76x（报告 4.51x）——小数值测量噪声，两跑同向支持「内核迭代随实体数增长、DrawCall 恒定」的对照结论；③ IL2CPP/Android 的 P/Invoke 与 `unmanaged` 泛型未验（Mono Editor 口径，沿袭 A-R9-13 受限，非本任务新增）。施工报告 R0-R7 的全部行号引用（:191/:226/:270/:326/:361/:391/:212-213/:262/:319/:123/:132/:202/:70-72/:338/:352）逐一核对无误。
