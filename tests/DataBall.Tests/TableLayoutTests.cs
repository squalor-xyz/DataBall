// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace squalor.DataBall.Tests
{
    /// <summary>
    /// Pure layout resolution: config + wide columns → tables. No DuckDB.
    /// </summary>
    public class TableLayoutTests
    {
        private static readonly string[] Wide = { "id", "SN", "Site", "Temp", "Pout", "EVM", "I_Total", "Bin" };

        [Theory]
        [InlineData("data")]
        [InlineData("meta")]
        [InlineData("_hidden")]
        public void Layout_ReservedName_Throws(string name)
        {
            var ex = Assert.Throws<DataBallException>(() =>
            {
                var config = Load($$$"""{ "tables": { "{{{name}}}": { "kind": "measurements", "columns": ["EVM"] } } }""");
                TableLayout.Validate(config);
            });
            Assert.Contains("reserved", ex.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Layout_UnknownKind_Throws()
        {
            var config = Load("""{ "tables": { "x": { "kind": "fact", "columns": ["EVM"] } } }""");
            var ex = Assert.Throws<DataBallException>(() => TableLayout.Validate(config));
            Assert.Contains("kind", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Layout_TwoRowsTables_Throws()
        {
            var config = Load("""{ "tables": { "a": { "kind": "rows" }, "b": { "kind": "rows" } } }""");
            var ex = Assert.Throws<DataBallException>(() => TableLayout.Validate(config));
            Assert.Contains("rows", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Layout_TwoMasters_Throws()
        {
            var config = Load("""{ "tables": { "a": { "kind": "master", "columns": ["SN"] }, "b": { "kind": "master", "columns": ["Site"] } } }""");
            var ex = Assert.Throws<DataBallException>(() => TableLayout.Validate(config));
            Assert.Contains("master", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Layout_Parent_NotSupportedYet_Throws()
        {
            var config = Load("""{ "tables": { "a": { "kind": "dimension", "columns": ["Site"], "parent": "b" }, "b": { "kind": "master", "columns": ["SN"] } } }""");
            var ex = Assert.Throws<DataBallException>(() => TableLayout.Validate(config));
            Assert.Contains("parent", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Layout_DuplicateExplicitClaim_Throws()
        {
            var config = Load("""{ "tables": { "a": { "kind": "measurements", "columns": ["EVM"] }, "b": { "kind": "measurements", "columns": ["evm"] } } }""");
            var ex = Assert.Throws<DataBallException>(() => TableLayout.Validate(config));
            Assert.Contains("claimed by both", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Layout_DuplicateRoleSelector_Throws()
        {
            var config = Load("""{ "tables": { "a": { "kind": "measurements", "roles": ["meas"] }, "b": { "kind": "measurements", "roles": ["Meas"] } } }""");
            var ex = Assert.Throws<DataBallException>(() => TableLayout.Validate(config));
            Assert.Contains("role", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Layout_KeyOnMeasurements_Throws()
        {
            var config = Load("""{ "tables": { "a": { "kind": "measurements", "columns": ["EVM"], "key": ["EVM"] } } }""");
            Assert.Throws<DataBallException>(() => TableLayout.Validate(config));
        }

        [Fact]
        public void Layout_ExplicitClaimBeatsRoleSelector()
        {
            var config = Load("""
                {
                  "stimulus": ["Temp"],
                  "tables": {
                    "setup": { "kind": "dimension", "columns": ["Site", "Temp"] },
                    "stim":  { "kind": "measurements", "roles": ["stimulus"] }
                  }
                }
                """);
            var layout = TableLayout.Build(config, Wide);
            Assert.Equal("setup", layout.TableFor("Temp"));
            Assert.DoesNotContain(layout.MeasurementGroups, g => g.Name == "stim");
        }

        [Fact]
        public void Layout_RoleSelector_BindsUnclaimedColumns()
        {
            var config = Load("""
                {
                  "stimulus": ["Temp", "Pout"],
                  "classification": ["Bin"],
                  "tables": {
                    "stim": { "kind": "measurements", "roles": ["stimulus"] },
                    "cls":  { "kind": "measurements", "roles": ["classification"] }
                  }
                }
                """);
            var layout = TableLayout.Build(config, Wide);
            Assert.Equal("stim", layout.TableFor("Temp"));
            Assert.Equal("stim", layout.TableFor("Pout"));
            Assert.Equal("cls", layout.TableFor("Bin"));
            Assert.Equal(TableLayout.ImpliedSpineName, layout.TableFor("EVM"));
        }

        [Fact]
        public void Layout_UnclaimedColumnsFallToSpine_ImpliedName()
        {
            var config = Load("""{ "tables": { "rf": { "kind": "measurements", "columns": ["Pout", "EVM"] } } }""");
            var layout = TableLayout.Build(config, Wide);
            Assert.Equal(TableLayout.ImpliedSpineName, layout.Spine.Name);
            Assert.Equal(new[] { "id", "SN", "Site", "Temp", "I_Total", "Bin" }, layout.Spine.Columns);
            var rf = Assert.Single(layout.MeasurementGroups);
            Assert.Equal(new[] { "Pout", "EVM" }, rf.Columns);
        }

        [Fact]
        public void Layout_DeclaredSpine_KeepsItsName()
        {
            var config = Load("""{ "tables": { "sweep": { "kind": "rows" }, "rf": { "kind": "measurements", "columns": ["EVM"] } } }""");
            var layout = TableLayout.Build(config, Wide);
            Assert.Equal("sweep", layout.Spine.Name);
            Assert.Equal(new[] { "rf", "sweep" }.OrderBy(n => n), layout.PhysicalTableNames.OrderBy(n => n));
        }

        [Fact]
        public void Layout_ImpliedSpineName_TakenByDimension_Throws()
        {
            var config = Load("""{ "tables": { "rows": { "kind": "dimension", "columns": ["Site"] } } }""");
            Assert.Throws<DataBallException>(() => TableLayout.Validate(config));
        }

        [Fact]
        public void Layout_TablesWithNoMatchingColumns_AreOmitted()
        {
            var config = Load("""
                {
                  "tables": {
                    "device": { "kind": "master", "columns": ["Lot"] },
                    "rf":     { "kind": "measurements", "columns": ["EVM"] },
                    "opt":    { "kind": "measurements", "columns": ["NotThere"] }
                  }
                }
                """);
            var layout = TableLayout.Build(config, Wide);
            Assert.Empty(layout.Dimensions);
            Assert.Equal("rf", Assert.Single(layout.MeasurementGroups).Name);
        }

        [Fact]
        public void Layout_DimensionKey_DefaultsToAllColumns_OrSubset()
        {
            var config = Load("""
                {
                  "tables": {
                    "device": { "kind": "master", "columns": ["SN", "Site"], "key": ["sn"] },
                    "setup":  { "kind": "dimension", "columns": ["Temp"] }
                  }
                }
                """);
            var layout = TableLayout.Build(config, Wide);
            var device = layout.Dimensions.Single(d => d.Name == "device");
            Assert.Equal(new[] { "SN" }, device.KeyColumns);
            Assert.Equal("device_key", device.KeyColumnName);
            var setup = layout.Dimensions.Single(d => d.Name == "setup");
            Assert.Equal(new[] { "Temp" }, setup.KeyColumns);
            Assert.Equal("CAST(hash(\"SN\") >> 1 AS BIGINT)", TableLayout.KeyExpression(device));
        }

        [Fact]
        public void Layout_ColumnCollidesWithRowKeyOrSurrogateKey_Throws()
        {
            var config = Load("""{ "tables": { "setup": { "kind": "dimension", "columns": ["Temp"] } } }""");
            Assert.Throws<DataBallException>(() => TableLayout.Build(config, new[] { "_row", "Temp" }));
            Assert.Throws<DataBallException>(() => TableLayout.Build(config, new[] { "setup_key", "Temp" }));
        }

        [Fact]
        public void Layout_ViewSelect_WideOrder_NoKeys_JoinsSpineDimsGroups()
        {
            var config = Load("""
                {
                  "tables": {
                    "device": { "kind": "master", "columns": ["SN"] },
                    "sweep":  { "kind": "rows" },
                    "rf":     { "kind": "measurements", "columns": ["Pout", "EVM"] }
                  }
                }
                """);
            var layout = TableLayout.Build(config, Wide);
            var sql = layout.BuildViewSelect();
            Assert.StartsWith(
                "SELECT \"sweep\".\"id\", \"device\".\"SN\", \"sweep\".\"Site\", \"sweep\".\"Temp\", \"rf\".\"Pout\", \"rf\".\"EVM\", \"sweep\".\"I_Total\", \"sweep\".\"Bin\" FROM \"sweep\"",
                sql,
                StringComparison.Ordinal);
            Assert.Contains("LEFT JOIN \"device\" ON \"device\".\"device_key\" = \"sweep\".\"device_key\"", sql, StringComparison.Ordinal);
            Assert.Contains("LEFT JOIN \"rf\" ON \"rf\".\"_row\" = \"sweep\".\"_row\"", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("_key\",", sql, StringComparison.Ordinal);
        }

        [Fact]
        public void Config_Tables_OverlayReplacesWhole_AndCloneIsDeep()
        {
            var baseline = Load("""{ "tables": { "a": { "kind": "measurements", "columns": ["EVM"] } } }""");
            var overlay = Load("""{ "tables": { "b": { "kind": "measurements", "columns": ["Pout"] } } }""");
            var merged = Config.Merge(baseline, overlay);
            Assert.Equal(new[] { "b" }, merged.Tables.Keys);
            merged.Tables["b"].Columns.Add("EVM");
            Assert.Equal(new[] { "Pout" }, overlay.Tables["b"].Columns);
        }

        [Fact]
        public void Config_Tables_Absent_IsEmpty_NotDeclared()
        {
            Assert.Empty(Config.CreateDefaults().Tables);
            Assert.False(TableLayout.IsDeclared(Load("{}")));
            Assert.False(TableLayout.IsDeclared(Load("""{ "tables": null }""")));
        }

        private static Config Load(string json)
        {
            var path = Path.Combine(Path.GetTempPath(), "databall-layout-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            try
            {
                return Config.LoadMerged(path);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
