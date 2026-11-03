using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>
    /// Self-building UI root (TASK-A-M3-01): one DontDestroyOnLoad GameObject
    /// holding exactly one independent Canvas (+ CanvasScaler + GraphicRaycaster)
    /// per <see cref="UIConsts.UILayer"/> — eight canvases total, sorting order
    /// fixed by the layer value so render order never depends on hierarchy luck.
    ///
    /// Bootstrap: <see cref="RuntimeInitializeOnLoadMethod"/> self-creates the
    /// root after scene load (same self-hosting pattern as DebugPanelRoot);
    /// tests call <see cref="EnsureCreated"/> directly (the runtime callback is
    /// not invoked in the editor). Also guarantees an EventSystem exists.
    ///
    /// NOTE (InputSystem, M4): the EventSystem is created with
    /// StandaloneInputModule (legacy input). When the Input System package is
    /// adopted (M4) this gets a versionDefines-gated switch to
    /// InputSystemUIInputModule — the seam is the single place below.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UIRoot : MonoBehaviour
    {
        /// <summary>GameObject name of the root (and of the auto EventSystem, prefixed).</summary>
        public const string RootName = "[UIRoot]";
        /// <summary>GameObject name of the auto-created EventSystem.</summary>
        public const string EventSystemName = "[UIRoot.EventSystem]";

        private const string LayerHostPrefix = "[Layer:";
        private const string LayerHostSuffix = "]";

        private static UIRoot _instance;

        private readonly Dictionary<UIConsts.UILayer, Canvas> _layerCanvases =
            new Dictionary<UIConsts.UILayer, Canvas>();

        /// <summary>The live root, or null when not created yet (see <see cref="EnsureCreated"/>).</summary>
        public static UIRoot Instance => _instance;

        /// <summary>Canvas of <paramref name="layer"/>; null when the root has not been created.</summary>
        public Canvas GetLayerCanvas(UIConsts.UILayer layer)
        {
            return _layerCanvases.TryGetValue(layer, out Canvas canvas) ? canvas : null;
        }

        /// <summary>Number of layer canvases built (should always be <see cref="UIConsts.LayerCount"/>).</summary>
        public int LayerCanvasCount => _layerCanvases.Count;

        /// <summary>Runtime bootstrap (AfterSceneLoad so gameplay scenes find the root ready).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void BootstrapOnLoad()
        {
            EnsureCreated();
        }

        /// <summary>
        /// Returns the live root or creates it: re-adopts an orphaned root
        /// from the scene graph when the static reference was lost (domain
        /// reload disabled), otherwise builds the root + 8 layer canvases +
        /// EventSystem check. Safe to call repeatedly.
        /// </summary>
        public static UIRoot EnsureCreated()
        {
            if (_instance != null)
            {
                return _instance;
            }

            foreach (UIRoot orphan in Object.FindObjectsOfType<UIRoot>(true))
            {
                _instance = orphan; // re-adopt (e.g. statics lost after Assembly Reload with enter-playmode-settings)
                orphan.BuildLayersIfMissing();
                EnsureEventSystem(orphan.transform);
                return _instance;
            }

            var host = new GameObject(RootName);
            _instance = host.AddComponent<UIRoot>();
            _instance.BuildLayersIfMissing();
            EnsureEventSystem(host.transform);

            if (Application.isPlaying)
            {
                Object.DontDestroyOnLoad(host);
            }

            return _instance;
        }

        /// <summary>
        /// Forgets the static instance (tests destroy the GameObject themselves). Does NOT touch scene objects.
        /// TASK-B-R10-01: 测试专用，收为 internal（见 View/AssemblyInfo.cs）。
        /// </summary>
        internal static void ResetForTests()
        {
            _instance = null;
        }

        private void BuildLayersIfMissing()
        {
            if (GetComponent<UIAnimRunner>() == null)
            {
                gameObject.AddComponent<UIAnimRunner>(); // hosts PanelAnim.Fade coroutines
            }

            // TASK-A-AUDIT-03 (A-4): the dictionary is a non-serialised cache, so it is
            // ALWAYS empty after a domain reload while the [Layer:*] children survive as
            // scene objects. Trusting the cache alone rebuilt a second full set of eight
            // canvases: the old set kept rendering and raycasting, UIManager only knew the
            // new one ("two UIs stacked, the old one still clickable"). Re-index from the
            // real hierarchy first; only layers that are genuinely absent get built.
            AdoptExistingLayers();

            if (_layerCanvases.Count == UIConsts.LayerCount)
            {
                return;
            }

            foreach (UIConsts.UILayer layer in UIConsts.AllLayers)
            {
                if (_layerCanvases.ContainsKey(layer))
                {
                    continue; // surviving child from before the reload
                }

                var layerHost = new GameObject($"[Layer:{layer}]");
                layerHost.transform.SetParent(transform, false);

                var canvas = layerHost.AddComponent<Canvas>();
                canvas.overrideSorting = false;
                canvas.sortingOrder = UIConsts.SortOrder(layer);

                var scaler = layerHost.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;

                layerHost.AddComponent<GraphicRaycaster>();

                // TASK-A-AND-03: reparenting an Overlay canvas under a non-root
                // transform makes Unity silently convert it to WorldSpace — the
                // layer hosts above are children of the UIRoot host, so on a
                // real device every menu panel ended up on a 100x100 WorldSpace
                // canvas at the origin: no pixels, no hit-testing, "gray boot
                // screen" while the boot chain completed. Re-assert the mode
                // AFTER parenting; a child canvas with an explicit Overlay mode
                // renders on screen normally.
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;

                _layerCanvases[layer] = canvas;
            }
        }

        /// <summary>
        /// Rebuilds <see cref="_layerCanvases"/> from the real children of this transform.
        /// Only direct children named <c>[Layer:&lt;UILayer&gt;]</c> carrying a Canvas are
        /// adopted, so a stale/foreign <c>Canvas</c> elsewhere in the hierarchy can never
        /// be mistaken for a layer. Duplicate hosts (the pre-fix corrupt state) resolve to
        /// the first one; nothing is created or destroyed here.
        /// </summary>
        private void AdoptExistingLayers()
        {
            _layerCanvases.Clear();

            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (!TryParseLayerHostName(child.name, out UIConsts.UILayer layer))
                {
                    continue;
                }

                var canvas = child.GetComponent<Canvas>();
                if (canvas == null || _layerCanvases.ContainsKey(layer))
                {
                    continue;
                }

                _layerCanvases.Add(layer, canvas);
            }
        }

        private static bool TryParseLayerHostName(string name, out UIConsts.UILayer layer)
        {
            layer = default;
            if (string.IsNullOrEmpty(name)
                || !name.StartsWith(LayerHostPrefix, System.StringComparison.Ordinal)
                || !name.EndsWith(LayerHostSuffix, System.StringComparison.Ordinal))
            {
                return false;
            }

            string token = name.Substring(
                LayerHostPrefix.Length,
                name.Length - LayerHostPrefix.Length - LayerHostSuffix.Length);

            return System.Enum.TryParse(token, out layer)
                   && System.Enum.IsDefined(typeof(UIConsts.UILayer), layer);
        }

        private static void EnsureEventSystem(Transform root)
        {
            EventSystem existing = Object.FindObjectOfType<EventSystem>();
            if (existing != null)
            {
                if (IsPersistentFor(existing, root))
                {
                    return;
                }

                // TASK-A-AUDIT-03 (B-3): a scene-authored EventSystem that is not part of
                // the root is NOT persistent — loading the next scene in single mode
                // destroys it, while EnsureCreated early-returns on the surviving
                // _instance and never rebuilds one. That is exactly the A-AND-04 dead-UGUI
                // bug (fcc59ad) coming back. Do not adopt it: switch it off (the object is
                // scene data, so it is not destroyed) and build ours under the root.
                // Leaving it enabled would also make two EventSystems fight over
                // EventSystem.current and dispatch every pointer twice.
                existing.enabled = false;
                ZLog.Warn("UI", "UIRoot: ignored a non-persistent EventSystem '" + existing.name
                                + "' (not under the UIRoot); it would die with its scene and take UGUI dispatch with it.");
            }

            var host = new GameObject(EventSystemName);
            // TASK-A-AND-04 (device finding): the EventSystem used to be a ROOT
            // object while only the UIRoot host got DontDestroyOnLoad. Loading
            // SCN_Game in single mode therefore destroyed the EventSystem with
            // SCN_Boot, and from that point on EVERY UGUI element in the game
            // scene was dead (no pointer dispatch at all — on-screen stick and
            // the pause panel's buttons included). The EventSystem is part of
            // the UI root, so it lives under it and inherits its persistence.
            if (root != null)
            {
                host.transform.SetParent(root, false);
            }

            host.AddComponent<EventSystem>();
            // Input System (M4) will swap this for InputSystemUIInputModule — see class remarks.
            host.AddComponent<StandaloneInputModule>();
        }

        /// <summary>
        /// Persistence, as defined by this framework, means "lives under the UIRoot host"
        /// (A-AND-04): the root is the DontDestroyOnLoad object, so a child inherits it.
        /// </summary>
        private static bool IsPersistentFor(EventSystem eventSystem, Transform root)
        {
            return root != null && eventSystem.transform.IsChildOf(root);
        }
    }
}
