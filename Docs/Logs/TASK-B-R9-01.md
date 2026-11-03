# TASK-B-R9-01 —— FG 的 C ABI + Unity 接线（DXGI swapchain hook 路线）

> **本任务带门禁：阶段 0 可行性 spike 不通过就停，不进入实现。** 失败是合法结果，但必须如实报告。
> 施工代理按本简报执行，第 2 节写施工报告。**禁止 git commit**。验收由另一代理独立执行。

## 任务简报（主控写）

### 背景与实测事实（2026-10-28 主控亲跑，证据在下方）

A-R9-01 曾裁决"FG 在 Unity 侧接不了"，把真接线转为本任务（todo）。本轮主控实测发现**原判断有一处事实错误**：

```
$ ./Native/build/bin/Release/zrhi_d3d12_window.exe --fg
--fg probe: support=3 rc=0 (OK) provider='3.1.6'
fg: ffxCreateContext rc=0 (OK) context=...
fg: swapchain ffxCreateContext rc=0 (OK) wrapped=...
--fg present counters: real=60 generated=60 (framesRan=60)
D3D12 FG OK
```

即：**FG 在本机 AMD Radeon 780M 上完全可用**（真插帧 real=60/gen=60），而
`Native/zupscaler/fg_ffx.h:31` 的注释写着"FG 官方支持矩阵不含核显（780M/RDNA3 iGPU 预期被拒，同 FSR4 先例）"
——**与实测矛盾，属错误注释，本任务顺手纠正**（只改注释，不改逻辑）。

**真正的缺口是宿主架构，不是硬件**：
- FG 需要 `ID3D12Device*`、`ID3D12GraphicsCommandList*`、depth/MV 的 `ID3D12Resource*`，以及**交换链**（FFX 要 wrap 它）。
- Unity 侧：device 可经 `IUnityGraphicsD3D12v7::GetDevice()` 拿到；cmdlist 可经 `IssuePluginEventAndData` +
  `GetCommandBuffer()` 在渲染线程拿到；depth/MV 可经 `Texture.GetNativeTexturePtr()` 拿到。
- **唯独交换链拿不到** —— 它是 Unity 内部创建的，不对外暴露。所以必须 hook DXGI。

### 阶段 0：可行性 spike（**门禁，必须先做**）

目标：用最小代价回答"这条路到底能不能成"，**不做完整实现**。必须回答三个问题并给出证据：

1. **Unity 2022.3 在 D3D12 下实际走哪个 DXGI 创建路径？**（`CreateSwapChainForHwnd` / `CreateSwapChainForComposition` / 其它）
   做法建议：先装一个**只记录不替换**的 hook（写日志后转发原始调用），实跑 Unity 看日志落在哪个函数、参数是什么。
2. **把返回的 swapchain 换成 FFX wrapped 版本后，Unity 能否正常工作？**
   判定标准：不崩、画面正确（截图/像素校验）、FFX 计数器 `generated` 随 `real` 增长。
3. **FFX swapchain context 的生命周期能否与 Unity 对齐？**（创建时机、resize、全屏切换、Editor 退出/进入 PlayMode）

**门禁判定**：
- 3 问全绿 → 进入阶段 1。
- 任一为红且无法在合理代价内解决 → **停止实现**，输出 spike 报告（含证据与卡点），由主控决定退回"仅 C ABI"或关闭任务。
- **不要为了"做完任务"而强行写一个跑不通的实现**。跑不通就如实说，这比假成功有价值。

### 阶段 1–3（仅在阶段 0 通过后执行）

1. **C ABI 导出**：在 `Native/zplugin/` 增 `ZR_FrameGen_*`（`QuerySupport` / `Create` / `Destroy` / `Configure` / `Prepare` / `Present` 等，按 FFX 实际需要裁剪），
   加入 `Native/zplugin/zrender.def`。**注意 def 文件的教训**（见文件内注释）：EXPORTS 节内不许有 UTF-8 中文行内注释，否则符号静默丢失。
   跨 DLL 边界只传 POD + `void*`，异常不得跨越边界。
2. **Unity 原生插件**：`IUnityGraphicsD3D12v7` 拿 device/cmdlist；depth/MV 用 `Texture.GetNativeTexturePtr()`；
   DXGI hook 做 proxy swapchain（**必须可关闭**：环境变量或设置项，关掉时完全走原生路径，不留副作用）。
