namespace XRegistry.Bindings.Git.Protocol;

/// <summary>Packet kinds; combinations specify the kinds permitted by a decoding phase.</summary>
[Flags]
public enum GitPacketKind
{
    /// <summary>No packet kinds.</summary>
    None = 0,
    /// <summary>A data packet, including the distinct empty packet 0004.</summary>
    Data = 1,
    /// <summary>The flush packet 0000.</summary>
    Flush = 2,
    /// <summary>The protocol-v2 delimiter 0001.</summary>
    Delimiter = 4,
    /// <summary>The protocol-v2 response terminator 0002.</summary>
    ResponseEnd = 8,
    /// <summary>All defined packet kinds.</summary>
    All = Data | Flush | Delimiter | ResponseEnd,
}

/// <summary>An immutable packet owning its payload; control packets have no payload.</summary>
public sealed class GitPacket
{
    private readonly byte[] payload;

    internal GitPacket(GitPacketKind kind, byte[] payload)
    {
        Kind = kind;
        this.payload = payload;
    }

    /// <summary>Gets the single kind represented by this packet.</summary>
    public GitPacketKind Kind { get; }

    /// <summary>Gets the exact payload, without the four-byte framing header.</summary>
    public ReadOnlySpan<byte> Payload => payload;

    /// <summary>Encodes one bounded packet using lowercase hexadecimal framing.</summary>
    public static byte[] Encode(GitPacketKind kind, ReadOnlySpan<byte> payload = default)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, GitPacketDecoder.MaximumPacketLength - 4);
        var wireLength = kind switch
        {
            GitPacketKind.Data => payload.Length + 4,
            GitPacketKind.Flush => 0,
            GitPacketKind.Delimiter => 1,
            GitPacketKind.ResponseEnd => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (kind != GitPacketKind.Data && !payload.IsEmpty)
        {
            throw new ArgumentException("Control packets cannot carry a payload.", nameof(payload));
        }

        var encoded = new byte[payload.Length + 4];
        for (var index = 3; index >= 0; index--)
        {
            encoded[index] = "0123456789abcdef"u8[wireLength & 15];
            wireLength >>= 4;
        }

        payload.CopyTo(encoded.AsSpan(4));
        return encoded;
    }
}
