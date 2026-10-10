// SPDX-License-Identifier: Apache-2.0
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using DuckDB.NET.Data;
using squalor.DataBall.Export;
using Xunit;
using Xunit.Abstractions;

namespace squalor.DataBall.Tests;

public sealed class BallDuckDbTrialTests(ITestOutputHelper output)
{
    private const string OrderedRows = "SELECT * FROM data ORDER BY groupId, sweepId";

    [Fact]
    public void Trial_DuckDbFile_RoundTrip_SameRowsAsWideSession()
    {
        using var dir = new TrialDirectory();
        var source = CaptureRows();
        using var wide = new DataBall();
        wide.AddRows(source);
        var path = Path.Combine(dir.Path, "capture.duckdb");
        using (var writer = new DataBall(databasePath: path))
            writer.AddRows(source);

        var expected = wide.Query(OrderedRows);
        var actual = ReopenRows(path);
        // Include every column name, value and row boundary in the comparison.
        Assert.Equal(expected.Select(r => r.ToArray()).ToArray(), actual.Select(r => r.ToArray()).ToArray());
        Assert.Equal(source.Select(r => r.ToArray()).ToArray(), actual.Select(r => r.ToArray()).ToArray());
        output.WriteLine($"RoundTrip: rows={actual.Count}, columns={actual[0].Count}; all rows equal to generated source and wide session");
    }

    [Fact]
    public async Task Trial_DuckDbFile_SizeVsBallAndParquet_LogsRatios()
    {
        using var dir = new TrialDirectory();
        output.WriteLine(await ProbeSizes(dir.Path, CaptureRows()));
    }

    [Fact]
    public async Task Trial_DuckDbFile_ReadOnlyOpenWhileWriterHolds_LogsOutcome()
    {
        using var dir = new TrialDirectory();
        var path = Path.Combine(dir.Path, "capture.duckdb");
        using (var writer = new DataBall(databasePath: path))
        {
            writer.AddRows(CaptureRows());
            output.WriteLine("ReadOnly while writer holds: " + await ProbeReadOnly(path));
        }
        output.WriteLine("ReadOnly after writer disposes: " + await ProbeReadOnly(path));
    }

    [Fact]
    public void Trial_DuckDbFile_StorageVersion_Logged()
    {
        using var dir = new TrialDirectory();
        var path = Path.Combine(dir.Path, "capture.duckdb");
        using (var writer = new DataBall(databasePath: path))
            writer.AddRows(CaptureRows());
        output.WriteLine(ProbeStorageVersion(path));
    }

    private static IReadOnlyList<Dictionary<string, object?>> ReopenRows(string path)
    {
        using var reopened = new DataBall(databasePath: path);
        return reopened.Query(OrderedRows);
    }

    private static async Task<string> ProbeSizes(string dir, IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var duck = Path.Combine(dir, "capture.duckdb");
        var ball = Path.Combine(dir, "capture.ball");
        var parquet = Path.Combine(dir, "capture.parquet");
        using (var writer = new DataBall(databasePath: duck))
            writer.AddRows(rows);
        using (var wide = new DataBall())
        {
            wide.AddRows(rows);
            await wide.SaveAsync(ball);
            await wide.ExportAsync(parquet, ExportType.Parquet);
        }
        var duckBytes = Bytes(duck);
        var ballBytes = Bytes(ball);
        var parquetBytes = Bytes(parquet);
        var sizes = FormattableString.Invariant($"Sizes: rows={rows.Count}, DuckDB={duckBytes} bytes, ZIP .ball={ballBytes} bytes, Parquet={parquetBytes} bytes; DuckDB/ball={(double)duckBytes / ballBytes:F6}, DuckDB/Parquet={(double)duckBytes / parquetBytes:F6}");

        // An initially absent configured dimension appears on append: db-02 re-splits all tables.
        var config = Path.Combine(dir, "layout.json");
        File.WriteAllText(config, """
            { "tables": {
              "groups": { "kind": "dimension", "columns": ["groupId"] },
              "environment": { "kind": "dimension", "columns": ["Humidity"] },
              "measurements": { "kind": "measurements", "columns": ["Pout", "EVM", "current"] }
            } }
            """);
        var growth = Path.Combine(dir, "growth.duckdb");
        string growthSizes;
        using (var layout = new DataBall(config, databasePath: growth))
        {
            layout.AddRows(rows);
            layout.Store.Execute("CHECKPOINT");
            var before = Bytes(growth);
            var newRow = new Dictionary<string, object?>(rows[^1])
            {
                ["groupId"] = 500L,
                ["sweepId"] = 0L,
                ["Humidity"] = 40.0
            };
            layout.AddRow(newRow);
            var afterAppend = Bytes(growth);
            var walAfterAppend = WalBytes(growth);
            var count = layout.Query("SELECT COUNT(*) AS n FROM data")[0]["n"];
            var dimensions = layout.Query("SELECT COUNT(*) AS n FROM environment")[0]["n"];
            layout.Store.Execute("CHECKPOINT");
            var afterCheckpoint = Bytes(growth);
            growthSizes = $"Dimension growth: before append={before} bytes, after append={afterAppend} bytes, WAL after append={walAfterAppend} bytes, after CHECKPOINT={afterCheckpoint} bytes, WAL after CHECKPOINT={WalBytes(growth)} bytes; main file shrank={afterCheckpoint < afterAppend}; rows={count}, environment dimension rows={dimensions}";
        }
        return sizes + Environment.NewLine + growthSizes + $", after dispose={Bytes(growth)} bytes";
    }

