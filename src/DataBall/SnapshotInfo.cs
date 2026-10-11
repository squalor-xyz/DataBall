// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;

namespace squalor.DataBall;

/// <summary>One published snapshot generation, or the generation a reader opened through a pointer.</summary>
public sealed class SnapshotInfo
{
    internal SnapshotInfo(int generation, string path, string pointerPath, long bytes, TimeSpan duration)
    {
        Generation = generation;
        Path = path;
        PointerPath = pointerPath;
        Bytes = bytes;
        Duration = duration;
    }

    /// <summary>The generation number <c>n</c> of <c>&lt;Name&gt;.&lt;n&gt;.ball</c>.</summary>
    public int Generation { get; init; }

    /// <summary>The full path of the generation file.</summary>
    public string Path { get; init; }

    /// <summary>The full path of the pointer file, <c>&lt;Name&gt;.latest</c>.</summary>
    public string PointerPath { get; init; }

    /// <summary>The size of the generation file in bytes.</summary>
    public long Bytes { get; init; }

    /// <summary>How long publishing took, from choosing the generation until the pointer named it. Zero for a snapshot a reader opened.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>Names, the pointer format and the retention of snapshot generations.</summary>
internal static class SnapshotFiles
{
    /// <summary>First line of a pointer file. A pointer is recognized by this line, not by its extension.</summary>
    internal const string Magic = "DATABALL-SNAPSHOT 1";

    private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes(Magic);

    private const string GenerationSuffix = ".ball";
    private const string PointerSuffix = ".latest";
    private const int PointerReadLimit = 4096;

    private static readonly StringComparison NameComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Test seam: runs before a generation is deleted; throwing an <see cref="IOException"/> simulates a held file.</summary>
    internal static Action<string>? BeforeDeleteForTests;

    /// <summary>Test seam: runs with the pointer path when the first read finds its generation missing, before the pointer is read again.</summary>
    internal static Action<string>? GenerationMissingForTests;

    internal static void Validate(SnapshotOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Directory))
            throw new DataBallException("Snapshot directory is required");
        if (!IsBareFileName(options.Name))
            throw new DataBallException($"Snapshot name must be a bare file name: '{options.Name}'");
        if (options.Keep < 1)
            throw new DataBallException("Snapshot Keep must be at least 1");
    }

    private static bool IsBareFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name == System.IO.Path.GetFileName(name) && !name.Contains('\\')
        && name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0 && name is not ("." or "..");

    internal static string PointerPath(string directory, string name) =>
        System.IO.Path.Combine(directory, name + PointerSuffix);

    internal static string GenerationFile(string name, int generation) =>
        name + "." + generation.ToString(CultureInfo.InvariantCulture) + GenerationSuffix;

    /// <summary>The generations of <paramref name="name"/> in <paramref name="directory"/>: files named <c>&lt;name&gt;.&lt;n&gt;.ball</c>.</summary>
    internal static List<(int Generation, string Path)> Generations(string directory, string name)
    {
        var found = new List<(int, string)>();
        if (!Directory.Exists(directory))
            return found;
        var prefix = name + ".";
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var fileName = System.IO.Path.GetFileName(file);
            if (fileName.Length <= prefix.Length + GenerationSuffix.Length
                || !fileName.StartsWith(prefix, NameComparison) || !fileName.EndsWith(GenerationSuffix, NameComparison))
                continue;
            var number = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - GenerationSuffix.Length);
            if (int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var generation) && generation > 0)
                found.Add((generation, file));
        }
        return found;
    }

    /// <summary>One more than the highest existing generation, so numbering survives a writer restart.</summary>
    internal static int NextGeneration(string directory, string name)
    {
        var existing = Generations(directory, name);
        return existing.Count == 0 ? 1 : existing.Max(g => g.Generation) + 1;
    }

    /// <summary>Replaces the pointer atomically: a uniquely named temp file, then a move over the old pointer.</summary>
    internal static void WritePointer(string pointerPath, string generationFile)
    {
        var temporary = pointerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, Magic + "\n" + generationFile + "\n", new UTF8Encoding(false));
            File.Move(temporary, pointerPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>
    /// Reads the generation file name a pointer names. Returns null when the file does not start with the magic line
    /// (it is not a pointer); throws when it does but is malformed.
    /// </summary>
    internal static string? TryReadPointer(string path)
    {
        var head = new byte[PointerReadLimit];
        int count;
        // Share every mode so a writer replacing the pointer is not blocked by a reader (Windows).
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            count = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        if (!head.AsSpan(0, count).StartsWith(MagicBytes))
            return null;
        var lines = Encoding.UTF8.GetString(head, 0, count).Split('\n');
        if (lines[0].TrimEnd('\r') != Magic)
            return null;
        var name = lines.Length > 1 ? lines[1].TrimEnd('\r') : "";
        if (!IsBareFileName(name))
            throw new DataBallException($"{path} is not a valid snapshot pointer: line 2 must name a generation file in the same directory");
        return name;
    }

    /// <summary>The generation number in <c>&lt;name&gt;.&lt;n&gt;.ball</c>.</summary>
    internal static int ParseGeneration(string pointerPath, string generationFile)
    {
        if (generationFile.EndsWith(GenerationSuffix, NameComparison))
        {
            var stem = generationFile[..^GenerationSuffix.Length];
            var dot = stem.LastIndexOf('.');
            if (dot > 0 && int.TryParse(stem.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var generation) && generation > 0)
                return generation;
        }
        throw new DataBallException($"{pointerPath} is not a valid snapshot pointer: '{generationFile}' is not a generation file name");
    }

    /// <summary>
    /// Deletes all but the newest <paramref name="keep"/> generations, never the one the pointer names.
    /// A generation that cannot be deleted (a reader holds it on Windows) is skipped and retried at the next publish.
    /// Does nothing when the pointer cannot be read.
    /// </summary>
    internal static void Prune(string directory, string name, int keep, ILogger logger)
    {
        string? protectedFile;
        try
        {
            protectedFile = TryReadPointer(PointerPath(directory, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DataBallException)
        {
            logger.LogWarning(ex, "Snapshot retention skipped: the pointer cannot be read");
            return;
        }
        if (protectedFile is null)
            return;
        foreach (var (_, path) in Generations(directory, name).OrderByDescending(g => g.Generation).Skip(keep))
        {
            if (System.IO.Path.GetFileName(path).Equals(protectedFile, NameComparison))
                continue;
            try
            {
                BeforeDeleteForTests?.Invoke(path);
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Snapshot generation {Path} could not be deleted; retrying at the next publish", path);
            }
        }
    }
}
