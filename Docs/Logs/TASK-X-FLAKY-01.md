# TASK-X-FLAKY-01 —— AssemblyConnect 刀锋态阈值余量修正（既有偶发，四次档案记录）

- **轨道**：X / HYG（微任务）
- **状态**：doing
- **派发**：主控，2026-11-14
- **缺陷**：`AssemblyConnectPlayModeTests.ConnectedChain_GroundSettled...` 锚点断言 `<0.05` 在负载波动窗恒超差 5.3e-7 m（0.0500005335）——HANDOFF 已知偶发（复跑即过）+B-PLANET-11 验收+B-PLANET-14 施工（8 轮 A/B 矩阵：全量带 diff 必现/HEAD stash 过/单 fixture 过）——**刀锋态对套件时序敏感，非语义缺陷**（B-PLANET-14 的 1.0~1.7s 帧节奏扰动即触发）

## 任务简报（主控写）

1. **修法**：锚点容差 `0.05 → 0.051`（或等效余量形式），注释写明依据（四次档案记录+8 轮 A/B 矩阵出处：TASK-B-PLANET-14 施工报告 §回归）；**物理语义零放宽声明**：5.3e-7 超差属噪声级，原阈值口径保留注释
2. **核验**：PlayMode 全套**连跑两轮** 114/114/114/114（含负载窗）；EditMode ≥1223 不受影响；arch/naming PASS
3. **白名单**：AssemblyConnectPlayModeTests.cs（其 .meta 如触发）、本日志；其余不碰
4. **staging 纪律**：主控提交时按目录核对 .cs↔.meta 配对（X-ARCH-02 教训）

## 完成判据

- PlayMode 连跑两轮全绿（114/114×2）；注释完整可追溯；全量不受影响

## 验收形态

微任务：验收由 B-PLANET-14 验收代理捎带（其 PlayMode 全套轮即验证场）——主控显式委托，记录在案。

## 施工报告（施工代理写，2026-11-14）

### 1. 交付概览

**修正内容（白名单单文件，git diff --numstat 实测 +17/−2）**：`UnityProject/Assets/ZFrame/Tests/PlayMode/AssemblyConnectPlayModeTests.cs`

- `ConnectedChain_GroundSettled_10Seconds_NoJitterNoExplosion` 锚点断言 `Assert.Less(maxAnchorError, 0.05f, ...)` → **`0.051f`**（断言消息同步标注"物理判据 0.05 + 1e-3 噪声余量"）；summary 注释同步注明门限构成。
- 断言处新增 15 行注释，四层依据全部入码可追溯：
  1. **物理语义零放宽声明**：物理判据语义不变——关节锚点误差 < 5 cm（0.05 m，TASK-A-TECH-02 原口径），0.05 的阈值口径以注释保留；
  2. **刀锋态量化**：实测超差恒为 5.3e-7 m（0.0500005335），噪声级、非语义缺陷；PhysX 多线程求解在负载波动窗非逐位确定；
  3. **四次档案同值记录**：TASK-A-AI-03 §205（0.0500005335，复跑 102/102）、TASK-B-PLANET-11 §202（0.0500005，复跑 114/114）、TASK-X-HAPTICS-01 §73（0.0500005335，复跑 114/114）、TASK-B-PLANET-12 §213（0.0500005335，复跑 114/114——该档案原文"复跑归因口径第四次成立"）；另见 HANDOFF.md「已知偶发」登记；
  4. **8 轮 A/B 归因矩阵出处**：TASK-B-PLANET-14 施工报告 §6（run1/2/4/6 全量带 diff FAIL×4、0.0500005335 四次逐位相同 / run3/5 HEAD stash PASS / 单 fixture 5/5、probes+fixture 9/9 PASS / B-PLANET-11 验收轮无 diff FAIL×1 同值）。
- **余量量级论证（入码）**：1e-3 ≫ 5.3e-7——余量覆盖档案最大观测超差**三个数量级**，只滤噪声不改判据。

**文件清单**：

| 文件 | 变更 | 白名单 |
|---|---|---|
| `UnityProject/Assets/ZFrame/Tests/PlayMode/AssemblyConnectPlayModeTests.cs` | +17/−2 | 白名单内 |
| `Docs/Logs/TASK-X-FLAKY-01.md` | 本节追加 | 白名单内 |

