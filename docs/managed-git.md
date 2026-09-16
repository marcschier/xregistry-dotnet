# Managed Git binding

The implementation uses managed pkt-line, sideband, DEFLATE/zlib, pack, delta,
commit/tag/tree and HTTP code. It does not invoke Git, use libgit2, check out a
working tree, run hooks/filters, or fetch LFS/submodule content at runtime.
Reference Git was used only to generate independent checked test fixtures.

`GitObjectReader` verifies bounded loose objects and self-contained pack v2/v3
data in both SHA-1 and SHA-256 formats. OFS_DELTA and REF_DELTA support includes
forward bases and chains. Headers, zlib completion/checksums, pack checksums,
object hashes, sizes, instructions and cumulative work are checked before an
object set is returned. Missing/cyclic/thin bases do not produce partial sets.

`GitSnapshot.Open` peels actual verified annotated tags and retains the exact
commit and root tree. `ReadBlob` walks case-sensitive raw tree names, checks
entry mode/type, and returns exact bytes. Executable mode means data, not
permission to execute. Symbolic links, gitlinks and selected LFS pointers are
explicit failures.

LFS detection includes the exact version signature ending at EOF as well as
LF/CRLF-terminated signatures; similarly prefixed ordinary content remains
exact data. An existing non-tree mapping root or a directory in place of
`registry.json` is an invalid package, not an absent path. Genuinely missing
roots/documents still return `NotFound`. These distinctions are exercised
through both `GitDocumentTreeReader` and `DirectoryMapping`, not just internal
error codes.

`GitDocumentTreeReader` adapts a snapshot to `IDocumentTreeReader` and the shared
directory-mapping interpreter. The default storage root is `xregistry`; pass an
empty root explicitly to use the repository root. Locator components use the
Git draft's stricter portable ASCII grammar, not the broader grammar of domain
IDs or mapping hrefs.

## HTTPS acquisition and trust

`GitSmartHttpClient.FetchAsync` negotiates smart HTTP v2 or legacy v0/v1,
selects a full ref or complete object ID, and acquires a bounded self-contained
pack. It only asks for supported optional capabilities, retains the selected
OID, and does not substitute a newer ref after failure. Legacy raw-pack and
sideband transports are supported.

The HTTP origin policy is shared with the Registry client: explicit origin,
validated actual connection addresses, normal TLS checks, no automatic
redirects/cookies/ambient credentials, finite deadlines, and an optional
origin-scoped credential callback. Local HTTP requires explicit loopback test
permission. Unsupported server behavior fails explicitly.

**Ordinary SHA-1 is not collision-detecting SHA1DC.** Network acquisition from a
SHA-1 repository therefore requires `GitFetchOptions.TrustedRegistryRootSha256`,
an independently trusted SHA-256 of the selected `registry.json`. The digest
must come from authorized configuration, not from the same untrusted download.
The root and its descendant mapping SHA-256 descriptors then commit to the
actual Registry bytes. SHA-256 repositories do not require that additional
SHA-1 policy input.

This is a Registry-root trust policy, not a claim of SHA-1 collision hardening,
Git signature verification, or authorship authentication. The low-level object
parser accepts caller-supplied SHA-1 data for integrity inspection and documents
that limitation. An unqualified SHA-1 network fetch is denied rather than
silently weakening the promise.

## Qualification boundaries

The current tests cover both object formats, independent Git-generated packs,
tag/tree reads, v2/legacy HTTP transcripts over real sockets, exact ref
selection, fatal sideband, bad checksums, and required trusted-root matching.
The suites pass 129 cases per managed TFM and 129 in a .NET 10
Windows x64 native executable, including 20 EOF/wrong-kind/missing-path and
lookalike regressions. The .NET 8 TUnit-host framework warning remains separate
from the clean all-package consumer evidence. Retained reference upload-pack
interoperability covers 26 cases and 56 exchanges per Windows x64 TFM; see
[Git interoperability](git-interoperability.md).

The full all-RID native matrix, broader fuzz/performance evidence, and a general
SHA1DC implementation are not qualified. Buffered acquisition is explicitly
capped at 256 MiB; larger repositories are unsupported without a bounded
on-disk acquisition design. These items are tracked in the
[roadmap](roadmap.md#native-platforms-and-package-consumers).

The selected-profile validation review adds 95 focused cases in
`GitAdvertisementValidationTests`: required revision shapes, full-ref/OID
syntax, portable root spelling, device aliases, 1/64/65-character component
limits and independent 4096/4097-character budgets. Invalid selected Git
parameters do not fall back to a valid HTTP advertisement. These tests required
no production change. The complete Federation suites pass 217 cases per managed
TFM and 217 in .NET 10 Windows x64 Native AOT with zero IL warnings. Only the
three corresponding syntax/evidence gaps in the clause ledger were closed.
