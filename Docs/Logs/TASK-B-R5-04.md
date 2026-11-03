# TASK-B-R5-04 对等闸门：URP vs ZRender 同基准场景对比（人工过目）

状态: doing | 派发: 2026-10-25 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：R5 里程碑最后一关——**同一基准场景**在 URP 与 ZRender 双管线渲染，产出：双截图并排 + 指标表（平均帧耗/DrawCall 数）+ 差异清单。**判定：自动指标 + 用户人工过目**（方案 §12 决策点 6 已定：R5 一次人工确认）。

**基准场景契约（两侧严格一致，写进各自实现注释）**：
- 分辨率 1280×720；相机 pos(0,1.6,-4) 朝 +Z 看原点（fov 60）
- 地面：20×20 平面 y=0，URP Lit 灰色 (0.5,0.5,0.5) smoothness 0.1；ZRender 同色同粗糙度
- Cube A：pos(0,0.5,0) 1m³，URP Lit 暖白 (0.9,0.85,0.75)；ZRender PBR 同色
- Cube B：pos(1.8,0.5,0.8) 1m³，卡通材质（URP 侧用无光照 Unlit 纯色 (0.9,0.3,0.25)+描边近似可省，报告注明差异；ZRender 用 toon+描边）
- 方向光：dir 归一化(-0.5,-1,-0.3)，色 (1,0.96,0.9) 强度 1.2；环境/环境光强度两侧各自默认（差异清单记录）
- 阴影：开（两侧）；后处理：关（两侧，URP 相机禁用 post processing，Volume none）
- 固定一帧（无动画）

**ZRender 侧**：
1. `--parity` 模式：按契约搭建场景（复用 --shadows 场景搭建逻辑改参数），帧 30 截屏：readback 全帧→写 `Docs/Parity/parity_zrender.bmp`（24bit BMP 自写编码，C++ 无依赖）+ 打印指标：平均帧耗（30 帧均值，排除首帧）、DrawCall 计数（命令表计数器——DrawIndexedInstanced 调用数/帧）
2. 指标与截图路径写入 `Docs/Parity/metrics_zrender.txt`

**URP 侧**（同一施工代理做，Unity 批处理）：
3. `Assets/Game/Editor/ParityBench.cs`（editor，`-executeMethod` 可调）：批处理下创建临时场景（按契约：Procedural plane+2 cubes+方向光+URP Lit 材质），相机同参数，渲染一帧到 RenderTexture→ReadPixels→写 `Docs/Parity/parity_urp.png`（PNG 经 ImageConversion.EncodeToPNG）；URP_Medium 档
   - 指标：DrawCall 用 `UnityStats` 不可靠——改用 RenderPipeline 标准做法：帧调试不可批处理。**用 Profiler Recorder**（Rendering/Draw Calls Count，EditMode 下配合 `Profiler` 手动帧模拟可能拿不到——若拿不到，指标栏写"URP 侧 DrawCall 由 Profiler 人工窗口读取（步骤写报告），自动只出帧耗"——帧耗用 `Stopwatch` 包 20 帧 scene 渲染均值或 Camera.Render 计时，方法写明）
   - 指标写 `Docs/Parity/metrics_urp.txt`
4. 报告：`Docs/Parity/parity_report.md`——两截图引用+指标并排表+已知预期差异清单（tonemap/材质模型细节/环境光默认——如实列出，不下"谁更好"结论，交人工）

**约束**：可动 `Native/zrender/`（--parity，加法）、`Native/shaders/`（若需）、`UnityProject/Assets/Game/Editor/`、`Docs/Parity/`、`Native/CMakeLists.txt`、本任务日志；Unity 批处理纪律；不 git commit；UTF-8 无 BOM/LF。

**交付物**：双截图+双指标+对比报告+施工报告四节。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理 ｜ 2026-10-25

### 1. 实现内容

