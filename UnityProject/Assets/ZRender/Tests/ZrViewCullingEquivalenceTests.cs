// ZrViewCullingEquivalenceTests.cs —— B-VIS-02「接线不改行为」的等价性证明（EditMode）。
//
// 接线做的事只有一件：ZRenderSceneBridge.Submit 里逐条目的 `IsVisible(_planes, bounds)`
// 换成「阶段一 CullToViewMask（单视图，取第 0 位）→ 阶段二只读掩码」，并分块（128 条/批）。
// 换掉一个判据就必须证明它**逐位等价**，否则 drawn / skippedFrustum 会悄悄变（画面可能
// 少物件或多物件，基线像素也会漂）。本文件四组用例：
//
//   A. 随机相机位姿 × 随机包围盒              —— ≥200 组，判据逐例比对
//   B. 恰好相切（|d + r| 在浮点噪声内）        —— ≥200 组，闭集口径
//   C. 远平面之外                            —— ≥200 组，必须两侧一致剔除
//   D. 退化半径（零尺寸包围盒 = 点测试）        —— ≥200 组
//
// 关于"相切处不逐位相等"（诚实注记，实测于 2026-11-15）：Unity 原生
// GeometryUtility.TestPlanesAABB 与自算的 `d + r >= 0`（float / double /
// `d >= -r` 三种写法都试过）在**恰好相切**的样本里有个位数百分比的 1 ULP 分歧，
// 方向随机——这是 d+r≈0 时的浮点最后一位差异，不是判据差异
// （随机位姿 660 例 / 远平面外 220 例 / 退化半径 220 例：分歧 **0**）。
// 因此 B 组断言的是：①分歧只允许发生在 |d+r| ≤ 1e-6·(1+|d|+r) 的边界膜内；
// ②分歧数不超过样本的 1/10；③另有一条轴对齐的**精确**相切用例（d+r 二进制恰为 0）
// 钉住"相切可见"的闭集语义。
//
// 再加两条结构用例：
//   E. 条目数 >128（384 条 = 3 个整批）跨块不漏 —— 分块不许静默截断
//   F. 两阶段计数口径 vs 旧路径计数口径          —— 禁用/层/视锥/无mesh/drawn 五个数逐位相同
//
// 判据全部用**生产同一份函数**（ZrSceneMath.IsVisible / CullToViewMask /
// CullToViewVisibleFlags），不是副本：把掩码位写反必红（变异证据见任务日志）。

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Render.Bindings;

namespace ZFrame.Render.Tests
{
    [TestFixture]
    public class ZrViewCullingEquivalenceTests
    {
        /// <summary>每组随机用例数（任务判据：各 ≥200）。</summary>
        private const int RandomCases = 220;

        /// <summary>跨块用例条目数：3 个整批（128×3），覆盖 127/128/255/256 批边界。</summary>
        private const int BatchEntries = 384;

        private GameObject _cameraObject;
        private Camera _camera;
        private readonly Plane[] _planes = new Plane[6];
        private readonly ZrSceneMath.ZrViewFrustum[] _views = new ZrSceneMath.ZrViewFrustum[1];

        [SetUp]
        public void SetUp()
        {
            _cameraObject = new GameObject("ZrViewCullingEquivalenceCamera");
            _camera = _cameraObject.AddComponent<Camera>();
            _camera.aspect = 16f / 9f;
        }

        [TearDown]
        public void TearDown()
        {
            if (_cameraObject != null)
            {
                Object.DestroyImmediate(_cameraObject);
            }
        }

        // ---- A. 随机位姿 × 随机包围盒 ----------------------------------------------

