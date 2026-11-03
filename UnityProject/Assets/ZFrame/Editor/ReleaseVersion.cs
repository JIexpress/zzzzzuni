// =============================================================================
// ReleaseVersion.cs —— 发布版本单源（Tools/version.json）的纯逻辑（TASK-A-AND-07）
//
// 只做四件事：解析 / 单调性守卫 / 自增 / 序列化落盘。**不碰 Unity API**，
// 因此能被 EditMode 单测直接驱动；Unity 侧的注入（PlayerSettings）留在
// Game/Editor/PlayerBuild.cs。
//
// schema：{ "version": "0.1.0", "build": N, "versionCode": M }
//   build       —— 已成功构建过的 Player 构建号。本次构建的 versionName =
//                  "<version>.<build+1>"，构建成功后写回 build+1（TASK-B-R9-15 语义）。
//   versionCode —— 下一次构建要用的 Android versionCode，**独立字段，不从 build 派生**。
//                  构建成功后写回 +1。
//
// 单调性守卫（TASK-A-AND-07 决策一）：versionCode 必须 >= MinVersionCode(2)。
//   背景：build 曾被重置（18→19）造成 versionName 倒挂 0.1.0.19 < 旧包 0.1.0.22。
//   versionCode 一旦倒退，Android 会拒绝安装 versionName 更高的包，且商店端不可逆
//   （已上传的 versionCode 收不回来）。所以宁可在构建前中止，也不静默继续。
// =============================================================================
using System;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace ZFrame.Editor
{
    /// <summary>发布版本三元组（version / build / versionCode）的解析、守卫与自增。</summary>
    public sealed class ReleaseVersion
    {
        /// <summary>versionCode 下限。已发布的包用过 1，下一个合法值是 2。</summary>
        public const int MinVersionCode = 2;

        /// <summary>JSON 传输形态（字段名即 version.json 的键名）。</summary>
        private sealed class JsonShape
        {
            [JsonProperty("version")] public string Version { get; set; }
            [JsonProperty("build")] public int Build { get; set; }
            [JsonProperty("versionCode")] public int VersionCode { get; set; }
        }

        public ReleaseVersion(string version, int build, int versionCode)
        {
            Version = version;
            Build = build;
            VersionCode = versionCode;
        }

        /// <summary>版本主段（"0.1.0"）。</summary>
        public string Version { get; }

        /// <summary>已成功构建过的构建号（落盘值 = 上一个已消耗的号）。</summary>
        public int Build { get; }

        /// <summary>下一次构建要用的 Android versionCode。</summary>
        public int VersionCode { get; }

        /// <summary>本次构建注入的 versionName = "&lt;version&gt;.&lt;build+1&gt;"。</summary>
        public string BundleVersion => Version + "." + (Build + 1).ToString(CultureInfo.InvariantCulture);

        /// <summary>构建成功后要落盘的值：build 与 versionCode 各自 +1（互不派生）。</summary>
        public ReleaseVersion Advance()
        {
            return new ReleaseVersion(Version, Build + 1, VersionCode + 1);
        }

        /// <summary>解析 version.json 文本。结构不合法抛 <see cref="FormatException"/>；
        /// versionCode 的单调性由 <see cref="ValidateForBuild"/> 负责。</summary>
        public static ReleaseVersion Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new FormatException("version source is empty");
            }
            JsonShape shape;
            try
            {
                shape = JsonConvert.DeserializeObject<JsonShape>(json);
            }
            catch (Exception ex)
            {
                throw new FormatException("version source is not valid JSON: " + ex.Message, ex);
            }
            if (shape == null || string.IsNullOrWhiteSpace(shape.Version))
            {
                throw new FormatException("version source malformed: \"version\" is required");
            }
            if (shape.Build < 0)
            {
                throw new FormatException("version source malformed: \"build\" must be >= 0");
            }
            return new ReleaseVersion(shape.Version.Trim(), shape.Build, shape.VersionCode);
        }

        /// <summary>读文件并解析。</summary>
        public static ReleaseVersion Load(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("version source missing: " + path, path);
            }
            return Parse(File.ReadAllText(path));
        }

        /// <summary>构建前守卫：versionCode 必须单调递增。不合规抛
        /// <see cref="InvalidOperationException"/>，调用方据此中止构建。</summary>
        public void ValidateForBuild()
        {
            EnsureMonotonic(VersionCode);
        }

        /// <summary>单调性断言：versionCode &lt; <see cref="MinVersionCode"/>（含缺字段时的 0）一律拒绝。</summary>
        public static void EnsureMonotonic(int versionCode)
        {
            if (versionCode < MinVersionCode)
            {
                throw new InvalidOperationException(
                    "versionCode=" + versionCode.ToString(CultureInfo.InvariantCulture)
                    + " rejected: must be >= " + MinVersionCode.ToString(CultureInfo.InvariantCulture)
                    + " (the published packages already consumed 1). versionCode must increase strictly -"
                    + " a regression makes Android refuse the next package and is irreversible on the store."
                    + " Fix Tools/version.json instead of building.");
            }
        }

        /// <summary>序列化为 version.json 文本（2 空格缩进 + 结尾换行，与既有格式一致）。</summary>
        public string ToJson()
        {
            var sb = new StringBuilder(128);
            sb.Append("{\n  \"version\": \"").Append(Version).Append("\",\n");
            sb.Append("  \"build\": ").Append(Build.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"versionCode\": ").Append(VersionCode.ToString(CultureInfo.InvariantCulture)).Append("\n}\n");
            return sb.ToString();
        }

        /// <summary>落盘：UTF-8 无 BOM + LF（file-conventions.md）。</summary>
        public void Save(string path)
        {
            File.WriteAllText(path, ToJson(), new UTF8Encoding(false));
        }
    }
}
