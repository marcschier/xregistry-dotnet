using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using XRegistry.Models;

namespace XRegistry.Server;

public sealed partial class RegistryEngine
{
    private sealed class Entity(string key, JsonObject attributes, bool isNew, bool stored)
    {
        internal string Key { get; } = key;
        internal JsonObject Attributes { get; set; } = attributes;
        internal JsonObject Original { get; } = (JsonObject)attributes.DeepClone();
        internal bool IsNew { get; } = isNew;
        internal bool Stored { get; } = stored;
        internal bool Deleted { get; set; }
        internal bool Dirty { get; set; }
        internal bool StorageDirty { get; set; }
        internal string? ShortId { get; set; }
        internal bool HadDocument { get; set; }
        internal BigInteger NextVersion { get; set; }
        internal RegistryDocumentAction DocumentAction { get; set; }
        internal byte[]? Document { get; set; }
        internal Func<CancellationToken, ValueTask<byte[]>>? DocumentInput { get; set; }
        internal System.Collections.ObjectModel.ReadOnlyCollection<RegistryValidationObligation> Obligations { get; set; } = new([]);
    }

    private sealed partial class Request : IDisposable
    {
        private readonly RegistryEngine _engine;
        private readonly RegistryOperation _operation;
        private readonly RegistryOperationContext _context;
        private readonly IRegistrySnapshot _snapshot;
        private readonly CancellationToken _ct;
        private readonly string _now;
        private readonly Dictionary<string, Entity> _entities = new(StringComparer.Ordinal);
        private readonly HashSet<string> _resources = new(StringComparer.Ordinal);
        private readonly List<string> _newVersions = [];
        private readonly RequestFlags _flags;
        private RegistryModel _model;
        private RegistryJson _modelSource;
        private RegistryPath _responsePath;
        private bool _modelChanged;
        private int _work;
        private long _workingBytes;

        internal Request(RegistryEngine engine, RegistryOperation operation, RegistryOperationContext context,
            IRegistrySnapshot snapshot, RegistryModel model, RegistryJson modelSource, CapabilityProfile capabilities,
            CancellationToken cancellationToken)
        {
            _engine = engine;
            _operation = operation;
            _context = context;
            _snapshot = snapshot;
            _model = model;
            _modelSource = modelSource;
            _previousCapabilities = capabilities;
            _capabilities = capabilities;
            _ct = cancellationToken;
            _now = ServerJson.Timestamp(engine._options.TimeProvider);
            _responsePath = operation.Path;
            _flags = RequestFlags.Parse(operation, engine.Limits, capabilities);
        }

        public void Dispose() => _queryBudget?.Dispose();

