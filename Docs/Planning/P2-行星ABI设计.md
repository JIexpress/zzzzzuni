# P2 行星 ABI 设计稿（TASK-B-PLANET-01）

> 日期：2026-11-14｜轨道：B/PLANET｜性质：**文档设计任务，不含生产实现**
> 来源：`Docs/Planning/ZRender-改进计划-2026-11-13.md` §3-P2（四项 ABI 要求与完成判据）+ §2（抉择 B：专用通道）+ §4（明确不做）+ §5（探针规矩）
> 证据规则：引用的实测数字一律带来源（文件:行 或 任务日志节号）；推导值标「计算」；未经实测的量级判断标「估算」；拿不到的写 MISSING（不填 0、不填默认值——改进计划 §5 探针规矩）。
> 本文件不改任何 .h/.cpp/.cs；主控按 §11 把 B-PLANET-02..12 登入 TASKS.md。
> 修订：2026-11-14 R1——F1-F7 定点修订（ApiVersion 链 2→3 等，见 Docs/Logs/TASK-B-PLANET-01.md）

---

## 0. 结论速览

四项 ABI（完整签名与协议见 §3–§6，可整体抄入 `Native/zplugin/zplugin.h` 的权威文本在 §14）：

| # | ABI | 签名（节选） | 一句话语义 |
|---|---|---|---|
| 1 | double 相机原点 | `ZR_Unity_SetCameraOrigin(double x, double y, double z)` + `ZR_Unity_GetCameraOrigin(double*)` | 每帧把 double 绝对相机原点交给原生；此后 view/proj 仍是 float 矩阵，但**相机相对**提交，chunk 模型平移由原生在 double 域相减后一次 cast |
| 2 | chunk 句柄 + double 变换 | `ZR_Planet_ChunkCreate(const ZrPlanetChunkDesc*, uint64_t*)` / `_ChunkDestroy(uint64_t)` / `_ChunkSetOrigin(uint64_t, double x,y,z)` | 顶点由原生按 planet_math 规范路由生成；句柄 = 高 32 代际 + 低 32 槽位（与 mesh 句柄同协议）；锚点 double 绝对坐标，顶点局部 float 静态存储 |
| 3 | LOD 级别与邻居 | `ZR_Planet_SetLod(uint64_t handle, uint32_t lodLevel, uint32_t neighborMask)` | 档位变更延迟到下一 RenderFrame 应用；neighborMask 进 ABI 但 v1 仅校验+记录（取舍分析见 §5：真根因是共享边双侧 float 基准，不是邻居信息缺失） |
| 4 | 高度采样 | `ZR_Planet_SetWorld(const ZrPlanetWorld*)` + `ZR_Planet_SampleHeight(double x,y,z, float* outHeightM)` | 纯函数同步采样（无状态、任意线程）；玩法侧（碰撞/放置）预算 ≤1000 次/帧（估算，探针实测校准） |

关键决策（正文有完整论证）：

| # | 决策 |
|---|---|
| D1 | 浮点原点重定位的**权威在 Unity 侧**（`WorldOrigin`，4000m、≥ 触发——A-WORLD-01 生产口径）；原生不重定位。planet_math.h 的 10km/严格> 是测试口径，差异如实登记（§9） |
| D2 | `SetCameraOrigin` 跨帧持久（`BeginScene` 不清）；NaN/Inf 拒绝（`ZR_UNITY_ERR_ARGS`——复用需同步扩充其注释定义，§14） |
| D3 | **chunk 不走 `ZR_Unity_DrawMesh`**——float world16 会重新引入 100km 处 7.8mm 量化（§3.2）；行星通道在 `ZR_Unity_RenderFrame` 内自主绘制，模型平移在原生 double 域计算 |
| D4 | 句柄/错误码采用 `ZR_MeshFree` 风格（显式错误码 + 代际防误用），不用 `ZR_Unity_DestroyMesh` 的 void 静默风格——chunk 是流式动态资源，释放必须可审计（§4.4） |
| D5 | 线程：句柄表 SRWLOCK 保护（`ZR_Upscaler_*` 先例，zplugin.h:59-66）；Create/Destroy/SetLod/SetOrigin/SampleHeight 主线程可调；GPU 上传/释放只在渲染线程 RenderFrame 边界，**延迟一帧**（§8） |
| D6 | `ZR_ApiVersion` 2→3 在 B-PLANET-02 递增（当前已是 2——B-FSR-02 已占用，zplugin.h:20-21、test_smoke.cpp:21）；托管侧启动断言 ≥3 才启用行星通道（§3.5） |
| D7 | SampleHeight 同步；异步回填（工作线程+回调）明确不做（§12） |
| D8 | Android 验收对象 = 行星**能力**的托管等效实现（帧耗+精度实测一次）；ZRender 原生通道 Android 化不做（Vulkan Unity 桥不在 P2，§10/§12） |
| D9 | 高度输出 = 纯函数值；它与渲染网格（三角形线性插值）的贴地偏差幅值 MISSING，由 planet-budget 探针实测（§13） |
| D10 | 行星通道输出仍走现有共享纹理链路（B-UNITY-02/04 双槽），合成归 Unity；本设计不新增合成机制（§2） |

---

## 1. 设计输入与事实基线

### 1.1 已有实测数字（引用必须带来源）

| 数字 | 值 | 来源 |
|---|---|---|
| 100km 处 float ULP | 7.8125 mm（2^-7） | `Native/tests/test_planet.cpp:31`；`Docs/Logs/TASK-A-WORLD-01.md:113,186` |
| naive 全 float 大坐标相对误差（100km 飞行审计） | >1mm，上界 ULP(100km)=7.8125mm | `Native/tests/test_planet.cpp:33-34` |
| 重定位路径近场相对误差 | <1e-6 m（µm 级）；naive/relocated 改善 ≥100×，实测 ~10³× | `Native/tests/test_planet.cpp:36,39`；`Native/tests/planet_math.h:173` |
| double+offset 守恒误差（A-WORLD-01 V6 实测） | 4.366e-11 m（与 float ULP 相差 >7 个数量级） | `Docs/Logs/TASK-A-WORLD-01.md:113,186` |
| float 在行星半径幅值（6.4e6 m）的 ULP | 0.5 m——「行星中心必须 double」的数值理由 | `Native/tests/test_planet.cpp:146-148` |
| 4km 处 float ULP | 0.488 mm（4096=2^12，精确） | `Docs/Logs/TASK-A-WORLD-01.md:148` |
| 10km / 100km 容差放宽口径 | 1.95mm / 7.8mm（日志口径；10km 公式值 2^-10≈0.98mm，见 §9 注） | `Docs/Logs/TASK-A-WORLD-01.md:149` |
| A-WORLD-01 生产形态 | `WorldPosition`(double) + `WorldOriginMath` 纯函数 + `WorldOrigin`（4000m 注册制重定位，≥ 触发）；**未挂载到任何场景**；唯一 double→float 运行时边界 `WorldOriginMath.cs:71` | `HANDOFF.md:29`；`Docs/Logs/TASK-A-WORLD-01.md:18,124,220` |
| planet 测试规模 | 15 条 doctest 全绿；`--planet2`：6144 chunk / 2 LOD / seams=0 / 640×480 / 帧耗 8.359、8.803ms（验收复跑 7.897、8.077ms） | `Docs/Logs/TASK-B-TECH-04.md:31-36,210`；`Docs/TECH-VALIDATION.md:141` |
| chunk 结构 | 每面 32×32 均匀格 × 6 面 = 6144；chunk 弧长 312.7km；锚点 double 绝对坐标，顶点局部 float 静态存储，逐帧相机偏移经 model 平移承载 | `Docs/Logs/TASK-B-TECH-04.md:57-60` |
| LOD 两档 | 远档 16×16（256+裙边 64=320 顶点）/ 近档 64×64（4096+256=4352）；阈值 900km 迟滞 ±90km；全程切换 48 次、VB 升容 19 次、几何重建 3021 次 | `Docs/Logs/TASK-B-TECH-04.md:69-77` |
| 无缝选型 | 同档：共享边同全局整数索引 → 同一 double 表达式 → 逐位一致（3021 次构建 seams=0）；跨档：63/15=4.2 非整数格距比，强制一致采样被否决，裙边 2% 弧长=6.25km（> 跨档边缘高程差上界 ~2km）遮蔽 | `Docs/Logs/TASK-B-TECH-04.md:79-94,119-123` |
| 可见 chunk 数 | 贴地 7-8 / 中空 33→764 / 远观 ~2500-2660；地平线曲率剔除，CB 池 3200 零截断 | `Docs/Logs/TASK-B-TECH-04.md:76-77,119-122,164-165` |
| 帧耗口径与首帧峰值 | QPC 墙钟含 Submit+WaitIdle、无 vsync、640×480 离屏 RGBA8+D32；首帧峰值 88.9ms（6144 VB 池 ~63MB + 首批近档构建，第 2 帧起 <10ms）；B-R9-11 离屏基线 2.045ms | `Docs/Logs/TASK-B-TECH-04.md:156-171` |
| 已知受限 | 裙边是遮蔽非消除；档界高度弹跳（迟滞只抑制档位抖动）；帧耗为 780M 开发机剖面；均匀 32×32 无自适应细分 | `Docs/Logs/TASK-B-TECH-04.md:184-195` |
| 地形参数 | 4 octave fBm、基频波长 36km、幅值 ±2.4km、种子可配（kP2Seed=1337）；确定性契约（同种子同输入逐位一致） | `Native/tests/planet_math.h:408-410`；`Docs/Logs/TASK-B-TECH-04.md:61-66` |
| 地平线距离 | 贴地 2m ≈ 5.048km；600m ≈ 87.4km；61.7km ≈ 888.8km | `Native/tests/test_planet.cpp:565-570` |
| zplugin.h 现状 | zrender.def 导出 46（34 ZR_* + 12 ZK_*）；无 chunk/双精度/重力接口；ApiVersion=2；错误码负值风格 + structSize 协议（Upscaler/FG）+ SRWLOCK 线程安全先例 + Unity 桥单线程（渲染线程）约束 | `Native/zplugin/zrender.def:17-62`；`Native/zplugin/zplugin.h:20-21,30-44,59-66,71-117,133-179,198,344-360`；`Docs/TECH-VALIDATION.md:147` |

