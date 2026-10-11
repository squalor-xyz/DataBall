// SPDX-License-Identifier: Apache-2.0
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace squalor.DataBall
{
    /// <summary>
    /// DuckDB-backed session store. Instances are not thread-safe; one session per owner.
    /// </summary>
    internal sealed partial class DuckDbStore : IDisposable
    {
        private static readonly object FileGate = new();
        private static readonly StringComparer FilePathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private static readonly HashSet<string> OpenFiles = new(FilePathComparer);

        private readonly DuckDBConnection _connection;
        private readonly ILogger _logger;
        private readonly Dictionary<string, object?> _metadata = new(StringComparer.OrdinalIgnoreCase);
        private readonly string? _exclusivePath;
        private readonly bool _temporary;
        private DbTransaction? _currentTx;
        private readonly List<string> _pendingDetach = new();
        private bool _disposed;
        private int _openStreams;
        private bool _isViewBacked;

        internal DuckDbStore(string? databasePath = null, ILogger? logger = null, bool readOnly = false, EngineOptions? engine = null, bool temporary = false)
        {
            _logger = logger ?? NullLogger.Instance;
            IsReadOnly = readOnly;
            engine?.Validate();
            _temporary = temporary;
            if (temporary)
            {
                try
                {
                    var directory = engine?.TempDirectory ?? Path.GetTempPath();
                    Directory.CreateDirectory(directory);
                    databasePath = Path.Combine(directory, $"databall-{Guid.NewGuid():N}.duckdb");
                    databasePath = Path.GetFullPath(databasePath);
                }
                catch (Exception ex)
                {
                    throw new DataBallException("Failed to create temporary store", ex);
                }
            }
            _exclusivePath = ExclusiveFilePath(databasePath);
            if (_exclusivePath is not null)
            {
                lock (FileGate)
                {
                    if (!OpenFiles.Add(_exclusivePath))
                        throw new DataBallException($"Database file is already open: {_exclusivePath}");
                }
            }

            DuckDBConnection? connection = null;
            try
            {
                connection = new DuckDBConnection(BuildConnectionString(databasePath, readOnly, engine));
                connection.Open();
                _connection = connection;
                if (!readOnly)
                    Execute("""
                    CREATE TABLE IF NOT EXISTS "meta" (
                        "key"   VARCHAR PRIMARY KEY,
                        "value" VARCHAR
                    );
                    """);
                HydrateMetadata();
            }
            catch (Exception ex)
            {
                try
                {
                    TrySaveCleanup(() => connection?.Dispose());
                    TrySaveCleanup(DeleteTemporaryStore);
                }
                finally
                {
                    ReleaseExclusivePath();
                }
                if (ex is DataBallException)
                    throw;
                throw new DataBallException("Failed to open database", ex);
            }
        }

        /// <summary>True for an external Parquet view, excluding a configured layout view.</summary>
        internal bool IsViewBacked
        {
            get
            {
                ThrowIfConnectionBusy();
                return _isViewBacked;
            }
        }

        /// <summary>Binds a native external Parquet view; returns false for any other definition.</summary>
        internal bool BindParquetView()
        {
            var sql = (string)Query("SELECT sql FROM duckdb_views() WHERE database_name = current_database() AND view_name = 'data'")[0]["sql"]!;
            _isViewBacked = sql.Contains("read_parquet(", StringComparison.OrdinalIgnoreCase);
            return _isViewBacked;
        }

        /// <summary><c>read_parquet</c> over the data files of a hive directory; partition columns come from the files, not the paths.</summary>
        internal static string ReadHiveParquet(IReadOnlyList<string> files) =>
            $"read_parquet([{string.Join(", ", files.Select(f => QuoteString(Path.GetFullPath(f).Replace('\\', '/'))))}], hive_partitioning = false)";

        internal void AttachParquetView(string path, long sourceKey, IReadOnlyList<string>? hiveFiles = null)
        {
            ThrowIfConnectionBusy();
            var full = Path.GetFullPath(path).Replace('\\', '/');
            var source = Directory.Exists(path)
                ? ReadHiveParquet(hiveFiles ?? HiveFiles.List(path))
                : $"read_parquet({QuoteString(full)})";
            var schema = Query($"DESCRIBE SELECT * FROM {source}");
            var fileColumns = schema.Select(c => (string)c["column_name"]!).ToList();
            var projection = string.Join(", ", schema.Select(c =>
            {
                var raw = (string)c["column_name"]!;
                // An earlier export's _source_file/_source_type arrive as origin_source_*; any other _source* name throws.
                var renamed = Lineage.IncomingName(raw);
                if (!renamed.Equals(raw, StringComparison.Ordinal) && fileColumns.Any(f => f.Equals(renamed, StringComparison.OrdinalIgnoreCase)))
                    throw new DataBallException($"Column '{renamed}' already exists; it is where '{raw}' would go, because source lineage reserves that name");
                var name = QuoteIdent(raw);
                var alias = QuoteIdent(renamed);
                return IsDateType((string)c["column_type"]!) ? $"CAST({name} AS TIMESTAMP) AS {alias}" : (renamed == raw ? name : $"{name} AS {alias}");
            }).Append($"CAST({sourceKey.ToString(CultureInfo.InvariantCulture)} AS BIGINT) AS {QuoteIdent(Lineage.KeyColumn)}"));
            Execute($"CREATE VIEW \"data\" AS SELECT {projection} FROM {source}");
            _isViewBacked = true;
        }

        /// <summary>Copies the external view inside the caller's transaction.</summary>
        internal void MaterializeView()
        {
            ThrowIfConnectionBusy();
            if (!IsViewBacked)
                return;
            if (_currentTx is null)
                throw new DataBallException("Materializing a view requires a transaction");
            var temporary = "materialize_" + Guid.NewGuid().ToString("N");
            Execute($"CREATE TABLE {QuoteIdent(temporary)} AS SELECT * FROM \"data\"");
            Execute("DROP VIEW \"data\"");
            Execute($"ALTER TABLE {QuoteIdent(temporary)} RENAME TO \"data\"");
            _isViewBacked = false;
        }

        internal bool IsReadOnly { get; }

        /// <summary>
        /// The session's <c>metadataPolicy</c>, read when an append reconciles stored metadata with incoming
        /// columns: under <c>first</c> the stored value wins and the incoming column is ignored.
        /// </summary>
        internal Func<string?>? MetadataPolicySource { get; set; }

        internal string? StorePath => _exclusivePath;

        internal bool HasStoredConfig => TableExists("_databall");

        internal Config ReadConfig(string? alias = null)
        {
            ThrowIfDisposed();
            var catalog = alias is null ? "" : QuoteIdent(alias) + ".";
            var exists = Query($"SELECT count(*) AS n FROM information_schema.tables WHERE table_name = '_databall'" +
                (alias is null ? "" : $" AND table_catalog = {QuoteString(alias)}"));
            if (Convert.ToInt64(exists[0]["n"], CultureInfo.InvariantCulture) == 0)
                throw new DataBallException("Native .ball is missing the '_databall' table");
            var rows = Query($"SELECT ball_format, config FROM {catalog}\"_databall\" ORDER BY version DESC LIMIT 1");
            if (rows.Count == 0 || Convert.ToInt32(rows[0]["ball_format"], CultureInfo.InvariantCulture) != 3)
                throw new DataBallException("Unsupported .ball format; expected ball_format = 3");
            return Config.FromJson((string)rows[0]["config"]!);
        }

        internal void WriteConfig(Config config, string? alias = null)
        {
            ThrowIfDisposed();
            var table = (alias is null ? "" : QuoteIdent(alias) + ".") + "\"_databall\"";
            Execute($"""
                CREATE TABLE IF NOT EXISTS {table} (
                    version INTEGER PRIMARY KEY, ball_format INTEGER, config VARCHAR,
                    duckdb_version VARCHAR, storage_version VARCHAR, written_at TIMESTAMP
                )
                """);
            var json = config.ToJson();
            var latest = Query($"SELECT config FROM {table} ORDER BY version DESC LIMIT 1");
            if (latest.Count > 0 && (string)latest[0]["config"]! == json)
                return;
            var catalog = alias ?? Convert.ToString(ExecuteScalar("SELECT current_database()"), CultureInfo.InvariantCulture)!;
            Execute($"""
                INSERT INTO {table}
                SELECT COALESCE((SELECT max(version) FROM {table}), 0) + 1, 3,
                    {QuoteString(json)}, version(),
                    (SELECT tags['storage_version'] FROM duckdb_databases()
                        WHERE database_name = {QuoteString(catalog)}), current_timestamp
                """);
        }

        private void StampWriter(string? alias = null)
        {
            var table = (alias is null ? "" : QuoteIdent(alias) + ".") + "\"_databall\"";
            var catalog = alias ?? Convert.ToString(ExecuteScalar("SELECT current_database()"), CultureInfo.InvariantCulture)!;
            Execute($"""
                UPDATE {table} SET duckdb_version = version(), storage_version =
                    (SELECT tags['storage_version'] FROM duckdb_databases() WHERE database_name = {QuoteString(catalog)}),
                    written_at = current_timestamp
                WHERE version = (SELECT max(version) FROM {table})
                """);
        }

        /// <summary>Test seam: runs inside a copy, after the data is copied and before the file moves into place.</summary>
        internal static Action<string>? AfterCopyForTests;

        internal void SaveTo(string path, Config? config = null, bool link = false)
        {
            ThrowIfConnectionBusy();
            if (_currentTx is not null)
                throw new DataBallException("Cannot save inside a store transaction");
            ThrowIfNothingToSave();
            var target = Path.GetFullPath(path);
            if (FilePathComparer.Equals(target, _exclusivePath))
            {
                if (IsReadOnly)
                    throw new DataBallException("Session is read-only; save to another path or open with writable: true");
                if (!link && IsViewBacked)
                    InTransaction(MaterializeView);
                StampWriter();
                Execute("CHECKPOINT");
                return;
            }
            CopyToFile(target, target + ".tmp", config, link, overwrite: true);
        }

        /// <summary>
        /// Writes a new snapshot generation: a complete copy at <paramref name="target"/>, built under <paramref name="temporary"/>.
        /// The target must not exist.
        /// </summary>
        internal void PublishTo(string target, string temporary, Config config, bool link)
        {
            ThrowIfConnectionBusy();
            if (_currentTx is not null)
                throw new DataBallException("Cannot save inside a store transaction");
            ThrowIfNothingToSave();
            CopyToFile(Path.GetFullPath(target), Path.GetFullPath(temporary), config, link, overwrite: false);
        }

        private void ThrowIfNothingToSave()
        {
            if (!DataTableExists() && _metadata.Count == 0)
                throw new DataBallException("No data to export");
        }

        /// <summary>
        /// Copies the session into <paramref name="temporary"/> and moves it to <paramref name="target"/>.
        /// The target is reserved in <see cref="OpenFiles"/> under <see cref="FileGate"/>, then the gate is released
        /// for the copy, so other sessions' opens, disposes and saves do not wait for it. The reservation ends after the move.
        /// </summary>
        private void CopyToFile(string target, string temporary, Config? config, bool link, bool overwrite)
        {
            lock (FileGate)
            {
                if (OpenFiles.Contains(target))
                    throw new DataBallException("Save target is already open in another session");
                OpenFiles.Add(target);
            }
            try
            {
                CopyCore(target, temporary, config, link, overwrite);
            }
            finally
            {
                lock (FileGate)
                    OpenFiles.Remove(target);
            }
        }

        private void CopyCore(string target, string temporary, Config? config, bool link, bool overwrite)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Delete(temporary);
            File.Delete(temporary + ".wal");
            var alias = "save_" + Guid.NewGuid().ToString("N");
            var attached = false;
            try
            {
                Execute($"ATTACH {QuoteString(temporary)} AS {QuoteIdent(alias)} (READ_WRITE)");
                attached = true;
                var source = Convert.ToString(ExecuteScalar("SELECT current_database()"), CultureInfo.InvariantCulture)!;
                Execute($"COPY FROM DATABASE {QuoteIdent(source)} TO {QuoteIdent(alias)}");
                AfterCopyForTests?.Invoke(target);
                if (!link && IsViewBacked)
                {
                    InTransaction(() =>
                    {
                        var temporaryTable = QuoteIdent("embedded_" + Guid.NewGuid().ToString("N"));
                        Execute($"CREATE TABLE {QuoteIdent(alias)}.{temporaryTable} AS SELECT * FROM {QuoteIdent(source)}.\"data\"");
                        Execute($"DROP VIEW {QuoteIdent(alias)}.\"data\"");
                        Execute($"ALTER TABLE {QuoteIdent(alias)}.{temporaryTable} RENAME TO \"data\"");
                    });
                }
                if (config is not null)
                    WriteConfig(config, alias);
                StampWriter(alias);
                // Read-only overlays live in memory; the saved copy gets the session metadata.
                Execute($"CREATE TABLE IF NOT EXISTS {QuoteIdent(alias)}.meta (key VARCHAR PRIMARY KEY, value VARCHAR)");
                foreach (var (key, value) in _metadata)
                    Execute($"INSERT OR REPLACE INTO {QuoteIdent(alias)}.meta VALUES ({QuoteString(key)}, {QuoteString(SerializeMetadataValue(value))})");
                Execute($"DETACH {QuoteIdent(alias)}");
                attached = false;
                File.Move(temporary, target, overwrite);
            }
            catch
            {
                TrySaveCleanup(() =>
                {
                    using var command = CreateCommand("ROLLBACK", Array.Empty<DuckDBParameter>());
                    try
                    {
                        command.ExecuteNonQuery();
                    }
                    catch (DuckDBException ex) when (ex.Message.Contains("no transaction is active", StringComparison.OrdinalIgnoreCase))
                    {
                        // A failed save may already have ended its implicit transaction.
                    }
                });
                throw;
            }
            finally
            {
                if (attached)
                    TrySaveCleanup(() => Execute($"DETACH {QuoteIdent(alias)}"));
                TrySaveCleanup(() => File.Delete(temporary));
                TrySaveCleanup(() => File.Delete(temporary + ".wal"));
            }
        }

        private void TrySaveCleanup(Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Store cleanup failed");
            }
        }

        internal string AttachReadOnly(string path)
        {
            var alias = "import_" + Guid.NewGuid().ToString("N");
            Execute($"ATTACH {QuotePath(path)} AS {QuoteIdent(alias)} (READ_ONLY)");
            return alias;
        }

        internal void Detach(string alias)
        {
            // DuckDB requires outstanding reads to commit or roll back before DETACH.
            if (_currentTx is not null)
                _pendingDetach.Add(alias);
            else
                Execute($"DETACH {QuoteIdent(alias)}");
        }

        private const string SourceColumnList = "source_key, path, sha256, type, bytes, modified_utc, added_utc";

        /// <summary>The built-in columns of the <c>source</c> table, as they are declared.</summary>
        private const string SourceColumnDefinitions =
            "source_key BIGINT, path VARCHAR, sha256 VARCHAR, type VARCHAR, bytes BIGINT, modified_utc TIMESTAMP, added_utc TIMESTAMP";

        internal bool HasSourceTable() => TableExists(Lineage.Table);

        /// <summary>Creates the <c>source</c> table on first need. Runs inside the caller's transaction.</summary>
        internal void EnsureSourceTable()
        {
            Execute($"CREATE TABLE IF NOT EXISTS \"source\" ({SourceColumnDefinitions})");
        }

        private static readonly string[] BuiltInSourceColumns = { "source_key", "path", "sha256", "type", "bytes", "modified_utc", "added_utc" };

        /// <summary>True for a column the <c>source</c> table always has; a per-source metadata column cannot take its name.</summary>
        internal static bool IsBuiltInSourceColumn(string name) =>
            BuiltInSourceColumns.Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));

        /// <summary>The columns of <c>source</c> beyond the built-in ones: per-source metadata, in table order. Empty without a table.</summary>
        internal IReadOnlyList<(string Name, string DuckDbType)> SourceMetadataColumns()
        {
            if (!HasSourceTable())
                return Array.Empty<(string, string)>();
            return GetColumnsOf(Lineage.Table).Where(c => !IsBuiltInSourceColumn(c.Name)).ToList();
        }

        /// <summary>
        /// The per-source metadata columns that a read of <c>"data"</c> may add: those whose name is not already one of
        /// <paramref name="dataColumns"/> (a row column of the same name wins).
        /// </summary>
        internal IReadOnlyList<(string Name, string DuckDbType)> SourceColumnsNotShadowedBy(IEnumerable<string> dataColumns)
        {
            var taken = new HashSet<string>(dataColumns, StringComparer.OrdinalIgnoreCase);
            return SourceMetadataColumns().Where(c => !taken.Contains(c.Name)).ToList();
        }

        /// <summary>Highest <c>source_key</c> in use; 0 when there is no table or no row.</summary>
        internal long MaxSourceKey()
        {
            if (!HasSourceTable())
                return 0;
            return Convert.ToInt64(ExecuteScalar("SELECT COALESCE(MAX(source_key), 0) FROM \"source\""), CultureInfo.InvariantCulture);
        }

        /// <summary>Forgets every source (a replacing import starts the keys over). The table stays, without per-source columns.</summary>
        internal void ResetSources()
        {
            if (!HasSourceTable())
                return;
            if (SourceMetadataColumns().Count > 0)
            {
                // Per-source columns belong to the sources being forgotten, and DuckDB cannot alter a table it has deleted from in the same
                // transaction: start over with a fresh table.
                Execute("DROP TABLE \"source\"");
                EnsureSourceTable();
                return;
            }
            Execute("DELETE FROM \"source\"");
        }

        /// <summary>Records <paramref name="record"/> under the next key and returns that key.</summary>
        internal long AddSource(SourceRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            EnsureSourceTable();
            var key = MaxSourceKey() + 1;
            InsertSource(key, record.Path, record.Sha256, record.Type, record.Bytes, record.ModifiedUtc, DateTime.UtcNow);
            return key;
        }

        private void InsertSource(long key, string path, string? sha256, string type, long bytes, DateTime? modifiedUtc, DateTime addedUtc)
        {
            ExecuteParameterized(
                $"INSERT INTO \"source\" ({SourceColumnList}) VALUES ($key, $path, $sha, $type, $bytes, $modified, $added)",
                Param("key", key), Param("path", path), Param("sha", sha256), Param("type", type),
                Param("bytes", bytes), Param("modified", modifiedUtc), Param("added", addedUtc));
        }

        internal IReadOnlyList<SourceInfo> ReadSources()
        {
            ThrowIfDisposed();
            if (!HasSourceTable())
                return Array.Empty<SourceInfo>();
            var rows = Query($"SELECT {SourceColumnList} FROM \"source\" ORDER BY source_key");
            var result = new List<SourceInfo>(rows.Count);
            foreach (var row in rows)
            {
                result.Add(new SourceInfo(
                    Convert.ToInt64(row["source_key"], CultureInfo.InvariantCulture),
                    (string)row["path"]!,
                    row["sha256"] as string,
                    (string)row["type"]!,
                    Convert.ToInt64(row["bytes"], CultureInfo.InvariantCulture),
                    row["modified_utc"] is DateTime modified ? DateTime.SpecifyKind(modified, DateTimeKind.Utc) : null,
                    DateTime.SpecifyKind((DateTime)row["added_utc"]!, DateTimeKind.Utc)));
            }
            return result;
        }

        /// <summary>
        /// Renames the lineage-looking columns of a staged file (<c>_source_file</c> and <c>_source_type</c> become
        /// <c>origin_source_*</c>) and throws for any other <c>_source*</c> column. <paramref name="allowKey"/> lets a
        /// native <c>.ball</c>'s own <c>_source_key</c> through.
        /// </summary>
        private void NormalizeIncomingColumns(string table, bool allowKey)
        {
            var columns = GetColumnsOf(table);
            foreach (var (name, _) in columns)
            {
                if (!Lineage.IsReserved(name) || (allowKey && name.Equals(Lineage.KeyColumn, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var target = Lineage.IncomingName(name);
                if (columns.Any(c => c.Name.Equals(target, StringComparison.OrdinalIgnoreCase)))
                    throw new DataBallException($"Column '{target}' already exists; it is where '{name}' would go, because source lineage reserves that name");
                Execute($"ALTER TABLE {QuoteIdent(table)} RENAME COLUMN {QuoteIdent(name)} TO {QuoteIdent(target)}");
            }
        }

        /// <summary>
        /// Gives every row of the TEMP staging table <paramref name="table"/> the key of the source that
        /// <paramref name="recordSource"/> records. An input's <c>_source*</c> columns are renamed (<c>_source_file</c> and <c>_source_type</c> become <c>origin_source_*</c>) or rejected.
        /// </summary>
        private void AttachSourceKey(string table, Func<long> recordSource)
        {
            NormalizeIncomingColumns(table, allowKey: false);
            AddSourceKeyColumn(table, recordSource());
        }

        private void AddSourceKeyColumn(string table, long key)
        {
            Execute($"ALTER TABLE {QuoteIdent(table)} ADD COLUMN {QuoteIdent(Lineage.KeyColumn)} BIGINT DEFAULT {key.ToString(CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Brings the <paramref name="sources"/> of another session in under keys that follow this
        /// session's, and moves the staged rows' <c>_source_key</c> by the same offset. All of the other
        /// session's sources are copied, including ones no row references, whenever the staging table has
        /// a <c>_source_key</c> column. Without sources behind the keys, the keys become NULL; a staging table
        /// without the column is left alone.
        /// </summary>
        internal void RemapStagingSources(string table, IReadOnlyList<SourceInfo> sources)
        {
            var staged = GetColumnsOf(table).Any(c => c.Name.Equals(Lineage.KeyColumn, StringComparison.OrdinalIgnoreCase));
            var q = QuoteIdent(table);
            if (!staged)
                return;
            if (sources.Count == 0)
            {
                Execute($"UPDATE {q} SET {QuoteIdent(Lineage.KeyColumn)} = NULL");
                return;
            }
            var offset = MaxSourceKey();
            EnsureSourceTable();
            foreach (var s in sources.OrderBy(s => s.Key))
                InsertSource(s.Key + offset, s.Path, s.Sha256, s.Type, s.Bytes, s.ModifiedUtc, s.AddedUtc);
            Execute($"UPDATE {q} SET {QuoteIdent(Lineage.KeyColumn)} = {QuoteIdent(Lineage.KeyColumn)} + {offset.ToString(CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Native import: copies the attached file's <c>source</c> rows under new keys and moves the
        /// staged rows' <c>_source_key</c> to match. A file with no <c>source</c> table gives NULL keys.
        /// </summary>
        private void BringNativeSources(string alias, string table)
        {
            var q = QuoteIdent(table);
            var k = QuoteIdent(Lineage.KeyColumn);
            var staged = GetColumnsOf(table).Any(c => c.Name.Equals(Lineage.KeyColumn, StringComparison.OrdinalIgnoreCase));
            var hasSource = Convert.ToInt64(
                ExecuteScalar($"SELECT count(*) FROM information_schema.tables WHERE table_catalog = {QuoteString(alias)} AND table_name = {QuoteString(Lineage.Table)}"),
                CultureInfo.InvariantCulture) > 0;
            if (!hasSource || !staged)
            {
                // No lineage in the file: every row gets a NULL key, and no source row comes along.
                if (staged)
                    Execute($"UPDATE {q} SET {k} = NULL");
                else
                    Execute($"ALTER TABLE {q} ADD COLUMN {k} BIGINT");
                return;
            }

            var offset = MaxSourceKey();
            EnsureSourceTable();
            var shift = offset.ToString(CultureInfo.InvariantCulture);
            Execute($"""
                INSERT INTO "source" ({SourceColumnList})
                SELECT source_key + {shift}, path, sha256, type, bytes, modified_utc, added_utc
                FROM {QuoteIdent(alias)}."source" ORDER BY source_key
                """);
            Execute($"UPDATE {q} SET {k} = {k} + {shift}");
        }

        /// <summary>
        /// Native <c>.ball</c> rows. <paramref name="configTypes"/> names the session's configured columns, which keep
        /// their type on append (cast, never widened), as they do for CSV and Parquet.
        /// </summary>
        internal void ImportNativeRows(string alias, bool append, IReadOnlyDictionary<string, Type>? configTypes = null, string? incomingName = null)
        {
            var exists = ExecuteScalar($"SELECT count(*) FROM information_schema.tables WHERE table_catalog = {QuoteString(alias)} AND table_name = 'data'");
            if (Convert.ToInt64(exists, CultureInfo.InvariantCulture) == 0)
            {
                if (!append)
                    DropDataRelation();
                return;
            }
            Execute($"CREATE OR REPLACE TEMP TABLE \"_staging\" AS SELECT * FROM {QuoteIdent(alias)}.\"data\"");
            try
            {
                PromoteDateColumns("_staging");
                NormalizeIncomingColumns("_staging", allowKey: true);
                BringNativeSources(alias, "_staging");
                MergeOrAppendFromTable("_staging", append, incomingName ?? "the appended rows", configTypes);
            }
            finally
            {
                DropTemp("_staging");
            }
        }

        internal void ImportNativeMetadata(string alias)
        {
            var exists = ExecuteScalar($"SELECT count(*) FROM information_schema.tables WHERE table_catalog = {QuoteString(alias)} AND table_name = 'meta'");
            if (Convert.ToInt64(exists, CultureInfo.InvariantCulture) == 0)
                return;
            foreach (var row in Query($"SELECT key, value FROM {QuoteIdent(alias)}.\"meta\""))
                SetMetadata((string)row["key"]!, row["value"] is string value ? DeserializeMetadataValue(value) : null);
        }

        private static string? ExclusiveFilePath(string? databasePath)
        {
            if (string.IsNullOrEmpty(databasePath)
                || databasePath.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
                return null;
            return Path.GetFullPath(databasePath);
        }

        private void ReleaseExclusivePath()
        {
            if (_exclusivePath is null)
                return;
            lock (FileGate)
                OpenFiles.Remove(_exclusivePath);
        }

        private void DeleteTemporaryStore()
        {
            if (!_temporary || _exclusivePath is null)
                return;
            TrySaveCleanup(() => File.Delete(_exclusivePath));
            TrySaveCleanup(() => File.Delete(_exclusivePath + ".wal"));
            TrySaveCleanup(() =>
            {
                var spill = _exclusivePath + ".tmp";
                if (Directory.Exists(spill))
                    Directory.Delete(spill, recursive: true);
            });
        }

        private static string BuildConnectionString(string? databasePath, bool readOnly, EngineOptions? engine)
        {
            var builder = new DuckDBConnectionStringBuilder
            {
                DataSource = ExclusiveFilePath(databasePath) ?? ":memory:"
            };
            if (readOnly)
                builder["ACCESS_MODE"] = "READ_ONLY";
            if (engine?.MemoryLimit is not null)
                builder["memory_limit"] = engine.MemoryLimit;
            if (engine?.Threads is not null)
                builder["threads"] = engine.Threads.Value.ToString(CultureInfo.InvariantCulture);
            if (engine?.TempDirectory is not null)
                builder["temp_directory"] = engine.TempDirectory;
            return builder.ConnectionString;
        }

        private void HydrateMetadata()
        {
            if (!TableExists("meta"))
                return;
            var rows = Query("SELECT \"key\", \"value\" FROM \"meta\"");
            foreach (var row in rows)
            {
                var key = Convert.ToString(row["key"], CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(key))
                    continue;
                var stored = row["value"] as string;
                object? value = stored is null ? null : DeserializeMetadataValue(stored);
                _metadata[key] = value;
            }
        }

        internal DuckDBConnection Connection
        {
            get
            {
                ThrowIfConnectionBusy();
                return _connection;
            }
        }

        internal static string QuoteIdent(string name)
        {
            return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        internal static string QuoteString(string value)
        {
            return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        }

        internal static string ToDuckDbType(Type clr)
        {
            clr = Nullable.GetUnderlyingType(clr) ?? clr;
            if (clr == typeof(int)) return "INTEGER";
            if (clr == typeof(long)) return "BIGINT";
            if (clr == typeof(float)) return "REAL";
            if (clr == typeof(double)) return "DOUBLE";
            if (clr == typeof(bool)) return "BOOLEAN";
            if (clr == typeof(DateTime)) return "TIMESTAMP";
            if (clr == typeof(string)) return "VARCHAR";
            throw new DataBallException($"Unsupported column type: {clr.FullName}");
        }

        internal static Type ClrTypeOf(object? value)
        {
            value = Unwrap(value);
            return value switch
            {
                null => typeof(string),
                int => typeof(int),
                long => typeof(long),
                float => typeof(float),
                double => typeof(double),
                bool => typeof(bool),
                DateTime => typeof(DateTime),
                DateOnly => typeof(DateTime),
                string => typeof(string),
                _ => throw new DataBallException($"Unsupported value type: {value.GetType().Name}")
            };
        }

        internal static object? Unwrap(object? value)
        {
            return value is JsonElement element ? UnwrapJson(element) : value;
        }

        internal void InTransaction(Action body)
        {
            ArgumentNullException.ThrowIfNull(body);
            ThrowIfDisposed();
            if (_currentTx is not null)
            {
                // Nested call joins the outer transaction; the outermost caller commits or rolls back.
                body();
                return;
            }

            ThrowIfConnectionBusy();
            using var tx = _connection.BeginTransaction();
            var previous = _currentTx;
            var wasViewBacked = _isViewBacked;
            _currentTx = tx;
            try
            {
                body();
                tx.Commit();
            }
            catch
            {
                try
                {
                    tx.Rollback();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Rollback failed");
                }
                _isViewBacked = wasViewBacked;
                throw;
            }
            finally
            {
                _currentTx = previous;
                var pending = _pendingDetach.ToArray();
                _pendingDetach.Clear();
                foreach (var alias in pending)
                {
                    // ATTACH itself is undone by a rollback.
                    try
                    {
                        var exists = ExecuteScalar($"SELECT count(*) FROM duckdb_databases() WHERE database_name = {QuoteString(alias)}");
                        if (Convert.ToInt64(exists, CultureInfo.InvariantCulture) > 0)
                            Execute($"DETACH {QuoteIdent(alias)}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Deferred detach failed");
                    }
                }
            }
        }

        internal void Execute(string sql)
        {
            ThrowIfDisposed();
            try
            {
                using var cmd = CreateCommand(sql, []);
                cmd.ExecuteNonQuery();
            }
            catch (DuckDBException ex)
            {
                throw new DataBallException("DuckDB operation failed", ex);
            }
        }

        internal object? ExecuteScalar(string sql)
        {
            ThrowIfDisposed();
            try
            {
                using var cmd = CreateCommand(sql, []);
                var result = cmd.ExecuteScalar();
                return result is DBNull ? null : result;
            }
            catch (DuckDBException ex)
            {
                throw new DataBallException("DuckDB operation failed", ex);
            }
        }

        internal List<Dictionary<string, object?>> Query(string sql, params DuckDBParameter[] parameters)
        {
            ThrowIfDisposed();
            try
            {
                using var cmd = CreateCommand(sql, parameters ?? []);
                using var reader = cmd.ExecuteReader();
                return ReadRows(reader);
            }
            catch (DuckDBException ex)
            {
                throw new DataBallException("DuckDB operation failed", ex);
            }
        }

        internal ColumnSet ReadColumns(ParameterizedSql select)
        {
            using var countCommand = CreateCommand($"SELECT COUNT(*) FROM ({select.Sql}) AS matching", select.Parameters);
            var count = Convert.ToInt64(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (count > int.MaxValue)
                throw new DataBallException("ReadColumns cannot allocate arrays above int.MaxValue rows (2,147,483,647)");
            var length = (int)count;
            var projections = new List<string>();
            var needsTextCast = false;
            using (var schemaCommand = CreateCommand($"SELECT * FROM ({select.Sql}) AS q LIMIT 0", select.Parameters))
            using (var schemaReader = schemaCommand.ExecuteReader())
            {
                for (var i = 0; i < schemaReader.FieldCount; i++)
                {
                    var name = schemaReader.GetName(i);
                    var identifier = QuoteIdent(name);
                    var sqlType = schemaReader.GetDataTypeName(i);
                    var textCast = ReadTypeOf(sqlType) == typeof(string) && NormalizeType(sqlType) != "VARCHAR";
                    needsTextCast |= textCast;
                    projections.Add(textCast
                        ? $"CAST(q.{identifier} AS VARCHAR) AS {identifier}"
                        : $"q.{identifier}");
                }
            }
            var readSql = needsTextCast
                ? $"SELECT {string.Join(", ", projections)} FROM ({select.Sql}) AS q"
                : select.Sql;
            using var command = CreateCommand(readSql, select.Parameters);
            using var reader = command.ExecuteReader();
            var columns = new ColumnData[reader.FieldCount];
            for (var i = 0; i < columns.Length; i++)
            {
                var name = reader.GetName(i);
                try
                {
                    var type = ReadTypeOf(reader.GetDataTypeName(i));
                    columns[i] = type == typeof(int) ? new ColumnData<int>(name, length, fieldType: reader.GetFieldType(i))
                        : type == typeof(long) ? new ColumnData<long>(name, length, fieldType: reader.GetFieldType(i))
                        : type == typeof(float) ? new ColumnData<float>(name, length, fieldType: reader.GetFieldType(i))
                        : type == typeof(double) ? new ColumnData<double>(name, length, fieldType: reader.GetFieldType(i))
                        : type == typeof(bool) ? new ColumnData<bool>(name, length, fieldType: reader.GetFieldType(i))
                        : type == typeof(DateTime) ? new ColumnData<DateTime>(name, length, reader.GetFieldType(i) == typeof(DateOnly), reader.GetFieldType(i))
                        : new ColumnData<string>(name, length, fieldType: reader.GetFieldType(i));
                }
                catch (Exception ex)
                {
                    throw new DataBallException($"Cannot read column '{name}'", ex);
                }
            }
            var row = 0;
            while (reader.Read())
            {
                for (var i = 0; i < columns.Length; i++)
                    columns[i].ReadValue(reader, i, row);
                row++;
            }
            return new ColumnSet(count, columns);
        }

        internal IEnumerable<IReadOnlyDictionary<string, object?>> StreamRows(
            ParameterizedSql select, CancellationToken cancellationToken)
        {
            using var command = CreateCommand(select.Sql, select.Parameters);
            command.UseStreamingMode = true;
            _openStreams++;
            try
            {
                using var reader = command.ExecuteReader();
                while (true)
                {
                    ThrowIfDisposed();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!reader.Read())
                        yield break;
                    var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        object? value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        if (value is DateOnly date)
                            value = date.ToDateTime(TimeOnly.MinValue);
                        row[reader.GetName(i)] = value;
                    }
                    yield return row;
                }
            }
            finally
            {
                _openStreams--;
            }
        }

        internal bool DataTableExists()
        {
            ThrowIfDisposed();
            var result = ExecuteScalar("""
                SELECT COUNT(*) FROM information_schema.tables
                WHERE table_catalog = current_database() AND table_schema = 'main' AND table_name = 'data'
                """);
            return Convert.ToInt64(result, CultureInfo.InvariantCulture) > 0;
        }

        internal IReadOnlyList<(string Name, string DuckDbType)> GetColumns()
        {
            return GetColumnsOf("data");
        }

        internal long RowCount()
        {
            ThrowIfDisposed();
            if (!DataTableExists())
                return 0;
            var result = ExecuteScalar("SELECT COUNT(*) FROM \"data\"");
            return Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }

        internal void EnsureDataTable(string firstColumnName, string duckDbType)
        {
            ThrowIfDisposed();
            ValidateName(firstColumnName, "Column");
            if (DataTableExists())
                return;
            Execute($"CREATE TABLE \"data\" ({QuoteIdent(firstColumnName)} {duckDbType})");
        }

        internal void EnsureColumn(string name, Type clrType)
        {
            ThrowIfDisposed();
            ValidateName(name, "Column");
            ArgumentNullException.ThrowIfNull(clrType);
            var sqlType = ToDuckDbType(clrType);
            if (Layout is not null)
                return; // the routed AddRow grows the layout schema inside its own transaction
            if (!DataTableExists())
            {
                Execute($"CREATE TABLE \"data\" ({QuoteIdent(name)} {sqlType})");
                return;
            }

            if (GetColumns().Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return;

            Execute($"ALTER TABLE \"data\" ADD COLUMN {QuoteIdent(name)} {sqlType}");
        }

        internal void AddColumn<T>(string name, IReadOnlyList<T> values)
        {
            ThrowIfDisposed();
            ValidateName(name, "Column");
            ArgumentNullException.ThrowIfNull(values);

            var sqlType = ToDuckDbType(typeof(T));
            var qname = QuoteIdent(name);
            var n = values.Count;

            if (!DataTableExists())
            {
                Execute($"CREATE TABLE \"data\" ({qname} {sqlType})");
                if (n > 0)
                    AppendSingleColumn(values);
                return;
            }

            if (GetColumns().Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new DataBallException($"Column '{name}' already exists");

            var existing = RowCount();
            if (n != existing)
                throw new DataBallException($"Column '{name}' length {n} does not match row count {existing}");

            InTransaction(() =>
            {
                Execute($"ALTER TABLE \"data\" ADD COLUMN {qname} {sqlType}");
                if (n == 0)
                    return;

                Execute("DROP TABLE IF EXISTS \"_addcol\"");
                Execute($"CREATE TEMP TABLE \"_addcol\" (\"rid\" BIGINT, \"v\" {sqlType})");
                try
                {
                    using (var appender = CreateAppender("_addcol"))
                    {
                        for (int i = 0; i < n; i++)
                        {
                            var row = appender.CreateRow();
                            row.AppendValue((long)i);
                            AppendClr(row, values[i]);
                            row.EndRow();
                        }
                    }

                    // values[i] attaches to remaining rows in rowid order (insertion
                    // order; stable across DELETE of other rows). Unordered
                    // row_number() OVER () is not a DuckDB promise under a parallel
                    // scan. rowid is this table's row identity until CREATE OR REPLACE
                    // / vacuum, not a warehouse key.
                    Execute($"""
                        UPDATE "data" SET {qname} = "_addcol"."v"
                        FROM "_addcol",
                        (
                            SELECT row_number() OVER (ORDER BY rowid) - 1 AS "_pos", rowid AS "_rid"
                            FROM "data"
                        ) "_ord"
                        WHERE "data".rowid = "_ord"."_rid"
                          AND "_addcol"."rid" = "_ord"."_pos"
                        """);
                }
                finally
                {
                    // Best effort: inside a joined transaction that an error aborted, plain SQL here would hide the original exception.
                    DropTemp("_addcol");
                }
            });
        }

        internal void RemoveColumn(string name)
        {
            ThrowIfDisposed();
            ValidateName(name, "Column");
            if (!DataTableExists())
                throw new DataBallException($"Column '{name}' does not exist");

            var cols = GetColumns();
            var match = cols.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match.Name is null)
                throw new DataBallException($"Column '{name}' does not exist");

            if (IsLastDataColumn(cols, match.Name))
                Execute("DROP TABLE \"data\"");
            else
                Execute($"ALTER TABLE \"data\" DROP COLUMN {QuoteIdent(match.Name)}");
        }

        internal void AddRow(
            IReadOnlyDictionary<string, object?> values,
            IReadOnlyDictionary<string, Type>? expectedTypes = null)
        {
            ThrowIfDisposed();
            if (values is null)
                throw new DataBallException("Row values are required");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in values.Keys)
            {
                ValidateName(key, "Column");
                if (!seen.Add(key))
                    throw new DataBallException($"Duplicate column name '{key}'");
            }

            if (Layout is not null)
            {
                // An empty row is a spine row with no values: stage one NULL for a wide column.
                var staged = values.Count == 0
                    ? new Dictionary<string, object?> { [Layout.WideColumns[0]] = null }
                    : values;
                AddRows(new[] { staged }, expectedTypes);
                return;
            }

            InTransaction(() =>
            {
                MaterializeView();
                if (!DataTableExists())
                {
                    if (values.Count == 0)
                        throw new DataBallException("AddRow requires at least one column");

                    var parts = new List<string>(values.Count);
                    foreach (var key in values.Keys)
                        parts.Add($"{QuoteIdent(key)} {ToDuckDbType(ResolveColumnType(key, values[key], expectedTypes))}");
                    Execute($"CREATE TABLE \"data\" ({string.Join(", ", parts)})");
                    InsertAppenderRow(GetColumns(), values);
                    return;
                }

                var cols = GetColumns();
                foreach (var key in values.Keys)
                {
                    if (cols.Any(c => c.Name.Equals(key, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    Execute($"ALTER TABLE \"data\" ADD COLUMN {QuoteIdent(key)} {ToDuckDbType(ResolveColumnType(key, values[key], expectedTypes))}");
                }

                InsertAppenderRow(GetColumns(), values);
            });
        }

        internal void AddRows(
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
            IReadOnlyDictionary<string, Type>? expectedTypes = null)
        {
            ThrowIfDisposed();
            if (rows.Count == 0)
                return;

            var keyOrder = RowColumnOrder(rows);

            if (keyOrder.Count == 0)
                throw new DataBallException("AddRows requires at least one column");

            if (Layout is not null)
            {
                AddRowsIntoLayout(rows, keyOrder, expectedTypes);
                return;
            }

            InTransaction(() =>
            {
                MaterializeView();
                AppendRowsTo("data", string.Empty, rows, keyOrder, expectedTypes);
            });
        }

        /// <summary>The column names of <paramref name="rows"/> in order of first appearance, validated: a name per row at most once.</summary>
        private static List<string> RowColumnOrder(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        {
            var keyOrder = new List<string>();
            var keySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var values in rows)
            {
                if (values is null)
                    throw new DataBallException("Row values are required");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var key in values.Keys)
                {
                    ValidateName(key, "Column");
                    if (!seen.Add(key))
                        throw new DataBallException($"Duplicate column name '{key}'");
                    if (keySet.Add(key))
                        keyOrder.Add(key);
                }
            }
            return keyOrder;
        }

        /// <summary>
        /// Appends <paramref name="rows"/> to the base table <paramref name="table"/> (<c>"data"</c>, or a handler source's staging table),
        /// creating it as <paramref name="createKind"/> (empty or <c>TEMP </c>) from their columns, adding the columns it lacks, and giving an
        /// all-null VARCHAR column the type of a later typed value. Values are coerced to the column types.
        /// </summary>
        private void AppendRowsTo(
            string table,
            string createKind,
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
            IReadOnlyList<string> keyOrder,
            IReadOnlyDictionary<string, Type>? expectedTypes)
        {
            var qTable = QuoteIdent(table);
            if (!TableExists(table))
            {
                var parts = new List<string>(keyOrder.Count);
                foreach (var key in keyOrder)
                    parts.Add($"{QuoteIdent(key)} {ToDuckDbType(ResolveColumnType(key, FirstValue(rows, key), expectedTypes))}");
                Execute($"CREATE {createKind}TABLE {qTable} ({string.Join(", ", parts)})");
            }
            else
            {
                var cols = GetColumnsOf(table);
                foreach (var key in keyOrder)
                {
                    var existing = cols.FirstOrDefault(c => c.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (existing.Name is not null)
                    {
                        var widened = InferredNullColumnType(table, existing, FirstValue(rows, key), expectedTypes);
                        if (widened is not null)
                            Execute($"ALTER TABLE {qTable} ALTER COLUMN {QuoteIdent(existing.Name)} SET DATA TYPE {widened}");
                        continue;
                    }
                    Execute($"ALTER TABLE {qTable} ADD COLUMN {QuoteIdent(key)} {ToDuckDbType(ResolveColumnType(key, FirstValue(rows, key), expectedTypes))}");
                }
            }

            var columns = GetColumnsOf(table);
            var coercedRows = new List<object?[]>(rows.Count);
            foreach (var values in rows)
                coercedRows.Add(CoerceAppenderValues(columns, values));

            using var appender = CreateAppender(table);
            foreach (var coerced in coercedRows)
                WriteCoercedAppenderRow(appender, coerced);
        }

        internal IReadOnlyDictionary<string, object?> SnapshotMetadata()
        {
            ThrowIfDisposed();
            return new Dictionary<string, object?>(_metadata, StringComparer.OrdinalIgnoreCase);
        }

        internal void SetMetadata(string key, object? value)
        {
            ThrowIfConnectionBusy();
            ValidateName(key, "Metadata key");
            var unwrapped = Unwrap(value);
            _metadata[key] = unwrapped;
            if (IsReadOnly)
                return;
            var storedKey = _metadata.Keys.First(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            var json = SerializeMetadataValue(unwrapped);
            ExecuteParameterized(
                "INSERT OR REPLACE INTO \"meta\" (\"key\", \"value\") VALUES ($key, $value)",
                Param("key", storedKey),
                Param("value", json));
        }

        internal void LoadMetadata(IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(pairs);
            foreach (var pair in pairs)
                SetMetadata(pair.Key, pair.Value);
        }

        internal void RemoveMetadata(string key)
        {
            ThrowIfDisposed();
            ValidateName(key, "Metadata key");
            if (!_metadata.TryGetValue(key, out _))
                return;
            if (IsReadOnly)
                return;
            var storedKey = _metadata.Keys.First(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            _metadata.Remove(storedKey);
            ExecuteParameterized("DELETE FROM \"meta\" WHERE \"key\" = $key", Param("key", storedKey));
        }

        internal void MergeOrAppendFromTable(
            string sourceTable,
            bool append,
            string incomingName = "the appended rows",
            IReadOnlyDictionary<string, Type>? configTypes = null)
        {
            ThrowIfDisposed();
            ValidateName(sourceTable, "Table");
            var qSrc = QuoteIdent(sourceTable);

            if (Layout is not null && append)
            {
                AppendStagingIntoLayout(sourceTable, incomingName, configTypes);
                return;
            }

            if (!append || !DataTableExists() || RowCount() == 0)
            {
                InTransaction(() =>
                {
                    Execute("DROP TABLE IF EXISTS \"data\"");
                    Execute($"CREATE TABLE \"data\" AS SELECT * FROM {qSrc}");
                });
                return;
            }

            try
            {
                InTransaction(() => AppendFromTable(sourceTable, qSrc, incomingName, configTypes));
            }
            catch
            {
                // ApplyMetadataAgainstSource mutated _metadata inside the transaction; reload once it is rolled back.
                if (_currentTx is null)
                    ReloadMetadata();
                throw;
            }
        }

        private void AppendFromTable(string sourceTable, string qSrc, string incomingName, IReadOnlyDictionary<string, Type>? configTypes)
        {
            var sourceRowCount = RowCountOf(sourceTable);
            // Every type merge is decided before the first change, so a conflict leaves the session as it was.
            var widen = PlanColumnWidening(GetColumns(), GetColumnsOf(sourceTable), sourceTable, incomingName, configTypes, _ => "data");
            var backfills = ApplyMetadataAgainstSource(sourceTable);

            var srcCols = GetColumnsOf(sourceTable);
            var dstCols = GetColumns();
            var dstByName = dstCols.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            // A new column that is a per-source column of the source table: the rows already in "data" take their source's value.
            var perSource = dstByName.ContainsKey(Lineage.KeyColumn)
                ? new HashSet<string>(SourceMetadataColumns().Select(c => c.Name), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>();
            var fromSource = new List<(string Name, string Type)>();

            foreach (var (srcName, srcType) in srcCols)
            {
                if (dstByName.ContainsKey(srcName))
                    continue;
                Execute($"ALTER TABLE \"data\" ADD COLUMN {QuoteIdent(srcName)} {srcType}");
                dstByName[srcName] = (srcName, srcType);
                if (perSource.Contains(srcName))
                    fromSource.Add((srcName, srcType));
            }

            // Widened columns take the wider type in place; their values insert without a cast.
            var widened = new HashSet<string>(widen.Select(w => w.Column), StringComparer.OrdinalIgnoreCase);
            foreach (var (column, _, target) in widen)
                Execute($"ALTER TABLE \"data\" ALTER COLUMN {QuoteIdent(column)} SET DATA TYPE {target}");

            // Every ALTER comes first: DuckDB fails the commit when an ALTER follows an UPDATE of the same table.
            foreach (var (column, value) in backfills)
                ExecuteParameterized($"UPDATE \"data\" SET {QuoteIdent(column)} = $v", Param("v", value));
            foreach (var (column, type) in fromSource)
                BackfillFromSource("\"data\"", column, type);

            if (srcCols.Count == 0)
            {
                InsertNullRows(sourceRowCount);
                return;
            }

            var selectParts = new List<string>(srcCols.Count);
            foreach (var (srcName, srcType) in srcCols)
            {
                var q = QuoteIdent(srcName);
                if (!widened.Contains(srcName) &&
                    dstByName.TryGetValue(srcName, out var dst) &&
                    NormalizeType(dst.DuckDbType) != NormalizeType(srcType))
                {
                    selectParts.Add($"CAST({q} AS {dst.DuckDbType}) AS {q}");
                }
                else
                {
                    selectParts.Add(q);
                }
            }

            Execute($"INSERT INTO \"data\" BY NAME SELECT {string.Join(", ", selectParts)} FROM {qSrc}");
        }

        /// <summary>
        /// Fills a new column of <paramref name="qTable"/> (<c>"data"</c> or a layout spine table) for the rows it already holds, from the
        /// per-source column of the same name on <c>source</c>, by <c>_source_key</c>. Runs after every ALTER of the append and before its rows are inserted.
        /// </summary>
        private void BackfillFromSource(string qTable, string column, string type)
        {
            var q = QuoteIdent(column);
            var key = QuoteIdent(Lineage.KeyColumn);
            Execute($"UPDATE {qTable} SET {q} = CAST(s.{q} AS {type}) FROM \"source\" AS s WHERE {qTable}.{key} = s.\"source_key\"");
        }

        /// <summary>
        /// The type a column holds after an incoming column of type <paramref name="incoming"/> is appended
        /// into it: the existing type when nothing changes (equal types, or a value that casts into the
        /// existing type), the wider of two integer types, DOUBLE for an integer or FLOAT meeting FLOAT or
        /// DOUBLE, or TIMESTAMP for DATE meeting TIMESTAMP. Null for any other conflict.
        /// </summary>
        internal static string? MergedColumnType(string existing, string incoming)
        {
            var e = NormalizeType(existing);
            var i = NormalizeType(incoming);
            if (e == i)
                return existing;
            var existingRank = IntegerRank(e);
            var incomingRank = IntegerRank(i);
            if (existingRank > 0 && incomingRank > 0)
                return incomingRank > existingRank ? i : existing;
            if (e == "DOUBLE" && (incomingRank > 0 || i == "FLOAT"))
                return existing;
            if (i == "DOUBLE" && (existingRank > 0 || e == "FLOAT"))
                return "DOUBLE";
            if ((existingRank > 0 && i == "FLOAT") || (e == "FLOAT" && incomingRank > 0))
                return "DOUBLE";
            if (e == "DATE" && i == "TIMESTAMP")
                return "TIMESTAMP";
            if (e == "TIMESTAMP" && i == "DATE")
                return existing;
            return null;
        }

        /// <summary>What happens to a column when an incoming column of another type joins it.</summary>
        internal enum ColumnMerge
        {
            /// <summary>The column keeps its type; the incoming values cast into it, or are empty.</summary>
            Keep,

            /// <summary>The column takes the type given with the decision.</summary>
            Become,

            /// <summary>The types cannot merge.</summary>
            Conflict,
        }

        /// <summary>
        /// The one decision of the merge ladder, for an append (<see cref="PlanColumnWidening"/>) and for the sources of a many-file open
        /// (<c>PlanStagedSchema</c>) alike: values that cast into the existing type keep it; an incoming column with no value
        /// (the reader types an all-empty column VARCHAR) keeps it; types that do not merge are a conflict unless the existing column is empty,
        /// which adopts the incoming type; otherwise the column takes the merged type. <paramref name="incomingEmpty"/> and
        /// <paramref name="existingEmpty"/> are asked only when needed. The caller has handled equal types.
        /// </summary>
        internal static (ColumnMerge Kind, string Type) DecideColumnMerge(
            string existing, string incoming, Func<bool> incomingEmpty, Func<bool> existingEmpty)
        {
            var merged = MergedColumnType(existing, incoming);
            if (merged is not null && NormalizeType(merged) == NormalizeType(existing))
                return (ColumnMerge.Keep, existing);
            if (incomingEmpty())
                return (ColumnMerge.Keep, existing);
            if (merged is null)
                return existingEmpty() ? (ColumnMerge.Become, incoming) : (ColumnMerge.Conflict, existing);
            return (ColumnMerge.Become, merged);
        }

        private static int IntegerRank(string normalized) => normalized switch
        {
            "TINYINT" => 1,
            "SMALLINT" => 2,
            "INTEGER" => 3,
            "BIGINT" => 4,
            _ => 0,
        };

        /// <summary>
        /// The widenings an append needs: each incoming column that matches an existing column by name
        /// (case-insensitive) and merges into a wider type. An incoming column with no non-null value in
        /// <paramref name="sourceTable"/> adopts the existing type. Throws for the first column whose type
        /// cannot merge. Reads only. <c>_source_key</c> is lineage and is never merged.
        /// </summary>
        private List<(string Column, string From, string To)> PlanColumnWidening(
            IReadOnlyList<(string Name, string DuckDbType)> existing,
            IEnumerable<(string Name, string DuckDbType)> incoming,
            string sourceTable,
            string incomingName,
            IReadOnlyDictionary<string, Type>? configTypes,
            Func<string, string> ownerOf)
        {
            var widen = new List<(string Column, string From, string To)>();
            foreach (var (name, incomingType) in incoming)
            {
                if (name.Equals(Lineage.KeyColumn, StringComparison.OrdinalIgnoreCase))
                    continue;
                var match = existing.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (match.Name is null)
                    continue;
                // A configured (or AddColumn-declared) column keeps its type: the incoming values cast into it.
                if (configTypes?.Keys.Any(k => k.Equals(match.Name, StringComparison.OrdinalIgnoreCase)) == true)
                    continue;
                if (NormalizeType(match.DuckDbType) == NormalizeType(incomingType))
                    continue;
                var decision = DecideColumnMerge(match.DuckDbType, incomingType,
                    () => IsAllNull(sourceTable, name), () => IsAllNull(ownerOf(match.Name), match.Name));
                if (decision.Kind == ColumnMerge.Conflict)
                {
                    throw new DataBallException(
                        $"Cannot append {incomingName}: column '{match.Name}' has type {match.DuckDbType} in the session and {incomingType} in the incoming data");
                }
                if (decision.Kind == ColumnMerge.Become)
                    widen.Add((match.Name, match.DuckDbType, decision.Type));
            }
            return widen;
        }

        /// <summary>True when <paramref name="column"/> of <paramref name="table"/> holds no non-null value.</summary>
        private bool IsAllNull(string table, string column)
        {
            var count = ExecuteScalar($"SELECT COUNT({QuoteIdent(column)}) FROM {QuoteIdent(table)}");
            return Convert.ToInt64(count, CultureInfo.InvariantCulture) == 0;
        }

        /// <summary>
        /// Imports a CSV. <paramref name="prepareStaging"/> runs against the staging table after type
        /// coercion and before the merge (the CSV schema is applied there, for wide and layout sessions alike).
        /// </summary>
        internal void ImportCsv(
            string path,
            bool append,
            IReadOnlyDictionary<string, Type>? expectedTypes = null,
            Action<string>? prepareStaging = null,
            Func<long>? recordSource = null,
            IReadOnlyDictionary<string, Type>? configTypes = null,
            IReadOnlySet<string>? learnedNames = null)
        {
            ImportFromFunction(path, append, "read_csv_auto", ", header=true", expectedTypes, prepareStaging, recordSource, null, configTypes, learnedNames);
        }

        internal void RenameColumnOf(string table, string from, string to)
        {
            ThrowIfDisposed();
            ValidateName(table, "Table");
            ValidateName(from, "Column");
            ValidateName(to, "Column");
            if (!TableExists(table))
                throw new DataBallException($"Column '{from}' does not exist");
            if (from.Equals(to, StringComparison.OrdinalIgnoreCase))
                return;
            var cols = GetColumnsOf(table);
            if (!cols.Any(c => c.Name.Equals(from, StringComparison.OrdinalIgnoreCase)))
                throw new DataBallException($"Column '{from}' does not exist");
            if (cols.Any(c => c.Name.Equals(to, StringComparison.OrdinalIgnoreCase)))
                throw new DataBallException($"Column '{to}' already exists");
            Execute($"ALTER TABLE {QuoteIdent(table)} RENAME COLUMN {QuoteIdent(from)} TO {QuoteIdent(to)}");
        }

        /// <summary>
        /// Rewrites <paramref name="table"/> casting the columns named in <paramref name="expectedTypes"/>.
        /// Works for the base table and for TEMP staging tables.
        /// </summary>
        internal void CoerceColumnsOf(string table, IReadOnlyDictionary<string, Type>? expectedTypes, IReadOnlySet<string>? learnedNames = null)
        {
            ThrowIfDisposed();
            ValidateName(table, "Table");
            if (expectedTypes is null || expectedTypes.Count == 0 || !TableExists(table))
                return;

            var cols = GetColumnsOf(table);
            if (cols.Count == 0)
                return;

            var byName = new Dictionary<string, Type>(expectedTypes.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in expectedTypes)
                byName[pair.Key] = pair.Value;

            if (!cols.Any(c => byName.ContainsKey(c.Name)))
                return;

            var qTable = QuoteIdent(table);
            var select = string.Join(", ", cols.Select(c =>
            {
                var q = QuoteIdent(c.Name);
                return byName.TryGetValue(c.Name, out var clr) && !WidensLearned(c.Name, clr, c.DuckDbType, learnedNames)
                    ? $"CAST({q} AS {ToDuckDbType(clr)}) AS {q}"
                    : q;
            }));
            var temp = IsTempTable(table) ? "TEMP " : string.Empty;
            InTransaction(() =>
            {
                Execute($"CREATE OR REPLACE {temp}TABLE {qTable} AS SELECT {select} FROM {qTable}");
            });
        }

        /// <summary>
        /// True when a learned type is narrower than the incoming column type under the merge's widening order. A learned
        /// type never narrows an incoming value: the column is left uncast and the merge widens it. Declared types never reach here.
        /// </summary>
        private static bool WidensLearned(string name, Type learned, string incomingType, IReadOnlySet<string>? learnedNames)
        {
            if (learnedNames is null || !learnedNames.Contains(name))
                return false;
            var learnedType = ToDuckDbType(learned);
            var merged = MergedColumnType(learnedType, incomingType);
            return merged is not null && NormalizeType(merged) != NormalizeType(learnedType);
        }

        /// <summary>
        /// Imports Parquet (a file or a hive directory). Parquet is not coerced (its types are the source's);
        /// <paramref name="configTypes"/> names the columns that keep their configured type, so an append
        /// casts into them rather than widening them. <paramref name="incomingName"/> names the source in a conflict.
        /// </summary>
        internal void ImportParquet(
            string path,
            bool append,
            Action<string>? prepareStaging = null,
            Func<long>? recordSource = null,
            IReadOnlyDictionary<string, Type>? configTypes = null,
            string? incomingName = null)
        {
            ThrowIfConnectionBusy();
            if (Directory.Exists(path))
            {
                ImportHiveParquet(path, append, prepareStaging, recordSource, configTypes, incomingName);
                return;
            }
            ImportFromFunction(path, append, "read_parquet", "", null, prepareStaging, recordSource, incomingName, configTypes);
        }

        private void ImportHiveParquet(
            string directory,
            bool append,
            Action<string>? prepareStaging,
            Func<long>? recordSource,
            IReadOnlyDictionary<string, Type>? configTypes,
            string? incomingName)
        {
            ThrowIfDisposed();
            var files = HiveFiles.List(directory);
            if (files.Count == 0)
                throw new DataBallException($"No parquet files in hive directory: {directory}");

            Execute($"CREATE OR REPLACE TEMP TABLE \"_staging\" AS SELECT * FROM {ReadHiveParquet(files)}");
            try
            {
                if (recordSource is not null)
                    AttachSourceKey("_staging", recordSource);
                PromoteDateColumns("_staging");
                prepareStaging?.Invoke("_staging");
                if (!TableExists("_staging"))
                    return;
                MergeOrAppendFromTable("_staging", append, incomingName ?? $"'{directory}'", configTypes);
            }
            finally
            {
                DropTemp("_staging");
            }
        }

        internal void ExportCsv(string path, ParameterizedSql? select = null)
        {
            ExportTo(path, "FORMAT CSV, HEADER true", select);
        }

        internal void ExportParquet(string path, ParameterizedSql? select = null)
        {
            ExportTo(path, "FORMAT PARQUET", select);
        }

        /// <summary>
        /// Internal hand-off between sessions: writes <c>"data"</c> as it is, <c>_source_key</c> included, with
        /// no <c>source</c> join. The receiving session brings the other session's sources itself.
        /// </summary>
        internal void ExportParquetKeepingSourceKey(string path)
        {
            ExportTo(path, "FORMAT PARQUET", null, resolveSource: false);
        }

        /// <summary>
        /// Exported files stand alone: rows that carry <c>_source_key</c> leave it behind and write
        /// <c>_source_file</c> and <c>_source_type</c> from the <c>source</c> table instead (empty for a
        /// NULL key). Without a <c>source</c> table (a session that never read a file) the key is still left
        /// behind and both columns are NULL. A select that does not carry the key is written as it is.
        /// </summary>
        private string ResolveSourceSql(string sql, DuckDBParameter[] parameters)
        {
            var names = new List<string>();
            using (var probe = CreateCommand($"SELECT * FROM ({sql}) AS q LIMIT 0", parameters))
            using (var reader = probe.ExecuteReader())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                    names.Add(reader.GetName(i));
            }
            var key = names.FirstOrDefault(n => n.Equals(Lineage.KeyColumn, StringComparison.OrdinalIgnoreCase));
            if (key is null)
                return sql;
            var file = QuoteIdent(Lineage.FileColumn);
            var type = QuoteIdent(Lineage.TypeColumn);
            if (!HasSourceTable())
            {
                return $"""
                    SELECT q.* EXCLUDE ({QuoteIdent(key)}), CAST(NULL AS VARCHAR) AS {file}, CAST(NULL AS VARCHAR) AS {type}
                    FROM ({sql}) AS q
                    """;
            }
            // Per-source metadata columns of a many-file open follow the lineage columns; a row column of the same name wins.
            var perSource = string.Concat(SourceColumnsNotShadowedBy(names).Select(c => $", s.{QuoteIdent(c.Name)}"));
            return $"""
                SELECT q.* EXCLUDE ({QuoteIdent(key)}), s."path" AS {file}, s."type" AS {type}{perSource}
                FROM ({sql}) AS q LEFT JOIN "source" AS s ON q.{QuoteIdent(key)} = s."source_key"
                """;
        }

        internal IReadOnlyList<string> ResolvePartitionColumns(IReadOnlyList<string> partitionColumns)
        {
            ThrowIfDisposed();
            if (partitionColumns is null || partitionColumns.Count == 0)
                throw new DataBallException("Partition columns are required");
            if (!DataTableExists())
                throw new DataBallException("No data to export");

            var cols = GetColumns();
            var resolved = new List<string>(partitionColumns.Count);
            foreach (var col in partitionColumns)
            {
                ValidateName(col, "Column");
                var match = cols.FirstOrDefault(c => c.Name.Equals(col, StringComparison.OrdinalIgnoreCase));
                if (match.Name is null)
                    throw new DataBallException($"Partition column '{col}' does not exist");
                if (match.Name.Equals(Lineage.KeyColumn, StringComparison.OrdinalIgnoreCase))
                    throw new DataBallException($"Partition column '{col}' is not exported; the output carries {Lineage.FileColumn} and {Lineage.TypeColumn} instead");
                resolved.Add(match.Name);
            }
            return resolved;
        }

        internal void ExportPartitionedParquet(string directory, IReadOnlyList<string> partitionColumns)
        {
            ThrowIfDisposed();
            var resolved = ResolvePartitionColumns(partitionColumns);

            // OVERWRITE true does not drop stale hive partition directories from a prior key set.
            ClearHiveTarget(directory);

            var qDir = QuotePath(directory);
            var by = string.Join(", ", resolved.Select(QuoteIdent));
            // DuckDB errors when every remaining column is a partition column unless those columns are also written into the files.
            var rows = ResolveSourceSql("SELECT * FROM \"data\"", []);
            Execute($"COPY ({rows}) TO {qDir} (FORMAT PARQUET, PARTITION_BY ({by}), OVERWRITE true, WRITE_PARTITION_COLUMNS true)");
        }

        private static void ClearHiveTarget(string directory)
        {
            if (Directory.Exists(directory))
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(path);
                    var isDir = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
                    if (isDir)
                    {
                        if (!IsHivePartitionName(name))
                            throw new DataBallException($"Refusing to overwrite '{directory}': it contains '{name}'.");
                    }
                    else if (!name.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DataBallException($"Refusing to overwrite '{directory}': it contains '{name}'.");
                    }
                }

                Directory.Delete(directory, recursive: true);
            }

            Directory.CreateDirectory(directory);
        }

        private static bool IsHivePartitionName(string name)
        {
            var eq = name.IndexOf('=');
            return eq > 0 && eq < name.Length - 1;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                TrySaveCleanup(_connection.Dispose);
            }
            finally
            {
                try
                {
                    TrySaveCleanup(DeleteTemporaryStore);
                }
                finally
                {
                    ReleaseExclusivePath();
                }
            }
        }

        private void ImportFromFunction(
            string path,
            bool append,
            string function,
            string extraArgs,
            IReadOnlyDictionary<string, Type>? expectedTypes = null,
            Action<string>? prepareStaging = null,
            Func<long>? recordSource = null,
            string? incomingName = null,
            IReadOnlyDictionary<string, Type>? configTypes = null,
            IReadOnlySet<string>? learnedNames = null)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(path))
                throw new DataBallException("Path is required");
            var qpath = QuotePath(path);
            Execute($"CREATE OR REPLACE TEMP TABLE \"_staging\" AS SELECT * FROM {function}({qpath}{extraArgs})");
            try
            {
                // The source is recorded once the file has parsed, so an unreadable input leaves no row behind.
                if (recordSource is not null)
                    AttachSourceKey("_staging", recordSource);
                PromoteDateColumns("_staging");
                // Learned types drop out of the cast only when this import merges into a live session.
                var merging = append && (Layout is not null || (DataTableExists() && RowCount() > 0));
                CoerceColumnsOf("_staging", expectedTypes, merging ? learnedNames : null);
                prepareStaging?.Invoke("_staging");
                // Extracting metadata may have consumed every staging column (and the table with it).
                if (!TableExists("_staging"))
                {
                    // A replace still replaces: the previous wide table goes, as it did when the merge created it first.
                    if (!merging && Layout is null && DataTableExists())
                        Execute("DROP TABLE \"data\"");
                    return;
                }
                MergeOrAppendFromTable("_staging", append, incomingName ?? $"'{path}'", configTypes ?? expectedTypes);
            }
            finally
            {
                DropTemp("_staging");
            }
        }

        private void ExportTo(string path, string copyOptions, ParameterizedSql? select = null, bool resolveSource = true)
        {
            ThrowIfDisposed();
            if (!DataTableExists())
                throw new DataBallException("No data to export");
            if (string.IsNullOrWhiteSpace(path))
                throw new DataBallException("Path is required");
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            // Subquery form so "data" may be a base table or the layout view.
            var parameters = select?.Parameters ?? [];
            var rows = select is null ? "SELECT * FROM \"data\"" : select.Value.Sql;
            if (resolveSource)
                rows = ResolveSourceSql(rows, parameters);
            var sql = $"COPY ({rows}) TO {QuotePath(path)} ({copyOptions})";
            if (parameters.Length > 0)
                ExecuteParameterized(sql, parameters);
            else
                Execute(sql);
        }

        /// <summary>
        /// Reconciles the stored metadata with the columns of <paramref name="sourceTable"/> before an append.
        /// A source column equal to the stored value is dropped from the source; so is any such column under
        /// <c>metadataPolicy: first</c>, where the stored value wins. A differing column is demoted: it is added
        /// to <c>"data"</c> and its key leaves the metadata. The old value is returned for the caller to backfill
        /// once its own ALTERs are done. The source keeps <see cref="Lineage.KeyColumn"/>, so a row from a file
        /// that held only metadata still carries its key.
        /// </summary>
        private List<(string Column, object? Value)> ApplyMetadataAgainstSource(string sourceTable)
        {
            var backfills = new List<(string Column, object? Value)>();
            var storedWins = string.Equals(MetadataPolicySource?.Invoke(), "first", StringComparison.OrdinalIgnoreCase);
            var dstNames = new HashSet<string>(GetColumns().Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var qSrc = QuoteIdent(sourceTable);

            foreach (var pair in _metadata.ToList())
            {
                if (dstNames.Contains(pair.Key))
                    continue;

                var srcCols = GetColumnsOf(sourceTable);
                if (srcCols.Count == 0)
                    break;

                var srcCol = srcCols.FirstOrDefault(c => c.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
                if (srcCol.Name is null)
                    continue;

                var qK = QuoteIdent(srcCol.Name);
                var differing = storedWins ? 0 : Convert.ToInt64(
                    ExecuteScalarParameterized(
                        $"SELECT COUNT(*) FILTER (WHERE {qK} IS DISTINCT FROM $v) FROM {qSrc}",
                        Param("v", pair.Value)),
                    CultureInfo.InvariantCulture);

                if (differing == 0)
                {
                    // Never drop the source table while another column (the key) remains.
                    if (srcCols.Count > 1)
                        Execute($"ALTER TABLE {qSrc} DROP COLUMN {qK}");
                    else
                        DropColumnFromTable(sourceTable, srcCol.Name);
                    continue;
                }

                Execute($"ALTER TABLE \"data\" ADD COLUMN {qK} {srcCol.DuckDbType}");
                backfills.Add((srcCol.Name, pair.Value));
                dstNames.Add(srcCol.Name);
                RemoveMetadata(pair.Key);
            }
            return backfills;
        }

        /// <summary>Drops a column from any table; drops the table itself when it was the last column.</summary>
        internal void RemoveColumnOf(string tableName, string columnName)
        {
            ThrowIfDisposed();
            ValidateName(tableName, "Table");
            ValidateName(columnName, "Column");
            DropColumnFromTable(tableName, columnName);
        }

        /// <summary>Excludes an extracted metadata column from the external Parquet view.</summary>
        internal void ExcludeFromView(string column)
        {
            ThrowIfConnectionBusy();
            ValidateName(column, "Column");
            var cols = GetColumns();
            if (!cols.Any(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase)))
                return;
            if (IsLastDataColumn(cols, column))
            {
                DropDataRelation();
                return;
            }
            var sql = (string)Query("SELECT sql FROM duckdb_views() WHERE database_name = current_database() AND view_name = 'data'")[0]["sql"]!;
            var select = sql[(sql.IndexOf(" AS ", StringComparison.OrdinalIgnoreCase) + 4)..].TrimEnd(';');
            Execute($"CREATE OR REPLACE VIEW \"data\" AS SELECT * EXCLUDE ({QuoteIdent(column)}) FROM ({select})");
        }

        /// <summary>
        /// True when <paramref name="column"/> is the only column left that is data. A table that holds
        /// nothing but <see cref="Lineage.KeyColumn"/> has no data: dropping the last data column drops the
        /// table, as it did before rows carried a key.
        /// </summary>
        private static bool IsLastDataColumn(IReadOnlyList<(string Name, string DuckDbType)> cols, string column)
        {
            if (column.Equals(Lineage.KeyColumn, StringComparison.OrdinalIgnoreCase))
                return cols.Count == 1;
            return cols.All(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase)
                || c.Name.Equals(Lineage.KeyColumn, StringComparison.OrdinalIgnoreCase));
        }

        private void DropColumnFromTable(string tableName, string columnName)
        {
            var cols = GetColumnsOf(tableName);
            var match = cols.FirstOrDefault(c => c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
            if (match.Name is null)
                return;
            if (IsLastDataColumn(cols, match.Name))
                Execute($"DROP TABLE {QuoteIdent(tableName)}");
            else
                Execute($"ALTER TABLE {QuoteIdent(tableName)} DROP COLUMN {QuoteIdent(match.Name)}");
        }

        private void PromoteDateColumns(string tableName)
        {
            var cols = GetColumnsOf(tableName);
            if (cols.Count == 0 || cols.All(c => !IsDateType(c.DuckDbType)))
                return;

            var qTable = QuoteIdent(tableName);
            var select = string.Join(", ", cols.Select(c =>
            {
                var q = QuoteIdent(c.Name);
                return IsDateType(c.DuckDbType) ? $"CAST({q} AS TIMESTAMP) AS {q}" : q;
            }));
            var temp = IsTempTable(tableName) ? "TEMP " : string.Empty;
            Execute($"CREATE OR REPLACE {temp}TABLE {qTable} AS SELECT {select} FROM {qTable}");
        }

        private long RowCountOf(string tableName)
        {
            if (!TableExists(tableName))
                return 0;
            var result = ExecuteScalar($"SELECT COUNT(*) FROM {QuoteIdent(tableName)}");
            return Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }

        private bool IsTempTable(string tableName)
        {
            // DuckDB puts TEMP tables in catalog 'temp', schema 'main'.
            var result = ExecuteScalar($"""
                SELECT COUNT(*) FROM information_schema.tables
                WHERE table_name = {QuoteString(tableName)} AND table_catalog = 'temp'
                """);
            return Convert.ToInt64(result, CultureInfo.InvariantCulture) > 0;
        }

        internal bool TableExists(string tableName)
        {
            var result = ExecuteScalar($"""
                SELECT COUNT(*) FROM information_schema.tables
                WHERE table_name = {QuoteString(tableName)}
                  AND table_schema IN ('main', 'temp')
                  AND table_catalog IN (current_database(), 'temp')
                """);
            return Convert.ToInt64(result, CultureInfo.InvariantCulture) > 0;
        }

        private void InsertNullRows(long count)
        {
            if (count <= 0 || !DataTableExists())
                return;
            var columns = GetColumns();
            using var appender = CreateAppender("data");
            for (long i = 0; i < count; i++)
            {
                var row = appender.CreateRow();
                for (int c = 0; c < columns.Count; c++)
                    row.AppendNullValue();
                row.EndRow();
            }
        }

        internal IReadOnlyList<(string Name, string DuckDbType)> GetColumnsOf(string tableName)
        {
            ThrowIfDisposed();
            var sql = $"""
                SELECT column_name, data_type
                FROM information_schema.columns
                WHERE table_name = {QuoteString(tableName)}
                  AND table_schema IN ('main', 'temp')
                  AND table_catalog IN (current_database(), 'temp')
                ORDER BY ordinal_position
                """;
            var rows = Query(sql);
            var cols = new List<(string Name, string DuckDbType)>(rows.Count);
            foreach (var row in rows)
            {
                var name = Convert.ToString(row["column_name"], CultureInfo.InvariantCulture) ?? string.Empty;
                var type = Convert.ToString(row["data_type"], CultureInfo.InvariantCulture) ?? string.Empty;
                cols.Add((name, type));
            }
            return cols;
        }

        private void AppendSingleColumn<T>(IReadOnlyList<T> values)
        {
            using var appender = CreateAppender("data");
            for (int i = 0; i < values.Count; i++)
            {
                var row = appender.CreateRow();
                AppendClr(row, values[i]);
                row.EndRow();
            }
        }

        private string? InferredNullColumnType(
            string table, (string Name, string DuckDbType) column, object? value,
            IReadOnlyDictionary<string, Type>? expectedTypes)
        {
            value = Unwrap(value);
            if (NormalizeType(column.DuckDbType) != "VARCHAR" || value is null
                || expectedTypes?.Keys.Any(key => key.Equals(column.Name, StringComparison.OrdinalIgnoreCase)) == true)
                return null;
            var type = ToDuckDbType(ClrTypeOf(value));
            if (type == "VARCHAR")
                return null;
            var nonNull = ExecuteScalar($"SELECT COUNT({QuoteIdent(column.Name)}) FROM {QuoteIdent(table)}");
            return Convert.ToInt64(nonNull, CultureInfo.InvariantCulture) == 0 ? type : null;
        }

        private static object? FirstValue(
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
            string key)
        {
            foreach (var values in rows)
            {
                foreach (var pair in values)
                {
                    if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && pair.Value is not null)
                        return pair.Value;
                }
            }

            return null;
        }

        private void InsertAppenderRow(
            IReadOnlyList<(string Name, string DuckDbType)> columns,
            IReadOnlyDictionary<string, object?> values)
        {
            using var appender = CreateAppender("data");
            WriteAppenderRow(appender, columns, values);
        }

        private void WriteAppenderRow(
            DuckDBAppender appender,
            IReadOnlyList<(string Name, string DuckDbType)> columns,
            IReadOnlyDictionary<string, object?> values)
        {
            WriteCoercedAppenderRow(appender, CoerceAppenderValues(columns, values));
        }

        private static object?[] CoerceAppenderValues(
            IReadOnlyList<(string Name, string DuckDbType)> columns,
            IReadOnlyDictionary<string, object?> values)
        {
            var coerced = new object?[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                var (colName, duckType) = columns[i];
                object? raw = null;
                var present = false;
                foreach (var pair in values)
                {
                    if (!pair.Key.Equals(colName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    raw = Unwrap(pair.Value);
                    present = true;
                    break;
                }

                coerced[i] = present ? Coerce(raw, FromDuckDbType(duckType)) : null;
            }

            return coerced;
        }

        private static void WriteCoercedAppenderRow(DuckDBAppender appender, object?[] coerced)
        {
            var row = appender.CreateRow();
            foreach (var value in coerced)
                AppendObject(row, value);
            row.EndRow();
        }

        private void ExecuteParameterized(string sql, params DuckDBParameter[] parameters)
        {
            ThrowIfDisposed();
            try
            {
                using var cmd = CreateCommand(sql, parameters);
                cmd.ExecuteNonQuery();
            }
            catch (DuckDBException ex)
            {
                throw new DataBallException("DuckDB operation failed", ex);
            }
        }

        private object? ExecuteScalarParameterized(string sql, params DuckDBParameter[] parameters)
        {
            ThrowIfDisposed();
            try
            {
                using var cmd = CreateCommand(sql, parameters);
                var result = cmd.ExecuteScalar();
                return result is DBNull ? null : result;
            }
            catch (DuckDBException ex)
            {
                throw new DataBallException("DuckDB operation failed", ex);
            }
        }

        private DuckDBCommand CreateCommand(string sql, DuckDBParameter[] parameters)
        {
            ThrowIfConnectionBusy();
            var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            if (_currentTx is not null)
                cmd.Transaction = _currentTx;
            foreach (var parameter in parameters)
                cmd.Parameters.Add(parameter);
            return cmd;
        }

        private static List<Dictionary<string, object?>> ReadRows(DbDataReader reader)
        {
            var rows = new List<Dictionary<string, object?>>();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    object? value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    if (value is DBNull)
                        value = null;
                    if (value is DateOnly dateOnly)
                        value = dateOnly.ToDateTime(TimeOnly.MinValue);
                    row[reader.GetName(i)] = value;
                }
                rows.Add(row);
            }
            return rows;
        }

        private static void AppendClr<T>(IDuckDBAppenderRow row, T value)
        {
            if (value is null)
            {
                row.AppendNullValue();
                return;
            }

            switch (value)
            {
                case int i:
                    row.AppendValue(i);
                    break;
                case long l:
                    row.AppendValue(l);
                    break;
                case float f:
                    row.AppendValue(f);
                    break;
                case double d:
                    row.AppendValue(d);
                    break;
                case bool b:
                    row.AppendValue(b);
                    break;
                case DateTime dt:
                    row.AppendValue(dt);
                    break;
                case string s:
                    row.AppendValue(s);
                    break;
                default:
                    throw new DataBallException($"Unsupported column type: {typeof(T).FullName}");
            }
        }

        private static void AppendObject(IDuckDBAppenderRow row, object? value)
        {
            if (value is null)
            {
                row.AppendNullValue();
                return;
            }

            switch (value)
            {
                case int i:
                    row.AppendValue(i);
                    break;
                case long l:
                    row.AppendValue(l);
                    break;
                case float f:
                    row.AppendValue(f);
                    break;
                case double d:
                    row.AppendValue(d);
                    break;
                case bool b:
                    row.AppendValue(b);
                    break;
                case DateTime dt:
                    row.AppendValue(dt);
                    break;
                case DateOnly dateOnly:
                    row.AppendValue(dateOnly.ToDateTime(TimeOnly.MinValue));
                    break;
                case string s:
                    row.AppendValue(s);
                    break;
                default:
                    throw new DataBallException($"Unsupported value type: {value.GetType().Name}");
            }
        }

        internal static object? Coerce(object? value, Type target)
        {
            if (value is null)
                return null;
            var t = Nullable.GetUnderlyingType(target) ?? target;
            if (value is DateOnly dateOnly && t == typeof(DateTime))
                return dateOnly.ToDateTime(TimeOnly.MinValue);
            if (t.IsInstanceOfType(value))
                return value;
            try
            {
                return Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                throw new DataBallException($"Cannot convert value of type {value.GetType().Name} to {t.Name}", ex);
            }
        }

        internal static Type ReadTypeOf(string duckDbType)
        {
            var type = NormalizeType(duckDbType);
            if (type.StartsWith("DECIMAL", StringComparison.Ordinal))
                return typeof(double);
            return type switch
            {
                "TINYINT" or "SMALLINT" or "UTINYINT" or "USMALLINT"
                    or "UNSIGNEDTINYINT" or "UNSIGNEDSMALLINT" or "INTEGER" => typeof(int),
                "UINTEGER" or "UNSIGNEDINTEGER" or "BIGINT" => typeof(long),
                "UBIGINT" or "UNSIGNEDBIGINT" or "HUGEINT" or "DOUBLE" => typeof(double),
                "FLOAT" => typeof(float),
                "BOOLEAN" => typeof(bool),
                "DATE" or "TIMESTAMP" => typeof(DateTime),
                _ => typeof(string)
            };
        }

        internal static Type FromDuckDbType(string duckDbType)
        {
            return NormalizeType(duckDbType) switch
            {
                "INTEGER" => typeof(int),
                "BIGINT" => typeof(long),
                "FLOAT" => typeof(float),
                "DOUBLE" => typeof(double),
                "BOOLEAN" => typeof(bool),
                "TIMESTAMP" => typeof(DateTime),
                "DATE" => typeof(DateTime),
                "VARCHAR" => typeof(string),
                _ => throw new DataBallException($"Unsupported column type: {duckDbType}")
            };
        }

        internal static string NormalizeType(string duckDbType)
        {
            var t = duckDbType.Trim().ToUpperInvariant();
            if (t.StartsWith("TIMESTAMP", StringComparison.Ordinal))
                return "TIMESTAMP";
            return t switch
            {
                "INT" or "INTEGER" or "INT32" => "INTEGER",
                "BIGINT" or "INT64" or "LONG" => "BIGINT",
                "REAL" or "FLOAT" or "FLOAT4" => "FLOAT",
                "DOUBLE" or "FLOAT8" or "DOUBLE PRECISION" => "DOUBLE",
                "BOOLEAN" or "BOOL" => "BOOLEAN",
                "DATE" => "DATE",
                "VARCHAR" or "TEXT" or "STRING" => "VARCHAR",
                _ => t
            };
        }

        internal static bool IsDateType(string duckDbType)
        {
            return NormalizeType(duckDbType) == "DATE";
        }

        private static Type ResolveColumnType(
            string name,
            object? value,
            IReadOnlyDictionary<string, Type>? expectedTypes)
        {
            if (expectedTypes is not null && expectedTypes.TryGetValue(name, out var expected))
                return expected;
            return ClrTypeOf(value);
        }

        internal static object? UnwrapJson(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                case JsonValueKind.Number:
                    if (element.TryGetInt32(out var i))
                        return i;
                    if (element.TryGetInt64(out var l))
                        return l;
                    return element.GetDouble();
                case JsonValueKind.Object:
                case JsonValueKind.Array:
                    return element.GetRawText();
                default:
                    return element.GetRawText();
            }
        }

        internal void ReloadMetadata()
        {
            ThrowIfDisposed();
            _metadata.Clear();
            HydrateMetadata();
        }

        internal static string SerializeMetadataValue(object? value)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
                WriteTagged(writer, Unwrap(value));
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        internal static object? DeserializeMetadataValue(string json)
        {
            var element = JsonSerializer.Deserialize<JsonElement>(json);
            return DeserializeMetadataValue(element);
        }

        internal static object? DeserializeMetadataValue(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("t", out var tagEl)
                && element.TryGetProperty("v", out var valueEl)
                && tagEl.ValueKind == JsonValueKind.String)
            {
                var tag = tagEl.GetString();
                return tag switch
                {
                    "null" => null,
                    "i32" => valueEl.GetInt32(),
                    "i64" => valueEl.GetInt64(),
                    "f32" => valueEl.GetSingle(),
                    "f64" => valueEl.GetDouble(),
                    "bool" => valueEl.GetBoolean(),
                    "str" => valueEl.GetString(),
                    "dt" => DateTime.Parse(
                        valueEl.GetString() ?? throw new DataBallException("DateTime metadata is missing"),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind),
                    _ => throw new DataBallException($"Unknown metadata type tag '{tag}'")
                };
            }

            return UnwrapJson(element);
        }

        private static void WriteTagged(Utf8JsonWriter writer, object? value)
        {
            if (value is DateOnly dateOnly)
                value = dateOnly.ToDateTime(TimeOnly.MinValue);

            writer.WriteStartObject();
            switch (value)
            {
                case null:
                    writer.WriteString("t", "null");
                    writer.WriteNull("v");
                    break;
                case int n:
                    writer.WriteString("t", "i32");
                    writer.WriteNumber("v", n);
                    break;
                case long n:
                    writer.WriteString("t", "i64");
                    writer.WriteNumber("v", n);
                    break;
                case short n:
                    writer.WriteString("t", "i32");
                    writer.WriteNumber("v", n);
                    break;
                case ushort n:
                    writer.WriteString("t", "i32");
                    writer.WriteNumber("v", n);
                    break;
                case byte n:
                    writer.WriteString("t", "i32");
                    writer.WriteNumber("v", n);
                    break;
                case sbyte n:
                    writer.WriteString("t", "i32");
                    writer.WriteNumber("v", n);
                    break;
                case float n:
                    writer.WriteString("t", "f32");
                    writer.WriteNumber("v", n);
                    break;
                case double n:
                    writer.WriteString("t", "f64");
                    writer.WriteNumber("v", n);
                    break;
                case bool n:
                    writer.WriteString("t", "bool");
                    writer.WriteBoolean("v", n);
                    break;
                case string n:
                    writer.WriteString("t", "str");
                    writer.WriteString("v", n);
                    break;
                case DateTime n:
                    writer.WriteString("t", "dt");
                    writer.WriteString("v", n.ToString("o", CultureInfo.InvariantCulture));
                    break;
                default:
                    throw new DataBallException($"Unsupported metadata type: {value.GetType().Name}");
            }
            writer.WriteEndObject();
        }

        internal static DuckDBParameter Param(string name, object? value)
        {
            return new DuckDBParameter(name, value ?? DBNull.Value);
        }

        private static string QuotePath(string path)
        {
            return QuoteString(Path.GetFullPath(path).Replace('\\', '/'));
        }

        private static void ValidateName(string? name, string kind)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new DataBallException($"{kind} name is required");
        }

        private DuckDBAppender CreateAppender(string table)
        {
            ThrowIfConnectionBusy();
            return _connection.CreateAppender(table);
        }

        internal void ThrowIfConnectionBusy()
        {
            ThrowIfDisposed();
            if (_openStreams > 0)
                throw new DataBallException("A row stream is open; finish or dispose it first");
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
