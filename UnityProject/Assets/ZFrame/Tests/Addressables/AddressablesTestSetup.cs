using System;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace ZFrame.Data.Addressables.Tests
{
    /// <summary>
    /// One-shot generator for the committed Addressables test configuration
    /// under Assets/ZFrame/Tests/AddressablesData/ (settings asset, default
    /// groups/data builders, and a ZFrameTests group holding the two committed
    /// test assets with the plain addresses "TestAsset" and "TestCube").
    ///
    /// Run via menu (ZFrame/Tests/Addressables/Generate Test Setup) or batch:
    ///   -executeMethod ZFrame.Data.Addressables.Tests.AddressablesTestSetup.Generate
    /// The produced assets (and their .meta files) are committed; tests only
    /// READ them. No content build is required — the PlayMode smoke test runs
    /// through the "Use Asset Database (fastest)" path: the
    /// AddressablesRuntimeAdapter points Addressables.InitializeAsync at the
    /// committed settings asset via GUID (mirroring BuildScriptFastMode), so
    /// the configuration may live at a non-default folder.
    /// </summary>
    public static class AddressablesTestSetup
    {
        /// <summary>Folder holding the committed Addressables test configuration.</summary>
        public const string SettingsFolder = "Assets/ZFrame/Tests/AddressablesData";

        private const string SettingsName = "AddressableAssetSettings";
        private const string TestGroupName = "ZFrameTests";
        private const string ResourcesDir = "Assets/ZFrame/Tests/Resources";

        [MenuItem("ZFrame/Tests/Addressables/Generate Test Setup")]
        public static void Generate()
        {
            Directory.CreateDirectory(SettingsFolder);

            // Settings asset (+ Built In Data / Default Local Group / DataBuilders
            // via Validate — the standard shape a real project gets from the menu).
            var settings = AddressableAssetSettings.Create(
                SettingsFolder, SettingsName, createDefaultGroups: true, isPersisted: true);

            // Dedicated non-default group for the test entries (schemas mirror
            // the default local group; irrelevant for asset-database play mode
            // but keeps the configuration realistic for a future content build).
            var group = settings.FindGroup(TestGroupName);
            if (group == null)
            {
                group = settings.CreateGroup(
                    TestGroupName, setAsDefaultGroup: false, readOnly: false, postEvent: false,
                    schemasToCopy: null,
                    typeof(ContentUpdateGroupSchema), typeof(BundledAssetGroupSchema));
            }

            AddEntry(settings, group, $"{ResourcesDir}/TestAsset.asset", "TestAsset");
            AddEntry(settings, group, $"{ResourcesDir}/TestCube.prefab", "TestCube");

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[AddressablesTestSetup] settings at {SettingsFolder} with group '{TestGroupName}' " +
                      "holding addresses: TestAsset, TestCube");
        }

        /// <summary>Moves the asset into the group (idempotent) and pins its plain address.</summary>
        private static void AddEntry(AddressableAssetSettings settings, AddressableAssetGroup group,
            string assetPath, string address)
        {
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid))
            {
                throw new InvalidOperationException(
                    $"[AddressablesTestSetup] test asset missing: {assetPath} " +
                    "(generate it via ZFrame/Tests/Generate Test Assets first).");
            }

            var entry = settings.CreateOrMoveEntry(guid, group, readOnly: false, postEvent: false);
            if (!string.Equals(entry.address, address, StringComparison.Ordinal))
            {
                entry.SetAddress(address, postEvent: false);
            }
        }
    }
}
