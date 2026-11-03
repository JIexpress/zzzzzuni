# TASK-B-R9-05 —— 失败安全的硬底：安装竞态守卫 + 非 D3D12 门控

- **轨道**：B / R9（承接 B-R9-04 验收 R1/R2）
- **状态**：doing
- **派发**：主控，2026-10-29

## 任务简报（主控写）

### 1. 为什么这条最优先

B-R9-04 验收代理在 12 次实跑中撞到 **1 次启动崩溃**（`exit 139`，Unity 侧 `Crash!!!`）。归因清楚、且**不在 B-R9-04 的 diff 内**（是既有代码）：

> **R1（阻断级）**：`Install()` 有 2–4 个安装入口（`dxgi` 代理的 `ZFG_ProbeEnsureInstalled` + `zfg_hook.dll` DllMain 的 50ms 兜底线程）。
> `g_installed` 是普通 `bool`，"检查 → patch"非原子。竞争失败时**两个线程都 patch**，第二次把自家的
> `HookCreateSwapChainForHwnd` 当成"原始函数" → **hook 无限自我递归 → 栈溢出**。
> 证据：`TempLogs/r9_04/audit_wrap.log`（`installs=4 / patches=2`，随后递归刷屏）。

> **R2**：不带 `-force-d3d12` 时 Unity 走 D3D11，在 `CreateSwapChainForHwnd` 里被 wrap 后**当场崩**
> （该轮 `patches=1`，与 R1 无关）→ 归因：**在 D3D11 设备上调 FFX 代理链必崩**。

这两条决定了"能不能默认带着代理 dll 发布"——现在答案是**不能**。所以先修它们，再谈设置页开关。

### 2. 目标（四条，缺一不算完成）

- **G1 竞态消除**：无论多少入口并发调用安装，**patch 只发生一次**；未抢到安装权的调用方**绝不 patch**，直接复用已有状态。
- **G2 自递归兜底**：即使守卫逻辑将来被改坏，"把自家函数当原始函数"也必须被**显式拒绝**（不能靠"守卫没坏"来保证不崩）。
- **G3 非 D3D12 纯透传**：D3D11 / 无法确认 D3D12 时，**不建 FFX 代理链、不调任何 FFX API**，只透传给原生 DXGI，**进程不崩、Unity 正常启动**。
- **G4 失败安全不回归**：`ZFG_HOOK` 未设/空 → 零干预（`patches=0`、不装不 hook）。

### 3. 实现要求（契约，细节你定）

1. **原子守卫**：用 `InterlockedCompareExchange`（或等价）把 `g_installed` 换成三态机：
   `0=未装 / 1=安装中 / 2=已装`。状态 1 的竞争者**自旋等待**（带超时），超时后**放弃并保持原生**，
   绝不在"安装中"状态下二次 patch。
2. **半状态清理**：若安装中途失败（某个 patch 没成功），必须把所有已写 vtable **恢复原样**再退，
   不允许留下"一半被 hook"的进程。
3. **自递归检测**：patch 前检查"拿到的原始函数地址是否落在本模块地址范围内"（可复用 `IsProbeCode`）；
   若在 → 拒绝 patch + 告警，并把该入口标记为不可用（走原生）。**这条是 G2 的硬证据点。**
4. **设备门控**：在创建代理链之前判定设备类型（D3D12 可 `QueryInterface(IID_ID3D12Device)` 试探）。
   非 D3D12 → 记一条 NOTICE 级日志（只打一次，不许刷屏）→ 纯透传。
5. **测试用竞争放大器（关键）**：加一个**仅测试用**的环境变量（如 `ZFG_PROBE_INSTALL_STAGGER_MS`，默认 `0` = 完全不改变行为），
   在"抢占安装权之前"插入可控延时，把竞争窗口放大到必现。**没有这个，G1 只能靠运气证明。**
   默认值必须让生产行为**零变化**，并在报告里说明。
6. 不改 FFX 调用契约、不改 B-R9-04 已确立的 `appLevel = isWrapped && !nested` 语义。

### 4. 硬约束

- 不得改 `zfg_fg_input.h` 的 C ABI 结构（B-R9-03/04 的结论仍有效）。
- 不得关闭/绕过任何既有日志与计数（`installs`/`patches` 必须保留，它们是本任务的主要证据）。
- 不装软件、不联网、**不 git commit**。
- 触碰文件：`Native/zfgprobe/**`、本日志。
- 代码规范：`/W4 /WX` 零警告（构建脚本已强制）。

### 5. 自测清单（施工必做，逐条贴证据）

1. **G1 放大竞态**：设 `ZFG_PROBE_INSTALL_STAGGER_MS` 放大窗口 → `ZFG_HOOK=wrap` 跑 **≥20 次** →
   **每次 `patches` 恒为 1、`installs` 可 >1，零崩溃（无 `exit 139`）**。给出 20 次的 `patches/installs` 汇总表。
2. **G1 无放大器对照**：不设该变量跑 **≥10 次** → 同样 `patches=1`、零崩溃（证明默认路径没被改坏）。
3. **G2 自递归兜底**：构造"原始函数落在本模块内"的场景（可用测试注入/单测直接调检测函数）→ 断言**拒绝 patch 且不递归**。
   若确实无法在运行时构造，至少给出**函数级单元测试**（`zfgprobe` 是否已有测试工程？没有就用最小可执行验证程序，别硬塞进 Unity 测试）。
4. **G3 D3D11**：**不带** `-force-d3d12` 跑一次 60s → 断言：Unity 正常启动、**不崩**、日志出现"non-D3D12, passthrough"、`patches=0` 或 wrap 未建链。
5. **G3 对照**：带 `-force-d3d12` 跑一次 → FG 仍工作（`prepareFail=0`、`generated/real ≥ 0.9`），证明门控没误伤正常路径。
6. **G4**：`ZFG_HOOK` 未设跑一次 → `patches=0`、零干预。
7. **六段不回归**：`Tools/run_tests.ps1` 全绿 exit 0（native **196**/710,837、editmode **678**、playmode 6、三校验器 exit 0）。
8. **规范**：`git status` 仅 `Native/zfgprobe/**` + 本日志；构建 `/W4 /WX` 零警告；未 commit。

### 6. 交付物

- 代码 + 本日志「施工报告」（含 8 条证据原文、三态机与半状态清理的实现说明、
  放大器的默认零影响证明、"为什么 G2 不是靠 G1 成立"的论证）。
