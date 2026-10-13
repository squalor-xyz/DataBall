// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.IO.Compression;
using squalor.DataBall.Import;
using Xunit;

namespace squalor.DataBall.Tests
{
    public class ManagerTests
    {
        [Fact]
        public async Task ImportFromArchive_ZipRoundTrip()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.zip");
                using (var db = Sample())
                    await db.ExportAsync(path, new ExportOptions { Format = ExportType.Archive });

                using var imported = new DataBall();
                await imported.ImportAsync(path, new ImportOptions { Append = false });
                AssertPeople(imported);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportFromBall_RoundTrip()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.ball");
                using (var db = Sample())
                    await db.SaveAsync(path);

                using var imported = new DataBall();
                await imported.ImportAsync(path, new ImportOptions { Append = false });
                AssertPeople(imported);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportFromArchive_NoCsv_Throws()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "empty.zip");
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                    zip.CreateEntry("readme.txt");

                using var db = new DataBall();
                var ex = await Assert.ThrowsAsync<DataBallException>(() =>
                    db.ImportAsync(path, new ImportOptions { Append = false }));
                Assert.Equal("Archive contains no CSV files", ex.Message);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportFromBall_LoadsMetadataOnly()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "meta-only.ball");
                using (var source = new DataBall(databasePath: path))
                    source.SetMetadata("Source", "ATE");

                using var db = new DataBall();
                await db.ImportAsync(path, new ImportOptions { Append = false });
                Assert.Equal("ATE", db.Metadata["Source"]?.ToString());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportFromCsv_MissingFile_Throws()
        {
            using var db = new DataBall();
            var path = Path.Combine(Path.GetTempPath(), "databall-missing-" + Guid.NewGuid().ToString("N") + ".csv");
            var ex = await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(path, new ImportOptions { Append = false }));
            // The removed ImportManager.ImportFromCsv rewrapped this as "Failed to import CSV file"; ImportAsync reports the missing file itself.
            Assert.StartsWith("File not found:", ex.Message, StringComparison.Ordinal);
            Assert.Contains(path, ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ExportToParquet_RoundTrip()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.parquet");
                using (var db = Sample())
                    await db.ExportAsync(path, new ExportOptions { Format = ExportType.Parquet });

                using var imported = new DataBall();
                await imported.ImportAsync(path, new ImportOptions { Append = false });
                AssertPeople(imported);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ExportToBall_RoundTrip()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.ball");
                using (var db = Sample())
                    await db.SaveAsync(path);

                using var imported = new DataBall();
                await imported.ImportAsync(path, new ImportOptions { Append = false });
                AssertPeople(imported);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ExportToArchive_ZipRoundTrip()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.zip");
                using (var db = Sample())
                    await db.ExportAsync(path, new ExportOptions { Format = ExportType.Archive });

                using var imported = new DataBall();
                await imported.ImportAsync(path, new ImportOptions { Append = false });
                AssertPeople(imported);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ExportToPartitionedParquet_WritesHiveDirs()
        {
            var dir = TempDir();
            try
            {
                var outDir = Path.Combine(dir, "parts");
                using var db = Sample();
                await db.ExportAsync(outDir, new ExportOptions { Format = ExportType.Parquet, PartitionBy = new[] { "Name" } });
                await AssertHiveParquetImported(outDir, "Name=Alice", "Name", "Alice");
                await AssertHiveParquetImported(outDir, "Name=Bob", "Name", "Bob");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Roll_Csv_WritesFile()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.csv");
                using var db = Sample();
                await db.ExportAsync(path, new ExportOptions { Format = ExportType.Csv });
                var text = File.ReadAllText(path).Replace("\r\n", "\n");
                Assert.Contains("Name,Age", text, StringComparison.Ordinal);
                Assert.Contains("Alice,30", text, StringComparison.Ordinal);
                Assert.Contains("Bob,25", text, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Roll_Parquet_WritesFile()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.parquet");
                using var db = Sample();
                await db.ExportAsync(path, new ExportOptions { Format = ExportType.Parquet });
                using var imported = new DataBall();
                await imported.ImportAsync(path);
                AssertPeople(imported);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static DataBall Sample()
        {
            var db = new DataBall();
            db.AddColumn("Name", new[] { "Alice", "Bob" });
            db.AddColumn<int>("Age", new[] { 30, 25 });
            return db;
        }

        private static void AssertPeople(DataBall db)
        {
            var rows = db.Query("SELECT \"Name\", \"Age\" FROM \"data\" ORDER BY \"Name\"");
            Assert.Equal(2, rows.Count);
            Assert.Equal("Alice", rows[0]["Name"]);
            Assert.Equal(30, Convert.ToInt32(rows[0]["Age"]));
            Assert.Equal("Bob", rows[1]["Name"]);
            Assert.Equal(25, Convert.ToInt32(rows[1]["Age"]));
        }

        private static async Task AssertHiveParquetImported(string root, string hiveDir, string column, string value)
        {
            var path = Path.Combine(root, hiveDir);
            var files = Directory.GetFiles(path, "*.parquet", SearchOption.AllDirectories);
            Assert.NotEmpty(files);
            using var imported = new DataBall();
            await imported.ImportAsync(files[0]);
            var row = Assert.Single(imported.Query($"SELECT \"{column}\" FROM \"data\""));
            Assert.Equal(value, row[column]?.ToString());
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-t2-manager", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
