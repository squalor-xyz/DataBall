// SPDX-License-Identifier: Apache-2.0
using System.Runtime.CompilerServices;
using squalor.DataBall.Handlers;
using Xunit;

namespace squalor.DataBall.Tests;

public class DetectionFixture : IDisposable
{
    internal readonly string Root = Path.Combine(AppContext.BaseDirectory, "detect-" + Guid.NewGuid().ToString("N"));

    internal string TypesRoot => Path.Combine(Root, "types");

    public DetectionFixture()
    {
        Directory.CreateDirectory(TypesRoot);
        DataBall.ClearHandlers();
        SetRoots(TypesRoot, TypesRoot);
    }

    public void Dispose()
    {
        DataBall.ClearHandlers();
        UseEmptyRoots();
        Directory.Delete(Root, true);
    }

    internal string Write(string name, string text)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, text);
        return path;
    }

    internal static FileProbe Probe(string path) => new(path);
    internal static DetectionReport Detect(string path) => DataBall.Detect(path);
    internal static double Score(IFormatHandler handler, string path) => handler.Score(Probe(path)).Score;
    [ModuleInitializer]
    internal static void UseEmptyRoots()
    {
        var empty = Path.Combine(AppContext.BaseDirectory, "no-file-types");
        SetRoots(empty, empty);
    }

    internal static void SetRoots(string? home, string? local)
        => DataBall.SetFileTypeRootsForTests(home, local);

}

[Collection("DataBall.Handlers")]
public class DetectionTests : DetectionFixture
{
    [Theory]
    [InlineData(true, 1.0)]
    [InlineData(false, 0.0)]
    public void Score_DefaultMapsCanHandle(bool claims, double expected)
    {
        var path = Write("legacy.test", new string('x', 100000));
        var result = ((IFormatHandler)new LegacyHandler(claims)).Score(Probe(path));
        Assert.Equal(expected, result.Score);
        Assert.NotEmpty(result.Reasons);
    }

    [Fact]
    public void Open_HighestScoreWins_NotFirstRegistered()
    {
        var path = Write("input.test", "x");
        DataBall.RegisterHandler(ScoredHandler.Create("low", 0.7, 0, 10));
        DataBall.RegisterHandler(ScoredHandler.Create("high", 0.9, 0, 20));
        using var db = DataBall.Open(path);
        Assert.Equal(20L, db.Query("SELECT Id FROM data")[0]["Id"]);
    }

    [Fact]
    public void Open_TieBrokenByPriority()
    {
        var path = Write("input.test", "x");
        DataBall.RegisterHandler(ScoredHandler.Create("low", 0.8, 0, 10));
        DataBall.RegisterHandler(ScoredHandler.Create("high", 0.8, 2, 20));
        using var db = DataBall.Open(path);
        Assert.Equal(20L, db.Query("SELECT Id FROM data")[0]["Id"]);
    }

    [Fact]
    public void Open_TieAfterPriority_ThrowsAmbiguous()
    {
        var path = Write("input.test", "x");
        DataBall.RegisterHandler(ScoredHandler.Create("first", 0.8, 0, 10));
        DataBall.RegisterHandler(ScoredHandler.Create("second", 0.8, 0, 20));
        var ex = Assert.Throws<DataBallException>(() => DataBall.Open(path));
        Assert.Contains("Ambiguous", ex.Message);
        Assert.Contains("first", ex.Message);
        Assert.Contains("second", ex.Message);
        Assert.Contains("0.8", ex.Message);
        Assert.Contains("test evidence", ex.Message);
    }

    [Fact]
    public void Open_TieAfterPriority_ThrowsAmbiguous_DefaultCanHandleHandlers()
    {
        var path = Write("legacy.test", new string('x', 100000));
        DataBall.RegisterHandler(new DefaultFirstHandler());
        DataBall.RegisterHandler(new DefaultSecondHandler());
        var ex = Assert.Throws<DataBallException>(() => DataBall.Open(path));
        Assert.Contains("Ambiguous", ex.Message);
        Assert.Contains("DefaultFirstHandler", ex.Message);
        Assert.Contains("DefaultSecondHandler", ex.Message);
        Assert.Contains("score=1", ex.Message);
        Assert.Contains("CanHandle matched", ex.Message);
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("parquet")]
    [InlineData("hive")]
    public void Open_NoCandidate_KnownExtension_UsesBuiltIn(string kind)
    {
        using var fixture = new ParquetFixture();
        var path = kind == "csv" ? Write("input.csv", "Id\n1\n2\n") : kind == "parquet" ? fixture.Source : fixture.Hive;
        var report = Detect(path);
        Assert.Equal("Generic", report.Outcome.ToString());
        Assert.NotEmpty(report.Reason);
        using var db = DataBall.Open(path);
        Assert.Equal(new long[] { 1, 2 }, db.Query("SELECT Id FROM data ORDER BY Id").Select(r => Convert.ToInt64(r["Id"])));
        Assert.Equal(kind == "csv" ? "BASE TABLE" : "VIEW", db.Query("SELECT table_type FROM information_schema.tables WHERE table_name='data'")[0]["table_type"]);
    }

