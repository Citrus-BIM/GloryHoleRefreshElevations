using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Xunit;

namespace GloryHoleRefreshElevations.Tests
{
    public sealed class LevelRebindingServiceTests
    {
        private static readonly Guid FamilyCode = new Guid("40bbbf16-4b6a-45e8-9896-620bb448db96");
        private static readonly Guid BaseHeight = new Guid("9f5f7e49-616e-436f-9acc-5305f34b6933");
        private static readonly Guid ReportOffset = new Guid("515dc061-93ce-40e4-859a-e29224d80a10");
        private static readonly Guid GhGuid = new Guid("652cfdd0-f868-4433-8fbd-16a517e1b878");
        private static readonly Guid Width = new Guid("8f2e4f93-9472-4941-a65d-0ac468fd6a5d");

        [Theory]
        [InlineData("task")]
        [InlineData("wall")]
        [InlineData("wall-round-code")]
        [InlineData("window")]
        [InlineData("slab")]
        public void RebindPreservesWorldPlacementIdentityDimensionsAndCutRelations(string shape)
        {
            var model = new Model();
            FamilyInstance item = model.Create(shape);
            string id = item.UniqueId, guid = item.get_Parameter(GhGuid).AsString();
            Element host = item.Host;
            var report = model.Run(item);

            Assert.Null(report.Failure);
            Assert.Equal(1, report.Changed);
            Assert.Equal(0, report.Refreshed);
            Assert.Empty(report.SkippedIds);
            Assert.Equal(model.Target.Id, item.LevelId);
            Assert.Equal(model.Target.Id, item.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).AsElementId());
            Assert.Equal(5, item.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM).AsDouble(), 8);
            Assert.Equal(15, ((LocationPoint)item.Location).Point.Z, 8);
            Assert.Equal(1, ((LocationPoint)item.Location).Point.X, 8);
            Assert.Equal(2, ((LocationPoint)item.Location).Point.Y, 8);
            Assert.True(item.GetTransform().BasisX.IsAlmostEqualTo(new XYZ(0, 1, 0)));
            Assert.True(item.GetTransform().BasisY.IsAlmostEqualTo(new XYZ(-1, 0, 0)));
            Assert.Equal(id, item.UniqueId);
            Assert.Equal(guid, item.get_Parameter(GhGuid).AsString());
            Assert.Same(host, item.Host);
            Assert.Equal(2, item.get_Parameter(Width).AsDouble());
            Assert.Equal(2, item.Symbol.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM).AsDouble());
            Assert.Equal(3, item.Symbol.get_Parameter(BuiltInParameter.FAMILY_HEIGHT_PARAM).AsDouble());
            Assert.Equal(5, item.get_Parameter(ReportOffset).AsDouble(), 8);
            if (item.get_Parameter(BaseHeight) != null)
                Assert.Equal(110, item.get_Parameter(BaseHeight).AsDouble(), 8);
            if (host != null)
            {
                Assert.Contains(item.Id, ((HostObject)host).FindInserts(false, false, false, false));
                Assert.Contains(host.Id, InstanceVoidCutUtils.GetElementsBeingCut(item));
                Assert.True(JoinGeometryUtils.IsCuttingElementInJoin(model.Document, item, host));
                Assert.Equal("face-" + host.UniqueId, item.HostFace.ConvertToStableRepresentation(model.Document));
            }
            Assert.True(UpdaterRegistry.Enabled);
        }

        [Theory]
        [InlineData("readonly")]
        [InlineData("missing-host")]
        [InlineData("pinned")]
        [InlineData("rejected-level-set")]
        [InlineData("level-id-not-changed")]
        public void UnsupportedOpeningIsSkippedWithoutChangingGeometryOrReports(string condition)
        {
            var model = new Model();
            FamilyInstance item = model.Create("wall");
            if (condition == "readonly") item.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).IsReadOnly = true;
            if (condition == "missing-host") item.Host = null;
            if (condition == "pinned") item.Pinned = true;
            if (condition == "rejected-level-set") item.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).RejectSet = true;
            if (condition == "level-id-not-changed") item.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).AfterSet = _ => { };
            int refreshCalls = 0;
            var report = model.Run(new[] { item }, _ => refreshCalls++);

            Assert.Null(report.Failure);
            Assert.Equal(0, report.Changed);
            Assert.Equal(0, report.Refreshed);
            Assert.Equal(item.Id, Assert.Single(report.SkippedIds));
            Assert.Equal(model.Original.Id, item.LevelId);
            Assert.Equal(15, ((LocationPoint)item.Location).Point.Z);
            Assert.Equal(15, item.get_Parameter(ReportOffset).AsDouble());
            Assert.Equal(0, refreshCalls);
            Assert.Equal(condition == "pinned", item.Pinned);
        }

        [Theory]
        [InlineData("point")]
        [InlineData("guid-size")]
        [InlineData("native-type-size")]
        [InlineData("casework-type-size")]
        [InlineData("host")]
        [InlineData("host-face")]
        [InlineData("host-insert")]
        [InlineData("void-cut")]
        [InlineData("new-void-cut")]
        [InlineData("new-join")]
        [InlineData("join-direction")]
        [InlineData("deleted")]
        public void CommitTimeDamageRollsBackOnlyThatOpeningAndDoesNotCountIt(string damage)
        {
            var model = new Model();
            FamilyInstance first = model.Create("window");
            FamilyInstance second = model.Create("wall");
            var originalHost = (HostObject)first.Host;
            string originalFace = first.HostFace.ConvertToStableRepresentation(model.Document);
            bool fired = false;
            model.Document.AfterCommit = _ =>
            {
                if (fired) return;
                fired = true;
                switch (damage)
                {
                    case "point": ((LocationPoint)first.Location).Point += new XYZ(0, 0, 0.5); break;
                    case "guid-size": first.get_Parameter(Width).Set(8.0); break;
                    case "native-type-size": first.Symbol.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM).Set(8.0); break;
                    case "casework-type-size": first.Symbol.get_Parameter(BuiltInParameter.CASEWORK_WIDTH).Set(8.0); break;
                    case "host": first.Host = second.Host; break;
                    case "host-face": first.HostFace = new Reference("different-face"); break;
                    case "host-insert": originalHost.Inserts.Remove(first.Id); break;
                    case "void-cut": model.Document.VoidCuts[first.Id].Clear(); break;
                    case "new-void-cut": model.Document.VoidCuts[first.Id].Add(second.Host.Id); break;
                    case "new-join": model.Document.Joins.Add((first.Id, second.Host.Id, first.Id)); break;
                    case "join-direction":
                        int index = model.Document.Joins.FindIndex(join => join.A.Equals(first.Id));
                        model.Document.Joins[index] = (first.Id, originalHost.Id, originalHost.Id); break;
                    case "deleted": first.IsValidObject = false; break;
                }
            };
            var report = model.Run(first, second);

            Assert.True(fired);
            Assert.Null(report.Failure);
            Assert.Equal(1, report.Changed);
            Assert.Equal(0, report.Refreshed);
            Assert.Equal(first.Id, Assert.Single(report.SkippedIds));
            Assert.Equal(model.Original.Id, first.LevelId);
            Assert.Equal(model.Target.Id, second.LevelId);
            Assert.Equal(15, ((LocationPoint)first.Location).Point.Z);
            Assert.Equal(2, first.get_Parameter(Width).AsDouble());
            Assert.Equal(2, first.Symbol.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM).AsDouble());
            Assert.Equal(2, first.Symbol.get_Parameter(BuiltInParameter.CASEWORK_WIDTH).AsDouble());
            Assert.Equal(15, first.get_Parameter(ReportOffset).AsDouble());
            Assert.Same(originalHost, first.Host);
            Assert.Equal(originalFace, first.HostFace.ConvertToStableRepresentation(model.Document));
            Assert.Contains(first.Id, originalHost.Inserts);
            Assert.Equal(originalHost.Id, Assert.Single(InstanceVoidCutUtils.GetElementsBeingCut(first)));
            Assert.Equal(originalHost.Id, Assert.Single(JoinGeometryUtils.GetJoinedElements(model.Document, first)));
            Assert.True(JoinGeometryUtils.IsCuttingElementInJoin(model.Document, first, originalHost));
            Assert.True(first.IsValidObject);
            Assert.True(UpdaterRegistry.Enabled);
        }

        [Fact]
        public void RevitFailureRollsBackOneOpeningAndKeepsProcessingTheNext()
        {
            var model = new Model();
            FamilyInstance first = model.Create("wall"), second = model.Create("wall");
            model.Document.NextFailures.Add(new FailureMessageAccessor { Severity = FailureSeverity.Error, Description = "Cannot cut host" });
            var report = model.Run(first, second);
            Assert.Null(report.Failure);
            Assert.Equal(1, report.Changed);
            Assert.Equal(first.Id, Assert.Single(report.SkippedIds));
            Assert.Contains(report.Details, text => text.Contains("Cannot cut host"));
            Assert.Equal(model.Original.Id, first.LevelId);
            Assert.Equal(model.Target.Id, second.LevelId);
        }

        [Fact]
        public void HostedSlabWithReadOnlyNativeOffsetCanRebindWithoutMovingItsPoint()
        {
            var model = new Model();
            FamilyInstance item = model.Create("slab");
            item.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM).IsReadOnly = true;
            // Some hosted families change only their reference level; their host fixes world Z.
            item.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).AfterSet = value => item.LevelId = (ElementId)value;
            var report = model.Run(item);

            Assert.Null(report.Failure);
            Assert.Equal(1, report.Changed);
            Assert.Empty(report.SkippedIds);
            Assert.Equal(model.Target.Id, item.LevelId);
            Assert.Equal(15, ((LocationPoint)item.Location).Point.Z);
            Assert.Equal(15, item.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM).AsDouble());
            Assert.Equal(5, item.get_Parameter(ReportOffset).AsDouble());
        }

        [Fact]
        public void HostedSlabWithReadOnlyOffsetRollsBackIfChangingLevelMovesIt()
        {
            var model = new Model();
            FamilyInstance item = model.Create("slab");
            item.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM).IsReadOnly = true;
            var report = model.Run(item);

            Assert.Null(report.Failure);
            Assert.Equal(0, report.Changed);
            Assert.Equal(item.Id, Assert.Single(report.SkippedIds));
            Assert.Equal(model.Original.Id, item.LevelId);
            Assert.Equal(15, ((LocationPoint)item.Location).Point.Z);
            Assert.Equal(15, item.get_Parameter(ReportOffset).AsDouble());
        }

        [Fact]
        public void NativeHostInsertWithoutUnattachedVoidCanRebind()
        {
            var model = new Model();
            FamilyInstance item = model.Create("window");
            item.IsVoidCutting = false;
            model.Document.VoidCuts.Remove(item.Id);
            model.Document.Joins.Clear();
            var report = model.Run(item);

            Assert.Null(report.Failure);
            Assert.Equal(1, report.Changed);
            Assert.Empty(report.SkippedIds);
            Assert.Equal(model.Target.Id, item.LevelId);
            Assert.Equal(15, ((LocationPoint)item.Location).Point.Z);
            Assert.Contains(item.Id, ((HostObject)item.Host).FindInserts(false, false, false, false));
        }

        [Fact]
        public void AutomaticFinishedSlabSelectionUsesActualPointWithoutTaskDownwardAdjustment()
        {
            var model = new Model();
            Level nearest = model.Document.Add(new Level { Name = "Nearest", ProjectElevation = 10.2, Elevation = 110.2 });
            FamilyInstance item = model.Create("slab");
            item.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM).Set(10.12);
            item.Host.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(10.12);
            var report = model.RunAutomatic(item);

            Assert.Null(report.Failure);
            Assert.Equal(1, report.Changed);
            Assert.Empty(report.SkippedIds);
            Assert.Equal(nearest.Id, item.LevelId);
            Assert.Equal(10.12, ((LocationPoint)item.Location).Point.Z, 8);
            Assert.Equal(-0.08, item.get_Parameter(ReportOffset).AsDouble(), 8);
        }

        [Fact]
        public void OffsetSetReturningFalseIsAcceptedWhenLevelChangeAlreadyPreservedPlacement()
        {
            var model = new Model();
            FamilyInstance item = model.Create("wall");
            Parameter offset = item.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
            offset.RejectSet = true;
            item.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).AfterSet = value =>
            {
                item.LevelId = (ElementId)value;
                // Native host constraints update the stored offset while leaving the world point fixed.
                offset.Value = 15.0 - ((Level)model.Document.GetElement(item.LevelId)).ProjectElevation;
            };
            var report = model.Run(item);

            Assert.Null(report.Failure);
            Assert.Equal(1, report.Changed);
            Assert.Empty(report.SkippedIds);
            Assert.Equal(model.Target.Id, item.LevelId);
            Assert.Equal(5, offset.AsDouble());
            Assert.Equal(15, ((LocationPoint)item.Location).Point.Z);
        }

        [Fact]
        public void FatalRegenerationRollsBackTheOperationAndStopsReadingTheDocument()
        {
            var model = new Model();
            FamilyInstance first = model.Create("task"), second = model.Create("task");
            int refreshCalls = 0;
            model.Document.OnRegenerate = () => throw new Autodesk.Revit.Exceptions.RegenerationFailedException("fatal");
            var report = model.Run(new[] { first, second }, _ => refreshCalls++);
            Assert.NotNull(report.Failure);
            Assert.Equal(0, report.Changed);
            Assert.Equal(0, report.Refreshed);
            Assert.Equal(model.Original.Id, first.LevelId);
            Assert.Equal(model.Original.Id, second.LevelId);
            Assert.Equal(15, ((LocationPoint)first.Location).Point.Z);
            Assert.Equal(0, refreshCalls);
            Assert.Equal(0, model.Document.ReadsAfterFatalRegeneration);
            Assert.True(UpdaterRegistry.Enabled);
        }

        [Fact]
        public void FatalRegenerationAlsoRollsBackPreviouslyCommittedElementsInTheSameRun()
        {
            var model = new Model();
            FamilyInstance first = model.Create("task"), second = model.Create("task");
            model.Document.OnRegenerate = () =>
            {
                if (second.LevelId.Equals(model.Target.Id))
                    throw new Autodesk.Revit.Exceptions.RegenerationFailedException("fatal second element");
            };
            var report = model.Run(first, second);

            Assert.NotNull(report.Failure);
            Assert.Equal(0, report.Changed);
            Assert.Equal(model.Original.Id, first.LevelId);
            Assert.Equal(model.Original.Id, second.LevelId);
            Assert.Equal(15, first.get_Parameter(ReportOffset).AsDouble());
            Assert.Equal(15, ((LocationPoint)first.Location).Point.Z);
            Assert.Equal(0, model.Document.ReadsAfterFatalRegeneration);
            Assert.True(UpdaterRegistry.Enabled);
        }

        private sealed class Model
        {
            internal Document Document { get; } = new Document();
            internal Level Original { get; }
            internal Level Target { get; }
            internal Model()
            {
                UpdaterRegistry.Registered = true; UpdaterRegistry.Enabled = true;
                Original = Document.Add(new Level { Name = "Original", ProjectElevation = 0, Elevation = 100 });
                Target = Document.Add(new Level { Name = "Target", ProjectElevation = 10, Elevation = 110 });
            }
            internal FamilyInstance Create(string shape)
            {
                bool task = shape == "task", window = shape == "window", slab = shape == "slab";
                string familyName = task ? "Пересечение_Стена_Прямоугольное"
                    : window ? "231_Отверстие прямоугольное (Окно_Стена)"
                    : slab ? "Отверстие_Плита_Прямоугольное"
                    : shape == "wall-round-code" ? "Пользовательское круглое отверстие" : "Отверстие_Стена_Прямоугольное";
                var family = Document.Add(new Family { Name = familyName,
                    FamilyPlacementType = task ? FamilyPlacementType.OneLevelBased : FamilyPlacementType.OneLevelBasedHosted });
                var symbol = Document.Add(new FamilySymbol { Family = family });
                symbol.Put(FamilyCode, new Parameter(StorageType.String, task ? "111" : slab ? "221" : shape == "wall-round-code" ? "126" : ""));
                symbol.Put(BuiltInParameter.FAMILY_WIDTH_PARAM, new Parameter(StorageType.Double, 2.0));
                symbol.Put(BuiltInParameter.FAMILY_HEIGHT_PARAM, new Parameter(StorageType.Double, 3.0));
                symbol.Put(BuiltInParameter.CASEWORK_WIDTH, new Parameter(StorageType.Double, 2.0));
                symbol.Put(BuiltInParameter.CASEWORK_HEIGHT, new Parameter(StorageType.Double, 3.0));
                var item = Document.Add(new FamilyInstance { Symbol = symbol, LevelId = Original.Id,
                    Name = familyName, Category = new Category { Id = new ElementId(window ? BuiltInCategory.OST_Windows : BuiltInCategory.OST_GenericModel) },
                    Location = new LocationPoint { Point = new XYZ(1, 2, 15) },
                    Orientation = new Transform { BasisX = new XYZ(0, 1, 0), BasisY = new XYZ(-1, 0, 0), BasisZ = XYZ.BasisZ } });
                item.Put(GhGuid, new Parameter(StorageType.String, "guid-" + item.UniqueId));
                item.Put(Width, new Parameter(StorageType.Double, 2.0));
                if (!window) item.Put(BaseHeight, new Parameter(StorageType.Double, 100.0));
                item.Put(ReportOffset, new Parameter(StorageType.Double, 15.0));
                var offset = new Parameter(StorageType.Double, 15.0);
                item.Put(BuiltInParameter.INSTANCE_ELEVATION_PARAM, offset);
                item.Put(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, offset);
                item.Put(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM, offset);
                offset.AfterSet = value =>
                {
                    var level = (Level)Document.GetElement(item.LevelId);
                    XYZ point = ((LocationPoint)item.Location).Point;
                    ((LocationPoint)item.Location).Point = new XYZ(point.X, point.Y, level.ProjectElevation + (double)value);
                };
                var levelParameter = new Parameter(StorageType.ElementId, Original.Id);
                item.Put(BuiltInParameter.FAMILY_LEVEL_PARAM, levelParameter);
                levelParameter.AfterSet = value =>
                {
                    item.LevelId = (ElementId)value;
                    offset.AfterSet(offset.Value);
                };
                if (!task)
                {
                    HostObject host = slab ? (HostObject)new Floor() : new Wall();
                    Document.Add(host); host.LevelId = Original.Id;
                    host.Put(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM, new Parameter(StorageType.Double, 15.0));
                    host.Inserts.Add(item.Id); item.Host = host;
                    item.HostFace = new Reference("face-" + host.UniqueId);
                    Document.VoidCuts[item.Id] = new HashSet<ElementId> { host.Id };
                    Document.Joins.Add((item.Id, host.Id, item.Id));
                }
                return item;
            }
            internal RebindReport Run(params FamilyInstance[] items) => Run(items, Refresh);
            internal RebindReport RunAutomatic(params FamilyInstance[] items)
                => LevelRebindingService.Execute(Document, items, true, true, false, null, Refresh, false, false);
            internal RebindReport Run(IEnumerable<FamilyInstance> items, Action<FamilyInstance> refresh)
                => LevelRebindingService.Execute(Document, items, true, true, true, Target.UniqueId, refresh, false, false);
            private void Refresh(FamilyInstance item)
            {
                if (OpeningElevationService.GetProfile(item) != RebindTaskProfile.Unsupported)
                {
                    OpeningElevationService.Refresh(item, false, 0);
                    return;
                }
                var level = (Level)Document.GetElement(item.LevelId);
                item.get_Parameter(BaseHeight)?.Set(level.Elevation);
                item.get_Parameter(ReportOffset).Set(item.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM).AsDouble());
            }
        }
    }
}
