# TASK-B-R1-01 DataKernel 数据内核（128 位掩码 + SoA + 查询）

状态: doing | 派发: 2026-10-23 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理（PASS，2026-10-23） | commit: （待填）

## 任务简报（主控写）

**目标**：在 `Native/zkernel/` 实现自研数据内核（不用任何通用 ECS 库）：128 位组件掩码 archetype + SoA 分块存储 + BMI2 掩码查询 + 实体句柄。要求查询路径零堆分配、组件列定位用硬件 POPCNT/TZCNT（约 3 周周期级）。

**设计要点（按此实现，细节可自行斟酌但需在报告说明）**：
1. **掩码**：`struct Mask128 { uint64_t w[4]; }`，组件 ID 0..127，`Has(bit)/Set(bit)/Unset(bit)`；archetype 身份 = 掩码相等
2. **SoA chunk**：每 archetype 持有 chunk 链表；chunk 容量按 16KB 预算与组件大小计算（`capacity = max(1, 16384 / bytesPerRow)`，最小 32）；组件列起始在 chunk 内的槽位序 = `POPCount(mask & (bit-1))`（低位置位计数）——**这就是"约 3 周期定位"的核心**，用 `__popcnt64`/`_tzcnt_u64`（`<immintrin.h>`，x64 BMI2，禁软件查表回退）
3. **实体句柄**：`struct ZkEntity { uint32_t index; uint32_t generation; }`；销毁用 swap-back（尾行搬到空洞），generation 防 dangling
4. **World API**（zkernel.h，C++ 类，本任务不跨 DLL ABI）：
   - 注册组件：`RegisterComponent(id, sizeBytes, alignBytes)`（id 显式分配，0..127）
   - `Entity Spawn(mask)` / `void Destroy(Entity)` / `void* GetComponent(Entity, compId)`（返回列内指针）
   - 查询：`template<class F> void Each(Mask128 with, F&& fn)`——遍历满足 `(arch.mask & with)==with` 的 archetype 的 chunk 行，fn 收到每组件指针（签名约定：`fn(void** comps, uint32_t rowCount)` 按列批量回调，避免逐行虚调用；或等价批量设计）
   - 统计：`ArchetypeCount()/EntityCount()`
5. **单线程**即可（线程安全留待后续任务，接口注释注明）；迭代路径**禁止 new/malloc**（chunk 预分配）
6. **基准程序**：新增独立目标 `zkernel_bench.exe`（`tests/zkernel_bench.cpp`）：
   - a) 组件列定位：反复测 `POPCount(mask & (bit-1))`（RDTSC，报告中位数周期数）
   - b) 100 万实体 Spawn(3 组件) + 全量 Each 遍历求和，报毫秒与 M 行/秒
   - 输出纯文本表格到 stdout
7. CMake：zkernel 目标加入新文件；bench 目标仅 Release 构建不跑 ctest；测试加进现有 zrender_test 或新测试文件均可（保持 doctest）

**约束**：只动 `Native/zkernel/`、`Native/tests/`、`Native/CMakeLists.txt`、本任务日志；`/W4 /WX` 零警告；UTF-8 无 BOM/LF/#pragma once；不 git commit；不动治理文件与其他模块。

**交付物**：实现+单测+基准+施工报告（含基准数字表格、设计偏差说明）。

## 施工报告（施工代理写）

### ① 实现摘要

