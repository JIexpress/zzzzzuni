# TASK-B-PLANET-10 —— abi-precision 探针：double→float 提交精度损失（四点双口径）

- **轨道**：B / PLANET（设计稿 §11-10；改进计划 §5"abi-precision"探针）
- **状态**：doing
- **派发**：主控，2026-11-14
- **依赖**：B-PLANET-05 ✓（SetCameraOrigin round-trip）；完整版加 04（chunk 顶点）

## 任务简报（主控写）

### 1. 做什么

1. **四点 ULP 实测**：相机/目标在 100m/1km/10km/100km 距离下——
   - 口径 A（**纯数学**）：double 绝对坐标 → 相机相对 float 的位置损失（m 与 ULP 双单位；对照 A-WORLD-01 实测 7.8mm@100km 与设计稿 §9 预算表逐环节）
   - 口径 B（**ABI 全链**）：经真实 SetCameraOrigin+chunk 提交路径（SetWorld→ChunkCreate 顶点→模型平移 double 域相减一次 cast）的端到端顶点位置损失
2. **输出**：四距离×双口径对照表 + JSON 落盘（Tools/Probes/out/，白名单+边界断言照 fsr4_probe）+ README 登记
3. **判据挂账**：设计稿 §9 预算表逐环节 vs 实测——超出预算环节如实标注（改进计划 §6"没有基线就不知道变好变坏"）
4. 形态：独立探针可执行（fsr4_probe 同款工程模式）或 test 内基准段（按口径归位）；**不进 zrender_test 断言**（精度是测量不是过/不过——设计稿口径），数字进 JSON+报告

### 2. 白名单

