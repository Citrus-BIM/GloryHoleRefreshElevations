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
        public void RejectedUserParameterIncludesExpectedActualAndIdentityWithoutSplittingReasonGroups()
        {
            var fixture = new Fixture();
            fixture.Old.get_Parameter(Custom).Definition.Name = "User note";
            fixture.PrepareCreation(item => item.get_Parameter(Custom).RejectSet = true);

            var report = fixture.Model.Run(fixture.Old);

            AssertRestored(fixture, report);
            Assert.Equal("Изменился параметр отверстия: User note", Assert.Single(report.Reasons).Key);
            Assert.Contains(report.Details, text => text.Contains(Custom.ToString())
                && text.Contains("approval text") && text.Contains("ожидалось") && text.Contains("получено"));
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
        [InlineData("custom")]
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
                    case "custom": item.get_Parameter(Custom).Set("lost"); break;
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
        [InlineData("readonly-custom")]
        [InlineData("missing-custom")]
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
                    case "readonly-custom": item.get_Parameter(Custom).IsReadOnly = true; break;
                    case "missing-custom": item.Put(Custom, null); break;
                    case "wrong-target": item.LevelId = fixture.Model.Original.Id; break;
                    case "no-cut": ((HostObject)item.Host).Inserts.Remove(item.Id); break;
                }
            });
            AssertRestored(fixture, fixture.Model.Run(fixture.Old));
            Assert.NotNull(fixture.New);
        }

        [Theory]
        [InlineData(FailureSeverity.Error)]
        [InlineData(FailureSeverity.Warning)]
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
            internal readonly LevelRebindingServiceTests.Model Model = new LevelRebindingServiceTests.Model();
            internal readonly FamilyInstance Old;
            internal FamilyInstance New;
            internal Fixture(string shape = "wall")
            {
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
