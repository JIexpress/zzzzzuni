# TASK-A-WORLD-01 —— 双精度世界坐标 + 浮点原点（4km 重定位）

> 出处：`Docs/TECH-VALIDATION.md` §7 第 3 项，关键路径 `A-WORLD-01 → A-WORLD-02 → A-LOOP-03`。
> 目标不是做行星，而是先把「走得远也不抖」的地基打对：**世界坐标用 double 存，渲染用 float 呈现**。

## 一、问题定义（施工前必读）

- Unity 的 `Transform` 是 float32：坐标到 10km 量级，float 的最小刻度约 1mm；到 100km 约 8mm；到 1000km 约 6cm——顶点抖动就是这么来的。
- 解法是**浮点原点（floating origin）**：世界坐标（double）不变，当相机离原点超过阈值时，把「原点」平移过去，所有世界物体整体反向平移同样的量；相机相对位置不变，float 只在原点附近的小范围内使用。
- 参考实现：`Native/zrender` 的 B-TECH-03（浮点原点抖动消除 24,576×，原生侧已验证数学），本任务在 **Unity 侧**落地同一套思路。

## 二、目标（本刀只做地基，不做地形）

1. **纯数学层**（引擎无关，EditMode 可断言）：
   - `WorldPosition`：double 三分量、加减、距离、与 Unity `Vector3` 的互转（带原点偏移）。
   - `WorldOriginMath`：`ShouldRebase(offset, threshold)`、`RebaseDelta(cameraWorld, currentOffset, threshold)`、`ToUnity(world, offset)` 等纯函数。
2. **运行时层**：`WorldOrigin`（MonoBehaviour）：
   - 跟踪主相机；超过阈值（默认 **4000m**）时执行一次重定位；
   - 维护 `OriginOffset`（double，世界坐标→Unity 坐标的平移量）；
   - 提供 `Register/Unregister(Transform)`：**只有注册过的物体**会被平移（避免误移 UI/相机/宿主）；
   - 提供 `WorldToUnity` / `UnityToWorld`，以及 `RebaseCount` 诊断；
   - 无相机/无注册物体时静默不动作（幂等、可重复调用）。
3. **测试**：
   - EditMode：纯函数（阈值边界、重定位后世界坐标守恒、100km 处 double↔float 误差量化）；
   - PlayMode：注册两个物体 → 相机推到 5km → 触发重定位 → 两物体的**世界坐标不变**、Unity 坐标被整体平移、相机相对位置不变。

## 三、设计要点

- `WorldPosition` 用 `double`，`ToUnity(offset)` 里减完偏移**再**转 float——顺序不能反（先转 float 再减，精度就丢在转换那一步了）。
- 重定位：`delta = ToUnity(cameraWorld, offset)`；把 delta 从所有注册物体的 `transform.position` 里减掉；`offset += delta`（double）。
- 相机不参与平移（它是参照物）：重定位后 `camera.transform.position` 归零附近，`cameraWorld` 不变。
- 阈值做成可配置（默认 4000m），并留 `ForceRebase()` 给测试/调试用。
- 不做：地形片、球面重力、球体 LOD（那是 A-WORLD-02）；不改 `SurvivalLoop`/`BuildMode` 的坐标语义（它们仍在原点附近工作）。

## 四、白名单

- `UnityProject/Assets/Game/Domain/World/**`（新增：WorldPosition / WorldOriginMath）
- `UnityProject/Assets/Game/View/World/**`（新增：WorldOrigin）
- `UnityProject/Assets/Game/Tests/Domain/WorldOriginMathTests.cs`（新增）
- `UnityProject/Assets/Game/Tests/PlayMode/WorldOriginPlayModeTests.cs`（新增）
- `Docs/Logs/TASK-A-WORLD-01.md`、`TASKS.md`（仅本任务行）

## 五、验收清单

