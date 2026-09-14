namespace XRegistry;

/// <summary>A structured failure at an RFC 6901 JSON pointer (the empty string denotes the root).</summary>
/// <param name="Code">A stable machine-readable error code.</param>
/// <param name="Path">The pointer into the input, or the protocol path for an addressing failure.</param>
/// <param name="Message">A description that does not include the rejected value.</param>
public sealed record RegistryDiagnostic(string Code, string Path, string Message);

/// <summary>An invalid registry value or model, with a machine-readable diagnostic.</summary>
public sealed class RegistryException : FormatException
{
    /// <summary>Creates a failure with its structured diagnostic and optional underlying parsing error.</summary>
    public RegistryException(RegistryDiagnostic diagnostic, Exception? innerException = null)
        : base((diagnostic ?? throw new ArgumentNullException(nameof(diagnostic))).Message, innerException)
    {
        Diagnostic = diagnostic;
    }

    /// <summary>Gets the code and exact location of the failure.</summary>
    public RegistryDiagnostic Diagnostic { get; }
}

internal static class Diagnostics
{
    internal static string At(string path, string name) =>
        path + "/" + name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    internal static RegistryException Error(string code, string path, string message) =>
        new(new RegistryDiagnostic(code, path, message));
}
