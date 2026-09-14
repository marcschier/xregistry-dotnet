using System.Net;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Federation;
using XRegistry.Queries;

namespace XRegistry.Bridge.Tests;

public class BridgeQueryTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task VersionQueriesReuseCapturedDefaultContextWithoutReadingLogicalMeta(bool producerOwned, bool collection)
    {
        const string owner = "/dirs/g/files/a";
        var source = new ControlledSource("one", "v1", ["a"]) { Producer = producerOwned };
        source.Override = async (request, _) =>
        {
            if (request.Target == owner + "/meta")
            {
                throw new FederationException(FederationErrorCode.PolicyDenied, "The logical Meta URI is not independently authorized.");
            }
            var result = await source.ReadDefault(request);
            if (request.Operation != FederationOperation.Entity || request.Target != owner) { return result; }
            var value = JsonNode.Parse(result.Metadata.GetRawText())!.AsObject();
            value["entity"]!["meta"] = source.Entity(owner + "/meta");
            return FederationReadResult.FromMetadata(result.SelectedXid, result.Context, RegistryJson.Parse(value.ToJsonString()).RootElement);
        };
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        var path = owner + (collection ? "/versions" : "/versions/v1$details");
        using var response = await client.GetAsync("/registry" + path + "?filter=isdefault%3Dtrue%2Cfilebase64%3DAP8NCkE%3D&inline=file");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var metadata = await BridgeFixture.Json(response);
        if (collection)
        {
            await Assert.That(string.Join(",", metadata.EnumerateObject().Select(property => property.Name))).IsEqualTo("v1");
            metadata = metadata.GetProperty("v1");
        }
        await Assert.That(metadata.GetProperty("versionid").GetString()).IsEqualTo("v1");
        await Assert.That(metadata.GetProperty("isdefault").GetBoolean()).IsTrue();
        await Assert.That(metadata.GetProperty("filebase64").GetString()).IsEqualTo("AP8NCkE=");
        await Assert.That(metadata.TryGetProperty("defaultversionid", out _)).IsFalse();
        await Assert.That(source.Reads.Any(read => read.Target == owner + "/meta")).IsFalse();
        await Assert.That(source.Reads.Count(read => read.Operation == FederationOperation.Entity && read.Target == owner)).IsEqualTo(1);
        await Assert.That(source.Opens).IsEqualTo(source.Closes);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NonDefaultVersionFactsDoNotReadOrExposeThePinnedDefaultVersion(bool producerOwned)
    {
        const string owner = "/dirs/g/files/a";
        var source = new ControlledSource("one", "v1", ["a"]) { Producer = producerOwned };
        source.Override = async (request, _) =>
        {
            if (request.Target == owner + "/meta" || request.Target == owner + "/versions/v1")
            {
                throw new FederationException(FederationErrorCode.PolicyDenied, "The separately addressed default or Meta is not authorized.");
            }
            if (request.Target == owner + "/versions/v2")
            {
                var version = source.Entity(owner + "/versions/v1");
                version["xid"] = request.Target;
                version["self"] = source.Context.Source + request.Target + "$details";
                version["versionid"] = "v2";
                version["ancestorid"] = "v2";
                version["isdefault"] = false;
                return FederationReadResult.FromMetadata(request.Target, source.Context,
                    RegistryJson.Parse(new JsonObject { ["entity"] = version }.ToJsonString()).RootElement);
            }
            var result = await source.ReadDefault(request);
            if (request.Operation != FederationOperation.Entity || request.Target != owner) { return result; }
            var value = JsonNode.Parse(result.Metadata.GetRawText())!.AsObject();
            value["entity"]!["meta"] = source.Entity(owner + "/meta");
            return FederationReadResult.FromMetadata(result.SelectedXid, result.Context, RegistryJson.Parse(value.ToJsonString()).RootElement);
        };
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry" + owner + "/versions/v2$details?filter=isdefault%3Dfalse");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var metadata = await BridgeFixture.Json(response);
        await Assert.That(metadata.GetProperty("versionid").GetString()).IsEqualTo("v2");
        await Assert.That(metadata.GetProperty("isdefault").GetBoolean()).IsFalse();
        await Assert.That(metadata.TryGetProperty("defaultversionid", out _)).IsFalse();
        await Assert.That(source.Reads.Any(read => read.Target == owner + "/meta" || read.Target == owner + "/versions/v1")).IsFalse();
        await Assert.That(source.Opens).IsEqualTo(source.Closes);
    }

    [Test]
    [Arguments("xid", "/dirs/g/files/other/meta")]
    [Arguments("fileid", "other")]
    public async Task AliasVersionDefaultContextRejectsForeignTargetMeta(string attribute, string invalid)
    {
        var source = AliasSource.Create("one", "v1", new Dictionary<string, string> { ["alias"] = "/dirs/g/files/real" });
        var inherited = source.Override!;
        source.Override = async (request, token) =>
        {
            var result = await inherited(request, token);
            if (request.Target != "/dirs/g/files/real/meta") { return result; }
            var value = JsonNode.Parse(result.Metadata.GetRawText())!.AsObject();
            value["entity"]![attribute] = invalid;
            return FederationReadResult.FromMetadata(result.SelectedXid, result.Context, RegistryJson.Parse(value.ToJsonString()).RootElement);
        };
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files/alias/versions/v1$details?filter=isdefault%3Dtrue");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
        await Assert.That((await BridgeFixture.Json(response)).GetProperty("code").GetString()).IsEqualTo("source_invalidpackage");
        await Assert.That(source.Reads.Any(read => read.Target.StartsWith("/dirs/g/files/other", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("", "/dirs/missing/files")]
    [Arguments("?filter=excludeall", "/dirs/missing/files")]
    [Arguments("?doc", "/dirs/missing/files")]
    [Arguments("?filter=excludeall&doc", "/dirs/missing/files")]
    [Arguments("", "/dirs/g/files/missing/versions")]
    [Arguments("?filter=excludeall", "/dirs/g/files/missing/versions")]
    [Arguments("?doc", "/dirs/g/files/missing/versions")]
    [Arguments("?filter=excludeall&doc", "/dirs/g/files/missing/versions")]
    public async Task EmptyQueriesRequireAnExistingCollectionOwner(string query, string path)
    {
        var source = new ControlledSource("one", "v1", ["a"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry" + path + query);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await BridgeFixture.Json(response)).GetProperty("code").GetString()).IsEqualTo("source_notfound");
        await Assert.That(source.Opens).IsEqualTo(source.Closes);
    }

    [Test]
    [Arguments("/dirs", "/")]
    [Arguments("/dirs/g/notes", "/dirs/g")]
    [Arguments("/dirs/g/files/a/versions", "/dirs/g/files/a")]
    public async Task ExcludeAllValidatesOnlyTheOwnerWithoutEnumeratingItsChildren(string path, string owner)
    {
        var source = new ControlledSource("one", "v1", ["a"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry" + path + "?filter=excludeall");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await BridgeFixture.Json(response)).EnumerateObject().Count()).IsEqualTo(0);
        await Assert.That(source.Reads.Count).IsEqualTo(1);
        await Assert.That(source.Reads[0]).IsEqualTo((FederationOperation.Entity, owner));
        await Assert.That(source.Opens).IsEqualTo(source.Closes);
    }

    [Test]
    public async Task ProducerQueriesAuthorizeTheCollectionOwnerBeforeReadingMembers()
    {
        var source = new ControlledSource("one", "v1", ["a"]) { Producer = true };
        source.Override = (request, _) => request.Target == "/dirs/g"
            ? throw new FederationException(FederationErrorCode.PolicyDenied, "The collection owner is not authorized.")
            : source.ReadDefault(request);
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files?sort=fileid");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await BridgeFixture.Json(response)).GetProperty("code").GetString()).IsEqualTo("source_policydenied");
        await Assert.That(source.Reads.Count).IsEqualTo(1);
        await Assert.That(source.Reads[0]).IsEqualTo((FederationOperation.Entity, "/dirs/g"));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AliasMetaQueriesEnforceSelectedSourceMetaAuthorization(bool dangling)
    {
        var target = dangling ? "/dirs/g/files/missing" : "/dirs/g/files/real";
        var source = AliasSource.Create("first", "v1", new Dictionary<string, string> { ["alias"] = target });
        var inherited = source.Override!;
        source.Override = (request, token) => request.Target == "/dirs/g/files/alias/meta"
            ? throw new FederationException(FederationErrorCode.PolicyDenied, "The logical alias Meta is not authorized.")
            : inherited(request, token);
        var later = new ControlledSource("later", "v2", ["alias", "real"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration(), later.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files/alias$details?filter=" + Uri.EscapeDataString("meta.xref=" + target));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await BridgeFixture.Json(response)).GetProperty("code").GetString()).IsEqualTo("source_policydenied");
        await Assert.That(source.Reads.Any(read => read.Target == "/dirs/g/files/alias/meta")).IsTrue();
        await Assert.That(later.Opens).IsEqualTo(dangling ? 1 : 0);
        await Assert.That(later.Reads.Any(read => read.Target == "/dirs/g/files/alias/meta")).IsFalse();
    }

    [Test]
    public async Task AliasMetaTargetChangesCannotRebindTheSelectedQueryOrigin()
    {
        var source = AliasSource.Create("first", "v1", new Dictionary<string, string> { ["alias"] = "/dirs/g/files/real" });
        var inherited = source.Override!;
        source.Override = async (request, token) =>
        {
            var result = await inherited(request, token);
            if (request.Target != "/dirs/g/files/alias/meta") { return result; }
            var value = JsonNode.Parse(result.Metadata.GetRawText())!.AsObject();
            value["entity"]!["xref"] = "/dirs/g/files/other";
            return FederationReadResult.FromMetadata(result.SelectedXid, result.Context, RegistryJson.Parse(value.ToJsonString()).RootElement);
        };
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files/alias$details?filter=meta.xref%3D%2Fdirs%2Fg%2Ffiles%2Freal");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
        await Assert.That((await BridgeFixture.Json(response)).GetProperty("code").GetString()).IsEqualTo("source_inconsistentsnapshot");
        await Assert.That(source.Reads.Any(read => read.Target.StartsWith("/dirs/g/files/other", StringComparison.Ordinal))).IsFalse();
        await Assert.That(source.Opens).IsEqualTo(source.Closes);
    }

    [Test]
    public async Task FilterAfterWholeResourceShadowNeverRecoversLowerPriorityVersions()
    {
        var local = new ControlledSource("local", "local-v", ["shared", "local"]);
        var remote = new ControlledSource("remote", "remote-v", ["shared", "remote"]);
        await using var app = await BridgeFixture.StartAsync([local.Registration(), remote.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files?filter=versionid%3Dremote-v");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var value = await BridgeFixture.Json(response);
        await Assert.That(string.Join(",", value.EnumerateObject().Select(property => property.Name))).IsEqualTo("remote");
        await Assert.That(value.GetProperty("remote").GetProperty("versionid").GetString()).IsEqualTo("remote-v");
        await Assert.That(remote.Reads.Any(read => read.Target == "/dirs/g/files/shared/versions/remote-v")).IsFalse();
        await Assert.That(local.Opens).IsEqualTo(local.Closes);
        await Assert.That(remote.Opens).IsEqualTo(remote.Closes);
    }

    [Test]
    public async Task MetaAndDefaultVersionFactsUseSharedCorePlanesAndExactNumericOrdering()
    {
        var source = new ControlledSource("one", "v1", ["larger", "smaller"]);
        source.Override = async (request, _) =>
        {
            var result = await source.ReadDefault(request);
            var metadata = JsonNode.Parse(result.Metadata.GetRawText())!.AsObject();
            if (metadata["entity"] is JsonObject entity)
            {
                entity["epoch"] = request.Target.EndsWith("/meta", StringComparison.Ordinal) ? JsonValue.Create(7) :
                    JsonNode.Parse(request.Target.Contains("/larger", StringComparison.Ordinal) ? "9007199254740993" : "9007199254740992");
            }
            return FederationReadResult.FromMetadata(result.SelectedXid, result.Context, RegistryJson.Parse(metadata.ToJsonString()).RootElement);
        };
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var sorted = await client.GetAsync("/registry/dirs/g/files?sort=epoch");
        await Assert.That(sorted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(string.Join(",", (await BridgeFixture.Json(sorted)).EnumerateObject().Select(property => property.Name)))
            .IsEqualTo("smaller,larger");
        using var filtered = await client.GetAsync("/registry/dirs/g/files?filter=meta.epoch%3D7%2Cepoch%3E9007199254740992");
        await Assert.That(filtered.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(string.Join(",", (await BridgeFixture.Json(filtered)).EnumerateObject().Select(property => property.Name)))
            .IsEqualTo("larger");
    }

    [Test]
    public async Task NestedSharedSelectionUpdatesCountsAndProducesAnExactFollowableCollectionLink()
    {
        var local = new ControlledSource("local", "v1", ["shared"]);
        var remote = new ControlledSource("remote", "v2", ["shared", "remote"]);
        await using var app = await BridgeFixture.StartAsync([local.Registration(), remote.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var group = await client.GetAsync("/registry/dirs/g?filter=files.fileid%3Dremote");
        await Assert.That(group.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var metadata = await BridgeFixture.Json(group);
        await Assert.That(metadata.GetProperty("filescount").GetInt32()).IsEqualTo(1);
        await Assert.That(metadata.GetProperty("filesurl").GetString())
            .IsEqualTo("https://bridge.example/registry/dirs/g/files?filter=fileid%3Dremote");
        using var follow = await client.GetAsync(new Uri(metadata.GetProperty("filesurl").GetString()!).PathAndQuery);
        await Assert.That(follow.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(string.Join(",", (await BridgeFixture.Json(follow)).EnumerateObject().Select(property => property.Name))).IsEqualTo("remote");
        using var document = await client.GetAsync("/registry/dirs/g?filter=files.fileid%3Dremote&doc&inline=files.versions");
        var doc = await BridgeFixture.Json(document);
        await Assert.That(doc.GetProperty("filesurl").GetString()).IsEqualTo("#/files");
        await Assert.That(doc.GetProperty("files").GetProperty("remote").GetProperty("self").GetString()).IsEqualTo("#/files/remote");
        await Assert.That(doc.GetProperty("files").TryGetProperty("shared", out _)).IsFalse();
    }

    [Test]
    public async Task RepeatedFilterValuesUseSharedOrAndAliasFactsRetainLogicalIds()
    {
        var source = AliasSource.Create("one", "v1", new Dictionary<string, string> { ["alias"] = "/dirs/g/files/real" });
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var alias = await client.GetAsync("/registry/dirs/g/files/alias$details?filter=meta.xref%3D%2Fdirs%2Fg%2Ffiles%2Freal%2Cfileid%3Dalias");
        await Assert.That(alias.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await BridgeFixture.Json(alias)).GetProperty("fileid").GetString()).IsEqualTo("alias");
        var normal = new ControlledSource("normal", "v1", ["a", "b", "c"]);
        await using var secondApp = await BridgeFixture.StartAsync([normal.Registration()]);
        using var secondClient = BridgeFixture.Client(secondApp);
        using var selected = await secondClient.GetAsync("/registry/dirs/g/files?filter=fileid%3Da&filter=fileid%3Dc&sort=fileid%3Ddesc");
        await Assert.That(string.Join(",", (await BridgeFixture.Json(selected)).EnumerateObject().Select(property => property.Name))).IsEqualTo("c,a");
    }

    [Test]
    public async Task SharedQueryErrorsAreSpecificAndDoNotOpenSourcesForInvalidGrammar()
    {
        var source = new ControlledSource("one", "v1", ["a"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var invalid = await client.GetAsync("/registry/dirs/g/files?filter=meta%5B");
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await BridgeFixture.Json(invalid)).GetProperty("code").GetString()).IsEqualTo("bad_filter");
        using var sortEntity = await client.GetAsync("/registry/dirs/g?sort=epoch");
        await Assert.That(sortEntity.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await BridgeFixture.Json(sortEntity)).GetProperty("code").GetString()).IsEqualTo("sort_noncollection");
        await Assert.That(source.Opens).IsEqualTo(0);
    }

    [Test]
    public async Task DocumentFactsAreProvidedAsExactMemoryAndEvaluatedByCore()
    {
        var source = new ControlledSource("one", "v1", ["a"]);
        await using var app = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(app);
        using var response = await client.GetAsync("/registry/dirs/g/files?filter=filebase64%3DAP8NCkE%3D");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(string.Join(",", (await BridgeFixture.Json(response)).EnumerateObject().Select(property => property.Name))).IsEqualTo("a");
        await Assert.That(source.Reads.Any(read => read.Operation == FederationOperation.Document && read.Target == "/dirs/g/files/a/versions/v1")).IsTrue();
    }

    [Test]
    public async Task SharedWorkBudgetAndMissingMetaInputsCannotBecomeAnEmptySuccessfulQuery()
    {
        var source = new ControlledSource("one", "v1", ["a"]);
        await using var limited = await BridgeFixture.StartAsync([source.Registration()], BridgeFixture.Options with
        {
            QueryLimits = new RegistryQueryEvaluationLimits { MaxWork = 1 }
        });
        using var limitedClient = BridgeFixture.Client(limited);
        using var exhausted = await limitedClient.GetAsync("/registry/dirs/g/files?filter=fileid%3Da");
        await Assert.That(exhausted.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That((await BridgeFixture.Json(exhausted)).GetProperty("code").GetString()).IsEqualTo("too_large");
        await Assert.That(source.Opens).IsEqualTo(0);
        source.Override = async (request, _) =>
        {
            var response = await source.ReadDefault(request);
            if (!request.Target.EndsWith("/meta", StringComparison.Ordinal)) { return response; }
            var value = JsonNode.Parse(response.Metadata.GetRawText())!.AsObject();
            value["entity"]!.AsObject().Remove("defaultversionid");
            return FederationReadResult.FromMetadata(response.SelectedXid, response.Context, RegistryJson.Parse(value.ToJsonString()).RootElement);
        };
        await using var incomplete = await BridgeFixture.StartAsync([source.Registration()]);
        using var client = BridgeFixture.Client(incomplete);
        using var rejected = await client.GetAsync("/registry/dirs/g/files?filter=fileid%3Da");
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
        await Assert.That((await BridgeFixture.Json(rejected)).TryGetProperty("code", out _)).IsTrue();
    }
}
