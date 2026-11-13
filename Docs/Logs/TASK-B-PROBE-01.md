# TASK-B-PROBE-01 —— fsr4-probe 探针：真设备/null 对照可复核工具（两机可跑）

- **轨道**：B / PROBE（U 盘改进计划 §5 四探针之一）
- **状态**：todo
- **派发**：主控，2026-11-14
- **来源**：`Docs/Planning/ZRender-改进计划-2026-11-13.md` §3-P0/§5；B-FSR-01 验收移交（阳性对照）；B-FSR-02 验收移交（FG 族双设备对照）。**B-FSR-01/02 已闭环**（probeMode 字段在库，ApiVersion=2）——本探针在其成果之上工作

## 任务简报（主控写）

### 0. 使命

回答一个问题：**这台机器的 FSR 4.1 到底支不支持，证据是什么**——并且第三人可复核、第二台机器可复跑。它同时是 B-FSR-01 修复的阳性对照工具（对端 RX 9060 XT 上 null=2 vs 真机=4 的差异应能复现出来）。

### 1. 做什么

1. **探针程序**（原生，新 CMake 小目标，参照 test 可执行目标的声明方式加进 Native 构建）：
   - 链 zupscaler 静态库（测试同款路径），依次：
     a) `ZR_Upscaler_QueryCaps` 取完整 caps（含 `fsr4ProbeMode`/vendorId/deviceId/fsrRuntimePresent/dlssRuntimePresent）
     b) null 设备路径 provider 枚举（条数+版本名列表）
     c) 真实设备路径 provider 枚举（DXGI 枚举适配器→D3D12CreateDevice→枚举；与 B-FSR-01 修法同源）
     d) **FG 族双设备对照**（B-FSR-02 移交项）：FRAMEGENERATION provider 枚举 null vs 真设备各一次——查 `fg_ffx.h` 现有导出面，能做就做；做不了如实 MISSING+原因
   - 打印对照表：两条路径条数、fsr4Available、probeMode、差异行
   - **两机可跑**：exe 只依赖同目录 FFX DLL（amd_fidelityfx_*.dll + amd_ags_x64.dll）；README 写清对端机跑法（拷 exe+DLL 集或对端重建）
2. **输出落盘**：`Tools/Probes/out/fsr4-<时间戳>.json`（caps+两路枚举+FG 对照+host 信息）——文件名白名单与 out 目录边界断言照 `env-probe.js` 的写法（Mimosa 复查口径），**不许拼接未校验路径**
3. **登记**：`Tools/Probes/README.md` 登记表加 `fsr4-probe` 行（测什么/怎么跑/最近一次结果）；探针不接入引擎任何流程
4. **本机跑一次存档**（780M）：预期负对照（null=真设备=2 条 3.1.5/2.3.4、fsr4Available=0、probeMode=0）——结果与 B-FSR-01 施工/验收数字一致即为可信；**阳性对照 MISSING（本机硬件）**，在 JSON 与 README 里如实标注"对端机补验"及步骤

### 2. 约束

- 约法三章四判据：真跑（可执行入口）/真输入（DXGI+FFX 真值）/缺就是缺（MISSING+原因，不填 0）/可复核（JSON+README 复跑说明）
- 探针**不进** zrender.dll、不进 Unity 绑定、不改引擎行为；`ZR_ApiVersion` 不动（只读消费）
- `build_native.ps1` 构建链内新增目标须 /WX 零警告；既有 351 用例不受影响（只增不减）
- 施工不 commit、不改 TASKS/HANDOFF/WORKLOG；UTF-8 无 BOM / LF

### 3. 完成判据

1. 本机一次真跑：JSON+控制台对照表入 `Tools/Probes/out/`，数字与 B-FSR-01 验收存档一致
2. `Tools/Probes/README.md` 登记完成（含对端机复跑步骤）
3. FG 族对照：有数字或 MISSING+原因，二者居其一
4. 验收代理亲自重跑探针，输出可复现（时间戳不同、能力数字一致）

## 施工报告（施工代理，2026-11-14）

### 1. 改动文件清单（git numstat / status，+增/−删）

