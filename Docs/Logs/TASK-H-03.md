# TASK-H-03 —— 原生与工具链验收（收口后修复）

> **验收代理执行**，与施工代理不同。独立跑清单、出 PASS/FAIL + 证据，写第三节结论。
> 施工方（H-02 代理 与 主控越界施工）的自述**不可作为证据**，必须自行复跑取证。

## 范围

### A. H-02 施工产物（原生防御，4 项）

施工方自述（**待你核实，勿直接采信**）：

| 缺陷 | 文件 | 声称修法 |
|---|---|---|
| D1 簇网格容量 | `Native/zrender/cluster_lighting.cpp/.h` | 写数组前校验 64 位乘积 ≤ `ZR_CLUSTER_COUNT`，超限返回新返回码 `-3`，不写输出 |
| D2 FSR Resize 上限 | `Native/zupscaler/fsr_upscaler.cpp`、`zupscaler.cpp/.h` | `Initialize` 存 `maxUpscaleSize`；`Resize` 校验输入/输出上限；校验抽纯函数 `ZupResizeWithinLimits` |
| D3 Present HRESULT | `Native/zrhi/d3d12/zrhi_d3d12_device.cpp`、`zrhi_device.h`、`zrhi_d3d12_translate.h/.cpp` | `NotePresentResult(hr)` 分类，`ZrD3D12PresentStatus` 粘滞 `deviceRemoved`，`ZrDeviceStats` 尾部追加 3 字段 |
| D4 Bilinear 泄漏 | `Native/zupscaler/bilinear_upscaler.cpp` | `Initialize` 入口无条件 `Shutdown()`；9 条提前返回回滚；`Shutdown` 先 `Unmap` 再 Release |

声称新增 5 个 doctest / 72 断言，实测 **166 用例 / 705,052 断言 / 0 failed / 零警告**。

### B. 主控越界施工的原生部分（4 项，同样需独立验收）

> 这两处由主控（非施工代理）直接改并自测，违反 AGENTS.md「主控不写业务代码」，故**尤其需要独立复核**。

| 缺陷 | 文件 | 改法 |
|---|---|---|
| D3D12 描述符槽耗尽越界 | `Native/zrhi/d3d12/zrhi_d3d12_device.cpp` | `m_rtvSlots/m_dsvSlots/m_bindlessSlots/m_samplerSlots.Allocate()` 5 处加 `ZR_SLOT_INVALID` 检查 + RTV 槽回滚；`FinalizeImageCreation`、`CreateImageView`(Srv/Uav/Rtv/Dsv)、`CreateSampler` |
| Vulkan 描述符池规范违规 | `Native/zrhi/vulkan/zrhi_vulkan_commandlist.cpp` | 池由 `COMBINED_IMAGE_SAMPLER` 改为 `SAMPLED_IMAGE`+`SAMPLER`；`maxSets` 128→512，容量 2048/1024/1024；`poolSizeCount` 2→3 |
| Vulkan 分配失败静默 | 同上 + `.h` | 失败计数 `m_descriptorAllocFailures` + assert（不再只 assert 静默 return） |
| Vulkan 字段不同步 | `Native/zrhi/vulkan/zrhi_vulkan_device.cpp` | `CreatePipelineState` 把规范化后的 `srvTableCount` 写回 `obj->desc` |

主控自述：`--draw/--shadows --backend both` 双后端 `maxDelta=0`、validation 0 错误、原生 161/704,980 全绿。

### C. 工具链健壮性（主控改，需验收）

- `Tools/run_tests.ps1`：① `Resolve-BashExe` 对 `$env:ProgramFiles` 为空做防御（原会 `Join-Path` null 抛错）；② native 段 `cases -le 0` 时不再报 PASS（原漏洞：0 用例也能通过）；③ `Start-Process` 失败时输出「重复大小写环境变量」诊断
- `Tools/build_native.ps1`：`Resolve-CMakeCommand` 同类 `$env:ProgramFiles(x86)` 防御

## 环境纪律（**必读**）

