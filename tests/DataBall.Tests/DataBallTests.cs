using System;
using System.Collections.Generic;
using System.IO;
using squalor.DataBall.Export;
using squalor.DataBall.Import;
using Xunit;

namespace squalor.DataBall.Tests
{
    /// <summary>
    /// Contains unit tests for the <see cref="DataBall"/> class.
    /// </summary>
    public class DataBallTests
    {
        /// <summary>
        /// Tests adding columns to a DataBall instance.
        /// </summary>
        [Fact]
        public void TestAddColumn()
        {
            using var db = new DataBall();
            db.AddColumn("Name", new[] { "Alice", "Bob" });
            db.AddColumn<int>("Age", new[] { 30, 25 });
            var rows = db.Query("SELECT \"Name\", \"Age\" FROM \"data\" ORDER BY \"Age\" DESC");
            Assert.Equal(2, rows.Count);
            Assert.Equal("Alice", rows[0]["Name"]);
            Assert.Equal(30, Convert.ToInt32(rows[0]["Age"]));
        }

        /// <summary>
        /// Tests adding a single row with string and integer columns.
        /// </summary>
        [Fact]
        public void TestAnother()
        {
            using var db = new DataBall();
            db.AddColumn("Name", new[] { "Charlie" });
            db.AddColumn<int>("Age", new[] { 40 });
            var rows = db.Query("SELECT \"Name\", \"Age\" FROM \"data\"");
            Assert.Single(rows);
            Assert.Equal("Charlie", rows[0]["Name"]);
            Assert.Equal(40, Convert.ToInt32(rows[0]["Age"]));
        }

        [Fact]
        public void Query_SelectStar_ReturnsDictionaries()
        {
            using var db = new DataBall();
            db.AddColumn("Name", new string?[] { "Alice", null });
            db.AddColumn<int>("Age", new[] { 30, 25 });
            var rows = db.Query("SELECT * FROM \"data\"");
            Assert.Equal(2, rows.Count);
            Assert.True(rows[0].ContainsKey("Name"));
            Assert.True(rows[0].ContainsKey("Age"));
            Assert.Equal("Alice", rows[0]["Name"]);
            Assert.Null(rows[1]["Name"]);
            Assert.NotSame(DBNull.Value, rows[1]["Name"]);
        }

        [Fact]
        public void Metadata_SetAndGet()
        {
            using var db = new DataBall();
            db.SetMetadata("Version", "1.0");
            Assert.Equal("1.0", db.Metadata["Version"]);
            var copy = db.Metadata;
            Assert.False(copy.ContainsKey("Nope"));
            Assert.Equal("1.0", copy["Version"]);
        }

