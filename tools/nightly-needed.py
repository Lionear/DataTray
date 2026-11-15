#!/usr/bin/env python3
"""Decides whether a scheduled nightly has anything new to build (SE-302).

Prints `build_needed=true|false` and a one-line `summary=` on stdout, ready for $GITHUB_OUTPUT.

A change is 'real' when at least one changed file falls outside the push trigger's `paths-ignore`.
That list is read from the workflow itself, so the two can never drift apart: the YAML `on:` block
cannot take a variable, which makes the workflow the only place that can own the list.

Everything that can be injected is injectable (--current, --previous, --changed-file), so the logic
runs without a runner. Without --changed-file the files come from `git diff` between the two commits.
The decision fails towards building: anything we cannot establish (no previous nightly, a previous
commit this clone cannot see) counts as 'build'.
"""
import argparse
import re
import subprocess
import sys


def read_push_paths_ignore(workflow_text):
    """The `paths-ignore` items under `on: push:`, in file order. Exits if there are none: an empty
    list would silently turn every file into a 'real' change (or worse, hide a renamed key)."""
    lines = workflow_text.splitlines()
    for i, line in enumerate(lines):
        if re.match(r"^  push:\s*$", line):
            start = i + 1
            break
    else:
        sys.exit("nightly-needed: no `push:` trigger found in the workflow")
    patterns = []
    in_list = False
    for line in lines[start:]:
        if re.match(r"^ {0,2}\S", line):  # left the push block (next trigger or top-level key)
            break
        if re.match(r"^\s+paths-ignore:\s*(#.*)?$", line):
            in_list = True
            continue
        m = re.match(r"^\s+-\s+[\"']?(.*?)[\"']?\s*(#.*)?$", line)
        if in_list and m:
            patterns.append(m.group(1))
        elif in_list and line.strip() and not line.strip().startswith("#"):
            break
    if not patterns:
        sys.exit("nightly-needed: `on.push.paths-ignore` is missing or empty in the workflow")
    return patterns


def glob_to_regex(glob):
    """GitHub path-filter globs: `**/` = any directories (or none), `**` = anything, `*` = anything
    but `/`, `?` = one character but `/`. The pattern must match the whole path."""
    out, i = [], 0
    while i < len(glob):
        if glob.startswith("**/", i):
            out.append("(?:.*/)?")
            i += 3
        elif glob.startswith("**", i):
            out.append(".*")
            i += 2
        elif glob[i] == "*":
            out.append("[^/]*")
            i += 1
        elif glob[i] == "?":
            out.append("[^/]")
            i += 1
        else:
            out.append(re.escape(glob[i]))
            i += 1
    return re.compile("".join(out))


def compile_patterns(globs):
    return [glob_to_regex(g) for g in globs]


def is_ignored(path, compiled):
    return any(rx.fullmatch(path) for rx in compiled)


def git_changed_files(previous, current):
    """None when git cannot answer (previous commit unknown to this clone, not an ancestor, ...)."""
    def git(*args):
        return subprocess.run(["git", *args], capture_output=True, text=True)

    if git("cat-file", "-e", f"{previous}^{{commit}}").returncode != 0:
        return None
    # --no-renames lists both sides of a rename, so a move from code into docs/ still counts.
    r = git("diff", "--name-only", "--no-renames", previous, current)
    return r.stdout.splitlines() if r.returncode == 0 else None


def decide(current, previous, previous_date, changed, ignore):
    short = previous[:7]
    when = f" ({previous_date})" if previous_date else ""
    if not previous:
        return True, "No previous nightly release recorded, building"
    if previous == current:
        return False, f"No changes since {short}{when}, nightly skipped"
    if changed is None:
        return True, f"Cannot compare with {short}{when}, building"
    real = [f for f in changed if not is_ignored(f, ignore)]
    if not real:
        return False, f"No changes since {short}{when}, nightly skipped (only ignored files differ: {len(changed)})"
    shown = ", ".join(real[:3]) + (", ..." if len(real) > 3 else "")
    return True, f"{len(real)} changed file(s) since {short}{when}, building: {shown}"


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--workflow", default=".github/workflows/build.yml")
    ap.add_argument("--current", required=True, help="commit about to be built")
    ap.add_argument("--previous", default="", help="commit of the last published nightly; empty = none")
    ap.add_argument("--previous-date", default="", help="its publish date, for the summary only")
    ap.add_argument("--changed-file", action="append", help="inject a changed path (repeatable)")
    a = ap.parse_args()

    with open(a.workflow, encoding="utf-8") as f:
        ignore = compile_patterns(read_push_paths_ignore(f.read()))

    if a.changed_file is not None:
        changed = a.changed_file
    elif a.previous:
        changed = git_changed_files(a.previous, a.current)
    else:
        changed = None

    build, summary = decide(a.current, a.previous, a.previous_date, changed, ignore)
    print(f"build_needed={'true' if build else 'false'}")
    print(f"summary={summary}")


if __name__ == "__main__":
    main()
