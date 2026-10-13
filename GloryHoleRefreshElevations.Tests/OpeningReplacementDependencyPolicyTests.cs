using System;
using Autodesk.Revit.DB;
using Xunit;

namespace GloryHoleRefreshElevations.Tests
{
    public sealed class OpeningReplacementDependencyPolicyTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" \t")]
        public void EmptyAnonymousDirectDependencyCanFollowOpeningDeletion(string? name)
        {
            var dependent = Anonymous();
            dependent.Name = name;
            Assert.True(OpeningReplacementDependencyPolicy.CanDeleteWithOpening(dependent));
        }

        [Fact]
        public void ExistingAnnotationPolicyRemainsAllowed()
        {
            var annotation = new Element
            {
                Name = "Opening tag",
                Category = new Category { CategoryType = CategoryType.Annotation },
                ViewSpecific = true,
                OwnerViewId = new ElementId(12)
            };
            annotation.Put(Guid.NewGuid(), new Parameter(StorageType.String, "tag data"));
            Assert.True(OpeningReplacementDependencyPolicy.CanDeleteWithOpening(annotation));
        }

        [Theory]
        [InlineData("named")]
        [InlineData("category")]
        [InlineData("location")]
        [InlineData("type")]
        [InlineData("parameter")]
        [InlineData("empty-parameter")]
        [InlineData("readonly-parameter")]
        [InlineData("schema")]
        [InlineData("pinned")]
        [InlineData("group")]
        [InlineData("view-specific")]
        [InlineData("owner-view")]
        [InlineData("invalid")]
        public void IdentifiedOrDataBearingElementRemainsProtected(string feature)
        {
            var dependent = Anonymous();
            switch (feature)
            {
                case "named": dependent.Name = "User object"; break;
                case "category": dependent.Category = new Category { CategoryType = CategoryType.Model }; break;
                case "location": dependent.Location = new LocationPoint { Point = XYZ.Zero }; break;
                case "type": dependent.TypeId = new ElementId(22); break;
                case "parameter": dependent.Put(Guid.NewGuid(), new Parameter(StorageType.String, "identity")); break;
                case "empty-parameter": dependent.Put(Guid.NewGuid(), new Parameter(StorageType.String, null)); break;
                case "readonly-parameter": dependent.Put(BuiltInParameter.ELEM_PARTITION_PARAM,
                    new Parameter(StorageType.Integer, 1) { IsReadOnly = true }); break;
                case "schema": dependent.EntitySchemaGuids.Add(Guid.NewGuid()); break;
                case "pinned": dependent.Pinned = true; break;
                case "group": dependent.GroupId = new ElementId(33); break;
                case "view-specific": dependent.ViewSpecific = true; break;
                case "owner-view": dependent.OwnerViewId = new ElementId(44); break;
                case "invalid": dependent.IsValidObject = false; break;
                default: throw new ArgumentOutOfRangeException(nameof(feature));
            }
            Assert.False(OpeningReplacementDependencyPolicy.CanDeleteWithOpening(dependent));
        }

        [Fact]
        public void AnonymousSubclassesAreNotTreatedAsOpaqueInternalRecords()
        {
            Assert.False(OpeningReplacementDependencyPolicy.CanDeleteWithOpening(new Wall { Name = "" }));
            Assert.False(OpeningReplacementDependencyPolicy.CanDeleteWithOpening(new FamilyInstance { Name = "" }));
            Assert.False(OpeningReplacementDependencyPolicy.CanDeleteWithOpening(new OtherElement { Name = "" }));
        }

        [Fact]
        public void MissingElementCannotBeWhitelisted()
            => Assert.False(OpeningReplacementDependencyPolicy.CanDeleteWithOpening(null));

        private static Element Anonymous() => new Element { Name = "" };
        private sealed class OtherElement : Element { }
    }
}
