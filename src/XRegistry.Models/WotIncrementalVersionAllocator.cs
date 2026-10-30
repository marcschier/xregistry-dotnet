// Copyright (c) 2026 xregistry-dotnet contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Numerics;

namespace XRegistry.Models;

/// <summary>A proposed revision and the counter that its publication must commit atomically.</summary>
public sealed record WotIncrementalVersionAssignment(string VersionId, string Origin, string HighWaterMark);

/// <summary>Assigns incremental WoT revision ids from caller-supplied durable allocation history.</summary>
/// <remarks>
/// This function does not infer a counter from retained Versions or commit an allocation.
/// The publication owner must persist the result with bytes, metadata and Core state using
/// a transaction or generation check, retaining allocation history after Resource deletion.
/// </remarks>
public static class WotIncrementalVersionAllocator
{
    /// <summary>Assigns an authored integer above the watermark, or generates its immediate successor.</summary>
    /// <param name="highWaterMark">The durable maximum allocated revision; null or "0" means no prior allocation.</param>
    /// <param name="authoredVersion">The document's version.model for a TM or version.instance for a TD.</param>
    /// <param name="suppliedVersionId">An optional registry id that must exactly match the assignment.</param>
    /// <exception cref="ArgumentException">The captured durable watermark is invalid.</exception>
    /// <exception cref="RegistryException">A revision is invalid, exhausted, reused or does not match the assignment.</exception>
    public static WotIncrementalVersionAssignment Assign(string? highWaterMark, string? authoredVersion,
        string? suppliedVersionId = null)
    {
        if (highWaterMark is not null && highWaterMark != "0" && !IsRevision(highWaterMark))
        {
            throw new ArgumentException("The durable high-water mark must be zero or a canonical revision id.",
                nameof(highWaterMark));
        }

        var maximum = highWaterMark is null ? BigInteger.Zero :
            BigInteger.Parse(highWaterMark, NumberStyles.None, CultureInfo.InvariantCulture);
        var authored = HasCanonicalDigits(authoredVersion);
        string assigned;
        if (authored)
        {
            if (authoredVersion!.Length > 128)
            {
                throw Error("invalid-version", "An authored incremental revision exceeds the Core id limit.");
            }
            if (BigInteger.Parse(authoredVersion, NumberStyles.None, CultureInfo.InvariantCulture) <= maximum)
            {
                throw Error("invalid-version", "A new authored revision must exceed the durable high-water mark.");
            }
            assigned = authoredVersion;
        }
        else
        {
            assigned = (maximum + BigInteger.One).ToString(CultureInfo.InvariantCulture);
            if (assigned.Length > 128)
            {
                throw Error("invalid-version", "The incremental revision sequence is exhausted.");
            }
        }

        if (suppliedVersionId is not null)
        {
            if (!IsRevision(suppliedVersionId))
            {
                throw Error("invalid-version", "A supplied incremental revision must be a canonical positive integer.");
            }
            if (!StringComparer.Ordinal.Equals(suppliedVersionId, assigned))
            {
                throw Error("version-mismatch", "The supplied revision differs from the profile's assignment.");
            }
        }
        return new(assigned, authored ? "authored" : "generated", assigned);
    }

    private static bool IsRevision(string value) => value.Length <= 128 && HasCanonicalDigits(value);

    internal static bool TryParseRevision(string? value, out BigInteger revision)
    {
        revision = BigInteger.Zero;
        return value is not null && IsRevision(value) &&
            BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out revision);
    }

    private static bool HasCanonicalDigits(string? value) =>
        !string.IsNullOrEmpty(value) && value[0] is >= '1' and <= '9' && value.All(char.IsAsciiDigit);

    private static RegistryException Error(string code, string message) => new(new(code, "", message));
}