- **掩码**（`Native/zkernel/zkernel.h`）：`struct Mask128 { uint64_t w[4]; }`，组件 ID 0..127（占低 2 字，高 2 字保留），`Has/Set/Unset/IsZero/==/Contains/PopCount`；archetype 身份 = 四字相等。位定位 `ZkBitsBelow(mask, bit)` = 低位置位计数（1~2 条 `__popcnt64`），位枚举 `ZkListBits`（`_tzcnt_u64` 升序），二者全内联于公共头。
- **SoA chunk**（`Native/zkernel/zkernel.cpp` `Impl::Archetype/Chunk`）：每 archetype 持 chunk 向量；容量 `max(32, 16384/bytesPerRow)`（16KB 预算，行超预算时 chunk 超 16KB 但保底 32 行）；列起始按槽位序（= 掩码升序位序）布局并对齐到组件对齐值；行 r 位于第 `r/capacity` 个 chunk 的第 `r%capacity` 行，行跨 chunk 紧凑排布。chunk 用 `_aligned_malloc` 一次分配（容量耗尽才追加，仅 Spawn 路径）。
- **定位**：`GetComponent` → `slot = ZkBitsBelow(arch.mask, compId)`（POPCNT）→ `chunkData + columnOffset[slot] + rowInChunk * size`。`Each` 对每个查询位做同样的 POPCNT 定位。无任何软件查表。
- **实体句柄**：`ZkEntity{index, generation}`；槽位数组（generation/archOf/rowOf）+ 空闲链；`Destroy` 按列 memcpy 尾行入洞（支持跨 chunk 搬运），同步修正反查表 `rowEntity`，代际 +1 使旧句柄失配。
- **World API**：`RegisterComponent`（id 0..127、size>0、align 2 的幂、禁重复）/ `Spawn(mask)`（未注册组件或 w[2..3] 非零返回 `ZK_INVALID_ENTITY`，组件内存未初始化）/ `Destroy` / `GetComponent` / `Each(with, fn)` / `ArchetypeCount` / `EntityCount`。C++ 类接口，不跨 DLL ABI；原 `ZK_GetVersion` C ABI 保留（zplugin 聚合不受影响）。
- **Each 批量回调**：签名 `fn(void** comps, uint32_t rowCount)`（简报第一选项），`comps[j]` = with 升序位序第 j 个组件的列起始指针，按 (archetype, chunk) 分批。实现经"无捕获 lambda → 函数指针 + 上下文" trampoline（`EachBatches` 私有入口），公共头不泄漏内部存储类型，转换零分配。
- **单线程**：头注释与实现注释均已注明（数据结构无锁，跨线程留待后续任务）。
- **迭代零分配**：`EachBatches`（zkernel.cpp:295-322）只读遍历 archetype/chunk 向量，指针数组 `compPtrs`（:301）与位枚举 `withBits`（:298）均在栈上；`GetComponent`（:264-293）纯指针算术。迭代路径无 new/malloc/realloc。

### ② 变更文件清单

| 文件 | 变更 |
|---|---|
| `Native/zkernel/zkernel.h` | 重写：Mask128/ZkEntity/ZkBitsBelow/ZkListBits/ZkWorld 公共接口（172 行） |
| `Native/zkernel/zkernel.cpp` | 重写：Impl 存储 + Spawn/Destroy/GetComponent/EachBatches 实现（325 行） |
| `Native/tests/test_zkernel_datakernel.cpp` | 新增：11 个 doctest 用例（441 行，main 由 test_smoke 提供） |
| `Native/tests/zkernel_bench.cpp` | 新增：基准程序（187 行，含 CPUID 品牌/RDTSC 中位数/校验和） |
| `Native/CMakeLists.txt` | zrender_test 增列新测试文件；新增 zkernel_bench 目标（仅 Release 默认构建、不进 ctest） |
| `Docs/Logs/TASK-B-R1-01.md` | 本报告 |

### ③ 自测结果

**构建**：`powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1` → **ALL GREEN**（/W4 /WX /O2 零警告零错误；configure + build + test + deploy 四步全过）。

**单测**：`zrender_test.exe` 16 用例 / 431 断言全过（新增 11 用例覆盖：掩码位运算与 AND 过滤、槽位序=低位计数（已知掩码 {3,7,100} 直接断言）、注册参数校验、多组件读写回、SoA 行步长、代际复用与旧句柄拒绝、跨 chunk（容量 32×2 以上，含跨 chunk swap-back 一致性）、Each 掩码过滤（3 archetype 交叉）、空掩码实体、Spawn 失败路径、1000 实体三列跨 chunk 求和校验）。

**基准**（`zkernel_bench.exe`，Release，AMD Ryzen 7 7840HS，6 次运行取典型值/范围）：

