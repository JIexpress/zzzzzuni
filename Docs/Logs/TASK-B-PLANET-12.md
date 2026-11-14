# TASK-B-PLANET-12 —— IL2CPP + Android 各测一次（P2 末段）

- **轨道**：B / PLANET（设计稿 §11-12；D8 裁决：Android 判据对象=行星能力的**托管等效实现**；依赖 11 ✓）
- **状态**：todo
- **派发**：主控，2026-11-14

## 任务简报（主控写）

### 0. 使命与口径（D8 已裁决）

原生通道限 D3D12——Android 不跑 ZRender 原生。两段验证：
- **A 段（Windows IL2CPP）**：行星托管桥（07 交付）在 IL2CPP Player 下真跑一次——P/Invoke、类型保留、门控在 IL2CPP 下的行为
- **B 段（Android）**：行星能力的托管等效实现——WorldOrigin 重定位 + LOD 决策（C# 镜像已有）+ **地形高度纯函数的 C# 移植**（PlanetTerrainHeightM 纯数学，机械移植），桌面端先与原生探针数字**逐位对照**验证移植正确，再进 Android 构建；帧耗+精度各测一次（设备在位则真机测，不在位 APK 构建+MISSING 如实）

### 1. 做什么

1. **A 段 IL2CPP**：
   - `Tools/build_player.ps1` IL2CPP 口径构建（既有脚本，A-PKG 线用过）；行星门控/绑定/流式管理器进包
   - Player 真跑：行星通道激活日志（门控通过）+ 至少一组流式 Tick 证据（log 或截图）——**P/Invoke 在 IL2CPP 下不能静默失效**（链接期检查 vs 懒解析：ZrPlanetChannel 的 DllImport 在 IL2CPP 下行为核对，必要时 __Internal 或preserve 口径，选型写报告）
2. **B 段 Android 等效**：
   - **C# 高度纯函数移植**：PlanetTerrainHeightM（含其依赖的 noise/hash/格几何）→ C# 纯函数（放 Game/Domain/World 或 Planet，A-WORLD-01 边界纪律）——桌面端与原生探针数字（abi-precision JSON 的锚点高度/budget9 贴地扫描锚点值）**逐位对照**（float 产出口径；若 double→float 顺序有差异导致位差，逐点归因并写明口径）
   - Android 构建：`Tools/build_android.ps1`（既有链）出 APK
   - 设备在位（adb devices 有设备）→ 安装真跑一次：重定位+LOD 决策+C# 高度采样冒烟（log 证据）；设备不在位 → **MISSING+APK 构建证据**（如实，不编造真机数据）
