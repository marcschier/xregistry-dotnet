using System.Security.Claims;
using XRegistry.Server;
using XRegistry.Storage.File;

namespace XRegistry.Sample.FileServer;

internal sealed class FileServerStore : IDisposable
{
    private readonly LocalFileStore _store;
    private readonly LocalRegistryPersistence _persistence;

    private FileServerStore(LocalFileStore store, LocalRegistryPersistence persistence, RegistryEngine engine)
    {
        _store = store;
        _persistence = persistence;
        Engine = engine;
    }

    internal RegistryEngine Engine { get; }
    internal long Generation => _store.ReadGeneration();

    internal static async ValueTask<FileServerStore> OpenAsync(
        string directory, bool initialize, RegistryEngineOptions options,
        IRegistryAuthorizationPolicy authorization, FileStoreLimits? limits = null, CancellationToken token = default)
    {
        directory = Path.GetFullPath(directory);
        if (initialize)
        {
            Directory.CreateDirectory(directory);
        }
        var store = initialize ? LocalFileStore.Initialize(directory, limits, token) : LocalFileStore.Open(directory, limits, token);
        var persistence = new LocalRegistryPersistence(store, ownsStore: true);
        var complete = false;
        try
        {
            if (!initialize && store.ReadGeneration(token) == 0)
            {
                throw new InvalidDataException("The storage directory has no committed Registry initialization; no empty Registry was substituted.");
            }
            var identity = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "local-initialization")], "local-initialization"));
            var setup = new RegistryEngine(options, persistence, new InitializationPolicy(identity));
            var context = new RegistryOperationContext(identity);
            if (initialize)
            {
                await setup.ExecuteAsync(new(RegistryAction.Replace, RegistryPath.Parse("/"))
                {
                    Metadata = options.InitialMetadata
                }, context, token).ConfigureAwait(false);
            }
            var existing = await setup.ExecuteAsync(
                new(RegistryAction.Read, RegistryPath.Parse("/")), context, token).ConfigureAwait(false);
            if (existing.Metadata!.RootElement.GetProperty("registryid").GetString() != options.RegistryId)
            {
                throw new InvalidOperationException("The configured Registry ID does not match the existing durable Registry.");
            }
            var result = new FileServerStore(store, persistence, new RegistryEngine(options, persistence, authorization));
            complete = true;
            return result;
        }
        finally
        {
            if (!complete)
            {
                persistence.Dispose();
            }
        }
    }

    internal ValueTask<long> BackupAsync(string emptyDestination, CancellationToken token) =>
        _store.CreateBackupAsync(Path.GetFullPath(emptyDestination), token);

    public void Dispose() => _persistence.Dispose();

    private sealed class InitializationPolicy(ClaimsPrincipal principal) : IRegistryAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(ClaimsPrincipal caller, RegistryAccess access, RegistryPath path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReferenceEquals(caller, principal));
        }
    }
}
