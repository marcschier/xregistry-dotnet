// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Core.Tests;

internal static class TestErrors
{
    internal static RegistryDiagnostic Capture(Action action)
    {
        try
        {
            action();
        }
        catch (RegistryException exception)
        {
            return exception.Diagnostic;
        }

        throw new InvalidOperationException("The operation unexpectedly accepted invalid input.");
    }
}
