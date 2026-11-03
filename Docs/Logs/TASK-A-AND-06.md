# TASK-A-AND-06 —— IL2CPP 正式包重出（ARM64，可对外）

> 施工代理按本简报执行，第 2 节写报告。**禁止 git commit**。验收由另一代理执行。

## 任务简报（主控写，2026-11-03）

### 为什么做

真机现在装的是 **Mono 开发包**（`ZFrame_MonoDev.apk`，带 `DEVELOPMENT_BUILD` 宏、调试红字、DeviceFileLogger）。
那是为取证出的，不能对外。工程一直有 IL2CPP ARM64 的正式构建入口，但当前工作区 `ProjectSettings.asset` 停在
**Mono / ARMv7**（`AndroidTargetArchitectures: 1`、`scriptingBackend:` 空），`bundleVersion` 0.1.0.18。
本任务重出一版**正式包**。

### 关键事实（我已核实，别重复摸索）

- **不需要手改 `ProjectSettings.asset`**：`Game/Editor/PlayerBuild.cs:387-391` 的 `RunAndroid` 会自己设
  `PlayerSettings.SetScriptingBackend(Android, backend)` 和
  `targetArchitectures = backend == IL2CPP ? ARM64 : ARMv7`。
  ⚠️ 也就是说**构建会写 ProjectSettings.asset**——这是设计内行为，构建后 `git status` 里出现它是正常的，
  **不要 `git checkout` 还原它**（否则下次构建又得改一次）。在报告里确认它最终是 `IL2CPP + ARM64` 即可。
- Unity：`C:/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe`（版本 `2022.3.62f3c1`）
- adb：`C:/Users/1/Android/platform-tools/adb.exe`
- IL2CPP 模块已装：`AndroidPlayer/Variations/il2cpp/{Release,Development,Managed}`
- 构建入口：`Tools/build_android.ps1`（**默认就是 IL2CPP**，不要加 `-Backend Mono`）
- 版本单源：`Tools/version.json`（`{"version":"0.1.0","build":18}`）→ 构建时注入 `bundleVersion` 并**自增写回**，
  所以构建后 `version.json` 变 19 是正常产物，也不要还原。

### 任务

1. **前置检查**：确认没有 GUI Editor 在跑（单实例），删 `UnityProject/Temp/UnityLockfile`；
   检查 `adb devices`（**设备可能没连，这不阻塞构建**）。
2. **构建**：`powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_android.ps1`
   （首次 IL2CPP 可能 20–45 分钟，脚本自带 2700s 超时；**耐心等，不要中途 taskkill**）
3. **静态验证正式包**（不依赖设备，必须做）：
   - 架构：APK 内 `lib/` 下应只有 **arm64-v8a**（不应有 armeabi-v7a）
   - 无调试标记：`DEVELOPMENT_BUILD` 相关不该存在；包名/版本号应为 `0.1.0.19`
   - 文件大小与产物路径：`Build/Android/ZFrame.apk`
   - 可用 `aapt dump badging`（Android SDK build-tools）或 `unzip -l` 佐证；**给出你实际用的命令与输出片段**
4. **真机验证**（**设备在线才做**）：
   `adb install -r` → 启动 → 看到主菜单 → 点开始 → 进 SCN_Game 渲染正常 → **确认屏幕上没有调试红字/开发者水印**。
   截图或 logcat 佐证。**设备不在线就明确写"未做，设备未连接"，不许写成已验证。**
5. **归档**：把构建日志、静态验证输出归档到 `TempLogs/and06/`。

### 判据

- APK 存在且 `lib/` 只有 arm64-v8a；
- `DEVELOPMENT_BUILD` 不存在（可用 `strings`/`grep` 或 Unity 的 `BuildOptions` 佐证）；
- 版本号 `0.1.0.19`；
- 设备在线时：安装、启动、进入游戏、**无调试红字**。

### 环境纪律（真实踩过）

