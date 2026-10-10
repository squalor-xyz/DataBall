// SPDX-License-Identifier: Apache-2.0
namespace squalor.DataBall;

/// <summary>Options for saving a native .ball session.</summary>
public sealed class SaveOptions
{
    /// <summary>Preserve an external Parquet view and its absolute paths instead of embedding data.</summary>
    public bool Link { get; init; }
}
