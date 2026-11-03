# TASK-A-R8-02 Addressables 切换验证（IAssetProvider 第二实现落地）

状态: doing | 派发: 2026-10-27 | 施工: general-purpose 施工子代理（2026-10-27 完成，EditMode 601/601 全绿 + PlayMode 6/6 全绿 + 三校验器 exit 0） | 验收: general-purpose 验收子代理（2026-10-27 独立验收，五项全 PASS） | commit: （待填）

## 任务简报（主控写）

**目标**：方案 §4.1 的核心承诺验证——"资源底层屏蔽：Resources 切 Addressables 业务零改动"：安装 Addressables 包、实现 `AddressablesProvider : IAssetProvider`、**双 Provider 并存可切换**、EditMode 测试两种模式下载入同一资产（用例级证明）。

**设计要点**：
1. 包：manifest.json 加 `"com.unity.addressables": "1.21.19"`（2022.3 官方配套；版本以 Package Manager 解析为准，报告记录实际版本）
2. `ZFrame/Data/Addressables/`（新 asmdef `ZFrame.Data.Addressables`，引用 ZFrame.Data+Addressables 运行时程序集——**架构矩阵需同步**：改 ArchitectureValidator 期望矩阵加此程序集，引用仅 ZFrame.Data）：
   - `AddressablesProvider : IAssetProvider`：`LoadAsync`→`Addressables.LoadAssetAsync<T>`（引用计数：同 key 复用 handle 表）；`Release`→`Addressables.Release(handle)`；`Exists`→`ResourceManagerConfig`/Resources 检查？——**Addressables 无 Exists 原语**：用 `AddressablesResourceDirectory` 或预生成的 key 清单（报告选型说明）；`InstantiateAsync`→`Addressables.InstantiateAsync`（走池逻辑与 Resources 版一致：经 ResourceService 层统一，Provider 只管 load/release）
   - **测试友好**：EditMode 下 Addressables 需 PlayModeScript——用 `EditorHostedPlayMode`（`Addressables.BuildScript` 无内容构建即可解析 asset database）……EditMode 是否可用 Addressables？**谨慎**：Addressables 官方推荐 PlayMode 测试。策略：单测主要测 Provider 的**引用计数/复用/释放逻辑**（注入 fake handle 抽象 `IAddressablesAdapter` 薄接口隔离 UnityEngine.Addressables 静态调用），真实 Addressables 路径用一条 **PlayMode 冒烟用例**（追加到 PlayMode 测试程序集：初始化→加载 TestAsset→断言）
3. **切换接线**：`ZFrameData.Init(AddressablesMode off/on)` 或 `UseAddressables()` 静态开关（默认 off 保持现状零破坏）；ResourceService 构造已注入 IAssetProvider——切换=注册不同 Provider，业务零改动（README 差异附录补验证记录）
4. 测试资产：TestAsset/TestCube 任一入 Addressables group（Setup 脚本：创建默认 group+加 entry，EditorHostedPlayMode 下无需真构建）
5. 单测（ZFrame.Data.Addressables.Tests 新程序集）：FakeAdapter 计数断言（两次 Load 一次底层调用/归零释放/复用）；PlayMode 1 条真实加载链
6. 自测：EditMode 全绿+PlayMode 全绿+Validate exit 0（新 asmdef 已入矩阵）

**约束**：可动 UnityProject/Packages/manifest.json、Assets/ZFrame/Data/Addressables/、Assets/ZFrame/Editor/ArchitectureValidator.cs（矩阵加行）、Assets/ZFrame/Tests/、Assets/ZFrame/Data/（Init 开关小改）、本任务日志；不 git commit；UTF-8 无 BOM/LF。

**交付物**：Provider+切换开关+双轨测试（Fake 单测+PlayMode 真链）+施工报告四节（含 Exists 选型与 PlayModeScript 说明）。

## 施工报告（施工代理写）

**施工**: general-purpose 施工子代理 ｜ 完成时间: 2026-10-27 ｜ 自测: **EditMode 601/601 全绿（Unity exit 0）+ PlayMode 6/6 全绿（Unity exit 0）+ 架构/配置/命名三校验器 exit 0**

