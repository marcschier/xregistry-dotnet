using System.Text.Json;

namespace XRegistry.Models;

public static partial class RegistryDomainRules
{
    /// <summary>Checks common and known binding declaration syntax without selection, access, or interpretation of unknown parameters.</summary>
    public static void ValidateCatalogAdvertisement(JsonElement advertisement, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCatalogCommonAdvertisement(advertisement, "", cancellationToken);
        ValidateCatalogBinding(advertisement, "");
    }

    private static void ValidateCatalogBinding(JsonElement advertisement, string path)
    {
        var name = advertisement.GetProperty("name").GetString();
        if (name is not ("http" or "git" or "oci" or "file"))
        {
            return;
        }

        var endpoint = advertisement.GetProperty("endpoint").GetString()!;
        var endpointPath = At(path, "endpoint");
        var uri = CatalogAbsoluteUri(endpoint, endpointPath);
        if (endpoint.Contains('?', StringComparison.Ordinal) || endpoint.Contains('#', StringComparison.Ordinal))
        {
            throw Invalid("A binding endpoint must not contain a query or fragment.", endpointPath);
        }
        _ = advertisement.TryGetProperty("parameters", out var parameters);
        var parametersPath = At(path, "parameters");
        switch (name)
        {
            case "http":
                CatalogHttpRoot(endpoint, endpointPath);
                break;
            case "git":
                if (uri.Scheme != "https" || uri.Host.Length == 0)
                {
                    throw Invalid("Expected an absolute HTTPS Git repository URL.", endpointPath);
                }
                if (!CatalogBindingSyntax.IsGitRevision(CatalogRequiredText(parameters, "revision", parametersPath)))
                {
                    throw Invalid("A Git revision must be a complete ref or object ID.", At(parametersPath, "revision"));
                }
                if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("path", out var root) &&
                    !CatalogBindingSyntax.IsGitRootPath(CatalogText(root, At(parametersPath, "path"), allowEmpty: true)))
                {
                    throw Invalid("A Git root must use portable relative directory components.", At(parametersPath, "path"));
                }
                break;
            case "oci":
                var pathStart = uri.Scheme == "oci" && uri.Host.Length != 0
                    ? endpoint.IndexOf('/', endpoint.IndexOf(':', StringComparison.Ordinal) + 3) : -1;
                if (pathStart < 0 || !CatalogOciRepository(endpoint[pathStart..]))
                {
                    throw Invalid("Expected an OCI repository locator without a tag or digest.", endpointPath);
                }
                CatalogOciReference(CatalogRequiredText(parameters, "reference", parametersPath), At(parametersPath, "reference"));
                break;
            case "file":
                var filePath = uri.AbsolutePath;
                var drivePath = filePath.Length >= 3 && char.IsAsciiLetter(filePath[0]) && filePath[1] == ':' && filePath[2] == '/';
                if (uri.Scheme != "file" || filePath.Length == 0 || !(filePath.StartsWith('/') || drivePath))
                {
                    throw Invalid("Expected an absolute directory file URI.", endpointPath);
                }
                var layout = CatalogRequiredText(parameters, "layout", parametersPath);
                if (layout == "oci-layout")
                {
                    CatalogOciReference(CatalogRequiredText(parameters, "reference", parametersPath), At(parametersPath, "reference"));
                }
                else if (layout == "document-tree")
                {
                    if (parameters.TryGetProperty("reference", out _))
                    {
                        throw Invalid("A File document tree must not declare a reference.", At(parametersPath, "reference"));
                    }
                }
                else
                {
                    throw Invalid("Unknown File layout.", At(parametersPath, "layout"));
                }
                break;
        }
    }

    private static void CatalogOciReference(string value, string path)
    {
        if (value.StartsWith("sha256:", StringComparison.Ordinal) &&
            value.Length == 71 && value[7..].All(static c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
        {
            return;
        }
        if (value.Length is < 1 or > 128 || !(char.IsAsciiLetterOrDigit(value[0]) || value[0] == '_') ||
            !value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
        {
            throw Invalid("Expected an OCI tag or lowercase SHA-256 digest reference.", path);
        }
    }

    private static bool CatalogOciRepository(string path)
    {
        if (!path.StartsWith('/') || path.Length < 2)
        {
            return false;
        }
        foreach (var part in path[1..].Split('/'))
        {
            var i = 0;
            while (i < part.Length)
            {
                var start = i;
                while (i < part.Length && (char.IsAsciiLetterLower(part[i]) || char.IsAsciiDigit(part[i])))
                {
                    i++;
                }
                if (i == start)
                {
                    return false;
                }
                if (i == part.Length)
                {
                    break;
                }
                if (part[i] == '-')
                {
                    while (i < part.Length && part[i] == '-')
                    {
                        i++;
                    }
                }
                else if (part[i] is '_' or '.')
                {
                    var separator = part[i++];
                    if (separator == '_' && i < part.Length && part[i] == '_')
                    {
                        i++;
                    }
                }
                else
                {
                    return false;
                }
                if (i == part.Length)
                {
                    return false;
                }
            }
            if (part.Length == 0)
            {
                return false;
            }
        }
        return true;
    }
}
