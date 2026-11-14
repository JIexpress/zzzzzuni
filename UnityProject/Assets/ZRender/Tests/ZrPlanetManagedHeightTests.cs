// ZrPlanetManagedHeightTests.cs —— 行星高度纯函数 C# 移植的桌面逐位对照（TASK-B-PLANET-12）。
//
// 被测对象：Game.Domain.World.PlanetTerrainMath（zplanet::PlanetTerrainHeightM 的 C# 机械
// 移植——hash/lattice/value-noise/fBm + cube-sphere 格几何）。
//
// 三条对照通道（本任务的"牙"——位差必须逐点归因）：
//   1. **原生 dll float 产出口径逐位**（最强通道）：EditMode 真实 zrender.dll 上调
//      ZR_Planet_SampleHeight（ZR_PLANET_OK 出口 = static_cast<float>(double)），与 C#
//      端"同一输入路径"的逐式复刻 cast 到 float 后**位模式比对**。输入路径复刻 =
//      zplugin.cpp SampleHeight 内部算式逐式（减中心 → sqrt → 除）——若 C# 移植与
//      原生 double 域数学逐位一致，则 float 位模式 100% 相等。
//   2. **planet-budget 探针 JSON 锚点**（B-PLANET-09 存档，验收 N4 逐位复现过的数字）：
//      100 个贴地扫描点的 hPureDouble（探针以 C++ 纯函数路径产出、JSON 存 3 位小数）
//      与 C# 纯路径重算值对照（容差 = 存档精度 0.0005m；位级一致时差异只来自 JSON
//      舍入）。其中 4 个格点锚点是"网格顶点=纯函数逐位"的校准项。
//   3. **abi-precision 探针 JSON 锚点**（B-PLANET-10 存档）：锚点顶点 anchorAbs
//      （17 位有效数字全精度）反推高度 |posAbs−center|−R 与 C# 纯路径对照
//      （容差 1e-6m——反推链自身的 double 舍入 ~1e-9m 量级，远紧于 3 位小数通道）。
//
// 探针点位构造（通道 1/2 的 100 点）：与 zrhi_d3d12_window.cpp --pbudget groundScan
// 逐式同序重建（seed 1337 / cellsPerEdge 32 / 近档 stride 63 / face 0 / 中心
// (0,−R,0)）——其中 35 个 interior 点的散布位置由 PlanetHashU32(0x5070BEEF^n) 决定，
// 因此"散布位置与 JSON 一致"本身就是哈希移植的逐位证据。
//
// 设备依赖：只碰无 GPU 触点的主线程调用（SetWorld/SampleHeight/GetStats——设计稿 §8），
// EditMode 裸跑；与 ZrPlanetStreamingTests 同口径（真 dll 必须在位）。

using System;
using Game.Domain.World;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Render.Bindings;
using ZFrame.Render.Planet;

namespace ZFrame.Render.Tests
{
    [TestFixture]
    public class ZrPlanetManagedHeightTests
    {
        // ---- 探针口径常量（与 zrhi_d3d12_window.cpp --pbudget 逐字对应）----------------

        private const uint ProbeSeed = 1337u;
        private const int ProbeCellsPerEdge = 32;
        private const double ProbeNearStride = 63.0;
        private const double ProbeNearTotal = ProbeCellsPerEdge * ProbeNearStride; // 2016
        private const double CenterX = 0.0;
        private const double CenterY = -6.371e6;
        private const double CenterZ = 0.0;

        /// <summary>JSON 存档精度（hPureDoubleM 三位小数）+ 二进制字面量转换余量。</summary>
        private const double JsonAnchorToleranceMeters = 5.001e-4;

