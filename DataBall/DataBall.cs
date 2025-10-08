// DataBall.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Analysis;
using Microsoft.Data.Sqlite;
using Parquet;
using Parquet.Data;
using System.IO.Compression;
using SharpCompress.Readers;
using SharpCompress.Writers;
using SharpCompress.Common;
using System.Text.Json;
using System.Reflection;
using NLog;

namespace squalor.DataBall
{
    public class DataBall
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public DataFrame Data { get; private set; } = new DataFrame();
        public Dictionary<string, object?> Metadata { get; } = new Dictionary<string, object?>();

        private Dictionary<string, object?>? _pendingRow;
        private Dictionary<string, object?>? _originalRow;
        private HashSet<string>? _modifiedFields;

        private List<Relationship> Relationships { get; set; } = new List<Relationship>();
        private Dictionary<string, Type> ExpectedColumnTypes { get; set; } = new Dictionary<string, Type>();

        private string? _currentFilePath;

        public DataBall(string? configPath = null)
        {
            Logger.Info("Initializing DataBall");
            if (!string.IsNullOrEmpty(configPath))
            {
                Logger.Debug($"Loading config from {configPath}");
                LoadConfig(configPath);
            }
        }

        private void LoadConfig(string path)
        {
            Logger.Debug("Loading configuration file");
            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var config = JsonSerializer.Deserialize<Config>(json, options) ?? throw new InvalidOperationException("Failed to deserialize config");

            foreach (var kvp in config.metadata ?? new Dictionary<string, object?>())
            {
                Metadata[kvp.Key] = kvp.Value;
                Logger.Debug($"Added metadata: {kvp.Key}");
            }

            Relationships = (config.relationships ?? new List<Relationship>()).Select(r => new Relationship { trigger = r.trigger ?? string.Empty, reset = r.reset ?? new List<string>() }).ToList();
            Logger.Debug($"Loaded {Relationships.Count} relationships");

            ExpectedColumnTypes = new Dictionary<string, Type>();
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

            foreach (var col in config.columns ?? new Dictionary<string, string>())
            {
                ExpectedColumnTypes[col.Key] = typeMap.TryGetValue(col.Value, out var t) ? t : typeof(string);
                Logger.Debug($"Expected type for {col.Key}: {ExpectedColumnTypes[col.Key]}");
            }

            if (Data.Columns.Count == 0)
            {
                foreach (var exp in ExpectedColumnTypes)
                {
                    AddEmptyColumn(exp.Key, exp.Value);
                }
            }
            Logger.Info("Configuration loaded successfully");
        }

        private void AddEmptyColumn(string name, Type type)
        {
            DataFrameColumn col;
            long length = Data.Rows.Count;
            if (type == typeof(string))
            {
                col = new StringDataFrameColumn(name, length);
            }
            else
            {
                col = CreatePrimitiveColumn(name, type, length);
            }
            Data.Columns.Add(col);
            Logger.Debug($"Added empty column {name} of type {type}");
        }

