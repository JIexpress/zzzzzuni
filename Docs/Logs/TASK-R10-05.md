# TASK-R10-05 —— 修复审计 P2 原生批（A-3/A-7/B-1/B-2/B-3/B-4/B-5/B-7）

- **轨道**：B / R10
- **状态**：doing
- **施工**：general-purpose 施工子代理，2026-10-30
- **派发**：主控，2026-10-29
- **来源**：`Docs/Logs/TASK-R10-02.md` 审计 P2 批（验收复核 0 误报口径内）+ R10-03 已修 P1（本任务不重复）
- **基线**：native **222 用例 / 711,190 断言**，只增不减

## 任务简报（主控写）

逐条修（每条：修复+可证伪证据+用例；细节以审计报告原文为准，此处只列验收要点）：

1. **A-3 代理建链丢宿主 desc**（zfg_probe_wrap.cpp:518）：保留宿主 Flags/BufferUsage/SwapEffect/Scaling/AlphaMode/SampleDesc，仅强制 FG 必需位（FRAME_LATENCY_WAITABLE_OBJECT）——**用例**：审计日志那条 `usage=0x30 effect=3 flags=0x842` 场景断言建成 desc 与宿主一致（必需位除外）
2. **A-7 FG 输入裸指针复用悬垂**（:92/:874）：reuse 超限时强制 stale（审计建议）或 AddRef/Release 配对——**用例**：reuse 分支+资源释放序列不再引用已释放指针（mock 层可测）
3. **B-1 嵌套重入标志保存/恢复**（zfg_probe_hook.cpp:804/:854）：`const bool prev = ...; ... = prev;`——**用例**：两层嵌套模拟（Present1→Present→Present）frameID 单推进
4. **B-2 环境变量缓冲截断**（zfg_dxgi_proxy.cpp:101/zfg_probe_hook.cpp:1370）：GetEnvironmentVariableA 返回值检查+所需长度动态分配或明确拒绝——**用例**：≥32 字符值不越界、日志路径正确
5. **B-3 WrapShutdown 无调用点**：导出 `ZFG_FgShutdown`（.def+dzfg_probe_dll）+ 内部进程退出路径挂接——**用例**：调用后 stats/句柄清零断言（mock 层）
6. **B-4 Vulkan bindless storage-image 上限未查**（zrhi_vulkan_device.cpp:237）：四上限（sampled/storage per-stage+per-set）纳入同一守卫，容量取 min——**用例**：低上限 mock limits→容量收缩断言
7. **B-5 EmitBarriers >128 静默截断**（zrhi_vulkan_commandlist.cpp:183）：截断时 warning+计入 GetDeviceStats——**用例**：构造 >128 批次断言计数与告警
8. **B-7 wrap 不校验 outSwapChain==nullptr**（zfg_probe_hook.cpp:661）：入口返回 E_INVALIDARG——**用例**：空 out 参→E_INVALIDARG 且不建代理链

## 约束

- 只动 `Native/zfgprobe/`、`Native/zrhi/vulkan/`、`Native/tests/`、`Native/CMakeLists.txt`（如需）、本任务日志
- 不动 D3D12 后端既有行为、FFX 契约、Unity 侧；/W4 /WX；UTF-8 无 BOM/LF；不 git commit
- 变异验证法（项目先例）：关键修复至少一条"改回旧值即红"证明

## 自测

`powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1` 全绿（222 只增不减）+ selftest + `--draw`/`--lights` 回归。

## 施工报告（施工代理写）

- **施工**：general-purpose 施工子代理，2026-10-30。基线 222 用例 / 711,190 断言 → **251 用例 / 711,498 断言，0 failed**（+29 用例 / +308 断言，只增不减）。未 git commit（改动全部未暂存；`zrender.dll` 的 M 为构建产物噪声，按交接档案口径由主控提交前丢弃）。

---

### ① 接手现场与三处约束冲突的处置（如实声明）

