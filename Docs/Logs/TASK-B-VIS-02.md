# TASK-B-VIS-02 —— 真视锥剔除纯函数 `CullToViewMask` + 帧内录入/消费计数断言

- **轨道**：B / VIS（B-VIS-01 裁决 A 的诚实注记衍生的加固项；不挡路）
- **状态**：todo（施工完成，待验收）
- **派发**：主控，2026-11-15（本轮接手指令）
- **来源**：`Docs/Logs/TASK-B-VIS-01.md` §审计报告 5（加固建议）+ §裁决与验收结论文末注记；
  `TASKS.md` B-VIS-02 行：「真视锥剔除纯函数CullToViewMask+帧内录入/消费计数断言(不挡路,核实任务衍生加固)」

## 任务简报（主控未落盘，施工代理按派发令代拟；来源=上列三处）

### 0. 背景与现状

- B-VIS-01 审计结论：`Native/zrender/draw_list.h` 的 `VisibilitySet` 是「一次录入（viewMask）、多视图消费」，
  全库无第二个剔除调用点；但**掩码由调用方按 castShadow/outline 标志位给定，真 per-view frustum culling 不存在**。
- Unity 侧已有剔除位置：`ZRender/Runtime/ZrSceneMath.cs:170 IsVisible`（包装 `GeometryUtility.TestPlanesAABB`，
  只回答「画不画」，产出 bool，不产出掩码，也不服务多视图）。
- 原生 `Mask128`（`Native/zkernel/zkernel.h:50`）语义：bit i → `w[i>>6]` 的第 `(i&63)` 位；托管形态为
  `ZRender/Bindings/ZkMask.cs` 的 `(Lo, Hi)`。

### 1. 做什么

1. 纯函数 `CullToViewMask`：输入 = 相机视锥（视图×投影矩阵提取六平面）+ 包围球，输出 = 128 位掩码
   （**复用 Mask128 位序，不新造**）。
2. 帧内「录入 / 消费」计数断言：录入 N、消费 M，`M ≤ N` 且同一 `(物件, 视图)` 不重复消费。
3. 断言逐例覆盖：可见 / 不可见 / 边界（恰好相切、退化半径、NaN）。
4. 变异复红：把平面测试方向或掩码位序改坏 → 必红 → 恢复。

### 2. 约束

- 纯函数放 `ZRender/Runtime` 既有剔除位置；不接线 `ZRenderSceneBridge`（「不挡路」，不改现有画面/像素基线）。
- 只碰 ZRender C# + 其测试；不动 Native/ZRender 原生、不动 asmdef（27 不变）。
- 不 commit、不改看板；UTF-8 无 BOM + LF。

### 3. 完成判据

- 新用例覆盖上列全部边界，且变异后必红（两组变异，方向 + 位序）。
- 全量 EditMode / PlayMode / arch / naming 全绿且不回退既有基线。

## 施工报告（施工子代理，2026-11-15）

### 1. 交付内容

| 文件（`UnityProject/Assets/` 下） | 角色 |
|---|---|
| `ZRender/Runtime/ZrSceneMath.cs`（修改） | 追加 `ZrViewFrustum`（六平面 + `FromViewProjection` Gribb-Hartmann 提取 + `IsValid`）、`CullToViewMask`、`IntersectsSphere` 及私有平面/有限性辅助。刻意放在既有剔除位置旁（`IsVisible` 同文件），不新开目录、不新造掩码类型 |
| `ZRender/Runtime/ZrVisibilityFrame.cs`（新增） | 帧内账本：`Record` / `TryConsume` / `ConsumedForView` / `Validate`；计数 `DuplicateRecords`、`DuplicateConsumptions`、`OrphanConsumptions` |
| `ZRender/Tests/ZrViewCullingTests.cs`（新增） | 20 例：几何三态、六面逐一越界、透视近远平面、相切、退化半径、负半径、NaN/无穷、退化矩阵、空输入、多视图位序、bit64 高字、>128 截断、账本 6 例 |

**API 与语义（可核对）**

- `CullToViewMask(ZrViewFrustum[] views, int viewCount, Vector3 center, float radius) → ZkMask`
  - 位序：视图 i → 第 i 位（i<64 落 `Lo`，否则 `Hi`），与 `Mask128::Has/Set` 的 `(i>>6, i&63)` 一致；
    `viewCount>128` 的部分直接不置位（掩码物理上限）。
  - 平面：`Left=row3+row0`、`Right=row3−row0`、`Bottom=row3+row1`、`Top=row3−row1`、
    `Near=row3+row2`、`Far=row3−row2`；抽出后归一化，法线朝内。
  - 边界：`d >= -r` 判可见，故**恰好相切可见**（闭集）；`r==0` 退化为点测试；`r<0` 按 0；
    任一 NaN/无穷输入或平面退化 → 该视图不可见（**fail-closed**，宁可不画不画错）。
- `ZrVisibilityFrame`：`Recorded = N`（物件数，非 N×视图数）；`ConsumedForView(v) = M_v <= N`；
  `ConsumedTotal = Σ M_v`（多视图合计允许 > N，**不是**重复消费）；重复只按 `(物件, 视图)` 判。
  账本不抛异常，违规计数后由 `Validate(out error)` 给初个违规说明。