### 1.2 MISSING（设计前就缺的数，不许编）

| 项 | 状态 | 补齐路径 |
|---|---|---|
| 单 chunk 顶点生成 CPU 耗时 | MISSING——首帧 88.9ms 里 VB 池创建与顶点生成混在一起，无法拆分（B-TECH-04 §4） | B-PLANET-04 落地时逐档实测 |
| 1080p 帧耗 | 未验证（TECH-VALIDATION §8；改进计划 §3-P2 原文） | B-PLANET-09 探针 |
| IL2CPP / Android 帧耗 | 未验证（TECH-VALIDATION §8） | B-PLANET-12 |
| SampleHeight 单次耗时 | MISSING——纯函数估算亚微秒级（估算），未实测 | B-PLANET-09 探针顺带实测 |
| 纯函数高度 vs 渲染网格插值的贴地偏差 | MISSING（§6.4/D9） | B-PLANET-09 贴地浮空/入地扫描 |
| 共享边顶点 float 化后的实际缝宽（GPU VB 口径） | MISSING——B-TECH-04 运行期 memcmp 的是 double 域规范路由重算值，非 VB 回读（B-TECH-04.md:215①） | B-PLANET-08/09 |
| 780M vs RX 9060 XT 双机帧耗口径 | MISSING（B-TECH-04 帧耗为 780M 剖面） | 探针双机各存档一次 |

---

## 2. 范围与视角纪律：只做行星 chunk 专用通道

改进计划 §2 抉择 B：**不把 ZRender 做成完整管线**——「如果一个改进项，URP 也能做，那就别在 ZRender 里做」。本设计的通道边界：

```
Unity（URP 活跃管线：场景/材质/光照/合成）
   │  共享纹理采样贴屏（B-UNITY-02/04 双槽链路——现状不变）
ZRender RenderFrame
   ├─ 场景路径（现有）：DrawMesh 表（float world16 + rgba）——普通物件，原点附近，float 够用
   └─ 行星通道（本设计新增）：chunk 句柄表 → 相机相对绘制（double 域定位）
        每帧时序（托管侧）：
        WorldOrigin(4000m 重定位，A-WORLD-01) → ZR_Unity_SetCameraOrigin(double×3)
        → ZR_Unity_SetCamera(viewRel float16, proj float16)   // viewRel = 纯旋转（平移分量为 0）
        → 主线程 chunk 表管理（Create/Destroy/SetLod/SetOrigin）
        → ZR_Unity_RenderFrame()                              // 场景路径 + 行星通道同深度缓冲
```

分工判据（每个「要不要进原生」的追问都用它裁决）：

| 事 | 归属 | 理由 |
|---|---|---|
| chunk 顶点生成 / GPU 上传 / 相机相对定位 / LOD 几何 | 原生通道 | Unity 做不了：double 定位 + 6144 chunk 批量管理 |
| 重定位触发与阈值 / 相机 double 累计 | Unity 侧（WorldOrigin） | 已有生产实现（A-WORLD-01），且必须与刚体/粒子/音频同步平移（TECH-VALIDATION §4.3-5） |
| 玩法碰撞 / 放置 | Unity 侧，走 SampleHeight | 原生通道无 PhysX 触点（§12-10） |
| 材质 / 贴图 / 光照 / 阴影 | 不做 | 完整管线化（§12-5）；v1 视觉沿用 planet2.hlsl 高程色带+距离雾+limb 口径（B-TECH-04.md:96-105） |

---

## 3. ABI-1：double 相机原点

### 3.1 它解决什么

现状 `ZR_Unity_SetCamera(float view16, float proj16)` 只有 float 矩阵——相机远离原点时，**托管侧若用 float 大坐标构造矩阵**，相对位置精度被 ULP(绝对幅值) 封顶：100km 处 7.8125mm（实测锚定 test_planet.cpp:31），近场顶点逐帧抖动（B-TECH-03 的 naive 路径审计：naiveMaxErrM > 1mm，重定位路径 < 1e-6 m，改善实测 ~10³×——test_planet.cpp:33-39）。A-WORLD-01 在托管侧已证同一件事：float 直接表示 vs double+offset 守恒，4.366e-11 m vs 7.8125mm，相差 >7 个数量级（TASK-A-WORLD-01.md:113,186）。

机制：**double 域相减、最后一次转 float**（planet_math.h:150-155 `RenderOffsetF` 的「重定位正确路径」；A-WORLD-01 把它定为顺序铁律，全仓唯一 double→float 边界 `WorldOriginMath.cs:71`）。ABI 的职责是把「谁来做这次减法」定下来：

- **相机自身**：托管以相机为原点构造 view（平移分量为 0，纯旋转），double 域构造后一次 cast——托管侧新边界点必须并入 `WorldOriginMath` 同一处（A-WORLD-01 顺序铁律延伸）。
- **chunk/场景物件**：物件相对相机的平移在**原生** double 域计算（`float(objAbs − camAbs)`），见 ABI-2/D3。托管不再逐物件算 float 世界矩阵传给行星通道。

### 3.2 为什么 chunk 不能走 DrawMesh（D3）

`ZR_Unity_DrawMesh(handle, world16, rgba)` 的 world16 是 float。若托管把 chunk 的世界平移 float 化后提交：平移幅值 = 相机离原点距离（重定位后 ≤4km 时 ULP 0.488mm，可接受；但**重定位前的爬升段**幅值到 9010km——B-TECH-04 实测 maxOriginOffset——ULP ≈1m（米级，计算：9.01e6m ∈ [2^23,2^24) → ULP=2^0=1m；较首轮「毫米级」口径更差三个数量级，结论方向不变且更强）且逐帧变化）→ 抖动回流。结论：行星通道绕开 DrawMesh，模型平移由原生逐帧 `float(anchorAbs − camOriginAbs)` 计算（与 B-TECH-04「逐帧相机偏移经 model 平移矩阵承载、顶点零逐帧重算」同构，B-TECH-04.md:57-60）。