3. **帧耗+精度各一次**：A 段 IL2CPP Player 帧耗（行星通道开销 vs 07 桌面数字对照）；B 段 C# 高度函数精度（与原生逐位对照）+ 设备帧耗（如真机可用）
4. **不碰**：Native/**（纯函数移植在 C# 侧；zplanet 语义零改动）、既有行星 doctest、TASKS/HANDOFF/WORKLOG
5. 白名单：UnityProject/Assets/**（新 C# 纯函数+测试+可能的 link.xml/preserve）、构建脚本参数（如需）、Build/ 产物（gitignore 口径照旧）、本日志

### 2. 铁律

- 约法三章四判据：真跑/真输入/缺就报缺（设备不在位=MISSING）/可复核；施工不 commit、不改看板；UTF-8 无 BOM/LF；零第三方依赖
- C# 移植与原生逐位对照是本任务的牙——位差必须逐点归因（数学等价但舍入序不同→写明；语义不等价→返工）
- Android 工具链在 C:/Users/1/Android（已装配勿重装，HANDOFF 速查）；构建走既有脚本

### 3. 完成判据

- A 段：IL2CPP Player 真跑证据（行星通道激活+流式 Tick）；P/Invoke 口径选型记录
- B 段：C# 纯函数移植+桌面逐位对照证据；APK 构建产物；设备帧耗/精度（真机数字或 MISSING+原因）
- 全量绿（EditMode/PlayMode/arch/naming）

## 施工报告（施工代理，2026-11-14）

### 0. 结论速览

**B 段核心交付全绿**：C# 高度纯函数移植（PlanetTerrainHeightM 及 noise/hash/格几何全链）与原生 **804 点 float 位模式逐位一致（0 位差）** + planet-budget/abi-precision 两份探针 JSON 锚点复现；**Android IL2CPP 正式包构建成功**（ZFrame.apk 38.76MB，IL2CPP 转换级证据齐）。**A 段 IL2CPP Player 构建与 Android 设备真跑均为 MISSING**（环境缺 Windows IL2CPP 模块 + 本机断网 + adb 无设备 + 既有 Handheld 编译缺陷阻断 Mono 兜底——逐项证据与最小 diff 见 §6）。全量绿：**EditMode 1223/1223（基线 1211 + 12）、PlayMode 114/114、arch PASS（0 违规）、naming PASS（0 违规）**。

### 1. 通过行（全部亲跑，证据 xml 本次亲写）

```
EditMode: total=1223 passed=1223 failed=0 skipped=0（基线 1211 → +12，只增不减；证据 TempLogs/runtests_editmode.xml）
PlayMode: total=114  passed=114  failed=0 skipped=0（基线 114 保持——本任务 PlayMode 零改动；证据 TempLogs/runtests_playmode.xml）
arch    : PASS exit 0（asmdef 扫描 27 / 期望矩阵 13（Tests 豁免）/ 违规 0；证据 TempLogs/arch_report.txt，本轮含 Game.Bootstrap 矩阵扩项验证）
naming  : PASS exit 0（违规 0；证据 TempLogs/name_report.txt）
Android : build_android.ps1 -Backend IL2CPP → RESULT: PASS，ZFrame.apk 40,637,720B（38.76MB），wall 0:17:47，Unity exit 0（证据 TempLogs/build_android_il2cpp_unity.log）
```

### 2. B 段第一交付：C# 高度纯函数移植（本任务的牙）

**移植体**：`UnityProject/Assets/Game/Domain/World/PlanetTerrainMath.cs`（新增 190 行，Game.Domain asmdef，purity 扫描目录内、零引擎类型）——`Native/zplanet/planet_math.h` 的逐式镜像：

| 原生 | C# 移植 | 说明 |
|---|---|---|
| `PlanetHashU32`（:295） | `HashU32` | xor-shift 16→乘 0x7feb352d→xor-shift 15→乘 0x846ca68b→xor-shift 16；uint 回绕语义同 C++ |
| `PlanetLatticeValue`（:307） | `LatticeValue` | 种子混入首级 0x9e3779b9，坐标逐级级联；负格点 `(uint)ix` 补码回绕同 `static_cast` |
| `PlanetValueNoise`（:317） | `ValueNoise` | quintic fade 三线性插值，插值表达式逐式同序 |
| `PlanetTerrainHeightM`（:353） | `TerrainHeightM` | 4 octave fBm（11.3/7.7/3.1 偏移；种子 +o·1013904223；`(s/norm)*2*2400` 乘序同原生） |
| `CubeSphereDir`/`CubeFaceBasisOf`（:399/:381） | `CubeSphereDir` | 六面基 + `p/|p|`，加乘序与原生一致（跨面棱逐位性质保持） |
| `ChunkFaceCoord`/`ChunkVertexIndex`（:418/:411） | 同名 | 规范式 `2g/total−1` |

**逐位依据**：float64 IEEE754 双精度 + round-to-nearest-even 在 C++（MSVC /O2 + 默认 /fp:precise，**无 FMA 缩并**——Native/CMakeLists.txt:24-29 旗标核查）与 C#（.NET JIT 同不缩并）同序同精度即逐位同值；`Math.Floor`/`Math.Sqrt` 与 `std::floor`/`std::sqrt` 同为正确舍入原语。

**三通道逐位/锚点对照**（`UnityProject/Assets/ZRender/Tests/ZrPlanetManagedHeightTests.cs`，新增 548 行 12 用例，EditMode 真实 zrender.dll）：

1. **原生 dll float 产出口径（最强通道）**——输入路径按 `zplugin.cpp:740-766` SampleHeight 内部算式逐式复刻（点=中心+dir·R → 减中心 → sqrt → 除 → 移植函数 → `(float)`），与 dll `ZR_Planet_SampleHeight` 出口 float **位模式（BitConverter.SingleToInt32Bits）比对**：
   - `NativeSampleHeight_ProbeScan100Points_FloatBitsBitwiseEqual`：--pbudget groundScan **100 点全等**；
   - `NativeSampleHeight_AllFaces_GridVertices_FloatBitsBitwiseEqual`：六面 8u×4v **192 点全等**（含共享棱端点）；
   - `NativeSampleHeight_HashDrivenDirections_FloatBitsBitwiseEqual`：哈希驱动全域方向 **512 点全等**；
   - **合计 804 点，位差 0，无任何逐点归因项**。
2. **planet-budget JSON 锚点**（Tools/Probes/out/planet-budget-2026-11-14T00-22-38.json，B-PLANET-09 验收 N2/N4 逐位复现档）：100 点 hPureDoubleM（3 位小数存档）全数复现（`Budget9Json_AnchorHeights_ReproducedByPort`）；4 个格点锚点（kind=vertex，254.267/−817.111/−267.696/−716.197）全数（`Budget9Json_VertexAnchors_...`）；**35 个 interior 点的散布位置（由 PlanetHashU32(0x5070BEEF^n) 决定）与 JSON 逐点一致**——哈希移植驱动同一点位选择的间接逐位证据（`Budget9Json_InteriorScatter_...`）。
3. **abi-precision JSON 锚点**（abi-precision-2026-11-14T00-57-35.json）：锚点顶点 anchorAbs（17 位有效数字）反推 `|posAbs−center|−R` 与移植值差 **1.0e-10 m**（反推链自身 double 舍入量级，紧于 3 位小数通道三个数量级；`AbiPrecision_AnchorVertex_HeightMatchesFullPrecisionArchive`）。

**移植自身语义钉**（4 用例）：确定性逐位（同输入位形恒同输出）、[0,1) 值域 + 负格点回绕确定性、跨面共享棱 ±1 端点两侧方向逐位（面 0↔1 / 面 0↔4）、**与 ZrPlanetChunkStreamer.CubeSphereDir 内部镜像逐位一致**（两份托管镜像不漂移，`CubeSphereDir_MatchesStreamerInternalMirror_Bitwise`，6 面 × 5u×5v=150 点）、值域 |h|≤2400 + 索引规范式。

### 3. B 段第二交付：Android IL2CPP APK + 托管等效装载

**构建**（Tools/build_android.ps1 默认 IL2CPP 口径，未改脚本）：

```
Unity exit: 0；ZFrame.apk = Build/Android/ZFrame.apk（40,637,720 B，2026-11-14 12:49:48）
log: [PlayerBuild] scripting=IL2CPP arch=ARM64 graphics=Vulkan,GLES3
     [PlayerBuild] result=Succeeded totalSize=677688859 errors=0
签名: useCustomKeystore=true（A-AND-08 既有 release 证书链路）
版本: bundleVersion=0.1.0.33 / versionCode 11（version.json build 32→33、versionCode →12 写回）
```

**IL2CPP 转换级证据（构建即取证——整库托管面经 IL2CPP AOT 转 C++ + NDK arm64 编译 0 错误）**：

- P/Invoke 懒解析口径（简报 §1-1 的核对点，**生成码级实锤**）：`Library/Bee/Android/Prj/IL2CPP/Il2CppBackup/il2cppOutput/ZFrame.Render.Bindings.cpp:4759-4776` 中 `ZR_Planet_SampleHeight` 桩 = **`il2cpp_codegen_resolve_pinvoke("zrender","ZR_Planet_SampleHeight",…)` 首调用解析**（`static` 桩缓存 + NULL 判定——懒解析而非链接期检查）；非 `__Internal`/`FORCE_PINVOKE_zrender_INTERNAL`（动态库按名解析）。**选型结论：现有 DllImport("zrender") 口径即正确，无需 __Internal/preserve/link.xml**——门控（ZrPlugin.EnsureLoaded→ZR_ApiVersion）在解析失败路径上抛 DllNotFoundException → WorldOriginPlanetLink/Driver 现有 catch → 整体关闭+一行日志，"静默失效"被门控纪律结构性排除。
- 转换覆盖（**无裁剪丢失、零 link.xml 依赖**，managedStrippingLevel=Minimal + 全部入口静态引用）：`PlanetRuntimeProbe` 在 Game.Bootstrap.cpp:226/324/512（含 `GameObject_AddComponent_TisPlanetRuntimeProbe_..._RuntimeMethod_var`）；`PlanetTerrainMath` 在 Game.Domain.cpp（24 处）；`ZrPlanetChunkStreamer` 在 ZFrame.Render.cpp（141 处）；`WorldOriginPlanetLink` 在 Game.View.cpp（17 处）；`ZFrame.Render.Bindings_CodeGen.c:118-120/279-281` 含 `ZrNative_ZR_Planet_GetStats/SampleHeight` 调用表条目。
- APK 本体：`lib/arm64-v8a/{libil2cpp.so,libunity.so,libmain.so,lib_burst_generated.so}` + `assets/bin/Data/Managed/Metadata/global-metadata.dat`（aapt badging：package com.ZFrame.ZFrame，versionCode 11，versionName 0.1.0.33，sdkVersion 22/targetSdk 34）。

**托管等效真跑（重定位+LOD 决策+C# 采样冒烟）→ MISSING**：`adb devices` 三次亲查（12:05 构建前/12:49 构建后/13:05 收尾）均 `List of devices attached` 空——本会话无设备在位。按简报铁律"无设备→MISSING+APK 证据（不编造真机数据）"。**设备侧冒烟装备已交付在包内**：`PlanetRuntimeProbe`（见 §5）在 Android 上自动激活（RuntimeInitializeOnLoadMethod，平台门控），一次真机安装即产出全部证据行（`[PlanetProbe]` 前缀，logcat 可 grep）——设备重位后**零改动**可复验。

### 4. B 段精度对照汇总（桌面侧实测）

| 对照项 | 结果 |
|---|---|
| C# 移植 vs 原生 dll（float 产出口径） | **804/804 点 float 位模式逐位相等，0 位差**（无归因项） |
| C# 移植 vs planet-budget JSON（100 点，3 位小数档） | 100/100 复现（差异全部 = JSON 存档舍入 ≤0.0005m） |
| C# 移植 vs abi-precision JSON 锚点（全精度档） | 差 1.0e-10 m（反推链舍入界内） |
| interior 点位选择（哈希驱动） | 35/35 与探针点位一致（哈希逐位的间接证据） |
| 移植内部语义（确定性/值域/跨面棱/双镜像一致性） | 4 用例全绿 |

**帧耗对照（B 段设备侧）→ MISSING**（无设备）；A 段 Player 帧耗 → MISSING（§6）。既有可引用基线：B-PLANET-09 探针 1080p 30-chunk 通道内部 p50=1.490ms/mean=1.528ms；B-PLANET-11 编辑器批处理 100-119fps@1080p、通道内部 LastFrameMs 0.05-0.67ms——Player 口径（IL2CPP/Mono）未有实测，不外推、不填 0。

### 5. Player 探针组件（两段共用的取证装备，已进包）

`UnityProject/Assets/Game/Bootstrap/PlanetRuntimeProbe.cs`（新增 451 行，Game.Bootstrap）——`[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]`，激活口径**与其余一切启动路径零行为变化**：

- **编辑器：恒不激活**（Application.isEditor 直接返回——EditMode/PlayMode 零影响，PlayMode 114/114 本轮实证）；
- **Windows Player：仅命令行 `-zrender-planet`**（与 ZRenderBoot 的 -zrender 同机制可叠加）→ 原生链模式：门控（PASS/FAIL 一行）+ rig（相机+WorldOrigin+Link+Driver，生产默认配置零偏置）+ 330 帧 100m/帧贴地行走（u=tanθ 测地线、悬停=移植高度+600m）→ WorldOrigin 4000m 重定位触发 ~8 次（计数+camAbs 漂移逐帧记账）+ 流式 stats 每 30 帧（live/create/destroy/vbytes/LastFrameMs/恒等式）+ IL2CPP P/Invoke 证据（Marshal.SizeOf 32/56/80 自证 + 16 方向移植 vs dll float 位对照）+ 帧耗 p50/mean/p95 + SUMMARY 自清场；
- **Android 设备：自动激活**（无命令行通道）→ 托管等效模式：同 rig 同行走（重定位证据）+ LOD 迟滞决策扫描（DecideLodLevel 780-1020km 全曲线一行）+ 16 方向 C# 高度采样 vs 内嵌原生链 float 位锚逐位对照（锚的权威性 = §2 通道 1 桌面 804 点全绿）+ 门控预期失败一行（D8：无原生通道，整体关闭证据）+ 帧耗 SUMMARY；
- 证据通道刻意为 log/数字（`[PlanetProbe]` 前缀）——**不截图**（B-PLANET-11 验收确立的 fixture 隔离纪律/多相机缺口 B-PLANET-15 未修，探针侧规避截图链路）。

**装配面改动**：`WorldOriginPlanetLink.Configure(origin, camera)`（+11 行 public 方法——Player 内无 UnityEditor.SerializedObject，程序化 rig 的合法注入口；既有序列化路径/测试零改动）；`Game.Bootstrap.asmdef` +`ZFrame.Render`/`+ZFrame.Render.Bindings`（Bootstrap 为最高装配层，向下引用渲染运行时无层级倒置；**矩阵已同步登记**——ArchitectureValidator.cs Game.Bootstrap AllowedReferences 扩 2 项+理由注释，arch PASS 实证）。

### 6. MISSING 清单（逐项原因链+证据，均环境/既有缺陷，非本任务代码问题）

**M1：Windows IL2CPP Player 构建 → MISSING**
- 原因链：①本机 Tuanjie 编辑器 2022.3.62f3c1 的 windowsstandalonesupport 仅装 Mono 变体（`Data/PlaybackEngines/windowsstandalonesupport/Variations/` 仅 mono/win32_win64_{development,nondevelopment}_mono 五项，无 IL2CPP 变体）→ 构建 Enter 后即 `Error building Player: Currently selected scripting backend (IL2CPP) is not installed.`（亲跑两次：首跑还命中旧 Mono 残留目录的 BuildFailedException，清 Build/Windows64 后复跑即本句，[PlayerBuild] result=Failed errors=1，证据 log 因后续构建轮转覆写，引句为亲读行）；②模块补装通道全断：本机无外网（download.unity.cn/.com curl 均不通，与 HANDOFF「GitHub 直连不通」登记一致）、Tuanjie Hub 无头 CLI 不认（HANDOFF 已登记勿重试）。**构建脚本与入口已就绪**：build_player.ps1 新增 `-Backend Il2cpp`（映射 BuildWindows64Il2cpp：先 SetScriptingBackend(Standalone, IL2CPP) 再走同一 Run()，IL2CPP 时超时默认抬至 2400s）——模块在位后一条命令即可补验。
- 状态复位：`BuildWindows64Mono` 入口已把 StandaloneWindows64 后端恢复 Mono2x（ProjectSettings 的 scriptingBackend Standalone: 0 = 默认值显式化，零行为差）。

**M2：Windows Player（任何后端）真跑 → MISSING（M1 的既有缺陷分支）**
- 兜底 Mono Player 构建被**既有生产缺陷**阻断：`Assets/ZFrame/View/Input/Touch/TouchHaptics.cs:60` `Handheld.Vibrate()` 在 StandaloneWindows64 player 编译域 `error CS0103: The name 'Handheld' does not exist in the current context`（亲跑 `[PlayerBuild] build failed: result=Failed errors=2`）。该文件为 A-AND-10（044c827）引入，**最后一次 Windows player 构建（B-R9-15）早于它**——EditMode/PlayMode 在编辑器域编译覆盖不到 player 目标的 #else 分支，缺陷自引入起潜伏。修复 4 行超出本任务白名单（简报白名单限定"新 C# 纯函数+测试+link.xml"），按 B-PLANET-07 G4-R1 先例 MISSING+最小 diff 回主控：

```diff
--- a/UnityProject/Assets/ZFrame/View/Input/Touch/TouchHaptics.cs
+++ b/UnityProject/Assets/ZFrame/View/Input/Touch/TouchHaptics.cs
@@ DefaultPulse() 的 #else 分支（:58-60）
 #else
+#if UNITY_IOS
             Handheld.Vibrate(); // iOS: documented light pulse（原语义保留——Handheld 类型仅 iOS/Android player 存在）
+#else
+            /* Standalone 等 player 目标无 Handheld 类型（A-AND-10 潜伏缺陷，
+               B-PLANET-12 MISSING M2 最小 diff）：无振动硬件，no-op。 */
+#endif
 #endif
