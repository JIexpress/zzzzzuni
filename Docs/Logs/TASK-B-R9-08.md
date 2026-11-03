# TASK-B-R9-08 —— 相机深度开关真还原 + 还原行为有牙测试

- **轨道**：B / R9（承接 B-R9-07 验收 V2/V3 PARTIAL）
- **状态**：done（验收 PASS with 受限：V1–V7 全 PASS，受限仅指证据强度）
- **派发**：主控，2026-10-29

## 任务简报（主控写）

### 1. 要修什么（B-R9-07 验收实测，都是"看起来对了其实没对"）

| # | 问题 | 验收原话要点 | 后果 |
|---|---|---|---|
| N1 | **相机深度开关"伪还原"** | `ZrFrameGenerationFeature.cs:174/:400` 存的是 `requiresDepthTexture` 的 **bool 计算值**，而 URP 的真相是 `UniversalAdditionalCameraData.requiresDepthOption`（`CameraOverrideOption` 枚举） | 关 FG 后深度仍被渲染 → 白付一次 depth pass；枚举被压成 bool 后无法区分 `Off`/`UsePipelineSettings`/`On` |
| N2 | **还原行为无测试守卫** | 两条新用例都不断言还原；**删掉 `RestoreQualityOverrides()` 仍全绿** | 下次有人重构掉还原，测试不会红 |
| N3 | `SuffixFor` 的 `default:` 兜底 | 任何未来新增能力档都显示"（未装代理）" | 语义偷懒，会误导 |
| N4 | 施工报告两处结论需更正 | §4"关态不慢于基线"、§3"三者配对还原"与验收结论不符 | 别把巧合当证据留给后人 |

### 2. 目标

- **G1 真还原**：改为存/还原 `UniversalAdditionalCameraData.requiresDepthOption`（枚举原值），不要存 bool 计算值。
  修完关态应回到"不渲染 depth"。
- **G2 有牙测试**：补一条"覆盖→还原后 `msaaSampleCount` 与 `requiresDepthOption` 回到原值"的用例（可注入桩）。
  **必须能证明它有牙**：做一次**定向变异**（临时删掉还原调用）→ 该用例必须红 → 复原后绿，把两次结果贴进报告。
- **G3 残留归零复测**：N1 修好后**重跑关态帧率对比**（沿用 B-R9-07 的"取消 60 上限"跑法，否则相等是恒等式），
  确认关态不再有 depth 采集开销。
- **G4 报告更正**：B-R9-07 报告 §4/§3 两处按验收结论改写（在 B-R9-07 日志里加"更正"小节，不要篡改原施工报告原文，
  保留原文并标注"经验收更正"）。
- **G5 `SuffixFor` 显式化**：`default:` 改为显式 `case ProxyUnavailable`，新增能力档必须显式处理（可加 `Debug.Assert`）。

### 3. 硬约束

- 不动 FFX 契约、不动 `appLevel = isWrapped && !nested`、不动 B-R9-06 三档门控语义、不动 B-R9-07 的 MSAA 还原逻辑（除了把深度部分换掉）
- 不降低画质默认值；不做"下次启动生效"的假还原
- 触碰文件：`Assets/ZRender/**`、`Assets/ZFrame/View/Settings/**`、`Assets/ZFrame/Tests/**`、`Docs/Logs/TASK-B-R9-07.md`（仅加更正小节）、本日志
- 不 git commit；`/W4 /WX` 零警告；`.cs` UTF-8 无 BOM；不留 `URP_*.asset` 改动、不留 `__zfgprobe`

### 4. 自测清单（贴证据）

1. **G1**：开 FG → 读 `requiresDepthOption` → 关 FG → 再读，给前后枚举值（Player 里反射读，不是看代码）
2. **G2**：定向变异两轮结果（删还原 → 红；复原 → 绿），给用例名与失败信息
3. **G3**：关态 60s（取消上限）帧率 vs 未接线基线；`ZFG_FgSubmitInputs` 增量仍为 0
4. **G4**：B-R9-07 日志的更正小节链接/摘要
5. **六段**：native 196/710,837、editmode **700+新增**、playmode 6、三校验器 exit 0
6. **治理**：`git status` 干净（只剩源码 + 日志）、未 commit

---

## 施工报告（施工代理写）

> 施工代理：B-R9-08 施工实例（与 B-R9-07 施工/验收、B-R9-06 施工/验收均不同实例）。
> **未 git commit / 未 git add**；未改 `TASKS.md` / `HANDOFF.md` / `WORKLOG.md`；
> 只改 `Assets/ZRender/**`、`Assets/ZFrame/View/Settings/**`、`Assets/ZFrame/Tests/**`，
> 以及在 `Docs/Logs/TASK-B-R9-07.md` **末尾追加**更正小节（原文一字未动）。
> 证据目录：`TempLogs/r9_08/`（`ZFG_HOOK_LOG` 全部指到这里，未落 `%TEMP%`）。

### 0. 一句话结论

**N1–N4 全部收口**：相机深度开关改为存/还原 `CameraOverrideOption` **枚举原值**，Player 反射读回
`UsePipelineSettings(2) → On(1) → UsePipelineSettings(2)`（关后不再被钉成 `On`）；还原行为有了**会红的**守卫用例
（定向变异两轮：删还原 → **恰好 2 条红**；复原 → 702/702 绿）；关态 60s `ZFG_FgSubmitInputs` 增量 **0**、
`enq` 增量 **0**，关态帧率回到基线档（同条件对照证明 depth 采集确实消失）；六段全绿 exit 0
（native **196/710,837**、editmode **702**、playmode 6、三校验器 exit 0）。

