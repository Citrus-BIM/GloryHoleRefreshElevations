using Xunit;

namespace GloryHoleRefreshElevations.Tests
{
    public sealed class OpeningRecreationUiPolicyTests
    {
        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(false, false, false)]
        public void ChoosingTargetIsAvailableOnlyForSelectedRebind(bool rebind, bool selected, bool expected)
            => Assert.Equal(expected, OpeningRecreationUiPolicy.CanChooseLevel(rebind, selected));

        [Theory]
        [InlineData(true, true, true, "level", true)]
        [InlineData(false, true, true, "level", false)]
        [InlineData(true, false, true, "level", false)]
        [InlineData(true, true, false, "level", false)]
        [InlineData(true, true, true, null, false)]
        [InlineData(true, true, true, "", false)]
        [InlineData(true, true, true, " ", false)]
        public void OpeningRecreationRequiresExplicitSelectedTarget(bool rebind, bool selected, bool manual, string? level, bool expected)
            => Assert.Equal(expected, OpeningRecreationUiPolicy.CanRecreate(rebind, selected, manual, level));

        [Theory]
        [InlineData(true, true, true, false)]
        [InlineData(false, true, true, false)]
        [InlineData(true, false, true, true)]
        [InlineData(false, false, true, false)]
        [InlineData(true, true, false, true)]
        [InlineData(false, true, false, false)]
        public void RebindingOpeningsPreservesPositionWhileTasksAndRefreshKeepRoundingChoice(
            bool requested, bool opening, bool rebind, bool expected)
            => Assert.Equal(expected, OpeningRecreationUiPolicy.ShouldRound(requested, opening, rebind));

        [Theory]
        [InlineData(0, 0, false)]
        [InlineData(1, 1, true)]
        [InlineData(3, 3, true)]
        [InlineData(3, 2, false)]
        [InlineData(3, 0, false)]
        public void ReusePreselectionOnlyWhenEverySelectedElementIsSupported(int selected, int supported, bool expected)
            => Assert.Equal(expected, OpeningRecreationUiPolicy.CanUsePreselection(selected, supported));
    }
}
