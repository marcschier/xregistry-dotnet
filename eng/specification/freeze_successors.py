#!/usr/bin/env python3
# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""Acquire the explicitly selected immutable successor sources, never a moving ref."""

from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
import importlib.util
from pathlib import Path
import sys
from urllib.request import urlopen


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("specification_tool", Path(__file__).with_name("manage.py"))
assert SPEC is not None and SPEC.loader is not None
tool = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(tool)

MERGED = "4a7e7f5e333f359e77d1bef5641d16120305352f"
DRAFT = "811de7d20711dd3edc8c676ae634adebee235188"
CLOSURE = "514f6eacd6297821e949dcb81a03e2943f5cb88e"
API = "5f9d0134407927c0fae3b95e0aa844548e3756d6"
MEDIA = "9613f002c82c9568fbefc5f1f0cf0490d6df572a"
CURRENT_MERGED = "d290b1ec38d9e5436decb5ba0854f2321401a118"
FROZEN_MERGED = "801aaf218a3d954c87f5014eb00c37aff93c001b"
CURRENT_DRAFT = "d757328e4e3ca70761244b4a77cacba6bfb5599b"
OPCUA_EVENTS = "f69085fb89b1204dd72d9dca00472a5f331be2d1"
HTTP_PROTOCOL_MODELS = "a1da400e938b420da1f097b58a73fd9f75deb9e5"
COMPATIBILITY_ADMISSION = "b0d2d81777dace4361227a9c37ddf5cd6629a563"
XREF_CREATION_EVENTS = "9e103e2be4de6a7d90630bd2b346e4d08c029cf1"
WOT_CLOSURE_CONTRACTS = "ab91328d0b210d054ed4278120fa949d13415ab2"
CLOUD_EVENT_VIEW = "890b4a62360f4b4869a16495523b7f7addba2e7e"


