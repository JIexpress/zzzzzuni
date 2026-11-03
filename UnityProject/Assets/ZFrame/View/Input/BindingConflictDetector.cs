using System;
using System.Collections.Generic;

namespace ZFrame.View
{
    /// <summary>
    /// Binding-conflict rule of TASK-A-M3-03, kept as a PURE function over
    /// <see cref="BindingSnapshot"/> tables: this file references no
    /// UnityEngine type at all (the Input System projection lives in the
    /// partial companion <c>BindingConflictDetector.CaptureMap.cs</c>), so the
    /// rule is unit-testable without instantiating a single Unity object.
    ///
    /// CONFLICT RULE (the contract the rebind flow and the tests share):
    /// 1. Two bindings CONFLICT when their EFFECTIVE paths
    ///    (<c>overridePath ?? path</c>) are equal (ordinal, case-sensitive —
    ///    Input System paths are lower-case by convention, e.g.
    ///    <c>&lt;Keyboard&gt;/e</c>).
    /// 2. Comparison is scoped to ONE action map: the caller passes the
    ///    snapshots of a single map (Player and UI may reuse the same key
    ///    without conflict — they are gated independently).
    /// 3. COMPOSITE HEADERS NEVER CONFLICT: a header binding ("2DVector" and
    ///    friends) carries no device path — <see cref="BindingSnapshot.IsComposite"/>
    ///    rows are skipped. The header's PARTS (up/down/left/right, flagged
    ///    <see cref="BindingSnapshot.IsCompositePart"/>) DO participate: rebinding
    ///    the W of WASD must be caught if another binding already uses W.
    /// 4. The binding being rebound excludes ITSELF
    ///    (<paramref name="excludeActionKey"/> + <paramref name="excludeBindingIndex"/>);
    ///    rebinding a key to the value it already has is not a conflict.
    /// 5. Bindings without an effective path (unbound slots) never conflict.
    /// </summary>
    public static partial class BindingConflictDetector
    {
        /// <summary>
        /// Returns the first snapshot of <paramref name="map"/> whose effective
        /// path equals <paramref name="newPath"/>, or null when the path is
        /// free. Pure: no engine objects, no side effects, stable order (the
        /// map's own binding order), so callers can show a deterministic
        /// "已被 X 使用" message.
        /// </summary>
        /// <param name="map">Snapshots of ONE action map (see <c>CaptureMap</c>); null/empty = no conflict.</param>
        /// <param name="newPath">Effective path the rebind captured, e.g. <c>&lt;Keyboard&gt;/f</c>.</param>
        /// <param name="excludeActionKey">Action key of the binding being rebound ("Player/Interact"); null = exclude nothing.</param>
        /// <param name="excludeBindingIndex">Binding index inside <paramref name="excludeActionKey"/>; ignored when the key is null.</param>
        public static BindingSnapshot FindConflict(
            IReadOnlyList<BindingSnapshot> map,
            string newPath,
            string excludeActionKey,
            int excludeBindingIndex)
        {
            if (map == null || map.Count == 0 || string.IsNullOrEmpty(newPath))
            {
                return null;
            }

            for (int i = 0; i < map.Count; i++)
            {
                BindingSnapshot candidate = map[i];
                if (candidate == null || !IsConflictRelevant(candidate))
                {
                    continue;
                }

                if (IsExcluded(candidate, excludeActionKey, excludeBindingIndex))
                {
                    continue;
                }

                if (string.Equals(candidate.EffectivePath, newPath, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// True when a snapshot can take part in conflict detection at all:
        /// it must carry an effective device path and must not be a composite
        /// HEADER (rule 3 + rule 5 of the class doc). Exposed so callers/tests
        /// can explain why a row is ignored.
        /// </summary>
        public static bool IsConflictRelevant(BindingSnapshot snapshot)
        {
            return snapshot != null
                && !snapshot.IsComposite
                && !string.IsNullOrEmpty(snapshot.EffectivePath);
        }

        /// <summary>True when <paramref name="snapshot"/> is the binding being rebound (rule 4).</summary>
        public static bool IsExcluded(BindingSnapshot snapshot, string excludeActionKey, int excludeBindingIndex)
        {
            return snapshot != null
                && !string.IsNullOrEmpty(excludeActionKey)
                && snapshot.BindingIndex == excludeBindingIndex
                && string.Equals(snapshot.ActionKey, excludeActionKey, StringComparison.Ordinal);
        }
    }
}
