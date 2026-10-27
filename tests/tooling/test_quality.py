import tempfile
import unittest
from pathlib import Path

from tools import check_quality

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]


class QualityChecks(unittest.TestCase):
    def test_current_project_includes_are_valid(self) -> None:
        self.assertEqual([], check_quality.validate_axaml(REPOSITORY_ROOT))

    def test_axaml_rejects_missing_local_include(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            ui = root / "src/CableRoutePlanner.UI"
            ui.mkdir(parents=True)
            (ui / "PlannerApplication.axaml").write_text(
                '<Application xmlns="https://github.com/avaloniaui">'
                '<Application.Styles><StyleInclude Source="avares://CableRoutePlanner.UI/Styles/Missing.axaml" />'
                "</Application.Styles></Application>",
                encoding="utf-8",
            )
            errors = check_quality.validate_axaml(root)
            self.assertTrue(
                any("include source does not exist" in error for error in errors)
            )

    def test_external_include_and_dynamic_resource_text_are_not_false_positives(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            ui = root / "src/CableRoutePlanner.UI"
            ui.mkdir(parents=True)
            (ui / "External.axaml").write_text(
                '<Styles xmlns="https://github.com/avaloniaui">'
                '<StyleInclude Source="avares://Avalonia.Themes.Fluent/Accents/Light.axaml" />'
                '<TextBlock Text="{DynamicResource AppProvidedByTheme}" />'
                "</Styles>",
                encoding="utf-8",
            )
            self.assertEqual([], check_quality.validate_axaml(root))

    def test_malformed_axaml_reports_xml_location(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            ui = root / "src/CableRoutePlanner.UI"
            ui.mkdir(parents=True)
            (ui / "Broken.axaml").write_text(
                '<Styles xmlns="https://github.com/avaloniaui">', encoding="utf-8"
            )
            errors = check_quality.validate_axaml(root)
            self.assertTrue(
                any("invalid XML" in error and ":1:" in error for error in errors)
            )


if __name__ == "__main__":
    unittest.main()
