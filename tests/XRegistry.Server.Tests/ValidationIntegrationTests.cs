// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Security.Claims;
using TUnit.Assertions;
using TUnit.Core;
using static XRegistry.Server.Tests.EngineTests;
using static XRegistry.Server.Tests.LifecycleTests;

namespace XRegistry.Server.Tests;

public class ValidationIntegrationTests
{
    private const string ValidatingModel = """
        {"groups":{"teams":{"singular":"team","resources":{"files":{
          "singular":"file","validateformat":true,"validatecompatibility":true
        }}}}}
        """;

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task DisabledFormatValidationSkipsRecognizedFormatsAndOmitsResultFlags(bool strict, bool omitted)
    {
        var validator = new ExactValidator();
        var model = ValidatingModel.Replace("\"validateformat\":true", "\"validateformat\":false,\"strictvalidation\":" +
            (strict ? "true" : "false"), StringComparison.Ordinal)
            .Replace("\"validatecompatibility\":true", "\"validatecompatibility\":false", StringComparison.Ordinal);
        if (omitted)
        {
            model = model.Replace("\"validateformat\":false,", "", StringComparison.Ordinal);
        }
        var compiled = RegistryModel.Compile(RegistryJson.Parse(model));
        await Assert.That(compiled.Groups["teams"].Resources["files"].ValidateFormat).IsFalse();
        var engine = new RegistryEngine(new()
        {
            RegistryId = "disabled-format",
            PublicRoot = new Uri("https://registry.example"),
            Model = compiled,
            ResourceValidator = validator
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        await Send(engine, RegistryAction.Replace, "/teams/g/files/f$details", """
            {"versionid":"v1","format":"test/v1","filebase64":"AA=="}
            """);
        await Send(engine, RegistryAction.Patch, "/teams/g/files/f/versions/v1$details", """
            {"filebase64":"AQ=="}
            """);
        await Send(engine, RegistryAction.Post, "/teams/g/files/f$details", """
            {"versionid":"v2","format":"test/v1","filebase64":"Ag=="}
            """);
        foreach (var version in new[] { "v1", "v2" })
        {
            var result = await Send(engine, RegistryAction.Read, "/teams/g/files/f/versions/" + version + "$details");
            await Assert.That(result.Metadata!.RootElement.GetProperty("format").GetString()).IsEqualTo("test/v1");
            foreach (var field in new[] { "formatvalidated", "formatvalidatedreason", "compatibilityvalidated", "compatibilityvalidatedreason" })
            {
                await Assert.That(result.Metadata.RootElement.TryGetProperty(field, out _)).IsFalse();
            }
            var stored = await Send(engine, RegistryAction.Read, "/teams/g/files/f/versions/" + version);
            using var bytes = stored.Document!.OpenRead();
            await Assert.That(bytes.ReadByte()).IsEqualTo(version == "v1" ? 1 : 2);
            await Assert.That(bytes.ReadByte()).IsEqualTo(-1);
        }
        await Assert.That(validator.Calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task VersionsWithoutFormatSkipValidatorCallsAndValidationFlags(bool validateFormat, bool strict)
    {
        var validator = new ExactValidator();
        var model = ValidatingModel.Replace("\"validateformat\":true",
            "\"validateformat\":" + (validateFormat ? "true" : "false") +
            ",\"strictvalidation\":" + (strict ? "true" : "false"), StringComparison.Ordinal)
            .Replace("\"validatecompatibility\":true", "\"validatecompatibility\":" +
                (validateFormat ? "true" : "false"), StringComparison.Ordinal);
        var engine = new RegistryEngine(new()
        {
            RegistryId = "no-format",
            PublicRoot = new Uri("https://registry.example"),
            Model = RegistryModel.Compile(RegistryJson.Parse(model)),
            ResourceValidator = validator
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        await Send(engine, RegistryAction.Replace, "/teams/g/files/f$details", """
            {"versionid":"v1","filebase64":"AA==","meta":{"compatibility":"full"}}
            """);
        await Send(engine, RegistryAction.Patch, "/teams/g/files/f/versions/v1$details", """
            {"filebase64":"/wA="}
            """);
        await Send(engine, RegistryAction.Post, "/teams/g/files/f$details", """
            {"versionid":"v2","filebase64":"AA=="}
            """);
        foreach (var version in new[] { "v1", "v2" })
        {
            var result = await Send(engine, RegistryAction.Read, "/teams/g/files/f/versions/" + version + "$details");
            foreach (var field in new[] { "formatvalidated", "formatvalidatedreason", "compatibilityvalidated", "compatibilityvalidatedreason" })
            {
                await Assert.That(result.Metadata!.RootElement.TryGetProperty(field, out _)).IsFalse();
            }
        }
        await Assert.That(validator.Calls).IsEqualTo(0);
        var meta = await Send(engine, RegistryAction.Read, "/teams/g/files/f/meta");
        await Assert.That(meta.Metadata!.RootElement.GetProperty("compatibility").GetString()).IsEqualTo("full");
        var first = await Send(engine, RegistryAction.Read, "/teams/g/files/f/versions/v1");
        using var firstBytes = first.Document!.OpenRead();
        await Assert.That(firstBytes.ReadByte()).IsEqualTo(255);
        await Assert.That(firstBytes.ReadByte()).IsEqualTo(0);
        await Assert.That(firstBytes.ReadByte()).IsEqualTo(-1);
        var second = await Send(engine, RegistryAction.Read, "/teams/g/files/f/versions/v2");
        using var secondBytes = second.Document!.OpenRead();
        await Assert.That(secondBytes.ReadByte()).IsEqualTo(0);
        await Assert.That(secondBytes.ReadByte()).IsEqualTo(-1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RemovingFormatClearsPriorValidationFlagsWithoutAnotherValidatorCall(bool strict)
    {
        var validator = new ExactValidator();
        var model = ValidatingModel.Replace("\"validateformat\":true", "\"validateformat\":true,\"strictvalidation\":" +
            (strict ? "true" : "false"), StringComparison.Ordinal);
        var engine = new RegistryEngine(new()
        {
            RegistryId = "remove-format",
            PublicRoot = new Uri("https://registry.example"),
            Model = RegistryModel.Compile(RegistryJson.Parse(model)),
            ResourceValidator = validator
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        var valid = await Send(engine, RegistryAction.Replace, "/teams/g/files/f$details", """
            {"versionid":"v1","format":"test/v1","filebase64":"/wA="}
            """);
        await Assert.That(valid.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await Assert.That(validator.Calls).IsEqualTo(1);
        var cleared = await Send(engine, RegistryAction.Patch, "/teams/g/files/f/versions/v1$details", """
            {"format":null,"filebase64":"AA=="}
            """);
        foreach (var field in new[] { "format", "formatvalidated", "formatvalidatedreason", "compatibilityvalidated", "compatibilityvalidatedreason" })
        {
            await Assert.That(cleared.Metadata!.RootElement.TryGetProperty(field, out _)).IsFalse();
        }
        await Assert.That(validator.Calls).IsEqualTo(1);
        var stored = await Send(engine, RegistryAction.Read, "/teams/g/files/f/versions/v1");
        using var bytes = stored.Document!.OpenRead();
        await Assert.That(bytes.ReadByte()).IsEqualTo(0);
        await Assert.That(bytes.ReadByte()).IsEqualTo(-1);
    }

    [Test]
    public async Task UriTargetObligationsRequireAnExplicitValidatorAndFailuresRollBack()
    {
        const string model = """
            {"attributes":{"link":{"type":"url","target":"/teams/notes"}},
             "groups":{"teams":{"singular":"team","resources":{"notes":{"singular":"note","hasdocument":false}}}}}
            """;
        await ExpectCode(() => Send(Create(model), RegistryAction.Patch, "/",
            """{"link":"/teams/g/notes/n"}"""), "obligation_policy_required");
        var validator = new TargetValidator();
        var engine = new RegistryEngine(new()
        {
            RegistryId = "targets",
            PublicRoot = new Uri("https://registry.example/catalog"),
            Model = RegistryModel.Compile(RegistryJson.Parse(model)),
            ObligationValidator = validator
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        await Send(engine, RegistryAction.Patch, "/", """{"link":"/teams/g/notes/n"}""");
        await ExpectCode(() => Send(engine, RegistryAction.Patch, "/", """{"link":"/teams/g/notes/denied"}"""), "forbidden");
        await Assert.That(validator.Calls).IsEqualTo(2);
        await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("link").GetString())
            .IsEqualTo("/teams/g/notes/n");
    }

    [Test]
    public async Task TimedOutValidationDoesNotPublishOrStartUnboundedReplacementWork()
    {
        var validator = new PausedValidator();
        var engine = new RegistryEngine(new()
        {
            RegistryId = "bounded",
            PublicRoot = new Uri("https://registry.example"),
            Model = RegistryModel.Compile(RegistryJson.Parse(ValidatingModel)),
            ResourceValidator = validator,
            Limits = new() { ValidationTimeout = TimeSpan.FromMilliseconds(100), MaxConcurrentExternalCalls = 1 }
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        try
        {
            var first = ExpectCode(() => Send(engine, RegistryAction.Replace, "/teams/g/files/f$details", """{"format":"slow"}"""), "server_busy");
            await validator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await first;
            await ExpectCode(() => Send(engine, RegistryAction.Replace, "/teams/g/files/second$details", """{"format":"slow"}"""), "server_busy");
            await Assert.That(validator.Calls).IsEqualTo(1);
            await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
        }
        finally
        {
            validator.Release.SetResult();
        }

        await validator.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task UnconfiguredFormatValidationIsExplicitlyUnknownNeverSuccessful()
    {
        var engine = Create(ValidatingModel);
        var result = await Send(engine, RegistryAction.Replace, "/teams/g/files/f$details", """
            {"format":"unregistered/v1","filebase64":"AA==","meta":{"compatibility":"backward"}}
            """);
        await Assert.That(result.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsFalse();
        await Assert.That(result.Metadata.RootElement.GetProperty("formatvalidatedreason").GetString()).IsNotNullOrEmpty();
        await Assert.That(result.Metadata.RootElement.GetProperty("compatibilityvalidated").GetBoolean()).IsFalse();
        var strict = Create(ValidatingModel.Replace("\"validateformat\":true", "\"validateformat\":true,\"strictvalidation\":true", StringComparison.Ordinal));
        await ExpectCode(() => Send(strict, RegistryAction.Replace, "/teams/g/files/f$details", """{"format":"unregistered/v1"}"""), "format_unknown");
        await Assert.That((await Send(strict, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task RequiredValidationReceivesExactBytesAndFailureRollsBack()
    {
        var validator = new ExactValidator();
        var engine = new RegistryEngine(new()
        {
            RegistryId = "validation",
            PublicRoot = new Uri("https://registry.example"),
            Model = RegistryModel.Compile(RegistryJson.Parse(ValidatingModel)),
            ResourceValidator = validator,
            AllowAnonymousReads = true
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        var valid = await Send(engine, RegistryAction.Replace, "/teams/g/files/f$details", """{"format":"test/v1","filebase64":"/wA="}""");
        await Assert.That(valid.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await ExpectCode(() => Send(engine, RegistryAction.Post, "/teams/g/files/f$details", """{"format":"test/v1","filebase64":"AA=="}"""), "format_violation");
        await Assert.That((await Send(engine, RegistryAction.Read, "/teams/g/files/f/meta")).Metadata!.RootElement.GetProperty("defaultversionid").GetString()).IsEqualTo("1");
        await Assert.That(validator.Calls).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExternalDocumentsRemainUncheckedAndStrictFailuresPreserveLocalValidation(bool strict)
    {
        var fetches = 0;
        var policy = new ReferencePolicy();
        var model = ValidatingModel.Replace("\"validateformat\":true", "\"validateformat\":true,\"strictvalidation\":" +
            (strict ? "true" : "false"), StringComparison.Ordinal);
        var engine = new RegistryEngine(new()
        {
            RegistryId = "external-validation",
            PublicRoot = new Uri("https://registry.example"),
            Model = RegistryModel.Compile(RegistryJson.Parse(model)),
            ResourceValidator = new BuiltInRegistryResourceValidator(new()
            {
                ResolveReference = (_, _) =>
                {
                    fetches++;
                    throw new InvalidOperationException("An external Document must not be fetched by schema validation.");
                }
            }),
            DocumentReferencePolicy = policy
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        const string external = """
            {"versionid":"v1","format":"JsonSchema/draft-07","fileurl":"https://documents.example/schema",
             "meta":{"compatibility":"full"}}
            """;
        if (strict)
        {
            await ExpectCode(() => Send(engine, RegistryAction.Replace, "/teams/g/files/external$details", external), "format_external");
            await Assert.That((await Send(engine, RegistryAction.Read, "/")).Metadata!.RootElement.GetProperty("teamscount").GetInt32()).IsEqualTo(0);
        }
        else
        {
            await Send(engine, RegistryAction.Replace, "/teams/g/files/external$details", external);
            await AssertExternalAsync("/teams/g/files/external");
        }
        await Send(engine, RegistryAction.Replace, "/teams/g/files/local$details", """
            {"versionid":"v1","format":"JsonSchema/draft-07","file":{"type":"string"},"meta":{"compatibility":"full"}}
            """);
        var local = await Send(engine, RegistryAction.Read, "/teams/g/files/local$details");
        await Assert.That(local.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await Assert.That(local.Metadata.RootElement.GetProperty("compatibilityvalidated").GetBoolean()).IsTrue();
        if (strict)
        {
            await ExpectCode(() => Send(engine, RegistryAction.Patch, "/teams/g/files/local$details",
                """{"fileurl":"https://documents.example/schema"}"""), "format_external");
        }
        else
        {
            await Send(engine, RegistryAction.Patch, "/teams/g/files/local$details", """{"fileurl":"https://documents.example/schema"}""");
            await AssertExternalAsync("/teams/g/files/local");
            await Send(engine, RegistryAction.Patch, "/teams/g/files/local$details", """{"file":{"type":"string"}}""");
        }
        var retained = await Send(engine, RegistryAction.Read, "/teams/g/files/local$details");
        await Assert.That(retained.Metadata!.RootElement.GetProperty("formatvalidated").GetBoolean()).IsTrue();
        await Assert.That(retained.Metadata.RootElement.GetProperty("compatibilityvalidated").GetBoolean()).IsTrue();
        foreach (var field in new[] { "fileurl", "formatvalidatedreason", "compatibilityvalidatedreason" })
        {
            await Assert.That(retained.Metadata.RootElement.TryGetProperty(field, out _)).IsFalse();
        }
        var stored = await Send(engine, RegistryAction.Read, "/teams/g/files/local");
        using var bytes = stored.Document!.OpenRead();
        using var reader = new StreamReader(bytes);
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("""{"type":"string"}""");
        await Assert.That(fetches).IsEqualTo(0);

        async Task AssertExternalAsync(string path)
        {
            var details = await Send(engine, RegistryAction.Read, path + "$details");
            await Assert.That(details.Metadata!.RootElement.GetProperty("fileurl").GetString()).IsEqualTo("https://documents.example/schema");
            await Assert.That(details.Metadata.RootElement.GetProperty("format").GetString()).IsEqualTo("JsonSchema/draft-07");
            foreach (var aspect in new[] { "format", "compatibility" })
            {
                await Assert.That(details.Metadata.RootElement.GetProperty(aspect + "validated").GetBoolean()).IsFalse();
                await Assert.That(details.Metadata.RootElement.GetProperty(aspect + "validatedreason").GetString()).IsNotNullOrEmpty();
            }
            var redirect = await Send(engine, RegistryAction.Read, path);
            await Assert.That(redirect.Kind).IsEqualTo(RegistryResultKind.SeeOther);
            await Assert.That(redirect.Location!.AbsoluteUri).IsEqualTo("https://documents.example/schema");
            await Assert.That(redirect.Document).IsNull();
        }
    }

    [Test]
    public async Task ExternalReferencesRequireExplicitPolicyAndNeverFetchAutomatically()
    {
        var closed = Create();
        await ExpectCode(() => Send(closed, RegistryAction.Replace, "/teams/g/files/f$details",
            """{"fileurl":"https://documents.example/file"}"""), "document_reference_policy_required");
        var policy = new ReferencePolicy();
        var engine = new RegistryEngine(new()
        {
            RegistryId = "references",
            PublicRoot = new Uri("https://registry.example"),
            Model = RegistryModel.Compile(RegistryJson.Parse(Model)),
            DocumentReferencePolicy = policy,
            AllowAnonymousReads = true
        }, new InMemoryRegistryPersistence(), new PermitPolicy());
        await Send(engine, RegistryAction.Replace, "/teams/g/files/f$details", """{"fileurl":"https://documents.example/file"}""");
        var redirect = await Send(engine, RegistryAction.Read, "/teams/g/files/f");
        await Assert.That(redirect.Kind).IsEqualTo(RegistryResultKind.SeeOther);
        await Assert.That(redirect.Location!.AbsoluteUri).IsEqualTo("https://documents.example/file");
        await Assert.That(redirect.Document).IsNull();
        await Assert.That(policy.Calls).IsEqualTo(2);
    }

    private sealed class ExactValidator : IRegistryResourceValidator
    {
        internal int Calls { get; private set; }
        public IReadOnlyList<string> Formats => ["test/v1"];
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Compatibilities => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        public ValueTask<IReadOnlyDictionary<string, RegistryVersionValidation>> ValidateAsync(RegistryResourceValidationContext context,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var results = new Dictionary<string, RegistryVersionValidation>(StringComparer.Ordinal);
            foreach (var version in context.Versions)
            {
                using var bytes = version.Document.OpenRead();
                var valid = bytes.Length == 2 && bytes.ReadByte() == 255 && bytes.ReadByte() == 0;
                results[version.VersionId] = new(valid ? RegistryValidationStatus.Valid : RegistryValidationStatus.Invalid,
                    RegistryValidationStatus.NotRequested);
            }

            return ValueTask.FromResult<IReadOnlyDictionary<string, RegistryVersionValidation>>(results);
        }
    }

    internal sealed class ReferencePolicy : IRegistryDocumentReferencePolicy
    {
        internal int Calls { get; private set; }
        public ValueTask AuthorizeAsync(ClaimsPrincipal caller, Uri reference, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (reference.Host != "documents.example")
            {
                throw new RegistryException(new("forbidden", "", "The Document authority is not allowed."));
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class PausedValidator : IRegistryResourceValidator
    {
        internal int Calls { get; private set; }
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<string> Formats => ["slow"];
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Compatibilities => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        public async ValueTask<IReadOnlyDictionary<string, RegistryVersionValidation>> ValidateAsync(RegistryResourceValidationContext context,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Entered.TrySetResult();
            await Release.Task;
            var result = context.Versions.ToDictionary(static version => version.VersionId, static _ =>
                new RegistryVersionValidation(RegistryValidationStatus.Valid, RegistryValidationStatus.NotRequested), StringComparer.Ordinal);
            Completed.TrySetResult();
            return result;
        }
    }

    private sealed class TargetValidator : IRegistryMetadataObligationValidator
    {
        internal int Calls { get; private set; }
        public ValueTask ValidateAsync(RegistryMetadataObligationContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (context.Caller.Identity?.IsAuthenticated != true || context.Obligations.Single().Kind != "target" ||
                context.Path.EscapedPath != "/" ||
                context.Metadata.RootElement.GetProperty("link").GetString() != "/teams/g/notes/n")
            {
                throw new RegistryException(new("forbidden", "/", "The authorized target context does not match."));
            }

            return ValueTask.CompletedTask;
        }
    }
}
