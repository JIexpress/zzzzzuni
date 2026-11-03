#if UNITY_EDITOR
using System.IO;
using Game.View.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.View.Editor
{
    /// <summary>
    /// One-shot setup of the M4 vertical slice UI prefabs (TASK-A-M4-02),
    /// assembled like the framework's own UISetup generator:
    ///  1. <c>Assets/UI/Resources/ui_main_menu.prefab</c> — main menu panel
    ///     (title + 开始/继续/设置/退出) carrying <see cref="MainMenuView"/>;
    ///  2. <c>Assets/UI/Resources/ui_pause_panel.prefab</c> — pause panel
    ///     (dim blocker + 继续/设置/回主菜单) carrying <see cref="PausePanelView"/>.
    ///
    /// The boot scene half (SCN_Boot carrying GameRoot + GameBootstrap) lives
    /// in Game.Bootstrap.Editor.BootSceneSetup — the View assembly must not
    /// reference Game.Bootstrap (the dependency edge is Bootstrap → View).
    ///
    /// Idempotent: valid prefabs are left untouched. Run via the ZFrame/Setup
    /// menu or batch:
    ///   -executeMethod Game.Bootstrap.Editor.BootSceneSetup.SetupFromCommandLine
    /// (which runs this setup too), or the menu item below.
    /// Editor-only by the #if UNITY_EDITOR guard (same pattern as UISetup).
    /// </summary>
    public static class GameUiSetup
    {
        private const string UiDir = "Assets/UI/Resources";
        private const string MainMenuPrefabPath = UiDir + "/ui_main_menu.prefab";
        private const string PausePrefabPath = UiDir + "/ui_pause_panel.prefab";

        [MenuItem("ZFrame/Setup/Generate Game UI Prefabs (menu + pause)")]
        public static void Setup()
        {
            Directory.CreateDirectory(UiDir);
            EnsureMainMenuPrefab();
            EnsurePausePrefab();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        /// <summary>Ensures the main menu prefab exists (public so BootSceneSetup can chain).</summary>
        public static void EnsureMainMenuPrefab()
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(MainMenuPrefabPath);
            if (existing != null && existing.GetComponent<MainMenuView>() != null)
            {
                Debug.Log($"[GameUiSetup] valid main menu prefab already present: {MainMenuPrefabPath}");
                return;
            }

            if (existing != null)
            {
                AssetDatabase.DeleteAsset(MainMenuPrefabPath);
            }

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var root = new GameObject("UI_MainMenu", typeof(RectTransform));
            Stretch(root.GetComponent<RectTransform>());
            root.AddComponent<CanvasGroup>();
            var view = root.AddComponent<MainMenuView>();

            Text title = BuildLabel(root.transform, "Title", font, "ZFrame 垂直切片", 56,
                new Vector2(0.1f, 0.78f), new Vector2(0.9f, 0.92f));

            Button start = BuildButton(root.transform, "StartButton", font, "开始",
                new Vector2(0.5f, 0.58f), new Vector2(320f, 80f));
            Button continueGame = BuildButton(root.transform, "ContinueButton", font, "继续",
                new Vector2(0.5f, 0.46f), new Vector2(320f, 80f));
            Button settings = BuildButton(root.transform, "SettingsButton", font, "设置",
                new Vector2(0.5f, 0.34f), new Vector2(320f, 80f));
            Button quit = BuildButton(root.transform, "QuitButton", font, "退出",
                new Vector2(0.5f, 0.22f), new Vector2(320f, 80f));

            var serialized = new SerializedObject(view);
            serialized.FindProperty("_titleText").objectReferenceValue = title;
            serialized.FindProperty("_startButton").objectReferenceValue = start;
            serialized.FindProperty("_continueButton").objectReferenceValue = continueGame;
            serialized.FindProperty("_settingsButton").objectReferenceValue = settings;
            serialized.FindProperty("_quitButton").objectReferenceValue = quit;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(root, MainMenuPrefabPath);
            Object.DestroyImmediate(root);
            Debug.Log($"[GameUiSetup] created {MainMenuPrefabPath}");
        }

        /// <summary>Ensures the pause panel prefab exists (public so BootSceneSetup can chain).</summary>
        public static void EnsurePausePrefab()
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PausePrefabPath);
            if (existing != null && existing.GetComponent<PausePanelView>() != null)
            {
                Debug.Log($"[GameUiSetup] valid pause panel prefab already present: {PausePrefabPath}");
                return;
            }

            if (existing != null)
            {
                AssetDatabase.DeleteAsset(PausePrefabPath);
            }

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var root = new GameObject("UI_PausePanel", typeof(RectTransform));
            Stretch(root.GetComponent<RectTransform>());

            // Dim blocker: raycastTarget=true so clicks cannot reach the gameplay HUD below.
            var blocker = root.AddComponent<Image>();
            blocker.color = new Color(0f, 0f, 0f, 0.6f);
            blocker.raycastTarget = true;
            root.AddComponent<CanvasGroup>();

            var view = root.AddComponent<PausePanelView>();

            Text title = BuildLabel(root.transform, "Title", font, "已暂停", 56,
                new Vector2(0.1f, 0.72f), new Vector2(0.9f, 0.86f));

            Button resume = BuildButton(root.transform, "ResumeButton", font, "继续",
                new Vector2(0.5f, 0.56f), new Vector2(320f, 80f));
            Button settings = BuildButton(root.transform, "SettingsButton", font, "设置",
                new Vector2(0.5f, 0.44f), new Vector2(320f, 80f));
            Button quitToMenu = BuildButton(root.transform, "QuitToMenuButton", font, "回主菜单",
                new Vector2(0.5f, 0.32f), new Vector2(320f, 80f));

            var serialized = new SerializedObject(view);
            serialized.FindProperty("_titleText").objectReferenceValue = title;
            serialized.FindProperty("_resumeButton").objectReferenceValue = resume;
            serialized.FindProperty("_settingsButton").objectReferenceValue = settings;
            serialized.FindProperty("_quitToMenuButton").objectReferenceValue = quitToMenu;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(root, PausePrefabPath);
            Object.DestroyImmediate(root);
            Debug.Log($"[GameUiSetup] created {PausePrefabPath}");
        }

        // ---- shared widget builders ---------------------------------------------

        private static Text BuildLabel(Transform parent, string name, Font font, string caption, int size,
            Vector2 anchorMin, Vector2 anchorMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            Text text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.raycastTarget = false;
            text.text = caption;
            return text;
        }

        private static Button BuildButton(Transform parent, string name, Font font, string caption,
            Vector2 anchor, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;

            Image image = go.AddComponent<Image>();
            image.color = new Color(0.25f, 0.45f, 0.7f);

            Button button = go.AddComponent<Button>();

            var labelGo = new GameObject("Text", typeof(RectTransform));
            RectTransform labelRect = labelGo.GetComponent<RectTransform>();
            labelRect.SetParent(go.transform, false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            Text label = labelGo.AddComponent<Text>();
            label.font = font;
            label.fontSize = 36;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.raycastTarget = false;
            label.text = caption;
            return button;
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
