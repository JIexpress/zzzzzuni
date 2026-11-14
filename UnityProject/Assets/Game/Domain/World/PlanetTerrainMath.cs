using System;

namespace Game.Domain.World
{
    /// <summary>
    /// TASK-B-PLANET-12：原生地形高度纯函数 <c>zplanet::PlanetTerrainHeightM</c> 的 C# 机械移植
    /// （Native/zplanet/planet_math.h:295-369——hash/lattice/value-noise/fBm 与 cube-sphere
    /// 格几何，语义零改动）。
    ///
    /// 为什么在托管侧再有一份：D8 裁决（TASK-B-PLANET-01 主控裁决第 2 条）把 Android 判据
    /// 对象定为行星能力的<b>托管等效实现</b>——原生 zplanet 是 D3D12 原生通道的一部分，
    /// Android 不跑 ZRender 原生（zrender.dll 仅 Windows x64），因此地形高度/格几何必须有
    /// 一份纯 C# 实现供 Android 真机与任何"无原生 dll"环境使用。桌面端逐位对照证据见
    /// ZrPlanetManagedHeightTests（EditMode，真实 zrender.dll SampleHeight float 位模式
    /// + planet-budget/abi-precision 探针 JSON 锚点值三通道对照）。
    ///
    /// 移植纪律（本文件正确性的全部依据）：
    ///  - <b>逐式镜像</b>：与 planet_math.h 逐运算同序（float64 IEEE754 双精度、
    ///    round-to-nearest-even；MSVC /O2 + /fp:precise 默认不做 FMA 缩并，C# JIT 同样
    ///    不缩并——同序同精度即逐位同值）；
    ///  - <b>uint 回绕</b>：C# uint 乘加与 C++ uint32 同为 mod 2^32 回绕（unchecked 默认）；
    ///    负格点坐标 (uint)ix 与 C++ static_cast 同为补码回绕；
    ///  - <b>double 域进出</b>：本类所有输出都是 double（生产口径的 float 化只发生在
    ///    ZR_Planet_SampleHeight 的出口——对照测试按同口径 cast 后比对位模式）；
    ///  - 与 ZrPlanetChunkStreamer.CubeSphereDir（ZFrame.Render 内部镜像）的关系：两份
    ///    独立镜像同式（跨程序集依赖方向使然——ZFrame.Render 不得引用 Game.Domain），
    ///    EditMode 用例钉住两者逐位一致；若原生语义变更，两处镜像与原生 doctest 三方
    ///    同步（B-PLANET-03 的 15 条 doctest 是上游权威）。
    /// </summary>
    public static class PlanetTerrainMath
    {
        // ---- 行星常量（与 planet_math.h 逐字对应）------------------------------------

        /// <summary>行星半径 m（planet_math.h kPlanetRadiusM）。</summary>
        public const double PlanetRadiusMeters = 6.371e6;

        /// <summary>fBm 层数（kTerrainNoiseOctaves，幅值逐半衰减）。</summary>
        public const uint TerrainNoiseOctaves = 4u;

        /// <summary>基频地形主波长 m（kTerrainBaseWavelengthM——噪声格胞 1 ≈ 地表 36km）。</summary>
        public const double TerrainBaseWavelengthMeters = 36000.0;

        /// <summary>地形起伏幅值上界 m（kTerrainMaxAmpM——输出 ∈ [−2400, +2400]）。</summary>
        public const double TerrainMaxAmplitudeMeters = 2400.0;

        // ---- uint32 混合哈希（PCG 风格 avalanche；planet_math.h PlanetHashU32）----------

        /// <summary>
        /// 三段乘法混合哈希（确定性纯函数）。与 C++ 逐式同序：xor-shift 16 → 乘 →
        /// xor-shift 15 → 乘 → xor-shift 16。
        /// </summary>
        public static uint HashU32(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            return x;
        }

        /// <summary>
        /// 三维整数格点 → [0,1) 标量（planet_math.h PlanetLatticeValue）。种子混入首级；
        /// 坐标逐级级联（负坐标按补码回绕——(uint)ix 与 C++ static_cast 同语义）。
        /// </summary>
        public static double LatticeValue(int ix, int iy, int iz, uint seed)
        {
            uint h = HashU32(seed ^ 0x9e3779b9u);
            h = HashU32(h ^ (0x85ebca6bu + (uint)ix));
            h = HashU32(h ^ (0xc2b2ae35u + (uint)iy));
            h = HashU32(h ^ (0x27d4eb2fu + (uint)iz));
            return h * (1.0 / 4294967296.0);
        }

        /// <summary>
        /// 3D value noise（quintic fade 三线性插值），输出 [0,1]
        /// （planet_math.h PlanetValueNoise——插值表达式逐式同序）。
        /// </summary>
        public static double ValueNoise(double x, double y, double z, uint seed)
        {
            double fx = Math.Floor(x);
            double fy = Math.Floor(y);
            double fz = Math.Floor(z);
            int ix = (int)fx;
            int iy = (int)fy;
            int iz = (int)fz;
            double tx = x - fx;
            double ty = y - fy;
            double tz = z - fz;
            double ux = tx * tx * tx * (tx * (tx * 6.0 - 15.0) + 10.0);
            double uy = ty * ty * ty * (ty * (ty * 6.0 - 15.0) + 10.0);
            double uz = tz * tz * tz * (tz * (tz * 6.0 - 15.0) + 10.0);
            double v000 = LatticeValue(ix, iy, iz, seed);
            double v100 = LatticeValue(ix + 1, iy, iz, seed);
            double v010 = LatticeValue(ix, iy + 1, iz, seed);
            double v110 = LatticeValue(ix + 1, iy + 1, iz, seed);
            double v001 = LatticeValue(ix, iy, iz + 1, seed);
            double v101 = LatticeValue(ix + 1, iy, iz + 1, seed);
            double v011 = LatticeValue(ix, iy + 1, iz + 1, seed);
            double v111 = LatticeValue(ix + 1, iy + 1, iz + 1, seed);
            double z00 = v000 + (v100 - v000) * ux;
            double z10 = v010 + (v110 - v010) * ux;
            double z01 = v001 + (v101 - v001) * ux;
            double z11 = v011 + (v111 - v011) * ux;
            double y0 = z00 + (z10 - z00) * uy;
            double y1 = z01 + (z11 - z01) * uy;
            return y0 + (y1 - y0) * uz;
        }

