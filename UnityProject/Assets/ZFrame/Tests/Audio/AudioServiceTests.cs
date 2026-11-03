using NUnit.Framework;
using UnityEngine;
using ZFrame.Core;
using ZFrame.View;

namespace ZFrame.View.Tests
{
    /// <summary>
    /// EditMode tests for the AudioService wiring (TASK-A-M2-02): Services
    /// registration, ITickQueue drive chain, bus volume bookkeeping and
    /// missing-asset robustness. No audio actually plays (EditMode never
    /// runs the audio DSP; clips are absent on purpose).
    /// </summary>
    public class AudioServiceTests
    {
        private GameObject _serviceGo;
        private GameObject _rootGo;

        [SetUp]
        public void SetUp()
        {
            GameRoot.Shutdown();
            GameRoot.Bootstrap();
        }

        [TearDown]
        public void TearDown()
        {
            if (_serviceGo != null)
            {
                Object.DestroyImmediate(_serviceGo); // OnDestroy unregisters from Services + ITickQueue
            }

            if (_rootGo != null)
            {
                Object.DestroyImmediate(_rootGo);
            }

            GameRoot.Shutdown();
        }

        private AudioService CreateService()
        {
            _serviceGo = new GameObject("AudioServiceUnderTest");
            var service = _serviceGo.AddComponent<AudioService>();
            service.Initialize(null);
            return service;
        }

        [Test]
        public void ITickQueue_ResolvesSameInstanceAsConcreteTickQueue()
        {
            Assert.AreSame(Services.Get<TickQueue>(), Services.Get<ITickQueue>(),
                "the ITickQueue registration must expose the boot TickQueue instance");
        }

        [Test]
        public void Initialize_RegistersAsIAudioService()
        {
            var service = CreateService();
            Assert.AreSame(service, Services.Get<IAudioService>(),
                "AudioService must be resolvable via IAudioService after Initialize");
        }

        [Test]
        public void Initialize_RegistersIntoViewTickStage()
        {
            CreateService();
            Assert.AreEqual(1, Services.Get<ITickQueue>().Count(TickStage.View),
                "AudioService must be the (single) View-stage registrant");
        }

        [Test]
        public void Shutdown_UnregistersFromServicesAndLoop()
        {
            var service = CreateService();

            // Deterministic teardown seam (OnDestroy routes here in play mode;
            // EditMode tests drive it directly, like GameRoot.Shutdown).
            service.Shutdown();

            Assert.IsFalse(Services.TryGet(out IAudioService _),
                "Shutdown must unregister IAudioService");
            Assert.AreEqual(0, Services.Get<ITickQueue>().Count(TickStage.View),
                "Shutdown must unregister the service from the game loop");
            Assert.IsFalse(AudioService.Instance == service,
                "Shutdown must clear the static instance");
        }

        [Test]
        public void DrivenThroughTickQueue_CrossfadeAppliesToTrackVolumes()
        {
            var service = CreateService();

            // Fade "to the other track" — works without any clip (StopBgm
            // targets the empty track) and exercises the exact production
            // drive chain: queue tick → AudioService.Tick → track volumes.
            service.StopBgm(1f);
            Assert.AreEqual(1f, FindSource(_serviceGo, "BGM_Track0").volume, 1e-4f,
                "no tick yet: track volumes untouched");

            // TickAll lives on the concrete queue (only the driver calls it);
            // resolving it here is the same path GameLoopDriver uses.
            Services.Get<TickQueue>().TickAll(TickStage.View, 0.5f);
            float expected = Mathf.Cos(Mathf.PI / 4f);
            Assert.AreEqual(expected, FindSource(_serviceGo, "BGM_Track0").volume, 1e-3f,
                "track A must ride the cosine curve down, driven by the queue tick");
            Assert.AreEqual(expected, FindSource(_serviceGo, "BGM_Track1").volume, 1e-3f,
                "track B must ride the sine curve up");
        }

        [Test]
        public void SetBusVolume_RoundtripsWithoutMixer()
        {
            var service = CreateService();

            Assert.AreEqual(1f, service.GetBusVolume(AudioBus.Master), 1e-5f, "buses default to full");
            service.SetBusVolume(AudioBus.Bgm, 0.25f);
            service.SetBusVolume(AudioBus.Sfx, 0f);
            Assert.AreEqual(0.25f, service.GetBusVolume(AudioBus.Bgm), 1e-5f);
            Assert.AreEqual(0f, service.GetBusVolume(AudioBus.Sfx), 1e-5f);
            Assert.AreEqual(1f, service.GetBusVolume(AudioBus.Ui), 1e-5f, "untouched bus unchanged");
        }

        [Test]
        public void SetMixer_Null_SwitchesToMixerlessGracefully()
        {
            var service = CreateService();
            service.SetBusVolume(AudioBus.Bgm, 0.5f); // cached, no mixer yet

            // Detaching the mixer must not throw or lose the cached values;
            // re-attaching a real mixer re-applies them through AudioMath.
            Assert.DoesNotThrow(() => service.SetMixer(null));
            Assert.AreEqual(0.5f, service.GetBusVolume(AudioBus.Bgm), 1e-5f);
            Assert.DoesNotThrow(() => service.SetBusVolume(AudioBus.Bgm, 0.75f));
            Assert.AreEqual(0.75f, service.GetBusVolume(AudioBus.Bgm), 1e-5f);
        }

        [Test]
        public void PlayBgmAndPlaySfx_MissingResources_WarnButDoNotThrow()
        {
            var service = CreateService(); // no IResourceService registered

            Assert.DoesNotThrow(() => service.PlayBgm("nonexistent"));
            Assert.DoesNotThrow(() => service.PlaySfx("nonexistent"));
            Assert.DoesNotThrow(() => service.PlaySfx(null), "null id must be rejected, not thrown");
            Assert.DoesNotThrow(() => service.PlayBgm(""), "empty id must be rejected, not thrown");
        }

        [Test]
        public void GetExposedParamName_MatchesTheFourBuses()
        {
            Assert.AreEqual("MasterVolume", AudioService.GetExposedParamName(AudioBus.Master));
            Assert.AreEqual("BGMVolume", AudioService.GetExposedParamName(AudioBus.Bgm));
            Assert.AreEqual("SFXVolume", AudioService.GetExposedParamName(AudioBus.Sfx));
            Assert.AreEqual("UIVolume", AudioService.GetExposedParamName(AudioBus.Ui));
        }

        private static AudioSource FindSource(GameObject root, string childName)
        {
            var child = root.transform.Find(childName);
            Assert.IsNotNull(child, $"child '{childName}' must exist after Initialize");
            return child.GetComponent<AudioSource>();
        }
    }
}
