# TASK-B-FSR-01 —— P0 修复：FSR4 探测传空设备假阴性

- **轨道**：B / FSR（U 盘改进计划 P0）
- **状态**：doing
- **派发**：主控，2026-11-14
- **来源**：`Docs/Planning/ZRender-改进计划-2026-11-13.md` §3-P0（对端机 RX 9060 XT 实测定位）+ `Docs/Planning/08-原始需求对照-2026-11-13.md` §二#16；本机历史 B-R6-02「FSR4 本机不可用=受限如实」即此根因

## 任务简报（主控写）

### 0. 背景（为什么改）

能力探测 `ZupProbeDeviceCaps`（`Native/zupscaler/upscaler_caps.cpp`）上半段已经用 DXGI 枚举到首个非软件适配器（vendorId/deviceId 都拿到），但下一行仍是：

```cpp
outCaps->fsr4Available = outCaps->fsrRuntimePresent && ZupFsr4Probe(nullptr);
```

`ZupFsr4Probe(void* nativeDevice)` 实现在 `Native/zupscaler/fsr_upscaler.cpp:521`，内部经 `ZupFsrEnumerateProviders` → `EnumerateProvidersImpl` 把设备交给 FFX 运行时枚举 provider。FFX 拿不到真实设备时返回的 provider 列表被截断：对端机实测 **null=2 条，真机=4 条（含 4.1.2）** → FSR4 被误标"不支持"。

**`upscaler_caps.cpp` 文件头注释「FFX 对 null 设备返回与真实设备一致的保守列表」已被实测证伪——改代码时必须同步把这句（及 `fsr_upscaler.cpp` 里同型表述，约 :131 附近）改为事实陈述，不得保留。**

### 1. 修法（方向）

1. 在 `ZupProbeDeviceCaps` 里用已枚举的 `IDXGIAdapter1*` 调 `D3D12CreateDevice(adapter, ...)` 创建真实设备（feature level 从高到低取首个成功的），把 `ID3D12Device*` 传给 `ZupFsr4Probe`，用完 Release
2. 设备创建失败（无硬件/CI 环境）→ 回退 nullptr 路径，行为与现状一致——**CI（GitHub Actions L1，无 GPU）必须仍绿**
3. `Native/tests/zrhi_d3d12_window.cpp:9907` 有一处同型调用 `ZupFsr4Probe(nullptr)`（窗口模式测试，本身有真设备上下文）——同步改传真设备
4. 检查 `ZR_Upscaler_QueryCaps`（`Native/zplugin/zplugin.cpp`）有无进程内缓存；有则保持缓存语义（首调探测一次，缓存命中不再创建设备）
5. **不动 ABI**：`ZrUpscalerCaps` 结构体与 `ZR_ApiVersion` 本任务不递增（P1 的 probeMode 字段在 B-FSR-02 才动）

### 2. 测试与验证

1. `powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1` 全绿（/WX 零警告）；**构建前按交接档案 §3.2 清理重复代理变量**（HTTP_PROXY 与 http_proxy 并存会崩 MSBuild）
2. `Native/build/bin/Release/zrender_test.exe --success=false` 全绿（基线 349 用例/778,723 断言；新增用例后数字只增不减）
3. `test_upscaler.cpp` 补 doctest：caps 探测结构稳定性（无硬件环境下字段合法、布尔自洽；**不得断言"本机有 FSR4"**——CI 无 GPU 必须可过）
4. **本机真机对照（施工报告必须贴数字证据）**：用临时验证程序（或借窗口模式测试路径）在本机各跑一次 null 路径 vs 真设备路径的 `ZupProbeDeviceCaps`，打印 provider 条数与 `fsr4Available`。**两条路径结果不一致才算修好**。本机=780M(RDNA3)，FFX DLL 在 `Native/build/bin/Release/` 齐备，预期真设备路径 `fsr4Available=true`
5. 临时验证程序不作为正式探针入库（正式探针是 B-PROBE-01）；验证完删除，不留死代码

