# TASK-B-R9-02 —— Unity(URP/D3D12) 下 depth/MV 的可获取性与正确性

> **带小门禁**：先证明"URP 能产出可用 depth/MV 且能被原生拿到"，再谈接线。施工代理按本简报执行，
> 第 2 节写报告。**禁止 git commit**。验收由另一代理执行。

## 任务简报（主控写）

### 背景（含一处必须纠正的前提）

B-R9-01 spike 判 STOP，原因之一：FG 的 `PrepareV2` 需要深度图 + 运动矢量，而"本工程拿不到"。
当时的推断是要在"ZRender 自研 SRP 的 RenderGraph 里加 MV pass"。

**实测核对后该前提不成立**：`UnityProject/Assets/ZRender/Runtime/ZRenderPipeline.cs` 仅 27 行，
`Render()` 里只有 `foreach (camera) context.Submit()`，注释明写"v0：仅提交空的 ScriptableRenderContext……
真渲染后续任务接入"。**Unity 内真正渲染的是 URP**（A-M2-03 已接），自研 SRP 是壳。
全仓也**没有任何 motion vector 代码**（`grep MotionVector` 零命中）。

所以 depth/MV 只能从 **URP** 侧获得，不存在"往自研 pass 里加"这条路。

### 目标（两段，第一段是门禁）

**阶段 A（门禁）：证明可行且数据可用**
必须回答并给出证据：
1. Unity 2022.3 + URP 在本项目下**能否产出可用的运动矢量**？需要什么前提
   （URP asset 的 motion vector 开关？shader 是否需要 MotionVector pass？当前材质是 URP Lit 吗？）
2. **深度**能否拿到（`_CameraDepthTexture` / depth attachment），在 D3D12 下能否取得 `ID3D12Resource*`
   （`Texture.GetNativeTexturePtr()`）。
3. 数据**正确性**的初步判据：静止相机时 MV ≈ 0；相机平移时 MV 非零且方向正确；depth 单调合理（近小远大）。

**阶段 B：暴露给原生（仅阶段 A 通过后执行）**
4. 在渲染完成后把 depth/MV 的 native ptr + 尺寸 + 格式传给原生（经插件或既有 C ABI 通道）。
5. 原生侧做**有效性校验**（尺寸/格式/非空），把结论落成可断言的返回值或统计——**不做 FG 联调**
   （联调属后续任务，且依赖 B-R9-01 的 hook 部署，另议）。

### 关键风险与约束

- **URP 的 motion vector 可能需要改 `URP_*.asset`**（`supportsMotionVectors` 之类）。
  A-R9 系列已立过纪律：**不污染资产**。因此：
  - 优先找**运行时可用**的途径（public property / 已有 API），不改资产；
  - **若确实必须改资产**：先停手报告（说明为什么没有运行时途径、改了哪几个字段），由主控决定是否接受，
    **不要擅自把 `URP_*.asset` 改了就提交**。
- MV 的**坐标系与尺度**必须说明清楚：Unity 的 MV 是屏幕空间 UV 偏移还是 NDC？单位是像素还是归一化？
  FFX 侧期望随 `displayResolutionMv` / `motionVectorScale` 变化。请在报告中给出**换算公式**并说明依据
  （可参考 `Native/zupscaler/fg_ffx.h` 里的 `ZupMotionVectorScale` 口径）。
- 若阶段 A 判定"URP 在本项目产不出可用 MV"（例如材质不支持、或只能用深度反推），**如实 STOP**，
  说明卡点与替代方案（如用 depth 重投影近似 MV），不要伪造数据。

### 交付

1. 阶段 A 结论（三问 + 证据：日志/截图/读回的数值）+ 门禁 PASS/STOP
2. 阶段 B（若执行）：暴露通道的实现 + 原生侧校验 + 测试
3. 报告写第 2 节，含：改了哪些文件、是否碰资产（碰了必须单独说明并等裁决）、未验证项

### 环境纪律

- 构建/测试前 `unset http_proxy https_proxy all_proxy`（重复大小写代理变量让 MSBuild 抛「字典重复键」）
- cmake 不在 PATH：`CM="/c/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"`
- 六段用 **PowerShell 工具**（Bash 侧禁止转调 powershell），先清代理变量 + `Set-Location` 到仓库根
- 基线：native **190/710,800**、editmode **678**、playmode **6**、三校验器 exit 0
- 清理用 `mv` 归档到 `TempLogs/archive_pre_audit/`，不要 `rm -rf`
- 已知环境事实：本机 GUI Editor 加 `-force-d3d12` 会挂起（B-R9-01 实测）；批处理能跑但无交换链；
  真渲染验证可能需要 build Player 到临时目录

