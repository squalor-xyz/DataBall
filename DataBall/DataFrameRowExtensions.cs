using System.Text;
using Microsoft.Data.Analysis;

namespace squalor.DataBall
{
    /// <summary>
    /// Provides extension methods for <see cref="DataFrameRow"/> to support DataBall operations.
    /// </summary>
    public static class DataFrameRowExtensions
    {
        /// <summary>
        /// Generates a string key from a DataFrameRow for deduplication purposes.
        /// </summary>
        /// <param name="row">The DataFrameRow to convert.</param>
        /// <returns>A string key representing the row values, with values separated by '|'.</returns>
        public static string ToStringKey(this DataFrameRow row)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < row.Length; i++)
            {
                sb.Append(row[i]?.ToString() ?? "");
                sb.Append("|");
            }
            return sb.ToString();
        }
    }
}