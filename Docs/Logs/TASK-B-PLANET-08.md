# TASK-B-PLANET-08 —— 接缝专项：float VB 缝宽实测 + neighborMask 行为定版

- **轨道**：B / PLANET（设计稿 §11-08；依赖 04 ✓ 8b03db6）
- **状态**：doing
- **派发**：主控，2026-11-14

## 任务简报（主控写）

### 0. 使命

改进计划/设计稿反复标注的最后一个 MISSING：**共享边顶点 float 化后的实际 GPU VB 缝宽**（B-TECH-04 的 seams=0 是 double 域口径，不是 VB 回读）。先测出数字，再按设计稿 §5 三方案定版 neighborMask v1 行为。这是"先测量后裁决"任务——**测量结果决定裁决，不许倒置**。

### 1. 做什么

1. **实测 VB 缝宽**（补 MISSING，设计稿 §1.1:73 挂账）：
   - 共享边同一顶点在相邻 chunk A/B 两侧的 float VB 值差——用生产路径生成两 chunk 顶点（ChunkVertexIndex/PlanetChunkVertexAtGlobal 规范路由），找到共享边顶点对，逐对比较 float 位置差（米），报告最大/均值/分布
   - GPU 实证（如可行）：--planet3 模式下共享边区域的像素级接缝检查（截图放大/差值），与 §5.1 的 ≤31mm 估算对照
2. **三方案裁决**（设计稿 §5.2 表，按实测数字定版）：
   - 维持全缘裙边（现状，裕度 ~200×估算）
   - 共享边统一 float 基准（消根因）
   - 档距比 4.0 缝合索引
   - **裁决写进任务日志**：依据=实测缝宽 vs 裙边覆盖裕度 vs 实现成本；若实测缝宽 << 视觉阈值且裙边覆盖成立，维持现状是合法裁决（改进计划 §6 口径：三档无可见接缝是判据，不是消灭全部浮点差）