### 1. 交付物清单

实际包版本：**com.unity.addressables 1.21.19**（简报指定版本，Package Manager 从 packages.unity.cn 解析成功；传递依赖 com.unity.scriptablebuildpipeline 解析为 **1.21.25**，≥声明的 1.21.21）。

| 路径（UnityProject/ 下） | 行数 | 职责 |
|---|---|---|
| `Packages/manifest.json`（改） | +1 | 加 `"com.unity.addressables": "1.21.19"`；`packages-lock.json` 为 UPM 解析自动更新（addressables + scriptablebuildpipeline 两条目），未手改 |
| `Assets/ZFrame/Data/Addressables/ZFrame.Data.Addressables.asmdef` | 16 | 新程序集：references 仅 `ZFrame.Data` + `Unity.Addressables` + `Unity.ResourceManager`（框架内引用仅 ZFrame.Data，不依赖 ZFrame.Core——经 IAssetProvider 反接） |
| `Assets/ZFrame/Data/Addressables/IAddressablesHandle.cs` | 14 | 不透明释放令牌接口（Fake 可替身的缝） |
| `Assets/ZFrame/Data/Addressables/AddressablesLoadResult.cs` | 27 | 一次底层加载结果 `{Asset, Handle}` |
| `Assets/ZFrame/Data/Addressables/IAddressablesAdapter.cs` | 37 | **薄接口**：`LoadAsync<T>/Release/Exists`，隔离 UnityEngine.Addressables 全部静态调用（EditMode 注 Fake） |
| `Assets/ZFrame/Data/Addressables/AddressablesRuntimeAdapter.cs` | 188 | 唯一真实现：InitializeAsync+LoadAssetAsync+Release（IsValid 防双放）+Exists（locator 同步查询）+编辑器 GUID 播放数据路径 |
| `Assets/ZFrame/Data/Addressables/AddressablesProvider.cs` | 178 | `AddressablesProvider : IAssetProvider`：**引用计数 handle 表**（同 key 单一底层加载复用/并发 in-flight 合并/归零释放） |
| `Assets/ZFrame/Data/Addressables/AddressablesBootstrap.cs` | 31 | 切换安装器 `UseAddressables()`（注册工厂，幂等）+ `[RuntimeInitializeOnLoadMethod]` 自动安装 |
| `Assets/ZFrame/Data/AssetProviderMode.cs`（新） | 19 | 枚举 `{Resources=0, Addressables=1}`（默认 Resources=零破坏） |
| `Assets/ZFrame/Data/ZFrameData.cs`（改，开关小改） | +62 | `Init(IAssetProvider, AssetProviderMode)` + 工厂注册表 `RegisterProviderFactory`（逆向依赖，无循环引用） |
| `Assets/ZFrame/Data/Resources/ResourceService.cs`（改） | +7 | 仅加只读属性 `public IAssetProvider Provider`（验收项 3 的"Provider 类型断言"观测点，镜像 GetRefCount 的 tests/diagnostics 先例；行为零改动） |
| `Assets/ZFrame/Editor/ArchitectureValidator.cs`（改，矩阵加行） | +6 | `ZFrame.Data.Addressables` 行：允许 `{ZFrame.Data, Unity.Addressables, Unity.ResourceManager}` |
| `Assets/ZFrame/Tests/Addressables/`（新程序集 ZFrame.Data.Addressables.Tests，Editor-only） | 391 | `AddressablesProviderTests.cs`（306 行 13 用例，FakeAdapter 计数）+ `AddressablesTestSetup.cs`（85 行 Setup 脚本） |
| `Assets/ZFrame/Tests/AddressablesData/`（Setup 脚本生成并随任务提交） | — | AddressableAssetSettings.asset + Built In Data/Default Local Group + **ZFrameTests group（entry: TestAsset/TestCube，纯地址）** + DataBuilders 四件套——全部在 `ZFrame/Tests/` 白名单内，未触碰 Assets/AddressableAssetsData/ |
| `Assets/ZFrame/Tests/PlayMode/AddressablesSmokeTests.cs`（新）+ `ZFrame.PlayMode.Tests.asmdef`（改：+ZFrame.Data.Addressables/+ZFrame.Data.Tests.Support 引用） | 96 | PlayMode 真链 1 条 |

