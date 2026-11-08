using Game.Domain.AI.Utility;
using Game.View.Npc;
using UnityEngine;
using ZFrame.Gameplay;

namespace Game.View.Animation
{
    /// <summary>
    /// TASK-A-ANIM-01: bridges the utility-AI brain to the humanoid Animator.
    /// This is the "brain → animation" drive component the brief asks for —
    /// it CONSUMES <see cref="NpcUtilityView"/> output (never modifies the
    /// Domain logic) and the Animator receives only what the body actually
    /// does:
    ///
    /// - <see cref="KenneyLocomotionPlan.SpeedParam"/>: measured plane
    ///   displacement per agent-clock second (frame-rate independent — read
    ///   from <see cref="NpcUtilityAgent.Position"/>/Clock, not from transform
    ///   jitter), run through
    ///   <see cref="KenneyLocomotionPlan.BrainActionToSpeedParam"/>.
    /// - <see cref="KenneyLocomotionPlan.GroundedParam"/>: the arc's truth —
    ///   false while the body is airborne, true the instant it lands
    ///   (TASK-A-ANIM-02; it used to be a hard-coded true).
    /// - Vertical motion: <see cref="RequestJump"/> starts a pure arc
    ///   (<see cref="KenneyLocomotionPlan.JumpHeightAt"/>) and every
    ///   <see cref="Step"/> writes <c>localPosition.y = baseY + height</c>;
    ///   landing snaps back to the captured base exactly. The plane motion
    ///   still belongs to <see cref="NpcUtilityView"/> (it writes XZ only and
    ///   preserves Y), so this driver owns Y and nothing else.
    /// - Smooth turning: yaw steps toward the displacement direction capped at
    ///   <see cref="_turnSpeedDegPerSec"/> — angular-speed driven, never an
    ///   instant flip.
    /// - Look-at IK: <see cref="OnAnimatorIK"/> turns the head toward the
    ///   look target with a yaw-gated, adjustable weight (the procedural
    ///   layer on top of the clips — the part plain playback cannot do).
    ///
    /// The controller's state machine owns all transitions (Speed thresholds,
    /// Jump trigger), so "current animator state" is a function of brain
    /// state through measured motion — the falsifiable PlayMode chain.
    ///
    /// <see cref="ManualDrive"/> lets tests freeze the drive (params stay as
    /// last written) while the Animator keeps evaluating — used by the
    /// look-at test to pose the NPC deterministically.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NpcAnimatorDriver : MonoBehaviour
    {
        [SerializeField] private NpcUtilityView _npcView;
        [SerializeField] private Animator _animator;
        [SerializeField] private Transform _lookTarget;

        [Tooltip("Master look-at weight (0 disables IK; tests flip this to falsify).")]
        [SerializeField] private float _lookAtWeight = 1f;

        [Tooltip("Look point = target position + up * this (aim at chest/head height).")]
        [SerializeField] private float _lookHeightOffset = 0.6f;

        [Tooltip("Body-relative yaw with full look weight (deg).")]
        [SerializeField] private float _lookAtMaxYawDeg = 55f;

        [Tooltip("Body-relative yaw where look weight reaches zero (deg).")]
        [SerializeField] private float _lookAtFadeYawDeg = 85f;

        [Tooltip("SetLookAtWeight clamp (0.5 = ±90°).")]
        [SerializeField] private float _lookAtClamp = 0.35f;

        [Tooltip("Max yaw turn rate while locomoting (deg/s) — smooth, not instant.")]
        [SerializeField] private float _turnSpeedDegPerSec = 360f;

        [Tooltip("Extra yaw the FBX's visual forward is off from +Z (Kenney: 0, per ModelStageLayout).")]
        [SerializeField] private float _modelYawOffsetDeg = 0f;

        private Vec2 _lastSimPosition;
        private float _lastSimClock;
        private bool _hasSimSample;
        private Vec2 _lastMoveDelta;

        // Jump arc state (TASK-A-ANIM-02). The base Y is captured lazily on
        // the first drive step / first jump request — NOT in Awake, because
        // EditMode tooling and DestroyImmediate paths may skip Awake, and the
        // settled feet-on-ground offset must be read after the scene is posed.
        private float _baseY;
        private bool _hasBaseY;
        private float _jumpElapsed;
        private bool _isAirborne;

        /// <summary>When true, Update() does nothing — tests drive <see cref="Step"/> or freeze the NPC.</summary>
        public bool ManualDrive { get; set; }

        /// <summary>Master look-at weight knob (serialized default 1; tests falsify with 0).</summary>
        public float LookAtWeight
        {
            get { return _lookAtWeight; }
            set { _lookAtWeight = value; }
        }

        /// <summary>The IK look target (nearest player / point of interest).</summary>
        public Transform LookTarget
        {
            get { return _lookTarget; }
            set { _lookTarget = value; }
        }

        /// <summary>Speed measured on the last drive step (m/s, agent-clock domain).</summary>
        public float LastMeasuredSpeed { get; private set; }

        /// <summary>Speed parameter value written on the last drive step.</summary>
        public float LastWrittenSpeed { get; private set; }

        /// <summary>Brain action consumed on the last drive step.</summary>
        public string LastBrainAction { get; private set; } = UtilityBrain.IdleActionName;

        /// <summary>Diagnostics: body-relative target yaw seen by the last OnAnimatorIK (deg).</summary>
        public float LastLookYawDeg { get; private set; }

        /// <summary>Diagnostics: look weight applied by the last OnAnimatorIK (0 = callback not firing / gated out).</summary>
        public float LastLookWeight { get; private set; }

        /// <summary>Diagnostics: times OnAnimatorIK has fired (0 = callback never invoked).</summary>
        public int LookIkCallbackCount { get; private set; }

        /// <summary>Diagnostics: jump requests accepted since spawn (never decremented).</summary>
        public int JumpRequestCount { get; private set; }

        /// <summary>Diagnostics: jump requests rejected because the body was airborne (no double jump).</summary>
        public int IgnoredJumpCount { get; private set; }

        /// <summary>Diagnostics: arc height written on the last drive step (m, 0 = on the ground).</summary>
        public float LastJumpHeight { get; private set; }

        private void Awake()
        {
            if (_animator == null)
            {
                _animator = GetComponent<Animator>();
            }
        }

        private void Update()
        {
            if (ManualDrive)
            {
                return;
            }

            Step(Time.deltaTime);
        }

        /// <summary>
        /// One deterministic drive step: measure the sim-domain displacement,
        /// write the animator parameters and turn the body. Called by
        /// <see cref="Update"/> in normal play; tests may call it directly
        /// (it never advances the animator itself).
        /// </summary>
        public void Step(float deltaTime)
        {
            if (_npcView == null || _animator == null)
            {
                return;
            }

            _npcView.EnsureAgent();
            NpcUtilityAgent agent = _npcView.Agent;
            string brainAction = agent.CurrentAction;
            float measured = MeasureSimSpeed(agent, out Vec2 moveDelta);
            _lastMoveDelta = moveDelta;
            float speedParam = KenneyLocomotionPlan.BrainActionToSpeedParam(brainAction, measured);

            LastBrainAction = brainAction;
            LastMeasuredSpeed = measured;
            LastWrittenSpeed = speedParam;

            _animator.SetFloat(KenneyLocomotionPlan.SpeedParam, speedParam);
            AdvanceJump(deltaTime);
            TurnToward(deltaTime);
        }

        /// <summary>
        /// Requests a jump. Returns true when the take-off started, false when
        /// the request was rejected — the body is already airborne (no double
        /// jump, counted in <see cref="IgnoredJumpCount"/>) or there is no
        /// animator. Callers that ignore the return value keep working.
        /// </summary>
        public bool RequestJump()
        {
            if (_animator == null)
            {
                return false;
            }

            if (_isAirborne)
            {
                IgnoredJumpCount++;
                return false;
            }

            EnsureBaseY();
            JumpRequestCount++;
            _jumpElapsed = 0f;
            _isAirborne = true;
            _animator.SetTrigger(KenneyLocomotionPlan.JumpParam);
            return true;
        }

        /// <summary>
        /// The procedural layer: head look-at toward <see cref="_lookTarget"/>,
        /// yaw-gated through <see cref="KenneyLocomotionPlan.LookAtWeightFor"/>
        /// so a target behind the body gets no weight. Fires only because the
        /// generated controller's base layer has IK pass enabled.
        /// </summary>
        private void OnAnimatorIK(int layerIndex)
        {
            LookIkCallbackCount++;
            if (_animator == null || _lookTarget == null)
            {
                LastLookWeight = 0f;
                return;
            }

            Vector3 lookPoint = _lookTarget.position + Vector3.up * _lookHeightOffset;
            Transform head = _animator.GetBoneTransform(HumanBodyBones.Head);
            Vector3 origin = head != null ? head.position : transform.position;
            Vector3 flat = lookPoint - origin;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-6f)
            {
                LastLookYawDeg = 0f;
                LastLookWeight = 0f;
                return;
            }

            float yawDeg = Vector3.Angle(transform.forward, flat.normalized);
            float weight = KenneyLocomotionPlan.LookAtWeightFor(yawDeg, _lookAtMaxYawDeg, _lookAtFadeYawDeg)
                           * _lookAtWeight;
            LastLookYawDeg = yawDeg;
            LastLookWeight = weight;
            if (weight <= 0f)
            {
                _animator.SetLookAtWeight(0f);
                return;
            }

            _animator.SetLookAtPosition(lookPoint);
            // bodyWeight 0: the body stays owned by the locomotion turning;
            // only the HEAD is allowed to look (that is what the tests assert).
            _animator.SetLookAtWeight(weight, 0f, 1f, 0f, _lookAtClamp);
        }

