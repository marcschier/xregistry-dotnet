// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using XRegistry.Federation;

namespace XRegistry.Bindings.File;

internal static class FileRegistryLocation
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int MaxLocatorCharacters = 16_384;

    internal static Uri ParseUri(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            throw Denied("A well-formed absolute local File URI is required.");
        }
        Parse(uri);
        return uri;
    }

    internal static string Parse(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeFile || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.Host.Length != 0 && !endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw Denied("The File locator must have local authority and no credentials, query or fragment.");
        }
        var original = endpoint.OriginalString;
        if (original.Length > MaxLocatorCharacters) { throw Limit("The File locator is too long."); }
        foreach (var component in original.Replace('\\', '/').Split('/'))
        {
            var dots = component.Replace("%2e", ".", StringComparison.OrdinalIgnoreCase);
            if (dots is "." or "..") { throw Denied("Original dot-segment aliases are forbidden."); }
        }
        var explicitUri = original.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        var text = explicitUri ? original : endpoint.AbsoluteUri;
        if (text.Contains('\\', StringComparison.Ordinal) || text.Contains('?', StringComparison.Ordinal) ||
            text.Contains('#', StringComparison.Ordinal) || text.Any(char.IsControl))
        {
            throw Denied("The File URI contains a forbidden separator, control, query or fragment.");
        }
        var remainder = text[5..];
        if (remainder.StartsWith("//", StringComparison.Ordinal))
        {
            var slash = remainder.IndexOf('/', 2);
            if (slash < 0) { throw Denied("A File directory path is required."); }
            var authority = remainder[2..slash];
            if (authority.Length != 0 && !authority.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                throw Denied("Remote or decorated File authorities are not authorized.");
            }
            remainder = remainder[slash..];
        }
        if (!remainder.StartsWith('/')) { throw Unsupported("The File URI must contain an absolute native path."); }
        var raw = remainder[1..].Split('/');
        if (raw.Length > 129) { throw Limit("The File root has too many components."); }
        var components = new List<string>();
        for (var index = 0; index < raw.Length; index++)
        {
            if (raw[index].Length == 0)
            {
                if (index == raw.Length - 1) { continue; }
                throw Denied("Empty interior File path components are forbidden.");
            }
            var component = Decode(raw[index]);
            if (component is "." or ".." || component.Any(c => char.IsControl(c) || c is '/' or '\\'))
            {
                throw Denied("Decoded traversal, separators and controls are forbidden.");
            }
            components.Add(component);
        }
        var drive = components.Count > 0 && components[0].Length == 2 &&
            char.IsAsciiLetter(components[0][0]) && components[0][1] == ':';
        if (OperatingSystem.IsWindows())
        {
            if (!drive) { throw Unsupported("This host requires an absolute ordinary Windows drive File URI."); }
            foreach (var component in components.Skip(1)) { WindowsComponent(component); }
            return Path.TrimEndingDirectorySeparator(components.Count == 1 ? components[0] + "\\" : string.Join('\\', components));
        }
        if (!OperatingSystem.IsLinux()) { throw Unsupported("Handle-anchored File sources support Windows and Linux."); }
        if (drive || components.Count > 0 && components[0].Contains('|', StringComparison.Ordinal))
        {
            throw Unsupported("A Windows drive File URI cannot be reinterpreted on this host.");
        }
        return components.Count == 0 ? "/" : "/" + string.Join('/', components);
    }

    internal static string Relative(string root, string selected)
    {
        var relative = Path.GetRelativePath(root, selected);
        if (Path.IsPathFullyQualified(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw Denied("The selected File root is outside the explicitly authorized boundary.");
        }
        return relative;
    }

    private static string Decode(string text)
    {
        try
        {
            var input = Utf8.GetBytes(text);
            var decoded = new byte[input.Length];
            var count = 0;
            for (var index = 0; index < input.Length; index++)
            {
                var current = input[index];
                if (current == '%')
                {
                    if (index + 2 >= input.Length || !char.IsAsciiHexDigit((char)input[index + 1]) ||
                        !char.IsAsciiHexDigit((char)input[index + 2]))
                    {
                        throw Denied("A File URI contains an invalid percent escape.");
                    }
                    current = (byte)((Hex(input[index + 1]) << 4) | Hex(input[index + 2]));
                    index += 2;
                }
                decoded[count++] = current;
            }
            return Utf8.GetString(decoded, 0, count);
        }
        catch (EncoderFallbackException exception) { throw new FederationException(FederationErrorCode.PolicyDenied, "Invalid File URI Unicode.", innerException: exception); }
        catch (DecoderFallbackException exception) { throw new FederationException(FederationErrorCode.PolicyDenied, "Invalid File URI UTF-8 escapes.", innerException: exception); }
    }

    private static int Hex(byte value) => value is >= (byte)'0' and <= (byte)'9' ? value - '0' : (value | 32) - 'a' + 10;

    private static void WindowsComponent(string component)
    {
        var device = component.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (component.EndsWith(' ') || component.EndsWith('.') || component.Any(c => c is ':' or '<' or '>' or '"' or '|' or '?' or '*') ||
            device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
                device[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3')
        {
            throw Denied("The File path contains a Windows device, stream or ambiguous component.");
        }
    }

    internal static FederationException Denied(string message) => new(FederationErrorCode.PolicyDenied, message);
    internal static FederationException Unsupported(string message) => new(FederationErrorCode.UnsupportedOperation, message);
    internal static FederationException Limit(string message) => new(FederationErrorCode.LimitExceeded, message);
}

internal sealed class FileRegistryRoot : IDisposable
{
    private readonly FileDocumentTreeReader boundary;
    private FileRegistryRoot(FileDocumentTreeReader reader, FileDocumentTreeReader boundary)
    {
        Reader = reader;
        this.boundary = boundary;
    }
    internal FileDocumentTreeReader Reader { get; }

    internal static FileRegistryRoot Open(Uri endpoint, Uri? authorizedRoot, long maxFileBytes)
    {
        var selected = FileRegistryLocation.Parse(endpoint);
        var boundaryUri = authorizedRoot ?? endpoint;
        var boundaryPath = FileRegistryLocation.Parse(boundaryUri);
        var relative = FileRegistryLocation.Relative(boundaryPath, selected);
        FileDocumentTreeReader? anchor = null;
        FileDocumentTreeReader? reader = null;
        var transferred = false;
        try
        {
            anchor = FileDocumentTreeReader.Open(boundaryUri, maxFileBytes);
            anchor.VerifyCanonicalRoot();
            reader = anchor.OpenAuthorizedChild(endpoint, selected, relative, maxFileBytes);
            reader.VerifyCanonicalRoot();
            transferred = true;
            return new(reader, anchor);
        }
        catch (FileNotFoundException exception)
        {
            throw new FederationException(FederationErrorCode.NotFound, "The selected or authorized File directory is absent.", innerException: exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new FederationException(FederationErrorCode.PolicyDenied, "File root authorization failed.", innerException: exception);
        }
        catch (IOException exception)
        {
            throw new FederationException(FederationErrorCode.Unavailable, "File root access failed.", innerException: exception);
        }
        finally
        {
            if (!transferred)
            {
                reader?.Dispose();
                anchor?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        try { Reader.Dispose(); }
        finally { boundary.Dispose(); }
    }
}
