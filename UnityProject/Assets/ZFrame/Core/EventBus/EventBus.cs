using System;
using System.Collections.Generic;

namespace ZFrame.Core
{
    /// <summary>
    /// Strongly-typed static event bus (design doc §3.1).
    ///
    /// Threading: main-thread only. Subscribe/Fire/FireQueued/FlushQueued must be
    /// called from the main thread (the future GameLoopDriver owns FlushQueued at
    /// end of frame). No locking is performed by design.
    /// </summary>
    public static class EventBus
    {
        private static readonly Dictionary<Type, Delegate> _handlers = new Dictionary<Type, Delegate>();

        private static readonly Queue<Action> _queuedDispatches = new Queue<Action>();

        [ThreadStatic]
        private static EventScope _currentScope;

        /// <summary>
        /// Subscribes <paramref name="handler"/> to events of type <typeparamref name="TEvent"/>.
        /// Returns a token whose <see cref="IDisposable.Dispose"/> unsubscribes.
        /// If an <see cref="EventScope"/> opened by <see cref="OpenScope"/> is active,
        /// the token is registered there for bulk disposal.
        /// </summary>
        public static IDisposable Subscribe<TEvent>(Action<TEvent> handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            Type key = typeof(TEvent);
            if (_handlers.TryGetValue(key, out Delegate existing) && existing != null)
            {
                _handlers[key] = Delegate.Combine(existing, handler);
            }
            else
            {
                _handlers[key] = handler;
            }

            var token = new SubscriptionToken(key, handler);
            _currentScope?.Register(token);
            return token;
        }

        /// <summary>Fires an event immediately to all current subscribers of <typeparamref name="TEvent"/>. Zero boxing on the dispatch path.</summary>
        public static void Fire<TEvent>(in TEvent evt)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out Delegate del) || del == null)
            {
                return;
            }

            // Snapshot the multicast delegate before invoking: subscribe/unsubscribe
            // inside a handler must not corrupt the ongoing dispatch.
            var snapshot = (Action<TEvent>)del;
            snapshot(evt);
        }

        /// <summary>
        /// Enqueues an event for deferred dispatch. Nothing is delivered until
        /// <see cref="FlushQueued"/> is called (end of frame by the future GameLoopDriver).
        /// </summary>
        public static void FireQueued<TEvent>(in TEvent evt)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out Delegate del) || del == null)
            {
                return;
            }

            var snapshot = (Action<TEvent>)del;
            TEvent copy = evt; // capture by value; no boxing
            _queuedDispatches.Enqueue(() => snapshot(copy));
        }

        /// <summary>Dispatches all events enqueued since the last flush, in FIFO order.</summary>
        public static void FlushQueued()
        {
            int count = _queuedDispatches.Count;
            for (int i = 0; i < count; i++)
            {
                _queuedDispatches.Dequeue().Invoke();
            }
        }

        /// <summary>
        /// Opens a scope. All subscriptions made while it is active are bulk-disposed
        /// when the scope is disposed — guards against forgotten unsubscribes.
        /// </summary>
        public static EventScope OpenScope()
        {
            return new EventScope(_currentScope);
        }

        /// <summary>Number of live subscribers for <typeparamref name="TEvent"/> (debug/diagnostics).</summary>
        public static int SubscriberCount<TEvent>()
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out Delegate del) || del == null)
            {
                return 0;
            }

            return del.GetInvocationList().Length;
        }

        private static void Unsubscribe(Type key, Delegate handler)
        {
            if (!_handlers.TryGetValue(key, out Delegate del) || del == null)
            {
                return;
            }

            Delegate removed = Delegate.Remove(del, handler);
            if (removed == null)
            {
                _handlers.Remove(key); // last subscriber gone: clean up the entry
            }
            else
            {
                _handlers[key] = removed;
            }
        }

        /// <summary>Unsubscription token returned by <see cref="Subscribe{TEvent}"/>.</summary>
        private readonly struct SubscriptionToken : IDisposable
        {
            private readonly Type _eventType;
            private readonly Delegate _handler;

            public SubscriptionToken(Type eventType, Delegate handler)
            {
                _eventType = eventType;
                _handler = handler;
            }

            public void Dispose()
            {
                Unsubscribe(_eventType, _handler);
            }
        }

        /// <summary>
        /// Bulk-dispose scope opened by <see cref="OpenScope"/>. Disposing it
        /// unsubscribes every subscription made while it was the active scope.
        /// Nested scopes form a stack; inner scopes must be disposed first.
        /// </summary>
        public sealed class EventScope : IDisposable
        {
            private readonly EventScope _parent;
            private List<IDisposable> _tokens;

            internal EventScope(EventScope parent)
            {
                _parent = parent;
                _tokens = new List<IDisposable>();
                _currentScope = this;
            }

            internal void Register(IDisposable token)
            {
                if (_tokens != null)
                {
                    _tokens.Add(token);
                }
            }

            public void Dispose()
            {
                if (_tokens == null)
                {
                    return;
                }

                foreach (IDisposable token in _tokens)
                {
                    token.Dispose();
                }

                _tokens = null;
                if (_currentScope == this)
                {
                    _currentScope = _parent;
                }
            }
        }
    }
}
