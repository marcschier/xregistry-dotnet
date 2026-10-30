// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using XRegistry.Models;

namespace XRegistry.Server;

public sealed partial class RegistryEngine
{
    private sealed partial class Request
    {
        private const string WotAllocationsKey = "$wotalloc";
        private readonly Dictionary<string, string> _wotAllocations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _wotRevisionDocuments = new(StringComparer.Ordinal);
        private sealed record WotVersionAssignment(string VersionId, string Origin, string WotId);

        private static bool IsWotResource(RegistryResourceDefinition definition) =>
            definition.Annotations.ModelCompatibleWith == RegistryDomainRules.WotModelUri &&
            definition.Singular is "thingmodel" or "thingdescription";

        private Entity ApplyAssignedVersion(Entity resource, string? requestedVersionId, JsonObject input,
            bool patch, bool documentMode, bool generated)
        {
            var definition = ResourceDefinition(RegistryPath.Parse(resource.Key));
            var assignment = IsWotResource(definition)
                ? AssignWotVersion(resource, definition, input, requestedVersionId)
                : null;
            var versionId = assignment?.VersionId ?? requestedVersionId ?? GenerateVersionId(resource, definition);
            var version = ApplyVersion(resource, versionId, input, patch, documentMode,
                generated || assignment is not null || requestedVersionId is null);
            if (assignment is not null && ServerJson.Text(version.Attributes, "versionorigin") != assignment.Origin)
            {
                version.Attributes["versionorigin"] = assignment.Origin;
                Touch(version);
            }

            return version;
        }

        private bool IsIncrementalWot(RegistryResourceDefinition definition)
        {
            if (!IsWotResource(definition)) { return false; }
            var profile = WotRegistryProfiles.Read(_model);
            if (profile == WotVersionProfile.Incremental &&
                (definition.VersionMode != "manual" || definition.SingleVersionRoot))
            {
                throw ServerErrors.Create("unknown-profile", "/model",
                    "Incremental WoT Resources require manual versioning and multiple roots.");
            }
            return profile == WotVersionProfile.Incremental;
        }

        private WotVersionAssignment? AssignWotVersion(Entity resource, RegistryResourceDefinition definition,
            JsonObject input, string? requestedVersionId)
        {
            var bytes = WotDocumentBytes(definition, input);
            if (bytes is null)
            {
                if (IsIncrementalWot(definition) && (input.ContainsKey(definition.Singular) ||
                    requestedVersionId is null ||
                    Find(resource.Key + "/versions/" + Uri.EscapeDataString(requestedVersionId)) is null))
                {
                    throw ServerErrors.With("invalid_attribute", resource.Key,
                        "A new incremental WoT revision requires its complete Document.", ("name", definition.Singular));
                }
                return null;
            }

            JsonDocument document;
            try
            {
                document = RegistryJson.ParseDocument(bytes, _engine.Limits.Json);
            }
            catch (RegistryException exception)
            {
                throw ServerErrors.Wrap(exception, "invalid_attribute", resource.Key,
                    ("name", definition.Singular));
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String)
                {
                    throw ServerErrors.With("invalid_attribute", resource.Key,
                        "A WoT Document must have a string id member.", ("name", definition.Singular));
                }

                var wotId = idElement.GetString()!;
                if (input.ContainsKey("wotid") &&
                    (input["wotid"] is not JsonValue submittedWotId ||
                     !submittedWotId.TryGetValue<string>(out var submitted) ||
                     !StringComparer.Ordinal.Equals(wotId, submitted)))
                {
                    throw ServerErrors.With("invalid_attribute", resource.Key,
                        "The wotid attribute must equal the Document's id member.", ("name", "wotid"));
                }

                input["wotid"] = wotId;
                var versionField = definition.Singular == "thingmodel" ? "model" : "instance";
                if (IsIncrementalWot(definition))
                {
                    return AssignIncrementalWotVersion(resource, input, requestedVersionId, root, versionField, bytes, wotId);
                }
                var authoredVersion = AuthoredWotVersion(root, versionField, resource.Key);
                var origin = authoredVersion is null ? "generated" : "authored";
                var existingGeneratedVersion = requestedVersionId is not null &&
                    Find(resource.Key + "/versions/" + Uri.EscapeDataString(requestedVersionId)) is { } existing &&
                    ServerJson.Text(existing.Attributes, "versionorigin") == "generated";
                var versionId = authoredVersion ?? (existingGeneratedVersion
                    ? requestedVersionId! : LowestFreeWotVersionId(resource));
                var submittedVersionId = Identifier(input, "versionid", resource.Key);
                var candidate = requestedVersionId ?? submittedVersionId;
                if (candidate is not null && !StringComparer.Ordinal.Equals(candidate, versionId))
                {
                    throw ServerErrors.With("version-mismatch", resource.Key,
                        "versionid does not match the WoT Document's assigned Version id.",
                        ("singular", "version"), ("invalid_id", candidate), ("expected_id", versionId));
                }

                return new(versionId, origin, wotId);
            }
        }

