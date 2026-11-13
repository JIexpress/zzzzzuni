# TASK-B-KERNEL-01 —— 组件定位周期实测断言 + 文档指令集表述纠正

- **轨道**：B / KERNEL（08 需求对照 #7「未验证」补全）
- **状态**：todo
- **派发**：主控，2026-11-14（简报预写）
- **来源**：`Docs/Planning/08-原始需求对照-2026-11-13.md` §二#7——原始要求「约 3 个 CPU 周期定位组件偏移」；实现方式对（`ZkBitsBelow`=POPCNT），但**没有任何周期实测断言**；且文档写 BMI2、代码实际用 POPCNT/TZCNT（BMI1），表述不一致

## 任务简报（主控写）

### 0. 现状

- `Native/zkernel/zkernel.cpp`：`ZkBitsBelow(mask, bit)` 用于组件列槽位定位（:382/:390/:518/:552/:605/:815 等），注释写「硬件 POPCNT 低位置位计数」
- 全仓文档（设计方案/TECH-VALIDATION/README/交接档案）中凡写「BMI2」「pext」处需逐一核对：实际指令是 POPCNT/TZCNT（BMI1）还是确有 pext？**以代码为准改文档**，不许反向改代码凑文档

### 1. 做什么

1. **实测基准（原生，非编造）**：
   - 新增 micro-benchmark（可放 `zkernel_bench.exe` 或测试内）：对 `ZkBitsBelow` 热路径测 cycles/op（`__rdtsc` 或 `__rdtscp` 成对采样，足够大的迭代次数 + 防优化逃逸；典型混合 mask 稠疏分布）
   - 同时测"组件定位端到端"口径：mask 定位 + chunk 偏移换算一条龙每操作周期数（这是需求原话的合理解释）
2. **实测断言（doctest）**：把实测 cycles/op 写成断言——阈值取宽松硬件上限（如 cycles/op < 40，足以证明"硬件指令直查"而非"逐位循环"）；CPU 无 POPCNT 支持时（cpuid 检测）SKIP 并输出 MISSING 说明，**不许静默通过**
3. **文档纠正**：全仓 grep `BMI2|pext|3 周期|三个周期`——每处按实测事实改写（写明：实测 X cycles/op @本机 Zen4、口径、日期）；「约 3 周期」目标如实标注"设计目标，实测 Y"——**没测到的数字不许写**
4. 本机（Zen4）实测数字进施工报告，带时间戳

### 2. 测试与验证

1. `Tools/build_native.ps1` 全绿；`zrender_test.exe --success=false` 全绿（只增不减）
2. `zkernel_bench.exe`（若存在基准入口）输出含新指标；CI 无 POPCNT 分支行为=SKIP+MISSING 说明
3. 基准数字与既有 406M 行/s 口径不混淆（两个口径分开报）

### 3. 铁律

- 约法三章：真跑/真输入/缺就报缺/可复核；**不许用注释代替实测**
- 施工不 commit、不改看板/HANDOFF；UTF-8 无 BOM / LF；/WX 零警告
- 只动 Native/zkernel/（+基准/测试文件）与文档，不碰 zupscaler/zplugin（并行任务在改）

### 4. 完成判据

- 存在一条会红的断言：把实现改成逐位循环（变异）→ 断言红；还原 → 绿（变异验证法）
- 全仓文档无"BMI2 实装"类失实表述；实测数字+口径+日期可复核

---

## 施工报告（施工代理，2026-11-14）

### 一、实测数字（可复核）

- **机器**：AMD Ryzen 7 7840HS w/ Radeon 780M Graphics（Zen4 平台 8C/16T 笔记本，Windows 11 26200）
- **工具链**：MSVC（VS2022 BuildTools）+ CMake 3.31.6-msvc6，Release `/O2 /W4 /WX`
- **实测时间**：2026-11-14 02:31~02:40（UTC+8）
- **方法**：`__rdtsc` 成对采样，每区间 96 次定位（kInner）摊还读对开销，8192 个区间样本取中位；位索引 `k&127` 随迭代在寄存器内变化防外提；结果入全局 sink 防优化逃逸。掩码=26 位散布（bits 5,10,...,125，稠疏混合）。与既有 bench 方法完全一致（同 kInner/kSamples/掩码），仅扩口径。

