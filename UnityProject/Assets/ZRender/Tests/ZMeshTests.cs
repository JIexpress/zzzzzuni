// ZMeshTests.cs —— .zmesh 烘焙→读回→原生加载端到端 EditMode 测试（TASK-B-R4-01）。
// Unity 内置 Cube（GameObject.CreatePrimitive）烘焙到 TempLogs/temp.zmesh，
// ZMeshReader 读回断言 → ZrMesh P/Invoke 原生加载断言 → 释放。

using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Render;
using ZFrame.Render.Bindings;

namespace ZFrame.Render.Tests
{
    [TestFixture]
    public class ZMeshTests
    {
        private static string TempPath()
        {
            string dir = Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "..", "TempLogs"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "temp.zmesh");
        }

        private static Mesh CreateBuiltinCubeMesh()
        {
            // Unity 内置 Cube（GameObject.CreatePrimitive 取共享网格并实例化）。
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                Mesh mesh = UnityEngine.Object.Instantiate(go.GetComponent<MeshFilter>().sharedMesh);
                mesh.name = "Unity Builtin Cube";
                return mesh;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BakeThenReaderReadback_MatchesMesh()
        {
            Mesh cube = CreateBuiltinCubeMesh();
            string path = TempPath();
            ZMeshBaker.Bake(cube, path);

            // 内置 Cube 实测：24 顶点 / 36 索引 / 含法线 / 含 uv0（打印固化依据）。
            Debug.Log($"[ZMeshTests] 内置 Cube：vertexCount={cube.vertexCount} " +
                $"indexCount={cube.triangles.Length} normals={cube.normals.Length} uv={cube.uv.Length}");
            Assert.AreEqual(24, cube.vertexCount, "内置 Cube 顶点数（若失败请以实测更新固化值）");
            Assert.AreEqual(36, cube.triangles.Length, "内置 Cube 索引数（若失败请以实测更新固化值）");

            ZMeshData data = ZMeshReader.Read(path);
            Assert.AreEqual((uint)cube.vertexCount, data.VertexCount);
            Assert.AreEqual((uint)cube.triangles.Length, data.IndexCount);
            Assert.IsTrue(data.HasNormals, "Cube 应含法线列");
            Assert.IsTrue(data.HasUv, "Cube 应含 uv0 列");
            Assert.AreEqual(3u * data.VertexCount, (uint)data.Positions.Length);
            Assert.AreEqual(3u * data.VertexCount, (uint)data.Normals.Length);
            Assert.AreEqual(2u * data.VertexCount, (uint)data.Uv0.Length);

            // 首顶点/首索引逐值比对（列式交错布局）。
            Vector3 v0 = cube.vertices[0];
            Assert.AreEqual(v0.x, data.Positions[0], 1e-6f);
            Assert.AreEqual(v0.y, data.Positions[1], 1e-6f);
            Assert.AreEqual(v0.z, data.Positions[2], 1e-6f);
            Vector3 n0 = cube.normals[0];
            Assert.AreEqual(n0.x, data.Normals[0], 1e-6f);
            Assert.AreEqual(n0.y, data.Normals[1], 1e-6f);
            Assert.AreEqual(n0.z, data.Normals[2], 1e-6f);
            Assert.AreEqual((uint)cube.triangles[0], data.Indices[0]);
            Assert.AreEqual((uint)cube.triangles[1], data.Indices[1]);
            Assert.AreEqual((uint)cube.triangles[2], data.Indices[2]);

            UnityEngine.Object.DestroyImmediate(cube);
        }

        [Test]
        public void NativeLoadFromMemory_StatsMatchCube()
        {
            Mesh cube = CreateBuiltinCubeMesh();
            string path = TempPath();
            ZMeshBaker.Bake(cube, path);
            UnityEngine.Object.DestroyImmediate(cube);

            byte[] bytes = File.ReadAllBytes(path);
            ulong handle = ZrMesh.LoadFromMemory(bytes);
            Assert.AreNotEqual(0ul, handle, "加载成功应返回有效句柄");

            try
            {
                ZrMeshStats stats = ZrMesh.GetStats(handle);
                // 固化值来源：Unity 2022.3 内置 Cube 实测 24 顶点 / 36 索引。
                Assert.AreEqual(24u, stats.VertexCount, "原生加载顶点数");
                Assert.AreEqual(36u, stats.IndexCount, "原生加载索引数");
                Assert.IsTrue(stats.HasNormals, "原生加载应报告法线列");
                Assert.IsTrue(stats.HasUv, "原生加载应报告 uv0 列");
                // 内置 Cube 半边长 0.5：包围盒 [-0.5,0.5]^3。
                Assert.AreEqual(-0.5f, stats.BoundsMin[0], 1e-4f);
                Assert.AreEqual(0.5f, stats.BoundsMax[0], 1e-4f);
                Assert.AreEqual(-0.5f, stats.BoundsMin[1], 1e-4f);
                Assert.AreEqual(0.5f, stats.BoundsMax[1], 1e-4f);
                Assert.AreEqual(-0.5f, stats.BoundsMin[2], 1e-4f);
                Assert.AreEqual(0.5f, stats.BoundsMax[2], 1e-4f);
            }
            finally
            {
                ZrMesh.Free(handle);
            }

            // 重复释放应被拒绝（代际防护）。
            Assert.Throws<ArgumentException>(() => ZrMesh.Free(handle));
        }

        [Test]
        public void NativeLoadFromMemory_BadMagicRejected()
        {
            Mesh cube = CreateBuiltinCubeMesh();
            string path = TempPath();
            ZMeshBaker.Bake(cube, path);
            UnityEngine.Object.DestroyImmediate(cube);

            byte[] bytes = File.ReadAllBytes(path);
            bytes[0] = (byte)'X'; // 坏 magic
            Assert.Throws<ZrMesh.MeshLoadException>(() => ZrMesh.LoadFromMemory(bytes),
                "坏 magic 应被原生解析器拒绝");
        }
    }
}
