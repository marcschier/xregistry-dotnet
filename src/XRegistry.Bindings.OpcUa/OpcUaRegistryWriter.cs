// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.ExceptionServices;
using System.Text.Json;
using Opc.Ua;
using XRegistry.Federation;

namespace XRegistry.Bindings.OpcUa;

/// <summary>Writes Registry Properties, Methods, attributes, and document bytes over OPC UA.</summary>
public sealed class OpcUaRegistryWriter
{
    private const byte WriteAndEraseMode = 6;
    private readonly OpcUaRegistryReader reader;
    private readonly IOpcUaRegistrySession session;

    /// <summary>Creates a writer that uses the reader's validated model, session, and transfer limits.</summary>
    public OpcUaRegistryWriter(OpcUaRegistryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        this.reader = reader;
        session = reader.Session;
    }

    /// <summary>Writes a JSON scalar to a Property Variable.</summary>
    public async ValueTask WritePropertyAsync(NodeId variable, OpcUaJsonValue value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        reader.EnsureSpecVersionValidatedForWrite();
        await session.WriteValueAsync(variable, ToVariant(value), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a Group by calling CreateGroup on its Registry.</summary>
    public async ValueTask<NodeId> CreateGroupAsync(NodeId registry, string groupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupId);
        reader.EnsureSpecVersionValidatedForWrite();
        var outputs = await session.CallMethodAsync(registry, "CreateGroup", [new Variant(groupId)], cancellationToken)
            .ConfigureAwait(false);
        RequireOutputCount(outputs, 1, "CreateGroup");
        return ReadNodeIdOutput(outputs, 0, "CreateGroup");
    }

    /// <summary>Gets or creates a Group by calling GetOrCreateGroup on its Registry.</summary>
    public async ValueTask<OpcUaGroupResolution> GetOrCreateGroupAsync(NodeId registry, string groupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupId);
        reader.EnsureSpecVersionValidatedForWrite();
        var outputs = await session.CallMethodAsync(registry, "GetOrCreateGroup",
            [new Variant(groupId)], cancellationToken).ConfigureAwait(false);
        RequireOutputCount(outputs, 2, "GetOrCreateGroup");
        return new(ReadNodeIdOutput(outputs, 0, "GetOrCreateGroup"),
            ReadBooleanOutput(outputs, 1, "GetOrCreateGroup"));
    }

