// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using Xunit;

namespace squalor.DataBall.Tests
{
    public class ConfigTests
    {
        [Fact]
        public void LoadConfig_MissingFile_Throws()
        {
            var path = Path.Combine(Path.GetTempPath(), "databall-missing-config-" + Guid.NewGuid().ToString("N") + ".json");
            var ex = Assert.Throws<DataBallException>(() => Config.LoadConfig(path));
            Assert.Equal("Failed to load config", ex.Message);
        }

        [Fact]
        public void LoadConfig_InvalidJson_Throws()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "config.json");
                File.WriteAllText(path, "{ this is not json }");
                var ex = Assert.Throws<DataBallException>(() => Config.LoadConfig(path));
                Assert.Equal("Failed to load config", ex.Message);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ParseColumnType_Int32Alias()
        {
            Assert.Equal(typeof(int), Config.ParseColumnType("int32"));
        }

        [Fact]
        public void ParseColumnType_BooleanAlias()
        {
            Assert.Equal(typeof(bool), Config.ParseColumnType("boolean"));
        }

        [Fact]
        public void ParseColumnType_DateTimeAlias()
        {
            Assert.Equal(typeof(DateTime), Config.ParseColumnType("datetime"));
        }

        [Fact]
        public void ParseColumnType_Blank_Throws()
        {
            var ex = Assert.Throws<DataBallException>(() => Config.ParseColumnType(""));
            Assert.Equal("Unknown column type ''", ex.Message);
        }

        [Fact]
        public void ParseColumnType_Unknown_Throws()
        {
            var ex = Assert.Throws<DataBallException>(() => Config.ParseColumnType("nope"));
            Assert.Equal("Unknown column type 'nope'", ex.Message);
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-t2-config", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
