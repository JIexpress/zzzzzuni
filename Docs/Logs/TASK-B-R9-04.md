# TASK-B-R9-04 —— 双交换链接管与 prepare/present 对齐（FG 阶段 C 收口）

> **背景**：B-R9-03 阶段 C 未达（`generated=2` vs `real=562`）。验收代理已定位根因层：
> 同 hwnd 上 Unity 创建了**两条交换链**，`Present1` 落在被 wrap 的 #1、`Present` 落在未 wrap 的 #2，
> 且 `WrapOnFrame` 的"每次 Present 消耗一份输入"逻辑被两条链同时呈现打破 → FFX 的 1:1 契约失效。
> **本任务：把这条诊断坐实，并把阶段 C 打到达标或给出明确的不可行结论。**

## 任务简报（主控写）

### 已知事实（直接复用，勿重复摸索）

**B-R9-03 验收实测（`TempLogs/r9_03_audit/audit_fgmode.log`）**：
```
[#1] CreateSwapChainForHwnd desc usage=0x30 effect=3 flags=0x842 → WRAPPED  sc=0x...BD5A0
[#2] CreateSwapChainForHwnd desc usage=0x20 effect=4 flags=0x840 → 原生     sc=0x...F0990
Present1 落在 BD5A0（#1）、Present 落在 F0990（#2），5s 内全局呈现计数到 #1680
fgstats: real=562 generated=2 prepareOk=328 prepareFail=0 enabled=0
```
- 输入供给正常：`depth=1280x720 R32_SFloat ptrD≠0`、`MV R16G16_SFloat ptrM≠0`，`prepareFail=0`
- `prepareOk≈328`（≈Unity 帧数 330），但 `real=562`（≈1.7×）→ **多数 Present 没有配对 Prepare**
- 阶段 A/B 已过硬：代理透明（导出 20/20）、wrap 有像素读回、失败安全回退
- F2 已修（`jitterCancellation=false`）

### 阶段 1：坐实"第二条链是谁"（先取证，别急着改）

1. 增强 `zfg_probe_hook.cpp` 的交换链创建日志：记录**每次** `CreateSwapChain`/`ForHwnd`/`ForComposition` 的
   `hwnd`、`desc.Width/Height/Format/BufferCount/SwapEffect/Flags`、`usage`（从 `DXGI_SWAP_CHAIN_DESC1` 取）、
   `outSwapChain` 指针、**是否 wrap**、调用序号
2. 给**每条链**独立计 Present 次数与 Present 类型（`Present` vs `Present1`）、时间戳序列；退出时 dump 汇总表
3. 判定 #2（`usage=0x20` = SHADER_INPUT、`effect=4` = FLIP_DISCARD、`flags=0x840`）的来源：
   - 是 Unity 自己的"第二窗口/副链"？还是 AMD AGS / FFX loader / 探针自身创建？
   - **可证伪手段**：日志加调用栈（`CaptureStackBackTrace` + `SymFromAddr` 或模块基址归属），
     或临时屏蔽各候选源（AGS 初始化、FFX loader）看 #2 是否消失
4. **判据**：能明确写出"#2 由 X 创建、用途 Y、是否呈现到屏幕"

### 阶段 2：修复策略（二选一，按阶段 1 结论定，报告写清为什么选）

**策略 A：两条都 wrap，但 FG 只挂在"真正呈现到屏幕"的那条链上**
- 判定"真正呈现"的依据：Present 计数与 Unity 帧数匹配度 + 是否有可见 hwnd + 是否 flip 模型
- 另一条链纯透传（不消耗 FG 输入、不调 Prepare）

**策略 B：只 wrap 主链，副链透传**
- 若 #2 证实不呈现到屏幕（如离屏/辅助用途），直接透传即可

**关键修点（两策略共用）**：
- `zfg_probe_wrap.cpp` 的 `WrapOnFrame`（约 L554-575）"每次 Present 消耗一份输入"逻辑必须改为
  **按链绑定输入队列**，或只在 FG 链的 Present 上消耗
- 保证 FFX 契约：`frameID` 严格 +1、`Prepare` 与 `Configure` 对齐、**1 Prepare 对 1 Present（FG 链）**

### 阶段 3：验证（全部要给数字）

1. `ZFG_HOOK=fg` 跑 60s：`fgstats` 要求 `generated/real ≥ 0.9`、`prepareFail=0`、`lastRc=0`
2. 呈现比：全局 Present 计数 / Unity 帧数 ≈ **2.0×**（当前 1.7×），并给出两链各自的计数
3. 像素正确性：**两条链都要读回**——FG 链的插帧帧非黑非花（给出像素统计/哈希），副链透传帧与基线一致
4. 帧率不劣化到不可用（对比 B-R9-03 的 59.8 fps，允许 ±20%）
5. 失败安全回归：`ZFG_HOOK` 未设 / `=log` / wrap 失败三条路径仍回退原始 DXGI，Unity 正常启动
6. 六段不回归：native **196**/710,837、editmode **678**、playmode **6**、三校验器 exit 0

### 硬约束