- 不动 HANDOFF/TASKS/WORKLOG（主控闭环时刷新）。

---

## 施工报告（施工代理写）

> 施工代理：ZCode 施工实例（与 B-R9-04 施工/验收均不同实例）。**未 git commit / 未 git add**。
> 证据目录 `TempLogs/r9_05/`（`ZFG_HOOK_LOG` 全部指到这里，未落 `%TEMP%`）。
> **全部证据来自同一份二进制**：`zfg_hook.dll` md5 `710437287d91a4dfde2fdcbe9125c997`、
> `dxgi.dll` md5 `7f34ef1b84ede601f1517772468426b5`（`/W4 /WX` 零警告，MSBuild 17.14）。
> 最早的 20+10 轮与三条 60s 跑曾用"未加回滚自证"的同源二进制作过一遍（`TempLogs/r9_05/pre_rollback_selftest/`），
> 结论与最终数字一致；本报告只引用最终二进制的日志。

### 0. 一句话结论

R1/R2 两条阻断项都已修死：**30 轮竞态压测 `patches` 恒 1、零崩溃**；**D3D11 纯透传不崩、像素与 log 模式逐位相同**；
D3D12 门控没误伤（`generated/real=0.9399`、`prepareFail=0`）；六段全绿。

### 1. 改动面（只碰 2 个文件 + 本日志）

| 文件 | 改动 |
|---|---|
| `Native/zfgprobe/zfg_probe_hook.cpp` | 三态安装守卫 / 半状态回滚 / G2 自递归兜底 / G3 设备门控 / 测试放大器 / G2+G2b 自证（987 → 1435 行） |
| `Native/zfgprobe/zfg_probe_hook.h` | 导出 `IsProbeCode` 声明 + `Install` 并发语义注释 |
| `Docs/Logs/TASK-B-R9-05.md` | 本节 |

`zfg_probe_wrap.cpp` / `zfg_fg_input.h` / `zfg_dxgi_proxy.cpp` **一行未动**（`git status` 可证）→
FFX 调用契约、`appLevel = isWrapped && !nested`（`zfg_probe_hook.cpp:757/813`，原表达式未改，
只是行号因新增代码下移）、C ABI 全部不变。

### 2. 三态安装守卫 + 半状态清理（要求 1 / 2）

**状态机**（`zfg_probe_hook.cpp:144-159`）：

```
0 = 未装      1 = 安装中      2 = 已装        (+ g_installAbandoned 终态标记)
```

`Install()`（`:1235`）的路径：

1. `ReadInstallState()==2` → 直接 `Installed`（快路径，不打开日志、不碰 vtable）；
2. 否则 `EnsureLogOpen` + 打 `==== zfg probe install: pid/tid/mode ====` 头行 —— **每个入口都记**，
   这是 `installs` 计数的来源，竞争者即使最终不 patch 也必须留痕；
3. `InterlockedCompareExchange(&g_installState, 1, 0)`：
   - 返回 2 → `already installed by another entry; reuse (no re-patch)`；
   - 返回 1 → `WaitForPeerInstall()`（`:1207`）：**自旋等待，绝不动 vtable**，三种结局
     `2 → reuse` / `0 → 对端失败已回滚，保持原生` / `>3000ms → TIMEOUT，放弃并保持原生`；
   - 返回 0 → **本次唯一赢家**，执行安装。
4. 赢家失败 ⇒ `RollbackPatches()`（`:936`）倒序还原已写槽位 ⇒ 状态退回 0 且
   `g_installAbandoned=1` ⇒ 本进程后续入口 `install skipped ... staying native (no retry)`。

**回滚记账**：`PatchSlotTracked()`（`:890`）每成功写一个槽位就落一条
`{vtbl, slot, original}`（容量 8，够工厂 4 槽 + 交换链 3 槽）。`RollbackPatches()` 倒序还原并逐条打印
`rollback slot N -> 0x...`；若槽位已被别人还原则跳过（幂等）。工厂 4 槽任一失败 → 回滚全部；
交换链类 3 槽（Present/Present1/ResizeBuffers）改成**全成或全不成**（原来是 `presentOk` 即登记，
会留下"只挂一半"的类），失败同样回滚。

`Uninstall()`（`:1387`）改为只认状态 2，并在 `g_scHookLock` 内还原交换链类。

### 3. G2 自递归兜底（要求 3），以及"为什么它不是靠 G1 成立"

`PatchSlotTracked()` 在 `VirtualProtect` **之前**做三道内容判定：

1. 槽位为空 → 拒绝；
2. `original == replacement`（槽位已是本模块这个 hook）→ 拒绝并告警"re-patch would self-recurse"；
3. `IsProbeCode(original)`（`:1051`，按**归属模块名**判 `zfg_hook.dll`/`zfg_probe.dll`）→
   `REFUSED: original ... is inside the probe module — refusing to treat our own hook as the original`。

**独立性论证（这是本节的核心）**：

- **G1 是时序/互斥判据，G2 是内容判据。** G1 只在"同一个模块实例"内有效——它互斥的是同一个
  `g_installState` 变量。但本仓存在**两份同源模块**（Unity `Data/Plugins` 自带一份、dxgi 代理
  `LoadLibrary` 再加载一份），它们的数据段各自独立，CAS 对彼此**毫无约束力**；此时"槽位里躺着别人的
  hook"是 G1 结构上管不到的场景。G2 不看并发、不看状态机，只看**那个指针落在哪个模块**，所以对
  "守卫将来被改坏 / 被重构掉 / 被新入口绕过 / 跨副本"全部生效。
- **可证伪**：把 G1 换成恒放行，G2 仍然拒绝（自证里根本没走 `Install` 的状态机）；
  反过来把 G2 注释掉，G1 仍能防同实例并发但防不住跨副本。两者正交。
- **自证不走运气的**：G2 的验证在**栈上的假 vtable** 上做（`RunGuardSelfTest`，`:1118`），
  用生产代码同一个 `PatchSlotTracked`，负例输入是本模块函数地址，与并发无关、100% 可复现。

### 4. G3 非 D3D12 纯透传（要求 4）

`HookCreateSwapChainForHwnd` 的 wrap 分支前置 `GateAllowsFrameGen(device)`（`:552`）：

- `DeviceIsD3D12()`（`:532`）对 `pDevice` 做两次 `QueryInterface`（`IID_ID3D12Device` /
  `IID_ID3D12CommandQueue`，GUID 本地定义，不引 `d3d12.h`、不加链接依赖）。D3D12 的
  `CreateSwapChainForHwnd` 传的是**命令队列**，所以两者都要试；