    private static async Task<string> ProbeReadOnly(string path)
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "TrialProbe", "DataBall.TrialProbe.dll");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(helper);
        start.ArgumentList.Add(path);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start trial child");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await child.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            throw new TimeoutException("Read-only trial child did not finish within 30 seconds");
        }
        var result = await stdout;
        var error = await stderr;
        if (child.ExitCode != 0)
            throw new InvalidOperationException($"Trial child failed ({child.ExitCode}): {result}{error}");
        var probe = JsonSerializer.Deserialize<ReadOnlyResult>(result)
            ?? throw new InvalidOperationException("Trial child returned no result");
        return $"pid={probe.ProcessId}, readOnlyRequested={probe.ReadOnlyRequested}, success={probe.Success}, accessMode={probe.AccessMode}, rows={probe.Rows}, error={probe.Error}";
    }

    private static string ProbeStorageVersion(string path)
    {
        var builder = new DuckDBConnectionStringBuilder { DataSource = path };
        builder["ACCESS_MODE"] = "READ_ONLY";
        using var connection = new DuckDBConnection(builder.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version(), tags['storage_version'] FROM duckdb_databases() WHERE path IS NOT NULL";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("No file-backed database storage version returned");
        var version = reader.GetString(0);
        var storage = reader.GetString(1);
        // Also log the numeric header version using DuckDB's documented 20-byte header layout.
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[20];
        file.ReadExactly(header);
        Assert.Equal("DUCK"u8.ToArray(), header[8..12].ToArray());
        var numeric = BinaryPrimitives.ReadUInt64LittleEndian(header[12..]);
        return $"Storage: DuckDB library={version}, storage_version={storage}, header version={numeric}; DuckDB.NET.Data.Full package=1.5.5; platform={System.Runtime.InteropServices.RuntimeInformation.OSDescription}, arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";
    }

    private static long Bytes(string path) => new FileInfo(path).Length;
    private static long WalBytes(string path) => File.Exists(path + ".wal") ? Bytes(path + ".wal") : 0;

    private sealed record ReadOnlyResult(int ProcessId, bool ReadOnlyRequested, bool Success, string? AccessMode, long? Rows, string? Error);

    private static IReadOnlyList<Dictionary<string, object?>> CaptureRows()
        => Enumerable.Range(0, 50_000).Select(i => new Dictionary<string, object?>
        {
            ["groupId"] = (long)(i / 100),
            ["sweepId"] = (long)(i % 100),
            ["Pout"] = -20.0 + (i % 100) * 0.25 + (i % 7) * 0.001,
            ["EVM"] = 0.01 + (i % 97) * 0.0001,
            ["current"] = 0.1 + (i % 127) * 0.0005
        }).ToArray();

    private sealed class TrialDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "ball-trial-" + Guid.NewGuid().ToString("N"));

        public TrialDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