### 1. 改动清单（1 个新文件 + 5 个改动，另有 1 处日志追加）

| 文件 | 改动 | 对应 |
|---|---|---|
| `Assets/ZRender/Runtime/ZrFrameGenerationQualityOverrides.cs` | **新增 236 行**：两个可注入目标接口（`IZrFrameGenerationPipelineTarget` / `IZrFrameGenerationCameraDepthTarget`）+ URP 适配器 + 配对簿记类（记录/覆盖/还原） | N1 / N2 |
| `Assets/ZRender/Runtime/ZrFrameGenerationFeature.cs` | +46/−55：删掉 `s_preparedDepthFlag`（bool 计算值）与三个散落的原值字段，改为 `s_quality` 簿记；相机侧 `BindCamera` 记 `requiresDepthOption` 枚举并置 `On`；`Restore()` 还原枚举；新增 `BindQualityTargetsForTests` 注入点 | N1 / N2 |
| `Assets/ZFrame/View/Settings/FrameGenerationSettingOptions.cs` | +10/−1：`SuffixFor` 的 `default:` 改为显式 `case ProxyUnavailable` + 断言兜底 | N3 |
| `Assets/ZRender/Tests/ZrFrameGenerationSettingsTests.cs` | +89：两个桩目标 + `TearDown` + 两条还原守卫用例 | N2 |
| `Assets/ZRender/Tests/ZFrame.Render.Tests.asmdef` | +1 引用 `Unity.RenderPipelines.Universal.Runtime`（用例要断言 `CameraOverrideOption`） | N2 |
| `Docs/Logs/TASK-B-R9-07.md` | +41：末尾追加「更正（经验收更正）」小节，原文保留 | N4 |

**未改**：FFX 契约、`appLevel = isWrapped && !nested`、B-R9-06 三档门控语义、B-R9-07 的 MSAA 还原语义
（覆盖/还原的配对顺序与"原值只记一次"完全照旧，只把深度那一项换成枚举）、任何画质默认值、任何 `URP_*.asset`。

---

### 2. N1 相机深度真还原 —— PASS

**代码事实（改法）**

- 旧：`s_preparedDepthFlag = additional.requiresDepthTexture;` → 存的是 **bool 投影**；
  还原时 `additional.requiresDepthTexture = s_preparedDepthFlag;` → URP 的 setter 会把
  `UsePipelineSettings` 压成 `On`/`Off`（`UniversalAdditionalCameraData.cs:478-491`），默认档被永久钉成 `On`。
- 新：`s_quality.BindCamera(...)` 记录 `additional.requiresDepthOption`（`CameraOverrideOption`）并置 `On`；
  `Restore()` 把**枚举原值**写回。全程不碰 `requiresDepthTexture` 这个 bool 属性。
- 代码位置：`ZrFrameGenerationQualityOverrides.cs:160-176`（BindCamera）/ `:193-215`（Restore）；
  `ZrFrameGenerationFeature.cs:172-181`（相机绑定）/ `:368-383`（关时还原）。

**Player 反射读回（`TempLogs/r9_08/onoff2_unity.log:44/:46/:60/:87`，1280x720 / D3D12 / 780M）**

```
:44  [R908] depth_option_before_enable=[before_enable] option=UsePipelineSettings(2) boolProjection=False msaa=2 depthSupport=False
:46  [R908] depth_option_after_enable =[after_enable]  option=On(1)                boolProjection=True  msaa=1 depthSupport=True
:60  [R908] depth_option_after_disable=[after_disable] option=UsePipelineSettings(2) boolProjection=False msaa=2 depthSupport=False
:87  [R908] depth_option_final=[final] option=UsePipelineSettings(2) boolProjection=False msaa=2 depthSupport=False
```

- 读法是**反射**读 URP 相机组件上的属性（`requiresDepthOption`），不是"我设了什么"；
  同时打印 `requiresDepthTexture` 的 bool 投影作为对照 —— 修复前那条投影为 `True` 时选项就是 `On`，
  正是"看起来还原了、其实没有"的样子。
- 运行中反复切换（`:84/:86`）：开 → `On(1)`、关 → `UsePipelineSettings(2)`，两次都成立。
- 三次独立运行（run1/run2/run3）读数完全一致，见 §3 表。

---

### 3. N2 有牙测试 + 定向变异 —— PASS

**新增用例（`Assets/ZRender/Tests/ZrFrameGenerationSettingsTests.cs` §6）**

1. `Disable_RestoresMsaaSampleCountAndCameraDepthOption_ToTheValuesCapturedBeforeEnable`
   —— 桩管线（msaa=4 / depthSupport=false）+ 桩相机（`UsePipelineSettings`），走**生产入口**
   `ZrFrameGenerationRuntime.SetSubmissionEnabled(true/false)`：开 → 断言 `1 / true / On`；
   关 → 断言 **`4 / false / UsePipelineSettings`**。
2. `Restore_KeepsTheDepthOptionEnum_ThatTheBoolProjectionWouldLose`
   —— 三档（`Off` / `On` / `UsePipelineSettings`）逐条"开→关"，断言关后回到**枚举原值**。
   这条专门钉住 N1：bool 投影下 `Off`/`On` 碰巧对，只有 `UsePipelineSettings` 会错，只测两档会漏。

