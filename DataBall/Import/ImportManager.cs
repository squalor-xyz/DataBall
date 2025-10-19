using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Analysis;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using NLog;

namespace squalor.DataBall.Import
{
    /// <summary>
    /// Provides static methods for importing data into DataBall from various formats.
    /// </summary>
    public static class ImportManager
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Imports data from a Parquet file into the DataBall instance asynchronously.
        /// </summary>
        /// <param name="db">The DataBall instance to import into.</param>
        /// <param name="path">The path to the Parquet file.</param>
        /// <param name="append">If true, appends data; otherwise, replaces existing data.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous import operation.</returns>
        /// <exception cref="DataBallException">Thrown when the import operation fails.</exception>
        public static async Task ImportFromParquet(DataBall db, string path, bool append)
        {
            Logger.Info("Importing Parquet from {0}, append={1}", path, append);
            try
            {
                var df = new DataFrame();
                using var reader = await ParquetReader.CreateAsync(File.OpenRead(path)).ConfigureAwait(false);
                var schema = reader.Schema;
                for (int rg = 0; rg < reader.RowGroupCount; rg++)
                {
                    using var rgReader = reader.OpenRowGroupReader(rg);
                    for (int c = 0; c < schema.DataFields.Count; c++)
                    {
                        var field = schema.DataFields[c];
                        var colData = await rgReader.ReadColumnAsync(field).ConfigureAwait(false);
                        if (field.ClrType == typeof(string))
                            df.Columns.Add(new StringDataFrameColumn(field.Name, colData.Data.Cast<string?>()));
                        else if (field.ClrType == typeof(int))
                            df.Columns.Add(new PrimitiveDataFrameColumn<int>(field.Name, colData.Data.Cast<int>()));
                        else if (field.ClrType == typeof(double))
                            df.Columns.Add(new PrimitiveDataFrameColumn<double>(field.Name, colData.Data.Cast<double>()));
                        else
                            throw new NotSupportedException($"Unsupported Parquet data type: {field.ClrType}");
                    }
                }
                db.MergeOrAppend(df, append);
                Logger.Info("Parquet import completed");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Parquet import failed");
                throw new DataBallException("Failed to import Parquet file", ex);
            }
        }

        /// <summary>
        /// Imports data from a CSV file into the DataBall instance.
        /// </summary>
        /// <param name="db">The DataBall instance to import into.</param>
        /// <param name="path">The path to the CSV file.</param>
        /// <param name="chunkSize">The number of rows to read per chunk for large files.</param>
        /// <exception cref="DataBallException">Thrown when the import operation fails.</exception>
        public static void ImportFromCsv(DataBall db, string path, int chunkSize = 100000)
        {
            Logger.Info("Importing CSV from {0}", path);
            try
            {
                var df = DataFrame.LoadCsv(path, numberOfRowsToRead: chunkSize);
                db.MergeOrAppend(df, true);
                Logger.Info("CSV import completed");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "CSV import failed");
                throw new DataBallException("Failed to import CSV file", ex);
            }
        }

        /// <summary>
        /// Processes a DataFrame column, handling potential null references.
        /// </summary>
        /// <param name="col">The DataFrame column to process.</param>
        private static void ProcessColumn(DataFrameColumn? col)
        {
            if (col == null)
            {
                Logger.Warn("Column is null");
                return;
            }
            var val = col[0];
            Logger.Debug("Processed column value: {0}", val);
        }
    }
}