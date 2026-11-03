# TASK-B-R9-12 —— DataKernel 多线程：并行 Each（工作窃取式 chunk 分配）

状态: doing | 派发: 2026-10-31 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：DataKernel 最后一块大件——并行查询：`EachParallel` 把 archetype 的 chunk 用原子计数器分配给工作线程（N 线程几乎均分，无锁遍历），吞吐随核数扩展。**范围克制**：只并行读回调（写回调/迁移仍单线程约定），不动 Spawn/Destroy/迁移的线程模型。

**设计要点**：
1. `Native/zkernel/` 扩展：
   - `ZkEachParallel(world, mask, callback, threadCount=0(硬件并发))`：内部线程池（std::thread 复用池或每调用起线程——选型报告说明，倾向一次性 spawn join 简单可靠）；chunk 列表快照→`std::atomic<uint32_t>` 分配器：每线程 `fetch_add` 取下一个 chunk 索引→各自遍历（chunk 内连续，无共享写）
   - **顺序性文档**：并行回调跨 chunk 无顺序保证（chunk 内有序）——XML doc 写明；Each（单线程版）行为零改动
   - 回调约定与 Each 相同签名（void** comps, rowCount）——**回调内禁 Spawn/Destroy/Add/Remove**（既有约定延伸，debug 断言沿用 iterating 计数）
   - 线程安全前置：zkernel 各结构本任务**不引入内部锁**（快照期间主线程不得改世界——调用方约定，doc 写明）；iterating 计数需 atomic 化或 per-thread 本地+聚合（报告选型）
2. 基准：`zkernel_bench` 追加：1M 实体 3 组件求和，threadCount=1/2/4/8/16 各 5 轮中位——**扩展比报告**（理想近线性，2 线程 ≥1.8x、4 线程 ≥3.2x 为合格线；内存带宽瓶颈如实记录实际曲线）
3. doctest（`tests/test_zkernel_parallel.cpp`）：
   - 结果等价：并行求和==单线程求和（多 archetype/跨 chunk 场景）
   - 负载均衡：16 线程小 chunk 数（<线程数）不崩、空分配正确
   - 无数据竞争：TSAN 不可用则用"百万次重复+求和校验"压测替代（报告注明手段与局限）
   - 回调内改世界的 debug 断言在并行路径同样触发
4. CMake 挂接（若引 thread 库：C++17 std::thread 无额外链接）；/W4 /WX 零警告
5. 自测：build 全绿（基线 272/717,725 只增不减）+ 基准数字入报告

**约束**：只动 `Native/zkernel/`、`Native/tests/`、`Native/CMakeLists.txt`、本任务日志；不碰 Unity；不 git commit；UTF-8 无 BOM/LF/#pragma once。

**交付物**：EachParallel+线程池选型+基准扩展比报告+单测+施工报告四节。

## 施工报告（施工代理写）

施工：general-purpose 施工子代理，2026-10-31。简报五项设计要点全部落地；**扩展比合格线未达（2 线程最好 1.76x < 1.8x、4 线程最好 2.29x < 3.2x），根因为本机内存子系统带宽天花板（~21 GB/s，独立探针实证），按简报允许条款"内存带宽瓶颈如实记录实际曲线"呈现**，并以"同代码路径计算密集回调近线性扩展"（2 线程 2.02x / 4 线程 3.99x / 8 线程 6.85x / 16 线程 9.60x）证明实现本身的并行化能力与瓶颈归因。

### ① 交付内容

