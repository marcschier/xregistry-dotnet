// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using Opc.Ua;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.OpcUa;
using XRegistry.Federation;

namespace XRegistry.OpcUa.Tests;

public class OpcUaRegistryEventDecoderTests
{
    private static readonly NodeId s_resourceUpdated = new(5101u, 2);
    private static readonly NodeId s_groupCreated = new(5102u, 2);
    private static readonly NodeId s_resourceDeleted = new(5103u, 2);
    private static readonly NodeId s_modelUpdated = new(5104u, 2);
    private static readonly NodeId s_unknown = new(5199u, 2);
    private static readonly string[] s_changed = ["description", "name"];
    private static readonly string[] s_singleChanged = ["name"];
    private static readonly string[] s_duplicateChanged = ["name", "name"];

    [Test]
    public async Task ResourceUpdatedDecodesNativeFieldsAndCloudEventIdentity()
    {
        var decoder = Decoder(
            (s_resourceUpdated, "io.xregistry.resource.updated"));
        var time = new DateTimeOffset(2026, 9, 15, 12, 30, 45, TimeSpan.Zero);
        var notification = Notification(s_resourceUpdated, "/teams/g/files/f", eventId: [1, 2, 3],
            sourceUrl: "https://registry.example.test", sourceName: "files/f",
            epoch: 42u, metaEpoch: 9u, changed: s_changed, correlationId: "interaction-7",
            time: time);

        var found = decoder.TryDecode(notification, out var decoded);

        await Assert.That(found).IsTrue();
        await Assert.That(decoded).IsNotNull();
        await Assert.That(decoded!.Type).IsEqualTo("io.xregistry.resource.updated");
        await Assert.That(decoded.Id).IsEqualTo("AQID");
        await Assert.That(decoded.SourceNode).IsEqualTo(new NodeId(87u, 2));
        await Assert.That(decoded.SourceName).IsEqualTo("files/f");
        await Assert.That(decoded.SourceUrl).IsEqualTo("https://registry.example.test");
        await Assert.That(decoded.Subject).IsEqualTo("/teams/g/files/f");
        await Assert.That(decoded.Time).IsEqualTo(time);
        await Assert.That(decoded.Epoch).IsEqualTo(42u);
        await Assert.That(decoded.MetaEpoch).IsEqualTo(9u);
        await Assert.That(decoded.Changed).IsEquivalentTo(["description", "name"]);
        await Assert.That(decoded.CorrelationId).IsEqualTo("interaction-7");
    }

    [Test]
    public async Task GroupCreatedAllowsOnlyItsRequiredEpoch()
    {
        var decoder = Decoder((s_groupCreated, "io.xregistry.group.created"));
        var notification = Notification(s_groupCreated, "/teams/g", eventId: [8], epoch: 0u);

        var found = decoder.TryDecode(notification, out var decoded);

        await Assert.That(found).IsTrue();
        await Assert.That(decoded).IsNotNull();
        await Assert.That(decoded!.Epoch).IsEqualTo(0u);
        await Assert.That(decoded.MetaEpoch).IsNull();
        await Assert.That(decoded.Changed).IsNull();
        await Assert.That(decoded.CorrelationId).IsNull();
    }

    [Test]
    public async Task UnknownEventTypeIsIgnored()
    {
        var decoder = Decoder((s_resourceUpdated, "io.xregistry.resource.updated"));
        var notification = Notification(s_unknown, "/invalid", eventId: []);

        var found = decoder.TryDecode(notification, out var decoded);

        await Assert.That(found).IsFalse();
        await Assert.That(decoded).IsNull();
    }

