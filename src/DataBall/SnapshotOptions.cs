// SPDX-License-Identifier: Apache-2.0
namespace squalor.DataBall;

/// <summary>Options for <see cref="DataBall.PublishSnapshotAsync"/>: where the generations go and how many to keep.</summary>
public sealed class SnapshotOptions
{
    /// <summary>The directory that holds the generations and the pointer. Created when missing.</summary>
    public required string Directory { get; init; }

    /// <summary>
    /// The base name: generations are <c>&lt;Name&gt;.&lt;n&gt;.ball</c> and the pointer is <c>&lt;Name&gt;.latest</c>.
    /// A bare file name, without a directory part.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>How many of the newest generations to keep; at least 1. Default 2.</summary>
    public int Keep { get; init; } = 2;

    /// <summary>
    /// Copy an external Parquet view into the snapshot. By default a view-backed session's snapshot links to the
    /// Parquet instead, because embedding re-reads the whole Parquet on every publish.
    /// </summary>
    public bool EmbedViews { get; init; }
}
