# TASK-A-COL-07 · 报告面板迭代：滚动 + 异常项跳转 + 情报点买原始记录（信息比钱贵）

> 简报（主控写）｜2026-11-22｜施工：施工子代理（ZCode 会话）｜验收：general-purpose 验收子代理｜前置：A-COL-04（报告三档+瞒报）、A-COL-05（垂直切片）

## 一、任务简报（唯一需求来源）

### 背景

A-COL-05 登记的已知缺口：报告面板无滚动（Raw ~80 行超屏）。本轮一并做三件 UI 迭代：
1. **滚动**：Raw 长列表可滚；
2. **异常项跳转**：点 Summary 里的异常项 → 直接切到 Raw 视图并按该类事件过滤+高亮（异常项从"提示"变"入口"）；
3. **情报点买 Raw**（指挥代理 §1.2 + 真实感路线⑤「信息比资源贵」）：查原始记录要花情报点——Summary/Standard 免费，Raw 每次打开消耗 1 点，点数不够就查不了。**AI 报喜不报忧有了对抗手段，但手段本身是稀缺资源。**

### 交付物

1. **Domain 小改**（`Game/Domain/Colony/`）：
   - `Governor` 增 `IntelPoints`（初始 2）+ `SpendIntelPoint() → bool`（不够返回 false 不扣）；`GameWorld.MonthTick` 每月 +1（注释：情报渠道的月度维持费）；
   - `ColonySaveData` 增 IntelPoints 字段（Capture/Restore 同步——存档不丢点数）。
2. **ReportPanelView 迭代**（`Game/View/Colony/`）：
   - 内容区包 ScrollRect（代码构建，UGUI 约定同现有 pure-view 通道）；
   - 异常项行按钮化：点击 → Raw 视图 + 按 Kind 过滤 + 命中行高亮色；
   - 「查原始记录」改为消耗按钮：IntelPoints>0 → 扣 1 点开 Raw（含过滤态）；不足 → 按钮置灰+提示行「情报渠道不足（每月 +1）」；
   - 顶部常驻：当前情报点数。
3. **测试**：
   - EditMode `ReportUiLogicTests`（presenter/VM 纯逻辑）：N1 异常项点击 → VM 切 Raw+过滤 Kind+命中标记；N2 情报点 0 → Raw 请求被拒且给出禁用态；N3 消费成功扣 1 点；N4 MonthTick 后 +1（连同存档往返 S 系补一条 IntelPoints 断言到既有 S1 或新增 N4）。
   - PlayMode `ColonyVerticalSliceTests` 追加：C5 Raw 内容超屏时 ScrollRect 可滚（content 高 > viewport 高）；C6 点异常项 → 过滤视图行数 < 全量 Raw 行数且高亮存在；C7 IntelPoints=0 时按钮禁用态可见。
   - 不破坏既有 M1-M5/P1-P4/C1-C4（报告契约：Raw 折叠显示行已为 scoped 调整，本任务沿用）。
4. **白名单**：`Assets/Game/Domain/Colony/**`、`Assets/Game/View/Colony/**`、`Assets/Game/Tests/**`、`Docs/Logs/TASK-A-COL-07.md`。其余登记待裁决。不 commit、不推远程。

### 自测要求

编译 exit 0 → EditMode 全量 failed=0 total≥1311（1306+N1-N4）→ PlayMode 两轮同形 failed=0（119+C5-C7）→ arch exit 0 → 变异自证（改坏一条新断言 → RED → 还原 sha256 一致 → GREEN）。**Unity 撞锁纪律沿用：用户编辑器开着，只等待重试，禁止 taskkill 用户编辑器。**

### 验收清单 V1-Vn（验收代理独立亲证）

- V1 编译零 CS error。
- V2 EditMode 全量 failed=0 total≥1311（记录确切值）；旧 26 用例抽查零回归。
- V3 PlayMode 两轮同形 failed=0（记录确切值）。
- V4 架构校验 exit 0；Domain 纪律不变；存档 IntelPoints 往返断言在。
- V5 变异复红（验收自选与施工不同位置，先落备份 sha256，还原逐字节一致；哑弹如实登记重做）。
- V6 git status 改动仅限白名单；未 commit。

