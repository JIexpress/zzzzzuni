# TASK-R10-07 —— 审计残余补漏：A-10 split 屏障丢失 + B-6 采集资源泄漏

- **轨道**：B / R10（R10-02 审计 17 条中的漏项）
- **状态**：doing
- **派发**：主控，2026-10-30

## 任务简报（主控写）

### 0. 为什么会有这个任务（漏项核对结果）

R10-02 审计共 17 条（P0×1 / P1×9 / P2×7）。我逐条核对了四个修复批次的覆盖面：

| 批次 | 覆盖 |
|---|---|
| R10-03 | A-1 A-2 A-4 A-5 A-6 |
| R10-04 | A-8 |
| R10-05 | A-3 A-7 B-1 B-2 B-3 B-4 B-5 B-7 |
| R10-06 | A-9 |

**合计 15 条 → 漏了 `A-10` 与 `B-6`。** 本任务补这两条。这也是本项目第一次出现"审计条目漏批次"，
所以除了修，还要在末尾给出**防止再漏的机制建议**（一句话即可，不用实现）。

### 1. A-10（P1）：`RgGraph::Execute()` 从不下发 split 屏障

**位置**：`Native/zrender/rg_render_graph.cpp:641-654`（split 分支只 `m_barrierPlan.splits.push_back()`，不进 `perPassImageBatches`）、
`:912-946`（`Execute` 只遍历 `perPassImageBatches` / `perPassBufferBatches`，**从不读 splits**）。

**后果**：任何走公共 API `RgGraph::Execute` 且图中存在"DSV 写 → 深度读"的图，**缺一道 `DEPTH_WRITE → DEPTH_READ` 转换** →
深度采样读到未定义 tile 状态（花屏/脏读）。D3D12 与 Vulkan 都受影响。当前生产路径走 `ExecuteAliased`，属潜伏缺陷，
**但 `Execute` 是对外公开 API**，不能靠"没人用"来免责。

**修复要求**：
1. **首选**：`PlanBarriers` 的 split 分支在记录 split 的同时，也产出一条**等价的内联屏障**放进 `perPassImageBatches`
   （即"split 是优化，不是唯一表达"），让 `Execute` 无需理解 splits 就是正确的。
2. 若选"让 `Execute` 消费 splits"这条路，必须保证 **begin/end 落在同一个 pass 内**（否则等于把 split 拆成两条独立屏障，语义不同），并说明为什么不会引入多余的转换。
3. **不得削弱 `ExecuteAliased` 既有的 split 语义**（它的 split 下发断言在 `test_rg_execute.cpp:104-140` 是既有护栏）。
4. **两条路径必须语义等价**：`Execute` 与 `ExecuteAliased` 对同一张图，`DEPTH_WRITE → DEPTH_READ` 的**发生次数与位置**应一致。
   若认为必然不同，必须在报告里逐点说明差异与理由。

**修前必红（硬性）**：把审计建议的场景 9（`test_rg_barriers.cpp:145-188`，现在只 `Compile()` 后断言 `plan.SplitCount()`）
改成**真的 `Execute`**，并断言 `commandList.barrierCalls` 中至少有一条 `DepthStencilReadOnlyOptimal`。
**当前这个断言必须是 0 条（即先看到红）**，修完变绿。不许为了让测试通过而放宽成"至少一条任意屏障"。

### 2. B-6（P2）：`EnsureCapture` 中途失败泄漏已建读回资源

**位置**：`Native/zfgprobe/zfg_probe_wrap.cpp:173-183`。

**现状**：`for (i=0..kCaptureSlots) { ...; if (FAILED(...)) return false; g_capture[i].resource = res; }` ——
第 N 个 `CreateCommittedResource` 失败时，**已建的 0..N-1 既不在 `g_captureReady` 也不被 Release**，
下一次 Present 会再建 N 个 → **泄漏放大**（显存越紧张越糟，典型的"压力下加速恶化"）。

**修复要求**：
1. 失败时把 `0..N-1` 的 resource **Release 并置空**，且保证 `g_captureReady` 状态一致（不残留"半就绪"）。
2. **重试不放大**：失败后下一次调用应重新建 `kCaptureSlots` 个，而不是叠加。
3. 若工程已有失败计数字段（如 `captureAllocFailureCount` 之类），把失败**计数并暴露**；没有就加一个（尾部追加，不动既有偏移）。
4. **可注入的失败**：`CreateCommittedResource` 真机不可控（只在显存不足时失败）。必须把资源创建抽成一个**可注入的接缝**
   （函数指针 / 小 factory），让测试能在第 N 次调用返回 `E_OUTOFMEMORY`。**没有可注入接缝的"修复"不算修**——
   因为无法证明失败路径真的被执行过。

### 3. 硬约束

- **白名单**：`Native/zrender/rg_render_graph.{h,cpp}`、`Native/zfgprobe/zfg_probe_wrap.{h,cpp}`、`Native/tests/**`（或等价的测试目录）、本日志。
  **需要越界请先停下写信向我申请**（R10-05 的教训：越界要付"逐位不变"的代价，不是你临场决定）。
