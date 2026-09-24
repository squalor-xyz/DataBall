// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using squalor.DataBall.Export;
using Xunit;

namespace squalor.DataBall.Tests
{
    /// <summary>
    /// Config-declared layout (S46): import splits into tables behind the "data" view; reads,
    /// filter, export, and .ball work through the view; writes that need layout-aware routing
    /// throw a clear message until a later slice.
    /// </summary>
    public class MultiTableTests
    {
        private static readonly string[] FixtureTables = { "dc", "device", "rf", "setup", "sweep" };

        [Fact]
        public async Task Tables_Absent_DataIsBaseTable_NoOtherTables()
        {
            using var db = new DataBall();
            await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
            Assert.Equal("BASE TABLE", TableType(db, "data"));
            Assert.Equal(new[] { "data" }, BaseTables(db));
        }

        [Fact]
        public async Task Tables_CsvImport_DataIsView_AndPhysicalTablesExist()
        {
            using var db = new DataBall(TablesConfig());
            await db.ImportAsync(Fixture("semiconductor-sweep.csv"));

            Assert.Equal("VIEW", TableType(db, "data"));
            Assert.Equal(FixtureTables, BaseTables(db));
            Assert.Equal(1, Count(db, "device"));
            Assert.Equal(9, Count(db, "setup"));
            Assert.Equal(81, Count(db, "sweep"));
            Assert.Equal(81, Count(db, "rf"));
            Assert.Equal(81, Count(db, "dc"));
            Assert.Equal(81, Count(db, "data"));
        }

        [Fact]
        public async Task Tables_CsvImport_DataView_EqualsWideImport()
        {
            using var wide = new DataBall();
            await wide.ImportAsync(Fixture("semiconductor-sweep.csv"));
            using var split = new DataBall(TablesConfig());
            await split.ImportAsync(Fixture("semiconductor-sweep.csv"));

            const string sql = "SELECT * FROM data ORDER BY stimulusGrp, sweep";
            var expected = wide.Query(sql);
            var actual = split.Query(sql);
            Assert.Equal(expected.Count, actual.Count);
            Assert.Equal(expected[0].Keys, actual[0].Keys);
            for (var i = 0; i < expected.Count; i++)
            {
                foreach (var key in expected[i].Keys)
                {
                    Assert.Equal(expected[i][key], actual[i][key]);
                    if (expected[i][key] is not null)
                        Assert.Equal(expected[i][key]!.GetType(), actual[i][key]!.GetType());
                }
            }
        }

        [Fact]
        public async Task Tables_DataView_HasNoKeyColumns_AndInformationSchemaListsWideColumns()
        {
            using var db = new DataBall(TablesConfig());
            await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
            var columns = db.Query("""
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = 'main' AND table_name = 'data'
                ORDER BY ordinal_position
                """).Select(r => (string)r["column_name"]!).ToList();
            Assert.Equal(
                new[] { "stimulusGrp", "sweep", "SN", "Date", "Teststand", "Temp", "Vcc", "Frequency", "Pout", "Pin", "Gain", "I_Total", "EVM" },
                columns);
            Assert.DoesNotContain(columns, c => c.EndsWith("_key", StringComparison.Ordinal) || c == "_row");
        }

        [Fact]
        public async Task Tables_MeasGroup_AllNullRowsNotStored_ViewShowsNull()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "sparse.csv");
                File.WriteAllText(csv, "id,a,b\n1,1,\n2,,\n3,3,30\n");
                var config = WriteConfig(dir, """{ "tables": { "g": { "kind": "measurements", "columns": ["b"] } } }""");
                using var db = new DataBall(config);
                await db.ImportAsync(csv);

