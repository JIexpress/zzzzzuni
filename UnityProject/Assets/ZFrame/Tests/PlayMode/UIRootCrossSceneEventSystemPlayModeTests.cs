using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using ZFrame.View;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>
    /// TASK-A-AND-05 — regression lock for the A-AND-04 device fix (fcc59ad).
    ///
    /// The auto-created EventSystem used to be a ROOT object while only the
    /// UIRoot host got DontDestroyOnLoad. Loading SCN_Game in SINGLE mode
    /// therefore destroyed the EventSystem together with SCN_Boot, and
    /// <see cref="UIRoot.EnsureCreated"/> early-returned on the surviving
    /// <c>_instance</c> instead of rebuilding it — from that point every UGUI
    /// element in the game scene was dead (no pointer dispatch at all: the
    /// on-screen stick, the pause panel buttons, the settings key).
    ///
    /// This case reproduces that exact trigger: a REAL single-mode load of a
    /// DIFFERENT scene (SCN_Empty) — not a Destroy of some object — after the
    /// UIRoot was built. It then asserts three things:
    ///   1. EventSystem.current is still non-null (the symptom);
    ///   2. it is a CHILD of the UIRoot, i.e. it inherits the root's
    ///      DontDestroyOnLoad (this locks down the FIX ITSELF, so a future
    ///      "it happens to survive" refactor cannot silently drift back);
    ///   3. dispatch still works — a pointer-down/up/click goes through the
    ///      live EventSystem's GraphicRaycaster and reaches the handler.
    ///
    /// Mutation-verified: deleting <c>host.transform.SetParent(root, false)</c>
    /// in UIRoot.EnsureEventSystem turns this case red (assertion 1).
    ///
    /// Dispatch note (honest scope): batchmode has no OS pointer device to
    /// drive, so the input module cannot be fed a real click. The case does
    /// everything the module itself does after hit-testing — RaycastAll through
    /// the live EventSystem + ExecuteEvents on the hit handler — which is the
    /// phase the regression killed. It is not a simulated mouse.
    /// </summary>
    public sealed class UIRootCrossSceneEventSystemPlayModeTests
    {
        /// <summary>A build-settings scene that is neither boot nor game: cheap, and no boot chain runs.</summary>
        private const string OtherSceneName = "SCN_Empty";

        private const float LoadBudgetSeconds = 30f;

        private GameObject _sceneMarker;

        private GameObject _probe;

        [TearDown]
        public void TearDown()
        {
            // The probe lives under the shared (persistent) UIRoot — never leak it into other cases.
            if (_probe != null)
            {
                UnityEngine.Object.Destroy(_probe);
            }

            if (_sceneMarker != null)
            {
                UnityEngine.Object.Destroy(_sceneMarker);
            }
        }

        [UnityTest]
        public IEnumerator CrossSceneLoad_EventSystemStaysUnderUIRoot_AndStillDispatchesPointer()
        {
            UIRoot uiRoot = UIRoot.EnsureCreated();
            Assert.IsNotNull(uiRoot, "precondition: UIRoot.EnsureCreated must build the root");

            EventSystem eventSystemBefore = EventSystem.current;
            Assert.IsNotNull(eventSystemBefore,
                "precondition: an EventSystem exists before the load (true for BOTH the fixed and the " +
                "pre-fix code — the pre-fix one is a root object and only dies with the scene)");

            PointerProbe probe = BuildPointerProbe(uiRoot);
            _probe = probe.gameObject;

            // A plain scene object: if it outlives the load, no real single-mode unload happened
            // and the rest of this case would be proving nothing.
            _sceneMarker = new GameObject("AAND05_SceneMarker");

            yield return null; // one frame: the layer canvases register their GraphicRaycasters

            // ---- the defect trigger: a REAL single-mode load of another scene ----
            yield return SceneManager.LoadSceneAsync(OtherSceneName, LoadSceneMode.Single);
            yield return WaitUntil(
                () => string.Equals(SceneManager.GetActiveScene().name, OtherSceneName, StringComparison.Ordinal),
                "SCN_Empty becomes the active scene");
            yield return null; // the unloaded scene's objects are gone by now (Destroy is end-of-frame)

            Assert.IsTrue(_sceneMarker == null,
                "the single-mode load must have destroyed the previous scene's objects " +
                "(otherwise this case does not reproduce the defect)");
            Assert.IsTrue(uiRoot != null, "the UIRoot host itself must survive the load (DontDestroyOnLoad)");

            // ---- assertion 1: the EventSystem is still alive (the user-visible symptom) ----
            EventSystem eventSystem = EventSystem.current;
            Assert.IsNotNull(eventSystem,
                "EventSystem.current is null after a single-mode scene load — UGUI dispatch is dead in the " +
                "new scene (pre-A-AND-04 behaviour, fcc59ad)");

            Assert.AreSame(eventSystemBefore, eventSystem,
                "the EventSystem must SURVIVE the load, not be destroyed and rebuilt after a dead frame");

            // ---- assertion 2: and it lives under the UIRoot (the fix itself, not luck) ----
            Assert.IsTrue(eventSystem.transform.IsChildOf(uiRoot.transform),
                $"the EventSystem ('{eventSystem.name}') must be a CHILD of the UIRoot ('{uiRoot.name}') so it " +
                "inherits DontDestroyOnLoad; a root-level EventSystem dies with the scene being unloaded");

            // ---- assertion 3: the symptom — UGUI dispatch still works ----
            Assert.IsTrue(eventSystem.isActiveAndEnabled, "the surviving EventSystem must be active and enabled");
            Assert.IsTrue(eventSystem.IsActive(), "EventSystem.IsActive() must be true after the load");
            Assert.IsNotNull(eventSystem.currentInputModule,
                "the StandaloneInputModule must still be attached and enabled after the load");

            Vector3 worldCenter = probe.transform.TransformPoint(probe.Rect.center);
            Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(null, worldCenter);

            var pointer = new PointerEventData(eventSystem)
            {
                position = screenPoint,
                pressPosition = screenPoint,
                button = PointerEventData.InputButton.Left,
                clickCount = 1,
            };

            var hits = new List<RaycastResult>();
            eventSystem.RaycastAll(pointer, hits); // the live EventSystem + the UIRoot's GraphicRaycaster

            bool hitProbe = false;
            RaycastResult probeHit = default;
            foreach (RaycastResult hit in hits)
            {
                if (hit.gameObject == probe.gameObject)
                {
                    probeHit = hit;
                    hitProbe = true;
                    break;
                }
            }

            Assert.IsTrue(hitProbe,
                $"the surviving EventSystem must still raycast the UIRoot's UI (probe at {screenPoint}, " +
                $"{hits.Count} hit(s) — a dead/absent EventSystem returns none)");

            pointer.pointerCurrentRaycast = probeHit;
            pointer.pointerPressRaycast = probeHit;

            ExecuteEvents.Execute(probe.gameObject, pointer, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(probe.gameObject, pointer, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(probe.gameObject, pointer, ExecuteEvents.pointerClickHandler);

            Assert.AreEqual(1, probe.DownCount, "the pointer-down handler under the UIRoot must receive the event");
            Assert.AreEqual(1, probe.UpCount, "the pointer-up handler under the UIRoot must receive the event");
            Assert.AreEqual(1, probe.ClickCount, "the click handler under the UIRoot must receive the event");
        }

        // ---- helpers ------------------------------------------------------------

        /// <summary>Full-screen raycast target under the Popup layer canvas, counting pointer callbacks.</summary>
        private static PointerProbe BuildPointerProbe(UIRoot uiRoot)
        {
            Canvas canvas = uiRoot.GetLayerCanvas(UIConsts.UILayer.Popup);
            Assert.IsNotNull(canvas, "precondition: the Popup layer canvas exists");

            var go = new GameObject("AAND05_PointerProbe", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(canvas.transform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var image = go.AddComponent<Image>();
            image.color = Color.clear; // invisible, still hit-testable (raycastTarget is what matters)
            image.raycastTarget = true;

            return go.AddComponent<PointerProbe>();
        }

        private static IEnumerator WaitUntil(Func<bool> done, string what, float timeoutSeconds = LoadBudgetSeconds)
        {
            float start = Time.realtimeSinceStartup;
            while (!done())
            {
                if (Time.realtimeSinceStartup - start > timeoutSeconds)
                {
                    Assert.Fail($"timeout ({timeoutSeconds:F0}s) waiting for: {what} " +
                                $"(active scene: {SceneManager.GetActiveScene().name})");
                }

                yield return null;
            }
        }

        /// <summary>Raycast target that records whether UGUI actually reached it.</summary>
        private sealed class PointerProbe : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
        {
            public int DownCount { get; private set; }

            public int UpCount { get; private set; }

            public int ClickCount { get; private set; }

            public Rect Rect => ((RectTransform)transform).rect;

            public void OnPointerDown(PointerEventData eventData)
            {
                DownCount++;
            }

            public void OnPointerUp(PointerEventData eventData)
            {
                UpCount++;
            }

            public void OnPointerClick(PointerEventData eventData)
            {
                ClickCount++;
            }
        }
    }
}
