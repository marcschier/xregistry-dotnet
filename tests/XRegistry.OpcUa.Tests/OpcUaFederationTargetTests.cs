// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using Opc.Ua;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.OpcUa;
using XRegistry.Federation;

namespace XRegistry.OpcUa.Tests;

public class OpcUaFederationTargetTests
{
    [Test]
    public async Task PortableRegistryRootMapsItsNamespaceUriToTheConnectedServer()
    {
        var target = Target("""{"registryroot":"nsu=urn%3Axregistry;s=Registry"}""");
        var namespaceUris = new NamespaceTable([
            "http://opcfoundation.org/UA/",
            "urn:xregistry",
        ]);

        var root = target.ResolveRegistryRoot(namespaceUris);

        await Assert.That(root.NamespaceIndex).IsEqualTo((ushort)1);
        await Assert.That(root.TryGetValue(out string? identifier)).IsTrue();
        await Assert.That(identifier).IsEqualTo("Registry");
        await Assert.That(target.RegistryRoot).IsEqualTo("nsu=urn%3Axregistry;s=Registry");
        await Assert.That(target.Endpoint).IsEqualTo("opc.tcp://registry.example.test:4840");
    }

    [Test]
    public async Task StandardNamespaceMayUseTheBarePortableIdentifier()
    {
        var target = Target("""{"registryroot":"i=85"}""");
        var root = target.ResolveRegistryRoot(new NamespaceTable(["http://opcfoundation.org/UA/"]));

        await Assert.That(root.NamespaceIndex).IsEqualTo((ushort)0);
        await Assert.That(root.TryGetValue(out uint identifier)).IsTrue();
        await Assert.That(identifier).IsEqualTo(85u);
    }

    [Test]
    public async Task MissingNamespaceIsNotSubstitutedWithNamespaceZero()
    {
        var target = Target("""{"registryroot":"nsu=urn%3Amissing;s=Registry"}""");

        await Check.Error(() => target.ResolveRegistryRoot(
            new NamespaceTable(["http://opcfoundation.org/UA/"])), FederationErrorCode.NotFound);
    }

