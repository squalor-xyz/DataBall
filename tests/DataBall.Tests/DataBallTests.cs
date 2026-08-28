using Xunit;

namespace squalor.DataBall.Tests
{
    /// <summary>
    /// Contains unit tests for the <see cref="DataBall"/> class.
    /// </summary>
    public class DataBallTests
    {
        /// <summary>
        /// Tests adding columns to a DataBall instance.
        /// </summary>
        [Fact]
        public void TestAddColumn()
        {
            var db = new DataBall();
            db.AddColumn("Name", new[] { "Alice", "Bob" });
            db.AddColumn<int>("Age", new[] { 30, 25 });
            Assert.Equal(2, db.DataFrame.Rows.Count);
            Assert.Equal("Alice", db.DataFrame["Name"][0]);
            Assert.Equal(30, db.DataFrame["Age"][0]);
        }

        /// <summary>
        /// Tests adding a single row with string and integer columns.
        /// </summary>
        [Fact]
        public void TestAnother()
        {
            var db = new DataBall();
            db.AddColumn("Name", new[] { "Charlie" });
            db.AddColumn<int>("Age", new[] { 40 });
            Assert.Equal(1, db.DataFrame.Rows.Count);
            Assert.Equal("Charlie", db.DataFrame["Name"][0]);
            Assert.Equal(40, db.DataFrame["Age"][0]);
        }
    }
}