| # | 断言 | 判据 |
|---|---|---|
| V1 | 互转守恒 | `UnityToWorld(WorldToUnity(p))` 在 100km 处误差 < 1e-3 m（double 精度，不是 float） |
| V2 | 阈值语义 | 距离 < 阈值不重定位；≥ 阈值为 true；阈值 ≤0 视为非法（不重定位） |
| V3 | 重定位守恒 | 重定位前后，注册物体的 `WorldPosition` 不变（误差 < 1e-3 m） |
| V4 | 相对位置 | 重定位前后，相机到物体的 Unity 距离不变（误差 < 1e-3 m） |
| V5 | 边界 | 未注册物体不被平移；无相机/空注册表时 `Rebase` 幂等且不抛 |
| V6 | 精度证据 | 100km 处：float 直接表示误差 vs double 守恒误差，给出两个数字并对比（量化「为什么要 double」） |
| V7 | 变异复红 | 把 `ToUnity` 改成「先转 float 再减偏移」→ V1 或 V6 必红；恢复后全绿 |

## 六、施工报告

### 6.1 摘要

只做地基：世界坐标用 double 存，渲染时减浮点原点再降 float；一行地形/球面逻辑都没有。

- **`WorldPosition`**（Domain，struct）：double 三分量、`+ - *`/一元负、`Magnitude`/`SqrMagnitude`/`Distance`。
  **刻意不提供 `(float)X` 式转换**——互转只有 `ToUnity(offset)` / `FromUnity(unity, offset)` 两个入口，
  且都委托给 `WorldOriginMath`，保证「先 double 相减、最后一次转 float」只有一份实现（V7 的靶点只有一处）。
- **`WorldOriginMath`**（纯函数）：
  - `ShouldRebase(offsetMeters, threshold)`：`offset >= threshold` 触发；**恰好等于阈值即触发**
    （简报 V2 口径「≥ 阈值为 true」，与原生 B-TECH-03 的严格大于不同，这里按 Unity 侧简报关）；
    阈值 ≤0 / NaN / ±∞ 视为非法配置，永不触发。
  - `RebaseDelta(cameraWorld, offset, threshold)`：未达阈值返回 `Vector3.zero`，达到则返回 `ToUnity(cameraWorld, offset)`。
  - `ToUnity` / `ToWorld`：唯一的 double↔float 边界；`ToWorld` 只做 float→double 加宽，无新增误差。
  - `FloatUlpAt` / `FloatQuantizationError`：V6 的证据函数（不参与运行时逻辑）。
- **`WorldOrigin`**（View，MonoBehaviour）：`Register/Unregister/ClearRegistered`、`OriginOffset`（double）、
  `RebaseCount`、`Rebase()`（阈值判定）、`ForceRebase()`（忽略阈值）、`WorldToUnity`/`UnityToWorld`、
  `CameraWorldPosition()`、`TargetCamera`（为空退回 `Camera.main`）、`AutoRebase`（默认 true，每帧自检）。
  **不做自动挂载**（没有 `RuntimeInitializeOnLoadMethod`、不碰 `sceneLoaded`）：现有场景一个都不受影响，
  需要时手动加组件或由后续任务装配；未挂载即零行为。
- **相机口径（本刀唯一需要解释的实现选择）**：相机**不进注册表**，但重定位时与注册物体一起平移 `-delta`。
  只移物体不移相机，V4（相机到物体的相对距离）必然被破坏（距离会凭空减少 `|delta|`）；一起移才同时满足
  「世界坐标守恒 + 相对位置守恒 + 相机 transform 归零」。相机若被误 `Register` 也不会被二次平移（循环里跳过）。
- 未改 `SurvivalLoop` / `BuildMode` / 任何既有装配类；未新增 Input System action；未 commit。

### 6.2 改动清单

| 文件 | 类型 | 说明 |
|---|---|---|
| `UnityProject/Assets/Game/Domain/World/WorldPosition.cs` | 新增（83 行） | double 世界坐标；sha256 `7761b86224b6b3f8…` |
| `UnityProject/Assets/Game/Domain/World/WorldOriginMath.cs` | 新增（107 行） | 纯函数：阈值/重定位量/互转/精度工具；sha256 `cb65a701bc054299…` |
| `UnityProject/Assets/Game/View/World/WorldOrigin.cs` | 新增（199 行） | 运行时浮点原点；sha256 `7bfaaaaacfcc57f6…` |
| `UnityProject/Assets/Game/Domain/World.meta`、`UnityProject/Assets/Game/View/World.meta` | 新增（Unity 自动生成） | 两个新目录 |
| `UnityProject/Assets/Game/Tests/Domain/WorldOriginMathTests.cs` | 新增（217 行，14 条） | V1/V2/V3(数学)/V5(数学)/V6 + WorldPosition 运算 |
| `UnityProject/Assets/Game/Tests/PlayMode/WorldOriginPlayModeTests.cs` | 新增（218 行，3 条） | V3/V4/V5 + 自动 tick |
| `Docs/Logs/TASK-A-WORLD-01.md` | 修改 | 本报告 |