- 代理必须**可一键关闭**（`ZFG_HOOK` 未设 = 零干预），关闭后与 HEAD 零差异
- 不得改 `URP_*.asset`、`FrameGovernor`/`GovernorLadder`/`GovernorTuning`
- 代码放 `Native/zfgprobe/`，构建目标保持 `EXCLUDE_FROM_DEFAULT_BUILD`
- C++ `/W4 /WX` 零警告；`.cs` 无 BOM/LF；**禁止 git commit**
- **不得伪造**：`generated` 数字必须来自真实运行日志；像素证据必须来自读回

### 环境纪律

- 构建/测试前 `unset http_proxy https_proxy all_proxy`
- 六段用 **PowerShell 工具**（Bash 侧禁止转调 powershell）
- Player 是 GUI 程序：**必须超时兜底**（`timeout 60` 或后台+等待+`taskkill`）
- GUI Editor 加 `-force-d3d12` 会挂起 → 只测 Player
- 代理日志用 `ZFG_HOOK_LOG` 指到 `TempLogs/r9_04/`，**不要用 `%TEMP%`**（B-R9-03 因此丢过证据）
- 清理用 `mv` 归档到 `TempLogs/archive_pre_audit/`，不要 `rm -rf`
- 跑完检查 `git status`：Editor 可能改写 `URP_*.asset`，非本意就 `git checkout` 还原

### 交付

「施工报告」写第 2 节：**阶段 1 取证结论（#2 是谁）→ 策略选择与理由 → 阶段 3 数字**。
若阶段 3 仍不达标，写清"卡在哪一环、已排除什么、下一步建议"，**不要为了做完而伪造数字**。

---

## 施工报告（施工代理写）

> 施工代理：ZCode 施工实例（与 B-R9-03 施工/验收均不同实例）。**未 git commit / 未 git add**。
> 证据目录：`TempLogs/r9_04/`（全部日志按简报要求用 `ZFG_HOOK_LOG` 指到这里，未落 `%TEMP%`）。
> 复现材料：60 秒 Player 在 `TempLogs/r9_04/player/`，六个运行包 `run_{fg,wrap,log,unset,fail_nohook,fail_noffx}/`。

### 2.1 阶段 1 取证：第二条链是谁（结论先给）

**结论：#2 不是 Unity 的第二条链，是 AMD FFX 在 `ffxCreateContextDescFrameGenerationSwapChainForHwndDX12`
内部自己创建、自己呈现到屏幕的底层交换链。它本来就不该被 wrap（wrap 它会自我递归）。**

#### 证据 1：创建调用栈的模块归属（可证伪，不是猜）

给每条链建档时用 `RtlCaptureStackBackTrace` + `GetModuleHandleEx(FROM_ADDRESS)` 把栈帧翻译成
"模块名+偏移"。`ZFG_HOOK=fg` 60s 运行（`TempLogs/r9_04/final_fg.log`）：

```
chain[1] wrapped=0 1280x720 fmt=28 buffers=3 usage=0x20 effect=4 flags=0x840
  creator: amd_fidelityfx_framegeneration_dx12.dll+0xB978 <- amd_fidelityfx_framegeneration_dx12.dll+0x81D7
           <- amd_fidelityfx_framegeneration_dx12.dll+0x12A8E <- ...
chain[2] wrapped=1 1280x720 fmt=28 buffers=3 usage=0x30 effect=3 flags=0x842
  creator: UnityPlayer.dll+0x9530ED <- UnityPlayer.dll+0x10EF6A8 <- UnityPlayer.dll+0x81575D <- ...
```

- chain[1]（= 验收诊断里的 `#2`，`usage=0x20`/`effect=4`/`flags=0x840`）**栈里一个 UnityPlayer 帧都没有**，
  4 帧全在 FFX 的 dll 内；chain[2]（宿主那条）栈全在 UnityPlayer 内。
- 时序上也吻合：`zfg wrap: probe support=3` → **chain[1] 创建** → `zfg wrap: proxy swapchain created`，
  即它是在我们的 `WrapCreateSwapChainForHwnd` **内部**被 FFX 建出来的，日志里标 `inWrap=1`
  （现有 `t_inWrap` 闸门正是为它准备的：重入一律走原生，否则 wrap 自我递归）。

#### 证据 2：屏蔽候选源（最硬的一条）——不 wrap 时**只有一条链**

`ZFG_HOOK=log`（只记录不替换，60s，`TempLogs/r9_04/final_log.log`）：

```
==== zfg chain summary: chains=1 wrapFrames=0 ... ====
chain[1] wrapped=0 usage=0x30 effect=3 flags=0x842
  creator: UnityPlayer.dll+0x9530ED <- ...   presents: total=3856 (Present1=3856) ok=3856
```

**Unity 全程只建了 1 条交换链。** 第二条链只在"我们调 FFX 建代理链"之后才出现 → 归属只能是 FFX。
（B-R9-03 验收结论里"Unity 创建了两条交换链"的表述据此更正。）

#### 证据 3：谁真的呈现到屏幕

| 链 | 我们的 Present 计数 | DXGI 自己的 `GetLastPresentCount` | 说明 |
|---|---|---|---|
| chain[1]（FFX 底层链） | 7475 | **7475** | 逐条对上；FLIP_DISCARD + 同一 hwnd，flip 即上屏 |
| chain[2]（返回给宿主的代理链） | 3847 `Present1`（应用）+ 3847 `Present`（FFX 内部转发） | 7475（转发到 FFX 上下文） | 宿主看到的对象，实际呈现被 FFX 转给 chain[1] |