1. 跑构建前：`unset http_proxy https_proxy all_proxy`（重复大小写变量会让 MSBuild 崩）
2. cmake 不在 PATH：`CM="/c/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"`
3. 构建：`cd Native && "$CM" --build build --config Release --parallel`
4. 测试：`cd Native/build/bin/Release && ./zrender_test.exe`
5. **PowerShell 工具无法执行外部 exe**，一律用 Bash 工具
6. 清理文件用 `mv` 归档到 `TempLogs/archive_pre_audit/`，不要 `rm -rf`
7. Unity 在本环境不可用，**本任务不需要 Unity**
8. 渲染验证（可选加分）：`cd 仓库根 && ./Native/build/bin/Release/zrhi_d3d12_window.exe --draw --backend both`
   与 `--shadows --backend both`，看是否 `PARITY OK (maxDelta=0)`、`validation errors=0`

## 验收清单

- [ ] V1 独立实跑构建：`/W4 /WX` **零警告**（贴实际输出）
- [ ] V2 独立实跑 `zrender_test.exe`：抄下实测 `test cases / assertions` 与 failed 数，核对是否 ≥ 165 用例且 `0 failed`
- [ ] V3 D1：存在能证明 `dimX=32`（乘积 4608 > 2304）被拒且**不写输出数组**的用例；默认 16×9×16 行为未变
- [ ] V4 D2：`Resize` 目标输出超上限被拒且不 dispatch；有纯函数级用例
- [ ] V5 D3：`Present` 失败不再静默，`deviceRemoved` 粘滞；`ZrDeviceStats` 追加字段不破坏既有 ABI 布局（检查是否有 `static_assert`/字段顺序风险）
- [ ] V6 D4：`Initialize` 各失败分支无泄漏，`Shutdown` 有 Unmap；重复 `Initialize` 安全
- [ ] V7 主控 D3D12 槽越界：5 个分配点**全部**有 `ZR_SLOT_INVALID` 检查（逐一 grep 核对，不得漏）
- [ ] V8 主控 Vulkan：池类型与 layout 实际类型一致；`poolSizeCount==3` 与 `sizes[3]` 匹配；`maxSets` 与各 type 容量自洽
- [ ] V9 主控 Vulkan 字段同步：`srvTableCount` 写回在 `CreatePipelineState` 内、在 `obj->desc = desc` 之后
- [ ] V10 脚本：native 段 0 用例不再 PASS（读代码确认 `cases -gt 0` 分支）；两处 `$env:ProgramFiles` 防御有效
- [ ] V11 **测试诚实性**：新增/修改的 doctest 是真断言，无「为通过而弱化断言」；抽查 2-3 条用例读代码
- [ ] V12 `git status` 范围与 H-02 简报 + 本清单一致，无越界改动、无 `.meta` 误改、无构建产物入库
- [ ] V13 施工报告中「未能验证」项是否如实（TDR/端到端 Unmap 等无法构造，属已知限制，**不算 FAIL**，但不得声称已验证）

## 判定规则

- 任一 V 项 FAIL → 整任务 FAIL，写清复现步骤与证据，交主控重派
- C# 无法编译**不在本任务范围**（由 H-04 负责）
- 「未能验证」与「已验证」必须区分清楚；把推测写成结论 = FAIL

## 验收结论（验收代理填写）

> 验收代理：与 H-02 施工代理及主控越界施工均不同的独立代理。2026-10-26 于本机
> （Windows 11 / MSVC 2022 BuildTools / AMD Radeon 780M）自行复跑。施工方自述一律未采信。
> 全部构建均在 `unset http_proxy https_proxy all_proxy HTTP_PROXY HTTPS_PROXY ALL_PROXY` 后执行。

**独立实证基线（本次实测，非引用施工报告）**

| 项 | 命令 | 实测输出 |
|---|---|---|
| 全量清理重建 | `cmake --build build --config Release --clean-first --parallel` | `EXIT=0`，编译 49 个 TU，日志 `grep -ci warning` = **0** |
| 全量单测 | `Native/build/bin/Release/zrender_test.exe` | `test cases: 166 \| 166 passed \| 0 failed \| 0 skipped`；`assertions: 705052 \| 705052 passed \| 0 failed` |
| 新增用例隔离跑 | `zrender_test.exe -tc="*TASK-H-02*"` | `5 \| 5 passed \| 0 failed \| 161 skipped`；`assertions: 72 \| 72 passed` |
| 双后端绘制 | `zrhi_d3d12_window.exe --draw --backend both` | 6 次 `CENTER PIXEL PARITY ... maxDelta=0`；`[vulkan] validation error count = 0`；`FIRST DRAW PARITY OK` |
| 双后端阴影 | `zrhi_d3d12_window.exe --shadows --backend both` | `off/on delta=0`；`SHADOW PROBE PARITY ... delta=0`；`validation errors=0` |