| 基准项 | 结果 |
|---|---|
| 组件列定位：ZkBitsBelow(POPCNT) 纯位定位（寄存器位索引） | 中位 4~5 cycles/op（RDTSC tick） |
| 组件列定位：+偏移表取数（完整路径，含 L1 载入链） | 中位 5 cycles/op |
| Spawn：1,000,000 实体 × 3 组件（28B/行，容量 455，1710 chunk） | 42~56 ms（17.9~23.8 M 实体/s，典型 ~50 ms） |
| Each：全量遍历 3 列读求和（1M 行） | 2.1~6.0 ms（166~476 M 行/s，典型 ~3 ms / ~280 M 行/s） |
| Each 求和校验 | 1,499,998,500,000 == 期望 3×Σi（逐位吻合，证明未被优化器消去） |

注：RDTSC 为不变 TSC（参考频率计数），非核心时钟周期；Zen4 上 popcnt 延迟 1 周期，测得的 4~5 tick 含循环开销（and/加法链）与 TSC/核心频率比。Spawn 波动主要来自笔记本频率调度。

**反汇编证据**（dumpbin /DISASM，对象 `Native/build/zkernel.dir/Release/zkernel.obj`，Release /O2）：

- 统计：`popcnt` × **8**、`tzcnt` × **12**（真实硬件指令编码 `F3 0F B8` / `F3 0F BC`，非库调用）
- `?GetComponent@ZkWorld@@QEAAPEAXUZkEntity@@I@Z`（定位路径）内联调用：
  `000000000000009B: E8 00 00 00 00  call  ?ZkBitsBelow@@YAIAEBUMask128@@I@Z`
- `?ZkBitsBelow@@YAIAEBUMask128@@I@Z`：
  `0000000000000050: F3 49 0F B8 0C C2  popcnt  rcx,qword ptr [r10+rax*8]`
  `0000000000000059: F3 49 0F B8 4C C2 08  popcnt  rcx,qword ptr [r10+rax*8+8]`
- `?EachBatches@ZkWorld@@AEAAXAEBUMask128@@P6AXPEAXPEAPEAXI@Z1@Z`：
  `0000000000000220: F3 48 0F B8 0C C3  popcnt  rcx,qword ptr [rbx+rax*8]`
  `0000000000000040: F3 49 0F BC C9  tzcnt  rcx,r9`
- `?ZkListBits@@YAIAEBUMask128@@PEAI@Z`：`00000000000000B0: F3 49 0F BC D0  tzcnt  rdx,r8`
- `?CreateArchetype@Impl@ZkWorld@@QEAAHAEBUMask128@@@Z`：`0000000000000020: F3 4D 0F BC CA  tzcnt  r9,r10`
- 软件查表检查：反汇编中无 256 项（或任何）popcount 查表初始化；popcnt 指令直接消费掩码字 `[基址+索引*8]`（即 Mask128.w）。

**文件规范**：五个改动/新增文件均 UTF-8 无 BOM、LF、末尾单换行、无尾随空格、`#pragma once`（头文件）、4 空格缩进（已逐文件字节级核对）。

### ④ 设计偏差说明

1. **chunk 容量公式**：简报 `max(1, 16384/bytesPerRow)` + "最小 32" 合成为 `max(32, floor(16384/bytesPerRow))`（bytesPerRow=0 时取 32）。行超 16KB 预算的极端组件会使 chunk 超预算（保底批量 32 优先）。
2. **Each 签名**：采用简报给出的第一选项 `fn(void** comps, uint32_t rowCount)`；列序 = with 掩码升序位序（文档注明）。经无捕获 thunk 转发到私有 `EachBatches`，保持公共头 pimpl 化（内部存储类型不出头文件），间接调用开销为每 (archetype, chunk) 批一次，可忽略。
3. **archetype 查找**：Spawn 时线性扫描掩码相等（archetype 数量小）；后续任务可换哈希。基准 1M Spawn 仅 1 个 archetype，未受影响。
4. **空 archetype/chunk 保留**：实体清空后不归还内存（复用免反复分配）；`ArchetypeCount()` 含空 archetype（= 历史创建数）。
5. **Spawn 失败语义**：返回 `ZK_INVALID_ENTITY`（index=generation=0xFFFFFFFF），不抛异常不断言。
6. **rowEntity 反查表几何增长**（`AddChunk` 内 reserve ×2）：避免逐 chunk `resize` 的 O(n²) 拷贝（1M 实体下差异巨大），属实现细节优化。
7. **Spawn 后组件内存未初始化**（ECS 惯例，头文件注明），单测只对写过的列做断言。
8. **基准位索引取 `k & 127` 随迭代在寄存器内变化**：防编译器循环不变量外提（早版常量位被整体消除测得 0 周期）；模拟 compId 已在寄存器的真实 GetComponent 情形。
9. **Mask128 高 2 字保留**：按简报 `w[4]` 布局但组件位仅 0..127；`Spawn` 显式拒绝 w[2]/w[3] 非零（防数组越界）。