`usage=SHADER_INPUT` 也解释了用途：FFX 要在插帧时**读回**已合成的呈现帧，故必须带 SHADER_INPUT。

#### 证据 4：`flags=0x200` 是 `ALLOW_TEARING`，不是 `DO_NOT_WAIT`（口径纠正）

链上大量 `Present` 带 `flags=0x200`、`sync=0`，我一开始按 `DXGI_PRESENT_DO_NOT_WAIT` 解释，
实测计数器全为 0 才发现 `0x200` 是 **`DXGI_PRESENT_ALLOW_TEARING`**。
**不存在"FFX pacing 线程反复重试"这回事**：全部呈现（含 chain[1]）都在同一个渲染线程（tid 相同）、
同步发生，一条应用 `Present1` 内联展开成 `Present(chain2)` + `Present(chain1)`。

### 2.2 真正的根因：一次应用 Present 被当成了两帧（比"两条链"更具体）

修复前（同一份 5s 包，`TempLogs/r9_04/fg_stage1.log`）：

```
onFrame calls=1116  withInputs=328  prepared=328  prepareFail=0
fgstats: real=558 generated=3 prepareOk=328
```

一次应用 `Present1` 在 hook 里会**进两次** `WrapOnFrame`（`calls=1116 = 2 × 558`）：

1. 应用 `Present1`(chain[2]) → `Configure(frameId=2k, enabled=true)` + `Prepare(2k)`；
2. FFX 在它的 `Present1` 实现里又调了一次**同一个代理对象**的 `Present`（日志 `nested=1`）
   → 第二次 `WrapOnFrame` 拿不到新输入 → `Configure(frameId=2k+1, enabled=false)`；
3. FFX 随后按 2k+1 的配置真正呈现 → **本帧插帧被自己刚写下的 `enabled=false` 关掉**。

`generated≈0` 与 `prepareOk≈328`（只有一半调用有新输入）由此直接解释，不需要"SDK pacing 玄学"。

**顺带纠正"呈现 1.7×"的口径**：`real` 是 FFX presentCallback 的累计次数 = chain[1] 的呈现次数，
它包含**探针开始前的启动期**呈现（本机 ~220–250 帧）。5s 包里 558 = 230(启动) + 328(FG 窗口)，
拿它除以探针循环的 330 帧就得到假的 1.7×。60s 长跑下 3847/3600 = **1.07**，
多出来的 247 帧全部落在探针开始之前 → **"每帧多次呈现"这个异常并不存在**。

### 2.3 策略选择与实现

**选"策略 B 的变体"：只 wrap 主链（Unity 请求的那条），FFX 自建的底层链保持原生透传。**

为什么不是策略 A（两条都 wrap）：chain[1] 是 FFX 内部建的，wrap 它 = 在 FFX 的创建回调里再调
`ffxCreateContext…SwapChain`，必然自我递归（现有 `t_inWrap` 闸门已实测过这个死法）。
为什么不是"只 wrap 主链 + 副链透传"的原义：副链不是"宿主的第二条链"，而是 FFX 的实现细节，
我们连"接管"的动机都不该有——它本来就在 FFX 手里，透传是唯一正确形态。

共用修点落在简报指出的那一处，分两条：

1. **输入消耗按"应用级 Present"绑定**（`zfg_probe_hook.cpp`）：新增 `thread_local t_inWrappedPresent`，
   在转发给 FFX 的 `Present/Present1` 前置位、返回后复位；`HookPresent/HookPresent1` 里
   `appLevel = isWrapped && !nested`，**只有 appLevel 才推进 `Configure/Prepare`**，嵌套调用纯转发。
   → 每应用帧 1 次 `Configure`（frameID 严格 +1）+ 1 次 `Prepare`，1:1 契约恢复。
2. **输入消费改三态**（`zfg_probe_wrap.cpp` `WrapOnFrame`）：`fresh`（本帧新提交）/`reuse`
   （同一渲染帧的重复呈现，复用同一 depth/MV，上限 `kMaxInputReuse=2`）/`stale`（无可用输入 → 不插帧）。
   实测 60s 里 `fresh=3628 / reused=1 / stale=218`（stale 全是启动期无输入帧）。

新增取证设施（都是零干预、只在 `ZFG_HOOK` 非空时工作）：

| 设施 | 位置 | 作用 |
|---|---|---|
| 交换链注册表 + 创建栈归属 | `zfg_probe_hook.cpp` | 每条链的 desc/创建者/wrap 标记/独立 Present 计数（按 tid、ALLOW_TEARING、成败分桶）+ DXGI `GetLastPresentCount` |
| `ZFG_FG_TRACE=1` | 同上 | 逐次打印应用级呈现的毫秒时间戳（用于判定呈现节奏，默认关） |
| 底层链像素读回 | `zfg_probe_wrap.cpp` | chain[1] Present **返回后**取 `GetCurrentBackBufferIndex` 的 buffer 拷进 READBACK 堆，stride=16，`ZFG_NATIVE_CAPTURE=0` 可关，失败即永久停用 |
| 读回计数 | 同上 | `capture: total/generated/real`、`native capture: samples/skipped` |

### 2.4 阶段 3 验证数字（全部来自最终二进制的 60s 实跑）

