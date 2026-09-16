# Changelog

This is a retrospective engineering record. It preserves implementation
milestones, before/after findings, and specification-correction provenance.
None of these entries claims that packages were released or that the complete
conformance/native matrix is qualified.

## Current-source integration milestone

The latest retained local integration described by the repository used the
all-runtime-package feed `artifacts\embedding\29c45e4bd888`, source fingerprint
`5328167584619aded4223746a71c1fe2ea0d48f201513a4e49cc7a61cc0946e5`.
All eleven packages passed the 25-case Windows x64 native consumer on both
target frameworks, with zero IL warnings and both JIT rejection controls. The
same source state recorded 8,967 managed cases, 276 tooling tests, native
Core/Models/Server totals of 750/750/834 per framework, Federation 458 on
.NET 10, and principal sample gates of 17/14/6. These are development receipts,
not release qualification.

## Package-consumer checkpoints

| Checkpoint | Source fingerprint | Retained result |
| --- | --- | --- |
| Catalog/server-preset and restore-isolation | `22fa72eb00fa565020623569696a4aa92ef63bf35c49455bbb69e6e6add4d032` | Eleven packages, 25 native cases per Windows TFM, 6,841 managed and 273 tooling cases under the former restore-isolation policy. |
| Integrated domain/source | `857ebc04a188b57fca3f951dba287b4073f62fb720930891286cb0c37fb637dd` | Eleven packages, 25 native cases per Windows TFM, 6,395 managed cases, and principal samples 15/14/6. |
| Core protocol and representation | `aed7a9497465909169ca6d9712e46009bfb38e6e32d9b4cbcd169129cfd35717` | Eleven packages and principal samples passed after Core scalar, readonly, deprecation, Version precedence, and representation changes. |
| Compiler/model correction | `f014f0b340d214efe774c3e4cc9a03ade8425d654e22ad4cf557ec088d1194c0` | Retained predecessor feed for compiler/model fixes. |
| Conditional-header/event/Git edge cases | `f7694671d74cd442c900303dfca0e51f9dc25bc2608f84b6142941eb82ce4e24` | Both Windows TFMs passed 25 native cases. A predecessor probe incorrectly expected initial epoch one; the corrected probe retained the implementation's epoch-zero behavior. |
| Escaped federation identities | `abe22b350e5382f9090679a119d003b5e4fe4c4cb2a0fa86f35999009f9f164b` | Both Windows TFMs passed 25 cases including literal/escaped identities and rejection before dispatch. |
| First eleven-package feed | `b9863dc2df74967c6bf622ad5cdfd3a7218ae3d0f24eaff4704940cb26278c64` | Both Windows TFMs passed 24 cases with actual SQLite and no native Git backend. |
| Initial six-package feed | `c8eb92b70fd26315d3b5bc39179e77168996c5c7fd685a596825470410fcbca8` | Both Windows TFMs passed 15 cases and both JIT controls. |

The Linux package-consumer attempt on 2026-09-12 stopped before execution when
Docker's content store returned read-only/containerd blob I/O failures. The
retained logs classify this as infrastructure failure, not a failed consumer
test or Linux qualification.

## Native and performance checkpoints

- Earlier Windows native slices grew from recovered Core/Server/File/OCI/Git
  runs to the current source-matched package and sample receipts. Failed or
  stale-source attempts were retained instead of being relabeled.
- Linux x64 native File/OCI/HTTP/storage slices executed in isolated ext4
  containers before the local Docker filesystem became unavailable. Those
  receipts apply only to their recorded revisions and cases.
- The first 100/250 durable-server profile found the shipping entity-operation
  limit at larger writes. Its successful profile measured 741 validated
  requests; failed 250-write/1000-Resource attempts remained unmeasured.
- A later 100/1000 profile completed 3,960 validated requests per three-run
  qualification. The cursor workload was reduced from five to four measured
  collection iterations only after the original sequence correctly exhausted
  the unchanged 16,384 retained-record budget.
- Subsequent old/current A/B experiments showed substantial shared-host
  variance. A point-read increase did not repeat across alternating pairs, so
  no source-level speedup or latency regression conclusion was promoted.
  Current measurements and the investigation trigger remain in
  [server performance](server-performance.md).

## Specification correction ledger

The immutable baseline is under `tests/Conformance/Sources`; successor artifacts
are recorded by `eng/specification/corrections.json` under
`tests/Conformance/Corrections/<case>/...`. The active corpus, source hashes,
predecessor chain, reasons, and regression test IDs are verified by the
[provenance guide](spec-feedback.md) and
[`eng/specification/manage.py`](../eng/specification/README.md).