`.meta` 未触发（文件未增删改名，mtime 未变，git status 无该 .meta 条目）。诊断脚本与证据快照留存 `TempLogs/task-x-flaky-01/`（不入树）。

### 2. 核验数字（全部亲跑，证据 xml/报告本次亲写）

```
PlayMode : 2 轮连跑（1 忙等核负载窗内）均 114/114 failed=0
           轮 A：07:54:23Z–07:57:15Z（本地 15:54:23–15:57:15）171.897s——TempLogs/task-x-flaky-01/playmode_load_run1_PASS.xml
           轮 B：07:58:30Z–08:01:22Z（本地 15:58:30–16:01:22）172.564s——TempLogs/task-x-flaky-01/playmode_load_run2.xml
           两轮间隔 75 s（A 末 → B 起），"连跑"成立；负载核全程满转（单 PowerShell 忙等进程 ≈1/16 总 CPU，
           对应档案负载形态 Taskmgr 24~31% 的单进程轻度争抢带）
EditMode : total=1235 passed=1235 failed=0 skipped=0（≥1223 达标 = 基线 1223 + B-PLANET-14 锚定 12，只增不减）
           08:02:32Z–08:02:45Z（12.524s）——TempLogs/task-x-flaky-01/editmode_1235.xml；
           本轮 machine quiet=BUSY（Taskmgr 32.6%，恰为档案负载形态）下全绿
arch     : PASS exit 0，违规 0（TempLogs/arch_report.txt @ 16:03:09，27 asmdef/13 矩阵）
naming   : PASS exit 0，违规 0（TempLogs/name_report.txt @ 16:03:29）
```

### 3. 负载窗校准过程与一项过压发现（如实登记）

1. **档案负载形态辨析**：档案记录的负载带（X-HAPTICS-01 §73 / B-PLANET-12 §213 / B-PLANET-14 §6 均"Taskmgr 24~31%"）是 check_machine_quiet 采样窗内**单进程间歇性轻度负载**；本轮验证先以 4 个忙等进程（≈4/16 核持续满载，check 表 SharePct 各 22.4%）复刻，**形态失配**（SharePct 数字巧合同带、实际争抢强度完全不同）。
2. **过压轮结果（15:49:01Z–15:52:08Z，快照 `playmode_load_run1_FAIL.xml`）**：113/114，唯一失败仍是 ConnectedChain——但失败点是 **`Assert.Less(maxSpeed, 3f)`（:275）实测 10.5036 m/s**，而锚点断言 0.051 **本轮通过**。4 核持续满载造成 PhysX 求解器线程饥饿，关节求解质量**语义级**下降（速度 3.5 倍于界）——档案 8+4 轮从未出现此形态（档案全部失败均为锚点 5.3e-7 噪声级超差），该形态**不是阈值余量可修的对象**（放宽 maxSpeed = 真语义放宽，禁止）。处置：负载校准回档案形态（单忙等核），两轮连跑全绿（§2）；过压轮如实登记为"负载形态边界发现"，见 §5 遗留风险 3。
3. **对刀锋态修正的正面证据**：0.051 门限在最重观测负载（过压轮）下锚点误差仍 < 0.051，且档案形态负载窗两轮 114/114——余量在全部已观测负载形态下闭环。

### 4. 与简报口径的差异（如实登记）

- 简报所写文件路径 `UnityProject/Assets/Game/Tests/PlayMode/` 与实际不符，实际为 `UnityProject/Assets/ZFrame/Tests/PlayMode/`，以实际为准（文件内容、锚点断言均与简报/档案一致）。
- 简报"负载窗"按档案形态执行（单进程轻度争抢，两轮均含）；4 核过压为验证过程中的自造加压试探，未计入验收两轮（其失败点为语义级 maxSpeed，与刀锋态无关，§3-2）。

### 5. 遗留风险