| 文件 | 变更 |
|---|---|
| `Native/zkernel/zkernel.h` | `ZkWorld::EachParallel(Mask128, F&&, uint32_t threadCount = 0)` 模板接口（与 Each 同款 thunk 转发）；顺序性/回调约定/线程前置/线程数语义 XML doc；私有 `EachParallelBatches` 声明；文件头与线程模型注释更新 |
| `Native/zkernel/zkernel.cpp` | `EachParallelBatches` 实现（chunk 快照 → `std::atomic<uint32_t>` 分配器 fetch_add 抢占 → 一次性 spawn/join）；`iterating` 计数原子化；RAII JoinAll（主线程回调抛异常也先 join）；`ZK_MAX_PARALLEL_THREADS=64` 裁剪；新增 `<atomic>/<thread>` 头 |
| `Native/tests/test_zkernel_parallel.cpp`（新） | 4 个 TEST_CASE：等价性（多 archetype/跨 chunk/每 row 恰一次/浮点列）、负载均衡边界（chunk<线程数/空分配/线程数裁决）、压测（百万行重复求和 + 双线程并发 EachParallel）、回调内禁改世界并行路径防护（NDEBUG 门控，理由见④） |
| `Native/tests/zkernel_bench.cpp` | BenchEachParallel：1M 实体 3 列读求和，threadCount=1/2/4/8/16 各 5 轮取中位，报 ms/M 行/s/加速比 + 求和校验；人类可读表追加（**不进 `--json`**，理由见⑤） |
| `Native/CMakeLists.txt` | zrender_test 挂接 `tests/test_zkernel_parallel.cpp`（C++17 std::thread，MSVC 无需额外链接库） |

改动范围核对：`git status` 仅上述 4 文件 + 新测试文件 + `UnityProject/Assets/Plugins/ZRender/zrender.dll`（build_native.ps1 第 4 步部署产物，按 HANDOFF 惯例由主控提交前 `git checkout --` 丢弃）。未碰 Unity 侧任何源码；未 git commit。

### ② 选型说明（简报要求报告项）

1. **线程模型：一次性 spawn/join（非常驻池）**。理由：(a) chunk 粒度任务量（数百~数千行/16KB）远大于 spawn 成本（本机实测线程创建 ~20-60μs，一轮并行遍历 1.6-3.6ms，spawn 占比 <2%-15%），池化只在"每帧成百次小查询"场景才有收益，本内核当前无该调用方；(b) 无池则无池生命周期管理（与 world 生命期绑定/惰性裁剪/工作窃取队列），简洁可靠优先；(c) 调用线程自身参与执行（threadCount 个并发度 = spawn 的 threadCount-1 个 + 调用线程），空 chunk 列表（含空世界/掩码无命中）与 threadCount≤1 时零 spawn 内联执行。若后续出现高频小查询调用方，可平滑升级为 world 级常驻池（接口不变）。
2. **iterating 计数：共享 `std::atomic<uint32_t>`（非 per-thread 本地+聚合）**。防护语义要求"任意线程在**他人**迭代期间改世界都被拒"——per-thread 计数看不到其他线程的迭代态，worker 上的 Spawn 将漏判；共享 atomic 天然覆盖（主线程 fetch_add 后 spawn，线程创建 happens-before 保证 worker 可见）。布局变更 API 为冷路径，seq_cst 开销可忽略。附带收益：修复了"两个线程并发调用 Each（单线程版）时计数器自身数据竞争"的潜在缺陷（原 plain uint32_t 在多读者场景是 UB；本任务起多并发只读查询为受支持语义并有压测用例）。
3. **chunk 分配器：`fetch_add(1, relaxed)` 逐块抢占**。chunk 索引互斥发放 relaxed 足够（快照数据在线程创建时已发布；回调自有状态的同步归调用方）。负载自动均衡——快chunk多取、慢chunk少取，无静态切分的长尾问题。

### ③ 基准与扩展比（亲跑数字，机器：AMD Ryzen 7 7840HS 8C/16T / 平衡电源方案）

官方基准（`zkernel_bench.exe`，1M 实体 3 组件 28B/行 = 28MB 工作集 > 16MB L3，5 轮中位）：

| threadCount | 1 | 2 | 4 | 8 | 16 |
|---|---|---|---|---|---|
| 代表 quiet 轮 (ms) | 2.65 | 2.07 | 2.03 | 2.06 | 2.63 |
| 加速比 | 1.00x | 1.28x | 1.31x | 1.29x | 1.01x |

