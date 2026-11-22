using System;
using System.Collections.Generic;

namespace Game.Domain.Colony
{
    /// <summary>§4.2 三阶段：认同 → 质疑 → 沉默 → 离开。阶段三不可逆。</summary>
    public enum Stage : byte
    {
        Aligned = 0,   // 认同
        Doubt   = 1,   // 质疑：会上提不同意见，容易挽回
        Silent  = 2,   // 沉默：不再提意见只做分内事，配合度缓慢下降，最难察觉
        Left    = 3,   // 离开：辞呈 / 调离 / 倒戈，不可逆
    }

    /// <summary>§4.3 四种离开方式：怎么走，比走不走更重要。</summary>
    public enum LeaveStyle : byte
    {
        QuietTransfer,   // 科学家式：沉默 → 申请调离（后知后觉，最难受）
        SlamDoor,        // 工程师式：当面质问 → 摔门（直接冲击）
        TurnYou,         // 激进派式：不走，反过来劝你更狠；你不狠他才走
        WriteItDown,     // 保守派式：安静离开，然后把经历写下来（长期名声代价）
    }

    /// <summary>
    /// 信念契约（§4.1）：每个核心 NPC 只写一条信念 + 两条 Won't。
    /// 玩家违背 Won't → 违背次数超过容忍 → 直接离开（不可逆）；否则阶段 +1（最高到沉默）。
    /// 时间线上（MVP 3-5）：违背 → 质疑；30 天不修复 → 沉默；再 30 天 → 离开并带走一条情报。
    /// </summary>
    public class BeliefContract
    {
        public string CoreBelief;          // 一条信念（§4.1：科学家"知识不能用来杀人"式）
        public List<string> Wonts;         // 绝不做的两件事，会跟玩家起冲突的那种
        public int Tolerance = 1;          // §10：信念 Tolerance 1~2，核心 NPC 给 1
        public int Violations;

        public Stage Stage = Stage.Aligned;
        public int DaysInStage;
        public float Cooperation = 1.0f;   // 配合度：阶段二每天 -0.01（§10），1.0 → 0.7 约 30 天
        public LeaveStyle LeaveStyle = LeaveStyle.QuietTransfer;
        public string IntelCarried;        // MVP 5：离开时带走一条情报

        private int _daysSinceViolation;

        /// <summary>
        /// 距上次违背的天数（TASK-A-COL-03 存档字段：不存档会把 30/60 天升级线清零，
        /// 读档后沉默/离开时点整体漂移）。同程序集 Capture/Restore 专用。
        /// </summary>
        internal int DaysSinceViolation
        {
            get => _daysSinceViolation;
            set => _daysSinceViolation = value;
        }

        /// <summary>玩家做了违背 Won't 的事（§4.1 原逻辑）。</summary>
        public void OnViolation()
        {
            if (Stage == Stage.Left) return;
            Violations++;
            _daysSinceViolation = 0;
            if (Violations > Tolerance)
                Stage = Stage.Left;                      // 不可逆
            else
                Stage = (Stage)Math.Min((byte)Stage + 1, (byte)Stage.Silent);  // Min(Stage+1, 2)
            DaysInStage = 0;
        }

        /// <summary>每天的推进：时间不修复也会升级（MVP 4/5 的 30 天规则）。</summary>
        /// <returns>阶段发生变化时返回 true（供上层出事件）。</returns>
        public bool DailyTick()
        {
            if (Stage == Stage.Left) return false;
            DaysInStage++;
            _daysSinceViolation++;

            if (Stage == Stage.Silent)
                Cooperation = Math.Max(0.7f, Cooperation - 0.01f);   // §10：阶段二配合度衰减 -0.01/天

            bool changed = false;
            if (Stage == Stage.Doubt && _daysSinceViolation > 30) { Stage = Stage.Silent; DaysInStage = 0; changed = true; }
            if (Stage == Stage.Silent && _daysSinceViolation > 60) { Stage = Stage.Left;  DaysInStage = 0; changed = true; }
            return changed;
        }

        /// <summary>玩家真诚修复。阶段一容易挽回，阶段二难且容易被忽略（§4.2）。</summary>
        public bool MakeAmends()
        {
            if (Stage == Stage.Left) return false;
            if (Stage == Stage.Doubt) { Violations = 0; Stage = Stage.Aligned; Cooperation = 1.0f; }
            else if (Stage == Stage.Silent) { Violations = 0; Stage = Stage.Doubt; }   // 难：只回一档，且配合度不回满
            DaysInStage = 0;
            return true;
        }

        public void ForceLeave(string intel) { Stage = Stage.Left; IntelCarried = intel; }

        /// <summary>阶段三按 LeaveStyle 给出不同的走法文本（§4.3）。</summary>
        public string LeaveLine() => LeaveStyle switch
        {
            LeaveStyle.QuietTransfer => "调离申请已经放在你桌上了。没有第二个字。",
            LeaveStyle.SlamDoor      => "「我不伺候了。」——门是摔上的。",
            LeaveStyle.TurnYou       => "「我不走。我要看着你把这地方管成什么样。」——他没走，他在等你更狠。",
            LeaveStyle.WriteItDown   => "他安静地走了。三个月后，一篇手记开始在殖民地流通。",
            _ => "",
        };
    }
}