### 2. 变异复红（两组，均逐字节恢复）

| 变异 | 改法 | 结果 | 恢复 |
|---|---|---|---|
| M1 平面方向 | `frustum.Left` 的 `sign` `+1f → -1f`（左/右平面互换） | `TempLogs/vis02_mutant2.xml`：20 例 **19 过 / 1 红**，红例 = `CullToViewMask_SphereOutsideEachOfSixPlanes_IsCulled`（左面外球显形），exit 2 | `cp` 回备份，`sha256=0dbfe27a…f6c23` 与变异前逐字节一致 |
| M2 掩码位序 | `mask.Set((uint)view)` → `mask.Set((uint)(MaxViewCount-1-view))` | `TempLogs/vis02_mutant3.xml`：20 例 **11 过 / 9 红**（多位序/账本用例连带红），exit 2 | 同上，sha256 一致 |

> M1 首轮曾用「仅 +X 侧」用例做变异，结果 19/19 全绿——**变异没抓住**。据此补了「六面逐一越界」对称用例，
> 再变异才红。这条记录保留，避免后续把「测试写了」当成「测试有效」。

### 3. 测试与校验（本任务全量亲跑）

| 段 | 结果 | 证据 |
|---|---|---|
| EditMode | **1255/1255 全绿**（0 failed / 0 skipped，15.2s） | `TempLogs/runtests_editmode.xml`（total=1255）；基线 1235 + 本任务 20 = 1255 |
| PlayMode | **115/115 全绿**（181.6s） | `TempLogs/runtests_playmode.xml` |
| arch | **PASS，asmdef 扫描 27 / 违规 0** | `TempLogs/arch_report.txt` |
| naming | **PASS，违规 0** | `TempLogs/name_report.txt` |
| 变异 | 两组均红、恢复后全绿 | `TempLogs/vis02_mutant2.xml`、`vis02_mutant3.xml` |

### 4. 风险 / 遗留（如实）

1. **未接线生产路径**：`CullToViewMask` / `ZrVisibilityFrame` 目前无运行时消费者（`ZrSceneBridge` 仍用
   `IsVisible` 的 bool 闸门）。这是「不挡路」的刻意选择——接线会改变现有场景绘制集合与像素基线。
   接线时的最小改法：在 `ZRenderSceneBridge` 提交前对一个 `DrawList` 等价物调用一次 `CullToViewMask`，
   写回共享 viewMask；账本断言放开发构建。
2. **视图上限 128 vs 原生 kMaxViews=8**：128 位是 Mask128 的物理宽度；原生 `VisibilitySet.kMaxViews=8`。
   低 8 位直接兼容（bit i 同义），超出 8 的视图在本期内核无法表达——如实记录为设计边界，不是缺陷。
3. **账本为诊断件**：每视图一个 `HashSet<int>`，非热路径；未做零分配优化（当前无运行时调用者）。
4. **相切判定无 epsilon**：靠平面归一化后的精确比较；对极端矩阵（近/远平面法线长度 <1e-6）判退化并
   fail-closed。若将来出现超长 far/near 比导致精度问题，需在调用方改用 double 平面，不在本任务范围。
5. 未改 Native、未改 asmdef、未改 TASKS/HANDOFF/WORKLOG/看板；未 commit。

## 验收清单（施工代理建议，验收代理逐条跑并出证据）

- V1 范围：git status 仅 `ZrSceneMath.cs`、`ZrVisibilityFrame.cs(.meta)`、`ZrViewCullingTests.cs(.meta)`；无 asmdef 增删（27）。
- V2 位序：读码确认 `mask.Set((uint)view)` 与 `ZkMask` 的 `Lo/Hi` 语义；跑 `CullToViewMask_ViewIndex64_LandsOnHighWord`。
- V3 边界：相切可见、`r=0` 点测试、`r<0` 按 0、NaN/无穷/退化矩阵 fail-closed 五条用例逐条核对并亲跑。
- V4 计数：`VisibilityFrame_SixViews_…` 断言 `Recorded=N`、每视图 `M_v ≤ N`、`ConsumedTotal > N`；重复/越界三例各自变红路径。
- V5 变异：独立复做 M1/M2（建议另换一面，如 `Top`，或 `i → i+1` 循环移位），确认必红并逐字节恢复。
- V6 全量：EditMode 1255、PlayMode 115、arch 27/0、naming 0 亲自复跑。
- V7 规范：三文件 UTF-8 无 BOM / LF（`file` + 字节检查）。
- V8 不挡路：确认 `ZRenderSceneBridge.cs` 零改动、PlayMode 像素类用例 115 全绿未受影响。

## 验收结论

**判定：PASS**（验收子代理独立复跑，2026-11-15 10:57–11:12 本地；未采信施工数字）

### A. 基线四段（本次亲跑，均以当场写出的证据为准）