        // planet-budget JSON（Tools/Probes/out/planet-budget-2026-11-14T00-22-38.json，
        // 验收轮 N2/N4 逐位复现档）groundScan.perPoint 的 100 点：kind + hPureDoubleM，
        // 顺序 = 探针 pbPushScan 调用序（①共享边横穿 ×2×5 ②共享角 ×16 ③格对角 ×8
        // ④内部均匀 ×35 ⑤格中心 ×9 ⑥格点锚点 ×4 ⑦近/远档跨界 ×8）。
        private static readonly string[] ProbePointKinds =
        {
            "edgeEW-", "edgeEW+", "edgeNS-", "edgeNS+", "edgeEW-", "edgeEW+", "edgeNS-", "edgeNS+",
            "edgeEW-", "edgeEW+", "edgeNS-", "edgeNS+", "edgeEW-", "edgeEW+", "edgeNS-", "edgeNS+",
            "edgeEW-", "edgeEW+", "edgeNS-", "edgeNS+",
            "cornerDiag", "cornerDiag", "cornerAnti", "cornerAnti", "cornerDiag", "cornerDiag",
            "cornerAnti", "cornerAnti", "cornerDiag", "cornerDiag", "cornerAnti", "cornerAnti",
            "cornerDiag", "cornerDiag", "cornerAnti", "cornerAnti",
            "cellDiag", "cellDiag", "cellDiag", "cellDiag", "cellDiag", "cellDiag", "cellDiag", "cellDiag",
            "interior", "interior", "interior", "interior", "interior", "interior", "interior",
            "interior", "interior", "interior", "interior", "interior", "interior", "interior",
            "interior", "interior", "interior", "interior", "interior", "interior", "interior",
            "interior", "interior", "interior", "interior", "interior", "interior", "interior",
            "interior", "interior", "interior", "interior", "interior", "interior", "interior",
            "cellCenter", "cellCenter", "cellCenter", "cellCenter", "cellCenter", "cellCenter",
            "cellCenter", "cellCenter", "cellCenter",
            "vertex", "vertex", "vertex", "vertex",
            "lodCross-", "lodCross-", "lodCross-", "lodCross-", "lodCross-", "lodCross-",
            "lodCross-", "lodCross-",
        };

        private static readonly double[] ProbePointHeightsJson =
        {
            -692.514, -690.066, -16.377, 15.589, -708.676, -697.644, -190.117, 89.71,
            -722.301, -423.715, 13.439, 363.811, 311.986, 168.844, -466.242, 91.605,
            469.454, 558.081, 266.455, -206.796,
            -365.938, -493.057, -421.67, -434.46, -393.975, -695.919, -504.612, -438.537,
            473.377, -549.47, 624.178, -606.562, 471.324, -360.127, 588.489, -481.963,
            -954.551, -647.803, 1085.913, 995.917, 800.135, 807.302, -312.662, -36.632,
            -770.462, -869.103, -55.598, -326.332, 1114.492, 526.521, 319.091, 338.653,
            -984.24, 172.406, 647.591, 276.115, -302.341, -616.491, 1349.753, -748.531,
            -57.832, -160.206, -169.08, 1294.291, 344.784, -547.03, 381.302, -505.726,
            -146.167, 591.437, 631.904, -116.815, 75.777, 486.774, 713.884, -93.207,
            27.617, 543.104, -15.094, 645.151, 511.963, 574.787, 924.046, 449.016,
            -86.325, -830.06, 438.283, -477.818,
            254.267, -817.111, -267.696, -716.197,
            -225.333, -175.161, -31.278, 36.513, 131.018, -232.106, -1011.558, -17.372,
        };

        // interior ×35 的 (u,v)（JSON 6 位有效数字）——散布位置由 PlanetHashU32(0x5070BEEF^n)
        // 决定：与探针点位选择逐点一致 = 哈希移植的间接逐位证据。
        private static readonly double[] InteriorUvJson =
        {
            0.0423753, 0.0823157, 0.112986, 0.0426299, 0.00239471, 0.0383669, 0.000546464, 0.105021,
            -0.0458228, 0.104249, 0.123149, -0.0601339, 0.0741274, 0.0950017, -0.00650034, 0.122185,
            0.0121166, -0.0197871, 0.0516165, 0.0206168, 0.104924, 0.0582828, 0.120623, -0.018102,
            0.114923, 0.120877, 0.0832341, -0.011553, 0.0604286, 0.0979543, -0.0383726, 0.101954,
            -0.0557336, 0.112274, 0.0406615, -0.019604, 0.0122053, 0.0497568, 0.107565, -0.059164,
            -0.0190948, -0.0250114, 0.0409905, 0.0588579, 0.0550956, 0.105293, 0.0431392, 0.095325,
            0.10244, 0.021169, 0.0225595, 0.0665141, 0.0723478, -0.0076276, 0.0355087, 0.104077,
            0.104186, -0.00106432, -0.0423838, 0.0109207, 0.0114099, 0.0986667, -0.0164454, 0.088098,
            -0.0177615, -0.0314775, 0.0642596, -0.00592813, 0.107098, -0.0311627,
        };