### 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- 不改 `FrameGovernor`/`GovernorLadder`/`GovernorTuning`；不动 Unity 安装目录
- `.cs` 无 BOM、LF；C++ `/W4 /WX` 零警告

---

## 施工报告（施工代理写）

### 门禁判定：**PASS**（阶段 A 三问全绿，阶段 B 已执行并按简报收口）

一句话结论：**URP 14 在本项目下能产出可用 MV 与深度，D3D12 下两者都能拿到 `ID3D12Resource*`，
数值与理论预测对得上（误差 < 0.1%），且全程没改一个字节的 `URP_*.asset`。**
产出方式是一条**纯运行期**路径：克隆管线资产 + 在克隆的渲染器数据上挂一个只声明
`ConfigureInput(Motion|Depth)` 的空特性——URP 14 的 MV 本来就没有"资产开关"，只认 pass 的输入声明。

证据日志：`TempLogs/r9_02/player_run_d3d12.log`（真 Player，`-force-d3d12`），
副本：`TempLogs/archive_pre_audit/r9_02_probe/player_run_d3d12.log`。

---

### 阶段 A · Q1：URP 能否产出可用运动矢量 —— **绿（有数值实证）**

**能，前提是"有一个 pass 声明要 Motion 输入"，不是资产开关。**

源码核对（`Library/PackageCache/com.unity.render-pipelines.universal@14.0.12`）：

| 事实 | 证据 |
|---|---|
| URP **14 没有** `supportsMotionVectors` 字段/属性 | `grep -rn "supportsMotionVectors"` 在包内 **零命中**（该开关属 URP 12- 与 HDRP） |
| MV 只由 `ScriptableRenderPassInput.Motion`（或 TAA）触发 | `UniversalRenderer.GetRenderPassInputs`：`inputSummary.requiresMotionVectors \|= needsMotion` |
| MV 纹理格式与尺寸 | `MotionVectorRenderPass.k_TargetFormat = R16G16_SFloat`，强制 `msaaSamples = 1` |
| MV 的相机项/物体项分别由两张 fallback 材质画 | `MotionVectorRenderPass.DrawCameraMotionVectors/DrawObjectMotionVectors` |
| **URP Lit 没有 MotionVectors pass** | `Lit.shader` 的 pass 只有 ForwardLit/ShadowCaster/GBuffer/DepthOnly/DepthNormals/Meta/Universal2D |
| 物体 MV 走 fallback（`unity_MatrixPreviousM`） | `ObjectMotionVectors.shader`：`prevPos = (unity_MotionVectorsParams.x==1) ? positionOld : position`，再用 `UNITY_PREV_MATRIX_M` |

所以"当前材质是不是 URP Lit"不影响刚性物体：**没有 MV pass 的材质会吃 fallback 材质**，
刚体平移/旋转的 MV 是正确的（下面 objMove 一栏就是实证）。**只有蒙皮/顶点动画/GPU 动画**才需要
自带 `LightMode = "MotionVectors"` 的 pass——那是 FG 落地时的待办，不是本门禁的卡点。

实证（Player，D3D12）：

```
[R9PROBE] device=Direct3D12 api=Direct3D 12 [level 12.1] reversedZ=True uvTop=True editor=False
[R9PROBE] installed: clone=URP_Medium__R9Runtime rd=URP_Medium_Renderer__R9Runtime features=1 msaa=1 assetDepth=True
[R9PROBE] [static] mv 320x180 fmt=R16G16_SFloat ptr=0x1B4201CAB00 center=(0.000000,0.000000)
[R9PROBE] pass#20 ... dtexMode=Depth, MotionVectors ... globalMv=320x180 R16G16_SFloat
```

`dtexMode=Depth, MotionVectors` 是**引擎侧确认** URP 的 `MotionVectorRenderPass.Execute` 真的跑了
（那行 `camera.depthTextureMode |= MotionVectors | Depth` 只有它在写）。

### 阶段 A · Q2：深度能否拿到 + D3D12 native ptr —— **绿**

| 对象 | 尺寸/格式 | native ptr | 备注 |
|---|---|---|---|
| `_CameraDepthTexture`（深度拷贝） | 320x180 `R32_SFloat` | 非 0（如 `0x1ED8DDE68F0`） | MSAA=1 才走拷贝路径，是**彩色**纹理，可直接采样/读回 |
| `_CameraDepthTexture`（背缓冲阶段） | 640x480 `R32_SFloat` | 非 0 | 同上 |
| `cameraDepthTargetHandle`（真实深度附件） | 320x180 `gfx=None fmt=Depth` | 非 0 | MSAA>1 时走这条（未实测 MSAA=4 下的 ptr，见"未验证项"） |

