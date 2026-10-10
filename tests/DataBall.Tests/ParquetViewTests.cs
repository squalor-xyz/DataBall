// SPDX-License-Identifier: Apache-2.0
using System.Security.Cryptography;
using Xunit;

namespace squalor.DataBall.Tests;

public class ParquetViewTests
{
    [Fact]
    public void Open_Parquet_DataIsView()
    {
        using var f = new ParquetFixture();
        using var db = DataBall.Open(f.Source);
        AssertView(db);
        Assert.Equal(new[] { 1, 2 }, Ids(db));
        Assert.Equal(new[] { "Id", "Site", "Day" }, db.Store.GetColumns().Select(c => c.Name));
    }

    [Fact]
    public async Task Open_HiveDir_RowsEqualImport()
    {
        using var f = new ParquetFixture();
        using var db = DataBall.Open(f.Hive);
        using var imported = new DataBall();
        await imported.ImportAsync(f.Hive);
        AssertView(db);
        Assert.Equal(new[] { 1, 2 }, Ids(db));
        Assert.Equal(new[] { ("Id", "INTEGER"), ("Site", "VARCHAR"), ("Day", "TIMESTAMP") }, db.Store.GetColumns());
        Assert.Equal(new[] { ("Id", "INTEGER"), ("Site", "VARCHAR"), ("Day", "TIMESTAMP") }, imported.Store.GetColumns());
        Assert.Equal(new[] { "007", "007" }, db.Query("SELECT Site FROM data ORDER BY Id").Select(r => r["Site"]));
        Assert.Equal(new[] { new DateTime(2026, 1, 1), new DateTime(2026, 1, 2) },
            db.Query("SELECT Day FROM data ORDER BY Id").Select(r => (DateTime)r["Day"]!));
    }

    [Fact]
    public async Task Open_Parquet_DateColumnsMatchImport()
    {
        using var f = new ParquetFixture();
        var config = f.Config();
        File.WriteAllText(config, "{\"columns\":{\"Id\":\"string\"},\"metadataFields\":[]}");
        using var db = DataBall.Open(f.Source, config);
        using var imported = new DataBall(config);
        await imported.ImportAsync(f.Source);
        AssertView(db);
        Assert.Equal("TIMESTAMP", db.Store.GetColumns().Single(c => c.Name == "Day").DuckDbType);
        Assert.Equal(new[] { ("Id", "INTEGER"), ("Site", "VARCHAR"), ("Day", "TIMESTAMP") }, db.Store.GetColumns());
        Assert.Equal(new[] { ("Id", "INTEGER"), ("Site", "VARCHAR"), ("Day", "TIMESTAMP") }, imported.Store.GetColumns());
        Assert.Equal(new DateTime(2026, 1, 1), db.Query("SELECT Day FROM data WHERE Id = 1")[0]["Day"]);
    }

    [Fact]
    public void Open_Parquet_FilterPushesDown()
    {
        using var f = new ParquetFixture();
        using var db = DataBall.Open(f.Source);
        var filter = new SessionFilter
        {
            Columns = new[] { "Id" },
            Predicates = new[] { new ColumnPredicate { Column = "Id", Op = PredicateOp.Eq, Value = 2 } }
        };
        var sql = db.BuildSelectSql(filter);
        var plan = (string)db.Store.Query("EXPLAIN " + sql.Sql, sql.Parameters)[0]["explain_value"]!;
        Assert.Contains("READ_PARQUET", plan);
        Assert.Contains("Filters: Id=2", plan);
        Assert.Contains("Projections: Id", plan);
        Assert.Equal(new[] { 2 }, db.Filter(filter).Select(r => Convert.ToInt32(r["Id"])));
    }

    [Fact]
    public void Open_NonHiveDirectory_Throws()
    {
        using var f = new ParquetFixture();
        var empty = f.PathFor("empty");
        Directory.CreateDirectory(empty);
        var ex = Assert.Throws<DataBallException>(() => DataBall.Open(empty));
        Assert.Contains("not a hive Parquet directory", ex.Message);
    }