        // abi-precision JSON（Tools/Probes/out/abi-precision-2026-11-14T00-57-35.json）
        // setup 节：锚点=面 0 格点顶点（g=1039，双精度全量存档）。
        private static readonly double AbiAnchorX = 195756.29134543028;
        private static readonly double AbiAnchorY = -6175243.708654569;
        private static readonly double AbiAnchorZ = 6365236.828264336;

        // ---- 100 点构造（--pbudget groundScan 逐式重建，顺序与 JSON 对齐）------------------

        private struct ProbePoint
        {
            public string Kind;
            public double U;
            public double V;
        }

        private static ProbePoint[] BuildProbePoints()
        {
            var points = new System.Collections.Generic.List<ProbePoint>(100);
            void Push(string kind, double u, double v) => points.Add(new ProbePoint { Kind = kind, U = u, V = v });

            // ① 共享边横穿线 ×2（E-W 缘 gU=17·63=1071 与 N-S 缘 gV=1071，各 5 点；
            //    格位偏移 ±{0.125,0.5,2,8,32}；chunk 中线 midV=16·63+31.5=1039.5）
            const double edgeU = 17.0 * ProbeNearStride;
            const double midV = 16.0 * ProbeNearStride + 31.5;
            double[] edgeOffsets = { 0.125, 0.5, 2.0, 8.0, 32.0 };
            foreach (double o in edgeOffsets)
            {
                Push("edgeEW-", 2.0 * (edgeU - o) / ProbeNearTotal - 1.0, 2.0 * midV / ProbeNearTotal - 1.0);
                Push("edgeEW+", 2.0 * (edgeU + o) / ProbeNearTotal - 1.0, 2.0 * midV / ProbeNearTotal - 1.0);
                Push("edgeNS-", 2.0 * midV / ProbeNearTotal - 1.0, 2.0 * (edgeU - o) / ProbeNearTotal - 1.0);
                Push("edgeNS+", 2.0 * midV / ProbeNearTotal - 1.0, 2.0 * (edgeU + o) / ProbeNearTotal - 1.0);
            }

            // ② 共享角 (1071,1071) 密集簇 ×16：对角线对 (o,o)/(−o,−o) 与反对角 (o,−o)/(−o,o)
            for (int i = 0; i < edgeOffsets.Length - 1; ++i)
            {
                double o = edgeOffsets[i]; // {0.125, 0.5, 2, 8}
                Push("cornerDiag", 2.0 * (edgeU + o) / ProbeNearTotal - 1.0, 2.0 * (edgeU + o) / ProbeNearTotal - 1.0);
                Push("cornerDiag", 2.0 * (edgeU - o) / ProbeNearTotal - 1.0, 2.0 * (edgeU - o) / ProbeNearTotal - 1.0);
                Push("cornerAnti", 2.0 * (edgeU + o) / ProbeNearTotal - 1.0, 2.0 * (edgeU - o) / ProbeNearTotal - 1.0);
                Push("cornerAnti", 2.0 * (edgeU - o) / ProbeNearTotal - 1.0, 2.0 * (edgeU + o) / ProbeNearTotal - 1.0);
            }

            // ③ 格对角线横穿 ×8（chunk 16 内部 4 个格角，两侧各 0.5/0.25 格）
            double[] diagBases = { 1010.0, 1028.0, 1046.0, 1064.0 };
            foreach (double gi in diagBases)
            {
                Push("cellDiag", 2.0 * (gi + 0.5) / ProbeNearTotal - 1.0, 2.0 * (gi - 0.25) / ProbeNearTotal - 1.0);
                Push("cellDiag", 2.0 * (gi - 0.5) / ProbeNearTotal - 1.0, 2.0 * (gi + 0.25) / ProbeNearTotal - 1.0);
            }

            // ④ 内部均匀点 ×35（PlanetHashU32 确定性散布于内 3x3：g ∈ [945,1134]）
            for (uint n = 0u; n < 35u; ++n)
            {
                uint h = PlanetTerrainMath.HashU32(0x5070BEEFu ^ n);
                double fr1 = (h & 0xFFFFu) / 65535.0;
                double fr2 = ((h >> 16) & 0xFFFFu) / 65535.0;
                double gu = 945.0 + fr1 * (1134.0 - 945.0);
                double gv = 945.0 + fr2 * (1134.0 - 945.0);
                Push("interior", 2.0 * gu / ProbeNearTotal - 1.0, 2.0 * gv / ProbeNearTotal - 1.0);
            }

            // ⑤ 格中心点 ×9（中档格中心 (a+0.5,b+0.5)，a,b∈{0,1,2}）
            for (uint a = 0u; a < 3u; ++a)
            {
                for (uint b = 0u; b < 3u; ++b)
                {
                    double gu = 945.0 + 63.0 * a + 31.5;
                    double gv = 945.0 + 63.0 * b + 31.5;
                    Push("cellCenter", 2.0 * gu / ProbeNearTotal - 1.0, 2.0 * gv / ProbeNearTotal - 1.0);
                }
            }

            // ⑥ 格点锚点 ×4（恰在网格顶点上——dev≈0 的校准项；第 4 点在共享缘顶点上）
            Push("vertex", 2.0 * 1039.0 / ProbeNearTotal - 1.0, 2.0 * 1039.0 / ProbeNearTotal - 1.0);
            Push("vertex", 2.0 * 1070.0 / ProbeNearTotal - 1.0, 2.0 * 1070.0 / ProbeNearTotal - 1.0);
            Push("vertex", 2.0 * 1008.0 / ProbeNearTotal - 1.0, 2.0 * 1008.0 / ProbeNearTotal - 1.0);
            Push("vertex", 2.0 * 1071.0 / ProbeNearTotal - 1.0, 2.0 * 1039.0 / ProbeNearTotal - 1.0);

            // ⑦ 近/远档跨界 ×8（gU=15·63=945 缘外侧，格距 4× 于近档）
            double[] crossOffsets = { 0.125, 0.25, 0.5, 1.0, 2.0, 4.0, 8.0, 16.0 };
            foreach (double o in crossOffsets)
            {
                Push("lodCross-", 2.0 * (945.0 - o) / ProbeNearTotal - 1.0, 2.0 * midV / ProbeNearTotal - 1.0);
            }

            return points.ToArray();
        }

