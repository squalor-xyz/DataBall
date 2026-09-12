// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using squalor.DataBall.Export;
using Xunit;

namespace squalor.DataBall.Tests
{
    public class FileBackedTests
    {
        [Fact]
        public void Ctor_Memory_StillDefault()
        {
            using (var db = new DataBall())
            {
                db.AddColumn("Name", new[] { "Alice" });
            }

            using var other = new DataBall();
            Assert.Throws<DataBallException>(() => other.Query("SELECT * FROM data"));
        }

        [Fact]
        public void Ctor_FilePath_CreatesFile_DisposeDoesNotDelete()
        {
            var dir = TempDir();
            var path = Path.Combine(dir, "session.duckdb");
            try
            {
                using (var db = new DataBall(databasePath: path))
                    db.AddColumn("Name", new[] { "Alice" });

                Assert.True(File.Exists(path));
                File.Delete(path);
                Assert.False(File.Exists(path));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public void Ctor_FilePath_PersistsRowsAndMetadataAcrossReopen()
        {
            var dir = TempDir();
            var path = Path.Combine(dir, "session.duckdb");
            try
            {
                using (var db = new DataBall(databasePath: path))
                {
                    db.SetMetadata("Source", "ATE");
                    db.AddColumn("Name", new[] { "Alice" });
                    db.AddColumn<int>("Age", new[] { 30 });
                }

                using var reopened = new DataBall(databasePath: path);
                Assert.Equal("ATE", reopened.Metadata["Source"]);
                var row = Assert.Single(reopened.Query("SELECT Name, Age FROM data"));
                Assert.Equal("Alice", row["Name"]);
                Assert.Equal(30, Convert.ToInt32(row["Age"]));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public void Ctor_OverlappingSecondOpen_Throws()
        {
            var dir = TempDir();
            var path = Path.Combine(dir, "session.duckdb");
            try
            {
                using var first = new DataBall(databasePath: path);
                first.AddColumn("Name", new[] { "Alice" });
                Assert.Throws<DataBallException>(() => new DataBall(databasePath: path));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public void Ctor_BadConfig_FileCanBeOpenedAgain()
        {
            var dir = TempDir();
            var path = Path.Combine(dir, "session.duckdb");
            var config = Path.Combine(dir, "bad.json");
            File.WriteAllText(config, "{");
            try
            {
                Assert.Throws<DataBallException>(() => new DataBall(config, databasePath: path));
                using var db = new DataBall(databasePath: path);
                db.AddColumn("Name", new[] { "Alice" });
                Assert.Equal("Alice", Assert.Single(db.Query("SELECT Name FROM data"))["Name"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Theory]
        [InlineData("""{"csv": null}""")]
        [InlineData("""{"metadata": null}""")]
        [InlineData("""{"relationships": null}""")]
        [InlineData("""{"stimulus": null}""")]
        [InlineData("""{"metadataFields": null}""")]
        public void Ctor_ConfigWithExplicitNulls_DoesNotThrowNullReference(string json)
        {
            var dir = TempDir();
            var path = Path.Combine(dir, "session.duckdb");
            var config = Path.Combine(dir, "overlay.json");
            File.WriteAllText(config, json);
            try
            {
                using var db = new DataBall(config, databasePath: path);
                Assert.NotNull(db.Schema.Csv);
                Assert.NotNull(db.Schema.Metadata);
                Assert.NotNull(db.Schema.Relationships);
                Assert.NotNull(db.Schema.Stimulus);
                Assert.NotNull(db.Schema.MetadataFields);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public void Ctor_MissingParentDirectory_Throws()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "missing", "session.duckdb");
                Assert.Throws<DataBallException>(() => new DataBall(databasePath: path));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public void Ctor_WhitespaceDatabasePath_Throws()
        {
            Assert.Throws<DataBallException>(() => new DataBall(databasePath: "   "));
        }

        [Fact]
        public void Ctor_RejectsCatalogDuckDbName()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "catalog.duckdb");
                var ex = Assert.Throws<DataBallException>(() => new DataBall(databasePath: path));
                Assert.Contains("catalog.duckdb", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.False(File.Exists(path));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public void Ctor_ConfigPathAndDatabasePath_Together()
        {
            var dir = TempDir();
            var duck = Path.Combine(dir, "session.duckdb");
            try
            {
                var config = Path.Combine(dir, "config.json");
                File.WriteAllText(config, """{ "columns": { "Name": "string", "Age": "int" } }""");
                using var db = new DataBall(config, databasePath: duck);
                db.AddRows(new[]
                {
                    new Dictionary<string, object?> { ["Name"] = "Alice", ["Age"] = 30 }
                });
                Assert.Equal(30, Assert.IsType<int>(db.Query("SELECT Age FROM data")[0]["Age"]));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task FileBacked_ImportCsv_Bounce_SaveBall()
        {
            var dir = TempDir();
            var duck = Path.Combine(dir, "session.duckdb");
            try
            {
                var csv = Path.Combine(dir, "meas.csv");
                File.WriteAllText(csv, "Site,Meas\nA,1\nA,2\n");
                using var db = new DataBall(databasePath: duck);
                await db.ImportAsync(csv);
                await db.Bounce();
                Assert.Equal("A", db.Metadata["Site"]?.ToString());
                var ball = Path.Combine(dir, "out.ball");
                await db.SaveAsync(ball);
                using var opened = DataBall.Open(ball);
                Assert.Equal("A", opened.Metadata["Site"]?.ToString());
                Assert.Equal(2, opened.Query("SELECT Meas FROM data").Count);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task FileBacked_FilterWindow()
        {
            var dir = TempDir();
            var duck = Path.Combine(dir, "session.duckdb");
            try
            {
                var csv = Path.Combine(dir, "people.csv");
                File.WriteAllText(csv, "Name,Age\nAlice,30\nBob,25\nCarol,40\n");
                using var db = new DataBall(databasePath: duck);
                await db.ImportAsync(csv);
                var rows = db.Filter(new SessionFilter
                {
                    Columns = ["Name"],
                    Predicates =
                    [
                        new ColumnPredicate { Column = "Name", Op = PredicateOp.Eq, Value = "Alice" }
                    ]
                });
                var row = Assert.Single(rows);
                Assert.Equal("Alice", row["Name"]);
                Assert.False(row.ContainsKey("Age"));
                Assert.Equal(3L, Convert.ToInt64(db.Query("SELECT COUNT(*) AS n FROM data")[0]["n"]));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-file-" + Guid.NewGuid().ToString("N"));
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
                // Windows may keep a .wal until GC finalizes the native handle.
            }
        }
    }
}
