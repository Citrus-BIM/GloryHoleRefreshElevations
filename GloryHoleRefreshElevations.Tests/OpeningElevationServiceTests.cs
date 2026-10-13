using System;
using Autodesk.Revit.DB;
using Xunit;

namespace GloryHoleRefreshElevations.Tests
{
    public sealed class OpeningElevationServiceTests
    {
        private static readonly Guid FamilyCode = new Guid("40bbbf16-4b6a-45e8-9896-620bb448db96");
        private static readonly Guid BaseHeight = new Guid("9f5f7e49-616e-436f-9acc-5305f34b6933");
        private static readonly Guid ReportOffset = new Guid("515dc061-93ce-40e4-859a-e29224d80a10");

        [Theory]
        [InlineData("Отверстие_Стена_Прямоугольное", "", 1)]
        [InlineData("Гильза_Стена", "126", 2)]
        [InlineData("232_Отверстие в плите прямоугольное (Окно_Плита)", "", 3)]
        [InlineData("Отверстие_Стена_Прямоугольное", "222", 0)]
        public void ProfileUsesBothSymbolCodeAndFamilyName(string name, string code, int expected)
        {
            var model = new Model();
            model.Item.Symbol.Family.Name = name;
            model.Item.Symbol.Put(FamilyCode, new Parameter(StorageType.String, code));
            Assert.Equal((RebindTaskProfile)expected, OpeningElevationService.GetProfile(model.Item));
        }

        [Fact]
        public void WallReportUsesVerifiedNativeOffsetRegardlessOfDisplayElevation()
        {
            var model = new Model();
            model.NativeOffset.IsReadOnly = true;
            Assert.Equal(5, OpeningElevationService.GetReportOffset(model.Item, RebindTaskProfile.WallRectangular, model.Level));
            model.Refresh();
            Assert.Equal(5, model.Report.AsDouble());
            Assert.Equal(110, model.Base.AsDouble());
            Assert.Equal(15, model.Point.Z);
        }

        [Fact]
        public void FloorReportUsesActualHostLevelAndOffsetWithoutMovingTheHostOrOpening()
        {
            var model = new Model(slab: true);
            var floor = (Floor)model.Item.Host;
            var hostLevel = model.Document.Add(new Level { ProjectElevation = 35, Elevation = 900 });
            floor.LevelId = hostLevel.Id;
            var hostOffset = new Parameter(StorageType.Double, 2.0) { IsReadOnly = true };
            floor.Put(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM, hostOffset);
            model.NativeOffset.IsReadOnly = true;
            Assert.Equal(27, OpeningElevationService.GetReportOffset(model.Item, RebindTaskProfile.SlabRectangular, model.Level));
            model.Refresh(true, 10);
            Assert.Equal(27, model.Report.AsDouble());
            Assert.Equal(110, model.Base.AsDouble());
            Assert.Equal(15, model.Point.Z);
            Assert.Equal(5, model.NativeOffset.AsDouble());
            Assert.Same(floor, model.Item.Host);
            Assert.Equal(hostLevel.Id, floor.LevelId);
            Assert.Equal(2, hostOffset.AsDouble());
        }

        [Fact]
        public void WindowFamilyMayOmitBaseHeightButStillRequiresReportOffset()
        {
            var model = new Model(window: true);
            OpeningElevationService.ValidateParameters(model.Item);
            model.Refresh();
            Assert.Equal(5, model.Report.AsDouble());
            Assert.Null(model.Base);
            OpeningElevationService.VerifyReports(model.Item, RebindTaskProfile.WallRectangular, model.Level);
        }

        [Fact]
        public void ActualGenericModelCategoryRequiresBaseHeightEvenForKnownWindowFamilyName()
        {
            var model = new Model(window: true);
            model.Item.Category.Id = new ElementId(BuiltInCategory.OST_GenericModel);
            Assert.Throws<InvalidOperationException>(() => OpeningElevationService.ValidateParameters(model.Item));
        }

        [Fact]
        public void ActualWindowsCategoryMayOmitBaseHeightForAnyKnownOpeningProfile()
        {
            var model = new Model();
            model.Item.Category.Id = new ElementId(BuiltInCategory.OST_Windows);
            model.Item.Put(BaseHeight, null);
            model.Refresh();
            Assert.Equal(5, model.Report.AsDouble());
        }

