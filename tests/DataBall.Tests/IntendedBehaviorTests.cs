// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using squalor.DataBall.Export;
using squalor.DataBall.Import;
using Xunit;

namespace squalor.DataBall.Tests
{
    /// <summary>
    /// Contract tests for documented behavior. A failure is a product bug, not a test bug.
    /// </summary>
    public class IntendedBehaviorTests
    {
        [Fact]
        public void AddColumn_Int_QueryReturnsInt32()
        {
            using var db = new DataBall();
            db.AddColumn<int>("Age", new[] { 30, 25 });
            var rows = db.Query("SELECT Age FROM data ORDER BY Age DESC");
            Assert.Equal(30, Assert.IsType<int>(rows[0]["Age"]));
            Assert.Equal(25, Assert.IsType<int>(rows[1]["Age"]));
        }

        [Fact]
        public void Query_ReadmeSnippet_UnquotedFromData()
        {
            using var db = new DataBall();
            db.AddColumn("Name", new[] { "Alice", "Bob" });
            db.AddColumn<int>("Age", new[] { 30, 25 });
            var rows = db.Query("SELECT Name, Age FROM data ORDER BY Name");
            Assert.Equal(2, rows.Count);
            Assert.Equal("Alice", rows[0]["Name"]);
            Assert.Equal(30, Assert.IsType<int>(rows[0]["Age"]));
            Assert.Equal("Bob", rows[1]["Name"]);
            Assert.Equal(25, Assert.IsType<int>(rows[1]["Age"]));
        }

        [Fact]
        public void Metadata_ReturnedDictionary_DoesNotMutateStore()
        {
            using var db = new DataBall();
            db.SetMetadata("Source", "ATE");
            var snapshot = Assert.IsAssignableFrom<IDictionary<string, object?>>(db.Metadata);
            snapshot["Source"] = "mutated";
            snapshot["Extra"] = "nope";
            Assert.Equal("ATE", db.Metadata["Source"]);
            Assert.False(db.Metadata.ContainsKey("Extra"));
        }

