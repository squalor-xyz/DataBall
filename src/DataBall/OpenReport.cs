// SPDX-License-Identifier: Apache-2.0
using System.Collections.Generic;

namespace squalor.DataBall
{
    /// <summary>What an open did with its inputs: the sources it opened, and the files it left alone.</summary>
    public sealed class OpenReport
    {
        internal OpenReport(IReadOnlyList<OpenedSource> sources, IReadOnlyList<string> ignored)
        {
            Sources = sources;
            Ignored = ignored;
        }

        /// <summary>The sources the session holds, in the order they were opened (the order of <see cref="DataBall.Sources"/>).</summary>
        public IReadOnlyList<OpenedSource> Sources { get; }

        /// <summary>
        /// Full paths of files that came from a directory or a glob and were not recognized as any format. An explicitly
        /// named file that is not recognized fails the open instead.
        /// </summary>
        public IReadOnlyList<string> Ignored { get; }
    }

    /// <summary>One source an open read: the session's record of it, and how many rows it contributed.</summary>
    /// <param name="Source">The session's <see cref="SourceInfo"/> for the file, as <see cref="DataBall.Sources"/> lists it.</param>
    /// <param name="Rows">The number of rows the source contributed.</param>
    public sealed record OpenedSource(SourceInfo Source, long Rows);
}
