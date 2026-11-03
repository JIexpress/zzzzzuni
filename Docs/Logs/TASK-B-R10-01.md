# TASK-B-R10-01 —— 交付包干净化：剥离测试夹具 + 收敛测试接缝

- **轨道**：B / R10（第一档收尾 ②）
- **状态**：done（验收 PASS with 受限：V1–V6 全 PASS；受限=三处范围外残留）
- **派发**：主控，2026-10-29

## 任务简报（主控写）

### 背景

B-R9-07/08 为验证 FG 在 Player 里的行为，往产品代码里加了**测试夹具**与**公开测试接缝**。
功能正确，但交付包不干净：给别人发一个 Player 包，里面带着只在测试时有意义的类。

已知两处（施工代理可再自查是否有第三处）：

| # | 位置 | 现状 |
|---|---|---|
| C1 | `R908Harness`（B-R9-08 引入） | 编进 Player，运行期注册到场景 |
| C2 | `ZrFrameGenerationFeature.BindQualityTargetsForTests` | **public** 方法，产品 API 被测试污染 |

### 目标

- **G1 发布构建不含夹具**：Player 包里不出现 `R908Harness` 及其依赖（用构建后检查证明，不是"看代码觉得没有"）
- **G2 接缝收敛**：`BindQualityTargetsForTests` 改为 `internal` + 测试程序集 `InternalsVisibleTo`（或等价的强隔离），
  **EditMode/PlayMode 测试仍能通过**
- **G3 不破坏既有证据链**：六段全绿，EditMode **≥702**（只增不减），PlayMode 6

### 做法建议（不强制，按实际代码选）

1. 夹具类用 `#if UNITY_EDITOR || ZFG_TEST_HARNESS` 包裹，并在 asmdef 里给 Tests 程序集定义 `ZFG_TEST_HARNESS`；
   或把夹具整体挪进 Tests 程序集（若它只被测试引用）。
2. 接缝改 `internal`：`Assets/ZRender/Runtime/AssemblyInfo.cs` 加 `[assembly: InternalsVisibleTo("ZFrame.Tests.Editor")]` 等（**按实际 asmdef 名**）。
3. **G1 的证明方式**（任选其一，要可复现）：
   - 构建 Player 后对 `*_Data/Managed/*.dll` 跑 `strings`/`grep` 查 `R908Harness`（0 命中）
   - 或用 `monodis`/`ildasm` 列类型清单
   - 或运行时日志证明夹具未注册（弱一些，需配合静态检查）

### 硬约束

- 不动 FFX 契约、`appLevel = isWrapped && !nested`、B-R9-06 三档门控语义、B-R9-07/08 的画质还原逻辑
- 不得为了"干净"删掉测试覆盖：**用例数只增不减**
- 不 git commit；`/W4 /WX` 零警告；`.cs` UTF-8 无 BOM；不留 `URP_*.asset` 改动、不留 `__zfgprobe`
- 若某个夹具**确实是运行期必需**（例如 FG 只能在 Player 里跑），不要硬删——写清"为什么不能删 + 是否可条件编译"

### 自测清单（贴证据）

1. **G1**：Player 构建产物里 `R908Harness` 命中数 = 0（给出命令与输出）
2. **G2**：`BindQualityTargetsForTests` 已非 public（给 `git diff` 片段/行号）；测试仍通过
3. **G3**：六段全绿（native 196/710,837、editmode ≥702、playmode 6、三校验器 exit 0）
4. **诚实边界**：列出"哪些接缝/夹具保留、为什么"

---

## 施工报告（施工代理写）

> 施工代理：B-R10-01 施工实例（与 B-R9-08 施工/验收均为不同实例）。
> **未 git commit / 未 git add**；未改 `TASKS.md` / `HANDOFF.md` / `WORKLOG.md`。
> 证据目录：`TempLogs/r10_01/`（Player 产物、构建日志、六段输出、变异输出、归档的临时构建入口）。

### 0. 一句话结论

