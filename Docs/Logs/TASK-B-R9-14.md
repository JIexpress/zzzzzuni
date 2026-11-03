# TASK-B-R9-14 —— 审计遗留清零（A-10 split 屏障 + B-6 采集泄漏）

状态: doing | 派发: 2026-10-31 | 施工: general-purpose 施工子代理 | 验收: （待填） | commit: （待填）

## 任务简报（主控写）

两条小修复（细节以 Docs/Logs/TASK-R10-02.md 审计原文为准），修完 R10 审计 17 条全闭环。

1. **A-10 `RgGraph::Execute()` 丢 split 屏障**（rg_render_graph.cpp:641-654/:912-946）：
   - 先查事实：`ExecuteAliased`（生产路径，B-R3-02 起）已正确下发 split；`Execute` 是旧接口——**确认 Execute 是否还有调用方**（grep）；若纯遗留→**最小修法**：让 Execute 也消费 `m_barrierPlan.splits`（对齐 ExecuteAliased 语义）或标记 Obsolete 并由测试锁死"Execute 不得静默丢 split"；若仍有调用方→真修
   - **用例**：split 场景下 Execute 路径的 split 下发断言（修前红）——照 test_rg_execute.cpp 先例
2. **B-6 EnsureCapture 中途失败泄漏**（zfg_probe_wrap.cpp:173-183）：第 N 个 CreateCommittedResource 失败时 Release 并置空 0..i-1——**用例**：mock 失败注入→无泄漏（资源计数断言）；重试不放大
3. 自测：build 全绿（基线 276/717,652 只增不减）+ selftest + `--draw`/`--lights` 回归 + `--resize`（VK 重建路径不回归）

## 约束

- 只动 `Native/zrender/`、`Native/zfgprobe/`、`Native/tests/`、本任务日志；不碰 Unity；不 git commit；/W4 /WX；UTF-8 无 BOM/LF
- 变异验证法（改回旧值即红）至少 1 条

## 施工报告（施工代理写）

- **结论**：**本任务是重复派发——A-10 与 B-6 的修复本体、护栏用例、可注入接缝均已在 HEAD**（commit `98d377b`，
  TASK-R10-07，独立验收 PASS with 受限 10/10）。简报中的行号（`rg_render_graph.cpp:641-654/:912-946`、
  `zfg_probe_wrap.cpp:173-183`）是 TASK-R10-02 审计原文的**修前行号**，与 HEAD 代码不对应。本轮按简报完成
  事实核查 + 既有修复评审 + 两条变异验证（均先红后复原）+ 全量自测与回归，**零代码改动**（重复施工=制造同义代码，
  交主控裁决闭环口径）。R10 审计 17/17 闭环状态与 HANDOFF「R10 收口」节一致。

### 0. 重复派发的证据链（先于一切修法讨论）

| 证据 | 内容 |
|---|---|
| `git log -- Native/zrender/rg_render_graph.cpp` | 最新一笔即 `98d377b TASK-R10-07: 补审计漏项 A-10 split 屏障 + B-6 采集资源泄漏` |
| `Docs/Logs/TASK-R10-07.md` | 施工报告落点表与 A-10/B-6 修法、用例行号齐全；验收结论 **PASS with 受限 10/10**（含验收代理亲做的回退/变异） |
| `HANDOFF.md`「R10 收口」节 | 「R10-07 | 补漏项 A-10 split 屏障 + B-6 采集泄漏 | PASS with 受限 10/10（98d377b）」「审计残余：已清零——A-10/B-6 由 R10-07 补齐，17/17 全覆盖」 |
| HEAD 代码 | `rg_render_graph.cpp:667-673` split 分支产等价内联镜像；`zfg_probe_wrap.cpp:396-421` 整组临时数组+失败清理+失败计数 |
| 简报行号来源 | 与 `TASK-R10-02.md` 审计条目原文（`:282/:357`）逐字一致，即修前快照的行号 |

### 1. A-10 决策说明（简报要求项）

**第一步事实核查（grep 全 Native，排除 build/）**：
- `RgGraph::Execute` 的调用方**只有单测**，共 7 处：`Native/tests/test_rg_compile.cpp:55,114,222`、
  `Native/tests/test_rg_barriers.cpp:143,196,255,340`。**生产代码零调用**。
