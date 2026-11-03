# TASK-B-R9-06 —— FG 落地接线：每帧输入提交 + 设置页开关（不做假开关）

- **轨道**：B / R9（收尾用户最初选定的「设置页接 FG 开关」）
- **状态**：done（验收 PASS with 受限 10/10，受限项 = MSAA 未告知 → 已由 B-R9-07 修复）
- **派发**：主控，2026-10-29
- **侦察笔记（必读）**：`Docs/Logs/TASK-B-R9-06-recon.md`（349 行，带行号）

## 任务简报（主控写）

### 0. 现状与范围裁决

原生侧已经就绪，**只差一层接线**：

| 已经有的 | 证据 |
|---|---|
| 真插帧（双链已接管，2.0× 呈现、0.943、1:1 Prepare:Present） | B-R9-04（`1403186`） |
| 崩溃已堵（安装竞态 + D3D11 门控） | B-R9-05（`0a0928f`） |
| URP 能产出可用 depth/MV，D3D12 下可拿到 `ID3D12Resource*` | B-R9-02 门禁 PASS |
| 运行时控制导出已存在：`ZFG_FgSubmitInputs` / `ZFG_FgSetEnabled` / `ZFG_FgGetStats` | `zfg_probe_dll.cpp:41/53`、`zfg_dxgi_proxy.cpp:443/457`（proxy 转发） |

| 缺的 | 后果 |
|---|---|
| `UnityProject/Assets` 下对 `ZFG_*` 的 P/Invoke **0 处** | `haveInputs` 恒 false → **即使把开关做出来，一帧也不会被插**（侦察结论） |
| 没有每帧把 depth/MV 提交给 FFX 的路径 | 同上 |

**范围裁决（主控）**：本任务 = **让开关真生效**，即"C# 绑定 + 每帧提交真实 depth/MV + 设置行 + 能力门控"。
**明确不做**：
- 不做"能点但无效"的开关；若阶段 1 拿不到真实输入，**按简报 STOP 并回报**，不许用假数据/TODO 占位糊过去。
- 不动 FFX 调用契约与 `appLevel = isWrapped && !nested` 语义（B-R9-04 已确立）。
- 不做 FSR FG 以外的插帧后端，不碰 DLSS FG。

### 1. 三个阶段（阶段 1 是门禁，不过不许往下走）

**阶段 1（门禁）：把真实输入喂到 FFX**
1. C# 侧封装 `ZFG_FgSubmitInputs` / `ZFG_FgSetEnabled` / `ZFG_FgGetStats`（`DllImport`），
   按工程既有 P/Invoke 风格包一层（参考 `Assets/ZRender/**` 对 `zrender.dll` 的包法：失败不抛、返回 false + 一次告警）。
2. 每帧从 URP 拿 depth 与 motion vector 的 `ID3D12Resource*` + 尺寸 + 格式，调用提交。
   复用 B-R9-02 已证明可行的手段（其结论与代码在 `Docs/Logs/TASK-B-R9-02.md`；**先读它，别重新发明**）。
3. **门禁判据**：`ZFG_FgGetStats` 里 `haveInputs` 为真、且有非零 `generated`（即真的插了帧）。
   给出真实运行的数字。**拿不到就 STOP**，写清卡点（哪一步拿不到、为什么）。

**阶段 2：设置页开关 + 能力门控**
1. 视频页新增 `video.frameGeneration`（Toggle），默认**关**，走既有 `SettingDefinition` 数据驱动（参照 A-R9-01 的 `FrameGovernorSettingOptions.cs`）。
2. 开关的 Apply 直连 `ZFG_FgSetEnabled(0/1)`；关闭时必须**纯透传**（不提交输入、不 Prepare），
   且**不得销毁重建 FFX 上下文**（recon 结论：销毁会连带销毁 Unity 已持有的 swapchain，不可逆）。
3. **能力门控**（三档，都要在行标签上诚实表达，不新造灰显机制）：
   - **Editor 内**：dxgi 代理不生效 → 标注"（仅打包后生效）"，不得让人以为点了有用；
   - **非 D3D12**：不值得用（B-R9-05 已门控为透传）→ 标注"（需 D3D12）"；
   - **dll 不在 / hook 未装**：`DllImport` 会失败 → 捕获后禁用该行并标注原因，**不许抛异常、不许刷屏**。
4. 持久化走既有 settings.json；开机应用点（`SettingsBootApplier`）要把该键重放给 `ZFG_FgSetEnabled`。
   **注意执行序**：hook 由进程加载时安装，早于场景加载——若顺序上有问题，写清楚为什么安全。

**阶段 3：验证（要给数字，且关/开都要测）**
1. **开**：`ZFG_HOOK=fg`（或经开关打开）跑 60s → `generated/real ≥ 0.9`、`prepareFail=0`、`lastRc=0`、呈现比 ≈2.0×。
2. **关**：开关关闭跑 60s → `generated=0`、呈现比 **1.0×**、帧率与基线一致（证明真的没插）。
3. **切换**：运行中 开→关→开 各一次，**不崩**、frameID 连续、`prepareFail` 不增长。
4. **六段不回归**：`Tools/run_tests.ps1` 全绿 exit 0（native 196/710,837、editmode 678+新增、playmode 6、三校验器 exit 0）。

### 2. 硬约束

- **不许伪造输入**：不允许用常量/合成图冒充 depth/MV 来"让数字好看"。合成路径只能用于单元测试，且必须在报告里标明。
- **不许每帧锁 GPU**：取资源指针不得引入 `WaitForSingleObject`/`Flush` 之类同步 stall；性能数字要给（开/关帧率对比）。
- **失败安全**：任何一步失败（dll 缺失、格式不符、指针为空）都必须回退到"纯透传 + 一次告警"，**不许崩**。
- 不装软件、不联网、**不 git commit**。
- 触碰文件：`UnityProject/Assets/ZFrame/View/**`、`UnityProject/Assets/ZRender/**`（绑定层）、`UnityProject/Assets/ZFrame/Tests/**`、`Native/zfgprobe/**`（仅在确有必要时，且报告说明理由）、本日志。
- **不得提交任何 `Assets/__zfgprobe` 临时探针资产**（B-R9-04 的规矩）。

### 3. 自测清单（施工必做，贴证据）

1. 阶段 1 门禁：`haveInputs` 真、`generated` 非零（真实运行截图/日志）。
2. 深度/MV 的**正确性**至少一条数值证据（静止时 MV≈0 或 depth 单调），不能只看"提交成功"。
3. 开关开：60s 数字达标（上面阶段 3-1 全部指标）。
4. 开关关：`generated=0`、1.0×、帧率不劣化。
5. 运行中切换不崩（给出切换前后 frameID / prepareFail）。
6. 三种门控状态各验证一次（Editor / 非 D3D12 / dll 缺失），确认是"禁用 + 标注 + 不抛"。
7. 六段全绿 exit 0，写明 editmode 新增几条。
8. `git status` 无 `Assets/__zfgprobe`、无 `URP_*.asset`、改动清单完整。
9. 性能：开/关帧率对比（B-R9-04 基线 59.8–59.9）。

### 4. 交付物

- 代码 + 测试 + 本日志「施工报告」（9 条证据、depth/MV 获取方式与换算公式、门控矩阵、诚实边界）。
- 不动 HANDOFF/TASKS/WORKLOG。

