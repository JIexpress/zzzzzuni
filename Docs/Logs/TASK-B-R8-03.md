# TASK-B-R8-03 DataKernel 架型迁移（Add/RemoveComponent）+ chunk 收缩

状态: doing | 派发: 2026-10-27 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：数据内核运行时缺口补齐——B-R1-01 的 World 只有 Spawn(固定 mask)/Destroy，实体**无法在运行时增删组件**（无 archetype 迁移）。本任务：`AddComponent/RemoveComponent`（跨 archetype 搬行+数据保留/丢弃）、chunk 收缩（ Destroy 空洞回收）、压力基准。

**设计要点**：
1. `Native/zkernel/` 扩展（保持既有风格，B-R1-01 先读）：
   - `ZkAddComponent(world, entity, compId, const void* initData)`：目标 archetype=原 mask|bit → 若新 archetype 无 chunk 则建；**搬行**：旧列 memcpy 保留数据、新列写 initData（null 则零初始化）→ 旧 chunk swap-back 收行 → 实体行号/位置更新（含 chunk 索引表）
   - `ZkRemoveComponent(world, entity, compId)`：同理 mask&~bit，数据丢弃
   - 批量接口：`ZkAddComponentBatch(entity 列表)`（减少 per-call 查找——选做，报告说明）
   - **迭代安全**：迁移发生在 Each 遍历中的约定与防护（v1 单线程：文档注明回调内禁迁移+debug 断言钩子，与既有"回调内禁 Spawn/Destroy"同策）
   - **chunk 收缩**：Destroy 后空闲行比例 >50% 的 chunk 与相邻 chunk 合并回收（_aligned_free），保持"每 archetype chunk 链"不变量；`ZkCompact(world)` 显式整理
   - 迁移成本统计：`ZkWorldStats{migrations, rowsMoved, bytesMoved}`（基准用）
2. doctest（`tests/test_zkernel_migrate.cpp` 新文件）：
   - Add：数据保留（旧组件值迁移后可读）、新组件零/initData、mask 更新、查询立即命中新 archetype
   - Remove：数据丢弃、查询剔除
   - 跨 chunk 迁移（源/目标 chunk 满场景）；swap-back 后其他实体数据完整性
   - 连续 Add/Remove 1000 次同一实体（archetype 撕裂防护）——stats 计数正确
   - 收缩：大段落 Destroy 后 Compact 回收（chunk 数下降断言）、每次迭代仍正确
   - 代际/句柄在迁移后仍有效（同实体句柄读新组件）
3. 基准：`zkernel_bench` 追加场景：1 万实体各 Add/Remove 3 次（迁移吞吐 rowsMoved/s）+ 100 万实体 Destroy 一半后 Compact 毫秒
4. CMake 挂接；/W4 /WX 零警告

**约束**：只动 `Native/zkernel/`、`Native/tests/`、`Native/CMakeLists.txt`、本任务日志；不碰 Unity；不 git commit；UTF-8 无 BOM/LF/#pragma once。

**交付物**：迁移+收缩+stats+单测+基准+施工报告四节。

## 施工报告（施工代理写）

### 一、交付物与改动范围

| 文件 | 改动 |
|---|---|
| `Native/zkernel/zkernel.h` | 新增 `ZkWorldStats{migrations,rowsMoved,bytesMoved}`（:126）；`ZkWorld::AddComponent/RemoveComponent/AddComponentBatch/Compact/Stats/ChunkCount` 声明与契约注释；`Each` 迭代禁令更新（回调内禁 Spawn/Destroy/Add/Remove/Compact，:182-184）；私有 `MigrateComponent` 声明 |
| `Native/zkernel/zkernel.cpp` | 迁移核心 `MigrateComponent`（:300-386）；公共收行 helper `RemoveRowSwapBack`（:97-119，Destroy 重构共用，行为等价）；`Compact`（:540-561）；迭代防护 `iterating` 计数 + `IterScope` RAII（:499-506）+ 六处守卫（Spawn :224 / Destroy :272 / Add :391 / Remove :413 / Batch :436 / Compact :543） |
| `Native/tests/test_zkernel_migrate.cpp` | 新文件，10 个 TEST_CASE（见三节清单） |
| `Native/tests/zkernel_bench.cpp` | 追加场景 c) 迁移吞吐 + d) Compact 回收（`BenchMigrate`/`BenchCompact`） |
| `Native/CMakeLists.txt` | zrender_test 挂接 test_zkernel_migrate.cpp |

