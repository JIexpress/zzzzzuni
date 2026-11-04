using System.Collections.Generic;
using Game.Domain.AI;
using Game.Domain.Interaction;
using Game.View.Effects;
using NUnit.Framework;
using UnityEngine;
using ZFrame.Core;
using ZFrame.Gameplay;
using ZFrame.View;

namespace Game.View.Tests
{
    /// <summary>
    /// TASK-A-R9-14: the event→audio adapter's unit contract, entirely off-screen
    /// (EditMode, real EventBus, recording IAudioService mock). Covered here:
    ///  - the production service resolve (Build(null) must find the registered
    ///    IAudioService, exactly what play mode runs);
    ///  - subscribe/teardown through one EventScope (exactly one live
    ///    subscription per source; Shutdown unsubscribes; rebuild re-subscribes);
    ///  - the event→sfx mapping: pickup/door/hit carry the world position of
    ///    the gameplay plane (2D→X/Z, shared height convention), ui_click is
    ///    2D; SceneLoadedEvent drives BGM through the per-scene map (and fades
    ///    out on unmapped scenes).
    /// The audible side (clip loads, AudioSource.isPlaying) is NOT assertable
    /// in EditMode (no audio DSP, no clip import) — that half lives in the
    /// PlayMode evidence suite (GameAudioBindingsPlayModeTests): pickup → a
    /// really playing SFX source, SceneLoadedEvent → a really playing BGM track.
    /// </summary>
    public sealed class GameAudioBindingsTests
    {
        private RecordingAudioService _audio;
        private GameAudioBindings _bindings;
        private GameObject _host;

        [SetUp]
        public void SetUp()
        {
            _audio = new RecordingAudioService();
            Services.Register<IAudioService>(_audio);

            _host = new GameObject("~GameAudioBindingsTests");
            _bindings = _host.AddComponent<GameAudioBindings>();
            _bindings.Build(); // null → resolves the registered production service
            _audio.Clear();    // drop whatever the Build-time active-scene mapping asked for
        }

        [TearDown]
        public void TearDown()
        {
            _bindings.Shutdown();
            if (_host != null)
            {
                UnityEngine.Object.DestroyImmediate(_host);
                _host = null;
            }

            _bindings = null;
            Services.Unregister<IAudioService>();
            _audio = null;
        }

        // ---- lifecycle (EventScope + resolve) --------------------------------

        [Test]
        public void Build_ResolvesTheRegisteredServiceAsProductionWould()
        {
            EventBus.Fire(new ItemPickedUp(ActorId.Player, "gold", 1, 1, new Vec2(1f, 1f)));

            Assert.AreEqual(1, _audio.SfxPlays.Count,
                "Build(null) must resolve the registered IAudioService — the play-mode production path");
            Assert.AreEqual("pickup", _audio.SfxPlays[0].Id);
        }

