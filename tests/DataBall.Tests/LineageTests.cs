// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using squalor.DataBall.Export;
using squalor.DataBall.Import;
using Xunit;

namespace squalor.DataBall.Tests
{
    /// <summary>
    /// db-22: every file a session opens is recorded in the <c>source</c> table, and every row of
    /// <c>"data"</c> carries one <c>_source_key</c> that points at it.
    /// </summary>
    [Collection("DataBall.Handlers")]
    public class LineageTests
    {
        // The exact bytes of the fixture below. Size and hash were computed outside the product
        // with `wc -c` and `shasum -a 256`, never with DataBall.
        private const string FixtureCsv = "id,Gain(dB),Pin(dBm)\n1,20.5,-7.5\n2,21.5,-5.0\n3,22.5,-2.5\n";
        private const long FixtureBytes = 57;
        private const string FixtureSha256 = "ba8c82ea4141d4ef115f664e35384745d7668dcce144c9f0fca3d6c7f8fd7f6a";

        [Fact]
        public void Open_SingleCsv_SourceRowAndKey()
        {
            var dir = TempDir();
            try
            {
                var csv = WriteCsv(dir, "lineage.csv", FixtureCsv);
                var modified = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(csv, modified);
                var before = DateTime.UtcNow;
                using var db = DataBall.Open(csv);
                var after = DateTime.UtcNow;

                var source = Assert.Single(db.Sources);
                Assert.Equal(1L, source.Key);
                Assert.Equal(csv, source.Path);
                Assert.Equal("CsvImport", source.Type);
                Assert.Equal(FixtureBytes, source.Bytes);
                Assert.Equal(FixtureSha256, source.Sha256);
                Assert.Equal(modified, source.ModifiedUtc);
                Assert.Equal(DateTimeKind.Utc, source.AddedUtc.Kind);
                Assert.InRange(source.AddedUtc, before.AddTicks(-10), after.AddTicks(10));

                var rows = db.Query("SELECT id, _source_key FROM data ORDER BY id");
                Assert.Equal(3, rows.Count);
                Assert.All(rows, r => Assert.Equal(1L, Assert.IsType<long>(r["_source_key"])));

                // Same facts through SQL on the built-in table.
                var table = Assert.Single(db.Query("SELECT * FROM source"));
                Assert.Equal(new[] { "source_key", "path", "sha256", "type", "bytes", "modified_utc", "added_utc" }, table.Keys.ToArray());
                Assert.Equal(1L, table["source_key"]);
                Assert.Equal(csv, table["path"]);
                Assert.Equal(FixtureSha256, table["sha256"]);
                Assert.Equal("CsvImport", table["type"]);
                Assert.Equal(FixtureBytes, table["bytes"]);

                // The key is the last column of "data"; the rest is as before.
                Assert.Equal(new[] { "id", "Gain", "Pin", "_source_key" }, DataColumns(db));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_SingleCsv_FullPathRecordedForRelativeInput()
        {
            var dir = TempDir();
            try
            {
                var csv = WriteCsv(dir, "lineage.csv", FixtureCsv);
                var relative = Path.GetRelativePath(Environment.CurrentDirectory, csv);
                using var db = DataBall.Open(relative);
                Assert.Equal(Path.GetFullPath(relative), Assert.Single(db.Sources).Path);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_Csv_MetadataExtractionAndUnitHeaders_KeepSourceKey()
        {
            var dir = TempDir();
            try
            {
                var csv = WriteCsv(dir, "meta.csv", "id,Lot,Gain(dB)\n1,L7,20.5\n2,L7,21.5\n");
                using var db = DataBall.Open(csv);
                // Lot is a configured metadata field and leaves the table; the key is never one.
                Assert.Equal("L7", db.Metadata["Lot"]);
                Assert.False(db.Metadata.ContainsKey("_source_key"));
                Assert.Equal(new[] { "id", "Gain", "_source_key" }, DataColumns(db));
                Assert.Equal(2L, Convert.ToInt64(db.Query("SELECT count(*) AS n FROM data WHERE _source_key = 1")[0]["n"]));
                Assert.False(db.Schema.Columns.ContainsKey("_source_key"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_Csv_SourceKeyAsConfiguredMetadataField_IsStillAColumn()
        {
            var dir = TempDir();
            try
            {
                var csv = WriteCsv(dir, "a.csv", "id,Gain(dB)\n1,20.5\n");
                var config = Path.Combine(dir, "overlay.json");
                File.WriteAllText(config, """{ "metadataFields": ["_source_key", "Lot"] }""");
                using var db = DataBall.Open(csv, config);
                Assert.Equal(new[] { "id", "Gain", "_source_key" }, DataColumns(db));
                Assert.False(db.Metadata.ContainsKey("_source_key"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_Csv_WithReservedSourceKeyHeader_Throws()
        {
            var dir = TempDir();
            try
            {
                var csv = WriteCsv(dir, "bad.csv", "id,_source_key\n1,9\n");
                var ex = Assert.Throws<DataBallException>(() => DataBall.Open(csv));
                Assert.Equal("Column '_source_key' is reserved for source lineage; rename it in the input", ex.GetBaseException().Message);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_Csv_UnitNamedKey_DoesNotRenameTheSourceKey()
        {
            var dir = TempDir();
            try
            {
                // With a unit called "key", the header parser would split _source_key into _source + key.
                var csv = WriteCsv(dir, "a.csv", "id\n1\n");
                var config = Path.Combine(dir, "units.json");
                File.WriteAllText(config, """{ "units": { "key": { "type": "double" } } }""");
                using var db = DataBall.Open(csv, config);
                Assert.Equal(new[] { "id", "_source_key" }, DataColumns(db));
                Assert.Equal(1L, Assert.IsType<long>(db.Query("SELECT _source_key FROM data")[0]["_source_key"]));
                Assert.DoesNotContain(db.Schema.Columns.Keys, k => k.StartsWith("_source", StringComparison.Ordinal));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_LayoutSession_SpineCarriesKey()
        {
            var csv = Fixture("semiconductor-sweep.csv");
            using var db = DataBall.Open(csv, Fixture("semiconductor-sweep.tables.json"));

            Assert.Equal("VIEW", TableType(db, "data"));
            var wide = DataColumns(db);
            Assert.Equal("_source_key", wide[^1]);
            Assert.Equal(
                new[] { "stimulusGrp", "sweep", "SN", "Date", "Teststand", "Temp", "Vcc", "Frequency", "Pout", "Pin", "Gain", "I_Total", "EVM", "_source_key" },
                wide);
            Assert.Equal(81L, Convert.ToInt64(db.Query("SELECT count(*) AS n FROM data WHERE _source_key = 1")[0]["n"]));

            // The key sits on the spine ("sweep") and nowhere else.
            var owners = db.Query("""
                SELECT table_name FROM information_schema.columns
                WHERE table_schema = 'main' AND column_name = '_source_key' AND table_name <> 'data'
                ORDER BY table_name
                """).Select(r => (string)r["table_name"]!).ToArray();
            Assert.Equal(new[] { "sweep" }, owners);

            // "source" is a built-in table beside the layout, not part of it.
            Assert.DoesNotContain("source", db.Layout!.PhysicalTableNames);
            Assert.DoesNotContain("source", db.Schema.Tables.Keys);
            Assert.Equal("BASE TABLE", TableType(db, "source"));
            var source = Assert.Single(db.Sources);
            Assert.Equal(Path.GetFullPath(csv), source.Path);
            Assert.Equal("CsvImport", source.Type);
        }

        [Fact]
        public void Open_LayoutSession_RoleSelectorNeverClaimsTheKey()
        {
            var dir = TempDir();
            try
            {
                var csv = WriteCsv(dir, "a.csv", "id,Gain(dB)\n1,20.5\n2,21.5\n");
                // `meas` is the default role of an unknown column, and the group below selects it.
                // The key must stay on the spine anyway.
                var config = Path.Combine(dir, "layout.json");
                File.WriteAllText(config, """{ "tables": { "rf": { "kind": "measurements", "roles": ["meas"] } } }""");
                using var db = DataBall.Open(csv, config);
                var owners = db.Query("""
                    SELECT table_name FROM information_schema.columns
                    WHERE table_schema = 'main' AND column_name = '_source_key' AND table_name <> 'data'
                    """).Select(r => (string)r["table_name"]!).ToArray();
                Assert.Equal(new[] { "rows" }, owners);
                Assert.All(db.Query("SELECT _source_key FROM data"), r => Assert.Equal(1L, r["_source_key"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_Archive_EachEntryIsASource()
        {
            var dir = TempDir();
            try
            {
                var zip = Path.Combine(dir, "pair.zip");
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                {
                    AddEntry(archive, "sub/b.csv", "v\nB1\nB2\n");
                    AddEntry(archive, "a.csv", "v\nA1\n");
                    AddEntry(archive, "notes.txt", "ignored");
                }
                using var db = DataBall.Open(zip);

                Assert.Equal(new[] { zip + "!a.csv", zip + "!sub/b.csv" }, db.Sources.Select(s => s.Path).ToArray());
                Assert.Equal(new[] { 1L, 2L }, db.Sources.Select(s => s.Key).ToArray());
                Assert.All(db.Sources, s => Assert.Equal("ArchiveImport", s.Type));
                Assert.Equal(new[] { 5L, 8L }, db.Sources.Select(s => s.Bytes).ToArray());
                // The archive stores local time; the source table records UTC.
                Assert.All(db.Sources, src => Assert.InRange(src.ModifiedUtc!.Value, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5)));
                Assert.Equal(Sha256Hex("v\nA1\n"), db.Sources[0].Sha256);
                Assert.Equal(Sha256Hex("v\nB1\nB2\n"), db.Sources[1].Sha256);

                var rows = db.Query("SELECT v, _source_key FROM data ORDER BY v");
                Assert.Equal(new[] { ("A1", 1L), ("B1", 2L), ("B2", 2L) }, rows.Select(r => ((string)r["v"]!, (long)r["_source_key"]!)).ToArray());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_TarGz_EntriesAreSources()
        {
            var dir = TempDir();
            try
            {
                var tar = Path.Combine(dir, "seed.tar.gz");
                using (var seed = new DataBall())
                {
                    seed.AddRow(new Dictionary<string, object?> { ["v"] = "X" });
                    seed.ExportToArchive(tar);
                }
                using var db = DataBall.Open(tar);
                var only = Assert.Single(db.Sources);
                Assert.Equal(tar + "!data.csv", only.Path);
                Assert.Equal(1L, only.Key);
                Assert.Equal("ArchiveImport", only.Type);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_ParquetView_ConstantKey()
        {
            var dir = TempDir();
            try
            {
                var parquet = Path.Combine(dir, "plain.parquet");
                using (var seed = new DataBall())
                {
                    seed.AddRows(new[]
                    {
                        new Dictionary<string, object?> { ["id"] = 1L, ["v"] = 1.5 },
                        new Dictionary<string, object?> { ["id"] = 2L, ["v"] = 2.5 },
                    });
                    seed.Roll(ExportType.Parquet, parquet);
                }
                using var db = DataBall.Open(parquet);

                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.True(db.Store.IsViewBacked);
                Assert.Equal(new[] { "id", "v", "_source_key" }, DataColumns(db));
                var rows = db.Query("SELECT id, _source_key FROM data ORDER BY id");
                Assert.Equal(2, rows.Count);
                Assert.All(rows, r => Assert.Equal(1L, Assert.IsType<long>(r["_source_key"])));

                var source = Assert.Single(db.Sources);
                Assert.Equal(parquet, source.Path);
                Assert.Equal("ParquetView", source.Type);
                Assert.Equal(new FileInfo(parquet).Length, source.Bytes);
                Assert.Equal(Sha256Hex(File.ReadAllBytes(parquet)), source.Sha256);

                // The constant does not cost the view its pushdown.
                var plan = string.Join("\n", db.Query("EXPLAIN SELECT id FROM data WHERE id >= 2").Select(r => Convert.ToString(r["explain_value"])));
                Assert.Contains("READ_PARQUET", plan, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("id>=2", plan.Replace(" ", ""), StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_ParquetWithReservedSourceKeyColumn_Throws()
        {
            var dir = TempDir();
            try
            {
                var parquet = Path.Combine(dir, "bad.parquet");
                using (var seed = new DataBall())
                {
                    seed.AddRow(new Dictionary<string, object?> { ["id"] = 1L });
                    seed.Store.Execute("ALTER TABLE \"data\" ADD COLUMN \"_source_key\" BIGINT DEFAULT 7");
                    seed.Store.Execute($"COPY (SELECT * FROM data) TO '{parquet.Replace('\\', '/')}' (FORMAT PARQUET)");
                }
                var ex = Assert.Throws<DataBallException>(() => DataBall.Open(parquet));
                Assert.Contains("_source_key", ex.GetBaseException().Message);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Open_HiveDirectory_IsOneSourceWithAManifestHash()
        {
            var dir = TempDir();
            try
            {
                var hive = Path.Combine(dir, "hive");
                using (var seed = new DataBall())
                {
                    seed.AddRows(new[]
                    {
                        new Dictionary<string, object?> { ["Site"] = "A", ["id"] = 1L },
                        new Dictionary<string, object?> { ["Site"] = "B", ["id"] = 2L },
                    });
                    await seed.Squish(hive, new[] { "Site" });
                }
                var files = Directory.GetFiles(hive, "*.parquet", SearchOption.AllDirectories);
                using var db = DataBall.Open(hive);

                var source = Assert.Single(db.Sources);
                Assert.Equal(Path.GetFullPath(hive), source.Path);
                Assert.Equal("ParquetView", source.Type);
                Assert.Equal(ManifestSha256(hive), source.Sha256);
                Assert.Equal(files.Sum(f => new FileInfo(f).Length), source.Bytes);
                Assert.Equal(2L, Convert.ToInt64(db.Query("SELECT count(*) AS n FROM data WHERE _source_key = 1")[0]["n"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_ParquetWithLayoutConfig_CopiesWithKey()
        {
            var dir = TempDir();
            try
            {
                var parquet = Path.Combine(dir, "plain.parquet");
                using (var seed = new DataBall())
                {
                    seed.AddRows(new[]
                    {
                        new Dictionary<string, object?> { ["id"] = 1L, ["v"] = 1.5 },
                        new Dictionary<string, object?> { ["id"] = 2L, ["v"] = 2.5 },
                    });
                    seed.Roll(ExportType.Parquet, parquet);
                }
                var config = Path.Combine(dir, "layout.json");
                File.WriteAllText(config, """{ "tables": { "m": { "kind": "measurements", "columns": ["v"] } } }""");
                using var db = DataBall.Open(parquet, config);
                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Equal(new[] { "id", "v", "_source_key" }, DataColumns(db));
                Assert.All(db.Query("SELECT _source_key FROM data"), r => Assert.Equal(1L, r["_source_key"]));
                Assert.Equal("ParquetView", Assert.Single(db.Sources).Type);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Open_HiveDirectory_WithLayout_CopiesWithKey()
        {
            var dir = TempDir();
            try
            {
                var hive = Path.Combine(dir, "hive");
                using (var seed = new DataBall())
                {
                    seed.AddRows(new[]
                    {
                        new Dictionary<string, object?> { ["Site"] = "A", ["id"] = 1L, ["v"] = 1.5 },
                        new Dictionary<string, object?> { ["Site"] = "B", ["id"] = 2L, ["v"] = 2.5 },
                    });
                    await seed.Squish(hive, new[] { "Site" });
                }
                var config = Path.Combine(dir, "layout.json");
                File.WriteAllText(config, """{ "tables": { "m": { "kind": "measurements", "columns": ["v"] } } }""");
                using var db = DataBall.Open(hive, config);
                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Equal(2L, Convert.ToInt64(db.Query("SELECT count(*) AS n FROM data WHERE _source_key = 1")[0]["n"]));
                var source = Assert.Single(db.Sources);
                Assert.Equal(Path.GetFullPath(hive), source.Path);
                Assert.Equal(ManifestSha256(hive), source.Sha256);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportAsync_AppendToParquetView_MaterializesTheConstantKey()
        {
            var dir = TempDir();
            try
            {
                var parquet = Path.Combine(dir, "plain.parquet");
                using (var seed = new DataBall())
                {
                    seed.AddRow(new Dictionary<string, object?> { ["id"] = 1L });
                    await seed.ExportAsync(parquet, ExportType.Parquet);
                }
                var csv = WriteCsv(dir, "b.csv", "id\n2\n");
                using var db = DataBall.Open(parquet);
                Assert.True(db.Store.IsViewBacked);
                await db.ImportAsync(csv, new ImportOptions { Append = true });

                Assert.False(db.Store.IsViewBacked);
                Assert.Equal(new[] { parquet, csv }, db.Sources.Select(s => s.Path).ToArray());
                Assert.Equal(new[] { (1L, 1L), (2L, 2L) },
                    db.Query("SELECT id, _source_key FROM data ORDER BY id").Select(r => ((long)r["id"]!, (long)r["_source_key"]!)).ToArray());

                // A replace drops the view and starts the sources over.
                await db.ImportAsync(csv, new ImportOptions { Append = false });
                Assert.Equal(csv, Assert.Single(db.Sources).Path);
                Assert.Equal(1L, Assert.Single(db.Query("SELECT _source_key FROM data"))["_source_key"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_Handler_RecordsSource()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "data.xyz");
                File.WriteAllText(path, "opaque");
                DataBall.RegisterHandler(new LineageHandler(path));
                using var db = DataBall.Open(path);

                var source = Assert.Single(db.Sources);
                Assert.Equal("LineageHandler", source.Type);
                Assert.Equal(1L, source.Key);
                Assert.Equal(path, source.Path);
                Assert.Equal(6L, source.Bytes);
                Assert.Equal(Sha256Hex("opaque"), source.Sha256);
                var rows = db.Query("SELECT Name, _source_key FROM data ORDER BY Name");
                Assert.Equal(2, rows.Count);
                Assert.All(rows, r => Assert.Equal(1L, Assert.IsType<long>(r["_source_key"])));
                Assert.Equal(new[] { "Name", "_source_key" }, DataColumns(db));
            }
            finally
            {
                DataBall.ClearHandlers();
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_DelegatingHandler_OnArchive_EntriesTakeTheHandlerType()
        {
            var dir = TempDir();
            try
            {
                var zip = Path.Combine(dir, "pair.zip");
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                    AddEntry(archive, "a.csv", "v\nA1\n");
                DataBall.RegisterHandler(new LineageHandler(zip, delegates: true));
                using var db = DataBall.Open(zip);
                var source = Assert.Single(db.Sources);
                Assert.Equal(zip + "!a.csv", source.Path);
                Assert.Equal("LineageHandler", source.Type);
            }
            finally
            {
                DataBall.ClearHandlers();
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_Handler_LayoutSession_KeyOnSpine()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "data.xyz");
                File.WriteAllText(path, "opaque");
                var config = Path.Combine(dir, "layout.json");
                File.WriteAllText(config, """{ "tables": { "names": { "kind": "dimension", "columns": ["Name"] } } }""");
                DataBall.RegisterHandler(new LineageHandler(path));
                using var db = DataBall.Open(path, config);
                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Equal(2L, Convert.ToInt64(db.Query("SELECT count(*) AS n FROM data WHERE _source_key = 1")[0]["n"]));
                Assert.Equal("LineageHandler", Assert.Single(db.Sources).Type);
            }
            finally
            {
                DataBall.ClearHandlers();
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_HandlerEmittingSourceKey_DoesNotLetTheRowChooseItsSource()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "data.xyz");
                File.WriteAllText(path, "opaque");
                DataBall.RegisterHandler(new LineageHandler(path, emitKey: true));
                var ex = Assert.Throws<DataBallException>(() => DataBall.Open(path));
                Assert.Contains("_source_key", ex.GetBaseException().Message);
            }
            finally
            {
                DataBall.ClearHandlers();
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_DelegatingHandler_TypeIsTheHandlerName()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "data.csv");
                File.WriteAllText(path, "id\n1\n");
                DataBall.RegisterHandler(new LineageHandler(path, delegates: true));
                using var db = DataBall.Open(path);
                Assert.Equal("LineageHandler", Assert.Single(db.Sources).Type);
                Assert.Equal(1L, Assert.IsType<long>(db.Query("SELECT _source_key FROM data")[0]["_source_key"]));
            }
            finally
            {
                DataBall.ClearHandlers();
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportAsync_Append_AddsSource_KeysStable()
        {
            var dir = TempDir();
            try
            {
                var first = WriteCsv(dir, "a.csv", "id,Gain\n1,20.5\n2,21.5\n");
                var second = WriteCsv(dir, "b.csv", "id,Gain\n3,22.5\n");
                using var db = new DataBall();
                await db.ImportAsync(first);
                var firstSource = Assert.Single(db.Sources);
                await db.ImportAsync(second, new ImportOptions { Append = true });

                Assert.Equal(2, db.Sources.Count);
                Assert.Equal(firstSource, db.Sources[0]);
                Assert.Equal(new[] { 1L, 2L }, db.Sources.Select(s => s.Key).ToArray());
                Assert.Equal(new[] { first, second }, db.Sources.Select(s => s.Path).ToArray());
                Assert.True(db.Sources[1].AddedUtc >= db.Sources[0].AddedUtc);
                var rows = db.Query("SELECT id, _source_key FROM data ORDER BY id");
                Assert.Equal(new[] { (1L, 1L), (2L, 1L), (3L, 2L) }, rows.Select(r => ((long)r["id"]!, (long)r["_source_key"]!)).ToArray());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportAsync_Replace_StartsTheSourcesOver()
        {
            var dir = TempDir();
            try
            {
                var first = WriteCsv(dir, "a.csv", "id\n1\n");
                var second = WriteCsv(dir, "b.csv", "id\n2\n");
                using var db = new DataBall();
                await db.ImportAsync(first);
                await db.ImportAsync(second, new ImportOptions { Append = true });
                await db.ImportAsync(second, new ImportOptions { Append = false });

                var source = Assert.Single(db.Sources);
                Assert.Equal(1L, source.Key);
                Assert.Equal(second, source.Path);
                Assert.Equal(new[] { 1L }, db.Query("SELECT DISTINCT _source_key FROM data").Select(r => (long)r["_source_key"]!).ToArray());
                Assert.Single(db.Query("SELECT * FROM source"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportAsync_AppendIntoCaptureOnlySession_FirstKeyIsOne_CaptureRowsStayNull()
        {
            var dir = TempDir();
            try
            {
                var csv = WriteCsv(dir, "a.csv", "id\n10\n");
                using var db = new DataBall();
                Assert.Empty(db.Sources);
                db.AddRow(new Dictionary<string, object?> { ["id"] = 1L });
                Assert.Empty(db.Sources);
                Assert.Equal(new[] { "id" }, DataColumns(db));

                await db.ImportAsync(csv, new ImportOptions { Append = true });
                var source = Assert.Single(db.Sources);
                Assert.Equal(1L, source.Key);
                var rows = db.Query("SELECT id, _source_key FROM data ORDER BY id");
                Assert.Null(rows[0]["_source_key"]);
                Assert.Equal(1L, rows[1]["_source_key"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void CaptureOnlySession_HasNoSourceRowsAndNoKeyColumn()
        {
            using var db = new DataBall();
            db.AddRow(new Dictionary<string, object?> { ["id"] = 1L, ["Gain"] = 2.5 });
            Assert.Empty(db.Sources);
            Assert.Equal(new[] { "id", "Gain" }, DataColumns(db));
        }

        [Fact]
        public async Task ImportAsync_Layout_AppendAndResplit_KeepKeysOnTheSpine()
        {
            var dir = TempDir();
            try
            {
                var first = WriteCsv(dir, "a.csv", "id,Teststand,Gain(dB)\n1,T1,1.5\n2,T2,2.5\n");
                // Fixture is a new dimension column: the append re-splits the whole layout.
                var second = WriteCsv(dir, "b.csv", "id,Teststand,Fixture,Gain(dB)\n3,T1,F1,3.5\n");
                var config = Path.Combine(dir, "layout.json");
                File.WriteAllText(config, """{ "tables": { "setup": { "kind": "dimension", "columns": ["Teststand", "Fixture"] } } }""");

                using var db = new DataBall(config);
                await db.ImportAsync(first);
                await db.ImportAsync(second, new ImportOptions { Append = true });

                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.Equal(new[] { 1L, 2L }, db.Sources.Select(s => s.Key).ToArray());
                var rows = db.Query("SELECT id, _source_key FROM data ORDER BY id");
                Assert.Equal(new[] { (1L, 1L), (2L, 1L), (3L, 2L) }, rows.Select(r => ((long)r["id"]!, (long)r["_source_key"]!)).ToArray());
                var owners = db.Query("""
                    SELECT table_name FROM information_schema.columns
                    WHERE table_schema = 'main' AND column_name = '_source_key' AND table_name <> 'data'
                    """).Select(r => (string)r["table_name"]!).ToArray();
                Assert.Equal(new[] { "rows" }, owners);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportAsync_Layout_AppendInPlace_KeepsKeys()
        {
            var csv = Fixture("semiconductor-sweep.csv");
            using var db = new DataBall(Fixture("semiconductor-sweep.tables.json"));
            await db.ImportAsync(csv);
            await db.ImportAsync(csv, new ImportOptions { Append = true });

            Assert.Equal(new[] { 1L, 2L }, db.Sources.Select(s => s.Key).ToArray());
            var counts = db.Query("SELECT _source_key AS k, count(*) AS n FROM data GROUP BY _source_key ORDER BY k");
            Assert.Equal(new[] { (1L, 81L), (2L, 81L) }, counts.Select(r => ((long)r["k"]!, (long)r["n"]!)).ToArray());
        }

        [Fact]
        public async Task ImportAsync_Layout_UnsplitAndResplit_KeepKeys()
        {
            var csv = Fixture("semiconductor-sweep.csv");
            using var db = new DataBall(Fixture("semiconductor-sweep.tables.json"));
            await db.ImportAsync(csv);
            // A wide operation unsplits the layout, runs, and splits it again.
            db.AddColumn("Extra", Enumerable.Repeat(1L, 81).ToArray());

            Assert.Equal("VIEW", TableType(db, "data"));
            Assert.Equal(81L, Convert.ToInt64(db.Query("SELECT count(*) AS n FROM data WHERE _source_key = 1")[0]["n"]));
            Assert.Equal("sweep", db.Query("SELECT table_name FROM information_schema.columns WHERE table_schema = 'main' AND column_name = '_source_key' AND table_name <> 'data'")
                .Select(r => (string)r["table_name"]!).Single());
        }

        [Fact]
        public async Task ImportAsync_FailedImport_LeavesSourcesAsTheyWere()
        {
            var dir = TempDir();
            try
            {
                var first = WriteCsv(dir, "a.csv", "Site,Region,Meas\nA,N,1\n");
                // Site no longer determines Region: the layout append throws after the file was staged.
                var clash = WriteCsv(dir, "b.csv", "Site,Region,Meas\nA,S,2\n");
                var fine = WriteCsv(dir, "c.csv", "Site,Region,Meas\nA,N,3\n");
                var config = Path.Combine(dir, "layout.json");
                File.WriteAllText(config, """{ "tables": { "site": { "kind": "dimension", "key": ["Site"], "columns": ["Site", "Region"] } } }""");

                using var db = new DataBall(config);
                await db.ImportAsync(first);
                var before = db.Sources.ToArray();
                await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(clash, new ImportOptions { Append = true }));

                Assert.Equal(before, db.Sources.ToArray());
                Assert.Single(db.Query("SELECT * FROM source"));
                Assert.Equal(new[] { 1L }, db.Query("SELECT DISTINCT _source_key FROM data").Select(r => (long)r["_source_key"]!).ToArray());

                // The failed attempt did not use up a key.
                await db.ImportAsync(fine, new ImportOptions { Append = true });
                Assert.Equal(new[] { 1L, 2L }, db.Sources.Select(s => s.Key).ToArray());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportAsync_UnreadableParquet_LeavesSourcesAsTheyWere()
        {
            var dir = TempDir();
            try
            {
                var first = WriteCsv(dir, "a.csv", "id\n1\n");
                var garbage = Path.Combine(dir, "garbage.parquet");
                File.WriteAllText(garbage, "not parquet");
                using var db = new DataBall();
                await db.ImportAsync(first);
                await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(garbage, new ImportOptions { Append = true }));
                var source = Assert.Single(db.Sources);
                Assert.Equal(first, source.Path);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportManager_StaticRoutes_RecordSources()
        {
            var dir = TempDir();
            try
            {
                var csv = WriteCsv(dir, "a.csv", "id\n1\n");
                var parquet = Path.Combine(dir, "p.parquet");
                using (var seed = new DataBall())
                {
                    seed.AddRow(new Dictionary<string, object?> { ["id"] = 2L });
                    seed.Roll(ExportType.Parquet, parquet);
                }
                var zip = Path.Combine(dir, "z.zip");
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                    AddEntry(archive, "e.csv", "id\n3\n");

                using var db = new DataBall();
                ImportManager.ImportFromCsv(db, csv, append: false);
                await ImportManager.ImportFromParquet(db, parquet, append: true);
                ImportManager.ImportFromArchive(db, zip, append: true);

                Assert.Equal(new[] { csv, parquet, zip + "!e.csv" }, db.Sources.Select(s => s.Path).ToArray());
                Assert.Equal(new[] { "CsvImport", "ParquetView", "ArchiveImport" }, db.Sources.Select(s => s.Type).ToArray());
                var rows = db.Query("SELECT id, _source_key FROM data ORDER BY id");
                Assert.Equal(new[] { (1L, 1L), (2L, 2L), (3L, 3L) }, rows.Select(r => ((long)r["id"]!, (long)r["_source_key"]!)).ToArray());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Capture_Rows_NullKey()
        {
            var dir = TempDir();
            try
            {
                var csv = WriteCsv(dir, "a.csv", "id,Gain(dB)\n1,20.5\n2,21.5\n");
                using var db = DataBall.Open(csv);

                // CommitRow starts from a copy of the last row, which carries key 1.
                db.InitializeRow();
                db.ModifyField("id", 3L);
                db.CommitRow();
                db.AddRow(new Dictionary<string, object?> { ["id"] = 4L, ["Gain"] = 1.0 });
                db.AddRows(new[] { new Dictionary<string, object?> { ["id"] = 5L } });
                db.InitializeRow(new Dictionary<string, object?> { ["id"] = 6L });
                db.CommitRow();

                var rows = db.Query("SELECT id, _source_key FROM data ORDER BY id");
                Assert.Equal(6, rows.Count);
                Assert.Equal(1L, rows[0]["_source_key"]);
                Assert.Equal(1L, rows[1]["_source_key"]);
                Assert.All(rows.Skip(2), r => Assert.Null(r["_source_key"]));
                Assert.Single(db.Sources);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Capture_Rows_Layout_NullKey()
        {
            using var db = DataBall.Open(Fixture("semiconductor-sweep.csv"), Fixture("semiconductor-sweep.tables.json"));
            db.AddRow(new Dictionary<string, object?> { ["sweep"] = 99L, ["Pout"] = 1.5 });
            db.InitializeRow();
            db.ModifyField("sweep", 100L);
            db.CommitRow();

            Assert.Equal(83L, Convert.ToInt64(db.Query("SELECT count(*) AS n FROM data")[0]["n"]));
            Assert.Equal(2L, Convert.ToInt64(db.Query("SELECT count(*) AS n FROM data WHERE _source_key IS NULL")[0]["n"]));
            Assert.Equal(81L, Convert.ToInt64(db.Query("SELECT count(*) AS n FROM data WHERE _source_key = 1")[0]["n"]));
        }

        [Fact]
        public async Task Bounce_KeepsSourceKey()
        {
            var dir = TempDir();
            try
            {
                // Vcc is constant and leaves for metadata; so would _source_key if it were not kept.
                var csv = WriteCsv(dir, "a.csv", "id,Vcc(V)\n1,3.3\n2,3.3\n");
                using var db = DataBall.Open(csv);
                await db.Bounce();

                Assert.Equal(3.3, Convert.ToDouble(db.Metadata["Vcc"]), 3);
                Assert.False(db.Metadata.ContainsKey("_source_key"));
                Assert.Equal(new[] { "id", "_source_key" }, DataColumns(db));
                Assert.All(db.Query("SELECT _source_key FROM data"), r => Assert.Equal(1L, r["_source_key"]));
                Assert.Single(db.Sources);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Bounce_AllConstant_DropsDataAsBefore_LineageStaysInSources()
        {
            var dir = TempDir();
            try
            {
                // A table of nothing but _source_key holds no data: the last data column leaving drops it.
                var csv = WriteCsv(dir, "a.csv", "Vcc(V)\n3.3\n3.3\n");
                using var db = DataBall.Open(csv);
                await db.Bounce();
                Assert.Equal(0L, Convert.ToInt64(db.Query("SELECT count(*) AS n FROM information_schema.tables WHERE table_name = 'data'")[0]["n"]));
                Assert.Equal(3.3, Convert.ToDouble(db.Metadata["Vcc"]), 3);
                Assert.Equal(csv, Assert.Single(db.Sources).Path);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void RemoveColumn_LastDataColumn_DropsDataEvenWithTheKey()
        {
            var dir = TempDir();
            try
            {
                var csv = WriteCsv(dir, "a.csv", "id,Gain(dB)\n1,20.5\n");
                using var db = DataBall.Open(csv);
                db.RemoveColumn("Gain");
                Assert.Equal(new[] { "id", "_source_key" }, DataColumns(db));
                db.RemoveColumn("id");
                Assert.Equal(0L, Convert.ToInt64(db.Query("SELECT count(*) AS n FROM information_schema.tables WHERE table_name = 'data'")[0]["n"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Bounce_Layout_KeepsSourceKey()
        {
            var dir = TempDir();
            try
            {
                var csv = WriteCsv(dir, "a.csv", "id,Teststand,Gain(dB)\n1,T1,1.5\n2,T1,2.5\n");
                var config = Path.Combine(dir, "layout.json");
                File.WriteAllText(config, """{ "tables": { "setup": { "kind": "dimension", "columns": ["Teststand"] } } }""");
                using var db = DataBall.Open(csv, config);
                await db.Bounce();

                Assert.Equal("VIEW", TableType(db, "data"));
                Assert.False(db.Metadata.ContainsKey("_source_key"));
                Assert.Contains("_source_key", DataColumns(db));
                Assert.All(db.Query("SELECT _source_key FROM data"), r => Assert.Equal(1L, r["_source_key"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task MergeOrAppend_RemapsKeys()
        {
            var dir = TempDir();
            try
            {
                var a1 = WriteCsv(dir, "a1.csv", "id\n1\n");
                var a2 = WriteCsv(dir, "a2.csv", "id\n2\n");
                var b1 = WriteCsv(dir, "b1.csv", "id\n3\n");
                var b2 = WriteCsv(dir, "b2.csv", "id\n4\n5\n");
                using var left = DataBall.Open(a1);
                await left.ImportAsync(a2, new ImportOptions { Append = true });
                using var right = DataBall.Open(b1);
                await right.ImportAsync(b2, new ImportOptions { Append = true });
                var rightBefore = right.Sources.ToArray();

                left.MergeOrAppend(right, append: true);

                // Keys 1 and 2 of `right` collided with `left`; they now follow it, paths and hashes intact.
                Assert.Equal(new[] { 1L, 2L, 3L, 4L }, left.Sources.Select(s => s.Key).ToArray());
                Assert.Equal(new[] { a1, a2, b1, b2 }, left.Sources.Select(s => s.Path).ToArray());
                Assert.Equal(rightBefore.Select(s => s.Sha256), left.Sources.Skip(2).Select(s => s.Sha256));
                Assert.Equal(rightBefore.Select(s => s.AddedUtc), left.Sources.Skip(2).Select(s => s.AddedUtc));
                var rows = left.Query("SELECT id, _source_key FROM data ORDER BY id");
                Assert.Equal(
                    new[] { (1L, 1L), (2L, 2L), (3L, 3L), (4L, 4L), (5L, 4L) },
                    rows.Select(r => ((long)r["id"]!, (long)r["_source_key"]!)).ToArray());

                // The hand-off is internal: it keeps the key and does not resolve it.
                Assert.Equal(new[] { "id", "_source_key" }, DataColumns(left));
                // The other session is untouched.
                Assert.Equal(rightBefore, right.Sources.ToArray());
                Assert.Equal(new[] { 1L, 2L }, right.Query("SELECT DISTINCT _source_key FROM data ORDER BY 1").Select(r => (long)r["_source_key"]!).ToArray());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void MergeOrAppend_Replace_TakesTheOtherSessionsSources()
        {
            var dir = TempDir();
            try
            {
                var a = WriteCsv(dir, "a.csv", "id\n1\n");
                var b = WriteCsv(dir, "b.csv", "id\n2\n");
                using var left = DataBall.Open(a);
                using var right = DataBall.Open(b);
                left.MergeOrAppend(right, append: false);

                var source = Assert.Single(left.Sources);
                Assert.Equal(b, source.Path);
                Assert.Equal(1L, source.Key);
                Assert.Equal(2L, Convert.ToInt64(left.Query("SELECT id FROM data")[0]["id"]));
                Assert.Equal(1L, Convert.ToInt64(left.Query("SELECT _source_key FROM data")[0]["_source_key"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void MergeOrAppend_CaptureOnlyOther_LeavesNullKeysAndOurSources()
        {
            var dir = TempDir();
            try
            {
                var a = WriteCsv(dir, "a.csv", "id\n1\n");
                using var left = DataBall.Open(a);
                using var right = new DataBall();
                right.AddRow(new Dictionary<string, object?> { ["id"] = 2L });
                left.MergeOrAppend(right, append: true);

                Assert.Equal(a, Assert.Single(left.Sources).Path);
                var rows = left.Query("SELECT id, _source_key FROM data ORDER BY id");
                Assert.Equal(1L, rows[0]["_source_key"]);
                Assert.Null(rows[1]["_source_key"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void MergeOrAppend_OtherSessionKeysWithoutSources_AreNulled()
        {
            var dir = TempDir();
            try
            {
                var a = WriteCsv(dir, "a.csv", "id\n1\n");
                using var left = DataBall.Open(a);
                using var right = new DataBall();
                right.AddRow(new Dictionary<string, object?> { ["id"] = 2L });
                // Keys with no source table behind them: reachable only below the public API.
                right.Store.Execute("ALTER TABLE \"data\" ADD COLUMN \"_source_key\" BIGINT DEFAULT 9");
                left.MergeOrAppend(right, append: true);
                var rows = left.Query("SELECT id, _source_key FROM data ORDER BY id");
                Assert.Equal(1L, rows[0]["_source_key"]);
                Assert.Null(rows[1]["_source_key"]);
                Assert.Equal(a, Assert.Single(left.Sources).Path);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportBall_RemapsSourceKeys()
        {
            var dir = TempDir();
            try
            {
                var x = WriteCsv(dir, "x.csv", "id\n10\n");
                var c1 = WriteCsv(dir, "c1.csv", "id\n1\n");
                var c2 = WriteCsv(dir, "c2.csv", "id\n2\n3\n");
                var ball = Path.Combine(dir, "other.ball");
                using (var other = DataBall.Open(c1))
                {
                    await other.ImportAsync(c2, new ImportOptions { Append = true });
                    await other.SaveAsync(ball);
                }

                using var target = DataBall.Open(x);
                await target.ImportAsync(ball, new ImportOptions { Append = true });

                Assert.Equal(new[] { 1L, 2L, 3L }, target.Sources.Select(s => s.Key).ToArray());
                Assert.Equal(new[] { x, c1, c2 }, target.Sources.Select(s => s.Path).ToArray());
                Assert.Equal(new[] { "CsvImport", "CsvImport", "CsvImport" }, target.Sources.Select(s => s.Type).ToArray());
                var rows = target.Query("SELECT id, _source_key FROM data ORDER BY id");
                Assert.Equal(
                    new[] { (1L, 2L), (2L, 3L), (3L, 3L), (10L, 1L) },
                    rows.Select(r => ((long)r["id"]!, (long)r["_source_key"]!)).ToArray());
                // The .ball itself is not a source: only the files whose rows it holds are.
                Assert.DoesNotContain(target.Sources, s => s.Path == ball);

                // Replacing takes the ball's keys unchanged.
                await target.ImportAsync(ball, new ImportOptions { Append = false });
                Assert.Equal(new[] { 1L, 2L }, target.Sources.Select(s => s.Key).ToArray());
                Assert.Equal(new[] { c1, c2 }, target.Sources.Select(s => s.Path).ToArray());
                Assert.Equal(
                    new[] { (1L, 1L), (2L, 2L), (3L, 2L) },
                    target.Query("SELECT id, _source_key FROM data ORDER BY id").Select(r => ((long)r["id"]!, (long)r["_source_key"]!)).ToArray());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportBall_NoSourceTable_GivesNullKeys()
        {
            var dir = TempDir();
            try
            {
                var x = WriteCsv(dir, "x.csv", "id\n10\n");
                var ball = Path.Combine(dir, "pre-db22.ball");
                using (var old = new DataBall())
                {
                    old.AddRow(new Dictionary<string, object?> { ["id"] = 5L });
                    await old.SaveAsync(ball);
                }
                using (var reopened = DataBall.Open(ball))
                {
                    Assert.Empty(reopened.Sources);
                    Assert.Equal(0L, Convert.ToInt64(reopened.Query("SELECT count(*) AS n FROM information_schema.tables WHERE table_name = 'source'")[0]["n"]));
                }

                using var target = DataBall.Open(x);
                await target.ImportAsync(ball, new ImportOptions { Append = true });

                Assert.Equal(x, Assert.Single(target.Sources).Path);
                var rows = target.Query("SELECT id, _source_key FROM data ORDER BY id");
                Assert.Equal(5L, rows[0]["id"]);
                Assert.Null(rows[0]["_source_key"]);
                Assert.Equal(1L, rows[1]["_source_key"]);

                // Replace: a ball without lineage gives a table of NULL keys and no sources.
                await target.ImportAsync(ball, new ImportOptions { Append = false });
                Assert.Empty(target.Sources);
                Assert.Null(Assert.Single(target.Query("SELECT _source_key FROM data"))["_source_key"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportBall_KeysWithoutSourceTable_AreNulled()
        {
            var dir = TempDir();
            try
            {
                var x = WriteCsv(dir, "x.csv", "id\n10\n");
                var ball = Path.Combine(dir, "orphan.ball");
                using (var odd = new DataBall())
                {
                    // Keys that point at no source table cannot be kept: nothing says what they mean.
                    odd.AddRow(new Dictionary<string, object?> { ["id"] = 5L });
                    odd.Store.Execute("ALTER TABLE \"data\" ADD COLUMN \"_source_key\" BIGINT DEFAULT 7");
                    await odd.SaveAsync(ball);
                }
                using var target = DataBall.Open(x);
                await target.ImportAsync(ball, new ImportOptions { Append = true });
                var rows = target.Query("SELECT id, _source_key FROM data ORDER BY id");
                Assert.Null(rows[0]["_source_key"]);
                Assert.Equal(1L, rows[1]["_source_key"]);
                Assert.Equal(x, Assert.Single(target.Sources).Path);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportBall_IntoCaptureOnlySession_BringsSources()
        {
            var dir = TempDir();
            try
            {
                var c1 = WriteCsv(dir, "c1.csv", "id\n1\n");
                var ball = Path.Combine(dir, "other.ball");
                using (var other = DataBall.Open(c1))
                    await other.SaveAsync(ball);

                using var target = new DataBall();
                target.AddRow(new Dictionary<string, object?> { ["id"] = 0L });
                await target.ImportAsync(ball, new ImportOptions { Append = true });

                Assert.Equal(c1, Assert.Single(target.Sources).Path);
                var rows = target.Query("SELECT id, _source_key FROM data ORDER BY id");
                Assert.Null(rows[0]["_source_key"]);
                Assert.Equal(1L, rows[1]["_source_key"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Export_Csv_Parquet_Archive_ResolveSource()
        {
            var dir = TempDir();
            try
            {
                var a = WriteCsv(dir, "a.csv", "id,Gain\n1,20.5\n2,21.5\n");
                var b = WriteCsv(dir, "b.csv", "id,Gain\n3,22.5\n");
                using var db = DataBall.Open(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });
                db.AddRow(new Dictionary<string, object?> { ["id"] = 99L });

                var expected = new[]
                {
                    "1,20.5," + a + ",CsvImport",
                    "2,21.5," + a + ",CsvImport",
                    "3,22.5," + b + ",CsvImport",
                    "99,,,",
                };

                var csv = Path.Combine(dir, "out.csv");
                await db.ExportAsync(csv, ExportType.Csv);
                AssertCsv(File.ReadAllLines(csv), expected);

                var zip = Path.Combine(dir, "out.zip");
                await db.ExportAsync(zip, ExportType.Archive);
                using (var archive = ZipFile.OpenRead(zip))
                using (var reader = new StreamReader(archive.GetEntry("data.csv")!.Open()))
                    AssertCsv(reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries), expected);

                var tgz = Path.Combine(dir, "out.tar.gz");
                await db.ExportAsync(tgz, ExportType.Archive);
                using (var again = new DataBall())
                {
                    await again.ImportAsync(tgz);
                    Assert.Equal(new[] { "id", "Gain", "origin_source_file", "origin_source_type" }, DataColumns(again).Take(4).ToArray());
                }

                var parquet = Path.Combine(dir, "out.parquet");
                await db.ExportAsync(parquet, ExportType.Parquet);
                var described = db.Query($"DESCRIBE SELECT * FROM read_parquet('{parquet.Replace('\\', '/')}')");
                Assert.Equal(new[] { "id", "Gain", "_source_file", "_source_type" }, described.Select(r => (string)r["column_name"]!).ToArray());
                var rows = db.Query($"SELECT id, _source_file, _source_type FROM read_parquet('{parquet.Replace('\\', '/')}') ORDER BY id");
                Assert.Equal(new object?[] { a, a, b, null }, rows.Select(r => r["_source_file"]).ToArray());
                Assert.Equal(new object?[] { "CsvImport", "CsvImport", "CsvImport", null }, rows.Select(r => r["_source_type"]).ToArray());

                // The session itself is unchanged by exporting.
                Assert.Equal(new[] { "id", "Gain", "_source_key" }, DataColumns(db));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Export_WithApplyFilter_StillResolves()
        {
            var dir = TempDir();
            try
            {
                var a = WriteCsv(dir, "a.csv", "id,Gain\n1,20.5\n2,21.5\n");
                var b = WriteCsv(dir, "b.csv", "id,Gain\n3,22.5\n");
                using var db = DataBall.Open(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });
                db.ApplyFilter(new SessionFilter
                {
                    Predicates = [new ColumnPredicate { Column = "id", Op = PredicateOp.Ge, Value = 2L }]
                });

                var csv = Path.Combine(dir, "filtered.csv");
                await db.ExportAsync(csv, ExportType.Csv);
                AssertCsv(File.ReadAllLines(csv), new[] { "2,21.5," + a + ",CsvImport", "3,22.5," + b + ",CsvImport" });

                var zip = Path.Combine(dir, "filtered.zip");
                await db.ExportAsync(zip, ExportType.Archive);
                using (var archive = ZipFile.OpenRead(zip))
                using (var reader = new StreamReader(archive.GetEntry("data.csv")!.Open()))
                    AssertCsv(reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries), new[] { "2,21.5," + a + ",CsvImport", "3,22.5," + b + ",CsvImport" });

                var parquet = Path.Combine(dir, "filtered.parquet");
                await db.ExportAsync(parquet, ExportType.Parquet);
                var rows = db.Query($"SELECT id, _source_file FROM read_parquet('{parquet.Replace('\\', '/')}') ORDER BY id");
                Assert.Equal(new[] { a, b }, rows.Select(r => (string)r["_source_file"]!).ToArray());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Export_FilterProjectionWithoutKey_ExportsOnlyTheProjection()
        {
            var dir = TempDir();
            try
            {
                var a = WriteCsv(dir, "a.csv", "id,Gain\n1,20.5\n");
                using var db = DataBall.Open(a);

                db.ApplyFilter(new SessionFilter { Columns = ["id"] });
                var narrow = Path.Combine(dir, "narrow.csv");
                await db.ExportAsync(narrow, ExportType.Csv);
                Assert.Equal(new[] { "id", "1" }, File.ReadAllLines(narrow));

                // Naming the key keeps the lineage columns in the export.
                db.ApplyFilter(new SessionFilter { Columns = ["id", "_source_key"] });
                var keyed = Path.Combine(dir, "keyed.csv");
                await db.ExportAsync(keyed, ExportType.Csv);
                AssertCsv(File.ReadAllLines(keyed), new[] { "1," + a + ",CsvImport" }, "id,_source_file,_source_type");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Export_CaptureOnlySession_IsUnchanged()
        {
            var dir = TempDir();
            try
            {
                using var db = new DataBall();
                db.AddRow(new Dictionary<string, object?> { ["id"] = 1L, ["Gain"] = 2.5 });
                var csv = Path.Combine(dir, "plain.csv");
                await db.ExportAsync(csv, ExportType.Csv);
                Assert.Equal(new[] { "id,Gain", "1,2.5" }, File.ReadAllLines(csv));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Import_ReimportedExport_KeepsOriginProvenance()
        {
            var dir = TempDir();
            try
            {
                var a = WriteCsv(dir, "a.csv", "id\n1\n");
                var first = Path.Combine(dir, "first.csv");
                using (var db = DataBall.Open(a))
                    await db.ExportAsync(first, ExportType.Csv);

                using var again = DataBall.Open(first);
                // The first export's lineage columns arrive renamed, as ordinary data, beside a new key.
                Assert.Equal(new[] { "id", "origin_source_file", "origin_source_type", "_source_key" }, DataColumns(again));
                var row = Assert.Single(again.Query("SELECT id, origin_source_file, origin_source_type, _source_key FROM data"));
                Assert.Equal(a, row["origin_source_file"]);
                Assert.Equal("CsvImport", row["origin_source_type"]);
                Assert.Equal(1L, row["_source_key"]);
                Assert.Equal(first, Assert.Single(again.Sources).Path);

                // The second export carries both: where the rows came from before, and the file they were read from now.
                var second = Path.Combine(dir, "second.csv");
                await again.ExportAsync(second, ExportType.Csv);
                var lines = File.ReadAllLines(second);
                Assert.Equal("id,origin_source_file,origin_source_type,_source_file,_source_type", lines[0]);
                Assert.Equal("1," + a + ",CsvImport," + first + ",CsvImport", lines[1]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Import_ReimportedExport_ParquetAndHandlerRowsRenameToo()
        {
            var dir = TempDir();
            try
            {
                var a = WriteCsv(dir, "a.csv", "id\n1\n");
                var parquet = Path.Combine(dir, "first.parquet");
                using (var db = DataBall.Open(a))
                    await db.ExportAsync(parquet, ExportType.Parquet);

                // Parquet view
                using (var view = DataBall.Open(parquet))
                {
                    Assert.Equal(new[] { "id", "origin_source_file", "origin_source_type", "_source_key" }, DataColumns(view));
                    Assert.Equal(a, Assert.Single(view.Query("SELECT origin_source_file FROM data"))["origin_source_file"]);
                }
                // Parquet copy (append)
                using (var copy = new DataBall())
                {
                    await copy.ImportAsync(parquet);
                    Assert.Equal(new[] { "id", "origin_source_file", "origin_source_type", "_source_key" }, DataColumns(copy));
                }
                // Handler rows
                var path = Path.Combine(dir, "data.xyz");
                File.WriteAllText(path, "opaque");
                DataBall.RegisterHandler(new LineageHandler(path, extraColumn: "_source_file"));
                try
                {
                    using var handled = DataBall.Open(path);
                    Assert.Equal(new[] { "Name", "origin_source_file", "_source_key" }, DataColumns(handled));
                }
                finally
                {
                    DataBall.ClearHandlers();
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Theory]
        [InlineData("csv", "_source_other")]
        [InlineData("csv", "_SOURCE_File2")]
        [InlineData("csv", "_source_key")]
        [InlineData("parquet", "_source_other")]
        [InlineData("view", "_source_other")]
        [InlineData("handler", "_source_other")]
        [InlineData("handler", "_source_key")]
        public void Import_IncomingReservedSourceColumn_Throws(string route, string column)
        {
            var dir = TempDir();
            try
            {
                string path;
                if (route == "csv")
                    path = WriteCsv(dir, "bad.csv", $"id,{column}\n1,9\n");
                else if (route == "handler")
                {
                    path = Path.Combine(dir, "data.xyz");
                    File.WriteAllText(path, "opaque");
                    DataBall.RegisterHandler(new LineageHandler(path, extraColumn: column));
                }
                else
                {
                    path = Path.Combine(dir, "bad.parquet");
                    using var seed = new DataBall();
                    seed.AddRow(new Dictionary<string, object?> { ["id"] = 1L, ["x"] = 2L });
                    seed.Store.Execute($"ALTER TABLE \"data\" RENAME COLUMN x TO \"{column}\"");
                    seed.Store.Execute($"COPY (SELECT * FROM data) TO '{path.Replace('\\', '/')}' (FORMAT PARQUET)");
                }
                var ex = route == "parquet"
                    ? Assert.Throws<DataBallException>(() => { using var db = new DataBall(); db.ImportAsync(path).GetAwaiter().GetResult(); })
                    : Assert.Throws<DataBallException>(() => DataBall.Open(path));
                Assert.Equal($"Column '{column}' is reserved for source lineage; rename it in the input", ex.GetBaseException().Message);
            }
            finally
            {
                DataBall.ClearHandlers();
                Directory.Delete(dir, true);
            }
        }

        [Theory]
        [InlineData("csv")]
        [InlineData("view")]
        [InlineData("handler")]
        public void Import_IncomingOriginColumnAlreadyThere_Throws(string route)
        {
            var dir = TempDir();
            try
            {
                string path;
                if (route == "csv")
                    path = WriteCsv(dir, "both.csv", "id,_source_file,origin_source_file\n1,x,y\n");
                else if (route == "handler")
                {
                    path = Path.Combine(dir, "data.xyz");
                    File.WriteAllText(path, "opaque");
                    DataBall.RegisterHandler(new LineageHandler(path, extraColumns: new[] { "origin_source_file", "_source_file" }));
                }
                else
                {
                    path = Path.Combine(dir, "both.parquet");
                    using var seed = new DataBall();
                    seed.AddRow(new Dictionary<string, object?> { ["id"] = 1L, ["a"] = "x", ["origin_source_file"] = "y" });
                    seed.Store.Execute("ALTER TABLE \"data\" RENAME COLUMN a TO \"_source_file\"");
                    seed.Store.Execute($"COPY (SELECT * FROM data) TO '{path.Replace('\\', '/')}' (FORMAT PARQUET)");
                }
                var ex = Assert.Throws<DataBallException>(() => DataBall.Open(path));
                Assert.Equal("Column 'origin_source_file' already exists; it is where '_source_file' would go, because source lineage reserves that name", ex.GetBaseException().Message);
            }
            finally
            {
                DataBall.ClearHandlers();
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportBall_DataColumnNamedSourceFile_IsRenamed()
        {
            var dir = TempDir();
            try
            {
                var ball = Path.Combine(dir, "odd.ball");
                using (var odd = new DataBall())
                {
                    odd.AddRow(new Dictionary<string, object?> { ["id"] = 5L, ["a"] = "x" });
                    odd.Store.Execute("ALTER TABLE \"data\" RENAME COLUMN a TO \"_source_file\"");
                    await odd.SaveAsync(ball);
                }
                using var target = new DataBall();
                await target.ImportAsync(ball);
                Assert.Equal(new[] { "id", "origin_source_file", "_source_key" }, DataColumns(target));
                Assert.Equal("x", Assert.Single(target.Query("SELECT origin_source_file FROM data"))["origin_source_file"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Export_Layout_ResolvesSource()
        {
            var csv = Fixture("semiconductor-sweep.csv");
            using var db = DataBall.Open(csv, Fixture("semiconductor-sweep.tables.json"));
            var dir = TempDir();
            try
            {
                var output = Path.Combine(dir, "layout.csv");
                await db.ExportAsync(output, ExportType.Csv);
                var lines = File.ReadAllLines(output);
                Assert.Equal(82, lines.Length);
                Assert.EndsWith(",EVM,_source_file,_source_type", lines[0]);
                Assert.All(lines.Skip(1), l => Assert.EndsWith("," + Path.GetFullPath(csv) + ",CsvImport", l));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Squish_Hive_ResolvesSource()
        {
            var dir = TempDir();
            try
            {
                var a = WriteCsv(dir, "a.csv", "Site,id\nA,1\nA,2\n");
                var b = WriteCsv(dir, "b.csv", "Site,id\nB,3\n");
                using var db = DataBall.Open(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });
                var hive = Path.Combine(dir, "hive");
                await db.Squish(hive, new[] { "Site" });

                Assert.True(Directory.Exists(Path.Combine(hive, "Site=A")));
                Assert.True(Directory.Exists(Path.Combine(hive, "Site=B")));
                var glob = hive.Replace('\\', '/') + "/**/*.parquet";
                var columns = db.Query($"DESCRIBE SELECT * FROM read_parquet('{glob}', hive_partitioning = false)")
                    .Select(r => (string)r["column_name"]!).ToArray();
                Assert.Equal(new[] { "Site", "id", "_source_file", "_source_type" }, columns);
                var rows = db.Query($"SELECT id, _source_file, _source_type FROM read_parquet('{glob}', hive_partitioning = false) ORDER BY id");
                Assert.Equal(new[] { a, a, b }, rows.Select(r => (string)r["_source_file"]!).ToArray());
                Assert.All(rows, r => Assert.Equal("CsvImport", r["_source_type"]));

                // The session keeps its key after the Squish.
                Assert.Contains("_source_key", DataColumns(db));
                Assert.Equal(2, db.Sources.Count);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Squish_PartitionByKey_NamesTheProblem()
        {
            var dir = TempDir();
            try
            {
                var a = WriteCsv(dir, "a.csv", "Site,id\nA,1\nB,2\n");
                using var db = DataBall.Open(a);
                var ex = await Assert.ThrowsAsync<DataBallException>(() => db.Squish(Path.Combine(dir, "hive"), new[] { "_source_key" }));
                Assert.Contains("_source_key", ex.GetBaseException().Message);
                Assert.False(Directory.Exists(Path.Combine(dir, "hive")));
                Assert.Equal(new[] { "Site", "id", "_source_key" }, DataColumns(db));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ExportToPartitionedParquet_EmptyPartitions_ResolvesSource()
        {
            var dir = TempDir();
            try
            {
                var a = WriteCsv(dir, "a.csv", "id\n1\n");
                using var db = DataBall.Open(a);
                await ExportManager.ExportToPartitionedParquet(db, Path.Combine(dir, "flat"), Array.Empty<string>());
                var rows = db.Query($"SELECT id, _source_file FROM read_parquet('{Path.Combine(dir, "flat", "data.parquet").Replace('\\', '/')}')");
                Assert.Equal(a, Assert.Single(rows)["_source_file"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task SaveBall_KeepsNormalizedTables()
        {
            var dir = TempDir();
            try
            {
                var a = WriteCsv(dir, "a.csv", "id,Gain\n1,20.5\n");
                var b = WriteCsv(dir, "b.csv", "id,Gain\n2,21.5\n");
                using var db = DataBall.Open(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });
                db.AddRow(new Dictionary<string, object?> { ["id"] = 3L });
                var ball = Path.Combine(dir, "session.ball");
                await db.SaveAsync(ball);
                var viaExport = Path.Combine(dir, "export.ball");
                await db.ExportAsync(viaExport, ExportType.Ball);

                foreach (var path in new[] { ball, viaExport })
                {
                    using var reopened = DataBall.Open(path);
                    Assert.Equal(db.Sources.ToArray(), reopened.Sources.ToArray());
                    Assert.Equal(2, reopened.Query("SELECT * FROM source").Count);
                    Assert.Equal(new[] { "id", "Gain", "_source_key" }, DataColumns(reopened));
                    var rows = reopened.Query("SELECT id, _source_key FROM data ORDER BY id");
                    Assert.Equal(1L, rows[0]["_source_key"]);
                    Assert.Equal(2L, rows[1]["_source_key"]);
                    Assert.Null(rows[2]["_source_key"]);
                    // Exports of the reopened session resolve like the original's.
                    var csv = Path.Combine(dir, "again.csv");
                    await reopened.ExportAsync(csv, ExportType.Csv);
                    Assert.Equal("id,Gain,_source_file,_source_type", File.ReadAllLines(csv)[0]);
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task SaveBall_Layout_ReopensWithSourcesAndKeys()
        {
            var dir = TempDir();
            try
            {
                var csv = Fixture("semiconductor-sweep.csv");
                var config = Fixture("semiconductor-sweep.tables.json");
                var ball = Path.Combine(dir, "layout.ball");
                using (var db = DataBall.Open(csv, config))
                    await db.SaveAsync(ball);
                using var reopened = DataBall.Open(ball);
                Assert.Equal(Path.GetFullPath(csv), Assert.Single(reopened.Sources).Path);
                Assert.Equal(81L, Convert.ToInt64(reopened.Query("SELECT count(*) AS n FROM data WHERE _source_key = 1")[0]["n"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task SaveBall_LinkedParquetView_KeepsConstantKey()
        {
            var dir = TempDir();
            try
            {
                var parquet = Path.Combine(dir, "plain.parquet");
                using (var seed = new DataBall())
                {
                    seed.AddRow(new Dictionary<string, object?> { ["id"] = 1L });
                    await seed.ExportAsync(parquet, ExportType.Parquet);
                }
                var ball = Path.Combine(dir, "linked.ball");
                using (var db = DataBall.Open(parquet))
                    await db.SaveAsync(ball, new SaveOptions { Link = true });
                using var reopened = DataBall.Open(ball);
                Assert.Equal("ParquetView", Assert.Single(reopened.Sources).Type);
                Assert.Equal(1L, reopened.Query("SELECT _source_key FROM data")[0]["_source_key"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ReadColumns_SourceKeyIsLong()
        {
            var dir = TempDir();
            try
            {
                var a = WriteCsv(dir, "a.csv", "id,Gain\n1,20.5\n2,21.5\n");
                var b = WriteCsv(dir, "b.csv", "id,Gain\n3,22.5\n");
                using var db = DataBall.Open(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });
                db.AddRow(new Dictionary<string, object?> { ["id"] = 4L });

                var columns = db.ReadColumns(new SessionFilter());
                var key = Assert.IsType<ColumnData<long>>(columns["_source_key"]);
                Assert.Equal(typeof(long), key.ElementType);
                Assert.Equal(4L, columns.RowCount);
                var ids = (long[])columns["id"].Values;
                var keys = key.Values;
                var byId = ids.Zip(keys, (id, k) => (id, k)).OrderBy(p => p.id).ToArray();
                Assert.Equal(new[] { (1L, 1L), (2L, 1L), (3L, 2L) }, byId.Take(3).ToArray());
                Assert.NotNull(key.Nulls);
                Assert.Equal(1, key.Nulls!.Count(n => n));
                Assert.Equal(4L, ids[Array.IndexOf(key.Nulls, true)]);

                // Without a capture row there is no null mask.
                using var plain = DataBall.Open(a);
                Assert.Null(plain.ReadColumns(new SessionFilter())["_source_key"].Nulls);
                // A projection may name it.
                var only = db.ReadColumns(new SessionFilter { Columns = ["_source_key"] });
                Assert.IsType<ColumnData<long>>(only["_source_key"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ReadColumns_ParquetView_SourceKeyIsLong()
        {
            var dir = TempDir();
            try
            {
                var parquet = Path.Combine(dir, "plain.parquet");
                using (var seed = new DataBall())
                {
                    seed.AddRows(new[]
                    {
                        new Dictionary<string, object?> { ["id"] = 1L },
                        new Dictionary<string, object?> { ["id"] = 2L },
                    });
                    await seed.ExportAsync(parquet, ExportType.Parquet);
                }
                using var db = DataBall.Open(parquet);
                var key = Assert.IsType<ColumnData<long>>(db.ReadColumns(new SessionFilter())["_source_key"]);
                Assert.Equal(new long[] { 1, 1 }, key.Values);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Filter_Count_ReadRows_SeeTheKey()
        {
            var dir = TempDir();
            try
            {
                var a = WriteCsv(dir, "a.csv", "id\n1\n2\n");
                var b = WriteCsv(dir, "b.csv", "id\n3\n");
                using var db = DataBall.Open(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });
                var filter = new SessionFilter
                {
                    Predicates = [new ColumnPredicate { Column = "_source_key", Op = PredicateOp.Eq, Value = 2L }]
                };
                var rows = db.Filter(filter);
                var row = Assert.Single(rows);
                Assert.Equal(3L, row["id"]);
                Assert.Equal(2L, row["_source_key"]);
                Assert.Equal(1L, db.Count(filter));
                var streamed = new List<IReadOnlyDictionary<string, object?>>();
                await foreach (var r in db.ReadRowsAsync(filter))
                    streamed.Add(r);
                Assert.Equal(2L, Assert.Single(streamed)["_source_key"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Sources_ReturnsAnIndependentSnapshot()
        {
            var dir = TempDir();
            try
            {
                var a = WriteCsv(dir, "a.csv", "id\n1\n");
                using var db = DataBall.Open(a);
                var snapshot = db.Sources;
                db.Store.Execute("UPDATE source SET path = 'changed'");
                Assert.Equal(a, Assert.Single(snapshot).Path);
                Assert.Equal("changed", Assert.Single(db.Sources).Path);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Describe_HiveDirectory_Sha256_IsTheSortedManifestDigest()
        {
            var dir = TempDir();
            try
            {
                // Bytes fixed here; the literal was computed outside DataBall (python hashlib) from the
                // sorted lines "<relative path>\t<file sha256>\n" and does not depend on write order.
                var root = Path.Combine(dir, "hive");
                Directory.CreateDirectory(Path.Combine(root, "Site=B"));
                Directory.CreateDirectory(Path.Combine(root, "Site=A"));
                File.WriteAllBytes(Path.Combine(root, "top.parquet"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(root, "Site=B", "part-0.parquet"), "BB"u8.ToArray());
                File.WriteAllBytes(Path.Combine(root, "Site=A", "part-0.parquet"), "AAA\n"u8.ToArray());
                File.WriteAllText(Path.Combine(root, "ignored.txt"), "not a parquet file");

                var record = SourceFile.Describe(root, "ParquetView");
                Assert.Equal("f4c3b1ca3812a71dc222c99a6ec515b666704ee9df74813bc0fc582132496359", record.Sha256);
                Assert.Equal(6L, record.Bytes);
                Assert.Equal(Path.GetFullPath(root), record.Path);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void SourceInfo_IsASealedClassWithInitPropertiesAndNoPublicConstructor()
        {
            var type = typeof(SourceInfo);
            Assert.True(type.IsClass);
            Assert.True(type.IsSealed);
            Assert.Empty(type.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
            Assert.NotEmpty(type.GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));
            foreach (var name in new[] { "Key", "Path", "Sha256", "Type", "Bytes", "ModifiedUtc", "AddedUtc" })
            {
                var property = type.GetProperty(name)!;
                Assert.True(property.GetMethod!.IsPublic, name);
                var setter = property.SetMethod!;
                Assert.Contains(setter.ReturnParameter.GetRequiredCustomModifiers(), m => m.Name == "IsExternalInit");
            }
        }

        [Fact]
        public void SourceInfo_EqualityIsByValue_OfEveryProperty()
        {
            var modified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var added = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            SourceInfo Make(long key = 1, string path = "p", string? sha = "h", string type = "t", long bytes = 5, DateTime? mod = null, DateTime? add = null)
                => new(key, path, sha, type, bytes, mod ?? modified, add ?? added);

            var one = Make();
            Assert.Equal(one, Make());
            Assert.Equal(one.GetHashCode(), Make().GetHashCode());
            Assert.True(one.Equals((object)Make()));
            Assert.False(one.Equals(null));
            Assert.False(one.Equals((object)"x"));
            Assert.NotEqual(one, Make(key: 2));
            Assert.NotEqual(one, Make(path: "q"));
            Assert.NotEqual(one, Make(sha: "i"));
            Assert.NotEqual(one, Make(sha: null));
            Assert.NotEqual(one, Make(type: "u"));
            Assert.NotEqual(one, Make(bytes: 6));
            Assert.NotEqual(one, Make(mod: modified.AddSeconds(1)));
            Assert.NotEqual(one, Make(add: added.AddSeconds(1)));
            Assert.NotEqual(one.GetHashCode(), Make(key: 2).GetHashCode());
            // init-only: an object initializer can still change a copy.
            var changed = new SourceInfo(1, "p", "h", "t", 5, modified, added) { Path = "z" };
            Assert.Equal("z", changed.Path);
        }

        [Fact]
        public async Task ImportBall_NoSourceTable_IntoFreshSession_ExportHasNoKey()
        {
            var dir = TempDir();
            try
            {
                var ball = Path.Combine(dir, "capture.ball");
                using (var old = new DataBall())
                {
                    old.AddRow(new Dictionary<string, object?> { ["id"] = 5L, ["Gain"] = 2.5 });
                    await old.SaveAsync(ball);
                }
                using var fresh = new DataBall();
                await fresh.ImportAsync(ball);
                // The import gives the rows a NULL key; no file was read, so there is still no source table.
                Assert.Contains("_source_key", DataColumns(fresh));
                Assert.Empty(fresh.Sources);
                Assert.Equal(0L, Convert.ToInt64(fresh.Query("SELECT count(*) AS n FROM information_schema.tables WHERE table_name = 'source'")[0]["n"]));

                var csv = Path.Combine(dir, "out.csv");
                await fresh.ExportAsync(csv, ExportType.Csv);
                Assert.Equal(new[] { "id,Gain,_source_file,_source_type", "5,2.5,," }, File.ReadAllLines(csv));

                var parquet = Path.Combine(dir, "out.parquet");
                await fresh.ExportAsync(parquet, ExportType.Parquet);
                var described = fresh.Query($"DESCRIBE SELECT * FROM read_parquet('{parquet.Replace('\\', '/')}')");
                Assert.Equal(new[] { "id", "Gain", "_source_file", "_source_type" }, described.Select(r => (string)r["column_name"]!).ToArray());
                var zip = Path.Combine(dir, "out.zip");
                await fresh.ExportAsync(zip, ExportType.Archive);
                using var archive = ZipFile.OpenRead(zip);
                using var reader = new StreamReader(archive.GetEntry("data.csv")!.Open());
                Assert.Equal("id,Gain,_source_file,_source_type", reader.ReadLine());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Bounce_IdenticalRowsFromTwoFiles_Collapse_KeepsLowestKey()
        {
            var dir = TempDir();
            try
            {
                var a = WriteCsv(dir, "a.csv", "id,Gain\n1,20.5\n2,21.5\n");
                var a2 = WriteCsv(dir, "a2.csv", "id,Gain\n1,20.5\n2,21.5\n");
                using var db = DataBall.Open(a);
                await db.ImportAsync(a2, new ImportOptions { Append = true });
                Assert.Equal(4L, Convert.ToInt64(db.Query("SELECT count(*) AS n FROM data")[0]["n"]));
                await db.Bounce();
                var rows = db.Query("SELECT id, Gain, _source_key FROM data ORDER BY id");
                Assert.Equal(2, rows.Count);
                Assert.All(rows, r => Assert.Equal(1L, r["_source_key"]));
                Assert.Equal(new[] { "id", "Gain", "_source_key" }, DataColumns(db));
                // Both files stay recorded; only the duplicate rows merged.
                Assert.Equal(new[] { a, a2 }, db.Sources.Select(s => s.Path).ToArray());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Bounce_DifferentRowsFromTwoFiles_KeepTheirOwnKeys_AndCaptureRowsMatchingAFileRowTakeItsKey()
        {
            var dir = TempDir();
            try
            {
                var a = WriteCsv(dir, "a.csv", "id,Gain\n1,20.5\n");
                var b = WriteCsv(dir, "b.csv", "id,Gain\n2,21.5\n");
                using var db = DataBall.Open(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });
                db.AddRow(new Dictionary<string, object?> { ["id"] = 1L, ["Gain"] = 20.5 }); // NULL key, equal to the a.csv row
                db.AddRow(new Dictionary<string, object?> { ["id"] = 3L, ["Gain"] = 3.5 }); // NULL key, unique
                await db.Bounce();
                var rows = db.Query("SELECT id, _source_key FROM data ORDER BY id");
                Assert.Equal(new object?[] { 1L, 2L, null }, rows.Select(r => r["_source_key"]).ToArray());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Bounce_Layout_IdenticalRowsFromTwoFiles_Collapse_KeepsLowestKey()
        {
            var dir = TempDir();
            try
            {
                var a = WriteCsv(dir, "a.csv", "id,Teststand,Gain\n1,T1,1.5\n2,T2,2.5\n");
                var a2 = WriteCsv(dir, "a2.csv", "id,Teststand,Gain\n1,T1,1.5\n2,T2,2.5\n");
                var config = Path.Combine(dir, "layout.json");
                File.WriteAllText(config, """{ "tables": { "setup": { "kind": "dimension", "columns": ["Teststand"] } } }""");
                using var db = DataBall.Open(a, config);
                await db.ImportAsync(a2, new ImportOptions { Append = true });
                await db.Bounce();
                Assert.Equal("VIEW", TableType(db, "data"));
                var rows = db.Query("SELECT id, _source_key FROM data ORDER BY id");
                Assert.Equal(2, rows.Count);
                Assert.All(rows, r => Assert.Equal(1L, r["_source_key"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Theory]
        [InlineData("AddRow", "_source_key")]
        [InlineData("AddRow", "_SOURCE_File")]
        [InlineData("AddRow", "_source_other")]
        [InlineData("AddRows", "_source_key")]
        [InlineData("AddRows", "_Source_x")]
        [InlineData("InitializeRow", "_source_key")]
        [InlineData("InitializeRow", "_source_other")]
        [InlineData("ModifyField", "_source_key")]
        [InlineData("ModifyField", "_SOURCE_other")]
        [InlineData("AddColumn", "_source_key")]
        [InlineData("AddColumn", "_source_other")]
        [InlineData("AddColumnString", "_source_key")]
        [InlineData("AddColumnString", "_Source_other")]
        [InlineData("RemoveColumn", "_source_key")]
        [InlineData("RemoveColumn", "_source_other")]
        public async Task HostWrite_ReservedSourceColumn_Throws(string writer, string column)
        {
            var dir = TempDir();
            try
            {
                var a = WriteCsv(dir, "a.csv", "id,Gain\n1,20.5\n2,21.5\n");
                var b = WriteCsv(dir, "b.csv", "id,Gain\n3,22.5\n");
                using var db = DataBall.Open(a);
                if (writer == "ModifyField")
                    db.InitializeRow();
                var columnsBefore = DataColumns(db);
                var sourcesBefore = db.Sources.ToArray();
                var metadataBefore = db.Metadata.Keys.OrderBy(k => k).ToArray();

                Action act = writer switch
                {
                    "AddRow" => () => db.AddRow(new Dictionary<string, object?> { ["id"] = 9L, [column] = 1L }),
                    // The reserved name is in the second row: nothing from the first may be stored.
                    "AddRows" => () => db.AddRows(new[]
                    {
                        new Dictionary<string, object?> { ["id"] = 9L },
                        new Dictionary<string, object?> { ["id"] = 10L, [column] = 1L },
                    }),
                    "InitializeRow" => () => db.InitializeRow(new Dictionary<string, object?> { ["id"] = 9L, [column] = 1L }),
                    "ModifyField" => () => db.ModifyField(column, 1L),
                    "AddColumn" => () => db.AddColumn(column, new long[] { 1, 2 }),
                    "AddColumnString" => () => db.AddColumn(column, new string?[] { "x", "y" }),
                    "RemoveColumn" => () => db.RemoveColumn(column),
                    _ => throw new ArgumentException(writer)
                };
                var ex = Assert.Throws<DataBallException>(act);
                Assert.Equal($"Column name '{column}' is reserved: names starting with '_source' belong to source lineage", ex.Message);

                Assert.Equal(columnsBefore, DataColumns(db));
                Assert.Equal(2L, Convert.ToInt64(db.Query("SELECT count(*) AS n FROM data")[0]["n"]));
                Assert.Equal(sourcesBefore, db.Sources.ToArray());
                Assert.Equal(metadataBefore, db.Metadata.Keys.OrderBy(k => k).ToArray());
                Assert.All(db.Query("SELECT _source_key FROM data"), r => Assert.Equal(1L, r["_source_key"]));

                if (writer == "ModifyField")
                {
                    // The pending row is still usable and did not take the field.
                    db.CommitRow();
                    var last = db.Query("SELECT * FROM data WHERE _source_key IS NULL");
                    Assert.Single(last);
                    Assert.Equal(columnsBefore, DataColumns(db));
                }
                else
                {
                    // No pending row was left behind: the next write is accepted.
                    await db.ImportAsync(b, new ImportOptions { Append = true });
                    Assert.Equal(3L, Convert.ToInt64(db.Query("SELECT count(*) AS n FROM data")[0]["n"]));
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task HostWrite_InternalPaths_StillWriteTheKey()
        {
            // The handler drain and the merge write _source_key themselves; the host rule must not block them.
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "data.xyz");
                File.WriteAllText(path, "opaque");
                var a = WriteCsv(dir, "a.csv", "Name\nC\n");
                DataBall.RegisterHandler(new LineageHandler(path));
                using var db = DataBall.Open(path);
                using var other = DataBall.Open(a);
                db.MergeOrAppend(other, append: true);
                var rows = db.Query("SELECT Name, _source_key FROM data ORDER BY Name");
                Assert.Equal(new[] { ("A", 1L), ("B", 1L), ("C", 2L) }, rows.Select(r => ((string)r["Name"]!, (long)r["_source_key"]!)).ToArray());
                await Task.CompletedTask;
            }
            finally
            {
                DataBall.ClearHandlers();
                Directory.Delete(dir, true);
            }
        }

        private static void AssertCsv(IReadOnlyList<string> lines, string[] expectedRows, string header = "id,Gain,_source_file,_source_type")
        {
            Assert.Equal(header, lines[0].TrimEnd('\r'));
            Assert.DoesNotContain("_source_key", lines[0]);
            Assert.Equal(
                expectedRows.OrderBy(r => r, StringComparer.Ordinal).ToArray(),
                lines.Skip(1).Select(l => l.TrimEnd('\r')).OrderBy(r => r, StringComparer.Ordinal).ToArray());
        }

        private static string[] DataColumns(DataBall db) =>
            db.Query("SELECT column_name FROM information_schema.columns WHERE table_schema = 'main' AND table_name = 'data' ORDER BY ordinal_position")
                .Select(r => (string)r["column_name"]!).ToArray();

        private static string TableType(DataBall db, string table)
        {
            var rows = db.Query($"SELECT table_type FROM information_schema.tables WHERE table_schema = 'main' AND table_name = '{table}'");
            return rows.Count == 0 ? "" : (string)rows[0]["table_type"]!;
        }

        /// <summary>The hash of a hive directory, computed here a second way: sorted (relative path, file hash) lines.</summary>
        private static string ManifestSha256(string directory)
        {
            var full = Path.GetFullPath(directory);
            var lines = Directory.GetFiles(full, "*.parquet", SearchOption.AllDirectories)
                .Select(f => (Rel: Path.GetRelativePath(full, f).Replace('\\', '/'), Hash: Sha256Hex(File.ReadAllBytes(f))))
                .OrderBy(f => f.Rel, StringComparer.Ordinal)
                .Select(f => f.Rel + "\t" + f.Hash + "\n");
            return Sha256Hex(string.Concat(lines));
        }

        private static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

        private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private static string WriteCsv(string dir, string name, string text)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
            return path;
        }

        private static void AddEntry(ZipArchive archive, string name, string text)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(text));
        }

        private static string Fixture(string name)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
            Assert.True(File.Exists(path), "missing fixture " + path);
            return path;
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-lineage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private sealed class LineageHandler : IFormatHandler
        {
            private readonly string _path;
            private readonly bool _emitKey;
            private readonly bool _delegates;

            private readonly string[] _extraColumns;

            public LineageHandler(string path, bool emitKey = false, bool delegates = false, string? extraColumn = null, string[]? extraColumns = null)
            {
                _path = path;
                _emitKey = emitKey;
                _delegates = delegates;
                _extraColumns = extraColumns ?? (extraColumn is null ? Array.Empty<string>() : new[] { extraColumn });
            }

            public bool CanHandle(string path, Stream sniff) => string.Equals(path, _path, StringComparison.Ordinal);

            public string Name => "LineageHandler";

            public bool DelegatesToGenericImport => _delegates;

            public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Parse(
                string path,
                Config schema,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                _ = schema;
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                var first = new Dictionary<string, object?> { ["Name"] = "A" };
                if (_emitKey)
                    first["_source_key"] = 42L;
                foreach (var extra in _extraColumns)
                    first[extra] = "x";
                yield return first;
                yield return new Dictionary<string, object?> { ["Name"] = "B" };
            }
        }
    }
}
