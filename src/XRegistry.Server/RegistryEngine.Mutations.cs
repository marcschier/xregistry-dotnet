// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace XRegistry.Server;

public sealed partial class RegistryEngine
{
    private sealed partial class Request
    {
        private JsonObject? _ownerPostCollections;

        private async ValueTask ApplyAsync()
        {
            CheckReadonlyTarget();
            var path = _operation.Path;
            var key = ServerJson.Key(path);
            var patch = _operation.Action == RegistryAction.Patch;
            if (_operation.Action == RegistryAction.Delete)
            {
                Delete(path, _operation.Metadata is null ? null : InputMetadata());
                return;
            }

            var documentMode = path.Kind is RegistryPathKind.Resource or RegistryPathKind.Version &&
                ResourceDefinition(path).HasDocument && !path.IsDetails;
            if (!documentMode && _operation.Metadata is null)
            {
                throw ServerErrors.Create("missing_body", key, "The metadata body is absent; use an empty object for no attributes.");
            }

            if (documentMode && patch)
            {
                throw ServerErrors.Create("details_required", key, "PATCH requires the metadata view for document-bearing entities.");
            }

            var body = InputMetadata();
            if (documentMode)
            {
                body["contenttype"] = _operation.ContentType;
            }

            switch (path.Kind)
            {
                case RegistryPathKind.Registry:
                    await ApplyContainerAsync(Require("/"), body, patch, _operation.Action == RegistryAction.Post).ConfigureAwait(false);
                    break;
                case RegistryPathKind.Group:
                    await ApplyContainerAsync(EnsureGroup(path), body, patch, _operation.Action == RegistryAction.Post).ConfigureAwait(false);
                    break;
                case RegistryPathKind.GroupCollection:
                case RegistryPathKind.ResourceCollection:
                case RegistryPathKind.VersionCollection:
                    await ApplyCollectionAsync(path, body, patch).ConfigureAwait(false);
                    break;
                case RegistryPathKind.Resource:
                    if (_operation.Action == RegistryAction.Post)
                    {
                        var resource = EnsureResource(path);
                        var specifiedVersion = Identifier(body, "versionid", key);
                        var versionId = specifiedVersion ?? GenerateVersionId(resource, ResourceDefinition(path));
                        var version = ApplyVersion(resource, versionId, VersionInput(body, ResourceDefinition(path)),
                            documentMode, documentMode, generated: specifiedVersion is null);
                        _responsePath = RegistryPath.Parse(version.Key + (path.IsDetails ? "$details" : ""));
                    }
                    else
                    {
                        ApplyResource(path, body, patch, documentMode);
                    }

                    break;
                case RegistryPathKind.Meta:
                    if (ApplyCrossReference(path, body, metaOnly: true, patch))
                    {
                        break;
                    }

                    var meta = EnsureResource(path);
                    ApplyMeta(meta, body, patch);
                    if (ChildCount(meta.Key + "/versions") == 0)
                    {
                        ApplyVersion(meta, GenerateVersionId(meta, ResourceDefinition(path)), new JsonObject(), false, false, generated: true);
                    }

                    break;
                case RegistryPathKind.Version:
                    ApplyVersion(EnsureResource(path), path.VersionId!.Value, VersionInput(body, ResourceDefinition(path)),
                        patch || documentMode, documentMode);
                    break;
                case RegistryPathKind.ModelSource:
                    await ApplyModelAsync(ServerJson.Own(body, _engine.Limits.Json)).ConfigureAwait(false);
                    break;
                case RegistryPathKind.Capabilities:
                    await ApplyCapabilitiesAsync(body, patch).ConfigureAwait(false);
                    break;
                default:
                    throw ServerErrors.Create("action_not_supported", key, "This administrative metadata is not mutable.");
            }
        }

        private Entity EnsureGroup(RegistryPath path)
        {
            var key = ServerJson.Key(RegistryPath.ForGroup(path.GroupType!, path.GroupId!));
            var existing = Find(key);
            if (existing is not null)
            {
                return existing;
            }

            var entity = New(key);
            entity.Attributes[_model.Groups[path.GroupType!].Singular + "id"] = path.GroupId!.Value;
            Touch(Require("/"));
            return entity;
        }

