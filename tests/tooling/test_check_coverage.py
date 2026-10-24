import io
from contextlib import redirect_stdout
from pathlib import Path
import tempfile
import unittest

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
    return '<coverage><packages>' + "".join(packages) + "</packages></coverage>"


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
        self.assertEqual(check_coverage.main([str(self.write_report(report(line_hits=0)))]), 1)

    def test_branch_threshold_is_enforced(self) -> None:
        self.assertEqual(check_coverage.main([str(self.write_report(report(branch="50% (1/2)")))]), 1)

    def test_missing_report_fails(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            self.assertEqual(check_coverage.main([directory]), 1)

    def test_malformed_report_fails(self) -> None:
        self.assertEqual(check_coverage.main([str(self.write_report("not xml"))]), 1)

    def test_missing_project_fails(self) -> None:
        content = report().replace('<package name="CableRoutePlanner.UI">', '<package name="Other">')
        self.assertEqual(check_coverage.main([str(self.write_report(content))]), 1)


if __name__ == "__main__":
    unittest.main()
