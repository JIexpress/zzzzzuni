#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using ZFrame.Data.Config;
using ZFrame.Data.ConfigEditor;

namespace ZFrame.Data.ConfigEditor
{
    /// <summary>
    /// One-shot generator for the committed sample config assets. Run via
    /// menu (ZFrame/Tests/Generate Config Assets) or batch:
    ///   -executeMethod ZFrame.Data.ConfigEditor.ConfigAssetSetup.Generate
    /// Produces Assets/Configs/CFG_EnemyTable.asset from
    /// Assets/Configs/CSV/enemy.csv; both are committed; tests only read them.
    /// </summary>
    public static class ConfigAssetSetup
    {
        private static readonly string CsvPath = $"{CsvImporter.CsvRoot}/enemy.csv";
        private const string AssetPath = "Assets/Configs/CFG_EnemyTable.asset";

        [MenuItem("ZFrame/Tests/Generate Config Assets")]
        public static void Generate()
        {
            Directory.CreateDirectory(CsvImporter.CsvRoot);
            if (!File.Exists(CsvPath))
            {
                Debug.LogError($"[ConfigAssetSetup] csv missing: {CsvPath}");
                return;
            }

            var report = CsvImporter.Import<EnemyConfigRow>(
                CsvPath, AssetPath, () => ScriptableObject.CreateInstance<EnemyConfigTable>());
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[ConfigAssetSetup] imported: {report}");
        }
    }
}
#endif