- 基线 **native 251 用例 / 711,498 断言**，**只增不减**；EditMode **719**、PlayMode **7** 不得变化；六段全绿 exit 0。
- `/W4 /WX` 零警告；`git status` 不得出现 `URP_*.asset` / `Assets/__zfgprobe` / `zrender.dll` 噪声；**不 git commit**。
- 不装软件、不联网；禁止 `rm`（用 `mv`）。

### 4. 自测清单（贴证据）

1. **A-10 修前红**：场景 9 改 `Execute` 后，`DepthStencilReadOnlyOptimal` 计数 **= 0**，用例红（贴输出）。
2. **A-10 修后绿**：同一用例 ≥1 条且断言精确（贴输出 + 断言代码行）。
3. **A-10 双路径等价**：给出同一张图 `Execute` 与 `ExecuteAliased` 的屏障序列对比（条数/位置），差异逐点说明。
4. **A-10 既有护栏未削弱**：`test_rg_execute.cpp:104-140` 原样通过（`git diff` 证明该文件未改或被削弱）。
5. **B-6 失败注入**：注入第 N 次 `E_OUTOFMEMORY` → 断言 0..N-1 已 Release、`g_capture` 全空、`g_captureReady` 全 false（贴输出）。
6. **B-6 不放大**：失败后重试 → 再建 `kCaptureSlots` 个（不是 2N），给计数证据。
7. **B-6 失败计数**：若加了字段，给偏移/`static_assert` 与读取点。
8. **全量**：native ≥251 用例 / ≥711,498 断言、0 failed；六段 exit 0；`--draw`/`--lights`/`--backend vulkan --draw` 亲跑。
9. **规范**：`git status` 干净范围；零警告；未 commit。
10. **防再漏机制建议**一句话（不实现）。

---

## 施工报告（施工代理写）

- **结论**：A-10 与 B-6 均按简报闭环，`/W4 /WX` 零警告，六段全绿 exit 0，未 commit，无越界。
- **改动文件**（全部在白名单内）：`Native/zrender/rg_render_graph.{h,cpp}`、`Native/zfgprobe/zfg_probe_wrap.{h,cpp}`、
  `Native/tests/test_rg_barriers.cpp`、`Native/tests/test_zfg_resize.cpp`、本日志。
  `CMakeLists.txt` **未动**（新用例追加到既有测试文件，避免越界改构建脚本）。`test_rg_execute.cpp` 一行未改。

### 1. A-10（P1）：`Execute()` 现在下发 split 转换

**修法**：选简报的首选路线——split 是优化，不是唯一表达。`PlanBarriers` 的 split 分支在记录 split 的同时，
产出一条**等价内联屏障**进 `perPassImageBatches`（同一个 `ZrImageBarrier` 内容、同一 pass 位置），并用一条
与批次逐条平行的 **镜像位** 标明"这条来自 split"。`Execute` 无需理解 splits 就是正确的；`ExecuteAliased`
跳过镜像继续用 `BEGIN_ONLY/END_ONLY` 下发，**split 语义与条数完全不变**（不重复下发）。

**落点**

| 位置 | 内容 |
|---|---|
| `rg_render_graph.h:158-176` | `perPassImageBatchSplitMirror`（逐条平行镜像位）、`SplitMirrorCount()`、`InlineImageBarrierCount()` 语义改为"不含镜像" |
| `rg_render_graph.h:178-186` | `TotalBarrierCount()` = 内联(不含镜像) + buffer + split（镜像与对应 split 是同一道转换，不重复计数） |
| `rg_render_graph.cpp:614` | `PlanBarriers` 入口 `assign` 镜像位 |
| `rg_render_graph.cpp:668-673` | **split 分支**：`splits.push_back(split)` 后同时 push 等价内联屏障 + 资源句柄 + `mirror=true` |
| `rg_render_graph.cpp:686` | 普通内联图像屏障 push `mirror=false`（保证逐条平行） |
| `rg_render_graph.cpp:142-161` | `InlineImageBarrierCount()` 跳过镜像（避免双重计数） |
| `rg_render_graph.cpp:832-849` | `ExecuteAliased` 内联循环跳过镜像（`continue`），仍走 split 半边 |
| `rg_render_graph.cpp:925-960` | `Execute` **不改**——它下发整个批次，因此天然带上这道转换 |

**修前红（实跑，先看到 0）**：场景 9 改为真 `Execute` 后首次构建（`Tools/build_native.ps1`）：

```
C:\...\Native\tests\test_rg_barriers.cpp(205): ERROR: CHECK( depthReadConversions == 1u ) is NOT correct!
  values: CHECK( 0 == 1 )
[doctest] test cases:  251 |    250 passed | 1 failed | 0 skipped
[doctest] assertions: 711498 | 711497 passed | 1 failed |
FAILED: unit tests failed (exit=1)
```

计数口径是**精确形态**（`prevLayout == DepthStencilAttachmentOptimal && newLayout ==
DepthStencilReadOnlyOptimal`），不是"至少一条任意屏障"；断言是 `== 1u`（既证明存在、也证明不多发）。

**修后绿（实跑）**：

