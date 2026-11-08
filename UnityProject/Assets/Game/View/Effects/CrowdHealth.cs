// CrowdHealth.cs —— 千人演练场群体生命组件（TASK-A-AND-08）。
// unmanaged POD：经 ZKernelWorld.RegisterComponent<T> 注册到 DataKernel（compId 3）。
// 公开 readonly 字段是 file-conventions §2「禁 public 字段」的既定例外：
// blittable 组件须逐字段封送（同 CrowdPosition.cs / ZFrame.Render.Bindings 的 KcPos）。

using System.Runtime.InteropServices;

namespace Game.View.Effects
{
    /// <summary>
    /// 群体实体生命值（4 字节，compId = <see cref="KernelCrowdIds.Health"/>）。
    /// 行为语义（<see cref="KernelCrowdDriver"/>，全部经 kernel 游标原地读写）：
    /// 存活时按 <see cref="KernelCrowdIds.DecayPerSecond"/> 缓慢衰减，≤0 即死亡
    /// （Alive 位置 0、Health 置为重生窗起点）；死亡期间按
    /// <see cref="KernelCrowdIds.RespawnHealPerSecond"/> 回复，回到 ≥0 即重生
    /// （Alive 置 1、Health 重置为初始值）。死亡/重生只是数据写入，零布局变更，
    /// 与游标迭代天然兼容。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct CrowdHealth
    {
        /// <summary>生命值（秒计量：初始 12..36，衰减 0.1/s → 正常游玩 120..360s 后才会自然死亡）。</summary>
        public readonly float Value;

        /// <summary>构造生命分量。</summary>
        public CrowdHealth(float value)
        {
            Value = value;
        }
    }
}