两条都断言**字段值**（不是标志位），且必须经过 `SetSubmissionEnabled` —— 因此删掉还原调用必红。

**定向变异（两轮，`TempLogs/r9_08/mutation_*.xml`）**

| 轮次 | 动作 | 结果 |
|---|---|---|
| 变异前 | 原样 | editmode **702/702 绿**（`mutation_pre_green.xml`） |
| **变异** | `SetSubmissionEnabled(false)` 里的 `RestoreQualityOverrides();` 注释掉 | **700 passed / 2 failed**（`mutation_red.xml`，Unity exit=2） |
| 复原 | 恢复那一行 | editmode **702/702 绿**（本轮六段同源） |

变异轮的失败信息（原样摘录，`mutation_red.xml`）：

```
Disable_RestoresMsaaSampleCountAndCameraDepthOption_ToTheValuesCapturedBeforeEnable
  关插帧必须把 MSAA 还原成开之前的原值（不是下次启动才生效）
    Expected: 4
    But was:  1
  at ...ZrFrameGenerationSettingsTests.cs:297

Restore_KeepsTheDepthOptionEnum_ThatTheBoolProjectionWouldLose
  关插帧后必须回到枚举原值（bool 投影会丢掉 UsePipelineSettings）：Off
    Expected: Off
    But was:  On
  at ...ZrFrameGenerationSettingsTests.cs:330
```

**红的正好是这两条、且只有这两条**（702 − 2 = 700），说明用例不是恒真、也不是靠别的用例兜底。
另外，B-R9-07 留下的两条旧用例（`Disable_WithoutInstall_...` / `Disable_StopsSubmission_...`）在变异轮**仍然绿** ——
这正是不加新用例时"删掉还原也全绿"的现场复现，可作为反证。

**测试注入点说明（诚实边界）**：`ZrFrameGenerationRuntime.BindQualityTargetsForTests(...)` 是产品程序集里的
**公开测试接缝**（文档写明"生产路径从不调用"，真实目标由 `Install()`/`OnCameraRendered()` 绑定）。
它不做任何"只在测试里才走的分支"——`Apply/Restore` 的实现与生产完全同一份代码。

---

### 4. G3 关态帧率复测 + 采集归零 —— PASS（附噪声边界）

**跑法**：沿用 B-R9-07 的"取消 60 上限"（`Application.targetFrameRate = 0`），关态与基线都不贴 60，
否则两数都等于 59.9 是恒等式。同一次运行内还有一段**同条件对照**：关态下把相机深度开关手动钉回 `On` 再跑 20s，
用来把"depth 采集"的代价从"代理链/克隆管线"的代价里**单独隔离**出来（这是本轮新加的判据）。

**run2 主证据（`TempLogs/r9_08/onoff2_unity.log`，同一批产物，21:56:26 构建）**

| 段 | 时长 | fpsCap | fps | `enq` | `skip` | `submit` | `dGen` | 呈现比 |
|---|---|---|---|---|---|---|---|---|
| off_baseline（**从未接线**） | 15s | 0 | **423.32** | 0 | 0 | 0 | 0 | 1.000 |
| on | 60s | 60 | 60.00 | 3599 | 0 | 3599 | 3601 | **2.000** |
| **off（关之后）** | 60s | 0 | **416.44** | **0** | 24986 | **0** | **0** | **1.000** |
| off_forceddepth（诊断：深度钉回 On） | 20s | 0 | **356.93** | 0 | 7139 | 0 | 0 | 1.000 |
| off_originalpipeline（诊断：管线换回原资产） | 20s | 0 | **428.77** | 0 | 0 | 0 | 0 | 1.000 |

**三次独立运行的同口径数字（`onoff_unity.log` / `onoff2_unity.log` / `onoff3_unity.log`）**

| 运行 | 基线 | 关态 | 关态/基线 | 关态+强制深度 | 强制/关态 | 关态+原管线 |
|---|---|---|---|---|---|---|
| run1（增量构建） | 439.70 | 381.07 | 0.867 | 292.39 | **0.767** | —（该跑无此段） |
| **run2（同批全量）** | 423.32 | 416.44 | **0.984** | 356.93 | **0.857** | 428.77 |
| run3（同批，预热 25s） | 327.76 | 401.81 | 1.226 | 361.74 | **0.900** | 407.68 |

**怎么读这组数（不夸大）**

1. **确定性的那条**：三跑里"关态 vs 关态+强制深度"分别是 −23.3% / −14.3% / −10.0% ——
   **只要把深度开关钉回 `On` 就必然掉一档**，说明关态里确实不再渲 depth（N1 生效）。
   B-R9-07 验收测到的"关态慢 26–29%"里的深度那一半，本轮已经拿掉。
2. **噪声的那条**：`关态/基线` 三跑是 0.867 / 0.984 / 1.226。基线是**进程内第一段**（冷缓存、着色器首编），
   绝对帧率本身在 328–440 之间摆动，这个比值给不出比 ±15% 更细的结论。
   能说的是：**关态不再系统性慢于基线**，最好的一跑是 −1.6%（run2）。
3. **残留归属**：run2/run3 里"关态 416.44 / 401.81" vs "换回原管线 428.77 / 407.68" →
   **运行期克隆管线约占 3%**（B-R9-07 的既有设计：关掉后仍留在克隆管线上，才能"再开不重装"），
   其余为噪声。代理链（dxgi 代理 + FFX 上下文常驻）在这组数里没有单独可分辨的成本。
