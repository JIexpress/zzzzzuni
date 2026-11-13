# TASK-B-PLANET-07 —— Unity 托管桥：chunk 流式管理器 + WorldOrigin 每帧接线

- **轨道**：B / PLANET（设计稿 §11-07；依赖 02 ✓ + 04（验收中）+ 05 ✓）
- **状态**：todo
- **派发**：主控，2026-11-14
- **定位**：WorldOrigin 自 A-WORLD-01 落地以来**从未挂任何场景**（HANDOFF 渲染体检）——本任务是它首个生产消费者

## 任务简报（主控写）

### 0. 使命

Unity 托管侧把行星能力接进真实帧循环：可见性驱动的 chunk 流式 Create/Destroy/SetLod、WorldOrigin(4000m)→SetCameraOrigin 每帧接线、viewRel 纯旋转构造并入 WorldOriginMath 唯一边界（A-WORLD-01 顺序铁律，TASK-A-WORLD-01.md:124,191）。账目断言测试齐全。

### 1. 做什么

1. **chunk 流式管理器**（Runtime asmdef，如 `ZRender/Runtime/Planet/`）：
   - 可见性驱动：按相机位置计算需要哪些 chunk（近/远档），Create/Destroy/SetLod 决策
   - SetLod 决策选型：`PlanetLodLevel` 迟滞口径的 C# 等效 **或** P/Invoke 纯函数决策——**选型与理由写进施工报告**（两案都读设计稿 §5/§11-07 再定）
   - **分帧摊布**：B-PLANET-04 实测单 chunk 近档 ≈7,446µs——每帧创建预算上限（如 ≤2 个近档/帧）防尖峰，策略写明
   - **G4-R1 接缝核查开销（B-PLANET-04 验收独立发现，改判后口径）**：近档共享边 memcmp 核查现为**全档位执行**——全量流式（6144 远档）时该扫描开销会实际发生。本任务必须处理：加档位门（tier==0 才核查）或 (face,cx,cy) 哈希索引，选型+数字写进施工报告
   - G6 双快照同步责任：SetWorld 由谁调用、何时同步（本任务定契约并写明）
2. **WorldOrigin 接线**：每帧 `WorldOrigin` 当前原点 → `ZR_Unity_SetCameraOrigin`（经门面 EnsurePlanetChannelReady 门控）；**viewRel 纯旋转构造并入 WorldOriginMath 唯一边界**——不许在调用点散落 double→float 转换（A-WORLD-01 顺序铁律）
3. **门控纪律**：ApiVersion≥3 才启用行星通道；门控不过→功能整体关闭+日志一行，不许半启用
4. **账目断言测试**（EditMode）：创建/销毁/SetLod 操作与 `ZR_Planet_GetStats` 严格对账；分帧预算生效断言；WorldOrigin 接线后 SetCameraOrigin 收到的值与 WorldOrigin 状态一致
5. **EditMode 全绿**：≥1182 只增不减；naming/arch 段 PASS（**探针类/运行时类新 asmdef 必须同步登记 ArchitectureValidator 矩阵——B-PROBE-02 的债不许重犯**）
6. 不碰：zplugin/zrender 原生侧（04 已交付）、zplanet、TASKS/HANDOFF/WORKLOG；Unity 批处理纪律照旧

### 2. 铁律

- 约法三章：施工不 commit、不改看板；UTF-8 无 BOM/LF；零第三方依赖
- 设计稿+04 施工报告（G6 双快照/分帧摊布输入）是依据；偏离=MISSING+回主控

### 3. 完成判据

- 流式管理器+接线落地；账目断言全绿；WorldOriginMath 唯一边界保持（架构校验/代码审查证据）
- 分帧预算生效；门控行为正确（未过门控=整体关闭）
- 全量绿；后续 B-PLANET-08/09/10 可按此开工

## 施工报告（施工代理写，2026-11-14）

### 1. 交付概览

**通过行（全部亲跑 exit 0）**：

```
EditMode: total=1211 passed=1211 failed=0 skipped=0（基线 1182 → +29，只增不减；证据 TempLogs/runtests_editmode.xml）
arch    : PASS exit 0（asmdef 扫描 27 / 期望矩阵 13（Tests 豁免）/ 违规 0；证据 TempLogs/arch_report.txt）
naming  : PASS exit 0（违规 0；证据 TempLogs/name_report.txt）
```

EditMode 第一轮曾 1211 总 / 14 红（见 §8 缺口与修复记录），修复后第二轮起连续两轮全绿。

**文件清单（全部在白名单解释范围内；新增文件行数为 wc -l 实测）**：