**C1 的源头（源码里的 `R908Harness`）在 HEAD 上已经不存在**（它是 B-R9-08 临时拷进 `Assets/ZRender/Runtime/` 的
非跟踪文件，构建完就归档了，`git log --all -- '*__R908Harness.cs'` 为空）——"Player 包里有夹具"说的是**那次构建的产物**，
不是当前源码。本轮做了三件事把这件事从"口头保证"变成"可复现证据 + 有牙守卫"：

1. **G1**：从 HEAD 源码**重新构建**一个 Player，对全部 **118 个** `*_Data/Managed/*.dll` 跑 `strings | grep`，
   `R908Harness` 命中 **0**；同一命令对 B-R9-08 的旧产物 `R908.exe` 命中 **6**（对照，证明命令真的能查到）。
2. **G2**：`BindQualityTargetsForTests` 由 `public` 收为 `internal` + `InternalsVisibleTo("ZFrame.Render.Tests")`；
   新增**反射守卫用例**（接缝一旦被改回 public 就红）——定向变异实测 **705/706，恰好 1 条红**。
3. **自查发现并修掉第三处（比前两处更严重）**：`ZFrame.Data.Tests.Support` 整个**测试支撑程序集**被编进 Player
   （`autoReferenced: true` + 无 `defineConstraints`）；修完后重建 Player，该 dll 从 `Managed/` 消失。
   另把产品代码里**另外 5 个** `*ForTests` public 成员一并收为 internal（同一套 IVT 机制）。

**六段全绿**：native **196/710,837**、EditMode **706**（702 + 4 条新守卫用例）、PlayMode **6**、arch/config/naming **exit 0**。

---

### 1. C1（`R908Harness`）—— 现场核实与 G1 证明

**核实（先说清楚"现在到底有没有"）**

```bash
$ git grep -l "R908Harness" HEAD -- UnityProject        # 空：HEAD 源码树里没有
$ git log --all --oneline -- "*__R908Harness.cs"        # 空：这个文件从未入过库
$ grep -rn "Harness" UnityProject/Assets --include=*.cs # 空
```

夹具原件在 `TempLogs/r9_08/probe_assets/__R908Harness.cs`（B-R9-08 收口归档）。它的注释自证了当时的做法：
"构建前临时拷进 `Assets/ZRender/Runtime/`，这样它一定随 `ZFrame.Render.dll` 进入 Player"。
所以旧产物 `TempLogs/r9_08/player_clean/R908.exe` 里带夹具，而六段跑的是"移除夹具后"的源码——
B-R9-08 §9.3 已如实记过这条。**本轮不改源码（没得改），改的是"证明方式"：从 HEAD 重新构建，再静态查。**

**构建（临时入口，收口已归档）**

- 临时入口 `Assets/Editor/R1001BuildPlayer.cs`：`BuildPipeline.BuildPlayer` → `TempLogs/r10_01/player/R1001.exe`，
  场景 `SCN_Empty`（与 B-R9-08 同一口径），失败时 `EditorApplication.Exit(1)`。
- 纪律：清代理变量 → 查 `Unity.exe` 无在跑 → 移走 `UnityProject/Temp/UnityLockfile` → **经 Git Bash** 启动 Unity。
- 结果：`[R1001] build result=Succeeded errors=0 size=79521563`（`TempLogs/r10_01/build_player3.log:725`）。
- 说明（如实）：第一次构建**失败**，原因是我新加的守卫用例漏了 `using System;`（`CS0246: 'Type'`），
  日志 `build_player.log:924`；补上后重建成功。这次失败顺带证明了"IVT 生效、接缝改动本身能编过"
  （当时唯一的编译错误就是我自己那行）。

**G1 静态检查（新产物，`TempLogs/r10_01/player/R1001_Data/Managed`，共 118 个 dll）**

```bash
$ cd TempLogs/r10_01/player
$ total=0; for f in R1001_Data/Managed/*.dll; do n=$(strings -a "$f" | grep -c "R908Harness"); \
    total=$((total+n)); [ "$n" != "0" ] && echo "$n  $(basename $f)"; done; echo "TOTAL_R908HARNESS_HITS=$total"
TOTAL_R908HARNESS_HITS=0

$ strings -a R1001_Data/Managed/*.dll | grep -c "R908"        # 0（连前缀都没有）
$ strings -a R1001_Data/Managed/*.dll | grep -ci "r908probe"  # 0
```

