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
    private static string ResolutionDirectory => Path.Combine(AppContext.BaseDirectory, "Wot", "Resolution");
    private static string RegressionDirectory => Path.Combine(ResolutionDirectory, "regressions");
    private static string SharedDirectory => Path.Combine(ResolutionDirectory, "shared");

    public static IEnumerable<string> ResolutionVectorFiles() =>
        Directory.EnumerateFiles(RegressionDirectory, "*.json").Where(static path => IsVectorFile(Path.GetFileName(path)))
            .Order(StringComparer.Ordinal);

    public static IEnumerable<string> SharedVectorNames()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(SharedDirectory, "vectors.json")));
        return document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
    }

    [Test]
    public async Task ImplementationRegressionManifestMatchesCopiedBytes()
    {
        var manifestPath = Path.Combine(RegressionDirectory, "MANIFEST.sha256");
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        using var source = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(ResolutionDirectory, "sources.json")));
        await Assert.That(Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant())
            .IsEqualTo(source.RootElement.GetProperty("regressions").GetProperty("manifestSha256").GetString());
        await Assert.That(manifestBytes.Contains((byte)'\r')).IsFalse();
        await Assert.That(manifestBytes[^1]).IsEqualTo((byte)'\n');
        var entries = System.Text.Encoding.ASCII.GetString(manifestBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var files = Directory.GetFiles(RegressionDirectory).Where(path => Path.GetFileName(path) != "MANIFEST.sha256")
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
    public async Task ImplementationRegressionMatchesLiteralExpectation([MethodDataSource(nameof(ResolutionVectorFiles))] string path)
    {
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        await CheckLiteralExpectation(document.RootElement);
    }

    [Test]
    [CombinedDataSources]
    public async Task SharedResolutionVectorMatchesLiteralExpectation([MethodDataSource(nameof(SharedVectorNames))] string name)
    {
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(SharedDirectory, "vectors.json")));
        await CheckLiteralExpectation(document.RootElement.GetProperty(name));
    }

    [Test]
    public async Task SharedSourceManifestMatchesCopiedBytes()
    {
        using var source = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(ResolutionDirectory, "sources.json")));
        var files = source.RootElement.GetProperty("shared").GetProperty("files").EnumerateObject().ToArray();
        await Assert.That(files.Length).IsGreaterThan(0);
        var names = Directory.GetFiles(SharedDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        await Assert.That(names.SequenceEqual(files.Select(file => file.Name).Order(StringComparer.Ordinal))).IsTrue();
        foreach (var file in files)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(SharedDirectory, file.Name));
            await Assert.That(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()).IsEqualTo(file.Value.GetString());
        }
    }

    private static async Task CheckLiteralExpectation(JsonElement root)
    {
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
                    OptionalString(referenceJson, "floorTerm"))
                {
                    IsDocumentLocal = referenceJson.TryGetProperty("local", out var local) && local.GetBoolean()
                };
                var context = root.TryGetProperty("document", out var documentVersion)
                    ? ParseContext(documentVersion, reference, resources)
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
    [Arguments("18446744073709551616.0.0", "18446744073709551617.0.0")]
    [Arguments("1.18446744073709551616.0", "1.18446744073709551617.0")]
    [Arguments("1.0.18446744073709551616", "1.0.18446744073709551617")]
    [Arguments("1.0.0-18446744073709551616", "1.0.0-18446744073709551617")]
    public async Task SemanticVersionComparisonDoesNotOverflowMachineIntegers(string lower, string higher)
    {
        await Assert.That(WotSemanticVersion.Parse(lower) < WotSemanticVersion.Parse(higher)).IsTrue();
        await Assert.That(WotSemanticVersion.Parse(higher) > WotSemanticVersion.Parse(lower)).IsTrue();
    }

    [Test]
    public async Task VersionIdentifierLengthLimitAppliesToParsingAndPublication()
    {
        var valid = new string('9', 124) + ".0.0";
        var overlong = "9" + valid;
        await Assert.That(valid.Length).IsEqualTo(128);
        await Assert.That(WotSemanticVersion.TryParse(valid, out _)).IsTrue();
        await Assert.That(WotSemanticVersion.TryParse(overlong, out _)).IsFalse();
        WotResourceSnapshot[] resources = [new("model", false, valid, [new(valid, "authored", "bytes")])];
        await Assert.That(WotVersionResolver.Resolve(resources, new("ref", "model", valid), Origin))
            .IsEqualTo(new WotResolutionResult("model", valid, "bytes", Origin));
        await Assert.That(WotVersionResolver.AssignPublishedVersion(resources, "model", modelVersion: valid))
            .IsEqualTo(new WotPublicationAssignment(valid, "authored", valid));
        var floorFailure = await Assert.That(() => WotVersionResolver.Resolve(resources, new("ref", "model", overlong), Origin))
            .Throws<WotVersionException>();
        await Assert.That(floorFailure!.Code).IsEqualTo("unsatisfied");
        var publicationFailure = await Assert.That(() => WotVersionResolver.AssignPublishedVersion(resources, "model", modelVersion: overlong))
            .Throws<WotVersionException>();
        await Assert.That(publicationFailure!.Code).IsEqualTo("invalid-version");
    }

    [Test]
    [Arguments("tm:ref")]
    [Arguments("td:ref")]
    [Arguments("type")]
    public async Task ExplicitFloorHasNoCoreIdentifierLengthLimit(string member)
    {
        var floor = "1.0.0-" + new string('a', 123);
        await Assert.That(floor.Length).IsEqualTo(129);
        var json = member == "type"
            ? $$"""{"rel":"type","href":"urn:example:thing","uav:minVersion":"{{floor}}"}"""
            : $$"""{"{{member}}":"urn:example:thing","uav:minVersion":"{{floor}}"}""";
        using var source = JsonDocument.Parse(json);
        var reference = WotReferenceParser.Parse(source.RootElement);
        WotResourceSnapshot[] resources = [new("urn:example:thing", false, "1.0.0", [new("1.0.0", "authored", "bytes")])];
        var context = member == "type" ? new WotDocumentVersionContext(null, [new("urn:example:thing", floor)]) : null;
        await Assert.That(WotVersionResolver.Resolve(resources, reference, Origin, context))
            .IsEqualTo(new WotResolutionResult("urn:example:thing", "1.0.0", "bytes", Origin));
    }

    [Test]
    [Arguments(125)]
    [Arguments(5000)]
    public async Task LargeNumericFloorIsUnsatisfiedRatherThanInvalid(int digits)
    {
        var floor = new string('9', digits) + ".0.0";
        WotResourceSnapshot[] resources = [new("model", false, "1.0.0", [new("1.0.0", "authored", "bytes")])];
        var failure = await Assert.That(() => WotVersionResolver.Resolve(resources, new("ref", "model", floor), Origin))
            .Throws<WotVersionException>();
        await Assert.That(failure!.Code).IsEqualTo("unsatisfied");
    }

    [Test]
    [Arguments(122)]
    [Arguments(123)]
    [Arguments(5000)]
    public async Task ImplicitModelDependencyFloorHasNoCoreIdentifierLengthLimit(int suffixLength)
    {
        var floor = "1.0.0-" + new string('a', suffixLength);
        WotResourceSnapshot[] resources = [new("model", false, "1.0.0", [new("1.0.0", "authored", "bytes")])];
        var reference = new WotReferenceRequest("type", "model");
        var context = new WotDocumentVersionContext(floor, [new("model")]);
        await Assert.That(WotVersionResolver.Resolve(resources, reference, Origin, context))
            .IsEqualTo(new WotResolutionResult("model", "1.0.0", "bytes", Origin));
        await Assert.That(WotVersionResolver.Resolve(resources, reference, Origin, context, new(VersionId: "1.0.0")))
            .IsEqualTo(new WotResolutionResult("model", "1.0.0", "bytes", Origin));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LargeImplicitFloorIsUnsatisfiedWithOrWithoutAnExplicitFloor(bool explicitFloor)
    {
        var floor = new string('9', 125) + ".0.0";
        WotResourceSnapshot[] resources = [new("model", false, "1.0.0", [new("1.0.0", "authored", "bytes")])];
        var reference = new WotReferenceRequest("type", "model", explicitFloor ? "1.0.0" : null);
        var context = new WotDocumentVersionContext(floor, [new("model", reference.MinimumVersion)]);
        var failure = await Assert.That(() => WotVersionResolver.Resolve(resources, reference, Origin, context))
            .Throws<WotVersionException>();
        await Assert.That(failure!.Code).IsEqualTo("unsatisfied");
        await Assert.That(WotVersionResolver.Resolve(resources, reference, Origin, context, new(VersionId: "1.0.0")))
            .IsEqualTo(new WotResolutionResult("model", "1.0.0", "bytes", Origin));
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

    private static WotDocumentVersionContext ParseContext(JsonElement document, WotReferenceRequest reference,
        WotResourceSnapshot[] resources)
    {
        var links = document.TryGetProperty("typeLinks", out var typeLinks)
            ? typeLinks.EnumerateArray().Select(link =>
                new WotTypeLink(link.GetProperty("target").GetString()!, OptionalString(link, "minVersion"))).ToArray()
            : [];
        WotVersionSnapshot? containing = null;
        if (reference.IsDocumentLocal)
        {
            containing = resources.Single(resource => resource.WotId == reference.Target).Versions
                .Single(version => version.VersionId == document.GetProperty("versionid").GetString()) with
            {
                ModelVersion = OptionalString(document, "version.model"),
                InstanceVersion = OptionalString(document, "version.instance")
            };
        }

        return new(OptionalString(document, "version.model"), links) { ContainingVersion = containing };
    }

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
