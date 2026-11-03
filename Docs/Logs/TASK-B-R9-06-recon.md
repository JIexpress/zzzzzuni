# TASK-B-R9-06 侦察笔记：FG 落地接线（设置页开关 + 能力门控）

> 只读侦察。所有结论后面的 `文件:行号` 均为本次实测读取位置（Windows / 原生 C++ + Unity 2022.3）。
> 标注【待确认】的条目我没有找到可自证代码，不猜。
> 侦察时间：2026-10-29。基线：TASKS.md 里 B-R9-05 = `0a0928f`，B-R9-04 = `1403186`。

---

## 0. 先给三条会改变施工方案的事实（后面全部展开）

1. **运行期开关的 C 入口已经存在，不需要新加函数**：`ZFG_FgSetEnabled(int)` 在 `dxgi.dll` 与 `zfg_hook.dll` 两侧都有导出，实现是每帧读取的原子覆盖值，语义是"抑制插帧"而不是"销毁上下文"。
   证据：`Native/zfgprobe/zfg_dxgi.def:33`、`Native/zfgprobe/zfg_dxgi_proxy.cpp:457-461`、`Native/zfgprobe/zfg_probe_dll.cpp:53-56`、`Native/zfgprobe/zfg_probe_wrap.cpp:84`（`g_enabledOverride`）、`:895-896`、`:972-976`。

2. **但"关掉再打开"里的"打开"在当前代码里做不到**：`wantFrameGen` 由启动模式硬编码，运行期标志只能往下压，不能往上抬。
   证据：`Native/zfgprobe/zfg_probe_hook.cpp:779` 与 `:834`——`WrapOnFrame(self, g_mode == Mode::Fg)`；`g_mode` 只在 `InstallFromEnv()` 里由环境变量决定一次（`:1356-1385`）。

3. **最致命的事实：产品侧没有任何"每帧提交 depth/MV"的 C# 代码。** FG 需要 `ZFG_FgSubmitInputs` 每帧送输入；`Assets/` 下对 `ZFG_*` 的 P/Invoke 数量为 **0**（grep 全空）。唯一实现过的那份在 `TempLogs/r9_03/probe_assets/__zfgprobe/ZfgR903Bridge.cs`（临时探针，任务收口时应归档）。
   → 只加一个开关，玩家把开关打开后**一帧都不会被插**，因为 `WrapOnFrame` 里 `want && haveInputs` 的 `haveInputs` 恒 false（`zfg_probe_wrap.cpp:897`、`:907`）。

---

## 1. 关键文件地图

### 1.1 原生 / FG 控制面

| 路径 | 作用 | 关键行 |
|---|---|---|
| `Native/zfgprobe/zfg_dxgi.def` | `dxgi.dll` 代理导出清单：20 个系统导出按序号 @1..@20 + 5 个私有 `ZFG_*` | 11-30（系统名+序号）、31-35（`ZFG_FgSubmitInputs`/`ZFG_FgGetStats`/`ZFG_FgSetEnabled`/`ZFG_FgAnalyzeCaptures`/`ZFG_ProxyReady`）；注释 8 明确"不占序号" |
| `Native/zfgprobe/zfg_dxgi_proxy.cpp` | 代理本体：精确转发 20 导出 → 首次 `CreateDXGIFactory*` 里惰性加载 `zfg_hook.dll`；`ZFG_*` 二次转发 | `ZFG_HOOK` 读点 `:101-103`、`:216-221`；`MaybeInstallHook` `:210-252`（LoadLibrary `:233`，取 `ZFG_ProbeEnsureInstalled` `:241-242`，调用 `:248`）；`ZFG_FgSetEnabled` `:457-461`；`ZFG_ProxyReady` `:471-474`；`DllMain` `:476-486` |
| `Native/zfgprobe/zfg_probe_dll.cpp` | `zfg_hook.dll` 入口：4+1 个 C 导出 + DllMain 里 50ms 兜底安装线程 | `ZFG_ProbeEnsureInstalled` `:35-38`；`ZFG_FgSubmitInputs` `:41-44`；`ZFG_FgGetStats` `:47-50`；`ZFG_FgSetEnabled` `:53-56`；`ZFG_FgAnalyzeCaptures` `:59-62`；`DllMain` `:63-74`；兜底线程 `:23-30` |
| `Native/zfgprobe/zfg_probe_hook.cpp`（1435 行） | vtable hook 安装 + Present/Present1 拦截 + D3D12 门控 + FG 契约推进 | `Mode` 枚举 `:62`；`DeviceIsD3D12` `:532-548`；`GateAllowsFrameGen` `:552-565`；`HookCreateSwapChainForHwnd` `:606-660`（wrap 判定 `:629-658`）；`HookPresent` `:750-805`（应用级判定 `:757`、`WrapOnFrame` `:779`）；`HookPresent1` `:807-852`（`WrapOnFrame` `:834`）；`InstallFromEnv` `:1356-1385` |
| `Native/zfgprobe/zfg_probe_wrap.cpp`（1050 行） | FFX 代理交换链 + 每帧 Configure/PrepareV2 + 统计 + 运行期开关 | 全局 `:50-93`（`g_fg` `:50`、`g_frameId` `:53`、`g_enabledOverride` `:84`、`g_enabledNow` `:85`）；`WrapCreateSwapChainForHwnd` `:755-853`；`WrapOnFrame` `:855-941`；`WrapSubmitInputs` `:943-956`；`WrapGetStats` `:958-970`；`WrapSetFrameGenEnabled` `:972-976`；`WrapShutdown` `:1017-1043` |
| `Native/zfgprobe/zfg_probe_wrap.h` | 上表的声明 + 两种模式（wrap=直通 / fg=真插帧）的说明 | 9-13（模式语义）、`WrapSetFrameGenEnabled` 40、`WrapShutdown` 59 |
| `Native/zfgprobe/zfg_fg_input.h` | **C ABI 契约**：`ZfgFgFrameInputs`（56 行文件，结构 23-46）+ `ZfgFgStats`（49-56）+ 版本号（21） | 见左 |
| `Native/zfgprobe/zfg_probe_hook.h` | hook 的 C++ 接口（非 C ABI）：`InstallFromEnv`、`ProbeLog`、`DumpSwapchainSummary` | 32、52、56 |

