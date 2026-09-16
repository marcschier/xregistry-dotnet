// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using XRegistry.Models;
using XRegistry.Validation;

namespace XRegistry.Server;

public sealed partial class RegistryEngine
{
    private sealed partial class Request
    {
        private const string OpenUsdModel = "https://xregistry.io/xreg/domains/openusd/specs/model.json";
        private bool _openUsdReverseCheck;

        private static bool IsOpaqueOpenUsd(RegistryResourceDefinition definition, JsonElement metadata) =>
            definition.Annotations.ModelCompatibleWith == OpenUsdModel &&
            metadata.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String &&
            string.Equals(format.GetString(), "Opaque/1.0", StringComparison.OrdinalIgnoreCase);

        private async ValueTask ValidateOpenUsdAsync()
        {
            var affectedGroups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entity in _entities.Values.ToArray())
            {
                if (!entity.Dirty) { continue; }
                var path = RegistryPath.Parse(entity.Key);
                if (path.GroupType is not null && _model.Groups.TryGetValue(path.GroupType, out var definition) &&
                    definition.Annotations.ModelCompatibleWith == OpenUsdModel)
                {
                    affectedGroups.Add(ServerJson.Key(RegistryPath.ForGroup(path.GroupType, path.GroupId!)));
                }
            }
            if (_modelChanged)
            {
                foreach (var definition in _model.Groups.Values.Where(static group => group.Annotations.ModelCompatibleWith == OpenUsdModel))
                {
                    foreach (var group in Children("/" + definition.Plural)) { affectedGroups.Add(group.Key); }
                }
            }
            var directlyAffected = affectedGroups.ToHashSet(StringComparer.Ordinal);
            if (!_modelChanged && _resources.Any(key =>
                _model.Groups.TryGetValue(RegistryPath.Parse(key).GroupType!, out var group) &&
                group.Annotations.ModelCompatibleWith == OpenUsdModel))
            {
                foreach (var definition in _model.Groups.Values.Where(static group => group.Annotations.ModelCompatibleWith == OpenUsdModel))
                {
                    foreach (var group in Children("/" + definition.Plural))
                    {
                        Work();
                        if (affectedGroups.Contains(group.Key)) { continue; }
                        foreach (var resourceType in definition.Resources.Values)
                        {
                            foreach (var resource in Children(group.Key + "/" + resourceType.Plural))
                            {
                                Work();
                                if (ServerJson.Text(resource.Attributes, "xref") is { } reference &&
                                    _resources.Contains(ServerJson.Key(CrossReferencePath(RegistryPath.Parse(resource.Key), reference))))
                                {
                                    affectedGroups.Add(group.Key);
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            foreach (var key in affectedGroups)
            {
                _openUsdReverseCheck = !directlyAffected.Contains(key);
                try { await ValidateOpenUsdGroupAsync(key).ConfigureAwait(false); }
                finally { _openUsdReverseCheck = false; }
            }
        }

        private async ValueTask ValidateOpenUsdGroupAsync(string key)
        {
            Work();
            if (WasDeleted(key)) { return; }
            await AuthorizeOpenUsdReadAsync(RegistryPath.Parse(key), _operation.Path.EscapedPath).ConfigureAwait(false);
            var group = Find(key);
            if (group is null) { return; }
            var definition = _model.Groups[RegistryPath.Parse(key).GroupType!];
            var plugin = definition.Singular == "usdschemaplugingroup";
            var name = OpenUsdText(group, "name");
            await ValidateOpenUsdGroupIdentityAsync(group, name, plugin).ConfigureAwait(false);
            if (plugin && group.Attributes["rootlayer"] is not null)
            {
                throw OpenUsdError(group.Key, "rootlayer", "Schema Plugin Groups do not define a root layer.");
            }

            var assets = new Dictionary<string, OpenUsdAsset>(StringComparer.Ordinal);
            var manifests = 0;
            foreach (var resourceType in definition.Resources.Values.Where(static resource => resource.Annotations.ModelCompatibleWith == OpenUsdModel))
            {
                var siblings = Children(group.Key + "/" + resourceType.Plural);
                var siblingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var sibling in siblings)
                {
                    Work();
                    WorkingBytes(2L * Id(sibling).Length);
                    if (!siblingIds.Add(Id(sibling)))
                    {
                        throw OpenUsdError(sibling.Key, "assetidentifier", "OpenUSD sibling IDs must be unique case-insensitively.");
                    }
                }
                foreach (var resource in siblings)
                {
                    Work();
                    await AuthorizeOpenUsdReadAsync(RegistryPath.Parse(resource.Key), group.Key).ConfigureAwait(false);
                    await AuthorizeOpenUsdReadAsync(RegistryPath.Parse(resource.Key + "/meta"), group.Key).ConfigureAwait(false);
                    var owner = resource;
                    if (ServerJson.Text(resource.Attributes, "xref") is { } reference)
                    {
                        var target = CrossReferencePath(RegistryPath.Parse(resource.Key), reference);
                        await AuthorizeOpenUsdReadAsync(target, group.Key).ConfigureAwait(false);
                        await AuthorizeOpenUsdReadAsync(RegistryPath.Parse(ServerJson.Key(target) + "/meta"), group.Key).ConfigureAwait(false);
                        var resolved = Find(ServerJson.Key(target));
                        if (resolved is null || resolved.Attributes["xref"] is not null) { continue; }
                        owner = resolved;
                    }
                    var originalIdentifier = await OriginalOpenUsdIdentifierAsync(resource).ConfigureAwait(false);
                    var versions = Children(owner.Key + "/versions");
                    string? identifier = null;
                    Entity? current = null;
                    foreach (var version in versions)
                    {
                        Work();
                        await AuthorizeOpenUsdReadAsync(RegistryPath.Parse(version.Key), group.Key).ConfigureAwait(false);
                        var actual = OpenUsdText(version, "assetidentifier");
                        ValidateOpenUsdIdentity(resource, version, actual, siblingIds, originalIdentifier is not null);
                        if (identifier is not null && identifier != actual ||
                            originalIdentifier is not null && originalIdentifier != actual)
                        {
                            throw OpenUsdError(version.Key, "assetidentifier", "An artifact's authoritative identifier cannot change across Versions.");
                        }
                        identifier = actual;
                        await ValidateOpenUsdDigestAsync(version, resourceType).ConfigureAwait(false);
                        var kind = OpenUsdText(version, "assetkind");
                        var format = OpenUsdText(version, "format");
                        if (plugin && (kind == "RootLayer" ||
                            kind == "SchemaPlugin" && !string.Equals(format, "USD-PlugInfo/1.0", StringComparison.OrdinalIgnoreCase) ||
                            kind == "GeneratedSchema" && !string.Equals(format, "USD-GeneratedSchema/1.0", StringComparison.OrdinalIgnoreCase)))
                        {
                            throw OpenUsdError(version.Key, "assetkind", "The artifact role or format is not valid for a Schema Plugin Group.");
                        }
                        if (plugin && kind == "SchemaPlugin")
                        {
                            await ValidateOpenUsdManifestAsync(group, version, resourceType).ConfigureAwait(false);
                        }
                        if (Id(version) == ServerJson.Text(owner.Attributes, "defaultversionid")) { current = version; }
                    }
                    if (current is null || identifier is null)
                    {
                        throw OpenUsdError(resource.Key, "defaultversionid", "An OpenUSD artifact needs a current Version.");
                    }
                    if (!assets.TryAdd(identifier, new(resource, current)))
                    {
                        throw OpenUsdError(resource.Key, "assetidentifier", "Distinct artifacts cannot claim the same authoritative identifier.");
                    }
                    if (OpenUsdText(current, "assetkind") == "SchemaPlugin") { manifests++; }
                }
            }
            if (plugin)
            {
                if (manifests == 0)
                {
                    throw OpenUsdError(group.Key, "name", "The Group needs a SchemaPlugin document declaring its plugin name.");
                }
            }
            else
            {
                var roots = assets.Where(static pair => ServerJson.Text(pair.Value.Current.Attributes, "assetkind") == "RootLayer").ToArray();
                if (roots.Length != 1)
                {
                    throw OpenUsdError(group.Key, "rootlayer", "An Asset Container Group must contain exactly one current RootLayer.");
                }
                if (ServerJson.Text(group.Attributes, "rootlayer") is { } root && root != roots[0].Key)
                {
                    throw OpenUsdError(group.Key, "rootlayer", "rootlayer must name the current RootLayer's assetidentifier.");
                }
            }
            foreach (var asset in assets.Values)
            {
                if (asset.Current.Attributes["dependson"] is not JsonArray dependencies) { continue; }
                foreach (var dependency in dependencies)
                {
                    Work();
                    var reference = dependency!.GetValue<string>();
                    var normalized = OpenUsdIdentifiers.NormalizeAssetIdentifier(reference, _engine.Limits.Json.MaxBytes, _ct);
                    if (assets.ContainsKey(normalized)) { continue; }
                    var bracket = normalized.IndexOf('[', StringComparison.Ordinal);
                    if (bracket > 0 && normalized.EndsWith(']') &&
                        assets.TryGetValue(normalized[..bracket], out var package) &&
                        OpenUsdText(package.Current, "assetkind") == "Package")
                    {
                        continue;
                    }
                    throw OpenUsdError(asset.Resource.Key, "dependson", "A declared dependency has no artifact or canonical package in this Group.");
                }
            }
        }

        // Reverse checks validate existing stored relations; errors identify the requested mutation, never an unreadable referrer.
        private ValueTask AuthorizeOpenUsdReadAsync(RegistryPath path, string subject) =>
            _openUsdReverseCheck ? ValueTask.CompletedTask : AuthorizeConstraintReadAsync(path, subject);

        private async ValueTask ValidateOpenUsdGroupIdentityAsync(Entity group, string name, bool plugin)
        {
            string candidate;
            string? fallback;
            try
            {
                var symbolic = plugin || !RegistryId.IsValid(name);
                candidate = symbolic ? OpenUsdIdentifiers.CreateSymbolicIdCandidate(name, _engine.Limits.Json.MaxBytes, _ct) : name;
                fallback = symbolic ? OpenUsdIdentifiers.CreateSymbolicIdCollisionCandidate(name, _engine.Limits.Json.MaxBytes, _ct) : null;
            }
            catch (ArgumentException exception)
            {
                throw OpenUsdError(group.Key, "name", exception.Message);
            }
            if (Id(group) != candidate && Id(group) != fallback)
            {
                throw OpenUsdError(group.Key, "name", "The Group identifier does not match its authoritative name.");
            }
            Work();
            var before = _snapshot.Find(group.Key);
            string? originalName = null;
            if (before is not null)
            {
                WorkingBytes(System.Text.Encoding.UTF8.GetByteCount(before.Metadata.RootElement.GetRawText()));
                if (before.Metadata.RootElement.GetProperty("attributes").TryGetProperty("name", out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    originalName = value.GetString();
                }
            }
            if (originalName is not null && originalName != name)
            {
                throw OpenUsdError(group.Key, "name", "A published Group's authoritative source name cannot be rebound.");
            }
            var occupied = false;
            foreach (var sibling in Children("/" + RegistryPath.Parse(group.Key).GroupType))
            {
                Work();
                if (sibling.Key == group.Key) { continue; }
                var id = Id(sibling);
                var isCandidate = string.Equals(id, candidate, StringComparison.OrdinalIgnoreCase);
                if (!isCandidate && !string.Equals(id, fallback, StringComparison.OrdinalIgnoreCase)) { continue; }
                await AuthorizeOpenUsdReadAsync(RegistryPath.Parse(sibling.Key), group.Key).ConfigureAwait(false);
                if (OpenUsdText(sibling, "name") == name)
                {
                    throw OpenUsdError(group.Key, "name", "Distinct Groups cannot claim the same authoritative source name.");
                }
                occupied |= isCandidate;
            }
            if (Id(group) != candidate && originalName is null && !occupied)
            {
                throw OpenUsdError(group.Key, "name", "A new symbolic Group fallback requires an occupied candidate.");
            }
        }

        private void ValidateOpenUsdIdentity(Entity resource, Entity version, string identifier,
            HashSet<string> siblingIds, bool hasExistingBinding)
        {
            if (IsOpenUsdVersionReference(identifier))
            {
                throw OpenUsdError(version.Key, "assetidentifier", "A specific Registry Version XID cannot be an asset identifier.");
            }
            try
            {
                var candidate = OpenUsdIdentifiers.CreateSymbolicIdCandidate(identifier, _engine.Limits.Json.MaxBytes, _ct);
                if (OpenUsdIdentifiers.NormalizeAssetIdentifier(identifier, _engine.Limits.Json.MaxBytes, _ct) != identifier)
                {
                    throw OpenUsdError(version.Key, "assetidentifier", "The normalized authored identifier does not match the Resource's symbolic identifier.");
                }
                if (candidate != Id(resource) &&
                    (OpenUsdIdentifiers.CreateSymbolicIdCollisionCandidate(identifier, _engine.Limits.Json.MaxBytes, _ct) != Id(resource) ||
                     !hasExistingBinding && !siblingIds.Contains(candidate)))
                {
                    throw OpenUsdError(version.Key, "assetidentifier", "The Resource must retain its symbolic ID or use the sole fallback of an occupied candidate.");
                }
            }
            catch (ArgumentException exception)
            {
                throw OpenUsdError(version.Key, "assetidentifier", exception.Message);
            }
            if (OpenUsdText(version, "name") != identifier)
            {
                throw OpenUsdError(version.Key, "name", "The artifact name must preserve its assetidentifier verbatim.");
            }
        }

        private bool IsOpenUsdVersionReference(string identifier)
        {
            if (!identifier.StartsWith('/')) { return false; }
            RegistryPath path;
            try { path = RegistryPath.Parse(identifier); }
            catch (RegistryException) { return false; }
            return path.Kind == RegistryPathKind.Version && !path.IsDetails &&
                _model.Groups.TryGetValue(path.GroupType!, out var group) &&
                group.Resources.ContainsKey(path.ResourceType!);
        }

        private async ValueTask<string?> OriginalOpenUsdIdentifierAsync(Entity resource)
        {
            Work();
            var recordBefore = _snapshot.Find(resource.Key);
            if (recordBefore is null) { return null; }
            WorkingBytes(System.Text.Encoding.UTF8.GetByteCount(recordBefore.Metadata.RootElement.GetRawText()));
            var key = resource.Key;
            if (recordBefore.Metadata.RootElement.GetProperty("attributes").TryGetProperty("xref", out var reference) &&
                reference.ValueKind == JsonValueKind.String)
            {
                // Identity belongs to the published local Resource, including its previous one-hop alias target.
                var previousTarget = CrossReferencePath(RegistryPath.Parse(key), reference.GetString()!);
                await AuthorizeOpenUsdReadAsync(previousTarget, resource.Key).ConfigureAwait(false);
                key = ServerJson.Key(previousTarget);
                await AuthorizeOpenUsdReadAsync(RegistryPath.Parse(key + "/meta"), resource.Key).ConfigureAwait(false);
            }
            string? original = null;
            foreach (var record in _snapshot.GetChildren(key + "/versions"))
            {
                Work();
                await AuthorizeOpenUsdReadAsync(RegistryPath.Parse(record.Key), resource.Key).ConfigureAwait(false);
                WorkingBytes(System.Text.Encoding.UTF8.GetByteCount(record.Metadata.RootElement.GetRawText()));
                var attributes = record.Metadata.RootElement.GetProperty("attributes");
                if (!attributes.TryGetProperty("assetidentifier", out var value) || value.ValueKind != JsonValueKind.String) { continue; }
                var identifier = value.GetString();
                if (original is not null && original != identifier)
                {
                    throw OpenUsdError(resource.Key, "assetidentifier", "Existing Versions have inconsistent authoritative identifiers.");
                }
                original = identifier;
            }
            return original;
        }

        private async ValueTask ValidateOpenUsdDigestAsync(Entity version, RegistryResourceDefinition definition)
        {
            var digest = ServerJson.Text(version.Attributes, "digest");
            var algorithm = ServerJson.Text(version.Attributes, "digestalg");
            try { OpenUsdArtifactIntegrity.ValidateMetadata(digest, algorithm, OpenUsdDigestRole.Producer); }
            catch (ArgumentException exception)
            {
                throw OpenUsdError(version.Key, algorithm is null ? "digestalg" : "digest", exception.Message);
            }
            if (digest is null) { return; }
            if (ServerJson.Text(version.Attributes, definition.Singular + "url") is not null)
            {
                throw OpenUsdError(version.Key, "digest", "A delegated artifact cannot claim an unverified external digest.");
            }
            var bytes = await ReadDocumentAsync(version).ConfigureAwait(false);
            Work();
            var actual = algorithm switch
            {
                "Sha256" => SHA256.HashData(bytes),
                "Sha384" => SHA384.HashData(bytes),
                "Sha512" => SHA512.HashData(bytes),
                _ => throw new InvalidOperationException("An unvalidated digest algorithm reached content verification."),
            };
            _ct.ThrowIfCancellationRequested();
            if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(digest)))
            {
                throw OpenUsdError(version.Key, "digest", "The declared artifact digest does not match its exact Document bytes.");
            }
        }

        private async ValueTask ValidateOpenUsdManifestAsync(Entity group, Entity version, RegistryResourceDefinition definition)
        {
            if (ServerJson.Text(version.Attributes, definition.Singular + "url") is not null)
            {
                throw OpenUsdError(version.Key, "usdasseturl", "A delegated plugin manifest cannot be inspected without explicitly acquired bytes.");
            }
            var bytes = await ReadDocumentAsync(version).ConfigureAwait(false);
            WorkingBytes(2L * bytes.Length);
            RegistryJson document;
            try { document = RegistryJson.Parse(bytes, _engine.Limits.Json); }
            catch (RegistryException exception)
            {
                throw OpenUsdError(version.Key, "format", exception.Diagnostic.Message);
            }
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Plugins", out var plugins) ||
                plugins.ValueKind != JsonValueKind.Array || plugins.GetArrayLength() == 0 ||
                plugins[0].ValueKind != JsonValueKind.Object || !plugins[0].TryGetProperty("Name", out var name) ||
                name.ValueKind != JsonValueKind.String || name.GetString() != OpenUsdText(group, "name"))
            {
                throw OpenUsdError(version.Key, "name", "Plugins[0].Name must match the containing Group's exact plugin name.");
            }
        }

        private string OpenUsdText(Entity entity, string name) =>
            entity.Attributes[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text)
                ? text : throw OpenUsdError(entity.Key, name, "The OpenUSD declaration requires a nonempty string.");

        private RegistryException OpenUsdError(string subject, string attribute, string detail) =>
            _modelChanged ? ServerErrors.Create("model_compliance_error", "/model", detail) :
                ServerErrors.With("invalid_attribute", _openUsdReverseCheck ? _operation.Path.EscapedPath : subject,
                    _openUsdReverseCheck ? "The update would violate an existing OpenUSD Group constraint." : detail,
                    ("name", attribute));

        private sealed record OpenUsdAsset(Entity Resource, Entity Current);
    }
}