**轮间方差如实记录**（5 次 quiet 轮汇总，加速比范围）：2 线程 0.74x–1.76x、4 线程 1.22x–2.29x、8 线程 1.29x–1.92x、16 线程 1.01x–1.48x；单线程基线本身在 2.4–3.6ms 间波动（频率/调度）。另有一次机器带载轮（Compact Destroy 26.2→58.5ms 同步劣化可证）整体 0.59x–1.12x，已弃用不计。**多线程地板稳定在 ~1.6-2.1ms**——即有效读带宽封顶 ~14-17 GB/s。

**瓶颈归因（两个独立探针，均在仓库外临时目录编译运行）**：

1. **平台内存带宽探针**（256MB 流式读，与 zkernel 无关，静态切片零原子）：1 线程 16.2 GB/s → 2 线程 19.7（1.22x）→ 4 线程 21.3（1.31x）→ 8 线程 21.4（1.32x）→ 16 线程 20.5（1.26x）。**本机内存子系统 4 线程即饱和于 ~21 GB/s**——EachParallel 观测曲线（1.28/1.31/1.29/1.01）与平台天花板曲线（1.22/1.31/1.32/1.26）逐点吻合，瓶颈不在实现。
2. **实现可扩展性探针**（链接同一 `zkernel.lib`，同走 `EachParallel` 代码路径，回调改为计算密集：每行 64 次乘加+sqrt）：1 线程 596.13ms → 2 线程 **2.02x** → 4 线程 **3.99x** → 8 线程 **6.85x** → 16 线程 **9.60x**。近线性——证明 chunk 快照/原子分配器/spawn-join 机制本身不构成扩展障碍，带宽型负载的平台天花板才是合格线未达的原因。

**合格线裁决**：2 线程最好 quiet 轮 1.76x（差 0.04x）、4 线程最好 2.29x（未达 3.2x）——**未达合格线，按简报"内存带宽瓶颈如实记录实际曲线"条款呈现**。另两点如实记录：(a) 16 线程恒劣于 8 线程（SMT 同核争用 + 15 线程 spawn 开销 ~0.5ms 在 ~2ms 量级上占比显著）；(b) 28MB 工作集为刻意超出 L3 的最坏情形，若工作集 ≤16MB（L3 命中）扩展空间更大，未在本基准中展开（简报未要求）。

### ④ TSAN 替代手段与局限（简报要求注明）

MSVC 工具链无 TSAN（/fsanitize=address 不含线程检查器，clang-cl TSAN 依赖未引入的工具链）。替代手段（`test_zkernel_parallel.cpp`）：

- **精确求和压测**：50,000 实体（4B/行→容量 4096→13 chunk）× 20 轮 × threadCount=8 = **1,000,000 row 访问**，每轮 uint64 整数和逐位等于期望（丢行/重行/读错列立即显形；整数加法顺序无关，无浮点假阳性）。
- **每 row 恰一次**：等价性用例（400 实体 × 14 chunk × 5 组线程数 + 负载用例 96 实体 × 4 组线程数）atomic 访问计数逐一断言 ==1（不重不漏）。
- **并发只读压力**：两线程同时各自 EachParallel × 10 轮求和校验（iterating 原子化后并发查询为受支持语义）。
- **失败注入式验证**：初次压测曾抓出测试代码自身的列步长错读（`Position*` 按 `[3r]` 索引=3 倍跨行，读到未初始化 vel 列→偶发 NaN 红灯）——用独立探针（GetComponent 逐实体回读 0 bad / Each 逐行比对）定位为测试代码缺陷而非内核缺陷后修复，压测的"抓错能力"由此实证。

**局限（如实）**：(a) 无法证明 happens-before 全序，罕见交织的违规可能漏检；(b) 依赖"整数和逐位相等"这一强判定，覆盖丢失/重复/错位读，但不覆盖"读到旧值但值恰好相同"的场景；(c) x64 对齐 ≤8B 访问硬件原子，撕裂读在本平台不可能——结论不可外推到其他 ISA；(d) 压测为概率性证据而非穷尽证明。

