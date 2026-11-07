using UnityEngine;

namespace Game.View.Assembly
{
    /// <summary>
    /// Encapsulates one ConfigurableJoint bond between two
    /// <see cref="AssemblyPart"/>s (TASK-A-TECH-02). The component lives on
    /// the part that MOVED into the dock ("owner"); the joint's
    /// <c>connectedBody</c> is the part that was already there ("other").
    ///
    /// Default configuration locks all six degrees of freedom; the two lock
    /// switches exist so a later slice can free selected axes (wheel spin,
    /// hinge) without a new joint type. The parameters below are the tuned
    /// stable-rigid-bond recipe, measured on the velocity-transmission and
    /// 10-second ground-settling tests (full record in
    /// Docs/Logs/TASK-A-TECH-02.md):
    ///   - all motions Locked (no drives needed; drives at 0 would fight the
    ///     solver and are the classic jitter source),
    ///   - enablePreprocessing = TRUE (preprocessing is what makes all-locked
    ///     D6 joints converge hard; disabling it leaves the locked pair soft —
    ///     measured: relative velocity plateaued at ~1.2 m/s under a 2 m/s
    ///     pull. Spawn-safety is not a concern because Align() guarantees an
    ///     exact creation pose),
    ///   - enableCollision = false PLUS a Physics.IgnoreCollision pair, so
    ///     the seam between the two touching box colliders never generates
    ///     contact forces against the locked joint,
    ///   - PositionAndRotation projection at 0.05 m / 30 deg to bleed off
    ///     accumulated drift before it turns into snapping,
    ///   - per-body solver iterations raised 6/1 -> 8/2 for chained bodies.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AssemblyJoint : MonoBehaviour
    {
        [SerializeField] private bool _lockTranslation = true;
        [SerializeField] private bool _lockRotation = true;
        [SerializeField] private float _projectionDistance = 0.05f;
        [SerializeField] private float _projectionAngle = 30f;
        [SerializeField] private int _solverIterations = 8;
        [SerializeField] private int _solverVelocityIterations = 2;

        /// <summary>Part this component sits on (the side that moved into the dock).</summary>
        public AssemblyPart OwnerPart { get; private set; }

        /// <summary>Part the owner docked onto (the joint's connected body side).</summary>
        public AssemblyPart OtherPart { get; private set; }

        /// <summary>Docking point on <see cref="OwnerPart"/>.</summary>
        public AttachmentPoint OwnerPoint { get; private set; }

        /// <summary>Docking point on <see cref="OtherPart"/>.</summary>
        public AttachmentPoint OtherPoint { get; private set; }

        /// <summary>The underlying joint; null once broken or destroyed.</summary>
        public ConfigurableJoint PhysicsJoint { get; private set; }

        /// <summary>True after <see cref="Break"/> (or when the joint died).</summary>
        public bool IsBroken => PhysicsJoint == null;

        /// <summary>
        /// Creates and tunes the ConfigurableJoint. The two parts must already
        /// be aligned (use <see cref="AssemblyBuilder.Align"/>); the anchors
        /// are then the docking points' local positions on each body.
        /// </summary>
        public void Configure(AssemblyPart ownerPart, AttachmentPoint ownerPoint,
            AssemblyPart otherPart, AttachmentPoint otherPoint)
        {
            OwnerPart = ownerPart;
            OwnerPoint = ownerPoint;
            OtherPart = otherPart;
            OtherPoint = otherPoint;

            var joint = gameObject.AddComponent<ConfigurableJoint>();
            PhysicsJoint = joint;
            joint.connectedBody = otherPart.Body;
            joint.autoConfigureConnectedAnchor = false;
            joint.anchor = transform.InverseTransformPoint(ownerPoint.WorldPosition);
            joint.connectedAnchor = otherPart.transform.InverseTransformPoint(otherPoint.WorldPosition);

            ConfigurableJointMotion translation = _lockTranslation
                ? ConfigurableJointMotion.Locked
                : ConfigurableJointMotion.Free;
            joint.xMotion = translation;
            joint.yMotion = translation;
            joint.zMotion = translation;

            ConfigurableJointMotion rotation = _lockRotation
                ? ConfigurableJointMotion.Locked
                : ConfigurableJointMotion.Free;
            joint.angularXMotion = rotation;
            joint.angularYMotion = rotation;
            joint.angularZMotion = rotation;

            joint.rotationDriveMode = RotationDriveMode.Slerp;
            // Preprocessing ON (Unity default): it is what makes all-locked
            // D6 joints converge hard. An earlier tuning round disabled it
            // for spawn safety, but Align() guarantees the joint is created
            // in an EXACT pose, so there is no violated-spawn correction to
            // explode — and with it off the locked pair stayed soft (the
            // relative velocity plateaued at ~1.2 m/s under a 2 m/s pull
            // instead of converging to 0).
            joint.enablePreprocessing = true;
            joint.enableCollision = false;
            joint.projectionMode = JointProjectionMode.PositionAndRotation;
            joint.projectionDistance = _projectionDistance;
            joint.projectionAngle = _projectionAngle;
            joint.breakForce = Mathf.Infinity;
            joint.breakTorque = Mathf.Infinity;

            ApplySolverTuning(ownerPart.Body);
            ApplySolverTuning(otherPart.Body);

            IgnoreCollisions(true);
            ownerPart.BindJoint(this);
            otherPart.BindJoint(this);
            ownerPoint.BindOccupant(this);
            otherPoint.BindOccupant(this);
        }

        /// <summary>
        /// Tears the bond down: destroys the ConfigurableJoint, frees both
        /// docking points and restores collision between the pair.
        /// </summary>
        public void Break()
        {
            if (PhysicsJoint != null)
            {
                DestroyObject(PhysicsJoint);
                PhysicsJoint = null;
            }

            if (OwnerPart != null)
            {
                OwnerPart.UnbindJoint(this);
            }

            if (OtherPart != null)
            {
                OtherPart.UnbindJoint(this);
            }

            if (OwnerPoint != null)
            {
                OwnerPoint.UnbindOccupant(this);
            }

            if (OtherPoint != null)
            {
                OtherPoint.UnbindOccupant(this);
            }

            IgnoreCollisions(false);
        }

        private void OnDestroy()
        {
            // The ConfigurableJoint dies with this GameObject; just keep the
            // docking points and the other part's bookkeeping consistent.
            if (OwnerPart != null)
            {
                OwnerPart.UnbindJoint(this);
            }

            if (OtherPart != null)
            {
                OtherPart.UnbindJoint(this);
            }

            if (OwnerPoint != null)
            {
                OwnerPoint.UnbindOccupant(this);
            }

            if (OtherPoint != null)
            {
                OtherPoint.UnbindOccupant(this);
            }
        }

        private void ApplySolverTuning(Rigidbody body)
        {
            if (body == null)
            {
                return;
            }

            body.solverIterations = _solverIterations;
            body.solverVelocityIterations = _solverVelocityIterations;
        }

        private void IgnoreCollisions(bool ignore)
        {
            Collider owner = OwnerPart != null ? OwnerPart.GetComponentInChildren<Collider>() : null;
            Collider other = OtherPart != null ? OtherPart.GetComponentInChildren<Collider>() : null;
            if (owner != null && other != null)
            {
                Physics.IgnoreCollision(owner, other, ignore);
            }
        }

        private static void DestroyObject(Object target)
        {
            if (Application.isPlaying)
            {
                Object.Destroy(target);
            }
            else
            {
                Object.DestroyImmediate(target);
            }
        }
    }
}
