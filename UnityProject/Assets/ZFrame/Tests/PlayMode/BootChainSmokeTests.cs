using System;
using System.Collections;
using Game.Domain.Interaction;
using Game.Bootstrap;
using Game.View.Interaction;
using Game.View.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using ZFrame.Core;
using ZFrame.Data;
using ZFrame.Data.Save;
using ZFrame.Gameplay;
using ZFrame.View;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>
    /// PlayMode smoke of the M4 vertical slice (TASK-A-M4-02): five cases over
    /// the real boot chain — boot reaches the main menu, menu "开始" enters
    /// SCN_Game, an E-style interact flips the door, save/load roundtrips the
    /// inventory + player position through the registered ISaveable modules,
    /// and pause freezes the framework clock (GameTime.IsPaused semantics) and
    /// restores on resume.
    ///
    /// Batch discipline: NO wall-clock waits — every wait is a frame loop with
    /// a realtime timeout ceiling so a batchmode hang degrades into a clean
    /// assertion failure instead of a deadlock. Tests drive the chain through
    /// the same seams a human click would hit (menu view intent → presenter →
    /// GameBootstrap → SceneLoader), plus direct EventBus commands where a
    /// real key press is the trigger (Interact/Pause).
    /// </summary>
    public sealed class BootChainSmokeTests
    {
        private const string BootSceneName = GameBootstrap.BootSceneName;
        private const string GameSceneName = GameBootstrap.GameSceneName;

        [TearDown]
        public void TearDown()
        {
            // A failed assert must not poison the next test with a frozen clock
            // or a dangling input block (loading owns its own blocker — leave it).
            if (!SceneLoader.IsLoading)
            {
                Time.timeScale = 1f;
            }
        }

        // ---- case 1: boot chain reaches the main menu ------------------------

        [UnityTest]
        public IEnumerator BootChain_ReachesMainMenu()
        {
            yield return SceneManager.LoadSceneAsync(BootSceneName);

            yield return WaitUntil(
                () => UIManager.Instance != null && UIManager.Instance.IsOnStack<MainMenuView>(),
                "main menu pushed");

            Assert.IsTrue(GameRoot.IsBootstrapped, "GameRoot bootstrap did not run");
            Assert.IsTrue(ZFrameData.IsInitialized, "ZFrameData services missing");
            Assert.IsTrue(UIManager.Instance.TryGetView<MainMenuView>(out MainMenuView menu),
                "main menu view not on the stack");
            Assert.IsNotNull(menu.StartButton, "main menu prefab lost its start button");
        }

        // ---- case 2: menu start enters SCN_Game -------------------------------

        [UnityTest]
        public IEnumerator MenuStart_EntersGameScene()
        {
            yield return EnsureMenuVisible();

            Assert.IsTrue(UIManager.Instance.TryGetView<MainMenuView>(out MainMenuView menu));
            menu.ReplayPrimaryForTests(); // == human click on 开始

            yield return WaitUntil(
                () => string.Equals(SceneManager.GetActiveScene().name, GameSceneName, StringComparison.Ordinal),
                "SCN_Game activated",
                30f);
            yield return WaitUntil(() => GameObject.Find("Player") != null, "player present");

            Assert.IsFalse(UIManager.Instance.IsOnStack<MainMenuView>(), "menu should have been popped");
            Assert.IsNotNull(GameObject.Find("Door"), "door missing in the slice scene");
        }

        // ---- case 3: interact command flips the door --------------------------

        [UnityTest]
        public IEnumerator InteractCommand_FlipsDoor()
        {
            yield return EnsureInGame();

            GameObject door = GameObject.Find("Door");
            Assert.IsNotNull(door, "door missing");
            GameObject.Find("Player").transform.position = new Vector3(0f, 1f, 1.6f); // aim at the door
            yield return null; // scanner re-scans in Update
            yield return null;

            var doorEntity = door.GetComponent<InteractableProxy>().Target as DoorEntity;
            Assert.IsNotNull(doorEntity, "door proxy did not expose a DoorEntity");
            bool before = doorEntity.IsOpen;

            EventBus.Fire(default(InteractInputEvent)); // == human press of E

            Assert.AreEqual(!before, doorEntity.IsOpen, "interact did not flip the door");
        }

        // ---- case 4: save/load roundtrip through the ISaveable modules --------

        [UnityTest]
        public IEnumerator SaveLoad_InventoryAndPositionRoundtrip()
        {
            yield return EnsureInGame();

            InteractionWorld world = InteractionWorld.Shared;
            Transform player = GameObject.Find("Player").transform;

            world.Inventory.Add("gold", 7);
            player.position = new Vector3(1f, 1f, 2f);
            yield return null;

            ISaveSystem save = Services.Get<ISaveSystem>();
            save.SaveToSlot(1); // slot 1: keep the demo's slot 0 clean

            world.Inventory.Clear();
            player.position = new Vector3(5f, 1f, 5f);

            SaveData data = save.LoadFromSlot(1);
            Assert.IsNotNull(data, "slot 1 did not load");

            Assert.AreEqual(7, world.Inventory.Count("gold"), "inventory did not roundtrip");
            Assert.AreEqual(1f, player.position.x, 0.01f, "player position did not roundtrip");
            Assert.AreEqual(2f, player.position.z, 0.01f, "player position did not roundtrip");

            save.DeleteSlot(1);
        }

        // ---- case 5: pause freezes the clock, resume restores ------------------

        [UnityTest]
        public IEnumerator PauseFreezesLogic_AndResumeRestores()
        {
            yield return EnsureInGame();

            EventBus.Fire(default(PauseInputEvent)); // == human press of Esc
            yield return WaitUntil(
                () => UIManager.Instance != null && UIManager.Instance.IsOnStack<PausePanelView>(),
                "pause panel pushed");

            GameTime time = Services.Get<GameTime>();
            Assert.IsTrue(time.IsPaused, "GameTime.IsPaused not set");
            Assert.AreEqual(0f, Time.timeScale, "engine clock not frozen");

            time.Advance(0.016f);
            Assert.AreEqual(0f, time.DeltaTime, "Logic stage dt must be zero while paused (M0-04 semantics)");

            Assert.IsTrue(UIManager.Instance.TryGetView<PausePanelView>(out PausePanelView pause));
            pause.InvokeResumeForTests(); // == human click on 继续

            yield return WaitUntil(
                () => !UIManager.Instance.IsOnStack<PausePanelView>(),
                "pause panel popped");

            Assert.IsFalse(time.IsPaused, "GameTime.IsPaused not restored");
            Assert.AreEqual(1f, Time.timeScale, "engine clock not restored");
        }

        // ---- helpers ------------------------------------------------------------

        private static IEnumerator EnsureMenuVisible()
        {
            if (UIManager.Instance != null && UIManager.Instance.IsOnStack<MainMenuView>())
            {
                yield break;
            }

            yield return SceneManager.LoadSceneAsync(BootSceneName);
            yield return WaitUntil(
                () => UIManager.Instance != null && UIManager.Instance.IsOnStack<MainMenuView>(),
                "main menu pushed");
        }

        /// <summary>Walks the real chain (menu → 开始) into SCN_Game when not already there.</summary>
        private static IEnumerator EnsureInGame()
        {
            yield return EnsureMenuVisible();

            if (string.Equals(SceneManager.GetActiveScene().name, GameSceneName, StringComparison.Ordinal))
            {
                yield break;
            }

            Assert.IsTrue(UIManager.Instance.TryGetView<MainMenuView>(out MainMenuView menu),
                "menu view disappeared before start");
            menu.ReplayPrimaryForTests();

            yield return WaitUntil(
                () => string.Equals(SceneManager.GetActiveScene().name, GameSceneName, StringComparison.Ordinal),
                "SCN_Game activated",
                30f);
            yield return WaitUntil(() => GameObject.Find("Player") != null, "player present");
        }

        private static IEnumerator WaitUntil(Func<bool> done, string what, float timeoutSeconds = 15f)
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
