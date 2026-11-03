#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace ZFrame.View
{
    /// <summary>
    /// One-shot asset setup for the settings panel frame (TASK-A-M3-02):
    /// code-generates the committed UGUI prefab
    /// <c>Assets/UI/Resources/ui_settings_panel.prefab</c> — a full-screen
    /// blocking Panel root (dim blocker Image with raycastTarget, CanvasGroup
    /// for the fade) carrying <see cref="SettingsPanelView"/>, a title Text,
    /// the TAB BAR host (page buttons are generated at open time from the
    /// registry), the ROW HOST (rows are generated at open time by
    /// <see cref="SettingsRowFactory"/> — the prefab holds the host container
    /// only, zero per-option layout) and the three bottom buttons
    /// 应用并关闭/撤销更改/恢复默认.
    ///
    /// The prefab sits inside a Resources folder so the resource key
    /// "ui_settings_panel" resolves through <c>ZFrame.Data.ResourcesProvider</c>
    /// (file name mirrors the key exactly — the A-M3-01 precedent).
    /// Idempotent: a valid prefab is left untouched. Run via menu
    /// (ZFrame/Setup/Generate UI_SettingsPanel prefab) or batch:
    ///   -executeMethod ZFrame.View.SettingsUiSetup.SetupFromCommandLine
    /// Editor-only by the #if UNITY_EDITOR guard (same pattern as UISetup).
    /// </summary>
    public static class SettingsUiSetup
    {
        private const string UiDir = "Assets/UI/Resources";
        private const string PrefabPath = UiDir + "/ui_settings_panel.prefab";
        private const string ResourceKey = "ui_settings_panel";

        [MenuItem("ZFrame/Setup/Generate UI_SettingsPanel prefab")]
        public static void Setup()
        {
            bool created = EnsurePrefab();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(created
                ? $"[SettingsUiSetup] settings panel prefab (re)generated at {PrefabPath}"
                : $"[SettingsUiSetup] valid settings panel prefab already present at {PrefabPath} — no change");
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

                Debug.LogWarning($"[SettingsUiSetup] existing prefab invalid ({reason}) — regenerating.");
                AssetDatabase.DeleteAsset(PrefabPath);
            }

            BuildPrefab();

            // Persist, then validate the RELOADED on-disk artifact.
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(PrefabPath);
            var reloaded = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (reloaded == null)
            {
                throw new InvalidOperationException("[SettingsUiSetup] prefab did not reload after import.");
            }

            if (!Validate(reloaded, out reason))
            {
                throw new InvalidOperationException($"[SettingsUiSetup] generated prefab failed validation: {reason}");
            }

            Debug.Log($"[SettingsUiSetup] created {PrefabPath}: blocker + title + tab bar + row host + 3 buttons, SettingsPanelView wired, key '{ResourceKey}'");
            return true;
        }

        /// <summary>Acceptance check: SettingsPanelView present with every reference wired, blocker in place, three buttons reachable.</summary>
        public static bool Validate(GameObject prefab, out string reason)
        {
            if (prefab == null)
            {
                reason = "prefab is null";
                return false;
            }

            var view = prefab.GetComponent<SettingsPanelView>();
            if (view == null)
            {
                reason = "missing SettingsPanelView on root";
                return false;
            }

            var serialized = new SerializedObject(view);
            foreach (string field in new[] { "_titleText", "_tabBar", "_rowHost", "_applyButton", "_revertButton", "_resetButton" })
            {
                if (serialized.FindProperty(field)?.objectReferenceValue == null)
                {
                    reason = $"SettingsPanelView.{field} not wired";
                    return false;
                }
            }

            if (prefab.GetComponent<CanvasGroup>() == null)
            {
                reason = "missing CanvasGroup (fade target)";
                return false;
            }

            Image blocker = prefab.GetComponent<Image>();
            if (blocker == null || !blocker.raycastTarget)
            {
                reason = "missing raycast-blocking Image on root (BlockMask would not block input)";
                return false;
            }

            if (prefab.transform.Find("BottomBar/ApplyButton") == null ||
                prefab.transform.Find("BottomBar/RevertButton") == null ||
                prefab.transform.Find("BottomBar/ResetButton") == null)
            {
                reason = "bottom bar buttons missing";
                return false;
            }

            reason = null;
            return true;
        }

        private static void BuildPrefab()
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var root = new GameObject("UI_SettingsPanel", typeof(RectTransform));
            Stretch(root.GetComponent<RectTransform>());

            // Full-screen dim blocker: raycastTarget=true means clicks on the panel
            // never reach lower layers (the physical side of PanelConfig.BlockMask).
            Image blocker = root.AddComponent<Image>();
            blocker.color = new Color(0f, 0f, 0f, 0.72f);
            blocker.raycastTarget = true;

            root.AddComponent<CanvasGroup>();
            SettingsPanelView view = root.AddComponent<SettingsPanelView>();

            // Title (top strip).
            Text title = CreateText("TitleText", "设置", 52, Color.white, TextAnchor.MiddleCenter);
            title.transform.SetParent(root.transform, false);
            title.rectTransform.anchorMin = new Vector2(0f, 0.88f);
            title.rectTransform.anchorMax = new Vector2(1f, 0.98f);
            title.rectTransform.offsetMin = Vector2.zero;
            title.rectTransform.offsetMax = Vector2.zero;

            // Tab bar host (page buttons are GENERATED into it at open time).
            var tabBar = new GameObject("TabBar", typeof(RectTransform));
            RectTransform tabBarRect = tabBar.GetComponent<RectTransform>();
            tabBarRect.SetParent(root.transform, false);
            tabBarRect.anchorMin = new Vector2(0.5f, 1f);
            tabBarRect.anchorMax = new Vector2(0.5f, 1f);
            tabBarRect.pivot = new Vector2(0.5f, 1f);
            tabBarRect.anchoredPosition = new Vector2(0f, -110f);
            tabBarRect.sizeDelta = new Vector2(1000f, 64f);
            HorizontalLayoutGroup tabBarLayout = tabBar.AddComponent<HorizontalLayoutGroup>();
            tabBarLayout.childAlignment = TextAnchor.UpperCenter;
            tabBarLayout.childForceExpandWidth = false;
            tabBarLayout.childForceExpandHeight = false;
            tabBarLayout.spacing = 12f;

            // Row host (the page container + rows are GENERATED into it at open time).
            var rowHost = new GameObject("RowHost", typeof(RectTransform));
            RectTransform rowHostRect = rowHost.GetComponent<RectTransform>();
            rowHostRect.SetParent(root.transform, false);
            rowHostRect.anchorMin = new Vector2(0.5f, 0.5f);
            rowHostRect.anchorMax = new Vector2(0.5f, 0.5f);
            rowHostRect.sizeDelta = new Vector2(1400f, 560f);

            // Bottom bar: the three semantic buttons.
            var bottomBar = new GameObject("BottomBar", typeof(RectTransform));
            RectTransform bottomRect = bottomBar.GetComponent<RectTransform>();
            bottomRect.SetParent(root.transform, false);
            bottomRect.anchorMin = new Vector2(0.5f, 0f);
            bottomRect.anchorMax = new Vector2(0.5f, 0f);
            bottomRect.pivot = new Vector2(0.5f, 0f);
            bottomRect.anchoredPosition = new Vector2(0f, 48f);
            bottomRect.sizeDelta = new Vector2(900f, 72f);
            HorizontalLayoutGroup bottomLayout = bottomBar.AddComponent<HorizontalLayoutGroup>();
            bottomLayout.childAlignment = TextAnchor.MiddleCenter;
            bottomLayout.childForceExpandWidth = false;
            bottomLayout.childForceExpandHeight = false;
            bottomLayout.spacing = 24f;

            Button applyButton = CreateBottomButton(bottomRect.transform, "ApplyButton", "应用并关闭", font, new Color(0.25f, 0.45f, 0.3f));
            Button revertButton = CreateBottomButton(bottomRect.transform, "RevertButton", "撤销更改", font, new Color(0.45f, 0.38f, 0.2f));
            Button resetButton = CreateBottomButton(bottomRect.transform, "ResetButton", "恢复默认", font, new Color(0.45f, 0.25f, 0.25f));

            // Wire the view's serialized references before saving.
            var serialized = new SerializedObject(view);
            serialized.FindProperty("_titleText").objectReferenceValue = title;
            serialized.FindProperty("_tabBar").objectReferenceValue = tabBarRect;
            serialized.FindProperty("_rowHost").objectReferenceValue = rowHostRect;
            serialized.FindProperty("_applyButton").objectReferenceValue = applyButton;
            serialized.FindProperty("_revertButton").objectReferenceValue = revertButton;
            serialized.FindProperty("_resetButton").objectReferenceValue = resetButton;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            UnityEngine.Object.DestroyImmediate(root);
        }

        private static Button CreateBottomButton(Transform parent, string name, string caption, Font font, Color color)
        {
            var buttonGo = new GameObject(name, typeof(RectTransform));
            buttonGo.GetComponent<RectTransform>().SetParent(parent, false);
            buttonGo.GetComponent<RectTransform>().sizeDelta = new Vector2(260f, 72f);
            Image image = buttonGo.AddComponent<Image>();
            image.color = color;
            Button button = buttonGo.AddComponent<Button>();

            Text label = CreateText("Text", caption, 34, Color.white, TextAnchor.MiddleCenter);
            label.transform.SetParent(buttonGo.transform, false);
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = Vector2.zero;
            label.rectTransform.offsetMax = Vector2.zero;
            return button;
        }

        private static Text CreateText(string name, string content, int fontSize, Color color, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            Text text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = fontSize;
            text.color = color;
            text.alignment = alignment;
            text.raycastTarget = false;
            text.text = content;
            return text;
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