未改：`TASKS.md`（看板无 A-WORLD-01 行，按白名单「仅本任务行」不新增）、`SurvivalLoop.cs`、`BuildMode.cs`、
所有 asmdef（`Game.Domain`/`Game.View`/两个测试 asmdef 已覆盖，无需加引用）、原生 `Native/**`。
编码：UTF-8 无 BOM + LF（`od` 首字节非 `EF BB BF`、`grep -c $'\r'` = 0）。

### 6.3 自测数字（真跑，证据 `TempLogs/runtests_*.xml`）

| 段 | 命令 | 结果 | 基线 |
|---|---|---|---|
| EditMode | `Tools/run_tests.ps1 -Segments editmode` | **1127 / 1127 通过，0 失败**（10.07s） | 1113（**+14**，即本次 14 条） |
| PlayMode | `Tools/run_tests.ps1 -Segments playmode` | **101 / 101 通过，0 失败**（69.58s） | 98（**+3**，即本次 3 条） |

新增 EditMode 14 条：阈值边界 3 条（低于/等于及以上/非法阈值）+ 三维距离 1 条 + `RebaseDelta` 2 条 +
互转 3 条（100km 往返、小坐标精确、亚毫米细节）+ V6 精度 1 条 + 重定位守恒 1 条 + 非法阈值 no-op 1 条 +
WorldPosition 运算 1 条 + ULP 单调性 1 条。
新增 PlayMode 3 条：`Rebase_At5km_ConservesWorldPositions_AndRelativeDistance`、
`AutoRebase_TicksByItself_OverThreshold`、`Rebase_WithoutCameraOrRegistry_IsIdempotentAndSilent`。

**V6 两个数字（xml `<output>` 原文）**：

```
V6 @100km: float ULP = 7.8125 mm; float direct error = 1.0000 mm; double+offset conservation error = 4.366E-011 m (4.366E-008 mm)
V1 @100km roundtrip error = 0.000E+000 m
```

1. **float 直接表示**：100km 处最小刻度 **7.8125 mm**（2^-7）；把「100km+1mm」写进 float 直接变成
   `100000.0`，量化误差 **1.0000 mm**（细节被吃掉）。
2. **double+偏移方案**：同样输入经「double 相减 → 转 float → 加回偏移」后守恒误差 **4.366e-11 m**
   （约 4.4e-8 mm），比 float 直接表示小 **7~9 个数量级**。
3. V1（100km 处互转往返）实测误差 **0**：相对分量 12.5/7/-10 恰好可精确表示，且偏移 `100000.0001`
   本身不是 float 能表示的数——只有先在 double 域相减才能得到干净的 12.5，这正是顺序铁律的收益。

**V7 变异复红（已实做，非纸面）**：把 `WorldOriginMath.ToUnity` 改成
`(float)world.X - (float)originOffset.X`（先转 float 再减）→ EditMode **1127 总 / 1125 过 / 2 红**：

- `Precision_At100km_FloatDirectVersusDoubleWithOffset`：期望 <1e-6，实际 **0.001**（1mm 原样回来）；
- `ToUnity_PreservesSubMillimetreDetailAt100km`：期望 `0.001 ± 1e-6`，实际 **0.0**（1mm 直接被吃成 0）。

还原后 `grep MUTANT` = 0，复跑 **1127/1127 全绿**（证据为本节表格那一轮）。

**设备纪律**：每次跑前 `tasklist` 无 Unity、脚本自动删 `UnityProject/Temp/UnityLockfile`；`-runTests` 不带
`-quit`；五轮（绿 → 变异红 → 还原绿 → PlayMode 绿 ×2）全部以 `results.xml` 落地后 Unity 自行 exit 收尾，
**未触发 taskkill**。
**环境处置**：首个绿跑被 `Duplicate case-variant variables {HTTPS_PROXY/https_proxy}` 拦下（交接档案 §3.2 已知坑），
在调用 shell 里清掉小写代理变量后重跑即通过——脚本与仓库未改。
**噪声**：PlayMode 既有像素用例会重写 `Docs/Parity/*.png`，已两次 `git checkout --` 还原，未纳入本任务改动。

