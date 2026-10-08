using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace GloryHoleRefreshElevations
{
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    class GloryHoleRefreshElevationsCommand : IExternalCommand
    {
        Guid heightOfBaseLevelGuid = new Guid("9f5f7e49-616e-436f-9acc-5305f34b6933");
        Guid levelOffsetGuid = new Guid("515dc061-93ce-40e4-859a-e29224d80a10");
        Guid intersectionPointWidthGuid = new Guid("8f2e4f93-9472-4941-a65d-0ac468fd6a5d");
        Guid intersectionPointHeightGuid = new Guid("da753fe3-ecfa-465b-9a2c-02f55d0c2ff1");

        Guid gh_FamilyCode = new Guid("40bbbf16-4b6a-45e8-9896-620bb448db96");

        // Список допустимых значений параметра типа
        HashSet<string> validCodes = new HashSet<string>
        {
            "111", "113", "115", // Пересечение_Стена_Прямоугольное
            "112", "114", "116", // Пересечение_Стена_Круглое
            "121", "123",        // Пересечение_Плита_Прямоугольное
            "122", "124",        // Пересечение_Плита_Круглое
            "126",               // Отверстие_Стена_Круглое
            "221", "223",        // Отверстие_Плита_Прямоугольное
            "222", "224"         // Отверстие_Плита_Круглое
        };

        // Список допустимых семейств
        HashSet<string> validFamilies = new HashSet<string>
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

        private const double MM_PER_FOOT = 304.8;
        private const double PERP_TOL = 1e-3;
        private const double PREFER_HOST_MAX_DISTANCE_MM = 2000; 

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try { return ExecuteCore(commandData); }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex)
            {
                var dialog = new TaskDialog("Обновить отметки")
                {
                    MainInstruction = "Не удалось выполнить обновление.",
                    MainContent = ex is NullReferenceException
                        ? "Не удалось прочитать данные элементов или настроек. Подробности доступны ниже."
                        : ex.Message,
                    ExpandedContent = ex.ToString(),
                    MainIcon = TaskDialogIcon.TaskDialogIconWarning,
                    CommonButtons = TaskDialogCommonButtons.Close
                };
                dialog.Show();
                return Result.Cancelled;
            }
        }

        private Result ExecuteCore(ExternalCommandData commandData)
        {
            try { _ = GetPluginStartInfo(); } catch { }

            Document doc = commandData.Application.ActiveUIDocument.Document;
            Selection sel = commandData.Application.ActiveUIDocument.Selection;

            // Оси: хост + все загруженные связи (в координатах хоста)
            List<GridLine2D> gridLines = CollectAllGridLinesInHostXY(doc);
            double preferHostMaxDistFt = MmToFt(PREFER_HOST_MAX_DISTANCE_MM);

            GloryHoleRefreshElevationsWPF gloryHoleRefreshElevationsWPF = new GloryHoleRefreshElevationsWPF(LevelRebindingService.CollectLevels(doc));
            gloryHoleRefreshElevationsWPF.ShowDialog();
            if (gloryHoleRefreshElevationsWPF.DialogResult != true)
                return Result.Cancelled;

            string refreshElevationsOptionButtonName = gloryHoleRefreshElevationsWPF.RefreshElevationsOptionButtonName;
            string roundHolesPositionButtonName = gloryHoleRefreshElevationsWPF.RoundHolesPositionButtonName;
            double roundHolePositionIncrement = gloryHoleRefreshElevationsWPF.RoundHolePositionIncrement;

            string roundHolesLocationButtonName = gloryHoleRefreshElevationsWPF.RoundHolesLocationButtonName;
            double roundHoleLocationIncrement = gloryHoleRefreshElevationsWPF.RoundHoleLocationIncrement;

            List<FamilyInstance> intersectionPointFamilyInstanceList = null;
            List<FamilyInstance> intersectionPointWeandrevitList = null;

            if (refreshElevationsOptionButtonName == "rbt_AllProject")
            {
                intersectionPointFamilyInstanceList = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_GenericModel)
                    .OfClass(typeof(FamilyInstance))
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .Where(ip =>
                    {
                        string familyName = ip.Symbol.Family.Name;
                        string familyCode = ip.Symbol.get_Parameter(gh_FamilyCode)?.AsString();

                        if (validFamilies.Contains(familyName))
                            return true;

                        if (!string.IsNullOrWhiteSpace(familyCode) && validCodes.Contains(familyCode))
                            return true;

                        return false;
                    })
                    .ToList();

                intersectionPointWeandrevitList = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .OfClass(typeof(FamilyInstance))
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .Where(ip =>
                        ip.Symbol.Family.Name == "231_Отверстие прямоугольное (Окно_Стена)" ||
                        ip.Symbol.Family.Name == "231_Отверстие круглое с гильзой в стене (Окно_Стена)")
                    .ToList();
            }
            else
            {
                HolesSelectionFilter holesSelectionFilter = new HolesSelectionFilter();
                IList<Reference> selHoles = null;

                try
                {
                    selHoles = sel.PickObjects(ObjectType.Element, holesSelectionFilter, "Выберите отверстия!");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }

                intersectionPointFamilyInstanceList = new List<FamilyInstance>();
                intersectionPointWeandrevitList = new List<FamilyInstance>();

                foreach (Reference roomRef in selHoles)
                {
                    var fi = doc.GetElement(roomRef) as FamilyInstance;
                    if (fi == null) continue;

                    if (IsCategory(fi, BuiltInCategory.OST_GenericModel))
                        intersectionPointFamilyInstanceList.Add(fi);
                    else if (IsCategory(fi, BuiltInCategory.OST_Windows))
                        intersectionPointWeandrevitList.Add(fi);
                }
            }

            Action<FamilyInstance> refresh = instance =>
            {
                if (IsCategory(instance, BuiltInCategory.OST_Windows))
                    RefreshWeandrevit(instance, roundHolesPositionButtonName, roundHolePositionIncrement);
                else
                    RefreshIntersection(doc, instance, gridLines, preferHostMaxDistFt,
                        roundHolesPositionButtonName, roundHolePositionIncrement,
                        roundHolesLocationButtonName, roundHoleLocationIncrement);
            };
            var report = LevelRebindingService.Execute(doc,
                intersectionPointFamilyInstanceList.Concat(intersectionPointWeandrevitList),
                gloryHoleRefreshElevationsWPF.RebindToLevels,
                refreshElevationsOptionButtonName == "rbt_SelectedItems",
                gloryHoleRefreshElevationsWPF.RebindToSelectedLevel,
                gloryHoleRefreshElevationsWPF.SelectedRebindLevelUniqueId, refresh,
                roundHolesPositionButtonName == "radioButton_RoundHolesPositionYes",
                roundHolesLocationButtonName == "radioButton_RoundHolesLocationYes");
            ShowReport(report, sel, gloryHoleRefreshElevationsWPF.RebindToLevels);
            return report.Failure == null ? Result.Succeeded : Result.Cancelled;
        }

        private void RefreshIntersection(Document doc, FamilyInstance intersectionPoint,
            List<GridLine2D> gridLines, double preferHostMaxDistFt,
            string roundHolesPositionButtonName, double roundHolePositionIncrement,
            string roundHolesLocationButtonName, double roundHoleLocationIncrement)
        {
            string familyCode = intersectionPoint.Symbol.get_Parameter(gh_FamilyCode)?.AsString();

            // A family created by a replacement script can have no native level.
            // In ordinary refresh this is a local skip; the optional rebind phase repairs it first.
            var baseLevel = doc.GetElement(intersectionPoint.LevelId) as Level;
            if (baseLevel == null)
                throw new InvalidOperationException("Не задан уровень. Включите «Перепривязать к уровням».");
            RequiredParameter(intersectionPoint.get_Parameter(levelOffsetGuid), "ADSK_Размер_Смещение от уровня");
            RequiredParameter(intersectionPoint.get_Parameter(heightOfBaseLevelGuid),
                "Рзм.ВысотаБазовогоУровня").Set(baseLevel.Elevation);

            if (!string.IsNullOrWhiteSpace(familyCode))
            {
                // 111..116 — пересечения в стенах
                if (familyCode == "111" || familyCode == "112" || familyCode == "113" || familyCode == "114" || familyCode == "115" || familyCode == "116")
                {
                    if (intersectionPoint.get_Parameter(levelOffsetGuid) != null)
                    {
                        double elev = NativeOffset(intersectionPoint, BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                        if (roundHolesPositionButtonName == "radioButton_RoundHolesPositionYes")
                        {
                            elev = RoundToIncrement(elev, roundHolePositionIncrement);
                            intersectionPoint.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM).Set(elev);
                        }
                        intersectionPoint.get_Parameter(levelOffsetGuid).Set(elev);
                    }

                    if (roundHolesLocationButtonName == "radioButton_RoundHolesLocationYes")
                    {
                        XYZ originIntersection = (intersectionPoint.Location as LocationPoint)?.Point;
                        if (originIntersection != null)
                        {
                            bool alignByEdges = (familyCode == "111" || familyCode == "113" || familyCode == "115");
                            RoundHolesPositionInWalls(doc, gridLines, preferHostMaxDistFt, roundHoleLocationIncrement, originIntersection, intersectionPoint, alignByEdges);
                        }
                    }
                }
                // 121..124 — пересечения в плитах
                else if (familyCode == "121" || familyCode == "122" || familyCode == "123" || familyCode == "124")
                {
                    if (intersectionPoint.get_Parameter(levelOffsetGuid) != null)
                    {
                        double elev = NativeOffset(intersectionPoint, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM) - 50 / 304.8;
                        intersectionPoint.get_Parameter(levelOffsetGuid).Set(elev);
                    }

                    if (roundHolesLocationButtonName == "radioButton_RoundHolesLocationYes")
                    {
                        XYZ originIntersection = (intersectionPoint.Location as LocationPoint)?.Point;
                        if (originIntersection != null)
                        {
                            bool alignByEdges = (familyCode == "121" || familyCode == "123");
                            RoundHolesPositionInSlabs(doc, gridLines, preferHostMaxDistFt, roundHoleLocationIncrement, originIntersection, intersectionPoint, alignByEdges);
                        }
                    }
                }
                // 221..224 — отверстия в плитах с Host
                else if (familyCode == "221" || familyCode == "222" || familyCode == "223" || familyCode == "224")
                {
                    if (intersectionPoint.Host != null)
                    {
                        if (intersectionPoint.get_Parameter(levelOffsetGuid) != null)
                        {
                            double elev = FloorOffset(intersectionPoint);
                            intersectionPoint.get_Parameter(levelOffsetGuid).Set(elev);
                        }

                        if (roundHolesLocationButtonName == "radioButton_RoundHolesLocationYes")
                        {
                            XYZ originIntersection = (intersectionPoint.Location as LocationPoint)?.Point;
                            if (originIntersection != null)
                            {
                                bool alignByEdges = (familyCode == "221" || familyCode == "223");
                                RoundHolesPositionInSlabs(doc, gridLines, preferHostMaxDistFt, roundHoleLocationIncrement, originIntersection, intersectionPoint, alignByEdges);
                            }
                        }
                    }
                    else
                    {
                        OpeningRefreshRules.RequireHost(false);
                    }
                }
            }
            else
            {
                // fallback по имени семейства (как было)
                if (intersectionPoint.Symbol.FamilyName == "Пересечение_Плита_Прямоугольное"
                    || intersectionPoint.Symbol.FamilyName == "Пересечение_Плита_Круглое")
                {
                    if (intersectionPoint.get_Parameter(levelOffsetGuid) != null)
                    {
                        double elev = NativeOffset(intersectionPoint, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM) - 50 / 304.8;
                        intersectionPoint.get_Parameter(levelOffsetGuid).Set(elev);
                    }

                    if (roundHolesLocationButtonName == "radioButton_RoundHolesLocationYes")
                    {
                        XYZ originIntersection = (intersectionPoint.Location as LocationPoint)?.Point;
                        if (originIntersection != null)
                        {
                            bool alignByEdges = intersectionPoint.Symbol.FamilyName == "Пересечение_Плита_Прямоугольное";
                            RoundHolesPositionInSlabs(doc, gridLines, preferHostMaxDistFt, roundHoleLocationIncrement, originIntersection, intersectionPoint, alignByEdges);
                        }
                    }
                }
                else if (intersectionPoint.Symbol.FamilyName == "Отверстие_Плита_Прямоугольное"
                    || intersectionPoint.Symbol.FamilyName == "Отверстие_Плита_Круглое"
                    || intersectionPoint.Symbol.FamilyName == "Гильза_Плита")
                {
                    if (intersectionPoint.Host != null)
                    {
                        if (intersectionPoint.get_Parameter(levelOffsetGuid) != null)
                        {
                            double elev = FloorOffset(intersectionPoint);
                            intersectionPoint.get_Parameter(levelOffsetGuid).Set(elev);
                        }

                        if (roundHolesLocationButtonName == "radioButton_RoundHolesLocationYes")
                        {
                            XYZ originIntersection = (intersectionPoint.Location as LocationPoint)?.Point;
                            if (originIntersection != null)
                            {
                                bool alignByEdges = intersectionPoint.Symbol.FamilyName == "Отверстие_Плита_Прямоугольное";
                                RoundHolesPositionInSlabs(doc, gridLines, preferHostMaxDistFt, roundHoleLocationIncrement, originIntersection, intersectionPoint, alignByEdges);
                            }
                        }
                    }
                    else
                    {
                        OpeningRefreshRules.RequireHost(false);
                    }
                }
                else
                {
                    if (intersectionPoint.get_Parameter(levelOffsetGuid) != null)
                    {
                        double elev = NativeOffset(intersectionPoint, BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                        if (roundHolesPositionButtonName == "radioButton_RoundHolesPositionYes")
                        {
                            elev = RoundToIncrement(elev, roundHolePositionIncrement);
                            intersectionPoint.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM).Set(elev);
                        }
                        intersectionPoint.get_Parameter(levelOffsetGuid).Set(elev);
                    }

                    if (roundHolesLocationButtonName == "radioButton_RoundHolesLocationYes")
                    {
                        XYZ originIntersection = (intersectionPoint.Location as LocationPoint)?.Point;
                        if (originIntersection != null)
                        {
                            bool alignByEdges =
                                intersectionPoint.Symbol.FamilyName == "Пересечение_Стена_Прямоугольное" ||
                                intersectionPoint.Symbol.FamilyName == "Отверстие_Стена_Прямоугольное";

                            RoundHolesPositionInWalls(doc, gridLines, preferHostMaxDistFt, roundHoleLocationIncrement, originIntersection, intersectionPoint, alignByEdges);
                        }
                    }
                }
            }
        }

        private void RefreshWeandrevit(FamilyInstance intersectionPoint,
            string roundHolesPositionButtonName, double roundHolePositionIncrement)
        {
            RequiredParameter(intersectionPoint.get_Parameter(levelOffsetGuid), "ADSK_Размер_Смещение от уровня");
            double elev = NativeOffset(intersectionPoint, BuiltInParameter.INSTANCE_ELEVATION_PARAM);
            if (intersectionPoint.get_Parameter(levelOffsetGuid) != null)
            {
                if (roundHolesPositionButtonName == "radioButton_RoundHolesPositionYes")
                {
                    elev = RoundToIncrement(elev, roundHolePositionIncrement);
                    intersectionPoint.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM).Set(elev);
                    intersectionPoint.get_Parameter(levelOffsetGuid).Set(elev);
                }
                else
                {
                    intersectionPoint.get_Parameter(levelOffsetGuid).Set(elev);
                }
            }
        }

        private static Parameter RequiredParameter(Parameter? parameter, string name)
        {
            if (parameter == null)
                throw new InvalidOperationException("Отсутствует параметр «" + name + "». Проверьте семейство болванки.");
            if (parameter.StorageType != StorageType.Double)
                throw new InvalidOperationException("Неверный тип параметра «" + name + "».");
            return parameter;
        }

        private static double NativeOffset(FamilyInstance instance, BuiltInParameter parameter)
        {
            return RequiredParameter(instance.get_Parameter(parameter), "Смещение от уровня").AsDouble();
        }

        private static double FloorOffset(FamilyInstance instance)
        {
            return RequiredParameter(instance.Host?.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM),
                "Смещение перекрытия от уровня").AsDouble();
        }

        private static void ShowReport(RebindReport report, Selection selection, bool rebind)
        {
            if (!rebind && report.Failure == null && report.UpdaterRestoreFailure == null && report.SkippedIds.Count == 0)
                return;

            string? content = report.Failure;
            if (content == null)
            {
                content = rebind
                    ? "Перепривязано: " + report.Changed + " (без исходного уровня: " + report.Restored + ").\n"
                        + "Уровень уже подходил: " + report.Unchanged + ".\n"
                    : "Обновлено: " + report.Refreshed + ".\n";
                if (rebind && report.Refreshed > 0)
                    content += "Обновлены только отметки готовых отверстий: " + report.Refreshed + ".\n";
                content += "Пропущено: " + report.SkippedIds.Count + ".";
                if (report.Reasons.Count > 0)
                    content += "\n\n" + string.Join("\n", report.Reasons.Take(6).Select(p => p.Value + " — " + p.Key));
                if (report.Reasons.Count > 6) content += "\nОстальные причины — в подробностях.";
                if (rebind) content += "\n\nПосле перепривязки проверьте привязку к уровням.";
            }
            if (report.UpdaterRestoreFailure != null)
                content += "\n\nНе удалось восстановить автоматическое обновление: "
                    + report.UpdaterRestoreFailure + "\nПерезапустите Revit перед дальнейшей работой.";

            var dialog = new TaskDialog("Обновить отметки")
            {
                MainInstruction = report.Failure == null ? "Обновление завершено" : "Обновление отменено",
                MainContent = content,
                ExpandedContent = string.Join("\n", report.Details),
                CommonButtons = TaskDialogCommonButtons.Close
            };
            if (report.Failure == null && report.SkippedIds.Count > 0)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Выделить пропущенные элементы");
            if (dialog.Show() == TaskDialogResult.CommandLink1)
            {
                // Model changes are already committed; a view/selection error must not undo them.
                try { selection.SetElementIds(report.SkippedIds); }
                catch (Exception ex) { TaskDialog.Show("Обновить отметки", "Не удалось выделить элементы: " + ex.Message); }
            }
        }

        private double RoundToIncrement(double value, double increment)
        {
            if (increment == 0)
                return Math.Round(value, 6);

            return Math.Round(Math.Round(value * 304.8, 2) / increment) * increment / 304.8;
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

        private static double MmToFt(double mm) => mm / MM_PER_FOOT;

        // ====== Grid helpers (Host + Links) ======

        private sealed class GridLine2D
        {
            public Line Line2D { get; }
            public bool IsHost { get; }
            public ElementId SourceGridId { get; }

            public GridLine2D(Line line2D, bool isHost, ElementId sourceGridId)
            {
                Line2D = line2D;
                IsHost = isHost;
                SourceGridId = sourceGridId;
            }
        }

        private static XYZ ToXY(XYZ p) => new XYZ(p.X, p.Y, 0);

        private static XYZ SafeNormalize2D(XYZ v, XYZ fallback)
        {
            var v2 = new XYZ(v.X, v.Y, 0);
            return v2.GetLength() < 1e-9 ? fallback : v2.Normalize();
        }

        private static XYZ ProjectPointToUnboundLine2D(XYZ p2d, XYZ lineOrigin2d, XYZ dir2dUnit)
        {
            double t = (p2d - lineOrigin2d).DotProduct(dir2dUnit);
            return lineOrigin2d + dir2dUnit * t;
        }

        private static double DistancePointToUnboundLine2D(XYZ p2d, XYZ lineOrigin2d, XYZ dir2dUnit)
        {
            var proj = ProjectPointToUnboundLine2D(p2d, lineOrigin2d, dir2dUnit);
            return (proj - p2d).GetLength();
        }

        private static GridLine2D ToUnbound2D(Line boundLine, bool isHost, ElementId gridId)
        {
            var p0 = ToXY(boundLine.GetEndPoint(0));
            var p1 = ToXY(boundLine.GetEndPoint(1));
            var dir = (p1 - p0);
            if (dir.GetLength() < 1e-9) return null;

            var unbound = Line.CreateUnbound(p0, dir.Normalize());
            return new GridLine2D(unbound, isHost, gridId);
        }

        private static List<GridLine2D> CollectAllGridLinesInHostXY(Document hostDoc)
        {
            var res = new List<GridLine2D>();

            // Host grids
            var hostGrids = new FilteredElementCollector(hostDoc)
                .OfCategory(BuiltInCategory.OST_Grids)
                .OfClass(typeof(Grid))
                .Cast<Grid>();

            foreach (var g in hostGrids)
            {
                if (g.Curve is not Line l) continue;
                var gl = ToUnbound2D(l, isHost: true, g.Id);
                if (gl != null) res.Add(gl);
            }

            // Link grids
            var links = new FilteredElementCollector(hostDoc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>();

            foreach (var link in links)
            {
                var linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;

                Transform tr = link.GetTotalTransform();

                var linkGrids = new FilteredElementCollector(linkDoc)
                    .OfCategory(BuiltInCategory.OST_Grids)
                    .OfClass(typeof(Grid))
                    .Cast<Grid>();

                foreach (var g in linkGrids)
                {
                    if (g.Curve is not Line l) continue;

                    XYZ p0 = tr.OfPoint(l.GetEndPoint(0));
                    XYZ p1 = tr.OfPoint(l.GetEndPoint(1));

                    var p0xy = ToXY(p0);
                    var p1xy = ToXY(p1);
                    var dir = (p1xy - p0xy);
                    if (dir.GetLength() < 1e-9) continue;

                    var unbound2d = Line.CreateUnbound(p0xy, dir.Normalize());
                    res.Add(new GridLine2D(unbound2d, isHost: false, g.Id));
                }
            }

            return res;
        }

        private static GridLine2D GetClosestPerpendicularGridLinePreferHost(
            List<GridLine2D> allLines,
            XYZ pointWorld,
            XYZ orientationWorld,
            double preferHostMaxDistanceFt)
        {
            if (allLines == null || allLines.Count == 0) return null;

            XYZ p = ToXY(pointWorld);
            XYZ o = SafeNormalize2D(orientationWorld, XYZ.BasisX);

            GridLine2D bestHost = null;
            double bestHostDist = double.MaxValue;

            GridLine2D bestAny = null;
            double bestAnyDist = double.MaxValue;

            foreach (var gl in allLines)
            {
                if (gl?.Line2D == null) continue;

                XYZ dir = SafeNormalize2D(gl.Line2D.Direction, XYZ.BasisY);

                double dot = Math.Abs(dir.DotProduct(o));
                if (dot > PERP_TOL) continue;

                XYZ lineOrigin = ToXY(gl.Line2D.Origin);
                double dist = DistancePointToUnboundLine2D(p, lineOrigin, dir);

                if (dist < bestAnyDist)
                {
                    bestAnyDist = dist;
                    bestAny = gl;
                }

                if (gl.IsHost && dist < bestHostDist)
                {
                    bestHostDist = dist;
                    bestHost = gl;
                }
            }

            if (bestHost != null && bestHostDist <= preferHostMaxDistanceFt)
                return bestHost;

            return bestAny ?? bestHost;
        }

        // ====== Alignment ======

        private void RoundHolesPositionInWalls(
            Document doc,
            List<GridLine2D> gridLines,
            double preferHostMaxDistFt,
            double roundHolePosition,
            XYZ originIntersectionCurve,
            FamilyInstance intersectionPoint,
            bool alignByEdges)
        {
            doc.Regenerate();

            var closest = GetClosestPerpendicularGridLinePreferHost(
                gridLines, originIntersectionCurve, intersectionPoint.HandOrientation, preferHostMaxDistFt);

            if (closest?.Line2D == null)
                return;

            XYZ originXY = ToXY(originIntersectionCurve);
            XYZ handXY = SafeNormalize2D(intersectionPoint.HandOrientation, XYZ.BasisX);

            XYZ lineOrigin = ToXY(closest.Line2D.Origin);
            XYZ lineDir = SafeNormalize2D(closest.Line2D.Direction, XYZ.BasisY);

            XYZ pointToProject = originXY;

            if (alignByEdges)
            {
                double width = intersectionPoint.get_Parameter(intersectionPointWidthGuid)?.AsDouble() ?? 0;
                if (width > 1e-9)
                {
                    XYZ leftEdgeXY = originXY - handXY * (width / 2);
                    XYZ rightEdgeXY = originXY + handXY * (width / 2);

                    XYZ leftProj = ProjectPointToUnboundLine2D(leftEdgeXY, lineOrigin, lineDir);
                    XYZ rightProj = ProjectPointToUnboundLine2D(rightEdgeXY, lineOrigin, lineDir);

                    double leftDist = (leftProj - leftEdgeXY).GetLength();
                    double rightDist = (rightProj - rightEdgeXY).GetLength();

                    pointToProject = leftDist <= rightDist ? leftEdgeXY : rightEdgeXY;
                }
            }

            XYZ proj = ProjectPointToUnboundLine2D(pointToProject, lineOrigin, lineDir);
            double currentDistance = (proj - pointToProject).GetLength();

            double targetDistance = RoundToIncrement(currentDistance, roundHolePosition);
            double delta = targetDistance - currentDistance;

            if (Math.Abs(delta) < 1e-9)
                return;

            XYZ moveDir = proj - pointToProject;
            if (moveDir.GetLength() < 1e-9)
                return;

            XYZ finalMoveXY = moveDir.Normalize() * delta;

            ElementTransformUtils.MoveElement(doc, intersectionPoint.Id, -finalMoveXY);
        }

        private void RoundHolesPositionInSlabs(
            Document doc,
            List<GridLine2D> gridLines,
            double preferHostMaxDistFt,
            double roundHolePosition,
            XYZ originIntersectionCurve,
            FamilyInstance intersectionPoint,
            bool alignByEdges)
        {
            doc.Regenerate();

            XYZ originXY = ToXY(originIntersectionCurve);

            XYZ facingXY = SafeNormalize2D(intersectionPoint.FacingOrientation, XYZ.BasisY);
            XYZ handXY = SafeNormalize2D(intersectionPoint.HandOrientation, XYZ.BasisX);

            var gridHand = GetClosestPerpendicularGridLinePreferHost(
                gridLines, originIntersectionCurve, intersectionPoint.HandOrientation, preferHostMaxDistFt);

            var gridFacing = GetClosestPerpendicularGridLinePreferHost(
                gridLines, originIntersectionCurve, intersectionPoint.FacingOrientation, preferHostMaxDistFt);

            if (gridHand?.Line2D == null && gridFacing?.Line2D == null)
                return;

            XYZ finalMoveXY = XYZ.Zero;

            void AccumulateMove(GridLine2D gl, Func<XYZ> pickEdgePoint)
            {
                if (gl?.Line2D == null) return;

                XYZ lineOrigin = ToXY(gl.Line2D.Origin);
                XYZ lineDir = SafeNormalize2D(gl.Line2D.Direction, XYZ.BasisY);

                XYZ pointToProject = alignByEdges ? pickEdgePoint() : originXY;

                XYZ proj = ProjectPointToUnboundLine2D(pointToProject, lineOrigin, lineDir);
                double currentDistance = (proj - pointToProject).GetLength();

                double targetDistance = RoundToIncrement(currentDistance, roundHolePosition);
                double delta = targetDistance - currentDistance;

                if (Math.Abs(delta) < 1e-9) return;

                XYZ moveDir = proj - pointToProject;
                if (moveDir.GetLength() < 1e-9) return;

                finalMoveXY += moveDir.Normalize() * delta;
            }

            // Hand (ширина)
            AccumulateMove(
                gridHand,
                () =>
                {
                    double width = intersectionPoint.get_Parameter(intersectionPointWidthGuid)?.AsDouble() ?? 0;
                    if (width < 1e-9 || gridHand?.Line2D == null) return originXY;

                    XYZ left = originXY - handXY * (width / 2);
                    XYZ right = originXY + handXY * (width / 2);

                    XYZ lineOrigin = ToXY(gridHand.Line2D.Origin);
                    XYZ lineDir = SafeNormalize2D(gridHand.Line2D.Direction, XYZ.BasisY);

                    XYZ leftProj = ProjectPointToUnboundLine2D(left, lineOrigin, lineDir);
                    XYZ rightProj = ProjectPointToUnboundLine2D(right, lineOrigin, lineDir);

                    double leftDist = (leftProj - left).GetLength();
                    double rightDist = (rightProj - right).GetLength();

                    return leftDist <= rightDist ? left : right;
                });

            // Facing (высота)
            AccumulateMove(
                gridFacing,
                () =>
                {
                    double height = intersectionPoint.get_Parameter(intersectionPointHeightGuid)?.AsDouble() ?? 0;
                    if (height < 1e-9 || gridFacing?.Line2D == null) return originXY;

                    XYZ front = originXY + facingXY * (height / 2);
                    XYZ back = originXY - facingXY * (height / 2);

                    XYZ lineOrigin = ToXY(gridFacing.Line2D.Origin);
                    XYZ lineDir = SafeNormalize2D(gridFacing.Line2D.Direction, XYZ.BasisY);

                    XYZ frontProj = ProjectPointToUnboundLine2D(front, lineOrigin, lineDir);
                    XYZ backProj = ProjectPointToUnboundLine2D(back, lineOrigin, lineDir);

                    double frontDist = (frontProj - front).GetLength();
                    double backDist = (backProj - back).GetLength();

                    return frontDist <= backDist ? front : back;
                });

            if (finalMoveXY.GetLength() > 1e-9)
                ElementTransformUtils.MoveElement(doc, intersectionPoint.Id, -finalMoveXY);
        }

        private static async Task GetPluginStartInfo()
        {
            Assembly thisAssembly = Assembly.GetExecutingAssembly();
            string assemblyName = "GloryHoleRefreshElevations";
            string assemblyNameRus = "Обновить отметки";
            string assemblyFolderPath = Path.GetDirectoryName(thisAssembly.Location);

            int lastBackslashIndex = assemblyFolderPath.LastIndexOf("\\");
            string dllPath = assemblyFolderPath.Substring(0, lastBackslashIndex + 1) + "PluginInfoCollector\\PluginInfoCollector.dll";

            Assembly assembly = Assembly.LoadFrom(dllPath);
            Type type = assembly.GetType("PluginInfoCollector.InfoCollector");

            if (type != null)
            {
                object instance = Activator.CreateInstance(type);
                var method = type.GetMethod("CollectPluginUsageAsync");
                if (method != null)
                {
                    Task task = (Task)method.Invoke(instance, new object[] { assemblyName, assemblyNameRus });
                    await task;
                }
            }
        }
    }
}
