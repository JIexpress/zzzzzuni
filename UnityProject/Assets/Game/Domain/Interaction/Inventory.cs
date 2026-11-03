using System;
using System.Collections.Generic;

namespace Game.Domain.Interaction
{
    /// <summary>
    /// Flat item-count store for the pickup template (TASK-A-M4-01). The slice
    /// only needs counting; slot/weight rules belong to a later inventory
    /// system. Pure data, no events — the entity that mutates it owns the
    /// notification (see <see cref="ItemPickedUp"/>).
    /// </summary>
    public sealed class Inventory
    {
        private readonly Dictionary<string, int> _items = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Adds <paramref name="amount"/> of <paramref name="itemId"/> and returns the new total.</summary>
        public int Add(string itemId, int amount)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                throw new ArgumentException("Inventory: item id must not be empty.", nameof(itemId));
            }

            if (amount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(amount), "Inventory: amount must be positive.");
            }

            _items.TryGetValue(itemId, out int current);
            int total = current + amount;
            _items[itemId] = total;
            return total;
        }

        /// <summary>
        /// Removes <paramref name="amount"/> of <paramref name="itemId"/> —
        /// the rollback primitive behind pickup undo (TASK-A-R9-12). Returns
        /// false (nothing mutated) when the stack cannot cover the amount, so
        /// a revert can never leave a negative or phantom stack. Dropping to
        /// zero removes the key, which lets an undo restore the exact
        /// pre-pickup shape of the inventory (including
        /// <see cref="DistinctCount"/>).
        /// </summary>
        public bool TryRemove(string itemId, int amount)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                throw new ArgumentException("Inventory: item id must not be empty.", nameof(itemId));
            }

            if (amount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(amount), "Inventory: amount must be positive.");
            }

            if (!_items.TryGetValue(itemId, out int current) || current < amount)
            {
                return false;
            }

            if (current == amount)
            {
                _items.Remove(itemId);
            }
            else
            {
                _items[itemId] = current - amount;
            }

            return true;
        }

        /// <summary>Count of one item (0 when never added).</summary>
        public int Count(string itemId)
        {
            return itemId != null && _items.TryGetValue(itemId, out int count) ? count : 0;
        }

        /// <summary>Reads one item count without allocating.</summary>
        public bool TryGetCount(string itemId, out int count)
        {
            if (itemId != null && _items.TryGetValue(itemId, out count))
            {
                return true;
            }

            count = 0;
            return false;
        }

        /// <summary>Sum of all stacks.</summary>
        public int TotalCount
        {
            get
            {
                int total = 0;
                foreach (KeyValuePair<string, int> pair in _items)
                {
                    total += pair.Value;
                }

                return total;
            }
        }

        /// <summary>Number of distinct item ids held.</summary>
        public int DistinctCount => _items.Count;

        /// <summary>Snapshot of all stacks (read-only view over the live dictionary).</summary>
        public IReadOnlyDictionary<string, int> Items => _items;

        /// <summary>Empties the inventory (tests / new game).</summary>
        public void Clear()
        {
            _items.Clear();
        }
    }
}