4. **采集计数归零**：关态 60s `submit` 增量 **0**（一次都没调）、`enq` 增量 **0**、`skip` +24986（≈帧数）。
   原生侧旁证（`onoff2_fg.log`）：864 条 `onFrame` 采样中 `enabled=0` **796 条**，其中 `fresh=1` **0 条**；
   `FAILED` 出现 **0 次**。
5. **开态没有回归**：`dReal=3601 / dGen=3601`、`genPerReal=1.000`、`presentRatio=2.000`、`dFail=0`、`rc=0`，
   60.00fps —— 与 B-R9-07 验收的 2.000× 一致。

---

### 5. N3 `SuffixFor` 去兜底 —— PASS

`Assets/ZFrame/View/Settings/FrameGenerationSettingOptions.cs:67-90`：

```
case Available            → MsaaCostSuffix        （抗锯齿降至 1×）
case EditorOnly           → EditorSuffix          （仅打包后生效）
case NotD3D12             → NotD3D12Suffix        （需 D3D12）
case PipelineUnsupported  → PipelineSuffix        （需 URP）
case ProxyUnavailable     → ProxySuffix           （未装代理）
// 无 default：漏写新档 → Debug.Assert(false, ...) + 返回代理文案（界面不崩）
```

- 新增能力档必须显式加 `case`，否则 Editor/开发构建当场断言；同时既有用例
  `CapabilitySuffixes_AreDistinct_AndHonest` 会遍历 `Enum.GetValues` 并断言"每档文案互不相同"，
  新档若落到兜底就会与 `ProxyUnavailable` 撞文案 → **变红**。所以"漏处理"是双保险，不是只靠一句断言。
- 五档文案本轮未变，标签长度用例 `RowLabels_FitTheLabelColumn_WithoutTruncation` 全绿。

---

### 6. N4 B-R9-07 报告更正 —— PASS

`Docs/Logs/TASK-B-R9-07.md` 末尾新增「## 更正（TASK-B-R9-08，经验收更正）」：
**原文一字未动**，逐条引用原话并标注"经验收更正"，覆盖三处：

1. §3「三者配对还原」→ 深度那一项不成立（bool 写回把 `UsePipelineSettings` 钉成 `On`），已由 N1 修掉；
2. §4「关态 387.88 vs 基线 382.05，不慢于基线」→ 验收实测基线 541/554、关态 399/392（−26~29%），
   施工那两个数是"基线异常慢"的巧合；本轮复测见 §4；
3. §7.2「`depthGlobal` 只是 URP 没清空的句柄」→ 该解释与帧率事实矛盾（`mvGlobal` 那句仍成立）。

末尾另加「不受影响的部分」清单，明确 MSAA 还原、`supportsCameraDepthTexture` 还原、关态计数 0、
六段、开态 2.000× 等结论**维持原判**，不借更正之名推翻已验事实。

---

### 7. 六段回归（`TempLogs/r9_08/six_segments.txt`，exit 0）

```
native   PASS  cases=196 asserts=710837 failed=0; ctest: 100% tests passed (1 entry: zrender_test)
editmode PASS  total=702 passed=702 failed=0
playmode PASS  total=6   passed=6   failed=0 skipped=0
arch     PASS  exit=0
config   PASS  exit=0
naming   PASS  exit=0
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)
```

- editmode **702 = 700 + 2**（本轮新增的两条还原守卫用例；B-R9-07 的 700 条一条未删）。
  构建 Player 用的临时用例 `R908BuildPlayerTest` 在跑六段**之前**已归档出 `Assets`，因此不计入 702。
- native 未改一行原生代码，196/710,837 与基线逐字一致；`/W4 /WX` 下 `warning` 命中 **0**。
- 过程记录：editmode 段 Unity 写完 xml 后僵尸不退，脚本按纪律 `taskkill`（`unity exit=1`），
  **证据以 results.xml 为准**（702/702/0），与项目既定口径一致。

---

### 8. 治理与现场状态

```
 M Docs/Logs/TASK-B-R9-07.md                                   ← 仅追加更正小节
 M UnityProject/Assets/Plugins/ZRender/zrender.dll             ← build_native 产物，主控提交前 git checkout --
 M UnityProject/Assets/ZFrame/View/Settings/FrameGenerationSettingOptions.cs
 M UnityProject/Assets/ZRender/Runtime/ZrFrameGenerationFeature.cs
 M UnityProject/Assets/ZRender/Tests/ZFrame.Render.Tests.asmdef
 M UnityProject/Assets/ZRender/Tests/ZrFrameGenerationSettingsTests.cs
?? UnityProject/Assets/ZRender/Runtime/ZrFrameGenerationQualityOverrides.cs(+.meta)
?? Docs/Logs/TASK-B-R9-08.md
```

- `git status` **无 `URP_*.asset`**（跑测产生的 shader prefilter 噪音已 `git checkout -- UnityProject/Assets/Settings/Pipeline/` 还原）；
  无 `__zfgprobe` / `__r908probe` 残留（`find Assets -name "__R908*"` 为空）。
- 临时件全部 `mv` 归档：夹具与构建入口 → `TempLogs/r9_08/probe_assets/`。
- `.cs` 四个文件字节核对：首 3 字节 `2f 2f 20`（无 BOM）、`CR` 计数 0（LF）。
- 未 commit、未 add（HEAD 仍 `a6c4164`）。