        /// <summary>纯路径高度：d = CubeSphereDir(0,u,v) → TerrainHeightM（探针 hPureDouble 同式）。</summary>
        private static double HeightPure(double u, double v)
        {
            PlanetTerrainMath.CubeSphereDir(0, u, v, out double dx, out double dy, out double dz);
            return PlanetTerrainMath.TerrainHeightM(ProbeSeed, dx, dy, dz);
        }

        /// <summary>
        /// ZR_Planet_SampleHeight 内部算式的 C# 逐式复刻（zplugin.cpp:740-766）：点 = 中心 +
        /// dir·R（探针调用式）→ 减中心 → sqrt → 除 → TerrainHeightM → (float)。返回
        /// (double, float)——double 供诊断，float 供位模式对照。
        /// </summary>
        private static float HeightApiReplica(double u, double v, out double heightDouble)
        {
            PlanetTerrainMath.CubeSphereDir(0, u, v, out double dx, out double dy, out double dz);
            double px = CenterX + dx * PlanetTerrainMath.PlanetRadiusMeters;
            double py = CenterY + dy * PlanetTerrainMath.PlanetRadiusMeters;
            double pz = CenterZ + dz * PlanetTerrainMath.PlanetRadiusMeters;
            double relX = px - CenterX;
            double relY = py - CenterY;
            double relZ = pz - CenterZ;
            double len = Math.Sqrt(relX * relX + relY * relY + relZ * relZ);
            heightDouble = PlanetTerrainMath.TerrainHeightM(ProbeSeed, relX / len, relY / len, relZ / len);
            return (float)heightDouble;
        }

        private static bool FloatBitsEqual(float a, float b)
            => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

        // ---- 生命周期（真 dll 必须在位；只碰无 GPU 触点导出）-----------------------------

        [SetUp]
        public void SetUp()
        {
            ZrPlanetChannel.ResetForTests();
            ZrPlanetNative.ClearAll();
            ZrPlanetNative.EnsurePlanetChannelReady(); // ApiVersion≥3 门控（失败即抛，与既有套件同口径）
            var world = new ZrPlanetWorldNative
            {
                StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ZrPlanetWorldNative>(),
                Seed = ProbeSeed,
                CenterAbs = new[] { CenterX, CenterY, CenterZ },
            };
            Assert.AreEqual(ZrPlanetNative.Ok, ZrPlanetNative.SetWorld(ref world), "SetWorld(1337, (0,−R,0)) 必须成功");
        }

        [TearDown]
        public void TearDown()
        {
            ZrPlanetNative.ClearAll();
            ZrPlanetChannel.ResetForTests();
        }

        // ---- 通道 1：原生 dll float 产出口径逐位 ---------------------------------------

