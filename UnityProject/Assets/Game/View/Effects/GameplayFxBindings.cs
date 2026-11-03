using System;
using Game.Domain.AI;
using Game.Domain.Enemy;
using Game.Domain.Interaction;
using UnityEngine;
using ZFrame.Core;
using ZFrame.Gameplay;

namespace Game.View.Effects
{
    /// <summary>
    /// Event-driven wiring of gameplay events to the hit spark (TASK-A-R9-06):
    /// the zero-polling thin adapter between the Domain and the particle layer.
    /// The Domain keeps raising plain events — it never learns that particles
    /// exist (layering red line, enforced by the architecture validator's
    /// purity scan over <c>Assets/Game/Domain</c>); this component subscribes
    /// on the <see cref="EventBus"/>, maps each event's plane position to a
    /// world position and asks the injected <see cref="IGameplaySparkEmitter"/>
    /// for one spark burst.
    ///
    /// Contract
    /// --------
    /// * Zero polling: no Update, no per-frame scan. Work happens only inside
    ///   event handlers, i.e. exactly when a gameplay event fires.
    /// * Sources wired (five, all carrying positions after the R9-06/07
    ///   minimal event extensions):
    ///   <see cref="EnemyAttackRequested"/> → spark at the TARGET position
    ///   (<c>PlayerPosition</c> — where the swing landed); <see cref="ItemPickedUp"/>
    ///   → spark at the item position; <see cref="DoorStateChanged"/> → one
    ///   small burst per toggle at the door position (open and close alike);
    ///   <see cref="EnemyStateChanged"/> → one death burst when the enemy
    ///   enters <see cref="EnemyStateId.Dead"/> (other transitions are routine
    ///   AI chatter and spark nothing — a patrol loop would strobe the screen);
    ///   <see cref="DialogAdvanced"/> → one small burst when the conversation
    ///   completes (<c>IsFinished</c> only; per-line sparks would puncture the
    ///   narrative rhythm — see the TASK-A-R9-07 report for the product call).
    ///   Same-frame events at (nearly) the same world spot merge into ONE
    ///   spark — 100 pickups in one frame cost one burst, and the R9-05 pool
    ///   cap stays the second, harder bound.
    /// * Filter semantics: events that are dropped by a source's spark rule
    ///   (non-death transitions, mid-dialog lines) are ignored before the
    ///   counters, so <see cref="EventsReceived"/> keeps its "spark-relevant
    ///   events" meaning and EventsReceived == SparksRequested + SparksMerged
    ///   stays an invariant.
    /// * Sparks are NOT undoable (TASK-A-R9-12): the command system rewinds
    ///   GAME state only. A pickup undone through the CommandManager fires
    ///   <see cref="ItemReturned"/>, which this binding deliberately does not
    ///   subscribe — the original spark stays spent, no particle travels back
    ///   in time. (A redo re-applies the pickup and re-fires
    ///   <see cref="ItemPickedUp"/>, so it sparks once more — semantically the
    ///   pickup happened again, which is a NEW visual event, not a revert.)
    /// * Switch coordination: this class holds no on/off state of its own. It
    ///   keeps receiving events and forwarding requests while
    ///   <c>ParticleRuntime.Enabled == false</c>; the emitter returns 0 and no
    ///   pixel moves. One switch, one truth.
    /// * Lifecycle: <see cref="EventScope"/>-based — every subscription made in
    ///   <see cref="Build"/> is bulk-disposed by <see cref="Shutdown"/>, and
    ///   play mode reaches Shutdown via OnDestroy (EditMode tests call it
    ///   directly; GameHud/A-M3-02 precedent: EditMode does not drive
    ///   MonoBehaviour teardown).
    ///
    /// MOUNTING: self-bootstraps via <see cref="RuntimeInitializeOnLoadMethod"/>
    /// (DebugPanelRoot/SceneLoader precedent) into a hidden DontDestroyOnLoad
    /// host; GameBootstrap never references this class. Tests create isolated
    /// instances directly and destroy the shared one in SetUp/TearDown.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameplayFxBindings : MonoBehaviour
    {
        /// <summary>ZLog channel for the binding's one-line per-spark log.</summary>
        public const string LogChannel = "GameplayFx";

        /// <summary>
        /// Default merge radius (metres): two requests in the same frame whose
        /// spark positions are closer than this are one visual event.
        /// </summary>
        public const float DefaultMergeRadius = 0.25f;

        /// <summary>
        /// Height the 2D gameplay plane is lifted to when mapping to world
        /// space (the slice's sparks float above the ground plane).
        /// </summary>
        public const float DefaultSparkHeight = 1f;

        private const string HostName = "~GameplayFxBindings";

        private static GameplayFxBindings _instance;

        private EventBus.EventScope _scope;
        private IGameplaySparkEmitter _emitter;
        private float _sparkHeight = DefaultSparkHeight;
        private bool _built;
        private bool _shutdown;

        // Same-frame merge state: what the last forwarded request looked like.
        private int _lastFrame = -1;
        private Vector3 _lastSparkPosition;
        private bool _hasLastSpark;

        /// <summary>Live shared instance (null before EnsureInstance / after teardown).</summary>
        public static GameplayFxBindings Instance => _instance;

        /// <summary>Events received from the bus since Build (the "events keep flowing" counter).</summary>
        public int EventsReceived { get; private set; }

        /// <summary>Requests forwarded to the emitter (after same-frame merging).</summary>
        public int SparksRequested { get; private set; }

        /// <summary>Same-frame same-spot requests absorbed instead of forwarded.</summary>
        public int SparksMerged { get; private set; }

        /// <summary>
        /// Merge radius for same-frame requests (metres). Tuning knob, not a
        /// test seam: production may widen it for faster-moving fights.
        /// </summary>
        public float MergeRadius { get; set; } = DefaultMergeRadius;

        /// <summary>Height of the 2D→3D plane mapping in metres.</summary>
        public float SparkHeight
        {
            get => _sparkHeight;
            set => _sparkHeight = value;
        }

        /// <summary>
        /// Frame id source for the merge key. Internal test seam (B-R10-01
        /// discipline: test-only seams are never public): EditMode cannot
        /// advance <see cref="Time.frameCount"/>, so tests inject a manual
        /// counter to prove frame rollover re-arms the merge. Production never
        /// assigns this.
        /// </summary>
        internal Func<int> FrameSource { get; set; } = () => Time.frameCount;

        /// <summary>
        /// Self-bootstrap: creates the shared host after the first scene load
        /// and wires the subscriptions. Idempotent; a host that already exists
        /// (double domain reload, manual EnsureInstance) is reused.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBoot()
        {
            GameplayFxBindings bindings = EnsureInstance();
            bindings.Build();
        }

        /// <summary>
        /// Returns the shared instance, creating it when missing. A destroyed
        /// host counts as missing (Unity's null semantics), so scene teardown /
        /// test cleanup self-heals on the next call.
        /// </summary>
        public static GameplayFxBindings EnsureInstance()
        {
            if (_instance != null)
            {
                return _instance;
            }

            var host = new GameObject(HostName);
            GameplayFxBindings bindings = host.AddComponent<GameplayFxBindings>();
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(host);
            }

            _instance = bindings;
            return bindings;
        }

