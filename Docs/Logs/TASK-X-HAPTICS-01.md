# TASK-X-HAPTICS-01 —— TouchHaptics 平台条件化修复（Windows Player 编译债，A-AND-10 潜伏）

- **轨道**：X / HYG（微任务；B-PLANET-12 施工发现，主控批准 4 行最小 diff）
- **状态**：doing
- **派发**：主控，2026-11-14
- **缺陷**：`TouchHaptics.cs:60` 的 `Handheld.Vibrate()` 在 StandaloneWindows64 player 域编译不过（CS0103）——自 A-AND-10（044c827）起潜伏；Windows Player 构建路径（Mono/IL2CPP 皆然）被阻断

## 任务简报（主控写）

1. **修法**：`Handheld.Vibrate()` 调用平台条件化（`#if UNITY_ANDROID || UNITY_IOS` 或等效 API 门），移动平台触觉行为**零变化**；Windows 域该调用不可达。B-PLANET-12 施工报告 §6 已有最小 diff 参考（4 行）
2. **核验**：
   - Mono Player 编译核验：`Tools/build_player.ps1`（Mono 后端，B-PLANET-12 已复位）走完编译段（Player 构建全量耗时长——至少编译到无 CS 错误；如脚本支持仅编译检查则用之，如实记录）
   - EditMode ≥1223 / PlayMode ≥114 / arch / naming 全绿
3. **白名单**：TouchHaptics.cs（+.meta 如 Unity 触发变更）、（如触觉有测试）其测试文件、本日志；其余不碰
4. **staging 纪律（主控教训）**：本任务产物由主控提交——施工代理只需保证磁盘状态；主控 commit 时按目录核对 .cs↔.meta 配对

## 完成判据

- CS0103 消除（Mono Player 编译过）；移动触觉行为零变化；全量绿

## 施工报告（施工代理，2026-11-14）

### 1. diff（全量，TouchHaptics.cs +7/−1）

```diff
--- a/UnityProject/Assets/ZFrame/View/Input/Touch/TouchHaptics.cs
+++ b/UnityProject/Assets/ZFrame/View/Input/Touch/TouchHaptics.cs
@@ -57,7 +57,14 @@ namespace ZFrame.View
                 UnityEngine.Debug.LogWarning($"[TouchHaptics] vibration unavailable: {e.GetType().Name}");
             }
 #else
-            Handheld.Vibrate(); // editor/desktop: documented no-op without vibration hardware
+#if UNITY_ANDROID || UNITY_IOS
+            Handheld.Vibrate(); // handheld domains (editor / iOS player; Android players
+                                // take the JNI branch above): documented no-op without
+                                // vibration hardware in the editor
+#else
+            /* Standalone/WebGL 等 player 目标无 Handheld 类型（A-AND-10 潜伏缺陷，
+               TASK-X-HAPTICS-01 最小 diff）：无振动硬件，no-op。 */
+#endif
 #endif
         }
```

与 B-PLANET-12 §6 参考 diff 的结构差异：实际代码里 Android player 由外层 `UNITY_ANDROID && !UNITY_EDITOR` 分支走 JNI（不落 `#else`），故内层门采用主控简报命名形 `#if UNITY_ANDROID || UNITY_IOS`（§6 参考写的 `UNITY_IOS` 是按其假设结构；实际 `#else` 域内 UNITY_ANDROID 只能在 UNITY_EDITOR 下成立，两形语义等价，此形多覆盖 Android 编辑器目标域）。门内 `Handheld.Vibrate()` 语句本身零改动，仅注释更新。

行为矩阵（零变化论证）：

| 域 | 修前 | 修后 |
|---|---|---|
| Android player | JNI 分支（VibrationEffect/createOneShot） | 逐字节未动（外层条件优先命中） |
| iOS player | `Handheld.Vibrate()` | 同一语句（UNITY_IOS 命中） |
| 编辑器（目标 Android/iOS） | `Handheld.Vibrate()`（编辑器文档化 no-op） | 照旧调用（UNITY_ANDROID/UNITY_IOS 随激活目标在编辑器域有定义） |
| 编辑器（桌面/WebGL 目标） | `Handheld.Vibrate()`（编辑器 no-op） | 调用消失——编辑器 no-op → 空体，运行时零差 |
| Windows Standalone/WebGL player | **CS0103 编译不过**（不可达） | 编译可达，静默 no-op（修复目标本身） |
| 测试面 | Pulse seam（mock 计数） | 不变——DefaultPulse 不在任何断言路径（PlayMode Case5 走 seam 与 Enabled 门） |

### 2. Mono Player 编译核验（全链，非仅编译检查）