def sources() -> list[dict]:
    merged = {
        "id": "upstream-4a7e7f5e", "repository": "https://github.com/xregistry/spec",
        "commit": MERGED, "status": "merged", "files": [],
    }
    draft = {
        "id": "pr571-811de7d2", "repository": "https://github.com/xregistry/spec",
        "commit": DRAFT, "status": "working-draft", "files": [],
    }
    closure = {
        "id": "pr571-514f6eac", "repository": "https://github.com/xregistry/spec",
        "commit": CLOSURE, "status": "working-draft", "files": [],
    }
    api = {
        "id": "pr715-5f9d0134", "repository": "https://github.com/xregistry/spec",
        "commit": API, "status": "working-draft", "files": [],
    }
    media = {
        "id": "pr716-9613f002", "repository": "https://github.com/xregistry/spec",
        "commit": MEDIA, "status": "working-draft", "files": [],
    }
    for path, owner in (
        ("core/spec.md", "core"), ("core/model.md", "model"), ("core/http.md", "http"),
        ("core/events.md", "events"), ("endpoint/spec.md", "endpoint"),
        ("message/spec.md", "message"), ("schema/spec.md", "schema"),
        ("cloudevents/spec.md", "cloudevents"), ("pagination/spec.md", "pagination"),
    ):
        merged["files"].append({"path": path, "owner": owner, "kind": "normative", "role": "normative"})
    for owner in ("core", "endpoint", "message", "schema", "cloudevents"):
        merged["files"].append({"path": f"{owner}/model.json", "owner": owner,
                                "kind": "artifact", "role": "review-only"})
    merged["files"].append({"path": "core/model.schema.json", "owner": "model",
                            "kind": "artifact", "role": "review-only"})
    for owner in ("registry", "openusd", "wot", "api", "media-endpoints"):
        for filename, kind, role in (
            ("spec.md", "normative", "normative"), ("model.json", "artifact", "review-only"),
        ):
            draft["files"].append({
                "path": f"workingdrafts/models/{owner}/{filename}", "owner": owner,
                "kind": kind, "role": role,
            })
    for owner in ("http-federation", "mapping", "file", "git", "oci", "opcua"):
        draft["files"].append({"path": f"workingdrafts/bindings/{owner}.md", "owner": owner,
                               "kind": "normative", "role": "normative"})
    for path, owner, kind, role in (
        ("workingdrafts/federation/spec.md", "federation", "normative", "normative"),
        ("workingdrafts/publication/spec.md", "publication", "normative", "normative"),
        ("workingdrafts/publication/model.json", "publication", "artifact", "review-only"),
        ("workingdrafts/publication/design/design.md", "wot", "informative", "selected-design"),
        ("workingdrafts/models/wot/tests/resolution/vectors.json", "wot", "artifact", "review-only"),
        ("workingdrafts/models/wot/schemas/publication-closure.schema.json", "wot", "artifact", "review-only"),
        ("workingdrafts/models/media-endpoints/protocol-options.json", "media-endpoints", "artifact", "review-only"),
        ("workingdrafts/models/media-endpoints/source.json", "media-endpoints", "artifact", "review-only"),
        ("workingdrafts/models/media-endpoints/tests/validation-vectors.json", "media-endpoints", "artifact", "review-only"),
    ):
        draft["files"].append({"path": path, "owner": owner, "kind": kind, "role": role})
    for path, kind in (
        ("workingdrafts/models/api/spec.md", "normative"),
        ("workingdrafts/models/api/model.json", "artifact"),
    ):
        api["files"].append({"path": path, "owner": "api", "kind": kind, "role": "review-only"})
    media["files"].append({"path": "workingdrafts/models/media-endpoints.md", "owner": "media-endpoints",
                           "kind": "normative", "role": "review-only"})
    for path, owner, kind, role in (
        ("workingdrafts/models/wot/spec.md", "wot", "normative", "normative"),
        ("workingdrafts/publication/spec.md", "publication", "normative", "normative"),
        ("workingdrafts/publication/design/design.md", "wot", "informative", "selected-design"),
    ):
        closure["files"].append({"path": path, "owner": owner, "kind": kind, "role": role})
    replaced_paths = {entry["path"] for entry in closure["files"]}
    for entry in draft["files"]:
        if entry["path"] in replaced_paths:
            entry["role"] = "review-only"
    current_merged = deepcopy(merged)
    current_merged.update(id="upstream-d290b1ec", commit=CURRENT_MERGED)
    current_draft = deepcopy(draft)
    current_draft.update(id="pr571-d757328e", commit=CURRENT_DRAFT)
    current_roles = {entry["path"]: entry["role"] for entry in closure["files"]}
    for entry in current_draft["files"]:
        entry["role"] = current_roles.get(entry["path"], entry["role"])
        if entry["path"] == "workingdrafts/models/wot/spec.md":
            entry["role"] = "review-only"
    for path in (
        "workingdrafts/models/wot/samples/README.md",
        "workingdrafts/models/wot/samples/cloudevents.json",
        "workingdrafts/models/wot/tests/test_wot_examples.py",
    ):
        current_draft["files"].append({
            "path": path, "owner": "wot", "kind": "artifact", "role": "review-only",
        })
    for source in (merged, draft, closure):
        for entry in source["files"]:
            entry["role"] = "review-only"
    opcua_events = {
        "id": "pr571-f69085fb", "repository": "https://github.com/xregistry/spec",
        "commit": OPCUA_EVENTS, "status": "working-draft", "files": [
            {"path": "workingdrafts/bindings/opcua.md", "owner": "opcua",
             "kind": "normative", "role": "normative"},
        ],
    }
    http_protocol_models = {
        "id": "pr571-a1da400e", "repository": "https://github.com/marcschier/xregistry-drafts",
        "commit": HTTP_PROTOCOL_MODELS, "status": "working-draft", "files": [
            {"path": "endpoint/model.json", "owner": "endpoint", "kind": "artifact", "role": "review-only"},
            {"path": "message/model.json", "owner": "message", "kind": "artifact", "role": "review-only"},
            {"path": "workingdrafts/models/media-endpoints/sources/endpoint-model.source.json",
             "owner": "media-endpoints", "kind": "artifact", "role": "review-only"},
            {"path": "workingdrafts/models/media-endpoints/sources/message-model.source.json",
             "owner": "media-endpoints", "kind": "artifact", "role": "review-only"},
        ],
    }
    for entry in current_draft["files"]:
        if entry["path"] == "workingdrafts/bindings/opcua.md":
            entry["role"] = "review-only"
    compatibility_admission = {
        "id": "pr677-b0d2d817", "repository": "https://github.com/xregistry/spec",
        "commit": COMPATIBILITY_ADMISSION, "status": "working-draft", "files": [
            {"path": "core/spec.md", "owner": "core", "kind": "normative", "role": "review-only"},
        ],
    }
    xref_creation_events = {
        "id": "pr686-9e103e2b", "repository": "https://github.com/xregistry/spec",
        "commit": XREF_CREATION_EVENTS, "status": "working-draft", "files": [
            {"path": "core/events.md", "owner": "events", "kind": "normative", "role": "review-only"},
        ],
    }
    wot_closure_contracts = {
        "id": "pr571-ab91328d", "repository": "https://github.com/marcschier/xregistry-drafts",
        "commit": WOT_CLOSURE_CONTRACTS, "status": "working-draft", "files": [
            {"path": "workingdrafts/models/wot/spec.md", "owner": "wot",
             "kind": "normative", "role": "review-only"},
            {"path": "workingdrafts/models/wot/schemas/publication-closure.schema.json", "owner": "wot",
             "kind": "artifact", "role": "review-only"},
            {"path": "workingdrafts/models/wot/tests/test_wot_examples.py", "owner": "wot",
             "kind": "artifact", "role": "review-only"},
            {"path": "workingdrafts/publication/design/design.md", "owner": "wot",
             "kind": "informative", "role": "review-only"},
        ],
    }
    cloud_event_view = {
        "id": "pr571-890b4a62", "repository": "https://github.com/marcschier/xregistry-drafts",
        "commit": CLOUD_EVENT_VIEW, "status": "working-draft", "files": [
            {"path": "workingdrafts/models/wot/spec.md", "owner": "wot",
             "kind": "normative", "role": "normative"},
        ],
    }
    frozen_merged = deepcopy(current_merged)
    frozen_merged.update(id="upstream-801aaf21", commit=FROZEN_MERGED)
    for entry in frozen_merged["files"]:
        entry["role"] = "review-only"
    for owner in ("endpoint", "message", "schema", "cloudevents"):
        for filename in ("document-schema.json", "document-schema.avsc",
                         "document-schema.struct.json", "openapi.json"):
            frozen_merged["files"].append({
                "path": f"{owner}/schemas/{filename}", "owner": owner,
                "kind": "artifact", "role": "review-only",
            })
    for path in ("tools/schema-generator.py", "tools/test_schema_generator.py",
                 "core/templates/xregistry_openapi_template.json"):
        frozen_merged["files"].append({
            "path": path, "owner": "model", "kind": "artifact", "role": "review-only",
        })
    result = [merged, draft, closure, api, media, current_merged, current_draft, opcua_events,
              http_protocol_models, compatibility_admission, xref_creation_events, wot_closure_contracts,
              cloud_event_view, frozen_merged]
    for source in result:
        source["files"].append({"path": "LICENSE", "owner": "license", "kind": "license", "role": "review-only"})
    return result