        /// <summary>
        /// Subscribes every event source on the bus through one
        /// <see cref="EventScope"/>. Idempotent: a second call is ignored with
        /// a warning (exactly one live subscription per source). Pass an
        /// emitter to redirect sparks (tests); null uses the production
        /// <see cref="ParticleRuntimeSparkEmitter"/>.
        /// </summary>
        public void Build(IGameplaySparkEmitter emitter = null)
        {
            if (_built)
            {
                ZLog.Warn(LogChannel, "Build called twice — keeping the existing subscriptions.");
                return;
            }

            _emitter = emitter ?? new ParticleRuntimeSparkEmitter();
            _shutdown = false;
            _scope = EventBus.OpenScope();
            try
            {
                EventBus.Subscribe<EnemyAttackRequested>(OnEnemyAttackRequested);
                EventBus.Subscribe<ItemPickedUp>(OnItemPickedUp);
                EventBus.Subscribe<DoorStateChanged>(OnDoorStateChanged);
                EventBus.Subscribe<EnemyStateChanged>(OnEnemyStateChanged);
                EventBus.Subscribe<DialogAdvanced>(OnDialogAdvanced);
            }
            catch
            {
                // Never leak a half-open scope: dispose what was captured so far.
                _scope.Dispose();
                _scope = null;
                _emitter = null;
                throw;
            }

            _built = true;
        }

