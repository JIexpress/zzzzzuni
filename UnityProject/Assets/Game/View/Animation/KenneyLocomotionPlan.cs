using System;
using Game.Domain.AI.Utility;

namespace Game.View.Animation
{
    /// <summary>
    /// TASK-A-ANIM-01: the pure, engine-free plan of the Kenney locomotion
    /// animator controller and its drive chain — every constant the Setup
    /// script bakes into the <c>.controller</c> asset, plus the two pure
    /// mapping functions the runtime driver applies every frame.
    ///
    /// Design anchor (the user requirement "非播片"): the animation state is
    /// a FUNCTION of the utility brain's state, not a looping clip. The
    /// function is composed of two hops, both pinned by tests:
    ///   brain decision (NpcUtilityAgent) → actual plane displacement (the
    ///   Domain executor moves <see cref="NpcUtilityAgent.Position"/>) →
    ///   <see cref="SpeedParam"/> (the driver measures displacement per agent
    ///   clock second) → state machine transitions (Speed &gt; 0.1 ⇒ Run,
    ///   Speed &lt; 0.1 ⇒ Idle). Nothing anywhere plays a clip "for" an
    ///   action; the body simply moves because the brain said so, and the
    ///   animation follows the measured motion.
    ///
    /// TASK-A-ANIM-02 adds the vertical hop: <see cref="JumpHeightAt"/> /
    /// <see cref="AirborneAt"/> are the same kind of pure function for the
    /// Y axis — the body is only in the air because the arc says so, and
    /// <see cref="GroundedParam"/> is that truth, never a hard-coded true.
    ///
    /// Why the state machine is gated on measured speed rather than on the
    /// brain action name directly: the Rest decision includes the WALK back
    /// to the rest point — a name-gated bool would keep the state machine in
    /// Idle while the body slides across the ground, which is exactly the
    /// "animation decoupled from motion" playback-look the requirement
    /// forbids. Speed-gating keeps the state honest with the body, and the
    /// Rest decision still lands in Idle deterministically through
    /// <see cref="BrainActionToSpeedParam"/> (the resting dead-zone).
    ///
    /// Pure by contract: no UnityEngine types beyond math, no state — the
    /// same inputs always produce the same outputs (EditMode tests pin this).
    /// </summary>
    public static class KenneyLocomotionPlan
    {
        // ---- animator parameters (exactly the brief's set) ------------------

        /// <summary>Float parameter: measured plane displacement speed in m/s.</summary>
        public const string SpeedParam = "Speed";

        /// <summary>Bool parameter: grounded flag — the arc's truth (<see cref="AirborneAt"/>), not a constant.</summary>
        public const string GroundedParam = "Grounded";

        /// <summary>Trigger parameter: jump request (Idle→Jump / Run→Jump; see <c>NpcAnimatorDriver.RequestJump</c>).</summary>
        public const string JumpParam = "Jump";

        // ---- states ----------------------------------------------------------

        /// <summary>Idle clip state (resting / standing).</summary>
        public const string IdleState = "Idle";

        /// <summary>Run clip state (any locomotion: patrol / follow / pickup walk).</summary>
        public const string RunState = "Run";

        /// <summary>Jump clip state (one-shot; exits on <see cref="JumpExitTime"/> + grounded).</summary>
        public const string JumpState = "Jump";

        /// <summary>Default state of the state machine.</summary>
        public const string DefaultState = IdleState;

        // ---- thresholds and blends ------------------------------------------

        /// <summary>Speed above this (m/s) transitions Idle→Run; below it transitions Run→Idle (the brief's 口径 Speed>0.1→Run).</summary>
        public const float LocomotionSpeedThreshold = 0.1f;

        /// <summary>
        /// Playback calibration: the Run clip plays at (measured m/s) ÷ this
        /// constant, so an NPC moving at <see cref="RunReferenceSpeed"/> m/s
        /// sees the clip at its authored rate. The Kenney clips are in-place
        /// loops with no authored stride data, so this is a calibration
        /// constant (pinned in tests), not a measured physical stride.
        /// </summary>
        public const float RunReferenceSpeed = 2f;

        /// <summary>
        /// Resting dead-zone: a Rest-like brain action at or below this
        /// measured speed (m/s) clamps the parameter to an exact 0 so the
        /// Run→Idle transition fires deterministically instead of hovering on
        /// residual float drift.
        /// </summary>
        public const float LocomotionEpsilon = 0.05f;

        /// <summary>Idle→Run transition blend duration (s).</summary>
        public const float IdleToRunBlend = 0.1f;

        /// <summary>Run→Idle transition blend duration (s).</summary>
        public const float RunToIdleBlend = 0.15f;

        /// <summary>Run→Jump transition blend duration (s).</summary>
        public const float RunToJumpBlend = 0.05f;

        /// <summary>Jump→Idle exit time (normalized clip position).</summary>
        public const float JumpExitTime = 0.9f;

