# TASK-B-R9-17 —— DataKernel C ABI 导出 + C# 绑定（玩法侧实战第一阶段）

状态: doing | 派发: 2026-11-04 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

### 背景

DataKernel（B-R1/R8-03/R9-12）是 C++ 类、**从未跨 DLL ABI**——C# 玩法无法使用。本任务打开通道：句柄式 C ABI 导出 + C# P/Invoke 绑定。下游 A-R9-13 将用它在 Unity 场景里跑 1000 实体（混合架构：kernel 管群体，Mono 管英雄）。

### 设计要点（按此实现，细节可斟酌但报告说明）

1. **C ABI 导出**（Native/zkernel/zkernel_abi.h/.cpp + .def 追加进 zrender.dll）：
   - World 句柄：`ZK_WorldCreate(out hWorld)` / `ZK_WorldDestroy(h)`（内部句柄表+代际，最多 8 世界）
   - 组件注册：`ZK_RegisterComponent(h, compId, sizeBytes, alignBytes)`（id 由 C# 侧显式分配 0..127）
   - 实体：`ZK_Spawn(h, mask lo, mask hi, out ZkEntity)` / `ZK_Destroy(h, entity)`；`ZkEntity{u32 index; u32 generation;}` blittable
   - 组件读写：**Copy 语义**（指针不出 ABI）：`ZK_SetComponent(h, entity, compId, const void* data, u32 size)` / `ZK_GetComponent(h, entity, compId, void* out, u32 size, out ok)`
   - 迁移：`ZK_AddComponent/ZK_RemoveComponent`（R8-03 已有，包一层）
   - **游标式批量迭代（关键设计：无回调跨 ABI——委托生命周期问题规避）**：`ZK_EachBatchBegin(h, maskLo, maskHi, out cursor)` → `ZK_EachBatchNext(h, ref cursor, void** colPtrs /*出参定长数组*/, u32 maxCols, out u32 rowCount, out u32 chunkRowCount)`——每调用返回一个 chunk 的各列指针+行数，C# 侧 unsafe 读，直到 false
   - `ZK_WorldStats(h, out stats)`（实体数/chunk 数/迁移计数）
   - 线程模型：沿用单线程约定（doc 写明），游标只在主线程用
2. **doctest**（`tests/test_zkernel_abi.cpp`）：ABI 层全往返（Create→Register→Spawn→Set→游标读回→Add→Get→Destroy→句柄失效；游标跨 chunk 推进正确；坏句柄/坏 size 拒绝）
3. **C# 绑定**（`UnityProject/Assets/ZRender/Bindings/` 扩展——zkernel 在同一 zrender.dll）：`ZKernelNative.cs`（DllImport 集）+ `ZKernelWorld.cs` 安全包装（IDisposable；Spawn/AddComponent<T>/RemoveComponent<T> 泛型（T: struct，Marshal.SizeOf 校验）；`EachBatches(mask, delegate(void** cols, uint rows))` 包装游标——**delegate 生命周期**：调用期间 keepalive，文档写明）+ EditMode 单测（真 dll roundtrip：spawn 1000 实体→游标读回求和==期望；迁移后读回）
4. **性能守卫**：游标拉取全 1000 实体的 C# 循环耗时报告（EditMode 计时）——为 A-R9-13 提供基线
5. 自测：build 全绿（301 基线只增不减）+ `--assets` 回归 + C# EditMode 全绿（811+新增）

### 约束

- 白名单：`Native/zkernel/**`、`Native/tests/**`、`Native/CMakeLists.txt`（.def）、`UnityProject/Assets/ZRender/Bindings/**`、`UnityProject/Assets/ZFrame/Tests/**`（或 Render.Tests）、本日志
- ABI 只新增不改既有导出；/W4 /WX；UTF-8 无 BOM/LF；不 git commit
- delegate keepalive 与 unsafe 读的规范写 XML doc；错误码体系沿用 R10-03 风格

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理 ｜ **日期**: 2026-11-04 ｜ **自测**: 全绿（见 §五）

### 一、交付物清单