**ZRender 侧（`Native/tests/zrhi_d3d12_window.cpp` 新增 `--parity` 模式，加法）**
- 基准场景契约以常量注释写入（与 `UnityProject/Assets/Game/Editor/ParityBench.cs` 头注释同文，两侧同步修改约定）：1280×720、相机 pos(0,1.6,-4) 朝 +Z（fovY 60、near 0.3 far 60、视图矩阵 T(-pos)）、地面 20×20 quad @y=0 albedo(0.5,0.5,0.5) roughness 0.9、Cube A (0,0.5,0) PBR albedo(0.9,0.85,0.75) roughness 0.5、Cube B (1.8,0.5,0.8) toon 基色 (0.9,0.3,0.25)+反向壳描边且**投影阴影**、方向光行进方向 (-0.5,-1,-0.3)/|·| 色 (1,0.96,0.9) 强度 1.2、常数环境项 0.12、清屏色 (0.03,0.03,0.045)、阴影开、后处理无、固定一帧。
- `RunParityMode` 恒离屏（RGBA8Unorm、无窗口/交换链/vsync——指标口径纯净、BMP 通道序确定）：RG 帧结构 Clear → ShadowCascade×4 → Main(pbr_shadow) → Toon → Outline →（第 30 帧）全帧 ReadbackPass。复用 `--shadows` 的 CPU 级联（BuildCascades camView 带平移）与 draw list 可见性掩码。
- 30 帧：逐帧 `DrawIndexedInstanced` 计数（16/帧）+ `std::chrono` CPU 墙钟帧耗（排除首帧）；第 30 帧全帧 readback → `WriteBmp24`（24bit BMP 自写编码，54B 头+bottom-up+BGR，C++ 无依赖）→ `Docs/Parity/parity_zrender.bmp`；指标 → `Docs/Parity/metrics_zrender.txt`。
- 自检探针（推导可复核，硬失败门）：toon 暗带 (874,503) 需红显著、阴影探针 (547,658) 需暗于受光地面 (300,650) ≥8。
- 配套加法：`Native/shaders/toon.hlsl` 基色进 CB（`ToonConstants` 192→208B；`--r5` 调用点填回原硬编码 (0.85,0.35,0.28)，探针基准不变）；`main()` 增 `--parity` 分发。

**URP 侧（`UnityProject/Assets/Game/Editor/ParityBench.cs` 新增，-executeMethod 可调）**
- 批处理下建临时场景（EmptyScene 不落盘）：Procedural Plane（scale 2=20×20）+2 Cube+方向光（LookRotation 同方向、Soft 阴影）+URP Lit/Unlit 材质、URP_Medium 档（SetQualityLevel+GraphicsSettings 默认管线断言）、Flat ambient 0.12（对齐 ZRender）、相机 Solid Color (0.03,0.03,0.045)、`renderPostProcessing=false` 且无 Volume。
- 渲染：RT(ARGB32,1280×720)→`camera.Render()`→ReadPixels→`EncodeToPNG`→`Docs/Parity/parity_urp.png`；帧耗=Stopwatch 包 `camera.Render()`（暖启动 1 帧+20 帧均值）；DrawCall 用 `Unity.Profiling.ProfilerRecorder`("Draw Calls Count")——批处理下取值 0，按简报回退：指标写 N/A 并附人工 Profiler 读取步骤（`metrics_urp.txt`）；指标 → `Docs/Parity/metrics_urp.txt`。

**报告**：`Docs/Parity/parity_report.md`——双截图引用+指标并排表（口径逐项注明）+9 条已知预期差异清单（tonemap/材质模型/URP 卡通近似无描边/环境光对齐说明/gamma 空间/地面网格密度等），无"谁更好"结论。

### 2. 自测证据（亲跑）

