# TASK-B-TECH-04 —— 行星尺度第二阶段：球面地形 chunk + LOD + 大气渐变

状态: doing | 派发: 2026-11-04 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

### 背景

B-TECH-03 验证了数学（浮点原点/球面重力/球体渲染雏形）。第二阶段把它推向"真的行星地表"：**球面地形 chunk 化**（cube-sphere 六面→grid 贴球）、**程序化高度噪声**（顶点位移）、**距离驱动 LOD**（近高分/远低分）、**大气天色渐变**（贴地蓝→高空黑，方案"飞出大气层"的视觉层）。

### 设计要点

1. **Cube-sphere**：立方体六面 grid→投影到球面（标准化立方体映射，避免极点挤压）——`PlanetChunk`：面 id+grid 位置+尺寸；顶点高度=噪声（value/simplex 纯函数——种子可配）
2. **LOD**：每面 quadtree 两档起步（远档 16×16、近档 64×64）——按 chunk 中心与相机距离切换；**无缝**：共享边顶点必须一致（同噪声函数同采样——T-junction 裂缝用裙边 skirt 或一致采样，选型报告说明）
3. **大气**：全屏或天穹着色——天顶色/地平线色随相机海拔插值（贴地蓝天→2 万 km 黑）；地平线雾近似
4. **重定位与重力沿用 B-TECH-03**（planet_math.h 不改语义——新功能增量）
5. **性能预算**：chunk 总数（如 6 面×2 档×N 块）与帧耗报告（`--planet2` 模式 300 帧均值）；DrawCall 数（B-R9-16 的 hud 命令实测）
6. 可证伪：①chunk 缝隙无裂缝（相邻 chunk 共享边顶点位置逐位一致——doctest 断言）②LOD 切换前后同点采样高度一致（噪声纯函数保证）③大气渐变像素证据（贴地 vs 高空截图天色差异）④帧耗在预算内（报告数字 vs B-R9-11 基线口径）
7. 通过行：`D3D12 PLANET2 OK (chunks=N lods=2 seams=0 frame_ms=X)`

### 约束

- 白名单：`Native/tests/`（planet_math 扩展+窗口程序 --planet2+doctest）、`Native/shaders/`（planet.hlsl 扩展或新增）、`Native/CMakeLists.txt`、`Docs/Parity/planet2_*.png`、本任务日志（Docs/Logs/TASK-B-TECH-04.md 自建三节）
- planet_math.h 既有函数语义不改（只增量）；零 zrhi 改动优先（需要时加法报告说明）；不碰 Unity；不 git commit；/W4 /WX；UTF-8 无 BOM/LF/#pragma once
- 基线：native 342/777,011 只增不减

## 施工报告（施工代理写）

### 1. 交付概览

**通过行（连续两跑均 exit 0，结构数字一致——确定性证据）**：

```
D3D12 PLANET2 OK (chunks=6144 lods=2 seams=0 frame_ms=8.359)   # 第 1 跑
D3D12 PLANET2 OK (chunks=6144 lods=2 seams=0 frame_ms=8.803)   # 第 2 跑
```

**文件清单（全部在白名单内；零 zrhi 改动；Native/CMakeLists.txt 零改动——无新 .cpp，
planet2.hlsl 运行期 D3DCompile 加载同 planet.hlsl 先例）**：

| 文件 | 变更 | 内容 |
|---|---|---|
| `Native/tests/planet_math.h` | 增量（§④，~200 行） | cube-sphere 六面基/标准化映射、噪声纯函数、chunk 规范采样路由、LOD 迟滞、地平线距离；①②③节既有语义零改动 |
| `Native/tests/test_planet.cpp` | +7 TEST_CASE | 噪声确定性/共享边逐位一致/LOD 迟滞/跨面棱/地平线（行号见 §3） |
| `Native/tests/zrhi_d3d12_window.cpp` | 新增 `--planet2` 模式 | `zplanet2app` 命名空间（行 8054 起）+ `RunPlanet2Mode()` + main 分发 + 文件头 TASK-B-TECH-04 块；`--planet` 路径零改动 |
| `Native/shaders/planet2.hlsl` | 新增 | 地形（高程带色/等高线/雾/limb）+ 大气天穹两模式；CBufPlanet2 208B 与 CPU 侧 `Planet2DrawConstants` static_assert 对齐 |
| `Docs/Parity/planet2_ground|midair|far.png` | 新增 3 截图 | 贴地/中空/远观关键帧（Read 亲验，见 §3） |