### 1.2 FFX 封装（被 wrap 复用，不在 zfgprobe 内）

| 路径 | 作用 | 关键行 |
|---|---|---|
| `Native/zupscaler/fg_ffx.h` | `FrameGenerator` 类：`Create` / `CreateFgSwapchainForHwnd` / `ConfigureFrame` / `DispatchPrepare` / `PresentFrame` / `WaitForPresents` / `Shutdown` | `FrameGenCreateDesc` 100-117（`jitterCancellation` 111、`depthInverted` 114）；`ConfigureFrame` 声明 182-183；`PresentFrame` 注释 192-193（"FG 关闭时退化为普通 present + 一次内部合成拷贝"）；`Shutdown` 契约 200-203 |
| `Native/zupscaler/fg_ffx.cpp`（801 行） | 实现 | `Create` 里 `ffxCreateContext` 448、479-487；`CreateFgSwapchainForHwnd` 518-586（swapchain 的 `ffxCreateContext` 564-565、waitable 578）；`ConfigureFrame` 589-628（`cfg.frameGenerationEnabled` 609、`cfg.frameID` 618、`funcs.Configure` 619）；`DispatchPrepare` 630-676（`funcs.Dispatch` 668）；`PresentFrame` 688-704；`WaitForPresents` 706-724；`Shutdown` 726-776 |

### 1.3 构建 / 部署

| 路径 | 作用 | 关键行 |
|---|---|---|
| `Native/CMakeLists.txt` | `zfgprobe`（静态库）439-447；`zfg_probe` 449-454；**`zfg_dxgi`（OUTPUT_NAME=dxgi）456-470**；**`zfg_hook` 472-480**；四个目标全部 `EXCLUDE_FROM_DEFAULT_BUILD YES`（446/453/469/479） | 见左 |
| `Tools/build_native.ps1`（140 行） | 4 步：cmake configure / build / doctest / **只拷 `zrender.dll`** 到 `Assets/Plugins/ZRender` | 步骤 96-124；拷贝 112-124（`$DllFile` 只在 38 行定义为 `zrender.dll`） |
| `Docs/Logs/TASK-B-R9-04.md` | Player 目录是人工装配的 | 88（`TempLogs/r9_04/player/`）、353-356（`cp -r .../player/. .../audit_<mode>/`） |
| `Docs/Logs/TASK-B-R9-05.md` | 同上；二进制 md5 对照 | 391-393、529-530 |

### 1.4 Unity 设置页 / 能力门控 / 绑定风格

| 路径 | 作用 | 关键行 |
|---|---|---|
| `UnityProject/Assets/ZFrame/View/Settings/SettingsRegistry.cs` | 全站设置的唯一数据源；视频页 8 行 | `BuildVideoPage` 112-193；`video.upscaler` 161-165；`video.dynamicResolution` 170-174；`video.frameRateTarget` 178-183；**行序注释** 167-169（"两个新增行都排在 qualityTier 之后"） |
| `UnityProject/Assets/ZFrame/View/Settings/FrameGovernorSettingOptions.cs` | A-R9-01 两行的单一数据源（id/标签/后缀/apply） | id 29、32；`TargetFpsEffectSuffix` 62；`DrsNonUrpSuffix` 69；`RenderScaleSupported` 112-115；`ApplyDrs` 121-132；`ApplyTargetFps` 138-150 |
| `UnityProject/Assets/ZFrame/View/Settings/UpscalerSettingOptions.cs` | **现成的"能力查询 + 标签后缀"模板** | `UnsupportedSuffix="（本机不支持）"` 46；`BuildOptionLabels` 69-91；`MarkIfUnsupported` 113-116；`ApplyOptionIndex` 98-111 |
| `UnityProject/Assets/ZFrame/View/Settings/SettingsBootApplier.cs` | 冷启动应用点（`AfterSceneLoad`，重放 settings.json） | `AutoBoot` 32-36；`ApplyFromDisk` 44-56 |
| `UnityProject/Assets/ZFrame/View/Pipeline/FrameGovernorHost.cs` | 宿主自举（`BeforeSceneLoad`）+ `SetEnabled`/`SetTargetFps` | `AutoBoot` 105-116；`SetEnabled` 184-204；`SetTargetFps` 207-214 |
| `UnityProject/Assets/ZFrame/View/Settings/SettingsRowFactory.cs` | 行生成器（Toggle/Slider/Enum/Action/Binding/BindingReset/Note） | `LabelWidth=340f` 29；`RowNamePrefix="Row_"` 47；类型分派 145-165 |
| `UnityProject/Assets/ZFrame/View/Settings/SettingDefinition.cs` | 定义 API；**没有"灰显/禁用"概念**，只有 `Note` 与标签文案 | 工厂 122/133/155/177/199/222/228 |
| `UnityProject/Assets/ZFrame/View/Settings/SettingsModel.cs` | 持久化（settings.json，原子写） | `Save` 142-…（按 `AllSettings` 顺序写，150）；`Load` 221-269（**按 JSON 键顺序重放**，252） |
| `UnityProject/Assets/ZFrame/Core/Upscaler/IUpscalerSettings.cs` | 服务接口放 Core（View 才能用） | 12-34 |
| `UnityProject/Assets/ZRender/Bindings/ZrNative.cs` | **唯一 DllImport 集中地**（`[DllImport("zrender")]`） | 12-52 |
| `UnityProject/Assets/ZRender/Runtime/ZrUpscalerSettings.cs` | 服务实现 + `BeforeSceneLoad` 自注册 + 优雅降级 | `ZrUpscalerBootstrapper` 184-194；降级/告警 56-61、86-… |
| `UnityProject/Assets/ZFrame/View/ZFrame.View.asmdef` | View 的引用里**没有** `ZFrame.Render.Bindings`（只能经 Core 接口） | 第 4 行 `references` |

---

## 2. 现有导出 / 控制面（问题 1）

### 2.1 `dxgi.dll`（`zfg_dxgi.def`）

