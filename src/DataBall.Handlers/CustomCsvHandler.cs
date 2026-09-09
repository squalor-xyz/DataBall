using System.Runtime.CompilerServices;
using System.Text;

namespace squalor.DataBall.Handlers
{
    /// <summary>
    /// Primary lab dialect: unit-header CSV (<c>EVM(dB)</c>, Obfuscator semiconductor sweep).
    /// Translates via existing <see cref="DataBall.ImportAsync"/> (not a parser in DuckDbStore).
    /// Does not claim plain <c>Name,Age</c> CSV.
    /// </summary>
    public sealed class CustomCsvHandler : IFormatHandler
    {
        public bool CanHandle(string path, Stream sniff)
        {
            if (!EndsWith(path, ".csv"))
                return false;
            ArgumentNullException.ThrowIfNull(sniff);
            using var reader = new StreamReader(sniff, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            var header = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(header))
                return false;
            var config = Config.CreateDefaults();
            foreach (var raw in header.Split(','))
            {
                var (_, unit) = HeaderParser.Parse(raw.Trim(), config.Csv.HeaderPatterns, u => IsKnownUnit(config, u));
                if (unit is not null)
                    return true;
            }
            return false;
        }

        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Parse(
            string path,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var db = new DataBall();
            await db.ImportAsync(path);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var row in db.Query("SELECT * FROM data"))
                yield return row;
        }

        private static bool IsKnownUnit(Config config, string unit)
        {
            if (config.Units.ContainsKey(unit))
                return true;
            foreach (var spec in config.Units.Values)
            {
                if (spec.Aliases is null)
                    continue;
                foreach (var alias in spec.Aliases)
                {
                    if (string.Equals(alias, unit, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        private static bool EndsWith(string path, string suffix)
            => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }
}