| 文件 | 变更 | 内容 |
|---|---|---|
| `UnityProject/Assets/ZRender/Runtime/Planet/ZrPlanetChunkStreamer.cs` | 新增 765 行 | chunk 流式管理器本体：可见性排名（原生地平线剔除口径镜像+半块销毁迟滞带）、SetLod 迟滞决策 C# 镜像、成本加权分帧预算+近档硬帽、账目自检、稳态零 GC 分配 |
| `UnityProject/Assets/ZRender/Runtime/Planet/ZrPlanetStreamingDriver.cs` | 新增 209 行 | MonoBehaviour 装配面：门控（整体关闭+一行日志）、单驱动守卫、经原生 GetCameraOrigin 汇合读回当帧原点、OnDestroy→ClearAll |
| `UnityProject/Assets/ZRender/Bindings/ZrPlanetChannel.cs` | 新增 106 行 | 通道集成缝：Enable/Disable（整体关闭+一行日志）+ viewRel 交接（consume-once，关闭期发布被丢弃） |
| `UnityProject/Assets/ZRender/Bindings/AssemblyInfo.cs` | 新增 11 行 | Bindings 友元声明（ResetForTests 收 internal——Runtime/AssemblyInfo.cs 同策） |
| `UnityProject/Assets/Game/View/World/WorldOriginPlanetLink.cs` | 新增 109 行 | WorldOrigin→行星每帧接线：门控一次、SetCameraOrigin(相机绝对 double)、viewRel 发布（构造唯一在 WorldOriginMath） |
| `UnityProject/Assets/ZRender/Runtime/ZrSceneMath.cs` | +16/0 | `ToCameraRelativeWorld`（viewRel 帧物件世界矩阵相机相对化，纯函数可单测） |
| `UnityProject/Assets/ZRender/Runtime/ZRenderSceneBridge.cs` | +25/−2 | 消费通道 viewRel → SetCamera 纯旋转视图 + 物件世界矩阵相机相对化；通道关闭回退旧路径（行为与 B-UNITY-03 逐字一致） |
| `UnityProject/Assets/Game/Domain/World/WorldOriginMath.cs` | +25/0 | `ToCameraRelativeView`（viewRel 纯旋转构造——唯一边界延伸，见 §5） |
| `UnityProject/Assets/ZRender/Tests/ZFrame.Render.Tests.asmdef` | +2/0 | references 增加 Game.Domain/Game.View（Tests 程序集豁免矩阵；WiringScene 需要跨层测试装配件） |
| `UnityProject/Assets/Game/Tests/Domain/WorldOriginMathTests.cs` | +94/0 | viewRel 4 用例（平移归零+旋转逐位/方向不变性/点变换恒等式/幂等） |
| `UnityProject/Assets/ZRender/Tests/ZrPlanetStreamingTests.cs` | 新增 478 行（17 用例） | 迟滞边界/锚点方向镜像/账目对账/去重/Dispose 清场/预算钳制/近档硬帽/零预算/锚点先服务/一次性契约/非法配置/通道缝 |
| `UnityProject/Assets/ZRender/Tests/ZrPlanetWiringTests.cs` | 新增 426 行（8 用例） | 接线值一致性/viewRel 逐位/静默路径/相机相对世界矩阵恒等式/门控失败整体关闭/单驱动守卫/设备集成（延迟一帧+账目+drawn+culled 覆盖）/G4-R1 seam 成本测量 |
| `.meta` ×9（新目录/新文件的 Unity 自动生成件） | 自动 | 不手改 |

**本任务未触碰**：`Native/**` 零改动（G4-R1 原生侧按 MISSING 回主控，§4）；TASKS.md / HANDOFF / WORKLOG 零改动；无 git commit。

**工作区甄别（并行任务产物，非本任务改动，如实登记）**：`UnityProject/Assets/Plugins/ZRender/zrender.dll` 的修改来自并行任务 B-PROBE-01 的原生构建部署（本任务未触 dll；EditMode 门控用例 `EnsureLoaded_ReturnsApiVersion3` 在该 dll 上全绿，版本兼容性成立）；`Docs/Logs/TASK-B-PLANET-08.md` 与 `Tools/Probes/out/*` 为主控派发简报与 B-PROBE-01 在途产物；TASKS.md 的 1 行改动为主控派发态。

### 2. SetLod 决策选型（简报 §1-1 第 2 点）

**选型：PlanetLodLevel（Native/zplanet/planet_math.h:454）迟滞口径的 C# 逐式镜像**（`ZrPlanetChunkStreamer.DecideLodLevel`，public static 纯函数），不用 P/Invoke 纯函数决策。理由：

1. **P/Invoke 案不可行**：zrender.dll 六导出（ChunkCreate/Destroy/SetOrigin/SetLod/GetStats/ClearAll）只管"应用"，没有任何"决策"导出——P/Invoke 决策函数需要新增 Native 导出 = 动 Native/zplugin = 越本任务白名单（与 G4-R1 同理只能 MISSING 回主控）。
2. **成本反向**：决策输入是"相机位置 + 每块锚点距离"，输出是整数档位。原生纯函数方案必须对每块跨 ABI 调用一次（可见集 ~2600 次 P/Invoke/帧）；C# 等效把"候选枚举 + 决策"合并在锚点表的一次遍历里（double 域点积，无精度敏感性——决策输出是整数，不存在量化误差回流）。
3. **一致性保障**：C# 镜像与原生逐式同构——进近档需严格 `dist < nearDist − hyst`、退远档需严格 `dist > nearDist + hyst`、带内与恰在阈值**不切换**（原生 doctest 边界语义）。EditMode 用例 `DecideLodLevel_*` 三条钉住（810km−ε→近 / 810km 恰等→远 / 990km 恰等→近 / 990km+ε→远 / 带内双侧保持）。
4. 口径来源：B-TECH-04 两档迟滞（900km 阈值 / ±90km 迟滞）进 `ZrPlanetStreamConfig` 默认值；距离口径 = 相机到 chunk 锚点的 double 弦距（900km 弦/弧差 <0.1%，被 90km 迟滞带淹没——如实登记）。

### 3. 分帧预算策略（简报 §1-1 第 3 点）

**成本加权预算 + 近档硬帽**（`ZrPlanetStreamConfig` 默认：`MaxWorkMicrosPerTick=9000`、`MaxNearTierOpsPerTick=1`）：

