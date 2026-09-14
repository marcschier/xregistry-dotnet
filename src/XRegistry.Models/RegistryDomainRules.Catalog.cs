using System.Globalization;
using System.Text.Json;

namespace XRegistry.Models;

public static partial class RegistryDomainRules
{
    /// <summary>The explicit Resource model compatibility identity for Registry-of-Registries descriptions.</summary>
    public const string CatalogModelUri = "https://xregistry.io/xreg/domains/registry/specs/model.json";

    /// <summary>Checks a catalog-description Version without selecting or accessing an advertised Registry.</summary>
    public static void ValidateCatalogMetadata(JsonElement metadata, CancellationToken cancellationToken = default) =>
        ValidateCatalogDescriptionCore(metadata, true, cancellationToken);

    /// <summary>Checks common description fields before a consumer selects a binding; no binding parameters are interpreted.</summary>
    public static void ValidateCatalogDescription(JsonElement metadata, CancellationToken cancellationToken = default) =>
        ValidateCatalogDescriptionCore(metadata, false, cancellationToken);

    private static void ValidateCatalogDescriptionCore(JsonElement metadata, bool validateBindings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireMetadataObject(metadata);
        string? httpRoot = null;
        if (metadata.TryGetProperty("xregurl", out var xregurl))
        {
            httpRoot = CatalogText(xregurl, "/xregurl");
            CatalogHttpRoot(httpRoot, "/xregurl");
        }
        foreach (var name in new[] { "weburl", "authority" })
        {
            if (metadata.TryGetProperty(name, out var value))
            {
                var text = CatalogText(value, "/" + name, allowEmpty: true);
                var syntaxText = CatalogUriSyntaxText(text);
                if (!Uri.IsWellFormedUriString(syntaxText, UriKind.RelativeOrAbsolute))
                {
                    throw Invalid("Expected a catalog URI reference.", "/" + name);
                }
                if (Uri.TryCreate(syntaxText, UriKind.Absolute, out _))
                {
                    CatalogAbsoluteUri(text, "/" + name);
                }
            }
        }
        foreach (var (type, path) in CatalogItems(metadata, "registrytypes", cancellationToken))
        {
            CatalogAbsoluteUri(CatalogText(type, path), path);
        }
        if (metadata.TryGetProperty("labels", out var labels))
        {
            CatalogLabels(labels, "/labels", cancellationToken);
        }

        var explicitHttp = false;
        var matchingHttp = false;
        foreach (var (profile, path) in CatalogItems(metadata, "federationprofiles", cancellationToken))
        {
            ValidateCatalogCommonAdvertisement(profile, path, cancellationToken);
            if (validateBindings)
            {
                ValidateCatalogBinding(profile, path);
            }
            if (profile.GetProperty("name").GetString() == "http")
            {
                explicitHttp = true;
                matchingHttp |= string.Equals(profile.GetProperty("endpoint").GetString(), httpRoot, StringComparison.Ordinal);
            }
        }
        if (httpRoot is not null && explicitHttp && !matchingHttp)
        {
            throw Invalid("Conflicting xregurl and explicit HTTP advertisements.", "/xregurl");
        }
        foreach (var (relationship, path) in CatalogItems(metadata, "relationships", cancellationToken))
        {
            CatalogFields(relationship, path, "type", "target", "labels");
            _ = CatalogRequiredText(relationship, "type", path);
            var targetPath = At(path, "target");
            var target = CatalogRequiredText(relationship, "target", path);
            if (target.StartsWith('/'))
            {
                RegistryPath entry;
                try
                {
                    entry = RegistryPath.Parse(target);
                }
                catch (RegistryException exception)
                {
                    throw new RegistryException(new("invalid_attribute", targetPath,
                        "A local relationship target must name a catalog Resource XID."), exception);
                }
                if (entry.Kind != RegistryPathKind.Resource || entry.IsDetails ||
                    entry.GroupType != "categories" || entry.ResourceType != "registries")
                {
                    throw Invalid("A local relationship target must name a catalog Resource XID.", targetPath);
                }
            }
            else
            {
                CatalogAbsoluteUri(target, targetPath);
            }
            if (relationship.TryGetProperty("labels", out var relationshipLabels))
            {
                CatalogLabels(relationshipLabels, At(path, "labels"), cancellationToken);
            }
        }
    }

