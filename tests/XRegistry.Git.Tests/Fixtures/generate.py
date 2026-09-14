"""Explicit fixture-generation tool only. Tests and shipping code never execute it."""

import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile
import zlib


def git(repo, *args, data=None):
    env = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")}
    env.update(GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull)
    result = subprocess.run(
        ["git", "--no-pager", "-C", str(repo), "--git-dir=" + str(repo), *args],
        input=data, capture_output=True, check=False, env=env)
    if result.returncode:
        raise RuntimeError(result.stderr.decode("utf-8", errors="replace"))
    return result.stdout


def oid(algorithm, kind, content):
    canonical = kind.encode("ascii") + b" " + str(len(content)).encode("ascii") + b"\0" + content
    return hashlib.new(algorithm, canonical).hexdigest()


def header(kind, size):
    value = (kind << 4) | (size & 15)
    size >>= 4
    result = bytearray([value | (128 if size else 0)])
    while size:
        value = size & 127
        size >>= 7
        result.append(value | (128 if size else 0))
    return bytes(result)


def ofs(distance):
    result = bytearray([distance & 127])
    while distance >> 7:
        distance = (distance >> 7) - 1
        result.insert(0, 128 | (distance & 127))
    return bytes(result)


def pack(algorithm, entries, version=2):
    output = bytearray(b"PACK" + struct.pack(">II", version, len(entries)))
    offsets = []
    for kind, data, base in entries:
        offset = len(output)
        output.extend(header(kind, len(data)))
        if kind == 6:
            output.extend(ofs(offset - offsets[base]))
        elif kind == 7:
            output.extend(bytes.fromhex(base))
        output.extend(zlib.compress(data, 9))
        offsets.append(offset)
    output.extend(hashlib.new(algorithm, output).digest())
    return bytes(output)


def generate():
    result = {
        "provenance": {
            "gitVersion": subprocess.check_output(["git", "--version"], text=True).strip(),
            "pythonZlibVersion": zlib.ZLIB_VERSION,
            "commands": [
                "git --no-pager -C TEMP --git-dir=TEMP init --bare --object-format=sha1 .",
                "git --no-pager -C TEMP --git-dir=TEMP init --bare --object-format=sha256 .",
                "git --no-pager -C TEMP --git-dir=TEMP hash-object -t TYPE -w --stdin",
                "git --no-pager -C TEMP --git-dir=TEMP index-pack --strict --stdin",
            ],
            "expectations": "Git hash-object outputs independently compared with Python hashlib over exact canonical bytes; every pack accepted by reference Git index-pack --strict.",
        },
        "formats": {},
    }
    for algorithm in ("sha1", "sha256"):
        with tempfile.TemporaryDirectory(prefix="xregistry-git-fixtures-") as directory:
            repo = Path(directory)
            git(repo, "init", "--bare", "--object-format=" + algorithm, ".")
            objects = {}

            def add(name, kind, content):
                actual = git(repo, "hash-object", "-t", kind, "-w", "--stdin", data=content).decode("ascii").strip()
                expected = oid(algorithm, kind, content)
                assert actual == expected, (name, actual, expected)
                canonical = kind.encode("ascii") + b" " + str(len(content)).encode("ascii") + b"\0" + content
                loose = zlib.compress(canonical, 9)
                objects[name] = {
                    "type": kind, "oid": actual, "content": content.hex(),
                    "loose": loose.hex(), "looseSha256": hashlib.sha256(loose).hexdigest(),
                }
                return actual

            empty = add("empty", "blob", b"")
            hello = add("hello", "blob", b"hello\n")
            base = add("base", "blob", b"hello world\n")
            changed = add("changed", "blob", b"hello managed world\n")
            final = add("final", "blob", b"hello managed world!\n")
            binary = add("binary", "blob", b"\0\xff\r\nbinary\n")
            nested = add("nested", "tree", b"100644 raw.bin\0" + bytes.fromhex(binary))
            root = add("root", "tree",
                       b"100644 empty\0" + bytes.fromhex(empty)
                       + b"100755 hello\0" + bytes.fromhex(hello)
                       + b"40000 nested\0" + bytes.fromhex(nested))
            commit_content = (
                "tree " + root + "\nauthor Fixture <fixture@example.invalid> 946684800 +0000\n"
                "committer Fixture <fixture@example.invalid> 946684800 +0000\n\nfixture\n").encode("ascii")
            commit = add("commit", "commit", commit_content)
            tag_content = (
                "object " + commit + "\ntype commit\ntag fixture-v1\n"
                "tagger Fixture <fixture@example.invalid> 946684800 +0000\n\nfixture tag\n").encode("ascii")
            tag = add("tag", "tag", tag_content)
            add("nestedTag", "tag", (
                "object " + tag + "\ntype tag\ntag nested-v1\n"
                "tagger Fixture <fixture@example.invalid> 946684800 +0000\n\nnested\n").encode("ascii"))

            delta = bytes([12, 20, 0x90, 6, 8]) + b"managed " + bytes([0x91, 6, 6])
            delta2 = bytes([20, 21, 0x90, 19, 2]) + b"!\n"
            base_bytes = bytes.fromhex(objects["base"]["content"])
            plain = [(3, base_bytes, None)]
            all_entries = [
                ({"commit": 1, "tree": 2, "blob": 3, "tag": 4}[obj["type"]],
                 bytes.fromhex(obj["content"]), None)
                for obj in objects.values()
            ]
            packs = {
                "emptyV2": pack(algorithm, []),
                "plainV2": pack(algorithm, plain),
                "plainV3": pack(algorithm, plain, 3),
                "ofs": pack(algorithm, plain + [(6, delta, 0)]),
                "refBackward": pack(algorithm, plain + [(7, delta, base)]),
                "refForward": pack(algorithm, [(7, delta, base)] + plain),
                "ofsChain": pack(algorithm, plain + [(6, delta, 0), (6, delta2, 1)]),
                "refForwardChain": pack(algorithm, [(7, delta2, changed), (7, delta, base)] + plain),
                "snapshot": pack(algorithm, all_entries),
            }
            encoded_packs = {}
            for name, data in packs.items():
                git(repo, "index-pack", "--strict", "--stdin", data=data)
                encoded_packs[name] = {
                    "hex": data.hex(), "sha256": hashlib.sha256(data).hexdigest(),
                    "length": len(data),
                }
            result["formats"][algorithm] = {
                "objects": objects, "packs": encoded_packs,
                "delta": delta.hex(), "delta2": delta2.hex(),
            }
    output = Path(__file__).with_name("git-fixtures.json")
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="ascii", newline="\n")
    print("Fixture SHA-256:", hashlib.sha256(output.read_bytes()).hexdigest())
    print("Wrote:", output)


if __name__ == "__main__":
    generate()
