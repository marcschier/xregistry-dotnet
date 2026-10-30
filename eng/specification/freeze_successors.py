#!/usr/bin/env python3
# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

"""Acquire the explicitly selected immutable successor sources, never a moving ref."""

from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
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
    result = [merged, draft, closure, api, media]
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
