using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.View.Assembly
{
    /// <summary>
    /// Runtime assembly front-end of the slice (TASK-A-TECH-02), all static —
    /// state lives on the parts/joints/points themselves:
    ///  - <see cref="TryFindSnap"/>: nearest free, type-compatible docking
    ///    point on a DIFFERENT part (the snap preview source),
    ///  - <see cref="Align"/>: rigidly moves a part so its point sits on the
    ///    target point with the two outward normals opposed (faces meet),
    ///  - <see cref="Connect"/>: align + create the <see cref="AssemblyJoint"/>;
    ///    the returned joint is the reference to the bond (拆卸 = Destroy),
    ///  - <see cref="GetComposite"/>: the connected rigid graph of a part —
    ///    the "composite" this slice hands out instead of merging GameObjects.
    ///    (Roadmap note: this BFS over the joint graph is the seed of the
    ///    assembly-topology data structure named as the full-investment
    ///    prerequisite for direction two.)
    ///
    /// TASK-A-TECH-05 adds the two lifecycle events
    /// (<see cref="JointCreated"/>/<see cref="JointBroken"/>) that
    /// <see cref="AssemblyGraphSync"/> subscribes to keep an
    /// <see cref="AssemblyGraph"/> mirrored with the physical bonds; they are
    /// pure notifications and change no bond behaviour.
    /// </summary>
    public static class AssemblyBuilder
    {
        /// <summary>Upper bound on joint chain BFS in <see cref="GetComposite"/>.</summary>
        private const int MaxCompositeSize = 256;

        /// <summary>Raised after <see cref="Connect"/> created a live bond.</summary>
        public static event Action<AssemblyJoint> JointCreated;

        /// <summary>Raised once when a bond dies — via <see cref="Disconnect"/>,
        /// <see cref="AssemblyJoint.Break"/>, or the joint GameObject being
        /// destroyed (part demolition).</summary>
        public static event Action<AssemblyJoint> JointBroken;

        /// <summary>
        /// Raise helper for <see cref="AssemblyJoint"/> (events can only be
        /// invoked from their declaring type; the joint announces its own
        /// death through this).
        /// </summary>
        internal static void RaiseJointBroken(AssemblyJoint joint)
        {
            JointBroken?.Invoke(joint);
        }

        /// <summary>A free docking point found for the dragged part.</summary>
        public struct SnapCandidate
        {
            /// <summary>Part providing the free point.</summary>
            public AssemblyPart Part;

            /// <summary>The free, compatible point on <see cref="Part"/>.</summary>
            public AttachmentPoint Point;

            /// <summary>Distance from the search origin, in metres.</summary>
            public float Distance;
        }

        /// <summary>
        /// Canonical up vector for a docking normal: world up unless the
        /// normal is (near) vertical, then world forward. Keeps docking
        /// alignment deterministic (no random roll).
        /// </summary>
        public static Vector3 PickPerpendicularUp(Vector3 forward)
        {
            Vector3 normal = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
            return Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.forward;
        }

        /// <summary>
        /// Nearest free, type-compatible point on a different part within
        /// <paramref name="maxDistance"/> of <paramref name="fromPosition"/>;
        /// ties break on registration order.
        /// </summary>
        public static bool TryFindSnap(Vector3 fromPosition, AssemblyPartTypeFilter filter,
            float maxDistance, out SnapCandidate candidate)
        {
            candidate = default;
            float bestSqr = maxDistance * maxDistance;
            bool found = false;

            IReadOnlyList<AssemblyPart> parts = AssemblyPart.AllParts;
            for (int i = 0; i < parts.Count; i++)
            {
                AssemblyPart part = parts[i];
                if (part == null || (filter.Exclude != null && part == filter.Exclude))
                {
                    continue;
                }

                AttachmentPoint point = part.FindNearestFreePoint(fromPosition, maxDistance, filter.WantedType);
                if (point == null)
                {
                    continue;
                }

                float sqr = (point.WorldPosition - fromPosition).sqrMagnitude;
                if (sqr <= bestSqr)
                {
                    bestSqr = sqr;
                    candidate = new SnapCandidate
                    {
                        Part = part,
                        Point = point,
                        Distance = Mathf.Sqrt(sqr),
                    };
                    found = true;
                }
            }

            return found;
        }

        /// <summary>
        /// Docking seam clearance, in metres. The aligned part is left this
        /// far short of full contact so the two colliders never touch: an
        /// exact-touch seam sits INSIDE PhysX's contact offset (0.01 m) and
        /// keeps a live contact pair at the joint, whose separation/friction
        /// impulses fight the locked joint under load (measured: a 2 m/s
        /// pull left the pair ringing along the seam at ±0.38 m/s and the
        /// tangential velocity only half-transferred). 1.5 cm clears the
        /// contact offset while staying visually flush.
        /// </summary>
        public const float DockGapMeters = 0.015f;

        /// <summary>
        /// Rigidly teleports <paramref name="movingPart"/> so that
        /// <paramref name="movingPoint"/> docks onto
        /// <paramref name="targetPoint"/> with the two outward normals
        /// opposed and a <see cref="DockGapMeters"/> seam clearance. Twist is
        /// canonicalised via <see cref="PickPerpendicularUp"/>, so the result
        /// is deterministic.
        ///
        /// The teleport goes through the TRANSFORM (not Rigidbody.position):
        /// a direct rb.position/rb.rotation write is deferred, and the
        /// trailing Physics.SyncTransforms would push the STALE transform
        /// pose back into the physics body — the move silently undoes
        /// itself (measured: the aligned pair stayed 2 m apart and the
        /// locked joint then slammed the bodies together on the next
        /// simulation steps).
        /// </summary>
        public static void Align(AssemblyPart movingPart, AttachmentPoint movingPoint, AttachmentPoint targetPoint)
        {
            Quaternion targetPointRotation = Quaternion.LookRotation(
                -targetPoint.WorldForward, PickPerpendicularUp(targetPoint.WorldForward));
            Quaternion delta = targetPointRotation * Quaternion.Inverse(movingPoint.transform.rotation);

            Transform bodyTransform = movingPart.transform;
            bodyTransform.rotation = delta * bodyTransform.rotation;
            // Rotation pivots on the body origin (factory parts keep their
            // collider centred there), so re-read the point's world pose
            // AFTER the rotation before closing the positional gap.
            bodyTransform.position += targetPoint.WorldPosition - movingPoint.transform.position;
            // Back the mover off along the target's outward normal so the
            // two colliders keep the seam clearance (the target's normal
            // points away from the base body, i.e. towards the mover).
            bodyTransform.position += targetPoint.WorldForward.normalized * DockGapMeters;
            Physics.SyncTransforms();
        }

        /// <summary>
        /// Aligns <paramref name="movingPart"/> onto <paramref name="targetPoint"/>
        /// and bonds the two parts with a locked <see cref="AssemblyJoint"/>.
        /// Returns the joint (the reference to the bond), or null when the
        /// request is invalid (null parts/points, same part, occupied or
        /// incompatible points) — a warning is logged in that case.
        /// </summary>
        public static AssemblyJoint Connect(AssemblyPart movingPart, AttachmentPoint movingPoint,
            AttachmentPoint targetPoint)
        {
            if (movingPart == null || movingPoint == null || targetPoint == null)
            {
                Debug.LogWarning("[AssemblyBuilder] Connect rejected: null part or point.");
                return null;
            }

            if (movingPoint.Owner != movingPart)
            {
                Debug.LogWarning($"[AssemblyBuilder] Connect rejected: point '{movingPoint.name}' is not on '{movingPart.name}'.");
                return null;
            }

            AssemblyPart basePart = targetPoint.Owner;
            if (basePart == null || basePart == movingPart)
            {
                Debug.LogWarning("[AssemblyBuilder] Connect rejected: target point has no owner or belongs to the moving part.");
                return null;
            }

            if (movingPoint.IsOccupied || targetPoint.IsOccupied)
            {
                Debug.LogWarning("[AssemblyBuilder] Connect rejected: a docking point is already occupied.");
                return null;
            }

            if (!AttachmentPoint.AreCompatible(movingPoint.Type, targetPoint.Type))
            {
                Debug.LogWarning($"[AssemblyBuilder] Connect rejected: incompatible types {movingPoint.Type} <-> {targetPoint.Type}.");
                return null;
            }

            Align(movingPart, movingPoint, targetPoint);

            var joint = movingPart.gameObject.AddComponent<AssemblyJoint>();
            joint.Configure(movingPart, movingPoint, basePart, targetPoint);
            JointCreated?.Invoke(joint);
            return joint;
        }

        /// <summary>Tears the bond down (拆卸). Null-safe.</summary>
        public static void Disconnect(AssemblyJoint joint)
        {
            if (joint != null)
            {
                joint.Break();
            }
        }

        /// <summary>
        /// Every part rigidly reachable from <paramref name="part"/> through
        /// unbroken joints, <paramref name="part"/> included. Order is the
        /// BFS order starting at the given part.
        /// </summary>
        public static List<AssemblyPart> GetComposite(AssemblyPart part)
        {
            var composite = new List<AssemblyPart>();
            if (part == null)
            {
                return composite;
            }

            var visited = new HashSet<AssemblyPart>();
            var queue = new Queue<AssemblyPart>();
            visited.Add(part);
            queue.Enqueue(part);

            while (queue.Count > 0 && composite.Count < MaxCompositeSize)
            {
                AssemblyPart current = queue.Dequeue();
                composite.Add(current);

                IReadOnlyList<AssemblyJoint> joints = current.Joints;
                for (int i = 0; i < joints.Count; i++)
                {
                    AssemblyJoint joint = joints[i];
                    if (joint == null || joint.IsBroken)
                    {
                        continue;
                    }

                    AssemblyPart neighbour = joint.OwnerPart == current ? joint.OtherPart : joint.OwnerPart;
                    if (neighbour != null && visited.Add(neighbour))
                    {
                        queue.Enqueue(neighbour);
                    }
                }
            }

            return composite;
        }

        /// <summary>Filter passed to <see cref="TryFindSnap"/>.</summary>
        public readonly struct AssemblyPartTypeFilter
        {
            /// <summary>Part to skip (the dragged one).</summary>
            public readonly AssemblyPart Exclude;

            /// <summary>Wanted docking class on the other side, or null for any.</summary>
            public readonly AttachmentPointType? WantedType;

            public AssemblyPartTypeFilter(AssemblyPart exclude, AttachmentPointType? wantedType)
            {
                Exclude = exclude;
                WantedType = wantedType;
            }
        }
    }
}
