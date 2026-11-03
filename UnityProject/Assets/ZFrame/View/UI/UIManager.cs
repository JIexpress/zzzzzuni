using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using ZFrame.Core;
using ZFrame.Data;

namespace ZFrame.View
{
    /// <summary>
    /// Panel stack manager (TASK-A-M3-01). Owns the panel stack over the
    /// <see cref="UIRoot"/> layer canvases: Push/Pop with full lifecycle
    /// (OnInit → OnShow → (OnPause/OnResume)* → OnHide → OnDestroy),
    /// blockMask-driven pausing of the panels below, per-layer parenting,
    /// prefab loading through <see cref="IResourceService"/>, PanelCachePolicy
    /// instance caching, MVP assembly through the <see cref="UIBinding"/>
    /// registry (no reflection scanning) and PanelOpened/PanelClosed events on
    /// the <see cref="EventBus"/>.
    ///
    /// Stack semantics (one global stack, evaluated top-down):
    /// - the topmost panel is always active;
    /// - a panel is paused while any panel ABOVE it on the stack has
    ///   <see cref="PanelConfig.BlockMask"/> — so a blocking Popup over the HUD
    ///   pauses the HUD, while a non-blocking Toast leaves it running (layers
    ///   therefore never fight each other; the stack order decides);
    /// - pushing the same view type (or prefab key) twice is rejected — also
    ///   while the first push is still opening (prefab load / fade-in);
    /// - a push that fails half-way releases everything it acquired.
    ///
    /// Pure (non-Component) IUIView implementations are supported for tests —
    /// they skip prefab loading entirely (a PanelConfig without prefab key).
    /// </summary>
    public sealed class UIManager
    {
        private sealed class PanelRecord
        {
            public IUIView View;
            public UIPresenter Presenter;
            public PanelConfig Config;
            public GameObject Instance;
            public bool IsPaused;
        }

        private readonly IResourceService _resources;
        private readonly UIRoot _root;
        private readonly List<PanelRecord> _stack = new List<PanelRecord>();
        private readonly Dictionary<string, IUIView> _instanceCache =
            new Dictionary<string, IUIView>(StringComparer.Ordinal);

        /// <summary>
        /// Panels whose <see cref="Push{TView}"/> is between the duplicate check and the
        /// stack insert (prefab load + fade-in). They are not on <see cref="_stack"/> yet,
        /// so the duplicate check alone would let the same panel be opened twice.
        /// </summary>
        private readonly HashSet<Type> _pushingViews = new HashSet<Type>();

        /// <summary>Prefab keys of the pushes in flight (same purpose as <see cref="_pushingViews"/>).</summary>
        private readonly HashSet<string> _pushingKeys = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The most recently created manager (convenience accessor for runtime code; tests usually hold their own reference).</summary>
        public static UIManager Instance { get; private set; }

        /// <summary>Creates the manager over <paramref name="root"/>. Loads prefabs through <paramref name="resources"/> and installs default MVP bindings once.</summary>
        public UIManager(IResourceService resources, UIRoot root)
        {
            _resources = resources ?? throw new ArgumentNullException(nameof(resources));
            _root = root ?? throw new ArgumentNullException(nameof(root));
            UIBinding.EnsureDefaults();
            Instance = this;
        }

        /// <summary>Number of panels currently on the stack (all layers).</summary>
        public int StackCount => _stack.Count;

        /// <summary>True while a panel with this view type sits anywhere on the stack.</summary>
        public bool IsOnStack<TView>() where TView : IUIView
        {
            return _stack.Exists(r => r.View.GetType() == typeof(TView));
        }

        /// <summary>Returns the view instance of the topmost matching panel on the stack (tests / runtime queries).</summary>
        public bool TryGetView<TView>(out TView view) where TView : class, IUIView
        {
            PanelRecord record = _stack.Find(r => r.View is TView);
            view = record?.View as TView;
            return view != null;
        }

        /// <summary>Currently cached instances (cachePolicy Once) keyed by prefab key — diagnostics/tests.</summary>
        public int CachedPanelCount => _instanceCache.Count;

