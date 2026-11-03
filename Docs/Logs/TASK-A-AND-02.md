# TASK-A-AND-02 —— 外部 Android 工具链装配 + 首个 APK（绕过 Hub 子模块）

状态: doing | 派发: 2026-10-31 | 施工: general-purpose 施工子代理 | 验收: general-purpose 验收子代理 | commit: （待填）

## 任务简报（主控写）

### 背景

AndroidPlayer 构建程序已装（2.1GB，PlayMode 引擎与 Gradle 在位），但 **SDK/NDK/OpenJDK 子模块缺失**（Hub 子模块安装器在团结生态下拿不到，unity.cn 页面也不列子项）。路线：**外部装配官方工具链到用户目录（免管理员）+ Unity External Tools 指过去**。已实测可达：download.java.net（OpenJDK17 zip，200）、dl.google.com 主机（404 只是版本号猜错，官方 repository XML 有真链）。

### 目标（按序，做到 APK 即闭环；真机部署若手机未连则列遗留）

1. **工具链装配**（全部用户目录 `C:/Users/1/Android/`，免管理员）：
   - OpenJDK 17：download.java.net 17.0.2 zip（186MB，已验 200）→ 解压 `jdk/`
   - cmdline-tools：dl.google.com/android/repository/repository2-3.xml 里找 `commandlinetools_win-*_latest.zip` 真链 → 解压 `cmdline-tools/latest/`
   - sdkmanager（用我们的 JDK17）接受许可后安装：`platform-tools`、`platforms;android-34`、`build-tools;34.0.0`、`ndk;23.1.7779620`（Unity 2022.3 默认 NDK 版本，如 Gradle 构建时要求不同版本按需调整并报告）
2. **PlayerBuild 扩展**（`Game/Editor/PlayerBuild.cs`）：`BuildAndroid64()` 在 SwitchActiveBuildTarget 前写 EditorPrefs：`AndroidSdkRoot`/`AndroidNdkRoot`/`JdkPath`（**先查 Unity 2022.3 实际键名**——源码/文档核实，报告列出）+ IL2CPP+ARM64+Graphics API 顺序（Vulkan,GLES3）
3. **构建**：batch 构建 `Build/Android/ZFrame.apk`（首构建 20-40 分钟正常，超时 45 分钟）
4. **冒烟**：我们的 platform-tools/adb.exe `devices`（有设备→install+logcat 三关键词+screencap；无设备→APK 产出+`adb devices` 输出留证，真机部署列遗留）
5. run_tests.ps1 无需新段（构建按需手动跑）；.gitignore 加 `Build/Android/`、`C:/Users/1/Android` 不在仓内无需处理

### 已知坑（报告逐条覆盖）

- sdkmanager 首跑要 Java——用我们解压的 JDK17 显式 `--sdk_root`
- 许可证要 `--licenses` 全 y
- Gradle 首跑下载依赖（dl.google.com 可达已证；如被墙走腾讯镜像 maven，报告注明）
- IL2CPP 构建比 Mono 慢数倍——首包可先 Mono 快速验证管线，再 IL2CPP 正式包（两次都留报告）

### 约束

- 白名单：`Tools/android/`（工具链装配目录可放仓外 `C:/Users/1/Android`，推荐后者）、`UnityProject/Assets/Game/Editor/PlayerBuild.cs`、`Build/Android/`、本日志；.gitignore 补 `Build/Android/`
- Unity 批处理纪律；不 git commit；UTF-8 无 BOM/LF

## 施工报告（施工代理写）

**结论先行**：APK 两个都已产出——Mono 验证包 `Build/Android/ZFrame_MonoVerify.apk`（33,936,840 B / 32.36 MB，墙钟 7:26）与 IL2CPP ARM64 正式包 `Build/Android/ZFrame.apk`（37,956,641 B / 36.20 MB，墙钟 10:16），Unity exit 0 / `result=Succeeded errors=0`。Windows 构建路径零回归（build_player.ps1 全链抽查 PASS）。无 Android 设备连接，真机部署列遗留。

### 1. 工具链装配清单（全部仓外 `C:/Users/1/Android/`，免管理员）

