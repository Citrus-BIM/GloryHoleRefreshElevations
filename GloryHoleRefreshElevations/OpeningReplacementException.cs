using System;
using System.Linq;
using Autodesk.Revit.DB;

namespace GloryHoleRefreshElevations
{
    // Keep the summary reason stable; detailed diagnostics are captured before rollback.
    internal sealed class OpeningReplacementException : InvalidOperationException
    {
        internal OpeningReplacementException(string reason, string detail) : base(reason) { Detail = detail; }
        internal string Detail { get; }

        internal static OpeningReplacementException Dependent(ElementId id, Element? element)
        {
            string detail = "Зависимый элемент ID " + id;
            if (element == null) detail += "; объект недоступен";
            else
            {
                detail += "; класс=" + element.GetType().Name + "; имя=" + element.Name
                    + "; категория=" + (element.Category?.Name ?? "без категории")
                    + " (" + (element.Category?.CategoryType.ToString() ?? "не определена") + ")";
                if (element is FamilyInstance instance)
                    detail += "; семейство=" + instance.Symbol?.Family?.Name + "; тип=" + instance.Symbol?.Name
                        + "; SuperComponent=" + (instance.SuperComponent?.Id.ToString() ?? "нет")
                        + "; Host=" + (instance.Host?.Id.ToString() ?? "нет");
                if (element.GetType() == typeof(Element))
                    detail += "; параметров=" + element.Parameters.Cast<Parameter>().Count()
                        + "; служебных схем=" + element.GetEntitySchemaGuids().Count
                        + "; размещение=" + (element.Location?.GetType().Name ?? "нет")
                        + "; TypeId=" + element.GetTypeId() + "; GroupId=" + element.GroupId
                        + "; закреплён=" + element.Pinned + "; ViewSpecific=" + element.ViewSpecific
                        + "; OwnerViewId=" + element.OwnerViewId;
            }
            return new OpeningReplacementException("Пересоздание затронуло бы зависимый модельный или служебный элемент", detail);
        }
    }
}
