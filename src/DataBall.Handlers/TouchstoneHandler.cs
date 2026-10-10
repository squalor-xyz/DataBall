// SPDX-License-Identifier: Apache-2.0
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace squalor.DataBall.Handlers
{
    /// <summary>
    /// Touchstone translator slot (<c>.s1p</c>, <c>.s2p</c>, …). Parse waits on a golden file.
    /// </summary>
    public sealed class TouchstoneHandler : IFormatHandler
    {
        private static readonly Regex SnP = new(@"\.s\d+p$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        public bool CanHandle(string path, Stream sniff)
            => SnP.IsMatch(path);

        /// <summary>Scores the bounded probe by the supported extension.</summary>
        public DetectionScore Score(FileProbe probe)
        {
            var matched = SnP.IsMatch(probe.FileName);
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
                "Touchstone translation is not implemented until a golden file is named: " + path);
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
