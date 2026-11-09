# TASK-A-ASM-03 —— 装配玩法化（第一刀：建造模式 + 蓝图存档）

> 出处：`Docs/TECH-VALIDATION.md` §7 建议顺序第 1 项。装配的底层（吸附/关节/拓扑/快照/重建）
> 已在 A-TECH-02/05 闭环，只缺「玩家能在游戏场景里用它」这一层。
> 本刀只做**建造与存档**；载具驾驶（控制舱/轮驱/力矩）留给下一刀。

## 一、现状（施工前必读，别重造轮子）

- `Game/View/Assembly/AssemblyBuilder.cs`：`TryFindSnap` / `Align` / `Connect` / `Disconnect` / `GetComposite`。
- `Game/View/Assembly/AssemblyPartFactory.cs`：`CreateCube / CreateThruster / CreateWheel / CreateFuelTank / CreateStructuralBeam` 等（返回已挂点的 `AssemblyPart`）。
- `Game/View/Assembly/AssemblyGraphSync.cs`：`Capture(AssemblyGraph)` 把场上零件抓成图。
- `Game/View/Assembly/AssemblySnapshotCodec.cs`：`ToSnapshot(graph,name)` / `Save(snapshot)` / `Load(name)` / `Delete(name)` / `PathFor(name)`。
- `Game/View/Assembly/AssemblyRebuilder.cs`：`Rebuild(snapshot)` — 会用 `AssemblyBuilder.Connect` 复原关节。
- 现成演示场景 `SCN_Assembly`；本任务要把它搬进 **SCN_Game**。
- 输入：`activeInputHandler: 2`（Both），可用旧 Input（`SurvivalLoop` 已在用）。

## 二、目标

1. **建造模式**：SCN_Game 里按 **B** 进入/退出；进入后显示幽灵预览，鼠标指哪放哪。
2. **零件切换**：Q/E 循环 5 种零件（Cube / StructuralBeam / Thruster / Wheel / FuelTank）。
3. **吸附与连接**：靠近已有零件挂点（≤1.2m）时预览吸附到该点，放下即 `Connect`；否则自由放置。
4. **删除**：右键删除鼠标下的零件（先 `Disconnect` 所有关节再销毁）。
5. **蓝图**：K 保存到槽位 `slot1`；L 载入（清空现有 → `Rebuild`）。
6. 待机不建可见物（与 A-LOOP-01 同一纪律，避免污染像素用例）。

## 三、设计要点

- 新文件 `Game/View/Build/BuildMode.cs`（MonoBehaviour）：
  - 与 `SurvivalLoop` 一致的挂载方式：`sceneLoaded` 门控 `SCN_Game`，`DontDestroyOnLoad` 宿主；
  - **默认关闭**（不建幽灵、不显示提示），B 切换；
  - `Update` 里做鼠标射线（`Camera.main.ScreenPointToRay`）→ 与 y=0 平面求交得落点；
  - 吸附候选：`AssemblyBuilder.TryFindSnap`（先看其签名与 filter 参数，按实际调用）；
  - 放置：工厂创建 → 若吸附则 `Connect`；零件 id 用递增计数（`build-0001`），保存时稳定；
  - 删除：从鼠标射线 `Physics.Raycast` 到 `AssemblyPart`，`Disconnect` 其所有关节后销毁；
  - 存档：`new AssemblyGraph()` → `AssemblyGraphSync.Capture(graph)` → `ToSnapshot` → `Save`；
  - 读档：`Load("slot1")` → 清空现有零件（含关节）→ `AssemblyRebuilder.Rebuild`；
  - 幽灵预览：用工厂建一个零件后禁用其 `Rigidbody`/碰撞并染半透明色；**每次切换零件只重建一次**，不要每帧 new。
- HUD 用 IMGUI（与 SurvivalLoop 一致）：显示当前零件、操作提示、存档/读档结果。
- 不接 `AssemblyDragController`（那是拖拽已有零件，属于驾驶/编辑第二阶段）。

