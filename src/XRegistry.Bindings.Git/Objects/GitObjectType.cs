namespace XRegistry.Bindings.Git.Objects;

/// <summary>The canonical, non-delta Git object types.</summary>
public enum GitObjectType
{
    /// <summary>A commit record referencing a tree and optional parents.</summary>
    Commit = 1,
    /// <summary>A binary tree containing named object references.</summary>
    Tree = 2,
    /// <summary>Exact uninterpreted data bytes.</summary>
    Blob = 3,
    /// <summary>An annotated tag record.</summary>
    Tag = 4,
}

/// <summary>The repository's negotiated Git object format; formats cannot be mixed implicitly.</summary>
public enum GitHashAlgorithm
{
    /// <summary>Legacy 20-byte SHA-1 compatibility, without collision-detecting hardening.</summary>
    Sha1 = 1,
    /// <summary>The 32-byte SHA-256 Git object format.</summary>
    Sha256 = 2,
}
