using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Xunit;

namespace GloryHoleRefreshElevations.Tests
{
    [Collection("Revit service boundary")]
    public sealed class OpeningReplacementServiceTests
    {
        private static readonly Guid Width = new Guid("8f2e4f93-9472-4941-a65d-0ac468fd6a5d");
        private static readonly Guid GhGuid = new Guid("652cfdd0-f868-4433-8fbd-16a517e1b878");
        private static readonly Guid Custom = new Guid("e6fa7de9-eb9c-4adc-bd1a-f23d3cb3bd6f");
        private static readonly Guid ReportOffset = new Guid("515dc061-93ce-40e4-859a-e29224d80a10");
        private static readonly Guid Height = new Guid("da753fe3-ecfa-465b-9a2c-02f55d0c2ff1");

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void UnreadableOptionalSourceParameterCannotBlockReplacementAndKeepsOneNote(bool numeric)
        {
            var fixture = new Fixture();
            fixture.Old.Put(Custom, numeric ? new Parameter(StorageType.Double, 42.0)
                : new Parameter(StorageType.String, "approval text"));
            var parameter = fixture.Old.get_Parameter(Custom);
            parameter.Definition.Name = "Optional source data";
            parameter.BeforeRead = () => throw new InvalidOperationException("source getter unavailable");
            fixture.PrepareCreation(item =>
            {
                if (numeric) item.get_Parameter(Custom).Value = 0.0;
            });
            // Creation emulates family defaults; the unreadable old value must not be consulted.
            var report = fixture.Model.Run(fixture.Old);

            Assert.Null(report.Failure);
            Assert.Equal(1, report.Recreated);
            Assert.Empty(report.SkippedIds);
            Assert.Equal(fixture.Model.Target.Id, fixture.New.LevelId);
            Assert.Equal("preserved-guid", fixture.New.get_Parameter(GhGuid).AsString());
            Assert.Equal(7, fixture.New.get_Parameter(Width).AsDouble());
            Assert.Equal(15, ((LocationPoint)fixture.New.Location).Point.Z);
            Assert.Single(report.Details, note => note.Contains("Optional source data")
                && note.Contains("source getter unavailable"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void UnreadableCriticalSourceParameterStillBlocksReplacement(bool identity)
        {
            var fixture = new Fixture();
            var parameter = fixture.Old.get_Parameter(identity ? GhGuid : Width);
            parameter.BeforeRead = () => throw new InvalidOperationException("critical getter unavailable");
            fixture.PrepareCreation();
            Assert.Throws<InvalidOperationException>(() => OpeningReplacementSnapshot.Capture(fixture.Old));

            var report = fixture.Model.Run(fixture.Old);

            parameter.BeforeRead = null;
            AssertRestored(fixture, report);
            Assert.Null(fixture.New);
            Assert.Contains(report.Details, note => note.Contains("critical getter unavailable"));
        }

        [Fact]
        public void FatalRegenerationDuringOptionalSourceReadStillAborts()
        {
            var fixture = new Fixture();
            fixture.Old.get_Parameter(Custom).BeforeRead = () =>
                throw new Autodesk.Revit.Exceptions.RegenerationFailedException("fatal source getter");
            fixture.PrepareCreation();
            var report = fixture.Model.Run(fixture.Old);

            Assert.NotNull(report.Failure);
            Assert.Equal(0, report.Changed);
            Assert.Equal(0, report.Recreated);
            Assert.True(fixture.Old.IsValidObject);
            Assert.Null(fixture.New);
            Assert.Equal(fixture.Model.Original.Id, fixture.Old.LevelId);
        }

        [Fact]
        public void CombinedAuxiliaryChangesDoNotCancelAValidPinnedOpeningReplacement()
        {
            var fixture = new Fixture();
            fixture.Old.Pinned = true;
            Guid grouping = new Guid("3de5f1a4-d560-4fa8-a74f-25d250fb3401");
            Guid visibilityLine = new Guid("3efb3c8f-71cb-47b7-bf03-0f752198629e");
            fixture.Old.Put(grouping, new Parameter(StorageType.String, ""));
            fixture.Old.get_Parameter(grouping).Definition.Name = "ADSK_Группирование";
            fixture.Old.Put(visibilityLine, new Parameter(StorageType.Double, 0.0));
            fixture.Old.get_Parameter(visibilityLine).Definition.Name = "Удлинение линии вверх_вручную";
            var owned = fixture.Model.Document.Add(new Element { Name = "", Category = null, Location = null });
            for (int i = 0; i < 7; i++)
                owned.Put("service-" + i, new Parameter(StorageType.Integer, i) { IsReadOnly = true });
            fixture.Old.Dependents.Add(owned.Id);
            fixture.PrepareCreation(item => item.get_Parameter(grouping).Value = "4.Другие элементы систем");
            fixture.Model.Document.OnRegenerate = () => fixture.New?.get_Parameter(visibilityLine).Set(1000.0 / 304.8);
            fixture.Model.Document.AfterCommit = _ => fixture.New?.get_Parameter(ReportOffset).Set(-99.0);
            fixture.Model.Document.NextFailures.Add(new FailureMessageAccessor
                { Severity = FailureSeverity.Warning, Description = "Duplicate Mark" });

            var report = fixture.Model.Run(fixture.Old);

            Assert.Null(report.Failure);
            Assert.Equal(1, report.Changed);
            Assert.Equal(1, report.Recreated);
            Assert.Empty(report.SkippedIds);
            Assert.Equal(fixture.Model.Target.Id, fixture.New.LevelId);
            Assert.Equal(15, ((LocationPoint)fixture.New.Location).Point.Z);
            Assert.Equal(7, fixture.New.get_Parameter(Width).AsDouble());
            Assert.Equal("preserved-guid", fixture.New.get_Parameter(GhGuid).AsString());
            Assert.True(fixture.New.Pinned);
            Assert.False(owned.IsValidObject);
            Assert.Contains(fixture.New.Id, ((HostObject)fixture.New.Host).Inserts);
            Assert.Contains(fixture.New.Host.Id, InstanceVoidCutUtils.GetElementsBeingCut(fixture.New));
            Assert.Equal("4.Другие элементы систем", fixture.New.get_Parameter(grouping).AsString());
            Assert.Equal(1000.0 / 304.8, fixture.New.get_Parameter(visibilityLine).AsDouble());
            Assert.Contains(report.Details, text => text.Contains("Duplicate Mark"));
            Assert.Contains(report.Details, text => text.Contains("отчётный параметр"));
            Assert.DoesNotContain(report.Details, text => text.Contains("ADSK_Группирование"));
        }

        [Theory]
        [InlineData(false, "report-missing")]
        [InlineData(true, "report-missing")]
        [InlineData(false, "base-missing")]
        [InlineData(true, "base-missing")]
        [InlineData(false, "report-type")]
        [InlineData(true, "report-type")]
        [InlineData(false, "base-type")]
        [InlineData(true, "base-type")]
        [InlineData(false, "report-readonly")]
        [InlineData(true, "report-readonly")]
        [InlineData(false, "base-readonly")]
        [InlineData(true, "base-readonly")]
        [InlineData(false, "report-rejected")]
        [InlineData(true, "report-rejected")]
        [InlineData(false, "base-rejected")]
        [InlineData(true, "base-rejected")]
        [InlineData(false, "report-throws")]
        [InlineData(true, "report-throws")]
        [InlineData(false, "base-throws")]
        [InlineData(true, "base-throws")]
        [InlineData(false, "report-overwritten")]
        [InlineData(true, "report-overwritten")]
        [InlineData(false, "report-regeneration")]
        [InlineData(true, "report-regeneration")]
        [InlineData(false, "report-commit")]
        [InlineData(true, "report-commit")]
        [InlineData(false, "native-report-calculation")]
        [InlineData(true, "native-report-calculation")]
        public void ReportFieldsCannotCancelASelectedOpeningLevelChange(bool recreate, string damage)
        {
            var fixture = new Fixture();
            fixture.Old.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).IsReadOnly = recreate;
            Guid baseHeight = new Guid("9f5f7e49-616e-436f-9acc-5305f34b6933");
            Guid damagedId = damage.StartsWith("base") ? baseHeight : ReportOffset;
            Action<FamilyInstance> damageParameter = item =>
            {
                var parameter = item.get_Parameter(damagedId);
                if (damage.EndsWith("missing")) item.Put(damagedId, null);
                else if (damage.EndsWith("type")) item.Put(damagedId, new Parameter(StorageType.String, "not a length"));
                else if (damage.EndsWith("readonly")) parameter.IsReadOnly = true;
                else if (damage.EndsWith("rejected")) parameter.RejectSet = true;
                else if (damage.EndsWith("throws")) parameter.AfterSet = _ =>
                {
                    parameter.Value = -99.0;
                    throw new InvalidOperationException("family report write rejected");
                };
                else if (damage.EndsWith("overwritten")) parameter.AfterSet = _ => parameter.Value = -99.0;
            };
            if (recreate) fixture.PrepareCreation(damageParameter);
            else damageParameter(fixture.Old);
            Func<FamilyInstance> current = () => recreate ? fixture.New : fixture.Old;
            if (damage.EndsWith("regeneration"))
                fixture.Model.Document.OnRegenerate = () => current()?.get_Parameter(ReportOffset).Set(-99.0);
            if (damage.EndsWith("commit"))
                fixture.Model.Document.AfterCommit = _ => current()?.get_Parameter(ReportOffset).Set(-99.0);
            if (damage == "native-report-calculation")
                fixture.Model.Document.OnRegenerate = () =>
                {
                    if (current() != null) current().get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM).Value = double.NaN;
                };

            var report = fixture.Model.Run(fixture.Old);
            var target = current();

            Assert.Null(report.Failure);
            Assert.Equal(1, report.Changed);
            Assert.Equal(recreate ? 1 : 0, report.Recreated);
            Assert.Empty(report.SkippedIds);
            Assert.Equal(fixture.Model.Target.Id, target.LevelId);
            Assert.Equal(15, ((LocationPoint)target.Location).Point.Z);
            Assert.Equal(7, target.get_Parameter(Width).AsDouble());
            Assert.Equal("preserved-guid", target.get_Parameter(GhGuid).AsString());
            Assert.Contains(target.Id, ((HostObject)target.Host).Inserts);
            Assert.Contains(report.Details, text => text.Contains("отчёт") && text.Contains("перепривязка выполнена"));
            Guid otherId = damagedId == ReportOffset ? baseHeight : ReportOffset;
            Assert.Equal(otherId == ReportOffset ? 5 : 110, target.get_Parameter(otherId).AsDouble());
            if (damage.EndsWith("overwritten") || damage.EndsWith("regeneration") || damage.EndsWith("commit"))
                Assert.Contains(report.Details, text => text.Contains("ожидалось") && text.Contains("получено") && text.Contains("мм"));
        }

        [Theory]
        [InlineData(false, "point")]
        [InlineData(true, "point")]
        [InlineData(false, "dimension")]
        [InlineData(true, "dimension")]
        [InlineData(false, "guid")]
        [InlineData(true, "guid")]
        [InlineData(false, "cut")]
        [InlineData(true, "cut")]
        public void OptionalReportWritesCannotHidePhysicalDamage(bool recreate, string damage)
        {
            var fixture = new Fixture();
            fixture.Old.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).IsReadOnly = recreate;
            Action<FamilyInstance> configure = item => item.get_Parameter(ReportOffset).AfterSet = _ =>
            {
                item.get_Parameter(ReportOffset).Value = -99.0;
                if (damage == "point") ((LocationPoint)item.Location).Point += XYZ.BasisZ;
                if (damage == "dimension") item.get_Parameter(Width).Set(99.0);
                if (damage == "guid") item.get_Parameter(GhGuid).Set("lost");
                if (damage == "cut") ((HostObject)item.Host).Inserts.Remove(item.Id);
                throw new InvalidOperationException("family report write rejected");
            };
            if (recreate) fixture.PrepareCreation(configure); else configure(fixture.Old);
            // Metadata deliberately restores GH_GUID before commit. A later change must still fail.
            if (damage == "guid") fixture.Model.Document.AfterCommit = name =>
            {
                if (name == "Обновить отметки отверстия")
                    (recreate ? fixture.New : fixture.Old).get_Parameter(GhGuid).Set("lost after metadata");
            };
            AssertRestored(fixture, fixture.Model.Run(fixture.Old));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FatalRegenerationFromOptionalReportWriteStillAborts(bool recreate)
        {
            var fixture = new Fixture();
            fixture.Old.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).IsReadOnly = recreate;
            Action<FamilyInstance> configure = item => item.get_Parameter(ReportOffset).AfterSet = _ =>
                throw new Autodesk.Revit.Exceptions.RegenerationFailedException("fatal report write");
            if (recreate) fixture.PrepareCreation(configure); else configure(fixture.Old);
            var report = fixture.Model.Run(fixture.Old);
            Assert.NotNull(report.Failure);
            Assert.Equal(0, report.Changed);
            Assert.True(fixture.Old.IsValidObject);
            Assert.Equal(fixture.Model.Original.Id, fixture.Old.LevelId);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NewReportOffsetSettlesAfterRegenerationWithoutComparingTheOldOffset(bool recreate)
        {
            var fixture = new Fixture();
            fixture.Old.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).IsReadOnly = recreate;
            double? pending = null;
            Action<FamilyInstance> configure = item => item.get_Parameter(ReportOffset).AfterSet = value =>
            {
                pending = (double)value;
                item.get_Parameter(ReportOffset).Value = 15.0; // The old report value persists until regeneration.
            };
            if (recreate) fixture.PrepareCreation(configure); else configure(fixture.Old);
            fixture.Model.Document.OnRegenerate = () =>
            {
                if (pending.HasValue)
                    (recreate ? fixture.New : fixture.Old).get_Parameter(ReportOffset).Value = pending.Value;
            };

            var report = fixture.Model.Run(fixture.Old);
            var current = recreate ? fixture.New : fixture.Old;

            Assert.Null(report.Failure);
            Assert.Equal(1, report.Changed);
            Assert.Equal(recreate ? 1 : 0, report.Recreated);
            Assert.Equal(5, current.get_Parameter(ReportOffset).AsDouble());
            Assert.Equal(5, current.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM).AsDouble());
            Assert.Equal(15, ((LocationPoint)current.Location).Point.Z);
            Assert.DoesNotContain(report.Details, text => text.Contains("отчётный параметр"));
        }

