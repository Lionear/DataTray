#!/usr/bin/env python3
"""Add published plugin versions to the store's `index.json`.

Runs in a checkout of `Lionear/plugins.lionear.dev` (the store repo), against records written by the
publish jobs in `.github/workflows/publish-plugins.yml`. One record per plugin:

    {"id": "duckdb", "version": "1.0.0.41", "minHostApiVersion": 27,
     "downloadUrl": "https://…/duckdb-1.0.0.41.zip", "sha256": "…", "size": 123, "notes": "…"}

Only *versions* are appended. Creating a `StoreEntry` needs a description, an icon and a homepage —
none of which live in the loader manifest — so an unknown id is an error here, not a guess: add the
entry (and `sql-explorer/assets/<id>.png`) by hand once, and every later release lands automatically.

Old versions are kept. `HighestCompatibleVersion` in the app picks the best match for the host it runs
on, so removing one can strand a user on an older build (see the release procedure).

Usage:  plugin-store-entry.py --index sql-explorer/index.json --records records/
        plugin-store-entry.py --self-test
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

FIELDS = ("version", "minHostApiVersion", "downloadUrl", "sha256", "size", "notes")


def apply_record(index: dict, record: dict) -> str:
    """Adds (or replaces) one version on its entry. Returns a one-line description of what changed."""
    entry = next((p for p in index.get("plugins", []) if p["id"] == record["id"]), None)
    if entry is None:
        raise SystemExit(
            f"::error::no store entry for '{record['id']}' — add one to index.json by hand "
            f"(name, description, homepage, iconUrl) before publishing it"
        )

    version = {field: record[field] for field in FIELDS if record.get(field) is not None}
    versions = list(entry.get("versions", []))
    for i, existing in enumerate(versions):
        if existing["version"] == version["version"]:
            versions[i] = version
            entry["versions"] = versions
            return f"{record['id']} {version['version']} (replaced)"

    versions.append(version)
    entry["versions"] = versions
    return f"{record['id']} {version['version']} (added)"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--index", required=True, help="path to the store's index.json")
    parser.add_argument("--records", required=True, help="directory of publish records (*.json)")
    parser.add_argument("--self-test", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args()

    index_path = Path(args.index)
    index = json.loads(index_path.read_text(encoding="utf-8"))

    records = sorted(Path(args.records).rglob("*.json"))
    if not records:
        print("No publish records; index.json untouched.", file=sys.stderr)
        return 0

    for record_path in records:
        print(apply_record(index, json.loads(record_path.read_text(encoding="utf-8"))))

    # Two-space indent + trailing newline: the file is maintained by hand as often as by this script,
    # and a formatting change on every run would bury the one line that actually differs.
    index_path.write_text(json.dumps(index, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return 0


def self_test() -> int:
    index = {"plugins": [{"id": "duckdb", "versions": [{"version": "1.0.0", "sha256": "aa"}]}]}
    record = {
        "id": "duckdb",
        "version": "1.1.0.7",
        "minHostApiVersion": 27,
        "downloadUrl": "https://example/duckdb-1.1.0.7.zip",
        "sha256": "bb",
        "size": 12,
        "notes": None,
    }

    assert apply_record(index, record) == "duckdb 1.1.0.7 (added)"
    added = index["plugins"][0]["versions"][1]
    assert added["version"] == "1.1.0.7" and added["size"] == 12
    assert "notes" not in added, "an empty note is left out, not written as null"
    assert len(index["plugins"][0]["versions"]) == 2, "older versions stay"

    assert apply_record(index, dict(record, sha256="cc")) == "duckdb 1.1.0.7 (replaced)"
    assert len(index["plugins"][0]["versions"]) == 2, "re-publishing the same version replaces it"
    assert index["plugins"][0]["versions"][1]["sha256"] == "cc"

    try:
        apply_record(index, dict(record, id="unknown"))
    except SystemExit:
        pass
    else:
        raise AssertionError("an unknown id must fail loudly")

    print("self-test ok")
    return 0


if __name__ == "__main__":
    sys.exit(self_test() if "--self-test" in sys.argv else main())
