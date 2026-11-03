#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using ZFrame.Data.Config;

namespace ZFrame.Data.ConfigEditor
{
    /// <summary>
    /// Simple table inspector for the sample enemy table: tabular row view
    /// plus Validate / Import / Export buttons. The Import button re-reads
    /// the committed csv (Assets/Configs/CSV/enemy.csv) and updates the
    /// asset by Id — the "改 CSV → 重导入 → 运行时生效" loop.
    /// </summary>
    [CustomEditor(typeof(EnemyConfigTable))]
    public class EnemyConfigTableInspector : UnityEditor.Editor
    {
        private static readonly string CsvPath = $"{CsvImporter.CsvRoot}/enemy.csv";
        private static readonly string AssetPath = "Assets/Configs/CFG_EnemyTable.asset";

        public override void OnInspectorGUI()
        {
            var table = (EnemyConfigTable)target;

            EditorGUILayout.HelpBox($"行数: {table.Rows.Count}（数据源: {CsvPath}）", MessageType.None);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Id", GUILayout.MinWidth(80));
            EditorGUILayout.LabelField("Hp", GUILayout.MinWidth(50));
            EditorGUILayout.LabelField("Speed", GUILayout.MinWidth(50));
            EditorGUILayout.LabelField("Damage", GUILayout.MinWidth(50));
            EditorGUILayout.EndHorizontal();

            foreach (var row in table.Rows)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(row.Id, GUILayout.MinWidth(80));
                EditorGUILayout.IntField(row.Hp, GUILayout.MinWidth(50));
                EditorGUILayout.FloatField(row.Speed, GUILayout.MinWidth(50));
                EditorGUILayout.IntField(row.Damage, GUILayout.MinWidth(50));
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(8);

            if (GUILayout.Button("从 CSV 导入（按 Id 更新）"))
            {
                CsvImporter.Import<EnemyConfigRow>(
                    CsvPath, AssetPath, () => ScriptableObject.CreateInstance<EnemyConfigTable>());
            }

            if (GUILayout.Button("导出为 CSV"))
            {
                CsvExporter.Export(table, CsvPath);
            }

            if (GUILayout.Button("校验此表"))
            {
                var violations = ConfigValidator.Validate(table);
                foreach (var violation in violations)
                {
                    Debug.LogError($"[ConfigValidator] {violation}");
                }

                if (violations.Count == 0)
                {
                    Debug.Log("[ConfigValidator] PASS: 无违规");
                }
            }

            if (GUI.changed)
            {
                EditorUtility.SetDirty(table);
            }
        }
    }
}
#endif