def acquire(source: dict, entry: dict) -> tuple[dict, dict, bytes]:
    repository = source["repository"].removeprefix("https://github.com/")
    url = f"https://raw.githubusercontent.com/{repository}/{source['commit']}/{entry['path']}"
    with urlopen(url, timeout=60) as response:
        data = response.read(tool.MAX_JSON_BYTES + 1)
    if len(data) > tool.MAX_JSON_BYTES:
        raise tool.SpecificationError(f"Successor source exceeds its byte limit: {url}")
    return source, entry, data


def freeze(root: Path = ROOT) -> None:
    selected = sources()
    requests = [(source, entry) for source in selected for entry in source["files"]]
    # Finish acquisition before writing any source or changing the selected composition.
    with ThreadPoolExecutor(max_workers=4) as executor:
        acquired = list(executor.map(lambda pair: acquire(*pair), requests))
    frozen = {entry["path"]: data for source, entry, data in acquired
              if source["id"] == "upstream-801aaf21"}
    for source, entry, data in acquired:
        if source["status"] == "merged" and entry["role"] == "normative" and frozen:
            if frozen.get(entry["path"]) != data:
                raise tool.SpecificationError(
                    f"Frozen merged normative source differs; select its composition explicitly: {entry['path']}"
                )
    for source, entry, data in acquired:
        path = tool.contained(root / tool.SUCCESSOR_CORPUS, f"{source['id']}/{entry['path']}")
        if path.exists() and path.read_bytes() != data:
            raise tool.SpecificationError(f"Refusing to overwrite changed successor evidence: {path}")
    manifest = {"schemaVersion": 1, "sources": [], "composition": []}
    for source in selected:
        manifest["sources"].append({**source, "files": []})
    destinations = {source["id"]: source for source in manifest["sources"]}
    for source, entry, data in acquired:
        path = tool.contained(root / tool.SUCCESSOR_CORPUS, f"{source['id']}/{entry['path']}")
        tool.write_output(path, data)
        destinations[source["id"]]["files"].append({
            "path": entry["path"], "owner": entry["owner"], "kind": entry["kind"],
            "bytes": len(data), "sha256": tool.sha256(data),
        })
        manifest["composition"].append({
            "source": source["id"], "path": entry["path"], "role": entry["role"],
        })
    encoded = tool.encoded(manifest)
    tool.write_output(root / tool.SUCCESSORS, encoded)
    for path in (root / tool.LOCK, root / "eng/specification/scope.json"):
        document = tool.read_json(path)
        document["successorsSha256"] = tool.sha256(encoded)
        tool.write_output(path, tool.encoded(document))
    tool.verify_corpus(root)
    print(f"Frozen {len(acquired)} successor files from {len(selected)} immutable sources.")


if __name__ == "__main__":
    try:
        freeze()
    except (OSError, ValueError) as error:
        print(f"Successor acquisition failed: {error}", file=sys.stderr)
        raise SystemExit(1)