| Case | Before / after | Primary successor sources and evidence |
| --- | --- | --- |
| SPEC-001 | Invalid JSON Pointer fragments and an Endpoint array modeled with scalar `enum` prevented all packaged models from compiling. The correction uses RFC 6901 pointers, a required string array, and a bounded ordered include resolver. | CloudEvents/Endpoint model and generated projections; independent fragment/required-field cases and .NET model compilation. |
| SPEC-002 | Eleven CloudEvents examples used the nonexistent date 2024-04-31. They now use 2024-05-01 while preserving next-day ordering. | CloudEvents example successors and calendar/order regression. |
| SPEC-003 | HTTP discovery illustrated `registries` as an object instead of the Core array. The example now uses the array shape and keeps Registry-relative and host-relative discovery distinct. | `core/http.md` successor, Python JSON-shape regression, and Kestrel discovery cases. |
| SPEC-004 | Escaped legal Core IDs were parsed as already-decoded IDs. Each path component is now decoded exactly once with strict UTF-8 while retaining wire spelling. | Core helper successor and .NET escaped/double-decode identity cases. |
| SPEC-005 | Mapping identity comparisons and OCI byte-range routing could disagree for equivalent URI spellings. The correction defines canonical graph identity annotations and one bounded routing path. | Mapping/OCI draft successors, independent counterexamples, and native File/Git/OCI cases. |
| SPEC-006 | Pagination called an HTTP-date value RFC3339 and used the wrong weekday. The prose names HTTP-date and the example uses the correct weekday. | Pagination successor and exact expiry/deadline regressions. |
| SPEC-007 | Four Protobuf examples had an unmatched closing brace. Only the extra brace was removed. | Schema successor, original-rejection case, and both-target validators. |
| SPEC-008 | Message/Endpoint field names, shapes, defaults, query representation, and target type contradicted domain prose. Corrected sources and twelve generated projections align those contracts. | Seventeen successor inputs, independent source cases, and atomic Server admission tests. |
| SPEC-009 | OpenUSD plugin and asset-container Groups declared separate copies of one Resource type. The plugin Group now imports the shared type, enabling type-correct one-hop aliases. | OpenUSD model/feedback successors and Python/.NET model/Server cases. |
| SPEC-010 | Core string/object modeling lost or rejected typed protocol property literals, including null and AMQP names. Opaque declaration boundaries plus procedural Message validation preserve literal values. | Message models/prose, twelve generated schemas, source tests, and Server declaration cases. |
| SPEC-011 | OpenUSD collision suffix assignment could rename or disagree with lookup. Assignment now retains the first stable candidate and one source-derived fallback, then fails further collisions. | OpenUSD draft successor, six source guards, and Models/Server collision cases. |
| SPEC-012 | Endpoint authored templates in URIs, map keys, and enum strings were rejected. Protocol options now have an opaque authoring boundary while consumer checks remain procedural. | Endpoint/CloudEvents sources and projections plus source/model regressions. |
| SPEC-013 | Common header records omitted `specurl`; MQTT binary/media fields had incorrect types. Common references, base64 correlation data, and media-type strings now align with procedural checks. | Message sources/projections, source guards, and Server header/binary cases. |
| SPEC-014 | Mapping treated Document-like field names as Documents even on metadata-only Resource types. Detachment now applies only when `hasdocument` is true. | Mapping prose/oracle/regression successors and .NET field-plane cases. |
| SPEC-015 | OCI had the same metadata/Document-plane conflict and could omit metadata defaults. Reader and producer now preserve metadata-only values/defaults while retaining Document restrictions. | OCI prose/oracle/regressions and 270-case managed/native evidence. |
| SPEC-016 | Pytest subtests produced XML/accounting identities that the strict oracle could not reconcile. Each control now has its own test identity; the accounting gate was not relaxed. | Test-only OCI successor and retained rejected/passing receipts. |
| SPEC-017 | Catalog priority checks enforced token spelling instead of Core `uinteger` value semantics. Exact bounded numeric validation now accepts integer-valued decimal/exponent forms and negative zero. | Catalog prose/regressions and Models/Federation/Server cases. |
| SPEC-018 | One Registry vector retained the pre-SPEC-017 expectation. The test-only successor updates that coupled expectation. | Registry oracle successor and complete corrected run. |
| SPEC-019 | HTTP/NATS/MQTT/Kafka declarations lacked common `type`; MQTT lacked `required`. The corrected models add the common fields and explicit native value representations. | Message model/prose and generated-schema successors plus materializer/Server tests. |
| SPEC-020 | SPEC-019 initially placed documentation on an item shape where Core disallows annotations. The successor moves documentation to the owning attributes without weakening Core. | Message successor and the final corrected oracle. |

The correction sequence advanced the independent corrected oracle while keeping
the same 38 explicit OPC-UA exclusions. Rejected receipts for concurrent ledger
changes and subtest/XML accounting mismatches remain evidence that the
provenance gate fails closed.
