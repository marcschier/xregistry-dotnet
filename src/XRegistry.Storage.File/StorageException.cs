// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;

namespace XRegistry.Storage.File;

/// <summary>A storage failure that must not be translated into empty or successful state.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Every storage exception requires an explicit machine-readable failure classification.")]
public sealed class StorageException : IOException
{
    /// <summary>Creates a classified storage error, optionally retaining its underlying cause.</summary>
    public StorageException(StorageFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    /// <summary>Gets the failure classification.</summary>
    public StorageFailure Failure { get; }
}