### 3.3 签名与协议（权威文本见 §14 组 1）

- `int ZR_Unity_SetCameraOrigin(double x, double y, double z)`：设置本进程行星通道使用的相机绝对原点（行星中心系，米）。NaN/Inf 任一分量 → `ZR_UNITY_ERR_ARGS`；成功 `ZR_UNITY_OK`。**跨帧持久**，`BeginScene` 不清（与「相机与网格句柄表保留」现状语义一致，zplugin.h:390）；同帧多次调用最后一个生效。错误码沿用 `ZR_UNITY_*`（本组不新增错误码；无指针参数故不涉及 `ERR_NULL`）。
- `int ZR_Unity_GetCameraOrigin(double* outOrigin)`：取当前原点（3 元素，行星中心系）。out 为空 → `ZR_UNITY_ERR_NULL`。初始值 (0,0,0)。用途：C ABI round-trip doctest（TECH-VALIDATION §4.5「新增 C ABI round-trip」）与托管侧诊断。
- 线程：与现有 `ZR_Unity_*` 相同——单线程（Unity 渲染线程，zplugin.h:198 约束不变）。
- 未 Set 就 RenderFrame：原点按 (0,0,0)——合法初始态（行星中心=世界原点的场景），不报错。

### 3.4 每帧时序契约

`SetCameraOrigin` 必须在当帧 `RenderFrame` 之前调用；`SetCamera` 收到的 view 应为相机相对（纯旋转）。原生不校验矩阵语义（与现状一致——「proj 必须是标准 [0,1] 深度」等契约靠文档，zplugin.h:356-357），但 **GetCameraOrigin + GetPlanetStats 构成可断言的自证面**。

### 3.5 版本策略

`ZR_ApiVersion` 2→**3**：新增导出组即视为变更（改进计划 §8「改这里要递增 ZR_ApiVersion」口径；当前已是 2——B-FSR-02 已占用，zplugin.h:20-21、test_smoke.cpp:21）。托管侧启动时断言 ≥3 才启用行星通道——返回 2 的旧 DLL 被显式拒绝，行星通道缺导出的 P/Invoke 崩溃变成显式失败（版本门控的意义所在）。

---

## 4. ABI-2：chunk 句柄 + double 变换

### 4.1 句柄协议（与 mesh 句柄同构）

`uint64_t`：高 32 位代际、低 32 位槽位下标，**0 恒为无效**（zplugin.h:31 现行协议照搬）。槽位表容量 `ZR_PLANET_MAX_CHUNKS = 8192`（现状 6144 全量 + 余量；B-TECH-04.md:57）。

### 4.2 创建：谁生成、生成什么

顶点由**原生**在 `ChunkCreate` 内按规范路由 `PlanetChunkVertexAtGlobal(planetCenter, faceId, gU, gV, total, seed)` 生成（planet_math.h:561-566）——与 15 条 doctest 断言的是同一函数，保证「ABI 画出来的」与「测试断言的」同路径（B-TECH-04 验收 V4 同口径，B-TECH-04.md:212）。输入来自 desc + 行星上下文（SetWorld）：

- `faceId` 0..5（+Z/+X/−Z/−X/+Y/−Y，planet_math.h:499-511 面序）
- `cx/cy/cellsPerEdge`：面内 chunk 格（现状 32×32；参数化为未来细分留口——同 ABI 不重设计即可上更细格）
- `lodLevel`：0=远档(res 16) / 1=近档(res 64)——v1 两档锁死（改进计划「两档起步」口径），分辨率由档位映射，desc 不带 res 字段
- `seed`：来自 `ZrPlanetWorld`（§6），不逐 chunk 传
- `originAbs`：chunk 锚点（建议取 chunk 中心球面点，B-TECH-04 现状）double 绝对坐标；顶点局部坐标 = `float(posAbs − originAbs)`（double 域相减后一次 cast）

顶点布局复用场景路径 v1 的 32B/顶点（pos3f + normal3f + uv2f，strideFloats=8，zplugin.h:358）：pos=锚点局部 float；normal=球面法线+构建期中心差分坡度（B-TECH-04.md:66-67）；uv=面内 (u,v)（预留贴图，v1 高程色带不用）。

GPU 内存量级（计算，与实测吻合）：远档 320 顶点 × 32B = 10.24KB/chunk × 6144 = **62.9MB**——与 B-TECH-04 实测「6144 VB 池 ~63MB」一致（B-TECH-04.md:171）；近档 4352 × 32B = 139.3KB/chunk。

### 4.3 SetOrigin（double 变换）

`ZR_Planet_ChunkSetOrigin(handle, x, y, z)`：更新锚点绝对坐标。**顶点不重算**——顶点相对锚点不变，模型平移本就逐帧重算（§3.2）。用途：行星整体移动（轨道）时由托管按行星位移量批量平移可见 chunk。锚点变更而行星中心未同步平移 = 托管侧 bug（地形视觉错位），原生不校验、文档写明。NaN/Inf → `ZR_PLANET_ERR_NONFINITE`；句柄无效 → `ZR_PLANET_ERR_HANDLE`。

### 4.4 销毁与生命周期

`ZR_Planet_ChunkDestroy(handle)`：返回 int（D4——显式错误码，采用 `ZR_MeshFree` 幂等防护风格而非 `ZR_Unity_DestroyMesh` 的 void 静默风格：chunk 是流式动态资源，重复释放/代际不符必须可检测，zplugin.h:42-44 先例）。语义：

1. 句柄**立即失效**（代际 +1，独占锁内完成）——调用方马上可以复用逻辑 chunk id 重创。
2. GPU 资源**延迟到下一 RenderFrame** 由渲染线程释放（主线程不碰 GPU——D5）。
3. 状态机：`EMPTY → PENDING_UPLOAD（已发布待上传） → LIVE → PENDING_FREE → EMPTY`。LIVE 的 chunk 在 PENDING_FREE 期间仍可被当帧绘制（先画后删，帧内一致）。

同一 (faceId, cx, cy) 重复 Create 不去重（v1）——两份 GPU 资源由托管侧避免；`GetStats` 的 `createTotal − destroyTotal == liveChunks` 账目断言可审计泄漏（§7.3）。

### 4.5 校验规则汇总（ChunkCreate）

| 输入 | 越界 | 错误码 |
|---|---|---|
| desc 为空 / outHandle 为空 | — | `ZR_PLANET_ERR_NULL` |
| desc->structSize != sizeof | — | `ZR_PLANET_ERR_ABI` |
| faceId>5、lodLevel>1、cellsPerEdge==0 或 >4096、neighborMask 高 4 位非 0、reserved0 非 0 | — | `ZR_PLANET_ERR_RANGE` |
| originAbs 含 NaN/Inf | — | `ZR_PLANET_ERR_NONFINITE` |
| 行星上下文未设置（SetWorld 未调） | — | `ZR_PLANET_ERR_STATE` |
| 槽位满（liveChunks+pending ≥ 8192） | — | `ZR_PLANET_ERR_FULL` |

---

## 5. ABI-3：LOD 级别与邻居

### 5.1 现状与真根因

现状（全部实测，B-TECH-04）：同档相邻 chunk 共享边靠「同一全局整数索引 → 同一 double 表达式」做到**double 域逐位一致**（3021 次构建 seams=0）；跨档 T 型缝与面界靠 6.25km 裙边遮蔽（4.2 非整数格距比使强制一致采样在档界数学上不可行，B-TECH-04.md:86-90）。

**设计稿新增分析（标注：估算，待 B-PLANET-08/09 实测裁决）**：double 逐位一致 ≠ 渲染无缝。每 chunk 顶点以自身锚点 float 化（§4.2），共享边同一顶点在 A、B 两侧分别 `float(posAbs − anchorA)` / `float(posAbs − anchorB)`——两次独立舍入，幅值 ~221km（chunk 半对角 = 312.7×√2/2，计算）处 ULP=2^-6≈15.6mm → 同档边存在 ≤ ~31mm（2×ULP）的**静态**缝。它是静态失真（顶点不逐帧重算，无抖动），且被 6.25km 裙边覆盖（裕度 ~200×，计算）——现状截图无可见接缝与此相符。B-TECH-04 的运行期 seams=0 是 double 域口径，GPU VB float 回读缝宽 MISSING（§13）。