    private static void ValidateCatalogCommonAdvertisement(JsonElement advertisement, string path, CancellationToken cancellationToken)
    {
        CatalogFields(advertisement, path, "name", "endpoint", "priority", "parameters");
        _ = CatalogRequiredText(advertisement, "name", path);
        CatalogAbsoluteUri(CatalogRequiredText(advertisement, "endpoint", path), At(path, "endpoint"));
        if (advertisement.TryGetProperty("priority", out var priority))
        {
            if (priority.ValueKind != JsonValueKind.Number)
            {
                throw Invalid("Catalog priority must be an unsigned integer.", At(path, "priority"));
            }
            var value = RegistryNumber.FromElement(priority);
            if (!value.IsInteger || value.Significand.Sign < 0)
            {
                throw Invalid("Catalog priority must be an unsigned integer.", At(path, "priority"));
            }
        }
        if (advertisement.TryGetProperty("parameters", out var parameters))
        {
            if (parameters.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("Catalog parameters must be an object.", At(path, "parameters"));
            }
            foreach (var parameter in parameters.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CatalogAttributeName(parameter.Name))
                {
                    throw Invalid("Catalog parameter names must be Core attribute names.", At(At(path, "parameters"), parameter.Name));
                }
            }
        }
    }

    private static IEnumerable<(JsonElement Value, string Path)> CatalogItems(
        JsonElement metadata, string name, CancellationToken cancellationToken)
    {
        if (!metadata.TryGetProperty(name, out var values))
        {
            yield break;
        }
        if (values.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("The catalog field must be an array.", "/" + name);
        }
        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return (value, "/" + name + "/" + index.ToString(CultureInfo.InvariantCulture));
            index++;
        }
    }

    private static void CatalogFields(JsonElement value, string path, params ReadOnlySpan<string> allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("The catalog field must be an object.", path);
        }
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new RegistryException(new("unknown_attribute", At(path, property.Name),
                    "Unexpected field in a closed catalog object."));
            }
        }
    }

    private static bool CatalogAttributeName(string name) => name.Length is >= 1 and <= 63 &&
        (char.IsAsciiLetterLower(name[0]) || name[0] == '_') &&
        name.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    private static void CatalogLabels(JsonElement value, string path, CancellationToken cancellationToken)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Catalog labels must be a map of strings.", path);
        }
        foreach (var label in value.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = label.Name;
            if (name.Length is < 1 or > 63 || !(char.IsAsciiLetterLower(name[0]) || char.IsAsciiDigit(name[0])) ||
                !name.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is ':' or '-' or '_' or '.') ||
                label.Value.ValueKind != JsonValueKind.String)
            {
                throw Invalid("Catalog labels must use Core map keys and string values.", At(path, name));
            }
        }
    }

    private static string CatalogRequiredText(JsonElement metadata, string name, string path, bool allowEmpty = false)
    {
        if (metadata.ValueKind != JsonValueKind.Object || !metadata.TryGetProperty(name, out var value))
        {
            throw Invalid("A required catalog field is missing.", At(path, name));
        }
        return CatalogText(value, At(path, name), allowEmpty);
    }

    private static string CatalogText(JsonElement value, string path, bool allowEmpty = false)
    {
        if (value.ValueKind != JsonValueKind.String || !allowEmpty && value.GetString()!.Length == 0)
        {
            throw Invalid(allowEmpty ? "The catalog field must be a string." : "The catalog field must be a nonempty string.", path);
        }
        return value.GetString()!;
    }

    private static Uri CatalogAbsoluteUri(string value, string path)
    {
        var syntaxText = CatalogUriSyntaxText(value);
        if (value.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
            !Uri.TryCreate(syntaxText, UriKind.Absolute, out var uri) ||
            !Uri.IsWellFormedUriString(syntaxText, UriKind.Absolute))
        {
            throw Invalid("Expected an absolute catalog URI.", path);
        }
        var authority = value.AsSpan(value.IndexOf(':', StringComparison.Ordinal) + 1);
        var userInformation = uri.UserInfo.Length != 0;
        if (authority.StartsWith("//", StringComparison.Ordinal))
        {
            authority = authority[2..];
            var end = authority.IndexOfAny('/', '?', '#');
            userInformation |= (end < 0 ? authority : authority[..end]).Contains('@');
        }
        if (userInformation)
        {
            throw Invalid("Embedded credentials are prohibited in catalog URIs.", path);
        }
        return uri;
    }

    private static string CatalogUriSyntaxText(string value) =>
        // Uri treats single-letter schemes as Windows drives; only the syntax probe uses a longer scheme.
        value.Length >= 2 && value[1] == ':' && char.IsAsciiLetter(value[0]) ? "xregistry-" + value : value;

    private static void CatalogHttpRoot(string value, string path)
    {
        var uri = CatalogAbsoluteUri(value, path);
        if (uri.Scheme is not ("http" or "https") || uri.Host.Length == 0 ||
            value.Contains('?', StringComparison.Ordinal) || value.Contains('#', StringComparison.Ordinal))
        {
            throw Invalid("Expected an HTTP(S) Registry root without query or fragment.", path);
        }
    }
}
