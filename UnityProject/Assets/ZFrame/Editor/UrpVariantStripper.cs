using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ZFrame.Editor
{
    /// <summary>
    /// TASK-A-R9-16 的 IPreprocessShaders 适配层（薄壳）。
    ///
    /// 判定逻辑全部委托给纯函数 UrpVariantRules.Evaluate（有 EditMode 单测覆盖），
    /// 本类只负责：读默认关闭开关、把 ShaderCompilerData 转成关键字名列表、
    /// 按判定删除变体、记录计数。
    ///
    /// ===== 默认关闭 / 白名单保护（防误伤）=====
    ///  * 总开关 EditorPrefs "ZFrame.UrpVariantStripper.Enabled"，默认 false。
    ///    未显式置 true 时 OnProcessShader 直接 return，一个变体都不删 —— 因此
    ///    默认状态下不可能出现「粉红材质 / 后处理消失」。
    ///  * 逐条开关 EditorPrefs "ZFrame.UrpVariantStripper.DisabledRules"
    ///    （逗号分隔的 keyword），可单独停用某条规则而不改代码。
    ///  * 白名单：UrpVariantRules.ProtectedKeywords（Volume / UberPost 必需）优先
    ///    于剥离规则；三档 URP 必需的光照关键字根本不在规则表内，天然保留。
    ///  * 不改任何资产：不写 URP_*.asset / QualitySettings.asset /
    ///    GraphicsSettings.asset / 材质 / 场景；不改原生 C++。
    ///
    /// ===== 误伤判据与回滚 =====
    ///  误伤判据：开启剥离后做一次完整构建，若运行期出现粉红/洋红材质、
    ///  Bloom 或 ColorAdjustments 后处理消失、阴影/额外光异常，即判定误伤。
    ///  回滚：菜单 ZFrame/URP 变体/关闭剥离（或把上述 EditorPrefs 置 false /
    ///  删除该键）后重新构建即可；因默认不改资产、剥离只在构建期内存发生，
    ///  回滚零残留、无需 git checkout 任何文件。
    ///
    /// ===== 已知受限 =====
    ///  IPreprocessShaders 只在真实 Player / AssetBundle 构建时回调；
    ///  -batchmode 无构建时不会触发，故本适配层的「实际删除行为」在本轮
    ///  batchmode 环境不可验证（已写入施工报告「未验证」项）。
    /// </summary>
    public sealed class UrpVariantStripper : IPreprocessShaders
    {
        /// <summary>总开关 key；不存在时默认 false。</summary>
        public const string EnabledPrefKey = "ZFrame.UrpVariantStripper.Enabled";

        /// <summary>被逐条停用的规则 keyword，逗号分隔。</summary>
        public const string DisabledRulesPrefKey = "ZFrame.UrpVariantStripper.DisabledRules";

        /// <summary>是否逐条打印被剥的组合（默认关，避免刷屏）。</summary>
        public const string VerbosePrefKey = "ZFrame.UrpVariantStripper.Verbose";

        /// <summary>
        /// callbackOrder 取正值，排在 URP 自带的
        /// UpdateShaderPrefilteringDataBeforeBuild(-100) 之后；URP 的
        /// ShaderScriptableStripper 走 IShaderVariantStripper（另一条管线），
        /// 与本接口不冲突。
        /// </summary>
        public int callbackOrder => 10;

        private static long s_evaluated;
        private static long s_stripped;

        /// <summary>累计评估的 compilerData 条数（构建期内有效）。</summary>
        public static long EvaluatedCount => s_evaluated;

        /// <summary>累计被删除的 compilerData 条数（构建期内有效）。</summary>
        public static long StrippedCount => s_stripped;

        /// <summary>是否显式启用了剥离（默认关闭）。</summary>
        public static bool IsEnabled => EditorPrefs.GetBool(EnabledPrefKey, false);

        /// <summary>供菜单 / -executeMethod 调用，写显式开关。</summary>
        public static void SetEnabled(bool enabled)
        {
            EditorPrefs.SetBool(EnabledPrefKey, enabled);
            Debug.Log("[UrpVariantStripper] Enabled = " + enabled
                      + " (key=" + EnabledPrefKey + ")");
        }

        /// <summary>解析逐条停用的规则集合（总是返回非 null）。</summary>
        public static HashSet<string> ReadDisabledRules()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            string raw = EditorPrefs.GetString(DisabledRulesPrefKey, string.Empty);
            if (string.IsNullOrEmpty(raw))
            {
                return set;
            }

            foreach (string part in raw.Split(','))
            {
                string keyword = part.Trim();
                if (keyword.Length > 0)
                {
                    set.Add(keyword);
                }
            }

            return set;
        }

        /// <summary>
        /// 关心的关键字全集 = 剥离规则 + 白名单保护。只用这些名字去问
        /// shader.keywordSpace，避免构造不存在的 LocalKeyword。
        /// </summary>
        private static string[] BuildConcernedKeywords()
        {
            var names = new List<string>();
            foreach (UrpVariantRules.StripRule rule in UrpVariantRules.StripRules)
            {
                names.Add(rule.Keyword);
            }

            names.AddRange(UrpVariantRules.ProtectedKeywords);
            return names.ToArray();
        }

        public void OnProcessShader(
            Shader shader,
            ShaderSnippetData snippetData,
            IList<ShaderCompilerData> compilerDataList)
        {
            // 默认关闭：未显式启用时一个变体都不删。
            if (!IsEnabled)
            {
                return;
            }

            if (compilerDataList == null || compilerDataList.Count == 0)
            {
                return;
            }

            string platform = EditorUserBuildSettings.activeBuildTarget.ToString();
            bool isDevelopmentBuild = EditorUserBuildSettings.development;
            HashSet<string> disabledRules = ReadDisabledRules();
            bool verbose = EditorPrefs.GetBool(VerbosePrefKey, false);
            string[] concerned = BuildConcernedKeywords();

            // 预解析本 shader 上真实存在的 LocalKeyword（照抄 URP 14
            // ShaderScriptableStripper.InitializeLocalShaderKeywords 的用法）。
            var localKeywords = new LocalKeyword[concerned.Length];
            if (shader != null)
            {
                for (int i = 0; i < concerned.Length; i++)
                {
                    localKeywords[i] = shader.keywordSpace.FindKeyword(concerned[i]);
                }
            }

            var enabledKeywords = new List<string>(concerned.Length);
            for (int variantIndex = compilerDataList.Count - 1; variantIndex >= 0; variantIndex--)
            {
                ShaderCompilerData data = compilerDataList[variantIndex];
                enabledKeywords.Clear();

                for (int i = 0; i < concerned.Length; i++)
                {
                    if (localKeywords[i].isValid && data.shaderKeywordSet.IsEnabled(localKeywords[i]))
                    {
                        enabledKeywords.Add(concerned[i]);
                    }
                }

                UrpVariantRules.Decision decision = UrpVariantRules.Evaluate(
                    shader != null ? shader.name : string.Empty,
                    enabledKeywords,
                    platform,
                    isDevelopmentBuild,
                    null,
                    disabledRules);

                s_evaluated++;
                if (!decision.Strip)
                {
                    continue;
                }

                s_stripped++;
                compilerDataList.RemoveAt(variantIndex);

                if (verbose)
                {
                    var sb = new StringBuilder();
                    sb.Append("[UrpVariantStripper] strip shader=")
                      .Append(shader != null ? shader.name : "<null>")
                      .Append(" pass=").Append(snippetData.passName)
                      .Append(" keywords=[").Append(string.Join(",", enabledKeywords))
                      .Append("] reason=").Append(decision.Reason);
                    Debug.Log(sb.ToString());
                }
            }
        }

        // ------------------------------------------------------------------
        // 菜单项：显式开关（不写任何资产，只写 EditorPrefs）
        // ------------------------------------------------------------------
        [MenuItem("ZFrame/URP 变体/启用剥离（谨慎）", false, 1)]
        private static void MenuEnable()
        {
            SetEnabled(true);
        }

        [MenuItem("ZFrame/URP 变体/启用剥离（谨慎）", true, 1)]
        private static bool MenuEnableValidate()
        {
            return !IsEnabled;
        }

        [MenuItem("ZFrame/URP 变体/关闭剥离（回滚）", false, 2)]
        private static void MenuDisable()
        {
            SetEnabled(false);
        }

        [MenuItem("ZFrame/URP 变体/关闭剥离（回滚）", true, 2)]
        private static bool MenuDisableValidate()
        {
            return IsEnabled;
        }

        [MenuItem("ZFrame/URP 变体/打印当前配置", false, 3)]
        private static void MenuPrintConfig()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[UrpVariantStripper] === 当前配置 ===");
            sb.AppendLine("Enabled           = " + IsEnabled + "  (key=" + EnabledPrefKey + ")");
            sb.AppendLine("DisabledRules     = [" + EditorPrefs.GetString(DisabledRulesPrefKey, string.Empty) + "]");
            sb.AppendLine("Verbose           = " + EditorPrefs.GetBool(VerbosePrefKey, false));
            sb.AppendLine("StripRules        = " + UrpVariantRules.StripRules.Length + " 条");
            sb.AppendLine("ProtectedKeywords = " + UrpVariantRules.ProtectedKeywords.Length + " 个");
            sb.AppendLine("Evaluated/Stripped= " + s_evaluated + "/" + s_stripped + " (构建期内)");
            Debug.Log(sb.ToString());
        }

        /// <summary>
        /// 命令行入口：设置开关。示例
        /// -executeMethod ZFrame.Editor.UrpVariantStripper.SetEnabledFromCommandLine
        /// 参数通过 -urpStripEnabled true/false 传入（缺失时打印当前值，不改）。
        /// </summary>
        public static void SetEnabledFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-urpStripEnabled", StringComparison.OrdinalIgnoreCase))
                {
                    bool value;
                    if (bool.TryParse(args[i + 1], out value))
                    {
                        SetEnabled(value);
                    }
                    else
                    {
                        Debug.LogWarning("[UrpVariantStripper] -urpStripEnabled 需 true/false，收到: " + args[i + 1]);
                    }

                    return;
                }
            }

            Debug.Log("[UrpVariantStripper] 当前 Enabled = " + IsEnabled
                      + "（未传 -urpStripEnabled，未改动）");
        }
    }
}