        [Fact]
        public void Metadata_FromConfigFile()
        {
            var dir = TempDir();
            try
            {
                var configPath = Path.Combine(dir, "config.json");
                File.WriteAllText(configPath, """
                    {
                      "Metadata": { "Operator": "Alice" },
                      "Columns": { "Age": "int" }
                    }
                    """);
                using var db = new DataBall(configPath);
                Assert.Equal("Alice", db.Metadata["Operator"]);
                db.AddColumn<int>("Age", new[] { 30 });
                var rows = db.Query("SELECT \"Age\" FROM \"data\"");
                Assert.Equal(30, Convert.ToInt32(rows[0]["Age"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void AddRow_InsertsDictionary()
        {
            using var db = new DataBall();
            db.AddColumn("Name", new[] { "Alice", "Bob" });
            db.AddColumn<int>("Age", new[] { 30, 25 });
            db.AddRow(new Dictionary<string, object?> { ["Name"] = "Carol" });
            var rows = db.Query("SELECT \"Name\", \"Age\" FROM \"data\" ORDER BY \"Name\"");
            Assert.Equal(3, rows.Count);
            var carol = Assert.Single(rows, r => Equals(r["Name"], "Carol"));
            Assert.Null(carol["Age"]);
        }

        [Fact]
        public void AddRow_CreatesTableWhenEmpty()
        {
            using var db = new DataBall();
            db.AddRow(new Dictionary<string, object?>
            {
                ["Name"] = "Dana",
                ["Age"] = 22
            });
            var rows = db.Query("SELECT \"Name\", \"Age\" FROM \"data\"");
            Assert.Single(rows);
            Assert.Equal("Dana", rows[0]["Name"]);
            Assert.Equal(22, Convert.ToInt32(rows[0]["Age"]));
        }

        [Fact]
        public void AddColumn_LongBoolDateTimeFloatDouble()
        {
            using var db = new DataBall();
            db.AddColumn<long>("L", new[] { 5L });
            db.AddColumn<bool>("B", new[] { true });
            db.AddColumn<DateTime>("Dt", new[] { new DateTime(2020, 5, 6, 7, 8, 9) });
            db.AddColumn<float>("F", new[] { 1.25f });
            db.AddColumn<double>("Dbl", new[] { 2.5 });
            var row = Assert.Single(db.Query("SELECT * FROM \"data\""));
            Assert.IsType<long>(row["L"]);
            Assert.Equal(5L, row["L"]);
            Assert.IsType<bool>(row["B"]);
            Assert.True(Assert.IsType<bool>(row["B"]));
            var dt = Assert.IsType<DateTime>(row["Dt"]);
            Assert.Equal(2020, dt.Year);
            Assert.Equal(5, dt.Month);
            Assert.Equal(6, dt.Day);
            Assert.IsType<float>(row["F"]);
            Assert.IsType<double>(row["Dbl"]);
        }

        [Fact]
        public void RemoveColumn_DropsAndQueryFailsToSelectIt()
        {
            using var db = new DataBall();
            db.AddColumn("Name", new[] { "A" });
            db.AddColumn<int>("Age", new[] { 1 });
            db.RemoveColumn("Age");
            Assert.Throws<DataBallException>(() => db.Query("SELECT \"Age\" FROM \"data\""));
        }

        [Fact]
        public void AddColumn_LengthMismatch_Throws()
        {
            using var db = new DataBall();
            db.AddColumn("Name", new[] { "A", "B" });
            Assert.Throws<DataBallException>(() => db.AddColumn<int>("Age", new[] { 1 }));
        }

        [Fact]
        public void AddColumn_QuotedKeywordName()
        {
            using var db = new DataBall();
            db.AddColumn<int>("Order", new[] { 1, 2 });
            var rows = db.Query("SELECT \"Order\" FROM \"data\"");
            Assert.Equal(2, rows.Count);
            Assert.Equal(1, Convert.ToInt32(rows[0]["Order"]));
            Assert.Equal(2, Convert.ToInt32(rows[1]["Order"]));
        }

        [Fact]
        public void ImportCsv_ThenQuery()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "data.csv");
                File.WriteAllText(path, "Name,Age\nAlice,30\nBob,25\n");
                using var db = new DataBall();
                ImportManager.ImportFromCsv(db, path);
                var rows = db.Query("SELECT \"Name\", \"Age\" FROM \"data\" ORDER BY \"Name\"");
                Assert.Equal(2, rows.Count);
                Assert.Equal("Alice", rows[0]["Name"]);
                Assert.Equal(30, Convert.ToInt32(rows[0]["Age"]));
                Assert.Equal("Bob", rows[1]["Name"]);
                Assert.Equal(25, Convert.ToInt32(rows[1]["Age"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportParquet_RoundTrip()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "data.parquet");
                using var db = new DataBall();
                db.AddColumn("Name", new[] { "Alice", "Bob" });
                db.AddColumn<int>("Age", new[] { 30, 25 });
                await ExportManager.ExportToParquet(db, path);
                using var imported = new DataBall();
                await ImportManager.ImportFromParquet(imported, path, append: false);
                var rows = imported.Query("SELECT \"Name\", \"Age\" FROM \"data\" ORDER BY \"Age\" DESC");
                Assert.Equal(2, rows.Count);
                Assert.Equal("Alice", rows[0]["Name"]);
                Assert.Equal(30, Convert.ToInt32(rows[0]["Age"]));
                Assert.Equal("Bob", rows[1]["Name"]);
                Assert.Equal(25, Convert.ToInt32(rows[1]["Age"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void MergeOrAppend_UnionColumns()
        {
            using var a = new DataBall();
            a.AddColumn("Name", new[] { "Alice" });
            using var b = new DataBall();
            b.AddColumn("Name", new[] { "Bob" });
            b.AddColumn<int>("Age", new[] { 25 });
            a.MergeOrAppend(b, append: true);
            var rows = a.Query("SELECT \"Name\", \"Age\" FROM \"data\" ORDER BY \"Name\"");
            Assert.Equal(2, rows.Count);
            Assert.Equal("Alice", rows[0]["Name"]);
            Assert.Null(rows[0]["Age"]);
            Assert.Equal("Bob", rows[1]["Name"]);
            Assert.Equal(25, Convert.ToInt32(rows[1]["Age"]));
        }

        [Fact]
        public async Task Bounce_ExtractsConstantAndDedups()
        {
            using var db = new DataBall();
            db.AddColumn("Site", new[] { "A", "A" });
            db.AddColumn<int>("Meas", new[] { 1, 1 });
            await db.Bounce();
            Assert.Equal("A", db.Metadata["Site"]);
            var count = db.Query("SELECT COUNT(*) AS c FROM \"data\"");
            Assert.Equal(1, Convert.ToInt32(count[0]["c"]));
            var rows = db.Query("SELECT * FROM \"data\"");
            Assert.Single(rows);
            Assert.False(rows[0].ContainsKey("Site"));
            Assert.Equal(1, Convert.ToInt32(rows[0]["Meas"]));
        }

        [Fact]
        public void MergeOrAppend_IncomingColumnMatchesMetadata_DoesNotThrow()
        {
            using var dest = new DataBall();
            dest.SetMetadata("Site", "A");
            dest.AddColumn<int>("Meas", new[] { 1 });
            using var src = new DataBall();
            src.AddColumn("Site", new[] { "A" });
            dest.MergeOrAppend(src, append: true);
            Assert.Equal("A", dest.Metadata["Site"]);
            var rows = dest.Query("SELECT * FROM \"data\"");
            Assert.Equal(2, rows.Count);
            Assert.Equal(1, Convert.ToInt32(rows[0]["Meas"]));
            Assert.Null(rows[1]["Meas"]);
            Assert.False(rows[0].ContainsKey("Site"));
            Assert.False(rows[1].ContainsKey("Site"));
        }

        [Fact]
        public void ImportCsv_IncomingColumnMatchesMetadata_DoesNotThrow()
        {
            var dir = TempDir();
            try
            {
                using var dest = new DataBall();
                dest.SetMetadata("Site", "A");
                dest.AddColumn<int>("Meas", new[] { 1 });
                var path = Path.Combine(dir, "data.csv");
                File.WriteAllText(path, "Site\nA\n");
                ImportManager.ImportFromCsv(dest, path);
                Assert.Equal("A", dest.Metadata["Site"]);
                var rows = dest.Query("SELECT * FROM \"data\"");
                Assert.Equal(2, rows.Count);
                Assert.Null(rows[1]["Meas"]);
                Assert.False(rows[0].ContainsKey("Site"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ImportCsv_DateColumn_ThenAddRow_DoesNotThrow()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "data.csv");
                File.WriteAllText(path, "When,N\n2020-01-02,1\n");
                using var db = new DataBall();
                ImportManager.ImportFromCsv(db, path);
                var rows = db.Query("SELECT * FROM \"data\"");
                Assert.Single(rows);
                var when = Assert.IsType<DateTime>(rows[0]["When"]);
                Assert.Equal(2020, when.Year);
                Assert.Equal(1, when.Month);
                Assert.Equal(2, when.Day);
                db.AddRow(new Dictionary<string, object?>
                {
                    ["When"] = new DateTime(2020, 1, 3),
                    ["N"] = 2
                });
                Assert.Equal(2, db.Query("SELECT * FROM \"data\"").Count);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Dispose_ThenQuery_Throws()
        {
            var db = new DataBall();
            db.Dispose();
            Assert.Throws<ObjectDisposedException>(() => db.Query("SELECT 1"));
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-pr2", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