## 四、白名单

- `UnityProject/Assets/Game/View/Build/**`（新增）
- `UnityProject/Assets/Game/Tests/PlayMode/BuildModePlayModeTests.cs`（新增）
- `Docs/Logs/TASK-A-ASM-03.md`、`TASKS.md`（仅本任务行）

**不动**：`SurvivalLoop`、`AssemblyBuilder/Factory/Rebuilder/SnapshotCodec` 的既有语义（只调用，不修改；确需小改必须先说明理由）。

## 五、验收清单

| # | 断言 | 判据 |
|---|---|---|
| V1 | 开关幂等 | B 进入后 `IsBuilding=true`、再按 B 退出；重复调用不重复建幽灵 |
| V2 | 零件切换 | Q/E 在 5 种之间循环，幽灵的 `Kind` 随之变化 |
| V3 | 放置与连接 | PlayMode：在 (1,0,0) 与 (3,0,0) 各放一个 Cube → `GetComposite` 返回 2 个零件 |
| V4 | 删除 | 右键删除其中一个 → 场上剩余 1 个，其 `Joints` 为空 |
| V5 | 蓝图往返 | 放 2 个并联接 → K 保存 → 清空 → L 载入 → 仍是 2 个零件、1 条关节 |
| V6 | 变异复红 | 让存档跳过 `Capture`（存空图）→ V5 必红；恢复后全绿 |

## 六、施工报告

### 6.1 摘要

本刀只做「玩家能在场景里建造 + 存蓝图」这一层，装配底层一行语义未改，全部按既有 API 调用：

- **新增 `Game/View/Build/BuildMode.cs`**（MonoBehaviour，736 行，含注释）：与 `SurvivalLoop` 同一挂载纪律
  ——`sceneLoaded` 门控 `SCN_Game`、`DontDestroyOnLoad` 宿主、**默认关闭**；`OnGUI` 在关闭态直接 return，
  待机零可见物、零零件、零 HUD（像素用例零影响）。
- **操作**：B 开/关建造｜Q/E 循环 Cube→StructuralBeam→Thruster→Wheel→FuelTank｜左键放置在鼠标射线与
  y=0 平面的交点｜右键射线删除｜K 存 `slot1`｜L 清空后载入。
- **吸附**：落点 1.2m 内用 `AssemblyBuilder.TryFindSnap`（`AssemblyPartTypeFilter(自己, null)`）找最近的
  空闲兼容挂点；命中则取自己离目标最近的空闲点，`AssemblyBuilder.Connect` 完成对齐 + 关节。超出即自由放置。
- **幽灵预览**：工厂建一次、`isKinematic` + 关全部 Collider + 半透明 URP 材质；**只在零件种类变化时重建**
  （`GhostBuildCount` 可证）；`AssemblyPart` 组件被 `enabled = false`，因此幽灵不在 `AllParts` 里——
  吸附搜索、右键删除、蓝图 `Capture` 都看不到它。
- **删除**：先把该件的所有 `AssemblyPart.Joints` 逐条 `AssemblyBuilder.Disconnect`（另一侧当帧松手），再
  `SetActive(false)`（立刻离开静态注册表，不信帧末 Destroy）+ `Destroy`。
- **存档**：`new AssemblyGraph()` → `AssemblyGraphSync.Capture` → `AssemblySnapshotCodec.ToSnapshot(graph,"slot1")`
  → `Save`；**不自己造序列化**。读档：先 `Load`（文件缺失不清场）→ 清空场上零件 → `AssemblyRebuilder.Rebuild`
  → 按 `build-NNNN` 续推递增序号，避免读档后放件撞 id。
- 未接 `AssemblyDragController`，未新增 Input System action（全部旧 `Input`），未动 `SurvivalLoop` 与既有装配类。

### 6.2 改动清单

