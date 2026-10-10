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

        internal bool IsReadOnly { get; }

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

        internal void SaveTo(string path, Config? config = null)
        {
            ThrowIfDisposed();
            if (_currentTx is not null)
                throw new DataBallException("Cannot save inside a store transaction");
            lock (FileGate)
            {
                SaveToCore(path, config);
            }
        }

        private void SaveToCore(string path, Config? config)
        {
            if (!DataTableExists() && _metadata.Count == 0)
                throw new DataBallException("No data to export");
            var target = Path.GetFullPath(path);
            if (FilePathComparer.Equals(target, _exclusivePath))
            {
                if (IsReadOnly)
                    throw new DataBallException("Session is read-only; save to another path or open with writable: true");
                StampWriter();
                Execute("CHECKPOINT");
                return;
            }
            if (OpenFiles.Contains(target))
                throw new DataBallException("Save target is already open in another session");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".tmp";
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
                if (config is not null)
                    WriteConfig(config, alias);
                StampWriter(alias);
                // Read-only overlays live in memory; the saved copy gets the session metadata.
                Execute($"CREATE TABLE IF NOT EXISTS {QuoteIdent(alias)}.meta (key VARCHAR PRIMARY KEY, value VARCHAR)");
                foreach (var (key, value) in _metadata)
                    Execute($"INSERT OR REPLACE INTO {QuoteIdent(alias)}.meta VALUES ({QuoteString(key)}, {QuoteString(SerializeMetadataValue(value))})");
                Execute($"DETACH {QuoteIdent(alias)}");
                attached = false;
                File.Move(temporary, target, overwrite: true);
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

        internal void ImportNativeRows(string alias, bool append)
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
                MergeOrAppendFromTable("_staging", append);
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
                ThrowIfDisposed();
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

            using var tx = _connection.BeginTransaction();
            var previous = _currentTx;
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
                    using (var appender = _connection.CreateAppender("_addcol"))
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

            if (cols.Count == 1)
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

            if (keyOrder.Count == 0)
                throw new DataBallException("AddRows requires at least one column");

            if (Layout is not null)
            {
                AddRowsIntoLayout(rows, keyOrder, expectedTypes);
                return;
            }

            InTransaction(() =>
            {
                if (!DataTableExists())
                {
                    var parts = new List<string>(keyOrder.Count);
                    foreach (var key in keyOrder)
                        parts.Add($"{QuoteIdent(key)} {ToDuckDbType(ResolveColumnType(key, FirstValue(rows, key), expectedTypes))}");
                    Execute($"CREATE TABLE \"data\" ({string.Join(", ", parts)})");
                }
                else
                {
                    var cols = GetColumns();
                    foreach (var key in keyOrder)
                    {
                        if (cols.Any(c => c.Name.Equals(key, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        Execute($"ALTER TABLE \"data\" ADD COLUMN {QuoteIdent(key)} {ToDuckDbType(ResolveColumnType(key, FirstValue(rows, key), expectedTypes))}");
                    }
                }

                var columns = GetColumns();
                var coercedRows = new List<object?[]>(rows.Count);
                foreach (var values in rows)
                    coercedRows.Add(CoerceAppenderValues(columns, values));

                using var appender = _connection.CreateAppender("data");
                foreach (var coerced in coercedRows)
                    WriteCoercedAppenderRow(appender, coerced);
            });
        }

        internal IReadOnlyDictionary<string, object?> SnapshotMetadata()
        {
            ThrowIfDisposed();
            return new Dictionary<string, object?>(_metadata, StringComparer.OrdinalIgnoreCase);
        }

        internal void SetMetadata(string key, object? value)
        {
            ThrowIfDisposed();
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

        internal void MergeOrAppendFromTable(string sourceTable, bool append)
        {
            ThrowIfDisposed();
            ValidateName(sourceTable, "Table");
            var qSrc = QuoteIdent(sourceTable);

            if (Layout is not null && append)
            {
                AppendStagingIntoLayout(sourceTable);
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
                InTransaction(() => AppendFromTable(sourceTable, qSrc));
            }
            catch
            {
                // ApplyMetadataAgainstSource mutated _metadata inside the transaction; reload once it is rolled back.
                if (_currentTx is null)
                    ReloadMetadata();
                throw;
            }
        }

        private void AppendFromTable(string sourceTable, string qSrc)
        {
            var sourceRowCount = RowCountOf(sourceTable);
            ApplyMetadataAgainstSource(sourceTable);

            var srcCols = GetColumnsOf(sourceTable);
            var dstCols = GetColumns();
            var dstByName = dstCols.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var (srcName, srcType) in srcCols)
            {
                if (dstByName.ContainsKey(srcName))
                    continue;
                Execute($"ALTER TABLE \"data\" ADD COLUMN {QuoteIdent(srcName)} {srcType}");
                dstByName[srcName] = (srcName, srcType);
            }

            if (srcCols.Count == 0)
            {
                InsertNullRows(sourceRowCount);
                return;
            }

            var selectParts = new List<string>(srcCols.Count);
            foreach (var (srcName, srcType) in srcCols)
            {
                var q = QuoteIdent(srcName);
                if (dstByName.TryGetValue(srcName, out var dst) &&
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
        /// Imports a CSV. <paramref name="prepareStaging"/> runs against the staging table after type
        /// coercion and before the merge (layout sessions apply the CSV schema there).
        /// </summary>
        internal void ImportCsv(
            string path,
            bool append,
            IReadOnlyDictionary<string, Type>? expectedTypes = null,
            Action<string>? prepareStaging = null)
        {
            ImportFromFunction(path, append, "read_csv_auto", ", header=true", expectedTypes, prepareStaging);
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
        internal void CoerceColumnsOf(string table, IReadOnlyDictionary<string, Type>? expectedTypes)
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
                return byName.TryGetValue(c.Name, out var clr)
                    ? $"CAST({q} AS {ToDuckDbType(clr)}) AS {q}"
                    : q;
            }));
            var temp = IsTempTable(table) ? "TEMP " : string.Empty;
            InTransaction(() =>
            {
                Execute($"CREATE OR REPLACE {temp}TABLE {qTable} AS SELECT {select} FROM {qTable}");
            });
        }

        internal void ImportParquet(string path, bool append)
        {
            if (Directory.Exists(path))
            {
                ImportHiveParquet(path, append);
                return;
            }
            ImportFromFunction(path, append, "read_parquet", "");
        }

        private void ImportHiveParquet(string directory, bool append)
        {
            ThrowIfDisposed();
            var files = Directory.GetFiles(directory, "*.parquet", SearchOption.AllDirectories);
            if (files.Length == 0)
                throw new DataBallException($"No parquet files in hive directory: {directory}");

            var dir = Path.GetFullPath(directory).Replace('\\', '/').TrimEnd('/');
            var glob = QuoteString(dir + "/**/*.parquet");
            Execute($"CREATE OR REPLACE TEMP TABLE \"_staging\" AS SELECT * FROM read_parquet({glob}, hive_partitioning = false)");
            try
            {
                PromoteDateColumns("_staging");
                MergeOrAppendFromTable("_staging", append);
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
            Execute($"COPY (SELECT * FROM \"data\") TO {qDir} (FORMAT PARQUET, PARTITION_BY ({by}), OVERWRITE true, WRITE_PARTITION_COLUMNS true)");
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
            Action<string>? prepareStaging = null)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(path))
                throw new DataBallException("Path is required");
            var qpath = QuotePath(path);
            Execute($"CREATE OR REPLACE TEMP TABLE \"_staging\" AS SELECT * FROM {function}({qpath}{extraArgs})");
            try
            {
                PromoteDateColumns("_staging");
                CoerceColumnsOf("_staging", expectedTypes);
                prepareStaging?.Invoke("_staging");
                // Extracting metadata may have consumed every staging column (and the table with it).
                if (!TableExists("_staging"))
                    return;
                MergeOrAppendFromTable("_staging", append);
            }
            finally
            {
                DropTemp("_staging");
            }
        }

        private void ExportTo(string path, string copyOptions, ParameterizedSql? select = null)
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
            var source = select is null ? "(SELECT * FROM \"data\")" : "(" + select.Value.Sql + ")";
            var sql = $"COPY {source} TO {QuotePath(path)} ({copyOptions})";
            var parameters = select?.Parameters ?? [];
            if (parameters.Length > 0)
                ExecuteParameterized(sql, parameters);
            else
                Execute(sql);
        }

        private void ApplyMetadataAgainstSource(string sourceTable)
        {
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
                var distinctCount = Convert.ToInt64(
                    ExecuteScalarParameterized(
                        $"SELECT COUNT(*) FILTER (WHERE {qK} IS DISTINCT FROM $v) FROM {qSrc}",
                        Param("v", pair.Value)),
                    CultureInfo.InvariantCulture);

                if (distinctCount == 0)
                {
                    DropColumnFromTable(sourceTable, srcCol.Name);
                    continue;
                }

                Execute($"ALTER TABLE \"data\" ADD COLUMN {qK} {srcCol.DuckDbType}");
                ExecuteParameterized($"UPDATE \"data\" SET {qK} = $v", Param("v", pair.Value));
                dstNames.Add(srcCol.Name);
                RemoveMetadata(pair.Key);
            }
        }

        /// <summary>Drops a column from any table; drops the table itself when it was the last column.</summary>
        internal void RemoveColumnOf(string tableName, string columnName)
        {
            ThrowIfDisposed();
            ValidateName(tableName, "Table");
            ValidateName(columnName, "Column");
            DropColumnFromTable(tableName, columnName);
        }

        private void DropColumnFromTable(string tableName, string columnName)
        {
            var cols = GetColumnsOf(tableName);
            var match = cols.FirstOrDefault(c => c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
            if (match.Name is null)
                return;
            if (cols.Count == 1)
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
            Execute($"CREATE OR REPLACE TEMP TABLE {qTable} AS SELECT {select} FROM {qTable}");
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
            using var appender = _connection.CreateAppender("data");
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
            using var appender = _connection.CreateAppender("data");
            for (int i = 0; i < values.Count; i++)
            {
                var row = appender.CreateRow();
                AppendClr(row, values[i]);
                row.EndRow();
            }
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
            using var appender = _connection.CreateAppender("data");
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

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
