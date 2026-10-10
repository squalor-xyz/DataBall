// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.IO.Compression;
using squalor.DataBall.Export;
using squalor.DataBall.Import;
using Xunit;

namespace squalor.DataBall.Tests
{
    public class ManagerTests
    {
        [Fact]
        public void ImportFromArchive_ZipRoundTrip()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.zip");
                using (var db = Sample())
                    ExportManager.ExportToArchive(db, path);

                using var imported = new DataBall();
                ImportManager.ImportFromArchive(imported, path, append: false);
                AssertPeople(imported);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ImportFromBall_RoundTrip()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.ball");
                using (var db = Sample())
                    ExportManager.ExportToBall(db, path);

                using var imported = new DataBall();
                ImportManager.ImportFromBall(imported, path, append: false);
                AssertPeople(imported);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ImportFromArchive_NoCsv_Throws()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "empty.zip");
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                    zip.CreateEntry("readme.txt");

                using var db = new DataBall();
                var ex = Assert.Throws<DataBallException>(() =>
                    ImportManager.ImportFromArchive(db, path, append: false));
                Assert.Equal("Archive contains no CSV files", ex.Message);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ImportFromBall_LoadsMetadataOnly()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "meta-only.ball");
                using (var source = new DataBall(databasePath: path))
                    source.SetMetadata("Source", "ATE");

                using var db = new DataBall();
                ImportManager.ImportFromBall(db, path, append: false);
                Assert.Equal("ATE", db.Metadata["Source"]?.ToString());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ImportFromCsv_MissingFile_Throws()
        {
            using var db = new DataBall();
            var path = Path.Combine(Path.GetTempPath(), "databall-missing-" + Guid.NewGuid().ToString("N") + ".csv");
            var ex = Assert.Throws<DataBallException>(() => ImportManager.ImportFromCsv(db, path, append: false));
            Assert.Equal("Failed to import CSV file", ex.Message);
        }

        [Fact]
        public async Task ExportToParquet_RoundTrip()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.parquet");
                using (var db = Sample())
                    await ExportManager.ExportToParquet(db, path);

                using var imported = new DataBall();
                await ImportManager.ImportFromParquet(imported, path, append: false);
                AssertPeople(imported);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ExportToBall_RoundTrip()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.ball");
                using (var db = Sample())
                    ExportManager.ExportToBall(db, path);

                using var imported = new DataBall();
                ImportManager.ImportFromBall(imported, path, append: false);
                AssertPeople(imported);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ExportToArchive_ZipRoundTrip()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.zip");
                using (var db = Sample())
                    ExportManager.ExportToArchive(db, path);

                using var imported = new DataBall();
                ImportManager.ImportFromArchive(imported, path, append: false);
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
                await ExportManager.ExportToPartitionedParquet(db, outDir, new[] { "Name" });
                await AssertHiveParquetImported(outDir, "Name=Alice", "Name", "Alice");
                await AssertHiveParquetImported(outDir, "Name=Bob", "Name", "Bob");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Roll_Csv_WritesFile()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.csv");
                using var db = Sample();
                ExportManager.Roll(db, ExportType.Csv, path);
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
                ExportManager.Roll(db, ExportType.Parquet, path);
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
