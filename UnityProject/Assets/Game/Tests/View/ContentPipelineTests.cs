// ContentPipelineTests.cs —— TASK-A-CONT-01：内容管线守卫（贴图槽 / 导入设置）。
// 只断言「管线正确性」：每个环境材质必须挂 _BaseMap、贴图必须有 mipmap 且 Repeat、
// 着色器不能丢。美术好看与否不在这里判——这里拦的是"忘记挂图/挂错/导入设置错"
// 这类静默退化。

using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.View.Tests
{
    /// <summary>环境材质的贴图槽与导入设置守卫。</summary>
    public sealed class ContentPipelineTests
    {
        private const string MatDir = "Assets/Art/Models";
        private const string TexDir = "Assets/Art/Textures";

        private static readonly (string Material, string Texture)[] Expectations =
        {
            ("MAT_ENV_Ground.mat", "T_ENV_Grass"),
            ("MAT_ENV_Road.mat", "T_ENV_Road"),
            ("MAT_ENV_Rock.mat", "T_ENV_Rock"),
            ("MAT_ENV_Barrel.mat", "T_ENV_Metal"),
            ("MAT_ENV_Fence.mat", "T_ENV_Wood"),
            ("MAT_ENV_Tree_01.mat", "T_ENV_Bark"),
            ("MAT_ENV_Tree_02.mat", "T_ENV_Bark"),
        };

        [Test]
        public void EveryEnvironmentMaterial_HasItsAlbedoTexture_WithMipmapsAndRepeat()
        {
            foreach ((string materialName, string textureName) in Expectations)
            {
                string path = $"{MatDir}/{materialName}";
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                Assert.IsNotNull(material, $"{path} 必须存在");
                Assert.IsNotNull(material.shader, $"{path} 的着色器丢失（粉红材质的前兆）");

                var texture = material.GetTexture("_BaseMap") as Texture2D;
                Assert.IsNotNull(texture, $"{path} 必须挂 _BaseMap（内容管线不允许纯色裸材质）");
                Assert.AreEqual(textureName, texture.name, $"{path} 挂错了贴图");

                Assert.Greater(texture.mipmapCount, 1,
                    $"{texture.name} 必须开 mipmap（远景不闪烁）");
                Assert.AreEqual(TextureWrapMode.Repeat, texture.wrapMode,
                    $"{texture.name} 必须是 Repeat（平铺贴图）");

                string texturePath = AssetDatabase.GetAssetPath(texture);
                Assert.IsTrue(texturePath.StartsWith(TexDir),
                    $"{texture.name} 必须来自 {TexDir}（资产归口）");

                var importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
                Assert.IsNotNull(importer, $"{texturePath} 必须有 TextureImporter");
                Assert.AreEqual(TextureImporterType.Default, importer.textureType,
                    $"{texture.name} 必须按 Default 导入（albedo）");
                Assert.IsTrue(importer.sRGBTexture,
                    $"{texture.name} 作为 albedo 必须按 sRGB 采样");
                Assert.IsFalse(importer.isReadable,
                    $"{texture.name} 不该开 Read/Write（白占一份 CPU 内存）");
            }
        }

        [Test]
        public void EnvironmentMaterials_KeepBaseColorWhite_SoTheTextureOwnsTheAlbedo()
        {
            foreach ((string materialName, _) in Expectations)
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/{materialName}");
                Assert.IsNotNull(material);
                Color color = material.GetColor("_BaseColor");
                Assert.Greater(color.r, 0.95f, $"{materialName} 的 _BaseColor 必须是白（否则贴图被二次压暗）");
                Assert.Greater(color.g, 0.95f, $"{materialName} 的 _BaseColor 必须是白");
                Assert.Greater(color.b, 0.95f, $"{materialName} 的 _BaseColor 必须是白");
            }
        }
    }
}
