using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Microsoft.Data.Analysis;

namespace squalor.DataBall.Backend
{
    /// <summary>
    /// Implements an in-memory backend for DataBall using a DataFrame.
    /// </summary>
    public class InMemoryBackend : IDataBackend
    {
        private DataFrame _data = new DataFrame();

        public Task<DataFrame> Load(string path)
        {
            return Task.FromResult(_data.Clone());
        }

        public Task Save(DataFrame df, string path)
        {
            _data = df.Clone();
            return Task.CompletedTask;
        }

        public DataFrame Filter<T>(Expression<Func<T, bool>> predicate)
        {
            var compiled = predicate.Compile();
            var mask = new PrimitiveDataFrameColumn<bool>("mask", _data.Rows.Count);
            for (long i = 0; i < _data.Rows.Count; i++)
            {
                mask[i] = compiled((T)(object)_data.Rows[i]);
            }
            return _data.Filter(mask);
        }

        public GroupBy GroupBy(params string[] columnNames)
        {
            if (columnNames.Length == 0)
                throw new ArgumentException("At least one column name is required.", nameof(columnNames));
            if (columnNames.Length > 1)
            {
                var keyColumnName = "TempGroupKey";
                var keyCol = new StringDataFrameColumn(keyColumnName, _data.Rows.Count);
                for (long i = 0; i < _data.Rows.Count; i++)
                {
                    keyCol[i] = string.Join("_", columnNames.Select(c => _data[c][i]?.ToString() ?? ""));
                }
                _data.Columns.Add(keyCol);
                return _data.GroupBy(keyColumnName);
            }
            return _data.GroupBy(columnNames[0]);
        }

        public DataFrame Join(DataFrame other, string[] leftColumns, string[] rightColumns)
        {
            if (leftColumns.Length != rightColumns.Length || leftColumns.Length == 0)
                throw new ArgumentException("Invalid join columns.", nameof(leftColumns));
            if (leftColumns.Length == 1)
                return _data.Join(other, leftColumns[0], rightColumns[0], JoinAlgorithm.Inner);
            throw new NotImplementedException("Multi-column joins not supported.");
        }

        public DataFrame Sort(params string[] columnNames)
        {
            if (columnNames.Length == 0)
                throw new ArgumentException("At least one column name is required.", nameof(columnNames));
            var result = _data;
            foreach (var col in columnNames)
            {
                result = result.OrderBy(col);
            }
            return result;
        }

        public object Aggregate(Dictionary<string, Func<object[], object>> aggregators)
        {
            var results = new Dictionary<string, object>();
            foreach (var agg in aggregators)
            {
                var colName = agg.Key;
                var aggregator = agg.Value;
                var col = _data[colName];
                var values = new object[col.Length];
                for (long i = 0; i < col.Length; i++)
                {
                    values[i] = col[i];
                }
                results[colName] = aggregator(values);
            }
            return results;
        }
    }
}