        private DataFrameColumn CreatePrimitiveColumn(string name, Type type, long length)
        {
            var colType = typeof(PrimitiveDataFrameColumn<>).MakeGenericType(type);
            var col = (DataFrameColumn)Activator.CreateInstance(colType, name, length)!;
            var indexer = colType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance, null, type, new[] { typeof(long) }, null);
            for (long i = 0; i < length; i++)
            {
                indexer?.SetValue(col, null, new object[] { i });
            }
            return col;
        }

        public void InitializeRow(Dictionary<string, object?>? initialValues = null)
        {
            Logger.Debug("Initializing new row");
            _originalRow = new Dictionary<string, object?>();
            _pendingRow = new Dictionary<string, object?>();
            _modifiedFields = new HashSet<string>();

            if (Data.Rows.Count > 0)
            {
                var lastRow = Data.Rows.Last();
                for (int i = 0; i < Data.Columns.Count; i++)
                {
                    _originalRow[Data.Columns[i].Name] = lastRow[i];
                    _pendingRow[Data.Columns[i].Name] = lastRow[i];
                }
            }

            if (initialValues != null)
            {
                foreach (var kvp in initialValues)
                {
                    _pendingRow[kvp.Key] = kvp.Value;
                    _modifiedFields.Add(kvp.Key);
                    Logger.Debug($"Initial value set for {kvp.Key}");
                }
            }
        }

        public void ModifyField(string field, object? value)
        {
            if (_pendingRow == null)
            {
                Logger.Error("Attempted to modify field without initialized row");
                throw new InvalidOperationException("No pending row initialized.");
            }
            _pendingRow[field] = value;
            _modifiedFields.Add(field);
            Logger.Debug($"Modified field {field}");
        }

        public void Roll()
        {
            if (_pendingRow == null || _modifiedFields == null || _originalRow == null)
            {
                Logger.Error("Attempted to roll without initialized row");
                throw new InvalidOperationException("No pending row initialized.");
            }

            Logger.Debug("Applying relationships");
            foreach (var rel in Relationships)
            {
                bool hasPending = _pendingRow.TryGetValue(rel.trigger, out var pendingTrigger);
                bool hasOriginal = _originalRow.TryGetValue(rel.trigger, out var originalTrigger);

                if (hasPending != hasOriginal || !Equals(pendingTrigger, originalTrigger))
                {
                    foreach (var dep in rel.reset)
                    {
                        if (!_modifiedFields.Contains(dep))
                        {
                            _pendingRow[dep] = null;
                            Logger.Debug($"Reset dependent field {dep} due to trigger {rel.trigger}");
                        }
                    }
                }
            }

            foreach (var key in _pendingRow.Keys.ToList())
            {
                if (!Data.Columns.Any(c => c.Name == key))
                {
                    Type valueType = _pendingRow[key]?.GetType() ?? typeof(string);
                    Type columnType = ExpectedColumnTypes.TryGetValue(key, out var exp) ? exp : valueType;

                    DataFrameColumn newColumn;
                    long currentLength = Data.Rows.Count;

                    if (columnType == typeof(string))
                    {
                        newColumn = new StringDataFrameColumn(key, currentLength);
                    }
                    else
                    {
                        newColumn = CreatePrimitiveColumn(key, columnType, currentLength);
                    }
                    Data.Columns.Add(newColumn);
                    Logger.Debug($"Added new column {key}");
                }
            }

            var values = new object?[Data.Columns.Count];
            for (int i = 0; i < Data.Columns.Count; i++)
            {
                string columnName = Data.Columns[i].Name;
                values[i] = _pendingRow.ContainsKey(columnName) ? _pendingRow[columnName] : null;
            }

            Data.Append(values, inPlace: true);
            Logger.Info("Row committed successfully");

            _pendingRow = null;
            _originalRow = null;
            _modifiedFields = null;
        }

        public void Bounce(string? partitionedParquetPath = null, string[]? partitionColumns = null)
        {
            Logger.Info("Starting bounce operation");
            var columnsToRemove = new List<string>();
            for (int i = 0; i < Data.Columns.Count; i++)
            {
                var col = Data.Columns[i];
                object? first = null;
                bool isConstant = true;
                bool firstSet = false;
                for (long j = 0; j < col.Length; j++)
                {
                    object? val = col[j];
                    if (val == null) continue;
                    if (!firstSet)
                    {
                        first = val;
                        firstSet = true;
                    }
                    else if (!Equals(val, first))
                    {
                        isConstant = false;
                        break;
                    }
                }
                if (isConstant && firstSet)
                {
                    Metadata[col.Name] = first;
                    columnsToRemove.Add(col.Name);
                    Logger.Debug($"Moved constant column {col.Name} to metadata");
                }
            }
            foreach (var name in columnsToRemove)
            {
                RemoveColumn(name);
            }

            Data = DeduplicateDataFrame(Data);
            Logger.Debug("Dropped duplicate rows");

            if (!string.IsNullOrEmpty(partitionedParquetPath))
            {
                ExportToPartitionedParquet(partitionedParquetPath, partitionColumns ?? Array.Empty<string>());
                Logger.Info($"Exported to partitioned Parquet at {partitionedParquetPath}");
            }
            Logger.Info("Bounce operation completed");
        }

        private DataFrame DeduplicateDataFrame(DataFrame df)
        {
            var uniqueRows = new List<object?[]>();
            var seen = new HashSet<string>();
            for (long i = 0; i < df.Rows.Count; i++)
            {
                var row = df.Rows[i];
                var rowKey = string.Join("|", row.Select(v => v?.ToString() ?? ""));
                if (seen.Add(rowKey))
                {
                    uniqueRows.Add(row.ToArray());
                }
            }

            var newDf = new DataFrame();
            foreach (var col in df.Columns)
            {
                newDf.Columns.Add(col.Clone());
            }
            newDf.Append(uniqueRows, inPlace: true);
            return newDf;
        }

        public void Save(string? filePath = null, string[]? partitionColumns = null)
        {
            Logger.Info("Starting save operation");
            Bounce();

            string savePath = filePath ?? _currentFilePath ?? throw new InvalidOperationException("No file path provided for save and no current file path set.");
            Logger.Debug($"Save path: {savePath}");

            if (!savePath.EndsWith(".ball"))
            {
                savePath += ".ball";
            }

            using var fs = File.OpenWrite(savePath);
            using var zip = new System.IO.Compression.ZipArchive(fs, ZipArchiveMode.Create);

            var metadataEntry = zip.CreateEntry("metadata.json");
            using (var stream = metadataEntry.Open())
            using (var sw = new StreamWriter(stream))
            {
                var json = JsonSerializer.Serialize(Metadata);
                sw.Write(json);
                Logger.Debug("Saved metadata.json");
            }

            if (partitionColumns is { Length: > 0 })
            {
                var groupBy = Data.GroupBy(partitionColumns);
                foreach (var group in groupBy.Groupings)
                {
                    var entryName = "";
                    for (int i = 0; i < partitionColumns.Length; i++)
                    {
                        object? val = group.KeyValues[i];
                        string valStr = val?.ToString() ?? "null";
                        entryName += $"{partitionColumns[i]}={valStr}/";
                    }
                    entryName += "part-0.parquet";

                    var entry = zip.CreateEntry(entryName);
                    using var stream = entry.Open();
                    WriteParquet(group.Group, stream);
                    Logger.Debug($"Saved partitioned Parquet entry {entryName}");
                }
            }
            else
            {
                var parquetEntry = zip.CreateEntry("data.parquet");
                using var stream = parquetEntry.Open();
                WriteParquet(Data, stream);
                Logger.Debug("Saved data.parquet");
            }

            _currentFilePath = savePath;
            Logger.Info($"Saved to {savePath}");
        }

        private void WriteParquet(DataFrame df, Stream stream)
        {
            var fields = df.Columns.Select(c => new DataField(c.Name, c.DataType)).ToArray();
            var schema = new ParquetSchema(fields);
            using var writer = new ParquetWriter(schema, stream);
            using var groupWriter = writer.CreateRowGroup();
            for (int i = 0; i < df.Columns.Count; i++)
            {
                var col = df.Columns[i];
                var data = new object[col.Length];
                for (long j = 0; j < col.Length; j++)
                {
                    data[j] = col[j] ?? new object();
                }
                var dataColumn = new DataColumn(fields[i], data);
                groupWriter.WriteColumn(dataColumn);
            }
        }

        public void ExportToPartitionedParquet(string basePath, params string[] partitionColumns)
        {
            Logger.Info($"Exporting to partitioned Parquet at {basePath}");
            if (partitionColumns.Length == 0)
            {
                ExportToParquet(Path.Combine(basePath, "data.parquet"));
                return;
            }

            var groupBy = Data.GroupBy(partitionColumns);
            foreach (var group in groupBy.Groupings)
            {
                var partitionPath = basePath;
                for (int i = 0; i < partitionColumns.Length; i++)
                {
                    object? val = group.KeyValues[i];
                    string valStr = val?.ToString() ?? "null";
                    partitionPath = Path.Combine(partitionPath, $"{partitionColumns[i]}={valStr}");
                }
                Directory.CreateDirectory(partitionPath);

                var filePath = Path.Combine(partitionPath, "part-0.parquet");
                using var fs = File.OpenWrite(filePath);
                WriteParquet(group.Group, fs);
                Logger.Debug($"Exported partition to {filePath}");
            }
        }

        public void AddColumn<T>(string name, IEnumerable<T> values)
        {
            DataFrameColumn column;
            if (typeof(T) == typeof(string))
            {
                column = new StringDataFrameColumn(name, values.Cast<string?>());
            }
            else
            {
                column = new PrimitiveDataFrameColumn<T>(name, values);
            }
            Data.Columns.Add(column);
            Logger.Debug($"Added column {name}");
        }

        public void RemoveColumn(string name)
        {
            Data.Columns.Remove(name);
            Logger.Debug($"Removed column {name}");
        }

        public void AddRow(IEnumerable<object?> values)
        {
            Data.Append(values, inPlace: true);
            Logger.Debug("Added row");
        }

        public void RemoveRow(long index)
        {
            var newDf = new DataFrame();
            foreach (var col in Data.Columns)
            {
                newDf.Columns.Add(col.Clone());
            }
            for (long i = 0; i < Data.Rows.Count; i++)
            {
                if (i != index)
                {
                    newDf.Append(Data.Rows[i], inPlace: true);
                }
            }
            Data = newDf;
            Logger.Debug($"Removed row at index {index}");
        }

        public void SetValue(long rowIndex, string columnName, object? value)
        {
            var column = Data[columnName];
            column[rowIndex] = value;
            Logger.Debug($"Set value at row {rowIndex}, column {columnName}");
        }

        public void ImportFromCsv(string path, bool append = false, int? chunkSize = null)
        {
            Logger.Info($"Importing from CSV {path}");
            if (chunkSize.HasValue)
            {
                using var reader = new StreamReader(path);
                var header = reader.ReadLine();
                if (header == null) throw new InvalidOperationException("CSV file is empty");
                var columns = header.Split(',');

                if (!append || Data.Rows.Count == 0)
                {
                    foreach (var col in columns)
                    {
                        AddEmptyColumn(col, typeof(string));
                    }
                }

                var chunk = new List<string[]>();
                string? line;
                int chunkCount = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    chunk.Add(line.Split(','));
                    if (chunk.Count == chunkSize.Value)
                    {
                        AppendChunk(chunk, columns);
                        chunk.Clear();
                        chunkCount++;
                        Logger.Debug($"Processed chunk {chunkCount}");
                    }
                }
                if (chunk.Count > 0)
                {
                    AppendChunk(chunk, columns);
                    Logger.Debug("Processed final chunk");
                }
            }
            else
            {
                var df = DataFrame.LoadCsv(path);
                MergeOrAppend(df, append);
            }
            Logger.Info("CSV import completed");
        }

        private void AppendChunk(List<string[]> chunk, string[] columns)
        {
            var df = new DataFrame();
            for (int i = 0; i < columns.Length; i++)
            {
                var values = chunk.Select(row => row[i]).ToArray();
                df.AddColumn(values, columns[i]);
            }
            MergeOrAppend(df, true);
        }

        public void ImportFromParquet(string path, bool append = false)
        {
            Logger.Info($"Importing from Parquet {path}");
            if (Directory.Exists(path))
            {
                var parquetFiles = Directory.GetFiles(path, "*.parquet", SearchOption.AllDirectories);
                bool localAppend = append;
                foreach (var file in parquetFiles)
                {
                    var df = ReadParquet(file);
                    MergeOrAppend(df, localAppend);
                    localAppend = true;
                    Logger.Debug($"Imported from {file}");
                }
            }
            else
            {
                var df = ReadParquet(path);
                MergeOrAppend(df, append);
            }
            Logger.Info("Parquet import completed");
        }

        private DataFrame ReadParquet(string path)
        {
            using var reader = ParquetReader.CreateAsync(File.OpenRead(path)).GetAwaiter().GetResult();
            var table = reader.ReadEntireRowGroup(0); // Assume single row group for simplicity; extend for multiple
            return FromParquetTable(table);
        }

        private DataFrame FromParquetTable(RowGroup rowGroup)
        {
            var df = new DataFrame();
            for (int i = 0; i < rowGroup.Schema.DataFields.Length; i++)
            {
                var field = rowGroup.Schema.DataFields[i];
                var col = rowGroup.ReadColumn(field).Data;
                if (field.ClrType == typeof(string))
                {
                    df.AddColumn(field.Name, col.Cast<string?>());
                } else if (field.ClrType == typeof(int))
                {
                    df.AddColumn(field.Name, col.Cast<int>());
                } // add for other types
            }
            return df;
        }

        public void ImportFromSqlite(string path, string tableName = "data", bool append = false)
        {
            Logger.Info($"Importing from SQLite {path}, table {tableName}");
            using var conn = new SqliteConnection($"Data Source={path}");
            conn.Open();
            using var cmd = new SqliteCommand($"SELECT * FROM {tableName}", conn);
            using var reader = cmd.ExecuteReader();
            var df = LoadDataFrameFromReader(reader);
            MergeOrAppend(df, append);
            Logger.Info("SQLite import completed");
        }

        private DataFrame LoadDataFrameFromReader(SqliteDataReader reader)
        {
            var df = new DataFrame();
            var columns = new List<DataFrameColumn>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                string name = reader.GetName(i);
                Type type = reader.GetFieldType(i);
                if (type == typeof(int))
                    columns.Add(new PrimitiveDataFrameColumn<int>(name));
                else if (type == typeof(long))
                    columns.Add(new PrimitiveDataFrameColumn<long>(name));
                else if (type == typeof(float))
                    columns.Add(new PrimitiveDataFrameColumn<float>(name));
                else if (type == typeof(double))
                    columns.Add(new PrimitiveDataFrameColumn<double>(name));
                else if (type == typeof(bool))
                    columns.Add(new PrimitiveDataFrameColumn<bool>(name));
                else if (type == typeof(DateTime))
                    columns.Add(new PrimitiveDataFrameColumn<DateTime>(name));
                else
                    columns.Add(new StringDataFrameColumn(name));
            }
            foreach (var col in columns)
            {
                df.Columns.Add(col);
            }

            while (reader.Read())
            {
                var values = new object?[reader.FieldCount];
                reader.GetValues(values);
                df.Append(values, inPlace: true);
            }
            return df;
        }

        public void ImportFromArchive(string path, bool append = false)
        {
            Logger.Info($"Importing from archive {path}");
            using var fs = File.OpenRead(path);
            using var reader = ReaderFactory.Open(fs);
            bool localAppend = append;
            while (reader.MoveToNextEntry())
            {
                if (!reader.Entry.IsDirectory && reader.Entry.Key.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    using var entryStream = reader.OpenEntryStream();
                    var df = DataFrame.LoadCsv(entryStream);
                    MergeOrAppend(df, localAppend);
                    localAppend = true;
                    Logger.Debug($"Imported CSV from archive entry {reader.Entry.Key}");
                }
            }
            Logger.Info("Archive import completed");
        }

        public void ImportFromDataBall(string path, bool append = false)
        {
            Logger.Info($"Importing from DataBall {path}");
            _currentFilePath = path;
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var metadataEntry = zip.Entries.FirstOrDefault(e => e.FullName == "metadata.json");

            if (metadataEntry != null)
            {
                using var stream = metadataEntry.Open();
                using var sr = new StreamReader(stream);
                var json = sr.ReadToEnd();
                var loadedMetadata = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
                if (loadedMetadata != null)
                {
                    foreach (var kvp in loadedMetadata)
                    {
                        Metadata[kvp.Key] = kvp.Value;
                        Logger.Debug($"Loaded metadata {kvp.Key}");
                    }
                }
            }

            var parquetEntries = zip.Entries.Where(e => e.FullName.EndsWith(".parquet"));
            bool localAppend = append;
            foreach (var entry in parquetEntries)
            {
                using var stream = entry.Open();
                var df = ReadParquet(stream);
                MergeOrAppend(df, localAppend);
                localAppend = true;
                Logger.Debug($"Imported Parquet from {entry.FullName}");
            }
            Logger.Info("DataBall import completed");
        }

        private DataFrame ReadParquet(Stream stream)
        {
            using var reader = new ParquetReader(stream);
            var table = new Table(reader.Schema);
            for (int rg = 0; rg < reader.RowGroupCount; rg++)
            {
                using var rgReader = reader.OpenRowGroupReader(rg);
                for (int c = 0; c < reader.Schema.DataFields.Length; c++)
                {
                    var field = reader.Schema.DataFields[c];
                    var col = rgReader.ReadColumn(field);
                    table.AddColumn(col);
                }
            }
            return FromParquetTable(table);
        }

        private DataFrame FromParquetTable(Table table)
        {
            var df = new DataFrame();
            for (int i = 0; i < table.ColumnCount; i++)
            {
                var col = table[i];
                Type type = col.Field.ClrType ?? typeof(string);
                if (type == typeof(string))
                {
                    df.AddColumn(col.Field.Name, col.StringData());
                } else if (type == typeof(int))
                {
                    df.AddColumn(col.Field.Name, col.IntData());
                } // add for other types
            }
            return df;
        }

        private void MergeOrAppend(DataFrame df, bool append)
        {
            Logger.Debug("Merging or appending DataFrame");
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

                if (Data.Columns.Any(c => c.Name == key))
                {
                    continue;
                }

                if (df.Columns.Any(c => c.Name == key))
                {
                    var col = df.Columns.First(c => c.Name == key);
                    bool allMatch = true;
                    for (long j = 0; j < col.Length; j++)
                    {
                        if (!Equals(col[j], val))
                        {
                            allMatch = false;
                            break;
                        }
                    }
                    if (allMatch)
                    {
                        df.Columns.Remove(key);
                        Logger.Debug($"Removed constant column {key} from appended DF");
                    }
                    else
                    {
                        AddConstantColumn(key, val, Data.Rows.Count);
                        keepInMetadata = false;
                        Logger.Debug($"Promoted metadata {key} to column");
                    }
                }
                else
                {
                    AddConstantColumn(key, val, df.Rows.Count);
                    Logger.Debug($"Added metadata {key} as column to appended DF");
                }

                if (!keepInMetadata)
                {
                    Metadata.Remove(key);
                }
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
                {
                    newCol = new StringDataFrameColumn(colName, newLength);
                }
                else
                {
                    newCol = CreatePrimitiveColumn(colName, type, newLength);
                }

                if (Data.Columns.Any(c => c.Name == colName))
                {
                    var dataCol = Data.Columns.First(c => c.Name == colName);
                    for (long i = 0; i < Data.Rows.Count; i++)
                    {
                        SetColumnValue(newCol, i, dataCol[i]);
                    }
                }

                if (df.Columns.Any(c => c.Name == colName))
                {
                    var dfCol = df.Columns.First(c => c.Name == colName);
                    for (long i = 0; i < df.Rows.Count; i++)
                    {
                        SetColumnValue(newCol, Data.Rows.Count + i, dfCol[i]);
                    }
                }

                newData.Columns.Add(newCol);
            }

            Data = newData;
            Logger.Debug("Merge/append completed");
        }

        private void AddConstantColumn(string name, object? value, long length)
        {
            Type type = value?.GetType() ?? typeof(string);
            DataFrameColumn col;
            if (type == typeof(string))
            {
                col = new StringDataFrameColumn(name, Enumerable.Repeat((string?)value, (int)length));
            }
            else if (type == typeof(int))
            {
                col = new PrimitiveDataFrameColumn<int>(name, Enumerable.Repeat((int)(value ?? 0), (int)length));
            }
            else
            {
                col = new StringDataFrameColumn(name, Enumerable.Repeat(value?.ToString(), (int)length));
            }
            Data.Columns.Add(col);
            Logger.Debug($"Added constant column {name}");
        }

        private void SetColumnValue(DataFrameColumn col, long index, object? value)
        {
            if (value != null && value.GetType() != col.DataType)
            {
                try
                {
                    value = Convert.ChangeType(value, col.DataType);
                    Logger.Debug($"Converted value for index {index}");
                }
                catch
                {
                    value = null;
                    Logger.Warn($"Failed to convert value for index {index}, set to null");
                }
            }
            col[index] = value;
        }

        public void ExportToCsv(string path)
        {
            Logger.Info($"Exporting to CSV {path}");
            DataFrame.SaveCsv(Data, path);
            Logger.Info("CSV export completed");
        }

        public void ExportToParquet(string path, string[]? partitionColumns = null)
        {
            Logger.Info($"Exporting to Parquet {path}");
            if (partitionColumns is { Length: > 0 })
            {
                ExportToPartitionedParquet(Path.GetDirectoryName(path) ?? throw new ArgumentException("Invalid path"), partitionColumns);
            }
            else
            {
                using var fs = File.OpenWrite(path);
                using var writer = new ParquetWriter(fs);
                writer.Write(Data.ToTable());
            }
            Logger.Info("Parquet export completed");
        }

        public void ExportToSqlite(string path, string tableName = "data")
        {
            Logger.Info($"Exporting to SQLite {path}, table {tableName}");
            using var conn = new SqliteConnection($"Data Source={path}");
            conn.Open();

            var createSql = $"CREATE TABLE IF NOT EXISTS {tableName} (";
            var columns = Data.Columns.Select(c => $"{c.Name} {GetSqliteType(c.DataType)}");
            createSql += string.Join(", ", columns) + ")";
            using var createCmd = new SqliteCommand(createSql, conn);
            createCmd.ExecuteNonQuery();

            for (long i = 0; i < Data.Rows.Count; i++)
            {
                var insertSql = $"INSERT INTO {tableName} VALUES (";
                insertSql += string.Join(", ", Enumerable.Range(0, Data.Columns.Count).Select(_ => "?")) + ")";
                using var insertCmd = new SqliteCommand(insertSql, conn);
                for (int j = 0; j < Data.Columns.Count; j++)
                {
                    insertCmd.Parameters.AddWithValue(null, Data.Columns[j][i]);
                }
                insertCmd.ExecuteNonQuery();
            }
            Logger.Info("SQLite export completed");
        }

        private string GetSqliteType(Type type)
        {
            if (type == typeof(int) || type == typeof(long) || type == typeof(bool))
                return "INTEGER";
            if (type == typeof(float) || type == typeof(double))
                return "REAL";
            if (type == typeof(DateTime))
                return "DATETIME";
            return "TEXT";
        }

        public void ExportToArchive(string path)
        {
            Logger.Info($"Exporting to archive {path}");
            using var fs = File.OpenWrite(path);
            using var writer = WriterFactory.Open(fs, ArchiveType.Zip, new WriterOptions(CompressionType.Deflate));
            var csvStream = new MemoryStream();
            DataFrame.SaveCsv(Data, csvStream);
            csvStream.Position = 0;
            writer.Write("data.csv", csvStream);
            Logger.Info("Archive export completed");
        }

        public void ExportToDataBall(string path)
        {
            Logger.Info($"Exporting to DataBall {path}");
            using var fs = File.OpenWrite(path);
            using var zip = new System.IO.Compression.ZipArchive(fs, ZipArchiveMode.Create);
            var parquetEntry = zip.CreateEntry("data.parquet");
            using (var stream = parquetEntry.Open())
            {
                using var writer = new ParquetWriter(stream);
                writer.Write(Data.ToTable());
            }

            var metadataEntry = zip.CreateEntry("metadata.json");
            using (var stream = metadataEntry.Open())
            using (var sw = new StreamWriter(stream))
            {
                var json = JsonSerializer.Serialize(Metadata);
                sw.Write(json);
            }
            Logger.Info("DataBall export completed");
        }

        private class Config
        {
            public Dictionary<string, object?>? metadata { get; set; }
            public Dictionary<string, string>? columns { get; set; }
            public List<Relationship>? relationships { get; set; }
        }

        private class Relationship
        {
            public string trigger { get; set; } = null!;
            public List<string> reset { get; set; } = null!;
        }
    }
}