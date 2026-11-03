# TASK-A-M0-03 Services 服务定位器 + ObjectPool 对象池（含 reset 机制）

状态: done待验收 | 派发: 2026-10-23 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：实现 ZFrame.Core 剩余两件核心设施：服务定位器 Services、对象池 ObjectPool（含 IPoolable 复用重置与自动重置器），配 EditMode 单测。

**设计要点**：
1. `Services.cs`（namespace ZFrame.Core）：
   - `static void Register<T>(T instance)` / `static T Get<T>()` / `static bool TryGet<T>(out T v)` / `static void Unregister<T>()` / `static void Clear()`（测试用）
   - 重复注册=抛 InvalidOperationException（开发期尽早暴露）；未注册 Get=抛带注册清单的异常（方便排查）；接口优先约定写 XML doc
   - 主线程约定注明；`GetAllRegistered()` 返回类型名列表（调试面板用）
2. `ObjectPool/`（ZFrame.Core，允许 UnityEngine.CoreModule）：
   - `IPoolable { void OnSpawn(); void OnDespawn(); }`
   - `ComponentPool<T> where T : Component`：构造 `(T prefab, int warmup=0, int maxCapacity=int.MaxValue)`；`T Get(Transform parent=null)`（池空 Instantiate；出池 SetActive(true)→遍历 IPoolable.OnSpawn）；`void Release(T obj)`（SetActive(false)→归位池父节点"~Pooled"→IPoolable.OnDespawn）；容量上限告警（UnityEngine.Debug.LogWarning 一次）；`Shrink(int keep)` 收缩；Stats{ActiveCount,PooledCount,PeakActive,InstantiateCount}
   - `PoolResetter : MonoBehaviour`（自动重置常见残留，Release 前由池调用 `ResetForReuse()`）：Rigidbody 线速度/角速度清零、TrailRenderer.Clear、ParticleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear)、TrailRenderer/LineRenderer 隐藏（由 spawn 恢复）——逐项用 API 检查存在性
   - `PoolRegistry`：`static void Register<T>(prefab, pool)` 按 prefab 索引、`static T Spawn<T>(T prefab)` / `static void Despawn<T>(T instance)` 全局门面（内部查池，未注册则惰性建池）；`static void ClearAll()`（场景切换收缩用）
3. 单测（ZFrame.Core.Tests 追加）：
   - Services：注册/获取/退订/重复注册抛/未注册抛/Clear
   - 池：Get 首次 Instantiate 计数、Release 后复用不再 Instantiate、IPoolable OnSpawn/OnDespawn 成对调用顺序、warmup 预热数量、Stats 数值正确、Shrink 销毁超额对象、PoolResetter 清零 Rigidbody 速度（EditMode 下构造 GameObject+Rigidbody 验证 velocity→zero）
   - 全部用 `new GameObject()` 构造，测试结束清理（tearDown 销毁）
4. 自测：EditMode 测试全绿（命令同 A-M0-02 简报）

**约束**：只动 `UnityProject/Assets/ZFrame/Core/`、`UnityProject/Assets/ZFrame/Tests/`、本任务日志；Unity 单实例（等待其他 Unity 任务结束）；不 git commit；UTF-8 无 BOM/LF。

**交付物**：Services+对象池三件+测试全绿+施工报告四节。

## 施工报告（施工代理写）

**施工: general-purpose 施工子代理｜日期: 2026-10-24**

### ① 摘要

按简报实现 ZFrame.Core 的 Services 服务定位器与对象池四件套（IPoolable / ComponentPool / PoolResetter / PoolRegistry），并追加 25 个 EditMode 单测（Services 10 + 对象池 15）。EditMode 全套 38 用例（含既有 EventBus 13 个）全绿。

### ② 交付清单（均为 UTF-8 无 BOM / LF）

新增：
- `UnityProject/Assets/ZFrame/Core/Services.cs` —— Register/Get/TryGet/Unregister/Clear/GetAllRegistered；重复注册抛 InvalidOperationException；未注册 Get 抛含已注册类型清单的异常；接口优先与主线程约定写 XML doc
- `UnityProject/Assets/ZFrame/Core/ObjectPool/IPoolable.cs`
- `UnityProject/Assets/ZFrame/Core/ObjectPool/ComponentPool.cs` —— 构造(prefab, warmup, maxCapacity)；Get 空池 Instantiate→SetActive(true)→OnSpawn；Release OnDespawn→PoolResetter.ResetForReuse→容量判断→SetActive(false)→归位统一池父节点 "~Pooled(Type)#hash"；超上限销毁 + Debug.LogWarning 一次；Shrink(keep)；Stats{ActiveCount,PooledCount,PeakActive,InstantiateCount}；实现 IDisposable
- `UnityProject/Assets/ZFrame/Core/ObjectPool/PoolResetter.cs` —— Rigidbody velocity/angularVelocity 清零、TrailRenderer.Clear+隐藏、LineRenderer 隐藏、ParticleSystem.Stop(true, StopEmittingAndClear)；RestoreVisibility 在 spawn 侧恢复
- `UnityProject/Assets/ZFrame/Core/ObjectPool/PoolRegistry.cs` —— Register 按.prefab 索引、Spawn/Despawn 全局门面（未注册惰性建池）、Despawn 未跟踪对象兜底销毁、ClearAll 场景切换用
- `UnityProject/Assets/ZFrame/Tests/ServicesTests.cs`（10 用例）
- `UnityProject/Assets/ZFrame/Tests/ObjectPoolTests.cs`（15 用例）

