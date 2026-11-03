using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using ZFrame.View;

namespace ZFrame.Debug.Tests
{
    /// <summary>
    /// TASK-R10-04 (audit A-8): the single-completion contract of
    /// <see cref="UIAnimRunner"/> — every fade delivers EXACTLY ONE outcome
    /// (Completed/Cancelled/Failed) to its awaiter, however the fade dies, and the
    /// interrupted paths are reachable WITHOUT a real coroutine: EditMode never runs
    /// the routine body (fades are instant there by design — the audit's "why tests
    /// can't catch this"), so the tests park fades through the
    /// <c>HoldFadesForTests</c> seam (exactly the state a runtime fade occupies
    /// between StartCoroutine and completion) and interrupt them via
    /// <c>AbortActive</c> — the same settle point OnDisable uses — plus directly on
    /// the pure seam <see cref="FadeSession.CompleteIfInterrupted"/> that every
    /// interruption path funnels through.
    /// </summary>
    public sealed class UIAnimRunnerTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();
        private UIRoot _root;
        private UIAnimRunner _runner;

        [SetUp]
        public void SetUp()
        {
            _root = UITestEnv.SetUp();
            _runner = _root.GetComponent<UIAnimRunner>();
            Assert.IsNotNull(_runner, "UIRoot hosts the fade runner");
        }

        [TearDown]
        public void TearDown()
        {
            // The root is destroyed by UITestEnv (fresh runner per test, so the seam
            // flag cannot leak); the helper objects this fixture spawned are ours.
            foreach (GameObject created in _created)
            {
                if (created != null)
                {
                    Object.DestroyImmediate(created);
                }
            }

            _created.Clear();
            UITestEnv.TearDown();
        }

        // ---- regression: the deterministic paths -------------------------------

        [Test]
        public void FadeIn_EditMode_CompletesInstantlyWithCompleted()
        {
            CanvasGroup group = NewGroup("fade_target");
            group.alpha = 0f;

            Task<UIAnimOutcome> fade = _runner.FadeIn(group);

            Assert.IsTrue(fade.IsCompleted, "edit mode fades are instant (no coroutine)");
            Assert.AreEqual(UIAnimOutcome.Completed, fade.Result);
            Assert.AreEqual(1f, group.alpha, "the instant fade must still write the target alpha");
        }

        [Test]
        public void FadeOut_NullGroup_CompletesInstantly()
        {
            Task<UIAnimOutcome> fade = _runner.FadeOut(null);

            Assert.IsTrue(fade.IsCompleted);
            Assert.AreEqual(UIAnimOutcome.Completed, fade.Result);
        }

        // ---- the pure seam: interrupted → settles, exactly once -----------------

        [Test]
        public void CompleteIfInterrupted_TargetLost_IsFailed_AliveTarget_IsCancelled()
        {
            FadeSession lost = NewSession(NewGroup("lost_target"));
            Assert.IsTrue(lost.CompleteIfInterrupted(targetLost: true));
            Assert.AreEqual(UIAnimOutcome.Failed, lost.Task.Result,
                "a destroyed fade target fails the fade");

            FadeSession alive = NewSession(NewGroup("alive_target"));
            Assert.IsTrue(alive.CompleteIfInterrupted(targetLost: false));
            Assert.AreEqual(UIAnimOutcome.Cancelled, alive.Task.Result,
                "host loss with a live target cancels the fade");
        }

        [Test]
        public void Settle_IsExactlyOnce_FirstOutcomeWins()
        {
            FadeSession session = NewSession(NewGroup("single_target"));

            Assert.IsFalse(session.IsSettled, "a parked fade is pending");
            Assert.IsTrue(session.Settle(UIAnimOutcome.Cancelled));
            Assert.IsTrue(session.IsSettled);
            Assert.AreEqual(UIAnimOutcome.Cancelled, session.Task.Result);

            Assert.IsFalse(session.Settle(UIAnimOutcome.Completed), "a late completion must not overwrite");
            Assert.IsFalse(session.CompleteIfInterrupted(targetLost: true), "a second interrupt must not overwrite");
            Assert.AreEqual(UIAnimOutcome.Cancelled, session.Task.Result,
                "exactly one outcome ever reaches the awaiter");
        }

        // ---- the runner seams: pending registry + abort sweep -------------------

        [Test]
        public void AbortActive_PendingFade_SettlesCancelled_AndUnregisters()
        {
            _runner.HoldFadesForTests = true;
            Task<UIAnimOutcome> fade = _runner.FadeIn(NewGroup("parked"));

            Assert.IsFalse(fade.IsCompleted, "precondition: the parked fade is pending");

            Assert.AreEqual(1, _runner.AbortActive(), "exactly the parked fade was pending");
            Assert.IsTrue(fade.IsCompleted, "an interrupted fade must settle, never hang (audit A-8)");
            Assert.AreEqual(UIAnimOutcome.Cancelled, fade.Result, "host loss cancels the fade");

            Assert.AreEqual(0, _runner.AbortActive(), "the sweep clears the registry (no stale session)");
        }

        [Test]
        public void AbortActive_TargetDestroyedMidFade_SettlesFailed()
        {
            GameObject host = NewGroup("doomed_target").gameObject;
            _runner.HoldFadesForTests = true;
            Task<UIAnimOutcome> fade = _runner.FadeIn(host.GetComponent<CanvasGroup>());

            Object.DestroyImmediate(host); // the audit trigger: target gone mid-fade

            Assert.AreEqual(1, _runner.AbortActive());
            Assert.IsTrue(fade.IsCompleted, "a destroyed target must settle, never hang (audit A-8)");
            Assert.AreEqual(UIAnimOutcome.Failed, fade.Result, "target loss fails the fade");
        }

        // ---- helpers -------------------------------------------------------------

        private CanvasGroup NewGroup(string name)
        {
            GameObject host = new GameObject(name, typeof(RectTransform));
            _created.Add(host);
            return host.AddComponent<CanvasGroup>();
        }

        private static FadeSession NewSession(CanvasGroup group)
        {
            return new FadeSession(group);
        }
    }
}