合计新增 981 行 C#/asmdef。`git status` 证实改动全部在约束路径内（manifest.json、Data/、Editor/ArchitectureValidator.cs、Tests/、lock 文件自动更新）；未 git commit。

### 2. 设计要点对照（简报 6 条）

1. **包安装**：manifest.json 加 1.21.19；UPM 解析成功（packages.unity.cn），PackageCache 落盘 `com.unity.addressables@1.21.19` + `com.unity.scriptablebuildpipeline@1.21.25`。包源 API 逐条核实（反查 PackageCache 源码）：`IResourceLocator.Locate(key, type, out locations)`（1.21.19 无 GetLocations，语义等价——未知 key 返回 false 不抛）、`Addressables.ResourceLocators`（静态 IEnumerable）、`Addressables.kAddressablesRuntimeDataPath`（public const）、`BuildScriptFastMode.BuildDataImplementation` 的 GUID 写入机制。
2. **Provider + 引用计数 handle 表**（AddressablesProvider.cs）：completed-entry 复用分支 :84-88（第二次 Load **零**底层调用，直接 `(T)entry.Asset`）；首载唯一底层调用 :92-95（`InFlight = _adapter.LoadAsync` 每 entry 生命周期一次）；并发 in-flight 合并 :97-101（ResourceService 同 key 双 await 也只打一次底层）；归零释放 :159-169（`_adapter.Release(entry.Load.Handle)` 后移除 entry）；失败即弃 :126-133（无悬挂 entry、无 handle 泄漏）；释放对未跟踪 key 容错（:153 warning+ignore，镜像 ResourceService）。实例化不走 Provider——`ResourceService.InstantiateAsync` 经 PoolRegistry 统一，与 Resources 版逐字一致（零改动，简报点 2 的"Provider 只管 load/release"）。
3. **切换开关（默认 off 零破坏）**：`ZFrameData.Init(IAssetProvider assetProvider = null, AssetProviderMode providerMode = AssetProviderMode.Resources)`（ZFrameData.cs:97）——不传即 Resources 原路径；`AddressablesBootstrap.UseAddressables()` 只做工厂安装（幂等），`Init(providerMode: Addressables)` 才切换；工厂注册表为逆向依赖（ZFrame.Data 不认识 ZFrame.Data.Addressables），并且 `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`（AddressablesBootstrap.cs:23）进 Play 即自动安装——**游戏侧切换=Init 加一个参数，零新增引用**。既有 `Init()` 调用点（GameBootstrap/既有 542 用例）签名兼容零改动，EditMode 601 全绿即零破坏证明。
4. **测试资产入 group**：`AddressablesTestSetup.Generate`（菜单 ZFrame/Tests/Addressables/Generate Test Setup，批处理 `-executeMethod ZFrame.Data.Addressables.Tests.AddressablesTestSetup.Generate`）幂等生成第 1 节所列配置；ZFrameTests group 已核对含 `m_Address: TestAsset` / `m_Address: TestCube` 两条 entry。无需内容构建（见 §3）。
5. **Fake 单测 + PlayMode 真链**：FakeAdapter（AddressablesProviderTests.cs:21-119）计数 TotalLoadCalls/LoadCallsPerKey/ReleasedHandleIds，支持同步完成与"驻留模式"（`CompleteSynchronously=false` + `Resolve/Fail`）测并发 in-flight；13 用例覆盖：复用（:142 两次 Load 断言 TotalLoadCalls==1）、归零释放（:157 上层不放/+164 恰一次）、重载走底层、**并发合并**（:191）、失败弃 entry、Exists 委托、null/空 key、切换四用例（默认 Resources :257 / Addressables :270 / 显式 provider 优先 / Init 幂等保持首后端）+ 工厂 null 校验。PlayMode 真链 1 条 `Addressables_SwitchRealChain_LoadTestAssetAndCube`（AddressablesSmokeTests.cs:31，results.xml 全名 ZFrame.PlayMode.Tests.AddressablesSmokeTests.…）：UseAddressables→Init(Addressables)→**真 InitializeAsync→LoadAssetAsync**（TestAsset value==42 :46 + TestCube prefab）→缓存同实例同步命中→归零→重载再走 Addressables→**恢复 Resources 后端**（:71-73，不毒化既有 5 条 boot-chain 用例）。
6. **自测**：§4 证据表。

