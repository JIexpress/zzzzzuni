using UnityEngine;

namespace Game.View.Effects
{
    /// <summary>
    /// Where a gameplay spark goes (TASK-A-R9-06). The bindings adapter knows
    /// WHEN and WHERE a spark is wanted (gameplay events + plane→world mapping);
    /// this interface owns HOW one is drawn. The production implementation is
    /// <see cref="ParticleRuntimeSparkEmitter"/> (ZFrame's pooled
    /// <c>ParticleRuntime.SpawnHitSpark</c>); tests inject a recording fake to
    /// assert call counts and positions without instantiating a single particle.
    ///
    /// The return value is the number of particles actually emitted (0 when the
    /// particle runtime is switched off or self-disabled) so callers can log the
    /// real outcome instead of guessing.
    /// </summary>
    public interface IGameplaySparkEmitter
    {
        /// <summary>Emits one spark burst at <paramref name="worldPos"/>; returns the particles emitted.</summary>
        int Spawn(Vector3 worldPos);
    }
}