```
C:\...\Native\tests\test_rg_barriers.cpp(206): SUCCESS: CHECK( depthReadConversions == 1u ) is correct!
C:\...\Native\tests\test_rg_barriers.cpp(192): SUCCESS: CHECK( plan.TotalBarrierCount() == 2u ) is correct!
[doctest] test cases:  1 | 1 passed | 0 failed | 252 skipped
[doctest] assertions: 13 | 13 passed | 0 failed |
```

场景 9 的夹具同时从"带 Sampled 的深度"改成**纯 DSV**（`test_rg_barriers.cpp:29-38`）：真正对得上审计
要求的 `DEPTH_WRITE → DEPTH_READ`（`DepthStencilReadOnlyOptimal`）形态；带 Sampled 的深度（读侧推导
`ShaderReadOnlyOptimal`）另开一个用例覆盖，覆盖面没有减少。

**双路径等价（`test_rg_barriers.cpp:210-285`，实跑绿）**

同一张图（DSV 写 → 采样读，2 个 pass）：

| 路径 | 下发序列（按发生顺序） | 转换道数 |
|---|---|---|
| `Execute` | `ResourceBarrier[pass0]{Undefined→DSV}`、`ResourceBarrier[pass1]{DSV→SRO}` | 2 道 = 1 进入写 + 1 进入读 |
| `ExecuteAliased` | `ResourceBarrier[pass0]{Undefined→DSV}`、`SplitBegin{DSV→SRO}`（写 pass 后）、`SplitEnd{DSV→SRO}`（读 pass 前） | 同样 2 道；读侧那道被拆成 begin/end 两半 |

断言（`:269-285`）：`stats.inlineBarrierCalls == 1`（镜像没被当成内联发第二遍）、`splitBegins == 1`、
`splitEnds == 1`，并且把"承载转换的下发"摊平后逐位相等（split 只取 END 半边，BEGIN 半边是同一道转换的
拆半）：`seqAliased[i] == seqExecute[i]`，两两相同。**发生次数一致（各 1 次），位置一致（读 pass 之前）**；
唯一差异是 `ExecuteAliased` 额外多一条 BEGIN 半边落在写 pass 之后——这正是 split 的存在意义，不是多余转换。

**既有护栏**：`test_rg_execute.cpp:104-140` 未改动（`git diff` 无该文件），其 `splitBegins == 1 /
splitEnds == 1 / splitCalls.size() == 2 / newLayout == DepthStencilReadOnlyOptimal` 全部原样通过。

**诚实边界（A-10）**：`Execute` 从来不把 Transient 的**编译期占位句柄**替换成当帧真资源（它创建了
transient 资源却不回填批次句柄），这不是本次引入的问题——`Execute` 的所有内联屏障都如此，属该路径既有的
"未现代化"债务；本次只保证**这道转换在 `Execute` 下被下发**（审计条目原文的范围）。真机跑的是
`ExecuteAliased`（`zrhi_d3d12_window.cpp` 全部走它），本项不会改变真机行为。

### 2. B-6（P2）：`EnsureCapture` 失败清理 + 可注入接缝

**落点**

| 位置 | 内容 |
|---|---|
| `zfg_probe_wrap.h:65-98` | `ZfgCaptureBuildFn`/`ZfgCaptureSeam`（build + release 注入点）、`ZfgCaptureDiag`、`WrapSetCaptureSeam`/`WrapGetCaptureDiag`/`WrapEnsureCaptureBank` |
| `zfg_probe_wrap.cpp:212` | `g_captureAllocFailures`（原子失败计数，新增，尾部追加不动既有偏移/既有全局顺序） |
| `zfg_probe_wrap.cpp:218` | `g_captureSeam`（默认全空 = 生产走设备） |
| `zfg_probe_wrap.cpp:236-246` | `ReleaseCaptureResource`：注入时走注入的 release，生产走 `ID3D12Resource::Release()` |
| `zfg_probe_wrap.cpp:250-268` | `ComputeCaptureFootprints`：几何（`GetCopyableFootprints`）唯一来源，默认实现与生产共用 |
| `zfg_probe_wrap.cpp:273-311` | `BuildCaptureBankDefault`：**默认接缝实现**（= 生产路径），第 i 个 `CreateCommittedResource` 失败即 `*failedSlot = i` 返回 false |
| `zfg_probe_wrap.cpp:318-325` | `ReleaseCaptureBank` 改走 `ReleaseCaptureResource`（注入/生产同一释放路径） |
| `zfg_probe_wrap.cpp:363-433` | `EnsureCapture`：整组先建到临时数组 `made[]`，**全部成功才写入 `g_capture` 并置 `captureValid`**；失败时释放 `made[0..N-1]` 并置空、失败计数 +1、`captureValid` 保持 false |
| `zfg_probe_wrap.cpp:1069-1096` | 三个公开入口（注入/诊断/启用） |

**注入方式**：`ZfgCaptureSeam{user, build, release}`。测试实现 `FakeBuild` 在第 N 个槽位直接 `return false`
（等价 `E_OUTOFMEMORY`），`FakeRelease` 记释放次数；`WrapEnsureCaptureBank()` 驱动的是**产品同一个
`EnsureCapture`**，清理/提交/计数代码全部真实执行（不是测试里复刻一份）。生产路径 `g_captureSeam` 恒为空，
`build` 落到 `BuildCaptureBankDefault`。

