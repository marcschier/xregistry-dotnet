// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XRegistry.Models;

namespace XRegistry.Federation;

/// <summary>One selected WoT Version and the exact bytes used to compute its digest.</summary>
public sealed class WotResolvedDocument
{
    internal WotResolvedDocument(string resourceXid, WotReferenceTargetKind kind, WotResolvedVersion version,
        NativeRegistryContext context, FederationDocument document)
    {
        ResourceXid = resourceXid;
        Kind = kind;
        Version = version;
        Context = context;
        Document = document;
    }

    /// <summary>Gets the Resource selected within the caller's Group.</summary>
    public string ResourceXid { get; }
    /// <summary>Gets the WoT kind established by the captured source model.</summary>
    public WotReferenceTargetKind Kind { get; }
    /// <summary>Gets the identity, Version id, SHA-256 digest and Registry origin.</summary>
    public WotResolvedVersion Version { get; }
    /// <summary>Gets the retained source context, without asserting snapshot isolation.</summary>
    public NativeRegistryContext Context { get; }
    /// <summary>Gets independently owned document bytes; no URL is dereferenced.</summary>
    public FederationDocument Document { get; }
}

/// <summary>Resolves parsed SemVer WoT references through a selected native source or retained revision.</summary>
public static class WotRegistryResolver
{
    /// <summary>Resolves a parsed document-local reference against an already acquired containing revision.</summary>
    /// <remarks>
    /// The caller establishes locality through URI/base/context processing. This method never opens
    /// a source or selects a different Version. Authored version fields come from the retained bytes,
    /// not request.ContainingRevision. Parsing consumes work and checks object/result limits without
    /// recharging the already acquired input bytes, object or source requests.
    /// </remarks>
    public static async ValueTask<WotResolvedDocument> ResolveLocalAsync(WotResolvedDocument containingRevision,
        WotVersionResolutionRequest request, FederationReadBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(containingRevision);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.IsDocumentLocal || request.Operator == WotReferenceOperator.ThingModelTypeLink)
        {
            throw new ArgumentException("A parsed document-local tm:ref or td:ref is required.", nameof(request));
        }
        if (request.TargetWotId != containingRevision.Version.WotId)
        {
            throw new RegistryException(new("not-found", "", "The local target is not the containing WoT revision."));
        }
        if (request.TargetKind != containingRevision.Kind)
        {
            throw new RegistryException(new("invalid-floor", "", "The local reference has a different WoT kind."));
        }

        var captured = containingRevision.Version;
        var revision = new WotVersionCandidate(captured.VersionId, captured.ContentDigest, captured.Origin);
        if (request.VersionIdPin is not null || request.DigestPin is not null)
        {
            throw new RegistryException(new("invalid-pin", "",
                "A document-local reference cannot carry a Registry Version pin."));
        }

