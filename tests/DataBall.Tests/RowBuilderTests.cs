using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace squalor.DataBall.Tests
{
    public class RowBuilderTests
    {
        [Fact]
        public void InitializeRow_PropagatesLastRow()
        {
            using var db = new DataBall();
            db.AddColumn("Name", new[] { "Alice" });
            db.AddColumn<int>("Age", new[] { 30 });
            db.InitializeRow();
            db.ModifyField("Age", 31);
            db.CommitRow();
            var rows = db.Query("SELECT * FROM \"data\" ORDER BY rowid");
            Assert.Equal(2, rows.Count);
            Assert.Equal("Alice", rows[0]["Name"]);
            Assert.Equal(30, Convert.ToInt32(rows[0]["Age"]));
            Assert.Equal("Alice", rows[1]["Name"]);
            Assert.Equal(31, Convert.ToInt32(rows[1]["Age"]));
        }

        [Fact]
        public void CommitRow_TriggerChange_ResetsDependents()
        {
            var dir = TempDir();
            try
            {
                var path = WriteConfig(dir);
                using var db = new DataBall(path);
                db.InitializeRow(new Dictionary<string, object?>
                {
                    ["Name"] = "Alice",
                    ["Age"] = 30,
                    ["Date"] = new DateTime(2020, 1, 2)
                });
                db.CommitRow();
                db.InitializeRow();
                db.ModifyField("Name", "Bob");
                db.CommitRow();
                var rows = db.Query("SELECT * FROM \"data\" ORDER BY rowid");
                Assert.Equal(2, rows.Count);
                Assert.Equal("Alice", rows[0]["Name"]);
                Assert.Equal(30, Convert.ToInt32(rows[0]["Age"]));
                Assert.Equal("Bob", rows[1]["Name"]);
                Assert.Null(rows[1]["Age"]);
                Assert.Null(rows[1]["Date"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void CommitRow_ExplicitModifyDependent_NotReset()
        {
            var dir = TempDir();
            try
            {
                var path = WriteConfig(dir);
                using var db = new DataBall(path);
                db.InitializeRow(new Dictionary<string, object?>
                {
                    ["Name"] = "Alice",
                    ["Age"] = 30,
                    ["Date"] = new DateTime(2020, 1, 2)
                });
                db.CommitRow();
                db.InitializeRow();
                db.ModifyField("Name", "Bob");
                db.ModifyField("Age", 40);
                db.CommitRow();
                var rows = db.Query("SELECT * FROM \"data\" ORDER BY rowid");
                Assert.Equal(2, rows.Count);
                Assert.Equal("Bob", rows[1]["Name"]);
                Assert.Equal(40, Convert.ToInt32(rows[1]["Age"]));
                Assert.Null(rows[1]["Date"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void CommitRow_WithoutInitialize_Throws()
        {
            using var db = new DataBall();
            var ex = Assert.Throws<DataBallException>(() => db.CommitRow());
            Assert.Equal("No pending row. Call InitializeRow before CommitRow.", ex.Message);
        }

        [Fact]
        public void ModifyField_WithoutInitialize_Throws()
        {
            using var db = new DataBall();
            var ex = Assert.Throws<DataBallException>(() => db.ModifyField("Name", "Alice"));
            Assert.Equal("No pending row. Call InitializeRow before ModifyField.", ex.Message);
        }

        [Fact]
        public void InitializeRow_EmptyTable_CommitsInitialValues()
        {
            using var db = new DataBall();
            db.InitializeRow(new Dictionary<string, object?>
            {
                ["Name"] = "Alice",
                ["Age"] = 30
            });
            db.CommitRow();
            var rows = db.Query("SELECT * FROM \"data\" ORDER BY rowid");
            var row = Assert.Single(rows);
            Assert.Equal("Alice", row["Name"]);
            Assert.Equal(30, Convert.ToInt32(row["Age"]));
        }

        [Fact]
        public void CommitRow_AddsNewColumn_TypeFromConfig()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "config.json");
                File.WriteAllText(path, """
                    {
                      "columns": { "Age": "int" }
                    }
                    """);
                using var db = new DataBall(path);
                db.AddColumn("Name", new[] { "Alice" });
                db.InitializeRow();
                db.ModifyField("Age", 30);
                db.CommitRow();
                var rows = db.Query("SELECT * FROM \"data\" ORDER BY rowid");
                Assert.Equal(2, rows.Count);
                Assert.Null(rows[0]["Age"]);
                Assert.Equal("Alice", rows[1]["Name"]);
                Assert.Equal(30, Convert.ToInt32(rows[1]["Age"]));
                Assert.IsType<int>(rows[1]["Age"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void CommitRow_AddsNewColumn_TypeInferred()
        {
            using var db = new DataBall();
            db.AddColumn("Name", new[] { "A" });
            db.InitializeRow();
            db.ModifyField("Score", 1.5);
            db.CommitRow();
            var rows = db.Query("SELECT * FROM \"data\" ORDER BY rowid");
            Assert.Equal(2, rows.Count);
            Assert.Null(rows[0]["Score"]);
            Assert.Equal("A", rows[1]["Name"]);
            Assert.IsType<double>(rows[1]["Score"]);
            Assert.Equal(1.5, Assert.IsType<double>(rows[1]["Score"]));
        }

        [Fact]
        public void InitializeRow_ReplacesUncommittedPending()
        {
            using var db = new DataBall();
            db.InitializeRow(new Dictionary<string, object?> { ["Name"] = "A" });
            db.ModifyField("Age", 1);
            db.InitializeRow(new Dictionary<string, object?> { ["Name"] = "B" });
            db.CommitRow();
            var rows = db.Query("SELECT * FROM \"data\" ORDER BY rowid");
            var row = Assert.Single(rows);
            Assert.Equal("B", row["Name"]);
            Assert.False(row.ContainsKey("Age"));
        }

        [Fact]
        public void CommitRow_TriggerUnchanged_DoesNotReset()
        {
            var dir = TempDir();
            try
            {
                var path = WriteConfig(dir);
                using var db = new DataBall(path);
                var date = new DateTime(2020, 1, 2);
                db.InitializeRow(new Dictionary<string, object?>
                {
                    ["Name"] = "Alice",
                    ["Age"] = 30,
                    ["Date"] = date
                });
                db.CommitRow();
                db.InitializeRow();
                db.ModifyField("Age", 31);
                db.CommitRow();
                var rows = db.Query("SELECT * FROM \"data\" ORDER BY rowid");
                Assert.Equal(2, rows.Count);
                Assert.Equal("Alice", rows[1]["Name"]);
                Assert.Equal(31, Convert.ToInt32(rows[1]["Age"]));
                var committed = Assert.IsType<DateTime>(rows[1]["Date"]);
                Assert.Equal(2020, committed.Year);
                Assert.Equal(1, committed.Month);
                Assert.Equal(2, committed.Day);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void CommitRow_NumericTrigger_IntEqualsLong()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "config.json");
                File.WriteAllText(path, """
                    {
                      "columns": { "Id": "long", "Name": "string" },
                      "relationships": [ { "trigger": "Id", "reset": ["Name"] } ]
                    }
                    """);
                using var db = new DataBall(path);
                db.AddColumn<long>("Id", new[] { 1L });
                db.AddColumn("Name", new[] { "Alice" });
                db.InitializeRow();
                db.ModifyField("Id", 1);
                db.CommitRow();
                var rows = db.Query("SELECT * FROM \"data\" ORDER BY rowid");
                Assert.Equal(2, rows.Count);
                Assert.Equal("Alice", rows[1]["Name"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Dispose_WithPending_DoesNotThrowOrCommit()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-rb-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "session.duckdb");
            try
            {
                using (var db = new DataBall(databasePath: path))
                {
                    db.AddColumn("Name", new[] { "Alice" });
                    db.InitializeRow();
                    db.ModifyField("Name", "Bob");
                }

                using var reopened = new DataBall(databasePath: path);
                var names = reopened.Query("SELECT Name FROM data").Select(r => r["Name"]?.ToString()).ToArray();
                Assert.Equal(new[] { "Alice" }, names);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        [Fact]
        public async Task Bounce_WithPending_Throws()
        {
            using var db = new DataBall();
            db.AddColumn("Name", new[] { "Alice" });
            db.InitializeRow();
            db.ModifyField("Name", "Bob");
            var ex = await Assert.ThrowsAsync<DataBallException>(() => db.Bounce());
            Assert.Equal("Commit or discard the pending row first.", ex.Message);
            var rows = db.Query("SELECT * FROM \"data\" ORDER BY rowid");
            var row = Assert.Single(rows);
            Assert.Equal("Alice", row["Name"]);
        }

        [Fact]
        public void ModifyField_EmptyName_Throws()
        {
            using var db = new DataBall();
            db.InitializeRow();
            var ex = Assert.Throws<DataBallException>(() => db.ModifyField(" ", "x"));
            Assert.Equal("Field name is required.", ex.Message);
        }

        [Fact]
        public void InitializeRow_BlankInitialKey_Throws()
        {
            using var db = new DataBall();
            var ex = Assert.Throws<DataBallException>(() =>
                db.InitializeRow(new Dictionary<string, object?> { [" "] = "x" }));
            Assert.Equal("Field name is required.", ex.Message);
        }

        [Fact]
        public void CommitRow_QuotedColumnName()
        {
            using var db = new DataBall();
            db.InitializeRow();
            db.ModifyField("Order", 5);
            db.CommitRow();
            var rows = db.Query("SELECT \"Order\" FROM \"data\" ORDER BY rowid");
            var row = Assert.Single(rows);
            Assert.Equal(5, Convert.ToInt32(row["Order"]));
        }

        [Fact]
        public void CommitRow_EmptyRow_NoTable_Throws()
        {
            using var db = new DataBall();
            db.InitializeRow();
            var ex = Assert.Throws<DataBallException>(() => db.CommitRow());
            Assert.Equal("Cannot commit an empty row.", ex.Message);
        }

        [Fact]
        public void InitializeRow_ThenDispose_QueryThrowsObjectDisposed()
        {
            var db = new DataBall();
            db.InitializeRow();
            db.ModifyField("Name", "Alice");
            db.Dispose();
            Assert.Throws<ObjectDisposedException>(() => db.Query("SELECT 1"));
        }

        private static string WriteConfig(string dir)
        {
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, """
                {
                  "columns": { "Name": "string", "Age": "int", "Date": "datetime" },
                  "relationships": [
                    { "trigger": "Name", "reset": ["Age", "Date"] }
                  ]
                }
                """);
            return path;
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-pr3", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
