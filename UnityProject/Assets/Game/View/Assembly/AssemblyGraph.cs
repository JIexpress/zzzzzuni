using System;
using System.Collections.Generic;
using System.Linq;

namespace Game.View.Assembly
{
    /// <summary>
    /// Pure C# topology of a vehicle (TASK-A-TECH-05): part nodes + joint
    /// edges over a dictionary keyed by part id. This is the authoritative
    /// data of the assembly system — physics joints on the scene are the
    /// representation (see <see cref="AssemblyGraphSync"/> for the bridge in
    /// both directions).
    ///
    /// Invariants, enforced on mutation and re-checked by <see cref="Validate"/>:
    ///  - part ids and joint ids are unique per graph,
    ///  - a joint connects two DIFFERENT parts at points that exist,
    ///  - a docking point carries at most one joint (the physical layer's
    ///    <c>AttachmentPoint.IsOccupied</c> contract),
    ///  - the same point pair is never bonded twice.
    /// <see cref="RemovePart"/> cascades: every joint touching the removed
    /// part goes with it.
    /// </summary>
    public sealed class AssemblyGraph
    {
        private readonly Dictionary<string, AssemblyGraphNode> _parts =
            new Dictionary<string, AssemblyGraphNode>(StringComparer.Ordinal);
        private readonly Dictionary<string, AssemblyGraphJoint> _joints =
            new Dictionary<string, AssemblyGraphJoint>(StringComparer.Ordinal);
        /// <summary>(partId, pointIndex) -> joint id, the point-occupancy index.</summary>
        private readonly Dictionary<(string PartId, int PointIndex), string> _occupiedPoints =
            new Dictionary<(string, int), string>();

        private int _jointCounter;

        /// <summary>All part nodes in insertion order.</summary>
        public IReadOnlyCollection<AssemblyGraphNode> Parts => _parts.Values;

        /// <summary>All joint edges in insertion order.</summary>
        public IReadOnlyCollection<AssemblyGraphJoint> Joints => _joints.Values;

        /// <summary>Number of part nodes.</summary>
        public int PartCount => _parts.Count;

        /// <summary>Number of joint edges.</summary>
        public int JointCount => _joints.Count;

        /// <summary>Node lookup by part id, or null.</summary>
        public AssemblyGraphNode GetPart(string partId)
        {
            return partId != null && _parts.TryGetValue(partId, out AssemblyGraphNode node) ? node : null;
        }

        /// <summary>Edge lookup by joint id, or null.</summary>
        public AssemblyGraphJoint GetJoint(string jointId)
        {
            return jointId != null && _joints.TryGetValue(jointId, out AssemblyGraphJoint joint) ? joint : null;
        }

        /// <summary>Every joint currently touching the given part.</summary>
        public List<AssemblyGraphJoint> GetJointsOf(string partId)
        {
            var result = new List<AssemblyGraphJoint>();
            foreach (AssemblyGraphJoint joint in _joints.Values)
            {
                if (joint.FromPartId == partId || joint.ToPartId == partId)
                {
                    result.Add(joint);
                }
            }

            return result;
        }

        /// <summary>
        /// Adds a part node. Rejects null nodes, null/empty ids and duplicate
        /// ids (the node instance is copied by reference, pose and points as
        /// they are — the graph does not clone).
        /// </summary>
        public bool AddPart(AssemblyGraphNode node)
        {
            if (node == null || string.IsNullOrEmpty(node.PartId) || _parts.ContainsKey(node.PartId))
            {
                return false;
            }

            _parts.Add(node.PartId, node);
            return true;
        }

        /// <summary>
        /// Removes a part node and CASCADES: every joint touching it is
        /// removed first (freeing the far side's docking point). Returns
        /// false when the id is unknown.
        /// </summary>
        public bool RemovePart(string partId)
        {
            if (!_parts.ContainsKey(partId))
            {
                return false;
            }

            foreach (AssemblyGraphJoint joint in GetJointsOf(partId))
            {
                RemoveJoint(joint.JointId);
            }

            _parts.Remove(partId);
            return true;
        }

        /// <summary>
        /// Adds a joint edge. Rejects: null joints, unknown endpoints,
        /// self-loops (same part both sides), out-of-range point indices,
        /// already-occupied points, duplicate point pairs and duplicate
        /// joint ids. A null/empty <see cref="AssemblyGraphJoint.JointId"/>
        /// is auto-assigned ("j0", "j1", ...).
        /// </summary>
        public bool AddJoint(AssemblyGraphJoint joint)
        {
            if (joint == null)
            {
                return false;
            }

            if (string.IsNullOrEmpty(joint.JointId))
            {
                // Auto-assign skipping any id the caller already used.
                do
                {
                    joint.JointId = $"j{_jointCounter}";
                    _jointCounter++;
                }
                while (_joints.ContainsKey(joint.JointId));
            }
            else if (_joints.ContainsKey(joint.JointId))
            {
                return false;
            }

            if (!_parts.TryGetValue(joint.FromPartId, out AssemblyGraphNode from)
                || !_parts.TryGetValue(joint.ToPartId, out AssemblyGraphNode to))
            {
                return false;
            }

            if (joint.FromPartId == joint.ToPartId)
            {
                return false;
            }

            if (joint.FromPointIndex < 0 || joint.FromPointIndex >= from.Points.Count
                || joint.ToPointIndex < 0 || joint.ToPointIndex >= to.Points.Count)
            {
                return false;
            }

            var forwardKey = (joint.FromPartId, joint.FromPointIndex);
            var reverseKey = (joint.ToPartId, joint.ToPointIndex);
            if (_occupiedPoints.ContainsKey(forwardKey) || _occupiedPoints.ContainsKey(reverseKey))
            {
                return false;
            }

            _joints.Add(joint.JointId, joint);
            _occupiedPoints.Add(forwardKey, joint.JointId);
            _occupiedPoints.Add(reverseKey, joint.JointId);
            return true;
        }

