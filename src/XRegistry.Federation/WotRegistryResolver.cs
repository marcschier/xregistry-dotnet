// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

/// <summary>A selected JSON value and the acquired document whose bytes establish its provenance.</summary>
public sealed class WotResolvedFragment
{
    internal WotResolvedFragment(WotResolvedDocument document, JsonElement value)
    {
        ContainingDocument = document;
        Value = value;
    }

    /// <summary>Gets the complete selected Version; its digest covers the full document, not the fragment.</summary>
    public WotResolvedDocument ContainingDocument { get; }
    /// <summary>Gets an independently owned JSON value, including explicit null.</summary>
    public JsonElement Value { get; }
}

/// <summary>Resolves parsed profile-aware WoT references through a selected native source or retained revision.</summary>
public static class WotRegistryResolver
{
    /// <summary>Selects an object-valued affordance from an already acquired projection source.</summary>
    /// <remarks>
    /// The expected kind comes from the source manifest; the actual kind comes from the captured model.
    /// The pointer is a decoded JSON Pointer, not a URI fragment. Source association, Version requirements
    /// and nested-plan resolution must be established separately. This never acquires another Version.
    /// </remarks>
    public static async ValueTask<WotResolvedFragment> SelectProjectionAffordanceAsync(
        WotResolvedDocument document, WotReferenceTargetKind expectedKind, string targetPointer,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(targetPointer);
        if (!Enum.IsDefined(expectedKind)) { throw new ArgumentOutOfRangeException(nameof(expectedKind)); }
        cancellationToken.ThrowIfCancellationRequested();
        if (document.Kind != expectedKind)
        {
            throw FederationJson.Invalid("The captured projection source has a different WoT kind from its manifest.");
        }
        var readBudget = budget ?? new();
        var pointerBytes = Encoding.UTF8.GetByteCount(targetPointer);
        readBudget.CheckObjectBytes(pointerBytes);
        readBudget.ChargeBytes(pointerBytes);
        readBudget.ChargeObject();
        readBudget.CheckDepth(targetPointer.Count(character => character == '/'));
        var parts = targetPointer.Split('/');
        if (parts.Length != 3 || parts[0].Length != 0 ||
            parts[1] is not ("properties" or "actions" or "events"))
        {
            throw InvalidFragment("A projection target must identify one member of a root affordance map.");
        }
        var name = DecodePointerToken(parts[2], readBudget, cancellationToken);
        var root = await ParseAcquiredDocumentAsync(document, readBudget, cancellationToken).ConfigureAwait(false);
        if (!root.TryGetProperty(parts[1], out var map))
        {
            throw new RegistryException(new("not-found", "", "The projection source has no selected affordance map."));
        }
        FederationJson.RequireObject(map);
        if (!map.TryGetProperty(name, out var value))
        {
            throw new RegistryException(new("not-found", "", "The projection source has no selected affordance."));
        }
        FederationJson.RequireObject(value);
        readBudget.CheckResultBytes(Encoding.UTF8.GetByteCount(value.GetRawText()));
        cancellationToken.ThrowIfCancellationRequested();
        return new(document, value);
    }