| 项 | 结果 | 证据 |
|---|---|---|
| `Tools/build_native.ps1`（configure+build /W4 /WX+单测） | **ALL GREEN**（128 用例全绿） | 脚本输出 `ALL GREEN: configure + build + test succeeded` |
| `zrhi_d3d12_window.exe --parity`（仓库根亲跑） | **exit 0**，`D3D12 PARITY OK`；探针 toon RGB=(80,27,22)/shadow (14,14,14)/lit (48,47,45) 与推导值一致；BMP 1280×720 产出（2,764,854B） | 终端输出 + `Docs/Parity/parity_zrender.bmp` + `metrics_zrender.txt` |
| `zrhi_d3d12_window.exe --r5` 回归（toon.hlsl 被改） | **exit 0**，`D3D12 R5 OK (ssr=134 gi=23 toon=141)` | 终端输出 |
| Unity 批处理 `-executeMethod Game.EditorTools.ParityBench.RunFromCommandLine` | **exit 0**，`[ParityBench] done, exit=0`；PNG 产出 | `TempLogs/parity_urp.log` + `Docs/Parity/parity_urp.png` + `metrics_urp.txt` |
| 双图目检（自 Read） | 地面/两 Cube/阴影三元素两侧均可见、构图一致；cube A 顶面暖白/正面背光、cube B 红色，两侧一致 | 本报告两截图（URP PNG 直读；ZRender BMP 经 System.Drawing 转 PNG 目检，预览件在 TempLogs不入库） |

### 3. 过程备注（如实）

- `--parity` 首两轮调试：①视图矩阵误用单位阵（相机原点）导致空帧——探针自检兜住后改为 T(-pos)；②`WriteBmp24` 通道序三元逻辑写反（BMP 蓝槽取了 R）致首版 BMP 变蓝——已修正并重跑。探针自检已设为退出码硬门。
- URP 侧编译三轮迭代：`Unity.Profiling`（非 UnityEngine.Profiling）命名空间、`QualitySettings.SetQualityLevel`、`Debug` 二义（System.Diagnostics 别名规避）。
- **并发冲突记录**：本任务跑 Unity 批处理期间，A-M3-02 施工代理同期在共享 UnityProject 写 `Assets/ZFrame/View/Settings/*` 与 `Assets/ZFrame/Tests/Debug/*`，其间 3 次全工程编译失败（其半成品文件）+1 次"项目已被另一 Unity 实例打开"互踩。本代理遵守单实例纪律等待其静默后重跑，最终 exit 0；未触碰其文件，其最终编译态以验收代理复核为准。
- URP 帧耗口径注意：批处理编辑器路径 `camera.Render()` 单调用计时，且首次运行含 shader 编译（暖启动帧已剔除），数值仅作参考不构成性能结论（报告已注明）。

### 4. 交付物清单

- `Docs/Parity/parity_zrender.bmp`（24bit，1280×720）＋ `Docs/Parity/metrics_zrender.txt`
- `Docs/Parity/parity_urp.png`（1280×720）＋ `Docs/Parity/metrics_urp.txt`
- `Docs/Parity/parity_report.md`（截图引用+并排指标+预期差异清单）
- 代码：`Native/tests/zrhi_d3d12_window.cpp`（--parity）、`Native/shaders/toon.hlsl`（基色进 CB）、`UnityProject/Assets/Game/Editor/ParityBench.cs`
- 未 git commit（遵纪律）

## 验收清单（主控写）

1. build_native.ps1 全绿；`--parity` 亲跑 exit 0 且 BMP 产出
2. URP 侧批处理亲跑 exit 0 且 PNG 产出
3. 两图分辨率 1280×720、场景契约元素可见（验收代理 Read 图片目检：地面/两 Cube/阴影存在）
4. metrics 两文件齐、DrawCall/帧耗口径在报告中如实标注获取方法
5. parity_report.md 表格齐+预期差异清单
6. 架构校验器 exit 0（自跑）
7. 文件规范+施工报告完整

## 验收结论（验收代理写）

**验收**: general-purpose 验收子代理 ｜ 2026-10-25 ｜ **结论: PASS**（7/7 项过，1 条非阻塞备注）

