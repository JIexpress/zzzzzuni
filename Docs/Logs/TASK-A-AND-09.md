# TASK-A-AND-09 —— 人群四档 LOD 管线（L0≤20/L1≤50/L2 面片/L3 播片边界）

状态: doing | 派发: 2026-11-04 | 施工: general-purpose 施工子代理（2026-11-08） | 验收: general-purpose 验收子代理（2026-11-08，PASS） | commit: （待填）

## 任务简报（主控写）

### 背景（用户绑定约束，ROADMAP 方向四档位表）

人群渲染按精度四档：**L0 全精度≤20**（带骨骼动画，A-ANIM-01 的 NpcAnimatorDriver NPC）/ **L1 低模≤50**（程序化人形几百面）/ **L2 Impostor 面片**（billboard，数百）/ **L3 播片**（仅过场/固定机位——本任务只留接口与边界文档，不实现播片内容）。数量上限是**硬约束**（超限自动降级或拒绝，断言锁死）。

### 设计要点

1. `Game/View/Crowd/CrowdLODPipeline.cs`：统一管理四档——输入：DataKernel 群体状态（A-AND-08 迁移后的 kernel 数据）+ 相机位置；输出：各档位的渲染提交
   - **档位分配**：按与相机距离+重要性标记（剧情 NPC 永远 L0）——距离阈值+迟滞（防频繁切换）
   - **数量硬上限断言**：L0>20→自动降级最远者（或拒绝注册，报告选型）；L1>50 同理
   - L0 提交=NpcAnimatorDriver NPC（A-ANIM-01 产物）；L1=程序化低模人形（Primitive 组合或简单 mesh，几百面）；L2=billboard（CameraFacing+程序化人群纹理或纯色剪影）
2. **DataKernel 集成**：所有档位的实体状态统一从 ZKernelWorld 游标读（A-AND-08 已迁）——渲染档位只改表示法不改数据源
3. 切换平滑：L0↔L1 切换时位置/朝向连续（同坐标），无跳变（断言）
4. **可证伪 PlayMode 用例**：①注册 25 个全精度请求→恰 20 个 L0+5 个降级 L1（硬上限）②距离驱动：NPC 远移→档位降级（L0→L1→L2 断言）+迟滞（边界往返不振荡）③L2 billboard 面向相机断言④数量上限：L2 数百不超限
5. 自测：PlayMode 全绿（71+新增）+ EditMode 全绿（983+新增）+ Validate exit 0
6. **L3 边界文档**：播片实现留待过场任务（VideoPlayer+固定机位）；本任务在管线中预留 L3 枚举位与边界注释

### 约束

- 白名单：`Assets/Game/View/Crowd/**`（管线+L1/L2 表示）、`Assets/ZFrame/View/**`（若需）、`Assets/Game/Tests/**`、本日志
- 不动 DataKernel/kernel/效用 AI Domain 逻辑；A-ANIM-01 产物只消费；Unity 批处理纪律；不 git commit；UTF-8 无 BOM/LF；架构校验器 exit 0

## 施工报告（施工代理写）

施工: general-purpose 施工子代理 ｜ 2026-11-08 ｜ 白名单遵守：仅 `Assets/Game/View/Crowd/**`、`Assets/Game/Tests/**`、本日志；未 git commit。

### 1. 交付物清单（全部新增，9 文件 + Unity 生成 .meta，共 1958 行）

| 文件 | 行数 | 职责 |
|---|---|---|
| `Assets/Game/View/Crowd/CrowdLODPipeline.cs` | 748 | 四档 LOD 管线主体（ITickable，View 档）：单次游标读 kernel → 定档 → 硬上限裁决 → 三档表示同步/提交 |
| `Assets/Game/View/Crowd/CrowdLodLevel.cs` | 42 | 四档枚举（L0FullPrecision/L1LowPoly/L2Billboard/L3VideoClip，L3 只留枚举位） |
| `Assets/Game/View/Crowd/CrowdLodLimits.cs` | 27 | 硬上限常量（MaxL0=20 / MaxL1=50 / MaxL2=512，**不可配置**） |
| `Assets/Game/View/Crowd/CrowdLodConfig.cs` | 55 | 距离阈值与迟滞带（默认 L0 带 13..17 m、L1 带 36..44 m，构造/校验拒绝无带配置） |
| `Assets/Game/View/Crowd/CrowdLodEntityState.cs` | 52 | 单实体只读快照（档位/位置/朝向/billboard 旋转/提交矩阵） |
| `Assets/Game/View/Crowd/CrowdLodLowPolyHumanoid.cs` | 158 | L1 程序化低模人形：16 盒体部件、恰 **300 三角面**（数百面）、1.8 m 站立 |
| `Assets/Game/View/Crowd/CrowdLodBillboardSprite.cs` | 100 | L2 billboard：64×64 程序化头肩剪影纹理 + 单四边形（2 面，+Z 可见法线） |
| `Assets/Game/Tests/View/CrowdLodPipelineTests.cs` | 429 | EditMode 12 用例（真 dll + 真 ZKernelWorld，不 mock） |
| `Assets/Game/Tests/PlayMode/CrowdLodPipelinePlayModeTests.cs` | 347 | PlayMode 5 用例（简报点名的四条可证伪 + kernel 数据源统一） |