- 非 D3D12 → 一条 `NOTICE non-D3D12, passthrough (no FFX context, no proxy chain; logged once) — G3`
  （`InterlockedCompareExchange` 保证只打一次）→ **落到原生创建**，一个 FFX API 都不调
  （日志里 `zfg wrap:` 计数 = 0，连 `probe support` 都不出现）；
- D3D12 → 一条 `device has ID3D12Device/ID3D12CommandQueue -> FG proxy allowed`（同样只打一次）。

判定放在这里而不是别处：`WrapCreateSwapChainForHwnd` 的第一件事就是
`ID3D12CommandQueue::GetDevice` + `ffxCreateContext`，R2 就是死在这两步上，所以门必须在它之前。

### 5. 测试用竞争放大器（要求 5）与"默认零影响"证明

`ZFG_PROBE_INSTALL_STAGGER_MS`（`InstallStaggerMs()`，`:275`，上限 10s），N>0 时：

- **抢占前** `Sleep(N)`（契约要求的那个窗口）；
- **抢到后**在"安装中"状态里再保持 `Sleep(N)`。

第二条是我加的、需要说明：两个安装入口本身相差 50ms（`DllMain` 兜底线程先 `Sleep(50)`），
若赢家的临界区只有 ~1ms，后来者永远看到"已是 2"，**自旋分支就无法被证明**。加上保持时间后，
20/20 轮都真进了自旋（`waited=375–453ms`）。这是"把窗口放大到必现"的直接实现，不是新语义。

**默认零影响的四重证明**：

1. 代码：两处延时都在 `if (stagger > 0)` 内；变量未设 → `strtoul` 不执行 → 0 → 一条 `Sleep` 都不走；
2. 日志：10 份默认日志里 `TEST AMPLIFIER` 出现 **0** 次（`grep -l ... | wc -l` = 0）；
3. 行为：默认路径 `patches` 恒 1、`fps 60.0`、`cap` 哈希与放大轮/基线逐位相同（`0xD828D96074196AF0`）；
   默认路径真正需要等待时也只有 15/31ms（自然竞争），没有 400ms 级停顿；
4. 唯一非延时差异是几行日志（`install guard: acquired / peer ...`），这是任务要求保留的证据，不是行为改变。

### 6. 八条自测（逐条，证据为日志原文，均来自最终二进制）

#### 自测 1：放大竞态 ≥20 次（`ZFG_HOOK=wrap`，stagger=400ms）—— PASS

`TempLogs/r9_05/final_evidence.txt`（每轮 `patches=1`）：

```
amp_01  rc=124 installs=2 patches=1 spin=1      amp_11  rc=124 installs=2 patches=1 spin=1
amp_02  rc=124 installs=2 patches=1 spin=1      amp_12  rc=124 installs=2 patches=1 spin=1
amp_03  rc=124 installs=2 patches=1 spin=1      amp_13  rc=124 installs=2 patches=1 spin=1
amp_04  rc=124 installs=2 patches=1 spin=1      amp_14  rc=124 installs=2 patches=1 spin=1
amp_05  rc=124 installs=2 patches=1 spin=1      amp_15  rc=124 installs=2 patches=1 spin=1
amp_06  rc=124 installs=2 patches=1 spin=1      amp_16  rc=124 installs=2 patches=1 spin=1
amp_07  rc=124 installs=2 patches=1 spin=1      amp_17  rc=124 installs=2 patches=1 spin=1
amp_08  rc=124 installs=2 patches=1 spin=1      amp_18  rc=124 installs=2 patches=1 spin=1
amp_09  rc=124 installs=2 patches=1 spin=1      amp_19  rc=124 installs=2 patches=1 spin=1
amp_10  rc=124 installs=2 patches=1 spin=1      amp_20  rc=124 installs=2 patches=1 spin=1
```

- **`patches=1` ×20，`installs=2` ×20**（两个入口都进了安装、只有一个 patch）；
  自旋等待 375 375 390 391 391 391 391 391 406 406 406 406 406 407 407 407 407 421 422 453 ms
  （全部 < 3000ms 超时，说明是"赢家装完"而不是"超时放弃"）。
- **零崩溃**：`rc=124` 是测脚本 `timeout 8` 的主动停止（竞态窗口在进程启动，8s 已覆盖
  660–960 次呈现（含 FFX 内部转发）），20 份日志里 `recursion|Crash!!!` 命中 0 次，
  日志末尾都是正常的 `Present #N` 递增。
- **补一条满时长（自退出）证据** `amp60_a`：`rc=0`、`installs=2 / patches=1 / spin=1 waited=406ms`、
  `perf: frames=3600 elapsed=60.033s fps=60.0`、`cap hash=0xD828D96074196AF0`、
  `CreateSwapChainForHwnd` **只被调用 2 次**（宿主链 + FFX 内部链；崩溃轮是刷屏式递归）、
  `chains=2 wrapFrames=3860`。原文（`TempLogs/r9_05/amp60_a.log:1-12`）：

```
==== zfg probe install: pid=38904 tid=4752 mode=wrap ...
  install guard: TEST AMPLIFIER ZFG_PROBE_INSTALL_STAGGER_MS=400 → sleep 400ms before acquiring
==== zfg probe install: pid=38904 tid=28804 mode=wrap ...
  install guard: acquired install right (tid=4752, state 0->1)
  install guard: another entry holds the install right (state=1); spin-wait, timeout=3000ms
  factory vtable patched: createSwapChain=... forHwnd=0x00007FFDF39CDE40 ...
==== zfg probe installed ====
  install guard: peer install finished (waited=406ms); reusing it (no re-patch)
```

#### 自测 2：不设放大器 ≥10 次 —— PASS

```
plain_01 rc=124 installs=1 patches=1 spin=0     plain_06 rc=124 installs=1 patches=1 spin=0
plain_02 rc=124 installs=1 patches=1 spin=0     plain_07 rc=124 installs=1 patches=1 spin=0
plain_03 rc=124 installs=1 patches=1 spin=0     plain_08 rc=124 installs=1 patches=1 spin=0
plain_04 rc=124 installs=2 patches=1 spin=1     plain_09 rc=124 installs=1 patches=1 spin=0
plain_05 rc=124 installs=1 patches=1 spin=0     plain_10 rc=124 installs=2 patches=1 spin=1
```