| 文件 | 性质 | 说明 |
|---|---|---|
| `Native/zkernel/zkernel_abi.h` | 新增 | C ABI 公共头（纯 C，stdint 自包含；12 个 ZK_* 导出声明 + 错误码 + 3 个 POD） |
| `Native/zkernel/zkernel_abi.cpp` | 新增 | ABI 实现：静态句柄表（8 槽+代际）、Copy 语义读写、游标编排、异常折算错误码 |
| `Native/zkernel/zkernel.h` / `.cpp` | 修改（只增） | 追加 5 个只读查询（IsAlive/IsComponentRegistered/HasComponent/ComponentSize/LayoutVersion）+ 只读单步扫描 `ScanChunk` + 布局版本号成员（Spawn/Destroy/Migrate/Compact 成功时 +1）；既有 API 零改动 |
| `Native/zplugin/zrender.def` | 修改 | 追加 12 个 ZK_* 导出（`dumpbin //EXPORTS` 实证 12/12 在表） |
| `Native/CMakeLists.txt` | 修改 | zkernel 静态库加 `zkernel_abi.cpp`；zrender_test 加 `test_zkernel_abi.cpp` |
| `Native/tests/test_zkernel_abi.cpp` | 新增 | doctest 10 用例（见 §四） |
| `UnityProject/Assets/ZRender/Bindings/ZKernelNative.cs` 等 8 文件 | 新增 | P/Invoke 集中地 + 7 个类型（一类型一文件） |
| `UnityProject/Assets/ZRender/Bindings/ZFrame.Render.Bindings.asmdef` | 修改 | `allowUnsafeCode: false → true`（唯一一项；游标 `void**` 出参与取栈地址所需） |
| `UnityProject/Assets/ZRender/Tests/ZKernelWorldTests.cs` | 新增 | EditMode 11 用例（真 dll，见 §四） |
| `UnityProject/Assets/Plugins/ZRender/zrender.dll` | 构建产物更新 | 本任务产物（新增导出），**勿按 §3.7 既有噪声 checkout 丢弃** |

未触碰：Unity 场景/玩法文件（A-R9-13 下游）、并行任务 A-R9-14 的 Audio/Game 改动（工作区里可见但与本任务无关）。全部新文件 UTF-8 无 BOM + LF（od/awk 字节级自查过）。

### 二、原生 ABI 设计（简报设计点 1 的落地与两处细节决策）

**句柄表**：进程级静态 8 槽，`handle = (generation << 32) | slot`，代际自 1 起 → **0 恒无效**（未初始化缓冲不会误命中）；销毁代际 +1（回绕跳过 0）。与 zmesh 句柄表同一口径（单线程、无锁）。表满返回 `ZK_ABI_ERR_FULL`。

**Copy 语义**：`ZK_SetComponent/ZK_GetComponent` 全程 memcpy，原始行指针不出 ABI；sizeBytes 与注册字节数**精确一致**校验（`ZK_World::ComponentSize` 新查询），偏差即 `ZK_ABI_ERR_ARGS`（简报"坏 size 拒绝"）。`ZK_AddComponent` 的 initData=null 是**合法零初始化**（沿用 C++ 语义），不算 ErrNull。

**游标设计（关键）**：
- **无回调跨 ABI**：`ZK_EachBatchBegin` 把掩码/布局版本/推进位置写进**调用方持有的 48B POD 游标**（`ZkEachCursor`），`ZK_EachBatchNext` 每调用交付一个 chunk 的各列起始指针（列序 = 查询掩码升序 compId 位序，与 C++ `EachBatches` 逐 chunk 语义一致：archetype 向量序 → chunk 序，n = min(剩余行, chunk 容量)）。返回值三态：1=有批 / 0=耗尽（**幂等**，再次调用仍 0 且报累计总行数）/ 负=错误码。原生侧**不登记游标**——无表、无泄漏、无清理，C# 侧迭代中断（异常）也无原生善后。
- **两出参的语义消歧**（简报 `out rowCount, out chunkRowCount` 未定义区别，此为决策）：`*outChunkRowCount` = 本 chunk 可读行数（本次实读量）；`*outRowCount` = **累计已交付行数**（耗尽时 = 查询匹配总行数，跨 chunk 推进断言直接可用，C# 测试据此断言 1000）。
- **布局变更防护**：C++ Each 用 `iterating` 计数在回调期间锁布局，但游标推进是跨调用松散节奏，**持锁跨 ABI 调用会因游标被遗弃而永久冻结世界**。改为**布局版本号**：`ZkWorld` 新增 `layoutVersion`，Spawn/Destroy/Add/Remove（含批量）/Compact 每成功变更 +1；Begin 快照、Next 比对，不一致 → 游标作废（state=DONE）返回 `ZK_ABI_ERR_STATE`，且后续调用幂等返回 0。契约（"两次 Next 之间禁布局变更"）写进头文件 doc，违规有运行时牙齿。组件列**内容**原地改（Set）不触版本号，合法。
- chunk 扫描逻辑收口在 `ZkWorld::ScanChunk`（新增只读单步方法，不进 iterating 计数），ABI 层不复制 archetype 内部知识。

