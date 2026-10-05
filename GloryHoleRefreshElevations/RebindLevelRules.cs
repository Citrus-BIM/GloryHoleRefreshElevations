using System;
using System.Collections.Generic;
using System.Globalization;

namespace GloryHoleRefreshElevations
{
    internal sealed class RebindLevel
    {
        public RebindLevel(string uniqueId, string name, double projectElevation, double displayElevation)
        {
            UniqueId = uniqueId;
            Name = name;
            ProjectElevation = projectElevation;
            DisplayElevation = displayElevation;
        }

        public string UniqueId { get; }
        public string Name { get; }
        public double ProjectElevation { get; }
        public double DisplayElevation { get; }
        public string DisplayName
        {
            get { return Name + " (" + (DisplayElevation * 0.3048).ToString("0.000", CultureInfo.CurrentCulture) + " м)"; }
        }
    }

    internal enum RebindTaskProfile { Unsupported, WallRectangular, WallRound, SlabRectangular, SlabRound }
    internal enum LevelResolutionStatus { Resolved, NoLevels, NoLevelBelow, Ambiguous, InvalidHeight }

    internal sealed class LevelResolution
    {
        public LevelResolution(LevelResolutionStatus status, RebindLevel level = null)
        {
            Status = status;
            Level = level;
        }

        public LevelResolutionStatus Status { get; }
        public RebindLevel Level { get; }
    }

    internal static class RebindLevelRules
    {
        public const double Tolerance = 1e-6;

        public static RebindTaskProfile GetProfile(string code, string name)
        {
            // Finished openings and sleeves must not enter the task rebinding path,
            // even if a copied type parameter contains a task code.
            switch (name)
            {
                case "Отверстие_Стена_Прямоугольное":
                case "Отверстие_Стена_Круглое":
                case "Отверстие_Плита_Прямоугольное":
                case "Отверстие_Плита_Круглое":
                case "Гильза_Стена":
                case "Гильза_Плита":
                    return RebindTaskProfile.Unsupported;
            }

            RebindTaskProfile namedProfile;
            switch (name)
            {
                case "Пересечение_Стена_Прямоугольное": namedProfile = RebindTaskProfile.WallRectangular; break;
                case "Пересечение_Стена_Круглое": namedProfile = RebindTaskProfile.WallRound; break;
                case "Пересечение_Плита_Прямоугольное": namedProfile = RebindTaskProfile.SlabRectangular; break;
                case "Пересечение_Плита_Круглое": namedProfile = RebindTaskProfile.SlabRound; break;
                default: namedProfile = RebindTaskProfile.Unsupported; break;
            }

            if (string.IsNullOrWhiteSpace(code))
                return namedProfile;

            RebindTaskProfile codedProfile;
            switch (code)
            {
                case "111":
                case "113":
                case "115": codedProfile = RebindTaskProfile.WallRectangular; break;
                case "112":
                case "114":
                case "116": codedProfile = RebindTaskProfile.WallRound; break;
                case "121":
                case "123": codedProfile = RebindTaskProfile.SlabRectangular; break;
                case "122":
                case "124": codedProfile = RebindTaskProfile.SlabRound; break;
                default: return RebindTaskProfile.Unsupported;
            }

            return namedProfile != RebindTaskProfile.Unsupported && namedProfile != codedProfile
                ? RebindTaskProfile.Unsupported
                : codedProfile;
        }

        public static bool IsSlab(RebindTaskProfile profile)
        {
            return profile == RebindTaskProfile.SlabRectangular || profile == RebindTaskProfile.SlabRound;
        }

        public static double ReferenceHeight(RebindTaskProfile profile, double insertionPointZ)
        {
            if (!IsSupported(profile) || !IsFinite(insertionPointZ))
                return double.NaN;

            // The standard slab tasks' insertion point sits 50 mm above the slab reference.
            return insertionPointZ - (IsSlab(profile) ? 50.0 / 304.8 : 0);
        }

        public static LevelResolution Resolve(IReadOnlyList<RebindLevel> levels, RebindTaskProfile profile, double referenceHeight)
        {
            if (!IsSupported(profile) || !IsFinite(referenceHeight))
                return new LevelResolution(LevelResolutionStatus.InvalidHeight);
            if (levels == null || levels.Count == 0)
                return new LevelResolution(LevelResolutionStatus.NoLevels);

            bool slab = IsSlab(profile);
            RebindLevel best = null;
            double bestDistance = double.PositiveInfinity;
            foreach (RebindLevel level in levels)
            {
                if (level == null || !IsFinite(level.ProjectElevation))
                    return new LevelResolution(LevelResolutionStatus.InvalidHeight);

                double distance = Math.Abs(level.ProjectElevation - referenceHeight);
                if (!IsFinite(distance))
                    return new LevelResolution(LevelResolutionStatus.InvalidHeight);

                if (slab)
                {
                    if (distance < bestDistance)
                    {
                        best = level;
                        bestDistance = distance;
                    }
                }
                else if (level.ProjectElevation <= referenceHeight + Tolerance &&
                         (best == null || level.ProjectElevation > best.ProjectElevation))
                {
                    best = level;
                }
            }

            if (best == null)
                return new LevelResolution(LevelResolutionStatus.NoLevelBelow);

            // Compare all candidates to the final winner so tolerance ties are order-independent.
            int matchingCandidates = 0;
            foreach (RebindLevel level in levels)
            {
                bool tied = slab
                    ? Math.Abs(Math.Abs(level.ProjectElevation - referenceHeight) - bestDistance) <= Tolerance
                    : level.ProjectElevation <= referenceHeight + Tolerance &&
                      Math.Abs(level.ProjectElevation - best.ProjectElevation) <= Tolerance;
                if (tied && ++matchingCandidates > 1)
                    return new LevelResolution(LevelResolutionStatus.Ambiguous);
            }

            return new LevelResolution(LevelResolutionStatus.Resolved, best);
        }

        public static double CompensateOffset(double oldOffset, double oldProjectElevation, double newProjectElevation)
        {
            return oldOffset + oldProjectElevation - newProjectElevation;
        }

        public static double NativeOffsetForPosition(double insertionPointZ, double targetProjectElevation)
        {
            if (!IsFinite(insertionPointZ) || !IsFinite(targetProjectElevation))
                return double.NaN;

            // An unbound task has no reliable old level or offset. Recover its native
            // offset directly from the real insertion point. This is identical for walls
            // and slabs: the slab's 50 mm correction belongs only to selection/reporting.
            double offset = insertionPointZ - targetProjectElevation;
            return IsFinite(offset) ? offset : double.NaN;
        }

        private static bool IsSupported(RebindTaskProfile profile)
        {
            return profile == RebindTaskProfile.WallRectangular || profile == RebindTaskProfile.WallRound || IsSlab(profile);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
