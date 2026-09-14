using System.Net;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using XRegistry;
using XRegistry.Http;
using XRegistry.Models;
using XRegistry.Server;
using XRegistry.Validation;

namespace EmbeddingConsumer;

internal static class Program
{
    private const string GroupPath = "workspaces/Floor%3A01";
    private const string ResourcePath = GroupPath + "/artifacts/asset%401";
    private const string DocumentHex = "00FF0D0A0180007B2278223A317DFE";
    private const string ModelSource = """
        {
          "attributes":{"deployment":{"type":"string"}},
          "groups":{"workspaces":{"singular":"workspace",
            "attributes":{"location":{"type":"string"},"settings":{"type":"object","attributes":{
              "enabled":{"type":"boolean"},"owner":{"type":"string"}}}},
            "resources":{"artifacts":{"singular":"artifact","hasdocument":true,
              "attributes":{"vendor":{"type":"string"}}}}
          }}
        }
        """;

    private static async Task<int> Main(string[] args)
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 == args.Length || !arguments.TryAdd(args[index], args[index + 1]))
            {
                throw new ArgumentException("Expected unique --report, --rid and --assemblies arguments.");
            }
        }
        var report = new EmbeddingReport(arguments["--rid"]);
        var reportPath = arguments["--report"];
        if (!report.IsNative)
        {
            report.Outcome = "jit-rejected";
            report.Write(reportPath);
            Console.Error.WriteLine("EMBEDDING_JIT_REJECTED: runtime feature flags and the measured JIT method count must prove native execution.");
            return 2;
        }

        EmbeddingHost? host = null;
        using var store = new ApplicationRecordStore();
        using var persistence = new ApplicationRegistryPersistence(store);
        using var readOnly = new ApplicationRegistryPersistence(store, readOnly: true);
        var authorization = new EmbeddingAuthorizationPolicy();
        try
        {
            var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" : "unsupported";
            Require(arguments["--rid"] == os + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                "native host RID");
            await report.CaseAsync("package-assets-no-opc", () =>
            {
                report.InspectAssemblies(arguments["--assemblies"]);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            var model = RegistryModel.Compile(RegistryJson.Parse(ModelSource));
            await report.CaseAsync("model-path-and-header-codec", () =>
            {
                Require(model.Groups["workspaces"].Resources["artifacts"].HasDocument, "custom model-defined Resource");
                var id = RegistryId.Parse("Floor:01");
                var path = RegistryPath.Parse("/" + ResourcePath + "$details");
                Require(path.GroupId == id && path.ResourceId!.Value == "asset@1" && path.IsDetails, "exact protocol identities");
                Require(RegistryHeaderEncoding.Encode("\u00e9\n%") == "%C3%A9%0A%25", "canonical header encoding");
                Require(RegistryHeaderEncoding.Decode("%C3%A9%0A%25") == "\u00e9\n%", "single-pass header decoding");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            await report.CaseAsync("packaged-model-and-validator", async () =>
            {
                using var core = BuiltInRegistryModels.LoadSource(RegistryModelKind.Core);
                Require(core.RootElement.GetProperty("attributes").GetProperty("specversion").GetProperty("default")
                    .GetString() == "1.0-rc4", "packaged Core model");
                var validator = new BuiltInDocumentValidator();
                var valid = await validator.ValidateAsync("JsonSchema/draft/2020-12", """{"type":"object"}"""u8.ToArray())
                    .ConfigureAwait(false);
                var invalid = await validator.ValidateAsync("JsonSchema/draft/2020-12", """{"type":42}"""u8.ToArray())
                    .ConfigureAwait(false);
                Require(valid.Status == DocumentValidationStatus.Valid && invalid.Status == DocumentValidationStatus.Invalid,
                    "schema format validation is not merely JSON parsing");
                var proto = """syntax="proto3"; package telemetry; message Event { string id=1; }"""u8.ToArray();
                const string reference = "#/schemagroups/team:prod/schemas/events/versions/v:1";
                var concrete = await SchemaObjectSelector.SelectAsync("Protobuf/3", proto,
                    reference + ":telemetry.Event", new() { DocumentReference = reference }).ConfigureAwait(false);
                Require(concrete.Status == SchemaObjectSelectionStatus.Selected &&
                    concrete.Selection is { TypeName: "telemetry.Event" } selectedSchema &&
                    selectedSchema.Document.Span.SequenceEqual(proto) &&
                    selectedSchema.DocumentReference == reference,
                    "packaged concrete schema selection retains the full document and raw colon-bearing reference");
                var missingSelector = await SchemaObjectSelector.SelectAsync("Protobuf/3", proto,
                    reference, new() { DocumentReference = reference }).ConfigureAwait(false);
                Require(missingSelector.Status == SchemaObjectSelectionStatus.Invalid && missingSelector.Selection is null,
                    "a single Protobuf message does not waive the required concrete selector");
                var messageModel = BuiltInRegistryModels.Compile(RegistryModelKind.Message);
                var messageRoot = new Uri("https://registry.example/registry/");
                var baseMessage = new MessageDefinition(RegistryJson.Parse("""
                    {"description":"base","envelope":"CloudEvents/1.0",
                     "envelopemetadata":{"type":{"value":"example.created"}}}
                    """), new Uri(messageRoot, "messagegroups/shared/messages/base"), messageRoot, messageModel);
                var derivedMessage = new MessageDefinition(RegistryJson.Parse("""
                    {"basemessage":"/messagegroups/shared/messages/base","description":"derived",
                     "protocol":"HTTP","protocoloptions":{"method":"POST"}}
                    """), new Uri(messageRoot, "messagegroups/http/messages/created"), messageRoot, messageModel);
                var acquiredMessages = 0;
                var materialized = await MessageDefinitionMaterializer.MaterializeAsync(derivedMessage, (request, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    Require(request.TargetUri == baseMessage.Location, "only the explicitly authorized base is acquired");
                    acquiredMessages++;
                    return ValueTask.FromResult(MessageDefinitionSourceResult.Found(baseMessage));
                }).ConfigureAwait(false);
                Require(materialized.IsComplete && materialized.Definitions.Count == 2 && acquiredMessages == 1 &&
                    materialized.Metadata.RootElement.GetProperty("description").GetString() == "derived" &&
                    materialized.Metadata.RootElement.GetProperty("envelopemetadata").GetProperty("type")
                        .GetProperty("value").GetString() == "example.created" &&
                    materialized.Metadata.RootElement.GetProperty("protocoloptions").GetProperty("method").GetString() == "POST" &&
                    materialized.PropertySources["/envelopemetadata/type/value"].Location == baseMessage.Location,
                    "packaged Message inheritance keeps exact overrides and source-relative property provenance");
                var incomplete = await MessageDefinitionMaterializer.MaterializeAsync(derivedMessage, static (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(MessageDefinitionSourceResult.Unresolved(MessageDefinitionSourceStatus.AccessDenied));
                }).ConfigureAwait(false);
                Require(!incomplete.IsComplete && incomplete.UnresolvedStatus == MessageDefinitionSourceStatus.AccessDenied &&
                    incomplete.Definitions.Count == 1, "an unavailable base does not become a complete empty definition");
                var headerDefinition = RegistryDomainRules.CompleteMessageMetadata(RegistryJson.Parse("""
                    {"protocol":"HTTP","protocoloptions":{"headers":[
                      {"name":"X-Count","type":"stringified integer","value":"-42","specurl":"../headers/count"}]}}
                    """));
                var declaredHeader = headerDefinition.RootElement.GetProperty("protocoloptions").GetProperty("headers")[0];
                Require(declaredHeader.GetProperty("type").GetString() == "stringified integer" &&
                    declaredHeader.GetProperty("value").GetString() == "-42" &&
                    !declaredHeader.GetProperty("required").GetBoolean() &&
                    declaredHeader.GetProperty("specurl").GetString() == "../headers/count",
                    "packaged header declarations retain native textual refinements and common defaults");
                RegistryDomainRules.ValidateMessageBaseReference(
                    RegistryJson.Parse("""{"basemessage":"/messagegroups/missing/messages/base/versions/v1"}""").RootElement,
                    messageModel);
                var endpoint = EndpointDefinition.Materialize(RegistryJson.Parse("""
                    {"usage":["producer"],"protocol":"HTTP","protocoloptions":{
                      "endpoints":[{"uri":"https://api.example/{tenant}/events"}]}}
                    """), RegistryJson.Parse("""{"tenant":"west/east"}"""));
                Require(endpoint.GetProtocolOption("endpoints")[0].GetProperty("uri").GetString() ==
                    "https://api.example/west%2Feast/events" &&
                    endpoint.Authored.RootElement.GetProperty("protocoloptions").GetProperty("endpoints")[0]
                        .GetProperty("uri").GetString() == "https://api.example/{tenant}/events",
                    "packaged Endpoint Level-1 materialization preserves authored values separately");
                var identifier = OpenUsdIdentifiers.NormalizeAssetIdentifier("./textures/a.png", 64);
                Require(identifier == "textures/a.png" &&
                    OpenUsdIdentifiers.CreateSymbolicIdCandidate(identifier, 64) == "textures.a.png",
                    "packaged OpenUSD identifier construction");
                const string firstSource = "https://example.test/asset?source=one";
                const string secondSource = "https://example.test/asset?source=two";
                var candidate = OpenUsdIdentifiers.CreateSymbolicIdCandidate(firstSource, 128);
                var assigned = OpenUsdIdentifiers.AssignSymbolicId(secondSource,
                    new Dictionary<string, string> { [candidate] = firstSource }, 512, 1);
                var suffix = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(secondSource)))
                    .ToLowerInvariant()[..8];
                Require(assigned == candidate + "." + suffix &&
                    OpenUsdIdentifiers.AssignSymbolicId(secondSource,
                        new Dictionary<string, string> { [assigned] = secondSource }, 512, 1) == assigned,
                    "packaged OpenUSD collision assignment is exact-source-derived and retains a published fallback");
                using var content = new MemoryStream("abc"u8.ToArray(), false);
                var artifact = await OpenUsdArtifactIntegrity.ReadAsync(content,
                    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                    null, OpenUsdDigestRole.Consumer, new(3, 2)).ConfigureAwait(false);
                using var verified = artifact.OpenRead();
                Require(artifact.IsDigestVerified && artifact.Length == 3 && verified.ReadByte() == (byte)'a' && content.CanRead,
                    "packaged OpenUSD digest fallback and owned verified bytes");
                content.Position = 0;
                var mismatchRejected = false;
                try
                {
                    await OpenUsdArtifactIntegrity.ReadAsync(content, new string('0', 64), "Sha256",
                        OpenUsdDigestRole.Consumer, new(3, 2)).ConfigureAwait(false);
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    mismatchRejected = true;
                }
                Require(mismatchRejected && content.CanRead, "packaged OpenUSD digest mismatch is not success");
            }).ConfigureAwait(false);

            host = await EmbeddingHost.StartAsync(model, persistence, readOnly, authorization).ConfigureAwait(false);
            using var writer = host.Client("writer");
            using var reader = host.Client("reader");
            using var anonymous = host.Client("anonymous");
            var documentBytes = Convert.FromHexString(DocumentHex);
            var resourceModel = model.Groups["workspaces"].Resources["artifacts"];
            var documentMetadata = RegistryJson.Parse("""
                {"contenttype":"application/octet-stream","vendor":"\u00e9 100%","labels":{"stage.type":"prod east"}}
                """);
            JsonDocument? retainedMetadata = null;
            RegistryResult? retainedResult = null;
            try
            {
                await report.CaseAsync("anonymous-and-header-identity-denied", async () =>
                {
                    var before = persistence.Preparations;
                    using var rejected = await anonymous.SendAsync(HttpMethod.Get).ConfigureAwait(false);
                    Require(rejected.StatusCode == HttpStatusCode.Unauthorized &&
                        rejected.Headers.WwwAuthenticate.Single().Scheme == "Bearer", "anonymous challenge");
                    using var raw = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
                    using var forged = new HttpRequestMessage(HttpMethod.Get, new Uri(host.Origin, "/registry/"));
                    forged.Headers.Add("X-User", "embedding-writer");
                    forged.Headers.Add("X-User-Roles", "writer");
                    using var denied = await raw.SendAsync(forged).ConfigureAwait(false);
                    Require(denied.StatusCode == HttpStatusCode.Unauthorized && persistence.Preparations == before &&
                        store.Publications == 0, "header identity cannot mutate or authenticate");
                }).ConfigureAwait(false);
                await report.CaseAsync("arbitrary-metadata-and-caller-identity", async () =>
                {
                    using var response = await writer.SendAsync(HttpMethod.Put, GroupPath,
                        Encoding.UTF8.GetBytes("""{"location":"Assembly A","settings":{"enabled":true,"owner":"application-owned"}}"""),
                        "application/json").ConfigureAwait(false);
                    Require(response.StatusCode == HttpStatusCode.Created, "custom Group creation");
                    using var created = await response.ReadMetadataAsync().ConfigureAwait(false);
                    Require(created.RootElement.GetProperty("workspaceid").GetString() == "Floor:01", "Group ID spelling");
                    using var read = await reader.SendAsync(HttpMethod.Get, GroupPath).ConfigureAwait(false);
                    Require(read.StatusCode == HttpStatusCode.OK, "authenticated read");
                    retainedMetadata = await read.ReadMetadataAsync().ConfigureAwait(false);
                    Require(retainedMetadata.RootElement.GetProperty("location").GetString() == "Assembly A" &&
                        retainedMetadata.RootElement.GetProperty("settings").GetProperty("enabled").GetBoolean() &&
                        retainedMetadata.RootElement.GetProperty("settings").GetProperty("owner").GetString() == "application-owned",
                        "arbitrary model metadata roundtrip");
                    Require(authorization.Observations.Any(static observation => observation.Subject == "embedding-writer") &&
                        authorization.Observations.Any(static observation => observation.Subject == "embedding-reader" &&
                            observation.Access == RegistryAccess.Read), "trusted caller identities reach the explicit policy");
                }).ConfigureAwait(false);
                await report.CaseAsync("single-dispatch-single-publication", async () =>
                {
                    var requests = host.HttpRequests;
                    var preparations = persistence.Preparations;
                    var publications = store.Publications;
                    using var upload = new MemoryStream(documentBytes, writable: false);
                    using var response = await writer.SendDocumentAsync(HttpMethod.Put, ResourcePath, upload,
                        documentMetadata, resourceModel).ConfigureAwait(false);
                    Require(response.StatusCode == HttpStatusCode.Created, "Document creation");
                    var headers = response.ReadHeaderMetadata(resourceModel).RootElement;
                    Require(response.BodyBytesRead == 0, "header decoding does not consume Document bytes");
                    Require(headers.GetProperty("vendor").GetString() == "\u00e9 100%", "typed Unicode/percent metadata header");
                    Require(headers.GetProperty("labels").GetProperty("stage.type").GetString() == "prod east",
                        "model-aware map key and value");
                    Require(headers.GetProperty("artifactid").GetString() == "asset@1", "readonly Resource identity header");
                    Require(headers.GetProperty("versionid").GetString() == "1", "readonly initial Version identity header");
                    Require(headers.GetProperty("epoch").GetRawText() == "0", "exact initial Version epoch header");
                    using var echo = new MemoryStream();
                    await response.CopyDocumentToAsync(echo).ConfigureAwait(false);
                    Require(echo.ToArray().AsSpan().SequenceEqual(documentBytes), "actual upload response bytes");
                    Require(host.HttpRequests == requests + 1 && persistence.Preparations == preparations + 1 &&
                        store.Publications == publications + 1, "one HTTP dispatch and one atomic provider publication");
                    Require(upload.CanRead && upload.Position == upload.Length, "the HTTP client leaves the upload stream owned by its caller");
                    report.DocumentBytesVerified += echo.Length;
                }).ConfigureAwait(false);
                await report.CaseAsync("exact-document-and-upload-ownership", async () =>
                {
                    using var response = await reader.SendAsync(HttpMethod.Get, ResourcePath).ConfigureAwait(false);
                    var metadata = response.ReadHeaderMetadata(resourceModel);
                    Require(metadata.RootElement.GetProperty("vendor").GetString() == "\u00e9 100%" &&
                        response.BodyBytesRead == 0, "model-aware response metadata is independent of the Document body");
                    using var output = new MemoryStream();
                    await response.CopyDocumentToAsync(output).ConfigureAwait(false);
                    Require(response.StatusCode == HttpStatusCode.OK && response.BodyBytesRead == documentBytes.Length &&
                        output.ToArray().AsSpan().SequenceEqual(documentBytes), "exact binary HTTP Document roundtrip");
                    response.Dispose();
                    Require(output.CanWrite, "response disposal does not dispose the caller destination");
                    report.DocumentBytesVerified += output.Length;
                }).ConfigureAwait(false);
                await report.CaseAsync("metadata-document-separation", async () =>
                {
                    using var update = await writer.SendAsync(HttpMethod.Patch, ResourcePath + "$details",
                        """{"vendor":"independent-domain-metadata"}"""u8.ToArray(), "application/json").ConfigureAwait(false);
                    Require(update.StatusCode == HttpStatusCode.OK, "metadata-only update");
                    using var read = await reader.SendAsync(HttpMethod.Get, ResourcePath + "$details").ConfigureAwait(false);
                    using var details = await read.ReadMetadataAsync().ConfigureAwait(false);
                    Require(details.RootElement.GetProperty("vendor").GetString() == "independent-domain-metadata" &&
                        details.RootElement.GetProperty("artifactid").GetString() == "asset@1", "Resource metadata and identity");
                    using var raw = await reader.SendAsync(HttpMethod.Get, ResourcePath).ConfigureAwait(false);
                    using var bytes = new MemoryStream();
                    await raw.CopyDocumentToAsync(bytes).ConfigureAwait(false);
                    Require(bytes.ToArray().AsSpan().SequenceEqual(documentBytes), "metadata changes preserve the Document");
                    report.DocumentBytesVerified += bytes.Length;
                }).ConfigureAwait(false);
                await report.CaseAsync("authenticated-reader-write-denied", async () =>
                {
                    var before = persistence.Preparations;
                    var generation = store.Read().Generation;
                    using var denied = await reader.SendAsync(HttpMethod.Patch, GroupPath,
                        """{"location":"must-not-publish"}"""u8.ToArray(), "application/json").ConfigureAwait(false);
                    Require(denied.StatusCode == HttpStatusCode.Forbidden && persistence.Preparations == before &&
                        store.Read().Generation == generation, "read permissions do not imply write permission");
                }).ConfigureAwait(false);
                await report.CaseAsync("unsupported-mutation-before-preparation", async () =>
                {
                    var before = persistence.Preparations;
                    var generation = store.Read().Generation;
                    using var denied = await writer.SendAsync(HttpMethod.Delete, "model").ConfigureAwait(false);
                    using var problem = await denied.ReadMetadataAsync().ConfigureAwait(false);
                    Require(denied.StatusCode == HttpStatusCode.MethodNotAllowed &&
                        problem.RootElement.GetProperty("code").GetString() == "action_not_supported" &&
                        persistence.Preparations == before && store.Read().Generation == generation,
                        "unsupported mutation is rejected before application preparation");
                }).ConfigureAwait(false);
                await report.CaseAsync("readonly-provider-before-access", async () =>
                {
                    using var frozen = host.Client("writer", "frozen");
                    var reads = readOnly.SnapshotReads;
                    var generation = store.Read().Generation;
                    using var denied = await frozen.SendAsync(HttpMethod.Patch, GroupPath,
                        """{"location":"must-not-publish"}"""u8.ToArray(), "application/json").ConfigureAwait(false);
                    using var problem = await denied.ReadMetadataAsync().ConfigureAwait(false);
                    Require((int)denied.StatusCode >= 400 && problem.RootElement.GetProperty("code").GetString() == "readonly" &&
                        readOnly.SnapshotReads == reads && readOnly.Preparations == 0 && store.Read().Generation == generation,
                        "read-only mutation rejection precedes even provider snapshot access");
                }).ConfigureAwait(false);
                await report.CaseAsync("direct-operation-caller-stream-ownership", async () =>
                {
                    var requests = host.HttpRequests;
                    using var input = new MemoryStream(documentBytes, writable: false);
                    retainedResult = await host.Engine.ExecuteAsync(new RegistryOperation(RegistryAction.Replace,
                        RegistryPath.Parse("/" + GroupPath + "/artifacts/direct"))
                    {
                        Document = input,
                        ContentType = "application/octet-stream"
                    }, new RegistryOperationContext(EmbeddingCredentials.Principal("writer"))).ConfigureAwait(false);
                    Require(input.CanRead && input.Position == input.Length && host.HttpRequests == requests &&
                        retainedResult.Document!.Length == documentBytes.Length, "direct operations need no HTTP workflow and preserve caller stream ownership");
                }).ConfigureAwait(false);
                await report.CaseAsync("staged-publication-conflict-and-cancellation", PersistencePublicationAsync).ConfigureAwait(false);
                await report.CaseAsync("snapshot-lease-lifetimes", async () =>
                {
                    using var local = new ApplicationRecordStore();
                    using var adapter = new ApplicationRegistryPersistence(local);
                    using var input = new MemoryStream(documentBytes, writable: false);
                    using var staged = await adapter.PrepareAsync(0,
                        [RegistryMutation.PutDocument("application/record", RegistryJson.Parse("""{"owned":true}"""), input)]).ConfigureAwait(false);
                    Require(input.CanRead && local.Read().Generation == 0, "prepare stages without taking input ownership or publishing");
                    input.Dispose();
                    await staged.CommitAsync().ConfigureAwait(false);
                    using var snapshot = await adapter.ReadSnapshotAsync().ConfigureAwait(false);
                    var record = snapshot.Find("application/record")!;
                    using var lease = snapshot.OpenDocument("application/record");
                    snapshot.Dispose();
                    adapter.Dispose();
                    local.Dispose();
                    using var copy = new MemoryStream();
                    await lease.CopyToAsync(copy).ConfigureAwait(false);
                    Require(record.Metadata.RootElement.GetProperty("owned").GetBoolean() &&
                        copy.ToArray().AsSpan().SequenceEqual(documentBytes), "owned record and Document lease survive all publisher lifetimes");
                    report.DocumentBytesVerified += copy.Length;
                }).ConfigureAwait(false);
                await report.CaseAsync("results-outlive-host-and-store", async () =>
                {
                    await host.DisposeAsync().ConfigureAwait(false);
                    Require(!persistence.IsDisposed && !readOnly.IsDisposed && !store.IsDisposed,
                        "MapXRegistry/RegistryEngine do not own application persistence");
                    using var stillOwned = await persistence.ReadSnapshotAsync().ConfigureAwait(false);
                    Require(stillOwned.Generation >= 4, "application storage remains available after HTTP shutdown");
                    writer.Dispose();
                    reader.Dispose();
                    persistence.Dispose();
                    readOnly.Dispose();
                    store.Dispose();
                    Require(retainedMetadata!.RootElement.GetProperty("location").GetString() == "Assembly A",
                        "metadata is independent of response/client/host lifetimes");
                    using var content = retainedResult!.Document!.OpenRead();
                    using var copy = new MemoryStream();
                    await content.CopyToAsync(copy).ConfigureAwait(false);
                    Require(copy.ToArray().AsSpan().SequenceEqual(documentBytes), "core result owns its Document after shutdown");
                    report.DocumentBytesVerified += copy.Length;
                }).ConfigureAwait(false);
            }
            finally
            {
                retainedMetadata?.Dispose();
            }
            await EmbeddingRuntimeCases.RunAsync(report, arguments["--fixtures"], arguments["--work"], model).ConfigureAwait(false);
            Require(report.Cases.Count == 25 && JitInfo.GetCompiledMethodCount() == 0, "exact native case count");
            report.Outcome = "passed";
            return 0;
        }
        catch (Exception exception)
        {
            report.Failure = exception.GetType().Name + ": " + exception.Message;
            Console.Error.WriteLine("EMBEDDING_FAILED: " + report.Failure);
            return 1;
        }
        finally
        {
            if (host is not null)
            {
                report.HttpRequests = host.HttpRequests;
                report.Requests.AddRange(host.Requests);
                await host.DisposeAsync().ConfigureAwait(false);
            }
            report.CommittedBatches = store.Publications;
            report.Write(reportPath);
        }
    }

    private static async Task PersistencePublicationAsync()
    {
        using var local = new ApplicationRecordStore();
        using var adapter = new ApplicationRegistryPersistence(local);
        var metadata = RegistryJson.Parse("""{"application":"opaque"}""");
        using var first = await adapter.PrepareAsync(0,
            [RegistryMutation.Put("batch/a", metadata), RegistryMutation.Put("batch/b", metadata)]).ConfigureAwait(false);
        using var stale = await adapter.PrepareAsync(0, [RegistryMutation.Put("batch/stale", metadata)]).ConfigureAwait(false);
        using (var before = await adapter.ReadSnapshotAsync().ConfigureAwait(false))
        {
            Require(before.Generation == 0 && !before.EnumerateRecords().Any(), "uncommitted candidates remain private");
        }
        Require(await first.CommitAsync().ConfigureAwait(false) == 1, "single publication generation");
        await ExpectAsync<RegistryConcurrencyException>(async () => await stale.CommitAsync().ConfigureAwait(false)).ConfigureAwait(false);
        using var cancelled = await adapter.PrepareAsync(1, [RegistryMutation.Put("batch/cancelled", metadata)]).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(async () => await cancelled.CommitAsync(cancellation.Token).ConfigureAwait(false))
            .ConfigureAwait(false);
        using var after = await adapter.ReadSnapshotAsync().ConfigureAwait(false);
        Require(after.Generation == 1 && after.GetChildren("batch").Count() == 2 &&
            after.Find("batch/a") is not null && after.Find("batch/b") is not null &&
            after.Find("batch/stale") is null && after.Find("batch/cancelled") is null, "conflict/cancellation publish nothing");
    }

    internal static void Require(bool condition, string invariant)
    {
        if (!condition) { throw new InvalidOperationException("Failed invariant: " + invariant); }
    }

    private static async Task ExpectAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action().ConfigureAwait(false); }
        catch (TException) { return; }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
    }
}
