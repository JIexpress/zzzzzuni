using System.Threading.Tasks;
using UnityEngine;

namespace ZFrame.View
{
    /// <summary>
    /// Single-completion carrier of one fade (TASK-R10-04, audit A-8). Every way a
    /// fade can end — natural end, host deactivation/destroy, target destroyed
    /// mid-flight, coroutine killed by anything else — funnels into
    /// <see cref="Settle"/>, which delivers EXACTLY ONE outcome to the awaiting
    /// caller (the first settle wins; every later one is a documented no-op).
    /// Before this session type existed, an interrupted coroutine simply never
    /// called TrySetResult and <c>UIManager.Push</c> awaited forever with the panel
    /// stuck in-flight (UI soft lock).
    /// </summary>
    internal sealed class FadeSession
    {
        private readonly TaskCompletionSource<UIAnimOutcome> _completion = new TaskCompletionSource<UIAnimOutcome>();

        /// <summary>The fade target — Unity fake-null once its GameObject is destroyed mid-fade.</summary>
        public CanvasGroup Group { get; }

        /// <summary>Last frame the owning routine advanced (watchdog stamp; registration frame while parked).</summary>
        public int LastActiveFrame { get; set; }

        /// <summary>True once exactly one outcome has been delivered.</summary>
        public bool IsSettled { get; private set; }

        /// <summary>The task every awaiter observes; completes with exactly one outcome.</summary>
        public Task<UIAnimOutcome> Task => _completion.Task;

        public FadeSession(CanvasGroup group)
        {
            Group = group;
        }

        /// <summary>
        /// Delivers <paramref name="outcome"/> exactly once; every later call returns
        /// false and changes nothing, so a race between a late natural completion and
        /// an interrupt sweep can never double-settle or overwrite the observation.
        /// </summary>
        public bool Settle(UIAnimOutcome outcome)
        {
            if (IsSettled)
            {
                return false;
            }

            IsSettled = true;
            return _completion.TrySetResult(outcome);
        }

        /// <summary>
        /// The audit-A-8 seam: settles this session as INTERRUPTED. Pure with respect
        /// to Unity — given only whether the fade target is lost, it picks the
        /// documented outcome (target destroyed → Failed; host gone → Cancelled) and
        /// lands the task, so EditMode tests can prove the "interrupted → settles,
        /// never hangs" contract without a real coroutine. Every interruption path
        /// (OnDisable sweep, watchdog sweep, the routine's own liveness check) goes
        /// through this method.
        /// </summary>
        public bool CompleteIfInterrupted(bool targetLost)
        {
            return Settle(targetLost ? UIAnimOutcome.Failed : UIAnimOutcome.Cancelled);
        }
    }
}
