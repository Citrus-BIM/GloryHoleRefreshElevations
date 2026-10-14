using Autodesk.Revit.DB;
using Xunit;

namespace GloryHoleRefreshElevations.Tests
{
    [Collection("Revit service boundary")]
    public sealed class OpeningDependencyDiagnosticsTests
    {
        [Fact]
        public void AnonymousDependencyWithExtensibleStorageExplainsWhyItCannotFollowOpeningDeletion()
        {
            var model = new LevelRebindingServiceTests.Model();
            var source = model.Create("wall");
            source.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).IsReadOnly = true;
            var dependent = model.Document.Add(new Element { Name = string.Empty });
            dependent.Put(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, new Parameter(StorageType.String, "keep"));
            dependent.EntitySchemaGuids.Add(System.Guid.NewGuid());
            source.Dependents.Add(dependent.Id);

            var report = model.Run(source);

            Assert.Equal(0, report.Recreated);
            Assert.True(dependent.IsValidObject);
            Assert.Contains("параметров=1", Assert.Single(report.Details));
            Assert.Contains("служебных схем=1", Assert.Single(report.Details));
        }

        [Fact]
        public void ExternalDependencyReportIdentifiesObjectAndKeepsOriginals()
        {
            var model = new LevelRebindingServiceTests.Model();
            var source = model.Create("wall");
            source.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).IsReadOnly = true;
            var dependent = model.Document.Add(new Wall
            {
                Name = "Neighbour wall",
                Category = new Category { Id = new ElementId(BuiltInCategory.OST_Walls), CategoryType = CategoryType.Model }
            });
            source.Dependents.Add(dependent.Id);

            var report = model.Run(source);

            Assert.Equal(0, report.Recreated);
            Assert.True(source.IsValidObject);
            Assert.True(dependent.IsValidObject);
            var detail = Assert.Single(report.Details);
            Assert.Contains("Wall", detail);
            Assert.Contains("Neighbour wall", detail);
            Assert.Contains(dependent.Id.ToString(), detail);
            Assert.DoesNotContain("Neighbour wall", Assert.Single(report.Reasons).Key);
        }

        [Fact]
        public void NestedDependencyReportIncludesFamilyAndParentWithoutAssumingSafeDeletion()
        {
            var model = new LevelRebindingServiceTests.Model();
            var source = model.Create("wall");
            source.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM).IsReadOnly = true;
            var dependent = model.Create("slab");
            dependent.SuperComponent = source;
            source.Dependents.Add(dependent.Id);

            var report = model.Run(source);

            Assert.Equal(0, report.Recreated);
            Assert.True(source.IsValidObject);
            Assert.True(dependent.IsValidObject);
            var detail = Assert.Single(report.Details);
            Assert.Contains("FamilyInstance", detail);
            Assert.Contains(dependent.Symbol.Family.Name, detail);
            Assert.Contains("SuperComponent=" + source.Id, detail);
            Assert.Contains("Host=" + dependent.Host.Id, detail);
        }
    }
}
