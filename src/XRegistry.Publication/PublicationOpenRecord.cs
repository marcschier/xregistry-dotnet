// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace XRegistry.Publication;

/// <summary>The selected channel Resource and Meta epoch retained when opening a publication.</summary>
public sealed record PublicationChannelSelection(PublicationRegistryAccess Registry, string Resource, BigInteger Epoch);

/// <summary>A constituent Registry and pinned snapshot in a federated open context.</summary>
public sealed record PublicationSnapshotSource(PublicationRegistryAccess Registry, PublicationByteDescriptor Snapshot);

/// <summary>A caller-visible selection of one immutable publication snapshot.</summary>
public sealed class PublicationOpenRecord
{
    private readonly byte[] _exactBytes;

    private PublicationOpenRecord(byte[] exactBytes, string scope, PublicationByteDescriptor snapshot,
        PublicationByteDescriptor registry, string completeness, PublicationChannelSelection? channel,
        IReadOnlyList<PublicationSnapshotSource> sources)
    {
        _exactBytes = (byte[])exactBytes.Clone();
        Scope = scope;
        Snapshot = snapshot;
        Registry = registry;
        Completeness = completeness;
        Channel = channel;
        Sources = sources;
    }

    /// <summary>Gets the exact publication scope URI.</summary>
    public string Scope { get; }

    /// <summary>Gets the exact descriptor selected for the manifest.</summary>
    public PublicationByteDescriptor Snapshot { get; }

    /// <summary>Gets the checked Registry JSON descriptor.</summary>
    public PublicationByteDescriptor Registry { get; }

    /// <summary>Gets the completeness classification retained by the open operation.</summary>
    public string Completeness { get; }

    /// <summary>Gets the channel selection, when this record was opened from a channel.</summary>
    public PublicationChannelSelection? Channel { get; }

    /// <summary>Gets constituent pinned sources, when this is a federated context.</summary>
    public IReadOnlyList<PublicationSnapshotSource> Sources { get; }

    /// <summary>Returns a copy of the exact open-record JSON bytes.</summary>
    public byte[] GetExactBytes() => (byte[])_exactBytes.Clone();

    /// <summary>Parses an xregistry-open/1 record.</summary>
    /// <exception cref="PublicationException">The record does not satisfy the open-record schema.</exception>
    /// <exception cref="RegistryException">The input JSON is malformed or exceeds its configured limits.</exception>
    public static PublicationOpenRecord Parse(ReadOnlySpan<byte> exactJson, RegistryJsonLimits? limits = null)
    {
        using var document = RegistryJson.ParseDocument(exactJson, limits);
        var root = document.RootElement;
        PublicationJson.RequireObject(root, "");
        PublicationJson.RequireOnly(root, "", "format", "scope", "snapshot", "registry", "completeness", "channel", "sources");
        PublicationJson.RequireString(root, "format", "", "xregistry-open/1");

        var scope = PublicationJson.RequireUri(root, "scope", "");
        var snapshot = PublicationByteDescriptor.Parse(PublicationJson.RequireProperty(root, "snapshot", ""));
        var registry = PublicationByteDescriptor.Parse(PublicationJson.RequireProperty(root, "registry", ""));
        var completeness = PublicationJson.RequireOneOf(root, "completeness", "", "linked", "offline-complete");
        var channel = root.TryGetProperty("channel", out var channelValue) ? ParseChannel(channelValue) : null;
        var sources = root.TryGetProperty("sources", out var sourceValues) ? ParseSources(sourceValues) : [];

        return new PublicationOpenRecord(exactJson.ToArray(), scope, snapshot, registry, completeness, channel,
            Array.AsReadOnly(sources));
    }