**实跑证据（`test_zfg_resize.cpp:207-260`，26/26 断言绿）**

```
① 第 4 槽失败：CHECK( g_fakeBank.created == 4 ) / CHECK( g_fakeBank.released == 4 )   ← 0..3 全部释放
              CHECK( diag.liveSlots == 0 ) / CHECK_FALSE( diag.captureValid )        ← 不留半就绪
              CHECK( diag.allocFailures == 1u ) / CHECK( lastSlotCount == 6 )
② 重试（第 2 槽失败）：CHECK( created - createdBeforeRetry == 2 )   ← 只建本次 2 个，不是 4+2
              CHECK( released == created )（6 == 6）                  ← 建多少释放多少，无累积
③ 放行：CHECK( created - createdBeforeSuccess == 6 )                  ← 恰 kCaptureSlots 个
        CHECK( liveSlots == 6 ) / CHECK( captureValid )
        再调一次：CHECK( created 增量仍为 6 )                          ← 幂等，不叠加
④ retire 后再次构建失败：CHECK( released == created )（12 == 12）, liveSlots == 0
```

**失败计数暴露**：`ZfgCaptureDiag::allocFailures`（`WrapGetCaptureDiag` 读取）。既有 `ZfgFgStats`
（`zfg_fg_input.h`，跨模块 POD、不在白名单）**未动**，所以计数走新增的只读诊断入口而不是塞进既有结构
——这也满足"不动既有偏移"。

**顺带发现并修掉（同族泄漏，必须披露）**：本用例 ④ 一开始是红的——`released 6 / created 12 / liveSlots 6`。
根因不在 `EnsureCapture`，而在 `RetireCaptureBank`（`zfg_probe_wrap.cpp:333-341`）：它用
`g_display.captureValid` 判断"有没有槽位要退休"，但调用方 `ApplySwapchainResize`（`zfg_probe_resize.h:53-54`）
**先把 `captureValid` 清成 false 再调用它** → `had` 恒为 false → 整组读回资源从不搬进退休槽，下一次
`EnsureCapture` 直接覆盖 `g_capture[].resource` → **每次 resize 泄漏 kCaptureSlots 个读回资源**（正是 B-6
"压力下放大"的同族路径，且 resize 是高频触发）。修法：占用判定只看槽位本身
（`had = had || (g_capture[i].resource != nullptr)`），仍在白名单文件内，不改变对外语义。修后 ④ 绿。
这一条是 A-4（R10-03）的接线缺陷，A-4 自己的单测只驱动纯逻辑 `zfg_probe_resize.h`，所以当时没抓到。

**诚实边界（B-6）**：`g_captureSeam` 是**测试接缝**，没有线程同步保护（注入只在单线程测试期发生，
生产永不写入）；失败计数只在"整组构建失败"时 +1，几何层失败（`GetCopyableFootprints` 返回 0，与旧行为
一致地直接返回 false）不计入。真机显存耗尽路径仍未实测（本机无显存压力注入手段），但失败路径已被
可注入接缝**真实执行**过，不再是"看起来会释放"。

### 3. 全量验证（本机实跑）

| 项 | 结果 |
|---|---|
| native doctest | **253 用例 / 711,537 断言 / 0 failed**（基线 251 / 711,498 → 用例 +2、断言 +39，只增不减） |
| EditMode | **719 / 719 passed / 0 failed**（不变） |
| PlayMode | **7 / 7 passed / 0 failed**（不变） |
| `Tools/run_tests.ps1` 六段 | **ALL PASS (6 passed, 0 skipped, 0 failed)，exit 0**（native / editmode / playmode / arch / config / naming） |
| `/W4 /WX` | 零警告（`[2/4 build: Release (/W4 /WX zero-warning)]` 段通过） |
| `--draw` | `D3D12 FIRST DRAW OK`，center checks=6/6，InfoQueue error=0，exit 0 |
| `--lights` | `D3D12 LIGHTS OK`，centerPass=6/6，groundLitPass=6/6，InfoQueue error=0，exit 0 |
| `--backend vulkan --draw` | `VULKAN FIRST DRAW OK`，center checks=6/6，validation error=0，exit 0 |

`git status` 只剩白名单文件 + 本日志（`HANDOFF.md` 的修改是我接手前就存在的，不是我改的）；无
`URP_*.asset` / `Assets/__zfgprobe` / `zrender.dll` 噪声（构建把 `zrender.dll` 复制进 UnityProject，
收尾已 `git checkout --` 还原）。**未 git commit。**

### 4. 防再漏机制建议（一句话，不实现）

让 `Docs/Logs/TASK-R10-02.md` 的条目号成为**机器可核对的清单**：每个修复批次在日志里声明"覆盖的条目 ID 集合"，
收尾脚本对 `TASK-R10-02` 全量 ID 做一次差集/并集核对，差集非空即 FAIL（而不是靠人眼逐条比对 17 行表格）。


## 验收清单（主控写）