### 3. 铁律（约法三章，`Docs/Planning/约法三章-2026-11-13.md`）

- 不许把「未验证」写成「已验证」；真跑的结果带时间戳写进施工报告
- **不 git commit**（主控提交）；不改 TASKS.md/HANDOFF.md/WORKLOG.md（主控刷新）
- 构建产物 `zrender.dll` 入库（ADR-004）：按 build_native.ps1 的部署步骤把 dll 更新到 UnityProject，变更留给主控提交
- 文件规范：UTF-8 无 BOM / LF（.editorconfig 已强制）

### 4. 完成判据

- 本机实测：真设备路径 provider 条数 > null 路径，且真设备路径 `fsr4Available=true`（数字+时间戳进施工报告）
- 全量原生测试绿；无硬件回退路径行为不变（CI 口径不受影响）
- 两处失实注释已改为事实陈述（写明实测差异）
- 施工报告四节之「施工报告」节写全；验收由**不同**子代理出 PASS/FAIL

## 主控裁决（2026-11-14，简报预期修正——施工中途实测触发）

**实测事实**（施工代理本机临时探针，2026-11-14，780M/RDNA3 iGPU，驱动 32.0.31019.2002）：FFX SDK v2.3.0 运行时即使拿到真实 `ID3D12Device`，也不在本机枚举 4.x provider——null 与真设备两条路径一致（均 2 条：3.1.5 / 2.3.4，fsr4Probe=0）。与 B-R6-02「FSR4 本机不可用=受限如实」及交接档案「FSR4 真机需 RX 7000/9000 独显」一致。**简报原判据第 1 条在本机因硬件真相不可满足。**

**判据修正**（验收按此执行）：

1. 原判据 1（本机阳性）→ **MISSING + 原因**：本机硬件/运行时组合无 FSR4 provider；阳性证据=对端机 RX 9060 XT 2026-11-13 实测（null=2 vs 真=4 含 4.1.2，存档于 `Docs/Planning/ZRender-改进计划-2026-11-13.md` §3-P0）
2. 新增负对照判据：修复后本机两条路径结果一致（2 条 / fsr4Available=0）→ 证明「不支持」是硬件真相而非探测缺陷，且修复不引入误报
3. 代码判据不变：真设备指针真实传入 `EnumerateProvidersImpl`（审查确认）、两处失实注释已改、全量原生测试绿、CI 无硬件回退行为不变
4. **阳性验证移交 B-PROBE-01**：fsr4-probe 探针须两机可跑，下次同步到对端机补跑拿阳性对照（结果回填本文件与 Tools/Probes/README.md）

**顺带修正**：项目历史记忆「FSR4.1 主路径 780M 可跑」不准确——以 B-R6-02 与本次实测为准：**本机（780M iGPU + 仓内 SDK v2.3.0 运行时）FSR4 不可用属实**。

## 施工报告（施工代理，2026-11-14）

### 1. 改动文件清单（git diff --numstat，+增/-删行）

