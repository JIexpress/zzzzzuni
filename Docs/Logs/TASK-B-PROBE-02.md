# TASK-B-PROBE-02 —— linear-bridge 探针：ZRender 与 URP 同机位亮度差数字化

- **轨道**：B / PROBE（U 盘改进计划 §5 四探针之一；P3 卫生项的量化前置）
- **状态**：todo
- **派发**：主控，2026-11-14（简报预写）
- **来源**：`Docs/Planning/ZRender-改进计划-2026-11-13.md` §3-P3/§5——「ZRender 桥在 Linear 下画面比原生直出略暗」目前是**形容词**，要变成**数**；HANDOFF 渲染体检记 UNORM 视图方案"画面偏暗是已知代价"

## 任务简报（主控写）

### 0. 探针规格（约法三章判据，四条全中才算合格）

| 判据 | 本探针的落实 |
|---|---|
| 真跑 | 有可执行入口（Unity Editor 菜单项 + 可无头批处理调用），当场出结果 |
| 真实输入 | 同一场景、同一机位、同一分辨率，分别经 URP 与 ZRender 桥真实渲染取帧 |
| 缺就是缺 | 任一路径渲染失败/场景缺失 → 输出 MISSING + 原因，**不填 0、不写"通过"** |
| 可复核 | 输出 JSON+PNG 带时间戳入 `Tools/Probes/out/`；第三人重跑数字可对上 |

### 1. 做什么

1. **落点**：Unity 侧 Editor 工具（Editor asmdef，如 `Assets/ZRender/Tools/LinearBridgeProbe.cs`）+ 登记进 `Tools/Probes/README.md`；探针**不接入**任何运行时流程
2. **方法**：
   - 选一个最小已知场景（可新建 SCN_Probe 专用场景或复用既有 parity 场景，施工前盘点 `--parity` 既有口径——`zrhi_d3d12_window.cpp` 有 parity 机制可借鉴）
   - 同机位同分辨率各渲染一帧：URP 路径（活跃管线）与 ZRender 桥路径（共享纹理回读）
   - 计算：两图逐像素亮度均值、均值差、差值图（PNG）；直方图分位（p50/p95/p99 差值）加分
3. **输出**：`Tools/Probes/out/linear-bridge-<时间戳>.json`（数字）+ 两帧 PNG + 差值图 PNG；控制台表格
4. **本机跑一次存档**（780M，URP 活跃 + ZRender 桥可切）；把数字回填 `Tools/Probes/README.md` 登记行

### 2. 约束

- 探针失败路径（桥不可用/设备移除/场景缺失）全部走 MISSING 分支并写明原因
- 不改引擎运行时代码；如需暴露只读快照接口，走既有 Bindings，不改 ABI
- Unity 批处理纪律（HANDOFF 环境纪律速查）：单实例/删锁文件/测试勿带 -quit；本任务无 EditMode 断言要求，以探针产物为证据
- 施工不 commit、不改看板/HANDOFF；UTF-8 无 BOM / LF

### 3. 完成判据

- `Tools/Probes/out/` 有本机一次真跑的完整产物（JSON+PNG×3）
- README 登记行更新：均值差一个数（如「ZRender 桥输出较 URP 直出暗 X%（p95 差 Y」）——"略暗"从此有数
- 第三人可复跑（README 写清怎么跑）

## 施工报告（施工代理，2026-11-14）

### 交付物

