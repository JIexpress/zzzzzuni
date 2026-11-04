using Game.Domain.AI;
using Game.Domain.Interaction;
using UnityEngine;
using ZFrame.Core;
using ZFrame.Gameplay;
using ZFrame.View;

namespace Game.View.Effects
{
    /// <summary>
    /// Event-driven wiring of scene/gameplay events to <see cref="IAudioService"/>
    /// (TASK-A-R9-14): the zero-polling thin adapter between the Domain/SceneLoader
    /// and the audio layer, next to (not inside) <see cref="GameplayFxBindings"/>,
    /// which it mirrors structurally.
    ///
    /// Contract
    /// --------
    /// * Zero polling: no Update, no per-frame scan. Work happens only inside
    ///   event handlers, i.e. exactly when an event fires.
    /// * Sources wired (five):
    ///   <see cref="SceneLoadedEvent"/> → BGM via the per-scene map
    ///   (<see cref="BgmIdForScene"/>; an unmapped scene fades the BGM out —
    ///   unknown territory stays silent rather than carrying the last scene's
    ///   music). The ACTIVE scene is mapped once at <see cref="Build"/> so the
    ///   first scene (SCN_Boot, which never goes through SceneLoader) gets its
    ///   music too. BGM id "cave" is currently the ONLY clip in the library
    ///   (single-track reality: menu and game both map to it — the crossfade
    ///   pair still swaps tracks, so the SceneLoadedEvent→PlayBgm semantics are
    ///   real and a second track is a file drop + one map row away; see the
    ///   TASK-A-R9-14 report for the trade-off).
    ///   <see cref="ItemPickedUp"/> → sfx "pickup" at the item position;
    ///   <see cref="DoorStateChanged"/> → sfx "door" per toggle (open and close
    ///   alike); <see cref="EnemyAttackRequested"/> → sfx "hit" at the TARGET
    ///   position (where the swing landed — same convention as the hit spark);
    ///   <see cref="PanelOpenedEvent"/> → sfx "ui_click" (2D — a UI click is not
    ///   a world event).
    /// * Domain stays audio-blind: this class is the ONLY place that knows both
    ///   sides (layering red line, enforced by the architecture validator's
    ///   purity scan over Assets/Game/Domain). Same-frame duplicate SFX are
    ///   bounded by AudioService's SfxLimiter (same-id same-frame + per-frame
    ///   budget), so a burst of pickups costs one sound, not twelve.
    /// * Lifecycle: <see cref="EventScope"/>-based — every subscription made in
    ///   <see cref="Build"/> is bulk-disposed by <see cref="Shutdown"/>; play
    ///   mode reaches Shutdown via OnDestroy (GameplayFxBindings precedent:
    ///   EditMode tests call it directly). Build is idempotent; without a
    ///   registered <see cref="IAudioService"/> the binding stays passive
    ///   (events are counted, nothing is requested, nothing throws).
    ///
    /// MOUNTING: self-bootstraps via <see cref="RuntimeInitializeOnLoadMethod"/>
    /// (AfterSceneLoad — AFTER AudioService's BeforeSceneLoad boot, so the
    /// production resolve finds the service) into a hidden DontDestroyOnLoad
    /// host; GameBootstrap never references this class. Tests create isolated
    /// instances directly and destroy the shared one in SetUp/TearDown.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameAudioBindings : MonoBehaviour
    {
        /// <summary>ZLog channel for the binding's one-line per-request log.</summary>
        public const string LogChannel = "GameAudio";

        /// <summary>
        /// Height the 2D gameplay plane is lifted to when mapping to world
        /// space — identical to GameplayFxBindings.DefaultSparkHeight, so a
        /// sound and its spark come from the same spot.
        /// </summary>
        public const float DefaultSfxHeight = 1f;

        private const string HostName = "~GameAudioBindings";

        private static GameAudioBindings _instance;

        private EventBus.EventScope _scope;
        private IAudioService _audio;
        private float _sfxHeight = DefaultSfxHeight;
        private bool _built;
        private bool _shutdown;

        /// <summary>Live shared instance (null before EnsureInstance / after teardown).</summary>
        public static GameAudioBindings Instance => _instance;

        /// <summary>Events received from the bus since Build (the "events keep flowing" counter).</summary>
        public int EventsReceived { get; private set; }

        /// <summary>Requests forwarded to <see cref="IAudioService"/> (audio or BGM).</summary>
        public int AudioRequests { get; private set; }

        /// <summary>Height of the 2D→3D plane mapping in metres.</summary>
        public float SfxHeight
        {
            get => _sfxHeight;
            set => _sfxHeight = value;
        }

        /// <summary>
        /// Per-scene BGM map: scene name → BGM id (null = fade the music out).
        /// Single-track reality (TASK-A-R9-14): the library has exactly one
        /// loop, so menu and game both use "cave"; the map is where a second
        /// track lands when its file arrives.
        /// </summary>
        internal static string BgmIdForScene(string sceneName)
        {
            switch (sceneName)
            {
                case "SCN_Boot": return "cave";   // main menu lives on the boot scene
                case "SCN_Game": return "cave";   // game track (same loop for now)
                default: return null;             // unknown scene: fade out
            }
        }

        /// <summary>
        /// Self-bootstrap: creates the shared host after the first scene load
        /// and wires the subscriptions. Idempotent; a host that already exists
        /// (double domain reload, manual EnsureInstance) is reused.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBoot()
        {
            GameAudioBindings bindings = EnsureInstance();
            bindings.Build();
        }

        /// <summary>
        /// Returns the shared instance, creating it when missing. A destroyed
        /// host counts as missing (Unity's null semantics), so scene teardown /
        /// test cleanup self-heals on the next call.
        /// </summary>
        public static GameAudioBindings EnsureInstance()
        {
            if (_instance != null)
            {
                return _instance;
            }

            var host = new GameObject(HostName);
            GameAudioBindings bindings = host.AddComponent<GameAudioBindings>();
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(host);
            }

            _instance = bindings;
            return bindings;
        }

