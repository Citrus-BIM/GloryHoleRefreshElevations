using System;
using System.Globalization;
using Xunit;

namespace GloryHoleRefreshElevations.Tests
{
    public class RebindLevelRulesTests
    {
        [Theory]
        [InlineData("111", (int)RebindTaskProfile.WallRectangular)]
        [InlineData("113", (int)RebindTaskProfile.WallRectangular)]
        [InlineData("115", (int)RebindTaskProfile.WallRectangular)]
        [InlineData("112", (int)RebindTaskProfile.WallRound)]
        [InlineData("114", (int)RebindTaskProfile.WallRound)]
        [InlineData("116", (int)RebindTaskProfile.WallRound)]
        [InlineData("121", (int)RebindTaskProfile.SlabRectangular)]
        [InlineData("123", (int)RebindTaskProfile.SlabRectangular)]
        [InlineData("122", (int)RebindTaskProfile.SlabRound)]
        [InlineData("124", (int)RebindTaskProfile.SlabRound)]
        public void TaskCodesSupportRenamedFamilies(string code, int expected)
        {
            Assert.Equal((RebindTaskProfile)expected, RebindLevelRules.GetProfile(code, "Пользовательское задание"));
        }

        [Theory]
        [InlineData(null, "Пересечение_Стена_Прямоугольное", (int)RebindTaskProfile.WallRectangular)]
        [InlineData("", "Пересечение_Стена_Круглое", (int)RebindTaskProfile.WallRound)]
        [InlineData(" ", "Пересечение_Плита_Прямоугольное", (int)RebindTaskProfile.SlabRectangular)]
        [InlineData(null, "Пересечение_Плита_Круглое", (int)RebindTaskProfile.SlabRound)]
        public void MissingCodeFallsBackOnlyToExactTaskFamily(string code, string name, int expected)
        {
            Assert.Equal((RebindTaskProfile)expected, RebindLevelRules.GetProfile(code, name));
        }

        [Theory]
        [InlineData("111", "Пересечение_Плита_Прямоугольное")]
        [InlineData("121", "Пересечение_Стена_Прямоугольное")]
        [InlineData("111", "Пересечение_Стена_Круглое")]
        [InlineData("111", "Отверстие_Стена_Прямоугольное")]
        [InlineData("122", "Отверстие_Плита_Круглое")]
        [InlineData("111", "Гильза_Стена")]
        [InlineData("121", "Гильза_Плита")]
        [InlineData("126", "Пересечение_Стена_Круглое")]
        [InlineData("221", "Пересечение_Плита_Прямоугольное")]
        [InlineData("999", "Пересечение_Стена_Прямоугольное")]
        [InlineData("125", null)]
        [InlineData(null, "Моя_болванка")]
        [InlineData(null, "Пересечение_Стена_Круглое_Копия")]
        [InlineData(null, null)]
        public void UnknownOrConflictingProfileIsUnsupported(string code, string name)
        {
            Assert.Equal(RebindTaskProfile.Unsupported, RebindLevelRules.GetProfile(code, name));
        }

        [Theory]
        [InlineData((int)RebindTaskProfile.WallRectangular, false)]
        [InlineData((int)RebindTaskProfile.WallRound, false)]
        [InlineData((int)RebindTaskProfile.SlabRectangular, true)]
        [InlineData((int)RebindTaskProfile.SlabRound, true)]
        [InlineData((int)RebindTaskProfile.Unsupported, false)]
        public void SlabProfilesAreExplicit(int profileValue, bool expected)
        {
            Assert.Equal(expected, RebindLevelRules.IsSlab((RebindTaskProfile)profileValue));
        }

        [Theory]
        [InlineData((int)RebindTaskProfile.WallRectangular)]
        [InlineData((int)RebindTaskProfile.WallRound)]
        public void WallReferenceUsesInsertionPoint(int profileValue)
        {
            Assert.Equal(-4.5, RebindLevelRules.ReferenceHeight((RebindTaskProfile)profileValue, -4.5));
        }

        [Theory]
        [InlineData((int)RebindTaskProfile.SlabRectangular)]
        [InlineData((int)RebindTaskProfile.SlabRound)]
        public void SlabReferenceSubtractsFiftyMillimetres(int profileValue)
        {
            var reference = RebindLevelRules.ReferenceHeight((RebindTaskProfile)profileValue, 10 + 50.0 / 304.8);
            Assert.Equal(10, reference, 12);
        }

        [Fact]
        public void SlabAnchorCorrectionCanChangeChosenLevel()
        {
            var levels = new[] { Level("low", 0), Level("high", 2) };
            var reference = RebindLevelRules.ReferenceHeight(RebindTaskProfile.SlabRectangular, 1.1);
            Assert.Equal("low", RebindLevelRules.Resolve(levels, RebindTaskProfile.SlabRectangular, reference).Level.UniqueId);
        }