**错误码**：R10-03 风格负值，0=OK：NULL(-1)/HANDLE(-2)/RANGE(-3)/ARGS(-4)/STATE(-5)/FULL(-6)/NOMEM(-7)。校验顺序全函数一致：NULL→HANDLE→RANGE→ARGS→STATE（测试按此断言）。异常不跨 ABI：四条可能分配路径（WorldCreate/Spawn/Add/Remove）try/catch 折算 NOMEM，其余函数全链路无分配。

**统计**：`ZkWorldStatsAbi` 按简报"实体数/chunk 数/迁移计数"要求**扩充为 5 字段**（entityCount/chunkCount + C++ ZkWorldStats 三计数字段）——与 C++ ZkWorldStats **不是**内存镜像，`ZK_WorldStats` 逐字段搬运（zkernel_abi.cpp 注释写明，只对 ZkEntityAbi↔ZkEntity 做 static_assert 布局锁定）。

### 三、C# 绑定设计（简报设计点 3 的落地与三处细节决策）

文件（一类型一文件，file-conventions §2）：`ZKernelNative.cs`（internal DllImport 集中地，同 ZrNative 模式）、`ZKernelWorld.cs`（公开门面，IDisposable+finalizer 兜底）、`ZkEntity/ZkMask/ZkEachCursor/ZkWorldStats`（blittable POD）、`ZkEachBatchHandler`（委托）、`ZKernelErrorCodes/ZKernelException`。

- **泛型约束用 `T : unmanaged`（比简报"T: struct"更严）**：含引用字段的 struct 能过 struct 约束但 memcpy 语义即坏；unmanaged 在编译期排除。`Marshal.SizeOf<T>()` 校验落在 `ValidateComponentSize`（与注册尺寸不一致抛 ArgumentException，错误先于原生 ErrArgs）。
- **对齐启发式**：`RegisterComponent<T>` 的 alignBytes = size 的最大 2 的幂因子（封顶 64）。数学上恒安全：C# 封送 size 恒为真对齐的整数倍，故最大 2 幂因子的指数 ≥ 真对齐指数 → 结果恒为真对齐的**整数倍**（只高估不出错，代价仅 chunk 容量略降）；要精确对齐用 `(compId, size, align)` 显式重载。
- **delegate keepalive（简报要求文档化）**：`ZkEachBatchHandler` 的 remarks 与 `EachBatches` doc 写明——原生侧**不持有任何托管引用**（游标是调用方 POD，无回调跨 ABI），委托只在 C# 包装循环内同步调用、由调用栈根引用，**无需 GCHandle.Alloc**；回调内禁布局变更（违者下一次推进抛 ZKernelException）。出参 `IntPtr[]` 按查询位列数一次分配、fixed 跨循环复用 → 迭代路径零逐 chunk 分配。
- **unsafe 边界**：仅 Bindings 程序集开 unsafe（asmdef 一处布尔）；`ZFrame.Render.Tests` 保持不开——列读走 `Marshal.Copy`/`Marshal.ReadInt32`。两个编译期发现（留痕）：① `fixed` 于栈局部报 CS0213（局部天然固定）→ 直接 `&local`；② `TryGetComponent(entity, id, out var v)` 报 CS0411——T 只出现在 out 位且无其他推断源，`out var` 推断不出 T，须显式 `TryGetComponent<T>(...)`。
- 互操作 POD 的公开字段是 file-conventions §2"禁 public 字段"的既定例外（同 ZrNative 全部既有 POD），doc 已注明。

### 四、测试与自测结果（简报设计点 2/4）

