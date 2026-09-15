// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace squalor.DataBall.Export
{
    /// <summary>
    /// Provides static methods for exporting DataBall data to various formats.
    /// </summary>
    public static class ExportManager
    {
        /// <summary>
        /// Exports the DataBall to a Parquet file asynchronously.
        /// </summary>
        /// <param name="db">The DataBall to export.</param>
        /// <param name="path">The path to save the Parquet file.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous export operation.</returns>
        /// <exception cref="DataBallException">Thrown when the export operation fails.</exception>
        public static Task ExportToParquet(DataBall db, string path)
        {
            db.Logger.LogInformation("Exporting to Parquet at {Path}", path);
            try
            {
                db.Store.ExportParquet(path);
                db.Logger.LogInformation("Parquet export completed");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                db.Logger.LogError(ex, "Parquet export failed");
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
            db.Logger.LogInformation("Exporting to partitioned Parquet at {Path}", path);
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
                db.Logger.LogInformation("Partitioned Parquet export completed");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                db.Logger.LogError(ex, "Partitioned Parquet export failed");
                throw new DataBallException("Failed to export partitioned Parquet", ex);
            }
        }

        /// <summary>
        /// Test seam: invoked after the archive file is created and before bytes are written.
        /// </summary>
        internal static Action<string>? AfterArchiveFileCreatedForTests;

        /// <summary>
        /// Exports the DataBall to an archive (ZIP, TAR.GZ, or TAR.XZ).
        /// </summary>
        /// <param name="db">The DataBall to export.</param>
        /// <param name="path">The path to save the archive.</param>
        /// <exception cref="DataBallException">Thrown when the export operation fails.</exception>
        public static void ExportToArchive(DataBall db, string path)
        {
            db.Logger.LogInformation("Exporting to archive {Path}", path);
            var dir = Path.Combine(Path.GetTempPath(), "databall-archive-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string? tmp = null;
            try
            {
                EnsureArchiveExportSupported(path);
                var csvPath = Path.Combine(dir, "data.csv");
                db.Store.ExportCsv(csvPath, db.FilteredSelectOrNull());
                var (archiveType, compressionType) = GetArchiveFormat(path);
                var destDir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);
                tmp = path + ".tmp";
                if (File.Exists(tmp))
                    File.Delete(tmp);
                using (var fs = File.Create(tmp))
                {
                    AfterArchiveFileCreatedForTests?.Invoke(tmp);
                    using var writer = WriterFactory.OpenWriter(fs, archiveType, new WriterOptions(compressionType));
                    using var csvStream = File.OpenRead(csvPath);
                    writer.Write("data.csv", csvStream);
                }
                File.Move(tmp, path, overwrite: true);
                tmp = null;
                db.Logger.LogInformation("Archive export completed");
            }
            catch (Exception ex) when (ex is not DataBallException)
            {
                db.Logger.LogError(ex, "Archive export failed");
                throw new DataBallException("Archive export failed", ex);
            }
            finally
            {
                if (tmp is not null && File.Exists(tmp))
                    File.Delete(tmp);
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
        }

        /// <summary>
        /// Exports the DataBall as a <c>.ball</c> ZIP of parquet plus metadata.
        /// Parquet is omitted when there is no data table; metadata-only balls are allowed.
        /// </summary>
        /// <param name="db">The DataBall to export.</param>
        /// <param name="path">The path to save the <c>.ball</c> file.</param>
        /// <exception cref="DataBallException">Thrown when the export operation fails.</exception>
        public static void ExportToBall(DataBall db, string path)
        {
            db.Logger.LogInformation("Exporting to .ball at {Path}", path);
            var dir = Path.Combine(Path.GetTempPath(), "databall-ball-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string? tmp = null;
            try
            {
                var hasTable = db.Store.DataTableExists();
                if (!hasTable && db.Metadata.Count == 0)
                    throw new DataBallException("No data to export");

                string? parquetPath = null;
                if (hasTable)
                {
                    parquetPath = Path.Combine(dir, "data.parquet");
                    db.Store.ExportParquet(parquetPath, db.FilteredSelectOrNull());
                }

                var metadataPath = Path.Combine(dir, "metadata.json");
                File.WriteAllText(metadataPath, DuckDbStore.SerializeMetadataMap(db.Metadata));

                string? configPath = null;
                if (db.ExpectedColumnTypes.Count > 0 || db.Relationships.Count > 0)
                {
                    var config = new Config
                    {
                        Metadata = new Dictionary<string, object?>(),
                        Columns = ToColumnTypeNames(db.ExpectedColumnTypes),
                        Relationships = new List<Relationship>(db.Relationships)
                    };
                    configPath = Path.Combine(dir, "config.json");
                    File.WriteAllText(configPath, JsonSerializer.Serialize(config));
                }

                var destDir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                tmp = path + ".tmp";
                if (File.Exists(tmp))
                    File.Delete(tmp);

                using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
                {
                    if (parquetPath is not null)
                        zip.CreateEntryFromFile(parquetPath, "data.parquet");
                    zip.CreateEntryFromFile(metadataPath, "metadata.json");
                    if (configPath is not null)
                        zip.CreateEntryFromFile(configPath, "config.json");
                }
                File.Move(tmp, path, overwrite: true);
                tmp = null;
                db.Logger.LogInformation("Ball export completed");
            }
            catch (Exception ex) when (ex is not DataBallException)
            {
                db.Logger.LogError(ex, "Ball export failed");
                throw new DataBallException("Failed to export .ball file", ex);
            }
            finally
            {
                if (tmp is not null && File.Exists(tmp))
                    File.Delete(tmp);
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
            db.ExportAsync(path, type).GetAwaiter().GetResult();
        }

        private static void EnsureArchiveExportSupported(string path)
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".txz", StringComparison.OrdinalIgnoreCase))
            {
                throw new DataBallException(
                    "Export to .tar.xz/.txz is not supported (SharpCompress XZ is decompress-only). Import of .tar.xz is supported.");
            }
        }

        private static (ArchiveType Type, CompressionType Compression) GetArchiveFormat(string path)
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
                return (ArchiveType.Tar, CompressionType.GZip);
            if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
                return (ArchiveType.Tar, CompressionType.None);
            return (ArchiveType.Zip, CompressionType.Deflate);
        }

        private static Dictionary<string, string> ToColumnTypeNames(IReadOnlyDictionary<string, Type> types)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, type) in types)
                map[name] = TypeName(type);
            return map;
        }

        private static string TypeName(Type type)
        {
            if (type == typeof(int)) return "int";
            if (type == typeof(long)) return "long";
            if (type == typeof(float)) return "float";
            if (type == typeof(double)) return "double";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(DateTime)) return "datetime";
            if (type == typeof(string)) return "string";
            return type.FullName ?? type.Name;
        }
    }
}