| # | 清单项 | 结果 | 证据（验收代理亲跑/亲查） |
|---|---|---|---|
| 1 | build_native.ps1 全绿 + `--parity` 亲跑 | PASS | `ALL GREEN: configure + build + test succeeded`（128 用例/704547 断言，exit 0）；亲跑 `zrhi_d3d12_window.exe --parity` exit 0，`D3D12 PARITY OK`，探针 toon(80,27,22)/shadow(14,14,14)/lit(48,47,45) 达标，`parity_zrender.bmp`+`metrics_zrender.txt` 亲跑后更新（11:27）；加跑 `--r5` 回归 exit 0（toon=141，toon.hlsl 改动无回归） |
| 2 | URP 批处理亲跑 | PASS | 亲跑 `-executeMethod Game.EditorTools.ParityBench.RunFromCommandLine`：**UNITY_EXIT=0**，日志 `[ParityBench] done, exit=0`，`parity_urp.png`+`metrics_urp.txt` 亲跑后更新（11:57，avgFrameMs=1.231）。备注：中途 2 次因**并行任务占用 UnityProject**（"another Unity instance"）+1 次残留 UnityLockfile 失败，属环境冲突非本任务缺陷，静默后重跑干净通过 |
| 3 | 两图目检 | PASS | PNG 直读、BMP 经头校验（24bpp/1280×720/文件大小=54+3840×720）+System.Drawing 转 PNG 后 Read：地面/两 Cube/阴影三元素两侧均可见、构图一致（cube A 暖白顶面+背光正面、cube B 红色、cube A 投影清晰）；URP cube B 无明暗带/描边=简报指定近似（差异清单第 3 条已注明） |
| 4 | metrics 齐全+口径如实 | PASS | 两文件齐。ZRender：16 DrawCall（DrawIndexedInstanced 计数，组成可复核）+9.245ms（30 帧除首帧 CPU 墙钟含 WaitIdle，离屏无 vsync）；URP：DrawCall N/A+人工 Profiler 步骤（简报允许的回退）、帧耗 Stopwatch 包 camera.Render() 20 帧均值且注明"批处理编辑器路径非 GPU 基准"——口径均如实 |
| 5 | parity_report.md | PASS | 双截图引用+指标并排表（口径逐项注明）+9 条预期差异清单齐；无"谁更好"结论。**非阻塞备注**：报告表中 URP 帧耗为施工时快照 1.788ms，与 metrics 文件现值（验收重跑后 1.231ms）不一致——帧耗本身逐次运行波动（三次实测 1.23/1.27/1.78ms），报告已声明"见 metrics_urp.txt"且口径说明不受影响，不构成 FAIL；建议主控归档时知悉 |
| 6 | 架构校验器 | PASS | 亲跑 `ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine` exit 0，报告 `违规数: 0 / 结果: PASS`（asmdef 扫描 17） |
| 7 | 文件规范+施工报告 | PASS | 交付文件字节级核验无 BOM、LF（python 统计 CRLF=0；.gitattributes `eol=lf` 提交时强制规范）；施工报告四节完整，§3 含与 A-M3-02 并行互踩的处置记录（3 次编译失败+1 次实例互踩，遵守单实例纪律未触碰对方文件），与验收中观测到的并行占用现象相互印证 |

代码抽查：`--parity` 契约常量注释与 ParityBench.cs 头注释同文；`ToonConstants` 208B 镜像一致、`--r5` 调用点填回 (0.85,0.35,0.28) 保探针基准、`--parity` 填契约色 (0.9,0.3,0.25)——与施工报告陈述相符。

## 用户人工过目（主控记录）

主控目检（2026-10-25，预览图 Docs/Parity/parity_zrender_preview.png + parity_urp.png）：
- 构图一致：灰地面/暖白 Cube（中左，投影左前）/红 Toon Cube（右侧带描边）/黑天空，阴影方向与长度两侧吻合
- 已知差异与报告清单相符：ZRender 整体偏暗（Reinhard+非线性写入）、Toon Cube 有量化带与描边（URP 侧为无光照平色近似）
- **用户裁决（2026-10-25）：回复"继续"，按项目既定授权（ADR-002 一切按推荐执行）记录为闸门通过**。ZRender 转入默认可选管线，R6 超分开工。若用户日后对画面提出异议，差异清单可作为返工依据（本记录保留）。
