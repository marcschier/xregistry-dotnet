// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using XRegistry.Models;

namespace XRegistry.WotResolution.Testing;

internal sealed record CanonicalWotSelection(
    WotVersionResource? Resource,
    WotVersionResolutionRequest Request,
    string? ExpectedError,
    JsonElement ExpectedResult);

internal sealed record CanonicalWotAssignment(
    string Id,
    string WotId,
    string ResourceKind,
    JsonElement Registry,
    JsonElement Publish,
    JsonElement Expected);

internal static class WotResolutionVectors
{
    internal const string FixtureRelativePath = "Conformance/Vectors/WotResolution/vectors.json";
    internal const string FixtureSha256 = "115f7fa97b2aa33fbae2441ff4c672153512a18cf99c695460d238437d6c8f0c";
    internal const int FixtureByteLength = 36031;

    private static readonly string[] SelectionIds =
    [
        "001-floor-met",
        "002-floor-unmet",
        "004-digest-pin",
        "005-digest-pin-mismatch",
        "008-sticky-above-floor",
        "009-prerelease-excluded",
        "013-implicit-type-floor",
        "015-multiple-types-partly-specified",
        "017-deprecated-successor",
        "020-generated-floor-rejected",
        "024-not-found",
        "026-pin-overrides-floor-and-sticky",
        "027-generated-opt-in",
        "043-single-type-greater-floor-sticky",
        "046-multiple-types-greater-model-floor",
        "049-pin-bypasses-type-floor-derivation",
        "050-td-caret-within-major",
        "052-td-unfloored-crosses-majors",
        "055-td-prerelease-floor",
        "057-td-subthing-floor",
        "063-tm-minversion-on-td-reference-invalid-floor",
        "064-local-tm-ref-uses-containing-revision",
        "065-local-tm-ref-below-floor",
        "066-local-td-ref-next-major",
        "067-digest-pin-duplicate-highest-version",
        "068-local-tm-ref-missing-authored-version",
        "069-local-tm-ref-invalid-authored-version"
    ];

    private static readonly string[] AssignmentIds =
    [
        "012-late-patch-default",
        "019-generated-skip-occupied",
        "036-publish-authored-assignment",
        "058-td-publish-authored-assignment",
        "060-td-publish-version-mismatch",
        "061-td-publish-build-metadata"
    ];

    private static readonly Dictionary<string, JsonElement> Vectors = LoadAndValidate();

    internal static CanonicalWotSelection Selection(string id)
    {
        if (!SelectionIds.Contains(id, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"'{id}' is not one of the canonical selection vector IDs.");
        }

        var vector = Vectors[id];
        var reference = vector.GetProperty("reference");
        var targetWotId = RequiredString(reference, "target");
        var (targetKind, operation) = RequiredString(reference, "kind") switch
        {
            "extends" or "ref" => (WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModel),
            "type" => (WotReferenceTargetKind.ThingModel, WotReferenceOperator.ThingModelTypeLink),
            "td-extends" or "td-ref" or "td-subthing" =>
                (WotReferenceTargetKind.ThingDescription, WotReferenceOperator.ThingDescription),
            var value => throw new InvalidDataException($"Unsupported canonical reference kind '{value}' in '{id}'.")
        };

        var registry = vector.GetProperty("registry").EnumerateArray()
            .Where(item => StringComparer.Ordinal.Equals(RequiredString(item, "wotid"), targetWotId))
            .ToArray();
        if (registry.Length > 1)
        {
            throw new InvalidDataException($"Vector '{id}' has duplicate Registry Resources for '{targetWotId}'.");
        }

        var resource = registry.Length == 0 ? null : ToResource(registry[0], targetWotId, targetKind);
        var minimumVersion = OptionalString(reference, "minVersion");
        var typeLinkCount = 1;
        var allTypeLinksHaveMinimumVersion = true;
        string? referencingTdModelVersion = null;
        if (operation == WotReferenceOperator.ThingModelTypeLink)
        {
            if (!vector.TryGetProperty("document", out var document) ||
                !document.TryGetProperty("typeLinks", out var links) ||
                links.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"Type-link vector '{id}' has no canonical typeLinks array.");
            }

            var linkArray = links.EnumerateArray().ToArray();
            typeLinkCount = linkArray.Length;
            allTypeLinksHaveMinimumVersion = linkArray.All(static link =>
                link.TryGetProperty("minVersion", out _));
            referencingTdModelVersion = OptionalString(document, "version.model");
            var selectedLinks = linkArray.Where(link =>
                StringComparer.Ordinal.Equals(RequiredString(link, "target"), targetWotId)).ToArray();
            if (selectedLinks.Length != 1)
            {
                throw new InvalidDataException($"Vector '{id}' must identify exactly one type link for '{targetWotId}'.");
            }

            var selectedLinkFloor = OptionalString(selectedLinks[0], "minVersion");
            if (minimumVersion is not null && selectedLinkFloor is not null &&
                !StringComparer.Ordinal.Equals(minimumVersion, selectedLinkFloor))
            {
                throw new InvalidDataException($"Vector '{id}' has conflicting reference and type-link floors.");
            }

            minimumVersion ??= selectedLinkFloor;
        }

