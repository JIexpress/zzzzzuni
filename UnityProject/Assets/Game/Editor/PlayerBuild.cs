// =============================================================================
// PlayerBuild.cs —— Player 构建管线正式化（TASK-B-R9-15）+ Android（TASK-A-AND-02）
//
// 批处理入口（Unity 纪律：勿带 -quit——本方法自带 EditorApplication.Exit）：
//   Unity.exe -batchmode -projectPath <UnityProject> -logFile <log>
//     -executeMethod Game.EditorTools.PlayerBuild.BuildWindows64      （Windows64 目录形态）
//     -executeMethod Game.EditorTools.PlayerBuild.BuildAndroid64Mono  （Android Mono 快速验证包）
//     -executeMethod Game.EditorTools.PlayerBuild.BuildAndroid64      （Android IL2CPP ARM64 正式包）
//   （日常不要手工调 Windows 版——走 Tools/build_player.ps1 一键链：构建+体积报告+冒烟）
//
// 职责边界：本类只负责"构建"（版本注入 + BuildPipeline.BuildPlayer）；
//   体积报告（Top-20）与 Windows 包冒烟（启动真 exe 验启动链）在 Tools/build_player.ps1。
//
// 版本单源 = Tools/version.json（{ "version": "0.1.0", "build": N, "versionCode": M }）：
//   bundleVersion   = "<version>.<build+1>"；构建**成功后** build 自增写回
//                     （本次构建占用 N+1，包体与写回后的 version.json 一致）。
//   Android versionCode = versionCode（独立字段，不从 build 派生）；构建成功后 +1 写回。
//   构建前守卫（TASK-A-AND-07）：versionCode < 2 直接报错中止——versionCode 倒退会让
//   Android 拒绝安装 versionName 更高的包，商店端不可逆。逻辑在 ZFrame/Editor/ReleaseVersion.cs。
//   两处自增在同一个写回方法 CommitVersion() 里完成，只有一条写路径。
//   ProjectSettings.asset 的 bundleVersion 由 PlayerSettings API 在编辑器
//   退出时持久化——这就是白名单中"ProjectSettings 由脚本写"的唯一路径。
//
// 场景列表 = EditorBuildSettings 的 enabled 场景（ProjectSettings/EditorBuildSettings.asset）。
// Windows 产物 = <repo>/Build/Windows64/ZFrame.exe（目录形态）。exe 名取 productName=ZFrame
//   而非简报草稿中的 "ZFrameDemo"：实际 ProjectSettings productName 为 ZFrame，
//   改名会连动 LocalLow 存档/日志路径（LocalLow/ZFrame/ZFrame），超出本任务白名单。
// Android 产物 = <repo>/Build/Android/ZFrame.apk（IL2CPP 正式包）
//   与 ZFrame_MonoVerify.apk（Mono 管线验证包，仅验工具链/Gradle 链路，用完可删）。
//
// Android 签名（TASK-A-AND-08）：正式包必须用本地自签 release 证书（CN=ZFrame），
//   走 PlayerSettings.Android.useCustomKeystore。口令来源：环境变量 ZFRAME_KEYSTORE_PASSWORD /
//   ZFRAME_KEY_ALIAS_PASSWORD（还有 ZFRAME_KEYSTORE_PATH / ZFRAME_KEY_ALIAS）优先，
//   仓外 C:/Users/1/Android/keys/keystore.properties 兜底；两处都没有 → 报错中止构建。
//   **绝不静默退回 debug 证书**（debug 包不可分发）。口令绝不进日志。
//   保管与丢失后果见 Docs/交接档案.md「签名与密钥保管」。
//
// Android 外部工具链（TASK-A-AND-02）：构建前把 EditorPrefs 指向仓外用户目录
//   C:/Users/1/Android（OpenJDK11 / platform-tools / android-34 / build-tools 34.0.0 /
//   NDK r23b）。EditorPrefs 键名已按 Unity 2022.3.62f3c1 运行期反射实证
//   （AndroidNDKRoot/AndroidJavaRoot 等 *Root 类的 DirectoryPreferenceKey 属性）：
//     SDK  = "AndroidSdkRoot"      （+ 开关 "SdkUseEmbedded"）
//     NDK  = "AndroidNdkRootR23B"  （+ 开关 "NdkUseEmbedded"）——2021.2+ 升级 r23b
//                                   后取代老键 "AndroidNdkRoot"
//     JDK  = "Jdk11Path"           （+ 开关 "JdkUseEmbedded"）——2021.2+ 起取代旧键
//                                   "JdkPath"；RequiredVersion=11.0.14.1（JDK 17
//                                   会被硬校验拒绝："Incompatible Java version"）
//     Gradle = "GradlePath"        （+ 开关 "GradleUseEmbedded"）
//   gradle 随 AndroidPlayer 自带（PlaybackEngines/AndroidPlayer/Tools/gradle），
//   保持 GradleUseEmbedded=true；JDK/SDK/NDK 本机未随 Hub 安装，一律显式外指。
//
// 退出码：0 = 构建成功；1 = 任何失败。所有路径（含异常）保证 EditorApplication.Exit
//   被调用，批处理不会留僵尸 Unity（超时兜底仍由调用脚本 taskkill 承担）。
// =============================================================================
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Reporting; // BuildReport / BuildSummary / BuildResult（2022.3 在此命名空间）
using UnityEngine;
using UnityEngine.Rendering; // GraphicsDeviceType
using ZFrame.Editor; // ReleaseVersion（发布元数据纯逻辑，可单测）

