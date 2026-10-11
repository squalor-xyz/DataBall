// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;

namespace squalor.DataBall
{
    /// <summary>What an input file turned out to be.</summary>
    internal enum OpenKind
    {
        /// <summary>A snapshot pointer (<c>&lt;name&gt;.latest</c>).</summary>
        Pointer,

        /// <summary>A native DuckDB <c>.ball</c>.</summary>
        Native,

        /// <summary>Anything else: <see cref="OpenEntry.Report"/> says which handler or built-in path takes it.</summary>
        Detected,
    }

    /// <summary>One thing an open reads: a file, or a hive Parquet directory as a whole.</summary>
    internal sealed class OpenEntry
    {
        internal OpenEntry(string path, bool explicitlyNamed, long bytes, string? typedPath = null, IReadOnlyList<string>? hiveFiles = null)
        {
            Path = path;
            OpenPath = typedPath ?? path;
            Explicit = explicitlyNamed;
            HiveFiles = hiveFiles;
            Bytes = bytes;
        }

        /// <summary>The full path: the key that sorts and deduplicates entries.</summary>
        internal string Path { get; }

        /// <summary>The path as the caller typed it for a file or directory named as an input, else the full path; what detection and the readers get.</summary>
        internal string OpenPath { get; }

        /// <summary>True when the caller named it, false when a directory or a glob produced it.</summary>
        internal bool Explicit { get; }

        /// <summary>The data files of a directory named as an input whose files are all Parquet (one source); null for anything else.</summary>
        internal IReadOnlyList<string>? HiveFiles { get; }

        internal bool Hive => HiveFiles is not null;

        internal long Bytes { get; }

        internal OpenKind Kind { get; set; } = OpenKind.Detected;

        /// <summary>The generation file a snapshot pointer names.</summary>
        internal string? PointerTarget { get; set; }

        internal DetectionReport? Report { get; set; }
    }

    /// <summary>The expanded, sorted, detected inputs of an open.</summary>
    internal sealed class OpenPlan
    {
        internal OpenPlan(List<OpenEntry> entries, List<string> ignored)
        {
            Entries = entries;
            Ignored = ignored;
        }

        /// <summary>Entries to open, sorted by full path (ordinal), duplicates removed.</summary>
        internal List<OpenEntry> Entries { get; }

        /// <summary>Files from a directory or glob that detected as no format.</summary>
        internal List<string> Ignored { get; }

        /// <summary>Total input bytes the store is sized over; each file and each directory is measured once.</summary>
        internal long Bytes => Entries.Sum(e => e.Bytes);
    }

    /// <summary>Expands the inputs of <c>Open</c> into entries and detects each one.</summary>
    internal static class OpenInputs
    {
        private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>
        /// An existing file is itself. A directory named as an input whose files (at any depth) are all <c>.parquet</c> is one hive source;
        /// any other directory yields its files, those of its subdirectories too with <paramref name="recursive"/>. A pattern with <c>*</c> or
        /// <c>?</c> in its last segment yields the matching files (case-insensitive), in subdirectories too with <paramref name="recursive"/>.
        /// The result is sorted by full path (ordinal) with duplicates removed; files that detect as no format are ignored when expanded and
        /// fail the open when named.
        /// </summary>
        internal static OpenPlan Plan(IReadOnlyList<string> inputs, bool recursive)
        {
            if (inputs is null || inputs.Count == 0)
                throw new DataBallException("At least one input path is required");

            var found = new Dictionary<string, OpenEntry>(PathComparer);
            var strays = new List<string>();
            foreach (var input in inputs)
            {
                if (string.IsNullOrWhiteSpace(input))
                    throw new DataBallException("Path is required");
                try
                {
                    Expand(input, recursive, found, strays);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new DataBallException($"Cannot read '{input}': {ex.Message}", ex);
                }
            }

            var entries = new List<OpenEntry>();
            var ignored = new List<string>();
            var hives = found.Values.Where(e => e.Hive)
                .Select(h => (Hive: h, Files: new HashSet<string>(h.HiveFiles!, PathComparer)))
                .ToList();
            foreach (var entry in found.Values.Where(e => !IsReadByAHiveSource(e, hives)).OrderBy(e => e.Path, StringComparer.Ordinal))
            {
                try
                {
                    if (Classify(entry))
                        entries.Add(entry);
                    else
                        ignored.Add(entry.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new DataBallException($"Cannot read '{entry.Path}': {ex.Message}", ex);
                }
            }

            // Stray files and unrecognized files are reported together, sorted.
            var reported = ignored.Concat(strays.Where(p => !found.ContainsKey(p))).Distinct(PathComparer).OrderBy(p => p, StringComparer.Ordinal).ToList();
            ignored.Clear();
            ignored.AddRange(reported);
            if (entries.Count == 0)
            {
                if (inputs.Count == 1 && ignored.Count == 0 && Directory.Exists(inputs[0]))
                {
                    throw new DataBallException(
                        $"{inputs[0]} is not a hive Parquet directory and holds no files to open"
                        + (recursive ? "" : " (OpenOptions.Recursive includes subdirectories)"));
                }
                throw new DataBallException(ignored.Count > 0
                    ? $"No file to open: {ignored.Count} file(s) were not recognized as any format ({string.Join(", ", ignored.Take(3))}{(ignored.Count > 3 ? ", ..." : "")})"
                    : $"No files to open for: {string.Join(", ", inputs)}");
            }
            return new OpenPlan(entries, ignored);
        }

        private static void Expand(string input, bool recursive, Dictionary<string, OpenEntry> found, List<string> strays)
        {
            if (Directory.Exists(input))
            {
                ExpandDirectory(input, recursive, found, strays);
                return;
            }
            if (File.Exists(input))
            {
                Add(found, new OpenEntry(Path.GetFullPath(input), true, new FileInfo(input).Length, input));
                return;
            }

            var directoryPart = Path.GetDirectoryName(input);
            if (directoryPart is not null && HasWildcard(directoryPart))
                throw new DataBallException($"A wildcard is allowed in the last path segment only: {input}");
            var pattern = Path.GetFileName(input);
            if (!HasWildcard(pattern))
                throw new DataBallException($"File not found: {input}");
            var directory = string.IsNullOrEmpty(directoryPart) ? "." : directoryPart;
            if (!Directory.Exists(directory))
                throw new DataBallException($"Directory not found: {directory}");
            var options = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            {
                if (FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(file), ignoreCase: true))
                    Add(found, new OpenEntry(Path.GetFullPath(file), false, new FileInfo(file).Length));
            }
        }

