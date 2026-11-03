# TASK-A-M1-01 ResourceService 资源服务（IAssetProvider 抽象 + Resources 实现 + 池联动）

状态: done待验收 | 派发: 2026-10-24 | 施工: general-purpose 施工子代理(收尾) | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

**目标**：ZFrame.Data 程序集第一件——统一资源服务：`IAssetProvider` 屏蔽底层（本期实现 ResourcesProvider，Addressables 后续任务换注册即可）、引用计数、与对象池联动（InstantiateAsync 优先走池）。**不装 Addressables 包**。

**设计要点**：
1. `ZFrame/Data/Resources/`：
   - `IAssetProvider`（Core 无关，放 Data）：`Task<T> LoadAsync<T>(string key) where T : Object`；`void Release(string key)`；`bool Exists(string key)`
   - `ResourcesProvider : IAssetProvider`：Resources.Load 同步包装（Task.CompletedTask 即时返回，XML doc 注明 Resources 无真异步，Addressables 实现会补真异步——这就是屏蔽点）；Release 时 `Resources.UnloadUnusedAssets` **不调用**（由批量释放策略统一处理，注明）
   - `IResourceService`：`Task<T> LoadAsync<T>(key)`（引用计数：同 key 二次加载复用已加载对象+计数+1）；`void Release(key)`（计数归零才真释放）；`Task<GameObject> InstantiateAsync(string key, Transform parent = null)`（加载 prefab→经 PoolRegistry.Spawn 走池）；`void ReleaseInstance(GameObject go)`（归池）；`void UnloadAllUnused()`（触发 Resources.UnloadUnusedAssets——场景切换钩子用）；统计（加载中数/缓存数/实例数，调试面板将来接）
   - `ResourceService : IResourceService` 实现类（构造注入 IAssetProvider）；静态门面 `Res`（=Services.Get<IResourceService>() 简写，未注册给清晰报错）
   - 注册入口：`ZFrameData.Init()` 静态方法（注册 ResourceService 进 Services，幂等）——供将来 Bootstrap 调用；本任务测试里手动调用
2. 测试资产（随仓库提交）：`Assets/ZFrame/Tests/Resources/` 下 `TestAsset.asset`（自定义 `TestConfigData : ScriptableObject{public int value}`）与 `TestCube.prefab`（内置 Cube+TestComponent 标记）；测试程序集里已有生成脚本可复用/新增 Setup 菜单生成一次（生成后入库，测试只读）
3. 单测（新建 `ZFrame.Data.Tests.asmdef`，仅 Editor，引 ZFrame.Data+Core）：
   - Provider：Exists 真假两路；LoadAsync 返回非 null 且类型正确
   - 计数：同 key 两次 Load 只加载一次对象（引用相等断言）、两次 Release 不提前释放（可用内部计数属性断言）、归零后再 Load 重新走 Provider（可用 Provider 调用计数器 mock/子类验证——**做一个小 FakeProvider 用于计数断言**，真实 Resources 路径另测）
   - 池联动：InstantiateAsync 两次同 key→第二次复用同一 GameObject（先 ReleaseInstance 第一次的）；非池路径回退正确
   - Res 门面未注册报错清晰
   - 测试结束 `ZFrameData.Shutdown()`/Services 清理（防污染其他测试）
4. 自测：EditMode 全绿（Unity 纪律：清锁/轮询/taskkill）

**约束**：只动 `UnityProject/Assets/ZFrame/Data/`、`UnityProject/Assets/ZFrame/Tests/`、本任务日志；并行代理也在用 Unity 工程（它主要在 Native/ 但其 C# 测试也要跑 Unity）——**Unity 批处理撞锁时等 60s 重试，最多 10 次**；不装包；不 git commit；UTF-8 无 BOM/LF；架构校验器须保持零违规（新 asmdef 要符合矩阵——Data 引 Core，Data.Tests 豁免组）。

**交付物**：接口+实现+池联动+Fake/真实双路单测全绿+施工报告四节。

## 施工报告（施工代理写）

### ① 摘要

接手收尾 + 修 4 失败。前施工代理已将实现代码与测试全部落盘（Assets/ZFrame/Data/ 六文件 + Tests/Data/），但停滞于一次 88 过 84 的 EditMode 运行。本次接手核对失败堆栈与盘上代码后确认：**4 处缺陷的修复已由前代理写入磁盘但未复测**——失败运行的堆栈行号（ResourceServiceTests.cs:113/127/148、Prefab 无 TestComponent）与盘上源码对不上；盘上 FakeProvider 在构造时即预存 key（MakeFakeProvider），TestCube.prefab 的 m_Script guid（62e85380f6d36e242a831f819f265def）与 Support/TestComponent.cs.meta 一致，prefab 确带组件。本次未改动任何实现/测试代码，直接复测：EditMode 88/88 全绿，ValidateFromCommandLine exit 0。

### ② 清单

- `Assets/ZFrame/Data/`：IAssetProvider / ResourcesProvider / IResourceService / ResourceService / Res / ZFrameData + ZFrame.Data.asmdef（前代理完成，本次只读复核）
- `Assets/ZFrame/Tests/Data/`：ResourceServiceTests（13 用例：Provider 真路 4 + FakeProvider 计数 3 + 池联动 3 + 门面/生命周期 3）、Support/（TestConfigData+TestComponent+Support.asmdef 运行时程序集）、TestAssetSetup（资产生成菜单）、ZFrame.Data.Tests.asmdef
- `Assets/ZFrame/Tests/Resources/`：TestAsset.asset（value=42）+ TestCube.prefab（Cube+TestComponent）已入库，测试只读
- 本次修改：仅本任务日志（施工报告）与 TempLogs 证据文件；**零代码改动**

