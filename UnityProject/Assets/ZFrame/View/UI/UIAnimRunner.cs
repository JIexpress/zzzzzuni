using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace ZFrame.View
{
    /// <summary>
    /// Tiny coroutine host living on the <see cref="UIRoot"/> that performs the
    /// simple alpha fades requested by <see cref="PanelAnim.Fade"/> (TASK-A-M3-01
    /// keeps animations minimal). Runtime only: in the editor (Edit tests) every
    /// fade completes immediately so lifecycle/stack tests stay deterministic.
    ///
    /// TASK-R10-04 (audit A-8, UI soft lock): every fade is carried by a
    /// <see cref="FadeSession"/> whose outcome reaches the awaiter EXACTLY ONCE
    /// (Completed/Cancelled/Failed). Before this contract an interrupted fade
    /// (host deactivated/destroyed, target destroyed mid-flight) simply never
    /// settled its TaskCompletionSource, so <c>UIManager.Push</c> awaited forever
    /// with the panel stuck in-flight — the same panel could never be pushed again.
    /// Every interruption now funnels through the pure seam
    /// <see cref="FadeSession.CompleteIfInterrupted"/>:
    /// - host loss via <see cref="OnDisable"/> (deactivation AND destroy both call it),
    /// - target loss via the routine's own per-frame liveness check,
    /// - anything else (e.g. a foreign StopAllCoroutines) via the frame-stamp
    ///   watchdog in <see cref="Update"/> — no path can leave an awaiter pending.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UIAnimRunner : MonoBehaviour
    {
        /// <summary>Duration of one fade in seconds (short on purpose — a blocking fade delays Push/Pop).</summary>
        public const float DefaultFadeDuration = 0.15f;

        /// <summary>Frames a live fade may lag behind its last stamp before the watchdog calls it interrupted.</summary>
        private const int WatchdogGraceFrames = 2;

        private readonly List<FadeSession> _active = new List<FadeSession>();

        /// <summary>
        /// TEST SEAM ONLY (TASK-R10-04) — never set by production code. When true,
        /// <see cref="FadeIn"/>/<see cref="FadeOut"/> register the in-flight session
        /// and return its pending task WITHOUT starting the coroutine and WITHOUT
        /// writing alpha: exactly the state a runtime fade occupies between
        /// StartCoroutine and completion. This makes the interruption paths drivable
        /// in EditMode, which structurally cannot run the routine body (fades are
        /// instant there by design — the audit's "why tests can't catch this").
        /// </summary>
        internal bool HoldFadesForTests { get; set; }

        /// <summary>Fades <paramref name="group"/> alpha from 0 to 1 (instant in edit mode).</summary>
        public Task<UIAnimOutcome> FadeIn(CanvasGroup group, float duration = DefaultFadeDuration)
        {
            return Fade(group, 0f, 1f, duration);
        }

        /// <summary>Fades <paramref name="group"/> alpha from its current value to 0 (instant in edit mode).</summary>
        public Task<UIAnimOutcome> FadeOut(CanvasGroup group, float duration = DefaultFadeDuration)
        {
            return Fade(group, group != null ? group.alpha : 1f, 0f, duration);
        }

        /// <summary>
        /// Settles every fade still in flight and clears the registry (TASK-R10-04).
        /// The coroutine host is going away (deactivation or destroy) so no routine
        /// will ever advance again — each pending session must land now or its
        /// awaiter hangs forever (audit A-8). Sessions already settled by their own
        /// routine are skipped by the exactly-once guarantee. Returns how many
        /// pending fades were settled. Doubles as the EditMode test seam: tests park
        /// fades via <see cref="HoldFadesForTests"/> and interrupt them here.
        /// </summary>
        internal int AbortActive()
        {
            int settled = 0;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                FadeSession session = _active[i];
                _active.RemoveAt(i);
                if (session.CompleteIfInterrupted(targetLost: session.Group == null))
                {
                    settled++;
                }
            }

            return settled;
        }

        private void OnDisable()
        {
            // Fires for deactivation AND destroy (Unity calls OnDisable first for an
            // active behaviour) — in both cases the routines stop advancing, so the
            // pending sessions must settle now. Idempotent via exactly-once Settle.
            AbortActive();
        }

        private void Update()
        {
            // Watchdog (TASK-R10-04): the last-resort sweep for interruptions that
            // fire no callback of their own (e.g. a foreign StopAllCoroutines on the
            // root). A live routine stamps its session every iteration; a killed one
            // stops stamping and is settled here — no kill path may stay pending.
            // Runs only in play mode (Update does not run in the editor without
            // ExecuteInEditMode, and edit-mode fades never register sessions).
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                FadeSession session = _active[i];
                if (Time.frameCount - session.LastActiveFrame > WatchdogGraceFrames)
                {
                    _active.RemoveAt(i);
                    session.CompleteIfInterrupted(targetLost: session.Group == null);
                }
            }
        }

        private Task<UIAnimOutcome> Fade(CanvasGroup group, float from, float to, float duration)
        {
            if (group == null)
            {
                return Task.FromResult(UIAnimOutcome.Completed);
            }

            if (Application.isPlaying && duration > 0f && !HoldFadesForTests)
            {
                var session = new FadeSession(group);
                session.LastActiveFrame = Time.frameCount; // the routine re-stamps every iteration
                _active.Add(session);
                StartCoroutine(FadeRoutine(session, from, to, duration));
                return session.Task;
            }

            if (HoldFadesForTests)
            {
                var session = new FadeSession(group); // test seam: parked, unsettled, no coroutine
                _active.Add(session);
                return session.Task;
            }

            group.alpha = to; // edit mode / zero duration: deterministic immediate completion
            return Task.FromResult(UIAnimOutcome.Completed);
        }

        private System.Collections.IEnumerator FadeRoutine(FadeSession session, float from, float to, float duration)
        {
            float time = 0f;
            while (time < duration)
            {
                time += Time.unscaledDeltaTime;
                if (session.Group == null)
                {
                    // Target destroyed mid-fade: the coroutine machine would swallow
                    // the MissingReferenceException this write throws, which is how
                    // the awaiter used to hang forever (audit A-8) — settle instead.
                    EndActive(session, UIAnimOutcome.Failed);
                    yield break;
                }

                session.Group.alpha = Mathf.Lerp(from, to, Mathf.Clamp01(time / duration));
                session.LastActiveFrame = Time.frameCount;
                yield return null;
            }

            if (session.Group == null)
            {
                EndActive(session, UIAnimOutcome.Failed); // the final write must not re-introduce the hole
                yield break;
            }

            session.Group.alpha = to;
            EndActive(session, UIAnimOutcome.Completed);
        }

        /// <summary>Unregisters <paramref name="session"/> and settles it (exactly-once via <see cref="FadeSession.Settle"/> — a late completion after an interrupt sweep cannot overwrite the outcome).</summary>
        private void EndActive(FadeSession session, UIAnimOutcome outcome)
        {
            _active.Remove(session);
            session.Settle(outcome);
        }
    }
}
