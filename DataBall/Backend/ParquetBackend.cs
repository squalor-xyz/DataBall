using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.Data.Analysis;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using NLog;

namespace squalor.DataBall.Backend;

/// <summary>
/// Implements a Parquet-based backend for DataBall, supporting streaming load and save operations.
/// </summary>
public class ParquetBackend : IDataBackend
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly string _filePath;

    /// <summary>
    /// Initializes a new instance of the <see cref="ParquetBackend"/> class.
    /// </summary>
    /// <param name="filePath">The path to the Parquet file.</param>
    public ParquetBackend(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    /// <summary>
    /// Loads data from a Parquet file into a DataFrame.
    /// </summary>
    /// <param name="path">The path to the Parquet file.</param>
    /// <returns>A <see cref="DataFrame"/> containing the loaded data.</returns>
    public DataFrame Load(string path)
    {
        try
        {
            var df = new DataFrame();
            using var stream = File.OpenRead(path);
            using var reader = new ParquetReader(stream);
            var schema = reader.Schema;
            for (int rg = 0; rg < reader.RowGroupCount; rg++)
            {
                using var group = reader.OpenRowGroupReader(rg);
                foreach (var field in schema.GetDataFields())
                {
                    var col = group.ReadColumn(field).Data;
                    AddParquetColumnToDataFrame(df, field, col);
                }
            }
            Logger.Info($"Loaded Parquet data from {path}");
            return df;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to load from Parquet: {path}");
            throw new DataBallException("Failed to load Parquet data", ex);
        }
    }

    /// <summary>
    /// Adds a Parquet column to a DataFrame, handling type conversion.
    /// </summary>
    /// <param name="df">The DataFrame to add the column to.</param>
    /// <param name="field">The Parquet data field.</param>
    /// <param name="data">The data array from the Parquet column.</param>
    private void AddParquetColumnToDataFrame(DataFrame df, DataField field, Array data)
    {
        var type = field.ClrType ?? typeof(string);
        if (type == typeof(string))
        {
            df.Columns.Add(new StringDataFrameColumn(field.Name, (IEnumerable<string?>)data));
        }
        else if (type.IsValueType)
        {
            var genType = typeof(PrimitiveDataFrameColumn<>).MakeGenericType(type);
            var values = (IEnumerable<object>)data;
            var col = (DataFrameColumn)Activator.CreateInstance(genType, field.Name, values.Select(v => v == null ? default : Convert.ChangeType(v, type)))!;
            df.Columns.Add(col);
        }
        else
        {
            df.Columns.Add(new StringDataFrameColumn(field.Name, data.Cast<object>().Select(v => v?.ToString())));
        }
        Logger.Debug($"Added Parquet column {field.Name} of type {type.Name}");
    }

    /// <summary>
    /// Saves the DataFrame to a Parquet file.
    /// </summary>
    /// <param name="df">The DataFrame to save.</param>
    /// <param name="path">The path to save the Parquet file to.</param>
    public void Save(DataFrame df, string path)
    {
        try
        {
            var schemaFields = df.Columns.Select(c => new DataField(c.Name, c.DataType)).ToArray();
            var schema = new ParquetSchema(schemaFields);
            using var stream = File.OpenWrite(path);
            using var writer = new ParquetWriter(schema, stream);
            using var group = writer.CreateRowGroup();
            for (int i = 0; i < df.Columns.Count; i++)
            {
                var col = df.Columns[i];
                var values = Enumerable.Range(0, (int)df.Rows.Count).Select(j => col[j]).ToArray();
                var pcol = new DataColumn(schemaFields[i], values);
                group.WriteColumn(pcol);
            }
            Logger.Info($"Saved Parquet data to {path}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to save to Parquet: {path}");
            throw new DataBallException("Failed to save Parquet data", ex);
        }
    }

    /// <summary>
    /// Filters the DataFrame based on a predicate.
    /// </summary>
    /// <typeparam name="T">The type of the data rows (typically <see cref="DataFrameRow"/>).</typeparam>
    /// <param name="predicate">The filter predicate.</param>
    /// <returns>A <see cref="DataFrame"/> containing the filtered rows.</returns>
    public DataFrame Filter<T>(Expression<Func<T, bool>> predicate)
    {
        var df = Load(_filePath);
        var compiled = predicate.Compile();
        var indices = Enumerable.Range(0, (int)df.Rows.Count)
            .Where(i => compiled((T)(object)df.Rows[i]))
            .ToArray();
        Logger.Debug("Applied filter on Parquet backend");
        return df[indices];
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
        var df = Load(_filePath);
        Logger.Debug($"Grouping by {columnNames[0]} on Parquet backend");
        return df.GroupBy(columnNames[0]);
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
        var df = Load(_filePath);
        Logger.Debug($"Joining on {leftColumns[0]} and {rightColumns[0]} on Parquet backend");
        return df.Join(other, leftColumns[0], rightColumns[0]);
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
        var df = Load(_filePath);
        Logger.Debug($"Sorting by {columnNames[0]} on Parquet backend");
        return df.OrderBy(columnNames[0]);
    }

    /// <summary>
    /// Aggregates the DataFrame using the specified aggregation functions.
    /// </summary>
    /// <param name="aggregators">A dictionary of column names and their aggregation functions.</param>
    /// <returns>A dictionary containing the aggregated results.</returns>
    public object Aggregate(Dictionary<string, Func<object[], object>> aggregators)
    {
        var df = Load(_filePath);
        var results = new Dictionary<string, object>();
        foreach (var agg in aggregators)
        {
            var values = Enumerable.Range(0, (int)df.Rows.Count)
                .Select(i => df.Rows[i].ToArray())
                .Select(agg.Value)
                .ToArray();
            results[agg.Key] = values[0]; // Simplified; assumes single result
        }
        Logger.Debug("Applied aggregation on Parquet backend");
        return results;
    }
}