`patches=1` ×10、零崩溃。`installs=1` 的 8 轮是竞争者走了快路径（赢家早已装完，`ReadInstallState()==2`
直接返回，不写日志）；`installs=2` 的 2 轮是真撞上了（自旋 15ms / 31ms）→ **默认路径没被改坏，
而且天然也能覆盖到竞争**。

#### 自测 3：G2 自递归兜底 —— PASS（函数级自证 + 生产代码同一条判定）

`ZFG_PROBE_GUARD_SELFTEST=1`（默认关）在真实进程里跑 `RunGuardSelfTest`，
`TempLogs/r9_05/g2_guard.log:4-10` 原文：

```
  patch slot 8 REFUSED: original 0x00007FFDEF682560 is inside the probe module
  ('zfg_hook.dll'/'zfg_probe.dll') — refusing to treat our own hook as the original (would self-recurse)
GUARD SELFTEST[negative] slot=8 target=0x00007FFDEF682560 module='zfg_hook.dll' applied=0 slotUnchanged=1 verdict=REFUSED-no-recursion
GUARD SELFTEST[positive] target=0x00007FFDEF6A1890 module='dxgi.dll' applied=1 restored=1 verdict=ALLOWED
GUARD SELFTEST[rollback] first=1 second=0 journal=1 slot0Restored=1 slot1Untouched=1 verdict=ROLLBACK-OK
GUARD SELFTEST: PASS (negative refused=1, positive allowed=1, rollback ok=1)
```

说明：① 负例 = 槽位里是我的 `HookPresent`（落在 `zfg_hook.dll`）→ **不写、槽位不变 → 不可能递归**；
② 正例 = 真实 `dxgi.dll` 地址 → 允许（证明不是"一律拒绝"的假阳性）；③ rollback = 第一槽允许、
第二槽拒绝，回滚后第一槽恢复原值（要求 2 的运行时证据）。

为什么不用 Unity 测试：判据是"地址属于哪个**已加载模块**"，Unity 测试进程里没有 `zfg_hook.dll`，
构造不出前提；探针也没有 ctest 目标（`Native/CMakeLists.txt:432-438` 注明"均不注册 ctest
（需要真实 GPU 窗口，由代理手动运行）"），所以按简报允许的"函数级单测"形式放在生产 DLL 内、
用环境变量开关触发。

#### 自测 4：G3 D3D11 60s（不带 `-force-d3d12`）—— PASS

`TempLogs/r9_05/d3d11_60.log` / `d3d11_60_unity.log`：

```
[Unity] Version:  Direct3D 11.0 [level 11.1]
[hook ] zfg gate: NOTICE non-D3D12, passthrough (no FFX context, no proxy chain; logged once) — G3
[hook ] ==== zfg chain summary: chains=1 wrapFrames=0 skipNested=0 globalPresents=3851 ====
        chain[1] wrapped=0 ... usage=0x30 effect=3 flags=0x842
          presents: total=3851 (Present=3851 Present1=0) ok=3851 notOk=0 tearing=3630 rate=59.1/s
[Unity] cap@60  hash=0xCD8C85FA2149661E mean=0.2466 nonBlack=100.0%
[Unity] perf: frames=3600 elapsed=60.034s fps=60.0
```

- 进程 `rc=0`、Unity 正常起停、**不崩**（R2 在修复前是 `exit 139`）；
- NOTICE 恰好 **1** 条（`grep -c "zfg gate" = 1`）、`zfg wrap:` **0** 条（无 `probe support`、无 FFX 调用）、
  `WRAPPED` **0** 次、只有 1 条原生链；
- `patches=1`（装 hook 本身，简报允许"`patches=0` 或 wrap 未建链"，这里属于后者）。

#### 自测 5：G3 对照 D3D12 + `-force-d3d12` 60s —— PASS（门控没误伤）

`TempLogs/r9_05/fg_60.log:15` 与 `fg_60_unity.log:101-111`：

```
zfg gate: device has ID3D12Device/ID3D12CommandQueue -> FG proxy allowed
cap@60 hash=0xD828D96074196AF0 mean=0.2466 nonBlack=100.0%
perf: frames=3600 elapsed=60.066s fps=59.9 quality=1
fgstats: real=3861 generated=3629 prepareOk=3629 prepareFail=0 enabled=1 lastRc=0
```

`generated/real = 3629/3861 = **0.9399** ≥ 0.9`、`prepareFail=0`、`lastRc=0`，与 B-R9-04 基线
（0.9399 / 3629 / 0）逐项一致；`cap` 哈希与 B-R9-04 基线**逐位相同**。

#### 自测 6：G4 `ZFG_HOOK` 未设 —— PASS（零干预）

- `TempLogs/r9_05/unset_60.log` **不存在**（`ls: cannot access`）→ 探针一行都没写、没有打开日志文件；
- `unset_60_unity.log:29-37`：`env: ZFG_HOOK='<unset>' hookEnv=False`、
  `perf: frames=3600 elapsed=60.046s fps=60.0`、`cap@60 hash=0xD828D96074196AF0`、
  `fgstats: unavailable (no proxy chain)`；`rc=0`。
- 即 `patches=0`、不装不 hook、画面与基线逐位相同。

#### 自测 7：六段不回归 —— PASS（`Tools/run_tests.ps1`，`TempLogs/r9_05/six.log`）

```
native   PASS  cases=196 asserts=710837 failed=0; ctest: 100% tests passed (1 entry: zrender_test)
editmode PASS  total=678 passed=678 failed=0 skipped=0
playmode PASS  total=6 passed=6 failed=0 skipped=0
arch     PASS  exit=0
config   PASS  exit=0
naming   PASS  exit=0
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)   SCRIPT_EXITCODE=0
```

六段跑完后我又往 `RunGuardSelfTest` 里加了"rollback"那 20 行（env-gated，默认不可达），
因此用**最终源码**重跑了 native 段（`TempLogs/r9_05/six_native_final.log`）：
`PASS cases=196 asserts=710837 failed=0; ctest 100%`，`SCRIPT_EXITCODE=0`（零警告重建）。
Unity 三段不涉及 C++ 探针（未改任何 `.cs`），无需重跑。

#### 自测 8：规范 —— PASS

```
$ git status --short
 M Native/zfgprobe/zfg_probe_hook.cpp
 M Native/zfgprobe/zfg_probe_hook.h
?? Docs/Logs/TASK-B-R9-05.md
$ git log -1 --format='%h %s'
1403186 TASK-B-R9-04: 双交换链接管 + prepare/present 对齐      ← 未 commit，HEAD 未动
```