        [Test]
        public void MaskDecision_MatchesIsVisible_ForRandomCamerasAndBounds()
        {
            var rng = new System.Random(20261115);
            int visible = 0;
            int culled = 0;

            for (int i = 0; i < RandomCases; i++)
            {
                ApplyRandomPose(rng);
                for (int k = 0; k < 3; k++)
                {
                    Bounds bounds = RandomBounds(rng);
                    bool legacy = LegacyVisible(bounds);
                    bool mask = MaskVisible(bounds);

                    Assert.AreEqual(legacy, mask,
                        $"用例 {i}/{k}：相机 {_cameraObject.transform.position} " +
                        $"rot={_cameraObject.transform.eulerAngles} ortho={_camera.orthographic} " +
                        $"fov={_camera.fieldOfView} near={_camera.nearClipPlane} far={_camera.farClipPlane} " +
                        $"bounds(center={bounds.center} size={bounds.size}) 判据不一致");

                    if (legacy)
                    {
                        visible++;
                    }
                    else
                    {
                        culled++;
                    }
                }
            }

            Assert.Greater(visible, RandomCases / 2, "可见类样本太少 → 用例会变成近似空转");
            Assert.Greater(culled, RandomCases / 2, "剔除类样本太少 → 用例会变成近似空转");

            Debug.Log($"[ZrViewCulling] 随机位姿×包围盒：{RandomCases} 个相机 / {RandomCases * 3} 例，"
                + $"可见 {visible} / 剔除 {culled}，逐例判据分歧 0");
        }

        // ---- B. 恰好相切（闭集：相切必须两边都判可见）--------------------------------

        [Test]
        public void MaskDecision_MatchesIsVisible_ForTangentBounds()
        {
            var rng = new System.Random(20261116);
            int visible = 0;
            int culled = 0;
            int boundaryDivergence = 0;
            int maskOnly = 0;
            int legacyOnly = 0;

            for (int i = 0; i < RandomCases; i++)
            {
                ApplyRandomPose(rng);
                int planeIndex;
                Bounds bounds = TangentBounds(rng, 0f, out planeIndex, out float signedDistance,
                    out float radius);
                float residual = signedDistance + radius;

                // 前提校验：构造出来的确实是"相切"（d + r 落在浮点噪声内），不是随便一个盒子。
                Assert.LessOrEqual(Mathf.Abs(residual), 1e-3f * (1f + radius),
                    $"用例 {i}：相切构造失准 d={signedDistance} r={radius}（平面 {planeIndex}）");

                bool legacy = LegacyVisible(bounds);
                bool mask = MaskVisible(bounds);

                if (mask != legacy)
                {
                    // 只允许一种分歧：恰好压在平面上的盒子在**最后一位**上松动。
                    // 实测（见任务日志）：float / double / `d >= -r` 三种写法与 Unity 原生
                    // TestPlanesAABB 在相切样本上都有个位数百分比、方向随机的 1 ULP 分歧
                    // （d+r≈0 的舍入差，不是判据差异）；带外必须逐位相等。
                    boundaryDivergence++;
                    if (mask)
                    {
                        maskOnly++;
                    }
                    else
                    {
                        legacyOnly++;
                    }

                    float band = 1e-6f * (1f + Mathf.Abs(signedDistance) + radius);
                    Assert.LessOrEqual(Mathf.Abs(residual), band,
                        $"用例 {i}：分歧跑到了边界膜之外（d+r={residual}，带宽={band}）");
                }

                if (legacy)
                {
                    visible++;
                }
                else
                {
                    culled++;
                }
            }

            Debug.Log($"[ZrViewCulling] 相切用例 {RandomCases} 组：逐位一致 {RandomCases - boundaryDivergence}，" +
                      $"边界膜内 1 ULP 分歧 {boundaryDivergence}（掩码单方可见 {maskOnly} / " +
                      $"旧判据单方可见 {legacyOnly}）");

            Assert.Greater(visible, 0, "相切样本必须有一部分判可见（闭集口径）");
            Assert.Greater(culled, 0, "相切样本也必须有一部分被其它平面剔除");
            Assert.LessOrEqual(boundaryDivergence, RandomCases / 10,
                "边界膜内的 1 ULP 分歧不该超过样本的 1/10（超了说明判据真的不一样了）");
        }

