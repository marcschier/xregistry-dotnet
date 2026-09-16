# Git binding completion and evidence scope

The binding remains managed-only: no Git/helper process, checkout, hooks,
filters, LFS/submodule acquisition, local repository configuration, alternates,
replacement objects or grafts are consulted at runtime. See
[Managed Git](managed-git.md), [the design decision](adr/0003-pure-managed-git.md)
and [Git interoperability evidence](git-interoperability.md).

## Acquisition and immutable selection

`GitSmartHttpClient.FetchAsync` explicitly acquires one verified, bounded,
self-contained commit/tag object set. It supports v2 and legacy v0/v1 framing,
including a service banner and version-1 marker. Full refs and complete OIDs
are selection inputs; the verified commit remains pinned afterwards.

`GitFetchOptions.MaxReferences` bounds both references and advertised
capabilities. Capability overflow now fails before any fetch, rather than using
only the larger packet-framing allowance as an accidental capability limit.
The control-byte and ref-count limits are independent and inclusive. Deadlines
cover response bodies, and caller cancellation remains distinct from success.

The existing SHA-1 policy is unchanged: network acquisition requires an
independently trusted `TrustedRegistryRootSha256`, never a digest obtained from
the same untrusted response. SHA-256 object format does not make Git OIDs
equivalent to raw Document SHA-256, Core Version IDs or publisher signatures.

Authentication denial and redirects terminate acquisition without a fallback
or a request to the redirect target. The explicit credential callback sees only
the configured repository origin. Normal certificate validation and the shared
actual-address policy are not bypassed to facilitate tests.

## Git-backed mapping reads

After acquisition, `GitDocumentTreeReader` reads exact objects beneath the
selected tree, and `DirectoryMapping` owns the common model/view/selection and
closure semantics. The reader context separates repository `Source`, storage
`RootPath`, `RequestedRevision` and immutable commit `Revision`.

Current public composition tests verify literal label values, later-member
ambiguity/errors, default selection, one-hop aliases, explicit external
descriptors and their original URI base. An offline-complete mapping cannot
hide an external Version. `DirectoryMapping.ValidateAsync` performs full
declared-closure validation over a Git reader; an ordinary successful selective
read is not a substitute for that operation.

Missing tree names and unavailable pinned objects remain different outcomes.
Selected symlinks/gitlinks fail without following them, while unrelated
indirections do not invalidate readable ordinary/executable-mode data.
Present wrong object kinds, contradictory tag declarations and unsupported
Core/mapping versions are rejected. A verified Git object does not waive the
mapping's independent exact-byte descriptor SHA-256.

## Important scope and qualification distinctions

Commit acquisition precedes the XID request and may transfer other objects in a
self-contained pack. The later XID traversal is selective and does not open
unrelated domain Documents. This module does **not** claim target-aware minimal
network transfer, blob filtering or implicit lazy fetching; those are different
acquisition features. The source-boundary clarification is recorded in the
current implementation scope and is not silently imposed on the specification.

Current completion evidence is managed public-test execution on Windows.
Native/provenance full gates, Linux/ARM execution, mapped-share coherency and
permitted-origin TLS certificate experiments remain separate. No loopback TLS,
certificate, network-address or credential policy was weakened to manufacture a
platform/transport qualification result.

The Git package claims the native read-only resolver role, not a Git publication
implementation. Producer duties in the specification are not labeled as
implemented by parsing or fixture construction.
