using System;
using Xunit;

namespace GloryHoleRefreshElevations.Tests
{
    public sealed class OpeningRebindingRulesTests
    {
        [Theory]
        [InlineData("", "Отверстие_Стена_Прямоугольное", 1)]
        [InlineData("", "Отверстие_Стена_Круглое", 2)]
        [InlineData("", "Гильза_Стена", 2)]
        [InlineData("", "Отверстие_Плита_Прямоугольное", 3)]
        [InlineData("", "Отверстие_Плита_Круглое", 4)]
        [InlineData("", "Гильза_Плита", 4)]
        [InlineData("126", "Переименованное круглое отверстие", 2)]
        [InlineData("221", "Переименованное отверстие", 3)]
        [InlineData("223", "Переименованное отверстие", 3)]
        [InlineData("222", "Переименованное отверстие", 4)]
        [InlineData("224", "Переименованное отверстие", 4)]
        [InlineData("126", "Отверстие_Стена_Круглое", 2)]
        [InlineData("221", "Гильза_Плита", 3)]
        [InlineData("223", "Гильза_Плита", 3)]
        public void KnownOpeningsHaveSeparatePlacementProfiles(string code, string name, int expected)
            => Assert.Equal((RebindTaskProfile)expected, OpeningRebindingRules.GetProfile(code, name));

        [Theory]
        [InlineData("231_Отверстие прямоугольное (Окно_Стена)", 1)]
        [InlineData("231_Отверстие круглое с гильзой в стене (Окно_Стена)", 2)]
        [InlineData("232_Отверстие в плите прямоугольное (Окно_Плита)", 3)]
        [InlineData("232_Отверстие круглое с гильзой (Окно_Плита)", 4)]
        public void WindowOpeningsAreSupportedForBothHosts(string name, int expected)
        {
            Assert.True(OpeningRebindingRules.IsWindowFamily(name));
            Assert.Equal((RebindTaskProfile)expected, OpeningRebindingRules.GetProfile(null, name));
        }

        [Theory]
        [InlineData("111", "Пересечение_Стена_Прямоугольное")]
        [InlineData("121", "Пересечение_Плита_Прямоугольное")]
        [InlineData("126", "Пересечение_Стена_Круглое")]
        [InlineData("221", "Отверстие_Стена_Прямоугольное")]
        [InlineData("111", "Отверстие_Стена_Прямоугольное")]
        [InlineData("126", "Отверстие_Плита_Круглое")]
        [InlineData("unknown", "Отверстие_Стена_Прямоугольное")]
        [InlineData("", "Произвольное окно")]
        public void TasksAndContradictoryProfilesAreRejected(string code, string name)
            => Assert.Equal(RebindTaskProfile.Unsupported, OpeningRebindingRules.GetProfile(code, name));

        [Theory]
        [InlineData(30.0, -0.5, 20.0, 9.5)]
        [InlineData(30.0, -0.5, 30.0, -0.5)]
        [InlineData(30.0, -0.5, 40.0, -10.5)]
        public void FloorReportUsesOpeningLevelAndNoTaskFiftyMillimetreShift(
            double hostLevel, double hostOffset, double openingLevel, double expected)
            => Assert.Equal(expected, OpeningRebindingRules.FloorReportOffset(hostLevel, hostOffset, openingLevel), 9);

        [Fact]
        public void InvalidFloorHeightsCannotBecomeReportParameters()
            => Assert.Throws<ArgumentException>(() => OpeningRebindingRules.FloorReportOffset(double.NaN, 0, 0));
    }
}