    /// <summary>Creates a schema-checked xregistry-open/1 record for one selected snapshot.</summary>
    public static PublicationOpenRecord Create(string scope, PublicationByteDescriptor snapshot,
        PublicationByteDescriptor registry, string completeness, PublicationChannelSelection? channel = null,
        IReadOnlyList<PublicationSnapshotSource>? sources = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(completeness);
        sources ??= [];

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("format", "xregistry-open/1");
            writer.WriteString("scope", scope);
            writer.WritePropertyName("snapshot");
            WriteDescriptor(writer, snapshot);
            writer.WritePropertyName("registry");
            WriteDescriptor(writer, registry);
            writer.WriteString("completeness", completeness);
            if (channel is not null)
            {
                writer.WritePropertyName("channel");
                writer.WriteStartObject();
                writer.WritePropertyName("reference");
                writer.WriteStartObject();
                writer.WritePropertyName("registry");
                WriteRegistry(writer, channel.Registry);
                writer.WriteString("resource", channel.Resource);
                writer.WriteEndObject();
                writer.WritePropertyName("epoch");
                writer.WriteRawValue(channel.Epoch.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }
            if (sources.Count != 0)
            {
                writer.WriteStartArray("sources");
                foreach (var source in sources)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("registry");
                    WriteRegistry(writer, source.Registry);
                    writer.WritePropertyName("snapshot");
                    WriteDescriptor(writer, source.Snapshot);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }

        return Parse(buffer.ToArray());
    }

    /// <summary>Checks this open record against its exact manifest and selected descriptor.</summary>
    public void ValidateAgainstManifest(PublicationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.ValidateSnapshotDescriptor(Snapshot);
        if (!string.Equals(Scope, manifest.Scope, StringComparison.Ordinal) ||
            !string.Equals(Completeness, manifest.Completeness, StringComparison.Ordinal) ||
            !PublicationReferenceComparer.DescriptorsEqual(Registry, manifest.Registry))
        {
            throw PublicationJson.Error("open_context_mismatch", "",
                "The open record's scope, Registry descriptor or completeness does not match the manifest.");
        }
    }

    /// <summary>Checks that this open record retains the exact descriptor read from a channel head.</summary>
    public void ValidateSelectedHead(PublicationByteDescriptor head)
    {
        ArgumentNullException.ThrowIfNull(head);
        if (!PublicationReferenceComparer.DescriptorsEqual(Snapshot, head))
        {
            throw PublicationJson.Error("open_head_mismatch", "",
                "The open record's snapshot must equal the selected channel head.");
        }
    }

    private static PublicationChannelSelection ParseChannel(JsonElement element)
    {
        const string path = "/channel";
        PublicationJson.RequireObject(element, path);
        PublicationJson.RequireOnly(element, path, "reference", "epoch");
        var reference = PublicationJson.RequireProperty(element, "reference", path);
        PublicationJson.RequireObject(reference, path + "/reference");
        PublicationJson.RequireOnly(reference, path + "/reference", "registry", "resource");
        var access = PublicationByteDescriptor.ParseRegistryAccess(
            PublicationJson.RequireProperty(reference, "registry", path + "/reference"));
        var resource = PublicationJson.RequireResource(
            PublicationJson.RequireString(reference, "resource", path + "/reference"),
            path + "/reference/resource");
        var epoch = PublicationJson.RequireNonNegativeInteger(
            PublicationJson.RequireProperty(element, "epoch", path), path + "/epoch");
        return new PublicationChannelSelection(access, resource, epoch);
    }

    private static PublicationSnapshotSource[] ParseSources(JsonElement element)
    {
        var array = PublicationJson.RequireArray(element, "/sources");
        var sources = new PublicationSnapshotSource[array.GetArrayLength()];
        var index = 0;
        foreach (var source in array.EnumerateArray())
        {
            var path = $"/sources/{index}";
            PublicationJson.RequireObject(source, path);
            PublicationJson.RequireOnly(source, path, "registry", "snapshot");
            var registry = PublicationByteDescriptor.ParseRegistryAccess(
                PublicationJson.RequireProperty(source, "registry", path));
            var snapshot = PublicationByteDescriptor.Parse(
                PublicationJson.RequireProperty(source, "snapshot", path));
            if (!PublicationReferenceComparer.ReferencesEqual(snapshot.Reference,
                    new PublicationVersionReference(registry, snapshot.Reference.Version)))
            {
                throw PublicationJson.Error("source_context_mismatch", path + "/registry",
                    "A source Registry access context must match the Registry in its pinned snapshot reference.");
            }

            sources[index++] = new PublicationSnapshotSource(registry, snapshot);
        }

        return sources;
    }

    private static void WriteDescriptor(Utf8JsonWriter writer, PublicationByteDescriptor descriptor)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("reference");
        writer.WriteStartObject();
        writer.WritePropertyName("registry");
        WriteRegistry(writer, descriptor.Reference.Registry);
        writer.WriteString("version", descriptor.Reference.Version);
        writer.WriteEndObject();
        writer.WriteString("digest", descriptor.Digest);
        writer.WritePropertyName("size");
        writer.WriteRawValue(descriptor.Size.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static void WriteRegistry(Utf8JsonWriter writer, PublicationRegistryAccess registry)
    {
        writer.WriteStartObject();
        writer.WriteString("binding", registry.Binding);
        writer.WriteString("endpoint", registry.Endpoint);
        if (registry.Parameters.Count != 0)
        {
            writer.WriteStartObject("parameters");
            foreach (var parameter in registry.Parameters.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteString(parameter.Key, parameter.Value);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }
}

/// <summary>Compares publication references using decoded Core XID identity and explicit access context.</summary>
public static class PublicationReferenceComparer
{
    /// <summary>Gets an opaque key stable for equivalent Version reference identities.</summary>
    public static string GetReferenceIdentityKey(PublicationVersionReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("binding", reference.Registry.Binding);
            writer.WriteString("endpoint", reference.Registry.Endpoint);
            writer.WriteStartObject("parameters");
            foreach (var parameter in NormalizeParameters(reference.Registry)
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteString(parameter.Key, parameter.Value);
            }
            writer.WriteEndObject();
            writer.WriteString("version", PublicationJson.GetVersionIdentity(reference.Version));
            writer.WriteEndObject();
            writer.Flush();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    /// <summary>Gets an opaque key stable for equivalent Resource XID identities.</summary>
    public static string GetResourceIdentityKey(string resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return PublicationJson.GetResourceIdentity(resource);
    }

    /// <summary>Compares two exact Version references without normalizing endpoint or parameter values.</summary>
    public static bool ReferencesEqual(PublicationVersionReference left, PublicationVersionReference right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return AccessEqual(left.Registry, right.Registry) &&
            string.Equals(PublicationJson.GetVersionIdentity(left.Version),
                PublicationJson.GetVersionIdentity(right.Version), StringComparison.Ordinal);
    }

    /// <summary>Compares descriptors by reference identity, digest and byte size.</summary>
    public static bool DescriptorsEqual(PublicationByteDescriptor left, PublicationByteDescriptor right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return ReferencesEqual(left.Reference, right.Reference) &&
            string.Equals(left.Digest, right.Digest, StringComparison.Ordinal) && left.Size == right.Size;
    }

    /// <summary>Compares a selected Resource reference, including its access context.</summary>
    public static bool ResourceReferencesEqual(PublicationRegistryAccess leftRegistry, string leftResource,
        PublicationRegistryAccess rightRegistry, string rightResource)
    {
        ArgumentNullException.ThrowIfNull(leftRegistry);
        ArgumentNullException.ThrowIfNull(rightRegistry);
        return AccessEqual(leftRegistry, rightRegistry) &&
            string.Equals(PublicationJson.GetResourceIdentity(leftResource),
                PublicationJson.GetResourceIdentity(rightResource), StringComparison.Ordinal);
    }

    private static bool AccessEqual(PublicationRegistryAccess left, PublicationRegistryAccess right)
    {
        if (!string.Equals(left.Binding, right.Binding, StringComparison.Ordinal) ||
            !string.Equals(left.Endpoint, right.Endpoint, StringComparison.Ordinal))
        {
            return false;
        }

        var leftParameters = NormalizeParameters(left);
        var rightParameters = NormalizeParameters(right);
        if (leftParameters.Count != rightParameters.Count)
        {
            return false;
        }

        foreach (var parameter in leftParameters)
        {
            if (!rightParameters.TryGetValue(parameter.Key, out var value) ||
                !string.Equals(parameter.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, string> NormalizeParameters(PublicationRegistryAccess access)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in access.Parameters)
        {
            result.Add(parameter.Key, parameter.Value);
        }

        if (string.Equals(access.Binding, "git", StringComparison.Ordinal))
        {
            result.TryAdd("path", "xregistry");
            if (result.TryGetValue("revision", out var revision) &&
                revision.Length is 40 or 64 && revision.All(Uri.IsHexDigit))
            {
                result["revision"] = revision.ToLowerInvariant();
            }
        }

        return result;
    }
}
