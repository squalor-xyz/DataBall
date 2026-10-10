// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Xunit;

namespace squalor.DataBall.Tests
{
    public sealed class BallNativeTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "native-test-" + Guid.NewGuid().ToString("N"));
        private string FilePath(string name)
        {
            Directory.CreateDirectory(_dir);
            return Path.Combine(_dir, name);
        }
        private static Dictionary<string, object?> Row(long id) => new() { ["sample"] = id, ["value"] = id * 0.5 };

        private string Native(string name = "source.ball", string? config = null)
        {
            var path = FilePath(name);
            using var db = new DataBall(config, databasePath: path);
            db.AddRows(new[] { Row(1), Row(2) });
            db.SetMetadata("source", "ATE");
            return path;
        }

        private string LayoutConfig()
        {
            var path = FilePath("layout.json");
            File.WriteAllText(path, """{"tables":{"samples":{"kind":"dimension","columns":["sample"]},"measurements":{"kind":"measurements","columns":["value"]}}}""");
            return path;
        }

        private static long Count(DataBall db) => Convert.ToInt64(db.Query("SELECT count(*) AS n FROM data")[0]["n"]);

        [Fact]
        public void Reopen_EmptyLayoutSession_WithSameConfig_Works()
        {
            var config = LayoutConfig();
            var path = FilePath("capture.duckdb");
            using (var db = new DataBall(config, databasePath: path))
                Assert.False(db.Store.DataTableExists());
            using var reopened = new DataBall(config, databasePath: path);
            reopened.AddRow(Row(1));
            Assert.True(reopened.Store.DataIsView());
            Assert.Equal(1, Count(reopened));
        }

        [Fact]
        public async Task Open_MetadataOnlyLayout_WithSameOverlay_Works()
        {
            var config = LayoutConfig();
            var path = FilePath("metadata.ball");
            using (var db = new DataBall(config))
            {
                db.SetMetadata("source", "ATE");
                await db.SaveAsync(path);
            }
            using var reopened = DataBall.Open(path, config);
            Assert.Equal("ATE", reopened.Metadata["source"]);
            Assert.Equal(new[] { "samples", "measurements" }, reopened.Schema.Tables.Keys);
            Assert.False(reopened.Store.DataTableExists());
        }

        [Fact]
        public void Save_InsideStoreTransaction_ThrowsAndPreservesTransaction()
        {
            using var db = new DataBall();
            db.AddRow(Row(1));
            var path = FilePath("transaction.ball");
            db.Store.InTransaction(() =>
            {
                db.Store.Execute("INSERT INTO data VALUES (2, 1.0)");
                var error = Assert.Throws<DataBallException>(() => db.Store.SaveTo(path));
                Assert.Equal("Cannot save inside a store transaction", error.Message);
                Assert.Equal(2, Count(db));
                Assert.False(File.Exists(path));
                Assert.False(File.Exists(path + ".tmp"));
            });
            Assert.Equal(2, Count(db));
        }

        [Fact]
        public async Task Save_InMemory_WritesDuckDbFile_ReopenSameRowsAndMetadata()
        {
            using var db = new DataBall();
            db.AddRows(new[] { Row(1), Row(2) });
            db.SetMetadata("source", "ATE");
            var path = FilePath("saved.ball");
            await db.SaveAsync(path);
            Assert.Equal("DUCK"u8.ToArray(), File.ReadAllBytes(path)[8..12]);
            using var reopened = DataBall.Open(path);
            Assert.Equal(db.Query("SELECT * FROM data").Select(r => r.ToArray()), reopened.Query("SELECT * FROM data").Select(r => r.ToArray()));
            Assert.Equal(db.Metadata.ToArray(), reopened.Metadata.ToArray());
        }

        [Fact]
        public async Task Save_LayoutSession_OpenWithoutConfig_BindsLayout()
        {
            using var db = new DataBall(LayoutConfig());
            db.AddRows(new[] { Row(1), Row(2) });
            var path = FilePath("saved.ball");
            await db.SaveAsync(path);
            Assert.Equal("DUCK"u8.ToArray(), File.ReadAllBytes(path)[8..12]);
            using (var connection = new DuckDBConnection($"Data Source={path};ACCESS_MODE=READ_ONLY"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT config FROM _databall ORDER BY version DESC LIMIT 1";
                using var json = JsonDocument.Parse((string)command.ExecuteScalar()!);
                var config = Config.FromJson(json.RootElement.GetRawText());
                Assert.Equal(JsonSerializer.Serialize(db.Schema.Tables), JsonSerializer.Serialize(config.Tables));
            }
            using var reopened = DataBall.Open(path);
            Assert.True(reopened.Store.DataIsView());
            Assert.Equal(db.Schema.Tables.Keys, reopened.Schema.Tables.Keys);
            Assert.Equal(db.Query("SELECT * FROM data").Select(r => r.ToArray()), reopened.Query("SELECT * FROM data").Select(r => r.ToArray()));
        }

        [Fact]
        public async Task Open_Default_IsReadOnly_WritesNothing()
        {
            var path = Native();
            var source = Native("import.ball");
            var before = SHA256.HashData(File.ReadAllBytes(path));
            using (var db = DataBall.Open(path))
            {
                Assert.Throws<DataBallException>(() => db.AddRow(Row(3)));
                var metadata = db.Metadata.ToArray();
                Assert.Throws<DataBallException>(() => db.SetMetadata("source", "changed"));
                var importError = await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(source));
                Assert.Equal("Session is read-only; open with writable: true to change it", importError.Message);
                Assert.Equal(metadata, db.Metadata.ToArray());
            }
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Save_ReadOnly_ToOwnPath_Throws_FileUnchanged(bool withOverlay)
        {
            var path = Native();
            var overlay = FilePath("overlay.json");
            File.WriteAllText(overlay, """{"metadata":{"site":"A"}}""");
            var before = SHA256.HashData(File.ReadAllBytes(path));
            using (var db = DataBall.Open(path, withOverlay ? overlay : null))
            {
                var error = await Assert.ThrowsAsync<DataBallException>(() => db.SaveAsync(path));
                Assert.Equal("Session is read-only; save to another path or open with writable: true", error.Message);
                Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
                Assert.Equal(2, Count(db));
            }
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        }

        [Fact]
        public void Open_Writable_AppendPersists()
        {
            var path = Native();
            using (var db = DataBall.Open(path, writable: true))
            {
                db.AddRow(Row(3));
            }
            using var reopened = DataBall.Open(path);
            Assert.Equal(3, Count(reopened));
        }

        [Fact]
        public void Open_ReadOnly_SamePathTwiceInProcess_Throws()
        {
            var path = Native();
            using var first = DataBall.Open(path);
            Assert.Contains("already open", Assert.Throws<DataBallException>(() => DataBall.Open(path)).Message);
        }

        [Fact]
        public void Open_ZipNamedBall_ThrowsUnsupported()
        {
            var path = FilePath("old.ball");
            File.WriteAllBytes(path, "PK"u8.ToArray());
            Assert.Contains("1.x ZIP .ball", Assert.Throws<DataBallException>(() => DataBall.Open(path)).Message);
        }

        [Fact]
        public void Open_DuckDbWithoutDataballTable_Throws()
        {
            var path = Native();
            using (var store = new DuckDbStore(path))
            {
                store.Execute("DROP TABLE IF EXISTS _databall");
            }
            Assert.Contains("_databall", Assert.Throws<DataBallException>(() => DataBall.Open(path)).Message);
        }

        [Fact]
        public void Open_DataballFormatNot3_Throws()
        {
            var path = Native();
            using (var store = new DuckDbStore(path))
            {
                store.Execute("UPDATE _databall SET ball_format = 4");
            }
            Assert.Contains("format", Assert.Throws<DataBallException>(() => DataBall.Open(path)).Message);
        }

        [Fact]
        public async Task DataballTable_AppendsOnlyOnConfigChange_LatestWins()
        {
            using var db = new DataBall();
            db.AddRow(Row(1));
            var path = FilePath("saved.ball");
            await db.SaveAsync(path);
            await db.SaveAsync(path);
            Assert.Equal(1L, Convert.ToInt64(db.Query("SELECT count(*) AS n FROM _databall")[0]["n"]));
            var cfg = FilePath("new.json");
            File.WriteAllText(cfg, """{"columns":{"value":"double"},"metadata":{"revision":"new"}}""");
            var source = Native("new.ball", cfg);
            await db.ImportAsync(source);
            await db.SaveAsync(path);
            Assert.Equal(2L, Convert.ToInt64(db.Query("SELECT count(*) AS n FROM _databall")[0]["n"]));
            using var reopened = DataBall.Open(path);
            Assert.Equal("double", reopened.Schema.Columns["value"]);
            Assert.Equal("new", reopened.Metadata["revision"]);
        }

        [Fact]
        public async Task Save_WithFilter_SavesAllRows()
        {
            using var db = new DataBall();
            db.AddRows(new[] { Row(1), Row(2) });
            db.ApplyFilter(new SessionFilter { Predicates = [new ColumnPredicate { Column = "sample", Op = PredicateOp.Eq, Value = 1L }] });
            var path = FilePath("saved.ball");
            await db.SaveAsync(path);
            using var reopened = DataBall.Open(path);
            Assert.Equal(2, Count(reopened));
        }

        [Fact]
        public async Task Save_AfterDimensionGrowth_SmallerThanLiveFile()
        {
            var cfg = FilePath("growth.json");
            File.WriteAllText(cfg, """{"tables":{"groups":{"kind":"dimension","columns":["groupId"]},"environment":{"kind":"dimension","columns":["Humidity"]},"measurements":{"kind":"measurements","columns":["Pout","EVM","current"]}}}""");
            var live = FilePath("live.duckdb");
            using var db = new DataBall(cfg, databasePath: live);
            var rows = Enumerable.Range(0, 50_000).Select(i => new Dictionary<string, object?> { ["groupId"] = (long)(i / 100), ["sweepId"] = (long)(i % 100), ["Pout"] = -20.0 + i % 100 * .25, ["EVM"] = -40.0 + i % 37 * .1, ["current"] = .1 + i % 23 * .001 }).ToArray();
            db.AddRows(rows);
            db.Store.Execute("CHECKPOINT");
            var growth = new Dictionary<string, object?>(rows[^1]) { ["groupId"] = 500L, ["sweepId"] = 0L, ["Humidity"] = 40.0 };
            db.AddRow(growth);
            db.Store.Execute("CHECKPOINT");
            var saved = FilePath("saved.ball");
            await db.SaveAsync(saved);
            Assert.Equal("DUCK"u8.ToArray(), File.ReadAllBytes(saved)[8..12]);
            Assert.True(new FileInfo(saved).Length < new FileInfo(live).Length, $"saved={new FileInfo(saved).Length}; live={new FileInfo(live).Length}");
        }

        [Fact]
        public async Task Save_ToOwnLivePath_Checkpoints()
        {
            var path = Native();
            using (var db = DataBall.Open(path, writable: true))
            {
                db.AddRow(Row(3));
                await db.SaveAsync(path);
                Assert.False(File.Exists(path + ".wal"));
            }
            using var reopened = DataBall.Open(path);
            Assert.Equal(3, Count(reopened));
        }

        [Fact]
        public async Task ImportAsync_NativeBall_ReplaceAppendAndLayout()
        {
            var path = Native(config: LayoutConfig());
            using var db = new DataBall();
            db.AddRow(Row(99));
            await db.ImportAsync(path);
            Assert.Equal(2, Count(db));
            Assert.True(db.Store.DataIsView());
            Assert.Equal("ATE", db.Metadata["source"]);
            await db.ImportAsync(path, new ImportOptions { Append = true });
            Assert.Equal(4, Count(db));
            Assert.Empty(db.Query("SELECT database_name FROM duckdb_databases() WHERE starts_with(database_name, 'import_')"));
            await db.SaveAsync(path);
            using var reopened = DataBall.Open(path);
            Assert.Equal(Count(db), Count(reopened));
        }

        [Fact]
        public async Task ImportAsync_NativeBall_Failure_LeavesSessionUnchanged()
        {
            var cfg = FilePath("failure.json");
            File.WriteAllText(cfg, """{"metadata":{"revision":"bad"},"tables":{"other":{"kind":"dimension","columns":["sample"]},"measurements":{"kind":"measurements","columns":["value"]}}}""");
            var path = Native(config: cfg);
            using var db = new DataBall(LayoutConfig());
            db.AddRow(Row(99));
            db.SetMetadata("revision", "original");
            var before = db.Query("SELECT * FROM data").Select(r => r.ToArray()).ToArray();
            var schema = JsonSerializer.Serialize(db.Schema);
            var metadata = db.Metadata.ToArray();
            var versions = db.Query("SELECT count(*) AS n FROM _databall")[0]["n"];
            var error = await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(path, new ImportOptions { Append = true }));
            Assert.Contains("different 'tables' layout", error.ToString());
            Assert.Equal(before, db.Query("SELECT * FROM data").Select(r => r.ToArray()));
            Assert.Equal(schema, JsonSerializer.Serialize(db.Schema));
            Assert.Equal(metadata, db.Metadata.ToArray());
            Assert.Equal(versions, db.Query("SELECT count(*) AS n FROM _databall")[0]["n"]);
            Assert.Empty(db.Query("SELECT database_name FROM duckdb_databases() WHERE starts_with(database_name, 'import_')"));
        }

        [Fact]
        public async Task Save_RecordsEngineAndStorageVersion()
        {
            using var db = new DataBall();
            db.AddRow(Row(1));
            var path = FilePath("saved.ball");
            await db.SaveAsync(path);
            using var store = new DuckDbStore(path);
            var row = Assert.Single(store.Query("SELECT * FROM _databall ORDER BY version DESC LIMIT 1"));
            Assert.Equal(3, Convert.ToInt32(row["ball_format"]));
            Assert.Equal(store.ExecuteScalar("SELECT version()"), row["duckdb_version"]);
            Assert.Equal(store.ExecuteScalar("SELECT tags['storage_version'] FROM duckdb_databases() WHERE database_name = current_database()"), row["storage_version"]);
        }

        [Fact]
        public async Task Save_ReadOnly_CopyReopensWithSameRows()
        {
            using var db = DataBall.Open(Native());
            var path = FilePath("copy.ball");
            await db.SaveAsync(path);
            using var reopened = DataBall.Open(path);
            Assert.Equal(db.Query("SELECT * FROM data").Select(r => r.ToArray()), reopened.Query("SELECT * FROM data").Select(r => r.ToArray()));
        }

        [Fact]
        public async Task Save_StaleTemporaryDatabase_IsRemoved()
        {
            using var db = new DataBall();
            db.AddRow(Row(3));
            var path = FilePath("copy.ball");
            using (var stale = new DataBall(databasePath: path + ".tmp"))
                stale.AddRow(Row(99));
            await db.SaveAsync(path);
            Assert.Equal(1, Count(db));
            Assert.Empty(db.Query("SELECT database_name FROM duckdb_databases() WHERE starts_with(database_name, 'save_')"));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.False(File.Exists(path + ".tmp.wal"));
            using var reopened = DataBall.Open(path);
            Assert.Equal(3L, reopened.Query("SELECT sample FROM data")[0]["sample"]);
        }

        [Fact]
        public async Task Save_OtherSessionTarget_ThrowsAndLaterAppendPersists()
        {
            var path = Native();
            using var source = new DataBall();
            source.AddRow(Row(99));
            using (var held = DataBall.Open(path, writable: true))
            {
                await Assert.ThrowsAsync<DataBallException>(() => source.SaveAsync(path));
                held.AddRow(Row(3));
            }
            using var reopened = DataBall.Open(path);
            Assert.Equal(3, Count(reopened));
        }

        [Fact]
        public async Task Save_OwnPathDifferentCase_LaterAppendPersists()
        {
            var path = Native("c.ball");
            var upper = FilePath("C.ball");
            if (!File.Exists(upper))
                return; // This temporary directory is case-sensitive.
            using (var db = DataBall.Open(path, writable: true))
            {
                await db.SaveAsync(upper);
                db.AddRow(Row(3));
            }
            using var reopened = DataBall.Open(path);
            Assert.Equal(3, Count(reopened));
        }

        [Fact]
        public async Task Open_StoredMetadata_IsAuthoritativeInBothModes()
        {
            var config = FilePath("metadata.json");
            File.WriteAllText(config, """{"metadata":{"site":"A"}}""");
            var path = Native(config: config);
            using (var db = DataBall.Open(path, writable: true))
            {
                db.SetMetadata("site", "B");
                await db.SaveAsync(path);
            }
            using (var db = DataBall.Open(path))
                Assert.Equal("B", db.Metadata["site"]);
            using (var db = DataBall.Open(path, writable: true))
                Assert.Equal("B", db.Metadata["site"]);
            using var store = new DuckDbStore(path, readOnly: true);
            Assert.Equal("B", DuckDbStore.DeserializeMetadataValue((string)store.Query("SELECT value FROM meta WHERE key = 'site'")[0]["value"]!));
        }

        [Fact]
        public void Open_ReadOnlyOverlay_MetadataIsInMemoryAndFileUnchanged()
        {
            var path = Native();
            var config = FilePath("overlay.json");
            File.WriteAllText(config, """{"metadata":{"source":"overlay","site":"A"}}""");
            var before = SHA256.HashData(File.ReadAllBytes(path));
            using (var db = DataBall.Open(path, config))
            {
                Assert.Equal("overlay", db.Metadata["source"]);
                Assert.Equal("A", db.Metadata["site"]);
            }
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        }

        [Fact]
        public void Open_OverlayTablesDifferentOrder_AcceptsSameShape()
        {
            var path = Native(config: LayoutConfig());
            var overlay = FilePath("overlay.json");
            File.WriteAllText(overlay, """{"tables":{"measurements":{"kind":"measurements","columns":["value"]},"samples":{"kind":"dimension","columns":["sample"]}}}""");
            using (var db = DataBall.Open(path, overlay))
                Assert.Equal(2, Count(db));
        }

        [Fact]
        public void Open_OverlayTablesDifferentShape_Throws()
        {
            var path = Native(config: LayoutConfig());
            var overlay = FilePath("overlay.json");
            File.WriteAllText(overlay, """{"tables":{"samples":{"kind":"dimension","columns":["value"]},"measurements":{"kind":"measurements","columns":["sample"]}}}""");
            Assert.Throws<DataBallException>(() => DataBall.Open(path, overlay));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Save_FileBacked_StampsWriterWithoutHistoryGrowth(bool ownPath)
        {
            var path = Native();
            using var db = DataBall.Open(path, writable: true);
            db.Store.Execute("UPDATE _databall SET duckdb_version = 'old', storage_version = 'old'");
            var target = ownPath ? path : FilePath("copy.ball");
            await db.SaveAsync(target);
            using var saved = ownPath ? null : new DuckDbStore(target, readOnly: true);
            var store = saved ?? db.Store;
            var row = Assert.Single(store.Query("SELECT * FROM _databall"));
            Assert.Equal(store.ExecuteScalar("SELECT version()"), row["duckdb_version"]);
            Assert.Equal(store.ExecuteScalar("SELECT tags['storage_version'] FROM duckdb_databases() WHERE database_name = current_database()"), row["storage_version"]);
        }

        [Fact]
        public async Task Import_NativeConfig_OnlyMergesSchemaSubset()
        {
            var config = FilePath("session.json");
            File.WriteAllText(config, """{"metadataFields":["Wafer"],"metadataPolicy":"first"}""");
            using var db = new DataBall(config);
            var sourceConfig = LayoutConfig();
            var c = Config.LoadConfig(sourceConfig);
            c.Columns["value"] = "double";
            c.Metadata["revision"] = "imported";
            c.Relationships.Add(new Relationship { TriggerField = "sample", ResetFields = new() { "value" } });
            File.WriteAllText(sourceConfig, c.ToJson());
            await db.ImportAsync(Native(config: sourceConfig), new ImportOptions { Append = true });
            Assert.Equal(new[] { "Wafer" }, db.Schema.MetadataFields);
            Assert.Equal("first", db.Schema.MetadataPolicy);
            Assert.Equal("double", db.Schema.Columns["value"]);
            Assert.Equal(c.Tables.Keys, db.Schema.Tables.Keys);
            Assert.Equal("imported", db.Metadata["revision"]);
            Assert.Equal("sample", Assert.Single(db.Schema.Relationships).TriggerField);
        }

        [Fact]
        public async Task Save_ReadOnlyOverlay_PersistsMergedConfigInCopyOnly()
        {
            var path = Native();
            var overlay = FilePath("overlay.json");
            File.WriteAllText(overlay, """{"metadataFields":["Wafer"],"metadataPolicy":"first","metadata":{"site":"A"}}""");
            var copy = FilePath("copy.ball");
            using (var db = DataBall.Open(path, overlay))
                await db.SaveAsync(copy);
            using (var db = DataBall.Open(copy))
            {
                Assert.Equal(new[] { "Wafer" }, db.Schema.MetadataFields);
                Assert.Equal("first", db.Schema.MetadataPolicy);
                Assert.Equal("A", db.Metadata["site"]);
                Assert.Equal(2L, db.Query("SELECT count(*) AS n FROM _databall")[0]["n"]);
            }
            using var original = DataBall.Open(path);
            Assert.DoesNotContain("Wafer", original.Schema.MetadataFields);
            Assert.Equal(1L, original.Query("SELECT count(*) AS n FROM _databall")[0]["n"]);
        }

        [Fact]
        public async Task Import_ReplaceNativeWithoutData_DropsSessionData()
        {
            var path = Native();
            using (var store = new DuckDbStore(path))
                store.Execute("DROP TABLE data");
            using var db = new DataBall();
            db.AddRow(Row(99));
            await db.ImportAsync(path);
            Assert.False(db.Store.DataTableExists());
        }

        [Fact]
        public async Task NativeWithoutMeta_ImportAndReadOnlyOpenWork()
        {
            var path = Native();
            using (var store = new DuckDbStore(path))
                store.Execute("DROP TABLE meta");
            using (var opened = DataBall.Open(path))
            {
                Assert.Equal(2, Count(opened));
                Assert.Empty(opened.Metadata);
            }
            using var db = new DataBall();
            await db.ImportAsync(path);
            Assert.Equal(2, Count(db));
            Assert.Empty(db.Metadata);
        }

        [Fact]
        public async Task Save_FailedDestinationWrite_PreservesOriginalErrorAndSessionRemainsUsable()
        {
            using var db = new DataBall();
            db.AddRow(Row(1));
            db.Store.Execute("UPDATE _databall SET version = 2147483647");
            var changed = db.Schema;
            changed.Metadata["revision"] = "changed";
            db.Store.Execute("BEGIN TRANSACTION");
            var path = FilePath("failure.ball");
            var error = Assert.Throws<DataBallException>(() => db.Store.SaveTo(path, changed));
            Assert.Contains("Overflow", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Current transaction is aborted", error.ToString());
            Assert.Equal(1, Count(db));
            Assert.Empty(db.Query("SELECT database_name FROM duckdb_databases() WHERE starts_with(database_name, 'save_')"));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.False(File.Exists(path + ".tmp.wal"));
            db.Store.Execute("UPDATE _databall SET version = 1");
            db.AddRow(Row(2));
            await db.SaveAsync(path);
            using var reopened = DataBall.Open(path);
            Assert.Equal(2, Count(reopened));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Transaction_DeferredDetachError_DoesNotMaskCommitOrOriginalError(bool fail)
        {
            using var db = new DataBall();
            var original = new InvalidOperationException("original import failure");
            void Body()
            {
                db.SetMetadata("committed", "yes");
                // The current database cannot be detached. Exercise deferred cleanup failure.
                db.Store.Detach((string)db.Store.ExecuteScalar("SELECT current_database()")!);
                if (fail)
                    throw original;
            }
            if (fail)
                Assert.Same(original, Assert.Throws<InvalidOperationException>(() => db.Store.InTransaction(Body)));
            else
            {
                db.Store.InTransaction(Body);
                Assert.Equal("yes", DuckDbStore.DeserializeMetadataValue((string)db.Query("SELECT value FROM meta WHERE key = 'committed'")[0]["value"]!));
            }
            Assert.Single(db.Query("SELECT 1 AS n"));
        }

        [Fact]
        public async Task Open_ReadOnlyImport_ThrowsBeforeChanges()
        {
            var path = Native();
            var source = Native("import.ball");
            using var db = DataBall.Open(path);
            var before = db.Metadata.ToArray();
            var error = await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(source));
            Assert.Equal("Session is read-only; open with writable: true to change it", error.Message);
            Assert.Equal(before, db.Metadata.ToArray());
        }

        public void Dispose()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }
    }
}
