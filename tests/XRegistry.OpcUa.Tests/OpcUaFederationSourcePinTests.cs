// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json.Nodes;
using Opc.Ua;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Bindings.OpcUa;
using XRegistry.Federation;

namespace XRegistry.OpcUa.Tests;

public class OpcUaFederationSourcePinTests
{
    private const string Xid = "/teams/team-a/schemas/schema-a";

    [Test]
    [Arguments(false, false, "Numeric")]
    [Arguments(false, true, "String")]
    [Arguments(true, false, "Guid")]
    [Arguments(true, true, "Opaque")]
    [Arguments(true, false, "Numeric")]
    [Arguments(true, true, "String")]
    [Arguments(false, false, "Guid")]
    [Arguments(false, true, "Opaque")]
    public async Task SourcePinRoundTripRetainsOriginXidRoleAndAllNodeIdentifierKinds(
        bool applicationOrigin, bool exact, string kind)
    {
        var type = Enum.Parse<IdType>(kind);
        object identifier = type switch
        {
            IdType.Numeric => uint.MaxValue,
            IdType.String => "Exact%2f;Identity",
            IdType.Guid => Guid.Parse("9b81dd7d-333a-4746-ad52-403a07e353df"),
            _ => new byte[] { 0, 128, 255 }
        };
        var node = new OpcUaExternalNodeId("urn:App%2f", "urn:Namespace%2f", type, identifier);
        var origin = applicationOrigin ? OpcUaRegistryOrigin.Application(node) :
            OpcUaRegistryOrigin.Stable("urn:Origin%2f");
        var pin = new OpcUaFederationSourcePin(origin, Xid + (exact ? "/versions/V1" : ""),
            exact ? OpcUaExternalTargetRole.ExactVersion : OpcUaExternalTargetRole.LogicalResource, node);

        var bytes = pin.Serialize();
        var restored = OpcUaFederationSourcePin.Parse(bytes);
        pin.ValidateReconnect(restored);

        await Assert.That(restored.HasSameSource(pin)).IsTrue();
        await Assert.That(restored.Xid).IsEqualTo(pin.Xid);
        await Assert.That(restored.Role).IsEqualTo(pin.Role);
        await Assert.That(restored.RemoteNode.IdentifierType).IsEqualTo(type);
        await Assert.That(restored.RemoteNode.ServerApplicationUri).IsEqualTo("urn:App%2f");
        await Assert.That(restored.RemoteNode.NamespaceUri).IsEqualTo("urn:Namespace%2f");
        await Assert.That(restored.Origin.Equals(origin)).IsTrue();
        await Assert.That(restored.Serialize()).IsEquivalentTo(bytes);
        var targetTable = new NamespaceTable(["http://opcfoundation.org/UA/", "urn:other", "urn:Namespace%2f"]);
        await Assert.That(restored.RemoteNode.MapToLocalNodeId("urn:App%2f", targetTable).NamespaceIndex)
            .IsEqualTo((ushort)2);
    }