- 命令：`Tools/build_player.ps1 -Backend Mono -TimeoutSec 1200`。脚本无"仅编译"模式 → 走完 **build+smoke+report 全链**。
- **RESULT: PASS (build + smoke + report)**，关键证据（TempLogs/build_player_unity.log）：
  - `:7383 [PlayerBuild] result=Succeeded totalSize=88153034 errors=0`
  - `grep -c "error CS"` = 0、`grep -c "CS0103"` = 0 —— **CS0103 消除**（B-PLANET-12 亲跑对照：result=Failed errors=2）
  - smoke：ZFrame.exe 存活 20s + Player.log 三条启动链 marker（Initialize engine version / Loaded All Assemblies / [GameBootstrap] Awake）+ taskkill，Player.log 1.3 KB
  - 产物 `Build/Windows64/ZFrame.exe`（Player 84.09 MB / 175 files），报告 `Build/build_report.txt`
- 构建管线写回（既有约定，PlayerBuild 唯一写路径，非手工编辑）：`Tools/version.json` build 33→34、versionCode 12→13；`ProjectSettings.asset` bundleVersion 0.1.0.34（AndroidBundleVersionCode 保持 12 不动——Windows 构建不写 Android 专属字段；scriptingBackend `Standalone: 0` 为 BuildWindows64Mono 显式 Mono 复位，B-PLANET-12 已复位项）。32→33 与 versionCode 10→12 属 B-PLANET-12 Android 轮的在档写回。

### 3. 测试四段

| 段 | 结果 | 备注 |
|---|---|---|
| EditMode | **1223/1223 failed=0** | 两跑皆 1223/0：13:25 一轮（unity exit=0 自退干净）；13:32 四段套件轮（evidence=本轮新写 results.xml，unity exit=1 为脚本预设 zombie taskkill 采证口径，非失败） |
| PlayMode | **114/114 failed=0**（复跑） | 首跑 113/114：`AssemblyConnectPlayModeTests.ConnectedChain_GroundSettled_10Seconds_NoJitterNoExplosion` 关节锚点最大误差 0.0500005335 vs 门限 <0.05，超差 **5.3e-7 m**——与 TASK-A-AI-03 §205、TASK-B-PLANET-11 §202 两份档案**同用例同失败值逐位一致**（既有"瞬时边界抖动、复跑归因"先例；当轮 machine quiet=BUSY/Taskmgr 31.3%）。原样复跑即 114/114。失败快照未留（复跑覆写 results.xml，教训：应先拷贝存证），失败明细逐字存档于本行；该用例文件零 diff、与本刀零因果（触觉无物理交互） |
| arch | **PASS，违规数 0**（27 asmdef 扫描） | TempLogs/arch_report.txt（13:34:58） |
| naming | **PASS，违规数 0** | TempLogs/name_report.txt（13:35:20） |

工具备注：`powershell -File` 调用 run_tests.ps1 时 `-Segments` 数组参数只绑到首元素（首轮只跑 editmode 即此因），改 `-Command` 解析式调用后四段齐全——仅调用方式记录，脚本未改。

### 4. 文件清单（白名单核对）

| 文件 | 变更 | 归属 |
|---|---|---|
| `UnityProject/Assets/ZFrame/View/Input/Touch/TouchHaptics.cs` | +7/−1 | 白名单（本刀） |
| `Docs/Logs/TASK-X-HAPTICS-01.md` | 本节追加 | 白名单（本日志） |
| `Tools/version.json` / `ProjectSettings/ProjectSettings.asset` | 构建管线写回 | §2 既有约定（主控 commit 按 B-PLANET-12 口径一并核对） |
| `TouchHaptics.cs.meta` | **零改动**（Unity 未触发变更，git status 干净） | — |
| `TempLogs/*`、`Build/*`、`TestReport.txt` | 证据/产物 | gitignore 口径照旧 |

零触碰：TASKS.md（看板）、调用者（TouchControlsView/SettingsRegistry/TouchControlSettingOptions）、Tests 全部、Native/、PlayerBuild.cs、build_player.ps1。无 git commit。

### 5. 规范自检

TouchHaptics.cs 首字节 `75 73 69`（"usi"，无 BOM）、CR 计数 0（LF）；本日志写入前首字节 `23 20`（"# "）、CR 0，写入后复验同口径。Unity 批处理单实例纪律全程遵守（tasklist 前检/锁文件 best-effort 删/测试无 -quit）；终态无 Unity 僵尸残留；零第三方依赖。

### 6. 遗留风险