```
[R9PROBE] [static] depth(copy) 320x180 fmt=R32_SFloat ptr=0x1ED8DDE68F0 rawL=0.057171 rawR=0.027081 linearL=5.000 linearR=10.000
```

`Texture.GetNativeTexturePtr()` 在 `GraphicsDeviceType.Direct3D12` 下返回的就是 `ID3D12Resource*`；
阶段 B 把这枚指针连同尺寸/格式交给原生后**通过了非空/尺寸/格式三重体检**（见下文 Q4），
这条链路本身就是"指针是真资源"的旁证。

**注意（对本项目是硬约束）**：`URP_Medium.asset` 里 `m_MSAA: 4`。MSAA>1 时 URP 走"深度预通道 +
把深度附件直接当 `_CameraDepthTexture`"的路子，拿到的就是 depth-stencil 附件而不是 R32F 彩色拷贝。
本次探针在**运行期克隆**上把 `msaaSampleCount` 设成 1 取得拷贝路径；生产接线二选一：
① 关 MSAA（资产改动，需裁决）；② 直接用 depth 附件（ptr 有效，格式 `D32`，FFX 也吃）。

### 阶段 A · Q3：数据正确性 —— **绿（四组预测全部命中）**

理论预测（相机在 z=-10、FOV 60、物体在 z=-3 即距离 7、每帧位移 0.05 世界单位）：
`|mv.x| = 0.5 · 0.05 / (tan30° · 16:9 · 7) = 0.003480`，`|mv.y| = 0.5 · 0.05 / (tan30° · 7) = 0.006186`。

| 用例 | 实测 | 预测 | 判定 |
|---|---|---|---|
| 相机与物体全静止 | `(0.000000, 0.000000)`，全场 7×7 网格 `maxAbs=0.000000` | 0 | ✅ |
| 相机 +X 平移 0.05/帧 | `mv.x = -0.003479`（=−1.113 px @320） | `-0.003480`（−1.114 px） | ✅ 符号/幅度都对 |
| 相机 +Y 平移 0.05/帧 | `mv.y = -0.006184` | 幅度 `0.006186`，符号见下节 | ✅ |
| 相机静止、物体 +X 平移 | `mv.x = +0.003479` | `+0.003480` | ✅（fallback 路径正确） |
| 深度单调 | `linear=5.000 / 10.000`（raw `0.057171 / 0.027081`） | 5 / 10 | ✅ 精确命中 |

逐帧轨迹（证明是"每帧一帧历史"，不是累计误差）：

```
trace panX#0 f=20 cam=0.050 mv=(-0.003479,0.000000) vp=-0.0487 pvp=0.0000
trace panX#4 f=24 cam=0.250 mv=(-0.003479,0.000000) vp=-0.2436 pvp=-0.1949
trace objMove#4 f=38 moverX=0.250 mv=(0.003479,0.000000)
```

深度 raw 值随距离**变小**（0.0571 → 0.0271）说明本机是 reversed-Z（`SystemInfo.usesReversedZBuffer=True`），
换算后线性距离正好是 5 / 10——即"近小远大"在 raw 值上表现为"近大远小"，交付给原生时要按 reversed-Z 口径解释。

---

### MV 的坐标系与尺度（换算公式 + 依据）

**依据（源码，`CameraMotionVectors.shader` / `ObjectMotionVectors.shader`）**：

```hlsl
velocity = posNDC - prevPosNDC;      // NDC(-1..1) 增量，y 向上
#if UNITY_UV_STARTS_AT_TOP
    velocity.y = -velocity.y;        // 到背缓冲时 y 取反
#endif
velocity.xy *= 0.5;                  // 再折半 → 存的是"UV 空间增量"
```

**实测结论（重要）**：本工程 Unity 2022.3 + URP 14.0.12 下，**渲染到 RT 与渲染到背缓冲的 y 符号一致**
（两者都是 `-0.006184`，见 `panY` 与 `panYBack` 两行）。也就是说 `UNITY_UV_STARTS_AT_TOP` 这条分支
在采样到的两份 MV 里都没生效——等价于：

> **`_MotionVectorTexture` = ½ · (当前NDC − 上帧NDC)，y 向上为正（NDC 方向），与目标无关。**

**换算公式**（render = 渲染分辨率，mv 为纹理采样值）：

| 目标口径 | 公式 |
|---|---|
| NDC 增量（`motion_vectors.hlsl` 的输出口径） | `ΔNDC = (2·mv.x, 2·mv.y)` |
| 渲染分辨率**像素**，y 向下 | `pixels = (mv.x · W, −mv.y · H)` |
| 渲染分辨率**像素**，y 向上（NDC 同向） | `pixels = (mv.x · W, mv.y · H)` |