| 口径 | 实现 | 实测（中位，3 轮） | 断言 |
|---|---|---|---|
| a1 纯位定位 | `ZkBitsBelow` 本体（POPCNT） | **4~5 cycles/op** | doctest `<40` ✓ |
| a2 端到端（mask 定位 + chunk 列偏移表取数） | a1 + `columnOffset[slot]`（L1 载入链） | **5 cycles/op** | doctest `<40` ✓ |
| a3 随机取址（GetComponent 式） | a2 + ChunkIndexOf/RowInChunk 运行时除法 + 行内偏移乘加 | **17 cycles/op** | 只测不断言（含 idiv，诚实数据点） |
| 变异对照：逐位循环实现 | 临时改写（见三） | a1=83 / a2=82 / a3=90 | 断言红（80/82） |
| 指令集（cpuid） | leaf1 ECX bit23 / leaf7 EBX bit3、bit8 | POPCNT=1，BMI1=1，BMI2=1（**PEXT 存在但代码未使用**） | — |

- 测试内同口径复测（doctest MESSAGE，zrender_test.exe）：a1=5、a2=5；变异态 a1=80、a2=82。
- 既有吞吐口径不受影响（两口径分开报，不混用）：Each 全量 3 列求和本轮 **404~407 M 行/s**（与 406M 行/s 门禁口径一致）。
- 结论：定位路径为硬件指令直查（a1/a2 ≈ 1~2 条 POPCNT + 1 次 L1 取数，中位 5 cycles/op）；需求「约 3 周期」为设计目标，**实测 4~5/5，未达 3，如实记录**；逐位循环实现实测 80~90（16~18 倍劣化），40 阈值足以区分两种实现形态。

### 二、改动文件清单（git numstat，施工不 commit）

| 增 | 删 | 文件 | 内容 |
|---|---|---|---|
| 87 | 12 | `Native/tests/zkernel_bench.cpp` | 列定位扩为三口径（a1/a2/a3）+ cpuid 能力检测打印（POPCNT/BMI1/BMI2，注明 PEXT 未使用）+ MISSING 分支输出；人类可读表新增一行，`--json` 封闭指标集不动 |
| 121 | 0 | `Native/tests/test_zkernel_datakernel.cpp` | 新增 2 用例：「CPU 指令集能力自报」（缺 POPCNT/TZCNT 时 WARN 级 MISSING 说明）+「ZkBitsBelow 周期实测断言」（a1/a2 <40 cycles/op，`doctest::skip(!cpuid)` 装饰器整用例 SKIP）；文件头覆盖清单 +1 条 |
| 2 | 2 | `README.md` | zkernel 树注释、DataKernel 基准表：BMI2→POPCNT/TZCNT 定位（实测 5 cycles/op+日期机器） |
| 6 | 6 | `Docs/框架设计方案.md` | §1 映射表/硬件适配行/§5.1 定位机制/R1 验收行/交付对照行：指令集归属纠正+实测数字+日期+口径；「约3周期」标注设计目标 |
| 2 | 2 | `Docs/需求达成对照.md` | #18（BMI2→POPCNT/TZCNT 掩码定位）、#10（4-5 模糊表述→三分口径实测+断言+变异证据+日期机器） |
| 1 | 1 | `Docs/接入指南-给下一个Agent.md` | 需求对照表行：实测数字+口径+日期 |
| 2 | 2 | `Docs/Planning/08-原始需求对照-2026-11-13.md` | #7 ⚠️未验证→✅已验证（补验闭环证据）、遗留表#4（无实测→已实测三分口径） |
| 4 | 0 | `Docs/Logs/TASK-B-R1-01.md` | 追加日期化勘误（原文保留不改）：正文「BMI2」实为 POPCNT/TZCNT；验收反汇编 popcnt×8/tzcnt×12 本身即正确指令集实证 |
| — | — | `Native/zkernel/zkernel.h` | **净改动为零**（变异用后 `git checkout` 还原，`git diff` 空） |

关联产物：`UnityProject/Assets/Plugins/ZRender/zrender.dll` 由 `build_native.ps1` 第 4 步部署刷新（跟踪的构建产物，含重编译内核）。`Docs/TECH-VALIDATION.md` 与 `TASKS.md` 的未提交改动来自并行 B-PLANET-01 线（本任务未触碰）。