namespace Game.EditorTools
{
    /// <summary>Windows x64 Player 一键构建入口（TASK-B-R9-15）；Android 构建入口（TASK-A-AND-02）。</summary>
    public static class PlayerBuild
    {
        private const string VersionRelPath = "Tools/version.json";
        private const string OutputRelPath = "Build/Windows64/ZFrame.exe";
        private const string AndroidOutputRelPath = "Build/Android/ZFrame.apk";
        private const string AndroidMonoVerifyOutputRelPath = "Build/Android/ZFrame_MonoVerify.apk";
        private const string AndroidMonoDevOutputRelPath = "Build/Android/ZFrame_MonoDev.apk";

        // 外部工具链装配根（本任务固定为该用户目录；仓外，不入库）。
        // JDK 必须是 11：Unity 2022.3 硬校验 major==11（JDK 17 报
        // "Incompatible Java version '17'"——首跑实测，见 TASK-A-AND-02）。
        private const string ExternalAndroidRoot = "C:/Users/1/Android";
        private const string ExternalNdkPath = ExternalAndroidRoot + "/ndk/23.1.7779620";
        private const string ExternalJdkPath = ExternalAndroidRoot + "/jdk11";

        // ---- Android 自定义签名（TASK-A-AND-08）--------------------------------
        // 用户决策：本地自签 release keystore（不是商店托管、不是继续 debug）。
        // 密钥材料一律在**仓库外**：keystore 与密码文件都在 C:/Users/1/Android/keys，
        // 仓库内只有"路径"和"读取方式"，没有任何密钥材料或明文密码。
        // 读取优先级：环境变量 > properties 文件；两处都没有 → 报错中止构建。
        // **绝不静默退回 Unity 默认 debug 证书**——那会产出"看着像正式包、
        // 实际是 CN=Android Debug、别人的机器装不上"的包，是本任务最重要的一条。
        private const string DefaultKeystorePath = "C:/Users/1/Android/keys/zframe-release.keystore";
        private const string DefaultKeystorePropertiesPath = "C:/Users/1/Android/keys/keystore.properties";
        private const string DefaultKeyAlias = "zframe";
        private const string EnvKeystorePath = "ZFRAME_KEYSTORE_PATH";
        private const string EnvKeystorePassword = "ZFRAME_KEYSTORE_PASSWORD";
        private const string EnvKeyAlias = "ZFRAME_KEY_ALIAS";
        private const string EnvKeyAliasPassword = "ZFRAME_KEY_ALIAS_PASSWORD";

        // EditorPrefs 键名（Unity 2022.3.62f3c1 取证，两道证据）：
        //   ① dll UTF-16 字符串字面量扫描 ② 反射 *Root 类的 DirectoryPreferenceKey/
        //   EmbeddedPreferenceKey 属性（运行期实证）。NDK 键不是老文档的
        //   "AndroidNdkRoot"——2021.2+ 升级 r23b 后改用 "AndroidNdkRootR23B"。
        private const string AndroidSdkRootPrefKey = "AndroidSdkRoot";
        private const string AndroidNdkRootPrefKey = "AndroidNdkRootR23B";
        private const string Jdk11PathPrefKey = "Jdk11Path";
        private const string SdkUseEmbeddedPrefKey = "SdkUseEmbedded";
        private const string NdkUseEmbeddedPrefKey = "NdkUseEmbedded";
        private const string JdkUseEmbeddedPrefKey = "JdkUseEmbedded";
        private const string GradleUseEmbeddedPrefKey = "GradleUseEmbedded";