| 组件 | 实装版本 | 位置 | 下载来源（全 https，实测 200） | 体积 |
|---|---|---|---|---|
| JDK | **OpenJDK 11.0.32.1**（Microsoft LTS） | `C:/Users/1/Android/jdk11` | `aka.ms/download-jdk/microsoft-jdk-11-windows-x64.zip` → download.visualstudio.microsoft.com | 191,250,397 B |
| JDK（已退役） | OpenJDK 17.0.2（Oracle GA） | `_downloads/jdk-17…zip` + `jdk/`（保留但不再被 Unity 使用） | `download.java.net/java/GA/jdk17.0.2/…/openjdk-17.0.2_windows-x64_bin.zip` | 186,216,309 B |
| cmdline-tools | **19.0**（build 13114758，经典 sdkmanager） | `C:/Users/1/Android/cmdline-tools/19.0/` | `dl.google.com/android/repository/commandlinetools-win-13114758_latest.zip`（真链取自 repository2-3.xml） | 143,040,480 B |
| cmdline-tools（已退役） | 23.0（build 16111833，新版 "Android CLI" 重写版） | `_downloads/cmdline-tools-23.0-retired/` | 同上（16111833 zip） | 154,957,218 B |
| platform-tools | **r37.0.1**（adb 1.0.41） | `C:/Users/1/Android/platform-tools/` | sdkmanager 装包 | — |
| platform | **android-34**（ext7 r03） | `C:/Users/1/Android/platforms/android-34/` | sdkmanager 装包 | — |
| build-tools | **34.0.0** | `C:/Users/1/Android/build-tools/34.0.0/` | sdkmanager 装包 | — |
| NDK | **23.1.7779620（r23b）** | `C:/Users/1/Android/ndk/23.1.7779620/` | sdkmanager 装包 | — |
| licenses | 全部接受 | `C:/Users/1/Android/licenses/` | 经典 sdkmanager `--licenses` + `yes` 管道，末行 "All SDK package licenses accepted" | — |

下载全部经 `curl` https 直连；下载物集中在 `C:/Users/1/Android/_downloads/`（独立目录，未在其中运行任何解释器）。

### 2. EditorPrefs 键名核实清单（简报点名先查的项，V4）

简报给的三个键名 **两个是错的**（旧文档键名）。核实走两道独立证据：①安装内 `PlaybackEngines/AndroidPlayer/UnityEditor.Android.Extensions.dll` 的 UTF-16 字符串字面量扫描；②**运行期反射**（批处理执行 `DumpAndroidToolchainState`）读 `AndroidNDKRoot`/`AndroidJavaRoot`/`AndroidGradleRoot` 类的 `DirectoryPreferenceKey` 属性 + 注册表 `HKCU\Software\Unity Technologies\Unity Editor 5.x` 落盘验证。

| 组件 | 真实键名（Unity 2022.3.62f3c1） | 简报原文 | 证据 |
|---|---|---|---|
| SDK | `AndroidSdkRoot` | ✓ 一致 | dll 字符串 + getter 实测读到写入值 |
| NDK | **`AndroidNdkRootR23B`** | ✗ 简报写 `AndroidNdkRoot` | 反射 `AndroidNDKRoot.DirectoryPreferenceKey` 返回该串；写老键时 getter `ndkRootPath` 返回空串（实测） |
| JDK | **`Jdk11Path`** | ✗ 简报写 `JdkPath` | dll 内 `JdkPath` 零命中、`Jdk11Path` 命中；反射 `AndroidJavaRoot.DirectoryPreferenceKey` 同证 |
| Gradle | `GradlePath`（embedded，`GradleUseEmbedded=true`——gradle 随 AndroidPlayer 自带） | — | 反射 `AndroidGradleRoot.DirectoryPreferenceKey` |
| 开关 | `SdkUseEmbedded` / `NdkUseEmbedded` / `JdkUseEmbedded` / `GradleUseEmbedded`（bool，全部显式 false，仅 Gradle=true） | — | 反射 + 注册表 REG_DWORD |

### 3. 版本要求记录（简报"如 Gradle 构建时要求不同版本按需调整"条款的实测结论）

| 项 | Unity 2022.3.62f3c1 的要求 | 实测证据 |
|---|---|---|
| JDK | **major==11 硬校验**（反射 `AndroidJavaRoot.RequiredVersion=11.0.14.1`；错误文案 "Android development requires JDK 11 64-bit"） | JDK 17.0.2 首跑被拒：`UnityException: JDK not found`；社区同款报错 "Incompatible Java version '17'"。**简报推荐的 17.0.2 在本引擎上不可用**，改装 11 |
| JDK 补丁版 | 无下限要求，但 **11.0.2（2019-01）的 TLS 1.3 实现有 PSK resume bug** | Gradle 拉 Maven 依赖报 `peer not authenticated` / `No PSK available`；curl 同 URL 200（网络无墙，是 JVM TLS bug）；jshell 单连接不复现（Gradle/Apache HttpClient 复用 TLS session 才触发）。**升级 Microsoft OpenJDK 11.0.32.1 根治**，未启用镜像 |
| NDK | **精确 r23b**（反射 `AndroidNDKRoot.k_PackageRevision=23.1.7779620`） | 装对后 "Detect Android NDK" 2.3ms 通过 |
| cmdline-tools | ≥6.0（Unity 错误文案 "Command-line Tools (6.0)"） | sdkmanager 把我们的 19.0 目录识别为 `cmdline-tools;6.0` 通过 |
| Gradle/AGP | 模板自带 gradle（embedded）+ AGP **7.4.2** | 构建日志 "com.android.application.gradle.plugin:7.4.2" |