        /// <summary>
        /// Removes one joint edge and frees both docking points. Returns
        /// false when the id is unknown.
        /// </summary>
        public bool RemoveJoint(string jointId)
        {
            if (!_joints.TryGetValue(jointId, out AssemblyGraphJoint joint))
            {
                return false;
            }

            _joints.Remove(jointId);
            _occupiedPoints.Remove((joint.FromPartId, joint.FromPointIndex));
            _occupiedPoints.Remove((joint.ToPartId, joint.ToPointIndex));
            return true;
        }

        /// <summary>Clears all nodes and edges (fresh graph, same instance).</summary>
        public void Clear()
        {
            _parts.Clear();
            _joints.Clear();
            _occupiedPoints.Clear();
            _jointCounter = 0;
        }

        /// <summary>
        /// Full validation sweep: connectivity (single connected component),
        /// orphans (joint-less parts while others exist) and duplicate
        /// connections (a point used twice / a pair bonded twice). Mutation
        /// already rejects these; this re-check is the authority for graphs
        /// assembled by hand or loaded from disk.
        /// </summary>
        public AssemblyGraphValidation Validate()
        {
            var result = new AssemblyGraphValidation { ComponentCount = CountComponents() };

            if (result.ComponentCount > 1)
            {
                result.Errors.Add($"图不连通：{result.ComponentCount} 个连通分量（载具必须是单一体）");
            }

            if (_parts.Count > 1)
            {
                foreach (AssemblyGraphNode node in _parts.Values)
                {
                    bool hasJoint = _joints.Values.Any(j => j.FromPartId == node.PartId || j.ToPartId == node.PartId);
                    if (!hasJoint)
                    {
                        result.OrphanPartIds.Add(node.PartId);
                    }
                }

                if (result.OrphanPartIds.Count > 0)
                {
                    result.Errors.Add($"存在孤立件（无任何关节）：{string.Join(", ", result.OrphanPartIds)}");
                }
            }

            var seenPoints = new HashSet<(string, int)>();
            var seenPairs = new HashSet<(string, int, string, int)>();
            foreach (AssemblyGraphJoint joint in _joints.Values)
            {
                if (!seenPoints.Add((joint.FromPartId, joint.FromPointIndex))
                    || !seenPoints.Add((joint.ToPartId, joint.ToPointIndex)))
                {
                    result.Errors.Add($"连接点被重复占用：joint '{joint.JointId}' 的端点已在其它关节上");
                }

                var canonical = CanonicalPair(joint);
                if (!seenPairs.Add(canonical))
                {
                    result.Errors.Add($"重复连接：'{joint.FromPartId}' 与 '{joint.ToPartId}' 的同一对点已存在关节（joint '{joint.JointId}'）");
                }
            }

            return result;
        }

        private int CountComponents()
        {
            if (_parts.Count == 0)
            {
                return 0;
            }

            var visited = new HashSet<string>();
            var queue = new Queue<string>();
            int components = 0;
            foreach (AssemblyGraphNode node in _parts.Values)
            {
                if (!visited.Add(node.PartId))
                {
                    continue;
                }

                components++;
                queue.Enqueue(node.PartId);
                while (queue.Count > 0)
                {
                    string current = queue.Dequeue();
                    foreach (AssemblyGraphJoint joint in _joints.Values)
                    {
                        string neighbour = null;
                        if (joint.FromPartId == current)
                        {
                            neighbour = joint.ToPartId;
                        }
                        else if (joint.ToPartId == current)
                        {
                            neighbour = joint.FromPartId;
                        }

                        if (neighbour != null && visited.Add(neighbour))
                        {
                            queue.Enqueue(neighbour);
                        }
                    }
                }
            }

            return components;
        }

        private static (string, int, string, int) CanonicalPair(AssemblyGraphJoint joint)
        {
            // A pair (A,ai)-(B,bi) and its reversal must compare equal.
            return string.CompareOrdinal(joint.FromPartId, joint.ToPartId) <= 0
                ? (joint.FromPartId, joint.FromPointIndex, joint.ToPartId, joint.ToPointIndex)
                : (joint.ToPartId, joint.ToPointIndex, joint.FromPartId, joint.FromPointIndex);
        }
    }
}
