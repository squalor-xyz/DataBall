// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace squalor.DataBall
{
    public sealed partial class DataBall
    {
        private Dictionary<string, object?>? _pendingRow;
        private Dictionary<string, object?>? _originalRow;
        private HashSet<string>? _modifiedFields;
        private Dictionary<string, object?>? _lastCommittedRow;

        /// <summary>
        /// Starts a new pending row, copying values from the last committed row when one exists.
        /// </summary>
        /// <param name="initial">Optional field values applied on top of the copied last row.</param>
        public void InitializeRow(IReadOnlyDictionary<string, object?>? initial = null)
        {
            ThrowIfReadOnly();
            _pendingRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            _originalRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            _modifiedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var last = LoadLastRow();
            if (last is not null)
            {
                foreach (var kv in last)
                {
                    _originalRow[kv.Key] = kv.Value;
                    _pendingRow[kv.Key] = kv.Value;
                }
            }

            if (initial is not null)
            {
                foreach (var kv in initial)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key))
                        throw new DataBallException("Field name is required.");
                    _pendingRow[kv.Key] = DuckDbStore.Unwrap(kv.Value);
                    _modifiedFields.Add(kv.Key);
                }
            }

            _logger.LogDebug("Initialized pending row");
        }

        /// <summary>
        /// Sets a field on the pending row and marks it as explicitly modified.
        /// </summary>
        /// <param name="field">The field name.</param>
        /// <param name="value">The field value.</param>
        /// <exception cref="DataBallException">Thrown when no pending row exists or the field name is empty.</exception>
        public void ModifyField(string field, object? value)
        {
            ThrowIfReadOnly();
            if (_pendingRow is null || _modifiedFields is null)
                throw new DataBallException("No pending row. Call InitializeRow before ModifyField.");
            if (string.IsNullOrWhiteSpace(field))
                throw new DataBallException("Field name is required.");
            _pendingRow[field] = DuckDbStore.Unwrap(value);
            _modifiedFields.Add(field);
        }

        /// <summary>
        /// Applies configured relationships and commits the pending row to the store.
        /// </summary>
        /// <exception cref="DataBallException">Thrown when no pending row exists or the row is empty with no data table.</exception>
        public void CommitRow()
        {
            ThrowIfReadOnly();
            if (_pendingRow is null)
                throw new DataBallException("No pending row. Call InitializeRow before CommitRow.");

            ApplyRelationships();

            if (_pendingRow.Count == 0 && !_store.DataTableExists())
                throw new DataBallException("Cannot commit an empty row.");

            var addedTypes = EnsurePendingColumns();
            try
            {
                _store.AddRow(_pendingRow, _expectedColumnTypes);
            }
            catch
            {
                ForgetUnstoredColumnTypes(addedTypes);
                throw;
            }

            RememberRow(_pendingRow);
            ClearPending();
            _logger.LogDebug("Committed row");
            SplitIfLayout();
        }

        /// <summary>
        /// Applies each relationship in config order against the original snapshot.
        /// </summary>
        private void ApplyRelationships()
        {
            if (_pendingRow is null || _originalRow is null || _modifiedFields is null)
                return;

            foreach (var relationship in _relationships)
            {
                var trigger = relationship.TriggerField;
                if (string.IsNullOrWhiteSpace(trigger))
                    continue;

                var pendingHas = _pendingRow.ContainsKey(trigger);
                var originalHas = _originalRow.ContainsKey(trigger);
                var changed = pendingHas != originalHas
                    || (pendingHas && originalHas && !FieldEquals(_pendingRow[trigger], _originalRow[trigger]));
                if (!changed)
                    continue;

                foreach (var resetField in relationship.ResetFields)
                {
                    if (string.IsNullOrWhiteSpace(resetField) || _modifiedFields.Contains(resetField))
                        continue;
                    _pendingRow[resetField] = null;
                }
            }
        }

        /// <summary>Returns the column names whose expected type this call recorded.</summary>
        private List<string> EnsurePendingColumns()
        {
            var added = new List<string>();
            if (_pendingRow is null)
                return added;

            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_store.DataTableExists())
            {
                foreach (var (name, _) in _store.GetColumns())
                    existing.Add(name);
            }

            foreach (var kv in _pendingRow)
            {
                if (existing.Contains(kv.Key))
                    continue;

                Type clrType;
                if (_expectedColumnTypes.TryGetValue(kv.Key, out var expected))
                    clrType = expected;
                else if (kv.Value is not null)
                    clrType = InferClrType(kv.Value);
                else
                    clrType = typeof(string);

                _store.EnsureColumn(kv.Key, clrType);
                if (!_expectedColumnTypes.ContainsKey(kv.Key))
                {
                    _expectedColumnTypes[kv.Key] = clrType;
                    added.Add(kv.Key);
                }
                existing.Add(kv.Key);
            }

            return added;
        }

        /// <summary>
        /// After a failed commit, drops the expected types recorded for columns the store never
        /// got (a layout write rolls its new columns back), so a stale type does not constrain later writes.
        /// </summary>
        private void ForgetUnstoredColumnTypes(IReadOnlyList<string> added)
        {
            if (added.Count == 0)
                return;
            // Runs in a catch: a cleanup failure must not replace the commit's exception.
            try
            {
                var stored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (_store.DataTableExists())
                {
                    foreach (var (name, _) in _store.GetColumns())
                        stored.Add(name);
                }

                foreach (var name in added)
                {
                    if (!stored.Contains(name))
                        _expectedColumnTypes.Remove(name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not reconcile expected column types after a failed commit");
            }
        }

        private Dictionary<string, object?>? LoadLastRow()
        {
            if (_lastCommittedRow is not null)
                return new Dictionary<string, object?>(_lastCommittedRow, StringComparer.OrdinalIgnoreCase);
            if (!_store.DataTableExists() || _store.RowCount() == 0)
                return null;
            if (_store.Layout is not null)
            {
                var last = _store.Query(_store.Layout.BuildLastRowSelect());
                return last.Count == 0 ? null : last[0];
            }
            var rows = _store.Query($"SELECT * FROM {DuckDbStore.QuoteIdent("data")}");
            return rows.Count == 0 ? null : rows[rows.Count - 1];
        }

        private void RememberRow(IReadOnlyDictionary<string, object?> row)
        {
            _lastCommittedRow = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);
        }

        private void RememberAddedColumn(string name, object? lastValue)
        {
            if (_lastCommittedRow is null)
                return;
            _lastCommittedRow[name] = lastValue;
        }

        private void PruneLastCommittedRow()
        {
            if (_lastCommittedRow is null)
                return;
            if (!_store.DataTableExists())
            {
                _lastCommittedRow = null;
                return;
            }

            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, _) in _store.GetColumns())
                cols.Add(name);
            var keep = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _lastCommittedRow)
            {
                if (cols.Contains(pair.Key))
                    keep[pair.Key] = pair.Value;
            }

            _lastCommittedRow = keep.Count == 0 ? null : keep;
        }

        private void ClearPending()
        {
            _pendingRow = null;
            _originalRow = null;
            _modifiedFields = null;
        }

        private void ThrowIfPendingRow()
        {
            if (_pendingRow is not null)
                throw new DataBallException("Commit or discard the pending row first.");
        }

        private static Type InferClrType(object? value)
        {
            return DuckDbStore.Unwrap(value) switch
            {
                null => typeof(string),
                int => typeof(int),
                long => typeof(long),
                float => typeof(float),
                double => typeof(double),
                bool => typeof(bool),
                DateTime => typeof(DateTime),
                _ => typeof(string)
            };
        }

        private static bool FieldEquals(object? a, object? b)
        {
            a = DuckDbStore.Unwrap(a);
            b = DuckDbStore.Unwrap(b);
            if (a is null && b is null) return true;
            if (a is null || b is null) return false;
            if (a is bool || b is bool)
                return a is bool ab && b is bool bb && ab == bb;
            if (a is DateTime da && b is DateTime db) return da == db;
            if (a is string || b is string)
                return string.Equals(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.Ordinal);
            if (IsNumeric(a) && IsNumeric(b))
                return Convert.ToDecimal(a, CultureInfo.InvariantCulture) == Convert.ToDecimal(b, CultureInfo.InvariantCulture);
            return Equals(a, b);
        }

        private static bool IsNumeric(object value)
        {
            return Type.GetTypeCode(value.GetType()) switch
            {
                TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16
                    or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64
                    or TypeCode.Single or TypeCode.Double or TypeCode.Decimal => true,
                _ => false
            };
        }
    }
}