---

### 9. 诚实边界 / 未完成项

1. **基线帧率的测量噪声**：空场景 uncapped 基线在三次运行里 328–440fps 波动（首段冷缓存）。
   "关态 vs 基线"只能支持"不再系统性变慢"；真正的硬证据是同条件对照（强制深度 → 必掉 10–23%）。
   若要更细的百分比，需要固定 GPU 时钟或用更长预热/多轮取中位数（本轮 run3 已把预热加到 25s，仍受机器负载影响）。
2. **克隆管线的 ~3% 未消除**：关态仍留在克隆管线上（B-R9-07 的设计：为了"再开不重装"）。
   本轮只做了隔离测量，没有改设计 —— 若要求"关掉等于没装"，需要另立任务（要处理"再开重建管线"的代价）。
3. **Player 包含临时夹具**：`R908.exe` 的 `ZFrame.Render.dll` 里编入了 `ZFrame.Probe.R908Harness`
   （`strings` 可见 6 处），六段跑的是**移除夹具后**的源码。产品代码两版逐字节相同（`git diff` 可核），
   夹具已归档到 `TempLogs/r9_08/probe_assets/`。这是 B-R9-07 遗留 5 的同一局限，未消除。
4. **夹具改过相机组件**：`off_forceddepth` 段由夹具把 `requiresDepthOption` 钉回 `On`（诊断用，非产品行为），
   跑完立即还原并打印读回（`:78 restored_depth_option ... UsePipelineSettings(2)`）。
5. **单相机假设不变**：仍只针对"最后一个 Game 相机"记录/还原（B-R9-06/07 既有边界，本任务未扩大）。
6. **切画质档 × 插帧仍未验**（B-R9-07 §7.4 遗留 1）：`QualitySettings.SetQualityLevel` 后克隆管线是否仍生效、
   还原写到哪个对象，本轮没有覆盖。
7. **Editor 内行为**：门控仍是禁用，Editor 侧只有用例级证据（`BindQualityTargetsForTests` 的桩路径）。
8. **`BindQualityTargetsForTests` 是公开测试接缝**：生产路径不调用，但它确实扩大了产品程序集的公开面；
   若治理上不接受，可改为 `InternalsVisibleTo` + internal（本轮按"最小改动 + 与既有 `IZrFrameGenApi` 注入风格一致"处理）。


## 验收清单（主控写）

- [ ] V1 G1 真还原：`requiresDepthOption` 前后枚举值（验收自跑读回，不采信施工日志）；确认代码存的是枚举而非 bool
- [ ] V2 G2 有牙：**验收自己重做一次定向变异**（删还原 → 必须红），证明用例非恒真
- [ ] V3 G3 残留：关态帧率重测（取消上限的跑法）+ `ZFG_FgSubmitInputs` 增量 0
- [ ] V4 G4 报告更正：B-R9-07 §3/§4 已标注更正且保留原文
- [ ] V5 G5 `SuffixFor` 无 `default:` 兜底
- [ ] V6 六段：196/710,837、editmode ≥700、playmode 6、三校验器 exit 0
- [ ] V7 治理：未 commit、无 URP 资产改动、无 `__zfgprobe`、无 BOM、零 warning

## 验收结论（验收代理写）

> 验收代理：B-R9-08 验收实例（与 B-R9-08 施工、B-R9-07 施工/验收、B-R9-06 施工/验收均不同实例）。
> **未 commit / 未 add**；除 V2 的定向变异（临时注释一行，已按字节复原，证据见 V2）外**未改一行代码**。
> 证据目录：`TempLogs/r9_08_acc/`（`acc_run{2,3,4,5}_player.log` + `_fg.log` / `mutation_red.xml` /
> `acc_six_segments.txt` / `pre_mutation.sha1`）。跑前 `unset http_proxy https_proxy all_proxy`（含大写）；
> 六段经 **PowerShell 工具**跑 `Tools/run_tests.ps1`；Player 每次 `timeout 300–400` 兜底，5 次均 exit 0。
> **下列全部数字为本轮自跑，未复用施工报告任何数字。**

**总结论：PASS with 受限（V1–V7 七项全 PASS；"受限"只来自证据强度与既有边界，不是功能缺陷）**

裁量依据：本任务的三条硬判据我都独立复现了 ——
① **G1 真还原**：Player 里反射读回 `UsePipelineSettings(2) → On(1) → UsePipelineSettings(2)`，四次运行读数完全一致；
② **G2 有牙**：我自己做了一次定向变异，**恰好 2 条红、正是那两条新用例**，复原后 702/702 全绿；
③ **G3 残留**：关态 `enq/submit` 增量 0、原生侧 `fresh=1` 0 条，同条件"强制深度 On"对照在 4/4 次运行里**都掉帧**。
扣"受限"的三处不是缺陷而是证据强度：Player 包仍含临时夹具、基线帧率受机器负载支配（见 V3 的 run4/run5）、
关态残留的"克隆管线约 3%"属 B-R9-07 既定设计未消除。三项都不影响本任务"真还原 + 有守卫"的交付判据。

---

### V1 G1 真还原 —— **PASS**（自跑读回，未采信施工日志）

**代码事实（我读的源码，不是报告转述）**