        /// <summary>
        /// Unsubscribes everything <see cref="Build"/> registered (EventScope
        /// bulk dispose). Idempotent and safe on a never-built instance; a
        /// later Build re-subscribes. Play mode reaches this via OnDestroy;
        /// EditMode tests call it directly.
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
            _emitter = null;
            _built = false;
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

        private void OnEnemyAttackRequested(EnemyAttackRequested evt)
        {
            // The spark marks where the swing LANDED: the target's position.
            RequestSpark(ToWorld(evt.PlayerPosition), "attack:" + evt.EnemyId);
        }

        private void OnItemPickedUp(ItemPickedUp evt)
        {
            // The spark marks where the item was picked up.
            RequestSpark(ToWorld(evt.Position), "pickup:" + evt.ItemId);
        }

        private void OnDoorStateChanged(DoorStateChanged evt)
        {
            // One small burst per toggle, open and close alike: the door
            // physically moved, so the world owes the player a puff there.
            RequestSpark(ToWorld(evt.Position), "door:" + evt.DoorId + (evt.IsOpen ? ":open" : ":close"));
        }

        private void OnEnemyStateChanged(EnemyStateChanged evt)
        {
            // Death burst only. Every other transition is routine AI chatter —
            // a patrol loop idling/patrolling/chasing in a corner would strobe
            // the screen several times per second. Death is the one transition
            // that changes the world for the player, and it deserves the one
            // burst (position = where the enemy fell, evt.Position).
            if (evt.Current != EnemyStateId.Dead)
            {
                return;
            }

            RequestSpark(ToWorld(evt.Position), "death:" + evt.EnemyId);
        }

        private void OnDialogAdvanced(DialogAdvanced evt)
        {
            // PRODUCT CALL (TASK-A-R9-07, authorized by the brief): per-line
            // sparks would puncture the narrative rhythm, so lines spark
            // nothing. The conversation COMPLETING is a world-visible moment —
            // the same "state reached its end" family as a door settling or an
            // enemy falling — and earns exactly one small burst at the speaker.
            // Reverting this source is deleting this handler + one Subscribe
            // line; nothing else depends on it.
            if (!evt.IsFinished)
            {
                return;
            }

            RequestSpark(ToWorld(evt.Position), "dialog:" + evt.DialogId);
        }

        // ---- merge + forward -------------------------------------------------

        private void RequestSpark(Vector3 worldPos, string reason)
        {
            EventsReceived++;

            int frame = FrameSource();
            if (_hasLastSpark && frame == _lastFrame
                && (worldPos - _lastSparkPosition).sqrMagnitude <= MergeRadius * MergeRadius)
            {
                SparksMerged++;
                return;
            }

            _hasLastSpark = true;
            _lastFrame = frame;
            _lastSparkPosition = worldPos;
            SparksRequested++;

            int emitted = _emitter != null ? _emitter.Spawn(worldPos) : 0;
            ZLog.Info(LogChannel, $"spark '{reason}' at {worldPos} frame={frame} emitted={emitted}");
        }

        /// <summary>
        /// The plane→world mapping used by every source (one convention per
        /// adapter, identical to EnemyView): Vec2.X → world X, Vec2.Y → world
        /// Z, fixed height above the ground.
        /// </summary>
        private Vector3 ToWorld(Vec2 planePos)
        {
            return new Vector3(planePos.X, _sparkHeight, planePos.Y);
        }
    }
}