                Assert.Equal(1, Count(db, "g"));
                var rows = db.Query("SELECT id, a, b FROM data ORDER BY id");
                Assert.Equal(3, rows.Count);
                Assert.Null(rows[0]["b"]);
                Assert.Null(rows[1]["a"]);
                Assert.Null(rows[1]["b"]);
                Assert.Equal(30L, rows[2]["b"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Tables_DimensionNullKeys_RowsSurviveJoin()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "sites.csv");
                File.WriteAllText(csv, "Site,Meas\n,1\nA,2\n,3\n");
                var config = WriteConfig(dir, """{ "tables": { "site": { "kind": "dimension", "columns": ["Site"] } } }""");
                using var db = new DataBall(config);
                await db.ImportAsync(csv);

                Assert.Equal(2, Count(db, "site"));
                var rows = db.Query("SELECT Site, Meas FROM data ORDER BY Meas");
                Assert.Equal(3, rows.Count);
                Assert.Null(rows[0]["Site"]);
                Assert.Equal("A", rows[1]["Site"]);
                Assert.Null(rows[2]["Site"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Tables_DimensionKeyDoesNotDetermineColumns_Throws()
        {
            var dir = TempDir();
            try
            {
                var config = WriteConfig(dir, """
                    { "tables": { "setup": { "kind": "dimension", "columns": ["Teststand", "Temp"], "key": ["Teststand"] } } }
                    """);
                using var db = new DataBall(config);
                var ex = await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(Fixture("semiconductor-sweep.csv")));
                Assert.Contains("do not determine", FullMessage(ex), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(db.Query("SELECT COUNT(*) AS c FROM information_schema.tables WHERE table_name = 'setup'"), r => Convert.ToInt64(r["c"]) > 0);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Tables_Filter_Count_Export_ThroughView()
        {
            var dir = TempDir();
            try
            {
                using var wide = new DataBall();
                await wide.ImportAsync(Fixture("semiconductor-sweep.csv"));
                using var split = new DataBall(TablesConfig());
                await split.ImportAsync(Fixture("semiconductor-sweep.csv"));

                var temp = Convert.ToDouble(wide.Query("SELECT MIN(Temp) AS t FROM data")[0]["t"]);
                var filter = new SessionFilter
                {
                    Predicates = new List<ColumnPredicate> { new() { Column = "Temp", Op = PredicateOp.Eq, Value = temp } },
                    Columns = new List<string> { "sweep", "Temp", "EVM" }
                };
                Assert.Equal(wide.Count(filter), split.Count(filter));
                Assert.Equal(27, split.Count(filter));
                var rows = split.Filter(filter);
                Assert.Equal(27, rows.Count);
                Assert.Equal(new[] { "sweep", "Temp", "EVM" }, rows[0].Keys);

                var wideCsv = Path.Combine(dir, "wide.csv");
                var splitCsv = Path.Combine(dir, "split.csv");
                await wide.ExportAsync(wideCsv, ExportType.Csv);
                await split.ExportAsync(splitCsv, ExportType.Csv);
                var wideLines = File.ReadAllLines(wideCsv);
                var splitLines = File.ReadAllLines(splitCsv);
                Assert.Equal(wideLines[0], splitLines[0]);
                Assert.Equal(wideLines.Length, splitLines.Length);
                Assert.Equal(wideLines.Skip(1).OrderBy(l => l, StringComparer.Ordinal), splitLines.Skip(1).OrderBy(l => l, StringComparer.Ordinal));

                split.ApplyFilter(filter);
                var filteredCsv = Path.Combine(dir, "filtered.csv");
                await split.ExportAsync(filteredCsv, ExportType.Csv);
                Assert.Equal(28, File.ReadAllLines(filteredCsv).Length);

                var hive = Path.Combine(dir, "hive");
                split.ApplyFilter(null);
                await split.ExportAsync(Path.Combine(dir, "split.parquet"), ExportType.Parquet);
                using var back = new DataBall();
                await back.ImportAsync(Path.Combine(dir, "split.parquet"));
                Assert.Equal(81, Count(back, "data"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Tables_ReplaceImport_RebuildsLayout_NoDanglingView()
        {
            using var db = new DataBall(TablesConfig());
            await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
            await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
            Assert.Equal("VIEW", TableType(db, "data"));
            Assert.Equal(FixtureTables, BaseTables(db));
            Assert.Equal(81, Count(db, "data"));
            Assert.Equal(9, Count(db, "setup"));
        }

        [Fact]
        public async Task Tables_ReplaceImport_Failure_KeepsPreviousLayout()
        {
            var dir = TempDir();
            try
            {
                using var db = new DataBall(TablesConfig());
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));

                var bad = Path.Combine(dir, "bad.csv");
                File.WriteAllText(bad, "_row,Temp\n1,25\n");
                await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(bad));

                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Equal(FixtureTables, BaseTables(db));
                Assert.Equal(81, Count(db, "data"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Tables_FileBacked_ReopenWithConfig_Reads_WithoutConfig_Throws()
        {
            var dir = TempDir();
            var path = Path.Combine(dir, "session.duckdb");
            try
            {
                using (var db = new DataBall(TablesConfig(), databasePath: path))
                    await db.ImportAsync(Fixture("semiconductor-sweep.csv"));

                using (var reopened = new DataBall(TablesConfig(), databasePath: path))
                {
                    Assert.Equal("VIEW", TableType(reopened, "data"));
                    Assert.Equal(81, Count(reopened, "data"));
                    Assert.Equal(9, Count(reopened, "setup"));
                }

                var ex = Assert.Throws<DataBallException>(() => new DataBall(databasePath: path));
                Assert.Contains("tables", ex.Message, StringComparison.OrdinalIgnoreCase);

                using var again = new DataBall(TablesConfig(), databasePath: path);
                Assert.Equal(81, Count(again, "data"));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        public static IEnumerable<object[]> UnsupportedOps()
        {
            yield return new object[] { "AddColumn<int>", new Action<DataBall>(db => db.AddColumn<int>("X", Enumerable.Repeat(1, 81))) };
            yield return new object[] { "AddColumn", new Action<DataBall>(db => db.AddColumn("X", Enumerable.Repeat("a", 81))) };
            yield return new object[] { "RemoveColumn", new Action<DataBall>(db => db.RemoveColumn("EVM")) };
            yield return new object[] { "AddRow", new Action<DataBall>(db => db.AddRow(new Dictionary<string, object?> { ["EVM"] = 1.0 })) };
            yield return new object[] { "AddRows", new Action<DataBall>(db => db.AddRows(new[] { new Dictionary<string, object?> { ["EVM"] = 1.0 } })) };
            yield return new object[] { "InitializeRow", new Action<DataBall>(db => db.InitializeRow()) };
            yield return new object[] { "MergeOrAppend", new Action<DataBall>(db => { using var other = new DataBall(); db.MergeOrAppend(other, true); }) };
            yield return new object[] { "Bounce", new Action<DataBall>(db => db.Bounce().GetAwaiter().GetResult()) };
            yield return new object[] { "Squish", new Action<DataBall>(db => db.Squish().GetAwaiter().GetResult()) };
            yield return new object[] { "ImportAsync append", new Action<DataBall>(db => db.ImportAsync(Fixture("semiconductor-sweep.csv"), new ImportOptions { Append = true }).GetAwaiter().GetResult()) };
        }

        [Theory]
        [MemberData(nameof(UnsupportedOps))]
        public async Task Tables_UnsupportedOps_ThrowClearMessage(string name, Action<DataBall> op)
        {
            using var db = new DataBall(TablesConfig());
            await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
            var ex = Assert.Throws<DataBallException>(() => op(db));
            Assert.Contains("not supported on a multi-table session yet", ex.Message, StringComparison.Ordinal);
            Assert.Equal("VIEW", TableType(db, "data"));
            Assert.Equal(81, Count(db, "data"));
            _ = name;
        }

        [Fact]
        public void Tables_AddRows_OnEmptyLayoutSession_SplitsOnce()
        {
            var dir = TempDir();
            try
            {
                var config = WriteConfig(dir, """{ "tables": { "site": { "kind": "dimension", "columns": ["Site"] }, "g": { "kind": "measurements", "columns": ["Meas"] } } }""");
                using var db = new DataBall(config);
                db.AddRows(new[]
                {
                    new Dictionary<string, object?> { ["Site"] = "A", ["Meas"] = 1.0 },
                    new Dictionary<string, object?> { ["Site"] = "A", ["Meas"] = 2.0 },
                    new Dictionary<string, object?> { ["Site"] = "B", ["Meas"] = null },
                });
                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Equal(2, Count(db, "site"));
                Assert.Equal(2, Count(db, "g"));
                Assert.Equal(3, Count(db, "data"));
                Assert.Throws<DataBallException>(() => db.AddRows(new[] { new Dictionary<string, object?> { ["Site"] = "C" } }));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Tables_BadLayoutConfig_FailsAtConstruction()
        {
            var dir = TempDir();
            try
            {
                var config = WriteConfig(dir, """{ "tables": { "data": { "kind": "measurements" } } }""");
                Assert.Throws<DataBallException>(() => new DataBall(config));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Ball_WithLayout_ContainsManifestTablesAndDataParquet_AndRoundTrips()
        {
            var dir = TempDir();
            try
            {
                var ball = Path.Combine(dir, "sweep.ball");
                using (var db = new DataBall(TablesConfig()))
                {
                    await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                    db.SetMetadata("Operator", "Ada");
                    await db.SaveAsync(ball);
                }

                using (var zip = ZipFile.OpenRead(ball))
                {
                    var names = zip.Entries.Select(e => e.FullName).ToList();
                    Assert.Contains("data.parquet", names);
                    Assert.Contains("manifest.json", names);
                    Assert.Contains("config.json", names);
                    foreach (var table in FixtureTables)
                        Assert.Contains("tables/" + table + ".parquet", names);
                }

                // No config on the reader: config.json in the ball declares the layout.
                using var imported = new DataBall();
                await imported.ImportAsync(ball);
                Assert.Equal("VIEW", TableType(imported, "data"));
                Assert.Equal(FixtureTables, BaseTables(imported));
                Assert.Equal(81, Count(imported, "data"));
                Assert.Equal(9, Count(imported, "setup"));
                Assert.Equal("Ada", imported.Metadata["Operator"]);
                var row = imported.Query("SELECT Temp, EVM, Date FROM data ORDER BY stimulusGrp, sweep LIMIT 1")[0];
                Assert.IsType<double>(row["Temp"]);
                Assert.IsType<double>(row["EVM"]);
                Assert.IsType<DateTime>(row["Date"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Ball_WithLayout_Filtered_WritesOnlyWideParquet()
        {
            var dir = TempDir();
            try
            {
                var ball = Path.Combine(dir, "filtered.ball");
                using (var db = new DataBall(TablesConfig()))
                {
                    await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                    db.ApplyFilter(new SessionFilter
                    {
                        Predicates = new List<ColumnPredicate> { new() { Column = "sweep", Op = PredicateOp.Le, Value = 1L } }
                    });
                    await db.SaveAsync(ball);
                }

                using (var zip = ZipFile.OpenRead(ball))
                {
                    var names = zip.Entries.Select(e => e.FullName).ToList();
                    Assert.Contains("data.parquet", names);
                    Assert.DoesNotContain("manifest.json", names);
                    Assert.DoesNotContain(names, n => n.StartsWith("tables/", StringComparison.Ordinal));
                }

                using var imported = new DataBall();
                await imported.ImportAsync(ball);
                Assert.Equal("VIEW", TableType(imported, "data"));
                Assert.Equal(27, Count(imported, "data"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Ball_V1_WideOnly_IntoLayoutSession_Splits()
        {
            var dir = TempDir();
            try
            {
                var ball = Path.Combine(dir, "wide.ball");
                using (var db = new DataBall())
                {
                    await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                    await db.SaveAsync(ball);
                }

                using (var zip = ZipFile.OpenRead(ball))
                    Assert.DoesNotContain(zip.Entries, e => e.FullName == "manifest.json");

                using var imported = new DataBall(TablesConfig());
                await imported.ImportAsync(ball);
                Assert.Equal("VIEW", TableType(imported, "data"));
                Assert.Equal(FixtureTables, BaseTables(imported));
                Assert.Equal(81, Count(imported, "data"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Ball_V2_ConfigJsonTables_OverrideSessionLayout_LikeEveryOverlay()
        {
            var dir = TempDir();
            try
            {
                var ball = Path.Combine(dir, "sweep.ball");
                using (var db = new DataBall(TablesConfig()))
                {
                    await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                    await db.SaveAsync(ball);
                }

                // The ball's config.json is an overlay on the session config; its tables win, as columns/relationships do.
                var other = WriteConfig(dir, """{ "tables": { "rf": { "kind": "measurements", "columns": ["EVM", "Gain"] } } }""");
                using var imported = new DataBall(other);
                await imported.ImportAsync(ball);
                Assert.Equal("VIEW", TableType(imported, "data"));
                Assert.Equal(FixtureTables, BaseTables(imported));
                Assert.Equal(81, Count(imported, "data"));
                Assert.Equal(new[] { "device", "setup", "sweep", "rf", "dc" }, imported.Schema.Tables.Keys);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Ball_V2_MissingTableParquet_FallsBackToWideParquet_AndResplits()
        {
            var dir = TempDir();
            try
            {
                var ball = Path.Combine(dir, "sweep.ball");
                using (var db = new DataBall(TablesConfig()))
                {
                    await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                    await db.SaveAsync(ball);
                }

                using (var zip = ZipFile.Open(ball, ZipArchiveMode.Update))
                    zip.GetEntry("tables/dc.parquet")!.Delete();

                using var imported = new DataBall();
                await imported.ImportAsync(ball);
                Assert.Equal("VIEW", TableType(imported, "data"));
                Assert.Equal(FixtureTables, BaseTables(imported));
                Assert.Equal(81, Count(imported, "data"));
                Assert.Equal(81, Count(imported, "dc"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Tables_Schema_ReturnsTablesSnapshot()
        {
            using var db = new DataBall(TablesConfig());
            await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
            var schema = db.Schema;
            Assert.Equal(new[] { "device", "setup", "sweep", "rf", "dc" }, schema.Tables.Keys);
            schema.Tables.Clear();
            Assert.Equal(5, db.Schema.Tables.Count);
        }

        private static string TableType(DataBall db, string table)
        {
            var rows = db.Query($"SELECT table_type FROM information_schema.tables WHERE table_schema = 'main' AND table_name = '{table}'");
            return rows.Count == 0 ? "" : (string)rows[0]["table_type"]!;
        }

        private static string[] BaseTables(DataBall db)
        {
            return db.Query("""
                SELECT table_name FROM information_schema.tables
                WHERE table_schema = 'main' AND table_type = 'BASE TABLE' AND table_name <> 'meta'
                ORDER BY table_name
                """).Select(r => (string)r["table_name"]!).ToArray();
        }

        private static long Count(DataBall db, string table)
        {
            return Convert.ToInt64(db.Query($"SELECT COUNT(*) AS c FROM \"{table}\"")[0]["c"]);
        }

        private static string FullMessage(Exception ex)
        {
            var parts = new List<string>();
            for (Exception? e = ex; e is not null; e = e.InnerException)
                parts.Add(e.Message);
            return string.Join(" | ", parts);
        }

        private static string TablesConfig() => Fixture("semiconductor-sweep.tables.json");

        private static string Fixture(string name)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
            Assert.True(File.Exists(path), "missing fixture " + path);
            return path;
        }

        private static string WriteConfig(string dir, string json)
        {
            var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            return path;
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-tables-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void TryDeleteDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
