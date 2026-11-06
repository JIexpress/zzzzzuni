// ZRenderSceneBridge.cs —— 把 Unity 场景的物件搬进 ZRender（TASK-B-UNITY-03）。
//
// 为什么要有这一层：ZRender 的共享纹理路径已经通了（B-UNITY-02），但原生帧内容
// 是三个硬编码 cube。开 -zrender 后要看到**真实的游戏场景**，就必须有人每帧把
// "相机矩阵 + 这个物件用哪个 mesh + 它的世界矩阵 + 它的颜色"交给原生侧。
//
// 每帧（Render）：
//   1) 相机：worldToCameraMatrix + GL.GetGPUProjectionMatrix(projectionMatrix, false)
//      → 归一化（见 NormalizeProjection）→ 按元素转置成行主序 float[16]
//   2) 剔除：GeometryUtility.CalculateFrustumPlanes + TestPlanesAABB（CPU 近似）
//   3) 提交：SetCamera → BeginScene → 逐可见物件 DrawMesh → EndScene
//   （RenderFrame 由管线在之后调用——它消费本帧提交的绘制表）
//
// 性能底线（任务简报 §3）：
//   - mesh 只上传一次：按 Mesh.GetInstanceID() 缓存句柄，顶点/索引转换也只做一次；
//   - 每帧不 new 数组：矩阵/颜色/顶点打包全部复用成员缓冲；
//   - 不每帧 FindObjectsOfType：启动时扫一次，之后每 RescanIntervalFrames 补扫
//     （运行期会生成物件——敌人/掉落物——完全不扫会把它们漏掉，而每帧扫就是
//      简报明令禁止的做法，故取 2 秒一次的折中）。
//
// 与 URP 的关系：本桥只在 ZRender 管线里被调用；关掉 -zrender 时整条路径不存在。

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using ZFrame.Render.Bindings;

namespace ZFrame.Render
{
    /// <summary>场景提交桥（每帧把可见物件交给 ZRender）。</summary>
    internal sealed class ZRenderSceneBridge
    {
        /// <summary>补扫间隔（帧）：运行期新生成的物件最多 2 秒后出现在画面里。</summary>
        private const int RescanIntervalFrames = 120;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        /// <summary>一个可见候选（扫描期建好，逐帧只读）。</summary>
        private struct Entry
        {
            public Renderer Renderer;
            public Mesh Mesh;
        }

        private readonly List<Entry> _entries = new List<Entry>(256);
        private readonly Dictionary<int, ulong> _meshHandles = new Dictionary<int, ulong>(128);
        private readonly HashSet<int> _failedMeshes = new HashSet<int>();

        // 逐帧复用缓冲（禁止每帧分配）
        private readonly Plane[] _planes = new Plane[6];
        private readonly float[] _view = new float[16];
        private readonly float[] _proj = new float[16];
        private readonly float[] _world = new float[16];
        private readonly float[] _rgba = new float[4];
        private readonly List<Vector3> _positions = new List<Vector3>(2048);
        private readonly List<Vector3> _normals = new List<Vector3>(2048);
        private readonly List<Vector2> _uvs = new List<Vector2>(2048);
        private readonly List<int> _triangles = new List<int>(8192);
        private float[] _vertexScratch = new float[8 * 2048];
        private uint[] _indexScratch = new uint[3 * 8192];
        private byte[] _rawVertexBytes = new byte[64 * 1024]; // GPU 读回用的原始字节
        private ushort[] _index16 = new ushort[3 * 8192];
        private uint[] _index32 = new uint[3 * 8192];

        private int _nextScanFrame;
        private bool _scanned;
        private string _lastScanSummary = string.Empty;
        private bool _submitErrorLogged;
        private bool _loggedFirstSubmit;

        /// <summary>最近一帧提交的绘制项数（0 = 本帧没有可见物件）。</summary>
        public int LastDrawCount { get; private set; }

        /// <summary>本帧提交结果是否成功（false = 原生拒绝，管线仍会 RenderFrame 并走 cube 回落）。</summary>
        public bool LastSubmitOk { get; private set; }