修改：仅本任务日志。未动 asmdef（ZFrame.Core references 仍为空）。

### ③ 自测结果

- 命令：`Unity.exe -batchmode -projectPath ... -runTests -testPlatform EditMode -testResults ...`
- 终态 results.xml 关键行：`test-run id="2" testcasecount="38" result="Passed" total="38" passed="38" failed="0" ... duration="0.186"`
- 过程：首轮 38 用例 1 失败（MaxCapacity 用例用 NUnit IsNull 判断已销毁对象，Unity fake-null 语义不符）→ 改用 Unity 重载 `==` 断言 → 复跑 38/38 Passed
- 遵守批处理纪律：跑前确认无 Unity.exe + 删 UnityLockfile；两轮均在 results.xml 出现后即判定完成，Unity 进程僵死则 taskkill //PID //F（两轮各杀 1 个 PID），未无限等待

### ④ 设计偏差

1. ComponentPool 的销毁路径（超容量 Release/Shrink/Dispose/PoolRegistry 兜底）在非 Play 模式用 DestroyImmediate、Play 模式用 Destroy——否则 EditMode 单测无法观测销毁结果（简报未明确，属实现必要补充）
2. PoolResetter 以"池持有的禁用组件实例"形式挂在池根节点上作为无状态工具（满足简报"MonoBehaviour"要求，但不随池对象运行 Update）
3. ComponentPool 额外实现 IDisposable 与 Contains(instance)（后者供 PoolRegistry.Despawn 查归属）

### ⑤ 风险

- ComponentPool 内部对 pooled 栈加 lock（主线程约定下冗余但无害）
- PoolRegistry.Despawn 为 O(池数) 遍历查归属；池数规模小，暂可接受，后续可加 instance→pool 字典
- 池根节点 GameObject 公开可访问（PoolRoot 属性），误删将导致后续 Release 报 NullReference；已在 XML doc 标注属池所有


## 验收清单（主控写）

1. 验收代理自跑 EditMode 测试全绿（用例数写入结论）
2. Services 行为：重复注册/未注册异常路径有真实用例断言
3. 池行为：复用零重复 Instantiate（InstantiateCount 断言）、IPoolable 成对顺序、Resettes 清速度实证
4. 代码审查：Release 归位统一池父节点（防幽灵对象，行号证据）；Get/Release 无每帧分配热路径之外的闭包分配
5. ZFrame.Core.asmdef 引用仍为空（未偷引其他程序集）
6. 文件规范抽查
7. 施工报告完整

## 验收结论（验收代理写）

**验收: general-purpose 验收子代理｜2026-10-24｜结论: PASS**

1. **自跑 EditMode 测试 PASS**：`Unity.exe -batchmode -runTests -testPlatform EditMode -testResults <Temp xml>`（首轮误加 `-quit` 致未跑即退出，去掉后复跑）。results.xml 关键行：`test-run id="2" testcasecount="38" result="Passed" total="38" passed="38" failed="0" duration="0.177"`；日志无 error CS/warning CS。实际用例数 38（简报"44 左右"系含 native 桥接 6 例的估算，EditMode 实为 38，与施工报告一致）。遵守批处理纪律：跑前确认无 Unity.exe + 清 UnityLockfile；xml 出现后按纪律 taskkill //PID //F 清掉残留 Unity 进程（15020）；临时 xml/log 均已删除
2. **Services 异常路径 PASS**：ServicesTests.cs L50-57 `Register_Duplicate_Throws` 真实断言 `Assert.Throws<InvalidOperationException>`；L59-67 `Get_Unregistered_ThrowsWithRegisteredList` 断言异常消息含注册清单（StringAssert.Contains 两条）；对应实现 Services.cs L29-34 / L48-51
3. **池行为 PASS**：ObjectPoolTests.cs L70-82 `Release_ThenGet_ReusesWithoutInstantiate` 断言 `AreSame(first, second)` + `InstantiateCount==1`（复用零重复 Instantiate）；L84-101 `IPoolable_OnSpawnOnDespawn_CalledInOrder` 用 Log 列表断言精确序列 `["spawn","despawn","spawn"]`；L205-219 `PoolResetter_ClearsRigidbodyVelocity` 构造 GameObject+Rigidbody，置 velocity=(3,4,5)/angularVelocity=(1,2,3) 后断言均归 `Vector3.zero`
4. **代码审查 PASS**：Release 归位统一池父节点——ComponentPool.cs L150 `Store(instance)` → L213-220 `Store` 内 L215 `instance.transform.SetParent(poolRoot.transform, false)`，池根 L73 命名 `~Pooled(Type)#hash`（防幽灵对象，另有 L104-109 测试实证）；Get 主路径 L86-114 无 lambda/LINQ/闭包（仅 `(instance as IPoolable)?.OnSpawn()`），Release 主路径 L117-151 无 lambda/LINQ——字符串插值仅在异常/告警分支（L127-129、L139-142），非热路径
5. **asmdef PASS**：ZFrame.Core.asmdef L4 `"references": []` 仍为空
6. **文件规范 PASS**：抽查全部 7 新文件（Services/IPoolable/ComponentPool/PoolResetter/PoolRegistry/ServicesTests/ObjectPoolTests）——`od` 首字节无 EF BB BF（无 BOM），`tr -d -c '\r' | wc -c` 全部为 0（纯 LF）
7. **施工报告 PASS**：四节齐全（任务简报/施工报告含①-⑤/验收清单/验收结论），交付清单、自测 xml 证据、设计偏差 3 条、风险 3 条均如实记录