**阳性对照（证明这条命令不是"永远返回 0"）**

```bash
$ for f in TempLogs/r9_08/player_clean/R908_Data/Managed/*.dll; do n=$(strings -a "$f" | grep -c "R908Harness"); \
    [ "$n" != "0" ] && echo "$n  $(basename $f)"; done
6  ZFrame.Render.dll
```

**"程序集真的在里面"对照（防止 0 命中是因为 dll 根本没进包）**

```bash
$ strings -a R1001_Data/Managed/ZFrame.Render.dll | grep -c "ZrFrameGenerationRuntime"   # 2
$ strings -a R1001_Data/Managed/ZFrame.Render.dll | grep -c "ZrFrameGenerationFeature"  # 4
$ ls -l R1001_Data/Managed/ZFrame.Render.dll   # 26624 B，与 Game.View.dll 同在
```

**诚实边界**：`strings` 只能证明**类型名/字符串**不在产物里。`BindQualityTargetsForTests` 作为 **internal 方法**
仍会在元数据里留下名字（`grep -c` = 1），这是正常的——G2 约束的是**可见性**，不是"名字消失"。

---

### 2. C2（`BindQualityTargetsForTests`）—— 接缝收敛 + 有牙守卫

**改法（两行）**

`Assets/ZRender/Runtime/ZrFrameGenerationFeature.cs:390`

```diff
-        public static void BindQualityTargetsForTests(
+        internal static void BindQualityTargetsForTests(
```

新增 `Assets/ZRender/Runtime/AssemblyInfo.cs`

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ZFrame.Render.Tests")]
```

程序集名与 asmdef 的 `"name"` 逐字一致（`ZFrame.Render.Tests`，`Assets/ZRender/Tests`，Editor-only）。

**为什么不是"挪进 Tests 程序集"**：接缝要改的是**产品运行期状态**（`s_quality` 的绑定目标），
挪走就必须把 `s_quality` 也暴露出去，等于用一个更大的洞换一个小洞。`internal` + IVT 是这里最小的隔离面。

**新增守卫用例（2 条，`Assets/ZRender/Tests/AssemblySeamVisibilityTests.cs`）**

1. `FrameGenerationQualityBindSeam_IsInternal` —— 反射断言 `IsPublic == false && IsAssembly == true && IsStatic`；
   顺带断言方法**仍然存在**（有人删了接缝也得显式改这条用例，不会静默通过）。
2. `FrameGenerationQualityBindSeam_StillReachesTheProductionEntry` —— 注入桩 → 走**生产入口**
   `SetSubmissionEnabled(true)` → 断言桩管线的 MSAA 被压到 1、深度被要求。**这条同时证明 IVT 不是摆设**
   （访问不到就编译不过）。

**有牙证明（定向变异，`TempLogs/r10_01/mutation_editmode.txt`）**

把接缝**临时改回 `public`** → 只跑 EditMode：

```
editmode FAIL  tests failed: total=706 passed=705 failed=1 (Unity exit=2)
<test-case ... name="FrameGenerationQualityBindSeam_IsInternal"
           fullname="ZFrame.Render.Tests.AssemblySeamVisibilityTests.FrameGenerationQualityBindSeam_IsInternal"
           result="Failed" ...>