    [Theory]
    [InlineData("AddRow")]
    [InlineData("AddRows")]
    [InlineData("AddColumn")]
    [InlineData("RemoveColumn")]
    [InlineData("ImportAsync")]
    [InlineData("MergeOrAppend")]
    [InlineData("Bounce")]
    [InlineData("Squish")]
    [InlineData("InitializeRow")]
    public async Task ViewSession_FirstWrite_CopiesIn(string operation)
    {
        using var f = new ParquetFixture();
        using var db = DataBall.Open(f.Source);
        AssertView(db);
        var hash = SHA256.HashData(File.ReadAllBytes(f.Source));
        var expectedIds = new[] { 1, 2 };
        switch (operation)
        {
            case "AddRow":
                db.AddRow(new Dictionary<string, object?> { ["Id"] = 3 });
                expectedIds = new[] { 1, 2, 3 };
                break;
            case "AddRows":
                db.AddRows(new[] { new Dictionary<string, object?> { ["Id"] = 3 } });
                expectedIds = new[] { 1, 2, 3 };
                break;
            case "AddColumn": db.AddColumn("Extra", new[] { 10, 20 }); break;
            case "RemoveColumn": db.RemoveColumn("Site"); break;
            case "ImportAsync":
                await db.ImportAsync(f.Source, new ImportOptions { Append = true });
                expectedIds = new[] { 1, 1, 2, 2 };
                break;
            case "MergeOrAppend":
                using (var other = new DataBall())
                {
                    other.AddRow(new Dictionary<string, object?> { ["Id"] = 3 });
                    db.MergeOrAppend(other, true);
                }
                expectedIds = new[] { 1, 2, 3 };
                break;
            case "Bounce": await db.Bounce(); break;
            case "Squish": await db.Squish(); break;
            case "InitializeRow":
                db.InitializeRow(new Dictionary<string, object?> { ["Id"] = 3 });
                AssertTable(db);
                db.CommitRow();
                expectedIds = new[] { 1, 2, 3 };
                break;
        }
        AssertTable(db);
        Assert.Equal(expectedIds, Ids(db));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(f.Source)));
    }

    [Theory]
    [InlineData("InitializeRow")]
    [InlineData("AddRow")]
    [InlineData("AddRows")]
    [InlineData("AddColumn")]
    [InlineData("RemoveColumn")]
    [InlineData("ImportAsync")]
    public async Task ViewSession_FailedWrite_StaysView(string operation)
    {
        using var f = new ParquetFixture();
        using var db = DataBall.Open(f.Source);
        var hash = SHA256.HashData(File.ReadAllBytes(f.Source));
        var bad = new Dictionary<string, object?> { ["Id"] = "invalid integer" };
        switch (operation)
        {
            // InitializeRow rejects the invalid name before its transaction; this case checks validation, not rollback.
            case "InitializeRow": Assert.Throws<DataBallException>(() => db.InitializeRow(new Dictionary<string, object?> { [" "] = 3 })); break;
            case "AddRow": Assert.Throws<DataBallException>(() => db.AddRow(bad)); break;
            case "AddRows": Assert.Throws<DataBallException>(() => db.AddRows(new[] { bad })); break;
            case "AddColumn": Assert.Throws<DataBallException>(() => db.AddColumn("Extra", new[] { 10 })); break;
            case "RemoveColumn": Assert.Throws<DataBallException>(() => db.RemoveColumn("Missing")); break;
            case "ImportAsync":
                var csv = f.PathFor("invalid.csv");
                File.WriteAllText(csv, "Id\ninvalid integer\n");
                await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(csv, new ImportOptions { Append = true }));
                break;
        }
        AssertView(db);
        Assert.Equal(new[] { 1, 2 }, Ids(db));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(f.Source)));
        db.InitializeRow(new Dictionary<string, object?> { ["Id"] = 3 });
        AssertTable(db);
        db.CommitRow();
        Assert.Equal(new[] { 1, 2, 3 }, Ids(db));
    }

    [Fact]
    public void Open_Parquet_WithTables_CopiesIn()
    {
        using var f = new ParquetFixture();
        using (var wide = DataBall.Open(f.Source)) AssertView(wide);
        using var db = DataBall.Open(f.Source, f.Config(layout: true));
        Assert.Equal(1L, Convert.ToInt64(db.Query("SELECT count(*) AS n FROM duckdb_tables() WHERE table_name = 'sites'")[0]["n"]));
        db.AddRow(new Dictionary<string, object?> { ["Id"] = 3, ["Site"] = "B", ["Room"] = "lab" });
        Assert.Equal("lab", db.Query("SELECT Room FROM data WHERE Id = 3")[0]["Room"]);
        File.Delete(f.Source);
        Assert.Equal(new[] { 1, 2, 3 }, Ids(db));
    }

    [Fact]
    public async Task Save_ViewSession_EmbedsByDefault()
    {
        using var f = new ParquetFixture();
        var path = f.PathFor("embedded.ball");
        using (var db = DataBall.Open(f.Source))
        {
            AssertView(db);
            await db.SaveAsync(path);
            AssertView(db);
        }
        File.Delete(f.Source);
        using var saved = DataBall.Open(path);
        AssertTable(saved);
        Assert.Equal(new[] { 1, 2 }, Ids(saved));
    }

    [Fact]
    public async Task Save_ViewSession_Link()
    {
        using var f = new ParquetFixture();
        // A large deterministic payload makes the saved-size comparison meaningful.
        using (var writer = new DataBall())
            writer.Query($"COPY (SELECT i AS Id, md5(i::VARCHAR) AS Payload FROM range(20000) t(i)) TO {DuckDbStore.QuoteString(f.Source)} (FORMAT PARQUET)");
        var embedded = f.PathFor("embedded.ball");
        var linked = f.PathFor("linked.ball");
        using (var db = DataBall.Open(f.Source))
        {
            await db.SaveAsync(embedded);
            await db.SaveAsync(linked, new SaveOptions { Link = true });
        }
        Assert.True(new FileInfo(linked).Length < new FileInfo(embedded).Length);
        using (var saved = DataBall.Open(linked))
        {
            AssertView(saved);
            Assert.Equal(20000L, saved.Count(new SessionFilter()));
            Assert.Equal("c4ca4238a0b923820dcc509a6f75849b", saved.Query("SELECT Payload FROM data WHERE Id = 1")[0]["Payload"]);
        }
        File.Delete(f.Source);
        var ex = Assert.Throws<DataBallException>(() => DataBall.Open(linked));
        Assert.Contains(f.Source, ex.Message);
    }

    [Fact]
    public async Task ImportAsync_Parquet_StillCopies()
    {
        using var f = new ParquetFixture();
        using (var opened = DataBall.Open(f.Source)) AssertView(opened);
        using var db = new DataBall();
        await db.ImportAsync(f.Source);
        AssertTable(db);
        File.Delete(f.Source);
        Assert.Equal(new[] { 1, 2 }, Ids(db));
    }

    [Fact]
    public void ViewSession_ConfigMetadataFields_InMeta()
    {
        using var f = new ParquetFixture();
        var config = f.Config(metadata: true);
        using (var db = DataBall.Open(f.Source, config))
        {
            Assert.Equal("007", db.Metadata["Site"]);
            db.SetMetadata("Operator", "Ada");
            AssertView(db);
            Assert.Equal(new[] { "Id", "Day" }, db.Store.GetColumns().Select(c => c.Name));
            Assert.Equal(new[] { 1, 2 }, Ids(db));
        }
        using (var writer = new DataBall())
            writer.Query($"COPY (SELECT 1 AS Id, '007' AS Site UNION ALL SELECT 2, 'B') TO {DuckDbStore.QuoteString(f.Source)} (FORMAT PARQUET)");
        using var varying = DataBall.Open(f.Source, config);
        Assert.Equal(2L, varying.Count(new SessionFilter()));
        Assert.Contains(varying.Store.GetColumns(), c => c.Name == "Site");
        Assert.False(varying.Metadata.ContainsKey("Site"));
    }

    [Fact]
    public async Task ViewSession_StreamOpen_WriteThrows_StaysView()
    {
        using var f = new ParquetFixture();
        using var db = DataBall.Open(f.Source);
        await using (var rows = db.ReadRowsAsync(new SessionFilter()).GetAsyncEnumerator())
        {
            Assert.True(await rows.MoveNextAsync());
            var ex = Assert.Throws<DataBallException>(() => db.AddRow(new Dictionary<string, object?> { ["Id"] = 3 }));
            Assert.Contains("stream is open", ex.Message);
        }
        AssertView(db);
        Assert.Equal(new[] { 1, 2 }, Ids(db));
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("file")]
    [InlineData("hive")]
    [InlineData("view")]
    public async Task DefaultMetadata_VaryingField_StaysColumn(string kind)
    {
        using var f = new ParquetFixture();
        var source = f.MetadataSource(kind);
        using var db = kind == "view" ? DataBall.Open(source) : new DataBall();
        if (kind != "view") await db.ImportAsync(source);
        Assert.Equal(new[] { "A", "B" }, db.Query("SELECT Lot FROM data ORDER BY Id").Select(r => r["Lot"]));
        Assert.False(db.Metadata.ContainsKey("Lot"));
    }

    [Fact]
    public async Task DefaultMetadata_NullPolicy_UsesLiftConstantFallback()
    {
        using var f = new ParquetFixture();
        using var db = new DataBall();
        db.CurrentConfig.MetadataPolicy = null;
        await db.ImportAsync(f.MetadataSource("csv"));
        Assert.Equal(new[] { "A", "B" }, db.Query("SELECT Lot FROM data ORDER BY Id").Select(r => r["Lot"]));
        Assert.False(db.Metadata.ContainsKey("Lot"));
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("file")]
    [InlineData("hive")]
    [InlineData("view")]
    public async Task DefaultMetadata_ConstantWithNulls_Lifts(string kind)
    {
        using var f = new ParquetFixture();
        var source = f.MetadataSource(kind, nulls: true);
        using var db = kind == "view" ? DataBall.Open(source) : new DataBall();
        if (kind != "view") await db.ImportAsync(source);
        Assert.Equal("A", db.Metadata["Lot"]);
        Assert.Equal(new[] { "Id" }, db.Store.GetColumns().Select(c => c.Name));
        Assert.Equal(new[] { 1, 2 }, Ids(db));
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("file")]
    [InlineData("hive")]
    [InlineData("view")]
    public async Task ExplicitRequireConstant_VaryingField_Throws(string kind)
    {
        using var f = new ParquetFixture();
        var config = f.PathFor("strict.json");
        File.WriteAllText(config, """{"metadataPolicy":"requireConstant"}""");
        var source = f.MetadataSource(kind);
        var ex = kind == "view"
            ? Assert.Throws<DataBallException>(() => DataBall.Open(source, config))
            : await ImportStrict();
        Assert.Contains("Metadata field 'Lot' is not constant", ex.Message);

        async Task<DataBallException> ImportStrict()
        {
            using var db = new DataBall(config);
            return await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(source));
        }
    }

    [Fact]
    public async Task DefaultMetadata_WideCsvAppend_DifferentValue_StaysColumn()
    {
        using var f = new ParquetFixture();
        var first = f.PathFor("first.csv");
        var second = f.PathFor("second.csv");
        File.WriteAllText(first, "Id,Lot\n1,A\n");
        File.WriteAllText(second, "Id,Lot\n2,B\n");
        using var db = new DataBall();
        await db.ImportAsync(first);
        await db.ImportAsync(second, new ImportOptions { Append = true });
        Assert.Equal(new[] { "A", "B" }, db.Query("SELECT Lot FROM data ORDER BY Id").Select(r => r["Lot"]));
        Assert.False(db.Metadata.ContainsKey("Lot"));
    }

    [Fact]
    public void Open_Parquet_RegisteredHandler_WinsOverView()
    {
        using var f = new ParquetFixture();
        DataBall.RegisterHandler(new ParquetHandler(f.Source));
        try
        {
            using var db = DataBall.Open(f.Source);
            Assert.Equal(new[] { 99 }, Ids(db));
            AssertTable(db);
        }
        finally { DataBall.ClearHandlers(); }
    }

    [Theory]
    [InlineData("ImportAsync")]
    [InlineData("MergeOrAppend")]
    public async Task ViewSession_Replace_MissingSource_Succeeds(string operation)
    {
        using var f = new ParquetFixture();
        using var db = DataBall.Open(f.Source);
        File.Delete(f.Source);
        if (operation == "ImportAsync")
        {
            var csv = f.PathFor("replace.csv");
            File.WriteAllText(csv, "Id\n3\n");
            await db.ImportAsync(csv);
        }
        else
        {
            using var other = new DataBall();
            other.AddRow(new Dictionary<string, object?> { ["Id"] = 3 });
            db.MergeOrAppend(other, append: false);
        }
        AssertTable(db);
        Assert.Equal(new[] { 3 }, Ids(db));
    }

    [Fact]
    public async Task ViewSession_FailedReplace_StaysView()
    {
        using var f = new ParquetFixture();
        using var db = DataBall.Open(f.Source);
        db.CurrentConfig.MetadataPolicy = "requireConstant";
        await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(f.MetadataSource("csv")));
        AssertView(db);
        Assert.Equal(new[] { 1, 2 }, Ids(db));
        // A second write exercises the restored backing flag, not only the SQL catalog.
        db.AddRow(new Dictionary<string, object?> { ["Id"] = 3 });
        AssertTable(db);
        Assert.Equal(new[] { 1, 2, 3 }, Ids(db));
    }

    [Fact]
    public async Task Open_Native_NonParquetViewWithoutTables_Throws()
    {
        using var f = new ParquetFixture();
        var path = f.PathFor("other-view.ball");
        using (var writer = new DataBall())
        {
            writer.Query("CREATE TABLE physical AS SELECT 1 AS Id");
            writer.Query("CREATE VIEW data AS SELECT * FROM physical");
            await writer.SaveAsync(path, new SaveOptions { Link = true });
        }
        var ex = Assert.Throws<DataBallException>(() => DataBall.Open(path));
        Assert.Equal("Session file has a multi-table layout; open it with the config that declares 'tables'", ex.Message);
    }

    [Fact]
    public async Task Open_Native_ParquetView_FailedWrite_RestoresBackingFlag()
    {
        using var f = new ParquetFixture();
        var path = f.PathFor("linked.ball");
        using (var source = DataBall.Open(f.Source))
            await source.SaveAsync(path, new SaveOptions { Link = true });
        using var db = DataBall.Open(path, writable: true);
        Assert.Throws<DataBallException>(() => db.AddRow(new Dictionary<string, object?> { ["Id"] = "invalid" }));
        AssertView(db);
        db.AddRow(new Dictionary<string, object?> { ["Id"] = 3 });
        AssertTable(db);
        Assert.Equal(new[] { 1, 2, 3 }, Ids(db));
    }

    [Fact]
    public async Task Save_LinkedBall_OwnPath_EmbedsInPlace()
    {
        using var f = new ParquetFixture();
        var path = f.PathFor("linked.ball");
        using (var source = DataBall.Open(f.Source))
            await source.SaveAsync(path, new SaveOptions { Link = true });
        using (var db = DataBall.Open(path, writable: true))
        {
            await db.SaveAsync(path);
            AssertTable(db);
            File.Delete(f.Source);
            Assert.Equal(new[] { 1, 2 }, Ids(db));
        }
        using var reopened = DataBall.Open(path);
        AssertTable(reopened);
        Assert.Equal(new[] { 1, 2 }, Ids(reopened));
    }

    [Fact]
    public void Open_HiveDir_LargeSource_UsesTempStore()
    {
        using var f = new ParquetFixture();
        var temp = f.PathFor("temp");
        using var db = DataBall.Open(f.Hive, engine: new EngineOptions { InMemoryMaxBytes = 1, TempDirectory = temp });
        Assert.Equal(temp, Path.GetDirectoryName(db.StorePath));
        AssertView(db);
        Assert.Equal(new[] { 1, 2 }, Ids(db));
    }

    [Fact]
    public void Open_HiveDir_WithTables_CopiesIn()
    {
        using var f = new ParquetFixture();
        using var db = DataBall.Open(f.Hive, f.Config(layout: true));
        Assert.Equal(1L, Convert.ToInt64(db.Query("SELECT count(*) AS n FROM duckdb_tables() WHERE table_name = 'sites'")[0]["n"]));
        Directory.Delete(f.Hive, true);
        Assert.Equal(new[] { 1, 2 }, Ids(db));
    }

    private sealed class ParquetHandler(string claimedPath) : IFormatHandler
    {
        public bool CanHandle(string path, Stream sniff) => path == claimedPath;
        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Parse(string path, Config schema,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new Dictionary<string, object?> { ["Id"] = 99 };
        }
    }

    internal static int[] Ids(DataBall db) => db.Query("SELECT Id FROM data ORDER BY Id").Select(r => Convert.ToInt32(r["Id"])).ToArray();
    internal static void AssertView(DataBall db) => Assert.Equal((1L, 0L), RelationCounts(db));
    internal static void AssertTable(DataBall db) => Assert.Equal((0L, 1L), RelationCounts(db));
    private static (long Views, long Tables) RelationCounts(DataBall db) =>
        (Convert.ToInt64(db.Query("SELECT count(*) AS n FROM duckdb_views() WHERE database_name = current_database() AND view_name = 'data'")[0]["n"]),
         Convert.ToInt64(db.Query("SELECT count(*) AS n FROM duckdb_tables() WHERE database_name = current_database() AND table_name = 'data'")[0]["n"]));

}