        private WotVersionAssignment AssignIncrementalWotVersion(Entity resource, JsonObject input,
            string? requestedVersionId, JsonElement root, string versionField, byte[] bytes, string wotId)
        {
            string? authored = null;
            if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Object &&
                version.TryGetProperty(versionField, out var value) && value.ValueKind == JsonValueKind.String)
            {
                authored = value.GetString();
            }

            var submitted = Identifier(input, "versionid", resource.Key);
            if (requestedVersionId is not null && submitted is not null && requestedVersionId != submitted)
            {
                throw ServerErrors.Create("version-mismatch", resource.Key, "Conflicting incremental revision ids.");
            }
            var candidate = requestedVersionId ?? submitted;
            ApplyIncrementalLine(input, root, resource.Key, ResourceDefinition(RegistryPath.Parse(resource.Key)));
            if (candidate is not null &&
                Find(resource.Key + "/versions/" + Uri.EscapeDataString(candidate)) is { } existing)
            {
                using var stored = _wotRevisionDocuments.TryGetValue(existing.Key, out var staged)
                    ? new MemoryStream(staged, writable: false) : _snapshot.OpenDocument(existing.Key, _ct);
                var offset = 0;
                Span<byte> buffer = stackalloc byte[4096];
                int count;
                while ((count = stored.Read(buffer)) != 0)
                {
                    Work();
                    if (count > bytes.Length - offset || !buffer[..count].SequenceEqual(bytes.AsSpan(offset, count)))
                    {
                        throw ServerErrors.Create("invalid_attribute", existing.Key,
                            "An existing incremental revision's Document cannot change.");
                    }
                    offset += count;
                }
                if (offset != bytes.Length)
                {
                    throw ServerErrors.Create("invalid_attribute", existing.Key,
                        "An existing incremental revision's Document cannot change.");
                }
                return new(candidate, ServerJson.Text(existing.Attributes, "versionorigin")!, wotId);
            }

