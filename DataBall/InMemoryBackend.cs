using Microsoft.Data.Analysis;
using Parquet;
using Parquet.Data;
using System;
using System.Collections.Generic;
using System.IO;
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
            return clrType.Name switch
            {
                nameof(String) => new DataField<string>(col.Name),
                nameof(Int32) => new DataField<int>(col.Name),
                nameof(Int64) => new DataField<long>(col.Name),
                nameof(Single) => new DataField<float>(col.Name),
                nameof(Double) => new DataField<double>(col.Name),
                nameof(Boolean) => new DataField<bool>(col.Name),
                nameof(DateTime) => new DataField<DateTime>(col.Name),
                _ => new DataField<string>(col.Name)
            };
        }).ToArray();

        var schema = new ParquetSchema(fields);
        await using var writer = await ParquetWriter.CreateAsync(schema, stream);
        await using var rowGroup = writer.CreateRowGroupAsync();
        for (int i = 0; i < _dataFrame.Columns.Count; i++)
        {
            var col = _dataFrame.Columns[i];
            var data = Array.CreateInstance(col.DataType, (int)col.Length);
            for (long j = 0; j < col.Length; j++)
            {
                data.SetValue(col[j] ?? DBNull.Value, j);
            }
            var dataColumn = new DataColumn(fields[i], data);
            await rowGroup.WriteColumnAsync(dataColumn);
        }
    }

    public DataFrame Filter<T>(Expression<Func<T, bool>> predicate) where T : DataFrameRow
    {
        return _dataFrame.Filter(predicate.Compile());
    }

    public GroupBy GroupBy(string[] columnNames)
    {
        return _dataFrame.GroupBy(columnNames);
    }

    public DataFrame Join(DataFrame other, string[] leftKeys, string[] rightKeys)
    {
        // Simple inner join delegation; for large data, switch to ParquetBackend.
        return _dataFrame.Join(other, leftKeys, rightKeys, JoinType.Inner);
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
            var values = _dataFrame[kvp.Key].Select(x => x).ToArray();
            var aggValue = kvp.Value(values);
            result.AddColumn(new PrimitiveDataFrameColumn<object>(kvp.Key, new[] { aggValue }));
        }
        return result;
    }
}