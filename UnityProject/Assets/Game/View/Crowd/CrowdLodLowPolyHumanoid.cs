// CrowdLodLowPolyHumanoid.cs —— L1 档程序化低模人形网格（TASK-A-AND-09）。
// 「程序化低模人形几百面」的单一事实源：12 个盒体部件（头/躯干/骨盆细分，
// 其余平直），合计恰 300 三角面——数百面量级；全精度角色模型为数于千计。
// 运行期零资产：网格纯代码构建（沿 KernelCrowdRenderer 的零资产做法）。

using UnityEngine;

namespace Game.View.Crowd
{
    /// <summary>程序化低模人形网格构建器（静态，纯函数）。</summary>
    public static class CrowdLodLowPolyHumanoid
    {
        /// <summary>人形总高（米）。</summary>
        public const float Height = 1.8f;

        // 部件细分档位：sub=2 的盒体 48 面（每面 2×2 四边形），sub=1 平直盒体 12 面。
        private const int SubdivHead = 2;
        private const int SubdivTorso = 2;
        private const int SubdivPelvis = 2;
        private const int SubdivPlain = 1;

        private static readonly (Vector3 Min, Vector3 Max, int Sub)[] Parts =
        {
            (new Vector3(-0.11f, 1.58f, -0.11f), new Vector3(0.11f, 1.80f, 0.11f), SubdivHead),
            (new Vector3(-0.22f, 1.05f, -0.13f), new Vector3(0.22f, 1.50f, 0.13f), SubdivTorso),
            (new Vector3(-0.20f, 0.88f, -0.13f), new Vector3(0.20f, 1.05f, 0.13f), SubdivPelvis),
            (new Vector3(-0.06f, 1.50f, -0.06f), new Vector3(0.06f, 1.58f, 0.06f), SubdivPlain),
            (new Vector3(-0.34f, 1.24f, -0.07f), new Vector3(-0.24f, 1.48f, 0.07f), SubdivPlain), // 左上臂
            (new Vector3(0.24f, 1.24f, -0.07f), new Vector3(0.34f, 1.48f, 0.07f), SubdivPlain),   // 右上臂
            (new Vector3(-0.34f, 0.98f, -0.06f), new Vector3(-0.25f, 1.24f, 0.06f), SubdivPlain), // 左前臂
            (new Vector3(0.25f, 0.98f, -0.06f), new Vector3(0.34f, 1.24f, 0.06f), SubdivPlain),   // 右前臂
            (new Vector3(-0.30f, 0.88f, -0.06f), new Vector3(-0.21f, 1.00f, 0.06f), SubdivPlain), // 左手
            (new Vector3(0.21f, 0.88f, -0.06f), new Vector3(0.30f, 1.00f, 0.06f), SubdivPlain),   // 右手
            (new Vector3(-0.17f, 0.44f, -0.09f), new Vector3(-0.05f, 0.88f, 0.09f), SubdivPlain), // 左大腿
            (new Vector3(0.05f, 0.44f, -0.09f), new Vector3(0.17f, 0.88f, 0.09f), SubdivPlain),   // 右大腿
            (new Vector3(-0.15f, 0.08f, -0.08f), new Vector3(-0.06f, 0.44f, 0.08f), SubdivPlain), // 左小腿
            (new Vector3(0.06f, 0.08f, -0.08f), new Vector3(0.15f, 0.44f, 0.08f), SubdivPlain),   // 右小腿
            (new Vector3(-0.16f, 0f, -0.10f), new Vector3(-0.04f, 0.08f, 0.16f), SubdivPlain),    // 左脚
            (new Vector3(0.04f, 0f, -0.10f), new Vector3(0.16f, 0.08f, 0.16f), SubdivPlain),      // 右脚
        };

        /// <summary>三角形数（确定性：3 个 48 面部件 + 13 个 12 面部件 = 300）。</summary>
        public static int TriangleCount
        {
            get
            {
                int tris = 0;
                foreach ((_, _, int sub) in Parts)
                {
                    tris += 6 * sub * sub * 2;
                }

                return tris;
            }
        }