### 三、断言与变异验证证据（可复跑）

1. **断言本体**：`test_zkernel_datakernel.cpp` 尾部「ZkBitsBelow 周期实测断言（热路径 <40 cycles/op）…」用例，两条 `CHECK_MESSAGE`（a1<40、a2<40），口径与 bench 同源同参。
2. **变异验证（第一轮，红）**：将 `ZkBitsBelow` 临时改为逐位循环（`while(low){count+=low&1;low>>=1;}` 两段同构），定向重编 zrender_test+zkernel_bench：
   - `zrender_test.exe --success=false` → **1 failed**（恰为本断言用例；其余 352 用例全过，证明逐位循环功能等价、红仅来自周期断言）；失败输出 `CHECK( 80 < 40 ) is NOT correct!`、`CHECK( 82 < 40 ) is NOT correct!`
   - bench：a1=83.00 / a2=82.00 / a3=90.00 cycles/op
3. **还原（绿）**：`git checkout -- Native/zkernel/zkernel.h` → 重编 → 353/353 全绿，a1=5.00/a2=5.00/a3=17.00（bench）与 5/5（测试内 MESSAGE）。
4. **SKIP+MISSING 路径验证**：临时反转 cpuid 判定位（模拟无 TZCNT 机器）→ 重编 → 输出 `WARNING: WARN(false) is NOT correct! logged: MISSING: 本机 CPU 无 POPCNT/TZCNT（BMI1）支持——…用例已 SKIP…`，汇总行 `352 passed | 0 failed | 1 skipped`，Status SUCCESS（不静默通过：MISSING 说明默认输出可见，skip 计数入汇总）。随后判定还原，最终构建复验 353/353。

### 四、文档纠正清单（文件 × 处 × 改法）

| 文件 | 处 | 原表述 → 改法 |
|---|---|---|
| README.md | 2 | 「+ BMI2（基准 406M 行/s）」→「+ POPCNT/TZCNT 定位（实测 5 cycles/op，基准 406M 行/s）」；「406M 行/s（BMI2 实证）」→「406M 行/s（零分配）+ 定位实测 5 cycles/op（POPCNT/TZCNT 硬件直查，<40 断言防退化；2026-11-14 @Zen4 7840HS）」 |
| Docs/框架设计方案.md | 6 | §1 需求映射行标注「约3周期为设计目标」；硬件适配行「BMI2 指令集可用于数据内核」→「实际使用 POPCNT/TZCNT（POPCNT 属 SSE4.2/ABM、TZCNT 属 BMI1；BMI2=PEXT 存在但未实装，cpuid+实测核验）」；§5.1 「x64 用 BMI2 TZCNT/POPCNT」→ 指令集纠正+完整实测三元组（4~5/5/17，口径+日期+机器）+断言与变异；R1 验收「定位≤3 周期」→「实测 4~5/5/17，目标≤3 未达如实」；测试策略行「反汇编断言（BMI2 路径）」→「周期实测断言（<40，SKIP+MISSING）」；交付对照「BMI2/基准」→「POPCNT-TZCNT 掩码定位/基准+实测」 |
| Docs/需求达成对照.md | 2 | #18「SoA+BMI2」→「SoA+POPCNT/TZCNT 掩码定位」；#10「实测中位 4-5（目标≈3）」→ 三分口径+断言+变异+日期机器 |
| Docs/接入指南-给下一个Agent.md | 1 | 「定位周期实测 4-5 非目标 3」→ 补口径（端到端 5 / 含行内除法 17）+日期+机器+证据指针 |
| Docs/Planning/08-原始需求对照-2026-11-13.md | 2 | #7 ⚠️未验证→✅已验证（补验闭环：断言+SKIP+变异+实测三元组）；遗留表#4 「无实测」→「已实测」 |
| Docs/Logs/TASK-B-R1-01.md | 1（追加） | 尾部日期化勘误：正文「BMI2 掩码查询」归属失实，以反汇编 popcnt/tzcnt 实证为准；原文保留 |

