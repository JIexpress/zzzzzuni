// ZRenderMenu.cs —— 编辑器菜单与批处理入口：生成 Assets/Settings/ZRenderPipeline.asset。

#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ZFrame.Render
{
    /// <summary>ZRender 编辑器工具菜单。</summary>
    internal static class ZRenderMenu
    {
        private const string AssetPath = "Assets/Settings/ZRenderPipeline.asset";

        [MenuItem("ZFrame/Create ZRender Pipeline Asset")]
        public static void CreatePipelineAsset()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AssetPath)!);
            var asset = ScriptableObject.CreateInstance<ZRenderPipelineAsset>();
            AssetDatabase.CreateAsset(asset, AssetPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[ZRender] 已生成管线资产: {AssetPath}");
        }
    }
}
#endif
