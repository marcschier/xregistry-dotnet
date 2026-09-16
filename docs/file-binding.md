# Native File sources and OCI layout publication

`FileDocumentTreeReader` supplies exact, caller-owned streams to
`DirectoryMapping` without running an HTTP server. The selected root uses an
absolute `file` URI with no remote authority, query, fragment or credentials.
Already OS-mounted shares can be addressed through a local path; remote UNC
authorities are not accepted by the pinned binding.

The direct `FileDocumentTreeReader.Open(Uri)` seam and `FileRegistrySource`
now use the same original-spelling locator validation. `System.Uri` repair of
malformed percent escapes cannot open an existing literal-percent directory.
Valid `%25` escapes are decoded once; a literal `%2e%2e` filename is not a
parent traversal. Original encoded dot aliases remain rejected even when a
`Uri` was constructed from a native path. Foreign native path forms and legacy
drive-bar spelling fail explicitly rather than selecting a different root.

Windows traversal holds ancestor handles without delete sharing and opens each
component with reparse-point checks. Linux traversal uses directory-relative
`openat` with no-follow flags and `statx` regular-file/type checks. It does not
normalize or URI-decode a mapping href into another path.

Every opened Windows root component, internal directory and file is checked
against its canonical handle-reported name. Internal comparison is anchored to
the captured canonical root, rather than a mutable drive-letter mapping; mapped
drive roots can therefore retain their File locator without treating a different
resolved tree as the same object. Case and short-name aliases cannot silently
select another spelling. Dedicated root/intermediate/file case regressions
supplement reparse and boundary tests.

The reader checks file size on the opened handle and verifies size/mtime again
on stream disposal. A prevented write remains prevented; an observed mutation
is an explicit `InconsistentSnapshot` failure. A missing file is null to the
mapping adapter; permissions, links, invalid types and exhausted limits are not
represented as missing files.

The mapping layer verifies descriptor length/SHA-256 before parsing or
returning content and distinguishes empty Documents from documentless Versions.
Its root hash is captured context, not proof that a live directory is an
immutable filesystem snapshot.

Captured Registry, Group, ordinary Meta and Version metadata is checked against
the compiled model before it is cached or returned, including conditional
required fields, scalar rules and owning-Group equals constraints. Document
requests perform the same metadata checks before opening domain bytes. The
storage representation's omitted navigation/configuration fields do not waive
validation of ordinary metadata. Filtered definitions are cached by effective
model identity; owning Group reads reuse the pinned metadata cache.

For `hasdocument: false`, ordinary model-admitted fields named like the Resource,
`<RESOURCE>base64` or `<RESOURCE>url` remain metadata. Their exact JSON and
literal nulls are retained without base64 decoding or URI acquisition, while
their declared types still apply. The storage descriptor remains `none` and a
Document request still fails as unsupported. This does not permit inline bytes
or a conflicting URL on a detached local Document of a document-bearing type.

A relative URI target constraint that needs an explicitly authorized Registry context fails
with `UnsupportedOperation`; reading the mapping does not authorize a network
fetch or establish that target's model. An absolute URI/URL is exempt from the
Core `target` aspect and does not cause implicit acquisition. System timestamps use Core's full
RFC3339 grammar, including leap seconds, permitted offsets and sub-tick
fractions, rather than a narrower `DateTimeOffset` parser.

Complete Version collections and Resource views check their default flags,
ancestry, retention, single-root and `matchversions` constraints before
returning. Exact Version reads verify the selected ancestor chain and the
complete index's retention bound. A model with cross-Version constraints
requires its sibling Version metadata too, never unrelated domain bytes.
Ancestry depth and work are bounded; matching numbers remain exact and
timestamps compare normalized instants without truncating their fractions.
The returned storage metadata retains its original representation.