        budget ??= new();
        budget.CheckObjectBytes(containingRevision.Document.Length);
        RegistryJson parsed;
        using (var stream = containingRevision.Document.OpenRead())
        {
            try
            {
                parsed = await RegistryJson.ParseAsync(stream, new RegistryJsonLimits
                {
                    MaxBytes = budget.Limits.MaxObjectBytes,
                    MaxDepth = budget.Limits.MaxJsonDepth,
                    MaxNodes = (int)Math.Min(int.MaxValue, budget.Limits.MaxWork)
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (RegistryException exception)
            {
                throw FederationJson.FromCore(exception);
            }
        }
        var root = parsed.RootElement;
        FederationJson.CountWork(root, budget, cancellationToken);
        FederationJson.RequireObject(root);
        if (FederationJson.String(root, "id") != captured.WotId)
        {
            throw FederationJson.Invalid("The containing document's id differs from its acquired WoT identity.");
        }

        if (request.MinimumVersion is not null && root.TryGetProperty("version", out var version))
        {
            FederationJson.RequireObject(version);
            var field = containingRevision.Kind == WotReferenceTargetKind.ThingModel ? "model" : "instance";
            if (version.TryGetProperty(field, out var authored))
            {
                if (authored.ValueKind != JsonValueKind.String)
                {
                    throw new RegistryException(new("invalid-version", "/version/" + field,
                        "A local floor requires an authored SemVer string."));
                }
                revision = revision with { AuthoredVersion = authored.GetString() };
            }
        }
        WotVersionResolver.Resolve(null, request with { ContainingRevision = revision });
        budget.CheckResultBytes(containingRevision.Document.Length);
        cancellationToken.ThrowIfCancellationRequested();
        return containingRevision;
    }

    /// <summary>Resolves a parsed, nonlocal reference within one explicitly selected Resource collection.</summary>
    /// <remarks>
    /// The caller owns source authorization and URI/base/context processing. The source must expose
    /// its captured SemVer WoT model. Lookup uses exact wotid equality, rejects competing Resources,
    /// and never follows successors, document URLs or another source. Digest pins are compared with
    /// hashes of acquired bytes, not advisory metadata. All reads share the supplied finite budget.
    /// </remarks>
    public static ValueTask<WotResolvedDocument> ResolveAsync(IFederationReadSource source,
        string resourceCollection, WotVersionResolutionRequest request,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        var collection = new FederationReadRequest(FederationOperation.Collection, resourceCollection);
        if (collection.Parts.Length != 3 || request.IsDocumentLocal)
        {
            throw new ArgumentException("An explicit Group Resource collection and a nonlocal reference are required.");
        }

        return new Resolution(source, collection, request, budget ?? new(), cancellationToken).RunAsync();
    }

    private sealed class Resolution
    {
        private readonly IFederationReadSource _source;
        private readonly FederationReadRequest _collection;
        private readonly WotVersionResolutionRequest _request;
        private readonly FederationReadBudget _budget;
        private readonly CancellationToken _token;
        private readonly NativeRegistryContext _context;
        private readonly RegistryModel _model;
        private readonly FederationResolutionOwner _owner;

        internal Resolution(IFederationReadSource source, FederationReadRequest collection,
            WotVersionResolutionRequest request, FederationReadBudget budget, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _source = source;
            _collection = collection;
            _request = request;
            _budget = budget;
            _token = token;
            _context = source.Context;
            _owner = FederationCapabilities.GetResolutionOwner(source.Capabilities);
            _model = source.Model ?? throw new FederationException(FederationErrorCode.UnsupportedOperation,
                "WoT acquisition requires the source's captured model.");
            var singular = request.TargetKind switch
            {
                WotReferenceTargetKind.ThingModel => "thingmodel",
                WotReferenceTargetKind.ThingDescription => "thingdescription",
                _ => throw new ArgumentOutOfRangeException(nameof(request))
            };
            if (!_model.Groups.TryGetValue(collection.Parts[0], out var group) ||
                !group.Resources.TryGetValue(collection.Parts[2], out var resource) ||
                resource.Singular != singular || !resource.HasDocument ||
                resource.VersionMode != "semver")
            {
                throw new FederationException(FederationErrorCode.UnsupportedOperation,
                    "The selected collection is not a captured SemVer WoT Resource type.");
            }
            if (_model.Attributes.TryGetValue("wotversionprofile", out var profile) &&
                (!profile.ReadOnly || profile.DefaultValue.ValueKind != JsonValueKind.String ||
                    profile.DefaultValue.GetString() != "semver"))
            {
                throw new FederationException(FederationErrorCode.UnsupportedVersion,
                    "This resolver requires a fixed SemVer WoT profile.");
            }
        }

        internal async ValueTask<WotResolvedDocument> RunAsync()
        {
            var resources = Members(await MetadataAsync(_collection).ConfigureAwait(false), _collection.Target);
            string? resourceXid = null;
            JsonElement meta = default;
            foreach (var member in resources)
            {
                _token.ThrowIfCancellationRequested();
                var xid = _collection.Target + "/" + Uri.EscapeDataString(member.Name);
                var metaPath = xid + "/meta";
                var resourceMeta = Entity(await MetadataAsync(new(FederationOperation.Entity, metaPath))
                    .ConfigureAwait(false), metaPath);
                if (resourceMeta.TryGetProperty("xref", out _))
                {
                    throw new FederationException(FederationErrorCode.UnsupportedOperation,
                        "A WoT Resource alias must be resolved to its authorized source before Version selection.");
                }
                var defaultVersionId = FederationJson.String(resourceMeta, "defaultversionid");
                var defaultPath = xid + "/versions/" + Uri.EscapeDataString(defaultVersionId);
                var defaultVersion = Entity(await MetadataAsync(new(FederationOperation.Entity, defaultPath))
                    .ConfigureAwait(false), defaultPath);
                if (FederationJson.String(defaultVersion, "versionid") != defaultVersionId)
                {
                    throw FederationJson.Invalid("A WoT default Version has a different Version identity.");
                }
                if (FederationJson.String(defaultVersion, "wotid") != _request.TargetWotId) { continue; }
                if (resourceXid is not null)
                {
                    throw new FederationException(FederationErrorCode.Ambiguous,
                        "Several Resources in the selected Group have the requested WoT identity.");
                }
                resourceXid = xid;
                meta = resourceMeta;
            }
            if (resourceXid is null)
            {
                throw new RegistryException(new("not-found", "", "No WoT Resource matches in the selected Group."));
            }

            var defaultId = FederationJson.String(meta, "defaultversionid");
            var sticky = false;
            if (meta.TryGetProperty("defaultversionsticky", out var stickyValue))
            {
                if (stickyValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw FederationJson.Invalid("A WoT sticky-default flag must be a boolean.");
                }
                sticky = stickyValue.GetBoolean();
            }

            var versionsPath = resourceXid + "/versions";
            var versions = Members(await MetadataAsync(new(FederationOperation.Collection, versionsPath))
                .ConfigureAwait(false), versionsPath);
            var candidates = new List<WotVersionCandidate>();
            foreach (var member in versions)
            {
                var id = FederationJson.String(member.Value, "versionid");
                if (id != member.Name || FederationJson.String(member.Value, "wotid") != _request.TargetWotId)
                {
                    throw FederationJson.Invalid("A WoT Version has a different identity from its selected Resource.");
                }
                candidates.Add(new(id, "", _context.Source));
            }

            var captured = new WotVersionResource(_request.TargetWotId, _request.TargetKind,
                defaultId, sticky, candidates);
            var matchingDocuments = new Dictionary<string, FederationDocument>(StringComparer.Ordinal);
            if (_request.DigestPin is not null && _request.VersionIdPin is null)
            {
                for (var index = 0; index < candidates.Count; index++)
                {
                    var candidate = candidates[index];
                    var acquired = await AcquireAsync(versionsPath, candidate.VersionId).ConfigureAwait(false);
                    candidates[index] = candidate with { ContentDigest = acquired.Digest };
                    if (acquired.Digest == _request.DigestPin)
                    {
                        matchingDocuments.Add(candidate.VersionId, acquired.Document);
                    }
                }
            }

            var selected = WotVersionResolver.SelectVersionId(captured, _request);
            FederationDocument document;
            string digest;
            if (_request.DigestPin is not null)
            {
                document = matchingDocuments[selected];
                digest = _request.DigestPin;
            }
            else
            {
                (document, digest) = await AcquireAsync(versionsPath, selected).ConfigureAwait(false);
            }
            _budget.CheckResultBytes(document.Length);
            CheckContext();
            _token.ThrowIfCancellationRequested();
            return new(resourceXid, _request.TargetKind,
                new(_request.TargetWotId, selected, digest, _context.Source), _context, document);
        }

        private async ValueTask<(FederationDocument Document, string Digest)> AcquireAsync(string versionsPath, string id)
        {
            var path = versionsPath + "/" + Uri.EscapeDataString(id);
            var read = await ReadAsync(new(FederationOperation.Document, path)).ConfigureAwait(false);
            var document = read.Document ?? throw new FederationException(FederationErrorCode.Unavailable,
                "The selected WoT Version has no acquired document bytes; external URLs are not followed.");
            _budget.CheckObjectBytes(document.Length);
            _budget.ChargeBytes(document.Length);
            _budget.ChargeObject();
            using var stream = document.OpenRead();
            var hash = await SHA256.HashDataAsync(stream, _token).ConfigureAwait(false);
            _token.ThrowIfCancellationRequested();
            return (document, "sha256:" + Convert.ToHexString(hash).ToLowerInvariant());
        }

        private async ValueTask<JsonElement> MetadataAsync(FederationReadRequest request)
        {
            var result = await ReadAsync(request).ConfigureAwait(false);
            FederationJson.RequireObject(result.Metadata);
            var text = result.Metadata.GetRawText();
            var count = Encoding.UTF8.GetByteCount(text);
            _budget.CheckObjectBytes(count);
            _budget.ChargeBytes(count);
            _budget.ChargeObject();
            RegistryJson parsed;
            try
            {
                parsed = RegistryJson.FromElement(result.Metadata, new RegistryJsonLimits
                {
                    MaxBytes = _budget.Limits.MaxObjectBytes,
                    MaxDepth = _budget.Limits.MaxJsonDepth,
                    MaxNodes = (int)Math.Min(int.MaxValue, _budget.Limits.MaxWork)
                });
            }
            catch (RegistryException exception)
            {
                throw FederationJson.FromCore(exception);
            }
            FederationJson.CountWork(parsed.RootElement, _budget, _token);
            return parsed.RootElement;
        }

        private async ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request)
        {
            _token.ThrowIfCancellationRequested();
            CheckContext();
            _budget.ChargeRequest();
            var result = await _source.ReadAsync(request, _token).ConfigureAwait(false);
            _token.ThrowIfCancellationRequested();
            CheckContext();
            if (result.Context != _context)
            {
                throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                    "A WoT read returned bytes or metadata from another source context.");
            }
            if (!FederationSyntax.SameXid(result.SelectedXid, request.Target,
                request.Operation == FederationOperation.Collection))
            {
                throw FederationJson.Invalid("A WoT read returned a different selected path.");
            }
            return result;
        }

        private void CheckContext()
        {
            if (_source.Context != _context || !ReferenceEquals(_source.Model, _model) ||
                FederationCapabilities.GetResolutionOwner(_source.Capabilities) != _owner)
            {
                throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                    "The WoT source context, captured model or resolution owner changed between reads.");
            }
        }