**与 `ZupMotionVectorScale` 对齐**：`Native/zupscaler/zupscaler.h:178` 的口径是"输入 NDC 增量 →
像素、y 向下"，即 `(0.5W, −0.5H)`。两者关系只有一步：

```
pixels = ZupMotionVectorScale(W,H) · ΔNDC = (0.5W, −0.5H) · (2·mv)        // 先 ×2 还原 NDC
       = (W, −H) · mv                                                      // 等价：scale 直接取 (W,−H)
```

**所以原生侧只需要二选一**（推荐后者，少一次乘法且不引入中间量）：

- 方案 A：`motionVectorScale = (0.5·W, −0.5·H)`，但必须先把 Unity MV **乘 2** 当作 NDC 传；
- 方案 B：`motionVectorScale = (W, −H)`，把 Unity MV 原样当"半个 NDC"直接喂。

实测量级校验：`mv.x=−0.003479 × W(320) = −1.113 px`，与理论 `−1.114 px` 一致（见上式）。

**附加两条给 FG 落地**：

1. **jitter**：URP 的 MV 用 `_NonJitteredViewProjMatrix` / `_PrevViewProjMatrix`（非 jitter 矩阵）算，
   MV 里**不含** TAA 抖动。因此 FFX 侧不要开 `MOTION_VECTORS_JITTER_CANCELLATION`（我们的 jitter 视为 0）。
2. **首帧**：`MotionVectorRenderPass` 才把 `depthTextureMode |= MotionVectors`，切换相机/复位的那一帧 MV 为 0
   （实测 `trace panX#0` 之前一帧 pvp==vp），接线时应把 reset 帧的 MV 当无效帧丢弃。

---

### 阶段 B（已执行）：depth/MV 交接给原生 + 有效性校验

- **C ABI**：`ZR_FrameGen_ValidateInputs(const ZrFgInputDesc*, ZrFgInputReport*)`、
  `ZR_FrameGen_InputBytesPerPixel(uint32_t)`（`Native/zplugin/zplugin.h` / `.cpp` / `zrender.def`）。
  只做**非空 / 尺寸（0、超限、depth 与 MV 一致）/ 格式白名单**四类判定，**不建设备、不碰 FFX**。
  错误码：`0=OK, -1=NULL, -2=ABI, -3=PTR, -4=SIZE, -5=MISMATCH, -6=FORMAT`。
- **托管桥**：`ZrNative`（DllImport 集中地）新增两条 P/Invoke + `ZrFrameGenNative` 门面；
  `ZFrame.Render.ZrFrameGenInputs` 负责 `Texture → (ptr, w, h, GraphicsFormat→白名单格式)` 的翻译。
- **真机证据**（Player + D3D12，真纹理直接喂）：

```
[native] real depth=640x480 R32_SFloat mv=640x480 R16G16_SFloat rc=Ok report{accepted=True 640x480 depthBpp=4 mvBpp=4}
         ptrD=0x13D113C71A0 ptrM=0x13D113C5D90
[native] nullPtr rc=TexturePtr (exp -3) mismatch rc=Mismatch (exp -5) badFormat rc=Format (exp -6) oversize rc=Size (exp -4)
[native] validation verdict = PASS
```

- **原生单测**：`Native/tests/test_framegen_inputs.cpp`（6 例）覆盖合法双形态（R32F/D32F 深度）、
  空指针、ABI、尺寸、尺寸不一致、格式白名单 + 每像素字节数映射。

---

### 踩到的坑（写给下一个接线的人）

1. **必须在 Player 里验**。Editor 批处理能起 D3D12，但：
   ① 编辑态渲染会因 gizmo/预通道走 depth-stencil 附件，`_CameraDepthTexture` **拿不到** R32F 拷贝（实测 global=null）；
   ② 手动 `Camera.Render()` 连调多次时引擎**不按帧锁存上帧矩阵**，物体 MV 会变成"5 帧累计量"
   （实测 `0.017395 = 5 × 0.003479`）。这两点都会把人骗成"URP 产不出 MV"。
2. **`QualitySettings.renderPipeline = ...` 在 Editor 会落盘**：本轮 Editor 试跑后 `QualitySettings.asset`
   被改成 `antiAliasing: 2→0`、`customRenderPipeline: {fileID: 0}`，`URP_*.asset` 的 prefilter 字段也被
   Editor 重排。**已全部 `git checkout` 还原**（见"现场状态"）。运行期接管管线若要在 Editor 也生效，
   必须换成不写回 ProjectSettings 的方式，或只在 Player 生效。