系统导出 20 个、按序号 @1..@20 精确转发（`zfg_dxgi.def:11-30`，实现 `zfg_dxgi_proxy.cpp:311-419`），外加 5 个私有导出：

| 导出 | 实现位置（代理） | 语义 | 关键事实 |
|---|---|---|---|
| `ZFG_FgSubmitInputs(const ZfgFgFrameInputs*)` | `zfg_dxgi_proxy.cpp:443-447` → hook `zfg_probe_dll.cpp:41-44` → `WrapSubmitInputs` `:943-956` | 每帧送 depth/MV + 相机 | 返回 0 = hook 未加载或输入非法（`structSize`/`version`/空指针/0 尺寸校验 `:945-948`） |
| `ZFG_FgGetStats(ZfgFgStats*)` | `:450-454` → `:47-50` → `WrapGetStats` `:958-970` | 读统计 | **返回值 1 的前提是 `g_fg != nullptr`（`:969`）→ 这是"代理链真的建立了"的唯一可信探针** |
| `ZFG_FgSetEnabled(int)` | `:457-461` → `:53-56` → `WrapSetFrameGenEnabled` `:972-976` | **运行期开关** | 实现只有一句 `g_enabledOverride.store(enabled?1:0)` + `return 1`；**无条件返回 1（"已受理"≠"已生效"）** |
| `ZFG_FgAnalyzeCaptures()` | `:464-468` → `:59-62` → `:978-1000` | 像素读回取证 | 探针用，产品不需要 |
| `ZFG_ProxyReady()` | `:471-474` | 代理是否已接管 | **只反映 `g_state==kStateReady`（系统 dxgi 已解析）**，即使 `ZFG_HOOK` 未设、hook 没装也返回 1 → 不能当作"FG 可用" |

### 2.2 `zfg_hook.dll`（`__declspec(dllexport)`，无 .def）

`zfg_probe_dll.cpp` 里 5 个：`ZFG_ProbeEnsureInstalled:35-38`、`ZFG_FgSubmitInputs:41-44`、`ZFG_FgGetStats:47-50`、`ZFG_FgSetEnabled:53-56`、`ZFG_FgAnalyzeCaptures:59-62`。

### 2.3 `ZFG_HOOK` 环境变量读点（共 4 处）

| 位置 | 用途 |
|---|---|
| `zfg_dxgi_proxy.cpp:101-103`（`OpenLogIfNeeded`） | 决定是否开日志 |
| `zfg_dxgi_proxy.cpp:216-221`（`MaybeInstallHook`） | **决定是否 `LoadLibrary(zfg_hook.dll)` —— 代理的"总闸"** |
| `zfg_probe_hook.cpp:1359`（`InstallFromEnv`） | 决定 hook 装不装 + 模式 |
| `zfg_probe_hook.cpp:1363`（兼容旧 `ZFG_PROBE`） | 同上 |

模式解析（`zfg_probe_hook.cpp:1369-1375`）：`"wrap"` → `Mode::Wrap`；`"fg"` → `Mode::Fg`；**其它任何非零非空字符串 → `Mode::LogOnly`**；未设/`"0"` → 不安装（`:1366-1368`）。

### 2.4 结论：要不要新加 C 函数？

**不需要。** 现有 `ZFG_FgSetEnabled` 已经满足"enable/disable/query"三件里的两件半：
- enable/disable：`ZFG_FgSetEnabled(0|1)`；
- query：`ZFG_FgGetStats`（`ZfgFgStats.enabled` 字段 `zfg_fg_input.h:54`，由 `g_enabledNow` 每帧写入 `zfg_probe_wrap.cpp:923`）。

**真正缺的是两层"授予"能力**，如果主控要求"开关能真的把 FG 打开"，必须动这些地方：

| 缺口 | 现象 | 需要动的位置 |
|---|---|---|
| A. `wantFrameGen` 硬编码为 `g_mode == Mode::Fg` | 用 `ZFG_HOOK=wrap` 启动时，开关拨到 1 也永远不插帧 | `zfg_probe_hook.cpp:779`、`:834`（改成 `g_mode == Mode::Fg \|\| (g_mode == Mode::Wrap && override==1)`，或给 hook 加一个 `ZfgAllowFrameGen` 的授权位） |
| B. `ZFG_HOOK` 未设 → 代理根本不 `LoadLibrary` hook | 默认发布态（双击 exe）没有任何 `ZFG_*` 入口，`ZFG_FgSetEnabled` 返回 0，连代理链都没有 | `zfg_dxgi_proxy.cpp:216-221`（改为"始终加载 hook，由 C# 决定是否启用"）——**注意 `zfg_probe_hook.cpp:629` 的 `g_mode != Mode::LogOnly` 会连带要求把 `LogOnly` 拆成"装 hook 但不 wrap"** |
| C. 没有任何 `ZfgQueryFrameGenSupport` 式的"能不能插"查询 | C# 只能靠 `ZFG_FgGetStats` 返回值反推"代理链已建"，拿不到"本机 GPU 支持吗"（FFX 的 `QuerySupport` 在原生侧 `zfg_probe_wrap.cpp:782-790`，**没有导出**） | 新增导出（`.def:31-35` 追加一行 + `zfg_probe_dll.cpp` 追加一个 `extern "C"` + 代理转发 `zfg_dxgi_proxy.cpp:427-474` 追加 `HookProc` 转发） |

---

## 3. FFX 上下文生命周期 与 "运行时开关" 方案 A/B（问题 2）

### 3.1 生命周期事实