| 文件 | 类型 | 说明 |
|---|---|---|
| `UnityProject/Assets/Game/View/Build/BuildMode.cs` | 新增（736 行） | 建造模式本体；sha256 `9c94df65…73be` |
| `UnityProject/Assets/Game/View/Build/BuildMode.cs.meta` | 新增（Unity 自动生成） | 新目录 `Build/` + `.meta` |
| `UnityProject/Assets/Game/Tests/PlayMode/BuildModePlayModeTests.cs` | 新增（334 行） | V1–V5 五条 PlayMode 用例；sha256 `0458ab60…585d` |
| `UnityProject/Assets/Game/Tests/PlayMode/BuildModePlayModeTests.cs.meta` | 新增（Unity 自动生成） | — |
| `Docs/Logs/TASK-A-ASM-03.md` | 修改 | 本报告 |

未改：`AssemblyBuilder` / `AssemblyPartFactory` / `AssemblyPart` / `AssemblyGraphSync` / `AssemblySnapshotCodec` /
`AssemblyRebuilder` / `SurvivalLoop` / `TASKS.md`（看板里目前没有 A-ASM-03 行，按白名单不新增）。
编码：UTF-8 无 BOM + LF（已 `od` 校验首字节、`grep -c $'\r'` = 0）。未 commit。

### 6.3 自测数字（真跑，证据 `TempLogs/runtests_*.xml`）

| 段 | 命令 | 结果 | 基线 |
|---|---|---|---|
| EditMode | `Tools/run_tests.ps1 -Segments editmode` | **1113 / 1113 通过，0 失败**（12.07s） | 1113（持平） |
| PlayMode | `Tools/run_tests.ps1 -Segments playmode` | **98 / 98 通过，0 失败**（69–70s） | 93（**+5**，即本次 5 条） |

设备纪律：跑前 `tasklist` 无 Unity、删 `UnityProject/Temp/UnityLockfile`；测试不带 `-quit`；两次都以
`results.xml` 落地后 Unity 自行 exit 0 收尾，无需 taskkill。
环境处置：首次启动被 `Duplicate case-variant variables {HTTPS_PROXY/https_proxy}` 拦下（交接档案 §3.2 已知坑），
按档案清理 8 个大小写代理变量后重跑即通过——脚本与仓库未改。

新增 5 条用例（xml 均为 `result="Passed"`）：

- `V1_ToggleIsIdempotent_AndIdleBuildsNothing`（默认关闭、待机无幽灵/无零件、重复 SetBuilding(true) 不重建）
- `V2_CyclePartKinds_RebuildsGhostOncePerSwitch`（5 件循环 + 反向 + 空闲帧 GhostBuildCount 不变）
- `V3_PlaceTwoCubes_ThenSnapConnectsIntoComposite`（自由放置 + 吸附连接 + 缝 0.015m + 外法线相对）
- `V4_DeleteDisconnectsFirst_ThenRemovesFromScene`（删 owner 侧：对面 `Joints` 当帧清零、注册表当帧 -1）
- `V5_BlueprintRoundTrip_KeepsTwoPartsAndOneJoint`（2 件 1 关节往返 + id 原样 + 读档后续号 0003）

**V6 变异复红（已实做）**：把 `SaveBlueprint` 里的 `AssemblyGraphSync.Capture(graph)` 一行注释掉（存空图）→
PlayMode **98 总 / 97 过 / 1 红**，红的正是 `V5_BlueprintRoundTrip_KeepsTwoPartsAndOneJoint`
（断言 `saved.Parts.Count == 2` 失败）。恢复后 sha256 回到 `9c94df65…73be`，复跑 **98/98 全绿**。

### 6.4 风险与未验证

1. **V3 原文坐标与 1.2m 吸附半径算术冲突（需验收按语义认可）**：`(1,0,0)` 与 `(3,0,0)` 相距 **2.0m**，
   大于 1.2m，按契约不可能同件吸附。用例因此拆成两段、坐标原样保留：先在 `(1,0,0)`/`(3,0,0)` 放两件
   （断言"超半径不误吸附"），再在 `(3,0,1.05)` 放第三件（落点距 B 的 +Z 挂点 0.55m）→ 吸附 Connect →
   `GetComposite` 返回 **2 件**。V3 的「GetComposite 返回 2 个零件」判据保留，但连接对是 B+C。