| 段 | 结果 | 证据 |
|---|---|---|
| EditMode | **1264/1264 passed, 0 failed, 0 skipped**（13.979s） | `TempLogs/runtests_editmode.xml`（`start-time=2026-11-15 02:58:03Z`＝本地 10:58；末次恢复后复跑 `03:12`，同为 1264/1264） |
| PlayMode | **115/115 passed**（178.22s） | `TempLogs/runtests_playmode.xml`（`start-time 02:59:11Z`–`03:02:09Z`） |
| arch | **PASS，asmdef 27 / 违规 0** | `TempLogs/arch_report.txt`（`2026-11-15 11:02:53`） |
| naming | **PASS，违规 0** | `TempLogs/name_report.txt`（`2026-11-15 11:03:21`） |

运行器自报 `results.xml written by this run`（`Tools/run_tests.ps1` 的 freshness 判定），非陈旧文件。
基线前先把施工留下的 `TempLogs/*` 备份为 `acc_prev_*` 以便对照，二者数字一致。

### B. 语义逐条核对（读码 + 对应用例确实在 1264 内）

| 判据 | 代码位置 | 结论 |
|---|---|---|
| 位序（视图 i → bit i，≥64 落 Hi） | `ZrSceneMath.cs:256` `mask.Set((uint)view)`；`ZkMask.cs:30-37` 以 64 分界写 `Lo/Hi` | 一致；用例 `CullToViewMask_ViewIndex64_LandsOnHighWord` 断言 `Hi==1`、`Lo==0` |
| 恰好相切可见（闭集） | `ZrSceneMath.cs:288` `return distance >= -radius;` | 成立；`CullToViewMask_TangentExactly_IsVisible` 用 x=1.5/r=0.5（浮点精确 -0.5） |
| r=0 → 点测试 | `:246-249` 仅对 r<0 归零，r==0 自然退化 | 成立；`DegenerateRadius_BehavesAsPointTest` |
| r<0 → 按 0 | `:246-249` | 成立；`NegativeRadius_TreatedAsZero`（x=1.1/r=-5，若按 5 处理会误判可见） |
| NaN/∞ 输入 fail-closed | `:241` 入口守卫 + `:268`（IntersectsSphere 再查一次）+ `:283-286`（距离非有限即 false） | 成立；`NaNInputs_FailClosed` 三个输入面 |
| 退化矩阵 fail-closed | `PlaneFromRows :299-301` 长度 <1e-6 → `Plane(zero, NaN)`；`IsValid :223-230` 用 `IsFinitePlane :308-313` 拒 | 成立；`DegenerateFrustum_FailsClosed`（`Matrix4x4.zero`） |
| 空/负 viewCount | `:241` | 成立；`NullOrEmptyInputs_ReturnZeroMask` |

### C. 变异复红（我自己换的三种改法，与施工的 M1/M2 不同）

| 变异 | 改法 | 结果 | 红例 | 恢复 |
|---|---|---|---|---|
| **M-A 闭集→开集** | `ZrSceneMath.cs:288` `>=` → `>` | **1262/1264，1 段 FAIL，exit 2** | `CullToViewMask_TangentExactly_IsVisible`、`CullToViewMask_DegenerateRadius_BehavesAsPointTest` | `cp` 回备份，`sha256=0dbfe27a…f6c23` 逐字节一致 |
| **M-B 位序错一位** | `:256` `mask.Set((uint)view)` → `(uint)((view+1)&127)` | **1256/1264，8 红，exit 2** | `SphereInside` / `SphereOutsideEachOfSixPlanes` / `SphereBehindCamera` / `PerspectiveNearFar` / `TangentExactly` / `DegenerateRadius` / `MultipleViews` / `ViewIndex64` | 同上，sha256 一致 |
| **M-C 翻另一面符号** | `:218` `Far = PlaneFromRows(..., -1f, 2)` → `+1f` | **1262/1264，2 红，exit 2** | `CullToViewMask_SphereOutsideEachOfSixPlanes_IsCulled`、`CullToViewMask_PerspectiveNearFar_AreRespected` | 同上，sha256 一致 |

证据：`TempLogs/acc_vis02_mA_tangent.xml`、`acc_vis02_mB_bitorder.xml`、`acc_vis02_mC_farflip.xml`。
三组变异均**红**，且覆盖了施工暴露的盲区（只用「+X 单侧」用例时变异全绿）：

- 施工自曝的「首轮变异 19/19 全绿」**可信且已被补齐**——M-A 恰好打在他们补的「六面逐一越界」之外的相切/退化半径两条上，
  证明补的不是同一批用例；M-B 的错位位移（而非施工的 127-i 反序）同样被 `ViewIndex64`/`MultipleViews` 抓住。
- 恢复后末次全量 **1264/1264 全绿**，`git diff --stat` 仍只有 `ZrSceneMath.cs | 150 +`（本任务自身增量），无残留改动。

### D. 规范 / 范围

- 三个文件无 BOM（首字节 `2f2f20`）；工作区为 CRLF 但 `.gitattributes` `* text=auto eol=lf` 归一后 blob 哈希与「CRLF 全删」版本一致
  （实测 `ZrVisibilityFrame.cs` `git hash-object` = 手算 LF blob `4866cce6…`）→ **入库为 LF**。
- `git status` 本任务仅 `ZrSceneMath.cs`(M) + `ZrVisibilityFrame.cs(.meta)`(??) + `ZrViewCullingTests.cs(.meta)`(??)；无 asmdef 增删（arch 实测 27）。
- `ZRenderSceneBridge.cs` 零改动；PlayMode 115 全绿未受影响。