        WotMinimumVersionKind? minimumVersionKind = minimumVersion is null
            ? null
            : OptionalString(reference, "floorTerm") switch
            {
                null => targetKind == WotReferenceTargetKind.ThingModel
                    ? WotMinimumVersionKind.ThingModel : WotMinimumVersionKind.ThingDescription,
                "tm:minVersion" => WotMinimumVersionKind.ThingModel,
                "td:minVersion" => WotMinimumVersionKind.ThingDescription,
                var term => throw new InvalidDataException($"Unsupported canonical floor term '{term}' in '{id}'.")
            };

        var pin = vector.TryGetProperty("pin", out var pinElement) ? pinElement : default;
        var versionIdPin = pin.ValueKind == JsonValueKind.Object ? OptionalString(pin, "versionid") : null;
        var digestPin = pin.ValueKind == JsonValueKind.Object ? OptionalString(pin, "digest") : null;
        var isLocal = reference.TryGetProperty("local", out var local) && local.GetBoolean();
        WotVersionCandidate? containingRevision = null;
        if (isLocal)
        {
            if (!vector.TryGetProperty("document", out var document) ||
                resource is null)
            {
                throw new InvalidDataException($"Local-reference vector '{id}' lacks its containing document or Registry Resource.");
            }

            var containingVersionId = RequiredString(document, "versionid");
            var captured = resource.Versions.SingleOrDefault(candidate =>
                StringComparer.Ordinal.Equals(candidate.VersionId, containingVersionId))
                ?? throw new InvalidDataException(
                    $"Local-reference vector '{id}' has no captured version '{containingVersionId}' for its containing revision.");
            var authoredVersion = OptionalString(document,
                targetKind == WotReferenceTargetKind.ThingModel ? "version.model" : "version.instance");
            containingRevision = new(captured.VersionId, captured.ContentDigest, captured.Origin, authoredVersion);
        }

        var request = new WotVersionResolutionRequest(
            targetWotId,
            targetKind,
            operation,
            minimumVersion,
            minimumVersionKind,
            referencingTdModelVersion,
            typeLinkCount,
            allTypeLinksHaveMinimumVersion,
            versionIdPin,
            digestPin,
            vector.TryGetProperty("allowPrerelease", out var allowPrerelease) && allowPrerelease.GetBoolean(),
            isLocal,
            containingRevision);

