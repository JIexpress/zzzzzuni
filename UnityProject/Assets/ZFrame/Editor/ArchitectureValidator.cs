using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ZFrame.Editor
{
    /// <summary>
    /// Architecture guard rails for Track A. Two checks:
    ///  1. asmdef matrix — every .asmdef under Assets/ must be known and its
    ///     reference list must be a subset of the allowed list (设计方案 §3.2).
    ///  2. Logic-layer purity — Gameplay/Domain .cs sources must not use
    ///     scene/engine-object APIs (token blacklist, comments ignored; math
    ///     types like Vector3/Mathf are fine because they are not listed).
    /// Window: ZFrame/架构校验. Batch:
    ///   -executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine
    /// writes TempLogs/arch_report.txt (repo root) and exits 1 on violations.
    /// </summary>
    public static class ArchitectureValidator
    {
        private sealed class AsmdefRule
        {
            public string[] AllowedReferences;
            public bool EditorOnly;
        }

        private static readonly Dictionary<string, AsmdefRule> ExpectedMatrix =
            new Dictionary<string, AsmdefRule>
            {
                ["ZFrame.Core"] = new AsmdefRule { AllowedReferences = new string[0] },
                ["ZFrame.Data"] = new AsmdefRule { AllowedReferences = new[] { "ZFrame.Core" } },
                // TASK-A-R8-02：Addressables 第二资源后端。框架内引用仅 ZFrame.Data
                // （不依赖 ZFrame.Core——经 IAssetProvider 反接）；Unity.Addressables /
                // Unity.ResourceManager 是本程序集唯一允许的官方包运行时程序集。
                ["ZFrame.Data.Addressables"] = new AsmdefRule
                {
                    AllowedReferences = new[] { "ZFrame.Data", "Unity.Addressables", "Unity.ResourceManager" },
                },
                ["ZFrame.Gameplay"] = new AsmdefRule { AllowedReferences = new[] { "ZFrame.Core" } },
                ["ZFrame.View"] = new AsmdefRule
                {
                    // Unity.InputSystem 为 View 层唯一允许的 Unity 官方包引用
                    // （TASK-A-M3-03 输入重绑定；Gameplay/Domain 仍禁引擎依赖）。
                    AllowedReferences = new[] { "ZFrame.Core", "ZFrame.Data", "ZFrame.Gameplay", "Unity.InputSystem" },
                },
                ["ZFrame.Editor"] = new AsmdefRule
                {
                    AllowedReferences = new[] { "ZFrame.Core", "ZFrame.Data", "ZFrame.Gameplay", "ZFrame.View" },
                    EditorOnly = true,
                },
                // TASK-A-DEV-01：Editor 常驻 Dev Server（TcpListener 手写 HTTP/1.1，只监听 127.0.0.1）。
                // 额外依赖仅 TestRunner 的两个官方程序集（/test 端点用 TestRunnerApi + ICallbacks）。
                ["ZFrame.Editor.DevServer"] = new AsmdefRule
                {
                    AllowedReferences = new[] { "UnityEngine.TestRunner", "UnityEditor.TestRunner" },
                    EditorOnly = true,
                },
                ["ZFrame.Render.Bindings"] = new AsmdefRule { AllowedReferences = new string[0] },
                ["ZFrame.Render"] = new AsmdefRule
                {
                    AllowedReferences = new[] { "ZFrame.Core", "ZFrame.Render.Bindings" },
                },
                // NOTE: 当尚不存在的程序集（如 ZFrame.Render.Editor / Game.Editor）
                // 落地时，必须同步在此处补条目 —— 单测会校验矩阵条目数与实际 asmdef 数一致。
                ["Game.Domain"] = new AsmdefRule
                {
                    AllowedReferences = new[] { "ZFrame.Core", "ZFrame.Data", "ZFrame.Gameplay" },
                },
                ["Game.View"] = new AsmdefRule
                {
                    AllowedReferences = new[]
                    {
                        "ZFrame.Core", "ZFrame.Data", "ZFrame.Gameplay", "ZFrame.View", "Game.Domain",
                    },
                },
                ["Game.Bootstrap"] = new AsmdefRule
                {
                    AllowedReferences = new[]
                    {
                        "ZFrame.Core", "ZFrame.Data", "ZFrame.Gameplay", "ZFrame.View",
                        "Game.Domain", "Game.View",
                    },
                },
            };

        /// <summary>Directories whose .cs sources are scanned for engine-API purity.</summary>
        private static readonly string[] PurityScanDirs =
        {
            "Assets/ZFrame/Gameplay",
            "Assets/Game/Domain",
        };

        // Word-boundary blacklist. Math types (Vector2/3/4, Quaternion,
        // Mathf, Color, ...) are intentionally absent = allowed.
        private static readonly Regex[] ForbiddenPatterns =
        {
            new Regex(@"\bGameObject\b", RegexOptions.Compiled),
            new Regex(@"\bMonoBehaviour\b", RegexOptions.Compiled),
            new Regex(@"\bComponent\b", RegexOptions.Compiled),
            new Regex(@"\bCamera\b", RegexOptions.Compiled),
            new Regex(@"\bTransform\b", RegexOptions.Compiled),
            new Regex(@"\bFindObjectOfType\b", RegexOptions.Compiled),
            new Regex(@"\bObject\s*\.\s*Find", RegexOptions.Compiled),
            new Regex(@"UnityEngine\s*\.\s*SceneManagement", RegexOptions.Compiled),
        };

        private static readonly string[] ForbiddenNames =
        {
            "GameObject", "MonoBehaviour", "Component", "Camera", "Transform",
            "FindObjectOfType", "Object.Find", "UnityEngine.SceneManagement",
        };

        /// <summary>asmdef reference entries that never count as violations.</summary>
        private static readonly string[] IgnoredReferencePrefixes =
        {
            "GUID:", // GUID-based references cannot be resolved cheaply; name-form is our convention
        };

        /// <summary>Report of the last ValidateAll run (null before first run).</summary>
        public static string LastReport { get; private set; }

        /// <summary>Violations of the last ValidateAll run; empty/null before first run.</summary>
        public static IReadOnlyList<string> LastViolations { get; private set; }

        /// <summary>Number of asmdefs the expected matrix currently knows.</summary>
        public static int ExpectedMatrixCount => ExpectedMatrix.Count;

        /// <summary>Runs both checks. Returns true when there are zero violations.</summary>
        public static bool ValidateAll()
        {
            var violations = new List<string>();
            var asmdefFiles = Directory.GetFiles("Assets", "*.asmdef", SearchOption.AllDirectories);

            var knownAsmdefs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in asmdefFiles)
            {
                violations.AddRange(ValidateAsmdef(path, knownAsmdefs));
            }

            violations.AddRange(ValidatePurity());

            LastViolations = violations;
            LastReport = BuildReport(asmdefFiles.Length, violations);
            return violations.Count == 0;
        }

        private static IEnumerable<string> ValidateAsmdef(string path, HashSet<string> knownAsmdefs)
        {
            var json = File.ReadAllText(path);
            var name = ExtractJsonString(json, "name");
            var relPath = MakeRelative(path);

            if (name.EndsWith(".Tests", StringComparison.Ordinal) ||
                name.Contains(".Tests."))
            {
                // Test assemblies follow "被测层" and are exempt from the matrix.
                knownAsmdefs.Add(name);
                yield break;
            }

            if (!ExpectedMatrix.TryGetValue(name, out var rule))
            {
                yield return $"[asmdef] {relPath}: 程序集 '{name}' 不在期望矩阵中（新增程序集需先更新 ArchitectureValidator 期望矩阵）";
                yield break;
            }

            knownAsmdefs.Add(name);

            foreach (var rawRef in ExtractJsonStringArray(json, "references"))
            {
                if (IgnoredReferencePrefixes.Any(p => rawRef.StartsWith(p, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (!rule.AllowedReferences.Contains(rawRef))
                {
                    yield return
                        $"[asmdef] {relPath}: 非法引用 '{rawRef}'（允许: {string.Join(", ", rule.AllowedReferences)}）";
                }
            }

            if (rule.EditorOnly)
            {
                var platforms = ExtractJsonStringArray(json, "includePlatforms");
                if (!platforms.Contains("Editor"))
                {
                    yield return $"[asmdef] {relPath}: '{name}' 应为 Editor-only（includePlatforms 缺少 Editor）";
                }
            }
        }

        private static IEnumerable<string> ValidatePurity()
        {
            foreach (var dir in PurityScanDirs)
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    var relPath = MakeRelative(file);
                    var lines = File.ReadAllLines(file);
                    var inBlockComment = false;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var code = StripComments(lines[i], ref inBlockComment);
                        for (int p = 0; p < ForbiddenPatterns.Length; p++)
                        {
                            if (ForbiddenPatterns[p].IsMatch(code))
                            {
                                yield return $"[purity] {relPath}:{i + 1}: 禁用 token '{ForbiddenNames[p]}' 出现在逻辑层";
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Removes // line comments and tracks /* */ block comments across lines.</summary>
        private static string StripComments(string line, ref bool inBlockComment)
        {
            var sb = new StringBuilder(line.Length);
            for (int i = 0; i < line.Length; i++)
            {
                if (inBlockComment)
                {
                    if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '/')
                    {
                        inBlockComment = false;
                        i++;
                    }

                    continue;
                }

                if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    break;
                }

                if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    inBlockComment = true;
                    i++;
                    continue;
                }

                sb.Append(line[i]);
            }

            return sb.ToString();
        }

        private static string BuildReport(int asmdefCount, List<string> violations)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"ZFrame 架构校验报告 @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"asmdef 扫描数: {asmdefCount}（期望矩阵条目: {ExpectedMatrix.Count}，Tests 程序集豁免）");
            sb.AppendLine($"纯度扫描目录: {string.Join("; ", PurityScanDirs)}");
            sb.AppendLine($"违规数: {violations.Count}");
            sb.AppendLine(violations.Count == 0 ? "结果: PASS" : "结果: FAIL");
            sb.AppendLine(new string('-', 60));
            foreach (var violation in violations)
            {
                sb.AppendLine(violation);
            }

            return sb.ToString();
        }

        private static string MakeRelative(string path)
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath("Assets");
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length + 1)
                : path;
        }

        private static string ExtractJsonString(string json, string key)
        {
            var match = Regex.Match(json, $"\"{key}\"\\s*:\\s*\"([^\"]*)\"");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static string[] ExtractJsonStringArray(string json, string key)
        {
            var match = Regex.Match(json, $"\"{key}\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            if (!match.Success)
            {
                return new string[0];
            }

            return Regex.Matches(match.Groups[1].Value, "\"([^\"]*)\"")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .ToArray();
        }

        /// <summary>Batch-mode entry: validate, write report, exit 1 on violations.</summary>
        public static void ValidateFromCommandLine()
        {
            var ok = ValidateAll();
            var reportPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "TempLogs", "arch_report.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
            File.WriteAllText(reportPath, LastReport, new UTF8Encoding(false));
            Debug.Log($"[ArchitectureValidator] report written to {reportPath}\n{LastReport}");

            EditorApplication.Exit(ok ? 0 : 1);
        }

        private sealed class ValidatorWindow : EditorWindow
        {
            private Vector2 scroll;

            [MenuItem("ZFrame/架构校验")]
            private static void Open()
            {
                GetWindow<ValidatorWindow>("ZFrame 架构校验");
            }

            private void OnGUI()
            {
                if (GUILayout.Button("运行校验 (ValidateAll)"))
                {
                    ValidateAll();
                }

                GUILayout.Space(8f);
                if (LastReport == null)
                {
                    GUILayout.Label("尚未运行。点击上方按钮或使用批处理:\n-executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine");
                    return;
                }

                var ok = LastViolations.Count == 0;
                EditorGUILayout.HelpBox(ok ? "PASS — 0 违规" : $"FAIL — {LastViolations.Count} 违规", ok ? MessageType.Info : MessageType.Error);
                scroll = GUILayout.BeginScrollView(scroll);
                GUILayout.TextArea(LastReport, GUILayout.ExpandHeight(true));
                GUILayout.EndScrollView();
            }
        }
    }
}