| 文件 | 行数 | 内容 |
|---|---|---|
| `Native/tests/fsr4_probe.cpp` | 新增（759 行） | 探针主体：a) `ZR_Upscaler_QueryCaps` 取完整 caps（C ABI 只读消费，含 `fsr4ProbeMode` 三态）→ b) null 设备 provider 枚举（UPSCALER 族，条数+版本名+versionId）→ c) 真设备枚举（DXGI 首个非软件适配器 → `D3D12CreateDevice`，feature level 12.0→11.1→11.0 与 B-FSR-01 修法同源）→ d) FG 族双设备对照（`zr::FrameGenerator::QuerySupport`——fg_ffx.h 现有导出面，null/真设备各一次）→ e) 控制台对照表 + JSON 落盘。JSON 文件名白名单（`^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)+$` 手写等价实现）+ out 目录边界断言（最终路径必须以常量 out 根为前缀）照 `env-probe.js` 口径；输出目录自 exe 目录向上找仓库根（标记=`Tools/Probes/README.md`），找不到落 exe 同目录 `out/`（便携模式，outMode 字段如实记录）。退出码 0=JSON 已落盘（支持与否是数据不是失败）、1=out 目录致命 IO。MISSING 项不填默认值 |
| `Native/CMakeLists.txt` | +36/0 | 新增 `fsr4_probe` 目标：`add_executable(tests/fsr4_probe.cpp)` + 链 `zkernel/zrhi/zrender_core/zupscaler/zplugin/d3d12/dxgi`（zplugin 公共 include 根承载 `zplugin.h`/`zupscaler.h`/`fg_ffx.h`）+ FOLDER `ZRender/Tests` + `EXCLUDE_FROM_DEFAULT_BUILD`（仅 Release 默认构建，与窗口程序同类：真机工具不进 ctest）+ POST_BUILD 拷 4 个 FFX 运行时 dll（upscaler/loader/framegeneration/ags）到 exe 目录 |
| `Tools/Probes/README.md` | +12/−1 | 登记表 `fsr4-probe` 行由「缺」改「可用」（测什么/怎么跑/本机最近一次结果）；新增 "fsr4-probe 探针" 节：本机跑法、对端机两法（拷 exe+DLL 集或整仓重建）、exe 实际导入表（dumpbin 实证）、读表口径、阳性/负对照判读、边界声明 |
| `Tools/Probes/out/fsr4-2026-11-13T22-28-29.json` | 新增 | 本机真跑存档（见 §3） |
| `UnityProject/Assets/Plugins/ZRender/zrender.dll` | 二进制（非本任务改动） | build_native.ps1 步骤 4/4 标准链自动部署的伴生产物——源码与 HEAD 完全一致（375/822,139 全绿自证），与 HEAD dll 的字节差仅 4 处链接器时间戳位（文件尺寸 388,608 不变），按 ADR-004 留主控提交 |

**未触碰**（铁律自证）：`Native/zplugin/zplugin.h` 签名、`zrender.def`、`UnityProject/Assets/ZRender/**`（探针不进 dll/不进绑定）、`TASKS.md`/`HANDOFF.md`/`WORKLOG.md`（git status 里 TASKS.md 的 2 行改动是主控派发 B-PLANET-07/B-PROBE-01 时写下的状态行，先于本任务开工即存在）。

### 2. 探针用法

- **本机**：`powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1` 后直接跑 `Native/build/bin/Release/fsr4_probe.exe`。POST_BUILD 已拷 4 个 FFX dll 同目录；JSON 落 `Tools/Probes/out/fsr4-<UTC时间戳>.json`；退出码 0=落盘成功（FSR4 支持与否是数据不是失败），1=out 目录致命 IO。
- **对端机（免仓库，首选）**：拷 `fsr4_probe.exe` + 4 个 FFX DLL（`amd_fidelityfx_upscaler_dx12/loader_dx12/framegeneration_dx12.dll` + `amd_ags_x64.dll`）到**同一目录**直接运行；JSON 落该目录 `out/`（outMode=portable-exe-dir）。exe 实际导入表（dumpbin 实证）：`d3d12/dxgi/KERNEL32/UCRT`（Win10/11 自带）+ `MSVCP140/VCRUNTIME140/VCRUNTIME140_1`——缺 VC++ 2015-2022 x64 运行时时装一次 Redistributable 或从开发机拷这 3 个 DLL。
- **对端机（整仓重建）**：同步仓库后跑 `Tools/build_native.ps1`，跑 `Native/build/bin/Release/fsr4_probe.exe`——与本机等价。
- **复现性**：两次运行时间戳不同、能力数字应逐位一致（本机 §3 两次独立跑已自证）；`comparison.shape` 字段直接给出 negative-control / positive-control 形态结论。

### 3. 本机真跑数字（2026-11-13T22:28:29Z，780M/RDNA3 iGPU，vendor=0x1002 device=0x15BF，驱动环境同 B-FSR-01 存档机）

