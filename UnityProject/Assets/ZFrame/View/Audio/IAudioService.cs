using UnityEngine;

namespace ZFrame.View
{
    /// <summary>
    /// Audio front door registered in <see cref="ZFrame.Core.Services"/>
    /// (resolve via <c>Services.Get&lt;IAudioService&gt;()</c>). Implemented by
    /// the <see cref="AudioService"/> MonoBehaviour adapter.
    ///
    /// Volume conventions: <paramref name="volume01"/> is the normalized
    /// 0..1 game volume (0 = mute); conversion to mixer decibels goes through
    /// <see cref="AudioMath"/>. Missing audio assets log a warning and are
    /// never fatal. Resource keys follow the bgm_/sfx_ prefix convention.
    /// </summary>
    public interface IAudioService
    {
        /// <summary>
        /// Starts (or switches to) a music loop on the BGM bus, crossfading
        /// over <paramref name="fade"/> seconds from the currently playing
        /// track. Missing clip → LogWarn, current music keeps playing.
        /// </summary>
        void PlayBgm(string id, float fade = 0.5f);

        /// <summary>
        /// Fades the BGM bus to silence over <paramref name="fade"/> seconds.
        /// </summary>
        void StopBgm(float fade = 0.5f);

        /// <summary>
        /// Plays a one-shot on the SFX bus from the pooled sources. With
        /// <paramref name="worldPos"/> the source plays 3D at that position,
        /// otherwise 2D. Same-id same-frame and per-frame budget are limited
        /// by the internal SfxLimiter (rejected plays are silently dropped).
        /// </summary>
        void PlaySfx(string id, Vector3? worldPos = null);

        /// <summary>Sets a bus volume (0 = mute, 1 = full) via the mixer.</summary>
        void SetBusVolume(AudioBus bus, float volume01);

        /// <summary>Returns the last value set for a bus (0..1).</summary>
        float GetBusVolume(AudioBus bus);
    }
}
