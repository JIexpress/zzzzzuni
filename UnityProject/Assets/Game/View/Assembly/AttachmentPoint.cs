using UnityEngine;

namespace Game.View.Assembly
{
    /// <summary>
    /// Docking marker on an <see cref="AssemblyPart"/> (TASK-A-TECH-02): a
    /// child transform whose origin is the docking centre and whose local +Z
    /// (forward) is the OUTWARD normal of the docking face. A point is either
    /// free or occupied by exactly one <see cref="AssemblyJoint"/>;
    /// <see cref="AssemblyBuilder"/> only ever snaps to free points, so every
    /// face of a composite keeps one bond at most.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AttachmentPoint : MonoBehaviour
    {
        [SerializeField] private AttachmentPointType _type = AttachmentPointType.Structural;

        private AssemblyPart _owner;
        private AssemblyJoint _occupant;

        /// <summary>Docking class of this point.</summary>
        public AttachmentPointType Type => _type;

        /// <summary>The part this point belongs to (cached parent lookup).</summary>
        public AssemblyPart Owner
        {
            get
            {
                if (_owner == null)
                {
                    _owner = GetComponentInParent<AssemblyPart>();
                }

                return _owner;
            }
        }

        /// <summary>The bond currently attached here, or null when free.</summary>
        public AssemblyJoint Occupant => _occupant;

        /// <summary>True while exactly one joint is attached to this point.</summary>
        public bool IsOccupied => _occupant != null;

        /// <summary>Docking centre in world space.</summary>
        public Vector3 WorldPosition => transform.position;

        /// <summary>Outward normal of the docking face in world space.</summary>
        public Vector3 WorldForward => transform.forward;

        /// <summary>Sets the docking class (editor setup and tests).</summary>
        public void SetType(AttachmentPointType type)
        {
            _type = type;
        }

        /// <summary>
        /// Compatibility rule of the slice: structural faces are universal,
        /// otherwise the two classes must match exactly.
        /// </summary>
        public static bool AreCompatible(AttachmentPointType a, AttachmentPointType b)
        {
            return a == b
                || a == AttachmentPointType.Structural
                || b == AttachmentPointType.Structural;
        }

        internal void BindOccupant(AssemblyJoint joint)
        {
            _occupant = joint;
        }

        internal void UnbindOccupant(AssemblyJoint joint)
        {
            if (_occupant == joint)
            {
                _occupant = null;
            }
        }
    }
}