        internal async ValueTask<RegistryResult> ExecuteAsync()
        {
            var write = _operation.Action is not (RegistryAction.Read or RegistryAction.Head);
            if (write)
            {
                await ApplyAsync().ConfigureAwait(false);
                CheckVersionModes(_model, _capabilities);
                var completedGroups = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entity in _entities.Values.Where(entity => entity.Dirty && !entity.Deleted &&
                    RegistryPath.Parse(entity.Key).Kind is RegistryPathKind.Registry or RegistryPathKind.Group).ToArray())
                {
                    Complete(entity);
                    if (RegistryPath.Parse(entity.Key).Kind == RegistryPathKind.Group)
                    {
                        ScheduleGroupConstraintDependents(entity);
                        completedGroups.Add(entity.Key);
                    }
                }

                var finalizedResources = _resources.ToHashSet(StringComparer.Ordinal);
                foreach (var resource in finalizedResources)
                {
                    if (Find(resource) is { } entity)
                    {
                        FinalizeResource(entity);
                    }
                }

                // Deleting a last Version touches its Group only when finalization removes the Resource.
                foreach (var group in _entities.Values.Where(entity => entity.Dirty && !entity.Deleted &&
                    RegistryPath.Parse(entity.Key).Kind == RegistryPathKind.Group && !completedGroups.Contains(entity.Key)).ToArray())
                {
                    Complete(group);
                    ScheduleGroupConstraintDependents(group);
                }

                foreach (var resource in _resources.Except(finalizedResources, StringComparer.Ordinal).ToArray())
                {
                    if (Find(resource) is { } entity)
                    {
                        FinalizeResource(entity);
                    }
                }

                foreach (var entity in _entities.Values.Where(entity => entity.Dirty || _modelChanged))
                {
                    var path = RegistryPath.Parse(entity.Key);
                    var access = entity.Deleted ? RegistryAccess.Delete : entity.IsNew ? RegistryAccess.Create : RegistryAccess.Update;
                    await _engine.AuthorizeAsync(_context, path, access, _ct).ConfigureAwait(false);
                    if (path.Kind == RegistryPathKind.Resource)
                    {
                        await _engine.AuthorizeAsync(_context, RegistryPath.Parse(entity.Key + "/meta"), access, _ct).ConfigureAwait(false);
                    }
                }

                foreach (var version in _validationResources.Where(resource =>
                    ResourceDefinition(RegistryPath.Parse(resource.Entity.Key)).ValidateFormat)
                    .SelectMany(static resource => resource.Versions).Select(static version => version.Entity)
                    .Where(static entity => !entity.Dirty))
                {
                    await _engine.AuthorizeAsync(_context, RegistryPath.Parse(version.Key), RegistryAccess.Update, _ct).ConfigureAwait(false);
                }

                foreach (var reference in _documentReferences)
                {
                    await AuthorizeDocumentReferenceAsync(reference).ConfigureAwait(false);
                }

                if (_requireEmptyBody && _operation.Document is { } body)
                {
                    var probe = new byte[1];
                    if (await body.ReadAsync(probe, _ct).ConfigureAwait(false) != 0)
                    {
                        throw ServerErrors.Create("bad_request", _operation.Path.EscapedPath, "A non-null Document URL requires an empty HTTP body.");
                    }
                }

                foreach (var entity in _entities.Values.Where(static entity => entity.Dirty).ToArray())
                {
                    if (entity.DocumentInput is { } input)
                    {
                        entity.Document = await input(_ct).ConfigureAwait(false);
                        WorkingBytes(entity.Document.Length);
                        entity.DocumentAction = RegistryDocumentAction.Replace;
                    }

                    if (!entity.Deleted)
                    {
                        Complete(entity);
                    }
                }

                await ValidateResourcesAsync().ConfigureAwait(false);
                ReconcileValidatedOrdering();
                await ValidateOpenUsdAsync().ConfigureAwait(false);
                await ValidateCrossReferenceConstraintsAsync().ConfigureAwait(false);
                CheckReadonlyEffects();
                PrepareShortIdentifiers();
                foreach (var entity in _entities.Values.Where(static entity => (entity.Dirty || entity.StorageDirty) && !entity.Deleted).ToArray())
                {
                    await _engine.AuthorizeAsync(_context, RegistryPath.Parse(entity.Key),
                        entity.IsNew ? RegistryAccess.Create : RegistryAccess.Update, _ct).ConfigureAwait(false);
                    if (entity.StorageDirty && RegistryPath.Parse(entity.Key).Kind == RegistryPathKind.Resource)
                    {
                        await _engine.AuthorizeAsync(_context, RegistryPath.Parse(entity.Key + "/meta"),
                            entity.IsNew ? RegistryAccess.Create : RegistryAccess.Update, _ct).ConfigureAwait(false);
                    }
                }
            }

            var result = await RenderAsync().ConfigureAwait(false);
            RegistryMutation? preparedEvents = null;
            if (write)
            {
                SetOutcome(result);
                result.CorrelationId = NewCorrelationId();
                preparedEvents = EventMutation(result.CorrelationId);
            }

            if (_context.PrepareResponseAsync is { } prepare)
            {
                if (_queryBudget is null)
                {
                    await prepare(result, _ct).ConfigureAwait(false);
                }
                else
                {
                    await _queryBudget.PrepareAsync(ct => prepare(result, ct)).ConfigureAwait(false);
                }
            }

            _ct.ThrowIfCancellationRequested();
            if (_pendingCursor is not null)
            {
                QueryWork.Spend();
                _engine._queryCursors.Admit(_pendingCursor);
            }

            if (write)
            {
                await PublishAsync(result.CorrelationId!, preparedEvents).ConfigureAwait(false);
            }

