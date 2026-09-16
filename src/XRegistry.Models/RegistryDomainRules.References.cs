// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace XRegistry.Models;

public static partial class RegistryDomainRules
{
    /// <summary>Checks the type of a local base Message reference without requiring its existence or acquiring it.</summary>
    public static void ValidateMessageBaseReference(JsonElement message, RegistryModel model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        RequireMetadataObject(message);
        var reference = OptionalText(message, "basemessage", "/basemessage");
        if (reference is null) { return; }
        if (!reference.StartsWith('/'))
        {
            Scalar("uriabsolute", message.GetProperty("basemessage"), "/basemessage", cancellationToken);
            return;
        }
        RegistryPath path;
        try { path = RegistryPath.Parse(reference); }
        catch (RegistryException exception)
        {
            throw new RegistryException(new("invalid_attribute", "/basemessage",
                "The local base must be a Message Resource or Version XID."), exception);
        }
        if (path.Kind is not (RegistryPathKind.Resource or RegistryPathKind.Version) || path.IsDetails ||
            path.VersionId?.Value is "null" or "request" ||
            !model.Groups.TryGetValue(path.GroupType!, out var group) ||
            !group.Resources.TryGetValue(path.ResourceType!, out var resource) ||
            resource.Annotations.ModelCompatibleWith != "https://xregistry.io/xreg/domains/message/specs/model.json")
        {
            throw Invalid("The local base must identify a Message Resource or Version in this Registry's model.", "/basemessage");
        }
    }

    /// <summary>Checks a Message's local schema Resource type and optional URI identity without resolving or fetching the target.</summary>
    /// <param name="message">Completed Message metadata.</param>
    /// <param name="model">The effective model owning the local XID.</param>
    /// <param name="schemaSelfUrl">The host's binding-specific, non-fetching self-URL projection for a local schema Resource.</param>
    /// <param name="cancellationToken">Cancels validation.</param>
    public static void ValidateMessageSchemaReference(JsonElement message, RegistryModel model,
        Func<RegistryPath, RegistryResourceDefinition, string> schemaSelfUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(schemaSelfUrl);
        cancellationToken.ThrowIfCancellationRequested();
        RequireMetadataObject(message);
        var reference = OptionalText(message, "dataschemaxid", "/dataschemaxid");
        if (reference is null)
        {
            return;
        }

        RegistryPath path;
        try
        {
            path = RegistryPath.Parse(reference);
        }
        catch (RegistryException exception)
        {
            throw new RegistryException(new("invalid_attribute", "/dataschemaxid",
                "The schema reference must be a local schema Resource XID."), exception);
        }
        if (path.Kind != RegistryPathKind.Resource || path.IsDetails ||
            path.GroupType is not { } groupName || path.ResourceType is not { } resourceName ||
            !model.Groups.TryGetValue(groupName, out var group) ||
            !group.Resources.TryGetValue(resourceName, out var schema) ||
            !string.Equals(schema.Annotations.ModelCompatibleWith,
                "https://xregistry.io/xreg/domains/schema/specs/model.json", StringComparison.Ordinal))
        {
            throw Invalid("The schema XID must identify a schema Resource in this Registry's model.", "/dataschemaxid");
        }

        if (OptionalText(message, "dataschemauri", "/dataschemauri", nonempty: false) is { } uri)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var self = schemaSelfUrl(path, schema);
            if (string.IsNullOrEmpty(self))
            {
                throw new InvalidOperationException("The host must supply the schema Resource's self URL.");
            }
            if (!string.Equals(uri, self, StringComparison.Ordinal))
            {
                throw Invalid("dataschemauri must equal the local schema Resource's self URL.", "/dataschemauri");
            }
        }
    }
}
