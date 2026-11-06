// ZrSceneMathTests.cs —— 场景桥纯函数的 EditMode 单测（TASK-B-UNITY-04）。
//
// 为什么有这份测试：B-UNITY-03 验收列的第一条遗留就是"矩阵转换与顶点解析零测试"，
// 而这两块历史上每次改坏都是肉眼级事故（flip 矩阵写反 → 画面全空；直接 memcpy
// 矩阵 → 物件位置整个转置）。本文件钉住四件事，全部是纯 CPU 计算、不依赖 GPU：
//   1) 矩阵主序：Unity Matrix4x4（列主序）→ 本引擎 float[16]（行主序）
//   2) 投影归一化：GL.GetGPUProjectionMatrix 的 Y 翻转与反向 Z 被消掉，near→0/far→1
//   3) 顶点打包：32B/顶点（pos3f + normal3f + uv2f）、属性偏移、法线归一化
//   4) 剔除包装：完全在内 / 完全在外 / 跨界三态
//
// 每条都做过变异（改回错误实现必红，还原必绿），证据见 Docs/Logs/TASK-B-UNITY-04.md。

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace ZFrame.Render.Tests
{
    [TestFixture]
    public class ZrSceneMathTests
    {
        // ---- 1) 矩阵主序 -----------------------------------------------------------

        /// <summary>
        /// 非对称矩阵（只有 m03 = 7）是关键：Unity 的列主序存储会把 7 放在原始字节流
        /// 的第 12 位，而行主序目标里它必须落在第 3 位。直接 memcpy 的实现必红。
        /// </summary>
        [Test]
        public void ToNativeRowMajor_NonSymmetricMatrix_LandsOnTransposedIndex()
        {
            Matrix4x4 matrix = Matrix4x4.identity;
            matrix.m03 = 7f; // 第 0 行第 3 列（平移 x）

            float[] destination = new float[16];
            ZrSceneMath.ToNativeRowMajor(matrix, destination);

            Assert.AreEqual(7f, destination[3], "m03 必须落在行主序的第 3 位（memcpy 会落在 12）");
            Assert.AreEqual(0f, destination[12], "destination[12] 必须是 m30 = 0（memcpy 会把它写成 7）");
            // 单位矩阵的非对角项在两种主序下都是 0，只有 7 这一项能区分实现
            Assert.AreEqual(1f, destination[0]);
            Assert.AreEqual(1f, destination[5]);
            Assert.AreEqual(1f, destination[10]);
            Assert.AreEqual(1f, destination[15]);
        }

        /// <summary>
        /// 满阵逐元素验证：m[r,c] = r*10+c。行主序目标应为 0,1,2,3, 10,11,12,13, 20,…
        /// （memcpy 得到的是列主序字节序 0,10,20,30, 1,11,…——两者没有任何一项相同）。
        /// </summary>
        [Test]
        public void ToNativeRowMajor_FullMatrix_MatchesRowMajorLayout()
        {
            var matrix = new Matrix4x4();
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    matrix[row, column] = (row * 10) + column;
                }
            }

            float[] destination = new float[16];
            ZrSceneMath.ToNativeRowMajor(matrix, destination);

            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    Assert.AreEqual((row * 10) + column, destination[(row * 4) + column],
                        $"destination[{row * 4 + column}] 必须是 m[{row},{column}]");
                }
            }
        }

        /// <summary>平移量必须落在 3/7/11（本引擎"行主序存储 + 列向量"的判据）。</summary>
        [Test]
        public void ToNativeRowMajor_Translation_LandsAtIndexes3_7_11()
        {
            Matrix4x4 matrix = Matrix4x4.Translate(new Vector3(1.5f, -2.5f, 3.5f));
            float[] destination = new float[16];
            ZrSceneMath.ToNativeRowMajor(matrix, destination);

            Assert.AreEqual(1.5f, destination[3], 1e-5f);
            Assert.AreEqual(-2.5f, destination[7], 1e-5f);
            Assert.AreEqual(3.5f, destination[11], 1e-5f);
            Assert.AreEqual(0f, destination[12], 1e-5f);
            Assert.AreEqual(0f, destination[13], 1e-5f);
            Assert.AreEqual(0f, destination[14], 1e-5f);
        }

        // ---- 2) 投影归一化 ---------------------------------------------------------

        /// <summary>
        /// 归一化后的投影必须同时满足：① NDC +Y 朝上（m11 &gt; 0，视空间上方的点投到上方）；
        /// ② 标准 [0,1] 深度且 near→0 / far→1（PSO 固定 LESS + 清 1.0）。
        /// 输入取 Unity 真正给的那一份（GL.GetGPUProjectionMatrix），不是手搓的理想矩阵。
        /// </summary>
        [Test]
        public void NormalizeProjection_NearAtZeroFarAtOneAndYUp()
        {
            const float nearPlane = 0.3f;
            const float farPlane = 1000f;
            Matrix4x4 glProjection = Matrix4x4.Perspective(60f, 16f / 9f, nearPlane, farPlane);
            Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(glProjection, false);
            Matrix4x4 normalized = ZrSceneMath.NormalizeProjection(gpuProjection, nearPlane, farPlane);

            Assert.Greater(normalized.m11, 0f, "归一化后 NDC +Y 必须朝上（m11 > 0）");

            Vector3 nearNdc = ProjectToNdc(normalized, new Vector3(0f, 0f, -nearPlane));
            Vector3 farNdc = ProjectToNdc(normalized, new Vector3(0f, 0f, -farPlane));

            Assert.That(nearNdc.z, Is.EqualTo(0f).Within(2e-3f),
                $"近平面点的 NDC z 应为 0（实际 {nearNdc.z:F6}）");
            Assert.That(farNdc.z, Is.EqualTo(1f).Within(2e-3f),
                $"远平面点的 NDC z 应为 1（实际 {farNdc.z:F6}）");
            Assert.Less(nearNdc.z, farNdc.z, "near 的深度必须小于 far（否则遮挡关系整体反过来）");

            // 视空间 +Y 的点必须在 NDC 上方（Y 翻转已被消掉）
            Vector3 upNdc = ProjectToNdc(normalized, new Vector3(0f, 1f, -5f));
            Assert.Greater(upNdc.y, 0f, "视空间上方的点必须落在 NDC 上半部（+Y 朝上）");
            // 视空间 +X 的点仍在右侧（X 不该被翻转）
            Vector3 rightNdc = ProjectToNdc(normalized, new Vector3(1f, 0f, -5f));
            Assert.Greater(rightNdc.x, 0f, "视空间右侧的点必须落在 NDC 右半部");
        }

        // ---- 3) 顶点打包 -----------------------------------------------------------

        /// <summary>
        /// 32B/顶点：stride = 8 float，属性偏移 pos=0..2 / normal=3..5 / uv=6..7，
        /// 法线归一化（(0,2,0) → (0,1,0)），零法线给 +Y 兜底。
        /// </summary>
        [Test]
        public void PackVertices_Uses32ByteStride_AndNormalizesNormals()
        {
            Assert.AreEqual(8, ZrSceneMath.VertexStrideFloats, "stride 必须是 8 float");
            Assert.AreEqual(32, ZrSceneMath.VertexStrideBytes, "stride 必须是 32 字节");
            Assert.AreEqual(0, ZrSceneMath.PositionOffsetFloats);
            Assert.AreEqual(3, ZrSceneMath.NormalOffsetFloats);
            Assert.AreEqual(6, ZrSceneMath.UvOffsetFloats);

            var positions = new List<Vector3>
            {
                new Vector3(1f, 2f, 3f),
                new Vector3(-4f, 5f, -6f),
            };
            var normals = new List<Vector3>
            {
                new Vector3(0f, 2f, 0f),  // 非单位长度 → 必须归一化
                Vector3.zero,             // 零法线 → +Y 兜底（零向量在 PS 里 normalize 会发黑）
            };
            var uvs = new List<Vector2>
            {
                new Vector2(0.25f, 0.75f),
                new Vector2(1f, 0f),
            };

            var destination = new float[2 * ZrSceneMath.VertexStrideFloats];
            int packed = ZrSceneMath.PackVertices(positions, normals, uvs, destination);

            Assert.AreEqual(2, packed, "返回的顶点数必须与输入一致");
            Assert.AreEqual(destination.Length, packed * ZrSceneMath.VertexStrideFloats,
                "写入量必须正好是 顶点数 × stride");

            // 顶点 0：位置 0..2 / 法线 3..5（归一化后 y=1）/ uv 6..7
            Assert.AreEqual(1f, destination[0]);
            Assert.AreEqual(2f, destination[1]);
            Assert.AreEqual(3f, destination[2]);
            Assert.AreEqual(0f, destination[3], 1e-6f);
            Assert.AreEqual(1f, destination[4], 1e-6f);
            Assert.AreEqual(0f, destination[5], 1e-6f);
            Assert.AreEqual(0.25f, destination[6], 1e-6f);
            Assert.AreEqual(0.75f, destination[7], 1e-6f);

            // 顶点 1：基址 = 8（stride 必须真的跨 8 个 float）
            Assert.AreEqual(-4f, destination[8]);
            Assert.AreEqual(5f, destination[9]);
            Assert.AreEqual(-6f, destination[10]);
            Assert.AreEqual(0f, destination[11], 1e-6f);
            Assert.AreEqual(1f, destination[12], 1e-6f);
            Assert.AreEqual(0f, destination[13], 1e-6f);
            Assert.AreEqual(1f, destination[14], 1e-6f);
            Assert.AreEqual(0f, destination[15], 1e-6f);
        }

        /// <summary>容量不足必须返回 0（宁可跳过该 mesh，也不能越界写坏别人的缓冲）。</summary>
        [Test]
        public void PackVertices_TooSmallDestination_ReturnsZero()
        {
            var positions = new List<Vector3> { Vector3.one, Vector3.one };
            var normals = new List<Vector3> { Vector3.up, Vector3.up };
            var uvs = new List<Vector2> { Vector2.zero, Vector2.zero };

            Assert.AreEqual(0, ZrSceneMath.PackVertices(positions, normals, uvs, new float[15]));
        }

        // ---- 4) 剔除包装 -----------------------------------------------------------

        /// <summary>
        /// 三态：完全在内 true、完全在外（相机后方 / 侧面）false、跨近平面的巨大包围盒 true
        /// （TestPlanesAABB 是保守近似：只要没被任一平面完全排除就算可见）。
        /// </summary>
        [Test]
        public void IsVisible_InsideOutsideAndStraddling()
        {
            var cameraObject = new GameObject("ZrSceneMathTests_CullCamera");
            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.transform.position = new Vector3(0f, 0f, -10f);
                camera.transform.rotation = Quaternion.identity; // 朝 +Z
                camera.fieldOfView = 60f;
                camera.nearClipPlane = 0.3f;
                camera.farClipPlane = 100f;
                camera.aspect = 16f / 9f;

                var planes = new Plane[6];
                GeometryUtility.CalculateFrustumPlanes(camera, planes);

                var inside = new Bounds(new Vector3(0f, 0f, 10f), new Vector3(1f, 1f, 1f));
                var behindCamera = new Bounds(new Vector3(0f, 0f, -20f), new Vector3(1f, 1f, 1f));
                var farSide = new Bounds(new Vector3(500f, 0f, 10f), new Vector3(1f, 1f, 1f));
                var straddling = new Bounds(new Vector3(0f, 0f, -10f), new Vector3(4f, 4f, 60f));

                Assert.IsTrue(ZrSceneMath.IsVisible(planes, inside), "完全在视锥内的包围盒必须可见");
                Assert.IsFalse(ZrSceneMath.IsVisible(planes, behindCamera),
                    "相机后方的包围盒必须被剔除");
                Assert.IsFalse(ZrSceneMath.IsVisible(planes, farSide),
                    "远侧方的包围盒必须被剔除");
                Assert.IsTrue(ZrSceneMath.IsVisible(planes, straddling),
                    "跨近平面的包围盒必须判为可见（保守近似，不许误剔除）");

                // 与 Unity 原生判据逐字一致（包装层不得擅自改语义）
                Assert.AreEqual(GeometryUtility.TestPlanesAABB(planes, straddling),
                    ZrSceneMath.IsVisible(planes, straddling));
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
            }
        }

        // ---- 助手 ------------------------------------------------------------------

        /// <summary>视空间点 → NDC（clip 齐次除法）。</summary>
        private static Vector3 ProjectToNdc(Matrix4x4 projection, Vector3 viewPosition)
        {
            Vector4 clip = projection * new Vector4(viewPosition.x, viewPosition.y, viewPosition.z, 1f);
            return new Vector3(clip.x / clip.w, clip.y / clip.w, clip.z / clip.w);
        }
    }
}