### E. 未做 / 保留意见（不影响判定，登记在案）

1. **`ZrVisibilityFrame.Validate` 的「M_v ≤ N」是恒真断言**：`_consumedByView[v]` 的元素必属 `_recorded` 子集，
   `:148-155` 那段检查在结构上永不可能触发。计量本身没问题，但这条不变量不构成独立闸门（读代码即可判定，建议后续删除或换成可证伪口径）。
2. **`CullToViewMask` 未防 `views.Length < viewCount`**：`:252-258` 只挡 `views == null`，数组短于 viewCount 会 `IndexOutOfRange`（现无调用者，未加用例）。
3. **未接线生产路径**：如实（`ZrSceneBridge` 仍用 `IsVisible` bool 闸门），与施工 §4-1 一致，本任务判据允许。
4. 施工 §4-4「相切无 epsilon」保留意见成立：`distance >= -radius` 对极端 near/far 比依赖平面归一化精度。

## 接线（2026-11-15）

**结论**：`CullToViewMask` / `ZrVisibilityFrame` 从"库函数"变成真在用——`ZRenderSceneBridge.Submit` 改成
两阶段（批量掩码 → 只对掩码为 1 的条目做 mesh/矩阵/绘制），drawn / skippedFrustum 的**计数口径逐位不变**
（证据见 §3），并且找到并关闭了这一刀最大的隐患：**包围球版掩码不能直接替换 AABB 判据**（见 §1-1）。

### 1. 替换前后语义对照

| 项 | 替换前（HEAD） | 替换后 | 是否等价 |
|---|---|---|---|
| 视锥判据 | `GeometryUtility.CalculateFrustumPlanes(camera, _planes)` → 逐条目 `ZrSceneMath.IsVisible(_planes, renderer.bounds)` | 同一份 `_planes` → `ZrViewFrustum.FromPlanes(_planes)` → 逐条目 `CullToViewMask(_views, 1, bounds)` 取第 0 位 | **是**（平面是同一份数，不重算矩阵；AABB 半径按 Σ\|n_i\|·e_i 复算，与 `TestPlanesAABB` 同式） |
| 判定顺序 | 禁用 → 层 → 视锥 → mesh → 绘制 | 阶段一算掩码；阶段二 禁用 → 层 → **掩码** → mesh → 绘制 | **是**（顺序刻意不变，见下） |
| 计数口径 | `skippedDisabled / skippedLayer / skippedFrustum / skippedMesh / drawn` | 同名五个数 | **是**（表达式与短路顺序一字未改，只把第 3 条的条件换成 `!_visibleScratch[i]`） |
| 视图数 | 隐式 1（管线只 `Submit(cameras[0])`） | 显式 1（`_views` 恒长 1，只用第 0 位） | 是（多相机策略未动） |
| 坏相机（NaN 矩阵） | `TestPlanesAABB` 对 NaN 平面判**可见**（`d+r<0` 为 false）→ 全画 | 掩码 fail-closed → 全剔 | **否，刻意**：唯一行为差异，只在相机矩阵已退化时出现（此时旧路径画的是垃圾），登记为改进而非回归 |
| 每帧分配 | 零（复用缓冲） | 零（`_boundsScratch` / `_visibleScratch` / 账本容器全复用；账本 `BeginFrame` 改为清内容而非重建 HashSet） | 是 |

**1-1 为什么新增 `CullToViewMask(..., Bounds)` 重载（本任务最关键的一处判断）**：现有球版用
`r = |extents|`，而 AABB 在平面法线上的真实投影半径是 `Σ|n_i|·e_i ≤ |n||e|`（Cauchy–Schwarz）——
球版是 AABB 的**超集**，直接拿它替换 `IsVisible` 会把视锥边缘的物件多画进来（drawn 变、像素基线变）。
故新增 AABB 重载（逐面复算 `Σ|n_i|·e_i`，`d + r >= 0` 判可见，相切可见），球版原样保留（原有 20 例仍绿）。

**1-2 阶段二为什么还留着 enabled/layer 判定**：这两条是只读属性、不产生绘制成本，但它们决定
`skippedDisabled / skippedLayer` 的短路位置。把掩码判定提到它们之前，会让"既禁用又出视锥"的条目
从"禁用"改记成"视锥"，计数口径就不是逐位一致了。所以真正昂贵的部分（mesh 句柄创建、世界矩阵、
`DrawMesh`）严格只在掩码为 1 的条目上执行，而两个只读判定留在掩码之前——判据"计数不变"优先。

### 2. 分块策略（>128 条不许截断）

- 批大小 `ZrSceneMath.CullBatchSize = 128`（= 掩码物理宽度），`CullToViewVisibleFlags` 每批算一个 128 位字：
  批内条目 `j<64` 落 Lo 第 `j` 位、否则落 Hi 第 `j-64` 位（与 `ZkMask`/`Mask128` 同布局），批末再回读掩码。
- 条目数 384（3 个整批）用例断言：可见数恰为 128、384 个可见位逐条与逐条判定一致、
  批边界（126/129、255/258、381 可见；127/128、256/257、383 不可见）两侧都不漏。