**测试家底（只增不减）**：native **349 用例 / 778,723 断言** 全绿（基线 342 / 777,011 →
**+7 用例 / +1,712 断言**）；`Tools/build_native.ps1` 全绿（configure + build + test +
dll deploy 四段；zrender.dll 重建后逐位未变——本任务代码全在 test/window 目标，不入 dll）。

### 2. 设计与实现

**① cube-sphere chunk 化**（planet_math.h:499-570 + 窗口程序 zplanet2app）
- 六面基 `CubeFaceBasisOf`（face 0..5 = +Z/+X/−Z/−X/+Y/−Y，右手系 t1×t2=n，doctest 锁定）；
  `CubeSphereDir`：p = n + u·t1 + v·t2 → p/|p|（标准化立方体映射，无极点挤压）。
- chunk 格：每面 32×32（quadtree 深度 5 的均匀格）× 6 面 = **6144 chunk**，chunk 弧长
  312.7km；chunk 中心球面点为渲染锚点（double 绝对坐标），顶点以锚点局部 float 静态存储，
  逐帧相机偏移经 model 平移矩阵承载（B-TECH-03 整球同策——顶点零逐帧重算，无逐顶点抖动）。
- **顶点高度 = 噪声纯函数**：`PlanetTerrainHeightM(seed, dir)`——整数格点哈希
  （`PlanetHashU32` PCG 风格 avalanche，种子混入）→ value noise（quintic fade 三线性）→
  4 octave fBm，基频 = R/36km（噪声格胞 1 ≈ 地表 36km），幅值 ±2.4km，位移沿球面法线。
  输入只有 (seed, 单位方向)——**与 chunk/LOD/采样路由完全解耦**：同方向位形必得同高度
  （LOD 切换前后同点高度一致的数学基础，doctest 逐位断言）。种子常量 kP2Seed=1337（可配）。
- 地形法线：chunk 构建期噪声高度场中心差分（±1 全局索引；越界面界为纯函数光滑延拓，
  无分支保确定性）。

**② LOD 两档 + 迟滞**（`PlanetLodLevel`，planet_math.h:572-579）
- 远档 16×16 顶点（256+裙边 64=320）/ 近档 64×64（4096+256=4352），按 chunk 中心-相机
  距离切换：dist < 900km−90km 进近档；dist > 900km+90km 退远档；**迟滞带 ±90km 内保持
  原档**（阈值附近无抖动切换；边界语义"恰在带缘不切"由 doctest 锁定）。
- 档位变化只换 VB 内容 + 共享档位 IB（`BuildTierIndices`：grid + 四缘裙边，全 chunk 复用
  同一 IB）；VB 远档容量启动、进近档升容重建（19 次全程）、退远档复用不缩容。几何只在
  「可见且档位变化」时重建（3021 次全程），不可见 chunk 延迟到可见帧。
- 剔除：地平线曲率剔除（chunk 法线与相机-心方向夹角 > 地平线角 + chunk 半角 + 5° 裕度 →
  不可见）——贴地 7-8 可见 / 中空 33-764 / 远观 ~2600（近半球），CB 池 3200 上限零截断。

**③ 无缝选型（简报要求的选型报告）**
- **同档相邻 chunk：一致采样路线（构造性逐位一致）**。共享边顶点由同一全局整数索引表达：
  `ChunkVertexIndex(cx,i,stride)` 的 g = cx·stride + i（chunk 间共享边顶点，stride=res−1），
  邻 chunk 对 `(cx,i=stride)` 与 `(cx+1,i=0)` 的 g 恒等（整数证据）→ `ChunkFaceCoord`
  同一 double 表达式（u = 2g/total − 1，g/total 均精确整数 double，商正确舍入）→ u/v 逐位
  一致 → `CubeSphereDir`/噪声同输入位形 → 位置/高程 double 位形逐位一致。三层证据：
  doctest（test_planet.cpp:430 同档共享边 memcmp / :482 跨档共享端点同有理数逐位一致）+
  运行期对可见 chunk 同档邻居逐边实测（3021 次构建、**seams=0**）+ 构造注释（planet_math.h §③）。