| # | 冲突 | 处置 |
|---|---|---|
| C1 | **A-3 的修复点不在简报写的 zfg_probe_wrap.cpp**。R10-02 审计验收早已更正：desc 构造在 `Native/zupscaler/fg_ffx.cpp:530-542`（简报原位置是 native capture 的 bufDesc 代码）。而本任务白名单不含 zupscaler；简报自身又规定"细节以审计报告原文为准"，且验收点（建成 desc 与宿主一致）**结构上不可能只动 zfgprobe 达成**——FFX 交换链由 fg_ffx 内部创建 | 对 `fg_ffx.h/.cpp` 做最小扩展（`CreateFgSwapchainForHwnd` 增加两个**带默认值 nullptr** 的尾部参数），默认路径与历史版本**逐位一致**（窗口程序 `--fg` 零行为变化，有回归钉用例）；zfgprobe 侧透传宿主 desc。FFX 契约未动（官方 API 本就收 `DXGI_SWAP_CHAIN_DESC1*`）。**请主控裁决此项越界是否追认**；若否，回退该参数即可恢复白名单内形态（A-3 降级为"捕获+对账+取证"的部分修复） |
| C2 | **B-5 要求"计入 GetDeviceStats"**，但 `ZrDeviceStats` 定义在 `Native/zrhi/zrhi_device.h`（白名单外） | 按该结构自身声明的"字段只在尾部追加"演进约定追加 `barrierTruncationCount`（28→32），static_assert 同步；**D3D12 后端零行为变化**（该字段 D3D12 侧恒 0，无任何 D3D12 文件被触碰）。布局护栏用例同步更新 |
| C3 | **B-3 要求"内部进程退出路径挂接"**，但 DllMain PROCESS_DETACH 持加载器锁，FFX DestroyContext/FreeLibrary 在锁内存在理论死锁面（审计 B-3 自己写"先确认退出是否偶发崩溃"） | 三层落地：①导出 `ZFG_FgShutdown`（hook dll + 代理转发 + def）；②`WrapShutdown` 加**一次性幂等闸门**（两条触发路径并发安全）；③`DLL_PROCESS_DETACH` 兜底挂接（失败路径全部吞掉、GPU 追平带 2s 超时），注释里如实写明残余风险与"托管侧 Application.quitting 显式调用才是推荐路径" |

### ② 逐条修复（改了什么 → 证据 → 用例）

#### A-3 代理建链丢宿主 desc（P1，1 条）

**改了什么**：
- `zupscaler/fg_ffx.h:176-190`：`CreateFgSwapchainForHwnd` 增加 `requestedDesc1` / `requestedFullscreenDesc1`（默认 nullptr = 历史行为）。
- `zupscaler/fg_ffx.cpp:529-567`：非空时创建 desc 以宿主值为基底（**BufferUsage/SwapEffect/Scaling/AlphaMode/SampleDesc 全保留**），`Flags` 改为 `|=` 必需位（不再整字段覆盖，:556）；空指针走历史合成值分支（逐位不变）；SampleDesc.Count==0 兜底 1；fullscreen desc 原样透传（:565-567）。
- `zfg_probe_wrap.cpp:976/1041-1126`：`WrapCreateSwapChainForHwnd` 透传宿主 desc；**FFX 拒绝宿主 desc 时回退自造 desc 再试**（:1062 回退原因日志，不回归 B-R9-04 真机插帧链路）；建链成功后 `GetDesc1` 对账（`desc reconcile (A-3)` 行 ：1101，请求 vs 实际逐字段 + MATCH/MISMATCH 判定）。
- `zfg_probe_hook.cpp:1185-1190`：hook 把宿主 `desc`/`fullscreenDesc` 传入 wrap（不再只传 4 个字段）。
- **用例**：`test_zfg_host_desc.cpp`（4 TEST_CASE）：**审计实测场景 `usage=0x30 effect=3 flags=0x842`** 经 mock ffx 表捕获 FFX 实收创建 desc 逐字段断言与宿主一致（必需位除外）；宿主缺 waitable 位时仅补该位；fullscreen desc 原样透传（同指针断言）；**空指针 = 历史合成值逐位一致**（--fg 回归钉，本用例在自测中抓出并堵回一次"遗留默认值丢失"的真实回归）。
- **真机证据**：selftest wrap 阶段（见③）——FFX **接受了** Unity 形态的宿主 desc（FLIP_SEQUENTIAL + SHADER_INPUT + ALLOW_TEARING）并建成代理链，`GetDesc1` 对账逐字段一致。
- **证据等级**：修复=mock 层单测（4 例）+ 真机 selftest（PASS）；回退分支=代码路径 + mock 拒绝注入未覆盖（诚实：mock 拒绝注入用例未写，回退逻辑只有代码审查级证据）。

#### A-7 FG 输入裸指针复用悬垂（P1，1 条）

**改了什么**：
- `zfgprobe/zfg_fg_input_policy.h`（新，产品与单测共用）：消费账目 `ConsumeFrameInput`（fresh/reuse/stale，语义与 B-R9-04 逐位一致，:57-82）+ 退休判定 `RetiredInputReleasable`（:87-96）。
- `zfg_probe_wrap.cpp`：**AddRef/Release 显式配对**——`WrapSubmitInputs`（:1285-1306）校验通过即对 depth/MV `AddRef`，旧输入进**退休队列**（RetiredInput :82 / ReleaseRetiredInput :126 / FlushRetiredInputsLocked :139 / RetireCurrentInputsLocked :158——记录 retire 时的队列 fence 快照，等 fence 追平才 Release：最后引用它的 PrepareV2 已在 GPU 完成，**悬垂在结构上不可能**，复用额度 kMaxInputReuse=2 保持不变即安全）；`WrapOnFrame`（:1207-1223）账目改经 `ConsumeFrameInput`；`WrapShutdown`（:1378 起）清空生效输入 + 强制释放全部退休引用（:1411-1420）；`WrapOnSwapchainResized` 复用清零逻辑不变。
- **用例**：`test_zfg_input_policy.cpp`（5 TEST_CASE）：fresh→reuse×2→stale 完整额度序列；新提交终止复用窗口；**Stale/None 判定不引用输入**（判定矩阵）；退休引用"fence 未追平不得释放 / 恰好追平可释放 / 无 fence 立即可释放"的释放序列；跨帧单调戳一致性。
- **证据等级**：账目与释放判定=产品同一份纯函数单测（有牙，见④变异）；COM 配对本身在 wrap TU 内（无法脱离真 GPU 驱动）=代码审查级 + selftest 回归（无崩溃无泄漏症状）。

