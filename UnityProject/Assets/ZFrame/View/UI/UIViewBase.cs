using UnityEngine;

namespace ZFrame.View
{
    /// <summary>
    /// UGUI base class for panel views: turns a MonoBehaviour into an
    /// <see cref="IUIView"/> so <see cref="UIPresenter"/> and
    /// <see cref="UIManager"/> can drive it through the lifecycle interface.
    ///
    /// IMPORTANT: <see cref="IUIView.OnDestroy"/> is implemented EXPLICITLY.
    /// A public method literally named "OnDestroy" on a MonoBehaviour is
    /// intercepted by Unity as the destroy message and would fire at
    /// arbitrary times (double-invocation); the explicit interface mapping
    /// keeps the lifecycle call strictly under <see cref="UIManager"/>'s
    /// control. Subclasses override <see cref="OnViewDestroy"/> instead.
    /// </summary>
    [DisallowMultipleComponent]
    public abstract class UIViewBase : MonoBehaviour, IUIView
    {
        /// <inheritdoc cref="IUIView.OnInit"/>
        public virtual void OnInit(object args)
        {
        }

        /// <inheritdoc cref="IUIView.OnShow"/>
        public virtual void OnShow()
        {
        }

        /// <inheritdoc cref="IUIView.OnPause"/>
        public virtual void OnPause()
        {
        }

        /// <inheritdoc cref="IUIView.OnResume"/>
        public virtual void OnResume()
        {
        }

        /// <inheritdoc cref="IUIView.OnHide"/>
        public virtual void OnHide()
        {
        }

        /// <inheritdoc cref="IUIView.OnDestroy"/>
        void IUIView.OnDestroy()
        {
            OnViewDestroy();
        }

        /// <summary>Teardown hook — subclasses release listeners/resources here (called exactly once by the lifecycle).</summary>
        protected virtual void OnViewDestroy()
        {
        }
    }
}
