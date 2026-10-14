using System;
using Autodesk.Revit.DB;

namespace GloryHoleRefreshElevations
{
    internal static class OpeningElevationService
    {
        private static readonly Guid FamilyCode = new Guid("40bbbf16-4b6a-45e8-9896-620bb448db96");
        private static readonly Guid BaseHeight = new Guid("9f5f7e49-616e-436f-9acc-5305f34b6933");
        private static readonly Guid ReportOffset = new Guid("515dc061-93ce-40e4-859a-e29224d80a10");
        private const string BaseHeightName = "Рзм.ВысотаБазовогоУровня";
        private const string ReportOffsetName = "ADSK_Размер_Смещение от уровня";
        private const string NativeOffsetName = "Смещение от уровня";

        public static RebindTaskProfile GetProfile(FamilyInstance instance)
        {
            if (instance == null || !instance.IsValidObject || instance.Symbol?.Family == null)
                return RebindTaskProfile.Unsupported;
            Parameter code = instance.Symbol.get_Parameter(FamilyCode);
            if (code != null && code.StorageType != StorageType.String)
                return RebindTaskProfile.Unsupported;
            return OpeningRebindingRules.GetProfile(code?.AsString(), instance.Symbol.Family.Name);
        }

        public static void ValidateParameters(FamilyInstance instance)
        {
            RequireProfile(instance);
            RequiredDouble(instance.get_Parameter(ReportOffset), ReportOffsetName);
            Parameter baseHeight = instance.get_Parameter(BaseHeight);
            bool window = instance.Category != null
                && instance.Category.Id.Equals(new ElementId(BuiltInCategory.OST_Windows));
            if (baseHeight != null || !window)
                RequiredDouble(baseHeight, BaseHeightName);
        }

        public static double GetReportOffset(FamilyInstance instance, RebindTaskProfile profile, Level level)
        {
            RequireContext(instance, profile, level);
            if (RebindLevelRules.IsSlab(profile))
            {
                var floor = (Floor)instance.Host;
                var hostLevel = instance.Document.GetElement(floor.LevelId) as Level;
                if (hostLevel == null || !hostLevel.IsValidObject)
                    throw new InvalidOperationException("Не задан уровень перекрытия — основы отверстия.");
                double offset = ReadDouble(floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM),
                    "Смещение перекрытия от уровня");
                if (!IsFinite(hostLevel.ProjectElevation))
                    throw new InvalidOperationException("Некорректная отметка уровня перекрытия.");
                try
                {
                    return OpeningRebindingRules.FloorReportOffset(hostLevel.ProjectElevation, offset, level.ProjectElevation);
                }
                catch (ArgumentException ex) { throw new InvalidOperationException(ex.Message, ex); }
            }