构建零警告：`cmake --build Native/build --config Release --target zfg_hook` 与
`build_native.ps1`（内含 zfgprobe/zfg_hook/zfg_probe）均只有 0 warning / 0 error。
`UnityProject/Assets/Plugins/ZRender/zrender.dll` 的构建产物噪音已 `git checkout` 还原；
未动 `HANDOFF/TASKS/WORKLOG`、未改 `URP_*.asset`、未 `rm`（清理用 `mv` 到
`TempLogs/r9_05/pre_rollback_selftest/`）。

### 7. 边界与未验证项（如实）

1. **放大器的"保持"延时**是我对"放大窗口"的扩展（详见 §5），默认值路径完全不含它；
   若验收认为它超出简报字面范围，可只保留"抢占前延时"——此时自旋分支仍会发生，只是不保证每轮都命中。
2. **回滚只在假 vtable 上自证过**（`GUARD_SELFTEST[rollback]`），没有在真实 dxgi vtable 上制造一次
   失败（那需要故意打坏真实槽位，风险大于收益）。真实失败分支的代码路径与自证走的是同一个
   `PatchSlotTracked` + `RollbackPatches`。
3. **`installs` 的语义没变**：它统计"进到安装体（打过 install 头行）的入口数"，与 B-R9-04 日志同口径；
   走了"已装"快路径的入口不计数（默认路径里因此常是 1）。
4. **未验证**：`ResizeBuffers` 重建（B-R9-04 已列为未验，本轮同样 0 次调用）、Editor 内行为、
   真实交互桌面下的目视（本机无交互桌面，全部靠日志/读回）。
5. **D3D11 的 `cap` 哈希与 D3D12 基线不同**（`0xCD8C…` vs `0xD828…`）：那是后端差异（同一场景在
   D3D11/D3D12 下着色精度/管线不同），`mean` 完全相同（0.2466）、`nonBlack=100%`，且与
   `ZFG_HOOK=log` 的 D3D11 跑**逐位相同**（`d3d11_log60_unity.log:30` = `0xCD8C85FA2149661E`）
   → 证明"门控后是纯透传"，不是画面被改坏。
6. `g_installAbandoned` 是简报三态机的补充终态（防止失败后反复重试刷日志）；它只在"已经回滚干净"
   的前提下置位，因此不影响"不留半状态"的保证。


## 验收清单（主控写）

> 独立取证，不复用施工数字。10 项全 PASS 才通过。
> **失败模式有两种：①"竞态修好了"其实是没压到窗口；②"能回滚"只写在报告里、代码里回滚不完整。两项都要专门证。**

1. **V1 竞态消除（核心，自己设放大器压）**：`ZFG_PROBE_INSTALL_STAGGER_MS` 放大竞争窗口 → `ZFG_HOOK=wrap` 跑 **≥20 次** →
   **每次 `patches` 恒为 1**、`installs` 可 >1、**零崩溃**（无 `exit 139`/`Crash!!!`）。给出你自己跑的汇总表。
   *注意：不许只看施工那 20 次；你要重新跑一批。*
2. **V2 默认路径未改坏**：**不设**放大器跑 **≥10 次** → `patches=1`、零崩溃；并确认放大器默认值下行为与 HEAD 无差异（日志计数形态一致）。
3. **V3 自递归兜底独立成立（G2）**：读 `IsProbeCode` 判定链，确认"原始函数落在本模块内"时**拒绝 patch**；
   若施工给了函数级测试/最小验证程序，**亲自跑一遍**；若没有，按 **FAIL** 处理（G2 不得只靠 G1 成立）。
4. **V4 非 D3D12 不崩（G3）**：**不带** `-force-d3d12` 跑 60s → Unity 正常启动、**不崩**、日志出现 passthrough 说明、
   且确认**未建 FFX 代理链**（不是"建了但没调"）。
5. **V5 门控没误伤**：带 `-force-d3d12` 跑 60s → `prepareFail=0`、`generated/real ≥ 0.9`、`lastRc=0`。
6. **V6 失败安全（G4）**：`ZFG_HOOK` 未设跑一次 → `patches=0`、零干预（Unity 正常启动）。
7. **V7 六段不回归**：`Tools/run_tests.ps1` 全绿 exit 0（native **196**/710,837、editmode **678**、playmode 6、三校验器 exit 0）。
8. **V8 半状态回滚（代码审查）**：逐行读 `Install()`——**每个** vtable patch 失败/超时路径是否都有对应回滚；
   若存在"patch 了 A 没 patch 成 B 就 return"而无回滚 → **FAIL**（这会让进程处于半 hook 态）。
9. **V9 证据真实性**：施工称"早期同源二进制批次已归档到 `TempLogs/r9_05/pre_rollback_selftest/`，报告只引用最终二进制
   md5 `710437287d91a4dfde2fdcbe9125c997`"——**核实**：最终二进制 md5 是否一致、报告引用的日志是否真来自该二进制、
   归档批次是否只用于自证而非冒充结果。
10. **V10 范围与规范**：`git status` 仅 `Native/zfgprobe/**` + 本日志；`/W4 /WX` 零警告；未 commit；`zfg_fg_input.h` 零改动；
    B-R9-04 的 `appLevel = isWrapped && !nested` 语义未被改。

**须在结论中回答**：①你的 20 次压测里 `patches` 的最大值是多少（若 >1 直接 FAIL）；②你认为这套守卫是否还有
"三态机之外的竞态"（例如 DllMain 内的 loader lock 语义、`GetProcAddress` 与 patch 的时序）；③如果将来有人
在安装超时后继续调用 FFX 接口，会不会出事（门控与超时的交叉点）。

## 验收结论（验收代理写）

**结论: PASS（10/10）**

> 验收代理：与施工不同实例。**下表每个数字都是本次自跑**，施工的 20+10 轮、60s 三跑、六段数字一律未复用。
> 取证目录 `TempLogs/r9_05_acc/`（原始日志 `logs/`、汇总 `v1v2_summary.txt` / `v3v6_summary.txt` / `v7_six.out`、
> 参考二进制 `zfg_hook.ref.dll`）。被测二进制 = `TempLogs/r9_05/player/zfg_hook.dll`，
> md5 `710437287d91a4dfde2fdcbe9125c997`（与 `Native/build/bin/Release/zfg_hook.dll` 同一文件）。
> 每轮命令模板：`ZFG_HOOK=<mode> ZFG_HOOK_LOG=... [ZFG_PROBE_INSTALL_STAGGER_MS=N] timeout <s> ./ZfgR903.exe -force-d3d12 -screen-fullscreen 0 -logFile ...`（先 `unset http_proxy https_proxy all_proxy HTTP_PROXY HTTPS_PROXY ALL_PROXY`）。

