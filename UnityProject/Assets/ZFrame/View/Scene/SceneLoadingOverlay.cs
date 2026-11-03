using UnityEngine;

namespace ZFrame.View
{
    /// <summary>
    /// Minimal player-visible loading overlay: full-screen black cover with a
    /// centered progress bar and a plain text readout, drawn with OnGUI.
    /// Intentionally unstyled — the M3 UI framework will replace this with
    /// the real loading screen; the draw call site (SceneLoader.OnGUI) stays
    /// the same. Compiles in ALL build types (player-visible feature, not a
    /// debug tool — no DEVELOPMENT_BUILD guard).
    /// </summary>
    public static class SceneLoadingOverlay
    {
        /// <summary>Draws the overlay for one OnGUI pass. Call only while a load is running.</summary>
        public static void Draw(float normalized, string sceneName)
        {
            var full = new Rect(0f, 0f, Screen.width, Screen.height);

            var previousColor = GUI.color;
            GUI.color = Color.black;
            GUI.DrawTexture(full, Texture2D.whiteTexture);
            GUI.color = previousColor;

            float barWidth = Mathf.Min(420f, Screen.width * 0.6f);
            float barHeight = 18f;
            float barX = (Screen.width - barWidth) * 0.5f;
            float barY = Screen.height * 0.5f - barHeight * 0.5f;

            var barBack = new Rect(barX, barY, barWidth, barHeight);
            var barFill = new Rect(barX + 2f, barY + 2f, (barWidth - 4f) * Mathf.Clamp01(normalized), barHeight - 4f);

            GUI.Box(barBack, GUIContent.none);
            GUI.color = new Color(0.85f, 0.87f, 0.9f);
            GUI.DrawTexture(barFill, Texture2D.whiteTexture);
            GUI.color = previousColor;

            var labelRect = new Rect(barX, barY + barHeight + 12f, barWidth, 24f);
            string scene = string.IsNullOrEmpty(sceneName) ? "(unknown)" : sceneName;
            GUI.Label(labelRect, $"正在加载 {scene}  {Mathf.RoundToInt(Mathf.Clamp01(normalized) * 100f)}%");
        }
    }
}