            double nativeOffset = ReadDouble(instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM), NativeOffsetName);
            var location = instance.Location as LocationPoint;
            if (location == null || location.Point == null
                || !Near(location.Point.Z, level.ProjectElevation + nativeOffset))
                throw new InvalidOperationException("Смещение отверстия от уровня не соответствует его фактическому положению.");
            return nativeOffset;
        }

        // The caller owns the transaction and rolls it back if any validation or write fails.
        public static void Refresh(FamilyInstance instance, bool roundHeight, double mmIncrement)
        {
            ValidateParameters(instance);
            var level = instance.Document.GetElement(instance.LevelId) as Level;
            RebindTaskProfile profile = GetProfile(instance);
            double offset = GetReportOffset(instance, profile, level);
            Parameter baseHeight = instance.get_Parameter(BaseHeight);
            if (baseHeight != null && !IsFinite(level.Elevation))
                throw new InvalidOperationException("Некорректная отметка базового уровня отверстия.");

            if (roundHeight && !RebindLevelRules.IsSlab(profile))
            {
                if (!IsFinite(mmIncrement) || mmIncrement < 0)
                    throw new InvalidOperationException("Шаг округления высоты должен быть неотрицательным числом.");
                double rounded = mmIncrement == 0 ? Math.Round(offset, 6)
                    : Math.Round(Math.Round(offset * 304.8, 2) / mmIncrement) * mmIncrement / 304.8;
                WriteDouble(instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM), NativeOffsetName, rounded);
                instance.Document.Regenerate();
                offset = GetReportOffset(instance, profile, level);
            }

            if (baseHeight != null) WriteDouble(baseHeight, BaseHeightName, level.Elevation, true);
            WriteDouble(instance.get_Parameter(ReportOffset), ReportOffsetName, offset, true);
            VerifyReports(instance, profile, level);
        }

        public static void VerifyReports(FamilyInstance instance, RebindTaskProfile profile, Level level)
        {
            ValidateParameters(instance);
            double expected = GetReportOffset(instance, profile, level);
            if (!Near(ReadDouble(instance.get_Parameter(ReportOffset), ReportOffsetName), expected))
                throw new InvalidOperationException("Записанное смещение отверстия не соответствует его основе и уровню.");
            Parameter baseHeight = instance.get_Parameter(BaseHeight);
            if (baseHeight != null && !Near(ReadDouble(baseHeight, BaseHeightName), level.Elevation))
                throw new InvalidOperationException("Записанная отметка базового уровня отверстия не соответствует уровню.");
        }

        private static RebindTaskProfile RequireProfile(FamilyInstance instance)
        {
            RebindTaskProfile profile = GetProfile(instance);
            if (profile == RebindTaskProfile.Unsupported)
                throw new InvalidOperationException("Неизвестный или противоречивый профиль семейства готового отверстия.");
            return profile;
        }

        private static void RequireContext(FamilyInstance instance, RebindTaskProfile profile, Level level)
        {
            if (RequireProfile(instance) != profile)
                throw new InvalidOperationException("Профиль отверстия не соответствует выбранному способу расчёта отметки.");
            if (level == null || !level.IsValidObject || !level.Id.Equals(instance.LevelId)
                || !Equals(level.Document, instance.Document) || !IsFinite(level.ProjectElevation))
                throw new InvalidOperationException("Не задан корректный фактический уровень отверстия. Включите «Перепривязать к уровням».");
            Element host = instance.Host;
            if (host == null || !host.IsValidObject || !Equals(host.Document, instance.Document))
                throw new InvalidOperationException("У отверстия отсутствует действующая основа.");
            if (RebindLevelRules.IsSlab(profile) ? !(host is Floor) : !(host is Wall))
                throw new InvalidOperationException("Тип основы отверстия не соответствует семейству: требуется "
                    + (RebindLevelRules.IsSlab(profile) ? "перекрытие." : "стена."));
        }

        private static Parameter RequiredDouble(Parameter parameter, string name)
        {
            if (parameter == null)
                throw new InvalidOperationException("Отсутствует параметр «" + name + "» в семействе отверстия.");
            if (parameter.StorageType != StorageType.Double)
                throw new InvalidOperationException("Неверный тип параметра «" + name + "».");
            return parameter;
        }

        private static Parameter WritableDouble(Parameter parameter, string name)
        {
            RequiredDouble(parameter, name);
            if (parameter.IsReadOnly)
                throw new InvalidOperationException("Параметр «" + name + "» недоступен для записи.");
            return parameter;
        }

        private static double ReadDouble(Parameter parameter, string name)
        {
            RequiredDouble(parameter, name);
            if (!parameter.HasValue || !IsFinite(parameter.AsDouble()))
                throw new InvalidOperationException("Не задано корректное значение параметра «" + name + "».");
            return parameter.AsDouble();
        }

        private static void WriteDouble(Parameter parameter, string name, double value, bool allowReadonlyEquivalent = false)
        {
            RequiredDouble(parameter, name);
            if (!IsFinite(value))
                throw new InvalidOperationException("Некорректное значение для параметра «" + name + "».");
            // Formula-driven report values may already be correct after regeneration.
            // No write is needed in that case, even when Revit marks the parameter read-only.
            if (parameter.HasValue && (parameter.AsDouble().Equals(value)
                || (allowReadonlyEquivalent && parameter.IsReadOnly && Near(parameter.AsDouble(), value)))) return;
            WritableDouble(parameter, name);
            if (!parameter.Set(value))
                throw new InvalidOperationException("Revit не принял значение параметра «" + name + "».");
        }

        private static bool Near(double first, double second) => IsFinite(first) && IsFinite(second)
            && Math.Abs(first - second) <= RebindLevelRules.Tolerance;
        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