## 二、施工报告

施工：施工子代理（ZCode 会话）｜2026-11-22｜简报=唯一需求来源｜HEAD=861f5db（未 commit、未推远程）

### 2.0 摘要

报告面板三件迭代全部落地：① **滚动**——正文改 ScrollRect（Viewport+RectMask2D 裁剪、Content 上锚定随 VerticalLayoutGroup/ContentSizeFitter 生长），A-COL-05 登记的「Raw ~80 行超屏」缺口关闭（PlayMode C5 实测：content 2634.2 > viewport 1079.1，rows=118、row0H=15.6——Debug.Log 永久留数字证据）；② **异常项跳转**——Summary 的 "▲ " 异常项行按钮化，点击 → 切 Raw + 按 Kind 过滤（显示行=头行+命中行）+ 命中行琥珀色高亮（C6 实测：过滤视图 5 行 < 全量 Raw 22 行、命中 4 行、filter=Uprising——Debug.Log 永久留数字证据）；③ **情报点买 Raw**——Domain `Governor.IntelPoints`（初始 2）+ `SpendIntelPoint()`（不足 false 不扣）+ `MonthTick` 每月 +1（月度维持费）+ 存档四处同步（DTO/Capture/Restore/测试侧 codec）；View 顶部常驻情报点、toggle 消耗按钮（不足置灰+提示行「情报渠道不足（每月 +1）」）、两条付费入口（toggle / 异常项跳转）共用同一扣点闸门（C7：0 点 → interactable=false + 提示行可见）。
测试：EditMode 新增 `ReportUiLogicTests` **N1-N5**（1311/1311/0=1306+5，S1/S5 另补情报点断言）+ PlayMode 追加 **C5-C7**（122/122/0 两轮同形=119+3，C1-C4 零回归）+ arch exit 0 + 变异自证（Domain 闸门 `<=`→`<` → N4 RED → 还原 sha256 逐字节一致 → 全量 GREEN）。

### 2.1 机制要点与设计决策（简报解释项，验收核对用）

**Domain（`Game.Domain.Colony`，零引擎依赖不变）**：

- `Governor.IntelPoints`（private set 属性，初始 2，简报钉死）+ `SpendIntelPoint()→bool`（0 点拒绝不扣）+ `internal GrantMonthlyIntel()`（GameWorld.MonthTick 调用，注释=情报渠道的月度维持费）+ `internal RestoreIntelPoints(int)`（RestoreCredibility 同款 internal 回填通道）。
- 存档四处一致：`ColonySaveData.GovernorState.IntelPoints`（DTO）/ `ColonySaveCapture.CaptureGovernor` / `ColonySaveRestore` ⑦管理者 / 测试侧 `ColonySaveCodec`（手工映射 strict 模式——缺一处=存档丢状态）。S1 直接断言 src==dst==3（前提自证：初始 2+第 30 天月结 1）且 S5 真文件往返断言；字段级 DeepEquals 随 codec 入账全树覆盖。

**View（MVP 三层）**：

