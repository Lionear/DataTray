"""Tests for tools/nightly-needed.py (SE-302): the decision whether a scheduled nightly has to build.

Run:  python3 -m unittest discover -s tools -p 'test_*.py'
"""
import importlib.util
import pathlib
import subprocess
import sys
import unittest

TOOLS = pathlib.Path(__file__).resolve().parent
REPO = TOOLS.parent
WORKFLOW = REPO / ".github" / "workflows" / "build.yml"
SCRIPT = TOOLS / "nightly-needed.py"

spec = importlib.util.spec_from_file_location("nightly_needed", SCRIPT)
nn = importlib.util.module_from_spec(spec)
spec.loader.exec_module(nn)

OLD = "6805704f1c2a52744cc77f9a36cf7a1bfb7015ef"
NEW = "8ab5cf4a0000000000000000000000000000beef"


def run(*args):
    """Runs the CLI like the workflow does and returns its key=value output as a dict."""
    out = subprocess.run([sys.executable, str(SCRIPT), "--workflow", str(WORKFLOW), *args],
                         check=True, capture_output=True, text=True).stdout
    return dict(line.split("=", 1) for line in out.splitlines() if "=" in line)


class Decision(unittest.TestCase):
    def test_same_commit_skips(self):
        r = run("--current", OLD, "--previous", OLD, "--previous-date", "2026-11-15", "--changed-file", "src/A.cs")
        self.assertEqual(r["build_needed"], "false")
        self.assertIn("No changes since 6805704 (2026-11-15), nightly skipped", r["summary"])

    def test_no_commits_without_file_list_skips(self):
        r = run("--current", OLD, "--previous", OLD, "--previous-date", "2026-11-15")
        self.assertEqual(r["build_needed"], "false")

    def test_only_docs_and_markdown_skip(self):
        r = run("--current", NEW, "--previous", OLD, "--previous-date", "2026-11-15",
                "--changed-file", "README.md", "--changed-file", "docs/PLUGINS.md",
                "--changed-file", "src/Sub/notes.md", "--changed-file", "docs/img/a.png",
                "--changed-file", "LICENSE", "--changed-file", ".gitignore", "--changed-file", ".idea/x.xml",
                "--changed-file", "src/App/App.csproj.DotSettings", "--changed-file", "App.sln.DotSettings.user")
        self.assertEqual(r["build_needed"], "false")
        self.assertIn("No changes since 6805704 (2026-11-15), nightly skipped", r["summary"])

    def test_real_change_builds(self):
        r = run("--current", NEW, "--previous", OLD, "--previous-date", "2026-11-15",
                "--changed-file", "README.md", "--changed-file", "src/DataTray.Core/Foo.cs")
        self.assertEqual(r["build_needed"], "true")

    def test_workflow_file_itself_is_a_real_change(self):
        r = run("--current", NEW, "--previous", OLD, "--changed-file", ".github/workflows/build.yml")
        self.assertEqual(r["build_needed"], "true")

    def test_changelog_fragment_is_markdown_and_ignored(self):
        # Same as the push trigger: **.md covers changelog.d/*.md.
        r = run("--current", NEW, "--previous", OLD, "--changed-file", "changelog.d/SE-1.fixed.md")
        self.assertEqual(r["build_needed"], "false")

    def test_gitignore_only_matches_at_the_root(self):
        # paths-ignore `.gitignore` is anchored; a nested one is a different path and counts.
        r = run("--current", NEW, "--previous", OLD, "--changed-file", "src/.gitignore")
        self.assertEqual(r["build_needed"], "true")

    def test_no_previous_nightly_builds(self):
        r = run("--current", NEW, "--changed-file", "README.md")
        self.assertEqual(r["build_needed"], "true")
        self.assertIn("No previous nightly", r["summary"])

    def test_failed_previous_nightly_builds_again_without_new_commits(self):
        # A failed/cancelled nightly publishes nothing, so there is no 'previous' to compare with.
        # The workflow passes an empty --previous in that case; the same commit must still build.
        r = run("--current", OLD, "--previous", "")
        self.assertEqual(r["build_needed"], "true")

    def test_unreachable_previous_builds_in_git_mode(self):
        r = run("--current", "HEAD", "--previous", "0" * 40, "--previous-date", "2026-11-15")
        self.assertEqual(r["build_needed"], "true")


class IgnoreList(unittest.TestCase):
    def test_list_is_read_from_the_push_trigger(self):
        self.assertEqual(
            nn.read_push_paths_ignore(WORKFLOW.read_text()),
            ["**.md", "docs/**", "LICENSE", ".gitignore", ".idea/**", "**/*.DotSettings*"])

    def test_missing_list_is_an_error_not_an_empty_list(self):
        with self.assertRaises(SystemExit):
            nn.read_push_paths_ignore("on:\n  push:\n    branches: [main]\n")

    def test_glob_semantics(self):
        ign = nn.compile_patterns(["**.md", "docs/**", "**/*.DotSettings*", ".gitignore"])
        for p in ["a.md", "x/y/z.md", "docs/a/b.png", "a.DotSettings", "x/a.DotSettings.user", ".gitignore"]:
            self.assertTrue(nn.is_ignored(p, ign), p)
        for p in ["src/a.cs", "mddocs/a.cs", "docs", "x/.gitignore", "a.md.cs"]:
            self.assertFalse(nn.is_ignored(p, ign), p)


if __name__ == "__main__":
    unittest.main()