2. **右键删除的射线路径没有自动化**：批处理无真实鼠标，V4 直接调 `DeletePart`（与右键处理同一实现，
   射线只多 4 行 `Physics.Raycast` + `GetComponentInParent` 拾取）。射线拾取本身靠人工/实机验。
3. **幽灵观感未取证**：半透明材质（URP `_Surface=1` + SrcAlpha/OneMinusSrcAlpha + ZWrite off）没有像素证据，
   只保证编译与逻辑；Linear 色彩空间下如需更醒目需调 `tint`。
4. **落点取 y=0 平面**：零件中心落在 y=0 会与地面半埋（SCN_Game 地面在该平面）。这是本刀按简报取的极简口径；
   网格对齐 / 相机俯角 / 合法落点判定留给下一刀（载具驾驶 + 建造打磨）。
5. **存档口径是全场景 `Capture`**：场上有任何第三方 `AssemblyPart` 都会被存进 slot1，不是"建造者会话子集"。
   当前 SCN_Game 无预置零件，风险为零；若后续场景放装配件需改成归属过滤。
6. **幽灵禁用 `AssemblyPart` 组件**是刻意设计（保证不进 `AllParts`）。以后若给幽灵挂逻辑，需先把它从注册表
   隔离，而不是简单 `enabled = true`。
7. **未验证的交互组合**：建造模式与 `SurvivalLoop` 的 F/R、`AssemblyDragController` 同时在场的按键/射线抢占，
   本刀没有做互斥（B 与它们不共用键，但拖拽组件的鼠标左键可能与放置冲突——该组件此刻未挂到 SCN_Game）。
8. **PlayMode 既有像素用例会重写 `Docs/Parity/*.png`**（三条 M）。已 `git checkout --` 还原，未纳入本任务改动；
   验收复跑 PlayMode 后会再次出现，属既有行为。


## 七、验收结论

> **总判定：PASS**（2026-11-09，验收代理独立复跑；不采信施工数字，全部自跑自证）

### 7.1 独立复跑（证据 `TempLogs/runtests_*.xml`，均为本轮新写、非旧文件）

| 段 | 命令 | 本轮结果 | 施工前基线 | 判读 |
|---|---|---|---|---|
| EditMode | `Tools/run_tests.ps1 -Segments editmode,playmode` | **1113 / 1113 通过，0 失败**（8.95s，xml 18:51:33） | 1113 | 持平，零回归 |
| PlayMode | 同上（同轮） | **98 / 98 通过，0 失败**（65.48s，xml 18:53:09） | 93 | **+5**，正是本刀 5 条 |

5 条新用例在 xml 中逐条为 `result="Passed"`（`<test-case>` 全名）：
`V1_ToggleIsIdempotent_AndIdleBuildsNothing`、`V2_CyclePartKinds_RebuildsGhostOncePerSwitch`、
`V3_PlaceTwoCubes_ThenSnapConnectsIntoComposite`、`V4_DeleteDisconnectsFirst_ThenRemovesFromScene`、
`V5_BlueprintRoundTrip_KeepsTwoPartsAndOneJoint`。

### 7.2 逐项判定