产物：`Native/build/bin/Release/{dxgi.dll,zfg_hook.dll}`（`/W4 /WX` 零警告），
运行包 `TempLogs/r9_04/run_fg`，日志 `final_fg.log` / `final_unity_fg.log`。

#### （1）`ZFG_HOOK=fg` 60s —— 达标

| 指标 | 判据 | 实测 | 判定 |
|---|---|---|---|
| 帧数/时长 | 60s | frames=3600 elapsed=60.116s | — |
| `generated/real` | ≥0.9 | **3629 / 3847 = 0.943**（同二进制复跑 3629 / 3858 = **0.941**） | PASS |
| `prepareFail` | 0 | **0** | PASS |
| `lastRc` | 0 | **0** | PASS |
| `prepareOk` | 随帧增长 | 3629（= generated，1:1） | PASS |
| `enabled` | 1 | 1（每 600 帧快照 5/5 全 1） | PASS |
| 帧率 | ≥59.8×0.8 | **59.9 fps**（复跑 59.8；基线 59.8） | PASS |

`real` 含启动期无输入帧，故 0.943 而非 1.0；**FG 窗口内 prepared:generated = 3629:3629 = 1:1**，
`onFrame calls=3847 fresh=3628 reused=1 stale=218`。**两次独立 60s 实跑**（`final_fg.log` / `final2_fg.log`）
结论一致：`generated` 恒 3629、`prepareFail`/`lastRc` 恒 0，可复现。

#### （2）呈现比 ≈2.0×，两链分别计数

| 链 | 总呈现 | 其中 ALLOW_TEARING | 应用级 | DXGI 自报 |
|---|---|---|---|---|
| chain[1] FFX 底层链（上屏） | **7475** | 7258 | — | 7475 |
| chain[2] 代理链（宿主） | 7694 = 3847 `Present1` + 3847 `Present`（FFX 内部转发） | 7260 | 217 | 7475 |

- **FG 窗口内呈现比 = 7258 / 3630 = 2.00×**（3630 = 7260/2，代理链的应用级 tearing 呈现数）。
- 全生命周期 7475 / 3847 = 1.94×，差额来自启动期只呈现 1×（FG 未启用）的 217 帧。
- 对照 `ZFG_HOOK=wrap`（FG 关，`final_wrap.log`）：chain[1] **3856** 呈现 / 3856 应用呈现 = **1.00×**
  → 2.0× 确实是插帧带来的，不是计数错误。

#### （3）像素：两条链都读回

FG 链（代理链呈现帧，SDK present 回调旁路读回，`gen=1` 即插帧帧）：

```
zfg capture: slots=6 total=4563 generated=3629 real=934      ← 3629 个插帧帧全部读回过
zfg capture[1]: gen=1 frameId=3846 mean=0.2484 var=0.01505 black=0.0% rMin=47 rMax=249 hash=0x19543A09FE833529
zfg capture[2]: gen=1 frameId=3847 mean=0.2484 var=0.01513 black=0.0% rMin=47 rMax=249 hash=0x0ACCD57BA8B2A582
zfg capture[3]: gen=1 frameId=3843 mean=0.2489 var=0.01522 black=0.0% rMin=47 rMax=249 hash=0xA352B4733E5E5545
zfg capture[4]: gen=1 frameId=3844 mean=0.2485 var=0.01508 black=0.0% rMin=47 rMax=249 hash=0x790FC309D683A094
zfg capture[5]: gen=1 frameId=3845 mean=0.2487 var=0.01522 black=0.0% rMin=47 rMax=249 hash=0xAD67053875F7DDA9
```

判读：黑像素 **0.0%**、方差 0.015（有结构、不是纯色）、R 通道 47–249（有动态范围）、
5 帧哈希互不相同且与同帧真帧不同 → **插帧帧是"新合成的有效画面"，不是黑屏也不是花屏**。

副链（chain[1]，真正上屏那条）直接读回：

```
zfg native[0]: presentSerial=7456 bufIndex=0 mean=0.2490 var=0.01563 black=0.0% rMin=47 rMax=249 hash=0xC836C8A8B4812A86
zfg native[1]: presentSerial=7472 bufIndex=1 mean=0.2487 var=0.01546 black=0.0% rMin=47 rMax=249 hash=0x142C6EFE7AF6742D
zfg native[2]: presentSerial=7440 bufIndex=2 mean=0.2479 var=0.01514 black=0.0% rMin=47 rMax=250 hash=0xE365260D849188B3
zfg native capture: samples=7476 skipped=0
```

**交叉校验（关键）**：同一呈现序号上，副链读回与 SDK 旁路读回**逐字节一致**——
`serial=7472`：`native[1] hash=0x142C6EFE7AF6742D` == `capture[0] hash=0x142C6EFE7AF6742D`。
说明"上屏链的后台缓冲"就是"FFX 合成结果"，两条链读回的是同一幅画面（其余采样因缓冲区轮转
差一拍，统计量一致、哈希不同，见 2.5 边界①）。

基线对照：Unity 自己的 `ScreenCapture` 在 fg/wrap/log 三种模式下都是
`hash=0xD828D96074196AF0 mean=0.2466 nonBlack=100.0%`，与 B-R9-03 基线**逐位相同**；
副链读回统计量（mean 0.2479–0.2490 / black 0%）与之同量级（差异来自相机正弦平移的相位）。