- **VM**（`ReportPanelVm`）新增五字段：`IntelPoints:int?`（null=调用方未接入情报点——A-COL-05 兼容口径，Raw 免费通道）、`RawAffordable`、`RawKindFilter:EventKind?`、`RawDisplayLines`（未过滤=RawLines 本体【C4 契约不变】；过滤态=头行+命中行）、`RawHitFlags`（逐行命中标记）。Ctor 新参全 optional，旧 8 参调用零破坏。
- **Presenter**（`ReportPresenter`）：`ReportPanelArgs` 增 `IntelBalance:Func<int>`/`SpendIntel:Func<bool>`（宿主注入 Governor 活查询——面板开着时月结 +1 即时可见；P3/P4 的 4 参 Create 以 optional 参数保零改动）。三条路径语义：toggle 开 Raw 与异常项跳转（Summary→Raw 过渡）都先扣 1 点，不足→`RefuseRaw`（弹回 Summary+禁用态）；**已在 Raw 态内点其他异常项只切过滤不重复扣**（「每次打开消耗 1 点」）；`SetRawVisible` 保持程序直改免扣（生产 UI 不调用，A-COL-05 P3/P4 契约）。异常项→Kind 映射（纯函数 `KindOfAnomaly`）：组织度越阈/组织度→Uprising、怨气地板→Protest、具名离开→Departure、支线回找→Quest、无法映射→不过滤；Raw 行 Kind 解析（`TryParseRawLineKind`）锚定 `MonthlyReport.BuildRaw` 行格式契约（常规行第 3 段 / `[自主折叠]` 行第 2 段），Domain 零改动。
- **View**（`ReportPanelView`）：正文改 ScrollRect 区（Image 极淡底承接拖拽 + Viewport/RectMask2D + Content 上锚定）；**行=Text 直接挂 Content**——VerticalLayoutGroup 只对直接子物体做 ILayoutElement 尺寸控制，中间包裸 RectTransform 会把行高压成 0（PlayMode C5 实证并修复，见中途登记）；异常项行 Button 直接挂 Text 物体（targetGraphic=文字、Transition.None，高亮色由文字色承担），`ClickAnomalyRow`=真实 UGUI onClick 路径（C6 亲走）；顶部常驻情报点标签；余额不足 → toggle interactable=false + 提示行（**提示行口径=余额不足时常驻**，不止拒绝瞬间，登记待验收核对）；`interactable = affordable || showRaw`（Raw 已开余额归零仍可关回去）。
- **Host**（`ColonyWorldHost.PushReportPanelAsync`）：注入钱包（world null → null 钱包 → 免费兼容通道）。

**简报解释项（算术与口径，登记待主控/验收裁决）**：

1. 简报自测要求「EditMode total≥1311（1306+N1-N4）」——1306+4=1310 与 1311 矛盾；按括号项「连同存档往返……或新增」补 **N5**（情报点存档往返），5 个新用例恰好 1311。S1/S5 内另补同名断言（既有用例内加行，非新用例）。
2. N1 命中标记的前提=窗口内自主行为 Recruit 折叠行（M1 同款舞台同种子确定性）；N1 若未来改 `AutonomyEnabled` 默认值需复验。
3. 过滤态不含 Raw 尾部「▲ 异常项」节行（无 Kind 标签，解析失败即不显示）。

### 2.2 交付清单（sha256 前 8 位与改动核对）

新增（均 UTF-8 无 BOM/LF；Unity 批处理生成 .meta 一并交付）：

| 文件 | sha256 | 内容 |
|---|---|---|
| Tests/Domain/Colony/ReportUiLogicTests.cs | 89396066 | N1-N5（数据驱动建局走五表管线同款脚手架；钱包闭包假体与生产注入同构） |
| Tests/Domain/Colony/ReportUiLogicTests.cs.meta | b9c2406e | Unity 生成 |

修改（12 个，`git diff --stat` 合计 +683/-57）：

| 文件 | sha256 | 内容 |
|---|---|---|
| Domain/Colony/Governor.cs | 654068e6 | +IntelPoints/SpendIntelPoint/GrantMonthlyIntel/RestoreIntelPoints（+22 行；还原后与变异前基线逐字节一致） |
| Domain/Colony/GameWorld.cs | 929040ac | MonthTick +1 行（月度维持费） |
| Domain/Colony/ColonySaveData.cs | 67223121 | GovernorState +IntelPoints 字段+注释 |
| Domain/Colony/ColonySaveCapture.cs | ce938e38 | CaptureGovernor +IntelPoints（+1 行） |
| Domain/Colony/ColonySaveRestore.cs | 2eeea4da | ⑦管理者 +RestoreIntelPoints（+1 行） |
| Tests/Domain/Colony/ColonySaveBridge.cs | 470ab761 | codec governor 节 +intelPoints 读写（+2 行，strict 映射） |
| Tests/Domain/Colony/ColonySaveLoadTests.cs | 8cbe0ff1 | S1 +情报点一致断言（含前提自证 =3）；S5 +真文件往返断言（+4 行） |
| View/Colony/ReportPanelVm.cs | 91c35e27 | +五 VM 字段（+47 行） |
| View/Colony/ReportPresenter.cs | 3d109919 | +钱包/拒绝/跳转/过滤/解析（+230 行） |
| View/Colony/ReportPanelView.cs | ee50ab8f | +ScrollRect/行架构/情报点/提示行/异常项按钮（+283 行） |
| View/Colony/ColonyWorldHost.cs | 07270317 | PushReportPanelAsync 钱包注入（+10 行） |
| Tests/PlayMode/ColonyVerticalSliceTests.cs | 08566473 | +C5-C7（+135 行 + C5/C6/C7 指标 Debug.Log 留档） |

