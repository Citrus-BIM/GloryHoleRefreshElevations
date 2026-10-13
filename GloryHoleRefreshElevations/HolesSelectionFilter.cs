using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;

namespace GloryHoleRefreshElevations
{
    public class HolesSelectionFilter : ISelectionFilter
    {
        private readonly Guid gh_FamilyCode = new Guid("40bbbf16-4b6a-45e8-9896-620bb448db96");

        // Допустимые семейства
        private readonly HashSet<string> validFamilies = new HashSet<string>
        {
            "Пересечение_Стена_Прямоугольное",
            "Пересечение_Стена_Круглое",
            "Пересечение_Плита_Прямоугольное",
            "Пересечение_Плита_Круглое",
            "Отверстие_Стена_Прямоугольное",
            "Отверстие_Стена_Круглое",
            "Отверстие_Плита_Прямоугольное",
            "Отверстие_Плита_Круглое",
            "Гильза_Стена",
            "Гильза_Плита"
        };

        // Допустимые коды отверстий
        private readonly HashSet<string> validCodes = new HashSet<string>
        {
            "111", "112", "113", "114", "115", "116",  // Отверстия в стенах
            "121", "122", "123", "124",  // Отверстия в плитах
            "126", "221", "222", "223", "224"   // Готовые отверстия
        };

        public bool AllowElement(Element elem)
        {
            if (elem is FamilyInstance familyInstance && IsCategory(elem, BuiltInCategory.OST_GenericModel))
            {
                string familyName = familyInstance.Symbol.FamilyName;
                string familyCode = familyInstance.Symbol.get_Parameter(gh_FamilyCode)?.AsString();

                // 1. Если семейство есть в списке допустимых — сразу принимаем
                if (validFamilies.Contains(familyName))
                {
                    return true;
                }

                // 2. Если у элемента есть допустимый код — принимаем
                if (!string.IsNullOrEmpty(familyCode) && validCodes.Contains(familyCode))
                {
                    return true;
                }
            }

            // Проверяем оконные отверстия (категория OST_Windows)
            if (elem is FamilyInstance windowInstance && IsCategory(elem, BuiltInCategory.OST_Windows))
            {
                string familyName = windowInstance.Symbol.FamilyName;

                if (OpeningRebindingRules.IsWindowFamily(familyName))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsCategory(Element element, BuiltInCategory builtInCategory)
        {
            var categoryId = element?.Category?.Id;
            if (categoryId == null)
                return false;

#if R2019 || R2020 || R2021 || R2022 || R2023 || R2024
            return categoryId.IntegerValue == (int)builtInCategory;
#else
            return categoryId.Value == new ElementId(builtInCategory).Value;
#endif
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            throw new NotImplementedException();
        }
    }
}