`DirectoryMapping.ValidateAsync` explicitly walks the whole internal closure,
verifies every local Document including empty files, and returns the root hash,
closure class and counts of distinct metadata/local-document files. It does
not build a full JSON response, enumerate unreferenced project files, follow
catalog/provenance links or retrieve external-only domain Documents. Linked
snapshots can retain those external descriptors; offline-complete snapshots
reject them even during an ordinary metadata read that visits the descriptor.
Budget, context, cancellation and integrity failures return no partial
validation receipt. Ordinary selective reads do not prove unvisited closure.

Captured include material also uses `RegistryModel.CompileCaptured`: malformed
original directives and contradictions with known locally authored/imported
members fail before child reads. Original and resolved sources and `modelbase`
remain distinct capture context. No mutable include is re-fetched, and
unretained external include provenance is not claimed independently verified.

The reader tests include the three dot-alias regressions. Their Linux x64
Native AOT execution is reader evidence, not qualification of the local
publisher.

## Explicit source dispatch

`FileRegistrySource` implements `IFederationReadSource` and `IAsyncDisposable`.
It owns both a `FileDocumentTreeReader` and exactly one `DirectoryMapping` or
`OciSnapshot` session. It does not infer a format from files or fall back after
an error.

```csharp
await using var source = await FileRegistrySource.OpenAsync(
    endpoint: new Uri("file:///D:/snapshots/catalog/"),
    layout: FileRegistryLayout.OciLayout,
    reference: "release",
    authorizedRoot: new Uri("file:///D:/snapshots/"),
    budget: new FederationReadBudget(),
    cancellationToken: cancellationToken);

FederationReadResult result = await source.ReadAsync(
    new FederationReadRequest(
        FederationOperation.Document, "/dirs/main/files/sample"),
    cancellationToken);
```

The public factory overloads are:

```csharp
ValueTask<FileRegistrySource> OpenAsync(
    Uri endpoint, FileRegistryLayout layout, string? reference = null,
    Uri? authorizedRoot = null, FederationReadBudget? budget = null,
    CancellationToken cancellationToken = default);

ValueTask<FileRegistrySource> OpenAsync(
    Uri endpoint, string layout, string? reference = null,
    Uri? authorizedRoot = null, FederationReadBudget? budget = null,
    CancellationToken cancellationToken = default);

ValueTask<FileRegistrySource> OpenAsync(
    string endpoint, string layout, string? reference = null,
    Uri? authorizedRoot = null, FederationReadBudget? budget = null,
    CancellationToken cancellationToken = default);

ValueTask<FileRegistrySource> OpenProfileAsync(
    CatalogAdvertisement advertisement, Uri authorizedRoot,
    FederationReadBudget? budget = null,
    CancellationToken cancellationToken = default);
```

`FileRegistryLayout.DocumentTree` corresponds to exactly `document-tree` and
**forbids** a reference, including an empty or null-valued reference field in a
catalog entry. `FileRegistryLayout.OciLayout` corresponds to exactly
`oci-layout` and **requires** an explicit tag or digest. The string endpoint
overload requires an actual `file:` URI, not an implicitly interpreted native
path.

A direct factory invocation explicitly authorizes the selected root. The
optional `authorizedRoot` narrows it to an already approved enclosing directory;
`root-other` is not contained by `root`. The catalog helper always requires a
caller-supplied boundary: an advertisement never grants filesystem permission.
Original URI spellings, strict UTF-8 percent decoding, controls, separators,
dot/parent aliases, remote authorities, Windows streams/device names and
canonical Windows names are checked before binding bootstrap. Linux child
selection is relative to the opened authorization anchor; Windows retains the
existing no-delete-sharing ancestor pins.

`Context.Source` remains the selected File locator. Document-tree contexts
retain the root-byte hash without an immutable-filesystem claim. OCI contexts
retain the verified root digest in `Revision` and the original selector in
`RequestedRevision`. `Capabilities` and the non-null compiled `Model` come from
the selected binding. Results are the same complete shared federation
envelopes used by the parent resolver; OCI raw configs are not exposed as API
entities.