- 成本估算常量（构成与来源逐项登记在 `ZrPlanetChunkStreamer` 类注释）：远档 700µs = ChunkCreate 全路径实测 516µs（B-PLANET-04 E8：513.461µs）+ seam 扫描 ~80µs（G4 估算 0.5s/6144 块）+ 有邻居共享边重生成 ~110µs（均值量级）；近档 8500µs = 实测 7,446µs + 扫描 ~80µs + 重生成 ~880µs（4 缘×64×2×~1.7µs）。
- **每 tick 发操作前先扣预算**：估算成本超预算的操作延后到后续 tick（`DeferredWorkCount` 可观测）；近档操作另有硬帽 ≤1/tick。预算**严格钳制**：预算 0 = 0 操作（有测试钉住）；预算 ≥ 最小操作成本才有进度（契约写明：预算必须 ≥ 近档成本才能推进近档，默认 9000 > 8500 ✓）。
- 与简报"如 ≤2 个近档/帧"的关系：简报给的是示例策略。近档单次真实原生成本 ≈8.4ms（B-PLANET-04 实测 7.4ms + seam 摊入），放行 2 次/帧 = 16.8ms 尖峰 > 60fps 整帧预算——故默认改为成本预算 9000µs（自然得出 ≤1 近档操作/帧）+ 显式硬帽 1 双保险。**这是对简报示例数字的修正性偏离，依据 = B-PLANET-04 E8 实测**。
- 断言（EditMode，全绿）：`Tick_BudgetCapsOpsPerTick_AndDeferredWorkProgresses`（预算 800µs 下每 tick ≤1 操作、活跃数单调不减、收敛后账目恒等）、`Tick_NearCapAllowsAtMostOneNearOpPerTick`（预算 1e6µs + 硬帽 1 → 贴地期望集恰 1 块且近档创建 ≤1）、`Tick_ZeroBudget_StopsAllCreates`（0 预算 = 0 创建）。
- 推进语义：销毁不占预算（P/Invoke 微秒级，句柄立即失效、GPU 下一 RenderFrame 释放）；同 tick 内按"离相机方向角余弦降序"服务（最近优先，`Tick_CameraOnAnchorDirection_AnchorChunkServedFirst` 钉住）。

### 4. G4-R1 接缝核查开销处理——MISSING + 最小 diff 回主控

**结论：档位门 / 哈希索引两案都必须改 `Native/zrhi/d3d12/zrhi_d3d12_unity_target.cpp`，本任务白名单零 Native 改动 → MISSING，列出最小 diff 回主控裁决。**

- 事实链：seam 核查（`SeamCheckNear`，unity_target.cpp:868）只在渲染线程 RenderFrame 的 (b) 上传段（:1065）与 (c) 档位应用段（:1112）执行——**托管侧无任何接缝核查代码可加门**；`findNeighbor` 每邻居 O(8192) 槽扫，每块 4 邻。
- **托管侧实测数字**（EditMode 设备用例 `Integration_SeamCheckCost_AdjacentGridVersusScatteredChunks`，真 dll + D3D12，远档 256 块，3 轮取最小，MESSAGE 原文）：
  - 相邻 16×16 网格（有邻居，扫描找到即提前退出）：**166.433ms** ≈ 650µs/块
  - 互不相邻散布（无邻居，4×O(8192) 全表扫）：**186.967ms** ≈ 730µs/块
  - 差值 **−80µs/块**：无邻居反而更慢——证明 seam 槽扫是远档上传的**每块固定税**（~100-200µs/块量级，与有无邻居弱相关）。对照 ChunkCreate 全路径 516µs（B-PLANET-04 E8，不含渲染段），渲染段 seam 扫描占 ~15-30%。
  - 全量外推（计算）：6144 远档块 × ~150µs ≈ **0.92s** 渲染线程 CPU——与 B-PLANET-04 G4 的 ~0.5s 估算同量级，该开销在现行 dll 上会实际发生。
- **托管侧已有的钳制（不依赖原生修复）**：分帧预算把"每帧进入上传段的块数"钳在 ~12-15 远档/帧（9000µs ÷ 700µs）→ 渲染段 seam 税 ≤ ~2-3ms/帧；7.4ms 级尖峰同样被钳为 ≤1 近档/帧。**即：G4-R1 在托管桥层面已被预算封顶为每帧常数开销，不阻塞验收；但每块固定税本身只能靠原生门消除。**
- **MISSING 最小 diff（待主控裁决后由后续原生任务落地；门体 3 行，与 G4 裁决"v1=近档核查"及该函数头注释逐字对齐）**：

```diff
--- a/Native/zrhi/d3d12/zrhi_d3d12_unity_target.cpp
+++ b/Native/zrhi/d3d12/zrhi_d3d12_unity_target.cpp
@@ void SeamCheckNear(uint32_t faceId, uint32_t cx, uint32_t cy, uint32_t cellsPerEdge,
     uint32_t tier)   // 函数定义于 :868
 {
+    if (tier == 0u) {
+        return; // G4-R1：远档共享边一致由构造性路由+test_planet doctest 锁定（G4 裁决 v1=近档核查）；
+                // 免每块 4×O(8192) 槽扫（托管实测 ~150µs/块，6144 全量 ≈0.9s 渲染线程 CPU）
+    }
     const uint32_t res = TierRes(tier);
```

  - 哈希索引案（简报第二选项）评估：需在 chunk 表加 (face,cx,cy)→slot 索引并在 Create/Destroy/SetLod/ClearAll 四路径维护，diff 面 ≥30 行且引入新不变量；对"消除远档固定税"收益相同、风险更高——**不推荐**，供主控比选。
  - 落地前提：B-PLANET-08 若裁决扩展核查范围（全档/哈希索引），以 B-PLANET-08 为准重新定门；本 diff 仅消除与 G4 裁决及函数注释不符的"远档全量核查"现状。