- **跨档 T 型连接与面界：裙边（skirt）路线**。近档 63 格与远档 15 格格距比 4.2 非整数——
  中段顶点必然不对齐，「强制一致采样」在两档 LOD 的档界无法成立（选型否决理由）；故每
  chunk 四缘顶点沿 −球面方向下垂 **2% chunk 弧长 = 6.25km** 生成裙边（> 噪声 ±2.4km/36km
  波长下跨档边缘折线高程差上界 ~2km），T 型缝与面界缝被几何遮蔽。跨面棱方向本身亦逐位
  一致（±1 端点为小整数精确运算，doctest test_planet.cpp:303），面界裙边只兜底法线差分
  路径差与跨档组合。
- 结论：**同档共享边零裂缝（逐位一致，可证伪）+ 跨档/面界裙边遮蔽（鲁棒）**——两路线
  组合，与 quadtree-LOD 引擎惯例一致。

**④ 大气天色渐变**（planet2.hlsl mode 1 天穹 + mode 0 地形）
- 天穹：相机中心单位球（缩放经 model 承载，逐相位穹半径 0.85·farZ），视线方向相对渲染
  空间 up 做天顶/地平线两色插值；两色由 CPU 按海拔对数插值预混：
  t = (log10(alt+1)−3)/(log10(2e7)−3)，贴地全强蓝 天顶(0.16,0.36,0.78)/地平线(0.58,0.72,0.92)
  → 2 万 km 黑 (0.004,0.006,0.012)。
- **地平线雾**：地形距离雾目标色 = 当前海拔地平线色（贴地 150km / 爬升渐涨 / 太空关）——
  地平线处地形与天穹色无缝衔接（贴地截图水平线无接缝即此设计）。
- **limb 大气镶边**：地形掠射 fresnel（pow 4）× (0.25+0.75t) × 0.75 增蓝——远观行星轮廓
  蓝色镶边（像素证据：轮廓带 maxB=187 vs 太空角 3）。
- 太阳：与 planet.hlsl 同一 kSunDir（光照语义连续）+ 天穹太阳晕（900 次幂亮斑 + 8 次幂宽晕）。

**⑤ 沿用 B-TECH-03（语义零改）**：浮点原点（`zplanet::FloatingOrigin` 10km 阈值重定位，
全程 218 次——chunk 锚点为 double 绝对坐标，重定位对渲染透明）；球面重力常量/半径；
`--planet` 模式代码路径零触碰（回归同数字）。

**⑥ 飞行编排（--planet2，300 帧恒离屏 640×480）**：贴地（0-129，对地 600m 随地形起伏
跟随，沿面 0 对角线 v=0.55u 前进 6km/帧——穿二维噪声特征使山脊可见，跨 chunk 边界 +
每 2 帧一次重定位节奏）→ 爬升（130-219，alt=600+(2e7−600)·s⁴ 至 2 万 km；w 封顶 0.999
防 fwd 与 up 恰 180° 的 lookAt 退化——NaN 视图矩阵曾致全帧清屏色，修复见 §5）→ 远观
（220-299，2.6371e7 m 慢扫 0.5°→25°）。

### 3. 数值证据（--planet2 实跑 exit 0 ×2）

**结构数字**：chunks=6144（6 面×32²，chunk 弧长 312.7km）｜lods=2（远 16×16/近 64×64，
阈值 900km 迟滞 ±90km）｜**seams=0**（运行期共享边 double 位形 memcmp，3021 次构建）｜
裙边 6255m｜LOD 切换 48 次 / VB 升容 19 次｜可见均值 981.6 / 峰值 2662 / CB 池截断 0｜
重定位 218 次（10km 阈值，maxOriginOffset 9010km——爬升段合法峰值）。

