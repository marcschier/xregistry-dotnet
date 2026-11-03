// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Models;

internal static class HttpProtocolSemantics
{
    internal static bool IsHttp(string? protocol) => protocol?.ToUpperInvariant() is
        "HTTP" or "HTTP/1.1" or "HTTP/2" or "HTTP/3";
}