        [Test]
        public void NativeSampleHeight_ProbeScan100Points_FloatBitsBitwiseEqual()
        {
            ProbePoint[] points = BuildProbePoints();
            Assert.AreEqual(ProbePointKinds.Length, points.Length, "点位构造必须与 JSON 存档等长");
            Assert.AreEqual(100, points.Length, "探针扫描 = 100 点");

            int mismatched = 0;
            var report = new System.Text.StringBuilder();
            for (int i = 0; i < points.Length; ++i)
            {
                ProbePoint p = points[i];
                float replica = HeightApiReplica(p.U, p.V, out double replicaDouble);
                PlanetTerrainMath.CubeSphereDir(0, p.U, p.V, out double dx, out double dy, out double dz);
                double px = CenterX + dx * PlanetTerrainMath.PlanetRadiusMeters;
                double py = CenterY + dy * PlanetTerrainMath.PlanetRadiusMeters;
                double pz = CenterZ + dz * PlanetTerrainMath.PlanetRadiusMeters;
                int rc = ZrPlanetNative.SampleHeight(px, py, pz, out float apiFloat);
                Assert.AreEqual(ZrPlanetNative.Ok, rc, $"点 {i}（{p.Kind}）SampleHeight 必须成功");
                if (!FloatBitsEqual(replica, apiFloat))
                {
                    ++mismatched;
                    report.AppendLine(
                        $"#{i} {p.Kind} u={p.U:R} v={p.V:R}: csharp={replica:R} ({BitConverter.SingleToInt32Bits(replica):X8}) " +
                        $"native={apiFloat:R} ({BitConverter.SingleToInt32Bits(apiFloat):X8}) double={replicaDouble:R}");
                }
            }

            Assert.AreEqual(0, mismatched,
                $"float 产出口径必须逐位一致（{mismatched}/100 点位差需逐点归因）：\n{report}");
        }

        [Test]
        public void NativeSampleHeight_AllFaces_GridVertices_FloatBitsBitwiseEqual()
        {
            // 六面面中心 + 共享棱端点（跨面逐位性质）+ 各面内部点：
            // 6 faces × 8 u × 4 v = 192 点，覆盖 faceId 0..5 与共享棱。
            var mismatches = new System.Collections.Generic.List<string>();
            int sampleCount = 0;
            double[] us = { 0.0, -1.0, 1.0, 0.25, -0.75, 0.5, -0.25, 0.75 };
            double[] vs = { 0.0, 1.0, -1.0, 0.375 };
            for (int face = 0; face < 6; ++face)
            {
                foreach (double u in us)
                {
                    foreach (double v in vs)
                    {
                        PlanetTerrainMath.CubeSphereDir(face, u, v, out double dx, out double dy, out double dz);
                        double px = CenterX + dx * PlanetTerrainMath.PlanetRadiusMeters;
                        double py = CenterY + dy * PlanetTerrainMath.PlanetRadiusMeters;
                        double pz = CenterZ + dz * PlanetTerrainMath.PlanetRadiusMeters;
                        Assert.AreEqual(ZrPlanetNative.Ok, ZrPlanetNative.SampleHeight(px, py, pz, out float apiFloat));
                        double relX = px - CenterX;
                        double relY = py - CenterY;
                        double relZ = pz - CenterZ;
                        double len = Math.Sqrt(relX * relX + relY * relY + relZ * relZ);
                        double replicaDouble = PlanetTerrainMath.TerrainHeightM(
                            ProbeSeed, relX / len, relY / len, relZ / len);
                        float replica = (float)replicaDouble;
                        ++sampleCount;
                        if (!FloatBitsEqual(replica, apiFloat))
                        {
                            mismatches.Add(
                                $"face={face} u={u:R} v={v:R}: csharp={replica:R} ({BitConverter.SingleToInt32Bits(replica):X8}) " +
                                $"native={apiFloat:R} ({BitConverter.SingleToInt32Bits(apiFloat):X8})");
                        }
                    }
                }
            }

            Assert.AreEqual(0, mismatches.Count,
                $"六面 192 点 float 产出口径逐位一致失败（{sampleCount} 点中 {mismatches.Count} 点位差）：\n" +
                string.Join("\n", mismatches));
        }

