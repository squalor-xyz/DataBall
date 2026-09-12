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
            await foreach (var row in new CustomCsvHandler().Parse(Fixture(), Config.CreateDefaults()))
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
        public void Open_WithHandler_AppliesOverlaySchema()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-h15-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var csv = Path.Combine(dir, "press.csv");
                File.WriteAllText(csv, "Wafer,Press(Torr),EVM(dB)\nW1,1,2.5\n");
                var overlay = Path.Combine(dir, "overlay.json");
                File.WriteAllText(overlay, """{"units":{"Torr":{"type":"double"}},"metadataFieldsAdd":["Wafer"]}""");

                DataBall.RegisterHandler(new CustomCsvHandler());
                using var withHandler = DataBall.Open(csv, overlay);
                DataBall.ClearHandlers();
                using var withoutHandler = DataBall.Open(csv, overlay);

                AssertSameOverlayShape(withHandler, withoutHandler);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_WithHandler_ExtractsConfiguredMetadata()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-h15-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var csv = Path.Combine(dir, "lot.csv");
                File.WriteAllText(csv, "Lot,EVM(dB)\nL1,1.5\nL1,2.0\n");
                DataBall.RegisterHandler(new CustomCsvHandler());
                using var db = DataBall.Open(csv);
                Assert.Equal("L1", db.Metadata["Lot"]?.ToString());
                var cols = db.Query("SELECT * FROM data LIMIT 1")[0];
                Assert.False(cols.ContainsKey("Lot"));
                Assert.True(cols.ContainsKey("EVM"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
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

        private static void AssertSameOverlayShape(DataBall withHandler, DataBall withoutHandler)
        {
            var withRow = Assert.Single(withHandler.Query("SELECT * FROM data"));
            var withoutRow = Assert.Single(withoutHandler.Query("SELECT * FROM data"));
            Assert.Equal(withoutRow.Keys.OrderBy(k => k, StringComparer.Ordinal), withRow.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.False(withRow.ContainsKey("Wafer"));
            Assert.Equal("W1", withHandler.Metadata["Wafer"]?.ToString());
            Assert.Equal(withoutHandler.Metadata["Wafer"]?.ToString(), withHandler.Metadata["Wafer"]?.ToString());
            Assert.IsType<double>(withRow["Press"]);
            Assert.Equal(withoutRow["Press"]?.GetType(), withRow["Press"]?.GetType());
            Assert.IsType<double>(withRow["EVM"]);
            Assert.Equal(withoutRow["EVM"]?.GetType(), withRow["EVM"]?.GetType());
        }

        private static void Drain(IFormatHandler handler, string path)
        {
            handler.Parse(path, Config.CreateDefaults()).ToBlockingEnumerable().ToList();
        }

        private static string Fixture()
            => Path.Combine(AppContext.BaseDirectory, "fixtures", "semiconductor-sweep.csv");
    }
}
