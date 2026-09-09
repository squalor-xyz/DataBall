using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using squalor.DataBall.Handlers;
using Xunit;

namespace squalor.DataBall.Tests
{
    [Collection("DataBall.Handlers")]
    public class HandlerTests : IDisposable
    {
        public HandlerTests() => DataBall.ClearHandlers();

        public void Dispose() => DataBall.ClearHandlers();

        [Fact]
        public void CustomCsv_CanHandle_UnitHeaderCsv()
        {
            var csv = "EVM(dB),I_Total(A)\n1,0.1\n";
            using var sniff = new MemoryStream(Encoding.UTF8.GetBytes(csv));
            Assert.True(new CustomCsvHandler().CanHandle("meas.csv", sniff));
        }

        [Fact]
        public void CustomCsv_DoesNotHandle_PlainNameAgeCsv()
        {
            var csv = "Name,Age\nAlice,30\n";
            using var sniff = new MemoryStream(Encoding.UTF8.GetBytes(csv));
            Assert.False(new CustomCsvHandler().CanHandle("people.csv", sniff));
        }

        [Fact]
        public async Task CustomCsv_Parse_ObfuscatorFixture_RowCount81()
        {
            var rows = 0;
            await foreach (var row in new CustomCsvHandler().Parse(Fixture()))
            {
                rows++;
                if (rows == 1)
                {
                    Assert.True(row.ContainsKey("EVM"));
                    Assert.True(row.ContainsKey("sweep") || row.ContainsKey("stimulusGrp"));
                }
            }
            Assert.Equal(81, rows);
        }

        [Fact]
        public void Open_WithCustomCsvRegistered_SameRowCountAsGeneric()
        {
            DataBall.RegisterHandler(new CustomCsvHandler());
            using var db = DataBall.Open(Fixture());
            Assert.Equal(81, db.Query("SELECT * FROM data").Count);
        }

        [Fact]
        public void Stdf_CanHandle_StdfExtension_ParseThrowsUntilFixture()
        {
            var handler = new StdfHandler();
            using var sniff = new MemoryStream();
            Assert.True(handler.CanHandle("lot.stdf", sniff));
            Assert.True(handler.CanHandle("lot.std", sniff));
            Assert.False(handler.CanHandle("lot.csv", sniff));
            var ex = Assert.Throws<DataBallException>(() => Drain(handler, "lot.stdf"));
            Assert.Contains("STDF", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Touchstone_CanHandle_S2p_ParseThrowsUntilFixture()
        {
            var handler = new TouchstoneHandler();
            using var sniff = new MemoryStream();
            Assert.True(handler.CanHandle("dut.s2p", sniff));
            Assert.True(handler.CanHandle("dut.s1p", sniff));
            Assert.False(handler.CanHandle("dut.csv", sniff));
            var ex = Assert.Throws<DataBallException>(() => Drain(handler, "dut.s2p"));
            Assert.Contains("Touchstone", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Production_CanHandle_Prd_ParseThrowsUntilDialect()
        {
            var handler = new ProductionHandler();
            using var sniff = new MemoryStream();
            Assert.True(handler.CanHandle("wafer.prd", sniff));
            var ex = Assert.Throws<DataBallException>(() => Drain(handler, "wafer.prd"));
            Assert.Contains("Production", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        private static void Drain(IFormatHandler handler, string path)
        {
            handler.Parse(path).ToBlockingEnumerable().ToList();
        }

        private static string Fixture()
            => Path.Combine(AppContext.BaseDirectory, "fixtures", "semiconductor-sweep.csv");
    }
}
