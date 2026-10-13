// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace squalor.DataBall.Tests
{
    /// <summary>
    /// db-27: one planner decides the level of a configured metadata field, for the many-file open (<c>PlanOpen</c>), the wide import and
    /// append staging (<c>PlanStaged</c>) and the append against a stored value, wide or layout (<c>PlanAgainstStored</c>).
    /// </summary>
    public class MetadataPlannerTests
    {
        private const MetadataPolicyKind Lift = MetadataPolicyKind.LiftConstant;
        private const MetadataPolicyKind Require = MetadataPolicyKind.RequireConstant;
        private const MetadataPolicyKind First = MetadataPolicyKind.First;
        private const MetadataPolicyKind Bounce = MetadataPolicyKind.Bounce;

        private static FieldFacts Facts(long distinct, long nonNull, long rows, object? value = null) =>
            new(new ColumnProfile(distinct, nonNull, value), rows);

        private static FieldFacts Constant(object? value, long rows = 2) => Facts(1, rows, rows, value);

        private static Func<FieldFacts> Never => () => throw new InvalidOperationException("the facts were not needed");

        private static OpenHolder Holder(int index, FieldFacts facts, bool handler = false) => new(index, handler, facts);

        [Theory]
        [InlineData(null, "LiftConstant")]
        [InlineData("liftConstant", "LiftConstant")]
        [InlineData("requireConstant", "RequireConstant")]
        [InlineData("REQUIRECONSTANT", "RequireConstant")]
        [InlineData("first", "First")]
        [InlineData("bounce", "Bounce")]
        [InlineData("something-else", "LiftConstant")]
        public void MetadataPlanner_ParsePolicy(string? name, string expected) =>
            Assert.Equal(Enum.Parse<MetadataPolicyKind>(expected), MetadataPlanner.ParsePolicy(name));

        // ---- the staged table of a wide import or a wide append ----

        [Theory]
        [InlineData("LiftConstant")]
        [InlineData("RequireConstant")]
        [InlineData("Bounce")]
        public void MetadataPlanner_Staged_Constant_GoesToMetadata(string policyName)
        {
            var policy = Enum.Parse<MetadataPolicyKind>(policyName);
            var decision = MetadataPlanner.PlanStaged(policy, FieldHome.Fresh, "Lot", () => Constant("L1"));
            Assert.Equal(new MetadataDecision(MetadataLevel.Metadata, "L1"), decision);
        }

        [Fact]
        public void MetadataPlanner_Staged_Varying_StaysARowColumn_UnlessRequired()
        {
            var varying = Facts(2, 2, 2, "L1");
            Assert.Equal(MetadataLevel.RowColumn, MetadataPlanner.PlanStaged(Lift, FieldHome.Fresh, "Lot", () => varying).Level);
            Assert.Equal(MetadataLevel.RowColumn, MetadataPlanner.PlanStaged(Bounce, FieldHome.Fresh, "Lot", () => varying).Level);
            var ex = Assert.Throws<DataBallException>(() => MetadataPlanner.PlanStaged(Require, FieldHome.Fresh, "Lot", () => varying));
            Assert.Equal("Metadata field 'Lot' is not constant", ex.Message);
        }

        [Fact]
        public void MetadataPlanner_Staged_AllNull_StaysARowColumn()
        {
            var empty = Facts(0, 0, 3);
            foreach (var policy in new[] { Lift, Require, Bounce })
                Assert.Equal(MetadataLevel.RowColumn, MetadataPlanner.PlanStaged(policy, FieldHome.Fresh, "Lot", () => empty).Level);
        }

        [Fact]
        public void MetadataPlanner_Staged_EmptyCell_IsConstantUnlessBounce()
        {
            // One value and one empty cell.
            var gap = Facts(1, 1, 2, "L1");
            Assert.Equal(new MetadataDecision(MetadataLevel.Metadata, "L1"), MetadataPlanner.PlanStaged(Lift, FieldHome.Fresh, "Lot", () => gap));
            Assert.Equal(new MetadataDecision(MetadataLevel.Metadata, "L1"), MetadataPlanner.PlanStaged(Require, FieldHome.Fresh, "Lot", () => gap));
            Assert.Equal(MetadataLevel.RowColumn, MetadataPlanner.PlanStaged(Bounce, FieldHome.Fresh, "Lot", () => gap).Level);
        }

        [Fact]
        public void MetadataPlanner_Staged_First_ReadsTheFirstValue_WithoutProfiling()
        {
            var decision = MetadataPlanner.PlanStaged(First, FieldHome.Fresh, "Lot", Never);
            Assert.Equal(new MetadataDecision(MetadataLevel.Metadata, null, FirstNonNull: true), decision);
        }

        [Theory]
        [InlineData("Stored")]
        [InlineData("SessionColumn")]
        public void MetadataPlanner_Staged_AppendToAField_LeavesItToTheStoredComparison(string homeName)
        {
            var home = Enum.Parse<FieldHome>(homeName);
            // Without requireConstant nothing is read: the append compares against the stored value, or keeps the column.
            foreach (var policy in new[] { Lift, First, Bounce })
                Assert.Equal(MetadataLevel.Leave, MetadataPlanner.PlanStaged(policy, home, "Lot", Never).Level);
            // requireConstant still checks the field within this source.
            Assert.Equal(MetadataLevel.Leave, MetadataPlanner.PlanStaged(Require, home, "Lot", () => Constant("L1")).Level);
            var ex = Assert.Throws<DataBallException>(() => MetadataPlanner.PlanStaged(Require, home, "Lot", () => Facts(2, 2, 2, "L1")));
            Assert.Equal("Metadata field 'Lot' is not constant", ex.Message);
        }

        [Theory]
        [InlineData("PerSource")]
        [InlineData("LayoutKey")]
        [InlineData("Reserved")]
        public void MetadataPlanner_Staged_ProtectedField_IsLeft_WhateverThePolicy(string homeName)
        {
            var home = Enum.Parse<FieldHome>(homeName);
            foreach (var policy in new[] { Lift, Require, First, Bounce })
                Assert.Equal(new MetadataDecision(MetadataLevel.Leave), MetadataPlanner.PlanStaged(policy, home, "Lot", Never));
        }

        // ---- the staged sources of a many-file open ----

        [Fact]
        public void MetadataPlanner_Open_ConstantEverywhere_GoesToMetadata()
        {
            var one = MetadataPlanner.PlanOpen(Lift, FieldHome.Fresh, "Lot", new[] { Holder(0, Constant("L1")) }, 1);
            Assert.Equal(new MetadataDecision(MetadataLevel.Metadata, "L1"), one);
            var two = MetadataPlanner.PlanOpen(Lift, FieldHome.Fresh, "Lot", new[] { Holder(0, Constant("L1")), Holder(1, Constant("L1", 3)) }, 2);
            Assert.Equal(new MetadataDecision(MetadataLevel.Metadata, "L1"), two);
        }

        [Fact]
        public void MetadataPlanner_Open_NumbersOfDifferentWidthsAreTheSameValue()
        {
            var decision = MetadataPlanner.PlanOpen(Lift, FieldHome.Fresh, "Batch", new[] { Holder(0, Constant(7)), Holder(1, Constant(7L)) }, 2);
            Assert.Equal(MetadataLevel.Metadata, decision.Level);
            Assert.True(MetadataPlanner.SameValue(7, 7L));
            Assert.True(MetadataPlanner.SameValue(2.5f, 2.5));
            Assert.True(MetadataPlanner.SameValue(1, 1.0));
            Assert.False(MetadataPlanner.SameValue(1, 2L));
            Assert.False(MetadataPlanner.SameValue("1", 1));
            Assert.True(MetadataPlanner.SameValue(null, null));
            Assert.False(MetadataPlanner.SameValue(null, "x"));
        }

        [Fact]
        public void MetadataPlanner_Open_ConstantPerSourceButDifferent_IsASourceColumn()
        {
            var decision = MetadataPlanner.PlanOpen(Lift, FieldHome.Fresh, "Lot", new[] { Holder(0, Constant("L1")), Holder(1, Constant("L2")) }, 2);
            Assert.Equal(MetadataLevel.SourceColumn, decision.Level);
            Assert.Equal(new (int, object?)[] { (0, "L1"), (1, "L2") }, decision.Values!.ToArray());
        }

        [Fact]
        public void MetadataPlanner_Open_ConstantButAbsentFromASource_IsASourceColumn()
        {
            // One of three sources holds the field.
            var absent = MetadataPlanner.PlanOpen(Lift, FieldHome.Fresh, "Lot", new[] { Holder(1, Constant("L1")) }, 3);
            Assert.Equal(MetadataLevel.SourceColumn, absent.Level);
            Assert.Equal(new (int, object?)[] { (1, "L1") }, absent.Values!.ToArray());
            // Both sources hold it, but one has no value.
            var empty = MetadataPlanner.PlanOpen(Lift, FieldHome.Fresh, "Lot", new[] { Holder(0, Constant("L1")), Holder(1, Facts(0, 0, 2)) }, 2);
            Assert.Equal(MetadataLevel.SourceColumn, empty.Level);
            Assert.Equal(new (int, object?)[] { (0, "L1") }, empty.Values!.ToArray());
        }

        [Fact]
        public void MetadataPlanner_Open_VaryingWithinASource_IsARowColumn_OrAnErrorWhenRequired()
        {
            var holders = new[] { Holder(0, Constant("L1")), Holder(1, Facts(2, 2, 2, "L2")) };
            Assert.Equal(MetadataLevel.RowColumn, MetadataPlanner.PlanOpen(Lift, FieldHome.Fresh, "Lot", holders, 2).Level);
            Assert.Equal(MetadataLevel.RowColumn, MetadataPlanner.PlanOpen(Bounce, FieldHome.Fresh, "Lot", holders, 2).Level);
            var ex = Assert.Throws<DataBallException>(() => MetadataPlanner.PlanOpen(Require, FieldHome.Fresh, "Lot", holders, 2));
            Assert.Equal("Metadata field 'Lot' is not constant", ex.Message);
        }

        [Fact]
        public void MetadataPlanner_Open_NoValueAnywhere_IsARowColumn()
        {
            var decision = MetadataPlanner.PlanOpen(Lift, FieldHome.Fresh, "Lot", new[] { Holder(0, Facts(0, 0, 2)), Holder(1, Facts(0, 0, 1)) }, 2);
            Assert.Equal(new MetadataDecision(MetadataLevel.RowColumn), decision);
        }

        [Fact]
        public void MetadataPlanner_Open_Bounce_SourceWithEmptyCell_IsNotConstant()
        {
            var holders = new[] { Holder(0, Constant("L1")), Holder(1, Facts(1, 1, 2, "L2")) };
            Assert.Equal(MetadataLevel.RowColumn, MetadataPlanner.PlanOpen(Bounce, FieldHome.Fresh, "Lot", holders, 2).Level);
            Assert.Equal(MetadataLevel.SourceColumn, MetadataPlanner.PlanOpen(Lift, FieldHome.Fresh, "Lot", holders, 2).Level);
        }

        [Fact]
        public void MetadataPlanner_Open_First_ReadsTheFirstValue()
        {
            var decision = MetadataPlanner.PlanOpen(First, FieldHome.Fresh, "Lot", new[] { Holder(0, Facts(2, 2, 2, "L1")), Holder(1, Constant("L2")) }, 2);
            Assert.Equal(new MetadataDecision(MetadataLevel.Metadata, null, FirstNonNull: true), decision);
        }

        [Fact]
        public void MetadataPlanner_Open_HandlerField_StaysARowColumn_WhateverThePolicy()
        {
            var holders = new[] { Holder(0, Constant("L1")), Holder(1, Facts(0, 0, 0), handler: true) };
            foreach (var policy in new[] { Lift, Require, First, Bounce })
                Assert.Equal(MetadataLevel.Leave, MetadataPlanner.PlanOpen(policy, FieldHome.Fresh, "Lot", holders, 2).Level);
        }

        [Fact]
        public void MetadataPlanner_Open_NoHolder_IsLeft()
        {
            foreach (var policy in new[] { Lift, Require, First, Bounce })
                Assert.Equal(MetadataLevel.Leave, MetadataPlanner.PlanOpen(policy, FieldHome.Fresh, "Lot", Array.Empty<OpenHolder>(), 2).Level);
        }

        [Theory]
        [InlineData("Stored")]
        [InlineData("SessionColumn")]
        [InlineData("PerSource")]
        [InlineData("LayoutKey")]
        [InlineData("Reserved")]
        public void MetadataPlanner_Open_ProtectedField_IsLeft(string homeName)
        {
            var home = Enum.Parse<FieldHome>(homeName);
            foreach (var policy in new[] { Lift, Require, First, Bounce })
                Assert.Equal(MetadataLevel.Leave, MetadataPlanner.PlanOpen(policy, home, "Lot", new[] { Holder(0, Constant("L1")) }, 1).Level);
        }

        [Fact]
        public void MetadataPlanner_BuiltInSourceName_ReachesMetadata_ButNeverASourceColumn()
        {
            var name = FieldHome.BuiltInSourceName;
            // One value everywhere: Metadata, as for any field.
            Assert.Equal(new MetadataDecision(MetadataLevel.Metadata, "t"),
                MetadataPlanner.PlanOpen(Lift, name, "type", new[] { Holder(0, Constant("t")), Holder(1, Constant("t")) }, 2));
            Assert.Equal(new MetadataDecision(MetadataLevel.Metadata, "t"), MetadataPlanner.PlanStaged(Lift, name, "type", () => Constant("t")));
            Assert.Equal(new MetadataDecision(MetadataLevel.Metadata, null, FirstNonNull: true), MetadataPlanner.PlanStaged(First, name, "type", Never));
            // Different per source, or missing from a source: the source column is refused, so the field stays a row column.
            Assert.Equal(new MetadataDecision(MetadataLevel.RowColumn),
                MetadataPlanner.PlanOpen(Lift, name, "path", new[] { Holder(0, Constant("p1")), Holder(1, Constant("p2")) }, 2));
            Assert.Equal(new MetadataDecision(MetadataLevel.RowColumn),
                MetadataPlanner.PlanOpen(Lift, name, "path", new[] { Holder(1, Constant("p1")) }, 2));
            // The rest of the rule is the field's own.
            Assert.Equal(MetadataLevel.RowColumn, MetadataPlanner.PlanOpen(Lift, name, "type", new[] { Holder(0, Facts(2, 2, 2, "t")) }, 1).Level);
            Assert.Throws<DataBallException>(() => MetadataPlanner.PlanStaged(Require, name, "type", () => Facts(2, 2, 2, "t")));
        }

        [Fact]
        public void MetadataPlanner_Rejects_OnlyAVaryingFieldUnderRequireConstant()
        {
            Assert.True(MetadataPlanner.Rejects(Require, Facts(2, 2, 2)));
            Assert.False(MetadataPlanner.Rejects(Require, Constant("L1")));
            Assert.False(MetadataPlanner.Rejects(Require, Facts(0, 0, 2)));
            foreach (var policy in new[] { Lift, First, Bounce })
                Assert.False(MetadataPlanner.Rejects(policy, Facts(2, 2, 2)));
        }

        // ---- an append against a stored value, wide or layout ----

        [Theory]
        [InlineData("LiftConstant")]
        [InlineData("RequireConstant")]
        [InlineData("Bounce")]
        public void MetadataPlanner_Stored_EqualValues_DropTheIncomingColumn(string policyName) =>
            Assert.Equal(new MetadataDecision(MetadataLevel.Drop), MetadataPlanner.PlanAgainstStored(Enum.Parse<MetadataPolicyKind>(policyName), FieldHome.Stored, () => false));

        [Theory]
        [InlineData("LiftConstant")]
        [InlineData("RequireConstant")]
        [InlineData("Bounce")]
        public void MetadataPlanner_Stored_DifferingValues_Demote(string policyName) =>
            Assert.Equal(new MetadataDecision(MetadataLevel.Demote), MetadataPlanner.PlanAgainstStored(Enum.Parse<MetadataPolicyKind>(policyName), FieldHome.Stored, () => true));

        [Fact]
        public void MetadataPlanner_Stored_First_KeepsTheStoredValue_WithoutComparing() =>
            Assert.Equal(new MetadataDecision(MetadataLevel.Drop), MetadataPlanner.PlanAgainstStored(First, FieldHome.Stored, () => throw new InvalidOperationException("compared")));

        [Theory]
        [InlineData("SessionColumn")]
        [InlineData("PerSource")]
        [InlineData("LayoutKey")]
        [InlineData("Reserved")]
        [InlineData("Fresh")]
        public void MetadataPlanner_Stored_AFieldThatIsNotStored_IsLeft(string homeName)
        {
            var home = Enum.Parse<FieldHome>(homeName);
            foreach (var policy in new[] { Lift, Require, First, Bounce })
                Assert.Equal(new MetadataDecision(MetadataLevel.Leave), MetadataPlanner.PlanAgainstStored(policy, home, () => throw new InvalidOperationException("compared")));
        }
    }
}