        // ---- internals -------------------------------------------------------

        /// <summary>
        /// One step of the vertical arc: advance the elapsed air time, apply
        /// <see cref="KenneyLocomotionPlan.JumpHeightAt"/> on top of the
        /// captured base Y, and write the true grounded flag. Landing snaps
        /// the offset to exactly 0 (no float residue) in the same step the arc
        /// ends, so <c>Grounded</c> and the transform agree on every frame.
        /// </summary>
        private void AdvanceJump(float deltaTime)
        {
            EnsureBaseY();
            float height = 0f;
            if (_isAirborne)
            {
                _jumpElapsed += Mathf.Max(0f, deltaTime);
                if (KenneyLocomotionPlan.AirborneAt(_jumpElapsed))
                {
                    height = KenneyLocomotionPlan.JumpHeightAt(_jumpElapsed);
                }
                else
                {
                    _isAirborne = false; // landed: height stays exactly 0 this step
                }
            }

            LastJumpHeight = height;
            Vector3 localPosition = transform.localPosition;
            localPosition.y = _baseY + height;
            transform.localPosition = localPosition;
            _animator.SetBool(KenneyLocomotionPlan.GroundedParam, !_isAirborne);
        }

        /// <summary>
        /// Captures the rest-pose local Y the arc is measured from (the model
        /// child's feet-on-ground offset). Lazy on purpose: it must be read
        /// once the scene is posed, and Awake is not guaranteed (EditMode).
        /// </summary>
        private void EnsureBaseY()
        {
            if (_hasBaseY)
            {
                return;
            }

            _baseY = transform.localPosition.y;
            _hasBaseY = true;
        }

