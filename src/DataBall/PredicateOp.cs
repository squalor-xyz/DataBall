namespace squalor.DataBall
{
    /// <summary>
    /// Comparison for a <see cref="ColumnPredicate"/>. <see cref="Range"/> is inclusive on both ends.
    /// </summary>
    public enum PredicateOp
    {
        Eq,
        In,
        Ge,
        Le,
        Range
    }
}