1. IL2CPP Windows Player 构建仍因本机缺 IL2CPP 模块不可验（B-PLANET-12 M1 环境项）——本修复同样消除其编译阻断（内层 `#else` 在 IL2CPP Standalone 域同样不实例化 Handheld 引用），模块在位后 `build_player.ps1 -Backend Il2cpp` 一条命令补验。
2. `NoJitterNoExplosion` 物理用例在本机 BUSY 时有 5.3e-7 m 边界超差前科（三案同值 0.0500005335）——验收轮建议避开 Taskmgr 类负载，或沿用"复跑归因"既有口径。
3. 修复后编辑器桌面/WebGL 目标域 DefaultPulse 变空体（原 `Handheld.Vibrate()` 编辑器 no-op）——运行时零差；若未来有人在编辑器域给 DefaultPulse 挂探针需知此变化（当前无此用法）。
4. 本轮 PlayMode 失败快照未存档（复跑覆写）——失败签名已逐字入档且与两份历史档案同值，归因证据链自足，但严格"快照留证"口径上有此缺口。

### 7. 验收建议

四段全绿 + Mono Player errors=0 + smoke PASS + 移动平台行为矩阵零变化 + 白名单/编码/纪律自检齐备——**建议作为 PASS 候选**（由 B-PLANET-12 验收捎带或主控依本日志证据直接闭环皆可）。

## 验收结论（B-PLANET-12 验收代理捎带，主控委托，2026-11-14）

**判：PASS**（主控可据此单独 commit）。验收代理独立于施工代理，以下证据为验收代理亲跑/亲读：

1. **diff 核对**：`git diff TouchHaptics.cs` 亲读——外层 `UNITY_ANDROID && !UNITY_EDITOR` JNI 分支（Android player 触觉实现，:25-58）**逐字节未动**；改动全部落在外层 `#else` 域（:59-68）：内层 `#if UNITY_ANDROID || UNITY_IOS` 门内 `Handheld.Vibrate()` 语句零改动（仅注释更新），`#else` 域为空体注释（Standalone/WebGL player 无 Handheld 类型引用 → CS0103 消除）。实际 numstat **+8/−1**（§1/§4 头标 +7/−1 为小勘误，diff 块本身 8 行）。
2. **行为矩阵核对**：§1 六行矩阵与代码逐行相符——Android player=JNI 分支（外层条件优先命中，不变）；iOS player=UNITY_IOS 命中同一语句；编辑器 Android/iOS 目标=照旧调用（UNITY_ANDROID/IOS 在编辑器域随激活目标有定义）；编辑器桌面/WebGL 目标=空体（原编辑器 no-op，运行时零差）；Windows Standalone/WebGL player=编译可达静默 no-op（修复目标本身）；Pulse seam（:75-102）未触碰。内层门形与 B-PLANET-12 §6 参考形（UNITY_IOS）的语义等价论证（§1 末段）成立。
3. **构建证据亲读**（TempLogs/build_player_unity.log）：:7383 `[PlayerBuild] result=Succeeded totalSize=88153034 errors=0`；`grep -c CS0103` = **0**、`grep -c "error CS"` = **0**（亲跑计数）；:628 `[PlayerBuildMono] scripting=Mono2x restored`；Build/build_report.txt 亲读：Unity exit 0、Smoke PASS（20s 存活+Player.log 三 marker：Initialize engine/Loaded All Assemblies/[GameBootstrap] Awake）、wall 4:17（13:17:48 生成）——**CS0103 消除实锤**。
4. **测试数字互证**：验收代理本轮亲跑 EditMode **1223/1223 failed=0**（TempLogs/runtests_editmode.xml）+ PlayMode 复跑 **114/114 failed=0**（TempLogs/runtests_playmode.xml）+ arch/naming PASS 0 违规——与本施工报告 §3 四段数字互证成立（PlayMode 首跑 113/114 在验收轮以同签名 0.0500005335 复现一次、静机复跑绿，快照存 TempLogs/acceptance_planet12_playmode_fail_snapshot.xml，与本刀零因果，详见 B-PLANET-12 验收结论 T5）。
5. **白名单/规范**：TouchHaptics.cs +8/−1 与本日志为白名单内；TouchHaptics.cs.meta 零改动（git status 亲核）；调用者/Tests/Native/PlayerBuild.cs/build_player.ps1 零触碰；编码亲验无 BOM（首字节 `75 73 69`）/CR=0（LF）。
6. **形态备注**：本日志无独立「验收清单」节（微任务，清单由主控简报 §2 核验项 + B-PLANET-12 验收清单 H1 行承载）——主控闭环时可按需补一行或按现形接受；四判据（CS0103 消除/移动行为零变化/全量绿/白名单）全部成立。