        [Test]
        public void NativeSampleHeight_HashDrivenDirections_FloatBitsBitwiseEqual()
        {
            // 确定性伪随机方向（哈希驱动，避开任何手写常量偏差）：512 个方向，球面全域覆盖。
            var mismatches = new System.Collections.Generic.List<string>();
            for (uint n = 0u; n < 512u; ++n)
            {
                uint h = PlanetTerrainMath.HashU32(n * 0x9E3779B9u ^ 0xC0FFEEu);
                // 全域映射：face = h%6，u/v ∈ [−1,1]
                int face = (int)(h % 6u);
                uint h2 = PlanetTerrainMath.HashU32(h ^ 0x5BF03635u);
                double u = (h2 & 0xFFFFu) / 32767.5 - 1.0;
                double v = ((h2 >> 16) & 0xFFFFu) / 32767.5 - 1.0;
                PlanetTerrainMath.CubeSphereDir(face, u, v, out double dx, out double dy, out double dz);
                double px = CenterX + dx * PlanetTerrainMath.PlanetRadiusMeters;
                double py = CenterY + dy * PlanetTerrainMath.PlanetRadiusMeters;
                double pz = CenterZ + dz * PlanetTerrainMath.PlanetRadiusMeters;
                Assert.AreEqual(ZrPlanetNative.Ok, ZrPlanetNative.SampleHeight(px, py, pz, out float apiFloat));
                double relX = px - CenterX;
                double relY = py - CenterY;
                double relZ = pz - CenterZ;
                double len = Math.Sqrt(relX * relX + relY * relY + relZ * relZ);
                float replica = (float)PlanetTerrainMath.TerrainHeightM(ProbeSeed, relX / len, relY / len, relZ / len);
                if (!FloatBitsEqual(replica, apiFloat))
                {
                    mismatches.Add(
                        $"n={n} face={face} u={u:R} v={v:R}: csharp={replica:R} ({BitConverter.SingleToInt32Bits(replica):X8}) " +
                        $"native={apiFloat:R} ({BitConverter.SingleToInt32Bits(apiFloat):X8})");
                }
            }

            Assert.AreEqual(0, mismatches.Count,
                $"512 个哈希驱动方向 float 产出口径逐位一致失败：\n{string.Join("\n", mismatches)}");
        }

        // ---- 通道 2：planet-budget JSON 锚点（3 位小数存档精度）-------------------------

        [Test]
        public void Budget9Json_AnchorHeights_ReproducedByPort()
        {
            ProbePoint[] points = BuildProbePoints();
            int mismatched = 0;
            var report = new System.Text.StringBuilder();
            for (int i = 0; i < points.Length; ++i)
            {
                double h = HeightPure(points[i].U, points[i].V);
                double expected = ProbePointHeightsJson[i];
                if (Math.Abs(h - expected) > JsonAnchorToleranceMeters)
                {
                    ++mismatched;
                    report.AppendLine($"#{i} {points[i].Kind} u={points[i].U:R} v={points[i].V:R}: " +
                                      $"csharp={h:R} json={expected:R} diff={h - expected:G3}");
                }
            }

            Assert.AreEqual(0, mismatched,
                $"planet-budget 100 点锚高必须按存档精度复现（{mismatched} 点超差）：\n{report}");
        }

        [Test]
        public void Budget9Json_VertexAnchors_HeightMatchesJson_ExactlyAtStoragePrecision()
        {
            // 4 个格点锚点（kind=vertex，位于数组 84..87）是"网格顶点=纯函数"校准项
            // （B-PLANET-09：vertexAnchorMax=1.74e-9m——网格模型与渲染 VB 逐位同源）。
            ProbePoint[] points = BuildProbePoints();
            for (int i = 84; i <= 87; ++i)
            {
                double h = HeightPure(points[i].U, points[i].V);
                Assert.AreEqual(ProbePointHeightsJson[i], h, 5.001e-4,
                    $"格点锚点 #{i}（{ProbePointKinds[i]}）高度必须复现 JSON 存档值");
            }
        }

        [Test]
        public void Budget9Json_InteriorScatter_HashDrivenSelectionMatchesProbe()
        {
            // 35 个 interior 点的散布位置由 HashU32(0x5070BEEF^n) 决定——与 JSON 存档的
            // (u,v)（6 位有效数字）逐点一致 = 哈希移植驱动同一点位选择。
            ProbePoint[] points = BuildProbePoints();
            int firstInterior = Array.IndexOf(ProbePointKinds, "interior");
            Assert.AreEqual(44, firstInterior, "interior 段起点必须与探针构造顺序一致");
            for (int n = 0; n < 35; ++n)
            {
                ProbePoint p = points[firstInterior + n];
                Assert.AreEqual(InteriorUvJson[2 * n], p.U, 1e-6, $"interior #{n} u 必须与探针选择一致");
                Assert.AreEqual(InteriorUvJson[2 * n + 1], p.V, 1e-6, $"interior #{n} v 必须与探针选择一致");
            }
        }

