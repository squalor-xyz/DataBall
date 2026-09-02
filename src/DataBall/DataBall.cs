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
    public sealed partial class DataBall : IDisposable
    {
        private readonly ILogger _logger;
        private readonly DuckDbStore _store;
        private readonly Dictionary<string, Type> _expectedColumnTypes;
        private readonly List<Relationship> _relationships;
        private Config _config;
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
            _config = string.IsNullOrEmpty(configPath)
                ? Config.CreateDefaults()
                : Config.LoadMerged(configPath);
            ApplyConfig(_config);
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
        /// Gets the merged schema config (native defaults plus any overlay file).
        /// </summary>
        public Config Schema => _config;

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
            ThrowIfPendingRow();
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
        /// Compacts in-memory data: extracts strict constants (one distinct non-null value and no nulls)
        /// into metadata, then DISTINCT. The last remaining constant column is extracted too; if that
        /// leaves no columns, the data table is dropped (DuckDB cannot store a 0-column table).
        /// Columns named in <paramref name="partitionColumns"/> are not extracted so hive directories
        /// can still be written. Optionally exports hive-partitioned Parquet when both a path and
        /// partition columns are supplied.
        /// </summary>
        /// <param name="partitionedParquetPath">The path to export partitioned Parquet files, if any.</param>
        /// <param name="partitionColumns">The columns to partition by, if any.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        /// <exception cref="DataBallException">Thrown when the Bounce operation fails.</exception>
        public Task Bounce(string? partitionedParquetPath = null, string[]? partitionColumns = null)
        {
            ThrowIfDisposed();
            ThrowIfPendingRow();
            _logger.LogInformation("Starting Bounce operation");
            try
            {
                if (!_store.DataTableExists())
                {
                    _logger.LogDebug("Bounce skipped; no data table");
                    return Task.CompletedTask;
                }

                var writePartitioned = !string.IsNullOrEmpty(partitionedParquetPath) && partitionColumns is { Length: > 0 };
                // Resolve against the pre-extraction schema so a typo names the missing column instead of exporting after DROP TABLE.
                if (writePartitioned)
                    _store.ResolvePartitionColumns(partitionColumns!);

                ExtractConstantsToMetadataSql(partitionColumns);
                DistinctInPlace();
                if (writePartitioned)
                {
                    _store.ExportPartitionedParquet(partitionedParquetPath!, partitionColumns!);
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
        /// Performs the Squish operation. Delegates to <see cref="Bounce"/> (same compaction:
        /// strict no-nulls constant extraction and DISTINCT). Optionally writes hive-partitioned Parquet.
        /// </summary>
        /// <param name="partitionedParquetPath">The path to export partitioned Parquet files, if any.</param>
        /// <param name="partitionColumns">The columns to partition by, if any.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        /// <exception cref="DataBallException">Thrown when the Squish operation fails.</exception>
        public Task Squish(string? partitionedParquetPath = null, string[]? partitionColumns = null)
        {
            return Bounce(partitionedParquetPath, partitionColumns);
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
            ThrowIfPendingRow();
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
                        _store.ImportCsv(path, append, _expectedColumnTypes);
                        ApplyCsvSchema(path);
                        break;
                    case ExportType.Parquet:
                        _store.ImportParquet(path, append);
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
        /// <returns>A completed task after the export finishes.</returns>
        /// <exception cref="DataBallException">Thrown when export fails.</exception>
        public Task ExportAsync(string path, ExportType type)
        {
            ThrowIfDisposed();
            ThrowIfPendingRow();
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
        /// <exception cref="DataBallException">Thrown when the export operation fails.</exception>
        public void Roll(ExportType type, string path)
        {
            ThrowIfDisposed();
            ExportAsync(path, type).GetAwaiter().GetResult();
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
            if (_pendingRow is not null)
                _logger.LogWarning("Disposing with an uncommitted pending row; it will be discarded.");
            _store.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        private void ExtractConstantsToMetadataSql(string[]? partitionColumns)
        {
            if (!_store.DataTableExists() || _store.RowCount() == 0)
                return;

            // Hive PARTITION_BY needs these columns left in the table.
            HashSet<string>? skip = null;
            if (partitionColumns is { Length: > 0 })
                skip = new HashSet<string>(partitionColumns, StringComparer.OrdinalIgnoreCase);

            foreach (var (name, _) in _store.GetColumns().ToList())
            {
                // Last remaining constant column drops "data"; stop rather than query a missing table.
                if (!_store.DataTableExists())
                    break;
                if (skip is not null && skip.Contains(name))
                    continue;

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
            // Extracting the last constant column drops "data"; DuckDB cannot DISTINCT a 0-column table.
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
            _config = Config.Merge(_config, config);
            ApplyConfig(_config);
        }

        internal void ApplyCsvSchema(string csvPath)
        {
            if (!_store.DataTableExists())
                return;

            var headers = ReadCsvHeaders(csvPath);
            if (headers.Count == 0)
                return;

            var parsed = SchemaResolver.ResolveAll(headers, _config);
            var tableCols = _store.GetColumns();
            foreach (var col in parsed)
            {
                if (col.ClrType is not null)
                    _expectedColumnTypes[col.Name] = col.ClrType;
                var match = tableCols.FirstOrDefault(c => c.Name.Equals(col.Raw, StringComparison.OrdinalIgnoreCase));
                if (match.Name is null)
                    match = tableCols.FirstOrDefault(c => c.Name.Equals(col.Name, StringComparison.OrdinalIgnoreCase));
                if (match.Name is null)
                    continue;
                if (!match.Name.Equals(col.Name, StringComparison.Ordinal))
                    _store.RenameColumn(match.Name, col.Name);
            }

            _store.CoerceDataColumns(_expectedColumnTypes);
            ExtractConfiguredMetadata();
        }

        private void ApplyConfig(Config config)
        {
            foreach (var (key, value) in config.Metadata)
                _store.SetMetadata(key, DuckDbStore.Unwrap(value));
            foreach (var col in config.Columns)
                _expectedColumnTypes[col.Key] = Config.ParseColumnType(col.Value);
            if (config.Relationships.Count > 0)
            {
                _relationships.Clear();
                _relationships.AddRange(config.Relationships);
            }
        }

        private void ExtractConfiguredMetadata()
        {
            if (!_store.DataTableExists() || _config.MetadataFields.Count == 0)
                return;

            var policy = _config.MetadataPolicy ?? "requireConstant";
            foreach (var field in _config.MetadataFields.ToList())
            {
                var cols = _store.GetColumns();
                var match = cols.FirstOrDefault(c => c.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
                if (match.Name is null)
                    continue;

                var q = DuckDbStore.QuoteIdent(match.Name);
                var rows = _store.Query($"""
                    SELECT
                      COUNT(DISTINCT {q}) FILTER (WHERE {q} IS NOT NULL) AS d,
                      COUNT(*) FILTER (WHERE {q} IS NULL) AS n,
                      any_value({q}) AS v
                    FROM "data"
                    """);
                var distinct = Convert.ToInt64(rows[0]["d"], CultureInfo.InvariantCulture);
                var nulls = Convert.ToInt64(rows[0]["n"], CultureInfo.InvariantCulture);
                var value = rows[0]["v"];

                if (policy.Equals("first", StringComparison.OrdinalIgnoreCase))
                {
                    var first = _store.Query($"SELECT {q} FROM \"data\" WHERE {q} IS NOT NULL LIMIT 1");
                    if (first.Count > 0)
                        _store.SetMetadata(match.Name, first[0][match.Name]);
                    _store.RemoveColumn(match.Name);
                    _expectedColumnTypes.Remove(match.Name);
                    continue;
                }

                if (distinct == 1 && (nulls == 0 || policy.Equals("bounce", StringComparison.OrdinalIgnoreCase) is false))
                {
                    if (policy.Equals("bounce", StringComparison.OrdinalIgnoreCase) && nulls > 0)
                        continue;
                    _store.SetMetadata(match.Name, value);
                    _store.RemoveColumn(match.Name);
                    _expectedColumnTypes.Remove(match.Name);
                    continue;
                }

                if (policy.Equals("requireConstant", StringComparison.OrdinalIgnoreCase) && distinct > 1)
                    throw new DataBallException($"Metadata field '{match.Name}' is not constant");
            }
        }

        private static IReadOnlyList<string> ReadCsvHeaders(string path)
        {
            using var reader = new StreamReader(path);
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
                return Array.Empty<string>();
            if (line.Length > 0 && line[0] == '\uFEFF')
                line = line[1..];
            return SplitCsvLine(line);
        }

        private static List<string> SplitCsvLine(string line)
        {
            var result = new List<string>();
            var current = new System.Text.StringBuilder();
            var inQuotes = false;
            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            current.Append('"');
                            i++;
                        }
                        else
                            inQuotes = false;
                    }
                    else
                        current.Append(ch);
                }
                else if (ch == '"')
                    inQuotes = true;
                else if (ch == ',')
                {
                    result.Add(current.ToString().Trim());
                    current.Clear();
                }
                else
                    current.Append(ch);
            }
            result.Add(current.ToString().Trim());
            return result;
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
