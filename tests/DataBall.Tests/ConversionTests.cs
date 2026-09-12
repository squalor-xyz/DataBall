using System.Text.Json;
using Xunit;

namespace squalor.DataBall.Tests
{
    public class ConversionTests
    {
        [Theory]
        [InlineData("INTEGER", typeof(int))]
        [InlineData("INT", typeof(int))]
        [InlineData("BIGINT", typeof(long))]
        [InlineData("DOUBLE", typeof(double))]
        [InlineData("FLOAT", typeof(float))]
        [InlineData("REAL", typeof(float))]
        [InlineData("BOOLEAN", typeof(bool))]
        [InlineData("VARCHAR", typeof(string))]
        [InlineData("TIMESTAMP", typeof(DateTime))]
        [InlineData("DATE", typeof(DateTime))]
        public void FromDuckDbType_KnownTypes(string duck, Type clr)
        {
            Assert.Equal(clr, DuckDbStore.FromDuckDbType(duck));
        }

        [Theory]
        [InlineData("HUGEINT")]
        [InlineData("DECIMAL(38,9)")]
        public void FromDuckDbType_Unsupported_Throws(string duck)
        {
            Assert.Throws<DataBallException>(() => DuckDbStore.FromDuckDbType(duck));
        }

        [Fact]
        public void IsDateType_DateOnly()
        {
            Assert.True(DuckDbStore.IsDateType("DATE"));
            Assert.False(DuckDbStore.IsDateType("TIMESTAMP"));
            Assert.False(DuckDbStore.IsDateType("INTEGER"));
        }

        [Fact]
        public void NormalizeType_CollapsesAliases()
        {
            Assert.Equal("INTEGER", DuckDbStore.NormalizeType("INT32"));
            Assert.Equal("BIGINT", DuckDbStore.NormalizeType("INT64"));
            Assert.Equal("TIMESTAMP", DuckDbStore.NormalizeType("TIMESTAMP_NS"));
        }

        [Fact]
        public void Coerce_FractionalToIntegerColumn_RoundsWithChangeType()
        {
            Assert.Equal(3, DuckDbStore.Coerce(2.7, typeof(int)));
            Assert.Equal(2, DuckDbStore.Coerce(2.5, typeof(int)));
            Assert.Throws<DataBallException>(() => DuckDbStore.Coerce(1e20, typeof(int)));
        }

        [Fact]
        public void UnwrapJson_Number_PreservesWidth()
        {
            var i32 = JsonSerializer.Deserialize<JsonElement>("4");
            Assert.IsType<int>(DuckDbStore.UnwrapJson(i32));
            var i64 = JsonSerializer.Deserialize<JsonElement>("3000000000");
            Assert.IsType<long>(DuckDbStore.UnwrapJson(i64));
            var tagged = JsonSerializer.Deserialize<JsonElement>("{\"t\":\"i64\",\"v\":5}");
            Assert.Equal(5L, Assert.IsType<long>(DuckDbStore.DeserializeMetadataValue(tagged)));
        }
    }
}