### 3. Exists 选型与 PlayModeScript 说明（简报要求）

**Exists 选型**：Addressables 无同步 Exists 原语，候选三条——①尝试加载再释放（有副作用，否）；②预生成 key 清单（额外资产、需与 group 同步，否）；③**注册 locator 的同步查询**（采纳）：真实现 `AddressablesRuntimeAdapter.Exists`（AddressablesRuntimeAdapter.cs:93-111）遍历 `UnityAddressables.ResourceLocators`，任一 locator `Locate(key, typeof(Object), out locations)` 命中即 true。理由：无加载副作用、无额外资产；编辑器 AssetDatabase 模式（AddressableAssetSettingsLocator）与真机目录模式（ContentCatalogData→ResourceLocationMap）都实现同一接口，行为一致；未初始化（无 locator）时诚实返回 false 并在 XML doc 注明。ResourceLocationMap.Locate 对未知 key 返回 false 不抛（已核实包源 ResourceLocationMap.cs:117-125）。Fake 侧 `Exists=Store.ContainsKey` 同语义。

**PlayModeScript 说明（EditMode 可用性策略）**：Addressables 官方推荐 PlayMode 测试，简报策略照办——EditMode 只测 Fake 注入的计数逻辑，真链只进 PlayMode。播放模式脚本取**"Use Asset Database (fastest)"等价链路**：1.21.19 中该模式 = `BuildScriptFastMode`（编辑器挂起时仅写 PlayerPrefs `AddressablesRuntimeDataPath=GUID:<settings guid>`）+ 运行时 `FastModeInitializationOperation`（注册 AddressableAssetSettingsLocator + AssetDatabaseProvider，零内容构建）。本实现**不依赖包的 EditorBuildSettings 默认对象钩子**（那会把 DefaultObject.asset 固定生成在 Assets/AddressableAssetsData/ 且改 ProjectSettings，越出本任务路径约束）：Adapter 在编辑器下镜像 BuildScriptFastMode 自己写同一 PlayerPrefs GUID 键（`EnsureEditorPlaybackDataPath`，AddressablesRuntimeAdapter.cs:121-140，FindSettingsAssetGuid:148-173 经 `AssetDatabase.FindAssets("t:AddressableAssetSettings")` 定位——无编辑器程序集依赖；多 settings 时优先 ZFrame/Tests/AddressablesData 并告警），每次 init 前重设故陈旧 GUID 自愈；`#if UNITY_EDITOR` 包裹，真机构建整块裁剪、回落标准目录路径（RuntimePath/settings.json）。因此配置资产可落在白名单 `Assets/ZFrame/Tests/AddressablesData/`，且无需任何内容构建/播放前 hook。

### 4. 自测证据

