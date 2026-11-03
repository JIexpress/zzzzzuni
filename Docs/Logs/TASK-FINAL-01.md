# TASK-A/B-收口 全量测试基线 + 一键脚本 + 项目文档收口

状态: doing（验收 PASS，待主控收口） | 派发: 2026-10-26 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

**前置**：A-M4-02 与 B-R6/R7 主线完成后。

## 任务简报（主控写）

**目标**：双轨总收口——①`Tools/run_tests.ps1` 一键全量（原生 ctest+Unity EditMode+PlayMode+双校验器，汇总报告+任一失败非零退出）②README 重写（项目是什么/怎么跑/结构图/指标一栏）③方案文档终版对齐（实际实现 vs 方案 v2 的差异清单）④HANDOFF 归档标记。

**设计要点**：
1. `Tools/run_tests.ps1`：依次 ①build_native.ps1 ②Unity EditMode `-runTests` ③PlayMode `-runTests`（若 PlayMode 程序集存在）④ArchitectureValidator.ValidateFromCommandLine ⑤ConfigValidator ⑥命名检查——每步结果汇集为总表输出+`TestReport.txt`（含各段用例数/耗时）；任一失败 exit 1；Unity 纪律内置（锁清理/超时 taskkill）
2. `README.md`（仓库根，新写）：一页纸——双轨架构图（文字版）、五步跑起来（打开 Unity/构建原生/跑测试/跑窗口演示模式清单 --draw/--lights/--shadows/--r5/--fsr/--parity）、当前指标表（用例数/基准数字/里程碑状态）、文档索引（方案/HANDOFF/规范）
3. `Docs/框架设计方案.md` 末尾追加"实现差异附录"：逐系统对照方案 v2 承诺 vs 实际（如 SSR 简化边界/Fade 简版/操作页签演进），诚实标注
4. HANDOFF.md 标记"项目阶段收口"状态+后续路线（R8+ 愿望清单：帧生成/Addressables 切换/行为树/ECS 大规模实体）
5. 自测：run_tests.ps1 亲跑全绿 exit 0

**约束**：可动 Tools/、README.md、Docs/、本任务日志；不 git commit；UTF-8 无 BOM/LF。

**交付物**：一键脚本+README+差异附录+收口 HANDOFF+施工报告四节。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理 ｜ 日期: 2026-10-26 ｜ 结论: 四件交付物全部落地并自测通过（一键脚本两轮全量 ALL PASS exit 0 + 注入失败复现非零退出后恢复）。

### ① 摘要（四交付物）

1. **`Tools/run_tests.ps1`**（新增 363 行）：六段编排——native（build_native.ps1 内嵌 doctest + 补充 ctest）→ Unity EditMode（勿带 -quit）→ PlayMode（程序集不存在自动 SKIP）→ 架构校验 → 配置校验 → 命名检查；每段结果汇总仓库根 `TestReport.txt`（各段用例数/耗时/日志路径）；任一失败 exit 1。**Unity 批处理纪律内置**：单实例检查（检测到 Unity.exe 在跑即拒绝启动）、每次启动前删 `UnityProject/Temp/UnityLockfile`、测试跑不带 -quit、结果 xml 出现后 5 分钟未退 taskkill /PID /F、硬超时兜底 taskkill、**Unity 一律经 Git Bash 包装启动**（写 `TempLogs/runtests_launch.sh` 再 `bash <script>`，规避 HANDOFF 记录的"PS 工具链启动 Unity 授权握手 8 秒假退"）。 verdict 口径：测试段以 results.xml 为准（被杀僵尸但 xml 全绿 = PASS 并标注），校验器段以退出码为准。支持 `-Segments` 部分跑（验收/调试用，默认全量六段）。
2. **`README.md`**（仓库根，新写）：双轨架构文字图（目录树+渲染管线层次）、五步跑起来（开 Unity → build_native → run_tests → 编辑器切片 → 窗口演示）、窗口演示模式清单 8 条（--draw/--lights/--shadows/--r5/--fsr/--fsr4/--parity/--backend，各带预期 OK 输出与出处任务号）、指标表（用例数/基准数字/里程碑状态/外部补验项）、文档索引 9 条。
3. **`Docs/框架设计方案.md` 文末追加"附录 A 实现差异清单"**：A.1 两条事实修正（FSR4 本机不可跑的方案前提证伪；本机主路径实为 FSR 3.1）+ A.2 Track A 逐系统 12 行 + A.3 Track B 逐系统 15 行 + A.4 整块未实现 6 项（FrameGovernor/预算体系、GPU 调试工具链、PSO 磁盘缓存、Metal、帧生成、CI 接入）+ A.5 治理口径补充 3 条。全部差异标注任务号溯源，重点诚实标注：SSR 六条/GI 五条/NPR 五条简化边界（转抄 B-R5-03 清单）、Fade 简版与 IUIWidgetPool 只留接口（A-M3-01）、操作页签三阶段演进与重绑范围三动作（M3-02→03→04）、FSR4 需独显（B-R6-02）、r5/fsr VK 未接 NOTICE 显式（B-R7-02）、别名堆每槽一堆简化（B-R3-02）、遮挡剔除未做（B-R4-02）。变更记录表补"v2+附录"一行。
4. **`HANDOFF.md` 收口标记**：顶部新增"项目阶段收口（FINAL-01）"节（40 任务闭环/双轨全里程碑/测试家底 161+704,980+542+5/三类剩余工作）+ **R8+ 愿望清单 9 项**（帧生成、FrameGovernor+预算体系、Vulkan r5 接线、Addressables、行为树、DataKernel 玩法侧规模化、GPU 工具链、打包多平台、性能调优专项）；交棒清单/进度表/活跃任务/路线图/最近日志/注意事项同步收口改写（B-R7-02 已 commit f5099fe 的事实入快照）。