3. **MSAA=4 是深度拷贝路径的隐形开关**（见 Q2 的"注意"）。
4. 探针里 `SupportedRenderingFeatures.active` 在 Player 下**可能为 null**（编辑器专属初始化），
   读取前必须判空——第一版没判空，`NullReferenceException` 直接打断了 pass 的后续逻辑。

---

### 改了哪些文件

| 文件 | 变更 |
|---|---|
| `Native/zplugin/zplugin.h` | 新增 `ZrFgTexFormat` / `ZrFgInputDesc` / `ZrFgInputReport` / 两条导出声明 + 错误码 |
| `Native/zplugin/zplugin.cpp` | 实现 `ZR_FrameGen_ValidateInputs` / `ZR_FrameGen_InputBytesPerPixel`（纯逻辑，无锁无设备） |
| `Native/zplugin/zrender.def` | 追加两行导出（EXPORTS 节内无中文行内注释） |
| `Native/tests/test_framegen_inputs.cpp` | 新增 6 例单测 |
| `Native/CMakeLists.txt` | 测试源加入 `test_framegen_inputs.cpp` |
| `UnityProject/Assets/ZRender/Bindings/ZrNative.cs` | 两条 P/Invoke + `ZrFrameGenNative` 门面 + `ZrFgInputDescNative`/`ZrFgInputReportNative`/`ZrFgTexFormatNative` |
| `UnityProject/Assets/ZRender/Runtime/ZrFrameGenInputs.cs` | 新增：`Texture → 原生体检` 的托管入口（含错误枚举与可断言的 report） |

**是否碰 URP 资产：没有。** 三个 `URP_*.asset` 在 Editor 试跑后被 Unity 自动改写（prefilter 字段重排），
已 `git checkout` 还原；`git status` 现在只剩下面列的改动，**无 `URP_*.asset`**。
探针全程用 `ScriptableObject.Instantiate` 出来的**内存副本**（`URP_Medium__R9Runtime`），未调用
`AssetDatabase.SaveAssets`，未改任何资产字段。**因此没有需要主控裁决的资产改动。**

探针代码（`Assets/__r9probe/`，含两个临时 asmdef）已整体 `mv` 到
`TempLogs/archive_pre_audit/r9_02_probe/__r9probe/`，未 `rm -rf`。

---

### 六段数字

| 段 | 结果 |
|---|---|
| native | `test cases: 196 \| 196 passed`；`assertions: 710835 \| 710835 passed`（`/W4 /WX` 零警告） |
| editmode | `total=678 passed=678 failed=0` |
| playmode | `total=6 passed=6 failed=0` |
| arch / config / naming | 三个 `exit=0` |
| 汇总 | `ALL PASS (6 passed, 0 skipped, 0 failed)`，报告 `TestReport.txt` |

**基线口径说明**：native 由 **190 → 196 例、710,800 → 710,835 断言**，增量**全部来自本任务新增的
6 例**（`test_framegen_inputs.cpp`）；原有 190 例仍在且全通过，editmode 678 / playmode 6 / 三校验器
与基线完全一致。按"不回归"口径判 PASS；若验收要求**数字完全不动**，这 6 例即是唯一差异，请据此裁决。

---

### 未验证项

1. **MSAA=4 下的深度句柄**：本轮在克隆上把 MSAA 设为 1 取 R32F 拷贝路径；MSAA=4 时 `_CameraDepthTexture`
   退化为 depth-stencil 附件（`gfx=None fmt=Depth`，ptr 有效）——该形态下 GPU 侧采样/读回未实测。
2. **蒙皮/顶点动画物体的 MV**：URP Lit 无 MV pass，这类物体走 fallback 会得到"只有相机项"的错误 MV；
   需要自带 MotionVectors pass（ShaderGraph 在 URP 14 也不生成），属 FG 落地的前置工作。
3. **长时间稳定性 / resize / 全屏切换**：本轮只跑了几十帧的最小场景。
4. **TAA 路径**：本轮走"renderer feature 声明 Motion"路线，未验证 TAA 触发 MV 的路径
   （TAA 还要求 MSAA=1 + 后处理开启，与本项目 MSAA=4 冲突）。
5. **FG 联调**：按简报不做（依赖 B-R9-01 的 hook 部署）。

---

### 现场状态