- 生产 RenderGraph 执行全部走 `ExecuteAliased`：演示/窗口程序 `Native/tests/zrhi_d3d12_window.cpp` 14 处
  （:603/:1150/:1884/:2558/:3915/:5125/:6628/:7162/:8769/:9462/:10110/:10762/:12057/:12876）+ `test_ssr_pass.cpp:204`。
- 与简报预判一致：「`ExecuteAliased`（生产路径）已正确下发 split；`Execute` 是旧接口」→ 走"纯遗留→最小修法"分支。

**处置路线决策：保留 HEAD 已有的"镜像内联屏障"修法，不重做、不改 Obsolete**，理由：
1. 该修法就是简报"最小修法"首选形态的等价实现且更强——`PlanBarriers` 的 split 分支在记 `splits` 的同时
   产一条**等价内联屏障**进 `perPassImageBatches`（`rg_render_graph.cpp:667-673`），并用逐条平行的镜像位
   `perPassImageBatchSplitMirror` 标记；`Execute`（`:939-986`，只发批次）**无需理解 splits 就天然正确**；
   `ExecuteAliased`（`:843-848`）跳过镜像、仍以 `BEGIN_ONLY/END_ONLY` 真下发，**split 语义与条数零变化**。
2. 简报的备选"让 Execute 消费 `m_barrierPlan.splits`"反而更弱（须自己保证 begin/end 同 pass、防重复转换）；
   "Obsolete+锁死"路线无收益——`Execute` 有 7 个测试调用方，且修复后它已正确，废弃一个正确的公共 API 只减测试覆盖。
3. 简报要求的用例已在位且真实（行号）：**场景 9** `test_rg_barriers.cpp:155-206`——真调公共 API `Execute`
   （`:196`），断言精确形态 `depthReadConversions == 1u`（`:206`，`DSV→DepthStencilReadOnlyOptimal`，
   既证存在也证不多发，修前该计数为 0）；**场景 12** `test_rg_barriers.cpp:216-285`——双路径等价
   （`SplitMirrorCount()==1` `:253`、`seqExecute.size()==2` `:257`、两路径摊平序列逐位相等 `:281-284`）；
   既有护栏 `test_rg_execute.cpp:104-140`（`splitBegins/splitEnds==1` 等原样在位，R10-07 验收以空 diff 证实）。
4. **本轮变异验证①（亲做）**：把 `:671-673` 镜像三行注释掉重建 → **2 用例/3 断言红**：
   `test_rg_barriers.cpp(206) CHECK(depthReadConversions == 1u)`（0==1）、`(253) SplitMirrorCount`、
   `(257) FATAL seqExecute.size()==2`；`[doctest] 281 | 279 passed | 2 failed`。复原后 `cmp` 逐位一致、全量复绿。
   → **"Execute 不得静默丢 split"的锁死真实有牙。**

**诚实边界（沿 R10-07 验收登记，非本轮引入、简报未要求、未动）**：`Execute` 不把 Transient 编译期占位句柄
回填为当帧真资源（`:959-965` 直接发批次），真机上是无效句柄——这是该路径**所有**内联屏障的既有债务，
不在审计 17 条内；生产零调用，风险被"无人调用"兜底。若后续要现代化 `Execute`，应单独立项。

### 2. B-6：失败注入用例核实（简报要求项）

HEAD 修复完整在位（`Native/zfgprobe/`）：
- **接缝**：`ZfgCaptureSeam{user, build, release}`（`zfg_probe_wrap.h:77-85`）+ `WrapSetCaptureSeam`/
  `WrapGetCaptureDiag`/`WrapEnsureCaptureBank`（`zfg_probe_wrap.cpp:1069-1096`）；生产默认空接缝 →
  `BuildCaptureBankDefault`（`zfg_probe_wrap.cpp:273-311`，第 i 个 `CreateCommittedResource` 失败即
  `*failedSlot=i` 返回 false，`:303-306`）。
- **清理**：`EnsureCapture`（`:363-435`）整组先建到临时数组 `made[]`，失败时 Release 并置空 0..N-1
  （`:404-421`）、失败计数 `g_captureAllocFailures` +1（`:212` 声明，`:414-415` 自增）、`captureValid` 保持
  false（无半就绪）；全成才锁内一次性提交（`:423-431`）。