| 文件 | 行数变化 | 内容 |
|---|---|---|
| `Native/zupscaler/upscaler_caps.cpp` | +39 / −10 | 修复主体：DXGI 枚举到首个非软件适配器后**保留引用**，`D3D12CreateDevice(adapter, level)` 按 zrhi 同序 12.0→11.1→11.0 建短命真设备传给 `ZupFsr4Probe`，用完 `Release`；建备失败（无硬件/CI）→ `probeDevice=nullptr` 回退，调用链与修复前逐字等价。文件头失实注释「FFX 对 null 设备返回与真实设备一致的保守列表」改为事实陈述（null=2 vs 真设备=4 含 4.1.2，RX 9060 XT 2026-11-13 实测） |
| `Native/tests/zrhi_d3d12_window.cpp` | +20 / −7 | 原 :9907 同型调用改传真设备（`device->D3D12Device()`）；provider 枚举证据块改为 **null 与真设备两路对照打印**（原「device=null, conservative」标签失实，一并修正） |
| `Native/zupscaler/fsr_upscaler.cpp` | +10 / −4 | `EnumerateProvidersImpl` 注释（原 :131 附近）改为事实陈述（null=截断列表，能力判定必须传真设备）；文件头 780M 实测段补真机对照口径（null 与真设备两路均 2 条，2026-11-14 本机实测） |
| `Native/zupscaler/zupscaler.h` | +15 / −9 | 同型失实表述清理：`ZupFsrEnumerateProviders`/`ZupFsr4Probe`/`ZupProbeDeviceCaps`/能力矩阵 Fsr4 条目四处注释改为事实陈述（含 780M 两路径均无 4.x 的新实测）。**结构体/枚举/签名零改动** |
| `Native/tests/test_upscaler.cpp` | +53 / −2 | 新增第 9 节 doctest「ZupProbeDeviceCaps: structure stability」（原 9/10 节顺延为 10/11，文件头覆盖清单补第 8 条） |
| `UnityProject/Assets/Plugins/ZRender/zrender.dll` | 二进制 | build_native.ps1 步骤 4/4 自动部署（01:16，与 `Native/build/bin/Release/zrender.dll` 同构建产物） |

`Native/zplugin/zplugin.cpp` **零改动**（见 §4 缓存核对）。

### 2. 新增 doctest（test_upscaler.cpp 第 9 节）

断言全部为**硬件无关守恒性质**（CI 无 GPU 必须可过，未断言本机有 FSR4）：`ZupProbeDeviceCaps(nullptr)` 静默 false；`sizeof(UpscalerDeviceCaps)==12`（2×uint32+4×bool，B-FSR-02 扩字段时须同步更新）；`fsr4Available ⇒ fsrRuntimePresent`；`nvidiaHardware == (vendorId==0x10DE)`；vendorId==0 时 deviceId==0、非 0 时为已知 PCI vendor 且 deviceId 非 0；null 设备枚举写入数 ≤ capacity；两次探测进程内结论一致。

### 3. 构建与测试数字（2026-11-14 01:16 完成，代理变量已按交接档案 §3.2 清理——本机 env 无代理变量，清理为空操作）

- `Tools/build_native.ps1`：**ALL GREEN**（configure + build /W4 /WX 零警告 + 全量测试 + 部署，四步全过）
- `zrender_test.exe --success=false`（repo 根 CWD）：**350 用例 / 778,737 断言，0 failed / 0 skipped**（基线 349/778,723 → +1 用例 +14 断言，只增不减）。注：从 `bin/Release` 目录跑会因 CWD 相关的着色器路径用例假失败 7 例（基线即如此，TASK-G-63 已知口径），repo 根跑全绿
- `zrhi_d3d12_window.exe --fsr4`：exit=2（受限，非缺陷），`InfoQueue errors=0`、device removed reason=0，输出 `D3D12 FSR4 UNAVAILABLE (reason=FSR4 provider 4.x not enumerated (needs discrete RX 7000/RX 9000) ...)`——与本机硬件真相一致

### 4. zplugin.cpp 缓存语义核对（V4，零改动）

`ZR_Upscaler_QueryCaps` → `ResolveActiveLocked()`（SRWLock 内）：`g_upscalerCapsReady` 门控——首调执行一次 `ZupProbeDeviceCaps`（含短命建备），此后缓存命中**零探测零建备**；`SetPreferred` 亦只刷新缓存不重探测。语义与本任务要求一致，修复只改变首调探测内部传真设备，未触碰 zplugin。

### 5. 本机真机对照证据（V8，主控裁决口径：负对照）

- 机器：AMD Radeon 780M Graphics（vendor=0x1002 device=0x15BF，RDNA3 iGPU），驱动 32.0.31019.2002（2026-05-29），FFX SDK v2.3.0（仓内 vendored）
- **修复后探针（临时程序，链接修复后 zupscaler.lib）**：**2026-11-14 01:17:04**
  - `ZupProbeDeviceCaps`（内部真设备路径）：vendor=0x1002 device=0x15BF fsrRuntime=1 **fsr4Available=0**
  - null 设备路径：**providerCount=2，fsr4Probe=0**（3.1.5 / 2.3.4）
  - 真设备路径（D3D12CreateDevice(780M adapter) 成功）：**providerCount=2，fsr4Probe=0**（3.1.5 / 2.3.4）