        [Theory]
        [InlineData("report-missing")]
        [InlineData("report-readonly")]
        [InlineData("report-type")]
        [InlineData("base-missing")]
        [InlineData("base-readonly")]
        [InlineData("base-type")]
        [InlineData("window-base-readonly")]
        public void InvalidReportParameterIsRejectedBeforeWriting(string damage)
        {
            var model = new Model(window: damage.StartsWith("window"));
            bool report = damage.StartsWith("report");
            Guid id = report ? ReportOffset : BaseHeight;
            model.Item.Put(id, damage.EndsWith("missing") ? null
                : new Parameter(damage.EndsWith("type") ? StorageType.String : StorageType.Double,
                    damage.EndsWith("type") ? (object)"not a length" : -99.0)
                    { IsReadOnly = damage.EndsWith("readonly") });
            Assert.Throws<InvalidOperationException>(() => OpeningElevationService.ValidateParameters(model.Item));
            Assert.Throws<InvalidOperationException>(() => model.Refresh());
            Assert.Equal(5, model.NativeOffset.AsDouble());
            Assert.Equal(15, model.Point.Z);
        }

        [Theory]
        [InlineData("unknown-profile")]
        [InlineData("missing-host")]
        [InlineData("wrong-host")]
        [InlineData("missing-level")]
        [InlineData("missing-native-offset")]
        [InlineData("wrong-native-type")]
        [InlineData("native-nan")]
        [InlineData("world-z-mismatch")]
        public void InvalidWallContextCannotOverwriteReports(string damage)
        {
            var model = new Model();
            switch (damage)
            {
                case "unknown-profile": model.Item.Symbol.Family.Name = "Unknown"; break;
                case "missing-host": model.Item.Host = null; break;
                case "wrong-host": model.Item.Host = model.Document.Add(new Floor()); break;
                case "missing-level": model.Item.LevelId = ElementId.InvalidElementId; break;
                case "missing-native-offset": model.Item.Put(BuiltInParameter.INSTANCE_ELEVATION_PARAM, null); break;
                case "wrong-native-type": model.Item.Put(BuiltInParameter.INSTANCE_ELEVATION_PARAM, new Parameter(StorageType.String, "5")); break;
                case "native-nan": model.NativeOffset.Set(double.NaN); break;
                case "world-z-mismatch": ((LocationPoint)model.Item.Location).Point = new XYZ(1, 2, 17); break;
            }
            Assert.Throws<InvalidOperationException>(() => model.Refresh());
            Assert.Equal(-99, model.Report.AsDouble());
            Assert.Equal(-99, model.Base.AsDouble());
        }

        [Theory]
        [InlineData("missing-host-level")]
        [InlineData("missing-host-offset")]
        [InlineData("host-offset-nan")]
        public void InvalidFloorContextDoesNotProduceZeroOffset(string damage)
        {
            var model = new Model(slab: true);
            if (damage == "missing-host-level") model.Item.Host.LevelId = ElementId.InvalidElementId;
            else model.Item.Host.Put(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM,
                damage == "missing-host-offset" ? null : new Parameter(StorageType.Double, double.NaN));
            Assert.Throws<InvalidOperationException>(() => model.Refresh());
            Assert.Equal(-99, model.Report.AsDouble());
        }

        [Theory]
        [InlineData(1005.0006, 10, 1000)]
        [InlineData(1004.9994, 10, 1000)]
        [InlineData(1005.0056, 10, 1010)]
        public void WallHeightRoundingPreservesTwoMillimeterDecimalPlacesBeforeIncrement(double mm, double increment, double expectedMm)
        {
            var model = new Model();
            model.NativeOffset.Set(mm / 304.8);
            int regenerations = 0;
            model.Document.OnRegenerate = () =>
            {
                regenerations++;
                Assert.Equal(expectedMm / 304.8, model.NativeOffset.AsDouble(), 10);
                Assert.Equal(-99, model.Report.AsDouble());
            };
            model.Refresh(true, increment);
            Assert.Equal(1, regenerations);
            Assert.Equal(expectedMm / 304.8, model.Report.AsDouble(), 10);
            Assert.Equal(model.Level.ProjectElevation + expectedMm / 304.8, model.Point.Z, 10);
        }

        [Fact]
        public void ZeroIncrementRetainsSixDecimalInternalUnitRounding()
        {
            var model = new Model();
            model.NativeOffset.Set(5.12345678);
            model.Refresh(true, 0);
            Assert.Equal(5.123457, model.NativeOffset.AsDouble(), 10);
            Assert.Equal(5.123457, model.Report.AsDouble(), 10);
        }