3. **neighborMask v1 行为定版**：按裁决落实现（现状=校验+记录 最低保障；若裁决改行为，最小 diff 实现+doctest）
4. **测试**：实测程序/断言入 test_planet_channel.cpp 或独立探针（按性质归位）；全量 ≥375 只增不减；/WX 零警告
5. **不碰**：zplugin.h 签名（冻结）、zplanet/**（只读）、UnityProject/**、TASKS/HANDOFF/WORKLOG
6. 环境坑：构建走 Tools/build_native.ps1

### 2. 铁律

- 约法三章：真跑/真输入/缺就报缺/可复核——实测数字带方法与口径，估算标注估算
- 施工不 commit、不改看板；UTF-8 无 BOM/LF；/WX 零警告
- **裁决必须由实测数据支撑**——先写测量节，再写裁决节，顺序不许反

### 3. 完成判据

- VB 缝宽数字（最大/均值/分布）+ GPU 口径对照（如可行）入报告——MISSING 清零
- 三方案裁决有据+neighborMask 行为定版（实现或维持，均有 doctest 锁定）
- 全量绿；dll 双侧一致（如有原生变更）

---

## 二、施工报告（TASK-B-PLANET-08 施工代理，2026-11-14）

### 2.0 执行摘要

按简报顺序完成：先测量（三组探针全数落数），后裁决（三方案定版＝方案 A 维持，待主控确认），再落地（neighborMask v1 行为 doctest 锁定 + 主控中途追加的 G4-R1 门落地与复测）。设计稿 §1.2 MISSING「共享边顶点 float 化后的实际缝宽（GPU VB 口径）」**清零**：CPU float 域实测 + GPU 像端口径双层证据。全量 380 用例（375→380，+5）/823,621 断言全绿，/WX 零警告，zrender.dll 双侧 sha256 一致，zrender.def 56 导出零改动。

### 2.1 测量一：共享边 float VB 缝宽实测（本任务核心——先测量）

**方法与口径（引用数字必须连带）**：

1. **顶点来源 = 生产函数本体，零复刻漂移**：测试直连 `planet_detail::GenerateChunkVerts`（zrhi_d3d12_unity_target.cpp:780，与 zrender.dll 同一编译单元；planet_detail 位于匿名命名空间内部链接，经新增外部链接薄桥 `TestGenerateChunkVertsForUnitTests`（unity_target.cpp:2000 附近）转发到生产函数本体）。测试取回的字节就是 memcpy 进 GPU VB 的字节。布局镜像 `SeamVBVertex`（32B）以**逐顶点逐位比对自证**（240/240 全绿）——同时构成「生产 emit = float(posAbs − originAbs)（double 域相减一次 cast）」的复认。
2. **样本面**：6 条共享缘 × 缘上全部顶点＝240 顶点对：同面远档 E-W/S-N（16+16）、同面近档 E-W/S-N（64+64）、**跨面** far（face0 东缘 ↔ face1 西缘，16）、**跨面** near（64）。cellsPerEdge=32、seed=1337、行星中心 (0,−R,0)——与 --planet3 生产编排同构图。
3. **三层口径**：
   - (2) 每侧静态 float 化偏差 `|ε| = |float(posAbs−anchor) − (posAbs−anchor)|`（设计稿 §9 表第 5 行的实测）；
   - (3) **VB 回读口径**（无相机、静态，MISSING 本体）：`abs = anchor + double(local)`（double 域度量，避免二次 float 舍入），`seam = |absA − absB|`＝两次独立 float 舍入的绝对域发散；
   - (4) **渲染口径**（端到端）：`t = float(anchor−cam)`（生产逐绘 CB 口径，unity_target.cpp:2530-2537）＋ `viewPos = float(local + t)`（planet2.hlsl VS `mul(model,·)` 平移加法），`seam = |viewA − viewB|`。相机＝**本缘中点上方 2m（可见性口径）**——缝宽必须在「这条缘可见」的相机位测量；远处相机（600m/50km）的缝宽被 |anchor−cam| 的平移量化主导、角分辨率恒 ~1e-7 rad 级，不构成可见缝（跨面缘在 600m 相机下位于 ~4400km 外、远超 87km 地平线，属不可见场景，不进聚合）。
4. **前置不变量复认**：每顶点对先断言双侧 double 域 `posAbs` 逐位一致（memcmp）——B-TECH-04 构造性缝合前提在本生产生成路径上的全缘全顶点复认，240/240 全绿。

**实测数字（seed=1337, cells=32, Release, AMD 780M 开发机）**：

| 口径 | n | max | mean | 分布 [=0,(0,.5],(.5,1],(1,2],(2,4],(4,8],(8,16],>16]mm |
|---|---|---|---|---|
| 每侧静态 float 化偏差 \|ε\| | 240 | **7.797 mm** | — | 理论上界 ULP/2 = 7.8125 mm（幅值 221km 处）——实测贴着理论上界，构造性证实 |
| **VB 回读口径**（MISSING 本体） | 240 | **15.930 mm** | **4.481 mm** | [0, 4, 39, 26, 51, 84, 36, **0**]（无一超 16mm） |
| **渲染口径**（缘上 2m，可见性） | 240 | **22.119 mm** | **7.027 mm** | [55, 16, 27, 12, 11, 31, 64, 24]（55/240 顶点双侧在 float 域**恰好重合**，缝=0） |

分缘明细（VB 回读 max / 渲染口径 max，mm）：

| 缘 | tier | res | VB 回读 max | 渲染口径(缘上2m) max |
|---|---|---|---|---|
| 同面 E-W | 0 | 16 | 14.899 | 22.103 |
| 同面 S-N | 0 | 16 | 13.458 | 15.625 |
| 同面 E-W | 1 | 64 | 15.253 | 15.656 |
| 同面 S-N | 1 | 64 | **15.930** | **22.119** |
| 跨面 (0\|1) E-W | 0 | 16 | 8.262 | 15.625 |
| 跨面 (0\|1) E-W | 1 | 64 | 12.861 | 19.137 |

**与设计稿估算对照**（§5.1：≤~31mm＝2×ULP@221km，**估算**标注）：实测 VB 回读 max 15.93mm ≈ **1×ULP**——估算的 2×ULP 把两侧舍入都按满 ULP 计，而 round-to-nearest 每侧 ≤ULP/2、差 ≤1×ULP，估算**方向正确、量级正确、系数保守 2×**（§5.1 的「估算」标注按约定保留，实测数字以本节为准）。渲染口径 max 22.12mm 落在估算 31mm 之内（含平移舍入与加法舍入后仍在其内）。同档/跨档/同面/跨面四类共享边同一量级——**缝宽不随档位/面界系统性放大**。

**发现（测量附带，如实登记）**：生产 `SkirtDropM = 2% × 格距`（unity_target.cpp:769-771，far=416.98m / near=99.28m），而 B-TECH-04 planet2 与设计稿 §5.1 口径是 **2% × chunk 弧长 = 6.25km**——两口径差 15×/63×。对**本任务 float 缝**裕度依然巨大：near 裙边对渲染口径 max 的裕度 **×4488**、far **×18852**（VB 口径 ×6232 / ×26175）。但对 2.2 节的跨档 T 型几何失配（max 76.1m），near 侧裙边裕度仅 **×1.3**（far 侧 ×5.5）——见 2.9 遗留移交。

### 2.2 测量二（上下文）：跨档 T 型几何失配（double 域）——裙边真正遮蔽的对象

生产档界近(16,16)东缘 ↔ 远(17,16)西缘（同一物理线 u=0.0625；63/15=4.2 非整数格距比，B-TECH-04 强制一致采样被否决的同一构造）：远缘第 k 顶点对「近缘折线（相邻近缘顶点弦线）」的三维偏差（float 之前、与相机无关的静态几何量）：

- **max = 76.08 m，mean = 29.14 m**（16 个远缘顶点，seed=1337）。

对照尺：float VB 缝（2.1 节）mm 级 << T 型几何失配 m 级，相差 **5 个数量级**——裙边的存在理由是后者；float 缝被裙边遮蔽是「搭车」受益。

### 2.3 测量三（GPU 口径）：--planet3 贴地共享边像素检查

**方法**：生产 dll 全路径渲染（LoadLibrary zrender.dll，30 chunk，导出面验证同 B-PLANET-04）。新增贴地检查段（zrhi_d3d12_window.cpp RunPlanet3Mode 5.5 节，只报告不改判定）：相机 2m 悬停在近档 (16,16)/(17,16) 共享东西缘的 9 个采样顶点（k∈{4,12,20,28,32,36,44,52,60}）正上方、天顶向下（near=0.5m，像素足迹 ≈4.81mm/px）。每帧全量回读 640×480，扫描清屏色 (13,26,38)±6 泄漏（≥1px 的缝若存在必然漏出清屏色/裙间空腔——Cull None 下裙边侧立不占投影宽、缝底无几何、远面被 far=800km 裁剪）。

**实测**：9/9 帧渲染+回读成功；**每帧 307,200 像素全部非清屏色（clear_total=0，含投影缝带 ±3px 窗口）**；地形帧近场逐行 3px 组平滑亮度最大跳变仅 0.7/255（无 ≥1px 竖直缝）；证据图 `Docs/Parity/planet3_seam_closeup.png`（k=32 帧）。CPU 复刻同帧视空间缝宽 0.000–2.184mm，全部 << 4.81mm/px 像素足迹。

**结论**：GPU 光栅端到端口径下，贴地最坏可视视角（天顶向 2m）共享边**水密**——与 2.1 节 CPU float 域实测一致，MISSING 的 GPU 侧证据到位。GPU 像端口径对缝宽的下界是 ~4.8mm/px（亚像素缝不可分辨），精确数字以 2.1 节 CPU 口径为准。

**两点如实登记**：(a) 探针的 clip 链 CPU 复刻列（expect_px）存在灾难性抵消放大（绝对投影点偏离真值达 3.75px），不能作为 GPU clip 舍入的预测——GPU 真实 clip 舍入无读回手段（无 GPU 侧探针导出，见 2.9），本口径以「像素级无泄漏」+「视空间精确测量」两条腿支撑；(b) 截图链路存在 **pre-existing 竖条纹伪影**（1/3 列通道轮转，period-3）：B-TECH-04 存档 planet2_ground/midair.png 同样带此伪影（84.2%/95.1% period-3 连续率），非本任务引入，不影响本测量（清屏色检测对轮转不变、缝带窗口 ≥3px 必含未轮转列），建议独立任务修复（影响所有 Parity 截图判读）。

### 2.4 三方案裁决（测量之后——数据支撑）

| 方案 | 内容 | 实测依据 | 覆盖裕度 | 实现成本 | 判定 |
|---|---|---|---|---|---|
| **A. 维持全缘裙边**（现状） | 四缘一律裙边；neighborMask v1 校验+记录 | VB 缝 max **15.93mm**、渲染口径 max **22.12mm**；生产裙边深 near 99.3m / far 417m；double 域逐位不变量 240/240 | **×4488**（near，渲染口径）～×26175（far，VB 口径）——改进计划 §6 判据「三档无可见接缝」：GPU 像端口径实测零泄漏 | **零**（现状即此） | **✅ 定版（本任务裁决）** |
| B. 共享边统一 float 基准 + 同档去裙边 | 全局顶点索引确定性 snap 基准，两侧逐位一致 | 消除的根因缝 ≤22mm（渲染口径）——**在 4.81mm/px 贴地天顶向最坏视角下已不可见**（GPU 实测零泄漏）；收益=省 256 顶点/chunk（裙边占比 ~6%）且**去裙边会同时失去对 T 型几何失配（max 76m）的遮蔽**——收益与风险倒挂 | 消缝后同档无需裙边，但跨档/面界仍需 | 顶点生成路由重设计 + 锚点约定变更 + 全量回归；量级 M~L | ❌ 否决（v1）：消 mm 级已不可见缝，换来失去 m 级失配的遮蔽与全部重建成本 |
| C. 档距比改整（61/16 或 65/17）+ 缝合索引 | 消除跨档 T 型连接几何前提 | T 型几何失配实测 max 76m/mean 29m——是裙边的主用途；但改档距比牵动 LOD 阈值/几何密度/格距全局口径，且**不消 float 缝**（float 缝与档距比无关） | 与 A 同（裙边仍保留） | 档位定义变更 + 兼容扩展 + 全量回归；量级 L | ❌ 否决（v1）：解决的不是本任务实测出的问题；76m 失配现有裙边已遮蔽（far 侧 ×5.5，near 侧 ×1.3 见 2.9） |

**裁决结论：方案 A 维持**——依据链：实测缝宽（VB 15.93mm/渲染 22.12mm）<< 视觉阈值（贴地 4.81mm/px 足迹下 GPU 零泄漏）+ 裙边覆盖裕度 ×4488 起 + 零实现成本。改进计划 §6 口径「三档无可见接缝是判据，不是消灭全部浮点差」。

> **待主控确认**：本裁决由实测数据单方面支撑、三方案证据齐全，按简报「最终定版权在主控」约定，**定版待主控确认**后抄送设计稿 §5.2/D6 与改进计划 §6。

### 2.5 neighborMask v1 行为定版（按裁决落实现）

**定版 = 维持现状语义「校验 + 记录、不进几何路径」**（设计稿 §5.2 方案 A/D6 原文），**零生产行为 diff**，以 doctest 锁定三个可观测面：

1. **校验面**：合法域 0x0..0xF 全遍历 ChunkCreate/SetLod 接受；非法高 4 位 0x10..0xF0 步进 0x10 全遍历 + 混合位 0x11/0xFF → 一律 `ZR_PLANET_ERR_RANGE`（既有用例 + 新增全遍历）。
2. **记录面**：新增测试钩子 `TestNeighborMaskForUnitTests(handle)`（unity_target.cpp，与 TestResetForUnitTests 同策——不出 def、生产行为零改动；GetStats 无掩码字段、ZrPlanetStats 布局冻结不可扩，故走测试钩子）。doctest：ChunkCreate 写入的掩码逐值读回（16 值全遍历）、SetLod 更新读回（16 值全遍历）、SetLod 非法掩码拒绝后旧记录保持。
3. **几何零依赖面**：构造性证据 = 生产顶点生成函数 `GenerateChunkVerts` 签名**不含 neighborMask 参数**（白盒声明处注明）；行为证据 = 同格不同掩码两次创建均成功（v1 不去重契约不受掩码影响）+ 账目恒等式全程保持。

落点：`Native/tests/test_planet_channel.cpp` 新增用例「planet channel: B-PLANET-08 neighborMask v1 行为定版锁定（校验+记录+几何零依赖）」；unity_target.cpp 新增 `TestNeighborMaskForUnitTests`（12 行，含注释）。

### 2.6 G4-R1 主控裁决落地（主控中途追加，单列：测量→裁决→落地）

**输入（B-PLANET-07 实测，主控裁决采纳 3 行门方案）**：近档 seam 核查每块固定税 ~150µs（相邻 166.4ms vs 散布 187.0ms，256 远档块 3 轮取最小），6144 全量外推 ≈0.92s 渲染线程 CPU；托管预算只能钳制不能消除。裁决：`if (tier == 0u) return;` 门——远档跳过共享边 memcmp 核查；哈希索引案不采纳。

**落地（unity_target.cpp，diff +85/−4 中的门部分）**：
1. `SeamCheckNear` 入口 3 行门（tier==0 直接返回）+ 函数头注释重写：如实登记沿革（v1 仅近档 → G4 改判全档位 → G4-R1 实测税后主控裁决回收到近档）与证据链（构造性路由 + 本任务 2.1 节 240 顶点对全缘全顶点复认 + 裙边遮蔽）。
2. 正确性依据：远档共享边逐位一致由构造性路由保证（同全局整数索引 → 同一 double 表达式）；本任务测量探针 (0) 对远档缘（含跨面）240 顶点对中的 64 个远档顶点对逐位复认全绿。
3. 调用方（上传/档位路径）零改动——门在函数内裁决，语义半径最小。

**doctest 锁定**（「远档不核查/近档仍核查」行为面）：新增观测面 `g_seamChecks` 计数（邻居命中缘数；`seamCheckFails` 只反映失配、无法区分「查了全绿」与「没查」）+ 测试钩子 `TestSeamChecksForUnitTests` / `TestPromoteUploadsForUnitTests`（把 PENDING_UPLOAD 推进到 LIVE，模拟上传边界状态——SeamCheckNear 只读槽位元数据不触 VB，doctest 进程才能直驱该 RenderFrame 内路径）。用例「B-PLANET-08/G4-R1 seam 核查门行为锁定」：近档相邻对直驱核查 → 计数 +1 且零失配；远档相邻对 → 计数不动；近档孤块（无邻居）→ 计数不动。

**复测（同口径：256 远档块相邻 vs 散布、3 轮取最小，直驱 SeamCheckNear＝税的本体）**：

| 组 | 门后 µs/块 | 对照 |
|---|---|---|
| 远档相邻 | **0.0031** | 门后 ≈0 |
| 远档散布 | **0.0025** | 门后 ≈0——**相邻≈散布，固定税消除** ✓ |
| 近档相邻（门不覆盖） | 169.7 | 被消除量的量级对照（与 B-PLANET-07 ~150µs 同量级互证） |
| 近档散布（门不覆盖） | 62.7 | 表扫分量对照（相邻−散布≈逐顶点 double 重算分量 ~107µs） |

外推：远档 6144 全量门后核查开销 ≈**0.019ms**（门前 B-PLANET-07 实测 ≈920ms 级）——消除率 ~99.998%。全程 seamCheckFails==0。

### 2.7 测试与构建数字

- **全量 doctest**：380 用例（375→380，+5：缝宽实测/跨档失配/neighborMask 锁定/G4-R1 锁定/G4-R1 复测）/ **823,621 断言** 全绿，0 失败（只增不减 ✓）。
- **构建**：Tools/build_native.ps1 全链 /W4 /WX 零警告（MSVC 2022 x64 Release）。
- **dll 双侧一致**：`Native/build/bin/Release/zrender.dll` 与 `UnityProject/Assets/Plugins/ZRender/zrender.dll` sha256 相同（ea97a375…d12c2）。导出面零变化（zrender.def 56 导出零 diff）；新增符号均为非导出测试钩子/匿名命名空间内部链接。
- **Unity 侧**：ABI 与导出面未变 → 托管无需新接线；G4-R1 门改变远档运行时行为（seam 核查税消失）——**B-PLANET-07 并行期间其测得的 ~150µs/块税在合入后消失**，请主控转达；本任务按并行约束未跑 Unity 批处理。

### 2.8 改动文件清单（git diff --numstat，施工不 commit）

```
675    0  Native/tests/test_planet_channel.cpp
184    0  Native/tests/zrhi_d3d12_window.cpp
 85    4  Native/zrhi/d3d12/zrhi_d3d12_unity_target.cpp
  (二进制) Docs/Parity/planet3_terrain.png      # --planet3 重跑覆写：27/921600 字节差异（0.003%，光栅噪声级）
  (新增)   Docs/Parity/planet3_seam_closeup.png # GPU 口径证据帧（k=32）
```

白名单符合性：Native/tests/**（test_planet_channel.cpp、zrhi_d3d12_window.cpp 均在其中）✓；unity_target.cpp（G4-R1 门 + 测试钩子，门本身为主控裁决的行为变更）✓；zrender.def 零改动 ✓；dll 双侧部署 ✓；本日志 ✓。**白名单外新增 1 文件**：`Docs/Parity/planet3_seam_closeup.png`（GPU 口径证据 PNG——--planet3 运行的自然产物，与 planet3_terrain.png 同性质；是否入库待主控确认，不入库则删除即可，数字已录本日志）。zplugin.h 零改动（签名冻结 ✓）、zplanet/** 零改动（只读 ✓）、UnityProject 源码零改动 ✓、TASKS/HANDOFF/WORKLOG 零改动 ✓。

### 2.9 遗留风险与移交

1. **裙边深度口径差（新发现，建议单开任务裁决）**：生产 `SkirtDropM = 2%×格距`（far 417m/near 99.3m）vs B-TECH-04 planet2 与设计稿口径 `2%×chunk 弧长`（6.25km）。对本任务 float 缝裕度 ×4488 起仍然巨大、不影响 2.4 节裁决；但对跨档 T 型几何失配（实测 max 76.1m）**near 侧裙边裕度仅 ×1.3**（且裙边只在非掠射视角下遮蔽）——远档侧 ×5.5。不阻塞验收，但建议主控派单裁决生产裙边深度口径（改回 chunk 弧长口径属 unity_target.cpp 一行变更 + 截图回归）。
2. **GPU clip 域精度无直接读回手段**：像素口径对缝宽的下界 = 像素足迹（4.8mm/px@2m nadir）；clip 舍入的逐顶点读回需 GPU 侧探针（出 def 或宿主协作），v1 不做（§12-6 精神）。MISSING 以「CPU float 域精确数字 + GPU 像端零泄漏」双证据关闭，精度链路已够裁决。
3. **截图链路条纹伪影（pre-existing）**：1/3 列通道轮转，B-TECH-04 起所有 Parity 截图即带（含已验收存档 planet2_ground 84.2% / planet2_midair 95.1% period-3 连续率）；建议独立修复任务（WritePng24/回读链路排查），修复前所有截图判读应以「3px 组平滑」口径进行。
4. **跨档 T 型失配数字入档**：max 76.08m / mean 29.14m（seed 1337）——B-PLANET-09 贴地扫描与 B-PLANET-11 三档判读可直接引用。
5. **地板数字**：单缘 240 顶点对中 55 对渲染口径缝=0（两侧 float 恰好重合）——缝宽分布是 0 与 ~15.6/22.1mm 的双峰，不是连续分布；后续若做方案 B，snap 基准可预期把全部顶点归零（两侧同一 float 表达式）。

### 2.10 完成判据对照（简报 §3）

- VB 缝宽数字（最大/均值/分布）+ GPU 口径对照入报告 ✓——MISSING 清零（设计稿 §1.2 行 6 对应项，数字见 2.1/2.3）。
- 三方案裁决有据 + neighborMask 行为定版（维持 + doctest 三面锁定）✓——裁决待主控确认（2.4）。
- 全量绿（380 用例/823,621 断言）✓；dll 双侧一致 ✓；/WX 零警告 ✓；zrender.def 零改动 ✓。

## 主控裁决（2026-11-14，施工报告后）

1. **三方案裁决：确认 A 维持全缘裙边**——实测依据成立（渲染口径 max 22.1mm vs 裙边裕度 ×4488~×26175，GPU 9 帧 30.7 万像素零泄漏）；B/C 否决理由成立（收益风险倒挂/量级 L）。neighborMask v1 维持"校验+记录"零生产 diff——**确认**。
2. **planet3_seam_closeup.png（白名单外新增）**：**批准入库**——GPU 口径证据帧，验收核对。
3. **新发现①裙边深度口径差**（生产 2%×格距 vs 设计稿 2%×chunk 弧长，near 侧 T 型失配裕度仅 ×1.3）：登记 **B-PLANET-13**（独立裁决任务，不阻塞本任务验收）。
4. **新发现②截图条纹伪影**（period-3 通道轮转，B-TECH-04 存档即有）：登记 **X-DIAG-03**（独立修复）。
5. **G4-R1 落地确认**：远档固定税 920ms→0.019ms 外推消除；转达口径记入 HANDOFF（B-PLANET-07 测得的 ~150µs/块税已消失）。

## 验收清单（主控写，验收代理逐条跑并出证据）

- **M1 范围**：tracked diff 恰 4 处（test_planet_channel.cpp +675 / zrhi_d3d12_window.cpp +184 / zrhi_d3d12_unity_target.cpp +85/−4 / planet3_terrain.png 覆写）+ 新增 planet3_seam_closeup.png（主控批准）+ 本日志 + dll；zplugin.h、zplanet/**、zrender.def（56 不变）、UnityProject 源码零改动；看板/HANDOFF/WORKLOG 未被施工触碰
- **M2 VB 缝宽实测**：亲跑测量 TEST_CASE——三层口径数字与报告一致（每侧 max 7.797mm 贴理论 ULP/2 上界 / VB 15.930mm·mean 4.481mm / 渲染 22.119mm·mean 7.027mm，n=240）；memcmp double 域 240/240 复认
- **M3 GPU 口径**：亲跑 --planet3 贴地共享边段——9/9 帧 307,200 像素全非清屏色、近场逐行亮度跳变 ≤0.7/255；Read 亲验 planet3_seam_closeup.png
- **M4 neighborMask**：三面锁定 doctest 亲跑（校验面全遍历/记录面钩子读写/几何零依赖构造性证据）
- **M5 G4-R1**：亲跑——远档 seam 0.0031µs（税消除）、近档对照 169.7/62.7µs（与 07 实测互证）、seamChecks 计数 doctest（远档不核查/近档仍核查/无邻居不核查）、全程 seamCheckFails==0
- **M6 测试**：build_native ALL GREEN /WX；repo 根 zrender_test.exe **380/823,621 0 failed**（基线 375→380，+5 归因）；dll sha256 双侧一致
- **M7 规范**：文本文件 UTF-8 无 BOM/LF；四节结构完整（测量节先于裁决节）

验收输出 PASS/FAIL + 逐条证据。你独占原生构建链。

## 验收结论（验收代理，2026-11-14）

**总结论：PASS**（M1–M7 全过，证据均为验收代理亲跑；MISSING：无）

| 项 | 结论 | 证据（验收代理亲跑，2026-11-14，AMD 780M 开发机） |
|---|---|---|
| **M1 范围** | **PASS** | `git diff --numstat`：test_planet_channel.cpp +675 / zrhi_d3d12_window.cpp +184 / zrhi_d3d12_unity_target.cpp +85/−4 / planet3_terrain.png（二进制覆写），另有 zrender.dll（构建部署产物）与 TASKS.md +2——后者逐行核对恰为**主控裁决第 3/4 条**的 B-PLANET-13/X-DIAG-03 两条登记行（主控行为，非施工触碰）。untracked 新增：planet3_seam_closeup.png + 本日志。zplugin.h、zplanet/**、UnityProject 源码零 tracked 改动；zrender.def 零改动且实测 56 导出、0 个 Test 符号（unity_target.cpp diff 内 5 个测试钩子均未入 def）；HANDOFF.md / WORKLOG.md 未动。unity_target.cpp diff 逐行核对：`g_seamChecks` 计数器 + `SeamCheckNear` 3 行门（tier==0 return）+ 注释重写 + 5 个测试钩子，与报告 2.5/2.6 一致 |
| **M2 VB 缝宽实测** | **PASS** | 亲跑 `zrender_test.exe -tc="*float VB*"`：1 用例 752 断言全绿。三层口径与报告 2.1 逐项一致：每侧静态 \|ε\| max=7.79717mm（理论上界 ULP/2=7.8125mm，贴界证实）；**VB 回读** max=15.9302mm mean=4.48058mm 分布[0,4,39,26,51,84,36,0]；**渲染口径**（缘上 2m）max=22.1187mm mean=7.02699mm 分布[55,16,27,12,11,31,64,24]；n=240。memcmp double 域逐位复认 240/240 全绿 + 布局逐位自证 240/240。分缘 6 行（14.8985/13.4582/15.2525/15.9302/8.26242/12.8612mm）逐项与报告分缘明细一致。裙边深 far=416.981m near=99.2812m、裕度 ×18852/×4488.57 一致。附带亲跑 T 型失配用例：max=76.0773m mean=29.1433m（报告 76.08/29.14 ✓） |
| **M3 GPU 口径** | **PASS** | 亲跑 `zrhi_d3d12_window.exe --planet3`（repo 根 CWD）：seam pass **9/9 帧 ok**，9 行 clear_win / run_row / clear_total **全 0**（每帧 307,200 像素全非清屏色，含缝带 ±3px 窗口）；CPU 复刻 expect_mm 0.000–2.184mm（与报告一致）；`seamFails=0`；通过行 `D3D12 PLANET3 OK (chunks=30 drawn=4 culled=26 vbytes=1468416 lodNextFrame=1 frame_ms=0.902)`；两 PNG mtime 更新为本次运行（亲证重写）。**Read 亲验 planet3_seam_closeup.png**：整帧近档地形色连续、无贯穿竖直缝、无清屏色泄漏区；period-3 竖条纹伪影清晰可见（与 X-DIAG-03 登记一致）。亮度跳变为**验收侧亲算**（程序无此输出；PNG 离线解析，脚本在系统 temp 不入库）：closeup 全帧（除图像边缘行）max **0.111/255**、terrain 帧近场带（行 240–472）max **0.333/255**——与报告 ≤0.7/255 同量级，判据（无 ≥1px 竖缝）成立；terrain 行 102 处 10.8/255 为局部水平结构（列剖面相邻行亮度一致），非竖缝 |
| **M4 neighborMask** | **PASS** | 亲跑 `-tc="*neighborMask v1*"`：1 用例 **159 断言全绿**。三面可观测：校验面（合法域 0x0..0xF 全遍历 ChunkCreate/SetLod 接受、非法高 4 位+混合位一律 ZR_PLANET_ERR_RANGE）；记录面（TestNeighborMaskForUnitTests 逐值读回 ==mask、SetLod 更新读回、非法拒绝后旧记录保持——h1==0/h2==0xF）；几何零依赖（同格不同掩码两次创建 h1!=h2 均成功 + 账目恒等式保持 + 非法/空句柄读回 0） |
| **M5 G4-R1** | **PASS** | 亲跑 `-tc="*G4-R1*"`：2 用例 **569 断言全绿**。门行为锁定：近档相邻对直驱核查 seamChecks==checks0+1（test_planet_channel.cpp:1021）且零失配（:1024）；**远档相邻对计数不动**（:1033）；**近档孤块计数不动**（:1042）。固定税复测（3 轮取最小）：远档相邻 **0.003125** / 远档散布 **0.003125** µs/块（门后≈0、相邻≈散布 ✓）；近档对照 159.48 / 28.10 µs（与 B-PLANET-07 实测 ~150µs 同量级互证）；外推远档 6144 全量 ≈**0.0192ms**（门前 ~920ms 级，消除率 ~99.998%）；全程 seamCheckFails==0（:1171） |
| **M6 测试与构建** | **PASS** | 亲跑 `Tools/build_native.ps1`：**ALL GREEN**（configure + build + test + dll 部署）；/W4 /utf-8 /MP **/WX** 为全局编译选项（Native/CMakeLists.txt:27，警告即错误）；doctest **380 用例 / 823,621 断言 / 0 failed / 0 skipped**（基线 375→380，+5 = 本日志五个新用例逐一亲跑确认存在且绿）；zrender.dll sha256 双侧一致 **ea97a375…d12c2**（Native/build/bin/Release 与 UnityProject/Assets/Plugins/ZRender） |
| **M7 规范** | **PASS** | 触碰文本文件（3 cpp + 本日志 + TASKS.md）：`git ls-files --eol` 全部 **i/lf w/lf**；首 3 字节均无 BOM（EF BB BF 不存在）；本日志 0 CRLF / 196 行。四节结构完整（任务简报 / 施工报告 / 主控裁决 / 验收清单），**测量节（2.1）先于裁决节（2.4）**，裁决依据链引用实测数字 |