```
 M Native/CMakeLists.txt              (本任务)
 M Native/zplugin/zplugin.cpp         (本任务)
 M Native/zplugin/zplugin.h           (本任务)
 M Native/zplugin/zrender.def         (本任务)
 M UnityProject/Assets/Plugins/ZRender/zrender.dll   (build_native.ps1 产物，与新 ABI 配套)
 M UnityProject/Assets/ZRender/Bindings/ZrNative.cs  (本任务)
 M TASKS.md                           (进入任务前即为 M，非本代理改动)
?? Docs/Logs/TASK-B-R9-02.md          (本任务日志)
?? Native/tests/test_framegen_inputs.cpp
?? UnityProject/Assets/ZRender/Runtime/ZrFrameGenInputs.cs(+.meta)
```

- 未 `git commit`；未改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`；
  未改 `FrameGovernor`/`GovernorLadder`/`GovernorTuning`；未动 Unity 安装目录。
- `zrender.dll` 是 `build_native.ps1` 的正常产物，与新 C ABI 配套；若主控要求"仅源码入库"，可
  `git checkout -- UnityProject/Assets/Plugins/ZRender/zrender.dll` 还原，但还原后 Unity 侧新 P/Invoke
  会抛 `EntryPointNotFoundException`（需先跑一次原生构建）。


---

## 验收清单（供验收代理）

- [ ] V1 阶段 A 三问有实证（不是"应该可以"）；门禁判定明确
- [ ] V2 若 STOP：确有卡点证据，且未伪造数据/未硬写后续阶段
- [ ] V3 MV 坐标系与尺度的换算公式有依据（对照 `ZupMotionVectorScale` 口径）
- [ ] V4 资产未被擅自修改（`git status` 无 `URP_*.asset`）；若必须改，是否已停手等裁决
- [ ] V5 阶段 B：原生侧有效性校验为真断言，非法尺寸/格式会被拒
- [ ] V6 六段不回归（190/710,800 / 678 / 6 / exit 0）
- [ ] V7 范围与现场干净，未 commit

## 验收结论（验收代理写）

> 验收代理：TASK-B-R9-02 独立验收（非施工方）。验收时间 2026-10-29。
> 全部数字为本代理**本轮实测**；未采信施工报告的任何自述数字。

### 逐条结论

| 项 | 裁决 | 一行证据 |
|---|---|---|
| V1 阶段 A 三问有实证 | **PASS** | `TempLogs/r9_02/player_run_d3d12.log`（51,943B，真 Player：Mono path / UTP MemoryLeaks 尾块），65 行 `[R9PROBE]` 覆盖 device/pass/ptr/grid；`Q1/Q2/Q3 → PASS` 均由该日志写死 |
| V2 若 STOP 的卡点证据 | **PASS（N/A）** | 门禁判 PASS，无 STOP 分支；未发现硬写后续阶段（阶段 B 只在 A 全绿后执行，日志顺序一致） |
| V3 换算公式有依据 | **PASS with 受限** | 代数与 `zupscaler.h:178` `ZupMotionVectorScale` 自洽（见"公式判断"）；但"y 向上"的论证链表述与 URP shader 源码字面冲突，且与 jitterCancellation 既有默认值冲突（见 F2/F3） |
| V4 资产未擅改 | **PASS** | 跑前、跑完 Unity 两次 `git status` 均无 `URP_*.asset` / `ProjectSettings/*`；6 个 URP 资产 m_MSAA=4/4/2/2/1/1 与 HEAD 一致 |
| V5 阶段 B 真断言 | **PASS** | `test_framegen_inputs.cpp` 6 例 35 条 `CHECK`，逐分支断言具体返回码（-1/-2/-3/-4/-5/-6），无一恒真；实测 710,835−710,800=35 精确对上 |
| V6 六段不回归 | **PASS** | 本轮 `run_tests.ps1`：native **196/710,835**、editmode **678**、playmode **6**、arch/config/naming 三 `exit=0`、`RESULT: ALL PASS`、进程 exit 0 |
| V7 范围与现场干净 | **PASS** | 未改 FrameGovernor/GovernorLadder/GovernorTuning（git status 无）；HEAD 仍 `f7e5d1e`；无 commit；文件无 BOM；Unity 安装目录 `find -newermt 2026-10-28` 零命中 |
| A 六段全量 | **PASS** | 见 V6；另有 `git diff` 逐文件确认原有用例文件一个未改，`TEST_CASE` 计数 190(HEAD) → 196(现)，增量全在新文件 |
| B 导出表实测 | **PASS** | `dumpbin -exports`（MSVC 14.44）：11 个导出，`ZR_FrameGen_ValidateInputs` / `ZR_FrameGen_InputBytesPerPixel` 均在表内；重建后复测仍 11/11 |
| C 范围 | **PASS** | 同上；`.def` 的 EXPORTS 节内 0 条行内注释（只有第 1–8 行的整行 `;` 注释），GBK 吞行坑未触发 |

### 重点怀疑点 · 独立核实结果

**1) `supportsMotionVectors` 不存在（施工断言）—— 属实**

- 在 `Library/PackageCache/com.unity.render-pipelines.universal@14.0.12` 全包 grep `supportsMotionVectors`：**0 命中**；
  同级 `com.unity.render-pipelines.core@14.0.12` 亦 0 命中；`Runtime/Data/UniversalRenderPipelineAsset.cs` 内 `-i motion` **0 命中**。
- 正向对照（证明 grep 有效性）：同包 `requiresMotionVectors` 命中 7 处；`UniversalRenderer.cs:1476` `inputSummary.requiresMotionVectors |= needsMotion`、
  `:1486` TAA 分支，与施工描述逐行一致。
- 结论：URP 14 的 MV **确实没有资产级开关**，只认 pass 的 `ScriptableRenderPassInput.Motion`。施工"能不改资产"有硬依据，**非绕过**。
- 补充（不改变结论）：renderer 资产里确有 `cameraMotionVector` / `objectMotionVector` 两行，但那是**可选的 fallback 材质覆盖槽**（`{fileID: 0}`），不是启用开关；
  报告"没有资产开关"的表述对 enable 语义准确，但对"资产里有 motion 字样"未作区分，易被后来人误读。

**2) MV 数值是否真实 —— 真实，且推理成立**

- 证据文件存在且两份 `cmp` 完全一致（51,943B）；同目录有真 Player 产物 `R9Probe.exe` + `UnityPlayer.dll` + `_Data`，build.log 45 万行。
- 复算：`0.5·0.05/(tan30°·1.7778·7)=0.003479`，`0.5·0.05/(tan30°·7)=0.006186`；日志 `-0.003479` / `-0.006184`，误差 0.03% / 0.03%（<0.1%）。
- 深度 side：`raw 0.057171→linear 5.000`、`0.027081→10.000`；线性化走 `_ZBufferParams` 反解（`R9Probe.cs:591`），是引擎真值不是自证式自洽，可信。
- 静止态 `mv=(0,0)`、7×7 网格 `maxAbs=0.000000`；逐帧 `pvp` 恒滞后一帧（`-0.1949→-0.2436`），证明是"单帧历史"而非累计。
- 结论：数值来自真实运行，"与理论吻合"的推理成立。

**3) MV 公式与 FFX 契约 —— 代数自洽，但与既有契约有两处真冲突（高价值）**

- 自洽部分：`pixels = ZupMotionVectorScale(W,H)·ΔNDC`（ΔNDC=2·mv）= `(W,−H)·mv`，代数无误；方案 A/B 乘积相同，FFX 只吃乘积，故数值上等价。
- **F2（须在联调前处理）**：`Native/zupscaler/fg_ffx.h:107` `jitterCancellation` **默认 true**，且既有两处调用点硬编码 true
  （`Native/tests/zrhi_d3d12_window.cpp:7079`、`Native/zfgprobe/zfg_probe_wrap.cpp:95`）。报告只说"FG 侧不要开"，
  **没写"现有默认是 true、必须显式改"**。若后续接线沿用现成 desc，FFX 会按"MV 含 jitter"反解一份并不存在的抖动 → 每帧 0.5px 级重投影误差。
  这不是本任务 FAIL（简报明示不做 FG 联调），但**是该报告最有价值的一条却没落地成契约改动**。
- **F3（表述层面）**：报告称"`UNITY_UV_STARTS_AT_TOP` 分支未生效 ⇒ MV 为 y 向上"。但 `CameraMotionVectors.shader:116-118` 源码确实取反，
  探针自己的 `ExpectedMvY()` 注释也按"已取反、存正值"写（期望 +0.006186），与实测 −0.006184 **三处口径互不一致**。
  好的一面：读数来自 blit+ReadPixels，对"整屏均匀平移"这种场，行翻转不改符号，故符号结论对"探针 vs GPU 直读"的差异免疫；
  坏的一面："与目标无关"是两例支撑的过强断言。建议 FG 联调时用"静止=0 / 单向 1px 已知位移"做端到端符号自检兜底。

**4) 资产零改动 —— 属实**

- 跑测试前、跑完 Unity 六段后各查一次 `git status`：均**无任何 `URP_*.asset`**，`UnityProject/ProjectSettings/` 也 0 改动。
- 探针源码（归档件）只有 `Object.Instantiate`，全仓库探针代码 **0 处 `AssetDatabase.SaveAssets`** → "只动内存副本"成立。

**5) 阶段 B 是真断言 + 真导出**

- 单测：6 例 / 35 `CHECK`；`nullPtr→NULL(-1)`、`desc.depth=null→PTR(-3)`、`structSize−4→ABI(-2)`、`0 维/16385/maxDimension=32769→SIZE(-4)`、
  `mvWidth=640→MISMATCH(-5)`、`depthFormat=RG16F / mvFormat=R32F / UNKNOWN→FORMAT(-6)`，另有"放宽上限后 8192 应通过"的正向反证——不是恒真。
- 实现侧（`zplugin.cpp`）检查顺序 PTR→ABI→尺寸→一致→格式，与断言一一对应；`out` 先 memset 再填 `structSize`，失败态 `accepted=0` 可断言。
- 导出：`dumpbin -exports` 实测 11 个符号（原 9 + 新 2），RVA 非 0。**.def 历史坑已排查**：EXPORTS 节（第 10 行起）纯 ASCII、无行内注释，中文只在节外的整行 `;` 注释里，未见符号被吞。

### 新发现问题

| ID | 级别 | 问题 | 建议 |
|---|---|---|---|
| F1 | 低（报告事实错误） | 报告写 "`URP_Medium.asset` 里 `m_MSAA: 4`"，实测 **`m_MSAA: 2`**（4 是 `URP_High`） | 结论不受影响（2>1 同样走 depth 附件路径），但"关 MSAA"的量级描述需更正；勿据此判断生产代价 |
| F2 | **高（联调必踩）** | `jitterCancellation` 默认 true + 两处调用点硬编码 true，与"URP MV 不含 jitter"直接矛盾 | 在 FG 联调任务里把默认改 false 或调用点显式置 false，并加一条单测锁死 |
| F3 | 中（表述/口径） | "y 向上、与目标无关"与 shader 源码、探针自身期望三处不一致 | 复述为"本机 Player+R16G16 读回下实测 y 不取反"，并把符号自检列进联调验收项 |
| F4 | 低（范围） | `zrender.dll` 二进制随任务改动入库（21,504B），每次跑构建都会产生 diff | 主控决定"仅源码入库"还是"dll 一并入库"；现行做法可跑通，但会造成噪声 diff |
| F5 | 低（信息） | `TASKS.md` 处于 `M`（新增 B-R9-02 → doing）。内容属派发期看板更新、非施工完成回写，但无法逐字节证明写入者 | 主控收口时一并在 commit 里带上 |

### 六段实测（本代理本轮，`Tools/run_tests.ps1`，PowerShell 工具 + 先清代理）

```
native   PASS  cases=196 asserts=710835 failed=0; ctest 100% (1 entry)
editmode PASS  total=678 passed=678 failed=0
playmode PASS  total=6   passed=6   failed=0
arch     PASS  exit=0
config   PASS  exit=0
naming   PASS  exit=0
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)   EXITCODE=0
```

"6 例是新增而非替换"的独立证明：`TEST_CASE` 宏计数 HEAD=190（23 个已登记测试文件逐个对比，全部相等）→ 现 196，差额 6 全在新增文件
`Native/tests/test_framegen_inputs.cpp`；`git status` 显示**无任何既有测试文件被修改**；断言增量 710,835−710,800=35 与该文件 `CHECK` 数 35 精确吻合。

### 现场已还原

- 本代理唯一临时产物 `TempLogs/audit_r9_02_run_tests.log` 已 `mv` 到 `TempLogs/archive_pre_audit/r9_02_audit/`；无 `rm -rf`。
- 跑完 Unity 后复检：`git status` 与跑前逐行一致（11 项），HEAD 仍 `f7e5d1e`，**无 `URP_*.asset` 出现**，无需还原。
- 未 commit、未改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`、未改任何产品代码。

### 最终裁决

**PASS with 受限（验收通过，可收口；F2 必须在 FG 联调任务开工前解决）**

- 门禁实质成立：URP 14 在本项目下确能产出可用 MV + 深度，D3D12 下双双拿到非空原生句柄，数值误差 <0.1%，全程零资产改动——四条均有本代理独立复现的证据。
- 受限项：① MV/深度证据来自**探针 Player**，不是产品链路（任务本就只要求门禁，成立）；② MSAA>1 下的深度附件形态仅拿到 ptr，GPU 侧读写未实测；
  ③ MV 的 y 方向结论仅 2 个工况支撑，且与 shader 源码字面冲突（F3）；④ `m_MSAA` 记错（F1）。
- 不计 FAIL 的原因：以上均不影响"可行性"结论，也不产生伪造数据；但 F2 若不改就联调，FFX 侧行为必然错，**联调任务必须把它当第一项前置**。
