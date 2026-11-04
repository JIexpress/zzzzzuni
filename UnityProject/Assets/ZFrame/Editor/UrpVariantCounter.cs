using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace ZFrame.Editor
{
    /// <summary>
    /// TASK-A-R9-16 的「可复核计数工具 + batchmode 调研实证」。
    ///
    /// 一次运行同时做四件事，全部输出到 Unity 日志（-logFile - 可捕获）：
    ///  1) 反射探测 UnityEditor.ShaderUtil 在 Unity 2022.3 下有哪些变体统计 API，
    ///     并实调一次，给出「哪条可行 / 哪条不可行」的实证；
    ///  2) 从本工程实际用到的 URP shader 源码静态解析 #pragma 关键字，得到
    ///     去重关键字全集 K（输入集合完全可复核，来源文件逐一列出）；
    ///  3) 对 K 中每个单关键字组合调用纯函数 UrpVariantRules.Evaluate，
    ///     输出「规则命中数 / 保留数 / 白名单保护数」；
    ///  4) 用「空规则表（改前基线）」与「本任务核对后的规则表（改后）」跑
    ///     同一输入集合，输出改前 / 改后两栏，做同命令对照；
    ///     并自检三档 / Volume 必需变体一律 Keep。
    ///
    /// 命令（Git Bash 启动，勿转调 powershell；本机 -force-d3d12 会挂起，勿加）：
    ///   "C:/Program Files/Unity 2022.3.62f3c1/Editor/Unity.exe" -batchmode -quit \
    ///     -projectPath "C:/Users/1/Desktop/zcode/GameFramework/UnityProject" \
    ///     -logFile - -executeMethod ZFrame.Editor.UrpVariantCounter.RunFromCommandLine
    /// 或菜单 ZFrame/URP 变体/统计与规则命中。
    ///
    /// 【口径声明】本工具输出的是「静态单关键字组合的规则命中数」，不是真实
    /// 构建变体数。batchmode 无构建时 IPreprocessShaders 不触发、且
    /// UniversalRenderPipelineGlobalSettings.asset 的 m_ShaderVariantLogLevel=0
    /// 不产出变体日志，故真实构建变体数 / 构建时长对照列为「未验证」。
    /// </summary>
    public static class UrpVariantCounter
    {
        /// <summary>
        /// 本工程实际用到的 URP shader（相对 URP 包根目录）。
        /// 判据：
        ///  * Particles/ParticlesUnlit —— GraphicsSettings.m_AlwaysIncludedShaders
        ///    第 8 项 guid 0406db5a… 指向它；A-AND-06 12288 变体的直接来源。
        ///  * Lit / Unlit —— Shader.Find("Universal Render Pipeline/Lit|Unlit")
        ///    硬编码于 ParityBench / KernelCrowdRenderer / ModelAssetSetup /
        ///    TestAssetSetup / 多个 PlayMode 像素证据用例。
        ///  * PostProcessing/UberPost —— Vol_Medium(Bloom) / Vol_High(Bloom+
        ///    ColorAdjustments) 的后处理落点。
        /// </summary>
        private static readonly string[] ScannedShaderRelativePaths =
        {
            "Shaders/Particles/ParticlesUnlit.shader",
            "Shaders/Lit.shader",
            "Shaders/Unlit.shader",
            "Shaders/PostProcessing/UberPost.shader",
        };

        private const string UrpPackageName = "com.unity.render-pipelines.universal";

        /// <summary>
        /// 三档 / Volume 必需关键字自检清单：本剥离器必须一律 Keep。
        /// 覆盖主光阴影 / cascade / 软阴影 / 额外光 / cookie / LOD / 粒子材质
        /// / 后处理 Bloom / tonemapping / instancing / fog。
        /// </summary>
        private static readonly string[] RequiredKeepChecklist =
        {
            "_MAIN_LIGHT_SHADOWS",
            "_MAIN_LIGHT_SHADOWS_CASCADE",
            "_SHADOWS_SOFT",
            "_ADDITIONAL_LIGHTS",
            "_LIGHT_COOKIES",
            "LOD_FADE_CROSSFADE",
            "_ALPHATEST_ON",
            "_FLIPBOOKBLENDING_ON",
            "_SOFTPARTICLES_ON",
            "_EMISSION",
            "_BLOOM_LQ",
            "_BLOOM_HQ",
            "_BLOOM_LQ_DIRT",
            "_TONEMAP_ACES",
            "_HDR_GRADING",
            "INSTANCING_ON",
            "FOG_LINEAR",
        };

        [MenuItem("ZFrame/URP 变体/统计与规则命中", false, 0)]
        public static void RunFromMenu()
        {
            Run();
        }

        /// <summary>-executeMethod 入口（无参）。</summary>
        public static void RunFromCommandLine()
        {
            Run();
        }

        private static void Log(string line)
        {
            Debug.Log("[UrpVariantCounter] " + line);
        }

        public static void Run()
        {
            Log("================ TASK-A-R9-16 URP 变体计数/规则命中报告 ================");
            Log("UnityVersion   : " + Application.unityVersion);
            Log("Pipeline       : URP (manifest.json / packages-lock.json = 14.0.11；本机 PackageCache 目录 = 14.0.12)");
            Log("TargetPlatforms: StandaloneWindows64 (780M/D3D12), Android IL2CPP arm64");
            Log("ActiveBuild    : " + EditorUserBuildSettings.activeBuildTarget);
            Log("StripperEnabled(default off): " + UrpVariantStripper.IsEnabled);
            Log("GeneratedUtc   : " + DateTime.UtcNow.ToString("o"));

            // ---- 1) 调研：ShaderUtil 变体统计 API 可用性 ----
            ProbeShaderUtilApi();

            // ---- 2) 真实 shader pragma 解析 -> 关键字全集 K ----
            List<string> keywords = CollectKeywords(out List<string> scannedFiles);
            Log("---- 2) 静态输入集合（真实 shader #pragma 解析）----");
            foreach (string file in scannedFiles)
            {
                Log("  scanned: " + file);
            }

            Log("去重关键字全集 K = " + keywords.Count + " 个：[" + string.Join(", ", keywords) + "]");

            // ---- 3) 改前 / 改后（同一命令、同一输入集合）----
            Log("---- 3) 改前 / 改后对照（同一命令、同一输入集合）----");
            string platform = EditorUserBuildSettings.activeBuildTarget.ToString();
            const bool isDevelopmentBuild = false; // 对照按正式构建口径

            CountResult before = Count(keywords, new UrpVariantRules.StripRule[0], platform, isDevelopmentBuild);
            CountResult after = Count(keywords, UrpVariantRules.StripRules, platform, isDevelopmentBuild);

            Log("[改前] 规则表为空（剥离器未配置 / 默认关闭，不删任何组合）");
            Log("        evaluated=" + before.Evaluated + "  stripped=" + before.Stripped
                + "  kept=" + before.Kept + "  whitelistProtected=" + before.WhitelistProtected);
            Log("[改后] 本任务核对后的 " + UrpVariantRules.StripRules.Length + " 条规则");
            Log("        evaluated=" + after.Evaluated + "  stripped=" + after.Stripped
                + "  kept=" + after.Kept + "  whitelistProtected=" + after.WhitelistProtected);
            Log("        delta_stripped=+" + (after.Stripped - before.Stripped));
            Log("  改后命中(可剥)关键字 [" + after.StrippedKeywords.Count + "]: ["
                + string.Join(", ", after.StrippedKeywords) + "]");
            Log("  白名单保护命中: [" + string.Join(", ", after.WhitelistKeywords) + "]");
            Log("  口径: 上列数字是「静态单关键字组合的规则命中数」，不是真实构建变体数。");
            Log("        真实构建变体数 / 构建时长对照：未验证（batchmode 无构建；变体日志 level=0）。");

            // ---- 4) 必需变体保护自检 ----
            Log("---- 4) 三档 / Volume 必需变体保护自检（必须全部 Keep）----");
            int keepOk = 0;
            int keepBad = 0;
            foreach (string keyword in RequiredKeepChecklist)
            {
                UrpVariantRules.Decision d = UrpVariantRules.Evaluate(
                    string.Empty, new[] { keyword }, platform, isDevelopmentBuild,
                    UrpVariantRules.StripRules, null);
                if (d.Strip)
                {
                    keepBad++;
                    Log("  FAIL(误伤) " + keyword + " -> STRIP : " + d.Reason);
                }
                else
                {
                    keepOk++;
                }
            }

            Log("  必需关键字自检: keep=" + keepOk + " / " + RequiredKeepChecklist.Length
                + " 误伤=" + keepBad + " => " + (keepBad == 0 ? "OK（未剥任何必需变体）" : "FAIL"));

            Log("================ 报告结束 ================");
        }

        private sealed class CountResult
        {
            public int Evaluated;
            public int Stripped;
            public int Kept;
            public int WhitelistProtected;
            public readonly List<string> StrippedKeywords = new List<string>();
            public readonly List<string> WhitelistKeywords = new List<string>();
        }

        private static CountResult Count(
            IReadOnlyList<string> keywords,
            IReadOnlyList<UrpVariantRules.StripRule> rules,
            string platform,
            bool isDevelopmentBuild)
        {
            var result = new CountResult();
            foreach (string keyword in keywords)
            {
                UrpVariantRules.Decision d = UrpVariantRules.Evaluate(
                    string.Empty, new[] { keyword }, platform, isDevelopmentBuild, rules, null);
                result.Evaluated++;
                if (d.Strip)
                {
                    result.Stripped++;
                    result.StrippedKeywords.Add(keyword);
                }
                else
                {
                    result.Kept++;
                    if (d.Reason.StartsWith("白名单保护", StringComparison.Ordinal))
                    {
                        result.WhitelistProtected++;
                        result.WhitelistKeywords.Add(keyword);
                    }
                }
            }

            return result;
        }

        // ------------------------------------------------------------------
        // 2) 从 URP 包内真实 .shader 文件解析 #pragma 关键字
        // ------------------------------------------------------------------
        private static List<string> CollectKeywords(out List<string> scannedFiles)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            scannedFiles = new List<string>();

            string packageRoot = ResolveUrpPackageRoot();
            if (string.IsNullOrEmpty(packageRoot))
            {
                Log("  WARN: 无法解析 URP 包根目录，关键字集合为空（不编造）");
                return new List<string>();
            }

            foreach (string relative in ScannedShaderRelativePaths)
            {
                string full = Path.Combine(packageRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full))
                {
                    Log("  WARN: shader 文件不存在，跳过: " + full);
                    continue;
                }

                scannedFiles.Add(relative + " (resolved under " + packageRoot + ")");
                foreach (string line in File.ReadAllLines(full))
                {
                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith("#pragma", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string[] tokens = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length < 2)
                    {
                        continue;
                    }

                    string directive = tokens[1];
                    if (!directive.StartsWith("multi_compile", StringComparison.Ordinal) &&
                        !directive.StartsWith("shader_feature", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    for (int i = 2; i < tokens.Length; i++)
                    {
                        string token = tokens[i];
                        // 行尾注释（如 "// forward-only variant"）：其后全部 token 忽略，
                        // 否则注释词会被误当成关键字收进输入集合。
                        if (token.StartsWith("//", StringComparison.Ordinal))
                        {
                            break;
                        }

                        if (token == "_" || token.StartsWith("#", StringComparison.Ordinal) ||
                            token.IndexOf('=') >= 0)
                        {
                            continue;
                        }

                        // multi_compile_fog / multi_compile_instancing 等无显式
                        // token 的内建维度不在此解析（不凭空补关键字）。
                        set.Add(token);
                    }
                }
            }

            var list = set.ToList();
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        private static string ResolveUrpPackageRoot()
        {
            try
            {
                UnityEditor.PackageManager.PackageInfo info =
                    UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + UrpPackageName);
                if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
                {
                    return info.resolvedPath;
                }
            }
            catch (Exception e)
            {
                Log("  WARN: PackageInfo.FindForAssetPath 失败: " + e.Message);
            }

            return null;
        }

        // ------------------------------------------------------------------
        // 1) 调研：ShaderUtil 变体统计 API 可用性（反射实证）
        // ------------------------------------------------------------------
        private static void ProbeShaderUtilApi()
        {
            Log("---- 1) 调研：UnityEditor.ShaderUtil 变体统计 API 可用性 ----");
            Type t = typeof(ShaderUtil);
            Log("  ShaderUtil 类型: " + t.FullName);

            MethodInfo[] methods = t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var variantMethods = methods
                .Where(m => m.Name.IndexOf("Variant", StringComparison.OrdinalIgnoreCase) >= 0
                         || m.Name.IndexOf("Keyword", StringComparison.OrdinalIgnoreCase) >= 0
                         || m.Name.IndexOf("ShaderData", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ToArray();

            Log("  变体/关键字相关静态方法数: " + variantMethods.Length);
            foreach (MethodInfo m in variantMethods)
            {
                string ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                Log("    " + (m.IsPublic ? "public " : "nonpublic ") + m.ReturnType.Name
                    + " " + m.Name + "(" + ps + ")");
            }

            // 试 1：GetShaderVariantCount —— 真实变体数（potential）能否拿到？
            MethodInfo countMethod = methods.FirstOrDefault(
                m => m.Name == "GetShaderVariantCount");
            if (countMethod == null)
            {
                Log("  [试1] GetShaderVariantCount: 不存在于本 Unity 版本的 ShaderUtil");
            }
            else
            {
                string ps = string.Join(", ", countMethod.GetParameters().Select(p => p.ParameterType.Name));
                Log("  [试1] GetShaderVariantCount 存在，签名 (" + ps + ")；尝试实调 URP/Lit");
                try
                {
                    Shader lit = Shader.Find("Universal Render Pipeline/Lit");
                    object[] argv = BuildArguments(countMethod.GetParameters(), lit);
                    object value = countMethod.Invoke(null, argv);
                    Log("  [试1] 实调结果 = " + (value == null ? "<null>" : value.ToString())
                        + "（仅为 potential/静态估计，非完整构建实际编译变体数）");
                }
                catch (Exception e)
                {
                    Log("  [试1] 实调失败: " + Unwrap(e).Message + " => 该路径不可行");
                }
            }

            // 试 2：GetShaderGlobalKeywords / GetShaderLocalKeywords —— 能否枚举关键字？
            foreach (string name in new[] { "GetShaderGlobalKeywords", "GetShaderLocalKeywords" })
            {
                MethodInfo mi = methods.FirstOrDefault(m => m.Name == name);
                if (mi == null)
                {
                    Log("  [试2] " + name + ": 不存在");
                    continue;
                }

                try
                {
                    Shader lit = Shader.Find("Universal Render Pipeline/Lit");
                    object[] argv = BuildArguments(mi.GetParameters(), lit);
                    object value = mi.Invoke(null, argv);
                    if (value is string[] arr)
                    {
                        Log("  [试2] " + name + " 可行，URP/Lit 返回 " + arr.Length + " 个关键字");
                    }
                    else
                    {
                        Log("  [试2] " + name + " 可调用，返回类型 " + (value == null ? "null" : value.GetType().Name));
                    }
                }
                catch (Exception e)
                {
                    Log("  [试2] " + name + " 调用失败: " + Unwrap(e).Message);
                }
            }

            // 试 3：GetVariantCount —— ShaderUtil 里真实存在的 potential 变体计数（nonpublic，反射可调）
            MethodInfo vcMethod = methods.FirstOrDefault(m => m.Name == "GetVariantCount");
            if (vcMethod == null)
            {
                Log("  [试3] GetVariantCount: 不存在");
            }
            else
            {
                string ps = string.Join(", ", vcMethod.GetParameters().Select(p => p.ParameterType.Name));
                Log("  [试3] GetVariantCount 存在（nonpublic），签名 (" + ps
                    + ")；尝试实调 URP/Lit 与 Particles/Unlit");
                foreach (string shaderName in new[]
                         {
                             "Universal Render Pipeline/Lit",
                             "Universal Render Pipeline/Particles/Unlit",
                         })
                {
                    try
                    {
                        Shader s = Shader.Find(shaderName);
                        object[] argv = BuildArguments(vcMethod.GetParameters(), s);
                        object value = vcMethod.Invoke(null, argv);
                        Log("  [试3] " + shaderName + " -> potential variant count = "
                            + (value == null ? "<null>" : value.ToString())
                            + "（静态 potential 估计，非完整构建实际编译变体数）");
                    }
                    catch (Exception e)
                    {
                        Log("  [试3] " + shaderName + " 实调失败: " + Unwrap(e).Message);
                    }
                }
            }

            Log("  [调研结论-A] ShaderUtil 路径：反射可枚举关键字(GetShaderGlobal/LocalKeywords)、");
            Log("               可取 potential 变体数(GetVariantCount, nonpublic)，但 -batchmode");
            Log("               无构建时均拿不到「本次实际编译的变体数」。");
            Log("  [调研结论-B] IPreprocessShaders 回调计数：只在真实 Player/AB 构建时触发，");
            Log("               -batchmode 不构建 => 不触发 => 本轮不可得（未验证）。");
            Log("  [调研结论-C] 构建日志解析：GlobalSettings m_ShaderVariantLogLevel=0 不产出");
            Log("               变体日志；且本任务禁止改 GraphicsSettings/URP 资产 => 不可行。");
            Log("  [可行口径] 本工具改输出「静态单关键字组合的规则命中数」，可复核、不编数字。");
        }

        private static object[] BuildArguments(ParameterInfo[] parameters, Shader shader)
        {
            var argv = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                Type pt = parameters[i].ParameterType;
                if (pt == typeof(Shader))
                {
                    argv[i] = shader;
                }
                else if (pt == typeof(bool))
                {
                    argv[i] = false;
                }
                else if (pt == typeof(int))
                {
                    argv[i] = 0;
                }
                else
                {
                    argv[i] = null;
                }
            }

            return argv;
        }

        private static Exception Unwrap(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null)
            {
                e = e.InnerException;
            }

            return e;
        }
    }
}
