// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Globalization;
using System.Numerics;

namespace squalor.DataBall
{
    /// <summary>Matching rows stored as typed column arrays.</summary>
    public sealed class ColumnSet
    {
        private readonly Dictionary<string, ColumnData> _byName;

        internal ColumnSet(long rowCount, IReadOnlyList<ColumnData> columns)
        {
            RowCount = rowCount;
            Columns = Array.AsReadOnly(columns.ToArray());
            _byName = Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Number of matching rows.</summary>
        public long RowCount { get; }

        /// <summary>Columns in projection order.</summary>
        public IReadOnlyList<ColumnData> Columns { get; }

        /// <summary>Returns a column by its case-insensitive name.</summary>
        public ColumnData this[string name] => _byName.TryGetValue(name, out var column)
            ? column : throw new DataBallException($"Unknown column '{name}'");
    }

    /// <summary>A typed array and optional null mask for one column.</summary>
    public abstract class ColumnData
    {
        private protected ColumnData(string name, Type elementType, Array values)
        {
            Name = name;
            ElementType = elementType;
            Values = values;
        }

        /// <summary>Column name as stored in the session.</summary>
        public string Name { get; }

        /// <summary>CLR type of each array element.</summary>
        public Type ElementType { get; }

        /// <summary>True at null rows; null when the column contains no nulls.</summary>
        public bool[]? Nulls { get; private set; }

        /// <summary>Column values; null slots contain the element type's default value.</summary>
        public Array Values { get; }

        internal void ReadValue(DbDataReader reader, int ordinal, int row)
        {
            try
            {
                if (reader.IsDBNull(ordinal))
                {
                    Nulls ??= new bool[Values.Length];
                    Nulls[row] = true;
                }
                else
                    ReadNonNull(reader, ordinal, row);
            }
            catch (Exception ex)
            {
                throw new DataBallException($"Cannot read column '{Name}'", ex);
            }
        }

        internal abstract void ReadNonNull(DbDataReader reader, int ordinal, int row);
    }

    /// <summary>A column exposing its values without boxing.</summary>
    /// <typeparam name="T">CLR element type.</typeparam>
    public sealed class ColumnData<T> : ColumnData
    {
        private readonly T[] _values;
        private readonly bool _dateOnly;
        private readonly bool _useTypedReader;

        internal ColumnData(string name, int rowCount, bool dateOnly = false, Type? fieldType = null)
            : base(name, typeof(T), new T[rowCount])
        {
            _values = (T[])base.Values;
            _dateOnly = dateOnly;
            _useTypedReader = fieldType == typeof(T);
        }

        /// <summary>Typed values; null slots contain default(T).</summary>
        public new T[] Values => _values;

        internal override void ReadNonNull(DbDataReader reader, int ordinal, int row)
        {
            if (_useTypedReader)
            {
                _values[row] = reader.GetFieldValue<T>(ordinal);
                return;
            }
            var value = _dateOnly
                ? reader.GetFieldValue<DateOnly>(ordinal).ToDateTime(TimeOnly.MinValue)
                : reader.GetValue(ordinal);
            if (value is T typed)
                _values[row] = typed;
            else if (typeof(T) == typeof(string))
                _values[row] = (T)(object)(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
            else if (value is BigInteger big && typeof(T) == typeof(double))
                _values[row] = (T)(object)(double)big;
            else
                _values[row] = (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
        }
    }
}
