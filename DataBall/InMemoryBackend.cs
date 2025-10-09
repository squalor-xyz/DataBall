using Microsoft.Data.Analysis;
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
        // For in-memory, save as Parquet or CSV directly; partitioning not supported in-memory.
        if (partitionColumns?.Length > 0)
        {
            throw new NotSupportedException("Partitioning requires ParquetBackend for in-memory operations.");
        }

        // Serialize to Parquet (Arrow-backed) for efficiency.
        await _dataFrame.SaveParquetAsync(stream);
    }

    public DataFrame Filter<T>(Expression<Func<T, bool>> predicate) where T : DataFrameRow
    {
        return _dataFrame.Filter(predicate.Compile());
    }

    public GroupByResult GroupBy(string[] columnNames)
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
        var grouped = _dataFrame.GroupBy(new[] { "temp" }); // Dummy group for aggregate.
        // Implement aggregation logic by iterating over groups.
        // For simplicity, assume sum/mean etc.; extend as needed.
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