        private Entity EnsureResource(RegistryPath path)
        {
            var key = ResourceKey(path);
            EnsureGroup(path);
            var existing = Find(key);
            if (existing is not null && ServerJson.Boolean(existing.Attributes, "readonly"))
            {
                throw ServerErrors.Create("readonly", ServerJson.Key(path), "This Resource is read-only.");
            }

            if (existing is not null && existing.Attributes["xref"] is not null)
            {
                throw ServerErrors.With("extra_xref_attribute", key, "Cross-reference Resources cannot be updated through their target projection.",
                    ("name", "versions"), ("singular", ResourceDefinition(path).Singular));
            }

            _resources.Add(key);
            if (existing is not null)
            {
                return existing;
            }

            var entity = New(key);
            entity.Attributes[ResourceDefinition(path).Singular + "id"] = path.ResourceId!.Value;
            entity.Attributes["readonly"] = false;
            entity.Attributes["defaultversionsticky"] = false;
            Touch(Require(ServerJson.Key(RegistryPath.ForGroup(path.GroupType!, path.GroupId!))));
            return entity;
        }

        private async ValueTask ApplyContainerAsync(Entity entity, JsonObject body, bool patch, bool collectionsOnly)
        {
            var path = RegistryPath.Parse(entity.Key);
            var root = path.Kind == RegistryPathKind.Registry;
            var group = root ? null : _model.Groups[path.GroupType!];
            var names = root ? _model.Groups.Keys : group!.Resources.Keys;
            var attributes = (JsonObject)body.DeepClone();
            foreach (var name in names)
            {
                attributes.Remove(name);
            }

            if (collectionsOnly)
            {
                if (attributes.Count != 0)
                {
                    throw ServerErrors.With(root ? "groups_only" : "resources_only", entity.Key,
                        "POST to this entity accepts only nested collection maps.", ("name", attributes.First().Key));
                }

                _ownerPostCollections = body;
            }
            else
            {
                if (root)
                {
                    var capabilitiesRequested = attributes.ContainsKey("capabilities") && _previousCapabilities.Mutable("capabilities");
                    var modelRequested = attributes.ContainsKey("modelsource");
                    if (capabilitiesRequested)
                    {
                        await ApplyCapabilitiesAsync(attributes["capabilities"] is null ? null :
                            Map(attributes["capabilities"], "/capabilities"), patch).ConfigureAwait(false);
                    }

                    attributes.Remove("capabilities");

                    if (attributes.TryGetPropertyValue("modelsource", out var source))
                    {
                        await ApplyModelAsync(ServerJson.Own(source is null ? new JsonObject() : Map(source, "/modelsource"), _engine.Limits.Json)).ConfigureAwait(false);
                        names = _model.Groups.Keys;
                        attributes.Remove("modelsource");
                        foreach (var name in names)
                        {
                            attributes.Remove(name);
                        }
                    }

                    attributes.Remove("model");
                    if (!_capabilities.Mutable("entities") && (!capabilitiesRequested && !modelRequested ||
                        attributes.Any(property => !_model.Attributes.TryGetValue(property.Key, out var definition) ||
                            !definition.ReadOnly && property.Key is not ("registryid" or "epoch"))))
                    {
                        throw ServerErrors.Create("readonly", "/", "Registry entity metadata is not mutable in the enabled profile.");
                    }
                }

                Merge(entity, attributes, root ? _model.Attributes : group!.Attributes, patch,
                    root ? "registryid" : group!.Singular + "id", root ? _engine._options.RegistryId : path.GroupId!.Value);
                if (!root)
                {
                    _constraintGroups.Remove(entity.Key);
                }
            }

            foreach (var name in names)
            {
                if (body.TryGetPropertyValue(name, out var collection))
                {
                    await ApplyCollectionAsync(RegistryPath.Parse((root ? "" : entity.Key) + "/" + name),
                        Map(collection, entity.Key + "/" + name), patch).ConfigureAwait(false);
                }
            }
        }