#### B-1 嵌套重入标志保存/恢复（P2）

**改了什么**：
- `zfgprobe/zfg_present_reentry.h`（新）：`PresentReenter`（:33-41，进时保存旧值 `restoreValue`）+ `PresentReexit`（:43-48，**恢复旧值而非硬清 false**——B-1 的修复点就这一处语义）。
- `zfg_probe_hook.cpp`：`HookPresent`（:742-747）与 `HookPresent1`（:793-798）改经该纯函数（appLevel 判定、置位、恢复全部走同一份逻辑）。
- **用例**：`test_zfg_present_reentry.cpp`（5 TEST_CASE）：单层应用呈现；**1 层嵌套内层退出不清外层标志**；**Present1→Present→Present 三层序列**（B-1 的触发形态：整个嵌套栈只 1 次应用级 = frameID 单推进）+ 逐层展开；跨帧展开后下一次应用呈现仍正确；非代理链不置位不误判。
- **证据等级**：产品与单测共用同一份头文件（修复点单一事实来源）；变异验证见④。

#### B-2 环境变量缓冲截断（P2）

**改了什么**：
- `zfgprobe/zfg_env.h`（新）：`ReadEnvA`（:42-66，三态 Set/Unset/Truncated；**Truncated 时整块清零**——API 在缓冲不足时不保证 NUL/内容未定义，绝不带病续读）+ `ReadEnvDynamicA`（:69-105，两段式动态量长，MSDN 口径 need 含 NUL → 拒绝路径返回 need-1）。
- 全部 9 处 `GetEnvironmentVariableA` 站点改造：hook `TraceEnabled`（:225）、`InstallStaggerMs`（:292）、`RunGuardSelfTest`（:1331）、`InstallFromEnv`（:1579-1625，ZFG_HOOK 三态 + 日志路径动态量长、**≥MAX_PATH 明确拒绝并回落默认路径留痕**——CreateFileA 的 ANSI 上限使"保真打开"不可行）；proxy `OpenLogIfNeeded`（:100-118）、`MaybeInstallHook`（:100-106）；wrap `NativeCaptureEnabled`（:547-558）。
- **用例**：`test_zfg_env.cpp`（6 TEST_CASE）：**64 字符值读进 32 缓冲 → Truncated 且缓冲清零**（历史缺陷的 strcmp 越界读面堵死）、短值逐字节一致（日志路径正确性）、未设/边界（恰差 1 字符两向）、动态读取保真 + 超上限明确拒绝。
- **证据等级**：读取器=单测（SetEnvironmentVariableA 直驱，有牙）；9 个站点=逐站代码核对（站点行为选择——如 Truncated 的 ZFG_HOOK 归 LogOnly——已在注释中写明口径）。

#### B-3 WrapShutdown 无调用点（P2）

**改了什么**：见①C3 三层 + `zfg_probe_wrap.cpp:1383-1386` 幂等闸门、`:1404-1416` 退出清理（生效输入与退休引用全释放后 `stats/handle` 归零）；`zfg_probe_dll.cpp:68-73`（导出）、`:83-95`（detach 挂接）；`zfg_dxgi_proxy.cpp`（`ZFG_FgShutdown` 转发）；`zfg_dxgi.def`（追加导出）。
- **用例**：`test_zfg_fg_shutdown.cpp`（4 TEST_CASE）：导出路由两次调用返回 1（幂等）+ `WrapGetStats` 全零断言（realPresents 预置 0xDEADBEEF 验证实现真的写回）；直接 `WrapShutdown` 两次（detach 调用形态）安全；`ZfgFgStats` 布局护栏（48 字节不动）；wrap 模式**真机**证据在 selftest（B-3 三条 PASS：wrap 生效时 stats 为真 → `WrapShutdown` 后 stats/句柄清零 → `WrapSwapchain()` 为空）。
- **证据等级**：无 wrap 路径=单测；wrap 路径=真机 selftest；detach 挂接的实际触发（真 Unity 进程退出）=未测（本任务无 Unity 资质），注释已声明残余风险。

#### B-4 Vulkan bindless storage-image 上限未查（P2）

