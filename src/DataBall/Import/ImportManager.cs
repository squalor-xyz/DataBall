using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
        /// </summary>
        /// <param name="db">The DataBall instance to import into.</param>
        /// <param name="path">The path to the CSV file.</param>
        /// <param name="chunkSize">Ignored; DuckDB streams the file.</param>
        /// <exception cref="DataBallException">Thrown when the import operation fails.</exception>
        public static void ImportFromCsv(DataBall db, string path, int chunkSize = 100000)
        {
            _ = chunkSize;
            Logger.LogInformation("Importing CSV from {0}", path);
            try
            {
                db.Store.ImportCsv(path, append: true);
                Logger.LogInformation("CSV import completed");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "CSV import failed");
                throw new DataBallException("Failed to import CSV file", ex);
            }
        }
    }
}