- 容量不足（`visible.Length < bounds.Count`）→ **fail-closed 返回 0 且不写越界**（宁可不画也不画一半），有独立用例。
- 桥侧的掩码缓冲只在条目数增长时换一块（不每帧 new）。

### 3. 等价性证明（8 例；四组随机各 ≥200 + 两条结构）

| 组 | 用例 | 规模 | 结果 |
|---|---|---|---|
| A | `MaskDecision_MatchesIsVisible_ForRandomCamerasAndBounds` | 220 个随机相机位姿（透视/正交、FOV 15–110°、near/far 随机）× 每组 3 个包围盒（含超大盒、单维为 0 的退化盒）= **660 例** | 可见 220 / 剔除 440，**逐例判据分歧 0** |
| B | `MaskDecision_MatchesIsVisible_ForTangentBounds` | **220 组**与随机相机某平面"恰好相切"的盒子（d+r≈0） | 逐位一致 210，边界膜内 1 ULP 分歧 10（掩码单方可见 8 / 旧判据单方可见 2）——见 §3-1 |
| B′ | `TangentExactly_AxisAligned_IsVisibleInBothPaths` | 轴对齐精确相切（d+r 二进制恰为 0）+ 越过 1/1024 | 两条判据都判可见 / 都判剔除 |
| C | `MaskDecision_MatchesIsVisible_BeyondFarPlane` | **220 组**远平面外（margin 0.01–50m） | 全部剔除，分歧 0 |
| D | `MaskDecision_MatchesIsVisible_ForDegenerateBounds` | **220 组**退化半径（零尺寸点 / 单维为 0） | 可见 47 / 剔除 173，分歧 0 |
| E | `CullToViewVisibleFlags_MoreThan128Entries_DoesNotDropCrossChunkEntries` | 384 条 = 3 批 | 可见 128，逐条位一致，批边界两侧不漏 |
| E′ | `CullToViewVisibleFlags_BufferTooSmall_FailsClosed` | 容量不足 / null | 返回 0，不写越界 |
| F | `TwoPhaseCounters_EqualLegacyCounters_ForMixedEntries` | **512 条**合成条目（enabled/layer/bounds/hasMesh 随机），旧循环 vs 新循环各跑一遍 | 禁用/层/视锥/无mesh/drawn **五个数逐位相同**，且四类样本都非空（防空转） |

**3-1 相切处的 1 ULP 分歧（诚实注记，不许当成"判据一致"糊过去）**：先用临时探针（已删）在 2400 个
相切样本上比对 Unity 原生 `TestPlanesAABB` 与三种自算写法（float `d+r>=0`、double `d+r>=0`、float `d>=-r`）：
分歧 94 / 93 / 93 例，方向随机。原因是 `d+r` 本身在 ±1 ULP 量级，两条实现的舍入路径不同——
**这是浮点最后一位，不是判据差异**。故 B 组断言收敛为三条可证伪的口径：①分歧只允许落在
`|d+r| ≤ 1e-6·(1+|d|+r)` 的边界膜内（膜外必须逐位相等）；②分歧数 ≤ 样本 1/10（实测 10/220）；
③用轴对齐、二进制精确的相切用例（B′）单独钉住"相切可见"的闭集语义。
生产含义：`renderer.bounds` 精确压在视锥平面上是零测事件（A/C/D 三组共 1100 例分歧 0），
此膜对应 1e-6 相对量级（约 1 µm/m），不影响任何实际绘制集合。

### 4. drawn / culled 前后数字

| 口径 | 接线前（存档） | 接线后（本任务复跑） | 一致 |
|---|---|---|---|
| planet3（原生 chunk 剔除） | `drawn=4 culled=26 vbytes=1468416`（`Tools/Probes/out/planet3-acceptance-run.log`） | `drawn=4 culled=26 vbytes=1468416`（`TempLogs/vis02_planet3_after.log`，exit 0） | **逐位一致** |
| planet-budget（1080p 300 帧） | `drawn 4..4 culled 26..26 vbytes=1468416`（`out/planet-budget-2026-11-14T00-22-38.json`） | 同上（`TempLogs/vis02_pbudget_after.log`，新档 `out/planet-budget-2026-11-15T06-49-32.json`） | **逐位一致** |
| Unity 桥（`Submit` 内部的 drawn/skippedFrustum） | 旧循环 | 新循环（用例 F 逐数比对） | **逐位相同** |
| Unity 桥（决策级，1100 例真实相机/包围盒） | `IsVisible` | `CullToViewMask` 第 0 位 | **分歧 0** |

**边界说明（如实）**：planet 探针走的是原生 chunk 剔除，本次改动只在 Unity C# 侧，且探针二进制/dll
未重建（时间戳仍 11-14）——这组数字证明"原生侧未被本任务触碰"，**不是** C# 桥接线前后的对照。
桥侧的"前后一致"由用例 F（计数口径）+ A/C/D（决策级 1100 例 0 分歧）承担；没有重建 Player 做
端到端前后截图对比（避免把 `Build/` 产物与 version.json 拖进本次改动）。

### 5. 变异复红（掩码判定写反）

