using System;
using System.Linq;
using Game.Domain.Colony;

namespace Game.Domain.Colony.Sim
{
    /// <summary>
    /// 教学四级提示（交互与教学 §4）：世界 → 环境 → 一句话 → UI+永久关闭。
    /// >90 秒没再失败则计数归零（demo 用「隔了 1 个游戏日」近似）。
    /// 验收：全程零 UI 文字也能让玩家明白（§10 MVP 七步）。
    /// </summary>
    public class HintState
    {
        public int FailCount;
        public uint LastFailDay;
        public bool Dismissed;   // 玩家点「我知道了」→ 永久关闭该交互提示

        public HintLevel Current => Dismissed ? HintLevel.None
            : FailCount == 0 ? HintLevel.World
            : FailCount <= 2 ? HintLevel.Environment
            : FailCount <= 4 ? HintLevel.Line
            : HintLevel.Ui;

        public void OnFail(uint day)
        {
            if (day - LastFailDay > 1) FailCount = 0;   // 间隔久 → 先归零再计
            FailCount++;
            LastFailDay = day;
        }
    }

    public enum HintLevel : byte { None, World, Environment, Line, Ui }

    public class TeachingSystem
    {
        public HintState Hint = new();
        public bool FirstFailExempt = true;   // §7.1 首败豁免：首次尝试不结算损失，用世界语言给原因

        /// <summary>§3 NPC 示范：选外向且职业匹配的人——他不说话，示范；你盯着看太久他才会说「要试试吗？」。</summary>
        public NamedNpc PickDemonstrator(GameWorld w, string requiredJob)
            => w.Named.FirstOrDefault(n => n.Extraversion > 0.6f && n.Job == requiredJob)
               ?? w.Named.OrderByDescending(n => n.Extraversion).First();
    }

    /// <summary>辐射预算（真实性与厚重感 §3.2）：辐射是可用时间预算，不是血条。</summary>
    public class RadiationBudget
    {
        public float AccumulatedDose;      // Sv
        public float CurrentRate = 0.005f; // Sv/h（示例：5 mSv/h）
        public float AnnualLimit = 0.05f;  // 剩余额度 50 mSv

        public float SafeWorkHours => Math.Max(0f, (AnnualLimit - AccumulatedDose) / CurrentRate);
        public void Work(float hours) => AccumulatedDose += CurrentRate * hours;

        /// <summary>慢性代价（§3.3）：年剂量超限 → 上限永久降低（不可逆三支柱）。</summary>
        public string AnnualSettle()
            => AccumulatedDose > 0.02f
                ? $"年剂量 {AccumulatedDose * 1000:0} mSv —— 健康上限永久 -0.02，癌症风险 +0.005（几十年后可能发作）。"
                : "年剂量在安全线内。";
    }
}
