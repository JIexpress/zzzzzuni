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

        /// <summary>Bool parameter: grounded flag (always true in this slice — no vertical physics).</summary>
        public const string GroundedParam = "Grounded";

        /// <summary>Trigger parameter: jump request (Run→Jump; not fired by the demo loop, see <c>NpcAnimatorDriver.RequestJump</c>).</summary>
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
    }
}
