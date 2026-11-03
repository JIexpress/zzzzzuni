# TASK-A-AND-07 —— 发布元数据：versionCode 自增 + 修版本倒挂

> 施工代理按本简报执行，第 2 节写报告。**禁止 git commit**。验收由另一代理执行。

## 任务简报（主控写，2026-11-03）

### 背景（来自 A-AND-06 验收的两条实测结论）

1. **`versionCode` 写死为 `1`**：`ProjectSettings.asset:172 AndroidBundleVersionCode: 1`，
   `Tools/version.json` 只驱动 `versionName`（`bundleVersion`）。新旧两个包 versionCode 都是 `1`。
   内部 `adb install -r` 无碍（相等也覆盖），但**应用商店要求 versionCode 严格递增**，第二次上传即被拒。
2. **版本倒挂**：本包 `versionName 0.1.0.19` **低于**旧 MonoDev 包 `0.1.0.22`——
   因为 `version.json` 的 `build` 曾被重置回 18 再自增。安装无影响，但版本追踪会误判，
   且如果将来按 versionName 判断"谁更新"，会得到错的答案。

### 两个设计决策（**我已定，照做，别自己改方案**）

**决策一：versionCode 用独立字段，不从 `build` 派生。**
理由：`build` 历史上被重置过（18→19 就导致倒挂），用它派生 versionCode 会**直接违反单调递增**，
一旦倒退，Android 会拒绝安装更高 versionName 的包，而且商店端不可逆。
所以在 `Tools/version.json` 增加**独立字段** `"versionCode"`，构建时：
`PlayerSettings.Android.bundleVersionCode = <读到的值>` → 构建成功后**该值 +1 写回**。
并加**单调性守卫**：若读到的值 ≤ 上次已发布值（或 ≤ 1），直接报错中止构建，而不是静默继续。

初值取 **2**（当前两个包都是 1，2 是下一个合法值）。

**决策二：版本倒挂直接把 `build` 抬到 23。**
`0.1.0.22` 是已被真机装过的最高版本，下一个必须 ≥ 23，否则继续倒挂。
（不必 bump 到 0.2，那是产品决策不是修 bug；本次只保证**不再倒退**。）

### 任务

1. **改 `Tools/version.json`**：加 `"versionCode": 2`，`build` 改 `23`。
2. **改 `Game/Editor/PlayerBuild.cs`**：
   - 读取并注入 `PlayerSettings.Android.bundleVersionCode`；
   - 构建成功后 `versionCode + 1` 写回 `version.json`（与现有 `build` 自增同一处逻辑，别造第二套写回）；
   - 加单调性守卫（见决策一），守卫失败要**明确报错并中止**；
   - 日志里打出 `versionCode=<旧> -> <新>`，与现有 `[PlayerBuild] version: bundleVersion=...` 同级。
3. **改 `Tools/build_android.ps1`**（只在必要时）——如果脚本里有版本相关打印，补上 versionCode。
   若无需改动就不要动它（少改少错）。
4. **不要重新出包**：本任务只改元数据与构建逻辑。**验证靠单测 + 一次"干跑"读数**，
   不到万不得已不要跑 30 分钟 IL2CPP 构建（若你认为必须跑，先在报告说明理由，且优先用 `-Backend Mono` 快速管线验证逻辑）。
5. **测试**：
   - EditMode 或用例覆盖：versionCode 注入正确、自增写回正确、**倒退时守卫报错**（这条必须有牙）；
   - 现有 798 EditMode / 41 PlayMode 不回归。

### 判据

- `version.json` 含 `versionCode`，值与 `build` 相互独立；
- 构建逻辑：读 → 注入 → 成功后 +1 写回；**倒退被拒绝**（有测试证明）；
- `versionName` 下次构建将是 `0.1.0.23`（> 0.1.0.22，不再倒挂）；
- 不回归既有测试。

### 环境纪律（真实踩过）

- Unity 批处理**单实例**：跑前查进程 + 删 `UnityProject/Temp/UnityLockfile`
- 跑测试**勿带 `-quit`**；退出僵死 → 轮询 `results.xml`，5 分钟有 xml 无退出即 `taskkill //PID //F`
- 构建前 `unset http_proxy https_proxy all_proxy`；**Bash 侧禁止转调 powershell** → 用 **PowerShell 工具**
- 清理用 `mv` 归档到 `TempLogs/archive_pre_audit/`，不要 `rm -rf`
- 跑完查 `git status`：Editor 自动改写 `URP_*.asset`/`QualitySettings.asset` 非本意就 `git checkout` 还原

