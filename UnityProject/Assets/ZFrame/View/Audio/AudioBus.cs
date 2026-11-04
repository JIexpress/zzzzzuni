namespace ZFrame.View
{
    /// <summary>
    /// The four mixing buses of the audio system (方案 §6.6). Each maps to one
    /// AudioMixer group under Master (BGM/SFX/UI children of Master) and one
    /// exposed Volume parameter — see Assets/Audio/Resources/MIX_AudioMixer.asset
    /// (moved under Resources in TASK-A-R9-14 so the runtime lookup resolves)
    /// and its generator ZFrame.View.AudioMixerSetup.
    /// </summary>
    public enum AudioBus
    {
        /// <summary>Root bus; everything downstream routes through it.</summary>
        Master = 0,

        /// <summary>Music loop bus (two-track crossfade source).</summary>
        Bgm = 1,

        /// <summary>One-shot sound effects bus (pooled sources).</summary>
        Sfx = 2,

        /// <summary>UI feedback bus (pooled sources).</summary>
        Ui = 3,
    }
}
