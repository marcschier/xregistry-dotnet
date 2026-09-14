# Security contract and threat model

This is the implementation security contract. It is not a completed security
assessment or certification. Controls require executable evidence before release.

## Assets and actors

Assets include registry metadata and exact Document bytes; model/capability
configuration; caller identities and upstream credentials; durable state and
backups; source snapshot identities; package/provenance records; and bounded
memory, CPU, network and disk resources.

Treat remote callers, domain documents, imported models, catalogs, Git packs,
OCI descriptors, redirects, authentication challenges, local/mounted source
trees, and CI inputs as potentially hostile. Configured administrators and
credential providers are separate authorities, not aliases for remote callers.

## Trust and failure rules

- Authenticate and authorize writes. Check all affected descendants in a nested
  mutation, not just the addressed ancestor.
- Separate entity mutation, model administration and capability administration.
  Anonymous reads are an explicit host policy; they do not grant writes.
- Accept identity only from the host's validated authentication context, never
  arbitrary user-supplied identity headers.
- Keep inbound identity separate from upstream credentials. Do not forward
  cookies, bearer tokens, proxy credentials or custom secret headers by default.
- Preserve failures. Authentication failure, policy denial, unavailable objects,
  malformed data, integrity mismatch, exhausted quotas and uncertain commit
  outcomes must not become empty-success responses.
- Never claim rollback merely because a response could not be delivered.

## Required evidence

These identifiers describe security obligations, not tests already executed.

| ID | Attack or failure | Required control and observable evidence |
| --- | --- | --- |
| SEC-AUTH-001 | Anonymous or underprivileged nested write | Reject before mutation; verify all affected entities and configuration remain unchanged. |
| SEC-AUTH-002 | Confused deputy at a bridge | Separate caller authorization from origin-scoped upstream credentials; prove no credential crosses an unauthorized mount. |
| SEC-CACHE-001 | Data reused across callers/origins | Key cache entries and leases by appropriate identity, authorization and snapshot context; test isolation and revocation/freshness policy. |
| SEC-HTTP-001 | Spoofed Host/forwarding headers | Use configured public roots and explicitly trusted proxies; do not emit attacker-selected navigation origins. |
| SEC-HTTP-002 | Request/header/query ambiguity | Apply binding-specific single decoding and validation; preserve identifiers and repeated parameters without injection or unsafe normalization. |
| SEC-NET-001 | SSRF through imports, pagination or catalogs | Validate scheme, origin, DNS and actual connection under one outbound policy; reject unauthorized IPv4/IPv6/local/metadata destinations. |
| SEC-NET-002 | Redirect or token-service exfiltration | Revalidate each destination; disable automatic credential forwarding and uncontrolled challenge/redirect following. |
| SEC-NET-003 | Untrusted TLS peer | Preserve certificate/hostname validation. Explicit local HTTP demo allowances must not permit arbitrary remote plaintext. |
| SEC-DATA-001 | Oversized or compressed input | Charge actual encoded/decoded bytes across the entire operation; reject before unsafe allocation or publication. |
| SEC-DATA-002 | JSON/YAML/XML expansion or code execution | Bound depth/work; reject DTDs/external entities and arbitrary YAML construction; never execute stored artifacts. |
| SEC-DATA-003 | Expensive schema/filter/regex evaluation | Bound nodes, depth, instructions and evaluation work; preserve unsupported/indeterminate validation outcomes. |
| SEC-FILE-001 | Traversal, ADS, device or separator abuse | Use binding-specific path validation and safe containment; do not derive store paths directly from identifiers. |
| SEC-FILE-002 | Symlink/reparse or replacement race | Validate and retain safe handles/identities, including intermediate paths; prove a swapped source cannot escape the permitted root. |
| SEC-STORE-001 | Crash or disk-full during publication | Flush files before durable metadata references; recover without exposing partial mutations or acknowledging lost state. |
| SEC-STORE-002 | Missing/corrupt initialized state | Fail closed, remain unready and preserve evidence; never initialize an empty replacement automatically. |
| SEC-STORE-003 | Reader racing deletion/GC | Retain referenced immutable content for active leases; prove exact bytes remain readable. |
| SEC-GIT-001 | Hostile framing, sideband or pack | Reject truncation/fatal channels/checksum failures; validate sizes and deltas before allocation and never publish partial results. |
| SEC-GIT-002 | Mutable refs or ambiguous selection | Resolve exact refs and verified tag objects once; keep the selected commit even after the advertised ref moves. |
| SEC-GIT-003 | Hash/provenance confusion | Tag object IDs with their hash algorithm; distinguish integrity from authorship and explicitly qualify SHA-1 collision defenses. |
| SEC-GIT-004 | Alternate store, helper, hook or LFS escape | Do not invoke Git processes/native Git or follow unapproved alternate stores, configuration includes, hooks, gitlinks or LFS fetches. |
| SEC-OCI-001 | Forged descriptor or graph expansion | Verify bytes against digest/size/type/role, enforce graph budgets and exact index limits, and reject incomplete claimed closure. |
| SEC-FED-001 | Cycles, ambiguity or error fallback | Bound traversal; retain the selected origin; do not re-traverse producer-resolved views or bypass a selected-source failure. |
| SEC-LOG-001 | Credential or document disclosure | Redact secrets and sensitive URL data; use bounded structured diagnostic fields and policy-aware errors. |
| SEC-CI-001 | Untrusted build/publishing input | Separate PR jobs from privileged publication, pin trusted inputs and verify package source/provenance and release manifests. |

## Resource accounting

Every untrusted parser and graph operation must declare finite numerical limits
before qualification. Test zero/empty input, the configured limit, and
limit-plus-one as distinct observable cases.

Account for request/header/query bytes, metadata and Document bytes, decoded
bytes, nesting, node/attribute/ref counts, graph requests and depth, retries,
redirects, elapsed/idle budgets, concurrent operations, queued work, cache
residency, staging disk and total expanded data. An expansion ratio alone is not
a sufficient decompression defense.

For Git also account for advertisements, pkt-lines, packets, progress/error
output, object lengths/counts, deferred delta bases, cumulative delta work,
instructions, chain depth, tree entries and tag peeling. Never allocate directly
from an unchecked length.

OCI's 256-descriptor and 1,048,576-byte per-index limits are normative, not
operator-adjustable opportunities to accept a larger index as conformant.

## Storage and deployment

The durable writer uses qualified local storage with one writer per data
directory. Reading an already OS-mounted share through the File binding does
not qualify that share for SQLite WAL writer storage.

Production hosts require explicit authentication/authorization and TLS
configuration or a documented trusted TLS-termination deployment. A local demo
must be explicitly enabled and confined to loopback listeners; environment
names alone must not disable security.

Health/diagnostic surfaces must not expose secrets, unpublished content,
connection strings, internal filesystem locations or unauthenticated mutation.
Root readiness must reflect persistent-state integrity.

## Managed Git security gate

Pure-managed Git is a hard runtime constraint, not a reason to weaken parser or
identity checks. Ordinary SHA-1 recomputation is not collision-detecting SHA-1.
Before release, qualify the chosen collision/trust policy with independent
review and vectors. Do not infer authorship from HTTPS, tag peeling, or a digest
computed only after downloading untrusted content.

If a required security guarantee cannot be achieved, report the affected promise
as blocked. Do not silently accept residual risk, introduce native/executable
Git, or advertise a weakened implementation as complete.

## Release gate

Release requires mapped, executed evidence for the applicable obligations and no
unresolved release-blocking finding. False-positive exceptions must be narrow,
justified and reviewable; blanket warning suppression or a green command with
no executed cases is not evidence.
