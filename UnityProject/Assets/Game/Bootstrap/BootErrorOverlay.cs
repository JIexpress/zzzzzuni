using UnityEngine;
using UnityEngine.UI;

namespace Game.Bootstrap
{
    /// <summary>
    /// On-screen boot error surface (TASK-A-AND-03): one top-most legacy UGUI
    /// Text that shows boot-chain failures as plain lines instead of leaving
    /// the player staring at the boot scene's clear color ("黑屏盲等").
    ///
    /// Zero assets: the host GameObject, Canvas and Text are created at
    /// runtime; the font comes from the engine built-ins (LegacyRuntime.ttf,
    /// Arial.ttf fallback — both always present in a player). Idle cost when
    /// nothing failed: one dormant GameObject with an inactive Text — the
    /// host is created lazily on the first Show call, so a clean boot adds
    /// nothing at all.
    ///
    /// Not an exception-handling policy: callers still log the error to
    /// <see cref="ZFrame.View.Diagnostics.DeviceFileLogger"/>; this type only
    /// makes the failure VISIBLE on the device screen where logcat is
    /// suppressed (Huawei/HarmonyOS).
    /// </summary>
    public static class BootErrorOverlay
    {
        private const int SortingOrder = 30000; // above every UIRoot layer canvas

        private static Text _text;
        private static int _errorCount;

        /// <summary>Shows one error line on screen. Safe to call repeatedly and from anywhere.</summary>
        public static void Show(string message)
        {
            EnsureHost();
            if (_text == null)
            {
                return; // overlay creation failed — file log still carries the message
            }

            _errorCount++;
            _text.gameObject.SetActive(true);
            _text.text = $"BOOT ERROR #{_errorCount}\n{message}\n{_text.text}";
        }

        /// <summary>
        /// Non-error boot status line (TASK-A-AND-03 instrument): same overlay,
        /// no "ERROR" prefix, prepended like Show. Lets a Development package
        /// prove on-screen UGUI render-ability without touching logcat
        /// (suppressed on the affected device). Callers gate on dev builds.
        /// </summary>
        public static void Info(string message)
        {
            EnsureHost();
            if (_text == null)
            {
                return;
            }

            _text.gameObject.SetActive(true);
            _text.text = $"{message}\n{_text.text}";
        }

        /// <summary>Destroys the overlay host (tests). Safe when never shown.</summary>
        public static void Shutdown()
        {
            if (_text != null)
            {
                Object.Destroy(_text.gameObject);
                _text = null;
            }

            _errorCount = 0;
        }

        private static void EnsureHost()
        {
            if (_text != null)
            {
                return;
            }

            var host = new GameObject("~BootErrorOverlay");
            Object.DontDestroyOnLoad(host);

            var canvas = host.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;
            host.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            host.AddComponent<GraphicRaycaster>(); // present for completeness; the text ignores raycasts

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(host.transform, false);
            _text = textGo.AddComponent<Text>();
            _text.font = ResolveFont();
            _text.fontSize = 28;
            _text.color = new Color(1f, 0.35f, 0.25f);
            _text.alignment = TextAnchor.UpperLeft;
            _text.raycastTarget = false;
            _text.horizontalOverflow = HorizontalWrapMode.Wrap;
            _text.verticalOverflow = VerticalWrapMode.Overflow;
            var rect = _text.rectTransform;
            rect.anchorMin = new Vector2(0.02f, 0.02f);
            rect.anchorMax = new Vector2(0.98f, 0.35f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            textGo.SetActive(false); // nothing failed yet — keep the screen clean
        }

        private static Font ResolveFont()
        {
            // 2022.3 renamed the built-in font asset; try the new name first.
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
            {
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }

            return font;
        }
    }
}
