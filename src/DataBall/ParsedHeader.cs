using System;

namespace squalor.DataBall
{
    /// <summary>
    /// A CSV (or other) header after pattern parse and schema resolve.
    /// </summary>
    public sealed class ParsedHeader
    {
        public ParsedHeader(string raw, string name, string? unit, string role, Type? clrType)
        {
            Raw = raw;
            Name = name;
            Unit = unit;
            Role = role;
            ClrType = clrType;
        }

        public string Raw { get; }
        public string Name { get; }
        public string? Unit { get; }
        public string Role { get; }
        public Type? ClrType { get; }
    }
}
