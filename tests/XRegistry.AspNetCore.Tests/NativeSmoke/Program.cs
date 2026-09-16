// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using XRegistry;
using XRegistry.AspNetCore;
using XRegistry.Models;
using XRegistry.Server;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
await using var app = builder.Build();
app.Use(async (context, next) =>
{
    if (!IPAddress.IsLoopback(context.Connection.RemoteIpAddress!))
    {
        context.Response.StatusCode = 403;
        return;
    }

    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "native-loopback-smoke")], "isolated-test"));
    await next(context);
});
using var source = typeof(LoopbackPolicy).Assembly.GetManifestResourceStream("PinnedSchemaModel")!;
var engine = new RegistryEngine(new()
{
    RegistryId = "native-smoke",
    PublicRoot = new Uri("https://native.example/registry"),
    Model = RegistryModel.Compile(await RegistryJson.ParseAsync(source)),
    AllowAnonymousReads = false
}, new InMemoryRegistryPersistence(), new LoopbackPolicy());
app.MapXRegistry(engine);
var endpointEngine = new RegistryEngine(new()
{
    RegistryId = "native-endpoint-smoke",
    PublicRoot = new Uri("https://native.example/endpoint-domain"),
    Model = BuiltInRegistryModels.Compile(RegistryModelKind.Endpoint),
    AllowAnonymousReads = false
}, new InMemoryRegistryPersistence(), new LoopbackPolicy());
app.MapXRegistry(endpointEngine, new() { MountPath = "/endpoint-domain" });
var validatedEngine = new RegistryEngine(new()
{
    RegistryId = "native-validation-smoke",
    PublicRoot = new Uri("https://native.example/validated"),
    Model = BuiltInRegistryModels.Compile(RegistryModelKind.Schema),
    ResourceValidator = new BuiltInRegistryResourceValidator(),
    AllowAnonymousReads = false
}, new InMemoryRegistryPersistence(), new LoopbackPolicy());
app.MapXRegistry(validatedEngine, new() { MountPath = "/validated" });
await app.StartAsync();
try
{
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient { BaseAddress = new Uri(address) };
    using var put = new HttpRequestMessage(HttpMethod.Put, "/schemagroups/g/schemas/s") { Content = new ByteArrayContent([0, 255, 123, 0]) };
    put.Headers.TryAddWithoutValidation("xRegistry-format", "jsonschema/draft-07");
    using var created = await client.SendAsync(put);
    if (created.StatusCode != HttpStatusCode.Created ||
        Convert.ToBase64String(await created.Content.ReadAsByteArrayAsync()) != "AP97AA==" ||
        created.Headers.Location?.AbsoluteUri != "https://native.example/registry/schemagroups/g/schemas/s")
    {
        throw new InvalidOperationException("Native exact-byte creation failed: " + await created.Content.ReadAsStringAsync());
    }

    using var get = await client.GetAsync("/schemagroups/g/schemas/s$details?inline=schema&binary");
    var metadata = RegistryJson.Parse(await get.Content.ReadAsByteArrayAsync()).RootElement;
    if (get.StatusCode != HttpStatusCode.OK || metadata.GetProperty("schemabase64").GetString() != "AP97AA==" ||
        metadata.GetProperty("formatvalidated").GetBoolean())
    {
        throw new InvalidOperationException("Native inline document or explicit validation outcome failed.");
    }

    using var first = await client.PatchAsync("/", new StringContent("""{"epoch":1}""", Encoding.UTF8, "application/json"));
    using var stale = await client.PatchAsync("/", new StringContent("""{"epoch":0}""", Encoding.UTF8, "application/json"));
    if (first.StatusCode != HttpStatusCode.OK || stale.StatusCode != HttpStatusCode.BadRequest ||
        RegistryJson.Parse(await stale.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString() != "mismatched_epoch")
    {
        throw new InvalidOperationException("Native epoch guarding failed.");
    }

    using var invalidEndpoint = await client.PutAsync("/endpoint-domain/endpoints/rejected",
        new StringContent("""{"usage":["producer","consumer"],"protocol":"NATS"}""", Encoding.UTF8, "application/json"));
    if (invalidEndpoint.StatusCode != HttpStatusCode.BadRequest ||
        RegistryJson.Parse(await invalidEndpoint.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString() != "invalid_attribute")
    {
        throw new InvalidOperationException("Native Endpoint domain validation did not reject invalid roles.");
    }

    using var validEndpoint = await client.PutAsync("/endpoint-domain/endpoints/accepted",
        new StringContent("""{"usage":["subscriber","consumer"],"protocol":"MQTT/5.0"}""", Encoding.UTF8, "application/json"));
    if (validEndpoint.StatusCode != HttpStatusCode.Created ||
        RegistryJson.Parse(await validEndpoint.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("usage").GetArrayLength() != 2)
    {
        throw new InvalidOperationException("Native Endpoint domain validation rejected permitted roles.");
    }

    using var validated = await client.PutAsync("/validated/schemagroups/g/schemas/s$details",
        new StringContent("""{"format":"Avro/1.11.0","schema":"int","meta":{"compatibility":"forward"}}""", Encoding.UTF8, "application/json"));
    if (validated.StatusCode != HttpStatusCode.Created ||
        !RegistryJson.Parse(await validated.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("formatvalidated").GetBoolean())
    {
        throw new InvalidOperationException("Native concrete schema validation did not establish validity.");
    }

    using var incompatible = await client.PostAsync("/validated/schemagroups/g/schemas/s$details",
        new StringContent("""{"format":"Avro/1.11.0","schema":"long"}""", Encoding.UTF8, "application/json"));
    if (incompatible.StatusCode != HttpStatusCode.BadRequest ||
        RegistryJson.Parse(await incompatible.Content.ReadAsByteArrayAsync()).RootElement.GetProperty("code").GetString() != "compatibility_violation")
    {
        throw new InvalidOperationException("Native Avro compatibility did not reject incompatible evolution.");
    }

    (string Id, string Format, string Document)[] formats =
    [
        ("jsonschema", "JsonSchema/draft/2020-12", """{"type":"string"}"""),
        ("xsd", "XSD/1.0", """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="value" type="xs:string"/></xs:schema>"""),
        ("avro", "Avro/1.8.2", "\"int\""),
        ("proto2", "Protobuf/2", """syntax = "proto2"; message Row { optional int32 id = 1; }"""),
        ("proto3", "Protobuf/3", """syntax = "proto3"; message Row { int32 id = 1; }"""),
        ("jsonstructure", "JsonStructure/draft-04", """{"$schema":"https://json-structure.org/meta/core/v0/#","$id":"urn:native:row","name":"Row","type":"string"}""")
    ];
    foreach (var format in formats)
    {
        var input = "{\"format\":\"" + format.Format + "\",\"schemabase64\":\"" +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(format.Document)) + "\"}";
        using var response = await client.PutAsync("/validated/schemagroups/g/schemas/" + format.Id + "$details",
            new StringContent(input, Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.Created || !RegistryJson.Parse(text).RootElement.GetProperty("formatvalidated").GetBoolean())
        {
            throw new InvalidOperationException("Native validation failed for " + format.Format + ": " + text);
        }
    }

    Console.WriteLine("NATIVE_HTTP_SMOKE_PASSED: pinned models, Kestrel, exact bytes, metadata, errors, epochs, Endpoint rules, five syntax families, Avro compatibility");
}
finally
{
    await app.StopAsync();
}

internal sealed class LoopbackPolicy : IRegistryAuthorizationPolicy
{
    public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(caller.HasClaim(ClaimTypes.NameIdentifier, "native-loopback-smoke"));
}