**原生 doctest（`test_zkernel_abi.cpp`，10 用例）**：全往返（Create→Register→Spawn→Set→游标读回→Add 零初始化→Get→Destroy→World/实体句柄失效）；游标跨 chunk（1024B 组件容量 32 × 100 实体：逐 chunk 断言 32/32/32/4 与累计 32/64/96/100 + 幂等耗尽 + 全量回读）；三列 1000 实体全量求和（28B/行→容量 585→2 chunk，`calls==2` 锁 chunk 推进，双 archetype 过滤 1010 行）；游标防御（maxCols 不足/游标损坏/空出参/空掩码/布局变更作废）；坏 World 句柄族（0/越界/代际 0/销毁后，全 12 函数逐一点名）；坏 size 拒绝（±4/0，且 ARGS 先于 STATE）；坏实体句柄族（越界 index/代际失配/缺组件/未注册/compId 128）；注册参数拒绝；World 表满 8+释放复用+代际单调；ZkWorldStats 计数（300 次迁移→migrations/rowsMoved/bytesMoved 断言）。

**运行结果**：
- **build_native.ps1 全绿**：`test cases: 311 | 311 passed | 0 failed`，断言 **776,716**（基线 301/771,417，**+10 用例 / +5,299 断言**，零减）。
- **变异复做 1 次（V2 预演）**：注释 `zkernel_abi.cpp` 的 `cursor->rowsDelivered += n;`（游标累计推进改错）→ 重编 → **4 用例 / 12 断言红**（累计断言全数命中）→ 恢复 → 全绿。用例有牙。
- **C# EditMode：total=873 passed=873 failed=0**（results.xml 为证；本任务新增 11 用例，既有 862——**注意简报口径 811 已过期**：工作区含并行任务 A-R9-14 已入库的 11 个 GameAudioBindings 用例，故既有数比派发时多）。11 用例：真 dll roundtrip、1000 实体三列求和（499500 精确断言）、双 archetype 过滤、列序=升序 compId、迁移零初始化+读回+游标全量回读（300 实体，sum=44850）、删除迁移数据丢弃、代际复用拒旧句柄、布局变更作废游标抛 ErrState、World 表满 8+槽位复用、GetComponentSize、耗时报告。
- **C# 耗时基线（A-R9-13 用，简报设计点 4）**：1000 实体 {pos,vel,tag} 游标全量拉取（C# 循环 + Marshal.Copy 列读，无 unsafe）：**平均 10.0 µs/次**（200 次均值，总计 2.00 ms，Editor EditMode 单线程；守卫断言单次 < 100 ms 防病态回归）。测试日志行：`[B-R9-17] 1000 实体游标拉取…平均 10.0 us/次`。
- **既有导出零破坏**：`--assets` 回归 exit 0（`D3D12 ASSETS GALLERY OK (models=4)`，probe 6/6 × 4 模型，errors=0）；架构校验器 `ValidateFromCommandLine` **exit 0**。

### 五、已知受限与备注（非阻塞）

1. **数字口径**：EditMode 873（简报写 811+新增）——873 = 本任务 11 + 既有 862（862 含并行 A-R9-14 的 11 个）；建议验收与 HANDOFF 刷新按 873 记。
2. **跨 chunk 列指针距离不是契约**：chunk 是独立堆分配（_aligned_malloc），doctest 曾断言相邻 chunk 基址差 = 32×1024 而实测 32800（分配器行为）——已改为只锁"行数推进 + 数据完整性"，指针跨 chunk 不可比。验收变异若改的是 chunk 推进而非累计计数，注意这条边界。
3. `ZkWorldStatsAbi` 与 C++ `ZkWorldStats` 不同构（ABI 扩充五字段，逐字段搬运）——验收核对 C# 布局时以 `zkernel_abi.h` 为准，勿拿 zkernel.h 对表。
4. Bindings 程序集现开启 unsafe（asmdef 一处布尔变更）；绑定本身无 UnityEngine 依赖（references 仍为空，可被任意程序集消费）。
5. 工作区中 `Docs/Demo.md`、`Assets/Audio|Game` 等改动属并行任务 A-R9-14，与本任务无关；`zrender.dll` 的 M 是本任务产物（新导出），提交时应保留。

## 验收清单（主控写）

