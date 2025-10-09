using Microsoft.Data.Analysis;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace squalor.DataBall;

/// <summary>
/// Interface for backend abstractions in DataBall, allowing interchangeable storage backends
/// such as in-memory DataFrame or disk-based Parquet streaming for large datasets.
/// </summary>
public interface IDataBackend
{
    /// <summary>
    /// Loads data into the backend, returning a DataFrame view.
    /// </summary>
    /// <returns>A DataFrame representing the loaded data.</returns>
    DataFrame LoadData();

    /// <summary>
    /// Saves the current data from the backend to a stream or file.
    /// </summary>
    /// <param name="stream">The output stream.</param>
    /// <param name="partitionColumns">Optional columns for partitioning (Parquet only).</param>
    Task SaveDataAsync(Stream stream, string[]? partitionColumns = null);

    /// <summary>
    /// Filters the data based on a predicate, delegating to the backend's capabilities.
    /// </summary>
    /// <typeparam name="T">The type of the DataFrameRow.</typeparam>
    /// <param name="predicate">The filter predicate.</param>
    /// <returns>A filtered DataFrame.</returns>
    DataFrame Filter<T>(Expression<Func<T, bool>> predicate) where T : DataFrameRow;

    /// <summary>
    /// Groups the data by specified columns.
    /// </summary>
    /// <param name="columnNames">The columns to group by.</param>
    /// <returns>A grouped DataFrame.</returns>
    GroupByResult GroupBy(string[] columnNames);

    /// <summary>
    /// Joins two DataFrames on specified keys.
    /// </summary>
    /// <param name="other">The other DataFrame to join with.</param>
    /// <param name="leftKeys">Keys from this DataFrame.</param>
    /// <param name="rightKeys">Keys from the other DataFrame.</param>
    /// <returns>A joined DataFrame.</returns>
    DataFrame Join(DataFrame other, string[] leftKeys, string[] rightKeys);

    /// <summary>
    /// Sorts the data by specified columns.
    /// </summary>
    /// <param name="columnNames">The columns to sort by.</param>
    /// <returns>A sorted DataFrame.</returns>
    DataFrame Sort(string[] columnNames);

    /// <summary>
    /// Aggregates the data using specified functions.
    /// </summary>
    /// <param name="aggregations">Dictionary of column to aggregation function.</param>
    /// <returns>An aggregated DataFrame.</returns>
    DataFrame Aggregate(Dictionary<string, Func<object[], object>> aggregations);
}