---

## 施工报告（施工代理写）

> 施工代理：B-R9-06 施工实例（与 B-R9-04/05 施工、验收均不同实例）。**未 git commit / 未 git add**。
> 证据目录：`TempLogs/r9_06/`（`ZFG_HOOK_LOG` 全部指到这里，未落 `%TEMP%`）。
> 真机件：`TempLogs/r9_06/player/`（构建产物 + 五个运行时 dll，`TempLogs` 不入库）。

### 0. 门禁结论：**PASS**（阶段 1 通过，阶段 2/3 已执行）

一句话：**C# 侧现在真的把 URP 的 depth/MV 每帧交给 FFX 了 —— `haveInputs=True`、`generated=3596 / real=3597`、
`prepareFail=0`、`lastRc=0`、呈现比 2.000×；开关关掉后 `generated=0`、比值 1.000×、帧率 59.97。
开关不是装饰：它直连 `ZFG_FgSetEnabled`，且运行中 开→关→开 三次切换零失败。**

关键数字（`TempLogs/r9_06/gate_unity.log`，Player + `-force-d3d12` + `ZFG_HOOK=fg`，1280x720）：

| 段落 | 时长 | appFrames | fps | dReal | dGen | genPerReal | 呈现比 | prepareFail | lastRc |
|---|---|---|---|---|---|---|---|---|---|
| off 基线（开之前） | 10s | 556 | 55.54 | 556 | 0 | 0.000 | 1.000 | 0 | 0 |
| **开** | 60s | 3596 | 59.93 | 3597 | **3596** | **1.000** | **2.000** | 0 | 0 |
| **关** | 60s | 3599 | 59.97 | 3599 | **0** | 0.000 | **1.000** | 0 | 0 |
| 切换 开 | 12s | 720 | 59.93 | 720 | 720 | 1.000 | 2.000 | 0 | 0 |
| 切换 关 | 12s | 720 | 59.97 | 720 | 0 | 0.000 | 1.000 | 0 | 0 |
| 切换 开 | 12s | 720 | 59.94 | 719 | 720 | 1.001 | 1.999 | 0 | 0 |

（off 基线 55.54fps 含首帧建管线/建场景的卡顿，不作为可比基线；同进程内的关段 59.97 才是可比对象。）

---

### 1. 改了哪些文件

**新增（产品）**

| 文件 | 作用 |
|---|---|
| `ZRender/Bindings/ZrFgProxyNative.cs` | dxgi 代理三个 `ZFG_*` 导出的 P/Invoke + `ZfgFgFrameInputs`(128B)/`ZfgFgStats`(48B) 的 POD 镜像。三类绑定异常（`DllNotFound`/`EntryPointNotFound`/`BadImageFormat`）就地折算成 `false` + 记录失败类型，一次之后不再重试（不刷屏、不抛） |
| `ZFrame/Core/FrameGeneration/IFrameGenerationSettings.cs` | 服务接口 + `FrameGenerationCapability` 五档 + `FrameGenerationStatsInfo`（View 只能经 `Services` 消费） |
| `ZRender/Runtime/ZrFrameGenerationSettings.cs` | 能力判定纯函数 `FrameGenerationCapabilityPolicy.Decide`、API 缝隙 `IZrFrameGenApi`、服务实现、`BeforeSceneLoad` 自注册 + 出厂压 `SetEnabled(0)` |
| `ZRender/Runtime/ZrFrameGenerationFeature.cs` | 运行期装配 URP 通道（克隆管线资产 + `ConfigureInput(Motion\|Depth)` 的空 pass）+ **每帧提交**（读 `_CameraDepthTexture`/`_MotionVectorTexture` → `ZFG_FgSubmitInputs`） |
| `ZFrame/View/Settings/FrameGenerationSettingOptions.cs` | 行 id/标签后缀/可交互门控/apply（唯一数据源） |

**修改（产品）**

| 文件 | 改动 |
|---|---|
| `ZFrame/View/Settings/SettingsRegistry.cs` | 视频页**末尾**追加 `video.frameGeneration`（Toggle，默认 false；不打乱既有行序/下标） |
| `ZFrame/View/Settings/SettingDefinition.cs` | `Toggle(...)` 增加可选 `interactable` 门控（`Func<bool>`，null = 恒可交互，既有行零影响） |
| `ZFrame/View/Settings/SettingsRowFactory.cs` | Toggle 行按门控置 `interactable=false`（与 Binding 行按 `IsAvailable` 置灰同一原则） |
| `ZRender/Runtime/ZFrame.Render.asmdef` | 加 `GUID:15fc0a...`（`Unity.RenderPipelines.Universal.Runtime`）—— `ScriptableRendererFeature` 必需。**用 GUID 形式与 `ZFrame.View.asmdef` 现有 URP 引用一致**，ArchitectureValidator 对 `GUID:` 条目按既有规则放行；名字形式的允许表未动 |

**修改（原生，仅 3 处必要改动，理由在后）**

| 文件 | 改动 | 为什么必须 |
|---|---|---|
| `Native/zfgprobe/zfg_fg_input.h` | `ZfgFgStats` 尾插 `haveInputs` + `reserved`（40 → 48B） | 阶段 1 门禁判据就是 `ZfgFgStats.haveInputs`，原结构没有这个字段 |
| `Native/zfgprobe/zfg_probe_wrap.cpp/.h` | `WrapGetStats` 填 `haveInputs`（在 `g_inputLock` 共享锁下读）；新增 `WrapFrameGenAuthorized()`（= `g_enabledOverride == 1`） | 同上；授权位需要给 hook 读 |
| `Native/zfgprobe/zfg_probe_hook.cpp` | 两处 `WrapOnFrame(self, g_mode == Mode::Fg)` → `ModeWantsFrameGen()`；wrap 模式下**仅当托管侧显式 `ZFG_FgSetEnabled(1)`** 才 want | 不改的话，`ZFG_HOOK=wrap` 部署下设置页开关"能点但不生效"（recon R3 的假开关）。默认 `-1`（跟随模式）仍返回 false，wrap 的透明语义不变 |

**测试**：`ZFrame/Tests/Pipeline/FrameGenerationSettingsTests.cs`（新，11 例）、
`ZRender/Tests/ZrFrameGenerationSettingsTests.cs`（新，8 例）、
`ZFrame/Tests/Debug/SettingsRegistryTests.cs`（8→9 行）、`ZFrame/Tests/Debug/SettingsPanelSmokeTests.cs`（行数 8→9 / 9→10 + 插帧控件断言）。

---

### 2. depth/MV 怎么拿到、怎么换算（复用 B-R9-02，未重新发明）

**获取路径（全部运行期，资产零改动）**

```
帧内：URP pass（AfterRenderingTransparents）
  ├ Shader.GetGlobalTexture("_MotionVectorTexture") → 1280x720 R16G16_SFloat（uav 见下）
  ├ Shader.GetGlobalTexture("_CameraDepthTexture")  → 1280x720 R32_SFloat（单采样拷贝）
  ├ Texture.GetNativeTexturePtr() → ID3D12Resource*（D3D12 下就是资源指针）
  └ ZFG_FgSubmitInputs(结构体)  → dxgi 代理 → zfg_hook → ffxDispatchDescFrameGenerationPrepareV2
```

