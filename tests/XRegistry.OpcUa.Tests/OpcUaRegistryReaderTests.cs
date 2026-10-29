// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Opc.Ua;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.OpcUa;
using XRegistry.Federation;

namespace XRegistry.OpcUa.Tests;

public class OpcUaRegistryReaderTests
{
    private static readonly NodeId File = new("Model", 1);
    private static readonly NodeId GroupNode = new("group-team-a", 1);
    private static readonly NodeId LogicalResourceNode = new("logical-schema-a", 1);
    private static readonly NodeId VersionsNode = new("schema-a-versions", 1);
    private static readonly NodeId DefaultVersionNode = new("schema-a-default-v1", 1);
    private static readonly NodeId VersionTwoNode = new("schema-a-v2", 1);
    private static readonly NodeId ResourceType = new("ResourceType", 1);
    private static readonly NodeId ResourceVersionsType = new("ResourceVersionsType", 1);
    private static readonly NodeId AttributesType = new("AttributesType", 1);
    private const string TestModel =
        """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema"}}}}}""";

    [Test]
    public async Task FileReadContinuesPastShortReadsUntilAnEmptyChunkThenCloses()
    {
        var session = new FakeSession { Chunks = { new byte[] { 1, 2 }, new byte[] { 3 }, Array.Empty<byte>() } };
        var reader = await ValidatedReader(session);

        var data = await reader.ReadFileAsync(File);

        await Assert.That(data).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(session.OpenMode).IsEqualTo((byte)1);
    }

    [Test]
    public async Task ReadFailureIsRetainedWhenCloseAlsoFails()
    {
        var session = new FakeSession { ReadFailure = new InvalidOperationException("read"), CloseFailure = new InvalidOperationException("close") };
        var reader = await ValidatedReader(session);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await reader.ReadFileAsync(File));

