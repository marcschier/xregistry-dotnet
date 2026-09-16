// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Server;

namespace XRegistry.AspNetCore.Tests;

public class RegistryScalarMetadataContractHttpTests
{
    private static readonly RegistryModel s_model = RegistryModel.Compile(RegistryJson.Parse("""
        {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","hasdocument":false}}}}}
        """));

    [Test]
    [Arguments("/gs/g", "name")]
    [Arguments("/gs/g", "documentation")]
    [Arguments("/gs/g", "icon")]
    [Arguments("/gs/g/rs/r", "format")]
    public async Task HttpPutRejectsEmptySystemFieldsWithoutPublication(string path, string name)
    {
        var store = new InMemoryRegistryPersistence();
        await using var host = await HttpTests.TestHost.StartAsync(model: s_model, persistence: store);
        var before = await host.Client.GetStringAsync("/");
        using var beforeSnapshot = await store.ReadSnapshotAsync();
        using var response = await host.Client.PutAsync(path, Json("{\"" + name + "\":\"\"}"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var error = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("invalid_attribute");
        await Assert.That(await host.Client.GetStringAsync("/")).IsEqualTo(before);
        using var absent = await host.Client.GetAsync(path);
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var afterSnapshot = await store.ReadSnapshotAsync();
        await Assert.That(afterSnapshot.Generation).IsEqualTo(beforeSnapshot.Generation);
    }

    [Test]
    [Arguments(4085, true)]
    [Arguments(4086, false)]
    [Arguments(4096, false)]
    public async Task HttpPutEnforcesTheInclusiveScalarLimitBeforePublication(int length, bool accepted)
    {
        var store = new InMemoryRegistryPersistence();
        await using var host = await HttpTests.TestHost.StartAsync(model: s_model, persistence: store);
        var before = await host.Client.GetStringAsync("/");
        using var beforeSnapshot = await store.ReadSnapshotAsync();
        var text = new string('x', length);
        using var response = await host.Client.PutAsync("/gs/g", Json("{\"description\":\"" + text + "\"}"));

        if (accepted)
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var created = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
            await Assert.That(created.GetProperty("description").GetString()).IsEqualTo(text);
            var stored = RegistryJson.Parse(await host.Client.GetStringAsync("/gs/g")).RootElement;
            await Assert.That(stored.GetProperty("description").GetString()).IsEqualTo(text);
        }
        else
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            var error = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
            await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("invalid_attribute");
            await Assert.That(await host.Client.GetStringAsync("/")).IsEqualTo(before);
            using var absent = await host.Client.GetAsync("/gs/g");
            await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        using var afterSnapshot = await store.ReadSnapshotAsync();
        await Assert.That(afterSnapshot.Generation).IsEqualTo(beforeSnapshot.Generation + (accepted ? 1 : 0));
    }

    [Test]
    [Arguments("/", "name")]
    [Arguments("/gs/g", "documentation")]
    [Arguments("/gs/g/rs/r", "icon")]
    [Arguments("/gs/g/rs/r/versions/v1", "format")]
    public async Task HttpPatchRejectsEmptySystemFieldsWithoutChangingExistingEntities(string path, string name)
    {
        var store = new InMemoryRegistryPersistence();
        await using var host = await HttpTests.TestHost.StartAsync(model: s_model, persistence: store);
        using var seeded = await host.Client.PutAsync(path, Text(name, "before"));
        await Assert.That(seeded.StatusCode).IsEqualTo(path == "/" ? HttpStatusCode.OK : HttpStatusCode.Created);
        var before = await host.Client.GetStringAsync(path);
        using var beforeSnapshot = await store.ReadSnapshotAsync();

        using var rejected = await host.Client.PatchAsync(path, Text(name, ""));
        await AssertInvalidAttribute(rejected);
        await Assert.That(await host.Client.GetStringAsync(path)).IsEqualTo(before);
        using var afterSnapshot = await store.ReadSnapshotAsync();
        await Assert.That(afterSnapshot.Generation).IsEqualTo(beforeSnapshot.Generation);
    }

    [Test]
    [Arguments("/gs/g", "name")]
    [Arguments("/gs/g/rs/r", "format")]
    public async Task HttpPatchKeepsEmptyDescriptionDistinctFromSystemNullDeletion(string path, string name)
    {
        await using var host = await HttpTests.TestHost.StartAsync(model: s_model);
        using var seeded = await host.Client.PutAsync(path, Text(name, "before"));
        await Assert.That(seeded.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var patched = await host.Client.PatchAsync(path, Json("{\"description\":\"\",\"" + name + "\":null}"));
        await Assert.That(patched.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var stored = RegistryJson.Parse(await host.Client.GetStringAsync(path));

        await Assert.That(stored.GetPresence("description")).IsEqualTo(JsonPresence.Value);
        await Assert.That(stored.RootElement.GetProperty("description").GetString()).IsEqualTo("");
        await Assert.That(stored.GetPresence(name)).IsEqualTo(JsonPresence.Absent);
    }

    [Test]
    [Arguments("\u00e9", 2042, 1)]
    [Arguments("\u20ac", 1361, 2)]
    [Arguments("\U0001f600", 1021, 1)]
    public async Task HttpPatchCountsUtf8RatherThanUtf16AndPreservesTheAcceptedBoundary(string unit, int count, int padding)
    {
        var store = new InMemoryRegistryPersistence();
        await using var host = await HttpTests.TestHost.StartAsync(model: s_model, persistence: store);
        var text = string.Concat(Enumerable.Repeat(unit, count)) + new string('x', padding);
        using var created = await host.Client.PutAsync("/gs/g", Text("description", text));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var before = await host.Client.GetStringAsync("/gs/g");
        await Assert.That(RegistryJson.Parse(before).RootElement.GetProperty("description").GetString()).IsEqualTo(text);
        using var beforeSnapshot = await store.ReadSnapshotAsync();

        using var rejected = await host.Client.PatchAsync("/gs/g", Text("description", text + "x"));
        await AssertInvalidAttribute(rejected);
        await Assert.That(await host.Client.GetStringAsync("/gs/g")).IsEqualTo(before);
        using var afterSnapshot = await store.ReadSnapshotAsync();
        await Assert.That(afterSnapshot.Generation).IsEqualTo(beforeSnapshot.Generation);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HttpJsonEscapesDoNotShiftTheDecodedUtf8Boundary(bool escaped)
    {
        await using var host = await HttpTests.TestHost.StartAsync(model: s_model);
        var prefix = new string('x', 4082);
        var member = escaped ? "descri\\u0070tion" : "description";
        var suffix = escaped ? "\\u20ac" : "\u20ac";
        using var created = await host.Client.PutAsync("/gs/g", Json("{\"" + member + "\":\"" + prefix + suffix + "\"}"));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var stored = RegistryJson.Parse(await host.Client.GetStringAsync("/gs/g")).RootElement;
        await Assert.That(stored.GetProperty("description").GetString()).IsEqualTo(prefix + "\u20ac");

        using var rejected = await host.Client.PutAsync("/gs/bad", Json("{\"" + member + "\":\"" + prefix + suffix + "x\"}"));
        await AssertInvalidAttribute(rejected);
        using var absent = await host.Client.GetAsync("/gs/bad");
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments(" ", "%20", 4085, 0)]
    [Arguments("\U0001f600", "%F0%9F%98%80", 1021, 1)]
    public async Task HttpDocumentHeadersUseDecodedScalarBytesNotPercentEncodedWireBytes(
        string unit, string encodedUnit, int count, int padding)
    {
        var store = new InMemoryRegistryPersistence();
        await using var host = await HttpTests.TestHost.StartAsync(model: DocumentModel(), persistence: store);
        var text = string.Concat(Enumerable.Repeat(unit, count)) + new string('x', padding);
        var wire = string.Concat(Enumerable.Repeat(encodedUnit, count)) + new string('x', padding);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/gs/g/rs/r") { Content = new ByteArrayContent([1, 2, 3]) };
        await Assert.That(request.Headers.TryAddWithoutValidation("xRegistry-description", wire)).IsTrue();
        using var created = await host.Client.SendAsync(request);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.GetValues("xRegistry-description").Single()).IsEqualTo(wire);
        var before = await host.Client.GetStringAsync("/gs/g/rs/r$details");
        await Assert.That(RegistryJson.Parse(before).RootElement.GetProperty("description").GetString()).IsEqualTo(text);
        using var beforeSnapshot = await store.ReadSnapshotAsync();

        using var next = new HttpRequestMessage(HttpMethod.Put, "/gs/g/rs/r") { Content = new ByteArrayContent([4, 5, 6]) };
        await Assert.That(next.Headers.TryAddWithoutValidation("xRegistry-description", wire + "x")).IsTrue();
        using var rejected = await host.Client.SendAsync(next);
        await AssertInvalidAttribute(rejected);
        await Assert.That(await host.Client.GetStringAsync("/gs/g/rs/r$details")).IsEqualTo(before);
        using var stored = await host.Client.GetAsync("/gs/g/rs/r");
        await Assert.That(stored.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Convert.ToHexString(await stored.Content.ReadAsByteArrayAsync())).IsEqualTo("010203");
        await Assert.That(stored.Headers.GetValues("xRegistry-description").Single()).IsEqualTo(wire);
        using var afterSnapshot = await store.ReadSnapshotAsync();
        await Assert.That(afterSnapshot.Generation).IsEqualTo(beforeSnapshot.Generation);
    }

    [Test]
    [Arguments("name")]
    [Arguments("format")]
    [Arguments("documentation")]
    [Arguments("icon")]
    public async Task HttpDocumentHeadersRejectEmptySystemFieldsBeforeDocumentPublication(string name)
    {
        var store = new InMemoryRegistryPersistence();
        await using var host = await HttpTests.TestHost.StartAsync(model: DocumentModel(), persistence: store);
        var before = await host.Client.GetStringAsync("/");
        using var beforeSnapshot = await store.ReadSnapshotAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, "/gs/g/rs/r") { Content = new ByteArrayContent([1]) };
        await Assert.That(request.Headers.TryAddWithoutValidation("xRegistry-" + name, "\"\"")).IsTrue();

        using var rejected = await host.Client.SendAsync(request);
        await AssertInvalidAttribute(rejected);
        await Assert.That(await host.Client.GetStringAsync("/")).IsEqualTo(before);
        using var absent = await host.Client.GetAsync("/gs/g/rs/r$details");
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var afterSnapshot = await store.ReadSnapshotAsync();
        await Assert.That(afterSnapshot.Generation).IsEqualTo(beforeSnapshot.Generation);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HttpDetailsAllowsInlineDocumentsLargerThanTheScalarMetadataLimit(bool base64)
    {
        await using var host = await HttpTests.TestHost.StartAsync(model: DocumentModel());
        var text = new string('x', 8192);
        var expected = base64 ? new byte[6144] : Encoding.UTF8.GetBytes(text);
        var payload = new JsonObject
        {
            ["contenttype"] = base64 ? "application/octet-stream" : "text/plain",
            [base64 ? "rbase64" : "r"] = base64 ? Convert.ToBase64String(expected) : text
        };
        using var created = await host.Client.PutAsync("/gs/g/rs/r$details", Json(payload.ToJsonString()));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var stored = await host.Client.GetAsync("/gs/g/rs/r");

        await Assert.That(stored.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Convert.ToHexString(await stored.Content.ReadAsByteArrayAsync())).IsEqualTo(Convert.ToHexString(expected));
    }

    [Test]
    public async Task HttpDocumentlessPayloadNamedExtensionRetainsTheScalarLimit()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","hasdocument":false,
              "attributes":{"rbase64":"string"}}}}}}
            """));
        await using var host = await HttpTests.TestHost.StartAsync(model: model);
        var text = new string('x', 4089);
        using var created = await host.Client.PutAsync("/gs/g/rs/r", Text("rbase64", text));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var before = await host.Client.GetStringAsync("/gs/g/rs/r");
        await Assert.That(RegistryJson.Parse(before).RootElement.GetProperty("rbase64").GetString()).IsEqualTo(text);

        using var rejected = await host.Client.PatchAsync("/gs/g/rs/r", Text("rbase64", text + "x"));
        await AssertInvalidAttribute(rejected);
        await Assert.That(await host.Client.GetStringAsync("/gs/g/rs/r")).IsEqualTo(before);
    }

    [Test]
    public async Task HttpRetainedConditionalPatchStillValidatesTheActiveScalar()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"groups":{"gs":{"singular":"g","attributes":{
              "kind":{"type":"string","ifvalues":{"on":{"siblingattributes":{"extra":"string"}}}}
            }}}}
            """));
        await using var host = await HttpTests.TestHost.StartAsync(model: model);
        using var seeded = await host.Client.PutAsync("/gs/g", Json("""{"kind":"on","extra":"before"}"""));
        await Assert.That(seeded.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var text = new string('x', 4091);
        using var accepted = await host.Client.PatchAsync("/gs/g", Text("extra", text));
        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var before = await host.Client.GetStringAsync("/gs/g");
        var metadata = RegistryJson.Parse(before).RootElement;
        await Assert.That(metadata.GetProperty("kind").GetString()).IsEqualTo("on");
        await Assert.That(metadata.GetProperty("extra").GetString()).IsEqualTo(text);

        using var rejected = await host.Client.PatchAsync("/gs/g", Text("extra", text + "x"));
        await AssertInvalidAttribute(rejected);
        await Assert.That(await host.Client.GetStringAsync("/gs/g")).IsEqualTo(before);
    }

    [Test]
    public async Task HttpReadonlyInputAndSameNamedExtensionsKeepTheirSeparateContracts()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse("""
            {"documentation":"","groups":{"gs":{"singular":"g","attributes":{
              "name":{"type":"string","readonly":true,"required":true,"default":"server"},
              "format":"string","body":{"type":"object","attributes":{
                "name":"string","documentation":"url","icon":"url"}}}}}}
            """));
        await using var host = await HttpTests.TestHost.StartAsync(model: model);
        using var created = await host.Client.PutAsync("/gs/g", Json("""
            {"name":"","format":"","description":"","body":{"name":"","documentation":"","icon":""}}
            """));
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var patched = await host.Client.PatchAsync("/gs/g", Text("name", new string('x', 8192)));
        await Assert.That(patched.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var stored = RegistryJson.Parse(await host.Client.GetStringAsync("/gs/g")).RootElement;

        await Assert.That(stored.GetProperty("name").GetString()).IsEqualTo("server");
        await Assert.That(stored.GetProperty("format").GetString()).IsEqualTo("");
        await Assert.That(stored.GetProperty("description").GetString()).IsEqualTo("");
        foreach (var name in new[] { "name", "documentation", "icon" })
        {
            await Assert.That(stored.GetProperty("body").GetProperty(name).GetString()).IsEqualTo("");
        }
    }

    [Test]
    public async Task HttpNestedCollectionScalarFailureDoesNotPublishValidSiblings()
    {
        var store = new InMemoryRegistryPersistence();
        await using var host = await HttpTests.TestHost.StartAsync(model: s_model, persistence: store);
        var before = await host.Client.GetStringAsync("/");
        using var beforeSnapshot = await store.ReadSnapshotAsync();
        using var rejected = await host.Client.PostAsync("/gs", Json(
            "{\"good\":{\"description\":\"valid\"},\"bad\":{\"description\":\"" + new string('x', 4086) + "\"}}"));

        await AssertInvalidAttribute(rejected);
        await Assert.That(await host.Client.GetStringAsync("/")).IsEqualTo(before);
        using var afterSnapshot = await store.ReadSnapshotAsync();
        await Assert.That(afterSnapshot.Generation).IsEqualTo(beforeSnapshot.Generation);
    }

    private static async Task AssertInvalidAttribute(HttpResponseMessage response)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var error = RegistryJson.Parse(await response.Content.ReadAsByteArrayAsync()).RootElement;
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("invalid_attribute");
        await Assert.That(error.GetProperty("type").GetString())
            .IsEqualTo("https://github.com/xregistry/spec/blob/main/core/spec.md#invalid_attribute");
    }

    private static RegistryModel DocumentModel() => RegistryModel.Compile(RegistryJson.Parse("""
        {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r"}}}}}
        """));

    private static StringContent Text(string name, string text) => Json(new JsonObject { [name] = text }.ToJsonString());

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");
}
