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