        /// <summary>Jump→Idle transition blend duration (s).</summary>
        public const float JumpToIdleBlend = 0.1f;

        /// <summary>Idle→Jump transition blend duration (s).</summary>
        public const float IdleToJumpBlend = 0.05f;

        /// <summary>Jump→Run transition blend duration (s).</summary>
        public const float JumpToRunBlend = 0.1f;

        // ---- jump arc (TASK-A-ANIM-02 — pure, engine-free) -------------------

        /// <summary>Peak height of the jump arc above the take-off height (m).</summary>
        public const float JumpPeakHeight = 0.75f;

        /// <summary>
        /// Downward acceleration of the jump arc (m/s²). Earth gravity, kept
        /// as a written-down constant so the arc is reproducible in tests
        /// rather than read from the engine's physics settings.
        /// </summary>
        public const float JumpGravity = 9.81f;

        /// <summary>
        /// Take-off vertical speed (m/s), derived as v0 = √(2·g·h) — the arc
        /// peaks at exactly <see cref="JumpPeakHeight"/>, never hand-tuned.
        /// </summary>
        public static readonly float JumpInitialSpeed = (float)Math.Sqrt(2.0 * JumpGravity * JumpPeakHeight);

        /// <summary>
        /// Total air time (s), T = 2·v0/g: take-off and landing are exactly T
        /// apart, and <see cref="JumpHeightAt"/> is zero at both ends.
        /// </summary>
        public static readonly float JumpAirTime = 2f * JumpInitialSpeed / JumpGravity;

        // ---- pure functions (the drive chain, test-pinned) -------------------

        /// <summary>
        /// Maps the brain's chosen action plus the measured displacement speed
        /// onto the <see cref="SpeedParam"/> value. The brain hop lives here:
        /// Rest/Idle brain states at or below <see cref="LocomotionEpsilon"/>
        /// clamp to an exact 0 (deterministic Idle), any measured motion above
        /// the epsilon keeps its measured value regardless of the action name
        /// (a Rest still walking to the rest point shows Run — the body really
        /// moves; locomotion actions simply pass their measured speed through).
        /// </summary>
        public static float BrainActionToSpeedParam(string brainAction, float measuredSpeed)
        {
            bool isRestLike = string.Equals(brainAction, NpcUtilityAgent.RestActionName, StringComparison.Ordinal)
                              || string.Equals(brainAction, UtilityBrain.IdleActionName, StringComparison.Ordinal);
            if (isRestLike && measuredSpeed <= LocomotionEpsilon)
            {
                return 0f;
            }

            return measuredSpeed;
        }

        /// <summary>
        /// Look-at weight for a body-relative target yaw: full weight at or
        /// below <paramref name="fullWeightYawDeg"/>, zero at or above
        /// <paramref name="zeroWeightYawDeg"/>, linear in between — an NPC
        /// does not crane its neck at targets behind it. Requires
        /// <paramref name="zeroWeightYawDeg"/> &gt; <paramref name="fullWeightYawDeg"/>; symmetric in yaw sign.
        /// </summary>
        public static float LookAtWeightFor(float yawDeg, float fullWeightYawDeg, float zeroWeightYawDeg)
        {
            if (zeroWeightYawDeg <= fullWeightYawDeg)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(zeroWeightYawDeg), "Look-at fade end must be wider than the full-weight start.");
            }

            float yaw = Math.Abs(yawDeg);
            if (yaw <= fullWeightYawDeg)
            {
                return 1f;
            }

            if (yaw >= zeroWeightYawDeg)
            {
                return 0f;
            }

            return 1f - (yaw - fullWeightYawDeg) / (zeroWeightYawDeg - fullWeightYawDeg);
        }

        /// <summary>
        /// Height above the take-off point after <paramref name="elapsedSeconds"/>
        /// of air time: h = v0·t − ½g·t², with t clamped to [0, T]. Both ends
        /// are exactly 0 and the midpoint is <see cref="JumpPeakHeight"/>; a
        /// negative, NaN or past-landing time reads as "on the ground" (0)
        /// instead of throwing — the driver calls this every frame.
        /// </summary>
        public static float JumpHeightAt(float elapsedSeconds)
        {
            if (float.IsNaN(elapsedSeconds) || elapsedSeconds <= 0f)
            {
                return 0f;
            }

            if (elapsedSeconds >= JumpAirTime)
            {
                return 0f;
            }

            return (JumpInitialSpeed * elapsedSeconds) - (0.5f * JumpGravity * elapsedSeconds * elapsedSeconds);
        }

        /// <summary>
        /// True while the arc is still airborne: elapsed ∈ [0, T). Negative
        /// or past-landing times report "grounded" (false) instead of
        /// throwing — this is the value written to <see cref="GroundedParam"/>.
        /// </summary>
        public static bool AirborneAt(float elapsedSeconds)
        {
            if (float.IsNaN(elapsedSeconds) || elapsedSeconds < 0f)
            {
                return false;
            }

            return elapsedSeconds < JumpAirTime;
        }
    }
}
