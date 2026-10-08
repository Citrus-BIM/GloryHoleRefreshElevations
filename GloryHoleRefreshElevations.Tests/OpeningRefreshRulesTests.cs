using System;
using Xunit;

namespace GloryHoleRefreshElevations.Tests
{
    public sealed class OpeningRefreshRulesTests
    {
        [Fact] public void MissingSlabHostIsAnExplicitSkip()
        {
            var error = Assert.Throws<InvalidOperationException>(() => OpeningRefreshRules.RequireHost(false));
            Assert.Contains("основа", error.Message);
        }

        [Fact] public void ExistingHostAllowsRefresh() => OpeningRefreshRules.RequireHost(true);
    }
}
