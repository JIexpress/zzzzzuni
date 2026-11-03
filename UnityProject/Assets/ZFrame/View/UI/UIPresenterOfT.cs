namespace ZFrame.View
{
    /// <summary>
    /// Typed convenience layer over <see cref="UIPresenter"/>: exposes the
    /// injected view with its concrete interface type. Still PURE C# —
    /// <typeparamref name="TView"/> is an <see cref="IUIView"/>, and concrete
    /// presenters for UGUI views access only view methods/properties, never
    /// UnityEngine types directly (keeps the "no UnityEngine in presenters"
    /// rule verifiable).
    /// </summary>
    public abstract class UIPresenter<TView> : UIPresenter where TView : IUIView
    {
        /// <summary>The typed view (same instance as the base <c>View</c> property).</summary>
        protected new TView View { get; }

        /// <summary>Creates the presenter for <paramref name="view"/> (constructor injection).</summary>
        protected UIPresenter(TView view)
            : base(view)
        {
            View = view;
        }
    }
}