### 6.4 风险与未验证

1. **相机由角色控制器驱动时会被"抢回"**：本刀直接改 `camera.transform.position`。若相机是角色 rig 的子物体
   （后续 A-WORLD-02 接驾驶/第一人称），下一帧控制器会把世界位置写回去，重定位等于没发生——需要在装配层
   改成「平移 rig 根」或让控制器认 `OriginOffset`。当前 `SCN_Game` 的相机是独立物体，风险为零。
2. **父子级联未处理**：注册了父物体、又注册其子物体会被平移两次；注册相机与其父级同理。只做了「相机自身
   跳过」这一条，没有做层级去重（本刀验收里没有这种装配，刻意不提前抽象）。
3. **只平移 position，不动刚体速度**：`Rigidbody` 物体重定位后世界位置连续，但 `velocity` 语义不变（位置是
   瞬移的 `-delta`，速度不该变），当前无人使用，A-WORLD-02 接物理移动时要专门验一次。
4. **容差随距离增长**：`transform.position` 的 float 减法在 4km 处 ULP = 0.488mm，V3/V4 的 1e-3 容差刚好；
   若把阈值调到 10km 以上，测试容差必须按 ULP 放宽（10km 1.95mm、100km 7.8mm），不能照抄 1e-3。
5. **阈值语义与原生侧不同**：这里 `>=` 触发（V2 简报口径），`Native/tests/planet_math.h` 是 `>` 触发。
   两边都「恰在阈值不反复重定位」，但边界差一个等比点，跨轨对照时勿混用。
6. **没有诊断 HUD**：简报只要求 `OriginOffset`/`RebaseCount` 可读，未做 IMGUI（避免像 `SurvivalLoop` 那样
   再挂一个常驻面板）；如需可视化由后续任务接。
7. **世界坐标不持久化**：`WorldPosition` 不进存档，Unity 场景里存的仍是被平移后的 float。存档/读档要保证
   「先读 `OriginOffset` 再换算」，留给存档任务。
8. **未跑 arch/config/naming 三段**（本任务自测口径只有 editmode/playmode）；新目录只有 .cs + Unity 自动
   .meta，无资产命名风险，但仍建议主控在闭环前补一次全量。


## 七、验收结论

> **总判定：PASS（7/7）** —— 2026-11-09 验收子代理独立复跑，不采信施工数字；EditMode/PlayMode 全量各跑 3 轮（绿 → 变异红 → 还原绿），变异为本人亲手落地并逐字节还原。附 2 项文档级瑕疵（§7.6-1/2），不影响功能判定。
> 证据：`TempLogs/runtests_editmode.xml`（11:25 还原绿）、`TempLogs/runtests_playmode.xml`（11:23 绿）、`TempLogs/verify_world01_editmode_mutant.xml`（11:24 变异红，已另存）。

### 7.1 独立复跑（三组数字均来自本轮新写的 xml，非旧文件）

| 段 | 命令 | 本轮结果 | 基线 | 判读 |
|---|---|---|---|---|
| EditMode（首轮绿） | `Tools/run_tests.ps1 -Segments editmode` | **1127 / 1127 通过，0 失败**（10.07s） | 1113 | **+14**，恰为 `[Test]` 14 条（`grep -c "\[Test\]"` 实测） |
| PlayMode | `Tools/run_tests.ps1 -Segments playmode` | **101 / 101 通过，0 失败**（68.85s） | 98 | **+3**，恰为 `[UnityTest]` 3 条 |
| EditMode（变异） | 同上 | **1127 总 / 1125 过 / 2 红**（Unity exit=2） | — | 见 §7.5 |
| EditMode（还原后） | 同上 | **1127 / 1127 通过，0 失败**（10.43s） | — | 恢复施工完成态 |

PlayMode 三条在 xml 中均为 `result="Passed"`：`WorldOriginPlayModeTests.Rebase_At5km_ConservesWorldPositions_AndRelativeDistance`、`AutoRebase_TicksByItself_OverThreshold`、`Rebase_WithoutCameraOrRegistry_IsIdempotentAndSilent`。未发现 `skipped`。
设备纪律：三轮 Unity 均单实例启动、脚本自动清 lockfile、`-runTests` 不带 `-quit`；变异轮与还原轮均由 Unity 自行 exit（exit=2/0），**未 taskkill**。首轮 EditMode 起跑前在调用 shell 清掉小写 `https_proxy/http_proxy`（交接档案 §3.2 已知坑），仓库与脚本未改。

