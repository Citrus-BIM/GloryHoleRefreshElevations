using System;
using Autodesk.Revit.DB;
using Xunit;

namespace GloryHoleRefreshElevations.Tests
{
    public sealed class OpeningReplacementMetadataTests
    {
        private static readonly Guid GhGuid = new Guid("652cfdd0-f868-4433-8fbd-16a517e1b878");
        private static readonly Guid ProjectGuid = new Guid("8b5f2c01-9ad4-4e2f-b8c7-1d0e3a5b7c9d");

        [Fact]
        public void PlainOpeningPreservesGhGuidWithoutLookingForCutter()
        {
            var source = New("old"); var target = New("new");
            source.Put(GhGuid, new Parameter(StorageType.String, "original-guid"));
            target.Put(GhGuid, new Parameter(StorageType.String, "generated-guid"));
            var snapshot = OpeningReplacementMetadata.Capture(source, _ => throw new Exception("Bridge must not be loaded"));
            source.IsValidObject = false;
            snapshot.Apply(target);
            snapshot.Verify(target);
            Assert.Equal("original-guid", target.get_Parameter(GhGuid).AsString());
            Assert.Null(snapshot.AfterCommit);
        }

        [Fact]
        public void MissingTargetGuidCannotSilentlyLoseIdentity()
        {
            var source = New("old"); source.Put(GhGuid, new Parameter(StorageType.String, "identity"));
            var snapshot = OpeningReplacementMetadata.Capture(source);
            Assert.Throws<InvalidOperationException>(() => snapshot.Apply(New("new")));
        }

        [Fact]
        public void VerifyDetectsPostCommitIdentityDamage()
        {
            var source = New("old"); var target = New("new");
            source.Put(GhGuid, new Parameter(StorageType.String, "original"));
            target.Put(GhGuid, new Parameter(StorageType.String, "original"));
            var snapshot = OpeningReplacementMetadata.Capture(source);
            snapshot.Apply(target);
            target.get_Parameter(GhGuid).Set("changed-by-updater");
            Assert.Throws<InvalidOperationException>(() => snapshot.Verify(target));
        }

        [Fact]
        public void MeaningfulHubStampUsesBridgeAndDefersItsPublication()
        {
            var source = New("old"); var target = New("new");
            string project = Guid.NewGuid().ToString();
            source.Put(ProjectGuid, new Parameter(StorageType.String, project));
            target.Put(ProjectGuid, new Parameter(StorageType.String, ""));
            var bridge = new BridgeSnapshot();
            int captures = 0;
            var snapshot = OpeningReplacementMetadata.Capture(source, _ => { captures++; return bridge; });
            source.IsValidObject = false;
            snapshot.Apply(target); snapshot.Verify(target);
            Assert.Equal(1, captures);
            Assert.Equal(project, target.get_Parameter(ProjectGuid).AsString());
            Assert.Equal(1, bridge.Applied); Assert.Equal(1, bridge.Verified);
            Assert.False(bridge.Published);
            Assert.NotNull(snapshot.AfterCommit);
            snapshot.AfterCommit();
            Assert.True(bridge.Published);
        }

        [Fact]
        public void EmptyHubStampDoesNotLoadBridge()
        {
            var source = New("old"); source.Put(ProjectGuid, new Parameter(StorageType.String, ""));
            OpeningReplacementMetadata.Capture(source, _ => throw new Exception("No meaningful context"));
        }

        [Fact]
        public void BridgeFailureIsReportedBeforeOriginalCanBeDeleted()
        {
            var source = New("old"); source.Put(ProjectGuid, new Parameter(StorageType.String, Guid.NewGuid().ToString()));
            Assert.Throws<InvalidOperationException>(() => OpeningReplacementMetadata.Capture(source, _ => null));
            Assert.True(source.IsValidObject);
        }

        [Theory]
        [InlineData("00000000-0000-0000-0000-000000000000", false)]
        [InlineData(" ", false)]
        [InlineData("0", true)]
        public void EmptyLegacyHubValuesDoNotRequireCutter(string value, bool numeric)
        {
            var source = New("old");
            source.Put(numeric ? new Guid("9c603d12-0be5-5f3a-c9d8-2e1f4b6c8d0e") : ProjectGuid,
                new Parameter(StorageType.String, value));
            OpeningReplacementMetadata.Capture(source, _ => throw new Exception("No meaningful Hub identity"));
        }

        [Fact]
        public void UnknownSchemaIsRejectedBeforeBridgeLookupOrDeletion()
        {
            var source = New("old"); source.EntitySchemaGuids.Add(Guid.NewGuid());
            var failure = Assert.Throws<InvalidOperationException>(() => OpeningReplacementMetadata.Capture(source,
                _ => throw new Exception("Unknown ES must be rejected first")));
            Assert.Contains("неизвестные", failure.Message);
            Assert.True(source.IsValidObject);
        }

        [Fact]
        public void DirectLevelMetadataLeavesUnknownSchemaWithoutLoadingCutter()
        {
            var source = New("old");
            source.EntitySchemaGuids.Add(Guid.NewGuid());
            var snapshot = OpeningReplacementMetadata.Capture(source,
                _ => throw new Exception("Unknown storage must not load Cutter"), false);
            snapshot.Apply(source);
            snapshot.Verify(source);
            Assert.Null(snapshot.AfterCommit);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DirectLevelMetadataCannotBeAppliedToReplacementOrLoseUnknownSchema(bool removeSchema)
        {
            var source = New("old"); var target = removeSchema ? source : New("new");
            var schema = Guid.NewGuid();
            source.EntitySchemaGuids.Add(schema);
            var snapshot = OpeningReplacementMetadata.Capture(source, _ => null, false);
            if (removeSchema) source.EntitySchemaGuids.Clear();
            else target.EntitySchemaGuids.Add(schema);
            Assert.Throws<InvalidOperationException>(() => snapshot.Apply(target));
            Assert.Throws<InvalidOperationException>(() => snapshot.Verify(target));
        }

        [Fact]
        public void DirectLevelMetadataStillRefreshesKnownCutterDataAlongsideUnknownSchema()
        {
            var source = New("old");
            source.EntitySchemaGuids.Add(Guid.NewGuid());
            source.EntitySchemaGuids.Add(new Guid("a841b1f0-1c9a-4c78-80ce-6ca58a8ad564"));
            var bridge = new BridgeSnapshot();
            var snapshot = OpeningReplacementMetadata.Capture(source, _ => bridge, false);
            snapshot.Apply(source); snapshot.Verify(source);
            Assert.Equal(1, bridge.Applied);
            Assert.Equal(1, bridge.Verified);
        }

        [Fact]
        public void OutdatedBridgeContractIsRejectedBeforeDeletion()
        {
            var source = New("old"); source.EntitySchemaGuids.Add(new Guid("7fe4f25b-8156-45d4-956f-6cdccf518f70"));
            Assert.Throws<InvalidOperationException>(() => OpeningReplacementMetadata.Capture(source, _ => new object()));
            Assert.True(source.IsValidObject);
        }

        private static FamilyInstance New(string uniqueId) => new Document().Add(new FamilyInstance { UniqueId = uniqueId });

        public sealed class BridgeSnapshot
        {
            public int Applied, Verified;
            public bool Published;
            public void Apply(FamilyInstance target) { Applied++; }
            public void Verify(FamilyInstance target) { Verified++; }
            public Action AfterCommit => () => Published = true;
        }
    }
}