        await Assert.That(error!.Message).IsEqualTo("read");
        await Assert.That(error.Data.Contains("OpcUa.FileCloseFailure")).IsTrue();
        await Assert.That(session.Closed).IsEqualTo(1);
    }

    [Test]
    public async Task CloseFailureAfterCompleteReadIsReported()
    {
        var session = new FakeSession { Chunks = { Array.Empty<byte>() }, CloseFailure = new InvalidOperationException("close") };
        var reader = await ValidatedReader(session);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadFileAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.Unavailable);
    }

    [Test]
    public async Task BrowseFailureRetainsContinuationReleaseFailureDiagnostic()
    {
        var session = new FakeSession
        {
            BrowseNextFailure = new FederationException(FederationErrorCode.Unavailable, "browse"),
            ReleaseFailure = new InvalidOperationException("release")
        };
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([Ref("a")], [9]));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.BrowseAllAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.Unavailable);
        await Assert.That(error.Data.Contains("OpcUa.ContinuationReleaseFailure")).IsTrue();
    }

    [Test]
    public async Task DoesNotReleaseContinuationAlreadyConsumedBySuccessfulBrowseNext()
    {
        var failure = new FederationException(FederationErrorCode.InvalidPackage, "mapping failed");
        failure.Data["OpcUa.ContinuationConsumed"] = true;
        var session = new FakeSession { BrowseNextFailure = failure };
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([Ref("component")], [3]));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.BrowseAllAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.Released.Count).IsEqualTo(0);
    }

    [Test]
    public async Task OversizedFileFailsWithLimitAndStillCloses()
    {
        var session = new FakeSession { Chunks = { new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 } } };
        var reader = await ValidatedReader(session, maxBytes: 4);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadFileAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(session.Closed).IsEqualTo(1);
    }

    [Test]
    public async Task BrowseFollowsEveryContinuationPoint()
    {
        var session = new FakeSession();
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([Ref("a")], [1]));
        session.Pages.Enqueue(new([Ref("b")], [2]));
        session.Pages.Enqueue(new([Ref("c")]));

        var all = await reader.BrowseAllAsync(File);

        await Assert.That(string.Join(",", all.Select(r => r.BrowseName.Name))).IsEqualTo("a,b,c");
    }

    [Test]
    public async Task ExpiredContinuationPointNeverYieldsACompleteCollection()
    {
        var session = new FakeSession { BrowseNextFailure = new FederationException(FederationErrorCode.Unavailable, "expired") };
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([Ref("a")], [1]));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.BrowseAllAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.Unavailable);
    }

    [Test]
    public async Task BudgetOverrunReleasesTheOutstandingContinuationPoint()
    {
        var session = new FakeSession();
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session, maxReferences: 1);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([Ref("a"), Ref("b")], [9]));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.BrowseAllAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(session.Released.Single()).IsEquivalentTo(new byte[] { 9 });
    }

    [Test]
    public async Task RootThatIsNotARegistryIsNotFoundNeverSubstituted()
    {
        var target = OpcUaFederationTargetTests.Target("""{"registryroot":"nsu=urn%3Axregistry;s=Registry"}""");
        var namespaces = new NamespaceTable(["http://opcfoundation.org/UA/", "urn:xregistry"]);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await new OpcUaRegistryReader(new FakeSession { IsRegistry = false }).VerifyRootAsync(target, namespaces));
        var root = await new OpcUaRegistryReader(new FakeSession()).VerifyRootAsync(target, namespaces);

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(root.NamespaceIndex).IsEqualTo((ushort)1);
    }

    [Test]
    public async Task SpecVersionIsReadAndCheckedBeforeUse()
    {
        static FakeSession Components() { var s = new FakeSession(); s.Pages.Enqueue(new([SpecVersionRef()])); return s; }

        var version = await new OpcUaRegistryReader(Components()).ReadSpecVersionAsync(File, v => v.StartsWith("1.0", StringComparison.Ordinal));
        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await new OpcUaRegistryReader(Components()).ReadSpecVersionAsync(File, _ => false));

        await Assert.That(version).IsEqualTo("1.0-rc4");
        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedVersion);
    }

    [Test]
    public async Task RejectedSpecVersionRecheckClearsThePreviouslyAcceptedSessionVersion()
    {
        var session = new FakeSession();
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([SpecVersionRef()]));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadSpecVersionAsync(File, _ => false));
        var browseError = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.BrowseAllAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedVersion);
        await Assert.That(browseError!.Code).IsEqualTo(FederationErrorCode.UnsupportedVersion);
    }

    [Test]
    public async Task ModelAndCapabilitiesAreParsedFromTheirNamedFileObjects()
    {
        var session = new FakeSession { Chunks = { System.Text.Encoding.UTF8.GetBytes("{\"a\":1}"), Array.Empty<byte>(), System.Text.Encoding.UTF8.GetBytes("nope"), Array.Empty<byte>() } };
        session.Pages.Enqueue(new([SpecVersionRef()]));
        session.Pages.Enqueue(new([FileRef("Model")]));
        session.Pages.Enqueue(new([FileRef("Capabilities")]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);

        using var model = await reader.ReadModelAsync(File);
        var error = await Assert.ThrowsAsync<FederationException>(async () => await reader.ReadCapabilitiesAsync(File));

        await Assert.That(model.RootElement.GetProperty("a").GetInt32()).IsEqualTo(1);
        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    public async Task FileDocumentWriteReplacesSuppliedFileTypeNode()
    {
        var (session, writer) = await WriterForResource();
        var file = new NodeId("Capabilities", 1);

        await writer.ReplaceFileDocumentAsync(file, new byte[] { 8, 9 });

        await Assert.That(session.LastOpenedFile).IsEqualTo(file);
        await Assert.That(session.OpenMode).IsEqualTo((byte)6);
        await Assert.That(session.WrittenChunks.Single()).IsEquivalentTo(new byte[] { 8, 9 });
        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    public async Task ResourceDocumentWriteAcceptsExactByteBudget()
    {
        var (session, writer) = await WriterForResource(maxBytes: 256, chunkSize: 128);

        await writer.ReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
            ResourceType, new byte[256]);

        await Assert.That(session.WrittenChunks.Count).IsEqualTo(2);
        await Assert.That(session.WrittenChunks.Sum(chunk => chunk.Length)).IsEqualTo(256);
        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    public async Task MissingOrDuplicateComponentIsNotGuessed()
    {
        var none = new FakeSession();
        var twice = new FakeSession();
        var missingReader = new OpcUaRegistryReader(none);
        var duplicateReader = new OpcUaRegistryReader(twice);
        none.Pages.Enqueue(new([SpecVersionRef()]));
        twice.Pages.Enqueue(new([SpecVersionRef()]));
        await missingReader.ReadSpecVersionAsync(File, _ => true);
        await duplicateReader.ReadSpecVersionAsync(File, _ => true);
        none.Pages.Enqueue(new([]));
        twice.Pages.Enqueue(new([FileRef("Model"), FileRef("Model")]));

        var absent = await Assert.ThrowsAsync<FederationException>(async () => await missingReader.ReadModelAsync(File));
        var duplicate = await Assert.ThrowsAsync<FederationException>(async () => await duplicateReader.ReadModelAsync(File));

        await Assert.That(absent!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(duplicate!.Code).IsEqualTo(FederationErrorCode.Ambiguous);
    }

    [Test]
    public async Task SpecVersionMustBeAStringVariableComponent()
    {
        var session = new FakeSession();
        session.Pages.Enqueue(new([new OpcUaReference(new NodeId("SpecVersion", 1),
            new QualifiedName("SpecVersion", 1), NodeClass.Object)]));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await new OpcUaRegistryReader(session).ReadSpecVersionAsync(File, _ => true));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
    }

    [Test]
    public async Task ModelComponentMustBeAFileTypeObject()
    {
        var session = new FakeSession();
        session.Pages.Enqueue(new([SpecVersionRef()]));
        session.Pages.Enqueue(new([new OpcUaReference(new NodeId("Model", 1),
            new QualifiedName("Model", 1), NodeClass.Object, ObjectTypeIds.BaseObjectType)]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);

        var error = await Assert.ThrowsAsync<FederationException>(async () => await reader.ReadModelAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(session.Closed).IsEqualTo(0);
    }

    [Test]
    public async Task JsonValueComponentRequiresVariableAndReturnsItsTypedValue()
    {
        var session = new FakeSession
        {
            JsonValue = Value("42", BuiltInType.UInt32)
        };
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([new OpcUaReference(new NodeId("Epoch", 1),
            new QualifiedName("Epoch", 1), NodeClass.Variable)]));

        var value = await reader.ReadJsonValueComponentAsync(File, "Epoch");

        await Assert.That(value.GetUInt32()).IsEqualTo(42u);
    }

    [Test]
    public async Task RegistryPropertyMappingRequiresCoreFieldsAndPreservesTypedValues()
    {
        var session = new FakeSession
        {
            JsonValues =
            {
                ["RegistryId"] = Value("\"registry-1\"", BuiltInType.String),
                ["SpecVersion"] = Value("\"1.0-rc4\"", BuiltInType.String),
                ["Xid"] = Value("\"/\"", BuiltInType.String),
                ["Epoch"] = Value("7", BuiltInType.UInt32),
                ["CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["Name"] = Value("\"Production\"", BuiltInType.String)
            }
        };
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([
            VariableRef("RegistryId"), VariableRef("SpecVersion"), VariableRef("Xid"),
            VariableRef("Epoch"), VariableRef("CreatedAt"), VariableRef("ModifiedAt"),
            VariableRef("Name")
        ]));

        var metadata = await reader.ReadRegistryPropertiesAsync(File);

        await Assert.That(metadata.GetProperty("registryid").GetString()).IsEqualTo("registry-1");
        await Assert.That(metadata.GetProperty("specversion").GetString()).IsEqualTo("1.0-rc4");
        await Assert.That(metadata.GetProperty("xid").GetString()).IsEqualTo("/");
        await Assert.That(metadata.GetProperty("epoch").GetUInt32()).IsEqualTo(7u);
        await Assert.That(metadata.GetProperty("name").GetString()).IsEqualTo("Production");
    }

    [Test]
    public async Task RegistryPropertyMappingRejectsAnInvalidEpochValue()
    {
        var session = new FakeSession
        {
            JsonValues =
            {
                ["RegistryId"] = Value("\"registry-1\"", BuiltInType.String),
                ["SpecVersion"] = Value("\"1.0-rc4\"", BuiltInType.String),
                ["Xid"] = Value("\"/\"", BuiltInType.String),
                ["Epoch"] = Value("-1", BuiltInType.Int32),
                ["CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime)
            }
        };
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([
            VariableRef("RegistryId"), VariableRef("SpecVersion"), VariableRef("Xid"),
            VariableRef("Epoch"), VariableRef("CreatedAt"), VariableRef("ModifiedAt")
        ]));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadRegistryPropertiesAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task RegistryPropertyMappingRejectsTimestampsWithoutAnRfc3339Zone()
    {
        var session = new FakeSession
        {
            JsonValues =
            {
                ["RegistryId"] = Value("\"registry-1\"", BuiltInType.String),
                ["SpecVersion"] = Value("\"1.0-rc4\"", BuiltInType.String),
                ["Xid"] = Value("\"/\"", BuiltInType.String),
                ["Epoch"] = Value("7", BuiltInType.UInt32),
                ["CreatedAt"] = Value("\"2026-01-02T03:04:05\"", BuiltInType.DateTime),
                ["ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime)
            }
        };
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([
            VariableRef("RegistryId"), VariableRef("SpecVersion"), VariableRef("Xid"),
            VariableRef("Epoch"), VariableRef("CreatedAt"), VariableRef("ModifiedAt")
        ]));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadRegistryPropertiesAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task GroupPropertiesRequireTheExpectedTypeAndMapCoreMetadata()
    {
        var groupType = new NodeId("GroupType", 1);
        var session = new FakeSession
        {
            JsonValues =
            {
                ["GroupId"] = Value("\"team-a\"", BuiltInType.String),
                ["Xid"] = Value("\"/teams/team-a\"", BuiltInType.String),
                ["Epoch"] = Value("3", BuiltInType.UInt32),
                ["CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["Name"] = Value("\"Team A\"", BuiltInType.String)
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([GroupRef("team-a", groupType)]));
        session.Pages.Enqueue(new([
            VariableRef("GroupId"), VariableRef("Xid"), VariableRef("Epoch"), VariableRef("Name"),
            VariableRef("CreatedAt"), VariableRef("ModifiedAt")
        ]));

        var group = await reader.ReadGroupPropertiesAsync(File, "teams", "team-a", groupType);

        await Assert.That(group.GetProperty("groupid").GetString()).IsEqualTo("team-a");
        await Assert.That(group.GetProperty("xid").GetString()).IsEqualTo("/teams/team-a");
        await Assert.That(group.GetProperty("epoch").GetUInt32()).IsEqualTo(3u);
        await Assert.That(group.GetProperty("name").GetString()).IsEqualTo("Team A");
    }

    [Test]
    public async Task GroupPropertyMappingRejectsXidThatDoesNotMatchTheSelectedGroup()
    {
        var groupType = new NodeId("GroupType", 1);
        var session = new FakeSession
        {
            JsonValues =
            {
                ["GroupId"] = Value("\"team-a\"", BuiltInType.String),
                ["Xid"] = Value("\"/teams/team-b\"", BuiltInType.String),
                ["Epoch"] = Value("3", BuiltInType.UInt32),
                ["Name"] = Value("\"Team A\"", BuiltInType.String),
                ["CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime)
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([GroupRef("team-a", groupType)]));
        session.Pages.Enqueue(new([
            VariableRef("GroupId"), VariableRef("Xid"), VariableRef("Epoch"),
            VariableRef("Name"), VariableRef("CreatedAt"), VariableRef("ModifiedAt")
        ]));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadGroupPropertiesAsync(File, "teams", "team-a", groupType));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task GroupCollectionIncludesOnlyInstancesOfTheDeclaredGroupType()
    {
        var groupType = new NodeId("GroupType", 1);
        var session = new FakeSession
        {
            JsonValues =
            {
                ["GroupId"] = Value("\"team-a\"", BuiltInType.String),
                ["Xid"] = Value("\"/teams/team-a\"", BuiltInType.String),
                ["Epoch"] = Value("3", BuiltInType.UInt32),
                ["Name"] = Value("\"Team A\"", BuiltInType.String),
                ["CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime)
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([
            GroupRef("team-a", groupType), GroupRef("not-a-group", new NodeId("ResourceType", 1))
        ]));
        session.Pages.Enqueue(new([
            VariableRef("GroupId"), VariableRef("Xid"), VariableRef("Epoch"),
            VariableRef("Name"), VariableRef("CreatedAt"), VariableRef("ModifiedAt")
        ]));

        var groups = await reader.ReadGroupCollectionAsync(File, "teams", groupType);

        await Assert.That(groups.GetArrayLength()).IsEqualTo(1);
        await Assert.That(groups[0].GetProperty("groupid").GetString()).IsEqualTo("team-a");
    }

    [Test]
    public async Task GroupCollectionMustBeDeclaredByTheRegistryModel()
    {
        var reader = await ModelReader(new FakeSession());

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadGroupCollectionAsync(File, "unknown", new NodeId("GroupType", 1)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
    }

    [Test]
    public async Task ReaderResolvesLogicalResourceAndProjectsDefaultVersion()
    {
        var session = new FakeSession
        {
            JsonValues =
            {
                ["logical:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["logical:VersionId"] = Value("\"v1\"", BuiltInType.String),
                ["logical:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["logical:Epoch"] = Value("41", BuiltInType.UInt32),
                ["logical:Name"] = Value("\"Schema A default\"", BuiltInType.String),
                ["logical:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["logical:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["logical:Format"] = Value("\"application/json\"", BuiltInType.String),
                ["logical:ContentType"] = Value("\"application/schema+json\"", BuiltInType.String),
                ["logical:MetaEpoch"] = Value("17", BuiltInType.UInt32),
                ["default:VersionId"] = Value("\"decoy-version\"", BuiltInType.String),
                ["default:Epoch"] = Value("99", BuiltInType.UInt32),
                ["default:Name"] = Value("\"Must not be browsed\"", BuiltInType.String)
            },
            StringValues =
            {
                ["logical-label:region"] = "logical-default-label",
                ["default-label:region"] = "exact-version-label"
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId", "logical"),
            VariableRef("VersionId", "logical"), VariableRef("Xid", "logical"),
            VariableRef("Epoch", "logical"), VariableRef("Name", "logical"),
            VariableRef("CreatedAt", "logical"), VariableRef("ModifiedAt", "logical"),
            VariableRef("Format", "logical"), VariableRef("ContentType", "logical")
        ]));

        var resource = await reader.ReadResourcePropertiesAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType);
        session.Pages.Enqueue(new([
            new OpcUaReference(new NodeId("logical-labels", 1), new QualifiedName("Labels", 1),
                NodeClass.Object, AttributesType)
        ]));
        session.Pages.Enqueue(new([
            new OpcUaReference(new NodeId("logical-label:region", 1), new QualifiedName("region", 1),
                NodeClass.Variable)
        ]));
        var labels = await reader.ReadAttributesAsync(LogicalResourceNode, AttributesType);

        await Assert.That(resource.GetProperty("resourceid").GetString()).IsEqualTo("schema-a");
        await Assert.That(resource.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(resource.GetProperty("epoch").GetUInt32()).IsEqualTo(41u);
        await Assert.That(resource.GetProperty("name").GetString()).IsEqualTo("Schema A default");
        await Assert.That(resource.GetProperty("format").GetString()).IsEqualTo("application/json");
        await Assert.That(resource.GetProperty("contenttype").GetString()).IsEqualTo("application/schema+json");
        await Assert.That(labels.GetProperty("region").GetString()).IsEqualTo("logical-default-label");
        await Assert.That(NodeIdTrace(session.BrowsedNodes)).IsEqualTo(
            "Model|Model|group-team-a|logical-schema-a|logical-schema-a|logical-labels");
        await Assert.That(session.BrowsedNodes.Contains(DefaultVersionNode)).IsFalse();
    }

    [Test]
    public async Task ResourceMetaPropertiesMapMetaBrowseNamesAndDefaultVersionSeparately()
    {
        var session = new FakeSession
        {
            JsonValues =
            {
                ["meta:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["meta:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["meta:MetaEpoch"] = Value("17", BuiltInType.UInt32),
                ["meta:MetaCreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["meta:MetaModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["meta:MetaName"] = Value("\"Schema metadata\"", BuiltInType.String),
                ["meta:MetaDescription"] = Value("\"Meta description\"", BuiltInType.String),
                ["meta:MetaDocumentation"] = Value("\"https://example.test/schema\"", BuiltInType.String),
                ["meta:MetaDefaultVersionSticky"] = Value("true", BuiltInType.Boolean),
                ["meta:MetaReadonly"] = Value("false", BuiltInType.Boolean),
                ["meta:MetaCompatibility"] = Value("\"BACKWARD\"", BuiltInType.String),
                ["meta:VersionId"] = Value("\"v2\"", BuiltInType.String)
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId", "meta"), VariableRef("Xid", "meta"),
            VariableRef("MetaEpoch", "meta"), VariableRef("MetaCreatedAt", "meta"),
            VariableRef("MetaModifiedAt", "meta"), VariableRef("MetaName", "meta"),
            VariableRef("MetaDescription", "meta"), VariableRef("MetaDocumentation", "meta"),
            VariableRef("MetaDefaultVersionSticky", "meta"), VariableRef("MetaReadonly", "meta"),
            VariableRef("MetaCompatibility", "meta"), VariableRef("VersionId", "meta")
        ]));

        var meta = await reader.ReadResourceMetaPropertiesAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType);

        await Assert.That(meta.GetProperty("xid").GetString()).IsEqualTo("/teams/team-a/schemas/schema-a/meta");
        await Assert.That(meta.GetProperty("epoch").GetUInt32()).IsEqualTo(17u);
        await Assert.That(meta.GetProperty("createdat").GetString()).IsEqualTo("2026-01-02T03:04:05Z");
        await Assert.That(meta.GetProperty("modifiedat").GetString()).IsEqualTo("2026-01-03T03:04:05Z");
        await Assert.That(meta.GetProperty("name").GetString()).IsEqualTo("Schema metadata");
        await Assert.That(meta.GetProperty("description").GetString()).IsEqualTo("Meta description");
        await Assert.That(meta.GetProperty("documentation").GetString()).IsEqualTo("https://example.test/schema");
        await Assert.That(meta.GetProperty("defaultversionsticky").GetBoolean()).IsTrue();
        await Assert.That(meta.GetProperty("readonly").GetBoolean()).IsFalse();
        await Assert.That(meta.GetProperty("compatibility").GetString()).IsEqualTo("BACKWARD");
        await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo("v2");
        await Assert.That(meta.TryGetProperty("versionid", out _)).IsFalse();
    }

    [Test]
    public async Task ResourceMetaXrefDoesNotBecomeDefaultVersionId()
    {
        var session = new FakeSession
        {
            JsonValues =
            {
                ["meta:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["meta:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["meta:MetaXref"] = Value("\"/teams/team-a/schemas/schema-target\"", BuiltInType.String)
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId", "meta"), VariableRef("Xid", "meta"), VariableRef("MetaXref", "meta")
        ]));

        var meta = await reader.ReadResourceMetaPropertiesAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType);

        await Assert.That(meta.GetProperty("xref").GetString()).IsEqualTo("/teams/team-a/schemas/schema-target");
        await Assert.That(meta.TryGetProperty("defaultversionid", out _)).IsFalse();
    }

    [Test]
    public async Task ResourceMetaDeprecatedPreservesTypedOptionalFields()
    {
        var meta = await ReadMetaWithDeprecated(Value(
            """{"Effective":"2026-01-02T03:04:05Z","Removal":"2027-01-02T03:04:05+01:00","Alternative":"../replacement","Documentation":"https://example.test/deprecation"}""",
            BuiltInType.ExtensionObject, "DeprecationDataType"));

        var deprecated = meta.GetProperty("deprecated");
        await Assert.That(deprecated.GetProperty("effective").GetString()).IsEqualTo("2026-01-02T03:04:05Z");
        await Assert.That(deprecated.GetProperty("removal").GetString()).IsEqualTo("2027-01-02T03:04:05+01:00");
        await Assert.That(deprecated.GetProperty("alternative").GetString()).IsEqualTo("../replacement");
        await Assert.That(deprecated.GetProperty("documentation").GetString())
            .IsEqualTo("https://example.test/deprecation");
    }

    [Test]
    public async Task ResourceMetaDeprecatedRejectsTheWrongStructuredDataType()
    {
        var exception = await Assert.ThrowsAsync<FederationException>(async () =>
            await ReadMetaWithDeprecated(Value(
                """{"Effective":"2026-01-02T03:04:05Z"}""",
                BuiltInType.ExtensionObject, "VendorDeprecationDataType")))
            ?? throw new InvalidOperationException("The wrong MetaDeprecated DataType was accepted.");

        await Assert.That(exception.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    [Arguments("""{"Effective":"2026-01-02T03:04:05"}""")]
    [Arguments("""{"Documentation":"not a uri with spaces"}""")]
    public async Task ResourceMetaDeprecatedRejectsInvalidTypedFields(string deprecated)
    {
        var exception = await Assert.ThrowsAsync<FederationException>(async () =>
            await ReadMetaWithDeprecated(Value(deprecated, BuiltInType.ExtensionObject, "DeprecationDataType")))
            ?? throw new InvalidOperationException("The invalid MetaDeprecated field was accepted.");

        await Assert.That(exception.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task RegistryPropertiesIncludeModelledTypedExtensionAttributes()
    {
        const string model =
            """{"attributes":{"vendorconfig":{"type":"object","attributes":{"origins":{"type":"array","item":{"type":"string"}},"region":"string","tags":{"type":"map","item":{"type":"string"}}}}},"groups":{"teams":{"singular":"team"}}}""";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["RegistryId"] = Value("\"registry-a\"", BuiltInType.String),
                ["SpecVersion"] = Value("\"1.0-rc4\"", BuiltInType.String),
                ["Xid"] = Value("\"/\"", BuiltInType.String),
                ["Epoch"] = Value("1", BuiltInType.UInt32),
                ["CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["root:VendorConfig"] = Value(
                    """{"Region":"registry","Origins":["primary","backup"],"Tags":{"eu":"west"}}""",
                    BuiltInType.ExtensionObject, "VendorConfigDataType")
            }
        };
        var reader = await ModelReader(session, model);
        session.BrowsePages[File] = new([new([
            VariableRef("RegistryId"), VariableRef("SpecVersion"), VariableRef("Xid"), VariableRef("Epoch"),
            VariableRef("CreatedAt"), VariableRef("ModifiedAt"), VariableRef("VendorConfig", "root")
        ])]);

        var registry = await reader.ReadRegistryPropertiesAsync(File);

        var config = registry.GetProperty("vendorconfig");
        await Assert.That(config.GetProperty("region").GetString()).IsEqualTo("registry");
        await Assert.That(config.GetProperty("origins")[1].GetString()).IsEqualTo("backup");
        await Assert.That(config.GetProperty("tags").GetProperty("eu").GetString()).IsEqualTo("west");
    }

    [Test]
    public async Task RegistryPropertiesRejectCaseCollidingModelledProperties()
    {
        const string model = """{"attributes":{"vendorcode":"string"},"groups":{"teams":{"singular":"team"}}}""";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["RegistryId"] = Value("\"registry-a\"", BuiltInType.String),
                ["SpecVersion"] = Value("\"1.0-rc4\"", BuiltInType.String),
                ["Xid"] = Value("\"/\"", BuiltInType.String),
                ["Epoch"] = Value("1", BuiltInType.UInt32),
                ["CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["root:VendorCode"] = Value("\"first\"", BuiltInType.String)
            }
        };
        var reader = await ModelReader(session, model);
        session.BrowsePages[File] = new([new([
            VariableRef("RegistryId"), VariableRef("SpecVersion"), VariableRef("Xid"), VariableRef("Epoch"),
            VariableRef("CreatedAt"), VariableRef("ModifiedAt"),
            VariableRef("VendorCode", "root"), VariableRef("VENDORCODE", "root")
        ])]);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadRegistryPropertiesAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.Ambiguous);
    }

    [Test]
    public async Task RegistryPropertiesDoNotUnicodeFoldModelledBrowseNames()
    {
        const string model = """{"attributes":{"key":"string"},"groups":{"teams":{"singular":"team"}}}""";
        const string kelvinKey = "\u212Aey";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["RegistryId"] = Value("\"registry-a\"", BuiltInType.String),
                ["SpecVersion"] = Value("\"1.0-rc4\"", BuiltInType.String),
                ["Xid"] = Value("\"/\"", BuiltInType.String),
                ["Epoch"] = Value("1", BuiltInType.UInt32),
                ["CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                [$"root:{kelvinKey}"] = Value("\"not-key\"", BuiltInType.String)
            }
        };
        var reader = await ModelReader(session, model);
        session.BrowsePages[File] = new([new([
            VariableRef("RegistryId"), VariableRef("SpecVersion"), VariableRef("Xid"), VariableRef("Epoch"),
            VariableRef("CreatedAt"), VariableRef("ModifiedAt"), VariableRef(kelvinKey, "root")
        ])]);

        var registry = await reader.ReadRegistryPropertiesAsync(File);

        await Assert.That(registry.TryGetProperty("key", out _)).IsFalse();
    }

    [Test]
    public async Task GroupPropertiesIncludeModelledTypedExtensionAttributes()
    {
        const string model =
            """{"groups":{"teams":{"singular":"team","attributes":{"policy":{"type":"object","attributes":{"enabled":"boolean"}},"region":"string"}}}}""";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["group:GroupId"] = Value("\"team-a\"", BuiltInType.String),
                ["group:Xid"] = Value("\"/teams/team-a\"", BuiltInType.String),
                ["group:Epoch"] = Value("2", BuiltInType.UInt32),
                ["group:CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["group:ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["group:Name"] = Value("\"Team A\"", BuiltInType.String),
                ["group:Region"] = Value("\"west\"", BuiltInType.String),
                ["group:Policy"] = Value("""{"Enabled":true}""",
                    BuiltInType.ExtensionObject, "TeamPolicyDataType")
            }
        };
        var reader = await ModelReader(session, model);
        session.BrowsePages[File] = new([new([
            TypedObjectRef("team-a", GroupNode, new NodeId("GroupType", 1))
        ])]);
        session.BrowsePages[GroupNode] = new([new([
            VariableRef("GroupId", "group"), VariableRef("Xid", "group"), VariableRef("Epoch", "group"),
            VariableRef("CreatedAt", "group"), VariableRef("ModifiedAt", "group"), VariableRef("Name", "group"),
            VariableRef("Region", "group"), VariableRef("Policy", "group")
        ])]);

        var group = await reader.ReadGroupPropertiesAsync(File, "teams", "team-a",
            new NodeId("GroupType", 1));

        await Assert.That(group.GetProperty("region").GetString()).IsEqualTo("west");
        await Assert.That(group.GetProperty("policy").GetProperty("enabled").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task ResourcePropertiesIncludeModelledTypedExtensionAttributes()
    {
        const string model =
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","attributes":{"origin":"string","policy":{"type":"object","attributes":{"enabled":"boolean"}}}}}}}}""";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["logical:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["logical:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["logical:Epoch"] = Value("4", BuiltInType.UInt32),
                ["logical:CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["logical:ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["logical:Name"] = Value("\"Schema A\"", BuiltInType.String),
                ["logical:Origin"] = Value("\"catalog\"", BuiltInType.String),
                ["logical:Policy"] = Value("""{"Enabled":true}""",
                    BuiltInType.ExtensionObject, "SchemaPolicyDataType")
            }
        };
        var reader = await ModelReader(session, model);
        session.BrowsePages[GroupNode] = new([new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)
        ])]);
        session.BrowsePages[LogicalResourceNode] = new([new([
            VariableRef("ResourceId", "logical"), VariableRef("Xid", "logical"),
            VariableRef("Epoch", "logical"), VariableRef("CreatedAt", "logical"),
            VariableRef("ModifiedAt", "logical"), VariableRef("Name", "logical"),
            VariableRef("Origin", "logical"), VariableRef("Policy", "logical")
        ])]);

        var resource = await reader.ReadResourcePropertiesAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType);

        await Assert.That(resource.GetProperty("origin").GetString()).IsEqualTo("catalog");
        await Assert.That(resource.GetProperty("policy").GetProperty("enabled").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task ResourcePropertiesRejectMissingRequiredModelledExtensionAttributes()
    {
        const string model =
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","attributes":{"origin":{"type":"string","required":true}}}}}}}""";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["logical:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["logical:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["logical:Epoch"] = Value("4", BuiltInType.UInt32),
                ["logical:CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["logical:ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["logical:Name"] = Value("\"Schema A\"", BuiltInType.String)
            }
        };
        var reader = await ModelReader(session, model);
        session.BrowsePages[GroupNode] = new([new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)
        ])]);
        session.BrowsePages[LogicalResourceNode] = new([new([
            VariableRef("ResourceId", "logical"), VariableRef("Xid", "logical"),
            VariableRef("Epoch", "logical"), VariableRef("CreatedAt", "logical"),
            VariableRef("ModifiedAt", "logical"), VariableRef("Name", "logical")
        ])]);

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await reader.ReadResourcePropertiesAsync(
                GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(error.Message).Contains("does not conform to the xRegistry Model");
    }

    [Test]
    public async Task ResourcePropertiesApplyDefaultModelledExtensionAttributes()
    {
        const string model =
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","attributes":{"origin":{"type":"string","required":true,"default":"catalog"}}}}}}}""";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["logical:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["logical:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["logical:Epoch"] = Value("4", BuiltInType.UInt32),
                ["logical:CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["logical:ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime),
                ["logical:Name"] = Value("\"Schema A\"", BuiltInType.String)
            }
        };
        var reader = await ModelReader(session, model);
        session.BrowsePages[GroupNode] = new([new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)
        ])]);
        session.BrowsePages[LogicalResourceNode] = new([new([
            VariableRef("ResourceId", "logical"), VariableRef("Xid", "logical"),
            VariableRef("Epoch", "logical"), VariableRef("CreatedAt", "logical"),
            VariableRef("ModifiedAt", "logical"), VariableRef("Name", "logical")
        ])]);

        var resource = await reader.ReadResourcePropertiesAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType);

        await Assert.That(resource.GetProperty("origin").GetString()).IsEqualTo("catalog");
    }

    [Test]
    public async Task ResourceMetaPropertiesPreserveModelledExtensionObjects()
    {
        const string model =
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","metaattributes":{"policy":{"type":"object","attributes":{"enabled":"boolean","mode":{"type":"string","enum":["strict","relaxed"]}}}}}}}}}""";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["meta:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["meta:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["meta:MetaPolicy"] = Value(
                    """{"Enabled":true,"Mode":"strict"}""",
                    BuiltInType.ExtensionObject, "VendorPolicyDataType")
            }
        };
        var reader = await ModelReader(session, model);
        session.BrowsePages[GroupNode] = new([new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)
        ])]);
        session.BrowsePages[LogicalResourceNode] = new([new([
            VariableRef("ResourceId", "meta"), VariableRef("Xid", "meta"), VariableRef("MetaPolicy", "meta")
        ])]);

        var meta = await reader.ReadResourceMetaPropertiesAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType);

        var policy = meta.GetProperty("policy");
        await Assert.That(policy.GetProperty("enabled").GetBoolean()).IsTrue();
        await Assert.That(policy.GetProperty("mode").GetString()).IsEqualTo("strict");
    }

    [Test]
    public async Task ResourceMetaPropertiesRejectModelledExtensionObjectsThatViolateTheirModel()
    {
        const string model =
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","metaattributes":{"policy":{"type":"object","attributes":{"enabled":"boolean","mode":{"type":"string","enum":["strict","relaxed"]}}}}}}}}}""";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["meta:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["meta:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["meta:MetaPolicy"] = Value(
                    """{"Enabled":true,"Mode":"unsupported"}""",
                    BuiltInType.ExtensionObject, "VendorPolicyDataType")
            }
        };
        var reader = await ModelReader(session, model);
        session.BrowsePages[GroupNode] = new([new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)
        ])]);
        session.BrowsePages[LogicalResourceNode] = new([new([
            VariableRef("ResourceId", "meta"), VariableRef("Xid", "meta"), VariableRef("MetaPolicy", "meta")
        ])]);

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await reader.ReadResourceMetaPropertiesAsync(
                GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(error.Message).Contains("does not conform to the xRegistry Model");
    }

    [Test]
    public async Task ResourceMetaPropertiesRejectCaseCollidingExtensionObjectFields()
    {
        const string model =
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","metaattributes":{"policy":{"type":"object","attributes":{"enabled":"boolean"}}}}}}}}""";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["meta:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["meta:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["meta:MetaPolicy"] = Value(
                    """{"Enabled":true,"enabled":false}""",
                    BuiltInType.ExtensionObject, "VendorPolicyDataType")
            }
        };
        var reader = await ModelReader(session, model);
        session.BrowsePages[GroupNode] = new([new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)
        ])]);
        session.BrowsePages[LogicalResourceNode] = new([new([
            VariableRef("ResourceId", "meta"), VariableRef("Xid", "meta"), VariableRef("MetaPolicy", "meta")
        ])]);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadResourceMetaPropertiesAsync(
                GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(error.Message).Contains("duplicate model field 'enabled'");
    }

    [Test]
    public async Task ResourceMetaRejectsEpochWithWrongBuiltInType()
    {
        var session = new FakeSession
        {
            JsonValues =
            {
                ["meta:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["meta:MetaEpoch"] = Value("17", BuiltInType.Int32)
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)]));
        session.Pages.Enqueue(new([VariableRef("ResourceId", "meta"), VariableRef("MetaEpoch", "meta")]));

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await reader.ReadResourceMetaPropertiesAsync(
                GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task ResourceMetaAttributesReadMetaLabelsNotDelegatedVersionLabels()
    {
        var metaLabelsNode = new NodeId("meta-labels", 1);
        var session = new FakeSession { StringValues = { ["meta-label:region"] = "metadata-only" } };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)]));
        session.Pages.Enqueue(new([
            new OpcUaReference(metaLabelsNode, new QualifiedName("MetaLabels", 1),
                NodeClass.Object, AttributesType)
        ]));
        session.Pages.Enqueue(new([VariableRef("region", "meta-label")]));

        var labels = await reader.ReadResourceMetaAttributesAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType, AttributesType);

        await Assert.That(labels.GetProperty("region").GetString()).IsEqualTo("metadata-only");
        await Assert.That(NodeIdTrace(session.BrowsedNodes)).IsEqualTo(
            "Model|Model|group-team-a|logical-schema-a|meta-labels");
    }

    [Test]
    public async Task FederationSourceCompilesModelAndReturnsCapturedModelAndCapabilities()
    {
        var session = SourceSession();
        var source = await OpenSource(session);

        var model = await source.ReadAsync(new(FederationOperation.Model, "/"));
        var capabilities = await source.ReadAsync(new(FederationOperation.Capabilities, "/"));

        await Assert.That(source.SpecVersion).IsEqualTo("1.0-rc4");
        await Assert.That(source.Model.Groups.ContainsKey("teams")).IsTrue();
        await Assert.That(model.Metadata.GetProperty("model").GetProperty("groups")
            .GetProperty("teams").GetProperty("resources").GetProperty("schemas")
            .GetProperty("singular").GetString()).IsEqualTo("schema");
        await Assert.That(capabilities.Metadata.GetProperty("flags")[0].GetString()).IsEqualTo("doc");
        await Assert.That(model.Context).IsEqualTo(source.Context);
        await Assert.That(capabilities.Context).IsEqualTo(source.Context);
    }

    [Test]
    public async Task FederationResourceEntityIncludesMetaVersionsAndDocumentLocalPointers()
    {
        var session = SourceSession();
        ConfigureResourceAddressSpace(session);
        var source = await OpenSource(session);

        var result = await source.ReadAsync(new(FederationOperation.Entity,
            "/teams/team-a/schemas/schema-a"));
        var entity = result.Metadata.GetProperty("entity");
        var meta = entity.GetProperty("meta");
        var version = entity.GetProperty("versions").GetProperty("v1");

        await Assert.That(result.Metadata.GetProperty("kind").GetString()).IsEqualTo("resource");
        await Assert.That(entity.GetProperty("self").GetString()).IsEqualTo("#/entity");
        await Assert.That(entity.TryGetProperty("versionid", out _)).IsFalse();
        await Assert.That(entity.GetProperty("resourceid").GetString()).IsEqualTo("schema-a");
        await Assert.That(meta.GetProperty("xid").GetString()).IsEqualTo(
            "/teams/team-a/schemas/schema-a/meta");
        await Assert.That(meta.GetProperty("defaultversionid").GetString()).IsEqualTo("v1");
        await Assert.That(meta.GetProperty("self").GetString()).IsEqualTo("#/entity/meta");
        await Assert.That(meta.GetProperty("defaultversionurl").GetString())
            .IsEqualTo("#/entity/versions/v1");
        await Assert.That(version.GetProperty("xid").GetString())
            .IsEqualTo("/teams/team-a/schemas/schema-a/versions/v1");
        await Assert.That(version.GetProperty("isdefault").GetBoolean()).IsTrue();
        await Assert.That(version.GetProperty("self").GetString()).IsEqualTo("#/entity/versions/v1");
    }

    [Test]
    public async Task FederationCollectionIsCompleteAndUsesCollectionLocalPointers()
    {
        var session = SourceSession();
        ConfigureResourceAddressSpace(session);
        ConfigureResourceCollectionWalk(session);
        var source = await OpenSource(session);

        var result = await source.ReadAsync(new(FederationOperation.Collection,
            "/teams/team-a/schemas"));
        var entity = result.Metadata.GetProperty("entities").GetProperty("schema-a");

        await Assert.That(result.Metadata.GetProperty("kind").GetString()).IsEqualTo("collection");
        await Assert.That(result.Metadata.GetProperty("complete").GetBoolean()).IsTrue();
        await Assert.That(entity.GetProperty("self").GetString()).IsEqualTo("#/entities/schema-a");
        await Assert.That(entity.GetProperty("labels").GetProperty("region").GetString())
            .IsEqualTo("resource-region");
        await Assert.That(entity.GetProperty("meta").GetProperty("self").GetString())
            .IsEqualTo("#/entities/schema-a/meta");
        await Assert.That(entity.GetProperty("versions").GetProperty("v1").GetProperty("self").GetString())
            .IsEqualTo("#/entities/schema-a/versions/v1");
    }

    [Test]
    public async Task FederationCollectionSelectorMatchesLiteralLabelsAndRebasesPointers()
    {
        var session = SourceSession();
        ConfigureResourceAddressSpace(session);
        ConfigureResourceCollectionWalk(session);
        var source = await OpenSource(session);

        var result = await source.ReadAsync(new(FederationOperation.Collection, "/teams/team-a/schemas",
            new FederationLabelSelector("region", "RESOURCE-REGION")));
        var entity = result.Metadata.GetProperty("entity");

        await Assert.That(result.SelectedXid).IsEqualTo("/teams/team-a/schemas/schema-a");
        await Assert.That(entity.GetProperty("self").GetString()).IsEqualTo("#/entity");
        await Assert.That(entity.GetProperty("metaurl").GetString()).IsEqualTo("#/entity/meta");
        await Assert.That(entity.GetProperty("versions").GetProperty("v1").GetProperty("self").GetString())
            .IsEqualTo("#/entity/versions/v1");
    }

    [Test]
    public async Task FederationMetaEntityAndExactVersionUseEntityLocalPointers()
    {
        var session = SourceSession();
        ConfigureResourceAddressSpace(session);
        var source = await OpenSource(session);

        var meta = await source.ReadAsync(new(FederationOperation.Entity,
            "/teams/team-a/schemas/schema-a/meta"));
        var versionSession = SourceSession();
        ConfigureResourceAddressSpace(versionSession);
        var versionSource = await OpenSource(versionSession);
        var version = await versionSource.ReadAsync(new(FederationOperation.Entity,
            "/teams/team-a/schemas/schema-a/versions/v1"));

        await Assert.That(meta.Metadata.GetProperty("kind").GetString()).IsEqualTo("meta");
        await Assert.That(meta.Metadata.GetProperty("entity").GetProperty("self").GetString())
            .IsEqualTo("#/entity");
        await Assert.That(meta.Metadata.GetProperty("entity").GetProperty("defaultversionid").GetString())
            .IsEqualTo("v1");
        var versionEntity = version.Metadata.GetProperty("entity");
        await Assert.That(version.Metadata.GetProperty("kind").GetString()).IsEqualTo("version");
        await Assert.That(versionEntity.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(versionEntity.GetProperty("isdefault").GetBoolean()).IsTrue();
        await Assert.That(versionEntity.GetProperty("self").GetString()).IsEqualTo("#/entity");
    }

    [Test]
    public async Task FederationSourceRejectsApiViewWithoutRetrievableCoreApiUrls()
    {
        var source = await OpenSource(SourceSession());

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity, "/",
                representation: FederationRepresentation.ApiView)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
    }

    [Test]
    public async Task FederationSourceRejectsMissingObjectTypeMappingInsteadOfGuessing()
    {
        var source = await OpenSource(SourceSession(), new(
            new Dictionary<string, NodeId>(StringComparer.Ordinal),
            new Dictionary<(string GroupCollection, string ResourceCollection), OpcUaResourceTypeBinding>(),
            AttributesType));

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity, "/teams/team-a")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
    }

    [Test]
    public async Task FederationDocumentReadUsesDefaultVersionsExternalReference()
    {
        var (session, source) = await OpenExternalReferenceSource();

        var result = await source.ReadAsync(new(FederationOperation.Document,
            "/teams/team-a/schemas/schema-a"));

        await Assert.That(result.SelectedXid)
            .IsEqualTo("/teams/team-a/schemas/schema-a/versions/v1");
        await Assert.That(result.Document).IsNull();
        await Assert.That(result.ExternalDocument.GetProperty("binding").GetString()).IsEqualTo("opcua");
        var externalNodeId = result.ExternalDocument.GetProperty("expandednodeid");
        await Assert.That(externalNodeId.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(externalNodeId.GetProperty("serverapplicationuri").GetString())
            .IsEqualTo("urn:remote:application");
        await Assert.That(externalNodeId.GetProperty("namespaceuri").GetString())
            .IsEqualTo("urn:remote:namespace");
        await Assert.That(externalNodeId.GetProperty("identifiertype").GetString()).IsEqualTo("Numeric");
        await Assert.That(externalNodeId.GetProperty("identifier").GetUInt32()).IsEqualTo(42u);
        await Assert.That(session.Opened).IsEqualTo(2);
    }

    [Test]
    public async Task FederationDocumentReadUsesExactVersionsExternalReference()
    {
        var (_, source) = await OpenExternalReferenceSource();

        var result = await source.ReadAsync(new(FederationOperation.Document,
            "/teams/team-a/schemas/schema-a/versions/v1"));

        await Assert.That(result.SelectedXid)
            .IsEqualTo("/teams/team-a/schemas/schema-a/versions/v1");
        await Assert.That(result.ExternalDocument.GetProperty("expandednodeid")
            .GetProperty("serverapplicationuri").GetString()).IsEqualTo("urn:remote:application");
        await Assert.That(result.ExternalDocument.GetProperty("expandednodeid")
            .GetProperty("identifier").GetUInt32()).IsEqualTo(42u);
    }

    [Test]
    public async Task FederationDocumentReadReturnsExactLocalVersionBytes()
    {
        const string model =
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","hasdocument":true}}}}}""";
        byte[] expected = [0x00, 0x7f, 0x80, 0xff];
        var session = SourceSession(model);
        ConfigureResourceAddressSpace(session);
        session.BrowsePages[GroupNode] = new([GroupPropertiesPage(), ResourceReferencePage()]);
        session.Chunks.AddRange([expected, Array.Empty<byte>()]);
        var source = await OpenSource(session);

        var result = await source.ReadAsync(new(FederationOperation.Document,
            "/teams/team-a/schemas/schema-a"));

        await Assert.That(result.SelectedXid)
            .IsEqualTo("/teams/team-a/schemas/schema-a/versions/v1");
        await Assert.That(result.Document!.Length).IsEqualTo(expected.Length);
        await Assert.That(result.Document.ContentType).IsEqualTo("application/json");
        using var stream = result.Document.OpenRead();
        var actual = new byte[expected.Length];
        await stream.ReadExactlyAsync(actual);
        await Assert.That(actual).IsEquivalentTo(expected);
    }

    [Test]
    public async Task ExternalReferenceIsMappedToUriIdentitySeparateFromResourceUrl()
    {
        var resourceType = new NodeId("ResourceType", 1);
        var reference = new ExpandedNodeId("resource", 0, "urn:xregistry", 1u);
        var session = new FakeSession
        {
            JsonValues =
            {
                ["ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["Name"] = Value("\"Schema A\"", BuiltInType.String),
                ["CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime)
            },
            ExpandedNodeIds = { ["ExternalReference"] = reference },
            ExternalNodeIdMappings =
            {
                [reference] = new("urn:remote:application", "urn:xregistry", IdType.String, "resource")
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([TypedObjectRef("schema-a", LogicalResourceNode, resourceType)]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId"), VariableRef("Xid"), VariableRef("Name"),
            VariableRef("CreatedAt"), VariableRef("ModifiedAt")
        ]));
        session.Pages.Enqueue(new([VariableRef("ExternalReference")]));

        var actual = await reader.ReadExternalNodeIdAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", resourceType);

        var identity = actual!;
        await Assert.That(identity.ServerApplicationUri).IsEqualTo("urn:remote:application");
        await Assert.That(identity.NamespaceUri).IsEqualTo("urn:xregistry");
        await Assert.That(identity.IdentifierType).IsEqualTo(IdType.String);
        await Assert.That(identity.Identifier).IsEqualTo("resource");
    }

    [Test]
    public async Task ReaderExplicitVersionRequiresExactTypedMatchingChild()
    {
        var exactSession = new FakeSession
        {
            JsonValues =
            {
                ["v2:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["v2:VersionId"] = Value("\"v2\"", BuiltInType.String),
                ["v2:Xid"] = Value("\"/teams/team-a/schemas/schema-a/versions/v2\"", BuiltInType.String),
                ["v2:Epoch"] = Value("52", BuiltInType.UInt32),
                ["v2:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["v2:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["v2:Name"] = Value("\"Schema A v2\"", BuiltInType.String)
            }
        };
        var exactReader = await ModelReader(exactSession);
        exactSession.Pages.Enqueue(new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType),
            TypedObjectRef("v2", new NodeId("group-decoy-v2", 1), ResourceType)
        ]));
        exactSession.Pages.Enqueue(new([TypedObjectRef("Versions", VersionsNode, ResourceVersionsType)]));
        exactSession.Pages.Enqueue(new([
            TypedObjectRef("wrong-type", new NodeId("wrong-version", 1), new NodeId("OtherType", 1)),
            TypedObjectRef("v2", VersionTwoNode, ResourceType)
        ]));
        exactSession.Pages.Enqueue(new([
            VariableRef("ResourceId", "v2"), VariableRef("VersionId", "v2"),
            VariableRef("Xid", "v2"), VariableRef("Epoch", "v2"),
            VariableRef("Name", "v2"), VariableRef("CreatedAt", "v2"),
            VariableRef("ModifiedAt", "v2")
        ]));

        var resource = await exactReader.ReadResourcePropertiesAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType, "v2", ResourceVersionsType);

        await Assert.That(resource.GetProperty("versionid").GetString()).IsEqualTo("v2");
        await Assert.That(resource.GetProperty("epoch").GetUInt32()).IsEqualTo(52u);
        await Assert.That(NodeIdTrace(exactSession.BrowsedNodes)).IsEqualTo(
            "Model|Model|group-team-a|logical-schema-a|schema-a-versions|schema-a-v2");
    }

    [Test]
    public async Task ExplicitVersionPropertiesIncludeModelledTypedExtensionAttributes()
    {
        const string model =
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","attributes":{"policy":{"type":"object","attributes":{"enabled":"boolean","mode":{"type":"string","enum":["strict","relaxed"]}}}}}}}}}""";
        var session = new FakeSession
        {
            JsonValues =
            {
                ["v2:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["v2:VersionId"] = Value("\"v2\"", BuiltInType.String),
                ["v2:Xid"] = Value("\"/teams/team-a/schemas/schema-a/versions/v2\"", BuiltInType.String),
                ["v2:Epoch"] = Value("52", BuiltInType.UInt32),
                ["v2:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["v2:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["v2:Name"] = Value("\"Schema A v2\"", BuiltInType.String),
                ["v2:Policy"] = Value(
                    """{"Enabled":true,"Mode":"strict"}""",
                    BuiltInType.ExtensionObject, "VendorPolicyDataType")
            }
        };
        var reader = await ModelReader(session, model);
        session.Pages.Enqueue(new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType),
            TypedObjectRef("v2", new NodeId("group-decoy-v2", 1), ResourceType)
        ]));
        session.Pages.Enqueue(new([TypedObjectRef("Versions", VersionsNode, ResourceVersionsType)]));
        session.Pages.Enqueue(new([TypedObjectRef("v2", VersionTwoNode, ResourceType)]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId", "v2"), VariableRef("VersionId", "v2"),
            VariableRef("Xid", "v2"), VariableRef("Epoch", "v2"),
            VariableRef("CreatedAt", "v2"), VariableRef("ModifiedAt", "v2"),
            VariableRef("Name", "v2"),
            VariableRef("Policy", "v2")
        ]));
        session.Pages.Enqueue(new([VariableRef("Policy", "v2")]));

        var resource = await reader.ReadResourcePropertiesAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType, "v2",
            ResourceVersionsType);

        await Assert.That(resource.GetProperty("policy").GetProperty("enabled").GetBoolean()).IsTrue();
        await Assert.That(resource.GetProperty("policy").GetProperty("mode").GetString()).IsEqualTo("strict");
    }

    [Test]
    [Arguments("mismatched-id")]
    [Arguments("wrong-type")]
    [Arguments("missing")]
    public async Task ReaderExplicitVersionRejectsInvalidChildWithoutUsingGroupSibling(string scenario)
    {
        var hasMatchingTypeChild = scenario == "mismatched-id";
        var expectedCode = hasMatchingTypeChild
            ? FederationErrorCode.InvalidPackage : FederationErrorCode.NotFound;
        var page = scenario switch
        {
            "mismatched-id" => new OpcUaBrowsePage([TypedObjectRef("v2", VersionTwoNode, ResourceType)]),
            "wrong-type" => new OpcUaBrowsePage([
                TypedObjectRef("v2", new NodeId("wrong-version", 1), new NodeId("OtherType", 1))]),
            _ => new OpcUaBrowsePage([])
        };
        {
            var session = new FakeSession
            {
                JsonValues =
                {
                    ["v2:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                    ["v2:VersionId"] = Value("\"v1\"", BuiltInType.String),
                    ["v2:Xid"] = Value("\"/teams/team-a/schemas/schema-a/versions/v2\"", BuiltInType.String),
                    ["v2:Epoch"] = Value("51", BuiltInType.UInt32),
                    ["v2:Name"] = Value("\"Schema A mismatched version\"", BuiltInType.String),
                    ["v2:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                    ["v2:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime)
                }
            };
            var reader = await ModelReader(session);
            session.Pages.Enqueue(new([
                TypedObjectRef("schema-a", LogicalResourceNode, ResourceType),
                TypedObjectRef("v2", new NodeId("group-decoy-v2", 1), ResourceType)
            ]));
            session.Pages.Enqueue(new([TypedObjectRef("Versions", VersionsNode, ResourceVersionsType)]));
            session.Pages.Enqueue(page);
            if (hasMatchingTypeChild)
            {
                session.Pages.Enqueue(new([
                    VariableRef("ResourceId", "v2"), VariableRef("VersionId", "v2"),
                    VariableRef("Xid", "v2"), VariableRef("Epoch", "v2"),
                    VariableRef("Name", "v2"), VariableRef("CreatedAt", "v2"),
                    VariableRef("ModifiedAt", "v2")
                ]));
            }

            var error = await Assert.ThrowsAsync<FederationException>(async () =>
                await reader.ReadResourcePropertiesAsync(
                    GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType, "v2",
                    ResourceVersionsType));

            await Assert.That(error!.Code).IsEqualTo(expectedCode);
            await Assert.That(NodeIdTrace(session.BrowsedNodes)).IsEqualTo(
                hasMatchingTypeChild
                    ? "Model|Model|group-team-a|logical-schema-a|schema-a-versions|schema-a-v2"
                    : "Model|Model|group-team-a|logical-schema-a|schema-a-versions");
            await Assert.That(session.Opened).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ReaderResourceCollectionEnumeratesLogicalResourcesOnly()
    {
        var session = new FakeSession
        {
            JsonValues =
            {
                ["logical-a:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["logical-a:VersionId"] = Value("\"v1\"", BuiltInType.String),
                ["logical-a:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["logical-a:Epoch"] = Value("4", BuiltInType.UInt32),
                ["logical-a:Name"] = Value("\"Schema A default\"", BuiltInType.String),
                ["logical-a:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["logical-a:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["logical-b:ResourceId"] = Value("\"schema-b\"", BuiltInType.String),
                ["logical-b:VersionId"] = Value("\"v3\"", BuiltInType.String),
                ["logical-b:Xid"] = Value("\"/teams/team-a/schemas/schema-b\"", BuiltInType.String),
                ["logical-b:Epoch"] = Value("8", BuiltInType.UInt32),
                ["logical-b:Name"] = Value("\"Schema B default\"", BuiltInType.String),
                ["logical-b:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["logical-b:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime)
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType),
            TypedObjectRef("schema-b", new NodeId("logical-schema-b", 1), ResourceType)
        ]));
        session.Pages.Enqueue(new([VariableRef("ResourceId", "logical-a")]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId", "logical-a"), VariableRef("VersionId", "logical-a"),
            VariableRef("Xid", "logical-a"), VariableRef("Epoch", "logical-a"),
            VariableRef("Name", "logical-a"), VariableRef("CreatedAt", "logical-a"),
            VariableRef("ModifiedAt", "logical-a")
        ]));
        session.Pages.Enqueue(new([VariableRef("ResourceId", "logical-b")]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId", "logical-b"), VariableRef("VersionId", "logical-b"),
            VariableRef("Xid", "logical-b"), VariableRef("Epoch", "logical-b"),
            VariableRef("Name", "logical-b"), VariableRef("CreatedAt", "logical-b"),
            VariableRef("ModifiedAt", "logical-b")
        ]));

        var resources = await reader.ReadResourceCollectionAsync(
            GroupNode, "/teams/team-a", "schemas", ResourceType);
        var resourcesById = resources.EnumerateArray().ToDictionary(
            item => item.GetProperty("resourceid").GetString()!, StringComparer.Ordinal);

        await Assert.That(resources.GetArrayLength()).IsEqualTo(2);
        await Assert.That(resourcesById["schema-a"].GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(resourcesById["schema-a"].GetProperty("epoch").GetUInt32()).IsEqualTo(4u);
        await Assert.That(resourcesById["schema-b"].GetProperty("versionid").GetString()).IsEqualTo("v3");
        await Assert.That(resourcesById["schema-b"].GetProperty("epoch").GetUInt32()).IsEqualTo(8u);
        await Assert.That(session.BrowsedNodes.Count(node => node == LogicalResourceNode)).IsEqualTo(2);
        await Assert.That(session.BrowsedNodes.Contains(VersionsNode)).IsFalse();
        await Assert.That(session.BrowsedNodes.Contains(DefaultVersionNode)).IsFalse();
        await Assert.That(session.BrowsedNodes.Contains(VersionTwoNode)).IsFalse();
    }

    [Test]
    public async Task ResourceCollectionMustBeDeclaredByItsGroupModel()
    {
        var reader = await ModelReader(new FakeSession());

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadResourceCollectionAsync(
                File, "/teams/team-a", "unknown", new NodeId("ResourceType", 1)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
    }

    [Test]
    public async Task ReaderVersionCollectionEnumeratesOnlySelectedResourceVersions()
    {
        var session = new FakeSession
        {
            JsonValues =
            {
                ["default:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["default:VersionId"] = Value("\"v1\"", BuiltInType.String),
                ["default:Xid"] = Value("\"/teams/team-a/schemas/schema-a/versions/v1\"", BuiltInType.String),
                ["default:Epoch"] = Value("4", BuiltInType.UInt32),
                ["default:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["default:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["default:Name"] = Value("\"Schema A\"", BuiltInType.String),
                ["v2:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["v2:VersionId"] = Value("\"v2\"", BuiltInType.String),
                ["v2:Xid"] = Value("\"/teams/team-a/schemas/schema-a/versions/v2\"", BuiltInType.String),
                ["v2:Epoch"] = Value("5", BuiltInType.UInt32),
                ["v2:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["v2:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["v2:Name"] = Value("\"Schema A\"", BuiltInType.String)
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType),
            TypedObjectRef("schema-b", new NodeId("logical-schema-b", 1), ResourceType),
            TypedObjectRef("v2", new NodeId("group-decoy-v2", 1), ResourceType)
        ]));
        session.Pages.Enqueue(new([TypedObjectRef("Versions", VersionsNode, ResourceVersionsType)]));
        session.Pages.Enqueue(new([
            TypedObjectRef("v1", DefaultVersionNode, ResourceType),
            TypedObjectRef("v2", VersionTwoNode, ResourceType),
        ]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId", "default"), VariableRef("VersionId", "default"),
            VariableRef("Xid", "default"), VariableRef("Epoch", "default"),
            VariableRef("Name", "default"), VariableRef("CreatedAt", "default"),
            VariableRef("ModifiedAt", "default")
        ]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId", "v2"), VariableRef("VersionId", "v2"),
            VariableRef("Xid", "v2"), VariableRef("Epoch", "v2"),
            VariableRef("Name", "v2"), VariableRef("CreatedAt", "v2"),
            VariableRef("ModifiedAt", "v2")
        ]));

        var versions = await reader.ReadVersionCollectionAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType, ResourceVersionsType);
        var versionsById = versions.EnumerateArray().ToDictionary(
            item => item.GetProperty("versionid").GetString()!, StringComparer.Ordinal);

        await Assert.That(versions.GetArrayLength()).IsEqualTo(2);
        await Assert.That(versionsById["v1"].GetProperty("resourceid").GetString()).IsEqualTo("schema-a");
        await Assert.That(versionsById["v1"].GetProperty("xid").GetString())
            .IsEqualTo("/teams/team-a/schemas/schema-a/versions/v1");
        await Assert.That(versionsById["v2"].GetProperty("xid").GetString())
            .IsEqualTo("/teams/team-a/schemas/schema-a/versions/v2");
        await Assert.That(session.BrowsedNodes.Contains(VersionsNode)).IsTrue();
        await Assert.That(session.BrowsedNodes.Contains(DefaultVersionNode)).IsTrue();
        await Assert.That(session.BrowsedNodes.Contains(VersionTwoNode)).IsTrue();
        await Assert.That(session.BrowsedNodes.Contains(new NodeId("logical-schema-b", 1))).IsFalse();
        await Assert.That(session.BrowsedNodes.Contains(new NodeId("schema-b-v9", 1))).IsFalse();
    }

    [Test]
    public async Task AttributeContainerReadsTypedPropertiesByTheirBrowseNames()
    {
        var attributesType = new NodeId("AttributesType", 1);
        var session = new FakeSession
        {
            StringValues =
            {
                ["team"] = "alpha",
                ["x-vendor-flag"] = "enabled"
            }
        };
        session.Pages.Enqueue(new([SpecVersionRef()]));
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);
        session.Pages.Enqueue(new([new OpcUaReference(new NodeId("Labels", 1),
            new QualifiedName("Labels", 1), NodeClass.Object, attributesType)]));
        session.Pages.Enqueue(new([VariableRef("team"), VariableRef("x-vendor-flag")]));

        var attributes = await reader.ReadAttributesAsync(File, attributesType);

        await Assert.That(attributes.GetProperty("team").GetString()).IsEqualTo("alpha");
        await Assert.That(attributes.GetProperty("x-vendor-flag").GetString()).IsEqualTo("enabled");
    }

    [Test]
    public async Task ReaderDefaultAndExplicitDocumentOperationsTargetSelectedVersion()
    {
        var session = new FakeSession
        {
            Chunks =
            {
                new byte[] { 1, 2 }, Array.Empty<byte>(),
                new byte[] { 8, 9 }, Array.Empty<byte>()
            },
            JsonValues =
            {
                ["logical:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["logical:VersionId"] = Value("\"v1\"", BuiltInType.String),
                ["logical:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["logical:Epoch"] = Value("4", BuiltInType.UInt32),
                ["logical:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["logical:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["logical:Name"] = Value("\"Schema A delegated default\"", BuiltInType.String),
                ["default:VersionId"] = Value("\"wrong-default\"", BuiltInType.String),
                ["default:Epoch"] = Value("99", BuiltInType.UInt32),
                ["v2:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["v2:VersionId"] = Value("\"v2\"", BuiltInType.String),
                ["v2:Xid"] = Value("\"/teams/team-a/schemas/schema-a/versions/v2\"", BuiltInType.String),
                ["v2:Epoch"] = Value("9", BuiltInType.UInt32),
                ["v2:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                ["v2:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                ["v2:Name"] = Value("\"Schema A v2\"", BuiltInType.String)
            }
        };
        var reader = await ModelReader(session);
        QueueDocumentSelection(session, explicitVersion: false);

        var defaultBytes = await reader.ReadResourceDocumentAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType);
        await Assert.That(defaultBytes).IsEquivalentTo(new byte[] { 1, 2 });
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|logical-schema-a");
        await Assert.That(NodeIdTrace(session.ClosedFiles)).IsEqualTo("Model|logical-schema-a");
        QueueDocumentSelection(session, explicitVersion: true);
        var explicitBytes = await reader.ReadResourceDocumentAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType, "v2", ResourceVersionsType);

        await Assert.That(defaultBytes).IsEquivalentTo(new byte[] { 1, 2 });
        await Assert.That(explicitBytes).IsEquivalentTo(new byte[] { 8, 9 });
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|logical-schema-a|schema-a-v2");
        await Assert.That(NodeIdTrace(session.ClosedFiles)).IsEqualTo("Model|logical-schema-a|schema-a-v2");
        await Assert.That(session.BrowsedNodes.Contains(DefaultVersionNode)).IsFalse();
        await Assert.That(session.BrowsedNodes.Contains(VersionsNode)).IsTrue();
        await Assert.That(session.BrowsedNodes.Contains(VersionTwoNode)).IsTrue();
    }

    [Test]
    public async Task ResourceDocumentWriteCancellationStillClosesFile()
    {
        var (session, writer) = await WriterForResource(session: new FakeSession
        {
            WriteFailure = new OperationCanceledException()
        });

        _ = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await writer.ReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, new byte[] { 1 }));

        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    [Arguments(null)]
    [Arguments("v2")]
    public async Task ReaderDocumentOperationsRequireHasDocumentSupport(string? versionId)
    {
        var session = new FakeSession();
        var reader = await ModelReader(session, """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","hasdocument":false}}}}}""");

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await reader.ReadResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, versionId));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(session.Opened).IsEqualTo(1);
        await Assert.That(session.Closed).IsEqualTo(1);
        await Assert.That(session.LastOpenedFile).IsEqualTo(File);
        await Assert.That(NodeIdTrace(session.BrowsedNodes)).IsEqualTo("Model|Model");

        var (writerSession, writer) = await WriterForResource(
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","hasdocument":false}}}}}""");
        var writeError = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.ReplaceResourceDocumentAsync(
                GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType, new byte[] { 1 }, versionId));

        await Assert.That(writeError!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(writerSession.Opened).IsEqualTo(1);
        await Assert.That(writerSession.LastOpenedFile).IsEqualTo(File);
        await Assert.That(NodeIdTrace(writerSession.BrowsedNodes)).IsEqualTo("Model|Model");
    }

    [Test]
    public async Task ResourceDocumentWriteErasesThenWritesBoundedChunksAndCloses()
    {
        var (session, writer) = await WriterForResource(chunkSize: 2);

        await writer.ReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
            ResourceType, new byte[] { 1, 2, 3 });

        await Assert.That(session.OpenMode).IsEqualTo((byte)6);
        await Assert.That(session.Position).IsEqualTo(0UL);
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(2);
        await Assert.That(session.WrittenChunks[0]).IsEquivalentTo(new byte[] { 1, 2 });
        await Assert.That(session.WrittenChunks[1]).IsEquivalentTo(new byte[] { 3 });
        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    public async Task ResourceDocumentWriteClosesEmptyReplacementWithoutWritingChunks()
    {
        var (session, writer) = await WriterForResource();

        await writer.ReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
            ResourceType, ReadOnlyMemory<byte>.Empty);

        await Assert.That(session.OpenMode).IsEqualTo((byte)6);
        await Assert.That(session.Position).IsEqualTo(0UL);
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(0);
        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    public async Task ResourceDocumentWriteRejectsResourceWithoutDocumentBeforeBrowsing()
    {
        var (session, writer) = await WriterForResource(
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","hasdocument":false}}}}}""");

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.ReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, new byte[] { 1 }));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(session.Pages.Count).IsEqualTo(2);
        await Assert.That(NodeIdTrace(session.BrowsedNodes)).IsEqualTo("Model|Model");
        await Assert.That(session.Opened).IsEqualTo(1);
        await Assert.That(session.Closed).IsEqualTo(1);
    }

    [Test]
    public async Task ResourceDocumentWriteRejectsOversizeBeforeResolvingOrOpeningFile()
    {
        var (session, writer) = await WriterForResource(maxBytes: 100);

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.ReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, new byte[101]));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        await Assert.That(session.Pages.Count).IsEqualTo(2);
        await Assert.That(NodeIdTrace(session.BrowsedNodes)).IsEqualTo("Model|Model");
        await Assert.That(session.Opened).IsEqualTo(1);
        await Assert.That(session.Closed).IsEqualTo(1);
    }

    [Test]
    public async Task ResourceDocumentWriteFailureIsRetainedWhenCloseAlsoFails()
    {
        var writeFailure = new InvalidOperationException("write");
        var (session, writer) = await WriterForResource(session: new FakeSession
        {
            WriteFailure = writeFailure,
            CloseFailure = new InvalidOperationException("close")
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await writer.ReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, new byte[] { 1 }));

        await Assert.That(error!.Message).IsEqualTo("write");
        await Assert.That(error.Data.Contains("OpcUa.FileCloseFailure")).IsTrue();
        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    public async Task ResourceDocumentWritePositionFailureStillClosesFile()
    {
        var (session, writer) = await WriterForResource(session: new FakeSession
        {
            PositionFailure = new InvalidOperationException("position")
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await writer.ReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, new byte[] { 1 }));

        await Assert.That(error!.Message).IsEqualTo("position");
        await Assert.That(session.WrittenChunks.Count).IsEqualTo(0);
        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    public async Task ResourceDocumentWriteCloseFailureAfterSuccessIsReported()
    {
        var (session, writer) = await WriterForResource(session: new FakeSession
        {
            CloseFailure = new InvalidOperationException("close")
        });

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.ReplaceResourceDocumentAsync(GroupNode, "/teams/team-a", "schemas", "schema-a",
                ResourceType, new byte[] { 1 }));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.Unavailable);
        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    public async Task WriterCompletedCreatedDocumentPreservesHandleAndCloses()
    {
        var (session, writer) = await WriterForResource();

        await writer.CompleteCreatedResourceDocumentAsync(new NodeId("schema-a", 1), 42,
            new byte[] { 4, 5 });

        await Assert.That(session.Opened).IsEqualTo(1);
        await Assert.That(session.Position).IsEqualTo(0UL);
        await Assert.That(session.WrittenChunks.Single()).IsEquivalentTo(new byte[] { 4, 5 });
        await Assert.That(session.LastPositionFile).IsEqualTo(new NodeId("schema-a", 1));
        await Assert.That(session.LastPositionHandle).IsEqualTo(42u);
        await Assert.That(session.LastWrittenFile).IsEqualTo(new NodeId("schema-a", 1));
        await Assert.That(session.LastWrittenHandle).IsEqualTo(42u);
        await Assert.That(session.ClosedFiles.Last()).IsEqualTo(new NodeId("schema-a", 1));
        await Assert.That(session.LastClosedHandle).IsEqualTo(42u);
        await Assert.That(session.Closed).IsEqualTo(2);
    }

    [Test]
    public async Task PropertyWriteUsesTheDeclaredBuiltInType()
    {
        var (session, writer) = await WriterForResource();

        await writer.WritePropertyAsync(new NodeId("Epoch", 1), Value("17", BuiltInType.UInt32));

        await Assert.That(session.WrittenValues.Count).IsEqualTo(1);
        await Assert.That(session.WrittenValues[0].Variable).IsEqualTo(new NodeId("Epoch", 1));
        await Assert.That(session.WrittenValues[0].Value.TryGetValue(out uint value) && value == 17).IsTrue();
    }

    [Test]
    public async Task PropertyWriteMapsEverySupportedScalarBuiltInType()
    {
        var (session, writer) = await WriterForResource();
        var timestamp = new DateTime(2026, 1, 3, 3, 4, 5, DateTimeKind.Utc);
        var cases = new (BuiltInType Type, string Json, object Expected)[]
        {
            (BuiltInType.String, "\"text\"", "text"),
            (BuiltInType.Boolean, "true", true),
            (BuiltInType.SByte, "-3", (sbyte)-3),
            (BuiltInType.Byte, "3", (byte)3),
            (BuiltInType.Int16, "-4", (short)-4),
            (BuiltInType.UInt16, "4", (ushort)4),
            (BuiltInType.Int32, "-5", -5),
            (BuiltInType.UInt32, "5", 5u),
            (BuiltInType.Int64, "-6", -6L),
            (BuiltInType.UInt64, "6", 6UL),
            (BuiltInType.Float, "1.25", 1.25f),
            (BuiltInType.Double, "1.25", 1.25d),
            (BuiltInType.DateTime, "\"2026-01-03T03:04:05Z\"", timestamp)
        };

        foreach (var (type, json, expected) in cases)
        {
            await writer.WritePropertyAsync(new NodeId("Value", 1), Value(json, type));
            await Assert.That(session.WrittenValues[^1].Value.AsBoxedObject()).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task PropertyWriteRejectsScalarThatDoesNotMatchItsBuiltInType()
    {
        var (session, writer) = await WriterForResource();

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.WritePropertyAsync(new NodeId("Epoch", 1), Value("\"17\"", BuiltInType.UInt32)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.WrittenValues.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PropertyWriteRejectsScalarOutsideItsBuiltInRange()
    {
        var (session, writer) = await WriterForResource();

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.WritePropertyAsync(new NodeId("ByteValue", 1), Value("256", BuiltInType.Byte)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.WrittenValues.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PropertyWriteRejectsUnsupportedBuiltInType()
    {
        var (session, writer) = await WriterForResource();

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.WritePropertyAsync(new NodeId("ByteString", 1), Value("\"data\"", BuiltInType.ByteString)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(session.WrittenValues.Count).IsEqualTo(0);
    }

    [Test]
    public async Task WriterCreateAndGetOrCreatePreserveOutputsAndValidateTypes()
    {
        var (session, writer) = await WriterForResource();
        session.MethodOutputs["CreateGroup"] = [new Variant(new NodeId("team-a", 1))];
        session.MethodOutputs["GetOrCreateGroup"] =
            [new Variant(new NodeId("team-b", 1)), new Variant(true)];
        session.MethodOutputs["CreateResource"] =
            [new Variant(new NodeId("schema-a", 1)), new Variant("v1"), new Variant(42u)];
        session.MethodOutputs["GetOrCreateResource"] =
            [new Variant(new NodeId("schema-b", 1)), new Variant("v2"), new Variant(43u), new Variant(false)];

        var createdGroup = await writer.CreateGroupAsync(File, "team-a");
        var resolvedGroup = await writer.GetOrCreateGroupAsync(File, "team-b");
        var createdResource = await writer.CreateResourceAsync(new NodeId("team-a", 1), "schema-a", "v1", true);
        var resolvedResource = await writer.GetOrCreateResourceAsync(
            new NodeId("team-a", 1), "schema-b", "v2", true);

        await Assert.That(createdGroup).IsEqualTo(new NodeId("team-a", 1));
        await Assert.That(resolvedGroup.GroupNode).IsEqualTo(new NodeId("team-b", 1));
        await Assert.That(resolvedGroup.Created).IsTrue();
        await Assert.That(createdResource.ResourceNode).IsEqualTo(new NodeId("schema-a", 1));
        await Assert.That(createdResource.VersionId).IsEqualTo("v1");
        await Assert.That(createdResource.FileHandle).IsEqualTo(42u);
        await Assert.That(resolvedResource.ResourceNode).IsEqualTo(new NodeId("schema-b", 1));
        await Assert.That(resolvedResource.VersionId).IsEqualTo("v2");
        await Assert.That(resolvedResource.FileHandle).IsEqualTo(43u);
        await Assert.That(resolvedResource.Created).IsFalse();
        await Assert.That(session.MethodCalls.Count).IsEqualTo(4);
        await Assert.That(session.MethodCalls[0].Target).IsEqualTo(File);
        await Assert.That(session.MethodCalls[1].Target).IsEqualTo(File);
        await Assert.That(session.MethodCalls[2].Target).IsEqualTo(new NodeId("team-a", 1));
        await Assert.That(session.MethodCalls[3].Target).IsEqualTo(new NodeId("team-a", 1));
        await Assert.That(session.MethodCalls[0].Method).IsEqualTo("CreateGroup");
        await Assert.That(session.MethodCalls[1].Method).IsEqualTo("GetOrCreateGroup");
        await Assert.That(session.MethodCalls[2].Method).IsEqualTo("CreateResource");
        await Assert.That(session.MethodCalls[3].Method).IsEqualTo("GetOrCreateResource");
        await Assert.That(session.MethodCalls[2].Arguments.Count).IsEqualTo(3);
        await Assert.That(session.MethodCalls[2].Arguments[0].TryGetValue(out string? createResourceId) &&
            createResourceId == "schema-a").IsTrue();
        await Assert.That(session.MethodCalls[2].Arguments[1].TryGetValue(out string? createVersionId) &&
            createVersionId == "v1").IsTrue();
        await Assert.That(session.MethodCalls[2].Arguments[2].TryGetValue(out bool requestOpen) && requestOpen).IsTrue();
        await Assert.That(session.MethodCalls[3].Arguments[0].TryGetValue(out string? resolveResourceId) &&
            resolveResourceId == "schema-b").IsTrue();
        await Assert.That(session.MethodCalls[3].Arguments[1].TryGetValue(out string? resolveVersionId) &&
            resolveVersionId == "v2").IsTrue();
        await Assert.That(session.MethodCalls[3].Arguments[2].TryGetValue(out bool resolveOpen) &&
            resolveOpen).IsTrue();
    }

    [Test]
    public async Task GroupCreationRejectsMalformedMethodOutput()
    {
        var (session, writer) = await WriterForResource();
        session.MethodOutputs["CreateGroup"] = [new Variant(42u)];

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await writer.CreateGroupAsync(File, "team-a"));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.MethodCalls.Single().Method).IsEqualTo("CreateGroup");
    }

    [Test]
    public async Task WriterMetaAttributesTargetLogicalResource()
    {
        var (session, writer) = await WriterForResource();
        var entity = LogicalResourceNode;
        var logicalMetaLabels = new NodeId("logical-meta-labels", 1);
        var logicalLabels = new NodeId("logical-labels", 1);
        var exactVersionLabels = new NodeId("default-version-labels", 1);

        await writer.DeleteAsync(entity);
        await writer.DeleteAsync(entity, 5);
        await writer.AddAttributeAsync(logicalLabels, "team", "alpha", 6);
        await writer.RemoveAttributeAsync(exactVersionLabels, "team", 7);
        await writer.AddMetaAttributeAsync(logicalMetaLabels, "region", "west", 8);
        await writer.RemoveMetaAttributeAsync(logicalMetaLabels, "region", 9);

        await Assert.That(session.MethodCalls.Count).IsEqualTo(6);
        await Assert.That(session.MethodCalls[0].Method).IsEqualTo("Delete");
        await Assert.That(session.MethodCalls[0].Target).IsEqualTo(LogicalResourceNode);
        await Assert.That(session.MethodCalls[0].Arguments.Count).IsEqualTo(0);
        await Assert.That(session.MethodCalls[1].Method).IsEqualTo("Delete");
        await Assert.That(session.MethodCalls[1].Target).IsEqualTo(LogicalResourceNode);
        await Assert.That(session.MethodCalls[1].Arguments[0].TryGetValue(out uint deleteEpoch) &&
            deleteEpoch == 5).IsTrue();
        await Assert.That(session.MethodCalls[2].Method).IsEqualTo("AddAttribute");
        await Assert.That(session.MethodCalls[2].Target).IsEqualTo(logicalLabels);
        await Assert.That(session.MethodCalls[2].Arguments[0].TryGetValue(out string? addKey) &&
            addKey == "team").IsTrue();
        await Assert.That(session.MethodCalls[2].Arguments[1].TryGetValue(out string? addValue) &&
            addValue == "alpha").IsTrue();
        await Assert.That(session.MethodCalls[2].Arguments[2].TryGetValue(out uint addEpoch) &&
            addEpoch == 6).IsTrue();
        await Assert.That(session.MethodCalls[3].Method).IsEqualTo("RemoveAttribute");
        await Assert.That(session.MethodCalls[3].Target).IsEqualTo(exactVersionLabels);
        await Assert.That(session.MethodCalls[3].Arguments[0].TryGetValue(out string? removeKey) &&
            removeKey == "team").IsTrue();
        await Assert.That(session.MethodCalls[3].Arguments[1].TryGetValue(out uint removeEpoch) &&
            removeEpoch == 7).IsTrue();
        await Assert.That(session.MethodCalls[4].Method).IsEqualTo("AddAttribute");
        await Assert.That(session.MethodCalls[4].Arguments[0].TryGetValue(out string? addMetaKey) &&
            addMetaKey == "region").IsTrue();
        await Assert.That(session.MethodCalls[4].Arguments[1].TryGetValue(out string? addMetaValue) &&
            addMetaValue == "west").IsTrue();
        await Assert.That(session.MethodCalls[4].Arguments[2].TryGetValue(out uint addMetaEpoch) &&
            addMetaEpoch == 8).IsTrue();
        await Assert.That(session.MethodCalls[5].Method).IsEqualTo("RemoveAttribute");
        await Assert.That(session.MethodCalls[5].Arguments[0].TryGetValue(out string? removeMetaKey) &&
            removeMetaKey == "region").IsTrue();
        await Assert.That(session.MethodCalls[5].Arguments[1].TryGetValue(out uint removeMetaEpoch) &&
            removeMetaEpoch == 9).IsTrue();
        await Assert.That(session.MethodCalls[4].Target).IsEqualTo(logicalMetaLabels);
        await Assert.That(session.MethodCalls[5].Target).IsEqualTo(logicalMetaLabels);
    }

    private static string NodeIdTrace(IEnumerable<NodeId> nodes) =>
        string.Join("|", nodes.Select(node => node.IdentifierAsString));

    private static OpcUaReference Ref(string name) => new(new NodeId(name, 1), new QualifiedName(name, 1));
    private static OpcUaReference SpecVersionRef() =>
        new(new NodeId("SpecVersion", 1), new QualifiedName("SpecVersion", 1), NodeClass.Variable);
    private static OpcUaReference GroupRef(string name, NodeId groupType) =>
        new(new NodeId(name, 1), new QualifiedName(name, 1), NodeClass.Object, groupType);
    private static OpcUaReference TypedObjectRef(string name, NodeId objectType) =>
        new(new NodeId(name, 1), new QualifiedName(name, 1), NodeClass.Object, objectType);
    private static OpcUaReference TypedObjectRef(string name, NodeId node, NodeId objectType) =>
        new(node, new QualifiedName(name, 1), NodeClass.Object, objectType);
    private static OpcUaReference VariableRef(string name) =>
        new(new NodeId(name, 1), new QualifiedName(name, 1), NodeClass.Variable);
    private static OpcUaReference VariableRef(string name, string nodePrefix) =>
        new(new NodeId(nodePrefix + ":" + name, 1), new QualifiedName(name, 1), NodeClass.Variable);
    private static void QueueDocumentSelection(FakeSession session, bool explicitVersion)
    {
        session.Pages.Enqueue(new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType),
            TypedObjectRef("v2", new NodeId("group-decoy-v2", 1), ResourceType)
        ]));
        if (explicitVersion)
        {
            session.Pages.Enqueue(new([
                VariableRef("ResourceId", "logical"),
                TypedObjectRef("Versions", VersionsNode, ResourceVersionsType)
            ]));
            session.Pages.Enqueue(new([
                TypedObjectRef("v1", DefaultVersionNode, ResourceType),
                TypedObjectRef("v2", VersionTwoNode, ResourceType)
            ]));
            session.Pages.Enqueue(new([
                VariableRef("ResourceId", "v2"), VariableRef("VersionId", "v2"),
                VariableRef("Xid", "v2"), VariableRef("Epoch", "v2"),
                VariableRef("Name", "v2"), VariableRef("CreatedAt", "v2"),
                VariableRef("ModifiedAt", "v2")
            ]));
        }
        else
        {
            session.Pages.Enqueue(new([
                VariableRef("ResourceId", "logical"), VariableRef("VersionId", "logical"),
                VariableRef("Xid", "logical"), VariableRef("Epoch", "logical"),
                VariableRef("Name", "logical"), VariableRef("CreatedAt", "logical"),
                VariableRef("ModifiedAt", "logical")
            ]));
        }
    }
    private static OpcUaReference FileRef(string name) =>
        new(new NodeId(name, 1), new QualifiedName(name, 1), NodeClass.Object, ObjectTypeIds.FileType);
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static OpcUaJsonValue Value(string json, BuiltInType builtInType, string? dataTypeName = null) =>
        new(Json(json), builtInType, dataTypeName);

    private static async ValueTask<JsonElement> ReadMetaWithDeprecated(OpcUaJsonValue value)
    {
        var session = new FakeSession
        {
            JsonValues =
            {
                ["meta:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                ["meta:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                ["meta:MetaDeprecated"] = value
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId", "meta"), VariableRef("Xid", "meta"), VariableRef("MetaDeprecated", "meta")
        ]));
        return await reader.ReadResourceMetaPropertiesAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType);
    }

    private static async ValueTask<OpcUaRegistryReader> ValidatedReader(FakeSession session, int maxBytes = 64 * 1024 * 1024)
    {
        var reader = new OpcUaRegistryReader(session, maxBytes);
        session.Pages.Enqueue(new([SpecVersionRef()]));
        await reader.ReadSpecVersionAsync(File, _ => true);
        return reader;
    }

    private static async ValueTask<OpcUaRegistryReader> ModelReader(FakeSession session, string model = TestModel,
        int maxBytes = 64 * 1024 * 1024, int chunkSize = 64 * 1024)
    {
        var reader = new OpcUaRegistryReader(session, maxBytes, chunkSize);
        session.Pages.Enqueue(new([SpecVersionRef()]));
        session.Pages.Enqueue(new([FileRef("Model")]));
        session.Chunks.Insert(0, System.Text.Encoding.UTF8.GetBytes(model));
        session.Chunks.Insert(1, Array.Empty<byte>());
        await reader.ReadSpecVersionAsync(File, _ => true);
        _ = await reader.ReadModelDefinitionAsync(File);
        return reader;
    }

    private static FakeSession SourceSession(string model = TestModel)
    {
        var session = new FakeSession();
        session.Chunks.AddRange([
            System.Text.Encoding.UTF8.GetBytes(model), Array.Empty<byte>(),
            System.Text.Encoding.UTF8.GetBytes("""{"flags":["doc"]}"""), Array.Empty<byte>()
        ]);
        session.JsonValues["RegistryId"] = Value("\"registry-a\"", BuiltInType.String);
        session.JsonValues["SpecVersion"] = Value("\"1.0-rc4\"", BuiltInType.String);
        session.JsonValues["Xid"] = Value("\"/\"", BuiltInType.String);
        session.JsonValues["Epoch"] = Value("1", BuiltInType.UInt32);
        session.JsonValues["CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.BrowsePages[File] = new([
            new([SpecVersionRef()]),
            new([VariableRef("RegistryId"), VariableRef("SpecVersion"), VariableRef("Xid"),
                VariableRef("Epoch"), VariableRef("CreatedAt"), VariableRef("ModifiedAt")]),
            new([FileRef("Model")]),
            new([FileRef("Capabilities")]),
            new([TypedObjectRef("team-a", GroupNode, new NodeId("GroupType", 1))])
        ]);
        return session;
    }

    private static void ConfigureResourceAddressSpace(FakeSession session, bool hasExternalReference = false)
    {
        session.BrowsePages[GroupNode] = new([
            GroupPropertiesPage(), ResourceReferencePage(), ResourceReferencePage(), ResourceReferencePage(),
            ResourceReferencePage(), GroupPropertiesPage(), ResourceReferencePage(), GroupPropertiesPage(),
            ResourceReferencePage(), ResourceReferencePage(), ResourceReferencePage()
        ]);
        session.JsonValues["group:GroupId"] = Value("\"team-a\"", BuiltInType.String);
        session.JsonValues["group:Xid"] = Value("\"/teams/team-a\"", BuiltInType.String);
        session.JsonValues["group:Epoch"] = Value("2", BuiltInType.UInt32);
        session.JsonValues["group:CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["group:ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["group:Name"] = Value("\"Team A\"", BuiltInType.String);
        session.BrowsePages[LogicalResourceNode] = new([
            new([
                VariableRef("ResourceId", "logical"), VariableRef("VersionId", "logical"),
                VariableRef("Xid", "logical"), VariableRef("Epoch", "logical"),
                VariableRef("CreatedAt", "logical"), VariableRef("ModifiedAt", "logical"),
                VariableRef("Name", "logical"), VariableRef("Format", "logical"),
                VariableRef("ContentType", "logical"), VariableRef("MetaEpoch", "logical"),
                VariableRef("MetaCreatedAt", "logical"), VariableRef("MetaModifiedAt", "logical"),
                VariableRef("MetaName", "logical"), VariableRef("MetaDefaultVersionSticky", "logical"),
                VariableRef("MetaReadonly", "logical"), VariableRef("MetaCompatibility", "logical"),
                TypedObjectRef("Labels", new NodeId("logical-labels", 1), AttributesType),
                TypedObjectRef("MetaLabels", new NodeId("logical-meta-labels", 1), AttributesType),
                TypedObjectRef("Versions", VersionsNode, ResourceVersionsType)
            ])
        ]);
        if (hasExternalReference)
        {
            session.BrowsePages[LogicalResourceNode] = new([
                new([
                    VariableRef("ResourceId", "logical"), VariableRef("VersionId", "logical"),
                    VariableRef("Xid", "logical"), VariableRef("Epoch", "logical"),
                    VariableRef("CreatedAt", "logical"), VariableRef("ModifiedAt", "logical"),
                    VariableRef("Name", "logical"), VariableRef("Format", "logical"),
                    VariableRef("ContentType", "logical"), VariableRef("MetaEpoch", "logical"),
                    VariableRef("ExternalReference", "logical"),
                    VariableRef("MetaCreatedAt", "logical"), VariableRef("MetaModifiedAt", "logical"),
                    VariableRef("MetaName", "logical"), VariableRef("MetaDefaultVersionSticky", "logical"),
                    VariableRef("MetaReadonly", "logical"), VariableRef("MetaCompatibility", "logical"),
                    TypedObjectRef("Labels", new NodeId("logical-labels", 1), AttributesType),
                    TypedObjectRef("MetaLabels", new NodeId("logical-meta-labels", 1), AttributesType),
                    TypedObjectRef("Versions", VersionsNode, ResourceVersionsType)
                ])
            ]);
        }
        session.JsonValues["logical:ResourceId"] = Value("\"schema-a\"", BuiltInType.String);
        session.JsonValues["logical:VersionId"] = Value("\"v1\"", BuiltInType.String);
        session.JsonValues["logical:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String);
        session.JsonValues["logical:Epoch"] = Value("4", BuiltInType.UInt32);
        session.JsonValues["logical:CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["logical:ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["logical:Name"] = Value("\"Schema A\"", BuiltInType.String);
        session.JsonValues["logical:Format"] = Value("\"json\"", BuiltInType.String);
        session.JsonValues["logical:ContentType"] = Value("\"application/json\"", BuiltInType.String);
        session.JsonValues["logical:MetaEpoch"] = Value("3", BuiltInType.UInt32);
        session.JsonValues["logical:MetaCreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["logical:MetaModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["logical:MetaName"] = Value("\"Schema A metadata\"", BuiltInType.String);
        session.JsonValues["logical:MetaDefaultVersionSticky"] = Value("false", BuiltInType.Boolean);
        session.JsonValues["logical:MetaReadonly"] = Value("false", BuiltInType.Boolean);
        session.JsonValues["logical:MetaCompatibility"] = Value("\"backward\"", BuiltInType.String);
        session.BrowsePages[new NodeId("logical-labels", 1)] =
            new([new([VariableRef("region", "logical-label")])]);
        session.BrowsePages[new NodeId("logical-meta-labels", 1)] =
            new([new([VariableRef("region", "logical-meta-label")])]);
        session.StringValues["logical-label:region"] = "resource-region";
        session.StringValues["logical-meta-label:region"] = "meta-region";
        session.BrowsePages[VersionsNode] =
            new([new([TypedObjectRef("v1", DefaultVersionNode, ResourceType)])]);
        session.BrowsePages[DefaultVersionNode] = new([
            new([
                VariableRef("ResourceId", "version"), VariableRef("VersionId", "version"),
                VariableRef("Xid", "version"), VariableRef("Epoch", "version"),
                VariableRef("CreatedAt", "version"), VariableRef("ModifiedAt", "version"),
                VariableRef("Name", "version"), VariableRef("Format", "version"),
                VariableRef("ContentType", "version"),
                TypedObjectRef("Labels", new NodeId("version-labels", 1), AttributesType)
            ])
        ]);
        session.JsonValues["version:ResourceId"] = Value("\"schema-a\"", BuiltInType.String);
        session.JsonValues["version:VersionId"] = Value("\"v1\"", BuiltInType.String);
        session.JsonValues["version:Xid"] = Value(
            "\"/teams/team-a/schemas/schema-a/versions/v1\"", BuiltInType.String);
        session.JsonValues["version:Epoch"] = Value("4", BuiltInType.UInt32);
        session.JsonValues["version:CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["version:ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["version:Name"] = Value("\"Schema A v1\"", BuiltInType.String);
        session.JsonValues["version:Format"] = Value("\"json\"", BuiltInType.String);
        session.JsonValues["version:ContentType"] = Value("\"application/json\"", BuiltInType.String);
        session.BrowsePages[new NodeId("version-labels", 1)] = new([new([])]);
    }

    private static void ConfigureResourceCollectionWalk(FakeSession session) =>
        session.BrowsePages[GroupNode] = new([
            GroupPropertiesPage(), ResourceReferencePage(),
            GroupPropertiesPage(), ResourceReferencePage(), ResourceReferencePage(), ResourceReferencePage(),
            ResourceReferencePage(), GroupPropertiesPage(), ResourceReferencePage(),
            GroupPropertiesPage(), ResourceReferencePage(), ResourceReferencePage(), ResourceReferencePage()
        ]);

    private static OpcUaBrowsePage GroupPropertiesPage() => new([
        VariableRef("GroupId", "group"), VariableRef("Xid", "group"),
        VariableRef("Epoch", "group"), VariableRef("CreatedAt", "group"),
        VariableRef("ModifiedAt", "group"), VariableRef("Name", "group")
    ]);

    private static OpcUaBrowsePage ResourceReferencePage() => new([
        TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)
    ]);

    private static async ValueTask<OpcUaRegistryReadSource> OpenSource(FakeSession session,
        OpcUaRegistryTypeMap? types = null)
    {
        var reader = new OpcUaRegistryReader(session);
        await reader.ReadSpecVersionAsync(File, _ => true);
        var context = new NativeRegistryContext("opcua", "opc.tcp://localhost:4840");
        types ??= new OpcUaRegistryTypeMap(
            new Dictionary<string, NodeId>(StringComparer.Ordinal)
            {
                ["teams"] = new("GroupType", 1)
            },
            new Dictionary<(string GroupCollection, string ResourceCollection), OpcUaResourceTypeBinding>
            {
                [("teams", "schemas")] = new(ResourceType, ResourceVersionsType)
            },
            AttributesType);
        return await OpcUaRegistryReadSource.OpenAsync(reader, File, context, types);
    }

    private static async ValueTask<(FakeSession Session, OpcUaRegistryReadSource Source)> OpenExternalReferenceSource()
    {
        var session = SourceSession();
        ConfigureResourceAddressSpace(session, hasExternalReference: true);
        var logicalReference = new ExpandedNodeId(new NodeId(7u, 2), null, 2u);
        var versionReference = new ExpandedNodeId(new NodeId(42u, 2), null, 2u);
        session.ExpandedNodeIds["logical:ExternalReference"] = logicalReference;
        session.ExpandedNodeIds["version:ExternalReference"] = versionReference;
        session.ExternalNodeIdMappings[logicalReference] = new(
            "urn:logical:application", "urn:logical:namespace", IdType.Numeric, 7u);
        session.ExternalNodeIdMappings[versionReference] = new(
            "urn:remote:application", "urn:remote:namespace", IdType.Numeric, 42u);
        var versionProperties = new List<OpcUaReference>
        {
            VariableRef("ResourceId", "version"), VariableRef("VersionId", "version"),
            VariableRef("Xid", "version"), VariableRef("Epoch", "version"),
            VariableRef("CreatedAt", "version"), VariableRef("ModifiedAt", "version"),
            VariableRef("Name", "version"), VariableRef("Format", "version"),
            VariableRef("ContentType", "version"), VariableRef("ExternalReference", "version"),
            TypedObjectRef("Labels", new NodeId("version-labels", 1), AttributesType)
        };
        session.BrowsePages[DefaultVersionNode] = new([new(versionProperties)]);
        return (session, await OpenSource(session));
    }

    private static async ValueTask<(FakeSession Session, OpcUaRegistryWriter Writer)> WriterForResource(
        string model = TestModel, int maxBytes = 64 * 1024 * 1024, int chunkSize = 64 * 1024,
        FakeSession? session = null)
    {
        session ??= new FakeSession();
        session.JsonValues["logical:ResourceId"] = Value("\"schema-a\"", BuiltInType.String);
        session.JsonValues["logical:VersionId"] = Value("\"v1\"", BuiltInType.String);
        session.JsonValues["logical:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String);
        session.JsonValues["logical:Epoch"] = Value("4", BuiltInType.UInt32);
        session.JsonValues["logical:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime);
        session.JsonValues["logical:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime);
        session.JsonValues["logical:Name"] = Value("\"Schema A delegated default\"", BuiltInType.String);
        session.JsonValues["default:ResourceId"] = Value("\"schema-a\"", BuiltInType.String);
        session.JsonValues["default:VersionId"] = Value("\"decoy-default\"", BuiltInType.String);
        session.JsonValues["default:Xid"] = Value("\"/decoy/default-version\"", BuiltInType.String);
        session.JsonValues["default:Epoch"] = Value("99", BuiltInType.UInt32);
        session.JsonValues["default:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime);
        session.JsonValues["default:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime);
        session.JsonValues["default:Name"] = Value("\"Do not use exact default node\"", BuiltInType.String);
        session.JsonValues["v2:ResourceId"] = Value("\"schema-a\"", BuiltInType.String);
        session.JsonValues["v2:VersionId"] = Value("\"v2\"", BuiltInType.String);
        session.JsonValues["v2:Xid"] = Value("\"/teams/team-a/schemas/schema-a/versions/v2\"", BuiltInType.String);
        session.JsonValues["v2:Epoch"] = Value("5", BuiltInType.UInt32);
        session.JsonValues["v2:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime);
        session.JsonValues["v2:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime);
        session.JsonValues["v2:Name"] = Value("\"Schema A v2\"", BuiltInType.String);
        var closeFailure = session.CloseFailure;
        session.CloseFailure = null;
        var reader = await ModelReader(session, model, maxBytes, chunkSize);
        session.CloseFailure = closeFailure;
        session.Pages.Enqueue(new([
            TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)
        ]));
        session.Pages.Enqueue(new([
            VariableRef("ResourceId", "logical"), VariableRef("VersionId", "logical"),
            VariableRef("Xid", "logical"), VariableRef("Epoch", "logical"),
            VariableRef("Name", "logical"), VariableRef("CreatedAt", "logical"),
            VariableRef("ModifiedAt", "logical")
        ]));
        return (session, new OpcUaRegistryWriter(reader));
    }

    private sealed class FakeSession : IOpcUaRegistrySession
    {
        public List<byte[]> Chunks { get; } = [];
        public Queue<OpcUaBrowsePage> Pages { get; } = new();
        public Dictionary<NodeId, Queue<OpcUaBrowsePage>> BrowsePages { get; } = [];
        public List<NodeId> BrowsedNodes { get; } = [];
        public List<byte[]> Released { get; } = [];
        public Dictionary<string, OpcUaJsonValue> JsonValues { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> StringValues { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ExpandedNodeId> ExpandedNodeIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<ExpandedNodeId, OpcUaExternalNodeId> ExternalNodeIdMappings { get; } = [];
        public IReadOnlyList<string> ServerArray { get; init; } = ["urn:xregistry"];
        public IReadOnlyList<string> NamespaceArray { get; init; } =
            ["http://opcfoundation.org/UA/", "urn:xregistry"];
        public Dictionary<string, IReadOnlyList<Variant>> MethodOutputs { get; } = new(StringComparer.Ordinal);
        public Exception? ReadFailure { get; init; }
        public Exception? WriteFailure { get; init; }
        public Exception? WriteValueFailure { get; init; }
        public Exception? MethodFailure { get; init; }
        public Exception? PositionFailure { get; init; }
        public Exception? CloseFailure { get; set; }
        public Exception? BrowseNextFailure { get; init; }
        public Exception? ReleaseFailure { get; init; }
        public bool IsRegistry { get; init; } = true;
        public bool AdvanceMetadataRevisionOnOpen { get; set; }
        public int Closed { get; private set; }
        public int Opened { get; private set; }
        public int FileReads { get; private set; }
        public byte OpenMode { get; private set; }
        public NodeId LastOpenedFile { get; private set; }
        public List<NodeId> OpenedFiles { get; } = [];
        public List<NodeId> ClosedFiles { get; } = [];
        public List<(string Property, NodeId File)> PropertyReadsWhileFileOpen { get; } = [];
        public List<(NodeId File, uint Handle)> FileReadHandles { get; } = [];
        public List<(NodeId File, uint Handle)> FileClosedHandles { get; } = [];
        public uint LastClosedHandle { get; private set; }
        private Dictionary<string, OpcUaJsonValue>? metadataPropertySnapshot;
        private Queue<byte[]>? metadataChunkSnapshot;
        private bool metadataRevisionAdvanced;
        public NodeId LastPositionFile { get; private set; }
        public uint LastPositionHandle { get; private set; }
        public NodeId LastWrittenFile { get; private set; }
        public uint LastWrittenHandle { get; private set; }
        public ulong Position { get; private set; }
        public List<byte[]> WrittenChunks { get; } = [];
        public List<NodeId> WrittenFiles { get; } = [];
        public List<(NodeId Variable, Variant Value)> WrittenValues { get; } = [];
        public List<(NodeId Target, string Method, IReadOnlyList<Variant> Arguments)> MethodCalls { get; } = [];

        public ValueTask<IReadOnlyList<string>> ReadServerArrayAsync(CancellationToken cancellationToken) =>
            new(ServerArray);

        public ValueTask<NamespaceTable> ReadNamespaceArrayAsync(CancellationToken cancellationToken) =>
            new(new NamespaceTable(NamespaceArray));

        public ValueTask<bool> IsRegistryNodeAsync(NodeId node, CancellationToken cancellationToken) => new(IsRegistry);

        public ValueTask<bool> IsTypeOrSubtypeAsync(NodeId actualType, NodeId declaredType,
            CancellationToken cancellationToken) => new(actualType == declaredType);

        public ValueTask<OpcUaBrowsePage> BrowseAsync(NodeId node, CancellationToken cancellationToken)
        {
            BrowsedNodes.Add(node);
            if (BrowsePages.TryGetValue(node, out var pages) && pages.Count > 0)
            {
                return new(pages.Count == 1 ? pages.Peek() : pages.Dequeue());
            }
            return new(Pages.Dequeue());
        }

        public ValueTask<OpcUaBrowsePage> BrowseNextAsync(byte[] continuationPoint, bool release, CancellationToken cancellationToken)
        {
            if (release)
            {
                Released.Add(continuationPoint);
                return ReleaseFailure is null ? new(new OpcUaBrowsePage([])) : throw ReleaseFailure;
            }
            return BrowseNextFailure is null ? new(Pages.Dequeue()) : throw BrowseNextFailure;
        }

        public ValueTask<uint> OpenAsync(NodeId file, byte mode, CancellationToken cancellationToken)
        {
            if (AdvanceMetadataRevisionOnOpen && !metadataRevisionAdvanced &&
                file.IdentifierAsString == "Metadata")
            {
                metadataPropertySnapshot = JsonValues.Where(pair => pair.Key.StartsWith("metadata:", StringComparison.Ordinal))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                metadataChunkSnapshot = new(Chunks.Select(chunk => chunk.ToArray()));
                JsonValues["metadata:Name"] = Value("\"Advanced metadata revision\"", BuiltInType.String);
                Chunks.Clear();
                Chunks.Add(System.Text.Encoding.UTF8.GetBytes("""{"name":"Advanced metadata revision"}"""));
                Chunks.Add([]);
                metadataRevisionAdvanced = true;
            }
            Opened++;
            LastOpenedFile = file;
            OpenedFiles.Add(file);
            OpenMode = mode;
            return new(7u);
        }

        public ValueTask<byte[]> ReadAsync(NodeId file, uint handle, int length, CancellationToken cancellationToken)
        {
            FileReads++;
            FileReadHandles.Add((file, handle));
            if (ReadFailure is not null)
            {
                throw ReadFailure;
            }
            var chunk = file.IdentifierAsString == "Metadata" && metadataChunkSnapshot is not null
                ? metadataChunkSnapshot.Dequeue()
                : Chunks[0];
            if (file.IdentifierAsString != "Metadata" || metadataChunkSnapshot is null)
            {
                Chunks.RemoveAt(0);
            }
            return new(chunk);
        }

        public ValueTask SetPositionAsync(NodeId file, uint handle, ulong position,
            CancellationToken cancellationToken)
        {
            if (PositionFailure is not null)
            {
                throw PositionFailure;
            }
            LastPositionFile = file;
            LastPositionHandle = handle;
            Position = position;
            return default;
        }

        public ValueTask WriteAsync(NodeId file, uint handle, byte[] data, CancellationToken cancellationToken)
        {
            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }
            LastWrittenFile = file;
            LastWrittenHandle = handle;
            WrittenFiles.Add(file);
            WrittenChunks.Add(data);
            return default;
        }

        public ValueTask WriteValueAsync(NodeId variable, Variant value, CancellationToken cancellationToken)
        {
            if (WriteValueFailure is not null)
            {
                throw WriteValueFailure;
            }
            WrittenValues.Add((variable, value));
            return default;
        }

        public ValueTask<IReadOnlyList<Variant>> CallMethodAsync(NodeId target, string methodBrowseName,
            IReadOnlyList<Variant> inputArguments, CancellationToken cancellationToken)
        {
            if (MethodFailure is not null)
            {
                throw MethodFailure;
            }
            MethodCalls.Add((target, methodBrowseName, inputArguments.ToArray()));
            return new(MethodOutputs.TryGetValue(methodBrowseName, out var outputs) ? outputs : []);
        }

        public string SpecVersion { get; init; } = "1.0-rc4";
        public OpcUaJsonValue JsonValue { get; init; } = Value("42", BuiltInType.UInt32);

        public ValueTask<string> ReadStringAsync(NodeId variable, CancellationToken cancellationToken) =>
            new(StringValues.TryGetValue(variable.IdentifierAsString, out var value) ? value : SpecVersion);
        public ValueTask<OpcUaJsonValue> ReadJsonValueAsync(NodeId variable, CancellationToken cancellationToken)
        {
            if (Opened > Closed)
            {
                PropertyReadsWhileFileOpen.Add((variable.IdentifierAsString, LastOpenedFile));
            }
            if (Opened > Closed && LastOpenedFile.IdentifierAsString == "Metadata" &&
                metadataPropertySnapshot is not null &&
                metadataPropertySnapshot.TryGetValue(variable.IdentifierAsString, out var pinnedValue))
            {
                return new(pinnedValue);
            }
            return new(JsonValues.TryGetValue(variable.IdentifierAsString, out var value) ? value : JsonValue);
        }
        public ValueTask<ExpandedNodeId> ReadExpandedNodeIdAsync(NodeId variable, CancellationToken cancellationToken) =>
            new(ExpandedNodeIds[variable.IdentifierAsString]);

        public ValueTask<OpcUaExternalNodeId> MapExternalNodeIdAsync(ExpandedNodeId nodeId,
            CancellationToken cancellationToken) => new(ExternalNodeIdMappings[nodeId]);

        public ValueTask CloseAsync(NodeId file, uint handle, CancellationToken cancellationToken)
        {
            Closed++;
            ClosedFiles.Add(file);
            FileClosedHandles.Add((file, handle));
            LastClosedHandle = handle;
            if (file.IdentifierAsString == "Metadata")
            {
                metadataPropertySnapshot = null;
                metadataChunkSnapshot = null;
            }
            return CloseFailure is null ? default : throw CloseFailure;
        }

    }

    [Test]
    public async Task RegistryContentsCannotBeBrowsedBeforeSpecVersionIsAccepted()
    {
        var reader = new OpcUaRegistryReader(new FakeSession());

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.BrowseAllAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedVersion);
    }

    [Test]
    public async Task FileContentsCannotBeReadBeforeSpecVersionIsAccepted()
    {
        var session = new FakeSession();
        var reader = new OpcUaRegistryReader(session);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ReadFileAsync(File));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedVersion);
        await Assert.That(session.OpenMode).IsEqualTo((byte)0);
    }

    [Test]
    [Arguments("missing")]
    [Arguments("wrong-type")]
    [Arguments("duplicate")]
    public async Task ReaderExplicitVersionRejectsInvalidVersionsContainer(string scenario)
    {
        var session = new FakeSession();
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)]));
        session.Pages.Enqueue(scenario switch
        {
            "wrong-type" => new OpcUaBrowsePage([
                TypedObjectRef("Versions", VersionsNode, new NodeId("OtherType", 1))]),
            "duplicate" => new OpcUaBrowsePage([
                TypedObjectRef("Versions", VersionsNode, ResourceVersionsType),
                TypedObjectRef("Versions", new NodeId("duplicate-versions", 1), ResourceVersionsType)]),
            _ => new OpcUaBrowsePage([])
        });

        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await reader.ReadResourcePropertiesAsync(
                GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType, "v2",
                ResourceVersionsType));

        await Assert.That(error!.Code).IsEqualTo(
            scenario == "duplicate" ? FederationErrorCode.Ambiguous : FederationErrorCode.NotFound);
        await Assert.That(NodeIdTrace(session.BrowsedNodes)).IsEqualTo(
            "Model|Model|group-team-a|logical-schema-a");
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model");
    }

    [Test]
    public async Task WriterDocumentReplacementTargetsDefaultOrExactVersion()
    {
        var (session, writer) = await WriterForResource();

        await writer.ReplaceResourceDocumentAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType, new byte[] { 3, 4 });
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|logical-schema-a");
        await Assert.That(session.WrittenChunks.Single()).IsEquivalentTo(new byte[] { 3, 4 });
        await Assert.That(NodeIdTrace(session.WrittenFiles)).IsEqualTo("logical-schema-a");
        await Assert.That(NodeIdTrace(session.ClosedFiles)).IsEqualTo("Model|logical-schema-a");
        QueueDocumentSelection(session, explicitVersion: true);
        await writer.ReplaceResourceDocumentAsync(
            GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType,
            new byte[] { 7, 8 }, "v2", ResourceVersionsType);

        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|logical-schema-a|schema-a-v2");
        await Assert.That(NodeIdTrace(session.WrittenFiles)).IsEqualTo("logical-schema-a|schema-a-v2");
        await Assert.That(session.WrittenChunks[0]).IsEquivalentTo(new byte[] { 3, 4 });
        await Assert.That(session.WrittenChunks[1]).IsEquivalentTo(new byte[] { 7, 8 });
        await Assert.That(NodeIdTrace(session.ClosedFiles)).IsEqualTo("Model|logical-schema-a|schema-a-v2");
    }

    [Test]
    public async Task ReaderSeparatesLogicalMetaAttributesFromVersionAttributes()
    {
        var logicalMetaLabels = new NodeId("logical-meta-labels", 1);
        var logicalLabels = new NodeId("logical-labels", 1);
        var exactVersionLabels = new NodeId("default-version-labels", 1);
        var session = new FakeSession
        {
            JsonValues =
            {
                ["meta-epoch:MetaEpoch"] = Value("17", BuiltInType.UInt32),
                ["logical-epoch:Epoch"] = Value("41", BuiltInType.UInt32),
                ["exact-epoch:Epoch"] = Value("99", BuiltInType.UInt32)
            },
            StringValues =
            {
                ["meta-label:region"] = "logical-meta-only",
                ["logical-label:region"] = "logical-delegated-label",
                ["exact-version-label:region"] = "exact-version-label"
            }
        };
        var reader = await ModelReader(session);
        session.Pages.Enqueue(new([VariableRef("MetaEpoch", "meta-epoch")]));
        var metaEpoch = await reader.ReadJsonValueComponentAsync(LogicalResourceNode, "MetaEpoch");
        session.Pages.Enqueue(new([VariableRef("Epoch", "logical-epoch")]));
        var logicalEpoch = await reader.ReadJsonValueComponentAsync(LogicalResourceNode, "Epoch");
        session.Pages.Enqueue(new([VariableRef("Epoch", "exact-epoch")]));
        var exactEpoch = await reader.ReadJsonValueComponentAsync(DefaultVersionNode, "Epoch");
        session.Pages.Enqueue(new([
            new OpcUaReference(logicalMetaLabels, new QualifiedName("MetaLabels", 1),
                NodeClass.Object, AttributesType),
            new OpcUaReference(logicalLabels, new QualifiedName("Labels", 1),
                NodeClass.Object, AttributesType)
        ]));
        session.Pages.Enqueue(new([
            new OpcUaReference(new NodeId("logical-label:region", 1), new QualifiedName("region", 1),
                NodeClass.Variable)
        ]));
        var labels = await reader.ReadAttributesAsync(LogicalResourceNode, AttributesType);
        session.Pages.Enqueue(new([
            new OpcUaReference(exactVersionLabels, new QualifiedName("Labels", 1),
                NodeClass.Object, AttributesType)
        ]));
        session.Pages.Enqueue(new([
            new OpcUaReference(new NodeId("exact-version-label:region", 1), new QualifiedName("region", 1),
                NodeClass.Variable)
        ]));
        var exactLabels = await reader.ReadAttributesAsync(DefaultVersionNode, AttributesType);
        // The current convenience API selects Labels only. Resolve MetaLabels through
        // the public browse/read APIs rather than pretending it is a separate Meta entity.
        session.Pages.Enqueue(new([
            new OpcUaReference(logicalLabels, new QualifiedName("Labels", 1),
                NodeClass.Object, AttributesType),
            new OpcUaReference(logicalMetaLabels, new QualifiedName("MetaLabels", 1),
                NodeClass.Object, AttributesType)
        ]));
        var logicalChildren = await reader.BrowseAllAsync(LogicalResourceNode);
        var metaLabels = logicalChildren.Single(child => child.BrowseName.Name == "MetaLabels");
        session.Pages.Enqueue(new([
            new OpcUaReference(new NodeId("meta-label:region", 1), new QualifiedName("region", 1),
                NodeClass.Variable)
        ]));
        var metaRegion = await reader.ReadStringComponentAsync(metaLabels.Target, "region");

        await Assert.That(metaEpoch.GetUInt32()).IsEqualTo(17u);
        await Assert.That(logicalEpoch.GetUInt32()).IsEqualTo(41u);
        await Assert.That(exactEpoch.GetUInt32()).IsEqualTo(99u);
        await Assert.That(labels.GetProperty("region").GetString()).IsEqualTo("logical-delegated-label");
        await Assert.That(exactLabels.GetProperty("region").GetString()).IsEqualTo("exact-version-label");
        await Assert.That(metaLabels.Target).IsEqualTo(logicalMetaLabels);
        await Assert.That(metaRegion).IsEqualTo("logical-meta-only");
        await Assert.That(NodeIdTrace(session.BrowsedNodes)).IsEqualTo(
            "Model|Model|logical-schema-a|logical-schema-a|schema-a-default-v1|" +
            "logical-schema-a|logical-labels|schema-a-default-v1|default-version-labels|" +
            "logical-schema-a|logical-meta-labels");
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model");
    }

    [Test]
    public async Task ReaderResourceCollectionHandlesEmptyAndSingletonLogicalResources()
    {
        foreach (var count in new[] { 0, 1 })
        {
            var session = new FakeSession
            {
                JsonValues =
                {
                    ["logical:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                    ["logical:VersionId"] = Value("\"v1\"", BuiltInType.String),
                    ["logical:Xid"] = Value("\"/teams/team-a/schemas/schema-a\"", BuiltInType.String),
                    ["logical:Epoch"] = Value("4", BuiltInType.UInt32),
                    ["logical:Name"] = Value("\"Schema A delegated default\"", BuiltInType.String),
                    ["logical:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                    ["logical:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                    ["default:Epoch"] = Value("99", BuiltInType.UInt32)
                }
            };
            var reader = await ModelReader(session);
            session.Pages.Enqueue(new(count == 0
                ? Array.Empty<OpcUaReference>()
                : [TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)]));
            if (count == 1)
            {
                session.Pages.Enqueue(new([VariableRef("ResourceId", "logical")]));
                session.Pages.Enqueue(new([
                    VariableRef("ResourceId", "logical"), VariableRef("VersionId", "logical"),
                    VariableRef("Xid", "logical"), VariableRef("Epoch", "logical"),
                    VariableRef("Name", "logical"), VariableRef("CreatedAt", "logical"),
                    VariableRef("ModifiedAt", "logical")
                ]));
            }

            var resources = await reader.ReadResourceCollectionAsync(
                GroupNode, "/teams/team-a", "schemas", ResourceType);

            await Assert.That(resources.GetArrayLength()).IsEqualTo(count);
            if (count == 1)
            {
                await Assert.That(resources[0].GetProperty("resourceid").GetString()).IsEqualTo("schema-a");
                await Assert.That(resources[0].GetProperty("versionid").GetString()).IsEqualTo("v1");
                await Assert.That(resources[0].GetProperty("epoch").GetUInt32()).IsEqualTo(4u);
                await Assert.That(session.BrowsedNodes.Count(node => node == LogicalResourceNode)).IsEqualTo(2);
                await Assert.That(session.BrowsedNodes.Contains(DefaultVersionNode)).IsFalse();
            }
        }
    }

    [Test]
    public async Task ReaderVersionCollectionPreservesEmptySingletonAndMembersRegardlessOfBrowseOrder()
    {
        foreach (var count in new[] { 0, 1, 2 })
        {
            var session = new FakeSession
            {
                JsonValues =
                {
                    ["v1:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                    ["v1:VersionId"] = Value("\"v1\"", BuiltInType.String),
                    ["v1:Xid"] = Value("\"/teams/team-a/schemas/schema-a/versions/v1\"", BuiltInType.String),
                    ["v1:Epoch"] = Value("4", BuiltInType.UInt32),
                    ["v1:Name"] = Value("\"Schema A v1\"", BuiltInType.String),
                    ["v1:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                    ["v1:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime),
                    ["v2:ResourceId"] = Value("\"schema-a\"", BuiltInType.String),
                    ["v2:VersionId"] = Value("\"v2\"", BuiltInType.String),
                    ["v2:Xid"] = Value("\"/teams/team-a/schemas/schema-a/versions/v2\"", BuiltInType.String),
                    ["v2:Epoch"] = Value("5", BuiltInType.UInt32),
                    ["v2:Name"] = Value("\"Schema A v2\"", BuiltInType.String),
                    ["v2:CreatedAt"] = Value("\"2026-01-02T03:04:05Z\"", BuiltInType.DateTime),
                    ["v2:ModifiedAt"] = Value("\"2026-01-03T03:04:05Z\"", BuiltInType.DateTime)
                }
            };
            var reader = await ModelReader(session);
            session.Pages.Enqueue(new([
                TypedObjectRef("schema-a", LogicalResourceNode, ResourceType),
                TypedObjectRef("schema-b", new NodeId("logical-schema-b", 1), ResourceType)
            ]));
            session.Pages.Enqueue(new([TypedObjectRef("Versions", VersionsNode, ResourceVersionsType)]));
            var versions = new List<OpcUaReference>();
            string[] versionIds = count == 2 ? ["v2", "v1"] : count == 1 ? ["v1"] : [];
            foreach (var versionId in versionIds)
            {
                versions.Add(TypedObjectRef(versionId,
                    versionId == "v1" ? DefaultVersionNode : VersionTwoNode, ResourceType));
            }
            session.Pages.Enqueue(new(versions));
            foreach (var versionId in versionIds)
            {
                session.Pages.Enqueue(new([
                    VariableRef("ResourceId", versionId), VariableRef("VersionId", versionId),
                    VariableRef("Xid", versionId), VariableRef("Epoch", versionId),
                    VariableRef("Name", versionId), VariableRef("CreatedAt", versionId),
                    VariableRef("ModifiedAt", versionId)
                ]));
            }

            var actual = await reader.ReadVersionCollectionAsync(
                GroupNode, "/teams/team-a", "schemas", "schema-a", ResourceType, ResourceVersionsType);
            var byVersionId = actual.EnumerateArray().ToDictionary(
                item => item.GetProperty("versionid").GetString()!, StringComparer.Ordinal);

            await Assert.That(actual.GetArrayLength()).IsEqualTo(count);
            if (count >= 1)
            {
                await Assert.That(byVersionId["v1"].GetProperty("resourceid").GetString()).IsEqualTo("schema-a");
                await Assert.That(byVersionId["v1"].GetProperty("xid").GetString())
                    .IsEqualTo("/teams/team-a/schemas/schema-a/versions/v1");
                await Assert.That(byVersionId["v1"].GetProperty("epoch").GetUInt32()).IsEqualTo(4u);
            }
            if (count == 2)
            {
                await Assert.That(byVersionId["v2"].GetProperty("xid").GetString())
                    .IsEqualTo("/teams/team-a/schemas/schema-a/versions/v2");
                await Assert.That(byVersionId["v2"].GetProperty("epoch").GetUInt32()).IsEqualTo(5u);
            }
            await Assert.That(session.BrowsedNodes.Contains(VersionsNode)).IsTrue();
            await Assert.That(session.BrowsedNodes.Contains(new NodeId("logical-schema-b", 1))).IsFalse();
        }
    }

    [Test]
    public async Task WriterCreateAndGetOrCreateRejectMalformedOutputs()
    {
        var cases = new[]
        {
            (Method: "CreateResource", Outputs: (IReadOnlyList<Variant>)[new Variant(new NodeId("wrong", 1))]),
            (Method: "GetOrCreateResource", Outputs: (IReadOnlyList<Variant>)[
                new Variant(new NodeId("schema-a", 1)), new Variant("v1"), new Variant(7u)]),
            (Method: "GetOrCreateGroup", Outputs: (IReadOnlyList<Variant>)[
                new Variant(new NodeId("team-a", 1)), new Variant("not-a-bool")])
        };

        foreach (var (method, outputs) in cases)
        {
            var (session, writer) = await WriterForResource();
            session.MethodOutputs[method] = outputs;
            var error = await Assert.ThrowsAsync<FederationException>(async () =>
            {
                switch (method)
                {
                    case "CreateResource":
                        _ = await writer.CreateResourceAsync(GroupNode, "schema-a", "v1");
                        break;
                    case "GetOrCreateResource":
                        _ = await writer.GetOrCreateResourceAsync(GroupNode, "schema-a", "v1");
                        break;
                    default:
                        _ = await writer.GetOrCreateGroupAsync(File, "team-a");
                        break;
                }
            });

            await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
            await Assert.That(session.MethodCalls.Single().Method).IsEqualTo(method);
            await Assert.That(session.MethodCalls.Single().Target).IsEqualTo(
                method == "GetOrCreateGroup" ? File : GroupNode);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task FederationMetadataCollectionPreservesValuesAndOptionalPresence(
        bool includeOptionalMembers)
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers);

        var result = await source.ReadAsync(new(FederationOperation.Collection,
            "/teams/team-a/schemas"));

        var entities = result.Metadata.GetProperty("entities");
        await Assert.That(result.Metadata.GetProperty("kind").GetString()).IsEqualTo("collection");
        await Assert.That(result.Metadata.GetProperty("complete").GetBoolean()).IsTrue();
        await Assert.That(entities.EnumerateObject().Select(property => property.Name))
            .IsEquivalentTo(["metadata-a"]);
        await AssertMetadataOnlyProjection(entities.GetProperty("metadata-a"), includeOptionalMembers);
        await Assert.That(result.Document).IsNull();
        await Assert.That(result.ExternalDocument.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities|Metadata");
        await Assert.That(session.Chunks.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task FederationMetadataEntityPreservesValuesAndOptionalPresence(
        bool includeOptionalMembers)
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers);

        var result = await source.ReadAsync(new(FederationOperation.Entity,
            "/teams/team-a/schemas/metadata-a"));

        var entity = result.Metadata.GetProperty("entity");
        await Assert.That(result.SelectedXid).IsEqualTo("/teams/team-a/schemas/metadata-a");
        await Assert.That(result.Metadata.GetProperty("kind").GetString()).IsEqualTo("resource");
        await AssertMetadataOnlyProjection(entity, includeOptionalMembers);
        await Assert.That(result.Document).IsNull();
        await Assert.That(result.ExternalDocument.ValueKind).IsEqualTo(JsonValueKind.Undefined);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities|Metadata");
        await Assert.That(session.Chunks.Count).IsEqualTo(0);
        await Assert.That(session.PropertyReadsWhileFileOpen)
            .Contains(("metadata:ResourceId", new NodeId("Metadata", 1)));
        await Assert.That(session.PropertyReadsWhileFileOpen)
            .Contains(("metadata:VersionId", new NodeId("Metadata", 1)));
        await Assert.That(session.FileReadHandles)
            .Contains((new NodeId("Metadata", 1), 7u));
        await Assert.That(session.FileClosedHandles)
            .Contains((new NodeId("Metadata", 1), 7u));
    }

    [Test]
    public async Task FederationMetadataReadUsesPropertiesAndFileFromPinnedRevision()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        session.AdvanceMetadataRevisionOnOpen = true;

        var result = await source.ReadAsync(new(FederationOperation.Entity,
            "/teams/team-a/schemas/metadata-a"));

        var entity = result.Metadata.GetProperty("entity");
        await Assert.That(entity.GetProperty("name").GetString()).IsEqualTo("Metadata resource");
        await Assert.That(entity.GetProperty("label").GetString()).IsEqualTo("metadata label");
        await Assert.That(session.JsonValues["metadata:Name"].Value.GetString())
            .IsEqualTo("Advanced metadata revision");
    }

    [Test]
    public async Task FederationMetadataVersionIsSelectedByObservedVersionId()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var observedVersionId = session.JsonValues["metadata:VersionId"].Value.GetString()!;

        var result = await source.ReadAsync(new(FederationOperation.Entity,
            $"/teams/team-a/schemas/metadata-a/versions/{Uri.EscapeDataString(observedVersionId)}"));

        var version = result.Metadata.GetProperty("entity");
        await Assert.That(result.SelectedXid)
            .IsEqualTo("/teams/team-a/schemas/metadata-a/versions/metadata-v1");
        await Assert.That(version.GetProperty("resourceid").GetString()).IsEqualTo("metadata-a");
        await Assert.That(version.GetProperty("versionid").GetString()).IsEqualTo(observedVersionId);
        await Assert.That(version.GetProperty("isdefault").GetBoolean()).IsTrue();
        await Assert.That(version.GetProperty("xid").GetString())
            .IsEqualTo("/teams/team-a/schemas/metadata-a/versions/metadata-v1");
        await Assert.That(result.Document).IsNull();
        await Assert.That(result.ExternalDocument.ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    public async Task FederationMetadataVersionDoesNotResolveUnobservedVersionId()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var readsBefore = session.FileReads;
        var openedBefore = session.Opened;
        var closedBefore = session.Closed;

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity,
                "/teams/team-a/schemas/metadata-a/versions/not-observed")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(session.FileReads).IsEqualTo(readsBefore);
        await Assert.That(session.Opened).IsEqualTo(openedBefore + 1);
        await Assert.That(session.Closed).IsEqualTo(closedBefore + 1);
    }

    [Test]
    public async Task FederationMetadataReadRejectsMissingResourceIdProperty()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var metadataResource = new NodeId("metadata-resource-a", 1);
        session.BrowsePages[metadataResource] = new([new(session.BrowsePages[metadataResource].Peek().References
            .Where(reference => reference.BrowseName.Name != "ResourceId").ToArray())]);
        var readsBefore = session.FileReads;
        var openedBefore = session.Opened;
        var closedBefore = session.Closed;

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity,
                "/teams/team-a/schemas/metadata-a")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.FileReads).IsEqualTo(readsBefore);
        await Assert.That(session.Opened).IsEqualTo(openedBefore + 1);
        await Assert.That(session.Closed).IsEqualTo(closedBefore + 1);
    }

    [Test]
    [Arguments("\"/teams/team-a/schemas/other\"", BuiltInType.String)]
    [Arguments("42", BuiltInType.UInt32)]
    public async Task FederationMetadataReadRejectsInvalidXidProperty(string json, BuiltInType builtInType)
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        session.JsonValues["metadata:Xid"] = Value(json, builtInType);
        var readsBefore = session.FileReads;
        var openedBefore = session.Opened;
        var closedBefore = session.Closed;

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity,
                "/teams/team-a/schemas/metadata-a")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.FileReads).IsEqualTo(readsBefore);
        await Assert.That(session.Opened).IsEqualTo(openedBefore + 1);
        await Assert.That(session.Closed).IsEqualTo(closedBefore + 1);
    }

    [Test]
    [Arguments("\"different\"", BuiltInType.String)]
    [Arguments("42", BuiltInType.UInt32)]
    public async Task FederationMetadataCollectionRejectsInvalidResourceIdProperty(
        string json, BuiltInType builtInType)
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        session.JsonValues["metadata:ResourceId"] = Value(json, builtInType);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Collection,
                "/teams/team-a/schemas")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
    }

    [Test]
    public async Task FederationMetadataCollectionRejectsMissingResourceIdProperty()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var metadataResource = new NodeId("metadata-resource-a", 1);
        session.BrowsePages[metadataResource] = new([new(session.BrowsePages[metadataResource].Peek().References
            .Where(reference => reference.BrowseName.Name != "ResourceId").ToArray())]);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Collection,
                "/teams/team-a/schemas")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
    }

    [Test]
    public async Task FederationMetadataCollectionRejectsDuplicateResourceIds()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var groupReferences = session.BrowsePages[GroupNode].Peek().References;
        session.BrowsePages[GroupNode] = new([new([
            .. groupReferences,
            TypedObjectRef("metadata-a", new NodeId("metadata-resource-duplicate", 1),
                new NodeId("MetadataResourceType", 1))
        ])]);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Collection,
                "/teams/team-a/schemas")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.Ambiguous);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
    }

    [Test]
    public async Task FederationMetadataCollectionRejectsEmptyResourceId()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var groupReferences = session.BrowsePages[GroupNode].Peek().References;
        var resource = groupReferences.Single(reference => reference.BrowseName.Name == "metadata-a");
        session.BrowsePages[GroupNode] = new([new([
            .. groupReferences.Where(reference => reference.BrowseName.Name != "metadata-a"),
            new(resource.Target, new QualifiedName("", 1), resource.NodeClass, resource.TypeDefinition)
        ])]);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Collection,
                "/teams/team-a/schemas")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
    }

    [Test]
    public async Task FederationMetadataVersionDoesNotResolveWhenVersionIdIsAbsent()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var metadataResource = new NodeId("metadata-resource-a", 1);
        session.BrowsePages[metadataResource] = new([new(session.BrowsePages[metadataResource].Peek().References
            .Where(reference => reference.BrowseName.Name != "VersionId").ToArray())]);
        var readsBefore = session.FileReads;
        var openedBefore = session.Opened;
        var closedBefore = session.Closed;

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity,
                "/teams/team-a/schemas/metadata-a/versions/not-observed")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(session.FileReads).IsEqualTo(readsBefore);
        await Assert.That(session.Opened).IsEqualTo(openedBefore + 1);
        await Assert.That(session.Closed).IsEqualTo(closedBefore + 1);
    }

    [Test]
    [Arguments("\"\"", BuiltInType.String)]
    [Arguments("42", BuiltInType.UInt32)]
    public async Task FederationMetadataReadRejectsMalformedObservedVersionId(string json, BuiltInType builtInType)
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        session.JsonValues["metadata:VersionId"] = Value(json, builtInType);
        var readsBefore = session.FileReads;
        var openedBefore = session.Opened;
        var closedBefore = session.Closed;

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity,
                "/teams/team-a/schemas/metadata-a")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.FileReads).IsEqualTo(readsBefore);
        await Assert.That(session.Opened).IsEqualTo(openedBefore + 1);
        await Assert.That(session.Closed).IsEqualTo(closedBefore + 1);
    }

    [Test]
    [Arguments("\"different\"", BuiltInType.String)]
    [Arguments("42", BuiltInType.UInt32)]
    public async Task FederationMetadataReadRejectsInvalidResourceIdProperty(string json, BuiltInType builtInType)
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        session.JsonValues["metadata:ResourceId"] = Value(json, builtInType);
        var readsBefore = session.FileReads;
        var openedBefore = session.Opened;
        var closedBefore = session.Closed;

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity,
                "/teams/team-a/schemas/metadata-a")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(session.FileReads).IsEqualTo(readsBefore);
        await Assert.That(session.Opened).IsEqualTo(openedBefore + 1);
        await Assert.That(session.Closed).IsEqualTo(closedBefore + 1);
    }

    [Test]
    public async Task FederationMetadataProjectionDoesNotInventAnUnobservedVersionId()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var metadataResource = new NodeId("metadata-resource-a", 1);
        session.BrowsePages[metadataResource] = new([new(session.BrowsePages[metadataResource].Peek().References
            .Where(reference => reference.BrowseName.Name != "VersionId").ToArray())]);

        var result = await source.ReadAsync(new(FederationOperation.Entity,
            "/teams/team-a/schemas/metadata-a"));

        await Assert.That(result.Metadata.GetProperty("entity").TryGetProperty("versionid", out _)).IsFalse();
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities|Metadata");
    }

    [Test]
    [Arguments(FederationOperation.Collection)]
    [Arguments(FederationOperation.Entity)]
    public async Task FederationMetadataMappingRequiresMaxVersionsOne(FederationOperation operation)
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false, maxVersions: 2);
        var target = operation == FederationOperation.Collection
            ? "/teams/team-a/schemas" : "/teams/team-a/schemas/metadata-a";

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(operation, target)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
    }

    [Test]
    [Arguments(FederationOperation.Collection)]
    [Arguments(FederationOperation.Entity)]
    public async Task FederationMetadataMappingRequiresHasDocumentFalse(FederationOperation operation)
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false, hasDocument: true);
        var target = operation == FederationOperation.Collection
            ? "/teams/team-a/schemas" : "/teams/team-a/schemas/metadata-a";

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(operation, target)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
    }

    [Test]
    public async Task FederationMetadataHasNoMaterializedVersionsCollection()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Collection,
                "/teams/team-a/schemas/metadata-a/versions")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
    }

    [Test]
    public async Task FederationMetadataResourceRejectsNonFileMetadataComponent()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var metadataResource = new NodeId("metadata-resource-a", 1);
        session.BrowsePages[metadataResource] = new([new([
            .. session.BrowsePages[metadataResource].Peek().References
                .Where(reference => reference.BrowseName.Name != "Metadata"),
            new(new NodeId("Metadata", 1), new QualifiedName("Metadata", 1), NodeClass.Object,
                new NodeId("FolderType", 1))
        ])]);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity,
                "/teams/team-a/schemas/metadata-a")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
    }

    [Test]
    public async Task FederationMetadataResourceRequiresMetadataFileComponent()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var metadataResource = new NodeId("metadata-resource-a", 1);
        session.BrowsePages[metadataResource] = new([new(session.BrowsePages[metadataResource].Peek().References
            .Where(reference => reference.BrowseName.Name != "Metadata").ToArray())]);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity,
                "/teams/team-a/schemas/metadata-a")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
    }

    [Test]
    public async Task FederationMetadataReadRejectsNonObjectJson()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var openedBefore = session.Opened;
        var closedBefore = session.Closed;
        session.Chunks[0] = System.Text.Encoding.UTF8.GetBytes("[]");

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity,
                "/teams/team-a/schemas/metadata-a")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await AssertMetadataFileWasRead(session, openedBefore, closedBefore);
    }

    [Test]
    public async Task FederationMetadataReadRejectsIdentityContradictingMetadataJson()
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var openedBefore = session.Opened;
        var closedBefore = session.Closed;
        session.Chunks[0] = System.Text.Encoding.UTF8.GetBytes("""{"resourceid":"different"}""");

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Entity,
                "/teams/team-a/schemas/metadata-a")));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await AssertMetadataFileWasRead(session, openedBefore, closedBefore);
    }

    private static async ValueTask<(FakeSession Session, OpcUaRegistryReadSource Source)>
        OpenMetadataOnlySource(bool includeOptionalMembers, int maxVersions = 1, bool hasDocument = false)
    {
        const string modelTemplate =
            """
            {"groups":{"teams":{"singular":"team","resources":{"schemas":{
              "singular":"schema","hasdocument":false,"maxversions":1,
              "attributes":{
                "label":{"type":"string"},
                "enabled":{"type":"boolean"},
                "rank":{"type":"integer"},
                "nullable":{"type":"any"},
                "empty":{"type":"array","item":{"type":"any"}},
                "ordered":{"type":"array","item":{"type":"any"}},
                "optionaltext":{"type":"string"},
                "optionalnull":{"type":"any"}
              }
            }}}}}
            """;
        var model = modelTemplate.Replace("\"maxversions\":1",
                $"\"maxversions\":{maxVersions}", StringComparison.Ordinal)
            .Replace("\"hasdocument\":false", $"\"hasdocument\":{hasDocument.ToString().ToLowerInvariant()}",
                StringComparison.Ordinal);
        const string metadataWithOptional =
            """{"label":"metadata label","enabled":false,"rank":0,"nullable":null,"empty":[],"ordered":["second","first"],"optionaltext":"present","optionalnull":null,"meta":{"epoch":0,"readonly":false,"name":"Metadata resource meta"}}""";
        const string metadataWithoutOptional =
            """{"label":"metadata label","enabled":false,"rank":0,"nullable":null,"empty":[],"ordered":["second","first"],"meta":{"epoch":0,"readonly":false,"name":"Metadata resource meta"}}""";

        var session = SourceSession(model);
        var metadataResourceType = new NodeId("MetadataResourceType", 1);
        var metadataResourceNode = new NodeId("metadata-resource-a", 1);

        session.JsonValues["group:GroupId"] = Value("\"team-a\"", BuiltInType.String);
        session.JsonValues["group:Xid"] = Value("\"/teams/team-a\"", BuiltInType.String);
        session.JsonValues["group:Epoch"] = Value("2", BuiltInType.UInt32);
        session.JsonValues["group:CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["group:ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["group:Name"] = Value("\"Team A\"", BuiltInType.String);
        session.BrowsePages[GroupNode] = new([new([
            .. GroupPropertiesPage().References,
            TypedObjectRef("metadata-a", metadataResourceNode, metadataResourceType)
        ])]);
        session.BrowsePages[metadataResourceNode] = new([new([
            VariableRef("ResourceId", "metadata"), VariableRef("VersionId", "metadata"),
            VariableRef("Xid", "metadata"), VariableRef("Epoch", "metadata"),
            VariableRef("CreatedAt", "metadata"), VariableRef("ModifiedAt", "metadata"),
            VariableRef("Name", "metadata"),
            FileRef("Metadata")
        ])]);
        session.JsonValues["metadata:ResourceId"] = Value("\"metadata-a\"", BuiltInType.String);
        session.JsonValues["metadata:VersionId"] = Value("\"metadata-v1\"", BuiltInType.String);
        session.JsonValues["metadata:Xid"] =
            Value("\"/teams/team-a/schemas/metadata-a\"", BuiltInType.String);
        session.JsonValues["metadata:Epoch"] = Value("0", BuiltInType.UInt32);
        session.JsonValues["metadata:CreatedAt"] =
            Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["metadata:ModifiedAt"] =
            Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["metadata:Name"] = Value("\"Metadata resource\"", BuiltInType.String);
        var source = await OpenSource(session, new(
            new Dictionary<string, NodeId>(StringComparer.Ordinal)
            {
                ["teams"] = new("GroupType", 1)
            },
            new Dictionary<(string GroupCollection, string ResourceCollection), OpcUaResourceTypeBinding>
            {
                [("teams", "schemas")] = OpcUaResourceTypeBinding.ForMetadataOnly(metadataResourceType)
            },
            AttributesType));
        session.Chunks.Add(System.Text.Encoding.UTF8.GetBytes(includeOptionalMembers
            ? metadataWithOptional : metadataWithoutOptional));
        session.Chunks.Add(Array.Empty<byte>());
        return (session, source);
    }

    private static async Task AssertMetadataOnlyProjection(JsonElement entity, bool includeOptionalMembers)
    {
        await Assert.That(entity.GetProperty("resourceid").GetString()).IsEqualTo("metadata-a");
        await Assert.That(entity.GetProperty("name").GetString()).IsEqualTo("Metadata resource");
        await Assert.That(entity.GetProperty("epoch").GetUInt32()).IsEqualTo(0u);
        await Assert.That(entity.GetProperty("label").GetString()).IsEqualTo("metadata label");
        await Assert.That(entity.GetProperty("enabled").ValueKind).IsEqualTo(JsonValueKind.False);
        await Assert.That(entity.GetProperty("rank").GetInt32()).IsEqualTo(0);
        await Assert.That(entity.GetProperty("nullable").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(entity.GetProperty("empty").ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(entity.GetProperty("empty").GetArrayLength()).IsEqualTo(0);
        var ordered = entity.GetProperty("ordered");
        await Assert.That(ordered.GetArrayLength()).IsEqualTo(2);
        await Assert.That(ordered[0].GetString()).IsEqualTo("second");
        await Assert.That(ordered[1].GetString()).IsEqualTo("first");
        await Assert.That(entity.TryGetProperty("optionaltext", out var optionalText))
            .IsEqualTo(includeOptionalMembers);
        await Assert.That(entity.TryGetProperty("optionalnull", out var optionalNull))
            .IsEqualTo(includeOptionalMembers);
        if (includeOptionalMembers)
        {
            await Assert.That(optionalText.GetString()).IsEqualTo("present");
            await Assert.That(optionalNull.ValueKind).IsEqualTo(JsonValueKind.Null);
        }

        await Assert.That(entity.TryGetProperty("versions", out _)).IsFalse();
        await Assert.That(entity.TryGetProperty("versionsurl", out _)).IsFalse();
        var meta = entity.GetProperty("meta");
        await Assert.That(meta.TryGetProperty("defaultversionurl", out _)).IsFalse();
        await Assert.That(meta.GetProperty("epoch").GetUInt32()).IsEqualTo(0u);
        await Assert.That(meta.GetProperty("readonly").GetBoolean()).IsFalse();
        await Assert.That(meta.GetProperty("name").GetString()).IsEqualTo("Metadata resource meta");
    }

    [Test]
    [Arguments(FederationOperation.Collection, false)]
    [Arguments(FederationOperation.Collection, true)]
    [Arguments(FederationOperation.Entity, false)]
    [Arguments(FederationOperation.Entity, true)]
    public async Task FederationMetadataReadRejectsMalformedUtf8(
        FederationOperation operation, bool incompleteMultibyteSequence)
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var openedBefore = session.Opened;
        var closedBefore = session.Closed;
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
        // Replace only the queued metadata payload, after initialization has consumed Model/Capabilities.
        // Keep the invalid bytes raw: decoding them here could silently insert replacement characters.
        byte[] malformed = incompleteMultibyteSequence ? [0xE2] : [0xC3, 0x28];
        session.Chunks[0] = [
            .. System.Text.Encoding.UTF8.GetBytes("{\"label\":\""), .. malformed,
            .. System.Text.Encoding.UTF8.GetBytes("\"}")
        ];
        var target = operation == FederationOperation.Collection
            ? "/teams/team-a/schemas" : "/teams/team-a/schemas/metadata-a";

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(operation, target)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await AssertMetadataFileWasRead(session, openedBefore, closedBefore);
    }

    [Test]
    [Arguments(FederationOperation.Collection, false)]
    [Arguments(FederationOperation.Collection, true)]
    [Arguments(FederationOperation.Entity, false)]
    [Arguments(FederationOperation.Entity, true)]
    public async Task FederationMetadataReadRejectsDuplicateJsonNames(
        FederationOperation operation, bool escapedEquivalentName)
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var openedBefore = session.Opened;
        var closedBefore = session.Closed;
        await Assert.That(NodeIdTrace(session.OpenedFiles)).IsEqualTo("Model|Capabilities");
        // Raw JSON preserves both conflicting members and the escape in the equivalent property name.
        session.Chunks[0] = System.Text.Encoding.UTF8.GetBytes(escapedEquivalentName
            ? """{"label":"first","\u006cabel":"second"}"""
            : """{"label":"first","label":"second"}""");
        var target = operation == FederationOperation.Collection
            ? "/teams/team-a/schemas" : "/teams/team-a/schemas/metadata-a";

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(operation, target)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await AssertMetadataFileWasRead(session, openedBefore, closedBefore);
    }

    private static async Task AssertMetadataFileWasRead(
        FakeSession session, int openedBefore, int closedBefore)
    {
        await Assert.That(session.OpenedFiles.Skip(openedBefore).ToArray())
            .IsEquivalentTo(new NodeId[] { new("Metadata", 1) });
        await Assert.That(session.Opened).IsEqualTo(openedBefore + 1);
        await Assert.That(session.OpenMode).IsEqualTo((byte)1);
        await Assert.That(session.ClosedFiles.Skip(closedBefore).ToArray())
            .IsEquivalentTo(new NodeId[] { new("Metadata", 1) });
        await Assert.That(session.Closed).IsEqualTo(closedBefore + 1);
        await Assert.That(session.Chunks.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FederationMetadataDocumentReadRejectsBeforeFileTransfer(bool exactVersion)
    {
        var (session, source) = await OpenMetadataOnlySource(includeOptionalMembers: false);
        var metadataFile = new NodeId("Metadata", 1);
        var observedVersionId = session.JsonValues["metadata:VersionId"].Value.GetString()!;
        await Assert.That(observedVersionId).IsEqualTo("metadata-v1");
        var target = exactVersion
            ? $"/teams/team-a/schemas/metadata-a/versions/{Uri.EscapeDataString(observedVersionId)}"
            : "/teams/team-a/schemas/metadata-a";
        var domainDocumentFile = new NodeId("metadata-resource-a", 1);
        var openedFilesBefore = session.OpenedFiles.ToArray();
        var openedBefore = session.Opened;

        // Keep nonempty bytes queued so an accidental FileType read is observable,
        // rather than making an empty queue the reason the operation fails.
        byte[] sentinelBytes = [0x53, 0x45, 0x4e, 0x54, 0x49, 0x4e, 0x45, 0x4c];
        session.Chunks.Clear();
        session.Chunks.AddRange([sentinelBytes, Array.Empty<byte>()]);
        var chunksBefore = session.Chunks.Select(chunk => chunk.ToArray()).ToArray();

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await source.ReadAsync(new(FederationOperation.Document,
                target)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.UnsupportedOperation);
        await Assert.That(session.Opened).IsEqualTo(openedBefore);
        await Assert.That(session.OpenedFiles.ToArray()).IsEquivalentTo(openedFilesBefore);
        var openedAfter = session.OpenedFiles.Skip(openedFilesBefore.Length).ToArray();
        await Assert.That(openedAfter).IsEquivalentTo(Array.Empty<NodeId>());
        await Assert.That(openedAfter.Contains(metadataFile)).IsFalse();
        await Assert.That(openedAfter.Contains(domainDocumentFile)).IsFalse();

        await Assert.That(session.Chunks.Count).IsEqualTo(chunksBefore.Length);
        for (var index = 0; index < chunksBefore.Length; index++)
        {
            await Assert.That(session.Chunks[index]).IsEquivalentTo(chunksBefore[index]);
        }
    }

    [Test]
    public async Task VerifyRegistryRootAsyncReturnsTheExactRegistryNodeId()
    {
        var root = new NodeId("Registry", 1);
        var reader = new OpcUaRegistryReader(new FakeSession());

        var verified = await reader.VerifyRegistryRootAsync(root);

        await Assert.That(verified).IsEqualTo(root);
        await Assert.That(verified.NamespaceIndex).IsEqualTo((ushort)1);
        await Assert.That(verified.IdentifierAsString).IsEqualTo("Registry");
    }

    [Test]
    public async Task VerifyRegistryRootAsyncRejectsNullOrNonRegistryRoot()
    {
        var nullRootError = await Assert.ThrowsAsync<FederationException>(
            async () => await new OpcUaRegistryReader(new FakeSession()).VerifyRegistryRootAsync(NodeId.Null));
        var wrongRoot = new NodeId("NotARegistry", 1);
        var wrongTypeError = await Assert.ThrowsAsync<FederationException>(
            async () => await new OpcUaRegistryReader(new FakeSession { IsRegistry = false })
                .VerifyRegistryRootAsync(wrongRoot));

        await Assert.That(nullRootError!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(wrongTypeError!.Code).IsEqualTo(FederationErrorCode.NotFound);
    }

    [Test]
    public async Task VerifyRootAsyncResolvesAdvertisedRootUsingSessionNamespaceArray()
    {
        var target = OpcUaFederationTargetTests.Target(
            """{"registryroot":"nsu=urn%3Axregistry;s=Registry"}""");
        var reader = new OpcUaRegistryReader(new FakeSession());

        var root = await reader.VerifyRootAsync(target);

        await Assert.That(root.IdentifierAsString).IsEqualTo("Registry");
        await Assert.That(root.NamespaceIndex).IsEqualTo((ushort)1);
    }

    [Test]
    [Arguments("empty-server")]
    [Arguments("missing-namespace-entry")]
    [Arguments("different-uris")]
    public async Task VerifyRootAsyncRejectsInvalidServerAndNamespaceArrayIdentity(string mismatch)
    {
        var target = OpcUaFederationTargetTests.Target(
            """{"registryroot":"nsu=urn%3Axregistry;s=Registry"}""");
        var session = mismatch switch
        {
            "empty-server" => new FakeSession { ServerArray = [] },
            "missing-namespace-entry" => new FakeSession
            {
                NamespaceArray = ["http://opcfoundation.org/UA/"]
            },
            "different-uris" => new FakeSession { ServerArray = ["urn:server:foreign"] },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        var reader = new OpcUaRegistryReader(session);

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.VerifyRootAsync(target));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
    }

    [Test]
    public async Task ResolveResourceNodeAsyncUsesMappedResourceTypeAndReturnsExactNodeId()
    {
        var session = new FakeSession();
        var reader = await ModelReader(session);
        ConfigureResourceAddressSpace(session);
        session.BrowsePages[GroupNode] = new([
            new([TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)])
        ]);
        var browsedBefore = session.BrowsedNodes.Count;

        var resolved = await reader.ResolveResourceNodeAsync(GroupNode, "/teams/team-a", "schemas",
            "schema-a", ResourceType, resourceVersionsType: ResourceVersionsType);

        await Assert.That(resolved).IsEqualTo(LogicalResourceNode);
        await Assert.That(session.BrowsedNodes.Skip(browsedBefore).First()).IsEqualTo(GroupNode);
        await Assert.That(session.BrowsedNodes[^1]).IsEqualTo(LogicalResourceNode);
    }

    [Test]
    public async Task ResolveResourceNodeAsyncUsesMappedVersionTypeAndReturnsExactVersionNodeId()
    {
        var session = new FakeSession();
        var reader = await ModelReader(session);
        ConfigureResourceAddressSpace(session);
        session.BrowsePages[GroupNode] = new([
            new([TypedObjectRef("schema-a", LogicalResourceNode, ResourceType)])
        ]);
        var browsedBefore = session.BrowsedNodes.Count;

        var resolved = await reader.ResolveResourceNodeAsync(GroupNode, "/teams/team-a", "schemas",
            "schema-a", ResourceType, "v1", ResourceVersionsType);

        await Assert.That(resolved).IsEqualTo(DefaultVersionNode);
        await Assert.That(session.BrowsedNodes.Skip(browsedBefore)).IsEquivalentTo(
            new[] { GroupNode, LogicalResourceNode, VersionsNode, DefaultVersionNode });
        await Assert.That(session.JsonValues["version:VersionId"].Value.GetString()).IsEqualTo("v1");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ResolveResourceNodeAsyncRejectsWrongChildKindOrTypeMapping(bool wrongType)
    {
        var session = new FakeSession();
        var reader = await ModelReader(session);
        var child = wrongType
            ? TypedObjectRef("schema-a", LogicalResourceNode, new NodeId("OtherResourceType", 1))
            : VariableRef("schema-a");
        session.BrowsePages[GroupNode] = new([new([child])]);
        var browsedBefore = session.BrowsedNodes.Count;

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ResolveResourceNodeAsync(GroupNode, "/teams/team-a", "schemas",
                "schema-a", ResourceType));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(session.BrowsedNodes.Skip(browsedBefore)).IsEquivalentTo(new[] { GroupNode });
    }

    [Test]
    public async Task ResolveMetadataResourceNodeAsyncResolvesMetadataOnlyResourceByExactNodeId()
    {
        const string metadataModel =
            """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","hasdocument":false,"maxversions":1}}}}}""";
        var metadataType = new NodeId("MetadataResourceType", 1);
        var metadataNode = new NodeId("metadata-resource-a", 1);
        var session = new FakeSession();
        session.Chunks.AddRange([
            System.Text.Encoding.UTF8.GetBytes("{}"), Array.Empty<byte>()
        ]);
        var reader = await ModelReader(session, metadataModel);
        session.BrowsePages[GroupNode] = new([
            new([TypedObjectRef("metadata-a", metadataNode, metadataType)])
        ]);
        session.BrowsePages[metadataNode] = new([
            new([
                VariableRef("ResourceId", "metadata"), VariableRef("Xid", "metadata"),
                VariableRef("Name", "metadata"), FileRef("Metadata")
            ])
        ]);
        session.JsonValues["metadata:ResourceId"] = Value("\"metadata-a\"", BuiltInType.String);
        session.JsonValues["metadata:Xid"] =
            Value("\"/teams/team-a/schemas/metadata-a\"", BuiltInType.String);
        session.JsonValues["metadata:Name"] = Value("\"Metadata resource\"", BuiltInType.String);
        var openedBefore = session.OpenedFiles.Count;
        var closedBefore = session.Closed;

        var resolved = await reader.ResolveMetadataResourceNodeAsync(GroupNode, "/teams/team-a",
            "schemas", "metadata-a", metadataType);

        await Assert.That(resolved).IsEqualTo(metadataNode);
        await Assert.That(session.OpenedFiles.Skip(openedBefore))
            .IsEquivalentTo(new[] { new NodeId("Metadata", 1) });
        await Assert.That(session.Closed).IsEqualTo(closedBefore + 1);
    }

    [Test]
    public async Task OpcUaRegistryTypeMapResolvesExactTargetGroupResourceAndVersionTypes()
    {
        var groupType = new NodeId("TargetGroupType", 1);
        var resourceType = new NodeId("TargetResourceType", 1);
        var versionsType = new NodeId("TargetResourceVersionsType", 1);
        var attributesType = new NodeId("TargetAttributesType", 1);
        var binding = new OpcUaResourceTypeBinding(resourceType, versionsType);
        var map = new OpcUaRegistryTypeMap(
            new Dictionary<string, NodeId>(StringComparer.Ordinal) { ["teams"] = groupType },
            new Dictionary<(string GroupCollection, string ResourceCollection), OpcUaResourceTypeBinding>
            {
                [("teams", "schemas")] = binding
            },
            attributesType);

        await Assert.That(map.GroupTypes["teams"]).IsEqualTo(groupType);
        await Assert.That(map.GroupTypes.ContainsKey("Teams")).IsFalse();
        await Assert.That(map.ResourceTypes.ContainsKey(("teams", "schemas"))).IsTrue();
        var resolvedBinding = map.ResourceTypes[("teams", "schemas")];
        await Assert.That(resolvedBinding.ResourceType).IsEqualTo(resourceType);
        await Assert.That(resolvedBinding.ResourceVersionsType).IsEqualTo(versionsType);
        await Assert.That(resolvedBinding.IsMetadataOnly).IsFalse();
        await Assert.That(map.ResourceTypes.ContainsKey(("Teams", "schemas"))).IsFalse();
        await Assert.That(map.AttributesType).IsEqualTo(attributesType);
    }

    [Test]
    public async Task OpcUaResourceTypeBindingForMetadataOnlyHasNoVersionsType()
    {
        var metadataType = new NodeId("TargetMetadataResourceType", 1);

        var binding = OpcUaResourceTypeBinding.ForMetadataOnly(metadataType);

        await Assert.That(binding.ResourceType).IsEqualTo(metadataType);
        await Assert.That(binding.IsMetadataOnly).IsTrue();
        await Assert.That(binding.ResourceVersionsType.IsNull).IsTrue();
    }

    [Test]
    public async Task OpcUaRegistryTypeMapAllowsAnEmptyMappingWithoutGuessingTypes()
    {
        var attributesType = new NodeId("TargetAttributesType", 1);
        var map = new OpcUaRegistryTypeMap(
            new Dictionary<string, NodeId>(StringComparer.Ordinal),
            new Dictionary<(string GroupCollection, string ResourceCollection), OpcUaResourceTypeBinding>(),
            attributesType);

        await Assert.That(map.GroupTypes.Count).IsEqualTo(0);
        await Assert.That(map.ResourceTypes.Count).IsEqualTo(0);
        await Assert.That(map.GroupTypes.TryGetValue("teams", out _)).IsFalse();
        await Assert.That(map.ResourceTypes.TryGetValue(("teams", "schemas"), out _)).IsFalse();
        await Assert.That(map.AttributesType).IsEqualTo(attributesType);
    }

    [Test]
    public async Task ResolveGroupNodeAsyncResolvesExactGroupUsingMappedGroupType()
    {
        var groupType = new NodeId("TargetGroupType", 1);
        var session = new FakeSession();
        var reader = await ModelReader(session);
        ConfigureResourceAddressSpace(session);
        session.BrowsePages[File] = new([
            new([TypedObjectRef("team-a", GroupNode, groupType)])
        ]);
        var browsedBefore = session.BrowsedNodes.Count;

        var resolved = await reader.ResolveGroupNodeAsync(File, "teams", "team-a", groupType);

        await Assert.That(resolved).IsEqualTo(GroupNode);
        await Assert.That(session.BrowsedNodes.Skip(browsedBefore)).IsEquivalentTo(new[] { File, GroupNode });
        await Assert.That(session.JsonValues["group:GroupId"].Value.GetString()).IsEqualTo("team-a");
        await Assert.That(session.JsonValues["group:Xid"].Value.GetString()).IsEqualTo("/teams/team-a");
    }

    [Test]
    public async Task ResolveGroupNodeAsyncRejectsWrongMappedGroupType()
    {
        var session = new FakeSession();
        var reader = await ModelReader(session);
        session.BrowsePages[File] = new([
            new([TypedObjectRef("team-a", GroupNode, new NodeId("OtherGroupType", 1))])
        ]);
        var browsedBefore = session.BrowsedNodes.Count;

        var error = await Assert.ThrowsAsync<FederationException>(
            async () => await reader.ResolveGroupNodeAsync(File, "teams", "team-a",
                new NodeId("TargetGroupType", 1)));

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(session.BrowsedNodes.Skip(browsedBefore)).IsEquivalentTo(new[] { File });
    }

    [Test]
    public async Task ResolveExternalTargetAsyncPassesExactIdentityAndRetainsLeaseUntilResultDisposal()
    {
        var fixture = CreateResolverFixture();
        OpcUaExternalNodeId? receivedIdentity = null;
        OpcUaTrustedProviderResolver resolver = (identity, _) =>
        {
            receivedIdentity = identity;
            return ValueTask.FromResult<IOpcUaTrustedProviderLease>(fixture.Lease);
        };

        var result = await OpcUaRegistryReadSource.ResolveExternalTargetAsync(
            fixture.Identity, resolver, version => version == "1.0-rc4");

        await Assert.That(receivedIdentity).IsNotNull();
        await Assert.That(receivedIdentity!.ServerApplicationUri).IsEqualTo(TargetServerApplicationUri);
        await Assert.That(receivedIdentity.NamespaceUri).IsEqualTo(TargetNamespaceUri);
        await Assert.That(receivedIdentity.IdentifierType).IsEqualTo(IdType.String);
        await Assert.That(receivedIdentity.Identifier).IsEqualTo(LogicalResourceNode.IdentifierAsString);
        await Assert.That(result.ReadSource.Context).IsEqualTo(fixture.Lease.Context);
        await Assert.That(result.SpecVersion).IsEqualTo("1.0-rc4");
        await Assert.That(result.Xid).IsEqualTo("/teams/team-a/schemas/schema-a");
        await Assert.That(result.Node).IsEqualTo(LogicalResourceNode);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(0);

        await result.DisposeAsync();
        await result.DisposeAsync();

        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsServerNamespaceArrayMismatchAndDisposesLease()
    {
        var fixture = CreateResolverFixture(sessionNamespaceArrayUri: "urn:namespace:foreign");

        var error = await ResolveFailureAsync(fixture);

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncResolvesVersionIdentityToExactTargetNodeId()
    {
        var fixture = CreateResolverFixture(
            "/teams/team-a/schemas/schema-a/versions/v1");

        var result = await OpcUaRegistryReadSource.ResolveExternalTargetAsync(
            fixture.Identity, ReturnLease(fixture.Lease), _ => true);

        await Assert.That(result.Node).IsEqualTo(DefaultVersionNode);
        await Assert.That(result.Xid).IsEqualTo("/teams/team-a/schemas/schema-a/versions/v1");
        await Assert.That(result.ReadSource.SpecVersion).IsEqualTo("1.0-rc4");
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(0);
        await result.DisposeAsync();
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsInvalidSpecVersionAndDisposesLease()
    {
        var fixture = CreateResolverFixture(sessionSpecVersion: "malformed-version");

        var error = await ResolveFailureAsync(fixture, version => version == "1.0-rc4");

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.UnsupportedVersion);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsLeaseSpecVersionMismatchAndDisposesLease()
    {
        var fixture = CreateResolverFixture(leaseSpecVersion: "1.0");

        var error = await ResolveFailureAsync(fixture, version => version == "1.0-rc4");

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsSpecVersionChangeBeforeReadingModel()
    {
        var fixture = CreateResolverFixture(registryPropertySpecVersion: "1.0-rc5");

        var error = await ResolveFailureAsync(fixture, version => version == "1.0-rc4");

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
        await Assert.That(fixture.Session.OpenedFiles).IsEquivalentTo(Array.Empty<NodeId>());
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsNullRegistryRootAndDisposesLease()
    {
        var fixture = CreateResolverFixture(registryRoot: NodeId.Null);

        var error = await ResolveFailureAsync(fixture);

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.Unavailable);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsWrongRegistryRootAndDisposesLease()
    {
        var fixture = CreateResolverFixture(isRegistry: false);

        var error = await ResolveFailureAsync(fixture);

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsServerUriAvailableOnlyInAnotherContext()
    {
        var fixture = CreateResolverFixture(leaseServerApplicationUri: "urn:server:foreign");

        var error = await ResolveFailureAsync(fixture);

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsServerArrayMismatchAndDisposesLease()
    {
        var fixture = CreateResolverFixture(sessionServerArrayUri: "urn:server:foreign");

        var error = await ResolveFailureAsync(fixture);

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsNamespaceUriAvailableOnlyInAnotherContext()
    {
        var fixture = CreateResolverFixture(identityNamespaceUri: "urn:namespace:foreign");

        var error = await ResolveFailureAsync(fixture);

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    [Arguments("wrong-node")]
    [Arguments("wrong-kind")]
    [Arguments("wrong-type")]
    public async Task ResolveExternalTargetAsyncRejectsWrongNodeKindOrTypeMapping(string mismatch)
    {
        var fixture = mismatch switch
        {
            "wrong-node" => CreateResolverFixture(
                externalNode: new NodeId("unrelated-target-node", 1)),
            "wrong-kind" => CreateResolverFixture(
                "/teams/team-a/schemas/schema-a/versions/v1",
                externalNode: LogicalResourceNode),
            "wrong-type" => CreateResolverFixture(useWrongResourceType: true),
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        var error = await ResolveFailureAsync(fixture);

        await Assert.That(error.Code).IsEqualTo(
            mismatch == "wrong-type" ? FederationErrorCode.NotFound : FederationErrorCode.InvalidPackage);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncResolvesMetadataOnlyResourceWithoutVersions()
    {
        var fixture = CreateResolverFixture(metadataOnly: true);

        var result = await OpcUaRegistryReadSource.ResolveExternalTargetAsync(
            fixture.Identity, ReturnLease(fixture.Lease), _ => true);

        await Assert.That(result.Node).IsEqualTo(MetadataResourceNode);
        await Assert.That(result.Xid).IsEqualTo("/teams/team-a/schemas/metadata-a");
        await Assert.That(result.ReadSource.Model.Groups["teams"].Resources["schemas"].HasDocument)
            .IsFalse();
        await Assert.That(result.ReadSource.Model.Groups["teams"].Resources["schemas"]
                .MaxVersions.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .IsEqualTo("1");
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(0);
        await result.DisposeAsync();
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncRejectsMetadataOnlyXidWithVersionsAndDisposesLease()
    {
        var fixture = CreateResolverFixture(
            "/teams/team-a/schemas/metadata-a/versions/v1", metadataOnly: true);

        var error = await ResolveFailureAsync(fixture);

        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ResolveExternalTargetAsyncDisposesLeaseWhenTargetReadThrows()
    {
        var failure = new IOException("Synthetic target file read failure.");
        var fixture = CreateResolverFixture(readFailure: failure);

        var error = await Assert.ThrowsAsync<IOException>(async () =>
            await OpcUaRegistryReadSource.ResolveExternalTargetAsync(
                fixture.Identity, ReturnLease(fixture.Lease), _ => true));

        await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
    }

    private const string TargetServerApplicationUri = "urn:trusted:target-server";
    private const string TargetNamespaceUri = TargetServerApplicationUri;
    private const string MetadataModel =
        """{"groups":{"teams":{"singular":"team","resources":{"schemas":{"singular":"schema","hasdocument":false,"maxversions":1}}}}}""";
    private static readonly NodeId MetadataResourceNode = new("metadata-resource-a", 1);
    private static readonly NodeId MetadataResourceType = new("TargetMetadataResourceType", 1);

    private sealed record ResolverFixture(
        FakeSession Session,
        TrackingResolverLease Lease,
        OpcUaExternalNodeId Identity);

    private sealed class TrackingResolverLease(
        OpcUaRegistryReader reader,
        NodeId registryRoot,
        string specVersion,
        NativeRegistryContext context,
        OpcUaRegistryTypeMap types,
        string serverApplicationUri) : IOpcUaTrustedProviderLease
    {
        public string ServerApplicationUri { get; } = serverApplicationUri;
        public OpcUaRegistryReader Reader { get; } = reader;
        public NodeId RegistryRoot { get; } = registryRoot;
        public string SpecVersion { get; } = specVersion;
        public NativeRegistryContext Context { get; } = context;
        public OpcUaRegistryTypeMap Types { get; } = types;
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return default;
        }
    }

    private static ResolverFixture CreateResolverFixture(
        string? targetXid = null,
        NodeId? externalNode = null,
        bool isRegistry = true,
        NodeId? registryRoot = null,
        string sessionSpecVersion = "1.0-rc4",
        string leaseSpecVersion = "1.0-rc4",
        string? leaseServerApplicationUri = null,
        string? sessionServerArrayUri = null,
        string? sessionNamespaceArrayUri = null,
        string identityNamespaceUri = TargetNamespaceUri,
        string registryPropertySpecVersion = "1.0-rc4",
        bool metadataOnly = false,
        bool useWrongResourceType = false,
        Exception? readFailure = null)
    {
        targetXid ??= metadataOnly
            ? "/teams/team-a/schemas/metadata-a"
            : "/teams/team-a/schemas/schema-a";
        var targetNode = metadataOnly
            ? MetadataResourceNode
            : targetXid.Contains("/versions/", StringComparison.Ordinal)
                ? DefaultVersionNode
                : LogicalResourceNode;
        var resolvedExternalNode = externalNode ?? targetNode;
        var model = metadataOnly ? MetadataModel : TestModel;
        var sessionServerApplicationUri = sessionServerArrayUri ?? TargetServerApplicationUri;
        var session = new FakeSession
        {
            IsRegistry = isRegistry,
            ReadFailure = readFailure,
            SpecVersion = sessionSpecVersion,
            ServerArray = [sessionServerApplicationUri],
            NamespaceArray =
            [
                "http://opcfoundation.org/UA/",
                sessionNamespaceArrayUri ?? sessionServerApplicationUri
            ]
        };
        session.Chunks.AddRange([
            System.Text.Encoding.UTF8.GetBytes(model), Array.Empty<byte>(),
            System.Text.Encoding.UTF8.GetBytes("""{"flags":["doc"]}"""), Array.Empty<byte>()
        ]);
        session.JsonValues["RegistryId"] = Value("\"registry-target\"", BuiltInType.String);
        session.JsonValues["SpecVersion"] = Value(
            System.Text.Json.JsonSerializer.Serialize(registryPropertySpecVersion), BuiltInType.String);
        session.JsonValues["Xid"] = Value("\"/\"", BuiltInType.String);
        session.JsonValues["Epoch"] = Value("1", BuiltInType.UInt32);
        session.JsonValues["CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.JsonValues["ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
        session.BrowsePages[File] = new([
            new([SpecVersionRef()]),
            new([
                VariableRef("RegistryId"), VariableRef("SpecVersion"), VariableRef("Xid"),
                VariableRef("Epoch"), VariableRef("CreatedAt"), VariableRef("ModifiedAt")
            ]),
            new([FileRef("Model")]),
            new([FileRef("Capabilities")]),
            new([TypedObjectRef("team-a", GroupNode, new NodeId("GroupType", 1))])
        ]);

        var resourceType = useWrongResourceType
            ? new NodeId("WrongTargetResourceType", 1)
            : ResourceType;
        var typeBinding = metadataOnly
            ? OpcUaResourceTypeBinding.ForMetadataOnly(MetadataResourceType)
            : new OpcUaResourceTypeBinding(resourceType, ResourceVersionsType);
        var types = new OpcUaRegistryTypeMap(
            new Dictionary<string, NodeId>(StringComparer.Ordinal)
            {
                ["teams"] = new("GroupType", 1)
            },
            new Dictionary<(string GroupCollection, string ResourceCollection), OpcUaResourceTypeBinding>
            {
                [("teams", "schemas")] = typeBinding
            },
            AttributesType);
        var registryReader = new OpcUaRegistryReader(session);

        if (metadataOnly)
        {
            session.Chunks.AddRange([
                System.Text.Encoding.UTF8.GetBytes("{}"), Array.Empty<byte>()
            ]);
            session.BrowsePages[GroupNode] = new([
                GroupPropertiesPage(),
                new([TypedObjectRef("metadata-a", MetadataResourceNode, MetadataResourceType)])
            ]);
            session.JsonValues["group:GroupId"] = Value("\"team-a\"", BuiltInType.String);
            session.JsonValues["group:Xid"] = Value("\"/teams/team-a\"", BuiltInType.String);
            session.JsonValues["group:Epoch"] = Value("2", BuiltInType.UInt32);
            session.JsonValues["group:CreatedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
            session.JsonValues["group:ModifiedAt"] = Value("\"2026-01-01T00:00:00Z\"", BuiltInType.DateTime);
            session.JsonValues["group:Name"] = Value("\"Team A\"", BuiltInType.String);
            session.BrowsePages[MetadataResourceNode] = new([
                new([
                    VariableRef("ResourceId", "metadata"), VariableRef("Xid", "metadata"),
                    VariableRef("Name", "metadata"), FileRef("Metadata")
                ])
            ]);
            session.JsonValues["metadata:ResourceId"] = Value("\"metadata-a\"", BuiltInType.String);
            session.JsonValues["metadata:Xid"] =
                Value("\"/teams/team-a/schemas/metadata-a\"", BuiltInType.String);
            session.JsonValues["metadata:Name"] = Value("\"Metadata A\"", BuiltInType.String);
            session.StringValues["metadata:Xid"] = targetXid;
        }
        else
        {
            ConfigureResourceAddressSpace(session);
            session.StringValues["logical:Xid"] = "/teams/team-a/schemas/schema-a";
            session.StringValues["version:Xid"] =
                "/teams/team-a/schemas/schema-a/versions/v1";
        }

        if (resolvedExternalNode == LogicalResourceNode)
        {
            session.StringValues["logical:Xid"] = targetXid;
        }
        else if (resolvedExternalNode == DefaultVersionNode)
        {
            session.StringValues["version:Xid"] = targetXid;
        }
        else if (resolvedExternalNode != MetadataResourceNode)
        {
            session.BrowsePages[resolvedExternalNode] = new([
                new([VariableRef("Xid", "external")])
            ]);
            session.StringValues["external:Xid"] = targetXid;
        }

        var identity = new OpcUaExternalNodeId(TargetServerApplicationUri, identityNamespaceUri,
            IdType.String, resolvedExternalNode.IdentifierAsString);
        var context = new NativeRegistryContext("opcua", "urn:trusted:target-context");
        var lease = new TrackingResolverLease(registryReader, registryRoot ?? File, leaseSpecVersion,
            context, types, leaseServerApplicationUri ?? TargetServerApplicationUri);
        return new(session, lease, identity);
    }

    private static OpcUaTrustedProviderResolver ReturnLease(TrackingResolverLease lease) =>
        (_, _) => ValueTask.FromResult<IOpcUaTrustedProviderLease>(lease);

    private static async Task<FederationException> ResolveFailureAsync(
        ResolverFixture fixture, Func<string, bool>? isSupportedSpecVersion = null)
    {
        var error = await Assert.ThrowsAsync<FederationException>(async () =>
            await OpcUaRegistryReadSource.ResolveExternalTargetAsync(
                fixture.Identity, ReturnLease(fixture.Lease), isSupportedSpecVersion ?? (_ => true)));
        await Assert.That(fixture.Lease.DisposeCount).IsEqualTo(1);
        return error!;
    }
}
