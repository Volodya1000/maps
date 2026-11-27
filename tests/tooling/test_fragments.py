"""Exercise the hook against real isolated Git repositories and staged snapshots."""

import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SOURCE = Path(__file__).resolve().parents[2] / "tools/check_fragments.py"


class FragmentChecks(unittest.TestCase):
    def setUp(self):
        # Hooks inherit the caller's Git directory/index/worktree. Child repos
        # must not use any of that state, including when running the checker.
        self.environment = {
            key: value
            for key, value in os.environ.items()
            if not key.startswith("GIT_")
        }
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
            ["git", *args],
            cwd=self.root,
            env=self.environment,
            text=True,
            stderr=subprocess.STDOUT,
        )

    def check(self, *args):
        return subprocess.run(
            [sys.executable, "tools/check_fragments.py", "--base", "base", *args],
            cwd=self.root,
            env=self.environment,
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

    def test_non_utf8_locale_accepts_utf8_staged_and_committed_fragments(self):
        self.environment.update(
            PYTHONUTF8="0",
            PYTHONCOERCECLOCALE="0",
            LC_ALL="C",
            PYTHONIOENCODING="utf-8",
        )
        (self.root / "changes/5.bugfix.md").write_text(
            "Исправлена ошибка кэша; воспроизводимая карта.\n", encoding="utf-8"
        )
        self.git("add", "changes/5.bugfix.md")
        staged = self.check("--staged")
        self.assertEqual(0, staged.returncode, staged.stderr)
        self.git("commit", "-m", "utf8 fragment")
        committed = self.check()
        self.assertEqual(0, committed.returncode, committed.stderr)


if __name__ == "__main__":
    unittest.main()