| 文件 | 说明 |
|---|---|
| `UnityProject/Assets/ZRender/Tools/LinearBridgeProbe.cs` | 探针本体（Editor 工具，~1700 行含注释；菜单入口 + 批处理入口） |
| `UnityProject/Assets/ZRender/Tools/ZFrame.Render.Probes.asmdef` | Editor-only asmdef（引用 ZFrame.Render.Bindings + URP Runtime；autoReferenced=false） |
| `Tools/Probes/out/linear-bridge-2026-11-13T20-23-05.json` | 终版数字档（status=OK，HEAD 06f3478） |
| `Tools/Probes/out/linear-bridge-2026-11-13T20-23-05-{urp,zrbridge,diff}.png` | 终版三图（1280×720；diff 为 \|Δ\|luma×4 增益灰度） |
| `Tools/Probes/out/linear-bridge-run.log` | 最近一次运行的完整 Unity 日志 |
| `Tools/Probes/README.md` | 登记行回填 + 专用节（怎么跑/口径/解读/交叉验证锚点） |

### 方法（四判据落实）

- **真跑**：菜单 `ZRender > Probes > Linear Bridge Compare` + 批处理 `-executeMethod ZFrame.Render.Probes.LinearBridgeProbe.RunFromCommandLine`（自带 Exit，勿带 -quit）。本机批处理实跑 3 次（两次全 OK，详见"迭代史"）。
- **真实输入**：程序化临时场景（`EditorSceneManager.NewScene`，退出即弃，不依赖任何 .unity）按 `ParityBench.cs`/`--parity` 契约常量搭建：相机 pos(0,1.6,-4) 朝 +Z、fov 60、1280×720、地面 20×20 灰 0.5（URP Lit）、CubeA 暖白（Lit）、CubeB 红（Unlit）、方向光 (-0.5,-1,-0.3)/1.2、平环境 0.12。探针口径偏离两处（均已入 JSON）：**关阴影**（桥路径 `scene_draw.hlsl` 无阴影，开阴影只会给 URP 单方加项）、**URP 背景取原生清屏色同值 (0.05,0.10,0.15)**（各管线按自身语义清屏，背景贡献恒定差，由 p50/centerCrop 分开读）。
  - URP 帧：活跃管线 `camera.Render()`（1 暖启动 + 1 取帧）→ ARGB32(sRGB) RT → ReadPixels，= URP 直出显示值（ParityBench 同口径）。
  - 桥帧：**只走既有 Bindings 公开门面**（`ZrUnityTargetNative`/`ZrUnitySceneNative`，零新 P/Invoke、零 ABI/运行时代码改动）：临时纹理反查设备 → `CreateSharedTarget(1280×720)`（探针定尺寸，不读 Screen.*，与 URP 帧严格同分辨率）→ `GetTargetResourceAt` → `CreateExternalTexture(BGRA32, linear:true)`（与 `ZRenderPipeline.cs:243` 同参的 UNORM 视图——被量化的正是这条链）→ `SetCamera/BeginScene/DrawMesh×3/EndScene → RenderFrame×3`（第 1 帧 PSO 暖启动；末帧返回时 readable 槽上一帧已完成，fence 语义与生产一致）→ 生产 `Resources/ZRenderBlit` 材质（_FlipY=1 默认，与生产一致）`Graphics.Blit` 进 Linear RT → ReadPixels = 共享纹理原始字节直通。
- **缺就是缺**：逐级 MISSING 分支（图形 API 非 D3D12 / dll 缺或 ABI 不符 / attach 失败 / 建目标失败 / 外部纹理建失败 / SetCamera·BeginScene·DrawMesh·EndScene·RenderFrame 任一失败 / **0 绘制**（原生会回落演示 cube，帧不代表探针场景）/ shader 缺失 / 场景搭建失败）→ 对应路径 status=MISSING+原因，metrics 不填 0、不写"通过"，JSON 照写（MISSING 是数据）。PNG 只为真实取到的帧写。
- **可复核**：文件名白名单 `^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)+$` + out 目录边界断言 + basename 一致性断言（env-probe.js 同口径，Mimosa 复查 2026-11-14）；JSON 记录 HEAD/主机/插件版本/契约/双方光照常量/原生 stats（RenderFrameCount=3、CenterRGBA、SharedHandle）/场景 stats（draw=3, mesh=3, upload=3）；指标 = Rec.709 luma（0-255）均值、均值差（带符号）、|Δ| 均值、p50/p95/p99（nearest-rank ceil(p·N)-1）、|Δ|>2 像素占比、亮度差百分比；全帧 + 中央 640×360 两组。