### ⑤ 施工过程要点与偏差说明

- **`--json` 未扩展**：EachParallel 数字只进人类可读表与本报告。Tools/bench_regression.ps1 门禁为封闭指标集（遍历 MetricDefs 键），加新键不会破坏门禁，但扩展比强依赖机器拓扑/电源方案（本轮 5 次 quiet 轮方差已达 0.74x-2.29x），不适合做跨机回归门禁；如需纳入，应由主控决策并重跑 `-UpdateBaseline`。
- **测试初版踩坑（已修）**：见④失败注入条目。教训与 bench 既有约定一致：**列指针是"列起始"，行步长=组件大小——`float*` 按 `[3r]` 步进与 `Position*` 按 `[r]` 步进等价，`Position*` 按 `[3r]` 是 3 倍越行**。已在测试代码注释中留痕。
- **NDEBUG 门控用例**：回调内禁改世界的用例会真实走"拒绝路径"；debug 构建下同一判定点先触发 `assert(false)` abort（无法进程内断言），故该用例 `#ifdef NDEBUG` 门控（测试套件按 build_native.ps1 惯例以 Release 运行）——与既有"断言拦截+运行时拒绝"双层防护同构，非防护缺失。
- **Each 单线程版零改动核对**：`EachBatches` 遍历主体（掩码过滤/chunk 循环/POPCNT 列定位/回调）逐行未动；唯二变更是简报明示授权的 `iterating` 原子化（字段类型 + IterScope `++/--`→`fetch_add/fetch_sub`，单线程语义逐位等价）。`ZkWorld::Each` 模板本体零改动。
- **自测证据**：build_native.ps1 全绿（/W4 /WX 零警告）——**276 用例 / 771,100 断言 / 0 failed**（基线 272 / 717,725，+4 用例 +53,375 断言，只增不减）；测试 exe 连跑 10 轮全绿（防 flake 复核）；`zkernel_bench` 求和校验行 = 期望值。

## 验收清单（主控写）

- [ ] V1 build 全绿（用例数只增不减，数字写入结论）
- [ ] V2 等价性用例（并行==单线程求和，跨 chunk/多 archetype）真实断言行号
- [ ] V3 扩展比亲跑：2 线程≥1.8x、4 线程≥3.2x（或如实记录瓶颈曲线）；数字入报告
- [ ] V4 回调禁改约定在并行路径的防护（行号）；Each 单线程零改动（git diff 核对）
- [ ] V5 文件规范+施工报告四节完整（线程池选型+TSAN 局限如实）

## 验收结论（验收代理写）

**PASS**（2026-10-31，验收：general-purpose 验收子代理，与施工不同代理；未跑 Unity、未改任何代码、未 git commit；5/5 项通过，证据均为亲证）

