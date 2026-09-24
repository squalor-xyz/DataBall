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
    /// Config-declared layout. S46: import splits into tables behind the "data" view; reads,
    /// filter, export, and .ball work through the view. S47: row writes, append import, and
    /// MergeOrAppend route into the tables. AddColumn / RemoveColumn / Bounce on a layout throw
    /// a clear message until S48.
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
                Assert.NotNull(db.Layout);
                db.AddRows(new[] { Point(99, 1, 25.0, 3.3, -50.0) });
                Assert.Equal(82, Count(db, "data"));
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
            yield return new object[] { "Bounce", new Action<DataBall>(db => db.Bounce().GetAwaiter().GetResult()) };
            yield return new object[] { "Squish", new Action<DataBall>(db => db.Squish().GetAwaiter().GetResult()) };
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
                db.AddRows(new[] { new Dictionary<string, object?> { ["Site"] = "C", ["Meas"] = 4.0 } });
                Assert.Equal(3, Count(db, "site"));
                Assert.Equal(4, Count(db, "data"));
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

        // ---- S47: row writes route into layout tables ----

        [Fact]
        public void AddRows_WithLayout_ThreeBatches_ViewEqualsWide()
        {
            using var wide = new DataBall();
            using var split = new DataBall(TablesConfig());
            for (var batch = 0; batch < 3; batch++)
            {
                var rows = new[]
                {
                    Point(batch + 1, 1, -40.0, 3.3, -50.0 - batch),
                    Point(batch + 1, 2, -40.0, 3.3, -49.0 - batch),
                    Point(batch + 1, 3, 25.0, 5.0, -48.0 - batch),
                };
                wide.AddRows(rows);
                split.AddRows(rows);
            }

            Assert.Equal("VIEW", TableType(split, "data"));
            Assert.Equal(1, Count(split, "device"));
            Assert.Equal(2, Count(split, "setup"));
            Assert.Equal(9, Count(split, "sweep"));
            Assert.Equal(9, Count(split, "rf"));
            Assert.Equal(9, Count(split, "dc"));
            AssertSameRows(wide, split, "SELECT * FROM data ORDER BY stimulusGrp, sweep");
        }

        [Fact]
        public async Task AddRows_WithLayout_IntTempAfterDouble_ReusesSetupKey()
        {
            using var db = new DataBall(TablesConfig());
            await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
            var row = Point(99, 1, 25.0, 3.3, -50.0);
            row["Temp"] = 25;
            db.AddRows(new[] { row });
            Assert.Equal(9, Count(db, "setup"));
            Assert.Equal(82, Count(db, "sweep"));
            var added = Assert.Single(db.Query("SELECT Temp, Vcc, Teststand FROM data WHERE stimulusGrp = 99"));
            Assert.Equal(25.0, Assert.IsType<double>(added["Temp"]));
            Assert.Equal("sqBench-001", added["Teststand"]);
        }

        [Fact]
        public async Task AddRows_WithLayout_CaseInsensitiveColumn_NoNewColumn()
        {
            using var db = new DataBall(TablesConfig());
            await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
            db.AddRows(new[] { new Dictionary<string, object?> { ["stimulusGrp"] = 99L, ["sweep"] = 1L, ["evm"] = -1.5 } });
            Assert.Equal(13, ViewColumns(db).Count);
            var added = Assert.Single(db.Query("SELECT EVM FROM data WHERE stimulusGrp = 99"));
            Assert.Equal(-1.5, Assert.IsType<double>(added["EVM"]));
        }

        [Fact]
        public async Task AddRows_WithLayout_MissingDimensionColumn_NullKeyRow()
        {
            using var db = new DataBall(TablesConfig());
            await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
            db.AddRows(new[] { new Dictionary<string, object?> { ["stimulusGrp"] = 99L, ["sweep"] = 1L, ["Temp"] = 25.0 } });
            Assert.Equal(10, Count(db, "setup"));
            Assert.Equal(2, Count(db, "device"));
            var added = Assert.Single(db.Query("SELECT SN, Teststand, Temp, Vcc FROM data WHERE stimulusGrp = 99"));
            Assert.Null(added["SN"]);
            Assert.Null(added["Teststand"]);
            Assert.Null(added["Vcc"]);
            Assert.Equal(25.0, added["Temp"]);
        }

        [Fact]
        public async Task AddRows_IntoEmptyLayoutView_RowKeyStartsAtOne_GroupsJoin()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "empty.csv");
                File.WriteAllText(csv, "Site,Meas\n");
                var config = WriteConfig(dir, """
                    { "columns": { "Meas": "double" }, "tables": { "site": { "kind": "dimension", "columns": ["Site"] }, "g": { "kind": "measurements", "columns": ["Meas"] } } }
                    """);
                using var db = new DataBall(config);
                await db.ImportAsync(csv);
                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Equal(0, Count(db, "data"));

                db.AddRows(new[]
                {
                    new Dictionary<string, object?> { ["Site"] = "A", ["Meas"] = 1.0 },
                    new Dictionary<string, object?> { ["Site"] = "B", ["Meas"] = 2.0 },
                });
                var rows = db.Query("SELECT Site, Meas FROM data ORDER BY Meas");
                Assert.Equal(2, rows.Count);
                Assert.Equal("A", rows[0]["Site"]);
                Assert.Equal(1.0, rows[0]["Meas"]);
                Assert.Equal(2.0, rows[1]["Meas"]);
                Assert.Equal(1L, db.Query("SELECT MIN(\"_row\") AS m FROM rows")[0]["m"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task AddRows_WithLayout_DimensionKeyConflict_Throws_NothingInserted()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "sites.csv");
                File.WriteAllText(csv, "Site,Operator,Meas\nA,ann,1\nB,bob,2\n");
                var config = WriteConfig(dir, """
                    { "tables": { "site": { "kind": "dimension", "columns": ["Site", "Operator"], "key": ["Site"] } } }
                    """);
                using var db = new DataBall(config);
                await db.ImportAsync(csv);

                var ex = Assert.Throws<DataBallException>(() => db.AddRows(new[]
                {
                    new Dictionary<string, object?> { ["Site"] = "A", ["Operator"] = "zed", ["Meas"] = 3L },
                }));
                Assert.Contains("do not determine", FullMessage(ex), StringComparison.OrdinalIgnoreCase);
                Assert.Equal(2, Count(db, "site"));
                Assert.Equal(2, Count(db, "data"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task AddRows_WithLayout_Failure_RestoresLayoutAndMetadata_NextWriteSucceeds()
        {
            using var db = new DataBall(TablesConfig());
            await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
            db.SetMetadata("Tester", "A");

            var bad = Point(99, 1, 25.0, 3.3, -50.0);
            bad["Tester"] = "B";
            bad["_row"] = 1L;
            Assert.Throws<DataBallException>(() => db.AddRows(new[] { bad }));

            Assert.Equal("A", db.Metadata["Tester"]);
            Assert.Single(db.Query("SELECT 1 AS one FROM meta WHERE \"key\" = 'Tester'"));
            Assert.NotNull(db.Layout);
            Assert.Equal(13, ViewColumns(db).Count);
            Assert.Equal(81, Count(db, "data"));

            db.AddRows(new[] { Point(99, 1, 25.0, 3.3, -50.0) });
            Assert.Equal(82, Count(db, "data"));
        }

        [Fact]
        public async Task Import_Append_WithLayout_Fixture_ReusesKeys()
        {
            using var db = new DataBall(TablesConfig());
            await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
            await db.ImportAsync(Fixture("semiconductor-sweep.csv"), new ImportOptions { Append = true });
            Assert.Equal(162, Count(db, "sweep"));
            Assert.Equal(162, Count(db, "data"));
            Assert.Equal(9, Count(db, "setup"));
            Assert.Equal(1, Count(db, "device"));
            Assert.Equal(162L, db.Query("SELECT COUNT(DISTINCT \"_row\") AS c FROM sweep")[0]["c"]);
        }

        [Fact]
        public async Task Import_Append_WithLayout_UnitHeadersRenamedBeforeSplit()
        {
            var dir = TempDir();
            try
            {
                using var db = new DataBall(TablesConfig());
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                var csv = Path.Combine(dir, "more.csv");
                File.WriteAllText(csv, "stimulusGrp(id),sweep(id),EVM(dB),Temp(degC)\n99,1,-1.5,25.0\n");
                await db.ImportAsync(csv, new ImportOptions { Append = true });

                Assert.Equal(13, ViewColumns(db).Count);
                var added = Assert.Single(db.Query("SELECT EVM, Temp FROM data WHERE stimulusGrp = 99"));
                Assert.Equal(-1.5, Assert.IsType<double>(added["EVM"]));
                Assert.Equal(25.0, Assert.IsType<double>(added["Temp"]));
                Assert.Equal(82, Count(db, "data"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Import_Append_WithLayout_AllMetadataColumns_AddsNullRows()
        {
            var dir = TempDir();
            try
            {
                var (db, first) = await MetaSession(dir);
                using (db)
                {
                    var only = Path.Combine(dir, "only.csv");
                    File.WriteAllText(only, "Tester\nT1\nT1\nT1\n");
                    await db.ImportAsync(only, new ImportOptions { Append = true });
                    Assert.Equal(5, Count(db, "data"));
                    Assert.Equal(3L, db.Query("SELECT COUNT(*) AS c FROM data WHERE Meas IS NULL")[0]["c"]);
                    Assert.Equal("T1", db.Metadata["Tester"]);
                    Assert.DoesNotContain("Tester", ViewColumns(db));
                }
                _ = first;
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Import_Append_WithLayout_MetadataEqual_StaysMetadata()
        {
            var dir = TempDir();
            try
            {
                var (db, _) = await MetaSession(dir);
                using (db)
                {
                    var more = Path.Combine(dir, "more.csv");
                    File.WriteAllText(more, "Tester,Meas\nT1,3\n");
                    await db.ImportAsync(more, new ImportOptions { Append = true });
                    Assert.Equal(3, Count(db, "data"));
                    Assert.Equal("T1", db.Metadata["Tester"]);
                    Assert.DoesNotContain("Tester", ViewColumns(db));
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Import_Append_WithLayout_MetadataConflict_DemotesToSpineColumn()
        {
            var dir = TempDir();
            try
            {
                var (db, _) = await MetaSession(dir);
                using (db)
                {
                    var more = Path.Combine(dir, "more.csv");
                    File.WriteAllText(more, "Tester,Meas\nT2,3\n");
                    await db.ImportAsync(more, new ImportOptions { Append = true });
                    Assert.False(db.Metadata.ContainsKey("Tester"));
                    Assert.Contains("Tester", ViewColumns(db));
                    var rows = db.Query("SELECT Tester, Meas FROM data ORDER BY Meas");
                    Assert.Equal(new[] { "T1", "T1", "T2" }, rows.Select(r => (string)r["Tester"]!));
                    Assert.Contains("Tester", db.Query("SELECT column_name FROM information_schema.columns WHERE table_name = 'rows'").Select(r => (string)r["column_name"]!));
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Import_Append_WithLayout_MetadataPolicyFirst_KeepsExisting()
        {
            var dir = TempDir();
            try
            {
                var (db, _) = await MetaSession(dir, """ "metadataPolicy": "first", """);
                using (db)
                {
                    var more = Path.Combine(dir, "more.csv");
                    File.WriteAllText(more, "Tester,Meas\nT2,3\n");
                    await db.ImportAsync(more, new ImportOptions { Append = true });
                    Assert.Equal("T1", db.Metadata["Tester"]);
                    Assert.DoesNotContain("Tester", ViewColumns(db));
                    Assert.Equal(3, Count(db, "data"));
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportManager_ImportFromCsv_Append_OnLayout_Routes()
        {
            using var db = new DataBall(TablesConfig());
            await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
            Import.ImportManager.ImportFromCsv(db, Fixture("semiconductor-sweep.csv"), append: true);
            Assert.Equal(162, Count(db, "data"));
            Assert.Equal(9, Count(db, "setup"));
            Assert.Equal("VIEW", TableType(db, "data"));
        }

        [Fact]
        public async Task CommitRow_WithLayout_CopiesHighestRow_ModifiesExistingField_TriggerReset()
        {
            var dir = TempDir();
            try
            {
                var config = WriteConfig(dir, FixtureTablesJson(extraTop: """ "relationships": [ { "trigger": "Temp", "reset": ["EVM"] } ], """));
                using var db = new DataBall(config);
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));

                db.InitializeRow();
                db.ModifyField("sweep", 4L);
                db.CommitRow();
                var copied = Assert.Single(db.Query("SELECT stimulusGrp, Temp, Vcc, EVM FROM data WHERE sweep = 4"));
                Assert.Equal(27L, copied["stimulusGrp"]);
                Assert.Equal(85.0, copied["Temp"]);
                Assert.NotNull(copied["EVM"]);
                Assert.Equal(82, Count(db, "data"));
                Assert.Equal(9, Count(db, "setup"));

                db.InitializeRow();
                db.ModifyField("sweep", 5L);
                db.ModifyField("Temp", 30.0);
                db.CommitRow();
                var reset = Assert.Single(db.Query("SELECT Temp, EVM, Vcc FROM data WHERE sweep = 5"));
                Assert.Equal(30.0, reset["Temp"]);
                Assert.Null(reset["EVM"]);
                Assert.Equal(5.0, reset["Vcc"]);
                Assert.Equal(83, Count(db, "data"));
                Assert.Equal(10, Count(db, "setup"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task AddRow_WithLayout_NewUnclaimedColumn_LandsOnSpine_ViewRefreshed()
        {
            using var db = new DataBall(TablesConfig());
            await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
            db.AddRow(new Dictionary<string, object?> { ["stimulusGrp"] = 99L, ["sweep"] = 1L, ["Note"] = "x" });

            Assert.Equal(14, ViewColumns(db).Count);
            Assert.Equal("Note", ViewColumns(db)[13]);
            Assert.Contains("Note", db.Query("SELECT column_name FROM information_schema.columns WHERE table_name = 'sweep'").Select(r => (string)r["column_name"]!));
            Assert.Equal(81L, db.Query("SELECT COUNT(*) AS c FROM data WHERE Note IS NULL")[0]["c"]);
            Assert.Equal("x", Assert.Single(db.Query("SELECT Note FROM data WHERE stimulusGrp = 99"))["Note"]);
        }

        [Fact]
        public async Task AddRow_WithLayout_NewColumnClaimedByOmittedGroup_CreatesTable()
        {
            var dir = TempDir();
            try
            {
                var config = WriteConfig(dir, FixtureTablesJson(extraTables: """, "rf2": { "kind": "measurements", "columns": ["Pin2"] }"""));
                using var db = new DataBall(config);
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                Assert.DoesNotContain("rf2", BaseTables(db));

                db.AddRow(new Dictionary<string, object?> { ["stimulusGrp"] = 99L, ["sweep"] = 1L, ["Pin2"] = 1.5 });
                Assert.Contains("rf2", BaseTables(db));
                Assert.Equal(1, Count(db, "rf2"));
                Assert.Equal(1.5, Assert.Single(db.Query("SELECT Pin2 FROM data WHERE stimulusGrp = 99"))["Pin2"]);
                Assert.Equal(82, Count(db, "data"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task AddRow_WithLayout_NewDimensionColumn_ThrowsNotYet()
        {
            var dir = TempDir();
            try
            {
                var config = WriteConfig(dir, FixtureTablesJson().Replace("\"Teststand\", \"Temp\", \"Vcc\"", "\"Teststand\", \"Temp\", \"Vcc\", \"Humidity\"", StringComparison.Ordinal));
                using var db = new DataBall(config);
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                var ex = Assert.Throws<DataBallException>(() => db.AddRow(new Dictionary<string, object?> { ["stimulusGrp"] = 99L, ["sweep"] = 1L, ["Humidity"] = 40.0 }));
                Assert.Contains("dimension 'setup'", FullMessage(ex), StringComparison.Ordinal);
                Assert.Equal(81, Count(db, "data"));
                Assert.Equal(13, ViewColumns(db).Count);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task AddRow_WithLayout_NewDimensionTable_ThrowsNotYet()
        {
            var dir = TempDir();
            try
            {
                var config = WriteConfig(dir, FixtureTablesJson(extraTables: """, "env": { "kind": "dimension", "columns": ["Humidity"] }"""));
                using var db = new DataBall(config);
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                var ex = Assert.Throws<DataBallException>(() => db.AddRow(new Dictionary<string, object?> { ["stimulusGrp"] = 99L, ["sweep"] = 1L, ["Humidity"] = 40.0 }));
                Assert.Contains("dimension 'env'", FullMessage(ex), StringComparison.Ordinal);
                Assert.Equal(81, Count(db, "data"));
                Assert.DoesNotContain("env", BaseTables(db));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task MergeOrAppend_WithLayout_Append_ReusesKeys()
        {
            using var other = new DataBall();
            await other.ImportAsync(Fixture("semiconductor-sweep.csv"));
            using var db = new DataBall(TablesConfig());
            await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
            db.MergeOrAppend(other, append: true);
            Assert.Equal(162, Count(db, "data"));
            Assert.Equal(9, Count(db, "setup"));
        }

        [Fact]
        public async Task MergeOrAppend_WithLayout_Replace_IsView()
        {
            using var other = new DataBall();
            await other.ImportAsync(Fixture("semiconductor-sweep.csv"));
            using var db = new DataBall(TablesConfig());
            await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
            db.MergeOrAppend(other, append: false);
            Assert.Equal("VIEW", TableType(db, "data"));
            Assert.Equal(81, Count(db, "data"));
            Assert.NotNull(db.Layout);
        }

        [Fact]
        public async Task Tables_FileBacked_AppendThenReopen_PersistsRowsAndNewColumn()
        {
            var dir = TempDir();
            var path = Path.Combine(dir, "session.duckdb");
            try
            {
                using (var db = new DataBall(TablesConfig(), databasePath: path))
                {
                    await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                    db.AddRow(new Dictionary<string, object?> { ["stimulusGrp"] = 99L, ["sweep"] = 1L, ["Note"] = "x" });
                }

                using var reopened = new DataBall(TablesConfig(), databasePath: path);
                Assert.Equal(82, Count(reopened, "data"));
                Assert.Contains("Note", ViewColumns(reopened));
                reopened.AddRow(new Dictionary<string, object?> { ["stimulusGrp"] = 99L, ["sweep"] = 2L, ["Note"] = "y" });
                Assert.Equal(83, Count(reopened, "data"));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task Ball_V2_AfterAppend_ManifestHasNewColumn_RoundTrips()
        {
            var dir = TempDir();
            try
            {
                var ball = Path.Combine(dir, "sweep.ball");
                using (var db = new DataBall(TablesConfig()))
                {
                    await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                    db.AddRow(new Dictionary<string, object?> { ["stimulusGrp"] = 99L, ["sweep"] = 1L, ["Note"] = "x" });
                    await db.SaveAsync(ball);
                }

                using (var zip = ZipFile.OpenRead(ball))
                {
                    using var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open());
                    var manifest = System.Text.Json.JsonSerializer.Deserialize<BallManifest>(reader.ReadToEnd(), BallManifest.JsonOptions)!;
                    Assert.Equal("Note", manifest.Columns[^1]);
                }

                using var imported = new DataBall();
                await imported.ImportAsync(ball);
                Assert.Equal("VIEW", TableType(imported, "data"));
                Assert.Equal(82, Count(imported, "data"));
                Assert.Equal("x", Assert.Single(imported.Query("SELECT Note FROM data WHERE stimulusGrp = 99"))["Note"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // ---- S47 code-review regressions ----

        [Fact]
        public async Task Ball_Append_WithDifferentTablesConfig_Throws_SessionIntact()
        {
            var dir = TempDir();
            try
            {
                var ball = Path.Combine(dir, "other.ball");
                var other = WriteConfig(dir, """{ "tables": { "rf": { "kind": "measurements", "columns": ["EVM", "Gain"] } } }""");
                using (var src = new DataBall(other))
                {
                    await src.ImportAsync(Fixture("semiconductor-sweep.csv"));
                    await src.SaveAsync(ball);
                }

                using var db = new DataBall(TablesConfig());
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                var ex = await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(ball, new ImportOptions { Append = true }));
                Assert.Contains("different 'tables'", FullMessage(ex), StringComparison.Ordinal);

                Assert.Equal(new[] { "device", "setup", "sweep", "rf", "dc" }, db.Schema.Tables.Keys);
                Assert.Equal(FixtureTables, BaseTables(db));
                Assert.Equal(81, Count(db, "data"));
                db.AddRows(new[] { Point(99, 1, 25.0, 3.3, -50.0) });
                Assert.Equal(82, Count(db, "data"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Ball_Replace_FailingBallWithOtherConfig_RestoresConfigLayoutAndMetadata()
        {
            var dir = TempDir();
            try
            {
                var ball = Path.Combine(dir, "bad.ball");
                var other = WriteConfig(dir, """{ "tables": { "rf": { "kind": "measurements", "columns": ["EVM", "Gain"] } } }""");
                using (var src = new DataBall(other))
                {
                    await src.ImportAsync(Fixture("semiconductor-sweep.csv"));
                    await src.SaveAsync(ball);
                }
                using (var zip = ZipFile.Open(ball, ZipArchiveMode.Update))
                {
                    zip.GetEntry("metadata.json")!.Delete();
                    using var w = new StreamWriter(zip.CreateEntry("metadata.json").Open());
                    w.Write("{ not json");
                }

                using var db = new DataBall(TablesConfig());
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                db.SetMetadata("Operator", "Ada");
                await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(ball));

                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Equal(FixtureTables, BaseTables(db));
                Assert.Equal(81, Count(db, "data"));
                Assert.Equal(new[] { "device", "setup", "sweep", "rf", "dc" }, db.Schema.Tables.Keys);
                Assert.Equal("Ada", db.Metadata["Operator"]);
                Assert.NotNull(db.Layout);
                db.AddRows(new[] { Point(99, 1, 25.0, 3.3, -50.0) });
                Assert.Equal(82, Count(db, "data"));
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                Assert.Equal(81, Count(db, "data"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Ball_V2_TableParquetMissingColumn_OnLayoutSession_FallsBackToWide()
        {
            var dir = TempDir();
            try
            {
                var ball = Path.Combine(dir, "sweep.ball");
                using (var src = new DataBall(TablesConfig()))
                {
                    await src.ImportAsync(Fixture("semiconductor-sweep.csv"));
                    await src.SaveAsync(ball);
                }
                using (var zip = ZipFile.Open(ball, ZipArchiveMode.Update))
                {
                    // dc.parquet replaced by device.parquet: has device_key/SN, lacks _row/I_Total.
                    var device = Path.Combine(dir, "device.parquet");
                    zip.GetEntry("tables/device.parquet")!.ExtractToFile(device);
                    zip.GetEntry("tables/dc.parquet")!.Delete();
                    zip.CreateEntryFromFile(device, "tables/dc.parquet");
                }

                using var db = new DataBall(TablesConfig());
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                db.AddRows(new[] { Point(99, 1, 25.0, 3.3, -50.0) });
                await db.ImportAsync(ball);
                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Equal(FixtureTables, BaseTables(db));
                Assert.Equal(81, Count(db, "data"));
                Assert.Equal(81, Count(db, "dc"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Ball_IntroducingBadTables_IntoWideSession_IsAtomic_LeavesSessionWide()
        {
            var dir = TempDir();
            try
            {
                var ball = Path.Combine(dir, "wide.ball");
                using (var src = new DataBall())
                {
                    await src.ImportAsync(Fixture("semiconductor-sweep.csv"));
                    await src.SaveAsync(ball);
                }
                using (var zip = ZipFile.Open(ball, ZipArchiveMode.Update))
                {
                    zip.GetEntry("config.json")!.Delete();
                    using var w = new StreamWriter(zip.CreateEntry("config.json").Open());
                    w.Write("""{ "tables": { "setup": { "kind": "dimension", "columns": ["Teststand", "Temp"], "key": ["Teststand"] } } }""");
                }

                using var db = new DataBall();
                db.AddRows(new[] { new Dictionary<string, object?> { ["Name"] = "keep" } });
                var ex = await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(ball));
                Assert.Contains("do not determine", FullMessage(ex), StringComparison.OrdinalIgnoreCase);

                Assert.Empty(db.Schema.Tables);
                Assert.Null(db.Layout);
                Assert.Equal("BASE TABLE", TableType(db, "data"));
                Assert.Equal("keep", Assert.Single(db.Query("SELECT Name FROM data"))["Name"]);
                db.AddRows(new[] { new Dictionary<string, object?> { ["Name"] = "more" } });
                Assert.Equal(2, Count(db, "data"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task AddRows_WithLayout_MetadataKeyThatIsAWideColumn_IsNotReconciled()
        {
            using var db = new DataBall(TablesConfig());
            await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
            var d0 = new DateTime(2030, 1, 1);
            db.SetMetadata("Date", d0);
            var before = db.Query("SELECT Date FROM data WHERE stimulusGrp = 1 AND sweep = 1")[0]["Date"];

            var row = Point(99, 1, 25.0, 3.3, -50.0);
            row["Date"] = new DateTime(2031, 6, 1);
            db.AddRows(new[] { row });

            Assert.Equal(d0, db.Metadata["Date"]);
            Assert.Equal(before, db.Query("SELECT Date FROM data WHERE stimulusGrp = 1 AND sweep = 1")[0]["Date"]);
            Assert.Equal(new DateTime(2031, 6, 1), db.Query("SELECT Date FROM data WHERE stimulusGrp = 99")[0]["Date"]);
        }

        [Fact]
        public async Task Import_Replace_WithLayout_AllMetadataCsv_MetadataOnly_NoData()
        {
            var dir = TempDir();
            try
            {
                var only = Path.Combine(dir, "only.csv");
                File.WriteAllText(only, "Tester\nT1\nT1\n");
                var config = WriteConfig(dir, """{ "tables": { "g": { "kind": "measurements", "columns": ["Meas"] } } }""");
                using var db = new DataBall(config);
                await db.ImportAsync(only);
                Assert.Equal("T1", db.Metadata["Tester"]);
                Assert.Equal("", TableType(db, "data"));

                using var wide = new DataBall();
                await wide.ImportAsync(only);
                Assert.Equal("T1", wide.Metadata["Tester"]);
                Assert.Equal("", TableType(wide, "data"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportManager_ImportFromBall_And_Archive_Replace_OnLayoutSession_Work()
        {
            var dir = TempDir();
            try
            {
                var ball = Path.Combine(dir, "wide.ball");
                var zipPath = Path.Combine(dir, "wide.zip");
                using (var src = new DataBall())
                {
                    await src.ImportAsync(Fixture("semiconductor-sweep.csv"));
                    await src.SaveAsync(ball);
                    await src.ExportAsync(zipPath, ExportType.Archive);
                }

                using var db = new DataBall(TablesConfig());
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                db.AddRows(new[] { Point(99, 1, 25.0, 3.3, -50.0) });

                Import.ImportManager.ImportFromBall(db, ball, append: false);
                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Equal(81, Count(db, "data"));
                Assert.Equal(FixtureTables, BaseTables(db));

                db.AddRows(new[] { Point(99, 1, 25.0, 3.3, -50.0) });
                Import.ImportManager.ImportFromArchive(db, zipPath, append: false);
                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Equal(81, Count(db, "data"));

                using var empty = new DataBall(TablesConfig());
                Import.ImportManager.ImportFromBall(empty, ball, append: false);
                Assert.Equal("VIEW", TableType(empty, "data"));
                Assert.NotNull(empty.Layout);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Import_Append_WithLayout_CastFailure_SurfacesConversionError_AndRollsBack()
        {
            var dir = TempDir();
            try
            {
                using var db = new DataBall(TablesConfig());
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                var bad = Path.Combine(dir, "bad.csv");
                File.WriteAllText(bad, "stimulusGrp(id),sweep(id),Temp(degC)\n99,1,hot\n");
                var ex = await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(bad, new ImportOptions { Append = true }));
                var message = FullMessage(ex);
                Assert.DoesNotContain("aborted", message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("hot", message, StringComparison.Ordinal);

                Assert.Equal(81, Count(db, "data"));
                Assert.NotNull(db.Layout);
                db.AddRows(new[] { Point(99, 1, 25.0, 3.3, -50.0) });
                Assert.Equal(82, Count(db, "data"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void AddColumn_OnEmptyLayoutSession_ThrowsNotYet()
        {
            using var db = new DataBall(TablesConfig());
            var ex = Assert.Throws<DataBallException>(() => db.AddColumn("Site", new[] { "A" }));
            Assert.Contains("not supported on a multi-table session yet", ex.Message, StringComparison.Ordinal);
            Assert.Equal("", TableType(db, "data"));
        }

        [Fact]
        public async Task Ball_ConfigMetadata_StillWinsOverMetadataJson()
        {
            var dir = TempDir();
            try
            {
                var ball = Path.Combine(dir, "meta.ball");
                using (var zip = ZipFile.Open(ball, ZipArchiveMode.Create))
                {
                    using (var w = new StreamWriter(zip.CreateEntry("metadata.json").Open()))
                        w.Write("""{ "Operator": "lab" }""");
                    using (var w = new StreamWriter(zip.CreateEntry("config.json").Open()))
                        w.Write("""{ "metadata": { "Operator": "jon" } }""");
                }

                using var db = new DataBall();
                await db.ImportAsync(ball);
                Assert.Equal("jon", db.Metadata["Operator"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static Dictionary<string, object?> Point(long grp, long sweep, double temp, double vcc, double evm)
        {
            return new Dictionary<string, object?>
            {
                ["stimulusGrp"] = grp,
                ["sweep"] = sweep,
                ["SN"] = "sqFoo-001",
                ["Teststand"] = "sqBench-001",
                ["Temp"] = temp,
                ["Vcc"] = vcc,
                ["Frequency"] = 2400.0,
                ["Pout"] = 5.0 * sweep,
                ["Pin"] = -1.0,
                ["Gain"] = 20.0,
                ["I_Total"] = 3.5,
                ["EVM"] = evm,
            };
        }

        private static async Task<(DataBall Db, string FirstCsv)> MetaSession(string dir, string extraTop = "")
        {
            var csv = Path.Combine(dir, "first.csv");
            File.WriteAllText(csv, "Tester,Meas\nT1,1\nT1,2\n");
            var config = WriteConfig(dir, $$"""
                { {{extraTop}} "columns": { "Meas": "long" }, "tables": { "g": { "kind": "measurements", "columns": ["Meas"] } } }
                """);
            var db = new DataBall(config);
            await db.ImportAsync(csv);
            Assert.Equal("T1", db.Metadata["Tester"]);
            Assert.Equal(2, Count(db, "data"));
            return (db, csv);
        }

        private static string FixtureTablesJson(string extraTables = "", string extraTop = "")
        {
            return $$"""
                {
                  {{extraTop}}
                  "stimulus": ["Frequency", "Temp", "Vcc"],
                  "parameters": { "stimulusGrp": { "role": "identity" }, "sweep": { "role": "identity" } },
                  "tables": {
                    "device": { "kind": "master",       "columns": ["SN"] },
                    "setup":  { "kind": "dimension",    "columns": ["Teststand", "Temp", "Vcc"] },
                    "sweep":  { "kind": "rows",         "roles":   ["identity", "stimulus"], "columns": ["Date"] },
                    "rf":     { "kind": "measurements", "columns": ["Pout", "Pin", "Gain", "EVM"] },
                    "dc":     { "kind": "measurements", "columns": ["I_Total"] }{{extraTables}}
                  }
                }
                """;
        }

        private static List<string> ViewColumns(DataBall db)
        {
            return db.Query("""
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = 'main' AND table_name = 'data'
                ORDER BY ordinal_position
                """).Select(r => (string)r["column_name"]!).ToList();
        }

        private static void AssertSameRows(DataBall expectedDb, DataBall actualDb, string sql)
        {
            var expected = expectedDb.Query(sql);
            var actual = actualDb.Query(sql);
            Assert.Equal(expected.Count, actual.Count);
            Assert.Equal(expected[0].Keys, actual[0].Keys);
            for (var i = 0; i < expected.Count; i++)
            {
                foreach (var key in expected[i].Keys)
                    Assert.Equal(expected[i][key], actual[i][key]);
            }
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
