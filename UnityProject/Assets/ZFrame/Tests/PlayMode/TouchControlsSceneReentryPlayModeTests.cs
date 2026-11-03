using System;
using System.Collections;
using System.Collections.Generic;
using Game.View.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using ZFrame.Core;
using ZFrame.Gameplay;
using ZFrame.View;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>
    /// TASK-A-AUDIT-02 A-3 — regression lock for the touch overlay across scene visits.
    ///
    /// The defect: <see cref="TouchControlsView.Spawn"/> de-duplicated by asking the scene
    /// "is there a TouchControlsView under the HUD canvas?", but <see cref="TouchControlsView.Shutdown"/>
    /// destroys the overlay with <c>Destroy</c> in play mode — which only takes effect at the END of the
    /// frame. A Spawn that runs inside that window gets the doomed instance back, and the overlay is gone
    /// a few microseconds later; Spawn runs once per scene, so nothing ever rebuilds it (on device: the
    /// stick and both buttons silently vanish on a later visit, while keyboard/gamepad still work — which
    /// is why desktop acceptance stayed green).
    ///
    /// Two cases, deliberately at two different levels:
    ///   1. <c>SecondSceneVisit_...</c> — two REAL single-mode scene loads with a real <see cref="GameHud"/>
    ///      per visit (the HUD host dies with the scene, exactly like in SCN_Game), asserting the second
    ///      visit still gets a live overlay wired to the live bridge.
    ///   2. <c>Spawn_DuringTheDestroyWindow_...</c> — the mechanism itself, frame-exact: Shutdown() then
    ///      Spawn() in the SAME frame (the window <c>Destroy</c> leaves open), then one frame later.
    ///
    /// Teeth (mutation-verified, see Docs/Logs/TASK-A-AUDIT-02.md §2.3): restoring the old body
    /// <code>if (existing != null) { return existing; }</code> turns case 2 red at
    /// <c>Assert.IsNotNull(second)</c> (it hands back the doomed instance, which the next assertion
    /// rejects as "same as"). Case 1 stays green under the mutant: a real single-mode load destroys the
    /// old HUD a frame before the next visit builds its own, so the window does not overlap there — that
    /// is recorded honestly in the task log rather than claimed as reproduced.
    ///
    /// <see cref="TouchControlsView.ForceVisible"/> is on for the whole fixture: desktop play mode has no
    /// touchscreen, and the overlay only exists where <c>ShouldShow</c> is true.
    /// </summary>
    public sealed class TouchControlsSceneReentryPlayModeTests
    {
        /// <summary>A build-settings scene that is not SCN_Game: cheap, and no boot chain runs.</summary>
        private const string VisitSceneName = "SCN_Empty";

        private const float BudgetSeconds = 60f;

        private readonly List<IDisposable> _tokens = new List<IDisposable>();
        private int _interactEvents;

        [SetUp]
        public void SetUp()
        {
            TouchControlsView.ForceVisible = true;
            Time.timeScale = 1f; // a previous fixture may have paused the clock
            _interactEvents = 0;
            _tokens.Add(EventBus.Subscribe<InteractInputEvent>(_ => _interactEvents++));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (IDisposable token in _tokens)
            {
                token.Dispose();
            }

            _tokens.Clear();

            // The overlay hangs on the persistent UIRoot canvas: never leak it into another case.
            foreach (TouchControlsView view in UnityEngine.Object.FindObjectsOfType<TouchControlsView>())
            {
                view.Shutdown();
                UnityEngine.Object.Destroy(view.gameObject);
            }

            TouchControlsView.ForceVisible = false;

            if (!SceneLoader.IsLoading)
            {
                Time.timeScale = 1f;
            }
        }

        // --------------------------------------------------- case 1: two real scene visits

        [UnityTest]
        public IEnumerator SecondSceneVisit_KeepsTheOverlay_AliveAndWiredToTheBridge()
        {
            UIRoot root = UIRoot.EnsureCreated();
            Assert.IsNotNull(root, "precondition: UIRoot.EnsureCreated must build the root");
            Assert.IsNotNull(InputRuntime.Bridge, "precondition: the input stack must be booted");

            // ---- visit 1 ----
            yield return SceneManager.LoadSceneAsync(VisitSceneName, LoadSceneMode.Single);
            GameHud firstHud = BuildHud(root);
            TouchControlsView first = RequireSingleOverlay("first visit");
            Assert.IsTrue(first.isActiveAndEnabled, "first visit: the overlay must be live");
            Assert.IsNotNull(first.InteractButton, "first visit: the interact button must be built");

            // ---- visit 2: a REAL single-mode load, so the old HUD host dies with its scene ----
            yield return SceneManager.LoadSceneAsync(VisitSceneName, LoadSceneMode.Single);
            yield return WaitUntil(() => firstHud == null, "the first visit's HUD host died with its scene");
            yield return WaitUntil(
                () => UnityEngine.Object.FindObjectOfType<TouchControlsView>() == null,
                "the first visit's overlay destroyed (it hangs on the persistent HUD canvas, so Shutdown owns it)");

            GameHud secondHud = BuildHud(root);
            Assert.IsNotNull(secondHud, "second visit: the HUD must build again");

            // Cross the end-of-frame destroy pass: a de-dupe hit on a doomed overlay turns into a
            // fake-null right here.
            yield return null;
            yield return null;

            TouchControlsView second = RequireSingleOverlay("second visit");

            Assert.IsTrue(second.isActiveAndEnabled,
                "second visit: the overlay must still exist and be enabled after the frame boundary " +
                "(pre-fix: Spawn could hand back the pending-destroy instance and the HUD field went fake-null)");
            Assert.AreNotSame(first, second,
                "second visit must build a NEW overlay, not hand back the one that was destroyed on scene exit");
            Assert.AreSame(InputRuntime.Bridge, second.Bridge,
                "second visit: the new overlay must be wired to the live input bridge");
            Assert.IsNotNull(second.Stick, "second visit: the stick must be built");
            Assert.IsNotNull(second.PauseButton, "second visit: the pause button must be built");

            // ---- and it really works: a press reaches the gameplay command chain ----
            int before = _interactEvents;
            second.InteractButton.OnPointerDown(new PointerEventData(EventSystem.current) { pointerId = 71 });
            second.InteractButton.OnPointerUp(new PointerEventData(EventSystem.current) { pointerId = 71 });

            Assert.AreEqual(before + 1, _interactEvents,
                "second visit: pressing 互动 must publish InteractInputEvent (a dead bridge would swallow it)");
        }

        // ------------------------------------------------------- case 2: the mechanism, frame-exact

        [UnityTest]
        public IEnumerator Spawn_DuringTheDestroyWindow_BuildsANewOverlay_InsteadOfReturningTheDoomedOne()
        {
            UIRoot root = UIRoot.EnsureCreated();
            Assert.IsNotNull(root, "precondition: UIRoot.EnsureCreated must build the root");

            PlayerInputBridge bridge = InputRuntime.Bridge;
            Assert.IsNotNull(bridge, "precondition: the input stack must be booted (InputRuntime.AutoBoot)");

            TouchControlsView first = TouchControlsView.Spawn(root, bridge);
            Assert.IsNotNull(first, "precondition: ForceVisible must make the overlay spawn on desktop");

            // The scene-exit step, verbatim: Shutdown() queues Destroy for the end of the frame.
            first.Shutdown();
            Assert.IsTrue(first != null,
                "precondition: the destroyed overlay is STILL visible to the scene in this frame " +
                "(this is the window the old de-dupe fell into)");

            // The scene-enter step, same frame: the new HUD asks for its overlay.
            TouchControlsView second = TouchControlsView.Spawn(root, bridge);

            Assert.IsNotNull(second,
                "Spawn must not return a pending-destroy overlay; it must build a live one");
            Assert.AreNotSame(first, second, "the doomed instance must never be handed out again");
            Assert.AreSame(bridge, second.Bridge, "the rebuilt overlay must be wired to the bridge");

            yield return null; // end of frame: the doomed overlay is really gone now

            Assert.IsTrue(second != null,
                "the rebuilt overlay must survive the end-of-frame destroy pass " +
                "(this is where the old logic's return value became fake-null)");
            Assert.IsTrue(second.isActiveAndEnabled, "the rebuilt overlay must be active and enabled");

            Assert.AreEqual(1, UnityEngine.Object.FindObjectsOfType<TouchControlsView>().Length,
                "after the destroy pass exactly one overlay may remain");

            Assert.IsNotNull(second.Stick, "the rebuilt overlay must have its stick");
            Assert.IsNotNull(second.InteractButton, "the rebuilt overlay must have its buttons");
        }

        // ---- helpers ------------------------------------------------------------

        /// <summary>Exactly one live overlay under the HUD canvas, returned; asserts the count so a duplicate is a failure.</summary>
        private static TouchControlsView RequireSingleOverlay(string what)
        {
            TouchControlsView[] found = UnityEngine.Object.FindObjectsOfType<TouchControlsView>();

            Assert.AreEqual(1, found.Length,
                what + ": expected exactly one TouchControlsView in the scene, found " + found.Length +
                " (zero = the controls silently vanished; two = the rebuild leaked the old one)" +
                " [diag: gameHuds=" + UnityEngine.Object.FindObjectsOfType<GameHud>().Length +
                " bridgeNull=" + (InputRuntime.Bridge == null) +
                " forceVisible=" + TouchControlsView.ForceVisible +
                " scene=" + SceneManager.GetActiveScene().name + "]");

            TouchControlsView view = found[0];
            Assert.IsTrue(view != null, what + ": the found overlay must not be a destroyed object");

            UIRoot root = UIRoot.EnsureCreated();
            Canvas hud = root.GetLayerCanvas(UIConsts.UILayer.HUD);
            Assert.IsNotNull(hud, what + ": the HUD layer canvas must exist");
            Assert.IsTrue(view.transform.IsChildOf(hud.transform),
                what + ": the overlay must hang on the persistent HUD canvas");

            return view;
        }

        /// <summary>
        /// Builds a gameplay HUD exactly the way GameBootstrap does on SceneLoadedEvent
        /// (<c>new GameObject("GameHud")</c> + <see cref="GameHud.Build"/>), so the overlay goes through
        /// the production <c>TouchControlsView.Spawn</c> path. The host lives in the current scene, so the
        /// next single-mode load destroys it — and its <c>OnDestroy</c> shuts the overlay down.
        /// </summary>
        private static GameHud BuildHud(UIRoot root)
        {
            var host = new GameObject("GameHud");
            var hud = host.AddComponent<GameHud>();
            hud.Build(root);
            return hud;
        }

        private static IEnumerator WaitUntil(Func<bool> done, string what)
        {
            float start = Time.realtimeSinceStartup;
            while (!done())
            {
                if (Time.realtimeSinceStartup - start > BudgetSeconds)
                {
                    Assert.Fail($"timeout ({BudgetSeconds:F0}s) waiting for: {what} " +
                                $"(active scene: {SceneManager.GetActiveScene().name})");
                }

                yield return null;
            }
        }
    }
}
