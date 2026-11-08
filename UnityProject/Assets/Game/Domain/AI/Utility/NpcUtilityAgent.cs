using System;
using System.Collections.Generic;
using ZFrame.Gameplay;
using ZFrame.Gameplay.AI;

namespace Game.Domain.AI.Utility
{
    /// <summary>
    /// Reference utility-driven NPC (TASK-A-TECH-01): owns its self-state
    /// (fatigue, inventory, plane position) and a <see cref="UtilityBrain"/>
    /// preloaded with the demo candidates — Rest, Pickup, Follow, Patrol and
    /// (TASK-A-TECH-04) Repair — each scored by pure curves over the pumped
    /// <see cref="NPCWorldState"/>. The NPC therefore RANKS its own options
    /// every tick and switches behaviour when its state or the world changes;
    /// nothing here is a fixed branch order like the behaviour-tree/FSM
    /// neighbours in <c>Game.Domain.AI</c> / <c>Game.Domain.Enemy</c> (this
    /// slice is an add-alongside third brain, it does not touch them).
    ///
    /// TASK-A-TECH-04 additions:
    /// - <b>Personality weights</b>: an optional <see cref="NPCPersonalityDef"/>
    ///   (row of CFG_NpcPersonality) is injected into the
    ///   <see cref="UtilityBrain"/>; trait-tagged actions (Rest←fatigue,
    ///   Follow←social, Pickup←curiosity, Patrol←bravery) get their desire
    ///   multiplied by that NPC's weight. No def = all 1.0 = A-TECH-01
    ///   behaviour. The per-action <see cref="Personality"/> preset of
    ///   A-TECH-01 stays and composes (both default to neutral 1.0).
    /// - <b>GOAP forward planning</b>: the Repair candidate is a NEUTRAL duty
    ///   (no trait). When the world pumps a broken device plus a toolbox, its
    ///   execution consults <c>GoapPlanner.Plan</c> — single-step repair is
    ///   infeasible without the tool, so the planner returns the two-step
    ///   chain [TakeTool → RepairDevice] and the agent walks it: toolbox
    ///   first (grants the tool atom), then the device (fires
    ///   <see cref="DeviceRepaired"/>; the owner pumps the global
    ///   broken=false fact back). Integration path: UtilityBrain picks the
    ///   INTENT, GoapPlanner plans the HOW, the chain executor walks it.
    ///
    /// Closed loop, all engine-free (the View maps <see cref="Position"/> onto
    /// a transform exactly like EnemyView does for the FSM): fatigue rises
    /// while acting, resting at the rest point lowers it, walking to a drop
    /// and arriving raises inventory (which decays pickup desire), and the
    /// player distance is pumped in by the owner every tick.
    ///
    /// Selection contract (asserted in tests): a rested NPC with a mid-distance
    /// player patrols; the same NPC pumped to high fatigue rests; recovered,
    /// it patrols again — the same brain, changed only by world state.
    /// Five NPCs fed the identical state stream but different CFG personality
    /// rows produce pairwise-different action sequences (divergence proof).
    /// </summary>
    public sealed class NpcUtilityAgent
    {
        /// <summary>Demo candidate names (assertion anchors).</summary>
        public const string RestActionName = "Rest";

        /// <summary>Demo candidate names (assertion anchors).</summary>
        public const string PickupActionName = "Pickup";

        /// <summary>Demo candidate names (assertion anchors).</summary>
        public const string FollowActionName = "Follow";

        /// <summary>Demo candidate names (assertion anchors).</summary>
        public const string PatrolActionName = "Patrol";

        /// <summary>Demo candidate names (assertion anchors) — the GOAP-driven duty.</summary>
        public const string RepairActionName = "Repair";

        /// <summary>Planner chain step names (assertion anchors for the two-step evidence).</summary>
        public const string GoapTakeToolActionName = "TakeTool";

        /// <summary>Planner chain step names (assertion anchors for the two-step evidence).</summary>
        public const string GoapRepairActionName = "RepairDevice";

