// ZrSceneMath.cs —— 场景桥的纯函数内核（TASK-B-UNITY-04：从 ZRenderSceneBridge 抽出，
// 让 EditMode 单测能直接钉住这四件"改坏了就画面全错"的活）。
//
// 为什么单独成文件：B-UNITY-03 验收留下的事实是「矩阵/顶点打包零测试」，而历史上
// 这里的每一次错误都是肉眼级事故——flip 矩阵写反 → 画面全空；直接 memcpy 矩阵 →
// 物件位置整个转置。抽成 internal static 纯函数后，测试调用的就是**产品实际执行的
// 那一份**（不是副本），所以"改回错误实现"必然让测试变红（变异证据见任务日志）。
//
// 依赖：只用 UnityEngine（Matrix4x4 / Vector3 / Plane / Bounds / GeometryUtility），
// 不碰任何原生库或 GPU 资源——因此可在 EditMode 批处理里裸跑。

using System.Collections.Generic;
using UnityEngine;

namespace ZFrame.Render
{
    /// <summary>场景桥的纯数学部分：矩阵主序、投影归一化、顶点打包、剔除包装。</summary>
    internal static class ZrSceneMath
    {
        /// <summary>本引擎的顶点布局：pos3f + normal3f + uv2f = 8 float。</summary>
        internal const int VertexStrideFloats = 8;

        /// <summary>32 字节/顶点（与 first_draw.hlsl / ZrD3D12_GraphicsInputLayout 同源）。</summary>
        internal const int VertexStrideBytes = VertexStrideFloats * 4;

        /// <summary>顶点内各属性的 float 偏移（测试与打包器共用，避免两处写死）。</summary>
        internal const int PositionOffsetFloats = 0;

        /// <summary>法线起始 float 偏移。</summary>
        internal const int NormalOffsetFloats = 3;

        /// <summary>uv0 起始 float 偏移。</summary>
        internal const int UvOffsetFloats = 6;

        /// <summary>
        /// Unity <see cref="Matrix4x4"/>（列主序存储，索引器是 [行, 列]）→ 本引擎的行主序数组。
        /// <c>dst[r*4+c] = m[r, c]</c>：存储布局转置，数学含义不变（平移量因此落在 3/7/11）。
        /// 直接 memcpy Unity 的 float[16] 会得到**转置**的画面（物件位置全错）。
        /// </summary>
        internal static void ToNativeRowMajor(Matrix4x4 matrix, float[] destination)
        {
            for (int row = 0; row < 4; row++)
            {
                int rowOffset = row * 4;
                for (int column = 0; column < 4; column++)
                {
                    destination[rowOffset + column] = matrix[row, column];
                }
            }
        }

        /// <summary>
        /// 把 Unity 的 GPU 投影矩阵归一化成我们 PSO 期望的约定：
        ///   ① NDC +Y 朝上（m11 &gt; 0）——纹理第 0 行是画面顶部，Blit 端再做 Y 翻转；
        ///   ② 标准 [0,1] 深度且 near→0 / far→1——PSO 固定 D3D12_COMPARISON_FUNC_LESS
        ///      且清屏深度 1.0，反向 Z（Unity 在 D3D 上的默认）会让遮挡关系整个反过来。
        /// </summary>
        internal static Matrix4x4 NormalizeProjection(Matrix4x4 projection, float nearPlane,
            float farPlane)
        {
            if (projection.m11 < 0f)
            {
                projection.m10 = -projection.m10;
                projection.m11 = -projection.m11;
                projection.m12 = -projection.m12;
                projection.m13 = -projection.m13;
            }

            float depthAtNear = DepthAtViewDepth(projection, nearPlane);
            float depthAtFar = DepthAtViewDepth(projection, farPlane);
            if (depthAtNear > depthAtFar)
            {
                // 反向 Z（Unity 在 D3D 上就是这样）→ z' = 1 - z。
                // 行 2 = (0,0,-1,1)：z' = -z + w；行 3 = (0,0,0,1)：w' = w
                // （两者写反会得到 z'/(w-z) 这种畸形投影 —— 画面直接全空）。
                Matrix4x4 flip = Matrix4x4.zero;
                flip.m00 = 1f;
                flip.m11 = 1f;
                flip.m22 = -1f;
                flip.m23 = 1f;
                flip.m33 = 1f;
                projection = flip * projection;
            }

            return projection;
        }

        /// <summary>取相机前方 depth（正数）处的 NDC 深度（z/w）。</summary>
        internal static float DepthAtViewDepth(Matrix4x4 projection, float depth)
        {
            float z = projection.m22 * -depth + projection.m23;
            float w = projection.m32 * -depth + projection.m33;
            return (Mathf.Abs(w) > 1e-6f) ? z / w : 0f;
        }

        /// <summary>
        /// 顶点打包：pos/normal/uv 三列 → 32B/顶点的紧凑浮点流（复用调用方的缓冲）。
        /// 法线**归一化**后写出（缺列 / 零向量时给 +Y，别给零向量——零法线在 PS 里
        /// normalize 会变 NaN，整块面发黑）。返回实际写入的顶点数（容量不足返回 0）。
        /// </summary>
        internal static int PackVertices(IList<Vector3> positions, IList<Vector3> normals,
            IList<Vector2> uvs, float[] destination)
        {
            if (positions == null || destination == null)
            {
                return 0;
            }

            int vertexCount = positions.Count;
            if (vertexCount <= 0 || destination.Length < vertexCount * VertexStrideFloats)
            {
                return 0;
            }

            bool hasNormals = normals != null && normals.Count == vertexCount;
            bool hasUvs = uvs != null && uvs.Count == vertexCount;
            Vector3 fallbackNormal = Vector3.up;
            int at = 0;
            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 position = positions[i];
                Vector3 normal = hasNormals ? normals[i] : fallbackNormal;
                float lengthSquared = normal.sqrMagnitude;
                if (lengthSquared > 1e-12f)
                {
                    normal = normal * (1f / Mathf.Sqrt(lengthSquared));
                }
                else
                {
                    normal = fallbackNormal; // 零法线：给 +Y（与缺列同策）
                }

                Vector2 uv = hasUvs ? uvs[i] : Vector2.zero;
                destination[at + PositionOffsetFloats] = position.x;
                destination[at + PositionOffsetFloats + 1] = position.y;
                destination[at + PositionOffsetFloats + 2] = position.z;
                destination[at + NormalOffsetFloats] = normal.x;
                destination[at + NormalOffsetFloats + 1] = normal.y;
                destination[at + NormalOffsetFloats + 2] = normal.z;
                destination[at + UvOffsetFloats] = uv.x;
                destination[at + UvOffsetFloats + 1] = uv.y;
                at += VertexStrideFloats;
            }

            return vertexCount;
        }

        /// <summary>
        /// 视锥剔除（CPU 近似）：包装 <see cref="GeometryUtility.TestPlanesAABB"/>，
        /// 三态语义由它定义——完全在内 true、完全在外 false、跨边界 true（保守）。
        /// 单独抽一层的理由：桥里故意不用 Renderer.isVisible（自研 SRP 不调 Cull，
        /// 该位可能恒 false），所以这条判断是"画不画"的唯一闸门，值得被钉住。
        /// </summary>
        internal static bool IsVisible(Plane[] planes, Bounds bounds)
        {
            return GeometryUtility.TestPlanesAABB(planes, bounds);
        }
    }
}
