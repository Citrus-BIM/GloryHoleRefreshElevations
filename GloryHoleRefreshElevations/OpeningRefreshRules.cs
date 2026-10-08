using System;

namespace GloryHoleRefreshElevations
{
    internal static class OpeningRefreshRules
    {
        public static void RequireHost(bool hasHost)
        {
            if (!hasHost)
                throw new InvalidOperationException("У готового отверстия отсутствует основа. Проверьте привязку; отметки не изменены.");
        }
    }
}