### ② 变更文件清单

| 文件 | 操作 | 说明 |
|---|---|---|
| `Tools/run_tests.ps1` | 新增 | 一键全量测试脚本（CRLF，.ps1 例外；ASCII 无 BOM） |
| `README.md` | 新增 | 仓库根一页纸文档 |
| `Docs/框架设计方案.md` | 修改 | 变更记录 +1 行；文末附录 A（约 +90 行） |
| `HANDOFF.md` | 修改 | 收口标记 + R8+ 清单 + 各节刷新 |
| `Docs/Logs/TASK-FINAL-01.md` | 修改 | 本施工报告 |
| `UnityProject/Assets/ZFrame/Tests/Editor/TempFailingTestForFinal01.cs`(+meta) | 临时新增→**已删净** | 注入失败自测用；`git status` 确认无 UnityProject 残留 |
| `TestReport.txt` | 生成物 | run_tests.ps1 产出（每次运行覆盖；未入 .gitignore，见风险 ②-1） |

### ③ 自测数字（全部实跑取证，2026-10-26）

| # | 项 | 结果 | 证据 |
|---|---|---|---|
| 1 | 全量第 1 轮（六段） | **ALL PASS 6/6，exit 0**，TestReport.txt 产出 | native 161 用例/704,980 断言 0 失败；editmode 542/542；playmode 5/5；arch/config/naming 各 exit 0；总墙钟约 5 分钟 |
| 2 | 注入失败（EditMode 临时用例 `Assert.Fail`） | **exit 1**，`RESULT: FAILED (1 failed: editmode)`，TestReport.txt 标注 `editmode FAIL tests failed: total=543 passed=542 failed=1 (Unity exit=2)` | 控制台输出 + TestReport.txt；命令 `run_tests.ps1 -Segments editmode` |
| 3 | 恢复后全量第 2 轮（六段） | **ALL PASS 6/6，exit 0**，editmode 回到 542（恢复实证） | 控制台输出 + TestReport.txt（最终在盘版本=本轮） |
| 4 | Unity 纪律 | 两轮共 11 次 Unity 启动零假退（经 bash 包装）；启动前删锁、测试跑无 -quit、xml 轮询 | TempLogs/runtests_launch.sh/.out、各段日志 |
| 5 | 文件规范 | 全部改动文件 UTF-8 无 BOM；.md LF；.ps1 CRLF（file-conventions 例外） | `head -c 3` BOM 检查 + `git ls-files --eol` + `file` |

**最终用例数总表（FINAL-01 后基线）**：

| 段 | 用例 | 断言/口径 | 结果 |
|---|---|---|---|
| 原生 doctest（zrender_test，=ctest 唯一注册项） | **161** | **704,980 断言** | 全绿 |
| Unity EditMode（NUnit） | **542** | asserts 属性不填充（适配器口径，见附录 A.5） | 全绿 |
| Unity PlayMode（NUnit） | **5** | 同上 | 全绿 |
| 架构校验 | 21 asmdef 扫描 | 0 违规（矩阵 10 + Tests 豁免） | exit 0 |
| 配置校验 | 全配置资产 | 0 违规 | exit 0 |
| 资产命名检查 | Assets/ 全扫（白名单处置后） | 0 违规 | exit 0 |
| **合计可数用例** | **708** | —— | **全绿** |

### ④ 已知风险与说明

