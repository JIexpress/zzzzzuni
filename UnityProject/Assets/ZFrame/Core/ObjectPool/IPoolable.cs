namespace ZFrame.Core
{
    /// <summary>Lifecycle hooks for pooled objects, called by <see cref="ComponentPool{T}"/>.</summary>
    public interface IPoolable
    {
        /// <summary>Called after the instance is taken from the pool and activated.</summary>
        void OnSpawn();

        /// <summary>Called before the instance is deactivated and returned to the pool.</summary>
        void OnDespawn();
    }
}