要让 URP **产出** MV，唯一手段是"有 pass 声明 `ScriptableRenderPassInput.Motion`"（URP 14 没有资产级开关）。
产品侧用一个只声明输入、不画东西的 `ScriptableRendererFeature`，挂在**运行期克隆**的管线资产 + 渲染器数据上
（`Instantiate` + `m_RendererDataList`/`m_DefaultRendererIndex` 反射写入 + `QualitySettings.renderPipeline = clone`），
磁盘上的 `URP_*.asset` 一个字节都不碰（六段跑完 `git status` 无 `URP_*`，见第 7 节）。

两条**硬前提**（都是运行期字段，不是资产改动）：
1. `supportsCameraDepthTexture = true`（否则 `_CameraDepthTexture` 不存在）；
2. `msaaSampleCount = 1`（FFX 的 depth 必须单采样；MSAA>1 时 URP 把 MSAA 的 depth-stencil 附件当深度纹理，
   FFX 不可用）。**本次实测原值 2 → 1，日志：`[插帧] 管线 MSAA 2 → 1（FFX 的 depth 必须单采样；仅影响本次会话的内存副本）`。**

还有一条容易被忽略的前提：URP 的 `MotionVectorRenderPass` 内部是 `camera.TryGetComponent<UniversalAdditionalCameraData>()`，
拿不到就 `return`（MV 纹理停在清零状态）。产品侧因此在相机变化时调一次
`camera.GetUniversalAdditionalCameraData()` 并置 `requiresDepthTexture = true`；
**没有这一步时实测 `mv` 恒为 `(0.000000,0.000000)`（panX 也一样）** —— 这是本轮踩到并修掉的坑。

**换算公式（与 `ZupMotionVectorScale` 口径对齐，B-R9-02 结论）**

```
URP 存的是"半个 NDC 增量"：mv = 0.5 · (NDC_now − NDC_prev)，y 向上
像素位移 = (mv.x · W, −mv.y · H)          ← 所以 mvScale 直接取 (W, −H)，Unity 的 mv 原样喂
jitter 恒 0（URP 用 _NonJitteredViewProjMatrix 算 MV）→ desc.jitterCancellation=false（B-R9-03 F2 已修）
深度 reversed-Z（SystemInfo.usesReversedZBuffer=True）→ desc.depthInverted=true
线性化：linear = 1 / (raw · _ZBufferParams.z + _ZBufferParams.w)
```

**数值证据（同一份 Player，装好通道之后采）**

```
[R906] [static] mv 1280x720 R16G16_SFloat ptr=0x2252267AF80 center=(0.000000,0.000000) edgeX=(0.000000,0.000000)
       | depth 1280x720 R32_SFloat ptr=0x22522678760 raw=0.039977 linear=7.000m
       | camX=0.000 vp.m03=0.0000 pvp.m03=0.0000
[R906] [panX]  mv 1280x720 R16G16_SFloat ptr=0x2252267AF80 center=(-0.003479,0.000000) edgeX=(-0.004871,0.000000)
       | depth 1280x720 R32_SFloat ptr=0x22522678760 raw=0.039977 linear=7.000m
       | camX=0.250 vp.m03=-0.2436 pvp.m03=-0.1949
```

- 静止 → MV 逐点 `0.000000`（中心 + 边缘两点都读，不是只看一个像素）；
- 相机 +X 0.05/帧 → 中心（距 7m 的方块）`mv.x = -0.003479`，理论 `0.5·0.05/(tan30°·1.778·7) = 0.003480`（误差 0.03%）；
- 同一帧左边缘（距 5m 的墙）`mv.x = -0.004871`，理论 `0.5·0.05/(tan30°·1.778·5) = 0.004871` —— **视差随深度变化正确**；
- 深度：中心 raw `0.039977` → 线性 **7.000m**（正好是方块距离 7m，reversed-Z 正确解释）。

---

### 3. 每帧提交与开关（阶段 2 的落点）

- **提交时机**：URP pass 的 `Execute`（`AfterRenderingTransparents`，MV/深度都已写完、Present 之前），
  每次应用 Present 恰好一份输入；原生侧 `onFrame` 日志里 `fresh=1 reused=0 stale=0` 全程如此。
- **reset**：首帧 / 关→开 / 帧号断档（`frameCount - last > 1`）→ `reset=1`；其余 `0`。
- **开关**：`FrameGenerationSettingOptions.ApplyEnabled` → `IFrameGenerationSettings.TrySetEnabled` →
  `ZFG_FgSetEnabled(0/1)`。**关 = 纯透传**：停止提交输入 + 原生 `Configure(frameGenerationEnabled=false)`
  直接 return（不 Prepare），**不销毁/重建 FFX 上下文**（销毁会连带毁掉 Unity 持有的交换链，`fg_ffx.cpp:760-765`）。
- **失败安全**：能力不足/绑定失败/装配失败 → 一律回退纯透传 + **一次** `ZLog.Warn`，值仍持久化；
  `ZFG_FgSetEnabled` 返回 1 不代表生效（原生无条件返回 1），真状态读 `stats.enabled`（延后 1 帧生效）。

**门控矩阵（三档必需 + 两个细分，全部实测或有用例）**

| 环境 | `QueryCapability` | 行标签 | 行可交互 | `TrySetEnabled(true)` | 证据 |
|---|---|---|---|---|---|
| Editor | `EditorOnly` | `插帧（仅打包后生效）` | **禁用** | `false` | EditMode 用例 + 面板冒烟断言 |
| 非 D3D12 | `NotD3D12` | `插帧（需 D3D12）` | 禁用 | `false` | Player `-force-d3d11` 实跑 |
| dxgi 代理不在 / hook 未装 | `ProxyUnavailable` | `插帧（未装代理）` | 禁用 | `false` | 移走 `dxgi.dll` 实跑 |
| 非 URP 管线 | `PipelineUnsupported` | `插帧（需 URP）` | 禁用 | `false` | 纯函数用例 |
| 可用（Player+D3D12+URP+代理链） | `Available` | `插帧` | 可交互 | `true` | 真机三段 |

---

### 4. 阶段 3 的补充证据

**开机重放（settings.json → 开关）**：把 `{"version":1,"settings":{"video.frameGeneration":true}}` 写进
Player 的 `persistentDataPath`，**夹具一次都没调开**，第一段就已经在插帧：

```
[插帧] URP 通道已装配：clone=URP_Medium__ZFrameFG renderer=URP_Medium_Renderer__ZFrameFG features=1 msaa=1
[R906] capability=Available detail=''
[R906] PHASE off_baseline seconds=5.0 appFrames=226 fps=45.20 dReal=226 dGen=225 dFail=0 genPerReal=0.996 presentRatio=1.996 haveInputs=True enabled=True rc=0
```

执行序安全：hook 由代理在**首次 `CreateDXGIFactory*`** 装上（早于场景加载），交换链 wrap 也早于场景；
`SettingsBootApplier` 在 `AfterSceneLoad` 重放时代理链已在，服务又在 `BeforeSceneLoad` 注册，顺序天然正确。

**wrap 启动模式下开关同样真生效**（验证原生那 3 行改动，`ZFG_HOOK=wrap`）：

