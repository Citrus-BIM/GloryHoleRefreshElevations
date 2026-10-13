using System;
using System.Linq;

namespace GloryHoleRefreshElevations
{
    internal static class OpeningRebindingRules
    {
        public static RebindTaskProfile GetProfile(string code, string name)
        {
            // Do not reinterpret an assignment as a hosted opening because its type code was copied.
            if (RebindLevelRules.GetProfile(string.Empty, name) != RebindTaskProfile.Unsupported)
                return RebindTaskProfile.Unsupported;
            var named = NamedProfile(name);
            if (string.IsNullOrWhiteSpace(code)) return named;
            RebindTaskProfile coded;
            switch (code)
            {
                case "126": coded = RebindTaskProfile.WallRound; break;
                case "221": case "223": coded = RebindTaskProfile.SlabRectangular; break;
                case "222": case "224": coded = RebindTaskProfile.SlabRound; break;
                default: return RebindTaskProfile.Unsupported;
            }
            // The sleeve name identifies its host, not the cross-section. A known
            // opening code may refine the shape without conflicting with that name.
            if (name == "Гильза_Плита" && RebindLevelRules.IsSlab(coded)) return coded;
            return named == RebindTaskProfile.Unsupported || named == coded ? coded : RebindTaskProfile.Unsupported;
        }

        public static bool IsWindowFamily(string name) => WindowProfile(name) != RebindTaskProfile.Unsupported;

        public static bool IsOpening(string code, string name)
        {
            // Recognition is deliberately broader than validation, so contradictory profiles
            // receive a diagnostic instead of falling through to assignment processing.
            return NamedProfile(name) != RebindTaskProfile.Unsupported
                || code == "126" || code == "221" || code == "222" || code == "223" || code == "224";
        }

        public static double FloorReportOffset(double hostProjectElevation, double hostOffset, double openingProjectElevation)
        {
            double result = hostProjectElevation + hostOffset - openingProjectElevation;
            if (!IsFinite(hostProjectElevation) || !IsFinite(hostOffset) || !IsFinite(openingProjectElevation) || !IsFinite(result))
                throw new ArgumentException("Некорректная отметка основы или уровня отверстия.");
            return result;
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static RebindTaskProfile NamedProfile(string name)
        {
            switch (name)
            {
                case "Отверстие_Стена_Прямоугольное": return RebindTaskProfile.WallRectangular;
                case "Отверстие_Стена_Круглое": case "Гильза_Стена": return RebindTaskProfile.WallRound;
                case "Отверстие_Плита_Прямоугольное": return RebindTaskProfile.SlabRectangular;
                case "Отверстие_Плита_Круглое": case "Гильза_Плита": return RebindTaskProfile.SlabRound;
                default: return WindowProfile(name);
            }
        }

        private static RebindTaskProfile WindowProfile(string name)
        {
            switch (Normalize(name))
            {
                case "231ОТВЕРСТИЕПРЯМОУГОЛЬНОЕОКНОСТЕНА": return RebindTaskProfile.WallRectangular;
                case "231ОТВЕРСТИЕКРУГЛОЕСГИЛЬЗОЙВСТЕНЕОКНОСТЕНА": return RebindTaskProfile.WallRound;
                case "232ОТВЕРСТИЕВПЛИТЕПРЯМОУГОЛЬНОЕОКНОПЛИТА": return RebindTaskProfile.SlabRectangular;
                case "232ОТВЕРСТИЕКРУГЛОЕСГИЛЬЗОЙОКНОПЛИТА": return RebindTaskProfile.SlabRound;
                default: return RebindTaskProfile.Unsupported;
            }
        }

        private static string Normalize(string name) => new string((name ?? string.Empty)
            .Where(char.IsLetterOrDigit).Select(c => c == 'ё' || c == 'Ё' ? 'Е' : char.ToUpperInvariant(c)).ToArray());
    }
}