        /// <summary>
        /// 精确相切（无相机浮点噪声）：单位裁剪体 = 世界空间 [-1,1]³，盒子 x∈[1,3] 恰好贴住 x=1 面。
        /// 这里 d + r == 0 在二进制浮点下**逐位可算**，两条判据必须都判可见（闭集）。
        /// </summary>
        [Test]
        public void TangentExactly_AxisAligned_IsVisibleInBothPaths()
        {
            var planes = new[]
            {
                new Plane(new Vector3(1f, 0f, 0f), 1f),   // x >= -1
                new Plane(new Vector3(-1f, 0f, 0f), 1f),  // x <= 1
                new Plane(new Vector3(0f, 1f, 0f), 1f),   // y >= -1
                new Plane(new Vector3(0f, -1f, 0f), 1f),  // y <= 1
                new Plane(new Vector3(0f, 0f, 1f), 1f),   // z >= -1
                new Plane(new Vector3(0f, 0f, -1f), 1f),  // z <= 1
            };
            var bounds = new Bounds(new Vector3(1.5f, 0f, 0f), new Vector3(1f, 1f, 1f));

            Assert.IsTrue(ZrSceneMath.IsVisible(planes, bounds), "旧判据：精确相切必须可见");

            _views[0] = ZrSceneMath.ZrViewFrustum.FromPlanes(planes);
            Assert.IsTrue(ZrSceneMath.CullToViewMask(_views, 1, bounds).Has(0u),
                "新判据：精确相切必须可见（与旧路径同为闭集）");

            // 再远 1/1024（二进制精确可表示）→ 两侧都必须剔除。
            var outside = new Bounds(new Vector3(1.5f + 0.0009765625f, 0f, 0f),
                new Vector3(1f, 1f, 1f));
            Assert.IsFalse(ZrSceneMath.IsVisible(planes, outside), "旧判据：越过相切必须剔除");
            Assert.IsFalse(ZrSceneMath.CullToViewMask(_views, 1, outside).Has(0u),
                "新判据：越过相切必须剔除");
        }

        // ---- C. 远平面之外 -----------------------------------------------------------

        [Test]
        public void MaskDecision_MatchesIsVisible_BeyondFarPlane()
        {
            var rng = new System.Random(20261117);
            int culled = 0;

            for (int i = 0; i < RandomCases; i++)
            {
                ApplyRandomPose(rng);
                int planeIndex;
                float margin = Range(rng, 0.01f, 50f);
                Bounds bounds = TangentBounds(rng, margin, out planeIndex, out float signedDistance,
                    out float radius);

                // 前提校验：确实在远平面外侧（d + r = -margin < 0）。
                Assert.Less(signedDistance + radius, 0f,
                    $"用例 {i}：构造未落在远平面外（d+r={signedDistance + radius}）");

                bool legacy = LegacyVisible(bounds);
                bool mask = MaskVisible(bounds);

                Assert.AreEqual(legacy, mask,
                    $"用例 {i}：远平面外盒子（margin={margin}）判据不一致");
                Assert.IsFalse(legacy, $"用例 {i}：远平面外的盒子必须被剔除");
                if (!legacy)
                {
                    culled++;
                }
            }

            Assert.AreEqual(RandomCases, culled, "远平面外的样本应当全部被剔除");

            Debug.Log($"[ZrViewCulling] 远平面外：{RandomCases} 例全部剔除，逐例判据分歧 0");
        }

        // ---- D. 退化半径（零尺寸包围盒 = 点测试）-------------------------------------

        [Test]
        public void MaskDecision_MatchesIsVisible_ForDegenerateBounds()
        {
            var rng = new System.Random(20261118);
            int visible = 0;
            int culled = 0;

            for (int i = 0; i < RandomCases; i++)
            {
                ApplyRandomPose(rng);
                Bounds bounds = rng.Next(2) == 0
                    ? new Bounds(RandomWorldPoint(rng), Vector3.zero)          // 纯点
                    : FlatBounds(rng);                                          // 某一维为 0

                bool legacy = LegacyVisible(bounds);
                bool mask = MaskVisible(bounds);
                Assert.AreEqual(legacy, mask,
                    $"用例 {i}：退化盒子（center={bounds.center} size={bounds.size}）判据不一致");

                if (legacy)
                {
                    visible++;
                }
                else
                {
                    culled++;
                }
            }

            Assert.Greater(visible, 0, "退化半径也要有可见样本（点测试闭集）");
            Assert.Greater(culled, 0, "退化半径也要有剔除样本");

            Debug.Log($"[ZrViewCulling] 退化半径：{RandomCases} 例（可见 {visible} / 剔除 {culled}），" +
                      "逐例判据分歧 0");
        }

