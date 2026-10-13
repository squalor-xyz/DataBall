// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace squalor.DataBall
{
    /// <summary>The metadata policy of a config, parsed once.</summary>
    internal enum MetadataPolicyKind
    {
        /// <summary>A constant field goes to Metadata (the default).</summary>
        LiftConstant,

        /// <summary>Like <see cref="LiftConstant"/>, but a field that varies within a source is an error.</summary>
        RequireConstant,

        /// <summary>The first value found goes to Metadata and the column leaves the rows.</summary>
        First,

        /// <summary>Like <see cref="LiftConstant"/>, but a source with an empty cell is not constant.</summary>
        Bounce,
    }

    /// <summary>What the session already has under a configured metadata field's name, or why the planner may not touch the field.</summary>
    internal enum FieldHome
    {
        /// <summary>Nothing: the field is new.</summary>
        Fresh,

        /// <summary>A value in Metadata (an append compares against it).</summary>
        Stored,

        /// <summary>A column of <c>"data"</c> (an append keeps it a column).</summary>
        SessionColumn,

        /// <summary>A column of the <c>source</c> table (an append makes it a row column, never Metadata).</summary>
        PerSource,

        /// <summary>A declared table key: the split needs it as data.</summary>
        LayoutKey,

        /// <summary>Lineage: <c>_source_key</c>.</summary>
        Reserved,

        /// <summary>A name the <c>source</c> table's built-in columns use (<c>path</c>, <c>type</c>, …): the field may reach Metadata, but never a column of <c>source</c>.</summary>
        BuiltInSourceName,
    }

    /// <summary>Where a configured metadata field ends up.</summary>
    internal enum MetadataLevel
    {
        /// <summary>The planner does not decide this field here; whatever the session has stays.</summary>
        Leave,

        /// <summary>The field varies, or has no value: it stays a column of <c>"data"</c> for every row.</summary>
        RowColumn,

        /// <summary>One value in Metadata; the column leaves the rows (an append leaves it for the stored-value comparison).</summary>
        Metadata,

        /// <summary>A value per source in a column of the <c>source</c> table; the column leaves the rows.</summary>
        SourceColumn,

        /// <summary>An append: the stored value stands; the incoming column is dropped.</summary>
        Drop,

        /// <summary>An append: the incoming values differ from the stored one; the field becomes a row column and the stored value backfills the old rows.</summary>
        Demote,
    }

    /// <summary>What a column of one source holds: its profile and the rows of the source (only <c>bounce</c> reads the rows).</summary>
    internal readonly record struct FieldFacts(ColumnProfile Profile, long Rows);

    /// <summary>A source of a many-file open that holds the field.</summary>
    internal readonly record struct OpenHolder(int Index, bool FromHandler, FieldFacts Facts);

    /// <summary>
    /// The decision for one field. <see cref="Value"/> is the single value of <see cref="MetadataLevel.Metadata"/> unless
    /// <see cref="FirstNonNull"/> says the caller reads the first non-null value. <see cref="Values"/> is the (source index, value)
    /// pairs of <see cref="MetadataLevel.SourceColumn"/>, one per source that has a value.
    /// </summary>
    internal readonly record struct MetadataDecision(
        MetadataLevel Level,
        object? Value = null,
        bool FirstNonNull = false,
        IReadOnlyList<(int Index, object? Value)>? Values = null);

    /// <summary>
    /// The one place that decides a configured metadata field's level, for the many-file open, the wide import and append staging, and the
    /// append against a stored value (wide or layout). Callers gather the facts (profiles, stored state) and execute the decision; nothing
    /// here touches the store.
    /// </summary>
    internal static class MetadataPlanner
    {
        /// <summary>The policy named by a config's <c>metadataPolicy</c>; null or an unknown name is <see cref="MetadataPolicyKind.LiftConstant"/>.</summary>
        internal static MetadataPolicyKind ParsePolicy(string? policy)
        {
            if (string.Equals(policy, "requireConstant", StringComparison.OrdinalIgnoreCase))
                return MetadataPolicyKind.RequireConstant;
            if (string.Equals(policy, "first", StringComparison.OrdinalIgnoreCase))
                return MetadataPolicyKind.First;
            if (string.Equals(policy, "bounce", StringComparison.OrdinalIgnoreCase))
                return MetadataPolicyKind.Bounce;
            return MetadataPolicyKind.LiftConstant;
        }

        /// <summary>
        /// A field of one table that is staged for a wide import or an append. <paramref name="facts"/> is asked only when the decision needs it.
        /// A new field is decided as a many-file open decides it for a single source (<see cref="PlanOpen"/>). A stored or session-column field
        /// is left to the append (it compares against the stored value, or keeps the column), though <c>requireConstant</c> still checks the
        /// field within this source.
        /// </summary>
        internal static MetadataDecision PlanStaged(MetadataPolicyKind policy, FieldHome home, string field, Func<FieldFacts> facts)
        {
            ArgumentNullException.ThrowIfNull(facts);
            switch (home)
            {
                case FieldHome.Fresh or FieldHome.BuiltInSourceName:
                    if (policy == MetadataPolicyKind.First)
                        return new MetadataDecision(MetadataLevel.Metadata, FirstNonNull: true);
                    return PlanOpen(policy, home, field, new[] { new OpenHolder(0, false, facts()) }, 1);
                case FieldHome.Stored or FieldHome.SessionColumn:
                    if (policy == MetadataPolicyKind.RequireConstant && Rejects(policy, facts()))
                        throw NotConstant(field);
                    return new MetadataDecision(MetadataLevel.Leave);
                default:
                    return new MetadataDecision(MetadataLevel.Leave);
            }
        }

        /// <summary>
        /// A field across the staged sources of a many-file open; <paramref name="holders"/> are the sources that hold it, and a handler's
        /// source leaves its fields as they are. One value in every source goes to Metadata; a value in each source that has one, but not the
        /// same everywhere (or absent from some), is a column of the <c>source</c> table, unless the field is named like a built-in column of
        /// that table, when it stays a row column; a field that varies within a source stays a row column (or throws under
        /// <c>requireConstant</c>).
        /// </summary>
        internal static MetadataDecision PlanOpen(MetadataPolicyKind policy, FieldHome home, string field, IReadOnlyList<OpenHolder> holders, int sourceCount)
        {
            ArgumentNullException.ThrowIfNull(holders);
            if (home is not (FieldHome.Fresh or FieldHome.BuiltInSourceName) || holders.Count == 0 || holders.Any(h => h.FromHandler))
                return new MetadataDecision(MetadataLevel.Leave);
            if (policy == MetadataPolicyKind.First)
                return new MetadataDecision(MetadataLevel.Metadata, FirstNonNull: true);

            var constants = new List<(int Index, object? Value)>();
            foreach (var holder in holders)
            {
                var f = holder.Facts;
                if (Rejects(policy, f))
                    throw NotConstant(field);
                if (f.Profile.Distinct > 1)
                    return new MetadataDecision(MetadataLevel.RowColumn);
                if (f.Profile.Distinct == 0)
                    continue; // all empty: no value
                if (!IsConstant(policy, f))
                    return new MetadataDecision(MetadataLevel.RowColumn); // bounce lifts only a column without empty cells
                constants.Add((holder.Index, f.Profile.Value));
            }
            if (constants.Count == 0)
                return new MetadataDecision(MetadataLevel.RowColumn);
            if (constants.Count == sourceCount && constants.All(c => SameValue(c.Value, constants[0].Value)))
                return new MetadataDecision(MetadataLevel.Metadata, constants[0].Value);
            return home == FieldHome.BuiltInSourceName
                ? new MetadataDecision(MetadataLevel.RowColumn)
                : new MetadataDecision(MetadataLevel.SourceColumn, Values: constants);
        }

        /// <summary>
        /// A field with a stored value, met again by an append (wide or layout). <paramref name="differs"/> is asked only when the policy lets
        /// the incoming values win: the stored value stands under <c>first</c>. Equal values drop the incoming column; differing values demote
        /// the field to a row column. A field that is not stored (a session column, a protected field) is left.
        /// </summary>
        internal static MetadataDecision PlanAgainstStored(MetadataPolicyKind policy, FieldHome home, Func<bool> differs)
        {
            ArgumentNullException.ThrowIfNull(differs);
            if (home != FieldHome.Stored)
                return new MetadataDecision(MetadataLevel.Leave);
            if (policy == MetadataPolicyKind.First || !differs())
                return new MetadataDecision(MetadataLevel.Drop);
            return new MetadataDecision(MetadataLevel.Demote);
        }

        /// <summary>One value, and under <c>bounce</c> no empty cell.</summary>
        private static bool IsConstant(MetadataPolicyKind policy, FieldFacts f) =>
            f.Profile.Distinct == 1 && (policy != MetadataPolicyKind.Bounce || f.Profile.NonNull >= f.Rows);

        private static DataBallException NotConstant(string field) => new($"Metadata field '{field}' is not constant");

        /// <summary>True when the policy refuses this field: it varies within its source under <c>requireConstant</c>.</summary>
        internal static bool Rejects(MetadataPolicyKind policy, FieldFacts f) => policy == MetadataPolicyKind.RequireConstant && f.Profile.Distinct > 1;

        /// <summary>Equality of two values read from staging, numerically for numbers of different widths.</summary>
        internal static bool SameValue(object? a, object? b)
        {
            if (a is null || b is null)
                return a is null && b is null;
            static bool Integral(object v) => v is sbyte or byte or short or ushort or int or uint or long;
            static bool Number(object v) => Integral(v) || v is float or double or decimal;
            if (Number(a) && Number(b))
            {
                return Integral(a) && Integral(b)
                    ? Convert.ToInt64(a, CultureInfo.InvariantCulture) == Convert.ToInt64(b, CultureInfo.InvariantCulture)
                    : Convert.ToDouble(a, CultureInfo.InvariantCulture) == Convert.ToDouble(b, CultureInfo.InvariantCulture);
            }
            return a.Equals(b);
        }
    }
}