3. **Unity 侧封装**：`UnityProject/Assets/ZRender/Bindings/ZrNative.cs` 增 P/Invoke（这是全仓唯一 `DllImport` 集中地，勿分散）；
   能力门控（`QuerySupport` 不支持时不显示开关，**不加"能点但无效"或"永远灰"的死行**——A-R9-01 已定此纪律）。
4. **设置页开关**：仅 `Available` 时出现；文案说明代价（延迟增加/需要 MV）。

### 硬约束

- **不得修改 Unity 安装目录**；hook 只能来自我们自己的插件。
- **hook 必须可一键关闭**且关闭后与当前行为完全一致（崩溃时的逃生门）。
- 必须**失败安全**：hook 中任何异常/失败都要回退到原始 swapchain，绝不能让游戏起不来。
- 不改 `FrameGovernor`/`GovernorLadder`/`GovernorTuning`（与 A-R9 系列解耦）；不动 `URP_*.asset`。
- `.cs` 无 BOM、LF；C++ 保持 `/W4 /WX` 零警告。
- **禁止 git commit**。

### 验收口径预告（验收代理会查）

- spike 的三问是否有**实证**（不是"应该可以"）。
- hook 关闭后是否真的零副作用（对比开关前后的帧数/画面）。
- 是否出现"能点但无效"的开关（出现即 FAIL）。
- 原生侧六段不回归：native **190/710,800** 不变；editmode **678**；playmode 6。

### 环境纪律

- 构建/测试前 `unset http_proxy https_proxy all_proxy`（重复大小写代理变量会让 MSBuild 抛「字典重复键」）
- cmake 不在 PATH：`CM="/c/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"`
- 六段：`powershell -NoProfile -ExecutionPolicy Bypass -File Tools/run_tests.ps1`（**Bash 侧可能禁止转调 powershell，用 PowerShell 工具并先清代理变量）**
- 清理用 `mv` 归档到 `TempLogs/archive_pre_audit/`，不要 `rm -rf`
- Unity 本机**可用**（2026-10-28 六段实证），批处理 + PlayMode 都能跑

### 交付

「施工报告」写入第 2 节，**先写阶段 0 结论**（三问 + 证据 + 门禁判定 PASS/STOP），再写阶段 1–3（若执行）。
若门禁 STOP，报告到此为止即可，不要硬写后续阶段。

---

## 施工报告（施工代理写）

### 门禁判定：**STOP**

三问未全绿：**Q1 绿（硬实证）**、**Q2 半绿（交换链替换成立，插帧生成不成立）**、**Q3 未验证**。
按简报「任一为红且无法在合理代价内解决 → 停止实现」，阶段 1–3 **未执行**，本任务不出产品代码。

一句话结论：**交换链这一关过了——FFX 代理交换链可以被返回给 Unity，Unity 在它上面持续呈现、
不崩溃、每帧 Configure 成功；但"真插帧"卡在 depth/MV 没有来源，这不是 spike 能补的，是 SRP
侧的集成缺口。** 另外挖出两条会改变产品形态的硬事实（插件加载时机、Editor 不可行），需要主控裁。

### 阶段 0 · Q1：Unity 2022.3/D3D12 实际走哪条 DXGI 创建路径 —— **绿**

**路径：`IDXGIFactory2::CreateSwapChainForHwnd`。** 全过程没有 `IDXGIFactory::CreateSwapChain`
（legacy），也没有 `ForComposition` / `ForCoreWindow`——四个槽位都挂了记录钩子，只有 ForHwnd 命中。

Player（`-force-d3d12`）逐字日志：

```
[+662674703ms] IDXGIFactory2::CreateSwapChainForHwnd tid=41004
  desc 2880x1800 fmt=28 buffers=3 usage=0x30 effect=3 flags=0x842 scaling=0 alpha=0
  fullscreenDesc=null factory=0x0000018E1986F050
  hwnd hwnd=0x0000000006051910 class='UnityWndClass' title='ZFrame' client=2880x1800
  -> hr=0x00000000 swapchain=0x0000018EFA6900B0
  swapchain class[0] vtable=0x00007FFDF3A6D688 present=ok present1=ok resizeBuffers=ok
```

