using System.Collections;
using Game.Domain.AI;
using Game.Domain.Interaction;
using Game.View.Effects;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using ZFrame.Core;
using ZFrame.Data;
using ZFrame.Gameplay;
using ZFrame.View;

namespace ZFrame.PlayMode.Tests
{
    /// <summary>
    /// TASK-A-R9-14 evidence: the event→audio chain must be falsifiable on a
    /// REALLY PLAYING AudioSource, not on "a handler ran" — the half the
    /// EditMode mock suite (<see cref="Game.View.Tests.GameAudioBindingsTests"/>)
    /// cannot prove (no audio DSP, no imported clips there). Every verdict
    /// drives the REAL production chain: EventBus.Fire →
    /// <see cref="GameAudioBindings"/> → the registered
    /// <see cref="IAudioService"/> (the self-bootstrapped
    /// <see cref="AudioService"/>) → IResourceService (real Resources
    /// provider, real imported clips under Assets/Audio/Resources/) →
    /// AudioSource.Play. EditMode holds the mapping tests (mock); this suite
    /// holds the audible side (the brief's mock/real split).
    ///
    /// Split: 1 BGM test (SceneLoadedEvent → a BGM track isPlaying the library
    /// loop AND its volume rides up through the real game-loop tick — the
    /// crossfade chain), 1 pickup test (the brief's mandatory isPlaying
    /// assertion), 1 grouped test for the remaining mappings (door / hit /
    /// ui_click each ring their own clip; ui_click asserted 2D).
    /// </summary>
    public sealed class GameAudioBindingsPlayModeTests
    {
        private const string BgmClipName = "AUD_bgm_cave";
        private const string PickupClipName = "AUD_sfx_pickup";
        private const string DoorClipName = "AUD_sfx_door";
        private const string HitClipName = "AUD_sfx_hit";
        private const string UiClickClipName = "AUD_sfx_ui_click";

        private GameAudioBindings _bindings;
        private AudioService _ownedAudioService;
        private IResourceService _previousResourceService;
        private bool _registeredResourceService;

        [SetUp]
        public void SetUp()
        {
            // The auto-booted GameAudioBindings host must not leak its
            // subscriptions / active-scene BGM request between tests.
            DestroyAllBindings();

            // TASK-A-R9-14 rework: the auto-booted AudioService is a
            // DontDestroyOnLoad singleton that survives across the three
            // tests. TearDown only silences a shared instance (StopAllSources),
            // never resetting CrossfadeState / SfxLimiter / _bgmKeys, while
            // IResourceService is swapped per test. PlayBgm/StopBgm then pick
            // "incoming = 1 - _crossfade.Target" from a stale fade, so the
            // BGM volume-up and door/hit SFX verdicts drifted (44/43/1).
            // Give EVERY test a pristine, fully registered instance instead:
            // crossfade idle on A, empty limiter, null _bgmKeys, fresh sources.
            // Shutdown() clears Instance + registrations first, so Initialize()
            // registers cleanly (no "another IAudioService" early return).
            if (AudioService.Instance != null)
            {
                var shared = AudioService.Instance;
                shared.Shutdown();
                Object.Destroy(shared.gameObject);
            }
            Services.Unregister<IAudioService>(); // no-op when absent

            var host = new GameObject("~AudioRootPlayModeTests");
            _ownedAudioService = host.AddComponent<AudioService>();
            _ownedAudioService.Initialize(AudioService.FindBootMixer());

            // Deterministic resource side: swap in the production Resources
            // provider (the previous service, if another test registered one,
            // is restored in TearDown).
            if (Services.TryGet(out IResourceService previous))
            {
                _previousResourceService = previous;
                Services.Unregister<IResourceService>();
            }

            Services.Register<IResourceService>(new ResourceService(new ResourcesProvider()));
            _registeredResourceService = true;

            _bindings = GameAudioBindings.EnsureInstance();
            _bindings.Build(); // production resolve → the real AudioService
        }

        [TearDown]
        public void TearDown()
        {
            if (_bindings != null)
            {
                _bindings.Shutdown();
                _bindings = null;
            }

            DestroyAllBindings();

            if (_registeredResourceService)
            {
                Services.Unregister<IResourceService>();
                _registeredResourceService = false;
            }

            if (_previousResourceService != null)
            {
                Services.Register(_previousResourceService);
                _previousResourceService = null;
            }

            if (_ownedAudioService != null)
            {
                // Synchronous Shutdown FIRST (clears AudioService.Instance +
                // registrations), then destroy — a bare Destroy would leave the
                // dying service discoverable by the next test's SetUp until the
                // end of frame.
                _ownedAudioService.Shutdown();
                Object.Destroy(_ownedAudioService.gameObject);
                _ownedAudioService = null;
            }
            else
            {
                StopAllSources(); // shared auto-booted service: silence, don't kill
            }
        }

        [UnityTest]
        public IEnumerator SceneLoadedEvent_StartsAReallyPlayingBgmTrack_VolumeRidesTheLoopTick()
        {
            yield return null; // let the runner scene settle

            EventBus.Fire(new SceneLoadedEvent("SCN_Boot")); // the real production trigger

            AudioSource bgm = FindPlayingBgmTrack();
            Assert.IsNotNull(bgm,
                "a mapped SceneLoadedEvent must leave one BGM track playing — a wired-but-silent " +
                "adapter (dropped event, missing clip, dead source) is exactly what this fails on");
            Assert.IsTrue(bgm.isPlaying, "the BGM track must report isPlaying (真断言: the track is audible-scheduled)");
            Assert.AreEqual(BgmClipName, bgm.clip.name,
                $"the playing track must carry the library loop '{BgmClipName}' (real Resources load)");
            Assert.IsTrue(bgm.loop, "BGM tracks must loop");

            // The crossfade rides up through the REAL drive chain:
            // TickQueue.TickAll(View) → AudioService.Tick → track volumes.
            TickQueue queue = Services.Get<TickQueue>();
            for (int i = 0; i < 10; i++)
            {
                queue.TickAll(TickStage.View, 0.1f);
            }

            Assert.Greater(bgm.volume, 0.5f,
                "after 1 s of loop ticks the crossfade must have faded the track up (volume > 0.5) — " +
                "an AudioService that never ticks would leave it at 0");
        }

