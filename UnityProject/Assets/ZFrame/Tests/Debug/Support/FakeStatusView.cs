using System.Collections.Generic;
using ZFrame.View;

namespace ZFrame.Debug.Tests.Support
{
    /// <summary>Pure C# IUIView fake with a mutable status string — receives presenter output without any Unity type.</summary>
    public sealed class FakeStatusView : IUIView
    {
        /// <summary>Every value ever passed to <see cref="SetStatus"/> (first entry is the attach greeting).</summary>
        public readonly List<string> StatusHistory = new List<string>();

        /// <summary>Current status.</summary>
        public string Status { get; private set; }

        public void SetStatus(string status)
        {
            Status = status;
            StatusHistory.Add(status);
        }

        public void OnInit(object args)
        {
        }

        public void OnShow()
        {
        }

        public void OnPause()
        {
        }

        public void OnResume()
        {
        }

        public void OnHide()
        {
        }

        public void OnDestroy()
        {
        }
    }
}