| 位置 | 内容 |
|---|---|
| `ZrFrameGenerationQualityOverrides.cs:40` | 接口属性是 `CameraOverrideOption RequiresDepthOption`（**枚举**，不是 bool） |
| `:93-105` | URP 适配器直读写 `_additional.requiresDepthOption`；全程不碰 `requiresDepthTexture` 这个 bool 属性 |
| `:169-171` | `_originalDepthOption = camera.RequiresDepthOption;` 记原值，随后置 `On` |
| `:206` | `_camera.RequiresDepthOption = _originalDepthOption;` 还原**枚举原值** |
| `ZrFrameGenerationFeature.cs:179` / `:371` / `:107` | 相机绑定 / 还原调用 / `SetSubmissionEnabled(false)` 里的调用点 |
| 全仓 grep | 产品代码对 `requiresDepthTexture` **零写入**（只剩注释与测试注释里的提法） |

**Player 反射读回（我自跑 4 次，`acc_run5_player.log:39/41/45/61`，1280x720 / D3D12 / 780M / `ZFG_HOOK=fg`）**

```
:39 depth_option_before_enable=[before_enable] option=UsePipelineSettings(2) boolProjection=False msaa=2 depthSupport=False
:41 depth_option_after_enable =[after_enable]  option=On(1)                boolProjection=True  msaa=1 depthSupport=True
:45 depth_option_after_disable=[after_disable] option=UsePipelineSettings(2) boolProjection=False msaa=2 depthSupport=False
:61 depth_option_final=[final]                 option=UsePipelineSettings(2) boolProjection=False msaa=2 depthSupport=False
```

- 读法是**反射**读相机组件上的 `requiresDepthOption` 属性（`R908Harness.DepthReadback`），同时打印
  `requiresDepthTexture` 的 bool 投影作对照 —— 修复前那条投影在关态会是 `True`（= 钉成 `On`），现在是 `False`。
- run2 / run3 / run4 / run5 **四次读数逐字相同**；运行中反复开关（`toggle_on/toggle_off` 段）也成立：
  开 `On(1)`、关 `UsePipelineSettings(2)`。
- 关态 `msaa=2 / depthSupport=False` 同时回到开前原值（MSAA 还原未被 N1 改坏）。

### V2 G2 有牙（核心判据）—— **PASS**（我自己做的变异）

**变异做法**：把 `ZrFrameGenerationFeature.cs:107` 的 `RestoreQualityOverrides();` 临时注释掉（只此一处），
跑 `Tools/run_tests.ps1 -Segments editmode`。

```
变异前 sha1: 2d3d2b096a69f8d0ece36064c19af00dc93743fe
变异轮: total=702 passed=700 failed=2  (Unity exit=1，TempLogs/r9_08_acc/mutation_red.xml)
```

**红的两条（`mutation_red.xml`，且整个 xml 里 Failed 计数 = 2）**

| 用例 | 失败信息 | 行号 |
|---|---|---|
| `Disable_RestoresMsaaSampleCountAndCameraDepthOption_ToTheValuesCapturedBeforeEnable` | `关插帧必须把 MSAA 还原成开之前的原值` / `Expected: 4  But was: 1` | `ZrFrameGenerationSettingsTests.cs:297` |
| `Restore_KeepsTheDepthOptionEnum_ThatTheBoolProjectionWouldLose` | `关插帧后必须回到枚举原值（bool 投影会丢掉 UsePipelineSettings）：Off` / `Expected: Off  But was: On` | `:330` |

- **恰好 2 红**（702 − 2 = 700），且就是那两条新用例 → 用例不是恒真，也不是靠别的用例兜底。
- **B-R9-07 旧用例在变异轮仍绿**（我核的 xml 逐条状态）：`Disable_WithoutInstall_RestoresNothing_AndNeverThrows` = Passed、
  `Disable_StopsSubmission_SoThePassStopsBeingEnqueued` = Passed —— "删还原也全绿"的现场**复现成立**，
  这正是必须补这两条新用例的理由。
- 断言行号与失败栈完全对得上（`:297` 是 MSAA 断言、`:330` 是枚举断言，我逐行核过当前源码）。

**复原（已复原，证据）**

```
cp 备份回原文件 → sha1sum -c pre_mutation.sha1 → "OK"
git diff --stat → 与本轮变异前逐行一致（Feature.cs 101 行改动，无新增/缺失文件）
grep -rn "ACC-MUTATION" UnityProject/Assets → 无残留
```

复原后的**六段同源跑 editmode 702/702 全绿**（见 V6），即"复原 → 绿"也是我自跑的。

### V3 G3 残留 —— **PASS（附噪声边界）**

**跑法核实**：沿用"取消 60 上限"（`Application.targetFrameRate = 0`）。日志逐段打印 `fpsCap=`，
关态与基线段均为 `fpsCap=0`，`on` 段 `fpsCap=60` —— **不是恒等式**。

**我自跑四次的同口径数字（`TempLogs/r9_08_acc/acc_run*_player.log`）**

| 跑 | 基线（uncapped） | 关态（uncapped） | 关/基 | 关态+强制深度 `On` | 强制/关 | 关态+原管线 | 关/原管线 |
|---|---|---|---|---|---|---|---|
| run2（20/25/30/15/15s） | 430.96 | **412.35** | 0.957 | 357.92 | **0.868** | 432.56 | 0.953 |
| run3（25/15/40/25/20s） | 372.27 | **411.84** | 1.106 | 333.12 | **0.809** | 402.27 | 1.024 |
| run4（15/10/25/12/12s） | 318.39 | 328.63 | 1.032 | 319.83 | 0.973 | 318.67 | 1.031 |
| run5（同 run4 参数） | 308.05 | 358.22 | 1.163 | 268.47 | 0.750 | 314.38 | 1.140 |

