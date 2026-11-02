# WoT versioned references

`XRegistry.Wot` resolves Thing Model (TM) and Thing Description (TD) references over caller-supplied Resource snapshots. It returns the exact `(wotid, versionid, contentdigest, origin)` selection without acquiring documents, rewriting their bytes, or depending on registry storage. The caller supplies one Resource per `wotid` in its chosen registry and Group scope and remains responsible for authorization, document acquisition and digest verification.

The implementation follows the [WoT Registry Service 1.0-rc4 draft, sections 1.3 and 5.4](https://github.com/xregistry/spec/pull/571). The [resolution evidence](../tests/XRegistry.Core.Tests/Wot/Resolution/README.md) separates the current shared collection from the implementation-owned regression corpus. Source identities and file hashes are recorded in its manifest; neither the examples nor the informative design notes replace the specification's requirements.

## Normalize a reference

`WotReferenceParser.Parse` accepts one compact JSON link or definition-reference object. It recognizes `tm:extends`, `tm:submodel`, `type`, `td:extends`, `td:subthing`, `tm:ref` and `td:ref`. TM floors use `tm:minVersion`; TD-to-TD floors use `td:minVersion`. The [OPC UA WoT Binding draft](https://github.com/OPCF-Members/OPC10100-WoT/pull/56) also permits `uav:minVersion` as a target-kind-neutral alternative. This parser supports that compact term under SemVer, not arbitrary JSON-LD aliases or context expansion.

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

When a definition reference resolves to the same document as the supplied base URI, excluding the fragment, the parser sets `IsDocumentLocal`. Supply the containing document's base even for absolute references so the parser can distinguish local references from external lookup. Direct callers can set the same property on a normalized request.

Parsing validates the authored floor's syntax and target kind. A malformed floor produces `invalid-version`; a wrong-kind term or coexistence of `uav:minVersion` with either legacy term produces `invalid-floor`. Any `uav:compatibilityLine` on this SemVer reference interface produces `invalid-line`, including an explicit null value. A decimal revision string does not select the Incremental profile.

The parser's authored-reference checks are distinct from raw exact replay. Callers constructing normalized `WotReferenceRequest` values directly can supply `FloorTerm`; omitting it means the normalized floor belongs to the indicated reference kind. `Resolve` honors a raw exact pin before deriving floors, including TD type-link floors. Retrieving pinned bytes does not validate the authored references in those bytes.

## Select a Version

`WotVersionResolver.Resolve` accepts snapshots, a normalized request and the registry origin. Optional arguments supply document-version context, one digest or Version-id pin, and prerelease opt-in. The existing positional constructors and resolution signature remain unchanged.

A document-local `tm:ref` or `td:ref` requires `WotDocumentVersionContext.ContainingVersion`: the exact revision being processed, its digest, and its authored `ModelVersion` or `InstanceVersion`. The resolver returns that revision without enumerating Resource snapshots, consulting defaults, filtering prereleases, or selecting another stored Version. With a floor, it compares the containing revision's authored model version for `tm:ref`, or instance version for `td:ref`; the TD major cap still applies. A missing authored version produces `unsatisfied`, and an invalid one produces `invalid-version`. Without a floor, no authored version is required. Local requests reject pins and non-definition operators as invalid API arguments.

```csharp
var localRequest = new WotReferenceRequest("td-ref", "urn:example:pump", "1.2.0")
{
    IsDocumentLocal = true
};
var context = new WotDocumentVersionContext(null, [])
{
    ContainingVersion = new("1.2.0", "authored", digest, InstanceVersion: "1.2.0")
};
var selection = WotVersionResolver.Resolve([], localRequest, origin, context);
```

External selection follows this order:

1. Find the Resource by its exact `wotid`.
2. Honor a caller pin. A digest pin chooses the highest SemVer among matching bytes; a Version-id pin chooses that exact Version. Pins bypass floor derivation, TD major caps, prerelease filtering and sticky defaults.
3. Derive the floor. For TD-to-TD references, only the explicit floor applies. Neither the referencing TD's `version.instance` nor its `version.model` supplies an implicit floor.
4. Honor a sticky default as-is. An unavailable default fails as `not-found`; a default outside the required range fails as `unsatisfied` without substitution.
5. Otherwise select the highest eligible Version. Prereleases, including generated ids, require a prerelease floor or caller opt-in.

A TD floor `X.Y.Z` admits Versions at or above the floor whose major is X. It excludes the next major's prereleases even with prerelease opt-in. For major zero, a floor of `0.8.0` admits `0.9.4` but not `1.0.0`. Without a floor, a TD reference can select the highest eligible release across majors. TM floors remain uncapped.

A TD's `type` link to a TM uses the existing model-aware floor rules. Supply `WotDocumentVersionContext` with all type links and the document's `version.model`: the effective floor is the greater of the link floor and model version. Several type links with a model version require explicit floors on every link; a missing floor produces `ambiguous-floor` before individual floor validation.

A floor must be SemVer without build metadata and must not have the reserved `0.0.0-N` generated form. Explicit floors are not Core Version identifiers and have no 128-character limit. Stored and assigned Version identifiers, including an authored `version.model` used as an implicit floor, retain Core's limit; the public `WotSemanticVersion.Parse` and `TryParse` methods validate those identifiers. Syntax violations produce `invalid-version`. A valid floor beyond all available Versions produces `unsatisfied`, regardless of its length. A `td:minVersion` on a TM reference or `tm:minVersion` on a TD-to-TD reference produces `invalid-floor`. No selection follows a deprecated Resource's successor automatically.

## Assign publication identity

`AssignPublishedVersion` uses `version.model` for `targetKind: "tm"` (the default) and `instanceVersion` for `targetKind: "td"`. The latter argument carries the authored TD `version.instance`; its `modelVersion` argument does not determine TD identity.

A valid SemVer becomes the authored `versionid` verbatim. An absent or non-SemVer value receives the lowest free `0.0.0-N` id and `versionorigin: generated`. Build metadata and overlong SemVer identifiers fail as `invalid-version`. A supplied id must satisfy Core's grammar and match the assignment, or fail as `invalid-version` or `version-mismatch`, respectively. The helper computes the post-publication default without changing a sticky default or mutating the snapshots.

Both TD and TM Resources require `versionmode: semver` and `singleversionroot: true`. `XRegistry.Models` does not package a WoT model. Hosts must configure these Resource rules, set read-only `versionorigin`, preserve document bytes, and make assignment and creation atomic with concurrent writes. The helper computes an assignment; it does not reserve an id or perform a write.

Incremental-profile allocation, durable revision counters, publication channels, retained snapshots and publication storage are outside this API's implemented scope. No production registry adapter calls this resolver automatically.

## Verify

```powershell
python tests\XRegistry.Core.Tests\Wot\Resolution\shared\check_vectors.py
python tests\XRegistry.Core.Tests\Wot\Resolution\regressions\check_vectors.py
eng\test.ps1 -Project tests\XRegistry.Core.Tests\XRegistry.Core.Tests.csproj -Configuration Release
```

`SharedSourceManifestMatchesCopiedBytes` and `ImplementationRegressionManifestMatchesCopiedBytes` check the two file sets independently. `SharedResolutionVectorMatchesLiteralExpectation` and `ImplementationRegressionMatchesLiteralExpectation` run their authored expectations against the .NET resolver or publication helper on both target frameworks. Neither runner derives expected selections from the implementation. Local-reference tests exercise parsing through resolution, including a containing revision absent from the registry snapshot, authored-version errors and next-major prereleases. Additional parser tests cover the Binding floor alternative and distinguish authoring rejection from raw replay.