        /// <summary>
        /// Subscribes every event source on the bus through one
        /// <see cref="EventScope"/>, then maps the ACTIVE scene once (first
        /// scene boot — SCN_Boot never travels through SceneLoader). Idempotent:
        /// a second call is ignored with a warning. Pass an <see cref="IAudioService"/>
        /// to redirect audio (tests); null resolves the production service from
        /// <see cref="Services"/> — with none registered the binding stays passive.
        /// </summary>
        public void Build(IAudioService audio = null)
        {
            if (_built)
            {
                ZLog.Warn(LogChannel, "Build called twice — keeping the existing subscriptions.");
                return;
            }

            if (audio == null && Services.TryGet(out IAudioService resolved))
            {
                audio = resolved;
            }

            _audio = audio;
            _shutdown = false;
            _scope = EventBus.OpenScope();
            try
            {
                EventBus.Subscribe<SceneLoadedEvent>(OnSceneLoaded);
                EventBus.Subscribe<ItemPickedUp>(OnItemPickedUp);
                EventBus.Subscribe<DoorStateChanged>(OnDoorStateChanged);
                EventBus.Subscribe<EnemyAttackRequested>(OnEnemyAttackRequested);
                EventBus.Subscribe<PanelOpenedEvent>(OnPanelOpened);
            }
            catch
            {
                // Never leak a half-open scope: dispose what was captured so far.
                _scope.Dispose();
                _scope = null;
                _audio = null;
                throw;
            }

            _built = true;

            // First-scene BGM: the active scene was loaded before this binding
            // existed, so its SceneLoadedEvent (if any) is already gone.
            string active = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            StartBgmForScene(active, "build(active scene)");
        }