            var key = WotAllocationsKey + resource.Key;
            if (!_wotAllocations.TryGetValue(key, out var watermark))
            {
                Work();
                var record = _snapshot.Find(key);
                if (record is null)
                {
                    if (Children(resource.Key + "/versions").Any())
                    {
                        throw ServerErrors.Create("invalid-version", resource.Key,
                            "Published incremental revisions have no durable allocation history.");
                    }
                    watermark = "0";
                }
                else if (!record.Metadata.RootElement.TryGetProperty("highwater", out var highwater) ||
                    highwater.ValueKind != JsonValueKind.String ||
                    (watermark = highwater.GetString()!) == "0" ||
                    watermark.Length is < 1 or > 128 || watermark[0] is < '1' or > '9' ||
                    !watermark.All(char.IsAsciiDigit))
                {
                    throw ServerErrors.Create("invalid-version", resource.Key,
                        "The durable incremental allocation history is corrupt.");
                }
                var maximum = BigInteger.Parse(watermark, NumberStyles.None, CultureInfo.InvariantCulture);
                foreach (var retained in Children(resource.Key + "/versions"))
                {
                    Work();
                    var id = Id(retained);
                    if (id.Length is < 1 or > 128 || id[0] is < '1' or > '9' || !id.All(char.IsAsciiDigit) ||
                        BigInteger.Parse(id, NumberStyles.None, CultureInfo.InvariantCulture) > maximum)
                    {
                        throw ServerErrors.Create("invalid-version", resource.Key,
                            "Retained incremental revisions disagree with the durable allocation history.");
                    }
                }
            }
            var assignment = WotIncrementalVersionAllocator.Assign(watermark, authored, candidate);
            _wotAllocations[key] = assignment.HighWaterMark;
            _wotRevisionDocuments[resource.Key + "/versions/" + Uri.EscapeDataString(assignment.VersionId)] = bytes;
            return new(assignment.VersionId, assignment.Origin, wotId);
        }

        private void ApplyIncrementalLine(JsonObject input, JsonElement root, string path,
            RegistryResourceDefinition definition)
        {
            const string lineIri = "http://opcfoundation.org/UA/WoT-Binding/compatibilityLine";
            if (root.TryGetProperty("uav:compatibilityLine", out _))
            {
                throw ServerErrors.Create("invalid-line", path,
                    "Compact compatibility-line terms require JSON-LD context processing; use authorized metadata or the expanded IRI.");
            }
            var metadata = input["compatibilityline"];
            string? line = null;
            if (metadata is not null)
            {
                if (metadata is not JsonValue scalar || !scalar.TryGetValue<string>(out line))
                {
                    throw ServerErrors.Create("invalid-line", path, "The compatibility line must be an absolute URI.");
                }
            }
            if (root.TryGetProperty(lineIri, out var declaration))
            {
                if (declaration.ValueKind != JsonValueKind.String ||
                    line is not null && line != declaration.GetString())
                {
                    throw ServerErrors.Create("invalid-line", path, "Document and metadata compatibility lines must agree exactly.");
                }
                line = declaration.GetString();
            }
            if (line is not null)
            {
                try
                {
                    _ = RegistryMetadataValidator.Validate(RegistryJson.Parse(new JsonObject
                        { ["compatibilityline"] = line }.ToJsonString()),
                        new Dictionary<string, RegistryAttributeDefinition>
                        { ["compatibilityline"] = definition.Attributes["compatibilityline"] },
                        new() { Limits = _engine.Limits.Json }, _ct);
                }
                catch (RegistryException exception)
                {
                    throw ServerErrors.Wrap(exception, "invalid-line", path);
                }
                input["compatibilityline"] = line;
            }
        }

        private byte[]? WotDocumentBytes(RegistryResourceDefinition definition, JsonObject input)
        {
            if (_wotDocumentInput is { } requestDocument)
            {
                return requestDocument;
            }

            var name = definition.Singular;
            var representations = new[] { name, name + "base64", name + "url" }
                .Where(input.ContainsKey).ToArray();
            if (representations.Length > 1)
            {
                throw ServerErrors.With("one_resource", _operation.Path.EscapedPath,
                    "Only one Document representation may be supplied.",
                    ("list", string.Join(", ", representations)));
            }

            if (input.ContainsKey(name + "url"))
            {
                throw ServerErrors.With("invalid_attribute", _operation.Path.EscapedPath,
                    "WoT Versions require the complete Document to be stored by the Registry.",
                    ("name", name + "url"));
            }

            if (input.TryGetPropertyValue(name + "base64", out var encoded))
            {
                if (encoded is not JsonValue scalar || !scalar.TryGetValue<string>(out var text))
                {
                    throw ServerErrors.With("invalid_attribute", _operation.Path.EscapedPath,
                        "The base64 Document must be a string.", ("name", name + "base64"));
                }

                try
                {
                    var decoded = Convert.FromBase64String(text);
                    CheckDocumentSize(decoded.Length, _operation.Path.EscapedPath);
                    return decoded;
                }
                catch (FormatException exception)
                {
                    throw ServerErrors.Wrap(new RegistryException(new("invalid_attribute", _operation.Path.EscapedPath,
                        "The Document is not valid base64."), exception), "invalid_attribute", _operation.Path.EscapedPath,
                        ("name", name + "base64"));
                }
            }

            if (input.TryGetPropertyValue(name, out var document) && document is not null)
            {
                return ServerJson.Encode(document, _engine.Limits.MaxDocumentBytes);
            }

            return null;
        }

        private string LowestFreeWotVersionId(Entity resource)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var version in Children(resource.Key + "/versions"))
            {
                Work();
                existing.Add(Id(version));
            }

            for (var number = BigInteger.One; ; number++)
            {
                Work();
                var candidate = "0.0.0-" + number.ToString(CultureInfo.InvariantCulture);
                if (!existing.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        private static string? AuthoredWotVersion(JsonElement document, string field, string path)
        {
            if (!document.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.Object ||
                !version.TryGetProperty(field, out var authored) ||
                authored.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = authored.GetString()!;
            if (!IsSemVer(value, out var hasBuild))
            {
                return null;
            }

            if (hasBuild || value.Length > 128)
            {
                throw ServerErrors.With("invalid-version", path,
                    "A WoT authored Version must be SemVer 2.0.0 without build metadata and fit the Core id limit.",
                    ("name", "versionid"));
            }

            return value;
        }

        private static bool IsSemVer(string value, out bool hasBuild)
        {
            var valueSpan = value.AsSpan();
            var buildSeparator = valueSpan.IndexOf('+');
            hasBuild = buildSeparator >= 0;
            if (hasBuild)
            {
                var build = valueSpan[(buildSeparator + 1)..];
                if (build.IndexOf('+') >= 0 || !ValidSemVerIdentifiers(build, preRelease: false))
                {
                    return false;
                }

                valueSpan = valueSpan[..buildSeparator];
            }

            var prereleaseSeparator = valueSpan.IndexOf('-');
            var prerelease = prereleaseSeparator >= 0 ? valueSpan[(prereleaseSeparator + 1)..] : default;
            var core = prereleaseSeparator >= 0 ? valueSpan[..prereleaseSeparator] : valueSpan;
            var firstDot = core.IndexOf('.');
            if (firstDot < 0)
            {
                return false;
            }

            var secondPart = core[(firstDot + 1)..];
            var secondDot = secondPart.IndexOf('.');
            if (secondDot < 0 || secondPart[(secondDot + 1)..].IndexOf('.') >= 0 ||
                !ValidSemVerNumber(core[..firstDot]) ||
                !ValidSemVerNumber(secondPart[..secondDot]) ||
                !ValidSemVerNumber(secondPart[(secondDot + 1)..]))
            {
                return false;
            }

            return prereleaseSeparator < 0 || ValidSemVerIdentifiers(prerelease, preRelease: true);
        }

        private static bool ValidSemVerNumber(ReadOnlySpan<char> value) =>
            IsAsciiNumeric(value) && (value.Length == 1 || value[0] != '0');

        private static bool ValidSemVerIdentifiers(ReadOnlySpan<char> value, bool preRelease)
        {
            while (true)
            {
                var separator = value.IndexOf('.');
                var identifier = separator < 0 ? value : value[..separator];
                if (identifier.Length == 0)
                {
                    return false;
                }

                foreach (var character in identifier)
                {
                    if (!char.IsAsciiLetterOrDigit(character) && character != '-')
                    {
                        return false;
                    }
                }

                if (preRelease && identifier.Length > 1 && identifier[0] == '0' && IsAsciiNumeric(identifier))
                {
                    return false;
                }

                if (separator < 0)
                {
                    return true;
                }

                value = value[(separator + 1)..];
            }
        }

        private static bool IsAsciiNumeric(ReadOnlySpan<char> value)
        {
            if (value.Length == 0)
            {
                return false;
            }

            foreach (var character in value)
            {
                if (!char.IsAsciiDigit(character))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
