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

        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Parse(
            string path,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
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
