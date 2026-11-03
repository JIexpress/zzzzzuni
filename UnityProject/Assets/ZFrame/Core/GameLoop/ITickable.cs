namespace ZFrame.Core
{
    /// <summary>
    /// A component that participates in the central game loop. Implementations
    /// receive <see cref="Tick"/> once per frame at the stage they registered
    /// for on the <see cref="TickQueue"/>. Never implement Update/FixedUpdate
    /// on MonoBehaviours — subscribe to the loop instead.
    /// </summary>
    public interface ITickable
    {
        /// <summary>Called once per frame at the subscribed <see cref="TickStage"/> with the scaled delta time.</summary>
        void Tick(float dt);
    }
}
