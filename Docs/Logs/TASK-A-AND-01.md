# TASK-A-AND-01 —— Android 构建管线（平台切换/Gradle/APK/logcat 冒烟）

状态: doing | 派发: 2026-10-31 | 施工: （待填） | 验收: （待填） | commit: （待填）

## 任务简报（主控写）

**前置**：Unity Hub 已装 Android Build Support 模块（含 OpenJDK+SDK/NDK）——**安装由用户/主控负责，本任务开工前先自证**：`PlaybackEngines/AndroidPlayer` 存在+`SDK/platform-tools/adb.exe` 存在；不存在则停止并报告。

**目标**：一键出 Android APK 并 logcat 冒烟验证启动链。

**设计要点**：
1. `Tools/build_android.ps1`：
   - Unity batch `-executeMethod Game.Editor.PlayerBuild.BuildAndroid64()`（PlayerBuild.cs 扩展：`EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTarget.Android)` + `BuildPipeline.BuildPlayer`→`Build/Android/ZFrame.apk`）
   - **首次切平台很慢**（重导入全部资产）——超时放宽到 20 分钟+taskkill 兜底
   - Android 设置核对：ScriptingBackend=IL2CPP（Mono 亦可，报告选型）+ Target Architecture=ARM64 + Graphics API 顺序（Vulkan 优先，GLES3 兜底——780M 手机实证）+ PackageName 已是 com.ZFrame.ZFrame
2. **logcat 冒烟**：`adb install -r` → `adb shell am start` → logcat 过滤启动链三关键词（Initialize engine version/Loaded All Assemblies/GameBootstrap——按真机实测调整）→ `adb exec-out screencap -p` 截图 `Docs/Parity/android_boot.png` → `adb shell am force-stop`
3. **触屏最小适配**：Input System 在 Android 上触屏即 Point——启动链不依赖触控即可验证； gameplay 触屏操纵杆**不做**（明确列遗留），只验证"应用启动渲染+不崩溃"
4. run_tests.ps1 第 9 段 "android"（默认 SKIP，`ANDROID_BUILD=1` 才跑）
5. .gitignore：`Build/Android/`
6. 自测：APK 产出+logcat 三关键词+screencap 成图；run_tests 九段回归

**约束**：白名单 `Tools/**`、`UnityProject/Assets/Game/Editor/**`、`UnityProject/ProjectSettings/`（切平台脚本写）、`.gitignore`、本日志；不 git commit；UTF-8 无 BOM/LF。

## 施工报告（施工代理写）

（待填）

## 验收清单（主控写）

- [ ] V1 亲跑 build_android.ps1 exit 0：APK 产出+体积入报告
- [ ] V2 冒烟亲跑：logcat 三关键词+screencap 成图（真机）
- [ ] V3 run_tests 九段回归（android 段 SKIP/结果如实）
- [ ] V4 IL2CPP/ARM64/Graphics API 设置核对（ProjectSettings 或构建日志）
- [ ] V5 文件规范+施工报告四节完整（触屏遗留单列）

## 验收结论（验收代理写）

（待填）