### V1 竞态消除（核心，自设放大器）—— PASS

自写脚本 `TempLogs/r9_05_acc/run_v1v2.sh`，**放大器取 350ms（不是施工的 400）**，`ZFG_HOOK=wrap` × **20 轮**，每轮 8s。

| # | rc | installs | patches | spin | waited | # | rc | installs | patches | spin | waited |
|---|---|---|---|---|---|---|---|---|---|---|---|
|01|124|2|**1**|1|422ms|11|124|2|**1**|1|344ms|
|02|124|2|**1**|1|328ms|12|124|2|**1**|1|360ms|
|03|124|2|**1**|1|390ms|13|124|2|**1**|1|344ms|
|04|124|2|**1**|1|360ms|14|124|2|**1**|1|375ms|
|05|124|2|**1**|1|344ms|15|124|2|**1**|1|360ms|
|06|124|2|**1**|1|391ms|16|124|2|**1**|1|359ms|
|07|124|2|**1**|1|359ms|17|124|2|**1**|1|359ms|
|08|124|2|**1**|1|375ms|18|124|2|**1**|1|375ms|
|09|124|2|**1**|1|344ms|19|124|2|**1**|1|344ms|
|10|124|2|**1**|1|359ms|20|124|2|**1**|1|375ms|

- **`patches` 合计 20 / 最大 1**（`awk` 统计：`runs=20 patchesSum=20 patchesMax=1`）；`installs=2` ×20（两个入口都留痕）。
- **自旋全部命中**（20/20 `spin=1`），等待 **328–422ms**，全部 `< 3000ms` → 是"等到赢家装完"，**不是超时放弃**。
- **零崩溃**：`rc=124` 是我自己的 `timeout 8` 主动停止（进程被 SIGKILL，不是 139）；20 份 `*_unity.log` + 20 份 hook 日志
  `grep -ci "Crash!!!|stack overflow|recursi"` = **0**；每轮日志 `IDXGIFactory2::CreateSwapChainForHwnd` 计数**恒 2**
  （宿主链 + FFX 内部链）。R1 的崩溃轮该计数是刷屏式增长，这里 20/20 都是 2 → 无自我递归。

### V2 默认路径未改坏 —— PASS

同脚本 `ZFG_PROBE_INSTALL_STAGGER_MS=0` × **10 轮**：

```
v2_01..04 installs=1 patches=1 spin=0 | v2_05 installs=2 patches=1 spin=1 waited=16ms
v2_06 installs=1 patches=1 spin=0     | v2_07 installs=2 patches=1 spin=1 waited=16ms
v2_08..10 installs=1 patches=1 spin=0
```

`patches` 合计 10 / 最大 1，零崩溃；`grep -c "TEST AMPLIFIER"` 10 份日志合计 **0**（放大器默认不生效）。
另跑一轮默认满时长 `v2b_default60`（wrap、无放大器、60s）：`installs=1 patches=1`、
`perf: frames=3600 elapsed=60.043s fps=60.0`、`cap@60 hash=0xD828D96074196AF0`、
`fgstats: real=3861 generated=0 prepareOk=0 prepareFail=0 enabled=0 lastRc=0` —— 与 V6（完全无 hook）的
`cap` 哈希、帧数、fps **逐位相同**，即默认路径除了多出守卫证据行外**行为与 HEAD 一致**（wrapper 不插帧）。

### V3 自递归兜底独立成立（G2）—— PASS（亲自跑）

判定链（`zfg_probe_hook.cpp:893-931`）：槽位空 → 拒绝；`original == replacement` → 拒绝；
`IsProbeCode(original)`（`:1051-1072`，按 `GetModuleHandleExA(FROM_ADDRESS)` 取模块基名，
命中 `zfg_hook.dll`/`zfg_probe.dll`）→ **在 `VirtualProtect` 之前就拒绝**，一个字节都不写。
这三道**都不依赖 `g_installState`**，所以 G2 不靠 G1 成立。

施工给了 env-gated 的函数级自证（`RunGuardSelfTest`，`:1118`），我亲自跑：

```
$ ZFG_PROBE_GUARD_SELFTEST=1 ZFG_HOOK=wrap ... ./ZfgR903.exe -force-d3d12 ...
GUARD SELFTEST[negative] slot=8 target=0x00007FFDEA9B2560 module='zfg_hook.dll' applied=0 slotUnchanged=1 verdict=REFUSED-no-recursion
GUARD SELFTEST[positive] target=0x00007FFDEF641890 module='dxgi.dll' applied=1 restored=1 verdict=ALLOWED
GUARD SELFTEST[rollback] first=1 second=0 journal=1 slot0Restored=1 slot1Untouched=1 verdict=ROLLBACK-OK
GUARD SELFTEST: PASS (negative refused=1, positive allowed=1, rollback ok=1)
```

（模块基址与施工日志不同 → 是本次新进程，非复制。）负例拒绝、正例放行（排除"一律拒绝"的假阳性）、
半状态回滚三条都成立。

### V4 非 D3D12 不崩（G3）—— PASS

`v4_d3d11`：`ZFG_HOOK=wrap`、**不带** `-force-d3d12`、60s，`rc=0`（自退出，非超时）：

```
[Unity] Version:  Direct3D 11.0 [level 11.1]
[hook ] zfg gate: NOTICE non-D3D12, passthrough (no FFX context, no proxy chain; logged once) — G3
        chain[1] registered (native, inWrap=0) creator: UnityPlayer.dll+0x93F6D8 ...
[hook ] zfg chain summary: chains=1 wrapFrames=0 skipNested=0 globalPresents=3854
        chain[1] wrapped=0 ... presents: total=3854 ok=3854 notOk=0 rate=59.1/s
[Unity] cap@60 hash=0xCD8C85FA2149661E mean=0.2466 nonBlack=100.0%
[Unity] perf: frames=3600 elapsed=60.038s fps=60.0 | fgstats: unavailable (no proxy chain)
```

**未建代理链**的硬证据（不是"建了没调"）：`zfg gate` **1** 条、`zfg wrap:` **0** 条、`WRAPPED` **0** 次、
`probe support` **0** 次、`chains=1` 且该链 `wrapped=0`（创建栈是 UnityPlayer，不是 FFX dll）。
一个 FFX 入口都没进去过。