| 项 | 数字 | B-FSR-01 验收存档 | 一致性 |
|---|---|---|---|
| caps `fsrRuntimePresent` / `fsr4Available` / `fsr4ProbeMode` | 1 / **0** / **0** | 1 / 0 / 0（01:17:04 临时探针） | **逐位一致** |
| provider null 路 | **count=2**：3.1.5, 2.3.4 | 2 条（3.1.5/2.3.4） | **逐位一致** |
| provider 真设备路（D3D12CreateDevice(780M, FL12.0)） | **count=2**：3.1.5, 2.3.4 | 2 条（3.1.5/2.3.4） | **逐位一致** |
| 对照形态 | negative-control（两路一致、无 4.x） | 负对照成立 | **一致** |
| selfCheck | probeModeConsistent=true；fsr4AvailableMatchesRealList=true（C ABI 缓存路径 vs 本进程直枚举两条独立路径互证） | — | 自洽 |

- **JSON 存档**：`Tools/Probes/out/fsr4-2026-11-13T22-28-29.json`（missing=[]，positiveControl.status=MISSING，references 引 B-FSR-01 §5 与改进计划 §3-P0 两处出处）
- **阳性对照 MISSING（本机硬件，如实标注）**：本机真设备枚举无 4.x provider——阳性对照（null=2 vs real=4 含 4.1.2）需 RDNA4/RX 9000 级硬件，JSON `positiveControl` 节与 README 均已标注**对端机 RX 9060 XT 补验步骤**（免仓库拷跑或整仓重建→运行→JSON 回填 `Docs/Logs/TASK-B-FSR-01.md` 与 Probes README）。
- **便携模式实测**（模拟对端机免仓库跑法）：exe+4 DLL 拷到仓库外临时目录运行——outMode=portable-exe-dir，数字与仓库模式**逐位一致**（null=real=2、fsr4=0、probeMode=0、FG 两路 Available(3.1.6)），运行档存 `TempLogs/TASK-B-PROBE-01/fsr4-2026-11-13T22-29-05.json`（TempLogs 不入库）；临时目录已清理。

### 4. FG 族双设备对照（B-FSR-02 移交项——有数字，非 MISSING）

用 `fg_ffx.h` 现有导出面 `zr::FrameGenerator::QuerySupport(void* nativeDevice)` null/真设备各一次（fg_ffx.cpp 的 `EnumerateFgProviders` 走 `FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION` 族 provider 枚举）：

| 路 | support | returnCode | providerName |
|---|---|---|---|
| device=null | 3 (Available) | 0 (OK) | 3.1.6 |
| device=real | 3 (Available) | 0 (OK) | 3.1.6 |

本机 780M 上 FG 族 null 与真设备两路一致（均 Available/3.1.6，与 fg_ffx.h 文件头既有 `--fg` 实测 `support=3 rc=0 provider='3.1.6'` 互证）——FG 族双设备对照**首次有数**（B-FSR-02 审计 A2"FG 族未做双设备对照"补上本机一半；null 口径在本机未出现截断差异，阳性差异是否存在于 RDNA4 侧由对端机补验一并回答——探针已在 JSON 里记录该对照数据，无需再改代码）。

### 5. 构建与测试数字

- `Tools/build_native.ps1`：**ALL GREEN**（configure + build /W4 /WX 零警告 + 全量 doctest + 部署 4/4 步全过）；构建前代理变量核查=本机 env 无代理变量（交接档案 §3.2）
- `zrender_test.exe`（build_native 步骤 3/4，repo 根 CWD）：**375 用例 / 822,139 断言，0 failed / 0 skipped**——与 B-PLANET-04 基线 375/822,139 **完全一致（只增不减：本任务零 doctest 新增，探针不进 zrender_test）**
- 中途编译错误 1 次（`MAX_COMPUTER_NAME_LENGTH` 不在 WIN32_LEAN_AND_MEAN 头集内）已修（本地缓冲常量替代），修后一次通过——非 /WX 警告，如实记录

### 6. 遗留风险与移交