        // Config Age=int must stay INTEGER after CSV import, not DuckDB's inferred BIGINT.
        [Fact]
        public void CsvImport_WithConfigAgeInt_ValuesAreInt32()
        {
            var dir = TempDir();
            try
            {
                var config = Path.Combine(dir, "config.json");
                File.WriteAllText(config, """{ "columns": { "Name": "string", "Age": "int" } }""");
                var csv = Path.Combine(dir, "people.csv");
                File.WriteAllText(csv, "Name,Age\nAlice,30\nBob,25\n");

                using var db = new DataBall(config);
                ImportManager.ImportFromCsv(db, csv, append: false);
                var rows = db.Query("SELECT Name, Age FROM data ORDER BY Name");
                Assert.Equal(2, rows.Count);
                Assert.Equal("Alice", rows[0]["Name"]);
                Assert.Equal(30, Assert.IsType<int>(rows[0]["Age"]));
                Assert.Equal("Bob", rows[1]["Name"]);
                Assert.Equal(25, Assert.IsType<int>(rows[1]["Age"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void CsvImport_WithConfigBoolFloatDateTime_ValuesAreExactClrTypes()
        {
            var dir = TempDir();
            try
            {
                var config = Path.Combine(dir, "config.json");
                File.WriteAllText(config, """{ "columns": { "Flag": "bool", "Score": "float", "When": "datetime" } }""");
                var csv = Path.Combine(dir, "typed.csv");
                File.WriteAllText(csv, "Flag,Score,When\ntrue,1.25,2020-05-06 07:08:09\n");

                using var db = new DataBall(config);
                ImportManager.ImportFromCsv(db, csv, append: false);
                var row = Assert.Single(db.Query("SELECT Flag, Score, \"When\" FROM data"));
                Assert.True(Assert.IsType<bool>(row["Flag"]));
                Assert.Equal(1.25f, Assert.IsType<float>(row["Score"]));
                var when = Assert.IsType<DateTime>(row["When"]);
                Assert.Equal(new DateTime(2020, 5, 6, 7, 8, 9), when);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportAsync_CsvWithConfigAgeInt_ValuesAreInt32()
        {
            var dir = TempDir();
            try
            {
                var config = Path.Combine(dir, "config.json");
                File.WriteAllText(config, """{ "columns": { "Name": "string", "Age": "int" } }""");
                var csv = Path.Combine(dir, "people.csv");
                File.WriteAllText(csv, "Name,Age\nAlice,30\nBob,25\n");

                using var db = new DataBall(config);
                await db.ImportAsync(csv);
                var rows = db.Query("SELECT Name, Age FROM data ORDER BY Name");
                Assert.Equal(2, rows.Count);
                Assert.Equal("Alice", rows[0]["Name"]);
                Assert.Equal(30, Assert.IsType<int>(rows[0]["Age"]));
                Assert.Equal("Bob", rows[1]["Name"]);
                Assert.Equal(25, Assert.IsType<int>(rows[1]["Age"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ArchiveImport_ZipCsvWithConfigAgeInt_ValuesAreInt32()
        {
            var dir = TempDir();
            try
            {
                var config = Path.Combine(dir, "config.json");
                File.WriteAllText(config, """{ "columns": { "Name": "string", "Age": "int" } }""");
                var csv = Path.Combine(dir, "people.csv");
                File.WriteAllText(csv, "Name,Age\nAlice,30\nBob,25\n");
                var zip = Path.Combine(dir, "people.zip");
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                    archive.CreateEntryFromFile(csv, "people.csv");

                using var db = new DataBall(config);
                ImportManager.ImportFromArchive(db, zip, append: false);
                var rows = db.Query("SELECT Name, Age FROM data ORDER BY Name");
                Assert.Equal(2, rows.Count);
                Assert.Equal("Alice", rows[0]["Name"]);
                Assert.Equal(30, Assert.IsType<int>(rows[0]["Age"]));
                Assert.Equal("Bob", rows[1]["Name"]);
                Assert.Equal(25, Assert.IsType<int>(rows[1]["Age"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ImportFromCsv_AppendFalse_ReplacesRows()
        {
            var dir = TempDir();
            try
            {
                var first = Path.Combine(dir, "a.csv");
                var second = Path.Combine(dir, "b.csv");
                File.WriteAllText(first, "Name\nAlice\n");
                File.WriteAllText(second, "Name\nBob\n");
                using var db = new DataBall();
                ImportManager.ImportFromCsv(db, first, append: false);
                ImportManager.ImportFromCsv(db, second, append: false);
                var rows = db.Query("SELECT Name FROM data");
                var names = rows.Select(r => r["Name"]?.ToString()).ToArray();
                Assert.Equal(new[] { "Bob" }, names);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ImportFromCsv_AppendTrue_Concatenates()
        {
            var dir = TempDir();
            try
            {
                var first = Path.Combine(dir, "a.csv");
                var second = Path.Combine(dir, "b.csv");
                File.WriteAllText(first, "Name\nAlice\n");
                File.WriteAllText(second, "Name\nBob\n");
                using var db = new DataBall();
                ImportManager.ImportFromCsv(db, first, append: false);
                ImportManager.ImportFromCsv(db, second, append: true);
                var rows = db.Query("SELECT Name FROM data ORDER BY Name");
                var names = rows.Select(r => r["Name"]?.ToString()).ToArray();
                Assert.Equal(new[] { "Alice", "Bob" }, names);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void CsvImport_WithoutConfig_AgeValuesAreInt64()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "people.csv");
                File.WriteAllText(csv, "Name,Age\nAlice,30\nBob,25\n");
                using var db = new DataBall();
                ImportManager.ImportFromCsv(db, csv, append: false);
                var rows = db.Query("SELECT Name, Age FROM data ORDER BY Name");
                Assert.Equal(2, rows.Count);
                Assert.Equal(30L, Assert.IsType<long>(rows[0]["Age"]));
                Assert.Equal(25L, Assert.IsType<long>(rows[1]["Age"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportAsync_CsvWithoutConfig_AgeValuesAreInt64()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "people.csv");
                File.WriteAllText(csv, "Name,Age\nAlice,30\nBob,25\n");
                using var db = new DataBall();
                await db.ImportAsync(csv);
                var rows = db.Query("SELECT Name, Age FROM data ORDER BY Name");
                Assert.Equal(2, rows.Count);
                Assert.Equal(30L, Assert.IsType<long>(rows[0]["Age"]));
                Assert.Equal(25L, Assert.IsType<long>(rows[1]["Age"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void CsvImport_WithConfigAgeLong_SmallValuesStayInt64()
        {
            var dir = TempDir();
            try
            {
                var config = Path.Combine(dir, "config.json");
                File.WriteAllText(config, """{ "columns": { "Name": "string", "Age": "long" } }""");
                var csv = Path.Combine(dir, "people.csv");
                File.WriteAllText(csv, "Name,Age\nAlice,30\nBob,25\n");
                using var db = new DataBall(config);
                ImportManager.ImportFromCsv(db, csv, append: false);
                var rows = db.Query("SELECT Name, Age FROM data ORDER BY Name");
                Assert.Equal(2, rows.Count);
                Assert.Equal(30L, Assert.IsType<long>(rows[0]["Age"]));
                Assert.Equal(25L, Assert.IsType<long>(rows[1]["Age"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void CsvImport_PartialConfig_UntypedAgeStaysInt64()
        {
            var dir = TempDir();
            try
            {
                var config = Path.Combine(dir, "config.json");
                File.WriteAllText(config, """{ "columns": { "Name": "string" } }""");
                var csv = Path.Combine(dir, "people.csv");
                File.WriteAllText(csv, "Name,Age\nAlice,30\nBob,25\n");
                using var db = new DataBall(config);
                ImportManager.ImportFromCsv(db, csv, append: false);
                var rows = db.Query("SELECT Name, Age FROM data ORDER BY Name");
                Assert.Equal(2, rows.Count);
                Assert.Equal("Alice", rows[0]["Name"]);
                Assert.Equal(30L, Assert.IsType<long>(rows[0]["Age"]));
                Assert.Equal(25L, Assert.IsType<long>(rows[1]["Age"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ParquetRoundTrip_AgeRemainsInt32()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.parquet");
                using (var db = new DataBall())
                {
                    db.AddColumn("Name", new[] { "Alice" });
                    db.AddColumn<int>("Age", new[] { 30 });
                    await db.ExportAsync(path, ExportType.Parquet);
                }

                using var imported = new DataBall();
                await imported.ImportAsync(path);
                var row = Assert.Single(imported.Query("SELECT Name, Age FROM data"));
                Assert.Equal("Alice", row["Name"]);
                Assert.Equal(30, Assert.IsType<int>(row["Age"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task BallRoundTrip_AgeRemainsInt32()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.ball");
                using (var db = new DataBall())
                {
                    db.AddColumn("Name", new[] { "Alice" });
                    db.AddColumn<int>("Age", new[] { 30 });
                    db.SetMetadata("Source", "ATE");
                    await db.SaveAsync(path);
                }

                using var imported = new DataBall();
                await imported.ImportAsync(path);
                Assert.Equal("ATE", imported.Metadata["Source"]);
                var row = Assert.Single(imported.Query("SELECT Name, Age FROM data"));
                Assert.Equal("Alice", row["Name"]);
                Assert.Equal(30, Assert.IsType<int>(row["Age"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ExportCsv_HasHeaderAndDataRows()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.csv");
                using var db = new DataBall();
                db.AddColumn("Name", new[] { "Alice", "Bob" });
                db.AddColumn<int>("Age", new[] { 30, 25 });
                db.Roll(ExportType.Csv, path);

                var lines = File.ReadAllLines(path);
                Assert.Equal("Name,Age", lines[0]);
                Assert.Contains("Alice,30", lines);
                Assert.Contains("Bob,25", lines);
                Assert.Equal(3, lines.Length);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ExportToArchive_ZipContainsDataCsvWithRows()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.zip");
                using var db = new DataBall();
                db.AddColumn("Name", new[] { "Alice" });
                db.AddColumn<int>("Age", new[] { 30 });
                db.ExportToArchive(path);

                using var zip = ZipFile.OpenRead(path);
                var entry = Assert.Single(zip.Entries, e => e.FullName.Replace('\\', '/') == "data.csv");
                using var reader = new StreamReader(entry.Open());
                var csv = reader.ReadToEnd().Replace("\r\n", "\n");
                Assert.StartsWith("Name,Age\n", csv, StringComparison.Ordinal);
                Assert.Contains("Alice,30", csv, StringComparison.Ordinal);
                var dataLines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(2, dataLines.Length);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportAsync_DefaultDoesNotAppend()
        {
            var dir = TempDir();
            try
            {
                var first = Path.Combine(dir, "a.csv");
                var second = Path.Combine(dir, "b.csv");
                File.WriteAllText(first, "Name\nAlice\n");
                File.WriteAllText(second, "Name\nBob\n");
                using var db = new DataBall();
                await db.ImportAsync(first);
                await db.ImportAsync(second);
                var rows = db.Query("SELECT Name FROM data");
                var names = rows.Select(r => r["Name"]?.ToString()).ToArray();
                Assert.Equal(new[] { "Bob" }, names);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task HiveParquet_PartitionFilesAreImportableWithPartitionColumn()
        {
            var dir = TempDir();
            try
            {
                var hive = Path.Combine(dir, "parts");
                using (var db = new DataBall())
                {
                    db.AddColumn("Site", new[] { "Lab1", "Lab2" });
                    db.AddColumn("Name", new[] { "Alice", "Bob" });
                    await db.Squish(hive, new[] { "Site" });
                }

                var parts = Directory.GetDirectories(hive);
                Assert.Equal(2, parts.Length);
                Assert.Contains(parts, p => Path.GetFileName(p) == "Site=Lab1");
                Assert.Contains(parts, p => Path.GetFileName(p) == "Site=Lab2");
                var lab1 = Directory.GetFiles(Path.Combine(hive, "Site=Lab1"), "*.parquet", SearchOption.AllDirectories);
                Assert.NotEmpty(lab1);
                using var imported = new DataBall();
                await imported.ImportAsync(lab1[0]);
                var row = Assert.Single(imported.Query("SELECT Site, Name FROM data"));
                Assert.Equal("Lab1", row["Site"]?.ToString());
                Assert.Equal("Alice", row["Name"]?.ToString());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Bounce_DistinctRemainingIntColumn()
        {
            using var db = new DataBall();
            db.AddColumn("Site", new[] { "A", "A", "A" });
            db.AddColumn<int>("Meas", new[] { 1, 2, 1 });
            await db.Bounce();
            Assert.Equal("A", db.Metadata["Site"]);
            var rows = db.Query("SELECT Meas FROM data ORDER BY Meas");
            Assert.Equal(2, rows.Count);
            Assert.Equal(1, Assert.IsType<int>(rows[0]["Meas"]));
            Assert.Equal(2, Assert.IsType<int>(rows[1]["Meas"]));
        }

        [Fact]
        public void Roll_IsExport_NotRowCommit()
        {
            var dir = TempDir();
            try
            {
                using var db = new DataBall();
                db.AddColumn("Name", new[] { "Alice" });
                db.InitializeRow();
                db.ModifyField("Name", "Bob");
                var path = Path.Combine(dir, "out.csv");
                var ex = Assert.Throws<DataBallException>(() => db.Roll(ExportType.Csv, path));
                Assert.Contains("pending row", ex.Message, StringComparison.OrdinalIgnoreCase);
                var rows = db.Query("SELECT Name FROM data");
                Assert.Single(rows);
                Assert.Equal("Alice", rows[0]["Name"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void CommitRow_ResetsDependentToNull_NotDefaultInt()
        {
            var dir = TempDir();
            try
            {
                var config = Path.Combine(dir, "config.json");
                File.WriteAllText(config, """
                    {
                      "columns": { "Name": "string", "Age": "int" },
                      "relationships": [ { "trigger": "Name", "reset": ["Age"] } ]
                    }
                    """);
                using var db = new DataBall(config);
                db.InitializeRow(new Dictionary<string, object?> { ["Name"] = "Alice", ["Age"] = 30 });
                db.CommitRow();
                db.InitializeRow();
                db.ModifyField("Name", "Bob");
                db.CommitRow();
                var rows = db.Query("SELECT Name, Age FROM data ORDER BY Name");
                Assert.Equal("Alice", rows[0]["Name"]);
                Assert.Equal(30, Assert.IsType<int>(rows[0]["Age"]));
                Assert.Equal("Bob", rows[1]["Name"]);
                Assert.Null(rows[1]["Age"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-intended", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