The raw File reader never claims an immutable filesystem snapshot. OCI's
`Context.IsImmutable` describes the verified content-addressed selected graph,
not an immutable filesystem volume or an atomic upstream capture. The public
layout and File locator remain separate from that root commitment. Both layout
modes retain a producer-owned federation capability without invoking a catalog
crawler.

For external-only OCI Documents, the shared read returns an unfetched
`ExternalDocument` with `{"kind":"external","uri":"..."}`, no `Document` bytes,
and the pinned File/OCI context plus resolved Version XID. This matches the
shared DirectoryMapping/HTTP source contract used by bridge redirects. Native
`OciSnapshot.ReadAsync` still reports `Unavailable` for external bytes; no File
or OCI operation implicitly fetches the locator.

One source accepts one active operation at a time. Reentry and disposal during
an active read are rejected. Disposal closes the binding, tree and authorization
anchor; already returned owned JSON and document bytes remain usable.

Generic catalog selection accepts both absolute path forms returned by
`System.Uri`: `/...` and Windows drive forms such as `C:/approved/`.
Four independent locator cases cover drive, localhost, mapped-drive and POSIX
advertisements. Selection still does not authorize a filesystem read; the File
helper validates original spelling and the explicit boundary before opening it.

## Explicit local OCI publication

`FileOciLayout.PublishAsync` is a **producer operation**, not mutation through
`IFederationReadSource`. It accepts an already coherent, validated
`OciSnapshotPackage`, writes actual local OCI files and returns only after the
entrypoint has completed the required durability barriers.

```csharp
FileOciLayoutPublicationResult receipt = await FileOciLayout.PublishAsync(
    package,
    destination: new Uri("file:///D:/snapshots/published/"),
    reference: "release",
    authorizedRoot: new Uri("file:///D:/snapshots/"),
    options: new FileOciLayoutPublicationOptions(),
    cancellationToken: cancellationToken);
```

Full signature:

```csharp
ValueTask<FileOciLayoutPublicationResult> PublishAsync(
    OciSnapshotPackage package, Uri destination, string reference,
    Uri? authorizedRoot = null,
    FileOciLayoutPublicationOptions? options = null,
    FederationReadBudget? budget = null,
    CancellationToken cancellationToken = default);
```

The destination must already be an ordinary authorized directory. Empty new
layouts and existing valid layouts are supported. The publisher creates only
its `blobs`/`sha256` directories, content-addressed files, OCI controls, a
persistent `.xregistry-oci.lock`, and individually named temporary files. It
never recursively removes a directory or garbage-collects old snapshot objects.

Merging a local OCI entrypoint retains unknown string-valued annotations,
including empty keys/values permitted by OCI's annotation rules. They remain
non-authoritative metadata; duplicate JSON keys, invalid annotation value types
and graph/entrypoint limits still fail.

Publication order:

1. Verify the selected local filesystem and real directory barriers, then
   acquire an exclusive OS-backed writer lease for the directory.
2. Read and validate existing layout controls, prepare the bounded entrypoint
   merge, and reject duplicate selected tags or capacity failures before
   updating any reference.
3. Stream each required object through exact size/SHA-256 verification into a
   private same-directory temporary file, flush it, and place it without
   replacement. Existing digest files are verified and flushed, never rewritten.
4. Flush the blob directories and root before publishing the selected reference.
   Recheck controls for observed changes outside the writer lease.
5. Flush the prepared entrypoint, atomically replace its name, then flush the
   selected directory and parent directory before acknowledging success.

Windows uses retained no-follow/no-delete-sharing directory anchors and
handle-relative NT rename. The Win32 rename wrapper does not support the needed
destination-directory handle contract. Linux uses `openat`, `mkdirat`,
`renameat2`, `unlinkat`, `statx`, `flock` and `fsync`. Object identity and mount
checks are performed on opened handles. Symlinks, reparse points, device objects
and multiply linked files are rejected.

The writable policy is intentionally narrower than File reads:

- Windows: a fixed local **NTFS** volume with working file/directory flushes.
- Linux: the **ext4** driver, confirmed against the opened mount's identity;
  network, overlay and memory filesystems are unsupported.
- Publication directories, files and the selected directory's parent must be
  on the same qualified filesystem/mount. Choose a directory *inside* a mounted
  filesystem, not its mountpoint.

An inability to establish those guarantees produces an explicit failure, not
a best-effort durability claim. No SQLite, Server or Storage package dependency
was introduced, and the Storage durability helper was not moved or refactored.
Writers must honor the directory lease. These guarantees do not prevent a
filesystem owner or privileged non-cooperating process from deliberately
corrupting its own files. Windows readers can also cause explicit sharing
failures rather than the writer bypassing their handle protections.

### Reference preservation and bounds

Other entry descriptors and top-level entrypoint annotations are preserved.
Updating one tag replaces exactly its selected entry atomically. Duplicate
entries for that tag are `Ambiguous`, even when their digests match; publication
does not silently repair them or choose another tag. Digest publication retains
an existing eligible root entry or adds an untagged entry when needed.

Both existing and resulting `index.json` are bounded to **256 descriptors and
1,048,576 exact UTF-8 bytes**. Existing whitespace counts toward input size.
The resulting merge is checked after encoding, not estimated from character
counts. `MaxEntryPointDescriptors` and `MaxEntryPointBytes` may lower those
bounds, not raise them. Old roots and unrelated OCI artifacts are retained but
not recursively acquired or revalidated; the newly published package's closure
is the operation's content guarantee.

`FileOciLayoutPublicationOptions.Limits` defaults to finite 16-MiB objects,
256-MiB cumulative bytes, 32,768 requests/objects and 4,000,000 work units.
Streaming buffers are 64 KiB; exact empty documents remain zero bytes and never
become the two-byte OCI placeholder. The default operation/observer timeout is
two minutes, configurable up to ten minutes. Native filesystem calls still
complete under the operating system's I/O contract.

The receipt exposes `Context`, `EntryPointSha256`, `ObjectsWritten` and
`ObjectsReused`. Its Registry root digest is distinct from the hash of mutable
`index.json`.

### Cancellation, faults and acknowledgement

`Checkpoint` is an optional trusted host callback at `WriterLocked`,
`ObjectsDurable`, `BeforeReferenceCommit`, `ReferenceReplaced` and
`ReferenceDurable`. It supports progress, cancellation and fault injection, but
does not replace any native durability operation. Artifact metadata is never
executed as a callback.

Cancellation before reference replacement propagates without acknowledging a
new reference; immutable unreferenced objects may remain. After a possibly
visible replacement, I/O failure, deadline or cancellation produces
`FederationException(Unavailable)` with diagnostic
`publication_ack_unknown`. It must not be interpreted as rollback.
`ReferenceDurable` occurs after the native barriers, and success follows that
checkpoint. A competing writer receives `Unavailable` / `writer_busy`.
Unsupported barriers/storage and observed control changes are explicit failures.
Only owned, still-temporary files are removed during cleanup.

## Qualification and bounded scope

The tests live in `FileRegistrySourceTests.cs` and
`FileOciLayoutPublicationTests.cs`.
Read tests use the independent frozen mapping and OCI corpora, not writer output
as their sole oracle. Actual publication is inspected through `OciSnapshot`
and the frozen `tools\oci_examples.py` subprocess for both linked and
offline-complete packages.