### 给主控的备注

1. **tracked modified 共 6 文件** = 清单口径 4 处 + zrender.dll（部署产物，M1 清单自身列明）+ TASKS.md（主控本人两条登记，与裁决第 3/4 条逐字一致）——无施工越权。
2. **工作区未跟踪杂项（非本任务产物，闭环时勿误收）**：Docs/Logs/TASK-B-PLANET-09.md、TASK-B-PLANET-10.md（并行任务日志）、Tools/Probes/out/ 4 个文件、UnityProject/Assets/ZRender/Tests/ZrPlanetStreamingTests.cs(.meta)、ZrPlanetWiringTests.cs(.meta)、Runtime/Planet.meta（并行任务产物）。B-PLANET-08 闭环应只收白名单 4 处 + planet3_seam_closeup.png（裁决第 2 条已批准入库）+ 本日志。
3. **G4-R1 复测时序数字小幅轮次波动**（远档 0.003125/0.003125 vs 报告 0.0031/0.0025；近档 159.48/28.10 vs 报告 169.7/62.7）——时序测量固有波动，量级与两条结论（固定税消除、相邻≈散布）完全一致，不影响裁决。
4. **主控裁决第 5 条「转达口径记入 HANDOFF」**：HANDOFF.md 当前无改动、未含该条目——属主控闭环动作（施工简报禁止施工触碰 HANDOFF），请主控闭环时处理。
5. closeup PNG 的亮度跳变数字为验收侧独立口径（报告 0.7/255 vs 亲算 0.111/0.333），差异来自行带口径不同，两者同为亚像素噪声级；如需统一口径建议在 X-DIAG-03 修复条纹伪影后重测归档。
