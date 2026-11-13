#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ZFrame.Data.ConfigEditor
{
    /// <summary>Column value types supported by the config schema / codegen.</summary>
    public enum ConfigColumnType
    {
        String,
        Int,
        Float,
        Bool,
    }

    /// <summary>CLR mapping helpers for <see cref="ConfigColumnType"/>.</summary>
    public static class ConfigColumnTypeUtil
    {
        /// <summary>Maps a schema type to the CLR property type it generates.</summary>
        public static Type ToClrType(ConfigColumnType type)
        {
            switch (type)
            {
                case ConfigColumnType.Int: return typeof(int);
                case ConfigColumnType.Float: return typeof(float);
                case ConfigColumnType.Bool: return typeof(bool);
                default: return typeof(string);
            }
        }

        /// <summary>Canonical lower-case schema token ("string" / "int" / "float" / "bool").</summary>
        public static string ToSchemaToken(ConfigColumnType type)
        {
            switch (type)
            {
                case ConfigColumnType.Int: return "int";
                case ConfigColumnType.Float: return "float";
                case ConfigColumnType.Bool: return "bool";
                default: return "string";
            }
        }
    }

    /// <summary>
    /// One parsed header cell. Grammar: <c>Name</c> (bare, importer legacy),
    /// <c>Name:type</c> or <c>Name:type[min..max]</c> (Range, int/float only;
    /// ".." instead of a comma so the cell survives plain CSV splitting).
    /// Type tokens are case-insensitive and restricted to string/int/float/bool.
    /// </summary>
    public sealed class ConfigHeaderCell
    {
        /// <summary>Column / property name (verbatim, before the ':' annotation).</summary>
        public string Name { get; }

        /// <summary>True when the cell carried a <c>:type</c> annotation.</summary>
        public bool HasType { get; }

        /// <summary>Annotated type (valid when <see cref="HasType"/>).</summary>
        public ConfigColumnType Type { get; }

        /// <summary>True when the cell carried a <c>[min,max]</c> range annotation.</summary>
        public bool HasRange { get; }

        /// <summary>Range minimum (valid when <see cref="HasRange"/>).</summary>
        public double RangeMin { get; }

        /// <summary>Range maximum (valid when <see cref="HasRange"/>).</summary>
        public double RangeMax { get; }

        public ConfigHeaderCell(
            string name, bool hasType, ConfigColumnType type,
            bool hasRange, double rangeMin, double rangeMax)
        {
            Name = name;
            HasType = hasType;
            Type = type;
            HasRange = hasRange;
            RangeMin = rangeMin;
            RangeMax = rangeMax;
        }
    }

    /// <summary>
    /// Parsed schema of one config table row type: the annotated CSV header
    /// row (first column must be <c>Id</c>) plus the class name to generate.
    /// All problems are collected in <see cref="Errors"/> — an invalid schema
    /// never reaches the code emitter (bad header => error, no broken code).
    /// </summary>
    public sealed class ConfigRowSchema
    {
        /// <summary>Source table path recorded in the generated file header.</summary>
        public string SourceCsvPath { get; }

        /// <summary>Row class name to emit (Pascal csv stem + "ConfigRow").</summary>
        public string RowClassName { get; }

        /// <summary>Column schemas in header order, excluding the implicit Id column.</summary>
        public List<ConfigHeaderCell> Columns { get; } = new List<ConfigHeaderCell>();

        /// <summary>All validation problems found (empty => valid schema).</summary>
        public List<string> Errors { get; } = new List<string>();

        /// <summary>True when no validation errors were collected.</summary>
        public bool IsValid
        {
            get { return Errors.Count == 0; }
        }

        private ConfigRowSchema(string sourceCsvPath, string rowClassName)
        {
            SourceCsvPath = sourceCsvPath;
            RowClassName = rowClassName;
        }

        /// <summary>
        /// Parses and validates a header row into a schema. Never throws —
        /// every problem lands in <see cref="Errors"/>.
        /// </summary>
        public static ConfigRowSchema Parse(IReadOnlyList<string> headerCells, string sourceCsvPath,
            string rowClassName)
        {
            var schema = new ConfigRowSchema(sourceCsvPath, rowClassName);

            if (string.IsNullOrEmpty(rowClassName) || !IsValidIdentifier(rowClassName))
            {
                schema.Errors.Add($"行类名 '{rowClassName}' 不是合法 C# 标识符");
            }

            if (headerCells == null || headerCells.Count == 0)
            {
                schema.Errors.Add("CSV 为空（无表头）");
                return schema;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < headerCells.Count; c++)
            {
                var raw = headerCells[c];
                if (!TryParseHeaderCell(raw, out var cell, out var error))
                {
                    schema.Errors.Add($"第 {c} 列 '{raw}' 非法: {error}");
                    continue;
                }

                if (c == 0)
                {
                    // First column is the implicit Id (ConfigRow base property).
                    if (!string.Equals(cell.Name, "Id", StringComparison.Ordinal))
                    {
                        schema.Errors.Add($"表头首列必须为 Id，实际为 '{cell.Name}'");
                        continue;
                    }

                    if (cell.HasType && cell.Type != ConfigColumnType.String)
                    {
                        schema.Errors.Add(
                            $"Id 列类型标注只能是 string，实际为 '{ConfigColumnTypeUtil.ToSchemaToken(cell.Type)}'");
                        continue;
                    }
                }
                else
                {
                    if (!cell.HasType)
                    {
                        schema.Errors.Add($"列 '{cell.Name}' 缺少类型标注（格式: 名称:string|int|float|bool[min..max]）");
                        continue;
                    }

                    if (seen.Contains(cell.Name))
                    {
                        schema.Errors.Add($"列名 '{cell.Name}' 重复");
                        continue;
                    }

                    if (IsCSharpKeyword(cell.Name))
                    {
                        schema.Errors.Add($"列名 '{cell.Name}' 是 C# 关键字，请改名");
                        continue;
                    }
                }

                seen.Add(cell.Name);
                if (c > 0)
                {
                    schema.Columns.Add(cell);
                }
            }

            if (schema.Columns.Count == 0 && schema.IsValid)
            {
                schema.Errors.Add("表头除 Id 外没有任何列");
            }

            return schema;
        }

        /// <summary>
        /// Parses one header cell (bare / typed / typed+range). Returns false
        /// with a human-readable error for anything it cannot understand.
        /// </summary>
        public static bool TryParseHeaderCell(string raw, out ConfigHeaderCell parsed, out string error)
        {
            parsed = null;
            error = null;
            if (string.IsNullOrEmpty(raw))
            {
                error = "表头列为空";
                return false;
            }

            string name;
            string annotation = null;
            var colon = raw.IndexOf(':');
            if (colon >= 0)
            {
                name = raw.Substring(0, colon);
                annotation = raw.Substring(colon + 1);
            }
            else
            {
                name = raw;
            }

            if (!IsValidIdentifier(name))
            {
                error = $"'{name}' 不是合法 C# 标识符";
                return false;
            }

            var hasType = false;
            var type = ConfigColumnType.String;
            var hasRange = false;
            double rangeMin = 0;
            double rangeMax = 0;

            if (annotation != null)
            {
                var bracket = annotation.IndexOf('[');
                var typeToken = bracket >= 0 ? annotation.Substring(0, bracket) : annotation;
                var rangeText = bracket >= 0 ? annotation.Substring(bracket) : null;

                if (!TryMapTypeToken(typeToken, out type))
                {
                    error = $"类型标注 '{typeToken}' 未识别（支持: string/int/float/bool）";
                    return false;
                }

                if (rangeText != null)
                {
                    if (type == ConfigColumnType.String || type == ConfigColumnType.Bool)
                    {
                        error = $"{ConfigColumnTypeUtil.ToSchemaToken(type)} 列不支持 Range 标注";
                        return false;
                    }

                    if (rangeText.Length < 3 || !rangeText.EndsWith("]", StringComparison.Ordinal))
                    {
                        error = $"Range 标注 '{rangeText}' 缺少 [min..max] 结构";
                        return false;
                    }

                    var inner = rangeText.Substring(1, rangeText.Length - 2);
                    var separator = inner.IndexOf("..", StringComparison.Ordinal);
                    if (separator <= 0 || separator + 2 >= inner.Length)
                    {
                        error = $"Range 标注 '{rangeText}' 缺少 [min..max] 结构";
                        return false;
                    }

                    var minText = inner.Substring(0, separator);
                    var maxText = inner.Substring(separator + 2);
                    if (!double.TryParse(minText, NumberStyles.Float, CultureInfo.InvariantCulture, out rangeMin)
                        || !double.TryParse(maxText, NumberStyles.Float, CultureInfo.InvariantCulture, out rangeMax))
                    {
                        error = $"Range 标注 '{rangeText}' 无法解析为两个不变文化数字";
                        return false;
                    }

                    if (rangeMin > rangeMax)
                    {
                        error = $"Range 下限 {rangeMin} 大于上限 {rangeMax}";
                        return false;
                    }

                    if (type == ConfigColumnType.Int
                        && (Math.Floor(rangeMin) != rangeMin || Math.Floor(rangeMax) != rangeMax
                            || rangeMin < int.MinValue || rangeMax > int.MaxValue))
                    {
                        error = $"int 列 Range 必须是 int 范围内的整数，实际为 '{rangeText}'";
                        return false;
                    }

                    hasRange = true;
                }

                hasType = true;
            }

            parsed = new ConfigHeaderCell(name, hasType, type, hasRange, rangeMin, rangeMax);
            return true;
        }

        /// <summary>ASCII C# identifier check (letters / underscore start).</summary>
        public static bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            return Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$");
        }

        private static bool TryMapTypeToken(string token, out ConfigColumnType type)
        {
            switch (token?.Trim().ToLowerInvariant())
            {
                case "string": type = ConfigColumnType.String; return true;
                case "int": type = ConfigColumnType.Int; return true;
                case "float": type = ConfigColumnType.Float; return true;
                case "bool": type = ConfigColumnType.Bool; return true;
                default: type = ConfigColumnType.String; return false;
            }
        }

        private static bool IsCSharpKeyword(string name)
        {
            switch (name)
            {
                case "abstract": case "as": case "base": case "bool": case "break": case "byte":
                case "case": case "catch": case "char": case "checked": case "class": case "const":
                case "continue": case "decimal": case "default": case "delegate": case "do":
                case "double": case "else": case "enum": case "event": case "explicit": case "extern":
                case "false": case "finally": case "fixed": case "float": case "for": case "foreach":
                case "goto": case "if": case "implicit": case "in": case "int": case "interface":
                case "internal": case "is": case "lock": case "long": case "namespace": case "new":
                case "null": case "object": case "operator": case "out": case "override": case "params":
                case "private": case "protected": case "public": case "readonly": case "ref":
                case "return": case "sbyte": case "sealed": case "short": case "sizeof":
                case "stackalloc": case "static": case "string": case "struct": case "switch":
                case "this": case "throw": case "true": case "try": case "typeof": case "uint":
                case "ulong": case "unchecked": case "unsafe": case "ushort": case "using":
                case "virtual": case "void": case "volatile": case "while":
                    return true;
                default:
                    return false;
            }
        }
    }
}
#endif
