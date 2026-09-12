// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using Xunit;

namespace squalor.DataBall.Tests
{
    public class QuotingTests
    {
        [Theory]
        [InlineData("Freq", "\"Freq\"")]
        [InlineData("a\"b", "\"a\"\"b\"")]
        [InlineData("x'; DROP TABLE data; --", "\"x'; DROP TABLE data; --\"")]
        [InlineData("col--comment", "\"col--comment\"")]
        [InlineData("café", "\"café\"")]
        public void QuoteIdent_DoublesEmbeddedQuotes(string name, string quoted)
        {
            Assert.Equal(quoted, DuckDbStore.QuoteIdent(name));
        }

        [Fact]
        public void QuoteIdent_NewlinesAndNulAreLiteralInsideQuotes()
        {
            Assert.Equal("\"a\nb\"", DuckDbStore.QuoteIdent("a\nb"));
            Assert.Equal("\"a\0b\"", DuckDbStore.QuoteIdent("a\0b"));
        }

        [Theory]
        [InlineData("hello", "'hello'")]
        [InlineData("O'Brien", "'O''Brien'")]
        [InlineData("a\\b", "'a\\b'")]
        [InlineData("'; DROP TABLE data; --", "'''; DROP TABLE data; --'")]
        public void QuoteString_DoublesEmbeddedApostrophes(string value, string quoted)
        {
            Assert.Equal(quoted, DuckDbStore.QuoteString(value));
        }

        [Fact]
        public void Filter_AdversarialColumnName_DoesNotDropDataTable()
        {
            var name = "x\"; DROP TABLE \"data\"; --";
            using var db = new DataBall();
            db.AddColumn(name, new[] { "ok" });
            var rows = db.Filter(new SessionFilter
            {
                Predicates =
                [
                    new ColumnPredicate { Column = name, Op = PredicateOp.Eq, Value = "ok" }
                ]
            });
            Assert.Single(rows);
            Assert.Equal("ok", rows[0][name]);
            var tables = db.Query("""
                SELECT COUNT(*) AS c FROM information_schema.tables
                WHERE table_schema = 'main' AND table_name = 'data'
                """);
            Assert.Equal(1L, Convert.ToInt64(tables[0]["c"]));
        }
    }
}