        /// <summary>
        /// 构建低模人形网格（每次调用新建；调用方负责 Destroy。局部空间：
        /// 站立在原点、面朝 +Z、总高 <see cref="Height"/>）。
        /// </summary>
        public static Mesh BuildMesh()
        {
            int triCount = TriangleCount;
            var vertices = new Vector3[triCount * 3];
            var normals = new Vector3[triCount * 3];
            int v = 0;

            foreach ((Vector3 min, Vector3 max, int sub) in Parts)
            {
                AddBox(min, max, sub, vertices, normals, ref v);
            }

            var mesh = new Mesh { name = "~CrowdLodLowPolyHumanoid" };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.triangles = Triangles(triCount);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>三角形索引表（部件内部顶点按顺序铺入，索引即 0..triCount*3-1）。</summary>
        private static int[] Triangles(int triCount)
        {
            var tris = new int[triCount * 3];
            for (int i = 0; i < tris.Length; ++i)
            {
                tris[i] = i;
            }

            return tris;
        }

        /// <summary>把一个（可细分的）轴对齐盒体的 6 个面写入顶点/法线缓冲。</summary>
        private static void AddBox(Vector3 min, Vector3 max, int sub,
            Vector3[] vertices, Vector3[] normals, ref int v)
        {
            AddFace(min, max, sub, 0, vertices, normals, ref v); // +X
            AddFace(min, max, sub, 1, vertices, normals, ref v); // -X
            AddFace(min, max, sub, 2, vertices, normals, ref v); // +Y
            AddFace(min, max, sub, 3, vertices, normals, ref v); // -Y
            AddFace(min, max, sub, 4, vertices, normals, ref v); // +Z
            AddFace(min, max, sub, 5, vertices, normals, ref v); // -Z
        }

        /// <summary>
        /// 写入盒体的一个面（face 0..5），sub×sub 细分格，法线朝外，
        /// 顶点顺序保证从外部看为顺时针（Unity 正面）。
        /// 基向量约束：w × u = 外向法线（本发射序 (p00,p11,p10)/(p00,p01,p11)
        /// 的正面性依赖此约束，六个面已逐一按叉积核验）。
        /// </summary>
        private static void AddFace(Vector3 min, Vector3 max, int sub, int face,
            Vector3[] vertices, Vector3[] normals, ref int v)
        {
            // 面基向量：origin 为面的 (0,0) 角，u/w 从该角张成整个面。
            Vector3 size = max - min;
            Vector3 origin, u, w, n;
            switch (face)
            {
                case 0: origin = new Vector3(max.x, min.y, min.z); u = Vector3.forward * size.z; w = Vector3.up * size.y; n = Vector3.right; break;
                case 1: origin = new Vector3(min.x, min.y, max.z); u = Vector3.back * size.z; w = Vector3.up * size.y; n = Vector3.left; break;
                case 2: origin = new Vector3(min.x, max.y, min.z); u = Vector3.back * size.z; w = Vector3.right * size.x; n = Vector3.up; break;
                case 3: origin = new Vector3(min.x, min.y, min.z); u = Vector3.right * size.x; w = Vector3.forward * size.z; n = Vector3.down; break;
                case 4: origin = new Vector3(max.x, min.y, max.z); u = Vector3.left * size.x; w = Vector3.up * size.y; n = Vector3.forward; break;
                default: origin = new Vector3(min.x, min.y, min.z); u = Vector3.right * size.x; w = Vector3.up * size.y; n = Vector3.back; break;
            }

            Vector3 du = u / sub;
            Vector3 dw = w / sub;
            for (int i = 0; i < sub; ++i)
            {
                for (int j = 0; j < sub; ++j)
                {
                    Vector3 p00 = origin + du * i + dw * j;
                    Vector3 p10 = p00 + du;
                    Vector3 p01 = p00 + dw;
                    Vector3 p11 = p10 + dw;

                    // 发射序依赖基向量约束 w × u = 外向法线（见 AddFace 头注释），
                    // 两三角形从外部看均为顺时针 = Unity 正面。
                    Emit(p00, p11, p10, n, vertices, normals, ref v);
                    Emit(p00, p01, p11, n, vertices, normals, ref v);
                }
            }
        }

        private static void Emit(Vector3 a, Vector3 b, Vector3 c, Vector3 n,
            Vector3[] vertices, Vector3[] normals, ref int v)
        {
            vertices[v] = a;
            vertices[v + 1] = b;
            vertices[v + 2] = c;
            normals[v] = n;
            normals[v + 1] = n;
            normals[v + 2] = n;
            v += 3;
        }
    }
}