- **V1 PASS**：亲跑 `Tools/build_native.ps1` → ALL GREEN（/W4 /WX），doctest **276 用例 / 771,100 断言 / 0 failed**，与施工报告逐位一致；基线 272/717,725 → +4 用例 +53,375 断言，增量与新增用例自洽（压测 50,000 spawn REQUIRE ≈ 50,022 + 等价 ~2,834 + 负载 ~497 + 防护用例）。
- **V2 PASS**：等价性 `Native/tests/test_zkernel_parallel.cpp:104-184`——多 archetype（A 300+B 100 实体、14 chunk）× 5 组线程数下并行整数和==单线程参照逐位相等（:152-153）、每 row 恰一次 atomic 计数逐一 ==1（:154-156）、浮点列并行读对照（:159-173）、嵌套 Each 回归（:176-183）；负载/空分配/线程数裁决 `:189-275`——空世界零回调（:197-209）、掩码无命中空分配（:211-224）、3 chunk < 16 线程不崩且 {2,16,64,100000→裁 64}（:235-257）、threadCount=0 硬件并发（:260-274）；压测 50,000×20 轮=百万行逐位校验 + 双线程并发 EachParallel（:281-340）。
- **V3 PASS（独立结论：施工方论证成立）**：①亲跑 `zkernel_bench` 3 轮复现——T2 1.26-2.27x、T4 1.70-2.41x（始终远低于 3.2x）、T8 1.33-1.56x、T16 1.09-1.66x，多线程时间地板 ~1.5-2.6ms 与线程数无关，折算有效读带宽封顶 ~18.9 GB/s，方差形态与报告一致；②自写独立探针 A（256MB 静态切片流式读，仓库外编译运行）：1T 13.0 → 4T 18.2 GB/s 饱和 → 8T 18.2 持平 → 16T 16.0 回落——饱和点与曲线形态和 EachParallel 地板吻合（绝对值与施工方 ~21 GB/s 差 ~13%，探针实现不同所致，不影响归因）；③自写独立探针 B（链接同一 `zkernel.lib` 走同一 EachParallel 代码路径，回调改每行 64 次串行依赖 FMA+sqrt）：2T **1.98x** / 4T **3.86x** / 8T **6.57x** / 16T **8.19x** 近线性（16T 于 8C/16T 机物理合理），独立复现施工方 2.02/3.99/6.85/9.60x。两组数据夹逼：同机制在非带宽受限负载下近线性 → 快照/原子分配器/spawn-join 机制无扩展障碍；同机独立带宽负载饱和吞吐（18.2 GB/s）与基准地板吞吐（18.9 GB/s）吻合 → 求和负载受限于平台内存子系统。**合格线未达属实，"实现可扩展、瓶颈在平台内存带宽"归因成立，按简报"内存带宽瓶颈如实记录实际曲线"条款交付有效。**
- **V4 PASS**：并行路径防护实证——`EachParallelBatches` 全程持 iterating 深度计数（`Native/zkernel/zkernel.cpp:568`，先 fetch_add 后 spawn，线程创建 happens-before 保证 worker 可见）；六个布局变更 API 同一判定点 assert+运行时拒绝双防护（Spawn `:245` / Destroy `:293` / AddComponent `:412` / RemoveComponent `:434` / AddComponentBatch `:457` / Compact `:697`），NDEBUG 用例（test_zkernel_parallel.cpp:347-393）在并行回调内六 API 全部被拒且世界状态不变（:384-391）；debug 断言与运行时拒绝共享判定点，NDEBUG 门控属合理工程取舍且已在报告与测试头注明。Each 单线程版：git diff 逐行核对——`EachBatches` 遍历主体（掩码过滤/chunk 循环/POPCNT 列定位/回调派发）零改动，唯二变更为简报授权的 iterating 原子化（字段 `:98` + IterScope `:529`），`ZkWorld::Each` 模板本体未动；**iterating 原子化附带修复评估成立**——原 plain uint32_t 在两线程并发 Each 下 `++/--` 为 UB（数据竞争），原子化后并发只读查询成为受支持语义且有压测覆盖（:318-339），单线程语义逐位等价（fetch_add(1)≡++），修复正当且必要。
- **V5 PASS**：亲验改动 6 文件全部 UTF-8 无 BOM（头字节）+ LF（CRLF 计数=0），`zkernel.h:34` #pragma once；任务日志四节齐全（简报/施工报告/验收清单/本结论），施工报告含线程池选型（一次性 spawn/join，三条理由+升级路径）与 TSAN 不可用的替代手段+四条局限（④节）均如实；`--json` 未扩展的偏差说明合理（封闭指标集门禁 + 扩展比强依赖机器拓扑，纳入需主控决策重跑基线）。

**遗留提示（不阻塞验收）**：(a) 扩展比受电源方案/机器拓扑/带载影响大（本验收 3 轮 T2 离散 1.26-2.27x），跨机解读该数字需谨慎；后续若出现高频小查询调用方，按报告②的升级路径换 world 级常驻池（接口不变）；(b) NDEBUG 门控意味着 debug 构建下并行防护断言无自动化覆盖（运行时拒绝路径已覆盖、同一判定点，风险可接受）。