            return result;
        }

        private void Work()
        {
            _ct.ThrowIfCancellationRequested();
            if (++_work > _engine.Limits.MaxEntityOperations)
            {
                throw ServerErrors.Create("operation_limit", _operation.Path.EscapedPath, "The entity operation budget is exhausted.");
            }
        }

        private Entity? Find(string key)
        {
            if (_entities.TryGetValue(key, out var found))
            {
                return found.Deleted ? null : found;
            }

            Work();
            var record = StoredRecord(key);
            if (record is null && key != "/")
            {
                return null;
            }

            WorkingBytes(2L * System.Text.Encoding.UTF8.GetByteCount((record?.Metadata ?? _engine._initialRoot).RootElement.GetRawText()));
            var envelope = record is null ? null : ServerJson.Object(record.Metadata);
            var attributes = record is null ? ServerJson.Object(_engine._initialRoot) : envelope?["attributes"] as JsonObject
                ?? throw new InvalidDataException("The stored engine record has no attributes object.");
            var entity = new Entity(key, (JsonObject)attributes.DeepClone(), false, record is not null)
            {
                ShortId = envelope?["shortid"]?.GetValue<string>(),
                HadDocument = record?.HasDocument == true,
                NextVersion = envelope?["nextversion"] is { } counter ? ServerJson.Unsigned(counter, key) : BigInteger.Zero
            };
            _entities.Add(key, entity);
            return entity;
        }

        private void WorkingBytes(long count)
        {
            _workingBytes += count;
            if (_workingBytes > _engine.Limits.MaxWorkingSetBytes)
            {
                throw ServerErrors.Create("operation_limit", _operation.Path.EscapedPath, "The aggregate operation working-set budget is exhausted.");
            }
        }

        private Entity Require(string key) => Find(key) ?? throw ServerErrors.Create("not_found", key, "The requested entity does not exist.");

        private static string Parent(string key)
        {
            var slash = key.LastIndexOf('/');
            return slash <= 0 ? "" : key[..slash];
        }

        private static string ResourceKey(RegistryPath path) =>
            ServerJson.Key(RegistryPath.ForResource(path.GroupType!, path.GroupId!, path.ResourceType!, path.ResourceId!));

        private RegistryResourceDefinition ResourceDefinition(RegistryPath path) => _model.Groups[path.GroupType!].Resources[path.ResourceType!];

        private Entity New(string key)
        {
            Work();
            var collection = Collection(Parent(key));
            if (collection.Ids.Contains(LeafId(key)))
            {
                var path = RegistryPath.Parse(key);
                var singular = path.Kind == RegistryPathKind.Group ? _model.Groups[path.GroupType!].Singular :
                    path.Kind == RegistryPathKind.Version ? "version" : ResourceDefinition(path).Singular;
                var existing = collection.Ids.First(id => id.Equals(LeafId(key), StringComparison.OrdinalIgnoreCase));
                throw ServerErrors.With("mismatched_id", key, "Sibling identifiers must be case-insensitively unique.",
                    ("singular", singular), ("invalid_id", LeafId(key)), ("expected_id", existing));
            }

            var entity = new Entity(key, new JsonObject
            {
                ["epoch"] = 0,
                ["createdat"] = _now,
                ["modifiedat"] = _now
            }, true, false)
            { Dirty = true };
            _entities.Add(key, entity);
            AddMember(collection, key);
            return entity;
        }

        private void Touch(Entity entity)
        {
            if (!entity.Dirty)
            {
                entity.Attributes["epoch"] = ServerJson.Integer(ServerJson.Unsigned(entity.Original["epoch"]!, entity.Key) + 1);
                entity.Attributes["modifiedat"] = _now;
                entity.Dirty = true;
            }
        }

