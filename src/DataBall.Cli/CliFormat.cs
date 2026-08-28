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
        ".sqlite3",
        ".parquet",
        ".sqlite",
        ".tgz",
        ".txz",
        ".tar",
        ".zip",
        ".ball",
        ".csv",
        ".db",
    ];

    internal static ExportType DetectExportType(string path)
    {
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName))
            throw new DataBallException($"Cannot detect format from '{path}'. Use --format csv|parquet|sqlite|ball|archive.");

        if (EndsWith(fileName, ".tar.gz") || EndsWith(fileName, ".tgz")
            || EndsWith(fileName, ".tar.xz") || EndsWith(fileName, ".txz")
            || EndsWith(fileName, ".tar") || EndsWith(fileName, ".zip"))
            return ExportType.Archive;
        if (EndsWith(fileName, ".ball"))
            return ExportType.Ball;
        if (EndsWith(fileName, ".csv"))
            return ExportType.Csv;
        if (EndsWith(fileName, ".parquet"))
            return ExportType.Parquet;
        if (EndsWith(fileName, ".db") || EndsWith(fileName, ".sqlite") || EndsWith(fileName, ".sqlite3"))
            return ExportType.Sqlite;
        throw new DataBallException($"Cannot detect format from '{path}'. Use --format csv|parquet|sqlite|ball|archive.");
    }

    internal static ExportType ParseFormat(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "csv" => ExportType.Csv,
            "parquet" => ExportType.Parquet,
            "sqlite" => ExportType.Sqlite,
            "ball" => ExportType.Ball,
            "archive" => ExportType.Archive,
            _ => throw new DataBallException($"Unknown format '{value}'. Use csv, parquet, sqlite, ball, or archive."),
        };
    }

    internal static string DefaultBallPath(string input)
    {
        return StripKnownSuffix(input) + ".ball";
    }

    internal static string DefaultSquishDir(string input)
    {
        return StripKnownSuffix(input);
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

    private static bool EndsWith(string fileName, string suffix)
        => fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
}
