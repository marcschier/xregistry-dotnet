// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class ResourceFederationResolverTests
{
    private const string Resource = "/groups/main/resources/item";

    [Test]
    public async Task MissingLocalVersionNeverFallsThroughToARemoteResource()
    {
        var local = new Source("local", request => request.Target == Resource
            ? Entity(Resource) : throw Missing());
        var remote = new Source("remote", _ => Entity(Resource + "/versions/v2"));
        var resolver = new ResourceFederationResolver(local, [remote], static (_, _) => true);
        await Assert.That(async () => await resolver.ReadAsync(new(FederationOperation.Entity, Resource + "/versions/v2")))
            .Throws<FederationException>();
        await Assert.That(local.Reads).IsEqualTo(2);
        await Assert.That(remote.Reads).IsEqualTo(0);
    }

    [Test]
    public async Task FirstFoundRemoteRetainsItsOriginForTheRequestedVersion()
    {
        var local = new Source("local", _ => throw Missing());
        var first = new Source("first", request => Entity(request.Target));
        var last = new Source("last", _ => throw new InvalidOperationException("Later sources must not be read."));
        var resolver = new ResourceFederationResolver(local, [first, last], static (_, _) => true);
        var result = await resolver.ReadAsync(new(FederationOperation.Entity, Resource + "/versions/v3"));
        await Assert.That(result.Selected!.Context.Source).IsEqualTo("urn:test:first");
        await Assert.That(result.Selected.SelectedXid).IsEqualTo(Resource + "/versions/v3");
        await Assert.That(first.Reads).IsEqualTo(2);
        await Assert.That(last.Reads).IsEqualTo(0);
    }

    [Test]
    public async Task ProducerViewIsReadExactlyOnceWithoutProbingSources()
    {
        var local = new Source("producer", request => Entity(request.Target))
        {
            Capabilities = RegistryJson.Parse("""{"federation":{"resolution":"producer"}}""").RootElement
        };
        var remote = new Source("remote", _ => throw new InvalidOperationException("Producer catalog must not be traversed."));
        var resolver = new ResourceFederationResolver(local, [remote], static (_, _) => true);
        var result = await resolver.ReadAsync(new(FederationOperation.Entity, Resource + "/versions/v1"));
        await Assert.That(result.Selected!.SelectedXid).IsEqualTo(Resource + "/versions/v1");
        await Assert.That(local.Reads).IsEqualTo(1);
        await Assert.That(remote.Reads).IsEqualTo(0);
    }

    [Test]
    public async Task PolicyDenialIsNotAResourceMissAndUnorderedCompetitionIsAmbiguous()
    {
        var local = new Source("local", _ => throw Missing());
        var denied = new Source("denied", _ => throw new FederationException(FederationErrorCode.PolicyDenied, "Denied"));
        var other = new Source("other", _ => Entity(Resource));
        var resolver = new ResourceFederationResolver(local, [denied, other], static (_, _) => true);
        var error = await Assert.That(async () => await resolver.ReadAsync(new(FederationOperation.Entity, Resource)))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected denial.");
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(other.Reads).IsEqualTo(0);

        var first = new Source("first", _ => Entity(Resource));
        var unordered = new ResourceFederationResolver(local, [first, other], static (_, _) => true, sourcesAreOrdered: false);
        var ambiguity = await Assert.That(async () => await unordered.ReadAsync(new(FederationOperation.Entity, Resource)))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected ambiguity.");
        await Assert.That(ambiguity.Code).IsEqualTo(FederationErrorCode.Ambiguous);
    }

    [Test]
    public async Task CollectionShadowingPrecedesLabelsAndLaterSourcesStillAffectUniqueness()
    {
        const string collection = "/groups/main/resources";
        var local = new Source("local", _ => Collection("""
            {"item":{"xid":"/groups/main/resources/item","labels":{"stage":"development"}}}
            """));
        var first = new Source("first", _ => Collection("""
            {"item":{"xid":"/groups/main/resources/item","labels":{"stage":"production"}},
             "other":{"xid":"/groups/main/resources/other","labels":{"stage":"production"}}}
            """));
        var request = new FederationReadRequest(FederationOperation.Collection, collection,
            new FederationLabelSelector("stage", "production"));
        var resolver = new ResourceFederationResolver(local, [first], static (_, _) => true);
        var result = await resolver.ReadAsync(request);
        await Assert.That(result.Selected!.SelectedXid).IsEqualTo(collection + "/other");
        await Assert.That(result.Selected.Context.Source).IsEqualTo("urn:test:first");

        var later = new Source("later", _ => Collection("""
            {"third":{"xid":"/groups/main/resources/third","labels":{"stage":"production"}}}
            """));
        var ambiguous = new ResourceFederationResolver(local, [first, later], static (_, _) => true);
        var error = await Assert.That(async () => await ambiguous.ReadAsync(request))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected complete-view ambiguity.");
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.Ambiguous);
        await Assert.That(later.Reads).IsEqualTo(1);
    }

    [Test]
    public async Task ProducerMissCannotFallBackAndIncompleteCollectionsCannotClaimUniqueness()
    {
        var producer = new Source("producer", _ => throw Missing())
        {
            Capabilities = RegistryJson.Parse("""{"federation":{"resolution":"producer"}}""").RootElement
        };
        var remote = new Source("remote", _ => Entity(Resource));
        var resolver = new ResourceFederationResolver(producer, [remote], static (_, _) => true);
        var missing = await Assert.That(async () => await resolver.ReadAsync(new(FederationOperation.Entity, Resource)))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected producer miss.");
        await Assert.That(missing.Code).IsEqualTo(FederationErrorCode.NotFound);
        await Assert.That(remote.Reads).IsEqualTo(0);

        var incomplete = new Source("incomplete", _ => RegistryJson.Parse("""
            {"kind":"collection","xid":"/groups/main/resources","complete":false,"entities":{}}
            """).RootElement);
        var collection = new ResourceFederationResolver(incomplete, [], static (_, _) => true);
        var failure = await Assert.That(async () => await collection.ReadAsync(
            new(FederationOperation.Collection, "/groups/main/resources")))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected incomplete source rejection.");
        await Assert.That(failure.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
    }

    [Test]
    public async Task ContextMutationIsNotAcceptedAsTheSelectedSnapshot()
    {
        Source? source = null;
        source = new Source("before", request =>
        {
            source!.Context = new NativeRegistryContext("test", "urn:test:after");
            return Entity(request.Target);
        });
        var resolver = new ResourceFederationResolver(source, [], static (_, _) => true);
        var error = await Assert.That(async () => await resolver.ReadAsync(new(FederationOperation.Entity, Resource)))
            .Throws<FederationException>() ?? throw new InvalidOperationException("Expected context mutation rejection.");
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
    }

    [Test]
    public async Task CaseCollidingOriginsCannotFormAValidConsumerCollection()
    {
        var local = new Source("local", _ => Collection("""{"item":{"xid":"/groups/main/resources/item"}}"""));
        var other = new Source("other", _ => Collection("""{"ITEM":{"xid":"/groups/main/resources/ITEM"}}"""));
        var resolver = new ResourceFederationResolver(local, [other], static (_, _) => true);
        var error = await Assert.That(async () => await resolver.ReadAsync(new(FederationOperation.Collection,
            "/groups/main/resources"))).Throws<FederationException>()
            ?? throw new InvalidOperationException("Expected a case-colliding visible view to fail.");
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.Ambiguous);
    }

    [Test]
    public async Task AReadSourceCannotReturnACollectionForAnotherTypedPath()
    {
        var local = new Source("local", _ => RegistryJson.Parse(
            """{"kind":"collection","xid":"/other/g/things","complete":true,"entities":{}}""").RootElement);
        var resolver = new ResourceFederationResolver(local, [], static (_, _) => true);
        var error = await Assert.That(async () => await resolver.ReadAsync(new(FederationOperation.Collection,
            "/groups/main/resources"))).Throws<FederationException>()
            ?? throw new InvalidOperationException("Expected wrong collection identity to fail.");
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task ShadowingCannotTurnConsumedMalformedSourceMetadataIntoSuccess()
    {
        var local = new Source("local", _ => Collection("""{"item":{"xid":"/groups/main/resources/item"}}"""));
        var other = new Source("other", _ => Collection("""{"item":{"xid":"/groups/main/resources/wrong"}}"""));
        var resolver = new ResourceFederationResolver(local, [other], static (_, _) => true);
        var error = await Assert.That(async () => await resolver.ReadAsync(new(FederationOperation.Collection,
            "/groups/main/resources"))).Throws<FederationException>()
            ?? throw new InvalidOperationException("Expected consumed invalid metadata to fail.");
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task CancellationAfterAReadCannotAcceptAnUncooperativeSourcesResult()
    {
        using var cancellation = new CancellationTokenSource();
        var local = new Source("local", request =>
        {
            cancellation.Cancel();
            return Entity(request.Target);
        });
        var other = new Source("other", _ => throw new InvalidOperationException("Cancellation must not traverse another source."));
        var resolver = new ResourceFederationResolver(local, [other], static (_, _) => true);
        await Assert.That(async () => await resolver.ReadAsync(new(FederationOperation.Entity, Resource), cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(other.Reads).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task SelectedOriginCannotChangeBetweenAResourceProbeAndItsVersionRead(bool changeOwner, bool version)
    {
        var local = new Source("local", _ => throw Missing());
        var selected = new Source("selected", request => Entity(request.Target));
        var later = new Source("later", _ => throw new InvalidOperationException("No selected-origin fallback is permitted."));
        var resolver = new ResourceFederationResolver(local, [selected, later], (_, _) =>
        {
            if (changeOwner)
            {
                selected.Capabilities = RegistryJson.Parse("""{"federation":{"resolution":"producer"}}""").RootElement;
            }
            else
            {
                selected.Context = new NativeRegistryContext("test", "urn:test:another-revision");
            }
            return true;
        });

        var error = await Assert.That(async () => await resolver.ReadAsync(
            new(FederationOperation.Entity, Resource + (version ? "/versions/v1" : "")))).Throws<FederationException>()
            ?? throw new InvalidOperationException("Expected the source capture to remain fixed across reads.");
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        await Assert.That(selected.Reads).IsEqualTo(1);
        await Assert.That(later.Reads).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ACompleteCollectionCannotMixAnEarlierSourceWhoseCaptureChanged(bool select)
    {
        var local = new Source("local", _ => Collection("""{}"""));
        var remote = new Source("remote", _ =>
        {
            local.Context = new NativeRegistryContext("test", "urn:test:changed-local");
            return Collection("""{"item":{"xid":"/groups/main/resources/item","labels":{"stage":"production"}}}""");
        });
        var resolver = new ResourceFederationResolver(local, [remote], static (_, _) => true);
        var error = await Assert.That(async () => await resolver.ReadAsync(new(FederationOperation.Collection,
            "/groups/main/resources", select ? new("stage", "production") : null))).Throws<FederationException>()
            ?? throw new InvalidOperationException("Expected a complete collection to retain every consumed source capture.");
        await Assert.That(error.Code).IsEqualTo(FederationErrorCode.InconsistentSnapshot);
        await Assert.That(local.Reads).IsEqualTo(1);
        await Assert.That(remote.Reads).IsEqualTo(1);
    }

    private static JsonElement Entity(string xid) => RegistryJson.Parse("{\"xid\":\"" + xid + "\"}").RootElement;
    private static JsonElement Collection(string entities) => RegistryJson.Parse(
        """{"kind":"collection","xid":"/groups/main/resources","complete":true,"entities":""" + entities + "}").RootElement;
    private static FederationException Missing() => new(FederationErrorCode.NotFound, "Absent");

    private sealed class Source(string name, Func<FederationReadRequest, JsonElement> read) : IFederationReadSource
    {
        public NativeRegistryContext Context { get; set; } = new("test", "urn:test:" + name);
        public JsonElement Capabilities { get; set; } = RegistryJson.Parse("{}").RootElement;
        internal int Reads { get; private set; }

        public ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return ValueTask.FromResult(FederationReadResult.FromMetadata(request.Target, Context, read(request)));
        }
    }
}
