using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.Data.Analysis;

namespace squalor.DataBall.Backend;

/// <summary>
/// Implements an in-memory backend for DataBall using a DataFrame.
/// </summary>
public class InMemoryBackend : IDataBackend
{
    private readonly DataFrame _df;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryBackend"/> class.
    /// </summary>
    /// <param name="df">The DataFrame to use for in-memory storage.</param>
    public InMemoryBackend(DataFrame df)
    {
        _df = df ?? throw new ArgumentNullException(nameof(df));
    }

    /// <summary>
    /// Loads data from a CSV file into a DataFrame.
    /// </summary>
    /// <param name="path">The path to the CSV file.</param>
    /// <returns>A <see cref="DataFrame"/> containing the loaded data.</returns>
    public DataFrame Load(string path)
    {
        return DataFrame.LoadCsv(path);
    }

    /// <summary>
    /// Saves the DataFrame to a CSV file.
    /// </summary>
    /// <param name="df">The DataFrame to save.</param>
    /// <param name="path">The path to save the CSV file to.</param>
    public void Save(DataFrame df, string path)
    {
        DataFrame.SaveCsv(df, path);
    }

    /// <summary>
    /// Filters the DataFrame based on a predicate.
    /// </summary>
    /// <typeparam name="T">The type of the data rows (typically <see cref="DataFrameRow"/>).</typeparam>
    /// <param name="predicate">The filter predicate.</param>
    /// <returns>A <see cref="DataFrame"/> containing the filtered rows.</returns>
    public DataFrame Filter<T>(Expression<Func<T, bool>> predicate)
    {
        var compiled = predicate.Compile();
        var indices = Enumerable.Range(0, (int)_df.Rows.Count)
            .Where(i => compiled((T)(object)_df.Rows[i]))
            .ToArray();
        return _df[indices];
    }

    /// <summary>
    /// Groups the DataFrame by the specified columns.
    /// </summary>
    /// <param name="columnNames">The names of the columns to group by (only the first is used due to library limitations).</param>
    /// <returns>A <see cref="GroupBy"/> object for further aggregation.</returns>
    public GroupBy GroupBy(params string[] columnNames)
    {
        if (columnNames == null || columnNames.Length == 0)
            throw new ArgumentException("At least one column name must be provided.", nameof(columnNames));
        return _df.GroupBy(columnNames[0]);
    }

    /// <summary>
    /// Joins the DataFrame with another DataFrame using the specified columns.
    /// </summary>
    /// <param name="other">The other DataFrame to join with.</param>
    /// <param name="leftColumns">The key columns in the current DataFrame (only the first is used due to library limitations).</param>
    /// <param name="rightColumns">The key columns in the other DataFrame (only the first is used due to library limitations).</param>
    /// <returns>A <see cref="DataFrame"/> containing the joined data.</returns>
    public DataFrame Join(DataFrame other, string[] leftColumns, string[] rightColumns)
    {
        if (leftColumns == null || leftColumns.Length == 0 || rightColumns == null || rightColumns.Length == 0)
            throw new ArgumentException("At least one column name must be provided for both left and right columns.");
        return _df.Join(other, leftColumns[0], rightColumns[0]);
    }

    /// <summary>
    /// Sorts the DataFrame by the specified columns.
    /// </summary>
    /// <param name="columnNames">The names of the columns to sort by (only the first is used due to library limitations).</param>
    /// <returns>A <see cref="DataFrame"/> sorted by the specified column.</returns>
    public DataFrame Sort(params string[] columnNames)
    {
        if (columnNames == null || columnNames.Length == 0)
            throw new ArgumentException("At least one column name must be provided.", nameof(columnNames));
        return _df.OrderBy(columnNames[0]);
    }

    /// <summary>
    /// Aggregates the DataFrame using the specified aggregation functions.
    /// </summary>
    /// <param name="aggregators">A dictionary of column names and their aggregation functions.</param>
    /// <returns>A dictionary containing the aggregated results.</returns>
    public object Aggregate(Dictionary<string, Func<object[], object>> aggregators)
    {
        var results = new Dictionary<string, object>();
        foreach (var agg in aggregators)
        {
            var values = Enumerable.Range(0, (int)_df.Rows.Count)
                .Select(i => _df.Rows[i].ToArray())
                .Select(agg.Value)
                .ToArray();
            results[agg.Key] = values[0]; // Simplified; assumes single result
        }
        return results;
    }
}