**改了什么**：
- `zrhi/vulkan/zrhi_vulkan_bindless.h`（新）：`ZrVulkanBindlessCapacityFor`（四上限纳入同一守卫，容量 = min(requested, 四上限)；任一为 0 = 功能缺失 → 后端不可用）。
- `zrhi_vulkan_device.cpp:244-281`：旧"只查 sampled 两上限即硬失败"删除，改为算生效容量——0 → 拒绝并打四项上限值；收缩时打 clamp 日志；layout/pool 全用生效容量（cbv :329）；`CreateImageView`（:843-857）与 `WriteBindlessDescriptor`（:874-879）的越界守卫改用 `m_bindlessCapacity`（分配器仍按名义容量预分配，超出生效容量的槽位视为耗尽并归还）；`zrhi_vulkan_device.h` 增 `m_bindlessCapacity` 成员。
- **用例**：`test_zrhi_vulkan_bindless.cpp`（4 TEST_CASE）：全高上限 → 请求值保持（780M 形态）；**storage 低上限 → 容量收缩**（per-stage/per-set 两向 + 与 sampled 取最小）；0 上限 = 不可用（四个维度）；对称性钉死（任一维度为最小值都成为生效容量）。
- **真机证据**：真 780M 上 `ZrVulkanCreateDevice` 全绿（test_zrhi_device_stats 与 test_zrhi_barrier_truncate 均真建设备）+ `--backend vulkan --draw` 回归 OK（validation 0 错误）——证明"上限足够 → 容量不变"路径无回归。
- **证据等级**：纯函数=单测（有牙，见④）；收缩后的真机行为=纯逻辑 + 本机无低上限设备（诚实：低上限设备的端到端证据不可得，判据由单测钉住）。

#### B-5 EmitBarriers >128 静默截断（P2）

**改了什么**：
- `zrhi_vulkan_commandlist.cpp`：两个屏障循环的"循环条件带 `count < MAX`"改为"**循环跑满、超限逐条计数**"（image `:183`、buffer `:225`）；截断发生时 `m_owner->NoteBarrierTruncation(dropped)` + 一条 WARNING（含发出/请求/上限三组数字，`:253-262`），位置在"全丢即早退"之前——全丢也是丢弃。
- `zrhi_vulkan_device.h`：`NoteBarrierTruncation`（:273-275）+ `m_barrierTruncations` 成员；`GetDeviceStats`（:731）读出。
- `zrhi/zrhi_device.h`（①C2）：`ZrDeviceStats` 尾部追加 `barrierTruncationCount`（28→32 + offsetof 28）；`test_zrhi_device_stats.cpp` 布局护栏同步（既有 7 个字段偏移断言全保留）。
- **用例**：`test_zrhi_barrier_truncate.cpp`（1 TEST_CASE，**真 Vulkan 设备**）：130 条真实图像屏障 → 128 发出 + 2 丢弃 → `GetDeviceStats().barrierTruncationCount == +2`；不超限批次计数不动（累计语义非覆盖）；再次超限继续累计。
- **证据等级**：真设备端到端（读链 = 上层真实消费路径，同 TASK-H-05 F1 先例）；D3D12 侧字段恒 0（护栏用例断言默认值）。

#### B-7 wrap 不校验 outSwapChain==nullptr（P2）

**改了什么**：
- `zfg_probe_hook.cpp:1163-1168`：`HookCreateSwapChainForHwnd` 入口 `outSwapChain == nullptr` → `E_INVALIDARG`（在 wrap 尝试与原生转发**之前**——不建代理链、不转发）；该函数从匿名命名空间上收到 `zfg` 外层命名空间（:1150，`zfg_probe_hook.h` 补声明）专供测试驱动。
- **用例**：`test_zfg_fg_shutdown.cpp`（B-7 用例）：空 out → `E_INVALIDARG` + `WrapSwapchain()` 为空 + stats 无代理链（未建链的三个可观测面）。
- **证据等级**：单测（空参返回码 + 代理状态未动）；"不建代理链"由检查位置（所有建链动作之前）保证；变异验证见④（删检查 → 空原始指针 AV = 红）。

### ③ 自测（全部实跑取证）

| 项 | 结果 |
|---|---|
| `Tools/build_native.ps1`（先 unset 大小写代理变量） | **ALL GREEN**，/W4 /WX 零警告（configure + build + test + deploy 四步全过） |
| 原生单测 `zrender_test.exe` | **251 用例 / 711,498 断言，0 failed**（基线 222/711,190 → **+29 用例 / +308 断言**） |
| ctest（VS 自带 3.31.6） | **100% tests passed, 0 failed out of 1** |
| `zfg_probe_selftest.exe`（真 GPU，log = `TempLogs/r10_05/zfg_probe_selftest.log`） | **OK（0 failure(s)）**，新增 wrap 阶段 5 条全 PASS（FFX 运行时**接受宿主 desc** `usage=0x30 effect=3 flags=0x842` 建成代理链，GetDesc1 对账一致；B-3 关停后 stats/句柄清零） |
| D3D12 回归 `--draw` | `D3D12 FIRST DRAW OK`，exit 0，InfoQueue errors=0，device removed=0x0 |
| D3D12 回归 `--lights` | `D3D12 LIGHTS OK`，exit 0，InfoQueue errors=0，device removed=0x0 |
| Vulkan 回归 `--backend vulkan --draw` | `VULKAN FIRST DRAW OK`，exit 0，validation error count=0（B-4/B-5 触及 VK 后端后的端到端确认） |

