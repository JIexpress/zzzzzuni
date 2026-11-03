# TASK-B-R9-17 —— DataKernel C ABI 导出 + C# 绑定（玩法侧实战第一阶段）

状态: doing | 派发: 2026-11-04 | 施工: （待填） | 验收: （待填） | commit: （待填）

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

（待填）

## 验收清单（主控写）

- [ ] V1 build 全绿（native+C# 数字写入结论）
- [ ] V2 ABI doctest 真断言（游标跨 chunk/坏句柄/迁移往返——行号）；**变异复做 1 次**（游标推进改错→用例红→恢复）
- [ ] V3 C# EditMode roundtrip 1000 实体用例真实（求和==期望，行号）+ 1000 实体游标耗时报告
- [ ] V4 既有导出零破坏（`--assets`/`--draw` 回归）+ Bindings asmdef 引用核对
- [ ] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

（待填）