- Unity 批处理**单实例**：构建前查进程 + 删 `UnityProject/Temp/UnityLockfile`
- **Unity 必须经 Git Bash 启动**（直接 PS 启动会卡在 licensing handshake，见脚本注释与 HANDOFF）
- 构建**勿加 `-quit`**（构建方法自己调 `EditorApplication.Exit`）
- 构建前 `unset http_proxy https_proxy all_proxy`；**Bash 侧禁止转调 powershell** → 用 **PowerShell 工具**
- 本机 GUI Editor 加 `-force-d3d12` **会挂起**
- 清理用 `mv` 归档到 `TempLogs/archive_pre_audit/`，不要 `rm -rf`

### 铁律

- **禁止 git commit**；不改 `TASKS.md`/`HANDOFF.md`/`WORKLOG.md`/`README.md`
- 不改 `Tools/build_android.ps1`、不改 `PlayerBuild.cs`（除非构建失败且有明确根因，且要在报告单独说明）
- 不动 Governor 三件套、Unity 安装目录；`.cs` 无 BOM、LF
- **构建成功后不要还原 `ProjectSettings.asset` / `version.json`**（见上文"关键事实"）

---

## 施工报告（施工代理写）

**结论：构建成功。静态验证全部通过。真机验证未做（设备未连接）。**

### 2.1 前置检查

| 项 | 结果 |
|---|---|
| GUI/批处理 Unity 进程 | `Get-Process Unity` → 0 个（构建前/构建后各查一次） |
| `UnityProject/Temp/UnityLockfile` | 构建前不存在（脚本内 `Remove-UnityLockBestEffort` 仍会兜底） |
| `adb devices` | `List of devices attached` 后为空 —— **无设备**（不阻塞构建） |
| 代理变量 | 存在 `HTTP_PROXY/HTTPS_PROXY/https_proxy/http_proxy` 四份（大小写重复，会让 `Start-Process` 崩），已在启动构建的子 shell 内全部清除 |
| 工作区 | 构建前 `git status` 干净，HEAD=`550a6cc` |

### 2.2 构建

```
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File Tools/build_android.ps1
```

未加 `-Backend`（默认 IL2CPP），未加 `-quit`，Unity 由脚本内经 Git Bash 拉起（PID 7200）。

| 项 | 值 |
|---|---|
| 耗时 | **29 分 16 秒**（1756 s，脚本 2700 s 上限内） |
| Unity 退出码 | **0** |
| 脚本退出码 | **0**（`RESULT: PASS (Android build)`） |
| 构建方法 | `Game.EditorTools.PlayerBuild.BuildAndroid64` → `RunAndroid(..., IL2CPP, "PlayerBuild")`，`options=None` |

Unity 日志关键行：

```
329:[PlayerBuild] version: bundleVersion=0.1.0.19 (version.json build 18 -> 19)
1648:[PlayerBuild] scripting=IL2CPP arch=ARM64 graphics=Vulkan,GLES3
1688:[PlayerBuild] building -> .../Build/Android/ZFrame.apk (options=None)
8212:Build Finished, Result: Success.
8226:[PlayerBuild] result=Succeeded ... errors=0
8236:[PlayerBuild] SUCCESS
```

耗时主要花在：URP 着色器变体编译（12288 个变体/Particles Unlit pass，约 10 分钟）+ IL2CPP C++ 编译（1418 个 .o）+ Gradle。

### 2.3 静态验证（命令 + 实际输出）

**架构 —— `lib/` 只有 arm64-v8a：**
```
$ unzip -l Build/Android/ZFrame.apk | grep "lib/"
    62896  1981-01-01 01:01   lib/arm64-v8a/lib_burst_generated.so
 54813672  1981-01-01 01:01   lib/arm64-v8a/libil2cpp.so
     6736  1981-01-01 01:01   lib/arm64-v8a/libmain.so
 18363456  1981-01-01 01:01   lib/arm64-v8a/libunity.so

$ unzip -l Build/Android/ZFrame.apk | grep -oE "lib/[^/]+/" | sort -u
lib/arm64-v8a/
```
无 `armeabi-v7a`、无 `x86`/`x86_64`。

