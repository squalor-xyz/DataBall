// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DuckDB.NET.Data;

namespace squalor.DataBall
{
    internal readonly record struct ParameterizedSql(string Sql, DuckDBParameter[] Parameters);

    public sealed partial class DataBall
    {
        private SessionFilter? _currentFilter;

        /// <summary>
        /// Filter last passed to <see cref="ApplyFilter"/>. Null means full-table export.
        /// </summary>
        public SessionFilter? CurrentFilter
        {
            get
            {
                ThrowIfDisposed();
                return _currentFilter;
            }
        }

        /// <summary>
        /// Stores a filter for export/save. Does not change <c>Query(sql)</c> or the <c>data</c> table.
        /// Pass <c>null</c> to clear.
        /// </summary>
        public void ApplyFilter(SessionFilter? filter)
        {
            ThrowIfDisposed();
            if (filter is not null)
                _ = BuildSelectSql(filter);
            _currentFilter = filter;
        }

        /// <summary>
        /// Returns matching rows via DuckDB <c>SELECT</c>. Does not mutate the session table.
        /// Each row is a caller-owned copy; mutating it does not change the store.
        /// </summary>
        public IReadOnlyList<Dictionary<string, object?>> Filter(SessionFilter filter)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(filter);
            try
            {
                var built = BuildSelectSql(filter);
                return _store.Query(built.Sql, built.Parameters);
            }
            catch (Exception ex) when (ex is not DataBallException)
            {
                throw new DataBallException("Filter failed", ex);
            }
        }

        /// <summary>
        /// Returns the number of matching rows without materializing dictionaries.
        /// </summary>
        public long Count(SessionFilter filter)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(filter);
            try
            {
                if (!_store.DataTableExists())
                    throw new DataBallException("No data to filter");
                var tableCols = _store.GetColumns();
                var parameters = new List<DuckDBParameter>();
                var where = BuildWhere(filter.Predicates, tableCols, parameters);
                var sql = new StringBuilder("SELECT COUNT(*) AS c FROM \"data\"");
                if (where.Length > 0)
                    sql.Append(" WHERE ").Append(where);
                var rows = _store.Query(sql.ToString(), parameters.ToArray());
                if (rows.Count == 0)
                    return 0;
                return Convert.ToInt64(rows[0]["c"]);
            }
            catch (Exception ex) when (ex is not DataBallException)
            {
                throw new DataBallException("Filter failed", ex);
            }
        }

        internal ParameterizedSql? FilteredSelectOrNull()
        {
            return _currentFilter is null ? null : BuildSelectSql(_currentFilter);
        }

        private ParameterizedSql BuildSelectSql(SessionFilter filter)
        {
            if (!_store.DataTableExists())
                throw new DataBallException("No data to filter");

            var tableCols = _store.GetColumns();
            var select = BuildSelectList(filter.Columns, tableCols);
            var parameters = new List<DuckDBParameter>();
            var where = BuildWhere(filter.Predicates, tableCols, parameters);
            var sql = new StringBuilder("SELECT ").Append(select).Append(" FROM \"data\"");
            if (where.Length > 0)
                sql.Append(" WHERE ").Append(where);
            return new ParameterizedSql(sql.ToString(), parameters.ToArray());
        }

        private static string BuildSelectList(
            IReadOnlyList<string>? columns,
            IReadOnlyList<(string Name, string DuckDbType)> tableCols)
        {
            if (columns is null)
                return "*";
            if (columns.Count == 0)
                throw new DataBallException("Filter columns is empty");

            var parts = new List<string>(columns.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in columns)
            {
                var name = ResolveColumn(col, tableCols);
                if (!seen.Add(name))
                    throw new DataBallException($"Duplicate filter column '{col}'");
                parts.Add(DuckDbStore.QuoteIdent(name));
            }

            return string.Join(", ", parts);
        }

        private static string BuildWhere(
            IReadOnlyList<ColumnPredicate> predicates,
            IReadOnlyList<(string Name, string DuckDbType)> tableCols,
            List<DuckDBParameter> parameters)
        {
            if (predicates is null || predicates.Count == 0)
                return "";

            var clauses = new List<string>(predicates.Count);
            foreach (var pred in predicates)
            {
                ArgumentNullException.ThrowIfNull(pred);
                var name = ResolveColumn(pred.Column, tableCols);
                var q = DuckDbStore.QuoteIdent(name);
                clauses.Add(pred.Op switch
                {
                    PredicateOp.Eq => EqClause(q, pred.Value, parameters),
                    PredicateOp.Ge => $"{q} >= {Bind(pred.Value, parameters)}",
                    PredicateOp.Le => $"{q} <= {Bind(pred.Value, parameters)}",
                    PredicateOp.Range => RangeClause(q, pred.Value, pred.ValueTo, parameters),
                    PredicateOp.In => InClause(q, pred.Value, parameters),
                    _ => throw new DataBallException($"Unknown predicate op '{pred.Op}'")
                });
            }

            return string.Join(" AND ", clauses);
        }

        private static string ResolveColumn(
            string column,
            IReadOnlyList<(string Name, string DuckDbType)> tableCols)
        {
            if (string.IsNullOrWhiteSpace(column))
                throw new DataBallException("Column is required");
            var match = tableCols.FirstOrDefault(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
            if (match.Name is null)
                throw new DataBallException($"Unknown column '{column}'");
            return match.Name;
        }

        private static string EqClause(string quotedColumn, object? value, List<DuckDBParameter> parameters)
        {
            value = DuckDbStore.Unwrap(value);
            if (value is null)
                return $"{quotedColumn} IS NULL";
            return $"{quotedColumn} = {Bind(value, parameters)}";
        }

        private static string RangeClause(
            string quotedColumn,
            object? value,
            object? valueTo,
            List<DuckDBParameter> parameters)
        {
            if (DuckDbStore.Unwrap(value) is null || DuckDbStore.Unwrap(valueTo) is null)
                throw new DataBallException("Range predicate requires both bounds");
            return $"{quotedColumn} >= {Bind(value, parameters)} AND {quotedColumn} <= {Bind(valueTo, parameters)}";
        }

        private static string InClause(string quotedColumn, object? value, List<DuckDBParameter> parameters)
        {
            if (value is string or null || value is not IEnumerable enumerable)
                throw new DataBallException("IN predicate requires a non-string list");

            var items = new List<string>();
            foreach (var item in enumerable)
                items.Add(Bind(item, parameters));
            if (items.Count == 0)
                throw new DataBallException("IN predicate list is empty");
            return $"{quotedColumn} IN ({string.Join(", ", items)})";
        }

        private static string Bind(object? value, List<DuckDBParameter> parameters)
        {
            var name = $"p{parameters.Count}";
            parameters.Add(DuckDbStore.Param(name, BindValue(value)));
            return "$" + name;
        }

        private static object BindValue(object? value)
        {
            value = DuckDbStore.Unwrap(value);
            if (value is null)
                throw new DataBallException("Filter value cannot be null");
            return value switch
            {
                bool b => b,
                string s => s,
                DateTime dt => dt,
                DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                DateTimeOffset dto => dto.DateTime,
                sbyte n => n,
                byte n => n,
                short n => n,
                ushort n => n,
                int n => n,
                uint n => n,
                long n => n,
                ulong n => n,
                decimal n => n,
                float f when float.IsFinite(f) => f,
                double d when double.IsFinite(d) => d,
                float or double => throw new DataBallException("Non-finite filter value"),
                _ => throw new DataBallException($"Unsupported filter value type: {value.GetType().Name}")
            };
        }
    }
}