    [Fact]
    public void Open_NoCandidate_UnknownExtension_ThrowsWithTable()
    {
        var path = Write("input.unknown", "x");
        DataBall.RegisterHandler(ScoredHandler.Create("below", 0.2, 0, 10, claims: false));
        var ex = Assert.Throws<DataBallException>(() => DataBall.Open(path));
        Assert.Contains("below", ex.Message);
        Assert.Contains("0.2", ex.Message);
        Assert.Contains("test evidence", ex.Message);
    }

    [Fact]
    public async Task NativeBall_IsRecognizedBeforeHandlers()
    {
        var path = Path.Combine(Root, "session.ball");
        using (var db = new DataBall())
        {
            db.AddColumn("Id", new long[] { 42 });
            await db.SaveAsync(path);
        }
        DataBall.RegisterHandler(ScoredHandler.Create("trap", 1, 0, 99, throws: true));
        Assert.Equal("Generic", Detect(path).Outcome.ToString());
        using var reopened = DataBall.Open(path);
        Assert.Equal(path, reopened.Query("SELECT path FROM duckdb_databases() WHERE database_name=current_database()")[0]["path"]);
        Assert.Equal(42L, reopened.Query("SELECT Id FROM data")[0]["Id"]);
    }

    [Fact]
    public void Detect_Directory_NeverOfferedToHandlers()
    {
        using var fixture = new ParquetFixture();
        DataBall.RegisterHandler(ScoredHandler.Create("trap", 1, 0, 99, throws: true));
        Assert.Equal("Generic", Detect(fixture.Hive).Outcome.ToString());
    }

    [Fact]
    public void FileProbe_ReadsAtMost64KiB()
    {
        var probe = Probe(Write("large.csv", new string('x', 1024 * 1024)));
        Assert.Equal(65536, probe.Head.Length);
        Assert.Equal(1048576L, probe.Length);
    }

    [Fact]
    public void FileProbe_ReadsAtMost64KiB_ExactBoundaryUsesLatin1()
    {
        var path = Path.Combine(Root, "boundary.csv");
        var bytes = new byte[65536];
        Array.Fill(bytes, (byte)'x');
        bytes[^1] = 0xC2; // incomplete UTF-8 at EOF, a literal Â in Latin-1
        File.WriteAllBytes(path, bytes);
        Assert.Equal('Â', Probe(path).HeadLines[0][^1]);
    }

    [Fact]
    public void Open_HandlerClaimsParquet_StillWins()
    {
        using var fixture = new ParquetFixture();
        DataBall.RegisterHandler(ScoredHandler.Create("custom", 0.7, 0, 99));
        Assert.Equal("Match", Detect(fixture.Source).Outcome.ToString());
        using var db = DataBall.Open(fixture.Source);
        Assert.Equal(99L, db.Query("SELECT Id FROM data")[0]["Id"]);
    }