### 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- 不动 Governor 三件套、Unity 安装目录；`.cs` 无 BOM、LF
- **不要顺手改 A-AND-08（签名证书）相关**，那是另一个任务且待用户决策

---

## 施工报告（施工代理写）

### ① 改动清单

| 文件 | 类型 | 改动 |
|---|---|---|
| `Tools/version.json` | 改 | `build` 19→**23**；新增独立字段 `"versionCode": 2`（LF、2 空格缩进、无 BOM） |
| `UnityProject/Assets/Game/Editor/PlayerBuild.cs` | 改 | 版本注入去掉自建解析器，改为调用 `ReleaseVersion`；注入 `PlayerSettings.Android.bundleVersionCode`；**写回点收敛为唯一方法 `CommitVersion()`**（build+1 与 versionCode+1 同一次落盘，且只在 `BuildResult.Succeeded` 之后执行）；新增干跑入口 `DumpReleaseVersion()` |
| `UnityProject/Assets/ZFrame/Editor/ReleaseVersion.cs` | 新增 | 发布元数据**纯逻辑**（解析/守卫/自增/序列化落盘），零 Unity API，故可被 EditMode 单测直接驱动 |
| `UnityProject/Assets/ZFrame/Tests/Editor/ReleaseVersionTests.cs` | 新增 | 13 条单测（注入值/自增/守卫/编码/仓库真值防倒挂） |
| `Tools/build_android.ps1` | **未动** | 脚本内无任何版本打印（grep `version` 零命中），按简报"少改少错"不动 |

两个 `.meta` 由 Unity 自动生成（未手写）。

`version.json` 终态：

```json
{
  "version": "0.1.0",
  "build": 23,
  "versionCode": 2
}
```

### ② PlayerBuild 改动要点（单条写路径）

```
PrepareVersion()                      CommitVersion(version)          ← 唯一写回点
  LoadAndValidateVersion()              version.Advance()  → build+1, versionCode+1
    ReleaseVersion.Load(path)           next.Save(path)    → 一次落盘（UTF-8 无 BOM/LF）
    version.ValidateForBuild()          （仅 BuildResult.Succeeded 后调用；失败构建不消耗版本号）
  PlayerSettings.bundleVersion = "<version>.<build+1>"
  PlayerSettings.Android.bundleVersionCode = versionCode
  log: [PlayerBuild] version: bundleVersion=0.1.0.24 versionCode=2 (version.json build=23)
  log: [PlayerBuild] version committed: bundleVersion=0.1.0.24 versionCode=2 -> 3; version.json build 23 -> 24
```

- `Run()`（Windows64）与 `RunAndroid()`（IL2CPP / Mono / MonoDev 三个入口）共用同一对方法，
  **没有第二套写回**：`InjectBundleVersion()` 连同其内联的 `File.WriteAllText` 已整体删除。
- 构建失败（`result != Succeeded`）直接 `return 1`，不进 `CommitVersion` —— 失败轮不再白吃版本号
  （这是相对旧实现的语义收紧，旧实现是"构建前先写回"）。
- 写回失败（磁盘/权限）按失败收场（`return 1`）：包已产出但元数据落后会让下一轮复用同一
  versionCode，商店端会被拒，不能静默当成功。
- 日志与旧格式同级。`versionCode=<旧> -> <新>` 打在 `version committed` 行里。

### ③ 单调性守卫怎么实现（有牙）

`ReleaseVersion.EnsureMonotonic`（`ReleaseVersion.cs:112`）：`versionCode < 2` 一律抛
`InvalidOperationException`，消息含 `versionCode=…`、`must be >= 2`、`Fix Tools/version.json instead of building`。
注入前由 `LoadAndValidateVersion()` 调用，捕获后 `Debug.LogError` + 返回 null → 调用方 `return 1`，
**构建根本不会启动**。漏写 `versionCode` 字段时 Json.NET 给 0，同样被拒（不会静默当 1 用）。

为什么下限是"≤1 拒绝"而不是"≤上次已发布值"：仓库里没有"上次已发布 versionCode"的持久记录；
唯一候选 `ProjectSettings.AndroidBundleVersionCode` 会被**失败构建**改写（注入即写），拿它当高水位
会在"失败后重试"时误杀。故按简报括号里的第一个条件落地：`< 2` 即拒绝。真值由 `version.json` 单向推进。

### ④ 测试与变异（有牙的证明）

