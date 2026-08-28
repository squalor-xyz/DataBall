using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Microsoft.Data.Analysis;

namespace squalor.DataBall.Backend;

/// <summary>
/// Defines the interface for DataBall backend implementations, supporting data loading, saving, and operations.
/// </summary>
public interface IDataBackend
{
    /// <summary>
    /// Loads data from the specified path into a DataFrame asynchronously.
    /// </summary>
    /// <param name="path">The path to the data file.</param>
    /// <returns>A <see cref="Task{DataFrame}"/> containing the loaded data.</returns>
    Task<DataFrame> Load(string path);

    /// <summary>
    /// Saves the DataFrame to the specified path asynchronously.
    /// </summary>
    /// <param name="df">The DataFrame to save.</param>
    /// <param name="path">The path to save the data to.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous save operation.</returns>
    Task Save(DataFrame df, string path);

    /// <summary>
    /// Filters the data based on a predicate.
    /// </summary>
    /// <typeparam name="T">The type of the data rows.</typeparam>
    /// <param name="predicate">The filter predicate.</param>
    /// <returns>A <see cref="DataFrame"/> containing the filtered data.</returns>
    DataFrame Filter<T>(Expression<Func<T, bool>> predicate);

    /// <summary>
    /// Groups the data by the specified columns.
    /// </summary>
    /// <param name="columnNames">The names of the columns to group by.</param>
    /// <returns>A <see cref="GroupBy"/> object for further aggregation.</returns>
    GroupBy GroupBy(params string[] columnNames);

    /// <summary>
    /// Joins the data with another DataFrame on the specified columns.
    /// </summary>
    /// <param name="other">The other DataFrame to join with.</param>
    /// <param name="leftColumns">The key columns in the current DataFrame.</param>
    /// <param name="rightColumns">The key columns in the other DataFrame.</param>
    /// <returns>A <see cref="DataFrame"/> containing the joined data.</returns>
    DataFrame Join(DataFrame other, string[] leftColumns, string[] rightColumns);

    /// <summary>
    /// Sorts the data by the specified columns.
    /// </summary>
    /// <param name="columnNames">The names of the columns to sort by.</param>
    /// <returns>A <see cref="DataFrame"/> sorted by the specified columns.</returns>
    DataFrame Sort(params string[] columnNames);

    /// <summary>
    /// Aggregates the data using the specified aggregation functions.
    /// </summary>
    /// <param name="aggregators">A dictionary of column names and their aggregation functions.</param>
    /// <returns>An object containing the aggregated results.</returns>
    object Aggregate(Dictionary<string, Func<object[], object>> aggregators);
}