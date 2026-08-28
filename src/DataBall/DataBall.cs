using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Analysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using squalor.DataBall.Backend;
using squalor.DataBall.Export;
using squalor.DataBall.Import;

namespace squalor.DataBall
{
    /// <summary>
    /// Represents a versatile data handling class for test executive applications.
    /// </summary>
    public class DataBall
    {
        private readonly ILogger _logger;
        private DataFrame Data;
        private readonly Dictionary<string, object?> Metadata;
        private readonly Dictionary<string, Type> ExpectedColumnTypes;
        private readonly List<Relationship> Relationships;
        private readonly int MaxChunkSize = 1000;
        private readonly int MinChunkSize = 100;

        /// <summary>
        /// Gets the underlying DataFrame.
        /// </summary>
        public DataFrame DataFrame => Data;

        /// <summary>
        /// Initializes a new instance of the <see cref="DataBall"/> class, optionally loading configuration.
        /// </summary>
        /// <param name="configPath">The path to the configuration JSON file, if any.</param>
        /// <param name="logger">Optional logger. Defaults to a no-op logger.</param>
        public DataBall(string? configPath = null, ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
            Data = new DataFrame();
            Metadata = new Dictionary<string, object?>();
            ExpectedColumnTypes = new Dictionary<string, Type>();
            Relationships = new List<Relationship>();
            if (!string.IsNullOrEmpty(configPath))
            {
                var config = Config.LoadConfig(configPath);
                Metadata = config.Metadata;
                foreach (var col in config.Columns)
                {
                    ExpectedColumnTypes[col.Key] = Type.GetType(col.Value) ?? typeof(string);
                }
                Relationships = config.Relationships;
            }
        }

        /// <summary>
        /// Adds a column of primitive values to the DataFrame.
        /// </summary>
        /// <typeparam name="T">The type of the column values, which must be a non-nullable value type.</typeparam>
        /// <param name="name">The name of the column.</param>
        /// <param name="values">The values for the column.</param>
        public void AddColumn<T>(string name, IEnumerable<T> values) where T : unmanaged
        {
            Data.Columns.Add(new PrimitiveDataFrameColumn<T>(name, values));
            ExpectedColumnTypes[name] = typeof(T);
        }

        /// <summary>
        /// Adds a column of string values to the DataFrame.
        /// </summary>
        /// <param name="name">The name of the column.</param>
        /// <param name="values">The string values for the column.</param>
        public void AddColumn(string name, IEnumerable<string?> values)
        {
            Data.Columns.Add(new StringDataFrameColumn(name, values));
            ExpectedColumnTypes[name] = typeof(string);
        }

        /// <summary>
        /// Removes a column from the DataFrame.
        /// </summary>
        /// <param name="name">The name of the column to remove.</param>
        public void RemoveColumn(string name)
        {
            Data.Columns.Remove(name);
            ExpectedColumnTypes.Remove(name);
        }

