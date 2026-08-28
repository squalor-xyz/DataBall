using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using squalor.DataBall.Export;
using squalor.DataBall.Import;

namespace squalor.DataBall
{
    /// <summary>
    /// Represents a versatile data handling class for test executive applications.
    /// </summary>
    public sealed class DataBall : IDisposable
    {
        private readonly ILogger _logger;
        private readonly DuckDbStore _store;
        private readonly Dictionary<string, Type> _expectedColumnTypes;
        private readonly List<Relationship> _relationships;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="DataBall"/> class, optionally loading configuration.
        /// </summary>
        /// <param name="configPath">The path to the configuration JSON file, if any.</param>
        /// <param name="logger">Optional logger. Defaults to a no-op logger.</param>
        public DataBall(string? configPath = null, ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
            _store = new DuckDbStore();
            _expectedColumnTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            _relationships = new List<Relationship>();
            if (!string.IsNullOrEmpty(configPath))
            {
                var config = Config.LoadConfig(configPath);
                foreach (var (key, value) in config.Metadata)
                    _store.SetMetadata(key, DuckDbStore.Unwrap(value));
                foreach (var col in config.Columns)
                    _expectedColumnTypes[col.Key] = Config.ParseColumnType(col.Value);
                _relationships = config.Relationships;
            }
        }

        /// <summary>
        /// Gets a snapshot of the metadata dictionary.
        /// </summary>
        public IReadOnlyDictionary<string, object?> Metadata
        {
            get
            {
                ThrowIfDisposed();
                return _store.SnapshotMetadata();
            }
        }

        internal DuckDbStore Store
        {
            get
            {
                ThrowIfDisposed();
                return _store;
            }
        }

        internal IReadOnlyDictionary<string, Type> ExpectedColumnTypes => _expectedColumnTypes;

        internal IReadOnlyList<Relationship> Relationships => _relationships;

        /// <summary>
        /// Sets a metadata value.
        /// </summary>
        /// <param name="key">The metadata key.</param>
        /// <param name="value">The metadata value.</param>
        public void SetMetadata(string key, object? value)
        {
            ThrowIfDisposed();
            _store.SetMetadata(key, value);
        }

        /// <summary>
        /// Adds a column of primitive values.
        /// </summary>
        /// <typeparam name="T">The type of the column values, which must be a non-nullable value type.</typeparam>
        /// <param name="name">The name of the column.</param>
        /// <param name="values">The values for the column.</param>
        public void AddColumn<T>(string name, IEnumerable<T> values) where T : struct
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(values);
            EnsureExpectedType(name, typeof(T));
            var list = values as IReadOnlyList<T> ?? values.ToList();
            _store.AddColumn(name, list);
            _expectedColumnTypes[name] = typeof(T);
        }

