// SPDX-License-Identifier: Apache-2.0
using System.Collections.Generic;

namespace squalor.DataBall
{
    /// <summary>What an open did with its inputs: the sources it opened, the files it left alone, and the files it skipped.</summary>
    public sealed class OpenReport
    {
        internal OpenReport(IReadOnlyList<OpenedSource> sources, IReadOnlyList<string> ignored, IReadOnlyList<SkippedFile> skipped)
        {
            Sources = sources;
            Ignored = ignored;
            Skipped = skipped;
        }

        /// <summary>The sources the session holds, in the order they were opened (the order of <see cref="DataBall.Sources"/>).</summary>
        public IReadOnlyList<OpenedSource> Sources { get; }

        /// <summary>
        /// Full paths of files that came from a directory or a glob and were not recognized as any format, or are stray (a name or a directory in the path starts with <c>.</c> or <c>_</c>). An explicitly
        /// named file that is not recognized fails the open instead, or is listed in <see cref="Skipped"/> under <see cref="BadFilePolicy.Skip"/>.
        /// </summary>
        public IReadOnlyList<string> Ignored { get; }

        /// <summary>
        /// The files an open skipped under <see cref="BadFilePolicy.Skip"/>, in the order the open met them. Empty under <see cref="BadFilePolicy.Abort"/>,
        /// where a bad file fails the open.
        /// </summary>
        public IReadOnlyList<SkippedFile> Skipped { get; }
    }

    /// <summary>One source an open read: the session's record of it, and how many rows it contributed.</summary>
    /// <param name="Source">The session's <see cref="SourceInfo"/> for the file, as <see cref="DataBall.Sources"/> lists it.</param>
    /// <param name="Rows">The number of rows the source contributed.</param>
    public sealed record OpenedSource(SourceInfo Source, long Rows);

    /// <summary>
    /// A file that a skipping open left out: its full path (or <c>&lt;archive&gt;!&lt;entry&gt;</c> for one entry of an archive), and why.
    /// </summary>
    /// <param name="Path">The full path of the file, or <c>&lt;archive&gt;!&lt;entry&gt;</c> for an archive entry.</param>
    /// <param name="Message">Why the file was skipped.</param>
    /// <param name="Row">The row where the failure happened, when the error carries it; otherwise null.</param>
    /// <param name="Column">The column where the failure happened, when the error carries it; otherwise null.</param>
    /// <param name="Value">The value that failed, when the error carries it; otherwise null.</param>
    public sealed record SkippedFile(string Path, string Message, long? Row, string? Column, string? Value);
}