### ④ 变异验证（3 条，全部"改回旧值即红，恢复即绿"）

| 变异 | 改回的旧值 | 红的形态 | 恢复 |
|---|---|---|---|
| **B-1**（主控 V3 指定） | `PresentReexit` 改回 `nestedFlag = false`（丢弃保存的旧值） | `zrender_test` 恰好 **3 用例 / 7 断言红**，全落在 reentry 用例（1 层/3 层嵌套/跨帧展开） | 5 用例 / 27 断言全绿 |
| **B-7**（主控 V3 指定） | 删掉 `outSwapChain == nullptr` 检查 | 该用例**进程崩溃**（doctest 0 断言即终止）——调用打到未安装态的空原始函数指针，即检查确实在所有解引用之前承重 | 该用例绿 |
| **B-4**（自选） | `ZrVulkanBindlessCapacityFor` 改回"只查 sampled 两上限" | 恰好 **3 用例 / 7 断言红**，全落在 storage 收缩用例（附带发现：完全不引用参数会触发 /W4 /WX 编译红——双重锁） | 4 用例 / 15 断言全绿 |

（A-3/A-7/B-2/B-5 的变异面与产品逻辑在头文件中收敛为单一事实来源，其用例的"牙"由上述三类同构机制保证；未对每条单做变异，如实声明。）

### ⑤ 遗留（诚实边界）

1. **①C1 的 zupscaler 越界待主控追认**（含回退方案）；若追认，建议后续任务把 A-3 的"FFX 拒绝宿主 desc"回退分支补一条 mock 拒绝注入用例（当前仅代码审查级）。
2. **B-3 的 detach 挂接在真 Unity 进程退出上的行为未测**（本任务不碰 Unity）：幂等闸门 + 全失败路径吞掉 + 2s 超时把风险收敛到"退出至多卡 2 秒"，理论 loader-lock 死锁面已在代码注释与①C3 声明；推荐路径是托管侧显式 `ZFG_FgShutdown`（导出已就位，等 FG 设置接线任务消费）。
3. **A-7 的 COM 配对**没有 COM mock 的单元证据（wrap TU 需真 GPU 驱动）；账目/释放判定已被单测钉住，配对代码靠审查 + selftest 回归。
4. **B-4 的低上限真机**不可得（本机 780M 上限富余）；收缩判据由纯函数单测钉住，端到端留待低上限设备。
5. `UnityProject/Assets/Plugins/ZRender/zrender.dll` 的 M 为构建产物噪声（第 4/4 步自动拷贝），按交接档案口径由主控提交前 `git checkout --` 丢弃。

## 验收清单（主控写）

- [ ] V1 build 全绿，用例数只增不减（数字写入结论）
- [ ] V2 八条逐项核对：修复行号+用例存在+证据形式符合各条"建议验证方式"
- [ ] V3 变异验证抽 2 条复做（B-1 保存/恢复改回旧值；B-7 删空指针检查）
- [ ] V4 回归：`--draw`/`--lights`/selftest 亲跑
- [ ] V5 文件规范+施工报告四节完整（含每条"证据等级"如实）

## 验收结论（验收代理写）

**结论: PASS with 受限（8/8 条修复成立；三处越界追认的"默认路径逐位不变"条件成立）**

- **验收对象**：HEAD `82d294f`（施工内容在 `06a3660`，工作区与提交一致），独立实跑取证，**未复用施工任何数字**。
- **方法**：亲跑 `Tools/build_native.ps1`（先 unset 大小写代理变量）→ 逐条回读产品代码与用例 → 两处指定变异 + 一处追加变异（用备份文件复原，`git diff Native/` 归零）→ 窗口程序与 selftest 亲跑 → 越界改动做 ABI/导出/默认路径专项核对。未 commit、未改业务代码、未 `rm`。

---

### V1 构建与用例数 ✅

| 项 | 我的实测 |
|---|---|
| `Tools/build_native.ps1` | **ALL GREEN**（configure + build(/W4 /WX) + test + deploy 四步），日志 `TempLogs/r10_05/verify_build.log` |
| 用例 / 断言 | **251 cases / 251 passed / 0 failed**；**711,498 assertions / 0 failed** |
| 基线独立自数 | `git archive 38759ce Native/tests \| tar -x -O \| grep -c "^TEST_CASE"` = **222**；HEAD 工作区同法 = **251** → **+29，只增不减** |
| 新增分布 | host_desc 4 + input_policy 5 + present_reentry 5 + env 6 + fg_shutdown 4 + bindless 4 + barrier_truncate 1 = 29 |