        /// <summary>
        /// Pushes a panel of <typeparamref name="TView"/> described by
        /// <paramref name="config"/>. Rejected (returns false) when the same
        /// view type — or the same non-empty prefab key — is already on the
        /// stack OR is still being opened (prefab load / fade-in in flight).
        /// Completes synchronously when the resource layer answers
        /// synchronously (Resources provider), so tests can await it without a
        /// frame pump.
        ///
        /// Failure semantics of an INTERRUPTED fade-in (TASK-R10-04, audit A-8):
        /// the fade always delivers exactly one outcome (Cancelled — the UIRoot
        /// went away mid-fade; Failed — the panel instance was destroyed
        /// mid-fade), so this await can never hang. On Cancelled/Failed the push
        /// is ROLLED BACK and throws <see cref="InvalidOperationException"/>: the
        /// presenter is detached, the instance destroyed and the acquired prefab
        /// reference released (the H-01 D3 unwind in the finally below), the
        /// panel never enters the stack, no <see cref="PanelOpenedEvent"/> fires
        /// and the same panel is immediately pushable again.
        /// </summary>
        public async Task<bool> Push<TView>(PanelConfig config, object args = null)
            where TView : class, IUIView, new()
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            Type viewType = typeof(TView);
            string prefabKey = config.PrefabKey;
            bool keyDuplicated = !string.IsNullOrEmpty(prefabKey) &&
                (_pushingKeys.Contains(prefabKey) ||
                 _stack.Exists(r => string.Equals(r.Config.PrefabKey, prefabKey, StringComparison.Ordinal)));
            if (_pushingViews.Contains(viewType) ||
                _stack.Exists(r => r.View.GetType() == viewType) ||
                keyDuplicated)
            {
                Debug.LogWarning($"[UIManager] Push rejected — panel '{viewType.Name}' (key '{prefabKey}') is already open or still opening.");
                return false;
            }

            // In-flight mark BEFORE the first await: a panel reaches _stack only after its
            // fade-in finished, so "正在淡入" would otherwise be an open door for a second Push
            // of the very same panel (duplicate instance, duplicate presenter, double ref count).
            _pushingViews.Add(viewType);
            if (!string.IsNullOrEmpty(prefabKey))
            {
                _pushingKeys.Add(prefabKey);
            }

            GameObject instance = null;
            UIPresenter presenter = null;
            bool acquiredPrefab = false;
            bool createdInstance = false;
            bool committed = false;
            try
            {
                // Acquire the view: cached instance (policy Once) → prefab instance → pure C# instance.
                TView view;
                if (!string.IsNullOrEmpty(prefabKey) && config.CachePolicy == PanelCachePolicy.Once &&
                    _instanceCache.TryGetValue(prefabKey, out IUIView cached))
                {
                    view = (TView)cached;
                    instance = (view as Component)?.gameObject;
                    if (instance != null)
                    {
                        instance.SetActive(true);
                    }
                }
                else if (typeof(Component).IsAssignableFrom(viewType))
                {
                    if (string.IsNullOrEmpty(prefabKey))
                    {
                        throw new InvalidOperationException(
                            $"[UIManager] Push({viewType.Name}): a Component view needs a non-empty PanelConfig.PrefabKey.");
                    }

                    GameObject prefab = await _resources.LoadAsync<GameObject>(prefabKey);
                    acquiredPrefab = true; // the reference must be given back if the push fails
                    instance = UnityEngine.Object.Instantiate(prefab);
                    createdInstance = true;
                    view = instance.GetComponent<TView>();
                    if (view == null)
                    {
                        throw new InvalidOperationException(
                            $"[UIManager] Push({viewType.Name}): prefab '{prefabKey}' has no {viewType.Name} component on its root.");
                    }
                }
                else
                {
                    view = Activator.CreateInstance<TView>(); // pure C# view (tests / headless panels) — no prefab involved
                }

                if (instance != null)
                {
                    instance.transform.SetParent(_root.GetLayerCanvas(config.Layer).transform, false);
                }

                // Lifecycle: init → presenter attach (subscribes within its scope) → show → stack/pause pass.
                view.OnInit(args);

                if (UIBinding.TryGetFactory(viewType, out Func<IUIView, UIPresenter> factory))
                {
                    presenter = factory(view);
                    presenter.Attach(args);
                }

                view.OnShow();

                if (config.AnimIn == PanelAnim.Fade)
                {
                    UIAnimOutcome fade = await FadeAsync(instance, to: 1f);
                    if (fade != UIAnimOutcome.Completed)
                    {
                        // TASK-R10-04 (audit A-8): the fade-in was interrupted — the host
                        // went away (Cancelled) or this panel instance was destroyed
                        // mid-fade (Failed). A half-faded panel can never finish opening,
                        // so leave WITHOUT committing: the finally below runs the H-01 D3
                        // unwind (presenter detach, instance destroy, prefab release) and
                        // the failure surfaces to the caller. Stack, in-flight marks and
                        // PanelOpenedEvent are all left exactly as if the push never
                        // happened.
                        throw new InvalidOperationException(
                            $"[UIManager] Push({viewType.Name}): the fade-in was interrupted ({fade}); the push is rolled back and the panel is NOT on the stack.");
                    }
                }

                _stack.Add(new PanelRecord
                {
                    View = view,
                    Presenter = presenter,
                    Config = config,
                    Instance = instance
                });
                committed = true;
            }
            finally
            {
                _pushingViews.Remove(viewType);
                if (!string.IsNullOrEmpty(prefabKey))
                {
                    _pushingKeys.Remove(prefabKey);
                }

                if (!committed)
                {
                    // Half-opened panel (prefab root without the view component, throwing
                    // OnInit/OnShow, cancelled fade): unwind everything THIS call acquired —
                    // the acquired prefab reference is otherwise never released, and the
                    // orphan instance would stay on the layer canvas forever.
                    try
                    {
                        presenter?.Detach();
                    }
                    catch (Exception)
                    {
                        // Cleanup must never mask the failure that caused it.
                    }

                    if (createdInstance)
                    {
                        DestroyObject(instance);
                    }

                    if (acquiredPrefab)
                    {
                        _resources.Release(prefabKey);
                    }
                }
            }