```
Unity 调 CreateSwapChainForHwnd
  → hook (zfg_probe_hook.cpp:606)
  → GateAllowsFrameGen (D3D12 才继续) :629-658
  → WrapCreateSwapChainForHwnd (zfg_probe_wrap.cpp:755)
      ├ QuerySupport(device)                     :782-790   （不支持 → 不 wrap，回退原生）
      ├ fg->Create(desc) → ffxCreateContext       :815 / fg_ffx.cpp:448,479-487
      ├ fg->CreateFgSwapchainForHwnd              :825 / fg_ffx.cpp:518-586
      │     └ swapchain 的 ffxCreateContext       fg_ffx.cpp:564-565  ← 真实交换链在 SDL 上下文里
      └ 写 g_fg/g_wrapped/g_displayW/H、AddRef 队列 :836-844
每帧 Present/Present1
  → WrapOnFrame (zfg_probe_wrap.cpp:855)
      ├ g_frameId += 1                            :860
      ├ 输入消费（fresh/reuse/stale）             :872-893
      ├ g_enabledOverride 读取                    :895-896
      ├ ConfigureFrame(frameId, enabled)          :921 / fg_ffx.cpp:589-628（每帧都调，enabled 只是字段）
      └ enabled 才 DispatchPrepare(V2)            :930-939 / fg_ffx.cpp:630-676
销毁
  → WrapShutdown (zfg_probe_wrap.cpp:1017-1043)  ← ★ 全仓库无任何调用点（grep 仅自身 + 头声明）
```

- **创建点**：`zfg_probe_wrap.cpp:815`（FG 效果上下文）、`:825`（代理交换链上下文）。
- **销毁点**：`zfg_probe_wrap.cpp:1017-1043` → `FrameGenerator::Shutdown`（`fg_ffx.cpp:726-776`）：先 `Configure(enabled=false)`（731-754）→ 销毁 FG 上下文（755-759）→ 销毁**代理交换链上下文**（760-765）→ `CloseHandle(waitable)`（766-769）。
- **`ffxDispatch`**：只出现在 `DispatchPrepare`（`fg_ffx.cpp:668`，PrepareV2）与 `WaitForPresents`（`fg_ffx.cpp:718`，WAIT_FOR_PRESENTS），插帧本身的 `ffxDispatchDescFrameGeneration` 走 `FgFrameGenDispatchTrampoline`（`fg_ffx.cpp:369-370`）由 SDK 回调转发。
- **swapchain 创建**：`fg_ffx.cpp:518-586`，注意 `scd.Flags` 必须带 `FRAME_LATENCY_WAITABLE_OBJECT`（538-542，注释解释了为什么）。

### 3.2 方案 A：仅停 Prepare/Present（纯透传）——**已有实现，推荐**

机制：`g_enabledOverride != 0` 时才算 `want`；`want && haveInputs` 才 `enabled`；`enabled=false` 时 `ConfigureFrame` 把 `cfg.frameGenerationEnabled=false`，然后**直接 return**（不派发 PrepareV2）：

- `zfg_probe_wrap.cpp:895-896`（`want = wantFrameGen && override != 0`）
- `:907`（`enabled = want && haveInputs`）
- `:921`（Configure 每帧都调，**frameID 严格 +1 从不中断**，`:860`）
- `:930-932`（`!enabled` → return true，跳过 PrepareV2）
- `fg_ffx.cpp:609`（`cfg.frameGenerationEnabled = enabled`）

可逆性证据：
- `ConfigureFrame` 的 `presentCallback` / `frameGenerationCallback` 每帧随 Configure 重设（`zfg_probe_wrap.cpp:921` → `fg_ffx.cpp:605-608`），关闭时这两个回调**仍然挂着**，SDK 继续做一次内部合成拷贝——这正是 `fg_ffx.h:192-193` 写明的"FG 关闭时退化为普通 present + 一次内部合成拷贝"。**画面不黑不花，只是多做一次拷贝**。
- frameID 在开/关两种状态下同一条递增序列上（`:860`），不存在"关过之后 frameID 跳变导致 FFX 判历史失效"的问题。
- 线程安全：`g_enabledOverride` 是 `std::atomic<int>`（`:84`，relaxed store `:974` / relaxed load `:895`）；真正生效点在渲染线程的 `WrapOnFrame` 内、`Present` 之前 → **变更天然对齐到帧边界**，不会出现"Present 中途被改"。

**风险（A 的代价，不阻断）**：关闭后 FFX 代理链还在，`Present` 仍走 SDK 的合成路径（多一次 GPU 拷贝 + 一层间接），性能不会完全回到原生；显存（FG 上下文 + swapchain 上下文 + PrepareV2 的三环命令表 `:69-76`、读回槽位 `:101-120`）也不释放。

### 3.3 方案 B：销毁重建上下文（`WrapShutdown` + 重新 wrap）——**技术上不可行 / 极危险**

代码依据（三条独立的硬阻断）：

1. **真实交换链归 swapchain 上下文所有，销毁即悬空**：`fg_ffx.cpp:760-765` 销毁 `m_swapchainContext` 并注释 `m_wrappedSwapchain = nullptr; // 生命周期归代理交换链上下文`。而 Unity 手里拿的是**这个被 wrap 的对象指针**（`zfg_probe_hook.cpp:649-654` 把它 `AddRef` 后交给 `*outSwapChain`）。销毁上下文 = Unity 的 `IDXGISwapChain1*` 变成野指针 → 下一次 `Present`/`GetBuffer` 直接崩。
2. **无法 unwrap**：没有任何"把真实链还回去"的 API 路径；`WrapCreateSwapChainForHwnd` 开头就拒绝二次 wrap（`zfg_probe_wrap.cpp:758-761` `g_fg != nullptr` → `"already wrapped"`），而且它只能从 `HookCreateSwapChainForHwnd` 内部调（`zfg_probe_hook.cpp:629-658`），运行期没有第二个调用入口。
3. **重建必须由 Unity 重建交换链**：即分辨率/模式变更路径（`HookResizeBuffers :854-868` 只是转发，不重建 FFX 上下文）。玩家点一下开关就要重建窗口交换链，等于把"开关"降级成"改分辨率"。

→ **结论：方案 A**。一句话理由：`Configure(frameGenerationEnabled=false)` 是官方契约支持的每帧状态位（`fg_ffx.cpp:602-619`），而销毁路径会连带毁掉 Unity 持有的那个交换链对象（`fg_ffx.cpp:760-765`），不可逆。

### 3.4 A 方案下"再打开"的额外前提（必须写进施工简报）

`wantFrameGen` 目前 = `g_mode == Mode::Fg`（`zfg_probe_hook.cpp:779`/`:834`）。所以：
- 若启动时 `ZFG_HOOK=fg`：**关→开→关 全链路已可用，零原生改动**；
- 若启动时 `ZFG_HOOK=wrap` 或未设：开关只能"关"，打开无效。

