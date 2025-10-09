using Microsoft.Data.Analysis;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace squalor.DataBall;

/// <summary>
/// In-memory backend implementation using Microsoft.Data.Analysis.DataFrame.
/// Suitable for moderate-sized datasets that fit in RAM.
/// Delegates operations directly to DataFrame methods for efficiency.
/// </summary>
internal class InMemoryBackend : IDataBackend
{
    private DataFrame _dataFrame;

    public InMemoryBackend(DataFrame dataFrame)
    {
        _dataFrame = dataFrame ?? throw new ArgumentNullException(nameof(dataFrame));
    }

    public DataFrame LoadData() => _dataFrame;

    public async Task SaveDataAsync(Stream stream, string[]? partitionColumns = null)
    {
        // For in-memory, save as Parquet directly; partitioning not supported in-memory without custom logic.
        if (partitionColumns?.Length > 0)
        {
            throw new NotSupportedException("Partitioning requires ParquetBackend for in-memory operations.");
        }

        // Use Parquet.Net for saving, as DataFrame lacks built-in Parquet export.
        var fields = _dataFrame.Columns.Select(col =>
        {
            var clrType = col.DataType;
            return new DataField(col.Name, clrType);
        }).ToArray();

        var schema = new ParquetSchema(fields);
        await using var writer = await ParquetWriter.CreateAsync(schema, stream);
        using var rowGroup = writer.CreateRowGroup();
        for (int i = 0; i < _dataFrame.Columns.Count; i++)
        {
            var col = _dataFrame.Columns[i];
            var data = GetColumnArray(col);
            var dataColumn = new DataColumn(fields[i], data);
            rowGroup.WriteColumn(dataColumn);
        }
    }

    public DataFrame Filter<T>(Expression<Func<T, bool>> predicate) where T : DataFrameRow
    {
        var compiled = predicate.Compile();
        var mask = new PrimitiveDataFrameColumn<bool>("mask", _dataFrame.Rows.Count);
        for (long i = 0; i < _dataFrame.Rows.Count; i++)
        {
            mask[i] = compiled((T)_dataFrame.Rows[i]);
        }
        return _dataFrame.Filter(mask);
    }

    public GroupBy GroupBy(string[] columnNames)
    {
        return _dataFrame.GroupBy(columnNames);
    }

    public DataFrame Join(DataFrame other, string[] leftKeys, string[] rightKeys)
    {
        return _dataFrame.Join(other, leftKeys, rightKeys, JoinAlgorithm.Inner);
    }

    public DataFrame Sort(string[] columnNames)
    {
        return _dataFrame.OrderBy(columnNames);
    }

    public DataFrame Aggregate(Dictionary<string, Func<object[], object>> aggregations)
    {
        var result = new DataFrame();
        foreach (var kvp in aggregations)
        {
            var values = GetColumnArray(_dataFrame[kvp.Key]);
            var aggValue = kvp.Value(values);
            result.Columns.Add(new StringDataFrameColumn(kvp.Key, new[] { aggValue.ToString() }));
        }
        return result;
    }

    public static object[] GetColumnArray(DataFrameColumn col)
    {
        var array = new object[col.Length];
        for (long i = 0; i < col.Length; i++)
        {
            array[i] = col[i] ?? DBNull.Value;
        }
        return array;
    }
}