using XRegistry.Models;

namespace XRegistry.Federation;

/// <summary>Exact locator syntax shared by Git catalog selection and native acquisition.</summary>
public static class GitBindingSyntax
{
    /// <summary>Validates a full ref or complete SHA-1/SHA-256 object ID, never a revision expression.</summary>
    public static void ValidateRevision(string revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (revision.Length > 4096)
        {
            throw new FederationException(FederationErrorCode.LimitExceeded, "The Git revision selector is too long.");
        }

        if (!CatalogBindingSyntax.IsGitRevision(revision))
        {
            throw FederationJson.Invalid("A Git revision must be a complete ref or object ID.");
        }
    }

    /// <summary>Validates the stricter Git root-directory locator; empty selects the repository root.</summary>
    public static void ValidateRootPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        FederationSyntax.StoragePath(path, true);
        if (!CatalogBindingSyntax.IsGitRootPath(path))
        {
            throw new FederationException(FederationErrorCode.InvalidPackage,
                "A Git root locator needs 1-64 character portable ASCII directory components.");
        }
    }
}
