#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;

namespace ZFrame.Data.ConfigEditor
{
    /// <summary>
    /// Minimal self-written CSV reader/writer (no third-party dependency).
    ///
    /// Supported (基本集): comma separator; CRLF/LF/CR line breaks; fields
    /// wrapped in double quotes may contain commas, quotes (escaped as "")
    /// and line breaks; a single optional UTF-8 BOM at the start is skipped.
    ///
    /// Limitations (deliberate, documented): no configurable delimiter or
    /// comment lines; no trimming of spaces around unquoted fields (values
    /// are taken verbatim); a quote appearing mid-field of an unquoted field
    /// is kept literally; empty trailing lines are dropped.
    /// </summary>
    public static class CsvUtility
    {
        /// <summary>Parses CSV text into rows of raw cell strings.</summary>
        public static List<string[]> Parse(string text)
        {
            var rows = new List<string[]>();
            if (string.IsNullOrEmpty(text))
            {
                return rows;
            }

            var cells = new List<string>();
            var cell = new StringBuilder();
            var inQuotes = false;
            var i = 0;
            if (text[0] == '')
            {
                i = 1; // tolerate a UTF-8 BOM
            }

            var cellHasContent = false;
            while (i < text.Length)
            {
                var c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            cell.Append('"');
                            i += 2;
                            continue;
                        }

                        inQuotes = false;
                        i++;
                        continue;
                    }

                    cell.Append(c);
                    i++;
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inQuotes = true;
                        cellHasContent = true; // an explicit quote marks the field as intentional
                        i++;
                        continue;
                    case ',':
                        cells.Add(cell.ToString());
                        cell.Length = 0;
                        cellHasContent = false;
                        i++;
                        continue;
                    case '\r':
                    case '\n':
                        if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                        {
                            i++;
                        }

                        cells.Add(cell.ToString());
                        cell.Length = 0;
                        cellHasContent = false;
                        rows.Add(cells.ToArray());
                        cells.Clear();
                        i++;
                        continue;
                    default:
                        cell.Append(c);
                        cellHasContent = true;
                        i++;
                        continue;
                }
            }

            if (inQuotes)
            {
                throw new FormatException("CSV 解析失败: 存在未闭合的引号");
            }

            if (cellHasContent || cells.Count > 0)
            {
                cells.Add(cell.ToString());
                rows.Add(cells.ToArray());
            }

            return rows;
        }

        /// <summary>Escapes one field for CSV output (quotes only when needed).</summary>
        public static string Escape(string field)
        {
            field ??= "";
            var needsQuotes = field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            if (!needsQuotes)
            {
                return field;
            }

            var sb = new StringBuilder(field.Length + 2);
            sb.Append('"');
            foreach (var c in field)
            {
                if (c == '"')
                {
                    sb.Append('"');
                }

                sb.Append(c);
            }

            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>Joins fields into one CSV line.</summary>
        public static string JoinLine(string[] fields)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(Escape(fields[i]));
            }

            return sb.ToString();
        }
    }
}
#endif