| 变异 | 改法 | 结果 | 恢复 |
|---|---|---|---|
| M-VIS 掩码取反 | `ZrSceneMath.CullToViewVisibleFlags`：`if (!CullToViewMask(...).Has(0u)) continue;` → 去掉 `!` | **1276 总例 / 2 红**，红例 `CullToViewVisibleFlags_MoreThan128Entries_DoesNotDropCrossChunkEntries`、`TwoPhaseCounters_EqualLegacyCounters_ForMixedEntries`，exit 2（`TempLogs/vis02_wiring_mutant_flip.xml`） | `cp` 回备份，恢复后末次全量 1276/1276 全绿 |

> 只红了 2 例是正确的：A/C/D 组直接调 `CullToViewMask`（不经分块包装），本来就不该被这条变异影响；
> 被影响的是"批量分块 + 计数口径"这两条——正是本任务新增的行为面。

### 6. 测试与校验（本任务亲跑）

| 段 | 结果 | 证据 |
|---|---|---|
| EditMode | **1276/1276 全绿**（0 failed / 0 skipped，15.7s）；基线 1268 + 本任务 8 例 | `TempLogs/runtests_editmode.xml` |
| PlayMode | **115/115 全绿**（180.6s） | `TempLogs/runtests_playmode.xml` |
| arch | **PASS，asmdef 扫描 27 / 违规 0** | `TempLogs/arch_report.txt` |
| naming | **PASS，违规 0** | `TempLogs/name_report.txt` |
| 变异 | 掩码取反 → 2 红 → 恢复 → 全绿 | `TempLogs/vis02_wiring_mutant_flip.xml` |

### 7. 周期日志（一行，不刷屏）

`Time.frameCount % 300 == 0` 且 `entries > 0` 时打一行（`drawn == 0` 时保留原"本帧无绘制"抬头）：

```
[ZRender] 帧统计：entries=N 绘制=D 禁用=a 层=b 视锥=c 无可用mesh=d 录入=N 消费=M
```

账本口径：录入 = 本帧 entry 数（每物件一次，与视图数无关）；消费 = 掩码为 1 且通过 enabled/layer
进入绘制流程的条目数。每帧断言 `M ≤ min(N, 掩码可见数)`，违规打一次 `LogError`（不重复刷）。

### 8. 风险 / 遗留（如实）

1. **per-entry 成本上升**：`CullToViewMask` 每次调用都做一遍 `IsValid`（六平面有限性 + 归一化检查），
   比旧的 `TestPlanesAABB` 单次调用贵约 2 倍常数项；条目上千时按 60fps 计仍在微秒级，但这是"批量版
   按条目调用"的结构代价，未做"整帧只校验一次"的短路优化（留待有实测压力时再做）。
2. **`renderer.bounds` 的计算范围变大**：阶段一对**全量** entry 取 bounds（含禁用/异层条目），
   旧路径只对通过 enabled/layer 的条目取——属性读取成本小幅上升，换取"全量算掩码"的口径。
3. **相切 1 ULP 膜**（§3-1）：不是缺陷，但请勿把 B 组读成"相切处逐位相等"——它是"膜内允许、膜外必须相等"。
4. **账本仍是诊断件**：`ZrVisibilityFrame` 为单视图场景服务，`ConsumedTotal`/多视图口径仍无运行时消费者；
   `Validate` 的"M_v ≤ N"恒真问题（验收 E-1）未改，桥内改为直接读 `Recorded` / `ConsumedForView(0)` 断言。
5. 未 commit、未改看板、未改 `ZRenderPipeline` 多相机策略、未改 Native/asmdef；
   PlayMode 证据图（`Docs/Parity/*.png`）被测试重写后已 `git checkout` 还原，不在本次改动范围内。

## 接线验收结论（验收子代理独立复跑，2026-11-15 15:09–15:31）

**判定：PASS（接线可闭环）**——但**不采信施工数字**：下面每条都是本次当场跑出来的；另有一条与施工/派发口径的**实质分歧**（§C-3）必须登记，不影响通过。

### A. 范围与恢复（先做，确保后续数字是"同一棵树"）

- `git status` 施工面之外的增量只有本次证据文件（`TempLogs/acc_*`、`Tools/Probes/out/frame-baseline-*.json`），**无临时测试文件残留**：
  自写的 `AccVis02EquivTests.cs`、`AccVis02LogCadenceTests.cs` 均已在跑完后删除（含 `.meta`），最终 `git status` 里不出现。
- `ZrSceneMath.cs` 变异前后 sha256 = `844251775FA4BE7E0E566FAF0356E9697ECBE41B9BF01EB4B86C91B49E9ED9D4`（与备份逐字节一致）；
  `ZRenderSceneBridge.cs` 变异前后 sha256 = `3F652B987A38549015EC9CF74216010B818B068EB89176B9CA6010F75A5890CC`（同上）。
- PlayMode 测试重写的 `Docs/Parity/*.png`（crowd_r9_15_off/on、npc_attitude_divergence、utility_*）已 `git checkout --` 还原；结束时无 `ZFrame.exe`/`Unity.exe` 残留，`UnityProject/Temp/UnityLockfile` 不存在。

### B. 读码核对（逐条给位置）

