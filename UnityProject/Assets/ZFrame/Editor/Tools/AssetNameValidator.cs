using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ZFrame.Editor
{
    /// <summary>
    /// 资产命名规范检查器（只读不改——发现违规只出清单，绝不自动重命名，
    /// 重命名涉及 .meta guid 联动）。按 Docs/Standards/file-conventions.md §5
    /// 前缀表扫描 Assets/ 主要目录（排除 Plugins/ThirdParty 等第三方与包资产）。
    /// 规则判定在 <see cref="AssetNamingRules"/>（纯逻辑，可单测）。
    /// 窗口菜单：ZFrame/Tools/资产命名检查。批处理：
    ///   -executeMethod ZFrame.Editor.AssetNameValidator.ValidateFromCommandLine
    /// 写 TempLogs/name_report.txt，违规 exit 1。
    /// </summary>
    public static class AssetNameValidator
    {
        /// <summary>扫描时整树跳过的目录名。</summary>
        private static readonly string[] SkipDirectoryNames =
        {
            "Plugins", "ThirdParty", "Editor",
        };

        /// <summary>违规清单 of the last run; null before first run.</summary>
        public static IReadOnlyList<string> LastViolations { get; private set; }

        /// <summary>扫描 Assets/（只读），返回违规行清单。</summary>
        public static List<string> ValidateAll()
        {
            var violations = new List<string>();
            foreach (var file in Directory.GetFiles("Assets", "*", SearchOption.AllDirectories))
            {
                var relPath = MakeRelative(file);
                var segments = relPath.Split('/');
                var skip = false;
                foreach (var segment in segments)
                {
                    if (SkipDirectoryNames.Contains(segment))
                    {
                        skip = true;
                        break;
                    }
                }

                if (skip)
                {
                    continue; // 第三方/编辑器目录整树跳过（路径任一段命中即跳过）
                }

                var extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension == ".meta" || extension == ".cs" || extension == ".asmdef")
                {
                    continue;
                }

                var (result, reason) = AssetNamingRules.Check(relPath);
                if (result == NamingCheckResult.Violation)
                {
                    violations.Add($"{relPath}: {reason}");
                }
            }

            LastViolations = violations;
            return violations;
        }

        /// <summary>Batch entry: scan, write report, exit 1 on violations.</summary>
        public static void ValidateFromCommandLine()
        {
            var violations = ValidateAll();
            var report = new StringBuilder();
            report.AppendLine($"ZFrame 资产命名检查报告 @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"扫描范围: Assets/（跳过 Plugins/ThirdParty/Editor 目录与 .meta/.cs/.asmdef）");
            report.AppendLine($"违规数: {violations.Count}");
            report.AppendLine(violations.Count == 0 ? "结果: PASS" : "结果: FAIL");
            report.AppendLine(new string('-', 60));
            foreach (var violation in violations)
            {
                report.AppendLine(violation);
            }

            var reportPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "TempLogs",
                "name_report.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
            Debug.Log($"[AssetNameValidator] report written to {reportPath}\n{report}");
            EditorApplication.Exit(violations.Count == 0 ? 0 : 1);
        }

        /// <summary>Menu: violations list window.</summary>
        [MenuItem("ZFrame/Tools/资产命名检查")]
        private static void OpenWindow()
        {
            EditorWindow.GetWindow<AssetNameValidatorWindow>("ZFrame 命名检查");
        }

        private sealed class AssetNameValidatorWindow : EditorWindow
        {
            private Vector2 _scroll;
            private List<string> _violations;

            private void OnGUI()
            {
                if (GUILayout.Button("扫描 Assets/"))
                {
                    _violations = ValidateAll();
                }

                if (_violations == null)
                {
                    EditorGUILayout.HelpBox(
                        "尚未扫描。批处理:\n-executeMethod ZFrame.Editor.AssetNameValidator.ValidateFromCommandLine\n（违规时 exit 1）\n\n本工具只读：发现违规仅列清单，不自动重命名。",
                        MessageType.Info);
                    return;
                }

                EditorGUILayout.HelpBox(
                    _violations.Count == 0 ? "PASS: 无违规" : $"FAIL: {_violations.Count} 处违规",
                    _violations.Count == 0 ? MessageType.Info : MessageType.Error);
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                foreach (var violation in _violations)
                {
                    EditorGUILayout.LabelField(violation);
                }

                EditorGUILayout.EndScrollView();
            }
        }

        private static string MakeRelative(string path)
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath("Assets");
            return full.StartsWith(root, System.StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length + 1)
                : path;
        }
    }
}
