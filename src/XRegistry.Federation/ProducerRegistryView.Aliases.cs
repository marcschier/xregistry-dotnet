using System.Text.Json.Nodes;

namespace XRegistry.Federation;

public sealed partial class ProducerRegistryView
{
    private readonly Dictionary<(Slot Source, string Owner), AliasState?> _aliases = [];
    private readonly HashSet<(Slot Source, string Owner)> _aliasModelChecks = [];
    private sealed record AliasState(Slot Source, string Owner, string Target, string CrossReference,
        JsonObject? TargetEntity, JsonObject? TargetMeta)
    {
        internal bool Dangling => TargetEntity is null || TargetMeta is null;
    }

    private static bool HasAlias(JsonObject value) =>
        value["xref"] is not null || value["meta"] is JsonObject meta && meta["xref"] is not null;

    private async ValueTask<JsonObject> ReadUnitMetaAsync(Slot source, string owner, CancellationToken token)
    {
        var resource = Entity(await source.ReadAsync(new(FederationOperation.Entity, owner), token).ConfigureAwait(false));
        CheckEntityIdentity(resource, PathFor(owner));
        var meta = resource["meta"] is JsonObject embedded
            ? embedded.DeepClone().AsObject()
            : Entity(await source.ReadAsync(new(FederationOperation.Entity, owner + "/meta"), token).ConfigureAwait(false));
        CheckEntityIdentity(meta, PathFor(owner + "/meta"));
        return meta;
    }

    private async ValueTask<AliasState?> AliasAsync(Slot source, string owner, CancellationToken token)
    {
        if (_aliases.TryGetValue((source, owner), out var cached)) { return cached; }
        var meta = await ReadUnitMetaAsync(source, owner, token).ConfigureAwait(false);
        if (meta["xref"] is null)
        {
            _aliases.Add((source, owner), null);
            return null;
        }
        var target = ValidateAliasTarget(source, owner, meta);
        JsonObject? targetEntity = null;
        JsonObject? targetMeta = null;
        var targetExists = false;
        try
        {
            targetEntity = Entity(await source.ReadAsync(new(FederationOperation.Entity, target), token).ConfigureAwait(false));
            targetExists = true;
            targetMeta = targetEntity["meta"] is JsonObject targetEmbedded
                ? targetEmbedded.DeepClone().AsObject()
                : Entity(await source.ReadAsync(new(FederationOperation.Entity, target + "/meta"), token).ConfigureAwait(false));
            if (targetMeta["xref"] is not null) { targetEntity = null; targetMeta = null; }
        }
        catch (FederationException exception) when (exception.Code is FederationErrorCode.NotFound or FederationErrorCode.PolicyDenied)
        {
            targetEntity = null;
            targetMeta = null;
        }
        await CheckAliasProjectionAsync(source, target, targetExists, token).ConfigureAwait(false);
        var result = new AliasState(source, owner, target, RequiredString(meta, "xref"), targetEntity, targetMeta);
        _aliases.Add((source, owner), result);
        return result;
    }