### 2. 设计与选型（简报要求"报告选型"的决策）

- **硬上限超限策略**（L0>20 / L1>50 时"自动降级或拒绝"二选一）：
  - **L0 超限 → 自动降级最远的非剧情 L0 到 L1**（`CrowdLODPipeline.EnforceCaps` L498-509；最远并列时后注册者先降，`FarthestIndex` L524 确定性）；剧情 NPC 永不降级。
  - **L1 超限 → 自动降级最远的 L1 到 L2**（L511-518）。
  - **L2 预算耗尽 → 拒绝注册**（`Register` 返回 false）：L3 播片未实现、无处可降。选型理由：L0/L1 有下一档可接，降级保画面连续；L2 是最底实现档，拒绝优于占位。
  - **总注册数上限 = MaxL2(512)**：每个已注册实体必落 L0..L2 之一，L2 是最宽的桶 ⇒ 总数 ≤ 512 ⇒ 任何降级必有落位（L2 桶永不因降级溢出）。512 < 1023（单次 DrawMeshInstanced 上限），数百 L2 仍单 DrawCall。
  - **剧情 L0 槽满 → 拒绝注册**（20 个剧情 NPC 已占满时，第 21 个剧情 NPC 返回 false——剧情永不降级，无法腾位）。
- **档位分配**：距离 + 重要性标记 + 迟滞。每帧（View 档）`HysteresisStep`（L448-472）：升档用 Up 阈值、降档用 Down 阈值（Up < Down 构成迟滞带），**每帧至多一档**（天然防跳档）；剧情 NPC 直接覆盖为 L0（L325-329，距离无关）；无 L0 表示的实体（纯群体）最高 L1。
- **切换平滑**：三档表示的位置/朝向全部来自同一份 kernel 游标读回（`Reg.Position/YawDeg`），切换帧新表示出现在 kernel 坐标上 ⇒ 与旧表示严格同坐标同朝向（PlayMode 用例 2 断言 1e-4/1e-4，L169/L172）。
- **表示法**：L0 = 注册时传入的 NpcAnimatorDriver NPC（A-ANIM-01 产物只消费：管线接管 transform 同步 + SetActive + `ManualDrive=true` 防双写，L226-230）；L1 = 程序化低模人形 DrawMeshInstanced（≤1023/批）；L2 = 剪影四边形按帧 `LookRotation(camPos−pos)` 转向相机批量提交。
- **kernel 真实性**：World 销毁 → Tick 静默冻结不抛（L310-313，沿 A-AND-08 口径）。
- **布局守护**：`Initialize` 校验 World 必须按 A-AND-08 群体布局（KernelCrowdIds.Position/Velocity/Tag 三列尺寸），不符即抛（L708-722）。

### 3. DataKernel 集成（数据源统一，简报要点 2）

- 每帧 View 档**单次** `ZKernelWorld.EachBatches(Position|Velocity|Tag)`（L317）——三档的实体状态全部来自这一次游标读（`ReadBatch` L406-428 按 Tag 列回填注册槽）；**零逐实体 P/Invoke**（注册期一次性 Copy 读除外）。
- 渲染档位只改表示法：管线不持有模拟状态、不写 kernel 列（游标列指针只读）。
- 可证伪：PlayMode 用例 5 只写 kernel 列（SetComponent 瞬移三档实体、无任何其它输入）→ 三档表示全部跟随（L291-346），游标交付行数断言 L306。

### 4. L3 播片边界文档（本任务不实现，简报要点 6）