### 5. viewRel 纯旋转构造并入 WorldOriginMath 唯一边界（简报 §1-2）

- **构造唯一实现**：`WorldOriginMath.ToCameraRelativeView(Matrix4x4 worldToCamera)`（Game/Domain/World，+25 行）——平移列（m03/m13/m23）归零、旋转逐位保持。语义 = 设计稿 §3.1"double 域构造后一次 cast"的行星版等价形态：**相机平移一次都不进 float**（它经 `ZR_Unity_SetCameraOrigin(double)` 直传原生，chunk 模型平移由原生 double 域 `float(anchorAbs−camAbs)` 承载）；旋转元素来自相机四元数（局部量），与相机离原点距离无关，无大坐标量化问题。函数文档写明"调用点禁止在此外自建行星视图"。
- **调用点唯一**：生产路径仅 `WorldOriginPlanetLink.PushOriginAndViewRel()`（Game/View/World）一处调用，发布进 `ZrPlanetChannel`；`ZRenderSceneBridge` 只消费不构造。
- **边界守恒证据**：本任务全链路 double→float 体检——相机绝对原点全程 double（UnityToWorld 加宽 → SetCameraOrigin double 直传 → 原生域内相减）；接线链路新增 float 运算只有 viewRel 的旋转部分（float 精确）与 `ToCameraRelativeWorld` 的物件平移列（物件−相机，幅值 ≤4km，ULP 0.488mm，A-WORLD-01 §6.4-4 容差口径内）。架构校验 PASS（purity 扫描含 Assets/Game/Domain）。
- **物件世界矩阵配套**：viewRel 帧里 SetCamera 的平移为 0，物件世界矩阵必须自己减掉相机 Unity 坐标（`ZrSceneMath.ToCameraRelativeWorld`，T(−cam)·localToWorld，对任意父子层级成立），否则物件会被平移 Rᵀ·cam 出画面——这是 viewRel 落地的必然配套，实现于 ZRenderSceneBridge（通道启用帧才生效；通道关闭回退全量视图旧路径，与 B-UNITY-03 行为逐字一致）。**如实登记为设计缺口 G-07-1**：设计稿 §2 时序只写"SetCamera(viewRel)"，未写物件 world16 的配套相机相对化；本任务按数学必然性保守补齐（纯托管侧、通道门控内、可回退），待主控追认。

### 6. WorldOrigin 接线拓扑与 G6 双快照契约（简报 §1-1 第 4 点 / §1-2）

**拓扑（跨程序集接缝，零新 asmdef 引用）**：

```
WorldOrigin（Game.View，权威 double OriginOffset）
  └─ WorldOriginPlanetLink（Game.View/World，LateUpdate 每帧）
       ├─ 门控：ZrPlanetNative.EnsurePlanetChannelReady()（一次；失败=Disable+一行日志+每帧静默）
       ├─ ZR_Unity_SetCameraOrigin(UnityToWorld(camera.position))   ← double 直传，无 float 化
       └─ ZrPlanetChannel.PublishCameraRelativeView(WorldOriginMath.ToCameraRelativeView(...))
ZrPlanetStreamingDriver（ZFrame.Render/Runtime/Planet，Update/TickOnce）
  ├─ 门控（同上，独立判定；GateProbeForTests 可注入失败路径测试）
  ├─ streamer.Initialize → ZR_Planet_SetWorld（G6：全进程唯一托管调用点）
  ├─ ZrPlanetNative.GetCameraOrigin() 读回当帧原点（与 Link 的推送经"原生汇合"解耦——
  │   本程序集不引用 Game.View/Game.Domain，避免 Track B→Track A 反向依赖）
  └─ streamer.Tick(原点) → 预算内 Create/Destroy/SetLod
ZRenderSceneBridge（ZFrame.Render，管线 Render 内）
  └─ ZrPlanetChannel.TryConsumeCameraRelativeView → SetCamera(viewRel) + world16 相机相对化
```

- 接线值一致性断言（简报 §1-4）：`Link_Push_SetsNativeOriginToWorldOriginDerivedValue`——GetCameraOrigin 三分量与 `WorldOrigin.UnityToWorld(cameraPos)`（= OriginOffset(double) + 相机 float 坐标，经 ToWorld 加宽）**逐位相等**；`Link_Push_ViewRelHasZeroTranslation_AndRotationPreserved`——发布矩阵平移列恰 0、上 3×3 与 worldToCameraMatrix 逐位相等。
- 时序：Link.LateUpdate（Update 重定位之后、渲染之前）；重定位不变性使推送值在重定位前后数学恒等（camAbs = origin + pos 守恒），故不依赖脚本执行顺序。
- 静默哲学：无 WorldOrigin / 无相机 → 静默不动作（幂等可重复调，WorldOrigin 同款）；门控失败 / SetCameraOrigin 失败 → 通道整体关闭 + 一行日志（`ZrPlanetChannel.Disable` 状态翻转才打，不刷屏）。

**G6 双快照同步契约（本任务定版）**：

