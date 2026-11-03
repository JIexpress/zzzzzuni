# TASK-B-R3-01 手写 RenderGraph：声明/编译（排序/死Pass剔除/别名）/屏障规划（含合并）

状态: doing | 派发: 2026-10-23 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：`Native/zrender/` 实现手写 RenderGraph 的**纯逻辑层**（不接真后端）：Pass 声明与依赖、编译（拓扑排序+死 Pass 剔除+临时资源生命周期+内存别名复用计划）、**屏障规划与合并**（只在真实状态变化处发屏障、同性质合并、只读共享）。执行层经 `IZrDevice/IZrCommandList` 接口走——**用 Mock 设备单测**，真 D3D12 执行是下一任务。

**设计要点**：
1. 类型（zrender 模块，rg_ 前缀）：
   - `RgHandle{uint32 index, gen}` 资源句柄；`RgResourceDesc{type: Transient|Imported, name, ZrImageDesc 或 buffer desc}`——Imported=外部传入（如后台缓冲），Transient=图内临时
   - `RgPass`：名称、读写声明（资源句柄+访问类型：Read（SRV/只读）/Write（RTV/UAV/DSV 写）/Present）；执行回调 `std::function<void(RgContext&)>`；RgContext 可按句柄解析到实际设备资源（编译后）
   - `RgBuilder`（Record 阶段 API）：`CreateTransient(desc)` / `Import(texture)` / `AddPass(name, setup, execute)`；setup 里声明 `Read(handle)/Write(handle)`
2. 编译（`RgGraph::Compile`）：
   - 依赖：资源唯一写入 Pass → 读该资源的 Pass 建边（WAW 也建边，按声明顺序）；拓扑排序（稳定：同层按声明序）
   - **死 Pass 剔除**：从"根"（写 Imported 资源或被显式标记 Root 的 Pass）反向可达性，不可达剔除（剔除后重算生命周期）
   - **生命周期区间**：每 Transient 资源 first-use..last-use（pass 序号区间）
   - **别名计划**：区间不重叠的 Transient 资源共享同一别名池槽（输出 `RgAliasPlan`：resource→aliasSlot）；重叠必须异槽（断言/测试）
   - **未声明使用拒绝**：execute 阶段查句柄若 setup 未声明 Read/Write → 编译期报错（RgContext 解析时校验）
   - 环检测：声明顺序自环/重复写（多 Pass 写同资源且无序）给出明确错误码
3. 屏障规划（`RgBarrierPlanner`，核心）：输入编译后的 pass 序+每 pass 的资源访问（layout+access+stage，用 B-R2-01 的 Zr 枚举）：
   - 每资源维护"当前状态"，逐 pass 推进：仅当本 pass 访问与当前状态不兼容（layout 变化或 access 冲突）才产出屏障（`ZrImageBarrier`）
   - **只读共享**：连续多个 pass 同一 ShaderReadOnly 访问→中间零屏障（合并为一个进入屏障）
   - **同性质合并**：同一 pass 前多个资源需相同 srcStage→dstStage 屏障时合并为一次批量提交（输出按 pass 分组的 `ZrImageBarrier` 数组）
   - **split barrier**： planner 输出 begin/end 对（`RgSplitBarrier`）——被后一个 pass 读的 DSV 写，begin 提前到写完成后立刻发、end 延到读之前（本期输出计划即可，执行由下任务）
   - 输出屏障总数供测试断言（合并效果可量化）
4. 执行（`RgGraph::Execute(IZrDevice&, IZrCommandList&)`）：按编译序：发该 pass 的屏障批次（经 commandlist->ResourceBarrier）→ 调 execute 回调（RgContext 提供句柄→IZrImageView/Buffer 解析，未声明访问返回 null 并记错）
5. **Mock 设备/命令表**（tests 内）：实现 IZrDevice/IZrCommandList 最小子集，命令表记录所有调用序列（CreateBarrier 调用列表/顺序）；不依赖 D3D12
6. doctest 单测（`tests/test_rg_compile.cpp`、`test_rg_barriers.cpp`）至少覆盖：
   - 拓扑序：B 读 A 写的资源 → A 先执行（记录 execute 顺序断言）
   - 死 Pass：写无人读的 Transient 且不 Root → 剔除（execute 回调不触发）
   - 别名：两 Transient 生命周期不重叠 → 同槽；重叠 → 异槽
   - 屏障最小化：同资源连续 3 个只读 pass → 恰 2 个屏障（Undefined→ReadOnly、末尾→Present，若有）或按场景断言具体数
   - 只读共享：中间零屏障断言
   - 同性质合并：一 pass 前两资源屏障出现在同一次批量调用
   - 未声明使用拒绝：execute 中解析未声明句柄返回 null/错误
   - 环/重复写错误码
7. CMake 挂接 zrender 模块（已有静态库）+新测试；/W4 /WX 零警告

**约束**：只动 `Native/zrender/`、`Native/tests/`、`Native/CMakeLists.txt`、本任务日志；**不跑任何 Unity 命令**；不动 zrhi（只使用其头文件类型）；不 git commit；UTF-8 无 BOM/LF/#pragma once。

