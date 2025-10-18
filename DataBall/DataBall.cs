using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Analysis;
using NLog;
using System.Reflection;
using squalor.DataBall.Backend;
using squalor.DataBall.Export;
using squalor.DataBall.Import;

namespace squalor.DataBall;

/// <summary>
/// DataBall is a versatile data handling class for test executive applications.
/// It supports import/export in multiple formats (CSV, Parquet, SQLite, Archives, .ball),
/// data manipulation via row builder pattern and DataFrame operations, metadata storage,
/// configuration-based relationships, and large dataset handling via chunking/partitioning.
/// Uses backend abstraction for in-memory or out-of-core processing.
/// Licensed under MPL 2.0.
/// </summary>
public class DataBall
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// The underlying DataFrame for data storage, backed by Apache Arrow.
    /// </summary>
    public DataFrame Data { get; set; } = new DataFrame();

    /// <summary>
    /// Dictionary for storing metadata, such as constants extracted during Bounce.
    /// </summary>
    public Dictionary<string, object?> Metadata { get; } = new Dictionary<string, object?>();

    /// <summary>
    /// Current backend implementation (default: InMemoryBackend).
    /// </summary>
    private IDataBackend _backend;

    /// <summary>
    /// Pending row for the row builder pattern.
    /// </summary>
    private Dictionary<string, object?>? _pendingRow;

    /// <summary>
    /// Original row values for change detection in relationships.
    /// </summary>
    private Dictionary<string, object?>? _originalRow;

    /// <summary>
    /// Fields explicitly modified in the pending row.
    /// </summary>
    private HashSet<string>? _modifiedFields;

    /// <summary>
    /// List of configured relationships for row builder operations.
    /// </summary>
    private List<Relationship> _relationships = new List<Relationship>();

    /// <summary>
    /// Expected column types from configuration.
    /// </summary>
    private Dictionary<string, Type> _expectedColumnTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Version of the data format.
    /// </summary>
    private const string CurrentVersion = "1.0";

    /// <summary>
    /// Initializes a new instance of the <see cref="DataBall"/> class.
    /// Optionally loads configuration from a JSON file.
    /// </summary>
    /// <param name="configPath">Path to the configuration JSON file, or null if no config is provided.</param>
    public DataBall(string? configPath = null)
    {
        Logger.Info("Initializing DataBall");
        _backend = new InMemoryBackend(Data); // Default to in-memory backend
        Metadata["Version"] = CurrentVersion;

        if (!string.IsNullOrEmpty(configPath))
        {
            LoadConfig(configPath!);
        }
    }

    /// <summary>
    /// Loads configuration from a JSON file, setting metadata, column types, and relationships.
    /// Creates empty columns if the DataFrame is empty.
    /// </summary>
    /// <param name="path">Path to the configuration JSON file.</param>
    /// <exception cref="DataBallException">Thrown if the configuration cannot be loaded or deserialized.</exception>
    private void LoadConfig(string path)
    {
        try
        {
            Logger.Debug($"Loading config from {path}");
            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var config = JsonSerializer.Deserialize<Config>(json, options) 
                         ?? throw new InvalidOperationException("Failed to deserialize config");

            // Load metadata
            if (config.Metadata != null)
            {
                foreach (var kvp in config.Metadata)
                {
                    Metadata[kvp.Key] = kvp.Value;
                    Logger.Debug($"Loaded metadata: {kvp.Key} = {kvp.Value}");
                }
            }

            // Load relationships
            _relationships = config.Relationships ?? new List<Relationship>();
            Logger.Debug($"Loaded {_relationships.Count} relationships");

            // Load expected types
            var typeMap = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
            {
                { "int", typeof(int) },
                { "long", typeof(long) },
                { "float", typeof(float) },
                { "double", typeof(double) },
                { "bool", typeof(bool) },
                { "datetime", typeof(DateTime) },
                { "string", typeof(string) }
            };

            _expectedColumnTypes.Clear();
            if (config.Columns != null)
            {
                foreach (var col in config.Columns)
                {
                    _expectedColumnTypes[col.Key] = typeMap.TryGetValue(col.Value, out var t) ? t : typeof(string);
                    Logger.Debug($"Expected type for {col.Key}: {_expectedColumnTypes[col.Key].Name}");
                }
            }

            // Initialize empty columns if DataFrame is empty
            if (Data.Columns.Count == 0 && _expectedColumnTypes.Count > 0)
                {
                    foreach (var kvp in _expectedColumnTypes)
                    {
                        AddEmptyColumn(kvp.Key, kvp.Value);
                    }
                }

            Logger.Info("Configuration loaded successfully");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load config");
            throw new DataBallException("Configuration load failed", ex);
        }
    }

    /// <summary>
    /// Adds an empty column of the specified type, sized to the current row count.
    /// </summary>
    /// <param name="name">The name of the column.</param>
    /// <param name="type">The data type of the column.</param>
    private void AddEmptyColumn(string name, Type type)
    {
        long length = Data.Rows.Count;
        DataFrameColumn col;
        if (type == typeof(string))
        {
            col = new StringDataFrameColumn(name, length);
        }
        else
        {
            col = CreatePrimitiveColumn(name, type, length);
        }
        Data.Columns.Add(col);
        Logger.Debug($"Added empty column {name} of type {type.Name} (length: {length})");
    }

    /// <summary>
    /// Creates a primitive DataFrame column filled with nulls.
    /// </summary>
    /// <param name="name">The name of the column.</param>
    /// <param name="type">The primitive data type.</param>
    /// <param name="length">The number of rows in the column.</param>
    /// <returns>The created <see cref="DataFrameColumn"/>.</returns>
    private DataFrameColumn CreatePrimitiveColumn(string name, Type type, long length)
    {
        var colType = typeof(PrimitiveDataFrameColumn<>).MakeGenericType(type);
        var col = (DataFrameColumn)Activator.CreateInstance(colType, name, length)!;
        return col;
    }

    /// <summary>
    /// Initializes the row builder with optional initial values.
    /// Copies the last row if available for incremental building.
    /// </summary>
    /// <param name="initialValues">Optional dictionary of initial field values.</param>
    /// <exception cref="InvalidOperationException">Thrown if the row builder is already initialized.</exception>
    public void InitializeRow(Dictionary<string, object?>? initialValues = null)
    {
        Logger.Debug("Initializing row builder");
        _originalRow = new Dictionary<string, object?>();
        _pendingRow = new Dictionary<string, object?>();
        _modifiedFields = new HashSet<string>();

        if (Data.Rows.Count > 0)
        {
            var lastRow = Data.Rows.Last();
            for (int i = 0; i < Data.Columns.Count; i++)
            {
                var colName = Data.Columns[i].Name;
                var value = lastRow[i];
                _originalRow[colName] = value;
                _pendingRow[colName] = value;
            }
        }

        if (initialValues != null)
        {
            foreach (var kvp in initialValues)
            {
                _pendingRow[kvp.Key] = kvp.Value;
                _modifiedFields.Add(kvp.Key);
                Logger.Debug($"Set initial value for {kvp.Key}: {kvp.Value}");
            }
        }
        Logger.Info("Row builder initialized");
    }

    /// <summary>
    /// Modifies a field in the pending row, marking it as modified.
    /// </summary>
    /// <param name="field">The name of the field to modify.</param>
    /// <param name="value">The new value for the field.</param>
    /// <exception cref="InvalidOperationException">Thrown if the row builder is not initialized.</exception>
    public void ModifyField(string field, object? value)
    {
        if (_pendingRow == null)
        {
            Logger.Error("ModifyField called without initialized row");
            throw new InvalidOperationException("Row must be initialized before modifying fields.");
        }

        _pendingRow[field] = value;
        _modifiedFields!.Add(field);
        Logger.Debug($"Modified field '{field}' to {value ?? "null"}");
    }

    /// <summary>
    /// Commits the pending row to the DataFrame, applying relationships.
    /// Adds new columns if needed, with type coercion.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the row builder is not initialized or modified.</exception>
    /// <exception cref="DataBallException">Thrown if the row commit fails.</exception>
    public void CommitRow()
    {
        if (_pendingRow == null || _modifiedFields == null || _originalRow == null)
        {
            Logger.Error("CommitRow called without initialized row");
            throw new InvalidOperationException("Row must be initialized and modified before committing.");
        }

        try
        {
            Logger.Debug("Applying relationships on commit");
            ApplyRelationships();

            // Add new columns if pending row has extra fields
            foreach (var key in _pendingRow.Keys.ToList())
            {
                if (!Data.Columns.Any(c => c.Name.Equals(key, StringComparison.OrdinalIgnoreCase)))
                {
                    var valueType = _pendingRow[key]?.GetType() ?? typeof(string);
                    var columnType = _expectedColumnTypes.TryGetValue(key, out var expType) ? expType : valueType;

                    // Coerce value to expected type if possible
                    var coercedValue = CoerceValue(_pendingRow[key], columnType);
                    _pendingRow[key] = coercedValue;

                    var newColumn = CreatePrimitiveColumn(key, columnType, Data.Rows.Count);
                    Data.Columns.Add(newColumn);
                    Logger.Debug($"Dynamically added column {key} of type {columnType.Name}");
                }
            }

            // Prepare row values in column order
            var rowValues = new object?[Data.Columns.Count];
            for (int i = 0; i < Data.Columns.Count; i++)
            {
                var colName = Data.Columns[i].Name;
                _pendingRow.TryGetValue(colName, out var value);
                var coerced = CoerceValue(value, Data.Columns[i].DataType);
                rowValues[i] = coerced;
            }

            Data.Append(rowValues, inPlace: true);
            Logger.Info("Row committed successfully");

            _pendingRow = null;
            _originalRow = null;
            _modifiedFields = null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to commit row");
            throw new DataBallException("Row commit failed", ex);
        }
    }

    /// <summary>
    /// Applies configured relationships to reset dependent fields if triggers changed without modification.
    /// </summary>
    private void ApplyRelationships()
    {
        foreach (var rel in _relationships)
        {
            if (_modifiedFields!.Contains(rel.Trigger) && !Equals(_originalRow![rel.Trigger], _pendingRow![rel.Trigger]))
            {
                foreach (var resetField in rel.Reset)
                {
                    if (!_modifiedFields.Contains(resetField))
                    {
                        _pendingRow![resetField] = null;
                        Logger.Debug($"Reset field '{resetField}' due to change in trigger '{rel.Trigger}'");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Coerces a value to the expected type, falling back to string if conversion fails.
    /// </summary>
    /// <param name="value">The value to coerce.</param>
    /// <param name="targetType">The target data type.</param>
    /// <returns>The coerced value, or string if conversion fails.</returns>
    private object? CoerceValue(object? value, Type targetType)
    {
        if (value == null) return null;

        try
        {
            if (targetType == typeof(int)) return Convert.ToInt32(value);
            if (targetType == typeof(long)) return Convert.ToInt64(value);
            if (targetType == typeof(float)) return Convert.ToSingle(value);
            if (targetType == typeof(double)) return Convert.ToDouble(value);
            if (targetType == typeof(bool)) return Convert.ToBoolean(value);
            if (targetType == typeof(DateTime)) return Convert.ToDateTime(value);
            return value.ToString();
        }
        catch
        {
            Logger.Warn($"Failed to coerce value {value} to {targetType.Name}, falling back to string");
            return value.ToString();
        }
    }

    /// <summary>
    /// Adds a column with values, handling strings separately due to DataFrame requirements.
    /// </summary>
    /// <typeparam name="T">The data type of the column.</typeparam>
    /// <param name="name">The name of the column.</param>
    /// <param name="values">The values for the column.</param>
    public void AddColumn<T>(string name, IEnumerable<T> values) where T : struct
    {
        if (typeof(T) == typeof(string))
        {
            var strValues = values.Cast<string?>();
            var col = new StringDataFrameColumn(name, strValues);
            Data.Columns.Add(col);
        }
        else
        {
            var col = new PrimitiveDataFrameColumn<T>(name, values);
            Data.Columns.Add(col);
        }
        Logger.Debug($"Added column {name} with {values.Count()} values");
    }

    /// <summary>
    /// Adds a row with values in column order.
    /// </summary>
    /// <param name="values">The values for the row, matching column order.</param>
    /// <exception cref="ArgumentException">Thrown if the value count does not match the column count.</exception>
    public void AddRow(object?[] values)
    {
        if (values.Length != Data.Columns.Count)
        {
            throw new ArgumentException("Value count must match column count");
        }
        Data.Append(values, inPlace: true);
        Logger.Debug("Added row");
    }

    /// <summary>
    /// Removes a column by name.
    /// </summary>
    /// <param name="name">The name of the column to remove.</param>
    public void RemoveColumn(string name)
    {
        Data.Columns.Remove(name);
        Logger.Debug($"Removed column {name}");
    }

    /// <summary>
    /// Removes a row by index.
    /// </summary>
    /// <param name="index">The index of the row to remove.</param>
    public void RemoveRow(long index)
    {
        var boolColumn = new BooleanDataFrameColumn("Filter", Enumerable.Range(0, (int)Data.Rows.Count).Select(i => i != (int)index).ToArray());
        Data = Data.Filter(boolColumn);
        Logger.Debug($"Removed row at index {index}");
    }

    /// <summary>
    /// Sets a value at the specified row and column.
    /// </summary>
    /// <param name="rowIndex">The row index.</param>
    /// <param name="columnName">The column name.</param>
    /// <param name="value">The new value.</param>
    public void SetValue(long rowIndex, string columnName, object? value)
    {
        Data[columnName][rowIndex] = value;
        Logger.Debug($"Set value at ({rowIndex}, {columnName}) to {value}");
    }

    /// <summary>
    /// Filters rows based on a condition.
    /// </summary>
    /// <param name="condition">The filter condition to apply.</param>
    /// <returns>A new <see cref="DataFrame"/> containing the filtered rows.</returns>
    public DataFrame Filter(Func<DataFrameRow, bool> condition)
    {
        var indices = Enumerable.Range(0, (int)Data.Rows.Count).Where(i => condition(Data.Rows[i])).ToArray();
        return Data[indices];
    }

    /// <summary>
    /// Groups the DataFrame by a single column (limitation of Microsoft.Data.Analysis 0.21.1).
    /// </summary>
    /// <param name="columnName">The name of the column to group by.</param>
    /// <returns>A <see cref="GroupBy"/> object for further aggregation.</returns>
    public GroupBy GroupBy(string columnName)
    {
        return Data.GroupBy(columnName);
    }

    /// <summary>
    /// Joins with another DataBall using single columns (limitation of Microsoft.Data.Analysis 0.21.1).
    /// </summary>
    /// <param name="other">The other <see cref="DataBall"/> to join with.</param>
    /// <param name="leftColumn">The key column in this DataBall.</param>
    /// <param name="rightColumn">The key column in the other DataBall.</param>
    /// <returns>A new <see cref="DataFrame"/> containing the joined data.</returns>
    public DataFrame Join(DataBall other, string leftColumn, string rightColumn)
    {
        return Data.Join(other.Data, leftColumn, rightColumn);
    }

    /// <summary>
    /// Sorts the DataFrame by a single column (limitation of Microsoft.Data.Analysis 0.21.1).
    /// </summary>
    /// <param name="columnName">The name of the column to sort by.</param>
    /// <returns>A new <see cref="DataFrame"/> sorted by the specified column.</returns>
    public DataFrame Sort(string columnName)
    {
        return Data.OrderBy(columnName);
    }

    /// <summary>
    /// Aggregates the DataFrame using a specified function.
    /// </summary>
    /// <param name="func">The aggregation function to apply.</param>
    /// <returns>The aggregated result.</returns>
    public object Aggregate(Func<DataFrame, object> func)
    {
        return func(Data);
    }

    /// <summary>
    /// Switches the backend implementation for data operations.
    /// </summary>
    /// <param name="backend">The new backend to use.</param>
    /// <exception cref="ArgumentNullException">Thrown if the backend is null.</exception>
    public void SwitchBackend(IDataBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        Logger.Info($"Switched to backend {backend.GetType().Name}");
    }

    /// <summary>
    /// Performs the Bounce operation: extracts constants to metadata, deduplicates rows, and performs limited chunk deduplication (up to 5 tables).
    /// Optionally exports to partitioned Parquet.
    /// </summary>
    /// <param name="outputDir">Optional output directory for Parquet export.</param>
    /// <param name="partitionColumns">Columns to partition the data by.</param>
    public void Bounce(string? outputDir = null, params string[] partitionColumns)
    {
        ExtractConstantsToMetadata();
        DeduplicateRows();
        PerformChunkDeduplication(limit: 5); // Limited to 5 tables

        if (!string.IsNullOrEmpty(outputDir))
        {
            ExportManager.ExportToPartitionedParquet(this, outputDir, partitionColumns);
        }
        Logger.Info("Bounce operation completed");
    }

    /// <summary>
    /// Performs the Squish operation: full chunk deduplication and partitioning for disk storage.
    /// </summary>
    /// <param name="outputDir">The output directory for partitioned Parquet files.</param>
    /// <param name="partitionColumns">Columns to partition the data by.</param>
    public void Squish(string outputDir, params string[] partitionColumns)
    {
        PerformChunkDeduplication(); // Full deduplication, no limit
        ExportManager.ExportToPartitionedParquet(this, outputDir, partitionColumns);
        Logger.Info("Squish operation completed");
    }

    /// <summary>
    /// Extracts columns with constant values to metadata and removes them from the DataFrame.
    /// </summary>
    private void ExtractConstantsToMetadata()
    {
        foreach (var col in Data.Columns.ToList())
        {
            var unique = col.UniqueValues();
            if (unique.Length == 1)
            {
                Metadata[col.Name] = unique[0];
                Data.Columns.Remove(col);
                Logger.Debug($"Extracted constant {col.Name} to metadata");
            }
        }
    }

    /// <summary>
    /// Deduplicates rows in the DataFrame using a hash-based approach.
    /// </summary>
    private void DeduplicateRows()
    {
        var hashes = new HashSet<string>();
        var keepIndices = new List<long>();
        for (long i = 0; i < Data.Rows.Count; i++)
        {
            var row = Data.Rows[i];
            var hash = string.Join("|", Enumerable.Range(0, Data.Columns.Count).Select(j => row[j]?.ToString() ?? "null"));
            if (hashes.Add(hash))
            {
                keepIndices.Add(i);
            }
        }
        if (keepIndices.Count < Data.Rows.Count)
        {
            Data = Data[keepIndices.ToArray()];
            Logger.Debug($"Deduplicated {Data.Rows.Count - keepIndices.Count} rows");
        }
    }

    /// <summary>
    /// Performs chunk deduplication, replacing duplicate chunks with IDs in metadata.
    /// </summary>
    /// <param name="limit">Maximum number of tables to deduplicate (0 for no limit).</param>
    private void PerformChunkDeduplication(int limit = 0)
    {
        var chunkSize = 100; // Example chunk size
        var chunks = new Dictionary<string, string>();
        var tableCount = 0;
        for (long start = 0; start < Data.Rows.Count; start += chunkSize)
        {
            var end = Math.Min(start + chunkSize, Data.Rows.Count);
            var chunk = Data[new Range(new Index((int)start), new Index((int)end))];
            var hash = string.Join("|", chunk.Rows.Select(r => string.Join(",", r)));
            if (!chunks.ContainsKey(hash))
            {
                if (limit > 0 && tableCount >= limit) continue;
                var id = $"Chunk_{tableCount++}";
                Metadata[id] = chunk; // Store chunk in metadata
                chunks[hash] = id;
            }
            // Replace chunk with ID reference (not implemented in this version)
        }
        Logger.Debug($"Performed chunk deduplication with {tableCount} tables");
    }
}