### ⑤ 风险

1. **Each 回调内禁止 Spawn/Destroy**：会使 archetype 行映射与 chunk 批失效（头注释注明，未加运行时断言；后续可加调试模式断言）。
2. **单线程**：无任何锁；跨线程使用需外层同步（本任务范围外）。
3. **POPCNT/BMI1 硬件依赖**：无运行时检测与软件回退（按简报明确要求）；2013 年前的 x64 CPU 会非法指令。
4. **Destroy 大组件搬运成本**：swap-back 按列 memcpy，超大组件高频销毁时可优化（tombstone/延迟整理），当前基准未覆盖该模式。
5. **generation uint32 回绕**：2^32 次复用后旧句柄理论上可能撞新代际（实际不可达）。
6. **Spawn 性能含空闲链与向量增长**：~19-24 M 实体/s，若后续需要更高可换块状槽位分配。

## 验收清单（主控写）

1. `Tools/build_native.ps1` 全绿（含新增单测）
2. 单测覆盖并全过：掩码位运算与 AND 查询过滤、Spawn/Destroy 代际复用（销毁后旧句柄访问被拒）、跨 chunk 边界（实体数超过单 chunk 容量×2）、多组件列读写回正确性、组件列槽位序=低位计数（直接构造已知掩码断言）
3. **反汇编证据**：对 zkernel 编译产物（dumpbin /disasm 或 /FA 产物）grep `popcnt`/`tzcnt` 指令存在；不存在软件查表 popcount（无 256 项表初始化）
4. `zkernel_bench.exe` 可运行，基准数字已录入施工报告（列定位中位周期数 + 百万实体 Spawn/Each 毫秒）
5. 迭代路径零分配：代码审查确认 Each 路径无 new/malloc/realloc（验收代理读代码并在结论中给出行号证据）
6. 文件规范抽查（BOM/LF/pragma once）
7. 施工报告完整（含设计偏差说明）

## 验收结论（验收代理写）

**总判定：PASS**（验收代理：general-purpose 验收子代理，2026-10-23；全部 7 项独立复跑，未采信施工报告结论）