    [Test]
    [Arguments("origin")]
    [Arguments("origin-form")]
    [Arguments("origin-root")]
    [Arguments("xid")]
    [Arguments("role")]
    [Arguments("node")]
    [Arguments("namespace")]
    [Arguments("application")]
    public async Task SourcePinRejectsChangedSourceOrRemoteIdentity(string change)
    {
        var origin = change == "origin-root"
            ? OpcUaRegistryOrigin.Application(new("urn:app", "urn:ns", IdType.String, "Root"))
            : OpcUaRegistryOrigin.Stable("urn:origin");
        var node = new OpcUaExternalNodeId("urn:app", "urn:ns", IdType.String, "Resource");
        var pin = new OpcUaFederationSourcePin(origin, Xid, OpcUaExternalTargetRole.LogicalResource, node);
        var changedOrigin = change switch
        {
            "origin" => OpcUaRegistryOrigin.Stable("urn:Origin"),
            "origin-form" => OpcUaRegistryOrigin.Application(new("urn:app", "urn:ns", IdType.String, "Root")),
            "origin-root" => OpcUaRegistryOrigin.Application(new("urn:app", "urn:ns", IdType.String, "OtherRoot")),
            _ => origin
        };
        var candidate = new OpcUaFederationSourcePin(changedOrigin,
            change == "xid" ? "/teams/team-b/schemas/schema-a" :
                change == "role" ? Xid + "/versions/v1" : Xid,
            change == "role" ? OpcUaExternalTargetRole.ExactVersion : OpcUaExternalTargetRole.LogicalResource,
            new(change == "application" ? "urn:other" : "urn:app", change == "namespace" ? "urn:other" : "urn:ns",
                IdType.String, change == "node" ? "OtherResource" : "Resource"));

        var error = await Assert.ThrowsAsync<FederationException>(() =>
        {
            pin.ValidateReconnect(candidate);
            return Task.CompletedTask;
        });

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.PolicyDenied);
        await Assert.That(pin.HasSameSource(candidate))
            .IsEqualTo(change is "node" or "namespace" or "application");
    }

    [Test]
    public async Task SourcePinUsesDecodedConcreteXidWithoutFoldingCase()
    {
        var origin = OpcUaRegistryOrigin.Stable("urn:origin");
        var node = new OpcUaExternalNodeId("urn:app", "urn:ns", IdType.String, "Resource");
        var pin = new OpcUaFederationSourcePin(origin, Xid, OpcUaExternalTargetRole.LogicalResource, node);
        var equivalent = new OpcUaFederationSourcePin(origin, Xid.Replace("team-a", "%74eam-a", StringComparison.Ordinal),
            pin.Role, node);
        pin.ValidateReconnect(equivalent);
        var other = new OpcUaFederationSourcePin(origin, Xid.Replace("team-a", "Team-a", StringComparison.Ordinal),
            pin.Role, node);

        await Assert.That(pin.HasSameSource(equivalent)).IsTrue();
        await Assert.That(pin.HasSameSource(other)).IsFalse();
    }

    [Test]
    [Arguments(65535)]
    [Arguments(65536)]
    [Arguments(65537)]
    public async Task SourcePinSerializationEnforcesExactByteBudget(int length)
    {
        OpcUaFederationSourcePin Pin(string uri) => new(OpcUaRegistryOrigin.Stable(uri), Xid,
            OpcUaExternalTargetRole.LogicalResource, new("urn:app", "urn:ns", IdType.Numeric, 42u));
        var overhead = Pin("a").Serialize().Length - 1;
        var pin = Pin(new string('a', length - overhead));
        if (length <= 65536)
        {
            var bytes = pin.Serialize();
            await Assert.That(bytes.Length).IsEqualTo(length);
            pin.ValidateReconnect(OpcUaFederationSourcePin.Parse(bytes));
        }
        else
        {
            var error = await Assert.ThrowsAsync<FederationException>(() =>
            {
                _ = pin.Serialize();
                return Task.CompletedTask;
            });
            await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.LimitExceeded);
        }
    }

    [Test]
    [Arguments("truncated")]
    [Arguments("duplicate")]
    [Arguments("over-budget")]
    public async Task SourcePinParserRejectsInvalidJsonAndRetainsCoreDiagnostic(string state)
    {
        var text = state switch
        {
            "truncated" => "{",
            "duplicate" => """{"format":1,"format":1}""",
            _ => new string(' ', 65537)
        };
        var error = await Assert.ThrowsAsync<FederationException>(() =>
        {
            _ = OpcUaFederationSourcePin.Parse(Encoding.UTF8.GetBytes(text));
            return Task.CompletedTask;
        });

        await Assert.That(error!.Code).IsEqualTo(state == "over-budget" ? FederationErrorCode.LimitExceeded :
            FederationErrorCode.InvalidPackage);
        await Assert.That(error.InnerException is RegistryException).IsTrue();
        await Assert.That(error.Diagnostic).IsNotNull();
    }

    [Test]
    [Arguments("xid")]
    [Arguments("role")]
    [Arguments("application")]
    public async Task SourcePinConstructorRejectsInvalidIdentityCombination(string state)
    {
        var origin = OpcUaRegistryOrigin.Application(new("urn:app", "urn:ns", IdType.String, "Root"));
        var error = await Assert.ThrowsAsync<FederationException>(() =>
        {
            _ = new OpcUaFederationSourcePin(origin, state == "xid" ? "/teams/team-a" : Xid,
                state == "role" ? OpcUaExternalTargetRole.ExactVersion : OpcUaExternalTargetRole.LogicalResource,
                new(state == "application" ? "urn:other" : "urn:app", "urn:ns", IdType.String, "Resource"));
            return Task.CompletedTask;
        });

        await Assert.That(error!.Code).IsEqualTo(state == "xid" ? FederationErrorCode.UnsupportedOperation :
            FederationErrorCode.InvalidPackage);
    }

    [Test]
    public async Task SourcePinConstructorRejectsUnknownRole()
    {
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
        {
            _ = new OpcUaFederationSourcePin(OpcUaRegistryOrigin.Stable("urn:origin"), Xid,
                (OpcUaExternalTargetRole)99, new("urn:app", "urn:ns", IdType.String, "Resource"));
            return Task.CompletedTask;
        });

        await Assert.That(error!.ParamName).IsEqualTo("role");
    }

    [Test]
    [Arguments("format")]
    [Arguments("unknown")]
    [Arguments("missing")]
    [Arguments("mixed-origin")]
    [Arguments("absent-origin")]
    [Arguments("role")]
    [Arguments("kind")]
    [Arguments("identifier")]
    [Arguments("application")]
    [Arguments("guid")]
    [Arguments("opaque")]
    [Arguments("negative")]
    [Arguments("overflow")]
    public async Task SourcePinParserRejectsMalformedStoredIdentity(string state)
    {
        var pin = new OpcUaFederationSourcePin(OpcUaRegistryOrigin.Stable("urn:origin"), Xid,
            OpcUaExternalTargetRole.LogicalResource, new("urn:app", "urn:ns", IdType.Numeric, 42u));
        var json = JsonNode.Parse(pin.Serialize())!.AsObject();
        switch (state)
        {
            case "format": json["format"] = 2; break;
            case "unknown": json["endpoint"] = "opc.tcp://untrusted"; break;
            case "missing": json.Remove("xid"); break;
            case "mixed-origin": json["originRoot"] = json["remoteNode"]!.DeepClone(); break;
            case "absent-origin": json["originUri"] = null; break;
            case "role": json["role"] = "default"; break;
            case "kind": json["remoteNode"]!["kind"] = "Unknown"; break;
            case "identifier": json["remoteNode"]!["identifier"] = "42"; break;
            case "application": json["remoteNode"]!["application"] = ""; break;
            case "guid":
                json["remoteNode"]!["kind"] = "Guid";
                json["remoteNode"]!["identifier"] = "not-a-guid";
                break;
            case "opaque":
                json["remoteNode"]!["kind"] = "Opaque";
                json["remoteNode"]!["identifier"] = "*";
                break;
            case "negative": json["remoteNode"]!["identifier"] = -1; break;
            case "overflow": json["remoteNode"]!["identifier"] = (long)uint.MaxValue + 1; break;
        }

        var error = await Assert.ThrowsAsync<FederationException>(() =>
        {
            _ = OpcUaFederationSourcePin.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));
            return Task.CompletedTask;
        });

        await Assert.That(error!.Code).IsEqualTo(FederationErrorCode.InvalidPackage);
    }
}
