using System;
using System.Collections.Generic;

namespace ZFrame.View
{
    /// <summary>
    /// MVP assembly registry (TASK-A-M3-01): a static View → Presenter-factory
    /// dictionary. <see cref="UIManager"/> looks the presenter up here at Push
    /// time and creates it through the registered factory — NO reflection over
    /// assemblies, no attribute scanning. Registration happens at setup time:
    /// <see cref="EnsureDefaults"/> wires the framework's sample bindings and
    /// is called by <see cref="UIManager"/>'s constructor; games add their own
    /// bindings on top by calling <see cref="Register{TView}"/> during boot.
    /// </summary>
    public static class UIBinding
    {
        private static readonly Dictionary<Type, Func<IUIView, UIPresenter>> Factories =
            new Dictionary<Type, Func<IUIView, UIPresenter>>();

        private static bool _defaultsInstalled;

        /// <summary>Number of registered bindings (diagnostics/tests).</summary>
        public static int Count => Factories.Count;

        /// <summary>
        /// Registers how to build the presenter for <typeparamref name="TView"/>.
        /// Re-registering the same view replaces the previous factory (allows
        /// test overrides without an unregister API).
        /// </summary>
        public static void Register<TView>(Func<IUIView, UIPresenter> factory) where TView : IUIView
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            Factories[typeof(TView)] = factory;
        }

        /// <summary>Looks the presenter factory up; false = the view has no presenter (pure view is fine).</summary>
        public static bool TryGetFactory(Type viewType, out Func<IUIView, UIPresenter> factory)
        {
            return Factories.TryGetValue(viewType, out factory) && factory != null;
        }

        /// <summary>Removes all bindings and forgets that defaults were installed. Intended for test isolation.</summary>
        public static void Reset()
        {
            Factories.Clear();
            _defaultsInstalled = false;
        }

        /// <summary>
        /// Installs the framework's built-in bindings once (idempotent). Kept
        /// explicit and tiny — the sample TestPanel binding ships here because
        /// the panel prefab is part of the framework's acceptance assets.
        /// Game-specific bindings are registered by game boot code, not here.
        /// </summary>
        public static void EnsureDefaults()
        {
            if (_defaultsInstalled)
            {
                return;
            }

            Register<TestPanelView>(view => new TestPanelPresenter((TestPanelView)view));
            Register<SettingsPanelView>(view => new SettingsPanelPresenter((SettingsPanelView)view));
            _defaultsInstalled = true;
        }
    }
}