ELF 头佐证（`e_machine=0xb7`=AArch64，非 `0x28`=ARM32）：
```
$ unzip -p Build/Android/ZFrame.apk lib/arm64-v8a/libunity.so | head -c 20 | od -An -tx1
 7f 45 4c 46 02 01 01 00 00 00 00 00 00 00 00 00
 03 00 b7 00
```

**版本号 —— `0.1.0.19`：**
```
$ /c/Users/1/Android/build-tools/34.0.0/aapt.exe dump badging Build/Android/ZFrame.apk | head -1
package: name='com.ZFrame.ZFrame' versionCode='1' versionName='0.1.0.19' ...
```

**无 DEVELOPMENT_BUILD（三重佐证）：**

1. 构建选项来自源码，正式入口不传 `BuildOptions.Development`（`PlayerBuild.cs:121` 用 `RunAndroid` 默认 `extraOptions = BuildOptions.None`；日志第 1688 行 `options=None` 印证）。
2. `libil2cpp.so` 字符串扫描为 0：
```
$ unzip -p Build/Android/ZFrame.apk lib/arm64-v8a/libil2cpp.so > /tmp/so && grep -a -c "DEVELOPMENT_BUILD" /tmp/so
0
```
3. Manifest 无 `application-debuggable`（对照组 MonoDev 有）：
```
$ aapt dump badging Build/Android/ZFrame.apk | grep -i debuggable
（无输出）

$ aapt dump badging Build/Android/ZFrame_MonoDev.apk | grep -i debuggable
application-debuggable
```

> 备注（避免验收误判）：`global-metadata.dat` 里能搜到 `zframe_boot.log` 字符串，这是**常量声明** `DeviceFileLogger.FileName`（`DeviceFileLogger.cs:48`，在 `#if` 之外）。真正写文件的实现全部在 `#if DEVELOPMENT_BUILD || UNITY_EDITOR` 内（`:50` 起），正式包里是空方法体，不落盘、不上屏。

### 2.4 产物

| 项 | 值 |
|---|---|
| 路径 | `Build/Android/ZFrame.apk` |
| 大小 | **38,080,349 字节（36.32 MB）** |
| 构建时间 | 2026-11-03 14:55:36（覆盖了 11-01 15:47 的旧包） |

### 2.5 真机验证 —— **未做，设备未连接**

`adb devices`（构建前后各一次）均只输出 `List of devices attached`，无任何设备行，因此
安装 / 启动 / 主菜单 / SCN_Game 渲染 / "无调试红字" 这几项**均未执行、未验证**。
此包的真机行为需在设备接入后由验收代理补测。

### 2.6 ProjectSettings 与 version.json 最终状态（按简报保留，未还原）

```
UnityProject/ProjectSettings/ProjectSettings.asset
  144:  bundleVersion: 0.1.0.19
  265:  AndroidTargetArchitectures: 2        # 2 = ARM64
  670:  scriptingBackend:
  671:    Android: 1                          # 1 = IL2CPP

Tools/version.json  →  { "version": "0.1.0", "build": 19 }
```

`git status --porcelain` 恰为这两条，无其他改动：
```
 M Tools/version.json
 M UnityProject/ProjectSettings/ProjectSettings.asset
```
即：**未改** `Tools/build_android.ps1`、**未改** `Game/Editor/PlayerBuild.cs`、未 commit、未碰看板文件。

### 2.7 现场与异常

- 构建后 `Get-Process Unity` = 0，`UnityProject/Temp/UnityLockfile` 不存在 → 无残留。
- 未出现超时、未 taskkill，构建自然结束。
- **需要主控知悉（不在本任务范围）**：APK 用 **Android Debug 证书**签名
  （`apksigner verify --print-certs` → `CN=Android Debug`，SHA-256 `d2664dca…1143`）。
  "对外"若要上架/分发，需另派任务配置 release keystore 与 PlayerSettings 签名段。