        /// <summary>
        /// Behaviour thresholds of the demo candidate set. Properties (not
        /// fields) so callers can override single values via object initialiser.
        /// </summary>
        public sealed class Tuning
        {
            /// <summary>Fatigue gained per second while acting (resting recovers faster).</summary>
            public float FatiguePerSecond { get; set; } = 0.02f;

            /// <summary>Fatigue shed per second while at the rest point.</summary>
            public float RestRecoveryPerSecond { get; set; } = 0.35f;

            /// <summary>Move speed for Rest/Pickup/Patrol walking.</summary>
            public float WalkSpeed { get; set; } = 1.5f;

            /// <summary>Move speed while following the player.</summary>
            public float FollowSpeed { get; set; } = 2.2f;

            /// <summary>Waypoint arrival distance (patrol advances its index).</summary>
            public float ArriveEpsilon { get; set; } = 0.05f;

            /// <summary>Distance at which the NPC counts as AT the rest point.</summary>
            public float RestPointRadius { get; set; } = 0.4f;

            /// <summary>Distance at which a targeted drop is collected.</summary>
            public float PickupRadius { get; set; } = 0.5f;

            /// <summary>Rest desire is zero below this fatigue.</summary>
            public float RestDesireFloor { get; set; } = 0.35f;

            /// <summary>Rest desire saturates (1) at this fatigue.</summary>
            public float RestDesireCeiling { get; set; } = 0.9f;

            /// <summary>Follow desire peak (reached at/beyond FollowDesireOuter).</summary>
            public float FollowDesirePeak { get; set; } = 0.7f;

            /// <summary>Follow desire ramp start (already-close stays 0).</summary>
            public float FollowDesireInner { get; set; } = 1.5f;

            /// <summary>Follow desire ramp end.</summary>
            public float FollowDesireOuter { get; set; } = 8f;

            /// <summary>Patrol baseline desire at zero fatigue.</summary>
            public float PatrolBaseDesire { get; set; } = 0.3f;

            /// <summary>Pickup desire with an empty inventory.</summary>
            public float PickupBaseDesire { get; set; } = 0.85f;

            /// <summary>Inventory count at which pickup desire is fully decayed.</summary>
            public float PickupSaturation { get; set; } = 5f;

            /// <summary>How far pickup desire falls once saturated (0..1).</summary>
            public float PickupSatedPenalty { get; set; } = 0.9f;

            /// <summary>Clamps the stored fatigue each tick.</summary>
            public float FatigueCeiling { get; set; } = 1f;

            /// <summary>
            /// Repair desire while a broken device AND a toolbox are perceived
            /// (TASK-A-TECH-04). A neutral duty — no personality weight — so
            /// personality only shifts preferences around it.
            /// </summary>
            public float RepairDesireBase { get; set; } = 0.9f;

            /// <summary>Distance at which a chain target (toolbox/device) counts as reached.</summary>
            public float RepairArriveRadius { get; set; } = 0.5f;
        }

        /// <summary>
        /// Per-action weight multipliers (personality). All 1 = balanced.
        /// Weights scale the pure desire curves — preferences, not branches.
        /// </summary>
        public sealed class Personality
        {
            /// <summary>Rest desire multiplier.</summary>
            public float Rest { get; set; } = 1f;

            /// <summary>Pickup desire multiplier.</summary>
            public float Pickup { get; set; } = 1f;

            /// <summary>Follow desire multiplier.</summary>
            public float Follow { get; set; } = 1f;

            /// <summary>Patrol desire multiplier.</summary>
            public float Patrol { get; set; } = 1f;

            /// <summary>A copy of this personality (safe to mutate).</summary>
            public Personality Clone()
            {
                return new Personality { Rest = Rest, Pickup = Pickup, Follow = Follow, Patrol = Patrol };
            }
        }

        private readonly string _id;
        private readonly Tuning _tuning;
        private readonly Personality _personality;
        private readonly NPCPersonalityDef _personalityDef;
        private readonly UtilityBrain _brain;
        private readonly NPCWorldState _state = new NPCWorldState();
        private readonly List<Vec2> _waypoints;
        private readonly GoapAction _goapTakeTool;
        private readonly GoapAction _goapRepairDevice;
        private Vec2 _position;
        private Vec2 _restPoint;
        private Vec2 _playerPosition;
        private Vec2? _dropTarget;
        private Vec2? _toolboxPosition;
        private Vec2? _deviceTarget;
        private bool _deviceBroken;
        private bool _hasTool;
        private GoapPlan _chain;
        private int _chainIndex;
        private float _fatigue;
        private int _inventory;
        private int _waypointIndex;
        private float _clock;
        private float _lastDeltaSeconds;
        private string _currentAction = UtilityBrain.IdleActionName;