            ApplyPauseStates();

            EventBus.Fire(new PanelOpenedEvent { PrefabKey = config.PrefabKey, ViewType = viewType, Layer = config.Layer });
            return true;
        }

        /// <summary>
        /// Pops the topmost panel, or — when <paramref name="config"/> is given —
        /// the topmost panel matching that config (reference, else prefab key).
        /// False when there is nothing to pop.
        /// </summary>
        public bool Pop(PanelConfig config = null)
        {
            int index = -1;
            if (config == null)
            {
                if (_stack.Count == 0)
                {
                    return false;
                }

                index = _stack.Count - 1;
            }
            else
            {
                for (int i = _stack.Count - 1; i >= 0; i--)
                {
                    PanelRecord record = _stack[i];
                    bool matches = ReferenceEquals(record.Config, config) ||
                        (!string.IsNullOrEmpty(config.PrefabKey) &&
                         string.Equals(record.Config.PrefabKey, config.PrefabKey, StringComparison.Ordinal));
                    if (matches)
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0)
                {
                    return false;
                }
            }

            PopAt(index);
            return true;
        }

        /// <summary>Pops every panel currently on <paramref name="layer"/> (top-down) and resumes whatever remains unblocked. Returns how many panels were popped.</summary>
        public int PopAll(UIConsts.UILayer layer)
        {
            int popped = 0;
            while (true)
            {
                int index = _stack.FindLastIndex(r => r.Config.Layer == layer);
                if (index < 0)
                {
                    return popped;
                }

                PopAt(index);
                popped++;
            }
        }

        /// <summary>Drops cached panel instances (cachePolicy Once) that are not on the stack. Returns how many were destroyed. Intended for scene switches.</summary>
        public int EvictPanelCache()
        {
            List<string> keys = new List<string>(_instanceCache.Keys);
            int evicted = 0;
            foreach (string key in keys)
            {
                IUIView view = _instanceCache[key];
                if (_stack.Exists(r => ReferenceEquals(r.View, view)))
                {
                    continue;
                }

                _instanceCache.Remove(key);
                evicted++;
                view.OnDestroy();
                Component component = view as Component;
                if (component != null)
                {
                    DestroyObject(component.gameObject);
                }

                if (!string.IsNullOrEmpty(key))
                {
                    _resources.Release(key);
                }
            }

            return evicted;
        }

        /// <summary>
        /// Forgets all static references (tests destroy scene objects themselves).
        /// TASK-B-R10-01: 测试专用，收为 internal（见 View/AssemblyInfo.cs）。
        /// </summary>
        internal static void ResetForTests()
        {
            Instance = null;
        }

        // ---- internals ------------------------------------------------------

