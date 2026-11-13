# TASK-B-PLANET-02 —— 行星 ABI 头落地（zplugin.h + def + ApiVersion 2→3 + Bindings 声明）

- **轨道**：B / PLANET（设计稿 `Docs/Planning/P2-行星ABI设计.md` §11 拆解第一条）
- **状态**：todo
- **派发**：主控，2026-11-14
- **依赖**：B-PLANET-01 设计稿（已闭环 80d82bc）；B-FSR-02（ApiVersion=2 现状）

## 任务简报（主控写）

### 0. 使命

把设计稿 §14 的权威签名汇编落进生产 ABI 头与托管绑定——**只落声明与错误码，不实现**（实现在 B-PLANET-04/05/06）。落完这一步，后续实现任务的接口就冻结了。

### 1. 做什么

1. **`Native/zplugin/zplugin.h`**：按 §14 抄入——
   - 四组函数声明：`ZR_Unity_SetCameraOrigin/GetCameraOrigin`、`ZR_Planet_ChunkCreate/ChunkDestroy/ChunkSetOrigin`、`ZR_Planet_SetLod`、`ZR_Planet_SetWorld/SampleHeight`（+设计稿定义的 `GetStats/ClearAll`）
   - 新结构体：`ZrPlanetChunkDesc`/`ZrPlanetWorld` 等（§14 的三个 static_assert 尺寸 56/32/80B 必须原样带上）
   - 错误码组：`ZR_PLANET_ERR_*` 七码（§4.5 全表）；`ZR_UNITY_ERR_ARGS` 复用于 NaN/Inf——**同步更新 zplugin.h:258 处注释定义**（修订轮 F7 指定项）
   - 函数指针 typedef（如 §14 所列）
2. **`Native/zplugin/zplugin.cpp`**：`ZR_ApiVersion` 2→3；新符号先落"声明+未实现占位"？**不**——设计稿口径：本任务只落头文件声明与版本号，zplugin.cpp 不加导出（导出在实现任务逐个落）——**但 zrender.def 是否加行、def 注释"十二导出"如何维护，照设计稿 §11-B-PLANET-02 要点执行；若设计稿与 def 现实冲突，MISSING+回主控裁决，不许擅自改口径**
3. **C# 绑定声明**（`UnityProject/Assets/ZRender/Bindings/`）：新函数的 `[DllImport]` 声明 + 结构体镜像（含 static_assert 对应的 Marshal.SizeOf 检查）+ `ZrPlugin.ExpectedApiVersion` 2→3 + 托管门控"≥3 才启用行星通道"（设计稿 §3.5/D6）
4. **版本消费方全同步**：test_smoke、ZrPluginTests 等全部断言点 2→3（参考 B-FSR-02 的同步清单）
5. **编译验证**：build_native.ps1 全绿（/WX 零警告）；Unity 侧编译零错误（EditMode 快速段或编译检查；若与并行 Unity 任务撞批处理，改读最近 results.xml 并注明）
6. **纪律**：本任务不改任何运行时行为——新导出未实现，def 不加行（未实现的符号进 def 会造成加载期缺符号），唯一 dll 变化=版本号。**dll 若因版本号变化重编部署，C# 侧 ExpectedApiVersion 必须同 commit 内一致**（新旧 dll/绑定混装的行为差异如实写进施工报告）

### 2. 测试

1. test_smoke：ApiVersion==3
2. C# ZrPluginTests：ExpectedApiVersion=3；结构体尺寸断言（56/32/80）
3. 全量原生 + EditMode 只增不减（≥353 原生 / ≥1167 EditMode）
4. **接口冻结自检**：§14 签名与 zplugin.h 逐条 diff 一致（验收要抄这份清单）

### 3. 铁律

