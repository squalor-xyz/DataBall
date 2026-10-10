// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Readers;
using squalor.DataBall.Export;

namespace squalor.DataBall.Import
{
    /// <summary>
    /// Provides static methods for importing data into DataBall from various formats.
    /// </summary>
    public static class ImportManager
    {
        /// <summary>
        /// Imports data from a Parquet file into the DataBall instance asynchronously.
        /// </summary>
        /// <param name="db">The DataBall instance to import into.</param>
        /// <param name="path">The path to the Parquet file.</param>
        /// <param name="append">If true, appends data; otherwise, replaces existing data.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous import operation.</returns>
        /// <exception cref="DataBallException">Thrown when the import operation fails.</exception>
        public static Task ImportFromParquet(DataBall db, string path, bool append)
        {
            db.Logger.LogInformation("Importing Parquet from {Path}, append={Append}", path, append);
            try
            {
                db.RunImport(append, () => db.Store.ImportParquet(path, append));
                db.Logger.LogInformation("Parquet import completed");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                db.Logger.LogError(ex, "Parquet import failed");
                throw new DataBallException("Failed to import Parquet file", ex);
            }
        }

        /// <summary>
        /// Imports data from a CSV file into the DataBall instance.
        /// Column types come from config or <c>AddColumn</c>. Untyped CSV integers stay DuckDB BIGINT / <c>long</c>.
        /// </summary>
        /// <param name="db">The DataBall instance to import into.</param>
        /// <param name="path">The path to the CSV file.</param>
        /// <param name="append">If true, appends data; otherwise, replaces existing data. Empty dest treats append as replace.</param>
        /// <exception cref="DataBallException">Thrown when the import operation fails.</exception>
        public static void ImportFromCsv(DataBall db, string path, bool append)
        {
            db.Logger.LogInformation("Importing CSV from {Path}, append={Append}", path, append);
            try
            {
                db.RunImport(append, () => db.ImportCsvWithSchema(path, append));
                db.Logger.LogInformation("CSV import completed");
            }
            catch (Exception ex)
            {
                db.Logger.LogError(ex, "CSV import failed");
                throw new DataBallException("Failed to import CSV file", ex);
            }
        }

        /// <summary>
        /// Imports CSV files from a ZIP, TAR, TAR.GZ, or TAR.XZ archive.
        /// </summary>
        /// <param name="db">The DataBall instance to import into.</param>
        /// <param name="path">The path to the archive.</param>
        /// <param name="append">If true, the first CSV is appended; subsequent CSVs always append.</param>
        /// <exception cref="DataBallException">Thrown when the import operation fails.</exception>
        public static void ImportFromArchive(DataBall db, string path, bool append)
        {
            db.RunImport(append, () => ImportFromArchiveCore(db, path, append));
        }