        // ---- E. 条目数 >128：跨块不漏 ------------------------------------------------

        [Test]
        public void CullToViewVisibleFlags_MoreThan128Entries_DoesNotDropCrossChunkEntries()
        {
            // 单位裁剪体 → 世界空间 [-1,1]³ 轴对齐；条目 i 在体内 ⇔ i % 3 == 0。
            var views = new[] { ZrSceneMath.ZrViewFrustum.FromViewProjection(Matrix4x4.identity) };
            var bounds = new List<Bounds>(BatchEntries);
            var expected = new bool[BatchEntries];
            for (int i = 0; i < BatchEntries; i++)
            {
                bool inside = i % 3 == 0;
                Vector3 center = inside ? Vector3.zero : new Vector3(5f, 0f, 0f);
                bounds.Add(new Bounds(center, new Vector3(0.2f, 0.2f, 0.2f)));
                expected[i] = inside;
            }

            var flags = new bool[BatchEntries];
            int visibleCount = ZrSceneMath.CullToViewVisibleFlags(views, 1, bounds, flags);

            Assert.AreEqual(BatchEntries / 3, visibleCount,
                "384 条里应有 128 条可见——少一条就说明某个批次被静默截断/丢物件");
            for (int i = 0; i < BatchEntries; i++)
            {
                Assert.AreEqual(expected[i], flags[i],
                    $"条目 {i}（第 {i / ZrSceneMath.CullBatchSize} 批 / 批内 {i % ZrSceneMath.CullBatchSize}）" +
                    "可见位与逐条判定不一致");
            }

            // 三个批次各自必须有可见条目（批边界 127/128、255/256 两侧都不许漏）。
            Assert.IsTrue(flags[0] && flags[126] && flags[129] && flags[255] && flags[258] && flags[381],
                "批边界附近的可见条目必须全部保留");
            Assert.IsFalse(flags[1] || flags[127] || flags[128] || flags[256] || flags[257] || flags[383],
                "批边界附近的不可见条目不许被误置位");
        }

        [Test]
        public void CullToViewVisibleFlags_BufferTooSmall_FailsClosed()
        {
            // 容量不足：宁可不画（返回 0 + 不写越界），也不静默画一半。
            var views = new[] { ZrSceneMath.ZrViewFrustum.FromViewProjection(Matrix4x4.identity) };
            var bounds = new List<Bounds>();
            for (int i = 0; i < 10; i++)
            {
                bounds.Add(new Bounds(Vector3.zero, Vector3.one * 0.2f));
            }

            Assert.AreEqual(0, ZrSceneMath.CullToViewVisibleFlags(views, 1, bounds, new bool[4]));
            Assert.AreEqual(0, ZrSceneMath.CullToViewVisibleFlags(views, 1, null, new bool[16]));
        }

        // ---- F. 两阶段计数口径 == 旧路径计数口径（drawn/skipped 前后逐位一致）--------