        private static JsonElement Entity(JsonElement envelope, string target)
        {
            var entity = envelope.TryGetProperty("entity", out var value) ? value : envelope;
            if (!FederationSyntax.SameXid(FederationJson.String(entity, "xid"), target))
            {
                throw FederationJson.Invalid("WoT entity metadata identifies another path.");
            }
            return entity;
        }

        private static JsonProperty[] Members(JsonElement envelope, string target)
        {
            if (FederationJson.String(envelope, "kind") != "collection" ||
                !FederationSyntax.SameXid(FederationJson.String(envelope, "xid"), target, true))
            {
                throw FederationJson.Invalid("WoT collection metadata identifies another collection.");
            }
            if (!envelope.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True)
            {
                throw new FederationException(FederationErrorCode.LimitExceeded,
                    "WoT lookup and Version selection require complete collections.");
            }
            var entities = FederationJson.Required(envelope, "entities");
            FederationJson.RequireObject(entities);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var members = entities.EnumerateObject().ToArray();
            foreach (var member in members)
            {
                if (!RegistryId.IsValid(member.Name) || !ids.Add(member.Name) ||
                    !FederationSyntax.SameXid(FederationJson.String(member.Value, "xid"),
                        target + "/" + Uri.EscapeDataString(member.Name)))
                {
                    throw FederationJson.Invalid("A WoT collection member has an invalid or competing identity.");
                }
            }
            return members;
        }
    }
}
