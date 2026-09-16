// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace XRegistry.Models;

/// <summary>Materializes Message definition inheritance through a caller-authorized metadata source, without implicit I/O.</summary>
public static partial class MessageDefinitionMaterializer
{
    /// <summary>Resolves and composes one authored Message definition using only the supplied source callback.</summary>
    public static async ValueTask<MessageMaterializationResult> MaterializeAsync(MessageDefinition definition,
        MessageDefinitionSource? source = null, MessageMaterializationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.JsonLimits);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxDepth, 256);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxTotalBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxWork, 1);
        var operation = new Operation(options, cancellationToken);
        var definitions = new List<MessageDefinition>();
        var references = new List<MessageDefinitionReference>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { UriKey(definition.Location, "") };
        MessageDefinitionReference? unresolved = null;
        MessageDefinitionSourceStatus? unresolvedStatus = null;
        var current = definition;
        while (true)
        {
            operation.Read(current);
            definitions.Add(current);
            if (!current.Metadata.RootElement.TryGetProperty("basemessage", out var reference) ||
                reference.ValueKind == JsonValueKind.Null)
            {
                break;
            }
            if (reference.ValueKind != JsonValueKind.String || reference.GetString()!.Length == 0)
            {
                throw Error("invalid_attribute", "/basemessage", "The base Message reference must be a nonempty URI string.");
            }
            var request = ResolveReference(current, reference.GetString()!, definitions.Count) with { JsonLimits = options.JsonLimits };
            if (request.Depth > options.MaxDepth)
            {
                throw Error("message_depth_limit", "/basemessage", "The Message base-reference depth limit was exceeded.");
            }
            var requestedKey = UriKey(request.TargetUri, "/basemessage");
            if (!seen.Add(requestedKey))
            {
                throw Error("message_cycle", "/basemessage", "The Message base chain contains a circular reference.");
            }
            references.Add(request);
            operation.Work();
            cancellationToken.ThrowIfCancellationRequested();
            var acquired = source is null
                ? MessageDefinitionSourceResult.Unresolved(MessageDefinitionSourceStatus.SourceNotConfigured)
                : await source(request, cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (acquired is null)
            {
                throw new InvalidOperationException("The Message source callback must return an explicit outcome.");
            }
            if (acquired.Status != MessageDefinitionSourceStatus.Found)
            {
                unresolved = request;
                unresolvedStatus = acquired.Status;
                break;
            }
            current = acquired.Definition!;
            var actualKey = UriKey(current.Location, "/basemessage");
            if (actualKey != requestedKey && !seen.Add(actualKey))
            {
                throw Error("message_cycle", "/basemessage", "The resolved Message source closes a circular reference.");
            }
        }

        var merged = new MergeNode(definitions[^1].Metadata.RootElement, definitions[^1]);
        for (var index = definitions.Count - 2; index >= 0; index--)
        {
            merged = operation.Merge(merged, definitions[index].Metadata.RootElement, definitions[index]);
        }
        var metadata = RegistryJson.Create(writer => operation.Write(writer, merged), options.JsonLimits);
        var obligations = new List<RegistryValidationObligation>();
        if (unresolved is null)
        {
            metadata = operation.Complete(metadata, definition, obligations);
        }
        operation.CompleteSources(metadata.RootElement, "", definition);
        return new MessageMaterializationResult(definition, metadata, definitions.AsReadOnly(), unresolved, unresolvedStatus,
            new ReadOnlyDictionary<string, MessageDefinition>(operation.PropertySources), obligations.AsReadOnly(), references.AsReadOnly());
    }

    private static MessageDefinitionReference ResolveReference(MessageDefinition source, string text, int depth)
    {
        if (!text.StartsWith('/'))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var absolute))
            {
                throw Error("invalid_attribute", "/basemessage", "A base Message reference must be an absolute URI or a Registry-root Message XID.");
            }
            _ = UriKey(absolute, "/basemessage");
            return new(source, text, absolute, null, depth);
        }
        RegistryPath path;
        try
        {
            path = RegistryPath.Parse(text);
        }
        catch (RegistryException exception)
        {
            throw new RegistryException(new("invalid_attribute", "/basemessage",
                "The local base reference must be a Message Resource or Version XID."), exception);
        }
        if (path.Kind is not (RegistryPathKind.Resource or RegistryPathKind.Version) || path.IsDetails ||
            path.VersionId?.Value is "null" or "request")
        {
            throw Error("invalid_attribute", "/basemessage", "The local base reference must be a Message Resource or Version XID.");
        }
        if (source.RegistryRoot is null || source.Model is null)
        {
            throw Error("message_context_required", "/basemessage", "A Registry root and effective model are required for a local Message reference.");
        }
        _ = UriKey(source.RegistryRoot, "/basemessage");
        if (source.RegistryRoot.Query.Length != 0 || source.RegistryRoot.Fragment.Length != 0 ||
            !source.Model.Groups.TryGetValue(path.GroupType!, out var group) ||
            !group.Resources.TryGetValue(path.ResourceType!, out var resource) ||
            resource.Annotations.ModelCompatibleWith != "https://xregistry.io/xreg/domains/message/specs/model.json")
        {
            throw Error("invalid_attribute", "/basemessage", "The local base reference must identify the Message domain in the supplied Registry context.");
        }
        var root = source.RegistryRoot.AbsoluteUri.EndsWith('/') ? source.RegistryRoot :
            new Uri(source.RegistryRoot.AbsoluteUri + "/", UriKind.Absolute);
        var target = new Uri(root, text[1..]);
        return new(source, text, target, path, depth);
    }

    private static string UriKey(Uri uri, string path)
    {
        if (!uri.IsAbsoluteUri || !Uri.IsWellFormedUriString(uri.OriginalString, UriKind.Absolute) ||
            uri.OriginalString.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            throw Error("invalid_attribute", path, "An absolute Message definition URI is required.");
        }
        var tail = uri.OriginalString.AsSpan(uri.OriginalString.IndexOf(':', StringComparison.Ordinal) + 1);
        var hasUserInformation = uri.UserInfo.Length != 0;
        if (tail.StartsWith("//", StringComparison.Ordinal))
        {
            tail = tail[2..];
            var end = tail.IndexOfAny('/', '?', '#');
            hasUserInformation |= (end < 0 ? tail : tail[..end]).Contains('@');
        }
        if (hasUserInformation)
        {
            throw Error("invalid_attribute", path, "Message definition locations must not contain user information.");
        }
        return uri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped);
    }

    private static RegistryException Error(string code, string path, string message) => new(new(code, path, message));

    private static string At(string path, string name) =>
        path + "/" + name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private sealed partial class Operation(MessageMaterializationOptions options, CancellationToken cancellationToken)
    {
        private long _inputBytes;
        private int _work;

        internal void Work()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_work >= options.MaxWork)
            {
                throw Error("message_work_limit", "", "The Message materialization work limit was exceeded.");
            }
            _work++;
        }

        internal void Read(MessageDefinition definition)
        {
            Work();
            _ = UriKey(definition.Location, "");
            var root = definition.Metadata.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Error("invalid_attribute", "", "A Message definition must be an object.");
            }
            var bytes = Encoding.UTF8.GetByteCount(root.GetRawText());
            if (bytes > options.MaxTotalBytes - _inputBytes)
            {
                throw Error("message_byte_limit", "", "The Message chain exceeds its aggregate metadata byte limit.");
            }
            _inputBytes += bytes;
            _ = RegistryJson.FromElement(root, options.JsonLimits);
            if (definition.Model is not null || definition.Path is not null)
            {
                _ = MessageResource(definition);
            }
            Count(root);
        }

        private void Count(JsonElement value)
        {
            Work();
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject()) { Count(property.Value); }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray()) { Count(item); }
            }
        }
    }
}
