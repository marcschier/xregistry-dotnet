// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Federation.Tests;

public class ProducerModelTargetPolicyTests
{
    private const string ModelText = """
        {"groups":{"gs":{"singular":"g","resources":{"rs":{"singular":"r","attributes":{
          "reference":{"type":"url","target":"/gs/rs"}
        }}}}}}
        """;

    [Test]
    public async Task AbsoluteUriTargetValuesDoNotInvokeAProducerTargetPolicy()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(ModelText));
        var source = new ProducerModelDataSource("selected", model);
        source.AddGroup("g");
        source.AddResource("g", "item", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v1"] = """{"reference":"https://outside.invalid/unrelated/path"}"""
        });
        var calls = 0;
        var registration = new FederationSourceRegistration("selected", FederationRepresentation.ApiView,
            (_, _) => ValueTask.FromResult(new FederationSourceLease(source, static () => ValueTask.CompletedTask)),
            validateTarget: (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("An absolute URI has no Core target obligation.");
            });
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"), "view", [registration]);

        var result = await view.ReadAsync(RegistryPath.Parse("/gs/g/rs/item/versions/v1$details"));
        await Assert.That(result.Metadata.GetProperty("reference").GetString()).IsEqualTo("https://outside.invalid/unrelated/path");
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(source.Reads.Any(r => r.Operation == FederationOperation.Document)).IsFalse();
    }

    [Test]
    [Arguments(false, false, FederationErrorCode.UnsupportedOperation, "target_policy_required")]
    [Arguments(true, false, FederationErrorCode.PolicyDenied, "target_policy_denied")]
    public async Task ExternalTargetObligationsRequireAnExplicitPositiveDecision(
        bool configured, bool accepted, FederationErrorCode expected, string diagnostic)
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(ModelText));
        var source = Source(model);
        var registration = new FederationSourceRegistration("selected", FederationRepresentation.ApiView,
            (_, _) => ValueTask.FromResult(new FederationSourceLease(source, static () => ValueTask.CompletedTask)),
            validateTarget: configured ? (_, _) => ValueTask.FromResult(accepted) : null);
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"), "view", [registration]);

        var error = await Assert.That(async () =>
        {
            await view.ReadAsync(RegistryPath.Parse("/gs/g/rs/item/versions/v1$details"));
        }).Throws<FederationException>();
        await Assert.That(error!.Code).IsEqualTo(expected);
        await Assert.That(error.Diagnostic).IsEqualTo(diagnostic);
    }

    [Test]
    public async Task FailedTargetDependencyCannotBeCaughtAndConvertedIntoSuccessfulAdmission()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(ModelText));
        var source = Source(model);
        source.Denied.Add("/gs/g/rs/target");
        var registration = new FederationSourceRegistration("selected", FederationRepresentation.ApiView,
            (_, _) => ValueTask.FromResult(new FederationSourceLease(source, static () => ValueTask.CompletedTask)),
            validateTarget: async (context, _) =>
            {
                try { await context.ReadMetadataAsync(RegistryPath.Parse("/gs/g/rs/target")); }
                catch (FederationException) { return true; }
                return true;
            });
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"), "view", [registration]);

        var error = await Assert.That(async () =>
        {
            await view.ReadAsync(RegistryPath.Parse("/gs/g/rs/item/versions/v1$details"));
        }).Throws<FederationException>();
        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
    }

    [Test]
    public async Task CancellationDuringTargetPolicyNeverReturnsACompletedView()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(ModelText));
        var source = Source(model);
        using var cancellation = new CancellationTokenSource();
        var registration = new FederationSourceRegistration("selected", FederationRepresentation.ApiView,
            (_, _) => ValueTask.FromResult(new FederationSourceLease(source, static () => ValueTask.CompletedTask)),
            validateTarget: async (_, _) =>
            {
                await cancellation.CancelAsync();
                return true;
            });
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"), "view", [registration]);

        await Assert.That(async () =>
        {
            await view.ReadAsync(RegistryPath.Parse("/gs/g/rs/item/versions/v1$details"), cancellation.Token);
        }).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task ExplicitTargetPolicyUsesBoundedSourceMetadataAndRetainsItsDependencies()
    {
        var model = RegistryModel.Compile(RegistryJson.Parse(ModelText));
        var source = new ProducerModelDataSource("selected", model);
        source.AddGroup("g");
        source.AddResource("g", "item", new Dictionary<string, string>(StringComparer.Ordinal) { ["v1"] = """{"reference":"/gs/g/rs/target"}""" });
        source.AddResource("g", "target", new Dictionary<string, string>(StringComparer.Ordinal) { ["v1"] = "{}" });
        ProducerTargetValidationContext? observed = null;
        var registration = new FederationSourceRegistration("selected", FederationRepresentation.ApiView,
            (_, _) => ValueTask.FromResult(new FederationSourceLease(source, static () => ValueTask.CompletedTask, "policy-v1")),
            _ => ValueTask.FromResult("policy-v1"),
            async (context, token) =>
            {
                token.ThrowIfCancellationRequested();
                observed = context;
                context.Budget.ChargeWork();
                var target = await context.ReadMetadataAsync(RegistryPath.Parse("/gs/g/rs/target"));
                return target.GetProperty("rid").GetString() == "target";
            });
        await using var view = new ProducerRegistryView(model, new("https://view.test/registry"), "view", [registration]);

        var result = await view.ReadAsync(RegistryPath.Parse("/gs/g/rs/item/versions/v1$details"));

        await Assert.That(result.Metadata.GetProperty("reference").GetString()).IsEqualTo("/gs/g/rs/target");
        await Assert.That(observed!.SourceName).IsEqualTo("selected");
        await Assert.That(observed.EntityPath.EscapedPath).IsEqualTo("/gs/g/rs/item/versions/v1$details");
        await Assert.That(observed.Obligation.Kind).IsEqualTo("target");
        await Assert.That(observed.Obligation.Path).IsEqualTo("/reference");
        await Assert.That(observed.Metadata.RootElement.GetProperty("reference").GetString()).IsEqualTo("/gs/g/rs/target");
        await Assert.That(result.Dependencies.Any(d => d.Source == "selected" && d.Path.EscapedPath == "/gs/g/rs/target")).IsTrue();
        await Assert.That(result.Origins.Single().CredentialStamp).IsEqualTo("policy-v1");
        await Assert.That(source.Reads.Any(r => r.Operation == FederationOperation.Document)).IsFalse();
        await Assert.That(async () =>
        {
            await observed.ReadMetadataAsync(RegistryPath.Parse("/gs/g/rs/target"));
        }).Throws<InvalidOperationException>();
    }

    private static ProducerModelDataSource Source(RegistryModel model)
    {
        var source = new ProducerModelDataSource("selected", model);
        source.AddGroup("g");
        source.AddResource("g", "item", new Dictionary<string, string>(StringComparer.Ordinal) { ["v1"] = """{"reference":"/gs/g/rs/target"}""" });
        source.AddResource("g", "target", new Dictionary<string, string>(StringComparer.Ordinal) { ["v1"] = "{}" });
        return source;
    }
}
