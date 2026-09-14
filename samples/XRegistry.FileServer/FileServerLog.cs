namespace XRegistry.Sample.FileServer;

internal static class FileServerLog
{
    internal static readonly Action<ILogger, Exception?> StorageHealthFailure = LoggerMessage.Define(
        LogLevel.Error, new EventId(1, nameof(StorageHealthFailure)), "The Registry storage health check failed.");
}