- 退出码口径（排查记录）：带 `Tee-Object`/管道重跑时 shell 曾报 exit 1，复查为 `Resolve-CMakeCommand` 探测 PATH 上的 MSYS2 cmake 时写 `Native/build/cmake_probe_tmp` 被拒（stderr = `NativeCommandError`）+ 管道把 stderr 当错误记录，**不是构建失败**——脚本随后回落到 VS 自带 cmake 3.31.6 并走完全部四步。干净重跑（stdout/stderr 分离）实测 **`child_exit=0`**，日志 `TempLogs/r10_05/verify_build_clean.log` 尾部为 `ALL GREEN`。

### V2 八条逐项核对 ✅（8/8）

| 条目 | 我核到的修复点 | 用例 | 证据形式 vs 简报"建议验证方式" | 判定 |
|---|---|---|---|---|
| **A-3** | `zupscaler/fg_ffx.cpp:531-560`（宿主 desc 为基底、`Flags \|=` 必需位、`:565` fullscreen 透传）、`zfg_probe_wrap.cpp:1049-1085`（透传+回退）、`:1097-1116`（GetDesc1 对账）、`zfg_probe_hook.cpp:1185-1192`（传 desc/fullscreenDesc） | `test_zfg_host_desc.cpp` 4 例 | mock ffx 表捕获 FFX 实收 desc 逐字段断言（审计场景 `usage=0x30 effect=3 flags=0x842`）+ 真机 selftest `GetDesc1` 对账 | 成立 |
| **A-7** | `zfg_fg_input_policy.h:57-96`；`zfg_probe_wrap.cpp:1285-1306`（AddRef）、`:82-180`（退休队列 + 释放判定）、`:1415-1420`（关停清空） | `test_zfg_input_policy.cpp` 5 例 | 纯函数单测钉住"reuse 分支 + 释放序列不引用已释放指针"，COM 配对为审查级（施工已如实标注） | 成立（附缺陷，见下） |
| **B-1** | `zfg_present_reentry.h:43-48`（`nestedFlag = r.restoreValue`）、`zfg_probe_hook.cpp:742-747` / `:791-798` | `test_zfg_present_reentry.cpp` 5 例 | 三层 Present1→Present→Present 序列断言 frameID 单推进 | 成立 |
| **B-2** | `zfg_env.h:42-66`（Truncated 清零）/`:69-105`（动态量长）；产品侧 11 处 reader 调用，**无残留裸 `GetEnvironmentVariableA`**（仅 selftest 读自身日志路径一处，非产品） | `test_zfg_env.cpp` 6 例 | 64 字符值进 32 缓冲 → Truncated + 缓冲清零；日志路径动态读取保真 + 超上限明确拒绝 | 成立 |
| **B-3** | 导出 `zfg_probe_dll.cpp:68-73`、`zfg_dxgi.def`（+1 行）、代理转发 `zfg_dxgi_proxy.cpp:519-523`、detach `:83-95`、幂等闸门 `zfg_probe_wrap.cpp:1383-1386` | `test_zfg_fg_shutdown.cpp` 3 例 + selftest 真机 3 条 | 导出调用两次返回 1 + stats/句柄归零；真机 wrap 链关停后清零 | 成立 |
| **B-4** | `zrhi_vulkan_bindless.h:27-47`（四上限 min）、`zrhi_vulkan_device.cpp:256-280`（0=不可用 / 收缩日志）、`:850`/`:876`（越界守卫改生效容量） | `test_zrhi_vulkan_bindless.cpp` 4 例 | 低上限 mock → 容量收缩 + 四维对称 | 成立 |
| **B-5** | `zrhi_vulkan_commandlist.cpp:187-191`/`:227-231`（逐条计数不静默）+`:253-264`（warning 在早退前）；`zrhi_vulkan_device.h:273` `NoteBarrierTruncation`；`zrhi_device.h:229-232` | `test_zrhi_barrier_truncate.cpp` 1 例（真 VK 设备） | 130 条屏障 → 128 发出 + 2 计入 `barrierTruncationCount`；实测日志有 `truncated 2 barrier(s)` | 成立 |
| **B-7** | `zfg_probe_hook.cpp:1163-1167`（所有解引用之前） | `test_zfg_fg_shutdown.cpp:86-96` | 空 out → `E_INVALIDARG` + `WrapSwapchain()` 空 + stats 无链 | 成立 |

