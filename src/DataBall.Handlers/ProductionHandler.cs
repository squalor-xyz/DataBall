// SPDX-License-Identifier: Apache-2.0
using System.Runtime.CompilerServices;

namespace squalor.DataBall.Handlers
{
    /// <summary>
    /// Production/ATE translator slot (<c>.prd</c>). Parse waits on a named dialect.
    /// </summary>
    public sealed class ProductionHandler : IFormatHandler
    {
        public bool CanHandle(string path, Stream sniff)
            => path.EndsWith(".prd", StringComparison.OrdinalIgnoreCase);

        /// <summary>Scores the bounded probe by the supported extension.</summary>
        public DetectionScore Score(FileProbe probe)
        {
            var matched = probe.Extension.Equals(".prd", StringComparison.OrdinalIgnoreCase);
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
                "Production translation is not implemented until a dialect is named: " + path);
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
