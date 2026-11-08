using System;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace Game.View.Assembly
{
    /// <summary>
    /// Graph &lt;-&gt; JSON snapshot codec of the assembly system
    /// (TASK-A-TECH-05). Newtonsoft is the serializer (the project-wide JSON
    /// choice — ZFrame.Data's SaveSystem uses it too); the storage location
    /// is <c>Application.persistentDataPath/assemblies/{name}.json</c>.
    ///
    /// Why assemblies do NOT go through <see cref="ZFrame.Data.ISaveSystem"/>:
    /// the save system is the player's progress (slots, migrations, meta
    /// headers, single-owner, machine-local). An assembly is a shareable
    /// creation — a craft file a player hands to another player — so it is a
    /// separate, name-addressed, versioned file format in its own directory,
    /// deliberately not bound to slot indices or player metadata. Both are
    /// JSON under persistentDataPath; only the audience differs. (Full note
    /// in Docs/Logs/TASK-A-TECH-05.md.)
    /// </summary>
    public static class AssemblySnapshotCodec
    {
        /// <summary>Current snapshot format version (bump on incompatible changes).</summary>
        public const int CurrentVersion = 1;

        private const string DirectoryName = "assemblies";
        private const string FileExtension = ".json";
        private const string TempSuffix = ".tmp";
        private const int MaxNameLength = 64;
        private const string FallbackName = "assembly";

        /// <summary>
        /// Overrides the storage root (tests and tooling only; null restores
        /// the default persistentDataPath location). Callers that set this
        /// must reset it to null afterwards.
        /// </summary>
        public static string RootDirectoryOverride;

        /// <summary>Default storage root: <c>persistentDataPath/assemblies</c>.</summary>
        public static string RootDirectory =>
            string.IsNullOrEmpty(RootDirectoryOverride)
                ? Path.Combine(Application.persistentDataPath, DirectoryName)
                : RootDirectoryOverride;

        /// <summary>Maps a graph onto a snapshot (pure conversion, no validation gate —
        /// run <see cref="AssemblyGraph.Validate"/> first when a validity gate is wanted).</summary>
        public static AssemblySnapshot ToSnapshot(AssemblyGraph graph, string name)
        {
            if (graph == null)
            {
                throw new ArgumentNullException(nameof(graph));
            }

            var snapshot = new AssemblySnapshot
            {
                Name = SanitizeName(name),
                SavedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };

            foreach (AssemblyGraphNode node in graph.Parts)
            {
                var part = new AssemblySnapshotPart
                {
                    PartId = node.PartId,
                    Kind = (int)node.Kind,
                    PositionX = node.Position.x,
                    PositionY = node.Position.y,
                    PositionZ = node.Position.z,
                    RotationX = node.Rotation.x,
                    RotationY = node.Rotation.y,
                    RotationZ = node.Rotation.z,
                    RotationW = node.Rotation.w,
                };

                foreach (AssemblyGraphPointSpec point in node.Points)
                {
                    part.Points.Add(new AssemblySnapshotPoint
                    {
                        Type = (int)point.Type,
                        LocalPositionX = point.LocalPosition.x,
                        LocalPositionY = point.LocalPosition.y,
                        LocalPositionZ = point.LocalPosition.z,
                        LocalForwardX = point.LocalForward.x,
                        LocalForwardY = point.LocalForward.y,
                        LocalForwardZ = point.LocalForward.z,
                    });
                }

                snapshot.Parts.Add(part);
            }

            foreach (AssemblyGraphJoint joint in graph.Joints)
            {
                snapshot.Joints.Add(new AssemblySnapshotJoint
                {
                    JointId = joint.JointId,
                    FromPartId = joint.FromPartId,
                    FromPointIndex = joint.FromPointIndex,
                    ToPartId = joint.ToPartId,
                    ToPointIndex = joint.ToPointIndex,
                    Kind = (int)joint.Kind,
                });
            }

            return snapshot;
        }

        /// <summary>
        /// Maps a snapshot back onto a graph. Gates on format version and
        /// graph validity (unknown version, disconnected graphs, orphans or
        /// duplicate connections throw) — a rebuild must never start from a
        /// structurally broken vehicle.
        /// </summary>
        public static AssemblyGraph FromSnapshot(AssemblySnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (snapshot.Version > CurrentVersion)
            {
                throw new InvalidDataException(
                    $"装配快照版本不受支持：文件 v{snapshot.Version} > 当前 v{CurrentVersion}");
            }

            var graph = new AssemblyGraph();
            foreach (AssemblySnapshotPart part in snapshot.Parts)
            {
                var node = new AssemblyGraphNode
                {
                    PartId = part.PartId,
                    Kind = (AssemblyPartKind)part.Kind,
                    Position = new Vector3((float)part.PositionX, (float)part.PositionY, (float)part.PositionZ),
                    Rotation = new Quaternion(
                        (float)part.RotationX, (float)part.RotationY, (float)part.RotationZ, (float)part.RotationW),
                };

                foreach (AssemblySnapshotPoint point in part.Points)
                {
                    node.Points.Add(new AssemblyGraphPointSpec
                    {
                        Type = (AttachmentPointType)point.Type,
                        LocalPosition = new Vector3(
                            (float)point.LocalPositionX, (float)point.LocalPositionY, (float)point.LocalPositionZ),
                        LocalForward = new Vector3(
                            (float)point.LocalForwardX, (float)point.LocalForwardY, (float)point.LocalForwardZ),
                    });
                }

                if (!graph.AddPart(node))
                {
                    throw new InvalidDataException($"装配快照存在重复/非法零件 id：'{part.PartId}'");
                }
            }

            foreach (AssemblySnapshotJoint joint in snapshot.Joints)
            {
                if (!graph.AddJoint(new AssemblyGraphJoint
                {
                    JointId = joint.JointId,
                    FromPartId = joint.FromPartId,
                    FromPointIndex = joint.FromPointIndex,
                    ToPartId = joint.ToPartId,
                    ToPointIndex = joint.ToPointIndex,
                    Kind = (AssemblyJointKind)joint.Kind,
                }))
                {
                    throw new InvalidDataException($"装配快照的关节非法或与既有关节冲突：joint '{joint.JointId}'");
                }
            }

            AssemblyGraphValidation validation = graph.Validate();
            if (!validation.IsValid)
            {
                throw new InvalidDataException($"装配快照结构校验失败：{validation}");
            }

            return graph;
        }

        /// <summary>Serialises a snapshot to its JSON text (indented, readable diffs).</summary>
        public static string ToJson(AssemblySnapshot snapshot)
        {
            return JsonConvert.SerializeObject(snapshot, Formatting.Indented);
        }

        /// <summary>Parses snapshot JSON text.</summary>
        public static AssemblySnapshot FromJson(string json)
        {
            return JsonConvert.DeserializeObject<AssemblySnapshot>(json);
        }

        /// <summary>File path of a craft name inside the storage root.</summary>
        public static string PathFor(string name)
        {
            return Path.Combine(RootDirectory, SanitizeName(name) + FileExtension);
        }

        /// <summary>
        /// Writes the snapshot to <c>{root}/{name}.json</c>. The craft name is
        /// normalised through <see cref="SanitizeName"/> first (file name and
        /// body stay consistent). Atomic in the same way SaveSystem does it
        /// (the B-1 audit lesson): the full JSON goes to a .tmp file first and
        /// is swapped in only after the write succeeded, so a failure
        /// mid-write can never destroy the previous craft file. Returns the
        /// final path.
        /// </summary>
        public static string Save(AssemblySnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            snapshot.Name = SanitizeName(snapshot.Name);
            string path = PathFor(snapshot.Name);
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tmp = path + TempSuffix;
            File.WriteAllText(tmp, ToJson(snapshot));
            if (File.Exists(path))
            {
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }

            return path;
        }

        /// <summary>Reads <c>{root}/{name}.json</c>; throws FileNotFoundException when absent.</summary>
        public static AssemblySnapshot Load(string name)
        {
            string path = PathFor(name);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"装配文件不存在：{path}", path);
            }

            return FromJson(File.ReadAllText(path));
        }

        /// <summary>Deletes <c>{root}/{name}.json</c>; returns true when a file was removed.</summary>
        public static bool Delete(string name)
        {
            string path = PathFor(name);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }

        /// <summary>
        /// Craft-file name discipline: [A-Za-z0-9-_ ] kept (spaces become
        /// underscores), everything else collapsed to '_', trimmed, capped at
        /// 64 chars, empty input falls back to "assembly".
        /// </summary>
        public static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return FallbackName;
            }

            var sb = new StringBuilder(name.Length);
            foreach (char c in name.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                {
                    sb.Append(c);
                }
                else if (c == ' ')
                {
                    sb.Append('_');
                }
                else
                {
                    sb.Append('_');
                }
            }

            string result = sb.ToString().Trim('_');
            if (result.Length == 0)
            {
                return FallbackName;
            }

            if (result.Length > MaxNameLength)
            {
                result = result.Substring(0, MaxNameLength);
            }

            return result.ToLowerInvariant();
        }

        /// <summary>Culture-invariant parse helper (kept for future tooling).</summary>
        internal static string FormatInvariant(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