        private static void Guard(Entity entity, JsonObject input, string idName, string expectedId)
        {
            var subject = RegistryPath.Parse(entity.Key).Kind == RegistryPathKind.Resource ? entity.Key + "/meta" : entity.Key;
            CheckId(input, idName, expectedId, subject);
            if (!entity.IsNew && input["epoch"] is { } epoch)
            {
                BigInteger suppliedEpoch;
                try
                {
                    suppliedEpoch = ServerJson.Unsigned(epoch, subject);
                }
                catch (RegistryException exception)
                {
                    throw ServerErrors.Wrap(exception, "invalid_attribute", subject, ("name", "epoch"));
                }

                var actualEpoch = ServerJson.Unsigned(entity.Original["epoch"]!, entity.Key);
                if (suppliedEpoch != actualEpoch)
                {
                    throw ServerErrors.With("mismatched_epoch", subject, "The supplied epoch does not match the current entity.",
                        ("bad_epoch", suppliedEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        ("epoch", actualEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
            }
        }

        private static void CheckId(JsonObject input, string name, string id, string path)
        {
            if (input.TryGetPropertyValue(name, out var value) &&
                (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text) || !string.Equals(text, id, StringComparison.Ordinal)))
            {
                var submitted = value?.ToJsonString() ?? "null";
                throw ServerErrors.Create("mismatched_id", path,
                    $"The specified \"{name}\" value ({submitted}) for \"{path}\" needs to be \"{id}\".",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["singular"] = name[..^2],
                        ["invalid_id"] = submitted,
                        ["expected_id"] = id
                    });
            }
        }

        private void Merge(Entity entity, JsonObject input, IReadOnlyDictionary<string, RegistryAttributeDefinition> definitions,
            bool patch, string idName, string id)
        {
            Guard(entity, input, idName, id);
            var subject = RegistryPath.Parse(entity.Key).Kind == RegistryPathKind.Resource ? entity.Key + "/meta" : entity.Key;
            var normalized = ValidateMetadata(ServerJson.Own(input, _engine.Limits.Json), definitions,
                new()
                {
                    Mode = RegistryMetadataMode.ClientInput,
                    Model = _model,
                    RetainedMetadata = patch && definitions.Values.Any(static definition => definition.IfValues.Count != 0)
                        ? ServerJson.Own(entity.Attributes, _engine.Limits.Json).RootElement : default,
                    Limits = _engine.Limits.Json
                }, subject);
            RejectUnresolved(DomainObligations(normalized, entity.Key));
            var submitted = ServerJson.Object(normalized.Metadata);
            var output = patch ? (JsonObject)entity.Attributes.DeepClone() : new JsonObject();
            foreach (var definition in definitions.Values)
            {
                if (definition.ReadOnly && entity.Attributes.TryGetPropertyValue(definition.Name, out var retained))
                {
                    output[definition.Name] = retained?.DeepClone();
                }
            }

            foreach (var property in submitted)
            {
                if (definitions.TryGetValue(property.Key, out var definition) && definition.Immutable &&
                    entity.Original.TryGetPropertyValue(property.Key, out var previous) && previous is not null &&
                    !JsonNode.DeepEquals(previous, property.Value))
                {
                    throw ServerErrors.With("invalid_attribute", subject, "An immutable attribute cannot change.", ("name", property.Key));
                }

                output[property.Key] = property.Value?.DeepClone();
            }

            output[idName] = id;
            output["createdat"] = submitted.ContainsKey("createdat")
                ? submitted["createdat"]?.DeepClone() ?? JsonValue.Create(_now)
                : entity.Attributes["createdat"]?.DeepClone() ?? JsonValue.Create(_now);
            var modifiedAt = submitted["modifiedat"] is { } modified &&
                !ServerJson.SameTimestamp(modified, entity.Original["modifiedat"]) ? modified.DeepClone() : JsonValue.Create(_now);
            output["modifiedat"] = modifiedAt;
            entity.Attributes = output;
            Touch(entity);
            entity.Attributes["modifiedat"] = modifiedAt.Parent is null ? modifiedAt : modifiedAt.DeepClone();
            if (entity.IsNew)
            {
                entity.Attributes["epoch"] = 0;
            }
        }

        private System.Collections.ObjectModel.ReadOnlyCollection<RegistryValidationObligation> DomainObligations(
            RegistryMetadataValidationResult result, string entityKey)
        {
            var path = RegistryPath.Parse(entityKey);
            if (path.Kind != RegistryPathKind.Version ||
                ResourceDefinition(path).Annotations.ModelCompatibleWith != "https://xregistry.io/xreg/domains/message/specs/model.json")
            {
                return result.Obligations;
            }
            try { RegistryDomainRules.ValidateMessageBaseReference(result.Metadata.RootElement, _model, _ct); }
            catch (RegistryException exception) { throw MetadataError(exception, entityKey); }
            return Array.AsReadOnly(result.Obligations.Where(static obligation =>
                obligation.Kind != "target" || obligation.Path != "/basemessage").ToArray());
        }

        private void RejectUnresolved(IReadOnlyList<RegistryValidationObligation> obligations)
        {
            foreach (var obligation in obligations)
            {
                if (obligation.Kind != "matchversions" && _engine._options.ObligationValidator is null)
                {
                    throw ServerErrors.Create("obligation_policy_required", _operation.Path.EscapedPath,
                        "This metadata obligation requires an explicitly configured host validator: " + obligation.Kind);
                }
            }
        }

        private void Complete(Entity entity)
        {
            var before = (JsonObject)entity.Attributes.DeepClone();
            var path = RegistryPath.Parse(entity.Key);
            IReadOnlyDictionary<string, RegistryAttributeDefinition> definitions;
            RegistryGroupDefinition? group = null;
            RegistryResourceDefinition? resource = null;
            var attributes = entity.Attributes;
            switch (path.Kind)
            {
                case RegistryPathKind.Registry:
                    definitions = _model.Attributes;
                    attributes["registryid"] = _engine._options.RegistryId;
                    attributes["specversion"] = "1.0-rc4";
                    CollectionFields(attributes, _model.Groups.Keys, "");
                    break;
                case RegistryPathKind.Group:
                    group = _model.Groups[path.GroupType!];
                    _ = GroupForValidation(path);
                    definitions = group.Attributes;
                    attributes[group.Singular + "id"] = path.GroupId!.Value;
                    CollectionFields(attributes, group.Resources.Keys, entity.Key);
                    break;
                case RegistryPathKind.Resource:
                    resource = ResourceDefinition(path);
                    definitions = resource.MetaAttributes;
                    attributes[resource.Singular + "id"] = path.ResourceId!.Value;
                    if (ServerJson.Text(attributes, "xref") is { } xref)
                    {
                        _ = CrossReferencePath(path, xref);
                        attributes["self"] = _engine.Url(entity.Key + "/meta");
                        attributes["xid"] = entity.Key + "/meta";
                        return;
                    }

                    attributes["readonly"] ??= false;
                    attributes["defaultversionsticky"] ??= false;
                    attributes["defaultversionurl"] = _engine.Url(entity.Key + "/versions/" +
                        Uri.EscapeDataString(ServerJson.Text(attributes, "defaultversionid")!), resource.HasDocument);
                    break;
                case RegistryPathKind.Version:
                    group = GroupForValidation(path);
                    resource = ResourceDefinition(path);
                    definitions = resource.Attributes;
                    attributes[resource.Singular + "id"] = path.ResourceId!.Value;
                    attributes["versionid"] = path.VersionId!.Value;
                    attributes["isdefault"] = false;
                    break;
                default:
                    throw new InvalidOperationException("An unsupported entity was staged.");
            }

            var entityKey = path.Kind == RegistryPathKind.Resource ? entity.Key + "/meta" : entity.Key;
            attributes["xid"] = entityKey;
            attributes["self"] = _engine.Url(entityKey, path.Kind == RegistryPathKind.Version && resource!.HasDocument);
            attributes["epoch"] ??= 0;
            foreach (var definition in definitions.Values)
            {
                if (definition.Required && definition.Name != "*" && attributes[definition.Name] is null &&
                    definition.DefaultValue.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null &&
                    !(path.Kind == RegistryPathKind.Version && group!.Constraints.Values.Any(constraint =>
                        constraint.ResourceType == resource!.Plural && constraint.AttributePath.Count == 1 &&
                        constraint.AttributePath[0] == definition.Name &&
                        constraint.DefaultValue.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))))
                {
                    if (!entity.IsNew && attributes.ContainsKey(definition.Name) && entity.Original[definition.Name] is not null)
                    {
                        throw ServerErrors.With("invalid_attribute", entityKey, "A required attribute cannot be deleted.", ("name", definition.Name));
                    }

                    throw ServerErrors.With("required_attribute_missing", entityKey, "A required attribute is missing.", ("list", definition.Name));
                }
            }

            var owningGroupMetadata = path.Kind == RegistryPathKind.Version
                ? ServerJson.Own(Require(ServerJson.Key(RegistryPath.ForGroup(path.GroupType!, path.GroupId!))).Attributes,
                    _engine.Limits.Json).RootElement : default;
            var result = ValidateMetadata(ServerJson.Own(attributes, _engine.Limits.Json), definitions,
                new()
                {
                    Model = _model,
                    Group = path.Kind == RegistryPathKind.Version ? group : null,
                    Resource = path.Kind == RegistryPathKind.Version ? resource : null,
                    GroupMetadata = owningGroupMetadata,
                    Limits = _engine.Limits.Json
                }, entityKey, entity.IsNew ? null : entity.Original);
            var obligations = DomainObligations(result, entity.Key);
            RejectUnresolved(obligations);
            if (path.Kind is RegistryPathKind.Group or RegistryPathKind.Resource)
            {
                ValidateDeprecationRange(result.Metadata, entityKey);
            }
            var completedMetadata = result.Metadata;
            try
            {
                if (path.Kind == RegistryPathKind.Group &&
                    string.Equals(group!.Annotations.ModelCompatibleWith,
                        "https://xregistry.io/xreg/domains/endpoint/specs/model.json", StringComparison.Ordinal))
                {
                    RegistryDomainRules.ValidateEndpointMetadata(result.Metadata.RootElement, _ct);
                }
                else if (path.Kind == RegistryPathKind.Group &&
                    string.Equals(group!.Annotations.ModelCompatibleWith,
                        "https://xregistry.io/xreg/domains/message/specs/model.json", StringComparison.Ordinal))
                {
                    RegistryDomainRules.ValidateMessageGroupMetadata(result.Metadata.RootElement, _ct);
                }

                if (path.Kind == RegistryPathKind.Version &&
                    string.Equals(resource!.Annotations.ModelCompatibleWith,
                        "https://xregistry.io/xreg/domains/message/specs/model.json", StringComparison.Ordinal))
                {
                    completedMetadata = RegistryDomainRules.CompleteMessageMetadata(result.Metadata, _engine.Limits.Json, _ct);
                    RegistryDomainRules.ValidateMessageSchemaReference(completedMetadata.RootElement, _model,
                        (target, schema) => _engine.Url(ServerJson.Key(target), schema.HasDocument), _ct);
                    RegistryDomainRules.ValidateMessageGroupConstraints(completedMetadata.RootElement, group!, resource,
                        owningGroupMetadata, _ct);
                }
                else if (path.Kind == RegistryPathKind.Version &&
                    string.Equals(resource!.Annotations.ModelCompatibleWith,
                        RegistryDomainRules.CatalogModelUri, StringComparison.Ordinal))
                {
                    RegistryDomainRules.ValidateCatalogMetadata(result.Metadata.RootElement, _ct);
                }
            }
            catch (RegistryException exception)
            {
                throw MetadataError(exception, entityKey);
            }

            var changedByCompletion = !ServerJson.Equal(before, ServerJson.Object(completedMetadata));
            entity.Attributes = ServerJson.Object(completedMetadata);
            entity.Obligations = obligations;
            if (changedByCompletion)
            {
                Touch(entity);
            }
        }

        private void CollectionFields(JsonObject attributes, IEnumerable<string> names, string parent)
        {
            foreach (var name in names)
            {
                attributes[name + "url"] = _engine.Url(parent + "/" + name);
                attributes[name + "count"] = ChildCount(parent + "/" + name);
                attributes.Remove(name);
            }
        }

        private void SetOutcome(RegistryResult result)
        {
            if (_operation.Action == RegistryAction.Delete)
            {
                result.Kind = RegistryResultKind.NoContent;
                return;
            }

            var path = result.Path;
            var key = path.Kind == RegistryPathKind.Meta ? ResourceKey(path) : ServerJson.Key(path);
            if (_operation.Action is RegistryAction.Replace or RegistryAction.Patch ||
                _operation.Action == RegistryAction.Post && _operation.Path.Kind == RegistryPathKind.Resource)
            {
                if (Find(key)?.IsNew == true && result.Kind != RegistryResultKind.SeeOther)
                {
                    result.Kind = RegistryResultKind.Created;
                    result.Location = new Uri(_engine.Url(ServerJson.Key(path),
                        path.Kind is RegistryPathKind.Resource or RegistryPathKind.Version &&
                        ResourceDefinition(path).HasDocument && !result.IsDocument));
                }
            }

            if (_newVersions.Count != 0 && _operation.Path.Kind is RegistryPathKind.Resource or RegistryPathKind.Version or RegistryPathKind.Meta)
            {
                result.ContentLocation = new Uri(_engine.Url(_newVersions[^1],
                    ResourceDefinition(_operation.Path).HasDocument && !result.IsDocument));
            }
        }

        private async ValueTask PublishAsync(string correlationId, RegistryMutation? preparedEvents)
        {
            _ = Require("/");
            var mutations = new List<RegistryMutation>();
            var streams = new List<Stream>();
            try
            {
                foreach (var entity in _entities.Values.Where(static entity => entity.Dirty || entity.StorageDirty || entity.Key == "/" && !entity.Stored))
                {
                    if (entity.Deleted)
                    {
                        mutations.Add(RegistryMutation.Delete(entity.Key));
                        if (entity.ShortId is not null)
                        {
                            mutations.Add(RegistryMutation.Delete(ShortKeys + entity.ShortId));
                        }

                        continue;
                    }

                    var envelope = new JsonObject { ["attributes"] = entity.Attributes.DeepClone() };
                    if (entity.ShortId is not null)
                    {
                        envelope["shortid"] = entity.ShortId;
                    }

                    if (RegistryPath.Parse(entity.Key).Kind == RegistryPathKind.Resource)
                    {
                        envelope["nextversion"] = ServerJson.Integer(entity.NextVersion);
                    }

                    var metadata = ServerJson.Own(envelope, _engine.Limits.Json);
                    if (entity.DocumentAction == RegistryDocumentAction.Replace)
                    {
                        var stream = new MemoryStream(entity.Document!, writable: false);
                        streams.Add(stream);
                        mutations.Add(RegistryMutation.PutDocument(entity.Key, metadata, stream));
                    }
                    else
                    {
                        mutations.Add(entity.DocumentAction == RegistryDocumentAction.Remove
                            ? RegistryMutation.PutWithoutDocument(entity.Key, metadata) : RegistryMutation.Put(entity.Key, metadata));
                    }
                }

                if (_modelChanged || _snapshot.Find(ModelKey) is null)
                {
                    mutations.Add(RegistryMutation.Put(ModelKey, ServerJson.Own(new JsonObject
                    {
                        ["revision"] = Guid.NewGuid().ToString("N"),
                        ["source"] = JsonNode.Parse(_modelSource.RootElement.GetRawText()),
                        ["compiledsource"] = FreezeModel()
                    }, _engine._options.ModelCompilation.JsonLimits with { MaxBytes = _engine.Limits.MaxResponseBytes })));
                }

                if (_capabilitiesChanged)
                {
                    mutations.Add(RegistryMutation.Put(CapabilitiesKey, ServerJson.Own(new JsonObject
                    {
                        ["revision"] = _capabilities.Revision,
                        ["capabilities"] = JsonNode.Parse(_capabilities.Metadata.RootElement.GetRawText())
                    }, _engine.Limits.Json)));
                }

                mutations.AddRange(_shortMutations.Values);

                mutations.Add(RegistryMutation.Put(CorrelationsKey + "/" + correlationId,
                    ServerJson.Own(new JsonObject { ["correlationid"] = correlationId }, _engine.Limits.Json)));
                if (preparedEvents is not null)
                {
                    mutations.Add(preparedEvents);
                }

                using var candidate = await _engine._persistence.PrepareAsync(_snapshot.Generation, mutations, _ct).ConfigureAwait(false);
                await candidate.CommitAsync(_ct).ConfigureAwait(false);
            }
            finally
            {
                foreach (var stream in streams)
                {
                    stream.Dispose();
                }
            }
        }
    }
}