这是 B-R9-06 必须让主控裁决的第一个岔口（见 §2.4 缺口 A/B）。

---

## 4. C# 绑定落点（问题 3）

### 4.1 现状

- `zfg_fg_input.h` 是**跨三处共享的 POD 契约**（C# 探针 → dxgi 代理 → zfg_hook）：`ZfgFgFrameInputs`（23-46，`structSize`+`version` 双校验）、`ZfgFgStats`（49-56，含 `enabled` 与 `lastReturnCode`）、版本常量（21，当前 = 1）。头部注释 4-6 行明确"跨模块边界只传 POD + void*，异常不得跨越"。
- **Unity `Assets/` 下对 `zfg_hook` / `dxgi` 的 `DllImport`：0 处**（`grep -rn "ZFG_\|DllImport.*dxgi" UnityProject/Assets` 全空）。
- 唯一历史实现（临时探针，不在 Assets 里，任务收口时应归档）：
  `TempLogs/r9_03/probe_assets/__zfgprobe/ZfgR903Bridge.cs:56-66`
  ```csharp
  [DllImport("dxgi.dll", EntryPoint = "ZFG_FgSubmitInputs", CallingConvention = CallingConvention.Cdecl)]
  static extern int SubmitInputsNative(ref FgInputs inputs);
  ```
  结构体逐字段对应（同文件 11-37），`FgStats` 40-49。**注意它没有用 `ZFG_FgSetEnabled`**（那是 B-R9-03 之后才加/未被探针使用）。

### 4.2 按工程既有风格该放哪

既有分层（`ZFrame.View.asmdef` 第 4 行 references 只有 Core/Data/Gameplay/InputSystem，**没有** `ZFrame.Render.Bindings`）：

```
ZFrame.Core        ← 接口（View 可见）          例：Core/Upscaler/IUpscalerSettings.cs:12-34
ZFrame.Render.Bindings ← 纯 P/Invoke 集中地     例：Bindings/ZrNative.cs:10-52（[DllImport("zrender")]）
ZFrame.Render      ← 服务实现 + BeforeSceneLoad 自注册 + 优雅降级
                                                例：Runtime/ZrUpscalerSettings.cs:62-177, 184-194
ZFrame.View        ← 只认接口 + Services 定位器  例：View/Settings/UpscalerSettingOptions.cs:69-111
```

对应的 FG 落点（**新文件，不改既有文件的分层**）：

| 新增 | 内容 | 参照 |
|---|---|---|
| `Assets/ZFrame/Core/FrameGeneration/IFrameGenerationSettings.cs` | `bool IsAvailable { get; }`、`FrameGenCapability QueryCapability()`、`bool TrySetEnabled(bool)`、`bool TryGetStats(out FrameGenStatsInfo)` | `IUpscalerSettings.cs:12-34` |
| `Assets/ZRender/Bindings/ZrFrameGenNative.cs`（或并入 `ZrNative.cs`） | `[DllImport("dxgi.dll", EntryPoint="ZFG_FgSetEnabled", CallingConvention=CallingConvention.Cdecl)]` 等 + `[StructLayout(Sequential)]` 镜像 `ZfgFgStats`（**必须逐字段核对 `zfg_fg_input.h:49-56` 的 4×u64 + 2×u32 = 40 字节**） | `ZrNative.cs:12-52`、探针 `ZfgR903Bridge.cs:40-49` |
| `Assets/ZRender/Runtime/ZrFrameGenerationSettings.cs` | 捕获 `DllNotFoundException` / `EntryPointNotFoundException` → 返回"不可用"+ 一次性 `ZLog.Warn`；`BeforeSceneLoad` 自注册 | `ZrUpscalerSettings.cs:56-61, 86-…, 184-194` |
| `Assets/ZFrame/View/Settings/FrameGenerationSettingOptions.cs` | id 常量、标签（含后缀）、`ApplyEnabled` | `FrameGovernorSettingOptions.cs` 全文（尤其 121-150） |

**asmdef 注意**：`ZRender/Bindings/ZFrame.Render.Bindings.asmdef` 已存在（`Assets/ZRender/Bindings/`），新文件直接放进去即继承引用关系，不需要动 asmdef。

### 4.3 `DllImport("dxgi.dll")` 的一个真实陷阱

代理**只有叫 `dxgi.dll` 且与 exe 同目录**才会被加载（`zfg_dxgi_proxy.cpp:1-6` 注释：Windows 静态导入先搜可执行文件目录）。在 Editor 里 `DllImport("dxgi.dll")` 会绑定 `System32\dxgi.dll` → `ZFG_*` 符号不存在 → **`EntryPointNotFoundException`**（不是 `DllNotFoundException`）。所以 C# 包装必须两个异常都捕获，且不能只在 `catch (DllNotFoundException)` 上做门控。

---

## 5. 部署现状（问题 4）

### 5.1 `Tools/build_native.ps1` 做了什么

4 步（`:96-124`）：
1. `cmake -S Native -B Native/build -G "Visual Studio 17 2022" -A x64`
2. `cmake --build ... --config Release --parallel`
3. 跑 `zrender_test.exe`（doctest）
4. **只把 `Native/build/bin/<cfg>/zrender.dll` 拷到 `UnityProject/Assets/Plugins/ZRender/`**（`:112-124`）

### 5.2 `dxgi.dll` / `zfg_hook.dll` 由谁拷到哪？

**没有任何脚本。** 事实链：
- CMake 里 `zfg_dxgi`（`:456-470`）与 `zfg_hook`（`:472-480`）都是 `EXCLUDE_FROM_DEFAULT_BUILD YES`（`:469`、`:479`）→ **连第 2 步都不会构建它们**；CMakeLists 顶部注释（`:435-437`）只示范了 `zfg_probe`/`zfg_probe_inject`/`zfg_probe_selftest` 三个目标，**没有提 `zfg_dxgi`/`zfg_hook`**。
- 产物位置：`Native/build/bin/Release/zfg_hook.dll`（实测存在）、`dxgi.dll`（`OUTPUT_NAME "dxgi"`，`CMakeLists.txt:466`）。
- 部署动作是**人工 `cp`**：`Docs/Logs/TASK-B-R9-04.md:88`（`TempLogs/r9_04/player/` 是"施工方 60s Player 原件"）、`:353-356`（验收时 `cp -r .../player/. .../audit_<mode>/`）；`TASK-B-R9-05.md:391-393`、`:529-530` 用 md5 证明两边同一份文件。

