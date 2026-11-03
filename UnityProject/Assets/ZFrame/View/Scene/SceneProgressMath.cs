using UnityEngine;

namespace ZFrame.View
{
    /// <summary>
    /// Pure progress-shaping math for the scene loader (design: TASK-A-M2-01
    /// point 3). Everything here is a deterministic function of its inputs —
    /// no Unity object state — so the loading-bar behaviour is unit-testable
    /// in EditMode.
    ///
    /// Contract: raw <see cref="AsyncOperation.progress"/> enters at 0 and is
    /// held at <see cref="ActivationGate"/> (0.9) by
    /// <c>allowSceneActivation = false</c>; the displayed value is linear
    /// below the gate and smoothstep-eased from the gate to 1 afterwards, and
    /// must never regress.
    /// </summary>
    public static class SceneProgressMath
    {
        /// <summary>Raw progress value at which the async operation is gated by <c>allowSceneActivation</c>.</summary>
        public const float ActivationGate = 0.9f;

        /// <summary>
        /// Maps raw async progress [0,1] onto displayed progress [0,1]:
        /// linear over [0, <see cref="ActivationGate"/>] (reaching 0.9 exactly
        /// at the gate), then smoothstep-eased over (gate, 1]. Monotonic
        /// non-decreasing; f(0)=0, f(1)=1.
        /// </summary>
        public static float Remap(float rawProgress)
        {
            rawProgress = Mathf.Clamp01(rawProgress);
            if (rawProgress <= ActivationGate)
            {
                return rawProgress / ActivationGate * ActivationGate;
            }

            float t = (rawProgress - ActivationGate) / (1f - ActivationGate);
            return ActivationGate + (1f - ActivationGate) * SmoothStep(t);
        }

        /// <summary>
        /// Timed tail easing used while the operation is held at the gate:
        /// given normalized elapsed time [0,1] of the tail window, returns the
        /// eased fraction [0,1] (smoothstep) the bar travels from 0.9 to 1.
        /// </summary>
        public static float EaseTail(float normalizedTime)
        {
            return SmoothStep(Mathf.Clamp01(normalizedTime));
        }

        /// <summary>Monotonic clamp: keeps a displayed progress value from ever moving backwards.</summary>
        public static float NonRegressing(float previousDisplayed, float candidate)
        {
            return candidate > previousDisplayed ? candidate : previousDisplayed;
        }

        /// <summary>Hermite smoothstep on an already-clamped t.</summary>
        private static float SmoothStep(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }
    }
}
