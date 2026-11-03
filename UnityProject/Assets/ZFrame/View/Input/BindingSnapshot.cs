namespace ZFrame.View
{
    /// <summary>
    /// Plain-data view of one action binding (TASK-A-M3-03): the bridge
    /// between a live Input System map and the PURE conflict-detection logic
    /// in <see cref="BindingConflictDetector"/>. Snapshots carry the
    /// EFFECTIVE path (<c>overridePath ?? path</c>) so persisted rebinds are
    /// taken into account, and never reference UnityEngine types — the
    /// detector is unit-testable from plain tables.
    /// </summary>
    public sealed class BindingSnapshot
    {
        /// <summary>Fully qualified action key ("Player/Interact").</summary>
        public string ActionKey { get; }

        /// <summary>Bare action name ("Interact").</summary>
        public string ActionName { get; }

        /// <summary>Index of the binding inside its action's binding list.</summary>
        public int BindingIndex { get; }

        /// <summary>Effective device path (<c>overridePath ?? path</c>); empty for composite headers.</summary>
        public string EffectivePath { get; }

        /// <summary>True for composite headers ("2DVector") — device-less, never conflict-relevant.</summary>
        public bool IsComposite { get; }

        /// <summary>True for parts of a composite (e.g. the W in WASD).</summary>
        public bool IsCompositePart { get; }

        /// <summary>Creates a snapshot row (usually via <c>BindingConflictDetector.CaptureMap</c>).</summary>
        public BindingSnapshot(
            string actionKey,
            string actionName,
            int bindingIndex,
            string effectivePath,
            bool isComposite,
            bool isCompositePart)
        {
            ActionKey = actionKey;
            ActionName = actionName;
            BindingIndex = bindingIndex;
            EffectivePath = effectivePath ?? string.Empty;
            IsComposite = isComposite;
            IsCompositePart = isCompositePart;
        }
    }
}
