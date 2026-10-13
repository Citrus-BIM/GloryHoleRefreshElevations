using System;
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
            }
            return new OpeningReplacementException("Пересоздание затронуло бы зависимый модельный или служебный элемент", detail);
        }
    }
}