#### （4）帧率不劣化

| 模式 | fps | 相对基线 59.8 |
|---|---|---|
| 基线（B-R9-03 无代理） | 59.8 | — |
| `ZFG_HOOK=fg` | **59.9** | +0.2% |
| `ZFG_HOOK=wrap` | 59.9 | +0.2% |
| `ZFG_HOOK=log` | 59.9 | +0.2% |
| 未设 `ZFG_HOOK` | 59.9 | +0.2% |

（判据是"劣化 ≤20%"，实测无劣化。）

#### （5）失败安全三条路径（各 60s，全部 exit 0）

| 路径 | 构造 | 日志证据 | Unity |
|---|---|---|---|
| `ZFG_HOOK` 未设 | 只放代理 dll | **`unset60.log` 文件根本不存在**（零干预） | fps 59.9 / hash 与基线一致 / exit 0 |
| `=log` | 只记录 | `chains=1`，`WRAPPED` 0 次 | fps 59.9 / hash 与基线一致 / exit 0 |
| hook 模块缺失 | `zfg_hook.dll` → `.off`，`ZFG_HOOK=wrap` | `[dxgi] hook: LoadLibraryW(zfg_hook.dll) err=126 -> forwarding only` | fps 59.9 / hash 与基线一致 / exit 0 |
| FFX 运行库缺失 | 两个 `amd_fidelityfx_*` → `.off`，`ZFG_HOOK=wrap` | `probe support=0` → `wrap FAILED (FG not available); falling back to native swapchain` | fps 59.9 / hash 与基线一致 / exit 0 |

#### （6）六段不回归

`Tools/run_tests.ps1`（六段一键脚本，先清代理变量）：

```
native   PASS  cases=196 asserts=710837 failed=0
editmode PASS  total=678 passed=678 failed=0 skipped=0
playmode PASS  total=6 passed=6 failed=0 skipped=0
arch     PASS  exit=0
config   PASS  exit=0
naming   PASS  exit=0
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)   SCRIPT_EXITCODE=0
```

脚本把 ctest 判成 `binary not usable here` 而跳过（B-R9-03 已记录的脚本探测问题），手工补跑
`ctest --test-dir Native/build -C Release` → `1/1 Passed, 100% tests passed`。
断言数 **710,837** 与基线一致（本轮未改任何测试代码）。

#### （7）治理

- `git log -1` = `35f97b9`（与开工时同一 HEAD，**未 commit**）；`git status` 仅 4 个源文件 M：
  `Native/zfgprobe/zfg_probe_hook.{h,cpp}`、`Native/zfgprobe/zfg_probe_wrap.{h,cpp}`。
- `UnityProject/Assets/Settings/` 干净（Editor 构建时改写的 `URP_*.asset` 已 `git checkout` 还原）；
  `UnityProject/Assets/Plugins/ZRender/zrender.dll` 构建产物噪音已还原。
- 探针资产已移回 `TempLogs/r9_03/probe_assets/`（未入库）；未动 Unity 安装目录；未改
  `FrameGovernor`/`GovernorLadder`/`GovernorTuning`/`URP_*.asset`。

### 2.5 未完成项与边界（如实）

1. **两链读回的哈希只在"同一呈现序号"上逐字节相等，跨采样不等**。原因：FFX 在它自己的队列上做
   合成，我们的读回拷贝在游戏队列上，采样时后台缓冲可能已被轮转（3 缓冲），读到的相邻一帧内容。
   已把拷贝从 "Present 前" 改到 "Present 后"（`WrapCaptureNativePresent` / `WrapNativeCaptureAfterPresent`
   配对），并在 `serial=7472` 上拿到完全一致，但**不能保证每一次采样都同帧**。
   判据上我按"统计量一致 + 至少一次逐字节一致"立论，没有夸大成"每次都能逐字节比对"。
2. **`generated/real` 的分母含启动期**：0.943 达标，但严格说"FG 窗口内"是 1.0。若后续要更干净的
   指标，建议在 `ZfgFgStats` 里加 `realWhileEnabled`（本轮未改 C ABI 结构，避免动 C# 侧）。
3. **未验证**：`ResizeBuffers` 路径（窗口改尺寸时代理链重建）、D3D11 路径（FG 只支持 D3D12）、
   Editor 内行为（简报明确只测 Player）、真实交互桌面下的目视确认（本机无交互桌面，全部靠读回）。
4. **`reuse` 分支只被触发 1 次**（3847 帧里），属于防御性代码，实际未被压力验证。
5. **探针仍是临时资产**（`Assets/__zfgprobe` 用后归档，未入库）；正式接线（设置页开关 + 能力门控）
   不在本任务范围。
6. **`real` 与 `generated` 的口径差异**：`real` 是 FFX presentCallback 的累计（含启动期），
   `generated` 只在 enabled 帧产生，两者直接相除天然 <1；这一点已写进上表，不做修饰。

---

## 验收清单（供验收代理）