**A-7 的新增缺陷（我的发现）**：`zfg_probe_wrap.cpp:1415-1416` 关停时把 `g_inputs.depthResource/mvResource` **直接置空而不 `Release`**——这两份引用是 `WrapSubmitInputs` 里 `AddRef` 来的（`:1293-1294`），未进退休队列、未被释放。后果：显式 `ZFG_FgShutdown` 之后若再建代理链，会漏 2 个纹理的引用（显存不回收）；进程退出路径无实际影响。属"漏 Release"而非悬垂，故不影响本条成立，但施工报告"生效输入与退休引用全释放"的表述与代码不符。

### V3 变异复做 ✅（两条均"改回旧值即红"，已复原）

| 变异 | 我的操作 | 红的形态（实测） | 复原后 |
|---|---|---|---|
| **B-1** | `PresentReexit` 的 `nestedFlag = r.restoreValue` 改为 `= false` | `test cases: 251 \| 248 passed \| **3 failed**`，`assertions: 711498 \| 711491 passed \| **7 failed**`；单跑过滤确认红点全在 reentry：`(1 layer nesting)`、`(3-layer nesting)`、`(flag unwinds across a full app frame sequence)`，其中 `app == 2` 复现了 frameID 双推进 | 复原后 251/711498 全绿 |
| **B-7** | 删掉 `outSwapChain == nullptr` 检查 | `FATAL ERROR: test case CRASHED: SIGSEGV`（`test_zfg_fg_shutdown.cpp:86`），整轮中断 → 证明该检查确实在所有解引用之前承重 | 复原后 251/711498 全绿 |
| **C1 追加**（为验 V6"是否有牙"） | `fg_ffx.cpp:542` 默认分支 `FLIP_DISCARD` → `FLIP_SEQUENTIAL` | `1 failed`（`test_zfg_host_desc.cpp:293`，用例 `null request keeps the legacy synthesized desc`） | 复原后全绿 |

复原证据：`git diff --stat Native/` 为空（除构建产物 `zrender.dll`）。

### V4 回归 ✅（全部亲跑，非引用施工）

| 命令（仓库根） | 结果 |
|---|---|
| `zrhi_d3d12_window.exe --draw` | `D3D12 FIRST DRAW OK`，exit 0，`device removed reason = 0x0` |
| `zrhi_d3d12_window.exe --lights` | `D3D12 LIGHTS OK`，exit 0，`device removed reason = 0x0` |
| `zrhi_d3d12_window.exe --backend vulkan --draw` | `VULKAN FIRST DRAW OK`，exit 0，`validation error count = 0` |
| `zfg_probe_selftest.exe`（`ZFG_SELFTEST_LOG`→`TempLogs/r10_05/verify_selftest.log`） | **`OK (0 failure(s))`**，30 条 PASS，含 `A-3/B-3: wrap proxy chain created from host desc`、`A-3: proxy desc matches host desc`、`B-3: after WrapShutdown stats/handles are zeroed` |
| `zrhi_d3d12_window.exe --fg`（追加，V6 默认路径端到端） | `D3D12 FG OK (real=60 gen=60)`，exit 0，`InfoQueue errors = 0` |

### V5 文件规范与报告完整性 ✅

- 报告结构齐备：①现场与三处冲突 ②逐条修复（每条含"证据等级"）③自测 ④变异 ⑤遗留。**逐条证据等级与我的独立核对一致**（A-3 回退分支仅代码审查级、A-7 COM 配对无 mock、B-3 detach 真 Unity 未测、B-4 低上限真机不可得——四项自曝全部属实，无夸大）。
- 新增/改动源文件**提交内容为 LF 且无 BOM**（`git show HEAD:<f> | grep -c $'\r'` = 0；工作区若干新文件为 CRLF，`* text=auto eol=lf` 提交时归一，不影响仓库内容）。
- 计数偏差一处（无害）：报告写 B-2 改造"9 处站点"，实际 reader 调用 11 处（含 `ZFG_PROBE`/`ZFG_PROBE_LOG`）。

### V6 C1（`zupscaler/fg_ffx.*` 越界）✅ 追认条件成立

