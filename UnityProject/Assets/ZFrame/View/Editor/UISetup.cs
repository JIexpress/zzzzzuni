#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace ZFrame.View
{
    /// <summary>
    /// One-shot asset setup for the UI framework sample panel (TASK-A-M3-01):
    /// code-generates the committed UGUI prefab
    /// <c>Assets/UI/Resources/ui_test_panel.prefab</c> — a full-screen Popup
    /// root (dim blocker Image with raycastTarget = real input blocking, CanvasGroup
    /// for the fade) carrying <see cref="TestPanelView"/>, a status Text and a
    /// Button. The prefab sits inside a Resources folder so the resource key
    /// "ui_test_panel" resolves through <c>ZFrame.Data.ResourcesProvider</c>;
    /// the FILE NAME mirrors the key exactly (Resources keys are case-sensitive
    /// on Android — the deliberate deviation from the UI_ asset prefix is noted
    /// in the task log). Idempotent: a valid prefab is left untouched. Run via
    /// menu (ZFrame/Setup/Generate UI_TestPanel prefab) or batch:
    ///   -executeMethod ZFrame.View.UISetup.SetupFromCommandLine
    /// Editor-only by the #if UNITY_EDITOR guard (same pattern as AudioMixerSetup).
    /// </summary>
    public static class UISetup
    {
        private const string UiDir = "Assets/UI/Resources";
        private const string PrefabPath = UiDir + "/ui_test_panel.prefab";
        private const string ResourceKey = "ui_test_panel";

        [MenuItem("ZFrame/Setup/Generate UI_TestPanel prefab")]
        public static void Setup()
        {
            bool created = EnsurePrefab();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(created
                ? $"[UISetup] sample panel prefab (re)generated at {PrefabPath}"
                : $"[UISetup] valid sample panel prefab already present at {PrefabPath} — no change");
        }

        /// <summary>Batch entry: ensure the prefab, exit 0 on success (regenerated OR already valid).</summary>
        public static void SetupFromCommandLine()
        {
            EnsurePrefab();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorApplication.Exit(0);
        }

        /// <summary>Returns true when the prefab was (re)generated, false when already valid.</summary>
        public static bool EnsurePrefab()
        {
            System.IO.Directory.CreateDirectory(UiDir);

            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            string reason;
            if (existing != null)
            {
                if (Validate(existing, out reason))
                {
                    return false;
                }

                Debug.LogWarning($"[UISetup] existing prefab invalid ({reason}) — regenerating.");
                AssetDatabase.DeleteAsset(PrefabPath);
            }

            BuildPrefab();

            // Persist, then validate the RELOADED on-disk artifact.
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(PrefabPath);
            var reloaded = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (reloaded == null)
            {
                throw new InvalidOperationException("[UISetup] prefab did not reload after import.");
            }

            if (!Validate(reloaded, out reason))
            {
                throw new InvalidOperationException($"[UISetup] generated prefab failed validation: {reason}");
            }

            Debug.Log($"[UISetup] created {PrefabPath}: blocker + status Text + Button, TestPanelView wired, key '{ResourceKey}'");
            return true;
        }

        /// <summary>Acceptance check: TestPanelView present with both references wired, Text/Button reachable, dim blocker in place.</summary>
        public static bool Validate(GameObject prefab, out string reason)
        {
            if (prefab == null)
            {
                reason = "prefab is null";
                return false;
            }

            var view = prefab.GetComponent<TestPanelView>();
            if (view == null)
            {
                reason = "missing TestPanelView on root";
                return false;
            }

            var serialized = new SerializedObject(view);
            if (serialized.FindProperty("_statusText")?.objectReferenceValue == null)
            {
                reason = "TestPanelView._statusText not wired";
                return false;
            }

            if (serialized.FindProperty("_testButton")?.objectReferenceValue == null)
            {
                reason = "TestPanelView._testButton not wired";
                return false;
            }

            if (prefab.GetComponent<CanvasGroup>() == null)
            {
                reason = "missing CanvasGroup (fade target)";
                return false;
            }

            var blocker = prefab.GetComponent<Image>();
            if (blocker == null || !blocker.raycastTarget)
            {
                reason = "missing raycast-blocking Image on root (BlockMask would not block input)";
                return false;
            }

            if (view.TestButton == null)
            {
                reason = "TestButton reference lost";
                return false;
            }

            reason = null;
            return true;
        }

        private static void BuildPrefab()
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var root = new GameObject("UI_TestPanel", typeof(RectTransform));
            Stretch(root.GetComponent<RectTransform>());

            // Full-screen dim blocker: raycastTarget=true means clicks on the popup
            // never reach lower layers (the physical side of PanelConfig.BlockMask).
            var blocker = root.AddComponent<Image>();
            blocker.color = new Color(0f, 0f, 0f, 0.6f);
            blocker.raycastTarget = true;

            root.AddComponent<CanvasGroup>();
            var view = root.AddComponent<TestPanelView>();

            // Status text (top strip).
            var textGo = new GameObject("StatusText", typeof(RectTransform));
            var textRect = textGo.GetComponent<RectTransform>();
            textRect.SetParent(root.transform, false);
            textRect.anchorMin = new Vector2(0f, 0.8f);
            textRect.anchorMax = new Vector2(1f, 0.95f);
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            var text = textGo.AddComponent<Text>();
            text.font = font;
            text.fontSize = 42;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.raycastTarget = false;
            text.text = "UI_TestPanel ready";

            // Test button (below center).
            var buttonGo = new GameObject("TestButton", typeof(RectTransform));
            var buttonRect = buttonGo.GetComponent<RectTransform>();
            buttonRect.SetParent(root.transform, false);
            buttonRect.anchorMin = new Vector2(0.5f, 0.5f);
            buttonRect.anchorMax = new Vector2(0.5f, 0.5f);
            buttonRect.sizeDelta = new Vector2(360f, 120f);
            buttonRect.anchoredPosition = new Vector2(0f, -60f);
            var buttonImage = buttonGo.AddComponent<Image>();
            buttonImage.color = new Color(0.25f, 0.45f, 0.7f);
            var button = buttonGo.AddComponent<Button>();

            var buttonTextGo = new GameObject("Text", typeof(RectTransform));
            var buttonTextRect = buttonTextGo.GetComponent<RectTransform>();
            buttonTextRect.SetParent(buttonGo.transform, false);
            Stretch(buttonTextRect);
            var buttonText = buttonTextGo.AddComponent<Text>();
            buttonText.font = font;
            buttonText.fontSize = 36;
            buttonText.alignment = TextAnchor.MiddleCenter;
            buttonText.color = Color.white;
            buttonText.raycastTarget = false;
            buttonText.text = "点击我";

            // Wire the view's serialized references before saving.
            var serialized = new SerializedObject(view);
            serialized.FindProperty("_statusText").objectReferenceValue = text;
            serialized.FindProperty("_testButton").objectReferenceValue = button;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            UnityEngine.Object.DestroyImmediate(root);
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
#endif