- `ZrPlanetChunkStreamer.Initialize` 是**全进程唯一的托管 SetWorld 调用点**（重复 Initialize 显式抛 InvalidOperationException，有测试钉住）；zplugin 权威快照与渲染侧镜像由同一次原生 SetWorld 调用内先后写入（B-PLANET-04 G6 实现），托管只调一次 → 无漂移窗口，与设计稿 §6.1/§12-11"初始化期单行星静态"契约闭环。
- 每帧接线方（Link）**绝不调 SetWorld**，只推 SetCameraOrigin；Driver 与 Streamer 之外无任何托管 SetWorld 路径。
- 单行星上下文守卫：`ZrPlanetStreamingDriver` 进程内至多一个活跃实例（第二个 ActivateForRuntime 被拒用+一行日志，有测试钉住）。

### 7. 账目断言清单与数字（简报 §1-4）

| # | 断言 | 用例 | 结果 |
|---|---|---|---|
| A1 | 恒等式 `createTotal − destroyTotal == liveChunks`（Tick 后 + RenderFrame 后） | `Tick_CreateAndDestroy_ReconcileWithNativeStats` / `Integration_StreamTickRenderFrame_DelayedUploadAndAccount` | 绿 |
| A2 | 托管 CreatedTotal/DestroyedTotal 与原生 createTotal/destroyTotal 增量逐项相等 | 同 A1 | 绿 |
| A3 | 托管 LiveCount == 原生 liveChunks | 同 A1 | 绿 |
| A4 | (face,cx,cy) 去重：相机不动不产生重复 Create | `Tick_IdempotentCamera_NoDuplicateCreates` | 绿 |
| A5 | 延迟一帧：创建后 pendingUploads>0 → RenderFrame 后 ==0、vertexBytes>0 | `Integration_StreamTickRenderFrame_DelayedUploadAndAccount` | 绿 |
| A6 | 剔除覆盖：drawnLastFrame + culledLastFrame == liveChunks | 同 A5 | 绿 |
| A7 | Dispose → ClearAll → liveChunks==0 | `Dispose_ClearsAllNativeChunks` | 绿 |
| A8 | 预算生效（每 tick 操作数有界 / 待办推进 / 零预算零操作 / 近档硬帽 ≤1） | `Tick_BudgetCapsOpsPerTick...` / `Tick_NearCap...` / `Tick_ZeroBudget...` | 绿 |
| A9 | 迟滞边界（恰在阈值不切换，与原生 doctest 同语义） | `DecideLodLevel_*` ×3 | 绿 |
| A10 | 接线值一致性（原点逐位 + viewRel 逐位） | `Link_Push_*` ×2 | 绿 |
| A11 | 门控失败 = 整体关闭（Driver 不初始化 Streamer、通道不启用）+ 单驱动守卫 + 通道缝状态机 | `Driver_GateFailure...` / `Driver_SecondInstance...` / `Channel_*` ×2 | 绿 |
| A12 | 流式管理器自检：恒等式破坏时 LogError 一次（生产侧防线，正常路径不触发） | 实现（ZrPlanetChunkStreamer.Tick 末段） | 代码审查 |

测试数字：**EditMode 1211/1211（0 失败 0 跳过），较基线 1182 增 29 条**（WorldOriginMathTests +4 / ZrPlanetStreamingTests +17 / ZrPlanetWiringTests +8，`grep -c "[Test]"` 实测 18/17/8，合计恰 29）；**arch PASS（27 asmdef / 0 违规）**；**naming PASS（0 违规）**。

**新 asmdef 矩阵登记（简报 §1-5）**：本任务**未新增任何 asmdef**（流式管理器/Driver 进既有 `ZFrame.Render`，通道缝进既有 `ZFrame.Render.Bindings`，接线组件进既有 `Game.View`，测试进既有 `ZFrame.Render.Tests`）——按简报口径豁免登记。唯一 asmdef 改动 = `ZFrame.Render.Tests.asmdef` references +Game.Domain/Game.View（Tests 程序集豁免矩阵，arch PASS 实证）。B-PROBE-02 之债无复发条件。

### 8. 缺口与修复记录（第一轮 14 红 → 全绿的工程记录）

| # | 红因 | 修复 |
|---|---|---|
| F1 | `BuildChunkTable` IndexOutOfRange（10 例连坐）：表索引用了打包位布局 `(face<<24)|...` 而数组是线性布局 | TableIndex 改线性布局 `face·cells²+cy·cells+cx`，预算环解码同步 |
| F2 | `ToCameraRelativeView_PointTransform` 恒等式误用 TRS 位置当被移除平移——实际 `TRS().inverse` 平移列是 Rᵀ·(−pos) | 恒等式改为从视图矩阵本身读平移列（数学上更普适） |
| F3 | `Channel_ConsumeOnce` 断言写错期望（把任意发布矩阵当 viewRel 断平移零） | 改为逐元素与发布矩阵相等 |
| F4 | `Channel_DisableClearsPublishedView`：关闭期发布的 viewRel 在重启后复活 | **产品修复**：`PublishCameraRelativeView` 在通道关闭时丢弃发布（不许半启用的状态残留） |
| F5 | `Driver_SecondInstance`：EditMode 下 AddComponent 不触发 OnEnable | 注册守卫抽为 `ActivateForRuntime()`（OnEnable 与测试共用同一实现路径） |
| F6 | `Link_WithoutCamera`：Camera.main 退路在编辑器含相机场景下非空，断言环境敏感 | 改测确定性分支（无 WorldOrigin → 静默），相机缺席路径依赖场景状态如实注明 |

