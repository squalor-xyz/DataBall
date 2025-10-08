// DataBall.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Analysis;
using Microsoft.Data.Sqlite;
using Parquet;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Readers;
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
        public Dictionary<string, object> Metadata { get; } = new Dictionary<string, object>();

        private Dictionary<string, object> _pendingRow = null;
        private Dictionary<string, object> _originalRow = null;
        private HashSet<string> _modifiedFields = null;

        private List<Relationship> Relationships { get; set; } = new List<Relationship>();
        private Dictionary<string, Type> ExpectedColumnTypes { get; set; } = new Dictionary<string, Type>();

        private string _currentFilePath = null;

        public DataBall(string configPath = null)
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
            var config = JsonSerializer.Deserialize<Config>(json, options);

            foreach (var kvp in config.metadata ?? new Dictionary<string, object>())
            {
                Metadata[kvp.Key] = kvp.Value;
                Logger.Debug($"Added metadata: {kvp.Key}");
            }

            Relationships = (config.relationships ?? new List<Relationship>()).Select(r => new Relationship { trigger = r.trigger, reset = r.reset ?? new List<string>() }).ToList();
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

            // If no data, add empty columns
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
            IDataFrameColumn col;
            long length = Data.Rows.Count;
            if (type == typeof(string))
            {
                col = new StringDataFrameColumn(name, length);
            }
            else
            {
                col = CreatePrimitiveColumn(name, type, length);
            }
            Data.Append(col, inPlace: true);
            Logger.Debug($"Added empty column {name} of type {type}");
        }

        private IDataFrameColumn CreatePrimitiveColumn(string name, Type type, long length)
        {
            var colType = typeof(PrimitiveDataFrameColumn<>).MakeGenericType(type);
            var col = (IDataFrameColumn)Activator.CreateInstance(colType, name, length);
            var indexer = colType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance, null, type, new[] { typeof(long) }, null);
            for (long i = 0; i < length; i++)
            {
                indexer.SetValue(col, null, new object[] { i });
            }
            return col;
        }

        // Row building methods
        public void InitializeRow(Dictionary<string, object> initialValues = null)
        {
            Logger.Debug("Initializing new row");
            _originalRow = new Dictionary<string, object>();
            _pendingRow = new Dictionary<string, object>();
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

        public void ModifyField(string field, object value)
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
            if (_pendingRow == null)
            {
                Logger.Error("Attempted to roll without initialized row");
                throw new InvalidOperationException("No pending row initialized.");
            }

            Logger.Debug("Applying relationships");
            // Apply relationships
            foreach (var rel in Relationships)
            {
                object pendingTrigger = null;
                object originalTrigger = null;
                bool hasPending = _pendingRow.TryGetValue(rel.trigger, out pendingTrigger);
                bool hasOriginal = _originalRow.TryGetValue(rel.trigger, out originalTrigger);

                if (hasPending != hasOriginal || !Object.Equals(pendingTrigger, originalTrigger))
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

            // Add new columns for any new fields in the pending row
            foreach (var key in _pendingRow.Keys.ToList()) // ToList to avoid modification during enumeration
            {
                if (!Data.Columns.Any(c => c.Name == key))
                {
                    Type valueType = _pendingRow[key]?.GetType();
                    Type columnType = ExpectedColumnTypes.TryGetValue(key, out var exp) ? exp : (valueType ?? typeof(string));

                    IDataFrameColumn newColumn;
                    long currentLength = Data.Rows.Count;

                    if (columnType == typeof(string))
                    {
                        newColumn = new StringDataFrameColumn(key, currentLength);
                    }
                    else
                    {
                        newColumn = CreatePrimitiveColumn(key, columnType, currentLength);
                    }
                    Data.Append(newColumn, inPlace: true);
                    Logger.Debug($"Added new column {key}");
                }
            }

            // Prepare values in the order of current columns
            var values = new object[Data.Columns.Count];
            for (int i = 0; i < Data.Columns.Count; i++)
            {
                string columnName = Data.Columns[i].Name;
                values[i] = _pendingRow.ContainsKey(columnName) ? _pendingRow[columnName] : null;
            }

            // Append the row
            Data.Append(values, inPlace: true);
            Logger.Info("Row committed successfully");

            // Clear pending row
            _pendingRow = null;
            _originalRow = null;
            _modifiedFields = null;
        }

        // Bounce method for compaction
        public void Bounce(string partitionedParquetPath = null, string[] partitionColumns = null)
        {
            Logger.Info("Starting bounce operation");
            // Identify and remove constant columns to metadata
            var columnsToRemove = new List<string>();
            for (int i = 0; i < Data.Columns.Count; i++)
            {
                var col = Data.Columns[i];
                object first = null;
                bool isConstant = true;
                bool firstSet = false;
                for (long j = 0; j < col.Length; j++)
                {
                    object val = col[j];
                    if (val == null) continue;
                    if (!firstSet)
                    {
                        first = val;
                        firstSet = true;
                    }
                    else if (!val.Equals(first))
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

            // Other optimizations: e.g., deduplicate rows (simple unique)
            Data = Data.DropDuplicates();
            Logger.Debug("Dropped duplicate rows");

            // If partitioned path provided, export to partitioned Parquet
            if (!string.IsNullOrEmpty(partitionedParquetPath))
            {
                ExportToPartitionedParquet(partitionedParquetPath, partitionColumns ?? Array.Empty<string>());
                Logger.Info($"Exported to partitioned Parquet at {partitionedParquetPath}");
            }
            Logger.Info("Bounce operation completed");
        }

        // Save method
        public void Save(string filePath = null, string[] partitionColumns = null)
        {
            Logger.Info("Starting save operation");
            Bounce();

            string savePath = filePath ?? _currentFilePath;
            if (string.IsNullOrEmpty(savePath))
            {
                Logger.Error("No file path provided for save");
                throw new InvalidOperationException("No file path provided for save and no current file path set.");
            }

            if (!savePath.EndsWith(".ball"))
            {
                savePath += ".ball";
            }

            using var fs = File.OpenWrite(savePath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            // Metadata
            var metadataEntry = zip.CreateEntry("metadata.json");
            using (var stream = metadataEntry.Open())
            using (var sw = new StreamWriter(stream))
            {
                var json = JsonSerializer.Serialize(Metadata);
                sw.Write(json);
                Logger.Debug("Saved metadata.json");
            }

            // Data: If partitioned, create entries with paths
            if (partitionColumns != null && partitionColumns.Length > 0)
            {
                var groupBy = Data.GroupBy(partitionColumns);
                foreach (var group in groupBy.Groupings)
                {
                    // Build entry name
                    var entryName = "";
                    for (int i = 0; i < partitionColumns.Length; i++)
                    {
                        object val = group.KeyValues[i];
                        string valStr = val?.ToString() ?? "null";
                        entryName += $"{partitionColumns[i]}={valStr}/";
                    }
                    entryName += "part-0.parquet";

                    var entry = zip.CreateEntry(entryName);
                    using var stream = entry.Open();
                    stream.WriteParquetAsync(group.Group).GetAwaiter().GetResult();
                    Logger.Debug($"Saved partitioned Parquet entry {entryName}");
                }
            }
            else
            {
                var parquetEntry = zip.CreateEntry("data.parquet");
                using var stream = parquetEntry.Open();
                stream.WriteParquetAsync(Data).GetAwaiter().GetResult();
                Logger.Debug("Saved data.parquet");
            }

            _currentFilePath = savePath;
            Logger.Info($"Saved to {savePath}");
        }

        // Partitioned Parquet export (to directory)
        public void ExportToPartitionedParquet(string basePath, params string[] partitionColumns)
        {
            Logger.Info($"Exporting to partitioned Parquet at {basePath}");
            if (partitionColumns.Length == 0)
            {
                // No partitioning, write single file
                ExportToParquet(Path.Combine(basePath, "data.parquet"));
                return;
            }

            // Group by partition columns
            var groupBy = Data.GroupBy(partitionColumns);

            foreach (var group in groupBy.Groupings)
            {
                // Build partition path
                var partitionPath = basePath;
                for (int i = 0; i < partitionColumns.Length; i++)
                {
                    object val = group.KeyValues[i];
                    string valStr = val?.ToString() ?? "null";
                    partitionPath = Path.Combine(partitionPath, $"{partitionColumns[i]}={valStr}");
                }
                Directory.CreateDirectory(partitionPath);

                // Write group DataFrame to file
                var filePath = Path.Combine(partitionPath, "part-0.parquet");
                using var fs = File.OpenWrite(filePath);
                fs.WriteParquetAsync(group.Group).GetAwaiter().GetResult();
                Logger.Debug($"Exported partition to {filePath}");
            }
        }

        // Existing methods to manipulate data
        public void AddColumn<T>(string name, IEnumerable<T> values)
        {
            var column = new PrimitiveDataFrameColumn<T>(name, values);
            Data.Append(column, inPlace: true);
            Logger.Debug($"Added column {name}");
        }

        public void RemoveColumn(string name)
        {
            Data.Remove(name);
            Logger.Debug($"Removed column {name}");
        }

        public void AddRow(IEnumerable<object> values)
        {
            Data.Append(values, inPlace: true);
            Logger.Debug("Added row");
        }

        public void RemoveRow(long index)
        {
            Data.RemoveAt(index);
            Logger.Debug($"Removed row at index {index}");
        }

        // Modify cell
        public void SetValue(long rowIndex, string columnName, object value)
        {
            var column = Data[columnName];
            column[rowIndex] = value;
            Logger.Debug($"Set value at row {rowIndex}, column {columnName}");
        }

        // Import methods
        public void ImportFromCsv(string path, bool append = false, int? chunkSize = null)
        {
            Logger.Info($"Importing from CSV {path}");
            if (chunkSize.HasValue)
            {
                // For large files, load in chunks
                using var reader = new StreamReader(path);
                var header = reader.ReadLine();
                var columns = header.Split(',');

                // Create columns
                if (!append || Data.Rows.Count == 0)
                {
                    foreach (var col in columns)
                    {
                        AddEmptyColumn(col, typeof(string)); // Assume string for simplicity
                    }
                }

                var chunk = new List<string[]>();
                string line;
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
                df.Append(new StringDataFrameColumn(columns[i], values), inPlace: true);
            }
            MergeOrAppend(df, true);
        }

        public void ImportFromParquet(string path, bool append = false)
        {
            Logger.Info($"Importing from Parquet {path}");
            if (Directory.Exists(path))
            {
                // Assume partitioned directory
                var parquetFiles = Directory.GetFiles(path, "*.parquet", SearchOption.AllDirectories);
                bool localAppend = append;
                foreach (var file in parquetFiles)
                {
                    using var fs = File.OpenRead(file);
                    var df = fs.ReadParquetAsDataFrameAsync().GetAwaiter().GetResult();
                    MergeOrAppend(df, localAppend);
                    localAppend = true;
                    Logger.Debug($"Imported from {file}");
                }
            }
            else
            {
                using var fs = File.OpenRead(path);
                var df = fs.ReadParquetAsDataFrameAsync().GetAwaiter().GetResult();
                MergeOrAppend(df, append);
            }
            Logger.Info("Parquet import completed");
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
            var columns = new List<IDataFrameColumn>();
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
            df.Append(columns, inPlace: true);

            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
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
            using var zip = ZipFile.OpenRead(path);
            var metadataEntry = zip.Entries.FirstOrDefault(e => e.FullName == "metadata.json");

            if (metadataEntry != null)
            {
                using var stream = metadataEntry.Open();
                using var sr = new StreamReader(stream);
                var json = sr.ReadToEnd();
                var loadedMetadata = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                foreach (var kvp in loadedMetadata)
                {
                    Metadata[kvp.Key] = kvp.Value;
                    Logger.Debug($"Loaded metadata {kvp.Key}");
                }
            }

            // Find all .parquet entries (for partitioned or single)
            var parquetEntries = zip.Entries.Where(e => e.FullName.EndsWith(".parquet"));
            bool localAppend = append;
            foreach (var entry in parquetEntries)
            {
                using var stream = entry.Open();
                var df = stream.ReadParquetAsDataFrameAsync().GetAwaiter().GetResult();
                MergeOrAppend(df, localAppend);
                localAppend = true;
                Logger.Debug($"Imported Parquet from {entry.FullName}");
            }
            Logger.Info("DataBall import completed");
        }

        private void MergeOrAppend(DataFrame df, bool append)
        {
            Logger.Debug("Merging or appending DataFrame");
            if (!append || Data.Rows.Count == 0)
            {
                Data = df.Clone();
                return;
            }

            // Before append, check metadata consistency for existing metadata keys
            foreach (var kvp in Metadata.ToList())
            {
                string key = kvp.Key;
                object val = kvp.Value;
                bool keepInMetadata = true;

                if (Data.Columns.Contains(key))
                {
                    // Should not happen, but skip
                    continue;
                }

                if (df.Columns.Contains(key))
                {
                    // Check if all in df match val
                    var col = df[key];
                    bool allMatch = true;
                    for (long j = 0; j < col.Length; j++)
                    {
                        if (!Object.Equals(col[j], val))
                        {
                            allMatch = false;
                            break;
                        }
                    }
                    if (allMatch)
                    {
                        // Remove from df
                        df.Remove(key);
                        Logger.Debug($"Removed constant column {key} from appended DF");
                    }
                    else
                    {
                        // Move to column: Add column to existing Data with val
                        AddConstantColumn(key, val, Data.Rows.Count);
                        keepInMetadata = false;
                        Logger.Debug($"Promoted metadata {key} to column");
                    }
                }
                else
                {
                    // Add column to df with val
                    AddConstantColumn(key, val, df.Rows.Count);
                    Logger.Debug($"Added metadata {key} as column to appended DF");
                }

                if (!keepInMetadata)
                {
                    Metadata.Remove(key);
                }
            }

            // Align columns and append with nulls for missing
            var allColumnNames = Data.Columns.Select(c => c.Name).Union(df.Columns.Select(c => c.Name)).ToList();

            var newData = new DataFrame();

            foreach (var colName in allColumnNames)
            {
                Type type = null;
                if (ExpectedColumnTypes.TryGetValue(colName, out var exp))
                {
                    type = exp;
                }
                else
                {
                    Data.Columns.TryGetValue(colName, out var dCol);
                    df.Columns.TryGetValue(colName, out var fCol);
                    type = dCol?.DataType ?? fCol?.DataType ?? typeof(string);
                }

                IDataFrameColumn newCol;
                long newLength = Data.Rows.Count + df.Rows.Count;

                if (type == typeof(string))
                {
                    newCol = new StringDataFrameColumn(colName, newLength);
                }
                else
                {
                    newCol = CreatePrimitiveColumn(colName, type, newLength);
                }

                // Copy from Data
                if (Data.Columns.TryGetValue(colName, out var dataCol))
                {
                    for (long i = 0; i < Data.Rows.Count; i++)
                    {
                        SetColumnValue(newCol, i, dataCol[i]);
                    }
                }

                // Copy from df
                if (df.Columns.TryGetValue(colName, out var dfCol))
                {
                    for (long i = 0; i < df.Rows.Count; i++)
                    {
                        SetColumnValue(newCol, Data.Rows.Count + i, dfCol[i]);
                    }
                }

                newData.Append(newCol, inPlace: true);
            }

            Data = newData;
            Logger.Debug("Merge/append completed");
        }

        private void AddConstantColumn(string name, object value, long length)
        {
            Type type = value?.GetType() ?? typeof(string);
            IDataFrameColumn col;
            if (type == typeof(string))
            {
                col = new StringDataFrameColumn(name, Enumerable.Repeat((string)value, (int)length));
            }
            else if (type == typeof(int))
            {
                col = new PrimitiveDataFrameColumn<int>(name, Enumerable.Repeat((int)value, (int)length));
            }
            // Add other types as needed
            else
            {
                col = new StringDataFrameColumn(name, Enumerable.Repeat(value?.ToString(), (int)length));
            }
            Data.Append(col, inPlace: true);
            Logger.Debug($"Added constant column {name}");
        }

        private void SetColumnValue(IDataFrameColumn col, long index, object value)
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
                    value = null; // or throw
                    Logger.Warn($"Failed to convert value for index {index}, set to null");
                }
            }
            col[index] = value;
        }

        // Export methods
        public void ExportToCsv(string path)
        {
            Logger.Info($"Exporting to CSV {path}");
            DataFrame.SaveCsv(Data, path);
            Logger.Info("CSV export completed");
        }

        public void ExportToParquet(string path, string[] partitionColumns = null)
        {
            Logger.Info($"Exporting to Parquet {path}");
            if (partitionColumns != null && partitionColumns.Length > 0)
            {
                ExportToPartitionedParquet(Path.GetDirectoryName(path), partitionColumns);
            }
            else
            {
                using var fs = File.OpenWrite(path);
                fs.WriteParquetAsync(Data).GetAwaiter().GetResult();
            }
            Logger.Info("Parquet export completed");
        }

        public void ExportToSqlite(string path, string tableName = "data")
        {
            Logger.Info($"Exporting to SQLite {path}, table {tableName}");
            using var conn = new SqliteConnection($"Data Source={path}");
            conn.Open();

            // Create table
            var createSql = "CREATE TABLE IF NOT EXISTS " + tableName + " (";
            var columns = Data.Columns.Select(c => $"{c.Name} {GetSqliteType(c.DataType)}");
            createSql += string.Join(", ", columns) + ")";
            using var createCmd = new SqliteCommand(createSql, conn);
            createCmd.ExecuteNonQuery();

            // Insert rows
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

        public void ExportToArchive(string path, CompressionType compressionType = CompressionType.Deflate)
        {
            Logger.Info($"Exporting to archive {path}");
            // For simplicity, export as ZIP with CSV inside; can extend for tar.gz etc.
            using var fs = File.OpenWrite(path);
            using var archive = SharpCompress.Archives.Zip.ZipArchive.Create();
            var csvStream = new MemoryStream();
            DataFrame.SaveCsv(Data, csvStream);
            csvStream.Position = 0;
            archive.AddEntry("data.csv", csvStream, closeStream: false);
            archive.SaveTo(fs, new SharpCompress.Writers.WriterOptions(compressionType));
            Logger.Info("Archive export completed");
        }

        public void ExportToDataBall(string path)
        {
            Logger.Info($"Exporting to DataBall {path}");
            using var fs = File.OpenWrite(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            var parquetEntry = zip.CreateEntry("data.parquet");
            using (var stream = parquetEntry.Open())
            {
                stream.WriteParquetAsync(Data).GetAwaiter().GetResult();
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
            public Dictionary<string, object> metadata { get; set; }
            public Dictionary<string, string> columns { get; set; }
            public List<Relationship> relationships { get; set; }
        }

        private class Relationship
        {
            public string trigger { get; set; }
            public List<string> reset { get; set; }
        }
    }
}