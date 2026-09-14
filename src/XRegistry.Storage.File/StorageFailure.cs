namespace XRegistry.Storage.File;

/// <summary>Identifies an explicit failure of the local storage contract.</summary>
public enum StorageFailure
{
    /// <summary>The directory does not contain the expected intact store.</summary>
    InvalidStore,
    /// <summary>First initialization did not finish; ordinary open cannot recover it.</summary>
    InitializationIncomplete,
    /// <summary>The platform, filesystem, or required durability operation is unsupported.</summary>
    UnsupportedStorage,
    /// <summary>Another owner holds the directory's exclusive writer lock.</summary>
    WriterUnavailable,
    /// <summary>Another operation occupies the store's bounded execution slot.</summary>
    Busy,
    /// <summary>The candidate's storage generation is no longer current.</summary>
    GenerationMismatch,
    /// <summary>A configured finite resource budget was exceeded.</summary>
    LimitExceeded,
    /// <summary>Stored metadata or document content failed integrity verification.</summary>
    IntegrityFailure,
    /// <summary>A commit was attempted but its acknowledgement could not be completed.</summary>
    CommitOutcomeUnknown,
    /// <summary>The instance must be disposed and reopened after a storage failure.</summary>
    UnusableStore,
}
