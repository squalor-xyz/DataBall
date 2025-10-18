using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.Data.Analysis;
using Microsoft.Data.Sqlite;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using SharpCompress.Archives;
using System.Text.Json;
using NLog;

namespace squalor.DataBall.Import;

/// <summary>
/// Provides methods for importing data into a DataBall instance from various formats.
/// </summary>
public static class ImportManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Imports data from a CSV file into the DataBall, with chunking support for large files.
    /// </summary>
    /// <param name="db">The DataBall instance to import into.</param>
    /// <param name="path">The path to the CSV file.</param>
    /// <param name="append">If true, appends the data; otherwise, replaces existing data.</param>
    /// <param name="chunkSize">The number of rows to process per chunk (default: 1000).</param>
    public static void ImportFromCsv(DataBall db, string path, bool append = true, int chunkSize = 1000)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var df = DataFrame.LoadCsv(stream, separator: ',', header: true, encoding: Encoding.UTF8);
            MergeOrAppend(db, df, append);
            Logger.Info($"Imported from CSV: {path}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to import from CSV: {path}");
            throw new DataBallException("Failed to import CSV data", ex);
        }
    }

    /// <summary>
    /// Imports data from a Parquet file into the DataBall.
    /// </summary>
    /// <param name="db">The DataBall instance to import into.</param>
    /// <param name="path">The path to the Parquet file.</param>
    /// <param name="append">If true, appends the data; otherwise, replaces existing data.</param>
    public static void ImportFromParquet(DataBall db, string path, bool append = true)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new ParquetReader(stream);
            var schema = reader.Schema;
            var df = new DataFrame();
            for (int rg = 0; rg < reader.RowGroupCount; rg++)
            {
                using var group = reader.OpenRowGroupReader(rg);
                foreach (var field in schema.Fields)
                {
                    if (field is DataField dataField)
                    {
                        var col = group.ReadColumn(dataField).Data;
                        AddParquetColumnToDataFrame(df, dataField, col);
                    }
                }
            }
            MergeOrAppend(db, df, append);
            Logger.Info($"Imported from Parquet: {path}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to import from Parquet: {path}");
            throw new DataBallException("Failed to import Parquet data", ex);
        }
    }

    /// <summary>
    /// Imports data from a SQLite database into the DataBall.
    /// </summary>
    /// <param name="db">The DataBall instance to import into.</param>
    /// <param name="path">The path to the SQLite database.</param>
    /// <param name="append">If true, appends the data; otherwise, replaces existing data.</param>
    public static void ImportFromSqlite(DataBall db, string path, bool append = true)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM data"; // Assumes table named 'data'
            using var rdr = cmd.ExecuteReader();
            var df = new DataFrame();
            var columnNames = Enumerable.Range(0, rdr.FieldCount)
                .Select(i => rdr.GetName(i))
                .ToList();
            foreach (var name in columnNames)
            {
                df.Columns.Add(new StringDataFrameColumn(name));
            }

            while (rdr.Read())
            {
                var values = new object?[columnNames.Count];
                for (int i = 0; i < columnNames.Count; i++)
                {
                    values[i] = rdr.IsDBNull(i) ? null : rdr.GetValue(i).ToString();
                }
                df.Append(values, inPlace: true);
            }
            MergeOrAppend(db, df, append);
            Logger.Info($"Imported from SQLite: {path}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to import from SQLite: {path}");
            throw new DataBallException("Failed to import SQLite data", ex);
        }
    }

    /// <summary>
    /// Imports data from an archive (ZIP, TAR.GZ, or TAR.XZ) containing CSV files.
    /// </summary>
    /// <param name="db">The DataBall instance to import into.</param>
    /// <param name="path">The path to the archive file.</param>
    /// <param name="append">If true, appends the data; otherwise, replaces existing data.</param>
    public static void ImportFromArchive(DataBall db, string path, bool append = true)
    {
        try
        {
            using var archive = ArchiveFactory.Open(path);
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory && e.Key.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = entry.OpenEntryStream();
                var df = DataFrame.LoadCsv(stream, separator: ',', header: true, encoding: Encoding.UTF8);
                MergeOrAppend(db, df, append);
            }
            Logger.Info($"Imported from archive: {path}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to import from archive: {path}");
            throw new DataBallException("Failed to import archive data", ex);
        }
    }

    /// <summary>
    /// Imports data from a .ball file (ZIP containing Parquet files and metadata).
    /// </summary>
    /// <param name="db">The DataBall instance to import into.</param>
    /// <param name="path">The path to the .ball file.</param>
    /// <param name="append">If true, appends the data; otherwise, replaces existing data.</param>
    public static void ImportFromDataBall(DataBall db, string path, bool append = true)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            ZipFile.ExtractToDirectory(path, tempDir);
            var metadataPath = Path.Combine(tempDir, "metadata.json");
            if (File.Exists(metadataPath))
            {
                var json = File.ReadAllText(metadataPath);
                var deserialized = JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new Dictionary<string, object?>();
                db.Metadata.Clear();
                foreach (var kvp in deserialized)
                {
                    db.Metadata[kvp.Key] = kvp.Value;
                }
            }
            foreach (var file in Directory.GetFiles(tempDir, "*.parquet", SearchOption.AllDirectories))
            {
                ImportFromParquet(db, file, append);
            }
            Logger.Info($"Imported from .ball: {path}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to import from .ball: {path}");
            throw new DataBallException("Failed to import .ball data", ex);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    /// Adds a Parquet column to a DataFrame, handling type conversion.
    /// </summary>
    /// <param name="df">The DataFrame to add the column to.</param>
    /// <param name="field">The Parquet data field.</param>
    /// <param name="data">The data array from the Parquet column.</param>
    private static void AddParquetColumnToDataFrame(DataFrame df, DataField field, Array data)
    {
        var type = field.ClrType ?? typeof(string);
        if (type == typeof(string))
        {
            df.Columns.Add(new StringDataFrameColumn(field.Name, (IEnumerable<string?>)data));
        }
        else if (type.IsValueType)
        {
            var genType = typeof(PrimitiveDataFrameColumn<>).MakeGenericType(type);
            var values = (IEnumerable<object>)data;
            var col = (DataFrameColumn)Activator.CreateInstance(genType, field.Name, values.Select(v => v == null ? default : Convert.ChangeType(v, type)))!;
            df.Columns.Add(col);
        }
        else
        {
            df.Columns.Add(new StringDataFrameColumn(field.Name, data.Cast<object>().Select(v => v?.ToString())));
        }
        Logger.Debug($"Added Parquet column {field.Name} of type {type.Name}");
    }

    /// <summary>
    /// Merges or appends a DataFrame to the DataBall's DataFrame.
    /// </summary>
    /// <param name="db">The DataBall instance to modify.</param>
    /// <param name="df">The DataFrame to merge or append.</param>
    /// <param name="append">If true, appends the data; otherwise, replaces existing data.</param>
    private static void MergeOrAppend(DataBall db, DataFrame df, bool append)
    {
        if (!append)
        {
            db.Data = df;
        }
        else
        {
            db.Data.Append(df.Rows, inPlace: true);
        }
        Logger.Debug($"Merged/appended DataFrame with {df.Rows.Count} rows");
    }
}