**doctest（+7 用例 / +1,712 断言，行号）**：
- `cube-sphere: 六面基右手系+方向归一`（test_planet.cpp:270）
- `cube-sphere: 跨面共享棱两侧方向逐位一致`（:303——±1 端点小整数运算 memcmp）
- `噪声纯函数: 同种子同输出逐位确定+种子可配+值域有界+空间连续+确有起伏`（:331——
  324 方向全采样 memcmp 确定性、54 方向种子区分度、|h|≤2400m、C0 连续、起伏 ≥20% 幅值）
- `chunk 共享边: 同档相邻 chunk 共享边顶点逐位一致（规范路由）`（:430——水平/垂直邻居
  全缘 64 顶点 pos/normal/height memcmp + 非共享点必异防假绿）
- `chunk 共享边: 跨档共享端点逐位一致 + 球面约束`（:482——远 15/近 63 格同棱角点
  memcmp（正确舍入除法性质）+ |pos−C|=R+h + 构建确定性）
- `LOD: 两档迟滞——阈值/带内保持/边界严格语义/振荡抑制`（:520——±0.4 迟滞带振荡 200 次：
  无迟滞 >100 次切档 vs 迟滞 ≤2 次）
- `LOD: 地平线距离`（:561——√(2Rh) 量级窗 + 精确式界 √(1+h/2R) + 单调）

**像素证据（三关键帧 Read 亲验 + 探针 64/64）**：
- `planet2_ground.png`（帧 59）：贴地 600m——蓝天 + 绿色山地地形，**雪线山脊起伏可见**
  （对角航线穿越 ±2.4km 噪声地形）；地形探针非蓝主导 ✓ / 天探针 b−r=153 ✓
- `planet2_midair.png`（帧 160，alt≈258km）：**地平线弧线曲率可见** + 天色转深蓝
  （b−r=76）+ 行星面被海拔雾化为蓝灰（雾目标色=当前地平线色的直接体现）
- `planet2_far.png`（帧 255，2.6371e7 m）：行星圆盘 + 黑色太空 + **蓝色大气镶边**
  （limb 扫描 maxB=187 vs 太空角 3）；行星中心亮（地形探针 luma>15）/ 太空角黑（luma<13）
- **天色渐变三点梯度**：ground b−r=153 → climbFirst=76 → climbLast=2（0-255 口径，
  程序断言 ≥40 递减）+ 高空探针 luma<40 判黑——「贴地蓝→2 万 km 黑」逐档收窄的数值链
- 开发中修复的探针误报均属判据校准（雾化地形≠天空、渐变中段非纯蓝），已如实记录于 §5

**回归**：`--planet` 旧模式 exit 0——`D3D12 PLANET OK (origin=RELOCATED@90694m
jitter=0.0028125m/0.0000001m)`，与 B-TECH-03 基线数字一致（--planet 代码路径零改动）。

**InfoQueue 口径**：本机当前 D3D12 调试层不可用（`debug layer UNAVAILABLE (degraded)`，
--planet 同机同状态——环境态非本任务引入），errors=0 为降级计数；硬证据为 device
removed reason = S_OK + 两跑 exit 0。

### 4. 帧耗预算报告

**口径**：QPC 墙钟含 Submit+WaitIdle（与 B-R9-11 离屏 lights 基线 2.045ms 同法；无
vsync；640×480 离屏 RGBA8+D32）。300 帧均值两跑 **8.359 / 8.803ms**。

| 相位 | 帧段 | 可见 chunk | 绘制次数/帧 | 主导成本 |
|---|---|---|---|---|
| 贴地 | 0-129 | 7-8（全近档） | 8-9 | 近档 4352 顶点×7 构建（首帧后仅档变重建） |
| 爬升前段 | 130-165 | 33→数百（两档混） | 34→数百 | 档位切换重建 + 可见数增长 |
| 爬升后段 | 165-219 | 764→2408（全远档） | ~800-2400 | 远档绘制调用（每 chunk 4 次 RHI 调用） |
| 远观 | 220-299 | ~2500-2650（全远档） | ~2600 | 绘制调用主导（~1.3M 三角/帧 GPU 轻载） |

