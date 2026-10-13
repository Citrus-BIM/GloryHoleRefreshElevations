namespace GloryHoleRefreshElevations
{
    internal static class OpeningRecreationUiPolicy
    {
        public static bool CanChooseLevel(bool rebind, bool selectedOnly) => rebind && selectedOnly;

        public static bool CanRecreate(bool rebind, bool selectedOnly, bool manual, string targetLevelId)
            => CanChooseLevel(rebind, selectedOnly) && manual && !string.IsNullOrWhiteSpace(targetLevelId);

        public static bool ShouldRound(bool requested, bool finishedOpening, bool rebind)
            => requested && !(finishedOpening && rebind);

        public static bool CanUsePreselection(int selectedCount, int supportedCount)
            => selectedCount > 0 && selectedCount == supportedCount;
    }
}
