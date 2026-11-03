using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace ZFrame.Editor
{
    /// <summary>
    /// UI 面板校验器。扫描 Assets/UI 下的 prefab，检查（TASK-A-M5-01 简报）：
    ///  1. Root 带 Canvas 却缺 CanvasScaler —— 告警（面板 prefab 不带 Canvas 时
    ///     由 UIRoot 的分层 Canvas 承载，属正常形态，不告警）；
    ///  2. RectTransform 锚点不在预设集合（拉伸/角落/边中点/中心）—— 告警
    ///     （判定逻辑在 <see cref="UiAnchorRules"/>，纯逻辑可单测）；
    ///  3. 丢失引用：missing script 组件 —— 错误；组件上为空的场景引用字段 —— 告警。
    /// 清单窗口可点击定位；批处理：-executeMethod ZFrame.Editor.UiValidator.ValidateFromCommandLine
    /// 写 TempLogs/ui_report.txt，仅存在错误级（missing script）时 exit 1。
    /// </summary>
    public static class UiValidator
    {
        /// <summary>Finding severity.</summary>
        public enum Severity
        {
            /// <summary>提示（形态说明，不影响退出码）。</summary>
            Info,

            /// <summary>告警（建议修复，不影响退出码）。</summary>
            Warning,

            /// <summary>错误（影响退出码）。</summary>
            Error,
        }

        /// <summary>Single finding: prefab + node path + message.</summary>
        public sealed class Finding
        {
            public Severity Level;
            public string PrefabPath;
            public string NodePath;
            public string Message;
            public UnityEngine.Object Target;

            public override string ToString()
            {
                return $"[{Level}] {PrefabPath} :: {NodePath} : {Message}";
            }
        }

        /// <summary>Foldout setting for the report window.</summary>
        internal static bool ShowInfoFindings = false;

        /// <summary>Finding list of the last run; null before first run.</summary>
        public static IReadOnlyList<Finding> LastFindings { get; private set; }

        /// <summary>扫描 Assets/UI 全部 prefab（只读，不写回任何资产）。</summary>
        public static List<Finding> ValidateAll()
        {
            var findings = new List<Finding>();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/UI" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (root == null)
                {
                    continue;
                }

                CheckRootScaler(root, path, findings);
                CheckAnchors(root, path, findings);
                CheckReferences(root, path, findings);
            }

            LastFindings = findings;
            return findings;
        }

        private static void CheckRootScaler(GameObject root, string path, List<Finding> findings)
        {
            var canvas = root.GetComponent<Canvas>();
            if (canvas != null && root.GetComponent<CanvasScaler>() == null)
            {
                findings.Add(new Finding
                {
                    Level = Severity.Warning,
                    PrefabPath = path,
                    NodePath = root.name,
                    Message = "Root 带 Canvas 但缺 CanvasScaler",
                    Target = root,
                });
            }
        }

        private static void CheckAnchors(GameObject root, string path, List<Finding> findings)
        {
            foreach (var rect in root.GetComponentsInChildren<RectTransform>(true))
            {
                if (UiAnchorRules.IsPreset(rect.anchorMin, rect.anchorMax))
                {
                    continue;
                }

                findings.Add(new Finding
                {
                    Level = Severity.Warning,
                    PrefabPath = path,
                    NodePath = GetPath(root.transform, rect),
                    Message = $"锚点 ({rect.anchorMin}) ~ ({rect.anchorMax}) 不在预设集合（拉伸/角落/边中点/中心）",
                    Target = rect,
                });
            }
        }

        private static void CheckReferences(GameObject root, string path, List<Finding> findings)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                {
                    findings.Add(new Finding
                    {
                        Level = Severity.Error,
                        PrefabPath = path,
                        NodePath = root.name,
                        Message = "存在 missing script 组件",
                        Target = root,
                    });
                    continue;
                }

                // 跳过 uGUI 内建组件，聚焦自研组件的场景引用字段
                if (component.GetType().Namespace != null &&
                    component.GetType().Namespace.StartsWith("UnityEngine", StringComparison.Ordinal))
                {
                    continue;
                }

                var serialized = new SerializedObject(component);
                var iterator = serialized.GetIterator();
                while (iterator.NextVisible(true))
                {
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference)
                    {
                        continue;
                    }

                    // 只查扁平字段（自研组件的场景引用字段都是一层）
                    var fieldType = GetFieldType(component.GetType(), iterator.propertyPath);
                    var isSceneRef = fieldType != null &&
                        (typeof(Component).IsAssignableFrom(fieldType) || fieldType == typeof(GameObject));
                    if (!isSceneRef || iterator.objectReferenceValue != null)
                    {
                        continue;
                    }

                    findings.Add(new Finding
                    {
                        Level = Severity.Warning,
                        PrefabPath = path,
                        NodePath = GetPath(root.transform, component.transform),
                        Message = $"场景引用字段 '{iterator.propertyPath}' 为空（未赋值或引用丢失）",
                        Target = component,
                    });
                }
            }
        }

        private static string GetPath(Transform root, Transform node)
        {
            var sb = new StringBuilder(node.name);
            var current = node.parent;
            while (current != null && current != root)
            {
                sb.Insert(0, current.name + "/");
                current = current.parent;
            }

            return sb.ToString();
        }

        /// <summary>按属性路径取序列化字段的声明类型（沿类型链向上找）。</summary>
        private static Type GetFieldType(Type componentType, string propertyPath)
        {
            for (var t = componentType; t != null && t != typeof(object); t = t.BaseType)
            {
                var field = t.GetField(propertyPath,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    return field.FieldType;
                }
            }

            return null;
        }

        /// <summary>Batch entry: validate, write report, exit 1 only on error-level findings.</summary>
        public static void ValidateFromCommandLine()
        {
            var findings = ValidateAll();
            var errors = 0;
            var warnings = 0;
            var report = new StringBuilder();
            report.AppendLine($"ZFrame UI 面板校验报告 @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine("退出码策略: 仅错误级（missing script）导致 exit 1；告警级只记录不阻断");
            report.AppendLine(new string('-', 60));
            foreach (var finding in findings)
            {
                report.AppendLine(finding.ToString());
                switch (finding.Level)
                {
                    case Severity.Error: errors++; break;
                    case Severity.Warning: warnings++; break;
                }
            }

            report.AppendLine(new string('-', 60));
            report.AppendLine($"错误: {errors}, 告警: {warnings}");
            report.AppendLine(errors == 0 ? "结果: PASS" : "结果: FAIL");

            var reportPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "TempLogs",
                "ui_report.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
            Debug.Log($"[UiValidator] report written to {reportPath}\n{report}");
            EditorApplication.Exit(errors == 0 ? 0 : 1);
        }

        /// <summary>Menu: violations list window with click-to-locate.</summary>
        [MenuItem("ZFrame/Tools/UI 面板校验")]
        private static void OpenWindow()
        {
            EditorWindow.GetWindow<UiValidatorWindow>("ZFrame UI 校验");
        }

        private sealed class UiValidatorWindow : EditorWindow
        {
            private Vector2 _scroll;
            private List<Finding> _findings;

            private void OnGUI()
            {
                if (GUILayout.Button("扫描 Assets/UI prefab"))
                {
                    _findings = ValidateAll();
                }

                if (_findings == null)
                {
                    EditorGUILayout.HelpBox("尚未扫描。批处理:\n-executeMethod ZFrame.Editor.UiValidator.ValidateFromCommandLine", MessageType.Info);
                    return;
                }

                var errors = 0;
                var warnings = 0;
                var infos = 0;
                foreach (var finding in _findings)
                {
                    if (finding.Level == Severity.Error) { errors++; }
                    else if (finding.Level == Severity.Warning) { warnings++; }
                    else { infos++; }
                }

                EditorGUILayout.HelpBox($"错误: {errors}  告警: {warnings}  提示: {infos}", MessageType.None);
                ShowInfoFindings = EditorGUILayout.ToggleLeft("显示提示级", ShowInfoFindings);
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                foreach (var finding in _findings)
                {
                    if (finding.Level == Severity.Info && !ShowInfoFindings)
                    {
                        continue;
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var iconName = finding.Level == Severity.Error
                            ? "console.erroricon"
                            : "console.warnicon";
                        var content = new GUIContent(finding.ToString(),
                            EditorGUIUtility.IconContent(iconName).image);
                        var rect = GUILayoutUtility.GetRect(content, EditorStyles.label);
                        if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
                        {
                            if (finding.Target != null)
                            {
                                Selection.activeObject = finding.Target;
                                EditorGUIUtility.PingObject(finding.Target);
                            }
                            else
                            {
                                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(finding.PrefabPath);
                                Selection.activeObject = prefab;
                                EditorGUIUtility.PingObject(prefab);
                            }

                            Event.current.Use();
                        }

                        EditorGUI.LabelField(rect, content, EditorStyles.label);
                    }
                }

                EditorGUILayout.EndScrollView();
            }
        }
    }
}