推论：**「传邻居信息就能消缝」是伪命题——真根因是共享边双侧 float 基准不统一**。消缝的真路径是共享边统一 float 基准（如按全局顶点索引的确定性 snap 基准），工程量不小而收益仅省 256 顶点/chunk（裙边占比 ~6%）。

### 5.2 取舍与决策（D6）

| 方案 | 内容 | 判定 |
|---|---|---|
| A. 维持全缘裙边 | 四缘一律带裙边（现状），neighborMask 进 ABI 仅校验+记录 | **v1 采用**：零新风险，缝被 200× 裕度遮蔽 |
| B. 共享边统一 float 基准 + 同档去裙边 | 全局顶点索引确定性 snap 基准，两侧逐位一致后才可去裙边 | B-PLANET-08 出选型报告后裁决；ABI 无需改动（这正是「第二章不用重新设计 ABI」的意义） |
| C. 档距比改整（61/16 或 65/17）+ 缝合索引 | 消除跨档 T 型连接的几何前提 | 同入 B-PLANET-08 评估；涉及档位定义变更，若采纳属 ABI **兼容**扩展（lodLevel 映射表更新） |

`neighborMask` 编码：bit0..3 = −u/+u/−v/+v 缘「邻居档位更粗或无数据/面界」；高 4 位保留必须 0（`ZR_PLANET_ERR_RANGE`）。v1 行为：校验+统计记录；不改变几何。

`SetLod` 语义：设置期望档位，**下一 RenderFrame 在渲染线程应用**（VB 内容重建/升容策略照搬 B-TECH-04：档变重建、升容重建、退档复用不缩容，B-TECH-04.md:73-75）；同帧多次 SetLod 最后生效。档界**弹跳**（几何 LOD pop）不受 neighborMask 影响——迟滞只抑制档位抖动，弹跳的 morph 缓解明确不做 v1（§12-8，B-TECH-04.md:190-192 已知受限延续）。

---

## 6. ABI-4：高度采样通道

### 6.1 形态：SetWorld 上下文 + 无状态采样（D7）

`PlanetTerrainHeightM(seed, dir)` 是纯函数（同种子同输入逐位一致，doctest 锁定 test_planet.cpp:331），但 C ABI 不能让调用方每次传 seed+行星中心（易错且重复）。引入进程级行星上下文：

- `int ZR_Planet_SetWorld(const ZrPlanetWorld* world)`：`seed` + `centerAbs[3]`（行星中心绝对坐标，double）。SRWLOCK 独占锁替换快照；NaN/Inf → `ZR_PLANET_ERR_NONFINITE`；structSize 不符 → `ZR_PLANET_ERR_ABI`。v1 契约：初始化期调用（单行星静态语义——改进计划 §2 需求口径「一颗 6371km 星球」；多行星上下文明确不做 §12-11）。
- `int ZR_Planet_SampleHeight(double x, double y, double z, float* outHeightM)`：`dir = normalize(pos − C)`，`h = PlanetTerrainHeightM(seed, dir)`，写 `*outHeightM`。**同步、纯 CPU、任意线程可调**（共享锁内拷贝 32B 快照后无锁计算）。错误码：空 out → `ERR_NULL`；非有限输入 → `ERR_NONFINITE`；|pos−C| 退化（长度 0）→ `ERR_RANGE`；上下文未设 → `ERR_STATE`。

**为什么同步够用（D7）**：每次调用 = 4 octave × 8 格点哈希 + 三线性插值 ≈ 数百 flop，估算亚微秒级；玩法频率预算下总开销可忽略（下表）。异步回填（工作线程+托管回调）引入的句柄/回调生命周期复杂度与收益不成比例——明确不做（§12-7）。

### 6.2 玩法侧调用频率预算（估算，探针实测校准）

| 场景 | 频率 | 每帧次数 |
|---|---|---|
| 角色贴地（含 3 点坡度中心差分） | 每物理步 4 次 | ~4 |
| 载具/装配体（≤16 刚体） | 每刚体每步 1 次 | ≤16 |
| 放置/吸附交互 | 事件驱动 | ≤10 |
| 预算线 | — | **≤1000 次/帧，估算 <0.1ms**（B-PLANET-09 实测后写死基线） |

### 6.3 输出精度与用法

- 输出 float：高度幅值 ±2.4km → float ULP(2.4km)=2^-12≈0.244mm（计算）——玩法够用。
- 语义：返回的是**噪声高程** h（与 chunk 顶点 heightM 同一函数同一值）。地表点 = `C + dir·(R + h)`（R=kPlanetRadiusM=6.371e6 锁定，planet_math.h:34）。坡度/法线：调用方 2~4 次邻域采样中心差分（配方写入托管封装）。
- **与渲染网格的差异（D9/MISSING）**：渲染表面是顶点三角形线性插值（近档顶点间距 312.7km/63 ≈ 4.96km，计算），与纯函数 h(dir) 存在插值偏差——幅值 MISSING（§13），B-PLANET-09 贴地扫描（角色浮空/入地深度）实测后决定是否需要三角形精确采样（barycentric）。

---

## 7. 内存与所有权

### 7.1 所有权表

| 数据 | 生成者 | 持有者 | 上传时机 | 回收时机 |
|---|---|---|---|---|
| chunk 顶点（CPU，临时） | 原生 ChunkCreate（规范路由） | 生成即转 GPU，CPU 副本不保留（档位重建靠纯函数重生成——无缓存需求） | — | 函数返回前 |
| chunk VB（GPU） | 渲染线程（RenderFrame 开头批量处理 pendingUploads） | 句柄表槽位 | 主线程 Create 后下一帧 | PENDING_FREE 后下一 RenderFrame |
| 行星上下文快照 | SetWorld | 进程级（SRWLOCK 保护） | — | DetachDevice 时随桥释放 |
| 句柄表槽位 | ChunkCreate | 句柄表 | — | ChunkDestroy 代际失效 + 渲染线程回收 |

### 7.2 与现状的一致性

GPU 内存形态与 B-TECH-04 实测同构：全量远档 62.9MB（VB 池预分配策略沿用：远档容量启动、进近档升容重建、退档不缩容——B-TECH-04.md:73-75）；CB 池 3200 上限（可见峰值 2662 零截断，B-TECH-04.md:77,122）。

### 7.3 句柄泄漏防护（四层）

1. **代际防误用**：释放后旧句柄命中代际不符 → `ZR_PLANET_ERR_HANDLE`（zplugin.h:31 协议）。
2. **账目断言**：`GetStats` 暴露 `createTotal/destroyTotal/liveChunks`——三者恒等式由托管测试断言。
3. ** bulk 释放**：`ZR_Planet_ClearAll(void)`——场景卸载/Play 退出一次清空（句柄全失效 + 渲染线程排队释放），防逐个释放遗漏。
4. **桥级兜底**：`ZR_Unity_DetachDevice` 语义扩展为随桥释放全部行星资源（文档写明；与「ReleaseSharedTarget 释放纹理与渲染链」的既有兜底同构，zplugin.h:253）。

---

## 8. 线程归属

| API | 调用线程 | 锁 | GPU 触点 |
|---|---|---|---|
| `ZR_Unity_SetCameraOrigin` / `_GetCameraOrigin` | 渲染线程（与现有 `ZR_Unity_*` 同约束，zplugin.h:198） | 无（单线程） | 无 |
| `ZR_Planet_SetWorld` | 主线程（初始化期） | SRWLOCK 独占 | 无 |
| `ZR_Planet_ChunkCreate` | 主线程 | SRWLOCK（校验+占槽独占；顶点生成在锁外，完成后独占发布） | 无（上传延迟） |
| `ZR_Planet_ChunkDestroy` | 主线程 | SRWLOCK 独占（代际失效） | 无（释放延迟） |
| `ZR_Planet_ChunkSetOrigin` / `SetLod` | 主线程 | SRWLOCK 独占 | 无（下帧应用） |
| `ZR_Planet_SampleHeight` | **任意线程** | SRWLOCK 共享（拷贝快照）后无锁 | 无 |
| `ZR_Planet_GetStats` / `ClearAll` | 任意 / 主线程 | SRWLOCK | 无 |
| `ZR_Unity_RenderFrame` | 渲染线程 | 共享锁取快照（待上传/待释放/档位变更/绘制集合），锁内不做 GPU 与重活 | 全部 |