### 5.3 有没有 Player 构建脚本？

**没有。** 证据：
- `Assets/` 下 `find -iname "*Build*.cs"` → 空；
- 全仓库唯一的 `BuildPipeline.BuildPlayer` 在 `TempLogs/r9_03/probe_assets/__zfgprobe/Editor/ZfgR903Build.cs:11-19`（探针专用，`TASK-B-R9-03` 收口应归档的临时资产，注释 `ZfgR903Bridge.cs:5-7` 自证"任务收口时整个 Assets/__zfgprobe 会被归档"）；
- `Tools/` 只有 `build_native.ps1` 与 `run_tests.ps1`。

**所以"Player 运行时两个 dll 必须与 exe 同目录"这件事，目前 100% 靠人工。** B-R9-06 若要给玩家可用，必须新写一个部署/打包步骤（或 `[PostProcessBuild]` 钩子），这是本任务的第二块真实工作量。

---

## 6. 能力判定方案（问题 5）

### 6.1 需要判的五件事 + 现有可判据

| 判据 | 现在的可判方式 | 证据 |
|---|---|---|
| ① 是否 Player（Editor 不走代理） | `Application.isEditor` / `#if UNITY_EDITOR`（工程既有写法） | `View/Pipeline/RenderPipelineSelector.cs:4,99`、`View/Editor/*.cs:1`、`Data/Addressables/AddressablesRuntimeAdapter.cs:123,142` |
| ② 是否 D3D12 | Unity 侧 `SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12`（**工程里目前 0 处使用**，全 grep 空）／原生侧已有权威判定 `GateAllowsFrameGen`（QI 试 D3D12Device/D3D12CommandQueue） | 原生：`zfg_probe_hook.cpp:532-548, 552-565`；Unity 侧：需新写 |
| ③ dll 是否在 | `DllImport` 首调是否抛 `DllNotFoundException`（Editor 里则是 `EntryPointNotFoundException`，见 §4.3） | `Bindings/ZrPlugin.cs:37-41` 是既有范式（catch DllNotFound → 换成人话错误） |
| ④ hook 是否真的装上 / 代理链是否建立 | **`ZFG_FgGetStats` 返回 1**（= `g_fg != nullptr`，`zfg_probe_wrap.cpp:969`）。这是唯一"真的在插帧链上"的信号 | 不要用 `ZFG_ProxyReady`（`zfg_dxgi_proxy.cpp:471-474`，`ZFG_HOOK` 未设时也返回 1） |
| ⑤ 本机 GPU/驱动是否接受 FG | 原生 `FrameGenerator::QuerySupport`（`zfg_probe_wrap.cpp:782-790` → `fg_ffx.h:164`）**目前未导出** → C# 看不到。只能靠 ④ 的返回值间接推断"已建链即被接受" | 见 §2.4 缺口 C |

### 6.2 现成的"能力查询 + 标签后缀"模式（可直接照抄）

- **超分**：`UpscalerSettingOptions.BuildOptionLabels(IUpscalerSettings)`（`:80-91`）—— 拿不到能力（服务未注册 / `Available=false`）时**全部厂商项都加后缀**，最后一个参数 `supported` 为 false 就加 `"（本机不支持）"`（`MarkIfUnsupported:113-116`，常量在 `:46`）。原则写在类注释里（`:16-18`）："不许让用户选一个必然失败的项还无提示"。
- **DRS**：`FrameGovernorSettingOptions.BuildDrsLabel(bool)`（`:100-109`）—— 非 URP 时加 `"（仅降档）"`（`:69`）；依据 `RenderScaleSupported()`（`:112-115`，查 `GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset`）。
- **标签长度约束**：`SettingsRowFactory.LabelWidth = 340f`（`:29`），字号 30（`FrameGovernorSettingsTests.cs:162`），并有测试守门（`:137-153` `AssertLabelFits` + `:145-153` 的 `Assert.LessOrEqual(width, LabelWidth)`）。
- **没有灰显机制**：`SettingDefinition` 没有 `Enabled/IsInteractable` 字段（全文件只有 `RequiresRestart`，`:66`），`SettingsRowFactory` 只在 Binding 行上按可用性置灰（`:407,413,423,434,481`）。→ **门控只能靠"标签后缀 + Note 行 + apply 里拒绝并告警"**，与工程既有裁决一致（`FrameGovernorSettingOptions.cs:56-62` 注释："照超分行的'标签后缀'风格，不新造灰显机制"）。

### 6.3 建议的 FG 能力判定分层（供简报引用）

```
IsAvailable = Application.isEditor == false
           && SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12
           && (DllImport ZFG_FgGetStats 成功且返回 1)     // 代理链已建 = hook 已装 + GPU 被接受
```
三段中任一段为 false → 标签追加后缀（建议 `"（需 DXGI 代理 或 仅 Player）"` 一类，长度受 340px 约束，见 §6.2），apply 里只 `ZLog.Warn` 不抛（照 `FrameGovernorSettingOptions.ApplyDrs:121-132`）。

---

## 7. 设置行落点与受影响测试（问题 6）

### 7.1 加一行 `video.frameGeneration` 要动的文件