        /// <summary>
        /// Adds a column of string values.
        /// </summary>
        /// <param name="name">The name of the column.</param>
        /// <param name="values">The string values for the column.</param>
        public void AddColumn(string name, IEnumerable<string?> values)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(values);
            EnsureExpectedType(name, typeof(string));
            var list = values as IReadOnlyList<string?> ?? values.ToList();
            _store.AddColumn(name, list);
            _expectedColumnTypes[name] = typeof(string);
        }

        /// <summary>
        /// Removes a column.
        /// </summary>
        /// <param name="name">The name of the column to remove.</param>
        public void RemoveColumn(string name)
        {
            ThrowIfDisposed();
            _store.RemoveColumn(name);
            _expectedColumnTypes.Remove(name);
        }

        /// <summary>
        /// Inserts a row of values, creating the table or adding missing columns as needed.
        /// </summary>
        /// <param name="values">The column values for the new row.</param>
        public void AddRow(IReadOnlyDictionary<string, object?> values)
        {
            ThrowIfDisposed();
            _store.AddRow(values, _expectedColumnTypes);
        }

        /// <summary>
        /// Executes SQL against the in-memory store and returns rows as dictionaries.
        /// </summary>
        /// <param name="sql">The SQL to execute.</param>
        /// <returns>Query results as dictionaries keyed by column name.</returns>
        /// <exception cref="DataBallException">Thrown when the SQL is empty or execution fails.</exception>
        public IReadOnlyList<Dictionary<string, object?>> Query(string sql)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(sql))
                throw new DataBallException("SQL is required");
            try
            {
                return _store.Query(sql);
            }
            catch (Exception ex) when (ex is not DataBallException)
            {
                throw new DataBallException("Query failed", ex);
            }
        }

        /// <summary>
        /// Merges or appends another DataBall, handling schema differences.
        /// </summary>
        /// <param name="other">The DataBall to merge or append.</param>
        /// <param name="append">If true, appends data; otherwise, replaces existing data.</param>
        public void MergeOrAppend(DataBall other, bool append)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(other);
            ObjectDisposedException.ThrowIf(other._disposed, other);

            _logger.LogDebug("Merging or appending DataBall");
            try
            {
                if (!other.Store.DataTableExists())
                {
                    if (!append || !_store.DataTableExists() || _store.RowCount() == 0)
                    {
                        if (_store.DataTableExists())
                            _store.Execute("DROP TABLE IF EXISTS \"data\"");
                    }
                    _logger.LogDebug("Merge/append completed");
                    return;
                }

                var dir = Path.Combine(Path.GetTempPath(), "databall-merge-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    var path = Path.Combine(dir, "data.parquet");
                    other.Store.ExportParquet(path);
                    _store.ImportParquet(path, append);
                }
                finally
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, true);
                }
                _logger.LogDebug("Merge/append completed");
            }
            catch (Exception ex) when (ex is not DataBallException and not ObjectDisposedException)
            {
                _logger.LogError(ex, "Merge/append failed");
                throw new DataBallException("Failed to merge or append DataBall", ex);
            }
        }

        /// <summary>
        /// Performs the Bounce operation, extracting constants, deduplicating rows, and optionally exporting to partitioned Parquet.
        /// </summary>
        /// <param name="partitionedParquetPath">The path to export partitioned Parquet files, if any.</param>
        /// <param name="partitionColumns">The columns to partition by, if any.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        /// <exception cref="DataBallException">Thrown when the Bounce operation fails.</exception>
        public Task Bounce(string? partitionedParquetPath = null, string[]? partitionColumns = null)
        {
            ThrowIfDisposed();
            _logger.LogInformation("Starting Bounce operation");
            try
            {
                if (!_store.DataTableExists())
                {
                    _logger.LogDebug("Bounce skipped; no data table");
                    return Task.CompletedTask;
                }

                ExtractConstantsToMetadataSql();
                DistinctInPlace();
                if (!string.IsNullOrEmpty(partitionedParquetPath) && partitionColumns is { Length: > 0 })
                {
                    _store.ExportPartitionedParquet(partitionedParquetPath, partitionColumns);
                    _logger.LogInformation($"Exported to partitioned Parquet at {partitionedParquetPath}");
                }
                _logger.LogInformation("Bounce operation completed");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bounce operation failed");
                throw new DataBallException("Bounce operation failed", ex);
            }
        }

        /// <summary>
        /// Performs the Squish operation, applying full deduplication and partitioning, and optionally exporting to Parquet.
        /// </summary>
        /// <param name="partitionedParquetPath">The path to export partitioned Parquet files, if any.</param>
        /// <param name="partitionColumns">The columns to partition by, if any.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        /// <exception cref="DataBallException">Thrown when the Squish operation fails.</exception>
        public Task Squish(string? partitionedParquetPath = null, string[]? partitionColumns = null)
        {
            ThrowIfDisposed();
            _logger.LogInformation("Starting Squish operation");
            try
            {
                if (!_store.DataTableExists())
                {
                    _logger.LogDebug("Squish skipped; no data table");
                    return Task.CompletedTask;
                }

                ExtractConstantsToMetadataSql();
                DistinctInPlace();
                if (!string.IsNullOrEmpty(partitionedParquetPath) && partitionColumns is { Length: > 0 })
                {
                    _store.ExportPartitionedParquet(partitionedParquetPath, partitionColumns);
                    _logger.LogInformation($"Exported to partitioned Parquet at {partitionedParquetPath}");
                }
                _logger.LogInformation("Squish operation completed");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Squish operation failed");
                throw new DataBallException("Squish operation failed", ex);
            }
        }

        /// <summary>
        /// Imports data from a file, detecting the format by extension.
        /// </summary>
        /// <param name="path">The path of the file to import.</param>
        /// <param name="options">Optional import settings. <see cref="ImportOptions.Append"/> defaults to <c>false</c>.</param>
        /// <returns>A completed task after the import finishes.</returns>
        /// <exception cref="DataBallException">Thrown when the path is missing, the format is unknown, or import fails.</exception>
        public Task ImportAsync(string path, ImportOptions? options = null)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(path))
                throw new DataBallException("Path is required");

            var format = ImportManager.DetectImportFormat(path);
            if (!File.Exists(path))
                throw new DataBallException($"File not found: {path}");

            options ??= new ImportOptions();
            var append = options.Append;
            _logger.LogInformation("Importing {Path} as {Format}, append={Append}", path, format, append);
            try
            {
                switch (format)
                {
                    case ExportType.Csv:
                        _store.ImportCsv(path, append);
                        break;
                    case ExportType.Parquet:
                        _store.ImportParquet(path, append);
                        break;
                    case ExportType.Sqlite:
                        ImportManager.ImportFromSqlite(this, path, append, options.TableName);
                        break;
                    case ExportType.Archive:
                        ImportManager.ImportFromArchive(this, path, append);
                        break;
                    case ExportType.Ball:
                        ImportManager.ImportFromBall(this, path, append);
                        break;
                    default:
                        throw new DataBallException($"Unknown import format for '{path}'");
                }
                return Task.CompletedTask;
            }
            catch (Exception ex) when (ex is not DataBallException and not ObjectDisposedException)
            {
                _logger.LogError(ex, "Import failed");
                throw new DataBallException("Failed to import", ex);
            }
        }

        /// <summary>
        /// Exports data in the specified format.
        /// </summary>
        /// <param name="path">The destination path.</param>
        /// <param name="type">The export format.</param>
        /// <param name="options">Optional export settings such as SQLite table name.</param>
        /// <returns>A completed task after the export finishes.</returns>
        /// <exception cref="DataBallException">Thrown when export fails.</exception>
        public Task ExportAsync(string path, ExportType type, ExportOptions? options = null)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(path))
                throw new DataBallException("Path is required");

            _logger.LogInformation("Exporting to {Path} as {Type}", path, type);
            try
            {
                switch (type)
                {
                    case ExportType.Csv:
                        _store.ExportCsv(path);
                        break;
                    case ExportType.Parquet:
                        _store.ExportParquet(path);
                        break;
                    case ExportType.Archive:
                        ExportManager.ExportToArchive(this, path);
                        break;
                    case ExportType.Ball:
                        ExportManager.ExportToBall(this, path);
                        break;
                    case ExportType.Sqlite:
                        ExportManager.ExportToSqlite(this, path, options?.TableName);
                        break;
                    default:
                        throw new DataBallException($"Unsupported export type: {type}");
                }
                return Task.CompletedTask;
            }
            catch (Exception ex) when (ex is not DataBallException and not ObjectDisposedException)
            {
                _logger.LogError(ex, "Export failed");
                throw new DataBallException($"Failed to export to {type}", ex);
            }
        }

        /// <summary>
        /// Saves the data to the specified path using the .ball format.
        /// </summary>
        /// <param name="path">The path to save the .ball file.</param>
        /// <returns>A completed task after the save finishes.</returns>
        /// <exception cref="DataBallException">Thrown when the save operation fails.</exception>
        public Task SaveAsync(string path)
        {
            ThrowIfDisposed();
            return ExportAsync(path, ExportType.Ball);
        }

        /// <summary>
        /// Exports data in the specified format.
        /// </summary>
        /// <param name="type">The export format.</param>
        /// <param name="path">The destination path.</param>
        /// <param name="options">Optional export settings such as SQLite table name.</param>
        /// <exception cref="DataBallException">Thrown when the export operation fails.</exception>
        public void Roll(ExportType type, string path, ExportOptions? options = null)
        {
            ThrowIfDisposed();
            ExportAsync(path, type, options).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Exports the data to an archive (ZIP, TAR.GZ, or TAR.XZ).
        /// </summary>
        /// <param name="path">The path to save the archive.</param>
        /// <exception cref="DataBallException">Thrown when the export operation fails.</exception>
        public void ExportToArchive(string path)
        {
            ThrowIfDisposed();
            ExportAsync(path, ExportType.Archive).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Saves the data to the specified path using the .ball format.
        /// </summary>
        /// <param name="path">The path to save the .ball file.</param>
        /// <exception cref="DataBallException">Thrown when the save operation fails.</exception>
        public void Save(string path)
        {
            ThrowIfDisposed();
            SaveAsync(path).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Releases the underlying DuckDB connection.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _store.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        private void ExtractConstantsToMetadataSql()
        {
            if (!_store.DataTableExists() || _store.RowCount() == 0)
                return;

            foreach (var (name, _) in _store.GetColumns().ToList())
            {
                if (_store.GetColumns().Count <= 1)
                    break;

                var q = DuckDbStore.QuoteIdent(name);
                var rows = _store.Query($"""
                    SELECT
                      COUNT(DISTINCT {q}) FILTER (WHERE {q} IS NOT NULL) AS d,
                      COUNT(*) FILTER (WHERE {q} IS NULL) AS n,
                      any_value({q}) AS v
                    FROM "data"
                    """);
                var d = Convert.ToInt64(rows[0]["d"], CultureInfo.InvariantCulture);
                var n = Convert.ToInt64(rows[0]["n"], CultureInfo.InvariantCulture);
                if (d == 1 && n == 0)
                {
                    _store.SetMetadata(name, rows[0]["v"]);
                    _store.RemoveColumn(name);
                    _expectedColumnTypes.Remove(name);
                }
            }
        }

        private void DistinctInPlace()
        {
            if (!_store.DataTableExists())
                return;
            _store.Execute("CREATE TABLE \"data_new\" AS SELECT DISTINCT * FROM \"data\"");
            _store.Execute("DROP TABLE \"data\"");
            _store.Execute("ALTER TABLE \"data_new\" RENAME TO \"data\"");
        }

        private void EnsureExpectedType(string name, Type actual)
        {
            if (_expectedColumnTypes.TryGetValue(name, out var expected) && expected != actual)
                throw new DataBallException($"Column '{name}' expected type {expected.Name}, got {actual.Name}");
        }

        internal void ApplyImportedConfig(Config config)
        {
            ArgumentNullException.ThrowIfNull(config);
            foreach (var col in config.Columns)
                _expectedColumnTypes[col.Key] = Config.ParseColumnType(col.Value);
            if (config.Relationships.Count > 0)
            {
                _relationships.Clear();
                _relationships.AddRange(config.Relationships);
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