- **修复前同程序同机（链接修复前 zupscaler.lib，施工早期运行，未记录时刻——结果与修复后 01:17:04 复跑完全一致）**：null=2/0，real=2/0，`ZupProbeDeviceCaps` fsr4Available=0
- **修复后 `zrhi_d3d12_window --fsr4`（永久可复跑路径）**：caps 行 `fsrRuntime=1 fsr4Available=0`，`provider enumeration (device=null): count=2`，`provider enumeration (device=real): count=2`（运行日志存 `TempLogs/TASK-B-FSR-01/fsr4_window_run.log`，不入库）
- **结论**：本机 null 与真设备两路径结果一致（均 2 条 / fsr4=0）→「不支持」是硬件真相（FFX 对 RDNA3 iGPU 不注册 4.x），修复不引入误报（负对照成立）
- **MISSING（按主控裁决标注，不计 FAIL）**：本机真设备路径 `fsr4Available=true` 且条数>null 的阳性对照——不可满足，原因：本机硬件/运行时组合无 FSR4 provider；阳性证据=对端机 RX 9060 XT 2026-11-13 实测（null=2 vs 真=4 含 4.1.2，存档于改进计划 §3-P0），阳性复验移交 B-PROBE-01 两机探针
- **临时程序已删除（V9）**：`fsr_probe_evidence.cpp/.exe/.obj/build_evidence.bat` 验证完即删，仅留运行日志，无死代码入库

### 6. 代码判据自证（V1/V2/V3/V5）

- 真设备指针真实传入：`upscaler_caps.cpp` 中 `probeDevice` 由 `D3D12CreateDevice(hardwareAdapter, ...)` 产出后直接作为 `ZupFsr4Probe(probeDevice)` 实参 → `ZupFsrEnumerateProviders` → `EnumerateProvidersImpl`（`query.device = nativeDevice`）；用完 `Release`；`hardwareAdapter` 保留引用在设备创建后释放，引用计数路径无泄漏
- 回退路径：`hardwareAdapter==nullptr`（无硬件/CI）或三次 feature level 建备全失败 → `probeDevice==nullptr` → 与旧实现**同一条调用**（`ZupFsr4Probe(nullptr)`），行为逐字等价
- ABI 未动：`ZrUpscalerCaps`/`UpscalerDeviceCaps` 字段零改动、`ZR_ApiVersion` 未递增（留 B-FSR-02）

### 7. 遗留风险

1. **CI 实跑未验证**：无硬件回退行为经代码审查确认与修复前等价、本机全绿，但本机无法实测 GitHub Actions L1——待 CI 轮/验收确认（若 CI 存在暴露非软件适配器的异常虚拟化环境，行为差异仅在首次探测建备失败后回退，结果仍保守）
2. **阳性对照缺位**（MISSING 项）：修复将假阴性转阳的效果只有对端机文档证据，B-PROBE-01 两机探针补验前，严格说"修复在支持硬件上产生 true"属**文档佐证而非本机实测**
3. **探测期建备的开销**：有硬件机器上首次 `ZR_Upscaler_QueryCaps` 会创建/释放一个 D3D12 设备（毫秒级、进程内仅一次，缓存命中零开销）；驱动异常导致建备失败时静默回退 null 口径（保守不误报）
4. **FFX 枚举行为非 ABI 契约**：null/真设备枚举差异是 SDK v2.3.0 运行时的实测行为，SDK 升级可能改变 provider 注册策略——B-FSR-02 的 probeMode 字段将把探测口径显式化到 ABI

## 验收清单（主控写，验收代理逐条跑并出证据）

