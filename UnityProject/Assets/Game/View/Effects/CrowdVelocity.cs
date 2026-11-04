// CrowdVelocity.cs —— 千人演练场群体速度组件（TASK-A-R9-13）。
// unmanaged POD：经 ZKernelWorld.RegisterComponent<T> 注册到 DataKernel（compId 1）。
// 公开 readonly 字段是 blittable 封送需要的既定例外（见 CrowdPosition.cs 注释）。

using System.Runtime.InteropServices;

namespace Game.View.Effects
{
    /// <summary>
    /// 群体实体速度（12 字节，compId = <see cref="KernelCrowdIds.Velocity"/>）。
    /// 每帧被 <see cref="KernelCrowdDriver"/> 读出用于积分，再由 SetComponent 回写。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct CrowdVelocity
    {
        /// <summary>X 方向速度。</summary>
        public readonly float Dx;

        /// <summary>Y 方向速度。</summary>
        public readonly float Dy;

        /// <summary>Z 方向速度。</summary>
        public readonly float Dz;

        /// <summary>构造速度分量。</summary>
        public CrowdVelocity(float dx, float dy, float dz)
        {
            Dx = dx;
            Dy = dy;
            Dz = dz;
        }
    }
}