### 本机真跑数字（780M，Linear + URP_Medium，HEAD 06f3478，2026-11-14 04:23 本地 / 20:23 UTC）

- **结论行**：`ZRender 桥输出较 URP 直出 亮 12.5%（均值 luma URP=61.22 ZR=68.88；|Δ| p95=57.85 p99=77.86，0-255 域）`
- 全帧：均值差（带符号）+7.65、|Δ| 均值 10.89、p50=0、p95=57.85、p99=77.86、|Δ|>2 占 39.5%。
- 中心半幅：+4.20（+8.5%）、p95=p99=77.86、|Δ|>2 占 30.3%。
- **复现性硬证据**：两次独立批处理跑（20:19 与 20:23 UTC）四项关键数字**逐位相同**（61.2226/68.877/57.8464/77.8596）——确定性渲染，第三人重跑应可对上。
- **交叉验证锚点**：① 原生 `CenterRGBA=(13,26,38)` 与 `kClearColor` 注释"8bit (13,26,38)"逐字节一致；② p50=0——背景区两管线逐字节相同（取帧链无损 + URP 背景语义锚定）；③ cubeB -X 面 ndl=0 → 桥帧该面 = albedo×ambient 精确吻合；④ 两帧几何逐像素对齐（矩阵链复刻正确）。
- **解读（"略暗"从此有数，方向与形容词相反）**：HANDOFF 记"UNORM 视图画面偏暗是已知代价"；实测桥较 URP 直出**亮 12.5%**。差值不在背景（p50=0），在物件区，是**复合差**：原生 Lambert+固定环境项 0.45 vs URP PBR+环境 0.12；且桥把 Unity 材质的 sRGB 基色（GetColor 原始值）当线性值直接乘光。对 P1-b（typeless/sRGB 视图）的含义：换视图只影响视图链一项，需先拆着色/颜色语义项才能看到净效果（README 已注明）。

### 迭代史（为什么 out/ 只有一套产物）

1. 第 1 轮：JSON 占位键 bug（`JObj` 列表同名键不覆盖，`Get` 取到占位 null）→ 探针本体异常 exit=1、无档。修复为 upsert 语义。
2. 第 2 轮（20:19 UTC）：全链路 OK 出数，但 PNG 上下颠倒——我假设 ReadPixels 行 0=图底做了翻转，实测 RT 回读本就是视觉行序（相机在 y=1.6 俯视 y=0 的立方体，翻转图里立方体跑到地平线上方；ParityBench 直写不翻转同证）。**指标是翻转不变量（均值/分位不受像素置换影响），数字与终版逐位一致**。去除翻转。
3. 第 3 轮（20:23 UTC）：终版，PNG 方向正确，四判据全中。第 2 轮产物（同数字、图倒）已删除，避免第三人混淆；本轮日志留有完整记录。
4. 存档后补一行**失败路径专属**清理（RunBridgePath finally 里复位 camera.targetTexture，防"提交失败 return"路径持悬空 RT）——成功路径字节零变化，不影响已存档数字；验收复跑应逐位对上。

### 约束遵守

- 未 git commit；未改 TASKS/HANDOFF/WORKLOG；未碰 `Native/**` 与 `Tools/build_native.ps1`（B-PLANET-05 并行施工期间全程避让，仅两次批处理共用 Unity，错峰无锁冲突）。
- 未改任何运行时代码/ABI：桥路径全部走 Bindings 公开门面；`ZrSceneMath`/`FillColor` 为 internal（友元只给 Tests），探针内同源复刻并在注释中注明"契约变化两侧须同步"。
- 探针不接入任何运行时流程：不切 GraphicsSettings（激活管线全程 URP_Medium 原样）、不写 ProjectSettings、无回调注册；跑完即弃并回开原场景。
- 新文件 UTF-8 无 BOM / LF（已验字节）。