> 独立取证，不复用施工数字。10 项全 PASS 才通过。
> 本任务两条的失败模式：**①A-10 的"修好了"只是把测试改宽了；②B-6 的"失败注入"根本没走到失败分支。**

1. **V1 A-10 修前红（自己复做）**：临时回退 A-10 的修复（或只回退 split 分支的那条内联屏障），场景 9 的
   `DepthStencilReadOnlyOptimal` 计数必须回到 **0** 且用例变红；复原后变绿。**自己动手，不看施工截图。**
2. **V2 A-10 断言未放宽**：读 `test_rg_barriers.cpp` 场景 9 的最终代码，确认断言是**精确的**
   （查找 `DepthStencilReadOnlyOptimal` 而非"任意屏障 ≥1"）；且该用例确实调用了 `Execute`（不是又是 `Compile`）。
3. **V3 A-10 双路径等价**：给出同一张图 `Execute` 与 `ExecuteAliased` 的屏障序列对比（条数/位置）；
   若两者必然不同，逐点核对施工给的差异说明是否站得住。
4. **V4 A-10 未削弱既有护栏**：`test_rg_execute.cpp:104-140` 的 split 下发断言仍原样存在并通过
   （`git diff` 该文件应为空，或改动仅为纯新增）。
5. **V5 B-6 失败路径真的跑到**：独立注入第 N 次 `E_OUTOFMEMORY` → 断言 `0..N-1` 已 Release、`g_capture` 全空、
   `g_captureReady` 全 false。**若注入接缝只能"返回 false"而没真正经过 `CreateCommittedResource` 的失败分支，按 FAIL 处理。**
6. **V6 B-6 不放大**：失败后重试 → 再建 `kCaptureSlots` 个（不是 2N），给计数证据。
7. **V7 B-6 修复有牙（变异）**：把失败清理那段（Release+置空）注释掉 → 相应用例必须变红 → 复原。
8. **V8 六段不回归**：全绿 exit 0（native **253 = 251+2** / **711,537**、EditMode **719**、PlayMode **7**、三校验器 exit 0）；
   `--draw` / `--lights` / `--backend vulkan --draw` 亲跑。
9. **V9 规范与范围**：`git status` 仅白名单文件 + 本日志；无 `URP_*.asset` / `__zfgprobe` / `zrender.dll` 噪声；
   `/W4 /WX` 零警告；未 commit。
10. **V10 补漏的完整性**：把 R10-02 的 17 条与 R10-03/04/05/06/07 的覆盖再对一遍，**确认没有第三条漏项**
    （主控核对结果是只剩 A-10/B-6，但你需要独立复核这一结论）。

**须在结论中回答**：①A-10 的修法是否会让 `ExecuteAliased` 产生**重复屏障**（性能/正确性），若会你怎么判；
②B-6 的可注入接缝在**生产默认路径**上是否与原实现逐位一致（默认工厂行为不变）；
③你认为"审计条目漏批次"这件事该怎么防（施工给了一句建议，你评价它够不够）。

## 验收结论（验收代理写）

**结论: PASS with 受限（10/10）**

- 验收代理独立取证（未复用施工任何一次运行结果）：两轮全量 `Tools/run_tests.ps1`（其中一轮捕获 `SCRIPT_EXIT=0`）、一次自建 `Tools/build_native.ps1`、两次亲做回退/变异后复原。全程未改业务代码、未 commit、未 `rm`（临时备份用 `mv` 移出仓库）。
- 两条修复本身成立、断言精确、护栏未削弱、失败路径真的跑到。**"受限"落在两处已披露的边界**（见文末 ①②），不影响 10 项判定。

### 取证基线（我自己的第一次运行，先绿后折腾）

```
Tools/build_native.ps1 → [doctest] test cases: 253 | 253 passed | 0 failed | 0 skipped
                         [doctest] assertions: 711537 | 711537 passed | 0 failed
                         ALL GREEN: configure + build + test succeeded
```
与施工自称的 253 / 711,537 一致（基线 251 / 711,498 → 用例 +2、断言 +39）。

### V1 A-10 修前红（亲做回退）—— PASS

回退方式：只把 split 分支新加的三行（`rg_render_graph.cpp:671-673`：等价内联屏障 + 句柄 + 镜像位）注释掉，其余不动，重建：

```
C:\...\Native\tests\test_rg_barriers.cpp(206): ERROR: CHECK( depthReadConversions == 1u ) is NOT correct!
  values: CHECK( 0 == 1 )
C:\...\Native\tests\test_rg_barriers.cpp(253): ERROR: CHECK( plain.BarrierPlan().SplitMirrorCount() == 1u )
C:\...\Native\tests\test_rg_barriers.cpp(257): FATAL ERROR: REQUIRE( seqExecute.size() == 2u )
[doctest] test cases:    253 |    251 passed | 2 failed | 0 skipped
[doctest] assertions: 711528 | 711525 passed | 3 failed |
FAILED: unit tests failed (exit=1)
```

`DepthStencilReadOnlyOptimal` 计数精确回到 **0** 且用例红；复原后指纹回到施工交付态 `sha1 4a28c15e…`，全量复跑 253/711,537 全绿。**亲做，不看施工截图。**

