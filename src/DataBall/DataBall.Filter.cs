using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace squalor.DataBall
{
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
        /// </summary>
        public IReadOnlyList<Dictionary<string, object?>> Filter(SessionFilter filter)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(filter);
            try
            {
                return _store.Query(BuildSelectSql(filter));
            }
            catch (Exception ex) when (ex is not DataBallException)
            {
                throw new DataBallException("Filter failed", ex);
            }
        }

        internal string? FilteredSelectOrNull()
        {
            return _currentFilter is null ? null : BuildSelectSql(_currentFilter);
        }

        private string BuildSelectSql(SessionFilter filter)
        {
            if (!_store.DataTableExists())
                throw new DataBallException("No data to filter");

            var tableCols = _store.GetColumns();
            var select = BuildSelectList(filter.Columns, tableCols);
            var where = BuildWhere(filter.Predicates, tableCols);
            var sql = new StringBuilder("SELECT ").Append(select).Append(" FROM \"data\"");
            if (where.Length > 0)
                sql.Append(" WHERE ").Append(where);
            return sql.ToString();
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
            IReadOnlyList<(string Name, string DuckDbType)> tableCols)
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
                    PredicateOp.Eq => $"{q} = {SqlLiteral(pred.Value)}",
                    PredicateOp.Ge => $"{q} >= {SqlLiteral(pred.Value)}",
                    PredicateOp.Le => $"{q} <= {SqlLiteral(pred.Value)}",
                    PredicateOp.Range => $"{q} >= {SqlLiteral(pred.Value)} AND {q} <= {SqlLiteral(pred.ValueTo)}",
                    PredicateOp.In => InClause(q, pred.Value),
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

        private static string InClause(string quotedColumn, object? value)
        {
            if (value is string or null || value is not IEnumerable enumerable)
                throw new DataBallException("IN predicate requires a non-string list");

            var items = new List<string>();
            foreach (var item in enumerable)
                items.Add(SqlLiteral(item));
            if (items.Count == 0)
                throw new DataBallException("IN predicate list is empty");
            return $"{quotedColumn} IN ({string.Join(", ", items)})";
        }

        private static string SqlLiteral(object? value)
        {
            value = DuckDbStore.Unwrap(value);
            if (value is null)
                return "NULL";
            return value switch
            {
                bool b => b ? "TRUE" : "FALSE",
                string s => DuckDbStore.QuoteString(s),
                DateTime dt => DuckDbStore.QuoteString(dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                IFormattable n => n.ToString(null, CultureInfo.InvariantCulture) ?? "NULL",
                _ => throw new DataBallException($"Unsupported filter value type: {value.GetType().Name}")
            };
        }
    }
}