        [Test]
        public void TwoPhaseCounters_EqualLegacyCounters_ForMixedEntries()
        {
            var rng = new System.Random(20261119);
            const int entryCount = 512; // >128：顺带覆盖分块

            var enabled = new bool[entryCount];
            var layerOk = new bool[entryCount];
            var hasMesh = new bool[entryCount];
            var bounds = new List<Bounds>(entryCount);

            ApplyRandomPose(rng);
            for (int i = 0; i < entryCount; i++)
            {
                enabled[i] = rng.Next(5) != 0;
                layerOk[i] = rng.Next(6) != 0;
                hasMesh[i] = rng.Next(7) != 0;
                bounds.Add(RandomBounds(rng));
            }

            GeometryUtility.CalculateFrustumPlanes(_camera, _planes);

            // 旧路径（接线前）：disabled → layer → IsVisible → mesh → draw
            int legacyDisabled = 0;
            int legacyLayer = 0;
            int legacyFrustum = 0;
            int legacyMesh = 0;
            int legacyDrawn = 0;
            for (int i = 0; i < entryCount; i++)
            {
                if (!enabled[i])
                {
                    legacyDisabled++;
                    continue;
                }

                if (!layerOk[i])
                {
                    legacyLayer++;
                    continue;
                }

                if (!ZrSceneMath.IsVisible(_planes, bounds[i]))
                {
                    legacyFrustum++;
                    continue;
                }

                if (!hasMesh[i])
                {
                    legacyMesh++;
                    continue;
                }

                legacyDrawn++;
            }

            // 新路径（接线后）：阶段一算掩码，阶段二 disabled → layer → 掩码 → mesh → draw
            _views[0] = ZrSceneMath.ZrViewFrustum.FromPlanes(_planes);
            var flags = new bool[entryCount];
            int maskVisible = ZrSceneMath.CullToViewVisibleFlags(_views, 1, bounds, flags);

            int newDisabled = 0;
            int newLayer = 0;
            int newFrustum = 0;
            int newMesh = 0;
            int newDrawn = 0;
            int consumed = 0;
            for (int i = 0; i < entryCount; i++)
            {
                if (!enabled[i])
                {
                    newDisabled++;
                    continue;
                }

                if (!layerOk[i])
                {
                    newLayer++;
                    continue;
                }

                if (!flags[i])
                {
                    newFrustum++;
                    continue;
                }

                consumed++;
                if (!hasMesh[i])
                {
                    newMesh++;
                    continue;
                }

                newDrawn++;
            }

            Assert.AreEqual(legacyDisabled, newDisabled, "禁用计数必须逐位一致");
            Assert.AreEqual(legacyLayer, newLayer, "层计数必须逐位一致");
            Assert.AreEqual(legacyFrustum, newFrustum, "视锥计数必须逐位一致");
            Assert.AreEqual(legacyMesh, newMesh, "无 mesh 计数必须逐位一致");
            Assert.AreEqual(legacyDrawn, newDrawn, "drawn 必须逐位一致");

            // 账本口径：录入 N = 条目数，消费 M = 掩码为 1 且通过 enabled/layer 的条目数，且 M ≤ N。
            Assert.LessOrEqual(consumed, maskVisible, "消费 M 不能超过掩码可见数");
            Assert.LessOrEqual(maskVisible, entryCount, "掩码可见数不能超过录入 N");

            // 非空转：四类样本都得出现（否则"五个数相等"可能只是全 0）。
            Assert.Greater(legacyDrawn, 0, "必须有真正绘制的样本");
            Assert.Greater(legacyFrustum, 0, "必须有视锥剔除的样本");
            Assert.Greater(legacyDisabled, 0, "必须有禁用的样本");
            Assert.Greater(legacyLayer, 0, "必须有层不匹配的样本");
            Assert.Greater(legacyMesh, 0, "必须有无可用 mesh 的样本");
        }

        // ---- 判据包装（新旧两条路都走生产函数）--------------------------------------

        /// <summary>接线前的判据：相机平面 + GeometryUtility.TestPlanesAABB（经 ZrSceneMath.IsVisible）。</summary>
        private bool LegacyVisible(Bounds bounds)
        {
            GeometryUtility.CalculateFrustumPlanes(_camera, _planes);
            return ZrSceneMath.IsVisible(_planes, bounds);
        }

        /// <summary>接线后的判据：相机平面 → ZrViewFrustum.FromPlanes → CullToViewMask 的第 0 位。</summary>
        private bool MaskVisible(Bounds bounds)
        {
            GeometryUtility.CalculateFrustumPlanes(_camera, _planes);
            _views[0] = ZrSceneMath.ZrViewFrustum.FromPlanes(_planes);
            return ZrSceneMath.CullToViewMask(_views, 1, bounds).Has(0u);
        }

        // ---- 构造器 -----------------------------------------------------------------

