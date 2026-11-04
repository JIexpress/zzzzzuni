// CrowdPosition.cs —— 千人演练场群体位置组件（TASK-A-R9-13）。
// unmanaged POD：经 ZKernelWorld.RegisterComponent<T> 注册到 DataKernel（compId 0）。
// 公开 readonly 字段是 file-conventions §2「禁 public 字段」的既定例外：
// blittable 组件须逐字段封送（同 ZFrame.Render.Bindings 的 KcPos / ZrNative POD）。

using System.Runtime.InteropServices;

namespace Game.View.Effects
{
    /// <summary>
    /// 群体实体位置（12 字节，compId = <see cref="KernelCrowdIds.Position"/>）。
    /// 初始 X = 实体序号 0..N-1（便于 1000 实体求和断言），Y/Z 为高度与深度分量。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct CrowdPosition
    {
        /// <summary>世界 X（内核语义：本演练场用其承载实体序号，回绕保持 0..N-1）。</summary>
        public readonly float X;

        /// <summary>世界 Y。</summary>
        public readonly float Y;

        /// <summary>世界 Z。</summary>
        public readonly float Z;

        /// <summary>构造位置分量。</summary>
        public CrowdPosition(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }
}