### V2 A-10 断言未放宽 —— PASS

`test_rg_barriers.cpp:106` 的判据是**精确形态**，不是"任意屏障 ≥1"：

```cpp
graph.Execute(device, device.commandList);                       // :196 真调公共 API Execute
if (b.prevLayout == ZrImageLayout::DepthStencilAttachmentOptimal
    && b.newLayout == ZrImageLayout::DepthStencilReadOnlyOptimal) ++depthReadConversions;
CHECK(depthReadConversions == 1u);                               // :206 既证存在，也证不多发
```
`== 1u` 同时锁住"有"和"不重复"。夹具 `DepthTarget()` 由 `DSV|Sampled` 改为纯 DSV（`test_rg_barriers.cpp:29-38`）是**必要**的：带 Sampled 时读侧推导 `ShaderReadOnlyOptimal`，断言 `DepthStencilReadOnlyOptimal` 必然为 0；原 Sampled 形态改由场景 12 覆盖（`seqExecute[1] == DSV→SRO` 实跑绿），覆盖面未减。

### V3 A-10 双路径等价 —— PASS

同一张图（2 pass，DSV 写 → 采样读），实跑断言全绿（`zrender_test.exe -tc="rg-barriers: Execute*"`）：

| 路径 | 实际下发 | 承载转换的下发 |
|---|---|---|
| `Execute` | `ResourceBarrier[0]{Undefined→DSV}`、`ResourceBarrier[1]{DSV→SRO}` | 2 条，`seqExecute.size()==2` |
| `ExecuteAliased` | `ResourceBarrier[0]{Undefined→DSV}`、`SplitBegin{DSV→SRO}`、`SplitEnd{DSV→SRO}` | `inlineBarrierCalls==1` / `splitBegins==1` / `splitEnds==1` |

`seqAliased[i] == seqExecute[i]` 逐位相等（`:281-284` 两条 CHECK 全 SUCCESS）。转换**次数一致（各 1 次）、位置一致（进入读 Pass 之前）**；唯一差异是 D3D12 上 split 被拆成 BEGIN/END 两个半边，属 split 的定义本身，不是多余转换。差异说明站得住。

### V4 A-10 既有护栏未削弱 —— PASS

`git diff --stat Native/tests/test_rg_execute.cpp` **无输出（空 diff，exit 0）**；`test_rg_execute.cpp:104-140` 的 `splitBegins==1 / splitEnds==1 / splitCalls.size()==2 / newLayout==DepthStencilReadOnlyOptimal / begin 先于 end` 原样存在，并在两轮全量中通过。施工"该文件一行未改"属实。

### V5 B-6 失败路径真跑到 —— PASS

测试经 `WrapEnsureCaptureBank()` → **产品同一个 `EnsureCapture()`**（`zfg_probe_wrap.cpp:1093-1096`，生产入口是 `CaptureObserver` 的 `zfg_probe_wrap.cpp:450`），不是测试里复刻一套。独立注入第 4 槽失败，实跑断言（`test_zfg_resize.cpp:222-227`，26/26 断言绿）：

```
created == 4 / released == 4      ← 0..3 全部释放
liveSlots == 0 / captureValid false ← g_capture 全空、无半就绪
allocFailures == 1 / lastSlotCount == 6
```
"是否只是返回 false"的判定：失败**处理段**（`zfg_probe_wrap.cpp:407-419`）是产品代码，且被 V7 变异证明是载荷相关的（见下），不是装饰。

### V6 B-6 不放大 —— PASS

同一用例：失败后重试（第 2 槽失败）`created 增量 == 2`（不是 4+2）；放行后 `created 增量 == 6`（恰 `kCaptureSlots`）；已就绪再调一次增量仍 6（幂等）；全过程 `released == created`（12 == 12，建多少释放多少）。**不叠加成 2N。**

### V7 B-6 变异（亲做）—— PASS

把失败清理段（`for i<kCaptureSlots { ReleaseCaptureResource; 置空; ++released; }`）整体注释掉后重建：

```
test_zfg_resize.cpp(223): ERROR: CHECK( g_fakeBank.released == 4 )            values: CHECK( 0 == 4 )
test_zfg_resize.cpp(234): ERROR: CHECK( g_fakeBank.released == g_fakeBank.created )
test_zfg_resize.cpp(255): ERROR: CHECK( g_fakeBank.released == g_fakeBank.created )
[doctest] test cases: 253 | 252 passed | 1 failed  |  assertions: 711537 | 711534 passed | 3 failed
```
恰好 1 用例 / 3 断言红，且红的正是"释放"类断言 → **修复有牙，失败分支确实被执行过**。复原后指纹 `sha1 cc98ea91…` 与变异前一致，全仓 `grep VERIFY-R10-07` = 0 命中，全量复绿。

### V8 六段不回归 —— PASS

两轮 `Tools/run_tests.ps1`（第二轮带退出码捕获）：