简报 API 名映射：`ZkAddComponent(world,entity,compId,initData)` ⇒ `ZkWorld::AddComponent(entity,compId,initData)`、`ZkCompact(world)` ⇒ `ZkWorld::Compact()`（B-R1-01 既定 C++ 类接口，world 为隐式 this；唯一 C ABI 导出仍只有 ZK_GetVersion，不跨 DLL ABI）。`ZkWorldStats` 结构名与简报一致。

### 二、设计要点落位（对照简报 5 条）

1. **搬行数据保留/丢弃**：目标 archetype = 原掩码 ± bit（无则建）。共享列**逐列直接 memcpy**（SoA 行内跨列不连续，列寻址拷贝）；Add 新列写 initData（null 则 memset 零）、Remove 被删列**不迁移**（zkernel.cpp:351 有 skip，详见四节的 bug 记录）。全部 170 回归用例绿 = Destroy 重构未破坏既有语义。
2. **swap-back 与索引一致性**：`RemoveRowSwapBack`（:97-119）单点实现——尾行各列搬入洞行（跨 chunk 正确）+ `rowEntity[row]=movedSlot` + `rowOf[movedSlot]=row` + `count--`；迁移路径调用后统一更新 `archOf/rowOf`（:378-379），代际不变（迁移后原句柄仍有效，test :439 用例钉住）。**注意 CreateArchetype 会 push_back 致 archetypes 向量重分配——src/dst 引用绑定放在解析完成之后（:330 注释）**。
3. **撕裂防护（迭代安全）**：与既有"回调内禁 Spawn/Destroy"同策并加固——`EachBatches` 期间 `iterating` 计数（RAII 防回调抛出泄漏），布局变更 API debug 构建 `assert` 拦截 + **所有构建运行时直接拒绝**（返回 false/0，世界状态不变）；Spawn/Destroy 一并纳入守卫（此前仅文档约定）。test :176 钉失败路径状态不变。
4. **chunk 收缩语义（如实说明推导）**：行紧凑不变量下（Spawn 追加/swap-back 收行），空闲行**恒为 chunk 链尾后缀**，链最小 chunk 数 = ceil(count/容量) 是数学下界。故"空闲 >50% 的 chunk 与相邻合并"物理上可实现为且仅为**释放全空尾 chunk**（空闲 100%>50%）；部分占用的尾 chunk 无法并入前驱（前驱恒满，无空位）。`Compact()`（:540-561）按 ceil 下界释放：空 archetype 全释放（archetype 本体与掩码注册保留复用）、`rowEntity` 同步缩 size（不缩 capacity，复用免重分配）。**Destroy 不自动收缩**（chunk 保留供复用防高频增删抖动；且简报测试/基准的观察点均在 Compact——若 Destroy 即时收缩，"Destroy 一半后 Compact 毫秒"将退化为空操作）。test :356 断言 chunk 数 4→1→0 下降与全释放。
5. **迁移成本统计**：`ZkWorldStats`（header :126）+ `Stats()`；计数口径——migrations 仅 Add/Remove（含批量、不含 Destroy），rowsMoved = 迁移行 + 迁移 swap-back 收行，bytesMoved = 迁移路径 memcpy/memset 字节总和；Destroy 不计入。test :612 **逐字节会计断言**钉死口径（28B、40B 精确值）。

**批量接口（选做）已交付**：`AddComponentBatch(entities,count,compId,initData)`——compId 全批单次校验；目标 archetype 解析带 1 项缓存（连续同源实体免重复线性查找，archetype 下标终生稳定故缓存不失效）；失效实体跳过不中断批次，返回成功数。initData 为同一缓冲逐实体拷入新列。

### 三、自测结果

- **build_native.ps1 全绿**：`180 用例 / 710,633 断言 / 0 failed`，/W4 /WX 零警告（基线 170/705,271，只增不减：+10 用例 / +5,362 断言）。
- 新增 10 用例对位简报五类场景：

| 场景 | TEST_CASE（test_zkernel_migrate.cpp 行号） |
|---|---|
| Add：数据保留/零或 initData/mask 更新/查询立即命中 | :63 |
| Remove：数据丢弃（重加为零非旧值）/查询剔除 | :129 |
| 跨 chunk 迁移（源 4 chunk 全量迁出 + 目标链扩容）+ swap-back 完整性 | :223 |
| 连续 Add/Remove 1000 次撕裂防护 + stats 计数 | :311 |
| Compact 收缩：chunk 数 4→1→0 + 迭代正确 + 复用 | :356 |
| 代际句柄迁移后仍有效 | :439 |
| 附加：失败路径状态不变 / 空掩码往返 / 批量混合源 / stats 逐字节会计 | :176 / :485 / :531 / :612 |