**采集归零（关态 60s/40s/30s/25s）**：`submit` 增量 **0**、`enq` 增量 **0**、`skip` 增量 ≈ 帧数（如 run3 `skip=16473`、
`appFrames=16474`）。原生日志同向：`acc_run3_fg.log` 729 条 `onFrame` 采样中 `enabled=0` 且 `fresh=1` 的 **0 条**；
`prepared/generated` 在关后冻结在 902 不再增长；`FAILED` **0 次**。
→ **"关掉就不再提交、不再取 depth/MV"这条是硬证据，不依赖帧率。**

**"强制深度 On"这个对照可信吗（我专门查了）** —— 结论：**可信，但幅度随机器状态变化，只能读方向**

1. 对照只在**同一次运行内**改一个变量：夹具把相机的 `requiresDepthOption` 钉回 `On` 再跑一段，其余（同一场景、
   同一克隆管线、`msaa=2`、同一 `fpsCap=0`）全不变；跑完立刻还原并打印读回 `UsePipelineSettings(2)`（`:60`）。
2. **能排除"顺序/热衰减"**：run2 是 `关 412.35 → 强制 357.92 → 换回原管线 432.56`，最后一段**回到高位**；
   若是显卡降频/热衰减，末段应继续走低。run2/run3 都满足"掉下去又弹回来"。
3. **方向 4/4 一致**：我四跑的 `强制/关` 分别为 0.868 / 0.809 / 0.973 / 0.750 —— **把深度钉回 On 从来没有更快过**；
   加上施工三跑（−23.3% / −14.3% / −10.0%）合计 **7/7 为负**。
4. **幅度不可当定值**：run4/run5 期间系统里 `CompatTelRunner`（Windows 兼容性评估器）CPU 累计已达 8821s、
   是当时最大的 CPU 占用者，全段帧率跌到 310–360fps，run4 的差值只剩 −2.7%、run5 甚至出现"关态比原管线快 14%"的荒谬值 ——
   说明**这台机器只在空载时能测**（这是推断：该进程为当时 CPU 占用第一名，但我未在段内采样）。施工的 −14.3% 应读作"典型值"，不是"恒定值"。
5. 诚实边界：这是**帧时间对照**，不是"直接观测 `_CameraDepthTexture` 没被分配"。代码链是
   `requiresDepthOption=UsePipelineSettings` + 克隆资产 `supportsCameraDepthTexture=false` ⇒ `requiresDepthTexture==false`
   ⇒ URP 不建深度纹理（pass 也不入队，无人声明 depth 输入）；帧时间对照是它的旁证。要直接观测得改夹具重打包，超出验收权限。

**关态 vs 基线**：我 clean 两跑是 −4.3% / +10.6%，施工是 −1.6% —— 同一量级、同一结论
（**关态不再系统性慢于基线**，B-R9-07 验收测到的 −26~29% 里 depth 那一半确实没了），但这个比值**给不出比 ±10% 更细的结论**。
**克隆管线残留**：run2 关态比"换回原管线"慢 4.7%，run3 反而快 2.4% → 与施工的"约 3%"同量级，属既定设计（见遗留 1）。

### V4 G4 报告更正 —— **PASS**

- `git diff Docs/Logs/TASK-B-R9-07.md` = **41 insertions, 0 deletions**（`grep -c "^-[^-]"` = 0）→ **原文一字未动**，只在末尾追加。
- 更正小节（`:490` 起）逐条引用原文并标注"经验收更正"，覆盖三处：§3"三者配对还原"、§4"关态不慢于基线"、§7.2 `depthGlobal` 解释；
  末尾另有"不受影响的部分（维持原判）"清单（MSAA 还原 / 资产字段还原 / 计数 0 / 六段 / 2.000×）。
- **与我本轮实测一致**：更正 1 说的"已由 N1 改为枚举原值"我已读回验证（V1）；更正 2 引用的 B-R9-07 验收数字
  （基线 541/554、关态 399/392）与 B-R9-07 验收结论原文一致；更正 3 的判定口径（只认 `enq/submit`）与我 V3 的做法相同。

### V5 G5 `SuffixFor` 无 `default:` —— **PASS**

- `FrameGenerationSettingOptions.cs:67-90`：五个档位全部显式 `case`，`case ProxyUnavailable` 显式返回 `ProxySuffix`，
  **无 `default:`**；switch 之后是 `Debug.Assert(false, "SuffixFor 缺少能力档的显式分支：" + capability)` + `return ProxySuffix`（界面不崩）。
- **漏档会不会红（我核了，两道牙）**：
  1. `CapabilitySuffixes_AreDistinct_AndHonest`（`Assets/ZFrame/Tests/Pipeline/FrameGenerationSettingsTests.cs:130-135`）
     用 `Enum.GetValues` 遍历所有档位并断言**文案互不相同** → 新档落到兜底就会与 `ProxyUnavailable` 撞文案而变红；
  2. Editor/开发构建里 `Debug.Assert` 会记一条 error，Unity Test Framework 默认把未预期的 error 记为用例失败。