    [Theory]
    [InlineData("stdf", 1.0, "extension")]
    [InlineData("s2p", 1.0, "extension")]
    [InlineData("prd", 1.0, "extension")]
    [InlineData("csv", 0.6, "unit header")]
    [InlineData("plain.csv", 0.0, "unit header")]
    public void LabHandlers_ScoreWithReasons(string suffix, double expected, string reason)
    {
        IFormatHandler handler = suffix switch
        {
            "stdf" => new StdfHandler(),
            "s2p" => new TouchstoneHandler(),
            "prd" => new ProductionHandler(),
            _ => new CustomCsvHandler()
        };
        var path = Write("input." + suffix, suffix == "csv" ? "EVM(dB),I_Total(A)\n1,2\n" : "Id\n1\n");
        var result = handler.Score(Probe(path));
        Assert.Equal(expected, result.Score);
        Assert.Contains(result.Reasons, r => r.Contains(reason, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Detect_FloatingPointTie_UsesPriority(int priority)
    {
        var input = Write("input.csv", "Id\n1\n");
        // Lower the threshold so the exact 0.1 + 0.2 versus 0.3 case is eligible.
        DataBall.RegisterHandler(ScoredHandler.Create("sum", 0.1 + 0.2, 0, 1, minScore: 0));
        DataBall.RegisterHandler(ScoredHandler.Create("literal", 0.3, priority, 2, minScore: 0));
        var report = DataBall.Detect(input);
        Assert.Equal(priority == 0 ? "Ambiguous" : "literal", priority == 0 ? report.Outcome.ToString() : report.Winner?.Name);
    }

    [Fact]
    public void Detect_InvalidScore_NamesHandler()
    {
        DataBall.RegisterHandler(ScoredHandler.Create("bad-score", 2, 0, 1));
        Assert.Contains("bad-score", Assert.Throws<DataBallException>(() => DataBall.Detect(Write("input.csv", "Id\n1\n"))).Message);
    }

    [Fact]
    public void Detect_InvalidMinScore_NamesHandlerOnce()
    {
        DataBall.RegisterHandler(ScoredHandler.Create("bad-min", 1, 0, 1, minScore: 2));
        var ex = Assert.Throws<DataBallException>(() => DataBall.Detect(Write("input.csv", "Id\n1\n")));
        Assert.Equal("Invalid minScore for handler 'bad-min'", ex.Message);
    }

    [Fact]
    public void Detect_UnknownExtension_NamesPath()
    {
        var input = Write("input.unknown", "Id\n1\n");
        Assert.Contains(input, DataBall.Detect(input).ToString());
    }

    [Fact]
    public void FileProbe_ParquetColumns_AreLazyAndWrapErrors()
    {
        var input = Write("broken.parquet", "PAR1xxxxPAR1");
        var probe = new FileProbe(input);
        Assert.Contains($"Failed to probe '{input}'", Assert.Throws<DataBallException>(() => probe.ParquetColumns).Message);
    }

    [Theory]
    [InlineData("csv", "CsvImport")]
    [InlineData("parquet", "ParquetView")]
    [InlineData("hive", "ParquetView")]
    [InlineData("zip", "ArchiveImport")]
    [InlineData("ball", "BallImport")]
    public void Detect_BuiltInPath_IsTyped(string kind, string expected)
    {
        using var fixture = new ParquetFixture();
        var path = kind == "hive" ? fixture.Hive : kind == "parquet" ? fixture.Source : Write("input." + kind, "Id\n1\n");
        Assert.Equal(expected, DataBall.Detect(path).BuiltInPath?.ToString());
    }

    [Fact]
    public void LabHandlers_RegisterDefaultsTwice_OpensLabCsv()
    {
        LabHandlers.RegisterDefaults();
        LabHandlers.RegisterDefaults();
        using var db = DataBall.Open(Write("lab.csv", "EVM(dB)\n1\n"));
        Assert.Equal(1.0, db.Query("SELECT EVM FROM data")[0]["EVM"]);
    }

    [Theory]
    [InlineData("stdf", "StdfHandler", "STDF")]
    [InlineData("s2p", "TouchstoneHandler", "Touchstone")]
    [InlineData("prd", "ProductionHandler", "Production")]
    public void LabHandlers_RegisterDefaultsTwice_ClaimsSlotSamples(string extension, string winner, string message)
    {
        LabHandlers.RegisterDefaults();
        LabHandlers.RegisterDefaults();
        var path = Write("sample." + extension, "x");
        var report = DataBall.Detect(path);
        Assert.Equal(DetectionOutcome.Match, report.Outcome);
        Assert.Equal(winner, report.Winner!.Name);
        var ex = Assert.Throws<DataBallException>(() => DataBall.Open(path));
        Assert.Contains(message, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private class LegacyHandler(bool claims) : IFormatHandler
    {
        public bool CanHandle(string path, Stream sniff)
        {
            Assert.Equal(0L, sniff.Position);
            Assert.Equal(100000L, sniff.Length);
            sniff.Position = 99999;
            Assert.Equal((int)'x', sniff.ReadByte());
            return claims;
        }

        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Parse(string path, Config schema,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new Dictionary<string, object?> { ["Id"] = 42L };
        }
    }

    private sealed class DefaultFirstHandler() : LegacyHandler(true);
    private sealed class DefaultSecondHandler() : LegacyHandler(true);

    public sealed class ScoredHandler(string name, double score, int priority, long id, bool claims, bool throws, double minScore) : IFormatHandler
    {
        public string Name => name;
        public int Priority => priority;
        public double MinScore => minScore;
        internal static IFormatHandler Create(string name, double score, int priority, long id, bool claims = true, bool throws = false, double minScore = 0.6)
            => new ScoredHandler(name, score, priority, id, claims, throws, minScore);
        public bool CanHandle(string path, Stream sniff) => claims;
        public DetectionScore Score(FileProbe probe)
        {
            if (throws)
                throw new InvalidOperationException("Handler must not see native files or directories");
            return new DetectionScore(score, new[] { "test evidence" });
        }
        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Parse(string path, Config schema,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new Dictionary<string, object?> { ["Id"] = id };
        }
    }
}
