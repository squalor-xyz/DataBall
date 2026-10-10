// SPDX-License-Identifier: Apache-2.0
using System.IO.Enumeration;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace squalor.DataBall.FileTypes;

/// <summary>A loaded file type's identity and source file.</summary>
/// <param name="Id">The type identifier.</param>
/// <param name="Version">The declared version.</param>
/// <param name="Origin">The absolute path to its JSON definition.</param>
public sealed record FileTypeInfo(string Id, int Version, string Origin);

internal sealed class JsonFileType : IFormatHandler
{
    private sealed record Condition(string Key, double Weight, bool Required, Func<FileProbe, bool> Matches);
    private readonly IReadOnlyList<Condition> _conditions;
    internal FileTypeInfo Info { get; }
    public string Name => Info.Id;
    public int Priority { get; }
    public double MinScore { get; }
    public Config? Overlay { get; }
    public bool DelegatesToGenericImport => true;

    private JsonFileType(FileTypeInfo info, int priority, double minScore, Config? overlay, IReadOnlyList<Condition> conditions)
        => (Info, Priority, MinScore, Overlay, _conditions) = (info, priority, minScore, overlay, conditions);

    internal static JsonFileType Load(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            foreach (var property in root.EnumerateObject())
                if (property.Name is not "id" and not "version" and not "priority" and not "detect" and not "config")
                    throw new FormatException($"unknown top-level key '{property.Name}'");
            var id = root.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id))
                throw new FormatException("id must be non-empty");
            var version = root.GetProperty("version").GetInt32();
            var priority = root.TryGetProperty("priority", out var p) ? p.GetInt32() : 0;
            var detect = root.GetProperty("detect");
            var minScore = detect.TryGetProperty("minScore", out var m) ? m.GetDouble() : 0.6;
            if (!double.IsFinite(minScore) || minScore is < 0 or > 1)
                throw new FormatException("minScore must be between 0 and 1");
            var conditions = detect.GetProperty("conditions").EnumerateArray().Select(Compile).ToArray();
            if (conditions.Length == 0 || conditions.Sum(c => c.Weight) <= 0 || !double.IsFinite(conditions.Sum(c => c.Weight)))
                throw new FormatException("conditions must have a finite, positive total weight");
            var overlay = root.TryGetProperty("config", out var config) ? Config.FromJson(config.GetRawText()) : null;
            return new JsonFileType(new FileTypeInfo(id, version, System.IO.Path.GetFullPath(path)), priority, minScore, overlay, conditions);
        }
        catch (Exception ex)
        {
            throw new DataBallException($"Invalid file type '{path}': {ex.Message}", ex);
        }
    }

    private static Condition Compile(JsonElement entry)
    {
        var properties = entry.EnumerateObject().ToArray();
        if (properties.Select(p => p.Name).Distinct().Count() != properties.Length)
            throw new FormatException("duplicate condition key");
        var keys = properties.Where(p => p.Name is not "weight" and not "required").ToArray();
        if (keys.Length != 1)
            throw new FormatException("each condition must have exactly one condition key");
        var weight = entry.TryGetProperty("weight", out var w) ? w.GetDouble() : 1;
        if (!double.IsFinite(weight) || weight < 0)
            throw new FormatException("weight must be non-negative and finite");
        var required = entry.TryGetProperty("required", out var r) && r.GetBoolean();
        var key = keys[0].Name;
        var value = keys[0].Value;
        Func<FileProbe, bool> matcher;
        switch (key)
        {
            case "fileName":
                var glob = String(value);
                matcher = probe => FileSystemName.MatchesSimpleExpression(glob, probe.FileName, ignoreCase: true);
                break;
            case "fileNameRegex":
                var fileRegex = Regex(String(value));
                matcher = probe => fileRegex.IsMatch(probe.FileName);
                break;
            case "extension":
                var extensions = Strings(value);
                matcher = probe => extensions.Contains(probe.Extension, StringComparer.OrdinalIgnoreCase);
                break;
            case "headerContainsAll":
                var headers = Strings(value);
                matcher = probe => headers.All(h => probe.HeaderFields.Contains(h, StringComparer.OrdinalIgnoreCase));
                break;
            case "headerExact":
                var exact = Strings(value);
                matcher = probe => probe.HeaderFields.SequenceEqual(exact, StringComparer.OrdinalIgnoreCase);
                break;
            case "lineMatches":
                var lineKeys = value.EnumerateObject().Select(p => p.Name).ToArray();
                var hasLine = value.TryGetProperty("line", out var line);
                var hasFirst = value.TryGetProperty("firstLines", out var first);
                if (hasLine == hasFirst || lineKeys.Length != 2 || lineKeys.Any(k => k is not "line" and not "firstLines" and not "regex"))
                    throw new FormatException("lineMatches requires exactly one of line or firstLines, and regex");
                var count = (hasLine ? line : first).GetInt32();
                if (count < 0 || hasFirst && count == 0)
                    throw new FormatException("invalid line index or firstLines count");
                var lineRegex = Regex(String(value.GetProperty("regex")));
                matcher = hasLine
                    ? probe => count < probe.HeadLines.Count && lineRegex.IsMatch(probe.HeadLines[count])
                    : probe => probe.HeadLines.Take(count).Any(s => lineRegex.IsMatch(s));
                break;
            case "columnCount":
                var bounds = value.EnumerateObject().Select(p => p.Name).ToArray();
                if (bounds.Length == 0 || bounds.Length > 2 || bounds.Distinct().Count() != bounds.Length || bounds.Any(k => k is not "min" and not "max"))
                    throw new FormatException("columnCount requires min and/or max");
                var min = value.TryGetProperty("min", out var lower) ? lower.GetInt32() : 0;
                var max = value.TryGetProperty("max", out var upper) ? upper.GetInt32() : int.MaxValue;
                if (min < 0 || max < min)
                    throw new FormatException("invalid columnCount range");
                matcher = probe => probe.HeaderFields.Count >= min && probe.HeaderFields.Count <= max;
                break;
            case "parquetColumns":
                var columns = Strings(value);
                matcher = probe => probe.IsParquet && columns.All(c => probe.ParquetColumns.Contains(c, StringComparer.OrdinalIgnoreCase));
                break;
            default:
                throw new FormatException($"unknown condition '{key}'");
        }
        return new Condition(key, weight, required, matcher);
    }

    private static string String(JsonElement element) => element.GetString() ?? throw new FormatException("expected string");
    private static string[] Strings(JsonElement element) => element.EnumerateArray().Select(String).ToArray();
    private static Regex Regex(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public DetectionScore Score(FileProbe probe)
    {
        var matchedWeight = 0.0;
        var gated = false;
        var reasons = new List<string>();
        foreach (var condition in _conditions)
        {
            bool matched;
            try { matched = condition.Matches(probe); }
            catch (RegexMatchTimeoutException ex) { throw new DataBallException($"Regex timed out in file type '{Info.Origin}'", ex); }
            if (matched)
                matchedWeight += condition.Weight;
            else if (condition.Required)
                gated = true;
            reasons.Add($"{condition.Key}: {(matched ? "matched" : "missed")}{(condition.Required ? " (required)" : "")}");
        }
        return new DetectionScore(gated ? 0 : matchedWeight / _conditions.Sum(c => c.Weight), reasons);
    }

    public bool CanHandle(string path, Stream sniff) => Score(new FileProbe(path)).Score >= MinScore;
    public IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Parse(string path, Config schema, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("JSON file types delegate to generic import");
}
