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
using ZFrame.Render.Bindings;

namespace ZFrame.Render
{
    /// <summary>场景桥的纯数学部分：矩阵主序、投影归一化、顶点打包、剔除包装。</summary>
    internal static class ZrSceneMath
    {
        /// <summary>
        /// TASK-B-PLANET-07：行星通道启用帧的物件世界矩阵相机相对化——
        /// world' = T(−cam)·world（只有平移列变化）。当 SetCamera 收到 viewRel
        /// （纯旋转，平移恰为 0）时，物件平移必须由世界矩阵自己减掉相机 Unity 坐标
        /// （物件与相机都在原点附近，幅值 ≤4km，ULP 0.488mm——A-WORLD-01 §6.4-4 容差
        /// 口径），否则物件会被平移 R^T·cam 出画面。平移列 = 世界位置 − 相机位置：
        /// localToWorld 的平移列恒等于物件世界位置（任意父子层级），T(−cam) 只改它。
        /// </summary>
        internal static Matrix4x4 ToCameraRelativeWorld(Matrix4x4 localToWorld, Vector3 cameraUnityPosition)
        {
            localToWorld.m03 -= cameraUnityPosition.x;
            localToWorld.m13 -= cameraUnityPosition.y;
            localToWorld.m23 -= cameraUnityPosition.z;
            return localToWorld;
        }

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

        // =====================================================================
        // TASK-B-VIS-02：真视锥剔除纯函数（B-VIS-01 遗留的"逐视图真 frustum"缺口）
        // ---------------------------------------------------------------------
        // 语义与位序都复用 DataKernel 的 Mask128（ZkMask 即其托管形态）：
        //   视图索引 i → 第 i 位（i < 64 落 Lo，否则落 Hi），与原生
        //   Mask128::Has/Set 的 (i >> 6, i & 63) 完全一致。
        // 一个物件一次调用产出"哪几个视图看得见"的 128 位掩码，写回共享
        // VisibilitySet 的 viewMask 语义——不是每视图各剔除一次。
        //
        // 边界口径（可证伪，逐条有测试）：
        //   · 球与某平面恰好相切（d == -r）→ 判可见（闭集，严格 `<` 才剔除）
        //   · radius == 0 → 退化为点测试；radius < 0 → 按 0 处理
        //   · 任一输入 NaN / 无穷 / 平面退化 → 该视图判不可见（fail-closed，
        //     宁可不画也不画错；NaN 参与比较恒 false 会误判可见，必须显式挡）
        // =====================================================================

        /// <summary>掩码可表达的视图上限（128 位，与 Mask128 一致）。</summary>
        internal const int MaxViewCount = 128;

        /// <summary>一个视图的六个世界空间平面（法线朝视锥内侧且已归一化）。</summary>
        internal struct ZrViewFrustum
        {
            internal Plane Left;
            internal Plane Right;
            internal Plane Bottom;
            internal Plane Top;
            internal Plane Near;
            internal Plane Far;

            /// <summary>
            /// 由 view*projection（列向量约定 clip = M·v）提取六平面，Gribb-Hartmann：
            /// 左 = 行3+行0、右 = 行3−行0、下 = 行3+行1、上 = 行3−行1、
            /// 近 = 行3+行2、远 = 行3−行2（D3D [0,w] 与 GL [-w,w] 两套裁剪体均适用）。
            /// </summary>
            internal static ZrViewFrustum FromViewProjection(Matrix4x4 viewProjection)
            {
                ZrViewFrustum frustum;
                frustum.Left = PlaneFromRows(viewProjection, 1f, 0);
                frustum.Right = PlaneFromRows(viewProjection, -1f, 0);
                frustum.Bottom = PlaneFromRows(viewProjection, 1f, 1);
                frustum.Top = PlaneFromRows(viewProjection, -1f, 1);
                frustum.Near = PlaneFromRows(viewProjection, 1f, 2);
                frustum.Far = PlaneFromRows(viewProjection, -1f, 2);
                return frustum;
            }

            /// <summary>
            /// 由外部给定的六平面直接构造（顺序同
            /// <see cref="GeometryUtility.CalculateFrustumPlanes(UnityEngine.Camera, Plane[])"/>：
            /// 左/右/下/上/近/远，法线朝内）。接线（B-VIS-02）走这条：
            /// 复用相机自己那六个平面 → 与旧路径 <see cref="IsVisible"/> **同一份数**，
            /// 不存在"两套提取各算一遍"的浮点差；平面数不足 6 → 缺的位是 NaN 平面，
            /// <see cref="IsValid"/> 随之 fail-closed。
            /// </summary>
            internal static ZrViewFrustum FromPlanes(Plane[] planes)
            {
                ZrViewFrustum frustum;
                frustum.Left = PlaneAt(planes, 0);
                frustum.Right = PlaneAt(planes, 1);
                frustum.Bottom = PlaneAt(planes, 2);
                frustum.Top = PlaneAt(planes, 3);
                frustum.Near = PlaneAt(planes, 4);
                frustum.Far = PlaneAt(planes, 5);
                return frustum;
            }

