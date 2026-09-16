// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using TUnit.Assertions;
using TUnit.Core;
using XRegistry.Client;

namespace XRegistry.Http.Tests;

public class RegistryHttpConnectionPolicyTests
{
    [Test]
    [Arguments("8.8.8.8")]
    [Arguments("1.1.1.1")]
    [Arguments("2606:4700:4700::1111")]
    public async Task PublicAddressesAreAllowedForAnExplicitHttpsOrigin(string address)
    {
        var policy = new RegistryHttpConnectionPolicy(new Uri("https://registry.example/xreg"));

        await Assert.That(policy.IsAddressAllowed(IPAddress.Parse(address))).IsTrue();
    }

    [Test]
    [Arguments("0.0.0.0")]
    [Arguments("10.0.0.1")]
    [Arguments("100.64.0.1")]
    [Arguments("127.0.0.1")]
    [Arguments("169.254.169.254")]
    [Arguments("172.16.0.1")]
    [Arguments("192.168.1.1")]
    [Arguments("192.0.2.1")]
    [Arguments("198.18.0.1")]
    [Arguments("198.51.100.1")]
    [Arguments("203.0.113.1")]
    [Arguments("224.0.0.1")]
    [Arguments("255.255.255.255")]
    [Arguments("::")]
    [Arguments("::1")]
    [Arguments("::ffff:127.0.0.1")]
    [Arguments("fe80::1")]
    [Arguments("fc00::1")]
    [Arguments("ff02::1")]
    [Arguments("2001:db8::1")]
    [Arguments("2002:7f00:1::")]
    public async Task DefaultPolicyRejectsSpecialPrivateAndTranslatedDestinations(string address)
    {
        var policy = new RegistryHttpConnectionPolicy(new Uri("https://registry.example/xreg"));

        await Assert.That(policy.IsAddressAllowed(IPAddress.Parse(address))).IsFalse();
    }

    [Test]
    public async Task LoopbackHttpRequiresExplicitOptInAndDoesNotPermitPrivateNetworks()
    {
        await Assert.That(() => new RegistryHttpConnectionPolicy(new Uri("http://127.0.0.1/xreg")))
            .Throws<ArgumentException>();
        var policy = new RegistryHttpConnectionPolicy(
            new Uri("http://127.0.0.1/xreg"), allowLoopbackHttp: true);

        await Assert.That(policy.IsAddressAllowed(IPAddress.Loopback)).IsTrue();
        await Assert.That(policy.IsAddressAllowed(IPAddress.IPv6Loopback)).IsTrue();
        await Assert.That(policy.IsAddressAllowed(IPAddress.Parse("10.0.0.1"))).IsFalse();
        await Assert.That(policy.IsAddressAllowed(IPAddress.Parse("8.8.8.8"))).IsFalse();
    }

    [Test]
    public async Task EnterprisePrivateAddressOptInIsScopedAndStillRejectsMetadata()
    {
        var policy = new RegistryHttpConnectionPolicy(
            new Uri("https://enterprise.example/xreg"), allowPrivateOrigin: true);

        await Assert.That(policy.IsAddressAllowed(IPAddress.Parse("10.20.30.40"))).IsTrue();
        await Assert.That(policy.IsAddressAllowed(IPAddress.Parse("fc00::1"))).IsTrue();
        await Assert.That(policy.IsAddressAllowed(IPAddress.Parse("169.254.169.254"))).IsFalse();
        await Assert.That(policy.IsAddressAllowed(IPAddress.Loopback)).IsFalse();
        await Assert.That(policy.IsAddressAllowed(IPAddress.Parse("224.0.0.1"))).IsFalse();
    }

    [Test]
    [Arguments("http://example.com/xreg")]
    [Arguments("https://user:password@example.com/xreg")]
    [Arguments("https://example.com/xreg?token=secret")]
    [Arguments("https://example.com/xreg#fragment")]
    [Arguments("file:///tmp/xreg")]
    public async Task UnsafeOriginShapesAreRejected(string address)
    {
        await Assert.That(() => new RegistryHttpConnectionPolicy(new Uri(address), allowLoopbackHttp: true))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task RequestUrisMustRemainOnTheConfiguredOrigin()
    {
        var policy = new RegistryHttpConnectionPolicy(new Uri("https://registry.example:8443/xreg"));

        await Assert.That(policy.IsOriginAllowed(new Uri("https://REGISTRY.EXAMPLE:8443/xreg/model"))).IsTrue();
        await Assert.That(policy.IsOriginAllowed(new Uri("https://registry.example/xreg/model"))).IsFalse();
        await Assert.That(policy.IsOriginAllowed(new Uri("http://registry.example:8443/xreg/model"))).IsFalse();
        await Assert.That(policy.IsOriginAllowed(new Uri("https://other.example:8443/xreg/model"))).IsFalse();
        await Assert.That(policy.IsOriginAllowed(new Uri("https://user@registry.example:8443/xreg/model"))).IsFalse();
    }
}