参数解读（这几条对实现是硬约束）：

| 字段 | 实测值 | 含义 |
|---|---|---|
| `fmt=28` | `DXGI_FORMAT_R8G8B8A8_UNORM` | **不是**窗口程序 `--fg` 用的 B8G8R8A8（87）；代理链必须按 RGBA8 建 |
| `buffers=3` | 3 | 代理链 bufferCount 必须跟随 |
| `effect=3` | `FLIP_DISCARD` | FFX 也固定用 FLIP_DISCARD，兼容 |
| `flags=0x842` | `ALLOW_TEARING(0x800)` + `FRAME_LATENCY_WAITABLE_OBJECT(0x40)` + `ALLOW_MODE_SWITCH(0x2)` | 宿主自己就要 waitable，与 FFX 的 waitable 契约相容 |
| `usage=0x30` | `RENDER_TARGET_OUTPUT(0x20)` + `SHADER_INPUT(0x10)` | — |

呈现走的是 **`IDXGISwapChain1::Present1`**（不是 `Present`），`sync=1`，稳定 60fps：

```
[+663007218ms] IDXGISwapChain1::Present1 #1 sync=1 flags=0x0 sc=0x000001A699944080 dirtyRects=0
[+663007890ms] IDXGISwapChain1::Present1 #60 sync=1 flags=0x0 sc=0x000001A699944080 dirtyRects=0
[+663012343ms] IDXGISwapChain1::Present1 #300 sync=1 flags=0x0 sc=0x000001A699944080 dirtyRects=0
```

附加事实（影响 hook 健壮性）：**第一次创建若因故不返回，Unity 会换一套描述重试**——实测重试
描述为 `usage=0x20 effect=4(FLIP_SEQUENTIAL) flags=0x840`（我第一版 wrap 死循环时观察到它每秒
重试 2–3 次）。hook 必须容忍重试与多条链并存。

证据文件：`TempLogs/r9_probe/probe_player_log.log`（真实工程 Player）、`probe_empty_log.log`
（空场景 Player，含 Present1 节奏）。

### 阶段 0 · Q2：换成 FFX wrapped swapchain 后 Unity 能否正常工作 —— **半绿**

**能跑（已实证）**：wrap 模式把 FFX 代理交换链返回给 Unity，Unity 直接在它上面呈现，连续
~9 秒、1680 次呈现、无崩溃、无 D3D12 报错、`ConfigureFrame` 每帧成功（日志里没有一条
`ConfigureFrame FAILED`）：

```
[+663078515ms] IDXGIFactory2::CreateSwapChainForHwnd tid=21280
  desc 2880x1800 fmt=28 buffers=3 usage=0x30 effect=3 flags=0x842   <- Unity 的请求
[+663079062ms] IDXGIFactory2::CreateSwapChainForHwnd tid=21280
  desc 2880x1800 fmt=28 buffers=3 usage=0x20 effect=4 flags=0x840   <- FFX 内部链（重入→原生）
  -> hr=0x00000000 swapchain=0x000002C917CD8A00
  -> WRAPPED swapchain=0x000002C917C9FD90 (native creation skipped)
  swapchain class[1] vtable=0x00007FFD1567F820 present=ok present1=ok resizeBuffers=ok
[+663079187ms] IDXGISwapChain1::Present1 #1 sync=1 flags=0x0 sc=0x000002C917C9FD90
[+663088156ms] IDXGISwapChain::Present #1680 sync=1 flags=0x0 sc=0x000002C917CD8A00
```

（后半段 `sc=0x...CD8A00` 是 FFX 自己用同一个 factory 建的内部交换链；我第一版没加重入闸，
结果 FFX 的 ForHwnd 路径被我自己的 hook 递归拦住——日志里只剩入口行、Unity 反复重试。加
`thread_local` 闸后即通过，这条经验必须带进实现。）

**没证明的两条（正是判定红的部分）**：

