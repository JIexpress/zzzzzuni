#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using ZFrame.Data.ConfigEditor;
using Game.Domain.AI.Utility;

namespace Game.View.Editor
{
    /// <summary>
    /// One-shot generator for the personality config asset
    /// <c>Assets/Configs/CFG_NpcPersonality.asset</c> (TASK-A-TECH-04), from
    /// <c>Assets/Configs/CSV/npc_personality.csv</c> through the A-M1-02
    /// ConfigTable pipeline (<see cref="CsvImporter.Import{TRow}"/> — same
    /// pattern as ZFrame.Data.ConfigEditor.ConfigAssetSetup, placed here
    /// because the row/table types live in Game.Domain and this task's file
    /// whitelist does not open Assets/ZFrame/Data). Import is idempotent:
    /// rows are matched by Id and updated in place.
    ///
    /// Run via menu (ZFrame/Setup/Generate CFG_NpcPersonality) or batch:
    ///   -executeMethod Game.View.Editor.NpcPersonalityConfigSetup.SetupFromCommandLine
    /// Must run BEFORE UtilitySceneSetup (the scene wires the table asset
    /// into every NpcUtilityView).
    /// </summary>
    public static class NpcPersonalityConfigSetup
    {
        private static readonly string CsvPath = $"{CsvImporter.CsvRoot}/npc_personality.csv";
        private const string AssetPath = "Assets/Configs/CFG_NpcPersonality.asset";

        [MenuItem("ZFrame/Setup/Generate CFG_NpcPersonality")]
        public static void Setup()
        {
            if (!File.Exists(CsvPath))
            {
                Debug.LogError($"[NpcPersonalityConfigSetup] csv missing: {CsvPath}");
                return;
            }

            var report = CsvImporter.Import(
                CsvPath,
                AssetPath,
                () => ScriptableObject.CreateInstance<NpcPersonalityTable>());
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[NpcPersonalityConfigSetup] imported: {report}");
        }

        /// <summary>Batch entry: import the CSV into the table asset, then exit 0 (1 on missing csv).</summary>
        public static void SetupFromCommandLine()
        {
            if (!File.Exists(CsvPath))
            {
                Debug.LogError($"[NpcPersonalityConfigSetup] csv missing: {CsvPath}");
                EditorApplication.Exit(1);
                return;
            }

            Setup();
            EditorApplication.Exit(0);
        }
    }
}
#endif
