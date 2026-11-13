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
    /// 纹理域段模式来源：A-CONT-01 程序化环境套件（42f5a26）——T_ENV_Name.png，
    /// 见 <see cref="TextureDomains"/>（TASK-X-NAMES-01 扩规则，不 rename 存量资产）。
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

        /// <summary>纹理域段注册表：T_ 前缀纹理（T=Texture 大类前缀）按 "T_域段_Name" 分段判定，
        /// 域段必须在本表注册（大小写敏感，同前缀表；未注册域段不放宽，如 T_BOSS_ 判违规），
        /// Name 须为首字母大写标识符（首字符 A-Z，其余字母/数字/下划线）。
        /// 首个注册域段 ENV，依据：A-CONT-01 程序化环境套件（42f5a26）入库的
        /// Art/Textures/T_ENV_{Bark,Grass,Metal,Road,Rock,Wood}.png——约定模式
        /// T_ENV_Name.png，仅适用于 .png（套件产物均为 png）。</summary>
        public static readonly string[] TextureDomains =
        {
            "ENV",
        };

        /// <summary>纹理域段模式的 stem 前缀与适用扩展名（见 TextureDomains 注释）。</summary>
        private const string TextureStemPrefix = "T_";
        private const string TextureDomainExtension = ".png";

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

            // 纹理域段模式（A-CONT-01 程序化环境套件）：T_域段_Name.png。
            // 合法时同前缀表原则直接判 Ok（任何目录都合规）；非法时不在此直接判违规，
            // 先走白名单判定（存量白名单语义不变），最终仍未豁免才按域段违规报告。
            string textureDomainViolation = null;
            if (extension == TextureDomainExtension &&
                stem.StartsWith(TextureStemPrefix, StringComparison.Ordinal))
            {
                var (textureOk, textureReason) = CheckTextureDomainStem(stem);
                if (textureOk)
                {
                    return (NamingCheckResult.Ok, textureReason);
                }

                textureDomainViolation = textureReason;
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

            if (textureDomainViolation != null)
            {
                return (NamingCheckResult.Violation, textureDomainViolation);
            }

            return (NamingCheckResult.Violation,
                $"无合法前缀（期望其一: {string.Join("/", Prefixes)}）");
        }

        /// <summary>
        /// 判定 T_ 开头的纹理 stem（不含扩展名）是否符合 T_域段_Name 域段模式：
        /// 域段已注册（TextureDomains，大小写敏感）且 Name 为首字母大写标识符。
        /// 合规时 Ok=true 且 Reason 为命中说明；否则 Reason 为违规说明。
        /// </summary>
        private static (bool Ok, string Reason) CheckTextureDomainStem(string stem)
        {
            var body = stem.Substring(TextureStemPrefix.Length);
            var separator = body.IndexOf('_');
            if (separator <= 0)
            {
                return (false, $"纹理域段模式不合法（期望 T_域段_Name，如 T_ENV_Bark）: {stem}");
            }

            var domain = body.Substring(0, separator);
            var name = body.Substring(separator + 1);
            if (!TextureDomains.Contains(domain))
            {
                return (false,
                    $"纹理域段 '{domain}' 未注册（已注册: {string.Join("/", TextureDomains)}）");
            }

            if (!IsCapitalizedIdentifier(name))
            {
                return (false,
                    $"域段 {domain} 的纹理名 '{name}' 非首字母大写标识符（首字符 A-Z，仅字母/数字/下划线）");
            }

            return (true, $"命中纹理域段模式 T_{domain}_（A-CONT-01 程序化环境套件）");
        }

        /// <summary>首字母大写标识符：首字符 A-Z，其余字符为字母/数字/下划线（不含连字符/空格）。</summary>
        private static bool IsCapitalizedIdentifier(string value)
        {
            if (value.Length == 0 || value[0] < 'A' || value[0] > 'Z')
            {
                return false;
            }

            foreach (var c in value)
            {
                var valid = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                            (c >= '0' && c <= '9') || c == '_';
                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