| # | 文件 | 改动 | 参照行 |
|---|---|---|---|
| 1 | `Assets/ZFrame/View/Settings/FrameGenerationSettingOptions.cs`（**新建**） | `SettingId = "video.frameGeneration"`、`Toggle(...)`、`BuildLabel()`（能力后缀）、`ApplyEnabled(bool)` | `FrameGovernorSettingOptions.cs:29-32, 100-150`；`UpscalerSettingOptions.cs:69-116` |
| 2 | `Assets/ZFrame/View/Settings/SettingsRegistry.cs` | `BuildVideoPage` 里**追加 1 行** `SettingDefinition.Toggle(FrameGenerationSettingOptions.SettingId, FrameGenerationSettingOptions.BuildLabel(), false, apply: v => ApplyEnabled((bool)v))` | 170-183（两行的位置/写法）；行序注释 167-169 |
| 3 | `Assets/ZFrame/Core/FrameGeneration/IFrameGenerationSettings.cs`（新建） | 服务接口（View 经 `Services` 用） | `Core/Upscaler/IUpscalerSettings.cs:12-34` |
| 4 | `Assets/ZRender/Bindings/*.cs`（新建或并入 `ZrNative.cs`） | `DllImport("dxgi.dll", EntryPoint="ZFG_FgSetEnabled"/"ZFG_FgGetStats", Cdecl)` + `ZfgFgStats` 的 `StructLayout` 镜像 | `ZrNative.cs:12-52` |
| 5 | `Assets/ZRender/Runtime/ZrFrameGenerationSettings.cs`（新建） | 实现 + 降级 + `BeforeSceneLoad` 自注册 | `ZrUpscalerSettings.cs:56-77, 179-194` |
| 6 | （若走"开机应用"）`SettingsBootApplier.cs` | **不用改**——`ApplyFromDisk` 走 `SettingsModel.Load` → 重放所有行（`:44-56`）；只要第 2 步的 `apply` 非 null，冷启动自动生效 | 同左 |
| 7 | （若需 Player 部署）`Tools/build_native.ps1` 或新增 `[PostProcessBuild]` | 目前完全缺失（§5.2/5.3） | `build_native.ps1:112-124` |

**注意 `SettingsModel` 的重放顺序**：`Load` 是按 **settings.json 的键顺序**（`:252`）遍历，而 `Save` 按 `AllSettings` 顺序写（`:150`）。注册表注释（`SettingsRegistry.cs:167-169`）说"Load() 按定义顺序重放"——只在"文件由本工程 Save 写出"时成立；手改过的文件顺序可能不同。FG 行对顺序不敏感（不依赖其它行），但**如果要"DRS 开时先关 FG"之类的联动，这个前提不可靠**。

### 7.2 行数 / 结构硬编码的测试（会红）

| 文件:行 | 硬编码内容 | 影响 |
|---|---|---|
| `Assets/ZFrame/Tests/Debug/SettingsRegistryTests.cs:78-109` | 方法名 `VideoPage_HasEightDefinitionsWithExpectedTypes`；类型数组 8 项（82-94）；id 数组 8 项（96-102）；`Settings[5]`/`Settings[6]` 的下标（106-108） | **必红**：要改成 9 项 + 名字 + 下标后移（若新行插在 shadowDistance 之前，`Settings[6]` 会变） |
| `Assets/ZFrame/Tests/Debug/SettingsPanelSmokeTests.cs:101` | `Assert.AreEqual(8, view.RowCount, "video page row count ...")` | **必红** → 9 |
| `Assets/ZFrame/Tests/Debug/SettingsPanelSmokeTests.cs:141` | `SelectPage("video")` → `RowCount == 8` | **必红** → 9 |
| `Assets/ZFrame/Tests/Debug/SettingsPanelSmokeTests.cs:160` | 扩展注册表 `RowCount == 9`（8+1） | **必红** → 10 |
| `Assets/ZFrame/Tests/Debug/SettingsPanelSmokeTests.cs:109-120` | 按 `Row_video.dynamicResolution` / `Row_video.frameRateTarget` 取控件 | 不红，但**新行应照此加断言**（否则"只在注册表里、没渲染成控件"的假接线测不出来） |
| `Assets/ZFrame/Tests/Pipeline/FrameGovernorSettingsTests.cs:88-116` | 两行的定义/默认/标签断言 | 不红；是 FG 行测试的模板 |
| `Assets/ZFrame/Tests/Pipeline/FrameGovernorSettingsTests.cs:137-153` | 标签宽度守门（340px） | 不红，但**新标签必须加进这个守门**，否则超长会静默截断 |
| `Assets/ZFrame/Tests/Pipeline/FrameGovernorSettingsTests.cs:267-296, 316-336` | 冷启动 + 持久化 roundtrip 模板 | 不红；FG 需要同款（但**无法断言真生效**，见 §8-R6） |
| `Assets/ZFrame/Tests/Input/MoveRebindTests.cs:116` | 键位行 6 | 与本任务无关 |
| `Assets/ZFrame/Tests/Debug/SettingsRegistryTests.cs:48` | `TotalSettingCount == ids.Count`（自洽断言，不写死数字） | 不红 |

另外 `SettingsRegistry` 构造器有 id 唯一性检查（`:61-67`），新 id 不要和既有冲突。

---

## 8. 风险清单（每条写"会导致什么现象"）

