using Autodesk.Revit.DB;
using System;
using System.Linq;

namespace GloryHoleRefreshElevations
{
    internal static class OpeningReplacementDependencyPolicy
    {
        // Only call for IDs returned by the source opening's GetDependentElements.
        // This narrow heuristic permits opaque, empty API records to follow their owner;
        // it does not identify their Revit subsystem or authorize arbitrary category-null elements.
        // The caller must still validate the complete Delete result and the recreated opening.
        internal static bool CanDeleteWithOpening(Element? dependent)
        {
            if (dependent == null || !dependent.IsValidObject) return false;
            if (dependent.Category?.CategoryType == CategoryType.Annotation) return true;
            return dependent.GetType() == typeof(Element)
                && dependent.Category == null
                && string.IsNullOrWhiteSpace(dependent.Name)
                && dependent.Location == null
                && ElementId.InvalidElementId.Equals(dependent.GetTypeId())
                && !dependent.Parameters.Cast<Parameter>().Any()
                && dependent.GetEntitySchemaGuids().Count == 0
                && !dependent.Pinned
                && ElementId.InvalidElementId.Equals(dependent.GroupId)
                && !dependent.ViewSpecific
                && ElementId.InvalidElementId.Equals(dependent.OwnerViewId);
        }
    }
}