**帧一致性协议**：RenderFrame 开头对句柄表取共享锁做一次快照（决策拷贝），随后锁外执行上传/释放/重建/绘制——主线程当帧后半段的 Create/Destroy/SetLod 落入下一帧（**延迟一帧语义**，文档写明）。SRWLOCK 先例：`ZR_Upscaler_*` 三函数「可被任意线程并发调用」（zplugin.h:59-66）。

---

## 9. 精度链路误差预算表

从「double 绝对坐标」到「GPU 光栅」逐环节（配合 abi-precision 探针口径，改进计划 §5：100m/1km/10km/100km 四点实测 ULP）：

| # | 环节 | 执行方 | 误差量级 | 性质 | 来源 |
|---|---|---|---|---|---|
| 1 | 相机绝对坐标 double 逐帧累计 | 托管 WorldPosition | double ULP@1e7m = 2^-29 ≈ 1.86e-9 m（计算） | 每帧 | double 尾数 52 位 |
| 2 | 浮点原点重定位（4000m，≥ 触发） | 托管 WorldOrigin | 保持 \|cam−origin\| ≤ 4000m → 下游 float 化幅值封顶 | 事件 | `Docs/Logs/TASK-A-WORLD-01.md:18,182` |
| 3 | `SetCameraOrigin(double×3)` 跨 ABI | 托管→原生 | 0（double 直传无损） | 每帧 | 设计（§3） |
| 4 | chunk 锚点 double（绝对语义） | 原生句柄表 | 0 | 建块时 | 设计（§4） |
| 5 | 顶点局部 float = float(posAbs−anchor) | 原生建块 | ≤ ULP(~221km)=15.6mm（chunk 角点；计算，FloatUlpAt 可实测校准） | **静态**（建块一次，无逐帧抖动） | 计算 |
| 6 | 共享边双侧各自 float 化 → 同档边静态缝 | 原生建块 | ≤ ~31mm（2×ULP，最坏） | 静态 | **估算**（§5.1 设计稿分析；float VB 回读缝宽 MISSING） |
| 7 | model 平移 float = float(anchor−camOrigin) | 原生每帧 | 近场 ≤ ULP(4km)=0.488mm（仅近场成立）；远景平移幅值大（如地平线 87km 处 ULP≈7.8mm）但相对误差恒 ≈2^-24 rad 封顶，角偏差亚毫像素（贴地 600m 视距 ≈0.0015px @1080p/60°FOV，计算） | 每帧（chunk 整体平移） | `TASK-A-WORLD-01.md:148` |
| 8 | view/proj float（托管相机相对构造） | 托管 | 近场相对误差 <1e-6 m（重定位路径实测） | 每帧 | `test_planet.cpp:36` |
| 9 | GPU 光栅 float32 | RHI | 与现状一致 | — | 非新增误差源 |
| 10 | 高度输出 float（±2.4km） | SampleHeight | ≤ ULP(2.4km)≈0.244mm（计算） | 每次采样 | 计算 |

**对照（naive 全 float 大坐标路径）**：100km 处 naive 相对误差 >1mm、理论上界 ULP=7.8125mm（test_planet.cpp:31-34）；重定位路径近场 <1e-6 m（同文件 :36）；A-WORLD-01 V6：float ULP 7.8125mm vs double+offset 守恒 4.366e-11 m（TASK-A-WORLD-01.md:113,186）。**这就是 ABI-1 能消 100km 处 7.8mm 抖动的完整证据链**：抖动源 = float 大坐标相减把相对位置精度吞到 ULP(绝对幅值)；消法 = 重定位保持幅值 ≤4km + double 域相减后一次 cast——两步分别由 WorldOrigin（托管）与 SetCameraOrigin+chunk 模型平移（原生）承载。

**阈值口径差异（如实登记）**：A-WORLD-01 生产 = 4000m、恰等于阈值**触发**（≥，TASK-A-WORLD-01.md:182）；planet_math.h 测试 = 10km、严格 >（恰在阈值不触发，test_planet.cpp:54-74）。两者不冲突——前者是托管生产口径（D1），后者是原生数学测试口径；原生通道内部**不做**重定位，只消费托管给的原点。

**abi-precision 探针理论对照值（计算；探针须实测，不得照抄）**：ULP(100m)=2^-17≈7.6e-6 m；ULP(1km)≈6.1e-5 m（test_planet.cpp:48 实测 <1e-4 一致）；ULP(10km)=2^-10≈0.98mm（A-WORLD-01 日志放宽口径 1.95mm——TASK-A-WORLD-01.md:149，两口径并记）；ULP(100km)=7.8125mm（实测锚定）。

---

## 10. 验收判据落点（改进计划 §3-P2 四条，缺一不可）

| 判据 | 测法 | 落点任务 | 现状缺口（如实） |
|---|---|---|---|
| **1080p ≥30fps** | planet-budget 探针：1920×1080 离屏（RGBA8+D32，QPC 墙钟含 Submit+WaitIdle——B-TECH-04 §4 同口径）300 帧均值 + 相位分解（贴地/爬升/远观）+ chunk 数 + 接缝计数 | B-PLANET-09 | 只有 640×480 的 7.9–8.8ms（B-TECH-04.md:31-36）；1080p 未验证（TECH-VALIDATION §8）。判据线：均值 <33.3ms（30fps 硬线，B-TECH-04 §4 同款）；16.6ms 作参考线。**无法从 640×480 外推**——远观段 draw-call 主导（~2600 chunk × 4 次 RHI 调用，GPU 轻载，B-TECH-04.md:165）提示像素 ×4.5 增量可能温和，但这是推断不是证据，探针实测裁决 |
| **地面/中空/轨道三档无可见接缝** | 同机位三关键帧截图（planet2_ground/midair/far 先例，B-TECH-04.md:137-144）+ 贴地近距跨 chunk 边缝宽扫描（探针新增——覆盖 §5.1 的 float 化静态缝 MISSING） | B-PLANET-09（探针）/ B-PLANET-11（判读） | 同档 double 域逐位一致 + 裙边遮蔽已证（seams=0）；float VB 口径缝宽未测 |
| **跨 chunk 行走 1km 无跳变** | 贴地直线 1km，位移前后像素差 < 阈值（改进计划口径）。**设计补充（标注）**：chunk 弧长 312.7km，1km 行走不跨 chunk 边界、不触发 4km 重定位——该判据实际考验逐帧量化一致性；补 5km/50km 变体覆盖 chunk 边界穿越与重定位触发帧（重定位前后渲染偏移逐位不变已有 doctest 先例 test_planet.cpp:93-96，PlayMode 侧复验） | B-PLANET-11 | 未验证 |
| **编辑器内 / IL2CPP / Android 各一次** | 编辑器 PlayMode 实跑三档+行走+帧耗；Windows IL2CPP 包验证 P/Invoke（uint64 句柄/structSize 封送）+ 帧耗一次；Android 按 D8 跑**托管等效**（WorldOrigin+相机相对+Mesh 路径的行星数学等效实现）帧耗+精度一次；跑不了的项写 MISSING+原因（探针规矩） | B-PLANET-11 / B-PLANET-12 | 三项全部未验证（改进计划 §3-P2 原文；TECH-VALIDATION §8） |

补充判据（设计稿新增，入 B-PLANET-09/11）：

- **SampleHeight 一致性**：`SampleHeight(C + dir·R)` 与 `PlanetChunkVertexAtGlobal(...).heightM` 同方向逐位一致（doctest）——采样通道与地形渲染同源的证据。
- **贴地浮空/入地扫描**：D9 MISSING 的量化（纯函数 vs 网格插值差）。
- **账目断言**：`createTotal − destroyTotal == liveChunks` 全程成立（§7.3）。

---