**交付物**：RG 三层（Builder/Compile 含别名/BarrierPlanner 含 split）+Mock 设备+≥8 场景单测全绿+施工报告（含屏障合并前后数量对比示例）。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理 | 2026-10-23

### 1. 交付物

| 文件 | 内容 |
|---|---|
| `Native/zrender/rg_render_graph.h` | RG 全类型：RgHandle / RgResourceDesc(Transient\|Imported 工厂) / RgPass(Read/Write/Present/MarkRoot) / RgBuilder 语义(CreateTransient/Import/AddPass) / RgAliasPlan / RgSplitBarrier / RgBarrierPlan / RgError / RgContext / RgGraph(Compile/Execute) |
| `Native/zrender/rg_render_graph.cpp` | 编译管线（建边→环检测→死剔除→稳定拓扑→生命周期/别名→屏障规划）+ Execute（Transient 创建/销毁经 IZrDevice，屏障批次经 IZrCommandList::ResourceBarrier 单次批量提交） |
| `Native/tests/rg_mock_device.h` | Mock 设备/命令表：全 IZrDevice/IZrCommandList 接口最小实现，自增占位句柄；MockCommandList 记录屏障批量调用序列与全命令序列 |
| `Native/tests/test_rg_compile.cpp` | 7 场景（拓扑序/稳定序/死剔除/别名双向/未声明拒绝/环错误码/NoRoots） |
| `Native/tests/test_rg_barriers.cpp` | 5 场景（3 连读恰 2 屏障/同性质同批/split 计划/全链路 4 屏障/WAW 保序） |
| `Native/CMakeLists.txt` | zrender_core 增 rg_render_graph.cpp；zrender_test 增两个测试文件 |

### 2. 屏障合并前后数量对比（真实断言数字）

- **3 连读 + Present**（test_rg_barriers 场景 7）：朴素逐访问 = 4 道屏障（每次访问各一道）→ 合并后 **2 道**（Undefined→ShaderReadOnly 进入首读 + ShaderReadOnly→Present）；中间两个读 Pass **零屏障**（只读共享）。
- **写-读-读-写-呈现**（场景 10）：朴素逐访问 = 5 道 → 合并后 **4 道**（逐 Pass 分布 1/1/1/0/1，读-读共享处为 0）；执行层恰好 4 次 ResourceBarrier 批量调用。
- **双资源同性质**（场景 8）：两资源各自转换仍需 2 道屏障，但**合并进同一次 ResourceBarrier 调用**（imageBarrierCount == 2，单次批量提交断言）。
- **split barrier**（场景 9）：DSV 写→读输出 begin/end 对（beginAfterPass=0、endBeforePass=1，DSAOptimal→ShaderReadOnly、LateFragmentTests→FragmentShader），不占内联批次。

### 3. 核心实现要点（验收行号参考 rg_render_graph.cpp）

- 屏障只在真实状态变化处产出：`PlanBarriers()`（约 490-580 行）——`layoutChanged || accessConflict` 才产出；`ZrAccessCompatible`（读-读兼容）保证只读共享零屏障；同 pass 全部屏障进同一批次（per-pass vector，Execute 单次提交）。
- 别名区间算法：`ComputeLifetimesAndAlias()`（约 520-570 行）——first-fit 槽分配，槽末尾 lastUse **严格小于** 新区间 firstUse 才复用（端点相接视为重叠 → 异槽）。
- 环检测：`BuildEdgesAndDetectCycle()` 全图 Kahn，`sorted != passCount` → `RgError::CycleDetected`；拓扑稳定 = ready 集合每次取声明序最小者。
- 死剔除：根 = 写 Imported 资源 ∪ MarkRoot，沿前驱反向回溯，不可达者剔除后重算生命周期。
- 错误码：None/CycleDetected/InvalidHandle/NoRoots；未声明使用在 RgContext::GetImage/GetBuffer 拒绝（无效句柄 + 计数，图级 LastExecuteUndeclaredUses 汇总）。

### 4. 自测结果

- `Tools/build_native.ps1` **ALL GREEN**（configure + build + test，/W4 /WX 零警告）
- doctest：**57 用例全过，7232 断言全过**（本次新增 12 用例：编译层 7 + 屏障层 5）
- zrhi 零改动（git status 无 Native/zrhi/ 条目）；未跑任何 Unity 命令；未 git commit

### 设计偏差与说明