```
[R906] PHASE off_baseline ... dGen=0 ... haveInputs=False enabled=False   ← 未授权时不插帧（默认 -1）
[R906] TrySetEnabled(true) -> True installed=True
[R906] PHASE on seconds=8.0 appFrames=480 fps=59.92 dReal=481 dGen=480 ... presentRatio=2.002
```

**帧 ID 连续 / 契约对齐**（`gate_fg.log`）：`onFrame #60 frameId=60 ... stale=1 enabled=0`（开之前）→
`#1260 frameId=1260 fresh=1 enabled=1 prepared=471 generated=471 real=1259` → … →
`#10140 frameId=10140 fresh=1 enabled=1 prepared=5032 generated=5032 real=10139`。
`frameId` 严格 +1、`prepared == generated`、整份原生日志 `FAILED` 出现次数 **0**。

---

### 5. 自测清单（9 条，逐条贴证据原文）

**1. 阶段 1 门禁：`haveInputs` 真、`generated` 非零 —— PASS**

```
[R906] capability=Available detail=''
[R906] TrySetEnabled(true) -> True installed=True
[R906] t=5s phase=on real=1100 gen=312 prep=312 fail=0 rc=0 haveInputs=True enabled=True
[R906] PHASE on seconds=60.0 appFrames=3596 fps=59.93 dReal=3597 dGen=3596 dFail=0 genPerReal=1.000 presentRatio=2.000 haveInputs=True enabled=True rc=0
[R906] final submitCalls=5048 rejected=0 note=''
```

**2. 深度/MV 正确性数值证据 —— PASS**：见第 2 节三段读数（静止 0 / 中心 −0.003479 / 边缘 −0.004871 / 7.000m）。

**3. 开关开 60s 达标 —— PASS**：`genPerReal=1.000`（要求 ≥0.9）、`dFail=0`、`rc=0`、`presentRatio=2.000`。

**4. 开关关 60s —— PASS**

```
[R906] TrySetEnabled(false) -> True
[R906] t=5s phase=off real=4696 gen=3608 prep=3608 fail=0 rc=0 haveInputs=True enabled=False
[R906] t=55s phase=off real=7695 gen=3608 prep=3608 fail=0 rc=0 haveInputs=True enabled=False   ← 55 秒内 gen 一个都没涨
[R906] PHASE off seconds=60.0 appFrames=3599 fps=59.97 dReal=3599 dGen=0 dFail=0 genPerReal=0.000 presentRatio=1.000 haveInputs=True enabled=False rc=0
```

**5. 运行中 开→关→开 不崩 —— PASS**

```
[R906] PHASE toggle_on_1  ... dReal=720 dGen=720 dFail=0 presentRatio=2.000
[R906] PHASE toggle_off_1 ... dReal=720 dGen=0   dFail=0 presentRatio=1.000
[R906] PHASE toggle_on_2  ... dReal=719 dGen=720 dFail=0 presentRatio=1.999
```

`prepareFail` 全程 0（切换前后都是 0，不增长）；`frameId` 连续（10140 之后继续 +1，无重置）。

**6. 三种门控各验证一次（禁用 + 标注 + 不抛）—— PASS**

```
# 非 D3D12（-force-d3d11）
[R906] capability=NotD3D12 detail='当前图形 API 为 Direct3D11，插帧需要 D3D12'
[R906] TrySetEnabled(true) -> False installed=False
[R906] PHASE on ... appFrames=300 fps=59.96 dReal=0 dGen=0   ← 正常跑、不崩、零插帧

# 代理缺失（同目录移走 dxgi.dll）
[R906] capability=ProxyUnavailable detail='dxgi 代理的 ZFG_* 入口不可用（EntryPointNotFoundException）'
[R906] TrySetEnabled(true) -> False installed=False
[R906] PHASE on ... appFrames=300 fps=59.95 dReal=0 dGen=0

# Editor：EditMode 用例断言行禁用 + 标签「仅打包后生效」（无 dxgi 代理，DllImport 落到系统 dxgi.dll）
Passed FrameGenerationRow_RendersAsAReal_DisabledToggle_InEditor
Passed RowIsInteractable_OnlyWhenCapabilityIsAvailable
Passed CapabilitySuffixes_AreDistinct_AndHonest
```

**7. 六段全绿 exit 0 —— PASS**（`Tools/run_tests.ps1`，全段）

```
native   PASS  cases=196 asserts=710837 failed=0; ctest 100% (1 entry)
editmode PASS  total=697 passed=697 failed=0
playmode PASS  total=6   passed=6   failed=0
arch     PASS  exit=0
config   PASS  exit=0
naming   PASS  exit=0
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)
```

editmode **678 → 697（新增 19 条）**：`FrameGenerationSettingsTests` 11 条 + `ZrFrameGenerationSettingsTests` 8 条；
既有 678 条一条未删未改（只把"视频页 8 行"三处硬编码改成 9/10）。

**8. `git status` 干净（无 `Assets/__zfgprobe`、无 `URP_*.asset`）—— PASS**

见第 7 节清单。跑完 Unity 后 `URP_High/Low/Medium.asset` 被 Editor 重算了 shader prefilter 字段（已知副作用，
B-R9-02 同样），已 `git checkout --` 还原；`git status` 现无 `URP_*.asset`、无 `ProjectSettings/*`。

**9. 性能：开/关帧率对比 —— PASS（含边界）**

`开 59.93fps` vs `关 59.97fps`（同进程、同场景、60fps 上限、vsync=0），差 0.04fps（0.07%）；
B-R9-04 基线 59.8–59.9，本轮关段 59.97 略优（空场景差异）。**本任务新增路径不含任何 GPU 同步**
（C# 侧只读纹理句柄与相机参数，无 `WaitForSingleObject`/`Flush`）；原生侧唯一的潜在等待是 B-R9-04 既有的
`WaitAllocatorIdle`（3 环分配器，`GetCompletedValue` 快路径正常帧不等），60 秒 3596 帧 `prepareFail=0`。

---

### 6. 诚实边界 / 未验证项

1. **MSAA 被改成 1**：装 FG 通道时若 MSAA>1 会被置 1（FFX 的 depth 必须单采样）。这是**可见的画质变化**，
   只作用于本次会话的内存副本，磁盘资产不动；改完不恢复（恢复要再换一次管线）。日志里有一行 warning。
2. **通道靠反射改 URP 内部字段**（`m_RendererDataList` / `m_DefaultRendererIndex`）：URP 升级改字段名会抛
   `InvalidOperationException` → 捕获成"装配失败 → 回退纯透传 + 一次告警"（不崩），非静默降级。
3. **开关在 Editor 里是禁用的**（这是要求），所以"拨开关真的插帧"只能在打包 Player 里复现 —— 本轮全部真机
   证据都来自 Player（`-force-d3d12`），Editor 侧只有用例级证据（标签/禁用/apply 链路/持久化/冷启动）。
4. **帧率对比在 60fps 上限下测**：只能证明"关掉不掉帧"，不能说明 >60fps 时的性能余量（FFX FG 在无上限时几乎
   不生成，B-R9-03 已证）。开/关都在 2.000× / 1.000× 的呈现比下比较。
5. **部署仍要人工**：`dxgi.dll` + `zfg_hook.dll` + 三个 AMD 运行库必须与 exe 同目录，且 `ZFG_HOOK=fg`（或 `wrap`）。
   本轮没做 Player 构建/拷贝自动化（recon §5.3 的缺口，超出本任务范围）。