- [ ] V1 build 全绿（native+C# 数字写入结论）
- [ ] V2 ABI doctest 真断言（游标跨 chunk/坏句柄/迁移往返——行号）；**变异复做 1 次**（游标推进改错→用例红→恢复）
- [ ] V3 C# EditMode roundtrip 1000 实体用例真实（求和==期望，行号）+ 1000 实体游标耗时报告
- [ ] V4 既有导出零破坏（`--assets`/`--draw` 回归）+ Bindings asmdef 引用核对
- [ ] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

**验收**: general-purpose 验收子代理 ｜ **日期**: 2026-11-04 ｜ **结论: PASS**（V1–V5 全过，与施工不同代理、全部独立亲证，未改任何施工代码；变异复做已还原）

- **V1 build 全绿（亲证）——PASS**：native `Tools/build_native.ps1` 亲跑 exit 0，doctest `test cases: 311 | 311 passed | 0 failed`、`assertions: 776716`（基线 301/771,417 只增不减）；C# EditMode 亲跑两轮均 **total=873 passed=873 failed=0**（run_tests.ps1 editmode 段 + 验收独立重跑，results xml=`TempLogs/b_r9_17_verify.xml`，`<test-run total="873" passed="873" failed="0" result="Passed">`）。实跑数字 873 记口径（简报 811 已过期：含并行 A-R9-14 已入库 11 用例）。
- **V2 doctest 真断言 + 变异复做——PASS**：真断言行号（`Native/tests/test_zkernel_abi.cpp`）——游标跨 chunk 推进 :189–209（chunkRows 32/32/32/4 + cumulative 32/64/96/100 + 幂等耗尽）、坏 World 句柄族 :380–400、迁移往返 :122–133（Add 零初始化→Get 读回）与 :556–592（300 次迁移计数）、布局版本作废 ERR_STATE :361–362。**变异复做**：注释 `zkernel_abi.cpp:389 cursor->rowsDelivered += n;` → MSBuild 重编 → **4 用例/12 断言红**（cumulative 断言全数命中 :108/191/195/199/203/207/209/225/285/305/351）→ 恢复原行 → 全绿 311/776,716。源码零变异残留（grep VERIFY-MUTATION=0）。
- **V3 C# 绑定审查——PASS**：DllImport 集中于 `ZKernelNative.cs:18–64`（12 个 ZK_* 单点 internal）；`ZKernelWorld.cs:20` sealed : IDisposable + Dispose :46–57 + finalizer 兜底 :59–67；泛型 `T : unmanaged` :91/134/151/164/194/213（较简报 T:struct 更严，报告已声明）；delegate keepalive XML doc：`ZkEachBatchHandler.cs` remarks（原生不持托管引用/无需 GCHandle.Alloc/回调禁布局变更）+ `ZKernelWorld.cs:238–245`；Marshal.SizeOf 校验 :303–319。roundtrip 1000 实体用例真实：`ZKernelWorldTests.cs:100–144`（total==1000 :137、batches>=2 跨 chunk :138、sumPos/sumVel==499500 精确断言 :139–141、sumTag==499500 :142），xml 亲证 11 用例全 `result="Passed"`。耗时基线亲证：`TempLogs/b_r9_17_verify.log:981` `[B-R9-17] 1000 实体游标拉取…平均 10.2 us/次（200 次均值，总计 2.04 ms）sumPos=499500`——与报告 10.0µs 同量级吻合。
- **V4 既有导出零破坏——PASS**：`zrhi_d3d12_window.exe --assets` 亲跑 **exit 0**，`D3D12 ASSETS GALLERY OK (models=4)`，probePasses 6/6×4，errors=0；`dumpbin //EXPORTS`（部署位 `UnityProject/Assets/Plugins/ZRender/zrender.dll`）实见 23 导出 = 既有 ZR_* 11 个全在 + 新 ZK_* 12 个全在；`zrender.def` git diff 纯追加（+12 行 ZK_*，0 删除）。
- **V5 文件规范 + 报告四节——PASS**：13 个新文件字节级抽查无 BOM（首 3 字节 `// `）且 CR_bytes=0（纯 LF）；施工日志四节齐全（简报/施工报告/验收清单/验收结论）；`zkernel.h/.cpp` git diff = 133 行纯插入 0 删除（"只增不改"属实）；Bindings asmdef 仅 `allowUnsafeCode` 一处布尔变更；改动全部落在白名单内（Audio/Game 等改动属并行 A-R9-14，与本任务无关）。
