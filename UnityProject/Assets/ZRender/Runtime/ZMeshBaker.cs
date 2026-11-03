// ZMeshBaker.cs —— Unity Mesh → .zmesh v1 编辑器烘焙器（TASK-B-R4-01）。
//
// 置于 Runtime 程序集（ZFrame.Render）并以 #if UNITY_EDITOR 包裹编辑器部分：
// 不新增 asmdef（避免触碰架构校验器期望矩阵），Bake 静态方法供 EditMode 测试
// 与批处理 executeMethod 调用。

#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZFrame.Render
{
    /// <summary>把 Mesh 的位置/法线/uv0/索引以 SoA 列式烘焙为 .zmesh v1（小端）。</summary>
    public static class ZMeshBaker
    {
        /// <summary>烘焙选中物体（MeshFilter 或直接选中的 Mesh）到同目录 .zmesh。</summary>
        [MenuItem("ZFrame/ZMesh/Bake Selection")]
        public static void BakeSelection()
        {
            Mesh mesh = Selection.activeObject as Mesh;
            if (mesh == null && Selection.activeGameObject != null)
            {
                var filter = Selection.activeGameObject.GetComponent<MeshFilter>();
                if (filter != null)
                {
                    mesh = filter.sharedMesh;
                }
            }
            if (mesh == null)
            {
                Debug.LogError("[ZMeshBaker] 请选中一个 Mesh 或带 MeshFilter 的物体。");
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(mesh);
            string outPath = string.IsNullOrEmpty(assetPath)
                ? Path.Combine("Temp", mesh.name + ".zmesh")
                : Path.ChangeExtension(assetPath, ".zmesh");
            Bake(mesh, outPath);
            Debug.Log($"[ZMeshBaker] 烘焙 '{mesh.name}' -> {outPath}");
        }

        /// <summary>
        /// 烘焙 <paramref name="mesh"/> 到 <paramref name="path"/>。
        /// 法线/uv0 列按 Mesh 实际数据有无写入 flags；索引以 u32 写出（值域校验）。
        /// </summary>
        public static void Bake(Mesh mesh, string path)
        {
            Debug.Assert(mesh != null);
            Debug.Assert(!string.IsNullOrEmpty(path));

            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector2[] uv = mesh.uv;
            int[] triangles = mesh.triangles;

            // 头大小断言（与 C++ static_assert 对齐；显式炸错优于静默错位）。
            if (System.Runtime.InteropServices.Marshal.SizeOf(typeof(ZmeshHeader))
                != ZMeshFormat.HeaderSize)
            {
                throw new InvalidDataException("ZmeshHeader 托管布局与约定 20B 不符");
            }

            bool hasNormals = normals != null && normals.Length == vertices.Length;
            bool hasUv = uv != null && uv.Length == vertices.Length;
            uint flags = 0u;
            if (hasNormals)
            {
                flags |= ZMeshFormat.FlagNormals;
            }
            if (hasUv)
            {
                flags |= ZMeshFormat.FlagUv0;
            }

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);

            writer.Write(ZMeshFormat.Magic);
            writer.Write(ZMeshFormat.Version);
            writer.Write(checked((uint)vertices.Length));
            writer.Write(checked((uint)triangles.Length));
            writer.Write(flags);

            foreach (Vector3 v in vertices)
            {
                writer.Write(v.x);
                writer.Write(v.y);
                writer.Write(v.z);
            }
            if (hasNormals)
            {
                foreach (Vector3 n in normals)
                {
                    writer.Write(n.x);
                    writer.Write(n.y);
                    writer.Write(n.z);
                }
            }
            if (hasUv)
            {
                foreach (Vector2 t in uv)
                {
                    writer.Write(t.x);
                    writer.Write(t.y);
                }
            }
            foreach (int index in triangles)
            {
                if (index < 0 || index >= vertices.Length)
                {
                    throw new InvalidDataException($"索引越界：{index}（顶点数 {vertices.Length}）");
                }
                writer.Write(checked((uint)index));
            }
        }

        /// <summary>
        /// 批处理入口（-executeMethod）：烘焙 Unity 内置 Cube 到
        /// Native/tests/data/cube.zmesh（仓库入库示例资产，来源注明内置 Cube）。
        /// </summary>
        public static void BakeBuiltinCubeToRepoData()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh mesh = go.GetComponent<MeshFilter>().sharedMesh;
            // 临时实例化（内置 Cube 为共享资源，不修改原资源）。
            var meshCopy = Object.Instantiate(mesh);
            meshCopy.name = "Unity Builtin Cube";
            string outPath = Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "..", "Native", "tests", "data", "cube.zmesh"));
            Bake(meshCopy, outPath);
            Debug.Log($"[ZMeshBaker] 内置 Cube（顶点 {meshCopy.vertexCount} / 索引 "
                + $"{(int)meshCopy.GetIndexCount(0)}）已烘焙 -> {outPath}");
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(meshCopy);
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// 画廊批处理的 4 个目标 FBX（Assets/Art/Models，来源/许可证见同目录 SOURCES.md）。
        /// 输出命名 = FBX 文件名去扩展名 + .zmesh（与窗口程序 --assets 模式硬编码清单一致）。
        /// </summary>
        internal static readonly string[] GalleryFbxNames =
        {
            "CHR_Kenney_CharacterMedium",
            "CHR_Robot",
            "CHR_Human",
            "CHR_DarkElf",
        };

        /// <summary>
        /// 站立修正的绕 X 旋转方向（每模型实测，头部朝向经画廊截图核验）：
        /// +1 = +90°（y' = -z，Kenney：网格原点在脚、头朝本地 -Z）；
        /// -1 = -90°（y' = +z，Robot/Human/DarkElf：头朝本地 +Z）；
        ///  0 = 不旋转（网格已 Y-up）。
        /// 仅在探测到"躺倒"（size.z > size.y）时应用——见 <see cref="BakeGalleryToRepoData"/>。
        /// </summary>
        internal static readonly int[] GalleryStandUpFix =
        {
            +1, // CHR_Kenney_CharacterMedium
            -1, // CHR_Robot
            +1, // CHR_Human（首烘截图核验：-90° 头朝下，翻转）
            +1, // CHR_DarkElf（首烘截图核验：-90° 头朝下，翻转）
        };

        /// <summary>绕 X 轴旋转 ±90°： quarterTurn=+1 → (x,y,z)→(x,-z,y)；-1 → (x,y,z)→(x,z,-y)。</summary>
        private static void RotateQuarterTurnX(
            ref float x, ref float y, ref float z, int quarterTurn)
        {
            float ny = quarterTurn > 0 ? -z : z;
            float nz = quarterTurn > 0 ? y : -y;
            y = ny;
            z = nz;
        }

        /// <summary>
        /// 批处理入口（-executeMethod，TASK-B-R9-13）：把 Assets/Art/Models 下 4 个人物
        /// FBX 的首个 Mesh 烘焙为 Native/tests/data/CHR_*.zmesh（画廊模式真数据）。
        ///
        /// 幂等：重复运行覆盖同名输出并以 0 退出。
        /// 已知风险核对（简报）与其实测结论：
        /// ①缩放——FBX 容器单位/全局缩放异常（首烘实测 1.6cm~7.9cm 高），已按模型在
        ///   导入 meta 修 globalScale（Kenney 48 / Robot 100 / Human 96 / DarkElf 24）；
        /// ②轴朝向——这批 FBX 容器声明 Y-up 但几何实际 Z-up（躺倒），bakeAxisConversion=1
        ///   是 no-op（容器无可转换轴），导入设置无法表达此修正 → 烘焙入口按
        ///   <see cref="GalleryStandUpFix"/> 做 -90°/+90° 绕 X 旋转（位置+法线），
        ///   zmesh 产出统一为 Unity 语义 Y-up 站立（方向经画廊截图核验）。
        /// 多 Mesh FBX 取顶点数最多的 Mesh（子网格经 mesh.triangles 串联进同一索引表）。
        /// </summary>
        public static void BakeGalleryToRepoData()
        {
            const int exitOk = 0;
            const int exitFail = 1;
            try
            {
                string dataDir = Path.GetFullPath(Path.Combine(
                    Application.dataPath, "..", "..", "Native", "tests", "data"));
                Directory.CreateDirectory(dataDir);
                int baked = 0;
                for (int fbxIndex = 0; fbxIndex < GalleryFbxNames.Length; ++fbxIndex)
                {
                    string fbxName = GalleryFbxNames[fbxIndex];
                    string fbxPath = $"Assets/Art/Models/{fbxName}.fbx";
                    var meshes = AssetDatabase.LoadAllAssetsAtPath(fbxPath)
                        .OfType<Mesh>()
                        .ToArray();
                    if (meshes.Length == 0)
                    {
                        Debug.LogError($"[ZMeshBaker] {fbxPath} 内无 Mesh，跳过（总失败计数+1）");
                        continue;
                    }
                    // 多 Mesh（LOD/分件）取顶点最多者；单 Mesh 路径行为一致。
                    Mesh mesh = meshes[0];
                    foreach (Mesh candidate in meshes)
                    {
                        if (candidate.vertexCount > mesh.vertexCount)
                        {
                            mesh = candidate;
                        }
                    }

                    int indexCount = mesh.triangles.Length;
                    Bounds b = mesh.bounds;
                    Debug.Log(
                        $"[ZMeshBaker][Gallery] {fbxName}: vertexCount={mesh.vertexCount} "
                        + $"indexCount={indexCount} subMeshes={mesh.subMeshCount} "
                        + $"bounds(min=({b.min.x:F4},{b.min.y:F4},{b.min.z:F4}), "
                        + $"max=({b.max.x:F4},{b.max.y:F4},{b.max.z:F4}), "
                        + $"size=({b.size.x:F4},{b.size.y:F4},{b.size.z:F4})) "
                        + $"normals={(mesh.normals.Length == mesh.vertexCount ? 1 : 0)} "
                        + $"uv0={(mesh.uv.Length == mesh.vertexCount ? 1 : 0)}");

                    // 站立修正：躺倒探测（size.z > size.y）→ 临时实例上旋转位置+法线后烘焙
                    //（不改原资源；cube 批处理先例同策——实例化后修改）。
                    Mesh bakeMesh = mesh;
                    Mesh standUpCopy = null;
                    if (b.size.z > b.size.y)
                    {
                        int quarterTurn = GalleryStandUpFix[fbxIndex];
                        standUpCopy = Object.Instantiate(mesh);
                        standUpCopy.name = mesh.name + " (StandUpFix)";
                        Vector3[] rotatedVerts = standUpCopy.vertices;
                        Vector3[] rotatedNormals = standUpCopy.normals;
                        bool hasNormals = rotatedNormals.Length == rotatedVerts.Length;
                        for (int i = 0; i < rotatedVerts.Length; ++i)
                        {
                            float x = rotatedVerts[i].x;
                            float y = rotatedVerts[i].y;
                            float z = rotatedVerts[i].z;
                            RotateQuarterTurnX(ref x, ref y, ref z, quarterTurn);
                            rotatedVerts[i] = new Vector3(x, y, z);
                            if (hasNormals)
                            {
                                float nx = rotatedNormals[i].x;
                                float ny = rotatedNormals[i].y;
                                float nz = rotatedNormals[i].z;
                                RotateQuarterTurnX(ref nx, ref ny, ref nz, quarterTurn);
                                rotatedNormals[i] = new Vector3(nx, ny, nz);
                            }
                        }

                        standUpCopy.vertices = rotatedVerts;
                        if (hasNormals)
                        {
                            standUpCopy.normals = rotatedNormals;
                        }
                        standUpCopy.RecalculateBounds();
                        bakeMesh = standUpCopy;
                        Bounds fixedBounds = standUpCopy.bounds;
                        Debug.Log(
                            $"[ZMeshBaker][Gallery] {fbxName} 躺倒修正：绕X {quarterTurn * 90}° "
                            + $"（容器声明 Y-up 但几何 Z-up，导入设置不可表达）→ "
                            + $"size=({fixedBounds.size.x:F4},{fixedBounds.size.y:F4},"
                            + $"{fixedBounds.size.z:F4})");
                    }

                    string outPath = Path.Combine(dataDir, fbxName + ".zmesh");
                    Bake(bakeMesh, outPath);
                    Debug.Log($"[ZMeshBaker][Gallery] {fbxName}（顶点 {bakeMesh.vertexCount} / 索引 "
                        + $"{indexCount}）已烘焙 -> {outPath}");
                    if (standUpCopy != null)
                    {
                        Object.DestroyImmediate(standUpCopy);
                    }
                    ++baked;
                }

                Debug.Log($"[ZMeshBaker][Gallery] 完成：{baked}/4 烘焙成功");
                EditorApplication.Exit(baked == GalleryFbxNames.Length ? exitOk : exitFail);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ZMeshBaker][Gallery] 批处理失败：{ex}");
                EditorApplication.Exit(exitFail);
            }
        }
    }
}
#endif
