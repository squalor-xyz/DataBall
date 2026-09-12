// SPDX-License-Identifier: Apache-2.0
using System;
using Xunit;

namespace squalor.DataBall.Tests
{
    public class HeaderParserTests
    {
        private static readonly Config Defaults = Config.CreateDefaults();

        [Theory]
        [InlineData("EVM(dB)", "EVM", "dB")]
        [InlineData("I_Total(A)", "I_Total", "A")]
        [InlineData("  Gain(dB)  ", "Gain", "dB")]
        public void ParensPattern_SplitsNameAndUnit(string header, string name, string unit)
        {
            var parsed = HeaderParser.Parse(header, Defaults.Csv.HeaderPatterns, KnownUnit);
            Assert.Equal(name, parsed.Name);
            Assert.Equal(unit, parsed.Unit);
        }

        [Fact]
        public void UnderscorePattern_SplitsOnlyWhenSuffixIsKnownUnit()
        {
            var parsed = HeaderParser.Parse("EVM_dB", Defaults.Csv.HeaderPatterns, KnownUnit);
            Assert.Equal("EVM", parsed.Name);
            Assert.Equal("dB", parsed.Unit);
        }

        [Fact]
        public void UnderscoreInName_WithoutKnownUnitSuffix_StaysWholeName()
        {
            var parsed = HeaderParser.Parse("I_Total", Defaults.Csv.HeaderPatterns, KnownUnit);
            Assert.Equal("I_Total", parsed.Name);
            Assert.Null(parsed.Unit);
        }

        [Fact]
        public void BareName_HasNoUnit()
        {
            var parsed = HeaderParser.Parse("Serial", Defaults.Csv.HeaderPatterns, KnownUnit);
            Assert.Equal("Serial", parsed.Name);
            Assert.Null(parsed.Unit);
        }

        [Fact]
        public void Resolve_EvmDb_IsDoubleMeas()
        {
            var col = SchemaResolver.Resolve("EVM(dB)", Defaults);
            Assert.Equal("EVM", col.Name);
            Assert.Equal("dB", col.Unit);
            Assert.Equal(typeof(double), col.ClrType);
            Assert.Equal(ParameterRole.Meas, col.Role);
        }

        [Fact]
        public void Resolve_ITotalAmpsAlias_IsDouble()
        {
            var col = SchemaResolver.Resolve("I_Total(Amps)", Defaults);
            Assert.Equal("I_Total", col.Name);
            Assert.Equal(typeof(double), col.ClrType);
        }

        [Fact]
        public void Resolve_IdUnit_IsInt64()
        {
            var col = SchemaResolver.Resolve("Part(id)", Defaults);
            Assert.Equal("Part", col.Name);
            Assert.Equal(typeof(long), col.ClrType);
        }

        [Fact]
        public void Resolve_Freq_IsStimulus()
        {
            var col = SchemaResolver.Resolve("Freq(Hz)", Defaults);
            Assert.Equal("Freq", col.Name);
            Assert.Equal(ParameterRole.Stimulus, col.Role);
            Assert.Equal(typeof(double), col.ClrType);
        }

        [Fact]
        public void Overlay_ColumnsTypeWinsOverUnit()
        {
            var overlay = new Config();
            overlay.Columns["EVM"] = "float";
            var merged = Config.Merge(Config.CreateDefaults(), overlay);
            var col = SchemaResolver.Resolve("EVM(dB)", merged);
            Assert.Equal(typeof(float), col.ClrType);
        }

        [Fact]
        public void Overlay_StimulusList_SetsRole()
        {
            var overlay = new Config();
            overlay.Stimulus.Add("Vbias");
            var merged = Config.Merge(Config.CreateDefaults(), overlay);
            Assert.Equal(ParameterRole.Stimulus, merged.ResolveRole("Vbias"));
        }

        [Fact]
        public void DuplicateCanonicalNames_Throw()
        {
            var ex = Assert.Throws<DataBallException>(() =>
                SchemaResolver.ResolveAll(new[] { "EVM(dB)", "EVM_dB" }, Defaults));
            Assert.Contains("Duplicate canonical column 'EVM'", ex.Message, StringComparison.Ordinal);
        }

        private static bool KnownUnit(string unit) => Defaults.TryResolveUnitType(unit) is not null;
    }
}