6. **关掉后的性能不等于原生**：代理链与 FFX 的"一次内部合成拷贝"仍在（recon R11）。本轮关段 59.97fps ≈ 基线，
   但那是 60 上限下的结论。
7. **wrap 模式的默认透明**只做了静态保证 + 短跑（未授权时 `dGen=0 haveInputs=False`）；没有重跑 B-R9-04 的
   60s wrap 透明回归。改动的判据是"wrap 且 override==1 才 want"，而 `override` 只可能被 `ZFG_FgSetEnabled` 写成
   0/1，默认 −1 只有在**没有本模块**的进程里才出现（旧探针），那条路径的判定结果不变。
8. **单相机假设**：提交取"最后一个 Game 相机"的 depth/MV；多相机叠加（overlay）场景未验证。
9. **蒙皮/顶点动画物体的 MV** 仍走 fallback（URP Lit 无 MotionVectors pass）—— B-R9-02 的遗留限制，本任务不涉及。

---

### 7. 现场状态

```
 M Native/zfgprobe/zfg_fg_input.h
 M Native/zfgprobe/zfg_probe_hook.cpp
 M Native/zfgprobe/zfg_probe_wrap.cpp
 M Native/zfgprobe/zfg_probe_wrap.h
 M UnityProject/Assets/Plugins/ZRender/zrender.dll     (build_native.ps1 产物，与新构建配套)
 M UnityProject/Assets/ZFrame/Tests/Debug/SettingsPanelSmokeTests.cs
 M UnityProject/Assets/ZFrame/Tests/Debug/SettingsRegistryTests.cs
 M UnityProject/Assets/ZFrame/View/Settings/SettingDefinition.cs
 M UnityProject/Assets/ZFrame/View/Settings/SettingsRegistry.cs
 M UnityProject/Assets/ZFrame/View/Settings/SettingsRowFactory.cs
 M UnityProject/Assets/ZRender/Runtime/ZFrame.Render.asmdef
?? Docs/Logs/TASK-B-R9-06.md / TASK-B-R9-06-recon.md
?? UnityProject/Assets/ZFrame/Core/FrameGeneration(.meta)/
?? UnityProject/Assets/ZFrame/Tests/Pipeline/FrameGenerationSettingsTests.cs(.meta)
?? UnityProject/Assets/ZFrame/View/Settings/FrameGenerationSettingOptions.cs(.meta)
?? UnityProject/Assets/ZRender/Bindings/ZrFgProxyNative.cs(.meta)
?? UnityProject/Assets/ZRender/Runtime/ZrFrameGenerationFeature.cs(.meta)
?? UnityProject/Assets/ZRender/Runtime/ZrFrameGenerationSettings.cs(.meta)
?? UnityProject/Assets/ZRender/Tests/ZrFrameGenerationSettingsTests.cs(.meta)
```

- 临时件已 `mv`（未 `rm`）：夹具与构建入口 → `TempLogs/r9_06/probe_assets/`；开机重放用的 settings.json →
  `TempLogs/r9_06/evidence_settings_bootreplay.json`（Player 的 `persistentDataPath` 已复原，只剩 `saves/`）。
- 未 commit、未改 `HANDOFF`/`TASKS`/`WORKLOG`；未改 `FrameGovernor`/`GovernorLadder`/`GovernorTuning`；
  未改 `URP_*.asset`（Editor 副作用已还原）；`.cs` UTF-8 无 BOM + LF；`/W4 /WX` 零警告。
- 证据文件：`gate_unity.log` / `gate_fg.log`（主证据）、`gate_d3d11_*`、`gate_noproxy_*`、`gate_bootreplay_*`、
  `gate_wrapmode_*`、`smoke*_*`（早期冒烟，含 mv=0 的踩坑前状态）。

## 验收清单（主控写）

> 独立取证，不复用施工数字。10 项全 PASS 才通过。
> **本任务最大的风险是"看起来插了帧，其实是自己喂的假数据"和"为了跑通偷偷改了资产/画质"。两项都要专门查。**

1. **V1 门禁真伪（最关键）**：自己跑 60s（`ZFG_HOOK=fg` 或经开关）→ `haveInputs=True`、`generated>0`；
   **并核实输入来源是真实渲染产物**：给出至少一条数值证据（静止时 MV≈0 / 相机移动时 MV 非零且方向对 / depth 单调），
   或指出代码里 `ZFG_FgSubmitInputs` 的实参确实来自 URP 的 depth/MV 资源而不是常量/合成图。**发现合成图冒充 → FAIL。**
2. **V2 关=真没插**：开关关闭跑 60s → `generated=0`、呈现比 **1.000×**、帧率与基线一致。
3. **V3 运行中切换**：开→关→开 各一次 → 不崩、`frameId` 严格 +1、`prepareFail` 不增长（给出切换前后数字）。
4. **V4 三档门控**：Editor（行禁用 +「仅打包后生效」）、`-force-d3d11`（需 D3D12 拒开）、移走 `dxgi.dll`（ProxyUnavailable 拒开）——
   三者都必须**不抛异常、不刷屏、不崩**，且 UI 上能看出为什么不可用。
5. **V5 开机重放**：settings.json 里该键为 true → 冷启动（不打开设置面板）后 FG 已生效（给出 `dGen`/`generated` 证据）。
6. **V6 零破坏默认**：无 settings.json 的全新用户 → FG **默认关**、`generated=0`、六段数字与基线一致（editmode 只多新增用例）。
7. **V7 无 GPU stall**：代码审查 `ZFG_FgSubmitInputs` 调用路径，确认**没有** `WaitForSingleObject`/`Flush`/`Map(READ)` 之类同步；
   并给开/关帧率对比（基线 59.8–59.9）。
8. **V8 六段不回归**：全绿 exit 0（native **196**/710,837、editmode **697 = 678+19**、playmode 6、三校验器 exit 0）。
9. **V9 资产与范围**：`git status` **不得出现 `URP_*.asset`**、不得有 `Assets/__zfgprobe`；改动清单与简报白名单一致；
   原生 3 处改动（`ZfgFgStats` 加字段、`WrapGetStats`、`ModeWantsFrameGen`）是否**破坏 ABI 兼容**（`ZfgFgStats` 尾插字段可接受，须确认无 C# 侧按旧结构偏移读取）。
10. **V10 诚实边界核实**：施工自曝两点——①**MSAA 2→1**（FFX depth 需单采样）②**用反射改 URP 内部字段**。
    逐条核实是否属实、影响面多大（画质/稳定性/版本升级风险），是否在行标签或文档里对玩家如实说明；
    若"改了画质却不在 UI 上告知"→ 记为 **受限项**（不直接 FAIL，但必须写进结论与 HANDOFF）。

**须在结论中回答**：①`ZFG_FgStats` 尾插字段对既有 C# 消费方是否安全（有无按固定偏移读的代码）；
②MSAA 2→1 是"仅 FG 开启时"还是"全局"，关闭 FG 后是否恢复；
③反射改 URP 内部字段在 Unity 版本升级后会怎样（会不会静默失效/报错），有没有降级路径。

## 验收结论（验收代理写）