        [Theory]
        [InlineData(0, "ground")]
        [InlineData(9.9, "ground")]
        [InlineData(10, "first")]
        [InlineData(19.9, "first")]
        [InlineData(100, "second")]
        [InlineData(-5, "basement")]
        public void WallChoosesHighestLevelAtOrBelowReference(double reference, string expected)
        {
            var levels = new[] { Level("first", 10), Level("basement", -10), Level("second", 20), Level("ground", 0) };
            var result = RebindLevelRules.Resolve(levels, RebindTaskProfile.WallRectangular, reference);
            Assert.Equal(LevelResolutionStatus.Resolved, result.Status);
            Assert.Equal(expected, result.Level.UniqueId);
        }

        [Fact]
        public void WallBelowEveryLevelIsSkipped()
        {
            var result = RebindLevelRules.Resolve(new[] { Level("ground", 0) }, RebindTaskProfile.WallRound, -1);
            Assert.Equal(LevelResolutionStatus.NoLevelBelow, result.Status);
            Assert.Null(result.Level);
        }

        [Fact]
        public void WallAcceptsCoincidentLevelWithinGeometricTolerance()
        {
            var result = RebindLevelRules.Resolve(new[] { Level("ground", 0) }, RebindTaskProfile.WallRound, -RebindLevelRules.Tolerance / 2);
            Assert.Equal(LevelResolutionStatus.Resolved, result.Status);
        }

        [Theory]
        [InlineData(-12, "low")]
        [InlineData(-9, "low")]
        [InlineData(-1, "middle")]
        [InlineData(6, "high")]
        [InlineData(20, "high")]
        public void SlabChoosesNearestLevel(double reference, string expected)
        {
            var levels = new[] { Level("high", 10), Level("low", -10), Level("middle", 0) };
            var result = RebindLevelRules.Resolve(levels, RebindTaskProfile.SlabRound, reference);
            Assert.Equal(LevelResolutionStatus.Resolved, result.Status);
            Assert.Equal(expected, result.Level.UniqueId);
        }

        [Theory]
        [InlineData((int)RebindTaskProfile.WallRectangular)]
        [InlineData((int)RebindTaskProfile.SlabRound)]
        public void DuplicateWinningLevelsAreAmbiguous(int profileValue)
        {
            var result = RebindLevelRules.Resolve(new[] { Level("one", 0), Level("two", 0) }, (RebindTaskProfile)profileValue, 0);
            Assert.Equal(LevelResolutionStatus.Ambiguous, result.Status);
            Assert.Null(result.Level);
        }

        [Fact]
        public void EquallyNearSlabLevelsAreAmbiguous()
        {
            var result = RebindLevelRules.Resolve(new[] { Level("one", -10), Level("two", 0) }, RebindTaskProfile.SlabRound, -5);
            Assert.Equal(LevelResolutionStatus.Ambiguous, result.Status);
        }

        [Fact]
        public void NearTieWithinToleranceIsAmbiguous()
        {
            var result = RebindLevelRules.Resolve(new[] { Level("one", 0), Level("two", 2) }, RebindTaskProfile.SlabRound, 1 + RebindLevelRules.Tolerance / 4);
            Assert.Equal(LevelResolutionStatus.Ambiguous, result.Status);
        }

        [Fact]
        public void DuplicateNonWinningLevelsDoNotBlockUniqueTarget()
        {
            var result = RebindLevelRules.Resolve(new[] { Level("one", 0), Level("two", 0), Level("three", 10) }, RebindTaskProfile.WallRound, 11);
            Assert.Equal("three", result.Level.UniqueId);
        }

        [Fact]
        public void ResolverUsesProjectElevationRegardlessOfReportElevation()
        {
            var levels = new[] { new RebindLevel("one", "One", 0, 100), new RebindLevel("two", "Two", 10, 110) };
            Assert.Equal("two", RebindLevelRules.Resolve(levels, RebindTaskProfile.WallRound, 11).Level.UniqueId);
        }