        private async ValueTask ApplyCollectionAsync(RegistryPath path, JsonObject collection, bool patch)
        {
            if (!_capabilities.Mutable("entities"))
            {
                throw ServerErrors.Create("readonly", path.EscapedPath, "Entity collections are not mutable in the enabled profile.");
            }

            var key = ServerJson.Key(path);
            if (path.Kind == RegistryPathKind.ResourceCollection)
            {
                EnsureGroup(path);
            }
            else if (path.Kind == RegistryPathKind.VersionCollection && collection.Count == 0 && Find(ResourceKey(path)) is null)
            {
                throw ServerErrors.Create("missing_versions", key, "A new Resource requires at least one Version.");
            }

            foreach (var entry in collection.OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                Work();
                var id = ParseId(entry.Key);
                var childPath = RegistryPath.Parse(key + "/" + Uri.EscapeDataString(id.Value));
                var body = Map(entry.Value, childPath.EscapedPath);
                switch (path.Kind)
                {
                    case RegistryPathKind.GroupCollection:
                        await ApplyContainerAsync(EnsureGroup(childPath), body, patch, false).ConfigureAwait(false);
                        break;
                    case RegistryPathKind.ResourceCollection:
                        ApplyResource(childPath, body, patch, false);
                        break;
                    case RegistryPathKind.VersionCollection:
                        ApplyVersion(EnsureResource(childPath), id.Value, VersionInput(body, ResourceDefinition(childPath)), patch, false);
                        break;
                    default:
                        throw new InvalidOperationException("The staged collection has an unsupported shape.");
                }
            }
        }

