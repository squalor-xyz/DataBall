using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using DuckDB.NET.Data;

namespace squalor.DataBall
{
    internal sealed class DuckDbStore : IDisposable
    {
        private readonly DuckDBConnection _connection;
        private readonly Dictionary<string, object?> _metadata = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        internal DuckDbStore()
        {
            _connection = new DuckDBConnection("Data Source=:memory:");
            _connection.Open();
            Execute("""
                CREATE TABLE "meta" (
                    "key"   VARCHAR PRIMARY KEY,
                    "value" VARCHAR
                );
                """);
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

        internal void Execute(string sql)
        {
            ThrowIfDisposed();
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
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
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                var result = cmd.ExecuteScalar();
                return result is DBNull ? null : result;
            }
            catch (DuckDBException ex)
            {
                throw new DataBallException("DuckDB operation failed", ex);
            }
        }

        internal List<Dictionary<string, object?>> Query(string sql)
        {
            ThrowIfDisposed();
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
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
                WHERE table_schema = 'main' AND table_name = 'data'
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

                Execute($"""
                    UPDATE "data" SET {qname} = "_addcol"."v"
                    FROM "_addcol"
                    WHERE "data".rowid = "_addcol"."rid"
                    """);
            }
            finally
            {
                Execute("DROP TABLE IF EXISTS \"_addcol\"");
            }
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
            var storedKey = _metadata.Keys.First(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            var json = JsonSerializer.Serialize(unwrapped);
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
            var storedKey = _metadata.Keys.First(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            _metadata.Remove(storedKey);
            ExecuteParameterized("DELETE FROM \"meta\" WHERE \"key\" = $key", Param("key", storedKey));
        }

        internal void MergeOrAppendFromTable(string sourceTable, bool append)
        {
            ThrowIfDisposed();
            ValidateName(sourceTable, "Table");
            var qSrc = QuoteIdent(sourceTable);

            if (!append || !DataTableExists() || RowCount() == 0)
            {
                Execute("DROP TABLE IF EXISTS \"data\"");
                Execute($"CREATE TABLE \"data\" AS SELECT * FROM {qSrc}");
                return;
            }

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

        internal void ImportCsv(string path, bool append, IReadOnlyDictionary<string, Type>? expectedTypes = null)
        {
            ImportFromFunction(path, append, "read_csv_auto", ", header=true", expectedTypes);
        }

        internal void ImportParquet(string path, bool append)
        {
            ImportFromFunction(path, append, "read_parquet", "");
        }

        internal void ExportCsv(string path)
        {
            ExportTo(path, "FORMAT CSV, HEADER true");
        }

        internal void ExportParquet(string path)
        {
            ExportTo(path, "FORMAT PARQUET");
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
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            Directory.CreateDirectory(directory);

            var qDir = QuotePath(directory);
            var by = string.Join(", ", resolved.Select(QuoteIdent));
            // DuckDB errors when every remaining column is a partition column unless those columns are also written into the files.
            Execute($"COPY \"data\" TO {qDir} (FORMAT PARQUET, PARTITION_BY ({by}), OVERWRITE true, WRITE_PARTITION_COLUMNS true)");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _connection.Dispose();
            _disposed = true;
        }

        private void ImportFromFunction(
            string path,
            bool append,
            string function,
            string extraArgs,
            IReadOnlyDictionary<string, Type>? expectedTypes = null)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(path))
                throw new DataBallException("Path is required");
            var qpath = QuotePath(path);
            Execute($"CREATE OR REPLACE TEMP TABLE \"_staging\" AS SELECT * FROM {function}({qpath}{extraArgs})");
            try
            {
                PromoteDateColumns("_staging");
                CoerceStagingColumns("_staging", expectedTypes);
                MergeOrAppendFromTable("_staging", append);
            }
            finally
            {
                Execute("DROP TABLE IF EXISTS \"_staging\"");
            }
        }

        private void ExportTo(string path, string copyOptions)
        {
            ThrowIfDisposed();
            if (!DataTableExists())
                throw new DataBallException("No data to export");
            if (string.IsNullOrWhiteSpace(path))
                throw new DataBallException("Path is required");
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            Execute($"COPY \"data\" TO {QuotePath(path)} ({copyOptions})");
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

        private void CoerceStagingColumns(string tableName, IReadOnlyDictionary<string, Type>? expected)
        {
            if (expected is null || expected.Count == 0)
                return;

            var cols = GetColumnsOf(tableName);
            if (cols.Count == 0)
                return;

            var byName = new Dictionary<string, Type>(expected.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in expected)
                byName[pair.Key] = pair.Value;

            if (!cols.Any(c => byName.ContainsKey(c.Name)))
                return;

            var qTable = QuoteIdent(tableName);
            var select = string.Join(", ", cols.Select(c =>
            {
                var q = QuoteIdent(c.Name);
                return byName.TryGetValue(c.Name, out var clr)
                    ? $"CAST({q} AS {ToDuckDbType(clr)}) AS {q}"
                    : q;
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

        private bool TableExists(string tableName)
        {
            var result = ExecuteScalar($"""
                SELECT COUNT(*) FROM information_schema.tables
                WHERE table_name = {QuoteString(tableName)}
                  AND table_schema IN ('main', 'temp')
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

        private IReadOnlyList<(string Name, string DuckDbType)> GetColumnsOf(string tableName)
        {
            ThrowIfDisposed();
            var sql = $"""
                SELECT column_name, data_type
                FROM information_schema.columns
                WHERE table_name = {QuoteString(tableName)}
                  AND table_schema IN ('main', 'temp')
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

        private void InsertAppenderRow(
            IReadOnlyList<(string Name, string DuckDbType)> columns,
            IReadOnlyDictionary<string, object?> values)
        {
            using var appender = _connection.CreateAppender("data");
            var row = appender.CreateRow();
            foreach (var (colName, duckType) in columns)
            {
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

                if (!present)
                {
                    row.AppendNullValue();
                    continue;
                }

                var coerced = Coerce(raw, FromDuckDbType(duckType));
                AppendObject(row, coerced);
            }
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

        private static object? Coerce(object? value, Type target)
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

        private static Type FromDuckDbType(string duckDbType)
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

        private static string NormalizeType(string duckDbType)
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

        private static bool IsDateType(string duckDbType)
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

        private static object? UnwrapJson(JsonElement element)
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

        private static DuckDBParameter Param(string name, object? value)
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