        /// <summary>
        /// Unsubscribes everything <see cref="Build"/> registered (EventScope
        /// bulk dispose). Idempotent and safe on a never-built instance; a
        /// later Build re-subscribes. Play mode reaches this via OnDestroy;
        /// EditMode tests call it directly.
        ///
        /// Also clears <see cref="_instance"/> when it points here (same
        /// pattern as AudioService.Shutdown): callers may Shutdown BEFORE a
        /// deferred <see cref="Object.Destroy"/> lands (e.g. a test SetUp
        /// that calls Shutdown then Destroy, then immediately
        /// <see cref="EnsureInstance"/>) — during that window Unity's fake
        /// null cannot see the pending destruction, and without this clear
        /// EnsureInstance would hand back the doomed host whose resubscribed
        /// Build dies with the object at end of frame (the TASK-A-R9-14
        /// PlayMode door red: first test of the session only).
        /// </summary>
        public void Shutdown()
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            _scope?.Dispose();
            _scope = null;
            _audio = null;
            _built = false;

            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void OnDestroy()
        {
            Shutdown();
            if (_instance == this)
            {
                _instance = null;
            }
        }

        // ---- event handlers (the whole point: zero polling) -----------------

        private void OnSceneLoaded(SceneLoadedEvent evt)
        {
            StartBgmForScene(evt.SceneName, "sceneLoaded");
        }

        private void OnItemPickedUp(ItemPickedUp evt)
        {
            // The sound marks where the item was picked up.
            RequestSfx("pickup", ToWorld(evt.Position), "pickup:" + evt.ItemId);
        }

        private void OnDoorStateChanged(DoorStateChanged evt)
        {
            // One sound per toggle, open and close alike: the door physically
            // moved, so the world owes the player the creak either way.
            RequestSfx("door", ToWorld(evt.Position), "door:" + evt.DoorId + (evt.IsOpen ? ":open" : ":close"));
        }

        private void OnEnemyAttackRequested(EnemyAttackRequested evt)
        {
            // The hit marks where the swing LANDED: the target's position —
            // the same convention as the GameplayFxBindings attack spark.
            RequestSfx("hit", ToWorld(evt.PlayerPosition), "hit:" + evt.EnemyId);
        }

        private void OnPanelOpened(PanelOpenedEvent evt)
        {
            // 2D UI click: a panel opening is not a world event, so no position.
            RequestSfx("ui_click", null, "ui:" + (evt.PrefabKey ?? evt.ViewType?.Name ?? "?"));
        }

        // ---- request + forward ------------------------------------------------

        private void StartBgmForScene(string sceneName, string reason)
        {
            EventsReceived++;

            string bgmId = BgmIdForScene(sceneName);
            if (_audio == null)
            {
                return; // passive: counted, never requested
            }

            AudioRequests++;
            if (bgmId != null)
            {
                _audio.PlayBgm(bgmId);
                ZLog.Info(LogChannel, $"bgm '{bgmId}' for scene '{sceneName}' ({reason})");
            }
            else
            {
                _audio.StopBgm();
                ZLog.Info(LogChannel, $"bgm fade-out for unmapped scene '{sceneName}' ({reason})");
            }
        }

        private void RequestSfx(string sfxId, Vector3? worldPos, string reason)
        {
            EventsReceived++;

            if (_audio == null)
            {
                return; // passive: counted, never requested
            }

            AudioRequests++;
            _audio.PlaySfx(sfxId, worldPos);
            ZLog.Info(LogChannel, $"sfx '{reason}' at {worldPos?.ToString() ?? "2D"}");
        }

        /// <summary>
        /// The plane→world mapping used by every positioned source (one
        /// convention per adapter, identical to GameplayFxBindings/EnemyView):
        /// Vec2.X → world X, Vec2.Y → world Z, fixed height above the ground.
        /// </summary>
        private Vector3? ToWorld(Vec2 planePos)
        {
            return new Vector3(planePos.X, _sfxHeight, planePos.Y);
        }
    }
}