- **基准（zkernel_bench.exe Release 实跑两轮，AMD 7840HS）**：

| 基准项 | 第 1 轮 | 第 2 轮 |
|---|---|---|
| 迁移吞吐（1 万实体 ×3 轮 Add+Remove，6 万次迁移） | **24.34 M 行迁移/s**（Add 0.72 ms/轮，Remove 0.51 ms/轮） | **24.77 M 行/s**（0.61/0.61 ms） |
| Compact：100 万实体 Destroy 一半后回收 855 chunk（1710→855） | **1.86 ms** | **1.97 ms** |
| （顺带）Destroy 50 万毫秒，非验收指标 | 27.3 ms | 26.2 ms |

回归对照（B-R1-01 数字量级不变）：Spawn 100 万 52.7 ms / Each 全量 392 M 行/s / 列定位 5 cycles/op。

### 四、迁移路径分配审查结论 + 约束合规

**零堆分配审查（如实）**：搬行主体（共享列 memcpy/memset + 新列写）只使用栈上数组（bits[128] 等），**无暂存缓冲、零堆分配**（zkernel.cpp:337-372）。整个迁移路径的堆分配点仅两个，均与 Spawn 同策略、非迭代路径：
- 分配点①：目标掩码首次出现 → `CreateArchetype`（:321，archetypes push_back，每掩码一次）；
- 分配点②：目标 chunk 链容量耗尽 → `AddChunk`（:335，_aligned_malloc + rowEntity 增长）。
预热的迁移基准（计时轮 6 万次迁移）即为零分配路径的吞吐数字。`Compact` 只 `_aligned_free` + 缩 vector size，无分配。简报"预分配暂存"方案在 SoA 列寻址下不需要（无整行连续拷贝需求），此为设计结论而非省略。

**过程记录（诚实披露）**：迁移核心 v1 的 Remove 分支曾按源列枚举把被删列也 memcpy 进目标（目标无该列 → 越出列布局写错位 4 字节）——由本任务新增的 stats **逐字节会计断言**（44≠40）当场抓获，修复于 zkernel.cpp:351（被删列 skip）。该断言因此保留为回归护栏。

**约束合规**：只动 `Native/zkernel/`、`Native/tests/`、`Native/CMakeLists.txt`、本日志；未碰 Unity 源（build_native.ps1 第 4 步按既有脚本行为自动复制 zrender.dll 至 UnityProject/Assets/Plugins/ZRender，dll 入库 ADR-004，非手工 Unity 改动）；未 git commit；全部改动文件 UTF-8 无 BOM / LF / 无尾随空格 / EOF 单换行（实测 trailing_ws=0、末字节 \n）/ `#pragma once` 护栏保持。

**遗留建议（不影响验收）**：① `FindArchetype` 线性扫描现在是迁移热路径（批量已用 1 项缓存缓解），archetype 数大时可换哈希（既有 TODO 观点）；② 若未来需要 Destroy 即时收缩，应以滞后阈值实现并重订基准口径（当前 Compact 显式入口与简报测试/基准语义自洽）。


## 验收清单（主控写）

1. build 全绿（用例数写入结论，基线 170/705271 只增不减）
2. 数据保留/丢弃、跨 chunk、swap-back 完整性、代际有效、撕裂防护五类用例真实断言（读源码行号）
3. 收缩回收 chunk 数下降断言；Compact 后迭代正确
4. 迁移路径零堆分配审查（迁移用预分配暂存——行号；或如实报告分配点）
5. 基准数字入报告（迁移吞吐+Compact 毫秒）
6. 文件规范+施工报告完整

## 验收结论（验收代理写）

**结论：PASS（6/6）。** 验收代理独立执行：未跑 Unity（按指示），亲自跑 build 与两个测试定向过滤轮 + `zkernel_bench` 两轮，逐文件字节级规范抽查，逐行核对报告行号映射。

