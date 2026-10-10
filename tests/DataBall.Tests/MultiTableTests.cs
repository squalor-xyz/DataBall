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
    /// MergeOrAppend route into the tables. db-01: Bounce / Squish / AddColumn / RemoveColumn
    /// work on a layout by materializing it wide, running the wide operation, and re-splitting.
    /// db-02: an append that adds a dimension, adds a dimension column, or demotes metadata into
    /// a dimension re-splits the same way.
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
        public async Task Tables_FileBacked_ReopenUsesStoredConfig()
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

                using (var stored = new DataBall(databasePath: path))
                {
                    Assert.Equal("VIEW", TableType(stored, "data"));
                    Assert.Equal(81, Count(stored, "data"));
                }

                using var again = new DataBall(TablesConfig(), databasePath: path);
                Assert.Equal(81, Count(again, "data"));
            }
            finally
            {
                TryDeleteDir(dir);
            }
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
        public async Task Ball_WithLayout_Native_RoundTrips()
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

                Assert.Equal("DUCK"u8.ToArray(), File.ReadAllBytes(ball)[8..12]);

                // The stored config declares the layout.
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
        public async Task Ball_WithLayout_Filtered_SavesWholeSession()
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

                Assert.Equal("DUCK"u8.ToArray(), File.ReadAllBytes(ball)[8..12]);

                using var imported = new DataBall();
                await imported.ImportAsync(ball);
                Assert.Equal("VIEW", TableType(imported, "data"));
                Assert.Equal(81, Count(imported, "data"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Ball_Wide_IntoLayoutSession_Splits()
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

                Assert.Equal("DUCK"u8.ToArray(), File.ReadAllBytes(ball)[8..12]);

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
        public async Task Ball_StoredTables_OverrideSessionLayout()
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

                // The stored config overlays the session config.
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

        // ---- db-02: dimension growth on append re-splits ----

        [Fact]
        public async Task Append_NewDimension_CreatesTableAndKeysExistingRows()
        {
            var dir = TempDir();
            try
            {
                var config = WriteConfig(dir, FixtureTablesJson(extraTables: """, "env": { "kind": "dimension", "columns": ["Humidity"] }"""));
                using var db = new DataBall(config);
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                Assert.DoesNotContain("env", BaseTables(db));

                db.AddRow(new Dictionary<string, object?> { ["stimulusGrp"] = 99L, ["sweep"] = 1L, ["Humidity"] = 40.0 });

                Assert.Contains("env", BaseTables(db));
                Assert.Equal(2, Count(db, "env"));
                Assert.Equal(0L, db.Query("SELECT COUNT(*) AS c FROM sweep WHERE \"env_key\" IS NULL")[0]["c"]);
                Assert.Equal(new[] { 40.0 }, db.Query("SELECT Humidity FROM env WHERE Humidity IS NOT NULL").Select(r => (double)r["Humidity"]!));
                Assert.Equal(82, Count(db, "data"));
                Assert.Equal(81L, db.Query("SELECT COUNT(*) AS c FROM data WHERE Humidity IS NULL")[0]["c"]);
                Assert.Equal(40.0, Assert.Single(db.Query("SELECT Humidity FROM data WHERE stimulusGrp = 99"))["Humidity"]);
                Assert.Equal(14, ViewColumns(db).Count);
                Assert.Equal("VIEW", TableType(db, "data"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Append_NewColumnInDimension_RematerializesAndSplits()
        {
            var dir = TempDir();
            try
            {
                var config = WriteConfig(dir, FixtureTablesJson().Replace("\"Teststand\", \"Temp\", \"Vcc\"", "\"Teststand\", \"Temp\", \"Vcc\", \"Humidity\"", StringComparison.Ordinal));
                using var db = new DataBall(config);
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                var before = db.Query("SELECT * FROM data ORDER BY stimulusGrp, sweep");
                Assert.Equal(81, before.Count);
                Assert.DoesNotContain("Humidity", TableColumns(db, "setup"));

                db.AddRow(new Dictionary<string, object?> { ["stimulusGrp"] = 99L, ["sweep"] = 1L, ["Humidity"] = 40.0 });

                Assert.Contains("Humidity", TableColumns(db, "setup"));
                Assert.Equal(14, ViewColumns(db).Count);
                Assert.Equal(10, Count(db, "setup"));
                Assert.Equal("VIEW", TableType(db, "data"));

                var after = db.Query("SELECT * FROM data ORDER BY stimulusGrp, sweep");
                Assert.Equal(82, after.Count);
                for (var i = 0; i < before.Count; i++)
                {
                    foreach (var key in before[i].Keys)
                    {
                        Assert.Equal(before[i][key], after[i][key]);
                        Assert.Equal(before[i][key]?.GetType(), after[i][key]?.GetType());
                    }
                    Assert.Null(after[i]["Humidity"]);
                }
                Assert.Equal(40.0, after[81]["Humidity"]);
                Assert.Equal(99L, after[81]["stimulusGrp"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Append_MetadataVaryingIntoDimension_Demotes()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "first.csv");
                File.WriteAllText(csv, "Tester,Meas\nT1,1\nT1,2\n");
                var config = WriteConfig(dir, """
                    { "columns": { "Meas": "long" }, "tables": { "g": { "kind": "measurements", "columns": ["Meas"] }, "who": { "kind": "dimension", "columns": ["Tester"] } } }
                    """);
                using var db = new DataBall(config);
                await db.ImportAsync(csv);
                Assert.Equal("T1", db.Metadata["Tester"]);

                var more = Path.Combine(dir, "more.csv");
                File.WriteAllText(more, "Tester,Meas\nT2,3\n");
                await db.ImportAsync(more, new ImportOptions { Append = true });

                Assert.False(db.Metadata.ContainsKey("Tester"));
                Assert.Empty(db.Query("SELECT 1 AS one FROM meta WHERE \"key\" = 'Tester'"));
                Assert.Contains("Tester", ViewColumns(db));
                Assert.Equal(2, Count(db, "who"));
                Assert.Equal(new[] { "T1", "T2" }, db.Query("SELECT Tester FROM who ORDER BY Tester").Select(r => (string)r["Tester"]!));
                Assert.Equal(new[] { "T1", "T1", "T2" }, db.Query("SELECT Tester FROM data ORDER BY Meas").Select(r => (string)r["Tester"]!));
                Assert.Equal("VIEW", TableType(db, "data"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Append_DimensionGrowth_FileBacked_KeyConflict_RollsBack()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "sites.csv");
                var path = Path.Combine(dir, "session.duckdb");
                File.WriteAllText(csv, "Site,Operator,Meas\nA,ann,1\nB,bob,2\n");
                var config = WriteConfig(dir, """
                    { "columns": { "Meas": "long" }, "tables": { "site": { "kind": "dimension", "columns": ["Site", "Operator", "Shift"], "key": ["Site"] } } }
                    """);
                IReadOnlyList<Dictionary<string, object?>> before;
                void AssertRestored(DataBall session)
                {
                    Assert.Empty(session.Query("SELECT table_name FROM information_schema.tables WHERE table_name = '_unsplit'"));
                    Assert.Equal("VIEW", TableType(session, "data"));
                    Assert.Equal(before, session.Query("SELECT * FROM data ORDER BY Meas"));
                }
                using (var db = new DataBall(config, databasePath: path))
                {
                    await db.ImportAsync(csv);
                    before = db.Query("SELECT * FROM data ORDER BY Meas");
                    var ex = Assert.Throws<DataBallException>(() => db.AddRows(new[]
                    {
                        new Dictionary<string, object?> { ["Site"] = "A", ["Operator"] = "ann", ["Shift"] = "night", ["Meas"] = 3L }
                    }));
                    Assert.Contains("do not determine", FullMessage(ex), StringComparison.OrdinalIgnoreCase);
                    AssertRestored(db);
                }
                using var reopened = new DataBall(databasePath: path);
                AssertRestored(reopened);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Append_DimensionGrowth_KeyConflict_RollsBack()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "sites.csv");
                File.WriteAllText(csv, "Site,Operator,Meas\nA,ann,1\nB,bob,2\n");
                var config = WriteConfig(dir, """
                    { "columns": { "Meas": "long" }, "tables": { "site": { "kind": "dimension", "columns": ["Site", "Operator", "Shift"], "key": ["Site"] } } }
                    """);
                using var db = new DataBall(config);
                await db.ImportAsync(csv);
                Assert.DoesNotContain("Shift", ViewColumns(db));

                var viewColumns = ViewColumns(db);
                var baseTables = BaseTables(db);
                var metadata = db.Metadata.ToDictionary(p => p.Key, p => p.Value);
                var counts = baseTables.Concat(new[] { "data" }).ToDictionary(t => t, t => Count(db, t));
                var layoutShape = LayoutShape(db);
                var tableColumns = baseTables.ToDictionary(t => t, t => string.Join(",", TableColumns(db, t)));
                var dataBefore = db.Query("SELECT * FROM data ORDER BY Meas");

                var ex = Assert.Throws<DataBallException>(() => db.AddRows(new[]
                {
                    new Dictionary<string, object?> { ["Site"] = "A", ["Operator"] = "ann", ["Shift"] = "night", ["Meas"] = 3L },
                }));
                Assert.Contains("do not determine", FullMessage(ex), StringComparison.OrdinalIgnoreCase);

                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Equal(viewColumns, ViewColumns(db));
                Assert.Equal(baseTables, BaseTables(db));
                Assert.Equal(metadata, db.Metadata.ToDictionary(p => p.Key, p => p.Value));
                Assert.Equal(counts, baseTables.Concat(new[] { "data" }).ToDictionary(t => t, t => Count(db, t)));
                Assert.Equal(layoutShape, LayoutShape(db));
                Assert.Equal(tableColumns, baseTables.ToDictionary(t => t, t => string.Join(",", TableColumns(db, t))));
                var dataAfter = db.Query("SELECT * FROM data ORDER BY Meas");
                Assert.Equal(dataBefore.Count, dataAfter.Count);
                for (var i = 0; i < dataBefore.Count; i++)
                    Assert.Equal(dataBefore[i], dataAfter[i]);

                db.AddRows(new[] { new Dictionary<string, object?> { ["Site"] = "C", ["Operator"] = "cy", ["Meas"] = 4L } });
                Assert.Equal(3, Count(db, "data"));
                Assert.Equal(3, Count(db, "site"));
                Assert.Equal("VIEW", TableType(db, "data"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Append_MetadataVaryingIntoMeasurementGroup_Demotes()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "first.csv");
                File.WriteAllText(csv, "Lot,Meas\nL1,1\nL1,2\n");
                var config = WriteConfig(dir, """
                    { "columns": { "Meas": "long" }, "tables": { "g": { "kind": "measurements", "columns": ["Meas", "Lot"] } } }
                    """);
                using var db = new DataBall(config);
                await db.ImportAsync(csv);
                Assert.Equal("L1", db.Metadata["Lot"]);

                var more = Path.Combine(dir, "more.csv");
                File.WriteAllText(more, "Lot,Meas\nL2,3\n");
                await db.ImportAsync(more, new ImportOptions { Append = true });

                Assert.False(db.Metadata.ContainsKey("Lot"));
                Assert.Empty(db.Query("SELECT 1 AS one FROM meta WHERE \"key\" = 'Lot'"));
                Assert.Contains("Lot", TableColumns(db, "g"));
                Assert.Equal(3, Count(db, "g"));
                Assert.Equal(new[] { "L1", "L1", "L2" }, db.Query("SELECT Lot FROM data ORDER BY Meas").Select(r => (string)r["Lot"]!));
                Assert.Equal("VIEW", TableType(db, "data"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Append_MetadataVaryingIntoMeasurementGroup_OnlyColumn_CreatesTable()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "first.csv");
                File.WriteAllText(csv, "Lot,Meas\nL1,1\nL1,2\n");
                var config = WriteConfig(dir, """
                    { "columns": { "Meas": "long" }, "tables": { "g": { "kind": "measurements", "columns": ["Meas"] }, "h": { "kind": "measurements", "columns": ["Lot"] } } }
                    """);
                using var db = new DataBall(config);
                await db.ImportAsync(csv);
                Assert.Equal("L1", db.Metadata["Lot"]);
                Assert.DoesNotContain("h", BaseTables(db));

                var more = Path.Combine(dir, "more.csv");
                File.WriteAllText(more, "Lot,Meas\nL2,3\n");
                await db.ImportAsync(more, new ImportOptions { Append = true });

                Assert.False(db.Metadata.ContainsKey("Lot"));
                Assert.Empty(db.Query("SELECT 1 AS one FROM meta WHERE \"key\" = 'Lot'"));
                Assert.Contains("h", BaseTables(db));
                Assert.Equal(3, Count(db, "h"));
                Assert.Equal(new[] { "L1", "L1", "L2" }, db.Query("SELECT Lot FROM data ORDER BY Meas").Select(r => (string)r["Lot"]!));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        /// <summary>Site dimension (key Site) plus a Tester constant that lands in Metadata; the layout has no Shift yet.</summary>
        private static async Task<(DataBall Db, string Csv)> TesterSiteSession(string dir)
        {
            var csv = Path.Combine(dir, "sites.csv");
            File.WriteAllText(csv, "Tester,Site,Operator,Meas\nT1,A,ann,1\nT1,B,bob,2\n");
            var config = WriteConfig(dir, """
                { "columns": { "Meas": "long" }, "tables": { "site": { "kind": "dimension", "columns": ["Site", "Operator", "Shift"], "key": ["Site"] } } }
                """);
            var db = new DataBall(config);
            await db.ImportAsync(csv);
            Assert.Equal("T1", db.Metadata["Tester"]);
            return (db, csv);
        }

        private static void AssertTesterSessionIntact(DataBall db, List<string> viewColumns, List<string> layoutShape)
        {
            Assert.Equal("T1", db.Metadata["Tester"]);
            Assert.Single(db.Query("SELECT 1 AS one FROM meta WHERE \"key\" = 'Tester'"));
            Assert.Equal("VIEW", TableType(db, "data"));
            Assert.Equal(viewColumns, ViewColumns(db));
            Assert.Equal(layoutShape, LayoutShape(db));
            Assert.Equal(new[] { "rows", "site" }, BaseTables(db));
            Assert.Equal(2, Count(db, "data"));
            Assert.Equal(2, Count(db, "site"));
            Assert.Equal(2, Count(db, "rows"));
        }

        [Fact]
        public async Task Append_DimensionGrowth_RollbackRestoresDemotedMetadata_AddRows()
        {
            var dir = TempDir();
            try
            {
                var (db, _) = await TesterSiteSession(dir);
                using (db)
                {
                    var viewColumns = ViewColumns(db);
                    var layoutShape = LayoutShape(db);

                    var ex = Assert.Throws<DataBallException>(() => db.AddRows(new[]
                    {
                        new Dictionary<string, object?> { ["Tester"] = "T2", ["Site"] = "A", ["Operator"] = "ann", ["Shift"] = "night", ["Meas"] = 3L },
                    }));
                    Assert.Contains("do not determine", FullMessage(ex), StringComparison.OrdinalIgnoreCase);
                    AssertTesterSessionIntact(db, viewColumns, layoutShape);

                    db.AddRows(new[] { new Dictionary<string, object?> { ["Site"] = "C", ["Operator"] = "cy", ["Meas"] = 4L } });
                    Assert.Equal(3, Count(db, "data"));
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Append_DimensionGrowth_RollbackRestoresDemotedMetadata_ImportAppend()
        {
            var dir = TempDir();
            try
            {
                var (db, _) = await TesterSiteSession(dir);
                using (db)
                {
                    var viewColumns = ViewColumns(db);
                    var layoutShape = LayoutShape(db);

                    var more = Path.Combine(dir, "more.csv");
                    File.WriteAllText(more, "Tester,Site,Operator,Shift,Meas\nT2,A,ann,night,3\n");
                    var ex = await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(more, new ImportOptions { Append = true }));
                    Assert.Contains("do not determine", FullMessage(ex), StringComparison.OrdinalIgnoreCase);
                    AssertTesterSessionIntact(db, viewColumns, layoutShape);
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Append_DimensionGrowth_CastFailure_RollsBack()
        {
            var dir = TempDir();
            try
            {
                var config = WriteConfig(dir, FixtureTablesJson(extraTables: """, "env": { "kind": "dimension", "columns": ["Humidity"] }"""));
                using var db = new DataBall(config);
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                var viewColumns = ViewColumns(db);
                var baseTables = BaseTables(db);
                var layoutShape = LayoutShape(db);
                var before = db.Query("SELECT * FROM data ORDER BY stimulusGrp, sweep");

                // A wide session whose EVM is text: MergeOrAppend stages parquet without expected types,
                // so the failing CAST to DOUBLE happens inside the re-split, not before PlanAppend.
                using var other = new DataBall();
                other.AddRows(new[]
                {
                    new Dictionary<string, object?> { ["stimulusGrp"] = 99L, ["sweep"] = 1L, ["EVM"] = "not-a-number", ["Humidity"] = 40.0 },
                });
                Assert.Equal("VARCHAR", other.Query("SELECT typeof(EVM) AS t FROM data")[0]["t"]);
                var ex = Assert.Throws<DataBallException>(() => db.MergeOrAppend(other, append: true));
                Assert.Contains("not-a-number", FullMessage(ex), StringComparison.Ordinal);

                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Equal(viewColumns, ViewColumns(db));
                Assert.Equal(baseTables, BaseTables(db));
                Assert.Equal(layoutShape, LayoutShape(db));
                var after = db.Query("SELECT * FROM data ORDER BY stimulusGrp, sweep");
                Assert.Equal(before.Count, after.Count);
                for (var i = 0; i < before.Count; i++)
                    Assert.Equal(before[i], after[i]);

                db.AddRow(new Dictionary<string, object?> { ["stimulusGrp"] = 99L, ["sweep"] = 1L, ["Humidity"] = 40.0 });
                Assert.Equal(82, Count(db, "data"));
                Assert.Equal(2, Count(db, "env"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Append_NewDimension_SaveAsyncAndReopen_RoundTrips()
        {
            var dir = TempDir();
            try
            {
                var config = WriteConfig(dir, FixtureTablesJson(extraTables: """, "env": { "kind": "dimension", "columns": ["Humidity"] }"""));
                var ball = Path.Combine(dir, "grown.ball");
                using (var db = new DataBall(config))
                {
                    await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                    db.AddRow(new Dictionary<string, object?> { ["stimulusGrp"] = 99L, ["sweep"] = 1L, ["Humidity"] = 40.0 });
                    await db.SaveAsync(ball);
                }

                using var reopened = DataBall.Open(ball, config);
                Assert.Equal("VIEW", TableType(reopened, "data"));
                Assert.Contains("env", BaseTables(reopened));
                Assert.Equal(2, Count(reopened, "env"));
                Assert.Equal(82, Count(reopened, "data"));
                Assert.Equal(81L, reopened.Query("SELECT COUNT(*) AS c FROM data WHERE Humidity IS NULL")[0]["c"]);
                Assert.Equal(40.0, Assert.Single(reopened.Query("SELECT Humidity FROM data WHERE stimulusGrp = 99"))["Humidity"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Append_NewDimension_FileBacked_ReopenBindsNewTables()
        {
            var dir = TempDir();
            var path = Path.Combine(dir, "session.duckdb");
            try
            {
                var config = WriteConfig(dir, FixtureTablesJson(extraTables: """, "env": { "kind": "dimension", "columns": ["Humidity"] }"""));
                using (var db = new DataBall(config, databasePath: path))
                {
                    await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                    db.AddRow(new Dictionary<string, object?> { ["stimulusGrp"] = 99L, ["sweep"] = 1L, ["Humidity"] = 40.0 });
                }

                using var reopened = new DataBall(config, databasePath: path);
                Assert.NotNull(reopened.Layout);
                Assert.Equal(2, Count(reopened, "env"));
                Assert.Equal(82, Count(reopened, "data"));
                reopened.AddRow(new Dictionary<string, object?> { ["stimulusGrp"] = 99L, ["sweep"] = 2L, ["Humidity"] = 40.0 });
                Assert.Equal(83, Count(reopened, "data"));
                Assert.Equal(2, Count(reopened, "env"));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task Append_DimensionGrowth_KeepsRowKeyOrder()
        {
            var dir = TempDir();
            try
            {
                var config = WriteConfig(dir, FixtureTablesJson(extraTables: """, "env": { "kind": "dimension", "columns": ["Humidity"] }"""));
                using var db = new DataBall(config);
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                const string order = "SELECT stimulusGrp, sweep, \"Date\" FROM sweep ORDER BY \"_row\"";
                var before = db.Query(order);

                db.AddRows(new[]
                {
                    new Dictionary<string, object?> { ["stimulusGrp"] = 99L, ["sweep"] = 1L, ["Humidity"] = 40.0 },
                    new Dictionary<string, object?> { ["stimulusGrp"] = 99L, ["sweep"] = 2L, ["Humidity"] = 41.0 },
                });

                var after = db.Query(order);
                Assert.Equal(before.Count + 2, after.Count);
                for (var i = 0; i < before.Count; i++)
                    Assert.Equal(before[i], after[i]);
                Assert.Equal(99L, after[81]["stimulusGrp"]);
                Assert.Equal(1L, after[81]["sweep"]);
                Assert.Equal(2L, after[82]["sweep"]);
                Assert.Equal(Enumerable.Range(1, 83).Select(i => (long)i), db.Query("SELECT \"_row\" FROM sweep ORDER BY \"_row\"").Select(r => (long)r["_row"]!));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task CommitRow_NewDimensionColumn_Rematerializes()
        {
            var dir = TempDir();
            try
            {
                var config = WriteConfig(dir, FixtureTablesJson().Replace("\"Teststand\", \"Temp\", \"Vcc\"", "\"Teststand\", \"Temp\", \"Vcc\", \"Humidity\"", StringComparison.Ordinal));
                using var db = new DataBall(config);
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));

                db.InitializeRow(new Dictionary<string, object?> { ["stimulusGrp"] = 99L, ["sweep"] = 1L, ["Humidity"] = 40.0 });
                db.CommitRow();

                Assert.Equal(82, Count(db, "data"));
                Assert.Contains("Humidity", TableColumns(db, "setup"));
                Assert.Equal(14, ViewColumns(db).Count);
                Assert.Equal(81L, db.Query("SELECT COUNT(*) AS c FROM data WHERE Humidity IS NULL")[0]["c"]);
                Assert.Equal(40.0, Assert.Single(db.Query("SELECT Humidity FROM data WHERE stimulusGrp = 99"))["Humidity"]);
                Assert.Equal("VIEW", TableType(db, "data"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task CommitRow_NewDimensionColumn_DeclaredKeyConflict_Throws()
        {
            var dir = TempDir();
            try
            {
                using var db = new DataBall(KeyedSetupConfig(dir, "Humidity"));
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                var viewColumns = ViewColumns(db);
                var layoutShape = LayoutShape(db);

                // The copied last row reuses an existing (Teststand, Temp, Vcc) key whose Humidity is NULL.
                db.InitializeRow(new Dictionary<string, object?> { ["stimulusGrp"] = 99L, ["sweep"] = 1L, ["Humidity"] = 40.0 });
                var ex = Assert.Throws<DataBallException>(() => db.CommitRow());
                Assert.Contains("do not determine", FullMessage(ex), StringComparison.OrdinalIgnoreCase);

                Assert.Equal(81, Count(db, "data"));
                Assert.Equal(viewColumns, ViewColumns(db));
                Assert.Equal(layoutShape, LayoutShape(db));
                Assert.False(db.ExpectedColumnTypes.ContainsKey("Humidity"));

                // The pending row survived: a new key makes the same commit succeed.
                db.ModifyField("Temp", 125.0);
                db.CommitRow();
                Assert.Equal(82, Count(db, "data"));
                Assert.Equal(40.0, Assert.Single(db.Query("SELECT Humidity FROM data WHERE stimulusGrp = 99"))["Humidity"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task MergeOrAppend_NewDimension_Rematerializes()
        {
            var dir = TempDir();
            try
            {
                var config = WriteConfig(dir, FixtureTablesJson(extraTables: """, "env": { "kind": "dimension", "columns": ["Humidity"] }"""));
                using var other = new DataBall();
                await other.ImportAsync(Fixture("semiconductor-sweep.csv"));
                other.AddColumn("Humidity", Enumerable.Repeat(40.0, 81).ToList());
                using var db = new DataBall(config);
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));

                db.MergeOrAppend(other, append: true);

                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Contains("env", BaseTables(db));
                Assert.Equal(2, Count(db, "env"));
                Assert.Equal(162, Count(db, "data"));
                Assert.Equal(81L, db.Query("SELECT COUNT(*) AS c FROM data WHERE Humidity IS NULL")[0]["c"]);
                Assert.Equal(81L, db.Query("SELECT COUNT(*) AS c FROM data WHERE Humidity = 40.0")[0]["c"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Append_MetadataPolicyFirst_DimensionKey_StaysMetadata()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "first.csv");
                File.WriteAllText(csv, "Tester,Meas\nT1,1\nT1,2\n");
                var config = WriteConfig(dir, """
                    { "metadataPolicy": "first", "columns": { "Meas": "long" }, "tables": { "g": { "kind": "measurements", "columns": ["Meas"] }, "who": { "kind": "dimension", "columns": ["Tester"] } } }
                    """);
                using var db = new DataBall(config);
                await db.ImportAsync(csv);

                var more = Path.Combine(dir, "more.csv");
                File.WriteAllText(more, "Tester,Meas\nT2,3\n");
                await db.ImportAsync(more, new ImportOptions { Append = true });

                Assert.Equal("T1", db.Metadata["Tester"]);
                Assert.DoesNotContain("Tester", ViewColumns(db));
                Assert.DoesNotContain("who", BaseTables(db));
                Assert.Equal(3, Count(db, "data"));
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
        public async Task Ball_Native_AfterAppend_NewColumn_RoundTrips()
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

                Assert.Equal("DUCK"u8.ToArray(), File.ReadAllBytes(ball)[8..12]);

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
                using (var store = new DuckDbStore(ball))
                {
                    store.Execute("DROP TABLE meta");
                    store.Execute("CREATE TABLE meta (wrong VARCHAR)");
                }

                using var db = new DataBall(TablesConfig());
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                db.SetMetadata("Operator", "Ada");
                var versions = Count(db, "_databall");
                await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(ball));
                Assert.Equal(versions, Count(db, "_databall"));
                Assert.Empty(db.Query("SELECT database_name FROM duckdb_databases() WHERE starts_with(database_name, 'import_')"));

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
                using (var store = new DuckDbStore(ball))
                {
                    var config = store.ReadConfig();
                    config.Tables["setup"] = new TableSpec { Kind = "dimension", Columns = new() { "Teststand", "Temp" }, Key = new() { "Teststand" } };
                    store.WriteConfig(config);
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
        public async Task Ball_ConfigMetadata_WinsOverStoredMetadata()
        {
            var dir = TempDir();
            try
            {
                var ball = Path.Combine(dir, "meta.ball");
                var cfg = WriteConfig(dir, """{"metadata":{"Operator":"Ada"}}""");
                using (var source = new DataBall(cfg, databasePath: ball))
                    source.SetMetadata("Operator", "metadata");

                using var db = new DataBall();
                await db.ImportAsync(ball);
                Assert.Equal("Ada", db.Metadata["Operator"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // ---- db-01: Bounce / Squish / AddColumn / RemoveColumn on a layout ----

        [Fact]
        public async Task Bounce_WithLayout_ExtractsTeststand_DropsFromSetup_Distinct()
        {
            var dir = TempDir();
            try
            {
                using var wide = new DataBall(WideTwinConfig(dir));
                await wide.ImportAsync(Fixture("semiconductor-sweep.csv"));
                await wide.Bounce();

                using var db = new DataBall(TablesConfig());
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                await db.Bounce();

                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Equal(wide.Metadata["Teststand"], db.Metadata["Teststand"]);
                Assert.Equal(wide.Metadata["SN"], db.Metadata["SN"]);
                // SN was the master's only column, so the master is gone; setup keeps Temp and Vcc.
                Assert.Equal(new[] { "dc", "rf", "setup", "sweep" }, BaseTables(db));
                Assert.Equal(new[] { "setup_key", "Temp", "Vcc" }, TableColumns(db, "setup"));
                AssertSameRows(wide, db, "SELECT * FROM \"data\" ORDER BY ALL");
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task Bounce_WithLayout_AllConstant_DropsViewAndTables()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "const.csv");
                File.WriteAllText(csv, "Site,M\nA,1\nA,1\n");
                var config = WriteConfig(dir, """{ "tables": { "site": { "kind": "dimension", "columns": ["Site"] } } }""");
                using var db = new DataBall(config);
                await db.ImportAsync(csv);
                Assert.Equal("VIEW", TableType(db, "data"));

                await db.Bounce();

                Assert.Equal("", TableType(db, "data"));
                Assert.Empty(BaseTables(db));
                Assert.Equal("A", db.Metadata["Site"]);
                Assert.Equal(1L, Convert.ToInt64(db.Metadata["M"]));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task Squish_WithLayout_HivePartitionByTemp_Roundtrips()
        {
            var dir = TempDir();
            try
            {
                var hive = Path.Combine(dir, "hive");
                using var db = new DataBall(TablesConfig());
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                await db.Squish(hive, new[] { "Temp" });

                Assert.Equal("VIEW", TableType(db, "data"));
                var partitions = Directory.GetDirectories(hive).Select(Path.GetFileName).ToList();
                Assert.Equal(3, partitions.Count);
                Assert.All(partitions, p => Assert.StartsWith("Temp=", p, StringComparison.Ordinal));

                using var back = new DataBall();
                await back.ImportAsync(hive);
                var cols = string.Join(", ", ViewColumns(db).OrderBy(c => c, StringComparer.Ordinal).Select(c => $"\"{c}\""));
                AssertSameRows(db, back, $"SELECT {cols} FROM \"data\" ORDER BY ALL");
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task AddColumn_WithLayout_AlignsByRow()
        {
            var dir = TempDir();
            try
            {
                var idx = Enumerable.Range(0, 81).ToList();
                using var wide = new DataBall(WideTwinConfig(dir));
                await wide.ImportAsync(Fixture("semiconductor-sweep.csv"));
                wide.AddColumn<int>("Idx", idx);

                using var db = new DataBall(TablesConfig());
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                db.AddColumn<int>("Idx", idx);

                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Contains("Idx", TableColumns(db, "sweep"));
                // Rows are unique on (stimulusGrp, sweep), so an equal multiset means Idx landed on the same rows.
                AssertSameRows(wide, db, "SELECT * FROM \"data\" ORDER BY ALL");
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task AddColumn_ToDimension_RematerializesAndSplits()
        {
            var dir = TempDir();
            try
            {
                var config = WriteConfig(dir, """
                    {
                      "stimulus": ["Frequency", "Temp", "Vcc"],
                      "parameters": { "stimulusGrp": { "role": "identity" }, "sweep": { "role": "identity" } },
                      "tables": {
                        "device": { "kind": "master",       "columns": ["SN"] },
                        "setup":  { "kind": "dimension",    "columns": ["Teststand", "Temp", "Vcc", "Chamber"] },
                        "sweep":  { "kind": "rows",         "roles":   ["identity", "stimulus"], "columns": ["Date"] },
                        "rf":     { "kind": "measurements", "columns": ["Pout", "Pin", "Gain", "EVM"] },
                        "dc":     { "kind": "measurements", "columns": ["I_Total"] }
                      }
                    }
                    """);
                using var db = new DataBall(config);
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                var setupRows = Count(db, "setup");

                // Chamber is the CSV's own Temp text, row for row, so it is determined by the setup key.
                db.AddColumn("Chamber", CsvColumn(Fixture("semiconductor-sweep.csv"), "Temp(degC)"));

                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Contains("Chamber", TableColumns(db, "setup"));
                Assert.Equal(setupRows, Count(db, "setup"));
                Assert.Equal(81, Count(db, "data"));
                var mismatched = db.Query("SELECT COUNT(*) AS c FROM \"data\" WHERE CAST(\"Chamber\" AS DOUBLE) <> \"Temp\"");
                Assert.Equal(0L, Convert.ToInt64(mismatched[0]["c"]));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public void AddColumn_LayoutDeclared_NoData_SplitsOnFirstColumn()
        {
            var dir = TempDir();
            try
            {
                var config = WriteConfig(dir, """{ "tables": { "site": { "kind": "dimension", "columns": ["Site"] } } }""");
                using var db = new DataBall(config);
                db.AddColumn("Site", new[] { "A", "B", "A" });

                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Equal(2, Count(db, "site"));
                Assert.Equal(3, Count(db, "data"));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task RemoveColumn_WithLayout_DropsFromOwningTable_ViewRefreshed()
        {
            using var db = new DataBall(TablesConfig());
            await db.ImportAsync(Fixture("semiconductor-sweep.csv"));

            db.RemoveColumn("EVM");

            Assert.Equal("VIEW", TableType(db, "data"));
            Assert.Equal(FixtureTables, BaseTables(db));
            Assert.DoesNotContain("EVM", TableColumns(db, "rf"));
            Assert.DoesNotContain("EVM", ViewColumns(db));
            Assert.Equal(81, Count(db, "data"));
        }

        [Fact]
        public async Task RemoveColumn_WithLayout_LastColumn_DropsViewAndTables()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "two.csv");
                File.WriteAllText(csv, "Site,M\nA,1\nB,2\n");
                var config = WriteConfig(dir, """{ "tables": { "site": { "kind": "dimension", "columns": ["Site"] }, "g": { "kind": "measurements", "columns": ["M"] } } }""");
                using var db = new DataBall(config);
                await db.ImportAsync(csv);

                db.RemoveColumn("M");
                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Equal(new[] { "rows", "site" }, BaseTables(db));

                db.RemoveColumn("Site");
                Assert.Equal("", TableType(db, "data"));
                Assert.Empty(BaseTables(db));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task RemoveColumn_WithLayout_UnknownColumn_Throws_SessionIntact()
        {
            using var db = new DataBall(TablesConfig());
            await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
            var columns = ViewColumns(db);

            var ex = Assert.Throws<DataBallException>(() => db.RemoveColumn("Nope"));
            Assert.Contains("does not exist", FullMessage(ex), StringComparison.Ordinal);

            Assert.Equal("VIEW", TableType(db, "data"));
            Assert.Equal(FixtureTables, BaseTables(db));
            Assert.Equal(columns, ViewColumns(db));
            Assert.Equal(81, Count(db, "data"));
            // The in-memory layout is still bound: a row write routes into the tables.
            db.AddRows(new[] { Point(99, 1, 25.0, 3.3, -50.0) });
            Assert.Equal(82, Count(db, "data"));
        }

        [Fact]
        public async Task Bounce_WithLayout_ConstantDeclaredKeyColumn_StaysData()
        {
            var dir = TempDir();
            try
            {
                using var db = new DataBall(KeyedSetupConfig(dir));
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));

                await db.Bounce();

                // Teststand is constant but part of setup's declared key, so it stays a column.
                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.False(db.Metadata.ContainsKey("Teststand"));
                Assert.Contains("Teststand", TableColumns(db, "setup"));
                Assert.Equal(81, Count(db, "data"));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task RemoveColumn_WithLayout_DeclaredKeyColumn_ThrowsClearly_SessionIntact()
        {
            var dir = TempDir();
            try
            {
                using var db = new DataBall(KeyedSetupConfig(dir));
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                var columns = ViewColumns(db);

                var ex = Assert.Throws<DataBallException>(() => db.RemoveColumn("Temp"));
                Assert.Contains("declared key of table 'setup'", FullMessage(ex), StringComparison.Ordinal);

                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Equal(columns, ViewColumns(db));
                Assert.Equal(81, Count(db, "data"));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task AddColumn_ToDimension_KeyConflict_Throws_SessionIntact()
        {
            var dir = TempDir();
            try
            {
                using var db = new DataBall(KeyedSetupConfig(dir, extraSetupColumn: "Chamber"));
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                var columns = ViewColumns(db);
                var setupRows = Count(db, "setup");

                // One Chamber value per row: the setup key no longer determines its columns.
                var ex = Assert.Throws<DataBallException>(() => db.AddColumn("Chamber", Enumerable.Range(0, 81).Select(i => (string?)("C" + i))));
                Assert.Contains("do not determine", FullMessage(ex), StringComparison.Ordinal);

                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Equal(columns, ViewColumns(db));
                Assert.Equal(setupRows, Count(db, "setup"));
                Assert.Equal(81, Count(db, "data"));
                db.AddRows(new[] { Point(99, 1, 25.0, 3.3, -50.0) });
                Assert.Equal(82, Count(db, "data"));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task ApplyImportedConfig_TablesOntoWideData_AdoptsLayout()
        {
            var dir = TempDir();
            try
            {
                var ball = Path.Combine(dir, "layout.ball");
                using (var src = new DataBall(TablesConfig()))
                {
                    await src.ImportAsync(Fixture("semiconductor-sweep.csv"));
                    await src.SaveAsync(ball);
                }

                using var db = new DataBall(WideTwinConfig(dir));
                await db.ImportAsync(Fixture("semiconductor-sweep.csv"));
                Assert.Equal("BASE TABLE", TableType(db, "data"));

                await db.ImportAsync(ball, new ImportOptions { Append = true });

                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Equal(FixtureTables, BaseTables(db));
                Assert.Equal(162, Count(db, "data"));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        /// <summary>The fixture config without its <c>tables</c> section: same types and roles, one wide table.</summary>
        private static string WideTwinConfig(string dir)
        {
            return WriteConfig(dir, """
                {
                  "stimulus": ["Frequency", "Temp", "Vcc"],
                  "parameters": { "stimulusGrp": { "role": "identity" }, "sweep": { "role": "identity" } }
                }
                """);
        }

        /// <summary>The fixture layout with setup's key declared explicitly as (Teststand, Temp, Vcc).</summary>
        private static string KeyedSetupConfig(string dir, string? extraSetupColumn = null)
        {
            var extra = extraSetupColumn is null ? "" : $", \"{extraSetupColumn}\"";
            return WriteConfig(dir, $$"""
                {
                  "stimulus": ["Frequency", "Temp", "Vcc"],
                  "parameters": { "stimulusGrp": { "role": "identity" }, "sweep": { "role": "identity" } },
                  "tables": {
                    "device": { "kind": "master",       "columns": ["SN"] },
                    "setup":  { "kind": "dimension",    "columns": ["Teststand", "Temp", "Vcc"{{extra}}], "key": ["Teststand", "Temp", "Vcc"] },
                    "sweep":  { "kind": "rows",         "roles":   ["identity", "stimulus"], "columns": ["Date"] },
                    "rf":     { "kind": "measurements", "columns": ["Pout", "Pin", "Gain", "EVM"] },
                    "dc":     { "kind": "measurements", "columns": ["I_Total"] }
                  }
                }
                """);
        }

        /// <summary>Bound layout as text: each physical table with its columns.</summary>
        private static List<string> LayoutShape(DataBall db)
        {
            Assert.NotNull(db.Layout);
            return db.Layout!.PhysicalTables.Select(t => t.Name + ":" + string.Join(",", t.Columns)).ToList();
        }

        private static List<string> TableColumns(DataBall db, string table)
        {
            return db.Query($"""
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = 'main' AND table_name = '{table}'
                ORDER BY ordinal_position
                """).Select(r => (string)r["column_name"]!).ToList();
        }

        /// <summary>Raw text of one CSV column in file order (the fixture has no quoted fields).</summary>
        private static List<string?> CsvColumn(string path, string header)
        {
            var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
            var index = Array.IndexOf(lines[0].Split(','), header);
            Assert.True(index >= 0, "missing CSV column " + header);
            return lines.Skip(1).Select(l => (string?)l.Split(',')[index]).ToList();
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
                WHERE table_schema = 'main' AND table_type = 'BASE TABLE' AND table_name NOT IN ('meta', '_databall')
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