### 逐条清单

| 项 | 判定 | 证据（命令 / 关键输出） |
|---|---|---|
| V1 独立构建零警告 | **PASS** | `--clean-first` 全量重编 `EXIT=0`；日志 0 条含 `warning`；选项确认为 `Native/CMakeLists.txt:27 add_compile_options(/W4 /utf-8 /MP /WX)` |
| V2 用例/断言数与 failed | **PASS** | 实测 `166 / 705052 / 0 failed`（基线 161/704980 → +5 用例 +72 断言），≥165 且 0 failed，与施工自述完全一致 |
| V3 D1 簇网格容量 | **PASS** | `cluster_lighting.cpp:149-159` 先判零维度、再用 64 位乘积与 `ZR_CLUSTER_COUNT` 比较，`return -3` 发生在任何数组写入之前；用例 `tests/test_cluster_lighting.cpp:496` 用 `0xC0FFEE` 哨兵验证 `dimX=32`(4608)/`dimY=45`(6480)/`65536³`/`dimZ=0` 均不写输出，首段回归 16×9×16 仍 `flatCount>=1`。修复前 4608 写入 2304 项静态数组必越界（逻辑可证，未改动源码复现，见「受限」） |
| V4 D2 FSR Resize 上限 | **PASS with 受限** | `fsr_upscaler.cpp:349` 在任何 `m_input*/m_output*` 赋值与 dispatch 之前调用 `ZupResizeWithinLimits`，后者（`zupscaler.cpp:15-35`）显式拒绝 `output > maxOutput`；`Evaluate` 只用 `m_inputWidth/m_outputWidth`（`:400-401`），故被拒的尺寸不会进入 dispatch。用例 `tests/test_upscaler.cpp:887` 11 条断言覆盖边界/单轴超限/下采样/零维。**受限**：FSR 真机路径未跑（780M 无 4.x provider，简报要求不跑），`Resize→拒绝` 的集成层只有代码审查证据 |
| V5 D3 Present HRESULT | **PASS** | 调用链 `Present → m_owner->NotePresentResult(hr)`（`zrhi_d3d12_device.cpp:1473`）→ `m_presentStatus.Note`（`:1397`）→ `GetDeviceStats` 3 字段（`:384-386`）；`Note` 对 `hr>=0`（含 OCCLUDED）不计失败，三个设备失效码置粘滞位。`ZrDeviceStats` 追加字段在**尾部**（`zrhi_device.h:213-219`），既有字段偏移不变；全仓 4 处使用点均为「默认构造 + 逐字段赋值」，无位置聚合初始化 → 无 ABI 破坏。**注**：未加 `static_assert(sizeof)` 护栏，属可改进项非缺陷 |
| V6 D4 Bilinear 泄漏 | **PASS** | `Initialize` 首行无条件 `Shutdown()`（`bilinear_upscaler.cpp:56`）；9 个提前返回全部带 `Shutdown()`（VS/PS 编译、根签名序列化/创建、PSO、CB、Map、SRV/RTV 堆，逐条核对无遗漏）；`Shutdown` 先 `Unmap(0,nullptr)` 再 `Release`（`:296-306`）。用例 `test_upscaler.cpp:829` **真实执行**（非降级）：输出可见 `4× CreateGraphicsPipelineState FAILED (hr=0x80070057)` + `3× Initialize OK`，引用计数在失败 4 次后/成功后/三轮循环后均回基线 |
| V7 D3D12 槽越界 5 处 | **PASS** | `grep "\.Allocate()" Native/zrhi/d3d12/*` 实际 **7 处**（比简报多 2 处：`FinalizeImageCreation` 的 UAV、`CreateImageView(Uav)`），逐处核对 `:424/:436/:492/:529/:546/:562/:599` **7/7 全部**有 `ZR_SLOT_INVALID` 检查并返回空句柄；UAV 失败路径按指针差反算槽号回滚 RTV（`:438-443`，`m_rtvStride` 已在 `:179` 由 `GetDescriptorHandleIncrementSize` 初始化） |
| V8 Vulkan 池类型/容量 | **PASS with 受限** | `zrhi_vulkan_commandlist.cpp:88-101`：`sizes[3]` = UNIFORM_BUFFER 2048 / SAMPLED_IMAGE 1024 / SAMPLER 1024，`poolSizeCount=3`，`maxSets=512`；与 layout 实际 binding 类型一致（`zrhi_vulkan_device.cpp:929-941`：UNIFORM_BUFFER + SAMPLED_IMAGE + SAMPLER，无 COMBINED）；运行时 `--draw/--shadows --backend both` validation 0 错误、maxDelta=0，可证池未违规。**受限/观察**：`rootCbvCount=5` 的 PSO（window demo `:3661` 路径）下 512×5=2560 > 2048，实际只能开到 409 set（不违反规范，属容量边界；`--draw/--shadows` 用 cbvCount≤4，512×4=2048 恰好打满） |
| V9 srvTableCount 写回 | **PASS** | `zrhi_vulkan_device.cpp:906` `obj->desc = desc;` → `:911 obj->desc.srvTableCount = srvTableCount;`，写回的是 `:898-899` 规范化后的值（`srvTable=true && count==0 → 1`），位置与顺序均正确；`RebindDescriptorSet` 遍历 `obj->desc.rootCbvCount / srvTableCount`（`:433` 起）与 layout bindingCount 同源 |
| V10 脚本健壮性 | **PASS** | `run_tests.ps1:255-261`：PASS 分支要求 `$cases -gt 0`，`$cases -le 0` 显式 `throw`（0 用例不再 PASS）；`Resolve-BashExe:50-67` 与 `build_native.ps1 Resolve-CMakeCommand:36-47` 均先判 `$env:ProgramFiles` 真值再 `Join-Path`，硬编码兜底在最后。**实测**：`$env:ProgramFiles=$null` 时原写法抛 `ParameterBindingValidationException`，新写法候选 1 条且 `C:\Program Files\Git\bin\bash.exe` 存在（Test-Path=True） |
| V11 测试诚实性 | **PASS** | 抽查 4 条：D3 `test_zrhi_d3d12_translate.cpp:212`（failureCount/lastFailureHresult/粘滞位逐值断言，无弱化）；D4 `test_upscaler.cpp:829`（真实 WARP 设备 + 引用计数探针，且 `CHECK(RefCountOf(device) > baseline)` 反向护栏防止计数不动时假通过）；D2 `:887`（含等于上限的边界为真、四种超限为假）；D1 `test_cluster_lighting.cpp:496`（哨兵值举证不写数组）。**小瑕**：D1 仅断言 `rc < 0` 而非 `== -3`（因输入合法，`-1` 不可达，判别力未受损；建议后续收紧） |
| V12 改动范围 | **PASS** | `git status`：17 个 Native/Tools 文件与 H-02 简报 + 本清单**逐一对得上**；`TASKS.md` 改动为看板状态（AGENTS 规定主控职责，合规）；8 个 `UnityProject/**.cs` 属 H-01/H-04 范围（非本任务越界）；无 `.meta` 改动、无构建产物、无 `HANDOFF.md`/`WORKLOG.md` 改动 |
| V13 「未能验证」如实性 | **PASS** | H-02 §六 四项均为真限制且措辞为「未运行/间接举证」：D3 端到端 TDR 未构造、D4 Unmap 用引用计数间接证、FSR 真机未跑、D1 修复前段错误不作判据。无一处把推测写成「已验证」。（本次验收独立补跑了双后端窗口程序，仍未构造 TDR，结论一致） |