        // ---- 通道 3：abi-precision JSON 锚点（全精度）----------------------------------

        [Test]
        public void AbiPrecision_AnchorVertex_HeightMatchesFullPrecisionArchive()
        {
            // 锚点 = 面 0 格点顶点 g=(1039,1039)（abi-precision setup.anchorAbs，17 位有效
            // 数字）。反推链 |posAbs−center|−R 自身有 ~1e-9m 量级 double 舍入——1e-6m 容差
            // 远紧于 3 位小数通道，构成全精度量级的锚。
            double u = PlanetTerrainMath.ChunkFaceCoord(1039, 2016);
            double h = HeightPure(u, u);
            double dx = AbiAnchorX - CenterX;
            double dy = AbiAnchorY - CenterY;
            double dz = AbiAnchorZ - CenterZ;
            double implied = Math.Sqrt(dx * dx + dy * dy + dz * dz) - PlanetTerrainMath.PlanetRadiusMeters;
            Assert.AreEqual(implied, h, 1e-6,
                $"abi-precision 锚点高度：csharp={h:R} implied={implied:R} diff={h - implied:G3}");
        }

        // ---- 移植自身语义（确定性/值域/跨面棱逐位）--------------------------------------

        [Test]
        public void Determinism_SameInputs_BitIdenticalOutputs()
        {
            double a1 = HeightPure(0.030753968253968, 0.030753968253968);
            double a2 = HeightPure(0.030753968253968, 0.030753968253968);
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(a1), BitConverter.DoubleToInt64Bits(a2),
                "同 (seed,方向) 位形必须逐位同输出（确定性契约）");

            // 种子可配：不同种子输出不同（与原生"不同种子输出不同"契约同构）。
            double otherSeed = 0.0;
            for (uint seed = 0u; seed < 16u; ++seed)
            {
                PlanetTerrainMath.CubeSphereDir(0, 0.5, -0.25, out double dx, out double dy, out double dz);
                double h = PlanetTerrainMath.TerrainHeightM(seed + 1u, dx, dy, dz);
                otherSeed += h;
            }

            Assert.AreNotEqual(0.0, otherSeed, "多种子高度合成不应恒零（种子可配性冒烟）");
        }

        [Test]
        public void LatticeValue_RangeAndNegativeCoordWrapDeterministic()
        {
            // [0,1) 值域 + 负格点补码回绕确定性（与 C++ static_cast<uint32_t> 同语义）。
            for (int x = -4096; x <= 4096; x += 97)
            {
                double v = PlanetTerrainMath.LatticeValue(x, -x, x / 3, ProbeSeed);
                Assert.GreaterOrEqual(v, 0.0, $"格点 ({x},{-x},{x / 3}) 值域下界");
                Assert.Less(v, 1.0, $"格点 ({x},{-x},{x / 3}) 值域上界");
                double again = PlanetTerrainMath.LatticeValue(x, -x, x / 3, ProbeSeed);
                Assert.AreEqual(BitConverter.DoubleToInt64Bits(v), BitConverter.DoubleToInt64Bits(again),
                    "负格点回绕必须确定性（同格点恒同输出）");
            }
        }