1. **`generated` 随 `real` 增长——未做到。** PrepareV2 需要 **深度图 + 运动矢量**两张
   `ID3D12Resource`。本工程 Unity 侧两者都没有来源：URP 未开 motion vector pass，插件层拿不到
   相机深度。没有数据就不敢把 `frameGenerationEnabled` 置 true（FFX 契约要求每帧 Prepare 与
   Configure 的 frameID 对齐），所以本轮只验证到"代理链直通"。
2. **画面正确——未验证。** 同上（没开插帧）；且本机无法截屏（无交互桌面）。

**额外障碍**：产品自身的 Player（SCN_Boot）在本机 **boot 就中断**，日志停在
`[GameBootstrap] Awake: full boot starts.`，注入 hook 前后完全一致（`player_run2/3/4/5.log`
都一样），与本任务无关，但导致真实游戏场景无法用于端到端验证——所以才另建空场景 Player
（`Assets/Scenes/SCN_Empty.unity`，仅临时构建到 `TempLogs/archive_pre_audit/r9_probe_players/`）。

### 阶段 0 · Q3：FFX swapchain context 生命周期能否与 Unity 对齐 —— **未验证 + 结构性障碍**

- **创建时机（已实证）**：一次，发生在**图形设备初始化期、早于任何托管代码**。证据链：
  ① 注入器在 `CREATE_SUSPENDED` 状态装好 hook 才 `ResumeThread`，日志里创建调用紧跟在引擎
  起来之后；② Unity 自身日志顺序是 `GfxDevice: creating device client` → `Direct3D 12` →
  `Begin MonoManager ReloadAssembly`，创建发生在装配加载之前。
- **失败安全（已实证）**：`ZFG_PROBE=0/未设` 完全不装；wrap 失败立即回退原生创建并留日志
  （实测 `wrap FAILED (FG not available ...); falling back to native swapchain` 之后 Unity 正常
  跑满 60fps）。
- **未验证**：resize（两轮跑下来 `ResizeBuffers` 命中 0 次）、全屏/独占切换、进出 PlayMode。
- **Editor 侧无法验证**：本机 GUI Editor 加 `-force-d3d12` 启动后**挂起**（`-logFile` 一个字节
  都不产生，进程停在 108MB 不动），批处理模式能跑但没有交换链。

**结构性障碍（这条比 Q3 本身更重要）**：Unity 的原生插件是**惰性加载**的。实测 Player 里
`zrender.dll`（脚本 P/Invoke）在进程模块表里，而同目录的 `zfg_probe.dll` **全程未加载**
（读 `Process.Modules` 实证）。也就是说"把 DLL 丢进 `Assets/Plugins` 就自动接管交换链"
**不成立**——交换链在托管代码之前就建好了。spike 里验证可行的是**进程启动期注入**
（`zfg_probe_inject.exe`：挂起创建 → 注入 → 远程同步安装 → 恢复），或等价的 `dxgi.dll` 代理。
两者都是**部署形态改变**，不是纯 Assets 内资源。

### 做了什么（产物）

| 文件 | 作用 |
|---|---|
| `Native/zfgprobe/zfg_probe_hook.{h,cpp}` | vtable hook：4 个工厂槽（legacy/ForHwnd/CoreWindow/Composition）+ 交换链 `Present`/`Present1`/`ResizeBuffers`；`log`/`wrap` 双模式；环境变量可关；失败一律回退 |
| `Native/zfgprobe/zfg_probe_wrap.{h,cpp}` | 复用 `Native/zupscaler/fg_ffx`（与 `--fg` 同一份封装）建 FG 上下文 + 代理交换链；格式按 `ffxApiGetSurfaceFormatDX12` 官方映射（不再硬编码 BGRA8） |
| `Native/zfgprobe/zfg_probe_dll.cpp` | 插件入口（DllMain 起线程安装）+ `ZFG_ProbeEnsureInstalled` 导出 |
| `Native/zfgprobe/zfg_probe_inject.cpp` | 早期注入启动器（挂起→注入→同步安装→恢复），解决"插件惰性加载"时序 |
| `Native/tests/zfg_probe_selftest.cpp` | hook 自测：**13/13 PASS**（安装/转发/ForHwnd 拦截/Present/Resize/卸载 全绿） |
| `Native/CMakeLists.txt` | 4 个 spike 目标，**全部 `EXCLUDE_FROM_DEFAULT_BUILD`**，不注册 ctest，不进产品构建 |
| `Native/zupscaler/fg_ffx.h` | 纠正"780M 核显预期被拒"的错误注释（只改注释，逻辑零改动） |