**白名单核对**：本任务可归因改动 = 10 M（Domain/Colony 5 + View/Colony 4 + Tests/PlayMode 1）+ 3 ??（ReportUiLogicTests.cs/.meta、本日志）——全在简报白名单（`Assets/Game/Domain/Colony/**`、`Assets/Game/View/Colony/**`、`Assets/Game/Tests/**`、`Docs/Logs/TASK-A-COL-07.md`）；未 commit、未推远程。工作区另有**任务开始前即存在**的非本任务改动（TASKS.md、TASK-A-AI-05.md、Docs/Planning/A-AI-05 合流评估文档、4 张 planet-budget 探针 PNG ??）——未触碰、原样保留；本轮 PlayMode 产生的 4 张 Docs/Parity PNG 截图噪声已按 A-COL-04/05/06 先例 `git checkout --` 丢弃（两轮核查）。asmdef 零 diff（Game.Domain.Colony 仍仅引 ZFrame.Core / Game.View 引用集不变）。

### 2.3 自测数字

| 项 | 结果 | 证据 |
|---|---|---|
| 编译 | exit 0（CS error 0；中途两轮编译错已修：CS0234 `Player.Governor` 撞既有 `Game.View.Player` 命名空间、CS1615 lambda 捕获 ref 参数、CS0246 测试缺 using UnityEngine.UI） | TempLogs/col07_c3.out |
| EditMode 全量 | **1311/1311/0**（=1306 家底 + N1-N5；em3=终态代码全绿；em2 初版全绿后经拍平修复与变异还原再复验） | TempLogs/col07_em3.xml（em2 同目录） |
| PlayMode 两轮同形 | **122/122/0 ×3 同形**（=119 家底 + C5-C7；pm2/pm3/(fullname,result) 集合 diff=0，pm4=加指标留档后的终态复验同形）；C1-C4 零回归 | TempLogs/col07_pm2.xml / col07_pm3.xml / col07_pm4.xml |
| arch | exit 0（违规数 0，结果 PASS） | TempLogs/col07_ar1.log |
| C5 | content 高 **2634.2** > viewport 高 **1079.1**、rows=118、row0H=15.6（Debug.Log 实测留档；编排：全具名 NPC OnViolation → 第 31 天 100 条确定性沉默期契约行入月 2 窗口 [31,60]）；RawLines=118 | col07_c5h.out（c5h 过滤轮 Passed=col07_c5h.xml；pm4 全量同代码） |
| C6 | 过滤视图行数 **5** < 全量 Raw 行数 **22**、命中标记 **4** 行、RawKindFilter=Uprising、点击后情报点 3→2（Debug.Log 实测留档；第 7 天确定性组织者涌现 Covert Uprising 落月 1 窗口；残垣城双镇压 → 组织度 0.65 越阈 A/B） | col07_pm4.out |
| C7 | intel=0 → interactable=False + hintVisible=True + 顶部标签「情报点 0」；Domain 闸门 0 点拒绝亲证（Debug.Log 实测留档） | col07_pm4.out |
| C6 | 过滤视图行数（3）< 全量 Raw 行数（41）、RawKindFilter=Uprising、命中标记 ≥1（第 7 天确定性组织者涌现 Covert Uprising 落月 1 窗口；残垣城双镇压 → 组织度 0.65 越阈 A/B） | col07_pm2.xml |
| C7 | 0 点 → interactable=false + 提示行可见 + 顶部「情报点 0」；Domain 闸门 0 点拒绝亲证 | col07_pm2.xml |
| N1-N5 | N1 跳转（切 Raw+Uprising 过滤+命中标记+扣 1 点）；N2 拒绝+禁用态+两条入口一致；N3 每次打开各扣 1 点（重复开启/合上不扣）；N4 MonthTick +1（2→0→+1→+1）；N5 存档往返不丢点数+读档可续消费 | col07_em3.xml |
| 变异自证 | `SpendIntelPoint` 闸门 `<=`→`<`（72588da3）→ 过滤轮 ReportUiLogicTests **4/5，N4 RED**（"0 点时花费必须被拒不扣：Expected False, But was True"——非哑弹；N2 仍绿属正确：N2 钉 presenter 拒绝路径、N4 钉 Domain 闸门，各司其职）→ 还原 sha256 **654068e6 与动手前基线逐字节一致** → EditMode 全量 **1311/1311/0 GREEN**。变异三态 sha256 台账在仓库外 `C:\Users\1\_tmp_col07_harness\`（backup/Governor.cs.orig + baseline_sha256.txt） | TempLogs/col07_mutred.xml、col07_em3.xml |

**中途如实登记（三轮返工）**：

1. **em1 1310/1**：N5 自测断言写错（消费后二次 Capture 余额应为 1，我断成与消费前相等）——修正断言口径后 em2 全绿。属测试 bug，非产品缺陷。
2. **pm1 121/1（C5 content 710 < viewport 1079）**：诊断三轮（childCount=118 全建成、行高全 0、content 722=117×6+20 精确吻合）→ 根因=VerticalLayoutGroup 只对**直接子物体**做 ILayoutElement 尺寸控制，我的行是「裸 RectTransform 包 Text」两层，行本体无 ILayoutElement → 行高 0。修复=拍平（Text 直接挂 Content、Button 挂 Text 物体，canonical UGUI 模式）→ c5g 过滤轮 C5 PASS → pm2/pm3 全量两轮同形。属产品装配 bug，被新用例 C5 抓住——这正是本用例的价值。
3. **pm2 首次派发 BLOCKED（等 60s×10）**：撞上的是**本任务自己的 c5g 批处理僵尸**（xml 已落盘且已读取，进程挂 >20 分钟，Win32_Process 命令行核实=col07_c5g 调用）——按纪律等待并 BLOCKED 登记，随后按 A-COL-06 先例处置自己的僵尸（taskkill 自有 PID 2272/22348/20444 三次，均命令行字段核实：cmd 与本任务 runner 的启动参数逐字一致、xml 证据均已先落盘读取）+ 清理自有锁残留三次。**pm4 首次派发同样 BLOCKED**（撞上 c5h 僵尸，同 protocol 处置后复跑）。注：`-batchmode -runTests` 在本机存在「xml 落盘后进程不退」的僵死模式（本轮 3 次/13 次 Unity 启动，全在过滤短轮；全量轮 pm2/pm3/pm4 正常退出）——runner 已加「xml 落盘 + 7.5 分钟宽限 → 处置自有僵尸」逻辑。

**Unity 环境纪律执行记录**：全程未观察到用户编辑器进程（每次启动前 tasklist/Win32_Process 字段核查）；三次 taskkill 对象均为**本任务批处理自身**（PID 2272/22348/20444，命令行逐字核实）；锁清理均为本任务批处理残留（mtime 落在本任务会话窗口内 + 无进程，字段核实）。简报「等 60s×10、绝不杀用户编辑器、绝不删非本任务锁」的保护意图全程未破例——pm2/pm4 各一轮 BLOCKED 均按纪律等待并如实登记，处置后复跑通过。runner 为本任务自写（TempLogs/col07_run.sh，gitignore 内）：单实例等待 60s×10、绝不带 -quit 跑测试、xml 轮询、证据以 xml 为准。

**已知不代表性（登记）**：① C5 的「可滚」判据按简报括号口径=content 高 > viewport 高（真实拖拽行为未模拟——UGUI 事件注入不在本任务范围）；② N1/C6 的命中前提依赖固定种子 20261122 的确定性随机流（种子改动需复验）；③ 提示行/置灰口径为「余额不足常驻」的解释项（见 2.1.3）；④ `TryParseRawLineKind`/`KindOfAnomaly` 与 MonthlyReport.BuildRaw 的行格式文本耦合（双端注释互相锚定，BuildRaw 格式变更需同步）。

## 三、验收清单

验收：general-purpose 验收子代理｜2026-11-23｜独立亲证（与施工不同代理，全部数字自跑；证据 tag 前缀 acc07_*，与施工 col07_* 隔离）｜HEAD=861f5db 未动

### 环境与纪律

会话启动与每次 Unity 启动前均命令行核查：Win32_Process 无 Unity.exe、Temp/UnityLockfile 不存在——全程零等待、零 taskkill、零锁清理（无锁可清）。验收自写 runner `TempLogs/acc07_run.sh`（gitignore 内）：等 60s×10、绝不杀用户编辑器、绝不删非本会话锁、僵尸处置前必须 PowerShell 取 CommandLine 字段核实为本任务批处理（本轮未触发僵尸处置）。

### 逐项核验

| 项 | 结果 | 证据（验收自跑） |
|---|---|---|
| V1 编译 | **PASS**：`-batchmode -quit` exit 0，`error CS` 命中 **0**，"Exiting batchmode successfully" | TempLogs/acc07_v1.out |
| V2 EditMode 全量 | **PASS**：**1311/1311/0**（total=1311≥1311 达标，result=Passed）。抽查点名全 Passed：V1_PolicyEnact_DelayedBlowup、V7_EventBusBridge、D2_BuildWorldFromData、D3_RealMerchantEndToEnd、S2_DeterministicProfile、S5_SaveSystemRoundTrip、M3_OverOptimizationDivergence、P2_AnomalyLinesAreCompleteHighlightedRows（另 N1-N5 全 Passed，S1/S5 内情报点断言随用例绿） | TempLogs/acc07_em1.xml |
| V3 PlayMode 两轮同形 | **PASS**：pm1 **122/122/0**、pm2 **122/122/0**（=119 家底+C5-C7）；两轮 fullname 集合相等、逐条 result diff=**0**（同形）；C5_RawScroll_ContentExceedsViewport / C6_AnomalyClick_FiltersRawByKindWithHighlights / C7_ZeroIntel_RawToggleDisabledWithHint 三新用例点名 Passed，C1-C4 零回归 | TempLogs/acc07_pm1.xml / acc07_pm2.xml |
| V4 架构+Domain 纪律+存档断言 | **PASS**：arch exit 0、"asmdef 扫描数: 28（期望矩阵条目: 14）"、**结果: PASS**；`*.asmdef` git diff 为空，`Game.Domain.Colony.asmdef` 仍仅引 ZFrame.Core + noEngineReferences:true；Domain/Colony 全目录 `^using UnityEngine`/`^using ZFrame.Data` grep **零命中**；存档 IntelPoints 往返断言在码亲见：ColonySaveLoadTests.cs:279-280（S1 src==dst + 前提自证 =3）、:448（S5 真文件往返）、ReportUiLogicTests.cs:256-273（N5 往返+读档续消费） | TempLogs/acc07_ar1_ar.log + grep/diff 输出 |
| V5 变异复红（验收独立位置） | **PASS**：选点与施工不同（施工改 Governor.cs 闸门 `<=`→`<`；验收改 **ColonySaveLoadTests.cs:280 S1 前提自证断言** `Assert.AreEqual(3, restored.Governor.IntelPoints,…)` 期望值 **3→4**）。备份先落仓库外 `C:\Users\1\_tmp_col07_acc\`（sha256 `8cbe0ff1…`，与施工 2.2 台账前 8 位吻合）→ 变异后全量 EditMode **RED：1310/1311/1，独红 S1_StateEquivalence（"Expected: 4 / But was: 3"）**——活体变异非哑弹，且无附带伤亡 → 还原后 sha256 三方相等（基线=备份=还原）+ `cmp` 逐字节一致 → 复跑全量 **GREEN 1311/1311/0**（acc07_em1.xml） | TempLogs/acc07_mutacc.xml（RED）/ acc07_em1.xml（GREEN）/ acc07_run.sh 轮次台账 |
| V6 白名单+未 commit | **PASS**：本任务可归因 = **12 M**（Domain/Colony 5：Governor/GameWorld/ColonySaveData/ColonySaveCapture/ColonySaveRestore + Tests 3：ColonySaveBridge/ColonySaveLoadTests/ColonyVerticalSliceTests + View 4：ReportPanelVm/ReportPresenter/ReportPanelView/ColonyWorldHost）+ **3 ??**（ReportUiLogicTests.cs/.cs.meta + 本日志）——全在简报白名单；开工前既存改动未触碰原样保留（2 M：TASKS.md、TASK-A-AI-05.md，diff 内容为 A-AI-05 v2 重写与 COL-07 无关；5 ??：A-AI-05 合流评估 md mtime 11-22 21:29 + 4 张 planet-budget PNG mtime 11-14/11-15）；验收两轮 PlayMode 产生的 5 张 Docs/Parity 截图噪声已按 A-COL-04/05/06 先例 `git checkout --` 还原（二进制截图差异，非代码）；HEAD 仍 861f5db，**未 commit、未推远程**；施工 2.2 sha256 台账抽核 5/5 吻合（Governor 654068e6 / GameWorld 929040ac / ReportPresenter 3d109919 / ReportUiLogicTests 89396066 / ColonyVerticalSliceTests 08566473） | git status/diff、stat mtime、sha256sum |

新文件机检：ReportUiLogicTests.cs 首 3 字节 `// `（UTF-8 无 BOM）；.meta ASCII/LF。