- 构建期两条无害告警：`Active Input Handling is set to Both`（Android 上不推荐）、
  `This version only understands SDK XML versions up to 3 ... version 4`（build-tools 34 与 Unity 2022.3 的已知组合）。

### 2.8 归档

`TempLogs/and06/`：`build_console.log`（脚本控制台）、`build_android_il2cpp_unity.log`（Unity 全量日志副本）、`static_verify.txt`（上述全部静态验证命令与输出）。

---

## 验收清单（供验收代理）

- [ ] V1 `Build/Android/ZFrame.apk` 存在，修改时间是本轮
- [ ] V2 `lib/` 只有 **arm64-v8a**，无 `armeabi-v7a`
- [ ] V3 无 `DEVELOPMENT_BUILD`；`bundleVersion = 0.1.0.19`
- [ ] V4 `ProjectSettings.asset` 最终为 IL2CPP + ARM64（**且没被还原掉**）；`version.json` build=19
- [ ] V5 设备在线则真机实证（安装/启动/进游戏/**无调试红字**）；不在线则如实记"未做"
- [ ] V6 未改构建脚本与 PlayerBuild.cs；未 commit；无 Unity 残留进程/锁

## 验收结论（验收代理写）

> 验收代理：独立取证，**未采信第 2 节自述输出**。以下每条均为本轮亲自复跑命令所得（验收时间 2026-11-03 14:58，构建时间 14:55:36）。

### 逐条判定

| 项 | 判定 | 证据（本轮实际命令输出） |
|---|---|---|
| **V1** APK 存在/本轮/大小合理 | **PASS** | `stat Build/Android/ZFrame.apk` → `size=38080349 mtime=2026-11-03 14:55:36.786 +0800`（36.32 MB；验收时刻 14:58，同一轮） |
| **V2** 只有 arm64-v8a | **PASS** | `unzip -l … \| grep lib/` 仅 4 条，全部 `lib/arm64-v8a/`（lib_burst_generated / libil2cpp / libmain / libunity）；`grep -oE "lib/[^/]+/"\|sort -u` → 单行 `lib/arm64-v8a/`；`grep -E "armeabi\|x86"` → 无匹配（exit=1）；ELF `e_machine` = `03 00 b7 00`（0xb7=AArch64）；`aapt` → `native-code: 'arm64-v8a'` |
| **V3** 无调试标记 + 版本号 | **PASS** | `unzip -p … libil2cpp.so \| grep -a -c DEVELOPMENT_BUILD` → **0**；`aapt dump badging` 首行 → `versionName='0.1.0.19'`；`grep -i debuggable` → 无输出（exit=1），对照旧 `ZFrame_MonoDev.apk` 有 `application-debuggable` |
| **V4** 配置落地（IL2CPP+ARM64，未还原） | **PASS** | `ProjectSettings.asset:265 AndroidTargetArchitectures: 2`、`:670-671 scriptingBackend: Android: 1`、`:144 bundleVersion: 0.1.0.19`；`Tools/version.json` → `{"version":"0.1.0","build":19}`。属简报明示的设计内写入，非脏改动 |
| **V5** 真机实证 | **PASS with 受限** | `adb devices -l` → 仅 `List of devices attached`，在线设备数 **0**（14:58 二次复测仍为 0）。安装/启动/主菜单/SCN_Game 渲染/**屏上无调试红字** —— **全部未做，设备未连接**。按简报不判 FAIL，计入受限项 |
| **V6** 未改脚本/未 commit/无残留 | **PASS** | `git diff --stat -- Tools/build_android.ps1 Game/Editor/PlayerBuild.cs` → 空（无改动）；`git log --oneline -1` → `550a6cc`（= 简报派发 commit，**无新 commit**）；`git status --porcelain` 仅 3 项：`Docs/Logs/TASK-A-AND-06.md`（本日志自身）、`Tools/version.json`、`UnityProject/ProjectSettings/ProjectSettings.asset`（后两项为设计内写入）；`Get-Process Unity` → 0 个；`UnityProject/Temp/UnityLockfile` → 不存在 |

补充（V3 佐证链完整性）：`lib/` 内无 `libmonobdwgc-2.0.so`，且存在 `assets/bin/Data/Managed/Metadata/global-metadata.dat`（9,637,484 B）与 54.8 MB 的 `libil2cpp.so` —— 与 IL2CPP 产物特征一致，非 Mono 包。`unzip -t` → `No errors detected`（APK 完整性 OK）。

### 关于 `versionCode='1'`（额外核查，只结论不修复）

**事实成立**，且比施工所述更广：

```
aapt dump badging Build/Android/ZFrame.apk     → versionCode='1' versionName='0.1.0.19'
aapt dump badging Build/Android/ZFrame_MonoDev.apk → versionCode='1' versionName='0.1.0.22'
ProjectSettings.asset:172  AndroidBundleVersionCode: 1
```

根因定位（静态配置，非本任务引入）：`version.json` 只驱动 `bundleVersion`（versionName 字符串），**`AndroidBundleVersionCode` 恒为字面量 1**，没有任何自增逻辑；因此历次包的 versionCode 全是 1。

**对内部 `adb install -r` 覆盖安装的影响：无阻塞。**
- Android 的覆盖安装校验只看 versionCode 单调性，本包与已装包同为 `1`（相等 → 正常 UPDATE，无需 `-d`）；版本名不参与任何安装校验。
- 前提条件本轮已核：v2 签名块存在（`grep -a -c "APK Sig Block 42"` → 1），`targetSdkVersion:'34'`、`minSdk 22`，`native-code: arm64-v8a` —— 设备为 arm64 且 API≥22 即可装。
- 唯一真实风险是**签名不一致**：本包用 `CN=Android Debug` 调试证书（`META-INF/CERT.RSA` → `subject=CN=Android Debug, O=Android, C=US`）。同机调试密钥一致可覆盖；换机/换证书装同包名会 `INSTALL_FAILED_UPDATE_INCOMPATIBLE`。

**对外发布：构成问题（两个独立阻塞项）。**
1. versionCode 恒为 1 → 应用商店要求每次发布 versionCode **严格递增**，第二次上传同 versionCode 会被直接拒绝；亦无法做灰度/回滚/按包区分崩溃归因。
2. 调试证书签名（`CN=Android Debug`）→ 不能上架、不能对外分发，必须另配 release keystore。

另附一条观察（供主控知悉，非本任务判据）：本包 versionName `0.1.0.19` **低于**设备侧旧 MonoDev 包的 `0.1.0.22`，即版本号在字符串维度上回退了（`version.json` 被重置为 build 18 后构建自增到 19）。安装层面无影响（不看 versionName），但对外/追踪维度会造成"版本倒挂"。

### 最终裁决

**PASS with 受限（有条件通过）。**

- 交付物质量：IL2CPP + ARM64 + 无 `DEVELOPMENT_BUILD` + 无 debuggable + 版本名 0.1.0.19，**静态层面全部达标**；V1–V4、V6 五项独立取证通过。
- **受限项（必须挂账，不得视为已验证）**：V5 真机 —— 设备未连接，安装/启动/主菜单/SCN_Game 渲染/**屏上无调试红字**全部未执行。静态证据只能证明"包内不含 `DEVELOPMENT_BUILD` 且非 debuggable"，**不能替代真机屏幕观察**；"无调试红字"在本轮属未验证。
- 收口条件：设备接入后补测 V5（`adb install -r` → 启动 → 进 SCN_Game → 截图 + logcat 留证），补测通过即可转正式 PASS。
- 已知遗留（超出本任务范围，另派）：release keystore 与 `AndroidBundleVersionCode` 自增策略；两者不解决则"可对外"这一表述不成立。

