using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using squalor.DataBall.Export;
using squalor.DataBall.Import;
using Xunit;

namespace squalor.DataBall.Tests
{
    public class ImportExportTests
    {
        [Fact]
        public async Task Csv_RoundTrip_ViaImportExportAsync()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.csv");
                using (var db = Sample())
                    await db.ExportAsync(path, ExportType.Csv);

                using var imported = new DataBall();
                await imported.ImportAsync(path);
                AssertPeople(imported);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Parquet_RoundTrip_ViaImportExportAsync()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.parquet");
                using (var db = Sample())
                    await db.ExportAsync(path, ExportType.Parquet);

                using var imported = new DataBall();
                await imported.ImportAsync(path);
                AssertPeople(imported);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Ball_RoundTrip_IncludesMetadata_AndZipEntries()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.ball");
                using (var db = Sample())
                {
                    db.SetMetadata("Operator", "Ada");
                    await db.ExportAsync(path, ExportType.Ball);
                }

                using (var zip = ZipFile.OpenRead(path))
                {
                    Assert.Contains(zip.Entries, e => EntryName(e) == "data.parquet");
                    Assert.Contains(zip.Entries, e => EntryName(e) == "metadata.json");
                }

                using var imported = new DataBall();
                await imported.ImportAsync(path);
                AssertPeople(imported);
                Assert.Equal("Ada", imported.Metadata["Operator"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Ball_OptionalConfigJson()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.ball");
                using (var db = Sample())
                    await db.ExportAsync(path, ExportType.Ball);

                using (var zip = ZipFile.OpenRead(path))
                {
                    var configEntry = Assert.Single(zip.Entries, e => EntryName(e) == "config.json");
                    using var stream = configEntry.Open();
                    using var reader = new StreamReader(stream);
                    var config = JsonSerializer.Deserialize<Config>(reader.ReadToEnd());
                    Assert.NotNull(config);
                    Assert.Empty(config.Metadata);
                    Assert.Equal("int", config.Columns["Age"]);
                    Assert.Equal("string", config.Columns["Name"]);
                }

                using var imported = new DataBall();
                await imported.ImportAsync(path);
                AssertPeople(imported);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Ball_MetadataOnly_NoParquet_RoundTrips()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "meta.ball");
                using (var db = new DataBall())
                {
                    db.SetMetadata("Site", "A");
                    await db.SaveAsync(path);
                }

                using (var zip = ZipFile.OpenRead(path))
                {
                    Assert.Contains(zip.Entries, e => EntryName(e) == "metadata.json");
                    Assert.DoesNotContain(zip.Entries, e => EntryName(e) == "data.parquet");
                }

                using var imported = new DataBall();
                await imported.ImportAsync(path);
                Assert.Equal("A", imported.Metadata["Site"]);
                var exists = imported.Query("""
                    SELECT COUNT(*) AS c FROM information_schema.tables
                    WHERE table_schema = 'main' AND table_name = 'data'
                    """);
                Assert.Equal(0, Convert.ToInt64(exists[0]["c"]));
                var columns = imported.Query("""
                    SELECT column_name FROM information_schema.columns
                    WHERE table_schema = 'main' AND table_name = 'data'
                    """);
                Assert.Empty(columns);
                Assert.DoesNotContain(columns, r => Convert.ToString(r["column_name"]) == "_");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Ball_ImportMissingMetadataJson_StillLoadsParquet()
        {
            var dir = TempDir();
            try
            {
                var parquet = Path.Combine(dir, "data.parquet");
                using (var db = Sample())
                    await db.ExportAsync(parquet, ExportType.Parquet);

                var path = Path.Combine(dir, "no-meta.ball");
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                    zip.CreateEntryFromFile(parquet, "data.parquet");

                using var imported = new DataBall();
                await imported.ImportAsync(path);
                AssertPeople(imported);
                Assert.Empty(imported.Metadata);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Append_TrueConcatenates_FalseReplaces()
        {
            var dir = TempDir();
            try
            {
                var first = Path.Combine(dir, "a.csv");
                var second = Path.Combine(dir, "b.csv");
                File.WriteAllText(first, "Name,Age\nAlice,30\n");
                File.WriteAllText(second, "Name,Age\nBob,25\n");

                using var db = new DataBall();
                await db.ImportAsync(first);
                await db.ImportAsync(second, new ImportOptions { Append = true });
                var rows = db.Query("SELECT \"Name\", \"Age\" FROM \"data\" ORDER BY \"Name\"");
                Assert.Equal(2, rows.Count);
                Assert.Equal("Alice", rows[0]["Name"]);
                Assert.Equal("Bob", rows[1]["Name"]);

                await db.ImportAsync(second, new ImportOptions { Append = false });
                rows = db.Query("SELECT \"Name\", \"Age\" FROM \"data\"");
                Assert.Single(rows);
                Assert.Equal("Bob", rows[0]["Name"]);
                Assert.Equal(25, Convert.ToInt32(rows[0]["Age"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Archive_Zip_RoundTrip()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.zip");
                using (var db = Sample())
                    await db.ExportAsync(path, ExportType.Archive);

                using var imported = new DataBall();
                await imported.ImportAsync(path);
                AssertPeople(imported);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Archive_TarGz_RoundTrip()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.tar.gz");
                using (var db = Sample())
                    await db.ExportAsync(path, ExportType.Archive);

                using var imported = new DataBall();
                await imported.ImportAsync(path);
                AssertPeople(imported);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Archive_TarXz_Import_FromHandBuiltFile()
        {
            if (!XzAvailable())
                return;

            var dir = TempDir();
            try
            {
                var tar = Path.Combine(dir, "people.tar");
                using (var db = Sample())
                    await db.ExportAsync(tar, ExportType.Archive);

                var xz = Path.Combine(dir, "people.tar.xz");
                CompressWithXz(tar, xz);

                using var imported = new DataBall();
                await imported.ImportAsync(xz);
                AssertPeople(imported);

                var txz = Path.Combine(dir, "people.txz");
                File.Copy(xz, txz);
                using var importedTxz = new DataBall();
                await importedTxz.ImportAsync(txz);
                AssertPeople(importedTxz);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Archive_TarXz_Export_ThrowsClearDataBallException()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.tar.xz");
                using var db = Sample();
                var ex = await Assert.ThrowsAsync<DataBallException>(() => db.ExportAsync(path, ExportType.Archive));
                Assert.Contains("tar.xz", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("not supported", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("InvalidFormatException", ex.ToString(), StringComparison.Ordinal);
                Assert.False(File.Exists(path));

                var txz = Path.Combine(dir, "people.txz");
                var exTxz = await Assert.ThrowsAsync<DataBallException>(() => db.ExportAsync(txz, ExportType.Archive));
                Assert.Contains("txz", exTxz.Message, StringComparison.OrdinalIgnoreCase);
                Assert.False(File.Exists(txz));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Archive_Zip_MultipleCsv_Concatenates()
        {
            var dir = TempDir();
            try
            {
                var a = Path.Combine(dir, "a.csv");
                var b = Path.Combine(dir, "b.csv");
                File.WriteAllText(a, "Name,Age\nAlice,30\n");
                File.WriteAllText(b, "Name,Age\nBob,25\n");
                var path = Path.Combine(dir, "multi.zip");
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    zip.CreateEntryFromFile(a, "a.csv");
                    zip.CreateEntryFromFile(b, "b.csv");
                }

                using var imported = new DataBall();
                await imported.ImportAsync(path);
                var rows = imported.Query("SELECT \"Name\", \"Age\" FROM \"data\" ORDER BY \"Name\"");
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
        public async Task UnknownExtension_Throws()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.xyz");
                File.WriteAllText(path, "nope");
                using var db = new DataBall();
                await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(path));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task MissingFile_Throws()
        {
            using var db = new DataBall();
            await Assert.ThrowsAsync<DataBallException>(() =>
                db.ImportAsync(Path.Combine(Path.GetTempPath(), "databall-missing-" + Guid.NewGuid().ToString("N") + ".csv")));
        }

        [Fact]
        public async Task Save_Sync_WritesBall()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.ball");
                using (var db = Sample())
                    db.Save(path);

                Assert.True(File.Exists(path));
                using (var zip = ZipFile.OpenRead(path))
                {
                    Assert.Contains(zip.Entries, e => EntryName(e) == "data.parquet");
                    Assert.Contains(zip.Entries, e => EntryName(e) == "metadata.json");
                }

                using var imported = new DataBall();
                await imported.ImportAsync(path);
                AssertPeople(imported);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Roll_Csv_Works()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "people.csv");
                using (var db = Sample())
                    db.Roll(ExportType.Csv, path);

                using var imported = new DataBall();
                await imported.ImportAsync(path);
                AssertPeople(imported);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Export_MissingDataTable_Throws()
        {
            var dir = TempDir();
            try
            {
                using var db = new DataBall();
                Assert.Throws<DataBallException>(() => db.Roll(ExportType.Csv, Path.Combine(dir, "empty.csv")));
                await Assert.ThrowsAsync<DataBallException>(() => db.ExportAsync(Path.Combine(dir, "empty.parquet"), ExportType.Parquet));
                Assert.Throws<DataBallException>(() => db.Save(Path.Combine(dir, "empty.ball")));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportManager_CsvAndParquet_StillWork()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "people.csv");
                var parquet = Path.Combine(dir, "people.parquet");
                using (var db = Sample())
                {
                    await db.ExportAsync(csv, ExportType.Csv);
                    await ExportManager.ExportToParquet(db, parquet);
                }

                using var fromCsv = new DataBall();
                ImportManager.ImportFromCsv(fromCsv, csv, append: false);
                AssertPeople(fromCsv);

                using var fromParquet = new DataBall();
                await ImportManager.ImportFromParquet(fromParquet, parquet, append: false);
                AssertPeople(fromParquet);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportAsync_HiveDir_RoundTripSquishSiteMeas()
        {
            var dir = TempDir();
            try
            {
                var hive = Path.Combine(dir, "hive");
                using (var db = new DataBall())
                {
                    db.AddColumn("Site", new[] { "Lab1", "Lab1", "Lab2" });
                    db.AddColumn<int>("Meas", new[] { 1, 2, 3 });
                    await db.Squish(hive, new[] { "Site" });
                }

                using var imported = new DataBall();
                await imported.ImportAsync(hive);
                var rows = imported.Query("SELECT \"Site\", \"Meas\" FROM \"data\" ORDER BY \"Meas\"");
                Assert.Equal(3, rows.Count);
                Assert.Equal("Lab1", rows[0]["Site"]);
                Assert.Equal(1, Convert.ToInt32(rows[0]["Meas"]));
                Assert.IsType<int>(rows[0]["Meas"]);
                Assert.Equal("Lab2", rows[2]["Site"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportAsync_HiveDir_TwoPartitionColumns()
        {
            var dir = TempDir();
            try
            {
                var hive = Path.Combine(dir, "hive");
                using (var db = new DataBall())
                {
                    db.AddColumn("Site", new[] { "A", "A", "B" });
                    db.AddColumn("Lot", new[] { "1", "2", "1" });
                    db.AddColumn<int>("Meas", new[] { 10, 20, 30 });
                    await db.Squish(hive, new[] { "Site", "Lot" });
                }

                using var imported = new DataBall();
                await imported.ImportAsync(hive);
                var rows = imported.Query("SELECT \"Site\", \"Lot\", \"Meas\" FROM \"data\" ORDER BY \"Meas\"");
                Assert.Equal(3, rows.Count);
                Assert.Equal("A", rows[0]["Site"]);
                Assert.Equal("1", rows[0]["Lot"]);
                Assert.Equal(10, Convert.ToInt32(rows[0]["Meas"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportAsync_HiveDir_OnlyPartitionColumn()
        {
            var dir = TempDir();
            try
            {
                var hive = Path.Combine(dir, "hive");
                using (var db = new DataBall())
                {
                    db.AddColumn("Site", new[] { "Lab1", "Lab2" });
                    await db.Squish(hive, new[] { "Site" });
                }

                using var imported = new DataBall();
                await imported.ImportAsync(hive);
                var rows = imported.Query("SELECT \"Site\" FROM \"data\" ORDER BY \"Site\"");
                Assert.Equal(2, rows.Count);
                Assert.Equal("Lab1", rows[0]["Site"]);
                Assert.Equal("Lab2", rows[1]["Site"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportAsync_HiveDir_AppendTrue_Concatenates()
        {
            var dir = TempDir();
            try
            {
                var hive = Path.Combine(dir, "hive");
                using (var db = new DataBall())
                {
                    db.AddColumn("Site", new[] { "Lab1" });
                    db.AddColumn<int>("Meas", new[] { 1 });
                    await db.Squish(hive, new[] { "Site" });
                }

                using var imported = new DataBall();
                await imported.ImportAsync(hive);
                await imported.ImportAsync(hive, new ImportOptions { Append = true });
                Assert.Equal(2, imported.Query("SELECT * FROM \"data\"").Count);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportAsync_HiveDir_AppendFalse_Replaces()
        {
            var dir = TempDir();
            try
            {
                var hive = Path.Combine(dir, "hive");
                using (var db = new DataBall())
                {
                    db.AddColumn("Site", new[] { "Lab1" });
                    db.AddColumn<int>("Meas", new[] { 1 });
                    await db.Squish(hive, new[] { "Site" });
                }

                using var imported = new DataBall();
                await imported.ImportAsync(hive);
                await imported.ImportAsync(hive, new ImportOptions { Append = false });
                Assert.Single(imported.Query("SELECT * FROM \"data\""));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportAsync_EmptyDir_Throws()
        {
            var dir = TempDir();
            try
            {
                using var db = new DataBall();
                await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(dir));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportAsync_DirWithNoParquet_Throws()
        {
            var dir = TempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "notes.txt"), "no parquet");
                using var db = new DataBall();
                await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(dir));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Import_CaseInsensitiveExtensions()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "people.CSV");
                using (var db = Sample())
                    await db.ExportAsync(Path.Combine(dir, "people.csv"), ExportType.Csv);
                File.Move(Path.Combine(dir, "people.csv"), csv);

                using var imported = new DataBall();
                await imported.ImportAsync(csv);
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

        private static string EntryName(ZipArchiveEntry entry)
        {
            return entry.FullName.Replace('\\', '/');
        }

        private static bool XzAvailable()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "xz",
                    ArgumentList = { "--version" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var proc = Process.Start(psi);
                if (proc is null)
                    return false;
                proc.WaitForExit(2000);
                return proc.ExitCode == 0;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
            {
                return false;
            }
        }

        private static void CompressWithXz(string tarPath, string xzPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "xz",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(tarPath);
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("xz is required to build the .tar.xz import fixture");
            using (var fs = File.Create(xzPath))
                proc.StandardOutput.BaseStream.CopyTo(fs);
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(proc.StandardError.ReadToEnd());
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-pr4", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
