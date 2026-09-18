// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Runtime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;

namespace XRegistry.Models.Tests;

/// <summary>
/// Opt-in native-execution proof consumed by <c>eng/specification/native_receipt.py</c>.
/// Reuses the same nativeAot/jitCompiledMethods evidence as the sample executables'
/// <c>runtime-info</c> command. The proof file is written only when
/// <c>XREGISTRY_NATIVE_PROOF_PATH</c> is set; ordinary test runs are unaffected.
/// </summary>
public class NativeExecutionProofTests
{
    [Test]
    public async Task NativeAotStatusIsReportedWhenRequested()
    {
        var compiledMethods = JitInfo.GetCompiledMethodCount();
        var nativeAot = !RuntimeFeature.IsDynamicCodeSupported && !RuntimeFeature.IsDynamicCodeCompiled
            && compiledMethods == 0;
        var path = Environment.GetEnvironmentVariable("XREGISTRY_NATIVE_PROOF_PATH");
        if (!string.IsNullOrEmpty(path))
        {
            await using var stream = File.Create(path);
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            writer.WriteBoolean("nativeAot", nativeAot);
            writer.WriteNumber("jitCompiledMethods", compiledMethods);
            writer.WriteEndObject();
            await writer.FlushAsync();
        }

        await Assert.That(compiledMethods).IsGreaterThanOrEqualTo(0);
    }
}
