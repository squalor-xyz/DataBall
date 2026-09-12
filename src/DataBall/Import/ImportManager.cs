using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
        private static readonly ILogger Logger = NullLogger.Instance;

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
            Logger.LogInformation("Importing Parquet from {0}, append={1}", path, append);
            try
            {
                db.Store.ImportParquet(path, append);
                Logger.LogInformation("Parquet import completed");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Parquet import failed");
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
            Logger.LogInformation("Importing CSV from {0}, append={1}", path, append);
            try
            {
                db.Store.ImportCsv(path, append, db.ExpectedColumnTypes);
                db.ApplyCsvSchema(path);
                Logger.LogInformation("CSV import completed");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "CSV import failed");
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
            Logger.LogInformation("Importing archive from {0}, append={1}", path, append);
            var dir = Path.Combine(Path.GetTempPath(), "databall-archive-in-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // ArchiveFactory.Open cannot identify xz; skip it for those extensions.
                var imported = IsXzArchivePath(path)
                    ? ImportArchiveViaReader(db, path, dir, append)
                    : ImportArchiveViaFactory(db, path, dir, append)
                        || ImportArchiveViaReader(db, path, dir, append);

                if (!imported)
                    throw new DataBallException("Archive contains no CSV files");
                Logger.LogInformation("Archive import completed");
            }
            catch (Exception ex) when (ex is not DataBallException)
            {
                Logger.LogError(ex, "Archive import failed");
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
        /// </summary>
        /// <param name="db">The DataBall instance to import into.</param>
        /// <param name="path">The path to the <c>.ball</c> file.</param>
        /// <param name="append">If true, appends parquet rows; otherwise, replaces existing data.</param>
        /// <exception cref="DataBallException">Thrown when the import operation fails.</exception>
        public static void ImportFromBall(DataBall db, string path, bool append)
        {
            Logger.LogInformation("Importing .ball from {0}, append={1}", path, append);
            var dir = Path.Combine(Path.GetTempPath(), "databall-ball-in-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                ZipFile.ExtractToDirectory(path, dir);
                var parquet = FindExtractedFile(dir, "data.parquet");
                if (parquet is not null)
                    db.Store.ImportParquet(parquet, append);

                var metadataPath = FindExtractedFile(dir, "metadata.json");
                if (metadataPath is not null)
                    LoadMetadataJson(db, metadataPath);

                var configPath = FindExtractedFile(dir, "config.json");
                if (configPath is not null)
                    db.ApplyImportedConfig(Config.LoadConfig(configPath));

                Logger.LogInformation("Ball import completed");
            }
            catch (Exception ex) when (ex is not DataBallException)
            {
                Logger.LogError(ex, "Ball import failed");
                throw new DataBallException("Failed to import .ball file", ex);
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
        }

        internal static ExportType DetectImportFormat(string path)
        {
            if (Directory.Exists(path))
                return ExportType.Parquet;

            var fileName = Path.GetFileName(path);
            if (string.IsNullOrEmpty(fileName))
                throw new DataBallException($"Unknown import format for '{path}'");

            if (EndsWith(fileName, ".tar.gz") || EndsWith(fileName, ".tgz")
                || EndsWith(fileName, ".tar.xz") || EndsWith(fileName, ".txz")
                || EndsWith(fileName, ".tar") || EndsWith(fileName, ".zip"))
                return ExportType.Archive;
            if (EndsWith(fileName, ".ball"))
                return ExportType.Ball;
            if (EndsWith(fileName, ".csv"))
                return ExportType.Csv;
            if (EndsWith(fileName, ".parquet"))
                return ExportType.Parquet;
            throw new DataBallException($"Unknown import format for '{path}'");
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
                // Unrecognized stream (e.g. some compressed tars); ReaderFactory is the fallback.
                return false;
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
                db.Store.ImportCsv(dest, append, db.ExpectedColumnTypes);
                db.ApplyCsvSchema(dest);
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

        private static bool EndsWith(string fileName, string suffix)
        {
            return fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
