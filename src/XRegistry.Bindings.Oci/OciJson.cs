// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using XRegistry.Federation;

namespace XRegistry.Bindings.Oci;

internal static class OciJson
{
    internal static FederationException Invalid(string message) => new(FederationErrorCode.InvalidPackage, message);

    internal static FederationException CoreError(RegistryException exception) =>
        new(exception.Diagnostic.Code.EndsWith("_limit", StringComparison.Ordinal)
                ? FederationErrorCode.LimitExceeded : FederationErrorCode.InvalidPackage,
            exception.Message + " At " + exception.Diagnostic.Path + ".", exception.Diagnostic.Code, exception);

    internal static RegistryJsonLimits Limits(FederationReadBudget budget) => new()
    {
        MaxBytes = budget.Limits.MaxObjectBytes,
        MaxDepth = budget.Limits.MaxJsonDepth,
        MaxNodes = (int)Math.Max(1, Math.Min(int.MaxValue, budget.Limits.MaxWork - budget.Work)),
    };

    internal static JsonElement Parse(ReadOnlySpan<byte> bytes, FederationReadBudget budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = RegistryJson.Parse(bytes, Limits(budget)).RootElement;
            Charge(result, budget, cancellationToken);
            return result;
        }
        catch (RegistryException exception) { throw CoreError(exception); }
    }

    internal static void Charge(JsonElement value, FederationReadBudget budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        budget.ChargeWork();
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject()) { Charge(property.Value, budget, cancellationToken); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) { Charge(item, budget, cancellationToken); }
        }
    }

    internal static void Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) { throw Invalid("A JSON object is required."); }
    }

    internal static JsonElement Required(JsonElement value, string name)
    {
        Object(value);
        return value.TryGetProperty(name, out var result) ? result : throw Invalid("A required field is absent: " + name + ".");
    }

    internal static string Text(JsonElement value, string name, bool empty = false)
    {
        var result = Required(value, name);
        return result.ValueKind == JsonValueKind.String && (empty || result.GetString()!.Length != 0)
            ? result.GetString()! : throw Invalid("A string field is malformed: " + name + ".");
    }

    internal static bool Boolean(JsonElement value, string name)
    {
        var result = Required(value, name);
        return result.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? result.GetBoolean() : throw Invalid("A boolean field is malformed: " + name + ".");
    }

    internal static long Integer(JsonElement value, string name)
    {
        var result = Required(value, name);
        if (result.ValueKind != JsonValueKind.Number)
        {
            throw Invalid("A nonnegative bounded integer is required: " + name + ".");
        }
        var number = RegistryNumber.FromElement(result);
        if (!number.IsInteger || number.Significand.Sign < 0 || number.Exponent > 18)
        {
            throw Invalid("A nonnegative bounded integer is required: " + name + ".");
        }
        var integer = number.ToBigInteger();
        return integer <= long.MaxValue ? (long)integer : throw Invalid("An OCI integer exceeds the signed 64-bit bound.");
    }

    internal static void Fields(JsonElement value, params string[] names)
    {
        Object(value);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.Ordinal))
            {
                throw Invalid("An object contains an unsupported field: " + property.Name + ".");
            }
        }
    }

    internal static JsonNode? Copy(JsonElement value) => JsonNode.Parse(value.GetRawText());

    internal static bool Equal(JsonElement left, JsonElement right) =>
        JsonNode.DeepEquals(Copy(left), Copy(right));

    internal static bool Includes(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            return value.EnumerateObject().Any(p => p.Name is "$include" or "$includes" || Includes(p.Value));
        }
        return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(Includes);
    }

    internal static Uri AbsoluteUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw Invalid("A captured document locator must be an absolute URI.");
        }
        if (uri.UserInfo.Length != 0 || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
            value.Contains('\\', StringComparison.Ordinal))
        {
            throw new FederationException(FederationErrorCode.PolicyDenied, "A credential-free absolute URI is required.");
        }
        return uri;
    }

    internal static void ValidateSourcePolicy(JsonElement value, FederationReadBudget budget)
    {
        budget.ChargeWork();
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("$include", out var include)) { Reference(include); }
            if (value.TryGetProperty("$includes", out var includes))
            {
                foreach (var reference in includes.EnumerateArray()) { Reference(reference); }
            }
            foreach (var property in value.EnumerateObject()) { ValidateSourcePolicy(property.Value, budget); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray()) { ValidateSourcePolicy(child, budget); }
        }

        static void Reference(JsonElement value)
        {
            if (Uri.TryCreate(value.GetString(), UriKind.Absolute, out _))
            {
                AbsoluteUri(value.GetString()!);
            }
        }
    }

    internal static string PointerToken(string value) =>
        value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