> 验收代理：B-R9-06 验收实例（与施工不同实例）。**未 commit / 未 add / 未改业务代码**；
> 只做「读代码 + 跑 Player + 跑六段 + 归档」。全部数字为本轮自跑，未复用施工报告任何数字。
> 本轮证据目录：`TempLogs/r9_06/verify/`（`v1_*` / `v5_*` / `v4_d3d11_*` / `v4_noproxy_*` / `v8_runtests.txt`）。
> 跑前均 `unset http_proxy https_proxy all_proxy`（含大写），Unity 只经 `Tools/run_tests.ps1`。

**结论: PASS with 受限（10/10 功能项通过；1 项受限标记 = V10 的 MSAA 2→1 未对玩家告知）**

跑测环境（自跑实测）：`R906.exe -force-d3d12`，1280x720，AMD Radeon 780M，`Direct3D 12 [level 12.1]`，
`reversedZ=True`，`Application.targetFrameRate=60 / vSyncCount=0`，`ZFG_HOOK=fg`。
本轮四轮 Player：A（no settings.json，10/60/60/12）、B（settings.json=true，8/8/8/4）、
C（`-force-d3d11`）、D（同目录移走 `dxgi.dll`，跑完立即 `mv` 还原，已核对 152064 字节归位）。

### V1 门禁真伪 —— PASS

自跑 60s（`TempLogs/r9_06/verify/v1_unity.log`）：

```
[R906] capability=Available detail=''
[R906] PHASE on seconds=60.0 appFrames=3595 fps=59.91 dReal=3595 dGen=3595 dFail=0
       genPerReal=1.000 presentRatio=2.000 haveInputs=True enabled=True rc=0
[R906] final submitCalls=5047 rejected=0 note=''
```

**输入来源核实（两道，缺一不可）**

1. **代码实参**：`ZrFrameGenerationFeature.cs:147-148` 取
   `Shader.GetGlobalTexture("_CameraDepthTexture")` / `("_MotionVectorTexture")`，
   `:166-167` 取 `GetNativeTexturePtr()` 后原样填进 `ZfgFgFrameInputs.Depth/MotionVectors`
   （`:189-190`）。产品路径里 **没有任何** `Texture2D`/`RenderTexture`/`SetPixel`/`Clear`/`Blit`/`ReadPixels`
   （grep 四文件结果为空），不存在合成图/常量路径。
2. **数值正确性**（本轮自跑，同机同场景，与物理量对得上；`v1_unity.log`）：

```
[R906] [static] mv 1280x720 R16G16_SFloat center=(0.000000,0.000000) edgeX=(0.000000,0.000000)
       | depth 1280x720 R32_SFloat raw=0.039977 linear=7.000m | camX=0.000
[R906] [panX]  mv 1280x720 R16G16_SFloat center=(-0.003479,0.000000) edgeX=(-0.004871,0.000000)
       | depth raw=0.039977 linear=7.000m | camX=0.250 vp.m03=-0.2436 pvp.m03=-0.1949
```

静止 → 中心与左边缘两点 MV 均为 `0.000000`；相机 +X 0.25m 后中心 `-0.003479`
（理论 `0.5·0.05/(tan30°·1.778·7)=0.003480`），同一帧左边缘（更近的墙，5m）
`-0.004871`（理论 `0.005…`，随深度变大）——**视差随深度变化正确，方向（+X 相机 → −x 像素）正确**；
深度 `raw=0.039977` 经 `1/(raw·z+w)` 得 **7.000m**（正是 mover 的 7m，reversed-Z 解释正确）。
这三条只有真 URP 产物才可能同时成立，合成常量图不可能满足"两点+随深度变化+7.000m"。
**未发现合成图冒充。**

### V2 关 = 真没插 —— PASS

同进程、同场景、紧接 on 段之后的 off 段（`v1_unity.log`）：

```
[R906] PHASE off seconds=60.0 appFrames=3598 fps=59.96 dReal=3598 dGen=0 dFail=0
       genPerReal=0.000 presentRatio=1.000 haveInputs=True enabled=False rc=0
```

`t=5s…t=55s` 八条采样 `gen=` 恒 `3607`（60 秒一个都没涨）；呈现比 **1.000**；
帧率 59.96 vs on 段 59.91（差 0.05fps，落在噪声内，且 59.96 与 B-R9-04 基线 59.8–59.9 同档）。

### V3 运行中 开→关→开 —— PASS

```
[R906] PHASE toggle_on_1  seconds=12.0 appFrames=720 fps=59.93 dReal=720 dGen=720 dFail=0 presentRatio=2.000
[R906] PHASE toggle_off_1 seconds=12.0 appFrames=720 fps=59.97 dReal=720 dGen=0   dFail=0 presentRatio=1.000
[R906] PHASE toggle_on_2  seconds=12.0 appFrames=720 fps=59.92 dReal=720 dGen=720 dFail=0 presentRatio=2.000
```

进程 exit 0、无异常。`prepareFail` 全程 0（切换前后都是 0，不增长）；`frameId` 严格 +1：
原生日志 167 条 `onFrame` 采样，相邻差值**全部** `=60`（`awk` 校验零 ANOMALY），末条 `frameId=10020`
（=167×60，与采样序号完全对齐，说明中间无重置/跳号）。原生日志 `FAILED` 出现 **0** 次。
`prepared` 与 `generated` 有 2 处差 1（`865/864`、`1165/1164`）——是"本帧 Prepare、下一帧 Present 才计生成"
的会计滞后（recon R5 已述），非丢帧。

### V4 三档门控 —— PASS

| 档位 | 自跑证据 | 行标签 / 可交互 |
|---|---|---|
| Editor | 本轮 EditMode 全绿（`v8_runtests.txt`），`FrameGenerationRow_RendersAsAReal_DisabledToggle_InEditor` PASS：断言 `isOn=false`、`interactable=false`、标签含「仅打包后生效」 | `插帧（仅打包后生效）`，禁用 |
| `-force-d3d11` | `v4_d3d11_unity.log`：`capability=NotD3D12 detail='当前图形 API 为 Direct3D11，插帧需要 D3D12'`；`TrySetEnabled(true) -> False installed=False`；on 段 `fps=59.96` 照常跑 | `插帧（需 D3D12）`，禁用 |
| 移走 `dxgi.dll` | `v4_noproxy_unity.log`：`capability=ProxyUnavailable detail='dxgi 代理的 ZFG_* 入口不可用（EntryPointNotFoundException）'`；`TrySetEnabled(true) -> False`；`fps=59.95` | `插帧（未装代理）`，禁用 |

两轮 Player 日志均无 `Exception` 堆栈、无 `NullReferenceException`、exit 0；`detail` 只出现一次（不刷屏）。
标注机制复用既有 `interactable`（`SettingDefinition.Toggle` 新增可选 `Func<bool>`，默认 null = 既有行零影响），
未新造灰显框架。

### V5 开机重放（不打开面板） —— PASS

把 `{"version":1,"settings":{"video.frameGeneration":true}}` 放进
`%LocalLow%/ZFrame/ZFrame/settings.json` 冷启动（夹具**全程未打开设置面板**，其第一段在调用
`TrySetEnabled` **之前**就读统计）：

```
[R906] t=5s phase=off_baseline real=467 gen=236 prep=236 fail=0 rc=0 haveInputs=True enabled=True
[R906] PHASE off_baseline seconds=8.0 appFrames=417 fps=52.04 dReal=417 dGen=416 dFail=0
       genPerReal=0.998 presentRatio=1.998 haveInputs=True enabled=True rc=0
```