| 项 | 结果 | 证据 |
|---|---|---|
| Unity EditMode | **601/601 全绿，failed=0，Unity exit 0** | `TempLogs/runtests_editmode.xml`（total=601 passed=601 failed=0 skipped=0）；经 `run_tests.ps1 -Segments editmode`（Git Bash 启动、清锁、无 -quit、轮询 xml 纪律）；算术自洽：588（A-R8-01 基线）+13 新用例=601 |
| Unity PlayMode | **6/6 全绿，failed=0，Unity exit 0** | `TempLogs/runtests_playmode.xml`（total=6 passed=6 failed=0）；5 条 boot-chain 基线 + 1 条 Addressables 真链（同名核对 xml 内 fullname） |
| 架构校验器 | **exit 0** | `TempLogs/arch_report.txt`：asmdef 扫描 24（22+2）、期望矩阵 11 条（10+1）、违规 0、PASS |
| 配置/命名校验器 | **均 exit 0** | `TempLogs/config_report.txt` / `TempLogs/name_report.txt`（新配置资产在 ZFrame/Tests 白名单内） |
| 文件规范 | 17 个触碰文件逐一校验 | 首字节非 EF BB BF、CR=0（纯 LF）、尾字节 0A；新 .cs 无尾随空格 |
| 改动范围 | git status 全部在约束路径 | 5 改（manifest/packages-lock/ArchitectureValidator/ZFrameData/ResourceService/PlayMode asmdef）+ 新增 Data/Addressables、Tests/Addressables(+Data)、PlayMode 测试文件；packages-lock.json 为 UPM 自动更新（addressables/scriptablebuildpipeline 两行，diff 仅此） |

### 5. 施工过程如实记录（三轮编译修复，全部根因定位）

1. CS0104 `Object` 歧义（`using System`+`using UnityEngine` 并存）→ 三文件显式 `UnityEngine.Object`；连带 CS0425（约束歧义）随之消除。
2. CS0234：本程序集命名空间就叫 `ZFrame.Data.Addressables`，把 `Addressables.InitializeAsync` 等静态调用解析成了命名空间成员（命名空间遮蔽类名）→ 在命名空间内声明别名 `using UnityAddressables = UnityEngine.AddressableAssets.Addressables`（别名必须置于命名空间声明内才能在查找序中胜出，置于文件顶无效）。
3. CS1503：`handle.Completed` 回调参数是 `AsyncOperationHandle<T>`，与 `NativeHandle(AsyncOperationHandle<Object>)` 不匹配 → NativeHandle 改存非泛型 `AsyncOperationHandle`（隐式转换，释放/IsValid 语义不变）。
4. 工具备注：run_tests.ps1 的 `-Segments` 从 Git Bash 调用时应逐段单传（`-Segments config,naming` 会因 PowerShell 参数绑定成单字符串而 ValidateSet 失败）——分两次 `-Segments config` / `-Segments naming` 通过。

### 6. 遗留与建议（不阻塞验收）

- `ZFrameData.Init(providerMode: Addressables)` 在工厂未安装时抛 InvalidOperationException 并给出接线提示（防御路径，因静态状态跨用例持久、注入式负例会造成用例顺序耦合，未写单测；提示文案已覆盖）。
- 编辑器下若项目未来出现第二个 AddressableAssetSettings（如正式开启主线 Addressables），Adapter 取路径含 `ZFrame/Tests/AddressablesData` 者并告警——届时主线应建立自己的初始化策略（Adapter 的 GUID 机制可直接复用）。
- `UnloadAllUnused` 的 `Resources.UnloadUnusedAssets()`（ResourceService.cs 既有 NOTE）在 Addressables 模式下语义偏 Resources——本次未动（行为无碍：Addressables handle 由 Release 精确管理）；后续若切主线可经 Provider 增加 `UnloadAllUnused` 可选接口。
- 真机 Player 构建（Addressables 目录链路）未在本任务范围（无打包需求）；`#if UNITY_EDITOR` 裁剪保证 Player 走标准 catalog 初始化，代码路径已备好。

## 验收清单（主控写）

1. EditMode 全绿+PlayMode 全绿（用例数写入结论）——装包零破坏
2. Fake 计数断言真实（两次 Load 一次底层/归零释放——行号）；PlayMode 真链用例存在
3. 切换开关：Init 默认 off（现状零破坏——既有 Resource 用例无改动通过即证）；UseAddressables 后 ResourceService.Get 的 Provider 类型断言用例
4. 架构矩阵：新程序集已加且 Validate exit 0（自跑）；引用仅 ZFrame.Data（asmdef JSON 核对）
5. 文件规范+施工报告完整（Exists 选型说明）

## 验收结论（验收代理写）

**验收: general-purpose 验收子代理 ｜ 2026-10-27 ｜ 结论: PASS（5/5 项全过，全部证据为验收代理亲跑/亲读，未复用施工方结论）**