        /// <summary>version.json 的绝对路径（Application.dataPath = &lt;UnityProject&gt;/Assets
        /// → 仓库根在再上两级）。</summary>
        private static string VersionJsonPath()
        {
            string repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            return Path.Combine(repoRoot, VersionRelPath.Replace('/', '\\'));
        }

        /// <summary>读 version.json + 跑单调性守卫（失败记错误日志并返回 null）。
        /// 只读：不写文件、不改 PlayerSettings。构建与干跑读数是同一条入口。</summary>
        private static ReleaseVersion LoadAndValidateVersion()
        {
            string versionPath = VersionJsonPath();
            ReleaseVersion version;
            try
            {
                version = ReleaseVersion.Load(versionPath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PlayerBuild] version source unusable ({versionPath}): {ex.Message}");
                return null;
            }
            try
            {
                version.ValidateForBuild();
            }
            catch (InvalidOperationException ex)
            {
                Debug.LogError($"[PlayerBuild] version guard rejected the build: {ex.Message}");
                return null;
            }
            return version;
        }

        /// <summary>构建前注入（Windows/Android 共用）：version.json → PlayerSettings
        /// bundleVersion + Android.bundleVersionCode。守卫失败返回 null（调用方退出码 1）。</summary>
        private static ReleaseVersion PrepareVersion()
        {
            ReleaseVersion version = LoadAndValidateVersion();
            if (version == null)
            {
                return null;
            }
            PlayerSettings.bundleVersion = version.BundleVersion;
            PlayerSettings.Android.bundleVersionCode = version.VersionCode;
            Debug.Log(
                $"[PlayerBuild] version: bundleVersion={version.BundleVersion} "
                + $"versionCode={version.VersionCode} (version.json build={version.Build})");
            return version;
        }

        /// <summary>版本单源写回——**唯一写路径**：构建成功后 build+1 与 versionCode+1
        /// 一次落盘（不是两套自增）。构建失败不写回，版本号不被白白消耗。
        /// 写回失败返回 false：包已产出但元数据落后会让下一次构建复用同一个 versionCode，
        /// 商店端会被拒，因此如实按失败收场。</summary>
        private static bool CommitVersion(ReleaseVersion version)
        {
            ReleaseVersion next = version.Advance();
            string versionPath = VersionJsonPath();
            try
            {
                next.Save(versionPath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PlayerBuild] version write-back failed ({versionPath}): {ex.Message}");
                return false;
            }
            Debug.Log(
                $"[PlayerBuild] version committed: bundleVersion={version.BundleVersion} "
                + $"versionCode={version.VersionCode} -> {next.VersionCode}; "
                + $"version.json build {version.Build} -> {next.Build}");
            return true;
        }

        /// <summary>批处理诊断入口（TASK-A-AND-07 干跑用）：读 version.json → 跑单调性守卫 →
        /// 打出本次构建**将要**注入的 bundleVersion/versionCode 与当前 PlayerSettings 值。
        /// 只读：不写 version.json、不改 PlayerSettings、不构建。
        /// 用法：Unity.exe -batchmode -projectPath … -executeMethod
        /// Game.EditorTools.PlayerBuild.DumpReleaseVersion</summary>
        public static void DumpReleaseVersion()
        {
            int exitCode;
            try
            {
                ReleaseVersion version = LoadAndValidateVersion();
                if (version == null)
                {
                    exitCode = 1;
                }
                else
                {
                    Debug.Log(
                        $"[ReleaseVersion] dry-run: bundleVersion={version.BundleVersion} "
                        + $"versionCode={version.VersionCode} (version.json build={version.Build}, "
                        + $"versionCode={version.VersionCode}); after a successful build it would write back "
                        + $"build={version.Advance().Build}, versionCode={version.Advance().VersionCode}");
                    Debug.Log(
                        "[ReleaseVersion] current PlayerSettings: "
                        + $"bundleVersion={PlayerSettings.bundleVersion} "
                        + $"androidBundleVersionCode={PlayerSettings.Android.bundleVersionCode}");
                    Debug.Log("[ReleaseVersion] DONE");
                    exitCode = 0;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ReleaseVersion] FAILED: {ex}");
                exitCode = 1;
            }
            EditorApplication.Exit(exitCode);
        }

        /// <summary>批处理入口：读 version.json → 注入 bundleVersion/versionCode → 构建 →
        /// 成功后 build/versionCode 自增写回（目录形态 Windows64）→ exit 0/1。</summary>
        public static void BuildWindows64()
        {
            int exitCode;
            try
            {
                exitCode = Run();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PlayerBuild] FAILED: {ex}");
                exitCode = 1;
            }
            EditorApplication.Exit(exitCode);
        }