- 约法三章；施工不 commit、不改看板；UTF-8 无 BOM/LF；/WX 零警告
- 设计稿是接口权威：任何偏离（签名/尺寸/错误码值）=MISSING+回主控裁决，不许自由发挥
- 只动：Native/zplugin/**、UnityProject/Assets/ZRender/Bindings/**、对应 Tests、build 产物

### 4. 完成判据

- zplugin.h 含 §14 全部声明且逐条一致；ApiVersion=3 全链同步；dll+绑定版本匹配
- 原生/EditMode 全绿；§14↔头文件 diff 自检进施工报告
- 实现任务（B-PLANET-04/05/06）可以直接按头文件开工

## 施工报告（施工代理，2026-11-14——主控自会话报告代录入）

**改动**（8 文件）：zplugin.h +156/−3（§14 声明块整体插入 :412-560；ERR_ARGS 注释扩充 :256-262）；zplugin.cpp +4/−1（ApiVersion→3u 仅此一处）；test_smoke.cpp +26/−3；ZrNative.cs +244/−0（10 DllImport+3 结构体镜像+ZrPlanetNative 门面含门控）；ZrPlugin.cs ExpectedApiVersion=3；ZrPluginInfo.cs 过期注释同步；ZrPluginTests +46/−3（含尺寸 32/56/80 断言）；dll 重部署（sha256 dbc586a4… 双侧一致）。

**§14 对照**：149 行中 147 行逐字节一致；2 处注释级偏差（①§14 追溯标注行；②组 1 note 时态改为已落地——F7 注释扩充的必然结果），签名/结构体/static_assert/错误码值零偏离。**def 0 diff**（导出表与 v2 完全相同，无加载期缺符号风险）。ApiVersion 链 6 消费方全同步（zplugin.h 注释/zplugin.cpp/test_smoke/ZrPlugin.cs/ZrPluginTests/ZrPluginInfo.cs），全仓 grep 无遗漏。

**测试**：原生 build_native ALL GREEN /WX 零警告——354 cases/778,763 assertions 0 failed（基线 353/778,746，+1/+17 全为本任务）；EditMode 1182/1182（基线 1167：本任务 +3 ZrPluginTests 5→8、并行 X-NAMES-01 WIP +12，零交集）。混装行为显式失败不静默（旧 dll+新绑定 / 新 dll+旧绑定均抛 InvalidOperationException）；行星门控 ZrPlanetNative.EnsurePlanetChannelReady 要求 ≥3。

**遗留**：①实现前绕过门控 P/Invoke 行星函数会符号解析崩溃——门控是唯一护栏（B-PLANET-07 必须先过门控）；②def 口径与设计稿 §11 无冲突（MISSING=无）；③EditMode 总数含并行 WIP 会漂移，本任务净增 3、零失败。

## 验收清单（主控写，验收代理逐条跑并出证据）

- **P1 范围**：tracked diff 恰为 8 文件集（zplugin.h/zplugin.cpp/test_smoke.cpp/ZrNative.cs/ZrPlugin.cs/ZrPluginInfo.cs/ZrPluginTests.cs/zrender.dll）；zrender.def 0 diff；AssetNamingRules*（X-NAMES-01 区）零触碰；TASKS/HANDOFF/WORKLOG 未被施工改动
- **P2 §14 对照抽查**：抽 6 项亲核（七错误码值+EDGE 掩码 1u<<0..3 / 三 static_assert 56/32/80 / 组1 双签名 / 组3 六签名 / SampleHeight / typedef 计数 10）与 Docs/Planning/P2-行星ABI设计.md §14 一致；2 处注释级偏差**主控已裁决接受**（不计 FAIL）
- **P3 ApiVersion 链**：六消费方逐个实读=3；全仓 grep `ApiVersion` 无遗漏（Tools/Probes 无引用）
- **P4 行为不变**：def 实读未加行；dll sha256 双侧一致；混装显式失败路径实读确认（EnsureLoaded 抛 InvalidOperationException、门控 EnsurePlanetChannelReady ≥3、裸 P/Invoke 注释在）
- **P5 测试**：亲跑 build_native.ps1 ALL GREEN /WX 零警告；repo 根 zrender_test.exe --success=false ≥354 用例 0 failed；EditMode 以 results.xml 为证（total ≥1182 且 failed=0；总数漂移须归因）；ZrPluginTests 8/8（含真 dll ApiVersion==3、Marshal.SizeOf 32/56/80）
- **P6 ERR_ARGS（F7）**：zplugin.h:256-262 扩充注释存在且与实际语义相符（复用于 NaN/Inf 拒绝）
- **P7 规范**：触及文件 UTF-8 无 BOM/LF；本日志四节齐

验收输出 PASS/FAIL + 逐条证据。并行提示：X-NAMES-01 施工在跑（C# 规则区，与你零交集）；你独占原生构建链。

## 验收结论（验收代理，2026-11-14）

**总结论：PASS**（P1–P7 全过；无 MISSING 项）。

| 项 | 结论 | 证据（验收代理亲跑/亲读） |
|----|------|--------------------------|
| P1 范围 | **PASS** | `git diff --name-only` tracked 恰为 8 文件集（zplugin.h/zplugin.cpp/test_smoke.cpp/ZrNative.cs/ZrPlugin.cs/ZrPluginInfo.cs/ZrPluginTests.cs/zrender.dll）+3 个已归因非本任务文件：TASKS.md（唯一 diff=X-NAMES-01 行 todo→doing，主控派发登记，非施工；B-PLANET-02 行未被施工改动）、AssetNamingRules.cs/AssetNamingRulesTests.cs（X-NAMES-01 施工区）。zrender.def `git diff` 0 diff。Docs/ 区无 tracked 改动（仅主控创建的未跟踪任务日志）；HANDOFF/WORKLOG 未被触碰。未跟踪文件=4 个任务日志 + Tools/Probes/out/env-*.json（探针输出，与本任务无关） |
| P2 §14 对照 | **PASS** | 机器 diff：设计稿 §14 代码块（P2-行星ABI设计.md:381-529）vs zplugin.h:412-560——仅 2 处注释级偏差（①:414 设计依据行追加「§14 权威签名块」；②:495-496 组 1 note 时态改「已随本块落地同步扩充」），147/149 行逐字节一致，**与主控裁决一致，不计 FAIL**。6 项抽查亲核：①七错误码 OK=0/NULL=-1/ABI=-2/RANGE=-3/HANDLE=-4/STATE=-5/FULL=-6/NONFINITE=-7（zplugin.h:429-436）+EDGE 掩码 1u<<0..3（:444-447）；②三 static_assert 32/56/80（:456/:472/:491）；③组 1 双签名 SetCameraOrigin(:500)/GetCameraOrigin(:504)；④组 3 六签名 ChunkCreate(:518)/ChunkDestroy(:523)/ChunkSetOrigin(:528)/SetLod(:533)/GetStats(:536)/ClearAll(:539)；⑤SampleHeight(:548)；⑥函数指针 typedef 恰 10 个（:551-560，与 §14 清单逐一对应）。签名/结构体/错误码值零偏离 |
| P3 ApiVersion 链 | **PASS** | 六消费方逐个实读=3：①zplugin.h:20-21 注释「当前 3——B-PLANET-02 追加行星通道 ABI」；②zplugin.cpp:34 `return 3u;`；③test_smoke.cpp:18-21 `CHECK(ZR_ApiVersion() == 3u)`；④ZrPlugin.cs:15 `ExpectedApiVersion = 3`；⑤ZrPluginInfo.cs:8 注释「当前约定为 3」；⑥ZrPluginTests.cs:18 `Assert.AreEqual(3u, info.ApiVersion)`。全仓 grep（Native/+UnityProject/Assets/）无其他断言点遗漏；Tools/ 无 `ApiVersion` 引用（grep 0 命中） |
| P4 行为不变 | **PASS** | ①def 实读：EXPORTS 表以 ZK_WorldStats 结尾，无任何 ZR_Planet_*/ZR_Unity_SetCameraOrigin 行，与 v2 导出表完全相同；zplugin.cpp:30-33 注释记录「def 不加行：未实现符号进 def 会造成加载期缺符号」。②dll sha256 双侧一致：`dbc586a46ea0c54c398acf3d31cc4f71c545aa137e129b41fe02192440fe9749`（Native/build/bin/Release 与 UnityProject/Assets/Plugins/ZRender；验收构建重部署后复核仍一致），与施工报告 dbc586a4… 相符。③混装显式失败路径静态审查：EnsureLoaded 版本不符抛 InvalidOperationException（ZrPlugin.cs:42-49「期望 3，实际 N」）；门控 EnsurePlanetChannelReady 要求 ≥MinPlanetApiVersion(3) 否则 InvalidOperationException（ZrNative.cs:637-646）；裸 P/Invoke 危险注释在位（ZrNative.cs:124-125「版本门控…未通过时不得触碰任何 ZR_Planet_* 导出，否则 P/Invoke 崩溃」、:575「调用下列方法会抛 EntryPointNotFoundException」）。按清单口径为静态代码审查，未做真 dll 混装实验（不构成 MISSING） |
| P5 测试 | **PASS** | ①build_native.ps1 亲跑：ALL GREEN（configure+build+test+deploy 4/4），doctest **354 cases / 778,763 assertions，0 failed**（基线 353/778,746，+1/+17 为本任务常量自检用例）；/W4 /utf-8 /MP /WX 实读确认（Native/CMakeLists.txt:27），构建零警告。②repo 根亲跑 `zrender_test.exe --success=false`：**354/354 passed, 0 failed**（输出中 FAILED 字样均为测试内预期负路径消息）。③EditMode fresh xml 亲跑（run_tests.ps1 -Segments editmode，Unity exit 0，runtests_editmode.xml 2026-11-14 03:14）：**total=1182 passed=1182 failed=0 skipped=0**——与施工报告总数一致，本次窗口无漂移；总数归因沿用施工报告口径（基线 1167 = 本任务 +3（ZrPluginTests 5→8）+ 并行 X-NAMES-01 WIP +12，零交集）。④ZrPluginTests 8/8 在 fresh xml 全列（EnsureLoaded_ReturnsApiVersion3 真 dll、PlanetNative_StructMirrors_MatchNativeAbiSizes=Marshal.SizeOf 32/56/80、PlanetNative_ChannelGate_PassesWithApiVersion3、PlanetNative_AbiConstants_MatchDesignDoc）。注：跑前 machine quiet check 报 BUSY（Taskmgr 29.4%）——仅影响耗时类数字可比性，不影响 PASS/FAIL 判定 |
| P6 ERR_ARGS（F7） | **PASS** | zplugin.h:256-263 扩充注释在位：「通用"参数值非法"——除原有的尺寸非法（0 或超过 16384）、槽位越界外，自本任务起复用于 double 参数 NaN/Inf 拒绝（ZR_Unity_SetCameraOrigin，见文件末尾行星通道声明块组 1）」；:262 定义 `ZR_UNITY_ERR_ARGS (-6)` 行内注释同义；与实际语义相符；托管侧同口径（ZrNative.cs:649 ErrArgs(-6)） |
| P7 规范 | **PASS** | 8 文本文件全部无 BOM（首字节 `// `/`# T`，无 EF BB BF）+ 纯 LF（`git ls-files --eol` 全部 i/lf w/lf、attr eol=lf；awk 字节级 \r 计数=0）。本日志四节齐（简报/施工报告/验收清单/验收结论——结论节由本验收追加） |

### 给主控的备注

1. **无 MISSING**：P2 两处注释级偏差按主控既有裁决接受；def 口径与设计稿 §11 无冲突（实现任务 B-PLANET-04/05/06 落导出时 def 逐个加行，本任务 def 0 diff 是正确状态）。
2. **EditMode 总数漂移监控**：本次 fresh 跑 1182 与施工报告一致；X-NAMES-01 继续施工时该数还会变——后续闭环以 failed=0 + 归因为准，勿锁死 1182。
3. **跑前环境**：UnityProject/Temp/UnityLockfile 为陈旧残留（tasklist 无 Unity.exe），run_tests.ps1 纪律自动清锁后干净启动；验收未与任何并行 Unity 批处理撞车。
4. **接口冻结生效**：§14↔zplugin.h 零签名偏离 + ApiVersion=3 六链同步 + dll/绑定同 commit 一致——B-PLANET-04/05/06 可直接按头文件开工（门控 EnsurePlanetChannelReady 是绕过实现缺口的唯一护栏，B-PLANET-07 须先过门控）。
