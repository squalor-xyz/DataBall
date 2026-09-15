// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace squalor.DataBall.Tests
{
    public class BatchInsertTests
    {
        [Fact]
        public void AddRow_InsertFailsAfterSchemaChange_LeavesNoNewColumn()
        {
            using var db = new DataBall();
            db.AddColumn("Name", new[] { "Alice" });
            db.AddColumn("Age", new[] { 30 });
            Assert.ThrowsAny<Exception>(() => db.AddRow(Dict(("Name", "Bob"), ("Age", "not-an-int"), ("Extra", 1))));
            var cols = db.Query("""
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = 'main' AND table_name = 'data'
                """);
            Assert.DoesNotContain(cols, r => string.Equals(Convert.ToString(r["column_name"]), "Extra", StringComparison.OrdinalIgnoreCase));
            Assert.Single(db.Query("SELECT * FROM data"));
        }

        [Fact]
        public void AddRows_BatchFailsMidway_LeavesNoNewColumns()
        {
            using var db = new DataBall();
            db.AddColumn("Name", new[] { "Alice" });
            db.AddColumn("Age", new[] { 30 });
            Assert.ThrowsAny<Exception>(() => db.AddRows(
            [
                Dict(("Name", "Bob"), ("Age", 25), ("Extra", 1)),
                Dict(("Name", "Carol"), ("Age", "not-an-int"), ("Extra", 2)),
            ]));
            var cols = db.Query("""
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = 'main' AND table_name = 'data'
                """);
            Assert.DoesNotContain(cols, r => string.Equals(Convert.ToString(r["column_name"]), "Extra", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void AddRows_BatchFailsMidway_InsertsNoRows()
        {
            using var db = new DataBall();
            db.AddColumn("Name", new[] { "Alice" });
            db.AddColumn("Age", new[] { 30 });
            Assert.ThrowsAny<Exception>(() => db.AddRows(
            [
                Dict(("Name", "Bob"), ("Age", 25)),
                Dict(("Name", "Carol"), ("Age", "not-an-int")),
            ]));
            var rows = db.Query("SELECT Name FROM data");
            Assert.Equal("Alice", Assert.Single(rows)["Name"]);
        }

        [Fact]
        public void AddRows_InsertsAll_WithoutCommitRowLoop()
        {
            using var db = new DataBall();
            db.AddRows(new[]
            {
                Dict(("Name", "Alice")),
                Dict(("Name", "Bob")),
                Dict(("Name", "Carol")),
            });
            var rows = db.Query("SELECT Name FROM data ORDER BY Name");
            Assert.Equal(3, rows.Count);
            Assert.Equal("Alice", rows[0]["Name"]);
            Assert.Equal("Bob", rows[1]["Name"]);
            Assert.Equal("Carol", rows[2]["Name"]);
        }

        [Fact]
        public void AddRows_AppliesExpectedTypes_FromConfig()
        {
            var dir = TempDir();
            try
            {
                var config = Path.Combine(dir, "config.json");
                File.WriteAllText(config, """{ "columns": { "Name": "string", "Age": "int" } }""");
                using var db = new DataBall(config);
                db.AddRows(new[]
                {
                    Dict(("Name", "Alice"), ("Age", 30)),
                    Dict(("Name", "Bob"), ("Age", 25)),
                });
                var rows = db.Query("SELECT Name, Age FROM data ORDER BY Name");
                Assert.Equal(30, Assert.IsType<int>(rows[0]["Age"]));
                Assert.Equal(25, Assert.IsType<int>(rows[1]["Age"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void AddRows_EmptyEnumerable_NoTableChange()
        {
            using var empty = new DataBall();
            empty.AddRows(Array.Empty<Dictionary<string, object?>>());
            Assert.Throws<DataBallException>(() => empty.Query("SELECT * FROM data"));

            using var db = new DataBall();
            db.AddColumn("Name", new[] { "Alice" });
            db.AddRows(Array.Empty<Dictionary<string, object?>>());
            Assert.Equal("Alice", Assert.Single(db.Query("SELECT Name FROM data"))["Name"]);
        }

        [Fact]
        public void AddRows_ObfuscatorFixture_MatchesOpenRowCount()
        {
            var csv = Path.Combine(AppContext.BaseDirectory, "fixtures", "semiconductor-sweep.csv");
            using var opened = DataBall.Open(csv);
            var source = opened.Query("SELECT * FROM data");
            Assert.Equal(81, source.Count);

            using var db = new DataBall();
            db.AddRows(source);
            Assert.Equal(81, db.Query("SELECT * FROM data").Count);
        }

        [Fact]
        public void AddRows_DoesNotApplyTriggerReset()
        {
            var dir = TempDir();
            try
            {
                var config = Path.Combine(dir, "config.json");
                File.WriteAllText(config, """
                    { "columns": { "Name": "string", "Age": "int", "Date": "datetime" },
                      "relationships": [ { "trigger": "Name", "reset": ["Age"] } ] }
                    """);
                using var db = new DataBall(config);
                db.AddRows(new[]
                {
                    Dict(("Name", "Alice"), ("Age", 30), ("Date", new DateTime(2020, 1, 2))),
                    Dict(("Name", "Bob")),
                });
                var rows = db.Query("SELECT Name, Age, \"Date\" FROM data ORDER BY rowid");
                Assert.Equal(2, rows.Count);
                Assert.Equal("Bob", rows[1]["Name"]);
                Assert.Null(rows[1]["Age"]);
                Assert.Null(rows[1]["Date"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static Dictionary<string, object?> Dict(params (string Key, object? Value)[] pairs)
        {
            var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in pairs)
                d[key] = value;
            return d;
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-batch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
