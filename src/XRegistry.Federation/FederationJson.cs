// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Numerics;
using System.Text.Json;

namespace XRegistry.Federation;

internal static class FederationJson
{
    internal static FederationException Invalid(string message) =>
        new(FederationErrorCode.InvalidPackage, message);

    internal static JsonElement Parse(ReadOnlyMemory<byte> bytes, FederationReadBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        budget ??= new();
        cancellationToken.ThrowIfCancellationRequested();
        budget.CheckObjectBytes(bytes.Length);
        try
        {
            var json = RegistryJson.Parse(bytes.Span, new RegistryJsonLimits
            {
                MaxBytes = budget.Limits.MaxObjectBytes,
                MaxDepth = budget.Limits.MaxJsonDepth,
                MaxNodes = (int)Math.Min(int.MaxValue, budget.Limits.MaxWork),
            });
            RequireObject(json.RootElement);
            CountWork(json.RootElement, budget, cancellationToken);
            return json.RootElement;
        }
        catch (RegistryException exception)
        {
            throw FromCore(exception);
        }
    }

    internal static void ValidateKeys(JsonElement value)
    {
        try
        {
            RegistryJson.FromElement(value);
        }
        catch (RegistryException exception)
        {
            throw FromCore(exception);
        }
    }

    internal static FederationException FromCore(RegistryException exception) =>
        new(exception.Diagnostic.Code.EndsWith("_limit", StringComparison.Ordinal)
                ? FederationErrorCode.LimitExceeded : FederationErrorCode.InvalidPackage,
            exception.Message, exception.Diagnostic.Code, exception);

    internal static void CountWork(JsonElement value, FederationReadBudget budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        budget.ChargeWork();
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                CountWork(property.Value, budget, cancellationToken);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                CountWork(item, budget, cancellationToken);
            }
        }
    }

    internal static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Expected a JSON object.");
        }
    }

    internal static JsonElement Required(JsonElement value, string name)
    {
        RequireObject(value);
        return value.TryGetProperty(name, out var property)
            ? property : throw Invalid($"Missing required field '{name}'.");
    }

    internal static string String(JsonElement value, string name, bool allowEmpty = false)
    {
        var property = Required(value, name);
        if (property.ValueKind != JsonValueKind.String ||
            (!allowEmpty && property.GetString()!.Length == 0))
        {
            throw Invalid($"Field '{name}' must be {(allowEmpty ? "a" : "a nonempty")} string.");
        }
        return property.GetString()!;
    }

    internal static ulong Unsigned(JsonElement value, string name)
    {
        var property = Required(value, name);
        return property.ValueKind == JsonValueKind.Number && property.TryGetUInt64(out var number)
            ? number : throw Invalid($"Field '{name}' must be an unsigned integer.");
    }

    internal static BigInteger Priority(JsonElement value)
    {
        var number = Required(value, "priority");
        if (number.ValueKind != JsonValueKind.Number)
        {
            throw Invalid("Priority must be an unsigned integer.");
        }
        var parsed = RegistryNumber.FromElement(number);
        if (!parsed.IsInteger || parsed.Significand.Sign < 0)
        {
            throw Invalid("Priority must be an unsigned integer.");
        }
        return parsed.ToBigInteger();
    }

    internal static void UriReference(string value)
    {
        if (!Uri.IsWellFormedUriString(value, UriKind.RelativeOrAbsolute))
        {
            throw Invalid("Invalid URI reference.");
        }
        if (Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            AbsoluteUri(value);
        }
    }

    internal static void Fields(JsonElement value, params ReadOnlySpan<string> allowed)
    {
        RequireObject(value);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw Invalid("Unexpected field in a closed format object.");
            }
        }
    }

    internal static Uri AbsoluteUri(string value)
    {
        if (value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !Uri.IsWellFormedUriString(value, UriKind.Absolute))
        {
            throw Invalid("Expected an absolute URI.");
        }
        if (uri.UserInfo.Length != 0)
        {
            throw new FederationException(FederationErrorCode.PolicyDenied,
                "Embedded credentials are prohibited.");
        }
        return uri;
    }
}
