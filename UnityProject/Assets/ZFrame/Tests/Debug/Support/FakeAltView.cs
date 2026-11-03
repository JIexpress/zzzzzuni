using ZFrame.View;

namespace ZFrame.Debug.Tests.Support
{
    /// <summary>Second pure C# IUIView fake — a distinct TYPE so two panels can be pushed side by side (duplicate-type rule).</summary>
    public sealed class FakeAltView : IUIView
    {
        /// <summary>Unique id (shared counter with the other fakes).</summary>
        public int Id { get; }

        public int PauseCount { get; private set; }

        public FakeAltView()
        {
            Id = UIRecordLog.NextId();
        }

        public void OnInit(object args)
        {
            UIRecordLog.Add($"{Id}.OnInit");
        }

        public void OnShow()
        {
            UIRecordLog.Add($"{Id}.OnShow");
        }

        public void OnPause()
        {
            PauseCount++;
            UIRecordLog.Add($"{Id}.OnPause");
        }

        public void OnResume()
        {
            UIRecordLog.Add($"{Id}.OnResume");
        }

        public void OnHide()
        {
            UIRecordLog.Add($"{Id}.OnHide");
        }

        public void OnDestroy()
        {
            UIRecordLog.Add($"{Id}.OnDestroy");
        }
    }
}