        private static void ExpandDirectory(string input, bool recursive, Dictionary<string, OpenEntry> found, List<string> strays)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
            var top = Directory.EnumerateFiles(full, "*", SearchOption.TopDirectoryOnly).ToList();
            // A hive directory holds only Parquet data files (stray files, see HiveFiles, do not count). Only when its top level
            // allows that is the rest of the tree walked; Recursive walks it anyway, to expand it.
            var topLevelMayBeHive = top.Where(f => !HiveFiles.IsStray(full, f)).All(f => f.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase));
            var files = recursive || topLevelMayBeHive
                ? Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).ToList()
                : top;
            var data = files.Where(f => !HiveFiles.IsStray(full, f)).ToList();
            if (topLevelMayBeHive && data.Count > 0 && data.All(f => f.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase)))
            {
                var hiveFiles = data.OrderBy(f => f, StringComparer.Ordinal).ToList();
                Add(found, new OpenEntry(full, true, hiveFiles.Sum(f => new FileInfo(f).Length), input, hiveFiles));
                return;
            }
            foreach (var file in files)
            {
                if (!recursive && !PathComparer.Equals(Path.GetDirectoryName(file), full))
                    continue;
                if (HiveFiles.IsStray(full, file))
                    strays.Add(file);
                else
                    Add(found, new OpenEntry(file, false, new FileInfo(file).Length));
            }
        }

        private static void Add(Dictionary<string, OpenEntry> found, OpenEntry entry)
        {
            if (!found.TryGetValue(entry.Path, out var existing) || (entry.Explicit && !existing.Explicit))
                found[entry.Path] = entry;
        }

        /// <summary>
        /// True when a hive directory above <paramref name="entry"/> already reads every file of it (a file the hive lists, or
        /// a hive nested in it): reading both would read its rows twice. A file the hive leaves out, such as a named stray
        /// <c>_tmp.parquet</c> inside it, is not read by the hive and stays an entry of its own.
        /// </summary>
        private static bool IsReadByAHiveSource(OpenEntry entry, List<(OpenEntry Hive, HashSet<string> Files)> hives) =>
            hives.Any(h => !ReferenceEquals(h.Hive, entry)
                && entry.Path.StartsWith(h.Hive.Path + Path.DirectorySeparatorChar, PathComparison)
                && (entry.Hive ? entry.HiveFiles!.All(h.Files.Contains) : h.Files.Contains(entry.Path)));

        private static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static bool HasWildcard(string segment) => segment.AsSpan().IndexOfAny('*', '?') >= 0;

        /// <summary>
        /// Reads what <paramref name="entry"/> is. False when it came from expansion and is no known format.
        /// Throws for an explicitly named file that is no known format, and for ambiguous handler matches.
        /// </summary>
        private static bool Classify(OpenEntry entry)
        {
            if (!entry.Hive)
            {
                var target = SnapshotFiles.TryReadPointer(entry.OpenPath);
                if (target is not null)
                {
                    entry.Kind = OpenKind.Pointer;
                    entry.PointerTarget = target;
                    return true;
                }
                if (DataBall.IsNativeBall(entry.OpenPath))
                {
                    entry.Kind = OpenKind.Native;
                    return true;
                }
            }

            var report = DataBall.Detect(entry.OpenPath);
            if (report.Outcome == DetectionOutcome.Ambiguous)
                throw new DataBallException(report.ToString());
            if (report.Outcome == DetectionOutcome.None)
            {
                if (entry.Explicit)
                    throw new DataBallException(report.ToString());
                return false;
            }
            entry.Report = report;
            return true;
        }
    }
}
