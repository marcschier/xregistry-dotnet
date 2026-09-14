# Durable file-store foundation

`LocalFileStore` is an opaque-record persistence primitive, not a Registry
lifecycle engine. It stores owning metadata JSON and optional exact Documents
under one atomic storage generation; that generation is not an entity epoch.

The writer requires an existing, privately administered empty directory for
`Initialize`. `Open` never creates missing initialized state. Identity/ready
markers, SQLite schema identity and referenced blob integrity are checked.
Interrupted first initialization has a separate explicit recovery operation
that cannot relabel a populated store.

`PrepareAsync` validates all metadata/keys before reading supplied Documents,
copies metadata, stages bounded Document bytes, and retains caller ownership of
input streams. `Commit` checks the expected generation under the database write
transaction, places flushed immutable blobs without replacement, applies
directory durability barriers, and commits metadata/blob references and the
new generation atomically. An uncertain commit faults the instance; it is not
an automatic retry opportunity.

Snapshots own their metadata and pin Document references. Open Document streams
hold independent leases, including across snapshot/store disposal. Explicit
bounded orphan collection preserves committed, prepared and leased content and
never deletes arbitrary foreign filenames.

`ReadSnapshot(key)` uses the SQLite primary-key index to copy and verify only
the selected record and its Document. It does not scan unrelated metadata or
open unrelated Documents. Missing keys return an empty snapshot at the current
generation; absent Documents and missing keys remain distinct when opening
content. Whole-store snapshots remain explicitly whole-store operations.

## Registry engine adapter

`LocalRegistryPersistence` implements `IRegistryPersistence` from
`XRegistry.Server`. It owns a bounded cache of immutable point/child indexes
per observed generation and retains old images only while callers hold their
snapshots. Consecutive reads do not recopy or rescan the Registry. A generation
change is checked with the store's scalar `ReadGeneration` operation.

Index construction uses `ReadMetadataSnapshot`, which pins metadata and Document
references without reading all Document payloads. Exact byte length and SHA-256
remain checked whenever `OpenDocument` is called. Ordinary store startup and
`ReadSnapshot` retain exhaustive referenced-content verification.

The adapter distinguishes preserving, removing and replacing content.
`StorageMutation.PutPreservingDocument` retains the committed immutable
reference instead of opening one stream per mutation or restaging unchanged
bytes. Metadata-only `StorageMutation.Put` still means no Document; its existing
behavior was not changed. Every candidate rechecks its storage generation.

Engine model envelopes, outbox batches and permanent correlation reservations
remain opaque records; the adapter never filters or rewrites them. Real
file-backed engine tests cover frozen-model restart, exact Documents, committed
outbox/correlation state and nested rollback. Snapshot/stream lifetimes survive
replacement and adapter disposal, with explicit store ownership.

## Backup and restore

`await store.CreateBackupAsync(emptyDestinationDirectory)` captures one
generation using verified immutable Document copies and SQLite's backup API.
It copies only committed referenced bytes, deduplicates identical Documents,
preserves exact metadata and storage generation, and excludes prepared
candidates, abandoned staging files and orphan blobs. The source's single
execution slot is occupied during capture; operations are not silently queued.

The destination must be an existing, empty, independently administered directory
on a supported local filesystem, outside the source directory and its ancestors.
Existing destinations are never overwritten. The ready marker is written and
flushed last. Failed/cancelled captures can leave explicit incomplete evidence;
ordinary `Open` must not invent a usable backup from it.

A successful backup is independently openable by `LocalFileStore.Open`.
To restore, stop the deployment and select a validated backup directory as its
new authoritative data root, or copy the complete stopped backup to an empty
supported local directory first. Never overwrite an active writer or copy a
live SQLite main file without its transactional state. Restoring old metadata
and epochs is an explicit operator rollback, not replication or an online
cross-registry transaction. Retain the original backup when starting a restored
writable deployment.

The currently qualified filesystem policy is **fixed local NTFS on Windows and
local ext4 on Linux**. Network and overlay writer filesystems are not silently
accepted. This restriction does not prohibit reading mounted shares through the
separate File source binding. SQLite WAL uses full synchronous durability;
asynchronous-looking SQLite APIs are not used to disguise synchronous work.

Tests exercise real SQLite operations, indexed lookup, backup isolation,
generation rejection, restart, exact
binary/empty bytes, deduplication, corruption/missing blobs, staging limits,
cancellation, lease lifetime and orphan safety.

## Native process-crash evidence

`tests/XRegistry.Storage.CrashProbe` deliberately refuses JIT execution. The
independent Python driver kills exactly its owned writer process after durable
preparation, after an observed commit acknowledgement, and during four
blob-publication/commit races. A new native process must observe either all
1,000 exact metadata records and their Document, or no committed records when
the outcome was unacknowledged; partial transactions are rejected. An
acknowledged commit must survive, and abandoned stages must be collectable.

```powershell
pwsh -NoProfile -File eng\test-storage-native.ps1 -RuntimeIdentifier win-x64 -Framework net10.0 -DataRoot D:\xregistry-test-data
```

The root must use fixed NTFS on Windows or local ext4 on Linux. Run on the
specified OS architecture; the gate rejects cross-architecture execution.
The script runs native behavior cases and the crash driver, writes reports
under `artifacts/native-storage`, and removes only its unique per-case data
directories. The caller owns the data-root directory.

Six Windows x64 .NET 10 Native AOT process-termination/restart cases have executed
successfully locally. Filesystem fault injection, physical power-loss
certification, larger performance profiles and the complete native platform
matrix remain open. Process termination is not evidence of a real disk power
failure or controller-cache durability.
