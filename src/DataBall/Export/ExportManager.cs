// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace squalor.DataBall.Export
{
    /// <summary>
    /// Writes the archive export of a session; <see cref="DataBall.ExportAsync(string, ExportOptions)"/> is the way in.
    /// </summary>
    internal static class ExportManager
    {
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
        internal static void ExportToArchive(DataBall db, string path)
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

    }
}