## 11. 任务拆解（B-PLANET-02..12）

量级口径：S≈1–2 人日 / M≈3–6 / L≈8–12（**估算**；合计 ~40±10 人日，落在改进计划 P2「4–8 周」上沿）。每任务遵守：一任务一 commit、施工与验收不同代理、日志四节齐全（AGENTS.md）。B-PLANET-09/10 实现改进计划 §5 的 planet-budget / abi-precision 两探针；若主控愿意与既有 B-PROBE-01/02 归并编号，可改登 B-PROBE-03/04（要点不变）。

| ID | 标题 | 要点（简报素材） | 量级 | 依赖 |
|---|---|---|---|---|
| B-PLANET-02 | 行星 ABI 头落地 | 按 §14 把四组签名抄入 zplugin.h；`ZR_ApiVersion`→3（D6；当前 2 已被 B-FSR-02 占用）；新增 struct 的 C++ static_assert（56/32/80B，zmesh.h:31 先例）；托管侧 Bindings 声明（DllImport 签名 + Marshal.SizeOf 断言；声明位置按 zplugin.h:6「ZFrame.Render.Bindings」口径核实）；zrender.def 导出随实现任务追加；同步扩充 zplugin.h:258 `ZR_UNITY_ERR_ARGS` 注释（§14）。白名单：zplugin.h、Bindings 声明文件 | M | 本设计稿验收 |
| B-PLANET-03 | 行星数学生产化 | planet_math.h §①（浮点原点）§③（球面几何）§④（cube-sphere/chunk/LOD/地平线）提升为生产模块（建议 `Native/zplanet/`，头文件仅依赖标准库特性保持）；**语义零改动**（15 条 doctest 改指生产头，逐位回归）；§② 重力留在 tests（原生通道不含物理，§12-1）。白名单：新生产目录、test_planet.cpp 的 include 行、CMakeLists | M | 无（可与 02 并行） |
| B-PLANET-04 | chunk 通道生产实现 | 句柄表（SRWLOCK+代际+8192 槽）、ChunkCreate 顶点生成/校验链（§4.5 全表）、GPU 上传/延迟释放、SetLod 档位应用（B-TECH-04 重建/升容策略）、SetOrigin、GetStats、ClearAll；RenderFrame 集成（场景路径后追加行星通道、同深度缓冲）；地平线曲率剔除移植（B-TECH-04.md:76-77）；相机相对模型矩阵（double 域相减一次 cast）；planet2.hlsl 入生产着色器（CBuf 对齐 static_assert 先例 B-TECH-04.md:46）。**单 chunk 生成耗时实测**（补 §1.2 MISSING）。本任务是 P2 最大风险项——生产 dll 首个真实渲染负载（zrender.dll 入库 ADR-004） | L | 02+03 |
| B-PLANET-05 | SetCameraOrigin + round-trip | 两函数实现（§3.3 协议：NaN 拒绝/持久/初始零）；doctest：Set/Get round-trip 逐位、NaN 拒绝、跨帧持久、BeginScene 不清（对照 zplugin.h:390 语义） | S | 02 |
| B-PLANET-06 | SetWorld + SampleHeight | 上下文快照 + 纯函数封装（§6.1）；错误码全表；doctest：同种子确定性、**SampleHeight(C+dir·R) 与 chunk 顶点 heightM 逐位一致**、退化方向拒绝、任意线程并发冒烟 | S | 03 |
| B-PLANET-07 | Unity 托管桥 | ZFrame.Render.Bindings 运行时：chunk 流式管理器（可见性驱动 Create/Destroy，SetLod 决策复用 `PlanetLodLevel` 迟滞口径的 C# 等效——或 P/Invoke 纯函数决策，选型写日志）；WorldOrigin(4000m) → SetCameraOrigin 每帧接线；viewRel 纯旋转构造并入 WorldOriginMath 唯一边界（A-WORLD-01 顺序铁律，TASK-A-WORLD-01.md:124,191）；账目断言测试。注意：WorldOrigin 现状未挂任何场景（HANDOFF.md:29），本任务是首个真接线 | M | 02+04+05 |
| B-PLANET-08 | 接缝专项（neighborMask 行为定版） | §5 三方案裁决：先实测共享边 float VB 缝宽（补 MISSING），再定「维持全缘裙边」vs「共享边统一 float 基准」vs「档距比 4.0 缝合索引」；neighborMask v1 行为按裁决定版（校验+记录 最低保障）；选型报告入任务日志 | M | 04 |
| B-PLANET-09 | planet-budget 探针 | 1080p 离屏实测：chunk 数/帧耗（300 帧均值+相位分解）/接缝计数/贴地缝宽扫描/SampleHeight 耗时与贴地浮空扫描（D9）；双机各存档（补双机口径 MISSING）；**MISSING 规矩**（改进计划 §5） | S | 04（SampleHeight 部分依赖 06） |
| B-PLANET-10 | abi-precision 探针 | 100m/1km/10km/100km 四点 ULP 实测（重定位正确路径 vs naive 对照，双口径存档——§9 表为理论对照值）；数字入档供 §11 验收引用 | S | 05（完整版加 04） |
| B-PLANET-11 | 编辑器内验收集成 | 改进计划 §3-P2 前三条判据落地：三档同机位截图判读、1km 行走像素差 + 5km/50km 变体、1080p 帧耗基线入档、账目断言 PlayMode；EditMode/PlayMode 用例只增不减 | M | 06+07+08+09 |
| B-PLANET-12 | IL2CPP + Android 各一次 | Windows IL2CPP 包：P/Invoke 封送验证 + 帧耗一次；Android：D8 托管等效/降档实测一次（帧耗+精度）；任何跑不了的项写 MISSING+原因，不填 0 | M | 11 |

**关键路径**：02+03 → 04 → (07,08,09) → 11 → 12；05/06/10 沿途插入。并行性：02 ∥ 03；05 ∥ 06（依赖链不同）。

---

## 12. 明确不做清单

前四条抄自改进计划 §4（理由原文保留），后七条为本设计稿 P2 本地化新增：

| # | 不做 | 理由 |
|---|---|---|
| 1 | 原生侧自研刚体/约束求解器 | 等于自研物理，成本 > 收益；PhysX 已知问题靠钳制质量比更便宜（改进计划 §4）。**P2 本地化**：球面重力在 Unity 侧逐刚体施加（TECH-VALIDATION §4.4 path A ③），原生行星通道零物理、零 PhysX 触点；planet_math §② 重力积分器不进生产通道 |
| 2 | 全原生世界（双精度+自研物理+流式地形+大气散射全套） | 本阶段成本极高，属于「自研引擎」（改进计划 §4）；P2 只做行星 chunk 专用通道（改进计划 §2 抉择 B） |
| 3 | 把 DataKernel 接进玩法 | 406M 行/s 基准好看，但玩法侧几百实体用不上；人群 LOD 备选非必需（改进计划 §4） |
| 4 | 追 DLSS5 真机 | 需 RTX 50 系，本机没有；代码骨架留着即可（改进计划 §4） |
| 5 | 材质/贴图/光照/阴影系统 | 完整管线化（改进计划 §2）；行星 v1 视觉沿用高程色带+距离雾+limb 口径（B-TECH-04.md:96-105）；uv 列预留不实装 |
| 6 | Vulkan Unity 桥 / Android 原生通道 | 现有 Unity 桥是 D3D12 专用（zplugin.h:187-198）；Vulkan 化 = 新 ABI 面 + 桥工程，超出 P2；Android 按 D8 走托管等效（§10） |
| 7 | 异步高度采样（工作线程+回填回调） | 纯函数估算亚微秒级够用（§6.1）；异步的句柄/回调生命周期复杂度与收益不成比例 |
| 8 | CDLOD 全档位几何 morph（消档界弹跳） | 迟滞只抑制档位抖动；弹跳的 morph 缓解是后续可选——B-TECH-04.md:190-192 已知受限如实延续，本设计只登记不消除 |
| 9 | 视相关自适应细分 / 更多档位 | 均匀 32×32 + 两档现状保持（B-TECH-04.md:194-195）；`cellsPerEdge` 参数已为未来留口（§4.2），扩档不改 ABI |
| 10 | 原生侧地形碰撞体 / MeshCollider | 碰撞走 SampleHeight（玩法侧，§6）；原生通道不生成/持有碰撞几何 |
| 11 | 多行星并行上下文 / 运行期移动行星中心 | v1 全局单例（改进计划 §2「一颗星球」口径）；行星移动走 SetOrigin 批量平移（§4.3），中心变更语义留待需要时扩展 |