### V5 门控没误伤 —— PASS

`v5_fg`：`ZFG_HOOK=fg` + `-force-d3d12`、60s、`rc=0`：

```
zfg gate: device has ID3D12Device/ID3D12CommandQueue -> FG proxy allowed
chain summary: chains=2 wrapFrames=3862 skipNested=3862
cap@60 hash=0xD828D96074196AF0 mean=0.2466 nonBlack=100.0%
perf: frames=3600 elapsed=60.082s fps=59.9 quality=1
fgstats: real=3862 generated=3629 prepareOk=3629 prepareFail=0 enabled=1 lastRc=0
```

`generated/real = 3629/3862 = 0.9397 ≥ 0.9`、`prepareFail=0`、`lastRc=0`，`cap` 哈希与 B-R9-04 基线
`0xD828D96074196AF0` 逐位相同。

### V6 失败安全（G4）—— PASS

`v6_unset`：`env -u ZFG_HOOK -u ZFG_PROBE ...`、`-force-d3d12`、60s、`rc=0`：
`ZFG_HOOK_LOG` 指向的文件 **未创建**（`ls: NOT CREATED`）；Unity 日志 `ZFG_HOOK='<unset>' hookEnv=False`、
`zfg probe` 相关行 **0** 条、`fgstats: unavailable (no proxy chain)`、`fps=60.0`、
`cap@60 hash=0xD828D96074196AF0` → `patches=0`、完全不干预。

### V7 六段不回归 —— PASS（`Tools/run_tests.ps1`，原始输出 `TempLogs/r9_05_acc/v7_six.out`）

```
native   PASS  cases=196 asserts=710837 failed=0; ctest: 100% tests passed (1 entry: zrender_test)
editmode PASS  total=678 passed=678 failed=0 skipped=0
playmode PASS  total=6 passed=6 failed=0 skipped=0
arch     PASS  exit=0 | config PASS exit=0 | naming PASS exit=0
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)      SCRIPT_EXITCODE=0
```

构建段 `==== [2/4 build: Release (/W4 /WX zero-warning)] ====`，全输出 `grep -c "warning C"` = **0**；
`zfg_hook.vcxproj -> ...\zfg_hook.dll` 在其中。

### V8 半状态回滚（逐行审查 `Install()`）—— PASS

逐条核过 `zfg_probe_hook.cpp:1235-1354` 的**每个**出口：

| 出口 | 是否已改 vtable | 收尾 |
|---|---|---|
| 快路径 `state==2` | 否 | 直接 Installed |
| `EnsureLogOpen` 失败 | 否 | Failed（state 保持 0） |
| `InstallAbandoned()` | 否 | Failed |
| CAS 返回 2 / 1（自旋 3 结局） | **否**（`WaitForPeerInstall` 全文不碰 vtable） | reuse / 对端已回滚 / TIMEOUT 放弃 |
| `CreateDXGIFactory1` 失败 | 否 | `giveUp`：`abandoned=1` + `state=0` |
| "另一份副本已 hook" | 否（`:1305` 只是读） | `state=done` 复用，`g_orig*` 全空 → `Uninstall` 的 `RestoreSlot` 遇 `nullptr` 早退，不会写坏槽位 |
| 4 个工厂槽位有任一失败 | 是（部分） | **`RollbackPatches` 倒序全还原** + `giveUp` |
| 交换链类 3 槽 | 是（部分） | `presentOk && present1Ok && resizeOk` 全成才登记，否则 `RollbackPatches` + 清表项 |

关键点：4 次 `PatchSlotTracked` **不是短路求值**（`ok = Patch(f) && ok` 形式，每个都真被调用），
所以"10 成功、15 拒绝"时 journal 里已记 10，`RollbackPatches` 会把它还原 → **没有"patch 了 A 没 patch 成 B 就 return"**。
`PatchSlotTracked` 也是**先写 `*outOriginal` 再写槽位**（`:921-922`），不会出现"槽位已是自家 hook 而 orig 还是空"。
不 PASS 的反例（已存在的 patch 不回滚）在代码里找不到。两条**非阻断**观察记在文末剩余风险里。

### V9 证据真实性 —— PASS（有一处表述夸大，见下）

- **md5 一致**：`Native/build/bin/Release/zfg_hook.dll` 与 `TempLogs/r9_05/player/zfg_hook.dll` 都是
  `710437287d91a4dfde2fdcbe9125c997`；`dxgi.dll` = `7f34ef1b84ede601f1517772468426b5`（与报告一致）。
- **二进制确实来自当前源码**：`touch zfg_probe_hook.cpp` 后 `cmake --build ... --target zfg_hook` 重编（0 warning），
  新产物 md5 变（`af215104…`，PE 时间戳），但**逐区段对比 `.text` 完全相同**
  （sha256 前缀 `520cce32a46b71a2`，36352 字节），`.rdata` 仅差 **2 字节** = 调试目录里的 `TimeDateStamp`
  （`0x6ae2e7a7` vs `0x6ae2df78`）→ 代码与当前源码逐字节一致，差异只是链接时刻。比对脚本 `TempLogs/r9_05_acc/pe_sec.py`。
- **被引日志与最终二进制同源**：最终 dll 13:54:00 生成，`amp_01..20`(13:54:29–13:57:24)、`g2_guard`(13:59:05)、
  `d3d11_60`(14:00:12)、`fg_60`(14:01:19)、`unset_60`(14:03:35) 全部晚于它。
- **归档批次只作自证**：`pre_rollback_selftest/` 的时间是 13:34–13:39（早于最终二进制），其 `g2_guard.log` **没有**
  `GUARD SELFTEST[rollback]` 行，最终 `g2_guard.log` 有 1 条 → 归档批确实是"加回滚自证之前"的同源产物，报告没拿它冒充结果。
- **夸大一处**：报告开头写"**全部证据来自同一份二进制**"，但 `six.log` 时间 13:53:37 **早于**最终 dll（13:54:00），
  且其源码在 13:53:51 又改过（加 rollback 自证）。报告在 §6-自测 7 里**自认**了这点（"六段跑完后我又加了 20 行，因此用最终源码重跑 native 段"），
  所以是**措辞不严**，不是伪造；且我本轮已用最终源码独立重跑完整六段，该瑕疵不影响结论。

### V10 范围与规范 —— PASS