- **V1**：`upscaler_caps.cpp` 用已枚举适配器创建真实 `ID3D12Device` 传给 `ZupFsr4Probe`，用完释放；设备创建失败回退 nullptr 路径且行为与修复前一致
- **V2**：`Native/tests/zrhi_d3d12_window.cpp`（原 :9907）同型调用已传真设备
- **V3**：两处失实注释改为事实陈述（`upscaler_caps.cpp` 文件头 + `fsr_upscaler.cpp` 同型表述），写明实测差异与来源
- **V4**：`zplugin.cpp` 的 `ZR_Upscaler_QueryCaps` 缓存语义保持（缓存命中不重复建设备/探测）
- **V5**：ABI 未动——`ZrUpscalerCaps` 字段不变、`ZR_ApiVersion` 未递增（那是 B-FSR-02）
- **V6**：`Tools/build_native.ps1` 全绿（/WX 零警告），构建前代理变量已按交接档案 §3.2 清理
- **V7**：`zrender_test.exe --success=false` 全绿，用例数 ≥349（只增不减）；新增 caps 稳定性 doctest 在无硬件环境可过
- **V8**：本机负对照证据入报告：修复后 null 与真设备两条路径均 2 条 / fsr4Available=0（数字+时间戳）；按主控裁决此为**硬件真相（MISSING 项不算 FAIL）**
- **V9**：临时验证程序已删除，无死代码残留
- **V10**：`zrender.dll` 已按 ADR-004 部署至 UnityProject（git status 可见其变更）
- **V11**：施工报告在四节结构中完整（改动文件/测试数字/对照证据/遗留风险）
- **V12**：变异抽查：从核心 diff 随机抽 3 行读回，确认无意外改动混入

验收输出 PASS/FAIL + 逐条证据；MISSING 项（硬件阳性）按裁决单独标注，不计 FAIL。

## 验收结论（验收子代理独立取证，2026-11-14 01:25）

**总结论 = PASS（候选可 commit）**。V1–V12：11 PASS + 1 MISSING（V8 硬件阳性对照缺位，按主控裁决不计 FAIL）+ 0 FAIL。施工报告自述与独立取证全部吻合。

关键取证（验收代理亲自执行，非转抄施工报告）：
- **V6/V7**：亲跑 `build_native.ps1` 两次（exit=0，/W4 /WX 编译零警告）；亲跑 `zrender_test.exe --success=false` = **350 用例 / 778,737 断言 / 0 failed**
- **V1**：建备/传参/释放逐行核对——`hardwareAdapter` 建设备成功/失败两分支均释放、`probeDevice` 紧跟释放、唯一早退在任何分配前；回退路径与修复前**逐字等价**
- **V3**：grep「保守列表/与真实设备一致/conservative」在 5 个涉改文件零命中；新注释为事实陈述并引用对端实测（RX 9060 XT 2026-11-13 null=2 vs 真=4）
- **V4/V5**：`Native/zplugin/` 目录 git diff 为空——缓存语义未动、ABI 未动
- **V8**：负对照核实——`fsr4_window_run.log`（01:17:04）caps fsr4Available=0、null=2 条、真设备=2 条（3.1.5/2.3.4），两路一致=硬件真相且无误报
- **V10**：dll 部署两侧 sha256 一致（验收重跑构建后）
- **V12**：变异抽查 4 处核心段无意外改动；断言口径自洽（349+1=350、778,723+14=778,737，佐证数字非抄写）

移交与遗留：
1. **移交 B-FSR-02**：`Native/zupscaler/fg_ffx.cpp:126-127`、`fg_ffx.h:162` 存在同型失实注释（FRAMEGENERATION provider 枚举路径，仓库起点遗留）——B-FSR-02 全仓审计必须纳入
2. CI 实跑待云端 L1 轮确认（本机不可代验；回退路径已证代码等价，风险低）
3. FFX null/真设备枚举差异是 SDK v2.3.0 实测行为非 ABI 契约——SDK 升级可能改变，B-FSR-02 的 probeMode 将显式化