        [Fact]
        public void LinkedHeightIsVerifiedAfterTheParameterBatchRegenerates()
        {
            var fixture = new Fixture();
            fixture.Old.Put(Height, new Parameter(StorageType.Double, 9.0));
            double? pendingHeight = null;
            int regenerations = 0;
            fixture.PrepareCreation(item =>
            {
                var height = item.get_Parameter(Height);
                height.Value = 1.0;
                height.AfterSet = value =>
                {
                    pendingHeight = (double)value;
                    height.Value = 1.0; // A linked family value is not updated until regeneration.
                };
            });
            fixture.Model.Document.OnRegenerate = () =>
            {
                regenerations++;
                if (pendingHeight.HasValue)
                    fixture.New.get_Parameter(Height).Value = pendingHeight.Value;
            };

            var report = fixture.Model.Run(fixture.Old);

            Assert.Null(report.Failure);
            Assert.Equal(1, report.Recreated);
            Assert.Empty(report.SkippedIds);
            Assert.True(regenerations > 0);
            Assert.Equal(9, fixture.New.get_Parameter(Height).AsDouble());
            Assert.Equal(7, fixture.New.get_Parameter(Width).AsDouble());
            Assert.Equal("preserved-guid", fixture.New.get_Parameter(GhGuid).AsString());
        }

