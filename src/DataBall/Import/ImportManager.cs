// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace squalor.DataBall.Import
{
    /// <summary>
    /// The archive and native <c>.ball</c> import bodies and the archive extraction behind <c>Open</c>;
    /// <see cref="DataBall.ImportAsync(string, ImportOptions?, System.Threading.CancellationToken)"/> is the way in.
    /// </summary>
    internal static class ImportManager
    {
        /// <summary>Archive import body; <see cref="DataBall.ImportAsync(string, ImportOptions?, System.Threading.CancellationToken)"/> runs it inside its own <c>RunImport</c>.</summary>
        internal static void ImportFromArchiveCore(DataBall db, string path, bool append)
        {
            db.Logger.LogInformation("Importing archive from {Path}, append={Append}", path, append);
            var dir = Path.Combine(Path.GetTempPath(), "databall-archive-in-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            // Each CSV entry is its own source, recorded as <archive>!<entry>.
            var entrySource = new ArchiveEntrySource(Path.GetFullPath(path), nameof(BuiltInPath.ArchiveImport));
            try
            {
                var rowsBefore = db.Store.DataTableExists() ? db.Store.RowCount() : 0;
                var sourcesBefore = db.Store.MaxSourceKey();
                Exception? factoryError = null;
                var imported = false;

                // ArchiveFactory.Open cannot identify xz; skip it for those extensions.
                if (!IsXzArchivePath(path))
                {
                    try
                    {
                        imported = ImportArchiveViaFactory(db, path, dir, append, entrySource);
                    }
                    catch (Exception ex) when (ex is not DataBallException)
                    {
                        db.Logger.LogError(ex, "Archive factory import failed for {Path}", path);
                        factoryError = ex;
                        var rowsNow = db.Store.DataTableExists() ? db.Store.RowCount() : 0;
                        // Rows or recorded sources mean entries were already imported: the reader fallback would record them twice.
                        if (rowsNow > rowsBefore || db.Store.MaxSourceKey() > sourcesBefore)
                            throw new DataBallException("Failed to import archive", ex);
                    }
                }

                if (!imported)
                    imported = ImportArchiveViaReader(db, path, dir, append, entrySource);

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

        /// <summary>Ball import body; <see cref="DataBall.ImportAsync(string, ImportOptions?, System.Threading.CancellationToken)"/> runs it inside its own <c>RunImport</c>.</summary>
        internal static void ImportFromBallCore(DataBall db, string path, bool append)
        {
            var alias = db.Store.AttachReadOnly(path);
            try
            {
                var config = db.Store.ReadConfig(alias);
                db.ApplyImportedConfig(config);
                db.Store.ImportNativeRows(alias, append, db.DeclaredMergeTypes(), $"'{path}'");
                db.Store.ImportNativeMetadata(alias);
                db.ApplyImportedConfigMetadata(config);
            }
            finally
            {
                db.Store.Detach(alias);
            }
        }

        /// <summary>A CSV entry of an archive, extracted to <see cref="Path"/>.</summary>
        internal sealed record ExtractedEntry(string Key, string Path, DateTime? Modified);

        /// <summary>
        /// Extracts every CSV entry of an archive into <paramref name="dir"/> and returns them sorted by entry key. A many-file open
        /// stages each entry as its own source; the caller deletes <paramref name="dir"/>.
        /// </summary>
        /// <exception cref="DataBallException">The archive cannot be read or holds no CSV entry.</exception>
        internal static IReadOnlyList<ExtractedEntry> ExtractCsvEntries(string path, string dir)
        {
            Exception? factoryError = null;
            try
            {
                // ArchiveFactory.Open cannot identify xz; skip it for those extensions.
                if (!IsXzArchivePath(path))
                {
                    try
                    {
                        var viaFactory = ExtractViaFactory(path, dir);
                        if (viaFactory.Count > 0)
                            return viaFactory;
                    }
                    catch (Exception ex) when (ex is not DataBallException)
                    {
                        factoryError = ex;
                    }
                }

                var viaReader = ExtractViaReader(path, dir);
                if (viaReader.Count > 0)
                    return viaReader;
            }
            catch (Exception ex) when (ex is not DataBallException)
            {
                throw new DataBallException("Failed to import archive", ex);
            }
            if (factoryError is not null)
                throw new DataBallException("Failed to import archive", factoryError);
            throw new DataBallException("Archive contains no CSV files");
        }

        private static List<ExtractedEntry> ExtractViaFactory(string path, string dir)
        {
            using var archive = ArchiveFactory.OpenArchive(path);
            var result = new List<ExtractedEntry>();
            foreach (var entry in archive.Entries.Where(IsCsvEntry))
            {
                using var stream = entry.OpenEntryStream();
                result.Add(Extract(dir, entry.Key!, stream, entry.LastModifiedTime));
            }
            return SortByKey(result);
        }

        private static List<ExtractedEntry> ExtractViaReader(string path, string dir)
        {
            using var stream = File.OpenRead(path);
            using var reader = ReaderFactory.OpenReader(stream);
            var result = new List<ExtractedEntry>();
            while (reader.MoveToNextEntry())
            {
                if (!IsCsvEntry(reader.Entry))
                    continue;
                using var entryStream = reader.OpenEntryStream();
                result.Add(Extract(dir, reader.Entry.Key!, entryStream, reader.Entry.LastModifiedTime));
            }
            return SortByKey(result);
        }

        private static bool IsCsvEntry(SharpCompress.Common.IEntry entry) =>
            !entry.IsDirectory && entry.Key is not null && entry.Key.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

        private static List<ExtractedEntry> SortByKey(List<ExtractedEntry> entries) =>
            entries.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Key, StringComparer.Ordinal).ToList();

        private static ExtractedEntry Extract(string dir, string key, Stream entryStream, DateTime? modified)
        {
            var dest = ZipSlipSafePath(dir, key);
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);
            using (var file = File.Create(dest))
                entryStream.CopyTo(file);
            return new ExtractedEntry(key, dest, modified);
        }

        private sealed record ArchiveEntrySource(string ArchivePath, string Type);

        private static bool ImportArchiveViaFactory(DataBall db, string path, string dir, bool append, ArchiveEntrySource entrySource)
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
                    ExtractAndImportCsv(db, dir, entry.Key!, entry.OpenEntryStream(), first ? append : true, entrySource, entry.LastModifiedTime);
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

        private static bool ImportArchiveViaReader(DataBall db, string path, string dir, bool append, ArchiveEntrySource entrySource)
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
                ExtractAndImportCsv(db, dir, entry.Key, reader.OpenEntryStream(), first ? append : true, entrySource, entry.LastModifiedTime);
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

        private static void ExtractAndImportCsv(
            DataBall db, string dir, string entryKey, Stream entryStream, bool append, ArchiveEntrySource source, DateTime? modified)
        {
            using (entryStream)
            {
                var dest = ZipSlipSafePath(dir, entryKey);
                var destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);
                using (var fileStream = File.Create(dest))
                    entryStream.CopyTo(fileStream);
                var record = SourceFile.DescribeEntry(dest, source.ArchivePath + "!" + entryKey, source.Type, modified);
                db.ImportCsvWithSchema(dest, append, record);
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