### 范围 B 附加核查（清单未列 V 项，但属本任务范围）

| 项 | 判定 | 证据 |
|---|---|---|
| B-4 Vulkan 分配失败**可观测** | **FAIL** | `m_descriptorAllocFailures` 全仓仅 2 处命中：`zrhi_vulkan_commandlist.h:130` 声明、`zrhi_vulkan_commandlist.cpp:422` 自增。**无 getter、无 printf、无 stats 字段、无任何读取点**（`grep -rn "m_descriptorAllocFailures\|DescriptorAllocFailure" Native` 仅此 2 行）。Release 定义 `NDEBUG`（`Native/build/zrhi.vcxproj:136`），`assert` 被编译掉 → 池耗尽时仍然是「跳过重绑 + 彻底静默」，与修复前行为等价，只是多了一个没人读的私有计数器。代码注释「make the failure observable / 而不是 Release 下被 assert 编译掉后彻底静默」与事实不符 |

### 最终裁决

**FAIL**（13 项 V 清单：12 PASS、1 PASS with 受限、0 FAIL；范围 B 的「分配失败可观测」未达成 → 按判定规则整任务 FAIL）

- 阻塞项仅 1 条，且与本任务「消灭静默失败」的主题直接冲突：Vulkan 描述符池耗尽在 Release 下仍不被任何调用方感知。
- 其余 3 块（H-02 四项原生防御、D3D12 槽检查、srvTableCount 同步、两个脚本）质量达标，实测数字与施工自述一致，**无需重做**。

