using System;
using UnityEngine;

namespace Game.Domain.World
{
    /// <summary>
    /// TASK-A-WORLD-01：浮点原点的纯数学层（引擎无关，仅用 Vector3 作输出容器，EditMode 可断言）。
    ///
    /// 数学口径（与 Native/tests/planet_math.h 的 B-TECH-03 同一套思路）：
    ///  - 世界坐标 <c>p</c> 与原点偏移 <c>origin</c> 都是 double；
    ///  - Unity 坐标 = <c>(float)(p - origin)</c>：**先在 double 域相减，最后一次转 float**。
    ///    顺序反了（先各自转 float 再相减）就等于没有 double——100km 处 7.8mm 的量化
    ///    误差会原封不动回到相对坐标里（TASK 的 V7 变异点）。
    ///  - 重定位：把相机当前相对原点的偏移整体减掉（delta），原点 += delta；
    ///    物体的世界坐标因此在 double 域守恒。
    /// </summary>
    public static class WorldOriginMath
    {
        /// <summary>
        /// 是否触发重定位。距离 <paramref name="thresholdMeters"/> 不动；**等于**阈值即触发
        /// （几何上已经到界，且能保证重定位后的偏移严格小于阈值，不会每帧反复触发）。
        /// 阈值 ≤ 0 或 NaN 视为非法配置：永不重定位（并让运行时静默不动）。
        /// </summary>
        public static bool ShouldRebase(double offsetMeters, double thresholdMeters)
        {
            if (!(thresholdMeters > 0.0) || double.IsNaN(thresholdMeters) || double.IsInfinity(thresholdMeters))
            {
                return false;
            }

            if (double.IsNaN(offsetMeters) || double.IsInfinity(offsetMeters))
            {
                return false;
            }

            return offsetMeters >= thresholdMeters;
        }

        /// <summary>是否触发重定位（相机世界坐标 vs 当前原点偏移）。</summary>
        public static bool ShouldRebase(WorldPosition cameraWorld, WorldPosition originOffset, double thresholdMeters)
            => ShouldRebase(OffsetMeters(cameraWorld, originOffset), thresholdMeters);

        /// <summary>相机相对当前浮点原点的距离（米，double）。</summary>
        public static double OffsetMeters(WorldPosition cameraWorld, WorldPosition originOffset)
            => WorldPosition.Distance(cameraWorld, originOffset);

        /// <summary>
        /// 本次重定位要整体减掉的平移量（Unity 空间）。未达阈值时返回 <see cref="Vector3.zero"/>，
        /// 调用方据此判断「这一帧不需要动」。
        /// </summary>
        public static Vector3 RebaseDelta(WorldPosition cameraWorld, WorldPosition originOffset, double thresholdMeters)
        {
            if (!ShouldRebase(cameraWorld, originOffset, thresholdMeters))
            {
                return Vector3.zero;
            }

            return ToUnity(cameraWorld, originOffset);
        }

        /// <summary>
        /// 世界坐标 → 当前原点下的 Unity 坐标。**顺序铁律：先 double 相减，最后一次转 float。**
        /// 先转 float 再减偏移会丢掉全部 double 精度（V7 正是钉这一点）。
        /// </summary>
        public static Vector3 ToUnity(WorldPosition world, WorldPosition originOffset)
        {
            double dx = world.X - originOffset.X;
            double dy = world.Y - originOffset.Y;
            double dz = world.Z - originOffset.Z;

            return new Vector3((float)dx, (float)dy, (float)dz);
        }

        /// <summary>Unity 坐标 → 世界坐标（float→double 是精确加宽，无新增误差）。</summary>
        public static WorldPosition ToWorld(Vector3 unityPosition, WorldPosition originOffset)
            => new WorldPosition(
                originOffset.X + unityPosition.x,
                originOffset.Y + unityPosition.y,
                originOffset.Z + unityPosition.z);

        /// <summary>
        /// TASK-B-PLANET-07：行星通道专用的相机相对视图（viewRel，纯旋转——平移分量恰好为 0）。
        ///
        /// 这是 A-WORLD-01 顺序铁律在行星接线上的延伸（P2-行星ABI设计.md §3.1：托管侧新
        /// 边界点必须并入 <see cref="WorldOriginMath"/> 同一处）：
        /// 相机的绝对平移**绝不进 float 视图矩阵**——它经 <c>ZR_Unity_SetCameraOrigin(double)</c>
        /// 以 double 直传原生，由原生在 double 域计算 chunk 模型平移
        /// <c>float(anchorAbs − camAbs)</c>（设计稿 §3.2/D3）。若把相机平移（重定位爬升段
        /// 可达 9010km）float 化后塞回视图，ULP(幅值)≈1m 的抖动会原封不动回到画面。
        ///
        /// 实现上这意味着「double 域构造后一次 cast」的行星版等价形态：视图的平移列
        /// 直接归零（一次都不 cast），只保留 worldToCameraMatrix 的旋转部分——旋转元素
        /// 由相机四元数（局部量）决定，与相机离原点的距离无关，无大坐标量化问题。
        /// 调用点（WorldOriginPlanetLink）**禁止**在本函数之外自建行星视图，保证
        /// 「平移归零」只有一份实现（变异点：任何把平移列留下/塞值的改法都会被
        /// WorldOriginPlanetViewTests 的方向不变性 + 平移恒零断言钉红）。
        /// </summary>
        public static UnityEngine.Matrix4x4 ToCameraRelativeView(UnityEngine.Matrix4x4 worldToCamera)
        {
            worldToCamera.m03 = 0f;
            worldToCamera.m13 = 0f;
            worldToCamera.m23 = 0f;
            return worldToCamera;
        }

        /// <summary>
        /// 幅值 <paramref name="meters"/> 处 float 的 ULP（相邻可表示值的间距）——
        /// 顶点抖动的量化下限。100km 处 = 2^-7 = 7.8125mm（V6 的证据函数）。
        /// </summary>
        public static double FloatUlpAt(double meters)
        {
            if (!(meters > 0.0) || double.IsInfinity(meters))
            {
                return 0.0;
            }

            float value = (float)meters;
            if (float.IsInfinity(value) || value <= 0f)
            {
                return 0.0;
            }

            int bits = BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
            float next = BitConverter.ToSingle(BitConverter.GetBytes(bits + 1), 0);
            return next - (double)value;
        }

        /// <summary>float 直接表示 <paramref name="meters"/> 的量化误差（米）。</summary>
        public static double FloatQuantizationError(double meters)
            => Math.Abs((double)(float)meters - meters);
    }
}