        [UnityTest]
        public IEnumerator ItemPickedUp_PlaysAReallyPlayingSfxSource_AtTheItemPosition()
        {
            yield return null;

            // The REAL event path (exactly what a pickup fires):
            EventBus.Fire(new ItemPickedUp(ActorId.Player, "gold", 1, 1, new Vec2(3f, 4f)));
            yield return null;

            AudioSource sfx = FindPlayingSfxSource(PickupClipName);
            Assert.IsNotNull(sfx,
                "the pickup event must leave a pooled SFX source REALLY playing the pickup clip — " +
                "the mock suite proves the mapping, this proves the sound");
            Assert.IsTrue(sfx.isPlaying,
                "isPlaying must be true on the real AudioSource (the assertion the brief demands; " +
                "no mock can substitute it)");
            Assert.AreEqual(PickupClipName, sfx.clip.name,
                "the playing source must carry the imported pickup clip (real asset load)");
            Assert.AreEqual(1f, sfx.spatialBlend, 0.01f,
                "a positioned pickup must play 3D");
            Assert.AreEqual(new Vector3(3f, GameAudioBindings.DefaultSfxHeight, 4f), sfx.transform.position,
                "the source must sit at the item's world position (plane X→X, Y→Z, shared height)");
        }

        [UnityTest]
        public IEnumerator DoorHitAndUiClickEvents_EachRingTheirMappedClip()
        {
            yield return null;

            EventBus.Fire(new DoorStateChanged("door-1", true, new Vec2(-2f, 6f)));
            yield return null;
            AudioSource door = FindPlayingSfxSource(DoorClipName);
            Assert.IsNotNull(door, "a door toggle must really play the door clip");
            Assert.IsTrue(door.isPlaying, "the door source must report isPlaying");

            EventBus.Fire(new EnemyAttackRequested("goblin", new Vec2(100f, 100f), new Vec2(7f, 9f)));
            yield return null;
            AudioSource hit = FindPlayingSfxSource(HitClipName);
            Assert.IsNotNull(hit, "an attack event must really play the hit clip");
            Assert.IsTrue(hit.isPlaying, "the hit source must report isPlaying");

            EventBus.Fire(new PanelOpenedEvent { PrefabKey = "ui_main_menu" });
            yield return null;
            AudioSource click = FindPlayingSfxSource(UiClickClipName);
            Assert.IsNotNull(click, "a panel open must really play the ui_click clip");
            Assert.IsTrue(click.isPlaying, "the ui_click source must report isPlaying");
            Assert.AreEqual(0f, click.spatialBlend, 0.01f,
                "a UI click is not a world event — it must play 2D");
        }

        // ---- helpers -----------------------------------------------------------

        private static AudioSource FindPlayingBgmTrack()
        {
            AudioService service = AudioService.Instance;
            if (service == null)
            {
                return null;
            }

            for (int i = 0; i < AudioService.BgmTrackCount; i++)
            {
                Transform child = service.transform.Find($"BGM_Track{i}");
                if (child == null)
                {
                    continue;
                }

                var source = child.GetComponent<AudioSource>();
                if (source != null && source.clip != null && source.isPlaying)
                {
                    return source;
                }
            }

            return null;
        }

        private static AudioSource FindPlayingSfxSource(string clipName)
        {
            AudioService service = AudioService.Instance;
            if (service == null)
            {
                return null;
            }

            for (int i = 0; i < AudioService.SfxPoolSize; i++)
            {
                Transform child = service.transform.Find($"SFX_{i:D2}");
                if (child == null)
                {
                    continue;
                }

                var source = child.GetComponent<AudioSource>();
                if (source != null && source.clip != null && source.isPlaying
                    && source.clip.name == clipName)
                {
                    return source;
                }
            }

            return null;
        }

        private static void DestroyAllBindings()
        {
            foreach (GameAudioBindings bindings in Object.FindObjectsOfType<GameAudioBindings>())
            {
                bindings.Shutdown();
                Object.Destroy(bindings.gameObject);
            }
        }

        /// <summary>
        /// Synchronous silence for a SHARED AudioService (no fade, no tick
        /// dependency) so one test's music never leaks into the next.
        /// </summary>
        private static void StopAllSources()
        {
            AudioService service = AudioService.Instance;
            if (service == null)
            {
                return;
            }

            for (int i = 0; i < AudioService.BgmTrackCount; i++)
            {
                Transform track = service.transform.Find($"BGM_Track{i}");
                if (track != null)
                {
                    var source = track.GetComponent<AudioSource>();
                    if (source != null)
                    {
                        source.Stop();
                        source.clip = null;
                    }
                }
            }

            for (int i = 0; i < AudioService.SfxPoolSize; i++)
            {
                Transform slot = service.transform.Find($"SFX_{i:D2}");
                if (slot != null)
                {
                    var source = slot.GetComponent<AudioSource>();
                    if (source != null)
                    {
                        source.Stop();
                        source.clip = null;
                    }
                }
            }
        }
    }
}
