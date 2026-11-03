namespace ZFrame.View
{
    /// <summary>
    /// Lifecycle contract of one panel view (TASK-A-M3-01). Implemented by
    /// <see cref="UIViewBase"/> (UGUI MonoBehaviours) and by plain C# fakes in
    /// tests — <see cref="UIPresenter"/> only ever sees this interface, which
    /// is what keeps presenters unit-testable without Unity objects.
    ///
    /// Full order guaranteed by <see cref="UIManager"/>:
    /// OnInit → OnShow → (OnPause/OnResume)* → OnHide → OnDestroy.
    /// </summary>
    public interface IUIView
    {
        /// <summary>Called once per push with the caller-supplied arguments (may be null).</summary>
        void OnInit(object args);

        /// <summary>The panel became visible and interactive (top of the stack, unblocked).</summary>
        void OnShow();

        /// <summary>A blocking panel opened above this one; input and updates should idle.</summary>
        void OnPause();

        /// <summary>The blocking panels above are gone; the panel is interactive again.</summary>
        void OnResume();

        /// <summary>The panel is leaving the stack (hidden but possibly cached for reuse).</summary>
        void OnHide();

        /// <summary>The panel instance is being discarded (never called for cached views until they are destroyed).</summary>
        void OnDestroy();
    }
}