| 判据 | 位置 | 结论 |
|---|---|---|
| 两阶段顺序 | `ZRenderSceneBridge.cs:166` `int visibleEntries = CullVisibleEntries();` 在逐条目循环之前；循环体内 `:202` 只读 `_visibleScratch[i]` | 成立：掩码先整批算完，阶段二只读 |
| `enabled`/`layer` 仍在掩码之前 | `:188`（renderer null）→ `:192`（!enabled）→ `:196`（layer）→ `:202`（掩码） | 成立：短路顺序与旧路径一字不差，`skippedDisabled/skippedLayer/skippedFrustum` 不会串味 |
| 昂贵部分只在掩码为 1 时执行 | `GetOrCreateMeshHandle`/矩阵/`DrawMesh` 全在 `:202` 之后 | 成立 |
| 分块 128 | `ZrSceneMath.cs:363` `CullBatchSize = MaxViewCount`(=128)；`:385-391` 每批 128 条、批内 j<64 落 Lo 否则 Hi | 成立（>128 不截断，384 条用例覆盖 127/128/255/256/381/383 边界） |
| `M ≤ N` 断言是否真会报错 | 桥 `:244-250`：`consumed > recorded || consumed > visibleEntries` → `LogError` 一次；`ZrVisibilityFrame.Validate` 的 `M_v ≤ N`（`:157-164`）仍是**恒真** | **桥内断言有牙、账本内那条恒真**——见 §C-4 的实证与诚实注记 |

### C. 等价性与变异（自写用例 + 自有改法）

**C-1 自写等价性用例（`TempLogs/AccVis02EquivTests.cs`，跑完即删；证据 `TempLogs/acc_equiv_selfwritten.xml`）**

| 组 | 构造 | 规模 | 结果 |
|---|---|---|---|
| S1 | 随机位姿（透视/正交、FOV 20–100°、near/far 随机）× 随机盒（含一维为 0、超大盒） | 220 | 可见 65 / 剔除 155，**分歧 0** |
| S2 | **相机在物体内部**（盒心=相机±1m，尺寸 4–40m） | 80 | 全部可见，**分歧 0** |
| S3 | **远平面外**（相机局部系沿 +Z 退 far+4–60m，margin > 盒最大投影半径 2.6m） | 80 | 全部剔除，**分歧 0** |
| S4 | **退化半径**（零尺寸点 / 单维为 0） | 80 | 可见 15 / 剔除 65，**分歧 0** |
| S5 | 分块结构：300 条随机条目，`CullToViewVisibleFlags` vs 逐条 `CullToViewMask` | 300 条 | 可见 88，**逐条位一致** |

合计 **760 例/条，判据分歧 0**（判据两侧都是生产函数：`ZrSceneMath.IsVisible` vs `CullToViewMask(...,Bounds).Has(0)`）。
> 记录一次自写用例自身的构造错误（不是产品缺陷）：首版 S3 用**世界系**横向偏移，横向分量投影到视轴会把盒子拉回锥内（例 21 差 0.11m 判可见）；改成相机局部系后归零。第二版 margin 0.5–60 又踩到"margin < 盒投影半径"，收紧到 ≥4 后全绿。**先证构造没写错，再谈判据一致。**

**C-2 变异复红（三组，全部逐字节恢复）**

| 变异 | 改法 | 结果 | 红例 |
|---|---|---|---|
| **M1 掩码判定取反** | `ZrSceneMath.cs:397` 去掉 `!`（可见位反相） | `TempLogs/acc_m1_maskflip.xml`：**28 例 / 2 红**，exit 2 | `CullToViewVisibleFlags_MoreThan128Entries_…`、`TwoPhaseCounters_EqualLegacyCounters_…` |
| **M2 去掉跨块** | `:387-391` 的 `if (count > CullBatchSize)` 关掉（单批吃掉全部条目） | `acc_m2_nocrosschunk.xml`：**28 例 / 2 红**，断言"384 条里应有 128 条可见"（位错位导致丢/错置位） | 同上两条 |
| **M3 批大小 128→127** | `:363` 常量改 127 | `acc_m3_batch127.xml`：**28 例全绿** | 无 |

**C-3 与派发口径的分歧（必须登记）**：派发要求"分块大小改成 127 **或**去掉跨块 → 必须红"。实测 **M2 红、M3 绿**，且 M3 的绿是**正确行为不是测试漏洞**：
127 条一批仍然"批内位号 j 从 0 起、批末按批回读"，条目一条不丢、判据一字不变，只是批拓扑变了（`start` 步长 127 而非 128）。
真正会坏的是"一批装不下仍按 128 位编址"（M2：j 越过 63 后 `1ul << (j-64)` 位移溢出，位串位），M2 已被现有用例抓住。
若要把 128 也钉成不变量，需另加 `Assert.AreEqual(MaxViewCount, ZrSceneMath.CullBatchSize)` 之类断言——**本轮未加**（不改代码纪律），登记为可选项。

**C-4 `M ≤ N` 断言实证（不是"读起来像有牙"）**

临时 PlayMode 用例造 1 个视锥内 + 2 个相机背后的物件（entries=3、掩码可见=1），把 `TryConsume` 从掩码之后**提到**掩码之前：