        /// <summary>
        /// 地形高度纯函数（planet_math.h PlanetTerrainHeightM）：输入单位球方向
        /// （调用方保证 |d|≈1，与原生同一契约），输出高程 m ∈
        /// [−TerrainMaxAmplitudeMeters, +TerrainMaxAmplitudeMeters]。4 octave fBm
        /// （输入偏移 11.3/7.7/3.1 避开原点对称性；每层种子 +o·1013904223）。
        /// 确定性契约：同 (seed, dx, dy, dz) 位形 → 同输出位形。
        /// </summary>
        public static double TerrainHeightM(uint seed, double dx, double dy, double dz)
        {
            double sum = 0.0;
            double amp = 1.0;
            double norm = 0.0;
            double baseFreq = PlanetRadiusMeters / TerrainBaseWavelengthMeters;
            double freq = baseFreq;
            for (uint o = 0; o < TerrainNoiseOctaves; ++o)
            {
                double n = ValueNoise(dx * freq + 11.3, dy * freq + 7.7, dz * freq + 3.1,
                    seed + o * 1013904223u);
                sum += amp * (n - 0.5);
                norm += amp;
                amp *= 0.5;
                freq *= 2.0;
            }

            return sum / norm * 2.0 * TerrainMaxAmplitudeMeters;
        }

        // ---- cube-sphere 格几何（planet_math.h CubeFaceBasisOf/CubeSphereDir/ChunkFaceCoord）----

        /// <summary>
        /// 标准化立方体映射（planet_math.h CubeSphereDir）：面坐标 (u,v ∈ [−1,1]) →
        /// 单位球方向。p = n + u·t1 + v·t2 → p/|p|（加乘序与原生逐式一致——跨面共享棱
        /// ±1 端点两侧逐位一致的构造性性质保持）。faceId：0=+Z 1=+X 2=−Z 3=−X 4=+Y 5=−Y。
        /// </summary>
        public static void CubeSphereDir(int faceId, double u, double v,
            out double x, out double y, out double z)
        {
            double nx;
            double ny;
            double nz;
            double t1x;
            double t1y;
            double t1z;
            double t2x;
            double t2y;
            double t2z;
            switch (faceId)
            {
                case 0: nx = 0.0; ny = 0.0; nz = 1.0; t1x = 1.0; t1y = 0.0; t1z = 0.0; t2x = 0.0; t2y = 1.0; t2z = 0.0; break;
                case 1: nx = 1.0; ny = 0.0; nz = 0.0; t1x = 0.0; t1y = 0.0; t1z = -1.0; t2x = 0.0; t2y = 1.0; t2z = 0.0; break;
                case 2: nx = 0.0; ny = 0.0; nz = -1.0; t1x = -1.0; t1y = 0.0; t1z = 0.0; t2x = 0.0; t2y = 1.0; t2z = 0.0; break;
                case 3: nx = -1.0; ny = 0.0; nz = 0.0; t1x = 0.0; t1y = 0.0; t1z = 1.0; t2x = 0.0; t2y = 1.0; t2z = 0.0; break;
                case 4: nx = 0.0; ny = 1.0; nz = 0.0; t1x = 1.0; t1y = 0.0; t1z = 0.0; t2x = 0.0; t2y = 0.0; t2z = -1.0; break;
                default: nx = 0.0; ny = -1.0; nz = 0.0; t1x = 1.0; t1y = 0.0; t1z = 0.0; t2x = 0.0; t2y = 0.0; t2z = 1.0; break;
            }

            double px = nx + u * t1x + v * t2x;
            double py = ny + u * t1y + v * t2y;
            double pz = nz + u * t1z + v * t2z;
            double invLen = 1.0 / Math.Sqrt(px * px + py * py + pz * pz);
            x = px * invLen;
            y = py * invLen;
            z = pz * invLen;
        }

        /// <summary>
        /// 全局顶点索引 → 面坐标（planet_math.h ChunkFaceCoord 的规范 double 表达式：
        /// 2g/total − 1——g/total 均为精确整数 double，商为正确舍入结果，跨档共享端点
        /// 逐位一致）。
        /// </summary>
        public static double ChunkFaceCoord(int globalIndex, int totalGrid)
            => 2.0 * globalIndex / totalGrid - 1.0;

        /// <summary>
        /// chunk 顶点全局索引（planet_math.h ChunkVertexIndex）：g = c·stride + i
        /// （stride = res−1，chunk 间共享边顶点复用）。
        /// </summary>
        public static int ChunkVertexIndex(int chunkCoord, int vertexIndex, int stride)
            => chunkCoord * stride + vertexIndex;
    }
}