        var expectation = vector.GetProperty("expect");
        var expectedError = OptionalString(expectation, "error");
        return new(resource, request, expectedError,
            expectation.TryGetProperty("result", out var result) ? result : default);
    }

    internal static CanonicalWotAssignment Assignment(string id)
    {
        if (!AssignmentIds.Contains(id, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"'{id}' is not one of the canonical assignment vector IDs.");
        }

        var vector = Vectors[id];
        var publish = vector.GetProperty("publish");
        var targetWotId = RequiredString(publish, "target");
        var registryEntries = vector.GetProperty("registry").EnumerateArray()
            .Where(item => StringComparer.Ordinal.Equals(RequiredString(item, "wotid"), targetWotId))
            .ToArray();
        if (registryEntries.Length != 1)
        {
            throw new InvalidDataException($"Assignment vector '{id}' must seed exactly one matching Registry Resource.");
        }

        var resourceKind = OptionalString(publish, "targetKind") switch
        {
            null => "thingmodel",
            "td" => "thingdescription",
            var value => throw new InvalidDataException($"Unsupported publish targetKind '{value}' in '{id}'.")
        };

        return new(id, targetWotId, resourceKind, registryEntries[0], publish, vector.GetProperty("expect"));
    }

    internal static string Document(string wotId, JsonElement fields)
    {
        var document = new JsonObject { ["id"] = wotId };
        var version = new JsonObject();
        if (fields.ValueKind == JsonValueKind.Object)
        {
            CopyVersionTerm(fields, version, "version.model");
            CopyVersionTerm(fields, version, "version.instance");
        }
        if (version.Count > 0)
        {
            document["version"] = version;
        }

        return document.ToJsonString();
    }

    internal static string AssignmentDocument(CanonicalWotAssignment assignment) =>
        Document(assignment.WotId, assignment.Publish);

    private static string? OptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"'{propertyName}' must be a string when present.");
        }

        return property.GetString();
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        OptionalString(element, propertyName)
        ?? throw new InvalidDataException($"Required string property '{propertyName}' is missing.");

    private static WotVersionResource ToResource(JsonElement resource, string targetWotId,
        WotReferenceTargetKind targetKind)
    {
        var versions = resource.GetProperty("versions").EnumerateArray().Select(version =>
            new WotVersionCandidate(
                RequiredString(version, "versionid"),
                RequiredString(version, "contentdigest"),
                RequiredString(version, "versionorigin"),
                OptionalString(version, targetKind == WotReferenceTargetKind.ThingModel
                    ? "version.model" : "version.instance"))).ToArray();

        return new(targetWotId, targetKind, OptionalString(resource, "defaultversionid"),
            resource.GetProperty("defaultversionsticky").GetBoolean(), versions);
    }

    private static void CopyVersionTerm(JsonElement source, JsonObject destination, string term)
    {
        var value = OptionalString(source, term);
        if (value is not null)
        {
            destination[term["version.".Length..]] = value;
        }
    }

    private static Dictionary<string, JsonElement> LoadAndValidate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, FixtureRelativePath);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != FixtureByteLength)
        {
            throw new InvalidDataException(
                $"Canonical WoT vectors are {bytes.Length} bytes; expected {FixtureByteLength} bytes.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!StringComparer.Ordinal.Equals(hash, FixtureSha256))
        {
            throw new InvalidDataException($"Canonical WoT vector SHA-256 mismatch: {hash}.");
        }

        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        var root = document.RootElement;
        var expectedIds = SelectionIds.Concat(AssignmentIds).ToHashSet(StringComparer.Ordinal);
        var vectors = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!expectedIds.Contains(property.Name) || !vectors.TryAdd(property.Name, property.Value.Clone()))
            {
                throw new InvalidDataException($"Unexpected or duplicate canonical vector ID '{property.Name}'.");
            }

            ValidateVector(property.Name, property.Value,
                SelectionIds.Contains(property.Name, StringComparer.Ordinal));
        }

        if (vectors.Count != expectedIds.Count || expectedIds.Any(id => !vectors.ContainsKey(id)))
        {
            throw new InvalidDataException(
                $"Canonical WoT fixture must contain exactly {expectedIds.Count} expected vector IDs.");
        }

        return vectors;
    }

    private static void ValidateVector(string id, JsonElement vector, bool selection)
    {
        ValidateObject(vector, $"vector '{id}",
            ["description", "registry", "reference", "document", "pin", "publish", "allowPrerelease", "expect"],
            "description", "registry", "expect");
        if (vector.GetProperty("description").ValueKind != JsonValueKind.String ||
            vector.GetProperty("registry").ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Vector '{id}' has an invalid description or registry array.");
        }

        var expectation = vector.GetProperty("expect");
        ValidateObject(expectation, $"expectation for '{id}", ["result", "error", "assigned"]);
        if (selection)
        {
            if (!vector.TryGetProperty("reference", out var reference) ||
                vector.TryGetProperty("publish", out _) ||
                !ExactlyOneOf(expectation, "result", "error") ||
                expectation.EnumerateObject().Count() != 1)
            {
                throw new InvalidDataException($"Selection vector '{id}' has an invalid route or expectation.");
            }

            ValidateReference(id, reference);
            if (expectation.TryGetProperty("result", out var result))
            {
                ValidateObject(result, $"result for '{id}", ["wotid", "versionid", "contentdigest"],
                    "wotid", "versionid", "contentdigest");
                _ = RequiredString(result, "wotid");
                _ = RequiredString(result, "versionid");
                _ = RequiredString(result, "contentdigest");
            }
            else
            {
                _ = RequiredString(expectation, "error");
            }

            if (vector.TryGetProperty("pin", out var pin))
            {
                ValidateObject(pin, $"pin for '{id}", ["versionid", "digest"]);
                if (ExactlyOneOf(pin, "versionid", "digest") is false)
                {
                    throw new InvalidDataException($"Vector '{id}' must contain exactly one canonical pin value.");
                }

                _ = RequiredString(pin, pin.TryGetProperty("versionid", out _) ? "versionid" : "digest");
            }

            if (vector.TryGetProperty("allowPrerelease", out var prerelease) &&
                prerelease.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidDataException($"Vector '{id}' has a non-boolean allowPrerelease option.");
            }

            if (vector.TryGetProperty("document", out var selectionDocument))
            {
                ValidateDocument(id, selectionDocument);
            }
        }
        else
        {
            if (!vector.TryGetProperty("publish", out var publish) ||
                vector.TryGetProperty("reference", out _) ||
                !ExactlyOneOf(expectation, "assigned", "error") ||
                expectation.EnumerateObject().Count() != 1)
            {
                throw new InvalidDataException($"Assignment vector '{id}' has an invalid route or expectation.");
            }

            ValidateObject(publish, $"publish for '{id}",
                ["target", "targetKind", "versionid", "version.model", "version.instance"], "target");
            _ = RequiredString(publish, "target");
            _ = OptionalString(publish, "versionid");
            _ = OptionalString(publish, "version.model");
            _ = OptionalString(publish, "version.instance");
            if (publish.TryGetProperty("targetKind", out var targetKind) &&
                (targetKind.ValueKind != JsonValueKind.String || targetKind.GetString() != "td"))
            {
                throw new InvalidDataException($"Assignment vector '{id}' has an unsupported targetKind.");
            }

            if (publish.TryGetProperty("version.model", out _) && publish.TryGetProperty("version.instance", out _))
            {
                throw new InvalidDataException($"Assignment vector '{id}' contains both WoT version terms.");
            }

            if (expectation.TryGetProperty("assigned", out var assigned))
            {
                ValidateObject(assigned, $"assignment result for '{id}",
                    ["versionid", "versionorigin", "defaultversionid"],
                    "versionid", "versionorigin", "defaultversionid");
                _ = RequiredString(assigned, "versionid");
                _ = RequiredString(assigned, "versionorigin");
                _ = RequiredString(assigned, "defaultversionid");
            }
            else
            {
                _ = RequiredString(expectation, "error");
            }
        }

        foreach (var registry in vector.GetProperty("registry").EnumerateArray())
        {
            ValidateObject(registry, $"registry resource in '{id}",
                ["wotid", "defaultversionsticky", "defaultversionid", "versions", "deprecated"],
                "wotid", "defaultversionsticky", "versions");
            _ = RequiredString(registry, "wotid");
            if (registry.GetProperty("defaultversionsticky").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                registry.GetProperty("versions").ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"Vector '{id}' contains an invalid Registry Resource.");
            }

            if (registry.TryGetProperty("defaultversionid", out var defaultVersion) &&
                defaultVersion.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"Vector '{id}' has an invalid defaultversionid.");
            }

            if (registry.TryGetProperty("deprecated", out var deprecated))
            {
                ValidateObject(deprecated, $"deprecation metadata in '{id}", ["alternative"], "alternative");
                _ = RequiredString(deprecated, "alternative");
            }

            foreach (var version in registry.GetProperty("versions").EnumerateArray())
            {
                ValidateObject(version, $"version in '{id}",
                    ["versionid", "versionorigin", "contentdigest", "version.model", "version.instance"],
                    "versionid", "versionorigin", "contentdigest");
                _ = RequiredString(version, "versionid");
                _ = RequiredString(version, "versionorigin");
                _ = RequiredString(version, "contentdigest");
                if (version.TryGetProperty("version.model", out _) &&
                    version.TryGetProperty("version.instance", out _))
                {
                    throw new InvalidDataException($"Vector '{id}' contains both WoT version terms on one Version.");
                }
            }
        }
    }

    private static void ValidateReference(string id, JsonElement reference)
    {
        ValidateObject(reference, $"reference for '{id}",
            ["kind", "target", "minVersion", "floorTerm", "local"], "kind", "target");
        _ = RequiredString(reference, "target");
        var kind = RequiredString(reference, "kind");
        if (kind is not ("extends" or "ref" or "td-extends" or "td-ref" or "td-subthing" or "type"))
        {
            throw new InvalidDataException($"Vector '{id}' uses unknown reference kind '{kind}'.");
        }

        _ = OptionalString(reference, "minVersion");
        var floorTerm = OptionalString(reference, "floorTerm");
        if (floorTerm is not (null or "tm:minVersion" or "td:minVersion"))
        {
            throw new InvalidDataException($"Vector '{id}' uses unknown floor term '{floorTerm}'.");
        }

        if (reference.TryGetProperty("local", out var local) &&
            local.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"Vector '{id}' has a non-boolean local flag.");
        }
    }

    private static void ValidateDocument(string id, JsonElement document)
    {
        ValidateObject(document, $"containing document for '{id}",
            ["versionid", "version.model", "version.instance", "typeLinks"]);
        _ = OptionalString(document, "versionid");
        _ = OptionalString(document, "version.model");
        _ = OptionalString(document, "version.instance");
        if (document.TryGetProperty("version.model", out _) &&
            document.TryGetProperty("version.instance", out _))
        {
            throw new InvalidDataException($"Vector '{id}' contains both WoT version terms in its document.");
        }

        if (document.TryGetProperty("typeLinks", out var links))
        {
            if (links.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"Vector '{id}' has a non-array typeLinks value.");
            }

            foreach (var link in links.EnumerateArray())
            {
                ValidateObject(link, $"type link in '{id}", ["target", "minVersion"], "target");
                _ = RequiredString(link, "target");
                _ = OptionalString(link, "minVersion");
            }
        }
    }

    private static void ValidateObject(JsonElement element, string context, string[] allowed,
        params string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The {context} must be a JSON object.");
        }

        var allowedNames = allowed.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowedNames.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw new InvalidDataException($"The {context} has an unknown or duplicate property '{property.Name}'.");
            }
        }

        if (required.Any(name => !seen.Contains(name)))
        {
            throw new InvalidDataException($"The {context} is missing a required property.");
        }
    }

    private static bool ExactlyOneOf(JsonElement element, string first, string second) =>
        element.TryGetProperty(first, out _) != element.TryGetProperty(second, out _);
}