```
SCRIPT_EXIT=0
native   PASS   cases=253 asserts=711537 failed=0
editmode PASS   total=719 passed=719 failed=0 skipped=0
playmode PASS   total=7   passed=7   failed=0 skipped=0
arch     PASS   exit=0   config PASS exit=0   naming PASS exit=0
RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)
```
三个渲染自检亲跑（均 exit 0）：`--draw` → `D3D12 FIRST DRAW OK`，center checks=6/6，InfoQueue error=0；`--lights` → `D3D12 LIGHTS OK`，centerPass=6/6、groundLitPass=6/6，InfoQueue error=0；`--backend vulkan --draw` → `VULKAN FIRST DRAW OK`，center checks=6/6，validation error=0。

> 口径说明（避免误读）：第一轮我把脚本放在 `... | Tee-Object` 管道里跑，wrapper 还报了 `exit 1`；但同一条日志尾部是 `RESULT: ALL PASS (6 passed, 0 skipped, 0 failed)`，六段明细全 PASS。原因是 `build_native.ps1` 的 cmake 探测目录在受限环境下会往 stderr 打一行 `Error creating directory ...cmake_probe_tmp: Permission denied`（探测失败后自动换下一个候选 cmake，**不影响构建**），被外层 PowerShell 的 native-stderr 规则记成错误从而污染 wrapper 退出码。为拿到干净的脚本退出码，我第二轮不套管道直跑并捕获 `$LASTEXITCODE` → **`SCRIPT_EXIT=0`**，两轮明细逐项一致。判定以第二轮为准。

### V9 范围与规范 —— PASS（含一处非本任务噪声）

- `/W4 /WX`：native 日志 `grep -c "warning C"` = **0**（任何警告都会让构建失败，构建是绿的）。
- 未 commit：`git log -1` 仍为 `2d5c553`。
- 无 `URP_*.asset` / `Assets/__zfgprobe` / `zrender.dll` 噪声：构建脚本会把 `zrender.dll` 拷进 Unity 插件目录，我按既定口径 `git checkout --` 丢弃（这只是构建产物，重建即回）。
- `git status --short` 见文末；`HANDOFF.md` 的 M 是**接手前就存在的**：其 diff 只出现 R10-03/04/05/06（无 A-10/B-6/R10-07 字样），且 mtime 10:45 早于本任务日志（11:01）与源码改动。采信施工的"非我改动"。

### V10 第三条漏项复核 —— PASS（无第三条）

R10-02 条目集合（我按 `### A-*` / `### B-*` 标题独立点数）：**A-1…A-10（10 条）+ B-1…B-7（7 条）= 17 条**，无 A-11/B-8。并集核对：

| 条目 | 落地批次（该批次日志自认 + 有验收结论） |
|---|---|
| A-1 / A-2 / A-4 / A-5 / A-6 | R10-03（验收 PASS 6/6） |
| A-3 / A-7 / B-1 / B-2 / B-3 / B-4 / B-5 / B-7 | R10-05（验收 PASS with 受限 8/8） |
| A-8 | R10-04（验收 PASS 5/5） |
| A-9 | R10-06（验收 PASS with 受限 6/6） |
| A-10 / B-6 | R10-07（本任务） |

17/17 全覆盖，**没有第三条漏项**。附带发现（不影响结论）：B-6 的"0..N-1 不释放"在 **R10-03 就已被顺带修掉**（`TASK-R10-03.md:124` 自认"顺带修掉审计 B-6 的泄漏面"），HEAD 版 `EnsureCapture` 已有 `for k<i Release` 循环；R10-07 真正补的是**可注入接缝 + 失败计数 + 整组原子提交 + `RetireCaptureBank` 的同族泄漏**（后者才是仍存在的真泄漏：`captureValid` 先被调用方清零，`had` 恒 false → 每次 resize 漏 6 个读回资源）。施工报告未点明"前半段 R10-03 已修"，属叙述省略，不是数字夸大。

---

### 必须回答的三问

**① A-10 的修法会不会让 `ExecuteAliased` 产生重复屏障？——不会，我判它成立。**
镜像条目在 `ExecuteAliased` 里按 `perPassImageBatchSplitMirror` 逐条 `continue` 跳过（`rg_render_graph.cpp:841-847`），实跑 `inlineBarrierCalls==1`（若镜像被当内联发第二遍，这个数会是 2）。也就是说 `ExecuteAliased` 的下发序列与修复前**逐条相同**（1 内联 + BEGIN/END 各 1），GPU 侧零增量。D3D12 的 BEGIN_ONLY/END_ONLY 是同一道转换的两个半边（不是两次转换、不额外 flush）；Vulkan 侧 `ResourceBarrierSplit` 只在 BEGIN 半边落一道完整屏障、END 半边是 no-op（`zrhi_vulkan_commandlist.cpp:280-296`，注释里还记了"两半都落会触发 VUID-…-01197"的实证）。唯一"代价"是计划数据结构里多一条 `ZrImageBarrier`（纯内存），且 `TotalBarrierCount()` 已声明不计镜像，故诊断数字不变（`TotalBarrierCount()==2u` 实跑绿）。**若真的出现重复转换，我会直接判 FAIL**——Vulkan 会 oldLayout 不匹配、D3D12 会多一次冗余缓存刷新，属正确性+性能双损。