        private void PopAt(int index)
        {
            PanelRecord record = _stack[index];
            _stack.RemoveAt(index);

            record.View.OnHide();

            record.Presenter?.Detach(); // scope disposal unsubscribes everything OnAttach subscribed

            if (record.Config.CachePolicy == PanelCachePolicy.Once)
            {
                // Simple fade-out (fire-and-forget) on the surviving instance; runtime only — see UIAnimRunner.
                if (record.Config.AnimOut == PanelAnim.Fade && record.Instance != null)
                {
                    RunFadeOut(record.Instance);
                }

                _instanceCache[record.Config.PrefabKey] = record.View;
                if (record.Instance != null)
                {
                    record.Instance.SetActive(false);
                }
            }
            else
            {
                record.View.OnDestroy();
                if (record.Instance != null)
                {
                    DestroyObject(record.Instance);
                }

                if (!string.IsNullOrEmpty(record.Config.PrefabKey))
                {
                    _resources.Release(record.Config.PrefabKey);
                }
            }

            ApplyPauseStates();
            EventBus.Fire(new PanelClosedEvent
            {
                PrefabKey = record.Config.PrefabKey,
                ViewType = record.View.GetType(),
                Layer = record.Config.Layer
            });
        }

        /// <summary>
        /// Re-evaluates the paused state of every panel, top-down: a panel is
        /// paused while any panel above it has BlockMask. Emits OnPause/OnResume
        /// only on transitions and mirrors the state onto the instance's
        /// GraphicRaycaster (paused panels stop consuming raycasts).
        /// </summary>
        private void ApplyPauseStates()
        {
            bool blocked = false;
            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                PanelRecord record = _stack[i];
                if (blocked && !record.IsPaused)
                {
                    record.IsPaused = true;
                    record.View.OnPause();
                }
                else if (!blocked && record.IsPaused)
                {
                    record.IsPaused = false;
                    record.View.OnResume();
                }

                GraphicRaycaster raycaster = record.Instance != null
                    ? record.Instance.GetComponent<GraphicRaycaster>()
                    : null;
                if (raycaster != null)
                {
                    raycaster.enabled = !record.IsPaused;
                }

                if (record.Config.BlockMask)
                {
                    blocked = true;
                }
            }
        }

        /// <summary>
        /// Runs the panel fade and reports its outcome (TASK-R10-04): Completed on a
        /// normal end, Cancelled when the fade host went away, Failed when the fade
        /// target was destroyed mid-fade. The outcome is ALWAYS delivered (the
        /// single-completion guarantee lives in <see cref="UIAnimRunner"/>) — this
        /// await can no longer hang the push.
        /// </summary>
        private async Task<UIAnimOutcome> FadeAsync(GameObject instance, float to)
        {
            if (instance == null)
            {
                return UIAnimOutcome.Completed;
            }

            CanvasGroup group = instance.GetComponent<CanvasGroup>() ?? instance.AddComponent<CanvasGroup>();
            UIAnimRunner runner = _root.GetComponent<UIAnimRunner>();
            if (runner == null)
            {
                group.alpha = to;
                return UIAnimOutcome.Completed;
            }

            return to >= 1f ? await runner.FadeIn(group) : await runner.FadeOut(group);
        }

        private void RunFadeOut(GameObject instance)
        {
            CanvasGroup group = instance.GetComponent<CanvasGroup>() ?? instance.AddComponent<CanvasGroup>();
            UIAnimRunner runner = _root.GetComponent<UIAnimRunner>();
            if (runner != null)
            {
                // Fire-and-forget: the lifecycle above is already complete; this only
                // smooths the visual exit of cached instances (edit mode = instant).
                // The outcome is deliberately unobserved (nobody awaits it) — a fade
                // that dies mid-way still settles through UIAnimRunner's session, so
                // no pending task is ever left behind (TASK-R10-04).
                _ = runner.FadeOut(group);
            }
        }

        /// <summary>
        /// Destroy helper: runtime defers (Object.Destroy); edit mode destroys
        /// immediately so EditMode tests can assert on destruction right after Pop.
        /// Null-safe: the TASK-R10-04 rollback can run after the instance was
        /// already destroyed externally (Failed fade) — destroying a dead reference
        /// is a no-op either way, the guard just makes that explicit.
        /// </summary>
        private static void DestroyObject(GameObject instance)
        {
            if (instance == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(instance);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }
    }
}
