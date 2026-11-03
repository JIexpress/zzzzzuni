using System.Globalization;

namespace ZFrame.View
{
    /// <summary>
    /// HUD 数值格式化（纯函数、无 UnityEngine 依赖，便于 EditMode 覆盖）。
    /// 统一用 <see cref="CultureInfo.InvariantCulture"/>，避免区域设置把小数点换成逗号。
    /// 负数 / NaN / Infinity 一律降级为可读的占位值，**绝不让 "NaN" 出现在屏幕上**。
    /// </summary>
    public static class HudTextFormat
    {
        /// <summary>不可用时的占位符。</summary>
        public const string Unavailable = "—";

        /// <summary>整数计数：999 → "999"，1234 → "1.2k"，1,234,567 → "1.2M"。</summary>
        public static string Count(long value)
        {
            if (value < 0) value = 0;
            if (value < 1000) return value.ToString(CultureInfo.InvariantCulture);

            if (value < 1000000)
            {
                double thousands = value / 1000.0;
                // 999,999 四舍五入会变成 "1000.0k"——这个读法比 "1.0M" 更难认，直接进位。
                if (thousands < 999.95)
                {
                    return thousands.ToString("0.0", CultureInfo.InvariantCulture) + "k";
                }
            }

            return (value / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
        }

        /// <summary>字节 → MiB 文本：1572864 → "1.5 MB"。0 值为 "0.0 MB"，不会出现 NaN。</summary>
        public static string Megabytes(long bytes)
        {
            if (bytes < 0) bytes = 0;
            double mib = bytes / 1048576.0;
            return mib.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        /// <summary>毫秒：8.214 → "8.21 ms"；0 → "0.00 ms"；负值/NaN/∞ → "—"（表示不可用）。</summary>
        public static string Milliseconds(double milliseconds)
        {
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0)
            {
                return Unavailable;
            }

            return milliseconds.ToString("0.00", CultureInfo.InvariantCulture) + " ms";
        }

        /// <summary>FPS：59.94 → "59.9"；0 → "0.0"；负数/NaN/∞ → "—"。</summary>
        public static string Fps(double fps)
        {
            if (double.IsNaN(fps) || double.IsInfinity(fps) || fps < 0) return Unavailable;
            return fps.ToString("0.0", CultureInfo.InvariantCulture);
        }
    }
}