        /// <summary>Archive import body; <see cref="DataBall.ImportAsync"/> runs it inside its own <c>RunImport</c>.</summary>
        internal static void ImportFromArchiveCore(DataBall db, string path, bool append)
        {
            db.Logger.LogInformation("Importing archive from {Path}, append={Append}", path, append);
            var dir = Path.Combine(Path.GetTempPath(), "databall-archive-in-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var rowsBefore = db.Store.DataTableExists() ? db.Store.RowCount() : 0;
                Exception? factoryError = null;
                var imported = false;

                // ArchiveFactory.Open cannot identify xz; skip it for those extensions.
                if (!IsXzArchivePath(path))
                {
                    try
                    {
                        imported = ImportArchiveViaFactory(db, path, dir, append);
                    }
                    catch (Exception ex) when (ex is not DataBallException)
                    {
                        db.Logger.LogError(ex, "Archive factory import failed for {Path}", path);
                        factoryError = ex;
                        var rowsNow = db.Store.DataTableExists() ? db.Store.RowCount() : 0;
                        if (rowsNow > rowsBefore)
                            throw new DataBallException("Failed to import archive", ex);
                    }
                }

                if (!imported)
                    imported = ImportArchiveViaReader(db, path, dir, append);

                if (!imported)
                {
                    if (factoryError is not null)
                        throw new DataBallException("Failed to import archive", factoryError);
                    throw new DataBallException("Archive contains no CSV files");
                }

                db.Logger.LogInformation("Archive import completed");
            }
            catch (Exception ex) when (ex is not DataBallException)
            {
                db.Logger.LogError(ex, "Archive import failed");
                throw new DataBallException("Failed to import archive", ex);
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
        }

        /// <summary>
        /// Imports a native DuckDB .ball file through staging in one transaction.
        /// </summary>
        /// <param name="db">The DataBall instance to import into.</param>
        /// <param name="path">The path to the <c>.ball</c> file.</param>
        /// <param name="append">If true, appends native rows; otherwise, replaces existing data.</param>
        /// <exception cref="DataBallException">Thrown when the import operation fails.</exception>
        public static void ImportFromBall(DataBall db, string path, bool append)
        {
            db.RunImport(append, () => ImportFromBallCore(db, path, append));
        }

        /// <summary>Ball import body; <see cref="DataBall.ImportAsync"/> runs it inside its own <c>RunImport</c>.</summary>
        internal static void ImportFromBallCore(DataBall db, string path, bool append)
        {
            var alias = db.Store.AttachReadOnly(path);
            try
            {
                var config = db.Store.ReadConfig(alias);
                db.ApplyImportedConfig(config);
                db.Store.ImportNativeRows(alias, append);
                db.Store.ImportNativeMetadata(alias);
                db.ApplyImportedConfigMetadata(config);
            }
            finally
            {
                db.Store.Detach(alias);
            }
        }

        internal static ExportType DetectImportFormat(string path) => DataBall.DetectFormat(path);

        private static bool ImportArchiveViaFactory(DataBall db, string path, string dir, bool append)
        {
            try
            {
                using var archive = ArchiveFactory.OpenArchive(path);
                var csvEntries = archive.Entries
                    .Where(e => !e.IsDirectory
                        && e.Key is not null
                        && e.Key.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (csvEntries.Count == 0)
                    return false;

                var first = true;
                foreach (var entry in csvEntries)
                {
                    ExtractAndImportCsv(db, dir, entry.Key!, entry.OpenEntryStream(), first ? append : true);
                    first = false;
                }
                return true;
            }
            catch (Exception ex) when (ex is not DataBallException)
            {
                db.Logger.LogError(ex, "Archive factory could not read {Path}", path);
                throw;
            }
        }

        private static bool ImportArchiveViaReader(DataBall db, string path, string dir, bool append)
        {
            using var stream = File.OpenRead(path);
            using var reader = ReaderFactory.OpenReader(stream);
            var first = true;
            var anyCsv = false;
            while (reader.MoveToNextEntry())
            {
                var entry = reader.Entry;
                if (entry.IsDirectory || entry.Key is null
                    || !entry.Key.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    continue;
                ExtractAndImportCsv(db, dir, entry.Key, reader.OpenEntryStream(), first ? append : true);
                first = false;
                anyCsv = true;
            }
            return anyCsv;
        }

        private static bool IsXzArchivePath(string path)
        {
            var name = Path.GetFileName(path);
            return name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".txz", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".xz", StringComparison.OrdinalIgnoreCase);
        }

        private static void ExtractAndImportCsv(DataBall db, string dir, string entryKey, Stream entryStream, bool append)
        {
            using (entryStream)
            {
                var dest = ZipSlipSafePath(dir, entryKey);
                var destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);
                using (var fileStream = File.Create(dest))
                    entryStream.CopyTo(fileStream);
                db.ImportCsvWithSchema(dest, append);
            }
        }

        private static string ZipSlipSafePath(string destDir, string entryKey)
        {
            var destRoot = Path.GetFullPath(destDir);
            var relative = entryKey.Replace('\\', '/');
            while (relative.StartsWith('/'))
                relative = relative[1..];
            var combined = Path.GetFullPath(Path.Combine(destRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = destRoot.EndsWith(Path.DirectorySeparatorChar)
                ? destRoot
                : destRoot + Path.DirectorySeparatorChar;
            if (!combined.StartsWith(prefix, StringComparison.Ordinal))
                throw new DataBallException("Archive entry path is outside the extraction directory");
            return combined;
        }
    }
}
