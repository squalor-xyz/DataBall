using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.Data.Analysis;
using Parquet;
using Parquet.Data;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NLog;

namespace squalor.DataBall.Export;

/// <summary>
/// Provides methods for exporting DataBall data to various formats.
/// </summary>
public static class ExportManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Exports the DataBall data to the specified format and path.
    /// </summary>
    /// <param name="db">The DataBall instance to export.</param>
    /// <param name="type">The export format.</param>
    /// <param name="path">The output file path.</param>
    /// <param name="partitionColumns">Columns to partition the data by for Parquet or Archive exports.</param>
    /// <exception cref="ArgumentException">Thrown if the export type is unsupported.</exception>
    public static void Roll(DataBall db, ExportType type, string path, params string[] partitionColumns)
    {
        switch (type)
        {
            case ExportType.Csv:
                ExportToCsv(db, path);
                break;
            case ExportType.Parquet:
                ExportToParquet(db, path, partitionColumns);
                break;
            case ExportType.Sqlite:
                ExportToSqlite(db, path);
                break;
            case ExportType.Archive:
                ExportToArchive(db, path, partitionColumns);
                break;
            case ExportType.DataBall:
                Save(db, path, partitionColumns);
                break;
            default:
                throw new ArgumentException("Unsupported export type", nameof(type));
        }
        Logger.Info($"Exported to {path} as {type}");
    }

    /// <summary>
    /// Saves the DataBall data to the .ball format (ZIP containing Parquet files and metadata).
    /// </summary>
    /// <param name="db">The DataBall instance to save.</param>
    /// <param name="path">The output file path for the .ball file.</param>
    /// <param name="partitionColumns">Columns to partition the data by.</param>
    public static void Save(DataBall db, string path, params string[] partitionColumns)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(tempDir);
            ExportToPartitionedParquet(db, tempDir, partitionColumns);
            File.WriteAllText(Path.Combine(tempDir, "metadata.json"), JsonSerializer.Serialize(db.Metadata));
            ZipFile.CreateFromDirectory(tempDir, path);
            Logger.Info($"Saved to {path}");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    /// Exports the DataBall data to partitioned Parquet files in a Hive-style directory structure.
    /// </summary>
    /// <param name="db">The DataBall instance to export.</param>
    /// <param name="outputDir">The output directory for Parquet files.</param>
    /// <param name="partitionColumns">Columns to partition the data by.</param>
    public static void ExportToPartitionedParquet(DataBall db, string outputDir, params string[] partitionColumns)
    {
        if (partitionColumns.Length == 0)
        {
            ExportToParquet(db, Path.Combine(outputDir, "data.parquet"));
            return;
        }

        var grouped = db.Data.GroupBy(partitionColumns[0]); // Single column for Microsoft.Data.Analysis 0.21.1
        foreach (var group in grouped.Groupings)
        {
            var partPath = outputDir;
            for (int i = 0; i < partitionColumns.Length; i++)
            {
                var key = group.KeyColumn[0]?.ToString() ?? "null";
                partPath = Path.Combine(partPath, $"{partitionColumns[i]}={key}");
            }
            Directory.CreateDirectory(partPath);
            ExportToParquet(db, Path.Combine(partPath, "part-0.parquet"), group.DataFrame);
        }
        Logger.Debug($"Exported to partitioned Parquet in {outputDir}");
    }

    /// <summary>
    /// Exports the DataBall data to a CSV file.
    /// </summary>
    /// <param name="db">The DataBall instance to export.</param>
    /// <param name="path">The output file path for the CSV.</param>
    private static void ExportToCsv(DataBall db, string path)
    {
        using var stream = File.OpenWrite(path);
        DataFrame.SaveCsv(db.Data, stream, separator: ',', header: true, encoding: Encoding.UTF8);
        Logger.Debug($"Exported to CSV: {path}");
    }

    /// <summary>
    /// Exports the DataBall data to a Parquet file.
    /// </summary>
    /// <param name="db">The DataBall instance to export.</param>
    /// <param name="path">The output file path for the Parquet file.</param>
    /// <param name="df">Optional DataFrame subset; defaults to the full DataBall DataFrame.</param>
    private static void ExportToParquet(DataBall db, string path, DataFrame? df = null)
    {
        df ??= db.Data;
        var schemaFields = df.Columns.Select(c => new DataField(c.Name, c.DataType)).ToArray();
        var schema = new ParquetSchema(schemaFields);
        using var stream = File.OpenWrite(path);
        using var writer = new ParquetWriter(schema, stream);
        using var group = writer.CreateRowGroup();
        for (int i = 0; i < df.Columns.Count; i++)
        {
            var col = df.Columns[i];
            var values = Enumerable.Range(0, (int)df.Rows.Count).Select(j => col[j]).ToArray();
            var pcol = new Parquet.Data.DataColumn(schemaFields[i], values);
            group.WriteColumn(pcol);
        }
        Logger.Debug($"Exported to Parquet: {path}");
    }

    /// <summary>
    /// Exports the DataBall data to a SQLite database.
    /// </summary>
    /// <param name="db">The DataBall instance to export.</param>
    /// <param name="path">The output file path for the SQLite database.</param>
    private static void ExportToSqlite(DataBall db, string path)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={path}");
            conn.Open();
            // Create table based on DataFrame schema
            var columns = db.Data.Columns.Select(c => $"{c.Name} TEXT").ToList(); // Simplified: all as TEXT
            var createTableSql = $"CREATE TABLE IF NOT EXISTS data ({string.Join(", ", columns)})";
            using (var cmd = new SqliteCommand(createTableSql, conn))
            {
                cmd.ExecuteNonQuery();
            }
            // Insert rows
            for (long i = 0; i < db.Data.Rows.Count; i++)
            {
                var values = Enumerable.Range(0, db.Data.Columns.Count)
                    .Select(j => $"'{db.Data[i, j]?.ToString()?.Replace("'", "''") ?? "NULL"}'")
                    .ToList();
                var insertSql = $"INSERT INTO data VALUES ({string.Join(", ", values)})";
                using (var cmd = new SqliteCommand(insertSql, conn))
                {
                    cmd.ExecuteNonQuery();
                }
            }
            Logger.Debug($"Exported to SQLite: {path}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to export to SQLite: {path}");
            throw new DataBallException("Failed to export SQLite data", ex);
        }
    }

    /// <summary>
    /// Exports the DataBall data to an archive (ZIP, TAR.GZ, or TAR.XZ) containing partitioned CSVs.
    /// </summary>
    /// <param name="db">The DataBall instance to export.</param>
    /// <param name="path">The output file path for the archive.</param>
    /// <param name="partitionColumns">Columns to partition the data by.</param>
    private static void ExportToArchive(DataBall db, string path, params string[] partitionColumns)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(tempDir);
            if (partitionColumns.Length == 0)
            {
                var csvPath = Path.Combine(tempDir, "data.csv");
                ExportToCsv(db, csvPath);
            }
            else
            {
                var grouped = db.Data.GroupBy(partitionColumns[0]); // Single column for Microsoft.Data.Analysis 0.21.1
                foreach (var group in grouped.Groupings)
                {
                    var partPath = tempDir;
                    for (int i = 0; i < partitionColumns.Length; i++)
                    {
                        var key = group.KeyColumn[0]?.ToString() ?? "null";
                        partPath = Path.Combine(partPath, $"{partitionColumns[i]}={key}");
                    }
                    Directory.CreateDirectory(partPath);
                    ExportToCsv(db, Path.Combine(partPath, "part-0.csv"), group.DataFrame);
                }
            }
            using var stream = File.OpenWrite(path);
            var archiveType = path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? ArchiveType.Tar :
                              path.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) ? ArchiveType.Tar : ArchiveType.Zip;
            var compressionType = path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? CompressionType.GZip :
                                 path.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) ? CompressionType.Xz : CompressionType.Deflate;
            using var writer = WriterFactory.Open(stream, archiveType, new WriterOptions(compressionType));
            foreach (var file in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories))
            {
                var entryName = Path.GetRelativePath(tempDir, file).Replace('\\', '/');
                using var fileStream = File.OpenRead(file);
                writer.Write(entryName, fileStream);
            }
            Logger.Debug($"Exported to archive: {path}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to export to archive: {path}");
            throw new DataBallException("Failed to export archive data", ex);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    /// Exports the DataBall data to a CSV file, with optional DataFrame subset.
    /// </summary>
    /// <param name="db">The DataBall instance to export.</param>
    /// <param name="path">The output file path for the CSV.</param>
    /// <param name="df">Optional DataFrame subset; defaults to the full DataBall DataFrame.</param>
    private static void ExportToCsv(DataBall db, string path, DataFrame? df = null)
    {
        df ??= db.Data;
        using var stream = File.OpenWrite(path);
        DataFrame.SaveCsv(df, stream, separator: ',', header: true, encoding: Encoding.UTF8);
        Logger.Debug($"Exported to CSV: {path}");
    }

    /// <summary>
    /// Determines the archive type based on the file extension.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>The <see cref="ArchiveType"/> corresponding to the file extension.</returns>
    /// <exception cref="ArgumentException">Thrown if the archive type is unsupported.</exception>
    private static ArchiveType GetArchiveType(string path)
    {
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return ArchiveType.Zip;
        if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return ArchiveType.Tar;
        if (path.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase)) return ArchiveType.Tar;
        throw new ArgumentException("Unsupported archive type", nameof(path));
    }
}