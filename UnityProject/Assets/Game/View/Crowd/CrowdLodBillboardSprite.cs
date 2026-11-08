// CrowdLodBillboardSprite.cs —— L2 档 billboard 资产（TASK-A-AND-09）。
// 「程序化人群纹理或纯色剪影」的单一事实源：运行期生成的 64×64 头肩剪影
// 纹理 + 面向相机的单位四边形（2 三角面，+Z 为可见面法线）。
// 旋转由管线按帧计算（LookRotation(camPos − pos)），本类只提供静态资产。

using UnityEngine;

namespace Game.View.Crowd
{
    /// <summary>billboard 剪影纹理与四边形网格构建器（静态，纯函数）。</summary>
    public static class CrowdLodBillboardSprite
    {
        /// <summary>剪影纹理边长（像素，正方形）。</summary>
        public const int TextureSize = 64;

        /// <summary>billboard 世界尺寸（米，正方形；L2 远景剪影的视觉高度）。</summary>
        public const float WorldSize = 1.8f;

        /// <summary>剪影颜色（暗青灰，远景人群的统一色调）。</summary>
        private static readonly Color32 SilhouetteColor = new Color32(26, 34, 44, 255);

        /// <summary>
        /// 构建头肩剪影纹理（运行期程序化：头部圆 + 肩胸梯形，轮廓外全透明）。
        /// 调用方负责 Destroy。确定性：同参数逐像素一致。
        /// </summary>
        public static Texture2D BuildSilhouetteTexture()
        {
            var tex = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false)
            {
                name = "~CrowdLodSilhouette",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var pixels = new Color32[TextureSize * TextureSize];
            for (int y = 0; y < TextureSize; ++y)
            {
                for (int x = 0; x < TextureSize; ++x)
                {
                    pixels[y * TextureSize + x] = InsideSilhouette(x, y) ? SilhouetteColor : default;
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>
        /// 构建单位 billboard 四边形：XY 平面、中心在原点、边长 <see cref="WorldSize"/>、
        /// 可见面法线 +Z（2 三角面；旋转由管线 LookRotation(camPos−pos) 计算，
        /// 使 +Z 轴指向相机 → 可见面朝向相机）。
        /// </summary>
        public static Mesh BuildQuad()
        {
            float h = WorldSize * 0.5f;
            var mesh = new Mesh { name = "~CrowdLodBillboardQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-h, 0f, 0f), new Vector3(h, 0f, 0f),
                new Vector3(h, WorldSize, 0f), new Vector3(-h, WorldSize, 0f),
            };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>像素是否落在头肩剪影内（头部圆 + 肩胸轮廓，[0,64) 像素坐标）。</summary>
        private static bool InsideSilhouette(int x, int y)
        {
            float fx = x + 0.5f;
            float fy = y + 0.5f;

            // 头部：圆心 (32, 44)，半径 11。
            float dx = fx - 32f;
            float dy = fy - 44f;
            if (dx * dx + dy * dy <= 11f * 11f)
            {
                return true;
            }

            // 肩胸：y ∈ [4, 36]，半宽从肩部 24 收窄到胸口 14（梯形），底部平切。
            if (fy >= 4f && fy <= 36f)
            {
                float t = (fy - 4f) / 32f;              // 0 底部 → 1 肩线
                float halfWidth = Mathf.Lerp(14f, 24f, t);
                return Mathf.Abs(dx) <= halfWidth;
            }

            // 颈部：连接头与胸的窄柱。
            if (fy > 36f && fy < 40f)
            {
                return Mathf.Abs(dx) <= 5f;
            }

            return false;
        }
    }
}