1. **读-写边语义**：采用"资源的**每个**写 Pass 先于每个读 Pass"（读见最新写的保守建边），而非"仅最后声明写者"。保守但正确，拓扑序测试断言的 PostWrite 先于 Sample0 即此语义的体现。
2. **多写无序**：多 Pass 写同资源按声明序连 WAW 边保序；"无序重复写"经读旁路成环时统一报 `CycleDetected`（未单列错误码）。
3. **split barrier 本期仅计划**：DSV 写→只读的转换不进内联批次、只进 `RgBarrierPlan::splits`（简报第 3 条"本期输出计划即可"）；执行层落地在下任务。因此当前 DSV 写→读场景的 Execute 命令流无该同步（已注释说明）。
4. **显式根保活**：死剔除只保留根的祖先链——不通向任何根的纯读 Pass 需 MarkRoot 保活（测试中的纯读链均显式标记）。
5. Transient 资源在 Execute 经 Mock 设备 CreateImage/CreateBuffer 物化并回收；别名计划当前仅是槽位数据（真后端内存池复用下一任务）；屏障 POD 中 Transient 资源的 image/buffer 句柄字段在计划里为空，由执行层解析时替换（下一任务）。

### 风险

- `RgGraph::Error()` 命名避开 Win32 `GetLastError` 宏（曾致编译错误，已改）。
- 缓冲屏障走 access 兼容判定（layout 无意义），跨阶段域可见性未细化（与图像同一规则，后续按需收紧）。
- 屏障计划按"整资源"range，子资源级（mip/层窗口）合并留给后续任务。

## 验收清单（主控写）

1. `Tools/build_native.ps1` 全绿（用例数写入结论）
2. 8 场景单测逐项核对为真实断言（读两个 test_rg_*.cpp）
3. 别名正确性：不重叠同槽/重叠异槽两方向断言存在
4. 屏障最小化断言具体数字（如 3 连读恰 N 屏障）；只读共享零屏障断言；split barrier 计划输出存在性
5. 代码审查：屏障只在状态真实变化处产出（planner 核心循环行号证据）；别名区间算法行号
6. zrhi 模块零改动（git status 证明）
7. 文件规范+施工报告完整（含屏障合并数量对比）

## 验收结论（验收代理写）

**验收**: general-purpose 验收子代理 | 2026-10-23 | **PASS**（7/7 项通过）

1. **PASS** — `Tools/build_native.ps1` ALL GREEN（configure+build+test+/W4//WX 零警告+部署 dll）；doctest **57 用例全过 / 7232 断言全过**，与施工报告数字一致。
2. **PASS** — 逐项核对两个测试文件均为真实断言：拓扑序（test_rg_compile.cpp:30-59，执行序 GBuffer 先于 Post）/ 死剔除（:90-117，CulledPasses==[0] 且回调不触发）/ 别名同槽（:120-161）/ 别名异槽（:164-195）/ 屏障最小化具体数字（test_rg_barriers.cpp:44-88，`TotalBarrierCount()==2`）/ 只读共享零屏障（:81-82 `perPassImageBatches[1]/[2].empty()`）/ 同性质同批调用（:140-142 一次 ResourceBarrier 含 2 image）/ 未声明拒绝（compile:198-227，GetImage 无效+计数）/ 环错误码（compile:230-256 `RgError::CycleDetected`）。
3. **PASS** — 别名双向断言均在：不重叠→同槽 `slot1==slot2 && slotCount==1`（compile:151-152）；重叠→异槽 `slot1!=slot2 && slotCount==2`（:193-194）。
4. **PASS** — 3 连读+Present 恰 2 屏障（barriers:72 `TotalBarrierCount()==2u`，且布局/阶段逐字段断言 :75-87）；全链路写-读-读-写-呈现恰 4 道（:225，逐 Pass 分布 1/1/1/0/1 :233-237）；split barrier 计划存在性断言（:175-184，`SplitCount()==1`、begin/end 锚点、LateFragmentTests→FragmentShader）。
5. **PASS** — 代码审查：`PlanBarriers()`（rg_render_graph.cpp:595-690）核心门控在 **:630-634** `layoutChanged || accessConflict` 才产出屏障（:634），否则仅做状态并集推进（:678-681）——只读共享零屏障由此保证；别名 first-fit 区间算法在 `ComputeLifetimesAndAlias()`（**:520-565**），槽复用条件 **:549** `slots[s].lastUse < firstUse[r]`（严格小于，端点相接视为重叠→异槽）。
6. **PASS** — `git status --porcelain` 无任何 `Native/zrhi/` 条目；改动仅 Native/zrender、Native/tests、Native/CMakeLists.txt、本日志（zrender.dll 为构建脚本自动部署产物；UnityProject/Assets/ZFrame 的改动属并行 Track A 任务，与本任务无关）。
7. **PASS** — 文件规范抽查：5 个新文件均无 BOM（首 3 字节 `2f 2f 20`）、头文件均有 `#pragma once`（rg_render_graph.h:18、rg_mock_device.h:7）、CMake 已挂接（CMakeLists.txt:65/115/116）；工作区 CRLF 系 `core.autocrlf=true` 所致（与既有跟踪文件一致），.gitattributes `* text=auto eol=lf` 保证入库 LF。施工报告四要素齐全，含屏障合并前后数量对比（§2：3 连读 4→2、全链路 5→4、双资源同批、split 计划）。

**备注**（不影响 PASS）：读写建边采用"每个写者先于读者"的保守语义、split barrier 仅输出计划不执行、屏障按整资源 range——均为施工报告已声明的合理偏差，与简报"本期输出计划即可"一致。
