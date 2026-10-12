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
        internal OpenPlan(List<OpenEntry> entries, List<string> ignored, List<SkippedFile> skipped)
        {
            Entries = entries;
            Ignored = ignored;
            Skipped = skipped;
        }

        /// <summary>Entries to open, sorted by full path (ordinal), duplicates removed.</summary>
        internal List<OpenEntry> Entries { get; }

        /// <summary>Files from a directory or glob that detected as no format.</summary>
        internal List<string> Ignored { get; }

        /// <summary>Named or expanded files that could not be read or recognized and were skipped under <see cref="BadFilePolicy.Skip"/>.</summary>
        internal List<SkippedFile> Skipped { get; }

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
        /// <c>?</c> in its last segment yields the matching files (case-insensitive), in subdirectories too with <paramref name="recursive"/>,
        /// except stray files (see <see cref="HiveFiles.IsStray"/>, relative to the pattern's directory).
        /// The result is sorted by full path (ordinal) with duplicates removed. Files that detect as no format are ignored when expanded and
        /// fail the open when named. Under <see cref="BadFilePolicy.Skip"/> a named file with no known format, any ambiguous file and any file
        /// that cannot be read are skipped instead; an expanded file with no known format stays ignored.
        /// </summary>
        internal static OpenPlan Plan(IReadOnlyList<string> inputs, bool recursive, BadFilePolicy policy)
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
            var skipped = new List<SkippedFile>();
            var hives = found.Values.Where(e => e.Hive)
                .Select(h => (Hive: h, Files: new HashSet<string>(h.HiveFiles!, PathComparer)))
                .ToList();
            foreach (var entry in found.Values.Where(e => !IsReadByAHiveSource(e, hives)).OrderBy(e => e.Path, StringComparer.Ordinal))
            {
                Reading reading;
                DetectionReport? report;
                try
                {
                    reading = Classify(entry, out report);
                }
                catch (Exception ex) when (policy == BadFilePolicy.Skip && (ex is IOException or UnauthorizedAccessException or DataBallException))
                {
                    skipped.Add(new SkippedFile(entry.Path, ex.Message, null, null, null));
                    continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new DataBallException($"Cannot read '{entry.Path}': {ex.Message}", ex);
                }
                switch (reading)
                {
                    case Reading.Known:
                        entries.Add(entry);
                        break;
                    case Reading.Unrecognized when !entry.Explicit:
                        ignored.Add(entry.Path);
                        break;
                    case Reading.Unrecognized when policy == BadFilePolicy.Skip:
                        skipped.Add(new SkippedFile(entry.Path, report!.Reason, null, null, null));
                        break;
                    case Reading.Ambiguous when policy == BadFilePolicy.Skip:
                        skipped.Add(new SkippedFile(entry.Path, report!.ToString(), null, null, null));
                        break;
                    default:
                        throw new DataBallException(report!.ToString());
                }
            }

            // Unrecognized and stray files are both ignored; the message gives each its own wording, so they are kept apart.
            var strayPaths = strays.Where(p => !found.ContainsKey(p)).Distinct(PathComparer).OrderBy(p => p, StringComparer.Ordinal).ToList();
            var reported = ignored.Concat(strayPaths).Distinct(PathComparer).OrderBy(p => p, StringComparer.Ordinal).ToList();
            if (entries.Count == 0)
            {
                if (skipped.Count > 0)
                    throw new DataBallException($"No file to open: every input was skipped as bad: {Every(skipped.Select(s => s.Path))}");
                if (inputs.Count == 1 && reported.Count == 0 && Directory.Exists(inputs[0]))
                {
                    throw new DataBallException(
                        $"{inputs[0]} is not a hive Parquet directory and holds no files to open"
                        + (recursive ? "" : " (OpenOptions.Recursive includes subdirectories)"));
                }
                var problems = new List<string>();
                if (ignored.Count > 0)
                    problems.Add($"{ignored.Count} file(s) were not recognized as any format ({Names(ignored)})");
                if (strayPaths.Count > 0)
                    problems.Add($"{strayPaths.Count} stray file(s) were left out, as a name or a directory in the path starts with '.' or '_' ({Names(strayPaths)})");
                throw new DataBallException(problems.Count > 0
                    ? $"No file to open: {string.Join("; ", problems)}"
                    : $"No files to open for: {string.Join(", ", inputs)}");
            }
            return new OpenPlan(entries, reported, skipped);
        }

        /// <summary>The first three names, then an ellipsis when there are more.</summary>
        internal static string Names(IEnumerable<string> names)
        {
            var list = names.ToList();
            return string.Join(", ", list.Take(3)) + (list.Count > 3 ? ", ..." : "");
        }

        /// <summary>Every name, comma-separated: an error that names the files it could not open names all of them.</summary>
        internal static string Every(IEnumerable<string> names) => string.Join(", ", names);

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
            var root = Path.GetFullPath(directory);
            var options = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            {
                if (!FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(file), ignoreCase: true))
                    continue;
                // A stray match is bookkeeping, as in a directory expansion, unless the pattern asks for its name (dir/_*.csv).
                if (IsGlobStray(root, file, pattern))
                    strays.Add(Path.GetFullPath(file));
                else
                    Add(found, new OpenEntry(Path.GetFullPath(file), false, new FileInfo(file).Length));
            }
        }

        /// <summary>
        /// A glob match is stray when a directory between the glob's directory and the file is stray (its files stay ignored whatever the pattern),
        /// or the file's own name is stray and the pattern does not itself start with <c>.</c> or <c>_</c>.
        /// </summary>
        private static bool IsGlobStray(string root, string file, string pattern)
        {
            var parts = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (HiveFiles.IsStrayName(parts[i]))
                    return true;
            }
            return HiveFiles.IsStrayName(parts[^1]) && !HiveFiles.IsStrayName(pattern);
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

        /// <summary>What reading an entry found: a known format (or a pointer or native file), no known format, or several handlers tied.</summary>
        private enum Reading
        {
            Known,
            Unrecognized,
            Ambiguous,
        }

        /// <summary>
        /// Reads what <paramref name="entry"/> is. <paramref name="report"/> is the detection, unless the entry is a snapshot pointer or a native file.
        /// Throws when the file cannot be read or a handler throws while scoring it; no-format and ambiguous results are returned for the caller to judge.
        /// </summary>
        private static Reading Classify(OpenEntry entry, out DetectionReport? report)
        {
            report = null;
            if (!entry.Hive)
            {
                var target = SnapshotFiles.TryReadPointer(entry.OpenPath);
                if (target is not null)
                {
                    entry.Kind = OpenKind.Pointer;
                    entry.PointerTarget = target;
                    return Reading.Known;
                }
                if (DataBall.IsNativeBall(entry.OpenPath))
                {
                    entry.Kind = OpenKind.Native;
                    return Reading.Known;
                }
            }

            report = DataBall.Detect(entry.OpenPath);
            if (report.Outcome == DetectionOutcome.Ambiguous)
                return Reading.Ambiguous;
            if (report.Outcome == DetectionOutcome.None)
                return Reading.Unrecognized;
            entry.Report = report;
            return Reading.Known;
        }
    }
}
