using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;

namespace ZFrame.View
{
    /// <summary>
    /// Input System projection half of <see cref="BindingConflictDetector"/>
    /// (TASK-A-M3-03): the ONLY place where the conflict rule touches the
    /// engine. It walks a live <see cref="InputActionMap"/> and flattens every
    /// binding into a <see cref="BindingSnapshot"/> row, taking the EFFECTIVE
    /// path (<c>overridePath ?? path</c>) so a persisted rebind is what the
    /// conflict check sees — not the asset default.
    /// </summary>
    public static partial class BindingConflictDetector
    {
        /// <summary>
        /// Snapshots every binding of <paramref name="map"/> in the map's own
        /// order (action order, then binding index). Composite headers are
        /// included as rows (flagged <see cref="BindingSnapshot.IsComposite"/>)
        /// so the UI can render them, but the conflict rule skips them.
        /// </summary>
        public static IReadOnlyList<BindingSnapshot> CaptureMap(InputActionMap map)
        {
            if (map == null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            var rows = new List<BindingSnapshot>();
            ReadOnlyArray<InputAction> actions = map.actions;
            for (int a = 0; a < actions.Count; a++)
            {
                InputAction action = actions[a];
                string actionKey = map.name + "/" + action.name;
                ReadOnlyArray<InputBinding> bindings = action.bindings;
                for (int b = 0; b < bindings.Count; b++)
                {
                    InputBinding binding = bindings[b];
                    string effectivePath = string.IsNullOrEmpty(binding.overridePath)
                        ? binding.path
                        : binding.overridePath;

                    rows.Add(new BindingSnapshot(
                        actionKey,
                        action.name,
                        b,
                        effectivePath,
                        binding.isComposite,
                        binding.isPartOfComposite));
                }
            }

            return rows;
        }

        /// <summary>
        /// Convenience overload: captures the map that owns
        /// <paramref name="action"/> (the conflict scope is always one map).
        /// </summary>
        public static IReadOnlyList<BindingSnapshot> CaptureMapOf(InputAction action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            return CaptureMap(action.actionMap);
        }
    }
}
