# TASK-X-NAMES-01 —— 资产命名规则扩容：合法化 T_ENV_*.png，清 6 处存量红灯

- **轨道**：X / HYG（A-CONF-01 验收移交的存量红灯）
- **状态**：doing
- **派发**：主控，2026-11-14
- **背景**：naming 段自 A-CONT-01（42f5a26，2026-11-09）入库 6 张 `Art/Textures/T_ENV_{Bark,Grass,Metal,Road,Rock,Wood}.png` 起持续 6 FAIL——每次 naming 校验必撞，污染后续所有任务的验收对账。

## 任务简报（主控写）

### 0. 主控裁决（方案二选一已定）

**扩规则，不 rename。** 理由：6 张资产已被场景材质引用（GUID 稳定，改名虽不破引用但 churn 零收益）；命名规则的意图是约束**未来**资产，规则表补上环境纹理模式即达成意图。

### 1. 做什么

1. 读 `UnityProject/Assets/ZFrame/Editor/Tools/AssetNamingRules.cs`（规则表结构与既有前缀语义）+ `AssetNameValidator.cs` + `AssetNamingRulesTests.cs`
2. **扩规则**：新增环境纹理模式 `T_ENV_<Name>.png`（T=Texture 大类前缀，ENV=环境域段，Name=首字母大写标识符）——具体实现形态照既有规则表风格（正则/分段判断，与现文件一致）；规则注释写明依据（A-CONT-01 程序化环境套件命名约定）
3. **测试**（AssetNamingRulesTests）：6 个真实资产名全 PASS；负例各一（`T_ENV_bogus-name.png` 非法标识符 / `T_BOSS_Ogre.png` 未注册域段 仍 FAIL——不许把规则放宽成"啥都过"）；既有用例全绿只增不减
4. **验证**：跑 naming 段校验（`Tools/run_tests.ps1 -Segments naming` 或等价入口，以脚本实际为准）→ **0 违规**；架构段顺带确认不破
5. **不动的**：6 张 png 与其 .meta 零改动；其他规则语义零变化；Native/** 不碰

### 2. 铁律

- 约法三章：施工不 commit、不改看板/HANDOFF/WORKLOG；UTF-8 无 BOM/LF
- 若发现规则表结构与简报假设不符（如域段机制不存在），MISSING+按现文件风格实现等价效果，写清决策进施工报告

### 3. 完成判据

- naming 段 0 违规（6 处红灯清零）；新规则有测试锚定（正例+负例）；既有测试只增不减
- 下一个任务跑 naming 段不再撞这 6 处

## 验收清单（主控写，验收代理逐条跑并出证据）

- **N1 范围**：tracked diff 恰为 2 文件（AssetNamingRules.cs +90/−0、AssetNamingRulesTests.cs +66/−1）；AssetNameValidator.cs 0 diff；Art/Textures/** 与全部 .meta 零改动；未触碰 Native/**、TASKS/HANDOFF/WORKLOG
- **N2 红灯清零**：亲跑 `Tools/run_tests.ps1 -Segments naming`——TempLogs/name_report.txt（新时间戳）违规数 0、PASS
- **N3 负例不放宽**：亲验 `T_ENV_bogus-name.png`（非法标识符）与 `T_BOSS_Ogre.png`（未注册域段）仍 Violation（跑单测或独立 harness）；判定顺序漂移抽查：白名单目录内存量豁免语义不变（读分支代码确认 T_ stem 先走白名单再域段违规）
- **N4 测试**：EditMode 以 xml 为证 total ≥1182 / failed=0；12 个新用例逐条在 xml 且 Passed；既有命名用例 34 个全绿
- **N5 规范**：2 文件 UTF-8 无 BOM/LF；本日志四节结构完整

验收输出 PASS/FAIL + 逐条证据。并行提示：B-PLANET-02 验收代理独占原生链——你只跑 Unity 侧 naming/editmode，不碰 build_native。

## 施工报告（施工代理，2026-11-14——主控自会话报告代录入）

**改动**：AssetNamingRules.cs +90/−0（TextureDomains={ENV} 域段注册制 + T_域段_Name 模式，手写字符判定无正则，判定顺序：前缀表→纹理域段→白名单→ui_→违规，零漂移）；AssetNamingRulesTests.cs +66/−1（12 新用例：6 正例参数化 + 6 负例/边界）。AssetNameValidator.cs 零改动（委托不变）；6 张 png 与 .meta 零改动。

**证据**：EditMode 1167→1182 全绿（+12 本任务、+3 B-PLANET-02 并行，12 新例 xml 逐条 Passed）；dotnet 独立 harness 预验 23/23 后才进 Unity；naming 段 name_report.txt@03:08:44 违规数 0 PASS（改前 6 处 FAIL）；arch PASS。

**取舍**：Name 段=首字母大写+字母/数字/下划线（允许 T_ENV_Bark_02）；模式仅 .png（tga 未来再扩）；域段注册表仅 ENV，T_BOSS_ 等需主控逐个登记——注册表是唯一开关点。

## 验收结论（验收代理，2026-11-14）

**总结论：PASS**（N1–N5 全过，failed=0，无 MISSING 项）

- **N1 范围 PASS**：tracked diff 恰为 2 文件——`git diff --numstat`：AssetNamingRules.cs +90/−0、AssetNamingRulesTests.cs +66/−1；AssetNameValidator.cs 不在 diff（0 diff）；6 张 T_ENV_*.png 及其 .meta 在 `git status`/`git diff --name-only` 均零改动（全树无任何 .meta 入 diff）；无 staged。树内其余改动归因：Native/{tests/test_smoke.cpp,zplugin/zplugin.cpp,zplugin/zplugin.h}+zrender.dll+ZRender Bindings/Tests 5 文件=B-PLANET-02 在途 8 文件集（非本任务）；TASKS.md 仅 X-NAMES-01 行 todo→doing+日志路径=主控派发动作；HANDOFF/WORKLOG 类文件零改动。
- **N2 红灯清零 PASS**：验收代理亲跑 `Tools/run_tests.ps1 -Segments naming`（Unity batchmode exit 0，00:20）；`TempLogs/name_report.txt`@2026-11-14 03:13:22（新时间戳，覆盖施工代理 03:08:44 旧报告）违规数 0、结果 PASS。TestReport：ALL PASS。
- **N3 负例不放宽 PASS**：亲跑 EditMode（验收代理进程 PID 37660）中逐条 Passed——`TextureDomain_BadIdentifier_Violation`（T_ENV_bogus-name.png 仍 Violation，断言"标识符"）、`TextureDomain_UnregisteredDomain_Violation`（T_BOSS_Ogre.png 仍 Violation，断言"未注册"）——规则未放宽成"啥都过"。判定顺序读码确认（AssetNamingRules.cs Check()）：前缀表→纹理域段（合法 Ok 直返；非法仅缓存 textureDomainViolation 不即返）→根目录白名单→目录白名单（Settings、ZFrame/Tests）→ui_→缓存域段违规→兜底违规；`TextureDomain_ValidAnyDirectory_InvalidStemKeepsWhitelist` 亲跑 Passed（ZFrame/Tests/T_foo.png→Whitelisted），存量豁免语义零漂移。独立 dotnet harness 未跑（清单允许项；结论以 Unity 侧用例为准，已满足）。
- **N4 测试 PASS**：亲跑 EditMode xml（`TempLogs/runtests_editmode.xml`，start-time 2026-11-13T19:15:13Z=本地 03:15:13，验收代理本次进程产出）：total=1182 / passed=1182 / failed=0 / skipped=0（≥1182 达标）。12 新例逐条在 xml 且全 Passed：TextureDomainPattern_Passes("T_ENV_{Bark,Grass,Metal,Road,Rock,Wood}.png")×6、BadIdentifier_Violation、UnregisteredDomain_Violation、EmptyName_Violation、LowerCaseStart_Violation、PatternIsPngOnly、ValidAnyDirectory_InvalidStemKeepsWhitelist。既有命名用例 34 个全绿（本类共 46=34 既有+12 新；HEAD 版 8 [Test]+26 [TestCase]=34，只增不减）。+15 归因：本任务+12、B-PLANET-02 在 ZrPluginTests.cs +4/−1 净+3（1167+12+3=1182，对账闭合）。
- **N5 规范 PASS**：2 文件 UTF-8 无 BOM（首字节 75 73 69="usi"）、0 个 CR 字节、`git ls-files --eol` i/lf w/lf（attr eol=lf）。本日志四节结构完整：任务简报/验收清单/施工报告（代录入）原有三节在，本次追加「验收结论」为第四节。

**验收过程备注（给主控）**：
1. N2 期间遇 Unity 单实例占用（PID 16036，03:13:50 启动的 EditMode 批测，非本验收代理启动，疑似并行验收代理持有）——按纪律等待其退出后亲跑成功，未杀进程。该次他人运行（03:14 xml，1182/0）与本验收结论一致但不采信为本人证据。
2. 清单所述 `TempLogs/name_report.txt` 实际在仓库根 `TempLogs/`（run_tests.ps1 的 $LogDir=RepoRoot/TempLogs），非 UnityProject/TempLogs/。
3. TextureDomains 注册表当前仅 ENV；T_BOSS_ 等未来域段需主控逐个登记（施工取舍如实，唯一开关点）；T_ENV_Bark_02 类下划线数字尾缀按规则合法但暂无测试锚定（非清单要求，不判缺口）。
4. 验收期间 `TestReport.txt`（仓库根）被 run_tests.ps1 刷新数次，其中一行 editmode FAIL 为单实例占用所致，最终一次亲跑覆盖为 ALL PASS——工具产物，非树内源文件。
