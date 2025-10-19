using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Analysis;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using NLog;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace squalor.DataBall.Export
{
    /// <summary>
    /// Provides static methods for exporting DataBall data to various formats.
    /// </summary>
    public static class ExportManager
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Exports the DataFrame to a Parquet file asynchronously.
        /// </summary>
        /// <param name="df">The DataFrame to export.</param>
        /// <param name="path">The path to save the Parquet file.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous export operation.</returns>
        /// <exception cref="DataBallException">Thrown when the export operation fails.</exception>
        public static async Task ExportToParquet(DataFrame df, string path)
        {
            Logger.Info("Exporting to Parquet at {0}", path);
            try
            {
                using var stream = File.OpenWrite(path);
                var fields = df.Columns.Select(c => new DataField(c.Name, c.DataType)).ToArray();
                var schema = new ParquetSchema(fields);
                using var writer = await ParquetWriter.CreateAsync(schema, stream).ConfigureAwait(false);
                using var rgWriter = writer.CreateRowGroup();
                foreach (var col in df.Columns)
                {
                    var dataArray = GetDataArray(col);
                    var field = schema.DataFields.First(f => f.Name == col.Name);
                    var dataCol = new DataColumn(field, dataArray);
                    await rgWriter.WriteColumnAsync(dataCol).ConfigureAwait(false);
                }
                Logger.Info("Parquet export completed");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Parquet export failed");
                throw new DataBallException("Failed to export Parquet", ex);
            }
        }

        /// <summary>
        /// Exports the DataFrame to partitioned Parquet files based on specified columns.
        /// </summary>
        /// <param name="df">The DataFrame to export.</param>
        /// <param name="path">The directory to save the partitioned Parquet files.</param>
        /// <param name="partitionColumns">The columns to partition by.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous export operation.</returns>
        /// <exception cref="DataBallException">Thrown when the export operation fails.</exception>
        public static async Task ExportToPartitionedParquet(DataFrame df, string path, string[] partitionColumns)
        {
            Logger.Info("Exporting to partitioned Parquet at {0}", path);
            try
            {
                if (partitionColumns.Length == 0)
                {
                    await ExportToParquet(df, Path.Combine(path, "data.parquet"));
                    return;
                }

                string keyColumnName = partitionColumns.Length > 1 ? "TempPartitionKey" : partitionColumns[0];
                if (partitionColumns.Length > 1)
                {
                    var keyCol = new StringDataFrameColumn(keyColumnName, df.Rows.Count);
                    for (long i = 0; i < df.Rows.Count; i++)
                    {
                        keyCol[i] = string.Join("_", partitionColumns.Select(c => df[c][i]?.ToString() ?? ""));
                    }
                    df.Columns.Add(keyCol);
                }

                var groupBy = df.GroupBy<string>(keyColumnName);
                foreach (var grouping in groupBy.Groupings)
                {
                    var groupDf = new DataFrame();
                    foreach (var col in df.Columns)
                    {
                        var groupCol = col.Clone();
                        groupCol.Length = 0;
                        groupDf.Columns.Add(groupCol);
                    }
                    foreach (var row in grouping)
                    {
                        groupDf.Append(row, inPlace: true);
                    }
                    var groupPath = Path.Combine(path, $"{grouping.Key}.parquet");
                    await ExportToParquet(groupDf, groupPath);
                }

                if (partitionColumns.Length > 1)
                {
                    df.Columns.Remove(keyColumnName);
                }
                Logger.Info("Partitioned Parquet export completed");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Partitioned Parquet export failed");
                throw new DataBallException("Failed to export partitioned Parquet", ex);
            }
        }

        /// <summary>
        /// Exports the DataFrame to an archive (ZIP, TAR.GZ, or TAR.XZ).
        /// </summary>
        /// <param name="df">The DataFrame to export.</param>
        /// <param name="path">The path to save the archive.</param>
        /// <exception cref="DataBallException">Thrown when the export operation fails.</exception>
        public static void ExportToArchive(DataFrame df, string path)
        {
            Logger.Info("Exporting to archive {0}", path);
            try
            {
                var archiveType = path.EndsWith(".tar.gz") ? ArchiveType.Tar : path.EndsWith(".tar.xz") ? ArchiveType.Tar : ArchiveType.Zip;
                var compressionType = path.EndsWith(".tar.gz") ? CompressionType.GZip : path.EndsWith(".tar.xz") ? CompressionType.Xz : CompressionType.Deflate;
                using var fs = File.OpenWrite(path);
                using var writer = WriterFactory.Open(fs, archiveType, new WriterOptions(compressionType));
                using var csvStream = new MemoryStream();
                DataFrame.SaveCsv(df, csvStream);
                csvStream.Position = 0;
                writer.Write("data.csv", csvStream);
                Logger.Info("Archive export completed");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Archive export failed");
                throw new DataBallException("Archive export failed", ex);
            }
        }

        /// <summary>
        /// Dispatches the export operation based on the specified export type.
        /// </summary>
        /// <param name="df">The DataFrame to export.</param>
        /// <param name="type">The export format.</param>
        /// <param name="path">The path to save the exported data.</param>
        /// <exception cref="DataBallException">Thrown when the export type is unsupported or the operation fails.</exception>
        public static void Roll(DataFrame df, ExportType type, string path)
        {
            Logger.Info("Rolling DataFrame to {0} with type {1}", path, type);
            try
            {
                switch (type)
                {
                    case ExportType.Parquet:
                        ExportToParquet(df, path).GetAwaiter().GetResult();
                        break;
                    case ExportType.Archive:
                        ExportToArchive(df, path);
                        break;
                    case ExportType.Csv:
                        DataFrame.SaveCsv(df, File.OpenWrite(path));
                        break;
                    case ExportType.Ball:
                        // Placeholder: Implement .ball export (ZIP with Parquet and metadata)
                        throw new NotImplementedException("Ball export not implemented.");
                    case ExportType.Sqlite:
                        // Placeholder: Implement SQLite export
                        throw new NotImplementedException("SQLite export not implemented.");
                    default:
                        throw new DataBallException($"Unsupported export type: {type}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Roll operation failed");
                throw new DataBallException($"Failed to roll DataFrame to {type}", ex);
            }
        }

        private static Array GetDataArray(DataFrameColumn col)
        {
            if (col is StringDataFrameColumn strCol)
                return strCol.ToArray();
            if (col is PrimitiveDataFrameColumn<int> intCol)
                return intCol.ToArray();
            if (col is PrimitiveDataFrameColumn<double> doubleCol)
                return doubleCol.ToArray();
            throw new NotSupportedException($"Column type {col.DataType} not supported");
        }
    }
}