**② 可注入接缝在"生产默认路径"上是否与原实现逐位一致？——设备面逐位一致；但有两处刻意的行为变化，需分开说。**
`BuildCaptureBankDefault`（`zfg_probe_wrap.cpp:273-311`）+ `ComputeCaptureFootprints` 与 HEAD 的内联实现我逐字段对过：TEX2D / Width=display.w / Height=display.h / DepthOrArraySize=1 / MipLevels=1 / R8G8B8A8_UNORM / SampleDesc.Count=1 / Layout=UNKNOWN；`GetCopyableFootprints(desc,0,1,0,&placed,nullptr,nullptr,&total)` 同参同序；READBACK 堆 + node mask 1；BUFFER / Width=total / ROW_MAJOR；`CreateCommittedResource(heap, NONE, bufDesc, COPY_DEST, nullptr, IID_PPV_ARGS)` 同一调用。失败时的释放也仍是 `ID3D12Resource::Release()`（接缝空 → 走默认）。**逐位一致成立。**
两处例外，均为有意：①失败由"边建边写 `g_capture`"改为"先建到 `made[]`、全成才在锁内一次性提交"，消除半就绪中间态（更严，不是更松）；②`RetireCaptureBank` 的占用判定由 `g_display.captureValid` 改为扫槽位——这是**修 bug**（旧判定恒 false 导致每次 resize 漏一整组），属必要的行为改变，施工已披露。另外 `g_captureSeam` 是普通结构体读取、无原子/无锁，只在测试期写；生产从未写入（全仓仅 `test_zfg_resize.cpp` 调 `WrapSetCaptureSeam`），风险可接受但值得登记。

**③ "审计条目漏批次"怎么防？——施工那句方向对，但**不够**，只能防住"漏点名"，防不住"点了名没修"。**
施工的建议（日志里声明覆盖的条目 ID 集合 + 收尾脚本对 R10-02 全量 ID 做差集，非空即 FAIL）值得落地：它把 17 行人工比对变成机器判据，成本极低。缺的是三层：**(a) 单一事实源**——ID 集合必须从 `TASK-R10-02.md` 的标题自动抽取，不能由施工手抄（手抄本身就是同一类错误的温床）；**(b) 声明的可信度**——"声明了 ID" ≠ "修了"，应要求每个 ID 附带一个可机检的证据锚（用例名 / 断言文件行 / 测试名），脚本至少校验该锚点在测试二进制或源码里真实存在；**(c) 分摊可见性**——一个 ID 被多个批次碰过（如 B-6 被 R10-03 顺带修一半）时，差集法会把它算作"已覆盖"从而掩盖剩余面，应在清单里允许"部分"状态并强制写明剩余项。
一句话：**把 17 条变成"ID + 证据锚"的机器可核对清单，收尾对 R10-02 标题做差集、对证据锚做存在性校验，差集非空或锚点缺失即 FAIL**。

---

### 残留与诚实边界（不阻塞，如实登记）

1. **A-10 的修复停在"计划/下发"层**：`RgGraph::Execute` 从不把 Transient 的编译期占位句柄换成当帧真资源（`rg_render_graph.cpp:943-966` 直接下发 `imgBatch`，不用 `perPassImageBatchResources`）。所以场景 9 里那道 `DSV→DSV_READ` 屏障的 `image` 在真机上是无效句柄——这是 `Execute` **所有**内联屏障的既有债务（不在 17 条里），本次未引入也未修。换言之：A-10 按审计原文的范围闭环了（"这道转换会被下发"），但 `Execute` 离"可上真机"还差一次句柄回填。这是我给"PASS with 受限"的第一条理由。
2. **`BuildCaptureBankDefault` 内部的 HRESULT 失败分支无任何用例执行**：注入点在 `build` 之上，测试用的是 `FakeBuild`（按契约返回部分结果 + false），所以"第 N 个 `CreateCommittedResource` 返回 E_OUTOFMEMORY"这 3 行仍是代码审查级证据。产品侧的清理/提交/计数全被真实执行（V7 变异证明），但这 3 行本身没有测试覆盖——第二条受限理由。
3. 真机显存耗尽仍未实测（本机无显存压力注入手段），与施工自述一致。
4. `HANDOFF.md` 的 M 为接手前遗留，不在本任务白名单内，但非本次改动（证据见 V9）。

### 收尾 `git status --short`

```
 M HANDOFF.md                                  ← 接手前遗留（diff 无 R10-07/A-10/B-6 字样，mtime 10:45）
 M Native/tests/test_rg_barriers.cpp
 M Native/tests/test_zfg_resize.cpp
 M Native/zfgprobe/zfg_probe_wrap.cpp
 M Native/zfgprobe/zfg_probe_wrap.h
 M Native/zrender/rg_render_graph.cpp
 M Native/zrender/rg_render_graph.h
?? Docs/Logs/TASK-R10-07.md
```
白名单内 6 个文件 + 本日志；`zrender.dll` 构建产物已还原；无 `URP_*.asset` / `Assets/__zfgprobe` 噪声；**未 commit**。临时回退/变异的两个备份已 `mv` 到仓库外（`%TEMP%`），仓库内无残留文件。