- **存在形式**：`CrowdLodLevel.L3VideoClip = 3` 枚举位保留（CrowdLodLevel.cs L36-41）；分配器**永不产出 L3**——距离映射最高 L2（HysteresisStep/AssignInitialLevel 无 L3 出口），表示 switch 的 default 分支抛 `InvalidOperationException(L3BoundaryMessage)`（CrowdLODPipeline.cs L45-47、L363-365）作为契约破坏哨兵。
- **未来实现入口与前置条件**（边界定义）：
  1. **触发条件与距离无关**：L3 由过场/固定机位判定驱动（场景标记或叙事事件），不是距离档——不能塞进 HysteresisStep 的距离阶梯，应在 Tick 定档阶段以场景态覆盖（同剧情 NPC 覆盖 L0 的模式，L325-329 的扩展点）。
  2. **资产管线**：VideoPlayer + 预渲染播片资产（每群体一批固定机位片段）+ 播放器生命周期管理（字幕/音轨/硬解），均属过场任务范围。
  3. **数据侧无需新列**：L3 仍是"位置+朝向"的消费者，`CrowdPosition/CrowdVelocity/Tag` 三列即够——数据源统一约定在 L3 下不变。
  4. **数量语义**：L3 播片按"一屏一片"提交（非逐实体），届时 MaxL2 的总注册上限语义需随过场任务重估。

### 5. 自测证据（全部命令实跑，证据文件在 `TempLogs/TASK-A-AND-09/`）

- **PlayMode：80/80 全绿（exit 0）**，duration 54.3 s（`playmode.xml`，2026-11-08 10:34:05Z UTC）。新增 5 用例全过（行号）：
  1. 硬上限 25 请求 → 恰 20 L0 + 5 降级 L1（`CrowdLodPipelinePlayModeTests.cs` L94，20 断言 L109、稳定态 L130）；
  2. 距离驱动阶梯 L0→L1→L2→L1→L0 + 迟滞带 39↔41 / 14↔16 各 10 帧不振荡 + 切换帧位置/朝向连续（L144，连续断言 L169/L172）；
  3. L2 billboard 面向相机 + 相机绕转跟随（dot > 0.999，L221，L238/L248）；
  4. L2 数量上限 512 收 + 第 513 个拒绝（L257，拒绝断言 L271）；
  5. kernel 数据源统一（仅 SetComponent → 三档表示全部跟随，L291，游标行数断言 L306）。
- **EditMode：1029/1031（8.75 s，`editmode.xml`）**，新增 12 用例全过：硬上限常量锁死 L93、低模 300 面 L103、billboard 资产 L128、剧情 NPC 永久 L0 + 槽满拒绝 L175、25 请求 20/5 L205、降最远者+剧情免疫 L239、512 总预算拒绝 L274、迟滞阶梯+不振荡 L315、kernel 销毁冻结 L372、布局守护/配置校验/契约异常（全绿）。
  - **2 个红不属于本任务**：`Game.Domain.Tests.NpcAttitudeBehaviorTests`、`NpcUtilityAgentTests.InvalidSetup_Throws`——并行 A-AI-01 施工中的 Domain 用例（其白名单 `Assets/Game/Domain/**`、`Assets/Game/Tests/Domain/**`，本任务无权触碰）；本任务全部新增用例（12+5）零失败。
- **三校验器 exit 0**（2026-11-08 18:30-18:31 实跑）：架构 `arch=0`、配置 `config=0`、资产命名 `naming=0`（此后仅改动过测试 .cs 文件，不涉三者的扫描范围：纯度扫描仅 Gameplay/Game.Domain、asmdef 矩阵未动、资产名未变）。
- 环境纪律实录：与并行代理共用 Unity，撞锁（exit 21"another Unity instance"）共 10+ 次，全部按"等 60s×N 重试"消化；两次"xml 已出但进程不退"按纪律 taskkill 后续跑。调试期修正三处（管线 Dispose 在 EditMode 须 DestroyImmediate；PlayMode 两处断言时序错误——激活态须等首帧、快照须移动后重取；billboard 绕转相机须留在 L2 距离带）。

### 6. 文件规范自查

- 全部 9 个新文件：UTF-8 无 BOM（首字节 `2f 2f 20`）、LF（全文件 0 个 CR 字节）、末尾单换行；一个类型一个文件、文件名=类型名；私有字段 `_camelCase`、公共 API XML 注释齐备；无 public 字段（blittable 组件例外沿用 CrowdPosition 既有口径）、无 using 通配符。
- 新文件 `.meta` 已由 Unity 生成并与资产同在（`Assets/Game/View/Crowd.meta` + 7 个 .cs.meta + 2 个测试 .meta）。
- 未动：kernel / 效用 AI Domain / A-ANIM-01 产物（NpcAnimatorDriver 只读消费）；未 git commit；未手改任何 .meta。

