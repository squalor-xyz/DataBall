// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace squalor.DataBall.Tests
{
    /// <summary>
    /// db-23: an append merges schemas by name. Integer widths widen to the wider integer, an integer
    /// meeting FLOAT or DOUBLE widens to DOUBLE (the owning physical column is altered in place), a
    /// dimension key column widened to DOUBLE re-splits, and any other type conflict throws before
    /// the session changes. Each case runs on a wide session and on a config-declared layout.
    /// </summary>
    public class SchemaMergeTests
    {
        private const string MeasuredV = """{ "tables": { "g": { "kind": "measurements", "columns": ["v"] } } }""";
        private const string MeasuredNS = """{ "tables": { "g": { "kind": "measurements", "columns": ["n", "s"] } } }""";
        private const string MeasuredA = """{ "tables": { "g": { "kind": "measurements", "columns": ["a"] } } }""";

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Append_UnionByName_NullForMissing(bool layout)
        {
            var dir = TempDir();
            try
            {
                var first = WriteFile(dir, "first.csv", "id,a\n1,x\n");
                var second = WriteFile(dir, "second.csv", "ID,b\n2,y\n");
                using var db = layout ? new DataBall(WriteConfig(dir, MeasuredA)) : new DataBall();
                await db.ImportAsync(first);
                await db.ImportAsync(second, new ImportOptions { Append = true });

                var rows = db.Query("SELECT id, a, b FROM data ORDER BY id");
                Assert.Equal(2, rows.Count);
                Assert.Equal(1L, rows[0]["id"]);
                Assert.Equal("x", rows[0]["a"]);
                Assert.Null(rows[0]["b"]);
                Assert.Equal(2L, rows[1]["id"]);
                Assert.Null(rows[1]["a"]);
                Assert.Equal("y", rows[1]["b"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Append_Widens_IntegerToBigint(bool layout)
        {
            var dir = TempDir();
            try
            {
                var more = WriteFile(dir, "more.csv", "v\n3000000000\n");
                using var db = layout ? new DataBall(WriteConfig(dir, MeasuredV)) : new DataBall();
                // AddRows types the column from the value without registering an expected type; AddColumn<int>
                // would register one, and imports cast to expected types before the merge.
                db.AddRows(new[] { new Dictionary<string, object?> { ["v"] = 1 } });

                await db.ImportAsync(more, new ImportOptions { Append = true });

                Assert.Equal("BIGINT", ColumnType(db, layout ? "g" : "data", "v"));
                if (layout)
                    Assert.Equal("BIGINT", ColumnType(db, "data", "v"));
                var rows = db.Query("SELECT v FROM data ORDER BY v");
                Assert.Equal(2, rows.Count);
                Assert.Equal(1L, rows[0]["v"]);
                Assert.Equal(3000000000L, rows[1]["v"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Append_Widens_IntegerToDouble(bool layout)
        {
            var dir = TempDir();
            try
            {
                var more = WriteFile(dir, "more.csv", "v\n2.5\n");
                using var db = layout ? new DataBall(WriteConfig(dir, MeasuredV)) : new DataBall();
                // AddRows types the column from the value without registering an expected type; AddColumn<int>
                // would register one, and imports cast to expected types before the merge.
                db.AddRows(new[] { new Dictionary<string, object?> { ["v"] = 1 } });

                await db.ImportAsync(more, new ImportOptions { Append = true });

                Assert.Equal("DOUBLE", ColumnType(db, layout ? "g" : "data", "v"));
                if (layout)
                    Assert.Equal("DOUBLE", ColumnType(db, "data", "v"));
                var rows = db.Query("SELECT v FROM data ORDER BY v");
                Assert.Equal(2, rows.Count);
                Assert.Equal(1.0, Assert.IsType<double>(rows[0]["v"]));
                Assert.Equal(2.5, Assert.IsType<double>(rows[1]["v"]));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task Append_Layout_DimensionKeyToDouble_Rekeys()
        {
            var dir = TempDir();
            try
            {
                var more = WriteFile(dir, "more.csv", "Temp,Meas\n25.0,3.0\n");
                var config = WriteConfig(dir, """{ "tables": { "setup": { "kind": "dimension", "columns": ["Temp"] }, "g": { "kind": "measurements", "columns": ["Meas"] } } }""");
                using var db = new DataBall(config);
                db.AddRows(new[]
                {
                    new Dictionary<string, object?> { ["Temp"] = 25, ["Meas"] = 1.0 },
                    new Dictionary<string, object?> { ["Temp"] = 25, ["Meas"] = 2.0 },
                });
                Assert.Equal(1, Count(db, "setup"));

                await db.ImportAsync(more, new ImportOptions { Append = true });

                // hash(25) and hash(25.0) differ, so an in-place widen would add a second setup row.
                Assert.Equal("DOUBLE", ColumnType(db, "setup", "Temp"));
                Assert.Equal(1, Count(db, "setup"));
                Assert.Equal(3, Count(db, "data"));
                var rows = db.Query("SELECT Temp, Meas FROM data ORDER BY Meas");
                Assert.Equal(3, rows.Count);
                foreach (var row in rows)
                    Assert.Equal(25.0, Assert.IsType<double>(row["Temp"]));
                Assert.Equal(3.0, Assert.IsType<double>(rows[2]["Meas"]));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Append_VarcharVsDouble_Throws(bool layout)
        {
            var dir = TempDir();
            try
            {
                // n would widen to DOUBLE and s conflicts; the conflict must be found before any ALTER runs.
                var more = WriteFile(dir, "more.csv", "n,s\n2.5,1.5\n");
                using var db = layout ? new DataBall(WriteConfig(dir, MeasuredNS)) : new DataBall();
                db.AddRows(new[] { new Dictionary<string, object?> { ["n"] = 1, ["s"] = "abc" } });

                var ex = await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(more, new ImportOptions { Append = true }));
                var message = FullMessage(ex);
                Assert.Contains("'s'", message);
                Assert.Contains("VARCHAR", message);
                Assert.Contains("DOUBLE", message);
                Assert.Contains(more, message);
                Assert.DoesNotContain("'n'", message);

                var table = layout ? "g" : "data";
                Assert.Equal("INTEGER", ColumnType(db, table, "n"));
                Assert.Equal("VARCHAR", ColumnType(db, table, "s"));
                Assert.Equal(1, Count(db, "data"));
                var rows = db.Query("SELECT n, s FROM data");
                Assert.Equal(1, rows[0]["n"]);
                Assert.Equal("abc", rows[0]["s"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Append_ConfigTypedColumn_StillCasts(bool layout)
        {
            var dir = TempDir();
            try
            {
                var first = WriteFile(dir, "first.csv", "v\n1\n");
                var more = WriteFile(dir, "more.csv", "v\n2.4\n");
                var config = layout
                    ? WriteConfig(dir, """{ "columns": { "v": "long" }, "tables": { "g": { "kind": "measurements", "columns": ["v"] } } }""")
                    : WriteConfig(dir, """{ "columns": { "v": "long" } }""");
                using var db = new DataBall(config);
                await db.ImportAsync(first);
                await db.ImportAsync(more, new ImportOptions { Append = true });

                // The configured type wins: 2.4 casts to BIGINT (2) and the column is not widened to DOUBLE.
                Assert.Equal("BIGINT", ColumnType(db, layout ? "g" : "data", "v"));
                var rows = db.Query("SELECT v FROM data ORDER BY v");
                Assert.Equal(2, rows.Count);
                Assert.Equal(1L, rows[0]["v"]);
                Assert.Equal(2L, rows[1]["v"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Append_SourceKey_NotWidened(bool layout)
        {
            var dir = TempDir();
            try
            {
                var first = WriteFile(dir, "first.csv", "v\n1\n");
                var more = WriteFile(dir, "more.csv", "v\n3.5\n");
                using var db = layout ? new DataBall(WriteConfig(dir, MeasuredV)) : new DataBall();
                await db.ImportAsync(first);
                await db.ImportAsync(more, new ImportOptions { Append = true });

                // v widens in this append; _source_key is lineage and keeps its BIGINT type and its keys.
                Assert.Equal("BIGINT", ColumnType(db, "data", "_source_key"));
                var rows = db.Query("SELECT v, _source_key FROM data ORDER BY v");
                Assert.Equal(2, rows.Count);
                Assert.Equal(1L, rows[0]["_source_key"]);
                Assert.Equal(2L, rows[1]["_source_key"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public void MergeOrAppend_ConfigTypedColumn_StillCasts()
        {
            // MergeOrAppend stages the other session's parquet without expected types; the configured
            // BIGINT must keep casting (2.4 -> 2), not widen to DOUBLE.
            var dir = TempDir();
            try
            {
                var config = WriteConfig(dir, """{ "columns": { "v": "long" } }""");
                using var db = new DataBall(config);
                db.AddRows(new[] { new Dictionary<string, object?> { ["v"] = 1L } });
                using var other = new DataBall();
                other.AddRows(new[] { new Dictionary<string, object?> { ["v"] = 2.4 } });
                db.MergeOrAppend(other, append: true);

                Assert.Equal("BIGINT", ColumnType(db, "data", "v"));
                var rows = db.Query("SELECT v FROM data ORDER BY v");
                Assert.Equal(2, rows.Count);
                Assert.Equal(1L, rows[0]["v"]);
                Assert.Equal(2L, rows[1]["v"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Append_FloatThenDouble_WidensToDouble(bool layout)
        {
            var dir = TempDir();
            try
            {
                var more = WriteFile(dir, "more.csv", "v\n2.5\n");
                using var db = layout ? new DataBall(WriteConfig(dir, MeasuredV)) : new DataBall();
                db.AddRows(new[] { new Dictionary<string, object?> { ["v"] = 1.5f } });
                Assert.Equal("FLOAT", ColumnType(db, layout ? "g" : "data", "v"));

                await db.ImportAsync(more, new ImportOptions { Append = true });

                Assert.Equal("DOUBLE", ColumnType(db, layout ? "g" : "data", "v"));
                if (layout)
                    Assert.Equal("DOUBLE", ColumnType(db, "data", "v"));
                var rows = db.Query("SELECT v FROM data ORDER BY v");
                Assert.Equal(2, rows.Count);
                Assert.Equal(1.5, Assert.IsType<double>(rows[0]["v"]));
                Assert.Equal(2.5, Assert.IsType<double>(rows[1]["v"]));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Append_DateThenTimestamp_WidensToTimestamp(bool layout)
        {
            var dir = TempDir();
            try
            {
                // Imports promote DATE to TIMESTAMP, so a DATE column is set directly in the session first.
                var more = WriteFile(dir, "more.csv", "v\n2026-01-03\n");
                using var db = layout ? new DataBall(WriteConfig(dir, MeasuredV)) : new DataBall();
                db.AddRows(new[] { new Dictionary<string, object?> { ["v"] = new DateTime(2026, 1, 2) } });
                var owner = layout ? "g" : "data";
                db.Query($"ALTER TABLE \"{owner}\" ALTER COLUMN \"v\" SET DATA TYPE DATE");
                Assert.Equal("DATE", ColumnType(db, owner, "v"));

                await db.ImportAsync(more, new ImportOptions { Append = true });

                Assert.Equal("TIMESTAMP", ColumnType(db, owner, "v"));
                if (layout)
                    Assert.Equal("TIMESTAMP", ColumnType(db, "data", "v"));
                var rows = db.Query("SELECT v FROM data ORDER BY v");
                Assert.Equal(2, rows.Count);
                Assert.Equal(new DateTime(2026, 1, 2), Assert.IsType<DateTime>(rows[0]["v"]));
                Assert.Equal(new DateTime(2026, 1, 3), Assert.IsType<DateTime>(rows[1]["v"]));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task Append_Layout_DimensionKeyDateToTimestamp_Rekeys()
        {
            var dir = TempDir();
            try
            {
                // hash(DATE) and hash(TIMESTAMP) differ, so a DATE key must be recomputed by the re-split.
                var config = WriteConfig(dir, """{ "tables": { "setup": { "kind": "dimension", "columns": ["Day"] }, "g": { "kind": "measurements", "columns": ["Meas"] } } }""");
                var more = WriteFile(dir, "more.csv", "Day,Meas\n2026-01-02,3.0\n");
                using var db = new DataBall(config);
                db.AddRows(new[]
                {
                    new Dictionary<string, object?> { ["Day"] = new DateTime(2026, 1, 2), ["Meas"] = 1.0 },
                    new Dictionary<string, object?> { ["Day"] = new DateTime(2026, 1, 2), ["Meas"] = 2.0 },
                });
                Assert.Equal(1, Count(db, "setup"));
                // Stand in for a session built from DATE values: the column is DATE and its key is hash(DATE).
                db.Query("ALTER TABLE \"setup\" ALTER COLUMN \"Day\" SET DATA TYPE DATE");
                db.Query("UPDATE \"setup\" SET \"setup_key\" = CAST(hash(\"Day\") >> 1 AS BIGINT)");
                db.Query("UPDATE \"rows\" SET \"setup_key\" = (SELECT \"setup_key\" FROM \"setup\")");
                Assert.Equal("DATE", ColumnType(db, "setup", "Day"));
                Assert.Equal("VIEW", TableType(db, "data"));

                await db.ImportAsync(more, new ImportOptions { Append = true });

                Assert.Equal("TIMESTAMP", ColumnType(db, "setup", "Day"));
                Assert.Equal(1, Count(db, "setup"));
                Assert.Equal(3, Count(db, "data"));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Theory]
        [InlineData("UBIGINT")]
        [InlineData("DECIMAL(10,2)")]
        public async Task Append_UnsignedOrDecimalVsInteger_Throws(string existingType)
        {
            var dir = TempDir();
            try
            {
                var more = WriteFile(dir, "more.csv", "v\n2\n");
                using var db = new DataBall();
                db.AddRows(new[] { new Dictionary<string, object?> { ["v"] = 1L } });
                db.Query($"ALTER TABLE \"data\" ALTER COLUMN \"v\" SET DATA TYPE {existingType}");
                Assert.Equal(existingType, ColumnType(db, "data", "v"));

                var ex = await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(more, new ImportOptions { Append = true }));
                var message = FullMessage(ex);
                Assert.Contains("'v'", message);
                Assert.Contains(existingType, message);
                Assert.Contains("BIGINT", message);
                Assert.Contains(more, message);
                Assert.Equal(existingType, ColumnType(db, "data", "v"));
                Assert.Equal(1, Count(db, "data"));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Append_AllNullIncoming_AdoptsExistingType(bool layout)
        {
            var dir = TempDir();
            try
            {
                // An all-empty column reads as VARCHAR; it has no value to conflict with, so it keeps DOUBLE.
                var more = WriteFile(dir, "more.csv", "id,v\n2,\n3,\n");
                using var db = layout ? new DataBall(WriteConfig(dir, MeasuredV)) : new DataBall();
                db.AddRows(new[] { new Dictionary<string, object?> { ["id"] = 1L, ["v"] = 1.5 } });

                await db.ImportAsync(more, new ImportOptions { Append = true });

                Assert.Equal("DOUBLE", ColumnType(db, layout ? "g" : "data", "v"));
                var rows = db.Query("SELECT id, v FROM data ORDER BY id");
                Assert.Equal(3, rows.Count);
                Assert.Equal(1.5, Assert.IsType<double>(rows[0]["v"]));
                Assert.Equal(2L, rows[1]["id"]);
                Assert.Null(rows[1]["v"]);
                Assert.Equal(3L, rows[2]["id"]);
                Assert.Null(rows[2]["v"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public void MergeOrAppend_IntegerThenFloat_WidensToDouble()
        {
            using var db = new DataBall();
            db.AddRows(new[] { new Dictionary<string, object?> { ["v"] = 1 } });
            using var other = new DataBall();
            other.AddRows(new[] { new Dictionary<string, object?> { ["v"] = 2.5f } });
            Assert.Equal("FLOAT", ColumnType(other, "data", "v"));

            db.MergeOrAppend(other, append: true);

            Assert.Equal("DOUBLE", ColumnType(db, "data", "v"));
            var rows = db.Query("SELECT v FROM data ORDER BY v");
            Assert.Equal(2, rows.Count);
            Assert.Equal(1.0, Assert.IsType<double>(rows[0]["v"]));
            Assert.Equal(2.5, Assert.IsType<double>(rows[1]["v"]));
        }

        [Fact]
        public void MergeOrAppend_Conflict_NamesTheMergedSession()
        {
            using var db = new DataBall();
            db.AddRows(new[] { new Dictionary<string, object?> { ["v"] = 1.5 } });
            using var other = new DataBall();
            other.AddRows(new[] { new Dictionary<string, object?> { ["v"] = "abc" } });

            var ex = Assert.Throws<DataBallException>(() => db.MergeOrAppend(other, append: true));
            var message = FullMessage(ex);
            Assert.Contains("the merged session", message);
            Assert.DoesNotContain(".parquet", message);
            Assert.Contains("'v'", message);
            Assert.Contains("DOUBLE", message);
            Assert.Contains("VARCHAR", message);
            Assert.Equal("DOUBLE", ColumnType(db, "data", "v"));
            Assert.Equal(1, Count(db, "data"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ImportAsync_Parquet_ConfigTypedColumn_StillCasts(bool layout)
        {
            var dir = TempDir();
            try
            {
                var parquet = Path.Combine(dir, "more.parquet");
                using (var writer = new DataBall())
                    writer.Query($"COPY (SELECT CAST(2.4 AS DOUBLE) AS v) TO '{parquet}' (FORMAT PARQUET)");
                var config = layout
                    ? WriteConfig(dir, """{ "columns": { "v": "long" }, "tables": { "g": { "kind": "measurements", "columns": ["v"] } } }""")
                    : WriteConfig(dir, """{ "columns": { "v": "long" } }""");
                using var db = new DataBall(config);
                db.AddRows(new[] { new Dictionary<string, object?> { ["v"] = 1L } });

                await db.ImportAsync(parquet, new ImportOptions { Append = true });

                Assert.Equal("BIGINT", ColumnType(db, layout ? "g" : "data", "v"));
                var rows = db.Query("SELECT v FROM data ORDER BY v");
                Assert.Equal(2, rows.Count);
                Assert.Equal(1L, rows[0]["v"]);
                Assert.Equal(2L, rows[1]["v"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task Append_AddColumnDeclaredType_AlwaysCasts()
        {
            var dir = TempDir();
            try
            {
                // AddColumn<int> registers an expected type, so the incoming 2.4 casts to INTEGER (2) and the column stays INTEGER.
                var more = WriteFile(dir, "more.csv", "v\n2.4\n");
                using var db = new DataBall();
                db.AddColumn<int>("v", new[] { 1 });

                await db.ImportAsync(more, new ImportOptions { Append = true });

                Assert.Equal("INTEGER", ColumnType(db, "data", "v"));
                var rows = db.Query("SELECT v FROM data ORDER BY v");
                Assert.Equal(1, rows[0]["v"]);
                Assert.Equal(2, rows[1]["v"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task ImportAsync_NativeBall_ConfigTypedColumn_StillCasts()
        {
            var dir = TempDir();
            try
            {
                var ball = Path.Combine(dir, "more.ball");
                using (var writer = new DataBall())
                {
                    writer.AddRows(new[] { new Dictionary<string, object?> { ["v"] = 2.4 } });
                    writer.Save(ball);
                }
                var config = WriteConfig(dir, """{ "columns": { "v": "long" } }""");
                using var db = new DataBall(config);
                db.AddRows(new[] { new Dictionary<string, object?> { ["v"] = 1L } });

                await db.ImportAsync(ball, new ImportOptions { Append = true });

                Assert.Equal("BIGINT", ColumnType(db, "data", "v"));
                var rows = db.Query("SELECT v FROM data ORDER BY v");
                Assert.Equal(2, rows.Count);
                Assert.Equal(1L, rows[0]["v"]);
                Assert.Equal(2L, rows[1]["v"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        private static string ColumnType(DataBall db, string table, string column)
        {
            var rows = db.Query($"""
                SELECT data_type FROM information_schema.columns
                WHERE table_schema = 'main' AND table_name = '{table}' AND column_name = '{column}'
                """);
            return rows.Count == 0 ? "" : (string)rows[0]["data_type"]!;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Append_AllNullExisting_AdoptsIncomingType(bool layout)
        {
            var dir = TempDir();
            try
            {
                var first = WriteFile(dir, "first.csv", "id,v\n1,\n2,\n");
                var more = WriteFile(dir, "more.csv", "id,v\n3,4.5\n");
                using var db = layout ? new DataBall(WriteConfig(dir, MeasuredV)) : new DataBall();
                await db.ImportAsync(first);
                Assert.Equal("VARCHAR", ColumnType(db, layout ? "g" : "data", "v"));

                await db.ImportAsync(more, new ImportOptions { Append = true });

                Assert.Equal("DOUBLE", ColumnType(db, layout ? "g" : "data", "v"));
                Assert.Equal(3, Count(db, "data"));
                var rows = db.Query("SELECT id, v FROM data ORDER BY id");
                Assert.Null(rows[0]["v"]);
                Assert.Null(rows[1]["v"]);
                Assert.Equal(4.5, Assert.IsType<double>(rows[2]["v"]));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task Append_DeclaredVarcharAllNull_StillCasts()
        {
            var dir = TempDir();
            try
            {
                // A declared type always casts: the all-empty declared VARCHAR stays VARCHAR and 4.5 casts to text.
                var first = WriteFile(dir, "first.csv", "id,v\n1,\n");
                var more = WriteFile(dir, "more.csv", "id,v\n3,4.5\n");
                using var db = new DataBall(WriteConfig(dir, """{ "columns": { "v": "string" } }"""));
                await db.ImportAsync(first);

                await db.ImportAsync(more, new ImportOptions { Append = true });

                Assert.Equal("VARCHAR", ColumnType(db, "data", "v"));
                var rows = db.Query("SELECT id, v FROM data ORDER BY id");
                Assert.Equal(2, rows.Count);
                Assert.Equal("4.5", rows[1]["v"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task Append_LearnedType_NotPersisted_WidensAfterReopen()
        {
            var dir = TempDir();
            try
            {
                var ball = Path.Combine(dir, "session.ball");
                var more = WriteFile(dir, "more.csv", "id,v\n2,2.7\n");
                using (var db = new DataBall())
                {
                    db.InitializeRow(new Dictionary<string, object?> { ["id"] = 1L, ["v"] = 1 });
                    db.CommitRow();
                    Assert.Equal("INTEGER", ColumnType(db, "data", "v"));
                    db.Save(ball);
                }

                using var reopened = DataBall.Open(ball, writable: true);
                await reopened.ImportAsync(more, new ImportOptions { Append = true });

                Assert.Equal("DOUBLE", ColumnType(reopened, "data", "v"));
                var rows = reopened.Query("SELECT v FROM data ORDER BY id");
                Assert.Equal(1.0, Assert.IsType<double>(rows[0]["v"]));
                Assert.Equal(2.7, Assert.IsType<double>(rows[1]["v"]));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public void MergeOrAppend_LearnedType_DoesNotBlockWidening()
        {
            using var db = new DataBall();
            db.InitializeRow(new Dictionary<string, object?> { ["v"] = 1 });
            db.CommitRow();
            Assert.Equal("INTEGER", ColumnType(db, "data", "v"));
            using var other = new DataBall();
            other.AddRows(new[] { new Dictionary<string, object?> { ["v"] = 2.7 } });

            db.MergeOrAppend(other, append: true);

            Assert.Equal("DOUBLE", ColumnType(db, "data", "v"));
            var rows = db.Query("SELECT v FROM data ORDER BY v");
            Assert.Equal(1.0, Assert.IsType<double>(rows[0]["v"]));
            Assert.Equal(2.7, Assert.IsType<double>(rows[1]["v"]));
        }

        [Fact]
        public async Task ImportAsync_NativeBall_Conflict_NamesTheFile()
        {
            var dir = TempDir();
            try
            {
                var ball = Path.Combine(dir, "more.ball");
                using (var writer = new DataBall())
                {
                    writer.AddRows(new[] { new Dictionary<string, object?> { ["v"] = "abc" } });
                    writer.Save(ball);
                }
                using var db = new DataBall();
                db.AddRows(new[] { new Dictionary<string, object?> { ["v"] = 1.5 } });

                var ex = await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(ball, new ImportOptions { Append = true }));
                var message = FullMessage(ex);
                Assert.Contains("'" + ball + "'", message);
                Assert.Contains("DOUBLE", message);
                Assert.Contains("VARCHAR", message);
                Assert.Equal("DOUBLE", ColumnType(db, "data", "v"));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task ImportAsync_NativeBall_SourceKeyNotWidened()
        {
            var dir = TempDir();
            try
            {
                // The destination's _source_key is INTEGER and the native file's is BIGINT. Lineage is never widened,
                // so the key stays INTEGER; v (a data column) still widens.
                var csvSource = WriteFile(dir, "src.csv", "v\n2.4\n");
                var ball = Path.Combine(dir, "src.ball");
                using (var writer = new DataBall())
                {
                    await writer.ImportAsync(csvSource);
                    Assert.Equal("BIGINT", ColumnType(writer, "data", "_source_key"));
                    writer.Save(ball);
                }
                var first = WriteFile(dir, "first.csv", "v\n1\n");
                using var db = new DataBall();
                await db.ImportAsync(first);
                db.Query("ALTER TABLE \"data\" ALTER COLUMN \"_source_key\" SET DATA TYPE INTEGER");

                await db.ImportAsync(ball, new ImportOptions { Append = true });

                Assert.Equal("INTEGER", ColumnType(db, "data", "_source_key"));
                Assert.Equal("DOUBLE", ColumnType(db, "data", "v"));
                var rows = db.Query("SELECT v, _source_key FROM data ORDER BY v");
                Assert.Equal(2, rows.Count);
                Assert.Equal(1, rows[0]["_source_key"]);
                Assert.Equal(2, rows[1]["_source_key"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task Append_DateThenTimestampWithTimeZone_WidensToCanonicalTimestamp()
        {
            using var db = new DataBall();
            db.AddRows(new[] { new Dictionary<string, object?> { ["v"] = new DateTime(2026, 1, 2) } });
            db.Query("ALTER TABLE \"data\" ALTER COLUMN \"v\" SET DATA TYPE DATE");
            using var other = new DataBall();
            other.AddRows(new[] { new Dictionary<string, object?> { ["v"] = new DateTime(2026, 1, 3, 10, 0, 0) } });
            other.Query("ALTER TABLE \"data\" ALTER COLUMN \"v\" SET DATA TYPE TIMESTAMPTZ");
            Assert.Equal("TIMESTAMP WITH TIME ZONE", ColumnType(other, "data", "v"));

            db.MergeOrAppend(other, append: true);

            // The widened type is the canonical TIMESTAMP, not the spelling of the incoming type.
            Assert.Equal("TIMESTAMP", ColumnType(db, "data", "v"));
            Assert.Equal(2, Count(db, "data"));
        }

        [Fact]
        public async Task Append_Layout_DeclaredKeyToDouble_Rekeys()
        {
            var dir = TempDir();
            try
            {
                var more = WriteFile(dir, "more.csv", "Temp,Attr,Meas\n25.0,x,3.0\n");
                var config = WriteConfig(dir, """{ "tables": { "setup": { "kind": "dimension", "columns": ["Temp", "Attr"], "key": ["Temp"] }, "g": { "kind": "measurements", "columns": ["Meas"] } } }""");
                using var db = new DataBall(config);
                db.AddRows(new[]
                {
                    new Dictionary<string, object?> { ["Temp"] = 25, ["Attr"] = "x", ["Meas"] = 1.0 },
                    new Dictionary<string, object?> { ["Temp"] = 25, ["Attr"] = "x", ["Meas"] = 2.0 },
                });
                Assert.Equal(1, Count(db, "setup"));

                await db.ImportAsync(more, new ImportOptions { Append = true });

                Assert.Equal("DOUBLE", ColumnType(db, "setup", "Temp"));
                Assert.Equal(1, Count(db, "setup"));
                Assert.Equal(3, Count(db, "data"));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task Append_Layout_DimensionKeyIntegerToBigint_KeepsKeys()
        {
            var dir = TempDir();
            try
            {
                var more = WriteFile(dir, "more.csv", "Temp,Meas\n25,3.0\n");
                var config = WriteConfig(dir, """{ "tables": { "setup": { "kind": "dimension", "columns": ["Temp"] }, "g": { "kind": "measurements", "columns": ["Meas"] } } }""");
                using var db = new DataBall(config);
                db.AddRows(new[]
                {
                    new Dictionary<string, object?> { ["Temp"] = 25, ["Meas"] = 1.0 },
                    new Dictionary<string, object?> { ["Temp"] = 25, ["Meas"] = 2.0 },
                });
                var keyBefore = db.Query("SELECT setup_key FROM setup");
                Assert.Single(keyBefore);

                await db.ImportAsync(more, new ImportOptions { Append = true });

                // hash() is equal for every integer width, so the key is reused without a re-split.
                Assert.Equal("BIGINT", ColumnType(db, "setup", "Temp"));
                Assert.Equal(1, Count(db, "setup"));
                AssertSameRows(keyBefore, db.Query("SELECT setup_key FROM setup"));
                Assert.Equal(3, Count(db, "data"));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task Append_Layout_FloatKeyToDouble_Rekeys()
        {
            var dir = TempDir();
            try
            {
                var more = WriteFile(dir, "more.csv", "Temp,Meas\n25.0,3.0\n");
                var config = WriteConfig(dir, """{ "tables": { "setup": { "kind": "dimension", "columns": ["Temp"] }, "g": { "kind": "measurements", "columns": ["Meas"] } } }""");
                using var db = new DataBall(config);
                db.AddRows(new[]
                {
                    new Dictionary<string, object?> { ["Temp"] = 25.0f, ["Meas"] = 1.0 },
                    new Dictionary<string, object?> { ["Temp"] = 25.0f, ["Meas"] = 2.0 },
                });
                Assert.Equal("FLOAT", ColumnType(db, "setup", "Temp"));
                Assert.Equal(1, Count(db, "setup"));

                await db.ImportAsync(more, new ImportOptions { Append = true });

                Assert.Equal("DOUBLE", ColumnType(db, "setup", "Temp"));
                Assert.Equal(1, Count(db, "setup"));
                Assert.Equal(3, Count(db, "data"));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Theory]
        [InlineData("wide")]
        [InlineData("layout")]
        [InlineData("resplit")]
        public async Task Append_FailureAfterWiden_RollsBack(string kind)
        {
            var dir = TempDir();
            try
            {
                // The widen runs first; the configured column s then fails to cast 'abc' to BIGINT inside the same append.
                var parquet = Path.Combine(dir, "more.parquet");
                using (var writer = new DataBall())
                    writer.Query(kind == "resplit"
                        ? $"COPY (SELECT CAST(25.0 AS DOUBLE) AS Temp, CAST(2.0 AS DOUBLE) AS Meas, 'abc' AS s) TO '{parquet}' (FORMAT PARQUET)"
                        : $"COPY (SELECT CAST(1.5 AS DOUBLE) AS n, 'abc' AS s) TO '{parquet}' (FORMAT PARQUET)");
                var config = kind switch
                {
                    "wide" => WriteConfig(dir, """{ "columns": { "s": "long" } }"""),
                    "layout" => WriteConfig(dir, """{ "columns": { "s": "long" }, "tables": { "g": { "kind": "measurements", "columns": ["n", "s"] } } }"""),
                    _ => WriteConfig(dir, """{ "columns": { "s": "long" }, "tables": { "setup": { "kind": "dimension", "columns": ["Temp"] }, "g": { "kind": "measurements", "columns": ["Meas", "s"] } } }"""),
                };
                using var db = new DataBall(config);
                if (kind == "resplit")
                    db.AddRows(new[] { new Dictionary<string, object?> { ["Temp"] = 25, ["Meas"] = 1.0, ["s"] = 1L } });
                else
                    db.AddRows(new[] { new Dictionary<string, object?> { ["n"] = 1, ["s"] = 1L } });
                var widened = kind == "resplit" ? "Temp" : "n";
                var owner = kind switch { "wide" => "data", "layout" => "g", _ => "setup" };
                var before = db.Query("SELECT * FROM data");
                Assert.Equal("INTEGER", ColumnType(db, owner, widened));

                await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(parquet, new ImportOptions { Append = true }));

                Assert.Equal("INTEGER", ColumnType(db, owner, widened));
                Assert.Equal("BIGINT", ColumnType(db, kind == "wide" ? "data" : "g", "s"));
                Assert.Equal(kind == "wide" ? "BASE TABLE" : "VIEW", TableType(db, "data"));
                AssertSameRows(before, db.Query("SELECT * FROM data"));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Append_LearnedInteger_CsvDouble_WidensBeforeSave(bool layout)
        {
            var dir = TempDir();
            try
            {
                // CommitRow learns INTEGER for v. A learned type never narrows: 2.7 keeps its value and v widens.
                var more = WriteFile(dir, "more.csv", "v\n2.7\n");
                using var db = layout ? new DataBall(WriteConfig(dir, MeasuredV)) : new DataBall();
                db.InitializeRow(new Dictionary<string, object?> { ["v"] = 1 });
                db.CommitRow();
                Assert.Equal("INTEGER", ColumnType(db, layout ? "g" : "data", "v"));

                await db.ImportAsync(more, new ImportOptions { Append = true });

                Assert.Equal("DOUBLE", ColumnType(db, layout ? "g" : "data", "v"));
                var rows = db.Query("SELECT v FROM data ORDER BY v");
                Assert.Equal(2, rows.Count);
                Assert.Equal(1.0, Assert.IsType<double>(rows[0]["v"]));
                Assert.Equal(2.7, Assert.IsType<double>(rows[1]["v"]));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Append_EmptyDoubleExisting_AdoptsIncomingVarchar(bool layout)
        {
            var dir = TempDir();
            try
            {
                // The session's v is an empty DOUBLE (a Parquet NULL column). An undeclared column with no value adopts
                // the incoming type, whatever it was, so the text 'abc' widens v to VARCHAR instead of throwing.
                var parquet = Path.Combine(dir, "first.parquet");
                using (var writer = new DataBall())
                    writer.Query($"COPY (SELECT CAST(1 AS BIGINT) AS id, CAST(NULL AS DOUBLE) AS v) TO '{parquet}' (FORMAT PARQUET)");
                var more = WriteFile(dir, "more.csv", "id,v\n2,abc\n");
                using var db = layout ? new DataBall(WriteConfig(dir, MeasuredV)) : new DataBall();
                await db.ImportAsync(parquet);
                Assert.Equal("DOUBLE", ColumnType(db, layout ? "g" : "data", "v"));

                await db.ImportAsync(more, new ImportOptions { Append = true });

                Assert.Equal("VARCHAR", ColumnType(db, layout ? "g" : "data", "v"));
                var rows = db.Query("SELECT id, v FROM data ORDER BY id");
                Assert.Equal(2, rows.Count);
                Assert.Null(rows[0]["v"]);
                Assert.Equal("abc", rows[1]["v"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task Import_ReplaceOverLearnedInteger_StillCasts()
        {
            var dir = TempDir();
            try
            {
                // A replace is not a merge: the learned INTEGER still casts 2.7 to 3, as it always has.
                var csv = WriteFile(dir, "replace.csv", "v\n2.7\n");
                using var db = new DataBall();
                db.InitializeRow(new Dictionary<string, object?> { ["v"] = 1 });
                db.CommitRow();
                Assert.Equal("INTEGER", ColumnType(db, "data", "v"));

                await db.ImportAsync(csv);

                Assert.Equal("INTEGER", ColumnType(db, "data", "v"));
                var rows = db.Query("SELECT v FROM data");
                Assert.Single(rows);
                Assert.Equal(3, rows[0]["v"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task Import_FirstImport_LearnedHeaderType_StillCasts()
        {
            var dir = TempDir();
            try
            {
                // A first import is not a merge: the header's learned long still casts 2.7 to 3, as it always has.
                var csv = WriteFile(dir, "first.csv", "Count(id),v\n2.7,1.5\n");
                using var db = new DataBall();
                await db.ImportAsync(csv);

                Assert.Equal("BIGINT", ColumnType(db, "data", "Count"));
                var rows = db.Query("SELECT Count FROM data");
                Assert.Equal(3L, rows[0]["Count"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        private static void AssertSameRows(IReadOnlyList<Dictionary<string, object?>> expected, IReadOnlyList<Dictionary<string, object?>> actual)
        {
            Assert.Equal(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Keys.OrderBy(k => k).ToArray(), actual[i].Keys.OrderBy(k => k).ToArray());
                foreach (var key in expected[i].Keys)
                    Assert.Equal(expected[i][key], actual[i][key]);
            }
        }

        private static string TableType(DataBall db, string table)
        {
            var rows = db.Query($"SELECT table_type FROM information_schema.tables WHERE table_schema = 'main' AND table_name = '{table}'");
            return rows.Count == 0 ? "" : (string)rows[0]["table_type"]!;
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

        private static string WriteFile(string dir, string name, string content)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, content);
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
            var dir = Path.Combine(Path.GetTempPath(), "databall-merge-" + Guid.NewGuid().ToString("N"));
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