### 9. 已知受限与遗留风险（如实登记，不阻塞）

1. **G4-R1 原生固定税未除**：MISSING §4——托管预算已钳制为每帧常数，但 6144 全量流式累计 ~0.9s 渲染线程 CPU 仍存在，待主控裁决最小 diff 落地。
2. **G-07-1 物件世界矩阵相机相对化**：设计稿未写明的 viewRel 必然配套（§5），保守实现于通道门控内、可回退，待主控追认。
3. **白名单路径标注偏差（如实登记）**：简报白名单写"ZFrame/Core/World（WorldOriginMath 并入边界）"——该路径不存在；A-WORLD-01 实际布局为 `Game/Domain/World`（WorldOriginMath/WorldPosition）+ `Game/View/World`（WorldOrigin）。本任务按实际路径施工：WorldOriginMath.cs 并入边界（白名单字面授权）+ 新增 `WorldOriginPlanetLink.cs` 于 `Game/View/World`（接线使命的必然落点，该目录属 A-WORLD-01 白名单区域"Game/View/World/**"，且 Game.View 已引用 ZFrame.Render.Bindings——A-AND-08 既有 GUID 引用，零新 asmdef）。请主控追认该解释。
4. **v1 单相机契约**：管线只提交 cameras[0]（B-UNITY-03 既有口径）；viewRel 交接 consume-once，多相机场景行为未定义（与管线现状同边界）。
5. **seam 成本测量口径**：LastFrameMs 含 VB 创建/上传/快照全段（不止 seam），256 块级差值 −80µs/块为相邻/散布组的净差（含扫描提前退出效应），非纯重生成成本——量级与构成已在 §4 拆解，探针级精确拆分属 B-PLANET-09。
6. **驱动 Update 直推 TickOnce**：播放时每帧一次流式决策（含排名重建阈值控制）；真机帧耗基线属 B-PLANET-09/11。
7. **worldToCameraMatrix 旋转精度**：Unity 由 float 四元数构造，无大坐标减法（平移列被归零）；若未来阈值调到 10km 以上，按 A-WORLD-01 §6.4-5 放宽物件矩阵容差。

### 10. 白名单自检与规范

- `git status` 本任务改动 = 上表 13 项（5 修改 + 8 新增源/测试文件）+ 9 个 Unity 自动 .meta + 本日志；`git diff -- Native/` 为空；zplugin/zrender/zplanet 源树零触碰；TASKS/HANDOFF/WORKLOG 零触碰。工作区中 `zrender.dll` 修改与 `TASKS.md` 1 行为并行任务/主控产物（§1 已甄别）。
- 全部文本文件 UTF-8 无 BOM（新文件首字节 `2f 2f 20` od 亲验）、LF（`grep -c $'\r'` = 0）；.meta 为 Unity 自动生成未手改。
- 零第三方依赖；未 git commit；Unity 批处理单实例纪律（tasklist 前检 + run_tests.ps1 内建 lockfile 清理/Git Bash 启动/-runTests 不带 -quit）全程遵守。
- 施工代理未写验收清单与验收结论（属主控/验收代理职责）。

### 11. 验收建议

建议验收候选 **PASS**：流式管理器 + 接线落地、账目断言 A1-A12 全绿、EditMode 1211/1211 只增不减、arch/naming PASS、门控纪律（整体关闭+一行日志）有测试钉住、WorldOriginMath 唯一边界保持（构造/调用点唯一 + arch PASS + 4 条 viewRel 用例）。两个待追认项不阻塞：G4-R1 MISSING（§4，含最小 diff）与 G-07-1（§5）。

## 主控裁决（2026-11-14，施工退回三项）

1. **G4-R1（原生 seam 固定税）**：**改判采纳 3 行门** `if (tier == 0u) return;` @SeamCheckNear——已捎话给正在施工的 B-PLANET-08（接缝专项，文件在其白名单）并入其 scope，含 doctest 锁定+复测消除验证。本任务托管预算钳制（≤2-3ms/帧）与 08 的原生消除是互补两层，均保留。
2. **G-07-1（物件世界矩阵相机相对化）**：**追认**——viewRel（D3）的必然配套：相机平移不进 float 后，物件世界矩阵必须同为相机相对，否则背景/物件基准不一致。登记进设计稿勘误清单（与 G1 同批）。
3. **简报路径偏差（ZFrame/Core/World 不存在）**：**追认**——系主控简报笔误，A-WORLD-01 实际路径 Game/Domain/World + Game/View/World 为正确落点，施工处置正确。
4. 分帧预算对简报示例（≤2 近档/帧）的修正性偏离：**接受**——2×8.4ms=16.8ms 会吃光整帧，成本加权预算（9000µs/tick+近档硬帽 ≤1）依据 E8 实测，修正正确。

B-PLANET-07 验收范围：托管侧全部；原生 3 行门归 B-PLANET-08 验收（两任务验收报告交叉引用）。

## 验收结论（验收代理，2026-11-14）

**总结论：PASS**（J1–J8 全过；三项主控裁决施工处置逐条核对一致；2 条记账性备注不阻塞，见"给主控的备注"）。

### 通过行（全部亲跑，exit 0）