Native/tests/**（探针文件）、Native/CMakeLists.txt（如需）、Tools/Probes/**、本日志、dll（如重建）；不碰 zplugin.h/zplanet/zplugin.cpp/def/UnityProject/看板

### 3. 铁律

约法三章四判据；数字带口径（距离/方法/机器）；MISSING 规矩；施工不 commit、不改看板；UTF-8 无 BOM/LF；/WX 零警告；构建走 Tools/build_native.ps1

### 4. 完成判据

- 四点×双口径数字齐（JSON+报告）；与设计稿 §9 预算表对照结论（每环节 达标/超出+数字）
- README 登记；可复核（第三人重跑数字一致）

---

## 施工报告（施工代理，2026-11-14）

### 1. 交付形态

`Native/build/bin/Release/abi_precision_probe.exe`（独立探针，fsr4_probe 同款工程模式——**不进 zrender_test**，精度是测量不是过/不过）。纯 CPU + dll 导出面，**零 D3D12 设备依赖**（ChunkCreate 延迟上传语义下无 RenderFrame 即永不触 GPU）。四点（100m/1km/10km/100km）×双口径：

- **口径 A（纯数学）**，planet_math.h 生产头三件套：
  - `A.correct` = `float(objAbs−camAbs)`（`FloatingOrigin::RenderOffsetF`——重定位正确路径，生产同式）
  - `A.naive@planet-frame` = `float(objAbs)−float(camAbs)`（`RenderOffsetNaiveF`——全 float 大坐标；本几何下坐标幅值 ~6.37e6 m）
  - `A.jitter@origin-frame` = `JitterAudit(D, 1.0, 10.37)`（A-WORLD-01 7.8mm@100km 同函数同参数口径——相机自原点飞 D、近场物体恒前方 10.37m）
- **口径 B（ABI 全链）**，生产链每步实测：
  1. **zrender.dll LoadLibrary 生产导出面**（--planet3/--pbudget 同策）：`ZR_ApiVersion`(=3) → `ZR_Planet_SetWorld`(seed 1337, center (0,−R,0)) → `ZR_Planet_ChunkCreate`(face 0 / chunk (16,16) / cells 32 / lod 1 近档，rc=0 账目自证) → 逐距离 `ZR_Unity_SetCameraOrigin` → `ZR_Unity_GetCameraOrigin`（round-trip 逐位）→ `ZR_Planet_SampleHeight`（§9 row10）→ `ChunkDestroy`+`ClearAll` 收尾；
  2. **顶点字节 = 生产 `GenerateChunkVerts` 本体**（zrhi 静态库白盒钩子 `TestGenerateChunkVertsForUnitTests`——ChunkCreate 内部同函数零复制零漂移，B-PLANET-08 测量探针同策；喂上下文的 SetWorld 走静态 zplugin 同源实现，与 dll 各自独立实例互不触碰）——4352 顶点（64²+4×64 含裙边），每顶点 double 真值由同一规范路由 `PlanetChunkVertexAtGlobal` 复刻（网格顶点逐位自证，裙边按生产同式 `posAbs−normal·skirtDrop` 容差自证）；
  3. **模型平移 `t = float(anchorAbs−camOrigin)`**（unity_target.cpp:2545 逐绘 CB 口径：double 域相减后一次 cast）+ **`viewPos = local + t`**（float32 加法，planet2.hlsl VS `mul(model,·)` 口径）→ 端到端损失 = `viewPos − (posAbs−camOrigin)`（double 域度量，B-PLANET-08 (4) 同式）。

**几何口径**：相机 = 锚点沿球面法向悬停 D（target = 锚点顶点，|target−cam| = D；口径 A/B 同一对几何可直接对照）。锚点 = 面内中心格顶点（`ChunkVertexIndex(16,31,63)`——锚点即真实网格顶点，`local(anchor)` 恒 0 的构造性自证）。实测锚点 (195756.29, −6175243.71, 6365236.83) m、chunkArc 312736 m（= §10 "312.7km" ✓）、cellArc 4964.06 m（= planet-budget 近档格距 ≈4965m ✓）、skirt 99.28 m。JSON 落 `Tools/Probes/out/abi-precision-<UTC>.json`（文件名白名单 + out 目录边界断言照 fsr4_probe；退出码 0=JSON 落盘，1=未落盘）。

### 2. 四点×双口径数字表（本机 DESKTOP-08EO3II，Release /O2，seed 1337，2026-11-14T00:47:07Z）

单位 m（括号内 = 损失/ULP(D) 倍数）。ULP(D) 实测（FloatUlpAt nextafter 口径）：100m=7.62939e-6 / 1km=6.10352e-5 / 10km=9.76562e-4 / 100km=7.8125e-3——**与 §9 理论对照值逐位一致**（selfCheck.ulpMatchesDesign9=true）。

| D | 口径 A.correct | 口径 A.naive@planet | 口径 A.jitter@origin（naive / reloc） | 口径 B.trans（角偏差 px@1080p/hFOV60°） | 口径 B.center（锚点） | 口径 B.chunkMax（全块 4352 顶点） |
|---|---|---|---|---|---|---|
| 100m | **2.41026e-6**（0.316 ULP） | 0.405553（5.32e4 ULP） | 2.74658e-6 / 1.14441e-7 | **2.41026e-6**（0.316；4.01e-5 px） | = B.trans（逐位） | 13.4363 mm（idx906：camDist 170.6km/\|local\| 170.6km） |
| 1km | **8.84198e-6**（0.145 ULP） | 0.224922（3.69e3 ULP） | 4.88281e-6 / 1.14441e-7 | **8.84198e-6**（0.145；1.47e-5 px） | = B.trans | 13.9830 mm（idx4319：camDist 201.7km） |
| 10km | **3.32562e-4**（0.341 ULP） | 0.249222（255 ULP） | 1.17188e-4 / 1.14441e-7 | **3.32562e-4**（0.341；5.53e-5 px） | = B.trans | 8.58198 mm（idx906：camDist 171.0km） |
| 100km | **1.37250e-3**（0.176 ULP） | 0.053315（6.82 ULP） | **2.8125e-3** / 1.14441e-7 | **1.37250e-3**（0.176；2.28e-5 px） | = B.trans | 15.6509 mm（euclid 15.7036mm，idx4319：camDist 227.0km） |

读法（口径 A/B 同一对 target-camera，可直接对照）：

1. **重定位正确路径（A.correct）**：四点全部 ≤ 0.5 ULP(D)——单次舍入半 ULP 界成立，无异常放大；100km 处 1.37mm << naive 对照（下条）。
2. **naive@planet-frame（0.05~0.41 m）**：行星中心系下大坐标 float 的灾难在**所有距离**存在（误差由坐标幅值 ULP(6.37e6)=0.5m 封顶，与相机-目标距离无关）——比 A.correct 差 **6.82~53200 倍**；这就是 §9「naive 全 float 大坐标路径」对照在行星系几何下的量化版。
3. **naive@origin-frame（JitterAudit 口径）**：100km naive=**2.8125mm** ∈ (1mm, ULP=7.8125mm]——与 test_planet.cpp:31-40 doctest（同函数同参数，确定性复现）及 A-WORLD-01 §5.3「float direct error=1.0mm」同量级口径互证；reloc 恒 1.14e-7 m（只依赖近场偏移 10.37m，与飞行距离无关——重定位语义的直接证据）。
4. **B.center ≡ B.trans（逐距离相等）**：锚点顶点 local 恒 0 → 端到端链对锚点顶点**零附加损失**——§9 row7（平移量化）四点实测即本列。
5. **B.chunkMax（8.6~15.7mm）**：主导项 = chunk 角点顶点局部 float 化（row5，幅值 170~202km 的 ULP）+ 平移量化（row7）+ VS float 加法舍入三者合成；角偏差最大 **1.15e-4 px**@1080p/hFOV60°——亚像素（§9 row7「角偏差亚毫像素」成立）。**口径标注**：提交口径不含可见性剔除——100m 相机下 170km 外角点在地平线下不可见；可见性口径看角偏差列。

### 3. §9 预算表逐环节对照（budget9——数据非断言，达标/超出都落 JSON）

| # | 环节 | §9 参照 | 本探针实测 | 判定 |
|---|---|---|---|---|
| 1 | 相机 double 逐帧累计 | double ULP@1e7m = 2^-29 ≈ 1.86e-9 m（计算） | 实算 1.86264514923095703125e-9 m（= 2^-29 逐位）；逐帧累计属托管 WorldPosition | **NOT_IN_PROBE**（归属托管侧；参照值实算确认） |
| 2 | 浮点原点重定位 4000m | \|cam−origin\| ≤ 4000m 封顶 | 托管 WorldOrigin 生产口径（A-WORLD-01）；原生只消费 SetCameraOrigin——其直传在 row3 实测 | **NOT_IN_PROBE**（归属托管侧） |
| 3 | SetCameraOrigin 跨 ABI | 0（double 直传无损） | **4/4 距离 round-trip 逐位相等，maxdiff=0 m** | **达标** |
| 4 | chunk 锚点 double | 0（设计） | desc.originAbs 经 dll ChunkCreate double 直传（账目自证）；静态同源钩子路径 local(anchor)=**0 逐位** | **达标**（顶点级自证口径=静态同源，dll 内字节不可经 C ABI 回读——见 §5 风险） |
| 5 | 顶点局部 float = float(posAbs−anchor) | ≤ ULP(~221km)=15.6mm（chunk 角点；计算） | **devMax = 7.8109 mm**；实测 max\|posAbs−anchor\| = **284719 m**（ULP=31.25mm binade） | **达标**（≤§9 参照 15.6mm ✓；≤实测幅值半 ULP 15.625mm ✓；≤2^17 binade 半 ULP 7.8125mm ✓——devMax 即该 binade 的半 ULP 实例） |
| 6 | 共享边双侧静态缝 | ≤ ~31mm（2×ULP，估算） | 本探针不重测——B-PLANET-08 生产路径实测 **max 15.93mm**（240 顶点对） | **COVERED_ELSEWHERE**（达标：估算上界内） |
| 7 | model 平移 float = float(anchor−camOrigin) | 近场 ≤ ULP(4km)=0.488mm（仅近场成立）；远景角偏差亚毫像素 | 四点 **2.41µm / 8.84µm / 0.333mm / 1.373mm**；近场三点余量 202×/55×/1.47×；角偏差 2.28e-5~5.53e-5 px；B.chunkMax 角偏差 ≤1.15e-4 px | **达标**（近场 claim ✓；亚毫像素 ✓） |
| 8 | view/proj float（托管构造） | 近场 <1e-6 m（重定位路径实测） | 托管侧构造——test_planet.cpp:36 doctest 先例、A-WORLD-01 托管接线 | **NOT_IN_PROBE**（归属托管侧） |
| 9 | GPU 光栅 float32 | 与现状一致（非新增误差源） | RHI 内部——planet-budget 探针像素口径另测 | **NOT_IN_PROBE**（归属 RHI） |
| 10 | 高度输出 float（SampleHeight） | ≤ ULP(±2.4km) ≈ 0.244mm（计算） | 16 样本 `ZR_Planet_SampleHeight`（dll 纯函数 ABI）vs `PlanetTerrainHeightM(1337,·)` double 复刻：**devMax = 5.8232e-5 m** @ max\|h\|=1055.51 m（ULP=1.2207e-4 m；≤半 ULP=6.1e-5 ✓） | **达标**（实际幅值口径与 §9 参照口径均 ✓） |

**对 §9 的一处实测修正（如实登记，结论不变）**：row5 的幅值基准 "~221km" 按中心线性弧长（ChunkArcM=格距×√2×31.5）估算；实测 max|posAbs−anchor| = **284.7km**——gnomonic 投影使远离面心的格距放大 + 地形高程（±km 级）计入。ULP 界幅值口径建议后续按实测校准（31.25mm binade）；devMax=7.81mm 在任何口径下都达标。

### 4. 自证与可复核

- **顶点路径自证**：网格顶点 4096/4096 `local == float(posAbs−anchor)` 与生产钩子输出**逐位一致**（生产头复刻 = 生产函数本体）；裙边 256/256 容差内（kPi float 常量位级容忍域）；锚点顶点 local=(0,0,0) 逐位。
- **ULP 参照**：FloatUlpAt 四点实测与 §9 理论值逐位一致（ulpMatchesDesign9=true）。
- **账目**：create=1 → live=1/pendingUploads=1/vertexBytes=0（无 RenderFrame 不触 GPU）；destroy=1 → live=0/pendingFrees=1；`createTotal−destroyTotal == liveChunks` 全程成立。
- **naive 幅值帽**：四点 naive 误差均 ≤ 坐标幅值 ULP 帽 + 结果舍入（理论界内）。
- **复跑**：两次独立运行（00-47-07 / 00-47-55 JSON）**数字逐位一致**（probedAt/outFile 除外）——第三人重跑判据满足。

### 5. 测试数字与改动清单

- **既有用例回归**：`Tools/build_native.ps1` 全链（configure + Release /W4 /WX 零警告 + doctest）——**380/380 passed，823621 assertions，0 failed**（探针零侵入 zrender_test，用例数无增减）。
- 改动清单（施工不 commit）：

| 文件 | 改动 |
|---|---|
| `Native/tests/abi_precision_probe.cpp` | 新增（探针本体，~1100 行） |
| `Native/CMakeLists.txt` | +28/-0（abi_precision_probe 目标——fsr4_probe 同款：仅 Release 默认构建，不进 ctest） |
| `Tools/Probes/README.md` | abi-precision 登记行 + 探针节 |
| `Tools/Probes/out/abi-precision-2026-11-14T00-47-07.json` | 存档（本报告口径） |
| `Tools/Probes/out/abi-precision-2026-11-14T00-47-55.json` | 复跑存档（逐位一致证据） |
| `Docs/Logs/TASK-B-PLANET-10.md` | 本报告 |

- `git diff --numstat`：`28  0  Native/CMakeLists.txt`（其余为新增文件；不碰 zplugin.h/zplanet/zplugin.cpp/def/UnityProject/看板——git status 复核无越界）。

### 6. 风险与未验证（MISSING 规矩——本探针 0 MISSING，以下为口径边界）

1. **几何口径 = 锚点悬停**：相机在 chunk 中心正上方（|target−cam| = D 精确可控，四点可比）。生产中相机可在 chunk 内任意位置——角点顶点的端到端损失随相机位置变化，但角偏差恒 ≤2^-24 rad 级封顶（§9 row7 机制），玩法可见性口径以角偏差列为准。
2. **dll 内顶点字节不可经 C ABI 回读**（无 GPU 上传边界）：口径 B 的顶点数值证据来自 zrhi 静态库白盒钩子（ChunkCreate 内部同一 GenerateChunkVerts，同源编译）——与 B-PLANET-08 测量探针同口径限制；dll 侧经 ChunkCreate 校验链+账目自证提交路径本身工作正常。
3. **SampleHeight dir 复刻**：探针 normalize 复刻与 dll 内部 op 序可能差 1-2 double ULP（zplugin.cpp 组 4 注释声明的构造性不可避免）——经 float 输出粒度完全吸收（实测 dev ≤ 半 ULP(max|h|)，纯 cast 特征）。
4. **row1/2/8/9 不在本探针口径**（托管累计/重定位触发/托管 view/GPU 光栅）——JSON budget9 逐条写明归属，未伪造数字；1080p 像素口径归 planet-budget 探针（已有存档）。
5. **天穹（mode 1）与远档（tier 0）未单测**：天穹属窗口演示层（非 chunk 通道）；远档顶点局部幅值与近档同 chunk 网格（同一锚点/格距，res 16）——row5 结论按近档实测外推成立，未单独跑远档档位（如需可在探针加 lod=0 变体，数字入 JSON 同构）。

**验收建议**：四点×双口径数字齐、§9 逐环节对照齐（实测 5 环节全达标 + 4 环节归属明确 + row6 归 B-PLANET-08 存档）、JSON 落盘可复核（复跑逐位一致）、README 登记齐、380 用例全绿——建议按验收清单独立复跑（重跑探针应逐位一致）后判 PASS。

## 验收清单（主控写，验收代理逐条跑并出证据）

- **P1 范围**：tracked diff 恰为 abi_precision_probe.cpp（新增）/CMakeLists.txt(+28)/README(+14)/2 个 JSON/本日志；zplugin.h、zplanet/**、zplugin.cpp、zrender.def、UnityProject 零改动；看板/HANDOFF/WORKLOG 未被施工触碰
- **P2 复现**：亲跑探针两次——剥离时间戳/路径字段后能力载荷**逐位一致**（0 MISSING）
- **P3 §9 对照抽查**：亲核 3 环节（row5 顶点局部 float devMax=7.8109mm≤7.8125mm 半 ULP / row7 平移 2.41µm~0.333mm+角偏差 ≤1.15e-4 px / row10 SampleHeight 5.82e-5m≤0.244mm）+ULP(D) 理论值逐位（100m=7.62939e-6 等）
- **P4 naive 对照**：亲核 correct vs naive 比率（6.8~5.3e4×）与 jitter@100km naive=2.8125mm 与 test_planet.cpp 确定性一致
- **P5 ABI 链**：探针走生产 dll（ApiVersion=3）round-trip 4/4 逐位、顶点=生产 GenerateChunkVerts 白盒钩子零漂移——读码+亲跑核对
- **P6 构建**：build_native.ps1 ALL GREEN /WX；repo 根 zrender_test.exe 380/823,621 0 failed（探针零侵入）
- **P7 规范**：新增文件 UTF-8 无 BOM/LF；四节结构完整；README 登记含对端跑法

验收输出 PASS/FAIL + 逐条证据。你独占原生构建链。

---

## 验收结论（验收代理，2026-11-14）

独立复跑与读码复核全部完成。**总结论：PASS（P1–P7 七项全过，0 MISSING）**。验收证据落盘：`Tools/Probes/out/acceptance-planet10-build.log`（本次 build_native.ps1 全链）、`acceptance-planet10-run1.log`/`-run2.log`（探针两次亲跑控制台）、`abi-precision-2026-11-14T00-57-35.json`/`-00-57-39.json`（验收亲跑两次 JSON 存档）。

| # | 结论 | 证据（亲跑/亲算/读码） |
|---|---|---|
| **P1 范围** | **PASS** | `git diff --numstat` 恰为 `Native/CMakeLists.txt`(+28/0) 与 `Tools/Probes/README.md`(+14/-1)；新增文件恰为 `Native/tests/abi_precision_probe.cpp`、2 个施工 JSON 存档、本日志。zplugin.h/zplanet/**/zplugin.cpp/zrender.def/UnityProject（tracked）零改动；看板/HANDOFF/WORKLOG 不在 diff 中（git status 复核）。其余未跟踪文件均早于本任务施工窗（Unity 测试 06:49–06:56 属 B-PLANET-07；planet-budget/planet3 验收产物 08:14–08:24 属 B-PLANET-09 验收）——以施工 JSON 时间戳 00:47:07Z（=本地 08:47）为界逐一核对 mtime |
| **P2 复现** | **PASS** | 验收代理亲跑探针两次（00-57-35Z / 00-57-39Z，exit=0）：两份 JSON `diff` 仅 `probedAt`/`outFile` 两行；控制台日志剥离时间戳/文件名后 `diff` 逐字节一致；且两份验收 JSON 与施工存档 `00-47-07.json`/`-55.json` 同样仅差这两字段——**第三人重跑数字逐位一致**判据满足。两次亲跑 `[missing] 0 项` |
| **P3 §9 对照抽查** | **PASS** | 亲算（awk 独立复算，非转抄）：ULP(100m)=2^-17=7.62939453125e-6、ULP(1km)=2^-14=6.103515625e-5、ULP(10km)=2^-10=9.765625e-4、ULP(100km)=2^-7=7.8125e-3——与 JSON `ulpReference` **逐位一致**；double ULP@1e7=2^-29 ✓；ULP(4km)=2^-11=0.488mm、ULP(2.4km)=2^-12=0.244mm ✓。row5：284718.6m ∈ [2^18,2^19) → ULP=31.25mm、半 ULP=15.625mm；devMax=7.8109mm ≤ 7.8125mm（[2^17,2^18) binade 半 ULP）≤ 15.625mm ≤ §9 参照 15.6mm——三层界亲算全成立（devMax 顶点幅值未单独落索引，但其值贴 7.8125mm 半 ULP 界且 ≤ 一切口径上界，归因成立）。row7：2.41026µm/8.84198µm/0.333mm ≤ 0.488mm ✓，余量亲算 202.6×/55.2×/1.4682×（报告 202×/55×/1.47× ✓）；B.trans 角偏差 2.28e-5~5.53e-5 px ✓。row10：5.8232e-5 m ≤ 半 ULP(1055.51)=6.1035e-5 ✓ ≤ 0.244mm ✓ |
| **P4 naive 对照** | **PASS**（附一处报告措辞勘误，见下） | naive/ULP(D) 亲算 = 5.316e4/3685/255.2/6.824（= 表内括号值 5.32e4/3.69e3/255/6.82）；真实 naive/A.correct 比率 = **1.683e5×/2.544e4×/749×/38.85×**。jitter@100km：JitterAudit 为确定性纯函数（无随机源），probe 与 `test_planet.cpp:29` 同函数同参数 `JitterAudit(100000.0, 1.0, 10.37)`，实测 2.8125e-3 m ∈ doctest 断言域 (0.001, 7.8125e-3]，steps=100000 ✓ |
| **P5 ABI 链** | **PASS** | 读码：probe `LoadLibraryA("zrender.dll")`+8 个 GetProcAddress+ApiVersion==3 门禁（`Native/zplugin/zplugin.cpp:36` `return 3u`）；`TestGenerateChunkVertsForUnitTests`（`Native/zrhi/d3d12/zrhi_d3d12_unity_target.cpp:2015`）为外部链接薄桥，:2023 直转 `planet_detail::GenerateChunkVerts`（:780），与 ChunkCreate 本体（:1727）**同一函数零复制零漂移**；B-PLANET-08 日志 §1 有同策声明（"顶点来源=生产函数本体，零复刻漂移…经薄桥转发"，其 VB 缝宽 max 15.930mm/240 对 = row6 归属数字核实一致）。亲跑：apiVersion=3、SetCameraOrigin/GetCameraOrigin round-trip **4/4 逐位（memcmp）maxdiff=0**、`gridLocalBitwiseAll` 4096/4096 逐位、锚点 local≡0、裙边 256/256 容差内、账目 create=1/destroy=1/live=0 恒等式成立。静态钩子上下文与 dll 实例相互独立的口径边界在探针注释与报告 §5.2 如实声明 |
| **P6 构建** | **PASS** | 验收代理独占构建链亲跑 `Tools/build_native.ps1`：**ALL GREEN**（configure + Release /W4 /WX 零编译警告 + doctest + dll 部署 4/4 步）；zrender_test.exe **380 用例 / 823,621 断言 / 0 failed / 0 skipped**——与 B-PLANET-08/09 基线 380/823,621 完全一致（探针零侵入）。log 中仅有的 WARNING/FAILED 行为 Vulkan barrier 截断测试与负路径用例的**运行期输出**（B-PLANET-09 验收同款定性），非编译警告。`abi_precision_probe.exe` 亲跑构建产物存在（08:55 重建）；CMakeLists 全文仅 `add_test` 一条（zrender_test）——探针不进 ctest ✓ |
| **P7 规范** | **PASS** | 新增文件亲验：UTF-8 无 BOM（首 3 字节 od：cpp=`2f 2f 20`、md=`23 20 54`、JSON=`7b 0a 20`）、全文纯 LF（od 全文 0 个 0x0D；早前 grep CR 计数系 MSYS grep 文本模式伪差，以 od 为准）、`.gitattributes` eol=lf；本日志四节结构齐（简报/施工报告/验收清单/本验收结论）；README 登记行更新 + 探针专节含**对端跑法**（免仓库拷 exe+dll+VC 运行时、out 落 exe 同目录、对端补验回填流程） |