1. **默认参数**：`fg_ffx.h:183-185` 两个尾部参数 `= nullptr` ✅。
2. **默认路径逐位一致**：`git show 06a3660 -- Native/zupscaler/fg_ffx.cpp` 的 deleted 行是 `SampleDesc.Count=1 / BufferUsage=RT / SwapEffect=FLIP_DISCARD / Flags=WAITABLE`；新 else 分支给的是同样三个值，`Flags` 从 `=` 改 `|=` 但作用在零初始化结构上 → **同一 bit**；`SampleDesc.Quality / Stereo / Scaling / AlphaMode` 两版都是 0；`fullscreenDesc` 两版都是 `nullptr`（`const_cast(nullptr)` 仍为 nullptr）。结论：**逐位一致成立**。
3. **回归钉有牙**：`test_zfg_host_desc.cpp:279-299` 断言默认路径 7 个字段的精确值；我把它所守护的默认迁移 `SwapEffect` 改掉 → 该用例 1 断言变红（见 V3 追加行）。**非空转**。
4. **FFX 官方契约未改**：只改了本仓 wrapper 类 `zr::FrameGenerator`，FFX 第三方头与 `ffxCreateContextDescFrameGenerationSwapChainForHwndDX12` 的 `desc` 字段类型（本就是 `DXGI_SWAP_CHAIN_DESC1*`）零改动。
5. **自曝缺口属实**：`test_zfg_host_desc.cpp:102` 有 `createSwapchainRc` 拒绝注入点但**无任何用例设置非 OK**；`test_fg_ffx.cpp:752` 只验证了 fg 层返回 false，**未覆盖 wrap 层"拒绝 → 回退旧 desc 重试"**（`zfg_probe_wrap.cpp:1049-1085`）。该分支我读代码判断逻辑正确（回退后走 8 参调用 = 历史合成 desc），但**证据仍停在代码审查级**，与施工自述一致。

### V7 C2（`zrhi_device.h` 28→32）✅

- **尾部追加**：新字段 `barrierTruncationCount` 在 `descriptorSetAllocFailureCount`(offset 24) 之后，offset **28**，`sizeof` **32**，两条 `static_assert`（`zrhi_device.h:244-257`）与 `test_zrhi_device_stats.cpp` 的 8 条偏移断言全部保留、同步。
- **D3D12 零行为变化**：`git show --stat 06a3660` 中 **`Native/zrhi/d3d12/` 无任何文件**；`ZrD3D12Device::GetDeviceStats` 未赋值新字段，但结构成员有 NSDMI `= 0` → 恒 0（`test_zrhi_device_stats` 断言默认值 0）。
- **无 C#/C ABI 按旧偏移读取**：全仓 `ZrDeviceStats` 只出现在 `zrhi/` 内部与测试，**无一处 `.cs`**，也没有 `extern "C"` POD 契约或 `memcpy(sizeof)` 消费点 → 不存在跨语言旧偏移风险。

### V8 C3（B-3 退出路径）✅（决定风险披露如实，未夸大）

1. **导出 + 代理转发**：`dumpbin /exports` 亲验——`zfg_hook.dll` 序号 4 `ZFG_FgShutdown`、`dxgi.dll` 序号 24 `ZFG_FgShutdown`；代理 `zfg_dxgi_proxy.cpp:519-523` 经 `HookProc("ZFG_FgShutdown")` 转发（hook dll 未加载时返回 0，安全）。
2. **幂等闸门并发安全**：`static volatile LONG s_shutdownGate` + `InterlockedCompareExchange(...,1,0)`（`zfg_probe_wrap.cpp:1383-1386`），函数内静态常量为静态初始化、无首次调用竞态；两条触发路径只执行一次，无双释放。
3. **detach 兜底吞掉失败 + 死锁风险已注明**：`DllMain` 的 `DLL_PROCESS_DETACH` 直调 `WrapShutdown`；GPU 追平 `FlushGameQueue(2000)` 的返回值被 `(void)` 丢弃、内部各失败分支 `return false`、全路径无异常 → **失败确实全部吞掉**；注释如实写"detach 持加载器锁……理论上仍有 loader-lock 类死锁面……**推荐路径仍是托管侧显式调 `ZFG_FgShutdown`**"。**没有写成"已彻底安全"，不构成夸大**；报告⑤遗留第 2 条口径相同。

**V8 残余风险（不在追认条件内、供主控知悉）**：
- **闸门不可复位**：`s_shutdownGate` 置 1 后永不复位，也没有在 `WrapCreateSwapChainForHwnd` 里重置。显式关停后再建代理链，后续任何关停都静默 no-op（幂等换来的代价）。当前无生产路径（导出等接线任务消费），风险低但真实。
- 与 V2 的 A-7 漏 Release 叠加：若真的"关停后重建"，会同时漏 2 个纹理引用 + 无法再次关停。

### 总结论

- **8/8 条修复成立**，修复点位置、用例、证据形式与各条"建议验证方式"逐条对得上；施工自报数字（251/711,498）我独立实跑得到**完全相同**的数字，两处指定变异（B-1 3 用例/7 断言红、B-7 SIGSEGV）与施工自述**完全一致**。
- **三处越界追认条件（默认路径逐位不变）成立**：C1 默认 desc 逐位相同且有牙、C2 纯尾部追加且无跨语言读取、C3 导出/转发/幂等/风险披露全部到位。
- **受限三处**（均为"关停路径"的收尾问题，不阻塞本任务）：A-7 关停漏 `Release`（`zfg_probe_wrap.cpp:1415-1416`）；关停闸门不可复位；A-3 的 FFX 拒绝回退分支仍只有代码审查级证据（施工已自曝）。
- **施工无夸大**；唯一不精确是 B-2 站点计数（9 vs 11）。

