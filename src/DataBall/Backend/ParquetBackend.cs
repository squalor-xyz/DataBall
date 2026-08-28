using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Microsoft.Data.Analysis;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace squalor.DataBall.Backend
{
    /// <summary>
    /// Implements the <see cref="IDataBackend"/> interface for Parquet file-based data storage and operations.
    /// </summary>
    public class ParquetBackend : IDataBackend
    {
        private readonly ILogger _logger;
        private readonly string _path;

        /// <summary>
        /// Initializes a new instance of the <see cref="ParquetBackend"/> class with the specified file path.
        /// </summary>
        /// <param name="path">The path to the Parquet file.</param>
        /// <param name="logger">Optional logger. Defaults to a no-op logger.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="path"/> is null or empty.</exception>
        public ParquetBackend(string path, ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
            _path = path ?? throw new ArgumentNullException(nameof(path));
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Path cannot be empty.", nameof(path));
        }

        /// <summary>
        /// Loads data from the specified Parquet file into a DataFrame asynchronously.
        /// </summary>
        /// <param name="path">The path to the Parquet file.</param>
        /// <returns>A <see cref="Task{DataFrame}"/> containing the loaded data.</returns>
        /// <exception cref="DataBallException">Thrown when loading the Parquet file fails.</exception>
        public async Task<DataFrame> Load(string path)
        {
            _logger.LogInformation("Loading Parquet from {0}", path);
            try
            {
                var df = new DataFrame();
                using var reader = await ParquetReader.CreateAsync(File.OpenRead(path)).ConfigureAwait(false);
                var schema = reader.Schema;
                for (int rg = 0; rg < reader.RowGroupCount; rg++)
                {
                    using var rgReader = reader.OpenRowGroupReader(rg);
                    for (int c = 0; c < schema.DataFields.Length; c++)
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
                _logger.LogInformation("Parquet load completed");
                return df;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Parquet load failed");
                throw new DataBallException("Failed to load Parquet file", ex);
            }
        }

        /// <summary>
        /// Saves the DataFrame to the specified Parquet file asynchronously.
        /// </summary>
        /// <param name="df">The DataFrame to save.</param>
        /// <param name="path">The path to save the Parquet file to.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous save operation.</returns>
        /// <exception cref="DataBallException">Thrown when saving the Parquet file fails.</exception>
        public async Task Save(DataFrame df, string path)
        {
            _logger.LogInformation("Saving to Parquet at {0}", path);
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
                _logger.LogInformation("Parquet save completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Parquet save failed");
                throw new DataBallException("Failed to save Parquet file", ex);
            }
        }

        /// <summary>
        /// Filters the data based on a predicate.
        /// </summary>
        /// <typeparam name="T">The type of the data rows.</typeparam>
        /// <param name="predicate">The filter predicate.</param>
        /// <returns>A <see cref="DataFrame"/> containing the filtered data.</returns>
        public DataFrame Filter<T>(Expression<Func<T, bool>> predicate)
        {
            _logger.LogDebug("Filtering DataFrame with predicate");
            try
            {
                var df = Load(_path).GetAwaiter().GetResult();
                var compiled = predicate.Compile();
                var mask = new PrimitiveDataFrameColumn<bool>("mask", df.Rows.Count);
                for (long i = 0; i < df.Rows.Count; i++)
                {
                    mask[i] = compiled((T)(object)df.Rows[i]);
                }
                return df.Filter(mask);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Filter operation failed");
                throw new DataBallException("Failed to filter DataFrame", ex);
            }
        }

        /// <summary>
        /// Groups the data by the specified columns.
        /// </summary>
        /// <param name="columnNames">The names of the columns to group by.</param>
        /// <returns>A <see cref="GroupBy"/> object for further aggregation.</returns>
        public GroupBy GroupBy(params string[] columnNames)
        {
            _logger.LogDebug("Grouping DataFrame by {0}", string.Join(", ", columnNames));
            try
            {
                var df = Load(_path).GetAwaiter().GetResult();
                if (columnNames.Length == 0)
                    throw new ArgumentException("At least one column name is required.", nameof(columnNames));
                if (columnNames.Length > 1)
                {
                    var keyColumnName = "TempGroupKey";
                    var keyCol = new StringDataFrameColumn(keyColumnName, df.Rows.Count);
                    for (long i = 0; i < df.Rows.Count; i++)
                    {
                        keyCol[i] = string.Join("_", columnNames.Select(c => df[c][i]?.ToString() ?? ""));
                    }
                    df.Columns.Add(keyCol);
                    return df.GroupBy(keyColumnName);
                }
                return df.GroupBy(columnNames[0]);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GroupBy operation failed");
                throw new DataBallException("Failed to group DataFrame", ex);
            }
        }

        /// <summary>
        /// Joins the data with another DataFrame on the specified columns.
        /// </summary>
        /// <param name="other">The other DataFrame to join with.</param>
        /// <param name="leftColumns">The key columns in the current DataFrame.</param>
        /// <param name="rightColumns">The key columns in the other DataFrame.</param>
        /// <returns>A <see cref="DataFrame"/> containing the joined data.</returns>
        public DataFrame Join(DataFrame other, string[] leftColumns, string[] rightColumns)
        {
            _logger.LogDebug("Joining DataFrames on left: {0}, right: {1}", string.Join(", ", leftColumns), string.Join(", ", rightColumns));
            try
            {
                var df = Load(_path).GetAwaiter().GetResult();
                if (leftColumns.Length != rightColumns.Length || leftColumns.Length == 0)
                    throw new ArgumentException("Invalid join columns.", nameof(leftColumns));
                if (leftColumns.Length == 1)
                    return df.Join(other, leftColumns[0], rightColumns[0], JoinAlgorithm.Inner);
                throw new NotImplementedException("Multi-column joins not supported by Microsoft.Data.Analysis.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Join operation failed");
                throw new DataBallException("Failed to join DataFrames", ex);
            }
        }

        /// <summary>
        /// Sorts the data by the specified columns.
        /// </summary>
        /// <param name="columnNames">The names of the columns to sort by.</param>
        /// <returns>A <see cref="DataFrame"/> sorted by the specified columns.</returns>
        public DataFrame Sort(params string[] columnNames)
        {
            _logger.LogDebug("Sorting DataFrame by {0}", string.Join(", ", columnNames));
            try
            {
                var df = Load(_path).GetAwaiter().GetResult();
                if (columnNames.Length == 0)
                    throw new ArgumentException("At least one column name is required.", nameof(columnNames));
                var result = df;
                foreach (var col in columnNames)
                {
                    result = result.OrderBy(col);
                }
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sort operation failed");
                throw new DataBallException("Failed to sort DataFrame", ex);
            }
        }

        /// <summary>
        /// Aggregates the data using the specified aggregation functions.
        /// </summary>
        /// <param name="aggregators">A dictionary of column names and their aggregation functions.</param>
        /// <returns>An object containing the aggregated results.</returns>
        public object Aggregate(Dictionary<string, Func<object[], object>> aggregators)
        {
            _logger.LogDebug("Aggregating DataFrame");
            try
            {
                var df = Load(_path).GetAwaiter().GetResult();
                var results = new Dictionary<string, object>();
                foreach (var agg in aggregators)
                {
                    var colName = agg.Key;
                    var aggregator = agg.Value;
                    var col = df[colName];
                    var values = new object[col.Length];
                    for (long i = 0; i < col.Length; i++)
                    {
                        values[i] = col[i];
                    }
                    results[colName] = aggregator(values);
                }
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Aggregate operation failed");
                throw new DataBallException("Failed to aggregate DataFrame", ex);
            }
        }

        private Array GetDataArray(DataFrameColumn col)
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