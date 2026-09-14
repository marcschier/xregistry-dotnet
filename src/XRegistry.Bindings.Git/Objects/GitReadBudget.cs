namespace XRegistry.Bindings.Git.Objects;

internal sealed class GitReadBudget(GitReadLimits limits, CancellationToken cancellationToken)
{
    private long expanded;
    private long symbols;
    private long blocks;
    private long deltaBytes;
    private long deltaInstructions;

    internal GitReadLimits Limits { get; } = limits;

    internal CancellationToken CancellationToken { get; } = cancellationToken;

    internal void AddExpanded(long count) => Charge(ref expanded, count, Limits.MaxTotalDecompressedBytes, "decompressed bytes");

    internal void AddSymbols(long count) => Charge(ref symbols, count, Limits.MaxInflateSymbols, "DEFLATE symbols");

    internal void AddBlock() => Charge(ref blocks, 1, Limits.MaxDeflateBlocks, "DEFLATE blocks");

    internal void AddDeltaWork(long count) => Charge(ref deltaBytes, count, Limits.MaxDeltaWorkBytes, "delta work bytes");

    internal void AddDeltaInstruction() => Charge(ref deltaInstructions, 1, Limits.MaxDeltaInstructions, "delta instructions");

    internal void CheckObjectSize(ulong length)
    {
        if (length > (ulong)Limits.MaxObjectBytes)
        {
            throw new GitDataException(GitFailure.LimitExceeded, "The Git object byte budget was exceeded.");
        }
    }

    internal static void Charge(ref long used, long count, long limit, string dimension)
    {
        if (count > limit - used)
        {
            throw new GitDataException(GitFailure.LimitExceeded, $"The Git {dimension} budget was exceeded.");
        }

        used += count;
    }
}
