# WoT versioned references

`XRegistry.Wot` resolves Thing Model (TM) and Thing Description (TD) references over caller-supplied Resource snapshots. It returns the exact `(wotid, versionid, contentdigest, origin)` selection without acquiring documents, rewriting their bytes, or depending on registry storage. The caller supplies one Resource per `wotid` in its chosen registry and Group scope and remains responsible for authorization, document acquisition and digest verification.

The implementation follows the [WoT Registry draft, sections 1.3, 5.4 and 6.2](https://github.com/marcschier/xregistry-spec/blob/222527c643364d70bdf4ec687f6421397a96800a/workingdrafts/models/wot/spec.md). The [canonical corpus](../tests/XRegistry.Core.Tests/Wot/Resolution/vectors/README.md) is copied byte-for-byte from that commit and contains 64 vectors. Its manifest, schema and executable Python checker are mirrored with it.

## Normalize a reference

`WotReferenceParser.Parse` accepts one compact JSON link or definition-reference object. It recognizes `tm:extends`, `tm:submodel`, `type`, `td:extends`, `td:subthing`, `tm:ref` and `td:ref`. TM floors use `tm:minVersion`; TD-to-TD floors use `td:minVersion`.

```csharp
using System.Text.Json;
using XRegistry.Wot;

using var source = JsonDocument.Parse("""
    {
      "td:ref": "https://example.org/things/lamp-definitions#/properties/status",
      "td:minVersion": "1.2.0"
    }
    """);
var request = WotReferenceParser.Parse(source.RootElement);
```

The floor is a sibling of `td:ref` or `tm:ref`, or a member of the link containing `rel` and `href`. A relative URI requires an absolute document base. The parser removes the fragment from definition-reference lookup targets; the caller retains the original reference and applies its fragment after version selection. It does not expand JSON-LD contexts, walk a document, or implement composition operators.

A parser-produced request retains the floor term so unpinned resolution can report a cross-kind term as `invalid-floor`. Floor strings are validated by the resolver, after pin selection. Callers constructing `WotReferenceRequest` directly can supply `FloorTerm`; omitting it means the normalized floor belongs to the indicated reference kind.

## Select a Version

`WotVersionResolver.Resolve` accepts snapshots, a normalized request and the registry origin. Optional arguments supply TD type-link context, one digest or Version-id pin, and prerelease opt-in.

Selection follows this order:

1. Find the Resource by its exact `wotid`.
2. Honor a caller pin. A digest pin chooses the highest SemVer among matching bytes; a Version-id pin chooses that exact Version. Pins bypass floor derivation, TD major caps, prerelease filtering and sticky defaults.
3. Derive the floor. For TD-to-TD references, only the explicit floor applies. Neither the referencing TD's `version.instance` nor its `version.model` supplies an implicit floor.
4. Honor a sticky default as-is. An unavailable default fails as `not-found`; a default outside the required range fails as `unsatisfied` without substitution.
5. Otherwise select the highest eligible Version. Prereleases, including generated ids, require a prerelease floor or caller opt-in.

A TD floor `X.Y.Z` admits Versions at or above the floor whose major is X. It excludes the next major's prereleases even with prerelease opt-in. For major zero, a floor of `0.8.0` admits `0.9.4` but not `1.0.0`. Without a floor, a TD reference can select the highest eligible release across majors. TM floors remain uncapped.

A TD's `type` link to a TM uses the existing model-aware floor rules. Supply `WotDocumentVersionContext` with all type links and the document's `version.model`: the effective floor is the greater of the link floor and model version. Several type links with a model version require explicit floors on every link; a missing floor produces `ambiguous-floor` before individual floor validation.

A floor must be SemVer without build metadata, fit Core's 128-character identifier limit, and not have the reserved `0.0.0-N` generated form. Violations produce `invalid-version`. A `td:minVersion` on a TM reference or `tm:minVersion` on a TD-to-TD reference produces `invalid-floor`. No selection follows a deprecated Resource's successor automatically.

## Assign publication identity

`AssignPublishedVersion` uses `version.model` for `targetKind: "tm"` (the default) and `instanceVersion` for `targetKind: "td"`. The latter argument carries the authored TD `version.instance`; its `modelVersion` argument does not determine TD identity.

A valid SemVer becomes the authored `versionid` verbatim. An absent or non-SemVer value receives the lowest free `0.0.0-N` id and `versionorigin: generated`. Build metadata and overlong SemVer identifiers fail as `invalid-version`. A supplied id must satisfy Core's grammar and match the assignment, or fail as `invalid-version` or `version-mismatch`, respectively. The helper computes the post-publication default without changing a sticky default or mutating the snapshots.

Both TD and TM Resources require `versionmode: semver` and `singleversionroot: true`. `XRegistry.Models` does not package a WoT model. Hosts must configure these Resource rules, set read-only `versionorigin`, preserve document bytes, and make assignment and creation atomic with concurrent writes. The helper computes an assignment; it does not reserve an id or perform a write.

## Verify

```powershell
python tests\XRegistry.Core.Tests\Wot\Resolution\vectors\check_vectors.py
dotnet test --project tests\XRegistry.Core.Tests\XRegistry.Core.Tests.csproj -c Release
```

`CanonicalVectorManifestMatchesCopiedBytes` checks the complete mirrored file set and SHA-256 values. `CanonicalResolutionVectorMatchesImplementation` runs every vector against the .NET resolver or publication helper on both target frameworks. Parser tests and additional resolver cases cover sibling floors, relative-reference bases, next-major prereleases, pin precedence and the separation of TD instance identity from model dependencies.
