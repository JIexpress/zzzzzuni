using ZFrame.View;

namespace ZFrame.Debug.Tests.Support
{
    /// <summary>Pure C# IUIView fake: records every lifecycle call into <see cref="UIRecordLog"/> and counts pause/resume transitions.</summary>
    public sealed class FakeUIView : IUIView
    {
        /// <summary>Unique id (shared counter with the other fakes so a single log stays unambiguous).</summary>
        public int Id { get; }

        public int PauseCount { get; private set; }

        public int ResumeCount { get; private set; }

        public bool Destroyed { get; private set; }

        public object LastInitArgs { get; private set; }

        public FakeUIView()
        {
            Id = UIRecordLog.NextId();
        }

        public void OnInit(object args)
        {
            LastInitArgs = args;
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
            ResumeCount++;
            UIRecordLog.Add($"{Id}.OnResume");
        }

        public void OnHide()
        {
            UIRecordLog.Add($"{Id}.OnHide");
        }

        public void OnDestroy()
        {
            Destroyed = true;
            UIRecordLog.Add($"{Id}.OnDestroy");
        }
    }
}
