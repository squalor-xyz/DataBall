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
using NLog;

namespace squalor.DataBall.Backend
{
    /// <summary>
    /// Implements the <see cref="IDataBackend"/> interface for Parquet file-based data storage and operations.
    /// </summary>
    public class ParquetBackend : IDataBackend
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly string _path;

        public ParquetBackend(string path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Path cannot be empty.", nameof(path));
        }

        public async Task<DataFrame> Load(string path)
        {
            Logger.Info("Loading Parquet from {0}", path);
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
                Logger.Info("Parquet load completed");
                return df;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Parquet load failed");
                throw new DataBallException("Failed to load Parquet file", ex);
            }
        }

        public async Task Save(DataFrame df, string path)
        {
            Logger.Info("Saving to Parquet at {0}", path);
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
                Logger.Info("Parquet save completed");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Parquet save failed");
                throw new DataBallException("Failed to save Parquet file", ex);
            }
        }

        public DataFrame Filter<T>(Expression<Func<T, bool>> predicate)
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

        public GroupBy GroupBy(params string[] columnNames)
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

        public DataFrame Join(DataFrame other, string[] leftColumns, string[] rightColumns)
        {
            var df = Load(_path).GetAwaiter().GetResult();
            if (leftColumns.Length != rightColumns.Length || leftColumns.Length == 0)
                throw new ArgumentException("Invalid join columns.", nameof(leftColumns));
            if (leftColumns.Length == 1)
                return df.Join(other, leftColumns[0], rightColumns[0], JoinAlgorithm.Inner);
            throw new NotImplementedException("Multi-column joins not supported.");
        }

        public DataFrame Sort(params string[] columnNames)
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

        public object Aggregate(Dictionary<string, Func<object[], object>> aggregators)
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