- 全仓复扫 `bmi2|pext|3 周期|三个周期`（*.md/*.cpp/*.h/*.cs/*.json/*.ps1）：剩余命中均归属——① 已纠正条目中的目标引述/勘误说明；② 本任务简报与自述；③ `HANDOFF.md:13`、`WORKLOG.md:150`（铁律禁改的看板/交接/历史日志，见六）；④ `TASK-R10-02.md`「第 5–7 参」为参数序号误命中。`TECH-VALIDATION.md` 无周期性性能表述（「周期」仅指开发排期）——核对无失实，未改动。

### 五、测试与构建证据

- `Tools/build_native.ps1`（官方入口）：configure+build(`/W4 /WX`)+test+deploy 全链 **ALL GREEN**，编译警告 **0**（`warning C[0-9]+` 计数=0，复跑两遍确认）。
- `zrender_test.exe`：**353 用例 / 778746 断言** 全绿（基线 351/778743，**只增不减**：+2 用例、+3 断言）。
- `zkernel_bench.exe`：人类可读表含新口径 a1/a2/a3 与 cpuid 能力行；`--json` 指标集未动（门禁兼容）。
- 文件规范：全部触及文件核验首 3 字节无 BOM、CR 计数=0（纯 LF）。

### 六、遗留风险与残留表述归属

1. **受保护文件中的残留**：`HANDOFF.md:13`（「3 周期实测」仍在 08 对照摘要内）与 `WORKLOG.md:150`（B-R1-01 行「BMI2 实证」为历史记录）——铁律「不改 TASKS/HANDOFF/WORKLOG」约束施工代理不动这两处，需主控在闭环刷新时按本报告口径改写（TASK-B-R1-01.md 已有勘误可引用）。
2. **阈值语义**：40 cycles/op 是「硬件直查 vs 逐位循环」的形态判据，不是 3 周期目标的达标线（实测 4~5/5 未达 3，已如实写明）；a3（17）含运行时 idiv，属随机访问路径固有成本，不参与断言（批量查询路径无此除法，对应 a2）。
3. **跳过语义依赖 cpuid**：skip 装饰器在用例注册（静态初始化）时求值 cpuid——结果在进程生命周期内不变，语义安全；doctest 2.5.3 无运行时 SKIP() 宏，`WARN_MESSAGE(false)` 承担 MISSING 输出（已实测可见）。
4. **环境坑（复现记录）**：PATH 上的 MSYS2 cmake（`/c/msys64/mingw64/bin`，4.4.3）对本仓 `Native/build` 树写 `CMakeCache.txt.tmp*` 恒 Permission denied（本机缓存由 VS BuildTools cmake 3.31.6 配置生成）；`cmake --build` 裸调会撞上，**走 `Tools/build_native.ps1` 官方入口自动选对 cmake**。定向增量构建可用 VS BuildTools 的 cmake 全路径。
5. **变异复现说明**：三、2 的变异是临时实验态，未入库（zkernel.h 已还原且 `git diff` 为空）；复跑方法见施工报告三节文字。

### 七、验收建议

- 完成判据逐条对照：①周期实测断言存在且可红（变异已证）✓；②实测数字+口径+日期+机器入本报告与文档 ✓；③全仓活性文档无「BMI2 实装」类失实表述（受保护文件两处残留归属见六.1，建议主控闭环时处理）✓；④353 用例 /WX 零警告全绿 ✓；⑤测试只增不减 ✓。
- 建议**验收 PASS 候选**（待独立验收代理复核：重跑 build_native.ps1、zrender_test、zkernel_bench，抽查文档纠正与 `zkernel.h` 净零改动）。

## 验收清单（主控写，验收代理逐条跑并出证据）

- **K1 范围**：本任务 tracked diff 文件集 = Native/tests/{zkernel_bench.cpp,test_zkernel_datakernel.cpp} + 文档（README.md/框架设计方案.md/需求达成对照.md/接入指南/08-原始需求对照/TASK-B-R1-01 勘误）+ 本日志 + dll 部署；**`git diff Native/zkernel/` 必须为空**（zkernel.h 净零改动是本任务纪律）；不得触碰 zupscaler/zplugin/planets/TASKS/HANDOFF/WORKLOG
- **K2 实测真实性**：亲跑 `Native/build/bin/Release/zkernel_bench.exe`（或测试内基准路径）——a1/a2/a3 三口径数字与 cpuid 能力行输出在（a1 4~5、a2 5、a3 ~17、POPCNT/BMI1/BMI2=1）；数字与本报告一致
- **K3 断言有牙（变异复红）**：读 test_zkernel_datakernel.cpp 尾部两条 CHECK_MESSAGE（<40）与 SKIP 装饰器（cpuid 无 POPCNT→doctest::skip+MISSING 说明，不静默通过）；**亲自做一次变异**：把 ZkBitsBelow 临时改逐位循环→重编→恰 1 用例红→还原→全绿（施工称 80~90 vs 40 阈值）
- **K4 测试**：亲跑 build_native.ps1（/WX 零警告）+ repo 根 zrender_test.exe --success=false ≥353 用例 0 failed（只增不减）
- **K5 文档纠正抽查**：抽 3 处（README/设计方案/08对照#7）——均改为 POPCNT/TZCNT 表述+实测数字+口径+日期；「约 3 周期」处如实标注设计目标与实测差（不许写成"达标"）
- **K6 规范**：触及文件 UTF-8 无 BOM/LF；本日志四节结构完整
- **K7 dll**：zrender.dll 已部署（git status 可见；如与并行线重叠以最终哈希为准记录）

验收输出 PASS/FAIL + 逐条证据。并行提示：你是当前唯一原生链使用者，可放心跑构建。

## 验收结论（验收代理，2026-11-14，独立复跑）

**总结论：PASS（K1–K7 全过，无 MISSING）**

### 逐条证据

| # | 结论 | 证据（验收代理亲跑，2026-11-14） |
|---|---|---|
| K1 范围 | **PASS** | `git diff --stat` 恰为本任务文件集：Native/tests/{test_zkernel_datakernel.cpp(+121), zkernel_bench.cpp(+87/-12)} + 文档 5 处（README/框架设计方案/需求达成对照/接入指南/08对照）+ TASK-B-R1-01 勘误(+4) + zrender.dll(bin) + 本日志（untracked）。**`git diff Native/zkernel/` 输出 0 字节**（zkernel.h 净零改动纪律成立）。zupscaler/zplugin/planets/TASKS/HANDOFF/WORKLOG 零 working-tree 改动。工作树另有非本任务 untracked 文件：TASK-B-PROBE-01/02.md、Tools/Probes/out/env-*.json（B-PROBE 线产物，验收未触碰） |
| K2 实测真实性 | **PASS** | 亲跑 `Native/build/bin/Release/zkernel_bench.exe`：**a1=5.00 / a2=5.00 / a3=17.00 cycles/op**；cpuid 行 `CPU ISA: POPCNT=1 BMI1(TZCNT)=1 BMI2(PEXT)=1（内核实际使用 POPCNT/TZCNT；PEXT 未使用）`；CPU=AMD Ryzen 7 7840HS。与施工报告（a1 4~5/a2 5/a3 ~17）一致。测试内同口径 MESSAGE：a1=5、a2=5。`--json` 指标集仍为封闭原集（spawn/each/migrate/compact），each_m_rows_per_s=406.83 与 406M 门禁口径吻合、与周期口径分开呈现 |
| K3 断言有牙（变异复红） | **PASS** | 断言本体核读：test_zkernel_datakernel.cpp 尾部两条 `CHECK_MESSAGE(pure<40 / full<40)`（:558/:561）+ `doctest::skip(!CpuHasPopcntTzcnt())` 装饰器 + `WARN_MESSAGE` MISSING 分支（非静默）。**变异亲做**：把 `zkernel.h:89 ZkBitsBelow` 逐位循环化（`while(low){count+=low&1;low>>=1;}` 两段同构）→ `Tools/build_native.ps1` 重编（编译 0 警告）→ `zrender_test.exe` **353 用例：352 passed / 1 failed / 0 skipped**，唯红恰为「ZkBitsBelow 周期实测断言」用例，两条失败 `CHECK( 80 < 40 )`、`CHECK( 82 < 40 )`（其余 352 全过 = 逐位循环功能等价，红仅来自周期断言）；变异态 bench：a1=83.00/a2=82.00/a3=90.00（与施工报告一致）。变异期间零 commit。`git checkout -- Native/zkernel/zkernel.h` 还原 → 重编 ALL GREEN 353/353 → **`git diff Native/zkernel/` 0 字节零残留**；bench 复绿 5.00/5.00/17.00 |
| K4 测试 | **PASS** | 亲跑 `Tools/build_native.ps1`（基线与还原后各一遍）：均 exit 0 / ALL GREEN，`warning C[0-9]+` 计数 = 0（/W4 /WX）；脚本 cmake 探针自动选中 VS BuildTools cmake 3.31.6（MSYS2 坑由官方入口规避，未裸调 `cmake --build`）。repo 根跑 `zrender_test.exe --success=false`：**353 用例 / 778746 断言全绿，0 failed 0 skipped**，Status SUCCESS。基线 351/778743 → 353/778746（+2 用例 +3 断言，只增不减） |
| K5 文档纠正抽查 | **PASS** | 抽 3 处实核 diff：① README.md 2 处（树注释/DataKernel 基准表）→ POPCNT/TZCNT + 实测 5 cycles/op + 2026-11-14 @Zen4 7840HS；② Docs/框架设计方案.md 6 处（§1 映射标「约3周期为设计目标」/硬件适配行指令集归属纠正/§5.1 实测三元组 4~5/5/17 + 「设计目标≈3 未达，如实记录」/R1 验收行/测试策略行/交付对照行）；③ 08对照 #7 ⚠️未验证→✅已验证（闭环证据齐全）+ 遗留#4。全仓扫 `bmi2|pext` 与 `3 周期|三个周期|3 个 CPU 周期`：**无任何「已达标 3 周期」表述**，所有「3 周期」处均如实标注设计目标未达；剩余 BMI2 命中逐一定性为——本任务日志自述、TASK-B-R1-01 原文保留（:135 已附勘误且声明以勘误为准）、已纠正条目中的目标引述、bench cpuid 检测代码、TASK-R10-02「第 5–7 参」参数序号误命中、zfgprobe `pExtensions` 子串误命中。施工报告六.1 所列 HANDOFF.md:13 / WORKLOG.md:150 两处受保护残留，已由主控闭环刷新提交 61b0ea2 修正（现文为「目标 3 未达如实记」/「勘误：PEXT 存在未实装」） |
| K6 规范 | **PASS** | 9 个触及文件逐一核验：首 3 字节均无 BOM（EF BB BF 不存在）、CR 计数 = 0（纯 LF）。本日志四节结构完整（简报/施工报告/验收清单/验收结论——本节） |
| K7 dll | **PASS** | `UnityProject/Assets/Plugins/ZRender/zrender.dll` 已部署刷新（git status M，317440 B）。SHA256 `5501d1d7d823c0cbf38de1709297ff3a426ea2521f1b5c34dbdd7f7cc741a4b6` 与 `Native/build/bin/Release/zrender.dll` 一致，部署时间 2026-11-14 02:50 来自验收还原后的绿色构建（变异构建在测试步失败，从未进入 4/4 部署步，Unity dll 无变异态污染） |

### 验收备注（给主控）

1. **变异验证全过程零 commit**；zkernel.h 还原彻底（diff 0 字节），最终构建为绿色态，dll 部署哈希已记录（K7）。
2. 工作树 untracked 的 TASK-B-PROBE-01/02.md 与 Tools/Probes/out/env-*.json 属 B-PROBE 线文件，不在本任务集，验收未触碰；主控闭环时按 B-PROBE 自己的流程处理。
3. TASK-B-R1-01.md 正文（:7/:11）保留原简报「BMI2」字样属「原文保留+勘误为准」策略，非失实残留（:135 勘误已声明效力优先）。
4. 40 cycles/op 阈值语义为「硬件直查 vs 逐位循环」形态判据（变异 80~90 vs 直查 5，区分度 16 倍以上），非 3 周期目标达标线；a3=17 含运行时 idiv 只测不断言——文档与报告口径一致、诚实。
5. 验收链全程仅跑 Tools/build_native.ps1 官方入口，未裸调 cmake；验收期间产生的全部中间输出在 /tmp（验收会话临时区），未写入仓库。