**cmdline-tools 23.0（16111833）退役原因（新坑，后续任务别踩）**：新版 "Android CLI" 把 `platforms;android-34` 按 `;` 拆成两个包名（`Package platforms not found`，GitBash/PowerShell/引号包裹均同），且其 `--version` 输出与经典版不同。19.0 经典版无此问题。另注意 `latest` 目录名 Unity 不认——必须 `cmdline-tools/<版本号>/` 布局（`platform-tools version 0.0` 与 `NDK not found` 都是 sdkmanager 起不来的连锁反应，修好布局两个错误同时消失）。

### 4. 构建记录（两次都留证）

| 包 | 后端 | 架构 | 体积 | 墙钟 | versionName |
|---|---|---|---|---|---|
| `Build/Android/ZFrame_MonoVerify.apk` | Mono2x（管线验证） | armeabi-v7a（Android 的 Mono 不支持 ARM64） | 33,936,840 B (32.36 MB) | 7:26 | 0.1.0.11 |
| `Build/Android/ZFrame.apk` | **IL2CPP（正式）** | **arm64-v8a** | 37,956,641 B (36.20 MB) | 10:16 | 0.1.0.12 |

两包 aapt2 证据：`package: name='com.ZFrame.ZFrame'`、`compileSdkVersion='34'`、`targetSdkVersion:'34'`、`uses-feature-not-required: android.hardware.vulkan.version`（Vulkan 已入 Graphics API 顺序，GLES3 兜底）。`ProjectSettings.asset` 落盘 `m_BuildTargetGraphicsAPIs: AndroidPlayer → [Vulkan, GLES3]`（序列化字节 `150000000b000000` = 枚举 21,11）与 `scriptingBackend: Android: 1`（IL2CPP）。版本单源链路正常：`Tools/version.json` build 6→13（Android 排障五轮占 7–12，Windows 抽查占 13），包体 versionName 与写回后 json 一致。

### 5. 已知坑四条逐条覆盖

1. **sdkmanager 首跑要 Java** ✓：`JAVA_HOME` 显式指解压 JDK + `--sdk_root=C:\Users\1\Android`，未依赖系统 java（本机 PATH 无 java）。
2. **许可证要 --licenses 全 y** ✓：经典版 `yes | sdkmanager --licenses` 末行 "All SDK package licenses accepted"；注意新版 23.0 CLI 的 `--licenses` 直接报 "no longer needed"（自动接受，行为不同）。
3. **Gradle 首跑下载依赖** ✓（比简报预估的更曲折）：dl.google.com 与 repo.maven.apache.org 直连均通、**未走腾讯镜像**（连通性预检 200 留证备用）——真正的坑是 JDK 11.0.2 的 TLS 1.3 session-resume bug（见 §3），升级 JDK 补丁版后依赖下载顺畅，全构建仅 7~10 分钟。
4. **IL2CPP 比 Mono 慢** ✓：Mono 7:26 先行验证管线（工具链/Gradle/签名链全通），IL2CPP 10:16 正式包（增量主要在 il2cpp 原生编译 + 1408 Bee 任务）。首跑 IL2CPP 全量在 45 分钟超时内完成，实际远低于预估。

### 6. 冒烟与遗留