1. **build 全绿 PASS**：亲跑 `build_native.ps1` 全绿——`180 用例 / 710,633 断言 / 0 failed`，/W4 /WX 零警告；基线 170/705,271 只增不减（+10 用例 / +5,362 断言，与报告一致）；CMake 挂接确认（Native/CMakeLists.txt:282 test_zkernel_migrate.cpp、:339 zkernel_bench）。
2. **五类用例真实断言 PASS**：10 个 TEST_CASE 行号全部核实且定向执行通过（7 用例 ASCII 过滤轮 + 3 纯中文名用例 BOM 脚本轮，10/10 全过）：数据保留/零或 initData/mask/查询命中 :63；Remove 丢弃+重加为零值非旧值 :129；跨 chunk 迁移（Big 容量 32、100 实体 4 chunk 源 + 目标链扩容 + Σ1..100=5050/Σ10000..10039=400780 精确回读 + swap-back 后 ents[0]/[50]/[99] spot check）:223；1000 次撕裂（archetype 恒 2/chunk 恒 2 不泄漏 + 每轮保留列值断言 + stats=kRounds）:311；代际句柄（迁移不递增代际、销毁后失配、槽位复用 n.generation==h.generation+1）:439；另附加失败路径状态不变 :176 / 空掩码往返 :485 / 批量混合源+缓存 :531 / stats 逐字节会计 :612。断言均为真实值校验，非恒真。
3. **收缩回收 PASS**：chunk 数下降断言链完整（4→Destroy 90 后仍 4→Compact()==3→1→清空后 Compact()==1→0，:369-:422）；Compact 后迭代正确（幸存集合排序恰为 {91..100} + spot check + 幂等 Compact()==0 + 释放后 re-Spawn 复用，:385-:433）。收缩语义推导**成立**：行紧凑不变量下（Spawn 追加/swap-back 收行）空闲行恒为链尾后缀、部分占用 chunk 只可能是链尾一个，">50% 空闲与相邻合并"在此不变量下物理上等价于释放全空尾 chunk（前驱恒满无空位可并）——推导数学上无懈可击；部分占用尾 chunk 不进一步重排（保留容量复用）为已如实披露的保守取舍，与简报"Destroy 一半后 Compact 毫秒"的观察语义自洽（若 Destroy 即时收缩该基准退化为空操作），Destroy 不自动收缩的设计判断合理。
4. **零堆分配审查 PASS**：搬行主体 zkernel.cpp:340-372 逐列 memcpy/memset + 栈上 bits[128]，无暂存缓冲零分配；堆分配点仅 CreateArchetype（:321→push_back :172，每掩码一次）与 AddChunk（:335→_aligned_malloc :184），与 Spawn 同策略同条件（Spawn :239-247 调用同两 helper）；引用绑定在 CreateArchetype 之后（:330 注释防 vector 重分配悬垂）；src≠dst 由调用方掩码校验保证；预热的计时轮为纯零分配路径（zkernel_bench.cpp:194-201）。**v1 Remove 越位 bug 修复复核 PASS**：:351 `if (!add && bits[k] == compId) continue;` 在位——被删列不参与 dstSlot 计算，杜绝越出目标列布局写错位；回归护栏有效（test :645 断言 Remove 仅共享列 12B、总计 40B，若 bug 复发则 44B 必失败；:648-:650 数据完整性双保险）。
5. **基准亲跑 PASS**（AMD 7840HS，两轮）：迁移吞吐 **28.62 / 31.23 M 行迁移/s**（Add 0.50 ms / Remove 0.54 ms 每轮，60,000 次迁移计数正确）——与报告 24.34/24.77 M 同数量级；Compact **1.52 / 1.40 ms** 回收 855 chunk（1710→855，与报告精确同值）——与报告 1.86/1.97 ms 同数量级；回归项同量级（Spawn 48.5 ms / Each 306 M 行/s / 列定位 7 cycles/op）。
6. **文件规范+报告完整 PASS**：5 个改动/新增源文件字节级实测 UTF-8 无 BOM、CR=0（纯 LF）、尾随空格 0、EOF 单 0x0a；zkernel.h `#pragma once` :27 护栏在位；改动范围仅授权文件（zkernel.h/.cpp、tests 两文件、CMakeLists、本日志）+ zrender.dll（build 脚本第 4 步自动部署，报告已披露，ADR-004 dll 入库）；未 git commit（git status 确认全部未暂存）。施工报告四节齐全，三处"如实披露"（v1 bug 记录、分配点、收缩取舍）经复核与代码一致。

**验收备注**（不扣分）：① 工作区另有 untracked `UnityProject/Assets/ZFrame/Gameplay/BehaviorTree/`（BTStatus.cs 等）——属并行任务 A-R8-03 的交付物，不在本任务验收范围；② FindArchetype 线性扫描为迁移热路径，施工报告遗留建议已注明（不阻塞）。