1. **阳性对照仍缺位（MISSING 项，不计 FAIL）**：修复在支持硬件上产生 true 的实证只有对端机文档证据（RX 9060 XT 2026-11-13 实测，改进计划 §3-P0）——探针已两机可跑且便携模式本机实测过，补验只差"对端机跑一次+JSON 回填"这一步。
2. **FG 族阳性差异未知**：780M 上 FG 两路一致；FG 族 null vs 真设备是否存在 UPSCALER 族同型截断差异（null < real），要等对端机补验的 JSON 才能回答——现有数据诚实记录了这一点（fg_ffx.cpp 注释口径不变）。
3. **exe 便携依赖 VC 运行时**：导入表含 MSVCP140/VCRUNTIME140/140_1——对端机若从未装过 VC++ Redistributable 需补装或拷 3 个 DLL（README 已如实登记；未采用 /MT 静态 CRT 方案——五个静态库按 /MD 编译，单改 exe 会造成 CRT 混链，不为便携性引入引擎构建风险）。
4. **provider 版本串走内部静态面**：探针取版本名经 `ZupFsrEnumerateProviders`/`QuerySupport`（C++ 静态链接面）——与 B-FSR-02 遗留的"版本号进 UI 需新 C ABI 导出"不冲突，探针不要求任何 ABI 变更。
5. **zrender.dll 伴生变更**：标准链步骤 4/4 重部署，与 HEAD 字节差仅链接器时间戳（4 字节/尺寸不变）——留给主控提交时知悉，无需任何处置。

### 7. 验收建议

建议**验收 PASS 候选**：新目标编译零警告入链（/W4 /WX）+ 本机真跑负对照与 B-FSR-01 验收数字逐位一致 + JSON 白名单/边界断言与 env-probe 口径同源 + FG 族对照有数 + README 登记（含对端机两法与判读口径）+ 375/822,139 不变。验收代理复跑时注意：`zrender_test` 需从 repo 根跑（TASK-G-63 口径）；探针重复运行 JSON 时间戳不同但能力数字应逐位一致（自证可复现）；`positiveControl.status=MISSING` 是本机硬件真相，按 B-FSR-01 同款裁决不算 FAIL。

## 验收清单（主控写，验收代理逐条跑并出证据）

- **F1 范围**：tracked diff 恰为 fsr4_probe.cpp（新增）/CMakeLists.txt/README.md/2 个 JSON/本日志/dll 伴生；zplugin.h、zrender.def、ZRender/Bindings、UnityProject 源码零改动；TASKS/HANDOFF/WORKLOG 未被施工触碰
- **F2 复现**：亲跑 fsr4_probe.exe **两次**——时间戳不同、能力数字（caps/provider 两路/FG 对照）逐位一致；JSON 各自落盘
- **F3 互证**：数字与 B-FSR-01 验收存档（null=真设备=2 条 3.1.5/2.3.4、fsr4=0/probeMode=0）逐位一致
- **F4 FG 族**：数字在档（780M 两路 3.1.6 Available）——B-FSR-02 移交项闭环本机侧
- **F5 便携复现**：亲测拷 exe+4 DLL 到仓库外临时目录运行一次（数字逐位一致），测完清理
- **F6 构建**：亲跑 build_native.ps1 ALL GREEN /WX；repo 根 zrender_test.exe 375/822,139 0 failed；dll 伴生变更归因核实（源码 HEAD 一致、仅链接时间戳 4 字节、尺寸不变——重编比对）
- **F7 规范**：新增文件 UTF-8 无 BOM/LF；四节结构完整

验收输出 PASS/FAIL + 逐条证据。阳性 MISSING（本机硬件）按裁决不计 FAIL。你独占原生构建链（B-PLANET-07 在 Unity 侧）。

## 验收结论（验收子代理独立取证，2026-11-14）

**总结论 = PASS（候选可 commit）**。F1–F7：**7 PASS + 0 FAIL + 0 MISSING**；`positiveControl.status=MISSING` 为本机 780M 硬件真相（B-FSR-01 裁决口径），不计 FAIL。施工报告自述与独立取证全部吻合。

关键取证（验收代理亲自执行，非转抄施工报告）：