    private string ValidateAliasTarget(Slot source, string owner, JsonObject meta)
    {
        var definition = Resource(PathFor(owner))!;
        var id = PathFor(owner).ResourceId!.Value;
        if (!FederationSyntax.SameXid(RequiredString(meta, "xid"), owner + "/meta") ||
            RequiredString(meta, definition.Singular + "id") != id)
        {
            throw Invalid("Alias Meta has a different source identity.");
        }
        try
        {
            var path = RegistryPath.Parse(RequiredString(meta, "xref"));
            if (path.Kind != RegistryPathKind.Resource || path.IsDetails ||
                !source.Model!.Groups.TryGetValue(path.GroupType!, out var group) ||
                !group.Resources.TryGetValue(path.ResourceType!, out var actual) ||
                !ReferenceEquals(DocumentTreeFormat.Resource(source.Model, FederationSyntax.Xid(owner)), actual))
            {
                throw new FederationException(FederationErrorCode.InvalidPackage,
                    "xref must name the same actual source-local Resource type.", "malformed_xref");
            }
            return LogicalPath(path);
        }
        catch (RegistryException exception)
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "xref must be a valid source-local Resource XID.", "malformed_xref", exception);
        }
    }

    private async ValueTask CheckAliasProjectionAsync(Slot source, string target, bool sourceTargetExists, CancellationToken token)
    {
        if (_slots[0].Owner == FederationResolutionOwner.Producer) { return; }
        foreach (var candidate in _slots)
        {
            if (ReferenceEquals(candidate, source))
            {
                if (sourceTargetExists) { return; }
                continue;
            }
            var other = await candidate.GetAsync(token).ConfigureAwait(false);
            try { await other.ReadAsync(new(FederationOperation.Entity, target), token).ConfigureAwait(false); }
            catch (FederationException exception) when (exception.Code is FederationErrorCode.NotFound or FederationErrorCode.PolicyDenied) { continue; }
            throw new FederationException(FederationErrorCode.Ambiguous,
                "The source-local alias target is owned by another source in the aggregate; its meaning cannot be projected.",
                "alias_origin_conflict");
        }
    }

    private async ValueTask<ProducerRegistryResult> ReadAliasAsync(AliasState alias, RegistryPath path, bool document, CancellationToken token)
    {
        if (path.Kind == RegistryPathKind.Meta) { await ValidateAliasMetaAsync(alias, token).ConfigureAwait(false); }
        if (path.Kind == RegistryPathKind.VersionCollection)
        {
            var map = new JsonObject();
            if (!alias.Dangling)
            {
                foreach (var member in await VersionsAsync(alias.Source, alias.Target, token).ConfigureAwait(false))
                {
                    var actual = PathFor(alias.Target + "/versions/" + member.Key, true);
                    var projectedPath = PathFor(alias.Owner + "/versions/" + member.Key, true);
                    var projected = Reidentify(await NormalizeAsync(alias.Source, Entity(member.Value.Result), actual, token).ConfigureAwait(false), projectedPath);
                    await ValidateAliasConstraintsAsync(alias, projected, projectedPath, token).ConfigureAwait(false);
                    map[member.Key] = projected;
                }
            }
            return Result(LogicalPath(path), Element(map));
        }
        if (alias.Dangling)
        {
            if (path.Kind == RegistryPathKind.Version) { throw NotFound(); }
            var dangling = Dangling(alias, path.Kind == RegistryPathKind.Meta);
            return Result(LogicalPath(path), Element(dangling),
                document ? new FederationDocument([], "application/octet-stream") : null);
        }
        if (document)
        {
            var versionId = path.VersionId?.Value ?? RequiredString(alias.TargetMeta!, "defaultversionid");
            var actual = alias.Target + "/versions/" + versionId;
            var selected = await alias.Source.ReadAsync(new(FederationOperation.Document, actual), token).ConfigureAwait(false);
            CheckDocumentIdentity(actual, selected.SelectedXid);
            var version = Entity(await alias.Source.ReadAsync(new(FederationOperation.Entity, actual), token).ConfigureAwait(false));
            var ownVersion = alias.Owner + "/versions/" + versionId;
            var projectedPath = PathFor(ownVersion, true);
            var projected = Reidentify(await NormalizeAsync(alias.Source, version,
                PathFor(actual, true), token).ConfigureAwait(false), projectedPath);
            await ValidateAliasConstraintsAsync(alias, projected, projectedPath, token).ConfigureAwait(false);
            return Result(ownVersion, Element(projected), selected.Document, selected.ExternalDocument);
        }
        return Result(LogicalPath(path), Element(await AliasMetadataAsync(alias, path, token).ConfigureAwait(false)));
    }

    private async ValueTask ValidateAliasMetaAsync(AliasState alias, CancellationToken token)
    {
        var meta = Entity(await alias.Source.ReadAsync(
            new(FederationOperation.Entity, alias.Owner + "/meta"), token).ConfigureAwait(false));
        if (ValidateAliasTarget(alias.Source, alias.Owner, meta) != alias.Target)
        {
            throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                "The logical alias Meta changed its source-local target during the capture.");
        }
    }

    private async ValueTask<JsonObject> AliasMetadataAsync(AliasState alias, RegistryPath path, CancellationToken token)
    {
        if (alias.Dangling)
        {
            if (path.Kind == RegistryPathKind.Version) { throw NotFound(); }
            return Dangling(alias, path.Kind == RegistryPathKind.Meta);
        }
        if (path.Kind == RegistryPathKind.Meta) { return await AliasMetaAsync(alias, token).ConfigureAwait(false); }
        if (path.Kind == RegistryPathKind.Version)
        {
            var target = alias.Target + "/versions/" + path.VersionId!.Value;
            var result = await alias.Source.ReadAsync(new(FederationOperation.Entity, target), token).ConfigureAwait(false);
            var version = Reidentify(await NormalizeAsync(alias.Source, Entity(result), PathFor(target, true), token).ConfigureAwait(false), path);
            await ValidateAliasConstraintsAsync(alias, version, path, token).ConfigureAwait(false);
            return version;
        }
        var targetResource = FederationReadResult.FromMetadata(alias.Target, alias.Source.Context,
            RegistryJson.Parse(alias.TargetEntity!.ToJsonString()).RootElement);
        var projected = await MaterializeAsync(alias.Source, targetResource, PathFor(alias.Target, true), token).ConfigureAwait(false);
        projected = Reidentify(projected, path);
        projected["metaurl"] = Url(alias.Owner + "/meta");
        projected["versionsurl"] = Url(alias.Owner + "/versions");
        await ValidateAliasConstraintsAsync(alias, projected, path, token).ConfigureAwait(false);
        return projected;
    }

    private async ValueTask ValidateAliasConstraintsAsync(AliasState alias, JsonObject projected, RegistryPath path, CancellationToken token)
    {
        var group = _model.Groups[path.GroupType!];
        var resource = Resource(path)!;
        if (!group.Constraints.Values.Any(c => c.ResourceType == resource.Plural)) { return; }
        var metadata = await GroupContextAsync(path, token).ConfigureAwait(false);
        try
        {
            if (!_aliasModelChecks.Contains((alias.Source, alias.Owner)))
            {
                await VersionsAsync(alias.Source, alias.Target, token).ConfigureAwait(false);
                foreach (var (id, version) in _modelVersions[(alias.Source, alias.Target)])
                {
                    token.ThrowIfCancellationRequested();
                    ChargeGroupConstraintWork(group, resource, token);
                    var value = Reidentify(version.DeepClone().AsObject(), PathFor(alias.Owner + "/versions/" + id, true));
                    RegistryMetadataValidator.ValidateGroupConstraints(RegistryJson.Parse(value.ToJsonString(), JsonLimits()),
                        group, resource, metadata, ModelJsonLimits(), token);
                }
                _aliasModelChecks.Add((alias.Source, alias.Owner));
            }
            ChargeGroupConstraintWork(group, resource, token);
            RegistryMetadataValidator.ValidateGroupConstraints(RegistryJson.Parse(projected.ToJsonString(), JsonLimits()),
                group, resource, metadata, ModelJsonLimits(), token);
        }
        catch (RegistryException exception) { throw FederationJson.FromCore(exception); }
    }

    private async ValueTask<JsonObject> AliasMetaAsync(AliasState alias, CancellationToken token)
    {
        var targetMeta = await NormalizeAsync(alias.Source, alias.TargetMeta!.DeepClone().AsObject(),
            PathFor(alias.Target + "/meta"), token).ConfigureAwait(false);
        var value = Reidentify(targetMeta, PathFor(alias.Owner + "/meta"));
        value["xref"] = alias.CrossReference;
        value["defaultversionurl"] = Url(alias.Owner + "/versions/" + RequiredString(value, "defaultversionid"),
            Resource(PathFor(alias.Owner))!.HasDocument);
        return value;
    }

    private JsonObject Dangling(AliasState alias, bool metaOnly)
    {
        var definition = Resource(PathFor(alias.Owner))!;
        var id = PathFor(alias.Owner).ResourceId!.Value;
        var meta = new JsonObject
        {
            [definition.Singular + "id"] = id,
            ["self"] = Url(alias.Owner + "/meta"),
            ["xid"] = alias.Owner + "/meta",
            ["xref"] = alias.CrossReference
        };
        return metaOnly ? meta : new JsonObject
        {
            [definition.Singular + "id"] = id,
            ["self"] = Url(alias.Owner, definition.HasDocument),
            ["xid"] = alias.Owner,
            ["metaurl"] = Url(alias.Owner + "/meta"),
            ["meta"] = meta
        };
    }

    private JsonObject Reidentify(JsonObject value, RegistryPath path)
    {
        var target = LogicalPath(path);
        var resource = Resource(path)!;
        value[resource.Singular + "id"] = path.ResourceId!.Value;
        value["xid"] = target;
        value["self"] = Url(target, resource.HasDocument && path.Kind is RegistryPathKind.Resource or RegistryPathKind.Version);
        value.Remove("shortself");
        return value;
    }
}