### 验收附注（给主控的两处报告文字勘误——数据本体均正确，达标判定不受影响）

1. **"比 A.correct 差 6.82~53200 倍"系标注口径串用**（施工报告 §2.2 与 README 登记行同措辞；验收清单 P4 括号继承）：6.82~53200 实为 **naive 误差折合 ULP(D) 的倍数**（JSON `naivePlanetFrameUlpUnits`），不是 naive/A.correct 比率；真实的 naive/correct 比率为 **38.85×（100km）~1.683e5×（100m）**。两套数字都在 JSON 中且互洽，"naive 在所有距离灾难性更差"的结论方向不变，仅倍数引用错位。
2. **B.chunkMax 角偏差最大值应为 1.3095e-4 px（D=100m 行）**，报告 §2.5/§3-row7 写 "≤1.15e-4 px"（1.15e-4 是 1km 行值 1.15266e-4）。四行亲算：1.3095e-4 / 1.1527e-4 / 8.343e-5 / 1.1464e-4 px——同为亚像素，§9 row7 "角偏差亚毫像素" 结论不变。
3. 备查：施工报告 §2.2 声称"naive 幅值帽 ULP(6.37e6)=0.5m"——亲算 6.371e6 ∈ [2^22,2^23) → ULP=2^-1=0.5m ✓（四点 naive 0.053~0.406m 均在帽内）。

### 验收 MISSING

无。验收清单七项证据全部取得（构建链、探针两次亲跑、JSON 逐位比对、ULP 独立复算、源码核对均在验收代理本机完成）。