    /// <summary>Applies an RFC 6901 URI-fragment pointer after Version or local-revision selection.</summary>
    /// <remarks>
    /// The fragment includes its leading '#'; '#' selects the whole document. Percent decoding is
    /// strict UTF-8 and precedes pointer-token decoding. This method never opens a source or changes
    /// the selected Version. Its returned value retains the full document's digest and origin.
    /// </remarks>
    public static async ValueTask<WotResolvedFragment> SelectFragmentAsync(WotResolvedDocument document,
        string fragment, FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(fragment);
        cancellationToken.ThrowIfCancellationRequested();
        budget ??= new();
        budget.CheckObjectBytes(fragment.Length);
        budget.ChargeBytes(fragment.Length);
        budget.ChargeObject();
        var pointer = DecodeFragment(fragment, budget, cancellationToken);
        var depth = pointer.Count(character => character == '/');
        budget.CheckDepth(depth);
        var tokens = pointer.Length == 0 ? [] : pointer[1..].Split('/');
        for (var index = 0; index < tokens.Length; index++)
        {
            tokens[index] = DecodePointerToken(tokens[index], budget, cancellationToken);
        }

        var value = await ParseAcquiredDocumentAsync(document, budget, cancellationToken).ConfigureAwait(false);
        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ChargeWork();
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(token, out var property))
            {
                value = property;
            }
            else if (value.ValueKind == JsonValueKind.Array && token.Length > 0 &&
                (token == "0" || token[0] is >= '1' and <= '9') &&
                token.All(char.IsAsciiDigit) &&
                int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                index < value.GetArrayLength())
            {
                value = value[index];
            }
            else
            {
                throw new RegistryException(new("not-found", "", "The fragment does not identify a stored JSON value."));
            }
        }
        budget.CheckResultBytes(Encoding.UTF8.GetByteCount(value.GetRawText()));
        cancellationToken.ThrowIfCancellationRequested();
        return new(document, value);
    }

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
        var revision = new WotVersionCandidate(captured.VersionId, captured.ContentDigest, captured.Origin)
        {
            Profile = captured.Profile, CompatibilityLine = captured.CompatibilityLine
        };
        if (request.VersionIdPin is not null || request.DigestPin is not null)
        {
            throw new RegistryException(new("invalid-pin", "",
                "A document-local reference cannot carry a Registry Version pin."));
        }

        budget ??= new();
        var root = await ParseAcquiredDocumentAsync(containingRevision, budget, cancellationToken).ConfigureAwait(false);

        if (captured.Profile == WotVersionProfile.SemVer && request.MinimumVersion is not null &&
            root.TryGetProperty("version", out var version))
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

    /// <summary>Resolves a local WoT reference and applies its fragment to the retained containing revision.</summary>
    /// <remarks>
    /// The caller supplies the effective base and containing-document URI after JSON-LD context processing.
    /// This method rejects external references and never opens another source or selects another Version.
    /// The local Version check and fragment selection share one finite work budget.
    /// </remarks>
    public static async ValueTask<WotResolvedFragment> ResolveLocalReferenceAsync(
        WotResolvedDocument containingRevision, string reference, Uri effectiveBaseUri,
        Uri containingDocumentUri, WotVersionResolutionRequest request,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(containingRevision);
        ArgumentNullException.ThrowIfNull(request);
        var target = WotReferenceUriResolver.Resolve(reference, effectiveBaseUri, containingDocumentUri);
        if (!target.IsDocumentLocal)
        {
            throw new ArgumentException("The WoT reference does not identify its containing document.", nameof(reference));
        }

        return await ResolveLocalTargetAsync(containingRevision, reference, target, request, budget,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<WotResolvedFragment> ResolveLocalTargetAsync(
        WotResolvedDocument containingRevision, string reference, WotReferenceUri target,
        WotVersionResolutionRequest request, FederationReadBudget? budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(containingRevision);
        ArgumentNullException.ThrowIfNull(request);
        if (!target.IsDocumentLocal)
        {
            throw new ArgumentException("The WoT reference does not identify its containing document.", nameof(reference));
        }

        var readBudget = budget ?? new();
        var localRequest = request with
        {
            TargetWotId = containingRevision.Version.WotId,
            IsDocumentLocal = true
        };
        var resolved = await ResolveLocalAsync(containingRevision, localRequest, readBudget, cancellationToken)
            .ConfigureAwait(false);
        return await SelectFragmentAsync(resolved, target.Fragment ?? "#", readBudget, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<JsonElement> ParseAcquiredDocumentAsync(WotResolvedDocument document,
        FederationReadBudget budget, CancellationToken cancellationToken)
    {
        budget.CheckObjectBytes(document.Document.Length);
        RegistryJson parsed;
        using (var stream = document.Document.OpenRead())
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
        if (root.TryGetProperty("id", out _) && FederationJson.String(root, "id") != document.Version.WotId)
        {
            throw FederationJson.Invalid("The document's id differs from its acquired WoT identity.");
        }
        return root;
    }

    internal static string DecodeFragment(string fragment, FederationReadBudget budget, CancellationToken token,
        bool requirePointer = true)
    {
        if (fragment.Length == 0 || fragment[0] != '#')
        {
            throw InvalidFragment("A URI fragment must begin with '#'.");
        }
        var bytes = new byte[fragment.Length - 1];
        var count = 0;
        for (var index = 1; index < fragment.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            budget.ChargeWork();
            var character = fragment[index];
            if (character == '%')
            {
                if (index + 2 >= fragment.Length || !char.IsAsciiHexDigit(fragment[index + 1]) ||
                    !char.IsAsciiHexDigit(fragment[index + 2]))
                {
                    throw InvalidFragment("The fragment contains an invalid percent escape.");
                }
                bytes[count++] = byte.Parse(fragment.AsSpan(index + 1, 2),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                index += 2;
            }
            else if (char.IsAsciiLetterOrDigit(character) || "-._~!$&'()*+,;=:@/?".Contains(character))
            {
                bytes[count++] = (byte)character;
            }
            else
            {
                throw InvalidFragment("The fragment contains a character that must be percent-encoded.");
            }
        }
        string pointer;
        try { pointer = new UTF8Encoding(false, true).GetString(bytes, 0, count); }
        catch (DecoderFallbackException exception)
        {
            throw new RegistryException(new("invalid-fragment", "", "The fragment is not valid UTF-8."), exception);
        }
        if (requirePointer && pointer.Length > 0 && pointer[0] != '/')
        {
            throw InvalidFragment("A nonempty JSON Pointer must begin with '/'.");
        }
        return pointer;
    }

    private static string DecodePointerToken(string value, FederationReadBudget budget, CancellationToken token)
    {
        var decoded = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            budget.ChargeWork();
            if (value[index] != '~') { decoded.Append(value[index]); }
            else
            {
                if (++index >= value.Length || value[index] is not ('0' or '1'))
                {
                    throw InvalidFragment("The JSON Pointer contains an invalid tilde escape.");
                }
                decoded.Append(value[index] == '0' ? '~' : '/');
            }
        }
        return decoded.ToString();
    }

    private static RegistryException InvalidFragment(string message) => new(new("invalid-fragment", "", message));

    /// <summary>Resolves a parsed, nonlocal reference within one explicitly selected Resource collection.</summary>
    /// <remarks>
    /// The caller owns source authorization and URI/base/context processing. The source must expose
    /// its captured WoT model and fixed profile. Lookup uses exact wotid equality, rejects competing Resources,
    /// follows at most one same-source, same-Resource-type xref, and never follows successors, document URLs
    /// or another source. Digest pins are compared with hashes of acquired bytes, not advisory metadata.
    /// All reads share the supplied finite budget.
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

    /// <summary>Resolves a WoT URI-reference with its captured JSON-LD context, selects its Version, and applies its fragment.</summary>
    /// <remarks>
    /// The caller supplies an authorized Group Resource collection, the exact source pointer and only remote
    /// contexts already captured for this operation. This method does not retrieve live contexts. A local
    /// reference uses the retained containing revision; an external reference looks up its resolved
    /// fragment-free URI as a <c>wotid</c>. Source authorization and URI-to-alias mapping remain the caller's responsibility.
    /// </remarks>
    public static async ValueTask<WotResolvedFragment> ResolveReferenceAsync(
        IFederationReadSource source, string resourceCollection, WotResolvedDocument containingRevision,
        string sourcePointer, string referencePropertyName, string reference, Uri containingDocumentUri,
        IReadOnlyDictionary<Uri, FederationDocument> capturedContexts, WotVersionResolutionRequest request,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(containingRevision);
        ArgumentNullException.ThrowIfNull(capturedContexts);
        var readBudget = budget ?? new();
        var target = await WotJsonLdBaseResolver.ResolveReferenceUriAsync(containingRevision.Document,
            sourcePointer, referencePropertyName, reference, containingDocumentUri, capturedContexts,
            readBudget, cancellationToken).ConfigureAwait(false);
        return await ResolveReferenceTargetAsync(source, resourceCollection, containingRevision, reference,
            target, request, readBudget, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves an external URI through captured Resource identities from authorized Registry views.</summary>
    /// <remarks>
    /// A Resource-level identity uses the selected view's Version rules. An exact-Version identity pins that
    /// Version. Local references still use the retained containing revision and do not consult the identity index.
    /// </remarks>
    public static async ValueTask<WotResolvedFragment> ResolveCapturedReferenceAsync(
        WotResolvedDocument containingRevision, string reference, Uri effectiveBaseUri,
        Uri containingDocumentUri, WotCapturedUriIdentityIndex capturedIdentities,
        WotVersionResolutionRequest request, FederationReadBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(containingRevision);
        ArgumentNullException.ThrowIfNull(capturedIdentities);
        ArgumentNullException.ThrowIfNull(request);
        var target = WotReferenceUriResolver.Resolve(reference, effectiveBaseUri, containingDocumentUri);
        return await ResolveCapturedReferenceTargetAsync(containingRevision, reference, target,
            capturedIdentities, request, budget ?? new(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves a JSON-LD URI-reference through captured Resource identities and contexts.</summary>
    public static async ValueTask<WotResolvedFragment> ResolveCapturedReferenceAsync(
        WotResolvedDocument containingRevision, string sourcePointer, string referencePropertyName,
        string reference, Uri containingDocumentUri,
        IReadOnlyDictionary<Uri, FederationDocument> capturedContexts,
        WotCapturedUriIdentityIndex capturedIdentities, WotVersionResolutionRequest request,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(containingRevision);
        ArgumentNullException.ThrowIfNull(capturedContexts);
        ArgumentNullException.ThrowIfNull(capturedIdentities);
        ArgumentNullException.ThrowIfNull(request);
        var readBudget = budget ?? new();
        var target = await WotJsonLdBaseResolver.ResolveReferenceUriAsync(containingRevision.Document,
            sourcePointer, referencePropertyName, reference, containingDocumentUri, capturedContexts,
            readBudget, cancellationToken).ConfigureAwait(false);
        return await ResolveCapturedReferenceTargetAsync(containingRevision, reference, target,
            capturedIdentities, request, readBudget, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves a WoT URI-reference, selects its Version, and applies its fragment.</summary>
    /// <remarks>
    /// The caller supplies an authorized Group Resource collection and the effective base and containing
    /// document URIs established by JSON-LD context processing. A local reference uses the retained
    /// containing revision; an external reference looks up its resolved fragment-free URI as a <c>wotid</c>.
    /// Source authorization and URI-to-alias mapping remain the caller's responsibility.
    /// </remarks>
    public static async ValueTask<WotResolvedFragment> ResolveReferenceAsync(
        IFederationReadSource source, string resourceCollection, WotResolvedDocument containingRevision,
        string reference, Uri effectiveBaseUri, Uri containingDocumentUri,
        WotVersionResolutionRequest request, FederationReadBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(containingRevision);
        ArgumentNullException.ThrowIfNull(request);
        var target = WotReferenceUriResolver.Resolve(reference, effectiveBaseUri, containingDocumentUri);
        return await ResolveReferenceTargetAsync(source, resourceCollection, containingRevision, reference,
            target, request, budget, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<WotResolvedFragment> ResolveReferenceTargetAsync(
        IFederationReadSource source, string resourceCollection, WotResolvedDocument containingRevision,
        string reference, WotReferenceUri target, WotVersionResolutionRequest request,
        FederationReadBudget? budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(containingRevision);
        ArgumentNullException.ThrowIfNull(request);
        var readBudget = budget ?? new();
        if (target.IsDocumentLocal)
        {
            return await ResolveLocalTargetAsync(containingRevision, reference, target, request, readBudget,
                cancellationToken).ConfigureAwait(false);
        }

        var resolvedRequest = request with
        {
            TargetWotId = target.DocumentUri.AbsoluteUri,
            IsDocumentLocal = false,
            ContainingRevision = null
        };
        var resolved = await ResolveAsync(source, resourceCollection, resolvedRequest,
            readBudget, cancellationToken).ConfigureAwait(false);
        return await SelectFragmentAsync(resolved, target.Fragment ?? "#", readBudget, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<WotResolvedFragment> ResolveCapturedReferenceTargetAsync(
        WotResolvedDocument containingRevision, string reference, WotReferenceUri target,
        WotCapturedUriIdentityIndex capturedIdentities, WotVersionResolutionRequest request,
        FederationReadBudget budget, CancellationToken cancellationToken)
    {
        if (target.IsDocumentLocal)
        {
            return await ResolveLocalTargetAsync(containingRevision, reference, target, request, budget,
                cancellationToken).ConfigureAwait(false);
        }

        var identity = capturedIdentities.Resolve(target.DocumentUri, budget, cancellationToken);
        var expectedKind = request.TargetKind switch
        {
            WotReferenceTargetKind.ThingModel => "thingmodel",
            WotReferenceTargetKind.ThingDescription => "thingdescription",
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
        if (!StringComparer.Ordinal.Equals(identity.ResourceKind, expectedKind))
        {
            throw FederationJson.Invalid("The captured URI identity names a different Resource kind.");
        }
        var wotId = identity.WotId ??
            throw FederationJson.Invalid("The captured WoT Resource identity has no applicable wotid.");
        if (identity.VersionId is { } exactVersion &&
            (request.VersionIdPin is { } requestedVersion && requestedVersion != exactVersion ||
             request.DigestPin is not null))
        {
            throw new RegistryException(new("invalid-pin", "",
                "The captured exact-Version URI conflicts with the request's Version or digest pin."));
        }

        var resolvedRequest = request with
        {
            TargetWotId = wotId,
            VersionIdPin = identity.VersionId ?? request.VersionIdPin,
            IsDocumentLocal = false,
            ContainingRevision = null
        };
        var collection = new FederationReadRequest(FederationOperation.Collection, identity.ResourceCollection);
        var resolved = await new Resolution(identity.Source, collection, resolvedRequest, budget,
            cancellationToken, resourceXidPin: identity.ResourceXid,
            exactVersionId: identity.VersionId).RunAsync().ConfigureAwait(false);
        capturedIdentities.CheckSource(identity.Source, cancellationToken);
        return await SelectFragmentAsync(resolved, target.Fragment ?? "#", budget, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Acquires the TD source selected by decoded CloudEvents WoT query parameters.</summary>
    /// <remarks>
    /// The caller validates the complete source URI-reference and authorizes one Group Resource
    /// collection. An explicit wotversion selects exactly that Version; otherwise selection uses
    /// the captured Core default, including a sticky or prerelease default. Missing selections
    /// report not-found, without ordinary WoT floor selection or fallback. The result contains
    /// authored source bytes, not a composed resolved TD view suitable for CloudEvents subject.
    /// </remarks>
    public static ValueTask<WotResolvedDocument> AcquireCloudEventSourceAsync(IFederationReadSource source,
        string resourceCollection, WotCloudEventSourceQuery query,
        FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        var collection = new FederationReadRequest(FederationOperation.Collection, resourceCollection);
        if (collection.Parts.Length != 3)
        {
            throw new ArgumentException("An explicit Group Resource collection is required.", nameof(resourceCollection));
        }
        var request = new WotVersionResolutionRequest(query.WotId, WotReferenceTargetKind.ThingDescription,
            WotReferenceOperator.ThingDescription, VersionIdPin: query.VersionId);
        return new Resolution(source, collection, request, budget ?? new(), cancellationToken,
            useCoreDefaultSelection: true).RunAsync();
    }

    /// <summary>Selects a CloudEvents subject from the resolved TD stored at the selected publication Version.</summary>
    /// <remarks>
    /// The selected Version must contain the published resolved TD view under the CloudEvents WoT profile.
    /// This method does not compose authored sources or resolve live dependencies. The source and pointer
    /// selection share one finite read budget.
    /// </remarks>
    public static async ValueTask<WotResolvedFragment> AcquireCloudEventSubjectAsync(
        IFederationReadSource source, string resourceCollection, WotCloudEventSourceQuery query,
        string subject, FederationReadBudget? budget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(subject);
        var readBudget = budget ?? new();
        var document = await AcquireCloudEventSourceAsync(source, resourceCollection, query,
            readBudget, cancellationToken).ConfigureAwait(false);
        return await SelectFragmentAsync(document, subject, readBudget, cancellationToken).ConfigureAwait(false);
    }

    private sealed class Resolution
    {
        private readonly FederationReadRequest _collection;
        private readonly WotVersionResolutionRequest _request;
        private readonly FederationReadBudget _budget;
        private readonly CancellationToken _token;
        private readonly NativeRegistryContext _context;
        private readonly RegistryModel _model;
        private readonly WotVersionProfile _profile;
        private readonly CapturedFederationReadScope _reads;
        private readonly bool _useCoreDefaultSelection;
        private readonly string? _resourceXidPin;
        private readonly string? _exactVersionId;

        internal Resolution(IFederationReadSource source, FederationReadRequest collection,
            WotVersionResolutionRequest request, FederationReadBudget budget, CancellationToken token,
            bool useCoreDefaultSelection = false, string? resourceXidPin = null, string? exactVersionId = null)
        {
            token.ThrowIfCancellationRequested();
            _collection = collection;
            _request = request;
            _budget = budget;
            _token = token;
            _useCoreDefaultSelection = useCoreDefaultSelection;
            _resourceXidPin = resourceXidPin;
            _exactVersionId = exactVersionId;
            _reads = new(source, budget, token);
            _context = _reads.Context;
            _model = _reads.Model;
            if (resourceXidPin is not null)
            {
                var parts = FederationSyntax.Xid(resourceXidPin);
                if (parts.Length != 4 ||
                    !collection.Parts.SequenceEqual(parts.Take(3), StringComparer.Ordinal))
                {
                    throw new ArgumentException("The selected Resource must belong to the requested Resource collection.",
                        nameof(resourceXidPin));
                }
            }
            try { _profile = WotRegistryProfiles.Read(_model); }
            catch (RegistryException exception)
            {
                throw new FederationException(FederationErrorCode.UnsupportedVersion,
                    "The source has no supported fixed WoT profile.", exception.Diagnostic.Code, exception);
            }
            var singular = request.TargetKind switch
            {
                WotReferenceTargetKind.ThingModel => "thingmodel",
                WotReferenceTargetKind.ThingDescription => "thingdescription",
                _ => throw new ArgumentOutOfRangeException(nameof(request))
            };
            if (!_model.Groups.TryGetValue(collection.Parts[0], out var group) ||
                !group.Resources.TryGetValue(collection.Parts[2], out var resource) ||
                resource.Singular != singular || !resource.HasDocument ||
                resource.VersionMode != (_profile == WotVersionProfile.Incremental ? "manual" : "semver") ||
                _profile == WotVersionProfile.Incremental && resource.SingleVersionRoot)
            {
                throw new FederationException(FederationErrorCode.UnsupportedOperation,
                    "The selected collection is not a captured WoT Resource type with matching profile controls.");
            }
        }

        internal async ValueTask<WotResolvedDocument> RunAsync()
        {
            var resources = Members(await MetadataAsync(_collection).ConfigureAwait(false), _collection.Target);
            string? resourceXid = null;
            string? selectedResourceXid = null;
            string? aliasTargetXid = null;
            JsonElement aliasMetaSnapshot = default;
            JsonElement aliasTargetMetaSnapshot = default;
            JsonElement meta = default;
            List<WotVersionCandidate>? matchedCandidates = null;
            foreach (var member in resources)
            {
                _token.ThrowIfCancellationRequested();
                var xid = _collection.Target + "/" + Uri.EscapeDataString(member.Name);
                if (_resourceXidPin is not null && !FederationSyntax.SameXid(xid, _resourceXidPin))
                {
                    continue;
                }
                var metaPath = xid + "/meta";
                var resourceMeta = Entity(await MetadataAsync(new(FederationOperation.Entity, metaPath))
                    .ConfigureAwait(false), metaPath);
                var selectionMeta = resourceMeta;
                var selectionXid = xid;
                if (resourceMeta.TryGetProperty("xref", out _))
                {
                    if (_useCoreDefaultSelection)
                    {
                        throw new FederationException(FederationErrorCode.UnsupportedOperation,
                            "A CloudEvents source cannot be selected through a WoT Resource alias.");
                    }
                    var target = FederationJson.String(resourceMeta, "xref");
                    var targetParts = FederationSyntax.Xid(target);
                    var resourceParts = FederationSyntax.Xid(xid);
                    if (targetParts.Length != 4 ||
                        !ReferenceEquals(DocumentTreeFormat.Resource(_model, resourceParts),
                            DocumentTreeFormat.Resource(_model, targetParts)))
                    {
                        throw FederationJson.Invalid("A WoT Resource alias must target the same Resource model type.");
                    }

                    var targetMetaPath = target + "/meta";
                    selectionMeta = Entity(await MetadataAsync(new(FederationOperation.Entity, targetMetaPath))
                        .ConfigureAwait(false), targetMetaPath);
                    if (selectionMeta.TryGetProperty("xref", out _))
                    {
                        throw new FederationException(FederationErrorCode.UnsupportedOperation,
                            "A WoT Resource alias target cannot itself be an alias.");
                    }
                    selectionXid = target;
                }
                List<WotVersionCandidate>? resourceCandidates = null;
                string? wotId;
                if (_exactVersionId is not null)
                {
                    (wotId, resourceCandidates) = await ReadVersionsAsync(selectionXid,
                        _request.TargetWotId).ConfigureAwait(false);
                }
                else if (_useCoreDefaultSelection)
                {
                    (wotId, resourceCandidates) = await ReadVersionsAsync(selectionXid).ConfigureAwait(false);
                }
                else
                {
                    var defaultVersionId = FederationJson.String(selectionMeta, "defaultversionid");
                    var defaultPath = selectionXid + "/versions/" + Uri.EscapeDataString(defaultVersionId);
                    var defaultVersion = Entity(await MetadataAsync(new(FederationOperation.Entity, defaultPath))
                        .ConfigureAwait(false), defaultPath);
                    if (FederationJson.String(defaultVersion, "versionid") != defaultVersionId)
                    {
                        throw FederationJson.Invalid("A WoT default Version has a different Version identity.");
                    }
                    wotId = FederationJson.String(defaultVersion, "wotid");
                }
                if (wotId != _request.TargetWotId) { continue; }
                if (resourceXid is not null)
                {
                    throw new FederationException(FederationErrorCode.Ambiguous,
                        "Several Resources in the selected Group have the requested WoT identity.");
                }
                resourceXid = xid;
                selectedResourceXid = selectionXid;
                meta = selectionMeta;
                matchedCandidates = resourceCandidates;
                if (selectionXid != xid)
                {
                    aliasTargetXid = selectionXid;
                    aliasMetaSnapshot = resourceMeta;
                    aliasTargetMetaSnapshot = selectionMeta;
                }
            }
            if (resourceXid is null)
            {
                throw new RegistryException(new("not-found", "", "No WoT Resource matches in the selected Group."));
            }
            var resolvedResourceXid = selectedResourceXid ??
                throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                    "The matched WoT Resource has no selected source identity.");

            if (aliasTargetXid is not null)
            {
                await ValidateAliasSnapshotAsync(resourceXid, aliasTargetXid, aliasMetaSnapshot,
                    aliasTargetMetaSnapshot).ConfigureAwait(false);
            }

            var sticky = false;
            if (meta.TryGetProperty("defaultversionsticky", out var stickyValue))
            {
                if (stickyValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw FederationJson.Invalid("A WoT sticky-default flag must be a boolean.");
                }
                sticky = stickyValue.GetBoolean();
            }

            var versionsPath = resolvedResourceXid + "/versions";
            var candidates = matchedCandidates ??
                (await ReadVersionsAsync(resolvedResourceXid, _request.TargetWotId).ConfigureAwait(false)).Candidates;
            if (_exactVersionId is { } exactVersionId)
            {
                var exactCandidate = candidates.Find(candidate => candidate.VersionId == exactVersionId);
                if (exactCandidate is null)
                {
                    throw new RegistryException(new("not-found", "",
                        "The captured URI identifies a Version that is not present in the selected Resource."));
                }
                var acquired = await AcquireAsync(versionsPath, exactVersionId).ConfigureAwait(false);
                return Complete(resourceXid, exactCandidate, acquired.Document, acquired.Digest);
            }
            if (_useCoreDefaultSelection)
            {
                var selectedId = _request.VersionIdPin ??
                    (meta.TryGetProperty("defaultversionid", out _) ? FederationJson.String(meta, "defaultversionid") : null);
                var selectedCandidate = candidates.Find(candidate => candidate.VersionId == selectedId);
                if (selectedCandidate is null)
                {
                    throw new RegistryException(new("not-found", "",
                        "The CloudEvents TD Version or Core default is not available in the selected Resource."));
                }
                var acquired = await AcquireAsync(versionsPath, selectedCandidate.VersionId).ConfigureAwait(false);
                return Complete(resourceXid, selectedCandidate, acquired.Document, acquired.Digest);
            }

            var defaultId = FederationJson.String(meta, "defaultversionid");
            var captured = new WotVersionResource(_request.TargetWotId, _request.TargetKind,
                defaultId, sticky, candidates) { Profile = _profile };
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
            return Complete(resourceXid, candidates.First(candidate => candidate.VersionId == selected), document, digest);
        }

        private async ValueTask ValidateAliasSnapshotAsync(string aliasXid, string targetXid,
            JsonElement aliasMeta, JsonElement targetMeta)
        {
            var aliasPath = aliasXid + "/meta";
            var aliasAfter = Entity(await MetadataAsync(new(FederationOperation.Entity, aliasPath))
                .ConfigureAwait(false), aliasPath);
            var targetPath = targetXid + "/meta";
            var targetAfter = Entity(await MetadataAsync(new(FederationOperation.Entity, targetPath))
                .ConfigureAwait(false), targetPath);
            if (!SameMetadata(aliasMeta, aliasAfter) || !SameMetadata(targetMeta, targetAfter))
            {
                throw new FederationException(FederationErrorCode.InconsistentSnapshot,
                    "The WoT Resource alias or its target changed during Version selection.");
            }
        }

        private static bool SameMetadata(JsonElement left, JsonElement right) =>
            JsonNode.DeepEquals(JsonNode.Parse(left.GetRawText()), JsonNode.Parse(right.GetRawText()));

        private WotResolvedDocument Complete(string resourceXid, WotVersionCandidate selected,
            FederationDocument document, string digest)
        {
            _budget.CheckResultBytes(document.Length);
            CheckContext();
            _token.ThrowIfCancellationRequested();
            return new(resourceXid, _request.TargetKind,
                new(_request.TargetWotId, selected.VersionId, digest, _context.Source)
                {
                    Profile = _profile,
                    CompatibilityLine = selected.CompatibilityLine
                }, _context, document);
        }

        private async ValueTask<(string? WotId, List<WotVersionCandidate> Candidates)> ReadVersionsAsync(
            string resourceXid, string? expectedIdentity = null)
        {
            var versionsPath = resourceXid + "/versions";
            var versions = Members(await MetadataAsync(new(FederationOperation.Collection, versionsPath))
                .ConfigureAwait(false), versionsPath);
            var candidates = new List<WotVersionCandidate>();
            var identity = expectedIdentity;
            foreach (var member in versions)
            {
                var id = FederationJson.String(member.Value, "versionid");
                var wotId = FederationJson.String(member.Value, "wotid");
                if (id != member.Name || identity is not null && wotId != identity)
                {
                    throw FederationJson.Invalid("A WoT Version has a different identity from its selected Resource.");
                }
                identity = wotId;
                candidates.Add(new(id, "", _context.Source)
                {
                    Profile = _profile,
                    CompatibilityLine = member.Value.TryGetProperty("compatibilityline", out _)
                        ? FederationJson.String(member.Value, "compatibilityline") : null
                });
            }
            return (identity, candidates);
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

        private ValueTask<JsonElement> MetadataAsync(FederationReadRequest request) => _reads.MetadataAsync(request);

        private ValueTask<FederationReadResult> ReadAsync(FederationReadRequest request) => _reads.ReadAsync(request);

        private void CheckContext() => _reads.Check();

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