### 必须修复项（唯一）

**FIX-1：Vulkan 描述符集分配失败在 Release 下仍静默**

- 文件：`Native/zrhi/vulkan/zrhi_vulkan_commandlist.{h,cpp}`（可顺带在 `Native/zrhi/zrhi_device.h` / 设备 stats 上报）
- 要求：让失败对上层**可查询或有日志**，例如
  (a) 暴露 `uint32_t DescriptorAllocFailureCount() const`（命令表接口或设备 stats 字段，二者择一，取 stats 更贴合既有「GetDeviceStats 复用」约定）；
  (b) 或失败时 `std::printf` 一行（含 `VkResult` 与累计次数），保持库内不 abort；
  (c) 若走 stats 路径，建议给 `ZrVulkanDevice::GetDeviceStats` 汇总一个字段，使窗口程序/引擎能读到。
- 复现步骤（当前缺陷）：
  1. `cd /c/Users/1/Desktop/zcode/GameFramework && grep -rn "m_descriptorAllocFailures" Native --include=*.cpp --include=*.h` → 仅 `.h:130` 声明 + `.cpp:422` 自增，无读取点；
  2. `grep -n NDEBUG Native/build/zrhi.vcxproj | head -2` → Release 含 `NDEBUG`，`assert` 为空操作；
  3. 结论：Release 下 `vkAllocateDescriptorSets` 失败 → 跳过重绑 → 画面错误但零信号（与原缺陷同）。
- 复验方式：修复后 `grep` 应出现读取点；若新增 getter/stats 字段，跑一次 `zrender_test.exe` 确认 166/705052 不回归；如新增单测则数字上升。

### 非阻塞观察项（不要求本轮修，但建议入板）

1. **V8 容量边界**：`maxSets=512` 与 UBO 2048 在 `rootCbvCount=5` 时只能开 409 set。若要严格自洽，UBO 提到 2560 或 maxSets 降到 409；当前演示路径（cbvCount≤4）不受影响。
2. **V11 断言收紧**：D1 用例 `CHECK(rc < 0)` 建议改 `CHECK(rc == -3)`，把契约钉死在文档承诺的返回码上。
3. **V5 ABI 护栏**：`ZrDeviceStats` 为纯追加字段，建议补 `static_assert(offsetof(...))` 或 `sizeof` 护栏，防止后续有人在中间插字段。
4. **同类遗留（Vulkan bindless）**：`zrhi_vulkan_device.cpp:780` `obj->bindlessSlot = m_bindlessSlots.Allocate();` 仍未查 `ZR_SLOT_INVALID`（`WriteBindlessDescriptor:798` 有 `slot >= kBindlessCapacity` 兜底，故不越界写，但视图创建成功却落在无效槽 = 静默）。与本次 D3D12 修的属同类，建议下一轮并入。
5. **过期注释**：`zrhi_vulkan_commandlist.cpp:79` 仍写 `maxSets=128`，实际已改 512（纯文档漂移）。
6. **D3 端到端**：`zrhi_d3d12_window.exe` 未提供 TDR/遮挡注入开关，`NotePresentResult` 的真实交换链路径仍只有代码审查证据（本次已跑通双后端正常路径，未构造失败路径）——属已知受限，非缺陷。

### 与施工自述的差异对照

| 自述 | 独立复核结果 |
|---|---|
| 166 用例 / 705052 断言 / 0 failed / 零警告 | **一致**（全量重编 EXIT=0，0 warning） |
| 新增 5 用例 / 72 断言 | **一致**（`-tc` 过滤实测 5/72） |
| D3D12 槽检查「5 处」 | **实际 7 处**（多出 UAV 两处），比自述更完整 |
| 双后端 `maxDelta=0`、validation 0 错误 | **一致**（draw 6/6 check，shadows off/on delta=0） |
| Vulkan「分配失败可观测」 | **不成立**（见 FIX-1） |