- **adb 冒烟**（我们自己的 platform-tools）：`adb version` → 1.0.41 / 37.0.1-15733141 正常；`adb devices -l` → daemon 启动成功，输出 `List of devices attached`（空列表）。**无设备连接 → 真机部署（install + logcat 三关键词 + screencap）列遗留**，方案在 TASK-A-AND-01 简报（logcat 三关键词），手机插上 USB 调试后可直接用本任务产出的 `ZFrame.apk`。
- **Windows 路径零回归**（V4）：`Tools/build_player.ps1` 全链抽查 PASS——BuildWindows64 exit 0、bundleVersion=0.1.0.13 三通道校验过、20s 冒烟三关键词全中、149.04 MB Top-20 报告落盘。`Run()` 对 `Build/Windows64/ZFrame.exe` 的路径与场景列表逻辑逐行等价（版本注入抽为 `InjectBundleVersion()` 共用，行为不变）。
- **.gitignore**：简报要求"加 Build/Android/"——实测**无需改动**：第 53 行 `/Build/`（TASK-B-R9-15 所加）已覆盖，`git check-ignore -v Build/Android/ZFrame.apk` 实证命中；`*.apk`（第 19 行）双保险。零冗余改动。
- **Unity 纪律**：五轮构建全部走 `Tools/build_android.ps1`（build_player.ps1 同款五条纪律：单实例/`[IO.File]::Delete` 清锁/Git Bash 启动/无 `-quit`/`UNITY_EXIT_CODE` 轮询解析 + 硬超时 taskkill 兜底），无僵尸 Unity。

### 7. 交付物

- `UnityProject/Assets/Game/Editor/PlayerBuild.cs`：新增批处理入口 `BuildAndroid64()`（IL2CPP/ARM64/Vulkan,GLES3/外链工具链）与 `BuildAndroid64Mono()`（Mono 快速验证）；共用 `InjectBundleVersion()`（自 Windows 路径抽出，行为逐行等价）、`ConfigureExternalAndroidTools()`（SwitchActiveBuildTarget **之前**写 EditorPrefs）、`RunAndroid()`；保留 `DumpAndroidToolchainState()` 诊断入口（反射 dump 工具链状态，排障功臣，验收可复用）。UTF-8 无 BOM / LF。
- `Tools/build_android.ps1`（新）：Android 一键构建（`-Backend Mono|IL2CPP`，默认 IL2CPP，`-TimeoutSec` 默认 2700=45 分钟），纪律段与 build_player.ps1 同源。ASCII/CRLF。
- `Build/Android/ZFrame.apk` + `ZFrame_MonoVerify.apk`（gitignore 已覆盖，不入库）。
- `Tools/version.json`、`UnityProject/ProjectSettings/ProjectSettings.asset`：由构建流程按既有白名单路径写入（bundleVersion 0.1.0.13；Android 图标模板行、GraphicsAPIs、scriptingBackend Android:1 为切 target 的必要序列化产物）。
- 证据日志：`TempLogs/build_android_mono_unity.log`（Mono PASS）、`TempLogs/build_android_il2cpp_unity.log`（IL2CPP PASS）、`TempLogs/android_diag.out`（键名反射 dump）、`TempLogs/build_player_unity.log`（Windows 抽查）——TempLogs 不入库，验收如需重取证可跑同款命令。

### 8. 遗留（不阻塞闭环）

1. 真机部署未验（无设备）：`adb devices` 空列表；三关键词 logcat 冒烟方案见 TASK-A-AND-01。
2. `C:/Users/1/Android/jdk/`（17.0.2）与 `_downloads/` 里的退役包共约 800MB——保留原因：17 是简报原始指定物、23.0 CLI 是新坑的实证样本；验收后可让用户清理。
3. EditorPrefs 写入是用户级持久态（注册表），下次打开 Unity GUI 的 External Tools 面板会显示外部路径且各 "Installed with Unity" 复选框未勾——这是本方案预期行为，不是漂移。

## 验收清单（主控写）

- [ ] V1 工具链齐：adb/platform-tools/build-tools/NDK/JDK 路径核对（版本号打印）
- [ ] V2 APK 产出 exit 0（Mono 或 IL2CPP 如实记录）+ 体积
- [ ] V3 冒烟：有设备则三关键词+screencap；无设备则 adb devices 留证+遗留
- [ ] V4 EditorPrefs 键名核实清单（报告）；旧 Windows 构建路径零回归（build_player 抽查可选）
- [ ] V5 文件规范+施工报告四节完整

## 验收结论（验收代理写）

**总判定：PASS**（验收执行 2026-11-01；方式=独立重跑+第一手取证，未复用施工代理结论；验收过程未改代码、未 git commit）