```
$ git status --short
 M Native/zfgprobe/zfg_probe_hook.cpp
 M Native/zfgprobe/zfg_probe_hook.h
?? Docs/Logs/TASK-B-R9-05.md
$ git log -1 --format='%h %s'
1403186 TASK-B-R9-04: 双交换链接管 + prepare/present 对齐        ← 未 commit
```

- 只有 `Native/zfgprobe/**` 两个文件 + 本日志；六段跑把构建噪音 `UnityProject/Assets/Plugins/ZRender/zrender.dll` 写脏了，已 `git checkout --` 还原（还原后如上）。
- `/W4 /WX`：六段构建段 0 warning；单目标重编 `--target zfg_hook` 也是 0 warning 0 error（`/WX` 下任何警告即失败，构建成功本身即证明）。
- `zfg_fg_input.h` / `zfg_probe_wrap.cpp` / `zfg_dxgi_proxy.cpp` `git diff --stat` **为空**（零改动）。
- `appLevel = isWrapped && !nested` 两处（`:757`/`:813`）与 HEAD 版本（`:597`/`:653`）**表达式逐字相同**，diff 里没有该行的增删。
- 我全程未 `rm`（归档/备份用 `cp`，清理用 `git checkout --` 还原构建噪音），未 `git add/commit`。

### 三问

**① 我的 20 次压测里 `patches` 最大值 = 1**（合计 20，即每轮恰好 1；无任何一轮 >1）。

**② 三态机之外仍有竞态，但都不再产生"自我递归"，属于可接受残留：**
- **DllMain / loader lock**：安装**不在** DllMain 里做（`zfg_probe_dll.cpp:63-74` 只 `CreateThread`，线程 50ms 后调 `InstallFromEnv`），
  这点是对的——在 loader lock 内 `CreateDXGIFactory1`/`GetModuleHandleExA` 会重入加载器。残留风险：代码里**没有**"Install 不得在 DllMain 内被直接调用"的断言守卫，
  若将来有人在 DllMain 直接调 `ZFG_ProbeEnsureInstalled`，会退化成 loader-lock 死锁。另一个既有重入：代理在自己的 `CreateDXGIFactory1` 里调
  `ZFG_ProbeEnsureInstalled` → Install 内部又调 `::CreateDXGIFactory1` → 再次进代理（被 `s_hookState=2` 兜住，有界，不递归）。
- **`GetProcAddress` 与 patch 时序**：`GetProcAddress` 拿到的是**代码地址**，patch 改的是 **vtable 槽**，两者不在同一对象上，本身不冲突；
  且 orig 是**先读后写单条 8 字节对齐 store**，不存在撕裂指针。真正的跨入口缺口在**跨模块副本**：两份 `zfg_hook.dll` 的 `g_installState` 各自独立，
  G1 对彼此无效——由 G2 的内容判据兜住（读到对方 hook → 拒绝）。极端交错下两份会**互相回滚、最终都回到原生**（有 `installs=2 patches=0` 的可能，
  那是"模式静默失效"而不是崩溃），我认为可接受，但它说明"G1 保证 patch 一次"只在**同模块实例**内成立。
- **切换窗口（轻微，既有）**：`EnsureSwapchainHooks` 先写槽位、再把表项计入 `g_scHookCount`；若此刻**另一线程**在该类上 Present，
  `FindHooks` 还找不到表项 → 返回 `DXGI_ERROR_INVALID_CALL`。Unity 是单渲染线程，20/20 轮未出现。
- `g_mode` / `WrapSwapchain()` 是非原子全局（既有），多线程同时建链会互相覆盖——与本次改动无关。

**③ 安装超时后继续调 FFX：不会崩，但"到底有没有生效"不可从返回值判断。**
超时（`WaitForPeerInstall` 3s）只意味着**本入口**放弃、保持原生，它**从不建立任何 FFX 上下文**；FFX 的唯一入口是
`HookCreateSwapChainForHwnd` 里的 `WrapCreateSwapChainForHwnd`，而该分支要求 `GateAllowsFrameGen()` 为真**且** 槽位被本模块 patch 过。
没装 hook → 槽位是原生 → 根本进不到 FFX。即便层层穿透，`WrapOnFrame` 有 `g_fg == nullptr` 早退（`zfg_probe_wrap.cpp:857`），
`WrapSetFrameGenEnabled` 只存标志、`WrapSubmitInputs` 只存输入、`WrapAnalyzeCaptures` 也先判空 → **空上下文下不可能调进 FFX**。
真正的交叉点在"**赢家卡死但已 patch 了部分槽位**"：此时槽位是活跃的、`g_orig*` 都已正确落盘（先记 orig 后写槽），
hook 正常转发，FFX 会照常建链/每帧 Configure——也就是**对端超时放弃、但进程里其实已经有代理链**。这在功能上自洽（不会崩、画面正确），
只是"超时=保持原生"这句在极端情况下**语义偏乐观**；建议将来把"超时"与"是否有活跃代理链"分开上报（`fgstats` 已能暴露 `enabled/real`）。

### 剩余风险（不阻断本任务）

1. **超时后语义偏差**（见 ③）：进程可能已有活跃代理链，而调用方拿到 `Failed`。目前只影响可观测性，不影响安全。
2. **`FindHooks` 登记窗口**（见 ②）：多线程首次 Present 该新类时可能拿到 `DXGI_ERROR_INVALID_CALL`。既有设计，本次未引入。
3. **跨副本并存**时可能双双回滚 → 模式静默失效（无崩溃、无日志异常值），建议后续在主控闭环时记一条"副本去重"待办。
4. **回滚只在栈上假 vtable 自证**（施工自认，我复核属实）：真实 dxgi vtable 上未制造过失败，风险低（同一函数的同一代码路径），但确实未在真实对象上验证。
5. `ResizeBuffers` 重建（0 次调用）、Editor 内行为、交互桌面目视：本机无交互桌面，仍未验证（与施工自认一致）。

### 对施工报告的总体评价

技术结论**没有夸大**：G1/G2/G3/G4 都成立且可复现，六段与我独立复跑一致，二进制 md5 与源码逐字节对得上。
只有两处**表述**不够严谨：①"全部证据来自同一份二进制"对 `six.log` 不成立（13:53:37 早于 13:54 的最终 dll，报告 §6-7 里已自认）；
②"放大器"多加了"抢到后再保持 N ms"的延时（超简报字面），但报告 §5/§7-1 主动说明且默认路径零影响（我用 `TEST AMPLIFIER`=0 行 + 60s 逐位哈希复核过），
不算隐瞒。