| 项 | 结论 | 证据 |
|---|---|---|
| F1 范围 | **PASS** | tracked diff 恰为 6 文件：`Native/CMakeLists.txt`(+36) / `Tools/Probes/README.md`(+12/−1) / `zrender.dll`(伴生) + `TASKS.md`(+2/−1，内容为主控派发行 B-PLANET-07/B-PROBE-01 两行，与报告所述一致) + `WorldOriginMath.cs`(+25)/`WorldOriginMathTests.cs`(+93)（**B-PLANET-07 Unity 侧并行 WIP**，其日志 `Docs/Logs/TASK-B-PLANET-07.md` 在档，与本任务无关）；`git diff -- Native/zplugin/ Native/zrender.def Native/zrender/ Native/zupscaler/ Native/zrhi/ Native/zkernel/ UnityProject/Assets/ZRender/` **全空**；HANDOFF/WORKLOG 零改动。untracked 恰为：fsr4_probe.cpp + 2 个 fsr4 JSON + 本日志（本任务）+ TASK-B-PLANET-07.md / env JSON / linear-bridge 日志（并行任务产物） |
| F2 复现 | **PASS** | 亲跑 `fsr4_probe.exe` 两次（exit=0）：`out/fsr4-2026-11-13T22-38-34.json` / `out/fsr4-2026-11-13T22-38-37.json` 各自落盘；剥离 probedAt/outFile/exePath/computerName 后两次能力载荷**逐位相同**；控制台 diff 仅 2 行（时间戳行 + JSON 路径行）不同 |
| F3 互证 | **PASS** | 与施工两份存档（22-28-29/22-31-25）及 B-FSR-01 验收存档三方逐位一致：null=real=**2 条（3.1.5/2.3.4**，versionId 0xF5A5CA1E00C01005/0xF5A5CA1E00803004）、fsrRuntimePresent=1、fsr4Available=**0**、fsr4ProbeMode=**0**、dlssAvailable=0、shape=negative-control、selfCheck 四项 true、missing=[] |
| F4 FG 族 | **PASS** | 两路 support=3(Available) rc=0(OK) provider=3.1.6（null 与 real 逐位一致，fgSupportEqual=true）——B-FSR-02 移交项本机侧数字在档 |
| F5 便携复现 | **PASS** | 亲测：fsr4_probe.exe + 4 FFX DLL 拷至仓库外临时目录运行 exit=0，outMode=portable-exe-dir，JSON 落 exe 同目录 `out/`；与仓库运行能力载荷除 outMode 字段（模式本身，合法差异）外**逐位一致**；临时目录已删除 |
| F6 构建 | **PASS** | 验收删 `Native/build` 后亲跑 `build_native.ps1` **全量重编**：ALL GREEN exit=0；fsr4_probe.cpp 真实编译（构建日志行 117）+ exe 链接 + POST_BUILD 4 DLL 拷贝（行 179-180）；/W4 /WX 下零编译警告（日志中 FAILED/WARNING 均为 doctest 用例运行期打印，非编译器输出）；`zrender_test.exe`（repo 根 CWD）**375 用例 / 822,139 断言 / 0 failed / 0 skipped**（行 349-350），与 B-PLANET-04 基线一致 |
| F6 dll 归因 | **PASS** | 三方字节比对（尺寸均 388,608）：**提交候选 vs HEAD 恰 4 字节差** = 两处 4 字节链接器时间戳的低 2 字节（PE TimeDateStamp@264：HEAD 2026-11-13T21:49:15Z / 候选 22:27:51Z；debug directory@187204 同值）——与报告「仅时间戳、尺寸不变」完全一致。验收全量重编产物 vs 候选另多 7 字节差 = `__TIME__` 编译宏文本（06:36:35 vs 05:41:58，日期 Nov 14 2026 相同）——全量重编刷新编译期宏，非源码差异，源码语义与 HEAD 一致。验收后已恢复提交候选 dll（sha256 0c9ad3d9…与备份一致），工作区保持施工原状 |
| F7 规范 | **PASS** | fsr4_probe.cpp / 本日志 / 2 JSON + CMakeLists.txt / README.md 字节级核验：无 BOM（首 3 字节非 EF BB BF）、CR=0 全 LF、strict UTF-8 解码通过；cpp 以 `}\n` 收尾。本节追加后日志四节结构完整（简报/施工报告/验收清单/验收结论） |

给主控的备注：

1. **阳性对照 MISSING 维持**（本机 780M 硬件真相）——对端机 RX 9060 XT 补验步骤在 README fsr4-probe 节，补验后 JSON 与一行结论回填 `Docs/Logs/TASK-B-FSR-01.md`。
2. 验收的全量重编曾把 UnityProject 的 zrender.dll 刷新为 `__TIME__` 不同的重编版；**已恢复为施工代理的提交候选**（与 HEAD 仅 4 时间戳字节差，评审噪声最小），主控提交时知悉即可。
3. 探针 JSON `engine.apiVersion=3` 只读消费（`Native/zplugin/` diff 为空佐证），本任务未动 ABI、未动引擎行为。
4. 验收新增 2 个 JSON 存档（22-38-34/22-38-37）为 out/ 目录 untracked 产物，属正常探针输出；便携档在仓库外已删，数字以本节记录为准。
5. 验收窗口期间 B-PLANET-07 的 Unity 侧 WIP 持续推进（`ZRenderSceneBridge.cs`/`ZrSceneMath.cs` 修改 + `WorldOriginPlanetLink.cs`/`Bindings/ZrPlanetChannel.cs`/`Runtime/Planet/` 等新文件出现）——均在 `Assets/Game/**`、`Assets/ZRender/**` 的 PLANET-07 范围内，无探针相关路径，归因不受影响；F1 表中 tracked diff 数字为验收取证时点快照。