        private void ApplyResource(RegistryPath path, JsonObject body, bool patch, bool documentMode)
        {
            if (ApplyCrossReference(path, body, metaOnly: false, patch))
            {
                return;
            }

            var resource = EnsureResource(path);
            var definition = ResourceDefinition(path);
            CheckId(body, definition.Singular + "id", path.ResourceId!.Value, resource.Key);
            var previousDefault = ServerJson.Text(resource.Attributes, "defaultversionid");
            var versions = body.TryGetPropertyValue("versions", out var values) ? Map(values, resource.Key + "/versions") : null;
            if (documentMode && (body.ContainsKey("versions") || body.ContainsKey("meta")))
            {
                throw ServerErrors.With("extra_xregistry_header", resource.Key, "Complex Resource metadata requires the metadata view.",
                    ("name", body.ContainsKey("versions") ? "xRegistry-versions" : "xRegistry-meta"));
            }

            if (versions is not null)
            {
                foreach (var version in versions.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
                {
                    _ = ParseId(version.Key);
                    ApplyVersion(resource, version.Key, VersionInput(Map(version.Value, resource.Key), definition), patch, false);
                }
            }

            var versionInput = VersionInput(body, definition, resourceEnvelope: true);
            var explicitVersion = previousDefault is not null && versions?.ContainsKey(previousDefault) == true
                ? null : Identifier(versionInput, "versionid", resource.Key);
            var metaInput = body.TryGetPropertyValue("meta", out var metaValue) ? Map(metaValue, resource.Key + "/meta") : null;
            var chosen = previousDefault ?? explicitVersion ?? (metaInput is null ? null : Identifier(metaInput, "defaultversionid", resource.Key));
            if (chosen is null && _flags.SetDefault is { } requested && requested is not ("request" or "null"))
            {
                chosen = requested;
            }

            var applyDefault = chosen is not null ? versions?.ContainsKey(chosen) != true : versions is null || versions.Count == 0;
            if (patch && versionInput.Count == 0 && metaInput is not null && !resource.IsNew)
            {
                applyDefault = false;
            }

            if (applyDefault)
            {
                if (previousDefault is not null && explicitVersion is not null && previousDefault != explicitVersion)
                {
                    throw ServerErrors.With("mismatched_id", resource.Key, "versionid does not match the previous default Version.",
                        ("singular", "version"), ("invalid_id", explicitVersion), ("expected_id", previousDefault));
                }

                var generated = chosen is null;
                chosen ??= GenerateVersionId(resource, definition);
                ApplyVersion(resource, chosen, versionInput, patch || documentMode, documentMode, generated);
            }

            if (metaInput is not null)
            {
                ApplyMeta(resource, metaInput, patch);
            }
        }

        private void ApplyMeta(Entity resource, JsonObject body, bool patch)
        {
            var path = RegistryPath.Parse(resource.Key);
            var definition = ResourceDefinition(path);
            var input = (JsonObject)body.DeepClone();
            if (patch && input.ContainsKey("defaultversionid") && !input.ContainsKey("defaultversionsticky"))
            {
                input["defaultversionsticky"] = input["defaultversionid"] is not null;
            }

            Merge(resource, input, definition.MetaAttributes, patch, definition.Singular + "id", path.ResourceId!.Value);
            resource.Attributes["readonly"] ??= false;
            resource.Attributes["defaultversionsticky"] ??= false;
            _resources.Add(resource.Key);
        }

        private Entity ApplyVersion(Entity resource, string versionId, JsonObject input, bool patch, bool documentMode, bool generated = false)
        {
            _ = ParseId(versionId);
            if (versionId is "null" or "request")
            {
                throw ServerErrors.With("malformed_id", _engine.Url(ServerJson.Key(_operation.Path)), "This Version identifier is reserved.", ("id", versionId));
            }

            var path = RegistryPath.Parse(resource.Key);
            var definition = ResourceDefinition(path);
            var key = resource.Key + "/versions/" + Uri.EscapeDataString(versionId);
            var entity = Find(key);
            var isNew = entity is null;
            if (isNew && !definition.SetVersionId && !generated)
            {
                throw ServerErrors.With("versionid_not_allowed", resource.Key, "This Resource type requires server-generated Version identifiers.",
                    ("plural", definition.Plural));
            }

            entity ??= New(key);
            CheckId(input, definition.Singular + "id", path.ResourceId!.Value, key);
            var attributes = (JsonObject)input.DeepClone();
            var oldAncestor = entity.Attributes["ancestorid"]?.DeepClone();
            var documentName = definition.Singular;
            var submittedDocuments = definition.HasDocument
                ? new[] { documentName, documentName + "base64", documentName + "url" }.Where(input.ContainsKey).ToArray()
                : [];

            if (submittedDocuments.Length > 1)
            {
                throw ServerErrors.With("one_resource", key, "Only one Document representation may be supplied.",
                    ("list", string.Join(", ", documentName, documentName + "base64", documentName + "url")));
            }

            foreach (var name in submittedDocuments)
            {
                attributes.Remove(name);
            }

            if (!attributes.ContainsKey("ancestorid") && oldAncestor is not null)
            {
                attributes["ancestorid"] = oldAncestor;
            }

            if (attributes["ancestorid"] is JsonValue ancestor && ancestor.TryGetValue<string>(out var ancestorId) && ancestorId == "request")
            {
                attributes["ancestorid"] = versionId;
            }

            try
            {
                Merge(entity, attributes, definition.Attributes, patch, "versionid", versionId);
            }
            catch (RegistryException exception) when (!definition.HasDocument &&
                exception.Diagnostic.Code == "unknown_attribute" &&
                exception.Data["xregistry.args"] is IReadOnlyDictionary<string, string> arguments &&
                arguments.TryGetValue("name", out var name) &&
                (name == documentName || name == documentName + "base64" || name == documentName + "url"))
            {
                // Core determines admission; unmodeled Document-form input retains its existing error.
                throw ServerErrors.With("hasdocument_violation", key, "This Resource type has no separate Document.", ("plural", definition.Plural));
            }
            entity.Attributes[definition.Singular + "id"] = path.ResourceId!.Value;
            if (definition.HasDocument)
            {
                ApplyDocument(entity, resource, definition, input, submittedDocuments, patch, documentMode);
            }
            else if (documentMode || _operation.Document is not null)
            {
                throw ServerErrors.With("hasdocument_violation", key, "A separate Document cannot be supplied for this type.", ("plural", definition.Plural));
            }

            if (isNew)
            {
                if (entity.Attributes["ancestorid"] is null)
                {
                    var existing = Children(resource.Key + "/versions").Where(version => version.Key != entity.Key).ToList();
                    entity.Attributes["ancestorid"] = existing.Count == 0 ? versionId : Id(Newest(existing, definition));
                }

                _newVersions.Add(key);
                Touch(resource);
            }

            _resources.Add(resource.Key);
            return entity;
        }

        private void ApplyDocument(Entity entity, Entity resource, RegistryResourceDefinition definition, JsonObject input,
            string[] submitted, bool patch, bool documentMode)
        {
            var name = definition.Singular;
            var previousUrl = entity.Original[name + "url"];
            if (submitted.Length != 0 && submitted[0] == name + "url" && input[submitted[0]] is not null)
            {
                if (_engine._options.DocumentReferencePolicy is null)
                {
                    throw ServerErrors.Create("document_reference_policy_required", entity.Key, "External Document references require an explicit host policy.");
                }

                var value = input[submitted[0]]!;
                var validated = ValidateMetadata(ServerJson.Own(new JsonObject { [name + "url"] = value.DeepClone() }, _engine.Limits.Json),
                    definition.Attributes, new() { Mode = RegistryMetadataMode.ClientInput, Model = _model, Limits = _engine.Limits.Json }, entity.Key);
                var reference = validated.Metadata.RootElement.GetProperty(name + "url").GetString()!;
                _documentReferences.Add(DocumentReference(entity.Key, reference));
                entity.Attributes[name + "url"] = reference;
                entity.DocumentAction = RegistryDocumentAction.Remove;
                entity.DocumentInput = null;
                _requireEmptyBody |= documentMode;
                return;
            }

            entity.Attributes.Remove(name + "url");
            if (documentMode)
            {
                var stream = _operation.Document ?? Stream.Null;
                entity.DocumentInput = ct => BoundedContent.ReadAsync(stream, _engine.Limits.MaxDocumentBytes, ct, requestBody: true);
            }
            else if (submitted.Length != 0)
            {
                var value = input[submitted[0]];
                if (value is null)
                {
                    entity.Document = [];
                    entity.DocumentAction = RegistryDocumentAction.Replace;
                }
                else if (submitted[0] == name + "base64")
                {
                    if (value is not JsonValue base64 || !base64.TryGetValue<string>(out var text))
                    {
                        throw ServerErrors.With("invalid_attribute", entity.Key, "The base64 Document must be a string.", ("name", name + "base64"));
                    }

                    entity.DocumentInput = ct =>
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var bytes = Convert.FromBase64String(text);
                            CheckDocumentSize(bytes.Length, entity.Key);
                            return ValueTask.FromResult(bytes);
                        }
                        catch (FormatException exception)
                        {
                            throw ServerErrors.Wrap(new RegistryException(new("invalid_attribute", entity.Key, "The Document is not valid base64."), exception),
                                "invalid_attribute", entity.Key, ("name", name + "base64"));
                        }
                    };
                }
                else
                {
                    entity.DocumentInput = ct =>
                    {
                        ct.ThrowIfCancellationRequested();
                        var format = definition.ResolveDocumentFormat(ServerJson.Text(entity.Attributes, "contenttype") ??
                            _operation.ContentType ?? "application/json");
                        byte[] bytes;
                        try
                        {
                            bytes = format == RegistryDocumentFormat.String && value is JsonValue scalar && scalar.TryGetValue<string>(out var text)
                                ? Encoding.UTF8.GetBytes(text) : ServerJson.Encode(value, _engine.Limits.MaxDocumentBytes);
                        }
                        catch (RegistryException exception) when (exception.Diagnostic.Code == "too_large")
                        {
                            throw ServerErrors.Wrap(exception, "request_too_large", entity.Key);
                        }
                        CheckDocumentSize(bytes.Length, entity.Key);
                        return ValueTask.FromResult(bytes);
                    };
                }

                if (!input.ContainsKey("contenttype") && (submitted[0] == name && !patch || entity.Attributes["contenttype"] is null))
                {
                    entity.Attributes["contenttype"] = _operation.ContentType ?? "application/json";
                }
            }
            else if (entity.IsNew || previousUrl is not null && !patch)
            {
                entity.Document = [];
                entity.DocumentAction = RegistryDocumentAction.Replace;
            }
            else if (patch && previousUrl is not null)
            {
                entity.Attributes[name + "url"] = previousUrl.DeepClone();
            }

