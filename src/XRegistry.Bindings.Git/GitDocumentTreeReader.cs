// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using XRegistry.Bindings.Git.Objects;
using XRegistry.Federation;

namespace XRegistry.Bindings.Git;

/// <summary>Adapts a verified commit snapshot to the shared directory-mapping reader without a checkout.</summary>
public sealed class GitDocumentTreeReader : IDocumentTreeReader
{
    private readonly GitSnapshot _snapshot;
    private readonly string _root;

    /// <summary>Creates a reader whose repository locator and immutable commit remain separate from mapping paths.</summary>
    public GitDocumentTreeReader(GitSnapshot snapshot, Uri repository, string rootRelativePath = "xregistry",
        string? requestedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(repository);
        if (!repository.IsAbsoluteUri || repository.Scheme != Uri.UriSchemeHttps ||
            repository.UserInfo.Length != 0 || repository.Query.Length != 0 || repository.Fragment.Length != 0)
        {
            throw new ArgumentException("An explicit credential-free HTTPS repository locator is required.", nameof(repository));
        }

        ArgumentNullException.ThrowIfNull(rootRelativePath);
        GitBindingSyntax.ValidateRootPath(rootRelativePath);
        if (requestedRevision is not null) { GitBindingSyntax.ValidateRevision(requestedRevision); }
        _snapshot = snapshot;
        _root = rootRelativePath;
        Context = new NativeRegistryContext("git", repository.AbsoluteUri, snapshot.CommitId.ToString(), isImmutable: true)
        {
            RootPath = rootRelativePath,
            RequestedRevision = requestedRevision ?? snapshot.SelectedId.ToString()
        };
    }

    /// <summary>Gets the fixed repository/commit context; tag names are not retained as mutable revision pins.</summary>
    public NativeRegistryContext Context { get; }

    /// <summary>Returns an owned exact blob stream; ordinary absent paths are null, missing pinned objects remain errors.</summary>
    public ValueTask<Stream?> OpenReadAsync(string rootRelativePath, CancellationToken cancellationToken = default)
    {
        DocumentTreePath.Validate(rootRelativePath);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return ValueTask.FromResult<Stream?>(_snapshot.ReadBlob(
                _root.Length == 0 ? rootRelativePath : _root + "/" + rootRelativePath, cancellationToken).OpenRead());
        }
        catch (GitDataException exception) when (exception.Failure == GitFailure.PathNotFound)
        {
            return ValueTask.FromResult<Stream?>(null);
        }
        catch (GitDataException exception)
        {
            var code = exception.Failure switch
            {
                GitFailure.MissingObject => FederationErrorCode.InconsistentSnapshot,
                GitFailure.PolicyDenied => FederationErrorCode.PolicyDenied,
                GitFailure.UnsupportedIndirection or GitFailure.UnsupportedFormat => FederationErrorCode.UnsupportedOperation,
                GitFailure.LimitExceeded => FederationErrorCode.LimitExceeded,
                GitFailure.IntegrityMismatch => FederationErrorCode.IntegrityError,
                _ => FederationErrorCode.InvalidPackage
            };
            throw new FederationException(code, "The selected Git snapshot cannot provide the requested object.",
                innerException: exception);
        }
    }
}