1. **ctest 辅助证据在本机不可用**：MSYS2 ctest 写 build 树被沙箱拒绝（与 build_native.ps1 注释记录的 cmake 探测同源问题），脚本按设计回退——verdict 以 build_native 内嵌 doctest 运行为准并在 TestReport 标注"ctest: binary not usable here"；ctest 可用的环境会显示"100% tests passed (1 entry: zrender_test)"，ctest 若真报失败仍会判 FAIL（防御纵深）。
2. **`TestReport.txt` 未入 .gitignore**（约束禁改该文件）——untracked 生成物，主控可决定入库或补 ignore 一行（HANDOFF 注意事项已记）。
3. **AGENTS.md 常用命令节"run_tests.ps1 尚不存在"一行已过期**（约束禁改 AGENTS.md）——建议主控改为实际用法一行。
4. **PS-Unity 授权握手规避方式**：脚本把 Unity 命令写入临时 .sh 经 bash 执行——这是对 HANDOFF 既有纪律的实现而非新发明；若他机无此问题该包装亦无害。
5. **验收提示（对应清单第 2 项）**：注入失败复现可直接采用 ③-2 的做法（任一 Tests 程序集加临时 Assert.Fail 用例 → `-Segments editmode` → 验非零退出与 TestReport 标注 → 删净复跑）。
6. **治理口径**：B-R7-02 的 TASKS/WORKLOG 行尚未由主控刷新（其施工与 commit f5099fe 已完成）——本任务约束不动 TASKS/WORKLOG，已在 HANDOFF 活跃任务表记录待办。

## 验收清单（主控写）

1. run_tests.ps1 亲跑 exit 0+TestReport.txt 产出（各段数字齐全）
2. 故意注入一个失败（临时）验证非零退出与报告标注（验后恢复）
3. README 完整性核对（五步可复现——验收代理按文档实跑窗口演示一项）
4. 差异附录覆盖已实现全部系统（抽查 5 项与代码/报告一致）
5. 文件规范+施工报告完整

## 验收结论（验收代理写）

**验收**: general-purpose 验收子代理 ｜ 日期: 2026-10-26 ｜ **裁决: PASS（5/5 项全过）**

| # | 项 | 结果 | 证据（全部独立实跑取证） |
|---|---|---|---|
| 1 | run_tests.ps1 亲跑全量 | **PASS** | exit 0；TestReport.txt（Generated 02:20:42 wall 2:45）六段齐：native 161 用例/704,980 断言 0 失败、editmode 542/542、playmode 5/5、arch/config/naming 各 exit=0；Verdict `ALL PASS (6 passed, 0 skipped, 0 failed)` |
| 2 | 注入失败→非零退出→恢复 | **PASS** | 临时 EditMode 用例 `Assert.Fail`（`Tests/Editor/TempFailingTestForFinal01Accept.cs`）→ `-Segments editmode` **exit 1**，TestReport 标注 `[1] editmode -> FAIL ... total=543 passed=542 failed=1 (Unity exit=2)`、`Verdict: FAILED`；删净 .cs+.meta 后 `git status` 无 UnityProject 残留、`git diff --stat` 仅施工交付三改三增；复跑全量 exit 0、editmode 回 542（在盘 TestReport 终态=绿色） |
| 3 | README 完整性+演示实跑 | **PASS** | 实跑 `--draw` → `D3D12 FIRST DRAW OK`（InfoQueue error count=0、60 帧读回 6/6、exit 0）、`--lights` → `D3D12 LIGHTS OK`（readback checks=6 centerPass=6、exit 0）——与 README §2⑤ 预期输出行逐字一致；五步结构（开 Unity/构建原生/一键测试/编辑器切片/窗口演示）齐全，8 条演示模式+指标表+文档索引在位 |
| 4 | 差异附录抽查 5 项 | **PASS** | ①SSR 六条简化边界与 B-R5-03 §二逐条同（4-tap/常数法线(0,1,0)/alpha 通道粗糙度/线性 mip/Point-Clamp/单跳+边缘 fade）②FSR4 需独显、provider 仅枚举 3.1.5/2.3.4（B-R6-02）③r5 VK 未接 8 着色器/16 入口点+FFX 231 条目 0 个 vk 匹配、NOTICE 显式（B-R7-02）④Fade 简版 CanvasGroup alpha+AnimOut fire-and-forget、IUIWidgetPool 只留接口（A-M3-01）⑤操作页签 M3-02 占位"即将支持"→M3-03 实接重绑+冲突检测、重绑范围=移动/交互/暂停三动作（A-M3-02/03/04）——均一致无夸大 |
| 5 | 文件规范+施工报告完整 | **PASS** | 5 个交付文件首 3 字节均 `23 20`（# 空格，无 BOM）；README.md 0×CR（LF）、tracked .md `i/lf w/lf`；run_tests.ps1 ASCII+CRLF（.ps1 例外合规）；TestReport.txt 无 BOM/LF；施工报告四节齐全（①摘要②变更清单③自测数字④风险）且自测数字与本次独立复跑一致 |

**验收备注（主控后续处理，不构成 FAIL）**：
1. `TestReport.txt` 未入 .gitignore 属**预期**（施工受约束禁改 .gitignore）——主控决定入库或补 ignore 一行（HANDOFF 注意事项已记）。
2. AGENTS.md 常用命令节"run_tests.ps1【尚不存在】"行已过期——主控改写为实际用法。
3. 注入探针用例文件名（TempFailingTestForFinal01Accept）与施工方自测同名异名无冲突，已删净（grep Temp 零命中）。

