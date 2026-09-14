# General client sample

This executable uses the reusable packages rather than implementing a second
HTTP client or mapping parser. It currently supports generic HTTP reads,
opaque collection paging, both discovery locations, model inspection,
single-use streamed uploads, deletes, native File/Git document-tree reads,
remote/local OCI reads, exhaustive layout verification, portable OCI producer
preparation/publication, built-in model compilation and document validation.

```powershell
dotnet run --project samples\XRegistry.Client -- --help
dotnet run --project samples\XRegistry.Client -- model cloudevents
dotnet run --project samples\XRegistry.Client -- inspect https://registry.example/xreg
dotnet run --project samples\XRegistry.Client -- get https://registry.example/xreg /schemagroups
dotnet run --project samples\XRegistry.Client -- pages https://registry.example/xreg /schemagroups --query limit=100
dotnet run --project samples\XRegistry.Client -- discover https://registry.example/xreg
dotnet run --project samples\XRegistry.Client -- discover https://registry.example/xreg --host
dotnet run --project samples\XRegistry.Client -- put https://registry.example/xreg /schemagroups/demo/schemas/test/versions/v1 schema.json application/schema+json --token-env XREGISTRY_TOKEN
dotnet run --project samples\XRegistry.Client -- read-file D:\RegistrySnapshot /documents/main/assets/item --operation document
dotnet run --project samples\XRegistry.Client -- read-oci-layout D:\OciSnapshots offline /dirs/main/files/sample --operation document
dotnet run --project samples\XRegistry.Client -- verify-oci-layout D:\OciSnapshots offline
dotnet run --project samples\XRegistry.Client -- read-oci oci://registry.example/team/catalog release / --token-env OCI_REGISTRY_TOKEN
dotnet run --project samples\XRegistry.Client -- verify-oci-capture D:\CapturedRegistry\capture.json
dotnet run --project samples\XRegistry.Client -- publish-oci oci://registry.example/team/catalog release D:\CapturedRegistry\capture.json --token-env OCI_REGISTRY_TOKEN
dotnet publish samples\XRegistry.Client -c Release -r win-x64
```

The native executable is `XRegistry.Sample.Client` (with `.exe` on Windows).
This avoids colliding with the reusable `XRegistry.Client` assembly.
The early `ArtifactsProjectName` override also separates their intermediate
directories when using `--artifacts-path`.

No credentials belong in URLs, sample configuration or command-line token
values. `--token-env` explicitly selects an environment variable. HTTP
loopback/private-origin permissions are opt-in. Documents stream to stdout;
redirected/external bytes are not fetched behind the caller's back.
`pages` emits one compact JSON object per line without buffering the whole
collection. A later failure exits nonzero; previously printed pages are partial
output, not a complete-set guarantee. Discovery does not follow advertisements.
`--decode-content` opts Registry HTTP requests into bounded, strictly framed
gzip/deflate/Brotli response decoding. Native OCI uses exact-byte HTTPS and
rejects this Registry-specific option or plaintext loopback options.

Git uses full refs/OIDs, a default `xregistry` root, and no installed Git
executable. SHA-1 network acquisition requires an independently trusted
`--root-sha256`; the digest is not obtained from the untrusted source itself.

## OCI producer input

`verify-oci-capture` prepares and fully validates an OCI snapshot without
publishing it. `publish-oci` performs the same preparation before its explicit
reference-last remote publication. It does not retry mutations.

The sample's input envelope has two fields: `records` is an array of portable
OCI records defined by the frozen `oci-record.schema.json`, and optional
`documents` maps each embedded Version XID to a descriptor of a local file:

```json
{
  "records": [],
  "documents": {
    "/dirs/main/files/sample/versions/v1": {
      "file": "documents/sample.json",
      "contenttype": "application/schema+json"
    }
  }
}
```

This illustrates the envelope, **not a valid empty Registry**: supply the
coherently captured Registry/Group/Resource/Meta/Version portable records and
exactly their embedded Documents. `base` and `origin` are optional string
provenance fields on a file descriptor. References are bounded, read-only paths
inside the capture file's directory, using the handle-anchored File reader.
Traversal, missing files, duplicate/unknown fields and incoherent input fail.

The envelope is sample input, not a new xRegistry export format. It is not
interchangeable with core HTTP exports or DirectoryMapping result envelopes.
The caller must establish coherent source capture; a sequence of live reads or
a Registry epoch cannot establish snapshot isolation. Publication credentials
are scoped to the explicitly named destination; external Document locations
are not fetched automatically.

## Native fixture qualification

`runtime-info` reports native flags and the actual JIT compilation count, not
merely the executable's filename. `eng/verify_client_sample.py` checks those
values and uses the immutable
independent OCI corpus to verify exact graph counts, binary/empty/default and
aliased Documents, producer preparation and failure/containment cases:

```powershell
python eng\verify_client_sample.py --client artifacts\client-all-bindings-native\XRegistry.Sample.Client.exe --report artifacts\client-all-bindings-native\qualification.json
```

The Windows x64 native sample passed these 13 fixture checks locally.
`eng/test-client-sample.ps1 -RuntimeIdentifier win-x64` additionally executes a
real JIT negative control, for 14 checks. This is not live remote OCI publication
or proof of unexecuted native platforms.
