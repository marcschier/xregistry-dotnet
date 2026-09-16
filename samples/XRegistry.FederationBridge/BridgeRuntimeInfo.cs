// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace XRegistry.Samples.Bridge;

public static class BridgeRuntimeInfo
{
    public static string Json()
    {
        var dynamicSupported = RuntimeFeature.IsDynamicCodeSupported;
        var dynamicCompiled = RuntimeFeature.IsDynamicCodeCompiled;
        var methods = JitInfo.GetCompiledMethodCount();
        var process = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var os = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : "unsupported";
        using var output = new MemoryStream();
        using (var json = new Utf8JsonWriter(output))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", 1);
            json.WriteString("application", "XRegistry.FederationBridge");
            json.WriteString("framework", "net10.0");
            json.WriteString("runtimeDescription", RuntimeInformation.FrameworkDescription);
            json.WriteString("runtimeIdentifier", platform + "-" + process);
            json.WriteString("processArchitecture", process);
            json.WriteString("osArchitecture", os);
            json.WriteBoolean("dynamicCodeSupported", dynamicSupported);
            json.WriteBoolean("dynamicCodeCompiled", dynamicCompiled);
            json.WriteNumber("jitCompiledMethods", methods);
            json.WriteBoolean("nativeAot", !dynamicSupported && !dynamicCompiled && methods == 0 && process == os);
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
