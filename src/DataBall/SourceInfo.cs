// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace squalor.DataBall;

/// <summary>One row of the session's <c>source</c> table: a file whose rows the session holds.</summary>
public sealed class SourceInfo : IEquatable<SourceInfo>
{
    internal SourceInfo(long key, string path, string? sha256, string type, long bytes, DateTime? modifiedUtc, DateTime addedUtc)
    {
        Key = key;
        Path = path;
        Sha256 = sha256;
        Type = type;
        Bytes = bytes;
        ModifiedUtc = modifiedUtc;
        AddedUtc = addedUtc;
    }

    /// <summary>The <c>source_key</c>, assigned 1, 2, … in import order. Rows of <c>"data"</c> carry it as <c>_source_key</c>.</summary>
    public long Key { get; init; }

    /// <summary>The full path of the file, or <c>&lt;archive&gt;!&lt;entry&gt;</c> for an archive entry.</summary>
    public string Path { get; init; }

    /// <summary>
    /// The lowercase hex SHA-256 of the file. For a hive Parquet directory it is the SHA-256 over the directory's
    /// Parquet files, sorted by relative path, as lines <c>&lt;relative path&gt;\t&lt;file SHA-256&gt;\n</c> (relative paths use <c>/</c>).
    /// DataBall always writes it; it is null only for a <c>source</c> row that something else wrote.
    /// </summary>
    public string? Sha256 { get; init; }

    /// <summary>The detected type: a handler name, a JSON type id, or a built-in path name such as <c>CsvImport</c>.</summary>
    public string Type { get; init; }

    /// <summary>The file size in bytes (the sum of its files for a hive directory).</summary>
    public long Bytes { get; init; }

    /// <summary>The last-write time of the file in UTC, when known.</summary>
    public DateTime? ModifiedUtc { get; init; }

    /// <summary>When the session recorded the file, in UTC.</summary>
    public DateTime AddedUtc { get; init; }

    /// <inheritdoc />
    public bool Equals(SourceInfo? other) =>
        other is not null && Key == other.Key && Path == other.Path && Sha256 == other.Sha256 && Type == other.Type
        && Bytes == other.Bytes && ModifiedUtc == other.ModifiedUtc && AddedUtc == other.AddedUtc;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SourceInfo);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Key, Path, Sha256, Type, Bytes, ModifiedUtc, AddedUtc);

    /// <inheritdoc />
    public override string ToString() => $"{Key}: {Path} ({Type})";
}

/// <summary>Names of the lineage objects, and the rules for names that start with <c>_source</c>.</summary>
internal static class Lineage
{
    /// <summary>The built-in table with one row per imported file.</summary>
    internal const string Table = "source";

    /// <summary>The <c>"data"</c> column that points at a row of <see cref="Table"/>.</summary>
    internal const string KeyColumn = "_source_key";

    /// <summary>Export column that carries the path of the source.</summary>
    internal const string FileColumn = "_source_file";

    /// <summary>Export column that carries the type of the source.</summary>
    internal const string TypeColumn = "_source_type";

    /// <summary>Where an incoming <see cref="FileColumn"/> goes, so an earlier export keeps its provenance.</summary>
    internal const string OriginFileColumn = "origin_source_file";

    /// <summary>Where an incoming <see cref="TypeColumn"/> goes.</summary>
    internal const string OriginTypeColumn = "origin_source_type";

    private const string ReservedPrefix = "_source";

    /// <summary>True for every name that starts with <c>_source</c>, in any case.</summary>
    internal static bool IsReserved(string name) => name.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Host writes may not name a lineage column. Call before changing anything.</summary>
    internal static void ThrowIfReservedColumn(string? name)
    {
        if (name is not null && IsReserved(name))
            throw new DataBallException($"Column name '{name}' is reserved: names starting with '{ReservedPrefix}' belong to source lineage");
    }

    /// <summary>
    /// The name an incoming file column gets: <c>_source_file</c> and <c>_source_type</c> (an earlier export)
    /// become <c>origin_source_*</c>, any other <c>_source*</c> name throws, everything else is unchanged.
    /// </summary>
    internal static string IncomingName(string name)
    {
        if (!IsReserved(name))
            return name;
        if (name.Equals(FileColumn, StringComparison.OrdinalIgnoreCase))
            return OriginFileColumn;
        if (name.Equals(TypeColumn, StringComparison.OrdinalIgnoreCase))
            return OriginTypeColumn;
        throw new DataBallException($"Column '{name}' is reserved for source lineage; rename it in the input");
    }
}

/// <summary>A file about to be recorded in the <c>source</c> table.</summary>
internal sealed record SourceRecord(string Path, string Sha256, string Type, long Bytes, DateTime? ModifiedUtc);

/// <summary>Reads the facts the <c>source</c> table records about an input.</summary>
internal static class SourceFile
{
    /// <summary>
    /// Describes a file, or a hive directory: its <c>sha256</c> is the digest of the sorted
    /// (relative path, file SHA-256) lines of its Parquet files and <c>bytes</c> their total size. Each file is read once.
    /// </summary>
    internal static SourceRecord Describe(string path, string type)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (!Directory.Exists(full))
            return Hash(full, full, type, new FileInfo(full).LastWriteTimeUtc);

        var files = Directory.EnumerateFiles(full, "*.parquet", SearchOption.AllDirectories)
            .Select(f => (Relative: System.IO.Path.GetRelativePath(full, f).Replace('\\', '/'), File: f))
            .OrderBy(f => f.Relative, StringComparer.Ordinal)
            .ToList();
        var manifest = new StringBuilder();
        long bytes = 0;
        DateTime? newest = null;
        foreach (var (relative, file) in files)
        {
            var one = Hash(file, file, type, new FileInfo(file).LastWriteTimeUtc);
            manifest.Append(relative).Append('\t').Append(one.Sha256).Append('\n');
            bytes += one.Bytes;
            if (newest is null || one.ModifiedUtc > newest)
                newest = one.ModifiedUtc;
        }
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString()))).ToLowerInvariant();
        return new SourceRecord(full, digest, type, bytes, newest);
    }

    /// <summary>Describes an archive entry already extracted to <paramref name="extracted"/>.</summary>
    internal static SourceRecord DescribeEntry(string extracted, string recordedPath, string type, DateTime? modified)
    {
        DateTime? utc = modified is null
            ? null
            : modified.Value.Kind == DateTimeKind.Local
                ? modified.Value.ToUniversalTime()
                : DateTime.SpecifyKind(modified.Value, DateTimeKind.Utc);
        return Hash(extracted, recordedPath, type, utc);
    }

    private static SourceRecord Hash(string file, string recordedPath, string type, DateTime? modifiedUtc)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        var bytes = stream.Length;
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new SourceRecord(recordedPath, hash, type, bytes, modifiedUtc);
    }
}