```
EditMode: total=1211 passed=1211 failed=0 skipped=0   # Unity exit 0，results.xml 本次亲写（脚本证据新鲜度纪律）；证据 TempLogs/runtests_editmode.xml
arch    : PASS exit 0（asmdef 扫描 27 / 期望矩阵 13（Tests 豁免）/ 违规 0；报告时间戳 2026-11-14 07:09:47 = 验收亲跑轮，非施工遗留）
naming  : PASS exit 0（违规 0；报告时间戳 07:10:17 = 验收亲跑轮）
目标用例：Streaming 17 + Wiring 8 + viewRel 4 = 29 条在本轮 XML 逐条 result=Passed（Integration 两条未 Skip——真 dll + D3D12 现场跑通）
门控实证：EnsureLoaded_ReturnsApiVersion3 / PlanetNative_ChannelGate_PassesWithApiVersion3 绿——现行 dll（MD5 35bf8044…，双侧一致）= v3，真实门控通过路径成立
```

### 逐条证据

| # | 结论 | 证据（验收代理亲跑/亲验） |
|---|---|---|
| J1 | **PASS** | `git diff --numstat` 托管侧 5 修改与施工清单逐字一致（WorldOriginMath +25/0、WorldOriginMathTests +94/0、ZRenderSceneBridge +25/−2、ZrSceneMath +16/0、asmdef +2/0）；新增 7 文件 wc -l 实测 765/209/106/11/109/478/426 与报告逐字一致。**Native diff 甄别为 B-PLANET-08 并行在途**（验收期间从 2 文件增长到 3 文件，均为 08 标注件）：test_planet_channel.cpp（"TASK-B-PLANET-08：接缝专项"测量用例）、zrhi_d3d12_unity_target.cpp（TestNeighborMaskForUnitTests 测试钩子，注释明示仅 doctest）、zrhi_d3d12_window.cpp（验收中途新现，"TASK-B-PLANET-08：共享边贴地像素检查"段）——非本任务触碰；本任务白名单不含 Native，施工侧 Native 零改动成立。`zrender.dll` 修改 = 并行构建部署产物（双侧 MD5 35bf8044… 逐位一致，报告 §1 已甄别）。TASKS.md 恰 1 行 = B-PLANET-08 行 todo→doing（主控并行派发态；B-PLANET-07 行的 doing 在 HEAD 94e0519 已存在）；HANDOFF/WORKLOG 零 diff。meta 实测 **8**（7 文件 meta + Planet.meta 目录 meta）——报告写 ×9 差 1，记账性偏差（所有 meta 均在盘、均 Unity 自动生成，无越界物） |
| J2 | **PASS** | 读码：C# `DecideLodLevel`（ZrPlanetChunkStreamer.cs:243-252）与原生 `PlanetLodLevel`（planet_math.h:454-460）**逐式同构**——进近档严格 `dist < nearDist − hyst`、退远档严格 `dist > nearDist + hyst`、`prevLod >= 1` 分支与整数输出完全一致；选型理由成立（六导出无"决策"导出，P/Invoke 案需新增 Native 导出越白名单）。3 条边界用例本轮亲跑绿：FarSide（810km−ε→近 / 810km 恰等→远不切换 / 900km 带内→远）、NearSide（990km 恰等→近不切换 / 990km+ε→远 / 贴地→近）、Band（带内双侧保持）——恰在阈值不切换的 3 条边界语义与原生 doctest 同构钉死 |
| J3 | **PASS** | 读码：`CreateDefault` 默认 MaxWorkMicrosPerTick=**9000**、MaxNearTierOpsPerTick=**1**、远档 700µs/近档 8500µs（成本构成注释逐项有源：B-PLANET-04 E8 实测 516/7,446µs + seam 摊入）。亲跑 3 用例绿：预算 800µs 下 64 tick 每 tick ≤1 操作且活跃数单调不减、收敛后托管账目与原生 `CreateTotal−DestroyTotal` 一致（DeferredWork 推进语义成立）；预算 1e6µs + 硬帽 1 → 贴地期望集恰 1 块、近档创建 ≤1；预算 0 → 0 创建 0 活跃。对简报示例（≤2 近档/帧）的修正性偏离已获主控裁决第 4 条追认 |
| J4 | **PASS** | 25 用例（Streaming 17 + Wiring 8）本轮全绿，断言体逐条读码核对：A1 恒等式在 Tick 后与 RenderFrame 后两处断言（Tick_CreateAndDestroy / Integration_StreamTickRenderFrame）；A2 托管 Created/Destroyed 与原生增量逐项相等、A3 托管 LiveCount==原生 liveChunks（各两处）；A4 收敛后相机不动零重复 Create、计数稳定；A5 pendingUploads>0→RenderFrame→==0 且 vertexBytes>0；A6 drawn+culled==liveChunks；A7 Dispose→liveChunks==0；A8 预算三用例；A9 迟滞三用例；A10 原点三分量与 `UnityToWorld(cameraPos)` 逐位相等 + viewRel 平移列恰 0 且 3×3 旋转逐位一致（Link_Push ×2）；A11 门控失败 Streamer 保持 null 不碰 SetWorld + 通道不启用 + 第二 Driver 拒用 + consume-once/关闭丢弃不复活（Channel ×2）；A12 读码：Tick 末段 LastAccountOk 计算 + `_accountErrorLogged` 翻转才 LogError 一次（生产防线，绿跑不触发） |
| J5 | **PASS** | 全仓 grep：`ToCameraRelativeView` 定义唯一在 WorldOriginMath.cs:98（m03/m13/m23 归零、其余逐位保持，文档明示"调用点禁止外建"）；生产调用点唯一在 WorldOriginPlanetLink.cs:106（其余命中均为 Game/Tests 用例与注释）；ZrPlanetChannel 只搬运不构造（Publish 存、Consume 取走即清）；ZRenderSceneBridge 仅经 `TryConsumeCameraRelativeView` 消费（diff 读码），通道关闭回退全量视图旧路径（B-UNITY-03 行为）。A-WORLD-01 铁律抽查：ToUnity 先 double 相减最后一次 cast 未动；相机绝对原点全程 double（UnityToWorld 加宽 → SetCameraOrigin(double) 直传 → 原生域内相减）；新增 float 运算仅 viewRel 旋转（精确）与 ToCameraRelativeWorld 物件平移列（≤4km，报告登记的容差口径）。arch purity 扫描含 Assets/Game/Domain，PASS |
| J6 | **PASS** | 读码：`EnsurePlanetChannelReady`（ZrNative.cs:637）ApiVersion<3 抛 InvalidOperationException；Link 门控一次→失败 Disable 一行日志（`_disableLogged` 状态翻转才打）→每帧静默；SetCameraOrigin 失败同路径；Driver 门控失败→`_gateFailed` + Disable 一行 + Streamer 恒 null。无半启用：通道关闭期发布被丢弃（PublishCameraRelativeView 直接 return）、Consume 需 `_enabled`、Disable 清 `_hasCameraRelativeView`。亲跑：Driver_GateFailure（GateProbeForTests 注入失败，与真实门控同一 RunGate 出口）绿、Channel×2 绿、真实 v3 dll 门控通过用例绿。真实 ApiVersion<3 场景在 v3 dll 上不可复现（经探针注入同一代码路径覆盖 + 真实通过路径独立覆盖），如实登记 |
| J7 | **PASS（1 条备注）** | 亲跑 EditMode 全量：1211/1211/0 skipped=0，Unity exit 0，results.xml 本次亲写。只增不减成立：基线 1182（TASK-B-PLANET-06 验收轮确立）→ +29（viewRel +4 / Streaming +17 / Wiring +8；grep [Test] 文件总量 18/17/8 与新增数 4/17/8 对账吻合）。arch/naming 验收亲跑 PASS（27 asmdef / 0 违规；naming 0 违规）。**备注**：验收清单写"≥1212"，实测 1211 差 1——简报判据为"≥1182 只增不减"已满足；并行任务无 Unity 侧用例入树（B-PLANET-08 WIP 纯原生），判为清单算术口径偏差，非缺交付 |
| J8 | **PASS** | 7 新文件 + 本日志首字节 od 亲验无 BOM（.cs=`2f 2f 20`，log=`23 20 54`）；`tr -dc '\r' \| wc -c` 实测 **0 CR 字节**（LF——首检 grep -c $'\r' 计数等于行数系引号展开伪象，tr 字节口径复检定案）；5 个 tracked 修改文件 `git ls-files --eol` 全 i/lf w/lf。日志四节：任务简报/施工报告/主控裁决在位，本「验收结论」节追加后四节齐 |