新增 13 条 EditMode 用例（`ZFrame.Editor.Tests.ReleaseVersionTests`），定向跑 **13/13 通过（exit 0）**：

| 变异 | 手法 | 结果 |
|---|---|---|
| M1 去掉守卫（"改回旧逻辑"） | `ValidateForBuild()` 里注释掉 `EnsureMonotonic(VersionCode)` | **9 passed / 4 failed，Unity exit 2**。红：`Parse_MissingVersionCode_BecomesZero_AndGuardRejectsIt`、`ValidateForBuild_RejectsRegressedVersionCode(1)`、`(0)`、`(-5)`；还原守卫后复跑 **13/13 绿** |
| M2 真构建入口倒退 | `Tools/version.json` 的 `versionCode` 改成 `1`，跑 `DumpReleaseVersion` | **Unity exit 1**，日志 `[PlayerBuild] version guard rejected the build: versionCode=1 rejected: must be >= 2 …`；还原后 exit 0 |

M2 走的是 `PrepareVersion()` 的同一前置（`LoadAndValidateVersion`），所以证明的是**真实构建路径会中止**，
不只是单测。另有守门用例 `RepoVersionJson_PassesGuard_AndNextVersionNameBeatsMonoDev22`：直接读仓库真
`Tools/version.json`，断言 `versionCode >= 2` 且 `build+1 >= 23`——build 再被重置回 18 就会红。

### ⑤ 干跑读数（不写文件、不构建、不改 PlayerSettings）

`-executeMethod Game.EditorTools.PlayerBuild.DumpReleaseVersion` → **exit 0**：

```
[ReleaseVersion] dry-run: bundleVersion=0.1.0.24 versionCode=2 (version.json build=23, versionCode=2);
                 after a successful build it would write back build=24, versionCode=3
[ReleaseVersion] current PlayerSettings: bundleVersion=0.1.0.19 androidBundleVersionCode=1
```

即：当前两个包确实是 `versionCode=1` / `versionName=0.1.0.19`，下一次构建将注入 `versionCode=2`。

### ⑥ ⚠ 需要主控确认的一处算术（不改方案，只报数）

简报判据 V4 写"下次 versionName 为 `0.1.0.23`"，但现有语义是 **`version.json` 的 build = 已消耗的构建号**，
本次构建取 `build+1`。所以按简报"build 改 23"执行后，**下次 versionName = `0.1.0.24`**（已验证于⑤），
不是 `0.1.0.23`。倒挂本身已修（`0.1.0.24 > 0.1.0.22`）。若主控要的是严格的 `0.1.0.23`，应把 build 设为
**22**——但这与简报第 1 条"build 由 19 改为 23"字面冲突，故未擅自改动，请裁决。

### ⑦ 没跑完整构建（说明理由）

按简报"不要重新出包"：本任务只动元数据与构建逻辑，验证走单测 + 干跑。
不跑 IL2CPP 的理由：① 30 分钟且消耗一个 `build` 号，会把 `version.json` 写成 build=24，直接与判据 V1
（`build=23`）打架；② 本次改动的构建侧行为（读→守卫→注入→成功后写回）已由 13 条单测覆盖到纯逻辑层，
守卫中止已由干跑在真实入口证伪/证实；唯一未被运行时覆盖的是两行 `PlayerSettings` 赋值，属 API 直连，
且下一轮真机构建会立刻暴露。**未使用 `-Backend Mono` 快速管线**（同样会消耗 build 号并改写
`ProjectSettings.asset`，代价大于收益）。

### ⑧ 回归与环境

- EditMode 全量：**811/811 通过**（798 基线 + 新增 13），exit 0，0 failed。
- PlayMode 全量：**第 1 次 40/41**，唯一红是 `GameplayFxPixelEvidencePlayModeTests.DoorToggleEvents_BrightenTheFrame_AtTheDoorPosition_BothDirections`
  （像素提亮 0.0003 < 门限 0.0040）；**第 2 次 41/41 全绿**。该用例在 `TASK-A-AND-03`/`TASK-A-AUDIT-02`
  已有"重编译后首跑一次性抖动"记录，且本次改动不触碰任何渲染/门逻辑，判为既有抖动，非本任务回归。
