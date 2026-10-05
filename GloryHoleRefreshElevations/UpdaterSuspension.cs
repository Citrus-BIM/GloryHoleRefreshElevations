using System;

namespace GloryHoleRefreshElevations
{
    internal interface IUpdaterState
    {
        bool IsRegistered();
        bool IsEnabled();
        void Disable();
        void Enable();
    }

    /// <summary>Restore the registry state, independently of the UpdaterOn XML setting.</summary>
    internal sealed class UpdaterSuspension : IDisposable
    {
        private readonly IUpdaterState state;
        private bool restoreEnabled;

        public UpdaterSuspension(IUpdaterState state)
        {
            this.state = state ?? throw new ArgumentNullException(nameof(state));
            // Unknown registry state must abort the operation before model writes.
            if (!state.IsRegistered()) return;
            if (!state.IsEnabled()) return;

            // A registry mutation may change state before throwing, so cleanup is already owed.
            restoreEnabled = true;
            try
            {
                state.Disable();
                if (state.IsEnabled())
                    throw new InvalidOperationException("Не удалось приостановить автоматическое обновление.");
            }
            catch (Exception suspensionError)
            {
                try
                {
                    Dispose();
                }
                catch (Exception restorationError)
                {
                    throw new AggregateException(
                        "Не удалось приостановить и восстановить автоматическое обновление.",
                        suspensionError, restorationError);
                }
                throw;
            }
        }

        public void Dispose()
        {
            if (!restoreEnabled) return;
            state.Enable();
            if (!state.IsEnabled())
                throw new InvalidOperationException("Не удалось восстановить автоматическое обновление.");
            // Preserve the obligation on every failure, allowing the caller to retry restoration.
            restoreEnabled = false;
        }
    }
}