### 三项主控裁决核对

| 裁决 | 施工处置 | 核对 |
|---|---|---|
| G4-R1 改判采纳 3 行门、并入 B-PLANET-08 scope | 施工 = MISSING + 最小 diff 回主控（报告 §4），Native 零触碰；托管预算钳制（≤2-3ms/帧）保留为互补层 | **一致**。在途 Native diff 为 08 的测量工作（test hook + 缝宽实测用例），3 行门尚未落（08 施工中）——归 08 验收，非本任务事项 |
| G-07-1 追认（物件世界矩阵相机相对化） | `ZrSceneMath.ToCameraRelativeWorld`（T(−cam) 只改平移列）+ 桥内仅 planetRelative 帧应用、通道关闭回退 | **一致**（恒等式用例 SceneBridge_CameraRelativeWorld 绿） |
| 简报路径偏差追认（Game/Domain/World + Game/View/World 为正确落点） | WorldOriginMath 并入 `Game/Domain/World`、Link 落 `Game/View/World`，零新 asmdef 引用 | **一致**（asmdef 仅 Tests references +Game.Domain/Game.View，arch PASS 实证） |

### 给主控的备注

1. **两条记账性偏差（不阻塞，无需返工）**：①施工报告".meta ×9"实为 **8**（7 新文件 + Planet 目录 meta）；②报告 §7"grep -c [Test] 实测 18/17/8，合计恰 29"表述易误读（18 为 WorldOriginMathTests 文件总量含 14 条既有，新增合计 = 4+17+8 = 29，对账成立）。验收清单"≥1212"与实测 1211 差 1，简报判据 ≥1182 已满足，判为清单算术口径偏差。
2. **G4-R1 交叉引用提醒**：本任务托管实测（相邻 166.433ms vs 散布 186.967ms，差值 −80µs/块 = seam 槽扫每块固定税）已由 Integration_SeamCheckCost 用例钉住；B-PLANET-08 落 3 行门后的消除复测可直接对照该用例数字。
3. **dll 基线注**：现行 Unity 侧 dll（35bf8044…）与 B-PLANET-04 验收终态（6881a7c1…）不同——为并行构建部署产物，报告 §1 已甄别；本轮 EditMode 全绿 + 真实渲染集成用例（Integration_StreamTickRenderFrame 未 Skip）在该 dll 上成立，版本兼容性实证。
4. **验收纪律**：全程未修生产代码、未 commit、未触碰 Native 构建链（未跑 build_native.ps1，原生构建归 B-PLANET-08）；Unity 单实例纪律遵守（tasklist 前检空闲后启动）。