        [Test]
        public void Build_SubscribesExactlyOncePerSource()
        {
            // Snapshot includes SetUp's own instance; a second instance adds
            // exactly one subscription per source...
            int scenesBefore = EventBus.SubscriberCount<SceneLoadedEvent>();
            int pickupsBefore = EventBus.SubscriberCount<ItemPickedUp>();
            int doorsBefore = EventBus.SubscriberCount<DoorStateChanged>();
            int attacksBefore = EventBus.SubscriberCount<EnemyAttackRequested>();
            int panelsBefore = EventBus.SubscriberCount<PanelOpenedEvent>();

            var host = new GameObject("~GameAudioBindingsTestsSecond");
            GameAudioBindings second = host.AddComponent<GameAudioBindings>();
            second.Build();
            try
            {
                Assert.AreEqual(scenesBefore + 1, EventBus.SubscriberCount<SceneLoadedEvent>(),
                    "Build must subscribe SceneLoadedEvent exactly once");
                Assert.AreEqual(pickupsBefore + 1, EventBus.SubscriberCount<ItemPickedUp>(),
                    "Build must subscribe ItemPickedUp exactly once");
                Assert.AreEqual(doorsBefore + 1, EventBus.SubscriberCount<DoorStateChanged>(),
                    "Build must subscribe DoorStateChanged exactly once");
                Assert.AreEqual(attacksBefore + 1, EventBus.SubscriberCount<EnemyAttackRequested>(),
                    "Build must subscribe EnemyAttackRequested exactly once");
                Assert.AreEqual(panelsBefore + 1, EventBus.SubscriberCount<PanelOpenedEvent>(),
                    "Build must subscribe PanelOpenedEvent exactly once");

                // ...and a redundant Build must be a no-op, not a double subscription.
                second.Build();
                Assert.AreEqual(scenesBefore + 1, EventBus.SubscriberCount<SceneLoadedEvent>(),
                    "a second Build call must not double the subscriptions");
                Assert.AreEqual(pickupsBefore + 1, EventBus.SubscriberCount<ItemPickedUp>(),
                    "a second Build call must not double the subscriptions");
                Assert.AreEqual(doorsBefore + 1, EventBus.SubscriberCount<DoorStateChanged>(),
                    "a second Build call must not double the subscriptions");
                Assert.AreEqual(attacksBefore + 1, EventBus.SubscriberCount<EnemyAttackRequested>(),
                    "a second Build call must not double the subscriptions");
                Assert.AreEqual(panelsBefore + 1, EventBus.SubscriberCount<PanelOpenedEvent>(),
                    "a second Build call must not double the subscriptions");
            }
            finally
            {
                second.Shutdown();
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Shutdown_UnsubscribesAllSources()
        {
            int scenes = EventBus.SubscriberCount<SceneLoadedEvent>();
            int pickups = EventBus.SubscriberCount<ItemPickedUp>();
            int doors = EventBus.SubscriberCount<DoorStateChanged>();
            int attacks = EventBus.SubscriberCount<EnemyAttackRequested>();
            int panels = EventBus.SubscriberCount<PanelOpenedEvent>();

            _bindings.Shutdown();

            Assert.AreEqual(scenes - 1, EventBus.SubscriberCount<SceneLoadedEvent>(),
                "Shutdown must unsubscribe the scene source");
            Assert.AreEqual(pickups - 1, EventBus.SubscriberCount<ItemPickedUp>(),
                "Shutdown must unsubscribe the pickup source");
            Assert.AreEqual(doors - 1, EventBus.SubscriberCount<DoorStateChanged>(),
                "Shutdown must unsubscribe the door source");
            Assert.AreEqual(attacks - 1, EventBus.SubscriberCount<EnemyAttackRequested>(),
                "Shutdown must unsubscribe the attack source");
            Assert.AreEqual(panels - 1, EventBus.SubscriberCount<PanelOpenedEvent>(),
                "Shutdown must unsubscribe the panel source");

            // Rebuild must re-subscribe (the binding is reusable after a teardown).
            _bindings.Build();
            Assert.AreEqual(scenes, EventBus.SubscriberCount<SceneLoadedEvent>(),
                "Build after Shutdown must restore the scene subscription");
            Assert.AreEqual(pickups, EventBus.SubscriberCount<ItemPickedUp>(),
                "Build after Shutdown must restore the pickup subscription");
        }

        [Test]
        public void WithoutService_BuildStaysPassiveAndNeverThrows()
        {
            Services.Unregister<IAudioService>();
            var host = new GameObject("~GameAudioBindingsTestsPassive");
            GameAudioBindings passive = host.AddComponent<GameAudioBindings>();
            try
            {
                passive.Build(); // resolve finds nothing → passive
                Assert.DoesNotThrow(() =>
                {
                    EventBus.Fire(new ItemPickedUp(ActorId.Player, "gold", 1, 1, new Vec2(1f, 2f)));
                    EventBus.Fire(new DoorStateChanged("door-1", true));
                    EventBus.Fire(new EnemyAttackRequested("enemy-1", new Vec2(0f, 0f), new Vec2(1f, 1f)));
                    EventBus.Fire(new PanelOpenedEvent { PrefabKey = "ui_test" });
                    EventBus.Fire(new SceneLoadedEvent("SCN_Game"));
                }, "a missing audio service must degrade to a no-op, never throw");

                Assert.Greater(passive.EventsReceived, 0, "events are still counted while passive");
                Assert.AreEqual(0, passive.AudioRequests,
                    "a passive binding must forward nothing to the (absent) service");
            }
            finally
            {
                passive.Shutdown();
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        // ---- event→sfx mapping -----------------------------------------------

        [Test]
        public void ItemPickedUp_PlaysPickupSfxAtTheItemPosition()
        {
            EventBus.Fire(new ItemPickedUp(ActorId.Player, "gold", 1, 1, new Vec2(3f, 4f)));

            Assert.AreEqual(1, _audio.SfxPlays.Count, "one pickup event must request exactly one sfx");
            Assert.AreEqual("pickup", _audio.SfxPlays[0].Id, "pickup must map to the sfx id 'pickup'");
            Assert.AreEqual(new Vector3(3f, GameAudioBindings.DefaultSfxHeight, 4f), _audio.SfxPlays[0].Position,
                "the pickup sound must carry the item's world position (plane X→X, Y→Z, shared height)");
        }

        [Test]
        public void DoorStateChanged_PlaysDoorSfxOnOpenAndCloseAlike()
        {
            EventBus.Fire(new DoorStateChanged("door-1", true, new Vec2(-2f, 6f)));
            EventBus.Fire(new DoorStateChanged("door-1", false, new Vec2(-2f, 6f)));

            Assert.AreEqual(2, _audio.SfxPlays.Count, "open AND close are both real door motions");
            Assert.AreEqual("door", _audio.SfxPlays[0].Id, "door must map to the sfx id 'door'");
            Assert.AreEqual("door", _audio.SfxPlays[1].Id, "door must map to the sfx id 'door'");
            Assert.AreEqual(new Vector3(-2f, GameAudioBindings.DefaultSfxHeight, 6f), _audio.SfxPlays[0].Position,
                "the door sound must carry the door's world position");
        }

        [Test]
        public void EnemyAttackRequested_PlaysHitSfxAtTheTargetPosition()
        {
            // The hit marks where the swing LANDED (PlayerPosition) — the same
            // convention as the GameplayFxBindings attack spark.
            EventBus.Fire(new EnemyAttackRequested("goblin", new Vec2(100f, 100f), new Vec2(7f, 9f)));

            Assert.AreEqual(1, _audio.SfxPlays.Count, "one attack event must request exactly one sfx");
            Assert.AreEqual("hit", _audio.SfxPlays[0].Id, "attack must map to the sfx id 'hit'");
            Assert.AreEqual(new Vector3(7f, GameAudioBindings.DefaultSfxHeight, 9f), _audio.SfxPlays[0].Position,
                "the hit sound must be at the TARGET (PlayerPosition), not the attacker");
        }

        [Test]
        public void PanelOpened_PlaysUiClickSfx_2D()
        {
            EventBus.Fire(new PanelOpenedEvent { PrefabKey = "ui_main_menu" });

            Assert.AreEqual(1, _audio.SfxPlays.Count, "one panel open must request exactly one sfx");
            Assert.AreEqual("ui_click", _audio.SfxPlays[0].Id, "panel open must map to the sfx id 'ui_click'");
            Assert.IsFalse(_audio.SfxPlays[0].Position.HasValue,
                "a UI click is not a world event — it must play 2D (no position)");
        }

        // ---- event→bgm mapping -----------------------------------------------

        [Test]
        public void BgmIdForScene_MapsMenuAndGameToTheLibraryTrack()
        {
            Assert.AreEqual("cave", GameAudioBindings.BgmIdForScene("SCN_Boot"),
                "the main menu (boot scene) must start the menu track");
            Assert.AreEqual("cave", GameAudioBindings.BgmIdForScene("SCN_Game"),
                "the game scene must start the game track");
            Assert.IsNull(GameAudioBindings.BgmIdForScene("SCN_Empty"),
                "an unmapped scene must fade the music out, not keep the last scene's track");
        }

        [Test]
        public void SceneLoadedEvent_StartsBgmOnMappedScenes_AndFadesOutOnUnmapped()
        {
            EventBus.Fire(new SceneLoadedEvent("SCN_Game"));
            Assert.AreEqual(1, _audio.BgmPlays.Count, "a mapped scene must start (crossfade to) the mapped track");
            Assert.AreEqual("cave", _audio.BgmPlays[0], "SCN_Game must map to the 'cave' track");
            Assert.AreEqual(0, _audio.StopBgmCalls, "a mapped scene must not stop the music");

            EventBus.Fire(new SceneLoadedEvent("some_unmapped_scene"));
            Assert.AreEqual(1, _audio.StopBgmCalls,
                "an unmapped scene must fade the BGM out (unknown territory stays silent)");
            Assert.AreEqual(1, _audio.BgmPlays.Count, "an unmapped scene must not start anything");
        }

        /// <summary>Mock seam: records every audio request instead of playing anything.</summary>
        private sealed class RecordingAudioService : IAudioService
        {
            public readonly List<string> BgmPlays = new List<string>();
            public readonly List<SfxCall> SfxPlays = new List<SfxCall>();
            public int StopBgmCalls;

            public void PlayBgm(string id, float fade = 0.5f)
            {
                BgmPlays.Add(id);
            }

            public void StopBgm(float fade = 0.5f)
            {
                StopBgmCalls++;
            }

            public void PlaySfx(string id, Vector3? worldPos = null)
            {
                SfxPlays.Add(new SfxCall(id, worldPos));
            }

            public void SetBusVolume(AudioBus bus, float volume01)
            {
                // Not exercised by the bindings; volume routing is AudioServiceTests' scope.
            }

            public float GetBusVolume(AudioBus bus)
            {
                return 1f;
            }

            /// <summary>Drops the Build-time active-scene request so each test starts from zero.</summary>
            public void Clear()
            {
                BgmPlays.Clear();
                SfxPlays.Clear();
                StopBgmCalls = 0;
            }
        }

        /// <summary>One recorded PlaySfx call (test mock record type).</summary>
        private readonly struct SfxCall
        {
            public readonly string Id;
            public readonly Vector3? Position;

            public SfxCall(string id, Vector3? position)
            {
                Id = id;
                Position = position;
            }
        }
    }
}
