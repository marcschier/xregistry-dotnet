using System.Buffers;
using System.Security.Cryptography;

namespace XRegistry.Validation;

/// <summary>Distinguishes publication requirements from tolerant consumer digest processing.</summary>
public enum OpenUsdDigestRole
{
    /// <summary>A published digest must include its algorithm.</summary>
    Producer,
    /// <summary>A digest without an algorithm is interpreted as Sha256.</summary>
    Consumer,
}

/// <summary>Validates OpenUSD integrity declarations independently of artifact acquisition.</summary>
public static class OpenUsdArtifactIntegrity
{
    private static readonly SearchValues<char> HexCharacters = SearchValues.Create("0123456789abcdef");

    /// <summary>Checks digest metadata for its explicitly selected producer or consumer role.</summary>
    /// <remarks>
    /// Null represents an absent field; empty strings are invalid. Digests use lowercase hexadecimal
    /// and the exact length for Sha256, Sha384 or Sha512. A consumer's missing algorithm means Sha256.
    /// An absent digest is allowed with an absent or recognized algorithm and does not establish integrity.
    /// </remarks>
    public static void ValidateMetadata(string? digest, string? digestAlgorithm, OpenUsdDigestRole role) =>
        _ = ValidateDeclaration(digest, digestAlgorithm, role);

    private static HashAlgorithmName ValidateDeclaration(string? digest, string? digestAlgorithm, OpenUsdDigestRole role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), "Unknown OpenUSD digest role.");
        }

        var (algorithm, length) = digestAlgorithm switch
        {
            null or "Sha256" => (HashAlgorithmName.SHA256, 64),
            "Sha384" => (HashAlgorithmName.SHA384, 96),
            "Sha512" => (HashAlgorithmName.SHA512, 128),
            _ => throw new ArgumentException("digestalg must be exactly Sha256, Sha384 or Sha512.", nameof(digestAlgorithm)),
        };
        if (digest is not null && digestAlgorithm is null && role == OpenUsdDigestRole.Producer)
        {
            throw new ArgumentException("Publishing an OpenUSD digest requires an explicit digestalg.", nameof(digestAlgorithm));
        }

        if (digest is not null && (digest.Length != length || digest.AsSpan().ContainsAnyExcept(HexCharacters)))
        {
            throw new ArgumentException("An OpenUSD digest must have the algorithm's exact lowercase hexadecimal length.", nameof(digest));
        }

        return algorithm;
    }

    /// <summary>Reads caller-supplied bytes under explicit budgets before making an artifact available.</summary>
    /// <remarks>
    /// Validates metadata before reading, starts at the current source position, and never disposes or rewinds
    /// the source. Every read including the EOF probe consumes an operation. Actual bytes, not Length, enforce
    /// the byte limit. No artifact is returned on invalid metadata, mismatch, budget failure, cancellation or I/O
    /// failure. The source must cooperate with ReadAsync cancellation; no background reads are abandoned.
    /// Staging and its owned result use at most twice the byte budget, plus a read buffer of at most 64 KiB.
    /// </remarks>
    public static async ValueTask<OpenUsdArtifact> ReadAsync(Stream source, string? digest, string? digestAlgorithm,
        OpenUsdDigestRole role, OpenUsdArtifactReadLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        var algorithm = ValidateDeclaration(digest, digestAlgorithm, role);
        cancellationToken.ThrowIfCancellationRequested();
        if (!source.CanRead)
        {
            throw new ArgumentException("The artifact source must be readable.", nameof(source));
        }

        var expected = digest is null ? null : Convert.FromHexString(digest);
        using var hash = expected is null ? null : IncrementalHash.CreateHash(algorithm);
        using var staged = new MemoryStream(Math.Min(limits.MaxArtifactBytes, 64 * 1024));
        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min((long)limits.MaxArtifactBytes + 1, 64 * 1024));
        try
        {
            var reads = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reads == limits.MaxReadOperations)
                {
                    throw new InvalidDataException("The OpenUSD source-read operation limit was exhausted before EOF.");
                }

                var remaining = limits.MaxArtifactBytes - (int)staged.Length;
                var requested = (int)Math.Min(buffer.Length, (long)remaining + 1);
                reads++;
                var count = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (count < 0 || count > requested)
                {
                    throw new InvalidDataException("The artifact source returned an invalid read count.");
                }

                if (count == 0)
                {
                    break;
                }

                if (count > remaining)
                {
                    throw new InvalidDataException("The OpenUSD artifact exceeds its byte limit.");
                }

                var required = (int)staged.Length + count;
                if (required > staged.Capacity)
                {
                    staged.Capacity = Math.Max(required, (int)Math.Min(limits.MaxArtifactBytes, (long)staged.Capacity * 2));
                }

                staged.Write(buffer, 0, count);
                hash?.AppendData(buffer, 0, count);
            }

            if (hash is not null && !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected!))
            {
                throw new CryptographicException("The declared OpenUSD digest does not match the artifact bytes.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var bytes = staged.ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            return new OpenUsdArtifact(bytes, hash is not null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
