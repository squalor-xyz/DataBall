using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace squalor.DataBall.Export
{
    /// <summary>
    /// Provides static methods for exporting DataBall data to various formats.
    /// </summary>
    public static class ExportManager
    {
        private static readonly ILogger Logger = NullLogger.Instance;

        /// <summary>
        /// Exports the DataBall to a Parquet file asynchronously.
        /// </summary>
        /// <param name="db">The DataBall to export.</param>
        /// <param name="path">The path to save the Parquet file.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous export operation.</returns>
        /// <exception cref="DataBallException">Thrown when the export operation fails.</exception>
        public static Task ExportToParquet(DataBall db, string path)
        {
            Logger.LogInformation("Exporting to Parquet at {0}", path);
            try
            {
                db.Store.ExportParquet(path);
                Logger.LogInformation("Parquet export completed");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Parquet export failed");
                throw new DataBallException("Failed to export Parquet", ex);
            }
        }

        /// <summary>
        /// Exports the DataBall to partitioned Parquet files based on specified columns.
        /// </summary>
        /// <param name="db">The DataBall to export.</param>
        /// <param name="path">The directory to save the partitioned Parquet files.</param>
        /// <param name="partitionColumns">The columns to partition by.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous export operation.</returns>
        /// <exception cref="DataBallException">Thrown when the export operation fails.</exception>
        public static Task ExportToPartitionedParquet(DataBall db, string path, string[] partitionColumns)
        {
            Logger.LogInformation("Exporting to partitioned Parquet at {0}", path);
            try
            {
                if (partitionColumns.Length == 0)
                {
                    db.Store.ExportParquet(Path.Combine(path, "data.parquet"));
                }
                else
                {
                    db.Store.ExportPartitionedParquet(path, partitionColumns);
                }
                Logger.LogInformation("Partitioned Parquet export completed");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Partitioned Parquet export failed");
                throw new DataBallException("Failed to export partitioned Parquet", ex);
            }
        }

        /// <summary>
        /// Exports the DataBall to an archive (ZIP, TAR.GZ, or TAR.XZ).
        /// </summary>
        /// <param name="db">The DataBall to export.</param>
        /// <param name="path">The path to save the archive.</param>
        /// <exception cref="DataBallException">Thrown when the export operation fails.</exception>
        public static void ExportToArchive(DataBall db, string path)
        {
            Logger.LogInformation("Exporting to archive {0}", path);
            var dir = Path.Combine(Path.GetTempPath(), "databall-archive-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var csvPath = Path.Combine(dir, "data.csv");
                db.Store.ExportCsv(csvPath);
                var archiveType = path.EndsWith(".tar.gz") ? ArchiveType.Tar : path.EndsWith(".tar.xz") ? ArchiveType.Tar : ArchiveType.Zip;
                var compressionType = path.EndsWith(".tar.gz") ? CompressionType.GZip : path.EndsWith(".tar.xz") ? CompressionType.Xz : CompressionType.Deflate;
                using var fs = File.OpenWrite(path);
                using var writer = WriterFactory.Open(fs, archiveType, new WriterOptions(compressionType));
                using var csvStream = File.OpenRead(csvPath);
                writer.Write("data.csv", csvStream);
                Logger.LogInformation("Archive export completed");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Archive export failed");
                throw new DataBallException("Archive export failed", ex);
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
        }

        /// <summary>
        /// Dispatches the export operation based on the specified export type.
        /// </summary>
        /// <param name="db">The DataBall to export.</param>
        /// <param name="type">The export format.</param>
        /// <param name="path">The path to save the exported data.</param>
        /// <exception cref="DataBallException">Thrown when the export type is unsupported or the operation fails.</exception>
        public static void Roll(DataBall db, ExportType type, string path)
        {
            Logger.LogInformation("Rolling DataBall to {0} with type {1}", path, type);
            try
            {
                switch (type)
                {
                    case ExportType.Parquet:
                        ExportToParquet(db, path).GetAwaiter().GetResult();
                        break;
                    case ExportType.Archive:
                        ExportToArchive(db, path);
                        break;
                    case ExportType.Csv:
                        db.Store.ExportCsv(path);
                        break;
                    case ExportType.Ball:
                        throw new NotImplementedException("Ball export not implemented.");
                    case ExportType.Sqlite:
                        throw new NotImplementedException("SQLite export not implemented.");
                    default:
                        throw new DataBallException($"Unsupported export type: {type}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Roll operation failed");
                throw new DataBallException($"Failed to roll DataBall to {type}", ex);
            }
        }
    }
}