- [ ] V1 阶段 1 取证：两条链的创建来源、desc、是否 wrap、各自 Present 计数，**有可证伪依据**（调用栈或屏蔽实验），不是"猜"
- [ ] V2 策略选择有依据：所选策略与阶段 1 结论一致，另一条链的处理明确
- [ ] V3 `WrapOnFrame` 输入消耗逻辑已按链绑定（或等价方案），代码审查确认 1:1 契约
- [ ] V4 亲跑 `ZFG_HOOK=fg` 60s：`generated/real ≥ 0.9`、`prepareFail=0`、`lastRc=0`（独立复现，不采信施工单方日志）
- [ ] V5 呈现比 ≈2.0×，两链计数分别给出
- [ ] V6 两条链像素读回：FG 链插帧帧非黑非花、副链透传与基线一致
- [ ] V7 失败安全回归（未设/log/wrap 失败三条路径）
- [ ] V8 六段不回归（196/710,837、678、6、exit 0）+ 未 commit + URP 资产干净

## 验收结论（验收代理写）

> 验收代理：ZCode 验收实例（与 B-R9-03 施工/验收、B-R9-04 施工均不同实例）。
> **未 git commit / 未改业务代码**；只写本节 + 跑测试/Player + `mv` 归档。
> 全部数字来自本轮**自己跑的** 60s Player（`TempLogs/r9_04/audit_*.log`），未复用施工日志。

### 结论：**PASS with 受限（8/8 达成；受限项见 §R1–R3）**

### 验收方法（可复现，含装配说明）

1. **重建**（不复用施工二进制）：`mv` 掉 `Native/build/zfgprobe.dir/Release/zfg_probe_{hook,wrap}.obj` →
   `cmake --build Native/build --config Release --target zfg_hook`（MSBuild 17.14，`/W4 /WX`）
   → **零警告零错误**，重编译 `zfg_probe_hook.obj`/`zfg_probe_wrap.obj`（13:02），产出 `zfg_hook.dll` md5 `0799981b…`。
   `dxgi.dll`（md5 `3f9f4aed…`）已与当前 `zfg_dxgi_proxy.cpp` 同步，未重链。
2. **装配**：`cp -r TempLogs/r9_04/player/. TempLogs/r9_04/audit_<mode>/` + 从
   `Native/build/bin/Release/` 拷 `dxgi.dll`、`zfg_hook.dll`、`amd_ags_x64.dll`、
   `amd_fidelityfx_framegeneration_dx12.dll`、`amd_fidelityfx_loader_dx12.dll`。
   （`player/` 是施工方 60s Player 原件；`ZfgR903_Data` 内的 R903 场景脚本已随构建烘进 Player。）
3. **启动**（每轮 60s，Player 自计时 3600 帧后自行退出；`timeout 180` 兜底）：
   ```
   unset http_proxy https_proxy all_proxy HTTP_PROXY HTTPS_PROXY ALL_PROXY
   cd TempLogs/r9_04/audit_fg_b
   ZFG_HOOK=fg ZFG_HOOK_LOG='…/TempLogs/r9_04/audit_fg_b.log' timeout 180 \
     ./ZfgR903.exe -force-d3d12 -screen-fullscreen 0 -logFile '…/audit_unity_fg_b.log'
   ```
   注：**`ZFG_HOOK_LOG` 必须是完整文件名，不是目录**（`Install()` 直接 `CreateFileA` 该串；
   传目录会失败→不装 hook）。不加 `-force-d3d12` 会回落到 Direct3D 11 并在 FFX 代理链创建处崩溃（见 §R2）。
4. 本轮实跑：fg ×3、wrap ×4（含 1 次崩溃）、log ×1、未设 ×1、fail_nohook ×1、fail_noffx ×1，
   加首轮无参崩溃 ×1；六段测试 ×1。

### V1–V8 逐条

**V1 PASS**（独立复现，证据可证伪）——`audit_fg_b.log:14-24`：
```
chain[1] registered (native, inWrap=1) creator: amd_fidelityfx_framegeneration_dx12.dll+0xB978
   <- …+0x81D7 <- …+0x12A8E   …1280x720 fmt=28 buffers=3 usage=0x20 effect=4 flags=0x840
chain[2] registered (wrapped) creator: UnityPlayer.dll+0x9530ED <- …+0x10EF6A8 <- …+0x81575D
   …1280x720 fmt=28 buffers=3 usage=0x30 effect=3 flags=0x842
```
- 时序：`probe support=3` → `CreateSwapChainForHwnd(usage=0x20)` → `proxy swapchain created`，
  即 chain[1] 在我们 `WrapCreateSwapChainForHwnd` 内部被建出（`inWrap=1`）。
- **屏蔽实验（最硬）**：`ZFG_HOOK=log` 跑 60s → `audit_log.log:84` `chains=1`，
  唯一一条 `wrapped=0 usage=0x30 creator: UnityPlayer.dll…` → 不调 FFX 建代理时**没有第二条链**，
  第二条只能是 FFX 建的。与施工结论一致。
- 各自 Present 计数（同一轮 fg）：chain[1] `Present=7489`（DXGI `GetLastPresentCount=7489` 逐条对上）；
  chain[2] `Present=3861 + Present1=3861`（前半是宿主 Present1、后半是 FFX 内部转发，`nested=1`）。