### 7. 受限项（如实报告）

- EditMode 的 2 个红（A-AI-01 并行在制）使"EditMode 全绿"在**本任务验收时刻**无法由我单方达成——我的 17 个新增用例全绿；建议验收代理复核该 2 用例归属并在 A-AI-01 闭环后复跑全套。
- DrawMeshInstanced 的真实 GPU DrawCall 在 -batchmode 下不可测（沿 A-R9-13 既有口径，以 `LastL1/L2SubmitCalls` 真实调用次数为判据）。

## 验收清单（主控写）

- [x] V1 PlayMode 全绿（数字写入结论）；四条可证伪用例真实（行号）——PASS
- [x] V2 硬上限断言（L0>20 降级/拒绝——行号+变异复做 1 次）——PASS
- [x] V3 档位切换迟滞+无跳变断言（行号）；L2 billboard 面向相机——PASS
- [x] V4 kernel 数据源统一（渲染读 kernel 游标——行号）；L3 边界文档存在——PASS
- [x] V5 Validate exit 0（自跑）+文件规范+施工报告四节完整——PASS

## 验收结论（验收代理写）

**验收**: general-purpose 验收子代理 ｜ 日期: 2026-11-08 ｜ 结论: **PASS**（V1-V5 全过，全部独立亲证；验收代理未改任何业务代码，变异复做已恢复字节一致）