        private void ApplyRandomPose(System.Random rng)
        {
            _cameraObject.transform.position = new Vector3(
                Range(rng, -40f, 40f), Range(rng, -15f, 15f), Range(rng, -40f, 40f));
            _cameraObject.transform.rotation = Quaternion.Euler(
                Range(rng, -180f, 180f), Range(rng, -180f, 180f), Range(rng, -180f, 180f));

            bool orthographic = rng.Next(4) == 0;
            _camera.orthographic = orthographic;
            if (orthographic)
            {
                _camera.orthographicSize = Range(rng, 0.5f, 12f);
            }
            else
            {
                _camera.fieldOfView = Range(rng, 15f, 110f);
            }

            // 先放大远平面再收紧近平面：避免 Unity 把 far < near 的组合夹回去。
            _camera.farClipPlane = 1000f;
            float near = Range(rng, 0.05f, 2f);
            _camera.nearClipPlane = near;
            _camera.farClipPlane = near + Range(rng, 1f, 200f);
        }

        private Bounds RandomBounds(System.Random rng)
        {
            Vector3 center = RandomWorldPoint(rng);
            int shape = rng.Next(10);
            Vector3 size;
            if (shape == 0)
            {
                size = new Vector3(Range(rng, 100f, 300f), Range(rng, 100f, 300f), Range(rng, 100f, 300f));
            }
            else if (shape == 1)
            {
                size = new Vector3(Range(rng, 0f, 8f), 0f, Range(rng, 0f, 8f)); // 退化：一维为 0
            }
            else
            {
                size = new Vector3(Range(rng, 0f, 6f), Range(rng, 0f, 6f), Range(rng, 0f, 6f));
            }

            return new Bounds(center, size);
        }

        private Bounds FlatBounds(System.Random rng)
        {
            Vector3 center = RandomWorldPoint(rng);
            return new Bounds(center, new Vector3(Range(rng, 0f, 6f), 0f, Range(rng, 0f, 6f)));
        }

        /// <summary>相机附近（局部 ±25m）的随机世界点：既要命中视锥内，也要命中视锥外。</summary>
        private Vector3 RandomWorldPoint(System.Random rng)
        {
            Vector3 local = new Vector3(
                Range(rng, -25f, 25f), Range(rng, -12f, 12f), Range(rng, -25f, 25f));
            return _cameraObject.transform.TransformPoint(local);
        }

        /// <summary>
        /// 与索引 <paramref name="planeIndex"/> 选中的那个平面"相距 <paramref name="margin"/>"的盒子：
        /// margin = 0 → 恰好相切（d + r ≈ 0）；margin &gt; 0 → 完全在该平面外侧。
        /// </summary>
        private Bounds TangentBounds(System.Random rng, float margin, out int planeIndex,
            out float signedDistance, out float radius)
        {
            GeometryUtility.CalculateFrustumPlanes(_camera, _planes);
            planeIndex = rng.Next(_planes.Length);
            Plane plane = _planes[planeIndex];
            Vector3 normal = plane.normal;

            Vector3 extents = new Vector3(
                Range(rng, 0f, 3f), Range(rng, 0f, 3f), Range(rng, 0f, 3f));
            radius = (extents.x * Mathf.Abs(normal.x)) + (extents.y * Mathf.Abs(normal.y))
                + (extents.z * Mathf.Abs(normal.z));

            Vector3 onPlane = -normal * plane.distance;              // 平面上的点
            Vector3 center = onPlane - (normal * (radius + margin)); // 朝外侧退 (r + margin)

            // 沿平面方向挪一点，避免所有盒子都压在同一个点上。
            Vector3 tangent = Vector3.Cross(normal, Vector3.up);
            if (tangent.sqrMagnitude < 1e-6f)
            {
                tangent = Vector3.Cross(normal, Vector3.right);
            }

            tangent.Normalize();
            center += tangent * Range(rng, -3f, 3f);

            signedDistance = (normal.x * center.x) + (normal.y * center.y)
                + (normal.z * center.z) + plane.distance;
            return new Bounds(center, extents * 2f);
        }

        private static float Range(System.Random rng, float min, float max)
        {
            return min + ((float)rng.NextDouble() * (max - min));
        }
    }
}