**V2 PASS**——策略 = 只 wrap 宿主链（Unity 请求的那条），FFX 自建底层链保持原生透传；与阶段 1 结论一致：
`HookCreateSwapChainForHwnd` 里 `t_inWrap` 保证重入走原生（`zfg_probe_hook.cpp:469-474`），
`HookPresent/HookPresent1` 里 `appLevel = isWrapped && !nested`（`zfg_probe_hook.cpp:597/653`）
→ 副链**不推进 Configure/Prepare**，只做像素采样（`:629-631`）。不是"两条都 wrap"（会自我递归），
也不是"给副链另接一套 FG"——副链本就在 FFX 手里，透传是唯一正确形态。

**V3 PASS（等价方案）**——链路绑定落在 hook 层而非 `WrapOnFrame` 内部：只有
`isWrapped && !nested` 才调 `WrapOnFrame`，因此它天然只服务"应用级 Present"一条队列；
`WrapOnFrame`（`zfg_probe_wrap.cpp:855-941`）内部是 `frameId+=1`（严格 +1）+ 三态输入
（`fresh`/`reuse`≤2/`stale`），stale 帧 `enabled=false` 不插帧。1:1 契约实测：
`onFrame calls=3861` == chain[2] 应用级呈现 3861（总 `Present` 7722 = 2×3861，说明嵌套的那一半没再进 FG），
`prepared=3629 == generated=3629`、`prepareFail=0`、`skipNested=3861`（每次应用 Present 恰好一次内部转发）。
**"输入消耗按链绑定"在单 FG 链形态下等价成立**，但请注意它是"调用点绑定"，不是"每条链各一套输入队列"。

**V4 PASS（自己跑，3 轮一致）**——`ZFG_HOOK=fg` 60s：

| 轮次 | frames/耗时 | fps | real | generated | generated/real | prepareFail | lastRc |
|---|---|---|---|---|---|---|---|
| audit_fg_b | 3600 / 60.102s | 59.9 | 3861 | 3629 | **0.9399** | 0 | 0 |
| audit_fg_r1 | 3600 / 60.091s | 59.9 | 3861 | 3629 | **0.9399** | 0 | 0 |
| audit_fg_r2 | 3600 / 60.070s | 59.9 | 3862 | 3629 | **0.9397** | 0 | 0 |

原始行（`audit_unity_fg_b.log:106`）：`[R903] fgstats: real=3861 generated=3629 prepareOk=3629 prepareFail=0 enabled=1 lastRc=0`。
与施工自报 0.943 差 0.003，差在 `real` 的启动期帧数（我这轮 232 帧 stale），不是口径造假。

**V5 PASS**——两链分别计数（`audit_fg_b.log:347-354`）：

| 链 | 总呈现 | 其中 tearing | 应用级 |
|---|---|---|---|
| chain[1] FFX 底层链（上屏） | 7489（DXGI 自报 7489） | 7258 | — |
| chain[2] 代理链（宿主） | 7722 = 3861 Present1 + 3861 Present | 7260 | 231 |

- 全周期 `7489 / 3861 = 1.94×`；**FG 窗口 `7258 / (7260/2=3630) = 2.00×`**。
- 对照 `ZFG_HOOK=wrap`（FG 关，`audit_wrap_r1.log:287-295`）：chain[1] 3861 / chain[2] 3861 = **1.00×**
  → 2.0× 确由插帧产生，不是计数错误。

**V6 PASS（受限：逐字节只覆盖真帧）**——FG 链插帧帧（`gen=1`，`audit_fg_b.log:359-364`）：
```
zfg capture[0] gen=1 fid=3858 serial=7483 mean=0.2485 var=0.01508 black=0.0% rMin=47 rMax=249 hash=0x790FC309D683A094
zfg capture[2] gen=1 fid=3860 serial=7487 mean=0.2484 var=0.01505 black=0.0% rMin=47 rMax=249 hash=0x19543A09FE833529
zfg capture[4] gen=1 fid=3861 serial=7489 mean=0.2484 var=0.01513 black=0.0% rMin=47 rMax=249 hash=0x0ACCD57BA8B2A582
```
黑像素 0.0%、方差 0.015（有结构）、R 通道 47–249（有动态范围）、同帧 `gen=1` 与 `gen=0` 哈希不同
（fid=3860：`0x1954…` vs `0xCC77…`）→ 插帧是**新合成的有效画面**，非黑非花。
副链（chain[1]）读回：`mean 0.2479–0.2487 / var 0.0151–0.0154 / black=0.0%`，与 wrap 模式（纯透传）
同区间；Unity 自读回 `cap@60/cap@240 hash=0xD828D96074196AF0 nonBlack=100.0%` 在 fg/log/wrap/未设四种模式下**完全相同**。
**受限**：同 serial 逐字节相等只拿到了真帧一次（`serial=7488`：`capture[3]=native[2]=0xCC773C11A5CB18BF`），
原生采样 stride=16，本轮没抽到 `gen=1` 帧的同 serial 样本 → "插帧帧上屏 = FFX 合成结果"靠统计量+serial 推理，
未做逐字节证明（与施工 §2.5① 自述一致，不夸大）。另：像素统计是 8×8 抽样（`zfg_probe_wrap.cpp:304-325`）。

**V7 PASS**——三条路径均 `exit 0`、Unity 正常起停：

