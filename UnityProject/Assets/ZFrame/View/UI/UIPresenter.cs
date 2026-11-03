using System;
using ZFrame.Core;

namespace ZFrame.View
{
    /// <summary>
    /// Presenter base (MVP, TASK-A-M3-01). PURE C#: no UnityEngine types —
    /// the view arrives as an <see cref="IUIView"/> through constructor
    /// injection, so presenters are unit-testable with fakes alone.
    ///
    /// Attach/Detach bracket the subscription lifetime: <see cref="Attach"/>
    /// opens an <see cref="EventBus.EventScope"/> BEFORE <see cref="OnAttach"/>
    /// runs, so every <c>EventBus.Subscribe</c> made inside <see cref="OnAttach"/>
    /// (including nested scopes) is bulk-unsubscribed by <see cref="Detach"/> —
    /// a forgotten Unsubscribe can no longer leak a presenter.
    /// </summary>
    public abstract class UIPresenter
    {
        private EventBus.EventScope _scope;

        /// <summary>The view this presenter drives (injected at construction).</summary>
        protected IUIView View { get; }

        /// <summary>True between <see cref="Attach"/> and <see cref="Detach"/>.</summary>
        public bool IsAttached { get; private set; }

        /// <summary>Creates the presenter for <paramref name="view"/>; injection only — no Unity APIs involved.</summary>
        protected UIPresenter(IUIView view)
        {
            View = view ?? throw new ArgumentNullException(nameof(view));
        }

        /// <summary>
        /// Opens the event scope and calls <see cref="OnAttach"/>. Idempotent:
        /// a second call while attached is ignored (UIManager guarantees one
        /// Attach per push, but tests and future callers should not be able
        /// to double-subscribe by accident).
        /// </summary>
        public void Attach(object args)
        {
            if (IsAttached)
            {
                return;
            }

            IsAttached = true;
            _scope = EventBus.OpenScope();
            OnAttach(args);
        }

        /// <summary>
        /// Calls <see cref="OnDetach"/> then disposes the event scope, which
        /// unsubscribes everything subscribed during <see cref="OnAttach"/>.
        /// Idempotent.
        /// </summary>
        public void Detach()
        {
            if (!IsAttached)
            {
                return;
            }

            IsAttached = false;
            OnDetach();
            _scope?.Dispose();
            _scope = null;
        }

        /// <summary>Subscribe to events and wire the view here (runs inside the scope).</summary>
        protected abstract void OnAttach(object args);

        /// <summary>Optional teardown before the scope unsubscribes the handlers.</summary>
        protected virtual void OnDetach()
        {
        }
    }
}
