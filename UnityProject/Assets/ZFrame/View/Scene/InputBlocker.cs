using System;

namespace ZFrame.View
{
    /// <summary>
    /// Static nested input-blocking counter. Anything that must freeze player
    /// input (scene loading today, modal UI later) brackets its span with
    /// <see cref="Acquire"/>/<see cref="Release"/> (or disposes the returned
    /// token). The counter nests: overlapping acquirers stack and the block
    /// lifts only when the last one releases.
    ///
    /// M4 NOTE (integration point): this component deliberately holds NO
    /// input logic. When the InputSystem bridge lands (TASK A-M4), the input
    /// layer polls <see cref="IsAnyBlocking"/> each frame and suppresses
    /// player/device input while true. Until then this is a pure counter API.
    ///
    /// TASK-A-M3-03: the bridge exists now — <see cref="PlayerInputGate"/>
    /// subscribes to <see cref="Changed"/> and toggles the Player action map,
    /// so the counter stays engine-free and the input layer stays the only
    /// owner of Input System state.
    /// </summary>
    public static class InputBlocker
    {
        private static int _count;

        /// <summary>
        /// Raised after every counter change (Acquire/Release/Reset), including
        /// changes that do not flip <see cref="Active"/> (nested acquires).
        /// Subscribers must be idempotent — the gate simply re-reads
        /// <see cref="Active"/>.
        /// </summary>
        public static event Action Changed;

        /// <summary>True while at least one acquire is outstanding.</summary>
        public static bool Active => _count > 0;

        /// <summary>Nesting depth (0 = not blocking). Exposed for tests and debug overlays.</summary>
        public static int Count => _count;

        /// <summary>Counter form of <see cref="Active"/> — the poll entry for the future InputSystem bridge.</summary>
        public static bool IsAnyBlocking()
        {
            return _count > 0;
        }

        /// <summary>
        /// Enters a blocking span (depth +1). Returns a token whose
        /// <see cref="IDisposable.Dispose"/> releases — preferred over manual
        /// Release in scopes that can throw.
        /// </summary>
        public static IDisposable Acquire()
        {
            _count++;
            RaiseChanged();
            return new BlockToken();
        }

        /// <summary>Leaves a blocking span (depth -1). Over-releasing throws — fail fast on unbalanced brackets.</summary>
        public static void Release()
        {
            if (_count <= 0)
            {
                throw new InvalidOperationException(
                    "InputBlocker: Release() without a matching Acquire() (count already 0).");
            }

            _count--;
            RaiseChanged();
        }

        /// <summary>Forces the depth to zero. For tests and domain-reset recovery only — never call in runtime brackets.</summary>
        public static void Reset()
        {
            _count = 0;
            RaiseChanged();
        }

        private static void RaiseChanged()
        {
            Changed?.Invoke();
        }

        /// <summary>Disposable handle returned by <see cref="Acquire"/>.</summary>
        private sealed class BlockToken : IDisposable
        {
            public void Dispose()
            {
                Release();
            }
        }
    }
}
