using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.View.Assembly
{
    /// <summary>
    /// The physics &lt;-&gt; graph bridge of the assembly system
    /// (TASK-A-TECH-05). The graph is the authoritative data, the scene's
    /// parts and joints are its physical representation, and this class
    /// provides both directions:
    ///  - physical -&gt; graph: <see cref="Capture"/> re-pours the current
    ///    scene state (all enabled <see cref="AssemblyPart"/>s plus their
    ///    unbroken <see cref="AssemblyJoint"/>s) into a graph;
    ///  - graph -&gt; physical: <see cref="AssemblyRebuilder.Rebuild"/> replays
    ///    a graph/snapshot into live parts and joints;
    ///  - live mirroring: <see cref="Bind"/> subscribes an
    ///    <see cref="AssemblyGraph"/> to <see cref="AssemblyBuilder"/>'s
    ///    joint events so connects/breaks performed by gameplay are reflected
    ///    into the bound graph in real time.
    /// </summary>
    public static class AssemblyGraphSync
    {
        /// <summary>
        /// Re-pours the whole scene state into <paramref name="graph"/>
        /// (Clear first): every enabled part becomes a node (id =
        /// <see cref="AssemblyPart.PartId"/>, made unique with a numeric
        /// suffix on collision), every unbroken joint an edge (owner side =
        /// from/moving side). The graph is invalid-safe: mid-assembly scenes
        /// with orphans or islands capture fine — run
        /// <see cref="AssemblyGraph.Validate"/> when a validity gate is
        /// needed before serialising.
        /// </summary>
        public static void Capture(AssemblyGraph graph)
        {
            if (graph == null)
            {
                throw new ArgumentNullException(nameof(graph));
            }

            graph.Clear();

            var idByInstance = new Dictionary<AssemblyPart, string>();
            foreach (AssemblyPart part in AssemblyPart.AllParts)
            {
                if (part == null)
                {
                    continue;
                }

                var node = new AssemblyGraphNode
                {
                    PartId = UniquePartId(graph, part),
                    Kind = part.Kind,
                    Position = part.transform.position,
                    Rotation = part.transform.rotation,
                };

                IReadOnlyList<AttachmentPoint> points = part.Points;
                for (int i = 0; i < points.Count; i++)
                {
                    AttachmentPoint point = points[i];
                    node.Points.Add(new AssemblyGraphPointSpec
                    {
                        Type = point.Type,
                        LocalPosition = point.transform.localPosition,
                        LocalForward = point.transform.localRotation * Vector3.forward,
                    });
                }

                graph.AddPart(node);
                idByInstance.Add(part, node.PartId);
            }

            foreach (AssemblyPart part in AssemblyPart.AllParts)
            {
                if (part == null)
                {
                    continue;
                }

                foreach (AssemblyJoint joint in part.Joints)
                {
                    // Count each bond once, from its owner (moving) side.
                    if (joint == null || joint.IsBroken || joint.OwnerPart != part)
                    {
                        continue;
                    }

                    AssemblyPart other = joint.OtherPart;
                    if (other == null || !idByInstance.TryGetValue(part, out string fromId)
                        || !idByInstance.TryGetValue(other, out string toId))
                    {
                        continue;
                    }

                    graph.AddJoint(new AssemblyGraphJoint
                    {
                        FromPartId = fromId,
                        FromPointIndex = IndexOfPoint(part, joint.OwnerPoint),
                        ToPartId = toId,
                        ToPointIndex = IndexOfPoint(other, joint.OtherPoint),
                        Kind = AssemblyJointKind.Rigid,
                    });
                }
            }
        }

        /// <summary>
        /// Starts live mirroring of scene bonds into <paramref name="graph"/>:
        /// every subsequent <see cref="AssemblyBuilder.Connect"/> upserts its
        /// endpoints and adds an edge, every break (disconnect, part
        /// demolition) removes it. Dispose the returned link to stop.
        /// Parts destroyed while bound stay as graph nodes (the graph is the
        /// authority; run <see cref="Capture"/> for a full refresh).
        /// </summary>
        public static LiveLink Bind(AssemblyGraph graph)
        {
            return new LiveLink(graph ?? throw new ArgumentNullException(nameof(graph)));
        }

        private static string UniquePartId(AssemblyGraph graph, AssemblyPart part)
        {
            string baseId = string.IsNullOrEmpty(part.PartId) ? "part" : part.PartId;
            string candidate = baseId;
            int suffix = 2;
            while (graph.GetPart(candidate) != null)
            {
                candidate = $"{baseId}_{suffix}";
                suffix++;
            }

            return candidate;
        }

        private static int IndexOfPoint(AssemblyPart part, AttachmentPoint point)
        {
            IReadOnlyList<AttachmentPoint> points = part.Points;
            for (int i = 0; i < points.Count; i++)
            {
                if (ReferenceEquals(points[i], point))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Live-mirroring handle; Dispose to unsubscribe.</summary>
        public sealed class LiveLink : IDisposable
        {
            private readonly AssemblyGraph _graph;
            private readonly Dictionary<AssemblyJoint, string> _jointIds =
                new Dictionary<AssemblyJoint, string>();
            private readonly Dictionary<AssemblyPart, string> _partIds =
                new Dictionary<AssemblyPart, string>();

            private bool _disposed;

            internal LiveLink(AssemblyGraph graph)
            {
                _graph = graph;
                AssemblyBuilder.JointCreated += OnJointCreated;
                AssemblyBuilder.JointBroken += OnJointBroken;
            }

            /// <summary>Unsubscribes from the builder events (idempotent).</summary>
            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                AssemblyBuilder.JointCreated -= OnJointCreated;
                AssemblyBuilder.JointBroken -= OnJointBroken;
            }

            private void OnJointCreated(AssemblyJoint joint)
            {
                if (_disposed || joint == null || joint.IsBroken)
                {
                    return;
                }

                string fromId = UpsertPart(joint.OwnerPart);
                string toId = UpsertPart(joint.OtherPart);
                if (fromId == null || toId == null)
                {
                    return;
                }

                var edge = new AssemblyGraphJoint
                {
                    FromPartId = fromId,
                    FromPointIndex = IndexOfPoint(joint.OwnerPart, joint.OwnerPoint),
                    ToPartId = toId,
                    ToPointIndex = IndexOfPoint(joint.OtherPart, joint.OtherPoint),
                    Kind = AssemblyJointKind.Rigid,
                };
                if (_graph.AddJoint(edge))
                {
                    _jointIds[joint] = edge.JointId;
                }
            }

            private void OnJointBroken(AssemblyJoint joint)
            {
                if (_disposed || joint == null)
                {
                    return;
                }

                if (_jointIds.TryGetValue(joint, out string jointId))
                {
                    _graph.RemoveJoint(jointId);
                    _jointIds.Remove(joint);
                }
            }

            private string UpsertPart(AssemblyPart part)
            {
                if (part == null)
                {
                    return null;
                }

                if (_partIds.TryGetValue(part, out string known))
                {
                    return known;
                }

                string baseId = string.IsNullOrEmpty(part.PartId) ? "part" : part.PartId;

                // 图里已有同 id 节点（典型：Bind 之前 Capture 过）→ 按 id 认领，
                // 图是权威，不重建节点。
                if (_graph.GetPart(baseId) != null)
                {
                    _partIds.Add(part, baseId);
                    return baseId;
                }

                var node = new AssemblyGraphNode
                {
                    PartId = baseId,
                    Kind = part.Kind,
                    Position = part.transform.position,
                    Rotation = part.transform.rotation,
                };

                IReadOnlyList<AttachmentPoint> points = part.Points;
                for (int i = 0; i < points.Count; i++)
                {
                    AttachmentPoint point = points[i];
                    node.Points.Add(new AssemblyGraphPointSpec
                    {
                        Type = point.Type,
                        LocalPosition = point.transform.localPosition,
                        LocalForward = point.transform.localRotation * Vector3.forward,
                    });
                }

                if (!_graph.AddPart(node))
                {
                    return null;
                }

                _partIds.Add(part, node.PartId);
                return node.PartId;
            }
        }
    }
}
