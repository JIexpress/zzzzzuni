// CrowdAlive.cs —— 千人演练场群体存活组件（TASK-A-AND-08）。
// unmanaged POD：经 ZKernelWorld.RegisterComponent<T> 注册到 DataKernel（compId 4）。
// 简报口径「Alive→mask 位或 bool 组件」取 **bool 组件** 版：C# 的 bool 不是
// blittable，故用 1 字节 byte（0=死亡 1=存活）。相比「删组件当 mask 位」的变体，
// bool 组件的死亡/重生只需原地改一个字节——零布局变更、游标迭代内天然安全、
// 无需把死亡实体挪出查询再补回（Add/Remove 在游标回调内是禁止的）。

using System.Runtime.InteropServices;

namespace Game.View.Effects
{
    /// <summary>
    /// 群体实体存活标志（1 字节，compId = <see cref="KernelCrowdIds.Alive"/>）。
    /// 0 = 死亡（不移动、渲染矩阵置零即不可见、生命回复中）；非 0 = 存活。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct CrowdAlive
    {
        /// <summary>存活字节（0 = 死亡，1 = 存活）。</summary>
        public readonly byte Value;

        /// <summary>构造存活标志。</summary>
        public CrowdAlive(byte value)
        {
            Value = value;
        }

        /// <summary>存活常量。</summary>
        public static CrowdAlive Yes => new CrowdAlive(1);

        /// <summary>死亡常量。</summary>
        public static CrowdAlive No => new CrowdAlive(0);
    }
}