| Requirement | Public test evidence |
| --- | --- |
| Explicit modes and references, no fallback | `FrozenLayoutsDispatchExplicitly`, `ReferenceRulesAndUnknownLayoutsFailBeforeReads`, `SelectedLayoutNeverFallsBack` |
| Uniform unfetched external Document contract through File OCI dispatch | `LinkedOciFileSourceReturnsTheSharedUnfetchedDocumentDescriptor` |
| Caller authorization and original path safety | `AuthorizedBoundaryIsComponentAware`, `CatalogAdvertisementsRequireAnExplicitAuthorizedBoundary`, `UnsafeOriginalLocatorsCannotBecomeNormalizedAuthorizedPaths`, `AuthorizedRootsAndSelectedRootsNeverFollowReparseOrSymbolicLinks` |
| Owned lifetimes and error cleanup | `OwnedSourceAndReturnedResultsHaveSeparateLifetimes`, `FailedBootstrapClosesBothOwnedRootAndBoundaryAnchors` |
| Actual durable local publication and exact bytes | `FreshLayoutPublishesExactBytesAndDurableReference`, `IndependentPythonOracleAcceptsActuallyPublishedLayout` |
| No overwrite of bad content-addressed files | `ExistingCorruptDigestBytesAreNeverOverwrittenOrPublished` (wrong size and same-size corruption) |
| Retained references and exclusive writer | `ExistingReferencesArePreservedAndSameReferenceMovesAtomically`, `OneDirectoryHasOneActiveWriter`, `UnrelatedEntryMetadataAndTopLevelAnnotationsArePreservedWithoutAcquisition` |
| Exact input/output entrypoint bounds | `EntryPointDescriptorLimitPreservesAll256EntriesAndRejectsTheNextReference`, `ExistingEntryPointExactByteLimitIncludesWhitespace`, `OutputByteLimitIsCheckedAfterMergeAndBeforeAnyReferenceUpdate` |
| Failure, cancellation and unknown acknowledgement | `InjectedFailuresRetainOldSelectionOrReportUnknownAcknowledgement`, `CancellationBeforeAndAfterReferenceReplacementHasDistinctOutcomes`, `ObservedControlChangesBeforeCommitCannotBeOverwritten`, `PrecancelledPublicationAndExpiredCheckpointCannotAcknowledgeAReference` |

Build outputs are isolated from shared project PDBs:

The current full File suite passed **73/73 on Release net8.0, 73/73 on Release
net10.0, and 73/73 in the net10.0 win-x64 Native AOT executable**, with zero
failures or skips. This includes all 20 original reader cases and 53 new
integration/publication cases. The native PE has no CLR directory. Scoped
formatting verification also passed.

```powershell
dotnet test --project tests\XRegistry.File.Tests\XRegistry.File.Tests.csproj -c Release -f net8.0 --artifacts-path artifacts\file-integration-build --no-ansi --results-directory artifacts\file-integration-build\results\net8
dotnet test --project tests\XRegistry.File.Tests\XRegistry.File.Tests.csproj -c Release -f net10.0 --artifacts-path artifacts\file-integration-build --no-ansi --results-directory artifacts\file-integration-build\results\net10

$env:VSInstallerPath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$env:PATH = "$env:VSInstallerPath;$env:PATH"
dotnet publish tests\XRegistry.File.Tests\XRegistry.File.Tests.csproj -c Release -f net10.0 -r win-x64 -p:PublishAot=true --artifacts-path artifacts\file-integration-build -o artifacts\file-integration-build\native\win-x64
& .\artifacts\file-integration-build\native\win-x64\XRegistry.File.Tests.exe --no-ansi --results-directory artifacts\file-integration-build\results\native-win-x64
```

The Windows native evidence executes real NTFS barriers; it does
not claim physical power-loss testing, network/overlay durability, or executed
Linux/ARM64 qualification of the publisher. Destination-directory creation
and complete sample composition remain host responsibilities.

### Linux x64 execution

The current relocated File suite passed 75/75 cases on .NET 10 Linux x64
Native AOT, including the actual local OCI publisher on an isolated ext4
volume. The non-root container had a read-only root, no external network, and
no installed .NET runtime. Its published output carries the unchanged mapping,
OCI and Python-oracle fixtures instead of finding a sibling checkout.
ARM64, mapped-share and physical power-loss qualification are not covered by
this evidence; see the [roadmap](roadmap.md#durability-and-performance).
