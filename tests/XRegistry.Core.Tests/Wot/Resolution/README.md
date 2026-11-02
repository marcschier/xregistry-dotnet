# WoT resolution evidence

The .NET tests execute two independent collections of literal expected results.
The current shared examples establish parity with WoT Registry Service
1.0-rc4 sections 1.3 and 5.4; the implementation regressions retain cases from
the earlier PR baseline that the representative shared collection no longer
includes.

| Collection | Ownership and purpose |
|---|---|
| `shared` | Byte-identical copies of the shared `vectors.json`, schema, checker and README from [xregistry/spec#571](https://github.com/xregistry/spec/pull/571). |
| `regressions` | Implementation-owned coverage retaining all 64 numbered fixtures and their original manifest, schema, checker and README without byte changes. The README describes their historical source layout, not the current shared collection. |

`sources.json` records exact source revisions and hashes for audit purposes.
The shared case count is obtained from the collection rather than maintained
here. The old multiple-root migration fixture is deferred migration evidence;
it is not a resolver operation and is not part of either collection.

`SharedResolutionVectorMatchesLiteralExpectation` reads each named case from
the shared collection; `ImplementationRegressionMatchesLiteralExpectation`
reads each numbered regression file. Both adapt the fixture inputs to the
public API and compare the returned identity, digest and error with the
literal expectation. For local references the adapter supplies the exact
containing revision and authored document version, not a floor-selected
Version. The adapters never run the Python checker to generate expected values.

Run both independent checkers and the managed tests from the repository root:

```powershell
python tests\XRegistry.Core.Tests\Wot\Resolution\shared\check_vectors.py
python tests\XRegistry.Core.Tests\Wot\Resolution\regressions\check_vectors.py
eng\test.ps1 -Project tests\XRegistry.Core.Tests\XRegistry.Core.Tests.csproj -Configuration Release
```

Copied specification artifacts retain the xRegistry project's Apache-2.0
license; see `tests\Conformance\Sources\LICENSE` from the repository root.
The source-byte checks reject changes to the mirrored artifacts. An intentional
refresh requires selecting a new source revision and updating the recorded
hashes; it must not replace the implementation regressions or generate new
expectations from the .NET resolver.
