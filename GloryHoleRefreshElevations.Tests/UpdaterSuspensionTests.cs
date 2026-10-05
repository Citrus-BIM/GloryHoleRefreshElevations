using System;
using System.Collections.Generic;
using Xunit;

namespace GloryHoleRefreshElevations.Tests
{
    public class UpdaterSuspensionTests
    {
        [Fact]
        public void AbsentUpdaterRequiresNoEnablementQueryOrMutation()
        {
            var state = new FakeUpdaterState { Registered = false, EnablementErrorAt = 1 };
            using (CreateSuspension(state)) { }
            Assert.Equal(new[] { "registered" }, state.Calls);
        }

        [Fact]
        public void PreviouslyDisabledUpdaterStaysDisabled()
        {
            var state = new FakeUpdaterState { Enabled = false };
            using (CreateSuspension(state)) { Assert.False(state.Enabled); }
            Assert.False(state.Enabled);
            Assert.Equal(new[] { "registered", "enabled" }, state.Calls);
        }

        [Fact]
        public void EnabledUpdaterRemainsSuspendedUntilScopeEndsAndRestoresOnlyOnce()
        {
            var state = new FakeUpdaterState();
            var suspension = CreateSuspension(state);
            Assert.False(state.Enabled);
            Assert.Equal(0, state.EnableCount);
            suspension.Dispose();
            Assert.True(state.Enabled);
            suspension.Dispose();
            Assert.Equal(1, state.EnableCount);
        }

        [Fact]
        public void ModelOperationFailureStillRestoresUpdater()
        {
            var state = new FakeUpdaterState();
            var failure = new InvalidOperationException("Model transaction failed");
            var caught = Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using (CreateSuspension(state))
                {
                    Assert.False(state.Enabled);
                    throw failure;
                }
            }));
            Assert.Same(failure, caught);
            Assert.True(state.Enabled);
        }

        [Fact]
        public void UnknownRegistrationAbortsWithoutTreatingUpdaterAsAbsent()
        {
            var state = new FakeUpdaterState { RegistrationError = new InvalidOperationException("Unknown registration") };
            Assert.Same(state.RegistrationError, Assert.Throws<InvalidOperationException>(() => CreateSuspension(state)));
            Assert.True(state.Enabled);
            Assert.Equal(new[] { "registered" }, state.Calls);
        }

        [Fact]
        public void UnknownInitialEnablementAbortsWithoutChangingRegistry()
        {
            var state = new FakeUpdaterState { EnablementErrorAt = 1 };
            Assert.Same(state.QueryError, Assert.Throws<InvalidOperationException>(() => CreateSuspension(state)));
            Assert.True(state.Enabled);
            Assert.Equal(0, state.DisableCount);
            Assert.Equal(0, state.EnableCount);
        }

        [Fact]
        public void DisableThatChangesStateThenThrowsRestoresBeforePropagatingFailure()
        {
            var state = new FakeUpdaterState { DisableError = new InvalidOperationException("Disable failed after mutation") };
            Assert.Same(state.DisableError, Assert.Throws<InvalidOperationException>(() => CreateSuspension(state)));
            Assert.True(state.Enabled);
            Assert.Equal(1, state.EnableCount);
        }

        [Fact]
        public void IneffectiveDisableAbortsBeforeModelWork()
        {
            var state = new FakeUpdaterState { IgnoreDisable = true };
            Assert.Throws<InvalidOperationException>(() => CreateSuspension(state));
            Assert.True(state.Enabled);
            Assert.Equal(1, state.EnableCount);
        }

        [Fact]
        public void UnknownStateAfterDisableRestoresBeforePropagatingOriginalQueryFailure()
        {
            var state = new FakeUpdaterState { EnablementErrorAt = 2 };
            Assert.Same(state.QueryError, Assert.Throws<InvalidOperationException>(() => CreateSuspension(state)));
            Assert.True(state.Enabled);
            Assert.Equal(1, state.EnableCount);
        }

        [Fact]
        public void FailedSuspensionAndFailedCleanupRetainBothErrors()
        {
            var state = new FakeUpdaterState
            {
                DisableError = new InvalidOperationException("Disable failed after mutation"),
                EnableErrorAt = 1
            };
            var exception = Assert.Throws<AggregateException>(() => CreateSuspension(state));
            Assert.Collection(exception.InnerExceptions,
                error => Assert.Same(state.DisableError, error),
                error => Assert.Same(state.RestoreError, error));
            Assert.False(state.Enabled);
        }

        [Fact]
        public void FailedEnableIsVisibleAndRestorationCanBeRetried()
        {
            var state = new FakeUpdaterState { EnableErrorAt = 1 };
            var suspension = CreateSuspension(state);
            Assert.Same(state.RestoreError, Assert.Throws<InvalidOperationException>(() => suspension.Dispose()));
            Assert.False(state.Enabled);
            suspension.Dispose();
            Assert.True(state.Enabled);
            Assert.Equal(2, state.EnableCount);
        }

        [Fact]
        public void IneffectiveEnableIsVerifiedAndRestorationCanBeRetried()
        {
            var state = new FakeUpdaterState { IgnoreEnableAt = 1 };
            var suspension = CreateSuspension(state);
            Assert.Throws<InvalidOperationException>(() => suspension.Dispose());
            Assert.False(state.Enabled);
            suspension.Dispose();
            Assert.True(state.Enabled);
            Assert.Equal(2, state.EnableCount);
        }

        [Fact]
        public void UnknownRestoredStateIsVisibleAndCanBeVerifiedOnRetry()
        {
            var state = new FakeUpdaterState { EnablementErrorAt = 3 };
            var suspension = CreateSuspension(state);
            Assert.Same(state.QueryError, Assert.Throws<InvalidOperationException>(() => suspension.Dispose()));
            Assert.True(state.Enabled);
            suspension.Dispose();
            Assert.True(state.Enabled);
            Assert.Equal(2, state.EnableCount);
        }

        private static UpdaterSuspension CreateSuspension(FakeUpdaterState state)
        {
            return new UpdaterSuspension(state);
        }

        private sealed class FakeUpdaterState : IUpdaterState
        {
            public bool Registered = true;
            public bool Enabled = true;
            public readonly List<string> Calls = new List<string>();
            public Exception RegistrationError;
            public Exception DisableError;
            public readonly Exception QueryError = new InvalidOperationException("Unknown enablement");
            public readonly Exception RestoreError = new InvalidOperationException("Enable failed");
            public int EnablementErrorAt;
            public int EnableErrorAt;
            public int IgnoreEnableAt;
            public bool IgnoreDisable;
            public int DisableCount;
            public int EnableCount;
            private int enablementReads;

            public bool IsRegistered()
            {
                Calls.Add("registered");
                if (RegistrationError != null) throw RegistrationError;
                return Registered;
            }

            public bool IsEnabled()
            {
                Calls.Add("enabled");
                if (++enablementReads == EnablementErrorAt) throw QueryError;
                return Enabled;
            }

            public void Disable()
            {
                Calls.Add("disable");
                DisableCount++;
                if (!IgnoreDisable) Enabled = false;
                if (DisableError != null) throw DisableError;
            }

            public void Enable()
            {
                Calls.Add("enable");
                if (++EnableCount == EnableErrorAt) throw RestoreError;
                if (EnableCount != IgnoreEnableAt) Enabled = true;
            }
        }
    }
}