- 正常代码：日志 `本帧统计：entries=3 绘制=1 … 视锥=2 录入=3 消费=1`，账本报错 **0** 次（`acc_cadence_restored.log`）。
- 变异后：`可见性账本不一致：录入=3 消费=3 掩码可见=1（应当 M ≤ min(N, 掩码可见)）。` **恰好 1 次**（只报不刷），且该 PlayMode 用例因 `LogError` 判失败（`acc_cadence_mut.log`/`.xml`）。
- 恢复后 sha 一致、复跑 0 报错。

诚实注记：`ZrVisibilityFrame.Validate` 自己的 `M_v ≤ N`（`:157-164`）在结构上**永不可能触发**（消费集合必是录入集合的子集）——与前一轮验收 E-1 同一结论，**本次未改**；桥内那条是独立比较，才是有牙的那条。

### D. 行为未变（计数逐位）

| 口径 | 归档（2026-11-14） | 本次亲跑 | 一致 |
|---|---|---|---|
| planet3（原生 chunk 剔除） | `drawn=4 culled=26 vbytes=1468416`（`Tools/Probes/out/planet3-acceptance-run.log`） | `drawn=4 culled=26 vbytes=1468416`，exit 0（`TempLogs/acc_planet3.log`） | **逐位一致** |
| 桥内 drawn/skippedFrustum | 旧循环 | 新循环（用例 F，512 条混合条目，五个计数逐位相同） | 逐位一致（1264→1276 全量内） |

边界说明同施工 §4：planet3 走原生、二进制未重建（时间戳仍 11-14），它证明的是"原生侧未被本任务触碰"；**桥侧前后一致的证据是决策级等价（§C-1 的 760 例 0 分歧）+ 计数口径用例 F**，不是端到端前后截图。

### E. 周期日志节奏（亲测，不只看代码）

- 代码：`Time.frameCount % 300 == 0 && _entries.Count > 0` 才打一行（`ZRenderSceneBridge.cs:252-259`）；diff 里没有别的无闸 `Debug.Log`。
- 实测（`TempLogs/acc_cadence_restored.log`）：700 帧（11..711）内共 **4 行**，成两簇（对应 frameCount=300 与 600 两个边界），每簇 2 行——因为该临时用例**每帧显式 `cam.Render()` + 测试框架自身渲染各触发一次 Submit**；生产路径每帧一次 Submit，等价于**每 300 帧一行**。单行输出、不刷屏、格式为
  `[ZRender] 本帧统计：entries=N 绘制=D 禁用=a 层=b 视锥=c 无可用mesh=d 录入=N 消费=M`。
- 受限：`Build/Windows64/ZFrame.exe` 构建于 **11-14 13:17**，早于本次接线，播放器里没有这段日志；PlayMode 探针场景 `renderer=0` → `entries=0` 被条件抑制。故"每 300 帧一行"是**代码 + 编辑器内实测**双重证据，不是播放器端到端。

### F. 四段回归（本次亲跑，证据当场写出）

| 段 | 结果 | 证据 |
|---|---|---|
| EditMode | **1276/1276 passed, 0 failed, 0 skipped**（15.08s；`start-time=07:09:57Z`） | `TempLogs/acc_runtests_editmode.xml` |
| EditMode（恢复后复跑） | **1276/1276**（14.56s；`07:22:55Z`） | `TempLogs/acc_editmode_final.xml` |
| PlayMode | **115/115 passed**（176.01s；`07:10:54–07:13:50Z`） | `TempLogs/acc_runtests_playmode.xml` |
| arch | **PASS，asmdef 27 / 违规 0**（15:14:28） | `TempLogs/arch_report.txt` |
| naming | **PASS，违规 0**（15:14:53） | `TempLogs/name_report.txt` |

> 跑法说明（如实）：`Tools/run_tests.ps1` 在本代理宿主环境下起不来 Unity——`Start-Process` 建环境块时撞上宿主注入的 `HTTPS_PROXY/https_proxy` 大小写重复键（脚本自己诊断了这条），四段全 FAIL 且把根目录 `TestReport.txt` 覆盖成那份 FAILED（该文件未入库、是包装器产物，**不是测试失败**）。随后改为**直连 Unity.exe（经 Git Bash）**逐段跑，参数与 `run_tests.ps1` 一致（`-batchmode -runTests` 不带 `-quit`，起跑前删 `UnityLockfile`），结果即上表。

### G. 未做 / 保留意见

1. **端到端播放器口径未闭环**：未重建 Player（构建早于接线），"每 300 帧一行"与"玩家场景 drawn 不变"没有播放器端截图/日志对照；桥侧由决策级等价 + 计数用例承担。
2. **M3（127）不红**属正确行为（见 §C-3），若要求"批大小必须等于 128"需另加断言，本轮按"不改代码"未加。
3. **相切 1 ULP 膜**沿用施工/上轮口径（自写用例刻意不构造相切，避免把膜内抖动写成判据差异）；膜本身仍未在生产端量化。
4. `ZrVisibilityFrame.Validate` 的 `M_v ≤ N` 恒真（沿用 E-1），未修。
5. 本次新增的 `Tools/Probes/out/frame-baseline-*.json`（门禁四轮跑产生）留在原地作证据，未清理、未入库。

