using System.Security.Claims;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class RegistryDocumentlessExtensionTests
{
    private const string ModelSource = """
        {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","hasdocument":false,
          "attributes":{"r":{"type":"string"},"rurl":{"type":"string"},"rbase64":{"type":"string"}}}}}}}
        """;

    [Test]
    [Arguments("r", "extension")]
    [Arguments("rurl", "https://not-contacted.invalid/value")]
    [Arguments("rbase64", "not base64!")]
    public async Task ModeledDocumentFormNamesRemainDocumentlessMetadata(string name, string value)
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(ModelSource));
        var metadata = RegistryJson.Parse(new JsonObject { [name] = value }.ToJsonString());
        var admitted = RegistryMetadataValidator.Validate(metadata, model.Groups["gs"].Resources["rs"].Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput, Model = model });
        await Assert.That(admitted.Metadata.RootElement.GetProperty(name).GetString()).IsEqualTo(value);
        var engine = Create(ModelSource);

        var created = await engine.ExecuteAsync(new(RegistryAction.Replace, RegistryPath.Parse("/gs/g/rs/x"))
        {
            Metadata = metadata
        }, Writer());
        await Assert.That(created.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(created.IsDocument).IsFalse();
        await Assert.That(created.Document).IsNull();
        await Assert.That(created.Metadata!.RootElement.TryGetProperty(name, out _)).IsTrue();
        await Assert.That(created.Metadata.RootElement.GetProperty(name).GetString()).IsEqualTo(value);
        foreach (var path in new[] { "/gs/g/rs/x", "/gs/g/rs/x$details", "/gs/g/rs/x/versions/1", "/gs/g/rs/x/versions/1$details" })
        {
            var read = await Send(engine, RegistryAction.Read, path);
            await Assert.That(read.IsDocument).IsFalse();
            await Assert.That(read.Document).IsNull();
            await Assert.That(read.Metadata!.RootElement.GetProperty(name).GetString()).IsEqualTo(value);
        }

        var updated = await Send(engine, RegistryAction.Patch, "/gs/g/rs/x",
            new JsonObject { [name] = "updated extension" }.ToJsonString());
        await Assert.That(updated.Metadata!.RootElement.GetProperty(name).GetString()).IsEqualTo("updated extension");
        await Assert.That((await Send(engine, RegistryAction.Read, "/gs/g/rs/x/versions/1")).Metadata!.RootElement
            .GetProperty(name).GetString()).IsEqualTo("updated extension");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CoreAdmittedAnyDocumentFormNamesRemainStructuredMetadata(bool wildcard)
    {
        var model = ModelWithAttributes(wildcard ? """{"*":{"type":"any"}}""" : """
            {"r":{"type":"any"},"rurl":{"type":"any"},"rbase64":{"type":"any"}}
            """);
        var metadata = RegistryJson.Parse("""
            {"r":{"items":[1,"two",false]},"rurl":"https://not-contacted.invalid/value","rbase64":[true,7]}
            """);
        var admitted = RegistryMetadataValidator.Validate(metadata, model.Groups["gs"].Resources["rs"].Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput, Model = model });
        await AssertStructuredMetadataAsync(admitted.Metadata);
        var policy = new RejectDocumentReferences();
        var engine = CreateEngine(model, policy);

        var created = await engine.ExecuteAsync(new(RegistryAction.Replace, RegistryPath.Parse("/gs/g/rs/x"))
        {
            Metadata = metadata
        }, Writer());
        await Assert.That(created.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(created.IsDocument).IsFalse();
        await Assert.That(created.Document).IsNull();
        await AssertStructuredMetadataAsync(created.Metadata!);
        await AssertStructuredMetadataAsync((await Send(engine, RegistryAction.Read, "/gs/g/rs/x")).Metadata!);
        await AssertStructuredMetadataAsync((await Send(engine, RegistryAction.Read, "/gs/g/rs/x/versions/1")).Metadata!);
        await Assert.That(policy.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task WildcardStringAdmissionDoesNotCreateDocumentSemantics()
    {
        var model = ModelWithAttributes("""{"*":{"type":"string"}}""");
        var metadata = RegistryJson.Parse("""
            {"r":"extension","rurl":"https://not-contacted.invalid/value","rbase64":"not base64!"}
            """);
        var admitted = RegistryMetadataValidator.Validate(metadata, model.Groups["gs"].Resources["rs"].Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput, Model = model });
        await Assert.That(admitted.Metadata.RootElement.EnumerateObject().Count()).IsEqualTo(3);
        var policy = new RejectDocumentReferences();
        var engine = CreateEngine(model, policy);

        var created = await engine.ExecuteAsync(new(RegistryAction.Replace, RegistryPath.Parse("/gs/g/rs/x"))
        {
            Metadata = metadata
        }, Writer());
        await Assert.That(created.Kind).IsEqualTo(RegistryResultKind.Created);
        await Assert.That(created.IsDocument).IsFalse();
        await Assert.That(created.Document).IsNull();
        var stored = (await Send(engine, RegistryAction.Read, "/gs/g/rs/x")).Metadata!.RootElement;
        await Assert.That(stored.GetProperty("r").GetString()).IsEqualTo("extension");
        await Assert.That(stored.GetProperty("rurl").GetString()).IsEqualTo("https://not-contacted.invalid/value");
        await Assert.That(stored.GetProperty("rbase64").GetString()).IsEqualTo("not base64!");
        await Assert.That(policy.Calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("r")]
    [Arguments("rurl")]
    [Arguments("rbase64")]
    public async Task NamedDocumentFormDefinitionsStillOverrideWildcardTypes(string name)
    {
        var attributes = new JsonObject
        {
            ["*"] = new JsonObject { ["type"] = "any" },
            [name] = new JsonObject { ["type"] = "integer" }
        };
        var model = ModelWithAttributes(attributes.ToJsonString());
        var metadata = RegistryJson.Parse(new JsonObject { [name] = "wrong type" }.ToJsonString());
        await Assert.That(() => RegistryMetadataValidator.Validate(metadata, model.Groups["gs"].Resources["rs"].Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput, Model = model })).Throws<RegistryException>();
        var engine = CreateEngine(model);

        await ExpectCode(() => engine.ExecuteAsync(new(RegistryAction.Replace, RegistryPath.Parse("/gs/g/rs/x"))
        {
            Metadata = metadata
        }, Writer()), "invalid_attribute");
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("gscount").GetInt32())
            .IsEqualTo(0);
    }

    [Test]
    [Arguments("r")]
    [Arguments("rurl")]
    [Arguments("rbase64")]
    public async Task UnmodeledDocumentFormNamesStillRejectWithoutPublication(string name)
    {
        var engine = CreateEngine(ModelWithAttributes("{}"));
        await ExpectCode(() => Send(engine, RegistryAction.Replace, "/gs/g/rs/x",
            new JsonObject { [name] = "not admitted" }.ToJsonString()), "hasdocument_violation");
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("gscount").GetInt32())
            .IsEqualTo(0);
    }

    [Test]
    [Arguments("/gs/g/rs/x")]
    [Arguments("/gs/g/rs/x/versions/v")]
    public async Task ActualDocumentStreamsStayProhibitedWithoutConsumption(string path)
    {
        var engine = Create(ModelSource);
        using var document = new ObservedDocument();
        await ExpectCode(() => engine.ExecuteAsync(new(RegistryAction.Replace, RegistryPath.Parse(path))
        {
            Metadata = RegistryJson.Parse("""{"r":"metadata extension"}"""),
            Document = document
        }, Writer()), "hasdocument_violation");
        await Assert.That(document.ReadCalls).IsEqualTo(0);
        await Assert.That(document.Position).IsEqualTo(0L);
        await Assert.That(document.CanRead).IsTrue();
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("gscount").GetInt32())
            .IsEqualTo(0);
    }

    private static RegistryModel ModelWithAttributes(string attributes) => RegistryModel.Compile(RegistryJson.Parse(
        """{"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","hasdocument":false,"attributes":""" +
        attributes + "}}}}}"));

    private static RegistryEngine CreateEngine(RegistryModel model, IRegistryDocumentReferencePolicy? referencePolicy = null) =>
        new(new()
        {
            RegistryId = "documentless",
            PublicRoot = new Uri("https://registry.example/catalog"),
            Model = model,
            DocumentReferencePolicy = referencePolicy
        }, new InMemoryRegistryPersistence(), new PermitPolicy());

    private static async Task AssertStructuredMetadataAsync(RegistryJson metadata)
    {
        var root = metadata.RootElement;
        var items = root.GetProperty("r").GetProperty("items");
        await Assert.That(items.GetArrayLength()).IsEqualTo(3);
        await Assert.That(items[0].GetInt32()).IsEqualTo(1);
        await Assert.That(items[1].GetString()).IsEqualTo("two");
        await Assert.That(items[2].GetBoolean()).IsFalse();
        await Assert.That(root.GetProperty("rurl").GetString()).IsEqualTo("https://not-contacted.invalid/value");
        await Assert.That(root.GetProperty("rbase64")[0].GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("rbase64")[1].GetInt32()).IsEqualTo(7);
    }

    private sealed class RejectDocumentReferences : IRegistryDocumentReferencePolicy
    {
        internal int Calls { get; private set; }

        public ValueTask AuthorizeAsync(ClaimsPrincipal caller, Uri reference, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("A metadata extension must not invoke Document reference policy.");
        }
    }

    private sealed class ObservedDocument() : MemoryStream([0, 255])
    {
        internal int ReadCalls { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            return base.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