    /// <summary>Creates a resource or version by calling CreateResource on its Group.</summary>
    public async ValueTask<OpcUaResourceCreation> CreateResourceAsync(NodeId group, string resourceId,
        string? versionId = null, bool requestFileOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceId);
        reader.EnsureSpecVersionValidatedForWrite();
        var outputs = await session.CallMethodAsync(group, "CreateResource",
            [new Variant(resourceId), new Variant(versionId ?? string.Empty), new Variant(requestFileOpen)],
            cancellationToken).ConfigureAwait(false);
        RequireOutputCount(outputs, 3, "CreateResource");
        return new(ReadNodeIdOutput(outputs, 0, "CreateResource"),
            ReadStringOutput(outputs, 1, "CreateResource"),
            ReadUInt32Output(outputs, 2, "CreateResource"));
    }

    /// <summary>Gets or creates a resource or version by calling GetOrCreateResource on its Group.</summary>
    public async ValueTask<OpcUaResourceResolution> GetOrCreateResourceAsync(NodeId group, string resourceId,
        string? versionId = null, bool requestFileOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceId);
        reader.EnsureSpecVersionValidatedForWrite();
        var outputs = await session.CallMethodAsync(group, "GetOrCreateResource",
            [new Variant(resourceId), new Variant(versionId ?? string.Empty), new Variant(requestFileOpen)],
            cancellationToken).ConfigureAwait(false);
        RequireOutputCount(outputs, 4, "GetOrCreateResource");
        return new(ReadNodeIdOutput(outputs, 0, "GetOrCreateResource"),
            ReadStringOutput(outputs, 1, "GetOrCreateResource"),
            ReadUInt32Output(outputs, 2, "GetOrCreateResource"),
            ReadBooleanOutput(outputs, 3, "GetOrCreateResource"));
    }

    /// <summary>Deletes a Group, resource, or version with an optional expected Epoch.</summary>
    public ValueTask DeleteAsync(NodeId entity, uint? expectedEpoch = null,
        CancellationToken cancellationToken = default)
    {
        reader.EnsureSpecVersionValidatedForWrite();
        return CallWithoutOutputsAsync(entity, "Delete",
            expectedEpoch is uint epoch ? [new Variant(epoch)] : [], cancellationToken);
    }

    /// <summary>Adds or updates one label or extension attribute using optimistic concurrency.</summary>
    public ValueTask AddAttributeAsync(NodeId labels, string key, string value, uint expectedEpoch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        reader.EnsureSpecVersionValidatedForWrite();
        return CallWithoutOutputsAsync(labels, "AddAttribute",
            [new Variant(key), new Variant(value), new Variant(expectedEpoch)], cancellationToken);
    }

    /// <summary>Removes one label or extension attribute using optimistic concurrency.</summary>
    public ValueTask RemoveAttributeAsync(NodeId labels, string key, uint expectedEpoch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        reader.EnsureSpecVersionValidatedForWrite();
        return CallWithoutOutputsAsync(labels, "RemoveAttribute",
            [new Variant(key), new Variant(expectedEpoch)], cancellationToken);
    }

    /// <summary>Adds or updates one Resource Meta label or extension attribute using MetaEpoch.</summary>
    public ValueTask AddMetaAttributeAsync(NodeId metaLabels, string key, string value, uint expectedMetaEpoch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        reader.EnsureSpecVersionValidatedForWrite();
        return CallWithoutOutputsAsync(metaLabels, "AddAttribute",
            [new Variant(key), new Variant(value), new Variant(expectedMetaEpoch)], cancellationToken);
    }

    /// <summary>Removes one Resource Meta label or extension attribute using MetaEpoch.</summary>
    public ValueTask RemoveMetaAttributeAsync(NodeId metaLabels, string key, uint expectedMetaEpoch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        reader.EnsureSpecVersionValidatedForWrite();
        return CallWithoutOutputsAsync(metaLabels, "RemoveAttribute",
            [new Variant(key), new Variant(expectedMetaEpoch)], cancellationToken);
    }

    /// <summary>Replaces a document-bearing resource's complete byte stream.</summary>
    public async ValueTask ReplaceResourceDocumentAsync(NodeId groupNode, string groupXid,
        string resourceCollection, string resourceId, NodeId resourceType, ReadOnlyMemory<byte> contents,
        string? versionId = null, NodeId? resourceVersionsType = null,
        CancellationToken cancellationToken = default)
    {
        reader.EnsureSpecVersionValidatedForWrite();
        if (contents.Length > reader.MaxBytes)
        {
            throw new FederationException(FederationErrorCode.LimitExceeded, "The document exceeds the byte budget.");
        }

        var file = await reader.ResolveResourceDocumentAsync(groupNode, groupXid, resourceCollection, resourceId,
            resourceType, versionId, resourceVersionsType, cancellationToken).ConfigureAwait(false);
        await ReplaceFileDocumentAsync(file, contents, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces the complete byte stream of a supplied FileType node.</summary>
    public async ValueTask ReplaceFileDocumentAsync(NodeId file, ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default)
    {
        reader.EnsureSpecVersionValidatedForWrite();
        if (contents.Length > reader.MaxBytes)
        {
            throw new FederationException(FederationErrorCode.LimitExceeded, "The document exceeds the byte budget.");
        }
        var handle = await session.OpenAsync(file, WriteAndEraseMode, cancellationToken).ConfigureAwait(false);
        await WriteOpenFileAsync(file, handle, contents, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes and closes a handle returned by CreateResource with RequestFileOpen set.</summary>
    public async ValueTask CompleteCreatedResourceDocumentAsync(NodeId resource, uint handle,
        ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default)
    {
        reader.EnsureSpecVersionValidatedForWrite();
        if (contents.Length > reader.MaxBytes)
        {
            throw new FederationException(FederationErrorCode.LimitExceeded, "The document exceeds the byte budget.");
        }
        await WriteOpenFileAsync(resource, handle, contents, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteOpenFileAsync(NodeId file, uint handle, ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await session.SetPositionAsync(file, handle, 0, cancellationToken).ConfigureAwait(false);
            for (var offset = 0; offset < contents.Length;)
            {
                var length = Math.Min(reader.ChunkSize, contents.Length - offset);
                var chunk = contents.Slice(offset, length).ToArray();
                await session.WriteAsync(file, handle, chunk, cancellationToken).ConfigureAwait(false);
                offset += length;
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await session.CloseAsync(file, handle, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception closeFailure) when (failure is not null)
        {
            failure.Data["OpcUa.FileCloseFailure"] = closeFailure;
        }
        catch (Exception exception)
        {
            throw new FederationException(FederationErrorCode.Unavailable,
                "The file could not be closed after document replacement.", innerException: exception);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private async ValueTask CallWithoutOutputsAsync(NodeId target, string methodBrowseName,
        IReadOnlyList<Variant> inputArguments, CancellationToken cancellationToken)
    {
        var outputs = await session.CallMethodAsync(target, methodBrowseName, inputArguments, cancellationToken)
            .ConfigureAwait(false);
        RequireOutputCount(outputs, 0, methodBrowseName);
    }

    private static Variant ToVariant(OpcUaJsonValue value)
    {
        try
        {
            return value.BuiltInType switch
            {
                BuiltInType.String => new Variant(value.Value.GetString() ?? throw new InvalidOperationException()),
                BuiltInType.Boolean => new Variant(value.Value.GetBoolean()),
                BuiltInType.SByte => new Variant(value.Value.GetSByte()),
                BuiltInType.Byte => new Variant(value.Value.GetByte()),
                BuiltInType.Int16 => new Variant(value.Value.GetInt16()),
                BuiltInType.UInt16 => new Variant(value.Value.GetUInt16()),
                BuiltInType.Int32 => new Variant(value.Value.GetInt32()),
                BuiltInType.UInt32 => new Variant(value.Value.GetUInt32()),
                BuiltInType.Int64 => new Variant(value.Value.GetInt64()),
                BuiltInType.UInt64 => new Variant(value.Value.GetUInt64()),
                BuiltInType.Float => new Variant(value.Value.GetSingle()),
                BuiltInType.Double => new Variant(value.Value.GetDouble()),
                BuiltInType.DateTime => new Variant(ToUtc(value.Value.GetDateTime())),
                _ => throw new FederationException(FederationErrorCode.UnsupportedOperation,
                    "The OPC UA DataType has no JSON scalar mapping.")
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or OverflowException)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "The JSON scalar does not match the OPC UA DataType.", innerException: exception);
        }
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    private static NodeId ReadNodeIdOutput(IReadOnlyList<Variant> outputs, int index, string method) =>
        outputs.Count > index && outputs[index].AsBoxedObject() is NodeId value && !value.IsNull
            ? value
            : throw new FederationException(FederationErrorCode.InvalidPackage,
                $"{method} returned an invalid output argument.");

    private static uint ReadUInt32Output(IReadOnlyList<Variant> outputs, int index, string method) =>
        outputs.Count > index && outputs[index].TryGetValue(out uint value)
            ? value
            : throw new FederationException(FederationErrorCode.InvalidPackage,
                $"{method} returned an invalid output argument.");

    private static bool ReadBooleanOutput(IReadOnlyList<Variant> outputs, int index, string method) =>
        outputs.Count > index && outputs[index].TryGetValue(out bool value)
            ? value
            : throw new FederationException(FederationErrorCode.InvalidPackage,
                $"{method} returned an invalid output argument.");

    private static string ReadStringOutput(IReadOnlyList<Variant> outputs, int index, string method) =>
        outputs.Count > index && outputs[index].TryGetValue(out string? value) && value is not null
            ? value
            : throw new FederationException(FederationErrorCode.InvalidPackage,
                $"{method} returned an invalid output argument.");

    private static void RequireOutputCount(IReadOnlyList<Variant> outputs, int expected, string method)
    {
        if (outputs.Count != expected)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                $"{method} returned an unexpected number of output arguments.");
        }
    }
}

/// <summary>A Group returned by GetOrCreateGroup.</summary>
public sealed record OpcUaGroupResolution(NodeId GroupNode, bool Created);

/// <summary>A ResourceType node returned by CreateResource.</summary>
public sealed record OpcUaResourceCreation(NodeId ResourceNode, string VersionId, uint FileHandle);

/// <summary>A ResourceType node returned by GetOrCreateResource.</summary>
public sealed record OpcUaResourceResolution(NodeId ResourceNode, string VersionId, uint FileHandle, bool Created);
