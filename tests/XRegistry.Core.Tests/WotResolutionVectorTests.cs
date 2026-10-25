// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Wot;

namespace XRegistry.Core.Tests;

public class WotResolutionVectorTests
{
    private const string Origin = "https://registry.example";
    private static string VectorDirectory => Path.Combine(AppContext.BaseDirectory, "Wot", "Resolution", "vectors");

    public static IEnumerable<string> ResolutionVectorFiles() =>
        Directory.EnumerateFiles(VectorDirectory, "*.json").Where(static path => IsVectorFile(Path.GetFileName(path)))
            .Order(StringComparer.Ordinal);

    [Test]
    public async Task CanonicalVectorManifestMatchesCopiedBytes()
    {
        var manifestPath = Path.Combine(VectorDirectory, "MANIFEST.sha256");
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        await Assert.That(manifestBytes.Contains((byte)'\r')).IsFalse();
        await Assert.That(manifestBytes[^1]).IsEqualTo((byte)'\n');
        var entries = System.Text.Encoding.ASCII.GetString(manifestBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var files = Directory.GetFiles(VectorDirectory).Where(path => Path.GetFileName(path) != "MANIFEST.sha256")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
        await Assert.That(entries.Length).IsEqualTo(files.Length);

        for (var index = 0; index < entries.Length; index++)
        {
            var separator = entries[index].IndexOf("  ", StringComparison.Ordinal);
            await Assert.That(separator).IsEqualTo(64);
            var path = files[index];
            await Assert.That(entries[index][(separator + 2)..]).IsEqualTo(Path.GetFileName(path));
            var bytes = await File.ReadAllBytesAsync(path);
            await Assert.That(bytes.Contains((byte)'\r')).IsFalse();
            await Assert.That(bytes[^1]).IsEqualTo((byte)'\n');
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            await Assert.That(entries[index][..separator]).IsEqualTo(digest);
        }
    }

    [Test]
    [CombinedDataSources]
    public async Task CanonicalResolutionVectorMatchesImplementation([MethodDataSource(nameof(ResolutionVectorFiles))] string path)
    {
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        var root = document.RootElement;
        var resources = root.GetProperty("registry").EnumerateArray().Select(ParseResource).ToArray();
        var expect = root.GetProperty("expect");
        string? error = null;
        WotResolutionResult? resolution = null;
        WotPublicationAssignment? assignment = null;

        try
        {
            if (root.TryGetProperty("publish", out var publish))
            {
                var operation = WotVersionResolver.AssignPublishedVersion(resources,
                    publish.GetProperty("target").GetString()!,
                    OptionalString(publish, "versionid"), OptionalString(publish, "version.model"),
                    OptionalString(publish, "targetKind") ?? "tm", OptionalString(publish, "version.instance"));
                assignment = operation;
            }
            else
            {
                var referenceJson = root.GetProperty("reference");
                var reference = new WotReferenceRequest(referenceJson.GetProperty("kind").GetString()!,
                    referenceJson.GetProperty("target").GetString()!, OptionalString(referenceJson, "minVersion"),
                    OptionalString(referenceJson, "floorTerm"));
                var context = root.TryGetProperty("document", out var documentVersion)
                    ? new WotDocumentVersionContext(OptionalString(documentVersion, "version.model"),
                        documentVersion.GetProperty("typeLinks").EnumerateArray().Select(link =>
                            new WotTypeLink(link.GetProperty("target").GetString()!, OptionalString(link, "minVersion"))).ToArray())
                    : null;
                var pin = root.TryGetProperty("pin", out var pinJson)
                    ? new WotVersionPin(OptionalString(pinJson, "digest"), OptionalString(pinJson, "versionid"))
                    : null;
                resolution = WotVersionResolver.Resolve(resources, reference, Origin, context, pin,
                    root.TryGetProperty("allowPrerelease", out var allow) && allow.GetBoolean());
            }
        }
        catch (WotVersionException exception)
        {
            error = exception.Code;
        }

        if (expect.TryGetProperty("error", out var expectedError))
        {
            await Assert.That(error).IsEqualTo(expectedError.GetString());
            return;
        }

        await Assert.That(error).IsNull();
        if (expect.TryGetProperty("result", out var result))
        {
            await Assert.That(resolution).IsNotNull();
            await Assert.That(resolution!.WotId).IsEqualTo(result.GetProperty("wotid").GetString());
            await Assert.That(resolution.VersionId).IsEqualTo(result.GetProperty("versionid").GetString());
            await Assert.That(resolution.ContentDigest).IsEqualTo(result.GetProperty("contentdigest").GetString());
            await Assert.That(resolution.Origin).IsEqualTo(Origin);
            return;
        }

        var assigned = expect.GetProperty("assigned");
        await Assert.That(assignment).IsNotNull();
        await Assert.That(assignment!.VersionId).IsEqualTo(assigned.GetProperty("versionid").GetString());
        await Assert.That(assignment.VersionOrigin).IsEqualTo(assigned.GetProperty("versionorigin").GetString());
        await Assert.That(assignment.DefaultVersionId).IsEqualTo(assigned.GetProperty("defaultversionid").GetString());
    }

    [Test]
    [Arguments("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [Arguments("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
    [Arguments("1.0.0-alpha.beta", "1.0.0-beta", -1)]
    [Arguments("1.0.0-beta", "1.0.0-beta.2", -1)]
    [Arguments("1.0.0-beta.2", "1.0.0-beta.11", -1)]
    [Arguments("1.0.0-beta.11", "1.0.0-rc.1", -1)]
    [Arguments("1.0.0-rc.1", "1.0.0", -1)]
    [Arguments("1.0.0-2", "1.0.0-10", -1)]
    [Arguments("1.0.0-1", "1.0.0-alpha", -1)]
    [Arguments("2.0.0", "10.0.0", -1)]
    [Arguments("1.0.0-Alpha", "1.0.0-alpha", -1)]
    [Arguments("1.0.0", "1.0.0", 0)]
    public async Task SemanticVersionPrecedenceFollowsSemVer(string left, string right, int expected)
    {
        var comparison = WotSemanticVersion.Parse(left).CompareTo(WotSemanticVersion.Parse(right));
        await Assert.That(Math.Sign(comparison)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("01.0.0")]
    [Arguments("1.00.0")]
    [Arguments("1.0.0-01")]
    [Arguments("1.0.0-")]
    [Arguments("1.0.0-alpha..beta")]
    [Arguments("1.0.0+build")]
    [Arguments("v1.0.0")]
    public async Task SemanticVersionParserRejectsInvalidOrBuildMetadata(string value)
    {
        await Assert.That(WotSemanticVersion.TryParse(value, out _)).IsFalse();
    }

    [Test]
    [Arguments("td-extends")]
    [Arguments("td-subthing")]
    [Arguments("td-ref")]
    public async Task TdCaretExcludesNextMajorPrereleaseEvenWithOptIn(string kind)
    {
        WotResourceSnapshot[] resources = [new("thing", false, "2.0.0-rc.1",
            [new("1.2.0", "authored", "in-range"), new("2.0.0-rc.1", "authored", "next-major")])];
        var reference = new WotReferenceRequest(kind, "thing", "1.2.0");
        var selected = WotVersionResolver.Resolve(resources, reference, Origin, allowPrerelease: true);
        await Assert.That(selected.VersionId).IsEqualTo("1.2.0");
        await Assert.That(selected.ContentDigest).IsEqualTo("in-range");
        resources[0] = resources[0] with { DefaultVersionSticky = true, DefaultVersionId = "1.2.0" };
        foreach (var pin in new[] { new WotVersionPin(VersionId: "2.0.0-rc.1"), new WotVersionPin(Digest: "next-major") })
        {
            var pinned = WotVersionResolver.Resolve(resources, reference, Origin, pin: pin);
            await Assert.That(pinned.VersionId).IsEqualTo("2.0.0-rc.1");
            await Assert.That(pinned.ContentDigest).IsEqualTo("next-major");
        }
    }

    [Test]
    [Arguments("td-extends")]
    [Arguments("td-subthing")]
    [Arguments("td-ref")]
    public async Task TdReferencesDoNotDeriveImplicitModelFloors(string kind)
    {
        WotResourceSnapshot[] resources = [new("thing", false, "2.1.0", [new("2.1.0", "authored", "selected")])];
        var context = new WotDocumentVersionContext("not-semver", [new("model-a"), new("model-b")]);
        var selected = WotVersionResolver.Resolve(resources, new(kind, "thing"), Origin, context);
        await Assert.That(selected).IsEqualTo(new WotResolutionResult("thing", "2.1.0", "selected", Origin));
    }

    [Test]
    [Arguments("1.2.0", "authored", "1.2.0")]
    [Arguments("legacy", "generated", "0.0.0-2")]
    [Arguments(null, "generated", "0.0.0-2")]
    public async Task TdPublicationUsesInstanceVersionAndIgnoresModelVersion(string? instance, string expectedOrigin, string expectedId)
    {
        WotResourceSnapshot[] resources = [new("thing", false, "0.0.0-1", [new("0.0.0-1", "authored", "existing")])];
        var assignment = WotVersionResolver.AssignPublishedVersion(resources, "thing", modelVersion: "9.0.0+build",
            targetKind: "td", instanceVersion: instance);
        await Assert.That(assignment).IsEqualTo(new WotPublicationAssignment(expectedId, expectedOrigin, expectedId));
    }

    [Test]
    public async Task TmPublicationIgnoresInstanceVersion()
    {
        WotResourceSnapshot[] resources = [new("model", false, null, [])];
        var assignment = WotVersionResolver.AssignPublishedVersion(resources, "model", modelVersion: "1.2.0",
            instanceVersion: "9.0.0+build");
        await Assert.That(assignment).IsEqualTo(new WotPublicationAssignment("1.2.0", "authored", "1.2.0"));
    }

    private static bool IsVectorFile(string name) => name.Length > 4 && name[3] == '-' && char.IsAsciiDigit(name[0]) && char.IsAsciiDigit(name[1]) && char.IsAsciiDigit(name[2]);

    private static WotResourceSnapshot ParseResource(JsonElement resource)
    {
        var versions = resource.GetProperty("versions").EnumerateArray().Select(version => new WotVersionSnapshot(
            version.GetProperty("versionid").GetString()!, version.GetProperty("versionorigin").GetString()!,
            version.GetProperty("contentdigest").GetString()!, OptionalString(version, "version.model"),
            OptionalString(version, "version.instance"))).ToArray();
        return new(resource.GetProperty("wotid").GetString()!, resource.GetProperty("defaultversionsticky").GetBoolean(),
            resource.GetProperty("defaultversionid").ValueKind == JsonValueKind.Null ? null : resource.GetProperty("defaultversionid").GetString(), versions);
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() : null;
}