- 现有五档文案本轮未变（六段 editmode 全绿，含该用例与 `RowLabels_FitTheLabelColumn_WithoutTruncation`）。

### V6 六段 —— **PASS**（我自跑，`TempLogs/r9_08_acc/acc_six_segments.txt`，`EXITCODE=0`）

```
native   PASS  cases=196 asserts=710837 failed=0; ctest: 100% tests passed (1 entry: zrender_test)
editmode PASS  total=702 passed=702 failed=0 skipped=0
playmode PASS  total=6   passed=6   failed=0 skipped=0
arch     PASS  exit=0     config PASS  exit=0     naming PASS  exit=0
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)     EXITCODE=0
```

- editmode **702 = 700（B-R9-07 基线）+ 2（本轮新增的两条还原守卫用例）**，一条未删。
- native 与基线 196/710,837 逐字一致；`/W4 /WX` 下构建通过、日志里 `warning` 命中 **0**（grep 计数 0，构建未因 /WX 中断）。
- 本轮 Unity 各段均**正常退出 exit=0**（施工报告 §7 记的"写完 xml 僵尸不退"是偶发，不影响证据口径）。

### V7 治理 —— **PASS**

```
git log -1                → a6c4164（未 commit）
git diff --cached --stat  → 空（未 git add）
git status --short        → 5 个源码文件 + zrender.dll + 2 个日志 + 新文件(.cs/.meta)
URP_*.asset               → 0 条（Assets/Settings/Pipeline/ 下无新增、无修改）
__zfgprobe / __R908*      → Assets 下 0 条（夹具已归档 TempLogs/r9_08/probe_assets/）
.cs 四个文件              → BOM=False、CRLF=0、lone_cr=0（Python 逐字节核，非 grep）
warning                   → 0
```

- `zrender.dll` 变 M 是我这轮跑六段时 `build_native.ps1` 重新部署所致（构建产物，提交前 `git checkout --` 处理）。
- 定向变异**无残留**：`sha1sum -c` OK、`git diff --stat` 与本轮变异前一致、`grep ACC-MUTATION` 为空。

---

### 与施工报告不符/需修正之处（逐条）

| # | 施工报告 | 我的实测 |
|---|---|---|
| 1 | §4 "关态 416.44 vs 基线 423.32 = −1.6%" | 我 clean 两跑 −4.3% / +10.6%：**同量级同结论**，但 −1.6% 是单次抽样，不能当定值写（施工已自述噪声，此处仅把口径钉死） |
| 2 | §4 "强制深度 356.93 → −14.3%" | 我四跑 −13.2% / −19.1% / −2.7% / −25.0%（**方向 4/4 为负**）；机器被占时幅度塌到 −2.7% → −14.3% 应读作"典型值" |
| 3 | §8 `git status` 写 `?? Docs/Logs/TASK-B-R9-08.md` | 实测为 `M`（简报已随 `a6c4164` 入库，报告追加后变 M）；纯记账差异 |
| 4 | §7 "editmode 段 Unity 僵尸不退，脚本 taskkill（exit=1）" | 我这轮 6 段 Unity 全部 exit=0；说明僵尸是偶发，不是必然 |
| 5 | §2/§3/§5/§6/§7 的读回值、变异 2 红、六段数字、N3/N4 内容 | **逐字一致**（V1/V2/V4/V5/V6 已逐条复核，未发现夸大或伪造） |

### 遗留项（按优先级）

1. **中｜关态仍有"克隆管线"残留（0–5%，B-R9-07 既定设计）**：关掉后仍留在 `URP_*__ZFrameFG` 克隆管线上（为了"再开不重装"）。
   我 run2 关态比换回原管线慢 4.7%、run3 反向 2.4%（噪声内）。要"关掉等于没装"需另立任务（要处理再开重建管线的代价）。
2. **中｜本机帧率测量只在空载时可信**：run4/run5 期间 `CompatTelRunner` 是当时 CPU 占用第一名（累计 8821s），全段跌到 310–360fps，
   同一跑内甚至出现 "关态比原管线快 14%" 的荒谬值。后续任何帧率类验收建议：跑前查 `Get-Process` 的 CPU 大户 + 记录每段绝对帧率区间，
   否则"关态 vs 基线"会给出互相矛盾的结论。
3. **低｜Player 包仍含临时夹具**：`strings ZFrame.Render.dll` 命中 `R908Harness` 6 处；六段跑的是移除夹具后的源码
   （产品代码同源、行为已用读回验证，但严格意义上"同一批产物"只在产品代码层面成立）—— 与 B-R9-07 遗留 5 同一局限。
4. **低｜`BindQualityTargetsForTests` 是产品程序集里的公开测试接缝**：生产路径不调用（真实目标由 `Install()`/`OnCameraRendered()` 绑定），
   但它确实扩大了公开 API 面；治理上若不接受，可改 `InternalsVisibleTo` + internal。
5. **低｜`Restore()` 在"相机已被销毁"时会多说一句日志**：`UrpFrameGenerationCameraDepthTarget` 的 `!= null` 会跳过写入（行为安全），
   但 `Restore()` 仍返回 true 并打印"相机深度开关还原为 …"。属日志准确性小瑕疵，不影响行为。
6. **既有边界（本任务未扩大）**：单相机假设（只记最后一个 Game 相机）、Editor 内行为（门控仍禁用，只有桩路径用例）、
   切画质档 × 插帧、真实 UI 点击、60fps 上限下的性能余量。