        /// <summary>
        /// Displacement speed in the AGENT's clock (sim seconds), immune to
        /// real-frame jitter and to test harnesses that step the agent by
        /// other deltas than the render frame. Also reports the raw plane
        /// delta this step (the turn direction source).
        /// </summary>
        private float MeasureSimSpeed(NpcUtilityAgent agent, out Vec2 moveDelta)
        {
            Vec2 position = agent.Position;
            float clock = agent.Clock;
            float measured = 0f;
            moveDelta = Vec2.Zero;
            if (_hasSimSample)
            {
                float deltaSeconds = clock - _lastSimClock;
                moveDelta = position - _lastSimPosition;
                if (deltaSeconds > 1e-5f)
                {
                    measured = Vec2.Distance(position, _lastSimPosition) / deltaSeconds;
                }
            }

            _lastSimPosition = position;
            _lastSimClock = clock;
            _hasSimSample = true;
            return measured;
        }

        /// <summary>
        /// Yaw toward the last displacement direction at a capped angular
        /// speed — the "转向平滑" requirement (no instant flip).
        /// </summary>
        private void TurnToward(float deltaTime)
        {
            if (_lastMoveDelta.SqrMagnitude < 1e-8f)
            {
                return; // no meaningful displacement this step: keep facing
            }

            float targetYaw = Mathf.Atan2(_lastMoveDelta.X, _lastMoveDelta.Y) * Mathf.Rad2Deg
                              + _modelYawOffsetDeg;
            float current = transform.eulerAngles.y;
            float turn = Mathf.DeltaAngle(current, targetYaw);
            float maxStep = Mathf.Max(0f, _turnSpeedDegPerSec) * Mathf.Max(0f, deltaTime);
            turn = Mathf.Clamp(turn, -maxStep, maxStep);
            transform.rotation = Quaternion.Euler(0f, current + turn, 0f);
        }
    }
}
