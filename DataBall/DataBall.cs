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

namespace squalor.DataBall
{
    public class DataBall
    {
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
            if (!string.IsNullOrEmpty(configPath))
            {
                LoadConfig(configPath);
            }
        }

        private void LoadConfig(string path)
        {
            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var config = JsonSerializer.Deserialize<Config>(json, options);

            foreach (var kvp in config.metadata ?? new Dictionary<string, object>())
            {
                Metadata[kvp.Key] = kvp.Value;
            }

            Relationships = (config.relationships ?? new List<Relationship>()).Select(r => new Relationship { trigger = r.trigger, reset = r.reset ?? new List<string>() }).ToList();

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
            }

            // If no data, add empty columns
            if (Data.Columns.Count == 0)
            {
                foreach (var exp in ExpectedColumnTypes)
                {
                    AddEmptyColumn(exp.Key, exp.Value);
                }
            }
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
                }
            }
        }

        public void ModifyField(string field, object value)
        {
            if (_pendingRow == null)
            {
                throw new InvalidOperationException("No pending row initialized.");
            }
            _pendingRow[field] = value;
            _modifiedFields.Add(field);
        }

        public void Roll()
        {
            if (_pendingRow == null)
            {
                throw new InvalidOperationException("No pending row initialized.");
            }

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

            // Clear pending row
            _pendingRow = null;
            _originalRow = null;
            _modifiedFields = null;
        }

        // Bounce method for compaction
        public void Bounce(string partitionedParquetPath = null, string[] partitionColumns = null)
        {
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
                }
            }
            foreach (var name in columnsToRemove)
            {
                RemoveColumn(name);
            }

            // Other optimizations: e.g., deduplicate rows (simple unique)
            Data = Data.DropDuplicates();

            // If partitioned path provided, export to partitioned Parquet
            if (!string.IsNullOrEmpty(partitionedParquetPath))
            {
                ExportToPartitionedParquet(partitionedParquetPath, partitionColumns ?? Array.Empty<string>());
            }
        }

        // Save method
        public void Save(string filePath = null, string[] partitionColumns = null)
        {
            Bounce();

            string savePath = filePath ?? _currentFilePath;
            if (string.IsNullOrEmpty(savePath))
            {
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
                }
            }
            else
            {
                var parquetEntry = zip.CreateEntry("data.parquet");
                using var stream = parquetEntry.Open();
                stream.WriteParquetAsync(Data).GetAwaiter().GetResult();
            }

            _currentFilePath = savePath;
        }

        // Partitioned Parquet export (to directory)
        public void ExportToPartitionedParquet(string basePath, params string[] partitionColumns)
        {
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
            }
        }

        // Existing methods to manipulate data
        public void AddColumn<T>(string name, IEnumerable<T> values)
        {
            var column = new PrimitiveDataFrameColumn<T>(name, values);
            Data.Append(column, inPlace: true);
        }

        public void RemoveColumn(string name)
        {
            Data.Remove(name);
        }

        public void AddRow(IEnumerable<object> values)
        {
            Data.Append(values, inPlace: true);
        }

        public void RemoveRow(long index)
        {
            Data.RemoveAt(index);
        }

        // Modify cell
        public void SetValue(long rowIndex, string columnName, object value)
        {
            var column = Data[columnName];
            column[rowIndex] = value;
        }

        // Import methods
        public void ImportFromCsv(string path, bool append = false, int? chunkSize = null)
        {
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
                while ((line = reader.ReadLine()) != null)
                {
                    chunk.Add(line.Split(','));
                    if (chunk.Count == chunkSize.Value)
                    {
                        AppendChunk(chunk, columns);
                        chunk.Clear();
                    }
                }
                if (chunk.Count > 0)
                {
                    AppendChunk(chunk, columns);
                }
            }
            else
            {
                var df = DataFrame.LoadCsv(path);
                MergeOrAppend(df, append);
            }
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
                }
            }
            else
            {
                using var fs = File.OpenRead(path);
                var df = fs.ReadParquetAsDataFrameAsync().GetAwaiter().GetResult();
                MergeOrAppend(df, append);
            }
        }

        public void ImportFromSqlite(string path, string tableName = "data", bool append = false)
        {
            using var conn = new SqliteConnection($"Data Source={path}");
            conn.Open();
            using var cmd = new SqliteCommand($"SELECT * FROM {tableName}", conn);
            using var reader = cmd.ExecuteReader();
            var df = LoadDataFrameFromReader(reader);
            MergeOrAppend(df, append);
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
                    localAppend = true; // append subsequent files
                }
            }
        }

        public void ImportFromDataBall(string path, bool append = false)
        {
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
            }
        }

        private void MergeOrAppend(DataFrame df, bool append)
        {
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
                    }
                    else
                    {
                        // Move to column: Add column to existing Data with val
                        AddConstantColumn(key, val, Data.Rows.Count);
                        keepInMetadata = false;
                    }
                }
                else
                {
                    // Add column to df with val
                    AddConstantColumn(key, val, df.Rows.Count);
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
        }

        private void SetColumnValue(IDataFrameColumn col, long index, object value)
        {
            if (value != null && value.GetType() != col.DataType)
            {
                try
                {
                    value = Convert.ChangeType(value, col.DataType);
                }
                catch
                {
                    value = null; // or throw
                }
            }
            col[index] = value;
        }

        // Export methods
        public void ExportToCsv(string path)
        {
            DataFrame.SaveCsv(Data, path);
        }

        public void ExportToParquet(string path, string[] partitionColumns = null)
        {
            if (partitionColumns != null && partitionColumns.Length > 0)
            {
                ExportToPartitionedParquet(Path.GetDirectoryName(path), partitionColumns);
            }
            else
            {
                using var fs = File.OpenWrite(path);
                fs.WriteParquetAsync(Data).GetAwaiter().GetResult();
            }
        }

        public void ExportToSqlite(string path, string tableName = "data")
        {
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
            // For simplicity, export as ZIP with CSV inside; can extend for tar.gz etc.
            using var fs = File.OpenWrite(path);
            using var archive = SharpCompress.Archives.Zip.ZipArchive.Create();
            var csvStream = new MemoryStream();
            DataFrame.SaveCsv(Data, csvStream);
            csvStream.Position = 0;
            archive.AddEntry("data.csv", csvStream, closeStream: false);
            archive.SaveTo(fs, new SharpCompress.Writers.WriterOptions(compressionType));
        }

        public void ExportToDataBall(string path)
        {
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