---

## 13. 风险与未决

| # | 风险/未决 | 处置 |
|---|---|---|
| R1 | B-PLANET-04 是生产 dll 首个真实渲染负载——现有生产桥只清屏/共享目标/三 cube 场景；行星通道进入后 zrender.dll 二进制变化（入库产物，ADR-004） | 量级 L 的主因；验收须含 `--draw`/`--planet` 旧路径回归（B-TECH-04 回归先例 B-TECH-04.md:148-149） |
| R2 | 1080p 帧耗无外推依据（draw-call vs 像素主导比例未知） | 探针实测裁决（§10）；不预设结论 |
| R3 | 共享边 float 缝宽 MISSING（§5.1）——若贴地扫描证实可见，B-PLANET-08 才启动统一基准工程 | 先测量后动工；裙边 200× 裕度兜底 |
| R4 | 纯函数高度 vs 网格插值差 MISSING（D9）——若贴地浮空/入地可见，需三角形精确采样（barycentric），属 ABI 兼容扩展 | B-PLANET-09 扫描量化后裁决 |
| R5 | WorldOrigin 从未挂场景（HANDOFF.md:29），B-PLANET-07 是首个生产消费者；「物体权威值只在原点」的适配（TASK-A-WORLD-01.md:220）可能暴露新边界 | B-PLANET-07 简报须含「发现新 double→float 边界点必须并入 WorldOriginMath 同一处」的验收项 |
| R6 | 线程模型从「单线程渲染线程」扩展到「主线程写 + 渲染线程读」（D5）——zplugin 现有 Unity 组是单线程约束 | SRWLOCK + 快照协议（§8）；验收含并发冒烟（B-PLANET-06/07 用例） |
| R7 | 双机口径缺失（780M 开发机 vs 9060XT 游戏机） | B-PLANET-09 双机各存档（改进计划 §6「至少一组可对比数字基线」） |

---

## 14. 签名汇编——可直接抄入 zplugin.h 的完整声明块

> 本节为**唯一权威签名文本**（§3–§6 的解释如有出入以本节为准）。风格对齐：错误码负值、0=成功（ZR_UPSCALER_/ZR_FG_INPUT_/ZR_UNITY_ 先例）；structSize 首字段协议（ZR_UPSCALER_ERR_SIZE/ZR_FG_INPUT_ERR_ABI 先例）；句柄高 32 代际/低 32 槽位（mesh 先例）；函数指针 typedef 供 P/Invoke 复用（house style）；static_assert 布局断言（zmesh.h 先例）。插入位置：zplugin.h 的 `ZR_Unity_GetSceneStats` 组之后、`#ifdef __cplusplus }` 之前；同时 `ZR_ApiVersion` 实现与 zplugin.h:20-21 注释同步改为「当前 3——B-PLANET-02 追加行星通道 ABI」。**B-PLANET-02 落地时还需同步更新 zplugin.h:258 的 `ZR_UNITY_ERR_ARGS` 注释定义**——现为「尺寸非法（0 或超过 16384）」，本设计将其复用于 double 参数 NaN/Inf 拒绝，注释须同步扩充，否则签名块与既有定义注释语义不符。