| 路径 | 证据 | fps | Unity 读回 |
|---|---|---|---|
| 未设 `ZFG_HOOK` | `audit_unset.log` **文件不存在**（零干预） | 60.0（3600/60.042s） | `0xD828D96074196AF0` |
| `=log` | `audit_log.log:84` `chains=1`、`WRAPPED` 0 次 | 59.9 | 同上 |
| hook 缺失（`.off` + `=wrap`） | `audit_fail_nohook.log:2` `LoadLibraryW(zfg_hook.dll) err=126 -> forwarding only` | 60.0 | 同上 |
| FFX 缺失（两个 `amd_fidelityfx_*` → `.off` + `=wrap`） | `audit_fail_noffx.log:11-12` `probe support=0` → `wrap FAILED (FG not available…); falling back to native swapchain`，`chains=1` | 60.0 | 同上 |

**V8 PASS**——`Tools/run_tests.ps1`（本轮 `audit_run_tests.log`）：
```
native   PASS cases=196 asserts=710837 failed=0; ctest: 100% tests passed (1 entry: zrender_test)
editmode PASS total=678 passed=678 failed=0
playmode PASS total=6 passed=6 failed=0
arch/config/naming PASS exit=0
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)
```
治理：`git log -1` 仍 `35f97b9`（**未 commit**）；`git status --short` 仅 5 个文件（4 源 + 本文档）；
`UnityProject/` 全干净（测试脚本部署的 `zrender.dll` 二进制噪音已 `git checkout` 还原，`URP_*.asset` 无改写）。

### 复核施工 §2.5 六条自述（逐条，均如实）

1. 跨采样不逐字节相等 —— **属实**（见 V6 受限项，同 serial 一致只出现在真帧）。
2. 分母含启动期 —— **属实**（stale=232/233 全在启动期；`generated` 只在 enabled 帧产生）。
3. ResizeBuffers/D3D11/Editor 未验 —— **属实**：本轮 `ResizeBuffers` 调用 **0 次**（两条链日志均无）；
   D3D11 确实没跑通且**会崩**（无 `-force-d3d12` 那轮 `Direct3D 11.0` → 崩溃，见 §R2，本轮 1 patch 故可干净归因）；
   Editor 未测。
4. `reuse` 只触发 1 次 —— **属实**（三轮 fg `reused=1`，属防御性分支，未受压）。
5. 探针是临时资产、未入库 —— **属实**（工作区无 `Assets/__zfgprobe`）。
6. `real` vs `generated` 口径差异 —— **属实**（与 V4 数据自洽）。

**未发现把"没做"写成"做了"**：施工自报的 6 个数字（0.943 / 3629 / 0 / 59.9fps / 7475 / 2.00×）
我独立复现为 0.9397–0.9399 / 3629 / 0 / 59.9fps / 7489 / 2.00×，差异都能用启动期帧数解释。

### 剩余风险（施工 §2.5 未列的第 7 条：本轮新发现）

**R1（阻断级，既有缺陷，非本轮引入）：`Install()` 双安装竞态 → 偶发启动崩溃。**
每次运行都有 2–4 个安装入口（`dxgi` 代理的 `ZFG_ProbeEnsureInstalled` 与 `zfg_hook.dll` DllMain 的
50ms 兜底线程，实测 install 次数 2–4），`g_installed` 是普通 bool、"检查→patch"非原子：
- 正常时后来者打印 `another probe copy already hooked this process; skip re-patch`（施工 9 份日志 patches 全为 1；我的 9 次正常运行 patches 也是 1）；
- **竞争失败时两个线程都 patch**，第二次把自家的 `HookCreateSwapChainForHwnd` 当成"原始函数"
  → hook 无限自我递归 → 栈溢出（`exit 139`）。证据：`TempLogs/r9_04/audit_wrap.log`
  （`installs=4 / patches=2`，随后 `CreateSwapChainForHwnd` 递归刷屏，Unity 侧 `Crash!!!`）。
- 频率：本轮 12 次运行中 **1 次**（wrap 正常路径）。施工 9 次运行 0 次（他们没抽到失败侧）。
- 该代码路径**不在本轮 diff 内**（`IsProbeCode`/`g_installed` 是 HEAD 既有），但会让"正常模式"偶发起不来，
  与"可一键关闭 / 失败安全"的意图相冲，建议下一任务加原子守卫（`InterlockedCompareExchange` 抢占安装权）。

**R2（未验证 + 已知会崩）：非 D3D12 路径崩在 `CreateSwapChainForHwnd`。**
首轮不带 `-force-d3d12` 的 fg 运行 `exit 139`（`TempLogs/archive_pre_audit/audit_fg_a_crash.log`）：
Unity 日志 `Version: Direct3D 11.0 [level 11.1]`，hook 侧停在
`desc 1280x720 buffers=2 usage=0x30 effect=3 fullscreenDesc=non-null` 那条 `CreateSwapChainForHwnd` 之后，
且该轮 **patches=1**（与 R1 无关）→ 归因明确：**在 D3D11 设备上调 FFX 代理链会当场崩溃**。
生产接线必须做能力门控（`ZFG_HOOK` 只在确认 D3D12 时生效），否则"默认带上代理 dll 但不开 FG"也不安全。

**R3（口径）：** `generated/real ≥ 0.9` 靠"启动期无输入帧"凑出 0.94，本质是 FG 窗口内 1.0；
若后续要干净指标，应在 `ZfgFgStats` 加 `realWhileEnabled`（本轮未改 C ABI，`zfg_fg_input.h` 未动，属实）。