            private static Plane PlaneAt(Plane[] planes, int index)
            {
                if (planes == null || index < 0 || index >= planes.Length)
                {
                    return new Plane(Vector3.zero, float.NaN); // 缺面 → 视图非法（fail-closed）
                }

                return planes[index];
            }

            /// <summary>六平面全部有限且非退化（否则本视图整帧 fail-closed）。</summary>
            internal bool IsValid
            {
                get
                {
                    return IsFinitePlane(Left) && IsFinitePlane(Right) && IsFinitePlane(Bottom)
                        && IsFinitePlane(Top) && IsFinitePlane(Near) && IsFinitePlane(Far);
                }
            }
        }

        /// <summary>
        /// 一次调用算出"哪些视图能看见这个包围球"，返回 128 位掩码（位序见上方注释）。
        /// 纯函数、无分配、无 Unity 场景依赖；viewCount 超 128 的部分被截断（不置位）。
        /// </summary>
        internal static ZkMask CullToViewMask(ZrViewFrustum[] views, int viewCount, Vector3 center,
            float radius)
        {
            var mask = default(ZkMask);
            if (views == null || viewCount <= 0 || !IsFiniteVector(center) || !IsFinite(radius))
            {
                return mask; // fail-closed：坏输入不画
            }

            if (radius < 0f)
            {
                radius = 0f; // 退化半径按点处理（负半径无几何意义）
            }

            int count = viewCount < MaxViewCount ? viewCount : MaxViewCount;
            for (int view = 0; view < count; view++)
            {
                if (IntersectsSphere(views[view], center, radius))
                {
                    mask.Set((uint)view); // bit = 视图索引，与 Mask128 同位序
                }
            }

            return mask;
        }

        /// <summary>
        /// 批量版本（**包围盒**）：一次调用算出"哪些视图能看见这个 AABB"，返回 128 位掩码。
        ///
        /// 为什么需要这个重载（B-VIS-02 接线的关键，别删）：包围球版本用 r = |extents|
        /// 当半径，而 AABB 在平面法线上的真实投影半径是 Σ|n_i|·e_i ≤ |n|·|extents|
        /// （Cauchy-Schwarz），所以球版本是 AABB 的**超集**——拿它替换
        /// <see cref="IsVisible"/> 会在视锥边缘多画物件（drawn 数量、像素基线都会变）。
        /// 本重载逐面复算 Σ|n_i|·e_i，判据与 <see cref="GeometryUtility.TestPlanesAABB"/>
        /// 完全同式（d + r &gt;= 0 判可见，相切可见），故接线后 drawn / skippedFrustum
        /// 与接线前逐位一致（等价性用例：ZrViewCullingEquivalenceTests）。
        /// </summary>
        internal static ZkMask CullToViewMask(ZrViewFrustum[] views, int viewCount, Bounds bounds)
        {
            var mask = default(ZkMask);
            if (views == null || viewCount <= 0 || !IsFiniteVector(bounds.center)
                || !IsFiniteVector(bounds.extents))
            {
                return mask; // fail-closed：坏输入不画
            }

            int count = viewCount < MaxViewCount ? viewCount : MaxViewCount;
            for (int view = 0; view < count; view++)
            {
                if (IntersectsBox(views[view], bounds))
                {
                    mask.Set((uint)view);
                }
            }

            return mask;
        }

        /// <summary>
        /// AABB 与单个视图是否相交（保守：相交/相切/包含都算可见）。NaN 与非有限平面 → false。
        /// 与 <see cref="IntersectsSphere"/> 同一套平面判据，只是半径换成 AABB 的投影半径。
        /// </summary>
        internal static bool IntersectsBox(ZrViewFrustum view, Bounds bounds)
        {
            if (!view.IsValid || !IsFiniteVector(bounds.center) || !IsFiniteVector(bounds.extents))
            {
                return false;
            }

            return IsInsideBox(view.Left, bounds) && IsInsideBox(view.Right, bounds)
                && IsInsideBox(view.Bottom, bounds) && IsInsideBox(view.Top, bounds)
                && IsInsideBox(view.Near, bounds) && IsInsideBox(view.Far, bounds);
        }

        /// <summary>
        /// 盒是否在该平面的内侧：d + r &gt;= 0（d = 中心有符号距离，r = Σ|n_i|·e_i）。
        /// 与 GeometryUtility.TestPlanesAABB 同式同序（含 "相切可见" 的闭集口径）。
        /// </summary>
        private static bool IsInsideBox(Plane plane, Bounds bounds)
        {
            Vector3 normal = plane.normal;
            Vector3 center = bounds.center;
            Vector3 extents = bounds.extents;
            float distance = normal.x * center.x + normal.y * center.y + normal.z * center.z
                + plane.distance;
            float radius = extents.x * Mathf.Abs(normal.x) + extents.y * Mathf.Abs(normal.y)
                + extents.z * Mathf.Abs(normal.z);
            if (!IsFinite(distance) || !IsFinite(radius))
            {
                return false; // 溢出/NaN：不可见（fail-closed）
            }

            return distance + radius >= 0f;
        }

