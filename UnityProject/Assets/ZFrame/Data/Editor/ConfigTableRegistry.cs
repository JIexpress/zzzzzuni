#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ZFrame.Data.Config;

namespace ZFrame.Data.ConfigEditor
{
    /// <summary>
    /// csv -> concrete table type registry, discovered by reflection (TASK-A-CONF-02).
    ///
    /// Replaces the hand-maintained `TableByCsvName` dictionary of TASK-A-CONF-01:
    /// every non-abstract class deriving <see cref="ConfigTable{TRow}"/> is a
    /// registration, keyed by its class name minus the "ConfigTable" suffix,
    /// lower-cased (EnemyConfigTable -> "enemy", WaveConfigTable -> "wave").
    /// Adding a table = dropping a csv; the generated shell appears and is
    /// registered without editing any registry line.
    ///
    /// Determinism: candidates are sorted by (assembly name, full type name)
    /// before keying, so duplicate keys always resolve to the same type and the
    /// losers are reported through <see cref="Conflicts"/> instead of silently
    /// depending on assembly load order.
    /// </summary>
    public static class ConfigTableRegistry
    {
        private static readonly object Gate = new object();
        private static Dictionary<string, Type> _byCsvName;
        private static List<Type> _tableTypes;
        private static List<string> _conflicts;

        /// <summary>All registered table types (stable order: assembly, then full name).</summary>
        public static IReadOnlyList<Type> TableTypes
        {
            get
            {
                EnsureBuilt();
                return _tableTypes;
            }
        }

        /// <summary>Duplicate registration keys found while scanning (should stay empty).</summary>
        public static IReadOnlyList<string> Conflicts
        {
            get
            {
                EnsureBuilt();
                return _conflicts;
            }
        }

        /// <summary>csv stem (lower case) -> table type.</summary>
        public static IReadOnlyDictionary<string, Type> ByCsvName
        {
            get
            {
                EnsureBuilt();
                return _byCsvName;
            }
        }

        /// <summary>
        /// Resolves a csv path ("Assets/Configs/CSV/enemy.csv" or "enemy.csv")
        /// or a bare stem ("enemy") to its table type.
        /// </summary>
        public static bool TryGetTableType(string csvPathOrName, out Type tableType)
        {
            tableType = null;
            var key = CsvKeyOf(csvPathOrName);
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            EnsureBuilt();
            return _byCsvName.TryGetValue(key, out tableType);
        }

        /// <summary>Registry key of a csv path / name (file stem, lower case).</summary>
        public static string CsvKeyOf(string csvPathOrName)
        {
            if (string.IsNullOrEmpty(csvPathOrName))
            {
                return null;
            }

            var name = csvPathOrName.Replace('\\', '/');
            var slash = name.LastIndexOf('/');
            if (slash >= 0)
            {
                name = name.Substring(slash + 1);
            }

            return Path.GetFileNameWithoutExtension(name)?.ToLowerInvariant();
        }

        /// <summary>Registry key of a table type (WaveConfigTable -> "wave").</summary>
        public static string KeyOf(Type tableType)
        {
            var rowType = FindRowType(tableType);
            if (rowType == null)
            {
                return null;
            }

            var name = tableType.Name;
            if (name.EndsWith(ConfigTableCodegen.TableSuffix, StringComparison.Ordinal))
            {
                name = name.Substring(0, name.Length - ConfigTableCodegen.TableSuffix.Length);
            }

            return name.ToLowerInvariant();
        }

        /// <summary>Row type of a table type (walks the base chain to ConfigTable&lt;TRow&gt;).</summary>
        public static Type FindRowType(Type tableType)
        {
            for (var t = tableType; t != null && t != typeof(object); t = t.BaseType)
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ConfigTable<>))
                {
                    return t.GetGenericArguments()[0];
                }
            }

            return null;
        }

        /// <summary>Table type whose row type is <paramref name="rowType"/> (or null).</summary>
        public static Type FindTableTypeForRow(Type rowType)
        {
            if (rowType == null)
            {
                return null;
            }

            EnsureBuilt();
            foreach (var tableType in _tableTypes)
            {
                if (FindRowType(tableType) == rowType)
                {
                    return tableType;
                }
            }

            return null;
        }

        /// <summary>Drops the cache (tests / after a script reload that added types).</summary>
        public static void Invalidate()
        {
            lock (Gate)
            {
                _byCsvName = null;
                _tableTypes = null;
                _conflicts = null;
            }
        }

        private static void EnsureBuilt()
        {
            lock (Gate)
            {
                if (_byCsvName != null)
                {
                    return;
                }

                var candidates = new List<Type>();
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try
                    {
                        types = assembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        types = ex.Types ?? Array.Empty<Type>();
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    foreach (var type in types)
                    {
                        if (type == null || !type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition)
                        {
                            continue;
                        }

                        if (FindRowType(type) != null)
                        {
                            candidates.Add(type);
                        }
                    }
                }

                candidates.Sort((left, right) =>
                {
                    var byAssembly = string.CompareOrdinal(left.Assembly.GetName().Name,
                        right.Assembly.GetName().Name);
                    return byAssembly != 0
                        ? byAssembly
                        : string.CompareOrdinal(left.FullName, right.FullName);
                });

                var byName = new Dictionary<string, Type>(StringComparer.Ordinal);
                var conflicts = new List<string>();
                foreach (var type in candidates)
                {
                    var key = KeyOf(type);
                    if (string.IsNullOrEmpty(key))
                    {
                        continue;
                    }

                    if (byName.TryGetValue(key, out var existing))
                    {
                        conflicts.Add($"'{key}': {existing.FullName} 与 {type.FullName} 重复（保留前者）");
                        continue;
                    }

                    byName[key] = type;
                }

                _tableTypes = candidates;
                _conflicts = conflicts;
                _byCsvName = byName;
            }
        }
    }
}
#endif
