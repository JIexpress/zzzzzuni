namespace ZFrame.View
{
    /// <summary>
    /// The absolute governor-desired settings state at one ladder level
    /// (TASK-A-R8-01). Measures ACCUMULATE down the ladder: the state at any
    /// level is the union of every rung at or below it, expressed against the
    /// user baseline captured when the governor was enabled. Walking back up
    /// therefore unwinds the highest active measure first (LIFO restore).
    /// The host applies this struct field-by-field, skipping unchanged ones.
    /// </summary>
    public readonly struct GovernorState
    {
        /// <summary>Desired renderScale of the active URP asset (baseline value at ladder level 0).</summary>
        public readonly float RenderScale;

        /// <summary>Quality tiers below the user tier (0 = user tier itself; never above it — downgrade-only).</summary>
        public readonly int ShadowStepsDown;

        /// <summary>Desired grade volume weight (baseline value at ladder level 0).</summary>
        public readonly float PostFxWeight;

        /// <summary>True at the terminal TargetFps rung: everything is applied and the target is given up on.</summary>
        public readonly bool GiveUpTarget;

        public GovernorState(float renderScale, int shadowStepsDown, float postFxWeight, bool giveUpTarget)
        {
            RenderScale = renderScale;
            ShadowStepsDown = shadowStepsDown;
            PostFxWeight = postFxWeight;
            GiveUpTarget = giveUpTarget;
        }

        public override string ToString()
        {
            return $"GovernorState(scale={RenderScale:0.00}, shadowSteps={ShadowStepsDown}, postFx={PostFxWeight:0.00}, giveUp={GiveUpTarget})";
        }
    }
}