        /// <summary>批处理入口（TASK-B-PLANET-12）：Windows IL2CPP 口径构建——先把
        /// StandaloneWindows64 脚本后端置为 IL2CPP（持久化进 ProjectSettings，与 Android
        /// 侧 SetScriptingBackend 同一 API），再走与 <see cref="BuildWindows64"/> 完全
        /// 相同的 Run()。IL2CPP 下的 P/Invoke/裁剪行为正是 A 段的验证对象，构建产物与
        /// Mono 包同路径（Build/Windows64）由调用方决定是否保留。</summary>
        public static void BuildWindows64Il2cpp()
        {
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.IL2CPP);
            Debug.Log(
                $"[PlayerBuildIl2cpp] scripting={PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone)} " +
                $"(StandaloneWindows64 -> IL2CPP, managedStrippingLevel={PlayerSettings.GetManagedStrippingLevel(BuildTargetGroup.Standalone)})");
            BuildWindows64();
        }

        /// <summary>批处理入口（TASK-B-PLANET-12）：显式 Mono 口径——把 StandaloneWindows64
        /// 后端恢复 Mono（IL2CPP 尝试失败后的复位路径，设置持久化于 ProjectSettings），
        /// 再走同一 Run()。产物即历史 Mono 包形态。</summary>
        public static void BuildWindows64Mono()
        {
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
            Debug.Log(
                $"[PlayerBuildMono] scripting={PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone)} " +
                "(StandaloneWindows64 -> Mono2x restored)");
            BuildWindows64();
        }

        /// <summary>Android IL2CPP ARM64 正式包批处理入口（TASK-A-AND-02）。
        /// 产物 Build/Android/ZFrame.apk；脚本后端 IL2CPP、目标架构 ARM64、
        /// Graphics API 顺序 Vulkan→GLES3、外部工具链指 C:/Users/1/Android。</summary>
        public static void BuildAndroid64()
        {
            int exitCode;
            try
            {
                exitCode = RunAndroid(AndroidOutputRelPath, ScriptingImplementation.IL2CPP, "PlayerBuild");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PlayerBuild] FAILED: {ex}");
                exitCode = 1;
            }
            EditorApplication.Exit(exitCode);
        }

        /// <summary>Android Mono 快速验证包批处理入口（TASK-A-AND-02 已知坑四：
        /// IL2CPP 比 Mono 慢数倍——首包先 Mono 验证工具链/Gradle 链路，再出正式包）。
        /// 产物 Build/Android/ZFrame_MonoVerify.apk。Android 的 Mono 仅支持 ARMv7
        /// （ARM64 需 IL2CPP），验证包架构不受正式包约束。</summary>
        public static void BuildAndroid64Mono()
        {
            int exitCode;
            try
            {
                exitCode = RunAndroid(
                    AndroidMonoVerifyOutputRelPath, ScriptingImplementation.Mono2x, "PlayerBuildMonoVerify");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PlayerBuildMonoVerify] FAILED: {ex}");
                exitCode = 1;
            }
            EditorApplication.Exit(exitCode);
        }

        /// <summary>Android Mono 诊断包批处理入口（TASK-A-AND-03）：Mono 快速管线 +
        /// <see cref="BuildOptions.Development"/>（脚本宏 DEVELOPMENT_BUILD 生效——
        /// DeviceFileLogger 的设备文件日志只在该宏下编译；正式 IL2CPP 包不带
        /// Development，日志代码不编译，发布零开销）。产物
        /// Build/Android/ZFrame_MonoDev.apk。真机诊断时用它替换 MonoVerify。</summary>
        public static void BuildAndroid64MonoDev()
        {
            int exitCode;
            try
            {
                exitCode = RunAndroid(
                    AndroidMonoDevOutputRelPath, ScriptingImplementation.Mono2x, "PlayerBuildMonoDev",
                    BuildOptions.Development);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PlayerBuildMonoDev] FAILED: {ex}");
                exitCode = 1;
            }
            EditorApplication.Exit(exitCode);
        }

        /// <summary>把 Android 外部工具链（JDK/SDK/NDK）写入 EditorPrefs 并关掉对应的
        /// embedded 开关（本机 Hub 未装 SDK/NDK/OpenJDK 子模块，embedded 路径不存在；
        /// gradle 随 AndroidPlayer 自带，保持 GradleUseEmbedded=true）。
        /// 键名按 Unity 2022.3.62f3c1 运行期反射实证（*Root 类的
        /// DirectoryPreferenceKey 属性）：NDK 是 "AndroidNdkRootR23B"（2021.2+ 升级
        /// r23b 后取代老键 "AndroidNdkRoot"）、JDK 是 "Jdk11Path"（取代 "JdkPath"）；
        /// 必须在 SwitchActiveBuildTarget 之前写入。</summary>
        private static void ConfigureExternalAndroidTools()
        {
            EditorPrefs.SetString(AndroidSdkRootPrefKey, ExternalAndroidRoot);
            EditorPrefs.SetString(AndroidNdkRootPrefKey, ExternalNdkPath);
            EditorPrefs.SetString(Jdk11PathPrefKey, ExternalJdkPath);
            EditorPrefs.SetBool(SdkUseEmbeddedPrefKey, false);
            EditorPrefs.SetBool(NdkUseEmbeddedPrefKey, false);
            EditorPrefs.SetBool(JdkUseEmbeddedPrefKey, false);
            EditorPrefs.SetBool(GradleUseEmbeddedPrefKey, true);
            Debug.Log(
                "[PlayerBuild] android external tools: "
                + $"{AndroidSdkRootPrefKey}={ExternalAndroidRoot} "
                + $"{AndroidNdkRootPrefKey}={ExternalNdkPath} "
                + $"{Jdk11PathPrefKey}={ExternalJdkPath} "
                + "(Sdk/Ndk/JdkUseEmbedded=false, GradleUseEmbedded=true)");
        }

        /// <summary>批处理诊断入口（TASK-A-AND-02 排障用）：dump Android 工具链相关的
        /// EditorPrefs 键值、环境变量，并反射 UnityEditor.Android.Extensions 的
        /// ExternalTools/Ndk 类静态成员（含 Unity 期望的版本常量）。
        /// 用法：Unity.exe -batchmode -projectPath … -executeMethod
        /// Game.EditorTools.PlayerBuild.DumpAndroidToolchainState -logFile -</summary>
        public static void DumpAndroidToolchainState()
        {
            int exitCode;
            try
            {
                Debug.Log("[AndroidDiag] === EditorPrefs ===");
                string[] stringKeys = { AndroidSdkRootPrefKey, AndroidNdkRootPrefKey, Jdk11PathPrefKey, "GradlePath" };
                foreach (string key in stringKeys)
                {
                    Debug.Log($"[AndroidDiag] EditorPrefs[{key}] = '{EditorPrefs.GetString(key)}'");
                }
                string[] boolKeys = { SdkUseEmbeddedPrefKey, NdkUseEmbeddedPrefKey, JdkUseEmbeddedPrefKey, GradleUseEmbeddedPrefKey };
                foreach (string key in boolKeys)
                {
                    Debug.Log($"[AndroidDiag] EditorPrefs[{key}] = {EditorPrefs.GetBool(key)}");
                }

                Debug.Log("[AndroidDiag] === env vars ===");
                string[] envKeys = { "ANDROID_NDK_HOME", "ANDROID_NDK_ROOT", "ANDROID_SDK_HOME", "ANDROID_SDK_ROOT", "JAVA_HOME" };
                foreach (string key in envKeys)
                {
                    Debug.Log($"[AndroidDiag] env[{key}] = '{Environment.GetEnvironmentVariable(key)}'");
                }

                Debug.Log("[AndroidDiag] === reflect UnityEditor.Android.Extensions ===");
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .First(a => a.GetName().Name == "UnityEditor.Android.Extensions");
                var allTypes = asm.GetTypes().Where(t => t.FullName != null).ToArray();
                foreach (var type in allTypes)
                {
                    string fullName = type.FullName;
                    if (fullName.Contains("Ndk") || fullName.Contains("ExternalTools") || fullName.Contains("Jdk"))
                    {
                        Debug.Log($"[AndroidDiag] type: {fullName}");
                    }
                }
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
                var settingsType = allTypes.FirstOrDefault(t => t.Name == "AndroidExternalToolsSettings");
                if (settingsType != null)
                {
                    Debug.Log($"[AndroidDiag] --- getters of {settingsType.FullName} ---");
                    foreach (string propName in new[] { "sdkRootPath", "ndkRootPath", "jdkRootPath", "gradlePath" })
                    {
                        var prop = settingsType.GetProperty(propName, all);
                        if (prop == null) { Debug.Log($"[AndroidDiag]   {propName}: <missing>"); continue; }
                        string valueText;
                        try
                        {
                            object target = prop.GetMethod.IsStatic ? null : Activator.CreateInstance(settingsType, true);
                            valueText = (prop.GetValue(target) as string) ?? "null";
                        }
                        catch (Exception ex)
                        {
                            valueText = "<" + ex.GetType().Name + ": " + ex.Message + ">";
                        }
                        Debug.Log($"[AndroidDiag]   {propName} = '{valueText}'");
                    }
                }
                Debug.Log("[AndroidDiag] --- all public types in Extensions dll ---");
                foreach (var type in allTypes.OrderBy(t => t.FullName))
                {
                    if (type.FullName.Contains("PostProcessor") || type.FullName.Contains("Internal"))
                    {
                        continue; // 噪音裁剪
                    }
                    Debug.Log($"[AndroidDiag] type: {type.FullName}");
                }
                // Pref 包装类（AndroidNDKRoot/AndroidGradleRoot/…）的静态字段值：
                // 它们持有真正的 EditorPrefs 键名与当前值。
                foreach (var type in allTypes.Where(t =>
                    t.Name == "AndroidNDKRoot" || t.Name == "AndroidGradleRoot" ||
                    t.Name == "AndroidJavaRoot" || t.Name == "AndroidSDKRoot" ||
                    t.Name == "AndroidKeystoresDedicatedLocationRoot"))
                {
                    Debug.Log($"[AndroidDiag] --- statics of {type.FullName} ---");
                    foreach (var field in type.GetFields(all))
                    {
                        string valueText;
                        try { valueText = field.IsStatic ? (field.GetValue(null)?.ToString() ?? "null") : "<instance>"; }
                        catch (Exception ex) { valueText = "<" + ex.GetType().Name + ">"; }
                        Debug.Log($"[AndroidDiag]   field {field.FieldType.Name} {field.Name} = {valueText}");
                    }
                    foreach (var prop in type.GetProperties(all))
                    {
                        string valueText;
                        try
                        {
                            object target = prop.GetMethod.IsStatic ? null : Activator.CreateInstance(type, true);
                            valueText = prop.GetValue(target)?.ToString() ?? "null";
                        }
                        catch (Exception ex) { valueText = "<" + ex.GetType().Name + ">"; }
                        Debug.Log($"[AndroidDiag]   prop {prop.PropertyType.Name} {prop.Name} = {valueText}");
                    }
                }
                // Ndk 期望版本：列全部含 "Ndk" 的类型的静态成员值
                foreach (var type in allTypes.Where(t => t.Name == "Ndk" || t.Name.Contains("NdkVersion") || t.Name == "AndroidNdk"))
                {
                    Debug.Log($"[AndroidDiag] --- statics of {type.FullName} ---");
                    foreach (var field in type.GetFields(all))
                    {
                        string valueText;
                        try { valueText = field.IsStatic ? (field.GetValue(null)?.ToString() ?? "null") : "<instance>"; }
                        catch (Exception ex) { valueText = "<" + ex.GetType().Name + ">"; }
                        Debug.Log($"[AndroidDiag]   field {field.FieldType.Name} {field.Name} = {valueText}");
                    }
                }
                Debug.Log("[AndroidDiag] DONE");
                exitCode = 0;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AndroidDiag] FAILED: {ex}");
                exitCode = 1;
            }
            EditorApplication.Exit(exitCode);
        }

        /// <summary>极简 properties 读取（key=value，# / ! 注释）。只服务于签名配置，
        /// 不引第三方依赖。文件不存在返回 null。</summary>
        private static Dictionary<string, string> TryLoadProperties(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == '!')
                {
                    continue;
                }
                int sep = line.IndexOf('=');
                if (sep <= 0)
                {
                    continue;
                }
                map[line.Substring(0, sep).Trim()] = line.Substring(sep + 1).Trim();
            }
            return map;
        }

        /// <summary>按"环境变量优先、properties 兜底"取一个签名值。
        /// 两处都没有返回 null，并把 <paramref name="source"/> 置为 null。</summary>
        private static string ResolveSigningValue(string envKey, string propKey,
            Dictionary<string, string> props, out string source)
        {
            string fromEnv = Environment.GetEnvironmentVariable(envKey);
            if (!string.IsNullOrEmpty(fromEnv))
            {
                source = "env " + envKey;
                return fromEnv;
            }
            string fromProps;
            if (props != null && props.TryGetValue(propKey, out fromProps) && !string.IsNullOrEmpty(fromProps))
            {
                source = "properties " + propKey;
                return fromProps;
            }
            source = null;
            return null;
        }

        /// <summary>Android 自定义签名（TASK-A-AND-08）：把 keystore 与口令注入
        /// PlayerSettings.Android，缺任何一项口令都**报错中止构建**（返回 false）。
        ///
        /// 口令来源：环境变量 ZFRAME_* 优先 → 仓外 properties 文件兜底；
        /// 口令只在内存里传给 PlayerSettings，**绝不写日志**（日志只打来源与遮挡值）。
        /// keystore 必须位于仓库之外（红线：仓库内不允许出现密钥材料）。</summary>
        private static bool ConfigureAndroidSigning(string logTag)
        {
            string keystorePath = Environment.GetEnvironmentVariable(EnvKeystorePath);
            string keystoreSource = "env " + EnvKeystorePath;
            if (string.IsNullOrEmpty(keystorePath))
            {
                keystorePath = DefaultKeystorePath;
                keystoreSource = "default path";
            }

            Dictionary<string, string> props = TryLoadProperties(DefaultKeystorePropertiesPath);
            string propStoreFile;
            if (props != null && props.TryGetValue("storeFile", out propStoreFile)
                && !string.IsNullOrEmpty(propStoreFile) && keystoreSource == "default path")
            {
                keystorePath = propStoreFile;
                keystoreSource = "properties storeFile";
            }

            string storePassSource, keyPassSource, aliasSource;
            string storePass = ResolveSigningValue(EnvKeystorePassword, "storePassword", props, out storePassSource);
            string keyPass = ResolveSigningValue(EnvKeyAliasPassword, "keyPassword", props, out keyPassSource);
            string alias = ResolveSigningValue(EnvKeyAlias, "keyAlias", props, out aliasSource);
            if (string.IsNullOrEmpty(alias))
            {
                alias = DefaultKeyAlias; // alias 不是秘密；默认值与本项目 keystore 约定一致
                aliasSource = "default alias";
            }

            if (string.IsNullOrEmpty(storePass))
            {
                Debug.LogError(
                    $"[{logTag}] SIGNING ABORT: keystore password not found. Set env {EnvKeystorePassword} "
                    + $"or 'storePassword' in {DefaultKeystorePropertiesPath}. "
                    + "Build aborted — deliberately NOT falling back to Unity's debug certificate "
                    + "(a debug-signed APK looks like a release build but cannot be distributed).");
                return false;
            }
            if (string.IsNullOrEmpty(keyPass))
            {
                Debug.LogError(
                    $"[{logTag}] SIGNING ABORT: key alias password not found. Set env {EnvKeyAliasPassword} "
                    + $"or 'keyPassword' in {DefaultKeystorePropertiesPath}. "
                    + "Build aborted — deliberately NOT falling back to Unity's debug certificate.");
                return false;
            }
            if (!File.Exists(keystorePath))
            {
                Debug.LogError(
                    $"[{logTag}] SIGNING ABORT: keystore not found: {keystorePath} (source: {keystoreSource}). "
                    + "Build aborted — deliberately NOT falling back to Unity's debug certificate.");
                return false;
            }

            // 红线：密钥材料不得进仓库。配置指向仓库内路径时直接拒绝（防手滑把 .keystore 拷进仓库）。
            string repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            string fullKeystore = Path.GetFullPath(keystorePath);
            if ((fullKeystore + Path.DirectorySeparatorChar).StartsWith(
                    repoRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError(
                    $"[{logTag}] SIGNING ABORT: keystore lies inside the repository ({fullKeystore}). "
                    + "Key material must stay outside the repo (see Docs/交接档案.md '签名与密钥保管').");
                return false;
            }

            PlayerSettings.Android.useCustomKeystore = true;
            PlayerSettings.Android.keystoreName = keystorePath;
            PlayerSettings.Android.keystorePass = storePass;
            PlayerSettings.Android.keyaliasName = alias;
            PlayerSettings.Android.keyaliasPass = keyPass;

            if (!PlayerSettings.Android.useCustomKeystore)
            {
                Debug.LogError($"[{logTag}] SIGNING ABORT: useCustomKeystore did not stick.");
                return false;
            }
            Debug.Log(
                $"[{logTag}] signing: useCustomKeystore=true keystore={keystorePath} (source: {keystoreSource}) "
                + $"alias={alias} (source: {aliasSource}) "
                + $"storePassword=<redacted, {storePassSource}> keyPassword=<redacted, {keyPassSource}>");
            return true;
        }

        /// <summary>Android 构建共用体：版本注入 → 外部工具链 EditorPrefs → 切活跃
        /// BuildTarget → 脚本后端/架构/Graphics API → BuildPlayer（.apk 形态）→ 0/1。
        /// <paramref name="extraOptions"/> 追加构建选项（诊断包传
        /// <see cref="BuildOptions.Development"/>，正式包传 <see cref="BuildOptions.None"/>）。</summary>
        private static int RunAndroid(string outputRelPath, ScriptingImplementation backend, string logTag,
            BuildOptions extraOptions = BuildOptions.None)
        {
            // ---- 1. 版本单源：守卫 → 注入（与 Windows 版共用）-------------
            ReleaseVersion version = PrepareVersion();
            if (version == null)
            {
                return 1;
            }

            // ---- 2. 外部工具链 EditorPrefs（简报要求：在 Switch 之前写）----
            ConfigureExternalAndroidTools();

            // ---- 3. 切活跃 BuildTarget -------------------------------------
            if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
            {
                Debug.LogError($"[{logTag}] SwitchActiveBuildTarget(Android) failed");
                return 1;
            }

            // ---- 4. Android Player 设置 ------------------------------------
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, backend);
            // Android 的 Mono 后端仅支持 ARMv7；ARM64 需要 IL2CPP。
            PlayerSettings.Android.targetArchitectures = backend == ScriptingImplementation.IL2CPP
                ? AndroidArchitecture.ARM64
                : AndroidArchitecture.ARMv7;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[]
            {
                GraphicsDeviceType.Vulkan,
                GraphicsDeviceType.OpenGLES3
            });
            Debug.Log(
                $"[{logTag}] scripting={backend} arch={PlayerSettings.Android.targetArchitectures} "
                + "graphics=Vulkan,GLES3");

            // ---- 4.5 自定义签名（TASK-A-AND-08）----------------------------
            // 缺口令 / 缺 keystore / keystore 在仓库内 → 直接中止（返回 1），不退回 debug。
            if (!ConfigureAndroidSigning(logTag))
            {
                return 1;
            }

            // ---- 5. 场景列表 = EditorBuildSettings（enabled 场景）----------
            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();
            if (scenes.Length == 0)
            {
                Debug.LogError($"[{logTag}] EditorBuildSettings has no enabled scenes");
                return 1;
            }
            foreach (string scenePath in scenes)
            {
                Debug.Log($"[{logTag}] scene: {scenePath}");
            }

            // ---- 6. 构建（.apk 形态）---------------------------------------
            string repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            string outputPath = Path.Combine(repoRoot, outputRelPath.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = BuildTarget.Android,
                options = extraOptions
            };
            Debug.Log($"[{logTag}] building -> {outputPath} (options={extraOptions})");
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            Debug.Log(
                $"[{logTag}] result={summary.result} totalSize={summary.totalSize} "
                + $"errors={summary.totalErrors} output={summary.outputPath}");
            if (summary.result != BuildResult.Succeeded || summary.totalErrors > 0)
            {
                Debug.LogError(
                    $"[{logTag}] build failed: result={summary.result} errors={summary.totalErrors}");
                return 1;
            }
            // ---- 7. 版本写回（唯一写路径，仅构建成功后）--------------------
            if (!CommitVersion(version))
            {
                return 1;
            }
            Debug.Log($"[{logTag}] SUCCESS");
            return 0;
        }

        private static int Run()
        {
            // ---- 1. 版本单源：守卫 → 注入（共用）--------------------------
            ReleaseVersion version = PrepareVersion();
            if (version == null)
            {
                return 1;
            }

            // Application.dataPath = <UnityProject>/Assets → 仓库根在再上两级。
            string repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            string outputPath = Path.Combine(repoRoot, OutputRelPath.Replace('/', '\\'));

            // ---- 2. 场景列表 = EditorBuildSettings（enabled 场景）----------
            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();
            if (scenes.Length == 0)
            {
                Debug.LogError("[PlayerBuild] EditorBuildSettings has no enabled scenes");
                return 1;
            }
            foreach (string scenePath in scenes)
            {
                Debug.Log($"[PlayerBuild] scene: {scenePath}");
            }

            // ---- 3. 构建（目录形态 Windows64）------------------------------
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };
            Debug.Log($"[PlayerBuild] building -> {outputPath}");
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            Debug.Log(
                $"[PlayerBuild] result={summary.result} totalSize={summary.totalSize} "
                + $"errors={summary.totalErrors} output={summary.outputPath}");
            if (summary.result != BuildResult.Succeeded || summary.totalErrors > 0)
            {
                Debug.LogError(
                    $"[PlayerBuild] build failed: result={summary.result} errors={summary.totalErrors}");
                return 1;
            }
            // ---- 4. 版本写回（唯一写路径，仅构建成功后）--------------------
            if (!CommitVersion(version))
            {
                return 1;
            }
            Debug.Log("[PlayerBuild] SUCCESS");
            return 0;
        }
    }
}
#endif