        [Fact]
        public void RejectedHeightStillRestoresTheOriginalAfterRegeneration()
        {
            var fixture = new Fixture();
            fixture.Old.Put(Height, new Parameter(StorageType.Double, 9.0));
            bool regenerated = false;
            fixture.PrepareCreation(item =>
            {
                var height = item.get_Parameter(Height);
                height.Value = 1.0;
                height.RejectSet = true;
            });
            fixture.Model.Document.OnRegenerate = () => regenerated = true;

            var report = fixture.Model.Run(fixture.Old);

            AssertRestored(fixture, report);
            Assert.True(regenerated);
            Assert.Equal(9, fixture.Old.get_Parameter(Height).AsDouble());
            Assert.False(fixture.New.IsValidObject);
            Assert.Contains(report.Details, text => text.Contains(Height.ToString())
                && text.Contains("ожидалось 9") && text.Contains("получено 1") && text.Contains("внутренние единицы"));
        }

        [Fact]
        public void RejectedUserParameterAddsATransferNoteWithoutBlockingReplacement()
        {
            var fixture = new Fixture();
            fixture.Old.get_Parameter(Custom).Definition.Name = "User note";
            fixture.PrepareCreation(item => item.get_Parameter(Custom).RejectSet = true);

            var report = fixture.Model.Run(fixture.Old);

            Assert.Equal(1, report.Recreated);
            Assert.Empty(report.Reasons);
            Assert.Empty(report.SkippedIds);
            Assert.Contains(report.Details, text => text.Contains(Custom.ToString())
                && text.Contains("approval text") && text.Contains("ожидалось") && text.Contains("получено"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void EmptyOrdinaryGroupingKeepsTheFamilyDefaultWithoutNotes(string? oldValue)
        {
            var groupingId = new Guid("3de5f1a4-d560-4fa8-a74f-25d250fb3401");
            var fixture = new Fixture();
            fixture.Old.Put(groupingId, new Parameter(StorageType.String, oldValue));
            fixture.Old.get_Parameter(groupingId).Definition.Name = "ADSK_Группирование";
            int writes = 0;
            fixture.PrepareCreation(item =>
            {
                var grouping = item.get_Parameter(groupingId);
                grouping.Value = "4.Другие элементы систем";
                grouping.AfterSet = _ => { writes++; grouping.Value = "4.Другие элементы систем"; };
            });

            var report = fixture.Model.Run(fixture.Old);

            Assert.Equal(1, report.Recreated);
            Assert.Equal(0, writes);
            Assert.Equal("4.Другие элементы систем", fixture.New.get_Parameter(groupingId).AsString());
            Assert.DoesNotContain(report.Details, text => text.Contains("ADSK_Группирование"));
            Assert.Equal("preserved-guid", fixture.New.get_Parameter(GhGuid).AsString());
            Assert.Equal(7, fixture.New.get_Parameter(Width).AsDouble());
            Assert.Equal(15, ((LocationPoint)fixture.New.Location).Point.Z);
        }

        [Theory]
        [InlineData("readonly")]
        [InlineData("missing")]
        [InlineData("wrong-storage")]
        [InlineData("throws")]
        public void FilledOrdinaryParameterFailureDoesNotBlockGeometry(string reason)
        {
            var fixture = new Fixture();
            fixture.Old.get_Parameter(Custom).Definition.Name = "User note";
            fixture.PrepareCreation(item =>
            {
                var parameter = item.get_Parameter(Custom);
                switch (reason)
                {
                    case "readonly": parameter.IsReadOnly = true; break;
                    case "missing": item.Put(Custom, null); break;
                    case "wrong-storage": item.Put(Custom, new Parameter(StorageType.Double, 1.0)); break;
                    case "throws": parameter.AfterSet = _ => { parameter.Value = "family default"; throw new InvalidOperationException("ordinary parameter error"); }; break;
                }
            });

            var report = fixture.Model.Run(fixture.Old);

            Assert.Null(report.Failure);
            Assert.Equal(1, report.Recreated);
            Assert.Empty(report.SkippedIds);
            Assert.Contains(report.Details, text => text.Contains("User note"));
            Assert.Equal(7, fixture.New.get_Parameter(Width).AsDouble());
            Assert.Equal("preserved-guid", fixture.New.get_Parameter(GhGuid).AsString());
        }

        [Fact]
        public void PostCommitOrdinaryParameterChangesOnlyAddOneTransferNote()
        {
            var fixture = new Fixture();
            fixture.Old.get_Parameter(Custom).Definition.Name = "User note";
            fixture.PrepareCreation();
            fixture.Model.Document.AfterCommit = _ => fixture.New?.get_Parameter(Custom).Set("family value");

            var report = fixture.Model.Run(fixture.Old);

            Assert.Equal(1, report.Recreated);
            Assert.Empty(report.SkippedIds);
            Assert.Single(report.Details, text => text.Contains("User note"));
            Assert.Equal("family value", fixture.New.get_Parameter(Custom).AsString());
        }

        [Fact]
        public void FatalRegenerationDuringOrdinaryParameterWriteStillAborts()
        {
            var fixture = new Fixture();
            fixture.PrepareCreation(item => item.get_Parameter(Custom).AfterSet = _ =>
                throw new Autodesk.Revit.Exceptions.RegenerationFailedException("fatal parameter write"));

            var report = fixture.Model.Run(fixture.Old);

            Assert.NotNull(report.Failure);
            Assert.Equal(0, report.Recreated);
            Assert.True(fixture.Old.IsValidObject);
            Assert.False(fixture.New.IsValidObject);
        }

        [Fact]
        public void HubIdentityIsStillMandatoryWhenOrdinaryParametersAreBestEffort()
        {
            var projectId = new Guid("8b5f2c01-9ad4-4e2f-b8c7-1d0e3a5b7c9d");
            var fixture = new Fixture();
            fixture.Old.Put(projectId, new Parameter(StorageType.String, Guid.NewGuid().ToString()));
            fixture.PrepareCreation(item =>
            {
                var project = item.get_Parameter(projectId);
                project.Value = ""; project.IsReadOnly = true;
            });

            AssertRestored(fixture, RunWithBridge(fixture, new OrderingBridge(fixture.Model.Document)));
        }

        [Fact]
        public void AnonymousOwnedDependentMayBeDeletedWithOpening()
        {
            var fixture = new Fixture();
            var owned = fixture.Model.Document.Add(new Element { Name = "", Category = null, Location = null });
            fixture.Old.Dependents.Add(owned.Id);
            fixture.PrepareCreation();

            var report = fixture.Model.Run(fixture.Old);

            Assert.Equal(1, report.Recreated);
            Assert.False(owned.IsValidObject);
            Assert.False(fixture.Old.IsValidObject);
            Assert.True(fixture.New.IsValidObject);
        }

        [Fact]
        public void UnexpectedAnonymousDeletionStillRestoresOriginal()
        {
            var fixture = new Fixture();
            var unexpected = fixture.Model.Document.Add(new Element { Name = "", Category = null, Location = null });
            fixture.Model.Document.UnexpectedDeleteIds.Add(unexpected.Id);
            fixture.PrepareCreation();

            AssertRestored(fixture, fixture.Model.Run(fixture.Old));
            Assert.True(unexpected.IsValidObject);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(22)]
        public void OpeningsWithACommonSevenParameterDependencyAreRecreated(int count)
        {
            var model = new LevelRebindingServiceTests.Model();
            var owned = model.Document.Add(new Element { Name = "", Category = null, Location = null });
            for (int i = 0; i < 7; i++)
                owned.Put("service-" + i, new Parameter(StorageType.Integer, i) { IsReadOnly = true });
            var fixtures = Enumerable.Range(0, count).Select(_ => new Fixture(model: model)).ToArray();
            var creators = new Dictionary<ElementId, Func<XYZ, FamilySymbol, Element, Level, FamilyInstance>>();
            foreach (var fixture in fixtures)
            {
                fixture.Old.Dependents.Add(owned.Id);
                fixture.PrepareCreation();
                creators.Add(fixture.Old.Host.Id, model.Document.CreateFamilyInstance);
            }
            model.Document.CreateFamilyInstance = (point, symbol, host, level) => creators[host.Id](point, symbol, host, level);

            var report = model.Run(fixtures.Select(fixture => fixture.Old).ToArray());

            Assert.Null(report.Failure);
            Assert.Equal(count, report.Recreated);
            Assert.Empty(report.SkippedIds);
            Assert.False(owned.IsValidObject);
            foreach (var fixture in fixtures)
            {
                Assert.False(fixture.Old.IsValidObject);
                Assert.True(fixture.New.IsValidObject);
                Assert.Equal(model.Target.Id, fixture.New.LevelId);
                Assert.Equal(7, fixture.New.get_Parameter(Width).AsDouble());
                Assert.Equal("preserved-guid", fixture.New.get_Parameter(GhGuid).AsString());
                Assert.True(new XYZ(1, 2, 15).DistanceTo(((LocationPoint)fixture.New.Location).Point) < 1e-9);
                Assert.Contains(fixture.New.Id, ((HostObject)fixture.New.Host).Inserts);
            }
        }

        [Theory]
        [InlineData("width")]
        [InlineData("point")]
        [InlineData("guid")]
        [InlineData("native-cut")]
        public void RequiredOpeningDamageStillRestoresItsParameterizedDependency(string damage)
        {
            var fixture = new Fixture();
            var owned = fixture.Model.Document.Add(new Element { Name = "", Category = null, Location = null });
            owned.Put(BuiltInParameter.ELEM_PARTITION_PARAM, new Parameter(StorageType.Integer, 1));
            fixture.Old.Dependents.Add(owned.Id);
            fixture.PrepareCreation();
            fixture.Model.Document.AfterCommit = _ =>
            {
                switch (damage)
                {
                    case "width": fixture.New.get_Parameter(Width).Value = 1.0; break;
                    case "point": ((LocationPoint)fixture.New.Location).Point += XYZ.BasisZ; break;
                    case "guid": fixture.New.get_Parameter(GhGuid).Value = "lost"; break;
                    case "native-cut": ((HostObject)fixture.New.Host).Inserts.Remove(fixture.New.Id); break;
                }
            };

            AssertRestored(fixture, fixture.Model.Run(fixture.Old));
            Assert.NotNull(fixture.New);
            Assert.False(fixture.New.IsValidObject);
            Assert.True(owned.IsValidObject);
            Assert.Equal(1, owned.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM).AsInteger());
        }

        [Fact]
        public void FloorKeepsHostRelativeOffsetWhenConstructorResetsIt()
        {
            var fixture = new Fixture("slab");
            fixture.Old.Put(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, new Parameter(StorageType.Double, 3.0));
            fixture.Old.Host.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(12.0);
            fixture.PrepareCreation(item =>
            {
                var offset = item.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
                offset.Value = 0.0;
                offset.AfterSet = value => ((LocationPoint)item.Location).Point = new XYZ(1, 2, 12 + (double)value);
                offset.AfterSet(0.0);
            });
            var report = fixture.Model.Run(fixture.Old);
            Assert.Equal(1, report.Recreated);
            Assert.Equal(3, fixture.New.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM).AsDouble());
            Assert.Equal(15, ((LocationPoint)fixture.New.Location).Point.Z);
            Assert.Equal(2, fixture.New.get_Parameter(ReportOffset).AsDouble());
        }

        [Fact]
        public void ScheduleOnlyLevelIsNotCopiedFromTheOldOpening()
        {
            var fixture = new Fixture();
            fixture.Old.Put(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM, new Parameter(StorageType.ElementId, fixture.Model.Original.Id));
            fixture.PrepareCreation(item =>
            {
                var schedule = item.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM);
                schedule.Value = fixture.Model.Target.Id;
                schedule.AfterSet = value => item.LevelId = (ElementId)value;
            });
            var report = fixture.Model.Run(fixture.Old);
            Assert.Equal(1, report.Recreated);
            Assert.Equal(fixture.Model.Target.Id, fixture.New.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM).AsElementId());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MetadataUsesFinalReportsAndPublishesOnlyAfterOuterCommit(bool readOnly)
        {
            var fixture = new Fixture();
            fixture.Old.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).IsReadOnly = readOnly;
            fixture.PrepareCreation();
            var bridge = new OrderingBridge(fixture.Model.Document);
            var report = RunWithBridge(fixture, bridge);
            Assert.Null(report.Failure);
            Assert.Equal(1, report.Changed);
            Assert.Equal(readOnly ? 1 : 0, report.Recreated);
            Assert.True(bridge.Applied);
            Assert.True(bridge.Published);
            Assert.True(bridge.Verified >= 2);
            Assert.Equal(readOnly ? fixture.New.UniqueId : fixture.Old.UniqueId, bridge.TargetUniqueId);
            Assert.Contains(report.Details, text => text.Contains("metadata warning"));
        }

        [Fact]
        public void OuterRollbackDiscardsReplacementMappingsAndPublication()
        {
            var fixture = new Fixture(); fixture.PrepareCreation();
            fixture.Model.Document.FailOuterAssimilate = true;
            var bridge = new OrderingBridge(fixture.Model.Document);
            var report = RunWithBridge(fixture, bridge);
            Assert.True(bridge.Applied);
            Assert.False(bridge.Published);
            Assert.NotNull(report.Failure);
            Assert.Equal(0, report.Recreated);
            Assert.Empty(report.ReplacementIds);
            Assert.DoesNotContain(report.Details, text => text.StartsWith("Пересоздано"));
            Assert.True(fixture.Old.IsValidObject);
            Assert.False(fixture.New.IsValidObject);
        }

        [Fact]
        public void PublicationFailureDoesNotUndoOrMisreportCommittedReplacement()
        {
            var fixture = new Fixture(); fixture.PrepareCreation();
            var bridge = new OrderingBridge(fixture.Model.Document) { FailPublication = true };
            var report = RunWithBridge(fixture, bridge);
            Assert.Null(report.Failure);
            Assert.Equal(1, report.Changed);
            Assert.Equal(1, report.Recreated);
            Assert.True(fixture.New.IsValidObject);
            Assert.False(fixture.Old.IsValidObject);
            Assert.Contains(report.Details, text => text.Contains("external failure"));
        }

        private static RebindReport RunWithBridge(Fixture fixture, OrderingBridge bridge)
        {
            fixture.Old.EntitySchemaGuids.Add(new Guid("a841b1f0-1c9a-4c78-80ce-6ca58a8ad564"));
            return LevelRebindingService.Execute(fixture.Model.Document, new[] { fixture.Old }, true, true, true,
                fixture.Model.Target.UniqueId, item => OpeningElevationService.Refresh(item, false, 0), false, false,
                old => OpeningReplacementMetadata.Capture(old, _ => bridge));
        }

        public sealed class OrderingBridge
        {
            private readonly Document document;
            public OrderingBridge(Document document) { this.document = document; }
            public bool Applied, Published, FailPublication;
            public int Verified;
            public string TargetUniqueId;
            public string Warning => "metadata warning";
            public void Apply(FamilyInstance target)
            {
                Assert.True(document.IsModifiable);
                Assert.Equal(5, target.get_Parameter(ReportOffset).AsDouble());
                Applied = true; TargetUniqueId = target.UniqueId;
            }
            public void Verify(FamilyInstance target)
            {
                Assert.True(Applied);
                Assert.Equal(TargetUniqueId, target.UniqueId);
                Assert.Equal(5, target.get_Parameter(ReportOffset).AsDouble());
                Verified++;
            }
            public Action AfterCommit => () =>
            {
                Assert.False(document.IsModifiable);
                Assert.Equal(0, document.OpenGroups);
                Published = true;
                if (FailPublication) throw new Exception("external failure");
            };
        }

        [Theory]
        [InlineData("wall", false, false)]
        [InlineData("wall", true, true)]
        [InlineData("slab", true, false)]
        [InlineData("window", false, true)]
        public void ReadOnlyLevelRecreatesSameOpeningAndPreservesParametersRelationsAndPin(string shape, bool pinned, bool flipped)
        {
            var fixture = new Fixture(shape);
            fixture.Old.Pinned = pinned;
            if (flipped) fixture.Old.flipHand();
            fixture.PrepareCreation();
            var before = fixture.Old.GetTransform();
            var report = fixture.Model.Run(fixture.Old);
            Assert.Null(report.Failure);
            Assert.Equal(1, report.Changed);
            Assert.Equal(1, report.Recreated);
            Assert.Empty(report.SkippedIds);
            Assert.False(fixture.Old.IsValidObject);
            var current = fixture.New;
            Assert.NotNull(current);
            Assert.Equal(current.Id, Assert.Single(report.ReplacementIds));
            Assert.NotEqual(fixture.Old.Id, current.Id);
            Assert.Equal(fixture.Model.Target.Id, current.LevelId);
            Assert.Equal(fixture.Model.Target.Id, current.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).AsElementId());
            Assert.Equal(15, ((LocationPoint)current.Location).Point.Z);
            Assert.True(before.BasisX.IsAlmostEqualTo(current.GetTransform().BasisX));
            Assert.True(before.BasisY.IsAlmostEqualTo(current.GetTransform().BasisY));
            Assert.Equal(fixture.Old.HandFlipped, current.HandFlipped);
            Assert.Equal(pinned, current.Pinned);
            Assert.Equal(7, current.get_Parameter(Width).AsDouble());
            Assert.Equal("preserved-guid", current.get_Parameter(GhGuid).AsString());
            Assert.Equal("approval text", current.get_Parameter(Custom).AsString());
            Assert.Equal("A-42", current.get_Parameter(BuiltInParameter.ALL_MODEL_MARK).AsString());
            Assert.Equal(fixture.Old.CreatedPhaseId, current.CreatedPhaseId);
            Assert.Equal(fixture.Old.DemolishedPhaseId, current.DemolishedPhaseId);
            Assert.Equal(fixture.Old.WorksetId, current.WorksetId);
            Assert.Same(fixture.Old.Host, current.Host);
            Assert.Contains(current.Id, ((HostObject)current.Host).Inserts);
            Assert.True(InstanceVoidCutUtils.InstanceVoidCutExists(current.Host, current));
            Assert.True(JoinGeometryUtils.IsCuttingElementInJoin(fixture.Model.Document, current, current.Host));
            Assert.Equal(5, current.get_Parameter(ReportOffset).AsDouble());
            Assert.Contains(report.Details, text => text.Contains(fixture.Old.Id.ToString()) && text.Contains(current.Id.ToString()));
        }

        [Fact]
        public void ReadOnlyLevelAlreadyAtTargetOnlyRefreshesOriginal()
        {
            var fixture = new Fixture();
            var report = LevelRebindingService.Execute(fixture.Model.Document, new[] { fixture.Old }, true, true, true,
                fixture.Model.Original.UniqueId, item => OpeningElevationService.Refresh(item, false, 0), false, false);
            Assert.Null(report.Failure);
            Assert.Equal(1, report.Unchanged);
            Assert.Equal(0, report.Recreated);
            Assert.True(fixture.Old.IsValidObject);
            Assert.Null(fixture.New);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AutomaticModeCannotRecreateReadyOpenings(bool selected)
        {
            var fixture = new Fixture();
            fixture.PrepareCreation();
            var report = LevelRebindingService.Execute(fixture.Model.Document, new[] { fixture.Old }, true, selected, false,
                null, _ => throw new Exception("must not refresh"), false, false);
            Assert.Null(report.Failure);
            Assert.Equal(0, report.Changed);
            Assert.Equal(fixture.Old.Id, Assert.Single(report.SkippedIds));
            Assert.Contains(report.Details, text => text.Contains("выбран") && text.Contains("уров"));
            Assert.True(fixture.Old.IsValidObject);
            Assert.Null(fixture.New);
        }

        [Theory]
        [InlineData("dimension")]
        [InlineData("gh-guid")]
        [InlineData("phase")]
        [InlineData("workset")]
        [InlineData("level")]
        [InlineData("native-cut")]
        [InlineData("point")]
        public void CommitTimeDamageRestoresOriginalAndRemovesReplacement(string damage)
        {
            var fixture = new Fixture();
            fixture.PrepareCreation();
            fixture.Model.Document.AfterCommit = _ =>
            {
                var item = fixture.New;
                if (item == null) return;
                switch (damage)
                {
                    case "dimension": item.get_Parameter(Width).Set(19.0); break;
                    case "gh-guid": item.get_Parameter(GhGuid).Set("lost"); break;
                    case "phase": item.CreatedPhaseId = ElementId.InvalidElementId; break;
                    case "workset": item.WorksetId = new WorksetId(99); break;
                    case "level": item.LevelId = fixture.Model.Original.Id; break;
                    case "native-cut": ((HostObject)item.Host).Inserts.Remove(item.Id); break;
                    case "point": ((LocationPoint)item.Location).Point += XYZ.BasisZ; break;
                }
            };
            AssertRestored(fixture, fixture.Model.Run(fixture.Old));
            Assert.NotNull(fixture.New);
            Assert.False(fixture.New.IsValidObject);
        }

        [Theory]
        [InlineData("readonly-dimension")]
        [InlineData("wrong-target")]
        [InlineData("no-cut")]
        public void InvalidReplacementRollsBackBeforeCountingSuccess(string damage)
        {
            var fixture = new Fixture();
            fixture.PrepareCreation(item =>
            {
                switch (damage)
                {
                    case "readonly-dimension": item.get_Parameter(Width).IsReadOnly = true; break;
                    case "wrong-target": item.LevelId = fixture.Model.Original.Id; break;
                    case "no-cut": ((HostObject)item.Host).Inserts.Remove(item.Id); break;
                }
            });
            AssertRestored(fixture, fixture.Model.Run(fixture.Old));
            Assert.NotNull(fixture.New);
        }

        [Theory]
        [InlineData(FailureSeverity.Error)]
        public void RevitFailureRestoresOriginalAndItsAnnotations(FailureSeverity severity)
        {
            var fixture = new Fixture();
            var annotation = fixture.Model.Document.Add(new Element { Category = new Category { CategoryType = CategoryType.Annotation } });
            fixture.Old.Dependents.Add(annotation.Id);
            fixture.PrepareCreation();
            fixture.Model.Document.NextFailures.Add(new FailureMessageAccessor { Severity = severity, Description = "cannot cut host" });
            AssertRestored(fixture, fixture.Model.Run(fixture.Old));
            Assert.True(annotation.IsValidObject);
            Assert.NotNull(fixture.New);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WarningAloneDoesNotCancelAValidOpeningLevelChange(bool recreate)
        {
            var fixture = new Fixture();
            fixture.Old.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).IsReadOnly = recreate;
            fixture.PrepareCreation();
            fixture.Model.Document.NextFailures.Add(new FailureMessageAccessor
                { Severity = FailureSeverity.Warning, Description = "Duplicate Mark" });
            var report = fixture.Model.Run(fixture.Old);

            Assert.Null(report.Failure);
            Assert.Equal(1, report.Changed);
            Assert.Equal(recreate ? 1 : 0, report.Recreated);
            Assert.Empty(report.SkippedIds);
            Assert.Contains(report.Details, text => text.Contains("Duplicate Mark"));
        }

        [Theory]
        [InlineData("point")]
        [InlineData("dimension")]
        [InlineData("guid")]
        [InlineData("cut")]
        public void WarningCannotHideDamageToTheRecreatedOpening(string damage)
        {
            var fixture = new Fixture(); fixture.PrepareCreation();
            fixture.Model.Document.NextFailures.Add(new FailureMessageAccessor
                { Severity = FailureSeverity.Warning, Description = "warning with damage" });
            bool committedBeforeValidation = false;
            fixture.Model.Document.AfterCommit = _ =>
            {
                committedBeforeValidation = true;
                switch (damage)
                {
                    case "point": ((LocationPoint)fixture.New.Location).Point += XYZ.BasisZ; break;
                    case "dimension": fixture.New.get_Parameter(Width).Set(99.0); break;
                    case "guid": fixture.New.get_Parameter(GhGuid).Set("lost"); break;
                    case "cut": ((HostObject)fixture.New.Host).Inserts.Remove(fixture.New.Id); break;
                }
            };
            AssertRestored(fixture, fixture.Model.Run(fixture.Old));
            Assert.True(committedBeforeValidation);
        }

        [Fact]
        public void RecreatingOpeningStillRejectsUnknownStorageBeforeDeletion()
        {
            var fixture = new Fixture(); fixture.PrepareCreation();
            var schema = Guid.NewGuid();
            fixture.Old.EntitySchemaGuids.Add(schema);
            var report = fixture.Model.Run(fixture.Old);
            AssertRestored(fixture, report);
            Assert.Null(fixture.New);
            Assert.Contains(schema, fixture.Old.EntitySchemaGuids);
            Assert.Contains(report.Details, text => text.Contains("неизвестные служебные данные"));
        }

        [Fact]
        public void AnnotationDependentsAreAllowedToDisappear()
        {
            var fixture = new Fixture();
            var annotation = fixture.Model.Document.Add(new Element { Category = new Category { CategoryType = CategoryType.Annotation } });
            fixture.Old.Dependents.Add(annotation.Id);
            fixture.PrepareCreation();
            var report = fixture.Model.Run(fixture.Old);
            Assert.Equal(1, report.Recreated);
            Assert.False(annotation.IsValidObject);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CollateralModelDeletionIsRolledBack(bool discoverable)
        {
            var fixture = new Fixture();
            var neighbor = fixture.Model.Document.Add(new Wall { Category = new Category { CategoryType = CategoryType.Model } });
            if (discoverable) fixture.Old.Dependents.Add(neighbor.Id);
            else fixture.Model.Document.UnexpectedDeleteIds.Add(neighbor.Id);
            fixture.PrepareCreation();
            var report = fixture.Model.Run(fixture.Old);
            AssertRestored(fixture, report);
            Assert.True(neighbor.IsValidObject);
            Assert.Contains(report.Details, text => text.Contains("модель"));
        }

        [Fact]
        public void FatalRegenerationRestoresDeletedOriginalWithoutFurtherDocumentReads()
        {
            var fixture = new Fixture();
            fixture.PrepareCreation();
            fixture.Model.Document.OnRegenerate = () => throw new Autodesk.Revit.Exceptions.RegenerationFailedException("fatal");
            var report = fixture.Model.Run(fixture.Old);
            Assert.NotNull(report.Failure);
            Assert.True(fixture.Old.IsValidObject);
            Assert.Equal(0, report.Recreated);
            Assert.Empty(report.ReplacementIds);
            Assert.Equal(0, fixture.Model.Document.ReadsAfterFatalRegeneration);
        }

        private static void AssertRestored(Fixture fixture, RebindReport report)
        {
            Assert.Null(report.Failure);
            Assert.Equal(0, report.Changed);
            Assert.Equal(0, report.Recreated);
            Assert.Empty(report.ReplacementIds);
            Assert.Equal(fixture.Old.Id, Assert.Single(report.SkippedIds));
            Assert.True(fixture.Old.IsValidObject);
            Assert.Equal(fixture.Model.Original.Id, fixture.Old.LevelId);
            Assert.Equal(7, fixture.Old.get_Parameter(Width).AsDouble());
            Assert.Contains(fixture.Old.Id, ((HostObject)fixture.Old.Host).Inserts);
        }

        private sealed class Fixture
        {
            internal readonly LevelRebindingServiceTests.Model Model;
            internal readonly FamilyInstance Old;
            internal FamilyInstance New;
            internal Fixture(string shape = "wall", LevelRebindingServiceTests.Model model = null)
            {
                Model = model ?? new LevelRebindingServiceTests.Model();
                Old = Model.Create(shape);
                Old.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).IsReadOnly = true;
                Old.get_Parameter(Width).Set(7.0);
                Old.get_Parameter(GhGuid).Set("preserved-guid");
                Old.Put(Custom, new Parameter(StorageType.String, "approval text"));
                Old.Put(BuiltInParameter.ALL_MODEL_MARK, new Parameter(StorageType.String, "A-42"));
                Old.CreatedPhaseId = new ElementId(700); Old.DemolishedPhaseId = new ElementId(701);
                Old.WorksetId = new WorksetId(42);
                Old.Put(BuiltInParameter.ELEM_PARTITION_PARAM, new Parameter(StorageType.Integer, 42));
                Model.Document.IsWorkshared = true;
            }
            internal void PrepareCreation(Action<FamilyInstance> damage = null)
            {
                // This emulates the loaded family's defaults; it never reads the invalid old instance in the factory.
                var template = new Element(); Old.CloneParametersTo(template);
                var hostFace = Old.HostFace; var category = Old.Category; var name = Old.Name;
                Model.Document.CreateFamilyInstance = (point, symbol, host, level) =>
                {
                    Assert.False(Old.IsValidObject); // No overlapping old/new cutters during regeneration.
                    New = Model.Document.Add(new FamilyInstance { Symbol = symbol, Host = host, HostFace = hostFace,
                        LevelId = level.Id, Category = category, Name = name, Location = new LocationPoint { Point = point } });
                    template.CloneParametersTo(New);
                    New.get_Parameter(Width).Value = 1.0;
                    New.get_Parameter(GhGuid).Value = "";
                    New.get_Parameter(Custom).Value = "";
                    New.get_Parameter(BuiltInParameter.ALL_MODEL_MARK).Value = "";
                    New.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).Value = level.Id;
                    New.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM).Value = point.Z - level.ProjectElevation;
                    var offset = New.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                    offset.AfterSet = value => ((LocationPoint)New.Location).Point = new XYZ(point.X, point.Y, level.ProjectElevation + (double)value);
                    var workset = New.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM); workset.Value = 1;
                    workset.AfterSet = value => New.WorksetId = new WorksetId((int)value);
                    ((HostObject)host).Inserts.Add(New.Id);
                    damage?.Invoke(New);
                    return New;
                };
            }
        }
    }
}