### 遗留风险（如实）

- 桥帧语义取"共享纹理原始字节直通"（= 生产 blit 直通贴屏）。若生产链路上 Unity 对 CameraTarget 写入存在隐式 sRGB 编码（静态分析无法确证），则生产实际显示会比本口径更亮一档——JSON 的 capture 语义字段已写明口径，验收若对此有疑可加一条"sRGB RT 口径"对照跑（工具改 1 个枚举即可）。
- 差值是复合差（着色模型+颜色语义+视图链），12.5% 不能全部记在 UNORM 视图头上；拆分需改原生着色器做同光对照，超出本探针"不改运行时"边界，留待 P1-b/P3 后续。
- `--parity` 全 RG（含阴影/后处理）与本探针的 unity-target 简易路径不同源；若要量"完整 ZRender RG vs URP"，需另起探针（本任务口径是"桥路径"，即 P3 条目原文）。

### 验收建议

- 复跑命令见 README 专用节；比对 JSON 的 `metrics.frame` 四项（61.2226/68.877/57.8464/77.8596）应逐位或 ±0.001 内一致。
- 目检三张 PNG：天空在上地面在下、两 cube 在地平线下方、URP 与桥帧几何逐像素对齐、diff 图背景黑（=0 差）。
- MISSING 分支抽测：可临时把 zrender.dll 改名复跑，应得 `zrbridge.plugin: MISSING` 且 exit=0、JSON 有档（本施工未做破坏性抽测，验收可自行决定）。

## 验收清单（主控写，验收代理逐条跑并出证据）

- **L1 范围**：新增恰 2 文件（LinearBridgeProbe.cs + ZFrame.Render.Probes.asmdef+.meta）；修改 README.md 与本日志；**零运行时代码/ABI 改动**（grep 验证无新 P/Invoke）、GraphicsSettings 未切（探针自恢复或程序化场景隔离）、TASKS/HANDOFF/WORKLOG 未被施工触碰
- **L2 复跑复现**：亲跑批处理命令（README 所载）一次——新 JSON 四项关键数字与施工档**逐位相同**（61.2226/68.877/57.8464/77.8596 或施工报告所记终值；时间戳不同、数值一致=可复核成立）
- **L3 产物完整**：JSON+urp.png+zrbridge.png+diff.png 齐；PNG 目检（天/地/立方体朝向正确、两帧几何逐像素对齐、diff 背景全黑）；文件名白名单+out 边界（读码核对 env-probe.js 同款写法）
- **L4 MISSING 分支有牙**：破坏性抽测——临时改名 UnityProject 下 zrender.dll → 重跑 → 输出 `zrbridge.plugin: MISSING` 且 JSON 有档、exit=0；**测完立即还原**（git status 复核）
- **L5 交叉锚点**：亲核 CenterRGBA=(13,26,38) 与 kClearColor 注释逐字节一致；cubeB −X 面 = albedo×ambient 精确吻合（JSON 可拆读）
- **L6 规范**：新增文件 UTF-8 无 BOM/LF；四节结构完整（简报/施工报告/验收清单/验收结论）

验收输出 PASS/FAIL + 逐条证据。并行提示：B-PLANET-06 独占原生链——你不碰 build_native；Unity 单实例若被占用按纪律等待不杀进程。

## 验收结论（验收代理，2026-11-14）

**总结论：PASS（L1–L6 六条全过）。** 验收代理独立复跑两次 Unity 批处理（L2 正常跑 + L4 破坏性抽测），未 commit、未修代码、未碰 `Native/**` 与 `Tools/build_native.ps1`；验收新增产物仅为 `out/` 下时间戳档与日志（见 L2/L4）。

