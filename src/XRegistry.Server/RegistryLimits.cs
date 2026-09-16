// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

namespace XRegistry.Server;

/// <summary>Inclusive per-operation budgets. Exhaustion rejects the operation before publication.</summary>
public sealed record RegistryLimits
{
    /// <summary>Gets the maximum request Document size, including chunked inputs.</summary>
    public int MaxDocumentBytes { get; init; } = 8 * 1024 * 1024;
    /// <summary>Gets the maximum fully prepared response body size.</summary>
    public int MaxResponseBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>Gets the maximum records visited during one request, including nested entities.</summary>
    public int MaxEntityOperations { get; init; } = 10_000;
    /// <summary>Gets the maximum aggregate input/output HTTP metadata header bytes.</summary>
    public int MaxHeaderBytes { get; init; } = 32 * 1024;
    /// <summary>Gets the maximum query string length.</summary>
    public int MaxQueryCharacters { get; init; } = 8192;
    /// <summary>Gets the maximum aggregate working-set metadata and loaded Document bytes in one operation.</summary>
    public long MaxWorkingSetBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>Gets the schema work budget shared across Resource validator calls in one operation.</summary>
    public int MaxSchemaSteps { get; init; } = 100_000;
    /// <summary>Gets the wall-clock deadline for each injected validation or outbound-policy call.</summary>
    public TimeSpan ValidationTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Gets the maximum simultaneous engine operations; excess work is rejected rather than queued without bound.</summary>
    public int MaxConcurrentOperations { get; init; } = 32;
    /// <summary>Gets the maximum outstanding injected validation/model compilation calls, including calls that ignored cancellation.</summary>
    public int MaxConcurrentExternalCalls { get; init; } = 4;
    /// <summary>Gets bounded metadata parsing and validation work.</summary>
    public RegistryJsonLimits Json { get; init; } = new();

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDocumentBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxResponseBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEntityOperations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxHeaderBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxQueryCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxWorkingSetBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSchemaSteps, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentOperations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentExternalCalls, 1);
        if (ValidationTimeout <= TimeSpan.Zero || ValidationTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(ValidationTimeout));
        }

        ArgumentNullException.ThrowIfNull(Json);
        _ = RegistryJson.Parse("{}", Json);
    }
}

internal static class ServerErrors
{
    internal static RegistryException With(string code, string path, string message, params (string Name, string Value)[] arguments) =>
        Create(code, path, message, arguments.ToDictionary(static pair => pair.Name, static pair => pair.Value, StringComparer.Ordinal));

    internal static RegistryException Wrap(RegistryException original, string code, string path,
        params (string Name, string Value)[] arguments)
    {
        var exception = new RegistryException(new(code, path, original.Diagnostic.Message), original);
        var values = original.Data["xregistry.args"] is IReadOnlyDictionary<string, string> existing
            ? new Dictionary<string, string>(existing, StringComparer.Ordinal) : new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            values[argument.Name] = argument.Value;
        }
        AddDetail(code, original.Diagnostic.Message, values);

        exception.Data["xregistry.args"] = values;
        return exception;
    }

    internal static RegistryException Create(string code, string path, string message,
        IReadOnlyDictionary<string, string>? arguments = null)
    {
        var exception = new RegistryException(new(code, path, message));
        var values = arguments is null ? new Dictionary<string, string>(StringComparer.Ordinal) :
            new Dictionary<string, string>(arguments, StringComparer.Ordinal);
        AddDetail(code, message, values);
        if (values.Count != 0)
        {
            exception.Data["xregistry.args"] = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(values);
        }

        return exception;
    }

    private static void AddDetail(string code, string message, Dictionary<string, string> arguments)
    {
        if (code is "bad_request" or "bad_filter" or "bad_sort" or "bad_inline" or "bad_ignore" or "bad_defaultversionid" or
            "capability_error" or "invalid_attribute" or "malformed_id" or "malformed_xid" or "malformed_xref" or "model_error" or "parsing_data")
        {
            arguments.TryAdd("error_detail", message.TrimEnd('.'));
        }
    }
}

internal static class BoundedContent
{
    internal static async ValueTask<byte[]> ReadAsync(Stream stream, int limit, CancellationToken cancellationToken, bool requestBody = false)
    {
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(8192, limit)];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0,
                (int)Math.Min(buffer.Length, (long)limit - output.Length + 1)), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            if (count > limit - output.Length)
            {
                throw ServerErrors.Create(requestBody ? "request_too_large" : "too_large", "", "The Document exceeds its byte budget.");
            }

            output.Write(buffer, 0, count);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return output.ToArray();
    }
}
