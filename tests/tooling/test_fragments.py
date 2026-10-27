"""Exercise the hook against real isolated Git repositories and staged snapshots."""

import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SOURCE = Path(__file__).resolve().parents[2] / "tools/check_fragments.py"


class FragmentChecks(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        (self.root / "tools").mkdir()
        (self.root / "changes").mkdir()
        shutil.copyfile(SOURCE, self.root / "tools/check_fragments.py")
        self.git("init", "-b", "main")
        self.git("config", "user.name", "Test")
        self.git("config", "user.email", "test@example.invalid")
        self.git("add", ".")
        self.git("commit", "-m", "baseline")
        self.git("branch", "base")

    def git(self, *args):
        return subprocess.check_output(
            ["git", *args], cwd=self.root, text=True, stderr=subprocess.STDOUT
        )

    def check(self, *args):
        return subprocess.run(
            [sys.executable, "tools/check_fragments.py", "--base", "base", *args],
            cwd=self.root,
            text=True,
            capture_output=True,
        )

    def test_unstaged_fragment_cannot_satisfy_commit_check(self):
        (self.root / "changes/5.bugfix.md").write_text(
            "Fix visible behavior.\n", encoding="utf-8"
        )
        self.assertNotEqual(0, self.check("--staged").returncode)
        self.git("add", "changes/5.bugfix.md")
        self.assertEqual(0, self.check("--staged").returncode)

    def test_empty_index_is_not_hidden_by_nonempty_worktree(self):
        fragment = self.root / "changes/5.bugfix.md"
        fragment.write_text("", encoding="utf-8")
        self.git("add", ".")
        fragment.write_text("Only unstaged text.\n", encoding="utf-8")
        self.assertNotEqual(0, self.check("--staged").returncode)

    def test_committed_fragment_satisfies_pr_check_and_bad_name_fails(self):
        (self.root / "changes/5.feature.md").write_text(
            "New behavior.\n", encoding="utf-8"
        )
        self.git("add", ".")
        self.git("commit", "-m", "feature")
        self.assertEqual(0, self.check().returncode)
        (self.root / "changes/unknown.txt").write_text(
            "Wrong filename.\n", encoding="utf-8"
        )
        self.assertNotEqual(0, self.check().returncode)


if __name__ == "__main__":
    unittest.main()
