using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using ZFrame.View;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>
    /// TASK-A-AUDIT-03 (B-3) — regression lock for "EnsureEventSystem accepts any EventSystem".
    ///
    /// The A-AND-04 fix (fcc59ad) made the auto-created EventSystem a child of the
    /// DontDestroyOnLoad UIRoot. But <c>EnsureEventSystem</c> still returned early as soon
    /// as ANY EventSystem existed — so a scene that ships its own (non-persistent)
    /// EventSystem got it adopted, and the next single-mode load destroyed it: UGUI
    /// dispatch dead in the new scene, exactly the original device failure.
    ///
    /// This case reproduces that shape: our EventSystem is removed, a scene-authored one
    /// is put in its place, the statics are cleared (what EnsureCreated early-returns on
    /// after a reload), and EnsureCreated runs. Then a REAL single-mode load must leave a
    /// live EventSystem that is OUR persistent one.
    ///
    /// Mutation-verified: restoring <c>if (Object.FindObjectOfType&lt;EventSystem&gt;() != null) return;</c>
    /// turns this case red (EventSystem.current after the load belongs to the destroyed
    /// scene / is null, and no child EventSystem exists under the UIRoot).
    /// </summary>
    public sealed class UIRootForeignEventSystemPlayModeTests
    {
        private const string OtherSceneName = "SCN_Empty";

        private const float LoadBudgetSeconds = 30f;

        private GameObject _foreign;

        private GameObject _sceneMarker;

        [TearDown]
        public void TearDown()
        {
            if (_foreign != null)
            {
                UnityEngine.Object.Destroy(_foreign);
            }

            if (_sceneMarker != null)
            {
                UnityEngine.Object.Destroy(_sceneMarker);
            }
        }

        [UnityTest]
        public IEnumerator SceneAuthoredNonPersistentEventSystem_IsNotAdopted_OursSurvivesTheSceneLoad()
        {
            UIRoot root = UIRoot.EnsureCreated();
            Assert.IsNotNull(root, "precondition: EnsureCreated must build the root");
            yield return null;

            EventSystem ours = FindEventSystemUnder(root);
            Assert.IsNotNull(ours, "precondition: the UIRoot owns an EventSystem child (A-AND-04)");

            // ---- build the defect's shape ----
            UnityEngine.Object.Destroy(ours.gameObject);
            yield return null;
            yield return null; // deferred destroy lands at end of frame

            Assert.IsNull(FindEventSystemUnder(root), "precondition: the root no longer owns an EventSystem");

            _foreign = new GameObject("AUDIT03_SceneAuthoredEventSystem");
            _foreign.AddComponent<EventSystem>();
            _foreign.AddComponent<StandaloneInputModule>();
            yield return null;

            Assert.IsTrue(_foreign != null && _foreign.transform.IsChildOf(root.transform) == false,
                "precondition: the foreign EventSystem is a scene object, not part of the persistent root");

            // ---- the defect trigger: statics lost, EnsureCreated has to re-verify ----
            SimulateDomainReload();
            UIRoot readopted = UIRoot.EnsureCreated();
            yield return null;

            EventSystem current = EventSystem.current;
            Assert.IsNotNull(current, "EnsureCreated left no live EventSystem although a non-persistent one was present");
            Assert.IsTrue(current.transform.IsChildOf(readopted.transform),
                $"the live EventSystem ('{current.name}') must be OUR persistent one under the UIRoot, " +
                "not the scene's copy that dies with its scene (A-AND-04, fcc59ad)");
            Assert.IsNotNull(FindEventSystemUnder(readopted), "the UIRoot must own its EventSystem again");

            EventSystem foreign = _foreign.GetComponent<EventSystem>();
            Assert.IsFalse(foreign.enabled,
                "the non-persistent scene EventSystem must be switched off — two enabled systems fight over " +
                "EventSystem.current and dispatch every pointer twice");

            // ---- the symptom: a REAL single-mode load unloads the foreign scene object ----
            _sceneMarker = new GameObject("AUDIT03_SceneMarker");
            yield return SceneManager.LoadSceneAsync(OtherSceneName, LoadSceneMode.Single);
            yield return WaitUntil(
                () => string.Equals(SceneManager.GetActiveScene().name, OtherSceneName, StringComparison.Ordinal),
                "SCN_Empty becomes the active scene");
            yield return null; // the unloaded scene's objects are gone by now (Destroy is end-of-frame)

            Assert.IsTrue(_sceneMarker == null,
                "the single-mode load must have destroyed the previous scene's objects (otherwise this case " +
                "does not reproduce the defect)");

            EventSystem afterLoad = EventSystem.current;
            Assert.IsNotNull(afterLoad,
                "EventSystem.current is null after a single-mode load — every UGUI element in the new scene is " +
                "dead (the pre-A-AND-04 behaviour came back through the non-persistent EventSystem)");
            Assert.IsTrue(afterLoad.transform.IsChildOf(readopted.transform),
                "the surviving EventSystem must be the UIRoot's child so it inherits DontDestroyOnLoad");
            Assert.IsTrue(afterLoad.isActiveAndEnabled, "the surviving EventSystem must be active and enabled");
            Assert.IsNotNull(afterLoad.currentInputModule, "the input module must still be attached after the load");
        }

        private static EventSystem FindEventSystemUnder(UIRoot root)
        {
            EventSystem[] all = UnityEngine.Object.FindObjectsOfType<EventSystem>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].transform.IsChildOf(root.transform))
                {
                    return all[i];
                }
            }

            return null;
        }

        /// <summary>Clears the static instance the way a domain reload would (no production seam exists).</summary>
        private static void SimulateDomainReload()
        {
            FieldInfo instance = typeof(UIRoot).GetField(
                "_instance", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(instance, "UIRoot._instance not found — the reload simulation is stale");
            instance.SetValue(null, null);
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
    }
}
