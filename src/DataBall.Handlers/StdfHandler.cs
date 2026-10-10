// SPDX-License-Identifier: Apache-2.0
using System.Runtime.CompilerServices;

namespace squalor.DataBall.Handlers
{
    /// <summary>
    /// STDF translator slot. CanHandle by extension; Parse waits on a named golden file.
    /// </summary>
    public sealed class StdfHandler : IFormatHandler
    {
        public bool CanHandle(string path, Stream sniff)
            => EndsWith(path, ".stdf") || EndsWith(path, ".std");

        /// <summary>Scores the bounded probe by the supported extension.</summary>
        public DetectionScore Score(FileProbe probe)
        {
            var matched = EndsWith(probe.Path, ".stdf") || EndsWith(probe.Path, ".std");
            return new DetectionScore(matched ? 1 : 0, new[] { matched ? "extension matched" : "extension missed" });
        }

        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Parse(
            string path,
            Config schema,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = schema;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            throw new DataBallException(
                "STDF translation is not implemented until a golden file is named: " + path);
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        private static bool EndsWith(string path, string suffix)
            => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }
}