| # | 判定 | 证据（用例 + 数字 + 行号） |
|---|---|---|
| V1 | **PASS** | `V1_...`：`SCN_Game` 载入后 `BuildMode.Instance != null`、`IsBuilding=false`、`Ghost==null`、`GhostBuildCount==0`（用例 66-70 行）；`SetBuilding(true)` 后 `GhostBuildCount==1`、`Ghost.Kind==PartKinds[0]`、`AllParts.Count` 不变（74-80）；重复 `SetBuilding(true)` 后 `AreSame(ghost)`、计数仍 1（82-84）；`SetBuilding(false)` 后 `Ghost==null`、无残留（86-89）。生产侧幂等早退见 `BuildMode.cs:181-184`；挂载门控 `BuildMode.cs:108-123`。 |
| V2 | **PASS** | `V2_...`：`PartKinds.Length==5`，E 连按 5 次逐项 `CurrentKind==Ghost.Kind`（109-115），`GhostBuildCount==1+5==6`（117）；空闲 2 帧计数不增（119-122，证「非每帧 new」）；Q 反向 0→4（124-125）。生产侧 `CycleKind` 见 `BuildMode.cs:201-219`、`EnsureGhost` 见 `512-551`。 |
| V3 | **PASS** | `V3_...`：(1,0,0)/(3,0,0) 间距 2.0m > 1.2m → 两件自由落位、`CountOwnedJoints()==0`、各自复合体 1（144-153，证「超半径不误吸附」）；第三件 (3,0,1.05) 距 B 的 +Z 挂点 0.55m → 吸附 `Connect`，`GetComposite(b).Count==2` 且含 c、1 条关节（157-162）；缝 `seamGap < DockGapMeters+0.005`（实测落在 0.015m 量级）、两外法线 `Dot < -0.999`（164-168）。吸附判定 `BuildMode.cs:473-508`、`Connect` 调用 `245`。**注：按语义认可施工 6.4-1 的坐标拆段（连接对为 B+C，非 A-B）**。 |
| V4 | **PASS** | `V4_...`：删 owner 侧 B 后同帧 `AllParts.Count 2→1`、`ContainsPart(b)==false`、`a.Joints.Count==0`、`GetComposite(a).Count==1`、A 两挂点 `Occupant==null`（191-197）；幽灵不可删（200）；帧末被删对象 `b==null`（203）。生产侧先 `Disconnect` 全部关节再失活+销毁见 `BuildMode.cs:274-286`。 |
| V5 | **PASS** | `V5_...`：2 件 1 关节 → `SaveBlueprint()` true、`slot1.json` 真实落盘（227-229）；`saved.Parts.Count==2`、`saved.Joints.Count==1`（232-233）；清空后 `LoadBlueprint()` 恢复 2 件 1 关节、复合体连通（237-243）；id 原样 `build-0001/0002`（244-245）；读档后续号 `build-0003`（248-250）。链路见 `BuildMode.cs:313-315`（Capture→ToSnapshot→Save）与 `345-348`（ClearAllParts→Rebuild）。 |
| V6 | **PASS** | 见 7.5 变异记录：跳 `Capture` → 仅 V5 红（98/97/1）；逐字节恢复 sha256 一致后复跑 98/98 全绿。 |

### 7.3 三件重点复核

1. **默认关闭 / 像素零影响 —— 成立。** `OnGUI` 在 `!_isBuilding` 时直接 return（`BuildMode.cs:697-701`）；宿主仅一个空 `GameObject("~BuildMode")`，无渲染体/相机。`SCN_Game.unity` 内 **0 处** `BuildMode`/`AssemblyPart`（故 V1 的自动挂载确为运行时 hook，非场景预置）。同轮 98/98 中，加载 `SCN_Game` 的像素证据用例——`ModelStagePixelEvidence`(2)、`KernelCrowdPixelEvidence`(2)、`PostFxPixelEvidence`(1)、`ParticlePixelEvidence`(4)、`GameplayFxPixelEvidence`(4)——**全部 Passed**，即在「建造宿主已挂载但空闲」的状态下像素判据不被污染。
2. **只调用不改语义 —— 成立。** `git diff HEAD -- UnityProject/Assets/Game/View/Assembly UnityProject/Assets/Game/View/Survival` **为空**（既有装配类与 SurvivalLoop 零改动）。`AssemblySnapshotCodec.RootDirectoryOverride` 在 `HEAD` 已存在（line 41），非本刀新增。BuildMode 全程只调既有 API：`TryFindSnap`/`Align`/`Connect`/`GetComposite`/`Disconnect`、`Create*`、`Capture`、`ToSnapshot`/`Save`/`Load`、`Rebuild`。
3. **存档路径 —— 真走既有链路，无另造序列化。** `SaveBlueprint` 为 `new AssemblyGraph()` → `AssemblyGraphSync.Capture(graph)` → `AssemblySnapshotCodec.ToSnapshot(graph,"slot1")` → `Save`（`BuildMode.cs:312-315`）；`LoadBlueprint` 为 `Load` → `ClearAllParts` → `AssemblyRebuilder.Rebuild`（`336/345/348`）。全文无自定义 JSON/文件写入。