```

**恰好 1 条红，且红的正是这条守卫**（不是连带崩一片）。改回 `internal` 后复跑：706/706 绿。

---

### 3. 自查：还有第三处（以及第四、第五处）

按简报要求扫了产品代码里的 `*ForTests` public 成员与"只在测试里有意义的类"，**找到 6 处**：

| # | 位置 | 现状 | 本轮处置 |
|---|---|---|---|
| **C3** | `ZFrame.Data.Tests.Support`（`Assets/ZFrame/Tests/Data/Support/`） | **整个测试支撑程序集进了 Player**：`autoReferenced: true` + `defineConstraints: []` + `includePlatforms: []`。里面是 `TestComponent` / `TestConfigData` 两个夹具类 | **已修**：`autoReferenced: false` + `defineConstraints: ["UNITY_INCLUDE_TESTS"]`（与其它 5 个测试程序集同一口径） |
| C4 | `ZFrame.View.UIRoot.ResetForTests` / `UIManager.ResetForTests` / `SettingsEntry.ResetForTests` | public，产品程序集公开面 | **已修**：`internal` + `View/AssemblyInfo.cs` IVT（`ZFrame.Debug.Tests`、`Game.View.Tests`） |
| C5 | `Game.View.UI.MainMenuView.ReplayPrimaryForTests` / `PausePanelView.InvokeResumeForTests` | public，PlayMode 冒烟用来"模拟点击" | **已修**：`internal` + `Game/View/AssemblyInfo.cs` IVT（`ZFrame.PlayMode.Tests`） |
| — | `ZFrame.View.UI.Sample.TestPanelView` / `TestPanelPresenter` | 名字带 Test，但**是 UI 框架的样例面板**（`ui_test_panel` 预制体 + 教程用途），不是夹具 | **保留**（见 §5） |

**C3 的证明（A/B 两次构建，同一命令）**

```bash
# 修前（C2 后、C3 前那次构建；产物已被下一次构建覆盖，以下是当时的实测输出）
$ ls R1001_Data/Managed/ | grep -i tests
ZFrame.Data.Tests.Support.dll

