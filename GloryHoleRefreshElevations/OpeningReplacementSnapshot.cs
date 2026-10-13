using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GloryHoleRefreshElevations
{
    // Capture API values before Delete invalidates the old instance.
    // The caller owns both the transaction and the enclosing rollback group.
    internal sealed class OpeningReplacementSnapshot
    {
        private readonly Document document;
        private readonly ElementId oldId;
        private readonly FamilySymbol symbol;
        private readonly Element host;
        private readonly XYZ point;
        private readonly Transform transform;
        private readonly bool handFlipped, facingFlipped, mirrored, pinned, phased;
        private readonly ElementId createdPhase, demolishedPhase, designOption;
        private readonly WorksetId workset;
        private readonly HashSet<ElementId> allowedDeletion;
        private readonly List<ParameterValue> parameters;
        private readonly ParameterValue? floorHostOffset;

        private OpeningReplacementSnapshot(FamilyInstance source)
        {
            document = source.Document;
            oldId = source.Id;
            symbol = source.Symbol;
            host = source.Host;
            point = ((LocationPoint)source.Location).Point;
            transform = source.GetTransform();
            handFlipped = source.HandFlipped; facingFlipped = source.FacingFlipped;
            mirrored = source.Mirrored; pinned = source.Pinned;
            phased = source.HasPhases();
            createdPhase = phased ? source.CreatedPhaseId : ElementId.InvalidElementId;
            demolishedPhase = phased ? source.DemolishedPhaseId : ElementId.InvalidElementId;
            workset = source.WorksetId;
            designOption = source.DesignOption?.Id ?? ElementId.InvalidElementId;
            parameters = source.Parameters.Cast<Parameter>().Where(ShouldCopy).Select(p => new ParameterValue(p)).ToList();
            var hostOffset = source.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
            if (host is Floor && hostOffset != null && hostOffset.HasValue && hostOffset.StorageType == StorageType.Double)
                floorHostOffset = new ParameterValue(hostOffset, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
            allowedDeletion = new HashSet<ElementId> { oldId };
            foreach (var id in source.GetDependentElements(null))
            {
                if (id.Equals(oldId)) continue;
                var dependent = document.GetElement(id);
                if (dependent?.Category?.CategoryType != CategoryType.Annotation)
                    throw OpeningReplacementException.Dependent(id, dependent);
                allowedDeletion.Add(id);
            }
        }

        internal static OpeningReplacementSnapshot Capture(FamilyInstance source) => new OpeningReplacementSnapshot(source);

        internal FamilyInstance Replace(Level target)
        {
            var old = document.GetElement(oldId) ?? throw new InvalidOperationException("Исходное отверстие недоступно");
            if (old.Pinned) old.Pinned = false;
            // Native cutters must not coexist during Revit's implicit regeneration in NewFamilyInstance.
            var deleted = document.Delete(oldId);
            if (deleted.Any(id => !allowedDeletion.Contains(id)))
                throw new InvalidOperationException("Удаление отверстия затронуло посторонний модельный элемент");
            if (!symbol.IsActive) symbol.Activate();
            var replacement = document.Create.NewFamilyInstance(point, symbol, host, target, StructuralType.NonStructural);
            if (replacement == null) throw new InvalidOperationException("Revit не создал замену отверстия");
            if (replacement.Pinned) replacement.Pinned = false;

            foreach (var parameter in parameters) parameter.Apply(replacement);
            // The host is unchanged: preserve its offset verbatim, without any target-level compensation.
            floorHostOffset?.Apply(replacement);
            if (phased)
            {
                if (!replacement.HasPhases()) throw new InvalidOperationException("Не удалось сохранить стадии отверстия");
                // Clear the new instance's demolition stage before assigning the captured creation stage.
                if (!replacement.DemolishedPhaseId.Equals(ElementId.InvalidElementId))
                    replacement.DemolishedPhaseId = ElementId.InvalidElementId;
                replacement.CreatedPhaseId = createdPhase;
                if (!demolishedPhase.Equals(ElementId.InvalidElementId)) replacement.DemolishedPhaseId = demolishedPhase;
            }
            if (!replacement.WorksetId.Equals(workset))
            {
                var parameter = replacement.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.Integer)
                    throw new InvalidOperationException("Не удалось сохранить рабочий набор отверстия");
                parameter.Set(workset.IntegerValue);
            }

            // Level-based wall openings use this native offset. A floor may keep its position
            // through its host instead; do not substitute a host-relative offset for this value.
            var offset = replacement.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
            if (host is Wall && offset != null && offset.StorageType == StorageType.Double && !offset.IsReadOnly)
                offset.Set(RebindLevelRules.NativeOffsetForPosition(point.Z, target.ProjectElevation));
            if (replacement.HandFlipped != handFlipped && !replacement.flipHand())
                throw new InvalidOperationException("Не удалось восстановить разворот отверстия");
            if (replacement.FacingFlipped != facingFlipped && !replacement.flipFacing())
                throw new InvalidOperationException("Не удалось восстановить сторону отверстия");
            // Two rotations reproduce the original full frame, including sloped hosts. Reflection
            // comes from the family flip controls and is checked separately after regeneration.
            Align(replacement, replacement.GetTransform().BasisZ, transform.BasisZ);
            Align(replacement, replacement.GetTransform().BasisX, transform.BasisX);
            return replacement;
        }

        internal void RestorePin(FamilyInstance replacement) { replacement.Pinned = pinned; }

        internal void Verify(FamilyInstance replacement)
        {
            if (!replacement.IsValidObject || replacement.Id.Equals(oldId) || replacement.Pinned != pinned
                || !replacement.WorksetId.Equals(workset)
                || !(replacement.DesignOption?.Id ?? ElementId.InvalidElementId).Equals(designOption)
                || replacement.HasPhases() != phased
                || (phased && (!replacement.CreatedPhaseId.Equals(createdPhase) || !replacement.DemolishedPhaseId.Equals(demolishedPhase)))
                || replacement.HandFlipped != handFlipped || replacement.FacingFlipped != facingFlipped || replacement.Mirrored != mirrored)
                throw new InvalidOperationException("Не удалось сохранить свойства экземпляра отверстия");
            foreach (var parameter in parameters) parameter.Verify(replacement);
            floorHostOffset?.Verify(replacement);
        }

        private void Align(FamilyInstance replacement, XYZ from, XYZ to)
        {
            var a = from.Normalize(); var b = to.Normalize();
            var cross = a.CrossProduct(b);
            double length = cross.GetLength(), dot = Math.Max(-1, Math.Min(1, a.DotProduct(b)));
            if (length <= RebindLevelRules.Tolerance && dot > 0) return;
            var axis = length > RebindLevelRules.Tolerance ? cross.Normalize()
                : a.CrossProduct(Math.Abs(a.DotProduct(XYZ.BasisX)) < 0.9 ? XYZ.BasisX : XYZ.BasisY).Normalize();
            ElementTransformUtils.RotateElement(document, replacement.Id,
                Line.CreateBound(point, point + axis), Math.Atan2(length, dot));
        }

        private static readonly HashSet<string> PlacementParameters = new HashSet<string>(StringComparer.Ordinal)
        {
            "FAMILY_LEVEL_PARAM", "SCHEDULE_LEVEL_PARAM", "INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM", "FAMILY_BASE_LEVEL_PARAM", "FAMILY_TOP_LEVEL_PARAM",
            "INSTANCE_REFERENCE_LEVEL_PARAM", "INSTANCE_ELEVATION_PARAM", "INSTANCE_FREE_HOST_OFFSET_PARAM",
            "INSTANCE_SILL_HEIGHT_PARAM", "INSTANCE_HEAD_HEIGHT_PARAM", "FAMILY_BASE_LEVEL_OFFSET_PARAM",
            "FAMILY_TOP_LEVEL_OFFSET_PARAM", "HOST_ID_PARAM", "ELEM_TYPE_PARAM", "ELEM_FAMILY_PARAM",
            "ELEM_FAMILY_AND_TYPE_PARAM", "ELEM_PARTITION_PARAM", "PHASE_CREATED", "PHASE_DEMOLISHED",
            "DESIGN_OPTION_ID"
        };
        private static readonly Guid BaseHeight = new Guid("9f5f7e49-616e-436f-9acc-5305f34b6933");
        private static readonly Guid ReportOffset = new Guid("515dc061-93ce-40e4-859a-e29224d80a10");

        private static bool ShouldCopy(Parameter parameter)
        {
            if (parameter.IsReadOnly || parameter.StorageType == StorageType.None) return false;
            if (parameter.IsShared && (parameter.GUID == BaseHeight || parameter.GUID == ReportOffset)) return false;
            var definition = parameter.Definition as InternalDefinition;
            return definition == null || !PlacementParameters.Contains(definition.BuiltInParameter.ToString());
        }

        private sealed class ParameterValue
        {
            private readonly Definition definition;
            private readonly Guid? sharedId;
            private readonly BuiltInParameter? nativeId;
            private readonly StorageType storage;
            private readonly bool hasValue;
            private readonly object? value;
            private readonly string name;

            internal ParameterValue(Parameter source, BuiltInParameter? nativeId = null)
            {
                this.nativeId = nativeId;
                definition = source.Definition; name = definition.Name;
                sharedId = source.IsShared ? source.GUID : (Guid?)null;
                storage = source.StorageType; hasValue = source.HasValue;
                value = hasValue ? Read(source) : null;
            }

            private Parameter Get(FamilyInstance target)
            {
                var result = nativeId.HasValue ? target.get_Parameter(nativeId.Value)
                    : sharedId.HasValue ? target.get_Parameter(sharedId.Value) : target.get_Parameter(definition);
                if (result == null || result.StorageType != storage)
                    throw new InvalidOperationException("Не найден параметр для переноса: " + name);
                return result;
            }

            internal void Apply(FamilyInstance target)
            {
                var parameter = Get(target);
                if (Matches(parameter)) return;
                if (!hasValue || parameter.IsReadOnly)
                    throw new InvalidOperationException("Не удалось перенести параметр: " + name);
                switch (storage)
                {
                    case StorageType.Double: parameter.Set((double)value!); break;
                    case StorageType.Integer: parameter.Set((int)value!); break;
                    case StorageType.String: parameter.Set((string)value!); break;
                    case StorageType.ElementId: parameter.Set((ElementId)value!); break;
                    default: throw new InvalidOperationException("Неподдерживаемый параметр: " + name);
                }
                // Family constraints may not expose the final value until the whole parameter
                // batch and placement changes regenerate. The caller verifies all captured
                // values after regeneration and again after commit, within its rollback group.
            }

            internal void Verify(FamilyInstance target)
            {
                var actual = Get(target);
                if (!Matches(actual))
                    throw new OpeningReplacementException("Изменился параметр отверстия: " + name,
                        MismatchDetail(actual));
            }

            private string MismatchDetail(Parameter actual)
            {
                string identity = sharedId.HasValue ? "GUID " + sharedId.Value
                    : "BuiltInParameter " + (nativeId ?? (definition as InternalDefinition)?.BuiltInParameter ?? BuiltInParameter.INVALID);
                string units = storage == StorageType.Double ? " (внутренние единицы Revit)" : string.Empty;
                return "После восстановления параметров и регенерации: «" + name + "», " + identity
                    + "; ожидалось " + Format(value, hasValue) + ", получено " + Format(actual.HasValue ? Read(actual) : null, actual.HasValue)
                    + units + ".";
            }

            private static string Format(object? value, bool hasValue)
            {
                if (!hasValue) return "<не задано>";
                if (value is double number) return number.ToString("G17", System.Globalization.CultureInfo.InvariantCulture);
                if (value is string text) return "«" + text + "»";
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "<null>";
            }

            private bool Matches(Parameter parameter)
            {
                if (hasValue != parameter.HasValue) return false;
                if (!hasValue) return true;
                var actual = Read(parameter);
                if (storage == StorageType.Double)
                    return Math.Abs((double)value! - (double)actual!) <= RebindLevelRules.Tolerance;
                return Equals(value, actual);
            }

            private static object? Read(Parameter parameter)
            {
                switch (parameter.StorageType)
                {
                    case StorageType.Double: return parameter.AsDouble();
                    case StorageType.Integer: return parameter.AsInteger();
                    case StorageType.String: return parameter.AsString();
                    case StorageType.ElementId: return parameter.AsElementId();
                    default: return null;
                }
            }
        }
    }
}