    [Test]
    public async Task NotificationMustContainEverySelectedField()
    {
        var decoder = Decoder((s_resourceUpdated, "io.xregistry.resource.updated"));
        var notification = new EventFieldList
        {
            EventFields = new ArrayOf<Variant>(new[] { new Variant(s_resourceUpdated) })
        };

        await Check.Error(() => decoder.TryDecode(notification, out _), FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task EventTypeFieldMustBeANodeId()
    {
        var decoder = Decoder((s_resourceUpdated, "io.xregistry.resource.updated"));
        var notification = Notification(s_resourceUpdated, "/teams/g/files/f", eventId: [1], typeField: new Variant(5u));

        await Check.Error(() => decoder.TryDecode(notification, out _), FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task ResourceUpdatedRequiresUInt32EpochAndMetaEpoch()
    {
        var decoder = Decoder((s_resourceUpdated, "io.xregistry.resource.updated"));
        var missingEpoch = Notification(s_resourceUpdated, "/teams/g/files/f", eventId: [1], metaEpoch: 1u);
        var missingMetaEpoch = Notification(s_resourceUpdated, "/teams/g/files/f", eventId: [1], epoch: 1u);
        var wrongEpochType = Notification(s_resourceUpdated, "/teams/g/files/f", eventId: [1],
            epoch: new Variant(1), metaEpoch: 1u);

        await Check.Error(() => decoder.TryDecode(missingEpoch, out _), FederationErrorCode.InvalidPackage);
        await Check.Error(() => decoder.TryDecode(missingMetaEpoch, out _), FederationErrorCode.InvalidPackage);
        await Check.Error(() => decoder.TryDecode(wrongEpochType, out _), FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task ResourceDeletedRejectsEpochMetaEpochAndChanged()
    {
        var decoder = Decoder((s_resourceDeleted, "io.xregistry.resource.deleted"));
        var notification = Notification(s_resourceDeleted, "/teams/g/files/f", eventId: [1]);
        var withEpoch = Notification(s_resourceDeleted, "/teams/g/files/f", eventId: [1], epoch: 2u);
        var withZeroEpoch = Notification(s_resourceDeleted, "/teams/g/files/f", eventId: [1], epoch: 0u);
        var withMetaEpoch = Notification(s_resourceDeleted, "/teams/g/files/f", eventId: [1], metaEpoch: 3u);
        var withChanged = Notification(s_resourceDeleted, "/teams/g/files/f", eventId: [1],
            changed: s_singleChanged);

        await Check.Error(() => decoder.TryDecode(withEpoch, out _), FederationErrorCode.InvalidPackage);
        await Check.Error(() => decoder.TryDecode(withZeroEpoch, out _), FederationErrorCode.InvalidPackage);
        await Check.Error(() => decoder.TryDecode(withMetaEpoch, out _), FederationErrorCode.InvalidPackage);
        await Check.Error(() => decoder.TryDecode(withChanged, out _), FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task ChangedMustBeAnUniqueStringArray()
    {
        var decoder = Decoder((s_resourceUpdated, "io.xregistry.resource.updated"));
        var wrongType = Notification(s_resourceUpdated, "/teams/g/files/f", eventId: [1],
            epoch: 1u, metaEpoch: 1u, changed: new Variant("name"));
        var duplicate = Notification(s_resourceUpdated, "/teams/g/files/f", eventId: [1],
            epoch: 1u, metaEpoch: 1u, changed: s_duplicateChanged);

        await Check.Error(() => decoder.TryDecode(wrongType, out _), FederationErrorCode.InvalidPackage);
        await Check.Error(() => decoder.TryDecode(duplicate, out _), FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task SubjectMustMatchEventEntityAndSourceUrlMustBeAbsolute()
    {
        var decoder = Decoder((s_resourceUpdated, "io.xregistry.resource.updated"));
        var wrongSubject = Notification(s_resourceUpdated, "/teams/g", eventId: [1], epoch: 1u, metaEpoch: 1u);
        var relativeSource = Notification(s_resourceUpdated, "/teams/g/files/f", eventId: [1],
            sourceUrl: "/registry", epoch: 1u, metaEpoch: 1u);

        await Check.Error(() => decoder.TryDecode(wrongSubject, out _), FederationErrorCode.InvalidPackage);
        await Check.Error(() => decoder.TryDecode(relativeSource, out _), FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task RequiredNativeFieldsRejectWrongTypesAndEmptyEventIds()
    {
        var decoder = Decoder((s_resourceUpdated, "io.xregistry.resource.updated"));
        var valid = Notification(s_resourceUpdated, "/teams/g/files/f", eventId: [1], epoch: 1u, metaEpoch: 1u);
        var emptyEventId = Notification(s_resourceUpdated, "/teams/g/files/f", eventId: []);
        var wrongEventId = WithField(valid, 0, new Variant("id"));
        var wrongSourceNode = WithField(valid, 2, new Variant("node"));
        var wrongSourceName = WithField(valid, 3, new Variant(2u));
        var wrongTime = WithField(valid, 4, new Variant("time"));
        var wrongSourceUrl = WithField(valid, 5, new Variant(2u));
        var wrongSubject = WithField(valid, 6, new Variant(2u));

        await Check.Error(() => decoder.TryDecode(emptyEventId, out _), FederationErrorCode.InvalidPackage);
        await Check.Error(() => decoder.TryDecode(wrongEventId, out _), FederationErrorCode.InvalidPackage);
        await Check.Error(() => decoder.TryDecode(wrongSourceNode, out _), FederationErrorCode.InvalidPackage);
        await Check.Error(() => decoder.TryDecode(wrongSourceName, out _), FederationErrorCode.InvalidPackage);
        await Check.Error(() => decoder.TryDecode(wrongTime, out _), FederationErrorCode.InvalidPackage);
        await Check.Error(() => decoder.TryDecode(wrongSourceUrl, out _), FederationErrorCode.InvalidPackage);
        await Check.Error(() => decoder.TryDecode(wrongSubject, out _), FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task ModelUpdatedUsesItsFixedSubjectAndCannotContainChanged()
    {
        var decoder = Decoder((s_modelUpdated, "io.xregistry.model.updated"));
        var valid = Notification(s_modelUpdated, "/model", eventId: [1]);
        var withChanged = Notification(s_modelUpdated, "/model", eventId: [1], changed: s_singleChanged);
        var malformedSubject = Notification(s_modelUpdated, "/model/details", eventId: [1]);
        var malformedPath = Notification(s_modelUpdated, "/teams//files/f", eventId: [1]);

        var found = decoder.TryDecode(valid, out var decoded);

        await Assert.That(found).IsTrue();
        await Assert.That(decoded!.Epoch).IsNull();
        await Check.Error(() => decoder.TryDecode(withChanged, out _), FederationErrorCode.InvalidPackage);
        await Check.Error(() => decoder.TryDecode(malformedSubject, out _), FederationErrorCode.InvalidPackage);
        await Check.Error(() => decoder.TryDecode(malformedPath, out _), FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task PresentCorrelationIdMustNotBeEmpty()
    {
        var decoder = Decoder((s_resourceUpdated, "io.xregistry.resource.updated"));
        var notification = Notification(s_resourceUpdated, "/teams/g/files/f", eventId: [1],
            epoch: 1u, metaEpoch: 1u, correlationId: "");
        var wrongDefaultType = Notification(s_resourceUpdated, "/teams/g/files/f", eventId: [1],
            epoch: 1u, metaEpoch: 1u, correlationId: new Variant(false));

        await Check.Error(() => decoder.TryDecode(notification, out _), FederationErrorCode.InvalidPackage);
        await Check.Error(() => decoder.TryDecode(wrongDefaultType, out _), FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task ConstructorRejectsNonCanonicalEventTypes()
    {
        await Assert.That(() => Decoder((s_resourceUpdated, "io.xregistry.resource.created.extra")))
            .Throws<ArgumentException>();
    }

    private static OpcUaRegistryEventDecoder Decoder(params (NodeId NodeId, string Type)[] mappings) =>
        new(mappings.ToDictionary(static mapping => mapping.NodeId, static mapping => mapping.Type));

    private static EventFieldList Notification(NodeId eventType, string subject, byte[] eventId,
        string sourceUrl = "https://registry.example.test", string sourceName = "entity",
        object? epoch = null, object? metaEpoch = null, object? changed = null,
        object? correlationId = null, DateTimeOffset? time = null, Variant? typeField = null) =>
        new()
        {
            EventFields = new ArrayOf<Variant>(new Variant[]
            {
                new Variant(new ByteString(eventId)),
                typeField ?? new Variant(eventType),
                new Variant(new NodeId(87u, 2)),
                new Variant(sourceName),
                new Variant(new DateTimeUtc(time ?? DateTimeOffset.UnixEpoch)),
                new Variant(sourceUrl),
                new Variant(subject),
                Field(epoch),
                Field(metaEpoch),
                ChangedField(changed),
                Field(correlationId),
            })
        };

    private static EventFieldList WithField(EventFieldList notification, int index, Variant value)
    {
        var fields = notification.EventFields.ToArray() ?? [];
        fields[index] = value;
        return new EventFieldList { EventFields = new ArrayOf<Variant>(fields) };
    }

    private static Variant Field(object? value) => value switch
    {
        null => default,
        uint number => new Variant(number),
        int number => new Variant(number),
        string text => new Variant(text),
        Variant variant => variant,
        _ => throw new ArgumentException("Unsupported test field type.", nameof(value))
    };

    private static Variant ChangedField(object? value) => value switch
    {
        null => default,
        string[] array => new Variant(new ArrayOf<string>(array)),
        string text => new Variant(text),
        Variant variant => variant,
        _ => throw new ArgumentException("Unsupported test field type.", nameof(value))
    };
}