internal sealed class ParquetFixture : IDisposable
{
    private readonly string _dir = System.IO.Path.Combine(AppContext.BaseDirectory, "parquet-view-" + Guid.NewGuid().ToString("N"));
    internal string Source => PathFor("source.parquet");
    internal string Hive => PathFor("hive");
    internal string PathFor(string name) => System.IO.Path.Combine(_dir, name);
    internal ParquetFixture()
    {
        Directory.CreateDirectory(_dir);
        using var db = new DataBall();
        db.Query("CREATE TABLE data AS SELECT 1 AS Id, '007' AS Site, DATE '2026-01-01' AS Day UNION ALL SELECT 2, '007', DATE '2026-01-02'");
        db.Query($"COPY data TO {DuckDbStore.QuoteString(Source)} (FORMAT PARQUET)");
        db.Query($"COPY data TO {DuckDbStore.QuoteString(Hive)} (FORMAT PARQUET, PARTITION_BY (Site), WRITE_PARTITION_COLUMNS)");
        Directory.Move(System.IO.Path.Combine(Hive, "Site=007"), System.IO.Path.Combine(Hive, "Site=999"));
    }
    internal string MetadataSource(string kind, bool nulls = false)
    {
        if (kind == "csv")
        {
            var csv = PathFor("metadata.csv");
            File.WriteAllText(csv, "Id,Lot\n1,A\n2," + (nulls ? "" : "B") + "\n");
            return csv;
        }
        var path = PathFor(kind == "hive" ? "metadata-hive" : "metadata.parquet");
        using var writer = new DataBall();
        var select = "SELECT 1 AS Id, 'A' AS Lot UNION ALL SELECT 2, " + (nulls ? "NULL" : "'B'");
        writer.Query($"COPY ({select}) TO {DuckDbStore.QuoteString(path)} (FORMAT PARQUET" +
            (kind == "hive" ? ", PARTITION_BY (Id), WRITE_PARTITION_COLUMNS)" : ")"));
        return path;
    }
    internal string Config(bool metadata = false, bool layout = false)
    {
        var path = PathFor("config.json");
        File.WriteAllText(path, "{\"metadataFields\":" + (metadata ? "[\"Site\"]" : "[]") +
            ",\"tables\":" + (layout ? "{\"sites\":{\"kind\":\"dimension\",\"columns\":[\"Site\",\"Room\"]},\"rows\":{\"kind\":\"rows\",\"columns\":[\"Id\",\"Day\"]}}" : "{}") + "}");
        return path;
    }
    public void Dispose() => Directory.Delete(_dir, true);
}