1. **PASS**——验收代理亲跑（run_tests.ps1 单实例/清锁/无 -quit/GitBash 纪律）：EditMode `TempLogs/runtests_editmode.xml` total=601 passed=601 failed=0、Unity exit 0（xml 内点名 13 条 `ZFrame.Data.Addressables.Tests` 用例全过，588 基线+13=601 算术自洽）；PlayMode `runtests_playmode.xml` total=6 passed=6 failed=0、exit 0，真链用例 `AddressablesSmokeTests.Addressables_SwitchRealChain_LoadTestAssetAndCube` 在列；config/naming 校验器亦补跑 exit 0。
2. **PASS**——Fake 计数真实（亲读 AddressablesProviderTests.cs 行号）：两次 Load 一次底层 `:142 Assert.AreEqual(1, _fake.TotalLoadCalls)`（配 :143 LoadCallsPerKey==1）；归零精确释放 `:157` 有租约时 TotalReleaseCalls==0、`:163-164` 归零后恰一次且 ReleasedHandleIds.Count==1；in-flight 合并 `:191`（CompleteSynchronously=false 驻留 + `:195 Resolve`，两次并发断言 TotalLoadCalls==1、双 await 同实例 :197）；PlayMode 真链存在且断言完整（AddressablesSmokeTests.cs :46 value==42、:51 缓存同实例、:62 归零、:66 重载）。
3. **PASS**——切换语义：默认零破坏=既有用例文件零改动（git diff 证实 Tests 目录仅 PlayMode asmdef +2 行引用，无既有用例改动）而 601 全绿；Provider 类型断言用例亲读行号 `:257 Assert.IsInstanceOf<ResourcesProvider>`（Init_Default）/`:270 Assert.IsInstanceOf<AddressablesProvider>`（UseAddressables_Init）+ 显式 provider 优先 :276/幂等 :294；asmdef 亲核无循环：`ZFrame.Data.Addressables` 引用仅 {ZFrame.Data, Unity.Addressables, Unity.ResourceManager}，`ZFrame.Data` 仅引 ZFrame.Core（反向依赖经 RegisterProviderFactory 工厂注入，ZFrameData.cs diff 实证）。
4. **PASS**——验收代理自跑 `ValidateFromCommandLine` exit 0（arch_report.txt：asmdef 扫描 24/期望矩阵 11 条/违规 0/PASS）；强制语义核实（ArchitectureValidator.cs :158-160 未入矩阵即违规，故 exit 0 即新程序集已入矩阵且引用合规）；Exists 选型评估**合理**：locator 同步查询（AddressablesRuntimeAdapter.cs :93-111）无加载副作用、无额外资产、编辑器/真机两模式同接口，未初始化诚实返 false；依赖 API 事实经 PackageCache 包源独立核实（1.21.19 `kAddressablesRuntimeDataPath` :531、`ResourceLocators` :627、`ResourceLocationMap.Locate` 未知 key 返 false 不抛）。
5. **PASS**——文件规范抽查 12 个新文件全合规（首 3 字节非 EF BB BF/CR=0 纯 LF/尾字节 0A/无尾随空格）；施工报告四节齐全（简报/施工报告/验收清单/本结论），三专项齐备：包版本（manifest.json:3 = 1.21.19，packages-lock 实际解析 scriptablebuildpipeline 1.21.25 ≥ 声明 1.21.21）、Exists 选型（报告 §3，已评合理）、PlayModeScript 镜像 FastMode（报告 §3 + 代码 EnsureEditorPlaybackDataPath :121-140/FindSettingsAssetGuid :148-173 与行号声称一致）。

**如实记录（不阻塞 PASS）**：①验收代理未重跑 native 段（本任务未触碰原生层，基线 170 用例由 B-R8-01 管辖）；②施工报告 §6 遗留 4 条经评估均为后续主线改进项，与本任务验收标准无冲突；③`Exists` 语义为"locator 可解析"而非"bundle 落盘存在"，与 IAssetProvider.Exists 在 Resources 版的"键可解析"语义对等，属正确选型而非缺陷。