        /// <summary>已上传的 mesh 种类数（诊断：应与场景里不同 mesh 的数量一致）。</summary>
        public int UploadedMeshCount => _meshHandles.Count;

        /// <summary>
        /// 提交一帧。camera 为空时返回 false。永不抛出：原生符号缺失/句柄失效都降级为
        /// false + 一次日志（管线随后 RenderFrame 会画回落 cube，不至于黑屏）。
        /// </summary>
        public bool Submit(Camera camera)
        {
            LastSubmitOk = false;
            LastDrawCount = 0;
            if (camera == null)
            {
                return false;
            }

            try
            {
                if (!_scanned || Time.frameCount >= _nextScanFrame)
                {
                    Rescan();
                }

                ZrSceneMath.ToNativeRowMajor(camera.worldToCameraMatrix, _view);

                // renderIntoTexture=false：目标是我们自己的纹理，最终由 ZRenderBlit 上屏。
                // GetGPUProjectionMatrix 会把 Unity 的 GL 约定（z∈[-1,1]）转成平台约定，
                // 但 D3D 上它同时带 Y 翻转与反向 Z —— 我们的 PSO 固定 LESS + 清 1.0，
                // 且纹理以"第 0 行 = 画面顶部"交给 Blit，所以这里把两者都归一化掉。
                // （归一化与转置的实现在 ZrSceneMath，被 EditMode 单测逐值钉住。）
                Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false);
                gpuProjection = ZrSceneMath.NormalizeProjection(gpuProjection, camera.nearClipPlane,
                    camera.farClipPlane);
                ZrSceneMath.ToNativeRowMajor(gpuProjection, _proj);

                if (ZrUnitySceneNative.SetCamera(_view, _proj) != ZrUnitySceneNative.Ok)
                {
                    return false;
                }

                if (ZrUnitySceneNative.BeginScene() != ZrUnitySceneNative.Ok)
                {
                    return false;
                }

                GeometryUtility.CalculateFrustumPlanes(camera, _planes);

                int drawn = 0;
                int skippedDisabled = 0;
                int skippedLayer = 0;
                int skippedFrustum = 0;
                int skippedMesh = 0;
                int cullingMask = camera.cullingMask;
                for (int i = 0; i < _entries.Count; i++)
                {
                    Entry entry = _entries[i];
                    Renderer renderer = entry.Renderer;
                    if (renderer == null)
                    {
                        continue;
                    }

                    // 可见性：几何剔除就够了。**刻意不用 Renderer.isVisible**——在自研
                    // SRP 里 Unity 的可见性位由内置剔除流程驱动，管线不调 Cull 时它可能
                    // 恒为 false，照它判断会把整个场景画没（简报 §已知风险 4 的兜底）。
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    {
                        skippedDisabled++;
                        continue;
                    }

                    if ((cullingMask & (1 << renderer.gameObject.layer)) == 0)
                    {
                        skippedLayer++;
                        continue;
                    }

                    if (!ZrSceneMath.IsVisible(_planes, renderer.bounds))
                    {
                        skippedFrustum++;
                        continue;
                    }

                    ulong handle = GetOrCreateMeshHandle(entry.Mesh);
                    if (handle == 0UL)
                    {
                        skippedMesh++;
                        continue;
                    }

                    ZrSceneMath.ToNativeRowMajor(renderer.localToWorldMatrix, _world);
                    FillColor(renderer, _rgba);
                    if (ZrUnitySceneNative.DrawMesh(handle, _world, _rgba) != ZrUnitySceneNative.Ok)
                    {
                        continue;
                    }

                    drawn++;
                }

                ZrUnitySceneNative.EndScene();
                LastDrawCount = drawn;
                LastSubmitOk = drawn > 0;
                if (!_loggedFirstSubmit && drawn > 0)
                {
                    _loggedFirstSubmit = true;
                    Debug.Log($"[ZRender] 场景提交生效：本帧 {drawn} 个物件，" +
                              $"已上传 mesh {_meshHandles.Count} 份（每份只传一次）。");
                }

                if (drawn == 0 && _entries.Count > 0 && Time.frameCount % 300 == 0)
                {
                    Debug.Log($"[ZRender] 本帧无绘制：entries={_entries.Count} 禁用={skippedDisabled} " +
                              $"层={skippedLayer} 视锥={skippedFrustum} 无可用mesh={skippedMesh}");
                }

                return LastSubmitOk;
            }
            catch (Exception error)
            {
                if (!_submitErrorLogged)
                {
                    _submitErrorLogged = true;
                    Debug.LogError($"[ZRender] 场景桥不可用（{error.GetType().Name}: {error.Message}）" +
                                   "——本帧回落到 cube 演示。");
                }

                return false;
            }
        }

