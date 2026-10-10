// SPDX-License-Identifier: Apache-2.0
using System.Text;
using DuckDB.NET.Data;

namespace squalor.DataBall;

/// <summary>Immutable, bounded information used to score an input file.</summary>
public sealed class FileProbe
{
    /// <summary>The input path.</summary>
    public string Path { get; }
    /// <summary>The file name without its directory.</summary>
    public string FileName { get; }
    /// <summary>The extension including its dot.</summary>
    public string Extension { get; }
    /// <summary>The file's byte length, or zero for a directory.</summary>
    public long Length { get; }
    /// <summary>The last write time in UTC.</summary>
    public DateTime LastWriteTime { get; }
    /// <summary>At most the first 64 KiB of the file.</summary>
    public ReadOnlyMemory<byte> Head { get; }
    /// <summary>BOM-aware decoded lines from the bounded head.</summary>
    public IReadOnlyList<string> HeadLines { get; }
    /// <summary>The delimiter inferred from field-count consistency.</summary>
    public char Delimiter { get; }
    /// <summary>Literal fields of the first line, with CSV quoting removed.</summary>
    public IReadOnlyList<string> HeaderFields { get; }
    /// <summary>Whether both ends of the file contain Parquet magic.</summary>
    public bool IsParquet { get; }
    /// <summary>Top-level Parquet column names returned by DESCRIBE.</summary>
    public IReadOnlyList<string> ParquetColumns => _parquetColumns.Value;
    private readonly Lazy<IReadOnlyList<string>> _parquetColumns;
    /// <summary>Whether the input is a directory.</summary>
    public bool IsDirectory { get; }

    /// <summary>Reads a bounded probe. Only Parquet's four-byte tail and schema query read beyond the head.</summary>
    public FileProbe(string path)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        Extension = System.IO.Path.GetExtension(path);
        IsDirectory = Directory.Exists(path);
        HeadLines = Array.Empty<string>();
        HeaderFields = Array.Empty<string>();
        _parquetColumns = new Lazy<IReadOnlyList<string>>(ReadParquetColumns);
        Delimiter = ',';
        try
        {
            if (IsDirectory)
            {
                LastWriteTime = Directory.GetLastWriteTimeUtc(path);
                return;
            }
            using var file = File.OpenRead(path);
            Length = file.Length;
            LastWriteTime = File.GetLastWriteTimeUtc(path);
            var bytes = new byte[(int)Math.Min(Length, 65536)];
            file.ReadExactly(bytes);
            Head = bytes;
            using (var reader = new StringReader(Decode(bytes, Length <= 65536)))
            {
                var lines = new List<string>();
                while (reader.ReadLine() is { } line)
                    lines.Add(line);
                HeadLines = lines.AsReadOnly();
            }
            Delimiter = SniffDelimiter(HeadLines);
            HeaderFields = Array.AsReadOnly(HeadLines.Count == 0 ? Array.Empty<string>() : Fields(HeadLines[0], Delimiter));
            if (Length >= 8 && bytes.AsSpan(0, 4).SequenceEqual("PAR1"u8))
            {
                file.Position = Length - 4;
                Span<byte> tail = stackalloc byte[4];
                file.ReadExactly(tail);
                IsParquet = tail.SequenceEqual("PAR1"u8);
            }

        }
        catch (Exception ex) when (ex is not DataBallException)
        {
            throw new DataBallException($"Failed to probe '{path}'", ex);
        }
    }

    private IReadOnlyList<string> ReadParquetColumns()
    {
        if (!IsParquet)
            return Array.Empty<string>();
        try
        {
            using var connection = new DuckDBConnection("DataSource=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DESCRIBE SELECT * FROM read_parquet({DuckDbStore.QuoteString(Path)})";
            using var reader = command.ExecuteReader();
            var columns = new List<string>();
            while (reader.Read())
                columns.Add(reader.GetString(0));
            return columns.AsReadOnly();
        }
        catch (Exception ex) when (ex is not DataBallException)
        {
            throw new DataBallException($"Failed to probe '{Path}'", ex);
        }
    }

    private static string Decode(byte[] bytes, bool complete)
    {
        // Check UTF-32 before UTF-16 because the little-endian BOMs share a prefix.
        foreach (var encoding in new Encoding[] { new UTF32Encoding(false, true), new UTF32Encoding(true, true), Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF8 })
        {
            var bom = encoding.GetPreamble();
            if (bytes.AsSpan().StartsWith(bom))
                return encoding.GetString(bytes, bom.Length, bytes.Length - bom.Length);
        }
        try
        {
            // A multibyte character may straddle the head boundary. Do not mistake that for Latin-1.
            var decoder = new UTF8Encoding(false, true).GetDecoder();
            var chars = new char[bytes.Length];
            decoder.Convert(bytes, chars, flush: complete, out _, out var count, out _);
            return new string(chars, 0, count);
        }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    private static char SniffDelimiter(IReadOnlyList<string> lines)
    {
        var best = ',';
        var consistency = -1.0;
        var fields = 1;
        foreach (var delimiter in new[] { ',', ';', '\t', '|' })
        {
            var counts = lines.Take(20).Where(l => l.Length > 0).Select(l => Fields(l, delimiter).Length).ToArray();
            if (counts.Length == 0 || counts[0] <= 1)
                continue;
            var ratio = (double)counts.Count(c => c == counts[0]) / counts.Length;
            if (ratio > consistency || ratio == consistency && counts[0] > fields)
                (best, consistency, fields) = (delimiter, ratio, counts[0]);
        }
        return best;
    }

    private static string[] Fields(string line, char delimiter)
    {
        var result = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                { value.Append('"'); i++; }
                else
                    quoted = !quoted;
            }
            else if (line[i] == delimiter && !quoted)
            { result.Add(value.ToString().Trim()); value.Clear(); }
            else
                value.Append(line[i]);
        }
        result.Add(value.ToString().Trim());
        return result.ToArray();
    }
}