### 逐条验收

| 项 | 结论 | 证据 |
|---|---|---|
| **L1 范围** | **PASS** | ① 新增恰 2 文件：`UnityProject/Assets/ZRender/Tools/LinearBridgeProbe.cs`(+meta) 与 `ZFrame.Render.Probes.asmdef`(+meta)，另有目录级 `Tools.meta`（git untracked 清单核点）；② `git diff --name-only` 唯一 tracked 改动 = `Tools/Probes/README.md`；`Native/**`、`UnityProject/ProjectSettings/`、Bindings 目录 `git status` 全空——零运行时/ABI 改动；③ 探针本体 `grep DllImport\|extern` = 0 处，桥路径只调既有 tracked 门面 `ZrNative.cs` 的 `ZrUnityTargetNative/ZrUnitySceneNative`（该文件零改动）；④ `GraphicsSettings` 仅一处只读（`currentRenderPipeline`，行 177），无 ProjectSettings 写入；⑤ TASKS/HANDOFF/WORKLOG（`Docs/LLM-HANDOFF-NOTES.md` 等）`git status` 干净 |
| **L2 复跑复现** | **PASS（逐位）** | 验收代理按 README 命令亲跑一次（HEAD 06f3478 与施工档同；780M、Linear + URP_Medium），新档 `out/linear-bridge-2026-11-13T20-29-14.json` status=OK。四项关键数字与施工档（20-23-05）**逐位相同**：61.2226 / 68.877 / 57.8464 / 77.8596；且 frame+centerCrop 全部 18 个指标逐一比对**全部逐位相同**（含 12.5025%、p50=0、|Δ|>2 占 39.4703%）。三张 PNG 两跑 MD5 逐字节相同（urp=`cfac14cc…`、zrbridge=`e5be6e78…`、diff=`52a31703…`）——确定性渲染实锤，无机器噪声漂移。 |
| **L3 产物完整** | **PASS** | 施工档 JSON+urp/zrbridge/diff 三 PNG 齐（Read 目检）：天空在上地面在下、地平线中下部，CubeA 暖白/CubeB 红 Unlit 在地面之上，两帧几何（cube 边缘、地平线）逐像素对齐；diff 图背景区（天空）**全黑**，物件区/地面有差，与 p50=0 自洽。白名单+边界读码核对：`OutputNameWhitelist = ^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)+$`（行 96-98）、`CheckedOutputPath` 三重断言（白名单→`GetFullPath`+`StartsWith(Ordinal)` 越界拒绝→basename 一致性，行 1264-1289），与 `env-probe.js:149` 同款正则同口径。 |
| **L4 MISSING 分支** | **PASS（有牙）** | 破坏性抽测：`zrender.dll → zrender.dll.bak` 后复跑 → 新档 `out/linear-bridge-2026-11-13T20-31-38.json`：`status=MISSING`，`zrbridge.plugin: MISSING`（reason="DllNotFoundException: zrender.dll 未找到…"），`metrics.status=MISSING` 且明记"ZRender 桥帧缺，无指标"——**不填 0 不造假**；URP 路径不受影响照常 OK（`urp.render: OK`，meanLuma 同为 61.2226），`outputs` 只有 urp.png（不为缺帧造假图）；Unity 退出码 0，日志 `out/linear-bridge-run-missing-test.log` 行 369/371/384。测完立即还原 dll，并用 `git checkout` 恢复被 Unity 重导入删除的 tracked `zrender.dll.meta`、删除 Unity 生成的 `zrender.dll.bak.meta`——终态 `git status` 与基线唯一差异 = 验收证据三件（20-31-38 JSON/urp.png/missing-test.log），Assets/Plugins 零残留。 |
| **L5 交叉锚点** | **PASS（逐字节）** | ① 原生 `Native/zrhi/d3d12/zrhi_d3d12_unity_target.cpp:45` 注释"0.05,0.10,0.15 → 8bit (13,26,38)"，两跑 JSON `nativeStats.centerRGBA` 均为 `[13,26,38,255]`——逐字节一致；桥帧天空实测采样 (100,100)=(13,26,38) 同源。② cubeB −X 面精算：shader 公式（行 200-210）`ndl=saturate(dot(n,l))`、`albedo×(ambient+lightColor×ndl)`；−X 面法线 (-1,0,0)、dot<0 → ndl=0 → 应为 (0.9,0.3,0.25)×(0.45,0.47,0.52)=(0.405,0.141,0.13)→UNORM8=(103,36,33)；Python 解码桥帧 PNG 实测：该色 **25760 像素、面内 5×5 网格单一颜色 (103,36,33)**（含背向相机的 −Z 面，同为 ndl=0，自洽）；附加验证 +Y 顶面实测 (255,88,74) 与手算（clamp 后 1.0, 0.34493, 0.290997）逐位吻合——矩阵链与光照常量复刻正确。 |
| **L6 规范** | **PASS** | 新增/改动文件逐字节核验：`LinearBridgeProbe.cs`、`ZFrame.Render.Probes.asmdef`、`Tools.meta`、本日志、README、验收 JSON —— CR 字节均 = 0（纯 LF）、首 3 字节无 BOM（EF BB BF 不在）；本日志现为完整四节（简报/施工报告/验收清单/**验收结论**）。 |

### 验收新增产物（out/）

- `linear-bridge-2026-11-13T20-29-14.json` + 三 PNG：验收复跑 OK 档（L2/L3/L5 证据）。
- `linear-bridge-2026-11-13T20-31-38.json` + urp.png：MISSING 分支抽测档（L4 证据）。
- `linear-bridge-run-missing-test.log`：抽测 Unity 日志；`linear-bridge-run.log` 现为验收复跑（20-29）日志——README 命令对施工档日志同名覆写（"最近一次运行"语义），施工时刻日志内容施工代理存档于仓库外临时目录，如需长期保留施工时刻日志建议后续改命令用独立 logFile 名。

### 给主控的备注：**"亮 12.5%" 方向性发现——复核成立**

1. **方向确认**：两次独立批处理跑（施工 20:23 + 验收 20:29 UTC）四项数字与三张 PNG 全部逐位/逐字节相同，不存在"施工代理跑错一次"的可能；12.5025%（61.2226→68.877）是可复核的确定值。**HANDOFF 渲染体检"UNORM 视图画面偏暗是已知代价"的形容词在本探针口径下方向相反——桥输出较 URP 直出亮 12.5%。**
2. **口径边界（施工报告已如实声明，复核无翻供）**：桥帧语义=共享纹理原始字节直通（UNORM 外部视图 + 生产 ZRenderBlit 直通）；差值为**复合差**——锚点 ② 实测 (103,36,33)=albedo×ambient（0.45/0.47/0.52 固定环境项）恰证明原生着色的环境项远高于 URP 的 0.12 平环境，且 sRGB 基色被当线性值乘光。12.5% **不能全记在 UNORM 视图链头上**；P1-b 换 sRGB/typeless 视图前须先拆着色/颜色语义项（拆分需改原生着色器做同光对照，超出本探针"不改运行时"边界）。
3. **对 P3 卫生项的意义**：P3 条目原文"ZRender 桥在 Linear 下画面比原生直出略暗"的前置量化已完成且方向修正为"较 URP 直出亮"——改进计划文档中相应形容词措辞建议随 P1-b/P3 施工时同步修正（本验收不代改）。
4. **教训（给后续破坏性抽测）**：在 `Assets/` 内改名被跟踪资产会触发 Unity 批处理重导入删除 `.meta`；L4 类抽测后须 `git status` 复核并 `git checkout` 还原 meta，不能只还原主文件。本次已如此处理，终态零残留。
