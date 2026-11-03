namespace ZFrame.Core
{
    /// <summary>
    /// Abstraction over the central tick registry (<see cref="TickQueue"/>).
    /// Registered in <see cref="Services"/> by <see cref="GameRoot.Bootstrap"/>
    /// so lower layers (and View services like the audio system) can subscribe
    /// to the loop via <c>Services.Get&lt;ITickQueue&gt;()</c> without coupling
    /// to the concrete class. Same contract as <see cref="TickQueue"/>:
    /// registering while a stage is mid-tick is safe (deferred), unregistration
    /// tombstones in-flight entries. Main thread only.
    /// </summary>
    public interface ITickQueue
    {
        /// <summary>
        /// Registers <paramref name="tickable"/> for <paramref name="stage"/>.
        /// Duplicate registrations of the same instance at the same stage are ignored.
        /// If called during a tick, takes effect after the tick finishes.
        /// </summary>
        void Register(ITickable tickable, TickStage stage);

        /// <summary>Unregisters <paramref name="tickable"/> from every stage.</summary>
        void Unregister(ITickable tickable);

        /// <summary>Number of live tickables at <paramref name="stage"/> (debug/tests).</summary>
        int Count(TickStage stage);
    }
}