关键实现经验（留给后续实现者）：① FFX ForHwnd 会用**同一个 factory 重入**建内部交换链，必须
`thread_local` 重入闸，否则自我递归；② 同进程可能有两个 `zfg_probe.dll` 实例（注入的那份 +
Unity 从 `Data/Plugins` 加载的那份），全局变量不共享，需要"槽位里已是本模块代码就跳过"的
保护；③ Unity 用 `Present1` 不是 `Present`。

Unity 侧**未留任何改动**：探针脚本与插件 DLL 已归档到 `TempLogs/archive_pre_audit/unity_spike/`；
Editor/构建把 3 个 `URP_*.asset` 自动改写了（shader prefilter 字段重排），已 `git checkout`
还原；构建重写的 `Assets/Plugins/ZRender/zrender.dll` 也已还原到 HEAD。

### 六段数字（`Tools/run_tests.ps1`，ALL PASS，6 passed / 0 failed）

| 段 | 结果 |
|---|---|
| native | `test cases: 190 \| 190 passed`；`assertions: 710800 \| 710800 passed`（`build_native exit 0`，`/W4 /WX` 零警告） |
| editmode | `total=678 passed=678 failed=0` |
| playmode | `total=6 passed=6 failed=0` |
| arch | PASS `exit=0`（`TempLogs/arch_report.txt`） |
| config | PASS `exit=0`（`TempLogs/config_report.txt`） |
| naming | PASS `exit=0`（`TempLogs/name_report.txt`） |

原始日志：`TempLogs/r9_probe/six_segments.log`。基线未回归（190/710,800 / 678 / 6 全对得上）。

### 未验证项 / 卡点

1. **Q2 的两条硬指标**：`generated` 增长、画面正确。卡在 depth/MV 无来源。
2. **Q3**：resize、全屏切换、进出 PlayMode；Editor 路径（本机 GUI Editor 挂起，无法判定是
   环境问题还是 Unity 自身问题）。
3. 真实工程 Player 的 boot 中断（与本任务无关，但挡住了端到端验证）。
4. 代理链长时间稳定性（只跑了十来秒量级）；FFX 代理链固定 FLIP_DISCARD + waitable，与 Unity
   原始描述（含 `ALLOW_TEARING`）不完全一致，未验证 tearing 语义。
5. 未做 `generated` 计数的"假数据"探索——刻意不做：喂垃圾 MV 能骗出计数，但那是假成功。

### 需要主控决断（三选一）

- **A（推荐）**：**退回"仅 C ABI"**。把 `ZR_FrameGen_*` 导出与单测做掉（不碰 Unity），等 SRP
  能产出 depth/MV 再谈接线。
- **B**：接受"Player + 注入器/代理 `dxgi.dll`"的部署形态，并把任务拆成三条：① C ABI；
  ② 注入器/hook 产品化（含 Editor 不显示开关的门控口径）；③ **depth/MV 产出（第一优先级，
  工作量最大）**。
- **C**：关闭本任务。

不建议现在直接进阶段 1–3：交换链不是瓶颈，depth/MV 才是；先做 C ABI 不会浪费。

---

## 验收清单（供验收代理）

- [ ] V0 阶段 0 三问都有实证（日志/截图/计数），门禁判定明确；若 STOP 则后续阶段确未强行实现
- [ ] V1 C ABI 导出成功且 `zrender.def` 符号数正确（导出表实测，不是看代码）
- [ ] V2 hook 可关闭，关闭后行为与当前一致（对比证据）
- [ ] V3 失败安全：构造 hook 失败 → 回退原始 swapchain，游戏仍能启动
- [ ] V4 无"能点但无效"或"永远灰"的开关；能力门控正确
- [ ] V5 原生六段不回归（native 190/710,800 / editmode 678 / playmode 6）
- [ ] V6 范围：未改 Unity 安装目录、未动 URP 资产、`.cs` 无 BOM、未 commit
- [ ] V7 `fg_ffx.h:31` 错误注释已纠正（实测可证伪的部分）

## 验收结论（验收代理写）

（待填）
