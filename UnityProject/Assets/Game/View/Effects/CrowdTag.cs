// CrowdTag.cs —— 千人演练场群体标签组件（TASK-A-R9-13）。
// unmanaged POD：经 ZKernelWorld.RegisterComponent<T> 注册到 DataKernel（compId 2）。
// 关键用途：游标迭代只交付 SoA 列指针、不交付实体句柄；Tag 列随行携带
// 实体序号，驱动据此把积分结果按序号 SetComponent 回写，不依赖「行序==spawn 序」假设。

using System.Runtime.InteropServices;

namespace Game.View.Effects
{
    /// <summary>
    /// 群体实体标签（4 字节，compId = <see cref="KernelCrowdIds.Tag"/>）。
    /// 值恒等于实体生成序号（0..N-1），是回写与实例矩阵数组的下标。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct CrowdTag
    {
        /// <summary>实体序号。</summary>
        public readonly uint Value;

        /// <summary>构造标签。</summary>
        public CrowdTag(uint value)
        {
            Value = value;
        }
    }
}
