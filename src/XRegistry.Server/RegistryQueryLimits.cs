namespace XRegistry.Server;

/// <summary>Finite query evaluation, page preparation and retained-cursor budgets.</summary>
public sealed record RegistryQueryLimits
{
    /// <summary>Gets the maximum filter expressions across all AND/OR branches.</summary>
    public int MaxFilterExpressions { get; init; } = 128;
    /// <summary>Gets the maximum segments in one dot-notation path.</summary>
    public int MaxPathSegments { get; init; } = 32;
    /// <summary>Gets the maximum query comparison/traversal work units.</summary>
    public int MaxWork { get; init; } = 250_000;
    /// <summary>Gets the maximum distinct entity facts examined by one query.</summary>
    public int MaxEntities { get; init; } = 4096;
    /// <summary>Gets the maximum encoded entity-fact bytes retained during evaluation.</summary>
    public int MaxFactBytes { get; init; } = 8 * 1024 * 1024;
    /// <summary>Gets the maximum query preparation/authorization duration.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Gets the server's maximum and default number of records per page.</summary>
    public int MaxPageRecords { get; init; } = 128;
    /// <summary>Gets the maximum encoded JSON bytes in one page, including its enclosing map.</summary>
    public int MaxPageBytes { get; init; } = 1024 * 1024;
    /// <summary>Gets the maximum records retained for one cursor set.</summary>
    public int MaxCursorRecords { get; init; } = 2048;
    /// <summary>Gets the maximum charged bytes for one retained cursor set.</summary>
    public int MaxCursorBytes { get; init; } = 8 * 1024 * 1024;
    /// <summary>Gets the maximum simultaneously retained cursor sets per engine.</summary>
    public int MaxCursors { get; init; } = 32;
    /// <summary>Gets the maximum retained page tokens per engine.</summary>
    public int MaxCursorTokens { get; init; } = 4096;
    /// <summary>Gets the maximum retained records across all cursor sets.</summary>
    public int MaxTotalCursorRecords { get; init; } = 16_384;
    /// <summary>Gets the maximum charged retained bytes across all cursor sets.</summary>
    public int MaxTotalCursorBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>Gets the maximum authorization dependencies retained for one set.</summary>
    public int MaxAuthorizationPaths { get; init; } = 8192;
    /// <summary>Gets the fixed availability lifetime; reads do not extend it.</summary>
    public TimeSpan CursorLifetime { get; init; } = TimeSpan.FromMinutes(2);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFilterExpressions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPathSegments, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxPathSegments, 128);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxWork, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEntities, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFactBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPageRecords, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPageBytes, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCursorRecords, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCursorBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCursors, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCursorTokens, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTotalCursorRecords, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTotalCursorBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAuthorizationPaths, 1);
        if (MaxDuration <= TimeSpan.Zero || MaxDuration > TimeSpan.FromMinutes(1) ||
            CursorLifetime <= TimeSpan.Zero || CursorLifetime > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDuration), "Query duration and cursor lifetime must have finite supported bounds.");
        }
    }
}