```c
// ---- TASK-B-PLANET-02：P2 行星专用通道 C ABI（double 原点 + chunk 句柄 + LOD + 高度采样）----
//
// 设计依据：Docs/Planning/P2-行星ABI设计.md（TASK-B-PLANET-01，2026-11-14）。
// 通道形态：chunk 不走 DrawMesh——由 RenderFrame 在行星通道内自主绘制，
// 模型平移由原生在 double 域相减后一次 cast（float(objAbs - camAbs)，
// planet_math.h RenderOffsetF「重定位正确路径」）。重定位权威在 Unity 侧
// WorldOrigin（4000m，>= 触发）；原生不重定位，只消费每帧 SetCameraOrigin。
//
// 线程归属（SRWLOCK 先例：ZR_Upscaler_* 三函数）：
//   - SetCameraOrigin/GetCameraOrigin：渲染线程（与既有 ZR_Unity_* 同约束）；
//   - Planet 组写操作（SetWorld/ChunkCreate/ChunkDestroy/SetOrigin/SetLod/ClearAll）：
//     主线程可调，SRWLOCK 独占；
//   - SampleHeight/GetStats：任意线程（共享锁拷贝快照后无锁计算）；
//   - GPU 上传/释放/档位应用只在渲染线程 RenderFrame 边界（延迟一帧语义）。
//
// chunk 句柄：uint64_t 高 32 代际、低 32 槽位，0 恒无效（与 mesh 句柄同协议）。

#define ZR_PLANET_OK 0
#define ZR_PLANET_ERR_NULL (-1)      // 指针参数为空
#define ZR_PLANET_ERR_ABI (-2)       // structSize 与当前 ABI 不符
#define ZR_PLANET_ERR_RANGE (-3)     // faceId/lodLevel/neighborMask/cellsPerEdge 越界或方向退化
#define ZR_PLANET_ERR_HANDLE (-4)    // 句柄无效（含代际不符/已释放）
#define ZR_PLANET_ERR_STATE (-5)     // 行星上下文未设置（SetWorld 前置）
#define ZR_PLANET_ERR_FULL (-6)      // chunk 槽位满（> ZR_PLANET_MAX_CHUNKS）
#define ZR_PLANET_ERR_NONFINITE (-7) // double 参数含 NaN/Inf

#define ZR_PLANET_MAX_CHUNKS 8192u   // 槽位上限（现状全量 6144 + 余量）
#define ZR_PLANET_VERTEX_STRIDE_FLOATS 8u // 32B/顶点：pos3f+normal3f+uv2f（与场景路径 v1 同宽）

// LOD 档位：0 = 远档（16x16 顶点）、1 = 近档（64x64）——v1 两档锁定（B-TECH-04 口径）。
// neighborMask 四缘位：置 1 = 该缘邻居更粗档或无数据/面界（v1 仅校验+记录，不改几何；
// 取舍分析见设计稿 §5——消缝真根因是共享边 float 基准，不是邻居信息缺失）。
#define ZR_PLANET_EDGE_NEG_U (1u << 0)
#define ZR_PLANET_EDGE_POS_U (1u << 1)
#define ZR_PLANET_EDGE_NEG_V (1u << 2)
#define ZR_PLANET_EDGE_POS_V (1u << 3)
// 高 4 位保留，必须 0，否则 ZR_PLANET_ERR_RANGE。

/// 行星上下文（进程级单例，v1 单行星静态语义；SetWorld 独占锁替换快照）。
typedef struct ZrPlanetWorld {
    uint32_t structSize;      // 调用方填 sizeof(ZrPlanetWorld)
    uint32_t seed;            // 地形噪声种子（chunk 生成与 SampleHeight 共用）
    double centerAbs[3];      // 行星中心绝对坐标（行星中心系，米）
} ZrPlanetWorld;
static_assert(sizeof(ZrPlanetWorld) == 32, "ZrPlanetWorld must be 32 bytes (no padding)");

/// chunk 创建描述（全定宽 POD，托管侧按布局直接封送）。
/// 顶点由原生按规范路由 PlanetChunkVertexAtGlobal 生成（与 doctest 同函数）；
/// 顶点局部坐标 = float(posAbs - originAbs)（double 域相减后一次 cast）。
typedef struct ZrPlanetChunkDesc {
    uint32_t structSize;      // 调用方填 sizeof(ZrPlanetChunkDesc)
    uint32_t faceId;          // 0..5（+Z/+X/-Z/-X/+Y/-Y）
    uint32_t lodLevel;        // 0=远档 1=近档（分辨率由档位映射，不带 res 字段）
    uint32_t cx;              // 面内 chunk 格 u（0..cellsPerEdge-1）
    uint32_t cy;              // 面内 chunk 格 v
    uint32_t cellsPerEdge;    // 面内 chunk 数（1..4096；现状 32）
    uint32_t neighborMask;    // ZR_PLANET_EDGE_*（创建即可带；SetLod 可更新）
    uint32_t reserved0;       // 必须 0
    double originAbs[3];      // chunk 锚点绝对坐标（建议 chunk 中心球面点；顶点局部基准）
} ZrPlanetChunkDesc;
static_assert(sizeof(ZrPlanetChunkDesc) == 56, "ZrPlanetChunkDesc must be 56 bytes (no padding)");

/// 行星通道统计（全定宽 POD；账目断言 createTotal - destroyTotal == liveChunks）。
typedef struct ZrPlanetStats {
    uint32_t structSize;       // 由实现填 sizeof(ZrPlanetStats)
    uint32_t liveChunks;       // 当前存活 chunk 数
    uint32_t pendingUploads;   // 待渲染线程上传
    uint32_t pendingFrees;     // 待渲染线程释放
    uint32_t pendingLod;       // 待应用档位变更
    uint32_t drawnLastFrame;   // 最近一帧实际绘制数（地平线剔除后）
    uint32_t culledLastFrame;  // 最近一帧剔除数
    uint32_t seamCheckFails;   // 运行期共享边 double 位形不一致计数（诊断；B-TECH-04 seams 口径）
    uint64_t createTotal;      // 累计 ChunkCreate 成功数
    uint64_t destroyTotal;     // 累计 ChunkDestroy 成功数
    uint64_t lodChangeTotal;   // 累计档位变更应用数
    uint64_t vertexBytes;      // 当前 GPU VB 字节（顶点数 x 32B）
    double lastFrameMs;        // 最近一级行星通道段耗时
    double avgFrameMs;         // 累计平均耗时
} ZrPlanetStats;
static_assert(sizeof(ZrPlanetStats) == 80, "ZrPlanetStats must be 80 bytes (no padding)");

// ---- 组 1：double 相机原点（沿用 ZR_UNITY_* 错误码；渲染线程） ----
//
// 注意：ZR_UNITY_ERR_ARGS 现定义注释（zplugin.h:258）为「尺寸非法（0 或超过 16384）」；
// B-PLANET-02 落地时同步扩充该注释——本组将其复用于 double 参数 NaN/Inf 拒绝。

/// 设置相机绝对原点（行星中心系，米）。NaN/Inf 返回 ZR_UNITY_ERR_ARGS。
/// 跨帧持久（BeginScene 不清）；同帧多次调用最后一个生效；未设置时按 (0,0,0)。
int ZR_Unity_SetCameraOrigin(double x, double y, double z);

/// 取当前相机绝对原点（3 元素）。outOrigin 为空返回 ZR_UNITY_ERR_NULL。
/// 用途：C ABI round-trip 自证与托管诊断（TECH-VALIDATION §4.5 口径）。
int ZR_Unity_GetCameraOrigin(double* outOrigin);

// ---- 组 2：行星上下文 ----

/// 设置行星上下文（seed + 行星中心）。v1 契约：初始化期调用，单行星。
/// world 为空返回 ZR_PLANET_ERR_NULL；structSize 不符返回 ZR_PLANET_ERR_ABI；
/// centerAbs 含 NaN/Inf 返回 ZR_PLANET_ERR_NONFINITE。
int ZR_Planet_SetWorld(const ZrPlanetWorld* world);

// ---- 组 3：chunk 句柄生命周期 ----

/// 创建 chunk：原生生成顶点并排队 GPU 上传（下一 RenderFrame 生效——延迟一帧）。
/// 成功写 *outHandle 并返回 ZR_PLANET_OK；校验失败返回 ZR_PLANET_ERR_*（不写 outHandle）。
/// 同一 (faceId,cx,cy) 重复创建不去重（v1）——托管侧负责；账目以 GetStats 审计。
int ZR_Planet_ChunkCreate(const ZrPlanetChunkDesc* desc, uint64_t* outHandle);

/// 销毁 chunk：句柄立即失效（代际+1）；GPU 资源下一 RenderFrame 由渲染线程释放。
/// 幂等防护：重复释放/无效句柄返回 ZR_PLANET_ERR_HANDLE（显式错误码风格——
/// chunk 是流式资源，释放必须可审计；区别于 ZR_Unity_DestroyMesh 的 void 静默风格）。
int ZR_Planet_ChunkDestroy(uint64_t handle);

/// 更新 chunk 锚点绝对坐标（行星整体移动时由托管批量调用）。顶点不重算
/// （局部坐标相对锚点不变；锚点平移而行星中心未同步平移 = 托管侧 bug，不校验）。
/// NaN/Inf 返回 ZR_PLANET_ERR_NONFINITE；句柄无效返回 ZR_PLANET_ERR_HANDLE。
int ZR_Planet_ChunkSetOrigin(uint64_t handle, double x, double y, double z);

/// 设置期望 LOD 档位与邻居掩码。下一 RenderFrame 应用（同帧多次最后生效）。
/// 档位应用策略照搬 B-TECH-04：档变重建 VB、升容重建、退档复用不缩容。
/// lodLevel>1 或 neighborMask 高 4 位非 0 返回 ZR_PLANET_ERR_RANGE。
int ZR_Planet_SetLod(uint64_t handle, uint32_t lodLevel, uint32_t neighborMask);

/// 取统计快照。out 为空返回 ZR_PLANET_ERR_NULL；structSize 由实现填。
int ZR_Planet_GetStats(ZrPlanetStats* out);

/// 一次性清空全部 chunk（场景卸载/Play 退出兜底；句柄全失效，GPU 下一帧释放）。
void ZR_Planet_ClearAll(void);

// ---- 组 4：高度采样（同步纯函数；任意线程） ----

/// 采样地形噪声高程（与 chunk 顶点同一 PlanetTerrainHeightM：同 (seed,dir) 逐位一致）。
/// dir = normalize(pos - 行星中心)；地表点 = 中心 + dir*(R + h)，R = kPlanetRadiusM 锁定。
/// 返回 ZR_PLANET_OK；outHeightM 为空 ERR_NULL；非有限输入 ERR_NONFINITE；
/// pos 与中心重合（方向退化）ERR_RANGE；上下文未设 ERR_STATE。
/// 频率预算：<=1000 次/帧（估算 <0.1ms，探针实测校准——设计稿 §6.2）。
int ZR_Planet_SampleHeight(double x, double y, double z, float* outHeightM);

// 托管侧 P/Invoke 可直接复用的函数指针类型
typedef int (*ZrUnitySetCameraOriginFn)(double x, double y, double z);
typedef int (*ZrUnityGetCameraOriginFn)(double* outOrigin);
typedef int (*ZrPlanetSetWorldFn)(const ZrPlanetWorld* world);
typedef int (*ZrPlanetChunkCreateFn)(const ZrPlanetChunkDesc* desc, uint64_t* outHandle);
typedef int (*ZrPlanetChunkDestroyFn)(uint64_t handle);
typedef int (*ZrPlanetChunkSetOriginFn)(uint64_t handle, double x, double y, double z);
typedef int (*ZrPlanetSetLodFn)(uint64_t handle, uint32_t lodLevel, uint32_t neighborMask);
typedef int (*ZrPlanetGetStatsFn)(ZrPlanetStats* out);
typedef void (*ZrPlanetClearAllFn)(void);
typedef int (*ZrPlanetSampleHeightFn)(double x, double y, double z, float* outHeightM);
```

---

## 附：本设计稿的边界声明

- 本文件是设计稿：**不改任何 .h/.cpp/.cs**；§14 文本未经编译验证（抄入 zplugin.h 属 B-PLANET-02，落地时以 /W4 /WX 编译为准）。
- 所有「估算/计算/MISSING」标注是刻意保留的证据等级，不是待润色措辞——后续任务不得把 MISSING 改写成已验证（改进计划 §0 铁律 1）。
- 交叉引用已加入 `Docs/TECH-VALIDATION.md` §4 末尾（本任务唯一允许的另一处改动）。
