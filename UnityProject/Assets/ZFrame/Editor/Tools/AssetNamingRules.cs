using System;
using System.IO;
using System.Linq;

namespace ZFrame.Editor
{
    /// <summary>命名检查结果分类。</summary>
    public enum NamingCheckResult
    {
        /// <summary>符合前缀表（或不适用命名规则）。</summary>
        Ok,

        /// <summary>不在前缀表内但命中白名单（存量约定/基建/测试资产）。</summary>
        Whitelisted,

        /// <summary>违反命名规范。</summary>
        Violation,
    }

    /// <summary>
    /// 资产命名规则的纯判定逻辑（无 UnityEditor 依赖，供单测直接覆盖）。
    /// 前缀表来源：Docs/Standards/file-conventions.md §5 + A-M5-01 简报补充 MIX_。
    /// 白名单（存量决策，见 TASK-A-M5-01 施工报告）：
    ///  - Assets/UI/Resources/ui_* —— M3-01 资源键约定（prefabKey "ui_xxx"），改名会破坏运行时加载；
    ///  - Assets/Settings/Pipeline/ —— URP 管线资产 / Volume 档位 / 渲染管线资产（框架基建）；
    ///  - 根目录 UniversalRenderPipelineGlobalSettings.asset —— URP 包自动生成；
    ///  - Assets/ZFrame/Tests/ —— 测试资产（TestCube.prefab / TestAsset.asset）。
    /// </summary>
    public static class AssetNamingRules
    {
        /// <summary>前缀表：前缀 → 用途（与规范 §5 一致）。</summary>
        public static readonly string[] Prefixes =
        {
            "CHR_", "ENV_", "WPN_", "UI_", "CFG_", "MAT_", "TX_", "AUD_", "SCN_", "MIX_",
        };

        /// <summary>需要按前缀表检查的资产扩展名（小写，含点）。</summary>
        public static readonly string[] ScannedExtensions =
        {
            ".prefab", ".asset", ".mat", ".unity", ".fbx", ".png", ".tga", ".jpg",
            ".wav", ".mp3", ".ogg", ".controller", ".anim", ".overrideController",
        };

        /// <summary>整目录白名单（相对 Assets 的目录前缀）。Settings/ 为 M2 渲染管线基建
        /// （URP 档位 / Volume / 渲染管线资产），整目录豁免。</summary>
        public static readonly string[] WhitelistedDirectories =
        {
            "Settings",
            "ZFrame/Tests",
        };

        /// <summary>整文件名白名单（根目录基建资产）。</summary>
        public static readonly string[] WhitelistedFileNames =
        {
            "UniversalRenderPipelineGlobalSettings.asset",
        };

        /// <summary>
        /// 判定一个资产相对路径（相对 Assets/，使用 '/' 分隔）。不改变任何文件。
        /// </summary>
        public static (NamingCheckResult Result, string Reason) Check(string assetRelativePath)
        {
            var path = assetRelativePath.Replace('\\', '/');
            var fileName = path.Substring(path.LastIndexOf('/') + 1);
            var dir = path.Length > fileName.Length
                ? path.Substring(0, path.Length - fileName.Length - 1)
                : string.Empty;
            var extension = Path.GetExtension(fileName).ToLowerInvariant();

            if (!ScannedExtensions.Contains(extension))
            {
                return (NamingCheckResult.Ok, $"扩展名 {extension} 不适用命名规则");
            }

            // 前缀表判定优先于目录白名单（规范命名的资产在任何目录都算合规）
            var stem = fileName.Substring(0, fileName.Length - extension.Length);
            foreach (var prefix in Prefixes)
            {
                if (stem.StartsWith(prefix, StringComparison.Ordinal) &&
                    stem.Length > prefix.Length &&
                    stem.Substring(prefix.Length).Trim().Length > 0)
                {
                    return (NamingCheckResult.Ok, $"命中前缀 {prefix}");
                }
            }

            if (WhitelistedFileNames.Contains(fileName) && dir.Length == 0)
            {
                return (NamingCheckResult.Whitelisted, "根目录基建资产（URP 包生成）");
            }

            foreach (var whitelistedDir in WhitelistedDirectories)
            {
                if (dir == whitelistedDir || dir.StartsWith(whitelistedDir + "/", StringComparison.Ordinal))
                {
                    return (NamingCheckResult.Whitelisted, $"目录 '{whitelistedDir}' 在白名单内");
                }
            }

            if (dir == "UI/Resources" && fileName.StartsWith("ui_", StringComparison.Ordinal))
            {
                return (NamingCheckResult.Whitelisted, "M3-01 资源键约定（ui_* 由 ResourcesProvider 加载）");
            }

            return (NamingCheckResult.Violation,
                $"无合法前缀（期望其一: {string.Join("/", Prefixes)}）");
        }
    }
}