- **同族真泄漏**：`RetireCaptureBank`（`:331-361`）占用判定扫槽位本身而非被调用方先清零的 `captureValid`
  ——修掉"每次 resize 漏 kCaptureSlots=6 个读回资源"。
- **用例**：`Native/tests/test_zfg_resize.cpp:207-260`（经 `WrapEnsureCaptureBank` 驱动**产品同一个**
  `EnsureCapture`）：①第 4 槽失败→`created==4/released==4`、`liveSlots==0`、`captureValid=false`、
  `allocFailures==1`（:222-227）；②重试只建 2 个不叠加（:230-237）；③放行恰建 6 个、幂等（:240-249）；
  ④retire 后再建失败 `released==created`（:252-258）。
- **本轮变异验证②（亲做）**：把失败清理循环（`:406-413`）注释掉重建 → **恰 1 用例/3 断言红**：
  `test_zfg_resize.cpp(223) CHECK(g_fakeBank.released == 4)`（0==4）、`(234)`、`(255)` released==created；
  `[doctest] 281 | 280 passed | 1 failed`。复原后 `cmp` 逐位一致、全量复绿。

### 3. 自测与回归（全部本机实跑，2026-11-01）

| 项 | 结果 |
|---|---|
| `Tools/build_native.ps1` | **ALL GREEN exit 0**；`[doctest] 281 用例 / 771,162 断言 / 0 failed`；`grep -c "warning C"` = **0**（/W4 /WX）。基线 276/717,652 → 281/771,162，**只增不减**（增量来自 R10-07 之后的其他已闭环任务，非本轮） |
| 用例数口径说明 | 简报点名的 A-10/B-6 用例已存在于 HEAD（见上，行号齐全），本轮**未新增重复用例**——故"只增不减"按 281 ≥ 276 成立，新增数为 0（原因：重复派发，见第 0 节） |
| selftest | `zfg_probe_selftest.exe` exit 0，`zfg probe selftest: OK (0 failure(s))`（含 B-3 关停断言等全 PASS） |
| `--draw` | exit 0，`D3D12 FIRST DRAW OK`，InfoQueue error count = 0 |
| `--lights` | exit 0，`D3D12 LIGHTS OK`，InfoQueue error count = 0 |
| `--resize` | exit 0，`VULKAN RESIZE OK (old=640x480 new=480x360 serial=0→1)`，pre=6/6 post=9/9，failedAttempts=0，validationErrors=0，skipped=0 |

### 4. 范围、规范与交回事项

- **本轮改动文件**：仅本日志（`Docs/Logs/TASK-B-R9-14.md`）。两个源文件经两轮变异后均以 `cmp` 验证
  **逐位复原**（备份/比对在 %TEMP%，仓库内无残留）。
- 未 git commit；未碰 Unity 资产与 .meta；`zrender.dll` 构建部署噪声已按既定口径 `git checkout --` 还原；
  selftest 落在仓库根的 `zfg_probe_selftest.log` 已移出仓库。
- **并行任务提示**：工作区存在 `M Tools/run_tests.ps1`、`.gitignore` 与未跟踪 `Tools/build_player.ps1`、
  `Tools/version.json`、`UnityProject/Assets/Game/Editor/PlayerBuild.cs`——属并行派发的 B-R9-15（构建管线），
  非本任务产物，未触碰。
- **交主控裁决**：①B-R9-14 按本报告判"重复派发——独立验证后闭环"（与 R10-07 验收结论一致），或如有其他
  顾虑（如 R10-07 的两条"受限"边界）请另立任务并写明范围；②简报行号照抄审计原文的取材方式建议改为
  以 HEAD 代码现状为准，避免下一次重复派发。

## 验收清单（主控写）

- [ ] V1 build 全绿（用例数只增不减，数字写入结论）
- [ ] V2 A-10：调用方事实核对（grep 证据）+ 修法评审 + 用例真实（行号）；变异复做 1 次
- [ ] V3 B-6：失败注入用例真实（行号）
- [ ] V4 回归亲跑：`--draw`/`--lights`/`--resize`
- [ ] V5 文件规范+施工报告四节完整（A-10 处置路线的决策说明）

## 验收结论（验收代理写）

（待填）
