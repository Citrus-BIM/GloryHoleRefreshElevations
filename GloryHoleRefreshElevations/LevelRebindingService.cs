using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GloryHoleRefreshElevations
{
    internal sealed class RebindReport
    {
        public int Changed { get; set; }
        public int Recreated { get; set; }
        public List<ElementId> ReplacementIds { get; } = new List<ElementId>();
        public int Restored { get; set; }
        public int Unchanged { get; set; }
        public int Refreshed { get; set; }
        public List<ElementId> SkippedIds { get; } = new List<ElementId>();
        public Dictionary<string, int> Reasons { get; } = new Dictionary<string, int>();
        public List<string> Details { get; } = new List<string>();
        public string? Failure { get; set; }
        public string? UpdaterRestoreFailure { get; set; }

        public void Skip(ElementId id, string reason, string name)
        {
            SkippedIds.Add(id);
            Reasons[reason] = Reasons.ContainsKey(reason) ? Reasons[reason] + 1 : 1;
            Details.Add("ID " + id + " · " + name + ": " + reason);
        }
    }

    internal static class LevelRebindingService
    {
        private static readonly Guid FamilyCode = new Guid("40bbbf16-4b6a-45e8-9896-620bb448db96");
        private static readonly Guid BaseHeight = new Guid("9f5f7e49-616e-436f-9acc-5305f34b6933");
        private static readonly Guid ReportOffset = new Guid("515dc061-93ce-40e4-859a-e29224d80a10");
        private static readonly Guid GhGuid = new Guid("652cfdd0-f868-4433-8fbd-16a517e1b878");
        private static readonly Guid[] DimensionIds =
        {
            new Guid("8f2e4f93-9472-4941-a65d-0ac468fd6a5d"),
            new Guid("da753fe3-ecfa-465b-9a2c-02f55d0c2ff1"),
            new Guid("293f055d-6939-4611-87b7-9a50d0c1f50e"),
            new Guid("9b679ab7-ea2e-49ce-90ab-0549d5aa36ff")
        };
        private static readonly BuiltInParameter[] NativeDimensionIds =
        {
            BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.FAMILY_HEIGHT_PARAM,
            BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.WINDOW_HEIGHT,
            BuiltInParameter.CASEWORK_WIDTH, BuiltInParameter.CASEWORK_HEIGHT
        };

        public static IReadOnlyList<RebindLevel> CollectLevels(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.ProjectElevation).ThenBy(l => l.Name)
                .Select(l => new RebindLevel(l.UniqueId, l.Name, l.ProjectElevation, l.Elevation))
                .ToList();
        }

        public static RebindReport Execute(Document doc, IEnumerable<FamilyInstance> instances,
            bool rebind, bool selectedScope, bool manual, string targetLevelUniqueId, Action<FamilyInstance> refresh,
            bool roundHeight, bool roundLocation, Func<FamilyInstance, OpeningReplacementMetadata>? captureMetadata = null)
        {
            var report = new RebindReport();
            var afterCommit = new List<Action>();
            var completedDetails = new List<string>();
            UpdaterSuspension? suspension = null;
            try
            {
                // Enforce this here as well as in the UI, before any document writes.
                if (manual && (!selectedScope || !rebind))
                    throw new InvalidOperationException("Выбор уровня доступен только для выбранных элементов.");
                if (manual && string.IsNullOrEmpty(targetLevelUniqueId))
                    throw new InvalidOperationException("Выберите уровень для перепривязки.");
                Level? manualLevel = manual ? doc.GetElement(targetLevelUniqueId) as Level : null;
                if (manual && manualLevel == null)
                    throw new InvalidOperationException("Выбранный уровень больше недоступен.");

                var levels = CollectLevels(doc);
                // Materialize IDs before deleting any dependent elements or invalidating old API wrappers.
                var selected = instances.GroupBy(i => i.Id).Select(g => g.First())
                    .Select(i => new { i.Id, i.Name }).ToList();
                suspension = new UpdaterSuspension(new RevitUpdaterState());
                using (var operation = new TransactionGroup(doc,
                    rebind ? "Обновление отметок и перепривязка" : "Обновление отметок"))
                {
                    RequireStarted(operation.Start());
                    foreach (var source in selected)
                    {
                        var instance = doc.GetElement(source.Id) as FamilyInstance;
                        if (instance == null) { report.Skip(source.Id, "Элемент больше недоступен", source.Name); continue; }
                        if (rebind && IsFinishedOpening(instance))
                        {
                            if (!selectedScope || !manual || manualLevel == null)
                                report.Skip(source.Id, "Для готового отверстия нужны выбранные элементы и явно выбранный уровень", source.Name);
                            else ProcessOpening(doc, instance, manualLevel, refresh, report, afterCommit, completedDetails,
                                captureMetadata ?? OpeningReplacementMetadata.Capture);
                        }
                        else if (rebind)
                            ProcessInstance(doc, instance, levels, manualLevel, refresh,
                                roundHeight, roundLocation, report);
                        else
                            ProcessRefreshOnly(doc, instance, refresh, report);
                    }
                    // Counts are provisional until this succeeds. Dispose rolls back a started group.
                    if (operation.Assimilate() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Операция не была сохранена.");
                }
            }
            catch (Exception ex)
            {
                // In particular, never continue reading a document after RegenerationFailedException.
                report.Changed = 0;
                report.Recreated = 0;
                report.ReplacementIds.Clear();
                afterCommit.Clear();
                completedDetails.Clear();
                report.Restored = 0;
                report.Unchanged = 0;
                report.Refreshed = 0;
                report.Failure = ex is Autodesk.Revit.Exceptions.RegenerationFailedException
                    ? "Ошибка регенерации. Операция отменена."
                    : "Операция отменена: " + ex.Message;
            }
            finally
            {
                if (suspension != null)
                {
                    try { suspension.Dispose(); }
                    catch (Exception ex) { report.UpdaterRestoreFailure = ex.Message; }
                }
            }
            // Optional external bookkeeping is never inside geometry transactions, and cannot
            // turn a committed model change into a false 'operation rolled back' report.
            if (report.Failure == null)
            {
                report.Details.AddRange(completedDetails);
                foreach (var callback in afterCommit)
                    try { callback(); }
                    catch (Exception ex) { report.Details.Add("Отверстие сохранено; не удалось обновить внешние связи: " + ex.Message); }
            }
            return report;
        }

        private static void ProcessRefreshOnly(Document doc, FamilyInstance instance,
            Action<FamilyInstance> refresh, RebindReport report)
        {
            try
            {
                if (IsFinishedOpening(instance))
                    OpeningRefreshRules.RequireHost(instance.Host != null);
                RunTransaction(doc, "Обновить отметки элемента", () => refresh(instance), false);
                report.Refreshed++;
            }
            catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
            catch (TransactionStateException) { throw; }
            catch (Exception ex)
            {
                report.Skip(instance.Id, FriendlyReason(ex), instance.Name);
            }
        }

        internal static bool IsFinishedOpening(FamilyInstance instance)
        {
            return OpeningRebindingRules.IsOpening(instance.Symbol.get_Parameter(FamilyCode)?.AsString() ?? string.Empty,
                instance.Symbol.Family.Name);
        }

        private static void ProcessOpening(Document doc, FamilyInstance instance,
            Level target, Action<FamilyInstance> refresh, RebindReport report, List<Action> afterCommit,
            List<string> completedDetails, Func<FamilyInstance, OpeningReplacementMetadata> captureMetadata)
        {
            // Hosted openings cannot use the free task's placement offset or its slab correction.
            ElementId originalId = instance.Id;
            string originalName = instance.Name;
            using (var item = new TransactionGroup(doc, "Перепривязка отверстия"))
            {
                RequireStarted(item.Start());
                try
                {
                    var profile = OpeningElevationService.GetProfile(instance);
                    if (profile == RebindTaskProfile.Unsupported
                        || !(IsCategory(instance, BuiltInCategory.OST_GenericModel)
                            || IsCategory(instance, BuiltInCategory.OST_Windows)))
                        throw new SkipException("Неподдерживаемое или противоречивое семейство отверстия");
                    OpeningRefreshRules.RequireHost(instance.Host != null);
                    bool slab = RebindLevelRules.IsSlab(profile);
                    if (slab ? !(instance.Host is Floor) : !(instance.Host is Wall))
                        throw new SkipException("Основа не соответствует семейству отверстия");
                    if (!instance.GroupId.Equals(ElementId.InvalidElementId))
                        throw new SkipException("Элемент входит в группу");
                    if (instance.SuperComponent != null) throw new SkipException("Вложенное семейство");
                    if (!(instance.Location is LocationPoint)) throw new SkipException("Нет точки вставки");

                    var nativeLevel = instance.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
                    if (nativeLevel == null || nativeLevel.StorageType != StorageType.ElementId)
                        throw new SkipException("Отсутствует параметр уровня размещения отверстия");
                    var oldLevel = doc.GetElement(nativeLevel.AsElementId()) as Level;
                    if (oldLevel != null ? !oldLevel.Id.Equals(instance.LevelId)
                        : !instance.LevelId.Equals(ElementId.InvalidElementId))
                        throw new SkipException("Параметры исходного уровня противоречат друг другу");
                    OpeningElevationService.ValidateParameters(instance);
                    var before = PlacementSnapshot.Capture(instance);
                    var relations = OpeningRelationsSnapshot.Capture(instance);
                    bool changed = oldLevel == null || !oldLevel.Id.Equals(target.Id);
                    bool recreate = changed && nativeLevel.IsReadOnly;
                    if (changed && instance.Pinned && !recreate) throw new SkipException("Элемент закреплён");
                    var metadata = changed ? captureMetadata(instance) : null;
                    OpeningReplacementSnapshot? replacement = null;
                    if (recreate)
                    {
                        relations.RequireCut();
                        replacement = OpeningReplacementSnapshot.Capture(instance);
                        RunTransaction(doc, "Пересоздать отверстие на выбранном уровне", () =>
                        {
                            instance = replacement.Replace(target);
                            doc.Regenerate();
                            relations.Restore(instance);
                            replacement.RestorePin(instance);
                            doc.Regenerate();
                            VerifyOpening(instance, target, before, relations, false, false, true);
                            replacement.Verify(instance);
                        });
                        VerifyOpening(instance, target, before, relations, false, false, true);
                        replacement.Verify(instance);
                    }
                    else if (changed)
                    {
                        var offset = instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                        bool canCompensate = offset != null && offset.StorageType == StorageType.Double
                            && !offset.IsReadOnly && (oldLevel == null
                                || Near(oldLevel.ProjectElevation + offset.AsDouble(), before.Point.Z));
                        RunTransaction(doc, "Перепривязать отверстие к уровню", () =>
                        {
                            if (!nativeLevel.Set(target.Id) && !nativeLevel.AsElementId().Equals(target.Id))
                                throw new SkipException("Revit отклонил изменение уровня");
                            // Set both values before regeneration: the intermediate position may be outside the host.
                            // FREE_HOST_OFFSET is host-relative and is never substituted for a level-relative offset.
                            if (canCompensate)
                            {
                                var compensation = instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                                RequireWritable(compensation, StorageType.Double, "Отметка от уровня");
                                double expected = RebindLevelRules.NativeOffsetForPosition(before.Point.Z, target.ProjectElevation);
                                if (!compensation.Set(expected) && !Near(compensation.AsDouble(), expected))
                                    throw new SkipException("Revit отклонил изменение смещения");
                            }
                            doc.Regenerate();
                            VerifyOpening(instance, target, before, relations, false, false);
                        });
                        VerifyOpening(instance, target, before, relations, false, false);
                    }

                    RunTransaction(doc, "Обновить отметки отверстия", () =>
                    {
                        refresh(instance);
                        doc.Regenerate();
                        metadata?.Apply(instance);
                        VerifyOpening(instance, target, before, relations, false, false, recreate);
                        replacement?.Verify(instance);
                        OpeningElevationService.VerifyReports(instance, profile, target);
                        metadata?.Verify(instance);
                    });
                    VerifyOpening(instance, target, before, relations, false, false, recreate);
                    replacement?.Verify(instance);
                    OpeningElevationService.VerifyReports(instance, profile, target);
                    metadata?.Verify(instance);
                    var callback = metadata?.AfterCommit;
                    var warning = metadata?.Warning;
                    if (item.Assimilate() != TransactionStatus.Committed)
                        throw new TransactionStateException("Не удалось сохранить перепривязку отверстия.");
                    if (changed) report.Changed++; else report.Unchanged++;
                    if (oldLevel == null) report.Restored++;
                    if (recreate)
                    {
                        report.Recreated++;
                        report.ReplacementIds.Add(instance.Id);
                        completedDetails.Add("Пересоздано отверстие: ID " + originalId + " → " + instance.Id + " · " + originalName);
                    }
                    if (!string.IsNullOrWhiteSpace(warning)) completedDetails.Add("ID " + instance.Id + ": " + warning);
                    if (callback != null) afterCommit.Add(callback);
                }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                catch (TransactionStateException) { throw; }
                catch (Exception ex)
                {
                    if (item.GetStatus() != TransactionStatus.Started || item.RollBack() != TransactionStatus.RolledBack)
                        throw new TransactionStateException("Не удалось откатить отверстие: " + ex.Message);
                    report.Skip(originalId, FriendlyReason(ex), originalName);
                }
            }
        }

        private static bool IsCategory(Element element, BuiltInCategory category)
            => element.Category?.Id.Equals(new ElementId(category)) == true;

        private static void VerifyOpening(FamilyInstance instance, Level target, PlacementSnapshot before,
            OpeningRelationsSnapshot relations, bool allowZ, bool allowXY, bool replacement = false)
        {
            if (!instance.IsValidObject) throw new SkipException("Отверстие удалено при обработке Revit");
            if (!instance.LevelId.Equals(target.Id)
                || !instance.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).AsElementId().Equals(target.Id))
                throw new SkipException("Revit не сохранил уровень размещения отверстия");
            before.Verify(instance, allowZ, allowXY, replacement);
            relations.Verify(instance);
        }

        private static void ProcessInstance(Document doc, FamilyInstance instance,
            IReadOnlyList<RebindLevel> levels, Level? manualLevel, Action<FamilyInstance> refresh,
            bool roundHeight, bool roundLocation, RebindReport report)
        {
            // A separate group permits rollback even if a transaction committed and a subsequent
            // geometry check found an updater/constraint effect. The outer group provides one Undo.
            using (var item = new TransactionGroup(doc, "Перепривязка болванки"))
            {
                RequireStarted(item.Start());
                try
                {
                    RebindTaskProfile profile;
                    BuiltInParameter placementOffset;
                    Level? oldLevel;
                    ValidateInstance(doc, instance, out profile, out placementOffset, out oldLevel);
                    var before = PlacementSnapshot.Capture(instance);
                    // A script may create a normal Citrus instance with no level at all.
                    // Its old offset has no defined base: recover exclusively from world position.
                    if (oldLevel != null && !Near(oldLevel.ProjectElevation
                        + instance.get_Parameter(placementOffset).AsDouble(), before.Point.Z))
                        throw new SkipException("Нестандартное смещение семейства");

                    Level? target = manualLevel;
                    if (target == null)
                    {
                        var resolution = RebindLevelRules.Resolve(levels, profile,
                            RebindLevelRules.ReferenceHeight(profile, before.Point.Z));
                        if (resolution.Status != LevelResolutionStatus.Resolved)
                            throw new SkipException(ResolutionReason(resolution.Status));
                        target = doc.GetElement(resolution.Level.UniqueId) as Level;
                    }
                    if (target == null) throw new SkipException("Уровень недоступен");

                    bool restored = oldLevel == null;
                    bool changed = oldLevel == null || !oldLevel.Id.Equals(target.Id);
                    if (changed)
                    {
                        double newOffset = RebindLevelRules.NativeOffsetForPosition(before.Point.Z, target.ProjectElevation);
                        RunTransaction(doc, "Перепривязать к уровню", () =>
                        {
                            instance.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).Set(target.Id);
                            // Reacquire the native offset after assigning a formerly missing level.
                            doc.Regenerate();
                            placementOffset = SelectPlacementOffset(instance, profile, target);
                            var newOffsetParameter = instance.get_Parameter(placementOffset);
                            newOffsetParameter.Set(newOffset);
                            doc.Regenerate();
                            VerifyLevel(instance, target, newOffset, placementOffset);
                            before.Verify(instance, false, false);
                        });
                        // Commit-time updaters and failure resolution have now run.
                        VerifyLevel(instance, target, newOffset, placementOffset);
                        before.Verify(instance, false, false);
                    }

                    // Existing reporting and explicitly requested rounding run only after a verified rebind.
                    bool allowZ = roundHeight && !RebindLevelRules.IsSlab(profile);
                    RunTransaction(doc, "Обновить отметки болванки", () =>
                    {
                        refresh(instance);
                        doc.Regenerate();
                        before.Verify(instance, allowZ, roundLocation);
                        VerifyReports(instance, target, profile, placementOffset);
                    });
                    before.Verify(instance, allowZ, roundLocation);
                    VerifyReports(instance, target, profile, placementOffset);

                    if (item.Assimilate() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Не удалось сохранить перепривязку элемента.");
                    if (changed) report.Changed++;
                    else report.Unchanged++;
                    if (restored) report.Restored++;
                }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                catch (TransactionStateException) { throw; }
                catch (Exception ex)
                {
                    // An unexpected transaction state cannot safely be treated as a local skip.
                    if (item.GetStatus() != TransactionStatus.Started
                        || item.RollBack() != TransactionStatus.RolledBack)
                        throw new InvalidOperationException("Не удалось откатить элемент.", ex);
                    report.Skip(instance.Id, FriendlyReason(ex), instance.Name);
                }
            }
        }

        private static void ValidateInstance(Document doc, FamilyInstance instance,
            out RebindTaskProfile profile, out BuiltInParameter placementOffset, out Level? level)
        {
            profile = RebindLevelRules.GetProfile(instance.Symbol.get_Parameter(FamilyCode)?.AsString() ?? string.Empty,
                instance.Symbol.Family.Name);
            if (profile == RebindTaskProfile.Unsupported
                || !instance.Category.Id.Equals(new ElementId(BuiltInCategory.OST_GenericModel)))
                throw new SkipException("Не является поддерживаемой болванкой");
            if (instance.Pinned) throw new SkipException("Элемент закреплён");
            if (!instance.GroupId.Equals(ElementId.InvalidElementId))
                throw new SkipException("Элемент входит в группу");
            if (instance.SuperComponent != null) throw new SkipException("Вложенное семейство");
            if (instance.Host != null
                || instance.Symbol.Family.FamilyPlacementType != FamilyPlacementType.OneLevelBased)
                throw new SkipException("Семейство с основой или нестандартным размещением");
            if (!(instance.Location is LocationPoint)) throw new SkipException("Нет точки вставки");
            if (!Near(instance.GetTransform().BasisZ, XYZ.BasisZ))
                throw new SkipException("Болванка наклонена или перевёрнута");

            var nativeLevel = instance.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
            RequireWritable(nativeLevel, StorageType.ElementId, "Уровень");
            level = doc.GetElement(nativeLevel.AsElementId()) as Level;
            if (level != null && !level.Id.Equals(instance.LevelId))
                throw new SkipException("Параметры исходного уровня противоречат друг другу");
            placementOffset = level == null ? BuiltInParameter.INSTANCE_ELEVATION_PARAM
                : SelectPlacementOffset(instance, profile, level);
            RequireWritable(instance.get_Parameter(BaseHeight), StorageType.Double, "Рзм.ВысотаБазовогоУровня");
            RequireWritable(instance.get_Parameter(ReportOffset), StorageType.Double, "ADSK_Размер_Смещение от уровня");
        }

        private static void RequireWritable(Parameter parameter, StorageType storage, string name)
        {
            if (parameter == null) throw new SkipException("Отсутствует параметр «" + name + "»");
            if (parameter.StorageType != storage) throw new SkipException("Неверный тип параметра «" + name + "»");
            if (parameter.IsReadOnly) throw new SkipException("Параметр «" + name + "» недоступен для записи");
        }

        private static string FriendlyReason(Exception ex)
        {
            return ex is NullReferenceException
                ? "Семейство не содержит необходимых данных уровня или смещения"
                : ex.Message;
        }

        private static BuiltInParameter ReportOffsetParameter(RebindTaskProfile profile)
        {
            return RebindLevelRules.IsSlab(profile) ? BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM
                : BuiltInParameter.INSTANCE_ELEVATION_PARAM;
        }

        private static BuiltInParameter SelectPlacementOffset(FamilyInstance instance,
            RebindTaskProfile profile, Level level)
        {
            // DDBImport writes INSTANCE_ELEVATION_PARAM for both wall and slab tasks.
            // FREE_HOST_OFFSET is used by the existing slab report, but may be read-only.
            var candidates = RebindLevelRules.IsSlab(profile)
                ? new[] { BuiltInParameter.INSTANCE_ELEVATION_PARAM, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM }
                : new[] { BuiltInParameter.INSTANCE_ELEVATION_PARAM };
            foreach (var candidate in candidates)
            {
                var parameter = instance.get_Parameter(candidate);
                if (parameter != null && parameter.StorageType == StorageType.Double && !parameter.IsReadOnly
                    && Near(level.ProjectElevation + parameter.AsDouble(), ((LocationPoint)instance.Location).Point.Z))
                    return candidate;
            }
            throw new SkipException("Нет доступного параметра смещения с поддерживаемой привязкой");
        }

        private static void VerifyLevel(FamilyInstance instance, Level target, double offset,
            BuiltInParameter placementOffset)
        {
            if (!instance.LevelId.Equals(target.Id)
                || !instance.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).AsElementId().Equals(target.Id)
                || !Near(instance.get_Parameter(placementOffset).AsDouble(), offset)
                || !Near(((LocationPoint)instance.Location).Point.Z, target.ProjectElevation + offset))
                throw new SkipException("Не удалось сохранить уровень и смещение");
        }

        private static void VerifyReports(FamilyInstance instance, Level target, RebindTaskProfile profile,
            BuiltInParameter placementOffset)
        {
            double nativeOffset = instance.get_Parameter(placementOffset).AsDouble();
            VerifyLevel(instance, target, nativeOffset, placementOffset);
            var reportNative = instance.get_Parameter(ReportOffsetParameter(profile));
            if (reportNative == null || reportNative.StorageType != StorageType.Double
                || !Near(reportNative.AsDouble(), nativeOffset))
                throw new SkipException("Смещение для отчёта не соответствует положению болванки");
            double reportOffset = reportNative.AsDouble() - (RebindLevelRules.IsSlab(profile) ? 50.0 / 304.8 : 0);
            if (!Near(instance.get_Parameter(BaseHeight).AsDouble(), target.Elevation)
                || !Near(instance.get_Parameter(ReportOffset).AsDouble(), reportOffset))
                throw new SkipException("Не удалось обновить отчётные отметки");
        }

        private static string ResolutionReason(LevelResolutionStatus status)
        {
            switch (status)
            {
                case LevelResolutionStatus.NoLevels: return "В проекте нет уровней";
                case LevelResolutionStatus.NoLevelBelow: return "Нет уровня ниже элемента";
                case LevelResolutionStatus.Ambiguous: return "Неоднозначный выбор уровня";
                default: return "Некорректная высота элемента";
            }
        }

        private static void RequireStarted(TransactionStatus status)
        {
            if (status != TransactionStatus.Started)
                throw new InvalidOperationException("Не удалось начать транзакцию.");
        }

        private static void RunTransaction(Document doc, string name, Action action, bool rollbackWarnings = true)
        {
            using (var transaction = new Transaction(doc, name))
            {
                RequireStarted(transaction.Start());
                var failures = new RollbackOnFailure(rollbackWarnings);
                transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
                    .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                try
                {
                    action();
                    var status = transaction.Commit();
                    if (failures.DocumentCorruption)
                        throw new TransactionStateException("Revit сообщил о повреждении документа. Операция отменена.");
                    if (status == TransactionStatus.RolledBack)
                        throw new SkipException(failures.Reason ?? "Транзакция элемента отменена");
                    if (status != TransactionStatus.Committed)
                        throw new TransactionStateException("Revit не завершил обработку транзакции.");
                }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                catch
                {
                    if (transaction.GetStatus() == TransactionStatus.Started
                        && transaction.RollBack() != TransactionStatus.RolledBack)
                        throw new TransactionStateException("Не удалось отменить изменения элемента.");
                    throw;
                }
            }
        }

        private sealed class RollbackOnFailure : IFailuresPreprocessor
        {
            private readonly bool rollbackWarnings;
            public RollbackOnFailure(bool rollbackWarnings) { this.rollbackWarnings = rollbackWarnings; }
            public string? Reason { get; private set; }
            public bool DocumentCorruption { get; private set; }
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                var messages = accessor.GetFailureMessages();
                DocumentCorruption = messages.Any(m => m.GetSeverity() == FailureSeverity.DocumentCorruption);
                var failure = messages.FirstOrDefault(m => rollbackWarnings || m.GetSeverity() != FailureSeverity.Warning);
                if (failure == null) return FailureProcessingResult.Continue;
                // Ordinary refresh retains normal warning handling; rebind never accepts corrective geometry changes.
                Reason = "Сообщение Revit: " + failure.GetDescriptionText();
                return FailureProcessingResult.ProceedWithRollBack;
            }
        }

        private sealed class SkipException : Exception
        {
            public SkipException(string message) : base(message) { }
        }

        private sealed class TransactionStateException : Exception
        {
            public TransactionStateException(string message) : base(message) { }
        }

        private static bool Near(double a, double b)
        {
            return !double.IsNaN(a) && !double.IsInfinity(a) && !double.IsNaN(b)
                && !double.IsInfinity(b) && Math.Abs(a - b) <= RebindLevelRules.Tolerance;
        }

        private static bool Near(XYZ a, XYZ b)
        {
            return Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);
        }

        private sealed class OpeningRelationsSnapshot
        {
            private string hostId = string.Empty;
            private string? hostFace;
            private ElementId hostLevel = ElementId.InvalidElementId;
            private double? floorOffset;
            private bool inserted, pinned;
            private HashSet<string> joins = new HashSet<string>(StringComparer.Ordinal);
            private HashSet<string> voidCuts = new HashSet<string>(StringComparer.Ordinal);
            private List<Tuple<ElementId, bool>> joinElements = new List<Tuple<ElementId, bool>>();
            private List<ElementId> cutElements = new List<ElementId>();

            public static OpeningRelationsSnapshot Capture(FamilyInstance instance)
            {
                var doc = instance.Document;
                var host = instance.Host as HostObject;
                if (host == null || !host.IsValidObject)
                    throw new SkipException("Основа отверстия недоступна");
                var result = new OpeningRelationsSnapshot
                {
                    hostId = host.UniqueId,
                    hostLevel = host.LevelId,
                    hostFace = instance.HostFace?.ConvertToStableRepresentation(doc),
                    inserted = host.FindInserts(false, false, false, false).Contains(instance.Id),
                    pinned = instance.Pinned
                };
                var hostOffset = host.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                if (hostOffset != null && hostOffset.StorageType == StorageType.Double)
                    result.floorOffset = hostOffset.AsDouble();
                foreach (var id in JoinGeometryUtils.GetJoinedElements(doc, instance))
                {
                    var other = doc.GetElement(id);
                    if (other == null) throw new SkipException("Связанный с отверстием элемент недоступен");
                    result.joins.Add(other.UniqueId + ":" + JoinGeometryUtils.IsCuttingElementInJoin(doc, instance, other));
                    result.joinElements.Add(Tuple.Create(id, JoinGeometryUtils.IsCuttingElementInJoin(doc, instance, other)));
                }
                // Native window/host cuts are represented by the host insertion. Unattached
                // void cuts are additional relationships, not a universal test for an opening.
                if (InstanceVoidCutUtils.IsVoidInstanceCuttingElement(instance))
                    foreach (var id in InstanceVoidCutUtils.GetElementsBeingCut(instance))
                    {
                        var other = doc.GetElement(id);
                        if (other == null) throw new SkipException("Вырезаемый элемент недоступен");
                        result.voidCuts.Add(other.UniqueId);
                        result.cutElements.Add(id);
                    }
                return result;
            }

            public void RequireCut()
            {
                if (!inserted && !voidCuts.Contains(hostId))
                    throw new SkipException("Не подтверждено вырезание исходного отверстия в основе");
            }

            public void Restore(FamilyInstance instance)
            {
                var doc = instance.Document;
                foreach (var id in cutElements)
                {
                    var other = doc.GetElement(id) ?? throw new SkipException("Вырезаемый элемент недоступен после пересоздания");
                    if (!InstanceVoidCutUtils.InstanceVoidCutExists(other, instance))
                        InstanceVoidCutUtils.AddInstanceVoidCut(doc, other, instance);
                }
                foreach (var pair in joinElements)
                {
                    var other = doc.GetElement(pair.Item1) ?? throw new SkipException("Соединённый элемент недоступен после пересоздания");
                    if (!JoinGeometryUtils.AreElementsJoined(doc, instance, other))
                        JoinGeometryUtils.JoinGeometry(doc, instance, other);
                    if (JoinGeometryUtils.IsCuttingElementInJoin(doc, instance, other) != pair.Item2)
                        JoinGeometryUtils.SwitchJoinOrder(doc, instance, other);
                }
            }

            public void Verify(FamilyInstance instance)
            {
                var now = Capture(instance);
                if (hostId != now.hostId || !hostLevel.Equals(now.hostLevel) || hostFace != now.hostFace
                    || inserted != now.inserted || pinned != now.pinned
                    || floorOffset.HasValue != now.floorOffset.HasValue
                    || (floorOffset.HasValue && !Near(floorOffset.Value, now.floorOffset.GetValueOrDefault()))
                    || !joins.SetEquals(now.joins) || !voidCuts.SetEquals(now.voidCuts))
                    throw new SkipException("Изменились основа отверстия или связи вырезания и соединения");
            }
        }

        private sealed class PlacementSnapshot
        {
            public XYZ Point { get; private set; } = XYZ.Zero;
            private string uniqueId = string.Empty;
            private ElementId typeId = ElementId.InvalidElementId;
            private Transform transform = Transform.Identity;
            private bool mirrored, handFlipped, facingFlipped;
            private string? guid;
            private double?[] dimensions = Array.Empty<double?>();

            public static PlacementSnapshot Capture(FamilyInstance instance)
            {
                if (!instance.IsValidObject) throw new SkipException("Элемент удалён при обработке Revit");
                return new PlacementSnapshot
                {
                    Point = ((LocationPoint)instance.Location).Point,
                    uniqueId = instance.UniqueId,
                    typeId = instance.GetTypeId(),
                    transform = instance.GetTransform(),
                    mirrored = instance.Mirrored,
                    handFlipped = instance.HandFlipped,
                    facingFlipped = instance.FacingFlipped,
                    guid = instance.get_Parameter(GhGuid)?.AsString(),
                    dimensions = Dimensions(instance)
                };
            }

            public void Verify(FamilyInstance instance, bool allowZ, bool allowXY, bool replacement = false)
            {
                var now = Capture(instance);
                XYZ move = now.Point - Point;
                bool same = (replacement || uniqueId == now.uniqueId) && typeId.Equals(now.typeId)
                    && guid == now.guid && mirrored == now.mirrored
                    && handFlipped == now.handFlipped && facingFlipped == now.facingFlipped
                    && Near(transform.BasisX, now.transform.BasisX)
                    && Near(transform.BasisY, now.transform.BasisY)
                    && Near(transform.BasisZ, now.transform.BasisZ)
                    && Near(transform.Origin + move, now.transform.Origin)
                    && (allowZ || Near(move.Z, 0))
                    && (allowXY || (Near(move.X, 0) && Near(move.Y, 0)));
                for (int i = 0; i < dimensions.Length; i++)
                    same &= dimensions[i].HasValue == now.dimensions[i].HasValue
                        && (!dimensions[i].HasValue || Near(dimensions[i].GetValueOrDefault(), now.dimensions[i].GetValueOrDefault()));
                if (!same) throw new SkipException("Изменились положение, размеры или идентификатор элемента");
            }

            private static double?[] Dimensions(FamilyInstance instance)
            {
                // Check the task's size parameters, not its BoundingBox: visibility
                // helpers may change their extent when the reference level changes.
                return DimensionIds.SelectMany(id => new[]
                    { instance.get_Parameter(id), instance.Symbol.get_Parameter(id) })
                    .Concat(NativeDimensionIds.SelectMany(id => new[]
                        { instance.get_Parameter(id), instance.Symbol.get_Parameter(id) }))
                    .Select(p => p != null && p.StorageType == StorageType.Double ? (double?)p.AsDouble() : null)
                    .ToArray();
            }

        }
    }
}
