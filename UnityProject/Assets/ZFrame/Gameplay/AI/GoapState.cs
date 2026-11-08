using System;
using System.Collections.Generic;
using System.Text;

namespace ZFrame.Gameplay.AI
{
    /// <summary>
    /// Symbolic world state of the GOAP slice (TASK-A-TECH-04): a set of
    /// true atoms ("has_tool", "device_broken", ...). Anything absent is
    /// false — classic STRIPS-style closed world, which keeps
    /// preconditions/effects trivially serializable and comparable.
    ///
    /// Pure C#: part of the engine-free logic layer (purity-scanned under
    /// Assets/ZFrame/Gameplay). <see cref="Clone"/> gives planner-internal
    /// value semantics; equality for the visited set is the sorted atom list.
    /// </summary>
    public sealed class GoapState
    {
        private readonly HashSet<string> _atoms = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Number of true atoms (diagnostics/tests).</summary>
        public int AtomCount => _atoms.Count;

        /// <summary>Marks <paramref name="atom"/> true. Null/empty throws (atoms are identity).</summary>
        public void Add(string atom)
        {
            if (string.IsNullOrEmpty(atom))
            {
                throw new ArgumentException("GoapState: atom must not be null or empty.", nameof(atom));
            }

            _atoms.Add(atom);
        }

        /// <summary>Marks <paramref name="atom"/> false. Null/empty throws.</summary>
        public void Remove(string atom)
        {
            if (string.IsNullOrEmpty(atom))
            {
                throw new ArgumentException("GoapState: atom must not be null or empty.", nameof(atom));
            }

            _atoms.Remove(atom);
        }

        /// <summary>True when the atom is present in this state.</summary>
        public bool Has(string atom)
        {
            return atom != null && _atoms.Contains(atom);
        }

        /// <summary>True when every atom of <paramref name="required"/> is true here (precondition/goal test).</summary>
        public bool Satisfies(GoapState required)
        {
            if (required == null)
            {
                throw new ArgumentNullException(nameof(required));
            }

            foreach (string atom in required._atoms)
            {
                if (!_atoms.Contains(atom))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Independent copy (planner expands without mutating the parent node).</summary>
        public GoapState Clone()
        {
            var copy = new GoapState();
            foreach (string atom in _atoms)
            {
                copy._atoms.Add(atom);
            }

            return copy;
        }

        /// <summary>
        /// Canonical fingerprint (sorted atoms): the planner's visited-set key.
        /// Sorted so atom insertion order cannot change search behaviour.
        /// </summary>
        public string ToKey()
        {
            var sorted = new List<string>(_atoms);
            sorted.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            for (int i = 0; i < sorted.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(sorted[i]);
            }

            return sb.ToString();
        }

        /// <summary>Same atom set → equal (planner/test convenience).</summary>
        public override bool Equals(object obj)
        {
            return obj is GoapState other && ToKey().Equals(other.ToKey(), StringComparison.Ordinal);
        }

        /// <summary>Hash of the canonical fingerprint.</summary>
        public override int GetHashCode()
        {
            return ToKey().GetHashCode(StringComparison.Ordinal);
        }

        /// <summary>Debug form: "{atom1, atom2}".</summary>
        public override string ToString()
        {
            return "{" + string.Join(", ", new List<string>(_atoms).ToArray()) + "}";
        }
    }
}
