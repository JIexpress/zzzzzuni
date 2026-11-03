using System.IO;
using UnityEditor;
using UnityEngine;

namespace ZFrame.Data.Tests
{
    /// <summary>
    /// One-shot generator for the committed test assets under
    /// Assets/ZFrame/Tests/Resources/. Run via menu (ZFrame/Tests/Generate
    /// Test Assets) or batch:
    ///   -executeMethod ZFrame.Data.Tests.TestAssetSetup.Generate
    /// The produced asset + prefab (and their .meta files) are committed;
    /// tests only READ them.
    /// </summary>
    public static class TestAssetSetup
    {
        private static readonly string ResourcesDir = "Assets/ZFrame/Tests/Resources";

        [MenuItem("ZFrame/Tests/Generate Test Assets")]
        public static void Generate()
        {
            Directory.CreateDirectory(ResourcesDir);

            // MAT_TestCube.asset — URP Lit (migrated from the built-in default
            // material in TASK-A-M2-03; the built-in Standard shader renders
            // magenta under URP).
            var materialPath = $"{ResourcesDir}/MAT_TestCube.asset";
            var litShader = Shader.Find("Universal Render Pipeline/Lit");
            if (litShader == null)
            {
                throw new System.InvalidOperationException(
                    "[TestAssetSetup] URP Lit shader not found — is com.unity.render-pipelines.universal installed?");
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(litShader) { name = "MAT_TestCube" };
                AssetDatabase.CreateAsset(material, materialPath);
            }
            else
            {
                material.shader = litShader;
            }

            // TestAsset.asset — ScriptableObject payload.
            var assetPath = $"{ResourcesDir}/TestAsset.asset";
            var config = ScriptableObject.CreateInstance<TestConfigData>();
            config.value = 42;
            AssetDatabase.CreateAsset(config, assetPath);

            // TestCube.prefab — built-in Cube + TestComponent marker + URP Lit material.
            var prefabPath = $"{ResourcesDir}/TestCube.prefab";
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "TestCube";
            cube.GetComponent<MeshRenderer>().sharedMaterial = material;
            cube.AddComponent<TestComponent>();
            PrefabUtility.SaveAsPrefabAsset(cube, prefabPath);
            Object.DestroyImmediate(cube);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[TestAssetSetup] wrote {assetPath}, {prefabPath} (URP Lit) and {materialPath}");
        }
    }
}
