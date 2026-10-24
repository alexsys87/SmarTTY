using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SmarTTY.IO;

/// <summary>
/// Reads the output of <c>df -h</c> (GNU coreutils and BusyBox, as on OpenWrt) into rows. The columns are
/// "Filesystem Size Used Avail Use% Mounted on"; BusyBox wraps a long file system name onto its own line, and a mount
/// point may contain spaces, so a row is located by its "Use%" column instead of by a fixed column count.
/// </summary>
public static class DfOutputParser
{
    public sealed class DiskUsage
    {
        public string Filesystem { get; set; }

        public string Size { get; set; }

        public string Used { get; set; }

        public string Available { get; set; }

        /// <summary>The "Use%" column as a number: how much of the space that can be used is taken (0 to 100).</summary>
        public int UsePercent { get; set; }

        public string MountPoint { get; set; }

        /// <summary>The share of the space that is still free (0 to 100).</summary>
        public int FreePercent => Math.Max(0, Math.Min(100, 100 - UsePercent));
    }

    private static readonly Regex PercentColumn = new Regex(@"^\d{1,3}%$", RegexOptions.Compiled);

    // 12G, 8.8M, 100.0K, 1,5T (a decimal comma), 0, 512B, 1.2Gi
    private static readonly Regex SizeColumn = new Regex(@"^\d+([.,]\d+)?[KMGTPEZYkmgtpezy]?[iI]?[bB]?$", RegexOptions.Compiled);

    /// <summary>
    /// Returns the usable rows of the output, in the order they were printed. Rows of pseudo file systems that report
    /// no size ("-" instead of a percentage) and every line that is not a table row (the header, an error message) are left out.
    /// </summary>
    public static List<DiskUsage> Parse(IEnumerable<string> lines)
    {
        List<DiskUsage> rows = new List<DiskUsage>();
        if (lines == null)
        {
            return rows;
        }
        string pending = null;
        foreach (string rawLine in lines)
        {
            string line = (rawLine ?? "").Trim();
            if (line.Length == 0)
            {
                continue;
            }
            if (line.StartsWith("Filesystem", StringComparison.OrdinalIgnoreCase) && line.IndexOf("Mounted", StringComparison.OrdinalIgnoreCase) > 0)
            {
                pending = null;
                continue;
            }
            string[] tokens = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (pending != null)
            {
                // The previous line held only the file system name: the figures follow on this one.
                line = pending + " " + line;
                tokens = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                pending = null;
            }
            else if (tokens.Length == 1)
            {
                pending = line;
                continue;
            }
            DiskUsage row = ParseRow(tokens);
            if (row != null)
            {
                rows.Add(row);
            }
        }
        return rows;
    }

    private static DiskUsage ParseRow(string[] tokens)
    {
        // The file system name comes first and may hold spaces; the three sizes and the percentage follow it.
        for (int percentIndex = 4; percentIndex < tokens.Length; percentIndex++)
        {
            if (!PercentColumn.IsMatch(tokens[percentIndex]) ||
                !SizeColumn.IsMatch(tokens[percentIndex - 1]) ||
                !SizeColumn.IsMatch(tokens[percentIndex - 2]) ||
                !SizeColumn.IsMatch(tokens[percentIndex - 3]))
            {
                continue;
            }
            int usePercent = int.Parse(tokens[percentIndex].TrimEnd('%'), CultureInfo.InvariantCulture);
            return new DiskUsage
            {
                Filesystem = string.Join(" ", tokens, 0, percentIndex - 3),
                Size = tokens[percentIndex - 3],
                Used = tokens[percentIndex - 2],
                Available = tokens[percentIndex - 1],
                UsePercent = Math.Min(100, usePercent),
                MountPoint = percentIndex + 1 < tokens.Length ? string.Join(" ", tokens, percentIndex + 1, tokens.Length - percentIndex - 1) : ""
            };
        }
        return null;
    }

    /// <summary>The row to show in the bar: the file system mounted on "/", or the first row when there is none.</summary>
    public static DiskUsage SelectMain(IList<DiskUsage> rows)
    {
        if (rows == null || rows.Count == 0)
        {
            return null;
        }
        foreach (DiskUsage row in rows)
        {
            if (row.MountPoint == "/")
            {
                return row;
            }
        }
        return rows[0];
    }
}