1. **过压形态下 maxSpeed 断言可红（§3-2）**：≥4 核持续满载时 PhysX 求解器线程饥饿会让该用例在 `maxSpeed < 3f` 处失败（实测 10.5）。本机正常验收/CI 流程负载远低于此，但若未来在重载构建机上跑 PlayMode，此用例可能以**另一种签名**红——届时不应套用"复跑归因"或阈值余量，应先查机器负载形态。
2. **0.051 余量与 PhysX 非确定性**：余量滤的是 5.3e-7 级调度噪声；若未来物理改单线程求解（确定性）或降 simulationMode 线程数，刀锋态将消失，余量成为纯冗余（无害，注释已锚定语义口径）。
3. **Docs/Parity PNG 例行重写**：本轮 3 次 PlayMode 全套运行重写了套件捕获 PNG（含任务开始后新出现的 `utility_personality_goap_chain.png` 改动态）——B-PLANET-11 主控裁决 #4 先例，证据有效性以 fixture 隔离轮为准（B-PLANET-15 多相机缺口仍在途），本任务零触碰该批文件。
4. **B-PLANET-14 施工 diff 仍在工作区未 commit**（其 2 文件 + 本日志 + 本任务 2 文件均为 staged 前改动态）——主控提交时按目录核对 .cs↔.meta 配对（简报 staging 纪律；本次 .cs 无 .meta 变化，无需配对动作）。

### 6. 白名单自检与规范

- `git status`：本任务改动 = 白名单 2 项（AssemblyConnectPlayModeTests.cs +17/−2、本日志追加）；其余改动态均为任务前既有（B-PLANET-14 §1 已逐项登记）或套件轮例行 PNG 重写（§5-3）；TASKS/HANDOFF/WORKLOG/看板零触碰；**无 git commit**（HEAD 仍 774b10d）。
- 编码：修正后文件首字节 `2f 2f 20`（UTF-8 无 BOM，od 亲验）、CR 计数 = 0（LF-only，tr 亲验）；本日志同规范。
- Unity 批处理纪律：全程经 run_tests.ps1（单实例前检 + 删锁 + Git Bash 启动 + -runTests 不带 -quit）；负载体为临时脚本，随停随删（stop 标志 + 20 分钟自熔双保险），TempLogs 不入树。
- 施工代理未写验收清单与验收结论（属主控/验收代理职责；微任务验收由 B-PLANET-14 验收代理捎带）。
## 验收结论（捎带）（B-PLANET-14 验收代理写，2026-11-14）

**结论：PASS**（主控可据此单独 commit；捎带委托关系见「验收形态」节）

1. **diff 核对 PASS**：`git diff --numstat` = AssemblyConnectPlayModeTests.cs +17/−2 单文件（白名单内）；`Assert.Less(maxAnchorError, 0.051f, ...)` 门限 + 断言消息「物理判据 0.05 + 1e-3 噪声余量」+ summary 注释 + 断言处四层依据注释（物理语义零放宽声明 / 刀锋态量化 5.3e-7 / 四次档案 TASK-A-AI-03 §205、B-PLANET-11 §202、X-HAPTICS-01 §73、B-PLANET-12 §213 + HANDOFF 已知偶发 / 8 轮 A/B 矩阵出处）全部入码可追溯；B-PLANET-11 §202 档案出处抽查一致（0.0500005、复跑 114/114）；maxSpeed 断言未动。
2. **验证场亲跑 PASS**：B-PLANET-14 验收代理独占 Unity 链亲跑 PlayMode 全套三轮——quiet 轮 **114/114**（ConnectedChain Passed 0.129s，TempLogs/runtests_playmode.xml 本轮新写）；另两轮 113/114，唯一失败均在 **maxSpeed=10.5035982（:275）**，**锚点 0.051 断言两轮均通过**（测试推进越过 :259 才在 :275 失败；旧 0.05 门限在同等负载形态下即红）——修复本体在负载形态下得到正面验证，与施工代理两轮负载窗 114/114 互证（合计 3×114/114）。
3. **备注（回主控，不阻塞）**：两轮 maxSpeed 失败与施工报告 §3-2/§5-1 登记的过压形态签名同值同点（≥4 核级持续负载 → PhysX 线程饥饿确定性轨迹，验收三轮逐位同值 10.5035982 佐证确定性）——运维项：重载机跑 PlayMode 先查负载形态；验收轮2 的负载含验收侧自造成分（ZCode 31.2%），如实登记。
4. **编码复核 PASS**：修正文件首字节 2f 2f 20（UTF-8 无 BOM）、CR 计数 0（LF-only），与本日志一致。
