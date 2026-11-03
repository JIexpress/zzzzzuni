using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ZFrame.Debug.Tests.Support;
using ZFrame.View;

namespace ZFrame.Debug.Tests
{
    /// <summary>
    /// Shared EditMode environment for the UI framework fixtures: resets all
    /// statics (log/binding/instance), builds a fresh <see cref="UIRoot"/> per
    /// test and tears the scene objects down afterwards (DestroyImmediate —
    /// Object.Destroy is deferred in the editor and would leak between tests).
    /// </summary>
    internal static class UITestEnv
    {
        public static UIRoot SetUp()
        {
            UIRecordLog.Reset();
            UIBinding.Reset();
            UIRoot.ResetForTests();
            UIManager.ResetForTests();
            return UIRoot.EnsureCreated();
        }

        public static void TearDown()
        {
            if (UIRoot.Instance != null)
            {
                Object.DestroyImmediate(UIRoot.Instance.gameObject);
            }

            foreach (EventSystem eventSystem in Object.FindObjectsOfType<EventSystem>())
            {
                Object.DestroyImmediate(eventSystem.gameObject);
            }

            UIRoot.ResetForTests();
            UIManager.ResetForTests();
            UIBinding.Reset();
            UIRecordLog.Reset();
        }

        /// <summary>Synchronous Push (all test resource providers complete synchronously, so awaiting is safe).</summary>
        public static bool Push<TView>(UIManager ui, PanelConfig config, object args = null)
            where TView : class, IUIView, new()
        {
            return ui.Push<TView>(config, args).GetAwaiter().GetResult();
        }
    }
}
