using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.View.Assembly
{
    /// <summary>
    /// Snapshot &gt;-&gt; scene replay of the assembly system
    /// (TASK-A-TECH-05) — the "load your craft" half of the core loop. The
    /// replay is deliberately a REPLAY, not a restore: parts are created
    /// through the same <see cref="AssemblyPartFactory"/> creators and bonds
    /// through the same <see cref="AssemblyBuilder.Connect"/> path gameplay
    /// uses, in the saved joint order, from the saved poses. Determinism:
    /// float poses round-trip losslessly through the snapshot (double JSON
    /// storage), so the Connect alignments are exact no-ops and the rebuilt
    /// vehicle starts with bit-identical initial conditions.
    /// </summary>
    public static class AssemblyRebuilder
    {
        /// <summary>
        /// Replays a snapshot into live parts + joints. Returns the created
        /// parts, in snapshot part order. Throws on a snapshot that fails the
        /// structural gate (<see cref="AssemblySnapshotCodec.FromSnapshot"/>) or
        /// whose point layout no longer matches the factory geometry.
        /// </summary>
        public static List<AssemblyPart> Rebuild(AssemblySnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            AssemblyGraph graph = AssemblySnapshotCodec.FromSnapshot(snapshot);

            var partsBySnapshot = new List<AssemblyPart>(snapshot.Parts.Count);
            var partById = new Dictionary<string, AssemblyPart>(snapshot.Parts.Count);
            foreach (AssemblySnapshotPart partData in snapshot.Parts)
            {
                AssemblyPart part = CreatePartByKind(partData);
                partsBySnapshot.Add(part);
                partById.Add(partData.PartId, part);
            }

            foreach (AssemblySnapshotJoint jointData in snapshot.Joints)
            {
                AssemblyPart from = partById[jointData.FromPartId];
                AssemblyPart to = partById[jointData.ToPartId];
                AttachmentPoint fromPoint = GetPointOrThrow(from, jointData.FromPointIndex, jointData.JointId);
                AttachmentPoint toPoint = GetPointOrThrow(to, jointData.ToPointIndex, jointData.JointId);

                AssemblyJoint joint = AssemblyBuilder.Connect(from, fromPoint, toPoint);
                if (joint == null)
                {
                    throw new InvalidOperationException(
                        $"装配重建失败：关节 '{jointData.JointId}' 的连接被拒绝 " +
                        $"({jointData.FromPartId}[{jointData.FromPointIndex}] -> {jointData.ToPartId}[{jointData.ToPointIndex}]）");
                }
            }

            return partsBySnapshot;
        }

        private static AssemblyPart CreatePartByKind(AssemblySnapshotPart data)
        {
            var position = new Vector3((float)data.PositionX, (float)data.PositionY, (float)data.PositionZ);
            var rotation = new Quaternion(
                (float)data.RotationX, (float)data.RotationY, (float)data.RotationZ, (float)data.RotationW);

            AssemblyPart part;
            switch ((AssemblyPartKind)data.Kind)
            {
                case AssemblyPartKind.Cube:
                    part = AssemblyPartFactory.CreateCube(data.PartId, position);
                    break;
                case AssemblyPartKind.Thruster:
                    part = AssemblyPartFactory.CreateThruster(
                        data.PartId, position,
                        thrustNewtons: (float)(data.ThrustNewtons ?? 6f));
                    break;
                case AssemblyPartKind.Wheel:
                    part = AssemblyPartFactory.CreateWheel(data.PartId, position);
                    break;
                case AssemblyPartKind.StructuralBeam:
                    part = AssemblyPartFactory.CreateStructuralBeam(data.PartId, position);
                    break;
                case AssemblyPartKind.FuelTank:
                    part = AssemblyPartFactory.CreateFuelTank(
                        data.PartId, position,
                        fuelMassKg: (float)(data.FuelMassKg ?? 2f),
                        dryMassKg: (float)(data.DryMassKg ?? 1f));
                    break;
                case AssemblyPartKind.Separator:
                    part = AssemblyPartFactory.CreateSeparator(data.PartId, position);
                    break;
                case AssemblyPartKind.FuelLine:
                    part = AssemblyPartFactory.CreateFuelLine(data.PartId, position);
                    break;
                default:
                    throw new InvalidOperationException($"装配重建失败：未知零件种类 {(AssemblyPartKind)data.Kind}");
            }

            part.transform.rotation = rotation;
            ReplacePointsFromSnapshot(part, data);
            Physics.SyncTransforms();
            return part;
        }

        /// <summary>
        /// Points are data, not factory internals: the factory's default
        /// docking layout is replaced by the snapshot's (covers vehicles
        /// whose points were added or edited after creation). DestroyImmediate
        /// on purpose — a deferred Destroy would leave the stale points
        /// enumerable for the rest of the frame and shift the joint indices.
        /// </summary>
        private static void ReplacePointsFromSnapshot(AssemblyPart part, AssemblySnapshotPart data)
        {
            for (int i = part.transform.childCount - 1; i >= 0; i--)
            {
                Transform child = part.transform.GetChild(i);
                if (child.GetComponent<AttachmentPoint>() != null)
                {
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
                }
            }

            part.RefreshPoints();

            foreach (AssemblySnapshotPoint pointData in data.Points)
            {
                part.AddAttachmentPoint(
                    (AttachmentPointType)pointData.Type,
                    new Vector3(
                        (float)pointData.LocalPositionX,
                        (float)pointData.LocalPositionY,
                        (float)pointData.LocalPositionZ),
                    new Vector3(
                        (float)pointData.LocalForwardX,
                        (float)pointData.LocalForwardY,
                        (float)pointData.LocalForwardZ));
            }
        }

        private static AttachmentPoint GetPointOrThrow(AssemblyPart part, int index, string jointId)
        {
            IReadOnlyList<AttachmentPoint> points = part.Points;
            if (index < 0 || index >= points.Count)
            {
                throw new InvalidOperationException(
                    $"装配重建失败：关节 '{jointId}' 引用了 '{part.PartId}' 不存在的连接点 #{index}");
            }

            return points[index];
        }
    }
}
