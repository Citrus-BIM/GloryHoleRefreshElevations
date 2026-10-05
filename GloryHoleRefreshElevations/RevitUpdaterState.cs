using Autodesk.Revit.DB;
using System;

namespace GloryHoleRefreshElevations
{
    internal sealed class RevitUpdaterState : IUpdaterState
    {
        // The updater belongs to RibbonCITRUS, not to the dynamically loaded command.
        private readonly UpdaterId updaterId = new UpdaterId(
            new AddInId(new Guid("6f00eeea-d900-4ce3-b0cc-1409fc3dfc47")),
            new Guid("9cc8b3b9-8067-4d55-9fae-cf0601d0d067"));

        public bool IsRegistered()
        {
            // It is registered application-wide; the document overload would miss it.
            return UpdaterRegistry.IsUpdaterRegistered(updaterId);
        }

        public bool IsEnabled()
        {
            return UpdaterRegistry.IsUpdaterEnabled(updaterId);
        }

        public void Disable()
        {
            UpdaterRegistry.DisableUpdater(updaterId);
        }

        public void Enable()
        {
            UpdaterRegistry.EnableUpdater(updaterId);
        }
    }
}