        /// <summary>
        /// Performs the Bounce operation, extracting constants, deduplicating rows, and optionally exporting to partitioned Parquet.
        /// </summary>
        /// <param name="partitionedParquetPath">The path to export partitioned Parquet files, if any.</param>
        /// <param name="partitionColumns">The columns to partition by, if any.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        /// <exception cref="DataBallException">Thrown when the Bounce operation fails.</exception>
        public async Task Bounce(string? partitionedParquetPath = null, string[]? partitionColumns = null)
        {
            _logger.LogInformation("Starting Bounce operation");
            try
            {
                ExtractConstantsToMetadata();
                Data = DeduplicateDataFrame(Data);
                var metadataTables = new List<DataFrame>();
                for (int size = MaxChunkSize; size >= MinChunkSize && metadataTables.Count < 5; size--)
                {
                    var chunks = ExtractChunks(Data, size, size);
                    var uniqueTable = DedupToTable(chunks);
                    metadataTables.Add(uniqueTable);
                    ReplaceWithIDs(Data, uniqueTable, size, size);
                }
                Metadata["ChunkMetadataTables"] = metadataTables;
                _logger.LogDebug($"Stored {metadataTables.Count} chunk tables");

                if (!string.IsNullOrEmpty(partitionedParquetPath) && partitionColumns != null && partitionColumns.Length > 0)
                {
                    await ExportManager.ExportToPartitionedParquet(Data, partitionedParquetPath, partitionColumns).ConfigureAwait(false);
                    _logger.LogInformation($"Exported to partitioned Parquet at {partitionedParquetPath}");
                }
                _logger.LogInformation("Bounce operation completed");
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
        public async Task Squish(string? partitionedParquetPath = null, string[]? partitionColumns = null)
        {
            _logger.LogInformation("Starting Squish operation");
            try
            {
                ExtractConstantsToMetadata();
                Data = DeduplicateDataFrame(Data);
                var metadataTables = new List<DataFrame>();
                for (int size = MaxChunkSize; size >= MinChunkSize; size--)
                {
                    var chunks = ExtractChunks(Data, size, size);
                    var uniqueTable = DedupToTable(chunks);
                    metadataTables.Add(uniqueTable);
                    ReplaceWithIDs(Data, uniqueTable, size, size);
                }
                Metadata["ChunkMetadataTables"] = metadataTables;
                _logger.LogDebug($"Stored {metadataTables.Count} chunk tables");

                if (!string.IsNullOrEmpty(partitionedParquetPath) && partitionColumns != null && partitionColumns.Length > 0)
                {
                    await ExportManager.ExportToPartitionedParquet(Data, partitionedParquetPath, partitionColumns).ConfigureAwait(false);
                    _logger.LogInformation($"Exported to partitioned Parquet at {partitionedParquetPath}");
                }
                _logger.LogInformation("Squish operation completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Squish operation failed");
                throw new DataBallException("Squish operation failed", ex);
            }
        }

        /// <summary>
        /// Exports the DataFrame to an archive (ZIP, TAR.GZ, or TAR.XZ).
        /// </summary>
        /// <param name="path">The path to save the archive.</param>
        /// <exception cref="DataBallException">Thrown when the export operation fails.</exception>
        public void ExportToArchive(string path)
        {
            ExportManager.ExportToArchive(Data, path);
        }

        /// <summary>
        /// Saves the DataFrame to the specified path using the .ball format.
        /// </summary>
        /// <param name="path">The path to save the .ball file.</param>
        /// <exception cref="DataBallException">Thrown when the save operation fails.</exception>
        public void Save(string path)
        {
            ExportManager.Roll(Data, ExportType.Ball, path);
        }

        /// <summary>
        /// Merges or appends another DataFrame to the current DataFrame, handling schema differences.
        /// </summary>
        /// <param name="df">The DataFrame to merge or append.</param>
        /// <param name="append">If true, appends data; otherwise, replaces existing data.</param>
        public void MergeOrAppend(DataFrame df, bool append)
        {
            _logger.LogDebug("Merging or appending DataFrame");
            try
            {
                if (!append || Data.Rows.Count == 0)
                {
                    Data = df.Clone();
                    return;
                }

                foreach (var kvp in Metadata.ToList())
                {
                    string key = kvp.Key;
                    object? val = kvp.Value;
                    bool keepInMetadata = true;
                    if (Data.Columns.Any(c => c.Name == key)) continue;
                    if (df.Columns.Any(c => c.Name == key))
                    {
                        var col = df.Columns.First(c => c.Name == key);
                        bool allMatch = true;
                        for (long j = 0; j < col.Length; j++)
                            if (!Equals(col[j], val)) { allMatch = false; break; }
                        if (allMatch)
                            df.Columns.Remove(key);
                        else
                        {
                            AddConstantColumn(key, val, Data.Rows.Count);
                            keepInMetadata = false;
                        }
                    }
                    else
                    {
                        AddConstantColumn(key, val, df.Rows.Count);
                    }
                    if (!keepInMetadata) Metadata.Remove(key);
                }

                var allColumnNames = Data.Columns.Select(c => c.Name).Union(df.Columns.Select(c => c.Name)).ToList();
                var newData = new DataFrame();
                foreach (var colName in allColumnNames)
                {
                    Type type = ExpectedColumnTypes.ContainsKey(colName)
                        ? ExpectedColumnTypes[colName]
                        : Data.Columns.Any(c => c.Name == colName)
                            ? Data.Columns.First(c => c.Name == colName).DataType
                            : df.Columns.Any(c => c.Name == colName)
                                ? df.Columns.First(c => c.Name == colName).DataType
                                : typeof(string);
                    DataFrameColumn newCol;
                    long newLength = Data.Rows.Count + df.Rows.Count;
                    if (type == typeof(string))
                        newCol = new StringDataFrameColumn(colName, newLength);
                    else
                        newCol = CreatePrimitiveColumn(colName, type, newLength);

                    if (Data.Columns.Any(c => c.Name == colName))
                    {
                        var dataCol = Data.Columns.First(c => c.Name == colName);
                        for (long i = 0; i < Data.Rows.Count; i++)
                            SetColumnValue(newCol, i, dataCol[i], type);
                    }
                    if (df.Columns.Any(c => c.Name == colName))
                    {
                        var dfCol = df.Columns.First(c => c.Name == colName);
                        for (long i = 0; i < df.Rows.Count; i++)
                            SetColumnValue(newCol, Data.Rows.Count + i, dfCol[i], type);
                    }
                    newData.Columns.Add(newCol);
                }
                Data = newData;
                _logger.LogDebug("Merge/append completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Merge/append failed");
                throw new DataBallException("Failed to merge or append DataFrame", ex);
            }
        }

        private void ExtractConstantsToMetadata()
        {
            _logger.LogDebug("Extracting constant columns to metadata");
            var columnsToRemove = new List<string>();
            for (int i = 0; i < Data.Columns.Count; i++)
            {
                var col = Data.Columns[i];
                if (col.Cast<object?>().Distinct().Count() == 1 && col[0] != null)
                {
                    Metadata[col.Name] = col[0];
                    columnsToRemove.Add(col.Name);
                    _logger.LogDebug($"Moved constant column {col.Name} to metadata");
                }
            }
            foreach (var name in columnsToRemove)
                RemoveColumn(name);
        }

        private DataFrame DeduplicateDataFrame(DataFrame df)
        {
            _logger.LogDebug("Dropping duplicate rows");
            var uniqueRows = new HashSet<string>();
            var result = new DataFrame();
            foreach (var col in df.Columns)
            {
                result.Columns.Add(col.Clone());
            }
            for (long i = 0; i < df.Rows.Count; i++)
            {
                var rowKey = string.Join("|", df.Rows[i]);
                if (uniqueRows.Add(rowKey))
                {
                    result.Append(df.Rows[i], inPlace: true);
                }
            }
            return result;
        }

        private List<DataFrame> ExtractChunks(DataFrame df, int rows, int cols)
        {
            _logger.LogDebug("Extracting chunks of size {0}x{1}", rows, cols);
            var chunks = new List<DataFrame>();
            for (long r = 0; r < df.Rows.Count; r += rows)
            {
                for (int c = 0; c < df.Columns.Count; c += cols)
                {
                    var chunk = new DataFrame(df.Columns.Skip(c).Take(cols).ToArray());
                    for (long i = r; i < r + rows && i < df.Rows.Count; i++)
                    {
                        var row = new object[cols];
                        for (int j = 0; j < cols; j++)
                        {
                            row[j] = df.Columns[c + j][i];
                        }
                        chunk.Append(row, inPlace: true);
                    }
                    chunks.Add(chunk);
                }
            }
            return chunks;
        }

        private DataFrame DedupToTable(List<DataFrame> chunks)
        {
            _logger.LogDebug("Deduplicating chunks to table");
            var uniqueTable = new DataFrame();
            uniqueTable.Columns.Add(new PrimitiveDataFrameColumn<long>("ID", 0));
            foreach (var col in chunks[0].Columns)
            {
                uniqueTable.Columns.Add(col.Clone());
            }
            var uniqueChunks = new HashSet<string>();
            long id = 0;
            for (int i = 0; i < chunks.Count; i++)
            {
                var key = ToStringKey(chunks[i]);
                if (uniqueChunks.Add(key))
                {
                    var row = new object[uniqueTable.Columns.Count];
                    row[0] = id++;
                    for (int c = 0; c < chunks[i].Columns.Count; c++)
                    {
                        row[c + 1] = chunks[i][0, c];
                    }
                    uniqueTable.Append(row, inPlace: true);
                }
            }
            return uniqueTable;
        }

        private void ReplaceWithIDs(DataFrame df, DataFrame uniqueTable, int rows, int cols)
        {
            _logger.LogDebug("Replacing chunks with IDs");
            for (long r = 0; r < df.Rows.Count; r += rows)
            {
                for (int c = 0; c < df.Columns.Count; c += cols)
                {
                    var chunk = new DataFrame(df.Columns.Skip(c).Take(cols).ToArray());
                    for (long i = r; i < r + rows && i < df.Rows.Count; i++)
                    {
                        var row = new object[cols];
                        for (int j = 0; j < cols; j++)
                        {
                            row[j] = df.Columns[c + j][i];
                        }
                        chunk.Append(row, inPlace: true);
                    }
                    var key = ToStringKey(chunk);
                    var id = uniqueTable.Rows.First(row => row.ToStringKey(cols) == key)["ID"];
                    for (long i = 0; i < rows && i + r < df.Rows.Count; i++)
                        for (int j = 0; j < cols && c + j < df.Columns.Count; j++)
                            df.Columns[c + j][r + i] = id;
                }
            }
        }

        private string ToStringKey(DataFrame df)
        {
            var sb = new StringBuilder();
            for (long i = 0; i < df.Rows.Count; i++)
            {
                sb.Append(string.Join("|", df.Rows[i]));
                sb.Append(";");
            }
            return sb.ToString();
        }

        private void AddConstantColumn(string name, object? value, long length)
        {
            _logger.LogDebug("Adding constant column {0}", name);
            DataFrameColumn col;
            if (value is string)
                col = new StringDataFrameColumn(name, Enumerable.Repeat((string?)value, (int)length));
            else if (value is int)
                col = new PrimitiveDataFrameColumn<int>(name, Enumerable.Repeat((int)value, (int)length));
            else if (value is double)
                col = new PrimitiveDataFrameColumn<double>(name, Enumerable.Repeat((double)value, (int)length));
            else
                throw new NotSupportedException($"Unsupported type for constant column: {value?.GetType().Name}");
            Data.Columns.Add(col);
        }

        private DataFrameColumn CreatePrimitiveColumn(string name, Type type, long length)
        {
            if (type == typeof(int))
                return new PrimitiveDataFrameColumn<int>(name, length);
            if (type == typeof(double))
                return new PrimitiveDataFrameColumn<double>(name, length);
            throw new NotSupportedException($"Unsupported primitive type: {type.Name}");
        }

        private void SetColumnValue(DataFrameColumn col, long index, object? value, Type targetType)
        {
            if (value != null && value.GetType() != targetType)
            {
                try
                {
                    value = Convert.ChangeType(value.ToString(), targetType);
                    _logger.LogDebug($"Converted value for index {index} to {targetType}");
                }
                catch
                {
                    value = null;
                    _logger.LogWarning($"Failed to convert value for index {index}, set to null");
                }
            }
            col[index] = value;
        }
    }
}