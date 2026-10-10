// SPDX-License-Identifier: Apache-2.0

namespace squalor.DataBall;

/// <summary>A normalized detection score and its evidence.</summary>
public sealed class DetectionScore
{
    /// <summary>The normalized score in the inclusive range 0 to 1.</summary>
    public double Score { get; }
    /// <summary>Human-readable evidence, including misses.</summary>
    public IReadOnlyList<string> Reasons { get; }
    /// <summary>Creates a score with immutable evidence.</summary>
    public DetectionScore(double score, IReadOnlyList<string> reasons)
    {
        if (!double.IsFinite(score) || score is < 0 or > 1)
            throw new DataBallException("Detection score must be between 0 and 1");
        ArgumentNullException.ThrowIfNull(reasons);
        Score = score;
        Reasons = Array.AsReadOnly(reasons.ToArray());
    }
}

/// <summary>The result of scoring an input.</summary>
public enum DetectionOutcome
{
    /// <summary>A scored handler won.</summary>
    Match,
    /// <summary>No scored candidate; a built-in path is available.</summary>
    Generic,
    /// <summary>The highest score and priority are tied.</summary>
    Ambiguous,
    /// <summary>No scored candidate and no built-in path, or a delegating winner with no built-in path.</summary>
    None
}

/// <summary>A built-in route available when no parsing handler wins.</summary>
public enum BuiltInPath
{
    /// <summary>Open a native DuckDB session.</summary>
    NativeBall,
    /// <summary>Attach a Parquet file or hive directory as a view.</summary>
    ParquetView,
    /// <summary>Import generic CSV.</summary>
    CsvImport,
    /// <summary>Import CSV files from an archive.</summary>
    ArchiveImport,
    /// <summary>Import a non-native ball file.</summary>
    BallImport
}

/// <summary>One handler's entry in the scoring table.</summary>
/// <param name="Name">The handler name.</param>
/// <param name="Score">Its normalized score.</param>
/// <param name="MinScore">Its candidate threshold.</param>
/// <param name="Priority">Its tie-breaking priority.</param>
/// <param name="Reasons">Its matching evidence.</param>
/// <param name="Origin">The JSON source path, or null for a registered handler.</param>
public sealed record HandlerDetection(
    string Name, double Score, double MinScore, int Priority, IReadOnlyList<string> Reasons, string? Origin);

/// <summary>Detection evidence, winner and available built-in fallback.</summary>
public sealed class DetectionReport
{
    /// <summary>The scored handlers.</summary>
    public IReadOnlyList<HandlerDetection> Handlers { get; }
    /// <summary>The detection outcome.</summary>
    public DetectionOutcome Outcome { get; }
    /// <summary>The winning handler, if any.</summary>
    public IFormatHandler? Winner { get; }
    /// <summary>The built-in path, if available.</summary>
    public BuiltInPath? BuiltInPath { get; }
    /// <summary>Why the built-in path is or is not available.</summary>
    public string FallbackReason { get; }
    /// <summary>Why this outcome was chosen.</summary>
    public string Reason { get; }

    internal DetectionReport(IReadOnlyList<HandlerDetection> handlers, DetectionOutcome outcome, IFormatHandler? winner,
        BuiltInPath? builtInPath, string fallbackReason, string reason)
    {
        Handlers = Array.AsReadOnly(handlers.ToArray());
        Outcome = outcome;
        Winner = winner;
        BuiltInPath = builtInPath;
        FallbackReason = fallbackReason;
        Reason = reason;
    }

    private string BuiltInText => BuiltInPath switch
    {
        squalor.DataBall.BuiltInPath.NativeBall => "native .ball",
        squalor.DataBall.BuiltInPath.ParquetView => "Parquet view",
        squalor.DataBall.BuiltInPath.CsvImport => "CSV import",
        squalor.DataBall.BuiltInPath.ArchiveImport => "Archive import",
        squalor.DataBall.BuiltInPath.BallImport => "Ball import",
        _ => "unavailable"
    };

    /// <summary>Formats the outcome and full scoring table for diagnostics.</summary>
    public override string ToString()
    {
        var lines = new List<string> { $"{Outcome}: {Reason}" };
        foreach (var handler in Handlers)
            lines.Add(FormattableString.Invariant($"{handler.Name}: score={handler.Score:G} minScore={handler.MinScore:G} priority={handler.Priority} origin={handler.Origin ?? "registered handler"}; {string.Join("; ", handler.Reasons)}"));
        lines.Add($"Generic: {BuiltInText}; {FallbackReason}");
        return string.Join(Environment.NewLine, lines);
    }
}