### 7.4 假绿排查 —— 未发现

新用例经 `SceneManager.LoadSceneAsync("SCN_Game")`（用例 260 行）载入**真实场景**，取用生产 hook 挂载的 `BuildMode.Instance`；测试文件内 **无** `AddComponent<BuildMode>`、`new GameObject` 或自建场景（grep 仅见 `BuildMode.Instance` 与 `LoadSceneAsync`）。因此 V1 的「自动挂载」与 V3/V5 的放置/存档均落在 SCN_Game 集成链上，不是自搭场景的假绿。

唯一未被自动化的是**输入分发层**（`Update` 的按键/鼠标、`OnGUI`、`ScreenPointToRay`、右键 `Physics.Raycast` 拾取）——批处理无真实输入；用例直接调 `SetBuilding`/`CycleKind`/`PlaceAt`/`DeletePart`/`SaveBlueprint`/`LoadBlueprint`（与输入处理同一实现）。属**边界**而非逻辑假绿，见 7.6。

### 7.5 变异记录（实做，证据 `TempLogs/accept_asm03_mutant_playmode.xml`）

| 步骤 | 操作 | sha256 / 结果 |
|---|---|---|
| 基线 | 原始 `BuildMode.cs` | `9c94df65…73be` |
| 变异 | 注释 `AssemblyGraphSync.Capture(graph)`（`BuildMode.cs:313`），存空图 | `d2305625…6859` |
| 复跑 | PlayMode | **98 总 / 97 过 / 1 红**；红的仅 `V5_BlueprintRoundTrip_KeepsTwoPartsAndOneJoint`，断言 `存档必须含 2 件 Expected: 2 But was: 0`（`BuildModePlayModeTests.cs:232`） |
| 恢复 | 用备份 `cp` 回原文件 | sha256 **回到 `9c94df65…73be`（逐字节一致）**，无 MUTANT 残留 |
| 复跑 | PlayMode | **98 / 98 全绿** |
| 清理 | `git checkout --` 还原 PlayMode 重写的 3 张 `Docs/Parity/*.png`（既有行为噪声） | 工作区仅剩本刀 5 个新增项 |

### 7.6 受限（不影响本刀判 PASS）

1. **输入/射线未自动化**：按键与鼠标、`Physics.Raycast` 拾取路径靠实机；V4 直调 `DeletePart`（与右键同一实现，仅少 4 行射线拾取）。
2. **V3 坐标语义**：原文 (1,0,0)/(3,0,0) 相距 2.0m 与 1.2m 吸附半径算术冲突；用例保留原坐标并补第三件完成「2 件复合体」判据，连接对为 B+C 而非 A-B（施工 6.4-1，验收按简报 ≤1.2m 语义认可）。
3. **幽灵半透明观感**无像素证据（只证逻辑与编译）。
4. **存档口径为全场景 `Capture`**：当前 `SCN_Game` 无预置零件，风险为零；后续若有第三方装配件需改归属过滤。
5. **未做输入互斥**：与 `SurvivalLoop`(F/R)、`AssemblyDragController`(左键拖拽) 的键/射线抢占未处理；该拖拽组件当前未挂 `SCN_Game`。
6. 新增 `Build.meta`/测试 `.meta` 为 Unity 自动生成（白名单 `Build/**` 的同级/配套文件，属必要生成物）。
