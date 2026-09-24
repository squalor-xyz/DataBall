// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
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
        /// Imports a <c>.ball</c> ZIP (parquet + metadata, optional config).
        /// <c>data.parquet</c> is optional; metadata-only balls load metadata without a data table.
        /// A v2 ball (<c>manifest.json</c> + <c>tables/&lt;name&gt;.parquet</c>) whose <c>config.json</c>
        /// declares <c>tables</c> is rebuilt table by table; otherwise the wide <c>data.parquet</c> is
        /// imported as before and split when the session has a layout.
        /// </summary>
        /// <param name="db">The DataBall instance to import into.</param>
        /// <param name="path">The path to the <c>.ball</c> file.</param>
        /// <param name="append">If true, appends parquet rows; otherwise, replaces existing data.</param>
        /// <exception cref="DataBallException">Thrown when the import operation fails.</exception>
        public static void ImportFromBall(DataBall db, string path, bool append)
        {
            db.RunImport(append, () => ImportFromBallCore(db, path, append));
        }

        /// <summary>Ball import body; <see cref="DataBall.ImportAsync"/> runs it inside its own <c>RunImport</c>.</summary>
        internal static void ImportFromBallCore(DataBall db, string path, bool append)
        {
            db.Logger.LogInformation("Importing .ball from {Path}, append={Append}", path, append);
            var dir = Path.Combine(Path.GetTempPath(), "databall-ball-in-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                ZipFile.ExtractToDirectory(path, dir);

                // Config first: a layout in config.json decides how the parquet is read.
                Config? config = null;
                var configPath = FindExtractedFile(dir, "config.json");
                if (configPath is not null)
                {
                    config = Config.LoadConfig(configPath);
                    db.ApplyImportedConfig(config);
                }

                var parquet = FindExtractedFile(dir, "data.parquet");
                var loadedTables = !append && TryLoadLayoutTables(db, dir);
                if (!loadedTables && parquet is not null)
                    db.Store.ImportParquet(parquet, append);

                var metadataPath = FindExtractedFile(dir, "metadata.json");
                if (metadataPath is not null)
                    LoadMetadataJson(db, metadataPath);

                // Config metadata keeps winning over metadata.json, as when config was applied last.
                if (config is not null)
                    db.ApplyImportedConfigMetadata(config);

                db.Logger.LogInformation("Ball import completed");
            }
            catch (Exception ex) when (ex is not DataBallException)
            {
                db.Logger.LogError(ex, "Ball import failed");
                throw new DataBallException("Failed to import .ball file", ex);
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
        }

        internal static ExportType DetectImportFormat(string path) => DataBall.DetectFormat(path);

        /// <summary>
        /// Rebuilds a layout from <c>manifest.json</c> + <c>tables/*.parquet</c>. Returns false (and
        /// leaves the session untouched) when the ball is v1, the session has no layout, or the
        /// manifest does not match the config; the caller then falls back to <c>data.parquet</c>.
        /// </summary>
        private static bool TryLoadLayoutTables(DataBall db, string dir)
        {
            if (!db.HasLayoutConfig)
                return false;
            var manifestPath = FindExtractedFile(dir, BallManifest.FileName);
            if (manifestPath is null)
                return false;

            BallManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<BallManifest>(File.ReadAllText(manifestPath), BallManifest.JsonOptions);
            }
            catch (JsonException ex)
            {
                db.Logger.LogWarning(ex, "Ignoring unreadable {Manifest}", BallManifest.FileName);
                return false;
            }

            if (manifest is null || manifest.BallVersion < 2 || manifest.Columns.Count == 0)
                return false;

            TableLayout layout;
            try
            {
                layout = TableLayout.Build(db.Schema, manifest.Columns);
            }
            catch (DataBallException ex)
            {
                db.Logger.LogWarning(ex, "Ball manifest does not fit the session config; using data.parquet");
                return false;
            }

            var expected = new HashSet<string>(layout.PhysicalTableNames, StringComparer.OrdinalIgnoreCase);
            if (!expected.SetEquals(manifest.Tables))
            {
                db.Logger.LogWarning("Ball tables {BallTables} differ from config tables {ConfigTables}; using data.parquet",
                    string.Join(",", manifest.Tables), string.Join(",", layout.PhysicalTableNames));
                return false;
            }

            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in layout.PhysicalTableNames)
            {
                var file = Path.Combine(dir, "tables", table + ".parquet");
                if (!File.Exists(file))
                    return false;
                files[table] = file;
            }

            try
            {
                db.Store.LoadLayoutTables(layout, files, db.CurrentConfig);
                return true;
            }
            catch (DataBallException ex) when (ex.InnerException is not DuckDB.NET.Data.DuckDBException)
            {
                // Validation failed before any catalog write; the wide parquet is still a valid source.
                // A DuckDB-level error has aborted the enclosing transaction and must propagate.
                db.Logger.LogWarning(ex, "Ball tables/ did not fit; using data.parquet");
                return false;
            }
        }

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

        private static void LoadMetadataJson(DataBall db, string path)
        {
            var json = File.ReadAllText(path);
            var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (map is null)
                return;
            foreach (var (key, element) in map)
                db.SetMetadata(key, DuckDbStore.DeserializeMetadataValue(element));
        }

        private static string? FindExtractedFile(string dir, string fileName)
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase));
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
