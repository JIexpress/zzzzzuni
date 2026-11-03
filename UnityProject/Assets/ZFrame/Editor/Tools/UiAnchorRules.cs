using UnityEngine;

namespace ZFrame.Editor
{
    /// <summary>
    /// UI 面板锚点规则的纯判定逻辑（无 UnityEditor 依赖，供单测直接覆盖）。
    /// 预设集合（TASK-A-M5-01 简报）：拉伸 / 角落 / 中心。
    ///  - 拉伸：任一轴 min != max（成对拉伸，含全屏、上下带边距的横条等比例布局）；
    ///  - 角落：min == max 且两个分量都取 {0, 1}；
    ///  - 中心：min == max 且两个分量都取 0.5；
    ///  - 边中点（如 (0.5, 0)）视作角落集合的单轴特例，同属预设。
    /// 其余（如 (0.5, 0.34) 任意比例点锚）不在预设集合内，产出告警。
    /// </summary>
    public static class UiAnchorRules
    {
        /// <summary>锚点是否属于预设集合（拉伸/角落/中心/边中点）。</summary>
        public static bool IsPreset(Vector2 min, Vector2 max)
        {
            return PresetName(min, max) != null;
        }

        /// <summary>
        /// 返回预设类别名（"拉伸"/"角落"/"边中点"/"中心"）；不在预设集合时返回 null。
        /// </summary>
        public static string PresetName(Vector2 min, Vector2 max)
        {
            if (min.x != max.x || min.y != max.y)
            {
                return "拉伸";
            }

            var xIsMid = Approximately(min.x, 0.5f);
            var yIsMid = Approximately(min.y, 0.5f);
            var xIsEdge = Approximately(min.x, 0f) || Approximately(min.x, 1f);
            var yIsEdge = Approximately(min.y, 0f) || Approximately(min.y, 1f);

            if (xIsMid && yIsMid)
            {
                return "中心";
            }

            if (xIsEdge && yIsEdge)
            {
                return "角落";
            }

            if ((xIsMid && yIsEdge) || (xIsEdge && yIsMid))
            {
                return "边中点";
            }

            return null;
        }

        private static bool Approximately(float a, float b)
        {
            return Mathf.Approximately(a, b);
        }
    }
}