**预算判定**：均值 8.8ms < 16.6ms（60fps 实时线）✓ < 33.3ms（30fps 硬线）✓。相对
B-R9-11 基线 2.045ms 高 4.3×——差额即 chunk 化代价（均值 ~980 chunk × 每 chunk 独立
VB/CB 绑定 + 天穹；RHI v1 无 instancing/无 per-draw 根参数复用），数字如实呈报；优化
路径（instancing、按面合并 CB、间接绘制）留后续任务，不在本简报承诺内。峰值 88.9ms
为首帧一次性成本（6144 VB 池创建 ~63MB + 首批近档构建；第 2 帧起 <10ms），不影响稳态。

### 5. 开发过程缺陷记录（均已修复并以用例/断言锁定）

1. **噪声基频换算错误**：初版 fBm 输入 = dir/36000——采样范围落在单个噪声格胞内，
   地形恒平坦（doctest「确有起伏」抓出：hMax−hMin=0.17m）。修：baseFreq = R/波长
   （方向分量 1 ≈ 弧长 R），planet_math.h:471-489。
2. **lookAt 180° 退化**：爬升末帧 fwd 与 upHint 恰反向 → `PlanetMatLookAtLH` 叉积零
   → NaN 视图矩阵 → 全帧清屏色（天空探针 luma<40 分支假绿、地形探针真红）。修：爬升
   混合 w 封顶 0.999 + 远观 θ 从 0.5° 起（窗口程序相机编排注释）。
3. **探针判据两处过严**（非产品缺陷）：高空雾化地形被判"天空"、渐变中段 b−r 39 差 1
   过 40 线——按物理语义分层校准（低空蓝/中段偏蓝/高空黑），见 §3 探针口径。

### 6. 已知受限（如实登记，不阻塞）

- **同机 D3D12 调试层不可用**（--planet 同态）：InfoQueue errors=0 为降级口径，
  device removal S_OK 为硬证据（§3）。
- **裙边为遮蔽非消除**：跨档 T 型连接的几何裂缝仍存在于裙边下方（被地形遮挡不可见）；
  「消除」需邻接档位约束网格（CDLOD 式），超出两档起步范围。
- **LOD 档界高度突变**：迟滞只抑制档位抖动，不抑制近→远档边界处的几何 LOD 弹跳
  （900km 阈值在轨道视角下不可见，贴地跨档帧存在轻微跳动，截图帧未落于档界）。
- **帧耗为开发机剖面**（780M 核显 + 本机后台带载），均值受首帧一次性成本摊薄影响
  ~0.15ms；两跑差 5% 属环境噪声（B-R9-11 同款口径）。
- chunk 格为均匀 32×32（quadtree 深度固定），未做视相关自适应细分（两档×距离阈值已满足
  简报"每面 quadtree 两档起步"）。

## 验收清单（主控写）

- [ ] V1 build 全绿（用例数只增不减，数字写入结论）
- [ ] V2 亲跑 `--planet2`：PLANET2 OK 数字行+三张截图（贴地/中空/远观）Read 亲验（地形起伏可见+天色渐变）
- [ ] V3 无裂缝断言（共享边逐位一致——行号）；LOD 档位切换逻辑（距离阈值/迟滞——行号）
- [ ] V4 大气渐变像素证据（贴地 vs 高空截图对比数字）
- [ ] V5 帧耗预算报告（chunk 数/帧耗，与 B-R9-11 基线口径对照）+文件规范+施工报告四节完整

## 验收结论（验收代理写）

**结论: PASS**（2026-11-08，验收子代理独立执行，未跑 Unity，未改代码；下列所有运行/截图均为本验收亲跑产物）