        /// <summary>
        /// Creates the NPC with the five demo candidates registered in the
        /// order Rest, Pickup, Follow, Patrol, Repair — that registration
        /// order is the brain's tie-break priority. Throws on nonsense setup
        /// (fail fast).
        /// </summary>
        /// <param name="id">Stable identity, e.g. "npc-a".</param>
        /// <param name="startPosition">Initial plane position.</param>
        /// <param name="restPoint">Where resting happens.</param>
        /// <param name="waypoints">Patrol loop; empty means Patrol just idles in place.</param>
        /// <param name="tuning">Optional thresholds (null = defaults).</param>
        /// <param name="personality">Optional per-action desire weights (null = balanced; A-TECH-01 preset).</param>
        /// <param name="personalityDef">
        /// Optional CFG_NpcPersonality row (TASK-A-TECH-04) — trait weights
        /// injected into the brain; null = neutral all-1.0 (old behaviour).
        /// </param>
        public NpcUtilityAgent(
            string id,
            Vec2 startPosition,
            Vec2 restPoint,
            IEnumerable<Vec2> waypoints,
            Tuning tuning = null,
            Personality personality = null,
            NPCPersonalityDef personalityDef = null)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("NpcUtilityAgent: id must not be empty.", nameof(id));
            }

            _id = id;
            _position = startPosition;
            _restPoint = restPoint;
            _playerPosition = startPosition;
            _tuning = tuning ?? new Tuning();
            _personality = (personality ?? new Personality()).Clone();
            _personalityDef = personalityDef;
            _waypoints = waypoints != null ? new List<Vec2>(waypoints) : new List<Vec2>();

            // GOAP operators of the repair slice (immutable; the planner only
            // reads them). TakeTool is unconditional; RepairDevice needs the
            // tool — that precondition is what makes single-step repair
            // infeasible and forces the two-step chain.
            _goapTakeTool = new GoapAction(
                GoapTakeToolActionName,
                null,
                new[] { UtilityGoapAtoms.HasTool },
                null,
                1f);
            _goapRepairDevice = new GoapAction(
                GoapRepairActionName,
                new[] { UtilityGoapAtoms.HasTool, UtilityGoapAtoms.DeviceBroken },
                new[] { UtilityGoapAtoms.DeviceRepaired },
                new[] { UtilityGoapAtoms.DeviceBroken },
                1f);