1. **构建全绿 PASS**：独立执行 `Tools/build_native.ps1`，退出码 0，输出 `ALL GREEN: configure + build + test succeeded`（/W4 /WX /O2，8 目标编译零警告；deploy 步跳过属预期分支）。
2. **单测覆盖 PASS**：读 `Native/tests/test_zkernel_datakernel.cpp` 源码核对五类覆盖各有对应用例——掩码位运算+AND 过滤（"Mask128 位运算基础":36 / "Mask128 AND 包含过滤":69 / "Each 掩码过滤与多 archetype":293）、代际复用+旧句柄 GetComponent 被拒（"Destroy 代际复用":194，:215 `CHECK(world.GetComponent(bOld,...)==nullptr)`）、跨 chunk 实体数>容量×2（"跨 chunk 边界":236，100>32×2；"多组件跨 chunk":396，1000>455×2）、多组件列读写回（"Spawn/GetComponent 多组件读写回":123）、槽位序=低位计数直接断言（"ZkBitsBelow 槽位序":87，mask={3,7,100} 逐值断言）；独立重跑 `zrender_test.exe`：16 用例/431 断言全过。
3. **反汇编 PASS**：对本次验收自建构建的 `Native/build/zkernel.dir/Release/zkernel.obj` 跑 `dumpbin /disasm`（2845 行），独立统计 `popcnt` × **8**、`tzcnt` × **12**，与报告一致；全部为真实硬件编码（`F3 4x 0F B8/BC`）；`?GetComponent@ZkWorld` 偏移 +0x9B 处 `call ?ZkBitsBelow`，ZkBitsBelow 内 `popcnt rcx, qword ptr [r10+rax*8]`（直接消费 Mask128.w 字），EachBatches 内 `popcnt rcx, qword ptr [rbx+rax*8]` + 4 处 tzcnt 位枚举循环；无 256 项软件查表——obj `.rdata` 原始数据仅 0x10=16 字节（容不下 256 字节表）、无 dynamic_initializer、源码全读无查表。
4. **基准 PASS**：独立运行 `zkernel_bench.exe`（Ryzen 7 7840HS）：列定位纯位 5.00 cycles/op（报告 4~5）、+偏移表取数 5.00（报告 5）、Spawn 1M×3 组件 40.1 ms / 24.93 M 实体/s（报告 42~56 ms / 17.9~23.8 M/s）、Each 全量 3 列求和 2.5 ms / 406 M 行/s（报告 2.1~6.0 ms / 166~476 M/s）、校验和 1,499,998,500,000 与报告逐位一致——全部同数量级。
5. **迭代零分配 PASS**：`EachBatches`（zkernel.cpp:295-318）仅用栈数组 `withBits`(:298)/`compPtrs`(:301)，无任何分配调用；`GetComponent`（zkernel.cpp:264-293）纯指针算术；头文件 `Each` trampoline（zkernel.h:148-156）无捕获 lambda 转函数指针零分配。全部分配点均在生命周期路径：ctor `new Impl`(:153)、CreateArchetype `push_back`(:129)、AddChunk `_aligned_malloc`/`reserve`/`resize`(:138/:141/:146)、Spawn 槽位向量 `push_back`(:211-213)、Destroy `freeSlots.push_back`(:259)——均不可从 Each/GetComponent 迭代路径触达。
6. **文件规范 PASS**：zkernel.h/zkernel.cpp/test_zkernel_datakernel.cpp/zkernel_bench.cpp 四文件字节级核验——首 3 字节 `2f 2f 20`（无 BOM）、`tr` 计数 CR=0（纯 LF）、zkernel.h 含 `#pragma once`（.cpp 无需）、`[[:blank:]]$` 尾随空白 0 行、末字节 0a（单换行结尾）。
7. **施工报告完整 PASS**：①实现摘要②变更文件清单③自测结果（含基准数字表+反汇编证据）④设计偏差说明（9 条）⑤风险（6 条）五节齐全；git status 核对改动范围仅 `Native/zkernel/`、`Native/tests/`、`Native/CMakeLists.txt`、本日志，符合任务约束。

**保留意见**（不阻断 PASS）：a) 本次 Spawn 24.93 M 实体/s 略超报告上限 23.8（笔记本频率调度波动，同数量级）；b) RDTSC 测得为参考频率 tick 非核心周期（报告已自注）；c) Each 回调内禁 Spawn/Destroy 仅头注释声明、无运行时断言（报告风险①已列）；d) POPCNT/BMI1 无运行时检测回退，2013 年前 CPU 会非法指令（简报明确要求，报告风险③已列）。

---

> **勘误（TASK-B-KERNEL-01，2026-11-14 追加；原文保留不改，按「以代码为准」原则以此勘误为准）**：本日志正文（简报）所称「BMI2 掩码查询」的指令集归属失实——代码实装为 POPCNT/TZCNT 硬件指令（POPCNT 属 SSE4.2/ABM，TZCNT 属 BMI1；BMI2=PEXT **未实装**，cpuid 核验 PEXT 存在但代码不使用）。验收第 3 条的反汇编证据（`popcnt`×8/`tzcnt`×12，无查表）本身就是正确指令集的实证。周期实测与断言闭环见 `Docs/Logs/TASK-B-KERNEL-01.md`：纯位定位 4~5、定位+chunk 列偏移表取数（端到端口径）5、随机取址含运行时行内除法 17 cycles/op（2026-11-14 @Zen4 7840HS，RDTSC 中位），doctest 断言 <40 防逐位循环退化（变异逐位循环实测 80~82 必红）。