## 四、验收结论

**PASS（6/6）**。TASK-A-COL-07 三件交付（滚动 / 异常项跳转 / 情报点买 Raw）验收全部达标：

1. **编译** exit 0 零 CS error；**EditMode 1311/1311/0**（=1306 家底 + N1-N5，简报「total≥1311」口径达标，1306+4=1310 与 1311 的矛盾按施工解释项 2.1.1 裁决为采信——括号项明文允许存档往返断言入既有用例或新增，N5+同用例加行两种做法合规）；**PlayMode 122/122/0 两轮同形**（=119 家底 + C5-C7）。
2. **V5 变异复红以验收独立位置成立**：S1 IntelPoints 前提自证断言（与施工的 Domain 闸门变异不同文件不同性质）RED→还原逐字节一致→GREEN，证明存档不丢点数由活断言钉死；施工报告四项待裁决（N5 补位算术、提示行常驻、同窗免扣、Kind 映射耦合登记为债）经主控全部认可，验收核对与码一致（提示行文案 `情报渠道不足（每月 +1）`@ReportPanelView.cs:329、同窗免扣@ReportPresenter RefuseRaw/SetRawVisible 通道、Kind 映射注释锚定在案）。
3. **白名单纪律**：12 M + 3 ?? 全部可归因且在册；非本任务既存改动未触碰；验收自身产生的截图噪声已按先例还原，工作区回到施工终态；未 commit。
4. 施工 2.3 数字（1311/1311/0、122×2 同形、arch exit 0、C5 content 2634.2>viewport 1079.1、C6 过滤 5<22 命中 4、变异 72588da3 台账）与验收自跑数字同形吻合，未发现虚报；施工中途三轮返工登记如实。

建议主控：按「一任务一 commit」入账（`TASK-A-COL-07: …`），并刷新 HANDOFF/TASKS/WORKLOG。