        [Test]
        public void CubeSphereDir_SharedFaceEdges_BitwiseConsistentAcrossFaces()
        {
            // 跨面共享棱 ±1 端点两侧方向逐位一致（planet_math.h 的构造性性质在移植中保持）：
            // 面 0(+Z,u=+1) ↔ 面 1(+X,u=−1)；面 0(v=+1) ↔ 面 4(+Y,v=−1)。
            double[] vs = { -1.0, -0.5, 0.0, 0.5, 1.0 };
            foreach (double v in vs)
            {
                PlanetTerrainMath.CubeSphereDir(0, 1.0, v, out double x0, out double y0, out double z0);
                PlanetTerrainMath.CubeSphereDir(1, -1.0, v, out double x1, out double y1, out double z1);
                Assert.AreEqual(BitConverter.DoubleToInt64Bits(x0), BitConverter.DoubleToInt64Bits(x1),
                    $"u=+1(面0) ↔ u=−1(面1) 共享棱 x 分量逐位（v={v:R}）");
                Assert.AreEqual(BitConverter.DoubleToInt64Bits(y0), BitConverter.DoubleToInt64Bits(y1),
                    $"u=+1(面0) ↔ u=−1(面1) 共享棱 y 分量逐位（v={v:R}）");
                Assert.AreEqual(BitConverter.DoubleToInt64Bits(z0), BitConverter.DoubleToInt64Bits(z1),
                    $"u=+1(面0) ↔ u=−1(面1) 共享棱 z 分量逐位（v={v:R}）");
            }

            double[] us = { -1.0, -0.5, 0.0, 0.5, 1.0 };
            foreach (double u in us)
            {
                PlanetTerrainMath.CubeSphereDir(0, u, 1.0, out double x0, out double y0, out double z0);
                PlanetTerrainMath.CubeSphereDir(4, u, -1.0, out double x4, out double y4, out double z4);
                Assert.AreEqual(BitConverter.DoubleToInt64Bits(x0), BitConverter.DoubleToInt64Bits(x4),
                    $"v=+1(面0) ↔ v=−1(面4) 共享棱 x 分量逐位（u={u:R}）");
                Assert.AreEqual(BitConverter.DoubleToInt64Bits(y0), BitConverter.DoubleToInt64Bits(y4),
                    $"v=+1(面0) ↔ v=−1(面4) 共享棱 y 分量逐位（u={u:R}）");
                Assert.AreEqual(BitConverter.DoubleToInt64Bits(z0), BitConverter.DoubleToInt64Bits(z4),
                    $"v=+1(面0) ↔ v=−1(面4) 共享棱 z 分量逐位（u={u:R}）");
            }
        }

        [Test]
        public void CubeSphereDir_MatchesStreamerInternalMirror_Bitwise()
        {
            // 两份 C# 镜像（本移植 ↔ ZrPlanetChunkStreamer 内部镜像）逐位一致——
            // 同一原生语义的托管侧双镜像不漂移（漂移任一侧都会被钉红）。
            double[] us = { -1.0, -0.030753968253968254, 0.0, 0.25, 0.998015873015873 };
            double[] vs = { -0.998015873015873, -0.25, 0.0, 0.0615079365079365, 1.0 };
            for (int face = 0; face < 6; ++face)
            {
                foreach (double u in us)
                {
                    foreach (double v in vs)
                    {
                        PlanetTerrainMath.CubeSphereDir(face, u, v, out double xm, out double ym, out double zm);
                        ZrPlanetChunkStreamer.CubeSphereDir(face, u, v, out double xs, out double ys, out double zs);
                        Assert.AreEqual(BitConverter.DoubleToInt64Bits(xm), BitConverter.DoubleToInt64Bits(xs),
                            $"face={face} u={u:R} v={v:R} x 分量两镜像逐位");
                        Assert.AreEqual(BitConverter.DoubleToInt64Bits(ym), BitConverter.DoubleToInt64Bits(ys),
                            $"face={face} u={u:R} v={v:R} y 分量两镜像逐位");
                        Assert.AreEqual(BitConverter.DoubleToInt64Bits(zm), BitConverter.DoubleToInt64Bits(zs),
                            $"face={face} u={u:R} v={v:R} z 分量两镜像逐位");
                    }
                }
            }
        }

        [Test]
        public void TerrainHeight_BoundedByMaxAmplitude_AndChunkVertexIndexFormula()
        {
            // 值域上界（|h| ≤ 2400）+ ChunkVertexIndex/ChunkFaceCoord 规范式。
            PlanetTerrainMath.CubeSphereDir(2, 0.37, -0.61, out double dx, out double dy, out double dz);
            double h = PlanetTerrainMath.TerrainHeightM(42u, dx, dy, dz);
            Assert.GreaterOrEqual(h, -PlanetTerrainMath.TerrainMaxAmplitudeMeters);
            Assert.LessOrEqual(h, PlanetTerrainMath.TerrainMaxAmplitudeMeters);

            Assert.AreEqual(1039, PlanetTerrainMath.ChunkVertexIndex(16, 31, 63), "g = c·stride + i");
            Assert.AreEqual(-1.0, PlanetTerrainMath.ChunkFaceCoord(0, 2016), "g=0 → u=−1");
            Assert.AreEqual(1.0, PlanetTerrainMath.ChunkFaceCoord(2016, 2016), "g=total → u=+1");
            Assert.AreEqual(2.0 * 1039.0 / 2016.0 - 1.0, PlanetTerrainMath.ChunkFaceCoord(1039, 2016),
                "规范式 2g/total − 1（精确整数 double 商）");
        }
    }
}