- **V1 工具链 PASS**：`C:/Users/1/Android/platform-tools/adb.exe version` → "Android Debug Bridge version 1.0.41 / Version 37.0.1-15733141"；`build-tools/34.0.0/`（source.properties `Pkg.Revision=34.0.0`，aapt2.exe 在位）、`platforms/android-34/`（android.jar 在位）、`ndk/23.1.7779620/`（source.properties `Pkg.Revision=23.1.7779620`）、`cmdline-tools/19.0/`、`licenses/` 全部实存；`jdk11/bin/java.exe -version` → "openjdk version 11.0.32.1 2026-08-18 LTS（Microsoft）"。
- **V2 APK 双包 PASS**：`Build/Android/ZFrame.apk` 37,956,641 B、`ZFrame_MonoVerify.apk` 33,936,840 B，与报告字节级一致；aapt2 dump badging 实测两包均 `package: name='com.ZFrame.ZFrame'`、`targetSdkVersion:'34'`；ZFrame.apk `native-code: 'arm64-v8a'` 且包内实含 `lib/arm64-v8a/libil2cpp.so`（54.7MB，unzip -l 实证）、versionName 0.1.0.12；Mono 包 `native-code: 'armeabi-v7a'`（libmonobdwgc-2.0.so 在位）、versionName 0.1.0.11——双包身份（IL2CPP/ARM64 与 Mono/ARMv7）实锤。
- **V3 PlayerBuild.cs PASS**：L77-79 键名 `AndroidSdkRoot`/`AndroidNdkRootR23B`/`Jdk11Path` 与简报纠错一致（L80-83 四个 UseEmbedded 开关齐）；`ConfigureExternalAndroidTools()`（L201-216）经 `RunAndroid` L351 在 `SwitchActiveBuildTarget`（L354）**之前**写入；`BuildAndroid64` IL2CPP（L120→L361）+ARM64（L363-365）+Graphics API 显式 Vulkan→GLES3 且 `SetUseDefaultGraphicsAPIs(false)`（L366-371）；`DumpAndroidToolchainState()` 诊断入口在 L223-338。旁证：TempLogs/android_diag.out 反射 dump 实录 `DirectoryPreferenceKey = AndroidNdkRootR23B / Jdk11Path / AndroidSdkRoot / GradlePath`。
- **V4 Windows 零回归 PASS**：验收独立重跑 `run_tests.ps1 -Segments native` → **exit 0**（281 cases / 771162 asserts / 0 failed，ctest 100%）；`-Segments editmode` → **exit 0**（total=785 passed=785 failed=0 skipped=0，Unity exit 0，results.xml 留证）；另核 TempLogs/build_player_unity.log 第一手 `[PlayerBuild] result=Succeeded totalSize=84354200 errors=0 output=…/Build/Windows64/ZFrame.exe`（施工报告的 Windows 全链抽查属实）。
- **V5 文件规范+报告完整性 PASS**：PlayerBuild.cs UTF-8 无 BOM/LF（首三字节 `2f 2f 20`、0 个 CR）；build_android.ps1 ASCII/CRLF（.ps1 例外合规）；`git check-ignore -v` 实证 .gitignore L53 `/Build/` 命中两 APK（报告"无需改 .gitignore"属实）；`git status` 改动面=白名单内 4 文件 + 新增 Tools/build_android.ps1，无越界。日志四节齐全（简报/施工报告/验收清单/本节）；§3 版本要求实测记录完备且抽查属实：AGP 7.4.2 在 `Library/Bee/Android/Prj/IL2CPP/Gradle/build.gradle` 第一手坐实（`id 'com.android.application' version '7.4.2'`）、ProjectSettings L670-671 `scriptingBackend Android: 1`、L387-390 GraphicsAPIs `150000000b000000`（=Vulkan(21)+GLES3(11)，m_Automatic:0）、bundleVersion 0.1.0.13 与 Tools/version.json build=13 一致。**五轮排障记录评估：合格**——未以"轮次表"形式呈现，但实质完整：五条失败链（JDK17 被 major==11 硬校验拒绝→改 JDK11；JDK11.0.2 TLS PSK resume bug→升 11.0.32.1；23.0 CLI 按 `;` 拆包名→退回 19.0 经典版；`latest` 目录布局不被 Unity 认→改 `<版本号>` 目录；新旧 CLI 许可证行为差异）均有根因+修法+证据，且 cmdline-tools 双坑已单列"后续任务别踩"条目；version.json build 7–12 为 Android 排障占用的记录可追溯。
- **遗留确认**：`adb devices -l` 实测输出 `List of devices attached`（空列表，daemon 正常）→ 真机部署（install+logcat 三关键词+screencap）列遗留不阻塞，与报告一致。
- **备注**：验收首轮 run_tests 调用因 `-File` 模式不拆分逗号数组参数报 ValidateSet 错（验收侧调用语法问题，非工程问题），改分段调用后重跑全绿；native 段 configure 时一条 `cmake_probe_tmp` 权限告警不阻断（后续步骤正常通过，exit 0）。