            _resources.Add(resource.Key);
        }

        private void CheckDocumentSize(int count, string path)
        {
            if (count > _engine.Limits.MaxDocumentBytes)
            {
                throw ServerErrors.Create("request_too_large", path, "The Document exceeds its byte budget.");
            }
        }

        private string GenerateVersionId(Entity resource, RegistryResourceDefinition definition)
        {
            var existing = Collection(resource.Key + "/versions");
            string id;
            do
            {
                Work();
                resource.NextVersion += BigInteger.One;
                id = resource.NextVersion.ToString(CultureInfo.InvariantCulture) + (definition.VersionMode == "semver" ? ".0.0" : "");
            }
            while (existing.Ids.Contains(id));
            return id;
        }

        private string? Identifier(JsonObject body, string name, string path)
        {
            if (!body.TryGetPropertyValue(name, out var value))
            {
                return null;
            }

            if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var id))
            {
                throw ServerErrors.With("invalid_attribute", path, "A supplied identifier must be a non-null string.", ("name", name));
            }

            _ = ParseId(id);
            return id;
        }

        private static JsonObject VersionInput(JsonObject body, RegistryResourceDefinition definition, bool resourceEnvelope = false)
        {
            var attributes = (JsonObject)body.DeepClone();
            foreach (var attribute in definition.ResourceAttributes.Values)
            {
                if (attribute.ReadOnly || resourceEnvelope && attribute.Name is "meta" or "versions")
                {
                    attributes.Remove(attribute.Name);
                }
            }

            return attributes;
        }

        private static JsonObject Map(JsonNode? value, string path) =>
            value as JsonObject ?? throw ServerErrors.Create("bad_request", path, "Collections and their entities must be non-null JSON objects.");

        private void Delete(RegistryPath path, JsonObject? body)
        {
            var key = ServerJson.Key(path);
            if (path.Kind is RegistryPathKind.GroupCollection or RegistryPathKind.ResourceCollection or RegistryPathKind.VersionCollection)
            {
                if (path.Kind == RegistryPathKind.ResourceCollection)
                {
                    Require(ServerJson.Key(RegistryPath.ForGroup(path.GroupType!, path.GroupId!)));
                }
                else if (path.Kind == RegistryPathKind.VersionCollection)
                {
                    Require(ResourceKey(path));
                }

                if (body is null)
                {
                    foreach (var entity in Children(key))
                    {
                        if (path.Kind == RegistryPathKind.ResourceCollection && IgnoreReadonly(RegistryPath.Parse(entity.Key)))
                        {
                            continue;
                        }

                        DeleteEntity(entity, new JsonObject());
                    }
                }
                else
                {
                    foreach (var entry in body)
                    {
                        var id = ParseId(entry.Key);
                        var child = Find(key + "/" + Uri.EscapeDataString(id.Value));
                        var input = Map(entry.Value, key);
                        if (child is not null)
                        {
                            DeleteEntity(child, input);
                        }
                    }
                }
            }
            else
            {
                var entity = Require(key);
                var input = body ?? new JsonObject();
                if (_flags.Epoch is { } epoch && !_flags.Ignore.Contains("epoch"))
                {
                    if (path.Kind == RegistryPathKind.Resource)
                    {
                        input["meta"] = new JsonObject { ["epoch"] = epoch.DeepClone() };
                    }
                    else
                    {
                        input["epoch"] = epoch.DeepClone();
                    }
                }

                DeleteEntity(entity, input);
            }
        }

        private void DeleteEntity(Entity entity, JsonObject input)
        {
            Work();
            var path = RegistryPath.Parse(entity.Key);
            if (path.Kind == RegistryPathKind.Resource)
            {
                var definition = ResourceDefinition(path);
                CheckId(input, definition.Singular + "id", path.ResourceId!.Value, entity.Key);
                if (input.ContainsKey("epoch") && (input["meta"] as JsonObject)?.ContainsKey("epoch") != true)
                {
                    throw ServerErrors.Create("misplaced_epoch", entity.Key, "A Resource deletion guard belongs in meta.epoch.");
                }

                Guard(entity, input["meta"] as JsonObject ?? new JsonObject(), definition.Singular + "id", path.ResourceId!.Value);
                if (ServerJson.Boolean(entity.Attributes, "readonly"))
                {
                    throw ServerErrors.With(_flags.Ignore.Contains("readonly") ? "bad_flag" : "readonly", entity.Key,
                        "A cascade cannot omit a readonly Resource while deleting its parent.", ("flag", "ignore"));
                }

                CheckRemovalPromise(entity);
                foreach (var version in Children(entity.Key + "/versions"))
                {
                    DeleteEntity(version, new JsonObject());
                }

                Touch(Require(ServerJson.Key(RegistryPath.ForGroup(path.GroupType!, path.GroupId!))));
            }
            else if (path.Kind == RegistryPathKind.Group)
            {
                var group = _model.Groups[path.GroupType!];
                Guard(entity, input, group.Singular + "id", path.GroupId!.Value);
                CheckRemovalPromise(entity);
                foreach (var type in group.Resources.Keys)
                {
                    foreach (var resource in Children(entity.Key + "/" + type))
                    {
                        DeleteEntity(resource, new JsonObject());
                    }
                }

                Touch(Require("/"));
            }
            else if (path.Kind == RegistryPathKind.Version)
            {
                CheckId(input, ResourceDefinition(path).Singular + "id", path.ResourceId!.Value, entity.Key);
                Guard(entity, input, "versionid", path.VersionId!.Value);
                var resource = Require(ResourceKey(path));
                if (ServerJson.Boolean(resource.Attributes, "readonly"))
                {
                    throw ServerErrors.Create("readonly", resource.Key, "This Resource is read-only.");
                }

                Touch(resource);
                _resources.Add(resource.Key);
            }
            else
            {
                throw ServerErrors.Create("action_not_supported", entity.Key, "This entity cannot be deleted.");
            }

            entity.Deleted = true;
            entity.Dirty = true;
            RemoveMember(entity.Key);
        }
    }
}
