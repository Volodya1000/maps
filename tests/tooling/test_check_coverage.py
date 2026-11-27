import io
import os
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path

from tools import check_coverage

PROJECTS = check_coverage.REQUIRED_PROJECTS


def report(*, line_hits: int = 1, branch: str = "100% (2/2)") -> str:
    packages = []
    for project in PROJECTS:
        packages.append(
            f'<package name="{project}"><classes><class name="C"><lines>'
            f'<line number="1" hits="{line_hits}" branch="true" condition-coverage="{branch}" />'
            "</lines></class></classes></package>"
        )
    return "<coverage><packages>" + "".join(packages) + "</packages></coverage>"


class CoverageCheckTests(unittest.TestCase):
    def write_report(self, content: str) -> Path:
        directory = Path(self.tempdir.name) / "run"
        directory.mkdir()
        path = directory / "coverage.cobertura.xml"
        path.write_text(content, encoding="utf-8")
        return directory

    def setUp(self) -> None:
        self.tempdir = tempfile.TemporaryDirectory()

    def tearDown(self) -> None:
        self.tempdir.cleanup()

    def test_passing_report_uses_aggregate_counts(self) -> None:
        output = io.StringIO()
        with redirect_stdout(output):
            result = check_coverage.main([str(self.write_report(report()))])
        self.assertEqual(result, 0)
        self.assertIn("Result: PASS", output.getvalue())

    def test_line_threshold_is_enforced(self) -> None:
        self.assertEqual(
            check_coverage.main([str(self.write_report(report(line_hits=0)))]), 1
        )

    def test_branch_threshold_is_enforced(self) -> None:
        self.assertEqual(
            check_coverage.main([str(self.write_report(report(branch="50% (1/2)")))]), 1
        )

    def test_missing_report_fails(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            self.assertEqual(check_coverage.main([directory]), 1)

    def test_malformed_report_fails(self) -> None:
        self.assertEqual(check_coverage.main([str(self.write_report("not xml"))]), 1)

    def test_multiple_reports_are_rejected_even_when_newest_passes(self) -> None:
        root = Path(self.tempdir.name)
        failed = self.write_report(report(line_hits=0)) / "coverage.cobertura.xml"
        stale = root / "old-success" / "coverage.cobertura.xml"
        stale.parent.mkdir()
        stale.write_text(report(), encoding="utf-8")
        os.utime(failed, (100, 100))
        os.utime(stale, (200, 200))
        self.assertEqual(check_coverage.main([str(root)]), 1)

    def test_equal_timestamps_do_not_resolve_ambiguous_reports(self) -> None:
        root = Path(self.tempdir.name)
        first = self.write_report(report()) / "coverage.cobertura.xml"
        second = root / "second" / "coverage.cobertura.xml"
        second.parent.mkdir()
        second.write_text(report(), encoding="utf-8")
        for path in (first, second):
            os.utime(path, (100, 100))
        with self.assertRaises(check_coverage.CoverageError):
            check_coverage.find_report(root)

    def test_current_run_does_not_reuse_newer_successful_sibling(self) -> None:
        root = Path(self.tempdir.name)
        current = self.write_report(report(line_hits=0))
        stale = root / "old-success" / "coverage.cobertura.xml"
        stale.parent.mkdir()
        stale.write_text(report(), encoding="utf-8")
        os.utime(stale, (2000000000, 2000000000))
        self.assertEqual(check_coverage.main([str(current)]), 1)
        (current / "coverage.cobertura.xml").unlink()
        self.assertEqual(check_coverage.main([str(current)]), 1)

    def write_trx_report(self, *, deployed: bool = True) -> tuple[Path, Path]:
        root = Path(self.tempdir.name)
        attachment = "host/coverage.cobertura.xml"
        canonical = (
            root / "deployment" / "In" / attachment if deployed else root / attachment
        )
        canonical.parent.mkdir(parents=True)
        canonical.write_text(report(), encoding="utf-8")
        self.write_trx(root / "current.trx", attachment.replace("/", "\\"))
        return root, canonical

    def write_trx(self, path: Path, attachment: str) -> None:
        path.write_text(
            '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010" '
            'id="eb153b67-888c-4e62-8958-a560bc8e9c85">'
            '<TestSettings><Deployment runDeploymentRoot="deployment" /></TestSettings>'
            "<ResultSummary><CollectorDataEntries>"
            '<Collector uri="datacollector://microsoft/CoverletCodeCoverage/1.0">'
            f'<UriAttachments><UriAttachment><A href="{attachment}" />'
            "</UriAttachment></UriAttachments></Collector>"
            "</CollectorDataEntries></ResultSummary></TestRun>",
            encoding="utf-8",
        )

    def test_windows_trx_attachment_accepts_identical_collector_copy(self) -> None:
        root, canonical = self.write_trx_report()
        copy = root / "a9fe2a4e-544c-4354-9f53-105342fbc3fc" / canonical.name
        copy.parent.mkdir()
        copy.write_bytes(canonical.read_bytes())
        self.assertEqual(check_coverage.find_report(root), canonical)
        self.assertEqual(check_coverage.main([str(root)]), 0)

    def test_direct_relative_trx_attachment_is_resolved(self) -> None:
        root, canonical = self.write_trx_report(deployed=False)
        self.assertEqual(check_coverage.find_report(root), canonical)

    def test_missing_trx_attachment_never_reuses_unrelated_report(self) -> None:
        root, canonical = self.write_trx_report()
        canonical.unlink()
        unrelated = root / "old" / canonical.name
        unrelated.parent.mkdir()
        unrelated.write_text(report(), encoding="utf-8")
        with self.assertRaises(check_coverage.CoverageError):
            check_coverage.find_report(root)

    def test_divergent_collector_copy_cannot_override_trx_identity(self) -> None:
        root, canonical = self.write_trx_report()
        canonical.write_text(report(line_hits=0), encoding="utf-8")
        copy = root / "a9fe2a4e-544c-4354-9f53-105342fbc3fc" / canonical.name
        copy.parent.mkdir()
        copy.write_text(report(), encoding="utf-8")
        os.utime(copy, (2000000000, 2000000000))
        with self.assertRaises(check_coverage.CoverageError):
            check_coverage.find_report(root)

    def test_identical_unrelated_copy_does_not_prove_collector_identity(self) -> None:
        root, canonical = self.write_trx_report()
        copy = root / "old-success" / canonical.name
        copy.parent.mkdir()
        copy.write_bytes(canonical.read_bytes())
        with self.assertRaises(check_coverage.CoverageError):
            check_coverage.find_report(root)

    def test_direct_attachment_does_not_allow_arbitrary_guid_alias(self) -> None:
        root, canonical = self.write_trx_report(deployed=False)
        copy = root / "a9fe2a4e-544c-4354-9f53-105342fbc3fc" / canonical.name
        copy.parent.mkdir()
        copy.write_bytes(canonical.read_bytes())
        with self.assertRaises(check_coverage.CoverageError):
            check_coverage.find_report(root)

    def test_multiple_trx_runs_are_rejected(self) -> None:
        root, _ = self.write_trx_report()
        self.write_trx(root / "old.trx", "host/coverage.cobertura.xml")
        with self.assertRaises(check_coverage.CoverageError):
            check_coverage.find_report(root)

    def test_multiple_coverlet_attachments_are_rejected(self) -> None:
        root, _ = self.write_trx_report()
        trx = root / "current.trx"
        content = trx.read_text(encoding="utf-8")
        content = content.replace(
            "</UriAttachments>",
            '<UriAttachment><A href="second/coverage.cobertura.xml" />'
            "</UriAttachment></UriAttachments>",
        )
        trx.write_text(content, encoding="utf-8")
        with self.assertRaises(check_coverage.CoverageError):
            check_coverage.find_report(root)

    def test_trx_attachment_cannot_escape_results_directory(self) -> None:
        root = Path(self.tempdir.name)
        self.write_trx(root / "current.trx", "../coverage.cobertura.xml")
        with self.assertRaises(check_coverage.CoverageError):
            check_coverage.find_report(root)

    def test_malformed_trx_is_reported_as_coverage_error(self) -> None:
        root, _ = self.write_trx_report()
        (root / "current.trx").write_text("not XML", encoding="utf-8")
        with self.assertRaises(check_coverage.CoverageError):
            check_coverage.find_report(root)

    def test_missing_project_fails(self) -> None:
        content = report().replace(
            '<package name="CableRoutePlanner.UI">', '<package name="Other">'
        )
        self.assertEqual(check_coverage.main([str(self.write_report(content))]), 1)


if __name__ == "__main__":
    unittest.main()
