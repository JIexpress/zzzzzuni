using Game.View.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ZFrame.View;

namespace Game.View.Tests
{
    /// <summary>
    /// HUD lifetime (TASK-H-01 D4): <see cref="GameHud"/>'s captions hang on the
    /// DontDestroyOnLoad <see cref="UIRoot"/> HUD canvas while its host GameObject
    /// dies with the gameplay scene. Without an explicit teardown every visit to
    /// SCN_Game leaked two Texts and left the stale text of the previous run
    /// visible in the next scene.
    ///
    /// EditMode does not drive MonoBehaviour OnDestroy (A-M3-02 seam precedent),
    /// so the tests invoke <see cref="GameHud.Shutdown"/> directly — the exact
    /// path play mode reaches via OnDestroy→Shutdown (TASK-H-06).
    /// </summary>
    public sealed class GameHudLeakTests
    {
        private UIRoot _root;

        [SetUp]
        public void SetUp()
        {
            UIRoot.ResetForTests();
            _root = UIRoot.EnsureCreated();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (UIRoot root in Object.FindObjectsOfType<UIRoot>(true))
            {
                Object.DestroyImmediate(root.gameObject);
            }

            foreach (EventSystem eventSystem in Object.FindObjectsOfType<EventSystem>())
            {
                Object.DestroyImmediate(eventSystem.gameObject);
            }

            UIRoot.ResetForTests();
        }

        [Test]
        public void HudHostDestroyed_LeavesNoCaptionsOnThePersistentUIRoot()
        {
            Canvas hud = _root.GetLayerCanvas(UIConsts.UILayer.HUD);
            Assert.IsNotNull(hud, "the UIRoot must own a HUD layer canvas");
            int baseline = hud.transform.childCount;

            for (int visit = 1; visit <= 3; visit++)
            {
                var host = new GameObject("GameHud");
                GameHud gameHud = host.AddComponent<GameHud>();
                gameHud.Build(_root);

                Assert.AreEqual(baseline + 2, hud.transform.childCount,
                    $"visit #{visit}: Build must add exactly the two captions to the HUD canvas");

                // EditMode never fires MonoBehaviour OnDestroy, so the test drives
                // the same teardown explicitly — exactly what the engine reaches
                // via OnDestroy→Shutdown when the scene unloads in play mode.
                gameHud.Shutdown();

                // The host lives in the gameplay scene, so it is destroyed on scene unload —
                // Object.Destroy is deferred in the editor, DestroyImmediate models the unload.
                Object.DestroyImmediate(host);

                Assert.AreEqual(baseline, hud.transform.childCount,
                    $"visit #{visit}: Shutdown must destroy its own captions (2 Texts per visit would leak otherwise)");
                Assert.IsTrue(hud.transform.Find("PickupCounter") == null,
                    $"visit #{visit}: the pickup caption must be gone from the persistent canvas");
                Assert.IsTrue(hud.transform.Find("EnemyHint") == null,
                    $"visit #{visit}: the enemy hint must be gone from the persistent canvas");
            }
        }

        [Test]
        public void HudCaptions_AreStillCreatedOnTheHudCanvas()
        {
            var host = new GameObject("GameHud");
            try
            {
                GameHud gameHud = host.AddComponent<GameHud>();
                gameHud.Build(_root);

                Canvas hud = _root.GetLayerCanvas(UIConsts.UILayer.HUD);
                Assert.IsNotNull(hud.transform.Find("PickupCounter"), "the pickup counter lives on the HUD layer");
                Assert.IsNotNull(hud.transform.Find("EnemyHint"), "the enemy hint lives on the HUD layer");
                Assert.AreEqual("拾取计数：0", hud.transform.Find("PickupCounter").GetComponent<Text>().text,
                    "the counter starts at zero");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }
    }
}