    [Test]
    [Arguments("""{"registryroot":"ns=2;s=Registry"}""")]
    [Arguments("""{"registryroot":"nsu=urn%3Axregistry;ns=2;s=Registry"}""")]
    [Arguments("""{"registryroot":"svr=1;nsu=urn%3Axregistry;s=Registry"}""")]
    [Arguments("""{"registryroot":"svu=urn%3Aserver;nsu=urn%3Axregistry;s=Registry"}""")]
    [Arguments("""{"registryroot":"i=0"}""")]
    public async Task RegistryRootMustUseNonNullPortableNodeIdForm(string parameters)
    {
        await Check.Error(() => Target(parameters), FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task OpcUaParametersAreClosedAndRequireRegistryRoot()
    {
        await Check.Error(() => Target("""{"applicationuri":"urn:example:server"}"""),
            FederationErrorCode.InvalidPackage);
        await Check.Error(() => Target(
            """{"registryroot":"i=85","future":"value"}"""), FederationErrorCode.UnsupportedOperation);
        await Check.Error(() => Target(
            """{"registryroot":"i=85","transportprofileuri":"not a URI"}"""),
            FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task NativeEndpointMustNotEmbedCredentialsOrHttpQueryState()
    {
        await Check.Error(() => Description("******registry.example.test:4840")
            .Select(new(["opcua"])), FederationErrorCode.InvalidPackage);
        await Check.Error(() => Description("opc.tcp://registry.example.test:4840?x=1")
            .Select(new(["opcua"])), FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task SelectEndpointRequiresExactAdvertisedOptionalIdentities()
    {
        var target = Target(
            """{"registryroot":"i=85","applicationuri":"urn:example:registry","transportprofileuri":"urn:example:transport"}""");
        var candidate = Endpoint(
            "opc.tcp://registry.example.test:4840",
            "urn:example:registry",
            "urn:example:transport");

        var selected = target.SelectEndpoint([candidate], _ => true);

        await Assert.That(ReferenceEquals(selected, candidate)).IsTrue();
        await Assert.That(selected.EndpointUrl).IsEqualTo("opc.tcp://registry.example.test:4840");
        await Assert.That(selected.Server.ApplicationUri).IsEqualTo("urn:example:registry");
        await Assert.That(selected.TransportProfileUri).IsEqualTo("urn:example:transport");
    }

    [Test]
    public async Task SelectEndpointRejectsApplicationUriMismatch()
    {
        var target = Target(
            """{"registryroot":"i=85","applicationuri":"urn:example:registry","transportprofileuri":"urn:example:transport"}""");
        var candidate = Endpoint(
            "opc.tcp://registry.example.test:4840",
            "urn:other:registry",
            "urn:example:transport");

        await Check.Error(() => target.SelectEndpoint([candidate], _ => true), FederationErrorCode.NotFound);
    }

    [Test]
    public async Task SelectEndpointRejectsTransportProfileUriMismatch()
    {
        var target = Target(
            """{"registryroot":"i=85","applicationuri":"urn:example:registry","transportprofileuri":"urn:example:transport"}""");
        var candidate = Endpoint(
            "opc.tcp://registry.example.test:4840",
            "urn:example:registry",
            "urn:other:transport");

        await Check.Error(() => target.SelectEndpoint([candidate], _ => true), FederationErrorCode.NotFound);
    }

    [Test]
    public async Task SelectEndpointRejectsCandidatesOutsideCallerPolicy()
    {
        var target = Target("""{"registryroot":"i=85"}""");
        var candidate = Endpoint("opc.tcp://registry.example.test:4840");
        var policyWasApplied = false;

        await Check.Error(() => target.SelectEndpoint([candidate], endpoint =>
        {
            policyWasApplied = true;
            return false;
        }), FederationErrorCode.PolicyDenied);

        await Assert.That(policyWasApplied).IsTrue();
    }

    [Test]
    public async Task SelectEndpointReportsNoCandidates()
    {
        var target = Target("""{"registryroot":"i=85"}""");

        await Check.Error(() => target.SelectEndpoint([], _ => true), FederationErrorCode.NotFound);
    }

    [Test]
    public async Task SelectEndpointReportsAmbiguousPermittedCandidates()
    {
        var target = Target("""{"registryroot":"i=85"}""");
        var first = Endpoint("opc.tcp://registry.example.test:4840");
        var second = Endpoint("opc.tcp://registry.example.test:4840");

        await Check.Error(() => target.SelectEndpoint([first, second], _ => true), FederationErrorCode.Ambiguous);
    }

    [Test]
    public async Task SelectEndpointRequiresTheAdvertisedEndpointUrl()
    {
        var target = Target("""{"registryroot":"i=85"}""");
        var otherEndpoint = Endpoint("opc.tcp://other.example.test:4840");

        await Check.Error(() => target.SelectEndpoint([otherEndpoint], _ => true), FederationErrorCode.NotFound);
    }

    internal static OpcUaFederationTarget Target(string parameters) =>
        OpcUaFederationTarget.FromAdvertisement(Selected(parameters));

    private static CatalogAdvertisement Selected(string parameters) =>
        Description("opc.tcp://registry.example.test:4840", parameters).Select(new(["opcua"]));

    private static CatalogDescription Description(
        string endpoint,
        string parameters = """{"registryroot":"i=85"}""") =>
        CatalogDescription.Parse(Encoding.UTF8.GetBytes($$"""
            {"federationprofiles":[{"name":"opcua","endpoint":"{{endpoint}}","parameters":{{parameters}}}]}
            """));

    private static EndpointDescription Endpoint(
        string endpointUrl,
        string applicationUri = "urn:example:registry",
        string transportProfileUri = "urn:example:transport") =>
        new()
        {
            EndpointUrl = endpointUrl,
            Server = new ApplicationDescription { ApplicationUri = applicationUri },
            TransportProfileUri = transportProfileUri,
        };
}

internal static class Check
{
    internal static async Task<FederationException> Error(Action action, FederationErrorCode code)
    {
        var exception = await Assert.That(action).Throws<FederationException>()
            ?? throw new InvalidOperationException("Expected a federation failure.");
        await Assert.That(exception.Code).IsEqualTo(code);
        return exception;
    }
}
