using UnityEngine;
using ZFrame.View;

namespace Game.View.Effects
{
    /// <summary>
    /// Production <see cref="IGameplaySparkEmitter"/>: forwards into the pooled
    /// hit spark of <see cref="ParticleRuntime"/> (TASK-A-R9-05) with its default
    /// burst size. Bounded by construction — the runtime pools at most
    /// <c>SparkPoolSize</c> systems and clamps every burst — and inert while
    /// <c>ParticleRuntime.Enabled == false</c>, which is what makes the bindings'
    /// "events keep flowing, pixels stay dark" coordination work with a single
    /// switch and no duplicated state here.
    /// </summary>
    public sealed class ParticleRuntimeSparkEmitter : IGameplaySparkEmitter
    {
        /// <inheritdoc />
        public int Spawn(Vector3 worldPos)
        {
            return ParticleRuntime.SpawnHitSpark(worldPos);
        }
    }
}