- **V1 PASS**：亲跑 PlayMode **80/80 全绿**（`TempLogs/runtests_playmode.xml` total=80 passed=80 failed=0、duration 53.7s，存档 `TempLogs/TASK-A-AND-09/acceptance_v1_playmode.xml`；运行记录 `acceptance_v1_run.log`）——5 新用例逐名核对全过：`Lod_HardCap_25FullPrecisionRequests_Exact20L0And5Demoted` / `Lod_DistanceDriven_LadderHysteresisAndSwitchContinuity` / `Lod_L2Billboard_FacesCamera_FollowsCameraOrbit` / `Lod_HardCap_L2Budget_RefusesOverflow` / `Lod_KernelIsSoleDataSource_AllLevelsFollowKernelWrite`。亲跑 EditMode 全量 **1031/1031 全绿**（total=1031 passed=1031 failed=0、9.3s）——含并行 A-AI-01 的 Domain 用例，全绿无归属争议；12 个本任务新用例逐名核对全过（`Limits_HardCaps_MatchUserBoundTable`…`Config_InvalidHysteresisBands_Throw` 共 12 条，见 `Game.View.Tests.CrowdLodPipelineTests`）。施工报告受限项（"2 红系 A-AI-01 并行在制"）复核成立：本次亲跑已全绿，不再挂账。
- **V2 PASS**：硬上限用例真实——PlayMode `Lod_HardCap_25FullPrecisionRequests_Exact20L0And5Demoted`（`CrowdLodPipelinePlayModeTests.cs:94`）：恰 20 L0 断言 :109、恰 5 降级 :110、降级身份=最远者 tag 20..24 :114-119、一帧后稳定态 :130-131；EditMode 同名镜像 `Register_25FullPrecisionRequests_Exactly20L0And5DemotedToL1`（`CrowdLodPipelineTests.cs:205`，逐 tag 断言 :222-229）。**常量锁死**：`CrowdLodLimits.cs:16/19/25` 为 `const`（无 setter/无配置入口），`Limits_HardCaps_MatchUserBoundTable`（Tests:93）锁 20/50/512。**变异复做**：MaxL0 20→21 后亲跑——全量 EditMode **1029/1031（2 红）**（`acceptance_v2_mutant.log`），隔离复跑 `acceptance_v2_mutant.xml` 12 用例 2 红逐名：`Limits_HardCaps_MatchUserBoundTable`（常量锁死测试自身抓到违约）+ `Register_25FullPrecisionRequests_Exactly20L0And5DemotedToL1`（恰 20/恰 5 断言失败）；恢复自字节级备份后 `cmp`+md5 与基线 9 文件全等 → 复跑 `acceptance_v2_restored.xml` **12/12 全绿**。配套选型用例亦核：降最远非剧情+剧情免疫（Tests:239，实现 `CrowdLODPipeline.EnforceCaps:498-521`/`FarthestIndex:524-556` 剧情排除 :542-544、并列取后注册 :548）、剧情 L0 槽满拒绝（Tests:175/:193，实现 :205-208）、L2 总预算拒绝（Tests:274/:291、PlayMode:257/:271，实现 :199-202）。
- **V3 PASS**：距离阶梯 L0→L1→L2→L1→L0（PlayMode :144 用例内 :156→:165→:179→:196→:212，10m/17.5m/50m/15m/10m 全部对准阈值语义）；**迟滞带不振荡**：L2 带 [36,44] d=39↔41 各 10 帧（:183-190）+ L1 带 [13,17] d=14↔16 各 10 帧（:199-206），EditMode 侧 20 帧×2（Tests:337-344/:352-359）；**切换帧连续**：L1 提交矩阵平移= L0 表示位置（:169-170）、朝向差 <1e-4（:171-172，`Assert.Less(...magnitude, 1e-4f)`）、升档回 L0 重新对位（:214）——三档表示全由同一 `reg.Position/YawDeg` 驱动故严格同坐标。**L2 billboard**：dot(=forward·toCam)>0.999（:238）+ 相机绕转远端侧翼后仍 L2 且 dot>0.999 跟随（:243-249），实现 `LookRotation(camPos−pos)`（Pipeline:355-358）。
- **V4 PASS**：**每帧单次游标读**——`CrowdLODPipeline.Tick` 唯一 kernel 读取 = `LastKernelReadRows = _world.EachBatches(_readMask, _readBatchHandler)`（Pipeline:317，Position|Velocity|Tag 三列掩码 :116-118）；游标回调 `ReadBatch`（:422-441）纯指针列读，**逐帧路径零逐实体 P/Invoke**（全文件 `GetComponent` 仅注册期一次性 Copy 读 :211-212 + `Initialize` 布局校验 `GetComponentSize` :712-714；grep 证实）；**三档同源**：`reg.Position/YawDeg` 仅由 ReadBatch 回填（:436-439），L0 `SyncL0`（:585-586）/ L1 TRS（:348-350）/ L2 TRS（:359）全部消费同一份——PlayMode 用例 5 仅写 kernel 列（SetComponent 瞬移三档实体、无任何其它输入）→ 三档表示全部跟随（:291-345，游标交付行数断言 :306=3）。
- **V5 PASS**：**L3 边界文档存在**——施工报告 §4（触发与距离无关/资产管线/数据侧无需新列/数量语义四条边界+实现入口）；代码侧契约哨兵：`CrowdLodLevel.L3VideoClip=3` 枚举位（CrowdLodLevel.cs:40）、`L3BoundaryMessage`（Pipeline:45-47）、分配器无 L3 出口且 switch default 抛 `InvalidOperationException`（:363-365、:469-471）。**文件规范**：9 新文件字节级核验 UTF-8 无 BOM（首 3 字节 `2f 2f 20`）、全文件 0 CR（LF）、末尾单换行；.meta 齐全（7 个 .cs.meta + Crowd.meta + 2 个测试 .meta，均 Unity 生成未手改）；白名单遵守（git 状态仅 `Assets/Game/View/Crowd/**`、`Assets/Game/Tests/**`、本日志；asmdef 改动属并行 A-AI-01）。**施工报告四节齐全**（简报/施工报告/验收清单/验收结论——本节补齐）。**三校验器亲跑 exit 0**（2026-11-08 21:41 自跑，`acceptance_v5_validators.log` + 报告存档）：架构 0 违规 PASS、配置 0 违规 PASS、资产命名 0 违规 PASS（报告 `TempLogs/TASK-A-AND-09/{arch,config,name}_report.txt`）。

**验收备注（如实）**：① 与并行 A-AI-01 验收代理共用 Unity 单实例：全程按纪律轮询等锁 + 撞锁退避重试；期间我方一个 EditMode 运行 xml 被对方后续启动按工具语义清删——数字已先行提取并以运行日志（`acceptance_v1_run.log`）为凭，PlayMode xml 已存档；② 对方一个 >90 分钟零证据产出的 Unity 僵死进程（超过 run_tests.ps1 任何 HardTimeout）按共享纪律 taskkill 后释放槽位，我校验器随即 75 秒完成（三点 exit 0）；③ DrawMeshInstanced 真实 GPU DrawCall 在 -batchmode 不可测（沿 A-R9-13 口径，以 `LastL1/L2SubmitCalls` 真实调用次数为判据，用例 1/3/4 均断言）——非本任务新增受限。