**第一段就已经在插帧**（`enabled=True / dGen=416`），说明 `SettingsBootApplier`(AfterSceneLoad) →
同一 apply 路径 → `ZFG_FgSetEnabled(1)` 全链路在冷启动生效，且早于夹具任何拨动。
跑完把 settings.json `mv` 到 `TempLogs/r9_06/verify/v5_settings_after_run.json`（未 `rm`），
持久目录已复原为只剩 `saves/`。
（备注：`ZLog` 默认频道白名单为空 → "settings.json 开机应用 1 项"这行 Info 不进 Player 日志，
所以本条证据是行为证据而非日志文本，反而更强。）

### V6 零破坏默认 —— PASS

同一 Player、**无 settings.json** 冷启动（`v1_unity.log`，夹具首次 `TrySetEnabled` 之前的基线段）：

```
[R906] PHASE off_baseline seconds=10.0 appFrames=464 dReal=464 dGen=0 dFail=0 presentRatio=1.000
       haveInputs=False enabled=False
```

无 settings.json → `PrimeDisabled()`（`ZrFrameGenerationSettings.cs:232-236`）显式压 `SetEnabled(0)`，
`ZFG_HOOK=fg` 的默认"跟随模式"（override=−1）被覆盖为关：**默认纯透传**。
六段数字见 V8（全绿、editmode 只多 19 条新增，既有 678 条一条未删）。

### V7 无 GPU stall —— PASS（附一条既有边界）

- **C# 提交路径**（`ZFG_FgSubmitInputs` 的调用方，`ZrFrameGenerationFeature.cs:125-249`）：只读
  `Shader.GetGlobalTexture` / `GetNativeTexturePtr` / `camera.transform` / `Time`，**无** `WaitForSingleObject`、
  `Flush`、`Map(READ)`、`ReadPixels`、`AsyncGPUReadback`、`Graphics.Blit`。全路径无同步点。
- **绑定层**（`ZrFgProxyNative.cs:186-202`）：仅一次 `DllImport` 调用。
- **原生入库侧**（`zfg_probe_wrap.cpp:943-956` `WrapSubmitInputs`）：一次 `AcquireSRWLockExclusive` + POD 拷贝，无 GPU 操作。
- **既有边界（照实记）**：每帧 Prepare 走 `DispatchPrepare`(679) → `WaitAllocatorIdle`(657)，后者有
  `GetCompletedValue` 快路径，慢路径才 `WaitForSingleObject(ev, 100ms)`（3 环分配器，B-R9-04 既有设计）。
  60 秒 3595 帧 `dFail=0`、`prepareFail=0`、日志无 `prepare allocator still in flight`，即快路径 100% 命中。
  该等待不在 `ZFG_FgSubmitInputs` 上，且超时也只是"本帧不插"，不阻塞呈现。
- **开/关帧率对比（自跑）**：开 `59.91` / 关 `59.96`（差 0.08%，同在 60 上限下；关段优于 B-R9-04 基线 59.8–59.9）。

### V8 六段不回归 —— PASS

`powershell -NoProfile -ExecutionPolicy Bypass -File Tools/run_tests.ps1`（全段，本轮实跑，`v8_runtests.txt`）：

```
native   PASS  cases=196 asserts=710837 failed=0; ctest: 100% tests passed (1 entry: zrender_test)
editmode PASS  total=697 passed=697 failed=0 skipped=0
playmode PASS  total=6   passed=6   failed=0 skipped=0
arch     PASS  exit=0（违规数 0）
config   PASS  exit=0（违规数 0）
naming   PASS  exit=0（违规数 0）
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)
```

editmode **697 = 678 + 19** 已逐条核对：`FrameGenerationSettingsTests` 11 个 `[Test]` +
`ZrFrameGenerationSettingsTests` 8 个 `[Test]`；既有测试的改动只有"视频页 8 行→9 行"三处计数
（`git diff` 逐行看过，无删禁用例）。native 仍是 196/710837（与基线一致，`/W4 /WX` 零警告）。

### V9 资产与范围 —— PASS（含 1 处白名单外新增，理由成立）

```
git status --short（本轮跑完六段后）
 M Native/zfgprobe/zfg_fg_input.h
 M Native/zfgprobe/zfg_probe_hook.cpp
 M Native/zfgprobe/zfg_probe_wrap.cpp
 M Native/zfgprobe/zfg_probe_wrap.h
 M UnityProject/Assets/Plugins/ZRender/zrender.dll
 M UnityProject/Assets/ZFrame/Tests/Debug/SettingsPanelSmokeTests.cs
 M UnityProject/Assets/ZFrame/Tests/Debug/SettingsRegistryTests.cs
 M UnityProject/Assets/ZFrame/View/Settings/SettingDefinition.cs
 M UnityProject/Assets/ZFrame/View/Settings/SettingsRegistry.cs
 M UnityProject/Assets/ZFrame/View/Settings/SettingsRowFactory.cs
 M UnityProject/Assets/ZRender/Runtime/ZFrame.Render.asmdef
?? Docs/Logs/TASK-B-R9-06.md / TASK-B-R9-06-recon.md
?? Assets/ZFrame/Core/FrameGeneration(+.meta)/
?? Assets/ZFrame/Tests/Pipeline/FrameGenerationSettingsTests.cs(+.meta)
?? Assets/ZFrame/View/Settings/FrameGenerationSettingOptions.cs(+.meta)
?? Assets/ZRender/Bindings/ZrFgProxyNative.cs(+.meta)
?? Assets/ZRender/Runtime/ZrFrameGenerationFeature.cs(+.meta)
?? Assets/ZRender/Runtime/ZrFrameGenerationSettings.cs(+.meta)
?? Assets/ZRender/Tests/ZrFrameGenerationSettingsTests.cs(+.meta)
```

- **无 `URP_*.asset`、无 `Assets/__zfgprobe`**（`git status --porcelain | grep -E "URP_.*asset|__zfgprobe"` 为空）。
  跑完 EditMode/PlayMode 后也没冒出来——施工"运行期克隆、磁盘资产零改动"的说法成立。
- **白名单偏差 1 处**：新增目录 `Assets/ZFrame/Core/FrameGeneration/IFrameGenerationSettings.cs`
  不在简报白名单（简报只列 `ZFrame/View/**`、`ZRender/**`、`ZFrame/Tests/**`）。理由成立且必要：
  View 不得引用 `ZFrame.Render.Bindings`（架构矩阵），接口与 `Services` 同住 Core 是唯一不破矩阵的落点；
  `ArchitectureValidator` 本轮 exit 0、违规 0。
- `Assets/Plugins/ZRender/zrender.dll` 是 `build_native.ps1` 的部署产物（本轮六段又覆盖写了一次），非手改。
- **`ZfgFgStats` 尾插 ABI**：40 → 48 字节（`zfg_fg_input.h:53-62`），字段序 `4×u64 + enabled + lastRc + haveInputs + reserved`。
  C# 镜像 `ZfgFgStatsNative` 同序（`ZrFgProxyNative.cs:121-147`），单测断言 `SizeOf==48` 且
  `Enabled/LastReturnCode/HaveInputs` 偏移 `32/36/40`。全仓库 `ZfgFgStats` 的 C# 消费方**只有这一处**（新代码），
  **没有任何按固定 40 字节偏移读的旧代码**（grep `.cs/.ps1/.py` 仅命中新绑定与注释）。

