// SPDX-License-Identifier: Apache-2.0
using squalor.DataBall;
using squalor.DataBall.Export;

namespace squalor.DataBall.Cli;

internal static class CliFormat
{
    // Longest compound suffixes first so .tar.gz is not treated as .gz.
    private static readonly string[] KnownSuffixes =
    [
        ".tar.gz",
        ".tar.xz",
        ".parquet",
        ".tgz",
        ".txz",
        ".tar",
        ".zip",
        ".ball",
        ".csv",
    ];

    internal static ExportType DetectExportType(string path)
    {
        try
        {
            return DataBall.DetectFormat(path);
        }
        catch (DataBallException)
        {
            throw new DataBallException($"Cannot detect format from '{path}'. Use --format csv|parquet|ball|archive.");
        }
    }

    internal static ExportType ParseFormat(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "csv" => ExportType.Csv,
            "parquet" => ExportType.Parquet,
            "ball" => ExportType.Ball,
            "archive" => ExportType.Archive,
            _ => throw new DataBallException($"Unknown format '{value}'. Use csv, parquet, ball, or archive."),
        };
    }

    internal static string DefaultBallPath(string input)
    {
        var dest = StripKnownSuffix(input) + ".ball";
        if (SamePath(dest, input))
            dest = StripKnownSuffix(input) + ".bounced.ball";
        return dest;
    }

    internal static string DefaultSquishDir(string input)
    {
        if (Directory.Exists(input))
            throw new DataBallException("Output path is the input path. Pass -o explicitly.");
        return StripKnownSuffix(input);
    }

    internal static bool SamePath(string a, string b)
    {
        var fa = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fa == fb;
    }

    private static string StripKnownSuffix(string input)
    {
        var fileName = Path.GetFileName(input);
        if (string.IsNullOrEmpty(fileName))
            return input;

        foreach (var suffix in KnownSuffixes)
        {
            if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;
            var stem = fileName[..^suffix.Length];
            var dir = Path.GetDirectoryName(input);
            return string.IsNullOrEmpty(dir) ? stem : Path.Combine(dir, stem);
        }

        return input;
    }
}