| # | 风险 | 代码依据 | 会导致什么现象 |
|---|---|---|---|
| R1 | **产品侧没有每帧 SubmitInputs** | `zfg_probe_wrap.cpp:897,907`（`want && haveInputs`）；`Assets/` 下 `ZFG_*` P/Invoke = 0 处 | 开关能打开、UI 也显示"已开"，但**一帧都不会被插**；`ZFG_FgGetStats` 里 `enabled` 恒 0、`generatedPresents` 不增长。玩家感知 = "这个开关是假的"。**这是本任务的头号风险，且不是开关本身能修的**——B-R9-06 若不接 C# 的 depth/MV 提交，就只是一块假开关 |
| R2 | `ZFG_FgSetEnabled` 无条件 `return 1` | `zfg_probe_wrap.cpp:972-976` | C# 侧"调用成功"不代表生效（`g_fg==nullptr` 时也返回 1）。若 UI 用返回值当"已生效"，会向玩家谎报状态。**必须用 `ZFG_FgGetStats` 的返回值 + `stats.enabled` 二次确认** |
| R3 | 启动模式硬编码（只降不升） | `zfg_probe_hook.cpp:779,834`（`g_mode == Mode::Fg`） | `ZFG_HOOK=wrap`/未设 启动时，玩家把开关拨到"开"没有反应；关掉再打开也回不来。**必须门控成"仅当以 fg 模式启动时该行才可用"**，否则就是 §7 里"能点不生效的假开关"（`FrameGovernorSettingsTests.cs:96` 的同款判据） |
| R4 | `ZFG_HOOK` 未设时 hook 根本不加载 | `zfg_dxgi_proxy.cpp:216-221` | 默认发布态（玩家双击 exe）**连 `ZFG_FgSetEnabled` 符号都取不到**，proxy 侧返回 0（`:457-461`）。表现：开关永远是灰的/无反馈。修它要改代理的加载策略（§2.4 缺口 B），影响面比"加一行设置"大得多 |
| R5 | 关掉后 FFX 状态是否对齐 | `zfg_probe_wrap.cpp:860`（frameId 每帧 +1，开/关同序列）、`:921`（Configure 每帧都调）、`fg_ffx.cpp:609,618` | **不会失配**——两态共用同一条 frameID 序列和同一份 Configure 字段。真实风险在"关闭瞬间正在 Prepare 的历史帧"：SDK 内部可能还有 1 帧待合成（`onlyPresentGenerated=false`，`fg_ffx.cpp:613`），玩家可能看到**关掉后仍有 1 帧插帧**。属可接受，但需要在验收里写明"不是开关失灵" |
| R6 | 关掉时正在 Present 中途（线程/重入） | `zfg_probe_hook.cpp:757`（`appLevel = isWrapped && !nested`）、`zfg_probe_wrap.cpp:895`（渲染线程内读原子） | **不会重入**：开关只在 `WrapOnFrame`（Present 之前、应用级那一次）被采样；嵌套的 SDK 内部 Present 被 `nested` 排除（`:754,758-760`）。风险只在"改了标志但这一帧已经过了采样点" → 变更延后 1 帧生效 |
| R7 | UI 线程 vs 渲染线程 | `zfg_probe_wrap.cpp:84`（`std::atomic<int>`）、`:974`（store） | 无数据竞争（原子 + 单写单读）。**但 C# 侧 `ZFG_FgSetEnabled` 是从主线程调的**，若同时有 `ZFG_FgSubmitInputs`（渲染线程）在工作，两者无共享状态（`g_inputLock` 只护 `g_inputs`，`:950-954`），安全 |
| R8 | 开关持久化后开机应用 | `SettingsBootApplier.cs:32-36`（`AfterSceneLoad`） vs `zfg_dxgi_proxy.cpp:210-252`（首次 `CreateDXGIFactory*`，远早于场景加载） | **顺序是对的**：hook 安装 + 交换链 wrap 都发生在场景加载之前，`AfterSceneLoad` 的 apply 送达时代理链已在。**但**若 apply 在 Editor 里跑（无代理），会走"仅存储 + 告警"分支（照 `FrameGovernorSettingOptions.ApplyDrs:121-132`），表现是"每次启动都告警一次、永不生效" |
| R9 | Editor 里能点但永远不生效 | `zfg_dxgi_proxy.cpp:1-6`（同目录优先，Editor 的 exe 目录没有我们的 dxgi）；`DllImport("dxgi.dll")` → `EntryPointNotFoundException` | 若不做门控：Editor 里开关可点、点了没反应、日志里可能每帧一条异常 → 正是 `SettingsRegistry.cs:158-160` 明确要避免的"让用户选一个必然失败的项"。**必须门控 + 标签后缀诚实标注**（照 `UpscalerSettingOptions:16-18` 的原则） |
| R10 | `WrapShutdown` 是死代码（无调用点） | `zfg_probe_wrap.cpp:1017-1043`；全仓库 grep 仅头声明与自身 | 进程退出时不做 `Configure(false)`/`DestroyContext`，全靠 OS 回收。**当前不崩（B-R9-04/05 已验），但任何"运行期销毁重建"的想法都缺这条路的地基**——也是方案 B 不可行的旁证 |
| R11 | 关掉后性能不回满 | `fg_ffx.h:192-193`（关闭时"普通 present + 一次内部合成拷贝"）、`fg_ffx.cpp:760-765`（上下文不销毁） | 玩家关掉 FG 后帧率可能仍略低于原生（多一次 GPU 拷贝 + 一层代理）。**验收时必须区分"关掉后不插帧"（已达成）与"关掉后回到原生性能"（未达成）** |
| R12 | 标签长度 | `SettingsRowFactory.cs:29`（340px）、`FrameGovernorSettingsTests.cs:137-153` | 后缀写太长（如"（需 DXGI 代理 + D3D12 + 仅 Player）"）会被折行/截断，**诚实的标注反而看不见**——工程已有测试专门守这条 |

---

## 9. 给主控的裁决点（侦察发现的岔口，不是建议）

1. **B-R9-06 的范围**：只做"开关 + 门控"（＝一个当 `ZFG_HOOK=fg` 启动时能用的关/开按钮），还是连"C# 每帧提交 depth/MV"一起做（否则开关无实际效果，见 R1）？
2. **启动授权**（R3/R4）：是否允许改代理的加载策略，让"默认发布的 Player 也能开 FG"（要动 `zfg_dxgi_proxy.cpp:216-221` + `zfg_probe_hook.cpp:629,779,834`）？不改 → 开关只对 `ZFG_HOOK=fg` 启动的实例有效，玩家侧等于不可用。
3. **部署自动化**（§5）：是否在本任务内补 Player 构建 + 两个 dll 拷贝（目前 0 自动化，全是人工 `cp`）？

## 10. 【待确认】清单

- `zfg_dxgi.def` 里 5 个 `ZFG_*` 未写序号（`:8` 注释"不占序号"），而 `TASK-B-R9-03.md:103` 记为 `@21..@25`。谁对？——不影响功能（按名导入），但影响"导出表逐位对照"类验收。**未验证编译产物的实际序号。**
- Unity 侧 `SystemInfo.graphicsDeviceType` 在 Player 里的实际取值（工程里 0 处使用，我无法静态确认运行时值）。**未验证。**
- `FrameGenerator::QuerySupport` 若导出，其 `FrameGenProbe.providerName` 指针生命周期（`fg_ffx.h:164` 注释"探测后即释放"）→ 若要做 C ABI 必须拷字符串，**未验证具体缓冲策略**。