# 修后（build_player3.log 那次构建，现存的 player/）
$ ls R1001_Data/Managed/ | grep -i tests
（无）
$ ls R1001_Data/Managed/*.dll | wc -l
118
```

> 注：两次构建的产物落在同一目录，Unity 会覆盖，所以"修前"的 dll 总数没有留存；
> 这里只用**同一命令的两次输出**做 A/B（有 → 无），不拿两次构建的总数做差。

`ZFrame.Data.Tests.Support` 在 Player 构建日志里仍会出现（`build_player3.log:278` 等），那是 **Editor 侧**
`Library/ScriptAssemblies` 的编译记录（Editor 定义了 `UNITY_INCLUDE_TESTS`），不是 Player 产物——产物里确实没有这个 dll。

**C4/C5 的守卫用例（2 条，`Assets/Game/Tests/View/AssemblySeamVisibilityTests.cs`）**：
`UiStaticResetSeams_AreInternal`（3 个 `ResetForTests`）、`PlayModeClickSeams_AreInternal`（2 个点击接缝），
同样的反射断言口径。这两条在 `Game.View.Tests`（引用 `ZFrame.View` + `Game.View`），IVT 一旦写错就是编译错误。

> 本轮新增用例 **4 条**（2 + 2），EditMode 702 → **706**；**没有删任何既有用例**。

---

### 4. G3 六段（`TempLogs/r10_01/six_segments_final2.txt`，exit 0）

```
Segment  Status Detail
native   PASS   cases=196 asserts=710837 failed=0; ctest: 100% tests passed (1 entry: zrender_test)
editmode PASS   total=706 passed=706 failed=0 skipped=0
playmode PASS   total=6   passed=6   failed=0 skipped=0
arch     PASS   exit=0
config   PASS   exit=0
naming   PASS   exit=0
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)
```

- EditMode **706 = 702 + 4**（本轮新增的守卫用例），PlayMode 仍 6，native 未改一行原生代码（196/710,837 逐字一致）。
- `Machine quiet check: VERDICT: BUSY — vmmemWSL(20.3%)`：本轮**没有任何性能/帧率类结论**，该告警不影响上述判定。
- 三次六段全量（C2 后 / 变异后 / C3 后）均 exit 0，最终版见 `six_segments_final2.txt`。

---

### 5. 保留项与理由（诚实边界）

1. **`ZFrame.Probe.R908Harness` 不删源码——因为源码本来就不在库里**。它现在只存在于
   `TempLogs/r9_08/probe_assets/`（非跟踪）。本轮用"重建 + 静态查"证明它不会进包；**没有**为它加
   `#if` 包裹（无源码可包）。若将来还要在 Player 里做真机验证，建议沿用 B-R9-08 的做法（临时拷入 → 构建 → 立即归档），
   并把本轮 §1 的三条命令作为发版检查项。
2. **`TestPanelView` / `TestPanelPresenter` 保留为 public**：它们是 UI 框架的**样例/演示**（`ui_test_panel` 预制体、
   `Docs/Demo.md` 的用法示例），不是 FG 夹具；`internal` 化会让预制体在 Inspector 里失去脚本引用能力，
   收益（省两个类型名）远小于风险。**它们确实会进 Player**——如实记在这里，供后续决定是否挪进 `Assets/Samples`。
3. **接缝的"名字"仍在元数据里**：`internal` 只收敛可见性，不擦除符号。真要擦除需要
   `[assembly: Obfuscation]`/IL 裁剪，超出本任务范围且会伤可诊断性。
4. **`ZFrame.Render` 的 IVT 只开给 `ZFrame.Render.Tests`**：如果将来新增渲染测试程序集，必须同步加一行 IVT，
   否则编译期就会红（这是有意的：IVT 清单即"谁能碰内部"的白名单）。
5. **未做**：把"Player 干净度检查"做成 `Tools/` 下的常驻脚本（本轮只给命令）。它需要先解决"谁来构建 Player"
   （构建要 3 分钟 + 需要 `-executeMethod` 入口），不适合塞进六段；建议单独立项。

---

### 6. 改动清单（9 个文件：4 改 + 5 新）

| 文件 | 改动 | 对应 |
|---|---|---|
| `Assets/ZRender/Runtime/ZrFrameGenerationFeature.cs` | `BindQualityTargetsForTests` `public`→`internal` + 注释 | C2 |
| `Assets/ZRender/Runtime/AssemblyInfo.cs` | **新增**：IVT → `ZFrame.Render.Tests` | C2 |
| `Assets/ZFrame/View/UI/UIRoot.cs`、`UI/UIManager.cs`、`Settings/SettingsEntry.cs` | `ResetForTests` `public`→`internal` | C4 |
| `Assets/ZFrame/View/AssemblyInfo.cs` | **新增**：IVT → `ZFrame.Debug.Tests`、`Game.View.Tests` | C4 |
| `Assets/Game/View/UI/MainMenuView.cs`、`UI/PausePanelView.cs` | `*ForTests` `public`→`internal` | C5 |
| `Assets/Game/View/AssemblyInfo.cs` | **新增**：IVT → `ZFrame.PlayMode.Tests` | C5 |
| `Assets/ZFrame/Tests/Data/Support/ZFrame.Data.Tests.Support.asmdef` | `autoReferenced:false` + `defineConstraints:["UNITY_INCLUDE_TESTS"]` | C3 |
| `Assets/ZRender/Tests/AssemblySeamVisibilityTests.cs` | **新增**：2 条 FG 接缝守卫 | C2 |
| `Assets/Game/Tests/View/AssemblySeamVisibilityTests.cs` | **新增**：2 条 UI/Game 接缝守卫 | C4/C5 |

**未动**：FFX 契约、`appLevel = isWrapped && !nested`、B-R9-06 三档门控语义、B-R9-07/08 的画质覆盖与还原逻辑
（`s_quality` 的绑定/覆盖/还原一行未改，只把"谁来调用注入点"的可见性收窄）、任何画质默认值、任何 `URP_*.asset`。

---

### 7. 治理与现场状态

```
 M UnityProject/Assets/Game/View/UI/MainMenuView.cs
 M UnityProject/Assets/Game/View/UI/PausePanelView.cs
 M UnityProject/Assets/ZFrame/Tests/Data/Support/ZFrame.Data.Tests.Support.asmdef
 M UnityProject/Assets/ZFrame/View/Settings/SettingsEntry.cs
 M UnityProject/Assets/ZFrame/View/UI/UIManager.cs
 M UnityProject/Assets/ZFrame/View/UI/UIRoot.cs
 M UnityProject/Assets/ZRender/Runtime/ZrFrameGenerationFeature.cs
?? UnityProject/Assets/Game/Tests/View/AssemblySeamVisibilityTests.cs(+.meta)
?? UnityProject/Assets/Game/View/AssemblyInfo.cs(+.meta)
?? UnityProject/Assets/ZFrame/View/AssemblyInfo.cs(+.meta)
?? UnityProject/Assets/ZRender/Runtime/AssemblyInfo.cs(+.meta)
?? UnityProject/Assets/ZRender/Tests/AssemblySeamVisibilityTests.cs(+.meta)
```

- 无 `URP_*.asset`、无 `zrender.dll` 改动（跑完已 `git checkout -- UnityProject/Assets/Settings/Pipeline/` 与
  `UnityProject/Assets/Plugins/ZRender/zrender.dll`）。
- 无 `__zfgprobe` / `__R908*` / `__R1001*` 残留：`find UnityProject/Assets -iname "*R1001*" -o -iname "__zfgprobe*" -o -iname "*R908*"` 为空；
  临时构建入口归档在 `TempLogs/r10_01/probe_assets/`。
- 11 个被触碰的 `.cs` 字节核对：首 3 字节 `2f2f20` 或 `757369`（**无 BOM**），`CR` 字节数 **0**（LF）。
- `/W4 /WX`：本轮无原生代码改动；native 段 `build_native` 全绿且日志 `warning` 命中 0。
- 未 commit、未 add（HEAD 仍 `6d92b6f`）。


## 验收清单（主控写）

- [ ] V1 G1 静态证明：验收自己跑构建后检查（`strings`/类型清单），`R908Harness` 0 命中
- [ ] V2 G2 隔离有效：接缝非 public；**且测试真能访问**（不是靠删测试过关）
- [ ] V3 用例数不减：editmode ≥702、playmode 6
- [ ] V4 六段全绿 + 三校验器 exit 0
- [ ] V5 治理：未 commit、无 URP 资产改动、无 `__zfgprobe`、`.cs` 无 BOM
- [ ] V6 诚实性：保留的夹具/接缝有说明，没有"为了绿而删覆盖"

## 验收结论（验收代理写）

> 验收代理：B-R10-01 验收实例（与施工代理**不是**同一实例）。**只认本轮自己跑出来的证据**。
> 证据目录：`TempLogs/r10_01/acc_*`（我自己构建的 Player、变异结果、六段输出）。
> **未 git commit / 未 git add**，HEAD 仍 `6d92b6f`。除"定向变异 + C3 的 A/B 对照"（均已还原）外未改代码。

### 总结论：**PASS with 受限**

V1–V6 六项**逐项 PASS**（证据见下）。判"受限"而不判"纯 PASS"，是因为交付包离"零测试痕迹"仍差三处
（施工已如实披露、且都在简报范围之外，不影响本轮验收项判定，但不应被读成"包已经绝对干净"）：

1. **C1 没有常驻自动化守卫**：`R908Harness` 本轮只拿到"重建 + 静态查"的**一次性证据**与三条命令，
   六段里没有任何一段会因"夹具重新进包"而红。将来有人再把夹具拷进 `Assets/`，六段照样全绿。
2. **`TestPanelView` / `TestPanelPresenter` 仍随包发布**（`Assets/ZFrame/View/UI/Sample/`，预制体
   `Assets/UI/Resources/ui_test_panel.prefab` 在 Resources 下）——它是 UI 样例不是夹具，`internal` 化会断掉预制体脚本引用，
   保留合理；但它确实进 Player。
3. **接缝的符号名仍在元数据里**：`internal` 只收敛可见性。我实测自己构建的 `ZFrame.Render.dll` 里
   `BindQualityTargetsForTests` 字符串命中 1（与施工说法一致），属正常。

### V1 G1 静态证明 —— **PASS**

**我不复用施工的入口**：自己写了一个临时构建入口（`Assets/Editor/AccR1001Build.cs`，`BuildPipeline.BuildPlayer` +
`SCN_Empty`，构建完已删除 cs 与 meta，工作区无残留），从当前工作区源码构建了一个新 Player。

```
[ACC-R1001] build result=Succeeded errors=0 size=79521563 out=.../TempLogs/r10_01/acc_player
dll_count=118
ACC_TOTAL_R908HARNESS_HITS=0          # 逐个 dll 累加，无任何 dll 命中
Harness(任意子串): 0
R908(任意子串): 0
```

**阳性对照（证明命令有牙，不是"永远 0"）**——同一循环、同一 `strings -a | grep -c` 打在旧产物上：

```
6  ZFrame.Render.dll                  # TempLogs/r9_08/player_clean/R908_Data/Managed
OLD_TOTAL=6
```

**"0 命中不是因为程序集没进包"对照**：

```
AccR1001_Data/Managed/ZFrame.Render.dll  26624 B
ZrFrameGenerationRuntime 命中: 2
BindQualityTargetsForTests 命中: 1        # internal 方法名仍在元数据，符合预期
```

结论：我独立构建的 Player 中 `R908Harness` **命中 0**，且命令对旧产物命中 **6**，证据成立。

### V2 G2 隔离有效 —— **PASS**

- **读代码**：`Assets/ZRender/Runtime/ZrFrameGenerationFeature.cs:390` 为 `internal static void BindQualityTargetsForTests(`；
  `Assets/ZRender/Runtime/AssemblyInfo.cs:14` 为 `[assembly: InternalsVisibleTo("ZFrame.Render.Tests")]`，
  与 `Assets/ZRender/Tests/ZFrame.Render.Tests.asmdef` 的 `"name"` 逐字一致。
- **测试真能访问（不是靠删测试）**：六段里 `ZFrame.Render.Tests.AssemblySeamVisibilityTests.FrameGenerationQualityBindSeam_IsInternal`
  与 `..._StillReachesTheProductionEntry` 均 `Passed`；后者直接**调用** internal 接缝 → IVT 若失效是编译期错误，不可能绿。
  既有 `ZrFrameGenerationSettingsTests`（283/322 行）也仍直接调用该接缝。
- **我自己做的定向变异**：把该行改回 `public`（`git diff` 当场只剩 4 行注释、可见性行回到 HEAD 形态，证明变异确实回到"旧写法"），
  只跑 EditMode：

```
Unity exit=2
total=706 passed=705 failed=1
Failed  ZFrame.Render.Tests.AssemblySeamVisibilityTests.FrameGenerationQualityBindSeam_IsInternal
```

  **恰好 1 条红，红的正是那条守卫**，与施工声称的 705/706 完全一致。
- **已还原**：`md5 5ff31c9ed148b45010e69bad81b58030`（与变异前一致），`git diff` 恢复为施工原样（仅注释块 + internal 一行）。

### V3 用例数不减 —— **PASS**

- EditMode **706**（`TempLogs/runtests_editmode.xml`：`total=706 passed=706 failed=0 skipped=0`）≥ 702；
  四条新守卫用例（FG 2 条 + UI/Game 2 条）在 xml 里均为 `Passed`。
- PlayMode **6**（`total=6 passed=6`）。
- 全程无删除文件：`git status` 无 `D` 项，`git diff --stat` 只有 7 个改动文件（+31/−12），新增 5 个文件。

### V4 六段全绿 + 三校验器 exit 0 —— **PASS**

跑前同进程清代理变量；`Tools/run_tests.ps1` 全量（`TempLogs/r10_01/acc_six_segments.txt`，脚本 `EXITCODE=0`）：

```
native   PASS  cases=196 asserts=710837 failed=0; ctest: 100% tests passed (1 entry: zrender_test)
editmode PASS  total=706 passed=706 failed=0 skipped=0
playmode PASS  total=6   passed=6   failed=0 skipped=0
arch     PASS  exit=0
config   PASS  exit=0
naming   PASS  exit=0
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)
```

- `Machine quiet check: VERDICT: BUSY — vmmemWSL(24.7%)`：本轮无任何性能/帧率结论，不影响判定（如实记录）。
- 纪律：Unity 必须**经 Git Bash** 启动——我第一次按"PowerShell 工具直接起 Unity"试了一遍，
  6 秒假退出，日志停在 `Error: Access token is unavailable`（与 `run_tests.ps1` 头部注释记录的现象一致）；
  改经 Git Bash 后构建与测试全部正常。这条坑交接档案 §3.3 没写清，建议补一句。

### V5 治理 —— **PASS**

- `git log -1` = `6d92b6f G-78: B-R10-01简报入板(...)`（未 commit；我未 add/commit）。
- `git status` 只有：7 个 `M`（6 个 .cs/asmdef + 任务日志）+ 5 个新增文件（含 .meta）。
  **无 `URP_*.asset`、无 `zrender.dll`、无 `__zfgprobe`**。
  （如实说明：我自己的构建把 `URP_*.asset` 与 `zrender.dll` 弄脏过一次，已 `git checkout --` 还原，还原后状态与施工报告 §7 逐行一致。）
- 11 个被触碰的 `.cs`：首 3 字节 `757369`/`2f2f20`，**CR 字节 0**（无 BOM、LF）。
- 我引入的临时文件 `AccR1001Build.cs(+.meta)` 已删除，`find` 复查无 `AccR1001*`/`__R908*`/`__zfgprobe*` 残留。

### V6 诚实性 —— **PASS**（附两条措辞纠偏）

- **保留项理由站得住**：`R908Harness` 源码确实不在库（`git grep -l R908Harness HEAD` 空、`git log --all -- '*R908Harness*'` 空），
  所以"没源码可包"属实；`TestPanelView` 我核到 `Assets/UI/Resources/ui_test_panel.prefab` + `UISetup.cs` 引用，确是样例面板；
  "符号名不擦"我实测复现（命中 1）。
- **C3 不是写在报告里，是真修了**——我做了施工没留档的 **A/B 对照构建**：
  仅把 asmdef 用 `git checkout` 退回修前状态（`autoReferenced:true` + `defineConstraints:[]`）重建 →
  `Managed/` 里**出现** `ZFrame.Data.Tests.Support.dll`，dll 总数 **119**；
  恢复施工版（`autoReferenced:false` + `UNITY_INCLUDE_TESTS`）后我自己的构建为 **118** 个 dll、该 dll **不存在**。
  即"这处修复确实有牙"，不是文档承诺。
- **C4/C5 真修了**：6 个 `*ForTests` 成员现在全部 `internal`（`grep` 全库无 public 残留），
  且它们仍被真实测试直接调用（`GameHudLeakTests`、`UITestEnv`、`BootChainSmokeTests`）——不是删覆盖换绿。
- **两条措辞纠偏（不影响结论）**：
  1. 施工 §3 说"这两条在 `Game.View.Tests`…IVT 一旦写错就是编译错误"——对**新增的守卫用例本身不成立**：
     它们用反射取 `MethodInfo`，不依赖 IVT。真正在编译期依赖 IVT 的是 `GameHudLeakTests`（C4）与
     `BootChainSmokeTests`（C5），所以 IVT 事实上仍被钉住，只是理由句不准确。
  2. `SettingsEntry.ResetForTests` 在 HEAD 上**本就零调用**（`git grep` 只命中声明）。它是既有死接缝，
     本轮只收窄了可见性、没删也没补用例；守卫用例对它是"存在性 + 非 public"断言。建议后续决定"删掉"或"补用例"，别长期挂着。

### 遗留项（不阻塞本轮收口，供主控入板）

| # | 项 | 建议 |
|---|---|---|
| 1 | Player 干净度无自动化守卫（C1 只有一次性证据） | 单独立项：`Tools/check_player_clean.ps1`（构建 + `strings` 查夹具 + 断言 dll 清单），或并入发版清单 |
| 2 | `TestPanelView`/`TestPanelPresenter` 随包发布 | 后续决定是否挪进 `Assets/Samples`（需同步预制体） |
| 3 | `SettingsEntry.ResetForTests` 是死接缝 | 删掉，或补一条真调用它的用例 |
| 4 | 交接档案 §3.3 未写"Unity 必须经 Git Bash 启动，PowerShell 直启会 licensing 假退出" | 补进环境坑 |

**与施工报告不符处**：数字层面**零不符**（构建大小 79521563、118 dll、0/6 命中、706/6、exit 0 全部逐项复现）；
只有上列 V6 的两处**表述**不够准确（守卫用例与 IVT 的关系、`SettingsEntry` 的调用现状），以及 C3 施工只有"修前 A 输出"、
我补上了"修前产物真实存在"的 A/B 证据。
