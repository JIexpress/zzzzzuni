using System.Collections.Generic;
using ZFrame.Core;

namespace Game.Domain.Interaction
{
    /// <summary>
    /// Book-keeping for every live interactable plus the single "nearest"
    /// target the player is currently aiming at (TASK-A-M4-01).
    ///
    /// The View owns the aiming geometry (raycast) and pushes the winner in
    /// via <see cref="SetNearest"/>; the Domain owns identity, eligibility and
    /// activation. Every change of nearest target fires
    /// <see cref="InteractionTargetChanged"/> on the <see cref="EventBus"/> so
    /// any number of View listeners (prompt HUD, audio, tutorial) can react
    /// without the Domain knowing they exist.
    ///
    /// Not thread-safe (main thread only, like the rest of the framework).
    /// </summary>
    public sealed class InteractionRegistry
    {
        private readonly List<IInteractable> _registered = new List<IInteractable>();
        private IInteractable _nearest;

        /// <summary>Every registered interactable, in registration order.</summary>
        public IReadOnlyList<IInteractable> Registered => _registered;

        /// <summary>Number of registered interactables.</summary>
        public int Count => _registered.Count;

        /// <summary>Current aim target, or null when nothing is in range.</summary>
        public IInteractable Nearest => _nearest;

        /// <summary>Prompt data for <see cref="Nearest"/> (<see cref="InteractInfo.None"/> when null).</summary>
        public InteractInfo NearestInfo => _nearest != null ? _nearest.Info : InteractInfo.None;

        /// <summary>
        /// Registers an interactable. Returns false for null or an already
        /// registered instance (registration is idempotent by instance).
        /// </summary>
        public bool Register(IInteractable target)
        {
            if (target == null || _registered.Contains(target))
            {
                return false;
            }

            _registered.Add(target);
            return true;
        }

        /// <summary>
        /// Unregisters an interactable. If it was the current nearest target
        /// the nearest is cleared (and the change event fires), so a destroyed
        /// prop can never leave a dangling prompt.
        /// </summary>
        public bool Unregister(IInteractable target)
        {
            if (target == null || !_registered.Remove(target))
            {
                return false;
            }

            if (ReferenceEquals(_nearest, target))
            {
                ClearNearest();
            }

            return true;
        }

        /// <summary>True when the exact instance is registered.</summary>
        public bool Contains(IInteractable target)
        {
            return target != null && _registered.Contains(target);
        }

        /// <summary>Drops every registration and the current target.</summary>
        public void Clear()
        {
            _registered.Clear();
            ClearNearest();
        }

        /// <summary>
        /// Sets the aim target. Passing null is equivalent to
        /// <see cref="ClearNearest"/>. Unregistered instances are rejected
        /// (return false, state unchanged) — the View must register first.
        /// Fires <see cref="InteractionTargetChanged"/> only on an actual change.
        /// </summary>
        public bool SetNearest(IInteractable target)
        {
            if (target == null)
            {
                ClearNearest();
                return false;
            }

            if (!_registered.Contains(target) || ReferenceEquals(_nearest, target))
            {
                return false;
            }

            _nearest = target;
            EventBus.Fire(new InteractionTargetChanged(true, target.Info));
            return true;
        }

        /// <summary>Clears the aim target, firing <see cref="InteractionTargetChanged"/> when one was set.</summary>
        public void ClearNearest()
        {
            if (_nearest == null)
            {
                return;
            }

            _nearest = null;
            EventBus.Fire(InteractionTargetChanged.Cleared);
        }

        /// <summary>
        /// Activates the current nearest target for <paramref name="actor"/>.
        /// Returns false when there is no target or it refuses the interaction
        /// (locked door, consumed pickup, finished dialog).
        /// </summary>
        public bool InteractNearest(ActorId actor)
        {
            IInteractable target = _nearest;
            if (target == null || !target.CanInteract(actor))
            {
                return false;
            }

            target.Interact(actor);
            return true;
        }

        /// <summary>
        /// Pure selection rule used by the View scanner: closest candidate
        /// within <paramref name="maxDistance"/> wins; ties keep the first
        /// (i.e. the earliest hit). Null targets and out-of-range distances
        /// are skipped. Returns null when nothing qualifies.
        /// </summary>
        public static IInteractable PickNearest(IReadOnlyList<InteractionCandidate> candidates, float maxDistance)
        {
            if (candidates == null)
            {
                return null;
            }

            IInteractable best = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                InteractionCandidate candidate = candidates[i];
                if (candidate.Target == null || candidate.Distance < 0f || candidate.Distance > maxDistance)
                {
                    continue;
                }

                if (best == null || candidate.Distance < bestDistance)
                {
                    best = candidate.Target;
                    bestDistance = candidate.Distance;
                }
            }

            return best;
        }
    }
}
