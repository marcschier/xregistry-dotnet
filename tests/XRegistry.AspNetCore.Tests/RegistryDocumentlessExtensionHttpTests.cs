using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Server;

namespace XRegistry.AspNetCore.Tests;

public class RegistryDocumentlessExtensionHttpTests
{
    private const string ModelSource = """
        {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","hasdocument":false,
          "attributes":{"r":{"type":"string"},"rurl":{"type":"string"},"rbase64":{"type":"string"}}}}}}}
        """;

    [Test]
    [Arguments("r", "extension")]
    [Arguments("rurl", "https://not-contacted.invalid/value")]
    [Arguments("rbase64", "not base64!")]
    public async Task HttpModeledDocumentFormNamesRemainMetadata(string name, string value)
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(ModelSource));
        var metadata = RegistryJson.Parse(new JsonObject { [name] = value }.ToJsonString());
        var admitted = RegistryMetadataValidator.Validate(metadata, model.Groups["gs"].Resources["rs"].Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput, Model = model });
        await Assert.That(admitted.Metadata.RootElement.GetProperty(name).GetString()).IsEqualTo(value);
        await using var host = await HttpTests.TestHost.StartAsync(model: model, httpOptions: new() { MountPath = "/registry" });

        using var created = await host.Client.PutAsync("/registry/gs/g/rs/x",
            new StringContent(metadata.RootElement.GetRawText(), Encoding.UTF8, "application/json"));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Content.Headers.ContentType?.MediaType).IsEqualTo("application/json");
        var body = RegistryJson.Parse(await created.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(body.TryGetProperty(name, out _)).IsTrue();
        await Assert.That(body.GetProperty(name).GetString()).IsEqualTo(value);
        foreach (var path in new[] { "/registry/gs/g/rs/x", "/registry/gs/g/rs/x$details",
            "/registry/gs/g/rs/x/versions/1", "/registry/gs/g/rs/x/versions/1$details" })
        {
            using var read = await host.Client.GetAsync(path);
            await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(read.Content.Headers.ContentType?.MediaType).IsEqualTo("application/json");
            await Assert.That(RegistryJson.Parse(await read.Content.ReadAsByteArrayAsync()).RootElement.GetProperty(name).GetString())
                .IsEqualTo(value);
        }

        using var updated = await host.Client.PatchAsync("/registry/gs/g/rs/x",
            Json(new JsonObject { [name] = "updated extension" }.ToJsonString()));
        await Assert.That(updated.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(RegistryJson.Parse(await updated.Content.ReadAsByteArrayAsync()).RootElement.GetProperty(name).GetString())
            .IsEqualTo("updated extension");
        await Assert.That((await GetMetadataAsync(host.Client, "/registry/gs/g/rs/x/versions/1")).RootElement.GetProperty(name).GetString())
            .IsEqualTo("updated extension");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HttpCoreAdmittedAnyDocumentFormNamesRemainStructuredMetadata(bool wildcard)
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
        await using var host = await HttpTests.TestHost.StartAsync(model: model, documentReferencePolicy: policy,
            httpOptions: new() { MountPath = "/registry" });

        using var created = await host.Client.PutAsync("/registry/gs/g/rs/x", Json(metadata.RootElement.GetRawText()));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Content.Headers.ContentType?.MediaType).IsEqualTo("application/json");
        await AssertStructuredMetadataAsync(RegistryJson.Parse(await created.Content.ReadAsByteArrayAsync()));
        await AssertStructuredMetadataAsync(await GetMetadataAsync(host.Client, "/registry/gs/g/rs/x"));
        await AssertStructuredMetadataAsync(await GetMetadataAsync(host.Client, "/registry/gs/g/rs/x/versions/1"));
        await Assert.That(policy.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task HttpWildcardStringAdmissionDoesNotResolveDocumentUrls()
    {
        var model = ModelWithAttributes("""{"*":{"type":"string"}}""");
        var metadata = RegistryJson.Parse("""
            {"r":"extension","rurl":"https://not-contacted.invalid/value","rbase64":"not base64!"}
            """);
        var admitted = RegistryMetadataValidator.Validate(metadata, model.Groups["gs"].Resources["rs"].Attributes,
            new() { Mode = RegistryMetadataMode.ClientInput, Model = model });
        await Assert.That(admitted.Metadata.RootElement.EnumerateObject().Count()).IsEqualTo(3);
        var policy = new RejectDocumentReferences();
        await using var host = await HttpTests.TestHost.StartAsync(model: model, documentReferencePolicy: policy,
            httpOptions: new() { MountPath = "/registry" });

        using var created = await host.Client.PutAsync("/registry/gs/g/rs/x", Json(metadata.RootElement.GetRawText()));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var stored = (await GetMetadataAsync(host.Client, "/registry/gs/g/rs/x")).RootElement;
        await Assert.That(stored.GetProperty("r").GetString()).IsEqualTo("extension");
        await Assert.That(stored.GetProperty("rurl").GetString()).IsEqualTo("https://not-contacted.invalid/value");
        await Assert.That(stored.GetProperty("rbase64").GetString()).IsEqualTo("not base64!");
        await Assert.That(policy.Calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("r")]
    [Arguments("rurl")]
    [Arguments("rbase64")]
    public async Task HttpNamedDocumentFormDefinitionsStillOverrideWildcardTypes(string name)
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
        await using var host = await HttpTests.TestHost.StartAsync(model: model, httpOptions: new() { MountPath = "/registry" });

        using var response = await host.Client.PutAsync("/registry/gs/g/rs/x", Json(metadata.RootElement.GetRawText()));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var error = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("invalid_attribute");
        await Assert.That(error.GetProperty("args").GetProperty("name").GetString()).IsEqualTo(name);
        await Assert.That((await GetMetadataAsync(host.Client, "/registry")).RootElement.GetProperty("gscount").GetInt32())
            .IsEqualTo(0);
    }

    [Test]
    [Arguments("r")]
    [Arguments("rurl")]
    [Arguments("rbase64")]
    public async Task HttpUnmodeledDocumentFormNamesStillRejectWithoutPublication(string name)
    {
        await using var host = await HttpTests.TestHost.StartAsync(model: ModelWithAttributes("{}"),
            httpOptions: new() { MountPath = "/registry" });

        using var response = await host.Client.PutAsync("/registry/gs/g/rs/x",
            Json(new JsonObject { [name] = "not admitted" }.ToJsonString()));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString())
            .IsEqualTo("hasdocument_violation");
        await Assert.That((await GetMetadataAsync(host.Client, "/registry")).RootElement.GetProperty("gscount").GetInt32())
            .IsEqualTo(0);
    }

    [Test]
    public async Task HttpDocumentlessRoutesDoNotAcceptBinaryBodiesAsDocuments()
    {
        await using var host = await HttpTests.TestHost.StartAsync(model: RegistryModel.Compile(RegistryJson.Parse(ModelSource)),
            httpOptions: new() { MountPath = "/registry" });
        using var request = new HttpRequestMessage(HttpMethod.Put, "/registry/gs/g/rs/x")
        {
            Content = new ByteArrayContent([0, 255, 1])
        };
        request.Content.Headers.ContentType = new("application/octet-stream");

        using var response = await host.Client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString())
            .IsEqualTo("parsing_data");
        await Assert.That((await GetMetadataAsync(host.Client, "/registry")).RootElement.GetProperty("gscount").GetInt32())
            .IsEqualTo(0);
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    private static RegistryModel ModelWithAttributes(string attributes) => RegistryModel.Compile(RegistryJson.Parse(
        """{"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","hasdocument":false,"attributes":""" +
        attributes + "}}}}}"));

    private static async Task<RegistryJson> GetMetadataAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/json");
        return RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync());
    }

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
}