```

**M3：Android 托管等效真机真跑（重定位+LOD+C# 采样+设备帧耗）→ MISSING**
- `adb devices` 三次亲查均为空（§3）；APK+装备已就绪（零改动可验）。精度对照的设备分支随之 MISSING；桌面侧 804 点逐位对照（§4）已成立——设备端 C# 与桌面 C# 的逐位期望有 IEEE754 基本算子（+−×÷√ 与 floor）在 NEON/SSE2 同为 round-to-nearest 的分析支撑，**但按铁律不以分析充测量**，实机逐位待设备在位。

### 7. 文件清单（+numstat/wc，全部白名单解释内）

| 文件 | 变更 | 内容 |
|---|---|---|
| `UnityProject/Assets/Game/Domain/World/PlanetTerrainMath.cs` | 新增 190 行 | C# 高度纯函数移植（§2 全表） |
| `UnityProject/Assets/ZRender/Tests/ZrPlanetManagedHeightTests.cs` | 新增 548 行 | 12 用例三通道逐位对照（§2/§4） |
| `UnityProject/Assets/Game/Bootstrap/PlanetRuntimeProbe.cs` | 新增 451 行 | 双模式运行时探针（§5） |
| `UnityProject/Assets/Game/View/World/WorldOriginPlanetLink.cs` | +11/0 | `Configure(origin, camera)` 程序化装配方法 |
| `UnityProject/Assets/Game/Bootstrap/Game.Bootstrap.asmdef` | +1/1（references 行） | +ZFrame.Render / +ZFrame.Render.Bindings |
| `UnityProject/Assets/ZFrame/Editor/ArchitectureValidator.cs` | +7/−1 | Game.Bootstrap 矩阵扩项登记+理由注释 |
| `UnityProject/Assets/Game/Editor/PlayerBuild.cs` | +26/0 | `BuildWindows64Il2cpp`/`BuildWindows64Mono` 批处理入口 |
| `Tools/build_player.ps1` | +20/4 | `-Backend {Default,Il2cpp,Mono}` 参数（构建脚本参数=白名单明示项） |
| `Tools/version.json` + `ProjectSettings.asset` | 构建管线写回 | build 32→33、versionCode→12、bundleVersion 0.1.0.33（PlayerBuild 唯一写路径，既有约定） |
| `.meta` ×3 | Unity 自动生成 | 新文件对应（未手改） |

**零触碰**：`git diff --numstat -- Native/` 为空（zplanet 语义零改动）；既有行星 doctest/探针源码零改动；TASKS/HANDOFF/WORKLOG 零改动；无 git commit。**工作区甄别**：Docs/Parity 5 张 PNG 与 Tools/Probes/out/* 为并行任务/既有 PlayMode 用例例行产物（B-PLANET-11 验收 §Q1 同款甄别）；Build/ 产物 gitignore 口径照旧。

### 8. 规范自检

新/改文本文件首字节 od 亲验无 BOM（.cs=`2f 2f 20`/`75 73 69`，asmdef=`7b 0a 20`）+ `tr -dc '\r' | wc -c` 全 0（LF）；Unity 批处理单实例纪律（tasklist 前检/删锁/测试勿带 -quit）全程遵守；无僵死 Unity 残留；零第三方依赖。施工代理未写验收清单与验收结论。

### 9. 遗留风险与移交

1. **M1/M2/M3 三项 MISSING 的复验成本已压至最低**：模块/设备在位后各一条命令（`build_player.ps1 -Backend Il2cpp` → 拷证据；`adb install` + 启动即取证）。建议主控裁决 TouchHaptics 最小 diff（§6 M2）单独派发或并入下一 Windows 包任务——它是**所有** Windows player 构建（Mono/IL2CPP）的共同阻断。
2. `version.json` build 33 已被本次 Android 成功构建消耗（构建链既有自增语义）；两次 Windows 失败构建未消耗版本号（失败不写回）。
3. IL2CPP 运行期行为（懒解析异常路径、Minimal 裁剪下 Marshal.SizeOf/SerializedObject 等价性）当前只有生成码级证据（§3），真机运行证据随 M3 补齐；现有门控+异常路径设计使"静默失效"无结构性通道。
4. 探针在 Windows Player 上的流式/门控证据也随 M1/M2 一并缺失——组件逻辑与 PlayMode/EditMode 已测路径同源（Driver/Link/Streamer 均有既有用例），风险限于"player 域装配差异"。

### 10. 验收建议

B 段建议 **PASS 候选**（移植+逐位对照+APK 全部成立）；A 段与设备真跑按 MISSING 口径挂账（§6 三项，均环境/既有缺陷+最小 diff 就绪）。判据核对：简报 §3 的"C# 纯函数移植+桌面逐位对照证据；APK 构建产物"两条成立，"IL2CPP Player 真跑证据"与"设备帧耗/精度"两条如实 MISSING——请主控按"跑不了的项写 MISSING+原因，不填 0"口径（设计稿 §10）裁决本任务闭环形态。

## 主控裁决（2026-11-14，施工报告后）

1. **B 段 PASS 候选确认**：移植逐位对照（804 点 0 位差）+APK 构建+P/Invoke 生成码实锤——验收范围=B 段全部。
2. **M1（Windows IL2CPP 模块缺失）**：**按 MISSING+原因 闭环**——环境限制（无外网+Hub CLI 不认），`build_player.ps1 -Backend Il2cpp` 链路已就绪，模块补装后一条命令补验；登记 backlog 待办。
3. **M3（Android 设备不在位）**：**按 MISSING+原因 闭环**——探针装备已进 APK，设备在位后零改动可验（logcat `[PlanetProbe]` 前缀）；登记 backlog 待办。
4. **M2（TouchHaptics CS0103，A-AND-10 潜伏缺陷）**：**批准 4 行最小 diff**——平台条件化 `Handheld.Vibrate()` 调用（移动平台行为不变，Windows Player 恢复可编译）。派 **X-HAPTICS-01** 微任务落地（微修+Mono Player 编译核验+全量回归）；B-PLANET-12 验收范围不含此项，X-HAPTICS-01 单独闭环。

## 验收清单（主控写，验收代理逐条跑并出证据）

- **T1 范围**：tracked diff 恰为新增 3 文件+3 meta（PlanetTerrainMath/ZrPlanetManagedHeightTests/PlanetRuntimeProbe）+ 修改 5 文件（WorldOriginPlanetLink/Game.Bootstrap.asmdef/ArchitectureValidator/PlayerBuild/build_player.ps1）+ 本日志；**Native/** 零 diff；看板/HANDOFF/WORKLOG 未被施工触碰；ArchitectureValidator 矩阵登记已含 Game.Bootstrap 变更（X-ARCH 债不复发）
- **T2 C# 移植逐位（核心牙）**：亲跑 ZrPlanetManagedHeightTests——804 点 float 位模式 0 位差；JSON 锚点复现（planet-budget 100 点+4 格点+abi-precision 锚差 1.0e-10m）
- **T3 APK**：ZFrame.apk 在档（38.76MB，versionCode 11，IL2CPP+ARM64）；P/Invoke 懒解析证据（生成码 `il2cpp_codegen_resolve_pinvoke`）读报告核对
- **T4 门控与装配**：WorldOriginPlanetLink.Configure 程序化装配读码；PlanetRuntimeProbe 双模式（Android 自动激活/桌面入口）读码；门控不过=整体关闭
- **T5 测试**：亲跑 EditMode（≥1223/0 failed）+ PlayMode（≥114）+ arch/naming PASS
- **T6 三项 MISSING 形态**：M1/M2/M3 按裁决登记（M2 转 X-HAPTICS-01；M1/M3 进 backlog 待办），本任务不因 MISSING 计 FAIL
- **T7 规范**：新文件 UTF-8 无 BOM/LF；四节结构完整

验收输出 PASS/FAIL + 逐条证据。你独占 Unity 批处理链。

## 验收结论（验收代理，2026-11-14）

**总判：PASS**（T1–T7 全过；A 段 IL2CPP Player 真跑与设备真跑按主控裁决 MISSING 闭环，不计 FAIL）。全部 Unity 批处理证据为本验收代理亲跑（独占链，EditMode/PlayMode/arch/naming + T2 过滤段均本轮亲写 xml）。

| 项 | 判 | 证据（本验收代理亲跑/亲读） |
|---|---|---|
| **T1 范围** | **PASS** | `git diff --numstat -- Native/` 为空（zplanet 语义零改动，亲跑）；tracked diff 与施工报告 §7 文件集逐一相符（WorldOriginPlanetLink +11/0、asmdef +1/1、ArchitectureValidator +7/−1、PlayerBuild +26/0、build_player.ps1 +20/4、version.json/ProjectSettings 管线写回）；新增 3 文件+3 meta（untracked）点名相符；HANDOFF/WORKLOG 零改动（git status 甄别）；TASKS.md 仅 +2/−1=主控派发登记（B-PLANET-12 doing 行 + X-HAPTICS-01 行），非施工触碰；ArchitectureValidator.cs:89-101 矩阵已登记 Game.Bootstrap +ZFrame.Render/+ZFrame.Render.Bindings 带理由注释（X-ARCH 债未复发，arch PASS 实证）；Docs/Parity 14 张 PNG 为既有 PlayMode 用例例行产物（B-PLANET-11 §Q1 同款甄别，非本刀内容） |
| **T2 C# 移植逐位（核心牙）** | **PASS** | 亲跑 `-testFilter ZrPlanetManagedHeightTests`（EditMode）：**12/12 passed 0 failed**（TempLogs/acceptance_planet12_t2.xml，13:50 本轮亲写）。逐位 804 点=100（用例内 `Assert.AreEqual(100, points.Length)` 钉死）+192（6 面×8u×4v）+512（哈希驱动全域），三用例的 mismatch 断言全 0 → **float 位差 0，无归因项**；输入路径亲核=zplugin.cpp:739-768 SampleHeight 内部算式逐式同构（rel=pos−center→sqrt→除→移植函数→(float)）；移植体逐式亲核=planet_math.h:295-427 全表（hash 常量/lattice 级联/quintic fade/fBm 偏移与乘序/六面基表/规范化式全一致）；JSON 锚点三用例（budget 100 点+4 格点 254.267/−817.111/−267.696/−716.197 亲 grep 在档+interior 散点+abi-precision 锚）本轮全绿；abi-precision JSON（Tools/Probes/out/abi-precision-2026-11-14T00-57-35.json）在档亲验 |
| **T3 APK** | **PASS** | `Build/Android/ZFrame.apk` 40,637,720 B（2026-11-14 12:49）亲 ls；aapt 口径由 zip 清单亲核：lib/arm64-v8a/{libil2cpp.so 55.9MB, libunity.so, libmain.so, lib_burst_generated.so}+assets/bin/Data/Managed/Metadata/global-metadata.dat；构建 log 亲读（TempLogs/build_android_il2cpp_unity.log:1482/8313/8323）：`scripting=IL2CPP arch=ARM64 graphics=Vulkan,GLES3`、`result=Succeeded totalSize=677688859 errors=0`、版本写回 0.1.0.33/versionCode 11→12；**P/Invoke 懒解析亲读生成码**（Library/Bee/Android/Prj/IL2CPP/Il2CppBackup/il2cppOutput/ZFrame.Render.Bindings.cpp:4759-4776）：`il2cpp_codegen_resolve_pinvoke<PInvokeFunc>(IL2CPP_NATIVE_STRING("zrender"), "ZR_Planet_SampleHeight", …)` 静态桩缓存+NULL 判定，非 `__Internal`/`FORCE_PINVOKE_zrender_INTERNAL`（懒解析而非链接期检查）——与报告 §3 选型结论一致 |
| **T4 门控与装配** | **PASS** | 读码亲核：WorldOriginPlanetLink.Configure(+11)=纯字段注入（序列化路径零改动）；PlanetRuntimeProbe 激活矩阵与报告 §5 逐条相符（编辑器 isEditor 直接 return 恒不激活/Windows 仅 `-zrender-planet`/Android 自动激活；cullingMask=0 零像素取证；SUMMARY 自清场）；门控链亲核=EnsurePlanetChannelReady→Enable/Disable「一行日志」纪律（ZrPlanetChannel.cs）+ Link 门控失败→Disable→每帧静默退出（整体关闭，无半启用通道）——「静默失效」无结构性通道 |
| **T5 测试** | **PASS** | 亲跑四段：**EditMode 1223/1223 failed=0**（TempLogs/runtests_editmode.xml 13:55，Unity exit 0，≥1223 达标）；**arch PASS 违规 0**（TempLogs/arch_report.txt 13:59:27，27 asmdef/13 矩阵）；**naming PASS 违规 0**（TempLogs/name_report.txt 13:59:47）；**PlayMode 首跑 113/114**——失败恰为 `AssemblyConnectPlayModeTests.ConnectedChain_GroundSettled_10Seconds_NoJitterNoExplosion` 误差 0.0500005335 vs <0.05（超差 5.3e-7 m，与 A-AI-03 §205/B-PLANET-11 §202/X-HAPTICS-01 §3 三份档案**同用例同值逐位一致**；当轮 machine quiet=BUSY，Taskmgr 32.4%——失败快照已存 TempLogs/acceptance_planet12_playmode_fail_snapshot.xml，用例文件零 diff 与本刀零因果）→ 静机（CPU 3%）**复跑 114/114 failed=0**（TempLogs/runtests_playmode.xml 14:04，NoJitterNoExplosion Passed）——既有「复跑归因」口径第四次成立 |
| **T6 MISSING 形态** | **PASS** | M1 本地实地复证：windowsstandalonesupport/Variations 仅 mono 五变体（IL2CPP 模块确缺）+ 无外网/Hub CLI 不可用为在档环境项 → MISSING+backlog 合规，`-Backend Il2cpp` 链路代码亲核在位（PlayerBuild.cs/build_player.ps1）；M2 按裁决转 X-HAPTICS-01（捎带验收 PASS，见该日志验收结论）；M3 本地实地复证：`adb devices` 空（无设备）→ MISSING+backlog 合规，探针装备（PlanetRuntimeProbe）在包内零改动可复验。三项均按「跑不了的项写 MISSING+原因，不填 0」（设计稿 §10 B-PLANET-12 行）闭环，不计 FAIL |
| **T7 规范** | **PASS** | 新/改文本文件首字节+CR 亲验全过（PlanetTerrainMath/ZrPlanetManagedHeightTests/PlanetRuntimeProbe/TouchHaptics/asmdef=无 BOM、CR=0 全 LF；两份日志同口径）；行数亲核 190/548/451 与报告一致；本日志四节齐（简报/施工报告/主控裁决/验收清单+本验收结论） |

**给主控的备注**：
1. X-HAPTICS-01 捎带验收 **PASS**（结论在其日志），可单独 commit——commit 时按目录核对 .cs↔.meta（TouchHaptics.cs.meta 实际零改动，无需配对新增）。
2. 报告级小勘误（不影响判）：X-HAPTICS-01 §1/§4 头标 `+7/−1`，实际 numstat **+8/−1**（其 diff 块本身即 8 行）。
3. Docs/Parity 14 张 PNG 与 Tools/Probes/out/*（含本验收轮未新增）为测试例行产物，commit 分箱请按 B-PLANET-11 §Q1 先例处置。
4. version.json build 34/versionCode 13、bundleVersion 0.1.0.34、AndroidBundleVersionCode 12、scriptingBackend Standalone: 0——与两份报告的管线写回语义逐项吻合（Mono 构建 33→34 为 X-HAPTICS-01 轮；32→33 与 versionCode 11→12 为本任务 Android 轮）。
5. M1/M3 backlog 复验命令各一条：`Tools/build_player.ps1 -Backend Il2cpp`；`adb install Build/Android/ZFrame.apk` 后启动看 logcat `[PlanetProbe]`。