### V10 诚实边界核实 —— 属实，但触发受限项

1. **MSAA 2→1：属实**。本轮两份 Player 日志都有
   `[插帧] 管线 MSAA 2 → 1（FFX 的 depth 必须单采样；仅影响本次会话的内存副本）`，
   代码在 `ZrFrameGenerationFeature.cs:287-292`。
   - 触发条件：**只在 `TrySetEnabled(true)` 安装通道时**（`Install`）执行；
   - 作用对象：`Instantiate` 出来的管线**内存副本**（`QualitySettings.renderPipeline = clone`），磁盘 `URP_*.asset` 不动；
   - 影响面：本次会话内、走该质量档管线的**所有相机**（含关闭 FG 之后），画质可见变化；
   - **关闭 FG 不恢复**（全仓 `grep msaaSampleCount` 只有设置点，无还原点），重启进程才回到资产原值。
2. **反射改 URP 内部字段：属实**。`ZrFrameGenerationFeature.cs:253-279` 反射写
   `m_RendererDataList` / `m_DefaultRendererIndex`（`BindingFlags.Instance | NonPublic`），
   并 `rendererFeatures.Add` 空 pass + `QualitySettings.renderPipeline = clone`。同样是纯运行期、资产零改动。
3. **对玩家是否如实说明**：**没有**。行标签只有能力后缀（`插帧（仅打包后生效/需 D3D12/未装代理/需 URP）`），
   `FrameGenerationSettingOptions` 里没有任何 MSAA/画质下降的文案，设置页也无提示。
   → 按验收清单判据记 **受限项**（不 FAIL，但必须进 HANDOFF）：玩家打开"插帧"后抗锯齿被静默降为 1×，
   且关掉不恢复，UI 上没有任何交代。

### 末尾 3 问

**① `ZfgFgStats` 尾插字段对既有 C# 消费方是否安全？—— 安全。**
全仓只有 `ZrFgProxyNative.ZfgFgStatsNative` 一个消费方（新写，48 字节，单测钉死偏移 32/36/40），
不存在按旧 40 字节结构/固定偏移读取的代码，也没有 extern 声明指向旧符号形状（导出名未变，C ABI 只多了尾巴）。
唯一残留风险是"混装"：若有人拿**旧版**（40 字节托管结构）的绑定 dll 去调**新版** `WrapGetStats`，
原生会多写 8 字节 → 越界。本仓库两侧同批发布、结构无版本字段（`zfg_fg_input.h:50-52` 已注明"改布局两侧同时改"），
建议后续给 `ZfgFgStats` 加 `structSize` 或版本号，把这种混装变成可拒收。

**② MSAA 2→1 是"仅 FG 开启时"还是"全局"，关 FG 后是否恢复？—— 仅开启时改、对整会话生效、关 FG 不恢复。**
只在首次 `TrySetEnabled(true)` 安装通道时改一次，改的是当前质量档管线资产的内存副本
（`QualitySettings.renderPipeline`），因此对该会话内所有相机、包括关掉 FG 之后的帧都生效；
**没有还原路径**（代码里只有 `clone.msaaSampleCount = 1`，无回写原值）。
磁盘资产不变，重启即恢复；同一次会话里再打开设置把开关关掉，MSAA 仍是 1×。

**③ 反射改 URP 内部字段在 Unity 版本升级后会怎样？有无降级路径？—— 会显式失败并降级，不会静默失效。**
`Install` 先 `GetField` 判空，字段改名 → 主动抛 `InvalidOperationException("URP 资产字段改名…")`；
字段类型变了（如 `m_RendererDataList` 换成 `List<>`）→ `InvalidCastException`。两者都被
`TryInstallPipeline` 的 `catch (Exception)` 接住 → `s_installFailed=true` + 原因串 →
`TrySetEnabled` 回滚 `ZFG_FgSetEnabled(0)` + 停提交 + 一次告警 → **纯透传，不崩、不假开**。
降级路径是"功能不可用"，不是"画面错乱"；代价是本次会话永久失败（`s_installFailed` 不再重试），
玩家侧表现为开关拨了但 `stats.enabled` 仍为 0——这一点**只有在能读日志时**才看得见（见下"剩余风险"）。

---

### 施工是否夸大

- 数字**不夸大**：它自报 generated=3596/real=3597，我自跑 3595/3595，同量级同性质；关段 0/1.000 一致；
  六段 196/710837 + 697 + 6 一致；`frameId` 连续、`prepareFail=0` 一致。
- 有两处**表述偏乐观**（不算造假，但需要补正）：
  1. "关 = 纯透传"只在 FFX 侧成立——克隆管线上那个 `ConfigureInput(Motion|Depth)` 的 pass 仍每帧在跑，
     MV/depth 两张纹理关掉后照样生成（这也是 V2"关段帧率 59.96"能成立的原因：60 上限下测不出这笔开销）。
     施工报告 §6.6 提了 R11 的"一次内部合成拷贝"，但没提这笔"MV/depth 继续算"的常驻成本。
  2. "MSAA 只影响本次会话的内存副本"属实，但**没写"关掉 FG 也不恢复"**，也没写 UI 不告知——两处已计入受限项。
- 过程上有一处需要记录：`TempLogs/r9_06/player/R906_Data/Managed/ZFrame.Render.dll` 的时间戳是 15:10，
  而 `R906.exe` 是 14:59 构建——**Player 不是最后一次干净全量构建，是构建后替换了一个托管 dll**。
  我核对了两版 dll 的判别性符号（`_CameraDepthTexture`/`_MotionVectorTexture`/`ZFG_*`/`m_RendererDataList`/
  `OnCameraRendered`/`TryInstallPipeline`/`PrimeDisabled` 全部一一对应），且自跑数字与代码读法自洽，
  判定风险低；但**正式交付前应做一次干净全量构建**并复跑本清单。

### 剩余风险（建议进 HANDOFF）

1. **受限项**：MSAA 2→1 属可见画质变化，UI 无任何告知，且关 FG 不恢复——建议行标签或设置页加一句
   "开启后抗锯齿将降为 1×（本会话内不恢复）"。
2. 关掉 FG 之后，克隆管线上的 MV/depth 采集仍在每帧执行（常驻小开销，60 上限下测不出）。
3. "开关拨动 → 插帧"的端到端只在服务层验证（夹具直接调 `IFrameGenerationSettings`）；
   真正"点 UI 里的 Toggle"这条链只有 EditMode 用例（`ApplyRowFromRegistry_GoesThroughTheSameEntry`），
   本机无交互桌面，**没有做过 Player 里的真实点击验证**。
4. `ZLog` 默认频道白名单为空 → 生产包里"为什么不可用""开启失败已回退"这类一次告警**不会出现在 Player 日志**，
   排障只能靠设置页标签与 `stats`。
5. `ZfgFgStats` 无版本/尺寸字段，旧绑定混装会 8 字节越界（当前仓库无此组合）。
6. 单相机假设、蒙皮物体 MV 走 fallback、60fps 上限下测性能——均为施工已披露的既有边界，本轮未扩大验证。