### ③ 自测 xml 关键行（原证据文件已按收尾指令随 TempLogs 清理，关键行抄录于此；验收代理自跑复现）

- m1_01_results.xml：`testcasecount="88" result="Passed" total="88" passed="88" failed="0"`（ZFrame.Core.Tests 58 + ZFrame.Data.Tests 13 + Debug 等 17）
- m1_01_validate.log：`ExitCode: 0 Duration: 0s172ms`；arch_report.txt：`asmdef 扫描数: 15…违规数: 0 结果: PASS`

### ④ 偏差

- 无行为语义偏差。前代理遗留的 4 处失败经查并非需新修代码，而是"已修未验"：FakeProvider 旧版未预存 key + prefab 生成旧版漏挂组件，均已在其最后一次编辑中修正，本次仅复测确认
- Unity 测试运行后残留一个僵死 Unity.exe 子进程（PID 9392，持有 UnityLockfile），按纪律 taskkill //F 后重跑 Validate 成功；另有一次撞锁（exit 21）由该残留锁导致

### ⑤ 风险

- 低。EditMode 全绿且未改产品代码；唯一注意点：并行代理共用 Unity 工程时僵死子进程会持锁，撞锁先查残留进程再等 60s
- Resources.Load 无真异步的屏蔽点已在 ResourcesProvider XML doc 注明，Addressables 切换=换注册的注释存在于 UnloadAllUnused 与 IAssetProvider

## 验收清单（主控写）

1. 自跑 EditMode 全绿（用例数写入结论）
2. 引用计数真实断言（FakeProvider 调用计数：两次 Load 一次 Provider 调用、归零后再 Load 再调）
3. 池联动复用断言（同 key 二次 Instantiate 复用同对象）
4. 架构校验器跑批处理 exit 0（新 asmdef 已入矩阵或属豁免组——验收代理自跑）
5. Resources 无真异步的屏蔽点有 XML doc 说明；Addressables 切换=换注册的注释存在
6. 文件规范抽查
7. 施工报告完整

## 验收结论（验收代理写）

**PASS（7/7）** ｜ 验收人：general-purpose 验收子代理（与施工不同代理，零代码改动）｜ 2026-10-24

1. **PASS** — 独立自跑 EditMode（清锁 + `-runTests`，遵守批处理纪律）：`Temp/acc_m1_01_results.xml` `testcasecount="91" result="Passed" total="91" passed="91" failed="0"`，无任何 Failed 用例（Core 58 + Data 13 + Debug 等 + 并行 B 轨 ZMesh 3，总数与施工报告"88+并行新增"口径吻合）。首轮运行 Unity 在 Cleanup mono 阶段崩溃未落盘 xml，重跑一次即全绿；跑后按纪律 taskkill 僵死 Unity（PID 25692）。注：TempLogs 里并行代理遗留的 `zmesh_editmode.xml`（91 例 2 败）是其调试中间产物，与本任务无关且当前全绿已复现覆盖。
2. **PASS** — `ResourceServiceTests.cs` FakeProvider 调用计数真实断言：`RefCount_SecondLoad_ReusesObjectWithoutProviderCall`（L116-128，两次 Load 后 `Assert.AreEqual(1, fake.LoadCalls)`）；`RefCount_AfterZero_ReloadGoesThroughProviderAgain`（L151-163，归零再 Load 后 `Assert.AreEqual(2, fake.LoadCalls)`）。
3. **PASS** — 池联动复用断言存在：`Pool_InstantiateTwice_AfterRelease_ReusesSameGameObject`（L167-188），`Assert.AreEqual(go1.GetInstanceID(), go2.GetInstanceID(), "second InstantiateAsync must reuse the pooled instance")`。
4. **PASS** — 自跑 `-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine`：**exit 0**；报告 asmdef 扫描数 15（期望矩阵 10 + Tests 豁免）、违规数 0、结果 PASS（新 asmdef ZFrame.Data/ZFrame.Data.Tests(+Support) 已被正确覆盖）。
5. **PASS** — `ResourcesProvider.cs` L16-23 XML doc 明示"Resources has NO true async loading…Addressables implementation will provide real asynchronous loading"；换注册注释：ResourcesProvider L7-13（"migrating to Addressables later is a new provider class + one registration change"）、`IAssetProvider.cs` L7-19、`ResourceService.cs` L157-159（UnloadAllUnused 的 Addressables 路由 NOTE）。
6. **PASS** — 抽查 4 文件（ResourcesProvider.cs / ResourceService.cs / ZFrameData.cs / ResourceServiceTests.cs）：均 UTF-8 无 BOM（首 3 字节 `75 73 69`="usi"）、LF（od 全文 `\\r` 计数 0）。
7. **PASS** — 施工报告四节齐全：任务简报/施工报告（①摘要②清单③自测④偏差⑤风险）/验收清单/验收结论，含"接手收尾"说明与零代码改动声明，与盘上代码核对一致。

验收后清理：删除本人产生的临时 xml/log 与 TempLogs/arch_report.txt，无残留 Unity 进程、无锁文件。