### 7.2 逐项判定

| # | 判定 | 证据（用例 + 数字 + 行号） |
|---|---|---|
| V1 | **PASS**（附注） | `WorldOriginMathTests.ToUnity_ThenToWorld_RoundTripsAt100km`（测试 77-92）：100km、偏移 `100000.0001` 下 `unity=(12.5,7,-10)`，xml `<output>` 实测 **roundtrip error = 0.000E+000 m**，断言 `< 1e-3`（91 行）。**附注**：该输入的 0 误差有结构性成分——回程重放同一次 double 加法，12.5/7/-10 又恰好是 float 精确值；真正有信息量的是 `ToUnity_PreservesSubMillimetreDetailAt100km`（111-121）：100km 处 1mm 细节保留为 `0.001f`（±1e-6）、守恒误差 `<1e-6`。实现唯一转换点 `WorldOriginMath.cs:65-72`（67-69 double 相减，71 才转 float）。 |
| V2 | **PASS** | 三条边界用例全绿：`ShouldRebase_BelowThreshold_False`（3999.999→false，测试 20-25）、`ShouldRebase_AtOrAboveThreshold_True`（**恰等于 4000 为 true**，28-33）、`ShouldRebase_NonPositiveThreshold_IsIllegal_AndNeverFires`（0/-1 永不触发，36-41）；另 `ShouldRebase_UsesThreeDimensionalDistance` 实测斜向 **4242.640687119285 m** 触发、2000/2000/0 不触发（44-52）。生产侧 `WorldOriginMath.cs:24-37`，`>=` 在 36 行，非法阈值在 26 行早退。 |
| V3 | **PASS** | `Rebase_At5km...`（PlayMode 42-119）：注册 A(5003,100,2)/B(5020,100,0)，5km 触发后 A/B 世界坐标差 `<1e-3`（82-85；本组数据 delta=5000 精确可表示，实测误差 0）。生产侧 `WorldOrigin.cs:163-195`：`delta=ToUnity(cameraWorld,offset)`（165）、注册表逐项 `position -= delta`（173-189）、`offset += delta`（192）。EditMode 同名用例（152-176）是公式镜像，见 §7.4。 |
| V4 | **PASS** | 同用例 87-104：相机→A、相机→B 的 Unity 距离差均 `<1e-3`（88-91）；重定位后相机 `position.magnitude < 0.01`（92）；A 落位 `(3,0,2)`、B.x=20（95-98）；`cameraWorld` 前后差 `<1e-3`（101-102）；`OriginOffset == (5000,100,0)`（103-104）。**相机设计自洽性**：相机不在注册表，却在 `ApplyRebase` 里与注册物体同减 `delta`（`WorldOrigin.cs:191`），并更新 double 偏移（192）。于是 `cameraWorld = offset + position` 在数学上恒等（offset+delta 加 pos-delta），相对距离也恒等；若只移物体不移相机，V4 的距离会凭空缩 `|delta|`、相机也不会归零——**当前实现是同时满足 V4 与「相机归零」的唯一一组平移**。判定：自洽，V4 真成立；但它确实偏离简报 §三「相机不参与平移」的字面表述（简报同句又要求 transform 归零，本身互相矛盾），且直接写 `camera.transform.position` 的代价见 §7.6-4。 |
| V5 | **PASS**（限平面层级） | 用例 106-112：未注册 `bystander` 重定位前后 `AreEqual` 逐位相等，幂等二次调用同样不动（112）；空注册表+相机在原点：`Rebase/ForceRebase` 均 false、计数 0、offset 零、target 不动（166-171）；无相机（临时禁用全部相机并断言 `Camera.main==null`）：`Rebase/ForceRebase` false、不记账、不动已注册物体，连调两次仍幂等不抛（185-204）。生产侧只遍历 `_registered`（173-180）、销毁项就地清理（176-180）、相机若误登记在循环里跳过（183-186）。**边界**：若未注册物体是「已注册物体的子物体」，父级平移会带着它动——层级去重未做（施工风险 2 只提了双平移），本刀无此装配，不判 FAIL，记入 §7.6-3。 |
| V6 | **PASS** | `Precision_At100km_FloatDirectVersusDoubleWithOffset`（126-147），xml `<output>` 原文复现：`float ULP = 7.8125 mm; float direct error = 1.0000 mm; double+offset conservation error = 4.366E-011 m (4.366E-008 mm)`；断言 ULP=2^-7（143）、直接误差 1mm（144）、方案误差 `<1e-6`（145）、差距显著（146）。两数字相差 **7 个数量级以上**，量化了「为什么必须 double」。 |
| V7 | **PASS** | 本人实做变异（非纸面）：`ToUnity` 改为 `(float)world.X - (float)originOffset.X` 三分量同改 → EditMode **1125/1127，2 红**：`Precision_...` 期望 `<1e-6` 实际 **0.0010000000038417056**；`ToUnity_PreservesSubMillimetre...` 期望 `0.001±1e-6` 实际 **0.0**。逐字节还原 `cmp` 一致、sha256 回到 `cb65a701…`、`grep MUTANT=0`，复跑 **1127/1127 全绿**。详见 §7.5。 |

