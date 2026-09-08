namespace squalor.DataBall
{
    /// <summary>
    /// One column predicate in a <see cref="SessionFilter"/>. Unknown columns throw at Filter/ApplyFilter.
    /// </summary>
    public sealed class ColumnPredicate
    {
        public required string Column { get; init; }

        public PredicateOp Op { get; init; }

        /// <summary>
        /// Eq/Ge/Le value, Range lower bound, or In list (<see cref="System.Collections.IEnumerable"/>, not a string).
        /// </summary>
        public object? Value { get; init; }

        /// <summary>
        /// Inclusive upper bound for <see cref="PredicateOp.Range"/>.
        /// </summary>
        public object? ValueTo { get; init; }
    }
}
