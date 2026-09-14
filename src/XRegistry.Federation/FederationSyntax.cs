namespace XRegistry.Federation;

internal static class FederationSyntax
{
    internal static bool Id(string value) => RegistryId.IsValid(value);

    internal static bool Attribute(string value) => value.Length is >= 1 and <= 63 &&
        (char.IsAsciiLetterLower(value[0]) || value[0] == '_') &&
        value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    internal static bool MapKey(string value) => value.Length is >= 1 and <= 63 &&
        (char.IsAsciiLetterLower(value[0]) || char.IsAsciiDigit(value[0])) &&
        value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is ':' or '-' or '_' or '.');

    internal static string[] Xid(string value, bool collection = false)
    {
        if (value.Length > 8192)
        {
            throw new FederationException(FederationErrorCode.LimitExceeded, "The typed XID exceeds its path budget.");
        }
        if (value == "/" && !collection)
        {
            return [];
        }
        if (!value.StartsWith('/'))
        {
            throw FederationJson.Invalid("XIDs must be Registry-root-relative.");
        }
        var encoded = value[1..].Split('/');
        if (encoded.Length > 6)
        {
            throw FederationJson.Invalid("Invalid typed XID shape or identifier.");
        }
        string[] parts;
        try
        {
            parts = encoded.Select(part => RegistryId.ParseEscaped(part).Value).ToArray();
        }
        catch (RegistryException exception)
        {
            throw FederationJson.FromCore(exception);
        }
        if (
            (collection
                ? parts.Length is not (1 or 3 or 5) || (parts.Length == 5 && parts[4] != "versions")
                : parts.Length is not (2 or 4 or 5 or 6) ||
                    (parts.Length == 5 && parts[4] != "meta") ||
                    (parts.Length == 6 && (parts[4] != "versions" || parts[5] is "null" or "request"))))
        {
            throw FederationJson.Invalid("Invalid typed XID shape or identifier.");
        }
        return parts;
    }

    internal static bool SameXid(string left, string right, bool collection = false) =>
        Xid(left, collection).SequenceEqual(Xid(right, collection), StringComparer.Ordinal);

    internal static void Labels(System.Text.Json.JsonElement labels)
    {
        FederationJson.RequireObject(labels);
        foreach (var label in labels.EnumerateObject())
        {
            if (!MapKey(label.Name) || label.Value.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                throw FederationJson.Invalid("Labels must use Core map keys and string values.");
            }
        }
    }

    internal static void StoragePath(string value, bool allowEmpty = false)
    {
        if (allowEmpty && value.Length == 0)
        {
            return;
        }
        if (value.Length > 4096)
        {
            throw new FederationException(FederationErrorCode.LimitExceeded, "Storage path is too long.");
        }
        if (value.Length == 0 || value.Any(c => char.IsControl(c) || c is '\\' or '<' or '>' or ':' or '"' or '|' or '?' or '*') ||
            value.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("%5c", StringComparison.OrdinalIgnoreCase))
        {
            throw new FederationException(FederationErrorCode.PolicyDenied, "Unsafe root-relative storage path.");
        }
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsSurrogate(value[index]))
            {
                if (!char.IsHighSurrogate(value[index]) || index + 1 == value.Length ||
                    !char.IsLowSurrogate(value[++index]))
                {
                    throw FederationJson.Invalid("Unpaired surrogate in storage path.");
                }
            }
        }
        foreach (var part in value.Split('/'))
        {
            var dotCheck = part.Replace("%2e", ".", StringComparison.OrdinalIgnoreCase);
            var device = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (part.Length == 0 || dotCheck is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) ||
                    device.StartsWith("LPT", StringComparison.Ordinal)) &&
                    (device[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3')))
            {
                throw new FederationException(FederationErrorCode.PolicyDenied, "Unsafe root-relative storage path.");
            }
        }
    }
}
