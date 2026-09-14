namespace XRegistry.AspNetCore;

internal static class ProblemDisclosure
{
    internal static bool ValidCode(string code) => code.Length is > 0 and <= 63 && char.IsAsciiLetterLower(code[0]) &&
        code.All(static character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_');

    internal static string Detail(string value) => value.Length is > 0 and <= 1024 && !Sensitive(value)
        ? value.TrimEnd('.') : "Additional error details were withheld by the registry";

    internal static string Argument(string value) => value.Length <= 1024 && !Sensitive(value) ? value : "[redacted]";

    internal static string Subject(string code, string value, string requestPath, Uri root)
    {
        if (code == "not_available")
        {
            return value is "entities" or "model" or "modelsource" or "capabilities" or "capabilitiesoffered" or "export"
                ? value : requestPath;
        }

        if (value.Length is > 0 and <= 2048 && value[0] == '/' && !value.Contains('?', StringComparison.Ordinal) &&
            !value.Contains('#', StringComparison.Ordinal) && !value.Any(char.IsControl))
        {
            return value;
        }

        if (code is "malformed_id" or "malformed_xid" or "malformed_xref" &&
            Uri.TryCreate(value, UriKind.Absolute, out var url) && url.UserInfo.Length == 0 &&
            url.Query.Length == 0 && url.Fragment.Length == 0 && value.Length <= 2048 &&
            (value == root.AbsoluteUri.TrimEnd('/') || value.StartsWith(root.AbsoluteUri.TrimEnd('/') + "/", StringComparison.Ordinal)))
        {
            return value;
        }

        return requestPath.Length <= 2048 && !requestPath.Any(char.IsControl) ? requestPath : "/";
    }

    private static bool Sensitive(string value) => value.Contains("://", StringComparison.Ordinal) ||
        value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("token=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("api-key", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase) ||
        value.Contains('\\', StringComparison.Ordinal) && value.Contains(':', StringComparison.Ordinal);
}