### 7.3 四个重点复核

1. **顺序铁律：守住。** 运行时全仓只有一处 double→float 边界 `WorldOriginMath.cs:71`（`(float)dx`，dx 为 67-69 的 double 差）；`grep "(float)"` 在新增生产代码里只命中该行与 V6 证据函数（92/105，不参与运行时）。`WorldPosition` 刻意不提供 `(float)X`（`WorldPosition.cs:13-14`），`ToUnity/FromUnity` 只做委托（55-60）；`WorldOrigin` 三个入口（107/110/165）全部走同一函数。变异实验进一步证明这条顺序是承重的。
2. **未注册物体：绝不平移。** `ApplyRebase` 的写位置只有两处：注册表循环（`WorldOrigin.cs:188`）与相机（191）；宿主/UI/场景其余对象无任何入口，无自动挂载（`grep RuntimeInitializeOnLoadMethod|sceneLoaded|DontDestroyOnLoad` = 0 命中），且 `WorldOrigin` 在新增目录之外**零引用**（`grep -rln WorldOrigin` 只剩本任务 4 个文件）。PlayMode 用逐位相等验证了这一点。
3. **相机处理：自洽，V4 真成立**（数学见 §7.2 V4）。补充两条限制：相机若由角色控制器驱动，下一帧会把世界位置写回，重定位等于没发生（施工风险 1）；相机是已注册物体的子物体时会被父级+自身平移两次（风险 2）。二者均未在本刀装配中出现，属未验证边界而非本刀缺陷。
4. **未误改既有代码：成立。** `git diff HEAD` **为空**（含 `SurvivalLoop.cs`/`BuildMode.cs`/asmdef/场景/`.meta` 全部无改动）；`git status --porcelain` 仅白名单内 8 个新增路径 + 本日志。asmdef 无需改：`Game.Domain.Tests` 已引 `Game.Domain`、`Game.View.PlayMode.Tests` 已引 `Game.Domain`+`Game.View`（实测引用表）。

### 7.4 假绿排查 —— 未发现假绿，但记两条「证据强度」提示

- **PlayMode 是真实 Unity 路径**：全部用 `new GameObject` + `AddComponent<Camera>` + 真实 `Transform.position`（42-59、124-135），不存在「自造理想坐标」。V3/V4/V5 的运行时结论由它承载。
- **EditMode 的 V3 是公式镜像**：`Rebase_PreservesWorldPosition_AndRelativeDistance`（152-176）没有调用 `WorldOrigin`，而是用局部变量重放了 `objectUnity - delta / offset + delta` 的算式。它验证数学契约，**不能**捕获 `ApplyRebase` 的回归；此项由 PlayMode 补位，故不构成假绿，但单看该用例不能当运行时证据。
- **V3/V4 守恒断言对「统一错误平移量」不敏感**：若相机、物体、offset 三者同错一个 delta，V3/V4 照样绿。真正钉住量值的是绝对坐标断言（A=3、B.x=20、相机模长<0.01、offset=5000，95-104）。组合起来覆盖完整；单条守恒断言不宜单独引用。
- **V1 的用例对本次变异不敏感**：float-first 变异下 `ToUnity_ThenToWorld_RoundTripsAt100km` 仍会绿（12.5 精确可表示，先转 float 再减也得 12.5），V7 的复红实际由 V6 与亚毫米用例承担。与 V7 判据「V1 **或** V6 必红」一致，但报告不应把 V1 的 0 误差当作抗变异证据。