        [Theory]
        [InlineData("readonly")]
        [InlineData("rejected")]
        [InlineData("negative-increment")]
        public void InvalidHeightRoundingIsRejected(string damage)
        {
            var model = new Model();
            model.NativeOffset.IsReadOnly = damage == "readonly";
            model.NativeOffset.RejectSet = damage == "rejected";
            Assert.Throws<InvalidOperationException>(() => model.Refresh(true, damage == "negative-increment" ? -10 : 10));
            Assert.Equal(-99, model.Report.AsDouble());
        }

        [Theory]
        [InlineData("report")]
        [InlineData("base")]
        public void RejectedReportWriteIsNotCountedAsSuccess(string rejected)
        {
            var model = new Model();
            (rejected == "report" ? model.Report : model.Base).RejectSet = true;
            Assert.Throws<InvalidOperationException>(() => model.Refresh());
        }

        [Theory]
        [InlineData("report")]
        [InlineData("base")]
        [InlineData("wrong-level")]
        [InlineData("wrong-profile")]
        public void VerifyReportsDetectsStaleOrMismatchedData(string damage)
        {
            var model = new Model();
            model.Report.Set(5.0); model.Base.Set(110.0);
            if (damage == "report") model.Report.Set(7.0);
            if (damage == "base") model.Base.Set(210.0);
            Level level = damage == "wrong-level" ? model.Document.Add(new Level { ProjectElevation = 20 }) : model.Level;
            RebindTaskProfile profile = damage == "wrong-profile" ? RebindTaskProfile.SlabRectangular : RebindTaskProfile.WallRectangular;
            Assert.Throws<InvalidOperationException>(() => OpeningElevationService.VerifyReports(model.Item, profile, level));
        }

        [Fact]
        public void FatalRegenerationPropagatesWithoutReadingOrWritingReports()
        {
            var model = new Model();
            model.Document.OnRegenerate = () => throw new Autodesk.Revit.Exceptions.RegenerationFailedException("fatal");
            Assert.Throws<Autodesk.Revit.Exceptions.RegenerationFailedException>(() => model.Refresh(true, 10));
            Assert.Equal(0, model.Document.ReadsAfterFatalRegeneration);
            Assert.Equal(-99, model.Report.AsDouble());
        }

        private sealed class Model
        {
            internal Document Document { get; } = new Document();
            internal Level Level { get; }
            internal FamilyInstance Item { get; }
            internal Parameter NativeOffset => Item.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
            internal Parameter Report => Item.get_Parameter(ReportOffset);
            internal Parameter Base => Item.get_Parameter(BaseHeight);
            internal XYZ Point => ((LocationPoint)Item.Location).Point;

            internal Model(bool slab = false, bool window = false)
            {
                Level = Document.Add(new Level { ProjectElevation = 10, Elevation = 110 });
                var family = Document.Add(new Family { Name = window ? "231_Отверстие прямоугольное (Окно_Стена)"
                    : slab ? "Отверстие_Плита_Прямоугольное" : "Отверстие_Стена_Прямоугольное" });
                var symbol = Document.Add(new FamilySymbol { Family = family });
                symbol.Put(FamilyCode, new Parameter(StorageType.String, ""));
                HostObject host = slab ? (HostObject)new Floor() : new Wall();
                Document.Add(host); host.LevelId = Level.Id;
                host.Put(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM, new Parameter(StorageType.Double, 5.0));
                Item = Document.Add(new FamilyInstance { Symbol = symbol, Host = host, LevelId = Level.Id,
                    Category = new Category { Id = new ElementId(window ? BuiltInCategory.OST_Windows : BuiltInCategory.OST_GenericModel) },
                    Location = new LocationPoint { Point = new XYZ(1, 2, 15) } });
                Item.Put(ReportOffset, new Parameter(StorageType.Double, -99.0));
                if (!window) Item.Put(BaseHeight, new Parameter(StorageType.Double, -99.0));
                Item.Put(BuiltInParameter.INSTANCE_ELEVATION_PARAM, new Parameter(StorageType.Double, 5.0));
                NativeOffset.AfterSet = value => ((LocationPoint)Item.Location).Point = new XYZ(1, 2, Level.ProjectElevation + (double)value);
            }

            internal void Refresh(bool round = false, double increment = 0)
            {
                using (var transaction = new Transaction(Document, "Refresh"))
                {
                    transaction.Start();
                    OpeningElevationService.Refresh(Item, round, increment);
                    transaction.Commit();
                }
            }
        }
    }
}