- **V1 PASS（build 全绿）**：亲跑 `Tools/build_native.ps1` exit 0，四段全绿（configure+build+test+dll deploy，/W4 /WX）；doctest **349 用例 / 778,723 断言 0 failed**（基线 342/777,011 → +7/+1,712 只增不减）。
- **V2 PASS（--planet2 亲跑）**：两跑 exit 0，`D3D12 PLANET2 OK (chunks=6144 lods=2 seams=0 frame_ms=7.897)` 与 `(…frame_ms=8.077)`，结构数字两跑逐项一致（relocs=218 / switches=48 / vbUpgrades=19 / rebuilds=3021 / visibleAvg=981.6 / visibleMax=2662 / CB 截断 0）；运行期共享边核查亲证 `runtimeBitwiseFails=0 (shared-edge double memcmp over visible chunk neighbors)`；帧耗 7.9/8.1ms 均值 < 16.6ms 实时线（峰值 47-49ms 为首帧一次性成本，与报告口径一致）。
- **V3 PASS（三截图 Read 亲验）**：`planet2_ground.png` 贴地蓝天渐变（天顶深→地平线浅）+ 地平线山脊雪线起伏可见；`planet2_midair.png` 地平线弧线曲率清晰 + 天色转深蓝 + 行星面海拔雾化；`planet2_far.png` 行星圆盘 + 黑色太空 + 蓝色大气镶边（limb maxB=187 vs 太空角 3 程序断言）——「地形起伏可见+天色渐变」判读**达成**（ground 前景起伏较缓、起伏集中于地平线山脊，如实记录不阻塞）；天色三点梯度 ground b−r=153 → climbFirst=76 → climbLast=2（程序断言递减）与截图观感一致。
- **V4 PASS（无缝保证审查）**：同档共享边 doctest `test_planet.cpp:430`——水平/垂直邻居全缘 64 顶点 pos/normal/heightM 逐位 memcmp，且 **:470-479 非共享内部点必异（memcmp != 0）防恒等假绿**；跨档端点 `:482`（远 15/近 63 格同棱角点逐位 + |pos−C|=R+h 球面约束 + 构建确定性）；跨面棱 `:303`。**选型评估：成立**——同档走构造性一致采样（共享边同全局整数索引 `ChunkVertexIndex` planet_math.h:529 → 同一 double 表达式 `ChunkFaceCoord` :536 → 同路由 `PlanetChunkVertexAtGlobal` :561）；跨档 T 型缝否决"强制一致采样"理由正确（63/15=4.2 非整数格距比，中段顶点不可能对齐），改用裙边 6.25km（=2% 弧长，窗口程序 :8072，> 跨档边缘高程差上界 ~2km）遮蔽，§6 如实登记"遮蔽非消除"。运行期核查（zrhi_d3d12_window.cpp:8770-8797）对同档可见邻居逐边 memcmp 且与渲染构建同走规范路由——断言的就是渲染路径。
- **V5 PASS（确定性/迟滞/回归）**：噪声确定性 doctest `:331`（同种子两次全量采样 memcmp :359 + 逐点 :365 + 种子可配过半异 :386 + 值域有界 + C0 连续 + 起伏 ≥20% 幅值 :426——该断言即施工期缺陷 1"地形恒平坦"的抓手）；LOD 迟滞 doctest `:520`（带缘严格不切 :526/:531、带内保持 :535-538、**阈值附近振荡 200 次：无迟滞 >100 次切档 vs 有迟滞 ≤2 次 :554-555**）；`--planet` 回归亲跑 exit 0——`D3D12 PLANET OK (origin=RELOCATED@90694m jitter=0.0028125m/0.0000001m)` 与 B-TECH-03 基线数字一致。

**附加观察（均不阻塞）**：①运行期 seams 核查 memcmp 的是规范路由重算值（非 GPU VB 回读）——BuildChunkVerts（:8206-8209）写盘即同一路由值，保证传递有效，口径与报告描述相符；②文件规范亲验：`git ls-files --eol` i/lf w/lf + od 字节核验 UTF-8 无 BOM/LF（planet_math.h/test_planet.cpp/planet2.hlsl/本日志）；③白名单符合：git status 中本任务改动仅 whitelist 五类文件（其余 M/?? 为并行任务 A 轨产物，与 B-TECH-04 无关），`Native/CMakeLists.txt` 确实零改动；④D3D12 调试层不可用（degraded）为环境态（--planet 同态亲证），两跑 exit 0 + device removed S_OK 为硬证据，与报告口径一致；⑤三张 PNG 的 MD5 互异（542eaa…/a4f586…/2bc407…），排除同图复用。