- 全程单实例：跑前 `tasklist` 无 Unity、无 `UnityLockfile`；测试不带 `-quit`；均正常退出（无 taskkill）。
- 干跑只读，`ProjectSettings.asset` 未被改写（`bundleVersion: 0.1.0.19` / `AndroidBundleVersionCode: 1` 保持原样）。
- `git status` 只有本任务 6 个条目（2 改 + 2 新增 + 2 自动 .meta），无 `URP_*.asset`/`QualitySettings.asset` 污染。
- 未 commit；未动 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`、Governor 三件套、A-AND-08 签名相关。

---

## 验收清单（供验收代理）

- [x] V1 `version.json` 含独立 `versionCode`，`build=23`
- [x] V2 构建逻辑：读→注入→成功后 +1 写回（同一处写回，不是两套）
- [x] V3 **单调性守卫有牙**：把 versionCode 改回 1 或更小 → 构建/单测报错
- [x] V4 下次 versionName 为 0.1.0.23（不再倒挂 0.1.0.22）〔验收注：实测 0.1.0.24，见下 V4 行的换算核对〕
- [x] V5 回归：EditMode 798 / PlayMode 41 不降（实测 811 / 41×2 连绿）
- [x] V6 未动 A-AND-08 相关；未 commit；现场干净

## 验收结论（验收代理写）

> 验收代理：与施工不同代理；2026-11-03；全程只读取证 + 一次版本文件变异（已还原）；未跑完整 IL2CPP 构建。
> 证据目录：`TempLogs/audit_07/`（gitignored）。基线哈希（取证前）：`version.json` `E443C36E…`、
> `PlayerBuild.cs` `4D62B7FC…`、`ReleaseVersion.cs` `B89EB203…`。

| 判据 | 结论 | 一行证据 |
|---|---|---|
| **V1** 独立 versionCode + build=23 | **PASS** | `Tools/version.json` = `{version:"0.1.0", build:23, versionCode:2}`；独立性由源码证：`ReleaseVersion.BundleVersion`=build+1（`ReleaseVersion.cs:58`）、`Advance()` 对 build/versionCode **各自** +1（`:63`），`Parse` 无任何由 build 计算 versionCode 的表达式；单测 `Advance_IncrementsBuildAndVersionCodeIndependently` 用 build=100/versionCode=2 → 3 反证非派生 |
| **V2** 读→注入→成功后+1写回，唯一写路径 | **PASS with 受限** | `PrepareVersion()`（`PlayerBuild.cs:126-139`）读+守卫+注入 `PlayerSettings.bundleVersion` / `.Android.bundleVersionCode`（`:133-134`）；`CommitVersion()`（`:145-163`）是**唯一**写回点，仅在 `summary.result == Succeeded` 之后被 `Run()`（`:562`）与 `RunAndroid()`（`:505`）各调一次；旧 `InjectBundleVersion()` 连同其内联 `File.WriteAllText` 已整段删除（`git diff` −63 行）。全仓 grep：`bundleVersionCode` 赋值仅 `PlayerBuild.cs:134` 一处；`version.json` 落盘仅 `ReleaseVersion.cs:138`，调用方仅 `CommitVersion`；`build_player.ps1` 只读不写。**受限点**＝按简报未跑真实构建，"构建成功后写回"的时序只有静态/结构证据，两行 `PlayerSettings` 赋值与 `BuildPipeline` 后的写回落盘未在运行时执行过 |
| **V3** 守卫有牙（核心） | **PASS** | 自行把 `versionCode` 改 `1` → `DumpReleaseVersion` **exit 1**，日志 `:328`：`[PlayerBuild] version guard rejected the build: versionCode=1 rejected: must be >= 2 (the published packages already consumed 1)… Fix Tools/version.json instead of building.`；同日志**无** `[ReleaseVersion] dry-run`、无 `building ->`，即构建未启动。真实构建入口同源：`PrepareVersion()==null` → `Run()`/`RunAndroid()` 直接 `return 1`，早于任何 `BuildPipeline.BuildPlayer`（`PlayerBuild.cs:436-438`、`516-520`）。变异态另跑定向单测：**12/13，唯一红** `RepoVersionJson_PassesGuard_AndNextVersionNameBeatsMonoDev22`（exit 2）→ 守卫用例对仓库真值同样有牙。**还原**：`cp` 备份回填，`md5 e443c36eb095e82d8c7c806d0fbf8c5b` 与变异前逐字节一致 |
| **V4** 下次 versionName 不倒挂 | **PASS** | 干跑 exit 0：`bundleVersion=0.1.0.24 versionCode=2 (version.json build=23)`；换算核对：`build` 语义＝**已消耗构建号**，本次取 `build+1=24`，与 A-AND-06 "读 18→注入 0.1.0.19→写回 19" 同一规律；`0.1.0.24 > 0.1.0.22`，倒挂已修。简报 V4 字面的 `0.1.0.23` 是把 build 当"下一个号"的算术，按现有语义 `0.1.0.24` 才是正确注入值（判据实质满足，施工 ⑥ 的报数正确）。干跑前后 `version.json` md5 不变、`ProjectSettings.asset` 未被改写（git status 零命中） |
| **V5** 回归不降 | **PASS** | 亲跑 EditMode **811/811**（`results_editmode_full.xml`，passed=811 failed=0，exit 0，wall 53.6s）；PlayMode **41/41 两连**（`results_playmode_run1.xml` / `run2.xml`，均 exit 0，wall 188s / 79s） |
| **V6** 范围/现场 | **PASS** | `git status --porcelain` 仅 7 条（`Docs/Logs/TASK-A-AND-07.md`、`Tools/version.json`、`PlayerBuild.cs` + 2 新增 `.cs` + 2 自动 `.meta`），**无** `ProjectSettings.asset`/`URP_*.asset`/`QualitySettings.asset`；`HEAD=80414e0` 未 commit；`TASKS.md`/`HANDOFF.md`/`WORKLOG.md` 零改动；A-AND-08 签名项（`ProjectSettings.asset` 整体）未变 |

### 关于 PlayMode 那次 40/41 的判断：**判为抖动（非本任务回归）**，依据四条

1. **我自己两连全绿，且像素值可复现**：`door.open brighter=0.0179 / 0.0184`、`door.close=0.0066 / 0.0072`（门槛 0.0040），两次波动约 10%——同码稳定复现。
2. **失败读数是双峰而非连续退化**：本次 0.0003、A-AUDIT-02 记 0.0003、A-AND-03 记 0.0004，全部是"≈0"；而绿跑是 0.0066–0.0184。0.0003 不是"差一点没到门槛"，是**捕获时 burst 已不在**——与"重编译后首个 Play 会话 GPU/着色器预热把帧间隔拉过 burst 寿命 0.22–0.45s"的机理吻合（用例在 `Toggle()` 后固定等 2 帧取帧，对帧时长敏感）。
3. **本任务 diff 不触碰该链路的任何一环**：只改 Editor 侧构建脚本 + `version.json` + 新增 Editor-only 纯逻辑类与其单测，PlayMode 程序集与渲染/门逻辑零改动。
4. **历史已两次同签名复现并复跑转绿**：`TASK-A-AND-03.md:89-93`（19/20→20/20，含验收第 4 次独立复跑）、`TASK-A-AUDIT-02.md:267-270`（0.0003 同型）。

**保留意见（不计 FAIL，供主控知悉）**：该用例 `close` 分支余量仅约 1.65× 门槛（`open` 4.5×），本身偏脆，属既有测试质量债，建议后续单独收口。

**另一条观察（非本任务判据）**：守卫是"**地板**（`< 2` 拒绝）"而非"**高水位线**"。若 `version.json` 的 `versionCode` 被人为重置成 `2` 而历史已发布到 `7`，守卫**不会**报错，只有构建后的应用商店上传环节才会被拒。施工在 ③ 已说明理由（无持久高水位记录、`ProjectSettings` 会被失败构建改写），简报括号内也允许"≤1"这一支，故不算 FAIL；但"防重置"的语义强度弱于简报文字里的"≤ 上次已发布值"，请主控按需另立任务。

### 现场终态

- `Tools/version.json` = `{"version":"0.1.0","build":23,"versionCode":2}`，md5 `e443c36eb095e82d8c7c806d0fbf8c5b`（＝验收开始前基线，变异已完全还原）。
- `PlayerBuild.cs` `4d62b7fc…`、`ReleaseVersion.cs` `b89eb203…`：与基线一致，验收未改任何产品代码。
- 无 Unity 残留进程、无 `UnityLockfile`；证据与备份留档 `TempLogs/audit_07/`（不入库）。

**最终裁决：PASS（V1–V6 全通过，0 FAIL）。** 受限点仅一处：真实 `BuildPipeline` 未跑（简报明令禁止 30 分钟构建），"成功后写回"为静态结构证据 + 共享前置的运行时证伪；建议下一个真机构建轮次顺手核对 `ProjectSettings.asset:172 AndroidBundleVersionCode` 变为 `2`、`version.json` 变为 `build=24 / versionCode=3` 即闭环。