### 7.5 变异记录（本人落地，证据 `TempLogs/verify_world01_editmode_mutant.xml`）

- 改动：`WorldOriginMath.ToUnity` 三分量改为 `(float)world.X - (float)originOffset.X`（先转 float 再减），留 `// VERIFIER MUTANT` 标记；变异体 sha256 `f9a75f61…`。
- 结果：EditMode **1127 总 / 1125 过 / 2 红**，Unity exit=2；红例与实测值：
  - `Precision_At100km_FloatDirectVersusDoubleWithOffset`：期望 `< 1e-6`，实际 **0.0010000000038417056**（1mm 原样回来）；
  - `ToUnity_PreservesSubMillimetreDetailAt100km`：期望 **0.001 ± 1e-6**，实际 **0.0**（1mm 被吃成 0）。
  两条断言文案分别落在测试 145 行与 118 行，与施工报告描述一致（数字亦逐位一致）。
- 还原：`cp` 备份回写 → `cmp` 输出 IDENTICAL → sha256 恢复 `cb65a701bc05429980f347de417ea7d015704daae7dcd6ab4f98e76a562cb3d6`（与变异前逐字节一致）→ `grep -c "VERIFIER MUTANT"` = 0 → 复跑 **1127/1127 全绿**。
- 备份留存：`TempLogs/world01_WorldOriginMath.orig.bak`（与仓内文件同哈希）。

### 7.6 受限与瑕疵（不改变 PASS）

1. **施工报告 sha256 标签互换（文档笔误）**：报告把 `cb65a701…` 标给 WorldPosition、`7761b862…` 标给 WorldOriginMath；实测 **WorldPosition=7761b862…、WorldOriginMath=cb65a701…**。文件内容/行数（83/107/199）与用途均正确，仅表格标签对调，不影响判定。
2. **编码声明经独立核验成立**：新增 5 个 .cs 均 UTF-8 无 BOM（首字节 `75 73 69` / `2f 2f 20`）、CR 字节数 **0**、LF 数与行数一致（199 行=199 LF）。
3. **层级未去重**：已注册父+已注册子会双平移；未注册子会随已注册父被动移动（V5 字面「未注册不被平移」在层级场景不严格成立）。本刀测试均为根物体，属未覆盖边界；A-WORLD-02 接装配/载具前必须补层级策略。
4. **相机由控制器驱动会被抢回**：直接写 `camera.transform.position`；当前 `SCN_Game` 相机独立，零风险；接第一人称/载具时需要在装配层平移 rig 根或让控制器认 `OriginOffset`。
5. **容差随距离增长**：4km 处 float ULP=0.488mm，V3/V4 的 1e-3 刚好；阈值若调到 10km/100km，容差需按 1.95mm/7.8mm 放宽，不能照抄。
6. **世界坐标权威值只在原点**：每个物体并没有 double 的权威坐标，`world = offset(double) + transform(float)`；物体首次写入 Transform 时若已在 100km，量化损失不可逆，重定位无法找回。测试在 5km 内（ULP 0.488mm）不受影响；接行星尺度时应让逻辑层持有 `WorldPosition` 作为权威值。
7. **未跑 arch/config/naming**（本任务自测口径仅 editmode/playmode）；新增目录只有 .cs 与 Unity 自动 .meta，命名风险低，但闭环前建议主控补一次全量。
8. **未验证**：刚体速度、父子级联、存档换算、诊断 HUD——施工已在 §6.4 自曝，与本次复核结论一致，不重复计缺陷。
9. **环境噪声**：PlayMode 既有像素用例重写 3 张 `Docs/Parity/*.png`，已 `git checkout --` 还原，未纳入本任务改动；最终工作区仅剩白名单新增文件（未 commit）。

**最终状态**：与施工完成态逐字节一致（`WorldOriginMath.cs` sha256 已核），无白名单外改动、无 commit、无残留变异标记；`TempLogs` 三份本轮证据保留（已被 .gitignore 忽略）。