        [Fact]
        public void EmptyLevelSetHasDistinctFailure()
        {
            var result = RebindLevelRules.Resolve(new RebindLevel[0], RebindTaskProfile.WallRound, 0);
            Assert.Equal(LevelResolutionStatus.NoLevels, result.Status);
            Assert.Null(result.Level);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void NonFiniteReferenceIsRejected(double reference)
        {
            var result = RebindLevelRules.Resolve(new[] { Level("ground", 0) }, RebindTaskProfile.WallRound, reference);
            Assert.Equal(LevelResolutionStatus.InvalidHeight, result.Status);
            Assert.Null(result.Level);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void NonFiniteLevelElevationIsRejected(double elevation)
        {
            var result = RebindLevelRules.Resolve(new[] { Level("invalid", elevation), Level("ground", 0) }, RebindTaskProfile.SlabRound, 0);
            Assert.Equal(LevelResolutionStatus.InvalidHeight, result.Status);
        }

        [Fact]
        public void UnsupportedProfileCannotProduceTarget()
        {
            Assert.True(double.IsNaN(RebindLevelRules.ReferenceHeight(RebindTaskProfile.Unsupported, 0)));
            Assert.Equal(LevelResolutionStatus.InvalidHeight, RebindLevelRules.Resolve(new[] { Level("ground", 0) }, RebindTaskProfile.Unsupported, 0).Status);
        }

        [Theory]
        [InlineData(3, 0, 10, -7)]
        [InlineData(-4, 10, -10, 16)]
        [InlineData(1.5, -10, -10, 1.5)]
        public void OffsetCompensationPreservesWorldInsertionHeight(double oldOffset, double oldLevel, double newLevel, double expected)
        {
            var offset = RebindLevelRules.CompensateOffset(oldOffset, oldLevel, newLevel);
            Assert.Equal(expected, offset, 12);
            Assert.Equal(oldLevel + oldOffset, newLevel + offset, 12);
        }

        [Theory]
        [InlineData((int)RebindTaskProfile.WallRectangular)]
        [InlineData((int)RebindTaskProfile.WallRound)]
        public void UnboundWallUsesActualInsertionWithoutAnOldLevel(int profileValue)
        {
            var profile = (RebindTaskProfile)profileValue;
            const double insertionZ = 12.5;
            var reference = RebindLevelRules.ReferenceHeight(profile, insertionZ);
            var target = RebindLevelRules.Resolve(new[] { Level("low", 0), Level("high", 10) }, profile, reference);
            Assert.Equal(LevelResolutionStatus.Resolved, target.Status);

            var nativeOffset = RebindLevelRules.NativeOffsetForPosition(insertionZ, target.Level.ProjectElevation);
            Assert.Equal(2.5, nativeOffset, 12);
            Assert.Equal(insertionZ, target.Level.ProjectElevation + nativeOffset, 12);
        }

        [Theory]
        [InlineData((int)RebindTaskProfile.SlabRectangular)]
        [InlineData((int)RebindTaskProfile.SlabRound)]
        public void UnboundSlabAppliesFiftyMillimetresOnlyToSelectionAndReport(int profileValue)
        {
            var profile = (RebindTaskProfile)profileValue;
            double insertionZ = 10.01 + 50.0 / 304.8;
            var reference = RebindLevelRules.ReferenceHeight(profile, insertionZ);
            var target = RebindLevelRules.Resolve(new[] { Level("low", 10), Level("high", 20) }, profile, reference);
            Assert.Equal(LevelResolutionStatus.Resolved, target.Status);

            var nativeOffset = RebindLevelRules.NativeOffsetForPosition(insertionZ, target.Level.ProjectElevation);
            Assert.Equal(0.01 + 50.0 / 304.8, nativeOffset, 12);
            Assert.Equal(0.01, nativeOffset - 50.0 / 304.8, 12);
            Assert.Equal(insertionZ, target.Level.ProjectElevation + nativeOffset, 12);
        }

        [Theory]
        [InlineData(102.5, 100, 0, 2.5)]
        [InlineData(102.5, 110, 10, -7.5)]
        [InlineData(-5, -10, 90, 5)]
        public void NativeOffsetUsesProjectElevationAndAllowsTargetAboveTask(double insertionZ, double projectElevation, double reportElevation, double expected)
        {
            var target = new RebindLevel("target", "Target", projectElevation, reportElevation);
            var nativeOffset = RebindLevelRules.NativeOffsetForPosition(insertionZ, target.ProjectElevation);
            Assert.Equal(expected, nativeOffset, 12);
            Assert.Equal(insertionZ, target.ProjectElevation + nativeOffset, 12);
        }

        [Theory]
        [InlineData(double.NaN, 0)]
        [InlineData(double.PositiveInfinity, 0)]
        [InlineData(double.NegativeInfinity, 0)]
        [InlineData(0, double.NaN)]
        [InlineData(0, double.PositiveInfinity)]
        [InlineData(0, double.NegativeInfinity)]
        [InlineData(double.MaxValue, -double.MaxValue)]
        public void NativeOffsetRejectsNonFiniteInputOrResult(double insertionZ, double projectElevation)
        {
            Assert.True(double.IsNaN(RebindLevelRules.NativeOffsetForPosition(insertionZ, projectElevation)));
        }

        [Fact]
        public void LevelDisplayKeepsNameAndConvertsFeetToMetres()
        {
            var level = new RebindLevel("id", "Этаж 2", 0, 10);
            Assert.Contains("Этаж 2", level.DisplayName);
            Assert.Contains(3.048.ToString("0.000", CultureInfo.CurrentCulture), level.DisplayName);
        }

        private static RebindLevel Level(string id, double elevation)
        {
            return new RebindLevel(id, id, elevation, elevation);
        }
    }
}