            _brain = new UtilityBrain(personalityDef);
            _brain.Add(new UtilityAction(RestActionName, ScoreRest, ExecuteRest, 0f, UtilityTraits.Fatigue));
            _brain.Add(new UtilityAction(PickupActionName, ScorePickup, ExecutePickup, 0f, UtilityTraits.Curiosity));
            _brain.Add(new UtilityAction(FollowActionName, ScoreFollow, ExecuteFollow, 0f, UtilityTraits.Social));
            _brain.Add(new UtilityAction(PatrolActionName, ScorePatrol, ExecutePatrol, 0f, UtilityTraits.Bravery));
            _brain.Add(new UtilityAction(RepairActionName, ScoreRepair, ExecuteRepair)); // neutral duty — no trait
        }

        /// <summary>Raised when the chosen action CHANGES (View recolours on this, tests record sequences).</summary>
        public event Action<string> ActionChosen;

        /// <summary>Raised when a targeted drop is collected (View removes the object; payload = its plane position).</summary>
        public event Action<Vec2> DropCollected;

        /// <summary>Raised when the planner produced the repair chain (payload = the plan; TASK-A-TECH-04 evidence).</summary>
        public event Action<GoapPlan> ChainPlanned;

        /// <summary>Raised when this NPC finished repairing the device (owner flips the global broken fact).</summary>
        public event Action<Vec2> DeviceRepaired;

        /// <summary>Stable identity.</summary>
        public string Id => _id;

        /// <summary>Current plane position (the View maps it to XZ).</summary>
        public Vec2 Position => _position;

        /// <summary>0..1 tiredness.</summary>
        public float Fatigue => _fatigue;

        /// <summary>Collected items (drives the diminishing pickup desire).</summary>
        public int Inventory => _inventory;

        /// <summary>Action chosen on the last tick (Idle before the first tick).</summary>
        public string CurrentAction => _currentAction;

        /// <summary>The brain (rankings, thresholds, cooldown inspection for tests).</summary>
        public UtilityBrain Brain => _brain;

        /// <summary>The pumped world snapshot (tests may read it for wiring checks).</summary>
        public NPCWorldState WorldState => _state;

        /// <summary>Internal brain clock (sum of pumped deltas).</summary>
        public float Clock => _clock;

        /// <summary>Index of the waypoint currently being walked toward.</summary>
        public int WaypointIndex => _waypointIndex;

        /// <summary>The injected CFG personality row (null = neutral all-1.0 weighting).</summary>
        public NPCPersonalityDef PersonalityDef => _personalityDef;

        /// <summary>True once the chain's TakeTool step reached the toolbox (planner atom made real).</summary>
        public bool HasTool => _hasTool;

        /// <summary>The repair chain the planner produced (null before the first Repair win; TASK-A-TECH-04 evidence).</summary>
        public GoapPlan ActiveChain => _chain;

        /// <summary>Index of the chain step currently being executed.</summary>
        public int ChainIndex => _chainIndex;

        /// <summary>Pumps the player position the NPC reacts to (called each frame by the owner).</summary>
        public void SetPlayerPosition(Vec2 playerPosition)
        {
            _playerPosition = playerPosition;
        }

        /// <summary>Pumps the nearest perceived drop, or null when none is perceived.</summary>
        public void SetDropTarget(Vec2? dropPosition)
        {
            _dropTarget = dropPosition;
        }

        /// <summary>Pumps the toolbox (repair tool source) position, or null when none is perceived.</summary>
        public void SetToolboxPosition(Vec2? toolboxPosition)
        {
            _toolboxPosition = toolboxPosition;
        }

        /// <summary>Pumps the broken-device (repair target) position, or null when none is perceived.</summary>
        public void SetDeviceTarget(Vec2? devicePosition)
        {
            _deviceTarget = devicePosition;
        }

        /// <summary>
        /// Pumps the GLOBAL device state (the device is one shared world
        /// object — any NPC's repair flips it for everyone). Pumping false
        /// also aborts any running chain: a finished repair kills the intent.
        /// </summary>
        public void SetDeviceBroken(bool broken)
        {
            _deviceBroken = broken;
            if (!broken)
            {
                _chain = null;
                _chainIndex = 0;
            }
        }

        /// <summary>Overrides tiredness (clamped to 0..Tuning.FatigueCeiling) — used by the scene for start states and by tests.</summary>
        public void SetFatigue(float fatigue)
        {
            _fatigue = Math.Max(0f, Math.Min(_tuning.FatigueCeiling, fatigue));
        }

        /// <summary>Overrides the carried-item count (>= 0) — pumps the diminishing-desire state for tests.</summary>
        public void SetInventory(int count)
        {
            _inventory = Math.Max(0, count);
        }

        /// <summary>
        /// Advances the NPC by <paramref name="deltaTime"/> seconds: the clock
        /// and fatigue accumulate, the world snapshot is pumped, the brain
        /// selects and executes, and behaviour-change events fire. Non-positive
        /// dt is ignored. Returns the chosen action name.
        /// </summary>
        public string Tick(float deltaTime)
        {
            if (deltaTime <= 0f)
            {
                return _currentAction;
            }

            _clock += deltaTime;
            _lastDeltaSeconds = deltaTime;
            _fatigue = Math.Min(_tuning.FatigueCeiling, _fatigue + (_tuning.FatiguePerSecond * deltaTime));

            PumpState();
            string chosen = _brain.Tick(_state, _clock);
            if (!string.Equals(chosen, _currentAction, StringComparison.Ordinal))
            {
                _currentAction = chosen;
                ActionChosen?.Invoke(chosen);
            }

            return chosen;
        }

        private void PumpState()
        {
            _state.Set(NPCWorldState.Keys.Fatigue, _fatigue);
            _state.Set(NPCWorldState.Keys.InventoryCount, _inventory);
            _state.Set(NPCWorldState.Keys.PlayerDistance, Vec2.Distance(_position, _playerPosition));
            if (_dropTarget is Vec2 drop)
            {
                _state.Set(NPCWorldState.Keys.DropDistance, Vec2.Distance(_position, drop));
            }
            else
            {
                _state.Remove(NPCWorldState.Keys.DropDistance);
            }

            if (_toolboxPosition is Vec2 toolbox)
            {
                _state.Set(NPCWorldState.Keys.ToolboxDistance, Vec2.Distance(_position, toolbox));
            }
            else
            {
                _state.Remove(NPCWorldState.Keys.ToolboxDistance);
            }

            if (_deviceTarget is Vec2 device)
            {
                _state.Set(NPCWorldState.Keys.DeviceDistance, Vec2.Distance(_position, device));
            }
            else
            {
                _state.Remove(NPCWorldState.Keys.DeviceDistance);
            }

            _state.Set(NPCWorldState.Keys.DeviceBroken, _deviceBroken);
        }

        // ---- pure desire curves (state in — desire out, nothing mutated) ----

        private float ScoreRest(NPCWorldState state)
        {
            float fatigue = state.GetFloat(NPCWorldState.Keys.Fatigue, 0f);
            return UtilityCurves.Ramp(fatigue, _tuning.RestDesireFloor, _tuning.RestDesireCeiling) * _personality.Rest;
        }

        private float ScorePickup(NPCWorldState state)
        {
            if (!state.HasDrop())
            {
                return 0f;
            }

            int inventory = state.GetInt(NPCWorldState.Keys.InventoryCount, 0);
            float sated = UtilityCurves.Diminishing(inventory, _tuning.PickupSaturation) * _tuning.PickupSatedPenalty;
            return _tuning.PickupBaseDesire * (1f - sated) * _personality.Pickup;
        }

        private float ScoreFollow(NPCWorldState state)
        {
            float distance = state.GetFloat(NPCWorldState.Keys.PlayerDistance, 0f);
            return _tuning.FollowDesirePeak
                   * UtilityCurves.Ramp(distance, _tuning.FollowDesireInner, _tuning.FollowDesireOuter)
                   * _personality.Follow;
        }

        private float ScorePatrol(NPCWorldState state)
        {
            float fatigue = state.GetFloat(NPCWorldState.Keys.Fatigue, 0f);
            return _tuning.PatrolBaseDesire * (1f - fatigue) * _personality.Patrol;
        }

        private float ScoreRepair(NPCWorldState state)
        {
            // 修理是中立职责（无个性权重）：装置坏了且工具来源与目标都被感知才产生欲望。
            if (!state.TryGet(NPCWorldState.Keys.DeviceBroken, out bool broken) || !broken)
            {
                return 0f;
            }

            // 无工具来源或无目标感知：前瞻链必然无解 → 欲望为 0
            // （饿死防护：绝不选一个执行不出两步链的行动去堵死其他候选）。
            if (!state.Has(NPCWorldState.Keys.ToolboxDistance) || !state.Has(NPCWorldState.Keys.DeviceDistance))
            {
                return 0f;
            }

            return _tuning.RepairDesireBase;
        }

        // ---- execute delegates (the only place the NPC's world changes) ----

        private void ExecuteRest()
        {
            float dt = _lastDeltaSeconds;
            if (Vec2.Distance(_position, _restPoint) <= _tuning.RestPointRadius)
            {
                _fatigue = Math.Max(0f, _fatigue - (_tuning.RestRecoveryPerSecond * dt));
                return;
            }

            _position = Vec2.MoveTowards(_position, _restPoint, _tuning.WalkSpeed * dt);
        }

        private void ExecutePickup()
        {
            if (!(_dropTarget is Vec2 drop))
            {
                return;
            }

            float dt = _lastDeltaSeconds;
            if (Vec2.Distance(_position, drop) <= _tuning.PickupRadius)
            {
                _inventory++;
                _dropTarget = null;
                DropCollected?.Invoke(drop);
                return;
            }

            _position = Vec2.MoveTowards(_position, drop, _tuning.WalkSpeed * dt);
        }

        private void ExecuteFollow()
        {
            _position = Vec2.MoveTowards(_position, _playerPosition, _tuning.FollowSpeed * _lastDeltaSeconds);
        }

        private void ExecutePatrol()
        {
            if (_waypoints.Count == 0)
            {
                return; // nothing to walk: stand still (desire curves still rule the ranking)
            }

            Vec2 target = _waypoints[_waypointIndex];
            _position = Vec2.MoveTowards(_position, target, _tuning.WalkSpeed * _lastDeltaSeconds);
            if (Vec2.Distance(_position, target) <= _tuning.ArriveEpsilon)
            {
                _waypointIndex = (_waypointIndex + 1) % _waypoints.Count;
            }
        }

        /// <summary>
        /// GOAP-driven repair (TASK-A-TECH-04). First winning tick consults
        /// <c>GoapPlanner.Plan</c> (the forward look: without the tool the
        /// single-step repair is inapplicable, so the planner returns
        /// [TakeTool → RepairDevice]); every Repair-won tick then advances the
        /// chain one step — walking to the toolbox grants the tool atom,
        /// walking to the device fires <see cref="DeviceRepaired"/>. The
        /// global "device fixed" fact flows back through
        /// <see cref="SetDeviceBroken"/>, which kills both the desire and any
        /// running chain.
        /// </summary>
        private void ExecuteRepair()
        {
            if (!_deviceBroken)
            {
                // 世界已修复（可能由他人完成）：链作废，欲望门控随后把 Repair 归零。
                _chain = null;
                _chainIndex = 0;
                return;
            }

            if (!(_deviceTarget is Vec2 devicePosition) || !(_toolboxPosition is Vec2 toolboxPosition))
            {
                return; // 传感器丢失：本拍空转（正常门控下欲望已归零，不会走到这里）
            }

            EnsureChain();
            if (_chain == null || _chainIndex >= _chain.Steps.Count)
            {
                return; // 计划不可得（门控下不可达）：防御性空转
            }

            GoapAction step = _chain.Steps[_chainIndex];
            Vec2 target = string.Equals(step.Name, GoapTakeToolActionName, StringComparison.Ordinal)
                ? toolboxPosition
                : devicePosition;
            _position = Vec2.MoveTowards(_position, target, _tuning.WalkSpeed * _lastDeltaSeconds);
            if (Vec2.Distance(_position, target) > _tuning.RepairArriveRadius)
            {
                return; // 仍在路上
            }

            if (string.Equals(step.Name, GoapTakeToolActionName, StringComparison.Ordinal))
            {
                _hasTool = true;
                _chainIndex++;
                return;
            }

            // 修理步抵达：装置修复（本 NPC 的全局事实；Owner 经 SetDeviceBroken(false) 泵回全体验证）。
            _deviceBroken = false;
            _hasTool = false;
            _chainIndex++;
            DeviceRepaired?.Invoke(devicePosition);
        }

        private void EnsureChain()
        {
            if (_chain != null && _chainIndex < _chain.Steps.Count)
            {
                return;
            }

            var start = new GoapState();
            if (_hasTool)
            {
                start.Add(UtilityGoapAtoms.HasTool);
            }

            if (_deviceBroken)
            {
                start.Add(UtilityGoapAtoms.DeviceBroken);
            }

            var goal = new GoapState();
            goal.Add(UtilityGoapAtoms.DeviceRepaired);

            var actions = new[] { _goapTakeTool, _goapRepairDevice };
            _chain = GoapPlanner.Plan(start, goal, actions, GoapPlanner.DefaultMaxDepth);
            _chainIndex = 0;
            if (_chain != null)
            {
                ChainPlanned?.Invoke(_chain);
            }
        }
    }
}
