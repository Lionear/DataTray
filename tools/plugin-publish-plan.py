#!/usr/bin/env python3
"""Decide which plugins need publishing to the store, by comparing the repo against the live index.

The signal is deliberately *not* "these files changed". A path diff republishes on a whitespace fix and
says nothing about intent; the version in `plugin.json` says exactly one thing, on purpose: this is a
new release of this plugin. So a plugin is planned for publish when its manifest version is newer than
the newest version the live store already advertises for that id.

The published version carries a build number (`<manifest version>.<run number>`, see
`.github/workflows/publish-plugins.yml`), so the comparison is on the three-segment *base*: repo 1.2.0
against published 1.2.0.41 is "already published", against 1.1.0.41 is "publish".

Reading the live index rather than a marker in this repo is the same choice
`plugins.lionear.dev/tools/verify-store.py` makes: what a user's app fetches is the only fact that
decides whether something is published, and it survives a force-push, a reverted commit or a manual
release done outside this workflow.

Usage:
    plugin-publish-plan.py [--index URL|PATH] [--only id,id] [--github-output]
    plugin-publish-plan.py --self-test

Prints the plan as JSON on stdout. With --github-output it also writes `matrix` and `count` to
$GITHUB_OUTPUT for a job matrix.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.request
from pathlib import Path

DEFAULT_INDEX = "https://plugins.lionear.dev/sql-explorer/index.json"
TIMEOUT = 60

# Where a plugin project can live. Both are real: the store-only plugins sit under plugins/, while the
# bundled engines (and the MCP server) are published to the store as well, from src/.
PLUGIN_GLOBS = ("plugins/*/plugin.json", "src/*/plugin.json")

# Plugins that exist in the tree but are not store products. Listed by id rather than skipped silently,
# so "not in the index" keeps meaning "someone has to add a store entry for this one".
NOT_PUBLISHED = {
    "template",  # plugins/Providers.Template — Debug-only reference implementation for plugin authors.
}


def version_key(version: str) -> tuple:
    """Ordering that mirrors DataTray.Core.Store.SemVer: numeric dotted core, pre-release below release.

    Returns a sortable tuple; a non-numeric core falls back to a string compare rather than throwing, so
    one malformed entry in the index can never take the whole plan down.
    """
    core, _, pre = version.partition("+")[0].partition("-")
    parts = core.split(".")
    if not all(p.isdigit() for p in parts):
        return (1, (), 0, version)
    # Pad to four so 1.2.0 and 1.2.0.41 compare segment by segment, like SemVer.Compare does.
    nums = tuple(int(p) for p in parts[:4]) + (0,) * (4 - len(parts[:4]))
    # A pre-release sorts below the same release (SemVer §11).
    return (0, nums, 0 if pre else 1, pre)


def base_version(version: str) -> str:
    """The three-segment base of a published version — `1.2.0.41` and `1.2.0` are the same release."""
    return ".".join(version.partition("-")[0].split(".")[:3])


def fetch_index(location: str) -> dict:
    if "://" in location:
        with urllib.request.urlopen(location, timeout=TIMEOUT) as response:
            return json.loads(response.read())
    return json.loads(Path(location).read_text(encoding="utf-8"))


def published_versions(index: dict) -> dict[str, list[str]]:
    return {p["id"]: [v["version"] for v in p.get("versions", [])] for p in index.get("plugins", [])}


def plan(root: Path, index: dict, only: set[str] | None = None) -> tuple[list[dict], list[str]]:
    """Returns (jobs, warnings). One job per plugin that should be built and published."""
    published = published_versions(index)
    jobs: list[dict] = []
    warnings: list[str] = []

    manifests = sorted(m for glob in PLUGIN_GLOBS for m in root.glob(glob))
    for manifest_path in manifests:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        plugin_id = manifest["id"]
        version = manifest.get("version")
        directory = manifest_path.parent

        if plugin_id in NOT_PUBLISHED:
            continue

        if only is not None:
            if plugin_id not in only:
                continue
        elif plugin_id not in published:
            # A first release needs a store entry with description, icon and homepage — none of which the
            # loader manifest carries. Warn instead of guessing: the entry is written once, by hand, and
            # from then on this workflow only appends versions to it.
            warnings.append(
                f"{plugin_id} ({directory}) has no entry in the store index; "
                f"add one by hand before it can be published"
            )
            continue

        if not version:
            warnings.append(f"{plugin_id} ({directory}) has no version in plugin.json; skipped")
            continue

        newest = max(published.get(plugin_id, []), key=version_key, default=None)
        if only is None and newest is not None and version_key(version) <= version_key(base_version(newest)):
            continue

        projects = sorted(directory.glob("*.csproj"))
        if len(projects) != 1:
            warnings.append(f"{plugin_id} ({directory}) has {len(projects)} .csproj files; skipped")
            continue

        jobs.append(
            {
                "id": plugin_id,
                "name": manifest["name"],
                "type": manifest["type"],
                "dir": str(directory),
                "project": str(projects[0]),
                "manifest": str(manifest_path),
                "version": version,
                # "" rather than None: this dict becomes a GitHub Actions matrix entry, and a null
                # there is not a value the runner will take.
                "published": newest or "",
                "hostApiVersion": manifest["hostApiVersion"],
                "entryAssembly": manifest["entryAssembly"],
            }
        )

    return jobs, warnings


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--index", default=DEFAULT_INDEX, help="store index URL or path")
    parser.add_argument("--root", default=".", help="repository root")
    parser.add_argument("--only", default="", help="comma-separated plugin ids to force, ignoring the version check")
    parser.add_argument("--github-output", action="store_true", help="also write matrix/count to $GITHUB_OUTPUT")
    parser.add_argument("--self-test", action="store_true", help="run the built-in checks and exit")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    only = {i.strip() for i in args.only.split(",") if i.strip()} or None
    jobs, warnings = plan(Path(args.root), fetch_index(args.index), only)

    for warning in warnings:
        print(f"::warning::{warning}", file=sys.stderr)
    for job in jobs:
        print(f"{job['id']}: {job['published'] or '(none)'} -> {job['version']}", file=sys.stderr)
    if not jobs:
        print("Nothing to publish.", file=sys.stderr)

    print(json.dumps(jobs, indent=2))

    if args.github_output and (out := os.environ.get("GITHUB_OUTPUT")):
        with open(out, "a", encoding="utf-8") as handle:
            handle.write(f"matrix={json.dumps({'include': jobs})}\n")
            handle.write(f"count={len(jobs)}\n")

    return 0


def self_test() -> int:
    """The version arithmetic every publish decision rests on. Run: plugin-publish-plan.py --self-test"""
    assert version_key("1.2.0.41") > version_key("1.2.0"), "a build number is newer than the bare release"
    assert version_key("1.2.0") > version_key("1.1.9.99"), "the base decides before the build number"
    assert version_key("1.2.0") > version_key("1.2.0-nightly.3"), "a pre-release is below its release"
    assert version_key("1.10.0") > version_key("1.9.0"), "segments compare numerically, not as text"
    assert version_key("not-a-version") > version_key("1.0.0"), "a malformed version sorts last, not throws"

    assert base_version("1.2.0.41") == "1.2.0"
    assert base_version("1.2.0") == "1.2.0"
    assert base_version("1.2.0-nightly.3.7") == "1.2.0"

    # An already-published plugin is planned only when its manifest base is newer than what is live.
    import tempfile

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        for plugin_id, version in (("alpha", "1.2.0"), ("beta", "0.3.0"), ("template", "9.9.9"), ("newbie", "1.0.0")):
            directory = root / "plugins" / plugin_id
            directory.mkdir(parents=True)
            (directory / f"DataTray.{plugin_id}.csproj").write_text("<Project/>")
            (directory / "plugin.json").write_text(
                json.dumps(
                    {
                        "id": plugin_id,
                        "type": "provider",
                        "name": plugin_id,
                        "version": version,
                        "hostApiVersion": 30,
                        "entryAssembly": f"DataTray.{plugin_id}.dll",
                    }
                )
            )

        index = {
            "plugins": [
                {"id": "alpha", "versions": [{"version": "1.2.0.41"}]},  # same base, already published
                {"id": "beta", "versions": [{"version": "0.2.0.7"}, {"version": "0.1.0"}]},  # behind
                {"id": "template", "versions": []},
            ]
        }

        jobs, warnings = plan(root, index)
        assert [j["id"] for j in jobs] == ["beta"], jobs
        assert jobs[0]["published"] == "0.2.0.7"
        assert jobs[0]["project"].endswith("DataTray.beta.csproj")
        assert any("newbie" in w for w in warnings), warnings
        assert not any("template" in w for w in warnings), "the reference plugin is not a store product"

        forced, _ = plan(root, index, only={"alpha"})
        assert [j["id"] for j in forced] == ["alpha"], "--only republishes regardless of the version check"

    print("self-test ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
