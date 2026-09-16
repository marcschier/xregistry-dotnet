// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using XRegistry.Models;

if (RuntimeFeature.IsDynamicCodeSupported || RuntimeFeature.IsDynamicCodeCompiled ||
    JitInfo.GetCompiledMethodCount() != 0 || RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture)
{
    Console.Error.WriteLine("The package consumer must be a native executable on the selected native architecture.");
    return 2;
}

(RegistryModelKind Kind, string Hash)[] models =
[
    (RegistryModelKind.Core, "00f9342b7ef4f65fcf3859632c909dacd3cb7f6d8b8c019e03ea1936cc307e6e"),
    (RegistryModelKind.Endpoint, "9533a1720ec2da0b74b8f0bdb9df32057fc59b257aabb46f6aaae6535fc814e7"),
    (RegistryModelKind.Message, "0b72ea67304b040f7057ee0a924999a09d0a41605eff47ab196199278bd6e121"),
    (RegistryModelKind.Schema, "b2e7efd1a89512c6a0a9731c6e868ce1785ec1923d0348527667d8e191e484f7"),
    (RegistryModelKind.CloudEvents, "8000babf16b868ff97144b534b0639a68d408285c9ced823e5d5c3414eff08f7"),
    (RegistryModelKind.Registry, "afec5aeb7c2a3d435bdbdf7244f3e97bdb5ddb5c4679b6c418a6f7c643c22210"),
    (RegistryModelKind.OpenUsd, "bd2209ff833d3016809eb699330c5ebf798f610925a583796998427eff7a242d")
];

foreach (var model in models)
{
    using var stream = BuiltInRegistryModels.OpenSource(model.Kind);
    var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    if (!StringComparer.Ordinal.Equals(hash, model.Hash))
    {
        throw new InvalidOperationException($"Packaged model bytes changed: {model.Kind}.");
    }
    var compiled = BuiltInRegistryModels.Compile(model.Kind);
    if (compiled.Attributes["specversion"].DefaultValue.GetString() != "1.0-rc4")
    {
        throw new InvalidOperationException($"Compiled model has the wrong core specification version: {model.Kind}.");
    }
}

using var core = BuiltInRegistryModels.LoadSource(RegistryModelKind.Core);
if (core.RootElement.GetProperty("attributes").GetProperty("specversion")
    .GetProperty("default").GetString() != "1.0-rc4")
{
    throw new InvalidOperationException("The packaged core model has the wrong specification version.");
}

using var catalog = BuiltInRegistryModels.LoadSource(RegistryModelKind.Registry);
if (catalog.RootElement.GetProperty("groups").GetProperty("categories")
    .GetProperty("resources").GetProperty("registries").GetProperty("hasdocument").GetBoolean())
{
    throw new InvalidOperationException("Packaged catalog registries must be documentless.");
}

Console.WriteLine(
    $"Native package-model consumer passed: {RuntimeInformation.FrameworkDescription}; "
    + $"{RuntimeInformation.ProcessArchitecture}; 7 exact model sources. "
    + "This is not full xRegistry conformance.");
return 0;