        /// <summary>释放所有 mesh 句柄（管线销毁时调用；原生侧幂等）。</summary>
        public void Dispose()
        {
            try
            {
                foreach (KeyValuePair<int, ulong> pair in _meshHandles)
                {
                    ZrUnitySceneNative.DestroyMesh(pair.Value);
                }
            }
            catch (Exception)
            {
                // dll 已卸载 / 符号缺失：销毁路径不再抛
            }

            _meshHandles.Clear();
            _failedMeshes.Clear();
            _entries.Clear();
            _scanned = false;
        }

        // ---- 物件收集 ---------------------------------------------------------------

        private void Rescan()
        {
            _nextScanFrame = Time.frameCount + RescanIntervalFrames;
            _scanned = true;
            _entries.Clear();

            CollectRenderers<MeshRenderer>();
            CollectRenderers<SkinnedMeshRenderer>();

            int readable = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Mesh != null && _entries[i].Mesh.isReadable)
                {
                    readable++;
                }
            }

            // 只在构成变化时打一行：否则每 2 秒一条会把 Player.log 刷满
            string summary = $"renderer={_entries.Count} 可读={readable} 已上传={_meshHandles.Count}";
            if (!string.Equals(summary, _lastScanSummary, StringComparison.Ordinal))
            {
                _lastScanSummary = summary;
                Debug.Log($"[ZRender] 场景扫描：{summary}");
            }
        }

        /// <summary>
        /// 收集一类 renderer。FindObjectsOfType(true) 含未激活物件（由后面的
        /// activeInHierarchy 过滤），代价是一次 -2 秒，不在每帧路径上。
        /// </summary>
        private void CollectRenderers<T>() where T : Renderer
        {
            T[] found = UnityEngine.Object.FindObjectsOfType<T>(true);
            for (int i = 0; i < found.Length; i++)
            {
                T renderer = found[i];
                Mesh mesh = GetMesh(renderer);
                if (mesh == null)
                {
                    continue;
                }

                Entry entry;
                entry.Renderer = renderer;
                entry.Mesh = mesh;
                _entries.Add(entry);
            }
        }

        private static Mesh GetMesh(Renderer renderer)
        {
            if (renderer is MeshRenderer meshRenderer)
            {
                MeshFilter filter = meshRenderer.GetComponent<MeshFilter>();
                return (filter != null) ? filter.sharedMesh : null;
            }

            if (renderer is SkinnedMeshRenderer skinned)
            {
                return skinned.sharedMesh;
            }

            return null;
        }

        // ---- mesh 上传（每份只做一次） ----------------------------------------------

        private ulong GetOrCreateMeshHandle(Mesh mesh)
        {
            if (mesh == null)
            {
                return 0UL;
            }

            int id = mesh.GetInstanceID();
            if (_meshHandles.TryGetValue(id, out ulong cached))
            {
                return cached;
            }

            if (_failedMeshes.Contains(id))
            {
                return 0UL;
            }

            // Player 里导入的模型默认没开 Read/Write：mesh.vertices/GetVertices 拿不到
            // CPU 顶点。但 GPU 顶点缓冲能读回（GraphicsBuffer.GetData 实测可行），所以
            // 不可读网格走"读回 → 按顶点属性布局解析 → 重打包 32B/顶点"的路子；
            // 代价只在首次上传时发生一次，不是每帧。
            ulong handle;
            int result;
            try
            {
                result = mesh.isReadable ? UploadFromCpu(mesh, out handle)
                                         : UploadFromGpu(mesh, out handle);
            }
            catch (Exception error)
            {
                // 单个网格出错（读回失败/布局不支持）不能打断整帧：记一次、跳过它
                _failedMeshes.Add(id);
                Debug.LogWarning($"[ZRender] 上传网格 '{mesh.name}' 抛异常（{error.GetType().Name}: " +
                                 $"{error.Message}），已跳过。");
                return 0UL;
            }

            if (result != ZrUnitySceneNative.Ok)
            {
                _failedMeshes.Add(id);
                Debug.LogWarning($"[ZRender] 上传网格 '{mesh.name}' 失败（{result}），已跳过。");
                return 0UL;
            }

            _meshHandles.Add(id, handle);
            return handle;
        }

        /// <summary>
        /// 可读网格：顶点/索引 → 32B/顶点的紧凑数组（pos3f + normal3f + uv2f，与
        /// first_draw.hlsl 的输入布局同源），然后一次性交给原生。
        /// </summary>
        private int UploadFromCpu(Mesh mesh, out ulong handle)
        {
            handle = 0UL;
            mesh.GetVertices(_positions);
            mesh.GetNormals(_normals);
            mesh.GetUVs(0, _uvs);
            mesh.GetTriangles(_triangles, 0);

            int vertexCount = _positions.Count;
            int indexCount = _triangles.Count;
            if (vertexCount == 0 || indexCount == 0)
            {
                return ZrUnitySceneNative.ErrArgs;
            }

            // 8 float/顶点（strideFloats = 8 → 32B）；打包实现见 ZrSceneMath.PackVertices
            //（pos3f + normal3f 归一化 + uv2f，被 EditMode 单测逐值钉住）。
            if (_vertexScratch.Length < vertexCount * ZrSceneMath.VertexStrideFloats)
            {
                _vertexScratch = new float[vertexCount * ZrSceneMath.VertexStrideFloats];
            }

            if (ZrSceneMath.PackVertices(_positions, _normals, _uvs, _vertexScratch) != vertexCount)
            {
                return ZrUnitySceneNative.ErrArgs;
            }

            if (_indexScratch.Length < indexCount)
            {
                _indexScratch = new uint[indexCount];
            }

            for (int i = 0; i < indexCount; i++)
            {
                _indexScratch[i] = (uint)_triangles[i];
            }

            return ZrUnitySceneNative.CreateMesh(_vertexScratch, (uint)vertexCount,
                ZrSceneMath.VertexStrideFloats, _indexScratch, (uint)indexCount, out handle);
        }

        /// <summary>
        /// 不可读网格：从 GPU 顶点/索引缓冲读回，按 <see cref="Mesh.GetVertexAttributes"/>
        /// 描述的实际布局解析（导入模型常见 Float32x3 pos + Float16x4 normal +
        /// Float16x2 uv，stride 36），重新打包成我们的 32B/顶点布局后上传。
        /// 只在上传时发生一次。
        /// </summary>
        private int UploadFromGpu(Mesh mesh, out ulong handle)
        {
            handle = 0UL;
            int stride = mesh.GetVertexBufferStride(0);
            if (stride <= 0)
            {
                return ZrUnitySceneNative.ErrArgs;
            }

            GraphicsBuffer vertexBuffer = null;
            GraphicsBuffer indexBuffer = null;
            try
            {
                vertexBuffer = mesh.GetVertexBuffer(0);
                if (vertexBuffer == null)
                {
                    return ZrUnitySceneNative.ErrArgs;
                }

                int vertexCount = vertexBuffer.count;
                if (vertexCount == 0)
                {
                    return ZrUnitySceneNative.ErrArgs;
                }

                int byteCount = vertexCount * stride;
                // GraphicsBuffer.GetData 要求数组长度**精确等于**缓冲字节数（多一个字节都会抛
                // ArgumentException），所以这里不能"预分配大一点复用"，只能按需换一块。
                if (_rawVertexBytes.Length != byteCount)
                {
                    _rawVertexBytes = new byte[byteCount];
                }

                vertexBuffer.GetData(_rawVertexBytes);

                // ---- 属性布局（同一 stream 内按声明顺序紧凑排布）------------------------
                VertexAttributeDescriptor[] attributes = mesh.GetVertexAttributes();
                int[] streamCursor = new int[4];
                int positionOffset = -1;
                int normalOffset = -1;
                int uvOffset = -1;
                VertexAttributeFormat positionFormat = VertexAttributeFormat.Float32;
                VertexAttributeFormat normalFormat = VertexAttributeFormat.Float32;
                VertexAttributeFormat uvFormat = VertexAttributeFormat.Float32;
                for (int i = 0; i < attributes.Length; i++)
                {
                    VertexAttributeDescriptor descriptor = attributes[i];
                    if (descriptor.stream != 0)
                    {
                        continue; // 多 stream 布局不在本期范围（常规导入模型都是单 stream）
                    }

                    int size = FormatSize(descriptor.format) * descriptor.dimension;
                    if (size <= 0)
                    {
                        return ZrUnitySceneNative.ErrArgs; // 未知格式：宁可跳过也不画错
                    }

                    int offset = streamCursor[0];
                    streamCursor[0] += size;
                    switch (descriptor.attribute)
                    {
                        case VertexAttribute.Position:
                            positionOffset = offset;
                            positionFormat = descriptor.format;
                            break;
                        case VertexAttribute.Normal:
                            normalOffset = offset;
                            normalFormat = descriptor.format;
                            break;
                        case VertexAttribute.TexCoord0:
                            uvOffset = offset;
                            uvFormat = descriptor.format;
                            break;
                        default:
                            break; // Tangent/Color 等我们不消费
                    }
                }

                if (positionOffset < 0)
                {
                    return ZrUnitySceneNative.ErrArgs;
                }

                if (_vertexScratch.Length < vertexCount * 8)
                {
                    _vertexScratch = new float[vertexCount * 8];
                }

                for (int v = 0; v < vertexCount; v++)
                {
                    int source = v * stride;
                    int destination = v * 8;
                    _vertexScratch[destination] = ReadComponent(_rawVertexBytes, source + positionOffset,
                        positionFormat, 0);
                    _vertexScratch[destination + 1] = ReadComponent(_rawVertexBytes,
                        source + positionOffset, positionFormat, 1);
                    _vertexScratch[destination + 2] = ReadComponent(_rawVertexBytes,
                        source + positionOffset, positionFormat, 2);
                    if (normalOffset >= 0)
                    {
                        _vertexScratch[destination + 3] = ReadComponent(_rawVertexBytes,
                            source + normalOffset, normalFormat, 0);
                        _vertexScratch[destination + 4] = ReadComponent(_rawVertexBytes,
                            source + normalOffset, normalFormat, 1);
                        _vertexScratch[destination + 5] = ReadComponent(_rawVertexBytes,
                            source + normalOffset, normalFormat, 2);
                    }
                    else
                    {
                        _vertexScratch[destination + 4] = 1f; // 缺法线给 +Y，别给零向量（会发黑）
                    }

                    if (uvOffset >= 0)
                    {
                        _vertexScratch[destination + 6] = ReadComponent(_rawVertexBytes, source + uvOffset,
                            uvFormat, 0);
                        _vertexScratch[destination + 7] = ReadComponent(_rawVertexBytes, source + uvOffset,
                            uvFormat, 1);
                    }
                }

                // ---- 索引 ---------------------------------------------------------------
                indexBuffer = mesh.GetIndexBuffer();
                if (indexBuffer == null || indexBuffer.count == 0)
                {
                    return ZrUnitySceneNative.ErrArgs;
                }

                int indexCount = indexBuffer.count;
                if (_indexScratch.Length < indexCount)
                {
                    _indexScratch = new uint[indexCount];
                }

                if (indexBuffer.stride == 4)
                {
                    // 同顶点缓冲：GetData 的数组长度必须精确匹配
                    if (_index32.Length != indexCount)
                    {
                        _index32 = new uint[indexCount];
                    }

                    indexBuffer.GetData(_index32);
                    Array.Copy(_index32, _indexScratch, indexCount);
                }
                else
                {
                    if (_index16.Length != indexCount)
                    {
                        _index16 = new ushort[indexCount];
                    }

                    indexBuffer.GetData(_index16);
                    for (int i = 0; i < indexCount; i++)
                    {
                        _indexScratch[i] = _index16[i];
                    }
                }

                Debug.Log($"[ZRender] 网格 '{mesh.name}' 走 GPU 读回：{vertexCount} 顶点 " +
                          $"stride={stride} 索引={indexCount}（一次上传，之后复用）。");
                return ZrUnitySceneNative.CreateMesh(_vertexScratch, (uint)vertexCount, 8u,
                    _indexScratch, (uint)indexCount, out handle);
            }
            finally
            {
                vertexBuffer?.Release();
                indexBuffer?.Release();
            }
        }

        /// <summary>顶点属性格式的"每分量字节数"；未知格式返回 0（调用方据此跳过）。</summary>
        private static int FormatSize(VertexAttributeFormat format)
        {
            switch (format)
            {
                case VertexAttributeFormat.Float32:
                case VertexAttributeFormat.UInt32:
                case VertexAttributeFormat.SInt32:
                    return 4;
                case VertexAttributeFormat.Float16:
                case VertexAttributeFormat.UNorm16:
                case VertexAttributeFormat.SNorm16:
                case VertexAttributeFormat.UInt16:
                case VertexAttributeFormat.SInt16:
                    return 2;
                case VertexAttributeFormat.UNorm8:
                case VertexAttributeFormat.SNorm8:
                case VertexAttributeFormat.UInt8:
                case VertexAttributeFormat.SInt8:
                    return 1;
                default:
                    return 0;
            }
        }

        /// <summary>从紧凑顶点流里取第 component 个分量，统一转成 float。</summary>
        private static float ReadComponent(byte[] data, int attributeOffset,
            VertexAttributeFormat format, int component)
        {
            int offset = attributeOffset + (component * FormatSize(format));
            switch (format)
            {
                case VertexAttributeFormat.Float32:
                    return BitConverter.ToSingle(data, offset);
                case VertexAttributeFormat.Float16:
                    return HalfToFloat((ushort)(data[offset] | (data[offset + 1] << 8)));
                case VertexAttributeFormat.UNorm8:
                    return data[offset] / 255f;
                case VertexAttributeFormat.SNorm8:
                    return Mathf.Max((sbyte)data[offset] / 127f, -1f);
                case VertexAttributeFormat.UNorm16:
                    return (data[offset] | (data[offset + 1] << 8)) / 65535f;
                case VertexAttributeFormat.SNorm16:
                    return (short)(data[offset] | (data[offset + 1] << 8)) / 32767f;
                default:
                    return 0f;
            }
        }

        /// <summary>IEEE half → float（.NET Standard 2.0 没有 System.Half）。</summary>
        private static float HalfToFloat(ushort half)
        {
            int sign = (half >> 15) & 0x1;
            int exponent = (half >> 10) & 0x1F;
            int mantissa = half & 0x3FF;
            float value;
            if (exponent == 0)
            {
                value = mantissa * Mathf.Pow(2f, -24f);
            }
            else if (exponent == 0x1F)
            {
                value = (mantissa == 0) ? float.PositiveInfinity : float.NaN;
            }
            else
            {
                value = (1f + (mantissa / 1024f)) * Mathf.Pow(2f, exponent - 15);
            }

            return (sign == 0) ? value : -value;
        }

        // ---- 颜色 -------------------------------------------------------------------
        // 矩阵转换 / 投影归一化 / 顶点打包 / 剔除判断全部在 ZrSceneMath（纯函数，被
        // EditMode 单测逐值钉住；这里不再保留副本，避免"测的是副本、跑的是另一份"）。

        /// <summary>物件基色：优先 URP 的 _BaseColor，其次内置的 _Color，拿不到用白。</summary>
        private static void FillColor(Renderer renderer, float[] rgba)
        {
            Color color = Color.white;
            Material material = renderer.sharedMaterial;
            if (material != null)
            {
                if (material.HasProperty(BaseColorId))
                {
                    color = material.GetColor(BaseColorId);
                }
                else if (material.HasProperty(ColorId))
                {
                    color = material.GetColor(ColorId);
                }
            }

            rgba[0] = color.r;
            rgba[1] = color.g;
            rgba[2] = color.b;
            rgba[3] = color.a;
        }
    }
}