        /// <summary>批量剔除的批大小：一批一个 128 位字，与掩码物理宽度同源。</summary>
        internal const int CullBatchSize = MaxViewCount;

        /// <summary>
        /// 单视图批量剔除（**分块**）：把 <paramref name="bounds"/> 里每个条目喂一次
        /// <see cref="CullToViewMask(ZrViewFrustum[], int, Bounds)"/>，只看第 0 位（接线是单视图），
        /// 结果写进 <paramref name="visible"/>（调用方的复用缓冲）。返回可见条目数。
        ///
        /// 分块口径（不许省）：每 <see cref="CullBatchSize"/>=128 条一批，批内条目 j 攒成
        /// 一个 128 位字（j&lt;64 → Lo 的第 j 位，否则 Hi 的第 j-64 位，与 Mask128 同布局），
        /// 因此条目数 &gt;128 时是**多批**而不是静默截断/丢物件；批边界（127/128/255/256…）
        /// 由 ZrViewCullingEquivalenceTests 的 384 条用例钉住。
        /// </summary>
        internal static int CullToViewVisibleFlags(ZrViewFrustum[] views, int viewCount,
            IList<Bounds> bounds, bool[] visible)
        {
            if (bounds == null || visible == null || visible.Length < bounds.Count)
            {
                return 0; // 容量不足：宁可不画（fail-closed），也不写越界
            }

            int visibleCount = 0;
            int total = bounds.Count;
            for (int start = 0; start < total; start += CullBatchSize)
            {
                int count = total - start;
                if (count > CullBatchSize)
                {
                    count = CullBatchSize; // 一批最多 128 条 = 一个掩码字
                }

                ulong batchLo = 0ul;
                ulong batchHi = 0ul;
                for (int j = 0; j < count; j++)
                {
                    if (!CullToViewMask(views, viewCount, bounds[start + j]).Has(0u))
                    {
                        continue; // 单视图接线：只看视图 0 的那一位
                    }

                    if (j < 64)
                    {
                        batchLo |= 1ul << j;
                    }
                    else
                    {
                        batchHi |= 1ul << (j - 64);
                    }

                    visibleCount++;
                }

                // 阶段二只读掩码：批内第 j 位为 1 的条目才允许进入后续检查。
                for (int j = 0; j < count; j++)
                {
                    bool on = j < 64
                        ? ((batchLo >> j) & 1ul) != 0ul
                        : ((batchHi >> (j - 64)) & 1ul) != 0ul;
                    visible[start + j] = on;
                }
            }

            return visibleCount;
        }

        /// <summary>
        /// 球与单个视图是否相交（保守：相交/相切/包含都算可见）。NaN 与非有限平面 → false。
        /// </summary>
        internal static bool IntersectsSphere(ZrViewFrustum view, Vector3 center, float radius)
        {
            if (!view.IsValid || !IsFiniteVector(center) || !IsFinite(radius))
            {
                return false;
            }

            return IsInside(view.Left, center, radius) && IsInside(view.Right, center, radius)
                && IsInside(view.Bottom, center, radius) && IsInside(view.Top, center, radius)
                && IsInside(view.Near, center, radius) && IsInside(view.Far, center, radius);
        }

        /// <summary>球是否在该平面的内侧（d >= -r；相切算内侧）。</summary>
        private static bool IsInside(Plane plane, Vector3 center, float radius)
        {
            float distance = plane.normal.x * center.x + plane.normal.y * center.y
                + plane.normal.z * center.z + plane.distance;
            if (!IsFinite(distance))
            {
                return false; // 溢出/NaN：不可见（fail-closed）
            }

            return distance >= -radius;
        }

        /// <summary>行3 + sign·行axis → 归一化平面（法线朝内）；退化时返回 NaN 平面。</summary>
        private static Plane PlaneFromRows(Matrix4x4 matrix, float sign, int axisRow)
        {
            float a = matrix.m30 + sign * matrix[axisRow, 0];
            float b = matrix.m31 + sign * matrix[axisRow, 1];
            float c = matrix.m32 + sign * matrix[axisRow, 2];
            float d = matrix.m33 + sign * matrix[axisRow, 3];
            float length = Mathf.Sqrt((a * a) + (b * b) + (c * c));
            if (!IsFinite(length) || length < 1e-6f)
            {
                return new Plane(Vector3.zero, float.NaN); // IsValid 据此拒掉整个视图
            }

            float inverse = 1f / length;
            return new Plane(new Vector3(a * inverse, b * inverse, c * inverse), d * inverse);
        }

        private static bool IsFinitePlane(Plane plane)
        {
            return IsFinite(plane.normal.x) && IsFinite(plane.normal.y)
                && IsFinite(plane.normal.z) && IsFinite(plane.distance)
                && plane.normal.sqrMagnitude > 0.999f && plane.normal.sqrMagnitude < 1.001f;
        }

        private static bool IsFiniteVector(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
